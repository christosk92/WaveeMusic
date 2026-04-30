using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.Audio;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Video;
using Wavee.Playback.Contracts;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Protection;
using Windows.Media.Protection.PlayReady;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Spotify music-video engine. Fetches the v9 DASH manifest, synthesises an
/// in-memory MPD, creates an <see cref="AdaptiveMediaSource"/>, acquires a
/// PlayReady licence from Spotify's licence endpoint, and exposes the
/// underlying <see cref="Windows.Media.Playback.MediaPlayer"/> via
/// <see cref="IVideoSurfaceProvider"/> so the active-surface service can bind
/// it to the video player page or mini-player.
/// </summary>
public sealed class SpotifyVideoProvider
    : ISpotifyVideoPlayback, IVideoSurfaceProvider, IDisposable
{
    private const uint MsprContentEnablingActionRequired = 0x8004B895;
    private const string PlayReadyProtectionSystemId = "{F4637010-03C3-42CD-B932-B48ADF3A6A54}";
    private const string PlayReadyContainerGuid = "{9A04F079-9840-4286-AB92-E65BE0885F95}";
    private const string WebEmePlayerUrl = "https://wavee-player.example/spotify-video-player.html";

    private readonly ISpClient _spClient;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcher;

    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject = new(LocalPlaybackState.Empty);
    private readonly Subject<TrackFinishedMessage> _trackFinishedSubject = new();
    private readonly Subject<PlaybackError> _errorSubject = new();
    private readonly Subject<VideoSurfaceChange> _surfaceChangesSubject = new();

    private readonly DispatcherQueueTimer _positionTimer;
    private const int PositionPublishIntervalMs = 1000;

    private MediaPlayer? _player;
    private WebView2? _webView;
    private AdaptiveMediaSource? _ams;
    private InMemoryRandomAccessStream? _mpdStream;
    private SpotifyVideoManifest? _currentManifest;
    private SpotifyWebEmeVideoConfig? _currentWebEmeConfig;
    private string? _currentWidevineLicenseEndpoint;
    private string? _currentSanitizedMpd;
    private string? _currentTrackUri;
    private TrackMetadataDto? _currentMetadata;
    private long _currentDurationMs;
    private double _currentWebEmeStartPositionMs;
    private long _webPositionMs;
    private bool _webIsPlaying;
    private bool _webIsBuffering;
    private bool _surfaceIsLoading;
    private bool _surfaceHasFirstFrame;
    private bool _disposed;

    // Held as a field so it isn't GC'd while the player is alive.
    private MediaProtectionManager? _mpm;

    public SpotifyVideoProvider(ISpClient spClient, ILogger<SpotifyVideoProvider>? logger = null)
    {
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                          "SpotifyVideoProvider must be constructed on a UI thread.");

        _positionTimer = _dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionPublishIntervalMs);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += (_, _) => PublishStateFromSession();
    }

    // ── ISpotifyVideoPlayback ──────────────────────────────────────────────

    public bool IsActive => _currentTrackUri is not null;

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject.AsObservable();
    public IObservable<TrackFinishedMessage> TrackFinished => _trackFinishedSubject.AsObservable();
    public IObservable<PlaybackError> Errors => _errorSubject.AsObservable();

    // ── IVideoSurfaceProvider ──────────────────────────────────────────────

    MediaPlayer? IVideoSurfaceProvider.Surface => _currentTrackUri is null ? null : _player;
    FrameworkElement? IVideoSurfaceProvider.ElementSurface => _currentTrackUri is null ? null : _webView;
    bool IVideoSurfaceProvider.IsSurfaceLoading => _currentTrackUri is not null && _surfaceIsLoading;
    bool IVideoSurfaceProvider.HasFirstFrame => _currentTrackUri is not null && _surfaceHasFirstFrame;
    string IVideoSurfaceProvider.Kind => "spotify-music-video";
    public IObservable<VideoSurfaceChange> SurfaceChanges => _surfaceChangesSubject.AsObservable();

    public async Task PlayAsync(
        string manifestId,
        string trackUri,
        TrackMetadataDto? metadata,
        long durationMs,
        double startPositionMs,
        CancellationToken ct = default)
    {
        if (_disposed) return;

        await StopAsync(ct);

        _currentTrackUri = trackUri;
        _currentMetadata = metadata;
        _currentDurationMs = Math.Max(0, durationMs);

        _logger?.LogInformation("SpotifyVideoProvider: play {Uri} manifest={Manifest}", trackUri, manifestId);

        try
        {
            var webManifestJson = await _spClient.GetVideoManifestAsync(manifestId, ct);
            await StartWebEmePlaybackAsync(webManifestJson, durationMs, startPositionMs, ct);
            return;

            // 1. Fetch and parse the DASH manifest
            var manifestJson = await _spClient.GetVideoManifestAsync(manifestId, ct);
            var manifest = SpotifyVideoManifest.FromJson(manifestJson);
            _currentManifest = manifest;

            if (manifest.DurationMs > 0)
                _currentDurationMs = manifest.DurationMs;

            var dashVideoProfiles = manifest.GetDashVideoProfilesForDiagnostics();
            var initProtection = await FetchInitSegmentProtectionAsync(manifest, dashVideoProfiles, ct);
            var mpd = manifest.BuildDashMpd(initProtection);
            _currentSanitizedMpd = SanitizeMpdForLog(mpd);
            _logger?.LogDebug("SpotifyVideoProvider: MPD built ({Len} chars), sourceProfiles={SourceProfiles}, dashProfiles={DashProfiles}, drmKid={Kid}, initProtection={InitProtection}, pssh={HasPssh}, pro={HasPro}",
                mpd.Length,
                manifest.VideoProfiles.Count,
                FormatProfileSummary(dashVideoProfiles),
                manifest.DefaultKeyId ?? "<none>",
                FormatInitProtectionSummary(initProtection),
                manifest.PlayReadyPsshBytes is { Length: > 0 },
                manifest.PlayReadyProBytes is { Length: > 0 });

            // 2. Feed MPD to AdaptiveMediaSource
            var mpdBytes = System.Text.Encoding.UTF8.GetBytes(mpd);
            var mpdStream = new InMemoryRandomAccessStream();
            _mpdStream = mpdStream;
            await mpdStream.WriteAsync(mpdBytes.AsBuffer());
            mpdStream.Seek(0);

            var amsResult = await AdaptiveMediaSource.CreateFromStreamAsync(
                mpdStream,
                new Uri("https://video-cf.spotifycdn.com/"),
                "application/dash+xml");

            if (amsResult.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                throw new InvalidOperationException(
                    $"AdaptiveMediaSource creation failed: {amsResult.Status}");
            }

            var ams = amsResult.MediaSource;
            ams.DownloadFailed += OnAdaptiveDownloadFailed;
            ams.Diagnostics.DiagnosticAvailable += OnAdaptiveDiagnosticAvailable;
            _logger?.LogDebug("SpotifyVideoProvider: AMS created availableBitrates={Bitrates}",
                FormatBitrates(ams.AvailableBitrates));
            _ams = ams;

            // 3. Build PlayReady protection manager
            var mpm = BuildProtectionManager(manifest.PlayReadyProBytes);
            _mpm = mpm;

            // 4. Create the MediaPlayer and wire events
            var player = new MediaPlayer
            {
                AutoPlay = false,
                IsLoopingEnabled = false,
                CommandManager = { IsEnabled = false },
            };
            player.ProtectionManager = mpm;

            player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            player.PlaybackSession.NaturalDurationChanged += OnDurationChanged;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            player.MediaOpened += OnMediaOpened;

            player.Source = MediaSource.CreateFromAdaptiveMediaSource(ams);
            _player = player;

            // 5. Seek and play — done in OnMediaOpened to wait for MediaFoundation ready
            _pendingStartPositionMs = startPositionMs;

            // 6. Start position polling and notify surface consumers
            await RunOnUiAsync(() =>
            {
                _positionTimer.Start();
                _surfaceChangesSubject.OnNext(new VideoSurfaceChange(player, "spotify-music-video"));
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyVideoProvider: failed to start playback for {Uri}", trackUri);
            _errorSubject.OnNext(new PlaybackError(PlaybackErrorType.Unknown,
                $"Spotify video failed: {ex.Message}", ex));
            await CleanupAsync();
        }
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_webView is not null) return ExecuteWebScriptAsync("window.__waveePlayer?.pause();");
        if (_player is null) return Task.CompletedTask;
        return RunOnUiAsync(() => _player?.Pause());
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_webView is not null) return ExecuteWebScriptAsync("window.__waveePlayer?.play();");
        if (_player is null) return Task.CompletedTask;
        return RunOnUiAsync(() => _player?.Play());
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_webView is not null)
            return ExecuteWebScriptAsync($"window.__waveePlayer?.seek({Math.Max(0, positionMs).ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        if (_player is null) return Task.CompletedTask;
        return RunOnUiAsync(() =>
        {
            try
            {
                if (_player?.PlaybackSession.CanSeek == true)
                    _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(positionMs);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider seek failed");
            }
        });
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(CleanupPlayer);
    }

    public void SetVolume(float volume)
    {
        if (_disposed) return;
        var clamped = Math.Clamp(volume, 0f, 1f);
        if (_webView is not null)
        {
            _ = ExecuteWebScriptAsync($"window.__waveePlayer?.setVolume({clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
            return;
        }
        if (_player is null) return;
        _ = RunOnUiAsync(() => { if (_player is not null) _player.Volume = clamped; });
    }

    // ── MediaPlayer event plumbing ──────────────────────────────────────

    private double _pendingStartPositionMs;

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        _logger?.LogDebug("SpotifyVideoProvider: MediaOpened (uri={Uri})", _currentTrackUri);
        _ = RunOnUiAsync(() =>
        {
            _surfaceIsLoading = false;
            _surfaceHasFirstFrame = true;
            if (_pendingStartPositionMs > 0 && sender.PlaybackSession.CanSeek)
            {
                try { sender.PlaybackSession.Position = TimeSpan.FromMilliseconds(_pendingStartPositionMs); }
                catch (Exception ex) { _logger?.LogDebug(ex, "SpotifyVideoProvider: initial seek failed"); }
            }
            sender.Play();
            PublishStateFromSession(forcePlaying: true);
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(sender, "spotify-music-video"));
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        => _ = RunOnUiAsync(() => PublishStateFromSession());

    private void OnDurationChanged(MediaPlaybackSession sender, object args)
    {
        var ms = (long)sender.NaturalDuration.TotalMilliseconds;
        if (ms > 0) _currentDurationMs = ms;
        _ = RunOnUiAsync(() => PublishStateFromSession());
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        var uri = _currentTrackUri;
        _logger?.LogInformation("SpotifyVideoProvider: MediaEnded (uri={Uri})", uri);
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            PublishStateFromSession(forceFinished: true);
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "finished",
                });
            }
            CleanupPlayer();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger?.LogError("SpotifyVideoProvider: MediaFailed code={Error} hresult=0x{HResult:X8} message={Message}",
            args.Error, args.ExtendedErrorCode.HResult, args.ErrorMessage);
        if (!string.IsNullOrWhiteSpace(_currentSanitizedMpd))
        {
            _logger?.LogDebug("SpotifyVideoProvider: sanitized MPD at failure:{NewLine}{Mpd}",
                Environment.NewLine,
                _currentSanitizedMpd);
        }

        var uri = _currentTrackUri;
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            _errorSubject.OnNext(new PlaybackError(
                PlaybackErrorType.DecodeError,
                args.ErrorMessage ?? args.Error.ToString()));
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "error",
                });
            }
            CleanupPlayer();
        });
    }

    private void OnAdaptiveDownloadFailed(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadFailedEventArgs args)
    {
        _logger?.LogWarning(
            "SpotifyVideoProvider: AMS download failed type={Type} uri={Uri} hresult=0x{HResult:X8}",
            args.ResourceType,
            SanitizeLogUri(args.ResourceUri),
            args.ExtendedError?.HResult ?? 0);
    }

    private void OnAdaptiveDiagnosticAvailable(AdaptiveMediaSourceDiagnostics sender, AdaptiveMediaSourceDiagnosticAvailableEventArgs args)
    {
        if (args.ExtendedError is null) return;
        _logger?.LogWarning(
            "SpotifyVideoProvider: AMS diagnostic type={Diagnostic} resource={ResourceType} uri={Uri} hresult=0x{HResult:X8}",
            args.DiagnosticType,
            args.ResourceType,
            SanitizeLogUri(args.ResourceUri),
            args.ExtendedError.HResult);
    }

    private void PublishStateFromSession(bool forceFinished = false, bool forcePlaying = false)
    {
        if (_disposed || _currentTrackUri is null) return;

        if (_player is null)
        {
            var webDurationMs = _currentDurationMs;
            var webPlaying = !forceFinished && (forcePlaying || _webIsPlaying);
            var webState = new LocalPlaybackState
            {
                TrackUri = _currentTrackUri,
                TrackTitle = _currentMetadata?.Title,
                TrackArtist = _currentMetadata?.Artist,
                TrackAlbum = _currentMetadata?.Album,
                AlbumUri = _currentMetadata?.AlbumUri,
                ArtistUri = _currentMetadata?.ArtistUri,
                ImageUrl = _currentMetadata?.ImageUrl,
                ImageLargeUrl = _currentMetadata?.ImageLargeUrl,
                ImageSmallUrl = _currentMetadata?.ImageSmallUrl,
                ImageXLargeUrl = _currentMetadata?.ImageXLargeUrl,
                DurationMs = webDurationMs,
                PositionMs = _webPositionMs,
                IsPlaying = webPlaying,
                IsPaused = !forceFinished && !webPlaying && !_webIsBuffering,
                IsBuffering = !forceFinished && _webIsBuffering,
                CanSeek = true,
                Volume = 65535,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _stateSubject.OnNext(webState);
            return;
        }

        var session = _player.PlaybackSession;
        var positionMs = (long)session.Position.TotalMilliseconds;
        var durationMs = _currentDurationMs > 0
            ? _currentDurationMs
            : (long)session.NaturalDuration.TotalMilliseconds;

        bool isPlaying = !forceFinished && (forcePlaying || session.PlaybackState == MediaPlaybackState.Playing);
        bool isPaused = !forceFinished && !forcePlaying && session.PlaybackState == MediaPlaybackState.Paused;
        bool isBuffering = !forceFinished && !forcePlaying
            && (session.PlaybackState == MediaPlaybackState.Opening
                || session.PlaybackState == MediaPlaybackState.Buffering);

        var state = new LocalPlaybackState
        {
            TrackUri = _currentTrackUri,
            TrackTitle = _currentMetadata?.Title,
            TrackArtist = _currentMetadata?.Artist,
            TrackAlbum = _currentMetadata?.Album,
            AlbumUri = _currentMetadata?.AlbumUri,
            ArtistUri = _currentMetadata?.ArtistUri,
            ImageUrl = _currentMetadata?.ImageUrl,
            ImageLargeUrl = _currentMetadata?.ImageLargeUrl,
            ImageSmallUrl = _currentMetadata?.ImageSmallUrl,
            ImageXLargeUrl = _currentMetadata?.ImageXLargeUrl,
            DurationMs = durationMs,
            PositionMs = positionMs,
            IsPlaying = isPlaying,
            IsPaused = isPaused,
            IsBuffering = isBuffering,
            CanSeek = session.CanSeek,
            Volume = (uint)Math.Round(_player.Volume * 65535d),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _stateSubject.OnNext(state);
    }

    private static string FormatProfileSummary(System.Collections.Generic.IReadOnlyList<VideoProfile> profiles)
    {
        if (profiles.Count == 0) return "<none>";

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < profiles.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var profile = profiles[i];
            sb.Append(profile.Id)
              .Append(':')
              .Append(profile.Width)
              .Append('x')
              .Append(profile.Height)
              .Append('/')
              .Append(profile.VideoCodec);
        }

        return sb.ToString();
    }

    private async Task StartWebEmePlaybackAsync(
        string manifestJson,
        long durationMs,
        double startPositionMs,
        CancellationToken cancellationToken)
    {
        SpotifyWebEmeVideoConfig config;
        try
        {
            config = SpotifyWebEmeVideoConfig.FromJson(manifestJson);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "SpotifyVideoProvider: WebView2 EME manifest parse failed. diagnostics={Diagnostics}",
                SpotifyWebEmeVideoConfig.DescribeManifestForLog(manifestJson));
            throw;
        }

        _currentDurationMs = durationMs > 0 ? durationMs : config.DurationMs;
        _currentWebEmeConfig = config;
        _currentWebEmeStartPositionMs = startPositionMs;
        _currentWidevineLicenseEndpoint = config.LicenseServerEndpoint;
        _webPositionMs = Math.Max(0, (long)startPositionMs);
        _webIsPlaying = false;
        _webIsBuffering = true;
        _surfaceIsLoading = true;
        _surfaceHasFirstFrame = false;

        _logger?.LogInformation(
            "SpotifyVideoProvider: WebView2 EME manifest parsed video={VideoProfile} audio={AudioProfile} durationMs={DurationMs} segmentLength={SegmentLength}s segments={SegmentCount} license={LicenseEndpoint}",
            config.VideoProfileId,
            config.AudioProfileId,
            config.DurationMs,
            config.SegmentLength,
            config.SegmentTimes.Count,
            config.LicenseServerEndpoint ?? "<none>");

        WebView2? createdWebView = null;
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
            createdWebView = webView;
            _positionTimer.Start();
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));
            _logger?.LogDebug("SpotifyVideoProvider: WebView2 EME surface published xamlRoot={HasXamlRoot}", webView.XamlRoot is not null);

            for (var attempt = 0; attempt < 40 && webView.XamlRoot is null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }

            _logger?.LogDebug("SpotifyVideoProvider: WebView2 EME initializing xamlRoot={HasXamlRoot}", webView.XamlRoot is not null);
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.CoreWebView2.WebResourceRequested += OnWebEmeWebResourceRequested;
            webView.CoreWebView2.AddWebResourceRequestedFilter(
                WebEmePlayerUrl,
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document);
            _logger?.LogInformation(
                "SpotifyVideoProvider: WebView2 EME Core ready browserVersion={BrowserVersion}",
                webView.CoreWebView2.Environment.BrowserVersionString);

            webView.CoreWebView2.Navigate(WebEmePlayerUrl);
            _logger?.LogDebug("SpotifyVideoProvider: WebView2 EME document navigated url={Url}", WebEmePlayerUrl);
        });

        if (!ReferenceEquals(_webView, createdWebView))
            _logger?.LogDebug("SpotifyVideoProvider: WebView2 EME startup completed after provider state changed");
    }

    private void OnWebEmeWebResourceRequested(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            if (!string.Equals(args.Request.Uri, WebEmePlayerUrl, StringComparison.OrdinalIgnoreCase))
                return;

            var html = BuildWebEmePlayerHtml(_currentWebEmeConfig
                ?? throw new InvalidOperationException("WebView2 EME config was not available for document request."),
                _currentWebEmeStartPositionMs);
            args.Response = sender.Environment.CreateWebResourceResponse(
                CreateWebResourceStream(System.Text.Encoding.UTF8.GetBytes(html)),
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyVideoProvider: WebView2 EME document response failed");
            args.Response = sender.Environment.CreateWebResourceResponse(
                CreateWebResourceStream(System.Text.Encoding.UTF8.GetBytes("<!doctype html><title>Wavee video failed</title>")),
                500,
                "Internal Server Error",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
    }

    private static InMemoryRandomAccessStream CreateWebResourceStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return stream;
    }

    private async void OnWebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
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
                    _logger?.LogDebug("SpotifyVideoProvider WebView2: {Message}",
                        root.TryGetProperty("message", out var message) ? message.GetString() : "");
                    break;

                case "state":
                    UpdateWebState(root);
                    break;

                case "first-frame":
                    MarkFirstFrame(root.TryGetProperty("reason", out var reason) ? reason.GetString() : "unknown");
                    break;

                case "ended":
                    _logger?.LogInformation("SpotifyVideoProvider: WebView2 video ended");
                    OnWebEnded();
                    break;

                case "error":
                    var error = root.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : "WebView2 EME playback failed";
                    _logger?.LogError("SpotifyVideoProvider: WebView2 error {Error}", error);
                    OnWebError(error ?? "WebView2 EME playback failed");
                    break;

                case "autoplay-blocked":
                    _logger?.LogWarning("SpotifyVideoProvider: WebView2 autoplay blocked {Message}",
                        root.TryGetProperty("message", out var autoplayMessage) ? autoplayMessage.GetString() : "");
                    break;

                case "license-request":
                    await HandleWidevineLicenseRequestAsync(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyVideoProvider: WebView2 message handling failed");
        }
    }

    private void UpdateWebState(JsonElement root)
    {
        if (root.TryGetProperty("positionMs", out var position) && position.TryGetInt64(out var positionMs))
            _webPositionMs = positionMs;
        if (root.TryGetProperty("durationMs", out var duration) && duration.TryGetInt64(out var durationMs) && durationMs > 0)
            _currentDurationMs = durationMs;
        if (root.TryGetProperty("isPlaying", out var playing))
            _webIsPlaying = playing.GetBoolean();
        if (root.TryGetProperty("isBuffering", out var buffering))
            _webIsBuffering = buffering.GetBoolean();

        if (!_surfaceHasFirstFrame && _webIsPlaying)
            MarkFirstFrame("state-playing");

        PublishStateFromSession();
    }

    private void MarkFirstFrame(string? reason)
    {
        if (_surfaceHasFirstFrame)
            return;

        _surfaceHasFirstFrame = true;
        _surfaceIsLoading = false;
        _webIsBuffering = false;
        _logger?.LogInformation("SpotifyVideoProvider: WebView2 first frame reason={Reason}", reason ?? "<unknown>");
        PublishStateFromSession();
        _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));
    }

    private async Task HandleWidevineLicenseRequestAsync(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetString() ?? "";
        var challengeBase64 = root.GetProperty("body").GetString() ?? "";
        var challenge = Convert.FromBase64String(challengeBase64);
        _logger?.LogDebug("SpotifyVideoProvider: Widevine challenge from WebView2 requestId={RequestId} bytes={Bytes}",
            requestId,
            challenge.Length);

        try
        {
            var license = await _spClient.PostWidevineLicenseAsync(
                challenge,
                _currentWidevineLicenseEndpoint);

            PostWebMessage(new
            {
                type = "license-response",
                requestId,
                body = Convert.ToBase64String(license)
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SpotifyVideoProvider: Widevine license request failed requestId={RequestId}", requestId);
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

    private Task ExecuteWebScriptAsync(string script)
    {
        var webView = _webView;
        if (webView?.CoreWebView2 is null)
            return Task.CompletedTask;

        return RunOnUiAsync(() => _ = webView.CoreWebView2.ExecuteScriptAsync(script));
    }

    private void OnWebEnded()
    {
        var uri = _currentTrackUri;
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            PublishStateFromSession(forceFinished: true);
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "finished",
                });
            }
            CleanupPlayer();
        });
    }

    private void OnWebError(string message)
    {
        var uri = _currentTrackUri;
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            _errorSubject.OnNext(new PlaybackError(
                PlaybackErrorType.DecodeError,
                message));
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "error",
                });
            }
            CleanupPlayer();
        });
    }

    // ── PlayReady setup ────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<int, Mp4InitSegmentProtectionData>> FetchInitSegmentProtectionAsync(
        SpotifyVideoManifest manifest,
        System.Collections.Generic.IReadOnlyList<VideoProfile> videoProfiles,
        CancellationToken cancellationToken)
    {
        var protectionByProfile = new Dictionary<int, Mp4InitSegmentProtectionData>();

        foreach (var profile in videoProfiles)
        {
            await FetchInitSegmentProtectionForProfileAsync(
                manifest,
                profile.Id,
                "video",
                protectionByProfile,
                cancellationToken);
        }

        if (manifest.AudioProfile is not null)
        {
            await FetchInitSegmentProtectionForProfileAsync(
                manifest,
                manifest.AudioProfile.Id,
                "audio",
                protectionByProfile,
                cancellationToken);
        }

        return protectionByProfile;
    }

    private async Task FetchInitSegmentProtectionForProfileAsync(
        SpotifyVideoManifest manifest,
        int profileId,
        string kind,
        Dictionary<int, Mp4InitSegmentProtectionData> protectionByProfile,
        CancellationToken cancellationToken)
    {
        Uri initUri;
        try
        {
            initUri = manifest.BuildInitializationUri(profileId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "SpotifyVideoProvider: failed to build init URI kind={Kind} profile={Profile}",
                kind,
                profileId);
            return;
        }

        try
        {
            var bytes = await _spClient.GetVideoSegmentBytesAsync(initUri, cancellationToken);
            var protection = Mp4InitSegmentProtectionParser.Parse(bytes);
            if (protection.HasAnySignal)
                protectionByProfile[profileId] = protection;

            _logger?.LogDebug(
                "SpotifyVideoProvider: init parsed kind={Kind} profile={Profile} bytes={Bytes} tenc={TencCount} pssh={PsshCount} kid={Kid} prKid={PlayReadyKid} iv={IvSize} protected={IsProtected} prPssh={HasPssh} prPro={HasPro}",
                kind,
                profileId,
                bytes.Length,
                protection.TencBoxCount,
                protection.PsshBoxCount,
                protection.DefaultKeyId ?? "<none>",
                protection.DefaultPlayReadyKeyId ?? "<none>",
                protection.DefaultPerSampleIvSize?.ToString() ?? "<none>",
                protection.IsProtected?.ToString() ?? "<none>",
                protection.PlayReadyPsshBytes is { Length: > 0 },
                protection.PlayReadyProBytes is { Length: > 0 });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "SpotifyVideoProvider: init parse failed kind={Kind} profile={Profile} uri={Uri}; falling back to manifest DRM data",
                kind,
                profileId,
                SanitizeLogUri(initUri));
        }
    }

    private static string FormatInitProtectionSummary(
        IReadOnlyDictionary<int, Mp4InitSegmentProtectionData> protectionByProfile)
    {
        if (protectionByProfile.Count == 0)
            return "<none>";

        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var item in protectionByProfile)
        {
            if (!first)
                sb.Append(", ");
            first = false;

            var protection = item.Value;
            sb.Append(item.Key)
              .Append(':')
              .Append(protection.DefaultKeyId ?? "<no-kid>")
              .Append("/iv=")
              .Append(protection.DefaultPerSampleIvSize?.ToString() ?? "<none>")
              .Append("/pssh=")
              .Append(protection.PlayReadyPsshBytes is { Length: > 0 });
        }

        return sb.ToString();
    }

    private static string SanitizeLogUri(Uri? uri)
    {
        if (uri is null)
            return "<none>";

        var text = uri.ToString();
        var queryIndex = text.IndexOf('?');
        return queryIndex >= 0 ? text[..queryIndex] : text;
    }

    private static string FormatBitrates(System.Collections.Generic.IReadOnlyList<uint> bitrates)
    {
        if (bitrates.Count == 0)
            return "<none>";

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < bitrates.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(bitrates[i]);
        }

        return sb.ToString();
    }

    private static string SanitizeMpdForLog(string mpd)
    {
        var sb = new System.Text.StringBuilder(mpd.Length);
        var insideQuotedUrl = false;
        for (var i = 0; i < mpd.Length; i++)
        {
            if (StartsWithAt(mpd, i, "https://") || StartsWithAt(mpd, i, "http://"))
            {
                insideQuotedUrl = true;
            }

            if (insideQuotedUrl && mpd[i] == '?')
            {
                while (i < mpd.Length && mpd[i] != '"')
                    i++;

                insideQuotedUrl = false;
                if (i >= mpd.Length)
                    break;
            }

            sb.Append(mpd[i]);
            if (insideQuotedUrl && mpd[i] == '"')
                insideQuotedUrl = false;
        }

        return sb.ToString();
    }

    private static bool StartsWithAt(string value, int index, string prefix)
        => index + prefix.Length <= value.Length
           && string.Compare(value, index, prefix, 0, prefix.Length, StringComparison.Ordinal) == 0;

    private static string BuildWebEmePlayerHtml(SpotifyWebEmeVideoConfig config, double startPositionMs)
    {
        var configJson = JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        var startSeconds = (Math.Max(0, startPositionMs) / 1000d)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self' https: data: blob: 'unsafe-inline'; media-src blob: https:; connect-src https:;">
  <style>
    html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; background: #000; }
    video { width: 100%; height: 100%; background: #000; object-fit: contain; }
  </style>
</head>
<body>
  <video id="video" autoplay playsinline muted></video>
  <script>
    const config = {{configJson}};
    const startSeconds = {{startSeconds}};
    const video = document.getElementById('video');
    const pendingLicenses = new Map();
    let mediaSource;
    let sourceOpen = false;
    let fatal = false;
    let unmuteScheduled = false;

    function host(type, payload = {}) {
      chrome.webview.postMessage({ type, ...payload });
    }

    function log(message) {
      host('log', { message: String(message) });
    }

    function fail(error) {
      if (fatal) return;
      fatal = true;
      const message = error && error.stack ? error.stack : String(error);
      host('error', { message });
    }

    function bytesToBase64(bytes) {
      let binary = '';
      const chunk = 0x8000;
      for (let i = 0; i < bytes.length; i += chunk) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
      }
      return btoa(binary);
    }

    function base64ToBytes(text) {
      const binary = atob(text);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      return bytes;
    }

    chrome.webview.addEventListener('message', async event => {
      const msg = event.data || {};
      if (msg.type === 'license-response') {
        const pending = pendingLicenses.get(msg.requestId);
        if (!pending) return;
        pendingLicenses.delete(msg.requestId);
        pending.resolve(base64ToBytes(msg.body));
      } else if (msg.type === 'license-error') {
        const pending = pendingLicenses.get(msg.requestId);
        if (!pending) return;
        pendingLicenses.delete(msg.requestId);
        pending.reject(new Error(msg.message || 'License request failed'));
      }
    });

    async function requestLicense(message) {
      const requestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      const body = bytesToBase64(new Uint8Array(message));
      const promise = new Promise((resolve, reject) => pendingLicenses.set(requestId, { resolve, reject }));
      host('license-request', { requestId, body });
      return promise;
    }

    async function configureEme() {
      if (!navigator.requestMediaKeySystemAccess) {
        throw new Error('EME is not available in this WebView2 runtime');
      }

      const keySystem = 'com.widevine.alpha';
      const mediaConfig = [{
        initDataTypes: ['webm', 'cenc'],
        audioCapabilities: [{ contentType: config.audio.contentType }],
        videoCapabilities: [{ contentType: config.video.contentType }],
        distinctiveIdentifier: 'optional',
        persistentState: 'optional',
        sessionTypes: ['temporary']
      }];

      log(`requestMediaKeySystemAccess ${keySystem}`);
      const access = await navigator.requestMediaKeySystemAccess(keySystem, mediaConfig);
      const mediaKeys = await access.createMediaKeys();
      await video.setMediaKeys(mediaKeys);

      video.addEventListener('encrypted', async e => {
        try {
          log(`encrypted initDataType=${e.initDataType} bytes=${e.initData ? e.initData.byteLength : 0}`);
          const session = mediaKeys.createSession();
          session.addEventListener('message', async ev => {
            try {
              log(`license message bytes=${ev.message.byteLength}`);
              const license = await requestLicense(ev.message);
              log(`license response bytes=${license.byteLength}`);
              await session.update(license);
              log('license update complete');
            } catch (err) {
              fail(err);
            }
          });
          await session.generateRequest(e.initDataType, e.initData);
        } catch (err) {
          fail(err);
        }
      });
    }

    async function fetchBytes(url) {
      const response = await fetch(url, { mode: 'cors', credentials: 'omit', cache: 'no-store' });
      if (!response.ok) throw new Error(`fetch ${response.status} ${url}`);
      return new Uint8Array(await response.arrayBuffer());
    }

    function appendBuffer(sourceBuffer, bytes) {
      return new Promise((resolve, reject) => {
        const onError = () => {
          cleanup();
          reject(new Error('SourceBuffer append error'));
        };
        const onUpdateEnd = () => {
          cleanup();
          resolve();
        };
        const cleanup = () => {
          sourceBuffer.removeEventListener('error', onError);
          sourceBuffer.removeEventListener('updateend', onUpdateEnd);
        };
        sourceBuffer.addEventListener('error', onError, { once: true });
        sourceBuffer.addEventListener('updateend', onUpdateEnd, { once: true });
        sourceBuffer.appendBuffer(bytes);
      });
    }

    function removeBuffer(sourceBuffer, start, end) {
      return new Promise((resolve, reject) => {
        if (end <= start || sourceBuffer.updating) {
          resolve();
          return;
        }
        const onError = () => {
          cleanup();
          reject(new Error('SourceBuffer remove error'));
        };
        const onUpdateEnd = () => {
          cleanup();
          resolve();
        };
        const cleanup = () => {
          sourceBuffer.removeEventListener('error', onError);
          sourceBuffer.removeEventListener('updateend', onUpdateEnd);
        };
        sourceBuffer.addEventListener('error', onError, { once: true });
        sourceBuffer.addEventListener('updateend', onUpdateEnd, { once: true });
        sourceBuffer.remove(start, end);
      });
    }

    function sleep(ms) {
      return new Promise(resolve => setTimeout(resolve, ms));
    }

    function bufferedEnd(sourceBuffer) {
      const ranges = sourceBuffer.buffered;
      if (!ranges || ranges.length === 0) return 0;
      const t = video.currentTime || startSeconds || 0;
      for (let i = 0; i < ranges.length; i++) {
        if (ranges.start(i) <= t && ranges.end(i) >= t) return ranges.end(i);
      }
      return ranges.end(ranges.length - 1);
    }

    async function pruneBehind(sourceBuffer, label) {
      const ranges = sourceBuffer.buffered;
      if (!ranges || ranges.length === 0) return;
      const removeEnd = Math.max(0, (video.currentTime || 0) - 25);
      if (removeEnd <= 0) return;

      for (let i = 0; i < ranges.length; i++) {
        const start = ranges.start(i);
        const end = Math.min(ranges.end(i), removeEnd);
        if (end > start + 1) {
          log(`${label} prune ${start.toFixed(1)}-${end.toFixed(1)}`);
          await removeBuffer(sourceBuffer, start, end);
          return;
        }
      }
    }

    async function appendBufferWithQuotaRetry(sourceBuffer, bytes, label) {
      try {
        await appendBuffer(sourceBuffer, bytes);
      } catch (err) {
        if (!(err && err.name === 'QuotaExceededError') && !String(err).includes('QuotaExceededError')) throw err;
        log(`${label} quota reached; pruning and retrying`);
        await pruneBehind(sourceBuffer, label);
        await sleep(100);
        await appendBuffer(sourceBuffer, bytes);
      }
    }

    async function appendTrackRange(sourceBuffer, initUrl, segmentUrls, label, startIndex, endIndex, includeInit) {
      if (includeInit) {
        log(`${label} init fetch`);
        await appendBuffer(sourceBuffer, await fetchBytes(initUrl));
      }

      for (let i = startIndex; i < endIndex; i++) {
        if (fatal) return;
        await appendBuffer(sourceBuffer, await fetchBytes(segmentUrls[i]));
        if (i === startIndex || i % 8 === 0) log(`${label} appended ${i + 1}/${segmentUrls.length}`);
      }
    }

    async function appendTrackWindow(sourceBuffer, segmentUrls, label, startIndex) {
      const maxAheadSeconds = 35;
      let i = startIndex;
      while (!fatal && i < segmentUrls.length) {
        const ahead = bufferedEnd(sourceBuffer) - (video.currentTime || 0);
        if (ahead > maxAheadSeconds) {
          await pruneBehind(sourceBuffer, label);
          await sleep(350);
          continue;
        }

        await pruneBehind(sourceBuffer, label);
        await appendBufferWithQuotaRetry(sourceBuffer, await fetchBytes(segmentUrls[i]), label);
        if (i === startIndex || i % 8 === 0) log(`${label} appended ${i + 1}/${segmentUrls.length}`);
        i++;
      }
      log(`${label} append loop complete`);
    }

    let firstFrameSent = false;
    function postFirstFrame(reason) {
      if (firstFrameSent) return;
      firstFrameSent = true;
      log(`first frame ${reason}`);
      host('first-frame', { reason });
      postState(false);
    }

    function armFirstFrameSignal() {
      if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) {
        video.requestVideoFrameCallback(() => postFirstFrame('requestVideoFrameCallback'));
      }
    }

    function postState(isBuffering = false) {
      host('state', {
        positionMs: Math.round(video.currentTime * 1000),
        durationMs: Number.isFinite(video.duration) ? Math.round(video.duration * 1000) : config.durationMs,
        isPlaying: !video.paused && !video.ended,
        isBuffering
      });
    }

    function scheduleUnmute() {
      if (unmuteScheduled || !video.muted) return;
      unmuteScheduled = true;
      setTimeout(() => {
        video.muted = false;
        video.defaultMuted = false;
        log('unmuted after autoplay start');
        postState(false);
      }, 250);
    }

    async function playVideo(source) {
      try {
        await video.play();
        scheduleUnmute();
        postState(false);
        return true;
      } catch (err) {
        if (err && err.name === 'NotAllowedError') {
          log(`play blocked on ${source}; retrying muted`);
          video.defaultMuted = true;
          video.muted = true;
          try {
            await video.play();
            scheduleUnmute();
            postState(false);
            return true;
          } catch (mutedErr) {
            host('autoplay-blocked', { message: mutedErr && mutedErr.message ? mutedErr.message : String(mutedErr) });
            postState(false);
            return false;
          }
        }
        throw err;
      }
    }

    async function startMse() {
      mediaSource = new MediaSource();
      video.src = URL.createObjectURL(mediaSource);
      await new Promise(resolve => mediaSource.addEventListener('sourceopen', resolve, { once: true }));
      sourceOpen = true;
      log(`MediaSource open video=${config.video.contentType} audio=${config.audio.contentType}`);
      mediaSource.duration = Math.max(1, config.durationMs / 1000);

      const videoBuffer = mediaSource.addSourceBuffer(config.video.contentType);
      const audioBuffer = mediaSource.addSourceBuffer(config.audio.contentType);
      videoBuffer.mode = 'segments';
      audioBuffer.mode = 'segments';

      const startIndex = Math.max(0, Math.min(config.segmentTimes.length - 1, Math.floor(startSeconds / config.segmentLength)));
      const startupEnd = Math.min(config.segmentTimes.length, startIndex + 4);
      log(`startup append range ${startIndex + 1}-${startupEnd}/${config.segmentTimes.length}`);
      await Promise.all([
        appendTrackRange(videoBuffer, config.video.initUrl, config.video.segmentUrls, 'video', startIndex, startupEnd, true),
        appendTrackRange(audioBuffer, config.audio.initUrl, config.audio.segmentUrls, 'audio', startIndex, startupEnd, true)
      ]);

      if (startSeconds > 0) video.currentTime = startSeconds;
      armFirstFrameSignal();
      await playVideo('startup');

      Promise.all([
        appendTrackWindow(videoBuffer, config.video.segmentUrls, 'video', startupEnd),
        appendTrackWindow(audioBuffer, config.audio.segmentUrls, 'audio', startupEnd)
      ]).then(() => {
        log('background append complete');
        if (mediaSource.readyState === 'open') mediaSource.endOfStream();
      }).catch(fail);
    }

    video.addEventListener('timeupdate', () => postState(false));
    video.addEventListener('durationchange', () => postState(false));
    video.addEventListener('loadeddata', () => postFirstFrame('loadeddata'));
    video.addEventListener('canplay', () => postFirstFrame('canplay'));
    video.addEventListener('playing', () => { postFirstFrame('playing'); postState(false); });
    video.addEventListener('pause', () => postState(false));
    video.addEventListener('waiting', () => postState(true));
    video.addEventListener('ended', () => host('ended'));
    video.addEventListener('error', () => fail(video.error ? `media error code=${video.error.code} message=${video.error.message}` : 'media error'));

    window.__waveePlayer = {
      play: () => playVideo('host-play').catch(fail),
      pause: () => video.pause(),
      seek: ms => { video.currentTime = Math.max(0, ms / 1000); postState(false); },
      setVolume: value => { video.volume = Math.max(0, Math.min(1, value)); }
    };

    (async () => {
      try {
        await configureEme();
        await startMse();
      } catch (err) {
        fail(err);
      }
    })();
  </script>
</body>
</html>
""";
    }

    private MediaProtectionManager BuildProtectionManager(byte[]? proBytes)
    {
        var mpm = new MediaProtectionManager();

        var cpsystems = new Windows.Foundation.Collections.PropertySet();
        cpsystems.Add(
            PlayReadyProtectionSystemId,
            "Windows.Media.Protection.PlayReady.PlayReadyWinRTTrustedInput");

        mpm.Properties.Add("Windows.Media.Protection.MediaProtectionSystemIdMapping", cpsystems);
        mpm.Properties.Add("Windows.Media.Protection.MediaProtectionSystemId",
            PlayReadyProtectionSystemId);
        mpm.Properties.Add("Windows.Media.Protection.MediaProtectionContainerGuid",
            PlayReadyContainerGuid);
        mpm.Properties.Add("Windows.Media.Protection.UseSoftwareProtectionLayer", true);

        bool? hardwareDrmSupported = null;
        try
        {
            hardwareDrmSupported = PlayReadyStatics.CheckSupportedHardware(
                PlayReadyHardwareDRMFeatures.HardwareDRM);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SpotifyVideoProvider: PlayReady hardware DRM support check failed");
        }

        _logger?.LogDebug(
            "SpotifyVideoProvider: PlayReady protection manager configured systemId={SystemId} containerGuid={ContainerGuid} softwareLayer=true hardwareDrmSupported={HardwareDrmSupported} processArch={ProcessArch} osArch={OsArch}",
            PlayReadyProtectionSystemId,
            PlayReadyContainerGuid,
            hardwareDrmSupported?.ToString() ?? "<unknown>",
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture);

        mpm.ComponentLoadFailed += OnComponentLoadFailed;
        mpm.ServiceRequested += OnServiceRequested;
        return mpm;
    }

    private void OnComponentLoadFailed(MediaProtectionManager sender, ComponentLoadFailedEventArgs args)
    {
        _logger?.LogError("SpotifyVideoProvider: PlayReady component load failed");
        args.Completion.Complete(false);
    }

    private async void OnServiceRequested(MediaProtectionManager sender, ServiceRequestedEventArgs args)
    {
        if (args.Request is PlayReadyLicenseAcquisitionServiceRequest licReq)
        {
            var completion = args.Completion;
            bool success = false;
            try
            {
                // GetMessageBody() returns byte[] in the C#/WinRT projection.
                var soap = licReq.GenerateManualEnablingChallenge();
                var bodyBytes = soap.GetMessageBody();
                var soapHeaders = ExtractSoapHeaders(soap);

                _logger?.LogDebug("SpotifyVideoProvider: fetching PlayReady licence ({Bytes} bytes, endpoint={Endpoint}, soapHeaders={HeaderCount})",
                    bodyBytes.Length,
                    _currentManifest?.PlayReadyLicenseServerEndpoint ?? "<default>",
                    soapHeaders.Count);
                var responseBytes = await _spClient.PostPlayReadyLicenseAsync(
                    bodyBytes,
                    _currentManifest?.PlayReadyLicenseServerEndpoint,
                    soapHeaders);
                _logger?.LogDebug("SpotifyVideoProvider: PlayReady licence response received ({Bytes} bytes)",
                    responseBytes.Length);

                // ProcessManualEnablingResponse also takes byte[] in the C#/WinRT projection.
                var ex = licReq.ProcessManualEnablingResponse(responseBytes);
                if (ex is not null)
                    _logger?.LogError(ex, "SpotifyVideoProvider: PlayReady ProcessManualEnablingResponse failed");
                else
                {
                    _logger?.LogDebug("SpotifyVideoProvider: PlayReady licence acquired");
                    success = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SpotifyVideoProvider: PlayReady service request failed");
            }
            completion.Complete(success);
            return;
        }

        // Individualisation — automatic path (PlayReady handles the server call).
        if (args.Request is PlayReadyIndividualizationServiceRequest indivReq)
        {
            var completion = args.Completion;
            bool ok = false;
            try
            {
                await indivReq.BeginServiceRequest();
                ok = true;
            }
            catch (Exception ex) when (unchecked((uint)ex.HResult) == MsprContentEnablingActionRequired)
            {
                try
                {
                    await indivReq.NextServiceRequest().BeginServiceRequest();
                    ok = true;
                }
                catch (Exception nextEx)
                {
                    _logger?.LogDebug(nextEx, "SpotifyVideoProvider: PlayReady follow-up individualisation failed");
                }
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "SpotifyVideoProvider: individualisation failed"); }
            completion.Complete(ok);
        }
    }

    // ── Cleanup ────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> ExtractSoapHeaders(PlayReadySoapMessage soap)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in soap.MessageHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Key) || header.Value is null)
                continue;

            var value = header.Value as string ?? header.Value.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                headers[header.Key] = value;
        }

        return headers;
    }

    private void CleanupPlayer()
    {
        _positionTimer.Stop();
        var player = _player;
        var webView = _webView;
        var ams = _ams;
        var mpm = _mpm;
        var mpdStream = _mpdStream;
        _player = null;
        _webView = null;
        _ams = null;
        _mpm = null;
        _mpdStream = null;
        _currentManifest = null;
        _currentWebEmeConfig = null;
        _currentWidevineLicenseEndpoint = null;
        _currentSanitizedMpd = null;
        _currentTrackUri = null;
        _currentMetadata = null;
        _currentDurationMs = 0;
        _currentWebEmeStartPositionMs = 0;
        _webPositionMs = 0;
        _webIsPlaying = false;
        _webIsBuffering = false;
        _surfaceIsLoading = false;
        _surfaceHasFirstFrame = false;

        if (player is not null)
        {
            try
            {
                player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                player.PlaybackSession.NaturalDurationChanged -= OnDurationChanged;
                player.MediaEnded -= OnMediaEnded;
                player.MediaFailed -= OnMediaFailed;
                player.MediaOpened -= OnMediaOpened;
                player.Source = null;
                player.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider: player cleanup error");
            }
        }

        if (webView is not null)
        {
            try
            {
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    webView.CoreWebView2.WebResourceRequested -= OnWebEmeWebResourceRequested;
                    _ = webView.CoreWebView2.ExecuteScriptAsync("try { window.__waveePlayer?.pause(); } catch (_) {}");
                }
                webView.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider: WebView2 cleanup error");
            }
        }

        if (ams is not null)
        {
            try
            {
                ams.DownloadFailed -= OnAdaptiveDownloadFailed;
                ams.Diagnostics.DiagnosticAvailable -= OnAdaptiveDiagnosticAvailable;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider: AMS cleanup error");
            }
        }

        if (mpdStream is not null)
        {
            try
            {
                mpdStream.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider: MPD stream cleanup error");
            }
        }

        if (mpm is not null)
        {
            try
            {
                mpm.ComponentLoadFailed -= OnComponentLoadFailed;
                mpm.ServiceRequested -= OnServiceRequested;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SpotifyVideoProvider: protection manager cleanup error");
            }
        }

        _stateSubject.OnNext(LocalPlaybackState.Empty);
        _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));
    }

    private Task CleanupAsync()
    {
        return RunOnUiAsync(CleanupPlayer);
    }

    // ── Threading helpers ──────────────────────────────────────────────

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        if (_dispatcher.HasThreadAccess)
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        return tcs.Task;
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess)
            return action();

        var tcs = new TaskCompletionSource<bool>();
        _dispatcher.TryEnqueue(async () =>
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
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _positionTimer.Stop();
            CleanupPlayer();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SpotifyVideoProvider dispose error");
        }
        _stateSubject.OnCompleted();
        _trackFinishedSubject.OnCompleted();
        _errorSubject.OnCompleted();
    }
}

internal sealed record SpotifyWebEmeVideoConfig(
    int VideoProfileId,
    int AudioProfileId,
    long DurationMs,
    int SegmentLength,
    string? LicenseServerEndpoint,
    IReadOnlyList<int> SegmentTimes,
    SpotifyWebEmeTrackConfig Video,
    SpotifyWebEmeTrackConfig Audio)
{
    public static SpotifyWebEmeVideoConfig FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root;
        var templateHost = root;

        if (root.TryGetProperty("contents", out var contents)
            && contents.ValueKind == JsonValueKind.Array
            && contents.GetArrayLength() > 0)
        {
            content = contents[0];
        }

        if (root.TryGetProperty("sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array
            && sources.GetArrayLength() > 0)
        {
            content = sources[0];
            templateHost = content;
        }

        var segmentLength = GetInt32(content, "segment_length") ?? 4;
        var durationMs = GetDurationMs(root, content);
        if (durationMs <= 0)
            throw new InvalidOperationException("Spotify video manifest did not include a valid duration.");

        var initTemplate = GetString(templateHost, "initialization_template")
            ?? throw new InvalidOperationException("Spotify video manifest did not include initialization_template.");
        var segmentTemplate = GetString(templateHost, "segment_template")
            ?? throw new InvalidOperationException("Spotify video manifest did not include segment_template.");
        var baseUrl = "";
        if (templateHost.TryGetProperty("base_urls", out var baseUrls)
            && baseUrls.ValueKind == JsonValueKind.Array
            && baseUrls.GetArrayLength() > 0)
        {
            baseUrl = baseUrls[0].GetString() ?? "";
        }

        int? widevineEncryptionIndex = null;
        string? licenseEndpoint = null;
        var encryptionHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (encryptionHost.TryGetProperty("encryption_infos", out var encryptionInfos)
            && encryptionInfos.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var encryptionInfo in encryptionInfos.EnumerateArray())
            {
                if (string.Equals(GetString(encryptionInfo, "key_system"), "widevine", StringComparison.OrdinalIgnoreCase))
                {
                    widevineEncryptionIndex = index;
                    licenseEndpoint = GetString(encryptionInfo, "license_server_endpoint");
                    break;
                }
                index++;
            }
        }

        WebProfile? selectedVideo = null;
        WebProfile? selectedAudio = null;
        if (content.TryGetProperty("profiles", out var profiles)
            && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                if (!string.Equals(GetString(profile, "file_type"), "webm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ProfileMatchesEncryptionIndex(profile, widevineEncryptionIndex))
                    continue;

                var id = GetInt32(profile, "id") ?? 0;
                if (id <= 0)
                    continue;

                var videoCodec = GetString(profile, "video_codec");
                if (!string.IsNullOrWhiteSpace(videoCodec))
                {
                    var width = GetInt32(profile, "video_width") ?? GetInt32(profile, "width") ?? 0;
                    var height = GetInt32(profile, "video_height") ?? GetInt32(profile, "height") ?? 0;
                    var bandwidth = GetInt32(profile, "max_bitrate") ?? GetInt32(profile, "bandwidth_estimate") ?? 0;
                    var candidate = new WebProfile(id, videoCodec, width, height, bandwidth);
                    if (selectedVideo is null
                        || candidate.Height > selectedVideo.Height
                        || (candidate.Height == selectedVideo.Height && candidate.Bandwidth > selectedVideo.Bandwidth))
                    {
                        selectedVideo = candidate;
                    }
                    continue;
                }

                var audioCodec = GetString(profile, "audio_codec");
                if (!string.IsNullOrWhiteSpace(audioCodec))
                {
                    var bandwidth = GetInt32(profile, "max_bitrate") ?? GetInt32(profile, "bandwidth_estimate") ?? 0;
                    var candidate = new WebProfile(id, audioCodec, 0, 0, bandwidth);
                    if (selectedAudio is null || candidate.Bandwidth > selectedAudio.Bandwidth)
                        selectedAudio = candidate;
                }
            }
        }

        if (selectedVideo is null)
            throw new InvalidOperationException("Spotify video manifest did not include a Widevine WebM video profile.");
        if (selectedAudio is null)
            throw new InvalidOperationException("Spotify video manifest did not include a Widevine WebM audio profile.");

        var totalSegments = (int)Math.Ceiling(durationMs / 1000d / segmentLength);
        var segmentTimes = new List<int>(totalSegments);
        for (var i = 0; i < totalSegments; i++)
            segmentTimes.Add(i * segmentLength);

        var video = BuildTrack(baseUrl, initTemplate, segmentTemplate, selectedVideo, segmentTimes, isVideo: true);
        var audio = BuildTrack(baseUrl, initTemplate, segmentTemplate, selectedAudio, segmentTimes, isVideo: false);
        return new SpotifyWebEmeVideoConfig(
            selectedVideo.Id,
            selectedAudio.Id,
            durationMs,
            segmentLength,
            licenseEndpoint,
            segmentTimes,
            video,
            audio);
    }

    public static string DescribeManifestForLog(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var content = root;

            if (root.TryGetProperty("contents", out var contents)
                && contents.ValueKind == JsonValueKind.Array
                && contents.GetArrayLength() > 0)
            {
                content = contents[0];
            }

            if (root.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array
                && sources.GetArrayLength() > 0)
            {
                content = sources[0];
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("durationMs=").Append(GetDurationMs(root, content));
            sb.Append("; segmentLength=").Append(GetInt32(content, "segment_length") ?? 0);
            sb.Append("; encryption=[");
            AppendEncryptionInfo(sb, content, root);
            sb.Append("]; profiles=[");
            AppendProfileInfo(sb, content);
            sb.Append(']');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"manifest diagnostics unavailable: {ex.Message}; bytes={json.Length}";
        }
    }

    private static SpotifyWebEmeTrackConfig BuildTrack(
        string baseUrl,
        string initTemplate,
        string segmentTemplate,
        WebProfile profile,
        IReadOnlyList<int> segmentTimes,
        bool isVideo)
    {
        var initUrl = baseUrl + initTemplate
            .Replace("{{profile_id}}", profile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{{file_type}}", "webm");

        var segmentUrls = new List<string>(segmentTimes.Count);
        foreach (var time in segmentTimes)
        {
            segmentUrls.Add(baseUrl + segmentTemplate
                .Replace("{{profile_id}}", profile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{{segment_timestamp}}", time.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{{file_type}}", "webm"));
        }

        var contentType = isVideo
            ? $"video/webm; codecs=\"{NormalizeWebCodec(profile.Codec)}\""
            : $"audio/webm; codecs=\"{NormalizeWebCodec(profile.Codec)}\"";

        return new SpotifyWebEmeTrackConfig(contentType, initUrl, segmentUrls);
    }

    private static string NormalizeWebCodec(string codec)
        => codec.Equals("vp9", StringComparison.OrdinalIgnoreCase) ? "vp9" : codec;

    private static long GetDurationMs(JsonElement root, JsonElement content)
    {
        if (content.TryGetProperty("duration", out var duration) && duration.TryGetInt64(out var durationMs))
            return durationMs;

        var startMs = GetInt64(content, "start_time_millis") ?? GetInt64(root, "start_time_millis") ?? 0;
        var endMs = GetInt64(content, "end_time_millis") ?? GetInt64(root, "end_time_millis") ?? 0;
        return endMs > startMs ? endMs - startMs : 0;
    }

    private static bool ProfileMatchesEncryptionIndex(JsonElement profile, int? encryptionIndex)
    {
        if (encryptionIndex is null) return true;
        if (!profile.TryGetProperty("encryption_indices", out var indices)
            || indices.ValueKind != JsonValueKind.Array)
        {
            return !profile.TryGetProperty("encryption_index", out var index)
                   || !index.TryGetInt32(out var value)
                   || value == encryptionIndex.Value;
        }

        foreach (var index in indices.EnumerateArray())
        {
            if (index.TryGetInt32(out var value) && value == encryptionIndex.Value)
                return true;
        }

        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static void AppendEncryptionInfo(System.Text.StringBuilder sb, JsonElement content, JsonElement root)
    {
        var encryptionHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (!encryptionHost.TryGetProperty("encryption_infos", out var encryptionInfos)
            || encryptionInfos.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var index = 0;
        var wrote = false;
        foreach (var encryptionInfo in encryptionInfos.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(index)
              .Append(':')
              .Append(GetString(encryptionInfo, "key_system") ?? "<unknown>")
              .Append(":license=")
              .Append(string.IsNullOrWhiteSpace(GetString(encryptionInfo, "license_server_endpoint")) ? "<none>" : "<set>");
            index++;
        }

        if (!wrote) sb.Append("<empty>");
    }

    private static void AppendProfileInfo(System.Text.StringBuilder sb, JsonElement content)
    {
        if (!content.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var wrote = false;
        foreach (var profile in profiles.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(GetInt32(profile, "id") ?? 0)
              .Append(':')
              .Append(GetString(profile, "file_type") ?? "<type>")
              .Append(':')
              .Append(GetString(profile, "video_codec") ?? GetString(profile, "audio_codec") ?? "<codec>")
              .Append(':')
              .Append(GetInt32(profile, "video_width") ?? GetInt32(profile, "width") ?? 0)
              .Append('x')
              .Append(GetInt32(profile, "video_height") ?? GetInt32(profile, "height") ?? 0)
              .Append(":enc=");

            if (profile.TryGetProperty("encryption_index", out var encryptionIndex)
                && encryptionIndex.TryGetInt32(out var index))
            {
                sb.Append(index);
            }
            else if (profile.TryGetProperty("encryption_indices", out var encryptionIndices)
                     && encryptionIndices.ValueKind == JsonValueKind.Array)
            {
                sb.Append('[');
                var wroteIndex = false;
                foreach (var item in encryptionIndices.EnumerateArray())
                {
                    if (!item.TryGetInt32(out var value)) continue;
                    if (wroteIndex) sb.Append(',');
                    wroteIndex = true;
                    sb.Append(value);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append("<none>");
            }
        }

        if (!wrote) sb.Append("<empty>");
    }

    private sealed record WebProfile(int Id, string Codec, int Width, int Height, int Bandwidth);
}

internal sealed record SpotifyWebEmeTrackConfig(
    string ContentType,
    string InitUrl,
    IReadOnlyList<string> SegmentUrls);
