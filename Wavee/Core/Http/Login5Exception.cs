using Wavee.Protocol.Login;

namespace Wavee.Core.Http;

/// <summary>
/// Exception thrown when a login5 operation fails.
/// </summary>
public sealed class Login5Exception : Exception
{
    /// <summary>
    /// Reason for the login5 failure.
    /// </summary>
    public Login5FailureReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Login5Exception"/> class.
    /// </summary>
    /// <param name="reason">The failure reason.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public Login5Exception(
        Login5FailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>
    /// Creates a Login5Exception from a LoginError protobuf enum.
    /// </summary>
    /// <param name="error">The login error from the server.</param>
    /// <returns>A Login5Exception with the appropriate reason and message.</returns>
    internal static Login5Exception FromLoginError(LoginError error)
    {
        var reason = error switch
        {
            LoginError.InvalidCredentials => Login5FailureReason.InvalidCredentials,
            LoginError.BadRequest => Login5FailureReason.BadRequest,
            LoginError.UnsupportedLoginProtocol => Login5FailureReason.UnsupportedProtocol,
            LoginError.Timeout => Login5FailureReason.Timeout,
            LoginError.UnknownIdentifier => Login5FailureReason.UnknownIdentifier,
            LoginError.TooManyAttempts => Login5FailureReason.TooManyAttempts,
            LoginError.InvalidPhonenumber => Login5FailureReason.InvalidPhoneNumber,
            LoginError.TryAgainLater => Login5FailureReason.TryAgainLater,
            _ => Login5FailureReason.Unknown
        };

        return new Login5Exception(reason, $"Login5 authentication failed: {error}");
    }
}

/// <summary>
/// Reasons for login5 failures.
/// </summary>
public enum Login5FailureReason
{
    /// <summary>
    /// Unknown error.
    /// </summary>
    Unknown,

    /// <summary>
    /// Invalid credentials provided.
    /// </summary>
    InvalidCredentials,

    /// <summary>
    /// Malformed request.
    /// </summary>
    BadRequest,

    /// <summary>
    /// Unsupported login protocol.
    /// </summary>
    UnsupportedProtocol,

    /// <summary>
    /// Request timed out.
    /// </summary>
    Timeout,

    /// <summary>
    /// Unknown identifier (username not found).
    /// </summary>
    UnknownIdentifier,

    /// <summary>
    /// Too many authentication attempts.
    /// </summary>
    TooManyAttempts,

    /// <summary>
    /// Invalid phone number.
    /// </summary>
    InvalidPhoneNumber,

    /// <summary>
    /// Server requested retry later.
    /// </summary>
    TryAgainLater,

    /// <summary>
    /// No stored credentials available.
    /// </summary>
    NoStoredCredentials,

    /// <summary>
    /// Code challenge is not supported.
    /// </summary>
    CodeChallengeNotSupported,

    /// <summary>
    /// Maximum retry attempts exceeded.
    /// </summary>
    MaxRetriesExceeded,

    /// <summary>
    /// Login5 response missing Ok field.
    /// </summary>
    NoOkResponse
}
