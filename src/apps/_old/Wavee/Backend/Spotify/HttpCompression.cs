using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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

    /// <summary>A read-only stream OVER the compressed bytes, copying only when the memory is not array-backed (which
    /// no caller here is). The decompress pair used to take a span, which forced <c>new MemoryStream(data.ToArray())</c>
    /// — a full extra copy of every compressed response, on the large-object heap for anything past 85 KB, purely to
    /// satisfy a constructor. The write side of this file already documents exactly this hazard; the read side simply
    /// never got the same treatment.</summary>
    static MemoryStream ReadOnlyOver(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> seg) && seg.Array is { } backing)
            return new MemoryStream(backing, seg.Offset, seg.Count, writable: false);
        byte[] copy = data.ToArray();
        return new MemoryStream(copy, 0, copy.Length, writable: false);
    }

    public static byte[] Gunzip(ReadOnlyMemory<byte> data)
    {
        using var src = ReadOnlyOver(data);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gz.CopyTo(dst);
        return dst.ToArray();
    }

    public static byte[] BrotliDecompress(ReadOnlyMemory<byte> data)
    {
        using var src = ReadOnlyOver(data);
        using var br = new BrotliStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        br.CopyTo(dst);
        return dst.ToArray();
    }
}
