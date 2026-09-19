// ── Wavee.Tests/VideoRulesTests.cs — the pure video rules, pinned by the bug or the budget each one exists for ─────
//
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §4.1 (the rows), §1.4.3 (the ten seek bugs S1-S10),
// §3.5 (the budgets). Every fact here is over a function that takes values and returns a value: no engine, no player,
// no socket, no loop — which is the whole reason `Playback/Playback.Video.Rules.cs` exists as a CORE partial.
//
// Naming: a test is named after the BUG it prevents (`S4_…`) or the BUDGET it holds (`Warm_switch_budget_…`), never
// after the method it calls. When one of these goes red the failure message already says what broke for the user.

using Wavee;
using Xunit;

using V = Wavee.Playback.Video;

namespace Wavee.Tests;

// ── prefetch (§3.1.5) ──────────────────────────────────────────────────────────────────────────────────────────────

public class VideoPrefetchScheduleTests
{
    static V.PrefetchInput In(bool hasVideo = true, bool videoOn = false, bool metered = false, bool isCurrent = true,
                              int msToBoundary = int.MaxValue, V.PrefetchLevel already = V.PrefetchLevel.None,
                              bool manifestFresh = false)
        => new(hasVideo, videoOn, metered, isCurrent, msToBoundary, already, manifestFresh);

    [Fact]
    public void A_track_without_a_video_is_never_prefetched()
    {
        Assert.Equal(V.PrefetchLevel.None, V.PrefetchSchedule.Decide(In(hasVideo: false, videoOn: true)));
        Assert.Equal(V.PrefetchReason.None, V.PrefetchSchedule.WhyFor(In(hasVideo: false, videoOn: true)));
    }

    [Fact]
    public void A_lit_badge_costs_one_manifest_GET_and_only_for_the_current_track()
    {
        // The badge tells the user a video EXISTS; knowing WHAT to open costs one GET and no license, no bytes.
        Assert.Equal(V.PrefetchLevel.Manifest, V.PrefetchSchedule.Decide(In(videoOn: false, isCurrent: true)));
        Assert.Equal(V.PrefetchReason.Badge, V.PrefetchSchedule.WhyFor(In(videoOn: false, isCurrent: true)));
        Assert.Equal(V.PrefetchLevel.None, V.PrefetchSchedule.Decide(In(videoOn: false, isCurrent: false, msToBoundary: 1_000)));
    }

    [Fact]
    public void Video_on_and_current_prefetches_everything_unless_the_link_is_metered()
    {
        Assert.Equal(V.PrefetchLevel.Full, V.PrefetchSchedule.Decide(In(videoOn: true, isCurrent: true)));
        Assert.Equal(V.PrefetchLevel.ManifestAndLicense, V.PrefetchSchedule.Decide(In(videoOn: true, isCurrent: true, metered: true)));
        Assert.Equal(V.PrefetchReason.Current, V.PrefetchSchedule.WhyFor(In(videoOn: true, isCurrent: true)));
    }

    [Fact]
    public void The_next_track_is_prefetched_at_twenty_seconds_and_not_before()
    {
        Assert.Equal(20_000, V.PrefetchSchedule.NextTrackWindowMs);
        Assert.Equal(V.PrefetchLevel.Full, V.PrefetchSchedule.Decide(In(videoOn: true, isCurrent: false, msToBoundary: 20_000)));
        Assert.Equal(V.PrefetchReason.Next, V.PrefetchSchedule.WhyFor(In(videoOn: true, isCurrent: false, msToBoundary: 20_000)));
        Assert.Equal(V.PrefetchLevel.None, V.PrefetchSchedule.Decide(In(videoOn: true, isCurrent: false, msToBoundary: 20_001)));
        Assert.Equal(V.PrefetchReason.None, V.PrefetchSchedule.WhyFor(In(videoOn: true, isCurrent: false, msToBoundary: 20_001)));
        Assert.Equal(V.PrefetchLevel.ManifestAndLicense,
            V.PrefetchSchedule.Decide(In(videoOn: true, isCurrent: false, msToBoundary: 5_000, metered: true)));
    }

    [Fact]
    public void Prefetch_acquires_the_license_at_manifest_time_and_never_twice()
    {
        // Reaching ManifestAndLicense is what makes the license PROACTIVE (PlayReady's own word): it is acquired when
        // the manifest is known, long before the toggle. Asking again for a level already held is the defect this pins —
        // 0.2.9 re-ran the challenge on EVERY open of the same content (§1.3).
        var cold = In(videoOn: true, isCurrent: true, metered: true);
        Assert.Equal(V.PrefetchLevel.ManifestAndLicense, V.PrefetchSchedule.Decide(cold));

        var held = In(videoOn: true, isCurrent: true, metered: true, already: V.PrefetchLevel.ManifestAndLicense, manifestFresh: true);
        Assert.Equal(V.PrefetchLevel.None, V.PrefetchSchedule.Decide(held));

        var full = In(videoOn: true, isCurrent: true, already: V.PrefetchLevel.Full, manifestFresh: true);
        Assert.Equal(V.PrefetchLevel.None, V.PrefetchSchedule.Decide(full));

        // …but a manifest whose signed CDN urls have aged out is re-asked even at Full: the bytes in hand are useless
        // if their addressing has expired.
        var stale = In(videoOn: true, isCurrent: true, already: V.PrefetchLevel.Full, manifestFresh: false);
        Assert.Equal(V.PrefetchLevel.Full, V.PrefetchSchedule.Decide(stale));
    }

    [Fact]
    public void A_manifest_is_fresh_for_ten_minutes_and_a_broken_clock_is_stale()
    {
        Assert.Equal(600_000, V.PrefetchSchedule.ManifestTtlMs);
        Assert.True(V.PrefetchSchedule.IsFresh(0));
        Assert.True(V.PrefetchSchedule.IsFresh(599_999));
        Assert.False(V.PrefetchSchedule.IsFresh(600_000));
        Assert.False(V.PrefetchSchedule.IsFresh(-1));
    }

    [Fact]
    public void Full_puts_eight_seconds_at_the_carried_position_in_the_store()
    {
        Assert.Equal(8_000, V.PrefetchSchedule.PrefetchAheadMs);
        Assert.Equal(2, V.PrefetchSchedule.SegmentsFor(4_000));    // Spotify's default stride
        Assert.Equal(1, V.PrefetchSchedule.SegmentsFor(10_000));   // a long stride still costs one segment, never zero
        Assert.Equal(3, V.PrefetchSchedule.SegmentsFor(3_000));    // the tail rounds UP
        Assert.Equal(1, V.PrefetchSchedule.SegmentsFor(0));        // an unknown stride is one segment, not a divide by zero
    }
}

// ── the seek planner (§3.2.2) ──────────────────────────────────────────────────────────────────────────────────────

public class VideoSeekPlannerTests
{
    const long SegLen = 4_000;

    static long[] EverySecond(int count)
    {
        long[] kf = new long[count];
        for (int i = 0; i < count; i++) kf[i] = i * 1_000L;
        return kf;
    }

    [Fact]
    public void S5_a_keyframe_seek_never_waits_on_a_750ms_tolerance()
    {
        // The 0.2.9 session declared a seek "landed" when |native − target| ≤ 750 ms, which an APPROXIMATE seek to a
        // keyframe a whole GOP away NEVER satisfied, so the scrubber sat in Buffering for the ack or for 6 s. The plan
        // names the exact keyframe and the exact decode distance, so "landed" is arithmetic and not a tolerance.
        long[] kf = [0, 4_000, 8_000, 12_000];
        long[] buffered = [0, 16_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 200_000, 1_000, playing: true);

        var p = V.SeekPlanner.Plan(in ix, 10_500, V.SeekIntent.Commit);

        Assert.Equal(V.SeekVerb.Instant, p.Verb);
        Assert.Equal(8_000, p.KeyframeMs);
        Assert.Equal(2, p.SegmentIndex);
        Assert.Equal(2_500, p.DecodeToTargetMs);   // exact: the decoder's work, not a guess with a 750 ms skirt
    }

    [Fact]
    public void A_far_seek_plans_one_fetch_of_the_gop_that_holds_the_target()
    {
        // S3: video and audio ride the SAME arithmetic grid, so one segment INDEX is the whole fetch plan for both
        // streams — the two serial GETs of 0.2.9 become one index fetched in parallel.
        long[] kf = [0, 4_000];
        long[] buffered = [0, 8_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 400_000, 2_000, playing: true);

        var p = V.SeekPlanner.Plan(in ix, 185_500, V.SeekIntent.Commit);

        Assert.Equal(V.SeekVerb.Fetch, p.Verb);
        Assert.Equal(46, p.SegmentIndex);              // 185 500 / 4 000
        Assert.Equal(184_000, p.KeyframeMs);           // the segment start IS a keyframe (DASH), table or no table
        Assert.Equal(1_500, p.DecodeToTargetMs);
        Assert.Equal(p.SegmentIndex, new V.SegmentGrid(SegLen).IndexOf(185_500));
    }

    [Fact]
    public void Scrubbing_rides_the_buffered_range_and_never_refetches()
    {
        // The pointer is DOWN: every preview step must answer from what is already in memory (Instant/Coarse) — zero network
        // while dragging (§3.5, "scrub preview step … 0 network"). The fetch happens once, on release.
        long[] kf = EverySecond(120);                  // 0…119 s
        long[] buffered = [30_000, 90_000];            // only the middle minute is in the store
        var ix = new V.SeekIndex(kf, buffered, SegLen, 240_000, 60_000, playing: true);

        int inside = 0, outside = 0;
        for (long t = 0; t <= 240_000; t += 137)
        {
            var p = V.SeekPlanner.Plan(in ix, t, V.SeekIntent.Preview);
            Assert.NotEqual(V.SeekVerb.Fetch, p.Verb);                 // the whole rule: a drag costs no round trip
            Assert.NotEqual(V.SeekVerb.Ride, p.Verb);                  // and it never stands still: the picture follows the pointer
            Assert.True(V.SeekPlanner.IsBuffered(buffered, p.KeyframeMs));
            if (p.Verb == V.SeekVerb.Instant) inside++; else outside++;
        }
        Assert.True(inside > 0);                                       // inside the range: reposition now
        Assert.True(outside > 0);                                      // outside it: Coarse, the closest buffered keyframe

        // Release commits, and only then may it cost a round trip.
        Assert.Equal(V.SeekVerb.Fetch, V.SeekPlanner.Plan(in ix, 200_000, V.SeekIntent.Commit).Verb);
    }

    [Fact]
    public void A_preview_with_nothing_buffered_fetches_the_segment_start_and_decodes_no_further()
    {
        long[] kf = [];
        long[] buffered = [];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 240_000, 0, playing: false);

        var p = V.SeekPlanner.Plan(in ix, 61_000, V.SeekIntent.Preview);

        Assert.Equal(V.SeekVerb.Fetch, p.Verb);
        Assert.Equal(15, p.SegmentIndex);
        Assert.Equal(60_000, p.KeyframeMs);
        Assert.Equal(0, p.DecodeToTargetMs);
    }

    [Fact]
    public void A_preview_ties_break_backwards()
    {
        // fastSeek's rule for a backward seek: the adjusted position must also be before the current one. The target
        // sits in the HOLE between two buffered islands, equidistant from a keyframe in each, so only the tie-break decides.
        long[] kf = [10_000, 20_000];
        long[] buffered = [9_000, 11_000, 19_000, 21_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 60_000, 30_000, playing: true);

        var p = V.SeekPlanner.Plan(in ix, 15_000, V.SeekIntent.Preview);
        Assert.Equal(V.SeekVerb.Coarse, p.Verb);
        Assert.Equal(10_000, p.KeyframeMs);
        Assert.Equal(2, p.SegmentIndex);
        Assert.Equal(0, p.DecodeToTargetMs);
    }

    [Fact]
    public void A_forward_nudge_inside_the_buffer_rides_instead_of_seeking()
    {
        long[] kf = [0, 4_000, 8_000];
        long[] buffered = [0, 20_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 60_000, 10_000, playing: true);

        Assert.Equal(250, V.SeekPlanner.RideAheadMs);

        var ride = V.SeekPlanner.Plan(in ix, 10_200, V.SeekIntent.Commit);
        Assert.Equal(V.SeekVerb.Ride, ride.Verb);
        Assert.Equal(10_000, ride.KeyframeMs);          // the playhead: nothing moves
        Assert.Equal(0, ride.DecodeToTargetMs);

        // Past the window it is a real reposition…
        Assert.Equal(V.SeekVerb.Instant, V.SeekPlanner.Plan(in ix, 10_251, V.SeekIntent.Commit).Verb);
        // …and a BACKWARD nudge is never a ride, however small.
        Assert.Equal(V.SeekVerb.Instant, V.SeekPlanner.Plan(in ix, 9_900, V.SeekIntent.Commit).Verb);
        // …and a preview never rides: the picture must move while the pointer does (inside the buffer it repositions now).
        var preview = V.SeekPlanner.Plan(in ix, 10_200, V.SeekIntent.Preview);
        Assert.Equal(V.SeekVerb.Instant, preview.Verb);
        Assert.Equal(8_000, preview.KeyframeMs);
    }

    [Fact]
    public void S10_a_paused_seek_repositions_and_never_rides()
    {
        // "Ride" means LET PLAYBACK REACH IT. A paused decoder reaches nothing, so riding would leave the old frame on
        // screen for ever — exactly the symptom S10 names, and the reason 0.2.9 re-asserted Play up to eight times and
        // un-paused a deliberate pause. Pausing changes the plan; it never changes the transport.
        long[] kf = [0, 4_000, 8_000];
        long[] buffered = [0, 20_000];
        var paused = new V.SeekIndex(kf, buffered, SegLen, 60_000, 10_000, playing: false);

        var p = V.SeekPlanner.Plan(in paused, 10_200, V.SeekIntent.Commit);

        Assert.Equal(V.SeekVerb.Instant, p.Verb);
        Assert.Equal(8_000, p.KeyframeMs);
        Assert.Equal(2_200, p.DecodeToTargetMs);
    }

    [Fact]
    public void S1_the_carried_position_is_planned_before_the_open_and_segment_zero_is_never_the_answer()
    {
        // A song→video switch at 1:23 used to open at 0:00 and seek afterwards, fetching 4 + 4 segments at zero first.
        // The open carries its OWN plan, over the segment that holds P.
        long[] kf = [];
        long[] buffered = [];
        var cold = new V.SeekIndex(kf, buffered, SegLen, 210_000, 0, playing: false);

        var p = V.SeekPlanner.OpenAt(in cold, 83_000);

        Assert.Equal(V.SeekVerb.Fetch, p.Verb);
        Assert.Equal(20, p.SegmentIndex);
        Assert.Equal(80_000, p.KeyframeMs);
        Assert.Equal(3_000, p.DecodeToTargetMs);
        Assert.NotEqual(0, p.SegmentIndex);

        // S9: with the prefetch already in the store the same open is Instant — there is no "seek issued too early"
        // window to drop a seek in, because the open never issues a second one.
        long[] warmKf = [80_000, 84_000];
        long[] warmBuf = [80_000, 96_000];
        var warm = new V.SeekIndex(warmKf, warmBuf, SegLen, 210_000, 0, playing: false);
        var q = V.SeekPlanner.OpenAt(in warm, 83_000);
        Assert.Equal(V.SeekVerb.Instant, q.Verb);
        Assert.Equal(80_000, q.KeyframeMs);
        Assert.Equal(3_000, q.DecodeToTargetMs);
    }

    [Fact]
    public void A_target_outside_the_presentation_is_clamped()
    {
        long[] kf = [0, 4_000];
        long[] buffered = [0, 8_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 6_000, 0, playing: true);

        Assert.Equal(0, V.SeekPlanner.Plan(in ix, -5_000, V.SeekIntent.Commit).KeyframeMs);
        Assert.Equal(V.SeekVerb.Instant, V.SeekPlanner.Plan(in ix, -5_000, V.SeekIntent.Commit).Verb);

        var past = V.SeekPlanner.Plan(in ix, 999_999, V.SeekIntent.Commit);
        Assert.Equal(4_000, past.KeyframeMs);          // clamped to 6 000, whose previous keyframe is 4 000
        Assert.Equal(2_000, past.DecodeToTargetMs);

        // An unknown duration clamps nothing away.
        var unknown = new V.SeekIndex(kf, buffered, SegLen, 0, 0, playing: true);
        Assert.Equal(250, V.SeekPlanner.Plan(in unknown, 1_000_000, V.SeekIntent.Commit).SegmentIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1_024)]
    public void The_previous_keyframe_search_answers_at_every_table_size(int count)
    {
        long[] kf = EverySecond(count);

        if (count == 0)
        {
            Assert.Equal(-1, V.SeekPlanner.PreviousKeyframe(kf, 0));
            Assert.Equal(-1, V.SeekPlanner.PreviousKeyframe(kf, 999_999));
            return;
        }

        Assert.Equal(0, V.SeekPlanner.PreviousKeyframe(kf, 0));                     // exact hit on the first entry
        Assert.Equal(-1, V.SeekPlanner.PreviousKeyframe(kf, -1));                   // nothing ≤ a negative target
        long last = (count - 1) * 1_000L;
        Assert.Equal(last, V.SeekPlanner.PreviousKeyframe(kf, last));               // exact hit on the last
        Assert.Equal(last, V.SeekPlanner.PreviousKeyframe(kf, last + 999_999));     // past the end holds the last
        if (count >= 2)
        {
            Assert.Equal(0, V.SeekPlanner.PreviousKeyframe(kf, 999));               // ≤, never the next one up
            Assert.Equal(1_000, V.SeekPlanner.PreviousKeyframe(kf, 1_000));
        }
    }

    [Fact]
    public void A_buffered_range_starts_inclusive_and_ends_exclusive()
    {
        long[] pairs = [1_000, 2_000, 5_000, 6_000];

        Assert.False(V.SeekPlanner.IsBuffered(pairs, 999));
        Assert.True(V.SeekPlanner.IsBuffered(pairs, 1_000));     // start: in
        Assert.True(V.SeekPlanner.IsBuffered(pairs, 1_999));
        Assert.False(V.SeekPlanner.IsBuffered(pairs, 2_000));    // end: out — the MSE `buffered` convention
        Assert.False(V.SeekPlanner.IsBuffered(pairs, 3_000));    // the hole between two ranges
        Assert.True(V.SeekPlanner.IsBuffered(pairs, 5_500));
        Assert.False(V.SeekPlanner.IsBuffered(pairs, 6_000));
        Assert.False(V.SeekPlanner.IsBuffered([], 0));
        Assert.False(V.SeekPlanner.IsBuffered([1_000], 1_000));  // a dangling start is not a range
    }

    [Fact]
    public void Planning_a_warm_seek_allocates_nothing()
    {
        // P8: the planner runs on every scrub step (100 ms while the pointer is down). `SeekIndex` is a ref struct over
        // the host's two static buffers and `SeekPlan` is a 32-byte record struct, so a whole drag costs zero bytes.
        // The preview half gets its own index with only an island buffered, so every step walks the full closest-keyframe
        // scan over a 1 024-entry table — the most expensive path the planner has.
        long[] kf = EverySecond(1_024);
        long[] buffered = [0, 1_024_000];
        long[] island = [0, 50_000];
        var ix = new V.SeekIndex(kf, buffered, SegLen, 1_024_000, 500_000, playing: true);
        var scrub = new V.SeekIndex(kf, island, SegLen, 1_024_000, 500_000, playing: true);

        for (int i = 0; i < 8; i++)                   // warm the JIT
        {
            V.SeekPlanner.Plan(in ix, 100_000 + i, V.SeekIntent.Commit);
            V.SeekPlanner.Plan(in scrub, 100_000 + i, V.SeekIntent.Preview);
        }

        int instant = 0, coarse = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            if (V.SeekPlanner.Plan(in ix, 100_000 + i, V.SeekIntent.Commit).Verb == V.SeekVerb.Instant) instant++;
            if (V.SeekPlanner.Plan(in scrub, 100_000 + i, V.SeekIntent.Preview).Verb == V.SeekVerb.Coarse) coarse++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1_000, instant);
        Assert.Equal(1_000, coarse);
        Assert.Equal(0L, allocated);
    }
}

// ── the segment grid (§4.1) ────────────────────────────────────────────────────────────────────────────────────────

public class VideoSegmentGridTests
{
    [Fact]
    public void Segment_zero_holds_the_first_millisecond()
    {
        var g = new V.SegmentGrid(4_000);
        Assert.Equal(0, g.IndexOf(0));
        Assert.Equal(0, g.IndexOf(1));
        Assert.Equal(0, g.IndexOf(3_999));
        Assert.Equal(1, g.IndexOf(4_000));
        Assert.Equal(0, g.StartOf(0));
        Assert.Equal(4_000, g.StartOf(1));
    }

    [Theory]
    [InlineData(4_000L, 200_000L, 50)]
    [InlineData(4_000L, 200_001L, 51)]    // the last PARTIAL segment still counts
    [InlineData(10_000L, 200_000L, 20)]
    [InlineData(10_000L, 195_000L, 20)]
    [InlineData(4_000L, 0L, 0)]
    public void The_grid_covers_the_whole_presentation_including_its_tail(long segLen, long durationMs, int expected)
        => Assert.Equal(expected, new V.SegmentGrid(segLen).Count(durationMs));

    [Fact]
    public void An_unknown_stride_degrades_to_one_segment_and_never_divides_by_zero()
    {
        var g = new V.SegmentGrid(0);
        Assert.Equal(0, g.IndexOf(99_000));
        Assert.Equal(0, g.StartOf(7));
        Assert.Equal(0, g.Count(200_000));
    }
}

// ── the audio hand-off (§3.1.4) ────────────────────────────────────────────────────────────────────────────────────

public class VideoAudioHandoffTests
{
    [Fact]
    public void The_song_fades_for_eighty_milliseconds_and_the_soundtrack_starts_where_the_fade_ends()
    {
        Assert.Equal(80, V.AudioHandoff.FadeMs);

        // Pc = the audio clock NOW + the fade. The video is positioned to the AUDIO clock, never the other way round,
        // which is why the seek bar does not jump across the switch.
        long pc = V.AudioHandoff.CutAtMs(83_000, 48_000);
        Assert.Equal(83_080, pc);
        Assert.True(pc > 83_000);
    }

    [Fact]
    public void The_cut_lands_on_a_whole_sample_frame_on_both_sides()
    {
        // 44 100 Hz: one frame is ~0.0227 ms, so a millisecond position is almost never a frame boundary. Snapping DOWN
        // means both graphs cut on the same frame — the de-click envelope can then be a real ramp and not a step.
        Assert.Equal(0, V.AudioHandoff.SnapToSample(0, 44_100));
        Assert.Equal(1_000, V.AudioHandoff.SnapToSample(1_000, 44_100));      // a whole second is always a boundary
        long snapped = V.AudioHandoff.SnapToSample(83_081, 44_100);
        Assert.True(snapped <= 83_081);
        Assert.Equal(snapped, V.AudioHandoff.SnapToSample(snapped, 44_100));  // idempotent: a boundary stays put
        Assert.Equal(83_081, V.AudioHandoff.SnapToSample(83_081, 0));         // an unknown rate cannot snap
        Assert.Equal(0, V.AudioHandoff.SnapToSample(-5, 48_000));
    }

    [Fact]
    public void A_requested_fade_always_costs_at_least_one_frame()
    {
        Assert.Equal(3_840, V.AudioHandoff.FadeFrames(80, 48_000));
        Assert.Equal(3_528, V.AudioHandoff.FadeFrames(80, 44_100));
        Assert.Equal(1, V.AudioHandoff.FadeFrames(1, 100));       // rounds to zero frames ⇒ forced to one: no step, no click
        Assert.Equal(0, V.AudioHandoff.FadeFrames(0, 48_000));    // no fade asked for, no frames spent
        Assert.Equal(0, V.AudioHandoff.FadeFrames(80, 0));
    }

    [Fact]
    public void It_is_a_cut_not_a_gap()
    {
        // §3.5's acceptance line: the soundtrack's first audible sample within 100 ms of the fade's end is a CUT. The
        // whole switch used to be silent from the request; the gate reads `audio.cut gapMs=`.
        long pc = V.AudioHandoff.CutAtMs(83_000, 48_000);

        Assert.Equal(0, V.AudioHandoff.GapMs(pc, pc));
        Assert.True(V.AudioHandoff.IsCut(V.AudioHandoff.GapMs(pc, pc + 60)));    // one GOP of decode: inaudible as a gap
        Assert.True(V.AudioHandoff.IsCut(V.AudioHandoff.GapMs(pc, pc - 20)));    // a slight overlap is still a cut
        Assert.False(V.AudioHandoff.IsCut(V.AudioHandoff.GapMs(pc, pc + 101)));  // silence the user can hear
    }

    [Fact]
    public void The_way_back_resumes_a_parked_session_for_thirty_seconds()
    {
        Assert.Equal(30_000, V.AudioHandoff.ParkTtlMs);
        Assert.True(V.AudioHandoff.CanResumeParked(0));
        Assert.True(V.AudioHandoff.CanResumeParked(30_000));
        Assert.False(V.AudioHandoff.CanResumeParked(30_001));   // the fast-start head is gone: reload, do not resume
        Assert.False(V.AudioHandoff.CanResumeParked(-1));
    }
}

// ── the retention window (§3.2.1, §3.5) ────────────────────────────────────────────────────────────────────────────

public class VideoRetentionWindowTests
{
    [Fact]
    public void S4_a_backward_seek_of_thirty_seconds_never_refetches()
    {
        // `kRetainBehind = 300` SAMPLES is ≈ 6.4 s of AAC, which is why a ten-second scrub back went to the network.
        // The window is TIME now, and thirty seconds of it.
        Assert.Equal(30_000, V.RetentionWindow.RetainBehindMs);
        Assert.Equal(60_000, V.RetentionWindow.BufferAheadMs);

        const long pos = 120_000;
        Assert.True(V.RetentionWindow.Keep(88_000, 92_000, pos, 240_000));    // 30 s back: kept
        Assert.True(V.RetentionWindow.Keep(176_000, 180_000, pos, 240_000));  // 60 s ahead: kept
        Assert.False(V.RetentionWindow.Keep(84_000, 88_000, pos, 240_000));   // 32 s back: evicted
        Assert.False(V.RetentionWindow.Keep(180_000, 184_000, pos, 240_000)); // 60 s+ ahead: not fetched yet
    }

    [Fact]
    public void The_window_is_clamped_to_the_presentation()
    {
        Assert.Equal(0, V.RetentionWindow.WindowStart(5_000));                 // never negative
        Assert.Equal(20_000, V.RetentionWindow.WindowStart(50_000));
        Assert.Equal(110_000, V.RetentionWindow.WindowEnd(50_000, 240_000));  // the whole ahead window fits
        Assert.Equal(200_000, V.RetentionWindow.WindowEnd(190_000, 200_000));  // the tail: never past the end
        Assert.Equal(110_000, V.RetentionWindow.WindowEnd(50_000, 0));         // an unknown duration clamps nothing
    }

    [Fact]
    public void The_window_is_byte_capped_and_a_480p_rung_fits_it_whole()
    {
        Assert.Equal(32L * 1024 * 1024, V.RetentionWindow.StoreBudgetBytes);

        const int p480 = 200_000;    // ≈ 1.6 Mbps video + audio: 90 s ≈ 18 MB, inside the 32 MiB budget
        Assert.True(V.RetentionWindow.FitsBudget(p480));
        Assert.Equal(30_000, V.RetentionWindow.BehindAffordableMs(p480));
        Assert.Equal(60_000, V.RetentionWindow.AheadAffordableMs(p480));

        // A fat rung cannot buy the whole window: the BEHIND half is honoured first, because it is what makes a
        // backward seek free, and the ahead half takes what is left.
        const int fat = 4_000_000;   // 32 MiB ≈ 8.3 s at this rate
        Assert.False(V.RetentionWindow.FitsBudget(fat));
        Assert.Equal(8_388, V.RetentionWindow.BehindAffordableMs(fat));
        Assert.Equal(0, V.RetentionWindow.AheadAffordableMs(fat));

        Assert.True(V.RetentionWindow.FitsBudget(0));    // an unknown bitrate is not a reason to trim
    }

    [Fact]
    public void The_window_in_segments_rounds_up()
    {
        Assert.Equal(8, V.RetentionWindow.SegmentsBehind(4_000));
        Assert.Equal(15, V.RetentionWindow.SegmentsAhead(4_000));
        Assert.Equal(3, V.RetentionWindow.SegmentsBehind(10_000));
        Assert.Equal(6, V.RetentionWindow.SegmentsAhead(10_000));
        Assert.Equal(0, V.RetentionWindow.SegmentsBehind(0));
    }
}

// ── the position clock (S7) ────────────────────────────────────────────────────────────────────────────────────────

public class VideoPositionClockTests
{
    const long Qpc = 10_000_000;    // a plausible QPC frequency: 10 MHz

    [Fact]
    public void S7_position_between_events_is_extrapolated_from_the_sample_qpc()
    {
        // 0.2.9 read the native position on a 250 ms pump poll and relayed it on a 200 ms Wavee tick, so the seek bar
        // stair-stepped. A position EVENT carries its own timestamp; everything between is arithmetic.
        Assert.Equal(30_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 5, Qpc, 1.0, 240_000));
        Assert.Equal(30_500, V.PositionClock.At(30_000, Qpc * 5, Qpc * 5 + Qpc / 2, Qpc, 1.0, 240_000));
        Assert.Equal(31_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 6, Qpc, 1.0, 240_000));
        Assert.Equal(32_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 6, Qpc, 2.0, 240_000));   // rate is honoured
    }

    [Fact]
    public void A_stopped_or_unusable_clock_holds_the_sample()
    {
        Assert.Equal(30_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 6, Qpc, 0.0, 240_000));   // paused: rate 0
        Assert.Equal(30_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 6, 0, 1.0, 240_000));     // no frequency
        Assert.Equal(30_000, V.PositionClock.At(30_000, Qpc * 5, Qpc * 4, Qpc, 1.0, 240_000));   // a QPC that went backwards
        Assert.Equal(0, V.PositionClock.At(-5, 0, 0, Qpc, 1.0, 240_000));
        Assert.Equal(240_000, V.PositionClock.At(239_000, 0, Qpc * 10, Qpc, 1.0, 240_000));      // never past the end
    }
}

// ── the budgets (§3.5) ─────────────────────────────────────────────────────────────────────────────────────────────

public class VideoBudgetTests
{
    [Fact]
    public void Warm_switch_budget_is_three_hundred_ms()
    {
        // Christos's sentence, as a number: "switching to video is super slow, takes like 5 seconds". A warm switch is
        // a SetSource on a live engine whose license, init and first 8 s at P are already in memory.
        Assert.Equal(300, V.Budgets.WarmSwitchMs);
        Assert.Equal(450, V.Budgets.WarmSwitchAfterIdleMs);
        Assert.Equal(1_000, V.Budgets.ColdSwitchMs);
        Assert.Equal(1_500, V.Budgets.FirstVideoOfProcessMs);
    }

    [Fact]
    public void Every_budget_is_ordered_the_way_the_work_is()
    {
        Assert.True(V.Budgets.WarmSwitchMs < V.Budgets.WarmSwitchAfterIdleMs);
        Assert.True(V.Budgets.WarmSwitchAfterIdleMs < V.Budgets.ColdSwitchMs);
        Assert.True(V.Budgets.ColdSwitchMs < V.Budgets.FirstVideoOfProcessMs);
        Assert.True(V.Budgets.ScrubStepMs < V.Budgets.NearSeekMs);
        Assert.True(V.Budgets.NearSeekMs < V.Budgets.FarSeekMs);
    }

    [Fact]
    public void Seek_budgets_are_one_fifty_near_and_five_hundred_far()
    {
        Assert.Equal(150, V.Budgets.NearSeekMs);      // SeekVerb.Instant
        Assert.Equal(500, V.Budgets.FarSeekMs);       // SeekVerb.Fetch, one segment pair in parallel
        Assert.Equal(100, V.Budgets.ScrubStepMs);     // SeekVerb.Coarse, zero network
        Assert.Equal(120, V.Budgets.PausedSeekFirstFrameMs);
        Assert.Equal(120, V.Budgets.VideoToSongMs);
    }

    [Fact]
    public void A_seek_shorter_than_four_hundred_ms_shows_no_spinner()
    {
        // §3.4 rule 1: Seeking is a JOINING state (Media3's allowedJoiningTimeMs, Chromium's 250 ms first-paint timer),
        // so the previous frame stays. Both seek budgets fit under it, which is what makes the rule honest.
        Assert.Equal(400, V.Budgets.JoiningNoSpinnerMs);
        Assert.True(V.Budgets.NearSeekMs < V.Budgets.JoiningNoSpinnerMs);
        Assert.True(V.Budgets.PausedSeekFirstFrameMs < V.Budgets.JoiningNoSpinnerMs);
    }
}

// ── the always-on lines (§3.5, §4.3) ───────────────────────────────────────────────────────────────────────────────

public class VideoLogFormatTests
{
    [Fact]
    public void The_switch_timeline_formats_the_line_the_gate_reads()
    {
        char[] buf = new char[V.VideoLog.MaxLineChars];

        int n = V.VideoLog.Format(new V.VideoLog.SwitchBegin("spotify:video:abc", 83_000, V.SwitchAction.Switch, true, 7), buf);
        Assert.Equal("[video] switch.begin key=spotify:video:abc from=83000ms plan=Switch warm=true epoch=7", new string(buf, 0, n));

        n = V.VideoLog.Format(new V.VideoLog.FirstFrame("spotify:video:abc", 7, 284, 141, 83_000, 854, 480), buf);
        Assert.Equal("[video] first.frame key=spotify:video:abc epoch=7 sinceSwitchMs=284 sinceAttachMs=141 pos=83000ms natural=854x480",
            new string(buf, 0, n));

        n = V.VideoLog.Format(new V.VideoLog.AudioCut(80, 83_000, 83_080, 0), buf);
        Assert.Equal("[video] audio.cut fadeMs=80 songPos=83000ms videoPos=83080ms gapMs=0", new string(buf, 0, n));
    }

    [Fact]
    public void The_seek_timeline_formats_the_plan_and_the_landing()
    {
        char[] buf = new char[V.VideoLog.MaxLineChars];

        int n = V.VideoLog.Format(new V.VideoLog.SeekPlanned(10_500, V.SeekIntent.Commit, V.SeekVerb.Instant, 8_000, 2, 2_500), buf);
        Assert.Equal("[video] seek.plan target=10500 intent=Commit verb=Instant kf=8000 seg=2 decodeMs=2500", new string(buf, 0, n));

        n = V.VideoLog.Format(new V.VideoLog.SeekDone(10_500, 10_500, 96, false), buf);
        Assert.Equal("[video] seek.done target=10500 landed=10500 ms=96 fetched=0", new string(buf, 0, n));

        n = V.VideoLog.Format(new V.VideoLog.SeekDone(185_500, 185_500, 412, true), buf);
        Assert.Equal("[video] seek.done target=185500 landed=185500 ms=412 fetched=1", new string(buf, 0, n));

        n = V.VideoLog.Format(new V.VideoLog.PrefetchPlanned("spotify:track:xyz", V.PrefetchLevel.Full, V.PrefetchReason.Next, false), buf);
        Assert.Equal("[video] prefetch.plan track=spotify:track:xyz level=Full why=next metered=false", new string(buf, 0, n));
    }

    [Fact]
    public void A_line_that_does_not_fit_is_reported_as_nothing_rather_than_truncated()
    {
        // A truncated line is worse than no line: the gate would parse a half-written field as a real number.
        char[] tiny = new char[8];
        Assert.Equal(0, V.VideoLog.Format(new V.VideoLog.SeekDone(10_500, 10_500, 96, false), tiny));
    }

    [Fact]
    public void Every_enum_prints_its_own_name_without_boxing()
    {
        Assert.Equal("None", V.VideoLog.Name(V.SwitchAction.None));
        Assert.Equal("SeekOnly", V.VideoLog.Name(V.SwitchAction.SeekOnly));
        Assert.Equal("Rebuild", V.VideoLog.Name(V.SwitchAction.Rebuild));
        Assert.Equal("Preview", V.VideoLog.Name(V.SeekIntent.Preview));
        Assert.Equal("Ride", V.VideoLog.Name(V.SeekVerb.Ride));
        Assert.Equal("Coarse", V.VideoLog.Name(V.SeekVerb.Coarse));
        Assert.Equal("ManifestAndLicense", V.VideoLog.Name(V.PrefetchLevel.ManifestAndLicense));
        Assert.Equal("badge", V.VideoLog.Name(V.PrefetchReason.Badge));
        Assert.Equal("none", V.VideoLog.Name(V.PrefetchReason.None));
        Assert.Equal("Presenting", V.VideoLog.Name(V.SwitchPhase.Presenting));
        Assert.Equal("Idle", V.VideoLog.Name(V.SwitchPhase.Idle));
    }

    [Fact]
    public void Formatting_a_line_allocates_nothing_of_its_own()
    {
        char[] buf = new char[V.VideoLog.MaxLineChars];
        var l = new V.VideoLog.SeekPlanned(10_500, V.SeekIntent.Commit, V.SeekVerb.Instant, 8_000, 2, 2_500);
        for (int i = 0; i < 8; i++) V.VideoLog.Format(in l, buf);    // warm the JIT

        int total = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++) total += V.VideoLog.Format(in l, buf);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(total > 0);
        Assert.Equal(0L, allocated);
    }
}
