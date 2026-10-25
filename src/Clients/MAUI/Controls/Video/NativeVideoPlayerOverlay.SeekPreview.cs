using K7.Clients.Shared.Helpers;
using Microsoft.Maui.Controls.Shapes;
using SkiaSharp;

namespace K7.Clients.MAUI.Controls.Video;

/// <summary>
/// Seek preview HUD: cropped sprite-sheet thumbnail (Skia) + chapter title + time,
/// positioned above the seek thumb like Blazor SeekBar <c>.thumbnail</c>.
/// </summary>
public sealed partial class NativeVideoPlayerOverlay
{
    /// <summary>TV/desktop DPAD scrub: coalesce encodes so remotes do not thrash CPU.</summary>
    private const int TvCropDebounceMs = 90;

    private readonly Border _seekPreview = new();
    private readonly Border _seekPreviewImageBorder = new();
    private readonly Image _seekPreviewImage = new();
    private readonly Label _seekPreviewTime = new();
    private readonly Label _seekPreviewChapterTitle = new();
    private readonly object _spriteLock = new();

    private SKBitmap? _spriteBitmap;
    private string? _spriteLoadedUrl;
    private string? _spriteLoadingUrl;
    private Task? _spriteLoadTask;
    private int _spriteLoadGeneration;
    private int _lastSpriteIndex = -1;
    private int _cropVersion;
    private CancellationTokenSource? _cropDebounceCts;
    private (string Url, double Time, int SpriteIndex)? _phonePendingCrop;
    private int _phoneCropLoopRunning;

    private void BuildSeekPreview()
    {
        _seekPreviewChapterTitle.TextColor = Colors.White;
        _seekPreviewChapterTitle.FontSize = 12;
        _seekPreviewChapterTitle.FontAttributes = FontAttributes.Bold;
        _seekPreviewChapterTitle.HorizontalTextAlignment = TextAlignment.Center;
        _seekPreviewChapterTitle.IsVisible = false;

        _seekPreviewTime.TextColor = Colors.White;
        _seekPreviewTime.FontSize = 13;
        _seekPreviewTime.HorizontalTextAlignment = TextAlignment.Center;
        _seekPreviewTime.Margin = new Thickness(0, 4, 0, 0);

        _seekPreviewImage.WidthRequest = NativeSeekThumbnailHelper.ThumbWidth / 2.0;
        _seekPreviewImage.HeightRequest = NativeSeekThumbnailHelper.ThumbHeight / 2.0;
        _seekPreviewImage.Aspect = Aspect.AspectFill;

        _seekPreviewImageBorder.Content = _seekPreviewImage;
        _seekPreviewImageBorder.Stroke = Colors.White;
        _seekPreviewImageBorder.StrokeThickness = 1;
        _seekPreviewImageBorder.StrokeShape = new RoundRectangle { CornerRadius = 4 };
        _seekPreviewImageBorder.Padding = 0;
        _seekPreviewImageBorder.IsVisible = false;

        var stack = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 4,
            Children = { _seekPreviewImageBorder, _seekPreviewChapterTitle, _seekPreviewTime }
        };

        _seekPreview.Content = stack;
        _seekPreview.BackgroundColor = Color.FromArgb("#B3000000");
        _seekPreview.Stroke = Colors.Transparent;
        _seekPreview.StrokeShape = new RoundRectangle { CornerRadius = 8 };
        _seekPreview.Padding = new Thickness(8);
        _seekPreview.IsVisible = false;
        _seekPreview.Opacity = 0;
        // Stable size so the first PositionSeekPreview is not glued to the bar then jumping.
        _seekPreview.MinimumWidthRequest = 160;
        _seekPreview.HorizontalOptions = LayoutOptions.Start;
        _seekPreview.VerticalOptions = LayoutOptions.Start;
        _seekPreview.InputTransparent = true;
        _seekPreview.SizeChanged += (_, _) =>
        {
            if (!_seekPreview.IsVisible || _seekPreview.Width <= 1 || _seekPreview.Height <= 1)
                return;
            PositionSeekPreview(_seekBar.DisplayTime);
            _seekPreview.Opacity = 1;
        };
        Children.Add(_seekPreview);
    }

    private void UpdateSeekPreview(bool show)
    {
        if (!show)
        {
            _seekPreview.IsVisible = false;
            _seekPreview.Opacity = 0;
            _phonePendingCrop = null;
            _cropDebounceCts?.Cancel();
            return;
        }

        var time = _seekBar.DisplayTime;
        _seekPreviewTime.Text = NativeTimeFormatting.Format(time);

        var chapterTitle = GetHoveredChapterTitle(time);
        _seekPreviewChapterTitle.IsVisible = !string.IsNullOrEmpty(chapterTitle);
        _seekPreviewChapterTitle.Text = chapterTitle ?? string.Empty;

        _seekPreview.IsVisible = true;
        PositionSeekPreview(time);
        // Phone finger scrub: do not wait for SizeChanged or the HUD stays a black slab.
        if (IsPhoneOrTablet() || (_seekPreview.Width > 1 && _seekPreview.Height > 1))
            _seekPreview.Opacity = 1;

        var url = ResolveSeekThumbnailsUrl(_player.Source?.ThumbnailsUrl);
        if (string.IsNullOrEmpty(url))
        {
            _seekPreviewImageBorder.IsVisible = false;
            return;
        }

        // Warm the sheet once; never cancel that download from scrub ticks.
        _ = EnsureSpriteBitmapAsync(url);

        var spriteIndex = NativeSeekThumbnailHelper.GetSpriteIndex(time);
        if (spriteIndex == _lastSpriteIndex && _seekPreviewImage.Source is not null)
        {
            _seekPreviewImageBorder.IsVisible = true;
            return;
        }

        ScheduleSpriteCrop(url, time, spriteIndex);
    }

    private void PositionSeekPreview(double time)
    {
        var duration = Math.Max(_player.Duration, 0.001);
        var ratio = Math.Clamp(time / duration, 0, 1);

        var barX = GetAbsoluteX(_seekBar);
        var barY = GetAbsoluteY(_seekBar);
        var barW = Math.Max(_seekBar.Width, 1);
        var thumbX = barX + barW * ratio;

        // Measure after layout; fall back to a stable width estimate.
        var previewW = _seekPreview.Width > 1 ? _seekPreview.Width : 180;
        var previewH = _seekPreview.Height > 1
            ? _seekPreview.Height
            : (NativeSeekThumbnailHelper.ThumbHeight / 2.0) + 56;
        var left = Math.Clamp(thumbX - previewW / 2, 8, Math.Max(8, Width - previewW - 8));
        var top = Math.Max(8, barY - previewH - 12);

        _seekPreview.TranslationX = left;
        _seekPreview.TranslationY = top;
        _seekPreview.Margin = Thickness.Zero;
    }

    private static double GetAbsoluteX(VisualElement element)
    {
        double x = element.X;
        for (var parent = element.Parent as VisualElement; parent is not null; parent = parent.Parent as VisualElement)
            x += parent.X;
        return x;
    }

    private static double GetAbsoluteY(VisualElement element)
    {
        double y = element.Y;
        for (var parent = element.Parent as VisualElement; parent is not null; parent = parent.Parent as VisualElement)
            y += parent.Y;
        return y;
    }

    private void ScheduleSpriteCrop(string url, double time, int spriteIndex)
    {
        // Finger drag cancels a fixed debounce forever (new move before 90ms). Phone uses
        // latest-wins coalesce instead. TV/desktop keep the debounce - DPAD is sparse and
        // that path is already smooth.
        if (IsPhoneOrTablet())
        {
            _phonePendingCrop = (url, time, spriteIndex);
            if (Interlocked.CompareExchange(ref _phoneCropLoopRunning, 1, 0) == 0)
                _ = RunPhoneCropLoopAsync();
            return;
        }

        _cropDebounceCts?.Cancel();
        _cropDebounceCts?.Dispose();
        _cropDebounceCts = new CancellationTokenSource();
        var token = _cropDebounceCts.Token;
        var version = Interlocked.Increment(ref _cropVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TvCropDebounceMs, token).ConfigureAwait(false);
                await EnsureSpriteBitmapAsync(url).ConfigureAwait(false);
                if (token.IsCancellationRequested || version != _cropVersion)
                    return;

                ApplySpriteCrop(time, spriteIndex, version, halfResolution: false);
            }
            catch (OperationCanceledException)
            {
                // Expected while scrubbing.
            }
            catch (Exception ex)
            {
                NativeVideoDebug.Log("SeekThumb fail: " + ex.GetType().Name);
            }
        }, token);
    }

    private async Task RunPhoneCropLoopAsync()
    {
        try
        {
            while (true)
            {
                var pending = _phonePendingCrop;
                if (pending is null)
                    break;
                _phonePendingCrop = null;

                var version = Interlocked.Increment(ref _cropVersion);
                try
                {
                    await EnsureSpriteBitmapAsync(pending.Value.Url).ConfigureAwait(false);
                    if (version != _cropVersion)
                        continue;

                    // Half-res encode: less Glide work under continuous touch moves.
                    ApplySpriteCrop(
                        pending.Value.Time,
                        pending.Value.SpriteIndex,
                        version,
                        halfResolution: true);
                }
                catch (Exception ex)
                {
                    NativeVideoDebug.Log("SeekThumb fail: " + ex.GetType().Name);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _phoneCropLoopRunning, 0);
            // A scrub may have landed after the null check and before we cleared the flag.
            if (_phonePendingCrop is not null
                && Interlocked.CompareExchange(ref _phoneCropLoopRunning, 1, 0) == 0)
                _ = RunPhoneCropLoopAsync();
        }
    }

    private void ApplySpriteCrop(double time, int spriteIndex, int version, bool halfResolution)
    {
        if (version != _cropVersion)
            return;

        SKBitmap? sheet;
        lock (_spriteLock)
            sheet = _spriteBitmap;
        if (sheet is null)
            return;

        var (col, row) = NativeSeekThumbnailHelper.GetSpriteCell(time);
        var srcX = col * NativeSeekThumbnailHelper.ThumbWidth;
        var srcY = row * NativeSeekThumbnailHelper.ThumbHeight;
        if (srcX + NativeSeekThumbnailHelper.ThumbWidth > sheet.Width
            || srcY + NativeSeekThumbnailHelper.ThumbHeight > sheet.Height)
        {
            // Keep the last good frame; only hide when nothing has painted yet.
            if (_seekPreviewImage.Source is null)
                MainThread.BeginInvokeOnMainThread(() => _seekPreviewImageBorder.IsVisible = false);
            return;
        }

        SKBitmap? subset = null;
        lock (_spriteLock)
        {
            if (!ReferenceEquals(_spriteBitmap, sheet) || version != _cropVersion)
                return;

            subset = new SKBitmap(
                NativeSeekThumbnailHelper.ThumbWidth,
                NativeSeekThumbnailHelper.ThumbHeight);
            if (!sheet.ExtractSubset(
                    subset,
                    new SKRectI(
                        srcX,
                        srcY,
                        srcX + NativeSeekThumbnailHelper.ThumbWidth,
                        srcY + NativeSeekThumbnailHelper.ThumbHeight)))
            {
                subset.Dispose();
                if (_seekPreviewImage.Source is null)
                    MainThread.BeginInvokeOnMainThread(() => _seekPreviewImageBorder.IsVisible = false);
                return;
            }
        }

        // Encode outside the sprite lock so TV dispose/warm is not blocked by JPEG work.
        byte[]? bytes = null;
        try
        {
            SKBitmap encodeSource = subset;
            SKBitmap? scaled = null;
            try
            {
                if (halfResolution)
                {
                    scaled = subset.Resize(
                        new SKImageInfo(
                            NativeSeekThumbnailHelper.ThumbWidth / 2,
                            NativeSeekThumbnailHelper.ThumbHeight / 2),
                        new SKSamplingOptions(SKFilterMode.Linear));
                    if (scaled is not null)
                        encodeSource = scaled;
                }

                using var image = SKImage.FromBitmap(encodeSource);
                // Phone: slightly lower quality for faster encode under finger scrub.
                var quality = halfResolution ? 55 : 75;
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                bytes = data.ToArray();
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        finally
        {
            subset.Dispose();
        }

        if (bytes is null || version != _cropVersion)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (version != _cropVersion)
                return;
            _lastSpriteIndex = spriteIndex;
            var copy = bytes;
            _seekPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(copy));
            _seekPreviewImageBorder.IsVisible = true;
        });
    }

    private Task EnsureSpriteBitmapAsync(string url)
    {
        lock (_spriteLock)
        {
            if (_spriteLoadedUrl == url && _spriteBitmap is not null)
                return Task.CompletedTask;
        }

        if (_spriteLoadingUrl == url && _spriteLoadTask is not null)
            return _spriteLoadTask;

        var generation = Interlocked.Increment(ref _spriteLoadGeneration);
        _spriteLoadingUrl = url;
        _spriteLoadTask = LoadSpriteBitmapCoreAsync(url, generation);
        return _spriteLoadTask;
    }

    private async Task LoadSpriteBitmapCoreAsync(string url, int generation)
    {
        HttpClient? client = null;
        var ownsClient = false;
        if (_server?.HttpClient is HttpClient shared)
            client = shared;
        else
        {
            client = new HttpClient();
            ownsClient = true;
        }

        try
        {
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var managed = new SKManagedStream(stream);
            var bitmap = SKBitmap.Decode(managed)
                ?? throw new InvalidOperationException("sprite decode failed");

            // Await so callers of EnsureSpriteBitmapAsync see _spriteBitmap before continuing
            // (BeginInvoke raced scrub crops and left a black HUD until the next settle).
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (generation != _spriteLoadGeneration)
                {
                    bitmap.Dispose();
                    return;
                }

                lock (_spriteLock)
                {
                    DisposeSpriteBitmapUnlocked();
                    _spriteBitmap = bitmap;
                    _spriteLoadedUrl = url;
                }

                _spriteLoadingUrl = null;
                NativeVideoDebug.Log(
                    "SeekThumb sheet loaded w=" + bitmap.Width + " h=" + bitmap.Height);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (generation == _spriteLoadGeneration)
                _spriteLoadingUrl = null;
            NativeVideoDebug.Log("SeekThumb load fail: " + ex.GetType().Name);
            throw;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
            if (generation == _spriteLoadGeneration && _spriteLoadingUrl == url)
                _spriteLoadingUrl = null;
        }
    }

    private void InvalidateSpriteLoad()
    {
        Interlocked.Increment(ref _spriteLoadGeneration);
        _spriteLoadingUrl = null;
        _spriteLoadTask = null;
        _phonePendingCrop = null;
    }

    private void DisposeSpriteBitmap()
    {
        lock (_spriteLock)
            DisposeSpriteBitmapUnlocked();
    }

    private void DisposeSpriteBitmapUnlocked()
    {
        _spriteBitmap?.Dispose();
        _spriteBitmap = null;
        _spriteLoadedUrl = null;
        _lastSpriteIndex = -1;
    }

    private string? ResolveSeekThumbnailsUrl(string? relativeUrl) =>
        _server?.GetAbsoluteUri(relativeUrl)?.AbsoluteUri ?? relativeUrl;

    private void WarmSeekThumbnails()
    {
        // Mirrors Blazor SeekBar.EnsureThumbnailsPreloadedAsync: start the sprite download
        // as soon as ThumbnailsUrl is known, not on the first scrub.
        var url = ResolveSeekThumbnailsUrl(_player.Source?.ThumbnailsUrl);
        if (string.IsNullOrEmpty(url))
        {
            NativeVideoDebug.Log("SeekThumb warm skip (no url)");
            return;
        }

        lock (_spriteLock)
        {
            if (_spriteLoadedUrl == url && _spriteBitmap is not null)
                return;
        }

        NativeVideoDebug.Log("SeekThumb warm start");
        _ = EnsureSpriteBitmapAsync(url);
    }

    private void DisposeSeekPreview()
    {
        _cropDebounceCts?.Cancel();
        _cropDebounceCts?.Dispose();
        _cropDebounceCts = null;
        _phonePendingCrop = null;
        InvalidateSpriteLoad();
        DisposeSpriteBitmap();
        _seekPreviewImage.Source = null;
        _seekPreviewImageBorder.IsVisible = false;
        _seekPreview.IsVisible = false;
        _seekPreview.Opacity = 0;
    }

    private string? GetHoveredChapterTitle(double time)
    {
        var chapters = _seekBar.Chapters;
        string? title = null;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartSeconds <= time)
                title = chapters[i].Title;
            else
                break;
        }

        return title;
    }
}
