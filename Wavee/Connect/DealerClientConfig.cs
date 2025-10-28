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
}
