using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Connect;
using Wavee.Playback.Contracts;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// UI-process media player wrapper for tracks that carry video frames. Wraps
/// <see cref="Windows.Media.Playback.MediaPlayer"/> (MediaFoundation under the
/// hood) and exposes an observable contract that mirrors
/// <c>AudioPipelineProxy</c> so <c>PlaybackOrchestrator</c> can route to either
/// engine without per-engine state-translation logic.
///
/// <para>
/// Local v1: plays a file path directly via <c>MediaSource.CreateFromUri</c>.
/// Spotify v2: the same instance can play decrypted bytes via
/// <c>MediaSource.CreateFromStream</c> over the AudioDecryptStream chain — the
/// public Play API will gain a stream-based overload.
/// </para>
/// </summary>
public sealed class LocalMediaPlayer : Wavee.Audio.ILocalMediaPlayer, IVideoSurfaceProvider, IDisposable
{
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly Windows.Media.Playback.MediaPlayer _player;

    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject = new(LocalPlaybackState.Empty);
    private readonly Subject<TrackFinishedMessage> _trackFinishedSubject = new();
    private readonly Subject<PlaybackError> _errorSubject = new();
    private readonly Subject<VideoSurfaceChange> _surfaceChangesSubject = new();

    private readonly DispatcherQueueTimer _positionTimer;
    private const int PositionPublishIntervalMs = 1000;

    private string? _currentTrackUri;
    private TrackMetadataDto? _currentMetadata;
    private long _currentDurationMs;
    private bool _disposed;

    public LocalMediaPlayer(ILogger<LocalMediaPlayer>? logger = null)
    {
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                          "LocalMediaPlayer must be constructed on a UI thread.");

        _player = new Windows.Media.Playback.MediaPlayer
        {
            // Audio + video together — the PlaybackSession's PlayedAfterDelay
            // semantics matter for AV sync, which MediaFoundation handles
            // internally. We render to whatever MediaPlayerElement gets the
            // SetMediaPlayer call; without one, audio still plays.
            AutoPlay = true,
            IsLoopingEnabled = false,
            CommandManager = { IsEnabled = false },
        };

        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.PlaybackSession.NaturalDurationChanged += OnDurationChanged;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaOpened += OnMediaOpened;

        _positionTimer = _dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionPublishIntervalMs);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += (_, _) => PublishStateFromSession();
    }

    /// <summary>The underlying MediaPlayer. Bind a MediaPlayerElement's
    /// SetMediaPlayer to this to render video frames.</summary>
    public Windows.Media.Playback.MediaPlayer MediaPlayer => _player;

    /// <summary>True between PlayFile and Stop/MediaEnded/MediaFailed.</summary>
    public bool IsActive => _currentTrackUri is not null;

    /// <summary>The track URI currently driving the player; null when idle.</summary>
    public string? CurrentTrackUri => _currentTrackUri;

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject.AsObservable();
    public IObservable<TrackFinishedMessage> TrackFinished => _trackFinishedSubject.AsObservable();
    public IObservable<PlaybackError> Errors => _errorSubject.AsObservable();

    // ── IVideoSurfaceProvider ──────────────────────────────────────────────
    // Surface is only meaningful while we hold a track; null when idle so the
    // active-surface service can route the binding away (e.g. to a future
    // Spotify video engine that just claimed playback).
    Windows.Media.Playback.MediaPlayer? IVideoSurfaceProvider.Surface
        => _currentTrackUri is null ? null : _player;
    string IVideoSurfaceProvider.Kind => "local";
    public IObservable<VideoSurfaceChange> SurfaceChanges => _surfaceChangesSubject.AsObservable();

    /// <summary>
    /// Open and play a file from disk. Auto-plays on open. Position is polled
    /// at 1 Hz and published into <see cref="StateChanges"/>.
    /// </summary>
    public Task PlayFileAsync(
        string filePath,
        string trackUri,
        TrackMetadataDto? metadata,
        long startPositionMs,
        CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;

        _logger?.LogInformation("LocalMediaPlayer: play {Uri} ({Path})", trackUri, filePath);

        _currentTrackUri = trackUri;
        _currentMetadata = metadata;
        _currentDurationMs = 0;

        return RunOnUiAsync(() =>
        {
            try
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
                if (startPositionMs > 0)
                {
                    _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(startPositionMs);
                }
                _positionTimer.Start();
                _player.Play();
                PublishStateFromSession(forcePlaying: true);
                // Tell the active-surface service we now have a live MediaPlayer
                // — it will push us as the active provider so any registered
                // IMediaSurfaceConsumer (page or mini-player) gets bound.
                _surfaceChangesSubject.OnNext(new VideoSurfaceChange(_player, "local"));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LocalMediaPlayer: failed to open {Path}", filePath);
                _errorSubject.OnNext(new PlaybackError(
                    PlaybackErrorType.Unknown,
                    $"Could not open video file: {ex.Message}",
                    ex));
                _currentTrackUri = null;
                _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "local"));
            }
        });
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(() => _player.Pause());
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(() => _player.Play());
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(() =>
        {
            try
            {
                if (_player.PlaybackSession.CanSeek)
                    _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(positionMs);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "LocalMediaPlayer seek failed");
            }
        });
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        return RunOnUiAsync(() =>
        {
            try
            {
                _positionTimer.Stop();
                _player.Pause();
                _player.Source = null;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "LocalMediaPlayer stop failed");
            }
            _currentTrackUri = null;
            _currentMetadata = null;
            _currentDurationMs = 0;
            _stateSubject.OnNext(LocalPlaybackState.Empty);
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "local"));
        });
    }

    /// <summary>Volume in [0..1].</summary>
    public void SetVolume(float volume)
    {
        if (_disposed) return;
        var clamped = Math.Clamp(volume, 0f, 1f);
        _ = RunOnUiAsync(() => _player.Volume = clamped);
    }

    // ── MediaPlayer event plumbing ──────────────────────────────────────

    private void OnMediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        _logger?.LogDebug("LocalMediaPlayer: MediaOpened (uri={Uri})", _currentTrackUri);
        // Ensure first state push lands immediately so the player bar updates
        // before the 1 Hz timer ticks.
        _ = RunOnUiAsync(() =>
        {
            _player.Play();
            PublishStateFromSession(forcePlaying: true);
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        _ = RunOnUiAsync(() => PublishStateFromSession());
    }

    private void OnDurationChanged(MediaPlaybackSession sender, object args)
    {
        var ms = (long)sender.NaturalDuration.TotalMilliseconds;
        if (ms > 0) _currentDurationMs = ms;
        _ = RunOnUiAsync(() => PublishStateFromSession());
    }

    private void OnMediaEnded(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        var uri = _currentTrackUri;
        _logger?.LogInformation("LocalMediaPlayer: MediaEnded (uri={Uri})", uri);
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            // Final state — duration matches position so the bar locks.
            PublishStateFromSession(forceFinished: true);
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "finished",
                });
            }
            _currentTrackUri = null;
            _currentMetadata = null;
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "local"));
        });
    }

    private void OnMediaFailed(Windows.Media.Playback.MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger?.LogError("LocalMediaPlayer: MediaFailed code={Error} message={Message}",
            args.Error, args.ErrorMessage);
        var uri = _currentTrackUri;
        _ = RunOnUiAsync(() =>
        {
            _positionTimer.Stop();
            _errorSubject.OnNext(new PlaybackError(
                PlaybackErrorType.DecodeError,
                args.ErrorMessage ?? args.Error.ToString()));
            // Treat failure as "finished with error" so the orchestrator can
            // advance or stop without leaving the player wedged.
            if (!string.IsNullOrEmpty(uri))
            {
                _trackFinishedSubject.OnNext(new TrackFinishedMessage
                {
                    TrackUri = uri,
                    Reason = "error",
                });
            }
            _currentTrackUri = null;
            _currentMetadata = null;
            _stateSubject.OnNext(LocalPlaybackState.Empty);
            _surfaceChangesSubject.OnNext(new VideoSurfaceChange(null, "local"));
        });
    }

    private void PublishStateFromSession(bool forceFinished = false, bool forcePlaying = false)
    {
        if (_disposed || _currentTrackUri is null) return;

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

    // ── Threading helpers ───────────────────────────────────────────────

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _positionTimer.Stop();
            _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _player.PlaybackSession.NaturalDurationChanged -= OnDurationChanged;
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.MediaOpened -= OnMediaOpened;
            _player.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LocalMediaPlayer dispose error");
        }
        _stateSubject.OnCompleted();
        _trackFinishedSubject.OnCompleted();
        _errorSubject.OnCompleted();
    }
}
