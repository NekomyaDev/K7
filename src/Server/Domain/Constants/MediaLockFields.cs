namespace K7.Server.Domain.Constants;

/// <summary>
/// Synthetic <c>LockedFields</c> names that are not editable metadata properties.
/// </summary>
public static class MediaLockFields
{
    /// <summary>
    /// When set on a <c>Serie</c>, intro/outro detection and the MissingIntroOutro diagnostic
    /// are skipped (shows with no recurring intro/outro, or user preference).
    /// </summary>
    public const string IntroOutro = "IntroOutro";
}
