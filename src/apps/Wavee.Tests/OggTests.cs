// ── Wavee.Tests/OggTests.cs — the gate for the Ogg layer and the seek planner (Wave 3-parallel, owner V) ────────────
//
// `Playback/Playback.Audio.Ogg.cs` is CORE: a byte window in, pages and packets out, plus the page index and the
// `BeginSeek` / `TryNextProbe` / `Observe` planner. Every fact here is a pure fact over a byte array.
//
// WHAT THE FIXTURES CANNOT COVER, THIS FILE BUILDS. ffmpeg's muxer never splits a packet across pages below 255
// segments, never leaves a sequence hole, never interleaves a second serial and never ships a bad CRC. `Page` below
// is a ~20-line Ogg page writer (real lacing, real CRC) that builds each of those, so the reader's spanning-packet
// assembly, its refill contract, its hole and serial handling and its resync are facts, not hopes.
//
// THE PROBE COUNT IS THE NUMBER THAT MATTERS. One probe is one CDN range request (plan §4.5, §5.2). The planner is
// driven here over a counting source exactly the way the adapter drives it, and the bounds are the plan's: a cold
// seek to 70 % in ≤ 2 probes (≤ 3 on the VBR file), a warm seek beside it in ≤ 1, never more than 8.

using Wavee;
using Xunit;
using Ogg = Wavee.Playback.Ogg;

namespace Wavee.Tests;

public class OggTests
{
    static void Print(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    // ── 1. the CRC ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crc32_matches_the_reference()
    {
        // poly 0x04c11db7, init 0, unreflected, no final xor: CRC-32/CKSUM's check 0x765E7680 without its xor
        Assert.Equal(0x89A1897Fu, Ogg.Crc32("123456789"u8));
        Assert.Equal(0u, Ogg.Crc32(ReadOnlySpan<byte>.Empty));

        // slicing-by-8 against a bit-serial reference at every length through three 8-byte rounds and a tail
        uint x = 0x1234_5678;
        for (int len = 1; len <= 40; len++)
        {
            var data = new byte[len];
            for (int i = 0; i < len; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; data[i] = (byte)x; }
            Assert.Equal(BitSerialCrc(data), Ogg.Crc32(data));
            if (len >= 26)
            {
                var zeroed = (byte[])data.Clone();
                zeroed.AsSpan(22, 4).Clear();
                Assert.Equal(BitSerialCrc(zeroed), Ogg.Crc32(data, Ogg.CrcFieldOffset));
            }
        }

        // and every page of every real file checks out against its stored checksum
        foreach (string name in VorbisFixture.All)
        {
            byte[] file = VorbisFixture.Bytes(name);
            int at = 0, pages = 0;
            while (at < file.Length)
            {
                Assert.Equal(Ogg.PageResult.Ok, Ogg.TryParsePage(file, at, out Ogg.Page p));
                uint stored = BitConverter.ToUInt32(file, at + Ogg.CrcFieldOffset);
                Assert.Equal(stored, Ogg.Crc32(file.AsSpan(at, p.Length), Ogg.CrcFieldOffset));
                if (pages == 0) Assert.True(p.Bos);
                at += p.Length;
                pages++;
                if (at == file.Length) Assert.True(p.Eos);
            }
            Assert.Equal(file.Length, at);
        }
    }

    static uint BitSerialCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int k = 0; k < 8; k++) crc = (crc & 0x8000_0000u) != 0 ? (crc << 1) ^ 0x04C1_1DB7u : crc << 1;
        }
        return crc;
    }

    // ── 2. pages and packets ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One Ogg page with a real CRC (RFC 3533 §6).</summary>
    static byte[] Page(uint serial, uint seq, byte flags, long granule, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        var p = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(p);
        p[4] = 0;
        p[5] = flags;
        BitConverter.TryWriteBytes(p.AsSpan(6, 8), granule);
        BitConverter.TryWriteBytes(p.AsSpan(14, 4), serial);
        BitConverter.TryWriteBytes(p.AsSpan(18, 4), seq);
        p[26] = (byte)lacing.Length;
        lacing.CopyTo(p.AsSpan(27));
        body.CopyTo(p.AsSpan(27 + lacing.Length));
        BitConverter.TryWriteBytes(p.AsSpan(22, 4), Ogg.Crc32(p, Ogg.CrcFieldOffset));
        return p;
    }

    static byte[] Payload(int length, int tag)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 7 + tag * 13 + (i >> 8));
        return b;
    }

    static byte[] Lace(params (int Count, byte Value)[] runs)
    {
        var l = new List<byte>();
        foreach (var (count, value) in runs) for (int i = 0; i < count; i++) l.Add(value);
        return [.. l];
    }

    static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] part in parts) all.AddRange(part);
        return [.. all];
    }

    /// <summary>The spanning stream: P0 = 100 B on page 0; P1 = 70,000 B across pages 0, 1 and 2 (100 + 150 + 25
    /// segments); P2 = a zero-length packet; P3 = 510 B, a multiple of 255 closed by a 0 lacing value. Page 2 is EOS.</summary>
    sealed class Spanning
    {
        public readonly byte[] P0 = Payload(100, 0), P1 = Payload(70_000, 1), P3 = Payload(510, 3);
        public readonly byte[] Page0, Page1, Page2;

        public Spanning(uint serial = 42)
        {
            Page0 = Page(serial, 0, Ogg.FlagBos, 1000, Lace((1, 100), (100, 255)),
                         Concat(P0, P1.AsSpan(0, 25_500).ToArray()));
            Page1 = Page(serial, 1, Ogg.FlagContinued, Ogg.NoGranule, Lace((150, 255)),
                         P1.AsSpan(25_500, 38_250).ToArray());
            Page2 = Page(serial, 2, Ogg.FlagContinued | Ogg.FlagEos, 5000, Lace((24, 255), (1, 130), (1, 0), (2, 255), (1, 0)),
                         Concat(P1.AsSpan(63_750).ToArray(), P3));
        }
    }

    [Fact]
    public void Ogg_parser_handles_spanning_holes_and_serials()
    {
        var s = new Spanning();

        // (a) a 70 KB packet spans three pages and comes back whole; granules belong to the last COMPLETED packet
        {
            byte[] stream = Concat(s.Page0, s.Page1, s.Page2);
            var r = new Ogg.Reader();
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p0, out long g0));
            Assert.True(p0.SequenceEqual(s.P0));
            Assert.Equal(1000L, g0);                                  // page 0's last completed packet is P0, not P1
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p1, out long g1));
            Assert.Equal(70_000, p1.Length);
            Assert.True(p1.SequenceEqual(s.P1));
            Assert.Equal(-1L, g1);
            Assert.Equal(0, r.OpenPacketBytes);
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p2, out long g2));
            Assert.Equal(0, p2.Length);                               // a zero-length packet is a packet
            Assert.Equal(-1L, g2);
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p3, out long g3));
            Assert.True(p3.SequenceEqual(s.P3));                      // 2 × 255 closed by a 0
            Assert.Equal(5000L, g3);
            Assert.Equal(Ogg.Reader.Next.Eos, r.NextPacket(stream, out _, out _));
            Assert.Equal(5000L, r.LastGranule);
            Assert.Equal(0L, r.FirstPageOffset);
            Assert.Equal(2, r.Index.Count);                           // pages 0 and 2 carry granules; page 1 does not
        }

        // (b) a sequence hole (page 1 lost) drops the open packet only: P1 is gone, P2 and P3 survive
        {
            byte[] stream = Concat(s.Page0, s.Page2);
            var r = new Ogg.Reader();
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p0, out _));
            Assert.True(p0.SequenceEqual(s.P0));
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p2, out _));
            Assert.Equal(0, p2.Length);
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p3, out long g3));
            Assert.True(p3.SequenceEqual(s.P3));
            Assert.Equal(5000L, g3);
            Assert.Equal(Ogg.Reader.Next.Eos, r.NextPacket(stream, out _, out _));
        }

        // (c) a foreign serial between two of our pages is skipped, even in the middle of a spanning packet
        {
            byte[] foreign = Page(999, 0, Ogg.FlagBos, 7, Lace((1, 5)), Payload(5, 9));
            byte[] stream = Concat(s.Page0, foreign, s.Page1, s.Page2);
            var r = new Ogg.Reader();
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out _, out _));
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p1, out _));
            Assert.True(p1.SequenceEqual(s.P1));
            Assert.Equal(42u, r.Serial);
        }

        // (d) a bad CRC: the page is rejected, the scan resyncs one byte on and finds the next page (a hole)
        {
            byte[] stream = Concat(s.Page0, s.Page1, s.Page2);
            stream[s.Page0.Length + 27 + 150 + 1000] ^= 0x5A;          // one body byte of page 1
            Assert.Equal(Ogg.PageResult.BadCrc, Ogg.TryParsePage(stream, s.Page0.Length, out _));
            var r = new Ogg.Reader();
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p0, out _));
            Assert.True(p0.SequenceEqual(s.P0));
            Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream, out ReadOnlySpan<byte> p2, out _));
            Assert.Equal(0, p2.Length);                               // P1 was lost with page 1
        }

        // (e) truncated windows ask for more at the page's OWN offset
        {
            Assert.Equal(Ogg.PageResult.Truncated, Ogg.TryParsePage(s.Page0.AsSpan(0, 20), 0, out _));
            Assert.Equal(Ogg.PageResult.Truncated, Ogg.TryParsePage(s.Page0.AsSpan(0, 50), 0, out _));
            Assert.Equal(Ogg.PageResult.Truncated, Ogg.TryParsePage(s.Page0.AsSpan(0, s.Page0.Length - 1), 0, out _));
            Assert.Equal(Ogg.PageResult.NotSync, Ogg.TryParsePage(Payload(64, 5), 0, out _));
            byte[] junkThenPart = Concat(Payload(10, 7), s.Page0.AsSpan(0, s.Page0.Length - 1).ToArray());
            Assert.Equal(-1, Ogg.FindPage(junkThenPart, 0, out _, out int truncatedAt));
            Assert.Equal(10, truncatedAt);
            var r = new Ogg.Reader();
            Assert.Equal(Ogg.Reader.Next.NeedMore, r.NextPacket(junkThenPart, out _, out _));
            Assert.Equal(10, r.Cursor);
            Assert.True(Ogg.LooksLikePage(junkThenPart, 10));
            Assert.False(Ogg.LooksLikePage(junkThenPart, 9));
        }
    }

    [Fact]
    public void A_refill_between_pages_keeps_the_spanning_bytes()
    {
        // The SHELL's contract: on NeedMore, keep the window from Cursor, re-point with Reposition(WindowOffset +
        // Cursor) — a CONTINUATION — and the reader still owns the first 25,500 bytes of P1.
        var s = new Spanning();
        byte[] stream = Concat(s.Page0, s.Page1, s.Page2);
        int cut = s.Page0.Length + s.Page1.Length / 2;
        var r = new Ogg.Reader();
        Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(stream.AsSpan(0, cut), out ReadOnlySpan<byte> p0, out _));
        Assert.True(p0.SequenceEqual(s.P0));
        Assert.Equal(Ogg.Reader.Next.NeedMore, r.NextPacket(stream.AsSpan(0, cut), out _, out _));
        Assert.Equal(s.Page0.Length, r.Cursor);                       // the truncated page's own offset
        Assert.Equal(25_500, r.OpenPacketBytes);

        long next = r.WindowOffset + r.Cursor;
        r.Reposition(next);
        ReadOnlySpan<byte> refill = stream.AsSpan((int)next);
        Assert.Equal(Ogg.Reader.Next.Packet, r.NextPacket(refill, out ReadOnlySpan<byte> p1, out _));
        Assert.True(p1.SequenceEqual(s.P1));

        // …whereas a seek (any other offset) forgets them, and an orphan tail is never handed out as a packet
        var seek = new Ogg.Reader();
        Assert.Equal(Ogg.Reader.Next.Packet, seek.NextPacket(stream.AsSpan(0, cut), out _, out _));
        Assert.Equal(Ogg.Reader.Next.NeedMore, seek.NextPacket(stream.AsSpan(0, cut), out _, out _));
        long page2 = s.Page0.Length + s.Page1.Length;
        seek.Reposition(page2);
        Assert.Equal(0, seek.OpenPacketBytes);
        Assert.Equal(Ogg.Reader.Next.Packet, seek.NextPacket(stream.AsSpan((int)page2), out ReadOnlySpan<byte> first, out _));
        Assert.Equal(0, first.Length);                                // P2: P1's tail on page 2 was skipped
    }

    [Fact]
    public void Packet_spans_walk_the_lacing_table()
    {
        var s = new Spanning();
        Span<int> starts = stackalloc int[8];
        Span<int> lengths = stackalloc int[8];

        Assert.Equal(Ogg.PageResult.Ok, Ogg.TryParsePage(s.Page0, 0, out Ogg.Page p0));
        Assert.Equal(101, p0.Segments);
        Assert.Equal(1, Ogg.PacketSpans(s.Page0, in p0, starts, lengths, out bool c0));
        Assert.Equal(p0.BodyAt, starts[0]);
        Assert.Equal(100, lengths[0]);
        Assert.True(c0);

        Assert.Equal(Ogg.PageResult.Ok, Ogg.TryParsePage(s.Page1, 0, out Ogg.Page p1));
        Assert.Equal(0, Ogg.PacketSpans(s.Page1, in p1, starts, lengths, out bool c1));
        Assert.True(c1);
        Assert.True(p1.Continued);
        Assert.Equal(-1L, p1.Granule);

        Assert.Equal(Ogg.PageResult.Ok, Ogg.TryParsePage(s.Page2, 0, out Ogg.Page p2));
        Assert.Equal(3, Ogg.PacketSpans(s.Page2, in p2, starts, lengths, out bool c2));
        Assert.Equal(new[] { 6250, 0, 510 }, lengths[..3].ToArray());
        Assert.False(c2);
        Assert.True(p2.Eos);
    }

    // ── 3. the page index ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Page_index_brackets_and_decimates()
    {
        var index = new Ogg.PageIndex(4096);
        for (int i = 0; i < 5000; i++) index.Add(i * 1000L, i * 4410L, contiguous: true);
        Assert.InRange(index.Count, 2048, 4096);

        long minGap = long.MaxValue, maxGap = 0;
        for (int i = 1; i < index.Count; i++)
        {
            long gap = index[i].Offset - index[i - 1].Offset;
            Assert.True(gap > 0);                                     // still sorted by offset
            Assert.True(index[i].Granule > index[i - 1].Granule);
            minGap = Math.Min(minGap, gap);
            maxGap = Math.Max(maxGap, gap);
            Assert.True(index[i].Contiguous);                         // decimation keeps "fully parsed between"
        }
        Assert.Equal(0L, index.FirstOffset);
        Assert.Equal(4_999_000L, index.LastOffset);
        Assert.True(maxGap <= 2 * minGap, $"coverage gaps {minGap}..{maxGap}");

        // Bracket: the tightest pair around the target, lo.granule ≤ target < hi.granule
        Assert.True(index.Bracket(1_000_000, out long lo, out long loG, out long hi, out long hiG));
        Assert.True(loG <= 1_000_000 && 1_000_000 < hiG);
        Assert.True(index.Adjacent(lo, hi));
        Assert.False(index.Bracket(99_999_999, out _, out _, out _, out _));

        // Adjacent: only for a contiguous parse; a probe's lone page is not, until a parse reaches it back to back
        var small = new Ogg.PageIndex(16);
        small.Add(0, 0, contiguous: false);
        small.Add(1000, 10, contiguous: true);
        small.Add(3000, 30, contiguous: false);
        Assert.True(small.Adjacent(0, 1000));
        Assert.False(small.Adjacent(1000, 3000));
        small.Add(2000, 20, contiguous: false);                       // a probe fills the gap
        Assert.False(small.Adjacent(1000, 2000));
        Assert.False(small.Adjacent(2000, 3000));
        small.Add(2000, 20, contiguous: true);                        // a linear parse reaches it
        small.Add(3000, 30, contiguous: true);
        Assert.True(small.Adjacent(1000, 2000));
        Assert.True(small.Adjacent(2000, 3000));
        Assert.Equal(4, small.Count);
        Assert.False(small.Adjacent(0, 2000));
    }

    // ── 4. the seek planner: bounded probes over a counting source ──────────────────────────────────────────────────

    [Theory]
    [InlineData("pink-320.ogg", 2)]
    [InlineData("pink-96.ogg", 2)]
    [InlineData("vbr-q8.ogg", 3)]
    [InlineData("pages-100ms.ogg", 2)]
    [InlineData("sine-440.ogg", 2)]
    [InlineData("sweep-48k.ogg", 2)]
    public void Seek_probe_count_is_bounded_and_typical_is_one(string name, int coldBound)
    {
        byte[] file = VorbisFixture.Bytes(name);
        var reader = new Ogg.Reader();
        var (_, _, firstAudioPage) = VorbisFixture.Headers(file, reader);
        VorbisFixture.StartPlayback(file, reader, null);              // playback has started: the first page is known
        long total = VorbisFixture.LastGranule(file);
        Assert.True(total > 0);
        var src = new VorbisFixture.CountingSource(file);

        long coldTarget = total * 70 / 100;
        Ogg.SeekPlan cold = VorbisFixture.Plan(file, reader, firstAudioPage, total, coldTarget, src);
        Assert.Equal(cold.Probes, src.Requests);                      // one probe = one range request
        Assert.InRange(cold.Probes, 0, coldBound);
        Assert.True(cold.Offset >= firstAudioPage);
        WalkFrom(file, reader, cold.Offset, coldTarget + 4096);       // the decode after landing indexes its pages

        int requests = src.Requests;
        Ogg.SeekPlan warm = VorbisFixture.Plan(file, reader, firstAudioPage, total, total * 72 / 100, src);
        Assert.Equal(warm.Probes, src.Requests - requests);
        Assert.InRange(warm.Probes, 0, 1);

        // anywhere in the file, never more than MaxProbes
        foreach (int pct in new[] { 1, 13, 38, 51, 64, 88, 99 })
        {
            Ogg.SeekPlan any = VorbisFixture.Plan(file, reader, firstAudioPage, total, total * pct / 100, src);
            Assert.InRange(any.Probes, 0, 8);
        }
        Print($"{name}: cold 70 % in {cold.Probes} probe(s) ({cold.Tier}, window {cold.WindowBytes:N0} B), " +
              $"warm 72 % in {warm.Probes}, {src.Requests} requests in all, index {reader.Index.Count} pages");
    }

    static void WalkFrom(byte[] file, Ogg.Reader reader, long offset, long until)
    {
        reader.Reposition(offset);
        ReadOnlySpan<byte> rest = file.AsSpan((int)offset);
        while (reader.NextPacket(rest, out _, out long granule) == Ogg.Reader.Next.Packet)
            if (granule >= until) return;
    }

    [Fact]
    public void Seek_planner_resolves_from_the_index_without_a_probe()
    {
        // After a linear play-through every page is indexed back to back: a seek anywhere is a bracket of two
        // adjacent pages and costs zero probes (§4.5 "back into what has played").
        byte[] file = VorbisFixture.Bytes("pages-100ms.ogg");
        var reader = new Ogg.Reader();
        var (_, _, firstAudioPage) = VorbisFixture.Headers(file, reader);
        while (reader.NextPacket(file, out _, out _) == Ogg.Reader.Next.Packet) { }
        long total = reader.LastGranule;
        Assert.Equal(441_000L, total);
        var src = new VorbisFixture.CountingSource(file);
        foreach (int pct in new[] { 5, 25, 50, 75, 95 })
        {
            long target = total * pct / 100;
            Ogg.SeekPlan plan = VorbisFixture.Plan(file, reader, firstAudioPage, total, target, src);
            Assert.True(plan.Resolved);
            Assert.Equal(Ogg.SeekTier.Index, plan.Tier);
            Assert.Equal(0, plan.Probes);
            Assert.True(plan.OffsetGranule <= target && target < plan.HiGranule);
        }
        Assert.Equal(0, src.Requests);
    }

    // ── 5. the arithmetic ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Granule_arithmetic_handles_both_ends()
    {
        Assert.Equal(48 * 1024, Ogg.ProbeWindow(0));
        Assert.Equal(48 * 1024, Ogg.ProbeWindow(4096));
        Assert.Equal(4 * 30_000, Ogg.ProbeWindow(30_000));
        Assert.Equal(192 * 1024, Ogg.ProbeWindow(Ogg.MaxPageBytes));
        Assert.Equal(65_307, Ogg.MaxPageBytes);

        Assert.Equal(44_736L - 1024, Ogg.FirstSampleOfPacket(44_736, 1024));
        Assert.Equal(-1L, Ogg.FirstSampleOfPacket(-1, 1024));
        Assert.Equal(0L, Ogg.LeadIn(44_736, 44_736));                 // the libvorbis case: no lead-in
        Assert.Equal(1000L, Ogg.LeadIn(43_736, 44_736));
        Assert.Equal(0L, Ogg.LeadIn(-1, 44_736));
        Assert.Equal(1024, Ogg.TrimTail(0, 1024, 441_000));           // far from the end: untouched
        Assert.Equal(40, Ogg.TrimTail(440_960, 128, 441_000));        // the last packet: cut at the granule
        Assert.Equal(0, Ogg.TrimTail(441_000, 128, 441_000));         // wholly past the end
        Assert.Equal(128, Ogg.TrimTail(440_000, 128, -1));            // unknown end: nothing trimmed
        Assert.Equal(441_000L, Ogg.ExactFrames(441_000));
        Assert.Equal(-1L, Ogg.ExactFrames(-1));
    }
}
