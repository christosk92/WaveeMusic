using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Decoders;
using Wavee.AudioHost.Audio.Processors;
using Wavee.AudioHost.Audio.Streaming;
using Wavee.Playback.Contracts;

namespace Wavee.AudioHost.Audio;

/// <summary>
/// Pure audio engine. Receives a resolved track (URL + key + codec + metadata),
/// downloads → decrypts → decodes → processes → outputs to speakers.
/// No Spotify protocol, no queue management, no Connect awareness.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    private readonly IAudioSink _audioSink;
    private readonly AudioDecoderRegistry _decoderRegistry;
    private readonly AudioProcessingChain _processingChain;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    // Volume/EQ shortcuts
    private readonly VolumeProcessor? _volumeProcessor;
    private readonly EqualizerProcessor? _userEq;

    // State
    private readonly BehaviorSubject<EngineState> _stateSubject;
    private readonly Subject<EngineError> _errorSubject = new();
    private readonly Subject<string> _trackCompletedSubject = new();
    private EngineState _currentState = EngineState.Empty;
    private readonly object _stateLock = new();

    // Playback control
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private long? _pendingSeekMs;
    private readonly object _seekLock = new();
    private bool _disposed;

    // Position publish cadence
    private const long PositionPublishIntervalMs = 2000;

    public AudioEngine(
        IAudioSink audioSink,
        AudioDecoderRegistry decoderRegistry,
        AudioProcessingChain processingChain,
        HttpClient httpClient,
        ILogger? logger = null)
    {
        _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _decoderRegistry = decoderRegistry ?? throw new ArgumentNullException(nameof(decoderRegistry));
        _processingChain = processingChain ?? throw new ArgumentNullException(nameof(processingChain));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;

        _stateSubject = new BehaviorSubject<EngineState>(_currentState);

        _volumeProcessor = _processingChain.Processors.OfType<VolumeProcessor>().FirstOrDefault();
        _userEq = _processingChain.Processors.OfType<EqualizerProcessor>().FirstOrDefault();
    }

    /// <summary>Observable stream of engine state changes.</summary>
    public IObservable<EngineState> StateChanges => _stateSubject;

    /// <summary>Observable stream of playback errors.</summary>
    public IObservable<EngineError> Errors => _errorSubject;

    /// <summary>Fires when a track plays to natural completion (NOT on cancellation/skip).</summary>
    public IObservable<string> TrackCompleted => _trackCompletedSubject;

    /// <summary>Current state snapshot.</summary>
    public EngineState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    /// <summary>Play a fully resolved track (URL + key + codec + metadata).</summary>
    /// <summary>Play with deferred CDN resolution — instant start from head data.</summary>
    public async Task PlayAsync(PlayTrackCommand cmd, Task<DeferredResult> deferredTask, CancellationToken ct = default)
    {
        await StopInternalAsync();

        _logger?.LogInformation("Playing (deferred): {Title} by {Artist} [{Codec}]",
            cmd.Metadata?.Title, cmd.Metadata?.Artist, cmd.Codec);

        _playbackCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_playbackCts.Token, ct);

        _playbackTask = Task.Run(async () =>
        {
            try
            {
                await PlaybackLoopDeferredAsync(cmd, deferredTask, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Playback cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Playback error");
                _errorSubject.OnNext(new EngineError(ex.Message, ex));
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);
    }

    public async Task PlayAsync(PlayResolvedTrackCommand cmd, CancellationToken ct = default)
    {
        // Stop any current playback
        await StopInternalAsync();

        _logger?.LogInformation("Playing: {Title} by {Artist} [{Codec}]",
            cmd.Metadata?.Title, cmd.Metadata?.Artist, cmd.Codec);

        _playbackCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_playbackCts.Token, ct);

        _playbackTask = Task.Run(async () =>
        {
            try
            {
                await PlaybackLoopAsync(cmd, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Playback cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Playback error");
                _errorSubject.OnNext(new EngineError(ex.Message, ex));
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await _audioSink.PauseAsync();
        lock (_stateLock)
        {
            _currentState = _currentState with { IsPlaying = false, IsPaused = true };
        }
        PublishState();
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _audioSink.ResumeAsync();
        lock (_stateLock)
        {
            _currentState = _currentState with { IsPlaying = true, IsPaused = false };
        }
        PublishState();
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        lock (_seekLock)
            _pendingSeekMs = positionMs;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await StopInternalAsync();
        lock (_stateLock)
        {
            _currentState = EngineState.Empty;
        }
        PublishState();
    }

    public Task SetVolumeAsync(float volume, CancellationToken ct = default)
    {
        if (_volumeProcessor != null) _volumeProcessor.Volume = volume;
        return Task.CompletedTask;
    }

    public void SetNormalizationEnabled(bool enabled)
    {
        foreach (var p in _processingChain.Processors.OfType<NormalizationProcessor>())
            p.IsEnabled = enabled;
    }

    public void SetEqualizerEnabled(bool enabled, double[]? bandGains = null)
    {
        if (_userEq == null) return;
        _userEq.IsEnabled = enabled;
        if (bandGains != null && _userEq.Bands.Count >= bandGains.Length)
        {
            for (int i = 0; i < bandGains.Length; i++)
                _userEq.Bands[i].GainDb = bandGains[i];
            _userEq.RefreshFilters();
        }
    }

    // ── Deferred playback loop (instant start from head data) ──

    private async Task PlaybackLoopDeferredAsync(PlayTrackCommand cmd, Task<DeferredResult> deferredTask, CancellationToken ct)
    {
        var startPositionMs = cmd.PositionMs;

        // Update state to buffering
        lock (_stateLock)
        {
            _currentState = new EngineState
            {
                TrackUri = cmd.TrackUri,
                TrackUid = cmd.TrackUid,
                Title = cmd.Metadata?.Title,
                Artist = cmd.Metadata?.Artist,
                Album = cmd.Metadata?.Album,
                AlbumUri = cmd.Metadata?.AlbumUri,
                ArtistUri = cmd.Metadata?.ArtistUri,
                ImageUrl = cmd.Metadata?.ImageUrl,
                ImageLargeUrl = cmd.Metadata?.ImageLargeUrl,
                DurationMs = cmd.DurationMs,
                PositionMs = startPositionMs,
                IsPlaying = false,
                IsPaused = false,
                IsBuffering = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();

        // Parse head data
        byte[]? headData = !string.IsNullOrEmpty(cmd.HeadData)
            ? Convert.FromBase64String(cmd.HeadData) : null;

        // Create LazyProgressiveDownloader — plays head data instantly,
        // awaits deferredTask for CDN URL + key when head exhausted
        // FileId is used only for temp file naming — generate a stable hex ID from track URI
        var trackHash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(cmd.TrackUri ?? "")));
        var fileId = FileId.FromBase16(trackHash.ToLowerInvariant());

        var lazyStream = new LazyProgressiveDownloader(
            headData ?? Array.Empty<byte>(),
            deferredTask,
            _httpClient,
            fileId,
            _logger);

        // Read normalization from head data if available
        if (cmd.NormalizationGain.HasValue)
        {
            foreach (var proc in _processingChain.Processors.OfType<NormalizationProcessor>())
            {
                var meta = new TrackMetadata
                {
                    Uri = cmd.TrackUri,
                    ReplayGainTrackGain = cmd.NormalizationGain.Value,
                    ReplayGainTrackPeak = cmd.NormalizationPeak ?? 1.0f
                };
                proc.SetTrackGain(meta);
            }
        }

        // Decoder reads from lazyStream (head data first, then CDN seamlessly)
        var decoder = _decoderRegistry.FindDecoder(lazyStream, out var decodingStream);
        if (decoder == null)
            throw new NotSupportedException($"No decoder found for codec: {cmd.Codec}");

        _logger?.LogDebug("Using decoder: {DecoderName}", decoder.FormatName);

        var audioFormat = await decoder.GetFormatAsync(decodingStream, ct);
        _logger?.LogDebug("Audio format: {SampleRate}Hz {Channels}ch {Bits}bit",
            audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample);

        await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: 2000, ct);
        _audioSink.SetBasePosition(startPositionMs);
        await _processingChain.InitializeAsync(audioFormat, ct);

        // Now playing
        lock (_stateLock)
        {
            _currentState = _currentState with
            {
                IsPlaying = true,
                IsPaused = false,
                IsBuffering = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();

        lock (_seekLock) _pendingSeekMs = null;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

        // Single decode loop — seek handled inline without restarting
        long lastPublishMs = startPositionMs;

        await foreach (var buffer in decoder.DecodeAsync(
            decodingStream, startPositionMs,
            title => { lock (_stateLock) _currentState = _currentState with { Title = title }; PublishState(); },
            ct))
        {
            // Check for pending seek — call SeekTo on decoder inline
            long? seekTarget;
            lock (_seekLock)
            {
                seekTarget = _pendingSeekMs;
                _pendingSeekMs = null;
            }

            if (seekTarget.HasValue)
            {
                buffer.Return();
                await _audioSink.FlushAsync();
                _audioSink.SetBasePosition(seekTarget.Value);
                lastPublishMs = seekTarget.Value;
                // Decoder seeks via VorbisReader.TimePosition on next ReadSamples call
                decoder.SeekTo(seekTarget.Value);

                // Seek should be visible to UI/Connect immediately, not after interval tick.
                lock (_stateLock)
                {
                    _currentState = _currentState with
                    {
                        PositionMs = seekTarget.Value,
                        IsPlaying = true,
                        IsPaused = false,
                        IsBuffering = false,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
                PublishState();
                continue;
            }

            var processed = _processingChain.Process(buffer);
            await _audioSink.WriteAsync(processed.Data, ct);
            processed.Return();
            if (!ReferenceEquals(processed, buffer)) buffer.Return();

            var positionMs = _audioSink.PlaybackPositionMs;
            lock (_stateLock)
            {
                _currentState = _currentState with
                {
                    PositionMs = positionMs,
                    IsPlaying = true,
                    IsPaused = false,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }

            if (positionMs - lastPublishMs >= PositionPublishIntervalMs)
            {
                lastPublishMs = positionMs;
                PublishState();
            }
        }

        await _audioSink.DrainAsync(ct);

        var finalPosition = _audioSink.PlaybackPositionMs;
        lock (_stateLock)
        {
            _currentState = _currentState with
            {
                PositionMs = finalPosition,
                IsPlaying = false,
                IsPaused = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();
        _logger?.LogInformation("Track finished: {TrackUri}", cmd.TrackUri);
        _trackCompletedSubject.OnNext(cmd.TrackUri);
    }

    // ── Legacy playback loop (full resolution upfront) ──

    private async Task PlaybackLoopAsync(PlayResolvedTrackCommand cmd, CancellationToken ct)
    {
        var startPositionMs = cmd.PositionMs;

        // Update state to buffering
        lock (_stateLock)
        {
            _currentState = new EngineState
            {
                TrackUri = cmd.TrackUri,
                TrackUid = cmd.TrackUid,
                Title = cmd.Metadata?.Title,
                Artist = cmd.Metadata?.Artist,
                Album = cmd.Metadata?.Album,
                AlbumUri = cmd.Metadata?.AlbumUri,
                ArtistUri = cmd.Metadata?.ArtistUri,
                ImageUrl = cmd.Metadata?.ImageUrl,
                ImageLargeUrl = cmd.Metadata?.ImageLargeUrl,
                DurationMs = cmd.DurationMs,
                PositionMs = startPositionMs,
                IsPlaying = false,
                IsPaused = false,
                IsBuffering = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();

        // Download audio
        _logger?.LogDebug("Downloading audio from CDN...");
        var response = await _httpClient.GetAsync(cmd.CdnUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var httpStream = await response.Content.ReadAsStreamAsync(ct);

        // Wrap in buffered stream for progressive reading
        var bufferedStream = new BufferedHttpStream(httpStream);

        // Decrypt
        byte[]? audioKey = null;
        if (!string.IsNullOrEmpty(cmd.AudioKey))
            audioKey = Convert.FromBase64String(cmd.AudioKey);

        await using var decryptStream = new AudioDecryptStream(audioKey, bufferedStream);

        // Decode
        var decoder = _decoderRegistry.FindDecoder(decryptStream, out var decodingStream);
        if (decoder == null)
            throw new NotSupportedException($"No decoder found for codec: {cmd.Codec}");

        _logger?.LogDebug("Using decoder: {DecoderName}", decoder.FormatName);

        var audioFormat = await decoder.GetFormatAsync(decodingStream, ct);
        _logger?.LogDebug("Audio format: {SampleRate}Hz {Channels}ch {Bits}bit",
            audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample);

        // Initialize sink and processing chain
        await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: (int)2000, ct);
        _audioSink.SetBasePosition(startPositionMs);
        await _processingChain.InitializeAsync(audioFormat, ct);

        // Set normalization gain if provided
        if (cmd.NormalizationGain.HasValue)
        {
            foreach (var proc in _processingChain.Processors.OfType<NormalizationProcessor>())
            {
                var meta = new TrackMetadata
                {
                    Uri = cmd.TrackUri,
                    ReplayGainTrackGain = cmd.NormalizationGain.Value,
                    ReplayGainTrackPeak = cmd.NormalizationPeak ?? 1.0f
                };
                proc.SetTrackGain(meta);
            }
        }

        // Now playing
        lock (_stateLock)
        {
            _currentState = _currentState with
            {
                IsPlaying = true,
                IsPaused = false,
                IsBuffering = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();

        // Clear stale seeks
        lock (_seekLock)
            _pendingSeekMs = null;

        // GC after download burst
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

        // Decode → process → output loop
        long decodeStartPosition = startPositionMs;
        long lastPublishMs = startPositionMs;

        while (!ct.IsCancellationRequested)
        {
            bool seekRequested = false;

            await foreach (var buffer in decoder.DecodeAsync(
                decodingStream,
                decodeStartPosition,
                title =>
                {
                    // ICY metadata callback for radio streams
                    lock (_stateLock)
                        _currentState = _currentState with { Title = title };
                    PublishState();
                },
                ct))
            {
                // Check for seek
                lock (_seekLock)
                {
                    if (_pendingSeekMs.HasValue)
                    {
                        decodeStartPosition = _pendingSeekMs.Value;
                        _pendingSeekMs = null;
                        seekRequested = true;
                    }
                }

                if (seekRequested)
                {
                    if (decodingStream.CanSeek)
                        decodingStream.Position = 0;
                    _audioSink.SetBasePosition(decodeStartPosition);
                    lastPublishMs = decodeStartPosition;

                    // Emit seek result immediately so state consumers don't wait for cadence tick.
                    lock (_stateLock)
                    {
                        _currentState = _currentState with
                        {
                            PositionMs = decodeStartPosition,
                            IsPlaying = true,
                            IsPaused = false,
                            IsBuffering = false,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                    }
                    PublishState();

                    buffer.Return();
                    break;
                }

                // Process (volume, EQ, normalization, etc.)
                var processed = _processingChain.Process(buffer);

                // Output to speakers
                await _audioSink.WriteAsync(processed.Data, ct);

                // Return pooled buffers
                processed.Return();
                if (!ReferenceEquals(processed, buffer))
                    buffer.Return();

                // Update position from sink (what's actually been heard)
                var positionMs = _audioSink.PlaybackPositionMs;
                lock (_stateLock)
                {
                    _currentState = _currentState with
                    {
                        PositionMs = positionMs,
                        IsPlaying = true,
                        IsPaused = false,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }

                if (positionMs - lastPublishMs >= PositionPublishIntervalMs)
                {
                    lastPublishMs = positionMs;
                    PublishState();
                }
            }

            if (!seekRequested)
                break;
        }

        // Drain remaining audio from sink buffer
        await _audioSink.DrainAsync(ct);

        // Final state update
        var finalPosition = _audioSink.PlaybackPositionMs;
        lock (_stateLock)
        {
            _currentState = _currentState with
            {
                PositionMs = finalPosition,
                IsPlaying = false,
                IsPaused = false,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();

        _logger?.LogInformation("Track finished: {TrackUri}", cmd.TrackUri);
        _trackCompletedSubject.OnNext(cmd.TrackUri);
    }

    // ── Helpers ──

    private void PublishState()
    {
        EngineState snapshot;
        lock (_stateLock)
            snapshot = _currentState;
        _stateSubject.OnNext(snapshot);
    }

    private async Task StopInternalAsync()
    {
        if (_playbackCts != null)
        {
            await _playbackCts.CancelAsync();
            if (_playbackTask != null)
            {
                try { await _playbackTask; }
                catch { /* swallow cancellation */ }
            }
            _playbackCts.Dispose();
            _playbackCts = null;
            _playbackTask = null;
        }
        await _audioSink.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopInternalAsync();
        _stateSubject.Dispose();
        _errorSubject.Dispose();
    }
}

/// <summary>Engine state snapshot — what's currently happening in the audio pipeline.</summary>
public sealed record EngineState
{
    public string? TrackUri { get; init; }
    public string? TrackUid { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumUri { get; init; }
    public string? ArtistUri { get; init; }
    public string? ImageUrl { get; init; }
    public string? ImageLargeUrl { get; init; }
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public bool IsBuffering { get; init; }
    public long Timestamp { get; init; }

    public static EngineState Empty => new()
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

/// <summary>Playback error info.</summary>
public sealed record EngineError(string Message, Exception? Exception = null);
