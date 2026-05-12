using System;
using Wavee.Audio;
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

    // Track collections — populated when PlayFileAsync wraps MediaSource in a
    // MediaPlaybackItem (which is the only container that exposes embedded
    // audio / video / subtitle track lists). Null when idle. Drives the
    // track menus in the Theatre / Fullscreen surface.
    private MediaPlaybackItem? _currentPlaybackItem;

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
                _externalSubtitleLabels.Clear();

                var source = MediaSource.CreateFromUri(new Uri(filePath));
                // Wrap in MediaPlaybackItem so AudioTracks / VideoTracks /
                // TimedMetadataTracks are reachable — MediaSource alone
                // doesn't expose them.
                var item = new MediaPlaybackItem(source);
                _currentPlaybackItem = item;
                _player.Source = item;
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
                _playbackItemChanged?.Invoke(this, item);

                // Auto-attach previously-scanned + user-saved external subtitles
                // for this file from local_subtitle_files. The scanner's
                // sibling-discovery and the user's drag-drop history both
                // funnel through that table, so a single read here covers both.
                _ = AttachPersistedSubtitlesAsync(filePath, source);
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

    /// <summary>
    /// The live <see cref="MediaPlaybackItem"/> driving the player, or null
    /// when idle. Exposed so the Theatre / Fullscreen surface can populate
    /// audio / video / subtitle track menus from
    /// <see cref="MediaPlaybackItem.AudioTracks"/> etc.
    /// </summary>
    public MediaPlaybackItem? CurrentPlaybackItem => _currentPlaybackItem;

    /// <summary>
    /// Fires after a fresh <see cref="MediaPlaybackItem"/> is assigned. The
    /// Theatre surface uses this to repopulate its track menus when the
    /// playing item changes.
    /// </summary>
    public event EventHandler<MediaPlaybackItem>? PlaybackItemChanged
    {
        add => _playbackItemChanged += value;
        remove => _playbackItemChanged -= value;
    }
    private EventHandler<MediaPlaybackItem>? _playbackItemChanged;

    /// <summary>
    /// Side-loaded external subtitle (dropped onto the player surface). The
    /// file is loaded as a <see cref="TimedTextSource"/> on the active
    /// <see cref="MediaSource"/>, then auto-selected so it renders. Also
    /// persisted to <c>local_subtitle_files</c> via
    /// <see cref="Wavee.Local.ILocalLibraryService"/> so subsequent plays of
    /// the same video re-attach the subtitle without the user re-dropping it.
    /// <paramref name="label"/> is shown in the subtitle menu — typically the
    /// filename stem.
    /// </summary>
    public Task AddExternalSubtitleAsync(
        string filePath,
        string? languageCode,
        string? label,
        bool forced = false,
        bool sdh = false,
        CancellationToken ct = default)
    {
        if (_disposed || _currentPlaybackItem is null)
            return Task.CompletedTask;

        // Persist for future plays — fire-and-forget; the live attach below
        // is the immediately user-visible bit, the DB insert just makes it
        // sticky next session. Skipped silently when no library is registered.
        var videoUri = _currentTrackUri;
        if (videoUri is not null)
        {
            _ = PersistDroppedSubtitleAsync(filePath, languageCode, forced, sdh, videoUri);
        }

        return RunOnUiAsync(() =>
        {
            try
            {
                // CreateFromUri's (uri, defaultLanguage) overload imprints the
                // language on every TimedMetadataTrack the source resolves —
                // TimedMetadataTrack.Language is read-only at runtime so this
                // is the only way to set it. Falls back to the no-language
                // overload when we don't have a code (the menu then shows
                // whatever MediaFoundation reports).
                var tts = string.IsNullOrEmpty(languageCode)
                    ? TimedTextSource.CreateFromUri(new Uri(filePath))
                    : TimedTextSource.CreateFromUri(new Uri(filePath), languageCode);

                tts.Resolved += (sender, args) =>
                {
                    if (args.Error is not null)
                    {
                        _logger?.LogDebug("Subtitle resolve failed for {Path}: {Code} / {ExtErr}",
                            filePath, args.Error.ErrorCode, args.Error.ExtendedError);
                        return;
                    }

                    // Auto-select the freshly-added subtitle once it resolves.
                    // The platform appends the new track(s) to the playback
                    // item's TimedMetadataTracks; selecting the highest-index
                    // entry matches what the user just dropped.
                    var item = _currentPlaybackItem;
                    if (item is null) return;
                    _dispatcher.TryEnqueue(() =>
                    {
                        var count = item.TimedMetadataTracks.Count;
                        if (count == 0) return;
                        for (uint k = 0; k < count; k++)
                        {
                            item.TimedMetadataTracks.SetPresentationMode(k,
                                k == count - 1
                                    ? TimedMetadataTrackPresentationMode.PlatformPresented
                                    : TimedMetadataTrackPresentationMode.Disabled);
                        }
                    });
                };

                _currentPlaybackItem.Source.ExternalTimedTextSources.Add(tts);

                // Remember the human-readable label for the menu — the
                // platform doesn't let us mutate TimedMetadataTrack.Label,
                // but we track our own mapping from subtitle file to label
                // so the flyout can display "Movie.eng.srt" instead of an
                // empty string. Key = absolute path; value = label.
                if (!string.IsNullOrEmpty(label))
                    _externalSubtitleLabels[filePath] = label!;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LocalMediaPlayer: failed to attach subtitle {Path}", filePath);
            }
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _externalSubtitleLabels
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot of human-readable labels keyed by subtitle file path. The
    /// Theatre / Fullscreen track flyout consults this so a side-loaded
    /// "Movie.eng.srt" reads as "eng" / its filename in the menu instead of
    /// the empty string MediaFoundation often hands us for sidecar files.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string> ExternalSubtitleLabels
        => _externalSubtitleLabels;

    /// <summary>
    /// Loads all rows from <c>local_subtitle_files</c> for the given video and
    /// attaches them to the live <see cref="MediaSource"/> as
    /// <see cref="TimedTextSource"/>s. Runs in the background — playback starts
    /// immediately and subtitles materialise in <c>TimedMetadataTracks</c> as
    /// MediaFoundation finishes parsing them. Skipped silently when no library
    /// service is registered (tests, headless contexts).
    /// </summary>
    private async Task PersistDroppedSubtitleAsync(
        string subtitlePath,
        string? languageCode,
        bool forced,
        bool sdh,
        string trackUri)
    {
        try
        {
            var library = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.Local.ILocalLibraryService>();
            if (library is null) return;

            // local_subtitle_files is keyed by the on-disk video path, not by
            // the wavee:local:track URI. Resolve via the existing lookup so we
            // don't widen the contract.
            var videoPath = await library.GetFilePathForTrackAsync(trackUri).ConfigureAwait(false);
            if (string.IsNullOrEmpty(videoPath)) return;

            await library.AddExternalSubtitleAsync(
                videoFilePath: videoPath,
                subtitlePath: subtitlePath,
                language: languageCode,
                forced: forced,
                sdh: sdh).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PersistDroppedSubtitleAsync failed for {Path}", subtitlePath);
        }
    }

    private async Task AttachPersistedSubtitlesAsync(string filePath, MediaSource source)
    {
        try
        {
            var library = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Wavee.Local.ILocalLibraryService>();
            if (library is null) return;

            var subs = await library.GetSubtitlesForAsync(filePath).ConfigureAwait(false);
            if (subs.Count == 0) return;

            await RunOnUiAsync(() =>
            {
                foreach (var sub in subs)
                {
                    // Skip embedded entries — those are already in
                    // MediaPlaybackItem.TimedMetadataTracks via the container.
                    if (sub.Embedded) continue;
                    if (string.IsNullOrEmpty(sub.Path)) continue;

                    try
                    {
                        var tts = string.IsNullOrEmpty(sub.Language)
                            ? TimedTextSource.CreateFromUri(new Uri(sub.Path))
                            : TimedTextSource.CreateFromUri(new Uri(sub.Path), sub.Language);
                        source.ExternalTimedTextSources.Add(tts);

                        var stem = System.IO.Path.GetFileNameWithoutExtension(sub.Path);
                        _externalSubtitleLabels[sub.Path] = sub.Language ?? stem;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to attach persisted subtitle {Path}", sub.Path);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AttachPersistedSubtitlesAsync failed for {Path}", filePath);
        }
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
            _currentPlaybackItem = null;
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
