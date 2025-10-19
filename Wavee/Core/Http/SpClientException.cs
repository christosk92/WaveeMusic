namespace Wavee.Core.Http;

/// <summary>
/// Exception thrown when a SpClient operation fails.
/// </summary>
public sealed class SpClientException : Exception
{
    /// <summary>
    /// Reason for the SpClient failure.
    /// </summary>
    public SpClientFailureReason Reason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpClientException"/> class.
    /// </summary>
    /// <param name="reason">The failure reason.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SpClientException(
        SpClientFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for SpClient failures.
/// </summary>
public enum SpClientFailureReason
{
    /// <summary>
    /// HTTP request failed.
    /// </summary>
    RequestFailed,

    /// <summary>
    /// Unauthorized (401) - access token invalid or expired.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Not found (404) - resource doesn't exist.
    /// </summary>
    NotFound,

    /// <summary>
    /// Rate limited (429) - too many requests.
    /// </summary>
    RateLimited,

    /// <summary>
    /// Server error (5xx).
    /// </summary>
    ServerError
}
