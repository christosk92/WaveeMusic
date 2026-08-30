using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wavee.Backend.Lyrics.Lyricify;

/// <summary>QQ Music QRC + Kugou KRC lyric decrypters, adapted from Lyricify.Lyrics.Helper (Apache-2.0). KRC = base64 →
/// drop the 4-byte "krc1" header → XOR with a fixed key → zlib inflate → drop the leading char. QRC = hex → custom 3DES-ECB
/// (the vendored <see cref="DESHelper"/>) → zlib inflate → strip a leading UTF-8 BOM. SharpZipLib's InflaterInputStream is
/// replaced by .NET <see cref="ZLibStream"/> (same zlib framing) so no extra package is needed.</summary>
public static class LyricCrypto
{
    static readonly byte[] KrcKey = { 0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69 };
    static readonly byte[] QrcKey = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");   // 24 bytes → 3 DES keys

    public static string? DecryptKrc(string base64)
    {
        try
        {
            var data = Convert.FromBase64String(base64.Trim());
            if (data.Length <= 4) return null;
            data = data[4..];
            for (int i = 0; i < data.Length; i++) data[i] ^= KrcKey[i % KrcKey.Length];
            string s = Encoding.UTF8.GetString(InflateBytes(data));
            return s.Length > 0 ? s[1..] : s;
        }
        catch { return null; }
    }

    public static string? DecryptQrc(string hex)
    {
        try
        {
            var enc = FromHex(hex.Trim());
            if (enc.Length == 0 || enc.Length % 8 != 0) return null;

            var schedule = new byte[3][][];
            for (int i = 0; i < 3; i++) { schedule[i] = new byte[16][]; for (int j = 0; j < 16; j++) schedule[i][j] = new byte[6]; }
            DESHelper.TripleDESKeySetup(QrcKey, schedule, DESHelper.DECRYPT);

            var data = new byte[enc.Length];
            var tmp = new byte[8];
            for (int i = 0; i < enc.Length; i += 8)
            {
                DESHelper.TripleDESCrypt(enc[i..], tmp, schedule);
                Array.Copy(tmp, 0, data, i, 8);
            }
            var bytes = InflateBytes(data);
            var bom = Encoding.UTF8.GetPreamble();
            int skip = bytes.Length >= bom.Length && bytes.AsSpan(0, bom.Length).SequenceEqual(bom) ? bom.Length : 0;
            return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
        }
        catch { return null; }
    }

    static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0) hex = hex[..^1];
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < hex.Length; i += 2) bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        return bytes;
    }

    static byte[] InflateBytes(byte[] data)
    {
        using var src = new MemoryStream(data);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var outS = new MemoryStream();
        z.CopyTo(outS);
        return outS.ToArray();
    }
}
