using Microsoft.Extensions.Logging;

namespace Wavee.Connect;

/// <summary>
/// Configuration for the dealer client connection.
/// </summary>
public sealed class DealerClientConfig
{
    /// <summary>
    /// Optional logger for dealer client operations.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Timeout for connection attempts. Default is 30 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for operations. Default is 10 seconds.
    /// </summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to automatically start the connection on client creation.
    /// Default is false.
    /// </summary>
    public bool AutoConnect { get; init; } = false;

    /// <summary>
    /// Interval between client-initiated PING messages. Default is 30 seconds.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time to wait for PONG response before considering connection dead.
    /// Default is 3 seconds.
    /// </summary>
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether to enable automatic reconnection on connection failure. Default is true.
    /// </summary>
    public bool EnableAutoReconnect { get; init; } = true;

    /// <summary>
    /// Initial delay before first reconnection attempt. Default is 1 second.
    /// </summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum delay between reconnection attempts. Default is 300 seconds (5 minutes).
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Maximum number of reconnection attempts. Null means unlimited. Default is unlimited.
    /// </summary>
    public int? MaxReconnectAttempts { get; init; } = null;
}
