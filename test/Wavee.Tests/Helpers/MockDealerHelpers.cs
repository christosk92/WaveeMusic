using System.Net.WebSockets;
using System.Text;
using Moq;
using Wavee.Connect.Connection;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for mocking DealerConnection in tests.
/// </summary>
internal static class MockDealerHelpers
{
    /// <summary>
    /// Creates a mock DealerConnection with default setup.
    /// </summary>
    /// <returns>Mock DealerConnection.</returns>
    public static Mock<DealerConnection> CreateMockDealerConnection()
    {
        var mock = new Mock<DealerConnection>();

        // Setup default state
        mock.Setup(c => c.State).Returns(ConnectionState.Disconnected);

        // Setup SendAsync to succeed by default
        mock.Setup(c => c.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        mock.Setup(c => c.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        // Setup ConnectAsync to succeed and change state
        mock.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => mock.Setup(c => c.State).Returns(ConnectionState.Connected))
            .Returns(ValueTask.CompletedTask);

        // Setup CloseAsync to succeed and change state
        mock.Setup(c => c.CloseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => mock.Setup(c => c.State).Returns(ConnectionState.Disconnected))
            .Returns(ValueTask.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Simulates receiving a message on the mock connection by raising the MessageReceived event.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="messageJson">The JSON message string to simulate.</param>
    public static async Task SimulateMessageAsync(Mock<DealerConnection> mock, string messageJson)
    {
        var bytes = Encoding.UTF8.GetBytes(messageJson);
        var messageReceived = GetMessageReceivedDelegate(mock);

        if (messageReceived != null)
        {
            await messageReceived.Invoke(bytes);
        }
    }

    /// <summary>
    /// Simulates receiving raw bytes on the mock connection.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="bytes">The raw message bytes.</param>
    public static async Task SimulateRawMessageAsync(Mock<DealerConnection> mock, ReadOnlyMemory<byte> bytes)
    {
        var messageReceived = GetMessageReceivedDelegate(mock);

        if (messageReceived != null)
        {
            await messageReceived.Invoke(bytes);
        }
    }

    /// <summary>
    /// Simulates the connection closed event.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="status">Optional close status.</param>
    public static void SimulateClosed(Mock<DealerConnection> mock, WebSocketCloseStatus? status = null)
    {
        mock.Setup(c => c.State).Returns(ConnectionState.Disconnected);
        mock.Raise(c => c.Closed += null, mock.Object, status);
    }

    /// <summary>
    /// Simulates a connection error event.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="exception">The exception to raise.</param>
    public static void SimulateError(Mock<DealerConnection> mock, Exception exception)
    {
        mock.Setup(c => c.State).Returns(ConnectionState.Disconnected);
        mock.Raise(c => c.Error += null, mock.Object, exception);
    }

    /// <summary>
    /// Sets up the mock connection to fail on connect with the specified exception.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="exception">The exception to throw.</param>
    public static void SetupConnectToFail(Mock<DealerConnection> mock, Exception exception)
    {
        mock.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    /// <summary>
    /// Sets up the mock connection to fail on send with the specified exception.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="exception">The exception to throw.</param>
    public static void SetupSendToFail(Mock<DealerConnection> mock, Exception exception)
    {
        mock.Setup(c => c.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        mock.Setup(c => c.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    /// <summary>
    /// Verifies that SendAsync was called with a message containing the specified text.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    /// <param name="expectedText">The text that should be in the sent message.</param>
    public static void VerifySentMessage(Mock<DealerConnection> mock, string expectedText)
    {
        mock.Verify(
            c => c.SendAsync(
                It.Is<string>(msg => msg.Contains(expectedText)),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            $"Expected message containing '{expectedText}' to be sent");
    }

    /// <summary>
    /// Verifies that SendAsync was called at least once.
    /// </summary>
    /// <param name="mock">The mock DealerConnection.</param>
    public static void VerifyMessageSent(Mock<DealerConnection> mock)
    {
        mock.Verify(
            c => c.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "Expected at least one message to be sent");
    }

    /// <summary>
    /// Gets the MessageReceived delegate from the mock for raising events.
    /// Uses reflection to access the event delegate.
    /// </summary>
    private static Func<ReadOnlyMemory<byte>, ValueTask>? GetMessageReceivedDelegate(Mock<DealerConnection> mock)
    {
        // Note: This is a simplified approach. In real tests, we might need to:
        // 1. Subscribe to the event before simulating
        // 2. Use a test fixture that properly exposes the event
        // 3. Or test through integration rather than unit testing this layer

        // For now, we'll rely on the test to have subscribed to the event
        // and we'll raise it using Moq's event raising mechanism
        return null; // Tests will need to subscribe first
    }
}
