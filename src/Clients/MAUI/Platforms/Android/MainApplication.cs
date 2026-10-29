using Android.App;
using Android.Runtime;
using K7.Clients.Shared.Interfaces;

namespace K7.Clients.MAUI;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[K7-CRASH] AndroidEnvironment.UnhandledExceptionRaiser: {args.Exception}");
            TryReportError(args.Exception, "AndroidEnvironment.UnhandledExceptionRaiser");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[K7-CRASH] AppDomain.UnhandledException: {args.ExceptionObject}");
            if (args.ExceptionObject is Exception ex)
            {
                TryReportError(ex, "AppDomain.UnhandledException");
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[K7-CRASH] TaskScheduler.UnobservedTaskException: {args.Exception}");
            // Report the faulting exception, not the AggregateException wrapper: the wrapper has
            // no stack trace, so the server log only showed "message | null | null".
            var faulting = args.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? (Exception)args.Exception;
            // Lost connectivity surfaces here as a burst of faulted background tasks (OpenIddict
            // discovery, token refresh, hub). They retry on their own and are already logged
            // above; the server error list is for defects.
            if (!IsTransientNetworkFailure(faulting))
                TryReportError(faulting, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }

    private static bool IsTransientNetworkFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is HttpRequestException or System.Net.Sockets.SocketException or System.OperationCanceledException)
                return true;

            // IdentityModel wraps a failed OpenID discovery request (IDX20803) and OpenIddict a
            // failed HTTP exchange (ProtocolException): both retry on the next call.
            if (current.Message.StartsWith("IDX20803", StringComparison.Ordinal)
                || current.GetType().FullName is "OpenIddict.Abstractions.OpenIddictExceptions+ProtocolException")
                return true;
        }

        return false;
    }

    private static void TryReportError(Exception exception, string context)
    {
        try
        {
            var reporter = IPlatformApplication.Current?.Services.GetService<IClientErrorReporter>();
            reporter?.ReportError(exception, context);
        }
        catch
        {
            // Best-effort
        }
    }

    protected override MauiApp CreateMauiApp()
    {
        MauiStartupTrace.Mark("MainApplication.CreateMauiApp");
        MauiStartupTrace.MarkEnvironment();
        MauiStartupTrace.StartMainThreadWatchdog();
        return MauiProgram.CreateMauiApp();
    }
}
