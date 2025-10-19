namespace Wavee.Core.Session;

/// <summary>
/// Exception thrown when a session operation fails.
/// </summary>
public sealed class SessionException : Exception
{
    /// <summary>
    /// Reason for the session failure.
    /// </summary>
    public SessionFailureReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionException"/> class.
    /// </summary>
    /// <param name="reason">The failure reason.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SessionException(
        SessionFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for session failures.
/// </summary>
public enum SessionFailureReason
{
    /// <summary>
    /// Failed to resolve Access Point servers.
    /// </summary>
    ApResolveFailed,

    /// <summary>
    /// Failed to connect to any Access Point.
    /// </summary>
    ConnectionFailed,

    /// <summary>
    /// Handshake with Access Point failed.
    /// </summary>
    HandshakeFailed,

    /// <summary>
    /// Authentication failed.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// Connection lost (keep-alive failure).
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// Session was disposed.
    /// </summary>
    Disposed,

    /// <summary>
    /// Operation requires Premium subscription.
    /// </summary>
    PremiumRequired,

    /// <summary>
    /// Unexpected protocol error.
    /// </summary>
    ProtocolError
}
