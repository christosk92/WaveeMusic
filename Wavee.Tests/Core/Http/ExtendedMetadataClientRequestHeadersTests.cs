using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.ClientToken;
using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests.Core.Http;

public class ExtendedMetadataClientRequestHeadersTests
{
    [Fact]
    public async Task GetBatchedExtensionsAsync_ShouldSendDesktopSpclientHeaders()
    {
        CapturedRequestSnapshot? capturedMetadataRequest = null;

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var uri = request.RequestUri?.ToString();

                if (uri == "https://clienttoken.spotify.com/v1/clienttoken")
                {
                    return CreateClientTokenResponse();
                }

                if (uri == "https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata")
                {
                    capturedMetadataRequest = CapturedRequestSnapshot.From(request);
                    return CreateExtendedMetadataResponse();
                }

                throw new InvalidOperationException($"Unexpected request URI: {uri}");
            });

        using var httpClient = new HttpClient(handler.Object);
        var database = new Mock<IMetadataDatabase>(MockBehavior.Strict);
        database
            .Setup(x => x.GetExtensionAsync(
                "spotify:track:test-track-id",
                ExtensionKind.TrackV4,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((byte[] Data, string? Etag)?)null);
        database
            .Setup(x => x.GetExtensionEtagAsync(
                "spotify:track:test-track-id",
                ExtensionKind.TrackV4,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var client = new ExtendedMetadataClient(
            CreateSession().Object,
            httpClient,
            database.Object);

        var response = await client.GetBatchedExtensionsAsync(
            [("spotify:track:test-track-id", new[] { ExtensionKind.TrackV4 })]);

        response.ExtendedMetadata.Should().BeEmpty();

        capturedMetadataRequest.Should().NotBeNull();
        capturedMetadataRequest!.Method.Should().Be(HttpMethod.Post);
        capturedMetadataRequest.Version.Should().Be(HttpVersion.Version11);
        capturedMetadataRequest.RequestUri.Should().Be(new Uri("https://gew4-spclient.spotify.com/extended-metadata/v0/extended-metadata"));

        capturedMetadataRequest.AuthorizationScheme.Should().Be("Bearer");
        capturedMetadataRequest.AuthorizationParameter.Should().Be("test_access_token");

        capturedMetadataRequest.AcceptMediaTypes.Should().ContainSingle(x => x == "application/protobuf");
        capturedMetadataRequest.AcceptEncodings
            .Should().Contain(["gzip", "deflate", "br", "zstd"]);
        capturedMetadataRequest.ConnectionValues.Should().Contain("keep-alive");
        capturedMetadataRequest.GetSingleHeaderValue("User-Agent")
            .Should().Be("Spotify/128600502 Win32_x86_64/Windows 10 (10.0.26200; x64; AppX)");

        capturedMetadataRequest.GetSingleHeaderValue("Accept-Language").Should().Be("en");
        capturedMetadataRequest.GetSingleHeaderValue("App-Platform").Should().Be("Win32_x86_64");
        capturedMetadataRequest.GetSingleHeaderValue("Spotify-App-Version").Should().Be("128600502");
        capturedMetadataRequest.GetSingleHeaderValue("client-feature-id").Should().Be("collection");
        capturedMetadataRequest.GetSingleHeaderValue("client-token").Should().Be("test_client_token");
        capturedMetadataRequest.GetSingleHeaderValue("Origin").Should().Be("https://gew4-spclient.spotify.com");
        capturedMetadataRequest.GetSingleHeaderValue("Sec-Fetch-Site").Should().Be("same-origin");
        capturedMetadataRequest.GetSingleHeaderValue("Sec-Fetch-Mode").Should().Be("no-cors");
        capturedMetadataRequest.GetSingleHeaderValue("Sec-Fetch-Dest").Should().Be("empty");
        capturedMetadataRequest.ContentType.Should().Be("application/protobuf");
    }

    private static Mock<ISession> CreateSession()
    {
        var accessToken = new AccessToken
        {
            Token = "test_access_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        var spClient = new Mock<ISpClient>(MockBehavior.Strict);
        spClient.SetupGet(x => x.BaseUrl).Returns("https://gew4-spclient.spotify.com");

        var session = new Mock<ISession>(MockBehavior.Strict);
        session.SetupGet(x => x.Config).Returns(new SessionConfig
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device"
        });
        session.SetupGet(x => x.SpClient).Returns(spClient.Object);
        session.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(accessToken);
        session.Setup(x => x.GetCountryCodeAsync(It.IsAny<CancellationToken>())).ReturnsAsync("US");
        session.Setup(x => x.GetAccountTypeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AccountType.Premium);
        session.Setup(x => x.GetPreferredLocale()).Returns((string?)null);
        session.Setup(x => x.GetUserData()).Returns((UserData?)null);

        return session;
    }

    private static HttpResponseMessage CreateClientTokenResponse()
    {
        var body = new ClientTokenResponse
        {
            ResponseType = ClientTokenResponseType.ResponseGrantedTokenResponse,
            GrantedToken = new GrantedTokenResponse
            {
                Token = "test_client_token",
                RefreshAfterSeconds = 3600
            }
        };

        return CreateProtobufResponse(body, "application/x-protobuf");
    }

    private static HttpResponseMessage CreateExtendedMetadataResponse()
    {
        return CreateProtobufResponse(new BatchedExtensionResponse(), "application/protobuf");
    }

    private static HttpResponseMessage CreateProtobufResponse(IMessage message, string contentType)
    {
        var content = new ByteArrayContent(message.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private sealed record CapturedRequestSnapshot(
        HttpMethod Method,
        Version Version,
        Uri? RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        IReadOnlyList<string> AcceptMediaTypes,
        IReadOnlyList<string> AcceptEncodings,
        IReadOnlyList<string> ConnectionValues,
        Dictionary<string, string[]> HeaderValues,
        string? ContentType)
    {
        public static CapturedRequestSnapshot From(HttpRequestMessage request)
        {
            var headerValues = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headerValues[header.Key] = header.Value.ToArray();
                }
            }

            return new CapturedRequestSnapshot(
                request.Method,
                request.Version,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.Accept.Select(x => x.MediaType ?? string.Empty).ToArray(),
                request.Headers.AcceptEncoding.Select(x => x.Value ?? string.Empty).ToArray(),
                request.Headers.Connection.ToArray(),
                headerValues,
                request.Content?.Headers.ContentType?.MediaType);
        }

        public string GetSingleHeaderValue(string headerName)
        {
            HeaderValues.TryGetValue(headerName, out var values).Should().BeTrue();
            return values!.Should().ContainSingle().Subject;
        }
    }
}
