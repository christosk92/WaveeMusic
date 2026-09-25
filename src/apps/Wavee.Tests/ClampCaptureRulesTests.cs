// ── Wavee.Tests/ClampCaptureRulesTests.cs — the auto evidence bundle's trigger (2026-09-25, item G) ───────────────────
//
// One bundle on the FIRST coverage clamp of a scroll burst, never two within the minimum interval, re-armed when the
// burst ends; and the viewport the line names is the one whose shown window reaches past its realized coverage.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ClampCaptureRulesTests
{
    [Fact]
    public void The_first_clamp_of_a_burst_captures_once()
    {
        bool armed = true;
        Assert.False(ClampCaptureRules.Step(ref armed, 0, scrollActive: true, double.MaxValue));   // scrolling, no clamp
        Assert.True(ClampCaptureRules.Step(ref armed, 3, scrollActive: true, double.MaxValue));    // first clamp: capture
        Assert.False(armed);
        Assert.False(ClampCaptureRules.Step(ref armed, 5, scrollActive: true, double.MaxValue));   // same burst: no second
    }

    [Fact]
    public void The_end_of_a_burst_re_arms_but_the_interval_still_holds()
    {
        bool armed = false;
        Assert.False(ClampCaptureRules.Step(ref armed, 0, scrollActive: false, 100));   // burst over: re-armed
        Assert.True(armed);
        Assert.False(ClampCaptureRules.Step(ref armed, 2, scrollActive: true, ClampCaptureRules.MinIntervalMs - 1));
        Assert.True(armed);                                                              // not spent on a refused capture
        Assert.True(ClampCaptureRules.Step(ref armed, 2, scrollActive: true, ClampCaptureRules.MinIntervalMs));
    }

    [Theory]
    [InlineData(1000, 800, 5000, 0, 2000, 40, false)]      // inside coverage
    [InlineData(1000, 800, 5000, 1200, 2400, 40, true)]    // the window starts above the realized rows
    [InlineData(1000, 800, 5000, 0, 1500, 40, true)]       // the window runs past the realized rows
    [InlineData(1000, 800, 5000, 1200, 2400, 0, false)]    // a plain scroller has no coverage to be past
    // A shelf SHORTER than its viewport (3 cards, 582.8 of 773.1): its window runs past the content end, not past the
    // realized rows — the verify run's first auto line named exactly this (vp=- offset=0 window=773.1 cover=[0,582.8]).
    [InlineData(0, 773.1, 582.8, 0, 582.8, 3, false)]
    public void A_viewport_is_named_when_its_window_leaves_its_coverage(double offset, double viewport, double extent,
        double start, double end, int items, bool past)
        => Assert.Equal(past, ClampCaptureRules.ShowsPastCoverage(offset, viewport, extent, start, end, items));
}
