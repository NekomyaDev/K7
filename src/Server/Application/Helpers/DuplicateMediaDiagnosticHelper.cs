using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.Medias.Services;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Read-only detection of probable duplicate medias.
///
/// Media creation is a find-or-create over a fuzzy identity (provider external id lookup,
/// then title/artist/year or folder consensus). A database unicity constraint is not possible:
/// provider ids change over time and two distinct medias can legitimately share title + year.
/// The per-identity lock (IMediaIdentityLock) strongly reduces the duplicate probability but
/// cannot eliminate it, because two different identity keys can designate the same media.
/// Detection is therefore the safety net: it flags suspicious medias without rejecting them.
/// Merging is an explicit diagnostic action, not part of detection.
///
/// Two heuristics, both translated to SQL (Postgres and Sqlite) then grouped in process:
/// - DuplicateExternalId: two medias share the same (ProviderName, Value) external id. This is
///   the most reliable signal. A local media and its federated copy can legitimately carry the
///   same provider id, so only medias with the same PeerServerId are compared. The same
///   recording on different albums (isrc or musicbrainz) is intentional and is not flagged.
///   Two copies on the same album still are.
/// - SuspectedDuplicateMedia: identity depends on the type, always inside one library.
///   Movies and series share a normalized title (trim + case-insensitive) and release year.
///   Albums that have a file also share the artist name, so two Greatest Hits from different artists
///   stay apart. A file-less album with no tracks left is still listed as an album. Each of its
///   titles that already exists on the unique playable album is listed on its own, on both sides.
///   Music artists and persons share a normalized name only when they are not already distinct
///   provider identities. A shared provider id stays on DuplicateExternalId only. Two namesakes
///   that each have their own provider id stay out (homonyms). A copy with no provider id joins
///   one identified namesake, or other copies that also have none. Release year is ignored, and
///   a copy with no files of its own still joins that cluster (typical of a K7-import shell).
///   Episodes and tracks stay out so generic titles cannot form huge groups, except a file-less
///   album track whose title is already on the playable album: that pair is listed track by track.
///   Candidates are a slim projection. Trim, case-fold and year run in process so Postgres never groups on
///   lower(btrim(title)) / date_part.
/// </summary>
public static class DuplicateMediaDiagnosticHelper
{
    /// <summary>
    /// DateOnly legal range. Postgres accepts wider dates (including +/-infinity); extracting
    /// their year via date_part(...)::int throws 22003 (integer out of range / dtoi4).
    /// Filtering here keeps infinity dates out of the in-memory year extraction.
    /// </summary>
    /// <summary>
    /// DateOnly legal range. Postgres accepts wider dates (including +/-infinity); extracting
    /// their year via date_part(...)::int throws 22003 (integer out of range / dtoi4).
    /// Filtering here keeps infinity dates out of the in-memory year extraction.
    /// </summary>
    private static readonly DateOnly MinReleaseDate = new(1, 1, 1);
    private static readonly DateOnly MaxReleaseDate = new(9999, 12, 31);

    private static readonly MediaType[] SuspectedDuplicateMediaTypes =
    [
        MediaType.Movie,
        MediaType.Serie
    ];

    public sealed record NamedDuplicateGroup(IReadOnlyList<Guid> Ids, IReadOnlyList<Guid> LibraryIds);

    public sealed record DuplicatePersonExternalIdGroup(
        string ProviderName,
        string Value,
        Guid? PeerServerId,
        IReadOnlyList<Guid> PersonIds,
        IReadOnlyList<Guid> LibraryIds);

    public sealed record DuplicateExternalIdGroup(
        string ProviderName,
        string Value,
        Guid? PeerServerId,
        IReadOnlyList<Guid> MediaIds);

    public static async Task<HashSet<Guid>> GetDuplicateExternalIdMediaIdsAsync(
        IApplicationDbContext context,
        IReadOnlyCollection<Guid>? limitToMediaIds,
        CancellationToken cancellationToken = default)
    {
        var ids = await LoadDuplicateExternalIdMediaIdsAsync(context, cancellationToken);

        if (limitToMediaIds is not null)
            ids.IntersectWith(limitToMediaIds);

        return ids;
    }

    public static Task<IReadOnlyList<DuplicateExternalIdGroup>> GetDuplicateExternalIdGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default) =>
        LoadDuplicateExternalIdGroupsAsync(context, cancellationToken);

    public static async Task<HashSet<Guid>> GetSuspectedDuplicateMediaIdsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        IReadOnlyCollection<Guid>? limitToMediaIds,
        CancellationToken cancellationToken = default)
    {
        var flagged = await LoadSuspectedDuplicatePairsAsync(context, libraryId, cancellationToken);
        var ids = flagged.Select(p => p.MediaId).ToHashSet();

        if (limitToMediaIds is not null)
            ids.IntersectWith(limitToMediaIds);

        return ids;
    }

    public static async Task<Dictionary<Guid, int>> GetDuplicateExternalIdCountsByLibraryAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        var groups = await LoadDuplicateExternalIdGroupsAsync(context, cancellationToken);
        var ids = groups.SelectMany(g => g.MediaIds).ToHashSet();
        var counts = ids.Count == 0
            ? []
            : await CountByLibraryAsync(
                context,
                context.Medias.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => m.Id),
                cancellationToken);

        await AddUnlinkedDuplicateArtistCountsAsync(context, counts, groups, cancellationToken);
        await AddPersonExternalIdCountsAsync(context, counts, cancellationToken);
        return counts;
    }

    public static async Task<HashSet<Guid>> GetDuplicateExternalIdArtistIdsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken = default)
    {
        var groups = await LoadDuplicateExternalIdGroupsAsync(context, cancellationToken);
        if (groups.Count == 0)
            return [];

        var mediaIds = groups.SelectMany(g => g.MediaIds).Distinct().ToList();
        var artists = await context.Medias
            .OfType<MusicArtist>()
            .AsNoTracking()
            .Where(a => mediaIds.Contains(a.Id))
            .Select(a => new { a.Id, a.PeerServerId })
            .ToListAsync(cancellationToken);

        if (artists.Count == 0)
            return [];

        var artistIds = artists.Select(a => a.Id).ToHashSet();
        var peerByArtist = artists.ToDictionary(a => a.Id, a => a.PeerServerId);
        var artistLibraries = await LoadArtistLibrariesAsync(context, artistIds, cancellationToken);
        var mediaLibraries = await LoadAvailabilityLibrariesAsync(context, mediaIds, cancellationToken);
        var fallback = await LoadFallbackLibraryIdsAsync(context, cancellationToken);
        var flagged = new HashSet<Guid>();

        foreach (var group in groups)
        {
            var inGroup = group.MediaIds.Where(artistIds.Contains).Distinct().ToList();
            if (inGroup.Count == 0)
                continue;

            var libraries = LibrariesForCluster(
                inGroup,
                artistLibraries,
                group.MediaIds,
                mediaLibraries,
                group.PeerServerId,
                fallback);

            if (libraryId.HasValue && !libraries.Contains(libraryId.Value))
                continue;

            foreach (var id in inGroup)
            {
                if (peerByArtist.GetValueOrDefault(id) == group.PeerServerId)
                    flagged.Add(id);
            }
        }

        return flagged;
    }

    public static Task<IReadOnlyList<DuplicatePersonExternalIdGroup>> GetDuplicatePersonExternalIdGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default) =>
        LoadDuplicatePersonExternalIdGroupsAsync(context, cancellationToken);

    public static async Task<HashSet<Guid>> GetDuplicateExternalIdPersonIdsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken = default)
    {
        var groups = await LoadDuplicatePersonExternalIdGroupsAsync(context, cancellationToken);
        return IdsInLibrary(groups.Select(g => (g.PersonIds, g.LibraryIds)), libraryId);
    }

    public static async Task<HashSet<Guid>> GetSuspectedDuplicatePersonIdsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken = default)
    {
        var groups = await LoadNamedPersonGroupsAsync(context, cancellationToken);
        return IdsInLibrary(groups.Select(g => (g.Ids, g.LibraryIds)), libraryId);
    }

    public static Task<IReadOnlyList<NamedDuplicateGroup>> GetNamedArtistDuplicateGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default) =>
        LoadNamedArtistGroupsAsync(context, cancellationToken);

    public static Task<IReadOnlyList<NamedDuplicateGroup>> GetNamedPersonDuplicateGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default) =>
        LoadNamedPersonGroupsAsync(context, cancellationToken);

    private static async Task<HashSet<Guid>> LoadDuplicateExternalIdMediaIdsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var groups = await LoadDuplicateExternalIdGroupsAsync(context, cancellationToken);
        return groups.SelectMany(g => g.MediaIds).ToHashSet();
    }

    private static async Task<IReadOnlyList<DuplicateExternalIdGroup>> LoadDuplicateExternalIdGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        // Slim projection + in-process grouping: a correlated EXISTS self-scan of ExternalIds
        // does not stay on the (ProviderName, Value) index once PeerServerId is pulled in.
        var rows = await context.ExternalIds
            .AsNoTracking()
            .Where(e => e.MediaId != null)
            .Select(e => new
            {
                e.ProviderName,
                e.Value,
                MediaId = e.MediaId!.Value,
                e.Media!.PeerServerId
            })
            .ToListAsync(cancellationToken);

        var grouped = rows
            .GroupBy(r => (r.ProviderName, r.Value, r.PeerServerId))
            .Select(g => (g.Key, MediaIds: g.Select(x => x.MediaId).Distinct().ToList()))
            .Where(g => g.MediaIds.Count > 1)
            .ToList();

        if (grouped.Count == 0)
            return [];

        var candidateIds = grouped.SelectMany(g => g.MediaIds).Distinct().ToList();
        var albumByTrack = await context.Medias
            .OfType<MusicTrack>()
            .AsNoTracking()
            .Where(t => candidateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.AlbumId })
            .ToDictionaryAsync(t => t.Id, t => t.AlbumId, cancellationToken);

        var groups = new List<DuplicateExternalIdGroup>();
        foreach (var (key, mediaIds) in grouped)
        {
            var kept = ExcludeWantedCrossAlbumRecordings(key.ProviderName, mediaIds, albumByTrack);
            if (kept.Count <= 1)
                continue;

            groups.Add(new DuplicateExternalIdGroup(key.ProviderName, key.Value, key.PeerServerId, kept));
        }

        return groups;
    }

    /// <summary>
    /// A recording shared by tracks on different albums is an intentional copy.
    /// Copies that share one album stay flagged. Artists that use musicbrainz stay flagged.
    /// </summary>
    private static List<Guid> ExcludeWantedCrossAlbumRecordings(
        string providerName,
        List<Guid> mediaIds,
        Dictionary<Guid, Guid> albumByTrack)
    {
        if (!IsRecordingProvider(providerName) || !mediaIds.All(albumByTrack.ContainsKey))
            return mediaIds;

        var byAlbum = mediaIds.GroupBy(id => albumByTrack[id]).ToList();
        if (byAlbum.Count <= 1)
            return mediaIds;

        return byAlbum.Where(g => g.Count() > 1).SelectMany(g => g).ToList();
    }

    private static bool IsRecordingProvider(string providerName) =>
        providerName.Equals("isrc", StringComparison.OrdinalIgnoreCase)
        || providerName.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase);

    public static async Task<Dictionary<Guid, int>> GetSuspectedDuplicateCountsByLibraryAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        var flagged = await LoadSuspectedDuplicatePairsAsync(context, libraryId: null, cancellationToken);
        var persons = await LoadNamedPersonGroupsAsync(context, cancellationToken);
        var counts = flagged
            .GroupBy(p => p.LibraryId)
            .ToDictionary(g => g.Key, g => g.Count());

        AddGroupCounts(counts, persons.Select(g => (g.Ids, g.LibraryIds)));
        return counts;
    }

    private static async Task<List<SuspectedDuplicatePair>> LoadSuspectedDuplicatePairsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken)
    {
        var pairs = new List<SuspectedDuplicatePair>();
        pairs.AddRange(await LoadTitleYearPairsAsync(context, libraryId, cancellationToken));
        pairs.AddRange(await LoadAlbumPairsAsync(context, libraryId, cancellationToken));
        pairs.AddRange(await LoadImportedShellPairsAsync(context, libraryId, cancellationToken));
        pairs.AddRange(PairsFromGroups(
            await LoadNamedArtistGroupsAsync(context, cancellationToken),
            libraryId));
        return pairs.Distinct().ToList();
    }

    private static async Task<List<SuspectedDuplicatePair>> LoadTitleYearPairsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken)
    {
        // Slim join only: no SQL lower/trim/date_part, so Type / Title / ReleaseDate indexes
        // remain usable. Grouping stays in process.
        var query =
            from a in context.MediaLibraryAvailabilities.AsNoTracking()
            join library in context.Libraries.AsNoTracking() on a.LibraryId equals library.Id
            where library.PeerServerId == null
            join media in context.Medias.AsNoTracking() on a.MediaId equals media.Id
            where SuspectedDuplicateMediaTypes.Contains(media.Type)
                && media.Title != null
                && media.ReleaseDate != null
                && media.ReleaseDate >= MinReleaseDate
                && media.ReleaseDate <= MaxReleaseDate
            select new
            {
                a.LibraryId,
                MediaId = media.Id,
                media.Type,
                Title = media.Title!,
                ReleaseDate = media.ReleaseDate!.Value
            };

        if (libraryId.HasValue)
            query = query.Where(r => r.LibraryId == libraryId.Value);

        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return [];

        return rows
            .GroupBy(r => (r.LibraryId, r.Type, Title: NormalizeTitle(r.Title), Year: r.ReleaseDate.Year))
            .Where(g => g.Select(x => x.MediaId).Distinct().Count() > 1)
            .SelectMany(g => g)
            .Select(r => new SuspectedDuplicatePair(r.LibraryId, r.MediaId))
            .Distinct()
            .ToList();
    }

    private static async Task<List<SuspectedDuplicatePair>> LoadAlbumPairsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken)
    {
        var query =
            from a in context.MediaLibraryAvailabilities.AsNoTracking()
            join library in context.Libraries.AsNoTracking() on a.LibraryId equals library.Id
            where library.PeerServerId == null
            join album in context.Medias.OfType<MusicAlbum>().AsNoTracking() on a.MediaId equals album.Id
            where album.Title != null
                && album.ReleaseDate != null
                && album.ReleaseDate >= MinReleaseDate
                && album.ReleaseDate <= MaxReleaseDate
            select new
            {
                a.LibraryId,
                MediaId = album.Id,
                Title = album.Title!,
                ReleaseDate = album.ReleaseDate!.Value,
                album.ArtistId
            };

        if (libraryId.HasValue)
            query = query.Where(r => r.LibraryId == libraryId.Value);

        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return [];

        var albumIds = rows.Select(r => r.MediaId).Distinct().ToList();
        var albumsWithFiles = (await context.Medias
            .OfType<MusicAlbum>()
            .AsNoTracking()
            .Where(a => albumIds.Contains(a.Id)
                && (a.IndexedFiles.Any()
                    || a.RemoteIndexedFiles.Any()
                    || a.Tracks.Any(t => t.IndexedFiles.Any() || t.RemoteIndexedFiles.Any())))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        var playableRows = rows.Where(r => albumsWithFiles.Contains(r.MediaId)).ToList();
        if (playableRows.Count == 0)
            return [];

        var artistIds = playableRows.Where(r => r.ArtistId != null).Select(r => r.ArtistId!.Value).Distinct().ToList();
        var artistTitles = artistIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await context.Medias
                .OfType<MusicArtist>()
                .AsNoTracking()
                .Where(a => artistIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Title, cancellationToken);

        var needsCredits = playableRows
            .Where(r => ArtistKey(r.ArtistId, artistTitles, null).Length == 0)
            .Select(r => r.MediaId)
            .Distinct()
            .ToList();
        var creditsByAlbum = await LoadCreditNamesByAlbumAsync(context, needsCredits, cancellationToken);

        return playableRows
            .GroupBy(r => (
                r.LibraryId,
                Title: NormalizeTitle(r.Title),
                Year: r.ReleaseDate.Year,
                Artist: ArtistKey(r.ArtistId, artistTitles, creditsByAlbum.GetValueOrDefault(r.MediaId))))
            .Where(g => g.Select(x => x.MediaId).Distinct().Count() > 1)
            .SelectMany(g => g)
            .Select(r => new SuspectedDuplicatePair(r.LibraryId, r.MediaId))
            .Distinct()
            .ToList();
    }

    private static async Task<List<SuspectedDuplicatePair>> LoadImportedShellPairsAsync(
        IApplicationDbContext context,
        Guid? libraryId,
        CancellationToken cancellationToken)
    {
        var found = await ImportedMusicShellReattachHelper.FindPairsAsync(
            context, libraryId, limitToIds: null, cancellationToken);
        var match = await ImportedMusicShellReattachHelper.MatchTracksAsync(context, found, cancellationToken);
        var shellsWithTracks = match.Tracks.Select(t => t.ShellAlbumId).ToHashSet();
        var pairs = new List<SuspectedDuplicatePair>();
        foreach (var pair in match.StillOpen)
        {
            if (shellsWithTracks.Contains(pair.ShellAlbumId))
                continue;

            foreach (var library in pair.LibraryIds)
            {
                pairs.Add(new SuspectedDuplicatePair(library, pair.ShellAlbumId));
                pairs.Add(new SuspectedDuplicatePair(library, pair.PlayableAlbumId));
            }
        }

        var pairByShell = found.ToDictionary(p => p.ShellAlbumId);
        foreach (var track in match.Tracks)
        {
            if (!pairByShell.TryGetValue(track.ShellAlbumId, out var pair))
                continue;

            foreach (var library in pair.LibraryIds)
            {
                pairs.Add(new SuspectedDuplicatePair(library, track.ShellTrackId));
                pairs.Add(new SuspectedDuplicatePair(library, track.PlayableTrackId));
            }
        }

        return pairs;
    }

    private static async Task<Dictionary<Guid, List<string>>> LoadCreditNamesByAlbumAsync(
        IApplicationDbContext context,
        List<Guid> albumIds,
        CancellationToken cancellationToken)
    {
        if (albumIds.Count == 0)
            return [];

        var credits = await (
            from credit in context.MusicArtistCredits.AsNoTracking()
            where albumIds.Contains(credit.MediaId)
            join artist in context.Medias.OfType<MusicArtist>().AsNoTracking()
                on credit.MusicArtistId equals artist.Id
            select new { credit.MediaId, artist.Title, credit.Order }
        ).ToListAsync(cancellationToken);

        return credits
            .GroupBy(c => c.MediaId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.Order ?? int.MaxValue).ThenBy(c => c.Title).Select(c => c.Title ?? "").ToList());
    }

    private static async Task<IReadOnlyList<NamedDuplicateGroup>> LoadNamedArtistGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var artists = await context.Medias
            .OfType<MusicArtist>()
            .AsNoTracking()
            .Where(a => a.Title != null)
            .Select(a => new { a.Id, a.Title, a.PeerServerId })
            .ToListAsync(cancellationToken);

        if (artists.Count == 0)
            return [];

        var identity = await LoadArtistExternalIdentityAsync(context, cancellationToken);
        var pending = new List<(List<Guid> Ids, Guid? PeerServerId)>();

        foreach (var cluster in artists
            .Select(a => new { a.Id, a.PeerServerId, Name = NormalizeArtist(a.Title) })
            .Where(a => a.Name.Length > 0)
            .GroupBy(a => (a.PeerServerId, a.Name)))
        {
            var ids = SelectSuspectedNameIds(
                cluster.Select(a => a.Id).Distinct().ToList(),
                identity.IdsWithExternalId,
                identity.SharedSets);
            if (ids.Count < 2)
                continue;

            pending.Add((ids, cluster.Key.PeerServerId));
        }

        return await MaterializeNamedGroupsAsync(context, pending, LoadArtistLibrariesAsync, cancellationToken);
    }

    private static async Task<IReadOnlyList<NamedDuplicateGroup>> LoadNamedPersonGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var persons = await context.Persons
            .AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.PeerServerId })
            .ToListAsync(cancellationToken);

        if (persons.Count == 0)
            return [];

        var identity = await LoadPersonExternalIdentityAsync(context, cancellationToken);
        var pending = new List<(List<Guid> Ids, Guid? PeerServerId)>();

        foreach (var cluster in persons
            .Select(p => new { p.Id, p.PeerServerId, Name = NormalizeArtist(p.Name) })
            .Where(p => p.Name.Length > 0)
            .GroupBy(p => (p.PeerServerId, p.Name)))
        {
            var ids = SelectSuspectedNameIds(
                cluster.Select(p => p.Id).Distinct().ToList(),
                identity.IdsWithExternalId,
                identity.SharedSets);
            if (ids.Count < 2)
                continue;

            pending.Add((ids, cluster.Key.PeerServerId));
        }

        return await MaterializeNamedGroupsAsync(context, pending, LoadPersonLibrariesAsync, cancellationToken);
    }

    private static async Task<IReadOnlyList<NamedDuplicateGroup>> MaterializeNamedGroupsAsync(
        IApplicationDbContext context,
        List<(List<Guid> Ids, Guid? PeerServerId)> pending,
        Func<IApplicationDbContext, IReadOnlyCollection<Guid>, CancellationToken, Task<Dictionary<Guid, HashSet<Guid>>>> loadLibraries,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return [];

        var ids = pending.SelectMany(p => p.Ids).Distinct().ToList();
        var libraries = await loadLibraries(context, ids, cancellationToken);
        var fallback = await LoadFallbackLibraryIdsAsync(context, cancellationToken);
        var groups = new List<NamedDuplicateGroup>(pending.Count);

        foreach (var (memberIds, peerServerId) in pending)
        {
            var libraryIds = LibrariesForCluster(
                memberIds, libraries, memberIds, libraries, peerServerId, fallback);
            if (libraryIds.Count == 0)
                continue;

            groups.Add(new NamedDuplicateGroup(memberIds, libraryIds.ToList()));
        }

        return groups;
    }

    private sealed record ExternalIdentityIndex(HashSet<Guid> IdsWithExternalId, List<IReadOnlyList<Guid>> SharedSets);

    private static async Task<ExternalIdentityIndex> LoadArtistExternalIdentityAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from externalId in context.ExternalIds.AsNoTracking()
            join artist in context.Medias.OfType<MusicArtist>().AsNoTracking()
                on externalId.MediaId equals artist.Id
            select new
            {
                externalId.ProviderName,
                externalId.Value,
                ArtistId = artist.Id,
                artist.PeerServerId
            }
        ).ToListAsync(cancellationToken);

        return IndexExternalIdentities(rows.Select(r => (r.ProviderName, r.Value, r.ArtistId, r.PeerServerId)));
    }

    private static async Task<ExternalIdentityIndex> LoadPersonExternalIdentityAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.ExternalIds
            .AsNoTracking()
            .Where(e => e.PersonId != null)
            .Select(e => new
            {
                e.ProviderName,
                e.Value,
                PersonId = e.PersonId!.Value,
                e.Person!.PeerServerId
            })
            .ToListAsync(cancellationToken);

        return IndexExternalIdentities(rows.Select(r => (r.ProviderName, r.Value, r.PersonId, r.PeerServerId)));
    }

    private static ExternalIdentityIndex IndexExternalIdentities(
        IEnumerable<(string ProviderName, string Value, Guid Id, Guid? PeerServerId)> rows)
    {
        var list = rows.ToList();
        var withId = list.Select(r => r.Id).ToHashSet();
        var shared = list
            .GroupBy(r => (r.ProviderName, r.Value, r.PeerServerId))
            .Select(g => (IReadOnlyList<Guid>)g.Select(x => x.Id).Distinct().ToList())
            .Where(ids => ids.Count > 1)
            .ToList();

        return new ExternalIdentityIndex(withId, shared);
    }

    private static async Task<IReadOnlyList<DuplicatePersonExternalIdGroup>> LoadDuplicatePersonExternalIdGroupsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.ExternalIds
            .AsNoTracking()
            .Where(e => e.PersonId != null)
            .Select(e => new
            {
                e.ProviderName,
                e.Value,
                PersonId = e.PersonId!.Value,
                e.Person!.PeerServerId
            })
            .ToListAsync(cancellationToken);

        var grouped = rows
            .GroupBy(r => (r.ProviderName, r.Value, r.PeerServerId))
            .Select(g => (g.Key, PersonIds: g.Select(x => x.PersonId).Distinct().ToList()))
            .Where(g => g.PersonIds.Count > 1)
            .ToList();

        if (grouped.Count == 0)
            return [];

        var duplicatePersonIds = grouped.SelectMany(g => g.PersonIds).Distinct().ToList();
        var personLibraries = await LoadPersonLibrariesAsync(context, duplicatePersonIds, cancellationToken);
        var fallback = await LoadFallbackLibraryIdsAsync(context, cancellationToken);
        var groups = new List<DuplicatePersonExternalIdGroup>();

        foreach (var (key, personIds) in grouped)
        {
            var libraries = LibrariesForCluster(
                personIds, personLibraries, personIds, personLibraries, key.PeerServerId, fallback);
            if (libraries.Count == 0)
                continue;

            groups.Add(new DuplicatePersonExternalIdGroup(
                key.ProviderName, key.Value, key.PeerServerId, personIds, libraries.ToList()));
        }

        return groups;
    }

    private static async Task AddUnlinkedDuplicateArtistCountsAsync(
        IApplicationDbContext context,
        Dictionary<Guid, int> counts,
        IReadOnlyList<DuplicateExternalIdGroup> groups,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
            return;

        var mediaIds = groups.SelectMany(g => g.MediaIds).Distinct().ToList();
        var artistIds = await context.Medias
            .OfType<MusicArtist>()
            .AsNoTracking()
            .Where(a => mediaIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        if (artistIds.Count == 0)
            return;

        var artistSet = artistIds.ToHashSet();
        var linked = await context.MediaLibraryAvailabilities
            .AsNoTracking()
            .Where(a => artistSet.Contains(a.MediaId))
            .Select(a => a.MediaId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var linkedSet = linked.ToHashSet();
        var artistLibraries = await LoadArtistLibrariesAsync(context, artistSet, cancellationToken);
        var mediaLibraries = await LoadAvailabilityLibrariesAsync(context, mediaIds, cancellationToken);
        var fallback = await LoadFallbackLibraryIdsAsync(context, cancellationToken);
        var extra = new HashSet<(Guid LibraryId, Guid ArtistId)>();

        foreach (var group in groups)
        {
            var inGroup = group.MediaIds.Where(artistSet.Contains).Distinct().ToList();
            var unlinked = inGroup.Where(id => !linkedSet.Contains(id)).ToList();
            if (unlinked.Count == 0)
                continue;

            var libraries = LibrariesForCluster(
                inGroup, artistLibraries, group.MediaIds, mediaLibraries, group.PeerServerId, fallback);
            foreach (var library in libraries)
            {
                foreach (var artistId in unlinked)
                    extra.Add((library, artistId));
            }
        }

        foreach (var row in extra.GroupBy(x => x.LibraryId))
            counts[row.Key] = counts.GetValueOrDefault(row.Key) + row.Count();
    }

    private static async Task AddPersonExternalIdCountsAsync(
        IApplicationDbContext context,
        Dictionary<Guid, int> counts,
        CancellationToken cancellationToken)
    {
        var groups = await LoadDuplicatePersonExternalIdGroupsAsync(context, cancellationToken);
        AddGroupCounts(counts, groups.Select(g => (g.PersonIds, g.LibraryIds)));
    }

    private static void AddGroupCounts(
        Dictionary<Guid, int> counts,
        IEnumerable<(IReadOnlyList<Guid> Ids, IReadOnlyList<Guid> LibraryIds)> groups)
    {
        var pairs = new HashSet<(Guid LibraryId, Guid Id)>();
        foreach (var (ids, libraryIds) in groups)
        {
            foreach (var libraryId in libraryIds)
            {
                foreach (var id in ids)
                    pairs.Add((libraryId, id));
            }
        }

        foreach (var row in pairs.GroupBy(p => p.LibraryId))
            counts[row.Key] = counts.GetValueOrDefault(row.Key) + row.Count();
    }

    private static List<SuspectedDuplicatePair> PairsFromGroups(
        IReadOnlyList<NamedDuplicateGroup> groups,
        Guid? libraryId)
    {
        var pairs = new List<SuspectedDuplicatePair>();
        foreach (var group in groups)
        {
            var libraries = libraryId.HasValue
                ? group.LibraryIds.Where(id => id == libraryId.Value)
                : group.LibraryIds;

            foreach (var library in libraries)
            {
                foreach (var id in group.Ids)
                    pairs.Add(new SuspectedDuplicatePair(library, id));
            }
        }

        return pairs;
    }

    private static HashSet<Guid> IdsInLibrary(
        IEnumerable<(IReadOnlyList<Guid> Ids, IReadOnlyList<Guid> LibraryIds)> groups,
        Guid? libraryId)
    {
        var ids = new HashSet<Guid>();
        foreach (var (groupIds, libraryIds) in groups)
        {
            if (libraryId.HasValue && !libraryIds.Contains(libraryId.Value))
                continue;

            foreach (var id in groupIds)
                ids.Add(id);
        }

        return ids;
    }

    private static HashSet<Guid> LibrariesForCluster(
        IReadOnlyList<Guid> memberIds,
        IReadOnlyDictionary<Guid, HashSet<Guid>> memberLibraries,
        IReadOnlyList<Guid> relatedIds,
        IReadOnlyDictionary<Guid, HashSet<Guid>> relatedLibraries,
        Guid? peerServerId,
        IReadOnlyList<Guid> fallbackLibraryIds)
    {
        var libraries = new HashSet<Guid>();
        foreach (var id in memberIds)
        {
            if (memberLibraries.TryGetValue(id, out var member))
                libraries.UnionWith(member);
        }

        if (libraries.Count == 0)
        {
            foreach (var id in relatedIds)
            {
                if (relatedLibraries.TryGetValue(id, out var related))
                    libraries.UnionWith(related);
            }
        }

        if (libraries.Count == 0 && peerServerId is null)
            libraries.UnionWith(fallbackLibraryIds);

        return libraries;
    }

    private static async Task<Dictionary<Guid, HashSet<Guid>>> LoadArtistLibrariesAsync(
        IApplicationDbContext context,
        IReadOnlyCollection<Guid> artistIds,
        CancellationToken cancellationToken)
    {
        if (artistIds.Count == 0)
            return [];

        var ids = artistIds.Distinct().ToList();
        var map = new Dictionary<Guid, HashSet<Guid>>();

        void AddRange(IEnumerable<(Guid ArtistId, Guid LibraryId)> rows)
        {
            foreach (var (artistId, libraryId) in rows)
            {
                if (!map.TryGetValue(artistId, out var set))
                {
                    set = [];
                    map[artistId] = set;
                }

                set.Add(libraryId);
            }
        }

        var fromAvailability = await (
            from availability in context.MediaLibraryAvailabilities.AsNoTracking()
            join library in context.Libraries.AsNoTracking() on availability.LibraryId equals library.Id
            where library.PeerServerId == null && ids.Contains(availability.MediaId)
            join artist in context.Medias.OfType<MusicArtist>().AsNoTracking()
                on availability.MediaId equals artist.Id
            select new { ArtistId = artist.Id, availability.LibraryId }
        ).ToListAsync(cancellationToken);
        AddRange(fromAvailability.Select(x => (x.ArtistId, x.LibraryId)));

        var fromAlbums = await (
            from album in context.Medias.OfType<MusicAlbum>().AsNoTracking()
            where album.ArtistId != null && ids.Contains(album.ArtistId.Value)
            join availability in context.MediaLibraryAvailabilities.AsNoTracking() on album.Id equals availability.MediaId
            join library in context.Libraries.AsNoTracking() on availability.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { ArtistId = album.ArtistId!.Value, availability.LibraryId }
        ).ToListAsync(cancellationToken);
        AddRange(fromAlbums.Select(x => (x.ArtistId, x.LibraryId)));

        var fromAlbumFiles = await (
            from album in context.Medias.OfType<MusicAlbum>().AsNoTracking()
            where album.ArtistId != null && ids.Contains(album.ArtistId.Value)
            join file in context.IndexedFiles.AsNoTracking() on album.Id equals file.MediaId
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { ArtistId = album.ArtistId!.Value, file.LibraryId }
        ).ToListAsync(cancellationToken);
        AddRange(fromAlbumFiles.Select(x => (x.ArtistId, x.LibraryId)));

        var fromTracks = await (
            from track in context.Medias.OfType<MusicTrack>().AsNoTracking()
            where track.ArtistId != null && ids.Contains(track.ArtistId.Value)
            join file in context.IndexedFiles.AsNoTracking() on track.Id equals file.MediaId
            join library in context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { ArtistId = track.ArtistId!.Value, file.LibraryId }
        ).ToListAsync(cancellationToken);
        AddRange(fromTracks.Select(x => (x.ArtistId, x.LibraryId)));

        var fromCredits = await (
            from credit in context.MusicArtistCredits.AsNoTracking()
            where ids.Contains(credit.MusicArtistId)
            join availability in context.MediaLibraryAvailabilities.AsNoTracking()
                on credit.MediaId equals availability.MediaId
            join library in context.Libraries.AsNoTracking() on availability.LibraryId equals library.Id
            where library.PeerServerId == null
            select new { credit.MusicArtistId, availability.LibraryId }
        ).ToListAsync(cancellationToken);
        AddRange(fromCredits.Select(x => (x.MusicArtistId, x.LibraryId)));

        return map;
    }

    private static async Task<Dictionary<Guid, HashSet<Guid>>> LoadAvailabilityLibrariesAsync(
        IApplicationDbContext context,
        IReadOnlyCollection<Guid> mediaIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
            return [];

        var rows = await (
            from availability in context.MediaLibraryAvailabilities.AsNoTracking()
            join library in context.Libraries.AsNoTracking() on availability.LibraryId equals library.Id
            where library.PeerServerId == null && mediaIds.Contains(availability.MediaId)
            select new { availability.MediaId, availability.LibraryId }
        ).ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.MediaId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.LibraryId).ToHashSet());
    }

    private static async Task<Dictionary<Guid, HashSet<Guid>>> LoadPersonLibrariesAsync(
        IApplicationDbContext context,
        IReadOnlyCollection<Guid> personIds,
        CancellationToken cancellationToken)
    {
        if (personIds.Count == 0)
            return [];

        var ids = personIds.Distinct().ToList();
        var roles = await context.PersonRoles
            .AsNoTracking()
            .Where(r => ids.Contains(r.PersonId))
            .Select(r => new { r.PersonId, r.MediaId })
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
            return [];

        var mediaIds = roles.Select(r => r.MediaId).Distinct().ToList();
        var mediaLibraries = await LoadAvailabilityLibrariesAsync(context, mediaIds, cancellationToken);
        var artistIds = await context.Medias
            .OfType<MusicArtist>()
            .AsNoTracking()
            .Where(a => mediaIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
        var artistLibraries = await LoadArtistLibrariesAsync(context, artistIds, cancellationToken);
        var map = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var role in roles)
        {
            if (!map.TryGetValue(role.PersonId, out var set))
            {
                set = [];
                map[role.PersonId] = set;
            }

            if (mediaLibraries.TryGetValue(role.MediaId, out var libraries))
                set.UnionWith(libraries);

            if (artistLibraries.TryGetValue(role.MediaId, out var viaArtist))
                set.UnionWith(viaArtist);
        }

        return map;
    }

    private static async Task<List<Guid>> LoadFallbackLibraryIdsAsync(
        IApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var libraries = await context.Libraries
            .AsNoTracking()
            .Where(l => l.PeerServerId == null)
            .Select(l => new { l.Id, l.MediaType })
            .ToListAsync(cancellationToken);

        var music = libraries
            .Where(l => l.MediaType == LibraryMediaType.Music)
            .Select(l => l.Id)
            .ToList();

        return music.Count > 0
            ? music
            : libraries.Select(l => l.Id).ToList();
    }

    private static string ArtistKey(
        Guid? artistId,
        IReadOnlyDictionary<Guid, string?> artistTitles,
        IReadOnlyList<string>? creditNames)
    {
        if (artistId is Guid id
            && artistTitles.TryGetValue(id, out var title)
            && NormalizeArtist(title) is { Length: > 0 } primary)
        {
            return primary;
        }

        if (creditNames is null || creditNames.Count == 0)
            return "";

        return string.Join(
            "|",
            creditNames
                .Select(NormalizeArtist)
                .Where(name => name.Length > 0)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Name matches that are not already distinct provider identities.
    /// Shared provider ids stay on DuplicateExternalId. Namesakes that each have their own
    /// provider id stay out. Copies with no provider id join one identified namesake, or each other.
    /// </summary>
    private static List<Guid> SelectSuspectedNameIds(
        List<Guid> ids,
        HashSet<Guid> idsWithExternalId,
        IEnumerable<IReadOnlyList<Guid>> sharedExternalGroups)
    {
        var remaining = WithoutSharedExternalId(ids, sharedExternalGroups);
        if (remaining.Count < 2)
            return [];

        var linkedCount = 0;
        var unlinked = new List<Guid>();
        foreach (var id in remaining)
        {
            if (idsWithExternalId.Contains(id))
                linkedCount++;
            else
                unlinked.Add(id);
        }

        if (linkedCount <= 1)
            return remaining;

        return unlinked.Count >= 2 ? unlinked : [];
    }

    /// <summary>
    /// Drops members already tied to another member of the same name cluster by a provider id.
    /// Those stay on DuplicateExternalId only.
    /// </summary>
    private static List<Guid> WithoutSharedExternalId(
        List<Guid> ids,
        IEnumerable<IReadOnlyList<Guid>> externalGroups)
    {
        var idSet = ids.ToHashSet();
        var linked = new HashSet<Guid>();
        foreach (var group in externalGroups)
        {
            var overlap = group.Where(idSet.Contains).Distinct().ToList();
            if (overlap.Count < 2)
                continue;

            foreach (var id in overlap)
                linked.Add(id);
        }

        return ids.Where(id => !linked.Contains(id)).ToList();
    }

    private static string NormalizeTitle(string title) => title.Trim().ToLowerInvariant();

    private static string NormalizeArtist(string? name)
    {
        var normalized = MusicArtistNameNormalizer.NormalizeForMatch(name);
        return string.IsNullOrWhiteSpace(normalized) ? "" : normalized.ToLowerInvariant();
    }

    private static IQueryable<MediaLibraryAvailability> LocalAvailability(
        IApplicationDbContext context,
        Guid? libraryId)
    {
        var availability =
            from a in context.MediaLibraryAvailabilities.AsNoTracking()
            join library in context.Libraries.AsNoTracking() on a.LibraryId equals library.Id
            where library.PeerServerId == null
            select a;

        if (libraryId.HasValue)
            availability = availability.Where(a => a.LibraryId == libraryId.Value);

        return availability;
    }

    private static async Task<Dictionary<Guid, int>> CountByLibraryAsync(
        IApplicationDbContext context,
        IQueryable<Guid> flaggedMediaIds,
        CancellationToken cancellationToken)
    {
        // LongCount so a pathological library cannot throw Npgsql 22003 via count(*)::int.
        var counts = await LocalAvailability(context, libraryId: null)
            .Where(a => flaggedMediaIds.Contains(a.MediaId))
            .Select(a => new { a.LibraryId, a.MediaId })
            .Distinct()
            .GroupBy(x => x.LibraryId)
            .Select(g => new { LibraryId = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(
            x => x.LibraryId,
            x => x.Count > int.MaxValue ? int.MaxValue : (int)x.Count);
    }

    private readonly record struct SuspectedDuplicatePair(Guid LibraryId, Guid MediaId);
}
