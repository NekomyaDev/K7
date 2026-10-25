using K7.Server.Domain.Enums;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Shared Intro/Outro completeness checks for diagnostics and incremental detection.
/// </summary>
public static class IntroOutroSegmentCompleteness
{
    public const string IntroTypeName = "Intro";
    public const string OutroTypeName = "Outro";

    public static IReadOnlyList<string> GetMissingTypeNames(IReadOnlyCollection<MediaSegmentType> presentTypes)
    {
        var missing = new List<string>(2);
        if (!presentTypes.Contains(MediaSegmentType.Intro))
            missing.Add(IntroTypeName);
        if (!presentTypes.Contains(MediaSegmentType.Outro))
            missing.Add(OutroTypeName);
        return missing;
    }

    public static bool IsIncomplete(IReadOnlyCollection<MediaSegmentType> presentTypes) =>
        GetMissingTypeNames(presentTypes).Count > 0;

    public static bool NeedsType(IReadOnlyCollection<MediaSegmentType> presentTypes, MediaSegmentType type) =>
        !presentTypes.Contains(type);
}
