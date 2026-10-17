using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.Medias.Services;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Folds a file-less album onto the unique playable album of the same artist and title.
/// "Album (imported)" matches the title without that suffix.
/// Listen history, ratings, playlists and the other user state move to the track that is already there.
/// That title is then deleted from the shell. A shell track with no playable counterpart stays.
/// </summary>
public static class ImportedMusicShellReattachHelper
{
    public const string TitleSuffix = " (imported)";

    public sealed record Result(int TracksReattached, int TracksLeft, int AlbumsRemoved);

    public sealed record ShellPair(Guid ShellAlbumId, Guid PlayableAlbumId, IReadOnlyList<Guid> LibraryIds);

    public sealed record TrackOverlap(Guid ShellTrackId, Guid PlayableTrackId, Guid ShellAlbumId, Guid PlayableAlbumId);

    public sealed record ShellTrackMatch(IReadOnlyList<ShellPair> StillOpen, IReadOnlyList<TrackOverlap> Tracks);

    public static bool TitlesMatch(string? left, string? right, string? leftArtist, string? rightArtist)
    {
        var leftCore = TrackCore(left, leftArtist);
        var rightCore = TrackCore(right, rightArtist);
        return MediaIdentityKeys.MatchesIgnoringDiacritics(leftCore, rightCore);
    }

    public static Task<Result> ReattachAsync(
        IApplicationDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default) =>
        ReattachAsync(context, logger, libraryId: null, limitToIds: null, cancellationToken);

    public static async Task<Result> ReattachAsync(
        IApplicationDbContext context,
        ILogger logger,
        Guid? libraryId,
        IReadOnlyCollection<Guid>? limitToIds,
        CancellationToken cancellationToken = default)
    {
        var pairs = await FindPairsAsync(context, libraryId, limitToIds, cancellationToken);
        var tracksReattached = 0;
        var tracksLeft = 0;
        var albumsRemoved = 0;

        foreach (var pair in pairs)
        {
            var shell = await context.Medias
                .OfType<MusicAlbum>()
                .AsNoTracking()
                .Where(a => a.Id == pair.ShellAlbumId)
                .Select(a => new ShellAlbum(a.Id, a.Title!, a.ArtistId))
                .FirstAsync(cancellationToken);
            var outcome = await ReattachAlbumAsync(context, shell, pair.PlayableAlbumId, logger, cancellationToken);
            tracksReattached += outcome.TracksReattached;
            tracksLeft += outcome.TracksLeft;
            if (outcome.AlbumRemoved)
                albumsRemoved++;
        }

        return new Result(tracksReattached, tracksLeft, albumsRemoved);
    }

    public static async Task<IReadOnlyList<ShellPair>> FindPairsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        IReadOnlyCollection<Guid>? limitToIds,
        CancellationToken cancellationToken = default)
    {
        var shells = await context.Medias
            .OfType<MusicAlbum>()
            .AsNoTracking()
            .Where(a => a.Title != null && (
                a.Title.EndsWith(TitleSuffix)
                || (!a.IndexedFiles.Any()
                    && !a.RemoteIndexedFiles.Any()
                    && a.Tracks.Any()
                    && !a.Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any()))))
            .Select(a => new ShellAlbum(a.Id, a.Title!, a.ArtistId))
            .ToListAsync(cancellationToken);

        var bases = new List<(ShellAlbum Shell, string BaseTitle)>();
        foreach (var shell in shells)
        {
            var baseTitle = shell.Title.EndsWith(TitleSuffix, StringComparison.Ordinal)
                ? shell.Title[..^TitleSuffix.Length].Trim()
                : shell.Title.Trim();
            if (baseTitle.Length > 0)
                bases.Add((shell, baseTitle));
        }

        if (bases.Count == 0)
            return [];

        var baseLower = bases.Select(b => b.BaseTitle.ToLowerInvariant()).Distinct().ToList();
        var playable = await context.Medias
            .OfType<MusicAlbum>()
            .AsNoTracking()
            .Where(a => a.Title != null
                && baseLower.Contains(a.Title.ToLower())
                && (a.IndexedFiles.Any()
                    || a.RemoteIndexedFiles.Any()
                    || a.Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any())))
            .Select(a => new { a.Id, a.Title, a.ArtistId })
            .ToListAsync(cancellationToken);

        var matched = new List<(Guid ShellId, Guid PlayableId)>();
        foreach (var (shell, baseTitle) in bases)
        {
            var candidates = playable
                .Where(a => a.Id != shell.Id
                    && string.Equals(a.Title, baseTitle, StringComparison.OrdinalIgnoreCase)
                    && (shell.ArtistId is not Guid artistId || a.ArtistId == artistId))
                .Select(a => a.Id)
                .Distinct()
                .ToList();
            if (candidates.Count == 1)
                matched.Add((shell.Id, candidates[0]));
        }

        if (limitToIds is not null)
        {
            var limit = limitToIds.ToHashSet();
            var albumIdsFromTracks = await context.Medias
                .OfType<MusicTrack>()
                .AsNoTracking()
                .Where(t => limit.Contains(t.Id))
                .Select(t => t.AlbumId)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var albumId in albumIdsFromTracks)
                limit.Add(albumId);

            matched = matched.Where(p => limit.Contains(p.ShellId) || limit.Contains(p.PlayableId)).ToList();
        }

        if (matched.Count == 0)
            return [];

        var librariesByAlbum = await LoadPlayableLibrariesAsync(
            context, matched.Select(p => p.PlayableId).Distinct().ToList(), cancellationToken);
        List<Guid>? fallback = null;
        var result = new List<ShellPair>();

        foreach (var (shellId, playableId) in matched)
        {
            if (!librariesByAlbum.TryGetValue(playableId, out var libraries) || libraries.Count == 0)
            {
                fallback ??= await context.Libraries
                    .AsNoTracking()
                    .Where(l => l.PeerServerId == null && l.MediaType == LibraryMediaType.Music)
                    .Select(l => l.Id)
                    .ToListAsync(cancellationToken);
                libraries = fallback;
            }

            if (libraryId is Guid selected && !libraries.Contains(selected))
                continue;

            result.Add(new ShellPair(shellId, playableId, libraries));
        }

        return result;
    }

    /// <summary>
    /// Keeps pairs the merge can still clear: an empty shell, or a shell that still has a title
    /// already present on the playable album. A leftover title that does not exist there is not a duplicate.
    /// </summary>
    public static async Task<IReadOnlyList<ShellPair>> WhereTitlesStillOverlapAsync(
        IApplicationDbContext context,
        IReadOnlyList<ShellPair> pairs,
        CancellationToken cancellationToken = default) =>
        (await MatchTracksAsync(context, pairs, cancellationToken)).StillOpen;

    public static async Task<ShellTrackMatch> MatchTracksAsync(
        IApplicationDbContext context,
        IReadOnlyList<ShellPair> pairs,
        CancellationToken cancellationToken = default)
    {
        if (pairs.Count == 0)
            return new ShellTrackMatch(pairs, []);

        var shellIds = pairs.Select(p => p.ShellAlbumId).Distinct().ToList();
        var playableIds = pairs.Select(p => p.PlayableAlbumId).Distinct().ToList();

        var shellTracks = await context.Medias
            .OfType<MusicTrack>()
            .AsNoTracking()
            .Where(t => shellIds.Contains(t.AlbumId))
            .Select(t => new TrackRow(t.Id, t.Title, t.Artist != null ? t.Artist.Title : null,
                t.AlbumId, t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any()))
            .ToListAsync(cancellationToken);

        var playableTracks = await PlayableTracks(context)
            .AsNoTracking()
            .Where(t => playableIds.Contains(t.AlbumId))
            .Select(t => new TrackRow(t.Id, t.Title, t.Artist != null ? t.Artist.Title : null,
                t.AlbumId, true))
            .ToListAsync(cancellationToken);

        var shellByAlbum = shellTracks.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => g.ToList());
        var playableByAlbum = playableTracks.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => g.ToList());

        var kept = new List<ShellPair>();
        var overlaps = new List<TrackOverlap>();
        foreach (var pair in pairs)
        {
            if (!shellByAlbum.TryGetValue(pair.ShellAlbumId, out var tracks) || tracks.Count == 0)
            {
                kept.Add(pair);
                continue;
            }

            playableByAlbum.TryGetValue(pair.PlayableAlbumId, out var targets);
            targets ??= [];
            var pairOverlaps = false;
            foreach (var shellTrack in tracks)
            {
                if (shellTrack.HasFiles)
                    continue;

                var hits = targets
                    .Where(t => TitlesMatch(shellTrack.Title, t.Title, shellTrack.ArtistName, t.ArtistName))
                    .Select(t => t.Id)
                    .Distinct()
                    .ToList();
                if (hits.Count != 1)
                    continue;

                overlaps.Add(new TrackOverlap(shellTrack.Id, hits[0], pair.ShellAlbumId, pair.PlayableAlbumId));
                pairOverlaps = true;
            }

            if (pairOverlaps)
                kept.Add(pair);
        }

        return new ShellTrackMatch(kept, overlaps);
    }

    private static async Task<AlbumOutcome> ReattachAlbumAsync(
        IApplicationDbContext context,
        ShellAlbum shell,
        Guid targetAlbumId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var shellTracks = await context.Medias
            .OfType<MusicTrack>()
            .AsNoTracking()
            .Where(t => t.AlbumId == shell.Id)
            .Select(t => new TrackRow(t.Id, t.Title, t.Artist != null ? t.Artist.Title : null,
                t.AlbumId, t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any()))
            .ToListAsync(cancellationToken);

        var playableTracks = await PlayableTracks(context)
            .AsNoTracking()
            .Where(t => t.AlbumId == targetAlbumId)
            .Select(t => new TrackRow(t.Id, t.Title, t.Artist != null ? t.Artist.Title : null, t.AlbumId, true))
            .ToListAsync(cancellationToken);

        var reattached = 0;
        var left = 0;
        var reattachedIds = new List<Guid>();

        foreach (var shellTrack in shellTracks)
        {
            if (shellTrack.HasFiles)
            {
                left++;
                continue;
            }

            var hits = playableTracks
                .Where(t => TitlesMatch(shellTrack.Title, t.Title, shellTrack.ArtistName, t.ArtistName))
                .Select(t => t.Id)
                .Distinct()
                .ToList();

            if (hits.Count != 1)
            {
                left++;
                continue;
            }

            await MoveExternalIdsAsync(context, shellTrack.Id, hits[0], cancellationToken);
            await MoveCreditsAsync(context, shellTrack.Id, hits[0], cancellationToken);
            await MediaUserStateTransferHelper.TransferAsync(
                context, shellTrack.Id, hits[0], logger, cancellationToken);
            reattachedIds.Add(shellTrack.Id);
            reattached++;
        }

        await MediaUserStateTransferHelper.TransferAsync(
            context, shell.Id, targetAlbumId, logger, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var shellTrackId in reattachedIds)
        {
            await MusicOrphanCleanupHelper.TryDeleteTrackIfOrphanAsync(
                context, shellTrackId, logger, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        var albumStillThere = await context.Medias.OfType<MusicAlbum>()
            .AsNoTracking()
            .AnyAsync(a => a.Id == shell.Id, cancellationToken);
        var removed = !albumStillThere;
        if (albumStillThere)
        {
            removed = await MusicOrphanCleanupHelper.TryDeleteAlbumIfOrphanAsync(
                context, shell.Id, logger, cancellationToken: cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        if (reattached > 0)
        {
            logger.LogInformation(
                "Reattached {Count} imported tracks from album {ShellAlbumId} onto {TargetAlbumId}",
                reattached,
                shell.Id,
                targetAlbumId);
        }

        return new AlbumOutcome(reattached, left, removed);
    }

    private static async Task<Dictionary<Guid, List<Guid>>> LoadPlayableLibrariesAsync(
        IApplicationDbContext context,
        List<Guid> playableIds,
        CancellationToken cancellationToken)
    {
        var albumFileRows = await (
            from file in context.IndexedFiles.AsNoTracking()
            where file.MediaId != null && playableIds.Contains(file.MediaId.Value)
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { file.MediaId, file.LibraryId }
        ).ToListAsync(cancellationToken);
        var fromAlbumFiles = albumFileRows
            .Where(row => row.MediaId is Guid)
            .Select(row => new { AlbumId = row.MediaId!.Value, row.LibraryId })
            .ToList();

        var fromTracks = await (
            from track in context.Medias.OfType<MusicTrack>().AsNoTracking()
            where playableIds.Contains(track.AlbumId)
            join file in context.IndexedFiles.AsNoTracking() on track.Id equals file.MediaId
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { AlbumId = track.AlbumId, file.LibraryId }
        ).ToListAsync(cancellationToken);

        var map = new Dictionary<Guid, List<Guid>>();
        foreach (var row in fromAlbumFiles.Concat(fromTracks))
        {
            if (!map.TryGetValue(row.AlbumId, out var libraries))
            {
                libraries = [];
                map[row.AlbumId] = libraries;
            }

            if (!libraries.Contains(row.LibraryId))
                libraries.Add(row.LibraryId);
        }

        return map;
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

    private static string TrackCore(string? title, string? artistName) =>
        MediaIdentityKeys.StripTrackEditionSuffix(
            MediaIdentityKeys.StripRedundantArtistFromTitle(
                MediaIdentityKeys.StripFeatureCredits(title ?? string.Empty),
                artistName));

    private static IQueryable<MusicTrack> PlayableTracks(IApplicationDbContext context) =>
        context.Medias.OfType<MusicTrack>().Where(t =>
            t.IndexedFiles.Any()
            || t.RemoteIndexedFiles.Any()
            || context.Medias.OfType<MusicAlbum>().Any(a =>
                a.Id == t.AlbumId && (a.IndexedFiles.Any() || a.RemoteIndexedFiles.Any())));

    private sealed record ShellAlbum(Guid Id, string Title, Guid? ArtistId);

    private sealed record TrackRow(Guid Id, string? Title, string? ArtistName, Guid AlbumId, bool HasFiles);

    private sealed record AlbumOutcome(int TracksReattached, int TracksLeft, bool AlbumRemoved);
}
