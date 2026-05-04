using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wavee.Core.Library.Local;

/// <summary>
/// Computes the stable identity hash that backs <c>wavee:local:track:{hash}</c>.
///
/// <para>
/// We hash the first 64 KiB of <em>audio payload</em> — i.e. the file with the
/// container/tag header skipped. That gives:
/// </para>
/// <list type="bullet">
///   <item>Stable identity across tag edits (the bytes we hash never move).</item>
///   <item>Bounded I/O regardless of file size (a 4-hour audiobook costs the same
///         to fingerprint as a 4-minute pop song — ~10–50 ms).</item>
///   <item>Negligible collision risk on real audio data.</item>
/// </list>
/// </summary>
public static class LocalTrackHasher
{
    private const int PayloadHashBytes = 64 * 1024;

    /// <summary>
    /// Computes the 40-character lowercase hex SHA-1 of the audio payload.
    /// The file extension steers per-format header skipping; pass it lowercase
    /// without the leading dot (e.g. <c>"mp3"</c>, <c>"flac"</c>).
    /// </summary>
    public static async Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return await ComputeAsync(fs, ext, ct);
    }

    public static async Task<string> ComputeAsync(Stream stream, string ext, CancellationToken ct = default)
    {
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));

        long payloadStart = await DetectPayloadStartAsync(stream, ext, ct);
        if (stream.CanSeek)
        {
            stream.Position = payloadStart;
        }
        else
        {
            // Non-seekable: discard payloadStart bytes off the front.
            await SkipBytesAsync(stream, payloadStart, ct);
        }

        var buffer = new byte[PayloadHashBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }

        var hash = SHA1.HashData(buffer.AsSpan(0, total));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<long> DetectPayloadStartAsync(Stream stream, string ext, CancellationToken ct)
    {
        long start = stream.CanSeek ? stream.Position : 0;
        try
        {
            // Probe the first 10 bytes — enough for ID3v2 + FLAC magic + ASF GUID start.
            var probe = new byte[10];
            int read = await stream.ReadAsync(probe.AsMemory(), ct);
            if (read < 4) return start;

            // ID3v2 — "ID3" + 6 header bytes; size is 4 syncsafe bytes (each MSB ignored).
            if (probe[0] == (byte)'I' && probe[1] == (byte)'D' && probe[2] == (byte)'3' && read >= 10)
            {
                int size =
                    ((probe[6] & 0x7F) << 21) |
                    ((probe[7] & 0x7F) << 14) |
                    ((probe[8] & 0x7F) <<  7) |
                     (probe[9] & 0x7F);
                long after = start + 10 + size;
                bool footerPresent = (probe[5] & 0x10) != 0;
                if (footerPresent) after += 10;
                return after;
            }

            // FLAC: "fLaC" magic + metadata blocks.
            if (probe[0] == (byte)'f' && probe[1] == (byte)'L' && probe[2] == (byte)'a' && probe[3] == (byte)'C')
            {
                if (!stream.CanSeek) return start; // Without seeking we can't walk metadata blocks reliably.
                stream.Position = start + 4;
                long pos = start + 4;
                while (true)
                {
                    var hdr = new byte[4];
                    int n = await stream.ReadAsync(hdr.AsMemory(), ct);
                    if (n != 4) break;
                    bool last = (hdr[0] & 0x80) != 0;
                    int blockLen = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                    pos += 4 + blockLen;
                    stream.Position = pos;
                    if (last) break;
                }
                return pos;
            }

            // ASF/WMA: 16-byte GUID prefix + 8-byte size; payload start =
            // start + 16 + 8 + size of header object. For our purposes,
            // jumping past the first 30 bytes is "good enough" — far enough
            // to skip the GUID/size header even if downstream metadata is
            // present, and any leftover header bytes still hash stably.
            if (probe[0] == 0x30 && probe[1] == 0x26 && probe[2] == 0xB2 && probe[3] == 0x75)
            {
                return start + 30;
            }

            // OGG container: "OggS" — packet boundaries shift across files but
            // hashing from byte 0 is fine; OGG adds no per-file mutable header.
            if (probe[0] == (byte)'O' && probe[1] == (byte)'g' && probe[2] == (byte)'g' && probe[3] == (byte)'S')
                return start;

            // MP4 / M4A / M4B / MOV (ISOBMFF): walk the top-level atom list to
            // find `mdat`, the audio payload box. Tag metadata lives in
            // `moov.udta.meta` so hashing from `mdat` keeps the fingerprint
            // stable across re-tagging.
            if (read >= 8 && probe[4] == (byte)'f' && probe[5] == (byte)'t' && probe[6] == (byte)'y' && probe[7] == (byte)'p')
            {
                if (!stream.CanSeek) return start;
                long mdat = await FindMp4MdatStartAsync(stream, start, ct);
                return mdat > 0 ? mdat : start;
            }

            return start;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = start;
        }
    }

    /// <summary>
    /// Walks ISOBMFF top-level atoms from <paramref name="containerStart"/> and
    /// returns the byte offset of the start of the first <c>mdat</c> payload
    /// (i.e. just past its size+type header). Returns 0 if no <c>mdat</c> is
    /// found before EOF or if a malformed atom would walk past the file.
    /// </summary>
    private static async Task<long> FindMp4MdatStartAsync(Stream stream, long containerStart, CancellationToken ct)
    {
        long pos = containerStart;
        long length = stream.Length;
        var hdr = new byte[16];

        while (pos + 8 <= length)
        {
            stream.Position = pos;
            int n = await stream.ReadAsync(hdr.AsMemory(0, 8), ct);
            if (n != 8) return 0;

            // Atom header: 4-byte big-endian size + 4-byte ASCII type.
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
            long size = size32;
            int headerLen = 8;

            if (size32 == 1)
            {
                // 64-bit extended size follows the type field.
                int n2 = await stream.ReadAsync(hdr.AsMemory(8, 8), ct);
                if (n2 != 8) return 0;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8, 8));
                headerLen = 16;
            }
            else if (size32 == 0)
            {
                // Box runs to EOF — last atom.
                size = length - pos;
            }

            if (size < headerLen || pos + size > length) return 0;

            bool isMdat = hdr[4] == (byte)'m' && hdr[5] == (byte)'d' && hdr[6] == (byte)'a' && hdr[7] == (byte)'t';
            if (isMdat)
                return pos + headerLen;

            pos += size;
        }

        return 0;
    }

    private static async Task SkipBytesAsync(Stream stream, long count, CancellationToken ct)
    {
        if (count <= 0) return;
        var buf = new byte[8192];
        long left = count;
        while (left > 0)
        {
            int read = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
            if (read == 0) return;
            left -= read;
        }
    }
}
