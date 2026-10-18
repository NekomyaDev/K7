using System.Text.RegularExpressions;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Infers disc and track numbers from audio tags, filenames, and parent folders.
/// Complete embedded tags (disc + track) win over path layout.
/// Path layout (DDTT, D-TT, Disc N folder + track-only name) fills gaps when tags are incomplete.
/// A packed tag track like 201 with no disc becomes disc 2 / track 1.
/// </summary>
public static partial class MusicDiscTrackParser
{
    public sealed record Parsed(int? DiscNumber, int? TrackNumber, string? TitleRemainder);

    /// <summary>
    /// Resolve disc/track for a file. Complete tags win over path. Path fills gaps.
    /// </summary>
    public static (int? DiscNumber, int? TrackNumber) Resolve(
        int? tagDisc,
        int? tagTrack,
        string? fileName,
        string? parentDirectory)
    {
        var fromName = TryParseFileName(fileName);
        var folderDisc = TryParseDiscFromDirectory(parentDirectory);

        var disc = PositiveOrNull(tagDisc);
        var track = PositiveOrNull(tagTrack);

        if (disc is null && track is int packed && packed >= 100)
        {
            var split = SplitDdtt(packed);
            if (split is not null)
            {
                disc = split.Value.Disc;
                track = split.Value.Track;
            }
        }

        // Complete tags: trust them even if the folder still says Disc 2.
        if (disc is not null && track is not null)
            return (disc, track);

        // Incomplete tags: path layout fills what is missing (0103, 1-06, Disc 1/03 - Title).
        if (fromName?.DiscNumber is int fileDisc && fromName.TrackNumber is int fileTrack)
            return (fileDisc, fileTrack);

        if (fromName?.TrackNumber is int nameTrack && (fromName.DiscNumber ?? folderDisc) is int pathDisc)
            return (pathDisc, nameTrack);

        disc ??= fromName?.DiscNumber;
        track ??= fromName?.TrackNumber;
        disc ??= folderDisc;

        return (disc, track);
    }

    public static Parsed? TryParseFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        if (TryDiscLabelThenTrack().Match(stem) is { Success: true } discLabel)
        {
            var disc = ParsePositive(discLabel.Groups["disc"].Value);
            var track = discLabel.Groups["track"].Success
                ? ParsePositive(discLabel.Groups["track"].Value)
                : null;
            var rest = NullIfEmpty(discLabel.Groups["rest"].Value);
            if (disc is not null)
                return new Parsed(disc, track, rest);
        }

        if (TryDiscDashTrack().Match(stem) is { Success: true } discDash)
        {
            var disc = ParsePositive(discDash.Groups["disc"].Value);
            var track = ParsePositive(discDash.Groups["track"].Value);
            var rest = NullIfEmpty(discDash.Groups["rest"].Value);
            if (disc is not null && track is not null)
                return new Parsed(disc, track, rest);
        }

        if (TryDdtt().Match(stem) is { Success: true } ddtt)
        {
            var split = SplitDdtt(int.Parse(ddtt.Groups["code"].Value));
            if (split is not null)
                return new Parsed(split.Value.Disc, split.Value.Track, NullIfEmpty(ddtt.Groups["rest"].Value));
        }

        if (TryTrackOnly().Match(stem) is { Success: true } trackOnly)
        {
            var track = ParsePositive(trackOnly.Groups["track"].Value);
            if (track is not null)
                return new Parsed(null, track, NullIfEmpty(trackOnly.Groups["rest"].Value));
        }

        return null;
    }

    public static int? TryParseDiscFromDirectory(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
            return null;

        var match = TryDiscFolder().Match(directoryName.Trim());
        return match.Success ? ParsePositive(match.Groups["disc"].Value) : null;
    }

    private static (int Disc, int Track)? SplitDdtt(int value)
    {
        if (value is < 101 or > 9999)
            return null;

        var disc = value / 100;
        var track = value % 100;
        if (disc < 1 || track < 1)
            return null;

        return (disc, track);
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static int? ParsePositive(string raw) =>
        int.TryParse(raw, out var value) && value > 0 ? value : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // "Disc 2 - 03 - Title", "CD2 Track 01 Title", "Disk 1 - Title"
    [GeneratedRegex(
        @"^\s*(?:disc|disk|cd|dvd)\s*(?<disc>\d{1,2})(?:\s*[\-_.]\s*|\s+)(?:(?:track|tr|t)\s*)?(?<track>\d{1,3})?(?:\s*[\-_.]\s*|\s+)?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TryDiscLabelThenTrack();

    // "1-06 - Title", "2.01 Title", "1_06-Title" (disc then track, both required)
    [GeneratedRegex(
        @"^\s*(?<disc>\d{1,2})\s*[\-_.]\s*(?<track>\d{1,3})\s*[\-_.\s]+(?<rest>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TryDiscDashTrack();

    // "0101 - Title", "0201-The Logical Song" (exactly four digits = DDTT)
    [GeneratedRegex(
        @"^\s*(?<code>\d{4})\s*[\-_.\s]+(?<rest>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TryDdtt();

    // "01 - Title", "1. Song", "12_Title"
    [GeneratedRegex(
        @"^\s*(?<track>\d{1,3})\s*[\-_.\s]+(?<rest>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TryTrackOnly();

    // Parent folder: "Disc 2", "CD2", "Disk 02", "DVD 1"
    [GeneratedRegex(
        @"^\s*(?:disc|disk|cd|dvd)\s*(?<disc>\d{1,2})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TryDiscFolder();
}
