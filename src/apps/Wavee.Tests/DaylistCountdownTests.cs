// ── Wavee.Tests/DaylistCountdownTests.cs — countdown clock arithmetic and the sleep re-anchor ────────────────────────
//
// Pure: no timers, no thread. The "sleep" scenario is simulated by advancing the wall clock far more than the frame
// clock between two Now() calls, exactly what a suspend/resume looks like to this code.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DaylistCountdownTests
{
    [Fact]
    public void Now_is_anchor_plus_frame_delta()
    {
        long now = DaylistCountdown.Now(unixAnchorMs: 10_000, frameAnchorMs: 500, frameNowMs: 1_500);
        Assert.Equal(11_000, now);
    }

    [Fact]
    public void Now_with_zero_frame_delta_returns_the_anchor()
    {
        Assert.Equal(10_000, DaylistCountdown.Now(10_000, 500, 500));
    }

    [Theory]
    [InlineData(15, 0, true)]
    [InlineData(30, 0, true)]
    [InlineData(45, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(14, 0, false)]
    [InlineData(16, 0, false)]
    public void NeedsReanchor_on_tick_cadence(int tick, long gapMs, bool expected)
    {
        Assert.Equal(expected, DaylistCountdown.NeedsReanchor(tick, gapMs));
    }

    [Fact]
    public void NeedsReanchor_on_a_large_gap_even_off_cadence()
    {
        Assert.True(DaylistCountdown.NeedsReanchor(tick: 7, frameDeltaSinceLastTickMs: 3_000));
    }

    [Fact]
    public void NeedsReanchor_false_on_small_gap_off_cadence()
    {
        Assert.False(DaylistCountdown.NeedsReanchor(tick: 7, frameDeltaSinceLastTickMs: 100));
    }

    [Fact]
    public void RemainingMs_floors_at_zero()
    {
        Assert.Equal(0, DaylistCountdown.RemainingMs(expiresAtMs: 1_000, nowUnixMs: 5_000));
        Assert.Equal(0, DaylistCountdown.RemainingMs(expiresAtMs: 1_000, nowUnixMs: 1_000));
    }

    [Fact]
    public void RemainingMs_positive_before_expiry()
    {
        Assert.Equal(500, DaylistCountdown.RemainingMs(expiresAtMs: 1_500, nowUnixMs: 1_000));
    }

    [Theory]
    [InlineData(0, DaylistCountdown.Phase.Idle)]
    [InlineData(-1, DaylistCountdown.Phase.Idle)]
    public void PhaseOf_no_window_is_idle(long expiresAt, DaylistCountdown.Phase expected)
    {
        Assert.Equal(expected, DaylistCountdown.PhaseOf(expiresAt, nowUnixMs: 1_000));
    }

    [Fact]
    public void PhaseOf_future_is_counting()
    {
        Assert.Equal(DaylistCountdown.Phase.Counting, DaylistCountdown.PhaseOf(expiresAtMs: 2_000, nowUnixMs: 1_000));
    }

    [Fact]
    public void PhaseOf_exact_instant_is_rolling()
    {
        Assert.Equal(DaylistCountdown.Phase.Rolling, DaylistCountdown.PhaseOf(expiresAtMs: 1_000, nowUnixMs: 1_000));
    }

    [Fact]
    public void PhaseOf_past_is_rolling()
    {
        Assert.Equal(DaylistCountdown.Phase.Rolling, DaylistCountdown.PhaseOf(expiresAtMs: 1_000, nowUnixMs: 2_000));
    }

    [Fact]
    public void Sleep_scenario_after_reanchor_remaining_follows_the_wall_clock()
    {
        // Session starts with wall == frame == 0, a window expiring in 1 hour.
        long expiresAt = 3_600_000;
        long frameAnchor = 0, unixAnchor = 0;

        // The machine sleeps: wall advances 3600 s but the frame clock only ticks 10 s (it stalled).
        long frameNow = 10_000;
        long staleWallGap = frameNow - frameAnchor; // Even this partial advance exceeds the 2.5 s gap trigger.
        Assert.True(DaylistCountdown.NeedsReanchor(tick: 1, frameDeltaSinceLastTickMs: staleWallGap));

        // The real wall clock (read separately, e.g. from the OS) shows 3600 s passed.
        long realUnixNow = unixAnchor + 3_600_000;

        // Re-anchor: fold the real wall time back into the anchors.
        unixAnchor = realUnixNow;
        frameAnchor = frameNow;

        long now = DaylistCountdown.Now(unixAnchor, frameAnchor, frameNowMs: frameAnchor);
        Assert.Equal(realUnixNow, now);
        Assert.Equal(0, DaylistCountdown.RemainingMs(expiresAt, now));
        Assert.Equal(DaylistCountdown.Phase.Rolling, DaylistCountdown.PhaseOf(expiresAt, now));
    }
}
