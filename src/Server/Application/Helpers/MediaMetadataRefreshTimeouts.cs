namespace K7.Server.Application.Helpers;

/// <summary>
/// Wall-clock budgets for RefreshMediaMetadatas background tasks. Serie refresh walks every
/// episode (cast, stills, provider calls) and routinely exceeds the default 300s.
/// </summary>
public static class MediaMetadataRefreshTimeouts
{
    public const int DefaultSeconds = 300;
    public const int SerieSeconds = 1800;
}
