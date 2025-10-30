using System.Net;
using System.Net.Http;
using Google.Protobuf;
using Moq;
using Moq.Protected;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for mocking HttpClient and HttpMessageHandler in tests.
/// </summary>
internal static class MockHttpHelpers
{
    /// <summary>
    /// Creates a mock HttpMessageHandler that returns a specific status code and content.
    /// </summary>
    public static Mock<HttpMessageHandler> CreateMockHttpMessageHandler(
        HttpStatusCode statusCode,
        HttpContent? content = null)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = content ?? new ByteArrayContent(Array.Empty<byte>())
            });

        return mockHandler;
    }

    /// <summary>
    /// Creates a mock HttpMessageHandler that returns a protobuf message.
    /// </summary>
    public static Mock<HttpMessageHandler> CreateMockWithProtobufResponse<T>(T message) where T : IMessage
    {
        var bytes = message.ToByteArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        return CreateMockHttpMessageHandler(HttpStatusCode.OK, content);
    }

    /// <summary>
    /// Creates a mock HttpMessageHandler that returns different responses in sequence.
    /// </summary>
    public static Mock<HttpMessageHandler> CreateMockWithSequence(params HttpResponseMessage[] responses)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        var setup = mockHandler
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var response in responses)
        {
            setup = setup.ReturnsAsync(response);
        }

        return mockHandler;
    }

    /// <summary>
    /// Creates a mock HttpMessageHandler that throws an exception.
    /// </summary>
    public static Mock<HttpMessageHandler> CreateMockWithException(Exception exception)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);

        return mockHandler;
    }

    /// <summary>
    /// Creates an HttpResponseMessage with protobuf content.
    /// </summary>
    public static HttpResponseMessage CreateProtobufResponse<T>(T message, HttpStatusCode statusCode = HttpStatusCode.OK)
        where T : IMessage
    {
        var bytes = message.ToByteArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        return new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = content
        };
    }
}
