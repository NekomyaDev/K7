using System.Diagnostics;
using K7.Clients.MAUI.Constants;
using K7.Clients.MAUI.Services;
using K7.Clients.Shared.Interfaces;
using K7.Clients.Shared.Services;
using K7.Shared.Interfaces;
using Microsoft.Extensions.DependencyInjection;
#if ANDROID
using K7.Clients.MAUI.Platforms.Android;
#endif
#if WINDOWS
using K7.Clients.MAUI.Platforms.Windows;
#endif
#if LINUX
using K7.Clients.MAUI.Platforms.Linux;
#endif

namespace K7.Clients.MAUI;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        MauiStartupTrace.Mark("App-ctor-start");
        InitializeComponent();
        MauiStartupTrace.Mark("App-ctor-end");
    }

    private K7ServerManagerService ServerManager =>
        _services.GetRequiredService<K7ServerManagerService>();

    private IPlayerService PlayerService =>
        _services.GetRequiredService<IPlayerService>();

    private IAudioPlayerService AudioPlayerService =>
        _services.GetRequiredService<IAudioPlayerService>();

    private BackButtonService BackButtonService =>
        _services.GetRequiredService<BackButtonService>();

    private IK7ServerService K7ServerService =>
        _services.GetRequiredService<IK7ServerService>();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Debug.WriteLine("K7 MAUI - App.CreateWindow - start");
        MauiStartupTrace.Mark("CreateWindow-start");

        K7.Clients.Shared.Services.AppReadySignal.Reset();
        K7.Clients.Shared.Services.FirstPaintSignal.Reset();
        MauiStartupVisual.Reset();
#if ANDROID
        _startPageAssigned = false;
#else
        // Desktop/iOS: restore before first page. Android defers this until AssignStartPage
        // so CreateWindow stays cheap and DecorView splash can show (~9s OpenIddict DI
        // resolve was blocking base.OnCreate / Show on TV).
        PrepareServerConnectionAndRestore();
#endif

        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(8), () =>
        {
            MauiStartupVisual.NotifyFirstFrame();
            MauiStartupVisual.NotifyStartPageSet();
        });

#if ANDROID
        var placeholder = new ContentPage { BackgroundColor = Color.FromArgb("#0d0907") };
        var window = new Window(placeholder) { Title = "K7" };
        AndroidStartupLottieOverlay.ReadyToBuildStartPage += () => AssignStartPage(window);
        // Show() may have already signaled before CreateWindow subscribed (ordering
        // varies). Catch up so we do not wait on the 6s failsafe.
        if (AndroidStartupLottieOverlay.WasReadySignaled)
            AssignStartPage(window);
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(6), () => AssignStartPage(window));
        MauiStartupTrace.Mark("CreateWindow-end");
        Debug.WriteLine("K7 MAUI - App.CreateWindow - placeholder returned");
        return window;
#else
        ContentPage page;
        try
        {
            page = GetStartPage();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"K7 MAUI - Start page creation failed: {ex}");
            page = CreateStartupErrorPage(ex);
        }

        page.Loaded += OnStartPageLoaded;
        var window = new Window(page) { Title = "K7" };
        MauiStartupTrace.Mark("CreateWindow-end");
        Debug.WriteLine("K7 MAUI - App.CreateWindow - start page returned");
        return window;
#endif
    }

#if ANDROID
    private bool _startPageAssigned;

    private void AssignStartPage(Window window)
    {
        if (_startPageAssigned)
            return;

        _startPageAssigned = true;
        MauiStartupTrace.Mark("AssignStartPage-begin");
        // Under DecorView splash: first OpenIddict/auth DI resolve is expensive on TV.
        MauiStartupTrace.Mark("SessionRestore-start");
        PrepareServerConnectionAndRestore();
        MauiStartupTrace.Mark("SessionRestore-end");
        ContentPage page;
        try
        {
            page = GetStartPage();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"K7 MAUI - Start page creation failed: {ex}");
            page = CreateStartupErrorPage(ex);
        }

        page.Loaded += OnStartPageLoaded;
        window.Page = page;
        MauiStartupTrace.Mark("AssignStartPage");
        // Do not ResumeTicker here: BlazorWebView init pauses Skottie for first paint.
        // Resuming before WebView ready was fine; Attach/Start after Pause starved TV.
    }
#endif

    private static void OnStartPageLoaded(object? sender, EventArgs e)
    {
        if (sender is Page page)
            page.Loaded -= OnStartPageLoaded;

        MauiStartupTrace.Mark("StartPage-Loaded");
        MauiStartupVisual.NotifyFirstFrame();
        if (sender is not BlazorPage)
        {
#if ANDROID
            AndroidStartupLottieOverlay.Dismiss();
#endif
        }

        Application.Current?.Dispatcher.DispatchDelayed(
            TimeSpan.FromSeconds(2),
            MauiStartupVisual.NotifyStartPageSet);
    }

    /// <summary>
    /// Apply BackendUrl before solo restore. Restore used to start first, hit OpenIddict /
    /// HttpClient with no base address, throw inside BlazorPage setup, then wipe the URL
    /// and trap the user on native server setup until a full app reset.
    /// </summary>
    private static void PrepareServerConnectionAndRestore()
    {
        try
        {
            MauiSessionBootstrap.StartRestore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"K7 MAUI - Session restore start failed: {ex}");
        }
    }

    private ContentPage GetStartPage()
    {
        var k7ServerUrl = Preferences.Get(PreferenceKeys.K7_SERVER_URL, null);
        Debug.WriteLine($"K7 MAUI - GetStartPage - serverUrl={(!string.IsNullOrEmpty(k7ServerUrl) ? "set" : "null")}");

        if (string.IsNullOrEmpty(k7ServerUrl))
        {
            return new SetupPage(ServerManager, PlayerService, AudioPlayerService);
        }

        // Do not resolve K7ServerManagerService here - it pulls OpenIddict (~20s on TV).
        MauiSessionBootstrap.ApplyHttpBaseAddress(_services);

        try
        {
            Debug.WriteLine("K7 MAUI - GetStartPage - creating BlazorPage");
            MauiStartupTrace.Mark("ResolveStartServices-start");
            var player = PlayerService;
            var audio = AudioPlayerService;
            var back = BackButtonService;
            var server = K7ServerService;
            MauiStartupTrace.Mark("ResolveStartServices-end");
            var page = new BlazorPage(player, audio, back, server);
            Debug.WriteLine("K7 MAUI - GetStartPage - BlazorPage created");
            return page;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"K7 MAUI - BlazorPage failed: {ex}");
            return CreateStartupErrorPage(ex);
        }
    }

    private static ContentPage CreateStartupErrorPage(Exception ex)
    {
        var details = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is not null)
            details += $"{Environment.NewLine}{ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

        return new ContentPage
        {
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 24,
                    Spacing = 12,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label
                        {
                            Text = "Unable to start K7",
                            FontAttributes = FontAttributes.Bold,
                            FontSize = 20,
                            HorizontalTextAlignment = TextAlignment.Center
                        },
                        new Label
                        {
                            Text = "Please restart the app. If this keeps happening, clear app data and try again.",
                            HorizontalTextAlignment = TextAlignment.Center
                        },
                        new Label
                        {
                            Text = details,
                            FontSize = 12,
                            TextColor = Colors.Gray
                        }
                    }
                }
            }
        };
    }

    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        Debug.WriteLine($"K7 MAUI - App.xaml.cs - OnAppLinkRequestReceived - Uri: {uri}");
#if WINDOWS
        WindowsProtocolActivation.HandleUri(uri);
#elif LINUX
        LinuxProtocolActivation.HandleUri(uri);
#endif
        base.OnAppLinkRequestReceived(uri);
    }

    public void Restart()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AppReadySignal.Reset();
            FirstPaintSignal.Reset();
            MauiStartupVisual.Reset();
#if ANDROID
            Platform.CurrentActivity?.Recreate();
#else
            Current!.Windows[0]!.Page = GetStartPage();
#endif
        });
    }
}
