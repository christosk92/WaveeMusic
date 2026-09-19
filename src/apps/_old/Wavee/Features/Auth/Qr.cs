using System;
using System.Collections.Generic;

namespace Wavee;

// ── The QR encoder (byte mode, versions 1–10) ────────────────────────────────────────────────────────────────────────
// A compact, self-contained ISO/IEC 18004 implementation: GF(256) Reed–Solomon, the standard block/EC tables for v1–10,
// finder/alignment/timing/format/version structure, zig-zag data placement, and best-of-8 masking by the penalty score.
// Pure BCL (no FluentGpu) so the lean Wavee.Tests project can source-include it and round-trip-decode the output (QrTests).
// The BoxEl renderer that turns Encode()'s matrix into UI lives next door in QrGrid.cs.
static class Qr
{
    public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }   // the format-info index order is M,L,H,Q — mapped below

    // ── GF(256), primitive 0x11D ──
    static readonly int[] Exp = new int[512];
    static readonly int[] Log = new int[256];
    static Qr()
    {
        int x = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = x; Log[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11D; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }
    static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    // EC codewords per block, and block structure (count, dataCodewordsPerBlock) for group1[/group2] — versions 1..10, ECC M.
    // Each entry: { ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data }. (Group 2 blocks hold g2Data = g1Data+1.)
    static readonly int[][] M = // version 1..10
    {
        new[]{10,1,16,0,0}, new[]{16,1,28,0,0}, new[]{26,1,44,0,0}, new[]{18,2,32,0,0}, new[]{24,2,43,0,0},
        new[]{16,4,27,0,0}, new[]{18,4,31,0,0}, new[]{22,2,38,2,39}, new[]{22,3,36,2,37}, new[]{26,4,43,1,44},
    };
    static readonly int[][] L =
    {
        new[]{7,1,19,0,0}, new[]{10,1,34,0,0}, new[]{15,1,55,0,0}, new[]{20,1,80,0,0}, new[]{26,1,108,0,0},
        new[]{18,2,68,0,0}, new[]{20,2,78,0,0}, new[]{24,2,97,0,0}, new[]{30,2,116,0,0}, new[]{18,2,68,2,69},
    };
    static readonly int[][] Q =
    {
        new[]{13,1,13,0,0}, new[]{22,1,22,0,0}, new[]{18,2,17,0,0}, new[]{26,2,24,0,0}, new[]{18,2,15,2,16},
        new[]{24,4,19,0,0}, new[]{18,2,14,4,15}, new[]{22,4,18,2,19}, new[]{20,4,16,4,17}, new[]{24,6,19,2,20},
    };
    static readonly int[][] H =
    {
        new[]{17,1,9,0,0}, new[]{28,1,16,0,0}, new[]{22,2,13,0,0}, new[]{16,4,9,0,0}, new[]{22,2,11,2,12},
        new[]{28,4,15,0,0}, new[]{26,4,13,1,14}, new[]{26,4,14,2,15}, new[]{24,4,12,4,13}, new[]{28,6,15,2,16},
    };

    static int[][] Table(Ecc e) => e switch { Ecc.L => L, Ecc.Q => Q, Ecc.H => H, _ => M };

    // Alignment-pattern centre coordinates per version (1 = none). v2..10 each have one extra centre.
    static readonly int[][] AlignPos =
    {
        Array.Empty<int>(), new[]{6,18}, new[]{6,22}, new[]{6,26}, new[]{6,30},
        new[]{6,34}, new[]{6,22,38}, new[]{6,24,42}, new[]{6,26,46}, new[]{6,28,50},
    };

    public static bool[,] Encode(string text, Ecc ecc)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        // 1. smallest version (1..10) whose total data capacity holds the byte-mode payload.
        int version = -1, dataCodewords = 0;
        var tbl = Table(ecc);
        for (int v = 1; v <= 10; v++)
        {
            var t = tbl[v - 1];
            int dc = t[1] * t[2] + t[3] * t[4];
            int ccBits = v <= 9 ? 8 : 16;                          // byte-mode char-count indicator width
            int needBits = 4 + ccBits + bytes.Length * 8;
            if (needBits <= dc * 8) { version = v; dataCodewords = dc; break; }
        }
        if (version < 0) throw new ArgumentException("QR payload too large for v1–10");

        // 2. byte-mode bitstream → data codewords (mode 0100, count, data, terminator, pad bits, pad bytes 0xEC/0x11).
        var bits = new BitBuf();
        bits.Put(0b0100, 4);
        bits.Put(bytes.Length, version <= 9 ? 8 : 16);
        foreach (var b in bytes) bits.Put(b, 8);
        int cap = dataCodewords * 8;
        for (int i = 0; i < 4 && bits.Len < cap; i++) bits.Put(0, 1);   // terminator
        while (bits.Len % 8 != 0) bits.Put(0, 1);
        var data = bits.ToBytes();
        var padded = new int[dataCodewords];
        Array.Copy(data, padded, Math.Min(data.Length, dataCodewords));
        for (int i = data.Length, pad = 0; i < dataCodewords; i++, pad++) padded[i] = (pad & 1) == 0 ? 0xEC : 0x11;

        // 3. split into blocks, compute Reed–Solomon EC, interleave data then EC (the standard layout).
        var spec = tbl[version - 1];
        int ecLen = spec[0];
        int g1 = spec[1], g1d = spec[2], g2 = spec[3], g2d = spec[4];
        var blocksData = new List<int[]>();
        var blocksEc = new List<int[]>();
        int p = 0;
        for (int i = 0; i < g1; i++) { var blk = Slice(padded, ref p, g1d); blocksData.Add(blk); blocksEc.Add(RS(blk, ecLen)); }
        for (int i = 0; i < g2; i++) { var blk = Slice(padded, ref p, g2d); blocksData.Add(blk); blocksEc.Add(RS(blk, ecLen)); }

        var final = new List<int>();
        int maxData = Math.Max(g1d, g2d);
        for (int c = 0; c < maxData; c++) foreach (var blk in blocksData) if (c < blk.Length) final.Add(blk[c]);
        for (int c = 0; c < ecLen; c++) foreach (var blk in blocksEc) final.Add(blk[c]);

        // 4. lay out the matrix.
        int size = version * 4 + 17;
        var mod = new int[size, size];      // -1 = unset, 0/1 = light/dark, 2/3 = reserved light/dark (function patterns)
        for (int a = 0; a < size; a++) for (int b = 0; b < size; b++) mod[a, b] = -1;

        PlaceFinder(mod, 0, 0); PlaceFinder(mod, size - 7, 0); PlaceFinder(mod, 0, size - 7);
        PlaceTiming(mod, size);
        PlaceAlignment(mod, version, size);
        mod[8, size - 8] = 3;               // the always-dark module
        ReserveFormat(mod, size);
        if (version >= 7) ReserveVersion(mod, size);

        PlaceData(mod, size, final);

        // 5. choose the best mask (0..7) by the penalty score.
        int bestMask = 0, bestPenalty = int.MaxValue;
        int[,]? best = null;
        for (int mask = 0; mask < 8; mask++)
        {
            var trial = (int[,])mod.Clone();
            ApplyMask(trial, size, mask);
            PlaceFormat(trial, size, ecc, mask);
            if (version >= 7) PlaceVersion(trial, size, version);
            int pen = Penalty(trial, size);
            if (pen < bestPenalty) { bestPenalty = pen; bestMask = mask; best = trial; }
        }
        _ = bestMask;

        var outp = new bool[size, size];
        for (int a = 0; a < size; a++) for (int b = 0; b < size; b++) outp[a, b] = (best![a, b] & 1) == 1;
        return outp;
    }

    static int[] Slice(int[] src, ref int p, int len) { var r = new int[len]; Array.Copy(src, p, r, 0, len); p += len; return r; }

    static int[] RS(int[] data, int ecLen)
    {
        var gen = Generator(ecLen);
        var res = new int[ecLen];
        foreach (var d in data)
        {
            int factor = d ^ res[0];
            Array.Copy(res, 1, res, 0, ecLen - 1);
            res[ecLen - 1] = 0;
            // gen is the MONIC generator (length ecLen+1, leading 1 at gen[0]); the divisor coefficients are gen[1..ecLen].
            // Reading gen[i] here used the leading 1 and dropped the constant term → every EC codeword was wrong → the symbol
            // rendered fine but no reader could decode it (verified: gen[i+1] reproduces the canonical ISO/Thonky EC vector).
            for (int i = 0; i < ecLen; i++) res[i] ^= Mul(gen[i + 1], factor);
        }
        return res;
    }

    static int[] Generator(int n)
    {
        var g = new int[] { 1 };
        for (int i = 0; i < n; i++)
        {
            var ng = new int[g.Length + 1];
            for (int j = 0; j < g.Length; j++) { ng[j] ^= g[j]; ng[j + 1] ^= Mul(g[j], Exp[i]); }
            g = ng;
        }
        return g;
    }

    static void PlaceFinder(int[,] m, int ox, int oy)
    {
        for (int dy = -1; dy <= 7; dy++)
            for (int dx = -1; dx <= 7; dx++)
            {
                int x = ox + dx, y = oy + dy;
                if (x < 0 || y < 0 || x >= m.GetLength(0) || y >= m.GetLength(0)) continue;
                bool dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                            (dx == 0 || dx == 6 || dy == 0 || dy == 6 || (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                m[x, y] = dark ? 3 : 2;
            }
    }

    static void PlaceTiming(int[,] m, int size)
    {
        for (int i = 8; i < size - 8; i++)
        {
            if (m[i, 6] == -1) m[i, 6] = (i % 2 == 0) ? 3 : 2;
            if (m[6, i] == -1) m[6, i] = (i % 2 == 0) ? 3 : 2;
        }
    }

    static void PlaceAlignment(int[,] m, int version, int size)
    {
        var pos = AlignPos[version - 1];
        foreach (var cx in pos)
            foreach (var cy in pos)
            {
                if (m[cx, cy] != -1) continue;   // skips the ones overlapping finders
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        bool dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                        m[cx + dx, cy + dy] = dark ? 3 : 2;
                    }
            }
    }

    static void ReserveFormat(int[,] m, int size)
    {
        for (int i = 0; i < 9; i++) { if (m[i, 8] == -1) m[i, 8] = 2; if (m[8, i] == -1) m[8, i] = 2; }
        for (int i = 0; i < 8; i++) { if (m[size - 1 - i, 8] == -1) m[size - 1 - i, 8] = 2; if (m[8, size - 1 - i] == -1) m[8, size - 1 - i] = 2; }
    }

    static void ReserveVersion(int[,] m, int size)
    {
        for (int i = 0; i < 6; i++) for (int j = 0; j < 3; j++) { m[i, size - 11 + j] = 2; m[size - 11 + j, i] = 2; }
    }

    static void PlaceData(int[,] m, int size, List<int> codewords)
    {
        int bit = 0; int total = codewords.Count * 8;
        int Col = size - 1;
        bool up = true;
        while (Col > 0)
        {
            if (Col == 6) Col--;   // skip the vertical timing column
            for (int row = 0; row < size; row++)
            {
                int y = up ? size - 1 - row : row;
                for (int c = 0; c < 2; c++)
                {
                    int x = Col - c;
                    if (m[x, y] != -1) continue;
                    int v = 0;
                    if (bit < total) { v = (codewords[bit >> 3] >> (7 - (bit & 7))) & 1; bit++; }
                    m[x, y] = v;   // 0/1, unmasked data
                }
            }
            Col -= 2; up = !up;
        }
    }

    static bool IsData(int v) => v == 0 || v == 1;   // function-pattern cells are 2/3

    static void ApplyMask(int[,] m, int size, int mask)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (!IsData(m[x, y])) continue;
                bool inv = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => (x * y) % 2 + (x * y) % 3 == 0,
                    6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                    _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
                };
                if (inv) m[x, y] ^= 1;
            }
    }

    // Format info: 5-bit (ecc 2 bits + mask 3 bits) → 15-bit BCH, XOR 0x5412, placed in both copies.
    static readonly int[] EccFmt = { 1, 0, 3, 2 };   // L,M,Q,H → format field values 01,00,11,10
    static void PlaceFormat(int[,] m, int size, Ecc ecc, int mask)
    {
        int data = (EccFmt[(int)ecc] << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        int bitsv = ((data << 10) | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) m[8, i] = Bit(bitsv, i) | 2;
        m[8, 7] = Bit(bitsv, 6) | 2; m[8, 8] = Bit(bitsv, 7) | 2; m[7, 8] = Bit(bitsv, 8) | 2;
        for (int i = 9; i < 15; i++) m[14 - i, 8] = Bit(bitsv, i) | 2;

        for (int i = 0; i < 8; i++) m[size - 1 - i, 8] = Bit(bitsv, i) | 2;
        for (int i = 8; i < 15; i++) m[8, size - 15 + i] = Bit(bitsv, i) | 2;
    }

    // Indexed by version number. Version information exists only for version 7+; the zero slots keep the lookup literal.
    static readonly int[] VersionBits =
    {
        0,0,0,0,0,0,0, // v<7 unused
        0x07C94,0x085BC,0x09A99,0x0A4D3,0x0BBF6,0x0C762,0x0D847,0x0E60D,0x0F928,0x10B78,
    };
    static void PlaceVersion(int[,] m, int size, int version)
    {
        int v = VersionBits[version];
        for (int i = 0; i < 18; i++)
        {
            int b = (v >> i) & 1;
            int a = i / 3, c = i % 3;
            m[a, size - 11 + c] = b | 2;
            m[size - 11 + c, a] = b | 2;
        }
    }

    static int Bit(int v, int i) => (v >> i) & 1;

    static int Penalty(int[,] m, int size)
    {
        int score = 0;
        // Rule 1: runs of 5+ same-colour in rows/cols.
        for (int y = 0; y < size; y++) { score += RunPenalty(m, size, y, true); }
        for (int x = 0; x < size; x++) { score += RunPenalty(m, size, x, false); }
        // Rule 2: 2x2 blocks of one colour.
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                int v = m[x, y] & 1;
                if ((m[x + 1, y] & 1) == v && (m[x, y + 1] & 1) == v && (m[x + 1, y + 1] & 1) == v) score += 3;
            }
        // Rule 3: finder-like 1:1:3:1:1 runs (with a 4-module light margin) anywhere → a reader can mistake them for a
        // real finder. Penalise 40 each so the mask chooser avoids them (this rule was MISSING → a bad mask could be picked).
        for (int y = 0; y < size; y++)
            for (int x = 0; x <= size - 11; x++)
                if (FinderLike(m, x, y, true)) score += 40;
        for (int x = 0; x < size; x++)
            for (int y = 0; y <= size - 11; y++)
                if (FinderLike(m, x, y, false)) score += 40;
        // Rule 4: proportion of dark.
        int dark = 0;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) dark += m[x, y] & 1;
        int pct = dark * 100 / (size * size);
        score += Math.Min(Math.Abs(pct - 50) / 5, Math.Abs(pct - 50 + 4) / 5) * 10;  // approx of the 5%-step rule
        return score;
    }

    static int RunPenalty(int[,] m, int size, int line, bool row)
    {
        int score = 0, run = 1, prev = -1;
        for (int i = 0; i < size; i++)
        {
            int v = row ? m[i, line] & 1 : m[line, i] & 1;
            if (v == prev) { run++; if (run == 5) score += 3; else if (run > 5) score += 1; }
            else { run = 1; prev = v; }
        }
        return score;
    }

    // The two finder-like sequences (Rule 3): dark,light,dark,dark,dark,light,dark + a 4-module light margin, either side.
    static readonly int[] FinderA = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
    static readonly int[] FinderB = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
    static bool FinderLike(int[,] m, int x, int y, bool row)
    {
        bool a = true, b = true;
        for (int k = 0; k < 11; k++)
        {
            int v = row ? m[x + k, y] & 1 : m[x, y + k] & 1;
            if (v != FinderA[k]) a = false;
            if (v != FinderB[k]) b = false;
        }
        return a || b;
    }

    sealed class BitBuf
    {
        readonly List<byte> _bits = new();
        public int Len => _bits.Count;
        public void Put(int value, int width) { for (int i = width - 1; i >= 0; i--) _bits.Add((byte)((value >> i) & 1)); }
        public int[] ToBytes()
        {
            int n = (_bits.Count + 7) / 8; var r = new int[n];
            for (int i = 0; i < _bits.Count; i++) r[i >> 3] |= _bits[i] << (7 - (i & 7));
            return r;
        }
    }
}
