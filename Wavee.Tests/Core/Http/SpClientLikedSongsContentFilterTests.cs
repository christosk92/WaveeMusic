using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Wavee.Core.Http;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Http;

public class SpClientLikedSongsContentFilterTests
{
    [Fact]
    public async Task GetLikedSongsContentFiltersAsync_ShouldSendJsonConditionalGet_AndDeserializePayload()
    {
        HttpRequestMessage? capturedRequest = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"contentFilters":[{"title":"Pop","query":"tags contains pop"}]}
                """, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("ETag", "etag-123");

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(response);

        using var httpClient = new HttpClient(handler.Object);
        var session = new MockSession();
        var client = new SpClient(session, httpClient, "spclient.wg.spotify.com:443", null);

        var result = await client.GetLikedSongsContentFiltersAsync("etag-old");

        result.IsNotModified.Should().BeFalse();
        result.ETag.Should().Be("etag-123");
        result.Filters.Should().ContainSingle();
        result.Filters[0].Title.Should().Be("Pop");
        result.Filters[0].Query.Should().Be("tags contains pop");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Get);
        capturedRequest.RequestUri!.ToString().Should().Be("https://spclient.wg.spotify.com/content-filter/v1/liked-songs?subjective=true&market=from_token");
        capturedRequest.Headers.Accept.Should().ContainSingle(h => h.MediaType == "application/json");
        capturedRequest.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization.Parameter.Should().Be("test_access_token");
        capturedRequest.Headers.TryGetValues("If-None-Match", out var etagValues).Should().BeTrue();
        etagValues.Should().ContainSingle().Which.Should().Be("etag-old");
    }

    [Fact]
    public async Task GetLikedSongsContentFiltersAsync_ShouldReturnNotModified_On304()
    {
        var handler = MockHttpHelpers.CreateMockWithSequence(new HttpResponseMessage(HttpStatusCode.NotModified));
        using var httpClient = new HttpClient(handler.Object);
        var session = new MockSession();
        var client = new SpClient(session, httpClient, "spclient.wg.spotify.com:443", null);

        var result = await client.GetLikedSongsContentFiltersAsync("etag-old");

        result.IsNotModified.Should().BeTrue();
        result.ETag.Should().Be("etag-old");
        result.Filters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLikedSongsContentFiltersAsync_ShouldThrowSpClientException_OnUnauthorized()
    {
        var handler = MockHttpHelpers.CreateMockWithSequence(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler.Object);
        var session = new MockSession();
        var client = new SpClient(session, httpClient, "spclient.wg.spotify.com:443", null);

        var act = () => client.GetLikedSongsContentFiltersAsync();

        var exception = await Assert.ThrowsAsync<SpClientException>(act);
        exception.Reason.Should().Be(SpClientFailureReason.Unauthorized);
    }
}
