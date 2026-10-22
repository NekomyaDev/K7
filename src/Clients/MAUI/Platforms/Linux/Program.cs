using K7.Clients.MAUI.Linux;
using K7.Clients.MAUI.Services.Authentication;
using Microsoft.Maui.Platforms.Linux.Gtk4.BlazorWebView;
using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace K7.Clients.MAUI.Platforms.Linux;

/// <summary>
/// GTK4 entry point (dotnet/maui-labs Linux backend). The browser opens <c>k7://callback/...</c>
/// through the desktop entry, which starts a second process with the URI as argument. That
/// process forwards the URI to the running instance (socket, then callback file) and exits,
/// like the WinUI redirect on Windows. A second process never boots a second GTK application:
/// the labs <c>Gtk.Application</c> would re-run activation in the primary and open another window.
/// </summary>
public sealed class Program : GtkMauiApplication
{
    /// <summary>K7 writes its own desktop entry (with the k7 scheme handler).</summary>
    protected override bool CreateDesktopEntry => false;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public static int Main(string[] args)
    {
        // Before anything reads XDG_RUNTIME_DIR (socket / lock paths) or the audio session.
        LinuxSessionEnvironment.EnsureDefaults();
        NativeAuthTrace.Install();
        LinuxCrashLog.Install();
        LogBuildMarker();

        var callback = LinuxProtocolCallback.TryGetCommandLineCallback(args);

        // A reachable socket is the strongest proof of a running instance: never take the
        // lock (and never unlink the primary's socket) when the primary answers.
        if (LinuxSingleInstance.TryForwardToPrimary(callback))
        {
            NativeAuthTrace.Write("protocol-redirect", callback?.GetLeftPart(UriPartial.Path));
            return 0;
        }

        if (!LinuxSingleInstance.TryAcquirePrimaryLock())
        {
            // Primary alive but not listening yet: leave the URI where its watcher will find it.
            if (callback is not null)
            {
                NativeAuthTrace.Write("protocol-redirect-file", callback.GetLeftPart(UriPartial.Path));
                LinuxProtocolCallbackFile.Write(callback);
            }
            else
            {
                NativeAuthTrace.Write("protocol-redirect", "no-listener");
            }

            return 0;
        }

        LinuxProtocolActivation.SetStartupCallback(callback);
        LinuxNativeProtocol.EnsureRegistered();
        UseDotDecimalSeparator();

        GtkBlazorWebView.InitializeWebKit();
        ConfigureWebKitSandbox();
        RegisterBlazorSchemeSecurity();

        var app = new Program();
        try
        {
            app.Run(LinuxProtocolCallback.FilterCommandLine(args));
        }
        finally
        {
            LinuxProtocolActivation.Detach();
        }

        return Environment.ExitCode;
    }

    /// <summary>
    /// The labs GTK handlers format CSS numbers with the current culture: with a French culture
    /// the rgba() alpha becomes "0,6", GTK sees a fifth argument, rejects the style block
    /// ("Expected ')' at end of rgba()") and semi-transparent backgrounds (video overlay chrome)
    /// never paint. Keep the user's culture for dates and resources, only force the dot as
    /// decimal separator, both for the process culture and for the one CultureBootstrap
    /// applies later from the saved K7 language.
    /// </summary>
    private static void UseDotDecimalSeparator()
    {
        try
        {
            K7.Clients.Shared.Services.CultureBootstrap.CultureAdjuster = WithDotDecimalSeparator;
            var culture = WithDotDecimalSeparator(System.Globalization.CultureInfo.CurrentCulture);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("K7 MAUI - culture adjustment failed: " + ex.Message);
        }
    }

    private static System.Globalization.CultureInfo WithDotDecimalSeparator(System.Globalization.CultureInfo culture)
    {
        if (culture.NumberFormat.NumberDecimalSeparator == ".")
            return culture;

        var adjusted = (System.Globalization.CultureInfo)culture.Clone();
        adjusted.NumberFormat.NumberDecimalSeparator = ".";
        adjusted.NumberFormat.PercentDecimalSeparator = ".";
        adjusted.NumberFormat.CurrencyDecimalSeparator = ".";
        return adjusted;
    }

    /// <summary>
    /// Blazor is served from the custom <c>app://</c> scheme. WebKit web processes read the
    /// security registrations at spawn, so the scheme must be CORS-enabled and secure on the
    /// default context before the first WebView loads, or ES module imports (ApexCharts) fail.
    /// </summary>
    /// <summary>
    /// The labs host keeps WebKit's bubblewrap sandbox whenever user namespaces work. Inside it
    /// the web process cannot reach the WSLg PulseAudio socket (<c>/mnt/wslg/PulseServer</c>),
    /// so every HTML5 media element fails with "PulseAudio: Unable to connect" (music, HLS).
    /// Under WSLg (or with <c>K7_WEBKIT_DISABLE_SANDBOX=1</c>) the sandbox is disabled. Elsewhere
    /// the audio server directories are added to the sandbox instead.
    /// </summary>
    /// <summary>
    /// First stderr line: which build runs (assembly timestamp) and whether the libvlc debug
    /// channel is on. Packaged installs (Flatpak, deb) are easy to mix up between iterations.
    /// </summary>
    private static void LogBuildMarker()
    {
        try
        {
            var location = typeof(Program).Assembly.Location;
            var stamp = string.IsNullOrEmpty(location)
                ? "unknown"
                : File.GetLastWriteTimeUtc(location).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
            var debug = Environment.GetEnvironmentVariable("K7_VLC_DEBUG") == "1";
            Console.Error.WriteLine("K7 MAUI - build " + stamp + (debug ? " (K7_VLC_DEBUG on)" : ""));
        }
        catch
        {
        }
    }

    private static void ConfigureWebKitSandbox()
    {
        try
        {
            // Inside Flatpak /mnt/wslg is not mounted: fall back to the kernel banner.
            var wslg = Directory.Exists("/mnt/wslg") || LinuxHostInfo.IsWslKernel;
            // WSLg (no usable GPU) or on request: WebKitGTK's software path instead of the
            // DMA-BUF renderer. Never on hypervisor guests in general: on a Hyper-V guest the
            // DMA-BUF renderer works and disabling it stalls Video.js HLS (endless spinner).
            // Compositing itself always stays on: MSE / HLS video does not play without it.
            if (wslg || Environment.GetEnvironmentVariable("K7_WEBKIT_SOFTWARE_RENDERING") == "1")
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER")))
                    LinuxSessionEnvironment.Export("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
                Console.Error.WriteLine("K7 MAUI - WebKit DMA-BUF renderer disabled (WSLg or K7_WEBKIT_SOFTWARE_RENDERING)");
            }

            var disable = Environment.GetEnvironmentVariable("K7_WEBKIT_DISABLE_SANDBOX") == "1" || wslg;
            if (disable)
            {
                LinuxSessionEnvironment.Export("WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS", "1");
                Console.Error.WriteLine("K7 MAUI - WebKit sandbox disabled (WSLg or K7_WEBKIT_DISABLE_SANDBOX)");
                return;
            }

            var context = WebKit.WebContext.GetDefault();
            if (context is null)
                return;

            foreach (var path in AudioServerPaths())
            {
                if (Directory.Exists(path))
                    context.AddPathToSandbox(path, false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("K7 MAUI - WebKit sandbox configuration failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> AudioServerPaths()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir))
            yield return runtimeDir;

        var pulse = Environment.GetEnvironmentVariable("PULSE_SERVER");
        if (!string.IsNullOrEmpty(pulse))
        {
            var socket = pulse.StartsWith("unix:", StringComparison.Ordinal) ? pulse[5..] : pulse;
            var directory = Path.GetDirectoryName(socket);
            if (!string.IsNullOrEmpty(directory))
                yield return directory;
        }
    }

    private static void RegisterBlazorSchemeSecurity()
    {
        try
        {
            var securityManager = WebKit.WebContext.GetDefault()?.GetSecurityManager();
            // Local would make WebKit treat app:// like file:// and the page stays blank after the splash.
            securityManager?.RegisterUriSchemeAsCorsEnabled("app");
            securityManager?.RegisterUriSchemeAsSecure("app");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("K7 MAUI - WebKit scheme registration failed: " + ex.Message);
        }
    }
}
