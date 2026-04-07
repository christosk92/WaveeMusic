using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Runtime;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Connect.Protocol;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Processors;
using Wavee.Core.Audio;
using Wavee.Core.Audio.Download;

namespace Wavee.Connect.Playback;

/// <summary>
/// Main audio pipeline orchestrator that implements IPlaybackEngine.
/// Coordinates track sources, decoders, processors, and audio sinks for playback.
/// </summary>
/// <remarks>
/// ARCHITECTURE:
/// - Subscribes to ConnectCommandHandler observables for command handling
/// - Orchestrates: TrackSource → Decoder → Processors → Sink
/// - Publishes LocalPlaybackState changes to PlaybackStateManager
/// - Manages playback loop, buffering, and state transitions
///
/// THREADING:
/// - Background playback loop runs on dedicated task
/// - Command handlers are thread-safe and async
/// - State updates are published via BehaviorSubject (thread-safe)
/// </remarks>
public sealed class AudioPipeline : IPlaybackEngine, IAsyncDisposable
{
    private readonly TrackSourceRegistry _sourceRegistry;
    private readonly AudioDecoderRegistry _decoderRegistry;
    private readonly IAudioSink _audioSink;
    private readonly AudioProcessingChain _processingChain;
    private readonly ConnectCommandHandler? _commandHandler;
    private readonly DeviceStateManager? _deviceStateManager;
    private readonly EventService? _eventService;
    private readonly ContextResolver? _contextResolver;
    private readonly string _deviceId;
    private readonly ILogger? _logger;

    // Volume control
    private readonly VolumeProcessor? _volumeProcessor;
    private EqualizerProcessor? _userEq;
    private Action? _eqFiltersChangedHandler;

    // State management
    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject;
    private readonly Subject<PlaybackError> _errorSubject = new();
    private readonly Channel<LocalPlaybackState> _stateChannel;
    private LocalPlaybackState _currentState = LocalPlaybackState.Empty;
    private readonly object _stateLock = new();

    // Playback control
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private Thread? _playbackThread;
    private TaskCompletionSource? _playbackStartedTcs;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    // In-place seeking
    private long? _pendingSeekMs;
    private readonly object _seekLock = new();

    // Playback state
    private string? _currentTrackUri;
    private string? _currentTrackUid;
    private string? _currentContextUri;
    private string? _currentTrackTitle;
    private string? _currentTrackArtist;
    private string? _currentTrackAlbum;
    private string? _currentAlbumUri;
    private string? _currentArtistUri;
    private string? _currentImageSmallUrl;
    private string? _currentImageUrl;
    private string? _currentImageLargeUrl;
    private string? _currentImageXLargeUrl;
    private long _currentPositionMs;
    private long _currentDurationMs;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _shuffling;
    private bool _repeatingContext;
    private bool _repeatingTrack;
    private bool _currentCanSeek = true;

    // Event tracking for playback reporting
    private string? _currentSessionId;
    private string? _currentPlaybackId;
    private PlaybackMetrics? _currentMetrics;

    // Queue management
    private readonly PlaybackQueue _queue;
    private string? _nextPageUrl;  // For lazy loading more tracks

    // Command subscriptions
    private readonly CompositeDisposable _subscriptions = new();

    // Reconnection state (set by session connection observable)
    private volatile bool _isReconnecting;

    // Disposal
    private bool _disposed;

    // Event reporting configuration
    private EventReportingOptions _eventReportingOptions = EventReportingOptions.Default;

    // Progress publish cadence for periodic playback position updates.
    private const long PositionPublishIntervalMs = 1000;

    // Runtime health telemetry cadence.
    private const long RuntimeHealthLogIntervalMs = 30000;
    private long _lastRuntimeHealthLogAtMs;
    private long _lastRuntimeHealthUnderflowCount = -1;
    private int _lastRuntimeHealthGcGen0 = -1;
    private int _lastRuntimeHealthGcGen1 = -1;
    private int _lastRuntimeHealthGcGen2 = -1;

    /// <summary>
    /// Creates an AudioPipeline without command handler integration (manual control only).
    /// </summary>
    /// <param name="sourceRegistry">Registry for track sources.</param>
    /// <param name="decoderRegistry">Registry for audio decoders.</param>
    /// <param name="audioSink">Audio output sink.</param>
    /// <param name="processingChain">Audio processing chain.</param>
    /// <param name="deviceId">Device ID for event reporting.</param>
    /// <param name="eventService">Optional event service for reporting playback events.</param>
    /// <param name="contextResolver">Optional context resolver for playlist/album loading.</param>
    /// <param name="eventReportingOptions">Optional event reporting configuration.</param>
    /// <param name="logger">Optional logger.</param>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        string deviceId = "",
        EventService? eventService = null,
        ContextResolver? contextResolver = null,
        EventReportingOptions? eventReportingOptions = null,
        ILogger? logger = null)
    {
        _sourceRegistry = sourceRegistry ?? throw new ArgumentNullException(nameof(sourceRegistry));
        _decoderRegistry = decoderRegistry ?? throw new ArgumentNullException(nameof(decoderRegistry));
        _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _processingChain = processingChain ?? throw new ArgumentNullException(nameof(processingChain));
        _deviceId = deviceId ?? "";
        _eventService = eventService;
        _contextResolver = contextResolver;
        _eventReportingOptions = eventReportingOptions ?? EventReportingOptions.Default;
        _logger = logger;

        _stateSubject = new BehaviorSubject<LocalPlaybackState>(_currentState);
        _stateChannel = Channel.CreateBounded<LocalPlaybackState>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        _ = ConsumeStateChannelAsync();
        _queue = new PlaybackQueue(logger);

        // Find VolumeProcessor from processing chain for volume control
        _volumeProcessor = _processingChain.Processors.OfType<VolumeProcessor>().FirstOrDefault();

        // Subscribe to EQ filter changes — flush the sink buffer so new EQ is audible immediately
        _userEq = _processingChain.Processors.OfType<EqualizerProcessor>().FirstOrDefault();
        if (_userEq != null)
        {
            _eqFiltersChangedHandler = () =>
            {
                try { _ = _audioSink.FlushAsync(); }
                catch { /* sink may be disposed */ }
            };
            _userEq.FiltersChanged += _eqFiltersChangedHandler;
        }

        // Subscribe to NeedsMoreTracks for infinite contexts / large playlists
        _subscriptions.Add(_queue.NeedsMoreTracks.Subscribe(async _ =>
        {
            await LoadMoreTracksAsync();
        }));
    }

    /// <summary>
    /// Loads more tracks when the queue signals it needs more (lazy loading).
    /// </summary>
    private async Task LoadMoreTracksAsync()
    {
        if (_contextResolver == null || string.IsNullOrEmpty(_nextPageUrl))
        {
            _logger?.LogDebug("Cannot load more tracks: no context resolver or next page URL");
            return;
        }

        try
        {
            _logger?.LogDebug("Loading more tracks from: {NextPageUrl}", _nextPageUrl);

            var result = await _contextResolver.LoadNextPageAsync(_nextPageUrl, enrichMetadata: true);
            _queue.AppendTracks(result.Tracks);
            _nextPageUrl = result.NextPageUrl;

            _logger?.LogDebug("Loaded {Count} more tracks, hasMore={HasMore}",
                result.Tracks.Count, result.HasMoreTracks);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load more tracks from {NextPageUrl}", _nextPageUrl);
        }
    }

    /// <summary>
    /// Creates an AudioPipeline with command handler integration (auto-subscribes to commands).
    /// </summary>
    /// <param name="sourceRegistry">Registry for track sources.</param>
    /// <param name="decoderRegistry">Registry for audio decoders.</param>
    /// <param name="audioSink">Audio output sink.</param>
    /// <param name="processingChain">Audio processing chain.</param>
    /// <param name="commandHandler">Connect command handler for remote control.</param>
    /// <param name="deviceStateManager">Optional device state manager for activation on playback.</param>
    /// <param name="deviceId">Device ID for event reporting.</param>
    /// <param name="eventService">Optional event service for reporting playback events.</param>
    /// <param name="contextResolver">Optional context resolver for playlist/album loading.</param>
    /// <param name="eventReportingOptions">Optional event reporting configuration.</param>
    /// <param name="logger">Optional logger.</param>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        ConnectCommandHandler commandHandler,
        DeviceStateManager? deviceStateManager = null,
        string deviceId = "",
        EventService? eventService = null,
        ContextResolver? contextResolver = null,
        EventReportingOptions? eventReportingOptions = null,
        ILogger? logger = null)
        : this(sourceRegistry, decoderRegistry, audioSink, processingChain, deviceId, eventService, contextResolver, eventReportingOptions, logger)
    {
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _deviceStateManager = deviceStateManager;
        SubscribeToCommands();
        SubscribeToVolumeChanges();
    }

    // ================================================================
    // IPLAYBACKENGINE IMPLEMENTATION
    // ================================================================

    /// <inheritdoc/>
    public IObservable<LocalPlaybackState> StateChanges => _stateSubject;

    /// <inheritdoc/>
    public IObservable<PlaybackError> Errors => _errorSubject;

    /// <inheritdoc/>
    public LocalPlaybackState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    /// Gets or sets the event reporting configuration.
    /// Controls which playback sources report events to Spotify.
    /// </summary>
    /// <summary>
    /// Subscribes to session connection state changes so the pipeline can
    /// publish IsBuffering=true during AP reconnection.
    /// </summary>
    public void SubscribeToConnectionState(IObservable<Wavee.Core.Session.SessionConnectionState> connectionState)
    {
        _subscriptions.Add(connectionState.Subscribe(state =>
        {
            var wasReconnecting = _isReconnecting;
            _isReconnecting = state == Wavee.Core.Session.SessionConnectionState.Reconnecting;

            // Publish state change so UI picks up the buffering flag
            if (_isReconnecting != wasReconnecting && _isPlaying)
            {
                PublishStateUpdate();
            }
        }));
    }

    public EventReportingOptions EventReporting
    {
        get => _eventReportingOptions;
        set => _eventReportingOptions = value ?? EventReportingOptions.Default;
    }

    /// <inheritdoc/>
    public async Task PlayAsync(PlayCommand command, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Play command received: Track={TrackUri}, Context={ContextUri}, Position={PositionMs}, PageTracks={PageTrackCount}",
            command.TrackUri, command.ContextUri, command.PositionMs, command.PageTracks?.Count ?? 0);

        // Normalize context URI early for comparison
        var normalizedContextUri = !string.IsNullOrEmpty(command.ContextUri)
            ? NormalizeToUri(command.ContextUri)
            : null;

        // If the command already includes tracks (e.g. inline queue), skip context resolution entirely
        var hasInlineTracks = command.PageTracks?.Count > 0;

        // ── Phase 1: Pre-load context OUTSIDE the lock (network I/O) ──
        // This prevents context resolution from blocking Pause/Resume/Seek
        ContextLoadResult? preloadedContext = null;
        if (!hasInlineTracks && !string.IsNullOrEmpty(normalizedContextUri) && _contextResolver != null)
        {
            // Check if same-context optimization applies (no preload needed)
            var isSameContext = string.Equals(_queue.ContextUri, normalizedContextUri, StringComparison.Ordinal)
                && _queue.LoadedCount > 0
                && (command.PageTracks == null || command.PageTracks.Count == 0);

            if (!isSameContext)
            {
                try
                {
                    preloadedContext = await _contextResolver.LoadContextAsync(
                        normalizedContextUri,
                        maxTracks: 100,
                        enrichMetadata: true,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to preload context {ContextUri}", normalizedContextUri);
                    // Will fall back to single track inside the lock
                }
            }
        }

        // ── Phase 2: Acquire lock for state changes ──
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            // Activate device when playback starts
            if (_deviceStateManager != null)
            {
                await _deviceStateManager.SetActiveAsync(true, cancellationToken);
            }

            // Update playback options from command (before same-context check for shuffle handling)
            if (command.Options != null)
            {
                _shuffling = command.Options.ShufflingContext;
                _repeatingContext = command.Options.RepeatingContext;
                _repeatingTrack = command.Options.RepeatingTrack;
            }

            // SAME-CONTEXT OPTIMIZATION: If already playing from this context, just skip to the track
            if (!string.IsNullOrEmpty(normalizedContextUri) &&
                string.Equals(_queue.ContextUri, normalizedContextUri, StringComparison.Ordinal) &&
                _queue.LoadedCount > 0 &&
                (command.PageTracks == null || command.PageTracks.Count == 0))
            {
                // Queue is already populated — search it directly
                var targetIndex = !string.IsNullOrEmpty(command.TrackUid)
                    ? _queue.FindIndexByUid(command.TrackUid)
                    : !string.IsNullOrEmpty(command.TrackUri)
                        ? _queue.FindIndexByUri(command.TrackUri)
                        : command.SkipToIndex ?? -1;
                if (targetIndex >= 0 && targetIndex < _queue.LoadedCount)
                {
                    _logger?.LogInformation("Same-context optimization: navigating to index {Index} (skipping API call)",
                        targetIndex);

                    // Stop current playback
                    await StopInternalAsync();

                    // Update state
                    _currentContextUri = normalizedContextUri;
                    _currentPositionMs = command.PositionMs ?? 0;

                    // Sync shuffle state with queue
                    _queue.SetShuffle(_shuffling);

                    // Navigate and play
                    var targetTrack = _queue.SkipTo(targetIndex);
                    if (targetTrack != null)
                    {
                        _currentTrackUri = NormalizeToUri(targetTrack.Uri);
                        _currentTrackUid = targetTrack.Uid ?? string.Empty;

                        _playbackCts = new CancellationTokenSource();
                        var token = _playbackCts.Token;
                        _playbackTask = LaunchPlaybackLoop(_currentTrackUri, _currentPositionMs, token);

                        // Send reply
                        if (_commandHandler != null &&
                            !string.IsNullOrEmpty(command.Key) &&
                            !command.Key.StartsWith("local/", StringComparison.Ordinal))
                        {
                            await _commandHandler.SendReplyAsync(command.Key, RequestResult.Success);
                        }
                        return;
                    }
                }
                _logger?.LogDebug("Same-context navigation failed, falling back to full context load");
            }

            // Stop current playback
            await StopInternalAsync();

            // Determine track URI to play
            var trackUri = command.TrackUri ?? command.ContextUri;
            if (string.IsNullOrEmpty(trackUri))
            {
                _logger?.LogWarning("Play command has no track or context URI");
                return;
            }

            // Convert URL to URI if needed (e.g., https://open.spotify.com/track/xxx -> spotify:track:xxx)
            trackUri = NormalizeToUri(trackUri);

            // Store context URI (already normalized above)
            _currentContextUri = normalizedContextUri;
            _currentTrackUid = string.Empty;
            _currentPositionMs = command.PositionMs ?? 0;

            // Resolve context if preload was skipped/failed (but NOT if we have inline tracks)
            if (!hasInlineTracks && preloadedContext == null && !string.IsNullOrEmpty(_currentContextUri) && _contextResolver != null)
            {
                try
                {
                    _logger?.LogDebug("Resolving context {ContextUri}", _currentContextUri);
                    preloadedContext = await _contextResolver.LoadContextAsync(
                        _currentContextUri, maxTracks: 100, enrichMetadata: true, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to resolve context {ContextUri}", _currentContextUri);
                }
            }

            // Setup queue from inline tracks or resolved context
            if (hasInlineTracks)
            {
                // Inline tracks from PageTracks — use directly, no context resolution needed
                var queueTracks = command.PageTracks!
                    .Select(pt => new QueueTrack(pt.Uri, pt.Uid))
                    .ToList();
                _queue.Clear();
                _queue.SetTracks(queueTracks, startIndex: command.SkipToIndex ?? 0);

                var current = _queue.Current;
                if (current != null)
                {
                    trackUri = current.Uri;
                    _currentTrackUri = trackUri;
                    _currentTrackUid = current.Uid ?? "";
                }
                else
                {
                    _logger?.LogError("No tracks in inline PageTracks");
                    return;
                }

                _logger?.LogInformation("Inline tracks loaded: {Count} tracks, playing {TrackUri}",
                    queueTracks.Count, trackUri);
            }
            else if (preloadedContext?.Tracks.Count > 0)
            {
                _queue.SetContext(
                    _currentContextUri!,
                    isInfinite: preloadedContext.IsInfinite,
                    totalTracks: preloadedContext.TotalCount);

                var targetIndex = ContextResolver.FindTrackIndex(
                    preloadedContext.Tracks, command.TrackUri, command.TrackUid, command.SkipToIndex);
                _queue.SetTracks(preloadedContext.Tracks, startIndex: targetIndex);
                _nextPageUrl = preloadedContext.NextPageUrl;

                // Find first playable track
                var current = _queue.Current;
                while (current != null && !current.IsPlayable)
                {
                    _logger?.LogWarning("Skipping unplayable track: {Uri}", current.Uri);
                    current = _queue.MoveNext();
                }

                if (current != null)
                {
                    trackUri = current.Uri;
                    _currentTrackUri = trackUri;
                    _currentTrackUid = current.Uid ?? string.Empty;
                }
                else
                {
                    _logger?.LogError("No playable tracks in context: {ContextUri}", _currentContextUri);
                    return;
                }

                _logger?.LogInformation("Context loaded: {TrackCount} tracks, playing {TrackUri}",
                    preloadedContext.Tracks.Count, trackUri);
            }
            else if (trackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                // Single track playback (no context or context resolution failed)
                _queue.Clear();
                _queue.SetTracks([new QueueTrack(trackUri)], startIndex: 0);
                _currentTrackUri = trackUri;
            }
            else if (!string.IsNullOrEmpty(_currentContextUri) && _contextResolver == null)
            {
                _logger?.LogError("Cannot play context {ContextUri}: ContextResolver not available", _currentContextUri);
                return;
            }
            else
            {
                _logger?.LogError("No playable content for URI: {Uri}", trackUri);
                return;
            }

            // Sync shuffle state with queue
            _queue.SetShuffle(_shuffling);

            // Validate that we have a playable URI (track, local file, or stream)
            if (!IsPlayableTrackUri(trackUri))
            {
                _logger?.LogError("Invalid URI for playback: {TrackUri}. " +
                    "Must be a Spotify track, local file, or stream URL.", trackUri);
                throw new InvalidOperationException(
                    $"Cannot play URI: {trackUri}. " +
                    "Must be a Spotify track URI, file path, or stream URL.");
            }

            // Start playback loop with a signal for when audio actually starts
            _playbackStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _playbackCts = new CancellationTokenSource();
            var playbackToken = _playbackCts.Token;
            _playbackTask = LaunchPlaybackLoop(trackUri, _currentPositionMs, playbackToken);

            // Wait for audio to actually start (up to 2s) before sending success reply
            // This prevents the "playing but silent" race condition
            try
            {
                await Task.WhenAny(_playbackStartedTcs.Task, Task.Delay(2000, cancellationToken));
            }
            catch (OperationCanceledException) { }

            // Send success reply if command handler exists AND this is a dealer command (not local)
            if (_commandHandler != null &&
                !string.IsNullOrEmpty(command.Key) &&
                !command.Key.StartsWith("local/", StringComparison.Ordinal))
            {
                await _commandHandler.SendReplyAsync(command.Key, RequestResult.Success);
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _commandLock.WaitAsync(cancellationToken);
        _logger?.LogInformation("[PERF] PauseAsync: lock acquired after {Elapsed}ms", sw.ElapsedMilliseconds);
        try
        {
            if (!_isPlaying || _isPaused)
            {
                _logger?.LogDebug("Already paused or not playing");
                return;
            }

            await _audioSink.PauseAsync();
            _logger?.LogInformation("[PERF] PauseAsync: audioSink.PauseAsync completed after {Elapsed}ms", sw.ElapsedMilliseconds);

            _isPaused = true;
            _isPlaying = false;

            PublishStateUpdate();
            _logger?.LogInformation("[PERF] PauseAsync: total {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Stop command received");

            // Cancel any active playback
            _playbackCts?.Cancel();

            // Wait for playback task to complete before clearing state
            if (_playbackTask is not null)
            {
                try
                {
                    await _playbackTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) { /* Expected */ }
                catch (TimeoutException)
                {
                    _logger?.LogWarning("Playback task did not stop within timeout");
                }
                _playbackTask = null;
            }

            // Flush and pause the audio sink
            await _audioSink.FlushAsync();
            await _audioSink.PauseAsync();

            // Reset state
            _isPlaying = false;
            _isPaused = false;
            _currentTrackUri = null;
            _currentTrackUid = null;
            _currentContextUri = null;
            _currentTrackTitle = null;
            _currentTrackArtist = null;
            _currentTrackAlbum = null;
            _currentPositionMs = 0;
            _currentDurationMs = 0;

            PublishStateUpdate();

            _logger?.LogDebug("Playback stopped and state cleared");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _commandLock.WaitAsync(cancellationToken);
        _logger?.LogInformation("[PERF] ResumeAsync: lock acquired after {Elapsed}ms", sw.ElapsedMilliseconds);
        try
        {

            // Check if playback loop ended (track finished) but we have a track to resume
            if (!_isPaused && !string.IsNullOrEmpty(_currentTrackUri) &&
                (_playbackTask == null || _playbackTask.IsCompleted))
            {
                _logger?.LogDebug("Track ended, restarting from beginning");

                // Clean up completed task (await to ensure it's truly done)
                if (_playbackTask is not null)
                {
                    try { await _playbackTask; }
                    catch { /* Task already completed or cancelled */ }
                }
                _playbackCts?.Dispose();
                _playbackCts = null;
                _playbackTask = null;

                // Restart from beginning (or current position if not at end)
                var startPosition = _currentPositionMs >= _currentDurationMs ? 0 : _currentPositionMs;
                _currentPositionMs = startPosition;

                _playbackCts = new CancellationTokenSource();
                var playbackToken = _playbackCts.Token;
                _playbackTask = LaunchPlaybackLoop(_currentTrackUri, startPosition, playbackToken);

                PublishStateUpdate();
                return;
            }

            if (!_isPaused)
            {
                _logger?.LogDebug("Not paused, nothing to resume");
                return;
            }

            // Try to resume the audio sink
            var resumed = await _audioSink.ResumeAsync();
            _logger?.LogInformation("[PERF] ResumeAsync: audioSink.ResumeAsync completed after {Elapsed}ms", sw.ElapsedMilliseconds);

            if (!resumed)
            {
                _logger?.LogError("Failed to resume playback: audio device unavailable");

                // Notify subscribers of the error
                _errorSubject.OnNext(new PlaybackError(
                    PlaybackErrorType.AudioDeviceUnavailable,
                    "Failed to resume playback. The audio device may be disconnected or unavailable."));

                // Keep the paused state - don't publish a misleading "Playing" state
                return;
            }

            _isPaused = false;
            _isPlaying = true;

            PublishStateUpdate();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Toggles the normalization processor on/off. Takes effect immediately.
    /// </summary>
    public void SetNormalizationEnabled(bool enabled)
    {
        foreach (var proc in _processingChain.Processors.OfType<NormalizationProcessor>())
            proc.IsEnabled = enabled;
        _logger?.LogInformation("Normalization {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Switches audio quality mid-playback by reloading the current track at the new bitrate.
    /// Causes a brief ~500ms audio gap while the new file is fetched and decoded.
    /// </summary>
    public async Task SwitchQualityAsync(AudioQuality quality, CancellationToken cancellationToken = default)
    {
        // Update the track source's preferred quality
        var spotifySource = _sourceRegistry.FindSource(_currentTrackUri ?? "") as SpotifyTrackSource;
        if (spotifySource == null)
        {
            _logger?.LogWarning("Cannot switch quality: no SpotifyTrackSource found");
            return;
        }

        spotifySource.SetPreferredQuality(quality);
        _logger?.LogInformation("Switching audio quality to {Quality} at position {Position}ms", quality, _currentPositionMs);

        // If nothing is playing, just update for next track
        if (!_isPlaying || string.IsNullOrEmpty(_currentTrackUri)) return;

        // Save current state
        var trackUri = _currentTrackUri;
        var positionMs = _currentPositionMs;

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();

            _currentPositionMs = positionMs;
            _playbackStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _playbackCts = new CancellationTokenSource();
            _playbackTask = LaunchPlaybackLoop(trackUri, positionMs, _playbackCts.Token);

            // Wait for audio to start
            await Task.WhenAny(_playbackStartedTcs.Task, Task.Delay(3000, cancellationToken));
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _commandLock.WaitAsync(cancellationToken);
        _logger?.LogInformation("[PERF] SeekAsync: lock acquired after {Elapsed}ms, seeking to {PositionMs}ms", sw.ElapsedMilliseconds, positionMs);
        try
        {

            // Check if seeking is supported (disabled for infinite streams)
            if (!_currentCanSeek)
            {
                _logger?.LogWarning("Seeking not supported for current track (infinite stream)");
                return;
            }

            if (string.IsNullOrEmpty(_currentTrackUri))
            {
                _logger?.LogWarning("No track loaded, cannot seek");
                return;
            }

            // Check if playback loop is running
            if (_playbackTask == null || _playbackTask.IsCompleted)
            {
                // No active playback loop - restart playback from the seek position
                _logger?.LogDebug("No active playback, restarting from {PositionMs}ms", positionMs);

                // Clean up any completed task
                _playbackCts?.Dispose();
                _playbackCts = null;
                _playbackTask = null;

                // Start playback from the seek position
                _currentPositionMs = positionMs;
                _playbackCts = new CancellationTokenSource();
                var playbackToken = _playbackCts.Token;
                _playbackTask = LaunchPlaybackLoop(_currentTrackUri, positionMs, playbackToken);

                PublishStateUpdate();
                return;
            }

            // Signal seek to active playback loop
            lock (_seekLock)
            {
                _pendingSeekMs = positionMs;
            }

            // Flush audio sink buffer for immediate effect and reset position tracking
            await _audioSink.FlushAsync();
            _audioSink.SetBasePosition(positionMs);
            _logger?.LogInformation("[PERF] SeekAsync: flush completed after {Elapsed}ms, signaled seek to playback loop", sw.ElapsedMilliseconds);

            _currentPositionMs = positionMs;
            PublishStateUpdate();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SkipNextAsync(CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Skip next command received");

            // Get next track from queue
            var nextTrack = _queue.MoveNext();

            if (nextTrack == null)
            {
                _logger?.LogDebug("No next track in queue, handling end of context");
                await HandleEndOfContextAsync(cancellationToken);
                return;
            }

            _logger?.LogDebug("Playing next track: {TrackUri}", nextTrack.Uri);

            // Stop current playback and start next
            await StopInternalAsync();

            // Update state
            var trackUri = NormalizeToUri(nextTrack.Uri);
            _currentTrackUri = trackUri;
            _currentTrackUid = nextTrack.Uid ?? string.Empty;
            _currentPositionMs = 0;

            // Start playback of next track
            _playbackCts = new CancellationTokenSource();
            var playbackToken = _playbackCts.Token;
            _playbackTask = LaunchPlaybackLoop(trackUri, 0, playbackToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SkipPreviousAsync(CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Skip previous command received");

            // If we're more than 3 seconds into the track, restart it
            if (_currentPositionMs > 3000)
            {
                _logger?.LogDebug("More than 3s into track, restarting");
                lock (_seekLock)
                {
                    _pendingSeekMs = 0;
                }
                _currentPositionMs = 0;
                await _audioSink.FlushAsync();
                PublishStateUpdate();
                return;
            }

            // Try to go to previous track
            var prevTrack = _queue.MovePrevious();

            if (prevTrack == null)
            {
                _logger?.LogDebug("No previous track, restarting current");
                lock (_seekLock)
                {
                    _pendingSeekMs = 0;
                }
                _currentPositionMs = 0;
                await _audioSink.FlushAsync();
                PublishStateUpdate();
                return;
            }

            _logger?.LogDebug("Playing previous track: {TrackUri}", prevTrack.Uri);

            // Stop current playback and start previous
            await StopInternalAsync();

            // Update state
            var trackUri = NormalizeToUri(prevTrack.Uri);
            _currentTrackUri = trackUri;
            _currentTrackUid = prevTrack.Uid ?? string.Empty;
            _currentPositionMs = 0;

            // Start playback of previous track
            _playbackCts = new CancellationTokenSource();
            var playbackToken = _playbackCts.Token;
            _playbackTask = LaunchPlaybackLoop(trackUri, 0, playbackToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Set shuffle: {Enabled}", enabled);

        _shuffling = enabled;
        _queue.SetShuffle(enabled);
        PublishStateUpdate();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Set repeat context: {Enabled}", enabled);

        _repeatingContext = enabled;
        PublishStateUpdate();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Set repeat track: {Enabled}", enabled);

        _repeatingTrack = enabled;
        PublishStateUpdate();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Set volume: {Volume:F2}", volume);

        // Apply directly in the sink's audio callback for instant effect (~50ms).
        // No need for the VolumeProcessor — callback scaling is immediate and
        // doesn't suffer from the circular buffer delay.
        if (_audioSink is Sinks.PortAudioSink portSink)
        {
            portSink.CallbackVolume = Math.Clamp(volume, 0f, 1.0f);
        }

        return Task.CompletedTask;
    }

    // ================================================================
    // END OF CONTEXT HANDLING
    // ================================================================

    /// <summary>
    /// Handles end of context (called from SkipNextAsync when queue is empty).
    /// Acquires command lock.
    /// </summary>
    private async Task HandleEndOfContextAsync(CancellationToken cancellationToken)
    {
        // Called from SkipNextAsync which already holds the command lock
        await HandleEndOfContextInternalAsync(cancellationToken);
    }

    /// <summary>
    /// Internal handler for end of context - implements repeat context, autoplay, or stop.
    /// Does not acquire command lock (caller must hold it or be in playback loop).
    /// </summary>
    private async Task HandleEndOfContextInternalAsync(CancellationToken cancellationToken)
    {
        if (_repeatingContext)
        {
            _logger?.LogDebug("Repeat context enabled, restarting from beginning");

            // Skip to beginning of queue
            var firstTrack = _queue.SkipTo(0);
            if (firstTrack != null)
            {
                // Note: We're inside PlaybackLoopAsync, so we can't call StopInternalAsync
                // (it would await _playbackTask which is ourselves - deadlock!)
                // Instead, the current loop will exit and we start a new one

                _currentTrackUri = NormalizeToUri(firstTrack.Uri);
                _currentTrackUid = firstTrack.Uid ?? string.Empty;
                _currentPositionMs = 0;

                // Start new playback (current loop will exit after this returns)
                _playbackCts = new CancellationTokenSource();
                var playbackToken = _playbackCts.Token;
                _playbackTask = LaunchPlaybackLoop(_currentTrackUri, 0, playbackToken);
                return;
            }
        }

        // TODO: Phase 3 - Handle autoplay (fetch recommendations from Spotify)
        // if (_autoplayEnabled)
        // {
        //     await LoadAutoplayTracksAsync();
        //     var nextTrack = _queue.MoveNext();
        //     if (nextTrack != null) { ... }
        // }

        _logger?.LogInformation("End of context - stopping playback");

        // Note: We're inside PlaybackLoopAsync, so we can't call StopInternalAsync
        // (it would await _playbackTask which is ourselves - deadlock!)
        // Just flush the sink and set state flags - the loop will exit naturally
        await _audioSink.FlushAsync();
        _isPlaying = false;
        _isPaused = false;

        // Clean up CTS (but NOT _playbackTask - we ARE that task!)
        _playbackCts?.Dispose();
        _playbackCts = null;
        // _playbackTask will be set to null when we check IsCompleted in future commands
    }

    // ================================================================
    // PLAYBACK LOOP - Core audio processing
    // ================================================================

    /// <summary>
    /// Launches PlaybackLoopAsync on a dedicated high-priority thread
    /// to isolate it from thread pool starvation caused by UI work.
    /// </summary>
    private Task LaunchPlaybackLoop(string trackUri, long startPositionMs, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        _playbackThread = new Thread(() =>
        {
            // Install a single-threaded SynchronizationContext so that all await
            // continuations in PlaybackLoopAsync resume on THIS thread, not the
            // thread pool. Without this, the dedicated thread just blocks on
            // GetResult() while continuations run on thread pool threads —
            // which get starved when the UI is busy, causing audio underflows.
            var syncCtx = new SingleThreadedSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            // Suppress foreground Gen2 compacting collections during playback.
            // Background Gen2 still runs (concurrent GC is enabled). This prevents
            // long GC pauses that starve the PortAudio callback and cause underflows.
            var previousLatency = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            try
            {
                var task = PlaybackLoopAsync(trackUri, startPositionMs, cancellationToken);
                syncCtx.RunUntilComplete(task);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                GCSettings.LatencyMode = previousLatency;
            }
        })
        {
            Name = "Wavee-AudioPlayback",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _playbackThread.Start();
        return tcs.Task;
    }

    private async Task PlaybackLoopAsync(string trackUri, long startPositionMs, CancellationToken cancellationToken)
    {
        var currentTrackUri = trackUri;
        var currentStartPositionMs = startPositionMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            ITrackStream? trackStream = null;

            try
            {
                _logger?.LogDebug("Starting playback loop for track: {TrackUri} at {PositionMs}ms", currentTrackUri, currentStartPositionMs);

                // Load track from source registry
                trackStream = await _sourceRegistry.LoadAsync(currentTrackUri, cancellationToken);
                _logger?.LogDebug("Track loaded: {Title} by {Artist}", trackStream.Metadata.Title, trackStream.Metadata.Artist);

                // Ensure CDN is initialized before the decoder starts reading.
                // NVorbis reads pages sequentially on demand — the background download
                // provides data progressively, so no full-file download is needed.
                if (trackStream.AudioStream is LazyProgressiveDownloader lazyStream)
                {
                    await lazyStream.PrefetchRangeAsync(0, 1, cancellationToken);
                }

                // Store track metadata
                _currentTrackTitle = trackStream.Metadata.Title;
                _currentTrackArtist = trackStream.Metadata.Artist;
                _currentTrackAlbum = trackStream.Metadata.Album;
                _currentDurationMs = trackStream.Metadata.DurationMs ?? 0;
                _currentCanSeek = trackStream.CanSeek;

                // Store image URLs and URIs for Connect state
                _currentAlbumUri = trackStream.Metadata.AlbumUri;
                _currentArtistUri = trackStream.Metadata.ArtistUri;
                _currentImageSmallUrl = trackStream.Metadata.ImageSmallUrl;
                _currentImageUrl = trackStream.Metadata.ImageUrl;
                _currentImageLargeUrl = trackStream.Metadata.ImageLargeUrl;
                _currentImageXLargeUrl = trackStream.Metadata.ImageXLargeUrl;

                // Find appropriate decoder using registry
                // For non-seekable streams (HTTP radio), this returns a wrapped stream with buffered header
                var decoder = _decoderRegistry.FindDecoder(trackStream.AudioStream, out var decodingStream);
                if (decoder == null)
                {
                    throw new NotSupportedException($"No decoder found for audio format of track: {currentTrackUri}");
                }

                _logger?.LogDebug("Using decoder: {DecoderName}", decoder.FormatName);

                // Get audio format from decoder
                var audioFormat = await decoder.GetFormatAsync(decodingStream, cancellationToken);
                _logger?.LogDebug("Audio format: {SampleRate}Hz {Channels}ch {Bits}bit",
                    audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample);

                // Initialize audio sink and processing chain
                await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: 2000, cancellationToken);
                _audioSink.SetBasePosition(currentStartPositionMs);
                await _processingChain.InitializeAsync(audioFormat, cancellationToken);

                // Set track gain for normalization processor (if exists)
                foreach (var processor in _processingChain.Processors.OfType<NormalizationProcessor>())
                {
                    processor.SetTrackGain(trackStream.Metadata);
                    _logger?.LogInformation(
                        "Normalization: trackGain={TrackGain:F2}dB, albumGain={AlbumGain:F2}dB, peak={Peak:F4}, appliedFactor={Factor:F4} ({FactorDb:F2}dB), enabled={Enabled}",
                        trackStream.Metadata.ReplayGainTrackGain,
                        trackStream.Metadata.ReplayGainAlbumGain,
                        trackStream.Metadata.ReplayGainTrackPeak,
                        processor.CurrentGain,
                        20.0 * Math.Log10(Math.Max(processor.CurrentGain, 0.0001)),
                        processor.IsEnabled);
                }

                // Update state to playing
                _isPlaying = true;
                _isPaused = false;
                _playbackStartedTcs?.TrySetResult();
                PublishStateUpdate();

                // Start playback event tracking
                StartPlaybackSession(currentTrackUri, _currentContextUri ?? currentTrackUri, PlaybackReason.PlayBtn);

                // Nudge background GC to clean up download burst allocations now (non-blocking).
                // Track loading creates many short-lived large buffers; collecting them here
                // prevents pressure from building up and triggering a longer pause mid-playback.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

                // Clear any seek that was queued for a previous track — it must not bleed into this one
                lock (_seekLock)
                    _pendingSeekMs = null;

                // Decode and play using the decoder's async enumerable
                long decodeStartPosition = currentStartPositionMs;
                long lastPeriodicPublishPositionMs = currentStartPositionMs;

                while (!cancellationToken.IsCancellationRequested)
                {
                    bool seekRequested = false;

                    await foreach (var buffer in decoder.DecodeAsync(
                        decodingStream,
                        decodeStartPosition,
                        title =>
                        {
                            // ICY metadata callback - update track title
                            _currentTrackTitle = title;
                            _logger?.LogInformation("Stream title changed: {Title}", title);
                            PublishStateUpdate();
                        },
                        cancellationToken))
                    {
                        // Check for pending seek
                        lock (_seekLock)
                        {
                            if (_pendingSeekMs.HasValue)
                            {
                                decodeStartPosition = _pendingSeekMs.Value;
                                _pendingSeekMs = null;
                                seekRequested = true;

                                _logger?.LogDebug("Seek requested to {PositionMs}ms", decodeStartPosition);
                            }
                        }

                        if (seekRequested)
                        {
                            // Prefetch data at seek position for streaming tracks
                            await trackStream.PrefetchForSeekAsync(
                                TimeSpan.FromMilliseconds(decodeStartPosition),
                                cancellationToken);

                            // Reset stream position if seekable
                            if (decodingStream.CanSeek)
                            {
                                decodingStream.Position = 0;
                            }

                            // Reset sink position tracking to the new seek target
                            _audioSink.SetBasePosition(decodeStartPosition);
                            lastPeriodicPublishPositionMs = decodeStartPosition;

                            break; // Exit decode loop to restart from new position
                        }

                        // Process audio through chain (zero-copy: single pooled buffer, in-place transforms)
                        var processed = _processingChain.Process(buffer);

                        // Write to sink (copies into circular buffer)
                        await _audioSink.WriteAsync(processed.Data, cancellationToken);

                        // Return pooled buffers to ArrayPool.
                        // When the chain is active it copies decoder data into a new pooled buffer,
                        // so we must return both the pipeline buffer AND the decoder's original buffer.
                        processed.Return();
                        if (!ReferenceEquals(processed, buffer))
                            buffer.Return();

                        // Update position from the sink (tracks what's actually been played
                        // through the speakers, not the decode-ahead position)
                        _currentPositionMs = _audioSink.PlaybackPositionMs;

                        // Publish progress at a fixed interval without modulo windows,
                        // which can emit multiple updates around each boundary.
                        if (_currentPositionMs - lastPeriodicPublishPositionMs >= PositionPublishIntervalMs)
                        {
                            lastPeriodicPublishPositionMs = _currentPositionMs;
                            PublishStateUpdate(positionOnly: true);
                            MaybeLogRuntimeHealth();
                        }
                    }

                    // If no seek was requested, we're done with the track
                    if (!seekRequested)
                        break;
                }

                // Drain the audio sink buffer so the last seconds of audio are actually
                // heard through the speakers before we advance to the next track.
                // Without this, the circular buffer (up to 8s of audio) gets discarded.
                await _audioSink.DrainAsync(cancellationToken);

                // Update position one final time after drain completes
                _currentPositionMs = _audioSink.PlaybackPositionMs;
                PublishStateUpdate();

                // Playback completed
                _logger?.LogInformation("Playback completed for track: {TrackUri}", currentTrackUri);

                // Send track transition event
                EndPlaybackSession((int)_currentPositionMs, PlaybackReason.TrackDone);

                // Handle repeat track first
                if (_repeatingTrack)
                {
                    _logger?.LogDebug("Repeat track enabled, restarting");
                    _currentPositionMs = 0;
                    currentStartPositionMs = 0;
                    continue;
                }

                // Try to advance to next track in queue
                var nextTrack = _queue.MoveNext();
                if (nextTrack != null)
                {
                    _logger?.LogDebug("Auto-advancing to next track: {TrackUri}", nextTrack.Uri);

                    // Update state for next track
                    _currentTrackUri = NormalizeToUri(nextTrack.Uri);
                    _currentTrackUid = nextTrack.Uid ?? string.Empty;
                    _currentPositionMs = 0;

                    currentTrackUri = _currentTrackUri;
                    currentStartPositionMs = 0;
                    continue;
                }

                // No next track - handle end of context
                _logger?.LogDebug("End of queue reached, handling end of context");
                await HandleEndOfContextInternalAsync(cancellationToken);
                _isPlaying = false;
                PublishStateUpdate();
                return;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Playback loop cancelled");
                // Send transition event when cancelled (e.g., skip, stop)
                EndPlaybackSession((int)_currentPositionMs, PlaybackReason.EndPlay);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No suitable audio file"))
            {
                // Track is unavailable (region-restricted, no audio files, etc.)
                _logger?.LogWarning("Track unavailable: {TrackUri}, skipping to next", currentTrackUri);

                // Publish error for UI notification
                _errorSubject.OnNext(new PlaybackError(
                    PlaybackErrorType.TrackUnavailable,
                    $"Track unavailable: {currentTrackUri}",
                    ex));

                // Auto-skip to next track
                var nextTrack = _queue.MoveNext();
                if (nextTrack != null)
                {
                    _logger?.LogInformation("Auto-skipping to: {NextTrack}", nextTrack.Uri);
                    _currentTrackUri = NormalizeToUri(nextTrack.Uri);
                    _currentTrackUid = nextTrack.Uid ?? string.Empty;
                    _currentPositionMs = 0;

                    currentTrackUri = _currentTrackUri;
                    currentStartPositionMs = 0;
                    continue;
                }

                // No next track - handle end of context
                _logger?.LogInformation("No more tracks in queue after unavailable track");
                await HandleEndOfContextInternalAsync(cancellationToken);
                _isPlaying = false;
                PublishStateUpdate();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error in playback loop");

                // Emit error to subscribers (UI notification, etc.)
                var errorType = ex switch
                {
                    Core.Audio.AudioKeyException => PlaybackErrorType.TrackUnavailable,
                    NotSupportedException => PlaybackErrorType.DecodeError,
                    System.Net.Http.HttpRequestException => PlaybackErrorType.NetworkError,
                    System.IO.IOException => PlaybackErrorType.NetworkError,
                    _ => PlaybackErrorType.Unknown
                };
                _errorSubject.OnNext(new PlaybackError(errorType, ex.Message, ex));

                // Send transition event on error
                EndPlaybackSession((int)_currentPositionMs, PlaybackReason.EndPlay);
                _isPlaying = false;
                PublishStateUpdate();
                return;
            }
            finally
            {
                if (trackStream != null)
                {
                    await trackStream.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// Emits periodic runtime health counters for long-session diagnostics.
    /// Includes underflow rate and GC collection deltas.
    /// </summary>
    private void MaybeLogRuntimeHealth()
    {
        if (_logger?.IsEnabled(LogLevel.Information) != true)
            return;

        var nowMs = Environment.TickCount64;
        var previousLogAtMs = Volatile.Read(ref _lastRuntimeHealthLogAtMs);
        if (previousLogAtMs != 0 && nowMs - previousLogAtMs < RuntimeHealthLogIntervalMs)
            return;

        if (Interlocked.CompareExchange(ref _lastRuntimeHealthLogAtMs, nowMs, previousLogAtMs) != previousLogAtMs)
            return;

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        var gc0Delta = _lastRuntimeHealthGcGen0 >= 0 ? gc0 - _lastRuntimeHealthGcGen0 : 0;
        var gc1Delta = _lastRuntimeHealthGcGen1 >= 0 ? gc1 - _lastRuntimeHealthGcGen1 : 0;
        var gc2Delta = _lastRuntimeHealthGcGen2 >= 0 ? gc2 - _lastRuntimeHealthGcGen2 : 0;

        _lastRuntimeHealthGcGen0 = gc0;
        _lastRuntimeHealthGcGen1 = gc1;
        _lastRuntimeHealthGcGen2 = gc2;

        long underflowCount = 0;
        if (_audioSink is Sinks.PortAudioSink portAudioSink)
        {
            underflowCount = portAudioSink.UnderrunCount;
        }

        var previousUnderflowCount = Interlocked.Exchange(ref _lastRuntimeHealthUnderflowCount, underflowCount);
        var elapsedMs = previousLogAtMs == 0 ? 0 : nowMs - previousLogAtMs;
        var underflowRatePerMinute = elapsedMs > 0 && previousUnderflowCount >= 0
            ? (underflowCount - previousUnderflowCount) * 60000.0 / elapsedMs
            : 0.0;

        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        _logger.LogInformation(
            "Playback health: position={PositionMs}ms, underflows={Underflows} ({UnderflowRate:F2}/min), gcDelta={Gen0}/{Gen1}/{Gen2}, managed={ManagedMb:F1}MB",
            _currentPositionMs,
            underflowCount,
            underflowRatePerMinute,
            gc0Delta,
            gc1Delta,
            gc2Delta,
            managedMb);
    }

    // ================================================================
    // STATE MANAGEMENT
    // ================================================================

    private void PublishStateUpdate(bool positionOnly = false)
    {
        if (positionOnly)
        {
            LocalPlaybackState positionState;
            lock (_stateLock)
            {
                // Fast path for high-frequency progress ticks: reuse existing state shape
                // and only update volatile playback fields.
                positionState = _currentState with
                {
                    PositionMs = _currentPositionMs,
                    DurationMs = _currentDurationMs,
                    IsPlaying = _isPlaying,
                    IsPaused = _isPaused,
                    IsBuffering = _isReconnecting,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _currentState = positionState;
            }

            _stateChannel.Writer.TryWrite(positionState);
            return;
        }

        // Snapshot volatile data under lock (fast — no LINQ projections)
        QueueTrack? currentTrack;
        IReadOnlyList<QueueTrack> prevRaw, nextRaw;
        string? trackUri, trackUid, contextUri, albumUri, artistUri;
        string? trackTitle, trackArtist, trackAlbum;
        string? imgSmall, imgUrl, imgLarge, imgXLarge;
        long positionMs, durationMs;
        bool isPlaying, isPaused, isBuffering, shuffling, repeatingCtx, repeatingTrack, canSeek;
        int currentIndex;
        string? queueRevision;

        lock (_stateLock)
        {
            currentTrack = _queue.Current;
            prevRaw = _queue.GetPrevTracks();
            nextRaw = _queue.GetNextTracks();
            trackUri = _currentTrackUri;
            trackUid = _currentTrackUid;
            contextUri = _currentContextUri;
            albumUri = _currentAlbumUri ?? currentTrack?.AlbumUri;
            artistUri = _currentArtistUri ?? currentTrack?.ArtistUri;
            trackTitle = _currentTrackTitle;
            trackArtist = _currentTrackArtist;
            trackAlbum = _currentTrackAlbum;
            imgSmall = _currentImageSmallUrl;
            imgUrl = _currentImageUrl;
            imgLarge = _currentImageLargeUrl;
            imgXLarge = _currentImageXLargeUrl;
            positionMs = _currentPositionMs;
            durationMs = _currentDurationMs;
            isPlaying = _isPlaying;
            isPaused = _isPaused;
            isBuffering = _isReconnecting;
            shuffling = _shuffling;
            repeatingCtx = _repeatingContext;
            repeatingTrack = _repeatingTrack;
            canSeek = _currentCanSeek;
            currentIndex = _queue.CurrentIndex;
            queueRevision = _queue.GetQueueRevision();
        }

        // Build TrackReference lists outside the lock (LINQ allocation, no contention)
        var prevTracks = prevRaw
            .Select(t => new TrackReference(t.Uri, t.Uid ?? string.Empty, t.AlbumUri, t.ArtistUri, t.IsUserQueued))
            .ToList();
        var nextTracks = nextRaw
            .Select(t => new TrackReference(t.Uri, t.Uid ?? string.Empty, t.AlbumUri, t.ArtistUri, t.IsUserQueued))
            .ToList();

        var contextUrl = !string.IsNullOrEmpty(contextUri)
            ? $"context://{contextUri}"
            : null;

        var state = new LocalPlaybackState
        {
            TrackUri = trackUri,
            TrackUid = trackUid,
            AlbumUri = albumUri,
            ArtistUri = artistUri,
            TrackTitle = trackTitle,
            TrackArtist = trackArtist,
            TrackAlbum = trackAlbum,
            ImageSmallUrl = imgSmall,
            ImageUrl = imgUrl,
            ImageLargeUrl = imgLarge,
            ImageXLargeUrl = imgXLarge,
            ContextUri = contextUri,
            ContextUrl = contextUrl,
            PositionMs = positionMs,
            DurationMs = durationMs,
            IsPlaying = isPlaying,
            IsPaused = isPaused,
            IsBuffering = isBuffering,
            PlaybackSpeed = 1.0,
            Shuffling = shuffling,
            RepeatingContext = repeatingCtx,
            RepeatingTrack = repeatingTrack,
            CanSeek = canSeek,
            CurrentIndex = currentIndex,
            PrevTracks = prevTracks,
            NextTracks = nextTracks,
            QueueRevision = queueRevision,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        lock (_stateLock)
        {
            _currentState = state;
        }

        // Non-blocking: queue state for publishing on a separate thread
        // so we never block the playback thread waiting for Rx subscribers
        _stateChannel.Writer.TryWrite(state);
    }

    /// <summary>
    /// Background consumer that reads state from the channel and publishes to Rx subscribers.
    /// Runs independently from the playback thread so OnNext never blocks decoding.
    /// </summary>
    private async Task ConsumeStateChannelAsync()
    {
        try
        {
            await foreach (var state in _stateChannel.Reader.ReadAllAsync())
            {
                _stateSubject.OnNext(state);
            }
        }
        catch (ChannelClosedException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "State channel consumer failed");
        }
    }

    private static string GenerateTrackUid(string trackUri)
    {
        // Generate a unique ID for this playback instance
        // Format: <track_uri_hash>_<timestamp>
        var hash = trackUri.GetHashCode();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{hash:X8}_{timestamp}";
    }

    /// <summary>
    /// Checks if a URI is a playable track (Spotify track, local file, or stream).
    /// </summary>
    private static bool IsPlayableTrackUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        // Spotify tracks
        if (uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Local files (file:// URI)
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return true;

        // Absolute file paths (Windows C:\... or Unix /...)
        if (Path.IsPathRooted(uri))
            return true;

        // HTTP streams (for future support)
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // ================================================================
    // COMMAND SUBSCRIPTIONS
    // ================================================================

    private void SubscribeToCommands()
    {
        if (_commandHandler == null)
            return;

        _logger?.LogDebug("Subscribing to ConnectCommandHandler observables");

        // Subscribe to all command streams - each handler executes the command and sends reply
        _subscriptions.Add(_commandHandler.PlayCommands.Subscribe(async cmd =>
        {
            await PlayAsync(cmd, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.PauseCommands.Subscribe(async cmd =>
        {
            await PauseAsync(CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.ResumeCommands.Subscribe(async cmd =>
        {
            await ResumeAsync(CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.SeekCommands.Subscribe(async cmd =>
        {
            await SeekAsync(cmd.PositionMs, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.SkipNextCommands.Subscribe(async cmd =>
        {
            await SkipNextAsync(CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.SkipPrevCommands.Subscribe(async cmd =>
        {
            await SkipPreviousAsync(CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.ShuffleCommands.Subscribe(async cmd =>
        {
            await SetShuffleAsync(cmd.Enabled, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.RepeatContextCommands.Subscribe(async cmd =>
        {
            await SetRepeatContextAsync(cmd.Enabled, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.RepeatTrackCommands.Subscribe(async cmd =>
        {
            await SetRepeatTrackAsync(cmd.Enabled, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        // Queue commands
        _subscriptions.Add(_commandHandler.SetQueueCommands.Subscribe(async cmd =>
        {
            _logger?.LogInformation("SetQueue: {Count} tracks", cmd.TrackUris?.Length ?? 0);
            // TODO: Implement full queue replacement
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _subscriptions.Add(_commandHandler.AddToQueueCommands.Subscribe(async cmd =>
        {
            _logger?.LogInformation("AddToQueue: {Track}", cmd.TrackUri);
            _queue.AddToQueue(new QueueTrack(cmd.TrackUri));
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        // Transfer command - handle playback transfer from another device
        _subscriptions.Add(_commandHandler.TransferCommands.Subscribe(async cmd =>
        {
            _logger?.LogInformation("Transfer command received from {Device}", cmd.SenderDeviceId);
            // TODO: Implement full transfer logic using cmd.TransferState
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        // Update context - refresh context metadata
        _subscriptions.Add(_commandHandler.UpdateContextCommands.Subscribe(async cmd =>
        {
            _logger?.LogInformation("UpdateContext: {Uri}", cmd.ContextUri);
            // TODO: Implement context refresh
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        // Set options - combined shuffle/repeat options
        _subscriptions.Add(_commandHandler.SetOptionsCommands.Subscribe(async cmd =>
        {
            if (cmd.ShufflingContext.HasValue)
                await SetShuffleAsync(cmd.ShufflingContext.Value, CancellationToken.None);
            if (cmd.RepeatingContext.HasValue)
                await SetRepeatContextAsync(cmd.RepeatingContext.Value, CancellationToken.None);
            if (cmd.RepeatingTrack.HasValue)
                await SetRepeatTrackAsync(cmd.RepeatingTrack.Value, CancellationToken.None);
            await _commandHandler.SendReplyAsync(cmd.Key, RequestResult.Success);
        }));

        _logger?.LogInformation("AudioPipeline subscribed to all command streams");
    }

    /// <summary>
    /// Subscribes to volume changes from DeviceStateManager and updates VolumeProcessor.
    /// </summary>
    private void SubscribeToVolumeChanges()
    {
        if (_deviceStateManager == null || _volumeProcessor == null)
            return;

        // Subscribe to volume changes
        _subscriptions.Add(_deviceStateManager.Volume.Subscribe(OnVolumeChanged));

        // Volume is handled by CallbackVolume in the PortAudio sink (instant, no buffer delay).
        // Disable VolumeProcessor to prevent double-attenuation.
        _volumeProcessor.IsEnabled = false;

        // Apply initial volume to the sink callback
        var initialVolume = _deviceStateManager.CurrentVolume / 65535.0f;
        if (initialVolume > 0.001f)
        {
            if (_audioSink is Sinks.PortAudioSink portSink)
                portSink.CallbackVolume = initialVolume;
            _logger?.LogDebug("Initial volume applied: {Percent}%", (int)(initialVolume * 100));
        }
        else
        {
            _logger?.LogDebug("Skipped initial volume (0 = uninitialized), keeping default 1.0");
        }
    }

    /// <summary>
    /// Handles volume changes from DeviceStateManager.
    /// </summary>
    private void OnVolumeChanged(int volume)
    {
        // Skip 0 — DeviceStateManager often emits 0 before real state arrives
        if (volume <= 0) return;

        var linearVolume = volume / 65535.0f;

        if (_audioSink is Sinks.PortAudioSink portSink)
            portSink.CallbackVolume = linearVolume;

        _logger?.LogDebug("Volume applied to audio output: {Percent}%", (int)(linearVolume * 100));
    }

    // ================================================================
    // INTERNAL CONTROL
    // ================================================================

    private async Task StopInternalAsync()
    {
        if (_playbackCts != null)
        {
            _playbackCts.Cancel();
            _playbackCts.Dispose();
            _playbackCts = null;
        }

        if (_playbackTask != null)
        {
            try
            {
                // Wait for playback loop to finish, but cap at 500ms to avoid blocking commands
                var completed = await Task.WhenAny(_playbackTask, Task.Delay(500));
                if (completed != _playbackTask)
                {
                    _logger?.LogWarning("Playback loop did not stop within 500ms, continuing without waiting");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            _playbackTask = null;
        }

        await _audioSink.FlushAsync();

        _isPlaying = false;
        _isPaused = false;
    }

    // ================================================================
    // EVENT REPORTING
    // ================================================================

    /// <summary>
    /// Generates a new session ID (32-char hex string).
    /// </summary>
    private static string GenerateSessionId()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a new playback ID (32-char hex string).
    /// </summary>
    private static string GeneratePlaybackId()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Starts tracking a new playback session and sends events.
    /// </summary>
    private void StartPlaybackSession(string trackUri, string contextUri, PlaybackReason reason)
    {
        if (_eventService == null)
            return;

        // Check if event reporting is enabled for this track source
        if (!ShouldReportPlayback(trackUri))
        {
            _logger?.LogDebug("Skipping playback event reporting for track (disabled by config): {TrackUri}", trackUri);
            return;
        }

        // Check if context changed (new session needed)
        var contextChanged = _currentSessionId == null || _currentContextUri != contextUri;
        if (contextChanged)
        {
            _currentSessionId = GenerateSessionId();
            _eventService.SendEvent(new NewSessionIdEvent(
                _currentSessionId,
                contextUri,
                contextSize: 1)); // TODO: Get actual context size
            _logger?.LogDebug("Sent NewSessionIdEvent: session={SessionId}, context={ContextUri}",
                _currentSessionId, contextUri);
        }

        // Generate new playback ID for this track
        _currentPlaybackId = GeneratePlaybackId();
        _eventService.SendEvent(new NewPlaybackIdEvent(_currentSessionId!, _currentPlaybackId));
        _logger?.LogDebug("Sent NewPlaybackIdEvent: playback={PlaybackId}, session={SessionId}",
            _currentPlaybackId, _currentSessionId);

        // Create metrics for this playback
        _currentMetrics = new PlaybackMetrics(
            trackId: ExtractTrackId(trackUri),
            playbackId: _currentPlaybackId,
            contextUri: contextUri,
            featureVersion: "wavee",
            referrerIdentifier: "");

        _currentMetrics.SourceStart = "context";
        _currentMetrics.ReasonStart = reason;
        _currentMetrics.StartInterval(0);
    }

    /// <summary>
    /// Ends the current playback and sends TrackTransitionEvent.
    /// </summary>
    private void EndPlaybackSession(int endPositionMs, PlaybackReason reason)
    {
        if (_eventService == null || _currentMetrics == null || _currentPlaybackId == null)
            return;

        // End the current interval
        _currentMetrics.EndInterval(endPositionMs);
        _currentMetrics.SourceEnd = "context";
        _currentMetrics.ReasonEnd = reason;

        // Set player metrics with what we know
        _currentMetrics.Player = new PlayerMetrics
        {
            Duration = (int)_currentDurationMs,
            DecodedLength = (int)_currentDurationMs, // Approximate
            Bitrate = 320, // Default OGG Vorbis high quality
            Encoding = "vorbis",
            Transition = "fwdbtn", // TODO: Determine actual transition
            ContentMetrics = new ContentMetrics
            {
                PreloadedAudioKey = true, // TODO: Track actual audio key state
                AudioKeyTime = -1
            }
        };

        // Send the critical TrackTransitionEvent
        _eventService.SendEvent(new TrackTransitionEvent(
            _deviceId,
            _deviceId, // Last command sent by this device
            _currentMetrics));

        _logger?.LogDebug("Sent TrackTransitionEvent: playback={PlaybackId}, endPosition={EndPos}ms, reason={Reason}",
            _currentPlaybackId, endPositionMs, reason);

        // Clear current playback ID (session ID persists until context changes)
        _currentPlaybackId = null;
        _currentMetrics = null;
    }

    /// <summary>
    /// Determines whether playback events should be reported for the given track URI
    /// based on the configured EventReportingOptions.
    /// </summary>
    private bool ShouldReportPlayback(string trackUri)
    {
        // Spotify tracks
        if (trackUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            return _eventReportingOptions.ReportSpotifyTracks;

        // Spotify podcasts/episodes
        if (trackUri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase))
            return _eventReportingOptions.ReportPodcasts;

        // HTTP streams
        if (trackUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trackUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return _eventReportingOptions.ReportHttpStreams;

        // Local files (file:// URI or absolute path)
        if (trackUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(trackUri))
            return _eventReportingOptions.ReportLocalFiles;

        // Unknown source - default to not reporting
        return false;
    }

    /// <summary>
    /// Extracts the track ID from a Spotify URI.
    /// </summary>
    private static string ExtractTrackId(string trackUri)
    {
        // Format: spotify:track:XXXXXXXXXXXXXXXXXXXXXX
        var parts = trackUri.Split(':');
        return parts.Length >= 3 ? parts[2] : trackUri;
    }

    /// <summary>
    /// Normalizes a Spotify URL or URI to a standard URI format.
    /// Converts "https://open.spotify.com/track/xxx" to "spotify:track:xxx".
    /// </summary>
    private static string NormalizeToUri(string uriOrUrl)
    {
        // If it's already a spotify: URI, return as-is
        if (uriOrUrl.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return uriOrUrl;

        // Try to parse as URL and convert to URI
        if (Core.Audio.SpotifyId.TryParse(uriOrUrl, out var spotifyId))
        {
            return spotifyId.ToUri();
        }

        // Return as-is if we can't parse it (let downstream code handle the error)
        return uriOrUrl;
    }

    // Track finding is now handled by ContextResolver.FindTrackIndex (static method)

    // ================================================================
    // DISPOSAL
    // ================================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger?.LogInformation("Disposing AudioPipeline");

        // Signal state channel consumer to exit
        _stateChannel.Writer.TryComplete();

        // Stop playback
        await StopInternalAsync();

        // Dispose subscriptions
        _subscriptions.Dispose();

        // Unsubscribe EQ handler before disposing sink
        if (_userEq != null && _eqFiltersChangedHandler != null)
        {
            _userEq.FiltersChanged -= _eqFiltersChangedHandler;
            _eqFiltersChangedHandler = null;
        }

        // Dispose sink
        await _audioSink.DisposeAsync();

        // Complete state subject
        _stateSubject.OnCompleted();
        _stateSubject.Dispose();

        // Complete error subject
        _errorSubject.OnCompleted();
        _errorSubject.Dispose();

        // Dispose lock
        _commandLock.Dispose();

        // Dispose queue
        _queue.Dispose();

        _logger?.LogInformation("AudioPipeline disposed");
    }
}
