#if WINDOWS || LINUX
using System.Text;
using K7.Clients.Shared.Interfaces;
using K7.Shared.Interfaces;
using Microsoft.JSInterop;

namespace K7.Clients.MAUI.Playback;

/// <summary>
/// Fetches authenticated K7 HLS manifests and segments via HttpClient for Video.js VHS on
/// Windows and Linux MAUI: the WebView origin is not in the server CORS list and resource
/// interception is unavailable (WebView2 BlazorWebViewHandler breaks HLS, WebKitGTK has none).
/// </summary>
public sealed class WindowsStreamFetchJsBridge(IK7ServerService serverService, IDeviceStorageService? deviceStorage = null) : IWindowsStreamFetchJsBridge, IDisposable
{
    private DotNetObjectReference<WindowsStreamFetchJsBridge>? _ref;
    private int _tracedFetches;
#if LINUX
    private const int MaxLocalProxies = 4;
    private readonly List<KeyValuePair<string, VlcAuthProxy>> _localProxies = new();
#endif

    public async Task RegisterAsync(IJSRuntime js)
    {
        if (_ref is not null)
            return;

        _ref = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("K7.initWindowsStreamFetchBridge", _ref);
    }

    [JSInvokable]
    public async Task<StreamFetchResponse?> FetchStreamAsync(string url, string? rangeHeader)
    {
        // The first fetches of a session and every failure are traced: VHS runs its HLS
        // requests through this bridge, so a problem here looks like "no request ever reaches
        // the server" from the page and from the server alike.
        var trace = Interlocked.Increment(ref _tracedFetches) <= 12;
        try
        {
            var httpClient = serverService.HttpClient;
            if (httpClient.BaseAddress is not Uri serverBaseUri
                || !Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
            {
                VlcPlayerLog.Warn("stream fetch rejected (no server base or invalid url) " + VlcPlayerLog.SummarizeUrl(url));
                return null;
            }

            // Video.js on Windows must not remux EAC3 into fMP4 - force AAC when the playlist
            // omitted TranscodingAudioCodec (older server decisions / dropped query params).
            var fetchUrl = HlsStreamUrlHelper.EnsureWindowsHlsAudioTranscodeQuery(requestUri.AbsoluteUri);
            if (!Uri.TryCreate(fetchUrl, UriKind.Absolute, out requestUri))
            {
                VlcPlayerLog.Warn("stream fetch rejected (rewritten url) " + VlcPlayerLog.SummarizeUrl(fetchUrl));
                return null;
            }

            if (!requestUri.AbsoluteUri.StartsWith(serverBaseUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                || !HlsStreamUrlHelper.IsK7StreamResource(requestUri.AbsoluteUri))
            {
                VlcPlayerLog.Warn(
                    "stream fetch rejected (outside server or not a stream) " + VlcPlayerLog.SummarizeUrl(requestUri.AbsoluteUri)
                    + " base=" + serverBaseUri.AbsoluteUri);
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrEmpty(rangeHeader))
                request.Headers.TryAddWithoutValidation("Range", rangeHeader);

            using var response = await httpClient.SendAsync(request);
            var bodyBytes = await response.Content.ReadAsByteArrayAsync();
            bodyBytes = PrepareBodyBytes(bodyBytes, requestUri);

            var contentType = response.Content.Headers.ContentType?.MediaType
                ?? GetFallbackContentType(requestUri);

            if (trace || (int)response.StatusCode >= 400)
            {
                VlcPlayerLog.Info(
                    "stream fetch " + VlcPlayerLog.SummarizeUrl(requestUri.AbsoluteUri)
                    + (string.IsNullOrEmpty(rangeHeader) ? "" : " range=" + rangeHeader)
                    + " -> " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " " + contentType
                    + " " + bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B");
            }

            return new StreamFetchResponse((int)response.StatusCode, contentType, bodyBytes);
        }
        catch (Exception ex)
        {
            VlcPlayerLog.Warn("stream fetch " + VlcPlayerLog.SummarizeUrl(url) + " " + ex.GetType().Name + " " + ex.Message);
            throw;
        }
    }

    private static byte[] PrepareBodyBytes(byte[] bodyBytes, Uri requestUri)
    {
        if (bodyBytes.Length == 0)
            return bodyBytes;

        if (!requestUri.AbsoluteUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return bodyBytes;

        var manifestText = Encoding.UTF8.GetString(bodyBytes);
        var rewritten = HlsStreamUrlHelper.AbsolutizeManifestUrls(manifestText, requestUri);

        return Encoding.UTF8.GetBytes(rewritten);
    }

    private static string GetFallbackContentType(Uri requestUri)
    {
        var url = requestUri.AbsoluteUri;
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.apple.mpegurl";

        if (url.Contains(".vtt", StringComparison.OrdinalIgnoreCase))
            return "text/vtt";

        return "application/octet-stream";
    }

#if LINUX
    /// <summary>
    /// GTK: WebKit's GStreamer backend chokes on blob: audio (zero-size parse frames, "Internal
    /// data stream error"), so music streams straight from a loopback proxy that injects the
    /// bearer and forwards Range requests (same <see cref="VlcAuthProxy"/> as LibVLC).
    /// </summary>
    [JSInvokable]
    public Task<string?> GetLocalStreamUrlAsync(string url)
    {
        var httpClient = serverService.HttpClient;
        if (httpClient.BaseAddress is not Uri serverBaseUri
            || !Uri.TryCreate(url, UriKind.Absolute, out var requestUri)
            || !requestUri.AbsoluteUri.StartsWith(serverBaseUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
            || !HlsStreamUrlHelper.IsK7StreamResource(requestUri.AbsoluteUri))
        {
            return Task.FromResult<string?>(null);
        }

        var authorization = ResolveAuthorizationHeader();
        lock (_localProxies)
        {
            var index = _localProxies.FindIndex(p => string.Equals(p.Key, url, StringComparison.Ordinal));
            if (index >= 0 && _localProxies[index].Value.LocalUrl is { } reuse)
            {
                _localProxies[index].Value.SetAuthorization(authorization);
                return Task.FromResult<string?>(reuse);
            }

            while (_localProxies.Count >= MaxLocalProxies)
            {
                _localProxies[0].Value.Dispose();
                _localProxies.RemoveAt(0);
            }

            var proxy = new VlcAuthProxy(authorization) { LogRequests = true };
            if (!proxy.TryStart(url) || proxy.LocalUrl is null)
            {
                proxy.Dispose();
                return Task.FromResult<string?>(null);
            }

            _localProxies.Add(new KeyValuePair<string, VlcAuthProxy>(url, proxy));
            VlcPlayerLog.Info("audio local proxy " + proxy.LocalUrl + " -> " + VlcPlayerLog.SummarizeUrl(url));
            return Task.FromResult<string?>(proxy.LocalUrl);
        }
    }

    private string? ResolveAuthorizationHeader()
    {
        var token = deviceStorage?.Get(K7.Shared.PreferenceKeys.ACCESS_TOKEN);
        if (string.IsNullOrEmpty(token))
            token = serverService.HttpClient.DefaultRequestHeaders.Authorization?.Parameter;

        return string.IsNullOrEmpty(token) ? null : "Bearer " + token;
    }
#endif

    public void Dispose()
    {
#if !LINUX
        // Windows HLS fetches with the signed HttpClient. The storage parameter is the Linux proxy path.
        _ = deviceStorage;
#endif
        _ref?.Dispose();
#if LINUX
        lock (_localProxies)
        {
            foreach (var proxy in _localProxies)
                proxy.Value.Dispose();

            _localProxies.Clear();
        }
#endif
    }

    public sealed class StreamFetchResponse(int statusCode, string contentType, byte[] body)
    {
        public int StatusCode { get; } = statusCode;

        public string ContentType { get; } = contentType;

        public byte[] Body { get; } = body;
    }
}
#endif
