namespace K7.Clients.MAUI.Linux;

/// <summary>
/// Layout of the libvlc 4 bundle the Linux client ships next to its binaries
/// (<c>libvlc/linux-x64</c>, produced by <c>tools/linux/bundle-libvlc.sh</c>), the counterpart
/// of <c>libvlc/win-x64</c> on Windows. No GTK or libvlc dependency so the smoke tests cover it.
/// </summary>
public static class LinuxLibVlcBundle
{
    /// <summary>Environment variable pointing at an alternative bundle directory.</summary>
    public const string EnvironmentOverride = "K7_LIBVLC_DIR";
    public const string LibVlcFileName = "libvlc.so.12";
    public const string LibVlcCoreFileName = "libvlccore.so.9";

    public static readonly string RelativeDirectory = Path.Combine("libvlc", "linux-x64");

    public sealed record Layout(
        string Directory,
        string LibVlc,
        string LibVlcCore,
        string LibrariesDirectory,
        string PluginsDirectory);

    /// <summary>
    /// The bundle named by <see cref="EnvironmentOverride"/>, else the one next to the
    /// application. Null when neither holds <c>libvlc.so.12</c> (system libvlc is used then).
    /// </summary>
    public static Layout? Locate(string baseDirectory, string? overrideDirectory)
    {
        foreach (var candidate in Candidates(baseDirectory, overrideDirectory))
        {
            var libVlc = Path.Combine(candidate, LibVlcFileName);
            if (!File.Exists(libVlc))
                continue;

            return new Layout(
                candidate,
                libVlc,
                Path.Combine(candidate, LibVlcCoreFileName),
                Path.Combine(candidate, "lib"),
                Path.Combine(candidate, "plugins"));
        }

        return null;
    }

    /// <summary>
    /// Libraries to load by full path before libvlc itself: the private copies (ffmpeg and
    /// friends, libvlc_pulse), then libvlccore. The dynamic loader matches DT_NEEDED entries
    /// against already-loaded objects by soname when those were dlopen'ed first. Callers also
    /// put the bundle dirs on <c>LD_LIBRARY_PATH</c> (Flatpak / no RPATH). The order among the
    /// private copies is unknown (no dependency graph here): callers retry the failures until
    /// a pass makes no progress.
    /// </summary>
    public static IReadOnlyList<string> EnumeratePreloadLibraries(Layout layout)
    {
        var libraries = new List<string>();
        if (Directory.Exists(layout.LibrariesDirectory))
        {
            // Skip libva*: the bundle ships an older libva that shadows the Flatpak
            // runtime and breaks WebKit/GStreamer HLS (vaMapBuffer2). System libva is fine
            // for VLC's soft decode path.
            libraries.AddRange(Directory.EnumerateFiles(layout.LibrariesDirectory)
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return name.Contains(".so", StringComparison.Ordinal)
                        && !name.StartsWith("libva", StringComparison.Ordinal);
                })
                .OrderBy(file => file, StringComparer.Ordinal));
        }

        if (File.Exists(layout.LibVlcCore))
            libraries.Add(layout.LibVlcCore);

        return libraries;
    }

    private static IEnumerable<string> Candidates(string baseDirectory, string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            yield return Path.GetFullPath(overrideDirectory);

        yield return Path.GetFullPath(Path.Combine(baseDirectory, RelativeDirectory));
    }
}
