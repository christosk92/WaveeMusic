using System.Net;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Wavee.Core.Audio;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Audio;

/// <summary>
/// Tests for HeadFileClient.
/// Validates head file fetching for instant playback start.
/// </summary>
public class HeadFileClientTests
{
    private const string TestFileIdHex = "0123456789abcdef0123456789abcdef01234567";
    private static readonly FileId TestFileId = FileId.FromBase16(TestFileIdHex);

    [Fact]
    public async Task FetchHeadAsync_ValidFileId_ReturnsBytes()
    {
        // ============================================================
        // WHY: Valid file ID should return head file data.
        // ============================================================

        // Arrange
        var expectedData = new byte[] { 0x4F, 0x67, 0x67, 0x53 }; // OggS magic
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(
            HttpStatusCode.OK,
            new ByteArrayContent(expectedData));

        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        var result = await client.FetchHeadAsync(TestFileId);

        // Assert
        result.Should().BeEquivalentTo(expectedData);

        // Verify correct URL was called
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.ToString().Contains(TestFileIdHex)),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task FetchHeadAsync_InvalidFileId_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Invalid (empty) file ID should be rejected.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        Func<Task> act = () => client.FetchHeadAsync(FileId.Empty);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not valid*");
    }

    [Fact]
    public async Task FetchHeadAsync_NotFound_ThrowsHeadFileException()
    {
        // ============================================================
        // WHY: 404 response should throw HeadFileException with HttpError reason.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(HttpStatusCode.NotFound);
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        Func<Task> act = () => client.FetchHeadAsync(TestFileId);

        // Assert
        var exception = await act.Should().ThrowAsync<HeadFileException>();
        exception.Which.Reason.Should().Be(HeadFileFailureReason.HttpError);
        exception.Which.FileId.Should().Be(TestFileId);
    }

    [Fact]
    public async Task FetchHeadAsync_ServerError_ThrowsHeadFileException()
    {
        // ============================================================
        // WHY: 500 response should throw HeadFileException.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(
            HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        Func<Task> act = () => client.FetchHeadAsync(TestFileId);

        // Assert
        var exception = await act.Should().ThrowAsync<HeadFileException>();
        exception.Which.Reason.Should().Be(HeadFileFailureReason.HttpError);
    }

    [Fact]
    public async Task FetchHeadAsync_NetworkError_ThrowsHeadFileException()
    {
        // ============================================================
        // WHY: Network errors should throw HeadFileException with NetworkError reason.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockWithException(
            new HttpRequestException("Network error"));
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        Func<Task> act = () => client.FetchHeadAsync(TestFileId);

        // Assert
        var exception = await act.Should().ThrowAsync<HeadFileException>();
        exception.Which.Reason.Should().Be(HeadFileFailureReason.NetworkError);
        exception.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task FetchHeadAsync_Cancellation_ThrowsTaskCanceledException()
    {
        // ============================================================
        // WHY: User-initiated cancellation should propagate.
        // ============================================================

        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                await Task.Delay(10000, ct); // Long delay
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        Func<Task> act = () => client.FetchHeadAsync(TestFileId, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryFetchHeadAsync_ValidFileId_ReturnsData()
    {
        // ============================================================
        // WHY: TryFetchHeadAsync should return data on success.
        // ============================================================

        // Arrange
        var expectedData = new byte[] { 0x4F, 0x67, 0x67, 0x53 };
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(
            HttpStatusCode.OK,
            new ByteArrayContent(expectedData));

        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        var result = await client.TryFetchHeadAsync(TestFileId);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
    }

    [Fact]
    public async Task TryFetchHeadAsync_NotFound_ReturnsNull()
    {
        // ============================================================
        // WHY: TryFetchHeadAsync should return null on failure, not throw.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockHttpMessageHandler(HttpStatusCode.NotFound);
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        var result = await client.TryFetchHeadAsync(TestFileId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryFetchHeadAsync_NetworkError_ReturnsNull()
    {
        // ============================================================
        // WHY: TryFetchHeadAsync should return null on network error, not throw.
        // ============================================================

        // Arrange
        var mockHandler = MockHttpHelpers.CreateMockWithException(
            new HttpRequestException("Network error"));
        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        var result = await client.TryFetchHeadAsync(TestFileId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryFetchHeadAsync_Cancellation_ReturnsNull()
    {
        // ============================================================
        // WHY: TryFetchHeadAsync should return null on cancellation, not throw.
        // ============================================================

        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        var result = await client.TryFetchHeadAsync(TestFileId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        // ============================================================
        // WHY: Null HttpClient must be rejected.
        // ============================================================

        // Act
        Action act = () => new HeadFileClient(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task FetchHeadAsync_CorrectUrlFormat()
    {
        // ============================================================
        // WHY: URL should be formatted correctly with lowercase hex file ID.
        // ============================================================

        // Arrange
        string? capturedUrl = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                capturedUrl = req.RequestUri?.ToString();
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(new byte[] { 0x01 })
            });

        var httpClient = new HttpClient(mockHandler.Object);
        var client = new HeadFileClient(httpClient);

        // Act
        await client.FetchHeadAsync(TestFileId);

        // Assert
        capturedUrl.Should().NotBeNull();
        capturedUrl.Should().StartWith("https://heads-fa.spotify.com/head/");
        capturedUrl.Should().Contain(TestFileIdHex.ToLowerInvariant());
    }
}

/// <summary>
/// Tests for HeadFileException.
/// </summary>
public class HeadFileExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        // ============================================================
        // WHY: Exception should store reason and file ID.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16("0123456789abcdef0123456789abcdef01234567");
        var reason = HeadFileFailureReason.HttpError;
        var message = "Test error";

        // Act
        var exception = new HeadFileException(reason, message, fileId);

        // Assert
        exception.Reason.Should().Be(reason);
        exception.FileId.Should().Be(fileId);
        exception.Message.Should().Be(message);
    }

    [Fact]
    public void Constructor_WithInnerException_SetsProperties()
    {
        // ============================================================
        // WHY: Exception with inner exception should preserve it.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16("0123456789abcdef0123456789abcdef01234567");
        var reason = HeadFileFailureReason.NetworkError;
        var message = "Network failed";
        var innerException = new HttpRequestException("Connection refused");

        // Act
        var exception = new HeadFileException(reason, message, fileId, innerException);

        // Assert
        exception.Reason.Should().Be(reason);
        exception.FileId.Should().Be(fileId);
        exception.Message.Should().Be(message);
        exception.InnerException.Should().Be(innerException);
    }
}
