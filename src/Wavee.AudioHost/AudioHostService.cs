using System.Diagnostics;
using System.IO.Pipes;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Decoders;
using Wavee.AudioHost.Audio.Processors;
using Wavee.AudioHost.Audio.Sinks;
using Wavee.AudioHost.Audio.Streaming;
using Wavee.AudioHost.PlayPlay;
using Wavee.Core.Audio;
using Wavee.Playback.Contracts;

namespace Wavee.AudioHost;

/// <summary>
/// Hosts the AudioEngine in a separate process and exposes it via Named Pipes IPC.
/// Pure audio — no Spotify Session, no Connect protocol.
/// The UI process resolves tracks and sends CDN URLs + audio keys.
/// </summary>
internal sealed class AudioHostService : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly int _expectedParentProcessId;
    private readonly string? _expectedSessionId;
    private readonly string? _expectedLaunchToken;
    private readonly bool _standaloneDevMode;
    private readonly CancellationTokenSource _cts = new();

    private AudioEngine? _engine;
    private IAudioSink? _sink;
    private IpcPipeTransport? _transport;
    private IDisposable? _stateSubscription;
    private IDisposable? _errorSubscription;
    private readonly DeferredResolutionRegistry _deferredRegistry = new();
    private EngineState? _lastSentState;
    private BassDecoder? _bassDecoder;
    private PreviewAnalysisService? _previewAnalysisService;
    private Timer? _pipeIdleWatchdogTimer;
    private long _lastPipeMessageTimestamp;
    private const int PipeIdleTimeoutMs = 30_000;

    // Cached audio device state — re-sent in snapshot only when it changes, avoiding
    // IPC spam on every position tick.
    private string? _lastSentAudioDeviceName;

    // Windows CoreAudio endpoint change watcher + debouncer. Multiple events arrive
    // in bursts during a Bluetooth connect (added, default-changed, property-changed),
    // so we collapse them to a single refresh ~300ms after the last event.
    private WindowsAudioDeviceWatcher? _deviceWatcher;
    private Timer? _deviceRefreshDebounceTimer;
    private readonly object _deviceRefreshLock = new();

    // PlayPlay AES-key derivation (lazy — constructed on first request, kept
    // alive while the active pack id matches; rebuilt if the UI side ever sends
    // a different pack mid-session). Derivations are serialized through the
    // pipe message loop so a single instance is fine without extra locking.
    private PlayPlayKeyEmulator? _playPlayEmu;
    private string? _playPlayActivePackId;

    public AudioHostService(
        string pipeName,
        ILogger logger,
        int expectedParentProcessId = 0,
        string? expectedSessionId = null,
        string? expectedLaunchToken = null,
        bool standaloneDevMode = false)
    {
        _pipeName = pipeName;
        _logger = logger;
        _expectedParentProcessId = expectedParentProcessId;
        _expectedSessionId = expectedSessionId;
        _expectedLaunchToken = expectedLaunchToken;
        _standaloneDevMode = standaloneDevMode;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        _logger.LogInformation("AudioHost starting — pipe={PipeName}", _pipeName);

        // Create named pipe server and wait for UI process to connect
        var pipeServer = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _logger.LogInformation("Waiting for UI process to connect...");
        await pipeServer.WaitForConnectionAsync(token);
        _logger.LogInformation("UI process connected");

        _transport = new IpcPipeTransport(pipeServer, _logger);

        // Wait for config message from UI process
        var configMsg = await _transport.ReceiveAsync(token);
        if (configMsg?.Type != IpcMessageTypes.Configure)
        {
            _logger.LogError("Expected configure message, got: {Type}", configMsg?.Type);
            return;
        }

        var config = IpcPayloadHelper.Deserialize<AudioHostConfig>(configMsg);
        ValidateLaunchConfig(config);
        _logger.LogInformation("Configured — device={DeviceId}", config?.DeviceId);

        // Create audio engine
        InitializeEngine(config);

        // Send ready message
        await _transport.SendAsync(IpcMessageTypes.Ready,
            IpcPayloadHelper.SerializeToUtf8(new AudioHostReady
            {
                DeviceId = config?.DeviceId ?? "",
                PipeName = _pipeName,
                ParentProcessId = _expectedParentProcessId,
                SessionId = _expectedSessionId
            }), ct: token);

        // Subscribe to engine state and errors
        SubscribeToEngineEvents(token);

        // Process commands
        _logger.LogInformation("AudioHost ready — processing commands");
        _lastPipeMessageTimestamp = Stopwatch.GetTimestamp();
        _pipeIdleWatchdogTimer?.Dispose();
        _pipeIdleWatchdogTimer = new Timer(CheckPipeIdleTimeout, null, PipeIdleTimeoutMs, PipeIdleTimeoutMs);
        await ProcessCommandsAsync(token);
    }

    private void InitializeEngine(AudioHostConfig? config)
    {
        // Route NVorbis diagnostic traces through the host ILogger. The vendored
        // NVorbis project has no logging dependency; it emits via a static
        // Action<string> sink so we wire it up here at engine init.
        NVorbis.NVorbisDiagnostics.Trace = msg => _logger.LogDebug("{NVMsg}", msg);

        var sink = AudioSinkFactory.CreateDefault(_logger);
        _sink = sink;
        var decoderRegistry = CreateDecoderRegistry();
        var volumeProcessor = new VolumeProcessor();
        if (sink is PortAudioSink portAudioSink)
            portAudioSink.SetRealtimeVolumeProcessor(volumeProcessor);

        var processingChain = CreateProcessingChain(
            config,
            includeBufferedVolume: sink is not PortAudioSink,
            volumeProcessor);
        // Pinned SocketsHttpHandler keeps CDN connections warm across seek
        // gaps. ProgressiveDownloader no longer cancels the in-flight fetch
        // on seek (NotifySeek), and the on-demand fetch for the new seek
        // target reuses a pooled connection — no fresh TCP+TLS handshake.
        var httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = true,
        });

        _engine = new AudioEngine(sink, decoderRegistry, processingChain, httpClient, volumeProcessor, _logger,
            audioCacheDirectory: config?.AudioCacheDirectory);
        _previewAnalysisService = new PreviewAnalysisService(
            _bassDecoder ?? new BassDecoder(_logger),
            SendPreviewVisualizationFrameAsync,
            _logger);

        // Subscribe to Windows CoreAudio endpoint-change notifications so newly plugged
        // devices (Bluetooth headphones, USB DACs) propagate to the UI automatically,
        // without requiring the user to open/close the picker.
        _deviceWatcher = new WindowsAudioDeviceWatcher(_logger);
        _deviceWatcher.DevicesChanged += OnWindowsAudioDevicesChanged;
        // When Windows changes the *default* output (e.g. Bluetooth auto-selected),
        // follow it — refresh PortAudio AND reopen the stream on the new default device.
        _deviceWatcher.DefaultOutputDeviceChanged += OnWindowsDefaultOutputDeviceChanged;

        // Pre-seed volume so the first state snapshot carries a real value
        if (config?.InitialVolumePercent is > 0 and <= 100)
        {
            var initialVol = config.InitialVolumePercent / 100f;
            _ = _engine.SetVolumeAsync(initialVol);
            _logger.LogInformation("AudioEngine pre-seeded with volume={Vol}%", config.InitialVolumePercent);
        }

        _logger.LogInformation("AudioEngine created");
    }

    private void ValidateLaunchConfig(AudioHostConfig? config)
    {
        if (_standaloneDevMode)
        {
            _logger.LogWarning("AudioHost running in explicit standalone dev mode");
            return;
        }

        if (config is null)
            throw new InvalidOperationException("Missing AudioHost configuration");
        if (_expectedParentProcessId <= 0)
            throw new InvalidOperationException("Missing expected parent PID");
        if (config.ParentProcessId != _expectedParentProcessId)
            throw new InvalidOperationException("Parent PID mismatch");
        if (!string.Equals(config.SessionId, _expectedSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Launch session mismatch");
        if (string.IsNullOrWhiteSpace(_expectedLaunchToken)
            || !string.Equals(config.LaunchToken, _expectedLaunchToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Launch token mismatch");

        try
        {
            using var parent = Process.GetProcessById(_expectedParentProcessId);
            if (parent.HasExited)
                throw new InvalidOperationException("Parent process has exited");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Parent process is not running", ex);
        }
    }

    private void CheckPipeIdleTimeout(object? state)
    {
        if (_cts.IsCancellationRequested || _transport is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_lastPipeMessageTimestamp);
        if (elapsed.TotalMilliseconds <= PipeIdleTimeoutMs) return;

        _logger.LogWarning("No parent IPC heartbeat for {ElapsedMs:F0}ms; shutting down AudioHost", elapsed.TotalMilliseconds);
        _cts.Cancel();
    }

    private AudioDecoderRegistry CreateDecoderRegistry()
    {
        var registry = new AudioDecoderRegistry();
        registry.Register(new VorbisDecoder(_logger));
        _bassDecoder = new BassDecoder(_logger);
        registry.Register(_bassDecoder);
        return registry;
    }

    private AudioProcessingChain CreateProcessingChain(
        AudioHostConfig? config,
        bool includeBufferedVolume,
        VolumeProcessor volumeProcessor)
    {
        var chain = new AudioProcessingChain();

        // Normalization
        var normalization = new NormalizationProcessor();
        normalization.IsEnabled = config?.NormalizationEnabled ?? true;
        chain.AddProcessor(normalization);

        // Equalizer
        var eq = new EqualizerProcessor();
        eq.IsEnabled = config?.EqualizerEnabled ?? false;
        eq.CreateGraphicEq10Band();
        if (config?.EqualizerBandGains != null && eq.Bands.Count >= config.EqualizerBandGains.Length)
        {
            for (int i = 0; i < config.EqualizerBandGains.Length; i++)
                eq.Bands[i].GainDb = config.EqualizerBandGains[i];
            eq.RefreshFilters();
        }
        chain.AddProcessor(eq);

        // Compressor + Limiter for safety
        chain.AddProcessor(new CompressorProcessor());
        chain.AddProcessor(new LimiterProcessor());

        // Non-realtime sinks still need user volume in the normal buffered chain.
        if (includeBufferedVolume)
            chain.AddProcessor(volumeProcessor);

        return chain;
    }

    private void SubscribeToEngineEvents(CancellationToken ct)
    {
        if (_engine == null || _transport == null) return;

        // Stream state to UI directly — engine publishes at PositionPublishIntervalMs cadence
        _stateSubscription = _engine.StateChanges
            .Subscribe(state =>
            {
                if (ct.IsCancellationRequested) return;
                var snapshot = MapToSnapshot(state);
                _ = _transport.SendAsync(IpcMessageTypes.StateUpdate,
                    IpcPayloadHelper.SerializeToUtf8(snapshot), ct: CancellationToken.None);
            });

        // Stream errors to UI
        _errorSubscription = _engine.Errors.Subscribe(error =>
        {
            if (ct.IsCancellationRequested) return;
            var msg = new PlaybackErrorMessage
            {
                ErrorType = "Unknown",
                Message = error.Message
            };
            _ = _transport.SendAsync(IpcMessageTypes.Error,
                IpcPayloadHelper.SerializeToUtf8(msg), ct: CancellationToken.None);
        });

        // TrackFinished is now emitted by AudioEngine.TrackCompleted observable
        // (only fires on natural completion, NOT on cancellation from new play command)
        _engine.TrackCompleted.Subscribe(trackUri =>
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogInformation("Track finished naturally: {TrackUri}", trackUri);
            var finished = new TrackFinishedMessage { TrackUri = trackUri, Reason = "finished" };
            _ = _transport!.SendAsync(IpcMessageTypes.TrackFinished,
                IpcPayloadHelper.SerializeToUtf8(finished), ct: CancellationToken.None);
        });
    }

    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _transport != null)
        {
            IpcMessage? msg;
            try
            {
                msg = await _transport.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Pipe disconnected");
                break;
            }

            if (msg == null)
            {
                _logger.LogInformation("Pipe closed by UI process");
                break;
            }
            _lastPipeMessageTimestamp = Stopwatch.GetTimestamp();

            try
            {
                await HandleCommandAsync(msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling command {Type}", msg.Type);
                await _transport.SendAsync(IpcMessageTypes.CommandResult,
                    IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
                    {
                        RequestId = msg.Id,
                        Success = false,
                        ErrorMessage = ex.Message
                    }), ct: ct);
            }
        }
    }

    private async Task HandleCommandAsync(IpcMessage msg, CancellationToken ct)
    {
        if (_engine == null) return;

        switch (msg.Type)
        {
            case IpcMessageTypes.PlayResolved:
            {
                var cmd = IpcPayloadHelper.Deserialize<PlayResolvedTrackCommand>(msg);
                if (cmd != null)
                    await _engine.PlayAsync(cmd, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.PlayTrack:
            {
                var cmd = IpcPayloadHelper.Deserialize<PlayTrackCommand>(msg);
                if (cmd != null)
                {
                    var deferredTask = _deferredRegistry.CreateDeferred(cmd.DeferredId);
                    await _engine.PlayAsync(cmd, deferredTask, ct);
                }
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.PlayLocalFile:
            {
                var cmd = IpcPayloadHelper.Deserialize<PlayLocalFileCommand>(msg);
                if (cmd != null)
                    await _engine.PlayAsync(cmd, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.DeferredResolved:
            {
                var cmd = IpcPayloadHelper.Deserialize<DeferredResolvedCommand>(msg);
                if (cmd != null)
                {
                    var audioKey = Convert.FromBase64String(cmd.AudioKey);
                    if (!string.IsNullOrEmpty(cmd.LocalCacheFileId))
                    {
                        _deferredRegistry.CompleteFromCache(cmd.DeferredId, audioKey, cmd.FileSize, cmd.LocalCacheFileId);
                        _logger.LogInformation("Deferred resolved: {Id} → local cache ({FileId})",
                            cmd.DeferredId, cmd.LocalCacheFileId);
                    }
                    else
                    {
                        _deferredRegistry.Complete(cmd.DeferredId, cmd.CdnUrl ?? "", audioKey, cmd.FileSize,
                            spotifyFileId: cmd.SpotifyFileId);
                        _logger.LogInformation("Deferred resolved: {Id} → CDN ready", cmd.DeferredId);
                    }
                }
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.Resume:
                await _engine.ResumeAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Pause:
                await _engine.PauseAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Stop:
                await _engine.StopAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Seek:
            {
                var cmd = IpcPayloadHelper.Deserialize<SeekCommand>(msg);
                if (cmd != null)
                    await _engine.SeekAsync(cmd.PositionMs, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetVolume:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetVolumeCommand>(msg);
                if (cmd != null)
                    await _engine.SetVolumeAsync(cmd.VolumePercent / 100f, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetNormalization:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetNormalizationCommand>(msg);
                if (cmd != null)
                    _engine.SetNormalizationEnabled(cmd.Enabled);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetEqualizer:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetEqualizerCommand>(msg);
                var result = cmd == null
                    ? null
                    : await _engine.SetEqualizerEnabledAsync(cmd.Enabled, cmd.BandGains, ct);
                if (result == null)
                {
                    await SendCommandResult(msg.Id, success: false,
                        errorMessage: "Equalizer processor is not available in AudioHost", ct);
                    break;
                }

                var resultJson = IpcPayloadHelper.SerializeToUtf8(result);
                using var resultDoc = System.Text.Json.JsonDocument.Parse(resultJson);
                await _transport!.SendAsync(IpcMessageTypes.CommandResult,
                    IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
                    {
                        RequestId = msg.Id,
                        Success = true,
                        Result = resultDoc.RootElement.Clone()
                    }), ct: ct);
                break;
            }
            case IpcMessageTypes.SwitchAudioOutput:
            {
                var cmd = IpcPayloadHelper.Deserialize<SwitchAudioOutputCommand>(msg);
                if (cmd != null && _sink is IDeviceSelectableSink dss)
                {
                    try
                    {
                        await dss.SwitchToDeviceAsync(cmd.DeviceIndex, ct);
                        // Force the next state snapshot to re-send the device list
                        _lastSentAudioDeviceName = null;
                        await SendOk(msg.Id, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SwitchAudioOutput failed for index {Index}", cmd.DeviceIndex);
                        await SendCommandResult(msg.Id, success: false,
                            errorMessage: $"Could not open audio device (index {cmd.DeviceIndex}): {ex.Message}", ct);
                    }
                }
                else
                {
                    await SendOk(msg.Id, ct);
                }
                break;
            }
            case IpcMessageTypes.RefreshAudioDevices:
            {
                if (_sink is IDeviceSelectableSink dss)
                {
                    try
                    {
                        // Re-scan the system device list (Pa_Terminate + Pa_Initialize).
                        // May briefly interrupt the stream — this is user-initiated, so the
                        // small audio gap is acceptable.
                        dss.RefreshDeviceList();

                        // Force the next state snapshot to re-send the fresh list.
                        _lastSentAudioDeviceName = null;

                        // Push an immediate state update so the UI picker populates right
                        // away, rather than waiting for the next engine position tick.
                        if (_lastSentState != null && _transport != null)
                        {
                            var snapshot = MapToSnapshot(_lastSentState);
                            await _transport.SendAsync(IpcMessageTypes.StateUpdate,
                                IpcPayloadHelper.SerializeToUtf8(snapshot), ct: CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RefreshAudioDevices failed");
                    }
                }
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.Ping:
                _logger.LogDebug("Ping received");
                await _transport!.SendAsync(IpcMessageTypes.Pong, msg.Id, ct);
                break;
            case IpcMessageTypes.StartPreviewAnalysis:
            {
                var cmd = IpcPayloadHelper.Deserialize<StartPreviewAnalysisCommand>(msg);
                if (cmd != null && _previewAnalysisService != null)
                    await _previewAnalysisService.StartAsync(cmd, _cts.Token);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.StopPreviewAnalysis:
            {
                var cmd = IpcPayloadHelper.Deserialize<StopPreviewAnalysisCommand>(msg);
                if (cmd != null && _previewAnalysisService != null)
                    await _previewAnalysisService.StopAsync(cmd.SessionId, _cts.Token);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.DerivePlayPlayKey:
            {
                var cmd = IpcPayloadHelper.Deserialize<DerivePlayPlayKeyCommand>(msg);
                if (cmd is null)
                {
                    await SendCommandResult(msg.Id, success: false,
                        errorMessage: "DerivePlayPlayKey payload missing", ct);
                    break;
                }

                try
                {
                    var (config, packId) = ParsePackJson(cmd.PackJson);
                    if (_playPlayEmu is null || _playPlayActivePackId != packId)
                    {
                        _playPlayEmu?.Dispose();
                        _playPlayEmu = new PlayPlayKeyEmulator(cmd.SpotifyDllPath, config, _logger);
                        _playPlayActivePackId = packId;
                    }

                    var aes = _playPlayEmu.DeriveAesKey(
                        Convert.FromHexString(cmd.ObfuscatedKeyHex),
                        Convert.FromHexString(cmd.ContentIdHex));

                    var resultJson = IpcPayloadHelper.SerializeToUtf8(new DerivePlayPlayKeyResult
                    {
                        AesKeyHex = Convert.ToHexString(aes).ToLowerInvariant()
                    });
                    using var resultDoc = System.Text.Json.JsonDocument.Parse(resultJson);
                    await _transport!.SendAsync(IpcMessageTypes.CommandResult,
                        IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
                        {
                            RequestId = msg.Id,
                            Success = true,
                            Result = resultDoc.RootElement.Clone()
                        }), ct: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PlayPlay derivation failed");
                    await SendCommandResult(msg.Id, success: false,
                        errorMessage: $"playplay: {ex.Message}", ct);
                }
                break;
            }
            case IpcMessageTypes.Shutdown:
                _logger.LogInformation("Shutdown requested by UI process");
                await _cts.CancelAsync();
                break;
            default:
                _logger.LogWarning("Unknown command type: {Type}", msg.Type);
                break;
        }
    }

    private async Task SendOk(long requestId, CancellationToken ct)
    {
        if (_transport == null) return;
        await _transport.SendAsync(IpcMessageTypes.CommandResult,
            IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
            {
                RequestId = requestId,
                Success = true
            }), ct: ct);
    }

    private Task SendCommandResult(long requestId, bool success, string? errorMessage, CancellationToken ct)
    {
        if (_transport == null) return Task.CompletedTask;
        return _transport.SendAsync(IpcMessageTypes.CommandResult,
            IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
            {
                RequestId = requestId,
                Success = success,
                ErrorMessage = errorMessage
            }), ct: ct);
    }

    private Task SendPreviewVisualizationFrameAsync(PreviewVisualizationFrame frame, CancellationToken ct)
    {
        if (_transport == null)
            return Task.CompletedTask;

        return _transport.SendAsync(
            IpcMessageTypes.PreviewVisualizationFrame,
            IpcPayloadHelper.SerializeToUtf8(frame),
            ct: ct);
    }

    // ── Windows audio device change handler ──

    private void OnWindowsAudioDevicesChanged()
    {
        // Multiple MMDevice events fire in a burst during a Bluetooth connect
        // (added, state-changed, default-changed). Collapse them with a 300 ms debounce
        // so we only call Pa_Terminate/Pa_Initialize once per physical device event.
        _logger.LogDebug("[AudioHost] MMDevice event → debouncing device list refresh (300ms)");
        lock (_deviceRefreshLock)
        {
            _deviceRefreshDebounceTimer?.Dispose();
            _deviceRefreshDebounceTimer = new Timer(
                _ => PerformDeviceRefresh(),
                null,
                TimeSpan.FromMilliseconds(300),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWindowsDefaultOutputDeviceChanged()
    {
        // Windows changed the system default output (e.g. Bluetooth headphones auto-selected).
        // Debounce then refresh PortAudio AND switch the stream to the new default device.
        _logger.LogInformation("[AudioHost] MMDevice: default OUTPUT changed → debouncing FollowDefault switch (300ms)");
        lock (_deviceRefreshLock)
        {
            _deviceRefreshDebounceTimer?.Dispose();
            _deviceRefreshDebounceTimer = new Timer(
                _ => _ = PerformDefaultDeviceSwitchAsync(),
                null,
                TimeSpan.FromMilliseconds(300),
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task PerformDefaultDeviceSwitchAsync()
    {
        if (_sink is not IDeviceSelectableSink dss)
        {
            _logger.LogDebug("[AudioHost] PerformDefaultDeviceSwitchAsync: sink is not IDeviceSelectableSink — skipping");
            return;
        }

        _logger.LogInformation("[AudioHost] PerformDefaultDeviceSwitchAsync: starting PortAudio reinit + stream follow");
        try
        {
            await dss.SwitchToDefaultDeviceAsync(CancellationToken.None);
            _logger.LogInformation("[AudioHost] PerformDefaultDeviceSwitchAsync: PortAudio now on new default — pushing state snapshot");

            // Re-send device list to UI now that we've switched.
            _lastSentAudioDeviceName = null;
            if (_lastSentState != null && _transport != null)
            {
                var snap = MapToSnapshot(_lastSentState);
                await _transport.SendAsync(IpcMessageTypes.StateUpdate,
                    IpcPayloadHelper.SerializeToUtf8(snap), ct: CancellationToken.None);
                _logger.LogDebug("[AudioHost] State snapshot sent after default switch: activeDevice={Device}, deviceCount={Count}",
                    snap.ActiveAudioDeviceName, snap.AvailableAudioDevices?.Length ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AudioHost] PerformDefaultDeviceSwitchAsync failed");
        }
    }

    private void PerformDeviceRefresh()
    {
        if (_sink is not IDeviceSelectableSink dss)
        {
            _logger.LogDebug("[AudioHost] PerformDeviceRefresh: sink is not IDeviceSelectableSink — skipping");
            return;
        }

        _logger.LogInformation("[AudioHost] PerformDeviceRefresh: refreshing PortAudio device list");
        try
        {
            dss.RefreshDeviceList();

            // Force the next snapshot to re-enumerate and re-send the full device list.
            _lastSentAudioDeviceName = null;

            // Push an immediate state update so the UI picker reflects the new device
            // without waiting for the next engine position tick.
            if (_lastSentState != null && _transport != null)
            {
                var snap = MapToSnapshot(_lastSentState);
                _ = _transport.SendAsync(IpcMessageTypes.StateUpdate,
                    IpcPayloadHelper.SerializeToUtf8(snap), ct: CancellationToken.None);
                _logger.LogInformation("[AudioHost] PerformDeviceRefresh: snapshot pushed — activeDevice={Device}, deviceCount={Count}",
                    snap.ActiveAudioDeviceName, snap.AvailableAudioDevices?.Length ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AudioHost] PerformDeviceRefresh failed");
        }
    }

    private PlaybackStateSnapshot MapToSnapshot(EngineState state)
    {
        // Compute change flags by comparing with last sent state
        // Only flag Position on discontinuities (seeks), not normal progression
        int changes = 0;
        var prev = _lastSentState;

        if (prev == null || prev.TrackUri != state.TrackUri)
            changes |= 1; // Track
        if (prev == null || prev.IsPlaying != state.IsPlaying || prev.IsPaused != state.IsPaused || prev.IsBuffering != state.IsBuffering)
            changes |= 4; // Status

        // Position: only flag on discontinuity (jump > 2s from expected)
        if (prev != null && prev.PositionMs > 0)
        {
            var expectedPos = prev.PositionMs + (state.Timestamp - prev.Timestamp);
            var drift = Math.Abs(state.PositionMs - expectedPos);
            if (drift > 2000)
                changes |= 2; // Position (seek detected)
        }
        else if (prev == null)
        {
            changes |= 2; // First update
        }

        _lastSentState = state;

        // Audio output device info — only re-send the full device list when the active
        // device changes (plug/unplug, user switch). Always send the current name so the
        // UI can display it on the first state update.
        var dss = _sink as IDeviceSelectableSink;
        string? currentDeviceName = dss?.CurrentDeviceName;
        AudioOutputDeviceDto[]? availableDevices = null;
        if (dss != null && currentDeviceName != _lastSentAudioDeviceName)
        {
            availableDevices = dss.EnumerateOutputDevices().ToArray();
            _lastSentAudioDeviceName = currentDeviceName;
        }

        return new PlaybackStateSnapshot
        {
            Source = "local",
            TrackUri = state.TrackUri,
            TrackUid = state.TrackUid,
            TrackTitle = state.Title,
            TrackArtist = state.Artist,
            TrackAlbum = state.Album,
            AlbumUri = state.AlbumUri,
            ArtistUri = state.ArtistUri,
            ImageUrl = state.ImageUrl,
            ImageLargeUrl = state.ImageLargeUrl,
            PositionMs = state.PositionMs,
            DurationMs = state.DurationMs,
            IsPlaying = state.IsPlaying,
            IsPaused = state.IsPaused,
            IsBuffering = state.IsBuffering,
            CanSeek = true,
            Changes = changes,
            Timestamp = state.Timestamp,
            // 0-65535 cluster scale — must match LocalMediaPlayer / SpotifyVideoProvider.
            // Sending 0-100 here causes PlaybackStateService (state.Volume / 655.35) to
            // collapse the slider to 0 when the local engine echoes back.
            Volume = state.Volume > 0f ? (uint)Math.Round(state.Volume * 65535d) : 0,
            ActiveAudioDeviceName = currentDeviceName,
            AvailableAudioDevices = availableDevices,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _stateSubscription?.Dispose();
        _errorSubscription?.Dispose();
        _pipeIdleWatchdogTimer?.Dispose();
        _pipeIdleWatchdogTimer = null;

        // Stop the Windows device watcher before tearing down the engine so
        // no stray PerformDeviceRefresh runs after Pa_Terminate.
        _deviceWatcher?.Dispose();
        lock (_deviceRefreshLock)
        {
            _deviceRefreshDebounceTimer?.Dispose();
            _deviceRefreshDebounceTimer = null;
        }

        if (_previewAnalysisService != null)
            await _previewAnalysisService.DisposeAsync();

        if (_engine != null)
            await _engine.DisposeAsync();

        if (_transport != null)
            await _transport.DisposeAsync();

        // Tear down the PlayPlay emulator (removes the vectored exception
        // handler and restores the patched int3 byte) before exiting.
        _playPlayEmu?.Dispose();

        _cts.Dispose();
    }

    // Build a PlayPlayConfig from the PackJson sent by the UI side. Parsed via
    // JsonDocument (no DTO type) since AudioHost has zero project references
    // on Wavee where the manifest types live.
    private static (PlayPlayConfig Config, string PackId) ParsePackJson(string packJson)
    {
        if (string.IsNullOrWhiteSpace(packJson))
            throw new InvalidOperationException("packJson missing");

        using var doc = System.Text.Json.JsonDocument.Parse(packJson);
        var root = doc.RootElement;

        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
        int Int(string name, int def) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetInt32()
            : def;

        var packId = Str("id");
        var arch = Str("arch").Equals("Arm64", StringComparison.OrdinalIgnoreCase)
            ? Architecture.Arm64
            : Architecture.X64;

        var config = new PlayPlayConfig(
            Version: Str("spotify_version"),
            Arch: arch,
            Sha256: Convert.FromHexString(Str("sha256_hex")),
            PlayPlayToken: Convert.FromHexString(Str("playplay_token_hex")),
            VmInitValue: Convert.FromHexString(Str("vm_init_value_hex")),
            AnalysisBase: ParseHex(Str("analysis_base_hex")),
            VmRuntimeInitVa: ParseHex(Str("vm_runtime_init_va_hex")),
            VmObjectTransformVa: ParseHex(Str("vm_object_transform_va_hex")),
            RuntimeContextVa: ParseHex(Str("runtime_context_va_hex")),
            FillRandomBytesVa: ParseHex(Str("fill_random_bytes_va_hex")),
            AesKey: new AesKeyExtraction.TriggerRipBreakpoint(
                RipVa: ParseHex(Str("trigger_rip_va_hex")),
                ContextRegOffset: Int("trigger_rip_reg_offset", 0x88)),
            VmObjectSize: Int("vm_object_size", 144),
            RtContextSize: Int("rt_context_size", 16),
            DerivedKeySize: Int("derived_key_size", 24),
            ObfuscatedKeySize: Int("obfuscated_key_size", 16),
            InitValueSize: Int("init_value_size", 16),
            ContentIdSize: Int("content_id_size", 16),
            KeySize: Int("key_size", 16));

        return (config, packId);
    }

    private static ulong ParseHex(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan();
        return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }
}
