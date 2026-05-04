using System.Net.Http;
using FluentAssertions;
using Wavee.Core.Http;
using ZstdSharp;
using Xunit;

namespace Wavee.Tests.Core.Http;

public class ExtendedMetadataClientEncodingTests
{
    [Fact]
    public async Task ReadResponseBytesAsync_ShouldDecodeZstdPayload()
    {
        var expected = new byte[] { 0x08, 0x96, 0x01, 0x12, 0x03, 0x66, 0x6f, 0x6f };
        var compressed = new Compressor().Wrap(expected).ToArray();

        using var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressed)
        };
        response.Content.Headers.ContentEncoding.Add("zstd");

        var decoded = await ExtendedMetadataClient.ReadResponseBytesAsync(response);

        decoded.Should().Equal(expected);
    }

    [Fact]
    public async Task ReadResponseBytesAsync_ShouldReturnRawBytes_WhenUnencoded()
    {
        var expected = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        using var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(expected)
        };

        var decoded = await ExtendedMetadataClient.ReadResponseBytesAsync(response);

        decoded.Should().Equal(expected);
    }
}
