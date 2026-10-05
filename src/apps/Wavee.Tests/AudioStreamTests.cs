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
using Ogg = Wavee.Playback.Ogg;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioStreamCollection
{
    /// <summary>The fetch task and the fake CDN are timing-honest; they run alone so a loaded runner cannot starve them.</summary>
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
        float peak = 0f, string[]? mirrors = null, Func<string, string[]?>? reresolve = null, bool gainKnown = true,
        bool metered = false, Audio.BodyDecrypt? decrypt = null, Audio.Format fmt = Audio.Format.OggVorbis320)
    {
        long estimate = Skip + 60_000L * Audio.NominalBytesPerSecond(Audio.Format.OggVorbis320) / 1000;
        return new Audio.Body(cdn, mirrors ?? ["https://cdn-a.test/audio", "https://cdn-b.test/audio"], Key, Skip,
            known ? length : estimate, known, 60_000, fmt, 0f, fileId, head, disk, fetcher,
            metered: metered, waitMs: waitMs, peak: peak, prepared: prepared, gainKnown: gainKnown, reresolve: reresolve,
            decrypt: decrypt);
    }

    /// <summary>The open's seams over <paramref name="cdn"/>: the head, resolve and key COUNT their calls, work runs inline.</summary>
    sealed class OpenCounters
    {
        public int Heads, Resolves, Keys;
    }

    static Audio.OpenSeams Seams(FakeCdn cdn, Audio.Fetcher fetcher, OpenCounters counters, byte[]? head = null,
        ChunkDiskCache? disk = null)
        => new(cdn, disk,
            Head: (_, _) => { Interlocked.Increment(ref counters.Heads); return head ?? []; },
            Resolve: (_, _) =>
            {
                Interlocked.Increment(ref counters.Resolves);
                return new Audio.Mirrors(["https://cdn-a.test/audio"], long.MaxValue, Audio.Fault.None);
            },
            Key: (string _, ReadOnlySpan<byte> _, ReadOnlySpan<byte> _, Span<byte> key16, bool _, CancellationToken _) =>
            {
                Interlocked.Increment(ref counters.Keys);
                Key.CopyTo(key16);
                return Audio.Fault.None;
            },
            ExternalLength: (_, _) => 0,
            Run: work => { work(); return true; },
            Fetcher: fetcher);

    static Audio.FileChoice Choice(Audio.Format format, float catalogueGain = 0f, string fileId = FileA)
        => new(new byte[20], fileId, new byte[16], format, 60_000, catalogueGain, null, Audio.Fault.None);

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
    public void Far_seek_is_one_probe_one_fill_range_and_last_the_slots_behind_the_landing()
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
        (long Start, long End) probe = ProbeRange(far, BigBytes);
        long landingChunk = probe.Start / Slot;
        Assert.Equal(probe, after[0]);                                  // the 48 KiB probe, aligned out at both ends
        Assert.Equal((probe.End, (long)BigBytes), after[1]);            // the fill from the landing page to EOF
        // S-11: a cold landing owes the slots just behind it (a step back after the seek is then a ring hit). They are the LOWEST-priority
        // range — planned only once the window ahead is whole — and ONE request: KeepBehindSlots / 2 whole slots ending at the landing's.
        Assert.Equal(((landingChunk - Audio.Ring.KeepBehindSlots / 2) * Slot, landingChunk * Slot), after[2]);
        Assert.Equal(3, fetcher.Requests - before);
        Assert.Equal(3, after.Length);
        Assert.True(body.IsResident(landingChunk * Slot - Skip - 1), "the slot just behind the landing is resident");
        output.WriteLine($"far seek: probe 1 + fill {fetcher.Requests - before - 2} + behind 1; open cost {before}");
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

    // ── the async fetch: a broken source, a cancelled read, a mid-body wire fault ─────────────────────────────────────

    [Fact]
    public void A_source_that_throws_settles_the_range_and_the_ring_plans_again_and_recovers()
    {
        // The bug this batch closes: a sync-over-async fault used to escape `Serve` entirely and never reach
        // `body.Settle(...)`, so the ring's `_pendingStart` never cleared and `TryPlan` starved forever. Now every
        // exception outside the wire is caught by `ServeAsync`'s last resort, settled as `Refused`, and the ring
        // plans again — a broken source recovers the moment it stops being broken.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { ThrowOnOpen = true };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300);
        body.Start();

        Assert.Equal(Audio.Body.Starved, body.ReadAt(0, new byte[4_096], body.Epoch));  // a fault is a starve: not EOF, not −1
        WaitUntil(() => fetcher.Faults >= 2, "a second range to be planned after the first faulted one settled");
        Assert.Equal(0, fetcher.InFlight);

        cdn.ThrowOnOpen = false;
        Assert.Equal(Expected(plain, 0, 100_000), ReadThroughStarves(body, 0, 100_000)); // the same body, the same ring, recovered
        WaitIdle(fetcher);
        Assert.Equal(1, cdn.PeakLive);                                                  // never more than one range in flight
    }

    /// <summary>Container bytes [offset, offset + count) read the way the product's own reader reads them
    /// (<see cref="Audio.BodyStream"/>, D5): a <see cref="Audio.Body.Starved"/> is "call again", never the end; any other
    /// non-positive answer fails. For a recovery, whose first range may only be planned once the refusal backoff the
    /// last fault armed (250 ms) has run out — most of a short test bound, so ONE bounded wait would be a wall-clock
    /// race, not a proof. <see cref="WaitUntil"/>'s 20 s is the hang guard.</summary>
    static byte[] ReadThroughStarves(Audio.Body body, long offset, int count)
    {
        var dst = new byte[count];
        int got = 0;
        WaitUntil(() =>
        {
            int n = body.ReadAt(offset + got, dst.AsSpan(got), body.Epoch);
            if (n > 0) got += n;
            else Assert.Equal(Audio.Body.Starved, n);
            return got == count;
        }, $"{count} bytes at {offset} to be served");
        return dst;
    }

    [Fact]
    public void A_cancel_during_a_body_read_settles_the_range_stale_and_the_probe_goes_next()
    {
        // Distinct from `Retarget_cancels_the_in_flight_range_and_the_probe_goes_first` above: THIS cancel lands
        // inside the body READ (`HttpReply.ReadAsync`'s pass-through), not the open. `FetchRangeAsync`'s exception
        // filter order is what makes it settle Stale and not Refused — the cancellation-first `catch` must win over
        // the wire-fault filter even though the runtime wraps a cancelled read the same way a torn one is wrapped.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        cdn.HoldReads();
        body.Start();
        WaitUntil(() => cdn.Count == 1 && cdn.Live == 0, "range 1 opened and blocked in its first read");

        const long far = 2_600_000;
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        WaitUntil(() => cdn.ReadCancelsObserved == 1, "the held read to observe its token");
        WaitUntil(() => cdn.Count >= 2, "the probe to reach the wire");

        Assert.Equal(1, fetcher.Cancelled);
        Assert.Equal(0, cdn.CancelsObserved);          // the cancel landed in the read, not the open
        Assert.Equal(0, fetcher.Faults);                // Stale, not the fault path
        Assert.Equal(0, body.Resolves);                 // Stale invalidates no mirror set
        Assert.Equal(ProbeRange(far, BigBytes), cdn.Ranges[1]);

        cdn.ReleaseReads();
        Assert.Equal(Expected(plain, far, 16_000), Read(body, far, 16_000));
        WaitIdle(fetcher);
        Assert.Equal(1, fetcher.PeakInFlight);
    }

    [Fact]
    public void A_mirror_that_faults_mid_body_falls_through_to_the_next_one()
    {
        // A torn HTTP/2 stream (`IOException`) on ONE mirror is not a refusal of the range — the next mirror is
        // asked the SAME range, and nothing about it counts as a `Fetcher.Faults` fault or invalidates the mirror
        // set (that only happens once every mirror has been tried and none answered).
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { FaultPrefix = "https://flaky.", FaultAfterBytes = 4_096 };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes,
            mirrors: ["https://flaky.cdn-a.test/audio", "https://cdn-b.test/audio"]);
        body.Start();
        Assert.Equal(Expected(plain, 0, 100_000), Read(body, 0, 100_000));
        WaitIdle(fetcher);

        string[] urls = cdn.Urls;
        var ranges = cdn.Ranges;
        Assert.StartsWith("https://flaky.", urls[0]);
        Assert.StartsWith("https://cdn-b.", urls[1]);
        Assert.Equal(ranges[0], ranges[1]);            // the same range, re-asked of the next mirror
        for (int i = 2; i < urls.Length; i++) Assert.StartsWith("https://cdn-b.", urls[i]);
        Assert.Equal(0, fetcher.Faults);
        Assert.Equal(0, body.Resolves);
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

        body.ResumeFrom(landing);                                       // the landing: the held fill goes out, once …
        WaitIdle(fetcher);
        Assert.False(body.Ring.FillHeld);
        Assert.Equal(4, fetcher.Requests - before);                     // … and then, last, the slots behind it (S-11)
        Assert.Equal((ProbeRange(landing, BigBytes).End, (long)BigBytes), cdn.Ranges[opens + 2]);
        // The slots behind are the LANDING's (the last probe's), never the first probe's: KeepBehindSlots / 2 whole slots ending where the
        // landing probe begins, one request, planned only once the window ahead was whole.
        long landingChunk = ProbeRange(landing, BigBytes).Start / Slot;
        Assert.Equal(((landingChunk - Audio.Ring.KeepBehindSlots / 2) * Slot, landingChunk * Slot), cdn.Ranges[opens + 3]);
        Assert.Equal(cancelled, fetcher.Cancelled);                     // nothing was ever cancelled for any of it
        output.WriteLine($"two-probe seek: {fetcher.Requests - before} requests (2 probes + 1 fill at the landing + 1 behind it)");
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

        Assert.Equal(Audio.Ring.Starved, body.ReadAt(0, new byte[4_096], body.Epoch));   // starved against the held range
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

        Assert.Equal(Audio.Ring.Starved, n);                            // a starve, never a 0 the decoder would call EOF
        Assert.Equal(1, body.Ring.Starves);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 5_000);
        Assert.True(body.StallMs >= 250, $"stall {body.StallMs} ms");   // what the pump folds into "Reconnecting"
        cdn.Release();
    }

    [Fact]
    public void Every_mirror_refusing_backs_off_instead_of_spinning()
    {
        // "Backs off" is an invariant of the CDN's own request log, not a request count per second of wall clock (a count
        // is the rate times however long the reader happened to wait — a late reader made it 11). The fake stamps every
        // open with Environment.TickCount64, the clock the ring arms its backoff on, so each re-request of the refused fill
        // must come at least Ring.RefusedBackoffMs after the previous one however the box is loaded. The retry is made
        // certain, not likely: the second read starts once the ring's own backoff has run out (Ring.RetryAfter), so its
        // first plan is a re-request.
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { Refuse = true };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 1_000);
        body.Start();

        Assert.Equal(Audio.Ring.Starved, body.ReadAt(0, new byte[4_096], body.Epoch));
        WaitIdle(fetcher);
        long retryAfter = body.Ring.RetryAfter;
        Assert.True(retryAfter > 0, "every mirror refused, so the ring armed its backoff");
        WaitUntil(() => Environment.TickCount64 >= retryAfter, "the ring's refusal backoff to run out");
        Assert.Equal(Audio.Ring.Starved, body.ReadAt(0, new byte[4_096], body.Epoch));
        WaitIdle(fetcher);

        var opens = cdn.Opens;
        Assert.Equal(2 * fetcher.Requests, opens.Length);               // both mirrors, once each, per range
        var fills = opens.Where(o => o.Start == 0).ToArray();           // the reader's slot, mirror a then mirror b per range
        Assert.True(fills.Length >= 4 && fills.Length % 2 == 0, $"{fills.Length} opens of the refused fill: the first request and at least one re-request, both mirrors each");
        for (int i = 2; i < fills.Length; i += 2)
            Assert.True(fills[i].Tick - fills[i - 2].Tick >= Audio.Ring.RefusedBackoffMs,
                $"fill #{i / 2} re-requested {fills[i].Tick - fills[i - 2].Tick} ms after the previous one: a spin, not a {Audio.Ring.RefusedBackoffMs} ms backoff");
        output.WriteLine($"{fetcher.Requests} ranges; {fills.Length / 2} of the refused fill, spaced {string.Join(", ", Enumerable.Range(1, fills.Length / 2 - 1).Select(k => fills[2 * k].Tick - fills[2 * k - 2].Tick))} ms");
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

    // ── gap batch B4: the starve rule, landing, re-resolve, the reply rule, the gain, growth, the open ──────────────────

    [Fact]
    public void A_slow_range_lands_slot_by_slot_so_a_reader_keeping_pace_never_starves()
    {
        // G-102: at ~64 KB/s a 512 KiB range took the reader's whole bound before a byte of it was served. Landed a slot at
        // a time, a reader that asks for the next slot while it arrives waits a slot's time, never the range's.
        // A STEPPED link, not a timed one: the sequential ranges hand out bytes only against credits, and the feeder grants
        // exactly the slot the reader is blocked on (the ring publishes `Want` before it waits) and nothing ahead of it.
        // So every read below returns while the rest of its range is still on the far side of the link — the landing is
        // proven by construction, not by a delay-vs-bound ratio the scheduler can break. The default 8 s bound is only a
        // hang guard; the tail probe the open queues (past `SteppedBelow`) flows freely — it is not the range under test.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { SteppedBelow = 2L * Audio.Fetcher.MaxRangeBytes };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        using var stop = new CancellationTokenSource();
        Thread feeder = FeedTheSlotTheReaderWants(body, cdn, stop.Token);
        body.Start();

        const int span = 700_000;
        var got = new byte[span];
        int at = 0;
        long overServed = 0;                                             // link bytes past the slot a read was served from
        try
        {
            while (at < span)
            {
                int n = body.ReadAt(at, got.AsSpan(at), body.Epoch);
                Assert.True(n > 0, $"read at {at} answered {n}");
                long slotEnd = Audio.Ring.AlignDown(Skip + at) + Audio.Ring.SlotBytes;
                overServed = Math.Max(overServed, cdn.SteppedServed - slotEnd);
                at += n;
            }
        }
        finally
        {
            stop.Cancel();
            feeder.Join();
        }

        Assert.Equal(Expected(plain, 0, span), got);
        (long Start, long End) first = cdn.Ranges.First(r => r.Start == 0);
        Assert.True(first.End - first.Start > Audio.Ring.SlotBytes, $"the first range [{first.Start}, {first.End}) is one slot: nothing to prove");
        Assert.True(overServed <= 0, $"a read waited for {overServed} bytes past its own slot: the range landed whole, not slot by slot");
        Assert.Equal(0, body.Ring.Starves);
        Assert.Equal(0L, body.StallMs);                                  // bytes flowed at the last read: no stall to report
        output.WriteLine($"stepped read of {span} bytes: {body.Ring.Waits} waits, 0 starves, {fetcher.Requests} ranges, first range {first.End - first.Start} bytes");
    }

    /// <summary>The reader's side of a stepped link (<see cref="FakeCdn.SteppedBelow"/>): whenever the ring is blocked on a
    /// slot not yet granted, grant exactly that slot. Stepped bytes cross the link in file order from 0 (the sequential
    /// fill), so the granted edge is also the next slot to grant. A dedicated thread, not a pool task: the feeder must not
    /// queue behind the very pool work it is feeding.</summary>
    static Thread FeedTheSlotTheReaderWants(Audio.Body body, FakeCdn cdn, CancellationToken stop)
    {
        var feeder = new Thread(() =>
        {
            long granted = 0;
            while (!stop.IsCancellationRequested)
            {
                long want = body.Ring.Want;
                if (want >= 0 && Audio.Ring.AlignDown(want) >= granted)
                {
                    cdn.Step(Audio.Ring.SlotBytes);
                    granted += Audio.Ring.SlotBytes;
                }
                else Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "stepped-link feeder" };
        feeder.Start();
        return feeder;
    }

    [Fact]
    public void A_starve_is_never_the_end_the_stall_is_counted_and_bytes_clear_it()
    {
        // Structural at every step. The HELD link (cdn.Hold) is what starves the two reads: nothing can land while it is
        // held, whatever the scheduler does. The stall clock is Environment.TickCount64 across two whole bounded waits (the
        // ring's own deadline, ≥ 2 × 200 ms), which load can only lengthen. After the release the bytes are read once the
        // fetcher has LANDED them (Outstanding back to 0): what clears the stall is bytes arriving — not one read racing
        // the fetch thread inside its 200 ms bound (on a busy thread pool that race lost: the read starved a third time).
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 200);
        cdn.Hold();
        body.Start();

        var dst = new byte[4_096];
        Assert.Equal(Audio.Body.Starved, body.ReadAt(0, dst, body.Epoch));
        Assert.Equal(Audio.Body.Starved, body.ReadAt(0, dst, body.Epoch));       // asked again: still waiting, still not EOF
        Assert.True(body.StallMs >= 300, $"stall {body.StallMs} ms after two bounded 200 ms waits");

        cdn.Release();
        WaitIdle(fetcher);                                                        // the held range has landed
        Assert.True(body.StallMs > 0, "no read has run since the stall began: it is still counted");
        Assert.Equal(dst.Length, body.ReadAt(0, dst, body.Epoch));
        Assert.Equal(Expected(plain, 0, dst.Length), dst);
        Assert.Equal(0L, body.StallMs);
    }

    [Theory]
    [InlineData(0L, Playback.Audio.StarvePolicy.Verdict.Flowing)]
    [InlineData(1_499L, Playback.Audio.StarvePolicy.Verdict.Flowing)]
    [InlineData(1_500L, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(89_999L, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(90_000L, Playback.Audio.StarvePolicy.Verdict.Failed)]
    public void A_stall_is_reconnecting_after_a_second_and_a_half_and_a_network_fault_after_ninety(long stallMs,
        Playback.Audio.StarvePolicy.Verdict expected)
        => Assert.Equal(expected, Playback.Audio.StarvePolicy.Decide(stallMs));

    [Fact]
    public void A_seek_inside_the_clear_head_cancels_nothing_and_sends_no_probe()
    {
        // G-116: the window is the head's, so the seek is an epoch bump — the range already on the wire keeps going.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, head: plain[..Audio.HeadMaxBytes]);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "range 1 to be held open");

        body.Retarget(1_000, Audio.Ring.ProbeWindow, body.Epoch + 1);
        Assert.Equal(Expected(plain, 1_000, 16_000), Read(body, 1_000, 16_000));

        Assert.Equal(0, fetcher.Cancelled);
        Assert.Equal(1, cdn.Count);
        Assert.Equal(0, cdn.CancelsObserved);
        cdn.Release();
        WaitIdle(fetcher);
    }

    [Fact]
    public void Expired_mirrors_are_resolved_again_and_the_read_carries_on()
    {
        // G-115: every url of the set refuses (their TTL ran out during a long pause); the body asks storage-resolve for a
        // fresh set instead of retrying dead urls until the reader gives up.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { RefusePrefix = "https://old." };
        using var fetcher = new Audio.Fetcher();
        int asked = 0;
        using var body = NewBody(cdn, fetcher, BigBytes, mirrors: ["https://old.cdn-a.test/audio", "https://old.cdn-b.test/audio"],
            reresolve: _ => { Interlocked.Increment(ref asked); return ["https://new.cdn.test/audio"]; });
        body.Start();

        Assert.Equal(Expected(plain, 0, 100_000), Read(body, 0, 100_000));
        Assert.Equal(1, asked);
        Assert.Equal(1, body.Resolves);
        WaitIdle(fetcher);
    }

    [Fact]
    public void A_body_with_no_mirrors_resolves_them_on_its_first_miss()
    {
        // G-120: a body opened off the cache asked nothing; the first byte the cache cannot answer resolves the urls.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        int asked = 0;
        using var body = NewBody(cdn, fetcher, BigBytes, mirrors: [],
            reresolve: _ => { Interlocked.Increment(ref asked); return ["https://cdn.test/audio"]; });
        body.Start();

        Assert.Equal(Expected(plain, 0, 50_000), Read(body, 0, 50_000));
        Assert.Equal(1, asked);
        WaitIdle(fetcher);
    }

    [Theory]
    [InlineData(0L, 0L, 0L)]                   // asked 0, got 0
    [InlineData(524_288L, 524_288L, 0L)]       // a 206 where it was asked
    [InlineData(524_288L, 0L, 524_288L)]       // a 200 from byte 0: read past the start
    [InlineData(8_000_000L, 0L, -1L)]          // too far to read past: refused
    [InlineData(524_288L, 65_536L, -1L)]       // a 206 somewhere else: refused
    public void A_reply_that_does_not_start_where_it_was_asked_is_read_past_or_refused(long asked, long replyStart, long expected)
        => Assert.Equal(expected, Audio.RangeReply.SkipFor(asked, replyStart));

    [Fact]
    public void A_host_that_ignores_range_serves_the_right_bytes_after_the_first_range()
    {
        // G-118: every range after the first used to be taken as its own start, corrupting the rest of the track.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { IgnoreRange = true };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();

        Assert.Equal(Expected(plain, 0, 1_500_000), Read(body, 0, 1_500_000));
        WaitIdle(fetcher);
    }

    [Fact]
    public void An_ogg_body_with_no_header_at_hand_learns_its_gain_and_peak_from_chunk_zero()
    {
        // G-107: a head GET that failed on an uncached file used to leave the track un-normalized for good.
        var (_, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(new FakeCdn(cipher), fetcher, BigBytes, gainKnown: false);
        Assert.False(body.GainKnown);
        Assert.Equal(0f, body.GainDb);
        body.Start();
        WaitIdle(fetcher);

        Assert.True(body.GainKnown);
        Assert.Equal(-3.5f, body.GainDb);
        Assert.Equal(HeaderPeak, body.Peak);
    }

    [Fact]
    public void A_waiting_prepare_sees_the_tail_the_moment_it_lands()
    {
        var (_, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(new FakeCdn(cipher), fetcher, BigBytes);
        body.Start();

        Assert.True(body.WaitForTail(10_000, CancellationToken.None));
        Assert.Equal(LastGranule, body.TailGranule);
        WaitIdle(fetcher);
    }

    [Fact]
    public void A_promoted_prepared_body_grows_its_ring_to_the_measured_tier_and_keeps_its_bytes()
    {
        // G-114: a prepared track's ring is sized ≤ 30 s for the hand-off; once it IS the playing track on a link that earns
        // the 600 s tier, it grows — out of the shared budget — without losing a resident byte.
        var (plain, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        using (var warm = NewBody(new FakeCdn(cipher), fetcher, BigBytes, fileId: FileB))
        {
            warm.Start();
            WaitIdle(fetcher);                                          // the fetcher has measured a fast link now
        }
        var cdn = new FakeCdn(cipher);
        using var next = NewBody(cdn, fetcher, BigBytes, prepared: true);
        next.Start();
        WaitIdle(fetcher);
        int before = next.Ring.Slots;
        long inUse = Audio.ReadAheadBudget.InUseBytes;
        Assert.True(next.Ring.Seconds <= Audio.ReadAheadBudget.PreparedSeconds);

        next.Promote();
        WaitIdle(fetcher);

        Assert.True(next.Ring.Slots > before, $"slots {before} -> {next.Ring.Slots}");
        Assert.Equal(inUse + (long)(next.Ring.Slots - before) * Slot, Audio.ReadAheadBudget.InUseBytes);
        Assert.Equal(Expected(plain, 0, 400_000), Read(next, 0, 400_000));
        Assert.Equal(Expected(plain, 2_600_000, 100_000), Read(next, 2_600_000, 100_000));
        output.WriteLine($"promoted: {before} -> {next.Ring.Slots} slots, ring {next.Ring.Seconds}s");
    }

    [Fact]
    public void A_growing_ring_is_granted_what_is_left_and_possibly_nothing()
    {
        int total = (int)(Audio.ReadAheadBudget.TotalBytes / Slot);
        Assert.Equal(40, Audio.ReadAheadBudget.GrantExtra(40, 0));
        Assert.Equal(10, Audio.ReadAheadBudget.GrantExtra(40, (long)(total - 10) * Slot));
        Assert.Equal(0, Audio.ReadAheadBudget.GrantExtra(40, Audio.ReadAheadBudget.TotalBytes));
        Assert.Equal(0, Audio.ReadAheadBudget.GrantExtra(-3, 0));
    }

    [Fact]
    public void A_disposed_body_hands_its_slot_arrays_to_the_pool()
    {
        var (_, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        var body = NewBody(new FakeCdn(cipher), fetcher, BigBytes, fileId: FileB);
        int slots = body.Ring.Slots;
        body.Dispose();

        Assert.True(Audio.ReadAheadBudget.PooledSlots >= Math.Min(slots, Audio.ReadAheadBudget.PoolMaxSlots));
        Assert.Equal(-1, body.ReadAt(0, new byte[16], body.Epoch));     // gone: −1, never a starve a reader would wait on
    }

    [Fact]
    public void A_cold_open_asks_the_head_the_mirrors_and_the_key_once_each()
    {
        // G-134: `OpenBody`'s parallel trio, through its seams.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        var counters = new OpenCounters();

        Audio.Opened opened = Audio.OpenBody(Choice(Audio.Format.OggVorbis320),
            Seams(cdn, fetcher, counters, head: plain[..Audio.HeadMaxBytes]), CancellationToken.None);
        using var stream = opened.Stream;
        Audio.Body body = opened.Body!;
        WaitIdle(fetcher);

        Assert.True(opened.Ok);
        Assert.Equal((1, 1, 1), (counters.Heads, counters.Resolves, counters.Keys));
        Assert.Equal(-3.5f, opened.GainDb);                             // the Ogg header's gain, off the clear head
        Assert.Equal(HeaderPeak, opened.Peak);
        Assert.Equal(Expected(plain, 0, 200_000), Read(body, 0, 200_000));
    }

    [Fact]
    public void A_native_decryptor_replaces_the_key_stream_for_its_file()
    {
        // D3, the PlayPlay seam's second half: a file whose key path left a native decryptor is decrypted through it —
        // here a byte mask — and never through the AES-CTR keystream of the key the open was handed.
        var (plain, _) = Big.Value;
        byte[] masked = (byte[])plain.Clone();
        UnmaskInPlace(masked, 0);
        using var fetcher = new Audio.Fetcher();
        var seams = Seams(new FakeCdn(masked), fetcher, new OpenCounters()) with
        {
            Decryptor = static hex => hex == FileA ? new Audio.BodyDecrypt(UnmaskInPlace) : null,
        };

        Audio.Opened opened = Audio.OpenBody(Choice(Audio.Format.OggVorbis320), seams, CancellationToken.None);
        using var stream = opened.Stream;
        WaitIdle(fetcher);

        Assert.True(opened.Ok);
        Assert.Equal(Expected(plain, 0, 200_000), Read(opened.Body!, 0, 200_000));
    }

    /// <summary>The fixture "native" transform: XOR with a position-dependent byte, so a decryptor that ignored the stream
    /// offset would corrupt every range but the first.</summary>
    static void UnmaskInPlace(Span<byte> buffer, long streamOffset)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] ^= (byte)(0x5A ^ ((streamOffset + i) >> 12));
    }

    [Fact]
    public void A_flac_open_never_reads_a_gain_out_of_its_stream_info()
    {
        // G-105: byte 144 of a FLAC is STREAMINFO/SEEKTABLE data; read as a float it was up to +30 dB.
        var (plain, cipher) = Big.Value;
        using var fetcher = new Audio.Fetcher();
        Audio.Opened opened = Audio.OpenBody(Choice(Audio.Format.Flac),
            Seams(new FakeCdn(cipher), fetcher, new OpenCounters(), head: plain[..Audio.HeadMaxBytes]), CancellationToken.None);
        using var stream = opened.Stream;

        Assert.Equal(0f, opened.GainDb);
        Assert.Equal(0f, opened.Peak);
        Assert.True(opened.Body!.GainKnown);
        WaitIdle(fetcher);
    }

    [Fact]
    public void A_cached_file_opens_with_no_head_no_resolve_and_no_range()
    {
        // G-120, plan §5.4: a cached replay is zero requests. Only the key is asked for (it is not cached across launches).
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        var counters = new OpenCounters();
        Audio.Opened opened = Audio.OpenBody(Choice(Audio.Format.OggVorbis320),
            Seams(cdn, fetcher, counters, disk: disk), CancellationToken.None);
        using var stream = opened.Stream;
        Audio.Body body = opened.Body!;

        Assert.Equal(Expected(plain, 0, BigBytes - Skip), Read(body, 0, BigBytes - Skip));
        WaitIdle(fetcher);

        Assert.Equal((0, 0, 1), (counters.Heads, counters.Resolves, counters.Keys));
        Assert.Empty(cdn.Ranges);
        Assert.Equal(-3.5f, opened.GainDb);                             // the header came off the cached chunk 0
    }

    [Fact]
    public void An_external_body_s_length_comes_from_a_range_probe_when_the_host_has_no_head_length()
    {
        // G-119: a host that answers HEAD with no Content-Length used to fail the episode as a network fault.
        // `ProbeLength` is gone (folded into the `OpenSeams.ExternalLength` seam itself); exercised end to end
        // through `OpenBody` now, over the three answers that seam can give.
        const string url = "https://podcast.test/episode.mp3";

        // >0: the HEAD length names it directly.
        {
            var cdn = new FakeCdn(Big.Value.Cipher);
            using var fetcher = new Audio.Fetcher();
            Audio.OpenSeams seams = Seams(cdn, fetcher, new OpenCounters()) with { ExternalLength = (_, _) => BigBytes };
            Audio.Opened opened = Audio.OpenBody(Audio.ExternalChoice(url, 60_000), seams, CancellationToken.None);
            using var stream = opened.Stream;
            Assert.True(opened.Ok);
            Assert.True(opened.Body!.LengthKnown);
            Assert.Equal((long)BigBytes, opened.Body!.Length);
            WaitIdle(fetcher);
        }

        // 0: reachable but unnamed (a host that answers neither HEAD nor a Content-Range total) — opens on the
        // duration's estimate, and the first range names the truth off the fake's own `TotalLength`.
        {
            var cdn = new FakeCdn(Big.Value.Cipher);
            using var fetcher = new Audio.Fetcher();
            Audio.OpenSeams seams = Seams(cdn, fetcher, new OpenCounters()) with { ExternalLength = (_, _) => 0 };
            cdn.Hold();                                  // the open's own `Start` puts the first range on the wire at once:
            // held, it cannot name the length before the "opens unnamed" assertion reads it.
            Audio.Opened opened = Audio.OpenBody(Audio.ExternalChoice(url, 60_000), seams, CancellationToken.None);
            using var stream = opened.Stream;
            Audio.Body body = opened.Body!;
            Assert.True(opened.Ok);
            Assert.False(body.LengthKnown);
            cdn.Release();
            Read(body, 0, 4_096);
            WaitIdle(fetcher);
            Assert.True(body.LengthKnown);
            Assert.Equal((long)BigBytes, body.Length);
        }

        // −1: unreachable — the open itself fails.
        {
            var cdn = new FakeCdn(Big.Value.Cipher) { Refuse = true };
            using var fetcher = new Audio.Fetcher();
            Audio.OpenSeams seams = Seams(cdn, fetcher, new OpenCounters()) with { ExternalLength = (_, _) => -1 };
            Audio.Opened opened = Audio.OpenBody(Audio.ExternalChoice(url, 60_000), seams, CancellationToken.None);
            using var stream = opened.Stream;
            Assert.False(opened.Ok);
            Assert.Equal(Audio.Fault.Network, opened.Fault);
        }
    }

    [Fact]
    public void The_audio_cache_lives_where_0_2_9_put_it()
    {
        // D8, G-122: `%LOCALAPPDATA%\Wavee\Wavee\Cache\audio` — the root 0.2.9's AppDataStore("Wavee", "Wavee") answered,
        // so an upgrade replays its cache instead of orphaning it.
        string local = Path.Combine("C:", "Users", "someone", "AppData", "Local", "Wavee");
        Assert.Equal(Path.Combine(local, "Wavee", "Cache", "audio"), Audio.DiskCache.DirectoryUnder(local));
    }

    // ── playback smoothness (#167), wave 0: S-2, S-12, S-13, P-6, H-12, R-6 ───────────────────────────────────────────
    //
    // Plan docs/plans/wavee/playback-smoothness-implementation.md §4.16 / §5. The facts are the audit's stream findings:
    //
    //   S-2   The only range deadline was a 60 s TOTAL: a socket that stopped sending mid-range held the wire for a minute while
    //         the ring starved in 8 s rounds. Now an IDLE deadline (`Fetcher.RangeIdleTimeoutMs`, re-armed after every read that
    //         returned bytes), and a starving read cancels the range that should have answered it. (The idle deadline's own
    //         end-to-end test — a link that goes quiet for the production 8 s — was an ~11 s wall-clock fact and was dropped: the
    //         starve-cancel test below covers the same settle-Stale-and-replan path in 200 ms, and the deadline's rule is pure.)
    //   S-12  `FillFromDisk` ran BEFORE `_inFlight` was registered, so a seek that arrived during up to eight SHA-256-verified disk
    //         reads could not cancel the stale fill, and the probe queued behind it.
    //   S-13  For windows ≤ 512 KiB (metered, 96k, prepared) the low-water mark collapsed to ~80 KiB: the refill was a whole 512 KiB
    //         range requested with 1.6-2 s of audio left.
    //   P-6   A wire fault after some slots landed, then refusals, set `_refusedAt`: `Refusing` and the pump's 6 s fast-fail
    //         applied to a slow-but-alive link.
    //   H-12  `WidenWhenProven` ran inside `ReadAt` — on the engine's decode-ahead producer — and rented slots / re-hashed four
    //         tables under the ring's gate. It now raises a flag the fetch task polls.
    //   R-6   `Fetcher.Faults` was not in `Stream.Stats`.

    [Fact]
    public void A_read_that_starves_cancels_the_range_that_should_have_answered_it_and_the_ring_plans_it_again()
    {
        // S-2's second half: the idle deadline re-arms on every byte, so a TRICKLE never trips it. A read that has waited its whole
        // bound (8 s in production, 200 ms here) cancels the range its offset lies in — it settles Stale and the ring plans it again
        // from its first hole, every slot that landed kept.
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { SteppedBelow = 2L * MaxRange };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 200);
        cdn.Step(Slot);                                                   // slot 0 only: the range is then on the wire and quiet
        body.Start();
        WaitUntil(() => body.IsResident(0), "the first slot to land");
        Assert.Equal(0, fetcher.Cancelled);

        var dst = new byte[4_096];
        Assert.Equal(Audio.Body.Starved, body.ReadAt(Slot, dst, body.Epoch));   // slot 1 is inside the range that is still on the wire

        WaitUntil(() => cdn.ReadCancelsObserved == 1, "the quiet range to be cancelled by the starving read");
        Assert.Equal(1, fetcher.Cancelled);
        Assert.Equal(0, fetcher.Faults);                                  // a cancel, not a fault
        Assert.Equal(1, body.Ring.Starves);
        WaitUntil(() => cdn.Ranges.Any(r => r.Start == Slot), "the ring to plan the cancelled range's remainder again");
    }

    [Fact]
    public void A_seek_during_the_disk_fill_reaches_that_range_and_its_remainder_never_goes_to_the_wire()
    {
        // S-12. The fill's range is [0, 512 KiB); the disk answers its first chunk and the fetch task is parked INSIDE that read
        // (the decrypt seam blocks it — a disk answer is real time: eight SHA-256-verified 64 KiB reads). A seek arrives. Before the
        // fix the range was not yet "in flight", so the seek's cancel found nothing and the probe queued behind the stale fill; the
        // stale remainder then went to the wire.
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        using var disk = new ChunkDiskCache(dir.Path);
        disk.SetSize(FileA, BigBytes);
        disk.WriteChunk(FileA, 0, cipher.AsSpan(0, Slot));                // chunk 0 only (ciphertext: the body decrypts at the true offset)
        Assert.True(disk.WaitForPendingWrites(10_000));

        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var inTheFill = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        int armed = 0;
        int testThread = Environment.CurrentManagedThreadId;
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk, decrypt: (buffer, at) =>
        {
            Audio.Ctr.DecryptInPlace(buffer, Key, at);
            if (Environment.CurrentManagedThreadId == testThread) return;
            if (Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref armed, 0) == 1) { inTheFill.Set(); proceed.Wait(); }
        });

        try
        {
            Volatile.Write(ref armed, 1);
            body.Start();
            Assert.True(inTheFill.Wait(20_000), "the fetch task to be inside the disk fill");

            const long far = 2_600_000;
            body.Retarget(far, Audio.Ring.ProbeWindow, 1);
            Assert.Equal(1, fetcher.Cancelled);                           // the range was registered BEFORE its disk fill: the seek reached it
        }
        finally { proceed.Set(); }

        WaitIdle(fetcher);
        (long Start, long End) probe = ProbeRange(2_600_000, BigBytes);
        Assert.Contains(probe, cdn.Ranges);
        Assert.All(cdn.Ranges, r => Assert.True(r.Start >= probe.Start, $"range [{r.Start}, {r.End}) is the stale fill's remainder, on the wire"));
        Assert.Equal(0, fetcher.Faults);
        Assert.Equal(Expected(plain, 0, 4_096), Read(body, 0, 4_096));    // chunk 0, which the disk answered, is kept: bytes are bytes
    }

    [Fact]
    public void Ranges_the_disk_answers_never_count_as_on_the_wire()
    {
        // S-12's accounting: `_live` is incremented and decremented only by ranges that went on the wire. A range answered
        // entirely from disk (Source.Local) — or cancelled/stale during the disk fill — touches neither counter.
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk);
        body.Start();
        Assert.Equal(Expected(plain, 0, 300_000), Read(body, 0, 300_000));
        body.Retarget(2_600_000, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, 2_600_000, 100_000), Read(body, 2_600_000, 100_000));
        WaitIdle(fetcher);

        Assert.Empty(cdn.Ranges);
        Assert.Equal(0, fetcher.Requests);
        Assert.Equal(0, fetcher.InFlight);                                // not −1: nothing was decremented that was never incremented
        Assert.Equal(0, fetcher.PeakInFlight);
        Assert.Equal(0, fetcher.Faults);
    }

    [Fact]
    public void A_fault_in_the_disk_fill_settles_the_range_refused_counts_in_Faults_and_the_ring_recovers()
    {
        // S-12 moved the disk fill INSIDE the fetcher's last-resort catch: a fault there (the cache, a decrypt, a bug) used to
        // escape past `body.Settle` and leave the ring's pending range set forever. Now it settles Refused, counts, and the ring
        // plans again behind its backoff — and the stats the Diagnostics page reads carry the count (R-6).
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        using var disk = new ChunkDiskCache(dir.Path);
        disk.SetSize(FileA, BigBytes);
        disk.WriteChunk(FileA, 0, cipher.AsSpan(0, Slot));
        Assert.True(disk.WaitForPendingWrites(10_000));

        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        int failOnce = 1;
        int testThread = Environment.CurrentManagedThreadId;
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300, disk: disk, decrypt: (buffer, at) =>
        {
            // NOT a wire-fault type (`FetchRangeAsync` would retry the next mirror for those): this reaches the last-resort catch.
            if (Environment.CurrentManagedThreadId != testThread && Interlocked.Exchange(ref failOnce, 0) == 1)
                throw new NotSupportedException("fake: the disk fill's decrypt faulted");
            Audio.Ctr.DecryptInPlace(buffer, Key, at);
        });
        Audio.Stream.Stats mark = Audio.Stream.Stats.Read(fetcher);
        body.Start();

        WaitUntil(() => fetcher.Faults >= 1, "the disk fill's fault to be settled");
        // It never reached the wire: no request covers the faulted disk range. (Not `fetcher.InFlight == 0`: the ring may
        // already have the NEXT range on the wire when the fault settles, which a loaded test host makes likely.)
        Assert.DoesNotContain(cdn.Ranges, r => r.Start < Slot);
        WaitUntil(() => body.Ring.RetryAfter > 0, "the range to settle Refused: the ring arms its backoff");
        Audio.Stream.Stats delta = Audio.Stream.Stats.Read(fetcher).Since(in mark);
        Assert.Equal(1, delta.Faults);                                    // R-6: in the stats, as a delta

        Assert.Equal(Expected(plain, 0, 100_000), ReadThroughStarves(body, 0, 100_000));   // the same body recovered
        WaitIdle(fetcher);
        Assert.Equal(1, fetcher.Faults);                                  // exactly the injected one
    }

    [Theory]
    [InlineData(true)]     // a metered 320k ring: its window is ONE range, so half the window is what matters (S-13)
    [InlineData(false)]    // an unmetered 30 s ring: `window − MaxRangeBytes` dominates and is unchanged
    public void The_refill_waits_for_the_low_water_mark_which_is_never_below_half_the_window(bool metered)
    {
        // The refill policy is the ring's hysteresis: once the window is whole it stays quiet until the run ahead of the cursor
        // drops below LowWaterBytes, then refills. The disk answers every range, so this observes the POLICY alone — no CDN timing:
        // the fetcher's ping and throughput keep their initial values and a range planned shows up as disk-cache hits, not requests.
        // Run ahead after the cursor has crossed k slots is (window − k) slots, so the refill starts at the first k where that is
        // below the mark — and one slot earlier it must not.
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher) = Big.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk, metered: metered);
        body.Start();
        WaitIdle(fetcher);

        int window = body.Ring.Slots - Audio.Ring.KeepBehindSlots;
        long windowBytes = (long)window * Slot;
        long librespot = Math.Min(windowBytes,
            Audio.Fetcher.PrefetchThresholdBytes(fetcher.PingMs, body.Ring.FileBytesPerSecond, fetcher.BytesPerSecond));
        long lowWater = Math.Max(Slot, Math.Max(librespot, Math.Max(windowBytes / 2, windowBytes - MaxRange)));
        Assert.True(lowWater >= windowBytes / 2, "S-13: never below half the window");
        int refillAt = (int)((windowBytes - lowWater) / Slot) + 1;       // the first slot count whose run ahead is below the mark
        Assert.InRange(refillAt, 2, window - 1);

        Audio.Stream.Stats mark = Audio.Stream.Stats.Read(fetcher);
        Read(body, 0, (refillAt - 1) * Slot + 100 - Skip);               // one slot short of the mark…
        WaitIdle(fetcher);
        Assert.Equal(0L, Audio.Stream.Stats.Read(fetcher).Since(in mark).CacheHits);   // …plans nothing

        Read(body, (refillAt - 1) * Slot + 100 - Skip, Slot);            // …and across it
        WaitIdle(fetcher);
        long hits = Audio.Stream.Stats.Read(fetcher).Since(in mark).CacheHits;
        Assert.True(hits > 0, $"window {window} slots, mark {lowWater} B: no refill planned after {refillAt} slots");
        Assert.Empty(cdn.Ranges);                                         // (the disk answered it)
        output.WriteLine($"metered={metered}: window {window} slots, low-water {lowWater} B, refill from slot {refillAt}");
    }

    [Fact]
    public void A_range_whose_mirrors_all_broke_after_a_slot_landed_is_slow_not_refused()
    {
        // P-6. Both mirrors hand out one whole slot (+ 4 KiB) and then reset the stream. Slots landed, so the link made progress:
        // the range answers what landed, `_refusedAt` stays below `_landedAt` (so `Refusing` and the pump's 6 s fast-fail do not
        // apply), no mirror set is dropped and no re-resolve is asked for. The ring plans the rest from its first hole.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { FaultPrefix = "https://flaky.", FaultAfterBytes = Slot + 4_096 };
        int reresolves = 0;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes,
            mirrors: ["https://flaky.a.test/audio", "https://flaky.b.test/audio"],
            reresolve: _ => { Interlocked.Increment(ref reresolves); return null; });
        body.Start();
        WaitUntil(() => body.BodyBytes >= 2L * Slot, "slots to keep landing, one per range");
        WaitIdle(fetcher);

        Assert.Equal(0, body.Refusals);
        Assert.False(body.Refusing);
        Assert.Equal(0, Volatile.Read(ref reresolves));                   // the mirror set was never dropped
        Assert.Equal(0, fetcher.Faults);                                  // a wire fault a mirror was retried for is not a Fault
        Assert.Equal(Expected(plain, 0, 3 * Slot), Read(body, 0, 3 * Slot));   // and what landed is served
    }

    [Fact]
    public void A_range_whose_mirrors_all_broke_before_any_slot_landed_is_still_a_refusal()
    {
        // P-6's other side: with NOTHING landed there is no progress to call slow — the url set is dropped, a re-resolve asked for.
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { FaultPrefix = "https://flaky.", FaultAfterBytes = Slot / 2 };
        int reresolves = 0;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300,
            mirrors: ["https://flaky.a.test/audio", "https://flaky.b.test/audio"],
            reresolve: _ => { Interlocked.Increment(ref reresolves); return null; });
        body.Start();

        WaitUntil(() => body.Refusals >= 1, "every mirror to fail with nothing landed");
        Assert.True(body.Refusing);
        WaitUntil(() => Volatile.Read(ref reresolves) >= 1, "a re-resolve to be asked for");
    }

    [Fact]
    public void A_widening_the_reader_asked_for_is_applied_by_the_next_serve_and_never_by_the_reader()
    {
        // H-12. `WidenWhenProven` runs inside `Body.ReadAt` — on the engine's decode-ahead producer — so it only raises a flag; the
        // fetch task polls it at the top of every range it serves (a flag, not a post: the fetcher's queue is DropOldest and could
        // drop the post). The sequence: a probe is on the wire (held) → the playhead crosses the probation on resident bytes (the
        // flag goes up, the reader rents NOTHING) → the probe's serve began before the flag, so it does not apply it either →
        // the fill that follows the landing is the next serve, and the ring grows.
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int before = body.Ring.Slots;

        cdn.Hold();
        const long far = 2_600_000;
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        WaitUntil(() => cdn.Live == 1, "the probe to be on the wire, held");

        long probationBytes = Audio.ReadAhead.ProbationMs * (long)body.Ring.FileBytesPerSecond / 1000;
        Read(body, 0, (int)probationBytes + 2 * Slot);                    // resident (the 30 s window): the playhead passes the probation
        Assert.Equal(before, body.Ring.Slots);                            // the READER only raised the flag

        cdn.Release();
        WaitIdle(fetcher);
        Assert.Equal(before, body.Ring.Slots);                            // the probe's serve started before the flag: it waits for the next

        body.ResumeFrom(far);                                             // the landing releases the held fill: the NEXT serve
        WaitIdle(fetcher);
        Assert.True(body.Ring.Slots > before, $"the widening was dropped: {before} slots before, {body.Ring.Slots} after");
        Assert.Equal(0, fetcher.Faults);
    }

    [Fact]
    public void Stats_carry_the_fetchers_fault_count_and_since_subtracts_it()
    {
        // R-6. PURE: a monotonic counter is a delta, a gauge stays the value's.
        var mark = default(Audio.Stream.Stats) with { Faults = 2, Requests = 10 };
        var now = default(Audio.Stream.Stats) with { Faults = 5, Requests = 17, PingMs = 40 };
        Audio.Stream.Stats delta = now.Since(in mark);
        Assert.Equal(3, delta.Faults);
        Assert.Equal(7L, delta.Requests);
        Assert.Equal(40, delta.PingMs);

        // LIVE: a source that throws (not a wire fault) reaches the fetcher's last-resort catch; the stats see it.
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { ThrowOnOpen = true };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 300);
        Audio.Stream.Stats before = Audio.Stream.Stats.Read(fetcher);
        Assert.Equal(0, before.Faults);
        body.Start();
        WaitUntil(() => fetcher.Faults >= 1, "the broken source's first range to settle");

        int faults = fetcher.Faults;
        Audio.Stream.Stats after = Audio.Stream.Stats.Read(fetcher);
        Assert.True(after.Faults >= faults);
        Assert.Equal(after.Faults - before.Faults, after.Since(in before).Faults);
    }

    // ── playback smoothness (#167), wave 2: S-10, S-11, the second reader (V-PA20), the page index (V-PA36), the starve rule, R-6 ──────
    //
    //   S-10  A seek is a run of probes; a probe lying inside the range THIS seek (same epoch) already has queued or on the wire is that
    //         range's business — cancelling it to ask for the same bytes again threw away the bytes already on the way.
    //   S-11  A cold landing owes the slots just behind it (a step back after a seek is then a ring hit). They are one range, planned LAST
    //         (after the window ahead is whole), and a chunk landing after the cursor moved may not evict them.
    //   V-PA20  `Body.ProbeAt` / `ProbeRange`: the seek's SECOND reader. It copies what is resident and otherwise asks for ONE view-only
    //         range queued BEHIND the live fill; it moves nothing of the owner's (cursor, epoch, interrupt, stall clock, counters).
    //   V-PA36  The page index: every Ogg page found in a chunk as it lands (CDN or disk), CONTAINER offsets, landing order, grow-only.
    //   S-2+  A starving read cancels the range that should have answered it ONLY when that range showed no life during the whole wait: a
    //         slow link that keeps trickling is never cancelled (each cancel discards the slot being filled — a livelock at ≤ ~8 KiB/s).
    //   R-6   The ping sample is the time to headers of the mirror that ANSWERED, not of the range (a failover is not a slow mirror).

    // ── pure tables ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1L, 0L, 0u, 0u, 0L, 65_536L, false)]                 // nothing pending
    [InlineData(65_536L, 196_608L, 3u, 3u, 65_536L, 196_608L, true)]  // exactly the pending range
    [InlineData(65_536L, 196_608L, 3u, 3u, 65_536L, 131_072L, true)]  // inside, same start
    [InlineData(65_536L, 196_608L, 3u, 3u, 131_072L, 196_608L, true)] // inside, same end
    [InlineData(65_536L, 196_608L, 3u, 3u, 0L, 131_072L, false)]      // starts before it
    [InlineData(65_536L, 196_608L, 3u, 3u, 65_536L, 196_609L, false)] // ends one byte past it
    [InlineData(65_536L, 196_608L, 3u, 4u, 65_536L, 131_072L, false)] // a NEWER seek's probe: it supersedes, even for the same bytes
    [InlineData(0L, 524_288L, 0u, 0u, 65_536L, 131_072L, true)]       // a pending range at offset 0 is a real range
    public void A_probe_is_covered_only_by_the_pending_range_of_the_same_seek_that_encloses_it(
        long pendingStart, long pendingEnd, uint pendingEpoch, uint epoch, long start, long end, bool expected)
        => Assert.Equal(expected, Audio.Ring.ProbeCoveredByPending(pendingStart, pendingEnd, pendingEpoch, epoch, start, end));

    [Theory]
    [InlineData(-1L, 47L, 20L, false)]     // an empty slot evicts nothing
    [InlineData(18L, 18L, 20L, false)]     // the same chunk again is a rewrite, not an eviction
    [InlineData(19L, 47L, 20L, true)]      // just behind the cursor
    [InlineData(18L, 46L, 20L, true)]
    [InlineData(16L, 46L, 20L, true)]      // the oldest of the KeepBehindSlots (4) slots kept behind
    [InlineData(15L, 46L, 20L, false)]     // one older: nobody is keeping it
    [InlineData(20L, 46L, 20L, false)]     // the cursor's own chunk is the live window, not "behind"
    [InlineData(30L, 46L, 20L, false)]     // ahead of the cursor
    public void A_chunk_may_not_evict_one_of_the_slots_kept_just_behind_the_cursor(long resident, long incoming, long cursor, bool expected)
    {
        Assert.Equal(4, Audio.Ring.KeepBehindSlots);
        Assert.Equal(expected, Audio.Ring.WouldEvictBehind(resident, incoming, cursor));
    }

    [Theory]
    [InlineData(1_000L, 1_000L, true)]     // the last sign of life is at or before the moment the read began waiting: nothing since
    [InlineData(999L, 1_000L, true)]
    [InlineData(0L, 0L, true)]
    [InlineData(1_001L, 1_000L, false)]    // a byte (or a mirror attempt) arrived AFTER the wait began: the range is alive
    [InlineData(5_000L, 1_000L, false)]
    public void A_starving_read_may_cancel_a_range_only_when_it_showed_no_life_since_the_wait_began(long lastProgressAt, long waitStartedAt, bool expected)
        => Assert.Equal(expected, Audio.Fetcher.StarveMayCancel(lastProgressAt, waitStartedAt));

    [Theory]
    [InlineData(0, 1, 0)]                  // a single mirror has nowhere to go
    [InlineData(3, 1, 0)]
    [InlineData(0, 0, 0)]                  // (no mirrors at all: the same answer, never a modulo by zero)
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]                  // wraps
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, 0)]
    public void The_next_range_starts_on_the_mirror_after_the_one_that_went_silent_wrapping(int silent, int count, int expected)
        => Assert.Equal(expected, Audio.Body.MirrorAfterIdle(silent, count));

    // ── S-10 / S-11 live ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_second_probe_of_the_same_seek_inside_the_range_already_on_the_wire_cancels_nothing_and_asks_for_nothing()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int cancelled = fetcher.Cancelled, opens = cdn.Count;

        cdn.Hold();
        const long far = 2_000_000;
        (long Start, long End) probe = ProbeRange(far, BigBytes);
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        WaitUntil(() => cdn.Count == opens + 1 && cdn.Live == 1, "the probe to be on the wire, held");
        Assert.Equal(probe, cdn.Ranges[opens]);

        body.Retarget(far + 10_000, Audio.Ring.ProbeWindow, 1);          // the same seek's next window, inside the probe's aligned span
        Assert.Equal(cancelled, fetcher.Cancelled);                       // nothing was cancelled …
        Assert.Equal(1, fetcher.Outstanding);                             // … and nothing was queued: the probe on the wire still answers it
        Assert.Equal(1, cdn.Count - opens);

        body.Retarget(far + 20_000, Audio.Ring.ProbeWindow, 2);          // a NEWER seek, the very same bytes: it supersedes, as it always did
        WaitUntil(() => cdn.CancelsObserved == 1, "the superseded probe to observe its token");
        WaitUntil(() => cdn.Count == opens + 2, "the newer seek's probe to reach the wire");
        Assert.Equal(cancelled + 1, fetcher.Cancelled);
        Assert.Equal(probe, cdn.Ranges[opens + 1]);

        cdn.Release();
        WaitIdle(fetcher);
        Assert.Equal(Expected(plain, far + 20_000, 16_000), Read(body, far + 20_000, 16_000));
    }

    [Fact]
    public void The_slots_behind_a_cold_landing_are_one_range_planned_last_and_a_stale_landing_cannot_evict_them()
    {
        // A METERED body: its 10 s ring is 12 slots (8 ahead + 4 behind) and never grows, so chunks 12 apart share a slot and a stale
        // range landing a chunk "one lap earlier" would, before S-11, overwrite the slot kept just behind the cursor.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, metered: true);
        Assert.Equal(12, body.Ring.Slots);
        body.Start();
        WaitIdle(fetcher);
        int before = fetcher.Requests, opens = cdn.Count;

        const long far = 2_600_000;
        (long Start, long End) probe = ProbeRange(far, BigBytes);
        long landingChunk = probe.Start / Slot;
        long behindChunk = landingChunk - 1;                              // the slot just behind the landing
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, far, 16_000), Read(body, far, 16_000));
        WaitIdle(fetcher);
        Assert.Equal(1, fetcher.Requests - before);                       // the probe alone: the fill AND the slots behind wait for the landing
        Assert.False(body.IsResident(behindChunk * Slot - Skip));

        body.ResumeFrom(far);
        WaitIdle(fetcher);
        var ranges = cdn.Ranges[opens..];
        Assert.Equal(3, ranges.Length);                                   // the probe, the fill, then — LAST — the slots behind
        Assert.Equal(probe, ranges[0]);
        Assert.Equal(probe.End, ranges[1].Start);
        Assert.Equal(((landingChunk - Audio.Ring.KeepBehindSlots / 2) * Slot, landingChunk * Slot), ranges[2]);
        Assert.True(body.IsResident(behindChunk * Slot - Skip), "the slot just behind the landing is resident");

        // A stale range (an earlier seek's, past the fetcher's epoch check — a probe never is stale) lands one chunk that maps onto that
        // very slot, one lap earlier. The direct-mapped ring would overwrite it; the S-11 rule refuses.
        long staleChunk = behindChunk - body.Ring.Slots;
        Assert.True(staleChunk >= 0);
        int count = cdn.Count;
        fetcher.Enqueue(body, new Audio.RangeRequest(staleChunk * Slot, (staleChunk + 1) * Slot, body.Epoch, Probe: true));
        WaitIdle(fetcher);
        Assert.Equal(count + 1, cdn.Count);                               // it did go on the wire (that chunk was not resident) …
        Assert.True(body.IsResident(behindChunk * Slot - Skip), "a stale landing evicted a slot kept behind the cursor");   // … and landed nothing over it
        Assert.False(body.IsResident(staleChunk * Slot - Skip));
        int served = cdn.Count;
        Assert.Equal(Expected(plain, behindChunk * Slot - Skip, 2_000), Read(body, behindChunk * Slot - Skip, 2_000));
        WaitIdle(fetcher);
        Assert.Equal(served, cdn.Count);                                  // a step back after the seek costs nothing
    }

    // ── the second reader: Body.ProbeAt / ProbeRange (V-PA20) ───────────────────────────────────────────────────────

    [Fact]
    public void ProbeAt_serves_resident_bytes_and_moves_nothing_of_the_playing_reads()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        uint epoch = body.Epoch;
        long cursor = body.Ring.Cursor, want = body.Ring.Want;
        int waits = body.Ring.Waits, starves = body.Ring.Starves, requests = fetcher.Requests;

        var dst = new byte[10_000];
        Assert.Equal(dst.Length, body.ProbeAt(5_000, dst, waitMs: 1_000, residentOnly: false));
        Assert.Equal(Expected(plain, 5_000, dst.Length), dst);

        Assert.Equal(epoch, body.Epoch);                                  // not the epoch,
        Assert.Equal(cursor, body.Ring.Cursor);                           // not the cursor,
        Assert.Equal(want, body.Ring.Want);
        Assert.Equal(waits, body.Ring.Waits);                             // not the wait/starve counters,
        Assert.Equal(starves, body.Ring.Starves);
        Assert.Equal(0L, body.StallMs);                                   // not the stall clock,
        Assert.Equal(-1, body.FirstServedMs);                             // not the first-served stamp (only the playing read's first byte is)
        Assert.Equal(requests, fetcher.Requests);                         // and it asked for nothing
    }

    [Fact]
    public void A_resident_only_probe_that_misses_answers_zero_at_once_and_requests_nothing()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);
        int count = cdn.Count, requests = fetcher.Requests;

        var clock = Stopwatch.StartNew();
        Assert.Equal(0, body.ProbeAt(2_600_000, new byte[4_096], waitMs: Audio.Ring.DefaultWaitMs, residentOnly: true));
        Assert.True(clock.ElapsedMilliseconds < 1_000, $"a resident-only miss waited {clock.ElapsedMilliseconds} ms");
        WaitIdle(fetcher);

        Assert.Equal(count, cdn.Count);
        Assert.Equal(requests, fetcher.Requests);
        Assert.False(body.IsResident(2_600_000));                         // nothing was queued either: the view did not ask
    }

    [Fact]
    public void A_probe_miss_is_one_view_only_range_queued_behind_the_live_fill_nothing_is_cancelled_and_the_cursor_stays()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "the live fill's first range to be held open");
        uint epoch = body.Epoch;
        long cursor = body.Ring.Cursor;

        const long far = 2_600_000;
        var dst = new byte[4_096];
        Assert.Equal(Audio.Body.Starved, body.ProbeAt(far, dst, waitMs: 150, residentOnly: false));   // the wait ran out: not EOF, not gone

        Assert.Equal(0, fetcher.Cancelled);                               // the fill in flight was NOT cancelled for the view
        Assert.Equal(1, cdn.Count);                                       // and the view's range is still QUEUED behind it
        Assert.Equal(epoch, body.Epoch);
        Assert.Equal(cursor, body.Ring.Cursor);
        Assert.Equal(0, body.Ring.Waits);
        Assert.Equal(0, body.Ring.Starves);                               // a starved VIEW is not a starved listener
        Assert.Equal(0L, body.StallMs);

        cdn.Release();
        WaitIdle(fetcher);
        (long Start, long End) view = ProbeRange(far, BigBytes);
        var ranges = cdn.Ranges;
        Assert.True(Array.IndexOf(ranges, view) > 0, $"the view's range {view} was served ahead of the live fill: {string.Join(", ", ranges)}");
        Assert.Equal(1, ranges.Count(r => r == view));                    // asked for ONCE, however long the reader waited
        Assert.Equal(0, fetcher.Cancelled);
        Assert.Equal(dst.Length, body.ProbeAt(far, dst, waitMs: 5_000, residentOnly: false));         // the same bytes, now resident
        Assert.Equal(Expected(plain, far, dst.Length), dst);
        Assert.Equal(epoch, body.Epoch);
        Assert.Equal(cursor, body.Ring.Cursor);                           // the view landed far ahead of the cursor without moving it
    }

    [Fact]
    public void ProbeRange_queues_one_view_range_without_waiting_and_a_view_range_survives_the_owners_seek()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "the live fill's first range to be held open");

        const long far = 2_600_000;
        Assert.False(body.ProbeRange(0, 4_096));                          // the live fill's own pending range covers it: wait for that
        Assert.True(body.ProbeRange(far, 4_096));                         // queued — and the call returned at once
        Assert.False(body.ProbeRange(far + 200_000, 4_096));              // ONE view range at a time: the queue never fills with a reader's retries

        // The OWNER seeks: its in-flight fill is cancelled and its queue drained — but the view's range is side work it does not own.
        const long owner = 1_000_000;
        body.Retarget(owner, Audio.Ring.ProbeWindow, 1);
        cdn.Release();
        WaitIdle(fetcher);

        (long Start, long End) view = ProbeRange(far, BigBytes), ownerProbe = ProbeRange(owner, BigBytes);
        var ranges = cdn.Ranges;
        int viewAt = Array.IndexOf(ranges, view), probeAt = Array.IndexOf(ranges, ownerProbe);
        Assert.True(viewAt >= 0, $"the view's range was dropped by the owner's seek: {string.Join(", ", ranges)}");
        Assert.True(probeAt >= 0 && probeAt < viewAt, "the owner's probe goes first, the surviving view range after it");
        Assert.Equal(1, fetcher.Cancelled);                               // only the live fill's range
        Assert.True(body.IsResident(far));                                // and the view's bytes landed
    }

    [Fact]
    public void A_probe_inside_the_clear_head_is_served_from_it_and_never_asks_the_wire()
    {
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, head: plain[..Audio.HeadMaxBytes]);
        cdn.Hold();
        body.Start();
        WaitUntil(() => cdn.Live == 1, "the first range to be held open");

        var dst = new byte[16_000];
        Assert.Equal(dst.Length, body.ProbeAt(1_000, dst, waitMs: 1_000, residentOnly: true));
        Assert.Equal(Expected(plain, 1_000, dst.Length), dst);
        Assert.False(body.ProbeRange(1_000, 16_000));                     // the head's span is never a request
        Assert.Equal(1, cdn.Count);
        Assert.Equal(0, body.Ring.Waits);
        cdn.Release();
        WaitIdle(fetcher);
    }

    [Fact]
    public void A_probe_at_the_end_is_zero_and_a_probe_of_a_disposed_body_is_minus_one()
    {
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);

        Assert.Equal(0, body.ProbeAt(body.Length, new byte[16], waitMs: 100, residentOnly: false));    // a true EOF: the length is real
        Assert.Equal(0, body.ProbeAt(-1, new byte[16], waitMs: 100, residentOnly: false));
        Assert.Equal(0, body.ProbeAt(10, Span<byte>.Empty, waitMs: 100, residentOnly: false));

        body.Dispose();
        Assert.Equal(-1, body.ProbeAt(2_600_000, new byte[16], waitMs: 100, residentOnly: false));     // gone: not a starve, not the end
        Assert.False(body.ProbeRange(2_600_000, 16));
    }

    // ── the landing-time page index (V-PA36) ────────────────────────────────────────────────────────────────────────

    const int PagesPerChunk = 8;
    const int PageBytes = 27 + 1 + 100;                // the header, one lacing value, a 100-byte body

    static void ScrubOggS(byte[] buffer)
    {
        for (int i = 0; i + 4 <= buffer.Length; i++)
            if (buffer[i] == (byte)'O' && buffer[i + 1] == (byte)'g' && buffer[i + 2] == (byte)'g' && buffer[i + 3] == (byte)'S')
                buffer[i] = 0;
    }

    /// <summary>One VALID page at <paramref name="at"/> (real header, one segment of 100 bytes, a real CRC): the body is whatever random
    /// bytes were already there, so it never contains a second <c>OggS</c>.</summary>
    static void WriteValidPage(byte[] buffer, int at, long granule, uint sequence)
    {
        "OggS"u8.CopyTo(buffer.AsSpan(at));
        buffer[at + 4] = 0;                                                    // version
        buffer[at + 5] = 0;                                                    // header flags
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 14, 4), 7u);          // serial
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 18, 4), sequence);
        buffer.AsSpan(at + 22, 4).Clear();                                     // the CRC field is zero while the checksum is computed
        buffer[at + 26] = 1;                                                   // one segment …
        buffer[at + 27] = 100;                                                 // … of 100 bytes
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 22, 4), Ogg.Crc32(buffer.AsSpan(at, PageBytes), Ogg.CrcFieldOffset));
    }

    /// <summary>A file of <see cref="BigBytes"/> whose chunks each hold <see cref="PagesPerChunk"/> valid pages wholly inside them, plus the
    /// pages the index must NOT list: Spotify's lead-in page (before the container's first byte), a page straddling the edge between
    /// chunks 4 and 5, and a page with no granule. <c>Pages</c> are the ones it must: (chunk, CONTAINER offset, granule).</summary>
    static readonly Lazy<(byte[] Plain, byte[] Cipher, (int Chunk, long Offset, long Granule)[] Pages)> Paged = new(() =>
    {
        var plain = new byte[BigBytes];
        new Random(20260914).NextBytes(plain);
        ScrubOggS(plain);
        var pages = new List<(int Chunk, long Offset, long Granule)>();
        WriteValidPage(plain, 0, granule: 0, sequence: 0);                    // the lead-in page: container offset −167, never listed
        WriteValidPage(plain, Skip, granule: 5, sequence: 1);                 // the container's first page: container offset 0
        pages.Add((0, 0, 5));
        for (int chunk = 0; chunk < BigBytes / Slot; chunk++)
            for (int k = 0; k < PagesPerChunk; k++)
            {
                int at = chunk * Slot + 1_000 + k * 2_000;
                long granule = (long)chunk * 100_000 + k * 1_000 + 1;
                WriteValidPage(plain, at, granule, (uint)(2 + chunk * PagesPerChunk + k));
                pages.Add((chunk, at - Skip, granule));
            }
        WriteValidPage(plain, 5 * Slot - 50, granule: 777, sequence: 9_000);            // straddles a chunk edge: never listed
        WriteValidPage(plain, 7 * Slot + 40_000, granule: -1, sequence: 9_001);         // a page with no granule: never listed
        return (plain, Audio.Ctr.Decrypt(plain, Key, 0), [.. pages]);
    });

    static int ChunkOfContainer(long containerOffset) => (int)((containerOffset + Skip) / Slot);

    [Fact]
    public void The_page_index_lists_the_whole_pages_of_every_chunk_that_landed_as_container_offsets_with_their_granules()
    {
        var (_, cipher, pages) = Paged.Value;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        Assert.Equal(0, body.PageIndexCount);
        body.PageIndexSnapshot(out ReadOnlySpan<long> none, out ReadOnlySpan<long> noGranules);
        Assert.True(none.IsEmpty && noGranules.IsEmpty);

        body.Start();
        WaitIdle(fetcher);

        // What landed: the window from 0 (the opening fill) and the tail range's chunk — which is NOT in the ring, yet it is indexed.
        int window = body.Ring.Slots - Audio.Ring.KeepBehindSlots;
        var landed = new HashSet<int>(Enumerable.Range(0, window)) { BigBytes / Slot - 1 };
        var expected = new Dictionary<long, long>();
        foreach ((int chunk, long offset, long granule) in pages)
            if (landed.Contains(chunk)) expected[offset] = granule;

        body.PageIndexSnapshot(out ReadOnlySpan<long> offsetsSpan, out ReadOnlySpan<long> granulesSpan);
        long[] offsets = offsetsSpan.ToArray(), granules = granulesSpan.ToArray();
        Assert.Equal(offsets.Length, granules.Length);                    // the two views are always the same length
        Assert.Equal(body.PageIndexCount, offsets.Length);
        Assert.Equal(expected.Count, offsets.Length);                     // every whole page, and nothing else
        for (int i = 0; i < offsets.Length; i++)
        {
            Assert.True(expected.TryGetValue(offsets[i], out long granule), $"entry {i}: offset {offsets[i]} is not a listed page");
            Assert.Equal(granule, granules[i]);
        }
        Assert.Equal(offsets.Length, offsets.Distinct().Count());         // a chunk is scanned once

        long straddler = 5L * Slot - 50 - Skip, noGranule = 7L * Slot + 40_000 - Skip;
        Assert.DoesNotContain(straddler, offsets);                        // not wholly inside one chunk: the index is a bracket, never a promise
        Assert.DoesNotContain(noGranule, offsets);                        // no granule, nothing to bracket with
        Assert.DoesNotContain(-(long)Skip, offsets);                      // Spotify's own lead-in page precedes the container
        Assert.Contains(0L, offsets);                                     // the container's first page does not

        // Within a chunk the pages come in file order; across chunks in LANDING order — and the tail's chunk landed between two window chunks.
        int lastChunk = -1, inversions = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int chunk = ChunkOfContainer(offsets[i]);
            if (chunk == lastChunk) Assert.True(offsets[i] > offsets[i - 1], $"entry {i} is out of file order inside chunk {chunk}");
            else if (chunk < lastChunk) inversions++;
            lastChunk = chunk;
        }
        Assert.True(inversions >= 1, "the index came out sorted: it is supposed to list pages in the order their chunks landed");
    }

    [Fact]
    public void The_page_index_is_grow_only_never_lists_a_chunk_twice_and_a_seeks_behind_slots_come_last()
    {
        var (_, cipher, pages) = Paged.Value;
        var plain = Paged.Value.Plain;
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes);
        body.Start();
        WaitIdle(fetcher);

        body.PageIndexSnapshot(out ReadOnlySpan<long> early, out _);
        long[] earlyCopy = early.ToArray();
        Assert.InRange(earlyCopy.Length, 1, 255);                         // below the initial capacity: the landings below must GROW the arrays

        const long far = 2_600_000;
        long landingChunk = ProbeRange(far, BigBytes).Start / Slot;
        int window = body.Ring.Slots - Audio.Ring.KeepBehindSlots;
        body.Retarget(far, Audio.Ring.ProbeWindow, 1);
        Assert.Equal(Expected(plain, far, 16_000), Read(body, far, 16_000));
        body.ResumeFrom(far);
        WaitIdle(fetcher);

        // landed since: the probe's two chunks, the fill to EOF (chunk 47 a SECOND time: the tail range had it), then the slots behind
        var landed = new HashSet<int>(Enumerable.Range(0, window));
        for (long c = landingChunk - Audio.Ring.KeepBehindSlots / 2; c < BigBytes / Slot; c++) landed.Add((int)c);
        var expected = new Dictionary<long, long>();
        foreach ((int chunk, long offset, long granule) in pages)
            if (landed.Contains(chunk)) expected[offset] = granule;

        body.PageIndexSnapshot(out ReadOnlySpan<long> after, out ReadOnlySpan<long> afterGranules);
        Assert.True(after.Length > 256, $"{after.Length} entries: the growth path was never taken");
        Assert.Equal(expected.Count, after.Length);                       // chunk 47 landed twice and was indexed once
        Assert.Equal(after.Length, body.PageIndexCount);
        // APPEND-ONLY: a reader that merged the first N entries need only look at [N, count) next time — and the span it took earlier still
        // holds exactly what it held, though the arrays have since been replaced by bigger ones.
        Assert.True(early.SequenceEqual(earlyCopy));
        Assert.True(after[..earlyCopy.Length].SequenceEqual(earlyCopy));
        for (int i = 0; i < after.Length; i++)
        {
            Assert.True(expected.TryGetValue(after[i], out long granule), $"entry {i}: offset {after[i]} is not a listed page");
            Assert.Equal(granule, afterGranules[i]);
        }

        // The slots behind the landing landed LAST — after the fill ahead of it — so their (smaller) offsets are at the END of the list.
        int firstBehind = -1, lastAhead = -1;
        for (int i = 0; i < after.Length; i++)
        {
            int chunk = ChunkOfContainer(after[i]);
            if (chunk >= landingChunk - Audio.Ring.KeepBehindSlots / 2 && chunk < landingChunk) { if (firstBehind < 0) firstBehind = i; }
            else if (chunk >= landingChunk && chunk != BigBytes / Slot - 1) lastAhead = i;
        }
        Assert.True(firstBehind > lastAhead && lastAhead > 0, $"behind slots start at entry {firstBehind}, the fill's last page is entry {lastAhead}");
    }

    [Fact]
    public void A_body_that_is_not_ogg_keeps_no_page_index()
    {
        var (_, cipher, _) = Paged.Value;
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(new FakeCdn(cipher), fetcher, BigBytes, fmt: Audio.Format.Flac);   // the bytes hold valid pages; FLAC has none to index
        body.Start();
        WaitIdle(fetcher);

        Assert.Equal(0, body.PageIndexCount);
        body.PageIndexSnapshot(out ReadOnlySpan<long> offsets, out ReadOnlySpan<long> granules);
        Assert.True(offsets.IsEmpty && granules.IsEmpty);
    }

    [Fact]
    public void Chunks_that_land_from_the_disk_cache_are_indexed_too()
    {
        using var dir = new TempDir();
        SkipWhenTheVolumeIsInsideTheReserve(dir.Path);
        var (plain, cipher, pages) = Paged.Value;
        PrimeCache(dir.Path, plain, cipher);

        using var disk = new ChunkDiskCache(dir.Path);
        var cdn = new FakeCdn(cipher);
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, disk: disk);
        body.Start();
        Assert.Equal(Expected(plain, 0, BigBytes - Skip), Read(body, 0, BigBytes - Skip));
        WaitIdle(fetcher);

        Assert.Empty(cdn.Ranges);                                         // the disk answered every chunk …
        Assert.Equal(pages.Length, body.PageIndexCount);                  // … and every page of every chunk was indexed as it landed
    }

    // ── the starve rule: a trickle is never cancelled ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_range_that_keeps_trickling_is_never_cancelled_by_a_starving_read_so_its_slot_completes_across_the_starves()
    {
        // The livelock this closes: a starving read cancelled the range unconditionally, and a cancel discards the 64 KiB slot being filled
        // (only COMPLETED slots land). On a link at or below ~8 KiB/s — a slot per 8 s, the reader's whole bound — no slot could ever finish.
        // Now a range that shows ANY life (a read that returns bytes) after the reader began waiting is left alone. A STEPPED link, so the
        // trickle is a fact of the fixture: the credits below are all that crosses it. Two starving reads each see 24 KiB arrive after their
        // wait began (48 KiB, short of the slot); the third read's wait sees the last 16 KiB and the slot lands.
        var (plain, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { SteppedBelow = 2L * MaxRange };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, waitMs: 200);
        body.Start();
        WaitUntil(() => cdn.Count >= 1, "the first range to be opened");
        uint epoch = body.Epoch;
        const int Trickle = 6 * FakeCdn.StepBytes;                         // 24 KiB per starving read

        for (int round = 1; round <= 2; round++)
        {
            var dst = new byte[4_096];
            using var started = new ManualResetEventSlim();
            Task<int> read = Task.Run(() => { started.Set(); return body.ReadAt(0, dst, epoch); });
            started.Wait();
            Thread.Sleep(80);                                             // the link shows life STRICTLY after the wait began (TickCount64 is ~16 ms grained)
            cdn.Step(Trickle);
            Assert.Equal(Audio.Body.Starved, await read);   // its bound runs out with the slot still short …

            Assert.Equal(0, fetcher.Cancelled);                           // … and the range that is still sending is NOT cancelled
            Assert.Equal(0, cdn.ReadCancelsObserved);
            Assert.Equal(round, body.Ring.Starves);
            Assert.Equal((long)round * Trickle, cdn.SteppedServed);
        }

        var last = new byte[4_096];
        using var startedLast = new ManualResetEventSlim();
        Task<int> final = Task.Run(() => { startedLast.Set(); return body.ReadAt(0, last, epoch); });
        startedLast.Wait();
        Thread.Sleep(80);
        cdn.Step(Slot - 2 * Trickle);                                     // the slot's last 16 KiB: 64 KiB in all
        Assert.Equal(last.Length, await final);
        Assert.Equal(Expected(plain, 0, last.Length), last);
        Assert.Equal(0, fetcher.Cancelled);
        Assert.Equal(0, cdn.ReadCancelsObserved);
        Assert.Equal(2, body.Ring.Starves);                               // two starves, and the slot still landed: no livelock
    }

    // ── R-6: the ping is the answering mirror's ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ping_sample_is_taken_from_the_mirror_that_answered_not_from_the_range_that_failed_over_to_it()
    {
        // The first range opens on mirror A, which only says "refused" after 200 ms; mirror B answers at once. A sample taken from the
        // RANGE's start charged B with A's 200 ms, and the median-of-three ping (500, 500 → sample 1 → sample 2) sat near 200. Sampled from
        // B's own attempt it is a few ms. The link is frozen after the fourth open (the second fill range), so exactly two samples — the
        // failed-over range and the tail — have been folded in: median(500, p1, p2) = max(p1, p2).
        var (_, cipher) = Big.Value;
        var cdn = new FakeCdn(cipher) { SlowPrefix = "https://slow.", SlowOpenMs = 200, HoldFromOpen = 4 };
        using var fetcher = new Audio.Fetcher();
        using var body = NewBody(cdn, fetcher, BigBytes, mirrors: ["https://slow.a.test/audio", "https://cdn-b.test/audio"]);
        try
        {
            body.Start();
            WaitUntil(() => cdn.Count == 4 && cdn.Live == 1, "the fourth open (the second fill range) to be held");

            string[] urls = cdn.Urls;
            Assert.StartsWith("https://slow.", urls[0]);                  // the range tried the slow mirror first …
            Assert.StartsWith("https://cdn-b.", urls[1]);                 // … and fell over to the good one
            Assert.StartsWith("https://cdn-b.", urls[2]);                 // (the tail then starts there: the mirror that answers becomes the first choice)
            Assert.InRange(fetcher.PingMs, 1, 120);                       // not 200: the failed mirror's time is not B's ping
        }
        finally { cdn.Release(); }
        WaitIdle(fetcher);
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

    /// <summary>The fake wire: serves <paramref name="cipher"/>, records every open as [start, end) and its url, tracks
    /// how many opens are live, and can hold an OPEN or a READ open independently until released or cancelled — two
    /// gates, because a held read must let its `OpenAsync` return successfully first (a real reply is returned at
    /// HEADERS, before any body byte).</summary>
    sealed class FakeCdn(byte[] cipher) : Audio.IRangeSource
    {
        readonly Lock _gate = new();
        readonly List<(long Start, long End)> _ranges = [];
        readonly List<string> _urls = [];
        readonly List<(long Start, long End, string Url, long Tick)> _opens = [];
        volatile TaskCompletionSource _open = Done();
        volatile TaskCompletionSource _read = Done();
        int _live, _peak, _cancels, _readCancels;

        static TaskCompletionSource Done()
        {
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            t.SetResult();
            return t;
        }

        public bool Refuse { get; init; }
        /// <summary>Refuse every url that starts with this (an expired mirror set).</summary>
        public string? RefusePrefix { get; init; }
        /// <summary>A host that ignores `Range`: every reply is a 200 of the whole file from byte 0.</summary>
        public bool IgnoreRange { get; init; }
        /// <summary>A STEPPED link for every reply whose range starts below this offset (0 = none): each body read hands
        /// out at most <see cref="StepBytes"/>, and only against a credit <see cref="Step"/> granted — nothing crosses the
        /// link on its own, so a test decides exactly which bytes have arrived.</summary>
        public long SteppedBelow { get; init; }
        public const int StepBytes = 4 * 1024;
        /// <summary>Grant the stepped replies <paramref name="bytes"/> more (a multiple of <see cref="StepBytes"/>).</summary>
        public void Step(int bytes) => _steps.Release(bytes / StepBytes);
        /// <summary>Bytes the stepped replies have handed out so far.</summary>
        public long SteppedServed => Interlocked.Read(ref _steppedServed);
        readonly SemaphoreSlim _steps = new(0);
        long _steppedServed;
        /// <summary>Every open THROWS (a broken source, not a wire fault) until cleared.</summary>
        public bool ThrowOnOpen { get; set; }
        /// <summary>A mirror whose url starts with this hands out <see cref="FaultAfterBytes"/> then throws
        /// `IOException` mid-body — the wire fault `FetchRangeAsync` retries on the next mirror.</summary>
        public string? FaultPrefix { get; init; }
        public int FaultAfterBytes { get; init; }
        /// <summary>A mirror whose url starts with this answers "refused" (no reply) only after <see cref="SlowOpenMs"/> — a mirror that
        /// is slow to fail, so a range that falls over to the next one has spent real time on the first.</summary>
        public string? SlowPrefix { get; init; }
        public int SlowOpenMs { get; init; }
        /// <summary>Open number N (1-based, counted over every open) and every later one are HELD until <see cref="Release"/> —
        /// "freeze the link after the Nth open" without a race against the fetch task.</summary>
        public int HoldFromOpen { get; init; }

        public (long Start, long End)[] Ranges { get { lock (_gate) return [.. _ranges]; } }
        /// <summary>Every open in order, stamped with <c>Environment.TickCount64</c> — the clock the ring's backoff runs on.</summary>
        public (long Start, long End, string Url, long Tick)[] Opens { get { lock (_gate) return [.. _opens]; } }
        public string[] Urls { get { lock (_gate) return [.. _urls]; } }
        public int Count { get { lock (_gate) return _ranges.Count; } }
        public int Live => Volatile.Read(ref _live);
        public int PeakLive => Volatile.Read(ref _peak);
        public int CancelsObserved => Volatile.Read(ref _cancels);
        /// <summary>Reads cancelled while held (as opposed to an open cancelled before it ever answered).</summary>
        public int ReadCancelsObserved => Volatile.Read(ref _readCancels);

        /// <summary>Hold every subsequent `OpenAsync` until <see cref="Release"/>.</summary>
        public void Hold() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _open.TrySetResult();
        /// <summary>Hold every subsequent body `ReadAsync` until <see cref="ReleaseReads"/> — the open itself still
        /// answers at once.</summary>
        public void HoldReads() => _read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseReads() => _read.TrySetResult();

        public ValueTask<Audio.IRangeReply?> OpenAsync(string url, long start, long end, CancellationToken ct)
        {
            int live = Interlocked.Increment(ref _live);
            int count;
            lock (_gate)
            {
                _ranges.Add((start, end + 1));
                _urls.Add(url);
                _opens.Add((start, end + 1, url, Environment.TickCount64));
                if (live > _peak) _peak = live;
                count = _ranges.Count;
            }
            if (HoldFromOpen > 0 && count == HoldFromOpen) Hold();
            if (SlowPrefix is { } slow && url.StartsWith(slow, StringComparison.Ordinal)) return SlowRefuseAsync(ct);
            Task gate = _open.Task;
            if (gate.IsCompleted)
            {
                // Synchronous: a fast-path test that reasons about exact request counts and ordering sees no
                // scheduler hop between the call and the answer.
                try { return new ValueTask<Audio.IRangeReply?>(Serve(url, start, end)); }
                finally { Interlocked.Decrement(ref _live); }
            }
            return HeldAsync(gate, url, start, end, ct);
        }

        async ValueTask<Audio.IRangeReply?> SlowRefuseAsync(CancellationToken ct)
        {
            try { await Task.Delay(SlowOpenMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancels); }
            finally { Interlocked.Decrement(ref _live); }
            return null;
        }

        async ValueTask<Audio.IRangeReply?> HeldAsync(Task gate, string url, long start, long end, CancellationToken ct)
        {
            try
            {
                try { await gate.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancels); return null; }
                return Serve(url, start, end);
            }
            finally { Interlocked.Decrement(ref _live); }
        }

        Audio.IRangeReply? Serve(string url, long start, long end)
        {
            if (ThrowOnOpen) throw new NotSupportedException("fake: the source is broken");   // NOT a wire fault by
            // `FetchRangeAsync`'s filter — it reaches `ServeAsync`'s last-resort catch instead, exactly like a real bug.
            if (Refuse || start >= cipher.Length) return null;
            if (RefusePrefix is { } prefix && url.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int faultAt = FaultPrefix is { } flaky && url.StartsWith(flaky, StringComparison.Ordinal) ? FaultAfterBytes : -1;
            bool stepped = start < SteppedBelow;
            return IgnoreRange
                ? new Reply(cipher, 0, cipher.Length, this, faultAt, stepped)
                : new Reply(cipher, start, Math.Min(end + 1, cipher.Length), this, faultAt, stepped);
        }

        sealed class Reply(byte[] data, long start, long stop, FakeCdn owner, int faultAt, bool stepped) : Audio.IRangeReply
        {
            long _at = start;
            readonly long _from = start;
            int _served;

            public long TotalLength => data.Length;

            public long Start => _from;

            public ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct)
            {
                Task gate = owner._read.Task;
                if (!gate.IsCompleted || stepped) return SlowAsync(gate, dst, ct);
                return new ValueTask<int>(Copy(dst.Span));
            }

            async ValueTask<int> SlowAsync(Task gate, Memory<byte> dst, CancellationToken ct)
            {
                try
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    if (stepped) await owner._steps.WaitAsync(ct).ConfigureAwait(false);   // one StepBytes credit
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref owner._readCancels); throw; }   // as
                // the runtime's `HttpContent` stream does: a cancelled read THROWS, it does not answer 0.
                int n = Copy(dst.Span);
                if (stepped) Interlocked.Add(ref owner._steppedServed, n);
                return n;
            }

            int Copy(Span<byte> dst)
            {
                int n = (int)Math.Min(dst.Length, stop - _at);
                if (n <= 0) return 0;
                if (stepped) n = Math.Min(n, StepBytes);
                if (faultAt >= 0)
                {
                    if (_served >= faultAt) throw new IOException("fake: the mirror reset the stream");
                    n = Math.Min(n, faultAt - _served);   // the fault lands on a LATER read, not folded into this one
                }
                data.AsSpan((int)_at, n).CopyTo(dst);
                _at += n;
                _served += n;
                return n;
            }

            public void Dispose() { }
        }
    }
}
