using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Commands;
using Wavee.Connect.Protocol;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Processors;

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
    private readonly ILogger? _logger;

    // State management
    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject;
    private LocalPlaybackState _currentState = LocalPlaybackState.Empty;
    private readonly object _stateLock = new();

    // Playback control
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

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

    // Command subscriptions
    private readonly CompositeDisposable _subscriptions = new();

    // Disposal
    private bool _disposed;

    /// <summary>
    /// Creates an AudioPipeline without command handler integration (manual control only).
    /// </summary>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        ILogger? logger = null)
    {
        _sourceRegistry = sourceRegistry ?? throw new ArgumentNullException(nameof(sourceRegistry));
        _decoderRegistry = decoderRegistry ?? throw new ArgumentNullException(nameof(decoderRegistry));
        _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _processingChain = processingChain ?? throw new ArgumentNullException(nameof(processingChain));
        _logger = logger;

        _stateSubject = new BehaviorSubject<LocalPlaybackState>(_currentState);
    }

    /// <summary>
    /// Creates an AudioPipeline with command handler integration (auto-subscribes to commands).
    /// </summary>
    public AudioPipeline(
        TrackSourceRegistry sourceRegistry,
        AudioDecoderRegistry decoderRegistry,
        IAudioSink audioSink,
        AudioProcessingChain processingChain,
        ConnectCommandHandler commandHandler,
        ILogger? logger = null)
        : this(sourceRegistry, decoderRegistry, audioSink, processingChain, logger)
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
            _currentTrackUid = GenerateTrackUid(trackUri);
            _currentPositionMs = command.PositionMs ?? 0;

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
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Resume command received");

            if (!_isPaused)
            {
                _logger?.LogDebug("Not paused, nothing to resume");
                return;
            }

            await _audioSink.ResumeAsync();

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

            // Stop current playback
            await StopInternalAsync();

            // Restart playback from new position
            _currentPositionMs = positionMs;
            _playbackCts = new CancellationTokenSource();
            _playbackTask = Task.Run(() => PlaybackLoopAsync(_currentTrackUri, positionMs, _playbackCts.Token), _playbackCts.Token);
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

            // TODO: Implement queue integration
            // For now, just stop playback
            await StopInternalAsync();

            _logger?.LogWarning("Skip next: Queue not implemented yet, stopping playback");
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
            if (_currentPositionMs > 3000 && !string.IsNullOrEmpty(_currentTrackUri))
            {
                await SeekAsync(0, cancellationToken);
                return;
            }

            // TODO: Implement queue integration
            // For now, just restart current track
            await SeekAsync(0, cancellationToken);

            _logger?.LogWarning("Skip previous: Queue not implemented yet, restarting track");
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
    // PLAYBACK LOOP - Core audio processing
    // ================================================================

    private async Task PlaybackLoopAsync(string trackUri, long startPositionMs, CancellationToken cancellationToken)
    {
        ITrackStream? trackStream = null;
        IAudioDecoder? decoder = null;

        try
        {
            _logger?.LogDebug("Starting playback loop for track: {TrackUri} at {PositionMs}ms", trackUri, startPositionMs);

            // Load track
            trackStream = await _sourceRegistry.LoadAsync(trackUri, cancellationToken);
            _logger?.LogDebug("Track loaded: {Title} by {Artist}", trackStream.Metadata.Title, trackStream.Metadata.Artist);

            // Store track duration from metadata
            _currentDurationMs = trackStream.Metadata.DurationMs ?? 0;

            // Detect format and get decoder
            var (selectedDecoder, audioFormat) = await _decoderRegistry.DetectFormatAsync(
                trackStream.AudioStream,
                cancellationToken);

            decoder = selectedDecoder;
            _logger?.LogDebug("Decoder selected: {DecoderName}, Format: {SampleRate}Hz {Channels}ch {BitsPerSample}bit",
                decoder.FormatName, audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample);

            // Initialize audio sink and processing chain
            await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: 100, cancellationToken);
            await _processingChain.InitializeAsync(audioFormat, cancellationToken);

            // Set track gain for normalization processor (if exists)
            foreach (var processor in _processingChain.Processors.OfType<NormalizationProcessor>())
            {
                processor.SetTrackGain(trackStream.Metadata);
            }

            // Update state to playing
            _isPlaying = true;
            _isPaused = false;
            PublishStateUpdate();

            // Decode and play
            var targetPositionReached = startPositionMs == 0;
            await foreach (var buffer in decoder.DecodeAsync(trackStream.AudioStream, cancellationToken))
            {
                // Skip buffers until we reach target position (for seek)
                if (!targetPositionReached)
                {
                    if (buffer.PositionMs >= startPositionMs)
                    {
                        targetPositionReached = true;
                    }
                    else
                    {
                        continue; // Skip this buffer
                    }
                }

                // Process audio through chain
                var processed = _processingChain.Process(buffer);

                // Write to sink
                await _audioSink.WriteAsync(processed.Data, cancellationToken);

                // Update position
                _currentPositionMs = processed.PositionMs;

                // Publish state update periodically (every ~500ms)
                if (_currentPositionMs % 500 < 100)
                {
                    PublishStateUpdate();
                }
            }

            // Playback completed
            _logger?.LogInformation("Playback completed for track: {TrackUri}", trackUri);

            // Handle repeat
            if (_repeatingTrack)
            {
                _logger?.LogDebug("Repeat track enabled, restarting");
                await PlaybackLoopAsync(trackUri, 0, cancellationToken);
                return;
            }

            // TODO: Handle queue advance (skip to next track)
            _isPlaying = false;
            PublishStateUpdate();
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Playback loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in playback loop");
            _isPlaying = false;
            PublishStateUpdate();
        }
        finally
        {
            if (trackStream != null)
            {
                await trackStream.DisposeAsync();
            }
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

        // Dispose lock
        _commandLock.Dispose();

        _logger?.LogInformation("AudioPipeline disposed");
    }
}
