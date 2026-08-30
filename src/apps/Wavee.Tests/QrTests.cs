using System.Text;
using Xunit;

namespace Wavee.Tests;

// Regression guard for the QR encoder (Qr, source-included). The original bug was a Reed–Solomon off-by-one that made every
// EC codeword wrong: the symbol RENDERED fine but no reader could decode it. These tests close that gap by DECODING the
// encoder's own output with a standard read path (format BCH → mask removal → zig-zag → RS syndrome check → byte parse) and
// asserting it round-trips. A future RS/placement/mask regression then fails here instead of silently shipping a dead code.
public class QrTests
{
    [Theory]
    [InlineData("HELLO")]                                          // v1-M
    [InlineData("https://spotify.com/pair")]                       // v2-M
    [InlineData("https://spotify.com/pair?code=WZY5Q6TX")]         // v3-M (the live pairing URL)
    public void Encoded_qr_round_trips_through_a_standard_reader(string text)
    {
        var m = Qr.Encode(text, Qr.Ecc.M);
        var (decoded, ecc, syndromesZero) = QrReader.Decode(m);

        Assert.True(syndromesZero, "RS syndromes must be all-zero (a valid, error-free QR codeword)");
        Assert.Equal("M", ecc);
        Assert.Equal(text, decoded);
    }
}

// A minimal, standard-conformant QR reader for single-block ECC-M symbols (v1–v3 cover the pairing URL and its neighbours),
// written independently of the encoder's write path so a placement/mask/format/RS regression shows up as a decode mismatch.
static class QrReader
{
    public static (string text, string ecc, bool syndromesZero) Decode(bool[,] m)
    {
        int n = m.GetLength(0);
        int version = (n - 17) / 4;

        // format info (copy 1), de-XOR 0x5412, nearest of the 32 candidates → ecc + mask
        int fmtRaw = 0, bi = 0;
        void F(int x, int y) { fmtRaw |= (m[x, y] ? 1 : 0) << bi; bi++; }
        for (int i = 0; i <= 5; i++) F(8, i);
        F(8, 7); F(8, 8); F(7, 8);
        for (int i = 9; i < 15; i++) F(14 - i, 8);
        int best = 0, bestDist = 99;
        for (int d = 0; d < 32; d++)
        {
            int rem = d; for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            int enc = ((d << 10) | rem) ^ 0x5412;
            int dist = System.Numerics.BitOperations.PopCount((uint)(enc ^ fmtRaw));
            if (dist < bestDist) { bestDist = dist; best = d; }
        }
        int mask = best & 7;
        string ecc = ((best >> 3) & 3) switch { 1 => "L", 0 => "M", 3 => "Q", _ => "H" };

        // function-pattern map: finders+separators, timing row/col 6, one alignment centred at (n-7) for v≥2, format strips
        bool Func(int x, int y) =>
            (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8) ||
            x == 6 || y == 6 ||
            (version >= 2 && x >= n - 9 && x <= n - 5 && y >= n - 9 && y <= n - 5) ||
            (y == 8 && (x <= 8 || x >= n - 8)) || (x == 8 && (y <= 8 || y >= n - 8));

        bool MaskBit(int x, int y) => mask switch
        {
            0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
            4 => (y / 2 + x / 3) % 2 == 0, 5 => (x * y) % 2 + (x * y) % 3 == 0,
            6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0, _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
        };

        // zig-zag read (right→left column pairs, skip timing col 6), remove the data mask → codeword stream
        var bits = new System.Collections.Generic.List<int>();
        for (int col = n - 1, up = 1; col > 0; col -= 2, up ^= 1)
        {
            if (col == 6) col--;
            for (int r = 0; r < n; r++)
            {
                int y = up == 1 ? n - 1 - r : r;
                for (int c = 0; c < 2; c++)
                {
                    int x = col - c;
                    if (Func(x, y)) continue;
                    bits.Add((m[x, y] ? 1 : 0) ^ (MaskBit(x, y) ? 1 : 0));
                }
            }
        }
        var cw = new System.Collections.Generic.List<int>();
        for (int i = 0; i + 8 <= bits.Count; i += 8) { int v = 0; for (int k = 0; k < 8; k++) v = (v << 1) | bits[i + k]; cw.Add(v); }

        // RS syndrome check (single block) over GF(256), primitive 0x11D
        int[] Exp = new int[512], Log = new int[256];
        { int xx = 1; for (int i = 0; i < 255; i++) { Exp[i] = xx; Log[xx] = i; xx <<= 1; if ((xx & 0x100) != 0) xx ^= 0x11D; } for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255]; }
        int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
        int ecLen = version switch { 1 => 10, 2 => 16, _ => 26 };
        bool syn = true;
        for (int j = 0; j < ecLen; j++) { int acc = 0; foreach (var c in cw) acc = Mul(acc, Exp[j]) ^ c; if (acc != 0) syn = false; }

        // byte segment: mode(4)=byte, count(8), then count UTF-8 bytes
        int bp = 0;
        int Read(int nb) { int v = 0; for (int k = 0; k < nb; k++) { v = (v << 1) | ((cw[bp >> 3] >> (7 - (bp & 7))) & 1); bp++; } return v; }
        int mode = Read(4), count = Read(8);
        var by = new byte[count];
        for (int i = 0; i < count; i++) by[i] = (byte)Read(8);
        string text = mode == 4 ? Encoding.UTF8.GetString(by) : $"<mode {mode}>";
        return (text, ecc, syn);
    }
}
