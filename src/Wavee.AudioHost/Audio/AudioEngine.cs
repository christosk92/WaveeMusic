using System.Diagnostics;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Decoders;
using Wavee.AudioHost.Audio.Processors;
using Wavee.AudioHost.Audio.Sinks;
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

    // Audio cache directory — passed to LazyProgressiveDownloader so it can:
    //   a) Persist newly downloaded tracks for future cache hits
    //   b) Open locally cached files when LocalCacheFileId is set in DeferredResult
    private readonly string? _audioCacheDirectory;

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

    // Position publish cadence — how often the playback loop fires a
    // position-only IPC StateUpdate to the UI process while a track is playing.
    //
    // The UI's PlayerBarViewModel runs a 1 Hz interpolation timer that fills
    // the gap between authoritative ticks via wall-clock-based extrapolation
    // (see ViewModels/PlayerBarViewModel.cs:209). PlaybackStateService also
    // suppresses sub-250 ms drift corrections from incoming ticks
    // (Data/Contexts/PlaybackStateService.cs:408). Together this means the
    // user perceives no jump or jolt when the next authoritative tick lands —
    // even after several seconds of pure interpolation — because the audio
    // engine's drift vs wall clock stays well under that 250 ms threshold.
    //
    // Cadence is therefore a pure CPU vs IPC-traffic dial. Each tick costs
    // a serialize + named-pipe write on this side, then a deserialize +
    // dispatcher hop + Position-binding cascade (Slider thumb recompute,
    // composition layout/paint) on the UI side. 5 s halves it vs the prior
    // 2 s with no UX impact.
    private const long PositionPublishIntervalMs = 5000;

    // Per-seek sequence counter for [seek-trace] correlation across phases and components.
    private static int _seekTraceSeq;

    public AudioEngine(
        IAudioSink audioSink,
        AudioDecoderRegistry decoderRegistry,
        AudioProcessingChain processingChain,
        HttpClient httpClient,
        VolumeProcessor? volumeProcessor = null,
        ILogger? logger = null,
        string? audioCacheDirectory = null)
    {
        _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _decoderRegistry = decoderRegistry ?? throw new ArgumentNullException(nameof(decoderRegistry));
        _processingChain = processingChain ?? throw new ArgumentNullException(nameof(processingChain));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _audioCacheDirectory = audioCacheDirectory;

        if (audioCacheDirectory != null)
            Wavee.Playback.Contracts.AudioFileCache.EnsureDirectoryExists(audioCacheDirectory);

        _stateSubject = new BehaviorSubject<EngineState>(_currentState);

        _volumeProcessor = volumeProcessor ?? _processingChain.Processors.OfType<VolumeProcessor>().FirstOrDefault();
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
                _logger?.LogDebug("[AudioEngine] Playback cancelled: {Title} ({Uri})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.TrackUri ?? "<none>");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[AudioEngine] Playback error for {Title} ({Uri})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.TrackUri ?? "<none>");
                _errorSubject.OnNext(new EngineError(ex.Message, ex));
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>Play a local audio file from disk. No CDN, no audio key, no Spotify metadata.</summary>
    public async Task PlayAsync(PlayLocalFileCommand cmd, CancellationToken ct = default)
    {
        await StopInternalAsync();

        _logger?.LogInformation("Playing local file: {Title} ({Path})",
            cmd.Metadata?.Title, cmd.FilePath);

        _playbackCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_playbackCts.Token, ct);

        _playbackTask = Task.Run(async () =>
        {
            try
            {
                await PlaybackLoopLocalAsync(cmd, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("[AudioEngine] Local playback cancelled: {Title} ({Uri})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.TrackUri);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[AudioEngine] Local playback error for {Title} ({Path})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.FilePath);
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
                _logger?.LogDebug("[AudioEngine] Playback cancelled: {Title} ({Uri})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.TrackUri ?? "<none>");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[AudioEngine] Playback error for {Title} ({Uri})",
                    cmd.Metadata?.Title ?? "<unknown>", cmd.TrackUri ?? "<none>");
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
            _currentState = _currentState with
            {
                IsPlaying = false,
                IsPaused  = true,
                PositionMs = _audioSink.PlaybackPositionMs,
                Timestamp  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _audioSink.ResumeAsync();
        lock (_stateLock)
        {
            _currentState = _currentState with
            {
                IsPlaying  = true,
                IsPaused   = false,
                PositionMs = _audioSink.PlaybackPositionMs,
                Timestamp  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        PublishState();
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        lock (_seekLock)
            _pendingSeekMs = positionMs;
        // Visibility log so we can correlate "Seek confirmation timed out" UI errors with
        // whether the command actually reached AudioEngine. If this fires but no
        // [seek-trace] BEGIN follows, the playback loop is wedged.
        _logger?.LogDebug("[seek-trace] SeekAsync received target={Target}ms (pendingSet)", positionMs);
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
        lock (_stateLock)
        {
            _currentState = _currentState with { Volume = volume };
        }
        PublishState();
        return Task.CompletedTask;
    }

    public void SetNormalizationEnabled(bool enabled)
    {
        foreach (var p in _processingChain.Processors.OfType<NormalizationProcessor>())
            p.IsEnabled = enabled;
    }

    public async Task<EqualizerApplyResult?> SetEqualizerEnabledAsync(
        bool enabled,
        double[]? bandGains = null,
        CancellationToken ct = default)
    {
        if (_userEq == null) return null;
        _userEq.IsEnabled = enabled;
        if (bandGains != null && _userEq.Bands.Count >= bandGains.Length)
        {
            for (int i = 0; i < bandGains.Length; i++)
                _userEq.Bands[i].GainDb = bandGains[i];
            _userEq.RefreshFilters();
        }

        var version = _userEq.CommitSettingsVersion();
        EngineState state;
        lock (_stateLock)
            state = _currentState;

        var hasActiveAudio = state.IsPlaying && !state.IsPaused && !state.IsBuffering;
        var observedAudioBuffer = false;

        if (enabled && hasActiveAudio)
        {
            await _userEq.WaitForVersionProcessedAsync(version, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            observedAudioBuffer = true;
        }

        _logger?.LogInformation(
            "[AudioEngine] Equalizer installed: enabled={Enabled}, bands={Bands}, version={Version}, observedAudio={ObservedAudio}, state={IsPlaying}/{IsPaused}/{IsBuffering}",
            enabled, bandGains?.Length ?? 0, version, observedAudioBuffer, state.IsPlaying, state.IsPaused, state.IsBuffering);
        return new EqualizerApplyResult
        {
            Installed = true,
            ObservedAudioBuffer = observedAudioBuffer,
            Version = version,
            Message = observedAudioBuffer
                ? "EQ settings were observed by a processed audio buffer."
                : enabled
                    ? "EQ settings are installed; playback is paused/stopped so no audio buffer has verified them yet."
                    : "EQ disabled in AudioHost."
        };
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
            _logger,
            _audioCacheDirectory,
            playbackToken: ct);

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

        // [seek-perf] state: set when a seek is processed; cleared when the
        // first PCM bytes for that seek are written to the sink and we emit
        // one summary log line.
        long seekStartTs = 0;
        long seekFlushDoneTs = 0;
        long seekDecoderDoneTs = 0;
        bool seekTelemetryPending = false;
        long pendingSeekTargetMs = 0;

        long iterCount = 0;
        long lastIterLogTs = Stopwatch.GetTimestamp();

        // Decoder-recreate loop. Phase 3's forward-bisection materializes pages
        // mid-file in NVorbis _pageOffsets, leaving a sparse gap between the
        // sequentially-discovered early pages and the materialized one. A later
        // backward seek into that gap would land on the wrong page (resolved
        // granule above target → negative rollForward → wrong audio position).
        // We detect that case below and re-enter this loop with a fresh
        // VorbisReader (rebuilt _pageOffsets densely from start).
        long currentStartPos = startPositionMs;
        long lastForwardSeekTargetMs = -1;
        bool needsRecreate = false;
        while (true)
        {
            needsRecreate = false;
        await foreach (var buffer in decoder.DecodeAsync(
            decodingStream, currentStartPos,
            title => { lock (_stateLock) _currentState = _currentState with { Title = title }; PublishState(); },
            ct))
        {
            iterCount++;
            // Heartbeat: log iteration count once per ~3 s so we can tell from logs whether
            // the playback loop is iterating (audio producing buffers) vs hung. If a seek
            // command comes in but no [seek-trace] BEGIN follows, this heartbeat tells us
            // whether the loop iterated at all in that window.
            var nowTs = Stopwatch.GetTimestamp();
            if (TicksToMs(lastIterLogTs, nowTs) > 3000)
            {
                _logger?.LogDebug("[seek-trace] loop heartbeat iter={N} pendingSeek={Pending}",
                    iterCount, _pendingSeekMs.HasValue);
                lastIterLogTs = nowTs;
            }

            // Check for pending seek — call SeekTo on decoder inline
            long? seekTarget;
            lock (_seekLock)
            {
                seekTarget = _pendingSeekMs;
                _pendingSeekMs = null;
            }

            if (seekTarget.HasValue)
            {
                var seq = Interlocked.Increment(ref _seekTraceSeq);
                Wavee.AudioHost.Diagnostics.SeekTrace.CurrentSeq = seq;
                seekStartTs = Stopwatch.GetTimestamp();
                _logger?.LogDebug("[seek-trace] seq={Seq} BEGIN target={Target}ms duration={Duration}ms",
                    seq, seekTarget.Value, cmd.DurationMs);

                // Backward seek that crosses a previously materialized forward target?
                // NVorbis _pageOffsets is sparse in that range — bisecting would land on
                // the wrong page (out-of-place materialized one). Recreate the decoder
                // so it rebuilds _pageOffsets densely from byte 0.
                if (lastForwardSeekTargetMs > 0 && seekTarget.Value < lastForwardSeekTargetMs)
                {
                    _logger?.LogDebug("[seek-trace] seq={Seq} backward crosses materialized (target={Target}ms < lastForward={LastFwd}ms) — recreate decoder", seq, seekTarget.Value, lastForwardSeekTargetMs);

                    // Kick off predictive prefetch BEFORE the recreate. The fresh decoder
                    // will immediately run Phase 3 forward bisection from byte 0, and
                    // without these bytes in flight, each probe pays a ~500 ms cold CDN
                    // fetch (5 of them = ~2.5 s seek). With prefetch in flight the
                    // probes serve from cache after one ~250–500 ms TTFB.
                    if (cmd.DurationMs > 0 && lazyStream.CanSeek)
                    {
                        var streamLen = lazyStream.Length;
                        if (streamLen > 0)
                        {
                            var estimatedByte = (long)((double)seekTarget.Value / cmd.DurationMs * streamLen);
                            var prefetchStart = Math.Max(0, estimatedByte - 256 * 1024);
                            var prefetchLen = (int)Math.Min(768 * 1024, streamLen - prefetchStart);
                            if (prefetchLen > 0)
                            {
                                _logger?.LogDebug("[seek-trace] seq={Seq} prefetch (recreate) start={Start} len={Len}KB",
                                    seq, prefetchStart, prefetchLen / 1024);
                                _ = lazyStream.PrefetchRangeAsync(prefetchStart, prefetchLen, ct);
                            }
                        }
                    }

                    buffer.Return();
                    await _audioSink.FlushAsync();
                    _audioSink.SetBasePosition(seekTarget.Value);
                    lastPublishMs = seekTarget.Value;
                    currentStartPos = seekTarget.Value;
                    lastForwardSeekTargetMs = -1; // fresh decoder gets dense _pageOffsets
                    needsRecreate = true;
                    pendingSeekTargetMs = seekTarget.Value;
                    seekTelemetryPending = true;

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
                    break; // exit await foreach → outer while recreates decoder
                }

                // Track forward seeks so we know when a future backward seek crosses
                // a materialized page.
                if (seekTarget.Value > lastPublishMs)
                {
                    lastForwardSeekTargetMs = seekTarget.Value;
                }

                buffer.Return();
                await _audioSink.FlushAsync();
                seekFlushDoneTs = Stopwatch.GetTimestamp();
                _logger?.LogDebug("[seek-trace] seq={Seq} flush done elapsed={Ms:F1}ms",
                    seq, TicksToMs(seekStartTs, seekFlushDoneTs));
                _audioSink.SetBasePosition(seekTarget.Value);
                lastPublishMs = seekTarget.Value;

                // Predictive prefetch for the bisection's convergence region. Runs for
                // both forward AND backward seeks — the decoder-recreate path makes
                // backward seeks safe to go through the Phase 3 bisection, and that
                // bisection benefits from cached bytes around target the same way.
                // (Backward bytes are NOT always already cached — the bg loop only
                // fetches forward from current playback, so a backward seek past a
                // prior forward jump finds a fetch-gap there.)
                if (cmd.DurationMs > 0 && lazyStream.CanSeek)
                {
                    var streamLen = lazyStream.Length;
                    if (streamLen > 0)
                    {
                        var estimatedByte = (long)((double)seekTarget.Value / cmd.DurationMs * streamLen);
                        var prefetchStart = Math.Max(0, estimatedByte - 256 * 1024);
                        var prefetchLen = (int)Math.Min(768 * 1024, streamLen - prefetchStart);
                        if (prefetchLen > 0)
                        {
                            _logger?.LogDebug("[seek-trace] seq={Seq} prefetch start={Start} len={Len}KB",
                                seq, prefetchStart, prefetchLen / 1024);
                            _ = lazyStream.PrefetchRangeAsync(prefetchStart, prefetchLen, ct);
                        }
                    }
                }

                // Decoder seeks via VorbisReader.TimePosition on next ReadSamples call
                _logger?.LogDebug("[seek-trace] seq={Seq} decoder.SeekTo BEGIN", seq);
                decoder.SeekTo(seekTarget.Value);
                seekDecoderDoneTs = Stopwatch.GetTimestamp();
                _logger?.LogDebug("[seek-trace] seq={Seq} decoder.SeekTo END elapsed={Ms:F1}ms",
                    seq, TicksToMs(seekFlushDoneTs, seekDecoderDoneTs));
                // Tell the downloader the read pointer jumped. The in-flight
                // bg fetch is intentionally NOT cancelled (NotifySeek docs).
                lazyStream.NotifySeek();
                _logger?.LogDebug("[seek-trace] seq={Seq} NotifySeek done; awaiting first decode buffer", seq);

                pendingSeekTargetMs = seekTarget.Value;
                seekTelemetryPending = true;

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
            long firstDecodeTs = 0;
            if (seekTelemetryPending)
            {
                firstDecodeTs = Stopwatch.GetTimestamp();
                _logger?.LogDebug("[seek-trace] seq={Seq} first decode buffer received elapsed={Ms:F1}ms",
                    Wavee.AudioHost.Diagnostics.SeekTrace.CurrentSeq,
                    TicksToMs(seekDecoderDoneTs, firstDecodeTs));
            }
            var writeStartTs = Stopwatch.GetTimestamp();
            await _audioSink.WriteAsync(processed.Data, ct);
            // If WriteAsync is suspiciously slow (sink ring buffer full + device
            // truly stalled, not just normal back-pressure), log it. Normal writes
            // at 1× playback against a 2-s ring buffer block ~150 ms each when the
            // decoder produces faster than the device drains — that's expected
            // throughput regulation, not a bug. > 500 ms still catches genuine
            // stalls (device removed, callback frozen, etc.).
            var writeMs = TicksToMs(writeStartTs, Stopwatch.GetTimestamp());
            if (writeMs > 500)
            {
                _logger?.LogDebug("[seek-trace] slow WriteAsync elapsed={Ms:F1}ms iter={N}", writeMs, iterCount);
            }
            if (seekTelemetryPending)
            {
                var firstWriteTs = Stopwatch.GetTimestamp();
                var unmuteTs = (_audioSink as PortAudioSink)?.LastUnmuteAtTicks ?? 0;
                _logger?.LogDebug(
                    "[seek-perf] target={Target}ms flush={Flush:F1}ms decoderSeek={DecoderSeek:F1}ms firstDecode={FirstDecode:F1}ms firstWrite={FirstWrite:F1}ms unmute={Unmute}ms total={Total:F1}ms",
                    pendingSeekTargetMs,
                    TicksToMs(seekStartTs, seekFlushDoneTs),
                    TicksToMs(seekFlushDoneTs, seekDecoderDoneTs),
                    TicksToMs(seekDecoderDoneTs, firstDecodeTs),
                    TicksToMs(firstDecodeTs, firstWriteTs),
                    unmuteTs > seekStartTs ? TicksToMs(seekStartTs, unmuteTs).ToString("F1") : "pending",
                    TicksToMs(seekStartTs, firstWriteTs));
                seekTelemetryPending = false;
            }
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

            if (!needsRecreate) break; // normal completion — exit the recreate while loop

            // Recreate the decoder. lazyStream is reused; resetting its position to 0
            // makes the SkipStream constructor put us at the Spotify header offset, then
            // the new VorbisReader reads the 3 Vorbis header packets (cached in head data,
            // ~1 ms) and we await foreach again with currentStartPos as the target.
            _logger?.LogDebug("[seek-trace] decoder recreate begin (newStartPos={Start}ms)", currentStartPos);
            try { lazyStream.Position = 0; } catch { /* ignore */ }
            // VorbisDecoder caches its reader keyed on the input stream; the previous
            // DecodeAsync's finally already disposed and nulled the cache, so the next
            // DecodeAsync call below will create a fresh VorbisReader.
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

    // Stopwatch tick delta → milliseconds. Returns 0 when either tick is unset
    // so the [seek-perf] line stays meaningful even on the first decoded buffer.
    private static double TicksToMs(long startTs, long endTs)
    {
        if (startTs == 0 || endTs == 0 || endTs < startTs) return 0d;
        return (endTs - startTs) * 1000d / Stopwatch.Frequency;
    }

    // ── Local-file playback loop ──

    private async Task PlaybackLoopLocalAsync(PlayLocalFileCommand cmd, CancellationToken ct)
    {
        if (!File.Exists(cmd.FilePath))
            throw new FileNotFoundException("Local audio file no longer exists.", cmd.FilePath);

        var startPositionMs = cmd.StartPositionMs;

        lock (_stateLock)
        {
            _currentState = new EngineState
            {
                TrackUri = cmd.TrackUri,
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

        // BASS will own the file I/O via Bass.CreateStream(path, ...) — we hand it a
        // LocalFilePathStream marker so BassDecoder takes the path-aware fast path
        // and skips the in-memory copy. The FileStream inside is purely a placeholder
        // that satisfies the Stream contract for non-BASS code paths.
        await using var fileStream = new FileStream(
            cmd.FilePath,
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        await using var localStream = new LocalFilePathStream(fileStream, cmd.FilePath);

        var decoder = _decoderRegistry.FindDecoder(localStream, out var decodingStream);
        if (decoder == null)
            throw new NotSupportedException(
                $"No decoder accepted local file {Path.GetExtension(cmd.FilePath)}: {cmd.FilePath}");

        _logger?.LogDebug("Using decoder for local file: {DecoderName}", decoder.FormatName);

        var audioFormat = await decoder.GetFormatAsync(decodingStream, ct);
        _logger?.LogDebug("Local audio format: {SampleRate}Hz {Channels}ch {Bits}bit",
            audioFormat.SampleRate, audioFormat.Channels, audioFormat.BitsPerSample);

        await _audioSink.InitializeAsync(audioFormat, bufferSizeMs: 2000, ct);
        _audioSink.SetBasePosition(startPositionMs);
        await _processingChain.InitializeAsync(audioFormat, ct);

        if (cmd.Normalization is { } norm && norm.TrackGainDb.HasValue)
        {
            foreach (var proc in _processingChain.Processors.OfType<NormalizationProcessor>())
            {
                var meta = new TrackMetadata
                {
                    Uri = cmd.TrackUri,
                    ReplayGainTrackGain = norm.TrackGainDb.Value,
                    ReplayGainTrackPeak = norm.TrackPeak ?? 1.0f
                };
                proc.SetTrackGain(meta);
            }
        }

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

        long lastPublishMs = startPositionMs;

        await foreach (var buffer in decoder.DecodeAsync(decodingStream, startPositionMs, null, ct))
        {
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
                decoder.SeekTo(seekTarget.Value);

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
        _logger?.LogInformation("Local track finished: {TrackUri}", cmd.TrackUri);
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
                // Bounded wait: if the task observes cancellation it returns in
                // milliseconds; if it doesn't (e.g. an uncancellable sync block),
                // we abandon it after 5s so the IPC command pump stays responsive.
                // The orphaned task will terminate when its underlying block clears.
                var task = _playbackTask;
                var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed == task)
                {
                    try { await task; }
                    catch { /* swallow cancellation */ }
                }
                else
                {
                    _logger?.LogWarning("[AudioEngine] Playback task did not stop within 5s — abandoning to keep IPC pump responsive");
                }
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
    /// <summary>Current volume level (0.0 = silence, 1.0 = 100%). 0 when not yet set.</summary>
    public float Volume { get; init; }

    public static EngineState Empty => new()
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

/// <summary>Playback error info.</summary>
public sealed record EngineError(string Message, Exception? Exception = null);
