using K7.Server.Application.Common.Interfaces;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Folds medias that share a provider id onto the one that already has the most children or files.
/// Cross-album recording copies are not part of the groups passed in.
/// </summary>
public static class DuplicateExternalIdMergeHelper
{
    public static async Task<int> MergeAllAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var groups = await DuplicateMediaDiagnosticHelper.GetDuplicateExternalIdGroupsAsync(context, cancellationToken);
        if (libraryId is Guid selectedLibraryId)
        {
            var inLibrary = await context.MediaLibraryAvailabilities
                .AsNoTracking()
                .Where(a => a.LibraryId == selectedLibraryId)
                .Select(a => a.MediaId)
                .ToListAsync(cancellationToken);
            var allowed = inLibrary.ToHashSet();
            groups = groups.Where(g => g.MediaIds.Any(allowed.Contains)).ToList();
        }

        var absorbed = 0;
        foreach (var mediaIds in CollapseOverlappingGroups(groups))
        {
            try
            {
                absorbed += await MergeSetAsync(context, mediaIds, logger, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to merge duplicate external id medias {MediaIds}", mediaIds);
            }
        }

        return absorbed;
    }

    private static async Task<int> MergeSetAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> mediaIds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var medias = await context.Medias
            .AsNoTracking()
            .Where(m => mediaIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Type })
            .ToListAsync(cancellationToken);

        if (medias.Count < 2)
            return 0;

        var types = medias.Select(m => m.Type).Distinct().ToList();
        if (types.Count != 1)
        {
            logger.LogInformation(
                "Skipped duplicate external id group with mixed types {MediaIds}",
                medias.Select(m => m.Id).ToList());
            return 0;
        }

        var ids = medias.Select(m => m.Id).ToList();
        return types[0] switch
        {
            MediaType.Serie => await MergeSeriesAsync(context, ids, logger, cancellationToken),
            MediaType.Movie => await MergeMoviesAsync(context, ids, logger, cancellationToken),
            MediaType.MusicTrack => await MergeTracksAsync(context, ids, logger, cancellationToken),
            MediaType.MusicAlbum => await MergeAlbumsAsync(context, ids, logger, cancellationToken),
            MediaType.MusicArtist => await MergeArtistsAsync(context, ids, logger, cancellationToken),
            _ => LogSkipped(logger, types[0], ids)
        };
    }

    private static int LogSkipped(ILogger logger, MediaType type, IReadOnlyList<Guid> mediaIds)
    {
        logger.LogInformation(
            "Skipped duplicate external id group of type {MediaType} ({Count} medias)",
            type,
            mediaIds.Count);
        return 0;
    }

    private static async Task<int> MergeSeriesAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> ids,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var episodeCounts = await context.Medias
            .OfType<SerieEpisode>()
            .Where(e => ids.Contains(e.SerieId))
            .GroupBy(e => e.SerieId)
            .Select(g => new { SerieId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var countBySerieId = episodeCounts.ToDictionary(x => x.SerieId, x => x.Count);

        var series = await context.Medias
            .OfType<Serie>()
            .Include(s => s.ExternalIds)
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken);

        if (series.Count < 2)
            return 0;

        var ordered = series
            .OrderByDescending(s => countBySerieId.GetValueOrDefault(s.Id))
            .ThenBy(s => s.Created)
            .ThenBy(s => s.Id)
            .ToList();
        var target = ordered[0];
        var absorbed = 0;

        foreach (var source in ordered.Skip(1))
        {
            if (await MergeSerieIntoAsync(context, source, target, logger, cancellationToken))
                absorbed++;
        }

        return absorbed;
    }

    private static async Task<bool> MergeSerieIntoAsync(
        IApplicationDbContext context,
        Serie source,
        Serie target,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sourceSeasons = await context.Medias
            .OfType<SerieSeason>()
            .Include(s => s.Episodes).ThenInclude(e => e.IndexedFiles)
            .Include(s => s.Episodes).ThenInclude(e => e.RemoteIndexedFiles)
            .Where(s => s.SerieId == source.Id)
            .ToListAsync(cancellationToken);

        var targetSeasons = await context.Medias
            .OfType<SerieSeason>()
            .Include(s => s.Episodes)
            .Where(s => s.SerieId == target.Id)
            .ToListAsync(cancellationToken);

        var episodesToDelete = new List<Guid>();

        foreach (var sourceSeason in sourceSeasons)
        {
            var targetSeason = targetSeasons.FirstOrDefault(s => s.SeasonNumber == sourceSeason.SeasonNumber);
            if (targetSeason is null)
            {
                sourceSeason.SerieId = target.Id;
                sourceSeason.Serie = target;
                foreach (var episode in sourceSeason.Episodes)
                {
                    episode.SerieId = target.Id;
                    episode.Serie = target;
                }

                targetSeasons.Add(sourceSeason);
                continue;
            }

            foreach (var episode in sourceSeason.Episodes.ToList())
            {
                var targetEpisode = targetSeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == episode.EpisodeNumber);
                if (targetEpisode is null)
                {
                    episode.SeasonId = targetSeason.Id;
                    episode.Season = targetSeason;
                    episode.SerieId = target.Id;
                    episode.Serie = target;
                    sourceSeason.Episodes.Remove(episode);
                    targetSeason.Episodes.Add(episode);
                    continue;
                }

                MoveFiles(episode, targetEpisode);
                await MediaUserStateTransferHelper.TransferAsync(
                    context, episode.Id, targetEpisode.Id, logger, cancellationToken);
                episodesToDelete.Add(episode.Id);
            }

            await MediaUserStateTransferHelper.TransferAsync(
                context, sourceSeason.Id, targetSeason.Id, logger, cancellationToken);
        }

        MoveMissingExternalIds(source, target);
        await MediaUserStateTransferHelper.TransferAsync(
            context, source.Id, target.Id, logger, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var episodeId in episodesToDelete)
        {
            await SerieEpisodeOrphanCleanupHelper.TryDeleteIfOrphanAsync(
                context, episodeId, logger, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await DeleteEmptySourceSeasonsAsync(context, source.Id, cancellationToken);

        if (!await context.Medias.OfType<Serie>().AnyAsync(s => s.Id == source.Id, cancellationToken))
            return true;

        var deleted = await SerieEpisodeOrphanCleanupHelper.TryDeleteSerieIfOrphanAsync(
            context, source.Id, logger, cancellationToken: cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        if (!deleted)
        {
            logger.LogInformation(
                "Kept duplicate serie {SerieId} after moving its seasons because it still has data",
                source.Id);
        }

        return deleted;
    }

    private static async Task DeleteEmptySourceSeasonsAsync(
        IApplicationDbContext context,
        Guid sourceSerieId,
        CancellationToken cancellationToken)
    {
        var leftoverSeasonIds = await context.Medias
            .OfType<SerieSeason>()
            .Where(s => s.SerieId == sourceSerieId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        foreach (var seasonId in leftoverSeasonIds)
        {
            var hasEpisodes = await context.Medias
                .OfType<SerieEpisode>()
                .AnyAsync(e => e.SeasonId == seasonId, cancellationToken);
            if (hasEpisodes)
                continue;

            await MediaOrphanDependentCleanupHelper.ClearNonUserDependentsAsync(context, seasonId, cancellationToken);
            var season = await context.Medias.OfType<SerieSeason>().FirstOrDefaultAsync(s => s.Id == seasonId, cancellationToken);
            if (season is not null)
                context.Medias.Remove(season);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> MergeAlbumsAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> ids,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var trackCounts = await context.Medias
            .OfType<MusicTrack>()
            .Where(t => ids.Contains(t.AlbumId))
            .GroupBy(t => t.AlbumId)
            .Select(g => new { AlbumId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var countByAlbumId = trackCounts.ToDictionary(x => x.AlbumId, x => x.Count);

        var albums = await context.Medias
            .OfType<MusicAlbum>()
            .Include(a => a.ExternalIds)
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(cancellationToken);

        if (albums.Count < 2)
            return 0;

        var ordered = albums
            .OrderByDescending(a => countByAlbumId.GetValueOrDefault(a.Id))
            .ThenBy(a => a.Created)
            .ThenBy(a => a.Id)
            .ToList();
        var target = ordered[0];
        var absorbed = 0;

        foreach (var source in ordered.Skip(1))
        {
            if (await MergeAlbumIntoAsync(context, source, target, logger, cancellationToken))
                absorbed++;
        }

        return absorbed;
    }

    private static async Task<bool> MergeAlbumIntoAsync(
        IApplicationDbContext context,
        MusicAlbum source,
        MusicAlbum target,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sourceTracks = await context.Medias
            .OfType<MusicTrack>()
            .Include(t => t.IndexedFiles)
            .Include(t => t.RemoteIndexedFiles)
            .Where(t => t.AlbumId == source.Id)
            .ToListAsync(cancellationToken);

        var targetTracks = await context.Medias
            .OfType<MusicTrack>()
            .Where(t => t.AlbumId == target.Id)
            .ToListAsync(cancellationToken);

        var tracksToDelete = new List<Guid>();
        foreach (var sourceTrack in sourceTracks)
        {
            var match = FindMatchingTrack(targetTracks, sourceTrack);
            if (match is null)
            {
                sourceTrack.AlbumId = target.Id;
                sourceTrack.Album = target;
                targetTracks.Add(sourceTrack);
                continue;
            }

            MoveFiles(sourceTrack, match);
            await MediaUserStateTransferHelper.TransferAsync(
                context, sourceTrack.Id, match.Id, logger, cancellationToken);
            tracksToDelete.Add(sourceTrack.Id);
        }

        await MoveMediaCreditsAsync(context, source.Id, target.Id, cancellationToken);
        MoveMissingExternalIds(source, target);
        await MediaUserStateTransferHelper.TransferAsync(
            context, source.Id, target.Id, logger, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var trackId in tracksToDelete)
        {
            await MusicOrphanCleanupHelper.TryDeleteTrackIfOrphanAsync(
                context, trackId, logger, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        if (!await context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == source.Id, cancellationToken))
            return true;

        var deleted = await MusicOrphanCleanupHelper.TryDeleteAlbumIfOrphanAsync(
            context, source.Id, logger, cancellationToken: cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        if (!deleted)
        {
            logger.LogInformation(
                "Kept duplicate album {AlbumId} after moving its tracks because it still has data",
                source.Id);
        }

        return deleted;
    }

    private static MusicTrack? FindMatchingTrack(IReadOnlyList<MusicTrack> tracks, MusicTrack source)
    {
        if (source.TrackNumber is not int trackNumber)
            return null;

        return tracks.FirstOrDefault(t =>
            t.Id != source.Id
            && t.TrackNumber == trackNumber
            && t.DiscNumber == source.DiscNumber);
    }

    private static async Task<int> MergeArtistsAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> ids,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var albumCounts = await context.Medias
            .OfType<MusicAlbum>()
            .Where(a => a.ArtistId != null && ids.Contains(a.ArtistId.Value))
            .GroupBy(a => a.ArtistId!.Value)
            .Select(g => new { ArtistId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var albumsByArtistId = albumCounts.ToDictionary(x => x.ArtistId, x => x.Count);

        var trackCounts = await context.Medias
            .OfType<MusicTrack>()
            .Where(t => t.ArtistId != null && ids.Contains(t.ArtistId.Value))
            .GroupBy(t => t.ArtistId!.Value)
            .Select(g => new { ArtistId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var tracksByArtistId = trackCounts.ToDictionary(x => x.ArtistId, x => x.Count);

        var artists = await context.Medias
            .OfType<MusicArtist>()
            .Include(a => a.ExternalIds)
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(cancellationToken);

        if (artists.Count < 2)
            return 0;

        var ordered = artists
            .OrderByDescending(a => albumsByArtistId.GetValueOrDefault(a.Id))
            .ThenByDescending(a => tracksByArtistId.GetValueOrDefault(a.Id))
            .ThenBy(a => a.Created)
            .ThenBy(a => a.Id)
            .ToList();
        var target = ordered[0];
        var absorbed = 0;

        foreach (var source in ordered.Skip(1))
        {
            if (await MergeArtistIntoAsync(context, source, target, logger, cancellationToken))
                absorbed++;
        }

        return absorbed;
    }

    private static async Task<bool> MergeArtistIntoAsync(
        IApplicationDbContext context,
        MusicArtist source,
        MusicArtist target,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var albums = await context.Medias
            .OfType<MusicAlbum>()
            .Where(a => a.ArtistId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var album in albums)
        {
            album.ArtistId = target.Id;
            album.Artist = target;
        }

        var tracks = await context.Medias
            .OfType<MusicTrack>()
            .Where(t => t.ArtistId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var track in tracks)
        {
            track.ArtistId = target.Id;
            track.Artist = target;
        }

        await MoveArtistCreditsAsync(context, source.Id, target, cancellationToken);
        MoveMissingExternalIds(source, target);
        await MediaUserStateTransferHelper.TransferAsync(
            context, source.Id, target.Id, logger, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var deleted = await MusicOrphanCleanupHelper.TryDeleteArtistIfOrphanAsync(
            context, source.Id, logger, cancellationToken: cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        if (!deleted)
        {
            logger.LogInformation(
                "Kept duplicate artist {ArtistId} after moving its albums because it still has data",
                source.Id);
        }

        return deleted;
    }

    private static async Task MoveMediaCreditsAsync(
        IApplicationDbContext context,
        Guid sourceMediaId,
        Guid targetMediaId,
        CancellationToken cancellationToken)
    {
        var sourceCredits = await context.MusicArtistCredits
            .Where(c => c.MediaId == sourceMediaId)
            .ToListAsync(cancellationToken);
        if (sourceCredits.Count == 0)
            return;

        var targetArtistIds = await context.MusicArtistCredits
            .Where(c => c.MediaId == targetMediaId)
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

            credit.MediaId = targetMediaId;
        }
    }

    private static async Task MoveArtistCreditsAsync(
        IApplicationDbContext context,
        Guid sourceArtistId,
        MusicArtist target,
        CancellationToken cancellationToken)
    {
        var sourceCredits = await context.MusicArtistCredits
            .Where(c => c.MusicArtistId == sourceArtistId)
            .ToListAsync(cancellationToken);
        if (sourceCredits.Count == 0)
            return;

        var targetMediaIds = await context.MusicArtistCredits
            .Where(c => c.MusicArtistId == target.Id)
            .Select(c => c.MediaId)
            .ToListAsync(cancellationToken);
        var taken = targetMediaIds.ToHashSet();

        foreach (var credit in sourceCredits)
        {
            if (!taken.Add(credit.MediaId))
            {
                context.MusicArtistCredits.Remove(credit);
                continue;
            }

            credit.MusicArtistId = target.Id;
            credit.MusicArtist = target;
        }
    }

    private static async Task<int> MergeMoviesAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> ids,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var movies = await context.Medias
            .OfType<Movie>()
            .Include(m => m.ExternalIds)
            .Include(m => m.IndexedFiles)
            .Include(m => m.RemoteIndexedFiles)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(cancellationToken);

        return await MergeFileBackedMediasAsync(
            context,
            movies,
            (ctx, id, log, ct) => MovieOrphanCleanupHelper.TryDeleteIfOrphanAsync(ctx, id, log, ct),
            logger,
            cancellationToken);
    }

    private static async Task<int> MergeTracksAsync(
        IApplicationDbContext context,
        IReadOnlyList<Guid> ids,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tracks = await context.Medias
            .OfType<MusicTrack>()
            .Include(t => t.ExternalIds)
            .Include(t => t.IndexedFiles)
            .Include(t => t.RemoteIndexedFiles)
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(cancellationToken);

        var absorbed = 0;
        foreach (var albumGroup in tracks.GroupBy(t => t.AlbumId))
        {
            var albumTracks = albumGroup.ToList();
            if (albumTracks.Count < 2)
                continue;

            absorbed += await MergeFileBackedMediasAsync(
                context,
                albumTracks,
                (ctx, id, log, ct) => MusicOrphanCleanupHelper.TryDeleteTrackIfOrphanAsync(ctx, id, log, ct),
                logger,
                cancellationToken);
        }

        return absorbed;
    }

    private static async Task<int> MergeFileBackedMediasAsync<TMedia>(
        IApplicationDbContext context,
        List<TMedia> medias,
        Func<IApplicationDbContext, Guid, ILogger, CancellationToken, Task<bool>> deleteIfOrphan,
        ILogger logger,
        CancellationToken cancellationToken)
        where TMedia : BaseMedia
    {
        if (medias.Count < 2)
            return 0;

        var ordered = medias
            .OrderByDescending(m => m.IndexedFiles.Count + m.RemoteIndexedFiles.Count)
            .ThenBy(m => m.Created)
            .ThenBy(m => m.Id)
            .ToList();
        var target = ordered[0];
        var absorbed = 0;

        foreach (var source in ordered.Skip(1))
        {
            MoveFiles(source, target);
            MoveMissingExternalIds(source, target);
            await MediaUserStateTransferHelper.TransferAsync(
                context, source.Id, target.Id, logger, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            var deleted = await deleteIfOrphan(context, source.Id, logger, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            if (deleted)
                absorbed++;
            else
                logger.LogInformation(
                    "Kept duplicate media {MediaId} after moving its files because it still has data",
                    source.Id);
        }

        return absorbed;
    }

    private static void MoveFiles(BaseMedia source, BaseMedia target)
    {
        foreach (var file in source.IndexedFiles.ToList())
        {
            source.IndexedFiles.Remove(file);
            file.MediaId = target.Id;
            file.Media = target;
        }

        foreach (var file in source.RemoteIndexedFiles.ToList())
        {
            source.RemoteIndexedFiles.Remove(file);
            file.MediaId = target.Id;
            file.Media = target;
        }
    }

    private static void MoveMissingExternalIds(BaseMedia source, BaseMedia target)
    {
        foreach (var externalId in source.ExternalIds.ToList())
        {
            if (target.ExternalIds.Any(e =>
                    string.Equals(e.ProviderName, externalId.ProviderName, StringComparison.OrdinalIgnoreCase)))
                continue;

            source.ExternalIds.Remove(externalId);
            externalId.MediaId = target.Id;
            externalId.Media = target;
            target.ExternalIds.Add(externalId);
        }
    }

    private static List<List<Guid>> CollapseOverlappingGroups(
        IReadOnlyList<DuplicateMediaDiagnosticHelper.DuplicateExternalIdGroup> groups)
    {
        var parent = new Dictionary<Guid, Guid>();

        Guid Find(Guid id)
        {
            var root = id;
            while (parent[root] != root)
                root = parent[root];

            var current = id;
            while (parent[current] != root)
            {
                var next = parent[current];
                parent[current] = root;
                current = next;
            }

            return root;
        }

        void Union(Guid left, Guid right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
                parent[rightRoot] = leftRoot;
        }

        foreach (var group in groups)
        {
            var ids = group.MediaIds.Distinct().ToList();
            if (ids.Count == 0)
                continue;

            foreach (var id in ids)
                parent.TryAdd(id, id);

            for (var i = 1; i < ids.Count; i++)
                Union(ids[0], ids[i]);
        }

        return parent.Keys
            .GroupBy(Find)
            .Select(g => g.ToList())
            .Where(g => g.Count > 1)
            .ToList();
    }
}
