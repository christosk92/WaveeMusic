// ── Wavee.Tests/AudioStreamTests.cs — the CDN stream layer (Vorbis plan §5.6, owner F) ────────────────────────────
//
// The gate for `Spotify/Spotify.Audio.Stream.cs`: the head, the ring, the fetcher and the disk cache, over a FAKE CDN.
// Nothing here touches the network or the live profile. `FakeCdn` implements the one wire seam (`IRangeSource`), serves
// a synthetic encrypted file whose clear head, Spotify header and last Ogg page are known, COUNTS every request, and can
// HOLD a request open until its token is cancelled — which is how the in-flight cancel is observed rather than assumed.
//
// THE REQUEST COUNTS ARE FACTS, NOT ESTIMATES. §5.4's table is asserted per scenario (cold start, seek inside the ring,
// far seek, scrubbing, seek into the cache, a cached file, the next track), with the window-dependent part computed
// from the ring's own slot count so the facts stay true when the tiers are retuned. Each test writes what it measured.
//
// TIME. The ring's wait is bounded (8 s in production); tests pass a short bound where a stall is the point, and wait
// on the fetcher's `Outstanding` counter — never on a sleep — everywhere else.

using System.Buffers.Binary;
using System.Diagnostics;
using Wavee;
using Wavee.Sdk.Streams;
using Xunit;
using Audio = Wavee.Spotify.Audio;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioStreamCollection
{
    /// <summary>The fetch thread and the fake CDN are timing-honest; they run alone so a loaded runner cannot starve them.</summary>
    public const string Name = "audio-stream";
}

[Collection(AudioStreamCollection.Name)]
public sealed class AudioStreamTests(ITestOutputHelper output)
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly byte[] Key =
        [0x2b, 0x7e, 0x15, 0x16, 0x28, 0xae, 0xd2, 0xa6, 0xab, 0xf7, 0x15, 0x88, 0x09, 0xcf, 0x4f, 0x3c];

    const int Skip = Audio.Ctr.HeaderBytes;
    const int BigBytes = 3 * 1024 * 1024;           // 60 s → ~52 KB/s: a 320 kbit/s-shaped file larger than its window
    const int SmallBytes = 400_000;                 // 60 s → the whole file fits one window and one range
    const long LastGranule = 2_646_000;
    const string FileA = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    const string FileB = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const int Slot = Audio.Ring.SlotBytes;
    const int MaxRange = Audio.Fetcher.MaxRangeBytes;
    const float HeaderPeak = 0.9f;

    static readonly Lazy<(byte[] Plain, byte[] Cipher)> Big = new(() => SyntheticFile(BigBytes));
    static readonly Lazy<(byte[] Plain, byte[] Cipher)> Small = new(() => SyntheticFile(SmallBytes));

    /// <summary>Random bytes with every accidental `OggS` scrubbed, the track gain at header byte 144 and its linear peak
    /// at 148, an `OggS` at 0xa7 (the container start the key check looks for), and two pages near the end: the last
    /// carries <see cref="LastGranule"/>. The cipher is the plaintext through the real CTR transform.</summary>
    static (byte[] Plain, byte[] Cipher) SyntheticFile(int length)
    {
        var plain = new byte[length];
        new Random(20260913 + length).NextBytes(plain);
        for (int i = 0; i + 4 <= length; i++)
            if (plain[i] == (byte)'O' && plain[i + 1] == (byte)'g' && plain[i + 2] == (byte)'g' && plain[i + 3] == (byte)'S')
                plain[i] = 0;
        BitConverter.TryWriteBytes(plain.AsSpan(144, 4), -3.5f);
        BitConverter.TryWriteBytes(plain.AsSpan(148, 4), HeaderPeak);
        WritePage(plain, Skip, 0);
        WritePage(plain, length - 20_000, LastGranule - 44_100);
        WritePage(plain, length - 4_000, LastGranule);
        return (plain, Audio.Ctr.Decrypt(plain, Key, 0));
    }

    static void WritePage(byte[] buffer, int at, long granule)
    {
        "OggS"u8.CopyTo(buffer.AsSpan(at));
        buffer[at + 4] = 0;
        buffer[at + 5] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 6, 8), granule);
    }

    static Audio.Body NewBody(FakeCdn cdn, Audio.Fetcher fetcher, int length, bool known = true, byte[]? head = null,
        ChunkDiskCache? disk = null, int waitMs = Audio.Ring.DefaultWaitMs, string fileId = FileA, bool prepared = false,
        float peak = 0f)
    {
        long estimate = Skip + 60_000L * Audio.NominalBytesPerSecond(Audio.Format.OggVorbis320) / 1000;
        return new Audio.Body(cdn, ["https://cdn-a.test/audio", "https://cdn-b.test/audio"], Key, Skip,
            known ? length : estimate, known, 60_000, Audio.Format.OggVorbis320, 0f, fileId, head, disk, fetcher,
            metered: false, waitMs: waitMs, peak: peak, prepared: prepared);
    }

    /// <summary>The range a seek probe at container <paramref name="offset"/> must be: [start, end) aligned OUT to whole
    /// slots at both ends.</summary>
    static (long Start, long End) ProbeRange(long offset, int length, int bytes = Audio.Ring.ProbeWindow)
        => (Audio.Ring.AlignDown(Skip + offset), Math.Min(length, Audio.Ring.AlignUp(Skip + offset + bytes)));

    /// <summary>Container bytes [offset, offset + count) through `ReadAt`, looping over short reads as a decoder does.</summary>
    static byte[] Read(Audio.Body body, long offset, int count)
    {
        var dst = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = body.ReadAt(offset + got, dst.AsSpan(got), body.Epoch);
            if (n <= 0) break;
            got += n;
        }
        return got == count ? dst : dst[..got];
    }

    static byte[] Expected(byte[] plain, long containerOffset, int count)
        => plain.AsSpan((int)(Skip + containerOffset), count).ToArray();

    static void WaitIdle(Audio.Fetcher fetcher) => WaitUntil(() => fetcher.Outstanding == 0, "the fetcher to go idle");

    static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("timed out waiting for " + what);
            Thread.Sleep(1);
        }
    }

    static int FillRanges(Audio.Body body, long fileLength)
    {
        long window = (long)(body.Ring.Slots - Audio.Ring.KeepBehindSlots) * Slot;
        return (int)((Math.Min(window, fileLength) + MaxRange - 1) / MaxRange);
    }

    // ── cold start ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Head_serves_the_first_bytes_before_any_range_lands()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        cdn.Hold();
        using var body = NewBody(cdn, fetcher, BigBytes, known: false, head: plain[..Audio.HeadMaxBytes]);
        body.Start();
        WaitUntil(() => cdn.Live == 1, "range 1 to be on the wire");

        Assert.Equal(Expected(plain, 0, 16_384), Read(body, 0, 16_384));
        Assert.Equal(0, body.Ring.Waits);
        Assert.False(body.LengthKnown);
        Assert.Equal(0, body.SpliceProof);

        cdn.Release();
        WaitIdle(fetcher);
        Assert.True(body.LengthKnown);
    }

    [Fact]
    public void Cold_start_is_the_first_range_and_the_tail_then_the_window_fill()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, known: false, head: plain[..Audio.HeadMaxBytes]);
        body.Start();
        WaitIdle(fetcher);

        var ranges = cdn.Ranges;
        long tail = Audio.Ring.AlignDown(BigBytes - Slot);
        Assert.Equal((0L, (long)MaxRange), ranges[0]);                  // chunk 0: the one the splice proof needs
        Assert.Equal((tail, (long)BigBytes), ranges[1]);                // queued the moment Content-Range named the length
        int fill = FillRanges(body, BigBytes);
        Assert.Equal(fill + 1, fetcher.Requests);
        Assert.Equal(fill + 1, ranges.Length);
        Assert.Equal(1, fetcher.PeakInFlight);
        Assert.True(body.LengthKnown);
        Assert.Equal(BigBytes, body.FileLength);
        Assert.Equal(LastGranule, body.TailGranule);
        Assert.Equal(1, body.SpliceProof);
        Assert.Equal(0, body.HeadBytes);
        output.WriteLine($"cold start: head 1 ‖ resolve ‖ key, then first range 1 + tail 1 = 2 CDN ranges before the " +
                         $"window fill; fill to the {body.Ring.Seconds}s window ({body.Ring.Slots} slots) = {fill - 1} more; " +
                         $"total ranges {ranges.Length}");
    }

    // ── the splice ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Head_splice_is_byte_exact()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, head: plain[..Audio.HeadMaxBytes]);
        body.Start();
        WaitIdle(fetcher);

        Assert.Equal(1, body.SpliceProof);
        Assert.Equal(0, body.HeadBytes);                                // proven, so dropped: the ring serves chunk 0
        Assert.Equal(Expected(plain, 0, 200_000), Read(body, 0, 200_000));
    }

    [Fact]
    public void A_refused_head_supersedes_the_epoch_and_the_ring_serves_the_truth()
    {
        var (plain, cipher) = Big.Value;
        byte[] head = plain[..Audio.HeadMaxBytes];
        head[1_000] ^= 0xFF;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, head: head);
        body.Start();
        WaitIdle(fetcher);

        Assert.Equal(2, body.SpliceProof);
        Assert.Equal(1u, body.Epoch);
        Assert.Equal(0, body.HeadBytes);
        Assert.Equal(Expected(plain, 0, 200_000), Read(body, 0, 200_000));
    }

    [Fact]
    public void A_partial_proof_keeps_the_head_until_it_is_covered()
    {
        var (plain, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(new FakeCdn(cipher), fetcher, BigBytes, head: plain[..Audio.HeadMaxBytes]);

        Assert.True(body.ProveHead(plain.AsSpan(0, Slot)));             // a 64 KiB chunk from disk: equal so far
        Assert.Equal(Audio.HeadMaxBytes, body.HeadBytes);
        Assert.True(body.ProveHead(plain.AsSpan(0, MaxRange)));
        Assert.Equal(0, body.HeadBytes);
        Assert.Equal(0, fetcher.Requests);
    }

    // ── seeks ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seek_inside_the_ring_costs_no_request()
    {
        var (plain, cipher) = Small.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, SmallBytes);
        body.Start();
        WaitIdle(fetcher);
        int baseline = fetcher.Requests;

        body.Retarget(250_000, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, 250_000, 32_000), Read(body, 250_000, 32_000));
        body.ResumeFrom(250_000);
        body.Retarget(10_000, Audio.Ring.ProbeWindow, 2);               // and back
        Assert.Equal(Expected(plain, 10_000, 32_000), Read(body, 10_000, 32_000));
        WaitIdle(fetcher);

        Assert.Equal(1, baseline);                                      // the whole file was one range; the tail answered locally
        Assert.Equal(baseline, fetcher.Requests);
        Assert.Equal(LastGranule, body.TailGranule);
        output.WriteLine($"seek inside the ring: 0 requests (open cost {baseline})");
    }

    [Fact]
    public void Far_seek_is_one_probe_then_one_fill_range()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int before = fetcher.Requests, opens = cdn.Count;

        const long far = 2_600_000;
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, far, 48_000), Read(body, far, 48_000));
        body.ResumeFrom(far);
        WaitIdle(fetcher);

        var after = cdn.Ranges[opens..];
        Assert.Equal(ProbeRange(far, BigBytes), after[0]);              // the 48 KiB probe, aligned out at both ends
        Assert.Equal(2, fetcher.Requests - before);                     // + the fill from the landing page to EOF
        output.WriteLine($"far seek: probe 1 + fill {fetcher.Requests - before - 1}; open cost {before}");
    }

    [Fact]
    public void Retarget_cancels_the_in_flight_range_and_the_probe_goes_first()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "range 1 to be held open");

        const long far = 2_600_000;
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        WaitUntil(() => cdn.CancelsObserved == 1, "the held range to observe its token");
        WaitUntil(() => cdn.Count >= 2, "the probe to reach the wire");

        var ranges = cdn.Ranges;
        Assert.Equal(1, fetcher.Cancelled);
        Assert.Equal((0L, (long)MaxRange), ranges[0]);
        Assert.Equal(ProbeRange(far, BigBytes), ranges[1]);             // ahead of the tail that was already queued

        cdn.Release();
        WaitIdle(fetcher);
        Assert.Equal(Expected(plain, far, 16_000), Read(body, far, 16_000));
        Assert.Equal(1, cdn.PeakLive);
    }

    [Fact]
    public void Scrubbing_ten_seeks_keeps_one_range_in_flight()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "range 1 to be held open");

        long last = 0;
        var expected = new (long Start, long End)[10];
        for (int i = 0; i < 10; i++)
        {
            last = 400_000 + i * 250_000L;
            expected[i] = ProbeRange(last, BigBytes);
            body.Retarget(last, Audio.Ring.ProbeWindow, (uint)(i + 1));
            int expectOpens = i + 2;
            WaitUntil(() => cdn.Count == expectOpens && cdn.Live == 1, $"probe {i} to be held open");
        }
        cdn.Release();
        WaitIdle(fetcher);

        var ranges = cdn.Ranges;
        int probes = 0;
        for (int i = 0; i < expected.Length; i++) if (ranges[i + 1] == expected[i]) probes++;
        Assert.Equal(10, probes);
        Assert.Equal(10, fetcher.Cancelled);                            // range 1 and nine probes, each by the next seek
        Assert.Equal(1, fetcher.PeakInFlight);
        Assert.Equal(1, cdn.PeakLive);
        Assert.Equal(Expected(plain, last, 32_000), Read(body, last, 32_000));
        output.WriteLine($"scrub of 10: {probes} probes on the wire, {fetcher.Cancelled} cancelled, peak in flight " +
                         $"{fetcher.PeakInFlight}, {ranges.Length} ranges in all (incl. range 1, tail and the final fill)");
    }

    [Fact]
    public void A_stale_queued_range_never_reaches_the_wire()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        body.Retarget(2_600_000, Audio.Ring.ProbeWindow, 1);
        body.ResumeFrom(2_600_000);
        WaitIdle(fetcher);
        int opens = cdn.Count;

        // Slots 11-19 now hold chunks 39-47 (the two-slot probe, then the fill), so chunks 12-15 are NOT resident: only
        // the epoch keeps this off the wire.
        fetcher.Enqueue(body, new Audio.RangeRequest(12L * Slot, 16L * Slot, Epoch: 0, Probe: false));
        WaitIdle(fetcher);

        Assert.Equal(opens, cdn.Count);
    }

    [Fact]
    public void A_probe_just_short_of_a_slot_edge_is_one_range_aligned_out_at_both_ends()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int opens = cdn.Count, before = fetcher.Requests;

        // 1,000 bytes short of the edge between chunks 39 and 40: the window crosses it. Aligning only the START down
        // made the range stop at the edge, and the last 47 KiB of the window cost a second range and a second wait.
        long offset = 40L * Slot - Skip - 1_000;
        body.Retarget(offset, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, offset, Audio.Ring.ProbeWindow), Read(body, offset, Audio.Ring.ProbeWindow));
        WaitIdle(fetcher);

        Assert.Equal((39L * Slot, 41L * Slot), cdn.Ranges[opens]);
        Assert.Equal(1, fetcher.Requests - before);                     // the probe, and nothing else: the fill is held
        Assert.True(body.Ring.FillHeld);
        Assert.True(body.Ring.Waits <= 1, $"{body.Ring.Waits} waits");
    }

    [Fact]
    public void A_multi_probe_seek_sends_no_fill_until_the_landing_resumes_it()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int before = fetcher.Requests, cancelled = fetcher.Cancelled, opens = cdn.Count;

        // Two probes of one seek, each allowed to land. A fill planned when the first landed would have been the
        // request the second probe cancels (or, landed, a 512 KiB range nobody reads).
        const long probe1 = 2_000_000, landing = 2_700_000;
        body.Retarget(probe1, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, probe1, 16_000), Read(body, probe1, 16_000));
        WaitIdle(fetcher);
        body.Retarget(landing, Audio.Ring.ProbeWindow, 2);
        Assert.Equal(Expected(plain, landing, 16_000), Read(body, landing, 16_000));
        WaitIdle(fetcher);

        Assert.Equal(2, fetcher.Requests - before);
        Assert.Equal(cancelled, fetcher.Cancelled);
        Assert.Equal(ProbeRange(probe1, BigBytes), cdn.Ranges[opens]);
        Assert.Equal(ProbeRange(landing, BigBytes), cdn.Ranges[opens + 1]);

        body.ResumeFrom(landing);                                       // the landing: the held fill goes out, once
        WaitIdle(fetcher);
        Assert.False(body.Ring.FillHeld);
        Assert.Equal(3, fetcher.Requests - before);
        Assert.Equal((ProbeRange(landing, BigBytes).End, (long)BigBytes), cdn.Ranges[opens + 2]);
        output.WriteLine($"two-probe seek: {fetcher.Requests - before} requests (2 probes + 1 fill at the landing)");
    }

    [Fact]
    public async Task An_interrupt_releases_a_read_blocked_in_the_ring_wait_until_the_seek_retargets()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);               // the production 8 s bound
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "range 1 to be held open");

        uint epoch = body.Epoch;
        var blocked = Task.Run(() => body.ReadAt(0, new byte[4_096], epoch, interruptible: true));
        WaitUntil(() => body.Ring.Want >= 0, "the read to block in the ring's wait");
        var clock = Stopwatch.StartNew();
        body.InterruptPendingRead();
        Task first = await Task.WhenAny(blocked, Task.Delay(3_000));
        Assert.True(ReferenceEquals(blocked, first), "the interrupted read did not return");
        Assert.Equal(Audio.Body.Interrupted, await blocked);             // not EOF (0), not a fault (−1)
        Assert.True(clock.ElapsedMilliseconds < 3_000);

        // While the window is open an interruptible miss answers at once. The seek's own retarget closes the window.
        body.InterruptPendingRead();
        clock.Restart();
        Assert.Equal(Audio.Body.Interrupted, body.ReadAt(8_192, new byte[1_024], epoch, interruptible: true));
        Assert.True(clock.ElapsedMilliseconds < 1_000);
        const long far = 2_600_000;
        body.Retarget(far, Audio.Ring.ProbeWindow, epoch + 1);
        cdn.Release();
        Assert.Equal(Expected(plain, far, 16_000), Read(body, far, 16_000));
        var afterSeek = new byte[1_024];
        Assert.True(body.ReadAt(far, afterSeek, body.Epoch, interruptible: true) > 0);
        WaitIdle(fetcher);
    }

    // ── the counters (headless plan §3.4) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stats_count_probes_cdn_bytes_and_starves_and_since_is_the_delta_against_a_mark()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300);
        Audio.Stream.Stats mark = Audio.Stream.Stats.Read(fetcher);
        cdn.Hold();
        body.Start();

        Assert.Equal(-1, body.ReadAt(0, new byte[4_096], body.Epoch));  // starved against the held range
        cdn.Release();
        WaitIdle(fetcher);
        body.Retarget(2_600_000, Audio.Ring.ProbeWindow, body.Epoch + 1);
        WaitIdle(fetcher);

        Audio.Stream.Stats now = Audio.Stream.Stats.Read(fetcher);
        Audio.Stream.Stats delta = now.Since(in mark);
        Assert.Equal(1L, delta.RingStarves);
        Assert.Equal(1L, delta.Probes);
        Assert.Equal((long)fetcher.Requests, delta.Requests);           // a fresh fetcher: its whole count is the delta
        Assert.True(delta.CdnBytes > 0);
        Assert.Equal(delta.Requests + delta.Heads + delta.Resolves, delta.HttpRequests);
        Assert.Equal(0, now.InFlight);
        Assert.Equal(1, now.PeakInFlight);
        Assert.True(now.ReadAheadBytes >= (long)body.Ring.Slots * Slot);
        Assert.Equal(now.PingMs, delta.PingMs);                         // a gauge stays the value's, never a delta
        output.WriteLine($"stats delta: requests={delta.Requests} probes={delta.Probes} bytes={delta.CdnBytes} " +
                         $"starves={delta.RingStarves} readAhead={now.ReadAheadBytes}");
    }

    [Fact]
    public void Seek_into_the_disk_cache_counts_cache_hits_and_no_probe()
    {
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk);
        body.Start();
        WaitIdle(fetcher);
        Audio.Stream.Stats mark = Audio.Stream.Stats.Read(fetcher);
        body.Retarget(2_600_000, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, 2_600_000, 32_000), Read(body, 2_600_000, 32_000));
        WaitIdle(fetcher);

        Audio.Stream.Stats delta = Audio.Stream.Stats.Read(fetcher).Since(in mark);
        Assert.Equal(0L, delta.Probes);
        Assert.Equal(0L, delta.Requests);
        Assert.True(delta.CacheHits >= 2, $"{delta.CacheHits} cache hits for a two-slot probe");
    }

    // ── the shared read-ahead budget (Vorbis plan §5.3) ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_shared_budget_grants_what_is_left_never_more_than_asked_and_never_below_the_floor()
    {
        const long MiB = 1L << 20;
        int total = (int)(Audio.ReadAheadBudget.TotalBytes / Slot);                 // 375 slots
        Assert.Equal(260, Audio.ReadAheadBudget.Grant(260, 0));                      // the 600 s tier fits alone
        Assert.Equal(107, Audio.ReadAheadBudget.Grant(107, 260L * Slot));            // 30 s of FLAC24 beside it
        Assert.Equal(total - 300, Audio.ReadAheadBudget.Grant(107, 300L * Slot));    // only what is left
        Assert.Equal(Audio.ReadAheadBudget.MinSlots, Audio.ReadAheadBudget.Grant(40, 24 * MiB));   // spent: the floor
        Assert.Equal(Audio.ReadAheadBudget.MinSlots, Audio.ReadAheadBudget.Grant(3, 0));           // never below it
    }

    [Fact]
    public void A_prepared_body_takes_its_ring_from_the_shared_budget_and_every_body_gives_it_back()
    {
        var (_, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        long before = Audio.ReadAheadBudget.InUseBytes;
        using (var playing = NewBody(new FakeCdn(cipher), fetcher, BigBytes))
        {
            Assert.Equal(before + (long)playing.Ring.Slots * Slot, Audio.ReadAheadBudget.InUseBytes);
            using (var next = NewBody(new FakeCdn(cipher), fetcher, BigBytes, fileId: FileB, prepared: true))
            {
                Assert.True(next.Prepared);
                Assert.True(next.Ring.Seconds <= Audio.ReadAheadBudget.PreparedSeconds);
                Assert.Equal(before + (long)(playing.Ring.Slots + next.Ring.Slots) * Slot, Audio.ReadAheadBudget.InUseBytes);
            }
            Assert.Equal(before + (long)playing.Ring.Slots * Slot, Audio.ReadAheadBudget.InUseBytes);
        }
        Assert.Equal(before, Audio.ReadAheadBudget.InUseBytes);
    }

    // ── the disk cache ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seek_into_the_disk_cache_costs_no_request()
    {
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk);
        body.Start();
        WaitIdle(fetcher);
        body.Retarget(2_600_000, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, 2_600_000, 100_000), Read(body, 2_600_000, 100_000));
        WaitIdle(fetcher);

        Assert.Empty(cdn.Ranges);
        Assert.Equal(0, fetcher.Requests);
    }

    [Fact]
    public void Cached_file_needs_no_request()
    {
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        Assert.Equal((long?)BigBytes, disk.KnownSize(FileA));
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk);
        body.Start();
        Assert.Equal(Expected(plain, 0, BigBytes - Skip), Read(body, 0, BigBytes - Skip));
        WaitIdle(fetcher);

        Assert.Empty(cdn.Ranges);
        Assert.Equal(LastGranule, body.TailGranule);                    // the tail granule came off the disk too
    }

    [Fact]
    public void Cache_range_map_round_trips()
    {
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        const string id = "00112233445566778899aabbccddeeff";
        long size = 7L * Slot - 1_000;                                  // seven chunks, the last one short
        byte[] Chunk(int index) => Enumerable.Range(0, (int)Math.Min(Slot, size - (long)index * Slot))
            .Select(i => (byte)(i * 31 + index)).ToArray();

        using (var cache = new ChunkDiskCache(dir.Path))
        {
            cache.SetSize(id, size);
            foreach (int index in new[] { 0, 3, 5 }) cache.WriteChunk(id, index, Chunk(index));
            Assert.True(cache.WaitForPendingWrites(10_000));
        }

        var buffer = new byte[Slot];
        using (var reopened = new ChunkDiskCache(dir.Path))
        {
            foreach (int index in new[] { 0, 3, 5 })
            {
                Assert.True(reopened.TryReadChunk(id, index, buffer, out int length), $"chunk {index}");
                Assert.Equal(Chunk(index), buffer[..length]);
            }
            foreach (int index in new[] { 1, 2, 4, 6 }) Assert.False(reopened.TryReadChunk(id, index, buffer, out _), $"chunk {index}");
        }

        string enc = Directory.EnumerateFiles(dir.Path, "*.enc", SearchOption.AllDirectories).Single();
        using (var stream = new FileStream(enc, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = 3L * Slot + 10;
            int value = stream.ReadByte();
            stream.Position = 3L * Slot + 10;
            stream.WriteByte((byte)(value ^ 0xFF));
        }
        using var flipped = new ChunkDiskCache(dir.Path);
        Assert.False(flipped.TryReadChunk(id, 3, buffer, out _));       // the SHA-256 refuses it
        Assert.True(flipped.TryReadChunk(id, 5, buffer, out _));
    }

    [Fact]
    public void Free_space_reserve_is_five_gib_or_five_percent()
    {
        const long GiB = 1L << 30;
        Assert.Equal(5 * GiB, Audio.DiskCache.ReserveBytes(64 * GiB));
        Assert.Equal(1024 * GiB / 20, Audio.DiskCache.ReserveBytes(1024 * GiB));
        Assert.True(Audio.DiskCache.CanCommit(6 * GiB, GiB / 2, 64 * GiB));
        Assert.False(Audio.DiskCache.CanCommit(5 * GiB + GiB / 10, GiB / 2, 64 * GiB));
        Assert.True(Audio.DiskCache.CanCommit(60 * GiB, GiB, 1024 * GiB));
        Assert.False(Audio.DiskCache.CanCommit(52 * GiB, GiB, 1024 * GiB));
    }

    // ── the next track ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Next_track_shares_the_fetch_thread_and_costs_its_first_range_and_tail()
    {
        var (plain, cipher) = Big.Value;
        var playing = new FakeCdn(cipher);
        var next = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var a = NewBody(playing, fetcher, BigBytes);
        a.Start();
        WaitIdle(fetcher);
        Assert.Equal(Expected(plain, 0, 400_000), Read(a, 0, 400_000));

        using var b = NewBody(next, fetcher, BigBytes, known: false, head: plain[..Audio.HeadMaxBytes], fileId: FileB);
        b.Start();
        WaitIdle(fetcher);

        int fill = FillRanges(b, BigBytes);
        Assert.Equal(fill + 1, next.Count);
        Assert.Equal((0L, (long)MaxRange), next.Ranges[0]);
        Assert.Equal(1, fetcher.PeakInFlight);
        Assert.Equal(Expected(plain, 0, 300_000), Read(b, 0, 300_000));
        output.WriteLine($"next track: first range + tail + {fill - 1} fill = {next.Count} ranges; its tier was " +
                         $"{b.Ring.Seconds}s ({b.Ring.Slots} slots) because the shared fetcher had measured the link");
    }

    // ── the ring ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ring_never_blocks_a_realtime_consumer_while_delivery_keeps_up()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int rate = body.BytesPerSecond, before = fetcher.Requests;

        long position = 0;
        for (int second = 0; second < 40; second++)                    // one read per second of audio, on a test clock
        {
            byte[] got = Read(body, position, rate);
            Assert.Equal(Expected(plain, position, rate), got);
            position += rate;
            WaitIdle(fetcher);                                          // delivery ≥ 1× realtime
        }

        Assert.Equal(0, body.Ring.Waits);
        Assert.Equal(0, body.Ring.Starves);
        int steady = fetcher.Requests - before;
        Assert.InRange(steady, 1, (int)(position / MaxRange) + 2);     // hysteresis: ~one 512 KiB range per 512 KiB heard
        output.WriteLine($"40 s at {rate} B/s: {steady} refill ranges for {position} bytes, 0 waits");
    }

    [Fact]
    public void Ring_wait_is_bounded_when_delivery_stops()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300);
        cdn.Hold();
        body.Start();

        var clock = Stopwatch.StartNew();
        int n = body.ReadAt(0, new byte[4_096], body.Epoch);
        clock.Stop();

        Assert.Equal(-1, n);                                            // a fault, never a 0 the decoder would call EOF
        Assert.Equal(1, body.Ring.Starves);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 5_000);
        cdn.Release();
    }

    [Fact]
    public void Every_mirror_refusing_backs_off_instead_of_spinning()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { Refuse = true };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 1_000);
        body.Start();

        Assert.Equal(-1, body.ReadAt(0, new byte[4_096], body.Epoch));
        WaitIdle(fetcher);

        Assert.InRange(fetcher.Requests, 1, 8);                         // ~4/s for a second, plus the tail
        Assert.Equal(2 * fetcher.Requests, cdn.Count);                  // both mirrors, once each, per range
    }

    [Fact]
    public void Ring_slots_size_from_seconds_not_bytes()
    {
        const long Huge = 1L << 30;
        Assert.Equal(19 + 4, Audio.Ring.SlotCount(30, 40_000, Huge));   // 1.2 MB of 320 kbit/s
        Assert.Equal(103 + 4, Audio.Ring.SlotCount(30, 225_000, Huge)); // 6.75 MB of 24-bit FLAC
        Assert.Equal(256 + 4, Audio.Ring.SlotCount(600, 1_000_000, Huge)); // capped at 16 MiB
        Assert.Equal(8 + 4, Audio.Ring.SlotCount(30, 40_000, 100_000)); // never fewer than eight
    }

    [Fact]
    public void Read_ahead_tiers_are_ten_thirty_and_six_hundred_seconds()
    {
        Assert.Equal(10, Audio.Ring.ReadAheadSeconds(metered: true, 10_000_000, 40_000));
        Assert.Equal(30, Audio.Ring.ReadAheadSeconds(metered: false, 119_999, 40_000));
        Assert.Equal(600, Audio.Ring.ReadAheadSeconds(metered: false, 120_000, 40_000));
    }

    // ── pure helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ping_median_and_prefetch_threshold_follow_librespot()
    {
        Assert.Equal(2, Audio.Fetcher.MedianOf3(1, 2, 3));
        Assert.Equal(2, Audio.Fetcher.MedianOf3(3, 1, 2));
        Assert.Equal(2, Audio.Fetcher.MedianOf3(2, 3, 1));
        Assert.Equal(5, Audio.Fetcher.MedianOf3(5, 5, 1));
        Assert.Equal(80_000, Audio.Fetcher.PrefetchThresholdBytes(500, 40_000, 64_000));      // 4 × 0.5 s × 40 KB/s
        Assert.Equal(1_000_000, Audio.Fetcher.PrefetchThresholdBytes(500, 40_000, 2_000_000)); // 0.5 s × throughput
        Assert.Equal(240_000, Audio.Fetcher.PrefetchThresholdBytes(9_000, 40_000, 0));        // ping capped at 1.5 s
    }

    [Fact]
    public void Last_ogg_granule_is_read_from_the_last_page()
    {
        var buffer = new byte[10_000];
        Assert.Equal(-1, Audio.LastOggGranule(buffer));
        WritePage(buffer, 1_000, 100);
        WritePage(buffer, 6_000, 441_000);
        Assert.Equal(441_000, Audio.LastOggGranule(buffer));
        WritePage(buffer, 8_000, -1);                                   // a page on which no packet ends
        Assert.Equal(441_000, Audio.LastOggGranule(buffer));
        Assert.Equal(441_000, Audio.LastOggGranule(buffer.AsSpan(0, 6_014)));   // only 14 header bytes inside
    }

    [Fact]
    public void Header_gain_is_read_at_byte_144()
    {
        var (plain, _) = Small.Value;
        Assert.Equal(-3.5f, Audio.HeadGainDb(plain.AsSpan(0, Audio.HeadMaxBytes)));
        Assert.Equal(0f, Audio.HeadGainDb(plain.AsSpan(0, 100)));
    }

    [Fact]
    public void The_peak_is_read_at_byte_148_and_carried_so_the_gain_cap_can_apply()
    {
        var (plain, _) = Small.Value;
        Assert.Equal(HeaderPeak, Audio.HeadPeak(plain.AsSpan(0, Audio.HeadMaxBytes)));
        Assert.Equal(0f, Audio.HeadPeak(plain.AsSpan(0, 151)));         // too short to hold it: unknown

        // A peak that is not a believable amplitude is UNKNOWN, never a cap: a garbage header float would otherwise
        // silence the track through `NormalizationFactor`.
        Assert.Equal(0f, Audio.SanePeak(float.NaN));
        Assert.Equal(0f, Audio.SanePeak(-0.5f));
        Assert.Equal(0f, Audio.SanePeak(1_000f));
        Assert.Equal(0.9f, Audio.SanePeak(0.9f));
        Assert.Equal(MathF.Pow(10f, -3f / 20f), Audio.PeakLinear(-3f), 5);   // the catalogue's dBFS true peak, linear
        Assert.Equal(0f, Audio.PeakLinear(float.PositiveInfinity));

        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(new FakeCdn(Small.Value.Cipher), fetcher, SmallBytes, peak: HeaderPeak);
        Assert.Equal(HeaderPeak, body.Peak);
        using var garbage = NewBody(new FakeCdn(Small.Value.Cipher), fetcher, SmallBytes, fileId: FileB, peak: 99f);
        Assert.Equal(0f, garbage.Peak);
    }

    [Fact]
    public void Body_stream_reads_the_container_byte_for_byte()
    {
        var (plain, cipher) = Small.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        var body = NewBody(cdn, fetcher, SmallBytes, head: plain[..Audio.HeadMaxBytes]);
        using var stream = new Audio.BodyStream(body);
        body.Start();

        Assert.Equal(SmallBytes - Skip, stream.Length);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(Expected(plain, 0, SmallBytes - Skip), copy.ToArray());

        stream.Seek(100_000, SeekOrigin.Begin);
        var some = new byte[1_000];
        stream.ReadExactly(some);
        Assert.Equal(Expected(plain, 100_000, 1_000), some);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static void PrimeCache(string directory, byte[] plain, byte[] cipher)
    {
        using var disk = new ChunkDiskCache(directory);
        var cdn = new FakeCdn(cipher);
        using (var fetcher = new Audio.Fetcher())
        using (var body = NewBody(cdn, fetcher, cipher.Length, disk: disk))
        {
            body.Start();
            Assert.Equal(Expected(plain, 0, cipher.Length - Skip), Read(body, 0, cipher.Length - Skip));
            WaitIdle(fetcher);
        }
        Assert.True(disk.WaitForPendingWrites(10_000));
    }

    static void SkipWhenTheVolumeIsInsideTheReserve(string directory)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        Assert.SkipUnless(Audio.DiskCache.CanCommit(drive.AvailableFreeSpace, 64L << 20, drive.TotalSize),
            "the temp volume is inside the cache's max(5 GiB, 5 %) free-space reserve, so every commit is refused by design");
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-audiostream-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The fake wire: serves <paramref name="cipher"/>, records every open as [start, end), tracks how many are
    /// live, and can hold opens until released or cancelled.</summary>
    sealed class FakeCdn(byte[] cipher) : Audio.IRangeSource
    {
        readonly Lock _gate = new();
        readonly List<(long Start, long End)> _ranges = [];
        readonly ManualResetEventSlim _open = new(true);
        int _live, _peak, _cancels;

        public bool Refuse { get; init; }
        public (long Start, long End)[] Ranges { get { lock (_gate) return [.. _ranges]; } }
        public int Count { get { lock (_gate) return _ranges.Count; } }
        public int Live => Volatile.Read(ref _live);
        public int PeakLive => Volatile.Read(ref _peak);
        public int CancelsObserved => Volatile.Read(ref _cancels);
        public void Hold() => _open.Reset();
        public void Release() => _open.Set();

        public Audio.IRangeReply? Open(string url, long start, long end, CancellationToken ct)
        {
            int live = Interlocked.Increment(ref _live);
            lock (_gate)
            {
                _ranges.Add((start, end + 1));
                if (live > _peak) _peak = live;
            }
            try
            {
                try { _open.Wait(ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancels); return null; }
                if (Refuse || start >= cipher.Length) return null;
                return new Reply(cipher, start, Math.Min(end + 1, cipher.Length));
            }
            finally { Interlocked.Decrement(ref _live); }
        }

        sealed class Reply(byte[] data, long start, long stop) : Audio.IRangeReply
        {
            long _at = start;

            public long TotalLength => data.Length;

            public int Read(Span<byte> dst)
            {
                int n = (int)Math.Min(dst.Length, stop - _at);
                if (n <= 0) return 0;
                data.AsSpan((int)_at, n).CopyTo(dst);
                _at += n;
                return n;
            }

            public void Dispose() { }
        }
    }
}
