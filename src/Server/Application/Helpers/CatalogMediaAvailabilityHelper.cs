using K7.Server.Application.Common.Interfaces;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.Helpers;

internal static class CatalogMediaAvailabilityHelper
{
    internal static IQueryable<BaseMedia> WhereHasPlayableFiles(IQueryable<BaseMedia> query) =>
        query.Where(x =>
            x is MusicAlbum
                ? x.IndexedFiles.Any()
                    || x.RemoteIndexedFiles.Any()
                    || ((MusicAlbum)x).Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any())
                : x is Serie
                    ? x.RemoteIndexedFiles.Any()
                        || ((Serie)x).Seasons.Any(s => s.Episodes.Any(e => e.IndexedFiles.Any() || e.RemoteIndexedFiles.Any()))
                    : x.IndexedFiles.Any() || x.RemoteIndexedFiles.Any());

    internal static bool HasPlayableFiles(BaseMedia media, IReadOnlySet<Guid>? excludedLibraryIds = null)
    {
        bool FileAllowed(IndexedFile file) =>
            excludedLibraryIds is null || !excludedLibraryIds.Contains(file.LibraryId);

        bool HasLocalFiles(IEnumerable<IndexedFile> files) =>
            files.Any(FileAllowed);

        return media switch
        {
            MusicAlbum album => album.RemoteIndexedFiles.Count > 0
                || HasLocalFiles(album.IndexedFiles)
                || album.Tracks.Any(t => t.RemoteIndexedFiles.Count > 0 || HasLocalFiles(t.IndexedFiles)),
            Serie serie => serie.RemoteIndexedFiles.Count > 0
                || serie.Seasons.Any(s => s.Episodes.Any(e =>
                    e.RemoteIndexedFiles.Count > 0 || HasLocalFiles(e.IndexedFiles))),
            SerieEpisode episode => episode.RemoteIndexedFiles.Count > 0
                || HasLocalFiles(episode.IndexedFiles)
                || episode.Serie is Serie parentSerie && HasPlayableFiles(parentSerie, excludedLibraryIds),
            MusicTrack track => track.RemoteIndexedFiles.Count > 0 || HasLocalFiles(track.IndexedFiles),
            MusicArtist artist => artist.Albums.Any(a => HasPlayableFiles(a, excludedLibraryIds))
                || artist.ArtistCredits.Any(c => IsArtistCreditPlayable(artist, c, excludedLibraryIds)),
            _ => media.RemoteIndexedFiles.Count > 0 || HasLocalFiles(media.IndexedFiles)
        };
    }

    internal static bool IsArtistCreditPlayable(
        MusicArtist artist,
        MusicArtistCredit credit,
        IReadOnlySet<Guid>? excludedLibraryIds = null)
    {
        if (credit.Media is not MusicTrack track)
            return false;

        if (HasPlayableFiles(track, excludedLibraryIds))
            return true;

        var album = artist.Albums.FirstOrDefault(a => a.Id == track.AlbumId);
        return album is not null && HasPlayableFiles(album, excludedLibraryIds);
    }

    /// <summary>
    /// Albums that can be played: a file on the album or on one of its tracks, local or remote.
    /// Library exclusions drop local files only. Remote files stay, matching <see cref="HasPlayableFiles"/>.
    /// </summary>
    internal static async Task<HashSet<Guid>> GetPlayableAlbumIdsAsync(
        IApplicationDbContext context,
        IReadOnlyCollection<Guid> albumIds,
        IReadOnlySet<Guid>? excludedLibraryIds,
        CancellationToken cancellationToken)
    {
        if (albumIds.Count == 0)
            return [];

        var ids = albumIds.Distinct().ToList();
        var albums = context.Medias.OfType<MusicAlbum>().AsNoTracking().Where(a => ids.Contains(a.Id));

        if (excludedLibraryIds is not { Count: > 0 })
        {
            return (await albums
                .Where(a =>
                    a.IndexedFiles.Any()
                    || a.RemoteIndexedFiles.Any()
                    || a.Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any()))
                .Select(a => a.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        var excluded = excludedLibraryIds.ToList();
        return (await albums
            .Where(a =>
                a.RemoteIndexedFiles.Any()
                || a.IndexedFiles.Any(f => !excluded.Contains(f.LibraryId))
                || a.Tracks.Any(t =>
                    t.RemoteIndexedFiles.Any()
                    || t.IndexedFiles.Any(f => !excluded.Contains(f.LibraryId))))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    /// <summary>
    /// Local libraries only. A K7-import shell has neither an album file nor a track file and stays out.
    /// </summary>
    internal static IQueryable<ArtistLibraryFileRow> SelectArtistLibrariesFromIndexedFiles(IApplicationDbContext context)
    {
        var fromAlbums =
            from album in context.Medias.OfType<MusicAlbum>().AsNoTracking()
            where album.ArtistId != null
            join file in context.IndexedFiles.AsNoTracking() on album.Id equals file.MediaId
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new ArtistLibraryFileRow { ArtistId = album.ArtistId!.Value, LibraryId = file.LibraryId };

        var fromTracks =
            from album in context.Medias.OfType<MusicAlbum>().AsNoTracking()
            where album.ArtistId != null
            join track in context.Medias.OfType<MusicTrack>().AsNoTracking() on album.Id equals track.AlbumId
            join file in context.IndexedFiles.AsNoTracking() on track.Id equals file.MediaId
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new ArtistLibraryFileRow { ArtistId = album.ArtistId!.Value, LibraryId = file.LibraryId };

        return fromAlbums.Union(fromTracks);
    }

    internal static IQueryable<MusicArtist> WhereArtistHasIndexedCatalog(
        this IQueryable<MusicArtist> artists,
        IApplicationDbContext context,
        Guid? libraryId = null)
    {
        var pairs = SelectArtistLibrariesFromIndexedFiles(context);
        if (libraryId is Guid id)
            pairs = pairs.Where(p => p.LibraryId == id);

        return artists.Where(a => pairs.Any(p => p.ArtistId == a.Id));
    }

    /// <summary>
    /// Drops music-album availability rows that have no file on the album or its tracks.
    /// Other media types are unchanged. Availability is normally derived from files. This
    /// also catches a stale row left on a K7-import shell.
    /// </summary>
    internal static IQueryable<MediaLibraryAvailability> WhereAlbumRowHasFiles(
        this IQueryable<MediaLibraryAvailability> rows,
        IApplicationDbContext context) =>
        rows.Where(a =>
            !context.Medias.OfType<MusicAlbum>().Any(album => album.Id == a.MediaId)
            || context.Medias.OfType<MusicAlbum>().Any(album =>
                album.Id == a.MediaId
                && (album.IndexedFiles.Any()
                    || album.RemoteIndexedFiles.Any()
                    || album.Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any()))));
}

internal sealed class ArtistLibraryFileRow
{
    public Guid ArtistId { get; set; }
    public Guid LibraryId { get; set; }
}
