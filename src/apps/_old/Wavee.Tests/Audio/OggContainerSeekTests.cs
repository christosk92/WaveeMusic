using System;
using System.IO;
using NVorbis.Contracts;
using NVorbis.Ogg;
using Xunit;

namespace Wavee.Tests.Audio;

// Container-level seek tests for the vendored NVorbis fork, driven through the PUBLIC ContainerReader /
// IPacketProvider surface over a synthetic (non-Vorbis) Ogg stream: one packet per page, 1024 granules per
// packet, so getPacketGranuleCount is a constant and every landing position is exactly computable.
//
// The regression these pin down: forward-seek byte bisection APPENDS far pages to the page index, leaving a
// large un-indexed byte gap behind. The old backward seek bisected over that sparse index and resolved any
// target inside the gap to the first indexed page PAST it (the previous seek's landing point) — i.e. every
// seek "landed at the furthest-downloaded position". A probe that scanned past the last page also latched
// end-of-stream permanently, making later seeks throw and playback stop at the indexed boundary.
public class OggContainerSeekTests
{
    const int PacketGranules = 1024;
    const int Serial = 0x0666;

    // ── synthetic Ogg builder ────────────────────────────────────────────────────────────────────────────

    static byte[] BuildOgg(int dataPages, int payloadBytes, int lastPagePayload = -1)
    {
        using var ms = new MemoryStream();
        WritePage(ms, seq: 0, granule: 0, flags: 0x02, payload: MakePayload(32, 0));   // BOS "header" page (no samples)
        for (int k = 0; k < dataPages; k++)
        {
            bool last = k == dataPages - 1;
            int size = last && lastPagePayload > 0 ? lastPagePayload : payloadBytes;
            WritePage(ms, seq: k + 1, granule: (k + 1) * (long)PacketGranules,
                flags: (byte)(last ? 0x04 : 0x00), payload: MakePayload(size, k + 1));
        }
        return ms.ToArray();
    }

    static byte[] MakePayload(int size, int seed)
    {
        // constant-ish filler that can never contain the "OggS" sync word
        var p = new byte[size];
        for (int i = 0; i < size; i++) p[i] = (byte)(0x80 | ((seed + i) & 0x3F));
        return p;
    }

    static void WritePage(Stream s, int seq, long granule, byte flags, byte[] payload)
    {
        int full = payload.Length / 255, rem = payload.Length % 255;
        int segCnt = full + 1;
        Assert.True(segCnt <= 255, "payload too large for a single page");
        var page = new byte[27 + segCnt + payload.Length];
        page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
        page[4] = 0; page[5] = flags;
        BitConverter.GetBytes(granule).CopyTo(page, 6);
        BitConverter.GetBytes(Serial).CopyTo(page, 14);
        BitConverter.GetBytes(seq).CopyTo(page, 18);
        page[26] = (byte)segCnt;
        for (int i = 0; i < full; i++) page[27 + i] = 255;
        page[27 + full] = (byte)rem;
        payload.CopyTo(page, 27 + segCnt);
        BitConverter.GetBytes(OggCrc(page)).CopyTo(page, 22);
        s.Write(page, 0, page.Length);
    }

    // Same table CRC the reader verifies with (poly 0x04c11db7, CRC field zeroed during computation).
    static uint OggCrc(byte[] page)
    {
        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
        {
            byte b = i >= 22 && i < 26 ? (byte)0 : page[i];
            uint s = (uint)b ^ (crc >> 24);
            uint e = s << 24;
            for (int j = 0; j < 8; j++) e = (e << 1) ^ (e >= 1u << 31 ? 0x04c11db7u : 0);
            crc = (crc << 8) ^ e;
        }
        return crc;
    }

    static IPacketProvider Open(byte[] ogg)
    {
        IPacketProvider? provider = null;
        var container = new ContainerReader(new MemoryStream(ogg), true);
        container.NewStreamCallback = p => { provider ??= p; return true; };
        Assert.True(container.TryInit());
        Assert.NotNull(provider);
        return provider!;
    }

    static readonly GetPacketGranuleCount Granules = _ => PacketGranules;

    // ── tests ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForwardSeek_ByteBisection_LandsOnExactPacket()
    {
        var pp = Open(BuildOgg(dataPages: 400, payloadBytes: 4000));   // ~1.6 MB
        long target = 320 * (long)PacketGranules + 100;                // deep into never-read territory
        long resolved = pp.SeekTo(target, 0, Granules);
        Assert.Equal(320 * (long)PacketGranules, resolved);            // start of the packet containing the target
    }

    [Fact]
    public void BackwardSeek_IntoUnindexedGap_LandsOnTarget_NotAtFurthestDownloadedPage()
    {
        var pp = Open(BuildOgg(dataPages: 400, payloadBytes: 4000));

        // play a little (indexes the head pages)…
        for (int i = 0; i < 4; i++) Assert.NotNull(pp.GetNextPacket());

        // …forward seek to ~80% (bisection materializes far pages, leaving a huge un-indexed gap)…
        long far = 320 * (long)PacketGranules + 100;
        Assert.Equal(320 * (long)PacketGranules, pp.SeekTo(far, 0, Granules));

        // …then seek back INTO the gap. The old sparse-index bisection resolved this to the ~80%
        // page (the "always lands at max of buffer" symptom); it must land on the actual target page.
        long mid = 160 * (long)PacketGranules + 100;
        Assert.Equal(160 * (long)PacketGranules, pp.SeekTo(mid, 0, Granules));

        // and the packet stream must actually continue from there
        var pkt = pp.GetNextPacket();
        Assert.NotNull(pkt);
        Assert.Equal(161 * (long)PacketGranules, pkt!.GranulePosition);
    }

    [Fact]
    public void BackwardSeek_IntoGap_WithPreRoll_DoesNotThrow()
    {
        var pp = Open(BuildOgg(dataPages: 400, payloadBytes: 4000));
        for (int i = 0; i < 4; i++) Assert.NotNull(pp.GetNextPacket());
        Assert.Equal(320 * (long)PacketGranules, pp.SeekTo(320 * (long)PacketGranules + 100, 1, Granules));

        // preRoll=1 walks the packet index back across the materialized page's (unmergeable) boundary —
        // must clamp and land, not abort the seek.
        long mid = 160 * (long)PacketGranules + 100;
        Assert.Equal(160 * (long)PacketGranules, pp.SeekTo(mid, 1, Granules));
        Assert.NotNull(pp.GetNextPacket());
    }

    [Fact]
    public void SeekNearEnd_ProbeScanningPastLastPage_DoesNotLatchEndOfStream()
    {
        // Last page ~24 KB: bisection probes near the end get clamped inside its body, scan to EOF, and
        // find nothing. That probe must stay side-effect-free — previously it latched HasAllPages and tore
        // down the stream reader, making the seek throw and playback end at the furthest-indexed page.
        const int dataPages = 60;
        var pp = Open(BuildOgg(dataPages, payloadBytes: 4000, lastPagePayload: 24481));

        long target = dataPages * (long)PacketGranules - 512;          // inside the final packet
        long resolved = pp.SeekTo(target, 0, Granules);
        Assert.Equal((dataPages - 1) * (long)PacketGranules, resolved);

        // the stream still plays out to its real end…
        var last = pp.GetNextPacket();
        Assert.NotNull(last);
        Assert.Equal(dataPages * (long)PacketGranules, last!.GranulePosition);

        // …and later seeks (backward, into content skipped by the bisection) still work, with playback
        // CONTINUING across the never-indexed region — not truncating at the indexed boundary
        Assert.Equal(10 * (long)PacketGranules, pp.SeekTo(10 * (long)PacketGranules + 5, 0, Granules));
        for (int k = 11; k <= 20; k++)
        {
            var pkt = pp.GetNextPacket();
            Assert.NotNull(pkt);
            Assert.Equal(k * (long)PacketGranules, pkt!.GranulePosition);
        }
    }

    [Fact]
    public void SeekToZero_AfterForwardSeek_RestartsAtTheRealFirstDataPage()
    {
        // The prev-button restart path (host.Seek(0)). _firstDataPageIndex latches onto the first-ADDED
        // data page — after a bisected forward seek that's a far page, and the old code restarted THERE.
        var pp = Open(BuildOgg(dataPages: 400, payloadBytes: 4000));
        Assert.Equal(320 * (long)PacketGranules, pp.SeekTo(320 * (long)PacketGranules + 100, 0, Granules));

        Assert.Equal(0, pp.SeekTo(0, 0, Granules));
        var pkt = pp.GetNextPacket();
        Assert.NotNull(pkt);
        Assert.Equal(PacketGranules, pkt!.GranulePosition);   // the file's first data page, not the far page
    }

    [Fact]
    public void RepeatedAlternatingSeeks_AlwaysLandOnTarget()
    {
        // Scrub back and forth — every landing must track its own target even as the page index
        // accumulates sparse materialized entries from all the previous seeks.
        var pp = Open(BuildOgg(dataPages: 400, payloadBytes: 4000));
        foreach (int page in new[] { 350, 40, 300, 80, 200, 20, 390, 5 })
        {
            long target = page * (long)PacketGranules + 7;
            Assert.Equal(page * (long)PacketGranules, pp.SeekTo(target, 0, Granules));
        }
    }
}
