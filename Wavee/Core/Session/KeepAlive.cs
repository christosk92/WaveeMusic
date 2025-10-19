using Microsoft.Extensions.Logging;

namespace Wavee.Core.Session;

/// <summary>
/// Keep-alive state machine for detecting dead connections.
/// </summary>
/// <remarks>
/// Protocol:
/// - Send Ping every 30 seconds
/// - Expect Pong within 10 seconds
/// - Disconnect after 3 missed Pongs
///
/// This follows Spotify's keep-alive protocol from librespot.
/// </remarks>
internal sealed class KeepAlive
{
    // Librespot keepalive: server sends Ping, client sends Pong, server replies PongAck.
    // We only track timeouts; we do not proactively Ping.
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(80); // generous margin
    private const int MaxMissedPongs = 3;

    private readonly ILogger? _logger;

    public KeepAlive(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines the next action based on current keep-alive state.
    /// </summary>
    /// <param name="lastPingSent">Timestamp of last ping sent.</param>
    /// <param name="lastPongReceived">Timestamp of last pong received.</param>
    /// <param name="missedPongs">Number of consecutive missed pongs.</param>
    /// <returns>The next action to take.</returns>
    public KeepAliveAction GetNextAction(
        DateTime lastPingSent,
        DateTime lastPongReceived,
        int missedPongs)
    {
        var now = DateTime.UtcNow;

        // Check for too many missed pongs
        if (missedPongs >= MaxMissedPongs)
        {
            _logger?.LogWarning("Keep-alive failed: {MissedPongs} consecutive missed pongs", missedPongs);
            return KeepAliveAction.Disconnect;
        }

        // Check if we're waiting for a pong
        if (lastPingSent > lastPongReceived)
        {
            var timeSincePing = now - lastPingSent;
            if (timeSincePing > PongTimeout)
            {
                _logger?.LogWarning("Pong timeout after {Elapsed:F1}s", timeSincePing.TotalSeconds);
                return KeepAliveAction.IncrementMissedPong;
            }

            // Still waiting for pong, no action needed
            return KeepAliveAction.Wait;
        }

        // No proactive client Ping; rely on server Ping and PongAck timeouts
        return KeepAliveAction.Wait;
    }
}

/// <summary>
/// Actions for the keep-alive state machine.
/// </summary>
internal enum KeepAliveAction
{
    /// <summary>
    /// No action needed, wait for next check.
    /// </summary>
    Wait,

    /// <summary>
    /// Send a ping packet.
    /// </summary>
    SendPing,

    /// <summary>
    /// Increment missed pong counter (pong timeout occurred).
    /// </summary>
    IncrementMissedPong,

    /// <summary>
    /// Disconnect the session (too many missed pongs).
    /// </summary>
    Disconnect
}
