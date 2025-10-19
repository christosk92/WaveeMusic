namespace Wavee.Core.Connection;

/// <summary>
/// Exception thrown during the Spotify handshake process.
/// </summary>
public sealed class HandshakeException : Exception
{
    /// <summary>
    /// Gets the reason for the handshake failure.
    /// </summary>
    public HandshakeReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HandshakeException"/> class.
    /// </summary>
    /// <param name="reason">The reason for the handshake failure.</param>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public HandshakeException(HandshakeReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HandshakeException"/> class with an inner exception.
    /// </summary>
    /// <param name="reason">The reason for the handshake failure.</param>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public HandshakeException(HandshakeReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for handshake failure.
/// </summary>
public enum HandshakeReason
{
    /// <summary>
    /// Invalid key length was provided.
    /// </summary>
    InvalidKeyLength,

    /// <summary>
    /// Server signature verification failed (potential MITM attack).
    /// </summary>
    ServerVerificationFailed,

    /// <summary>
    /// Network error occurred during handshake.
    /// </summary>
    NetworkError,

    /// <summary>
    /// Protocol error (malformed or unexpected response).
    /// </summary>
    ProtocolError
}
