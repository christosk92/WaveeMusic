using System;
using System.IO;
using System.IO.Compression;
using Google.Protobuf;

namespace Wavee.Backend.Spotify;

// Request/response compression for the metadata POSTs. RESPONSES are auto-decompressed by the shared SocketsHttpHandler
// (DecompressionMethods.All = gzip/deflate/brotli). HttpClient won't compress the REQUEST, so we gzip the body ourselves
// (+ Content-Encoding: gzip) so a large BatchedEntityRequest ships small. (zstd would need a manual decoder; gzip/br
// cover the common path and are what we send.)
public static class HttpCompression
{
    public static byte[] Gzip(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.Length / 2 + 16);
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    /// <summary>Serialize a protobuf message STRAIGHT into the gzip stream — no intermediate full uncompressed byte[]
    /// (which would land on the LOH for a large BatchedEntityRequest).</summary>
    public static byte[] GzipProto(IMessage message)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            message.WriteTo(gz);
        return ms.ToArray();
    }

    public static byte[] Gunzip(ReadOnlySpan<byte> data)
    {
        using var src = new MemoryStream(data.ToArray());
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    public static byte[] BrotliDecompress(ReadOnlySpan<byte> data)
    {
        using var src = new MemoryStream(data.ToArray());
        using var br = new BrotliStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        br.CopyTo(dst);
        return dst.ToArray();
    }
}
