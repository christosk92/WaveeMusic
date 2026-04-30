using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Wavee.Core.Video;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Services.SpotifyVideo;

internal sealed class SpotifyWebEmePlayer : IDisposable
{
    public const string PlayerUrl = "https://wavee-player.example/spotify-video-player.html";

    private readonly DispatcherQueue _dispatcher;
    private readonly SpotifyWebEmePlayerDocumentRenderer _documentRenderer;
    private readonly Func<byte[], string?, CancellationToken, Task<byte[]>> _licenseRequester;
    private readonly ILogger? _logger;

    private WebView2? _webView;
    private SpotifyWebEmeVideoManifest? _config;
    private double _startPositionMs;
    private bool _autoPlay = true;
    private CancellationToken _playbackCancellationToken;
    private bool _disposed;

    public SpotifyWebEmePlayer(
        DispatcherQueue dispatcher,
        SpotifyWebEmePlayerDocumentRenderer documentRenderer,
        Func<byte[], string?, CancellationToken, Task<byte[]>> licenseRequester,
        ILogger? logger = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _documentRenderer = documentRenderer ?? throw new ArgumentNullException(nameof(documentRenderer));
        _licenseRequester = licenseRequester ?? throw new ArgumentNullException(nameof(licenseRequester));
        _logger = logger;
    }

    public FrameworkElement? Surface => _webView;
    public long PositionMs { get; private set; }
    public long DurationMs { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsBuffering { get; private set; }
    public bool HasFirstFrame { get; private set; }

    public event EventHandler? SurfaceCreated;
    public event EventHandler<SpotifyWebEmePlayerState>? StateChanged;
    public event EventHandler<string>? FirstFrame;
    public event EventHandler? Ended;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? Log;
    public event EventHandler<string>? AutoplayBlocked;
    public event EventHandler<SpotifyWebEmePlayerRecoveryRequest>? RecoveryRequested;

    public async Task StartAsync(
        SpotifyWebEmeVideoManifest config,
        double startPositionMs,
        CancellationToken cancellationToken,
        bool autoPlay = true)
    {
        if (_disposed) return;

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _startPositionMs = startPositionMs;
        _autoPlay = autoPlay;
        _playbackCancellationToken = cancellationToken;
        PositionMs = Math.Max(0, (long)startPositionMs);
        DurationMs = config.DurationMs;
        IsPlaying = false;
        IsBuffering = true;
        HasFirstFrame = false;

        await _documentRenderer.EnsureLoadedAsync(cancellationToken);
        await RunOnUiAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0),
            };

            _webView = webView;
            SurfaceCreated?.Invoke(this, EventArgs.Empty);
            _logger?.LogDebug("SpotifyWebEmePlayer: surface created xamlRoot={HasXamlRoot}", webView.XamlRoot is not null);

            for (var attempt = 0; attempt < 40 && webView.XamlRoot is null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }

            _logger?.LogDebug("SpotifyWebEmePlayer: initializing xamlRoot={HasXamlRoot}", webView.XamlRoot is not null);
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            webView.CoreWebView2.AddWebResourceRequestedFilter(
                PlayerUrl,
                CoreWebView2WebResourceContext.Document);

            _logger?.LogInformation(
                "SpotifyWebEmePlayer: Core ready browserVersion={BrowserVersion}",
                webView.CoreWebView2.Environment.BrowserVersionString);

            webView.CoreWebView2.Navigate(PlayerUrl);
            _logger?.LogDebug("SpotifyWebEmePlayer: document navigated url={Url}", PlayerUrl);
        });
    }

    public Task PauseAsync()
        => ExecuteScriptAsync("window.__waveePlayer?.pause();");

    public Task PlayAsync()
        => ExecuteScriptAsync("window.__waveePlayer?.play();");

    public Task SeekAsync(long positionMs)
        => ExecuteScriptAsync(
            $"window.__waveePlayer?.seek({Math.Max(0, positionMs).ToString(System.Globalization.CultureInfo.InvariantCulture)});");

    public Task SetVolumeAsync(float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        return ExecuteScriptAsync(
            $"window.__waveePlayer?.setVolume({clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            if (!string.Equals(args.Request.Uri, PlayerUrl, StringComparison.OrdinalIgnoreCase))
                return;

            var html = _documentRenderer.Render(
                _config ?? throw new InvalidOperationException("Web EME config was not available for document request."),
                _startPositionMs,
                _autoPlay);
            args.Response = sender.Environment.CreateWebResourceResponse(
                CreateWebResourceStream(Encoding.UTF8.GetBytes(html)),
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyWebEmePlayer: document response failed");
            args.Response = sender.Environment.CreateWebResourceResponse(
                CreateWebResourceStream(Encoding.UTF8.GetBytes("<!doctype html><title>Wavee video failed</title>")),
                500,
                "Internal Server Error",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProperty)
                ? typeProperty.GetString()
                : null;

            switch (type)
            {
                case "log":
                    Log?.Invoke(this, root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
                    break;

                case "state":
                    UpdateState(root);
                    break;

                case "first-frame":
                    MarkFirstFrame(root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "unknown" : "unknown");
                    break;

                case "ended":
                    Ended?.Invoke(this, EventArgs.Empty);
                    break;

                case "error":
                    Error?.Invoke(this, root.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString() ?? "WebView2 EME playback failed"
                        : "WebView2 EME playback failed");
                    break;

                case "autoplay-blocked":
                    AutoplayBlocked?.Invoke(this, root.TryGetProperty("message", out var autoplayMessage)
                        ? autoplayMessage.GetString() ?? ""
                        : "");
                    break;

                case "recover-restart":
                    RecoveryRequested?.Invoke(
                        this,
                        new SpotifyWebEmePlayerRecoveryRequest(
                            root.TryGetProperty("positionMs", out var recoveryPosition)
                                && recoveryPosition.TryGetInt64(out var positionMs)
                                ? Math.Max(0, positionMs)
                                : PositionMs,
                            root.TryGetProperty("reason", out var recoveryReason)
                                ? recoveryReason.GetString() ?? "stall"
                                : "stall"));
                    break;

                case "license-request":
                    await HandleLicenseRequestAsync(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyWebEmePlayer: message handling failed");
        }
    }

    private void UpdateState(JsonElement root)
    {
        if (root.TryGetProperty("positionMs", out var position) && position.TryGetInt64(out var positionMs))
            PositionMs = positionMs;
        if (root.TryGetProperty("durationMs", out var duration) && duration.TryGetInt64(out var durationMs) && durationMs > 0)
            DurationMs = durationMs;
        if (root.TryGetProperty("isPlaying", out var playing))
            IsPlaying = playing.GetBoolean();
        if (root.TryGetProperty("isBuffering", out var buffering))
            IsBuffering = buffering.GetBoolean();

        StateChanged?.Invoke(this, new SpotifyWebEmePlayerState(PositionMs, DurationMs, IsPlaying, IsBuffering));
    }

    private void MarkFirstFrame(string reason)
    {
        if (HasFirstFrame)
            return;

        HasFirstFrame = true;
        IsBuffering = false;
        FirstFrame?.Invoke(this, reason);
        StateChanged?.Invoke(this, new SpotifyWebEmePlayerState(PositionMs, DurationMs, IsPlaying, IsBuffering));
    }

    private async Task HandleLicenseRequestAsync(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetString() ?? "";
        var challengeBase64 = root.GetProperty("body").GetString() ?? "";
        var challenge = Convert.FromBase64String(challengeBase64);
        _logger?.LogDebug(
            "SpotifyWebEmePlayer: Widevine challenge requestId={RequestId} bytes={Bytes}",
            requestId,
            challenge.Length);

        try
        {
            var license = await _licenseRequester(
                challenge,
                _config?.LicenseServerEndpoint,
                _playbackCancellationToken);

            PostWebMessage(new
            {
                type = "license-response",
                requestId,
                body = Convert.ToBase64String(license)
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyWebEmePlayer: Widevine license request failed requestId={RequestId}", requestId);
            PostWebMessage(new
            {
                type = "license-error",
                requestId,
                message = ex.Message
            });
        }
    }

    private void PostWebMessage(object payload)
    {
        var webView = _webView;
        if (webView?.CoreWebView2 is null)
            return;

        var json = JsonSerializer.Serialize(payload);
        _ = RunOnUiAsync(() => webView.CoreWebView2.PostWebMessageAsJson(json));
    }

    private Task ExecuteScriptAsync(string script)
    {
        var webView = _webView;
        if (webView?.CoreWebView2 is null)
            return Task.CompletedTask;

        return RunOnUiAsync(() => _ = webView.CoreWebView2.ExecuteScriptAsync(script));
    }

    private static InMemoryRandomAccessStream CreateWebResourceStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return stream;
    }

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        if (_dispatcher.HasThreadAccess)
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        }
        else if (!_dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue WebView2 operation on the UI thread."));
        }

        return tcs.Task;
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess)
            return action();

        var tcs = new TaskCompletionSource<bool>();
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue WebView2 operation on the UI thread."));
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var webView = _webView;
        _webView = null;
        _config = null;
        _playbackCancellationToken = default;

        if (webView is null)
            return;

        try
        {
            if (webView.CoreWebView2 is not null)
            {
                webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                _ = webView.CoreWebView2.ExecuteScriptAsync("try { window.__waveePlayer?.pause(); } catch (_) {}");
            }

            webView.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SpotifyWebEmePlayer: cleanup error");
        }
    }
}

internal sealed record SpotifyWebEmePlayerState(
    long PositionMs,
    long DurationMs,
    bool IsPlaying,
    bool IsBuffering);

internal sealed record SpotifyWebEmePlayerRecoveryRequest(
    long PositionMs,
    string Reason);
