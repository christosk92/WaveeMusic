using System.Net.WebSockets;

namespace Wavee.Connect.Connection;

/// <summary>
/// Interface for dealer WebSocket connection abstraction.
/// Enables dependency injection and mocking in tests.
/// </summary>
public interface IDealerConnection : IAsyncDisposable
{
    /// <summary>
    /// Raised when a complete WebSocket message is received.
    /// Handler receives UTF-8 encoded message bytes.
    /// </summary>
    event Func<ReadOnlyMemory<byte>, ValueTask>? MessageReceived;

    /// <summary>
    /// Raised when the WebSocket connection is closed.
    /// </summary>
    event EventHandler<WebSocketCloseStatus?>? Closed;

    /// <summary>
    /// Raised when an error occurs during receive/process.
    /// </summary>
    event EventHandler<Exception>? Error;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Connects to the WebSocket endpoint.
    /// </summary>
    /// <param name="wsUrl">WebSocket URL (wss://...).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ConnectAsync(string wsUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a UTF-8 encoded message.
    /// </summary>
    /// <param name="utf8Message">UTF-8 encoded message bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> utf8Message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a string message (encodes to UTF-8).
    /// </summary>
    /// <param name="message">String message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the WebSocket connection gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
