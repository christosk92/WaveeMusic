// ── Wavee.Tests/ScrollTraceRulesTests.cs — the per-frame scroll trace verdict ──────────────────────────────────────
//
// Gate for `Screens/Diagnostics.ScrollTrace.cs`: the pure figures the always-on `scroll.trace` log line prints for every
// wheel/drag burst. The defect it exists for: "scrolling still sometimes feels bad, I cannot explain it" (2026-09-16),
// while `scroll.frames` showed every CPU frame under 9 ms and a missedVblanks figure that counts the idle gap between two
// landed wheel glides exactly like a real hitch. These facts feed synthetic bursts and pin what each figure means.
//
// No engine, no clock, no logger: samples in, a verdict record and a formatted shift string out.

using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ScrollTraceRulesTests
{
    const float Refresh = 8.333f;

    static Diagnostics.ScrollTraceSample Live(float delta, float dt = Refresh, int missed = 0, int notches = 0)
        => new(dt, delta, missed, notches, Live: true);
    static Diagnostics.ScrollTraceSample Idle(float dt = Refresh, int missed = 0)
        => new(dt, 0f, missed, 0, Live: false);

    static Diagnostics.ScrollTraceSample[] EvenGlide(int frames, float shift)
    {
        var s = new Diagnostics.ScrollTraceSample[frames];
        for (int i = 0; i < frames; i++) s[i] = Live(shift, notches: i == 0 ? 1 : 0);
        return s;
    }

    [Fact]
    public void An_even_glide_is_smooth()
    {
        var v = Diagnostics.ScrollTraceRules.Analyse(EvenGlide(24, 12f), Refresh);
        Assert.Equal("smooth", v.Grade);
        Assert.Equal(24, v.LiveFrames);
        Assert.Equal(0, v.LiveMissed);
        Assert.Equal(0, v.LiveZero);
        Assert.Equal(0, v.Dips);
        Assert.Equal(0, v.Late);
        Assert.Equal(0f, v.Jitter);
        Assert.Equal(1, v.Notches);
    }

    [Fact]
    public void A_vblank_without_a_present_during_motion_is_a_hitch()
    {
        var s = EvenGlide(24, 12f);
        s[10] = Live(12f, dt: 2 * Refresh, missed: 1);   // the frame after a held refresh
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.LiveMissed);
        Assert.Equal(1, v.Late);
        Assert.Equal("present-hitch", v.Grade);
    }

    [Fact]
    public void The_idle_gap_between_two_landed_glides_is_not_a_hitch()
    {
        // glide, land, 30 idle frames' worth of nothing presented (the engine counts those as missed), next notch.
        var s = new Diagnostics.ScrollTraceSample[20 + 1 + 20];
        for (int i = 0; i < 20; i++) s[i] = Live(10f, notches: i == 0 ? 1 : 0);
        s[20] = Idle(dt: 250f, missed: 29);
        for (int i = 0; i < 20; i++) s[21 + i] = Live(10f, notches: i == 0 ? 1 : 0);
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.LiveMissed);   // the previous frame (the landing) was live, the gap frame itself is not live…
        Assert.Equal(40, v.LiveFrames);
        Assert.Equal(2, v.Notches);
        Assert.Equal("smooth", v.Grade);
    }

    [Fact]
    public void A_miss_right_after_the_landing_frame_counts_the_landing_as_motion()
    {
        // The landing frame IS live; a vblank lost immediately after it (before the idle frame) is charged as a hitch
        // only when the following frame is itself live — an idle follower means the glide had ended.
        var s = new Diagnostics.ScrollTraceSample[12];
        for (int i = 0; i < 10; i++) s[i] = Live(10f);
        s[10] = Live(10f, dt: 2 * Refresh, missed: 1);   // motion continues after a held refresh → hitch
        s[11] = Idle();
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.LiveMissed);
    }

    [Fact]
    public void A_live_frame_that_moved_nothing_inside_motion_is_a_stall()
    {
        var s = EvenGlide(20, 12f);
        s[9] = Live(0f);
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.LiveZero);
        Assert.Equal("stalled-frames", v.Grade);
    }

    [Fact]
    public void The_landing_frame_may_move_nothing()
    {
        var s = EvenGlide(12, 12f);
        s[11] = Live(0f);   // last live frame, nothing live follows
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.LiveZero);
    }

    [Fact]
    public void A_shift_that_collapses_and_recovers_is_a_dip_but_a_landing_tail_is_not()
    {
        var dip = EvenGlide(20, 12f);
        dip[10] = Live(4f);          // 0.33× the trailing median, next frame back at 12
        var v = Diagnostics.ScrollTraceRules.Analyse(dip, Refresh);
        Assert.Equal(1, v.Dips);
        Assert.Equal("velocity-dips", v.Grade);

        var tail = new Diagnostics.ScrollTraceSample[16];
        float[] shifts = [12, 12, 12, 12, 12, 12, 12, 12, 10, 8, 6, 4, 3, 2, 1, 0.5f];
        for (int i = 0; i < tail.Length; i++) tail[i] = Live(shifts[i], notches: i == 0 ? 1 : 0);
        Assert.Equal(0, Diagnostics.ScrollTraceRules.Analyse(tail, Refresh).Dips);
    }

    [Fact]
    public void A_notch_frame_may_kick_without_counting_as_jitter_or_a_dip()
    {
        var s = EvenGlide(20, 12f);
        s[10] = Live(20f, notches: 1);   // a stacked notch kicks the velocity
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.Dips);
        Assert.Equal(2, v.Notches);
        // the ONE unequal pair the kick leaves (20 → 12 on the frame after) is a single sample in a median of 18 zeros
        Assert.True(v.Jitter < 0.01f, "jitter=" + v.Jitter);
    }

    [Fact]
    public void Uneven_shifts_read_as_uneven_and_the_notch_gap_is_the_cadence()
    {
        var s = new Diagnostics.ScrollTraceSample[24];
        for (int i = 0; i < s.Length; i++) s[i] = Live((i & 1) == 0 ? 14f : 9f, notches: i % 12 == 0 ? 1 : 0);
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal("uneven", v.Grade);
        Assert.True(v.Jitter > Diagnostics.ScrollTraceRules.JitterSmooth, "jitter=" + v.Jitter);
        Assert.Equal(2, v.Notches);
        Assert.Equal(12 * Refresh, v.NotchGapMs, 1);
    }

    [Fact]
    public void Too_few_live_frames_is_a_nudge_not_a_verdict()
    {
        var v = Diagnostics.ScrollTraceRules.Analyse(EvenGlide(5, 12f), Refresh);
        Assert.Equal("short", v.Grade);
    }

    [Fact]
    public void The_shift_string_carries_the_markers()
    {
        var s = new Diagnostics.ScrollTraceSample[]
        {
            Live(12.34f, notches: 1),
            Live(11.9f),
            Live(11f, dt: 2 * Refresh, missed: 1),
            Idle(),
            Live(-8f, notches: 1),
        };
        Assert.Equal("12.3* 11.9 11.0!~ . -8.0*", Diagnostics.ScrollTraceRules.Format(s, Refresh));
    }

    [Fact]
    public void Describe_prints_every_figure_with_invariant_formatting()
    {
        var v = Diagnostics.ScrollTraceRules.Analyse(EvenGlide(24, 12f), Refresh);
        string line = Diagnostics.ScrollTraceRules.Describe(in v);
        Assert.Contains("verdict=smooth", line);
        Assert.Contains("live=24", line);
        Assert.Contains("liveMissed=0", line);
        Assert.Contains("jitter=0.00", line);
        Assert.Contains("dtP95=8.3", line);
    }

    [Fact]
    public void Slack_frames_are_counted_and_printed()
    {
        var s = EvenGlide(20, 12f);
        s[5] = Live(12f) with { SlackMs = 14f };
        s[6] = Live(12f) with { SlackMs = 12f };   // at the threshold: not slack
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.SlackFrames);
        Assert.Contains("slackFrames=1", Diagnostics.ScrollTraceRules.Describe(in v));
    }

    // ── the ring: the frame watch hands it the counter delta it READ; the ring moves it to the frame it belongs to ──

    /// <summary>Feed the ring the way the frame watch does: (shift, missed delta as read this frame, dt).</summary>
    static ReadOnlySpan<Diagnostics.ScrollTraceSample> Recorded(Diagnostics.ScrollTraceRing ring, params (float Shift, int Read, float Dt)[] frames)
    {
        ring.Clear();
        foreach (var (shift, read, dt) in frames) ring.Add(Live(shift, dt: dt, missed: read));
        return ring.Samples();
    }

    [Fact]
    public void The_ring_moves_a_missed_delta_back_to_the_frame_whose_present_it_measured()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var s = Recorded(ring, (10f, 0, Refresh), (10f, 0, Refresh), (10f, 0, Refresh), (10f, 2, 3 * Refresh), (10f, 0, Refresh));
        Assert.Equal(new[] { 0, 0, 2, 0, 0 }, new[] { s[0].Missed, s[1].Missed, s[2].Missed, s[3].Missed, s[4].Missed });
        Assert.Equal(3 * Refresh, s[3].DtMs);   // dt stays with the frame that completed late
    }

    [Fact]
    public void The_delta_read_on_the_bursts_second_frame_is_the_idle_gap_before_it_and_is_dropped()
    {
        // The log's `33.0 40.9! 92.6 50.7 49.8` with liveMissed=11 and dtMax=8.8: eleven vblanks cannot fit in 8.8 ms.
        var ring = new Diagnostics.ScrollTraceRing();
        var s = Recorded(ring, (33.0f, 0, Refresh), (40.9f, 11, Refresh), (92.6f, 0, Refresh), (50.7f, 0, Refresh), (49.8f, 0, Refresh),
            (50f, 0, Refresh), (50f, 0, Refresh), (50f, 0, Refresh), (50f, 0, Refresh), (50f, 0, Refresh));
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.LiveMissed);
        Assert.NotEqual("present-hitch", v.Grade);
        Assert.Equal("smooth", v.Grade);
        Assert.DoesNotContain("!", Diagnostics.ScrollTraceRules.Format(s, Refresh));
    }

    [Fact]
    public void A_dead_frame_after_the_dropped_pre_burst_delta_is_a_stall_not_a_hitch()
    {
        // The log's `18.9 0.0! 48.0 24.6 26.5`: the `!` was the pre-burst idle; the 0.0 is a frame that moved nothing.
        var ring = new Diagnostics.ScrollTraceRing();
        var s = Recorded(ring, (18.9f, 0, Refresh), (0f, 1, Refresh), (48f, 0, Refresh), (24.6f, 0, Refresh), (26.5f, 0, Refresh),
            (26.5f, 0, Refresh), (26.5f, 0, Refresh), (26.5f, 0, Refresh), (26.5f, 0, Refresh), (26.5f, 0, Refresh));
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.LiveMissed);
        Assert.Equal(1, v.LiveZero);
        Assert.Equal("stalled-frames", v.Grade);
    }

    [Fact]
    public void A_present_hitch_mid_burst_is_still_reported_on_the_frame_that_was_held()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var frames = new (float, int, float)[16];
        for (int i = 0; i < frames.Length; i++) frames[i] = (12f, 0, Refresh);
        frames[11] = (12f, 1, 2 * Refresh);   // frame 10's present was a refresh late; the delta is read at frame 11
        var s = Recorded(ring, frames);
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.LiveMissed);
        Assert.Equal(1, s[10].Missed);
        Assert.Equal(0, s[11].Missed);
        Assert.Equal("present-hitch", v.Grade);
        Assert.Equal(1, v.Late);
        Assert.StartsWith("12.0 12.0 12.0 12.0 12.0 12.0 12.0 12.0 12.0 12.0 12.0! 12.0~", Diagnostics.ScrollTraceRules.Format(s, Refresh));
    }

    [Fact]
    public void Clearing_the_ring_starts_a_new_burst_whose_second_read_is_dropped_again()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        Recorded(ring, (10f, 0, Refresh), (10f, 3, Refresh), (10f, 4, Refresh));
        var s = Recorded(ring, (10f, 0, Refresh), (10f, 5, Refresh), (10f, 0, Refresh));
        Assert.Equal(3, s.Length);
        Assert.Equal(0, s[0].Missed);
        Assert.Equal(0, s[1].Missed);
    }

    [Fact]
    public void The_ring_keeps_the_newest_capacity_frames_oldest_first()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        int n = Diagnostics.ScrollTraceRules.Capacity + 25;
        for (int i = 0; i < n; i++) ring.Add(Live(i));
        var s = ring.Samples();
        Assert.Equal(Diagnostics.ScrollTraceRules.Capacity, s.Length);
        Assert.Equal(25f, s[0].DeltaDip);
        Assert.Equal(n - 1, s[^1].DeltaDip);
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Samples().Length);
    }

    // ── a resting finger, clamp pins and structural rebases are not stalls, hitches or dips ──

    /// <summary>Feed the ring fully formed samples the way the frame watch does (missed 0 everywhere).</summary>
    static ReadOnlySpan<Diagnostics.ScrollTraceSample> Recorded(Diagnostics.ScrollTraceRing ring, params Diagnostics.ScrollTraceSample[] frames)
    {
        ring.Clear();
        foreach (var f in frames) ring.Add(f);
        return ring.Samples();
    }

    /// <summary>9, 9, then <paramref name="zeros"/> zero frames, then 9, 9 — a drag that paused mid-way.</summary>
    static Diagnostics.ScrollTraceSample[] PausedDrag(int zeros, bool held)
    {
        var s = new Diagnostics.ScrollTraceSample[zeros + 4];
        s[0] = Live(9f); s[1] = Live(9f);
        for (int i = 0; i < zeros; i++) s[2 + i] = Live(0f) with { ContactHeld = held };
        s[zeros + 2] = Live(9f); s[zeros + 3] = Live(9f);
        return s;
    }

    [Fact]
    public void A_finger_resting_mid_drag_is_hold_frames_not_a_stall()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var v = Diagnostics.ScrollTraceRules.Analyse(Recorded(ring, PausedDrag(23, held: true)), Refresh);
        Assert.Equal(0, v.LiveZero);
        Assert.Equal(23, v.HoldFrames);
        Assert.Equal(27, v.LiveFrames);
        Assert.NotEqual("present-hitch", v.Grade);
        Assert.NotEqual("stalled-frames", v.Grade);
        Assert.Contains("holdFrames=23", Diagnostics.ScrollTraceRules.Describe(in v));
    }

    [Fact]
    public void The_same_zero_run_without_a_contact_is_a_stall()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var v = Diagnostics.ScrollTraceRules.Analyse(Recorded(ring, PausedDrag(23, held: false)), Refresh);
        Assert.Equal(23, v.LiveZero);
        Assert.Equal(0, v.HoldFrames);
        Assert.Equal("stalled-frames", v.Grade);
    }

    [Fact]
    public void A_short_held_zero_run_that_recovers_stays_a_stall()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var two = Diagnostics.ScrollTraceRules.Analyse(Recorded(ring, PausedDrag(2, held: true)), Refresh);
        Assert.Equal(2, two.LiveZero);
        Assert.Equal(0, two.HoldFrames);

        var three = Diagnostics.ScrollTraceRules.Analyse(Recorded(ring, PausedDrag(Diagnostics.ScrollTraceRules.HoldRun, held: true)), Refresh);
        Assert.Equal(0, three.LiveZero);
        Assert.Equal(Diagnostics.ScrollTraceRules.HoldRun, three.HoldFrames);
    }

    [Fact]
    public void A_missed_present_under_a_resting_finger_is_not_a_hitch()
    {
        var s = PausedDrag(23, held: true);
        s[10] = s[10] with { Missed = 2, DtMs = 3 * Refresh };
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.LiveMissed);
        Assert.NotEqual("present-hitch", v.Grade);
    }

    [Fact]
    public void A_clamp_pin_is_counted_and_marked()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var s = Recorded(ring, Live(8.3f), Live(0f) with { EdgePins = 1 }, Live(8.1f));
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.Pins);
        Assert.Equal("8.3 0.0p 8.1", Diagnostics.ScrollTraceRules.Format(s, Refresh));
        Assert.Contains("pins=1", Diagnostics.ScrollTraceRules.Describe(in v));
    }

    [Fact]
    public void A_structural_rebase_is_counted_and_marked_but_is_not_a_dip_or_a_jump()
    {
        var ring = new Diagnostics.ScrollTraceRing();
        var frames = EvenGlide(20, 8.1f);
        frames[10] = Live(70f) with { StructuralDip = 70f };   // the body was rebased by 70 DIP, not scrolled
        var s = Recorded(ring, frames);
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.Structural);
        Assert.Equal(0, v.Dips);
        Assert.Equal(0, v.LiveZero);
        Assert.True(v.Jitter < 0.01f, "jitter=" + v.Jitter);
        Assert.NotEqual("velocity-dips", v.Grade);
        Assert.Contains(" 70.0s ", Diagnostics.ScrollTraceRules.Format(s, Refresh));
        Assert.Contains("structural=1", Diagnostics.ScrollTraceRules.Describe(in v));
    }

    [Fact]
    public void A_structural_frame_that_reports_no_motion_is_not_a_stall_or_a_dip()
    {
        var s = EvenGlide(20, 8.1f);
        s[10] = Live(0f) with { StructuralDip = 70f };   // without the exclusion this frame reads as a stall AND a dip
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(1, v.Structural);
        Assert.Equal(0, v.LiveZero);
        Assert.Equal(0, v.Dips);
        Assert.NotEqual("stalled-frames", v.Grade);
    }

    [Fact]
    public void A_rebase_under_the_zero_threshold_is_not_structural()
    {
        var s = EvenGlide(12, 8.1f);
        s[5] = Live(8.1f) with { StructuralDip = 0.1f };
        var v = Diagnostics.ScrollTraceRules.Analyse(s, Refresh);
        Assert.Equal(0, v.Structural);
        Assert.DoesNotContain("s", Diagnostics.ScrollTraceRules.Format(s, Refresh));
    }
}
