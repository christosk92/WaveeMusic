// ── Wavee.Tests/JumpCaptureRulesTests.cs — the auto evidence bundle on an unrequested scroll jump (2026-09-25, item J) ──
//
// The engine's always-on detector counts every jump of a viewport at rest (ScrollProbe.Jumps); the first new jump captures
// one bundle ("jump-auto"), never two within the minimum interval, at most the session cap, and the first frame only takes
// the counter's baseline.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class JumpCaptureRulesTests
{
    [Fact]
    public void The_first_frame_takes_the_baseline_and_a_new_jump_captures()
    {
        long seen = -1;
        Assert.False(JumpCaptureRules.Step(ref seen, 3, double.MaxValue, 0));   // baseline: jumps before the watch
        Assert.False(JumpCaptureRules.Step(ref seen, 3, double.MaxValue, 0));   // nothing new
        Assert.True(JumpCaptureRules.Step(ref seen, 4, double.MaxValue, 0));    // a new jump
        Assert.Equal(4L, seen);
    }

    [Fact]
    public void The_interval_and_the_session_cap_hold()
    {
        long seen = 4;
        Assert.False(JumpCaptureRules.Step(ref seen, 5, JumpCaptureRules.MinIntervalMs - 1, 1));
        Assert.True(JumpCaptureRules.Step(ref seen, 6, JumpCaptureRules.MinIntervalMs, 1));
        Assert.False(JumpCaptureRules.Step(ref seen, 7, double.MaxValue, JumpCaptureRules.MaxPerSession));
    }

    [Fact]
    public void Extent_growth_at_the_top_is_benign_anything_else_is_not()
    {
        var extent = FluentGpu.Scroll.Diag.ScrollJumpCause.Extent;
        Assert.True(JumpCaptureRules.IsBenign(extent, 0, true, 1));
        Assert.False(JumpCaptureRules.IsBenign(extent, 0, true, 2));     // another jump in the same frame
        Assert.False(JumpCaptureRules.IsBenign(extent, 0, false, 1)); // any other list
        Assert.False(JumpCaptureRules.IsBenign(extent, 0, false, 1));
        Assert.False(JumpCaptureRules.IsBenign(extent, 340, true, 1));   // mid-list
        Assert.False(JumpCaptureRules.IsBenign(FluentGpu.Scroll.Diag.ScrollJumpCause.Plan, 0, true, 1));
    }

    [Fact]
    public void Any_auto_bundle_holds_back_the_next_one_of_any_cause()
    {
        long seen = 4;
        Assert.False(JumpCaptureRules.Step(ref seen, 5, double.MaxValue, 0, ClampCaptureRules.AnyMinIntervalMs - 1));
        Assert.True(JumpCaptureRules.Step(ref seen, 6, double.MaxValue, 0, ClampCaptureRules.AnyMinIntervalMs));
        bool armed = true;
        Assert.False(ClampCaptureRules.Step(ref armed, 3, true, double.MaxValue, 1000));
        Assert.True(ClampCaptureRules.Step(ref armed, 3, true, double.MaxValue, double.MaxValue));
    }
}
