using System.Text.RegularExpressions;
using K7.Server.Domain.Entities.Metadatas.Files;
using K7.Server.Domain.Enums;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Maps named file chapters to Intro/Outro when chromaprint finds nothing.
/// </summary>
public static partial class ChapterSegmentFallbackHelper
{
    public const double MinIntroDurationSeconds = 15;
    public const double MaxIntroDurationSeconds = 120;
    public const double MinOutroDurationSeconds = 15;
    public const double MaxOutroDurationSeconds = 450;

    public static bool TryCreateSegment(
        ChapterMarker chapter,
        double episodeDurationSeconds,
        MediaSegmentType type,
        out double startSeconds,
        out double endSeconds)
    {
        startSeconds = 0;
        endSeconds = 0;

        if (string.IsNullOrWhiteSpace(chapter.Title) || episodeDurationSeconds <= 0)
            return false;

        if (type == MediaSegmentType.Intro && !IntroTitleRegex().IsMatch(chapter.Title))
            return false;
        if (type == MediaSegmentType.Outro && !OutroTitleRegex().IsMatch(chapter.Title))
            return false;

        // Prefer openings in the first quarter and endings in the last quarter.
        var maxIntroStart = Math.Min(episodeDurationSeconds * 0.25, 600);
        var minOutroStart = Math.Max(0, episodeDurationSeconds - Math.Min(episodeDurationSeconds * 0.25, 600));
        if (type == MediaSegmentType.Intro && chapter.StartSeconds > maxIntroStart)
            return false;
        if (type == MediaSegmentType.Outro && chapter.StartSeconds < minOutroStart)
            return false;

        startSeconds = Math.Max(0, chapter.StartSeconds);
        endSeconds = chapter.EndSeconds ?? InferEndSeconds(chapter, episodeDurationSeconds, type);
        if (endSeconds <= startSeconds)
            return false;

        var duration = endSeconds - startSeconds;
        var (min, max) = type == MediaSegmentType.Intro
            ? (MinIntroDurationSeconds, MaxIntroDurationSeconds)
            : (MinOutroDurationSeconds, MaxOutroDurationSeconds);

        return duration >= min && duration <= max;
    }

    private static double InferEndSeconds(
        ChapterMarker chapter,
        double episodeDurationSeconds,
        MediaSegmentType type)
    {
        // Untitled end: assume a typical length capped by episode remaining time.
        var defaultLength = type == MediaSegmentType.Intro ? 90.0 : 60.0;
        return Math.Min(episodeDurationSeconds, chapter.StartSeconds + defaultLength);
    }

    [GeneratedRegex(
        @"\b(opening|intro|op\d*|title\s*sequence|g[eé]n[eé]rique(?!\s*(de\s*)?(fin|end|closing)))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntroTitleRegex();

    [GeneratedRegex(
        @"\b(ending|outro|ed\d*|credits?|closing|g[eé]n[eé]rique\s*(de\s*)?(fin|end|closing)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutroTitleRegex();
}
