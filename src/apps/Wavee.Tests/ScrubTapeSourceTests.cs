// ── Wavee.Tests/ScrubTapeSourceTests.cs — the scrub voice, over a synthetic decoder (playback smoothness WP-3c, §4.12) ───
//
// `Playback/Playback.Audio.Scrub.cs` holds the tape source the scrub voice plays: a second decoder feeding a 3 s PCM cache around the
// head, played at a varying speed that follows the pointer — forward, backward, faster, slower — like tape under a hand. Everything it
// decides is pinned here with no device, no stream layer and no wall clock: the decoder is a tone generator, the byte view is a flag
// holder, and the clock is a hand-advanced `TimeProvider`.
//
//   THE HEAD. A steady drag is followed (forward and backward); a resting pointer winds the tape down to silence and parks it; a far
//   pointer is a jump (relocate, never a fast-forward through everything between).
//   THE CACHE. One recentre per excursion, sought at `from + lead-in`; travel inside the cache decodes nothing more.
//   NEVER BLOCK. The byte view is resident-only for every decoder call; a miss, a short decode or a throwing decoder is silence and a
//   parked source that tries again only when the pointer moves, never a fault.
//   LIFETIME. Dispose releases the decoder, the view and the lease once; a cancelled read closes the view only.
//   ADMISSION. The pure rule for "scrub voice or visual only".

using FluentGpu.Media;
using Xunit;
using ScrubAdmission = Wavee.Playback.Audio.ScrubAdmission;
using ScrubTapeSource = Wavee.Playback.Audio.ScrubTapeSource;
using IScrubBytes = Wavee.Playback.Audio.IScrubBytes;
using ScrubRefusal = Wavee.Playback.Audio.ScrubRefusal;

namespace Wavee.Tests;

public sealed class ScrubTapeSourceTests
{
    const int Rate = 48_000, Ch = 2;
    static readonly int Block = Rate * ScrubTapeSource.BlockMs / 1000;            // 480
    static readonly int CacheFrames = Rate * ScrubTapeSource.CacheMs / 1000;      // 144 000
    const long Pointer = 480_000;                                                  // 10 s in: far from both ends

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FakeBytes : IScrubBytes
    {
        public bool ResidentOnly { get; set; }
        public int Closes;
        public void Close() => Closes++;
    }

    sealed class FakeLease : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    /// <summary>A clock the test moves by hand: one tick = one millisecond.</summary>
    sealed class FakeTime : TimeProvider
    {
        long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Volatile.Read(ref _ms);
        public void Advance(long ms) => Interlocked.Add(ref _ms, ms);
    }

    /// <summary>A decoder whose sample at decoder frame <c>d</c> is a 440 Hz tone at 0.5 on every channel. Reads come in ≤ 2048-frame
    /// packets, like a real codec. It records every seek and whether the byte view was resident-only at each call; <see cref="Resident"/>
    /// false makes a read answer 0 (the resident-only miss), and <see cref="Frames"/> is where the track ends in decoder frames.</summary>
    sealed class ToneDecoder(FakeBytes gate, int leadIn) : IAudioDecoder, IDisposable
    {
        public bool Resident = true, Throws;
        public long Frames = long.MaxValue, Decoded;
        public readonly List<long> Seeks = [];
        public int GateViolations, Disposed;
        long _pos;

        public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info) { info = default; return true; }

        public GaplessInfo Gapless => new(leadIn, 0, -1, false);

        public long Seek(long frame)
        {
            if (!gate.ResidentOnly) GateViolations++;
            if (Throws) throw new InvalidOperationException("seek");
            Seeks.Add(frame);
            if (frame < 0) return -1;
            _pos = frame;
            return frame;
        }

        public int Read(Span<float> dst)
        {
            if (!gate.ResidentOnly) GateViolations++;
            if (Throws) throw new InvalidOperationException("read");
            if (!Resident) return 0;
            int frames = (int)Math.Clamp(Frames - _pos, 0, Math.Min(dst.Length / Ch, 2048));
            for (int f = 0; f < frames; f++)
            {
                float v = 0.5f * MathF.Sin(2f * MathF.PI * 440f * ((_pos + f) % Rate) / Rate);
                for (int c = 0; c < Ch; c++) dst[f * Ch + c] = v;
            }
            _pos += frames;
            Decoded += frames;
            return frames;
        }

        public void Dispose() => Disposed++;
    }

    sealed class Rig
    {
        public readonly FakeBytes Bytes = new();
        public readonly FakeLease Lease = new();
        public readonly FakeTime Time = new();
        public readonly ToneDecoder Dec;
        public readonly ScrubTapeSource Src;
        readonly float[] _buf = new float[Block * Ch];

        public Rig(long start, int leadIn = 0)
        {
            Dec = new ToneDecoder(Bytes, leadIn);
            Src = new ScrubTapeSource(Dec, Bytes, Lease, Rate, Ch, start, Time);
        }

        /// <summary>One block (10 ms of wall time passes with it); returns its peak.</summary>
        public float ReadBlock()
        {
            Assert.Equal(Block, Src.Read(_buf, Ch));
            Time.Advance(ScrubTapeSource.BlockMs);
            float peak = 0;
            foreach (float v in _buf) peak = MathF.Max(peak, MathF.Abs(v));
            return peak;
        }

        /// <summary>A steady drag: the pointer moves <paramref name="velocity"/> × one block per block, retargeted every block.</summary>
        public long Drag(long from, double velocity, int blocks, out float lastPeak)
        {
            long at = from;
            lastPeak = 0;
            for (int k = 0; k < blocks; k++)
            {
                at += (long)Math.Round(velocity * Block);
                Src.Retarget(at, velocity);
                lastPeak = ReadBlock();
            }
            return at;
        }
    }

    // ── construction ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_source_sits_at_its_start_frame_is_never_exhausted_and_holds_its_view_resident_only()
    {
        var r = new Rig(start: 123_456);
        Assert.Equal(123_456, r.Src.PositionFrames);                              // V-PA29: never MinValue
        Assert.False(r.Src.Exhausted);
        Assert.False(r.Src.Parked);
        Assert.Equal(GaplessInfo.None, r.Src.Gapless);
        Assert.Equal(default(ReplayGainInfo), r.Src.Loudness);
        Assert.Equal(Block, r.Src.BlockFrames);
        Assert.True(r.Bytes.ResidentOnly);
    }

    // ── the head ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_steady_forward_drag_is_followed_closely_and_is_audible()
    {
        var r = new Rig(Pointer);
        long at = r.Drag(Pointer, 1.0, 100, out float peak);                     // one second of drag at normal speed
        // the follower trails a steady drag by velocity × FollowSec (critically damped: never ahead of the pointer)
        long lag = (long)(ScrubTapeSource.FollowSec * Rate);
        Assert.InRange(r.Src.PositionFrames, at - lag - 2L * Block, at + Block);
        Assert.True(peak > 0.2f, $"peak {peak}");                                 // the tone comes through at speed
        Assert.False(r.Src.Parked);
    }

    [Fact]
    public void A_steady_backward_drag_plays_the_tape_in_reverse()
    {
        var r = new Rig(Pointer);
        long at = r.Drag(Pointer, -1.0, 100, out float peak);
        Assert.True(at < Pointer);
        long lag = (long)(ScrubTapeSource.FollowSec * Rate);
        Assert.InRange(r.Src.PositionFrames, at - Block, at + lag + 2L * Block);
        Assert.True(peak > 0.2f, $"peak {peak}");
    }

    [Fact]
    public void A_fast_drag_is_capped_at_the_maximum_rate()
    {
        var r = new Rig(Pointer);
        long before = r.Src.PositionFrames;
        for (int k = 0; k < 6; k++)
        {
            r.Src.Retarget(Pointer + (k + 1) * 5_000L, 10.0);                     // a pointer racing ahead of any tape speed, but never a jump
            r.ReadBlock();
            Assert.True(Math.Abs(r.Src.PositionFrames - before) <= ScrubTapeSource.MaxRate * Block + 1, "the head moved faster than MaxRate");
            before = r.Src.PositionFrames;
        }
    }

    [Fact]
    public void A_resting_pointer_winds_the_tape_down_to_silence_and_parks()
    {
        var r = new Rig(Pointer);
        long at = r.Drag(Pointer, 1.0, 50, out _);
        float peak = 1f;
        for (int k = 0; k < 80; k++) peak = r.ReadBlock();                         // 800 ms with no move at all
        Assert.True(r.Src.Parked);
        Assert.Equal(0f, peak);
        Assert.InRange(r.Src.PositionFrames, at - Block, at + Block);              // stopped at the pointer, not past it

        r.Src.Retarget(at + Block, 1.0);                                           // a move wakes it
        Assert.False(r.Src.Parked);
    }

    [Fact]
    public void A_far_pointer_is_a_jump_not_a_fast_forward()
    {
        var r = new Rig(Pointer);
        r.Drag(Pointer, 1.0, 20, out _);
        long far = Pointer + 20L * Rate;                                           // 20 s ahead
        r.Src.Retarget(far, 0);
        for (int k = 0; k < 4; k++) r.ReadBlock();                                 // the fade-out block, the relocation, and settling
        Assert.InRange(r.Src.PositionFrames, far - 2L * Block, far + 2L * Block);
        Assert.Equal(2, r.Dec.Seeks.Count);                                        // one recentre at the start, one at the landing — nothing in between
        Assert.Equal(0, r.Dec.GateViolations);
    }

    // ── the cache ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1_105)]                                                            // a codec lead-in: decoder frames are content + 1105
    public void The_first_read_fills_the_cache_ahead_of_the_head_and_travel_inside_it_decodes_nothing_more(int leadIn)
    {
        var r = new Rig(Pointer, leadIn);
        r.ReadBlock();
        Assert.Single(r.Dec.Seeks);
        Assert.Equal(Pointer - CacheFrames / 4 + leadIn, r.Dec.Seeks[0]);          // a forward-facing head sits a quarter in
        Assert.Equal(CacheFrames, r.Dec.Decoded);
        r.Drag(Pointer, 1.0, 100, out _);                                          // one second forward: still inside
        Assert.Single(r.Dec.Seeks);
        Assert.Equal(0, r.Dec.GateViolations);                                     // every decoder call ran with the view resident-only
    }

    [Fact]
    public void Travel_off_the_end_of_the_cache_recentres_once_ahead_of_the_head()
    {
        var r = new Rig(Pointer);
        r.Drag(Pointer, 2.0, 120, out _);                                          // 2.4 s of tape: past the 2.25 s ahead of the head
        Assert.Equal(2, r.Dec.Seeks.Count);
        Assert.InRange(r.Dec.Seeks[1], r.Src.PositionFrames - CacheFrames / 4 - 20L * Block, r.Src.PositionFrames - CacheFrames / 4 + 20L * Block);
    }

    [Fact]
    public void Near_the_start_of_the_track_the_cache_begins_at_frame_zero()
    {
        var r = new Rig(start: 100);
        r.Drag(100, 1.0, 20, out _);
        Assert.Single(r.Dec.Seeks);
        Assert.Equal(0, r.Dec.Seeks[0]);
        Assert.False(r.Src.Parked);
    }

    [Fact]
    public void A_negative_retarget_is_clamped_to_the_start_of_the_track()
    {
        var r = new Rig(start: 2_000);
        r.ReadBlock();
        r.Src.Retarget(-5_000, 0);
        for (int k = 0; k < 30; k++) r.ReadBlock();
        Assert.InRange(r.Src.PositionFrames, 0, Block);
    }

    // ── never blocking ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_head_with_nothing_resident_parks_with_silence_and_asks_the_decoder_once_until_the_pointer_moves()
    {
        var r = new Rig(Pointer);
        r.Dec.Resident = false;
        for (int i = 0; i < 6; i++) Assert.Equal(0f, r.ReadBlock());
        Assert.True(r.Src.Parked);
        Assert.Single(r.Dec.Seeks);                                                // five parked blocks asked nothing
        Assert.Equal(6L * Block, r.Src.FramesRead);                                // silence is still a block per call

        r.Dec.Resident = true;                                                     // the ring fetched it
        r.Drag(Pointer, 1.0, 30, out float peak);
        Assert.False(r.Src.Parked);
        Assert.Equal(2, r.Dec.Seeks.Count);                                        // one retry for the move
        Assert.True(peak > 0.2f, $"peak {peak}");                                  // audible again
    }

    [Fact]
    public void A_decode_too_short_to_hold_the_head_is_a_miss()
    {
        var r = new Rig(Pointer);
        r.Dec.Frames = Pointer - CacheFrames / 4 + 200;                            // the decode ends before the head
        Assert.Equal(0f, r.ReadBlock());
        Assert.True(r.Src.Parked);
    }

    [Fact]
    public void A_decoder_that_throws_is_a_miss_never_a_fault()
    {
        var r = new Rig(Pointer);
        r.Dec.Throws = true;
        Assert.Equal(0f, r.ReadBlock());
        Assert.True(r.Src.Parked);
    }

    [Fact]
    public void A_destination_shorter_than_one_frame_is_refused_and_consumes_nothing()
    {
        var r = new Rig(Pointer);
        Assert.Equal(0, r.Src.Read(new float[Ch - 1], Ch));
        Assert.Empty(r.Dec.Seeks);
        Assert.Equal(0, r.Src.FramesRead);
        Assert.False(r.Src.Exhausted);                                             // a short destination is never the end
    }

    [Fact]
    public void A_read_hands_out_at_most_one_block_per_control_step()
    {
        var r = new Rig(Pointer);
        Assert.Equal(Block, r.Src.Read(new float[Block * 4 * Ch], Ch));
        Assert.Equal(Block / 2, r.Src.Read(new float[Block / 2 * Ch], Ch));
    }

    [Fact]
    public void Steady_reads_allocate_nothing()
    {
        var r = new Rig(Pointer);
        r.Drag(Pointer, 1.0, 10, out _);                                           // warm: the cache is filled
        var buf = new float[Block * Ch];
        long at = Pointer + 10L * Block;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 100; k++)
        {
            at += Block;
            r.Src.Retarget(at, 1.0);
            r.Src.Read(buf, Ch);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    // ── lifetime ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_releases_the_decoder_the_view_and_the_lease_exactly_once()
    {
        var r = new Rig(Pointer);
        r.ReadBlock();
        r.Src.Dispose();
        r.Src.Dispose();
        Assert.Equal(1, r.Dec.Disposed);
        Assert.Equal(1, r.Lease.Disposed);
        Assert.Equal(1, r.Bytes.Closes);
        Assert.Equal(0, r.Src.Read(new float[Block * Ch], Ch));                    // a disposed source hands out nothing
    }

    [Fact]
    public void Cancelling_the_pending_read_closes_the_view_but_keeps_the_decoder_and_the_lease()
    {
        var r = new Rig(Pointer);
        r.Src.CancelPendingRead();
        Assert.Equal(1, r.Bytes.Closes);
        Assert.Equal(0, r.Dec.Disposed);
        Assert.Equal(0, r.Lease.Disposed);
    }

    // ── admission ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, true, true, false, ScrubRefusal.NoSession)]
    [InlineData(false, true, false, false, true, ScrubRefusal.NoSession)]              // the first failing gate names the refusal
    [InlineData(true, true, true, true, true, ScrubRefusal.Silent)]
    [InlineData(true, false, false, true, false, ScrubRefusal.NoRingBytes)]            // a local file has no second reader
    [InlineData(true, false, true, false, false, ScrubRefusal.NotPlaying)]             // paused ⇒ visual only (V-PA41)
    [InlineData(true, false, true, true, true, ScrubRefusal.TransitionInFlight)]
    [InlineData(true, false, true, true, false, ScrubRefusal.None)]
    public void Admission_names_the_first_failing_gate(bool hasSession, bool silent, bool ringBytes, bool playing, bool transition, ScrubRefusal expected)
        => Assert.Equal(expected, ScrubAdmission.Check(hasSession, silent, ringBytes, playing, transition));
}
