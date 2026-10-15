using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using K7.Clients.MAUI.Linux;
using K7.Clients.MAUI.Playback;
using K7.Clients.Shared.Enums;
using K7.Clients.Shared.Helpers;
using LibVLCSharp;
using DeviceType = K7.Server.Domain.Enums.DeviceType;
using MediaPlayer = LibVLCSharp.MediaPlayer;

namespace K7.Clients.MAUI.Platforms.Linux;

/// <summary>
/// LibVLC 4 Direct Play for Linux (GTK4). Decoded frames arrive through the vmem callbacks
/// (RV32) and are presented as <c>Gdk.MemoryTexture</c> on a <c>Gtk.Picture</c>. HLS transcode
/// never reaches this class: it stays on Video.js in WebKitGTK. Seek and track switches
/// reopen the media with <c>:start-time</c>, like the Windows player, because SetTime over
/// HTTP Direct Play is unreliable.
/// </summary>
internal sealed class LinuxVlcVideoPlayer : IDesktopVlcVideoPlayer
{
    // "BGRA" has a defined byte order (B, G, R, A) on every host. "RV32" changed meaning in
    // VLC 4 and came out with red and blue swapped. The alpha plane is not guaranteed to be
    // written by every converter, so it is forced opaque before the upload.
    private const string Chroma = "BGRA";
    private const int BytesPerPixel = 4;
    private const int FrameBufferCount = 2;

    private static int _resolverInstalled;
    // libvlc 4 shipped next to the app (libvlc/linux-x64, see tools/linux/bundle-libvlc.sh).
    // null when the client runs against a system VLC 4.
    private static LinuxLibVlcBundle.Layout? _bundle;
    // One libvlc instance per process: releasing it while its threads still log / tear the
    // player down crashed the close path. The player is per session, the instance is not.
    private static LibVLC? _sharedLibVlc;
    private static LinuxVlcVideoPlayer? _logTarget;
    // One MediaPlayer per process as well: VLC 4 recycles the video output across inputs
    // (seek / track reopen) but destroys it on player release, and that teardown aborts the
    // "vlc-vout" thread (SIGABRT) on the nightly builds. Sessions only Stop and reuse it. The
    // vmem callbacks and player events are static and forward to the current session.
    private static MediaPlayer? _sharedPlayer;
    private static LinuxVlcVideoPlayer? _current;
    private static readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback = SharedVideoFormat;
    private static readonly MediaPlayer.LibVLCVideoLockCb _lockCallback = SharedVideoLock;
    private static readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback = SharedVideoUnlock;
    private static readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback = SharedVideoDisplay;
    private static readonly object _frameGate = new();
    // One buffer set per libvlc video output, keyed by the vmem opaque handle: VLC 4 stops
    // asynchronously, so the previous vout can still lock/display while the next one formats.
    // Static: the recycled output keeps its buffers across sessions.
    private static readonly Dictionary<IntPtr, FrameBufferSet> _frameSets = new();
    private static IntPtr _nextFrameSetId = (IntPtr)1;

    private readonly Grid _host;
    private readonly LinuxVlcVideoView _videoView;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private VlcAuthProxy? _authProxy;
    private Gtk.Picture? _picture;
    private bool _surfaceLowered;
    private Gdk.Texture? _currentTexture;
    private GLib.Bytes? _pendingFrame;
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;
    private int _pendingFrameStride;
    private int _presentScheduled;
    private string? _lastNativeError;
    private readonly Dictionary<string, int> _nativeLogCounts = new(StringComparer.Ordinal);
    private string? _currentUrl;
    private string? _pendingUrl;
    private string? _pendingAuthorization;
    private double _pendingStartSeconds;
    private bool _active;
    private bool _suppressEnded;
    private bool _directSeekReopenBusy;
    private double? _coalescedDirectSeekSeconds;
    private bool _firstFrameRaised;
    private bool _firstFrameNotified;
    private Task? _teardownTask;
    private bool _pendingPreciseStart;
    private AspectRatioMode _aspect = AspectRatioMode.Fit;
    private int? _pendingAudioOrdinal;
    private int? _pendingSubtitleOrdinal;
    private bool _overlayOwnsTextSubs;
    private bool _holdTransport;
    private double _pinnedPosition;
    private double _pinnedDuration;
    private double _timelineEpochSeconds;
    private bool? _demuxTimelineRelative;
    private double _volume01 = 1;
    private bool _muted;
    private double _rate = 1;
    private int _audioBindAttempts;
    private int _clockTickId;
    private double _lastPublishedSeconds = -1;
    private long _ticksPerSecond = VlcTime.MicrosecondsPerSecond;
    private bool _ticksScaleLogged;
    private int _clockLogCountdown;

    public LinuxVlcVideoPlayer(Grid host)
    {
        _host = host;
        _videoView = new LinuxVlcVideoView
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            InputTransparent = true,
            BackgroundColor = Colors.Black,
            ZIndex = 3,
            IsVisible = false
        };
        _videoView.PictureChanged += OnPictureChanged;
        _picture = _videoView.Picture;
        host.Children.Add(_videoView);
        // The labs layout parents the widget synchronously: lower it before any frame.
        EnsureSurfaceBelowOverlay();
    }

    /// <summary>GTK decode surface, for explicit stack ordering by the page.</summary>
    internal Gtk.Picture? Surface => _picture;

    public event Action? Playing;
    public event Action? Paused;
    public event Action? Ended;
    public event Action<string>? EncounteredError;
    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action? FirstFrame;
    public event Action? Reopening;

    public bool IsActive => _active;

    public double PositionSeconds => ReadVlcSeconds();

    public double DurationSeconds =>
        _pinnedDuration > 1
            ? _pinnedDuration
            : ReadVlcDurationSeconds();

    public void Play(
        string url,
        string? authorizationHeader,
        double startSeconds,
        int? audioOrdinal = null,
        int? subtitleOrdinal = null,
        int? hlsAudioTrackIndex = null,
        double knownDuration = 0)
    {
        if (StreamingSourceKind.IsHls(mimeType: null, url))
        {
            // Routing bug guard: Linux HLS is Video.js in WebKitGTK.
            VlcPlayerLog.Warn("vlc linux refused hls url");
            EncounteredError?.Invoke("hls-not-supported-on-linux-vlc");
            return;
        }

        _pendingUrl = url;
        _currentUrl = url;
        _pendingAuthorization = authorizationHeader;
        _pendingStartSeconds = startSeconds;
        _pendingAudioOrdinal = audioOrdinal;
        _pendingSubtitleOrdinal = subtitleOrdinal;
        lock (_frameGate)
            _nativeLogCounts.Clear();
        _directSeekReopenBusy = false;
        _coalescedDirectSeekSeconds = null;
        _firstFrameNotified = false;
        _firstFrameRaised = false;
        _holdTransport = startSeconds > 1;
        _pinnedPosition = startSeconds > 0 ? startSeconds : 0;
        _timelineEpochSeconds = startSeconds > 1 ? startSeconds : 0;
        _demuxTimelineRelative = startSeconds > 1 ? true : null;
        _pinnedDuration = knownDuration > 1 ? knownDuration : 0;
        _lastPublishedSeconds = _pinnedPosition;
        _ticksPerSecond = VlcTime.MicrosecondsPerSecond;
        _ticksScaleLogged = false;
        _audioBindAttempts = 0;
        _clockTickId++;
        _suppressEnded = false;
        _active = true;
        _videoView.IsVisible = true;
        ApplyAspectCore();

        try
        {
            EnsureEngine();
        }
        catch (Exception ex)
        {
            VlcPlayerLog.Warn("vlc linux engine failed " + ex.GetType().Name + " " + ex.Message);
            _active = false;
            _videoView.IsVisible = false;
            EncounteredError?.Invoke(BlazorPage.LibVlcUnavailablePrefix + " " + ex.Message);
            return;
        }

        StartPending();
    }

    public void UpdateAuthorization(string? authorizationHeader)
    {
        _pendingAuthorization = authorizationHeader;
        _authProxy?.SetAuthorization(authorizationHeader);
    }

    public void Resume()
    {
        if (_player is null || !_active)
            return;

        _player.SetPause(false);
        if (!_player.IsPlaying)
            _player.Play();
        StartClockTick();
    }

    public void Pause()
    {
        if (_player is null || !_active)
            return;

        _clockTickId++;
        _player.SetPause(true);
        var seconds = _lastPublishedSeconds > 0 ? _lastPublishedSeconds : ReadVlcSeconds();
        if (seconds >= 0)
        {
            _lastPublishedSeconds = seconds;
            PositionChanged?.Invoke(seconds);
        }
    }

    public void SetSurfaceVisible(bool visible)
    {
        _videoView.IsVisible = visible && _active;
    }

    public void Stop()
    {
        _clockTickId++;
        _suppressEnded = true;
        _active = false;
        _directSeekReopenBusy = false;
        _coalescedDirectSeekSeconds = null;
        _pendingUrl = null;
        try
        {
            _videoView.IsVisible = false;
        }
        catch
        {
        }

        var player = _player;
        var media = _media;
        var proxy = _authProxy;
        _player = null;
        _libVlc = null;
        _media = null;
        _authProxy = null;
        if (ReferenceEquals(_logTarget, this))
            _logTarget = null;
        if (ReferenceEquals(_current, this))
            _current = null;

        // Stop off the GTK thread: it joins the decoder threads and the vmem callbacks may
        // still be posting frames. The player itself is process-wide and stays alive.
        _teardownTask = Task.Run(() =>
        {
            try
            {
                player?.Stop();
            }
            catch
            {
            }

            try
            {
                if (player is not null)
                    player.Media = null;
            }
            catch
            {
            }

            try
            {
                media?.Dispose();
            }
            catch
            {
            }

            try
            {
                proxy?.Dispose();
            }
            catch
            {
            }
        });

        ClearPresentedFrame();
    }

    public void PrepareForAppExit() => Stop();

    public void Seek(double seconds)
    {
        if (_player is null || !_active)
            return;

        var target = Math.Max(0, seconds);
        _pendingStartSeconds = target;

        // Direct Play over HTTP: SetTime is often ignored. Reopen with :start-time, and
        // coalesce rapid seeks while a reopen is still buffering.
        if (_directSeekReopenBusy)
        {
            _coalescedDirectSeekSeconds = target;
            _pinnedPosition = target;
            _lastPublishedSeconds = target;
            _holdTransport = true;
            PositionChanged?.Invoke(target);
            return;
        }

        SeekDirectReopen(target);
    }

    public void PinDuration(double seconds)
    {
        if (seconds <= 1)
            return;

        _pinnedDuration = Math.Max(_pinnedDuration, seconds);
        DurationChanged?.Invoke(_pinnedDuration);
    }

    public void SetVolume(double volume01)
    {
        _volume01 = Math.Clamp(volume01, 0, 1);
        ApplyOutputLevel();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        ApplyOutputLevel();
    }

    public void SetRate(double rate)
    {
        var previous = _rate;
        _rate = Math.Clamp(rate, 0.25, 4.0);
        if (_player is null)
            return;

        var result = _player.SetRate((float)_rate);
        VlcPlayerLog.Info(
            "vlc rate=" + _rate.ToString("0.##", CultureInfo.InvariantCulture)
            + " result=" + result
            + " now=" + _player.Rate.ToString("0.##", CultureInfo.InvariantCulture));

        // VLC 4 keeps the rate in the player but the running HTTP input does not apply it live
        // (it is honoured when the next input starts, hence "works after restart"). Reopen at
        // the current position so the new input starts at the requested rate.
        var changed = Math.Abs(previous - _rate) > 0.001;
        if (changed && _active && _pendingUrl is null && !string.IsNullOrEmpty(_currentUrl))
            ReopenAtCurrent("rate");
    }

    public void ApplyAspect(AspectRatioMode mode)
    {
        _aspect = mode;
        ApplyAspectCore();
    }

    public bool TrySelectAudio(int ordinal, string? language, string? name)
    {
        if (_player is null)
            return false;

        var tracks = VlcTracks.Snapshot(_player, TrackType.Audio);
        try
        {
            VlcTracks.Log("audio", tracks, ordinal, language, name, VlcTracks.SelectedId(_player, TrackType.Audio));
            if (tracks.Length == 0)
                return false;

            if (!VlcTracks.TryResolve(tracks, ordinal, language, name, out var index, out var track))
                return false;

            _pendingAudioOrdinal = index;
            if (track.Selected)
                return true;

            // Same as Windows: mid-play Select is unreliable on Direct Play, reopen instead.
            ReopenAtCurrent("audio");
            return true;
        }
        finally
        {
            VlcTracks.DisposeAll(tracks);
        }
    }

    public bool TrySelectSubtitle(int? ordinal, string? language, string? name)
    {
        if (_player is null)
            return false;

        if (ordinal is null)
        {
            _pendingSubtitleOrdinal = null;
            _overlayOwnsTextSubs = false;
            _player.Unselect(TrackType.Text);
            VlcPlayerLog.Info("vlc sub off");
            return true;
        }

        var tracks = VlcTracks.Snapshot(_player, TrackType.Text);
        try
        {
            VlcTracks.Log("sub", tracks, ordinal, language, name, VlcTracks.SelectedId(_player, TrackType.Text));
            if (tracks.Length == 0)
                return false;

            if (!VlcTracks.TryResolve(tracks, ordinal, language, name, out var index, out var track))
                return false;

            _pendingSubtitleOrdinal = index;
            if (track.Selected)
                return true;

            ReopenAtCurrent("sub");
            return true;
        }
        finally
        {
            VlcTracks.DisposeAll(tracks);
        }
    }

    public void SetOverlayOwnsTextSubs(bool owns)
    {
        _overlayOwnsTextSubs = owns;
        if (_player is null || !owns)
            return;

        _pendingSubtitleOrdinal = null;
        _player.Unselect(TrackType.Text);
        VlcPlayerLog.Info("vlc sub overlay");
    }

    public void RefreshSubtitleStyle()
    {
    }

    public void LogEsTracks()
    {
        if (_player is null)
            return;

        var audio = VlcTracks.Snapshot(_player, TrackType.Audio);
        var subs = VlcTracks.Snapshot(_player, TrackType.Text);
        try
        {
            VlcTracks.Log("audio", audio, _pendingAudioOrdinal, null, null, VlcTracks.SelectedId(_player, TrackType.Audio));
            VlcTracks.Log("sub", subs, _pendingSubtitleOrdinal, null, null, VlcTracks.SelectedId(_player, TrackType.Text));
        }
        finally
        {
            VlcTracks.DisposeAll(audio);
            VlcTracks.DisposeAll(subs);
        }
    }

    public void Dispose()
    {
        Stop();
        _videoView.PictureChanged -= OnPictureChanged;
        // The pending ClearPresentedFrame / PresentPendingFrame idles must not reach a widget
        // that the layout is about to unparent and free.
        _picture = null;

        // Defer Remove until LibVLC Stop settles. Sync unparent during Direct -> HLS
        // quality swaps can SIGSEGV while vmem still posts frames.
        var host = _host;
        var videoView = _videoView;
        var teardown = _teardownTask ?? Task.CompletedTask;
        _ = teardown.ContinueWith(
            _ => MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    host.Children.Remove(videoView);
                }
                catch
                {
                }
            }),
            TaskScheduler.Default);
    }

    private void OnPictureChanged(Gtk.Picture? picture)
    {
        _picture = picture;
        _surfaceLowered = false;
        ApplyAspectCore();
        if (picture is null)
            return;

        EnsureSurfaceBelowOverlay();
        if (_currentTexture is not null)
            picture.SetPaintable(_currentTexture);
    }

    /// <summary>
    /// GTK4 draws the last sibling on top and the labs layout does not reorder children by
    /// ZIndex at insertion: the surface is added after the native overlay and would cover it
    /// once frames arrive. The handler hands the picture over before the panel parents it, so
    /// this runs again from the first presented frame.
    /// </summary>
    private void EnsureSurfaceBelowOverlay()
    {
        if (_surfaceLowered || _picture is null)
            return;

        try
        {
            if (_picture.GetParent() is not Gtk.Widget parent)
                return;

            _picture.InsertAfter(parent, null);
            _surfaceLowered = true;
            VlcPlayerLog.Info("vlc surface lowered below overlay");
        }
        catch (Exception ex)
        {
            VlcPlayerLog.Warn("vlc surface reorder " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private void EnsureEngine()
    {
        if (_libVlc is not null && _player is not null)
            return;

        InstallNativeResolver();
        try
        {
            Core.Initialize();
        }
        catch (VLCException ex)
        {
            throw new InvalidOperationException("libvlc could not be loaded: " + ex.Message, ex);
        }

        // LibVLCSharp 4 against a distro VLC 3 (libvlc.so.5) loads but calls into changed
        // signatures: refuse early with the version instead of spinning on a dead open.
        string version;
        try
        {
            version = Marshal.PtrToStringAnsi(libvlc_get_version()) ?? "unknown";
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "libvlc not found (no libvlc/linux-x64 bundle next to the app and no system VLC 4): " + ex.Message, ex);
        }

        if (!version.StartsWith("4.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "LibVLC 4 is required but libvlc " + version + " was loaded (distro VLC 3). Install a VLC 4 nightly.");
        }

        VlcPlayerLog.Info("vlc libvlc version " + version);

        var style = VlcSubtitleStyle.ToVlcInstanceOptions(DeviceType.Desktop);
        var args = new List<string>
        {
            "--no-osd",
            "--network-caching=400",
            // VLC 4 dropped "--spdif" on Linux: the "spdif" pass-through decoder (priority 120)
            // is tried first for AC3 / E-AC3 / DTS, the Pulse output fails to open it after a
            // ~10s timeout, then VLC falls back to avcodec ("failing back to linear format").
            // Prefer avcodec up front: PCM from the start, no clock stall at open / seek.
            "--codec=avcodec"
        };
        // Desktop sessions expose PulseAudio / PipeWire. VLC's automatic pick sometimes lands on
        // the raw ALSA "default" device (absent under WSLg) and retries it on every frame.
        if (HasPulseAudioSession())
            args.Add("--aout=pulse");
        // The bundle ships no plugins.dat and installs read-only (/usr/lib/k7): scan the plugin
        // directory instead of warning about a cache that cannot be written.
        if (_bundle is not null)
            args.Add("--no-plugins-cache");
        args.AddRange(style);
        // Debug logs go straight to stderr on Linux (libvlc console logger). The Log event
        // below still receives every message, so keep the console quiet.
        if (_sharedLibVlc is null)
        {
            try
            {
                _sharedLibVlc = new LibVLC(enableDebugLogs: false, args.ToArray());
            }
            catch (VLCException)
            {
                // Drop the optional switches one by one before giving up the audio output.
                var reduced = args.Where(a => a is not "--codec=avcodec").ToArray();
                VlcPlayerLog.Warn("vlc ctor options rejected, retrying without --codec");
                try
                {
                    _sharedLibVlc = new LibVLC(enableDebugLogs: false, reduced);
                    args = reduced.ToList();
                }
                catch (VLCException)
                {
                    VlcPlayerLog.Warn("vlc ctor options rejected, retrying minimal");
                    _sharedLibVlc = new LibVLC(enableDebugLogs: false, "--no-osd");
                    args = ["--no-osd"];
                }
            }

            VlcPlayerLog.Info("vlc args " + string.Join(" ", args));

            _sharedLibVlc.SetUserAgent("K7", "K7");
            _sharedLibVlc.Log += OnSharedLibVlcLog;
        }

        _libVlc = _sharedLibVlc;
        _logTarget = this;
        if (_sharedPlayer is null)
        {
            _sharedPlayer = new MediaPlayer(_libVlc);
            // No cleanup callback: LibVLCSharp's trampoline declares it "ref IntPtr opaque"
            // while libvlc passes "void *opaque" (the GCHandle of the MediaPlayer). The
            // marshaller then dereferences the GCHandle value and GetInstance<MediaPlayer>()
            // aborts the process. Buffer sets are retired on the next format callback instead.
            _sharedPlayer.SetVideoFormatCallbacks(_formatCallback, null);
            _sharedPlayer.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
            Hook(_sharedPlayer);
            VlcPlayerLog.Info("vlc engine linux libvlc4 vmem bgra gtk-picture direct");
        }

        _player = _sharedPlayer;
        _current = this;
    }

    /// <summary>
    /// LibVLCSharp P/Invokes <c>libvlc</c>. Distros ship only the versioned soname unless the
    /// -dev package is installed. Try the VLC 4 and VLC 3 sonames before giving up.
    /// </summary>
    private static void InstallNativeResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
            return;

        PrepareBundle();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // Another component already registered a resolver for this assembly.
        }
    }

    /// <summary>
    /// Loads the bundled libvlc 4 (<c>libvlc/linux-x64</c>, or <c>K7_LIBVLC_DIR</c>) when
    /// present: the private libraries (ffmpeg and friends) and libvlccore are dlopen'ed by full
    /// path first so every DT_NEEDED of libvlc and its plugins resolves to the bundled sonames
    /// whatever the distro ships, then <c>VLC_PLUGIN_PATH</c> points libvlc at the bundled
    /// plugins. Without a bundle the resolver falls back to the system libvlc.
    /// </summary>
    private static void PrepareBundle()
    {
        try
        {
            var layout = LinuxLibVlcBundle.Locate(
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable(LinuxLibVlcBundle.EnvironmentOverride));
            if (layout is null)
                return;

            var pending = LinuxLibVlcBundle.EnumeratePreloadLibraries(layout).ToList();
            var loaded = 0;
            // No dependency graph: retry the failures (a library whose dependency was not loaded
            // yet) until a pass makes no progress.
            while (pending.Count > 0)
            {
                var remaining = new List<string>();
                foreach (var library in pending)
                {
                    if (NativeLibrary.TryLoad(library, out _))
                        loaded++;
                    else
                        remaining.Add(library);
                }

                if (remaining.Count == pending.Count)
                    break;
                pending = remaining;
            }

            foreach (var library in pending)
                VlcPlayerLog.Warn("vlc bundle could not load " + Path.GetFileName(library));

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")))
                LinuxSessionEnvironment.Export("VLC_PLUGIN_PATH", layout.PluginsDirectory);

            _bundle = layout;
            VlcPlayerLog.Info("vlc bundle " + layout.Directory + " libs=" + loaded + (pending.Count == 0 ? "" : " failed=" + pending.Count));
        }
        catch (Exception ex)
        {
            VlcPlayerLog.Warn("vlc bundle " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static bool HasPulseAudioSession()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PULSE_SERVER")))
            return true;

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir)
            && File.Exists(Path.Combine(runtimeDir, "pulse", "native"));
    }

    // Same library name as LibVLCSharp so the resolver above applies.
    [DllImport("libvlc")]
    private static extern IntPtr libvlc_get_version();

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var candidates = libraryName switch
        {
            "libvlc" or "vlc" => new[] { "libvlc.so.12", "libvlc.so.5", "libvlc.so" },
            "libvlccore" or "vlccore" => new[] { "libvlccore.so.9", "libvlccore.so" },
            _ => Array.Empty<string>()
        };
        // The bundled copies first (full paths), the system sonames as a fallback.
        if (_bundle is { } bundle && candidates.Length > 0)
        {
            var bundled = libraryName is "libvlc" or "vlc" ? bundle.LibVlc : bundle.LibVlcCore;
            candidates = [bundled, .. candidates];
        }

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private void DisposeLibVlcCore()
    {
        if (_player is not null)
        {
            try
            {
                _player.Stop();
            }
            catch
            {
            }

            _player = null;
        }

        // The libvlc instance and the player are process-wide (see _sharedLibVlc / _sharedPlayer).
        _libVlc = null;
        if (ReferenceEquals(_logTarget, this))
            _logTarget = null;
        if (ReferenceEquals(_current, this))
            _current = null;
    }

    // ----- vmem callbacks (LibVLC decoder thread) -----

    private static uint SharedVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) =>
        OnVideoFormat(ref opaque, chroma, ref width, ref height, ref pitches, ref lines);

    private static IntPtr SharedVideoLock(IntPtr opaque, IntPtr planes) => OnVideoLock(opaque, planes);

    private static void SharedVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private static void SharedVideoDisplay(IntPtr opaque, IntPtr picture) => _current?.OnVideoDisplay(opaque, picture);

    private static uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var chromaBytes = System.Text.Encoding.ASCII.GetBytes(Chroma);
        Marshal.Copy(chromaBytes, 0, chroma, chromaBytes.Length);

        var frameWidth = (int)Math.Max(1, width);
        var frameHeight = (int)Math.Max(1, height);
        var stride = frameWidth * BytesPerPixel;
        pitches = (uint)stride;
        lines = (uint)frameHeight;

        var set = new FrameBufferSet(frameWidth, frameHeight, stride);
        lock (_frameGate)
        {
            // Keep the previous set alive: with VLC 4's asynchronous stop the previous output
            // may still lock/display while this one is being formatted. Older ones are done.
            foreach (var stale in _frameSets.Where(kv => (long)kv.Key < (long)_nextFrameSetId - 1).ToList())
            {
                stale.Value.Release();
                _frameSets.Remove(stale.Key);
            }

            var id = _nextFrameSetId;
            _nextFrameSetId = (IntPtr)((long)_nextFrameSetId + 1);
            _frameSets[id] = set;
            // LibVLCSharp virtualizes this value (_videoUserData): lock/display get it back as is.
            opaque = id;
        }

        VlcPlayerLog.Info(
            "vlc vmem format "
            + frameWidth.ToString(CultureInfo.InvariantCulture)
            + "x"
            + frameHeight.ToString(CultureInfo.InvariantCulture)
            + " sets="
            + _frameSets.Count.ToString(CultureInfo.InvariantCulture));
        return FrameBufferCount;
    }

    private static IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        lock (_frameGate)
        {
            if (!_frameSets.TryGetValue(opaque, out var set))
                return IntPtr.Zero;

            var buffer = set.NextBuffer();
            if (buffer == IntPtr.Zero)
                return IntPtr.Zero;

            Marshal.WriteIntPtr(planes, buffer);
            return buffer;
        }
    }

    private unsafe void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        if (picture == IntPtr.Zero || !_active)
            return;

        GLib.Bytes frame;
        int width;
        int height;
        int stride;
        lock (_frameGate)
        {
            if (!_frameSets.TryGetValue(opaque, out var set) || !set.Owns(picture))
                return;

            // Copy now: VLC reuses this buffer for the frame after next.
            ForceOpaqueAlpha(picture, set.Size);
            frame = GLib.Bytes.New(new Span<byte>((void*)picture, set.Size));
            width = set.Width;
            height = set.Height;
            stride = set.Stride;
        }

        GLib.Bytes? replaced;
        lock (_frameGate)
        {
            replaced = _pendingFrame;
            _pendingFrame = frame;
            _pendingFrameWidth = width;
            _pendingFrameHeight = height;
            _pendingFrameStride = stride;
        }

        replaced?.Dispose();

        if (Interlocked.Exchange(ref _presentScheduled, 1) == 0)
            GLib.Functions.IdleAdd(0, PresentPendingFrame);
    }

    /// <summary>Sets the alpha byte of every BGRA pixel to 0xFF (our own buffer, VLC is done writing).</summary>
    private static unsafe void ForceOpaqueAlpha(IntPtr picture, int size)
    {
        var pixels = new Span<uint>((void*)picture, size / BytesPerPixel);
        var mask = new System.Numerics.Vector<uint>(0xFF000000u);
        var width = System.Numerics.Vector<uint>.Count;
        var i = 0;
        for (; i + width <= pixels.Length; i += width)
        {
            var chunk = new System.Numerics.Vector<uint>(pixels.Slice(i, width));
            (chunk | mask).CopyTo(pixels.Slice(i, width));
        }

        for (; i < pixels.Length; i++)
            pixels[i] |= 0xFF000000u;
    }

    // ----- GTK main thread -----

    private bool PresentPendingFrame()
    {
        Interlocked.Exchange(ref _presentScheduled, 0);
        GLib.Bytes? frame;
        int width;
        int height;
        int stride;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            width = _pendingFrameWidth;
            height = _pendingFrameHeight;
            stride = _pendingFrameStride;
        }

        if (frame is null)
            return false;

        if (!_active)
        {
            frame.Dispose();
            return false;
        }

        try
        {
            EnsureSurfaceBelowOverlay();
            var texture = Gdk.MemoryTexture.New(width, height, Gdk.MemoryFormat.B8g8r8a8, frame, (nuint)stride);
            var previous = _currentTexture;
            _currentTexture = texture;
            _picture?.SetPaintable(texture);
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            VlcPlayerLog.Warn("vlc present " + ex.GetType().Name);
        }
        finally
        {
            frame.Dispose();
        }

        if (!_firstFrameRaised)
            RaiseFirstFrame();

        return false;
    }

    private void ClearPresentedFrame()
    {
        GLib.Bytes? pending;
        lock (_frameGate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        pending?.Dispose();
        Post(() =>
        {
            try
            {
                _picture?.SetPaintable(null);
            }
            catch
            {
            }

            _currentTexture?.Dispose();
            _currentTexture = null;
        });
    }

    /// <summary>Pixel buffers of one libvlc video output (vmem opaque).</summary>
    private sealed class FrameBufferSet
    {
        private readonly IntPtr[] _buffers = new IntPtr[FrameBufferCount];
        private int _nextLockIndex;

        public FrameBufferSet(int width, int height, int stride)
        {
            Width = width;
            Height = height;
            Stride = stride;
            Size = stride * height;
            for (var i = 0; i < FrameBufferCount; i++)
                _buffers[i] = Marshal.AllocHGlobal(Size);
        }

        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public int Size { get; }

        public IntPtr NextBuffer()
        {
            var buffer = _buffers[_nextLockIndex];
            _nextLockIndex = (_nextLockIndex + 1) % FrameBufferCount;
            return buffer;
        }

        public bool Owns(IntPtr picture)
        {
            foreach (var buffer in _buffers)
            {
                if (buffer != IntPtr.Zero && buffer == picture)
                    return true;
            }

            return false;
        }

        public void Release()
        {
            for (var i = 0; i < FrameBufferCount; i++)
            {
                if (_buffers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffers[i]);
                    _buffers[i] = IntPtr.Zero;
                }
            }

        }
    }

    // ----- playback -----

    private void StartPending()
    {
        var url = _pendingUrl;
        if (string.IsNullOrEmpty(url) || _libVlc is null || _player is null)
            return;

        _pendingUrl = null;
        _suppressEnded = true;
        StopPlayback(keepSession: false);
        _suppressEnded = false;
        _firstFrameRaised = false;
        _firstFrameNotified = false;
        _audioBindAttempts = 0;
        StartMedia(url);
    }

    private void StartMedia(string url)
    {
        if (_libVlc is null || _player is null)
            return;

        Media media;
        var via = "http";
        if (LocalPlaybackUrl.TryGetLocalFilesystemPath(url, out var localPath))
        {
            if (!File.Exists(localPath))
                VlcPlayerLog.Warn("vlc local missing path=" + localPath);

            media = new Media(localPath, FromType.FromPath);
            via = "file";
        }
        else
        {
            var playUrl = EnsurePlayProxy(url);
            via = _authProxy is { LocalUrl: not null } ? "http-proxy" : "http-query";
            media = new Media(playUrl, FromType.FromLocation);
            media.AddOption(":network-caching=400");
            media.AddOption(":http-reconnect");
            if (_authProxy is null)
                AddHttpExtraHeader(media, _pendingAuthorization);
        }

        AddMediaPlaybackOptions(media);
        _media = media;
        _player.Play(media);
        ApplyOutputLevel();
        _player.SetRate((float)_rate);
        StartClockTick();
        var precise = _pendingPreciseStart;
        _pendingPreciseStart = false;
        VlcPlayerLog.Info(
            "vlc play url="
            + VlcPlayerLog.SummarizeUrl(url)
            + " start="
            + _pendingStartSeconds.ToString("F3", CultureInfo.InvariantCulture)
            + "s auth="
            + !string.IsNullOrEmpty(_pendingAuthorization)
            + " via="
            + via
            + " audio="
            + (_pendingAudioOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " sub="
            + (_pendingSubtitleOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + (precise ? " seek=precise" : ""));
    }

    private void AddMediaPlaybackOptions(Media media)
    {
        if (_pendingPreciseStart)
            media.AddOption(":no-input-fast-seek");
        else
            media.AddOption(":input-fast-seek");

        if (Math.Abs(_rate - 1) > 0.001)
            media.AddOption(":rate=" + _rate.ToString("0.###", CultureInfo.InvariantCulture));

        // VLC 4's Matroska demuxer loads the Cues (Range requests at the end of the file) but
        // then fails DEMUX_SET_TIME over HTTP: the core falls back to reading and discarding
        // from the first cluster up to ":start-time" (1.9 GB for a resume at 4778s) and
        // reports the remaining duration as Length. The avformat demuxer seeks through the
        // Cues directly. K7_VLC_DEMUX=native restores the VLC demuxers.
        var demux = Environment.GetEnvironmentVariable("K7_VLC_DEMUX");
        if (string.IsNullOrEmpty(demux))
            demux = "avformat";
        if (!string.Equals(demux, "native", StringComparison.OrdinalIgnoreCase))
            media.AddOption(":demux=" + demux);

        if (_pendingStartSeconds > 0)
        {
            media.AddOption(
                ":start-time="
                + _pendingStartSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        if (_pendingAudioOrdinal is int audio && audio >= 0)
            media.AddOption(":audio-track=" + audio.ToString(CultureInfo.InvariantCulture));

        if (_pendingSubtitleOrdinal is int sub && sub >= 0)
            media.AddOption(":sub-track=" + sub.ToString(CultureInfo.InvariantCulture));
        else
            media.AddOption(":sub-track=-1");
    }

    private void SeekDirectReopen(double targetSeconds)
    {
        var url = _currentUrl;
        if (string.IsNullOrEmpty(url) || !_active)
        {
            _directSeekReopenBusy = false;
            _coalescedDirectSeekSeconds = null;
            return;
        }

        var fromPlayer = DurationSeconds;
        if (fromPlayer > 1)
            _pinnedDuration = Math.Max(_pinnedDuration, fromPlayer);

        _directSeekReopenBusy = true;
        _coalescedDirectSeekSeconds = null;
        _pendingStartSeconds = targetSeconds;
        _pinnedPosition = targetSeconds;
        _timelineEpochSeconds = targetSeconds > 1 ? targetSeconds : 0;
        _demuxTimelineRelative = targetSeconds > 1 ? true : null;
        _holdTransport = true;
        _pendingPreciseStart = true;
        _pendingUrl = url;
        _firstFrameRaised = false;
        _firstFrameNotified = false;
        _audioBindAttempts = 0;
        _lastPublishedSeconds = targetSeconds;
        _suppressEnded = true;
        Reopening?.Invoke();
        VlcPlayerLog.Info(
            "vlc reopen reason=seek start="
            + targetSeconds.ToString("F3", CultureInfo.InvariantCulture)
            + "s duration="
            + _pinnedDuration.ToString("F1", CultureInfo.InvariantCulture));
        if (_pinnedDuration > 1)
            DurationChanged?.Invoke(_pinnedDuration);
        PositionChanged?.Invoke(targetSeconds);
        StopPlayback(keepSession: true);
        _suppressEnded = false;
        StartMedia(url);
    }

    private void TryDrainCoalescedDirectSeek()
    {
        var next = _coalescedDirectSeekSeconds;
        _coalescedDirectSeekSeconds = null;
        if (next is null)
        {
            _directSeekReopenBusy = false;
            return;
        }

        var target = next.Value;
        Post(() =>
        {
            if (!_active)
            {
                _directSeekReopenBusy = false;
                return;
            }

            SeekDirectReopen(target);
        });
    }

    private void ReopenAtCurrent(string reason)
    {
        var url = _currentUrl;
        if (string.IsNullOrEmpty(url) || !_active)
            return;

        var resumeAt = ResolveResumeSeconds();
        if (_pinnedDuration <= 1)
        {
            var duration = ReadVlcDurationSeconds();
            if (duration > 1)
                _pinnedDuration = duration;
        }

        _pendingStartSeconds = resumeAt;
        _pinnedPosition = resumeAt;
        _timelineEpochSeconds = resumeAt > 1 ? resumeAt : 0;
        _demuxTimelineRelative = resumeAt > 1 ? true : null;
        _holdTransport = true;
        _pendingPreciseStart = false;
        _pendingUrl = url;
        _firstFrameRaised = false;
        _firstFrameNotified = false;
        _audioBindAttempts = 0;
        _lastPublishedSeconds = resumeAt;
        _suppressEnded = true;
        Reopening?.Invoke();
        if (_pinnedDuration > 1)
            DurationChanged?.Invoke(_pinnedDuration);
        PositionChanged?.Invoke(resumeAt);
        VlcPlayerLog.Info(
            "vlc reopen reason="
            + reason
            + " start="
            + _pendingStartSeconds.ToString("F3", CultureInfo.InvariantCulture)
            + "s audio="
            + (_pendingAudioOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " sub="
            + (_pendingSubtitleOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "-"));
        StopPlayback(keepSession: true);
        _suppressEnded = false;
        StartMedia(url);
    }

    private double ResolveResumeSeconds()
    {
        var published = Math.Max(
            Math.Max(_lastPublishedSeconds, _pinnedPosition),
            _pendingStartSeconds);
        var raw = ReadVlcSeconds();
        if (raw > 1 && published > 1 && Math.Abs(raw - published) <= 4)
            return raw;
        if (published > 1)
            return published;
        return Math.Max(raw, 0);
    }

    private string EnsurePlayProxy(string url)
    {
        if (_authProxy is { LocalUrl: not null }
            && !_authProxy.IsHls
            && string.Equals(_authProxy.TargetUrl, url, StringComparison.Ordinal))
        {
            return _authProxy.LocalUrl;
        }

        _authProxy?.Dispose();
        _authProxy = new VlcAuthProxy(_pendingAuthorization);
        if (_authProxy.TryStart(url) && !string.IsNullOrEmpty(_authProxy.LocalUrl))
            return _authProxy.LocalUrl;

        _authProxy.Dispose();
        _authProxy = null;
        return AppendAccessToken(url, _pendingAuthorization);
    }

    private static void AddHttpExtraHeader(Media media, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return;

        media.AddOption(":http-extra-header=" + authorizationHeader);
    }

    private void StopPlayback(bool keepSession = false)
    {
        if (_player is null)
            return;

        try
        {
            _player.Stop();
        }
        catch
        {
        }

        _player.Media = null;
        _media?.Dispose();
        _media = null;
        if (keepSession)
            return;

        _authProxy?.Dispose();
        _authProxy = null;
    }

    private static void Hook(MediaPlayer player)
    {
        player.Playing += (sender, e) => _current?.OnPlaying(sender, e);
        player.Paused += (sender, e) => _current?.OnPaused(sender, e);
        player.Stopped += (sender, e) => _current?.OnStopped(sender, e);
        player.EncounteredError += (sender, e) => _current?.OnEncounteredError(sender, e);
        player.LengthChanged += (sender, e) => _current?.OnLengthChanged(sender, e);
        player.Vout += (sender, e) => _current?.OnVout(sender, e);
        player.ESAdded += (sender, e) => _current?.OnEsAdded(sender, e);
    }

    private void OnPlaying(object? sender, EventArgs e) =>
        Post(() =>
        {
            if (!_active)
                return;

            ApplyOutputLevel();
            BindPendingTracksIfNeeded();
            // Wait for the first presented frame before lifting the veil. Fall back after 2.5s.
            ScheduleDirectFirstFrameFallback();
            Playing?.Invoke();
            if (_firstFrameRaised)
                StartClockTick();
        });

    private void ScheduleDirectFirstFrameFallback()
    {
        var playId = _clockTickId;
        PostDelayed(2_500, () =>
        {
            if (!_active || _firstFrameRaised || playId != _clockTickId)
                return;

            VlcPlayerLog.Warn("vlc direct first-frame fallback after 2.5s");
            RaiseFirstFrame();
            StartClockTick();
        });
    }

    private void OnPaused(object? sender, EventArgs e) =>
        Post(() =>
        {
            if (_active)
                Paused?.Invoke();
        });

    private void OnStopped(object? sender, EventArgs e) =>
        Post(() =>
        {
            if (_active && !_suppressEnded)
                Ended?.Invoke();
        });

    private void OnEncounteredError(object? sender, EventArgs e) =>
        Post(() =>
        {
            if (!_active)
                return;

            _directSeekReopenBusy = false;
            var pendingSeek = _coalescedDirectSeekSeconds;
            _coalescedDirectSeekSeconds = null;

            var detail = string.IsNullOrEmpty(_lastNativeError)
                ? (_media?.Mrl ?? "vlc-error")
                : _lastNativeError;
            VlcPlayerLog.Warn("vlc error mrl=" + VlcPlayerLog.SummarizeUrl(_media?.Mrl) + " native=" + detail);
            EncounteredError?.Invoke(detail);

            if (pendingSeek is double retry)
                SeekDirectReopen(retry);
        });

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (!VlcTime.IsReliableLength(e.Length, _pinnedDuration))
            return;

        NoteVlcTimeScale(e.Length);
        var seconds = VlcTime.ToSeconds(e.Length, _ticksPerSecond);
        if (seconds <= 1)
            return;

        if (!VlcTime.TryAcceptDuration(seconds, _pinnedDuration, out seconds))
            return;

        if (_pinnedDuration > 1)
        {
            if (seconds >= _pinnedDuration * 0.9 && seconds <= _pinnedDuration * 1.1)
                _pinnedDuration = Math.Max(_pinnedDuration, seconds);
            Post(() =>
            {
                if (_active)
                    DurationChanged?.Invoke(_pinnedDuration);
            });
            return;
        }

        _pinnedDuration = seconds;
        Post(() =>
        {
            if (!_active)
                return;

            DurationChanged?.Invoke(seconds);
            PublishTransportFromPlayer();
        });
    }

    private void OnVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        if (e.Count <= 0)
            return;

        Post(() =>
        {
            if (_active)
                ApplyAspectCore();
        });
    }

    private void OnEsAdded(object? sender, EventArgs e) =>
        Post(() =>
        {
            if (_active)
                BindPendingTracksIfNeeded();
        });

    private void RaiseFirstFrame()
    {
        if (_firstFrameRaised)
            return;

        _firstFrameRaised = true;
        ApplyOutputLevel();
        BindPendingTracksIfNeeded();
        PublishTransportFromPlayer();
        if (!_firstFrameNotified)
        {
            _firstFrameNotified = true;
            FirstFrame?.Invoke();
        }

        StartClockTick();
        TryDrainCoalescedDirectSeek();
    }

    private void StartClockTick()
    {
        var id = ++_clockTickId;
        PostDelayed(100, () => TickOverlayClock(id));
    }

    private void TickOverlayClock(int id)
    {
        if (!_active || id != _clockTickId || _player is null)
            return;

        if (_player.IsPlaying)
        {
            var seconds = ReadVlcSeconds();
            if (--_clockLogCountdown <= 0)
            {
                // Every ~5s: raw libvlc clock vs. what the overlay receives (resume / timeline bugs).
                _clockLogCountdown = 50;
                VlcPlayerLog.Info(
                    "vlc clock time=" + _player.Time.ToString(CultureInfo.InvariantCulture)
                    + " pos=" + _player.Position.ToString("0.####", CultureInfo.InvariantCulture)
                    + " len=" + _player.Length.ToString(CultureInfo.InvariantCulture)
                    + " mapped=" + seconds.ToString("0.##", CultureInfo.InvariantCulture)
                    + " hold=" + _holdTransport
                    + " pinned=" + _pinnedPosition.ToString("0.##", CultureInfo.InvariantCulture)
                    + " epoch=" + _timelineEpochSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                    + " relative=" + (_demuxTimelineRelative?.ToString() ?? "?"));
            }

            if (_holdTransport)
            {
                seconds = VlcTime.FollowAfterReopen(
                    seconds,
                    _pinnedPosition,
                    ref _holdTransport);
            }

            if (seconds >= 0 && (seconds > 0 || _holdTransport || _pendingStartSeconds <= 1))
            {
                _lastPublishedSeconds = seconds;
                PositionChanged?.Invoke(seconds);
            }
        }
        else if (_holdTransport)
        {
            var held = _lastPublishedSeconds > 0 ? _lastPublishedSeconds : _pinnedPosition;
            PositionChanged?.Invoke(held);
        }

        PostDelayed(100, () => TickOverlayClock(id));
    }

    private void NoteVlcTimeScale(long lengthTicks)
    {
        if (!VlcTime.IsReliableLength(lengthTicks, _pinnedDuration))
            return;

        var detected = VlcTime.DetectTicksPerSecond(lengthTicks, _pinnedDuration);
        if (detected == _ticksPerSecond && _ticksScaleLogged)
            return;

        _ticksPerSecond = detected;
        if (_ticksScaleLogged)
            return;

        _ticksScaleLogged = true;
        VlcPlayerLog.Info(
            "vlc time scale="
            + (_ticksPerSecond == VlcTime.MillisecondsPerSecond ? "ms" : "us")
            + " lengthRaw="
            + lengthTicks.ToString(CultureInfo.InvariantCulture)
            + " knownDuration="
            + _pinnedDuration.ToString("F1", CultureInfo.InvariantCulture)
            + "s");
    }

    private double ReadVlcDurationSeconds()
    {
        if (_player is not { Length: > 0 } player)
            return 0;

        NoteVlcTimeScale(player.Length);
        return VlcTime.ToSeconds(player.Length, _ticksPerSecond);
    }

    private double ReadVlcSeconds()
    {
        if (_player is null)
            return Math.Max(0, _lastPublishedSeconds);

        if (_player.Length > 0)
            NoteVlcTimeScale(_player.Length);

        var duration = _pinnedDuration > 1 ? _pinnedDuration : ReadVlcDurationSeconds();
        var timeSeconds = _player.Time > 0
            ? VlcTime.ToSeconds(_player.Time, _ticksPerSecond)
            : 0;

        // VLC 4 after ":start-time" reports Time sometimes relative to the start, sometimes
        // absolute, and can switch within one input (the first reading right after the open
        // locked "absolute" and a later relative 1s read as 00:01). Pick whichever reading
        // keeps the published clock continuous instead of locking on the first sample.
        if (_timelineEpochSeconds > 1 && timeSeconds > 0 && _lastPublishedSeconds > 1)
        {
            var relative = _timelineEpochSeconds + timeSeconds;
            var absolute = timeSeconds;
            var pickRelative = Math.Abs(relative - _lastPublishedSeconds) <= Math.Abs(absolute - _lastPublishedSeconds);
            _demuxTimelineRelative = pickRelative;
            return pickRelative ? relative : absolute;
        }

        return VlcTime.MapDemuxSeconds(
            timeSeconds,
            _player.Position,
            duration,
            _timelineEpochSeconds,
            ref _demuxTimelineRelative);
    }

    private void PublishTransportFromPlayer()
    {
        var duration = _pinnedDuration > 1 ? _pinnedDuration : ReadVlcDurationSeconds();
        if (duration > 1)
        {
            _pinnedDuration = Math.Max(_pinnedDuration, duration);
            DurationChanged?.Invoke(_pinnedDuration);
        }

        var seconds = ReadVlcSeconds();
        if (_holdTransport)
        {
            seconds = VlcTime.FollowAfterReopen(
                seconds,
                _pinnedPosition,
                ref _holdTransport);
        }

        _lastPublishedSeconds = seconds;
        if (seconds > 0 || _holdTransport)
            PositionChanged?.Invoke(seconds);
    }

    private void ApplyOutputLevel()
    {
        if (_player is null)
            return;

        // Linux: plain 0-100 software gain (PulseAudio / PipeWire own the sink volume).
        _player.SetVolume((int)Math.Clamp(_volume01 * 100.0, 0, 100));
        _player.Mute = _muted;
    }

    private void BindPendingTracksIfNeeded()
    {
        if (_player is null)
            return;

        BindAudio();
        if (_overlayOwnsTextSubs)
        {
            _player.Unselect(TrackType.Text);
            return;
        }

        BindSubtitle();
    }

    private void BindAudio()
    {
        if (_player is null)
            return;

        var tracks = VlcTracks.Snapshot(_player, TrackType.Audio);
        try
        {
            if (tracks.Length == 0)
            {
                if (_audioBindAttempts < 16)
                {
                    _audioBindAttempts++;
                    PostDelayed(250, BindPendingTracksIfNeeded);
                }

                return;
            }

            if (!VlcTracks.TryResolve(tracks, _pendingAudioOrdinal, null, null, out var index, out var track))
            {
                if (_audioBindAttempts < 16)
                {
                    _audioBindAttempts++;
                    PostDelayed(250, BindPendingTracksIfNeeded);
                }

                return;
            }

            if (track.Selected)
                return;

            _player.Select(track);
            VlcPlayerLog.Info(
                "vlc audio bind id="
                + (track.Id ?? "-")
                + " name="
                + (track.Name ?? "-")
                + " ordinal="
                + index);
        }
        finally
        {
            VlcTracks.DisposeAll(tracks);
        }
    }

    private void BindSubtitle()
    {
        if (_player is null)
            return;

        if (_pendingSubtitleOrdinal is null)
        {
            // ":sub-track=-1" does not stop VLC 4 from auto-selecting a text track.
            _player.Unselect(TrackType.Text);
            return;
        }

        var tracks = VlcTracks.Snapshot(_player, TrackType.Text);
        try
        {
            if (!VlcTracks.TryResolve(tracks, _pendingSubtitleOrdinal, null, null, out _, out var track))
                return;

            if (track.Selected)
                return;

            _player.Select(track);
            VlcPlayerLog.Info("vlc sub bind id=" + (track.Id ?? "-") + " name=" + (track.Name ?? "-"));
        }
        finally
        {
            VlcTracks.DisposeAll(tracks);
        }
    }

    private void ApplyAspectCore()
    {
        // vmem renders at the decoded size. The picture widget applies the aspect mode.
        var fit = _aspect switch
        {
            AspectRatioMode.Stretch => Gtk.ContentFit.Fill,
            AspectRatioMode.Fill => Gtk.ContentFit.Cover,
            _ => Gtk.ContentFit.Contain
        };

        try
        {
            _picture?.SetContentFit(fit);
        }
        catch
        {
        }

        if (_player is null)
            return;

        _player.AspectRatio = null;
        _player.Scale = 0;
    }

    private static void OnSharedLibVlcLog(object? sender, LogEventArgs e) => _logTarget?.OnLibVlcLog(sender, e);

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        var module = e.Module ?? "-";
        var message = e.Message ?? "";
        if (IsNoisyVlcLog(message))
            return;

        if (e.Level is not (LogLevel.Error or LogLevel.Warning))
            return;

        if (message.Length > 180)
            message = message[..180];

        if (e.Level == LogLevel.Error)
            _lastNativeError = module + " " + message;

        // A dead audio output (ALSA "default" missing under WSLg / headless) cycles the same
        // three or four lines per decoded frame: log each distinct line once per session, then
        // every 100th occurrence, so the useful lines stay readable.
        var line = module + " " + message;
        int count;
        lock (_frameGate)
        {
            _nativeLogCounts.TryGetValue(line, out count);
            count++;
            _nativeLogCounts[line] = count;
        }

        if (count == 1)
            VlcPlayerLog.Warn("vlc-native " + line);
        else if (count % 100 == 0)
            VlcPlayerLog.Warn("vlc-native " + line + " (x" + count + ")");
    }

    private static bool IsNoisyVlcLog(string message) =>
        message.Contains("picture is too late", StringComparison.Ordinal)
        || message.Contains("More than 11 late frames", StringComparison.Ordinal)
        || message.Contains("not implemented", StringComparison.Ordinal)
        || message.Contains("invalid stop-time", StringComparison.Ordinal)
        || message.Contains("playback too early", StringComparison.Ordinal)
        || message.Contains("playback too late", StringComparison.Ordinal)
        || message.Contains("down-sampling", StringComparison.Ordinal)
        || message.Contains("up-sampling", StringComparison.Ordinal)
        // avformat demux over HTTP: one clock context per cluster, every second.
        || message.Contains("clock gap, unexpected stream discontinuity", StringComparison.Ordinal)
        || message.Contains("new clock context", StringComparison.Ordinal)
        || message.Contains("clock(input)", StringComparison.Ordinal)
        || message.Contains("feeding synchro with a new reference point", StringComparison.Ordinal);

    private static string AppendAccessToken(string url, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return url;

        var token = authorizationHeader;
        const string bearer = "Bearer ";
        if (token.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            token = token[bearer.Length..];

        if (url.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("ephemeral_token=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return url + separator + "access_token=" + Uri.EscapeDataString(token);
    }

    private static void PostDelayed(int delayMs, Action action)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            Post(action);
        });
    }

    /// <summary>GTK main loop. LibVLC events arrive on decoder / input threads.</summary>
    private static void Post(Action action)
    {
        GLib.Functions.IdleAdd(0, () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                VlcPlayerLog.Warn("vlc post " + ex.GetType().Name + " " + ex.Message);
            }

            return false;
        });
    }
}
