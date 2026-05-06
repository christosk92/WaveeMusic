using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.Audio;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Playback.Contracts;

namespace Wavee.AudioIpc;

/// <summary>
/// IPlaybackEngine proxy that routes all commands to the audio host process via Named Pipes IPC.
/// Lives in the UI process. State updates arrive via the pipe and are exposed as observables.
/// </summary>
public sealed class AudioPipelineProxy : IPlaybackEngine, IAsyncDisposable
{
    private readonly IpcPipeTransport _transport;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject = new(new LocalPlaybackState());
    private readonly Subject<PlaybackError> _errorSubject = new();
    private readonly Subject<TrackFinishedMessage> _trackFinishedSubject = new();
    private readonly Subject<PreviewVisualizationFrame> _previewVisualizationFrameSubject = new();

    private long _nextRequestId;
    private Task? _receiveLoop;

    // ── Awaitable RPC ──
    // Most commands are fire-and-forget — the proxy logs the CommandResult
    // failure but doesn't surface it to the caller. PlayPlay derivation needs
    // a real request/reply: the caller awaits the AES key. Each call
    // registers a TCS keyed by the message id; the receive loop completes it
    // when the matching CommandResult comes back.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CommandResultMessage>> _pendingRequests = new();

    // ── IPC Metrics ──
    private long _lastPingSentTimestamp;
    private long _messagesReceived;
    private long _messagesSent;
    private long _lastStateUpdateTimestamp;

    // Per-message monotonic sequence assigned on receipt (not the remote-side sequence).
    // Use this in logs to detect out-of-order apply, dropped updates, or stale reads.
    private long _receiveSequence;
    // State-update specific counters — helps spot bursts and detect lost messages.
    private long _stateUpdatesReceived;
    private long _stateUpdatesOutOfOrder;
    private long _lastStateUpdateRemoteTimestamp;

    // Audio device info is re-sent sparingly by AudioHost — carry the most recent
    // values forward so every LocalPlaybackState snapshot includes them.
    private string? _lastActiveAudioDeviceName;
    private IReadOnlyList<AudioOutputDeviceDto>? _lastAvailableAudioDevices;

    /// <summary>Last measured round-trip time in milliseconds.</summary>
    public double LastRttMs { get; private set; }

    private static readonly TimeSpan RttHistoryWindow = TimeSpan.FromMinutes(1);

    /// <summary>Rolling history of RTT samples (oldest to newest), retained for the last minute.</summary>
    public double[] RttHistory { get; } = new double[256];
    public int RttHistoryCount { get; private set; }
    private readonly long[] _rttHistoryTimestamps = new long[256];

    /// <summary>Total messages received from the audio process.</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>Total messages sent to the audio process.</summary>
    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    /// <summary>Time since last state update from audio process, in milliseconds.</summary>
    public double StateFreshnessMs =>
        _lastStateUpdateTimestamp == 0 ? 0 :
        Stopwatch.GetElapsedTime(_lastStateUpdateTimestamp).TotalMilliseconds;

    /// <summary>
    /// Fires when the pipe connection is lost (process crash, pipe break, etc.).
    /// The AudioProcessManager subscribes to this for auto-restart.
    /// </summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// Fires when a pong response is received from the audio process.
    /// </summary>
    public event Action? PongReceived;

    /// <summary>
    /// True while the receive loop is running and the pipe is connected.
    /// </summary>
    public bool IsConnected { get; private set; }

    public AudioPipelineProxy(IpcPipeTransport transport, ILogger? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger;
    }

    // ── IPlaybackEngine ──

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject.AsObservable();
    public IObservable<PlaybackError> Errors => _errorSubject.AsObservable();
    public IObservable<PreviewVisualizationFrame> PreviewVisualizationFrames => _previewVisualizationFrameSubject.AsObservable();

    /// <summary>
    /// Fires when AudioHost reports a track has finished playing naturally.
    /// The layer above (PlaybackService) should resolve and send the next track.
    /// </summary>
    public IObservable<TrackFinishedMessage> TrackFinished => _trackFinishedSubject.AsObservable();
    public LocalPlaybackState CurrentState => _stateSubject.Value;

    /// <summary>
    /// Audio underrun count from the remote process (updated via state snapshots).
    /// </summary>
    public long UnderrunCount { get; private set; }

    /// <summary>
    /// Starts the background receive loop that processes state updates from the audio process.
    /// </summary>
    public void StartReceiving()
    {
        IsConnected = true;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends configuration to the audio process and waits for the Ready message.
    /// </summary>
    public async Task<bool> ConfigureAsync(
        string deviceId,
        bool normalizationEnabled = true,
        int initialVolumePercent = 0,
        string? audioCacheDirectory = null,
        int parentProcessId = 0,
        string? sessionId = null,
        string? launchToken = null,
        CancellationToken ct = default)
    {
        var config = new AudioHostConfig
        {
            DeviceId = deviceId,
            ParentProcessId = parentProcessId,
            SessionId = sessionId,
            LaunchToken = launchToken,
            NormalizationEnabled = normalizationEnabled,
            InitialVolumePercent = initialVolumePercent,
            AudioCacheDirectory = audioCacheDirectory,
        };
        var configJson = IpcPayloadHelper.SerializeToUtf8(config);
        await _transport.SendAsync(IpcMessageTypes.Configure, configJson, ct: ct);

        var response = await _transport.ReceiveAsync(ct);
        if (response?.Type == IpcMessageTypes.Ready)
        {
            _logger?.LogInformation("Audio host ready");
            return true;
        }

        _logger?.LogError("Unexpected handshake response: {Type}", response?.Type);
        return false;
    }

    public async Task PlayAsync(PlayCommand command, CancellationToken cancellationToken = default)
    {
        // Legacy path — callers should prefer PlayResolvedAsync
        _logger?.LogWarning("PlayAsync(PlayCommand) called without resolution — this is a no-op in the new architecture");
    }

    /// <summary>
    /// Plays a fully resolved track via IPC to AudioHost.
    /// </summary>
    public async Task PlayResolvedAsync(ResolvedTrack resolvedTrack, long positionMs = 0, CancellationToken ct = default)
    {
        var cmd = resolvedTrack.ToIpcCommand(positionMs);
        await SendCommandAsync(IpcMessageTypes.PlayResolved, cmd, ct);
    }

    /// <summary>
    /// Sends head data + deferred ID for instant start. CDN resolution follows via SendDeferredResolvedAsync.
    /// </summary>
    public Task PlayTrackDeferredAsync(PlayTrackCommand cmd, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.PlayTrack, cmd, ct);

    /// <summary>
    /// Tells AudioHost to open and play a local audio file on disk. No CDN, no
    /// audio key, no head data — BASS streams directly from the path.
    /// </summary>
    public Task PlayLocalFileAsync(PlayLocalFileCommand cmd, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.PlayLocalFile, cmd, ct);

    /// <summary>
    /// Completes the deferred CDN resolution so AudioHost can continue from CDN after head data.
    /// Pass <paramref name="spotifyFileId"/> so AudioHost can persist the download to the audio cache.
    /// </summary>
    public Task SendDeferredResolvedAsync(string deferredId, string cdnUrl, byte[] audioKey, long fileSize,
        string? spotifyFileId = null, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.DeferredResolved, new DeferredResolvedCommand
        {
            DeferredId = deferredId,
            CdnUrl = cdnUrl,
            AudioKey = Convert.ToBase64String(audioKey),
            FileSize = fileSize,
            SpotifyFileId = spotifyFileId,
        }, ct);

    /// <summary>
    /// Completes the deferred resolution using a locally cached file.
    /// AudioHost reads from <c>$cacheDir/audio/$localCacheFileId.enc</c> instead of CDN.
    /// </summary>
    public Task SendDeferredCachedAsync(string deferredId, string localCacheFileId, byte[] audioKey, long fileSize,
        CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.DeferredResolved, new DeferredResolvedCommand
        {
            DeferredId = deferredId,
            CdnUrl = null,
            AudioKey = Convert.ToBase64String(audioKey),
            FileSize = fileSize,
            SpotifyFileId = localCacheFileId,
            LocalCacheFileId = localCacheFileId,
        }, ct);

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Pause, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Stop, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Resume, cancellationToken);

    public Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.Seek, new Playback.Contracts.SeekCommand { PositionMs = positionMs }, cancellationToken);

    public Task SkipNextAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.SkipNext, cancellationToken);

    public Task SkipPreviousAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.SkipPrevious, cancellationToken);

    public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.SetShuffle, new SetShuffleCommand { Enabled = enabled }, cancellationToken);

    public Task SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.SetRepeat,
            new SetRepeatCommand { State = enabled ? "context" : "off" }, cancellationToken);

    public Task SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.SetRepeat,
            new SetRepeatCommand { State = enabled ? "track" : "off" }, cancellationToken);

    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.SetVolume,
            new SetVolumeCommand { VolumePercent = (int)(volume * 100) }, cancellationToken);

    // ── Additional controls (not on IPlaybackEngine) ──

    public Task SetNormalizationEnabledAsync(bool enabled, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.SetNormalization,
            new SetNormalizationCommand { Enabled = enabled }, ct);

    public async Task<EqualizerApplyResult> SetEqualizerAsync(bool enabled, double[]? bandGains, CancellationToken ct = default)
    {
        var result = await SendRequestAsync(IpcMessageTypes.SetEqualizer,
            new SetEqualizerCommand { Enabled = enabled, BandGains = bandGains }, ct).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "AudioHost rejected equalizer settings");

        if (result.Result is { } payload)
        {
            var applyResult = payload.Deserialize<EqualizerApplyResult>(IpcJsonContext.Default.EqualizerApplyResult);
            if (applyResult is not null)
                return applyResult;
        }

        return new EqualizerApplyResult
        {
            Installed = true,
            ObservedAudioBuffer = false,
            Message = "AudioHost accepted equalizer settings, but did not return verification details."
        };
    }

    public Task SwitchQualityAsync(string quality, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.SwitchQuality,
            new SwitchQualityCommand { Quality = quality }, ct);

    /// <summary>
    /// Switch the local PortAudio output device used by the AudioHost process.
    /// </summary>
    public Task SwitchAudioOutputAsync(int deviceIndex, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.SwitchAudioOutput,
            new SwitchAudioOutputCommand { DeviceIndex = deviceIndex }, ct);

    /// <summary>
    /// Music-video switch is owned by the orchestrator (PlayReady/DASH lives
    /// in the UI process). The bare AudioHost proxy has no path for it.
    /// </summary>
    public Task SwitchToVideoAsync(
        string? manifestIdOverride = null,
        string? videoTrackUriOverride = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SwitchToAudioAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Asks AudioHost to rescan the live system audio device list (Pa_Terminate +
    /// Pa_Initialize) and push a fresh <c>state_update</c> with the updated list.
    /// Called by the UI when the user opens the device picker so newly-plugged
    /// headphones/speakers appear even during ongoing playback.
    /// </summary>
    public Task RefreshAudioDevicesAsync(CancellationToken ct = default)
        => SendSimpleCommandAsync(IpcMessageTypes.RefreshAudioDevices, ct);

    public Task ShutdownAsync(CancellationToken ct = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Shutdown, ct);

    public Task SendPingAsync(CancellationToken ct = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Ping, ct);

    public Task StartPreviewAnalysisAsync(string sessionId, string previewUrl, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.StartPreviewAnalysis, new StartPreviewAnalysisCommand
        {
            SessionId = sessionId,
            PreviewUrl = previewUrl
        }, ct);

    public Task StopPreviewAnalysisAsync(string sessionId, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.StopPreviewAnalysis, new StopPreviewAnalysisCommand
        {
            SessionId = sessionId
        }, ct);

    // PlayPlay is Spotify property. Forwards a derivation request to AudioHost.
    public async Task<byte[]> DerivePlayPlayKeyAsync(
        ReadOnlyMemory<byte> obfuscatedKey,
        ReadOnlyMemory<byte> contentId16,
        string spotifyDllPath,
        CancellationToken ct = default)
    {
        if (obfuscatedKey.Length != 16) throw new ArgumentException("obfuscated key must be 16 bytes", nameof(obfuscatedKey));
        if (contentId16.Length != 16) throw new ArgumentException("content id must be 16 bytes", nameof(contentId16));
        if (string.IsNullOrWhiteSpace(spotifyDllPath)) throw new ArgumentException("spotifyDllPath required", nameof(spotifyDllPath));

        var cmd = new DerivePlayPlayKeyCommand
        {
            ObfuscatedKeyHex = Convert.ToHexString(obfuscatedKey.Span).ToLowerInvariant(),
            ContentIdHex = Convert.ToHexString(contentId16.Span).ToLowerInvariant(),
            SpotifyDllPath = spotifyDllPath,
        };

        var result = await SendRequestAsync(IpcMessageTypes.DerivePlayPlayKey, cmd, ct).ConfigureAwait(false);

        if (!result.Success || result.Result is not JsonElement resultElem)
            throw new InvalidOperationException(
                $"AudioHost rejected DerivePlayPlayKey: {result.ErrorMessage ?? "no result payload"}");

        var derived = resultElem.Deserialize(IpcJsonContext.Default.DerivePlayPlayKeyResult)
            ?? throw new InvalidOperationException("AudioHost returned malformed DerivePlayPlayKeyResult");
        var aes = Convert.FromHexString(derived.AesKeyHex);
        if (aes.Length != 16)
            throw new InvalidOperationException(
                $"AudioHost returned AES key of length {aes.Length}, expected 16");
        return aes;
    }

    // ── Internals ──

    private async Task SendCommandAsync<T>(string type, T payload, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        try
        {
            Interlocked.Increment(ref _messagesSent);
            var payloadBytes = IpcPayloadHelper.SerializeToUtf8(payload);
            _logger?.LogTrace("IPC send: type={Type}, id={Id}, bytes={Bytes}", type, id, payloadBytes.Length);
            await _transport.SendAsync(type, payloadBytes, id, ct);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("IPC send TIMEOUT: type={Type}, id={Id}", type, id);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IPC send FAILED (pipe broken): type={Type}, id={Id}", type, id);
        }
    }

    /// <summary>
    /// Send a command and await its CommandResult reply. Use only for RPCs
    /// where the caller actually needs the result; everything else should
    /// remain fire-and-forget so a hung audio process doesn't stall the UI.
    /// </summary>
    private async Task<CommandResultMessage> SendRequestAsync<T>(string type, T payload, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<CommandResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, tcs))
            throw new InvalidOperationException($"Duplicate request id {id}");

        // Hook cancellation: if the caller cancels, fail the awaiter and stop
        // tracking the id. A late-arriving CommandResult will simply be dropped.
        using var reg = ct.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        });

        try
        {
            Interlocked.Increment(ref _messagesSent);
            var payloadBytes = IpcPayloadHelper.SerializeToUtf8(payload);
            _logger?.LogTrace("IPC request: type={Type}, id={Id}, bytes={Bytes}", type, id, payloadBytes.Length);
            await _transport.SendAsync(type, payloadBytes, id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(id, out _);
            tcs.TrySetException(ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendSimpleCommandAsync(string type, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        try
        {
            Interlocked.Increment(ref _messagesSent);
            if (type == IpcMessageTypes.Ping)
                _lastPingSentTimestamp = Stopwatch.GetTimestamp();
            _logger?.LogTrace("IPC send (simple): type={Type}, id={Id}", type, id);
            await _transport.SendAsync(type, id, ct);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("IPC send TIMEOUT: type={Type}, id={Id}", type, id);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IPC send FAILED (pipe broken): type={Type}, id={Id}", type, id);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _logger?.LogDebug("AudioPipelineProxy receive loop started");

        while (!ct.IsCancellationRequested)
        {
            IpcMessage? msg;
            try
            {
                msg = await _transport.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Audio pipe disconnected");
                _errorSubject.OnNext(new PlaybackError(
                    PlaybackErrorType.AudioDeviceUnavailable,
                    "Audio process disconnected"));
                break;
            }

            if (msg == null)
            {
                _logger?.LogWarning("Audio pipe closed");
                break;
            }

            try
            {
                HandleIncomingMessage(msg);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing message {Type}", msg.Type);
            }
        }

        IsConnected = false;

        // Fail any callers awaiting a CommandResult that will never come.
        FailAllPendingRequests("Audio pipe connection lost");

        _logger?.LogWarning(
            "AudioPipelineProxy receive loop ended: messagesReceived={Received}, stateUpdatesReceived={StateUpdates}, outOfOrder={OutOfOrder}, lastFreshness={FreshnessMs:F0}ms",
            Interlocked.Read(ref _messagesReceived),
            Interlocked.Read(ref _stateUpdatesReceived),
            Interlocked.Read(ref _stateUpdatesOutOfOrder),
            StateFreshnessMs);
        Disconnected?.Invoke("Audio pipe connection lost");
    }

    private void FailAllPendingRequests(string reason)
    {
        if (_pendingRequests.IsEmpty) return;
        foreach (var kv in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kv.Key, out var pending))
                pending.TrySetException(new IOException(reason));
        }
    }

    private void HandleIncomingMessage(IpcMessage msg)
    {
        Interlocked.Increment(ref _messagesReceived);
        var recvSeq = Interlocked.Increment(ref _receiveSequence);

        switch (msg.Type)
        {
            case IpcMessageTypes.StateUpdate:
            {
                var snapshot = IpcPayloadHelper.Deserialize<PlaybackStateSnapshot>(msg);
                if (snapshot == null)
                {
                    _logger?.LogWarning("[ipc#{Seq}] StateUpdate payload failed to deserialize", recvSeq);
                    break;
                }

                // Track freshness gap — interval between consecutive StateUpdates.
                // Large gaps mean the AudioHost went silent; rapid gaps mean chattiness.
                var previousRecvTicks = _lastStateUpdateTimestamp;
                _lastStateUpdateTimestamp = Stopwatch.GetTimestamp();
                var intervalMs = previousRecvTicks == 0
                    ? 0
                    : Stopwatch.GetElapsedTime(previousRecvTicks, _lastStateUpdateTimestamp).TotalMilliseconds;

                var stateSeq = Interlocked.Increment(ref _stateUpdatesReceived);

                // Remote-side timestamp regression = out-of-order apply risk.
                // Drop the stale snapshot — applying it would overwrite fresher state
                // that already reached subscribers.
                var prevRemote = _lastStateUpdateRemoteTimestamp;
                if (snapshot.Timestamp > 0 && prevRemote > snapshot.Timestamp)
                {
                    var outOfOrder = Interlocked.Increment(ref _stateUpdatesOutOfOrder);
                    _logger?.LogWarning(
                        "[ipc#{Seq} state#{StateSeq}] DROPPED out-of-order StateUpdate: prevTs={Prev} newTs={New} regressionMs={Reg} track={Track} outOfOrderTotal={Total}",
                        recvSeq, stateSeq, prevRemote, snapshot.Timestamp, prevRemote - snapshot.Timestamp, snapshot.TrackUri ?? "<none>", outOfOrder);
                    break;
                }
                _lastStateUpdateRemoteTimestamp = snapshot.Timestamp;

                // Suppress the every-2-second "nothing changed" tick — those carry
                // changes=0 and are just position updates from AudioHost. Only log when
                // there's a real state transition (track/status/position-jump/etc).
                if (snapshot.Changes != 0)
                {
                    _logger?.LogTrace(
                        "[ipc#{Seq} state#{StateSeq}] StateUpdate: src={Src}, track={Track}, pos={Pos}/{Dur}ms, playing={Playing}, buffering={Buffering}, changes={Changes}, remoteTs={RemoteTs}, intervalMs={Interval:F0}",
                        recvSeq, stateSeq, snapshot.Source, snapshot.TrackUri ?? "<none>", snapshot.PositionMs, snapshot.DurationMs,
                        snapshot.IsPlaying, snapshot.IsBuffering, snapshot.Changes, snapshot.Timestamp, intervalMs);
                }

                UnderrunCount = snapshot.UnderrunCount;

                // Persist audio device info across snapshots — AudioHost only re-sends the
                // device list when it changes, so we carry it forward from the last update.
                if (!string.IsNullOrEmpty(snapshot.ActiveAudioDeviceName))
                    _lastActiveAudioDeviceName = snapshot.ActiveAudioDeviceName;
                if (snapshot.AvailableAudioDevices is { Length: > 0 })
                    _lastAvailableAudioDevices = snapshot.AvailableAudioDevices;

                var state = new LocalPlaybackState
                {
                    Source = ParseStateSource(snapshot.Source),
                    ActiveDeviceId = snapshot.ActiveDeviceId,
                    ActiveDeviceName = snapshot.ActiveDeviceName,
                    Volume = snapshot.Volume,
                    IsVolumeRestricted = snapshot.IsVolumeRestricted,
                    TrackUri = snapshot.TrackUri,
                    TrackUid = snapshot.TrackUid,
                    TrackTitle = snapshot.TrackTitle,
                    TrackArtist = snapshot.TrackArtist,
                    TrackAlbum = snapshot.TrackAlbum,
                    AlbumUri = snapshot.AlbumUri,
                    ArtistUri = snapshot.ArtistUri,
                    ImageUrl = snapshot.ImageUrl,
                    ImageLargeUrl = snapshot.ImageLargeUrl,
                    ContextUri = snapshot.ContextUri,
                    PositionMs = snapshot.PositionMs,
                    DurationMs = snapshot.DurationMs,
                    IsPlaying = snapshot.IsPlaying,
                    IsPaused = snapshot.IsPaused,
                    IsBuffering = snapshot.IsBuffering,
                    Shuffling = snapshot.Shuffling,
                    RepeatingContext = snapshot.RepeatingContext,
                    RepeatingTrack = snapshot.RepeatingTrack,
                    CanSeek = snapshot.CanSeek,
                    Timestamp = snapshot.Timestamp,
                    UpstreamChanges = (StateChanges)snapshot.Changes,
                    ActiveAudioDeviceName = _lastActiveAudioDeviceName,
                    AvailableAudioDevices = _lastAvailableAudioDevices,
                };
                _stateSubject.OnNext(state);
                break;
            }
            case IpcMessageTypes.Error:
            {
                var error = IpcPayloadHelper.Deserialize<PlaybackErrorMessage>(msg);
                if (error == null) break;

                Enum.TryParse<PlaybackErrorType>(error.ErrorType, true, out var errorType);
                _errorSubject.OnNext(new PlaybackError(errorType, error.Message));
                break;
            }
            case IpcMessageTypes.CommandResult:
            {
                var result = IpcPayloadHelper.Deserialize<CommandResultMessage>(msg);
                if (result is null) break;

                // Complete the awaiter (if any) BEFORE surfacing as a generic
                // error toast — request/reply callers want to handle their own
                // failures (e.g. PlayPlay falls through to "stay disabled").
                if (_pendingRequests.TryRemove(result.RequestId, out var pending))
                {
                    pending.TrySetResult(result);
                    break;
                }

                if (!result.Success)
                {
                    var errMsg = result.ErrorMessage ?? "Audio host command failed";
                    _logger?.LogWarning("Command {Id} failed: {Error}", result.RequestId, errMsg);
                    _errorSubject.OnNext(new PlaybackError(
                        PlaybackErrorType.AudioDeviceUnavailable, errMsg));
                }
                break;
            }
            case IpcMessageTypes.TrackFinished:
            {
                var finished = IpcPayloadHelper.Deserialize<TrackFinishedMessage>(msg);
                if (finished != null)
                {
                    _logger?.LogInformation("Track finished: {TrackUri} reason={Reason}", finished.TrackUri, finished.Reason);
                    _trackFinishedSubject.OnNext(finished);
                }
                break;
            }
            case IpcMessageTypes.PreviewVisualizationFrame:
            {
                var frame = IpcPayloadHelper.Deserialize<PreviewVisualizationFrame>(msg);
                if (frame != null)
                    _previewVisualizationFrameSubject.OnNext(frame);
                break;
            }
            case IpcMessageTypes.Pong:
            {
                // Compute RTT
                if (_lastPingSentTimestamp > 0)
                {
                    LastRttMs = Stopwatch.GetElapsedTime(_lastPingSentTimestamp).TotalMilliseconds;
                    AppendRttSample(LastRttMs, Stopwatch.GetTimestamp());
                }
                PongReceived?.Invoke();
                break;
            }
            default:
                _logger?.LogDebug("Unknown message from audio host: {Type}", msg.Type);
                break;
        }
    }

    private static StateSource? ParseStateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        return source.Equals("cluster", StringComparison.OrdinalIgnoreCase)
            ? StateSource.Cluster
            : source.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? StateSource.Local
                : null;
    }

    private void AppendRttSample(double value, long timestamp)
    {
        if (RttHistoryCount == RttHistory.Length)
        {
            Array.Copy(RttHistory, 1, RttHistory, 0, RttHistory.Length - 1);
            Array.Copy(_rttHistoryTimestamps, 1, _rttHistoryTimestamps, 0, _rttHistoryTimestamps.Length - 1);
            RttHistoryCount--;
        }

        RttHistory[RttHistoryCount] = value;
        _rttHistoryTimestamps[RttHistoryCount] = timestamp;
        RttHistoryCount++;

        // Keep only samples from the last minute to prevent unbounded diagnostics growth.
        var cutoffTimestamp = timestamp - (long)(RttHistoryWindow.TotalSeconds * Stopwatch.Frequency);
        int expiredCount = 0;
        while (expiredCount < RttHistoryCount && _rttHistoryTimestamps[expiredCount] < cutoffTimestamp)
        {
            expiredCount++;
        }

        if (expiredCount <= 0)
        {
            return;
        }

        var remaining = RttHistoryCount - expiredCount;
        if (remaining > 0)
        {
            Array.Copy(RttHistory, expiredCount, RttHistory, 0, remaining);
            Array.Copy(_rttHistoryTimestamps, expiredCount, _rttHistoryTimestamps, 0, remaining);
        }

        RttHistoryCount = remaining;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_receiveLoop != null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* timeout is fine */ }
        }

        _stateSubject.Dispose();
        _errorSubject.Dispose();
        _previewVisualizationFrameSubject.Dispose();
        await _transport.DisposeAsync();
        _cts.Dispose();
    }
}
