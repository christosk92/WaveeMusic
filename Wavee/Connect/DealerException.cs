namespace Wavee.Connect;

/// <summary>
/// Exception thrown when dealer operations fail.
/// </summary>
public sealed class DealerException : Exception
{
    /// <summary>
    /// Reason for the dealer failure.
    /// </summary>
    public DealerFailureReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DealerException"/> class.
    /// </summary>
    /// <param name="reason">The failure reason.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DealerException(
        DealerFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for dealer failures.
/// </summary>
public enum DealerFailureReason
{
    /// <summary>
    /// Failed to resolve dealer endpoint.
    /// </summary>
    ResolveFailed,

    /// <summary>
    /// WebSocket connection failed.
    /// </summary>
    ConnectionFailed,

    /// <summary>
    /// Access token is invalid or expired.
    /// </summary>
    InvalidToken,

    /// <summary>
    /// Heartbeat timeout (no PONG received).
    /// </summary>
    HeartbeatTimeout,

    /// <summary>
    /// WebSocket closed unexpectedly.
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// Message parsing/processing error.
    /// </summary>
    MessageError,

    /// <summary>
    /// Dealer client was disposed.
    /// </summary>
    Disposed
}
