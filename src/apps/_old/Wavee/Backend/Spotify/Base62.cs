using System.Numerics;

namespace Wavee.Backend.Spotify;

// ── LIFTED protocol mechanic — Spotify gid (16 raw bytes) ↔ base62 id (22 chars) ─────────────────────────────────────
// librespot alphabet "0-9a-zA-Z". Pure + reversible; pinned by a round-trip + known-vector unit test.
public static class Base62
{
    const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Encode(ReadOnlySpan<byte> gid, int width = 22)
    {
        // A gid is ≤16 bytes (128 bits), big-endian → a UInt128. No BigInteger, no byte[] copy, no array reverse — only the
        // 22-char result string allocates (and that's the data). This runs per artist/album/track gid at 10k+ scale.
        UInt128 value = 0;
        foreach (var b in gid) value = (value << 8) | b;
        Span<char> buf = stackalloc char[width];
        for (int i = width - 1; i >= 0; i--)
        {
            buf[i] = Alphabet[(int)(value % 62)];
            value /= 62;
        }
        return new string(buf);
    }

    public static byte[] Decode(string id, int byteLen = 16)
    {
        BigInteger value = BigInteger.Zero;
        foreach (char c in id)
        {
            int d = Alphabet.IndexOf(c);
            if (d < 0) throw new FormatException($"invalid base62 char '{c}'");
            value = value * 62 + d;
        }
        var le = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outBytes = new byte[byteLen];
        for (int i = 0; i < byteLen && i < le.Length; i++) outBytes[byteLen - 1 - i] = le[i];
        return outBytes;
    }
}
