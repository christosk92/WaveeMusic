using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wavee.Connect;

/// <summary>
/// Manages heartbeat mechanism for dealer connections.
/// Sends client-initiated PING messages and monitors PONG responses with timeout detection.
/// </summary>
internal sealed class HeartbeatManager : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _pongTimeout;
    private readonly Func<ValueTask> _sendPingAsync;

    private PeriodicTimer? _pingTimer;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;

    private DateTime _lastPongReceived;
    private bool _waitingForPong;
    private readonly object _lock = new();

    /// <summary>
    /// Raised when a heartbeat timeout is detected (no PONG received within timeout).
    /// </summary>
    public event EventHandler? HeartbeatTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatManager"/> class.
    /// </summary>
    /// <param name="pingInterval">Interval between PING messages.</param>
    /// <param name="pongTimeout">Maximum time to wait for PONG response.</param>
    /// <param name="sendPingAsync">Callback to send PING message.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public HeartbeatManager(
        TimeSpan pingInterval,
        TimeSpan pongTimeout,
        Func<ValueTask> sendPingAsync,
        ILogger? logger = null)
    {
        _pingInterval = pingInterval;
        _pongTimeout = pongTimeout;
        _sendPingAsync = sendPingAsync ?? throw new ArgumentNullException(nameof(sendPingAsync));
        _logger = logger;
        _lastPongReceived = DateTime.UtcNow;
    }

    /// <summary>
    /// Starts the heartbeat loop.
    /// </summary>
    public void Start()
    {
        if (_pingTimer != null)
            throw new InvalidOperationException("Heartbeat already started");

        _cts = new CancellationTokenSource();
        _pingTimer = new PeriodicTimer(_pingInterval);
        _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);

        _logger?.LogDebug("Heartbeat manager started (interval: {Interval}s, timeout: {Timeout}s)",
            _pingInterval.TotalSeconds, _pongTimeout.TotalSeconds);
    }

    /// <summary>
    /// Records that a PONG message was received.
    /// Should be called when the dealer client receives a PONG.
    /// </summary>
    public void RecordPong()
    {
        lock (_lock)
        {
            _lastPongReceived = DateTime.UtcNow;
            _waitingForPong = false;
        }

        _logger?.LogTrace("PONG received, heartbeat alive");
    }

    /// <summary>
    /// Stops the heartbeat loop.
    /// </summary>
    public async ValueTask StopAsync()
    {
        if (_pingTimer == null)
            return;

        _logger?.LogDebug("Stopping heartbeat manager");

        _cts?.Cancel();
        _pingTimer?.Dispose();
        _pingTimer = null;

        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;
        _heartbeatTask = null;

        _logger?.LogDebug("Heartbeat manager stopped");
    }

    /// <summary>
    /// Main heartbeat loop that sends PINGs and checks for timeouts.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _pingTimer!.WaitForNextTickAsync(cancellationToken))
            {
                // Check if previous PONG was received (if we were waiting for one)
                bool timedOut = false;
                lock (_lock)
                {
                    if (_waitingForPong)
                    {
                        var elapsed = DateTime.UtcNow - _lastPongReceived;
                        if (elapsed > _pongTimeout)
                        {
                            timedOut = true;
                            _logger?.LogWarning("Heartbeat timeout detected. No PONG received in {Elapsed}s (timeout: {Timeout}s)",
                                elapsed.TotalSeconds, _pongTimeout.TotalSeconds);
                        }
                    }
                }

                if (timedOut)
                {
                    // Raise timeout event and stop
                    HeartbeatTimeout?.Invoke(this, EventArgs.Empty);
                    break;
                }

                // Send PING
                try
                {
                    await _sendPingAsync();

                    lock (_lock)
                    {
                        _waitingForPong = true;
                    }

                    _logger?.LogTrace("Sent PING, waiting for PONG");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to send PING");
                    // Connection is likely broken, trigger timeout
                    HeartbeatTimeout?.Invoke(this, EventArgs.Empty);
                    break;
                }

                // Timeout detection happens at next timer tick (main loop check above)
                // This simplifies the logic and eliminates race conditions
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
            _logger?.LogDebug("Heartbeat loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in heartbeat loop");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
