// ── Playback/Playback.Audio.Ogg.cs ─────────────────────────────────────────────────────────────────────────────────
// The Ogg container, from scratch: page sync + header parse + CRC-32, lacing → packets across page boundaries,
// the bounded page index, the seek planner, and the granule ↔ sample arithmetic
//
// Role: CORE
// Owner: V
// Wave: 3-parallel (pure over spans, no wave dependency)
// Budget: 600 lines (over by ~250: the CRC's slicing-by-8 tables, `PageIndex` — which §4.2 describes in prose but
//         does not write — and the granule arithmetic §4.4 needs are all here rather than in the SHELL, for the same
//         reason the FLAC planner moved into CORE: a probe count and a lead-in are facts a unit test must be able
//         to assert without a byte source)
// Spec: docs/plans/wavee/wavee-0.3-vorbis-implementation.md §4 + RFC 3533 + Vorbis I §A.2
//
// WHAT THIS FILE IS. A byte window in, pages and packets out. It owns no bytes, no stream, no thread and no engine
// type: the SHELL (`Playback.Audio.cs`, owner H) holds a `byte[]` window over the byte seam and hands this file a
// `ReadOnlySpan<byte>`; `Reader.NextPacket` answers a span that is a ZERO-COPY slice of that window unless the
// packet spans a page boundary, in which case it is assembled into the reader's own 64 KiB buffer. That is what
// makes every fact in `OggTests.cs` a pure fact over a byte array, and it is why the seek planner — `BeginSeek` /
// `TryNextProbe` / `Observe`, the same three names as `Flac.SeekPlan` — lives here: the probe count is the number
// that decides whether a seek is one CDN range request or six, and a number that matters is a number a test pins.
//
// THE CRC IS THE SYNC ORACLE. Ogg's page checksum is the unreflected CRC-32 of the whole page with the checksum
// field read as zero (poly 0x04c11db7, init 0, no final xor — libogg framing.c:110-113 is explicit that this is NOT
// the usual reflected/0xffffffff variant). Audio bytes contain "OggS" often enough that a byte scan alone is not a
// page finder; the CRC is what turns a candidate into a page, which is why `FindPage` verifies before it reports
// and why a probe window can be dropped anywhere in a file and still resolve (stb `vorbis_find_page`:4561-4629).
//
// Rules this file is written under (P1-P16 / C1-C10, relationships_wavee.md §5.11):
//   P8   ZERO allocation after construction. `Reader` allocates one 64 KiB spanning-packet buffer and one page
//        index at construction; `TryParsePage`, `FindPage`, `Crc32`, `BeginSeek`, `TryNextProbe` and `Observe`
//        allocate nothing at all — they are static over spans and a `struct` plan.
//   P9   No LINQ, no closures, no async, no boxing, no exceptions. A short window is `Truncated`, a bad page is
//        `BadCrc`, a lost packet is `Corrupt`: all return values, never throws.
//   P15  The CRC is slicing-by-8 (eight 256-entry tables, built once from the polynomial); the page scan is
//        `Span.IndexOf`, which is already vectorised by the BCL. Nothing else here is per-sample work.
//   C8   The page index is BOUNDED: 4,096 entries, decimated by half when full, so coverage stays uniform and a
//        five-minute scrub can never grow it (NVorbis's is append-only for the life of the reader — plan §1.4).
//
// The reference for anything RFC 3533 leaves implicit is libogg's `framing.c` (`ogg_sync_pageseek` :642-729,
// `ogg_stream_pagein` :775-901, `_packetout` :944-996) and Symphonia's `symphonia-format-ogg`; the plan's §1.7
// table maps each of them to the section below that transliterates it.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The Ogg encapsulation (RFC 3533), as a set of pure functions over spans plus one small class that
    /// owns the packet-assembly buffer and the page index. See the file header for the rules; see the plan §4 for
    /// the derivation.</summary>
    public static unsafe class Ogg
    {
        // ── 1. Constants, the page header, the CRC ──────────────────────────────────────────────────────────────────

        /// <summary>27 header bytes + 255 lacing values + 255 × 255 body bytes = 65,307 (RFC 3533 §6).</summary>
        public const int MaxPageBytes = 27 + 255 + 255 * 255;

        /// <summary>The fixed part of a page header, before the lacing table (RFC 3533 §6).</summary>
        public const int HeaderBytes = 27;

        /// <summary>A page carries at most 255 segments (the segment count is one byte).</summary>
        public const int MaxSegments = 255;

        /// <summary>Header type flags (RFC 3533 §6): the body starts with the tail of a packet from the previous
        /// page / this is the first page of the logical stream / this is the last.</summary>
        public const byte FlagContinued = 0x01, FlagBos = 0x02, FlagEos = 0x04;

        /// <summary>Where the checksum lives in the header; the four bytes are read as zero when it is computed.</summary>
        public const int CrcFieldOffset = 22;

        /// <summary>A granule position of −1 means "no packet ends on this page" (RFC 3533 §6).</summary>
        public const long NoGranule = -1;

        public enum PageResult : byte { Ok, NotSync, Truncated, BadCrc, BadVersion }

        /// <summary>One parsed page header. <see cref="Granule"/> is −1 when no packet ends on the page.</summary>
        public struct Page
        {
            /// <summary>Offset of "OggS" in the window the page was parsed from.</summary>
            public int At;
            /// <summary>27 + <see cref="Segments"/>.</summary>
            public int HeaderLen;
            /// <summary>Σ of the lacing table.</summary>
            public int BodyLen;
            public long Granule;
            public uint Serial;
            public uint Sequence;
            public byte Flags;
            public byte Segments;

            public readonly int Length => HeaderLen + BodyLen;
            public readonly int BodyAt => At + HeaderLen;
            public readonly int LacingAt => At + HeaderBytes;
            public readonly bool Continued => (Flags & FlagContinued) != 0;
            public readonly bool Bos => (Flags & FlagBos) != 0;
            public readonly bool Eos => (Flags & FlagEos) != 0;
        }

        // CRC-32, poly 0x04c11db7, direct (NON-reflected), init 0, no final xor (RFC 3533 §6; libogg framing.c:110).
        // Slicing-by-8: T0[x] is the register after byte x from zero; Ts[x] is T(s-1)[x] pushed one more zero byte
        // through T0. Eight bytes then cost eight table loads and seven xors instead of eight shift+load pairs.
        const uint Poly = 0x04C11DB7u;
        static readonly uint[] s_crc = BuildCrcTables();

        static uint[] BuildCrcTables()
        {
            var t = new uint[8 * 256];
            for (int i = 0; i < 256; i++)
            {
                uint c = (uint)i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x8000_0000u) != 0 ? (c << 1) ^ Poly : c << 1;
                t[i] = c;
            }
            for (int i = 0; i < 256; i++)
            {
                uint c = t[i];
                for (int s = 1; s < 8; s++)
                {
                    c = (c << 8) ^ t[(int)(c >> 24)];
                    t[s * 256 + i] = c;
                }
            }
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint CrcUpdate(uint crc, byte* p, int len, uint* t)
        {
            int i = 0;
            for (; len - i >= 8; i += 8)
            {
                crc ^= ((uint)p[i] << 24) | ((uint)p[i + 1] << 16) | ((uint)p[i + 2] << 8) | p[i + 3];
                crc = t[7 * 256 + (int)(crc >> 24)]
                    ^ t[6 * 256 + (int)((crc >> 16) & 0xFF)]
                    ^ t[5 * 256 + (int)((crc >> 8) & 0xFF)]
                    ^ t[4 * 256 + (int)(crc & 0xFF)]
                    ^ t[3 * 256 + p[i + 4]]
                    ^ t[2 * 256 + p[i + 5]]
                    ^ t[1 * 256 + p[i + 6]]
                    ^ t[0 * 256 + p[i + 7]];
            }
            for (; i < len; i++) crc = (crc << 8) ^ t[(int)(crc >> 24) ^ p[i]];
            return crc;
        }

        /// <summary>The Ogg CRC-32 of <paramref name="data"/>. <c>Crc32("123456789"u8) == 0x89A1897F</c> — CRC-32/CKSUM's
        /// catalogue check value without its final xor, which is exactly the Ogg variant.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;
            fixed (uint* t = s_crc)
            fixed (byte* p = data)
                return CrcUpdate(0, p, data.Length, t);
        }

        /// <summary>The Ogg CRC-32 of a whole page with the four checksum bytes at <paramref name="crcFieldAt"/> read
        /// as zero (libogg framing.c:237-253). Pass a negative offset for a plain CRC.</summary>
        public static uint Crc32(ReadOnlySpan<byte> page, int crcFieldAt)
        {
            if (crcFieldAt < 0) return Crc32(page);
            if (page.Length < crcFieldAt + 4) return Crc32(page);
            fixed (uint* t = s_crc)
            fixed (byte* p = page)
            {
                uint crc = crcFieldAt > 0 ? CrcUpdate(0, p, crcFieldAt, t) : 0;
                for (int k = 0; k < 4; k++) crc = (crc << 8) ^ t[(int)(crc >> 24)];      // the zeroed checksum field
                int rest = page.Length - (crcFieldAt + 4);
                if (rest > 0) crc = CrcUpdate(crc, p + crcFieldAt + 4, rest, t);
                return crc;
            }
        }

        /// <summary>Cheap candidate test: the capture pattern and a version this decoder understands are present at
        /// <paramref name="at"/>. Says nothing about the CRC — <see cref="TryParsePage"/> is the judge.</summary>
        public static bool LooksLikePage(ReadOnlySpan<byte> win, int at)
        {
            if (at < 0 || at + 5 > win.Length) return false;
            return win[at] == (byte)'O' && win[at + 1] == (byte)'g' && win[at + 2] == (byte)'g'
                && win[at + 3] == (byte)'S' && win[at + 4] == 0;
        }

        /// <summary>Parse the page at <paramref name="win"/>[<paramref name="at"/>]. <see cref="PageResult.Truncated"/>
        /// ⇒ the caller needs more bytes at the SAME offset; <see cref="PageResult.BadCrc"/> / NotSync / BadVersion ⇒
        /// the caller scans on by one byte. The CRC is over the whole page with the checksum field zeroed
        /// (libogg framing.c:642-729).</summary>
        public static PageResult TryParsePage(ReadOnlySpan<byte> win, int at, out Page p)
        {
            p = default;
            if (at < 0 || at > win.Length) return PageResult.NotSync;
            if (at + HeaderBytes > win.Length)
                return LooksLikePartialCapture(win, at) ? PageResult.Truncated : PageResult.NotSync;
            if (win[at] != (byte)'O' || win[at + 1] != (byte)'g' || win[at + 2] != (byte)'g' || win[at + 3] != (byte)'S')
                return PageResult.NotSync;
            if (win[at + 4] != 0) return PageResult.BadVersion;
            int segs = win[at + 26];
            int hdr = HeaderBytes + segs;
            if (at + hdr > win.Length) return PageResult.Truncated;
            int body = 0;
            for (int i = 0; i < segs; i++) body += win[at + HeaderBytes + i];
            if (at + hdr + body > win.Length) return PageResult.Truncated;
            ReadOnlySpan<byte> whole = win.Slice(at, hdr + body);
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(whole.Slice(CrcFieldOffset, 4));
            if (Crc32(whole, CrcFieldOffset) != stored) return PageResult.BadCrc;
            p.At = at;
            p.HeaderLen = hdr;
            p.BodyLen = body;
            p.Flags = win[at + 5];
            p.Granule = BinaryPrimitives.ReadInt64LittleEndian(win.Slice(at + 6, 8));
            p.Serial = BinaryPrimitives.ReadUInt32LittleEndian(win.Slice(at + 14, 4));
            p.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(win.Slice(at + 18, 4));
            p.Segments = (byte)segs;
            return PageResult.Ok;
        }

        /// <summary>A capture pattern that runs off the end of the window is a truncation, not a miss.</summary>
        static bool LooksLikePartialCapture(ReadOnlySpan<byte> win, int at)
        {
            ReadOnlySpan<byte> sync = "OggS"u8;
            int have = win.Length - at;
            if (have <= 0) return false;
            int n = have < 4 ? have : 4;
            for (int i = 0; i < n; i++) if (win[at + i] != sync[i]) return false;
            return true;
        }

        /// <summary>The next VALID page at or after <paramref name="from"/>: a scan for "OggS" whose CRC checks out
        /// (stb <c>vorbis_find_page</c> :4561-4629). Returns the page's offset in the window, or −1 when there is
        /// none; <paramref name="truncatedAt"/> ≥ 0 when a page STARTS in the window but does not end in it — the
        /// caller refills from exactly there and tries again.</summary>
        public static int FindPage(ReadOnlySpan<byte> win, int from, out Page p, out int truncatedAt)
        {
            p = default;
            truncatedAt = -1;
            if (from < 0) from = 0;
            for (int i = from; i < win.Length; i++)
            {
                if (win[i] != (byte)'O')
                {
                    int j = win[(i + 1)..].IndexOf((byte)'O');
                    if (j < 0) return -1;
                    i = i + 1 + j;
                }
                PageResult r = TryParsePage(win, i, out p);
                if (r == PageResult.Ok) return i;
                if (r == PageResult.Truncated) { truncatedAt = i; return -1; }
            }
            return -1;
        }

        /// <summary>Walk a parsed page's lacing table and hand back the packets that END on it. <paramref name="starts"/>
        /// is each packet's offset in the window (for a packet continued from the previous page that is the offset of
        /// its TAIL, not of the packet). Returns the count written, or −1 when the spans do not fit.
        /// <paramref name="lastContinues"/> is true when the page's final packet runs on into the next page.</summary>
        public static int PacketSpans(ReadOnlySpan<byte> win, in Page p, Span<int> starts, Span<int> lengths,
                                      out bool lastContinues)
        {
            lastContinues = false;
            int n = 0, pos = p.BodyAt, len = 0, lace = p.LacingAt;
            for (int s = 0; s < p.Segments; s++)
            {
                int v = win[lace + s];
                len += v;
                if (v < 255)
                {
                    if (n >= starts.Length || n >= lengths.Length) return -1;
                    starts[n] = pos;
                    lengths[n] = len;
                    n++;
                    pos += len;
                    len = 0;
                }
            }
            lastContinues = len > 0 || (p.Segments > 0 && win[lace + p.Segments - 1] == 255);
            return n;
        }

        // ── 2. The packet reader ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The reader the SHELL drives. It owns no bytes but its own spanning-packet buffer and the page
        /// index; <see cref="NextPacket"/> answers a span that points INTO the caller's window unless the packet
        /// spans pages. Zero allocation after construction.</summary>
        public sealed class Reader
        {
            readonly byte[] _packet = GC.AllocateUninitializedArray<byte>(MaxPageBytes, pinned: true);
            int _packetLen;                     // bytes of an open (spanning) packet held in _packet
            Page _page;                         // the page being walked
            int _seg;                           // the next lacing index in that page
            int _bodyPos;                       // the window offset of the next packet's first byte
            uint _serial;
            bool _haveSerial;
            uint _expectSeq;
            bool _seqKnown;
            long _lastPageEnd = -1;             // absolute offset just past the last page parsed (contiguity)
            bool _chain;                        // every page since the last INDEXED one was parsed back to back

            /// <summary>Every page this reader has seen or a probe has fed it (§4.2). Bounded; never grows.</summary>
            public PageIndex Index { get; } = new PageIndex(4096);

            /// <summary>Absolute file offset of <c>window[0]</c>.</summary>
            public long WindowOffset;

            /// <summary>The next unread byte in the window. The SHELL may discard everything before it.</summary>
            public int Cursor;

            /// <summary>Granule of the last page whose final packet was returned (−1 until one is).</summary>
            public long LastGranule = -1;

            /// <summary>Absolute offset of the first page this reader parsed (−1 until one is).</summary>
            public long FirstPageOffset = -1;

            /// <summary>The largest page seen, in bytes; the probe window is four times this (§4.2).</summary>
            public int MaxPageSeen = 4096;

            /// <summary>True once a page with the EOS flag has been walked to its end.</summary>
            public bool SawEos;

            /// <summary>The logical stream this reader locked onto (the first serial it saw).</summary>
            public uint Serial => _serial;

            public bool HasSerial => _haveSerial;

            /// <summary>Bytes of a packet held across a page boundary — diagnostics and tests only.</summary>
            public int OpenPacketBytes => _packetLen;

            public enum Next : byte { Packet, NeedMore, Eos, Corrupt }

            /// <summary>Advance to the next packet. <paramref name="packet"/> is valid until the next call or until
            /// the SHELL refills the window. <see cref="Next.NeedMore"/>: refill from <c>WindowOffset + Cursor</c> and
            /// call <see cref="Reposition"/> with that offset — the reader keeps its own spanning bytes across such a
            /// refill, so nothing is lost. <paramref name="granuleAtEnd"/> is the page's granule when this packet is
            /// the last one ENDING on it (Vorbis I §A.2: that granule is the packet's end position), else −1.</summary>
            public Next NextPacket(ReadOnlySpan<byte> win, out ReadOnlySpan<byte> packet, out long granuleAtEnd)
            {
                packet = default;
                granuleAtEnd = NoGranule;
                while (true)
                {
                    if (_seg >= _page.Segments)                                   // need a page
                    {
                        int at = FindPage(win, Cursor, out Page p, out int truncatedAt);
                        if (at < 0)
                        {
                            // keep the last three bytes: "OggS" may straddle the window's end
                            Cursor = truncatedAt >= 0 ? truncatedAt
                                   : (win.Length > 3 ? win.Length - 3 : Cursor);
                            return Next.NeedMore;
                        }
                        long abs = WindowOffset + at;
                        _chain = _chain && abs == _lastPageEnd;                               // every byte between parsed
                        _lastPageEnd = abs + p.Length;
                        if (!_haveSerial) { _serial = p.Serial; _haveSerial = true; }
                        else if (p.Serial != _serial) { Cursor = at + p.Length; continue; }   // a second logical stream
                        if (FirstPageOffset < 0) FirstPageOffset = abs;
                        if (_seqKnown && p.Sequence != _expectSeq) _packetLen = 0;            // a hole: drop the open packet
                        _expectSeq = p.Sequence + 1;
                        _seqKnown = true;
                        if (p.Granule >= 0) { Index.Add(abs, p.Granule, contiguous: _chain); _chain = true; }
                        if (p.Length > MaxPageSeen) MaxPageSeen = p.Length;
                        if (!p.Continued) _packetLen = 0;                                     // cannot continue a packet
                        _page = p;
                        _seg = 0;
                        _bodyPos = p.BodyAt;
                        Cursor = at + p.Length;
                    }

                    int start = _bodyPos, len = 0;
                    bool ended = false;
                    while (_seg < _page.Segments)
                    {
                        int v = win[_page.LacingAt + _seg++];
                        len += v;
                        if (v < 255) { ended = true; break; }
                    }
                    _bodyPos = start + len;
                    if (!ended)
                    {
                        if (_packetLen + len > _packet.Length) { _packetLen = 0; return Next.Corrupt; }
                        win.Slice(start, len).CopyTo(_packet.AsSpan(_packetLen));
                        _packetLen += len;
                        if (_page.Eos) { SawEos = true; return Next.Eos; }
                        continue;                                                            // spans into the next page
                    }

                    bool lastOnPage = _seg >= _page.Segments;
                    if (lastOnPage)
                    {
                        granuleAtEnd = _page.Granule;
                        if (_page.Granule >= 0) LastGranule = _page.Granule;
                        if (_page.Eos) SawEos = true;
                    }
                    if (_packetLen > 0)
                    {
                        if (_packetLen + len > _packet.Length) { _packetLen = 0; return Next.Corrupt; }
                        win.Slice(start, len).CopyTo(_packet.AsSpan(_packetLen));
                        packet = _packet.AsSpan(0, _packetLen + len);
                        _packetLen = 0;
                    }
                    else packet = win.Slice(start, len);
                    return Next.Packet;
                }
            }

            /// <summary>Re-point the window. A CONTINUATION (<paramref name="windowOffset"/> ==
            /// <c>WindowOffset + Cursor</c>, i.e. the SHELL's refill) keeps the bytes of a packet that spans the
            /// boundary; any other offset is a seek and drops them. Either way the page walk restarts, which is
            /// always safe: <see cref="NextPacket"/> only ever returns <see cref="Next.NeedMore"/> when the current
            /// page is exhausted, because a page is never parsed until all of its body is in the window.</summary>
            public void Reposition(long windowOffset)
            {
                bool continuing = windowOffset == WindowOffset + Cursor;
                WindowOffset = windowOffset;
                Cursor = 0;
                _seg = 0;
                _page = default;
                _bodyPos = 0;
                if (!continuing) { _packetLen = 0; _seqKnown = false; _chain = false; }
            }

            /// <summary>Forget the stream entirely (a new file on the same reader). The index is NOT cleared — call
            /// <c>Index.Clear()</c> for that.</summary>
            public void Reset()
            {
                WindowOffset = 0;
                Cursor = 0;
                _seg = 0;
                _page = default;
                _bodyPos = 0;
                _packetLen = 0;
                _haveSerial = false;
                _seqKnown = false;
                _chain = false;
                _lastPageEnd = -1;
                _serial = 0;
                LastGranule = NoGranule;
                FirstPageOffset = -1;
                MaxPageSeen = 4096;
                SawEos = false;
            }
        }

        // ── 3. The page index ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every page offset and granule the reader has passed or a probe has seen, sorted by offset and
        /// (in a well-formed stream) by granule. BOUNDED: at <see cref="Capacity"/> entries every other one is
        /// dropped, which halves the resolution and keeps coverage uniform — the array never grows (C8). A seek back
        /// into what has played resolves from here with ZERO probes (§4.5).</summary>
        public sealed class PageIndex
        {
            public struct Entry
            {
                public long Offset;
                public long Granule;
                /// <summary>True when this page was added immediately after its predecessor in the index with no
                /// page in between — i.e. the two bracket a region that is fully known.</summary>
                public bool Contiguous;
            }

            readonly Entry[] _e;
            int _n;

            public PageIndex(int capacity) => _e = new Entry[capacity < 8 ? 8 : capacity];

            public int Count => _n;
            public int Capacity => _e.Length;
            public void Clear() => _n = 0;
            public Entry this[int i] => _e[i];
            public long FirstOffset => _n > 0 ? _e[0].Offset : -1;
            public long LastOffset => _n > 0 ? _e[_n - 1].Offset : -1;
            public long LastGranule => _n > 0 ? _e[_n - 1].Granule : NoGranule;

            /// <summary>Record a page. An append (playing forward) is O(1); a probe into unseen territory is a
            /// binary-search insert. A duplicate offset is ignored.</summary>
            public void Add(long offset, long granule, bool contiguous)
            {
                if (offset < 0 || granule < 0) return;
                if (_n > 0)
                {
                    long last = _e[_n - 1].Offset;
                    if (offset == last) return;
                    if (offset > last)
                    {
                        if (_n == _e.Length) Decimate();
                        _e[_n].Offset = offset;
                        _e[_n].Granule = granule;
                        _e[_n].Contiguous = contiguous && _n > 0;
                        _n++;
                        return;
                    }
                }
                else
                {
                    _e[0].Offset = offset;
                    _e[0].Granule = granule;
                    _e[0].Contiguous = false;
                    _n = 1;
                    return;
                }
                int lo = 0, hi = _n - 1, at = _n;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_e[mid].Offset == offset) return;
                    if (_e[mid].Offset < offset) lo = mid + 1; else { at = mid; hi = mid - 1; }
                }
                if (_n == _e.Length) { Decimate(); Add(offset, granule, contiguous); return; }
                Array.Copy(_e, at, _e, at + 1, _n - at);
                _e[at].Offset = offset;
                _e[at].Granule = granule;
                _e[at].Contiguous = contiguous && at > 0;
                if (at + 1 < _n + 1) _e[at + 1].Contiguous = false;                 // a page now sits between them
                _n++;
            }

            /// <summary>Keep every other entry. Contiguity survives only where BOTH links did.</summary>
            void Decimate()
            {
                int w = 0;
                for (int i = 0; i < _n; i += 2)
                {
                    bool contig = _e[i].Contiguous && (i == 0 || _e[i - 1].Contiguous);
                    _e[w].Offset = _e[i].Offset;
                    _e[w].Granule = _e[i].Granule;
                    _e[w].Contiguous = w > 0 && contig;
                    w++;
                }
                _n = w;
            }

            /// <summary>The tightest known pair of page starts around <paramref name="granule"/>: <paramref name="lo"/>
            /// is a page whose granule is ≤ it (the target lies after it), <paramref name="hi"/> the first page whose
            /// granule is &gt; it (the target's samples end on it). False when the
            /// index cannot bracket the target at all.</summary>
            public bool Bracket(long granule, out long lo, out long loG, out long hi, out long hiG)
            {
                lo = hi = -1;
                loG = hiG = NoGranule;
                if (_n == 0) return false;
                int l = 0, h = _n - 1, li = -1, hidx = -1;
                while (l <= h)
                {
                    int mid = (l + h) >> 1;
                    if (_e[mid].Granule <= granule) { li = mid; l = mid + 1; } else h = mid - 1;
                }
                l = 0; h = _n - 1;
                while (l <= h)
                {
                    int mid = (l + h) >> 1;
                    if (_e[mid].Granule > granule) { hidx = mid; h = mid - 1; } else l = mid + 1;
                }
                if (li < 0 && hidx < 0) return false;
                if (li >= 0) { lo = _e[li].Offset; loG = _e[li].Granule; }
                if (hidx >= 0) { hi = _e[hidx].Offset; hiG = _e[hidx].Granule; }
                return li >= 0 && hidx >= 0;
            }

            /// <summary>True when <paramref name="hi"/> is the very next page after <paramref name="lo"/> AND both
            /// came from one contiguous parse — the "seek back into what we played" case, which costs no probe.</summary>
            public bool Adjacent(long lo, long hi)
            {
                if (lo < 0 || hi <= lo) return false;
                int l = 0, h = _n - 1;
                while (l <= h)
                {
                    int mid = (l + h) >> 1;
                    if (_e[mid].Offset == lo)
                        return mid + 1 < _n && _e[mid + 1].Offset == hi && _e[mid + 1].Contiguous;
                    if (_e[mid].Offset < lo) l = mid + 1; else h = mid - 1;
                }
                return false;
            }
        }

        // ── 4. The seek planner ─────────────────────────────────────────────────────────────────────────────────────

        public enum SeekTier : byte { Index, Estimate, Bisect, Linear }
        public enum ProbeResult : byte { Continue, Found, NoPage }

        /// <summary>The seek state machine (§4.2). Drivable by anything that can read bytes at an offset; the loop
        /// lives here so a probe count is a unit-test fact. Buffers: none.</summary>
        public struct SeekPlan
        {
            /// <summary>The sample asked for, in the granule domain.</summary>
            public long Target;
            /// <summary>The page to resume from once <see cref="Resolved"/> — at or before the page holding Target.</summary>
            public long Offset;
            /// <summary>That page's granule, or −1 when it is not known.</summary>
            public long OffsetGranule;
            public bool Resolved;
            /// <summary>How many windows the caller had to read. The number §7.2 pins.</summary>
            public int Probes;
            public SeekTier Tier;
            /// <summary>The bracket: page starts whose granules straddle the target.</summary>
            public long Lo, LoGranule, Hi, HiGranule;
            public int WindowBytes;
            public int MaxProbes;
        }

        /// <summary>The probe window: enough for at least one whole page after the landing point plus the page
        /// straddling it. 48 KiB on oggenc-shaped files, growing with the largest page seen, never over 192 KiB.
        /// This is also the first range the stream layer fetches (§5.2).</summary>
        public static int ProbeWindow(int maxPageSeen)
        {
            int want = maxPageSeen <= 0 ? 48 * 1024 : 4 * maxPageSeen;
            return Math.Clamp(want, 48 * 1024, 192 * 1024);
        }

        /// <summary>Plan a seek to <paramref name="target"/> samples. The index narrows first; a stream with no known
        /// length or total can only go linear.</summary>
        public static SeekPlan BeginSeek(PageIndex index, long firstAudioPage, long streamLength, long totalGranules,
                                         long target, int maxPageSeen, int maxProbes = 8)
        {
            SeekPlan p = default;
            p.Target = target < 0 ? 0 : target;
            p.Lo = firstAudioPage < 0 ? 0 : firstAudioPage;
            p.LoGranule = 0;
            p.Hi = streamLength;
            p.HiGranule = totalGranules;
            p.WindowBytes = ProbeWindow(maxPageSeen);
            p.MaxProbes = maxProbes < 1 ? 1 : maxProbes;
            if (index is not null && index.Bracket(p.Target, out long lo, out long loG, out long hi, out long hiG))
            {
                if (lo >= p.Lo) { p.Lo = lo; p.LoGranule = loG; }
                if (p.Hi <= 0 || hi < p.Hi) { p.Hi = hi; p.HiGranule = hiG; }
                p.Tier = SeekTier.Index;
            }
            else p.Tier = p.HiGranule > 0 ? SeekTier.Estimate : SeekTier.Linear;
            p.Offset = p.Lo;
            p.OffsetGranule = p.LoGranule;
            // Two adjacent indexed pages around the target: the page is known, no probe at all.
            p.Resolved = p.Tier == SeekTier.Index && index!.Adjacent(p.Lo, p.Hi);
            return p;
        }

        /// <summary>The next window to read: <c>[offset, offset + WindowBytes)</c>. False when done — resolved, out of
        /// probes, or a bracket small enough to scan linearly from <see cref="SeekPlan.Lo"/>.</summary>
        public static bool TryNextProbe(ref SeekPlan plan, out long offset)
        {
            offset = plan.Offset;
            if (plan.Resolved || plan.HiGranule <= 0 || plan.Probes >= plan.MaxProbes) return false;
            if (plan.Hi - plan.Lo <= plan.WindowBytes) { plan.Tier = SeekTier.Linear; return false; }
            // Interpolate inside the CURRENT bracket: every Observe moved Lo or Hi to a real page with a real
            // granule, so the second estimate is corrected by what the first one saw (libvorbisfile's bisect
            // formula, vorbisfile.c:1464-1468, the bracket doing what stb's explicit probe-1 error term does).
            long est;
            long span = plan.HiGranule - plan.LoGranule;
            if (plan.Probes >= 2 || span <= 0) est = plan.Lo + (plan.Hi - plan.Lo) / 2;   // plain bisection: bounded
            else est = plan.Lo + (long)((double)(plan.Target - plan.LoGranule) / span * (plan.Hi - plan.Lo));
            long backoff = plan.WindowBytes / 4;                                          // land BEFORE the page
            long ceiling = plan.Hi - plan.WindowBytes;
            if (ceiling < plan.Lo) ceiling = plan.Lo;
            offset = Math.Clamp(est - backoff, plan.Lo, ceiling);
            plan.Probes++;
            plan.Tier = plan.Probes > 2 ? SeekTier.Bisect : SeekTier.Estimate;
            return true;
        }

        /// <summary>Feed the window read at <paramref name="windowOffset"/> back. Every page in it goes into the
        /// index; the FIRST page whose granule ≥ Target, and whose predecessor's granule is below it, resolves the
        /// plan (Symphonia <c>inspect_page</c>: a probe parses headers and decodes nothing). Otherwise the bracket
        /// narrows to the side the target is on.</summary>
        public static ProbeResult Observe(ref SeekPlan plan, PageIndex index, ReadOnlySpan<byte> window,
                                          long windowOffset)
        {
            int from = 0;
            bool any = false, chain = false;
            long prevAt = -1, prevG = NoGranule, prevEnd = -1;
            while (true)
            {
                int at = FindPage(window, from, out Page p, out _);
                if (at < 0) break;
                any = true;
                long abs = windowOffset + at;
                chain = chain && prevEnd == abs;
                prevEnd = abs + p.Length;
                from = at + p.Length;
                if (p.Granule < 0) continue;
                index?.Add(abs, p.Granule, contiguous: chain);
                chain = true;
                if (p.Granule > plan.Target)
                {
                    // The target's samples end on this page. Resume at the page BEFORE it so a packet primes ahead of
                    // the target's first packet (§4.3). That page is either in this window (the usual case: the
                    // estimate backs off a quarter window) or is the bracket's Lo when the index proves nothing
                    // lies between them; otherwise this probe landed late and only narrows the bracket.
                    if (prevAt >= 0)
                    {
                        plan.Offset = prevAt;
                        plan.OffsetGranule = prevG;
                    }
                    else if (abs <= plan.Lo || (index is not null && index.Adjacent(plan.Lo, abs)))
                    {
                        plan.Offset = abs <= plan.Lo ? abs : plan.Lo;
                        plan.OffsetGranule = abs <= plan.Lo ? NoGranule : plan.LoGranule;
                    }
                    else
                    {
                        if (abs < plan.Hi || plan.Hi <= 0) { plan.Hi = abs; plan.HiGranule = p.Granule; }
                        return ProbeResult.Continue;
                    }
                    plan.Hi = abs;
                    plan.HiGranule = p.Granule;
                    plan.Resolved = true;
                    return ProbeResult.Found;
                }
                if (abs >= plan.Lo) { plan.Lo = abs; plan.LoGranule = p.Granule; }
                plan.Offset = plan.Lo;
                plan.OffsetGranule = plan.LoGranule;
                prevAt = abs;
                prevG = p.Granule;
            }
            return any ? ProbeResult.Continue : ProbeResult.NoPage;
        }

        // ── 5. Granule ↔ sample arithmetic (Vorbis I §A.2) ──────────────────────────────────────────────────────────
        //
        // "The granule position of a page is the PCM sample position of the last completed packet in the page", and
        // a packet's own first sample is that granule minus the frames of the packets that end after it on the page.
        // Two ends of a stream bend that rule and both are expressed here rather than in the SHELL:
        //   • the FIRST audio page may claim FEWER samples than the packets ending on it produced — the excess is
        //     lead-in and belongs to no one (libvorbis block.c:831-843; stb ignores it outright, :17);
        //   • the LAST page may claim fewer than the decoder produced — the excess is the end truncation, which is
        //     what makes `GaplessInfo.ExactFrames` exact (libvorbis block.c:864-936).
        // The "first packet produces no output" rule (spec §1.3.2) is the decoder's, not the container's: it simply
        // means the frames summed here start at the SECOND packet of the stream.

        /// <summary>The absolute sample of the first frame of the packet that ends at <paramref name="pageGranule"/>,
        /// given how many frames that packet produced.</summary>
        public static long FirstSampleOfPacket(long pageGranule, long framesInPacket)
            => pageGranule < 0 ? NoGranule : pageGranule - framesInPacket;

        /// <summary>The lead-in: frames the decoder produces for the first audio page beyond what its granule claims.
        /// Zero when the page accounts for everything (the usual libvorbis case).</summary>
        public static long LeadIn(long firstPageGranule, long framesEndingOnFirstPage)
        {
            if (firstPageGranule < 0) return 0;
            long extra = framesEndingOnFirstPage - firstPageGranule;
            return extra > 0 ? extra : 0;
        }

        /// <summary>How many of <paramref name="frames"/> survive when the stream ends at <paramref name="eosGranule"/>
        /// and the packet starts at <paramref name="positionBefore"/>. The excess is the last page's truncation.</summary>
        public static int TrimTail(long positionBefore, int frames, long eosGranule)
        {
            if (eosGranule < 0) return frames;
            long end = positionBefore + frames;
            if (end <= eosGranule) return frames;
            long keep = eosGranule - positionBefore;
            if (keep <= 0) return 0;
            return keep < frames ? (int)keep : frames;
        }

        /// <summary>The stream's exact length in samples: the last page's granule, or −1 when the tail is unknown.</summary>
        public static long ExactFrames(long lastPageGranule) => lastPageGranule < 0 ? -1 : lastPageGranule;
    }
}
