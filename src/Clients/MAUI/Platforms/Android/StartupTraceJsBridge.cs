#if ANDROID
using Android.Webkit;
using Java.Interop;

namespace K7.Clients.MAUI.Platforms.Android;

/// <summary>
/// index.html -> <see cref="MauiStartupTrace"/>. JS timing lands on the same monotonic
/// clock and logcat tag as the native markers (console.log is a separate clock and tag).
/// Registered in <c>TransparentBlazorWebViewHandler.CreatePlatformView</c> so the object
/// exists before the first document load.
/// </summary>
public sealed class StartupTraceJsBridge : Java.Lang.Object
{
    public const string InterfaceName = "K7StartupTrace";

    [JavascriptInterface]
    [Export("mark")]
    public void Mark(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        MauiStartupTrace.Mark("js-" + name);

        // K7.dismissPreload runs from EmptyLayout OnAfterRender: the first Blazor frame is in
        // the DOM and the in-page preload is fading. Fade the native Lottie overlay with it.
        if (string.Equals(name, "dismiss-preload", StringComparison.Ordinal))
            AndroidStartupLottieOverlay.NotifyWebContentPainted();
    }
}
#endif
