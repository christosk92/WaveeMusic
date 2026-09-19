using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wavee;

// ── QR diagnostic (--qr-dump) ────────────────────────────────────────────────────────────────────────────────────────
// Encodes a string with the REAL Qr encoder (the same one QrGrid uses) and emits it two ways: an ASCII structural dump to
// stdout (eyeball the finders/timing), and a crisp grayscale PNG (14px/module, 4-module quiet zone) you can scan from the
// file. This ISOLATES the encoder from the GUI renderer — if the PNG scans but the in-app QR doesn't, it's a rendering
// issue (resolution/anti-aliasing), not the bitstream. Behind a CLI flag; no NuGet (PNG via the BCL ZLibStream + a tiny CRC32).
static class QrDump
{
    public static int Run(string text, string path, WaveeLogger log)
    {
        bool[,] m;
        try { m = Qr.Encode(text, Qr.Ecc.M); }
        catch (Exception ex) { log.Info("QR encode failed: " + ex.Message); return 1; }
        int n = m.GetLength(0);

        // ASCII structure (dark = two full blocks → roughly square in a terminal), 2-module margin.
        var sb = new StringBuilder("\n");
        for (int y = -2; y < n + 2; y++)
        {
            for (int x = -2; x < n + 2; x++)
                sb.Append((uint)x < (uint)n && (uint)y < (uint)n && m[x, y] ? "██" : "  ");
            sb.Append('\n');
        }
        Console.Out.Write(sb.ToString());

        const int quiet = 4, scale = 14;
        int px = (n + quiet * 2) * scale;
        byte Gray(int sx, int sy)
        {
            int mx = sx / scale - quiet, my = sy / scale - quiet;
            return (uint)mx < (uint)n && (uint)my < (uint)n && m[mx, my] ? (byte)0 : (byte)255;
        }
        WritePng(path, px, Gray);
        log.Info($"QR for \"{text}\": {n}x{n} modules -> {px}x{px}px PNG at {Path.GetFullPath(path)}");
        return 0;
    }

    static void WritePng(string path, int size, Func<int, int, byte> px)
    {
        using var fs = File.Create(path);
        fs.Write([(byte)137, 80, 78, 71, 13, 10, 26, 10]);   // PNG signature

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, size); WriteBE(ihdr, 4, size);
        ihdr[8] = 8;   // 8-bit depth
        ihdr[9] = 0;   // colour type 0 = grayscale
        WriteChunk(fs, "IHDR", ihdr);

        var raw = new byte[(size + 1) * size];   // each scanline: 1 filter byte (0=None) + `size` gray bytes
        int o = 0;
        for (int y = 0; y < size; y++) { raw[o++] = 0; for (int x = 0; x < size; x++) raw[o++] = px(x, y); }
        byte[] comp;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            comp = ms.ToArray();
        }
        WriteChunk(fs, "IDAT", comp);
        WriteChunk(fs, "IEND", []);
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var tb = Encoding.ASCII.GetBytes(type);
        s.Write(tb); s.Write(data);
        uint crc = Crc32(tb, data);
        Span<byte> cb = stackalloc byte[4]; WriteBE(cb, 0, (int)crc); s.Write(cb);
    }

    static void WriteBE(Span<byte> b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static readonly uint[] CrcTable = BuildCrc();
    static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
