namespace Wavee.Core.OAuth;

/// <summary>
/// Exception thrown when OAuth 2.0 authentication or token operations fail.
/// </summary>
public sealed class OAuthException : Exception
{
    /// <summary>
    /// The specific reason for the OAuth failure.
    /// </summary>
    public OAuthFailureReason Reason { get; }

    /// <summary>
    /// Creates a new OAuth exception with a specific failure reason and message.
    /// </summary>
    /// <param name="reason">The specific reason for the failure.</param>
    /// <param name="message">Human-readable error message.</param>
    public OAuthException(OAuthFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Creates a new OAuth exception with a specific failure reason, message, and inner exception.
    /// </summary>
    /// <param name="reason">The specific reason for the failure.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="innerException">The exception that caused this OAuth failure.</param>
    public OAuthException(OAuthFailureReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Specific reasons why an OAuth operation might fail.
/// </summary>
public enum OAuthFailureReason
{
    /// <summary>
    /// Invalid client ID provided.
    /// </summary>
    InvalidClient,

    /// <summary>
    /// Authorization code is invalid, expired, or already used.
    /// </summary>
    InvalidGrant,

    /// <summary>
    /// Client is not authorized to use this grant type.
    /// </summary>
    UnauthorizedClient,

    /// <summary>
    /// One or more requested scopes are invalid or not allowed.
    /// </summary>
    InvalidScope,

    /// <summary>
    /// User explicitly denied the authorization request.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// Spotify authorization server encountered an internal error.
    /// </summary>
    ServerError,

    /// <summary>
    /// Network error prevented completing the OAuth request.
    /// </summary>
    NetworkError,

    /// <summary>
    /// Token has expired and needs to be refreshed.
    /// </summary>
    TokenExpired,

    /// <summary>
    /// Refresh token is invalid or has been revoked.
    /// </summary>
    InvalidRefreshToken,

    /// <summary>
    /// Authorization is pending - user has not yet completed authorization (Device Code Flow).
    /// </summary>
    AuthorizationPending,

    /// <summary>
    /// Polling too frequently - need to slow down (Device Code Flow).
    /// </summary>
    SlowDown,

    /// <summary>
    /// Device code has expired (Device Code Flow).
    /// </summary>
    ExpiredDeviceCode,

    /// <summary>
    /// Failed to start local HTTP server for callback (Authorization Code Flow).
    /// </summary>
    ServerStartFailed,

    /// <summary>
    /// Failed to parse redirect URI or extract authorization code.
    /// </summary>
    InvalidRedirectUri,

    /// <summary>
    /// Authorization code was not found in the callback URI.
    /// </summary>
    CodeNotFound,

    /// <summary>
    /// HTTP listener terminated without receiving authorization callback.
    /// </summary>
    ListenerTerminated,

    /// <summary>
    /// Invalid PKCE verifier or challenge.
    /// </summary>
    InvalidPkce,

    /// <summary>
    /// Failed to open browser for authorization.
    /// </summary>
    BrowserLaunchFailed,

    /// <summary>
    /// Timeout while waiting for user authorization.
    /// </summary>
    Timeout,

    /// <summary>
    /// Operation was cancelled by user or application.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Unknown or unspecified OAuth error.
    /// </summary>
    Unknown
}
