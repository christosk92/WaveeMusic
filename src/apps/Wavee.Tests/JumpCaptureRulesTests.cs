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
}
