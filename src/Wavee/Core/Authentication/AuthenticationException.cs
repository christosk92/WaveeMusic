namespace Wavee.Core.Authentication;

/// <summary>
/// Exception thrown when authentication with Spotify Access Point fails.
/// </summary>
public sealed class AuthenticationException : Exception
{
    /// <summary>
    /// Reason for authentication failure.
    /// </summary>
    public AuthenticationFailureReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="AuthenticationException"/>.
    /// </summary>
    /// <param name="reason">The reason for authentication failure.</param>
    /// <param name="message">The error message.</param>
    public AuthenticationException(AuthenticationFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="AuthenticationException"/>.
    /// </summary>
    /// <param name="reason">The reason for authentication failure.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AuthenticationException(AuthenticationFailureReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for authentication failure.
/// </summary>
public enum AuthenticationFailureReason
{
    /// <summary>
    /// Invalid username or password provided.
    /// </summary>
    BadCredentials,

    /// <summary>
    /// Account requires Spotify Premium subscription.
    /// </summary>
    PremiumRequired,

    /// <summary>
    /// Server requested trying a different Access Point.
    /// </summary>
    TryAnotherAp,

    /// <summary>
    /// Encrypted credential blob decryption failed.
    /// </summary>
    InvalidBlob,

    /// <summary>
    /// Unexpected packet type received during authentication.
    /// </summary>
    UnexpectedPacket,

    /// <summary>
    /// Transport connection closed unexpectedly during authentication.
    /// </summary>
    TransportClosed,

    /// <summary>
    /// Generic login failure.
    /// </summary>
    LoginFailed,

    /// <summary>
    /// Protocol error occurred during authentication.
    /// </summary>
    ProtocolError
}
