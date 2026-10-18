using K7.Server.Application.Common.Interfaces;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Cleans duplicates that share one album: file-less shells next to a playable title,
/// and flac/mp3 (or same basename) pairs. Does not split multi-folder editions.
/// </summary>
public static class SameAlbumDuplicateTrackRepairHelper
{
    public sealed record Result(int FileLessFolded, int FormatDuplicatesFolded);

    public static async Task<Result> RepairAsync(
        IApplicationDbContext context,
        ILogger logger,
        Guid? libraryId = null,
        IReadOnlyCollection<Guid>? limitToIds = null,
        CancellationToken cancellationToken = default)
    {
        var albumIds = await ResolveAlbumIdsAsync(context, libraryId, limitToIds, cancellationToken);
        if (albumIds.Count == 0)
            return new Result(0, 0);

        var fileLessFolded = 0;
        var formatFolded = 0;

        foreach (var albumId in albumIds)
        {
            fileLessFolded += await FoldFileLessShellsAsync(context, albumId, logger, cancellationToken);
            formatFolded += await FoldFormatDuplicatesAsync(context, albumId, logger, cancellationToken);
        }

        return new Result(fileLessFolded, formatFolded);
    }

    private static async Task<List<Guid>> ResolveAlbumIdsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        IReadOnlyCollection<Guid>? limitToIds,
        CancellationToken cancellationToken)
    {
        var query = context.Medias.OfType<MusicAlbum>().AsNoTracking().Select(a => a.Id);

        if (limitToIds is not null)
        {
            var limit = limitToIds.ToHashSet();
            var fromAlbums = limit.ToList();
            var fromTracks = await context.Medias.OfType<MusicTrack>()
                .AsNoTracking()
                .Where(t => limit.Contains(t.Id))
                .Select(t => t.AlbumId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var scoped = fromAlbums.Concat(fromTracks).Distinct().ToList();
            if (scoped.Count == 0)
                return [];
            query = query.Where(id => scoped.Contains(id));
        }

        var albumIds = await query.ToListAsync(cancellationToken);
        if (libraryId is null || albumIds.Count == 0)
            return albumIds;

        var inLibrary = await (
            from track in context.Medias.OfType<MusicTrack>().AsNoTracking()
            where albumIds.Contains(track.AlbumId)
            join file in context.IndexedFiles.AsNoTracking() on track.Id equals file.MediaId
            where file.LibraryId == libraryId
            select track.AlbumId
        ).Distinct().ToListAsync(cancellationToken);

        return inLibrary;
    }

    private static async Task<int> FoldFileLessShellsAsync(
        IApplicationDbContext context,
        Guid albumId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tracks = await context.Medias
            .OfType<MusicTrack>()
            .Include(t => t.IndexedFiles)
            .Include(t => t.RemoteIndexedFiles)
            .Where(t => t.AlbumId == albumId)
            .ToListAsync(cancellationToken);

        var playable = tracks.Where(t => t.IndexedFiles.Count > 0 || t.RemoteIndexedFiles.Count > 0).ToList();
        var shells = tracks.Where(t => t.IndexedFiles.Count == 0 && t.RemoteIndexedFiles.Count == 0).ToList();
        if (playable.Count == 0 || shells.Count == 0)
            return 0;

        var folded = 0;
        var toDelete = new List<Guid>();

        foreach (var shell in shells)
        {
            // Same disc only - deluxe albums repeat titles across discs.
            var shellDisc = MusicAlbumTrackMatchHelper.NormalizeDisc(shell.DiscNumber);
            var hits = playable
                .Where(t =>
                    MusicAlbumTrackMatchHelper.NormalizeDisc(t.DiscNumber) == shellDisc
                    && ImportedMusicShellReattachHelper.TitlesMatch(shell.Title, t.Title, null, null))
                .ToList();
            if (hits.Count != 1)
                continue;

            var target = hits[0];
            await MoveExternalIdsAsync(context, shell.Id, target.Id, cancellationToken);
            await MoveCreditsAsync(context, shell.Id, target.Id, cancellationToken);
            await MediaUserStateTransferHelper.TransferAsync(
                context, shell.Id, target.Id, logger, cancellationToken);
            toDelete.Add(shell.Id);
            folded++;
        }

        if (toDelete.Count == 0)
            return 0;

        await context.SaveChangesAsync(cancellationToken);
        foreach (var trackId in toDelete)
        {
            await MusicOrphanCleanupHelper.TryDeleteTrackIfOrphanAsync(
                context, trackId, logger, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        if (folded > 0)
        {
            logger.LogInformation(
                "Folded {Count} file-less duplicate tracks on album {AlbumId}",
                folded,
                albumId);
        }

        return folded;
    }

    private static async Task<int> FoldFormatDuplicatesAsync(
        IApplicationDbContext context,
        Guid albumId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tracks = await context.Medias
            .OfType<MusicTrack>()
            .Include(t => t.IndexedFiles)
            .Include(t => t.RemoteIndexedFiles)
            .Where(t => t.AlbumId == albumId)
            .ToListAsync(cancellationToken);

        var playable = tracks
            .Where(t => t.IndexedFiles.Count > 0)
            .ToList();
        if (playable.Count < 2)
            return 0;

        var folded = 0;
        var consumed = new HashSet<Guid>();

        for (var i = 0; i < playable.Count; i++)
        {
            var left = playable[i];
            if (!consumed.Add(left.Id))
                continue;

            for (var j = i + 1; j < playable.Count; j++)
            {
                var right = playable[j];
                if (consumed.Contains(right.Id))
                    continue;

                if (!AreFormatDuplicates(left, right))
                    continue;

                var (keep, drop) = PreferKeep(left, right);
                MoveIndexedFiles(drop, keep);
                await MoveExternalIdsAsync(context, drop.Id, keep.Id, cancellationToken);
                await MoveCreditsAsync(context, drop.Id, keep.Id, cancellationToken);
                await MediaUserStateTransferHelper.TransferAsync(
                    context, drop.Id, keep.Id, logger, cancellationToken);
                consumed.Add(drop.Id);
                folded++;

                await context.SaveChangesAsync(cancellationToken);
                await MusicOrphanCleanupHelper.TryDeleteTrackIfOrphanAsync(
                    context, drop.Id, logger, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        if (folded > 0)
        {
            logger.LogInformation(
                "Folded {Count} format-duplicate tracks on album {AlbumId}",
                folded,
                albumId);
        }

        return folded;
    }

    private static bool AreFormatDuplicates(MusicTrack left, MusicTrack right)
    {
        var sameDisc = NormalizeDisc(left.DiscNumber) == NormalizeDisc(right.DiscNumber);

        // Title match only when both sides share a disc (studio vs live same title must not fold).
        if (sameDisc
            && ImportedMusicShellReattachHelper.TitlesMatch(left.Title, right.Title, null, null))
            return HasCompatibleFormats(left, right);

        if (left.TrackNumber is int leftTrack
            && right.TrackNumber == leftTrack
            && sameDisc
            && HasCompatibleFormats(left, right))
            return true;

        return sameDisc && SameBasename(left, right) && HasCompatibleFormats(left, right);
    }

    private static bool HasCompatibleFormats(MusicTrack left, MusicTrack right)
    {
        var leftExts = left.IndexedFiles.Select(NormalizedExtension).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightExts = right.IndexedFiles.Select(NormalizedExtension).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftExts.Count == 0 || rightExts.Count == 0)
            return false;

        if (leftExts.Overlaps(rightExts))
            return SameBasename(left, right);

        var leftLossless = leftExts.Any(IsLossless);
        var rightLossless = rightExts.Any(IsLossless);
        var leftLossy = leftExts.Any(IsLossy);
        var rightLossy = rightExts.Any(IsLossy);
        return (leftLossless && rightLossy) || (rightLossless && leftLossy);
    }

    private static bool SameBasename(MusicTrack left, MusicTrack right)
    {
        var leftNames = left.IndexedFiles.Select(BaseNameWithoutExtension).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightNames = right.IndexedFiles.Select(BaseNameWithoutExtension).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftNames.Overlaps(rightNames);
    }

    private static (MusicTrack Keep, MusicTrack Drop) PreferKeep(MusicTrack left, MusicTrack right)
    {
        var leftScore = ScoreTrack(left);
        var rightScore = ScoreTrack(right);
        if (leftScore != rightScore)
            return leftScore > rightScore ? (left, right) : (right, left);

        var leftSize = left.IndexedFiles.Sum(f => f.Size);
        var rightSize = right.IndexedFiles.Sum(f => f.Size);
        if (leftSize != rightSize)
            return leftSize >= rightSize ? (left, right) : (right, left);

        return left.Id.CompareTo(right.Id) <= 0 ? (left, right) : (right, left);
    }

    private static int ScoreTrack(MusicTrack track)
    {
        var exts = track.IndexedFiles.Select(NormalizedExtension).ToList();
        if (exts.Any(IsLossless))
            return 2;
        if (exts.Any(IsLossy))
            return 1;
        return 0;
    }

    private static void MoveIndexedFiles(MusicTrack source, MusicTrack target)
    {
        foreach (var file in source.IndexedFiles.ToList())
        {
            source.IndexedFiles.Remove(file);
            file.MediaId = target.Id;
            file.Media = target;
            target.IndexedFiles.Add(file);
        }

        foreach (var file in source.RemoteIndexedFiles.ToList())
        {
            source.RemoteIndexedFiles.Remove(file);
            file.MediaId = target.Id;
            file.Media = target;
            target.RemoteIndexedFiles.Add(file);
        }
    }

    private static async Task MoveExternalIdsAsync(
        IApplicationDbContext context,
        Guid fromMediaId,
        Guid toMediaId,
        CancellationToken cancellationToken)
    {
        var sourceIds = await context.ExternalIds
            .Where(e => e.MediaId == fromMediaId)
            .ToListAsync(cancellationToken);
        if (sourceIds.Count == 0)
            return;

        var targetIds = await context.ExternalIds
            .Where(e => e.MediaId == toMediaId)
            .ToListAsync(cancellationToken);

        foreach (var source in sourceIds)
        {
            var taken = targetIds.Any(t =>
                string.Equals(t.ProviderName, source.ProviderName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Value, source.Value, StringComparison.Ordinal));
            if (taken)
                context.ExternalIds.Remove(source);
            else
            {
                source.MediaId = toMediaId;
                targetIds.Add(source);
            }
        }
    }

    private static async Task MoveCreditsAsync(
        IApplicationDbContext context,
        Guid fromMediaId,
        Guid toMediaId,
        CancellationToken cancellationToken)
    {
        var sourceCredits = await context.MusicArtistCredits
            .Where(c => c.MediaId == fromMediaId)
            .ToListAsync(cancellationToken);
        if (sourceCredits.Count == 0)
            return;

        var targetArtistIds = await context.MusicArtistCredits
            .Where(c => c.MediaId == toMediaId)
            .Select(c => c.MusicArtistId)
            .ToListAsync(cancellationToken);
        var taken = targetArtistIds.ToHashSet();

        foreach (var credit in sourceCredits)
        {
            if (!taken.Add(credit.MusicArtistId))
            {
                context.MusicArtistCredits.Remove(credit);
                continue;
            }

            credit.MediaId = toMediaId;
        }
    }

    private static int NormalizeDisc(int? discNumber) => discNumber is null or < 1 ? 1 : discNumber.Value;

    private static string NormalizedExtension(IndexedFile file)
    {
        var ext = file.Extension ?? string.Empty;
        if (ext.Length == 0 && !string.IsNullOrWhiteSpace(file.Name))
        {
            var dot = file.Name.LastIndexOf('.');
            if (dot >= 0)
                ext = file.Name[dot..];
        }

        return ext.Trim().TrimStart('.').ToLowerInvariant();
    }

    private static string BaseNameWithoutExtension(IndexedFile file)
    {
        var name = file.Name ?? string.Empty;
        if (name.Length == 0 && !string.IsNullOrWhiteSpace(file.Path))
            name = Path.GetFileName(file.Path) ?? string.Empty;

        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static bool IsLossless(string ext) =>
        ext is "flac" or "wav" or "aiff" or "aif" or "alac" or "wv";

    private static bool IsLossy(string ext) =>
        ext is "mp3" or "m4a" or "aac" or "ogg" or "opus" or "wma";
}
