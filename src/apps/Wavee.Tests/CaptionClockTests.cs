// ── Wavee.Tests/CaptionClockTests.cs — the stage caption's playhead and its one-shot wake (themes plan C4, X11) ────────
// (Shell/CaptionClock.cs.) Pure: the frame clock is an argument, so a fake one drives the REAL clock.
//
//   THE PLAYHEAD   re-anchors on every CHANGED report and on a play/pause edge (without the edge a resume would extrapolate
//                  across the whole paused gap); extrapolates only while the transport advances; clamps into the duration.
//   THE WAKE       one one-shot at the next view edge, re-armed when the edge or the due instant moved, and again after it FIRED.

using Xunit;

namespace Wavee.Tests;

public class CaptionClockPlayheadTests
{
    [Fact]
    public void Before_the_first_report_the_clock_is_not_anchored()
    {
        var clock = new Stage.CaptionClock();
        Assert.False(clock.Anchored);
        Assert.False(clock.Advancing);
    }

    [Fact]
    public void The_first_report_anchors_and_a_repeat_of_it_does_not()
    {
        var clock = new Stage.CaptionClock();
        Assert.True(clock.Report(10_000, advancing: true, frameNowMs: 1_000));
        Assert.True(clock.Anchored);
        Assert.True(clock.Advancing);
        Assert.False(clock.Report(10_000, advancing: true, frameNowMs: 1_500));     // the same report is not a re-anchor
    }

    [Fact]
    public void While_advancing_the_position_extrapolates_from_the_anchor()
    {
        var clock = new Stage.CaptionClock();
        clock.Report(10_000, advancing: true, frameNowMs: 1_000);
        Assert.Equal(10_000L, clock.Now(1_000, 200_000));
        Assert.Equal(10_750L, clock.Now(1_750, 200_000));
    }

    [Fact]
    public void While_paused_the_position_stands_still()
    {
        var clock = new Stage.CaptionClock();
        clock.Report(10_000, advancing: false, frameNowMs: 1_000);
        Assert.Equal(10_000L, clock.Now(9_000, 200_000));
    }

    [Fact]
    public void A_changed_report_re_anchors()
    {
        var clock = new Stage.CaptionClock();
        clock.Report(10_000, advancing: true, frameNowMs: 1_000);
        Assert.True(clock.Report(50_000, advancing: true, frameNowMs: 2_000));       // a seek
        Assert.Equal(50_500L, clock.Now(2_500, 200_000));
    }

    [Fact]
    public void A_play_pause_edge_re_anchors_even_when_the_position_did_not_move()
    {
        var clock = new Stage.CaptionClock();
        clock.Report(10_000, advancing: false, frameNowMs: 1_000);                  // paused for a long while
        Assert.True(clock.Report(10_000, advancing: true, frameNowMs: 60_000));      // the resume
        // anchored at the RESUME instant: a stale anchor would extrapolate the whole 59 s paused gap
        Assert.Equal(10_100L, clock.Now(60_100, 200_000));
    }

    [Fact]
    public void The_position_is_clamped_into_the_duration()
    {
        var clock = new Stage.CaptionClock();
        clock.Report(199_000, advancing: true, frameNowMs: 0);
        Assert.Equal(200_000L, clock.Now(5_000, 200_000));
        Assert.Equal(204_000L, clock.Now(5_000, durationMs: 0));                     // no duration: only the floor applies
    }
}

public class CaptionClockWakeTests
{
    [Fact]
    public void Nothing_to_wake_for_arms_nothing()
    {
        var clock = new Stage.CaptionClock();
        Assert.False(clock.ArmEdge(long.MaxValue, 0, 0, out _));
    }

    [Fact]
    public void An_edge_arms_a_one_shot_for_the_media_time_left()
    {
        var clock = new Stage.CaptionClock();
        Assert.True(clock.ArmEdge(5_000, mediaNowMs: 1_000, frameNowMs: 100, out float delay));
        Assert.Equal(4_000f, delay);
    }

    [Fact]
    public void The_same_edge_with_a_due_instant_that_moved_by_frame_jitter_keeps_the_armed_wake()
    {
        var clock = new Stage.CaptionClock();
        clock.ArmEdge(5_000, 1_000, 100, out _);
        Assert.False(clock.ArmEdge(5_000, 1_001, 100, out _));                       // 1 ms: inside the re-arm slack
        Assert.True(clock.ArmEdge(5_000, 1_500, 100, out float delay));              // a seek within the line moved the due instant
        Assert.Equal(3_500f, delay);
    }

    [Fact]
    public void A_new_edge_re_arms()
    {
        var clock = new Stage.CaptionClock();
        clock.ArmEdge(5_000, 1_000, 100, out _);
        Assert.True(clock.ArmEdge(9_000, 1_000, 100, out float delay));
        Assert.Equal(8_000f, delay);
    }

    [Fact]
    public void A_fired_wake_arms_afresh_even_for_the_same_edge()
    {
        var clock = new Stage.CaptionClock();
        clock.ArmEdge(5_000, 1_000, 100, out _);
        clock.FireEdge();
        Assert.True(clock.ArmEdge(5_000, 1_000, 100, out _));    // a timer that fired a little early on another clock finds the view unchanged
    }

    [Fact]
    public void Clearing_an_armed_wake_cancels_it_once()
    {
        var clock = new Stage.CaptionClock();
        clock.ArmEdge(5_000, 1_000, 100, out _);
        Assert.True(clock.ArmEdge(long.MaxValue, 0, 0, out float delay));
        Assert.True(delay < 0f);                                 // < 0 = cancel
        Assert.False(clock.ArmEdge(long.MaxValue, 0, 0, out _));
    }
}
