namespace Wavee.Connect.Connection;

/// <summary>
/// Exception thrown when WebSocket connection operations fail.
/// </summary>
internal sealed class DealerConnectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DealerConnectionException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DealerConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
