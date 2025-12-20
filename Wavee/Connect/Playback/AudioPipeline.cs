using System.Buffers;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using NVorbis;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Connect.Protocol;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Processors;
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
    private readonly EventService? _eventService;
    private readonly ContextResolver? _contextResolver;
    private readonly string _deviceId;
    private readonly ILogger? _logger;

    // State management
    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject;
    private readonly Subject<PlaybackError> _errorSubject = new();
    private LocalPlaybackState _currentState = LocalPlaybackState.Empty;
    private readonly object _stateLock = new();

    // Playback control
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    // In-place seeking
    private long? _pendingSeekMs;
    private readonly object _seekLock = new();

    // Playback state
    private string? _currentTrackUri;
    private string? _currentTrackUid;
    private string? _currentContextUri;
    private long _currentPositionMs;
    private long _currentDurationMs;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _shuffling;
    private bool _repeatingContext;
    private bool _repeatingTrack;

    // Event tracking for playback reporting
    private string? _currentSessionId;
    private string? _currentPlaybackId;
    private PlaybackMetrics? _currentMetrics;

    // Queue management
    private readonly PlaybackQueue _queue;
    private string? _nextPageUrl;  // For lazy loading more tracks

    // Command subscriptions
    private readonly CompositeDisposable _subscriptions = new();

    // Disposal
    private bool _disposed;

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
    /// <param name="logger">Optional logger.</param>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        string deviceId = "",
        EventService? eventService = null,
        ContextResolver? contextResolver = null,
        ILogger? logger = null)
    {
        _sourceRegistry = sourceRegistry ?? throw new ArgumentNullException(nameof(sourceRegistry));
        _decoderRegistry = decoderRegistry ?? throw new ArgumentNullException(nameof(decoderRegistry));
        _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _processingChain = processingChain ?? throw new ArgumentNullException(nameof(processingChain));
        _deviceId = deviceId ?? "";
        _eventService = eventService;
        _contextResolver = contextResolver;
        _logger = logger;

        _stateSubject = new BehaviorSubject<LocalPlaybackState>(_currentState);
        _queue = new PlaybackQueue(logger);

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
    /// <param name="deviceId">Device ID for event reporting.</param>
    /// <param name="eventService">Optional event service for reporting playback events.</param>
    /// <param name="contextResolver">Optional context resolver for playlist/album loading.</param>
    /// <param name="logger">Optional logger.</param>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        ConnectCommandHandler commandHandler,
        string deviceId = "",
        EventService? eventService = null,
        ContextResolver? contextResolver = null,
        ILogger? logger = null)
        : this(sourceRegistry, decoderRegistry, audioSink, processingChain, deviceId, eventService, contextResolver, logger)
    {
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        SubscribeToCommands();
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

    /// <inheritdoc/>
    public async Task PlayAsync(PlayCommand command, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Play command received: Track={TrackUri}, Context={ContextUri}, Position={PositionMs}",
                command.TrackUri, command.ContextUri, command.PositionMs);

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

            // Update playback options from command
            if (command.Options != null)
            {
                _shuffling = command.Options.ShufflingContext;
                _repeatingContext = command.Options.RepeatingContext;
                _repeatingTrack = command.Options.RepeatingTrack;
            }

            // Store context and track info
            _currentContextUri = command.ContextUri;
            _currentTrackUri = trackUri;
            _currentTrackUid = string.Empty; // TODO: uids in spotify are weird.. They are part of either an album or a playlist.
            //_currentTrackUid = GenerateTrackUid(trackUri);
            _currentPositionMs = command.PositionMs ?? 0;

            // Setup queue
            // For single track playback (no context), create a single-item queue
            // For context playback, load tracks from context resolver
            if (!string.IsNullOrEmpty(command.ContextUri) && _contextResolver != null)
            {
                try
                {
                    // Load context tracks with batch metadata enrichment
                    var result = await _contextResolver.LoadContextAsync(
                        command.ContextUri,
                        maxTracks: 100,  // Initial load, more via lazy loading
                        enrichMetadata: true,
                        cancellationToken);

                    _queue.SetContext(
                        command.ContextUri,
                        isInfinite: result.IsInfinite,
                        totalTracks: result.TotalCount);

                    _queue.SetTracks(result.Tracks, startIndex: command.SkipToIndex ?? 0);
                    _nextPageUrl = result.NextPageUrl;

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
                    }
                    else
                    {
                        _logger?.LogError("No playable tracks in context: {ContextUri}", command.ContextUri);
                        return;
                    }

                    _logger?.LogInformation("Context loaded: {TrackCount} tracks, playing {TrackUri}",
                        result.Tracks.Count, trackUri);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load context {ContextUri}, falling back to single track",
                        command.ContextUri);
                    // Fall back to single track playback
                    _queue.Clear();
                    _queue.SetTracks([new QueueTrack(trackUri)], startIndex: 0);
                }
            }
            else if (!string.IsNullOrEmpty(command.ContextUri))
            {
                // Context provided but no resolver - just add the track
                _queue.SetContext(command.ContextUri, isInfinite: false, totalTracks: null);
                _queue.SetTracks([new QueueTrack(trackUri)], startIndex: 0);
            }
            else
            {
                // Single track playback - add to queue
                _queue.Clear();
                _queue.SetTracks([new QueueTrack(trackUri)], startIndex: 0);
            }

            // Sync shuffle state with queue
            _queue.SetShuffle(_shuffling);

            // Start playback loop
            _playbackCts = new CancellationTokenSource();
            var playbackToken = _playbackCts.Token; // Capture token value (struct) before Task.Run
            _playbackTask = Task.Run(() => PlaybackLoopAsync(trackUri, _currentPositionMs, playbackToken));

            // Send success reply if command handler exists
            if (_commandHandler != null && !string.IsNullOrEmpty(command.Key))
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
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Pause command received");

            if (!_isPlaying || _isPaused)
            {
                _logger?.LogDebug("Already paused or not playing");
                return;
            }

            await _audioSink.PauseAsync();

            _isPaused = true;
            _isPlaying = false;

            PublishStateUpdate();
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

            // Flush and pause the audio sink
            await _audioSink.FlushAsync();
            await _audioSink.PauseAsync();

            // Reset state
            _isPlaying = false;
            _isPaused = false;
            _currentTrackUri = null;
            _currentTrackUid = null;
            _currentContextUri = null;
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
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Resume command received");

            // Check if playback loop ended (track finished) but we have a track to resume
            if (!_isPaused && !string.IsNullOrEmpty(_currentTrackUri) &&
                (_playbackTask == null || _playbackTask.IsCompleted))
            {
                _logger?.LogDebug("Track ended, restarting from beginning");

                // Clean up completed task
                _playbackCts?.Dispose();
                _playbackCts = null;
                _playbackTask = null;

                // Restart from beginning (or current position if not at end)
                var startPosition = _currentPositionMs >= _currentDurationMs ? 0 : _currentPositionMs;
                _currentPositionMs = startPosition;

                _playbackCts = new CancellationTokenSource();
                var playbackToken = _playbackCts.Token;
                _playbackTask = Task.Run(() => PlaybackLoopAsync(_currentTrackUri, startPosition, playbackToken));

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

    /// <inheritdoc/>
    public async Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Seek command received: Position={PositionMs}ms", positionMs);

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
                _playbackTask = Task.Run(() => PlaybackLoopAsync(_currentTrackUri, positionMs, playbackToken));

                PublishStateUpdate();
                return;
            }

            // Signal seek to active playback loop
            lock (_seekLock)
            {
                _pendingSeekMs = positionMs;
            }

            // Flush audio sink buffer for immediate effect
            await _audioSink.FlushAsync();

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
            _playbackTask = Task.Run(() => PlaybackLoopAsync(trackUri, 0, playbackToken));
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
            _playbackTask = Task.Run(() => PlaybackLoopAsync(trackUri, 0, playbackToken));
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
                _playbackTask = Task.Run(() => PlaybackLoopAsync(_currentTrackUri, 0, playbackToken));
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

    private async Task PlaybackLoopAsync(string trackUri, long startPositionMs, CancellationToken cancellationToken)
    {
        ITrackStream? trackStream = null;
        VorbisReader? vorbisReader = null;
        SkipStream? skipStream = null;

        try
        {
            _logger?.LogDebug("Starting playback loop for track: {TrackUri} at {PositionMs}ms", trackUri, startPositionMs);

            // Load track
            trackStream = await _sourceRegistry.LoadAsync(trackUri, cancellationToken);
            _logger?.LogDebug("Track loaded: {Title} by {Artist}", trackStream.Metadata.Title, trackStream.Metadata.Artist);

            // Store track duration from metadata
            _currentDurationMs = trackStream.Metadata.DurationMs ?? 0;

            // Create VorbisReader directly (skip Spotify header)
            skipStream = new SkipStream(trackStream.AudioStream, VorbisDecoder.SpotifyHeaderSize, leaveOpen: true);
            vorbisReader = new VorbisReader(skipStream, closeOnDispose: false);

            var audioFormat = new AudioFormat(vorbisReader.SampleRate, vorbisReader.Channels, 16);
            _logger?.LogDebug("VorbisReader created: {SampleRate}Hz {Channels}ch",
                vorbisReader.SampleRate, vorbisReader.Channels);

            // Initialize audio sink and processing chain
            await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: 100, cancellationToken);
            await _processingChain.InitializeAsync(audioFormat, cancellationToken);

            // Set track gain for normalization processor (if exists)
            foreach (var processor in _processingChain.Processors.OfType<NormalizationProcessor>())
            {
                processor.SetTrackGain(trackStream.Metadata);
            }

            // Seek to start position if specified
            if (startPositionMs > 0)
            {
                vorbisReader.TimePosition = TimeSpan.FromMilliseconds(startPositionMs);
                _logger?.LogDebug("Initial seek to {PositionMs}ms", startPositionMs);
            }

            // Update state to playing
            _isPlaying = true;
            _isPaused = false;
            PublishStateUpdate();

            // Start playback event tracking
            StartPlaybackSession(trackUri, _currentContextUri ?? trackUri, PlaybackReason.PlayBtn);

            // Allocate decode buffers
            const int samplesPerBuffer = 4096;
            var floatBuffer = ArrayPool<float>.Shared.Rent(samplesPerBuffer * vorbisReader.Channels);
            var pcmBuffer = ArrayPool<byte>.Shared.Rent(samplesPerBuffer * vorbisReader.Channels * 2);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // CHECK FOR PENDING SEEK
                    long? seekTarget;
                    lock (_seekLock)
                    {
                        seekTarget = _pendingSeekMs;
                        _pendingSeekMs = null;
                    }

                    if (seekTarget.HasValue)
                    {
                        // In-place seek using VorbisReader.TimePosition
                        _logger?.LogDebug("In-place seek to {PositionMs}ms", seekTarget.Value);
                        vorbisReader.TimePosition = TimeSpan.FromMilliseconds(seekTarget.Value);
                        _currentPositionMs = seekTarget.Value;
                        continue; // Skip to next iteration
                    }

                    // Read samples from Vorbis decoder
                    var samplesRead = vorbisReader.ReadSamples(floatBuffer, 0, samplesPerBuffer * vorbisReader.Channels);
                    if (samplesRead == 0)
                        break; // End of stream

                    // Convert float samples to 16-bit PCM
                    var pcmBytes = samplesRead * 2;
                    ConvertFloatToPcm16(floatBuffer.AsSpan(0, samplesRead), pcmBuffer.AsSpan(0, pcmBytes));

                    // Get current position
                    var positionMs = (long)vorbisReader.TimePosition.TotalMilliseconds;

                    // Copy PCM data for AudioBuffer (since we reuse the pooled array)
                    var bufferCopy = new byte[pcmBytes];
                    pcmBuffer.AsSpan(0, pcmBytes).CopyTo(bufferCopy);

                    var buffer = new AudioBuffer(bufferCopy, positionMs);

                    // Process audio through chain
                    var processed = _processingChain.Process(buffer);

                    // Write to sink
                    await _audioSink.WriteAsync(processed.Data, cancellationToken);

                    // Update position
                    _currentPositionMs = positionMs;

                    // Publish state update periodically (every ~500ms)
                    if (_currentPositionMs % 500 < 100)
                    {
                        PublishStateUpdate();
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floatBuffer);
                ArrayPool<byte>.Shared.Return(pcmBuffer);
            }

            // Playback completed
            _logger?.LogInformation("Playback completed for track: {TrackUri}", trackUri);

            // Send track transition event
            EndPlaybackSession((int)_currentPositionMs, PlaybackReason.TrackDone);

            // Handle repeat track first
            if (_repeatingTrack)
            {
                _logger?.LogDebug("Repeat track enabled, restarting");
                // Reset seek position and restart
                lock (_seekLock)
                {
                    _pendingSeekMs = 0;
                }
                vorbisReader.TimePosition = TimeSpan.Zero;
                await PlaybackLoopAsync(trackUri, 0, cancellationToken);
                return;
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

                // Continue playing next track (recursive call)
                await PlaybackLoopAsync(_currentTrackUri, 0, cancellationToken);
                return;
            }

            // No next track - handle end of context
            _logger?.LogDebug("End of queue reached, handling end of context");
            await HandleEndOfContextInternalAsync(cancellationToken);
            _isPlaying = false;
            PublishStateUpdate();
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Playback loop cancelled");
            // Send transition event when cancelled (e.g., skip, stop)
            EndPlaybackSession((int)_currentPositionMs, PlaybackReason.EndPlay);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in playback loop");
            // Send transition event on error
            EndPlaybackSession((int)_currentPositionMs, PlaybackReason.EndPlay);
            _isPlaying = false;
            PublishStateUpdate();
        }
        finally
        {
            vorbisReader?.Dispose();
            skipStream?.Dispose();
            if (trackStream != null)
            {
                await trackStream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Converts float samples (-1.0 to 1.0) to 16-bit PCM.
    /// </summary>
    private static void ConvertFloatToPcm16(ReadOnlySpan<float> floatSamples, Span<byte> pcmOutput)
    {
        for (int i = 0; i < floatSamples.Length; i++)
        {
            // Clamp to [-1, 1] range
            var sample = Math.Clamp(floatSamples[i], -1f, 1f);

            // Convert to 16-bit signed integer
            var pcmSample = (short)(sample * 32767f);

            // Write as little-endian
            var offset = i * 2;
            pcmOutput[offset] = (byte)(pcmSample & 0xFF);
            pcmOutput[offset + 1] = (byte)((pcmSample >> 8) & 0xFF);
        }
    }

    // ================================================================
    // STATE MANAGEMENT
    // ================================================================

    private void PublishStateUpdate()
    {
        lock (_stateLock)
        {
            _currentState = new LocalPlaybackState
            {
                TrackUri = _currentTrackUri,
                TrackUid = _currentTrackUid,
                ContextUri = _currentContextUri,
                PositionMs = _currentPositionMs,
                DurationMs = _currentDurationMs,
                IsPlaying = _isPlaying,
                IsPaused = _isPaused,
                IsBuffering = false,
                PlaybackSpeed = 1.0,
                Shuffling = _shuffling,
                RepeatingContext = _repeatingContext,
                RepeatingTrack = _repeatingTrack,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _stateSubject.OnNext(_currentState);
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

    // ================================================================
    // COMMAND SUBSCRIPTIONS
    // ================================================================

    private void SubscribeToCommands()
    {
        if (_commandHandler == null)
            return;

        _logger?.LogDebug("Subscribing to ConnectCommandHandler observables");

        // Subscribe to all command streams
        _subscriptions.Add(_commandHandler.PlayCommands.Subscribe(async cmd =>
            await PlayAsync(cmd, CancellationToken.None)));

        _subscriptions.Add(_commandHandler.PauseCommands.Subscribe(async _ =>
            await PauseAsync(CancellationToken.None)));

        _subscriptions.Add(_commandHandler.ResumeCommands.Subscribe(async _ =>
            await ResumeAsync(CancellationToken.None)));

        _subscriptions.Add(_commandHandler.SeekCommands.Subscribe(async cmd =>
            await SeekAsync(cmd.PositionMs, CancellationToken.None)));

        _subscriptions.Add(_commandHandler.SkipNextCommands.Subscribe(async _ =>
            await SkipNextAsync(CancellationToken.None)));

        _subscriptions.Add(_commandHandler.SkipPrevCommands.Subscribe(async _ =>
            await SkipPreviousAsync(CancellationToken.None)));

        _subscriptions.Add(_commandHandler.ShuffleCommands.Subscribe(async cmd =>
            await SetShuffleAsync(cmd.Enabled, CancellationToken.None)));

        _subscriptions.Add(_commandHandler.RepeatContextCommands.Subscribe(async cmd =>
            await SetRepeatContextAsync(cmd.Enabled, CancellationToken.None)));

        _subscriptions.Add(_commandHandler.RepeatTrackCommands.Subscribe(async cmd =>
            await SetRepeatTrackAsync(cmd.Enabled, CancellationToken.None)));

        _logger?.LogInformation("AudioPipeline subscribed to all command streams");
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
                await _playbackTask;
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

    // ================================================================
    // DISPOSAL
    // ================================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger?.LogInformation("Disposing AudioPipeline");

        // Stop playback
        await StopInternalAsync();

        // Dispose subscriptions
        _subscriptions.Dispose();

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
