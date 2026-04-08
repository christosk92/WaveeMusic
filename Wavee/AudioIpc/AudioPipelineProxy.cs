using System.Diagnostics;
using System.IO.Pipes;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Commands;

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

    private long _nextRequestId;
    private Task? _receiveLoop;

    // ── IPC Metrics ──
    private long _lastPingSentTimestamp;
    private long _messagesReceived;
    private long _messagesSent;
    private long _lastStateUpdateTimestamp;

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
    /// Sends credentials to the audio process and waits for the Ready message.
    /// </summary>
    public async Task<bool> HandshakeAsync(string username, byte[] storedCredential, string deviceId, CancellationToken ct)
    {
        var credsJson = JsonSerializer.SerializeToUtf8Bytes(
            new CredentialsHandshake(username, Convert.ToBase64String(storedCredential), deviceId),
            typeof(CredentialsHandshake),
            IpcJsonContext.Default);

        await _transport.SendAsync("credentials", credsJson, ct: ct);

        // Wait for Ready
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
        var cmd = new PlayContextCommand
        {
            ContextUri = command.ContextUri ?? "",
            TrackUri = command.TrackUri,
            TrackIndex = command.SkipToIndex,
            PositionMs = command.PositionMs,
            PageTracks = command.PageTracks?.Select(t => new PageTrackDto
            {
                Uri = t.Uri,
                Uid = t.Uid
            }).ToList(),
        };
        await SendCommandAsync(IpcMessageTypes.PlayContext, cmd, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Pause, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Stop, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Resume, cancellationToken);

    public Task SeekAsync(long positionMs, CancellationToken cancellationToken = default)
        => SendCommandAsync(IpcMessageTypes.Seek, new SeekCommand { PositionMs = positionMs }, cancellationToken);

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

    public Task SwitchQualityAsync(string quality, CancellationToken ct = default)
        => SendCommandAsync(IpcMessageTypes.SwitchQuality,
            new SwitchQualityCommand { Quality = quality }, ct);

    public Task ShutdownAsync(CancellationToken ct = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Shutdown, ct);

    public Task SendPingAsync(CancellationToken ct = default)
        => SendSimpleCommandAsync(IpcMessageTypes.Ping, ct);

    // ── Internals ──

    private async Task SendCommandAsync<T>(string type, T payload, CancellationToken ct)
    {
        try
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            Interlocked.Increment(ref _messagesSent);
            var payloadBytes = IpcPayloadHelper.SerializeToUtf8(payload);
            await _transport.SendAsync(type, payloadBytes, id, ct);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("IPC send timed out for {Type}", type);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IPC send failed for {Type} — pipe broken", type);
        }
    }

    private async Task SendSimpleCommandAsync(string type, CancellationToken ct)
    {
        try
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            Interlocked.Increment(ref _messagesSent);
            if (type == IpcMessageTypes.Ping)
                _lastPingSentTimestamp = Stopwatch.GetTimestamp();
            await _transport.SendAsync(type, id, ct);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("IPC send timed out for {Type}", type);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IPC send failed for {Type} — pipe broken", type);
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
        _logger?.LogDebug("AudioPipelineProxy receive loop ended");
        Disconnected?.Invoke("Audio pipe connection lost");
    }

    private void HandleIncomingMessage(IpcMessage msg)
    {
        Interlocked.Increment(ref _messagesReceived);

        switch (msg.Type)
        {
            case IpcMessageTypes.StateUpdate:
            {
                var snapshot = IpcPayloadHelper.Deserialize<PlaybackStateSnapshot>(msg);
                if (snapshot == null) break;

                _lastStateUpdateTimestamp = Stopwatch.GetTimestamp();
                UnderrunCount = snapshot.UnderrunCount;

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
                if (result is { Success: false })
                {
                    _logger?.LogWarning("Command {Id} failed: {Error}", result.RequestId, result.ErrorMessage);
                }
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
        await _transport.DisposeAsync();
        _cts.Dispose();
    }
}
