using K7.Clients.MAUI.Playback;
using K7.Clients.Shared.Helpers;
using K7.Shared.Dtos;
using K7.Shared.Dtos.Entities.Metadatas.Files.Tracks;
using K7.Shared.Enums;
using K7.Shared.QueryBuilders;

namespace K7.Clients.MAUI.Controls.Video;

public sealed partial class NativeVideoPlayerOverlay
{
    private readonly Label _sidecarSubtitleLabel = new();
    private IReadOnlyList<WebVttCue> _sidecarCues = [];
#if WINDOWS || LINUX
    private Guid? _sidecarFileId;
    private int? _sidecarTrackIndex;
#endif
    private CancellationTokenSource? _sidecarLoadCts;
    private string? _sidecarShownText;

    // Windows: XAML sidecar (or Popup) paints text VTT. Android: Exo SubtitleView only.
    private const int SidecarSubtitleZIndex = 12;

    private void BuildSidecarSubtitleLabel()
    {
        _sidecarSubtitleLabel.HorizontalOptions = LayoutOptions.Center;
        _sidecarSubtitleLabel.VerticalOptions = LayoutOptions.End;
        _sidecarSubtitleLabel.HorizontalTextAlignment = TextAlignment.Center;
        _sidecarSubtitleLabel.LineBreakMode = LineBreakMode.WordWrap;
        _sidecarSubtitleLabel.Margin = new Thickness(48, 0, 48, 36);
        _sidecarSubtitleLabel.Padding = new Thickness(12, 6);
        _sidecarSubtitleLabel.InputTransparent = true;
        _sidecarSubtitleLabel.IsVisible = false;
        _sidecarSubtitleLabel.ZIndex = SidecarSubtitleZIndex;
        ApplySidecarSubtitleStyle();
        AttachSidecarLayer();
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        AttachSidecarLayer();
        ApplyPlaybackStatsHud();
    }

    private void AttachSidecarLayer()
    {
#if WINDOWS
        EnsureWindowsSubtitlePopup();
#elif !ANDROID
        // Android ExoPlayer paints text cues via SubtitleView; sidecar would double-draw.
        if (Parent is not Grid host)
        {
            DetachSidecarLayer();
            return;
        }

        if (ReferenceEquals(_sidecarSubtitleLabel.Parent, host))
            return;

        DetachSidecarLayer();
#if LINUX
        // GTK paints RootGrid children in MAUI ZIndex order: stay above the overlay (5).
        _sidecarSubtitleLabel.ZIndex = 6;
#else
        _sidecarSubtitleLabel.ZIndex = 3;
#endif
        host.Children.Add(_sidecarSubtitleLabel);
#endif
    }

    private void DetachSidecarLayer()
    {
        if (_sidecarSubtitleLabel.Parent is Grid oldHost)
            oldHost.Children.Remove(_sidecarSubtitleLabel);
#if WINDOWS
        CloseWindowsSubtitlePopup();
#endif
    }

    private void RefreshSidecarSubtitles()
    {
#if ANDROID
        // ExoPlayer SubtitleView owns text cues; do not load XAML VTT sidecar.
        ClearSidecarSubtitles();
#elif WINDOWS || LINUX
        if (!IsVisible)
            return;

#if LINUX
        // HLS on Linux is Video.js in WebKitGTK and loadSidecarSubtitleTrack paints the cues
        // there; a GTK label on top would show every cue twice.
        if (WindowsVideoPlayback.ShouldUseWebVideoPlayer(_player.Source?.MimeType, _player.Source?.Url))
        {
            ClearSidecarSubtitles();
            return;
        }
#endif

        var track = _player.SelectedSubtitleTrack;
        var fileId = _player.Source?.IndexedFileId;
        if (track is not { IsTextBased: true } || fileId is null || _server is null)
        {
            ClearSidecarSubtitles();
            return;
        }

        if (_sidecarFileId == fileId && _sidecarTrackIndex == track.Index && _sidecarCues.Count > 0)
        {
            UpdateSidecarCue(_player.CurrentTime);
            return;
        }

        _ = LoadSidecarSubtitlesAsync(fileId.Value, track);
#endif
    }

    private void ClearSidecarSubtitles()
    {
        _sidecarLoadCts?.Cancel();
        _sidecarLoadCts = null;
        _sidecarCues = [];
#if WINDOWS || LINUX
        _sidecarFileId = null;
        _sidecarTrackIndex = null;
#endif
        _sidecarShownText = null;
        _sidecarSubtitleLabel.Text = string.Empty;
        _sidecarSubtitleLabel.IsVisible = false;
#if WINDOWS
        CloseWindowsSubtitlePopup();
#endif
#if ANDROID || WINDOWS || LINUX
        FindBlazorPage()?.ReleaseSidecarTextSubtitles();
#endif
    }

    private void UpdateSidecarCue(double timeSeconds)
    {
#if LINUX
        if (WindowsVideoPlayback.ShouldUseWebVideoPlayer(_player.Source?.MimeType, _player.Source?.Url))
            return;
#endif
        if (IsNextEpisodeVisible || _sidecarCues.Count == 0)
        {
            if (_sidecarShownText is not null)
            {
                _sidecarShownText = null;
                _sidecarSubtitleLabel.Text = string.Empty;
                _sidecarSubtitleLabel.IsVisible = false;
#if WINDOWS
                CloseWindowsSubtitlePopup();
#endif
            }

            return;
        }

        var text = WebVttCueParser.CueAt(_sidecarCues, timeSeconds);
        if (text == _sidecarShownText)
        {
#if WINDOWS
            if (!string.IsNullOrEmpty(text) && !IsWindowsSubtitlePopupLive())
                UpdateWindowsSubtitlePopup(text);
#endif
            return;
        }

        _sidecarShownText = text;
        _sidecarSubtitleLabel.Text = text ?? string.Empty;
        _sidecarSubtitleLabel.IsVisible = text is not null;
#if WINDOWS
        UpdateWindowsSubtitlePopup(text);
#endif
    }

    private void ApplySidecarSubtitleStyle()
    {
        var settings = _videoSettings ?? _player.VideoPlayerUxSettings ?? new VideoPlayerSettingsDto();
        var size = SubtitleStyleHelper.ToFontSizePx(settings.SubtitleFontSize, _deviceType);
#if LINUX
        // Device-independent px scaled like the other clients do (GTK scale stays 1 under WSLg),
        // capped: subtitles grow faster than the chrome glyphs to the eye.
        size = (int)Math.Round(size * Math.Min(Platforms.Linux.LinuxUiScale.Factor, 1.25));
#endif
        _sidecarSubtitleLabel.FontSize = size;
        _sidecarSubtitleLabel.FontFamily = ToMauiSubtitleFontFamily(settings.SubtitleFontFamily);

        if (SubtitleStyleHelper.TryParseHexColor(settings.SubtitleFontColor, out var a, out var r, out var g, out var b))
            _sidecarSubtitleLabel.TextColor = Color.FromRgba(r, g, b, a);
        else
            _sidecarSubtitleLabel.TextColor = Colors.White;

        var bg = Math.Clamp(settings.SubtitleBackgroundOpacity, 0, 1);
        _sidecarSubtitleLabel.BackgroundColor = bg <= 0.01
            ? Colors.Transparent
            : Color.FromRgba(0, 0, 0, bg);

        if (settings.SubtitleShadowEnabled
            && SubtitleStyleHelper.TryParseHexColor(
                settings.SubtitleShadowColor, out var sa, out var sr, out var sg, out var sb))
        {
            _sidecarSubtitleLabel.Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Color.FromRgba(sr, sg, sb, sa)),
                Offset = new Point(1, 1),
                Radius = (float)Math.Max(0, settings.SubtitleShadowBlur),
                Opacity = 1
            };
        }
        else
        {
            _sidecarSubtitleLabel.Shadow = new Shadow { Opacity = 0 };
        }
#if WINDOWS
        ApplyWindowsSubtitlePopupStyle();
#endif
    }

#if WINDOWS || LINUX
    private async Task LoadSidecarSubtitlesAsync(Guid fileId, SubtitleFileTrackDto track)
    {
        _sidecarLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sidecarLoadCts = cts;
        _sidecarFileId = fileId;
        _sidecarTrackIndex = track.Index;
        _sidecarCues = [];
        _sidecarShownText = null;
        _sidecarSubtitleLabel.Text = string.Empty;
        _sidecarSubtitleLabel.IsVisible = false;
#if WINDOWS
        CloseWindowsSubtitlePopup();
#endif

        if (_server is null)
            return;

        var relative = GetIndexedFileSubtitleVttQueryUriBuilder.Build(fileId, track.Index);
        var uri = _server.GetAbsoluteUri(relative);
        if (uri is null)
            return;

        // 503 means ffmpeg is still extracting the VTT from the whole file (minutes on a large
        // remux). Poll on the shared schedule, bounded by elapsed time, and retry transient
        // network errors the same way: 8 polls used to give up silently and left the selected
        // track off until the user toggled it.
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await _server.HttpClient.GetAsync(uri, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var vtt = await response.Content.ReadAsStringAsync(cts.Token);
                        if (!ReferenceEquals(_sidecarLoadCts, cts))
                            return;

                        _sidecarCues = WebVttCueParser.Parse(vtt);
                        VlcPlayerLog.Info(
                            "sidecar vtt cues=" + _sidecarCues.Count + " track=" + track.Index);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (!ReferenceEquals(_sidecarLoadCts, cts))
                                return;
                            ApplySidecarSubtitleStyle();
                            UpdateSidecarCue(_player.CurrentTime);
                            NotifySidecarTextReady(_sidecarCues.Count > 0);
                        });
                        return;
                    }

                    if (!SubtitleVttRetryPolicy.IsExtractionInProgress((int)response.StatusCode))
                    {
                        VlcPlayerLog.Warn(
                            "sidecar vtt fail status=" + (int)response.StatusCode + " track=" + track.Index);
                        MainThread.BeginInvokeOnMainThread(() => NotifySidecarTextReady(false));
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    VlcPlayerLog.Warn(
                        "sidecar vtt error " + ex.GetType().Name + " track=" + track.Index + " attempt=" + attempt);
                }

                if (SubtitleVttRetryPolicy.ShouldGiveUp(started.Elapsed))
                {
                    VlcPlayerLog.Warn(
                        "sidecar vtt gave up track=" + track.Index
                        + " after " + started.Elapsed.TotalSeconds.ToString("F0") + "s");
                    MainThread.BeginInvokeOnMainThread(() => NotifySidecarTextReady(false));
                    return;
                }

                await Task.Delay(SubtitleVttRetryPolicy.DelayBeforeRetry(attempt), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
#endif

    private static string? ToMauiSubtitleFontFamily(SubtitleFontFamily family) => family switch
    {
        SubtitleFontFamily.Monospace => "monospace",
        _ => null
    };
}
