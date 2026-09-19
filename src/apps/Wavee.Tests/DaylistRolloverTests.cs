// ── Wavee.Tests/DaylistRolloverTests.cs — the rollover grace + retry ladder ──────────────────────────────────────────
//
// Pure: no timers, no clock. Every instant is passed in.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DaylistRolloverTests
{
    [Fact]
    public void No_window_is_idle()
    {
        var v = DaylistRollover.Decide(0, 0, 0, out var fireAt);
        Assert.Equal(DaylistRollover.Verdict.Idle, v);
        Assert.Equal(0, fireAt);
    }

    [Fact]
    public void Negative_window_is_idle()
    {
        var v = DaylistRollover.Decide(-5, 0, 0, out _);
        Assert.Equal(DaylistRollover.Verdict.Idle, v);
    }

    [Fact]
    public void First_attempt_arms_at_end_plus_grace()
    {
        int end = 1_000;
        long expectedFireAt = (long)end * 1000 + DaylistRollover.GraceMs;
        var v = DaylistRollover.Decide(end, 0, 0, out var fireAt);
        Assert.Equal(DaylistRollover.Verdict.Arm, v);
        Assert.Equal(expectedFireAt, fireAt);
    }

    [Fact]
    public void Fires_exactly_at_due()
    {
        int end = 1_000;
        long due = (long)end * 1000 + DaylistRollover.GraceMs;
        var v = DaylistRollover.Decide(end, 0, due, out var fireAt);
        Assert.Equal(DaylistRollover.Verdict.Fire, v);
        Assert.Equal(due, fireAt);
    }

    [Fact]
    public void Fires_after_due()
    {
        int end = 1_000;
        long due = (long)end * 1000 + DaylistRollover.GraceMs;
        var v = DaylistRollover.Decide(end, 0, due + 1, out _);
        Assert.Equal(DaylistRollover.Verdict.Fire, v);
    }

    [Fact]
    public void Just_before_due_arms()
    {
        int end = 1_000;
        long due = (long)end * 1000 + DaylistRollover.GraceMs;
        var v = DaylistRollover.Decide(end, 0, due - 1, out _);
        Assert.Equal(DaylistRollover.Verdict.Arm, v);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30_000)]
    [InlineData(2, 90_000)]
    [InlineData(3, 210_000)]
    public void Ladder_instants_are_cumulative(int attempts, long extraMsOverGrace)
    {
        int end = 2_000;
        // Attempt zero is due after grace; later attempts add only the retry steps already spent.
        var v = DaylistRollover.Decide(end, attempts, 0, out var fireAt);
        Assert.Equal(DaylistRollover.Verdict.Arm, v);
        Assert.Equal((long)end * 1000 + DaylistRollover.GraceMs + extraMsOverGrace, fireAt);
    }

    [Fact]
    public void Exhausted_at_max_attempts()
    {
        var v = DaylistRollover.Decide(1_000, DaylistRollover.MaxAttempts, long.MaxValue, out var fireAt);
        Assert.Equal(DaylistRollover.Verdict.Exhausted, v);
        Assert.Equal(0, fireAt);
    }

    [Fact]
    public void Exhausted_beyond_max_attempts_too()
    {
        var v = DaylistRollover.Decide(1_000, DaylistRollover.MaxAttempts + 3, 0, out _);
        Assert.Equal(DaylistRollover.Verdict.Exhausted, v);
    }

    [Fact]
    public void DelayMs_floors_at_one_second()
    {
        Assert.Equal(1000, DaylistRollover.DelayMs(fireAtUnixMs: 500, nowUnixMs: 0));
        Assert.Equal(1000, DaylistRollover.DelayMs(fireAtUnixMs: 100, nowUnixMs: 100));
        Assert.Equal(1000, DaylistRollover.DelayMs(fireAtUnixMs: 0, nowUnixMs: 1000));
    }

    [Fact]
    public void DelayMs_passes_through_within_range()
    {
        Assert.Equal(5000, DaylistRollover.DelayMs(fireAtUnixMs: 5000, nowUnixMs: 0));
    }

    [Fact]
    public void DelayMs_caps_at_int_max_minus_one()
    {
        long huge = (long)int.MaxValue + 100;
        Assert.Equal(int.MaxValue - 1, DaylistRollover.DelayMs(fireAtUnixMs: huge, nowUnixMs: 0));
    }

    [Fact]
    public void Advanced_is_strictly_later()
    {
        Assert.True(DaylistRollover.Advanced(100, 200));
        Assert.False(DaylistRollover.Advanced(200, 200));
        Assert.False(DaylistRollover.Advanced(200, 100));
    }

    [Fact]
    public void ResetsOnActivation_false_before_window_ends()
    {
        Assert.False(DaylistRollover.ResetsOnActivation(windowEndUnixS: 1_000, lastFireUnixMs: 0, nowUnixMs: 999_000));
    }

    [Fact]
    public void ResetsOnActivation_false_within_min_refire_of_last_fire()
    {
        long end = 1_000;
        long now = end * 1000 + 30_000;
        long lastFire = now - (DaylistRollover.MinRefireMs - 1);
        Assert.False(DaylistRollover.ResetsOnActivation((int)end, lastFire, now));
    }

    [Fact]
    public void ResetsOnActivation_true_once_both_conditions_hold()
    {
        long end = 1_000;
        long now = end * 1000 + 30_000;
        long lastFire = now - DaylistRollover.MinRefireMs;
        Assert.True(DaylistRollover.ResetsOnActivation((int)end, lastFire, now));
    }

    [Fact]
    public void ResetsOnActivation_false_without_a_window()
    {
        Assert.False(DaylistRollover.ResetsOnActivation(0, 0, 1_000_000));
    }
}
