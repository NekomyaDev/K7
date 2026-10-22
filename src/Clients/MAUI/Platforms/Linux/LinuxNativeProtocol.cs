using System.Diagnostics;
using K7.Clients.MAUI.Linux;

namespace K7.Clients.MAUI.Platforms.Linux;

/// <summary>
/// Registers the <c>k7://</c> scheme handler for the current user: a desktop entry with
/// <c>MimeType=x-scheme-handler/k7</c> under <c>$XDG_DATA_HOME/applications</c>, the K7 icon
/// under the user hicolor theme, then <c>xdg-mime default</c>. Other desktop entries claiming the
/// scheme lose it so only one application answers the URI (the HKCU key on Windows is unique by
/// construction). Per-user, no root.
/// </summary>
internal static class LinuxNativeProtocol
{
    private const string IconRelativePath = "hicolor/512x512/apps/" + LinuxProtocolCallback.IconName + ".png";

    public static void EnsureRegistered()
    {
        if (!LinuxPackaging.ShouldRegisterNativeProtocol())
            return;

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return;

        try
        {
            var applicationsDirectory = GetApplicationsDirectory();
            Directory.CreateDirectory(applicationsDirectory);

            var icon = InstallIcon() ?? FindAppIcon(AppContext.BaseDirectory);
            var entryAssembly = Environment.GetCommandLineArgs().FirstOrDefault();
            var dotnetRoot = LinuxProtocolCallback.ResolveDotnetRoot(
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                AppContext.BaseDirectory);
            var execCommand = LinuxProtocolCallback.BuildExecCommand(processPath, entryAssembly, dotnetRoot);
            var contents = LinuxProtocolCallback.BuildDesktopEntry(execCommand, icon);

            var desktopFilePath = Path.Combine(applicationsDirectory, LinuxProtocolCallback.DesktopFileName);
            if (!File.Exists(desktopFilePath) || File.ReadAllText(desktopFilePath) != contents)
                File.WriteAllText(desktopFilePath, contents);

            RemoveForeignHandlers(applicationsDirectory, desktopFilePath);
            RewriteMimeAppsLists(applicationsDirectory);

            RunQuiet("xdg-mime", "default", LinuxProtocolCallback.DesktopFileName, LinuxProtocolCallback.MimeType);
            RunQuiet("update-desktop-database", applicationsDirectory);
            Debug.WriteLine("K7 MAUI - registered k7:// protocol via " + desktopFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("K7 MAUI - protocol registration failed: " + ex);
        }
    }

    internal static string GetDataHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return dataHome;
    }

    internal static string GetConfigHome()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return configHome;
    }

    internal static string GetApplicationsDirectory() => Path.Combine(GetDataHome(), "applications");

    /// <summary>
    /// Copies the branding icon (linked into the app output by the csproj) to the user hicolor
    /// theme so "Open with", the dock and the app chooser show the K7 mark. Returns the icon name.
    /// </summary>
    private static string? InstallIcon()
    {
        var source = Path.Combine(AppContext.BaseDirectory, IconRelativePath);
        if (!File.Exists(source))
            return null;

        try
        {
            var target = Path.Combine(GetDataHome(), "icons", IconRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                File.Copy(source, target, overwrite: true);

            RunQuiet("gtk4-update-icon-cache", "-q", "-t", "-f", Path.Combine(GetDataHome(), "icons", "hicolor"));
            return LinuxProtocolCallback.IconName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("K7 MAUI - icon install failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>The labs targets copy <c>MauiIcon</c> to <c>hicolor/scalable/apps</c> next to the exe.</summary>
    internal static string? FindAppIcon(string baseDirectory)
    {
        var iconDirectory = Path.Combine(baseDirectory, "hicolor", "scalable", "apps");
        if (!Directory.Exists(iconDirectory))
            return null;

        return Directory.EnumerateFiles(iconDirectory)
            .FirstOrDefault(path => Path.GetExtension(path) is ".svg" or ".png");
    }

    private static void RemoveForeignHandlers(string applicationsDirectory, string ownDesktopFilePath)
    {
        foreach (var path in Directory.EnumerateFiles(applicationsDirectory, "*.desktop"))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(ownDesktopFilePath), StringComparison.Ordinal))
                continue;

            try
            {
                var rewritten = LinuxProtocolCallback.RemoveSchemeHandler(File.ReadAllText(path));
                if (rewritten is null)
                    continue;

                File.WriteAllText(path, rewritten);
                Debug.WriteLine("K7 MAUI - removed k7 scheme handler from " + path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("K7 MAUI - could not update " + path + ": " + ex.Message);
            }
        }
    }

    private static void RewriteMimeAppsLists(string applicationsDirectory)
    {
        foreach (var path in new[]
        {
            Path.Combine(GetConfigHome(), "mimeapps.list"),
            Path.Combine(applicationsDirectory, "mimeapps.list")
        })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var rewritten = LinuxProtocolCallback.RewriteMimeAppsList(File.ReadAllText(path));
                if (rewritten is not null)
                    File.WriteAllText(path, rewritten);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("K7 MAUI - could not update " + path + ": " + ex.Message);
            }
        }
    }

    private static void RunQuiet(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            // xdg-utils / desktop-file-utils missing: the entry still exists for the file manager.
            Debug.WriteLine("K7 MAUI - " + fileName + " failed: " + ex.Message);
        }
    }
}
