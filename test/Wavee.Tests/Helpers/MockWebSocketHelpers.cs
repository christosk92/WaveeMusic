using System.Net.WebSockets;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for WebSocket-related testing.
/// Note: ClientWebSocket is a sealed class and difficult to mock directly.
/// For DealerConnection tests, consider using integration tests with a real WebSocket server
/// or testing through the DealerClient layer with mocked DealerConnection.
/// </summary>
internal static class MockWebSocketHelpers
{
    /// <summary>
    /// Creates a CancellationToken that will cancel after the specified timeout.
    /// Useful for preventing tests from hanging indefinitely on WebSocket operations.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <returns>CancellationToken that will cancel after timeout.</returns>
    public static CancellationToken CreateTimeoutToken(int timeoutMs = 5000)
    {
        var cts = new CancellationTokenSource(timeoutMs);
        return cts.Token;
    }

    /// <summary>
    /// Creates test WebSocket options with common settings for testing.
    /// </summary>
    /// <returns>ClientWebSocketOptions configured for testing.</returns>
    public static ClientWebSocketOptions CreateTestWebSocketOptions()
    {
        var ws = new ClientWebSocket();
        var options = ws.Options;

        // Add any common test configuration
        options.KeepAliveInterval = TimeSpan.FromSeconds(1);

        return options;
    }

    /// <summary>
    /// Simulates WebSocket message bytes.
    /// </summary>
    /// <param name="message">The message string to convert.</param>
    /// <returns>UTF-8 encoded bytes.</returns>
    public static byte[] CreateWebSocketMessageBytes(string message)
    {
        return System.Text.Encoding.UTF8.GetBytes(message);
    }

    /// <summary>
    /// Creates a WebSocket close status for testing.
    /// </summary>
    /// <param name="status">The close status.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>Tuple of status and description.</returns>
    public static (WebSocketCloseStatus status, string description) CreateCloseStatus(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? description = null)
    {
        return (status, description ?? "Test close");
    }

    /// <summary>
    /// Documentation helper: Explains WebSocket mocking strategies.
    /// </summary>
    /// <remarks>
    /// WebSocket Testing Strategies:
    ///
    /// 1. **Integration Testing Approach (Recommended)**:
    ///    - Create a local WebSocket server for tests
    ///    - Test DealerConnection with real WebSocket communication
    ///    - Pros: Tests real behavior, Cons: Slower, more setup
    ///
    /// 2. **Mock at Higher Layer**:
    ///    - Mock DealerConnection when testing DealerClient
    ///    - Test DealerConnection separately with integration tests
    ///    - Pros: Fast unit tests, Cons: Less coverage of WebSocket layer
    ///
    /// 3. **Abstraction Layer**:
    ///    - Create IWebSocket interface
    ///    - Implement adapter for ClientWebSocket
    ///    - Mock the interface in tests
    ///    - Pros: Fully testable, Cons: Extra abstraction overhead
    ///
    /// For Wavee's Dealer tests, we use strategy #2:
    /// - DealerClient tests mock DealerConnection
    /// - DealerConnection uses integration tests (future work)
    /// </remarks>
    public static void DocumentWebSocketTestingStrategy() { }
}
