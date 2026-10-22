namespace K7.Clients.MAUI.Linux;

/// <summary>
/// Detects how the Linux client was packaged so start-up can skip a redundant per-user desktop
/// rewrite when Flatpak already exported the <c>k7://</c> handler to the host.
/// </summary>
public static class LinuxPackaging
{
    /// <summary>
    /// Flatpak sets <c>FLATPAK_ID</c> inside the sandbox. Browser sign-in still works: Flatpak
    /// exports the app desktop file (<c>MimeType=x-scheme-handler/k7</c>) to the host. Rewriting
    /// <c>$XDG_DATA_HOME/applications</c> inside the sandbox is wrong (paths would point at
    /// <c>/app</c> and never reach the host mime database).
    /// </summary>
    public static bool IsFlatpak() =>
        IsFlatpak(Environment.GetEnvironmentVariable("FLATPAK_ID"));

    /// <summary>Same as <see cref="IsFlatpak()"/> with an injectable id (unit tests).</summary>
    public static bool IsFlatpak(string? flatpakId) =>
        !string.IsNullOrWhiteSpace(flatpakId);

    /// <summary>
    /// Whether the process should write a per-user desktop entry for k7://.
    /// False inside Flatpak (host registration comes from the exported desktop file).
    /// </summary>
    public static bool ShouldRegisterNativeProtocol() =>
        !IsFlatpak();

    /// <summary>Same as <see cref="ShouldRegisterNativeProtocol()"/> with an injectable id.</summary>
    public static bool ShouldRegisterNativeProtocol(string? flatpakId) =>
        !IsFlatpak(flatpakId);
}
