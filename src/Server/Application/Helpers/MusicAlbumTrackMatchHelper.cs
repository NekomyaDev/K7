using K7.Server.Application.Features.Medias.Services;
using K7.Server.Domain.Entities.Medias;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Matches a local file or metadata row to a track on a multi-disc album.
/// Never matches by title alone across discs - deluxe releases often repeat titles
/// (e.g. studio Disc 1 + live Disc 2 both have "Goodbye Stranger").
/// </summary>
public static class MusicAlbumTrackMatchHelper
{
    public static int NormalizeDisc(int? discNumber) => discNumber is null or < 1 ? 1 : discNumber.Value;

    /// <summary>
    /// Prefer disc+track, then title on the same disc when unique. No album-wide title-only match.
    /// </summary>
    public static MusicTrack? FindOnAlbum(
        IEnumerable<MusicTrack> tracks,
        string? title,
        int? trackNumber,
        int? discNumber,
        Func<MusicTrack, bool>? predicate = null)
    {
        var disc = NormalizeDisc(discNumber);
        IEnumerable<MusicTrack> list = predicate is null ? tracks : tracks.Where(predicate);

        if (trackNumber is int number)
        {
            var bySlot = list.FirstOrDefault(t =>
                t.TrackNumber == number
                && NormalizeDisc(t.DiscNumber) == disc);
            if (bySlot is not null)
                return bySlot;
        }

        if (string.IsNullOrWhiteSpace(title))
            return null;

        var byTitle = list
            .Where(t =>
                NormalizeDisc(t.DiscNumber) == disc
                && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    /// <summary>
    /// Match a metadata tracklist entry by disc+track slot only.
    /// Used when stamping MusicBrainz recording ids - title fallback would collide
    /// when the same title appears twice (studio vs live, or two same-title slots).
    /// </summary>
    public static TMeta? FindMetadataTrackBySlot<TMeta>(
        IEnumerable<TMeta> metadataTracks,
        int? discNumber,
        int? trackNumber,
        Func<TMeta, int?> getDisc,
        Func<TMeta, int?> getTrack)
        where TMeta : class
    {
        if (trackNumber is not int number)
            return null;

        var disc = NormalizeDisc(discNumber);
        return metadataTracks.FirstOrDefault(mt =>
            getTrack(mt) == number
            && NormalizeDisc(getDisc(mt)) == disc);
    }

    /// <summary>
    /// Resolve which metadata row should stamp recording/ISRC ids onto a local track.
    /// Prefer a recording id that already exists on the track when it appears on the release,
    /// unless that id is shared by multiple local tracks (likely a prior bad stamp) - then
    /// the disc+track slot wins so studio/live can be corrected.
    /// </summary>
    public static TMeta? FindMetadataTrackForRecordingStamp<TMeta>(
        IEnumerable<TMeta> metadataTracks,
        int? discNumber,
        int? trackNumber,
        string? existingRecordingId,
        bool existingRecordingIdIsShared,
        Func<TMeta, int?> getDisc,
        Func<TMeta, int?> getTrack,
        Func<TMeta, string?> getRecordingId)
        where TMeta : class
    {
        var list = metadataTracks as IList<TMeta> ?? metadataTracks.ToList();
        var bySlot = FindMetadataTrackBySlot(list, discNumber, trackNumber, getDisc, getTrack);

        TMeta? byRecording = null;
        if (!string.IsNullOrWhiteSpace(existingRecordingId))
        {
            byRecording = list.FirstOrDefault(mt =>
                string.Equals(getRecordingId(mt), existingRecordingId, StringComparison.OrdinalIgnoreCase));
        }

        if (byRecording is not null && bySlot is not null && !ReferenceEquals(byRecording, bySlot))
            return existingRecordingIdIsShared ? bySlot : byRecording;

        return bySlot ?? byRecording;
    }

    /// <summary>
    /// Match a metadata tracklist entry to a local track (same priority as FindOnAlbum).
    /// Title fallback only when exactly one title matches on the disc.
    /// </summary>
    public static TMeta? FindMetadataTrack<TMeta>(
        IEnumerable<TMeta> metadataTracks,
        int? discNumber,
        int? trackNumber,
        string? title,
        Func<TMeta, int?> getDisc,
        Func<TMeta, int?> getTrack,
        Func<TMeta, string?> getTitle)
        where TMeta : class
    {
        var bySlot = FindMetadataTrackBySlot(
            metadataTracks,
            discNumber,
            trackNumber,
            getDisc,
            getTrack);
        if (bySlot is not null)
            return bySlot;

        if (string.IsNullOrWhiteSpace(title))
            return null;

        var disc = NormalizeDisc(discNumber);
        var byTitle = metadataTracks
            .Where(mt =>
                NormalizeDisc(getDisc(mt)) == disc
                && string.Equals(getTitle(mt), title, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    /// <summary>
    /// True when rematch must create a new MusicTrack instead of reusing the former row
    /// (two files previously fused by title across discs).
    /// </summary>
    public static bool ShouldSplitFusedFormerTrack(
        bool hasOtherSlotMatch,
        bool otherFilesOnFormer,
        bool formerSlotMatchesResolved,
        bool hasTrackNumber) =>
        !hasOtherSlotMatch
        && otherFilesOnFormer
        && !formerSlotMatchesResolved
        && hasTrackNumber;

    /// <summary>
    /// Builds file-less shells for metadata tracklist slots missing on the album.
    /// </summary>
    public static List<MusicTrack> CreateMissingTrackShells(
        Guid albumId,
        IEnumerable<MusicTrack> existingTracks,
        IEnumerable<(int? DiscNumber, int? TrackNumber, string? Title, string? SortTitle)> metadataTracks)
    {
        var known = existingTracks.ToList();
        var created = new List<MusicTrack>();
        foreach (var trackMeta in metadataTracks)
        {
            if (trackMeta.TrackNumber is null && string.IsNullOrWhiteSpace(trackMeta.Title))
                continue;

            var existing = FindOnAlbum(
                known.Concat(created),
                trackMeta.Title,
                trackMeta.TrackNumber,
                trackMeta.DiscNumber);
            if (existing is not null)
                continue;

            var title = trackMeta.Title ?? $"Track {trackMeta.TrackNumber}";
            created.Add(new MusicTrack
            {
                AlbumId = albumId,
                Title = title,
                SortTitle = trackMeta.SortTitle ?? MediaSortTitleHelper.Compute(title),
                TrackNumber = trackMeta.TrackNumber,
                DiscNumber = trackMeta.DiscNumber,
            });
        }

        return created;
    }
}
