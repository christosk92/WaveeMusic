using System.Net.WebSockets;
using System.Text;
using Wavee.Connect.Connection;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Mock implementation of IDealerConnection for testing DealerClient.
/// Allows tests to simulate WebSocket message flow without real connections.
/// </summary>
internal class MockDealerConnection : IDealerConnection
{
    private readonly List<byte[]> _sentMessages = new();
    private readonly List<string> _sentStrings = new();

    public event Func<ReadOnlyMemory<byte>, ValueTask>? MessageReceived;
    public event EventHandler<WebSocketCloseStatus?>? Closed;
    public event EventHandler<Exception>? Error;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>
    /// Gets all messages sent via SendAsync(ReadOnlyMemory).
    /// </summary>
    public IReadOnlyList<byte[]> SentMessages => _sentMessages.AsReadOnly();

    /// <summary>
    /// Gets all messages sent via SendAsync(string).
    /// </summary>
    public IReadOnlyList<string> SentStrings => _sentStrings.AsReadOnly();

    /// <summary>
    /// Simulates connecting to a WebSocket.
    /// </summary>
    public ValueTask ConnectAsync(string wsUrl, CancellationToken cancellationToken = default)
    {
        if (State == ConnectionState.Connected)
            throw new InvalidOperationException("Already connected");

        State = ConnectionState.Connected;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Simulates sending a UTF-8 encoded message.
    /// Stores the message for later verification.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8Message, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
            throw new InvalidOperationException("Not connected");

        _sentMessages.Add(utf8Message.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Simulates sending a string message.
    /// Stores the message for later verification.
    /// </summary>
    public ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
            throw new InvalidOperationException("Not connected");

        _sentStrings.Add(message);
        _sentMessages.Add(Encoding.UTF8.GetBytes(message));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Simulates closing the WebSocket connection.
    /// </summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Simulates receiving a message from the dealer.
    /// Invokes the MessageReceived event.
    /// </summary>
    /// <param name="message">UTF-8 encoded message bytes.</param>
    public async Task SimulateMessageAsync(ReadOnlyMemory<byte> message)
    {
        if (MessageReceived != null)
        {
            await MessageReceived.Invoke(message);
        }
    }

    /// <summary>
    /// Simulates receiving a message from the dealer (string variant).
    /// Invokes the MessageReceived event.
    /// </summary>
    /// <param name="json">JSON message string.</param>
    public async Task SimulateMessageAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await SimulateMessageAsync(bytes);
    }

    /// <summary>
    /// Simulates the connection being closed by the server.
    /// Invokes the Closed event.
    /// </summary>
    /// <param name="closeStatus">Optional close status.</param>
    public void SimulateClosed(WebSocketCloseStatus? closeStatus = null)
    {
        State = ConnectionState.Disconnected;
        Closed?.Invoke(this, closeStatus);
    }

    /// <summary>
    /// Simulates an error occurring during receive/process.
    /// Invokes the Error event.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    public void SimulateError(Exception exception)
    {
        Error?.Invoke(this, exception);
    }

    /// <summary>
    /// Clears all sent message history.
    /// </summary>
    public void ClearSentMessages()
    {
        _sentMessages.Clear();
        _sentStrings.Clear();
    }

    public ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        MessageReceived = null;
        Closed = null;
        Error = null;
        return ValueTask.CompletedTask;
    }
}
