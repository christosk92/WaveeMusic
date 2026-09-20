using System;
using System.IO;

namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// Xing/LAME gapless header probe for the MP3 decode edge (W2 gapless fix §3). LAME writes the encoder delay and end
/// padding into the first (info) frame; without applying them the decoder emits ~576–1105 samples of priming silence at
/// the start and up to a frame of pad at the end, which a butt-join then plays as audio. NLayer does not read the tag,
/// so this probe does — header bytes only, no audio decode. It runs ONLY on a seekable stream (local file / buffered
/// HTTP) and restores <see cref="Stream.Position"/>; a live forward-only stream skips the probe (GaplessInfo.None)
/// rather than buffer an unbounded ID3v2 block. All values are SOURCE-rate samples; the decoder converts to mix frames.
/// </summary>
internal static class Mp3GaplessProbe
{
    /// <summary>The MDCT/filterbank delay a conforming MP3 decoder adds on top of the encoder delay (the LAME/gapless
    /// convention: skip <c>delay + 529</c> at the start, trim <c>padding − 529</c> at the end).</summary>
    public const int DecoderDelaySamples = 529;

    /// <summary>Parsed source-rate gapless values. <see cref="TotalSamples"/> is the LAME-accounted total
    /// (<c>frames × spf − delay − padding</c>), or −1 when the Xing frame count was absent.</summary>
    public readonly record struct Result(int DelaySamples, int PaddingSamples, long TotalSamples);

    /// <summary>Probe <paramref name="stream"/> for a Xing/Info + LAME tag. Returns false (and leaves the position
    /// untouched beyond a restore) when the stream is not seekable, carries no tag, or the tag fails sanity checks.</summary>
    public static bool TryProbe(Stream stream, out Result result)
    {
        result = default;
        if (!stream.CanSeek) return false;
        long restore = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> head = stackalloc byte[10];
            if (ReadExactly(stream, head) < 10) return false;

            long frameStart = 0;
            if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
            {
                // ID3v2: syncsafe 28-bit size after the 10-byte header (footer flag adds another 10).
                int id3 = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14)
                        | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
                frameStart = 10 + id3 + ((head[5] & 0x10) != 0 ? 10 : 0);
            }

            // Read one window from the first frame; the Xing/Info + LAME block sits within the first ~300 bytes of it.
            stream.Position = frameStart;
            Span<byte> buf = stackalloc byte[512];
            int got = ReadExactly(stream, buf);
            if (got < 160) return false;
            return TryParseFirstFrame(buf[..got], out result);
        }
        catch (Exception) { return false; }   // a probe failure is never a playback failure — fall back to no trim
        finally
        {
            try { stream.Position = restore; } catch { /* restore is best-effort on a torn stream */ }
        }
    }

    // Parse the first MPEG audio frame header + its Xing/Info tag + the LAME extension out of `buf`.
    private static bool TryParseFirstFrame(ReadOnlySpan<byte> buf, out Result result)
    {
        result = default;
        // Frame sync: 11 set bits. The window starts AT the first frame (ID3 already skipped), so no long resync scan —
        // tolerate a few bytes of slack for writers that pad after ID3.
        int sync = -1;
        for (int i = 0; i + 4 <= Math.Min(buf.Length, 192); i++)
            if (buf[i] == 0xFF && (buf[i + 1] & 0xE0) == 0xE0) { sync = i; break; }
        if (sync < 0 || sync + 160 > buf.Length) return false;

        var h = buf[sync..];
        int versionBits = (h[1] >> 3) & 0x3;    // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
        int layerBits = (h[1] >> 1) & 0x3;      // 1 = Layer III
        if (layerBits != 1 || versionBits == 1) return false;
        bool mpeg1 = versionBits == 3;
        int channelMode = (h[3] >> 6) & 0x3;    // 3 = mono
        bool mono = channelMode == 3;
        int samplesPerFrame = mpeg1 ? 1152 : 576;

        // The Xing/Info tag sits after the side info: MPEG1 32/17 bytes (stereo/mono), MPEG2(.5) 17/9 — plus the 4-byte header.
        int xing = 4 + (mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17));
        if (xing + 8 > h.Length) return false;
        bool tagged = (h[xing] == (byte)'X' && h[xing + 1] == (byte)'i' && h[xing + 2] == (byte)'n' && h[xing + 3] == (byte)'g')
                   || (h[xing] == (byte)'I' && h[xing + 1] == (byte)'n' && h[xing + 2] == (byte)'f' && h[xing + 3] == (byte)'o');
        if (!tagged) return false;

        uint flags = ReadU32(h, xing + 4);
        int cursor = xing + 8;
        long frames = -1;
        if ((flags & 0x1) != 0) { frames = ReadU32(h, cursor); cursor += 4; }   // frame count
        if ((flags & 0x2) != 0) cursor += 4;                                    // byte count
        if ((flags & 0x4) != 0) cursor += 100;                                  // TOC
        if ((flags & 0x8) != 0) cursor += 4;                                    // VBR quality

        // The LAME extension: a 9-byte encoder string, then delay/padding packed 12+12 bits at offset 21.
        if (cursor + 24 > h.Length) return false;
        int delay = (h[cursor + 21] << 4) | (h[cursor + 22] >> 4);
        int padding = ((h[cursor + 22] & 0x0F) << 8) | h[cursor + 23];
        if (delay <= 0 && padding <= 0) return false;              // no gapless data written
        if (delay > 4096 || padding > 4608) return false;          // outside any real encoder's range → distrust the block

        long total = frames > 0 ? frames * samplesPerFrame - delay - padding : -1;
        result = new Result(delay, padding, total > 0 ? total : -1);
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int at)
        => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    private static int ReadExactly(Stream s, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = s.Read(dst[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
