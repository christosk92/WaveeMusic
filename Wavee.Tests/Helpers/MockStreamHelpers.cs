using System;
using System.IO;
using System.Threading;
using Moq;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for mocking Stream operations in Connection tests.
/// </summary>
internal static class MockStreamHelpers
{
    /// <summary>
    /// Creates a mock stream that returns predefined data on read.
    /// </summary>
    public static Mock<Stream> CreateMockStreamWithResponse(byte[] responseData)
    {
        var mockStream = new Mock<Stream>();
        var position = 0;

        mockStream
            .Setup(s => s.ReadAsync(
                It.IsAny<Memory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Memory<byte> buffer, CancellationToken ct) =>
            {
                if (position >= responseData.Length)
                    return 0; // EOF

                int bytesToCopy = Math.Min(buffer.Length, responseData.Length - position);
                responseData.AsSpan(position, bytesToCopy).CopyTo(buffer.Span);
                position += bytesToCopy;
                return bytesToCopy;
            });

        mockStream
            .Setup(s => s.WriteAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        mockStream.Setup(s => s.CanRead).Returns(true);
        mockStream.Setup(s => s.CanWrite).Returns(true);

        return mockStream;
    }

    /// <summary>
    /// Creates a mock stream that simulates partial reads.
    /// </summary>
    public static Mock<Stream> CreateMockStreamWithPartialReads(
        byte[] responseData,
        int maxBytesPerRead)
    {
        var mockStream = new Mock<Stream>();
        var position = 0;

        mockStream
            .Setup(s => s.ReadAsync(
                It.IsAny<Memory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Memory<byte> buffer, CancellationToken ct) =>
            {
                if (position >= responseData.Length)
                    return 0;

                // Limit read to simulate partial reads
                int bytesToCopy = Math.Min(
                    Math.Min(buffer.Length, maxBytesPerRead),
                    responseData.Length - position);

                responseData.AsSpan(position, bytesToCopy).CopyTo(buffer.Span);
                position += bytesToCopy;
                return bytesToCopy;
            });

        mockStream.Setup(s => s.CanRead).Returns(true);

        return mockStream;
    }

    /// <summary>
    /// Creates a mock stream that throws on read (simulates network error).
    /// </summary>
    public static Mock<Stream> CreateMockStreamWithReadError()
    {
        var mockStream = new Mock<Stream>();

        mockStream
            .Setup(s => s.ReadAsync(
                It.IsAny<Memory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Network error"));

        mockStream.Setup(s => s.CanRead).Returns(true);

        return mockStream;
    }

    /// <summary>
    /// Creates a mock stream that returns EOF immediately.
    /// </summary>
    public static Mock<Stream> CreateMockStreamWithEOF()
    {
        var mockStream = new Mock<Stream>();

        mockStream
            .Setup(s => s.ReadAsync(
                It.IsAny<Memory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // EOF

        mockStream.Setup(s => s.CanRead).Returns(true);

        return mockStream;
    }
}
