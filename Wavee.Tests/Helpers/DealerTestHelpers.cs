using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Session;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for creating test dealer messages and configurations.
/// </summary>
internal static class DealerTestHelpers
{
    /// <summary>
    /// Creates a PING message JSON string.
    /// </summary>
    public static string CreatePingMessage()
    {
        return "{\"type\":\"ping\"}";
    }

    /// <summary>
    /// Creates a PONG message JSON string.
    /// </summary>
    public static string CreatePongMessage()
    {
        return "{\"type\":\"pong\"}";
    }

    /// <summary>
    /// Creates a MESSAGE JSON string with optional headers and base64-encoded payload.
    /// </summary>
    /// <param name="uri">The message URI (e.g., "hm://connect-state/v1/connect/volume").</param>
    /// <param name="headers">Optional headers dictionary.</param>
    /// <param name="payload">Optional payload bytes (will be base64-encoded).</param>
    /// <returns>JSON message string.</returns>
    public static string CreateDealerMessage(
        string uri,
        Dictionary<string, string>? headers = null,
        byte[]? payload = null)
    {
        headers ??= new Dictionary<string, string>();
        payload ??= Array.Empty<byte>();

        var payloadBase64 = Convert.ToBase64String(payload);

        var message = new
        {
            type = "message",
            uri = uri,
            headers = headers,
            payloads = new[] { payloadBase64 }
        };

        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// Creates a REQUEST JSON string with the specified parameters.
    /// </summary>
    /// <param name="messageId">The numeric message ID.</param>
    /// <param name="senderDeviceId">The sender device ID.</param>
    /// <param name="messageIdent">The message identifier (e.g., "hm://connect-state/v1/cluster").</param>
    /// <param name="payload">The payload object (will be JSON-serialized).</param>
    /// <returns>JSON request string.</returns>
    public static string CreateDealerRequest(
        int messageId,
        string senderDeviceId,
        string messageIdent,
        object payload)
    {
        var key = $"{messageId}/{senderDeviceId}";

        var request = new
        {
            type = "request",
            key = key,
            message_ident = messageIdent,
            payload = payload
        };

        return JsonSerializer.Serialize(request);
    }

    /// <summary>
    /// Creates a DealerClientConfig with short timeouts suitable for testing.
    /// </summary>
    /// <param name="logger">Optional logger for test diagnostics.</param>
    /// <returns>Configuration with fast timeouts.</returns>
    public static DealerClientConfig CreateTestConfig(ILogger? logger = null)
    {
        return new DealerClientConfig
        {
            Logger = logger,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            OperationTimeout = TimeSpan.FromSeconds(2),
            PingInterval = TimeSpan.FromSeconds(1),           // Fast heartbeat for testing
            PongTimeout = TimeSpan.FromMilliseconds(500),     // Quick timeout for testing
            EnableAutoReconnect = true,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),  // Fast reconnect for testing
            MaxReconnectDelay = TimeSpan.FromSeconds(5),
            MaxReconnectAttempts = 3
        };
    }

    /// <summary>
    /// Creates a mock Session with basic setup for testing.
    /// </summary>
    /// <returns>Mock Session object.</returns>
    public static Mock<Session> CreateMockSession()
    {
        var mockSession = new Mock<Session>();

        // Setup GetAccessTokenAsync to return a test token
        mockSession
            .Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken
            {
                Token = "test_access_token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });

        return mockSession;
    }

    /// <summary>
    /// Creates an HTTP client with a mock handler for ApResolver dealer endpoints.
    /// </summary>
    /// <param name="dealerEndpoints">List of dealer endpoints to return.</param>
    /// <returns>HttpClient configured with mock responses.</returns>
    public static HttpClient CreateMockHttpClientForDealers(params string[] dealerEndpoints)
    {
        var dealerList = dealerEndpoints.Length > 0
            ? dealerEndpoints
            : new[] { "dealer.spotify.com:443" };

        // Setup ApResolver response
        var apResolverResponse = new
        {
            dealer = dealerList
        };

        var jsonResponse = JsonSerializer.Serialize(apResolverResponse);
        var content = new StringContent(jsonResponse, Encoding.UTF8, "application/json");

        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(HttpStatusCode.OK, content);

        return new HttpClient(mockHandler.Object);
    }

    /// <summary>
    /// Creates a UTF-8 encoded byte array from a JSON string.
    /// Useful for simulating raw WebSocket messages.
    /// </summary>
    /// <param name="json">The JSON string.</param>
    /// <returns>UTF-8 encoded bytes.</returns>
    public static byte[] CreateMessageBytes(string json)
    {
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Creates a protobuf payload and returns it as a base64-encoded string array.
    /// </summary>
    /// <param name="protobufBytes">The protobuf message bytes.</param>
    /// <returns>Array containing single base64-encoded payload.</returns>
    public static string[] CreateProtobufPayload(byte[] protobufBytes)
    {
        return new[] { Convert.ToBase64String(protobufBytes) };
    }
}
