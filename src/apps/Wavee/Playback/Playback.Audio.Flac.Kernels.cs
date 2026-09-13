// ── Playback/Playback.Audio.Flac.Kernels.cs ────────────────────────────────────────────────────────────────────────
// The FLAC decoder's inner loops: the 64-bit bit reader, the Rice partition, the LPC and FIXED restorations, the
// stereo decorrelation, the wasted-bits shift, the int→float conversion and the two CRCs
//
// Role: CORE
// Owner: U
// Wave: 3 (parallel — pure over spans, no wave dependency)
// Budget: 920 lines (split out of `Playback.Audio.Flac.cs` by the optimisation pass: the two halves together
//         passed that file's 1,100 + 30 %, so the arithmetic became this named partial and the format stayed there)
// Spec: docs/plans/wavee/wavee-0.3-flac-implementation.md §3 + RFC 9639
//
// WHY THIS FILE EXISTS. `Playback.Audio.Flac.cs` is the FORMAT — what a metadata block is, what a frame header
// says, which subframe type the six bits name, how a seek is planned. This file is the ARITHMETIC, and it is
// written the way Christos asked the decoders to be written: unsafe, over pinned buffers, with the bounds proven
// once at the loop head and never inside it, with `Unsafe.ReadUnaligned` where a byte-at-a-time loop was, and with
// SIMD exactly where the elements are independent and there are more than ~16 of them (P15) and nowhere else.
// It follows the house pattern of the Vorbis plan (docs/plans/wavee/wavee-0.3-vorbis-implementation.md §3.3-§3.8):
// the two decoders read alike on purpose — the bit reader below is that plan's LSB-first reader, mirrored.
//
// THE FOUR THINGS THAT MAKE IT FAST, in the order they cost:
//   1. The bit reader refills with ONE unaligned 8-byte big-endian load instead of eight shifts (Fabian Giesen's
//      "reading bits in far too many ways, part 2" variant 4, mirrored MSB-first; the same refill the Vorbis plan
//      §3.3 specifies LSB-first). `ReadUnary` is one `LeadingZeroCount` over the cache, never a bit loop
//      (libFLAC `FLAC__bitreader_read_unary_unsigned`, bitreader.c:725-803).
//   2. `ReadRicePartition` keeps the cache, the bit count and the byte cursor in LOCALS for the whole partition
//      and writes through an `int*`, so a residual costs a CLZ, two shifts and a store — no field reload, no
//      bounds check, no call. This is libFLAC's `FLAC__bitreader_read_rice_signed_block`
//      (deduplication/bitreader_read_rice_signed_block.c:37-141) in C#, including its "hand this one value back to
//      the general reader" tail.
//   3. `RestoreLpc` is libFLAC's shape: a separately unrolled kernel for every order 1-12 (the subset ceiling) on
//      the 32-bit accumulator, the common orders (1-4, 6, 8, 12) unrolled on the 64-bit one, a 4-way-unrolled
//      general path for everything else, and the 32-bit accumulator whenever `bps + precision + log2(order) + 1`
//      fits in one (`FLAC__lpc_restore_signal` vs `_wide`, the unrolled builds at lpc.c:1018-1239 and 1270-1491).
//   4. `Crc16` is slicing-by-8, eight bytes and eight table lookups per round (libFLAC `FLAC__crc16`,
//      crc.c:376-396, tables built by `FLAC__crc16_init_table`, crc.c:345-364). The frame CRC covers every byte of
//      every frame, so it is the one place in the decoder that is pure byte throughput.
//
// WHERE SIMD IS **NOT**, and why — this is a decision, not an omission:
//   • `RestoreLpc` / `RestoreFixed`. `s[i]` needs `s[i−1]`: the filter is a serial recurrence and the horizontal
//     reduction would sit on its critical path. libFLAC agrees — `lpc_intrin_sse41.c` and `lpc_intrin_neon.c`
//     ship vector kernels for `compute_autocorrelation` and `compute_residual` (the ENCODER direction, where the
//     outputs are independent) and none for `restore_signal`, which stays the plain C of lpc.c:978-1239 and
//     fixed.c:571-599. Symphonia is scalar here too (decoder.rs:716-752).
//   • The Rice loop. Every residual's bit length depends on the one before it; there is nothing to widen.
//   • `Crc8`. Sixteen header bytes at most — below any threshold worth a table wider than one.
// What is left — the decorrelation, the wasted-bits shift and the int→float scale — is elementwise over a whole
// block, and that is where `Vector256` runs with a `Vector128` (NEON: arm64 ships) path under it and a scalar tail
// that is also the whole path on a short block. `ForceScalar` turns every one of them off so a test can assert the
// two agree bit for bit.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Wavee;

public static partial class Playback
{
    public static unsafe partial class Flac
    {
        // ── K0. The scalar switch ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Forces every vector kernel in this file down its scalar path. A plain static, set by
        /// <c>FlacTests</c> around one assert and put back: the point is that the two paths are the same function,
        /// which is a fact a test can hold rather than a comment. Never written by the app (P-rule: no environment
        /// switch, no build flag — the scalar path is reached on a short block anyway, and on a host without
        /// acceleration, so it is live code either way).</summary>
        public static bool ForceScalar;

        // ── K1. The bit reader (§3.3) ───────────────────────────────────────────────────────────────────────────────

        /// <summary>MSB-first bit reader over a byte span. A 64-bit cache refilled with ONE unaligned big-endian
        /// 8-byte load (Giesen variant 4, mirrored: the valid bits are the TOP <see cref="Bits"/> of the cache, new
        /// bits are OR'd in underneath and the cursor advances only by the whole bytes that were counted, so the
        /// bits below the count are re-read next time and the OR is idempotent). A window of any length is legal —
        /// the last seven bytes take the byte-at-a-time tail — and reading past the end sets <see cref="Overrun"/>
        /// and yields zeros, never throws. A <c>ref struct</c> passed by <c>ref</c>: the Rice loop runs 88,200 times
        /// a second on a 16/44.1 stereo file and nothing in it may allocate or branch on a field load.</summary>
        public ref struct BitReader
        {
            readonly ReadOnlySpan<byte> _b;
            int _pos;          // next byte to load into the cache
            ulong _cache;      // MSB-aligned: the next bit to read is bit 63
            int _bits;         // valid bits in _cache

            /// <summary>Set once the reader has been asked for bits the span does not have. Never cleared: the frame
            /// decoder checks it once, at the end, and discards the whole frame.</summary>
            public bool Overrun;

            public BitReader(ReadOnlySpan<byte> bytes, int start)
            {
                _b = bytes;
                _pos = start;
                _cache = 0;
                _bits = 0;
                Overrun = false;
            }

            /// <summary>Byte offset of the next unread bit's byte. Exact only after <see cref="AlignToByte"/>.
            /// The refill keeps <c>_pos * 8 − _bits</c> equal to the bits consumed, which is what makes it exact.</summary>
            public readonly int BytePosition => _pos - (_bits >> 3);

            /// <summary>True while the reader is still inside its span.</summary>
            public readonly bool Ok => !Overrun;

            /// <summary>Valid bits in the cache. ≥ 56 after a <see cref="Refill"/> that had eight bytes to read.</summary>
            public readonly int Bits => _bits;

            /// <summary>Top the cache up to ≥ 56 bits with one unaligned 8-byte load while eight bytes remain; the
            /// tail is read a byte at a time. <c>_bits | 56</c> is <c>_bits + 8·((63 − _bits) &gt;&gt; 3)</c> for
            /// every <c>_bits</c> in 0..56 — the identity Giesen's variant 4 turns on.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Refill()
            {
                if (_bits >= 56) return;
                int pos = _pos;
                if (pos + 8 <= _b.Length)
                {
                    ulong w = BinaryPrimitives.ReverseEndianness(
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_b), (nint)(uint)pos)));
                    _cache |= w >> _bits;                       // _bits ≤ 55 here, so the shift never masks
                    _pos = pos + ((63 - _bits) >> 3);
                    _bits |= 56;
                    return;
                }
                RefillTail();
            }

            /// <summary>The last seven bytes of the window, and any window shorter than eight bytes.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            void RefillTail()
            {
                while (_bits <= 56)
                {
                    if (_pos >= _b.Length) return;              // the callers decide whether that is an overrun
                    _cache |= (ulong)_b[_pos++] << (56 - _bits);
                    _bits += 8;
                }
            }

            /// <summary>The next <paramref name="n"/> (1..32) bits without consuming them. The caller has refilled
            /// (<see cref="Bits"/> ≥ n) or accepts the zeros a spent cache yields.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly uint Peek(int n) => (uint)(_cache >> (64 - n));

            /// <summary>Drop <paramref name="n"/> (0..32) bits. Branch-light: one shift, one subtract, and the
            /// overrun test the subtract already set the flags for.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Consume(int n)
            {
                _cache <<= n;
                _bits -= n;
                if (_bits < 0) { Overrun = true; _bits = 0; _cache = 0; }
            }

            /// <summary>Read 0..32 bits. <paramref name="n"/> == 0 is legal and returns 0 (a Rice parameter of 0 is
            /// common, and so is a 0-bit "constant" subframe after wasted bits).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint Read(int n)
            {
                if (n == 0) return 0;
                if (_bits < n)
                {
                    Refill();
                    if (_bits < n) { Overrun = true; _bits = 0; _cache = 0; return 0; }
                }
                uint v = (uint)(_cache >> (64 - n));
                _cache <<= n;
                _bits -= n;
                return v;
            }

            /// <summary>Read 33..64 bits (STREAMINFO's 36-bit total-samples field, a seek point's 64-bit fields).</summary>
            public ulong ReadLong(int n)
            {
                if (n <= 32) return Read(n);
                ulong hi = Read(n - 32);
                return (hi << 32) | Read(32);
            }

            /// <summary>Read n bits as a two's-complement signed value (n ≤ 32).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadSigned(int n) => n == 0 ? 0 : (int)(Read(n) << (32 - n)) >> (32 - n);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool ReadBit() => Read(1) != 0;

            /// <summary>Count zeros up to and including the terminating one — the Rice quotient (§9.2.7). One
            /// <c>LeadingZeroCount</c> per cache fill, not one branch per bit (libFLAC
            /// <c>FLAC__bitreader_read_unary_unsigned</c>, bitreader.c:744-770, which is the same CLZ over a word).
            /// The refill at &lt; 32 bits means a quotient under 32 — every quotient a real file carries — costs one
            /// predicted branch and one CLZ with no loop at all.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadUnary()
            {
                int n = 0;
                while (true)
                {
                    if (_bits < 32) Refill();
                    if (_bits == 0) { Overrun = true; return 0; }
                    int z = BitOperations.LeadingZeroCount(_cache);
                    if (z < _bits)
                    {
                        n += z;
                        int consume = z + 1;                        // the zeros AND the terminating one
                        // C# MASKS a shift count — `x << 64` is `x << 0`, not zero — and this is the one place the
                        // reader can consume all 64 cached bits at once: a Rice quotient of exactly 63 with a full
                        // cache. Without this branch the terminating one survives in the cache, the next Refill ORs
                        // a byte on top of it, and every bit from there is off: the frame then fails its own CRC-16.
                        // (xiph vector 61, the 16-bit predictor-overflow file, is where quotients get that long.)
                        _cache = consume == 64 ? 0UL : _cache << consume;
                        _bits -= consume;
                        return n;
                    }
                    n += _bits;
                    _cache = 0;
                    _bits = 0;
                }
            }

            /// <summary>§9.2.7's Rice partition, decoded straight into <paramref name="dst"/>. The cache, the bit
            /// count and the byte cursor live in LOCALS for the whole partition and the residuals are written
            /// through an <c>int*</c>, so the common value — a quotient under 32 with the quotient, the stop bit and
            /// the <paramref name="param"/> low bits all already cached — costs a CLZ, two shifts, an XOR and a
            /// store. Anything else (a long quotient, a value straddling the end of the window) flushes the locals
            /// back to the fields and lets <see cref="ReadUnary"/> / <see cref="Read"/> answer that one value, then
            /// reloads: libFLAC's <c>process_tail</c> structure exactly
            /// (deduplication/bitreader_read_rice_signed_block.c:49-131). Bit-identical to the scalar loop it
            /// replaces — same zigzag, same <c>(q &lt;&lt; param) | r</c> composition, same overrun behaviour.</summary>
            public void ReadRicePartition(int param, Span<int> dst)
            {
                if (dst.Length == 0) return;
                ulong cache = _cache;
                int bits = _bits, pos = _pos, len = _b.Length;
                ref byte src = ref MemoryMarshal.GetReference(_b);
                int need = 33 + param;                              // a quotient under 32, its stop bit, the LSBs

                fixed (int* d0 = dst)
                {
                    int* d = d0;
                    int* end = d0 + dst.Length;
                    while (d < end)
                    {
                        if (bits < need && bits < 56)
                        {
                            if (pos + 8 <= len)
                            {
                                ulong w = BinaryPrimitives.ReverseEndianness(
                                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, (nint)(uint)pos)));
                                cache |= w >> bits;
                                pos += (63 - bits) >> 3;
                                bits |= 56;
                            }
                            else
                            {
                                while (bits <= 56 && pos < len)
                                {
                                    cache |= (ulong)Unsafe.Add(ref src, (nint)(uint)pos) << (56 - bits);
                                    pos++;
                                    bits += 8;
                                }
                            }
                        }

                        int q = BitOperations.LeadingZeroCount(cache);
                        // q < 32 bounds `q + 1 + param` at 62, which is what keeps every shift below C#'s mask.
                        if (q < 32 && q + 1 + param <= bits)
                        {
                            cache <<= q + 1;
                            uint r = param == 0 ? 0u : (uint)(cache >> (64 - param));
                            cache <<= param;
                            bits -= q + 1 + param;
                            uint u = ((uint)q << param) | r;
                            *d++ = (int)(u >> 1) ^ -(int)(u & 1);   // zigzag (§9.2.7)
                            continue;
                        }

                        _cache = cache; _bits = bits; _pos = pos;   // the tail: one value through the general reader
                        uint qq = (uint)ReadUnary();
                        uint rr = Read(param);
                        cache = _cache; bits = _bits; pos = _pos;   // past the end both reads yield zeros, exactly
                        uint uu = (qq << param) | rr;               // as the value-at-a-time loop did
                        *d++ = (int)(uu >> 1) ^ -(int)(uu & 1);
                    }
                }

                _cache = cache;
                _bits = bits;
                _pos = pos;
            }

            /// <summary>Drop the bits to the next byte boundary — the end of the subframes, before the CRC-16 (§9.3).</summary>
            public void AlignToByte()
            {
                int drop = _bits & 7;
                _cache <<= drop;
                _bits -= drop;
            }
        }

        // ── K2. CRC-8 and CRC-16 (§9.1.8, §9.3) ─────────────────────────────────────────────────────────────────────
        //
        // CRC-8: poly x^8+x^2+x+1 = 0x07, init 0, one 256-entry table. CRC-16/UMTS: poly x^16+x^15+x^2+1 = 0x8005,
        // init 0, SLICING BY EIGHT — eight tables of 256, built from the first by libFLAC's own recurrence
        // (crc.c:345-364), consumed eight bytes at a time (crc.c:376-396). 4 KiB of table at type init pays on the
        // one loop in the decoder that touches every byte of every frame (a 4096-sample 16-bit stereo frame is up to
        // ~16 KB of CRC): the single-table version is one lookup per byte, each waiting on the last; the sliced one
        // is eight lookups per eight bytes that do not wait on each other. (The comment this replaced said libFLAC
        // uses one table; the current crc.c does not.)
        // The tables are BUILT from the polynomial — there is no 2,048-entry literal here to get wrong.

        static readonly byte[] s_crc8 = BuildCrc8();
        static readonly ushort[] s_crc16 = BuildCrc16();

        static byte[] BuildCrc8()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
                t[i] = (byte)c;
            }
            return t;
        }

        static ushort[] BuildCrc16()
        {
            var t = new ushort[8 * 256];
            for (int i = 0; i < 256; i++)
            {
                int c = i << 8;
                for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) & 0xFFFF : (c << 1) & 0xFFFF;
                t[i] = (ushort)c;
            }
            for (int j = 1; j < 8; j++)
                for (int i = 0; i < 256; i++)
                {
                    ushort prev = t[((j - 1) << 8) + i];
                    t[(j << 8) + i] = (ushort)(t[prev >> 8] ^ (prev << 8));
                }
            return t;
        }

        /// <summary>The frame header's CRC-8 (check value 0xF4 over "123456789"). A header is at most sixteen bytes,
        /// so this stays one table and one dependent lookup per byte — a wider slice would cost more to set up than
        /// the whole call.</summary>
        public static byte Crc8(ReadOnlySpan<byte> b)
        {
            if (b.IsEmpty) return 0;
            byte c = 0;
            fixed (byte* p = b)
            fixed (byte* t = s_crc8)
            {
                byte* q = p;
                byte* end = p + b.Length;
                while (q < end) c = t[c ^ *q++];
            }
            return c;
        }

        /// <summary>The frame footer's CRC-16, over everything from the sync byte (CRC-16/UMTS; check 0xFEE8).
        /// Slicing by eight (libFLAC <c>FLAC__crc16</c>, crc.c:376-396): eight independent lookups
        /// per round, so the loop is bound by the L1 hits and not by a sixteen-deep dependency chain.</summary>
        public static ushort Crc16(ReadOnlySpan<byte> b)
        {
            if (b.IsEmpty) return 0;
            uint crc = 0;
            fixed (byte* p = b)
            fixed (ushort* t = s_crc16)
            {
                byte* q = p;
                int n = b.Length;
                while (n >= 8)
                {
                    crc ^= (uint)((q[0] << 8) | q[1]);
                    crc = (uint)(t[0x700 + (crc >> 8)] ^ t[0x600 + (crc & 0xFF)]
                               ^ t[0x500 + q[2]] ^ t[0x400 + q[3]]
                               ^ t[0x300 + q[4]] ^ t[0x200 + q[5]]
                               ^ t[0x100 + q[6]] ^ t[q[7]]);
                    q += 8;
                    n -= 8;
                }
                while (n-- > 0) crc = ((crc << 8) ^ (uint)t[(crc >> 8) ^ *q++]) & 0xFFFFu;
            }
            return (ushort)crc;
        }

        // ── K3. LPC restoration (§9.2.6) ────────────────────────────────────────────────────────────────────────────

        /// <summary>The recurrence <c>s[i] += (Σ c[j]·s[i−1−j]) &gt;&gt; shift</c>, restored in place over the
        /// residuals from index <c>c.Length</c> on. It IS a recurrence — sample i needs sample i−1 — so it does not
        /// vectorise across samples, and libFLAC does not try: <c>lpc_intrin_sse41.c</c> and <c>lpc_intrin_neon.c</c>
        /// vectorise <c>compute_autocorrelation</c> and <c>compute_residual</c> (the encoder's direction, where the
        /// outputs are independent) and neither has a <c>restore_signal</c> kernel. What libFLAC does instead, and
        /// what this is, is one unrolled loop per order up to the subset's ceiling of 12 and a general path above
        /// that (<c>FLAC__lpc_restore_signal</c>, lpc.c:1018-1239; the general path here is 4-way unrolled).
        /// <para><paramref name="sumBits"/> picks the accumulator: when <c>bps + precision + log2(order) + 1</c>
        /// fits in 32 bits the products cannot overflow an <c>int</c> and the narrow path runs; otherwise the long
        /// path does (libFLAC's <c>_wide</c> split, lpc.c:1270-1491). The xiph "predictor overflow check" vectors
        /// 61-63 are exactly this decision at the edge.</para></summary>
        public static void RestoreLpc(Span<int> s, ReadOnlySpan<int> c, int shift, int sumBits)
        {
            int order = c.Length;
            if (order <= 0 || order > MaxLpcOrder || s.Length <= order) return;
            fixed (int* d = s)
            fixed (int* q = c)
            {
                if (sumBits <= 32) RestoreLpcNarrow(d, d + s.Length, q, order, shift);
                else RestoreLpcWide(d, d + s.Length, q, order, shift);
            }
        }

        /// <summary>The 32-bit accumulator, one unrolled kernel per order 1-12 (lpc.c:1030-1197).</summary>
        static void RestoreLpcNarrow(int* d, int* end, int* q, int order, int shift)
        {
            int* p = d + order;
            switch (order)
            {
                case 1:
                    for (; p < end; p++) *p += (q[0] * p[-1]) >> shift;
                    return;
                case 2:
                    for (; p < end; p++) *p += (q[0] * p[-1] + q[1] * p[-2]) >> shift;
                    return;
                case 3:
                    for (; p < end; p++) *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3]) >> shift;
                    return;
                case 4:
                    for (; p < end; p++) *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4]) >> shift;
                    return;
                case 5:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4] + q[4] * p[-5]) >> shift;
                    return;
                case 6:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4]
                             + q[4] * p[-5] + q[5] * p[-6]) >> shift;
                    return;
                case 7:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4]
                             + q[4] * p[-5] + q[5] * p[-6] + q[6] * p[-7]) >> shift;
                    return;
                case 8:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4]
                             + q[4] * p[-5] + q[5] * p[-6] + q[6] * p[-7] + q[7] * p[-8]) >> shift;
                    return;
                case 9:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4] + q[4] * p[-5]
                             + q[5] * p[-6] + q[6] * p[-7] + q[7] * p[-8] + q[8] * p[-9]) >> shift;
                    return;
                case 10:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4] + q[4] * p[-5]
                             + q[5] * p[-6] + q[6] * p[-7] + q[7] * p[-8] + q[8] * p[-9] + q[9] * p[-10]) >> shift;
                    return;
                case 11:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4] + q[4] * p[-5]
                             + q[5] * p[-6] + q[6] * p[-7] + q[7] * p[-8] + q[8] * p[-9] + q[9] * p[-10]
                             + q[10] * p[-11]) >> shift;
                    return;
                case 12:
                    for (; p < end; p++)
                        *p += (q[0] * p[-1] + q[1] * p[-2] + q[2] * p[-3] + q[3] * p[-4] + q[4] * p[-5]
                             + q[5] * p[-6] + q[6] * p[-7] + q[7] * p[-8] + q[8] * p[-9] + q[9] * p[-10]
                             + q[10] * p[-11] + q[11] * p[-12]) >> shift;
                    return;
                default:
                    for (; p < end; p++)
                    {
                        int sum = 0;
                        int j = 0;
                        for (; j + 4 <= order; j += 4)
                            sum += q[j] * p[-1 - j] + q[j + 1] * p[-2 - j]
                                 + q[j + 2] * p[-3 - j] + q[j + 3] * p[-4 - j];
                        for (; j < order; j++) sum += q[j] * p[-1 - j];
                        *p += sum >> shift;
                    }
                    return;
            }
        }

        /// <summary>The 64-bit accumulator — the same kernels with <c>long</c> products, for a stream whose
        /// <c>bps + precision + log2(order) + 1</c> does not fit in 32 bits (lpc.c:1270-1491). A 32-bit side channel
        /// at order 12 is the case that reaches it.</summary>
        static void RestoreLpcWide(int* d, int* end, int* q, int order, int shift)
        {
            int* p = d + order;
            switch (order)
            {
                case 1:
                    for (; p < end; p++) *p += (int)(((long)q[0] * p[-1]) >> shift);
                    return;
                case 2:
                    for (; p < end; p++) *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2]) >> shift);
                    return;
                case 3:
                    for (; p < end; p++)
                        *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2] + (long)q[2] * p[-3]) >> shift);
                    return;
                case 4:
                    for (; p < end; p++)
                        *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2] + (long)q[2] * p[-3]
                                   + (long)q[3] * p[-4]) >> shift);
                    return;
                case 6:
                    for (; p < end; p++)
                        *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2] + (long)q[2] * p[-3]
                                   + (long)q[3] * p[-4] + (long)q[4] * p[-5] + (long)q[5] * p[-6]) >> shift);
                    return;
                case 8:
                    for (; p < end; p++)
                        *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2] + (long)q[2] * p[-3]
                                   + (long)q[3] * p[-4] + (long)q[4] * p[-5] + (long)q[5] * p[-6]
                                   + (long)q[6] * p[-7] + (long)q[7] * p[-8]) >> shift);
                    return;
                case 12:
                    for (; p < end; p++)
                        *p += (int)(((long)q[0] * p[-1] + (long)q[1] * p[-2] + (long)q[2] * p[-3]
                                   + (long)q[3] * p[-4] + (long)q[4] * p[-5] + (long)q[5] * p[-6]
                                   + (long)q[6] * p[-7] + (long)q[7] * p[-8] + (long)q[8] * p[-9]
                                   + (long)q[9] * p[-10] + (long)q[10] * p[-11] + (long)q[11] * p[-12]) >> shift);
                    return;
                default:
                    for (; p < end; p++)
                    {
                        long sum = 0;
                        int j = 0;
                        for (; j + 4 <= order; j += 4)
                            sum += (long)q[j] * p[-1 - j] + (long)q[j + 1] * p[-2 - j]
                                 + (long)q[j + 2] * p[-3 - j] + (long)q[j + 3] * p[-4 - j];
                        for (; j < order; j++) sum += (long)q[j] * p[-1 - j];
                        *p += (int)(sum >> shift);
                    }
                    return;
            }
        }

        // ── K4. FIXED restoration (§9.2.5) ──────────────────────────────────────────────────────────────────────────

        /// <summary>§9.2.5's fixed polynomial predictors, restored in place over the residuals from index
        /// <paramref name="order"/> on. Order 0 is the residual itself. Unrolled pointer loops, one per order
        /// (libFLAC <c>FLAC__fixed_restore_signal</c>, fixed.c:571-599; Symphonia <c>fixed_predict</c>, decoder.rs:663-710).
        /// The long arithmetic for orders 2-4 is Symphonia's and is the honest form: a 32-bit side channel's order-4
        /// sum can exceed 32 bits before the residual brings it back, and the low 32 bits of the long are what the
        /// format asks for.</summary>
        public static void RestoreFixed(Span<int> s, int order)
        {
            if (order <= 0 || order > 4 || s.Length <= order) return;
            fixed (int* d = s)
            {
                int* p = d + order;
                int* end = d + s.Length;
                switch (order)
                {
                    case 1:
                        for (; p < end; p++) *p += p[-1];
                        return;
                    case 2:
                        for (; p < end; p++) *p += (int)(2L * p[-1] - p[-2]);
                        return;
                    case 3:
                        for (; p < end; p++) *p += (int)(3L * p[-1] - 3L * p[-2] + p[-3]);
                        return;
                    default:
                        for (; p < end; p++) *p += (int)(4L * p[-1] - 6L * p[-2] + 4L * p[-3] - p[-4]);
                        return;
                }
            }
        }

        // ── K5. Decorrelation, the wasted-bits shift, and the float conversion — the SIMD sites (P15) ────────────────

        /// <summary>§4.2, in place over the two channel runs. Left/side: R = L − S. Side/right: L = S + R. Mid/side:
        /// mid = (M &lt;&lt; 1) | (S &amp; 1); L = (mid + S) &gt;&gt; 1; R = (mid − S) &gt;&gt; 1. Anything other than
        /// assignments 1..3 is not a decorrelation and is a no-op.
        /// <para>The elements are independent, so this is one of the three SIMD sites: <c>Vector256</c> where the
        /// host has it, <c>Vector128</c> under it (arm64 ships), and a scalar tail that is also the whole path below
        /// ~16 samples (P15). The assignment is hoisted OUT of the vector loop — three loops, not one loop with a
        /// switch in it.</para></summary>
        public static void Decorrelate(byte assignment, Span<int> ch0, Span<int> ch1)
        {
            if (assignment is < 1 or > 3) return;
            int n = ch0.Length < ch1.Length ? ch0.Length : ch1.Length;
            int i = 0;
            if (n >= 16 && !ForceScalar && (Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
            {
                fixed (int* a = ch0)
                fixed (int* b = ch1)
                    i = Vector256.IsHardwareAccelerated
                        ? Decorrelate256(assignment, a, b, n)
                        : Decorrelate128(assignment, a, b, n);
            }
            for (; i < n; i++)                                                                 // tail, and the whole
            {                                                                                  // thing on a short block
                switch (assignment)
                {
                    case 1:
                        ch1[i] = ch0[i] - ch1[i];
                        break;
                    case 2:
                        ch0[i] += ch1[i];
                        break;
                    default:
                        int mid = (ch0[i] << 1) | (ch1[i] & 1);
                        int side = ch1[i];
                        ch0[i] = (mid + side) >> 1;
                        ch1[i] = (mid - side) >> 1;
                        break;
                }
            }
        }

        static int Decorrelate256(byte assignment, int* a, int* b, int n)
        {
            int i = 0;
            int step = Vector256<int>.Count;
            switch (assignment)
            {
                case 1:
                    for (; i <= n - step; i += step)
                        (Vector256.Load(a + i) - Vector256.Load(b + i)).Store(b + i);          // ch1: side → right
                    return i;
                case 2:
                    for (; i <= n - step; i += step)
                        (Vector256.Load(a + i) + Vector256.Load(b + i)).Store(a + i);          // ch0: side → left
                    return i;
                default:
                    var one = Vector256.Create(1);
                    for (; i <= n - step; i += step)
                    {
                        var x = Vector256.Load(a + i);
                        var y = Vector256.Load(b + i);
                        var mid = Vector256.ShiftLeft(x, 1) | (y & one);
                        Vector256.ShiftRightArithmetic(mid + y, 1).Store(a + i);
                        Vector256.ShiftRightArithmetic(mid - y, 1).Store(b + i);
                    }
                    return i;
            }
        }

        static int Decorrelate128(byte assignment, int* a, int* b, int n)
        {
            int i = 0;
            int step = Vector128<int>.Count;
            switch (assignment)
            {
                case 1:
                    for (; i <= n - step; i += step)
                        (Vector128.Load(a + i) - Vector128.Load(b + i)).Store(b + i);
                    return i;
                case 2:
                    for (; i <= n - step; i += step)
                        (Vector128.Load(a + i) + Vector128.Load(b + i)).Store(a + i);
                    return i;
                default:
                    var one = Vector128.Create(1);
                    for (; i <= n - step; i += step)
                    {
                        var x = Vector128.Load(a + i);
                        var y = Vector128.Load(b + i);
                        var mid = Vector128.ShiftLeft(x, 1) | (y & one);
                        Vector128.ShiftRightArithmetic(mid + y, 1).Store(a + i);
                        Vector128.ShiftRightArithmetic(mid - y, 1).Store(b + i);
                    }
                    return i;
            }
        }

        /// <summary>§9.2.2's "add k least-significant zero bits", over a whole channel run.</summary>
        public static void ShiftLeft(Span<int> s, int bits)
        {
            // Same C# shift-masking hazard as `ReadUnary`, and here the vector and scalar paths would not even
            // disagree the same way: 32 wasted bits on a 33-bit side channel shifts every significant bit out, so
            // the only value an `int` can hold is zero. Unreachable for a real file; deterministic if one appears.
            if (bits >= 32) { s.Clear(); return; }
            int n = s.Length;
            int i = 0;
            if (n >= 16 && !ForceScalar && (Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated))
            {
                fixed (int* a = s)
                {
                    if (Vector256.IsHardwareAccelerated)
                        for (int step = Vector256<int>.Count; i <= n - step; i += step)
                            Vector256.ShiftLeft(Vector256.Load(a + i), bits).Store(a + i);
                    else
                        for (int step = Vector128<int>.Count; i <= n - step; i += step)
                            Vector128.ShiftLeft(Vector128.Load(a + i), bits).Store(a + i);
                }
            }
            for (; i < n; i++) s[i] <<= bits;
        }

        /// <summary>Planar int → interleaved stereo float, scaled by 1 / 2^(bps−1) (24-bit divides by 8,388,608 —
        /// the constant go-librespot corrected to in v0.8.0) times the caller's LINEAR gain (normalization, applied
        /// here so the engine's ReplayGain stays unity, as 0.2.9 did).
        /// <para>ONE scale for the whole stream, which makes the map ASYMMETRIC because two's complement is:
        /// −2^(bps−1) becomes exactly −1.0, and the largest positive sample, 2^(bps−1)−1, is one step short at
        /// 1 − 2^(1−bps) (127/128 at 8 bits, 0.99999988 at 24). That is what libFLAC, Symphonia and go-librespot
        /// do. Stretching the positive half to reach +1.0 would mean two scales in one stream and would move every
        /// sample; nothing clips as it is, because ±1.0 is what the graph calls full scale.</para>
        /// <para>The convert-and-scale is elementwise and vectorises; the interleave is a TRANSPOSE and is done with
        /// the ISA's own unpack (see <see cref="Interleave256"/>), not with eight lane extracts. Every path
        /// multiplies in the same order — <c>sample × scale</c> — so the vector and the scalar tail agree to the
        /// bit, not to a tolerance.</para></summary>
        public static void ToFloat(ReadOnlySpan<int> ch0, ReadOnlySpan<int> ch1, int bps, float gain, Span<float> interleaved)
        {
            float scale = gain / (float)(1L << (bps - 1));
            int n = ch0.Length < ch1.Length ? ch0.Length : ch1.Length;
            if (interleaved.Length < n * 2) n = interleaved.Length / 2;
            if (n <= 0) return;
            int i = 0;
            fixed (int* a = ch0)
            fixed (int* b = ch1)
            fixed (float* o = interleaved)
            {
                if (n >= 16 && !ForceScalar)
                {
                    if (Vector256.IsHardwareAccelerated)
                    {
                        var k = Vector256.Create(scale);
                        for (int step = Vector256<int>.Count; i <= n - step; i += step)
                            Interleave256(Vector256.ConvertToSingle(Vector256.Load(a + i)) * k,
                                          Vector256.ConvertToSingle(Vector256.Load(b + i)) * k, o + (i << 1));
                    }
                    else if (Vector128.IsHardwareAccelerated)
                    {
                        var k = Vector128.Create(scale);
                        for (int step = Vector128<int>.Count; i <= n - step; i += step)
                            Interleave128(Vector128.ConvertToSingle(Vector128.Load(a + i)) * k,
                                          Vector128.ConvertToSingle(Vector128.Load(b + i)) * k, o + (i << 1));
                    }
                }
                for (; i < n; i++)
                {
                    o[i << 1] = a[i] * scale;
                    o[(i << 1) + 1] = b[i] * scale;
                }
            }
        }

        /// <summary>Mono, and 3..8-channel files (local only; Spotify serves stereo). A mono source is duplicated; a
        /// multichannel source is downmixed to L/R in the RFC §9.1.3 channel order (FL FR FC LFE BL BR SL SR) with
        /// the usual −3 dB centre and surround weights. We play a stereo device, so a downmix is the honest answer
        /// for a file the user dropped — 0.2.9's FlacBox path interleaved N channels into a stereo buffer.
        /// <para>Same three paths as <see cref="ToFloat"/>. The weights multiply in the scalar path's order —
        /// <c>(sample × scale) × k</c>, never <c>sample × (scale × k)</c> — because float multiplication is not
        /// associative and the two would differ by a ULP on some samples; the equivalence fact would then be a
        /// tolerance instead of an equality, and a tolerance is not a proof.</para></summary>
        public static void ToFloatMulti(ReadOnlySpan<int> planar, int channels, int block, int bps, float gain, Span<float> interleaved)
        {
            float scale = gain / (float)(1L << (bps - 1));
            const float k = 0.7071f;
            if (interleaved.Length < block * 2) block = interleaved.Length / 2;
            if (block <= 0 || channels <= 0 || planar.Length < channels * block) return;
            // Which planes the downmix reads, resolved once: centre, then the two surrounds, then the two sides.
            int c3 = channels >= 3 ? 2 : -1;
            int c5L = channels >= 5 ? (channels == 5 ? 3 : 4) : -1;
            int c5R = channels >= 5 ? (channels == 5 ? 4 : 5) : -1;
            int c7L = channels >= 7 ? channels - 2 : -1;
            int c7R = channels >= 7 ? channels - 1 : -1;
            int i = 0;
            fixed (int* p = planar)
            fixed (float* o = interleaved)
            {
                if (block >= 16 && !ForceScalar && Vector256.IsHardwareAccelerated)
                {
                    var kv = Vector256.Create(scale);
                    var kw = Vector256.Create(k);
                    for (int step = Vector256<int>.Count; i <= block - step; i += step)
                    {
                        var l = Vector256.ConvertToSingle(Vector256.Load(p + i)) * kv;
                        var r = l;
                        if (channels > 1)
                        {
                            r = Vector256.ConvertToSingle(Vector256.Load(p + block + i)) * kv;
                            if (c3 >= 0)
                            {
                                var cv = Vector256.ConvertToSingle(Vector256.Load(p + c3 * block + i)) * kv * kw;
                                l += cv;
                                r += cv;
                            }
                            if (c5L >= 0)
                            {
                                l += Vector256.ConvertToSingle(Vector256.Load(p + c5L * block + i)) * kv * kw;
                                r += Vector256.ConvertToSingle(Vector256.Load(p + c5R * block + i)) * kv * kw;
                            }
                            if (c7L >= 0)
                            {
                                l += Vector256.ConvertToSingle(Vector256.Load(p + c7L * block + i)) * kv * kw;
                                r += Vector256.ConvertToSingle(Vector256.Load(p + c7R * block + i)) * kv * kw;
                            }
                        }
                        Interleave256(l, r, o + (i << 1));
                    }
                }
                else if (block >= 16 && !ForceScalar && Vector128.IsHardwareAccelerated)
                {
                    var kv = Vector128.Create(scale);
                    var kw = Vector128.Create(k);
                    for (int step = Vector128<int>.Count; i <= block - step; i += step)
                    {
                        var l = Vector128.ConvertToSingle(Vector128.Load(p + i)) * kv;
                        var r = l;
                        if (channels > 1)
                        {
                            r = Vector128.ConvertToSingle(Vector128.Load(p + block + i)) * kv;
                            if (c3 >= 0)
                            {
                                var cv = Vector128.ConvertToSingle(Vector128.Load(p + c3 * block + i)) * kv * kw;
                                l += cv;
                                r += cv;
                            }
                            if (c5L >= 0)
                            {
                                l += Vector128.ConvertToSingle(Vector128.Load(p + c5L * block + i)) * kv * kw;
                                r += Vector128.ConvertToSingle(Vector128.Load(p + c5R * block + i)) * kv * kw;
                            }
                            if (c7L >= 0)
                            {
                                l += Vector128.ConvertToSingle(Vector128.Load(p + c7L * block + i)) * kv * kw;
                                r += Vector128.ConvertToSingle(Vector128.Load(p + c7R * block + i)) * kv * kw;
                            }
                        }
                        Interleave128(l, r, o + (i << 1));
                    }
                }

                for (; i < block; i++)
                {
                    float l, r;
                    if (channels == 1)
                    {
                        l = r = p[i] * scale;
                    }
                    else
                    {
                        l = p[i] * scale;
                        r = p[block + i] * scale;
                        if (c3 >= 0) { float c = p[c3 * block + i] * scale * k; l += c; r += c; }
                        if (c5L >= 0)
                        {
                            l += p[c5L * block + i] * scale * k;
                            r += p[c5R * block + i] * scale * k;
                        }
                        if (c7L >= 0)
                        {
                            l += p[c7L * block + i] * scale * k;
                            r += p[c7R * block + i] * scale * k;
                        }
                    }
                    o[i << 1] = l;
                    o[(i << 1) + 1] = r;
                }
            }
        }

        /// <summary>Store eight L and eight R lanes as sixteen interleaved floats. AVX: two unpacks and two
        /// cross-lane permutes — the classic 2×8 AoS transpose, because AVX's unpack works per 128-bit lane and the
        /// permute is what puts the lanes back in sample order. Anything else: two 128-bit interleaves.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Interleave256(Vector256<float> l, Vector256<float> r, float* dst)
        {
            if (Avx.IsSupported)
            {
                Vector256<float> t0 = Avx.UnpackLow(l, r);       // l0 r0 l1 r1 | l4 r4 l5 r5
                Vector256<float> t1 = Avx.UnpackHigh(l, r);      // l2 r2 l3 r3 | l6 r6 l7 r7
                Avx.Store(dst, Avx.Permute2x128(t0, t1, 0x20));
                Avx.Store(dst + 8, Avx.Permute2x128(t0, t1, 0x31));
                return;
            }
            Interleave128(l.GetLower(), r.GetLower(), dst);
            Interleave128(l.GetUpper(), r.GetUpper(), dst + 8);
        }

        /// <summary>Store four L and four R lanes as eight interleaved floats: SSE's UNPCKLPS/UNPCKHPS, NEON's
        /// ZIP1/ZIP2, and a lane-wise store where there is neither (the convert and the multiply were still one
        /// instruction each for four samples).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Interleave128(Vector128<float> l, Vector128<float> r, float* dst)
        {
            if (Sse.IsSupported)
            {
                Sse.Store(dst, Sse.UnpackLow(l, r));
                Sse.Store(dst + 4, Sse.UnpackHigh(l, r));
                return;
            }
            if (AdvSimd.Arm64.IsSupported)
            {
                AdvSimd.Store(dst, AdvSimd.Arm64.ZipLow(l, r));
                AdvSimd.Store(dst + 4, AdvSimd.Arm64.ZipHigh(l, r));
                return;
            }
            dst[0] = l[0]; dst[1] = r[0];
            dst[2] = l[1]; dst[3] = r[1];
            dst[4] = l[2]; dst[5] = r[2];
            dst[6] = l[3]; dst[7] = r[3];
        }
    }
}
