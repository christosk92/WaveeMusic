using System.Net;
using Moq;
using Moq.Protected;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Protocol.Player;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for creating testable SpClient instances.
/// </summary>
internal static class MockSpClientHelpers
{
    /// <summary>
    /// Tracks calls made to PutConnectStateAsync for test verification.
    /// </summary>
    public class PutStateCallTracker
    {
        public List<PutStateCall> Calls { get; } = new();

        public class PutStateCall
        {
            public required string DeviceId { get; init; }
            public required string ConnectionId { get; init; }
            public required PutStateRequest Request { get; init; }
            public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Creates a testable SpClient that succeeds all PUT state requests.
    /// </summary>
    /// <param name="tracker">Optional tracker to record PUT state calls.</param>
    /// <returns>Configured SpClient instance.</returns>
    public static SpClient CreateMockSpClient(PutStateCallTracker? tracker = null)
    {
        return CreateMockSpClient(HttpStatusCode.OK, tracker);
    }

    /// <summary>
    /// Creates a testable SpClient that returns a specific status code.
    /// </summary>
    /// <param name="statusCode">HTTP status code to return.</param>
    /// <param name="tracker">Optional tracker to record PUT state calls.</param>
    /// <returns>Configured SpClient instance.</returns>
    public static SpClient CreateMockSpClient(HttpStatusCode statusCode, PutStateCallTracker? tracker = null)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                // Track the call if tracker provided
                if (tracker != null && request.RequestUri?.PathAndQuery.Contains("/connect-state/") == true)
                {
                    // Extract device ID from URL path
                    var deviceId = ExtractDeviceIdFromUrl(request.RequestUri);

                    // Extract connection ID from headers
                    var connectionId = request.Headers.GetValues("X-Spotify-Connection-Id").FirstOrDefault() ?? "";

                    // Parse protobuf request body
                    var requestBody = request.Content?.ReadAsByteArrayAsync(ct).Result ?? Array.Empty<byte>();
                    var putStateRequest = PutStateRequest.Parser.ParseFrom(requestBody);

                    tracker.Calls.Add(new PutStateCallTracker.PutStateCall
                    {
                        DeviceId = deviceId,
                        ConnectionId = connectionId,
                        Request = putStateRequest
                    });
                }

                return new HttpResponseMessage
                {
                    StatusCode = statusCode,
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var session = DealerTestHelpers.CreateMockSession();

        return new SpClient(session, httpClient, "test.spotify.com", null);
    }

    /// <summary>
    /// Creates a testable SpClient that throws an exception.
    /// </summary>
    /// <param name="exception">Exception to throw.</param>
    /// <returns>Configured SpClient instance.</returns>
    public static SpClient CreateFailingSpClient(Exception exception)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);

        var httpClient = new HttpClient(mockHandler.Object);
        var session = DealerTestHelpers.CreateMockSession();

        return new SpClient(session, httpClient, "test.spotify.com", null);
    }

    private static string ExtractDeviceIdFromUrl(Uri uri)
    {
        // URL format: /connect-state/v1/devices/{deviceId}
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 ? segments[3] : "";
    }
}
