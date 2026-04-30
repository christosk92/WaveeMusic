using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Audio;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Video;
using Wavee.Playback.Contracts;
using Wavee.UI.WinUI.Services.SpotifyVideo;
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
    : ISpotifyVideoPlayback, IVideoSurfaceProvider, ISpotifyVideoPlaybackDetails, IDisposable
{
    private const uint MsprContentEnablingActionRequired = 0x8004B895;
    private const string PlayReadyProtectionSystemId = "{F4637010-03C3-42CD-B932-B48ADF3A6A54}";
    private const string PlayReadyContainerGuid = "{9A04F079-9840-4286-AB92-E65BE0885F95}";

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
    private SpotifyWebEmePlayer? _webEmePlayer;
    private SpotifyWebEmeVideoManifest? _currentWebEmeManifest;
    private AdaptiveMediaSource? _ams;
    private InMemoryRandomAccessStream? _mpdStream;
    private SpotifyVideoManifest? _currentManifest;
    private string? _currentSanitizedMpd;
    private string? _currentTrackUri;
    private TrackMetadataDto? _currentMetadata;
    private long _currentDurationMs;
    private long _webPositionMs;
    private bool _webIsPlaying;
    private bool _webIsBuffering;
    private bool _surfaceIsLoading;
    private bool _surfaceHasFirstFrame;
    private bool _disposed;
    private IReadOnlyList<SpotifyVideoQualityOption> _availableQualities = Array.Empty<SpotifyVideoQualityOption>();
    private SpotifyVideoQualityOption? _currentQuality;
    private SpotifyVideoPlaybackMetadata? _playbackMetadata;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── IVideoSurfaceProvider ──────────────────────────────────────────────

    MediaPlayer? IVideoSurfaceProvider.Surface => _currentTrackUri is null ? null : _player;
    FrameworkElement? IVideoSurfaceProvider.ElementSurface => _currentTrackUri is null ? null : _webEmePlayer?.Surface;
    bool IVideoSurfaceProvider.IsSurfaceLoading => _currentTrackUri is not null && _surfaceIsLoading;
    bool IVideoSurfaceProvider.HasFirstFrame => _currentTrackUri is not null && _surfaceHasFirstFrame;
    bool IVideoSurfaceProvider.IsSurfaceBuffering => _currentTrackUri is not null
        && _surfaceHasFirstFrame
        && (_player is null
            ? _webEmePlayer?.IsBuffering == true || _webIsBuffering
            : _player.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering);
    string IVideoSurfaceProvider.Kind => "spotify-music-video";
    public IObservable<VideoSurfaceChange> SurfaceChanges => _surfaceChangesSubject.AsObservable();

    public IReadOnlyList<SpotifyVideoQualityOption> AvailableQualities => _availableQualities;
    public SpotifyVideoQualityOption? CurrentQuality => _currentQuality;
    public SpotifyVideoPlaybackMetadata? PlaybackMetadata => _playbackMetadata;
    public bool CanSelectQuality => _webEmePlayer is not null && _availableQualities.Count > 1;

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
        if (_webEmePlayer is not null) return _webEmePlayer.PauseAsync();
        if (_player is null) return Task.CompletedTask;
        return RunOnUiAsync(() => _player?.Pause());
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_webEmePlayer is not null) return _webEmePlayer.PlayAsync();
        if (_player is null) return Task.CompletedTask;
        return RunOnUiAsync(() => _player?.Play());
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (_webEmePlayer is not null)
            return _webEmePlayer.SeekAsync(positionMs);
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
        if (_webEmePlayer is not null)
        {
            _ = _webEmePlayer.SetVolumeAsync(clamped);
            return;
        }
        if (_player is null) return;
        _ = RunOnUiAsync(() => { if (_player is not null) _player.Volume = clamped; });
    }

    public Task SelectQualityAsync(int videoProfileId, CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(() => SelectQualityOnUiAsync(videoProfileId, cancellationToken));
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
        => _ = RunOnUiAsync(() =>
        {
            PublishStateFromSession();
            if (_player is not null)
                _surfaceChangesSubject.OnNext(new VideoSurfaceChange(_player, "spotify-music-video"));
        });

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
        SpotifyWebEmeVideoManifest config;
        try
        {
            config = SpotifyWebEmeVideoManifest.FromJson(manifestJson);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "SpotifyVideoProvider: WebView2 EME manifest parse failed. diagnostics={Diagnostics}",
                SpotifyWebEmeVideoManifest.DescribeManifestForLog(manifestJson));
            throw;
        }

        _currentDurationMs = durationMs > 0 ? durationMs : config.DurationMs;
        _currentWebEmeManifest = config;
        _webPositionMs = Math.Max(0, (long)startPositionMs);
        _webIsPlaying = false;
        _webIsBuffering = true;
        _surfaceIsLoading = true;
        _surfaceHasFirstFrame = false;
        UpdatePlaybackDetails(config);

        _logger?.LogInformation(
            "SpotifyVideoProvider: WebView2 EME manifest parsed video={VideoProfile} audio={AudioProfile} durationMs={DurationMs} segmentLength={SegmentLength}s segments={SegmentCount} license={LicenseEndpoint}",
            config.VideoProfileId,
            config.AudioProfileId,
            config.DurationMs,
            config.SegmentLength,
            config.SegmentTimes.Count,
            config.LicenseServerEndpoint ?? "<none>");

        var webPlayer = CreateWebEmePlayer();
        AttachWebEmePlayer(webPlayer);
        _webEmePlayer = webPlayer;
        OnPropertyChanged(nameof(CanSelectQuality));

        await webPlayer.StartAsync(config, startPositionMs, cancellationToken);

        if (!ReferenceEquals(_webEmePlayer, webPlayer))
            _logger?.LogDebug("SpotifyVideoProvider: WebView2 EME startup completed after provider state changed");
    }

    private async Task SelectQualityOnUiAsync(int videoProfileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = _currentWebEmeManifest;
        var oldPlayer = _webEmePlayer;
        if (manifest is null || oldPlayer is null)
            return;

        var selectedTrack = manifest.VideoTracks.FirstOrDefault(track => track.ProfileId == videoProfileId);
        if (selectedTrack is null || selectedTrack.ProfileId == manifest.Video.ProfileId)
            return;

        var restartPositionMs = oldPlayer.PositionMs > 0 ? oldPlayer.PositionMs : _webPositionMs;
        var wasPlaying = oldPlayer.IsPlaying || _webIsPlaying;
        var updatedManifest = manifest with
        {
            VideoProfileId = selectedTrack.ProfileId,
            Video = selectedTrack
        };

        _logger?.LogInformation(
            "SpotifyVideoProvider: switching WebView2 EME quality {OldProfile}->{NewProfile} at {PositionMs}ms",
            manifest.Video.ProfileId,
            selectedTrack.ProfileId,
            restartPositionMs);

        await oldPlayer.PauseAsync();
        DetachWebEmePlayer(oldPlayer);

        _currentWebEmeManifest = updatedManifest;
        _webPositionMs = Math.Max(0, restartPositionMs);
        _webIsPlaying = false;
        _webIsBuffering = true;
        _surfaceIsLoading = true;
        _surfaceHasFirstFrame = false;
        UpdatePlaybackDetails(updatedManifest);
        PublishStateFromSession();
        _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));

        var newPlayer = CreateWebEmePlayer();
        AttachWebEmePlayer(newPlayer);
        _webEmePlayer = newPlayer;
        OnPropertyChanged(nameof(CanSelectQuality));
        await newPlayer.StartAsync(updatedManifest, restartPositionMs, cancellationToken, wasPlaying);

        try
        {
            oldPlayer.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SpotifyVideoProvider: old WebView2 player cleanup failed during quality switch");
        }
    }

    private SpotifyWebEmePlayer CreateWebEmePlayer()
    {
        return new SpotifyWebEmePlayer(
            _dispatcher,
            new SpotifyWebEmePlayerDocumentRenderer(),
            (challenge, endpoint, ct) => _spClient.PostWidevineLicenseAsync(challenge, endpoint, null, ct),
            _logger);
    }

    private void AttachWebEmePlayer(SpotifyWebEmePlayer webPlayer)
    {
        webPlayer.SurfaceCreated += OnWebEmeSurfaceCreated;
        webPlayer.StateChanged += OnWebEmeStateChanged;
        webPlayer.FirstFrame += OnWebEmeFirstFrame;
        webPlayer.Ended += OnWebEmeEnded;
        webPlayer.Error += OnWebEmeError;
        webPlayer.Log += OnWebEmeLog;
        webPlayer.AutoplayBlocked += OnWebEmeAutoplayBlocked;
    }

    private void DetachWebEmePlayer(SpotifyWebEmePlayer webPlayer)
    {
        webPlayer.SurfaceCreated -= OnWebEmeSurfaceCreated;
        webPlayer.StateChanged -= OnWebEmeStateChanged;
        webPlayer.FirstFrame -= OnWebEmeFirstFrame;
        webPlayer.Ended -= OnWebEmeEnded;
        webPlayer.Error -= OnWebEmeError;
        webPlayer.Log -= OnWebEmeLog;
        webPlayer.AutoplayBlocked -= OnWebEmeAutoplayBlocked;
    }

    private void OnWebEmeSurfaceCreated(object? sender, EventArgs e)
    {
        _positionTimer.Start();
        _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));
        if (sender is SpotifyWebEmePlayer webPlayer)
        {
            _logger?.LogDebug(
                "SpotifyVideoProvider: WebView2 EME surface published xamlRoot={HasXamlRoot}",
                webPlayer.Surface?.XamlRoot is not null);
        }
    }

    private void OnWebEmeStateChanged(object? sender, SpotifyWebEmePlayerState state)
    {
        var wasBuffering = _webIsBuffering;
        var hadFirstFrame = _surfaceHasFirstFrame;

        _webPositionMs = state.PositionMs;
        if (state.DurationMs > 0)
            _currentDurationMs = state.DurationMs;
        _webIsPlaying = state.IsPlaying;
        _webIsBuffering = state.IsBuffering;

        if (!_surfaceHasFirstFrame && _webIsPlaying)
        {
            MarkFirstFrame("state-playing");
            return;
        }

        PublishStateFromSession();
        if (hadFirstFrame && wasBuffering != _webIsBuffering)
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "spotify-music-video"));
    }

    private void OnWebEmeFirstFrame(object? sender, string reason)
        => MarkFirstFrame(reason);

    private void OnWebEmeEnded(object? sender, EventArgs e)
    {
        _logger?.LogInformation("SpotifyVideoProvider: WebView2 video ended");
        OnWebEnded();
    }

    private void OnWebEmeError(object? sender, string message)
    {
        _logger?.LogError("SpotifyVideoProvider: WebView2 error {Error}", message);
        OnWebError(message);
    }

    private void OnWebEmeLog(object? sender, string message)
        => _logger?.LogDebug("SpotifyVideoProvider WebView2: {Message}", message);

    private void OnWebEmeAutoplayBlocked(object? sender, string message)
        => _logger?.LogWarning("SpotifyVideoProvider: WebView2 autoplay blocked {Message}", message);

    private void UpdatePlaybackDetails(SpotifyWebEmeVideoManifest? manifest)
    {
        if (manifest is null)
        {
            _availableQualities = Array.Empty<SpotifyVideoQualityOption>();
            _currentQuality = null;
            _playbackMetadata = null;
            NotifyPlaybackDetailsChanged();
            return;
        }

        var qualities = manifest.VideoTracks
            .Select(ToQualityOption)
            .ToArray();

        _availableQualities = qualities;
        _currentQuality = qualities.FirstOrDefault(q => q.ProfileId == manifest.Video.ProfileId)
                          ?? ToQualityOption(manifest.Video);
        _playbackMetadata = new SpotifyVideoPlaybackMetadata(
            DrmSystem: "Widevine",
            Container: "DASH WebM",
            VideoCodec: manifest.Video.Codec,
            AudioCodec: manifest.Audio.Codec,
            LicenseServerEndpoint: manifest.LicenseServerEndpoint,
            SegmentLengthSeconds: manifest.SegmentLength,
            SegmentCount: manifest.SegmentTimes.Count,
            DurationMs: manifest.DurationMs);

        NotifyPlaybackDetailsChanged();
    }

    private static SpotifyVideoQualityOption ToQualityOption(SpotifyWebEmeTrackConfig track)
    {
        return new SpotifyVideoQualityOption(
            track.ProfileId,
            track.Label,
            track.Width,
            track.Height,
            track.Bandwidth,
            track.Codec);
    }

    private void NotifyPlaybackDetailsChanged()
    {
        OnPropertyChanged(nameof(AvailableQualities));
        OnPropertyChanged(nameof(CurrentQuality));
        OnPropertyChanged(nameof(PlaybackMetadata));
        OnPropertyChanged(nameof(CanSelectQuality));
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
        var webEmePlayer = _webEmePlayer;
        var ams = _ams;
        var mpm = _mpm;
        var mpdStream = _mpdStream;
        _player = null;
        _webEmePlayer = null;
        _currentWebEmeManifest = null;
        _ams = null;
        _mpm = null;
        _mpdStream = null;
        _currentManifest = null;
        _currentSanitizedMpd = null;
        _currentTrackUri = null;
        _currentMetadata = null;
        _currentDurationMs = 0;
        _webPositionMs = 0;
        _webIsPlaying = false;
        _webIsBuffering = false;
        _surfaceIsLoading = false;
        _surfaceHasFirstFrame = false;
        UpdatePlaybackDetails(null);

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

        if (webEmePlayer is not null)
        {
            try
            {
                DetachWebEmePlayer(webEmePlayer);
                webEmePlayer.Dispose();
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
