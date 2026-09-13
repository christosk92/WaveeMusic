// ── Playback/Playback.Audio.Vorbis.cs ──────────────────────────────────────────────────────────────────────────────
// The Vorbis I decoder, from scratch: the LSB-first 64-bit bit reader, the three header packets, codebooks with a
// 10-bit fast-Huffman table, floor 1 (and floor 0), residues 0/1/2, the stb-structured inverse MDCT, window +
// overlap-add, channel coupling and the gain-folded interleave
//
// Role: CORE
// Owner: V
// Wave: 3-parallel (pure over spans, no wave dependency)
// Budget: 1600 lines (over by ~480, a third of it the 256-literal dB table and doc comments: floor 0 and residues
//         0/1 are here in full because a dropped local .ogg can be anything; the setup parser validates every index
//         it stores so the packet loops run without a single bounds check; and the scalar twins of the vector
//         kernels live beside them so `ForceScalar` is a fact)
// Spec: docs/plans/wavee/wavee-0.3-vorbis-implementation.md §3 + Vorbis I (xiph.org/vorbis/doc/Vorbis_I_spec.html)
//
// WHAT THIS FILE IS. One packet in, interleaved stereo `float` frames out. No `Stream`, no `IMediaByteSource`, no
// engine type, no thread, no clock: the Ogg layer (`Playback.Audio.Ogg.cs`) hands `Decoder.DecodePacket` a
// `ReadOnlySpan<byte>` and the decoder writes into a buffer it owns and allocated once, in `Open`. Every buffer a
// hot loop touches is on the pinned object heap and addressed through a pointer taken once; the packet path
// allocates NOTHING (the fact `Decoding_two_hundred_packets_allocates_nothing` pins).
//
// THE SAMPLE CONVENTION IS THE SPEC'S, NOT STB'S. A packet returns blocksize(prev)/4 + blocksize(cur)/4 frames
// (Vorbis I §1.3.2), i.e. the samples between the CENTRES of two consecutive blocks — the convention libvorbis uses
// and the one a page's granule position counts in. stb returns [left_start, right_start) instead, which tiles the
// same timeline but attributes up to (n1 − n0)/4 samples to the wrong packet around every long→short transition
// and resyncs at page granules (stb :3413-3441). A decoder whose packet boundaries disagree with the granules
// cannot seek sample-exactly, so this one holds back [n/2, right_start) of every block and emits it at the head
// of the next packet (§8 of this file). `PeekFrames` is then exact by construction.
//
// Rules this file is written under (P1-P16 / C1-C10, relationships_wavee.md §5.11):
//   P8   ZERO allocation after `Decoder.Open`. Every array — codebook arenas, VQ tables, IMDCT twiddles, windows,
//        per-channel block buffers, the output — is sized from the setup header and allocated grow-only on the
//        pinned object heap; a second Open with a setup no larger allocates nothing. The table is in `Open`.
//   P9   No LINQ, no closures, no async, no boxing, no exceptions on the packet path. A malformed packet is a
//        `PacketResult`; reading past the end yields zeros and sets `BitReader.Overrun` (stb `get_bits` past EOP).
//   P15  SIMD where elements are independent: the IMDCT's step-3 butterflies are `Vector128` (the algorithm's unit
//        is four floats and NEON has no 256-bit register); the streaming kernels — overlap-add, uncoupling, the
//        floor's constant tail and interleave+gain — are `Vector256` when accelerated, `Vector128` always, scalar
//        tail. Every path performs the same float operations in the same order, so vector == scalar to the BIT.
//        `ForceScalar` turns them off for the equivalence facts; it is a plain static, never set by the app.
//   C2   Synchronous. The only state across packets is the saved right half of the previous block; `Prime()`
//        forgets it, and the next packet decodes, saves and returns 0 frames (spec §4.3.8 "prime the engine").
//
// The reference for anything the spec leaves implicit is stb_vorbis.c v1.22 (`C:\WAVEE\stb`), transliterated where
// the plan says so (codeword construction :1086-1130, fast Huffman :1134-1154/:1717-1729, the IMDCT :2408-2929,
// draw_line/do_floor :2022-2081/:3072-3108, decode_initial :3124-3180), with libvorbis for the single-entry
// codebook (sharedbook.c:135, :524) and the granule convention (block.c:731-944).

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The Vorbis I codec, as pure functions over pointers plus one class that owns the decode buffers. See
    /// the file header for the rules; see the plan §3 for the derivation.</summary>
    public static unsafe class Vorbis
    {
        // ── 1. Constants, the bit reader, the spec's helper functions ───────────────────────────────────────────────

        public const int FastBits = 10;                       // stb STB_VORBIS_FAST_HUFFMAN_LENGTH default
        public const int FastSize = 1 << FastBits;
        public const uint Miss = 0xFFFF_FFFFu;
        public const int MaxCodeLen = 32;
        /// <summary>Floor 1 posts: 2 + 31 partitions × 8 dimensions.</summary>
        public const int MaxPosts = 2 + 31 * 8;
        /// <summary>Floor 0 per-channel scratch: amplitude + ≤ 255 coefficients + their cosines.</summary>
        public const int Floor0Stride = 1 + 2 * 255 + 1;
        /// <summary>The decoder always hands out interleaved STEREO: mono is duplicated, 3..8 channels downmixed.</summary>
        public const int OutputChannels = 2;

        public enum PacketResult : byte { Ok, NotAudio, BadMode, Truncated, NotOpen }

        /// <summary>Forces every vector kernel in this file down its scalar path, so a test can assert the two agree
        /// bit for bit. A plain static; never written by the app (no environment switch, no build flag).</summary>
        public static bool ForceScalar;

        /// <summary>LSB-first bit reader over a packet (Vorbis I §2). 64-bit right-aligned cache refilled with one
        /// unaligned 8-byte load (Giesen, "reading bits in far too many ways" part 2, variant 4): the load is OR'd in
        /// above the valid bits and the pointer advances by <c>(63 − bits) / 8</c>, so the bits above the count are
        /// always exactly the stream bits the next load re-reads — the OR is idempotent. ≥ 56 valid bits after every
        /// refill while 8 bytes remain; the packet's last 7 bytes go byte by byte. Never throws: past the end it
        /// yields zeros and sets <see cref="Overrun"/>.</summary>
        public ref struct BitReader
        {
            readonly byte* _end;          // one past the packet's last byte
            byte* _p;                     // next byte to load into the cache
            ulong _cache;                 // bit 0 = the next bit to read
            int _bits;                    // valid bits in _cache
            public bool Overrun;

            public BitReader(byte* packet, int length)
            {
                _p = packet;
                _end = packet + (length < 0 ? 0 : length);
                _cache = 0;
                _bits = 0;
                Overrun = false;
            }

            public readonly int Bits => _bits;

            /// <summary>Top the cache up to ≥ 56 bits. One load while 8 bytes remain; byte-wise on the tail.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Refill()
            {
                if (_end - _p >= 8)
                {
                    _cache |= Unsafe.ReadUnaligned<ulong>(_p) << _bits;
                    _p += (63 - _bits) >> 3;
                    _bits |= 56;
                    return;
                }
                while (_bits <= 56 && _p < _end) { _cache |= (ulong)*_p++ << _bits; _bits += 8; }
            }

            /// <summary>The next <paramref name="n"/> ≤ 32 bits without consuming them (zeros past the end).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly uint Peek(int n) => (uint)(_cache & ((1UL << n) - 1UL));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Consume(int n)
            {
                _cache >>= n;
                _bits -= n;
                if (_bits < 0) { Overrun = true; _bits = 0; _cache = 0; }
            }

            /// <summary>Read 0..32 bits (§2 "read n bits"). n == 0 is legal and returns 0.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint Read(int n)
            {
                if (n <= 0) return 0;
                if (_bits < n) Refill();
                uint v = (uint)(_cache & ((1UL << n) - 1UL));
                Consume(n);
                return v;
            }

            public bool ReadBit() => Read(1) != 0;
        }

        /// <summary>§9.2.1 ilog: the bits needed to represent x (ilog(0) = 0, ilog(7) = 3, ilog(8) = 4).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ILog(int x) => x <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)x);

        /// <summary>§9.2.2 float32_unpack. Setup only.</summary>
        public static float Float32Unpack(uint x)
        {
            uint mantissa = x & 0x1F_FFFF;
            int exponent = (int)((x & 0x7FE0_0000) >> 21);
            double m = (x & 0x8000_0000) != 0 ? -(double)mantissa : mantissa;
            return (float)Math.ScaleB(m, exponent - 788);
        }

        /// <summary>§9.2.3 lookup1_values: the largest r with r^dims ≤ entries. Integer, overflow-safe.</summary>
        public static int Lookup1Values(int entries, int dims)
        {
            if (entries <= 0 || dims <= 0) return 0;
            int r = (int)Math.Floor(Math.Pow(entries, 1.0 / dims));
            while (PowLe(r + 1, dims, entries)) r++;
            while (r > 0 && !PowLe(r, dims, entries)) r--;
            return r;
        }

        static bool PowLe(long b, int e, long limit)
        {
            long acc = 1;
            for (int i = 0; i < e; i++)
            {
                acc *= b;
                if (acc > limit) return false;
            }
            return true;
        }

        // ── 2. The header packets (§4.2) ────────────────────────────────────────────────────────────────────────────

        /// <summary>1, 3 or 5 when <paramref name="packet"/> starts with a Vorbis header type byte and "vorbis"; −1
        /// otherwise (an audio packet has bit 0 clear, so it is never mistaken for one).</summary>
        public static int HeaderType(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 7) return -1;
            byte t = packet[0];
            if (t != 1 && t != 3 && t != 5) return -1;
            return packet.Slice(1, 6).SequenceEqual("vorbis"u8) ? t : -1;
        }

        /// <summary>The identification header (§4.2.2).</summary>
        public struct Identification
        {
            public int Channels, SampleRate, BitrateMaximum, BitrateNominal, BitrateMinimum, BlockSize0, BlockSize1;
        }

        public static bool TryParseIdentification(ReadOnlySpan<byte> p, out Identification id)
        {
            id = default;
            if (p.Length < 30 || HeaderType(p) != 1) return false;
            if (ReadU32(p, 7) != 0) return false;                                    // vorbis_version
            id.Channels = p[11];
            id.SampleRate = (int)ReadU32(p, 12);
            id.BitrateMaximum = (int)ReadU32(p, 16);
            id.BitrateNominal = (int)ReadU32(p, 20);
            id.BitrateMinimum = (int)ReadU32(p, 24);
            int e0 = p[28] & 0x0F, e1 = p[28] >> 4;
            if (id.Channels == 0 || id.SampleRate <= 0) return false;
            if (e0 < 6 || e0 > 13 || e1 < 6 || e1 > 13 || e0 > e1) return false;
            if ((p[29] & 1) == 0) return false;                                      // framing bit
            id.BlockSize0 = 1 << e0;
            id.BlockSize1 = 1 << e1;
            return true;
        }

        static uint ReadU32(ReadOnlySpan<byte> p, int at)
            => (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));

        /// <summary>An allocation-free walk over the comment header (§5): the vendor string, then each
        /// <c>FIELD=value</c> entry as UTF-8 bytes. Parsing the fields is <c>Playback.VorbisComment</c>'s (U's) job.</summary>
        public ref struct CommentReader
        {
            readonly ReadOnlySpan<byte> _p;
            int _pos, _left;
            public ReadOnlySpan<byte> Vendor;
            public int Count;

            CommentReader(ReadOnlySpan<byte> p) { _p = p; _pos = 0; _left = 0; Vendor = default; Count = 0; }

            public static bool TryCreate(ReadOnlySpan<byte> packet, out CommentReader reader)
            {
                reader = new CommentReader(packet);
                if (HeaderType(packet) != 3 || packet.Length < 15) return false;
                long vlen = ReadU32(packet, 7);
                if (11 + vlen + 4 > packet.Length) return false;
                reader.Vendor = packet.Slice(11, (int)vlen);
                int at = 11 + (int)vlen;
                reader.Count = (int)Math.Min(ReadU32(packet, at), int.MaxValue);
                reader._pos = at + 4;
                reader._left = reader.Count;
                return true;
            }

            public bool TryNext(out ReadOnlySpan<byte> comment)
            {
                comment = default;
                if (_left <= 0 || _pos + 4 > _p.Length) return false;
                long len = ReadU32(_p, _pos);
                if (_pos + 4 + len > _p.Length) { _left = 0; return false; }
                comment = _p.Slice(_pos + 4, (int)len);
                _pos += 4 + (int)len;
                _left--;
                return true;
            }
        }

        // ── 3. The setup structures (§4.2.4) — flat, unmanaged, addressed by pointer ────────────────────────────────

        /// <summary>One codebook (§3.2): offsets into the decoder's arenas.</summary>
        public struct Book
        {
            public int Dims, Entries;
            /// <summary>Into the fast arena: <see cref="FastSize"/> entries of <c>len &lt;&lt; 24 | entry</c>.</summary>
            public int FastOff;
            /// <summary>Into the sorted arenas: the codes longer than <see cref="FastBits"/>, left-aligned in MaxLen.</summary>
            public int SortedOff, SortedCount, MaxLen;
            /// <summary>Into the VQ arena: Entries × Dims floats, or −1 (lookup type 0).</summary>
            public int VqOff;
            public byte LookupType;
        }

        /// <summary>A floor, type 0 or 1 (§6.2.1, §7.2.2). The fixed buffers make it one flat ~1.4 KB value.</summary>
        public struct Floor
        {
            public byte Type;
            // floor 1
            public int Partitions, Multiplier, RangeBits, Values;
            /// <summary>{256, 128, 86, 64}[Multiplier − 1] and ilog(Range − 1), set by <see cref="PrepareFloor1"/> so
            /// the packet loop reads two fields instead of a table (a collection-expression span property allocated
            /// 72 bytes per access on the Debug JIT — 288 bytes a packet).</summary>
            public int Range, YBits;
            public fixed byte PartitionClass[31];
            public fixed byte ClassDims[16];
            public fixed byte ClassSubclasses[16];
            public fixed byte ClassMasterbook[16];
            public fixed short SubclassBooks[16 * 8];          // −1 = none
            public fixed ushort X[MaxPosts];
            public fixed byte Sorted[MaxPosts];                // post indices in ascending X
            public fixed byte LowNeighbor[MaxPosts];
            public fixed byte HighNeighbor[MaxPosts];
            // floor 0
            public int Order, Rate, BarkMapSize, AmplitudeBits, AmplitudeOffset, BookCount;
            public fixed byte BookList[16];
            public int BarkOff0, BarkOff1;                     // into the bark arenas: n0/2 and n1/2 entries
        }

        /// <summary>A residue (§8.6.1).</summary>
        public struct Residue
        {
            public byte Type;
            public int Begin, End, PartitionSize, Classifications, Classbook;
            /// <summary>Into the class-map arena: classbook.Entries × classbook.Dims partition classes.</summary>
            public int ClassMapOff;
            public fixed byte Cascade[64];
            public fixed short Books[64 * 8];                  // −1 = none
        }

        /// <summary>A mapping (§4.2.4 mapping type 0).</summary>
        public struct Mapping
        {
            public int Submaps, CouplingSteps;
            public fixed byte Magnitude[256];
            public fixed byte Angle[256];
            public fixed byte Mux[256];
            public fixed byte SubmapFloor[16];
            public fixed byte SubmapResidue[16];
        }

        public struct Mode
        {
            public byte BlockFlag;
            public byte Mapping;
        }

        // ── 4. Codebooks: canonical codewords, the fast table, the sorted fallback ──────────────────────────────────

        /// <summary>Canonical Huffman assignment (§3.2.1; stb <c>compute_codewords</c> :1086-1130): each used entry,
        /// in order, takes the lowest unused codeword of its length. <paramref name="codes"/>[i] is the MSB-first
        /// (spec-order) codeword, right-aligned; 0 for unused entries. False on an over-subscribed length set; an
        /// under-populated one is legal (libvorbis rejects only over-population plus the single-entry retcon).</summary>
        [SkipLocalsInit]
        public static bool BuildCodewords(ReadOnlySpan<sbyte> len, Span<uint> codes)
        {
            Span<uint> available = stackalloc uint[MaxCodeLen + 1];
            available.Clear();
            int n = len.Length, k = 0;
            while (k < n && len[k] <= 0) { codes[k] = 0; k++; }
            if (k == n) return true;                                   // no used entries: a legal, unused book
            if (len[k] > MaxCodeLen) return false;
            codes[k] = 0;
            for (int i = 1; i <= len[k]; i++) available[i] = 1u << (32 - i);
            for (int i = k + 1; i < n; i++)
            {
                int z = len[i];
                if (z <= 0) { codes[i] = 0; continue; }
                if (z > MaxCodeLen) return false;
                while (z > 0 && available[z] == 0) z--;
                if (z == 0) return false;                              // over-subscribed
                uint res = available[z];
                available[z] = 0;
                codes[i] = res >> (32 - len[i]);
                for (int y = len[i]; y > z; y--) available[y] = res + (1u << (32 - y));
            }
            return true;
        }

        /// <summary>Reverse the low <paramref name="len"/> bits of <paramref name="x"/> — a spec-order codeword as it
        /// appears in the LSB-first cache, or back.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReverseBits(uint x, int len)
        {
            x = ((x & 0x5555_5555) << 1) | ((x >> 1) & 0x5555_5555);
            x = ((x & 0x3333_3333) << 2) | ((x >> 2) & 0x3333_3333);
            x = ((x & 0x0F0F_0F0F) << 4) | ((x >> 4) & 0x0F0F_0F0F);
            x = ((x & 0x00FF_00FF) << 8) | ((x >> 8) & 0x00FF_00FF);
            x = (x << 16) | (x >> 16);
            return len <= 0 ? 0 : x >> (32 - len);
        }

        /// <summary>Fill one book's fast table (stb <c>compute_accelerated_huffman</c> :1134-1154): every codeword of
        /// length ≤ FastBits is bit-reversed and written at every index that has it as a prefix, stepping by
        /// <c>1 &lt;&lt; len</c>. Longer codewords go to the sorted table, left-aligned into MaxLen bits and packed
        /// with their length (<c>len &lt;&lt; 24 | entry</c>). A book with ONE used entry of length 1 decodes both
        /// one-bit codes to it (errata 20150226; libvorbis sharedbook.c:524, Symphonia codebook.rs:314).</summary>
        public static void BuildTables(ReadOnlySpan<sbyte> len, ReadOnlySpan<uint> codes, Span<uint> fast,
                                       Span<uint> sorted, Span<int> sortedEntry, out int sortedCount, out int maxLen)
        {
            fast.Fill(Miss);
            sortedCount = 0;
            maxLen = 0;
            int used = 0, last = -1;
            for (int i = 0; i < len.Length; i++)
            {
                if (len[i] <= 0) continue;
                used++;
                last = i;
                if (len[i] > maxLen) maxLen = len[i];
            }
            if (used == 1 && len[last] == 1)
            {
                fast.Fill((1u << 24) | (uint)last);
                return;
            }
            for (int i = 0; i < len.Length; i++)
            {
                int l = len[i];
                if (l <= 0) continue;
                if (l <= FastBits)
                {
                    uint packed = ((uint)l << 24) | (uint)i;
                    for (uint z = ReverseBits(codes[i], l); z < FastSize; z += 1u << l) fast[(int)z] = packed;
                }
                else
                {
                    sorted[sortedCount] = codes[i] << (maxLen - l);
                    sortedEntry[sortedCount++] = (l << 24) | i;
                }
            }
            // insertion sort by key — the long codes are the short tail of a book
            for (int i = 1; i < sortedCount; i++)
            {
                uint key = sorted[i];
                int e = sortedEntry[i], j = i - 1;
                while (j >= 0 && sorted[j] > key) { sorted[j + 1] = sorted[j]; sortedEntry[j + 1] = sortedEntry[j]; j--; }
                sorted[j + 1] = key;
                sortedEntry[j + 1] = e;
            }
        }

        /// <summary>Decode one codeword (stb <c>DECODE_RAW</c> :1717-1729). Fast path: the next FastBits of the cache
        /// index the table — one load. Returns −1 at end of packet or on an invalid code. No bounds check:
        /// <c>Peek(FastBits) &lt; FastSize</c> and the book's table is FastSize long by construction.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecodeScalar(ref BitReader r, Book* b, uint* fast, uint* sorted, int* sortedEntry)
        {
            if (r.Bits < 24) r.Refill();
            uint e = fast[b->FastOff + (int)r.Peek(FastBits)];
            if (e != Miss)
            {
                int l = (int)(e >> 24);
                if (l > r.Bits) { r.Overrun = true; return -1; }
                r.Consume(l);
                return (int)(e & 0xFF_FFFF);
            }
            return DecodeSlow(ref r, b, sorted, sortedEntry);
        }

        /// <summary>A codeword longer than FastBits: reverse the next MaxLen bits into spec order and binary-search the
        /// left-aligned sorted codewords for the largest key ≤ it; prefix-freedom makes that the match (libvorbis
        /// <c>decode_packed_entry_number</c>, codebook.c:354-367). The prefix is re-checked, so garbage is −1.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int DecodeSlow(ref BitReader r, Book* b, uint* sorted, int* sortedEntry)
        {
            int ml = b->MaxLen;
            if (b->SortedCount == 0 || ml <= FastBits) { r.Overrun = true; return -1; }
            if (r.Bits < ml) r.Refill();
            uint key = ReverseBits(r.Peek(ml), ml);
            int lo = b->SortedOff, hi = b->SortedOff + b->SortedCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= key) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found < 0) { r.Overrun = true; return -1; }
            int packed = sortedEntry[found];
            int len = (int)((uint)packed >> 24);
            int shift = ml - len;
            if ((key >> shift) != (sorted[found] >> shift) || len > r.Bits) { r.Overrun = true; return -1; }
            r.Consume(len);
            return packed & 0xFF_FFFF;
        }

        // ── 5. Floors ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>§7.2.3. Fills <paramref name="y"/>[0..Values). False when the floor is unused — the "nonzero" bit is
        /// 0, OR the packet ended inside the floor (spec: "as if the nonzero flag had been unset"; stb :3263).</summary>
        public static bool DecodeFloor1Posts(ref BitReader r, Floor* f, Book* books, uint* fast, uint* sorted, int* sortedEntry,
                                      int* y)
        {
            if (r.Read(1) == 0) return false;
            int bits = f->YBits;
            y[0] = (int)r.Read(bits);
            y[1] = (int)r.Read(bits);
            int offset = 2;
            for (int i = 0; i < f->Partitions; i++)
            {
                int cls = f->PartitionClass[i];
                int cdim = f->ClassDims[cls];
                int cbits = f->ClassSubclasses[cls];
                int csub = (1 << cbits) - 1;
                int cval = 0;
                if (cbits > 0)
                {
                    cval = DecodeScalar(ref r, books + f->ClassMasterbook[cls], fast, sorted, sortedEntry);
                    if (cval < 0) return false;
                }
                for (int j = 0; j < cdim; j++)
                {
                    int book = f->SubclassBooks[cls * 8 + (cval & csub)];
                    cval >>= cbits;
                    if (book >= 0)
                    {
                        int v = DecodeScalar(ref r, books + book, fast, sorted, sortedEntry);
                        if (v < 0) return false;
                        y[offset + j] = v;
                    }
                    else y[offset + j] = 0;
                }
                offset += cdim;
            }
            return !r.Overrun;
        }

        /// <summary>§7.2.4 render_point: the predicted amplitude at x on the line (x0,y0)-(x1,y1), integer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int RenderPoint(int x0, int y0, int x1, int y1, int x)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = dy < 0 ? -dy : dy;
            int off = ady * (x - x0) / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        /// <summary>§7.2.4 step 1: unwrap the posts into absolute amplitudes. Bit 15 of <paramref name="final"/>[i]
        /// is SET when the post is interpolated (step2_flag clear) and so does not end a segment.</summary>
        public static void UnwrapPosts(Floor* f, int* y, int* final)
        {
            int range = f->Range;
            final[0] = y[0];
            final[1] = y[1];
            for (int i = 2; i < f->Values; i++)
            {
                int lo = f->LowNeighbor[i], hi = f->HighNeighbor[i];
                int pred = RenderPoint(f->X[lo], final[lo] & 0x7FFF, f->X[hi], final[hi] & 0x7FFF, f->X[i]);
                int val = y[i];
                int highroom = range - pred, lowroom = pred;
                int room = (highroom < lowroom ? highroom : lowroom) << 1;
                if (val != 0)
                {
                    final[lo] &= 0x7FFF;                                                 // step2_flag[lo] = set
                    final[hi] &= 0x7FFF;
                    if (val >= room) final[i] = highroom > lowroom ? val - lowroom + pred : pred - val + highroom - 1;
                    else final[i] = (val & 1) != 0 ? pred - ((val + 1) >> 1) : pred + (val >> 1);
                    final[i] &= 0x7FFF;
                }
                else final[i] = (pred & 0x7FFF) | 0x8000;
            }
        }

        /// <summary>§7.2.4 step 2 fused with §4.3.6's dot product: walk the posts in X order, draw each segment with the
        /// integer line algorithm and MULTIPLY the dB-table value straight into the spectrum (stb <c>do_floor</c> with
        /// <c>LINE_OP = *=</c>, :3072-3108). One pass, no floor buffer.</summary>
        public static void RenderFloor1(Floor* f, int* final, float* spec, int n2, float* db)
        {
            int mult = f->Multiplier;
            int lx = 0, ly = (final[0] & 0x7FFF) * mult;
            for (int q = 1; q < f->Values; q++)
            {
                int j = f->Sorted[q];
                if ((final[j] & 0x8000) != 0) continue;
                int hy = (final[j] & 0x7FFF) * mult;
                int hx = f->X[j];
                if (lx != hx) DrawLine(spec, lx, ly, hx, hy, n2, db);
                lx = hx;
                ly = hy;
            }
            if (lx < n2) MultiplyConstant(spec + lx, n2 - lx, db[ly < 0 ? 0 : ly > 255 ? 255 : ly]);
        }

        /// <summary>§9.2.7 render_line (stb <c>draw_line</c> :2022-2081): the slope is taken from the UNCLAMPED end
        /// point, then the walk stops at <paramref name="n"/>. y is clamped to the table.</summary>
        static void DrawLine(float* spec, int x0, int y0, int x1, int y1, int n, float* db)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = dy < 0 ? -dy : dy;
            int bas = dy / adx, sy = dy < 0 ? bas - 1 : bas + 1;
            ady -= (bas < 0 ? -bas : bas) * adx;
            if (x1 > n) x1 = n;
            int x = x0, y = y0, err = 0;
            if (x >= x1) return;
            spec[x] *= db[y < 0 ? 0 : y > 255 ? 255 : y];
            for (++x; x < x1; ++x)
            {
                err += ady;
                if (err >= adx) { err -= adx; y += sy; } else y += bas;
                spec[x] *= db[y < 0 ? 0 : y > 255 ? 255 : y];
            }
        }

        /// <summary>§6.2.2 floor 0 packet decode: amplitude, book number, then VQ vectors accumulated into the LSP
        /// coefficients. <paramref name="coef"/>[0] = amplitude, [1..order] = coefficients. False when unused.</summary>
        static bool DecodeFloor0(ref BitReader r, Floor* f, Book* books, uint* fast, uint* sorted, int* sortedEntry,
                                 float* vq, float* coef)
        {
            uint amp = r.Read(f->AmplitudeBits);
            if (amp == 0 || r.Overrun) return false;
            int bn = (int)r.Read(ILog(f->BookCount));
            if (bn >= f->BookCount || r.Overrun) return false;
            Book* b = books + f->BookList[bn];
            if (b->VqOff < 0 || b->Dims <= 0) return false;
            coef[0] = amp;
            float last = 0;
            int i = 0, dims = b->Dims;
            while (i < f->Order)
            {
                int e = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                if (e < 0) return false;
                float* v = vq + b->VqOff + e * dims;
                for (int d = 0; d < dims && i < f->Order; d++, i++) coef[1 + i] = v[d] + last;
                last = coef[i];
            }
            return true;
        }

        /// <summary>§6.2.3 floor 0 curve, multiplied into the spectrum. The one place on the packet path that calls
        /// MathF (a cosine per coefficient and an exp per bark band): floor 0 has not been produced by libvorbis
        /// since 2002, stb refuses it, and it is here only so a dropped file of that age plays.</summary>
        static void RenderFloor0(Floor* f, float* coef, float* spec, int n2, int* map, float* cosw)
        {
            int order = f->Order;
            float* cc = coef + 1 + order;
            for (int j = 0; j < order; j++) cc[j] = MathF.Cos(coef[1 + j]);
            float amp = coef[0], ampOff = f->AmplitudeOffset;
            float ampMax = (float)((1UL << f->AmplitudeBits) - 1UL);
            int i = 0;
            while (i < n2)
            {
                int m = map[i];
                float w = cosw[i], p, q;
                if ((order & 1) != 0)
                {
                    p = 1f - w * w;
                    for (int j = 0; j <= (order - 3) / 2; j++) { float t = cc[2 * j + 1] - w; p *= 4f * t * t; }
                    q = 0.25f;
                    for (int j = 0; j <= (order - 1) / 2; j++) { float t = cc[2 * j] - w; q *= 4f * t * t; }
                }
                else
                {
                    p = 1f - w;
                    q = 1f + w;
                    for (int j = 0; j <= (order - 2) / 2; j++)
                    {
                        float tp = cc[2 * j + 1] - w, tq = cc[2 * j] - w;
                        p *= 4f * tp * tp;
                        q *= 4f * tq * tq;
                    }
                }
                float val = MathF.Exp(0.11512925f * (amp * ampOff / (ampMax * MathF.Sqrt(p + q)) - ampOff));
                do { spec[i] *= val; i++; } while (i < n2 && map[i] == m);
            }
        }

        /// <summary>§10.1 floor1_inverse_dB_table: the spec's 256 literals (stb :1946-2012, libvorbis floor1.c:280).
        /// Pinned so the floor loop addresses it by pointer.</summary>
        static readonly float[] s_floor1Db = BuildDbTable();

        /// <summary>The 256-entry table, for callers that render a floor outside a decoder (tests).</summary>
        public static ReadOnlySpan<float> Floor1InverseDb => s_floor1Db;

        /// <summary>Complete a floor 1 whose X list is filled: the X sort order and each post's low/high neighbour
        /// (§9.2.4-5; stb :4094-4122). False on a duplicate X (invalid, stb :4101). Setup only.</summary>
        public static bool PrepareFloor1(Floor* f)
        {
            int values = f->Values;
            if (values < 2 || values > MaxPosts || f->Multiplier < 1 || f->Multiplier > 4) return false;
            f->Range = f->Multiplier switch { 1 => 256, 2 => 128, 3 => 86, _ => 64 };
            f->YBits = ILog(f->Range - 1);
            for (int i = 0; i < values; i++) f->Sorted[i] = (byte)i;
            for (int i = 1; i < values; i++)
            {
                byte key = f->Sorted[i];
                int j = i - 1;
                while (j >= 0 && f->X[f->Sorted[j]] > f->X[key]) { f->Sorted[j + 1] = f->Sorted[j]; j--; }
                f->Sorted[j + 1] = key;
            }
            for (int i = 1; i < values; i++) if (f->X[f->Sorted[i]] == f->X[f->Sorted[i - 1]]) return false;
            for (int i = 2; i < values; i++)
            {
                int lo = 0, hi = 1, lx = -1, hx = 1 << 17, x = f->X[i];
                for (int j = 0; j < i; j++)
                {
                    int xj = f->X[j];
                    if (xj < x && xj > lx) { lx = xj; lo = j; }
                    if (xj > x && xj < hx) { hx = xj; hi = j; }
                }
                f->LowNeighbor[i] = (byte)lo;
                f->HighNeighbor[i] = (byte)hi;
            }
            return true;
        }

        static float[] BuildDbTable()
        {
            ReadOnlySpan<float> src =
            [
                1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f, 1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
                1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.1287530e-07f, 2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
                2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f, 3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
                4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f, 6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
                7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f, 1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
                1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f, 1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
                2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f, 2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
                3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f, 4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
                5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f, 7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
                9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f, 1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
                1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f, 2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
                2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f, 3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
                4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f, 5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
                7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f, 9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
                0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f, 0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
                0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f, 0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
                0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f, 0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
                0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f, 0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
                0.00092223983f, 0.00098217216f, 0.0010459992f, 0.0011139742f, 0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
                0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f, 0.0019632195f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
                0.0025254795f, 0.0026895994f, 0.0028643847f, 0.0030505286f, 0.0032487691f, 0.0034598925f, 0.0036847358f, 0.0039241906f,
                0.0041792066f, 0.0044507950f, 0.0047400328f, 0.0050480668f, 0.0053761186f, 0.0057254891f, 0.0060975636f, 0.0064938176f,
                0.0069158225f, 0.0073652516f, 0.0078438871f, 0.0083536271f, 0.0088964928f, 0.009474637f, 0.010090352f, 0.010746080f,
                0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f, 0.014722068f, 0.015678791f, 0.016697687f, 0.017782797f,
                0.018938423f, 0.020169149f, 0.021479854f, 0.022875735f, 0.024362330f, 0.025945531f, 0.027631618f, 0.029427276f,
                0.031339626f, 0.033376252f, 0.035545228f, 0.037855157f, 0.040315199f, 0.042935108f, 0.045725273f, 0.048696758f,
                0.051861348f, 0.055231591f, 0.058820850f, 0.062643361f, 0.066714279f, 0.071049749f, 0.075666962f, 0.080584227f,
                0.085821044f, 0.091398179f, 0.097337747f, 0.10366330f, 0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
                0.14201813f, 0.15124727f, 0.16107617f, 0.17154380f, 0.18269168f, 0.19456402f, 0.20720788f, 0.22067342f,
                0.23501402f, 0.25028656f, 0.26655159f, 0.28387361f, 0.30232132f, 0.32196786f, 0.34289114f, 0.36517414f,
                0.38890521f, 0.41417847f, 0.44109412f, 0.46975890f, 0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f,
                0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f, 0.82788260f, 0.88168307f, 0.9389798f, 1.0f,
            ];
            var t = GC.AllocateUninitializedArray<float>(256, pinned: true);
            src.CopyTo(t);
            return t;
        }

        // ── 6. Residues (§8.6.2) ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Type 2, all channels of a submap at once: the interleaved vector <c>v[j·ch + c]</c> is decoded
        /// straight into the channel spectra — no deinterleave pass (stb <c>codebook_decode_deinterleave_repeat</c>
        /// :1865-1933). <paramref name="spec"/> = ch pointers to n2 floats, already zeroed; VQ values are ADDED.
        /// Stops at end of packet: what was decoded stands. Bounds: <c>end ≤ ch × n2</c> is clamped here, the class
        /// map was built so every class &lt; Classifications ≤ 64, every VQ book was checked at Open.</summary>
        public static void DecodeResidue2(ref BitReader r, Residue* res, Book* books, uint* fast, uint* sorted, int* sortedEntry,
                                   float* vq, byte* classMap, float** spec, int ch, int n2, byte* partClass)
        {
            int size = ch * n2;
            int begin = res->Begin < size ? res->Begin : size;
            int end = res->End < size ? res->End : size;
            int psize = res->PartitionSize;
            int partitions = (end - begin) / psize;
            if (partitions <= 0) return;
            Book* classbook = books + res->Classbook;
            int cw = classbook->Dims;
            byte* map = classMap + res->ClassMapOff;
            for (int pass = 0; pass < 8; pass++)
            {
                int p = 0;
                while (p < partitions)
                {
                    if (pass == 0)
                    {
                        int e = DecodeScalar(ref r, classbook, fast, sorted, sortedEntry);
                        if (e < 0) return;
                        byte* m = map + e * cw;
                        for (int k = 0; k < cw; k++) partClass[p + k] = m[k];
                    }
                    int stop = p + cw < partitions ? p + cw : partitions;
                    for (; p < stop; p++)
                    {
                        int cls = partClass[p];
                        if ((res->Cascade[cls] & (1 << pass)) == 0) continue;
                        int bi = res->Books[cls * 8 + pass];
                        if (bi < 0) continue;
                        Book* b = books + bi;
                        float* table = vq + b->VqOff;
                        int dims = b->Dims;
                        int off = begin + p * psize, stopOff = off + psize;
                        if (ch == 2)
                        {
                            float* l = spec[0], rr = spec[1];
                            if ((dims & 1) == 0 && (off & 1) == 0 && psize % dims == 0)
                            {
                                // even dims from an even offset: every pair is (L, R) of one bin
                                int bin = off >> 1;
                                for (int q = psize / dims; q > 0; q--)
                                {
                                    int entry = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                                    if (entry < 0) return;
                                    float* v = table + entry * dims;
                                    for (int d = 0; d < dims; d += 2, bin++) { l[bin] += v[d]; rr[bin] += v[d + 1]; }
                                }
                            }
                            else
                            {
                                while (off < stopOff)
                                {
                                    int entry = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                                    if (entry < 0) return;
                                    float* v = table + entry * dims;
                                    for (int d = 0; d < dims && off < stopOff; d++, off++)
                                        ((off & 1) == 0 ? l : rr)[off >> 1] += v[d];
                                }
                            }
                        }
                        else
                        {
                            while (off < stopOff)
                            {
                                int entry = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                                if (entry < 0) return;
                                float* v = table + entry * dims;
                                for (int d = 0; d < dims && off < stopOff; d++, off++) spec[off % ch][off / ch] += v[d];
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Types 0 and 1: one vector per channel, one classification stream per channel (§8.6.2 steps
        /// 1-21). Type 1 adds <c>dims</c> consecutive values; type 0 strides them by <c>psize / dims</c>.
        /// <paramref name="noDecode"/>[c] ≠ 0 skips a channel entirely (nothing is read for it).</summary>
        public static void DecodeResidue01(ref BitReader r, Residue* res, Book* books, uint* fast, uint* sorted, int* sortedEntry,
                                    float* vq, byte* classMap, float** spec, byte* noDecode, int ch, int n2,
                                    byte* partClass)
        {
            int begin = res->Begin < n2 ? res->Begin : n2;
            int end = res->End < n2 ? res->End : n2;
            int psize = res->PartitionSize;
            int partitions = (end - begin) / psize;
            if (partitions <= 0) return;
            Book* classbook = books + res->Classbook;
            int cw = classbook->Dims;
            int stride = partitions + cw;
            byte* map = classMap + res->ClassMapOff;
            bool type0 = res->Type == 0;
            for (int pass = 0; pass < 8; pass++)
            {
                int p = 0;
                while (p < partitions)
                {
                    if (pass == 0)
                    {
                        for (int c = 0; c < ch; c++)
                        {
                            if (noDecode[c] != 0) continue;
                            int e = DecodeScalar(ref r, classbook, fast, sorted, sortedEntry);
                            if (e < 0) return;
                            byte* m = map + e * cw;
                            byte* dst = partClass + c * stride + p;
                            for (int k = 0; k < cw; k++) dst[k] = m[k];
                        }
                    }
                    int stop = p + cw < partitions ? p + cw : partitions;
                    for (; p < stop; p++)
                    {
                        for (int c = 0; c < ch; c++)
                        {
                            if (noDecode[c] != 0) continue;
                            int cls = partClass[c * stride + p];
                            if ((res->Cascade[cls] & (1 << pass)) == 0) continue;
                            int bi = res->Books[cls * 8 + pass];
                            if (bi < 0) continue;
                            Book* b = books + bi;
                            float* table = vq + b->VqOff;
                            int dims = b->Dims;
                            float* v = spec[c];
                            int off = begin + p * psize;
                            if (type0)
                            {
                                int step = psize / dims;
                                for (int q = 0; q < step; q++)
                                {
                                    int entry = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                                    if (entry < 0) return;
                                    float* t = table + entry * dims;
                                    for (int d = 0; d < dims; d++) v[off + q + d * step] += t[d];
                                }
                            }
                            else
                            {
                                int stopOff = off + psize;
                                while (off < stopOff)
                                {
                                    int entry = DecodeScalar(ref r, b, fast, sorted, sortedEntry);
                                    if (entry < 0) return;
                                    float* t = table + entry * dims;
                                    for (int d = 0; d < dims && off < stopOff; d++, off++) v[off] += t[d];
                                }
                            }
                        }
                    }
                }
            }
        }

        // ── 7. The inverse MDCT — stb's eight stages, step 3 in Vector128 butterflies ───────────────────────────────

        /// <summary>The per-block-size tables (stb <c>compute_twiddle_factors</c> :1254-1269, <c>compute_bitreverse</c>
        /// :1278-1284): A, B (n/2), C (n/4), bitrev (n/8). Open only — the only trigonometry the decoder does.</summary>
        public static void ComputeImdctTables(int n, Span<float> a, Span<float> b, Span<float> c, Span<ushort> bitrev)
        {
            int n4 = n >> 2, n8 = n >> 3;
            for (int k = 0, k2 = 0; k < n4; k++, k2 += 2)
            {
                a[k2] = (float)Math.Cos(4 * k * Math.PI / n);
                a[k2 + 1] = (float)-Math.Sin(4 * k * Math.PI / n);
                b[k2] = (float)Math.Cos((k2 + 1) * Math.PI / n / 2) * 0.5f;
                b[k2 + 1] = (float)Math.Sin((k2 + 1) * Math.PI / n / 2) * 0.5f;
            }
            for (int k = 0, k2 = 0; k < n8; k++, k2 += 2)
            {
                c[k2] = (float)Math.Cos(2 * (k2 + 1) * Math.PI / n);
                c[k2 + 1] = (float)-Math.Sin(2 * (k2 + 1) * Math.PI / n);
            }
            int ld = ILog(n) - 1;
            for (int i = 0; i < n8; i++) bitrev[i] = (ushort)((ReverseBits((uint)i, 32) >> (32 - ld + 3)) << 2);
        }

        /// <summary>The window slopes (§4.3.1, stb <c>compute_window</c> :1271-1276): the rising half
        /// <c>w[i] = sin(π/2 · sin²((i + ½)/(n/2) · π/2))</c> and its reversal <c>wr[i] = w[n/2 − 1 − i]</c>, so the
        /// overlap-add is elementwise.</summary>
        public static void ComputeWindow(int n, Span<float> w, Span<float> wr)
        {
            int n2 = n >> 1;
            for (int i = 0; i < n2; i++)
            {
                float s = (float)Math.Sin((i + 0.5) / n2 * 0.5 * Math.PI);
                w[i] = (float)Math.Sin(0.5 * Math.PI * (s * s));
            }
            for (int i = 0; i < n2; i++) wr[i] = w[n2 - 1 - i];
        }

        /// <summary>One four-pair butterfly group: e0[−7..0] and e2[−7..0]. Lanes of the vector at address −3 are
        /// [k11b, k00b, k11a, k00a]; <c>e2' = k ⊙ re + swap(k) ⊙ im</c> reproduces stb's
        /// <c>ee2[0] = k00·A0 − k11·A1; ee2[−1] = k11·A0 + k00·A1</c> for both pairs, operation for operation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Butterfly8(float* e0, float* e2, Vector128<float> reHi, Vector128<float> imHi,
                               Vector128<float> reLo, Vector128<float> imLo)
        {
            var a0 = Vector128.Load(e0 - 3);
            var b0 = Vector128.Load(e2 - 3);
            var a1 = Vector128.Load(e0 - 7);
            var b1 = Vector128.Load(e2 - 7);
            var k0 = a0 - b0;
            var k1 = a1 - b1;
            Vector128.Store(a0 + b0, e0 - 3);
            Vector128.Store(a1 + b1, e0 - 7);
            var s0 = Vector128.Shuffle(k0, Vector128.Create(1, 0, 3, 2));
            var s1 = Vector128.Shuffle(k1, Vector128.Create(1, 0, 3, 2));
            Vector128.Store(k0 * reHi + s0 * imHi, e2 - 3);
            Vector128.Store(k1 * reLo + s1 * imLo, e2 - 7);
        }

        /// <summary>re = [b.re, b.re, a.re, a.re], im = [b.im, −b.im, a.im, −a.im]: a = the pair at the higher address.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Twiddle(float* a, float* b, out Vector128<float> re, out Vector128<float> im)
        {
            re = Vector128.Create(b[0], b[0], a[0], a[0]);
            im = Vector128.Create(b[1], -b[1], a[1], -a[1]);
        }

        /// <summary>The scalar twin of <see cref="Butterfly8"/> (stb's four unrolled pairs), same operation order.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Butterfly8Scalar(float* e0, float* e2, float* t0, float* t1, float* t2, float* t3)
        {
            ButterflyPair(e0, e2, t0);
            ButterflyPair(e0 - 2, e2 - 2, t1);
            ButterflyPair(e0 - 4, e2 - 4, t2);
            ButterflyPair(e0 - 6, e2 - 6, t3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ButterflyPair(float* e0, float* e2, float* t)
        {
            float k00 = e0[0] - e2[0], k11 = e0[-1] - e2[-1];
            e0[0] = e0[0] + e2[0];
            e0[-1] = e0[-1] + e2[-1];
            e2[0] = k00 * t[0] + k11 * -t[1];
            e2[-1] = k11 * t[0] + k00 * t[1];
        }

        /// <summary>stb <c>imdct_step3_iter0_loop</c> (:2408-2451): n/4 groups, the twiddles advancing 8 per pair.</summary>
        static void Step3Iter0(int n, float* e, int iOff, int kOff, float* A, bool simd)
        {
            float* ee0 = e + iOff, ee2 = ee0 + kOff;
            if (simd)
                for (int i = n >> 2; i > 0; i--)
                {
                    Twiddle(A, A + 8, out var reHi, out var imHi);
                    Twiddle(A + 16, A + 24, out var reLo, out var imLo);
                    Butterfly8(ee0, ee2, reHi, imHi, reLo, imLo);
                    A += 32; ee0 -= 8; ee2 -= 8;
                }
            else
                for (int i = n >> 2; i > 0; i--)
                {
                    Butterfly8Scalar(ee0, ee2, A, A + 8, A + 16, A + 24);
                    A += 32; ee0 -= 8; ee2 -= 8;
                }
        }

        /// <summary>stb <c>imdct_step3_inner_r_loop</c> (:2453-2501): the twiddles advance k1 per pair.</summary>
        static void Step3R(int lim, float* e, int d0, int kOff, float* A, int k1, bool simd)
        {
            float* e0 = e + d0, e2 = e0 + kOff;
            if (simd)
                for (int i = lim >> 2; i > 0; i--)
                {
                    Twiddle(A, A + k1, out var reHi, out var imHi);
                    Twiddle(A + 2 * k1, A + 3 * k1, out var reLo, out var imLo);
                    Butterfly8(e0, e2, reHi, imHi, reLo, imLo);
                    A += 4 * k1; e0 -= 8; e2 -= 8;
                }
            else
                for (int i = lim >> 2; i > 0; i--)
                {
                    Butterfly8Scalar(e0, e2, A, A + k1, A + 2 * k1, A + 3 * k1);
                    A += 4 * k1; e0 -= 8; e2 -= 8;
                }
        }

        /// <summary>stb <c>imdct_step3_inner_s_loop</c> (:2503-2552): the same four twiddles for every group, held in
        /// registers; groups k0 apart.</summary>
        static void Step3S(int n, float* e, int iOff, int kOff, float* A, int aOff, int k0, bool simd)
        {
            float* ee0 = e + iOff, ee2 = ee0 + kOff;
            if (simd)
            {
                Twiddle(A, A + aOff, out var reHi, out var imHi);
                Twiddle(A + 2 * aOff, A + 3 * aOff, out var reLo, out var imLo);
                for (int i = n; i > 0; i--) { Butterfly8(ee0, ee2, reHi, imHi, reLo, imLo); ee0 -= k0; ee2 -= k0; }
            }
            else
            {
                float* t1 = A + aOff, t2 = A + 2 * aOff, t3 = A + 3 * aOff;
                for (int i = n; i > 0; i--) { Butterfly8Scalar(ee0, ee2, A, t1, t2, t3); ee0 -= k0; ee2 -= k0; }
            }
        }

        /// <summary>stb <c>imdct_step3_inner_s_loop_ld654</c> + <c>iter_54</c> (:2554-2627): the LAST three stages
        /// fused, where the twiddles are 1, 0 and ±√½ for every block size. Kept in stb's scalar shape: every lane
        /// has its own sign pattern.</summary>
        static void Step3Ld654(int n, float* e, int iOff, float* A, int baseN)
        {
            float a2 = A[baseN >> 3];
            float* z = e + iOff, bas = z - 16 * n;
            while (z > bas)
            {
                float k00 = z[0] - z[-8], k11 = z[-1] - z[-9], l00 = z[-2] - z[-10], l11 = z[-3] - z[-11];
                z[0] = z[0] + z[-8]; z[-1] = z[-1] + z[-9]; z[-2] = z[-2] + z[-10]; z[-3] = z[-3] + z[-11];
                z[-8] = k00; z[-9] = k11; z[-10] = (l00 + l11) * a2; z[-11] = (l11 - l00) * a2;
                k00 = z[-4] - z[-12]; k11 = z[-5] - z[-13]; l00 = z[-6] - z[-14]; l11 = z[-7] - z[-15];
                z[-4] = z[-4] + z[-12]; z[-5] = z[-5] + z[-13]; z[-6] = z[-6] + z[-14]; z[-7] = z[-7] + z[-15];
                z[-12] = k11; z[-13] = -k00; z[-14] = (l11 - l00) * a2; z[-15] = (l00 + l11) * -a2;
                Iter54(z);
                Iter54(z - 8);
                z -= 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Iter54(float* z)
        {
            float k00 = z[0] - z[-4], y0 = z[0] + z[-4], y2 = z[-2] + z[-6], k22 = z[-2] - z[-6];
            z[0] = y0 + y2;
            z[-2] = y0 - y2;
            float k33 = z[-3] - z[-7];
            z[-4] = k00 + k33;
            z[-6] = k00 - k33;
            float k11 = z[-1] - z[-5], y1 = z[-1] + z[-5], y3 = z[-3] + z[-7];
            z[-1] = y1 + y3;
            z[-3] = y1 - y3;
            z[-5] = k11 - k22;
            z[-7] = k11 + k22;
        }

        /// <summary>The inverse MDCT (stb <c>inverse_mdct</c> :2629-2929). <paramref name="buf"/>: n floats — the n/2
        /// spectrum on entry, n unwindowed time samples on exit. <paramref name="buf2"/>: n/2 scratch. Tables from
        /// <see cref="ComputeImdctTables"/>. No bounds check: every pointer derives from n and every table is sized
        /// from n; the loop bounds are stb's. ONE correction: stb applies step 3's stage 1 twice (n = 128) or stage 0
        /// twice (n = 64), because its fixed iteration-0/iteration-1 loops overlap the fused last-three-stage loop
        /// when fewer than six stages exist; libvorbis never emits a block that small, stb never met one, but the
        /// format allows 64 and 128, so those sizes skip the overlapping calls.</summary>
        public static void Imdct(float* buf, float* buf2, int n, float* A, float* B, float* C, ushort* bitrev)
        {
            int n2 = n >> 1, n4 = n >> 2, n8 = n >> 3;
            int ld = ILog(n) - 1;
            bool simd = !ForceScalar && Vector128.IsHardwareAccelerated;

            // steps 0+1: copy and reflect the spectrum into buf2 with A (the "missing ×2" is repaid in B)
            {
                float* d = buf2 + n2 - 2, aa = A, e = buf, eStop = buf + n2;
                while (e != eStop)
                {
                    d[1] = e[0] * aa[0] - e[2] * aa[1];
                    d[0] = e[0] * aa[1] + e[2] * aa[0];
                    d -= 2; aa += 2; e += 4;
                }
                e = buf + n2 - 3;
                while (d >= buf2)
                {
                    d[1] = -e[2] * aa[0] - -e[0] * aa[1];
                    d[0] = -e[2] * aa[1] + -e[0] * aa[0];
                    d -= 2; aa += 2; e -= 4;
                }
            }
            float* u = buf, v = buf2;

            // step 2: v → u (stb :2689-2727)
            {
                float* aa = A + n2 - 8, e0 = v + n4, e1 = v, d0 = u + n4, d1 = u;
                while (aa >= A)
                {
                    float v41_21 = e0[1] - e1[1], v40_20 = e0[0] - e1[0];
                    d0[1] = e0[1] + e1[1];
                    d0[0] = e0[0] + e1[0];
                    d1[1] = v41_21 * aa[4] - v40_20 * aa[5];
                    d1[0] = v40_20 * aa[4] + v41_21 * aa[5];
                    v41_21 = e0[3] - e1[3];
                    v40_20 = e0[2] - e1[2];
                    d0[3] = e0[3] + e1[3];
                    d0[2] = e0[2] + e1[2];
                    d1[3] = v41_21 * aa[0] - v40_20 * aa[1];
                    d1[2] = v40_20 * aa[0] + v41_21 * aa[1];
                    aa -= 8; d0 += 4; d1 += 4; e0 += 4; e1 += 4;
                }
            }

            // step 3 (stb :2729-2790)
            if (ld >= 7)
            {
                Step3Iter0(n >> 4, u, n2 - 1 - n4 * 0, -(n >> 3), A, simd);
                Step3Iter0(n >> 4, u, n2 - 1 - n4 * 1, -(n >> 3), A, simd);
            }
            if (ld >= 8)
            {
                Step3R(n >> 5, u, n2 - 1 - n8 * 0, -(n >> 4), A, 16, simd);
                Step3R(n >> 5, u, n2 - 1 - n8 * 1, -(n >> 4), A, 16, simd);
                Step3R(n >> 5, u, n2 - 1 - n8 * 2, -(n >> 4), A, 16, simd);
                Step3R(n >> 5, u, n2 - 1 - n8 * 3, -(n >> 4), A, 16, simd);
            }
            int l = 2;
            for (; l < (ld - 3) >> 1; l++)
            {
                int k0 = n >> (l + 2), k0_2 = k0 >> 1, lim = 1 << (l + 1);
                for (int i = 0; i < lim; i++) Step3R(n >> (l + 4), u, n2 - 1 - k0 * i, -k0_2, A, 1 << (l + 3), simd);
            }
            for (; l < ld - 6; l++)
            {
                int k0 = n >> (l + 2), k1 = 1 << (l + 3), k0_2 = k0 >> 1, rlim = n >> (l + 6), lim = 1 << (l + 1);
                float* a0 = A;
                int iOff = n2 - 1;
                for (int r = rlim; r > 0; r--)
                {
                    Step3S(lim, u, iOff, -k0_2, a0, k1, k0, simd);
                    a0 += k1 * 4;
                    iOff -= 8;
                }
            }
            Step3Ld654(n >> 5, u, n2 - 1, A, n);

            // steps 4-6: bit reversal u → v (stb :2796-2825)
            {
                float* d0 = v + n4 - 4, d1 = v + n2 - 4;
                ushort* br = bitrev;
                while (d0 >= v)
                {
                    int k4 = br[0];
                    d1[3] = u[k4 + 0]; d1[2] = u[k4 + 1]; d0[3] = u[k4 + 2]; d0[2] = u[k4 + 3];
                    k4 = br[1];
                    d1[1] = u[k4 + 0]; d1[0] = u[k4 + 1]; d0[1] = u[k4 + 2]; d0[0] = u[k4 + 3];
                    d0 -= 4; d1 -= 4; br += 2;
                }
            }

            // step 7: in place in v with C (stb :2833-2866)
            {
                float* d = v, e = v + n2 - 4, cc = C;
                while (d < e)
                {
                    float a02 = d[0] - e[2], a11 = d[1] + e[3];
                    float b0 = cc[1] * a02 + cc[0] * a11, b1 = cc[1] * a11 - cc[0] * a02;
                    float b2 = d[0] + e[2], b3 = d[1] - e[3];
                    d[0] = b2 + b0; d[1] = b3 + b1; e[2] = b2 - b0; e[3] = b1 - b3;
                    a02 = d[2] - e[0]; a11 = d[3] + e[1];
                    b0 = cc[3] * a02 + cc[2] * a11; b1 = cc[3] * a11 - cc[2] * a02;
                    b2 = d[2] + e[0]; b3 = d[3] - e[1];
                    d[2] = b2 + b0; d[3] = b3 + b1; e[0] = b2 - b0; e[1] = b1 - b3;
                    cc += 4; d += 4; e -= 4;
                }
            }

            // step 8 + decode: B twiddle, written to the four quarters of buf from both ends (stb :2867-2925)
            {
                float* bb = B + n2 - 8, e = v + n2 - 8;
                float* d0 = buf, d1 = buf + n2 - 4, d2 = buf + n2, d3 = buf + n - 4;
                while (e >= v)
                {
                    float p3 = e[6] * bb[7] - e[7] * bb[6], p2 = -e[6] * bb[6] - e[7] * bb[7];
                    d0[0] = p3; d1[3] = -p3; d2[0] = p2; d3[3] = p2;
                    float p1 = e[4] * bb[5] - e[5] * bb[4], p0 = -e[4] * bb[4] - e[5] * bb[5];
                    d0[1] = p1; d1[2] = -p1; d2[1] = p0; d3[2] = p0;
                    p3 = e[2] * bb[3] - e[3] * bb[2]; p2 = -e[2] * bb[2] - e[3] * bb[3];
                    d0[2] = p3; d1[1] = -p3; d2[2] = p2; d3[1] = p2;
                    p1 = e[0] * bb[1] - e[1] * bb[0]; p0 = -e[0] * bb[0] - e[1] * bb[1];
                    d0[3] = p1; d1[0] = -p1; d2[3] = p0; d3[0] = p0;
                    bb -= 8; e -= 8; d0 += 4; d3 -= 4; d1 -= 4; d2 += 4;
                }
            }
        }

        // ── 8. The streaming kernels — Vector256 when accelerated, Vector128 always, scalar tail ────────────────────

        /// <summary><c>cur[i] = cur[i]·w[i] + prev[i]·wr[i]</c> for i &lt; m: one channel's overlap region (stb
        /// <c>vorbis_finish_frame</c> :3468-3477 with the reversed slope precomputed).</summary>
        public static void OverlapAdd(float* cur, float* prev, float* w, float* wr, int m)
        {
            int i = 0;
            if (m >= 16 && !ForceScalar)
            {
                if (Vector256.IsHardwareAccelerated)
                    for (; i <= m - 8; i += 8)
                        Vector256.Store(Vector256.Load(cur + i) * Vector256.Load(w + i)
                                      + Vector256.Load(prev + i) * Vector256.Load(wr + i), cur + i);
                else if (Vector128.IsHardwareAccelerated)
                    for (; i <= m - 4; i += 4)
                        Vector128.Store(Vector128.Load(cur + i) * Vector128.Load(w + i)
                                      + Vector128.Load(prev + i) * Vector128.Load(wr + i), cur + i);
            }
            for (; i < m; i++) cur[i] = cur[i] * w[i] + prev[i] * wr[i];
        }

        /// <summary>§4.3.5 inverse coupling for one (magnitude, angle) pair, branch-free: <c>d = m &gt; 0 ? a : −a</c>;
        /// <c>a &gt; 0 ? (m, m − d) : (m + d, m)</c> — the spec's four cases folded (stb :3330-3349).</summary>
        public static void Uncouple(float* m, float* a, int n)
        {
            int i = 0;
            if (n >= 16 && !ForceScalar)
            {
                if (Vector256.IsHardwareAccelerated)
                {
                    var zero = Vector256<float>.Zero;
                    for (; i <= n - 8; i += 8)
                    {
                        var mv = Vector256.Load(m + i);
                        var av = Vector256.Load(a + i);
                        var d = Vector256.ConditionalSelect(Vector256.GreaterThan(mv, zero), av, -av);
                        var aPos = Vector256.GreaterThan(av, zero);
                        Vector256.Store(Vector256.ConditionalSelect(aPos, mv, mv + d), m + i);
                        Vector256.Store(Vector256.ConditionalSelect(aPos, mv - d, mv), a + i);
                    }
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    var zero = Vector128<float>.Zero;
                    for (; i <= n - 4; i += 4)
                    {
                        var mv = Vector128.Load(m + i);
                        var av = Vector128.Load(a + i);
                        var d = Vector128.ConditionalSelect(Vector128.GreaterThan(mv, zero), av, -av);
                        var aPos = Vector128.GreaterThan(av, zero);
                        Vector128.Store(Vector128.ConditionalSelect(aPos, mv, mv + d), m + i);
                        Vector128.Store(Vector128.ConditionalSelect(aPos, mv - d, mv), a + i);
                    }
                }
            }
            for (; i < n; i++)
            {
                float mv = m[i], av = a[i], d = mv > 0 ? av : -av;
                if (av > 0) { m[i] = mv; a[i] = mv - d; }
                else { m[i] = mv + d; a[i] = mv; }
            }
        }

        /// <summary><c>p[i] *= k</c> — the floor-1 constant tail past the last post.</summary>
        public static void MultiplyConstant(float* p, int n, float k)
        {
            int i = 0;
            if (n >= 16 && !ForceScalar)
            {
                if (Vector256.IsHardwareAccelerated)
                {
                    var kv = Vector256.Create(k);
                    for (; i <= n - 8; i += 8) Vector256.Store(Vector256.Load(p + i) * kv, p + i);
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    var kv = Vector128.Create(k);
                    for (; i <= n - 4; i += 4) Vector128.Store(Vector128.Load(p + i) * kv, p + i);
                }
            }
            for (; i < n; i++) p[i] *= k;
        }

        /// <summary>Two channels → interleaved frames with the gain folded in (the one multiply per sample the adapter
        /// promised). The interleave is a TRANSPOSE, so it is the ISA's own unpack: AVX <c>unpacklo/hi</c> +
        /// <c>permute2f128</c>, SSE <c>unpcklps/unpckhps</c>, NEON <c>zip1/zip2</c>. Mono passes <c>l == r</c>.</summary>
        public static void InterleaveStereo(float* l, float* r, float* dst, int frames, float gain)
        {
            int i = 0;
            if (frames >= 16 && !ForceScalar)
            {
                if (Vector256.IsHardwareAccelerated && Avx.IsSupported)
                {
                    var g = Vector256.Create(gain);
                    for (; i <= frames - 8; i += 8)
                    {
                        var lv = Vector256.Load(l + i) * g;
                        var rv = Vector256.Load(r + i) * g;
                        var lo = Avx.UnpackLow(lv, rv);                                   // l0 r0 l1 r1 | l4 r4 l5 r5
                        var hi = Avx.UnpackHigh(lv, rv);                                  // l2 r2 l3 r3 | l6 r6 l7 r7
                        Vector256.Store(Avx.Permute2x128(lo, hi, 0x20), dst + 2 * i);
                        Vector256.Store(Avx.Permute2x128(lo, hi, 0x31), dst + 2 * i + 8);
                    }
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    var g = Vector128.Create(gain);
                    for (; i <= frames - 4; i += 4)
                    {
                        var lv = Vector128.Load(l + i) * g;
                        var rv = Vector128.Load(r + i) * g;
                        Vector128<float> lo, hi;
                        if (Sse.IsSupported) { lo = Sse.UnpackLow(lv, rv); hi = Sse.UnpackHigh(lv, rv); }
                        else if (AdvSimd.Arm64.IsSupported) { lo = AdvSimd.Arm64.ZipLow(lv, rv); hi = AdvSimd.Arm64.ZipHigh(lv, rv); }
                        else
                        {
                            var even = Vector128.Create(-1, 0, -1, 0).AsSingle();
                            lo = Vector128.ConditionalSelect(even, Vector128.Shuffle(lv, Vector128.Create(0, 0, 1, 1)),
                                                                   Vector128.Shuffle(rv, Vector128.Create(0, 0, 1, 1)));
                            hi = Vector128.ConditionalSelect(even, Vector128.Shuffle(lv, Vector128.Create(2, 2, 3, 3)),
                                                                   Vector128.Shuffle(rv, Vector128.Create(2, 2, 3, 3)));
                        }
                        Vector128.Store(lo, dst + 2 * i);
                        Vector128.Store(hi, dst + 2 * i + 4);
                    }
                }
            }
            for (; i < frames; i++)
            {
                dst[2 * i] = l[i] * gain;
                dst[2 * i + 1] = r[i] * gain;
            }
        }

        /// <summary>3..255 channels → stereo, scalar (local files only; Spotify serves stereo). The Vorbis channel
        /// order (§4.3.9) with −3 dB centre and surround weights and the LFE dropped; each sample is
        /// <c>(x · gain) · k</c>. <paramref name="planes"/>[c · stride + at] is channel c's first frame.</summary>
        public static void InterleaveMulti(float* planes, int stride, int at, int channels, float* dst, int frames,
                                           float* kl, float* kr, float gain)
        {
            for (int i = 0; i < frames; i++)
            {
                float sl = 0, sr = 0;
                for (int c = 0; c < channels; c++)
                {
                    float x = planes[c * stride + at + i] * gain;
                    sl += x * kl[c];
                    sr += x * kr[c];
                }
                dst[2 * i] = sl;
                dst[2 * i + 1] = sr;
            }
        }

        /// <summary>The stereo downmix weights for a Vorbis channel count (§4.3.9 order). Setup only.</summary>
        public static void DownmixWeights(int channels, Span<float> kl, Span<float> kr)
        {
            const float h = 0.70710678f;
            kl.Clear();
            kr.Clear();
            switch (channels)
            {
                case 1: kl[0] = 1; kr[0] = 1; break;
                case 2: kl[0] = 1; kr[1] = 1; break;
                case 3: kl[0] = 1; kl[1] = h; kr[1] = h; kr[2] = 1; break;                                   // L C R
                case 4: kl[0] = 1; kr[1] = 1; kl[2] = h; kr[3] = h; break;                                   // FL FR RL RR
                case 5: case 6: kl[0] = 1; kl[1] = h; kr[1] = h; kr[2] = 1; kl[3] = h; kr[4] = h; break;     // FL C FR RL RR (LFE)
                case 7: kl[0] = 1; kl[1] = h; kr[1] = h; kr[2] = 1; kl[3] = h; kr[4] = h; kl[5] = 0.5f; kr[5] = 0.5f; break;
                case 8: kl[0] = 1; kl[1] = h; kr[1] = h; kr[2] = 1; kl[3] = h; kr[4] = h; kl[5] = h; kr[6] = h; break;
                default: kl[0] = 1; kr[1] = 1; break;
            }
        }

        // ── 9. The decoder ──────────────────────────────────────────────────────────────────────────────────────────
        //
        // Allocation (P8), all in Open, grow-only on the pinned object heap. Computed from the setup headers of the
        // fixtures (44.1 kHz stereo, 256/2048, libvorbis via ffmpeg); `AllocatedBytes` reports the live figure.
        //                                           pink-320 (44 books)   pink-96 (38 books)
        //   fast tables    books × 1,024 uints       180,224               155,648   (sized exactly: count is known)
        //   sorted+entries Σ codes longer than 10      25,600                ~25,000
        //   VQ values      Σ Entries × Dims floats     45,184               419,904   (lower rates = bigger lattices)
        //   class maps     Σ classbook Entries × Dims     400                   ~400
        //   Book/Floor/Residue/Mapping/Mode arrays     ~8,600                ~8,400
        //   streams (independent of the setup, 2048/256 stereo): IMDCT A/B/C + w/wr 20,736, bitrev 576, buf 16,384,
        //   prev 8,192, buf2 4,096, floor posts 4,000, output 16,384, flags/pointers/weights 38, partClass 66
        //                                           = 70,472
        //   retained after Open                     ≈ 330 KB              ≈ 668 KB
        //   first Open incl. growth garbage + ~6 KB of parse scratch ≈ 397 KB; a second Open of a setup no larger: 0
        //   per packet                                0 bytes

        public sealed class Decoder
        {
            // setup
            Book[] _books = [];
            Floor[] _floors = [];
            Residue[] _residues = [];
            Mapping[] _mappings = [];
            Mode[] _modes = [];
            uint[] _fast = [];
            uint[] _sorted = [];
            int[] _sortedEntry = [];
            float[] _vq = [];
            byte[] _classMap = [];
            int[] _barkMap = [];
            float[] _barkCos = [];
            // per block size, per channel, scratch
            float[] _tables = [];
            ushort[] _rev = [];
            float[] _buf = [];
            float[] _prev = [];
            float[] _buf2 = [];
            int[] _y = [];
            int[] _final = [];
            float[] _f0 = [];
            byte[] _flags = [];
            nint[] _submapSpec = [];
            byte[] _partClass = [];
            float[] _output = [];
            float[] _weights = [];
            // parse scratch (not pinned, grow-only)
            sbyte[] _lens = [];
            uint[] _codes = [];
            uint[] _mults = [];

            int _ch, _rate, _n0, _n1, _modeBits, _bookCount, _floorCount, _residueCount, _mappingCount, _modeCount;
            bool _open;
            float _gain = 1f;

            // pointers, taken once at the end of Open
            Book* _booksP; Floor* _floorsP; Residue* _residuesP; Mapping* _mappingsP; Mode* _modesP;
            uint* _fastP; uint* _sortedP; int* _sortedEntryP; float* _vqP; byte* _classMapP;
            int* _barkMapP; float* _barkCosP; float* _dbP;
            float* _a0, _b0, _c0, _w0, _wr0, _a1, _b1, _c1, _w1, _wr1;
            ushort* _rev0, _rev1;
            float* _bufP, _prevP, _buf2P, _f0P, _outputP, _klP, _krP;
            int* _yP, _finalP;
            byte* _floorUnusedP, _noResidueP, _submapNoP, _partClassP;
            nint* _submapSpecP;

            // lapping state: the previous block's [n/2, n) sits in _prev; its first _tailLen samples are owed to the
            // next packet's output, the remaining _lapLen overlap the next block's left slope
            int _prevN, _tailLen, _lapLen;

            public int Channels => _ch;
            public int SampleRate => _rate;
            public int BlockSize0 => _n0;
            public int BlockSize1 => _n1;
            public bool IsOpen => _open;
            /// <summary>The most frames one packet can return: blocksize1/4 + blocksize1/4.</summary>
            public int MaxFrames => _n1 >> 1;
            /// <summary>Frames the last <see cref="DecodePacket(ReadOnlySpan{byte})"/> wrote to <see cref="Output"/>.</summary>
            public int Frames { get; private set; }
            /// <summary>Interleaved STEREO floats, <see cref="Frames"/> × 2, valid until the next packet.</summary>
            public float* Output => _outputP;
            public ReadOnlySpan<float> OutputSpan => new(_outputP, Frames * OutputChannels);
            /// <summary>The linear gain folded into the interleave (normalization). 1 = unity.</summary>
            public float Gain { get => _gain; set => _gain = value; }
            /// <summary>The previous block's size, 0 when the next packet primes.</summary>
            public int PreviousBlockSize => _prevN;
            /// <summary>Packets that ran past their end (decoded what they had) — diagnostics, not a fault.</summary>
            public long Overruns { get; private set; }
            /// <summary>Bytes held by this decoder's arrays after Open — the number the allocation table states.</summary>
            public long AllocatedBytes { get; private set; }

            /// <summary>Parse the identification and setup headers and size every buffer (the ONLY allocation site).
            /// The comment header is not needed here. False on any header this decoder cannot play.</summary>
            public bool Open(ReadOnlySpan<byte> identification, ReadOnlySpan<byte> setup, float gainLinear = 1f)
            {
                _open = false;
                Frames = 0;
                if (!TryParseIdentification(identification, out Identification id)) return false;
                if (HeaderType(setup) != 5) return false;
                _ch = id.Channels;
                _rate = id.SampleRate;
                _n0 = id.BlockSize0;
                _n1 = id.BlockSize1;
                _gain = gainLinear;
                bool ok;
                fixed (byte* sp = setup)
                {
                    var r = new BitReader(sp + 7, setup.Length - 7);
                    ok = ParseSetup(ref r);
                }
                if (!ok) return false;
                AllocateStreamBuffers();
                _open = true;
                Prime();
                return true;
            }

            /// <summary>Forget the previous block: the next packet decodes, saves its right half and returns 0 frames
            /// (spec §4.3.8; stb <c>previous_length = 0</c>). Call after every seek.</summary>
            public void Prime()
            {
                _prevN = 0;
                _tailLen = 0;
                _lapLen = 0;
                Frames = 0;
            }

            /// <summary>The plan's name for <see cref="Prime"/> (§3.9, §6.2).</summary>
            public void ResetLapping() => Prime();

            /// <summary>What a packet WOULD return, from its first byte only (stb <c>peek_decode_initial</c>
            /// :4855-4878, libvorbis <c>vorbis_packet_blocksize</c>): <c>prevN/4 + n/4</c>, 0 when
            /// <paramref name="prevN"/> is 0, −1 for a non-audio packet. Advances <paramref name="prevN"/> to this
            /// packet's block size — exactly what <see cref="DecodePacket(ReadOnlySpan{byte})"/> would return.</summary>
            public int PeekFrames(ReadOnlySpan<byte> packet, ref int prevN)
            {
                if (!_open || packet.Length == 0 || (packet[0] & 1) != 0) return -1;
                int modeIdx = (packet[0] >> 1) & ((1 << _modeBits) - 1);
                if (modeIdx >= _modeCount) return -1;
                int n = _modesP[modeIdx].BlockFlag != 0 ? _n1 : _n0;
                int frames = prevN == 0 ? 0 : (prevN >> 2) + (n >> 2);
                prevN = n;
                return frames;
            }

            /// <summary>The task-shaped overload: decode and hand back the frame count.</summary>
            public PacketResult DecodePacket(ReadOnlySpan<byte> packet, out int samples)
            {
                PacketResult res = DecodePacket(packet);
                samples = Frames;
                return res;
            }

            /// <summary>Decode one audio packet into <see cref="Output"/> (<see cref="Frames"/> stereo frames). The
            /// order is the spec's (§4.3) and stb's <c>vorbis_decode_packet_rest</c>: floors, coupling propagation,
            /// residues straight into the block buffers, inverse coupling, floor curve × residue, IMDCT, overlap-add,
            /// interleave + gain. Zero allocation.</summary>
            [SkipLocalsInit]
            public PacketResult DecodePacket(ReadOnlySpan<byte> packet)
            {
                Frames = 0;
                if (!_open) return PacketResult.NotOpen;
                if (packet.Length == 0) return PacketResult.Truncated;
                fixed (byte* pk = packet)
                {
                    var r = new BitReader(pk, packet.Length);
                    if (r.Read(1) != 0) return PacketResult.NotAudio;
                    int modeIdx = (int)r.Read(_modeBits);
                    if (modeIdx >= _modeCount) return PacketResult.BadMode;
                    Mode* mode = _modesP + modeIdx;
                    bool isLong = mode->BlockFlag != 0;
                    int n = isLong ? _n1 : _n0, n2 = n >> 1;
                    bool prevLong = true, nextLong = true;
                    if (isLong) { prevLong = r.Read(1) != 0; nextLong = r.Read(1) != 0; }
                    if (r.Overrun) return PacketResult.Truncated;
                    // §4.3.1 window boundaries (stb vorbis_decode_initial :3159-3176)
                    int left = isLong && !prevLong ? (n - _n0) >> 2 : 0;
                    int leftEnd = isLong && !prevLong ? (n + _n0) >> 2 : n2;
                    int right = isLong && !nextLong ? (n * 3 - _n0) >> 2 : n2;
                    int rightEnd = isLong && !nextLong ? (n * 3 + _n0) >> 2 : n;   // past it the window is 0

                    Mapping* map = _mappingsP + mode->Mapping;
                    int ch = _ch, stride = _n1;
                    Book* books = _booksP;
                    uint* fast = _fastP, sorted = _sortedP;
                    int* sortedEntry = _sortedEntryP;

                    // 1. floors — an unused floor silences the channel (floorUnused) and, before coupling, its residue
                    for (int c = 0; c < ch; c++)
                    {
                        Floor* f = _floorsP + map->SubmapFloor[map->Mux[c]];
                        bool used = f->Type == 1
                            ? DecodeFloor1Posts(ref r, f, books, fast, sorted, sortedEntry, _yP + c * MaxPosts)
                            : DecodeFloor0(ref r, f, books, fast, sorted, sortedEntry, _vqP, _f0P + c * Floor0Stride);
                        byte off = used ? (byte)0 : (byte)1;
                        _floorUnusedP[c] = off;
                        _noResidueP[c] = off;
                    }
                    // 2. §4.3.3 nonzero vector propagate: either half of a coupled pair used ⇒ both residues decode
                    for (int s = 0; s < map->CouplingSteps; s++)
                    {
                        int mc = map->Magnitude[s], ac = map->Angle[s];
                        if (_noResidueP[mc] == 0 || _noResidueP[ac] == 0) { _noResidueP[mc] = 0; _noResidueP[ac] = 0; }
                    }
                    // 3. residues into the first n/2 of each block buffer (the IMDCT's input half)
                    for (int c = 0; c < ch; c++) new Span<float>(_bufP + c * stride, n2).Clear();
                    float** specs = (float**)_submapSpecP;
                    for (int s = 0; s < map->Submaps; s++)
                    {
                        int cnt = 0;
                        bool anyOn = false;
                        for (int c = 0; c < ch; c++)
                        {
                            if (map->Mux[c] != s) continue;
                            specs[cnt] = _bufP + c * stride;
                            _submapNoP[cnt] = _noResidueP[c];
                            if (_noResidueP[c] == 0) anyOn = true;
                            cnt++;
                        }
                        if (cnt == 0) continue;
                        Residue* res = _residuesP + map->SubmapResidue[s];
                        if (res->Type == 2)
                        {
                            if (anyOn)
                                DecodeResidue2(ref r, res, books, fast, sorted, sortedEntry, _vqP, _classMapP, specs, cnt,
                                               n2, _partClassP);
                        }
                        else if (anyOn)
                            DecodeResidue01(ref r, res, books, fast, sorted, sortedEntry, _vqP, _classMapP, specs,
                                            _submapNoP, cnt, n2, _partClassP);
                    }
                    // 4. §4.3.5 inverse coupling, last step first
                    for (int s = map->CouplingSteps - 1; s >= 0; s--)
                        Uncouple(_bufP + map->Magnitude[s] * stride, _bufP + map->Angle[s] * stride, n2);
                    // 5. §4.3.6 floor curve × residue, in place
                    for (int c = 0; c < ch; c++)
                    {
                        float* spec = _bufP + c * stride;
                        if (_floorUnusedP[c] != 0) { new Span<float>(spec, n2).Clear(); continue; }
                        Floor* f = _floorsP + map->SubmapFloor[map->Mux[c]];
                        if (f->Type == 1)
                        {
                            UnwrapPosts(f, _yP + c * MaxPosts, _finalP + c * MaxPosts);
                            RenderFloor1(f, _finalP + c * MaxPosts, spec, n2, _dbP);
                        }
                        else
                        {
                            int bo = isLong ? f->BarkOff1 : f->BarkOff0;
                            RenderFloor0(f, _f0P + c * Floor0Stride, spec, n2, _barkMapP + bo, _barkCosP + bo);
                        }
                    }
                    // 6. IMDCT, then overlap the left slope with the previous block's saved right slope
                    int half1 = _n1 >> 1;
                    bool lap = _prevN != 0 && _lapLen == leftEnd - left;
                    float* w = _lapLen == (_n0 >> 1) ? _w0 : _w1, wr = _lapLen == (_n0 >> 1) ? _wr0 : _wr1;
                    for (int c = 0; c < ch; c++)
                    {
                        float* blk = _bufP + c * stride;
                        if (isLong) Imdct(blk, _buf2P, n, _a1, _b1, _c1, _rev1);
                        else Imdct(blk, _buf2P, n, _a0, _b0, _c0, _rev0);
                        if (lap) OverlapAdd(blk + left, _prevP + c * half1 + _tailLen, w, wr, _lapLen);
                    }
                    // 7. output: the previous block's owed [n/2, right) then this block's [left, n/2) — the spec's
                    //    blocksize(prev)/4 + blocksize(cur)/4 frames between the two block centres
                    int frames = 0;
                    if (lap)
                    {
                        int t = _tailLen, main = n2 - left;
                        frames = t + main;
                        float g = _gain;
                        float* o = _outputP;
                        if (ch == 2)
                        {
                            if (t > 0) InterleaveStereo(_prevP, _prevP + half1, o, t, g);
                            InterleaveStereo(_bufP + left, _bufP + stride + left, o + 2 * t, main, g);
                        }
                        else if (ch == 1)
                        {
                            if (t > 0) InterleaveStereo(_prevP, _prevP, o, t, g);
                            InterleaveStereo(_bufP + left, _bufP + left, o + 2 * t, main, g);
                        }
                        else
                        {
                            if (t > 0) InterleaveMulti(_prevP, half1, 0, ch, o, t, _klP, _krP, g);
                            InterleaveMulti(_bufP, stride, left, ch, o + 2 * t, main, _klP, _krP, g);
                        }
                    }
                    // 8. save this block's [n/2, n) for the next packet
                    long bytes = (long)n2 * sizeof(float);
                    for (int c = 0; c < ch; c++) Buffer.MemoryCopy(_bufP + c * stride + n2, _prevP + c * half1, bytes, bytes);
                    _tailLen = right - n2;
                    _lapLen = rightEnd - right;                                     // stb: previous_length = right_end − right
                    _prevN = n;
                    Frames = frames;
                    if (r.Overrun) Overruns++;
                    return PacketResult.Ok;
                }
            }

            // ── 9.1 setup parse (§4.2.4) — the only code that allocates ────────────────────────────────────────────

            bool ParseSetup(ref BitReader r)
            {
                // codebooks
                _bookCount = (int)r.Read(8) + 1;
                Grow(ref _books, _bookCount);
                Grow(ref _fast, _bookCount * FastSize);                            // exact: the count is known now
                _booksP = Ptr(_books);
                int fastLen = 0, sortedLen = 0, vqLen = 0;
                for (int i = 0; i < _bookCount; i++)
                    if (!ParseBook(ref r, _booksP + i, ref fastLen, ref sortedLen, ref vqLen)) return false;
                // time domain transforms: placeholders, all zero
                int times = (int)r.Read(6) + 1;
                for (int i = 0; i < times; i++) if (r.Read(16) != 0) return false;
                // floors
                _floorCount = (int)r.Read(6) + 1;
                Grow(ref _floors, _floorCount);
                _floorsP = Ptr(_floors);
                int barkLen = 0;
                for (int i = 0; i < _floorCount; i++)
                    if (!ParseFloor(ref r, _floorsP + i, ref barkLen)) return false;
                // residues
                _residueCount = (int)r.Read(6) + 1;
                Grow(ref _residues, _residueCount);
                _residuesP = Ptr(_residues);
                int classLen = 0;
                for (int i = 0; i < _residueCount; i++)
                    if (!ParseResidue(ref r, _residuesP + i, ref classLen)) return false;
                // mappings
                _mappingCount = (int)r.Read(6) + 1;
                Grow(ref _mappings, _mappingCount);
                _mappingsP = Ptr(_mappings);
                for (int i = 0; i < _mappingCount; i++)
                    if (!ParseMapping(ref r, _mappingsP + i)) return false;
                // modes
                _modeCount = (int)r.Read(6) + 1;
                Grow(ref _modes, _modeCount);
                _modesP = Ptr(_modes);
                for (int i = 0; i < _modeCount; i++)
                {
                    int blockFlag = (int)r.Read(1), windowType = (int)r.Read(16), transformType = (int)r.Read(16);
                    int mapping = (int)r.Read(8);
                    if (windowType != 0 || transformType != 0 || mapping >= _mappingCount) return false;
                    _modesP[i].BlockFlag = (byte)blockFlag;
                    _modesP[i].Mapping = (byte)mapping;
                }
                if (r.Read(1) != 1 || r.Overrun) return false;                     // framing bit
                _modeBits = ILog(_modeCount - 1);
                _fastP = Ptr(_fast);
                _sortedP = Ptr(_sorted);
                _sortedEntryP = Ptr(_sortedEntry);
                _vqP = Ptr(_vq);
                _classMapP = Ptr(_classMap);
                _barkMapP = Ptr(_barkMap);
                _barkCosP = Ptr(_barkCos);
                AllocatedBytes = (long)_books.Length * sizeof(Book) + (long)_floors.Length * sizeof(Floor)
                               + (long)_residues.Length * sizeof(Residue) + (long)_mappings.Length * sizeof(Mapping)
                               + _modes.Length * 2L + (_fast.Length + _sorted.Length + _sortedEntry.Length) * 4L
                               + _vq.Length * 4L + _classMap.Length + (_barkMap.Length + _barkCos.Length) * 4L;
                return true;
            }

            bool ParseBook(ref BitReader r, Book* b, ref int fastLen, ref int sortedLen, ref int vqLen)
            {
                *b = default;
                if (r.Read(24) != 0x56_4342) return false;                          // "BCV"
                int dims = (int)r.Read(16), entries = (int)r.Read(24);
                if (r.Overrun || (dims == 0 && entries != 0)) return false;
                b->Dims = dims;
                b->Entries = entries;
                b->VqOff = -1;
                GrowScratch(ref _lens, entries);
                GrowScratch(ref _codes, entries);
                Span<sbyte> lens = _lens.AsSpan(0, entries);
                bool ordered = r.Read(1) != 0;
                if (!ordered)
                {
                    bool sparse = r.Read(1) != 0;
                    for (int j = 0; j < entries; j++)
                        lens[j] = !sparse || r.Read(1) != 0 ? (sbyte)(r.Read(5) + 1) : (sbyte)0;
                }
                else
                {
                    int cur = (int)r.Read(5) + 1, e = 0;
                    while (e < entries)
                    {
                        int num = (int)r.Read(ILog(entries - e));
                        if (r.Overrun || e + num > entries || (num > 0 && cur > MaxCodeLen)) return false;
                        lens.Slice(e, num).Fill((sbyte)cur);
                        e += num;
                        cur++;
                    }
                }
                if (r.Overrun) return false;
                Span<uint> codes = _codes.AsSpan(0, entries);
                if (!BuildCodewords(lens, codes)) return false;
                int longCount = 0;
                for (int j = 0; j < entries; j++) if (lens[j] > FastBits) longCount++;
                Grow(ref _fast, fastLen + FastSize);
                Grow(ref _sorted, sortedLen + longCount);
                Grow(ref _sortedEntry, sortedLen + longCount);
                BuildTables(lens, codes, _fast.AsSpan(fastLen, FastSize), _sorted.AsSpan(sortedLen, longCount),
                            _sortedEntry.AsSpan(sortedLen, longCount), out int sortedCount, out int maxLen);
                b->FastOff = fastLen;
                b->SortedOff = sortedLen;
                b->SortedCount = sortedCount;
                b->MaxLen = maxLen;
                fastLen += FastSize;
                sortedLen += sortedCount;

                int lookup = (int)r.Read(4);
                if (lookup > 2) return false;
                b->LookupType = (byte)lookup;
                if (lookup == 0) return !r.Overrun;
                float min = Float32Unpack(r.Read(32)), delta = Float32Unpack(r.Read(32));
                int valueBits = (int)r.Read(4) + 1;
                bool seq = r.Read(1) != 0;
                long lookupValues = lookup == 1 ? Lookup1Values(entries, dims) : (long)entries * dims;
                if (r.Overrun || lookupValues > (1 << 26) || (entries > 0 && lookupValues <= 0)) return false;
                GrowScratch(ref _mults, (int)lookupValues);
                for (int j = 0; j < lookupValues; j++) _mults[j] = r.Read(valueBits);
                if (r.Overrun) return false;
                long total = (long)entries * dims;
                if (vqLen + total > (1 << 25)) return false;                        // 128 MB of VQ is not a real file
                Grow(ref _vq, vqLen + (int)total);
                Span<float> vq = _vq.AsSpan(vqLen, (int)total);
                for (int e = 0; e < entries; e++)
                {
                    float last = 0;
                    long div = 1;
                    for (int d = 0; d < dims; d++)
                    {
                        long off = lookup == 1 ? (e / div) % lookupValues : (long)e * dims + d;
                        float val = _mults[off] * delta + min + last;
                        vq[e * dims + d] = val;
                        if (seq) last = val;
                        if (lookup == 1 && div <= entries) div *= lookupValues;
                    }
                }
                b->VqOff = vqLen;
                vqLen += (int)total;
                return true;
            }

            bool ParseFloor(ref BitReader r, Floor* f, ref int barkLen)
            {
                *f = default;
                int type = (int)r.Read(16);
                if (type == 0)
                {
                    f->Type = 0;
                    f->Order = (int)r.Read(8);
                    f->Rate = (int)r.Read(16);
                    f->BarkMapSize = (int)r.Read(16);
                    f->AmplitudeBits = (int)r.Read(6);
                    f->AmplitudeOffset = (int)r.Read(8);
                    f->BookCount = (int)r.Read(4) + 1;
                    for (int j = 0; j < f->BookCount; j++)
                    {
                        int bk = (int)r.Read(8);
                        if (bk >= _bookCount) return false;
                        f->BookList[j] = (byte)bk;
                    }
                    if (r.Overrun || f->Order < 1 || f->Rate < 1 || f->BarkMapSize < 1) return false;
                    // the bark maps and cos(ω) for both block sizes (§6.2.3), so the packet loop only multiplies
                    int h0 = _n0 >> 1, h1 = _n1 >> 1;
                    Grow(ref _barkMap, barkLen + h0 + h1);
                    Grow(ref _barkCos, barkLen + h0 + h1);
                    f->BarkOff0 = barkLen;
                    FillBark(f, h0, barkLen);
                    f->BarkOff1 = barkLen + h0;
                    FillBark(f, h1, barkLen + h0);
                    barkLen += h0 + h1;
                    return true;
                }
                if (type != 1) return false;
                f->Type = 1;
                f->Partitions = (int)r.Read(5);
                int maxClass = -1;
                for (int i = 0; i < f->Partitions; i++)
                {
                    int c = (int)r.Read(4);
                    f->PartitionClass[i] = (byte)c;
                    if (c > maxClass) maxClass = c;
                }
                for (int c = 0; c <= maxClass; c++)
                {
                    f->ClassDims[c] = (byte)(r.Read(3) + 1);
                    f->ClassSubclasses[c] = (byte)r.Read(2);
                    if (f->ClassSubclasses[c] != 0)
                    {
                        int mb = (int)r.Read(8);
                        if (mb >= _bookCount) return false;
                        f->ClassMasterbook[c] = (byte)mb;
                    }
                    for (int j = 0; j < 1 << f->ClassSubclasses[c]; j++)
                    {
                        int bk = (int)r.Read(8) - 1;
                        if (bk >= _bookCount) return false;
                        f->SubclassBooks[c * 8 + j] = (short)bk;
                    }
                }
                f->Multiplier = (int)r.Read(2) + 1;
                f->RangeBits = (int)r.Read(4);
                f->X[0] = 0;
                f->X[1] = (ushort)(1 << f->RangeBits);
                int values = 2;
                for (int i = 0; i < f->Partitions; i++)
                {
                    int cls = f->PartitionClass[i];
                    for (int j = 0; j < f->ClassDims[cls]; j++)
                    {
                        if (values >= MaxPosts) return false;
                        f->X[values++] = (ushort)r.Read(f->RangeBits);
                    }
                }
                f->Values = values;
                if (r.Overrun) return false;
                return PrepareFloor1(f);
            }

            void FillBark(Floor* f, int n, int at)
            {
                static double Bark(double x) => 13.1 * Math.Atan(0.00074 * x) + 2.24 * Math.Atan(0.0000000185 * x * x)
                                               + 0.0001 * x;
                double top = Bark(0.5 * f->Rate);
                for (int i = 0; i < n; i++)
                {
                    int m = (int)Math.Floor(Bark((double)f->Rate * i / (2.0 * n)) * f->BarkMapSize / top);
                    if (m > f->BarkMapSize - 1) m = f->BarkMapSize - 1;
                    _barkMap[at + i] = m;
                    _barkCos[at + i] = (float)Math.Cos(Math.PI * m / f->BarkMapSize);
                }
            }

            bool ParseResidue(ref BitReader r, Residue* res, ref int classLen)
            {
                *res = default;
                int type = (int)r.Read(16);
                if (type > 2) return false;
                res->Type = (byte)type;
                res->Begin = (int)r.Read(24);
                res->End = (int)r.Read(24);
                res->PartitionSize = (int)r.Read(24) + 1;
                res->Classifications = (int)r.Read(6) + 1;
                res->Classbook = (int)r.Read(8);
                if (r.Overrun || res->Classbook >= _bookCount || res->Begin > res->End) return false;
                for (int c = 0; c < res->Classifications; c++)
                {
                    int low = (int)r.Read(3);
                    int high = r.Read(1) != 0 ? (int)r.Read(5) : 0;
                    res->Cascade[c] = (byte)(high * 8 + low);
                }
                for (int c = 0; c < res->Classifications; c++)
                    for (int j = 0; j < 8; j++)
                    {
                        short bk = -1;
                        if ((res->Cascade[c] & (1 << j)) != 0)
                        {
                            int v = (int)r.Read(8);
                            if (v >= _bookCount) return false;
                            Book* vb = _booksP + v;
                            if (vb->VqOff < 0 || vb->Dims <= 0) return false;      // a residue book must have VQ
                            bk = (short)v;
                        }
                        res->Books[c * 8 + j] = bk;
                    }
                if (r.Overrun) return false;
                // the classification map: entry → the classwords its codeword unpacks to, most significant first
                Book* cb = _booksP + res->Classbook;
                int cw = cb->Dims;
                if (cw <= 0 || (long)cb->Entries * cw > (1 << 24)) return false;
                Grow(ref _classMap, classLen + cb->Entries * cw);
                res->ClassMapOff = classLen;
                for (int e = 0; e < cb->Entries; e++)
                {
                    int t = e;
                    for (int k = cw - 1; k >= 0; k--)
                    {
                        _classMap[classLen + e * cw + k] = (byte)(t % res->Classifications);
                        t /= res->Classifications;
                    }
                }
                classLen += cb->Entries * cw;
                return true;
            }

            bool ParseMapping(ref BitReader r, Mapping* m)
            {
                *m = default;
                if (r.Read(16) != 0) return false;
                m->Submaps = r.Read(1) != 0 ? (int)r.Read(4) + 1 : 1;
                m->CouplingSteps = r.Read(1) != 0 ? (int)r.Read(8) + 1 : 0;
                int bits = ILog(_ch - 1);
                for (int s = 0; s < m->CouplingSteps; s++)
                {
                    int mag = (int)r.Read(bits), ang = (int)r.Read(bits);
                    if (mag == ang || mag >= _ch || ang >= _ch) return false;
                    m->Magnitude[s] = (byte)mag;
                    m->Angle[s] = (byte)ang;
                }
                if (r.Read(2) != 0) return false;                                   // reserved
                for (int c = 0; c < _ch; c++)
                {
                    int mux = m->Submaps > 1 ? (int)r.Read(4) : 0;
                    if (mux >= m->Submaps) return false;
                    m->Mux[c] = (byte)mux;
                }
                for (int s = 0; s < m->Submaps; s++)
                {
                    r.Read(8);                                                     // unused time configuration
                    int fl = (int)r.Read(8), rs = (int)r.Read(8);
                    if (fl >= _floorCount || rs >= _residueCount) return false;
                    m->SubmapFloor[s] = (byte)fl;
                    m->SubmapResidue[s] = (byte)rs;
                }
                return !r.Overrun;
            }

            void AllocateStreamBuffers()
            {
                int ch = _ch, n0 = _n0, n1 = _n1, h0 = n0 >> 1, h1 = n1 >> 1;
                // IMDCT + window tables: per size A n/2, B n/2, C n/4, w n/2, wr n/2
                int t0 = h0 + h0 + (n0 >> 2) + h0 + h0, t1 = h1 + h1 + (n1 >> 2) + h1 + h1;
                Grow(ref _tables, t0 + t1);
                Grow(ref _rev, (n0 >> 3) + (n1 >> 3));
                Span<float> tab = _tables.AsSpan();
                int at = 0;
                ComputeImdctTables(n0, tab.Slice(at, h0), tab.Slice(at + h0, h0), tab.Slice(at + 2 * h0, n0 >> 2),
                                   _rev.AsSpan(0, n0 >> 3));
                ComputeWindow(n0, tab.Slice(at + 2 * h0 + (n0 >> 2), h0), tab.Slice(at + 3 * h0 + (n0 >> 2), h0));
                at = t0;
                ComputeImdctTables(n1, tab.Slice(at, h1), tab.Slice(at + h1, h1), tab.Slice(at + 2 * h1, n1 >> 2),
                                   _rev.AsSpan(n0 >> 3, n1 >> 3));
                ComputeWindow(n1, tab.Slice(at + 2 * h1 + (n1 >> 2), h1), tab.Slice(at + 3 * h1 + (n1 >> 2), h1));
                float* tp = Ptr(_tables);
                _a0 = tp; _b0 = tp + h0; _c0 = tp + 2 * h0; _w0 = tp + 2 * h0 + (n0 >> 2); _wr0 = _w0 + h0;
                _a1 = tp + t0; _b1 = _a1 + h1; _c1 = _a1 + 2 * h1; _w1 = _a1 + 2 * h1 + (n1 >> 2); _wr1 = _w1 + h1;
                _rev0 = Ptr(_rev);
                _rev1 = _rev0 + (n0 >> 3);

                Grow(ref _buf, ch * n1);
                Grow(ref _prev, ch * h1);
                Grow(ref _buf2, h1);
                Grow(ref _y, ch * MaxPosts);
                Grow(ref _final, ch * MaxPosts);
                bool anyFloor0 = false;
                for (int i = 0; i < _floorCount; i++) if (_floorsP[i].Type == 0) anyFloor0 = true;
                if (anyFloor0) Grow(ref _f0, ch * Floor0Stride);
                Grow(ref _flags, 3 * ch);
                Grow(ref _submapSpec, ch);
                Grow(ref _output, n1 * OutputChannels);
                Grow(ref _weights, 2 * ch);
                // residue classification scratch: type 2 → one stream of (partitions + cw); types 0/1 → ch of them
                int need = 1;
                for (int i = 0; i < _residueCount; i++)
                {
                    Residue* res = _residuesP + i;
                    int cw = _booksP[res->Classbook].Dims;
                    int size = res->Type == 2 ? ch * h1 : h1;
                    int b = res->Begin < size ? res->Begin : size, e = res->End < size ? res->End : size;
                    int parts = (e - b) / res->PartitionSize + cw;
                    int total = res->Type == 2 ? parts : ch * parts;
                    if (total > need) need = total;
                }
                Grow(ref _partClass, need);

                _bufP = Ptr(_buf);
                _prevP = Ptr(_prev);
                _buf2P = Ptr(_buf2);
                _yP = Ptr(_y);
                _finalP = Ptr(_final);
                _f0P = Ptr(_f0);
                _floorUnusedP = Ptr(_flags);
                _noResidueP = _floorUnusedP + ch;
                _submapNoP = _floorUnusedP + 2 * ch;
                _submapSpecP = Ptr(_submapSpec);
                _partClassP = Ptr(_partClass);
                _outputP = Ptr(_output);
                _klP = Ptr(_weights);
                _krP = _klP + ch;
                DownmixWeights(ch, _weights.AsSpan(0, ch), _weights.AsSpan(ch, ch));
                _dbP = Ptr(s_floor1Db);
                new Span<float>(_prevP, ch * h1).Clear();
                AllocatedBytes += (_tables.Length + _buf.Length + _prev.Length + _buf2.Length + _f0.Length
                                 + _output.Length + _weights.Length) * 4L + _rev.Length * 2L
                                + (_y.Length + _final.Length) * 4L + _flags.Length + _submapSpec.Length * 8L
                                + _partClass.Length;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static T* Ptr<T>(T[] a) where T : unmanaged
                => (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a));

            /// <summary>Grow-only, pinned, contents kept: the arrays hot loops address by pointer.</summary>
            static void Grow<T>(ref T[] a, int need) where T : unmanaged
            {
                if (a.Length >= need) return;
                long size = Math.Max(need, (long)a.Length * 2);
                if (size > Array.MaxLength) size = need;
                var n = GC.AllocateUninitializedArray<T>((int)size, pinned: true);
                a.AsSpan().CopyTo(n);
                a = n;
            }

            /// <summary>Grow-only, not pinned: setup-parse scratch.</summary>
            static void GrowScratch<T>(ref T[] a, int need)
            {
                if (a.Length >= need) return;
                a = new T[Math.Max(need, a.Length * 2)];
            }
        }
    }
}
