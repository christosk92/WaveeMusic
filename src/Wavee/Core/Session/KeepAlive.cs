using Microsoft.Extensions.Logging;

namespace Wavee.Core.Session;

/// <summary>
/// Keep-alive state machine for detecting dead connections.
/// Matches librespot's 3-state protocol exactly.
/// </summary>
/// <remarks>
/// Expected keep-alive sequence:
///   Server: Ping (with timestamp)
///   wait 60s
///   Client: Pong
///   Server: PongAck
///   wait ~60s
///   repeat
///
/// Timeouts:
///   - ExpectingPing: 20s on first cycle, 80s after (60s expected interval + 20s buffer)
///   - PendingPong: 60s delay before sending Pong
///   - ExpectingPongAck: 20s for server to acknowledge
///
/// Disconnect on first timeout (no missed-pong counter).
/// </remarks>
internal sealed class KeepAlive
{
    private static readonly TimeSpan InitialPingTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(80);
    private static readonly TimeSpan PongDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PongAckTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger? _logger;

    private KeepAliveState _state;
    private DateTime _stateEnteredAt;
    private bool _isFirstCycle;

    public KeepAlive(ILogger? logger = null)
    {
        _logger = logger;
        Reset();
    }

    /// <summary>
    /// Resets the state machine to the initial state (expecting first Ping from server).
    /// </summary>
    public void Reset()
    {
        _state = KeepAliveState.ExpectingPing;
        _stateEnteredAt = DateTime.UtcNow;
        _isFirstCycle = true;
    }

    /// <summary>
    /// Evaluates the current state and returns the next action.
    /// Called every dispatcher loop iteration (~100ms).
    /// </summary>
    public KeepAliveAction Evaluate()
    {
        var elapsed = DateTime.UtcNow - _stateEnteredAt;

        switch (_state)
        {
            case KeepAliveState.ExpectingPing:
            {
                var timeout = _isFirstCycle ? InitialPingTimeout : PingTimeout;
                if (elapsed > timeout)
                {
                    _logger?.LogWarning(
                        "Keep-alive timeout in {State} after {Elapsed:F1}s (limit {Timeout:F0}s, firstCycle={First})",
                        _state, elapsed.TotalSeconds, timeout.TotalSeconds, _isFirstCycle);
                    return KeepAliveAction.Disconnect;
                }
                return KeepAliveAction.Wait;
            }

            case KeepAliveState.PendingPong:
            {
                if (elapsed >= PongDelay)
                {
                    _logger?.LogDebug("PongDelay elapsed ({Elapsed:F1}s), sending Pong", elapsed.TotalSeconds);
                    TransitionTo(KeepAliveState.ExpectingPongAck);
                    return KeepAliveAction.SendPong;
                }
                return KeepAliveAction.Wait;
            }

            case KeepAliveState.ExpectingPongAck:
            {
                if (elapsed > PongAckTimeout)
                {
                    _logger?.LogWarning(
                        "Keep-alive timeout in {State} after {Elapsed:F1}s (limit {Timeout:F0}s)",
                        _state, elapsed.TotalSeconds, PongAckTimeout.TotalSeconds);
                    return KeepAliveAction.Disconnect;
                }
                return KeepAliveAction.Wait;
            }

            default:
                return KeepAliveAction.Wait;
        }
    }

    /// <summary>
    /// Called when a Ping packet is received from the server.
    /// Transitions to PendingPong state (Pong will be sent after 60s delay).
    /// </summary>
    public void OnPingReceived()
    {
        if (_state != KeepAliveState.ExpectingPing)
        {
            _logger?.LogWarning("Received unexpected Ping from server (state={State})", _state);
        }

        _logger?.LogTrace("Received Ping, transitioning to PendingPong (will send Pong in {Delay}s)",
            PongDelay.TotalSeconds);
        TransitionTo(KeepAliveState.PendingPong);
    }

    /// <summary>
    /// Called when a PongAck packet is received from the server.
    /// Transitions back to ExpectingPing state.
    /// </summary>
    public void OnPongAckReceived()
    {
        if (_state != KeepAliveState.ExpectingPongAck)
        {
            _logger?.LogWarning("Received unexpected PongAck from server (state={State})", _state);
        }

        _logger?.LogTrace("Received PongAck, keep-alive confirmed");
        _isFirstCycle = false;
        TransitionTo(KeepAliveState.ExpectingPing);
    }

    private void TransitionTo(KeepAliveState newState)
    {
        _state = newState;
        _stateEnteredAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Internal states for the keep-alive protocol.
/// </summary>
internal enum KeepAliveState
{
    /// <summary>
    /// Waiting for the server to send a Ping packet.
    /// </summary>
    ExpectingPing,

    /// <summary>
    /// Ping received; waiting for PongDelay (60s) before sending Pong.
    /// </summary>
    PendingPong,

    /// <summary>
    /// Pong sent; waiting for server's PongAck.
    /// </summary>
    ExpectingPongAck
}

/// <summary>
/// Actions returned by <see cref="KeepAlive.Evaluate"/>.
/// </summary>
internal enum KeepAliveAction
{
    /// <summary>
    /// No action needed.
    /// </summary>
    Wait,

    /// <summary>
    /// Send a Pong packet to the server (PongDelay has elapsed).
    /// </summary>
    SendPong,

    /// <summary>
    /// Disconnect — keep-alive timeout exceeded.
    /// </summary>
    Disconnect
}
