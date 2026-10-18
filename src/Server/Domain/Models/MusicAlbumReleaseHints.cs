namespace K7.Server.Domain.Models;

/// <summary>
/// Optional hints for picking a MusicBrainz release inside a release-group.
/// Local file layout (track count / disc count) should beat a stale preferred release id.
/// </summary>
public sealed record MusicAlbumReleaseHints
{
    public int? ExpectedTrackCount { get; init; }
    public int? ExpectedDiscCount { get; init; }
    public IReadOnlyList<string>? ExpectedTrackTitles { get; init; }
    public TimeSpan? ExpectedTotalDuration { get; init; }
    public string? PreferredReleaseId { get; init; }
}
