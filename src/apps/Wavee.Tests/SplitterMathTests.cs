using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <see cref="SplitterMath"/> had NO xUnit coverage before issue #84 — only the engine's in-app
/// <c>ControlsSuite.SplitterMathChecks</c> exercised it, which nothing in this repo's own test run gates. #84
/// re-points the sidebar splitter's detents around the new 180-DIP <c>ShellResponsiveLayout.NavPaneMinW</c> floor
/// (<c>WaveeShell.cs</c>: <c>FadeStart = 240f, FadeDistance = 64f, ForcePush = 64f, ReExpand = 190f</c>), so this pins
/// the pure arithmetic those numbers depend on.
/// </summary>
public class SplitterMathTests
{
    [Fact]
    public void ClampWidth_ClampsToTheGivenRange()
    {
        Assert.Equal(180f, SplitterMath.ClampWidth(10f, 180f, 460f));
        Assert.Equal(460f, SplitterMath.ClampWidth(9000f, 180f, 460f));
        Assert.Equal(300f, SplitterMath.ClampWidth(300f, 180f, 460f));
    }

    [Fact]
    public void RawWidth_TrailingPolarityGrowsWithThePointer()
    {
        // A left pane's own right seam: dragging right (px > startPx) grows the width.
        Assert.Equal(220f, SplitterMath.RawWidth(startW: 200f, startPx: 500f, px: 520f, SplitterPolarity.Trailing));
        Assert.Equal(180f, SplitterMath.RawWidth(startW: 200f, startPx: 500f, px: 480f, SplitterPolarity.Trailing));
    }

    [Fact]
    public void RawWidth_LeadingPolarityShrinksWithThePointer()
    {
        // A right pane's own left seam: dragging right SHRINKS it.
        Assert.Equal(180f, SplitterMath.RawWidth(startW: 200f, startPx: 500f, px: 520f, SplitterPolarity.Leading));
        Assert.Equal(220f, SplitterMath.RawWidth(startW: 200f, startPx: 500f, px: 480f, SplitterPolarity.Leading));
    }

    [Theory]
    [InlineData(240f, 240f, 0f)]     // right at FadeStart: no overshoot yet
    [InlineData(240f, 180f, 60f)]    // at the new 180-DIP floor: 60 DIP into the resist zone
    [InlineData(240f, 176f, 64f)]    // WaveeShell's chosen collapse point (FadeStart − ForcePush)
    public void Into_IsHowFarPastFadeStartThePointerHasPushed(float fadeStart, float rawW, float expected)
        => Assert.Equal(expected, SplitterMath.Into(fadeStart, rawW));

    [Fact]
    public void ResistWidth_ShrinksBySlowerThanTheRawOvershoot()
    {
        // WaveeShell's Resist stays at the engine default 0.28: at the 180-DIP floor (into = 60) the STICKY width is
        // still well above the raw 180 the pointer has actually reached — that IS the resist feel.
        float into = SplitterMath.Into(fadeStart: 240f, rawW: 180f);
        float sticky = SplitterMath.ResistWidth(fadeStart: 240f, into, resist: 0.28f);
        Assert.Equal(223.2f, sticky, 3);
        Assert.True(sticky > 180f);
    }

    [Fact]
    public void Fade_IsFullOpacityBeforeFadeStart_AndMinFadeAtOrPastFadeDistance()
    {
        // WaveeShell's FadeDistance = 64 spans the whole 240 → 176 resist-to-collapse run.
        Assert.Equal(1f, SplitterMath.Fade(into: 0f, fadeDistance: 64f, minFade: 0.35f));
        Assert.Equal(0.35f, SplitterMath.Fade(into: 64f, fadeDistance: 64f, minFade: 0.35f), 3);
        // Halfway through the resist span (into = 32, the pane at 208) sits halfway between full and min opacity.
        Assert.Equal(0.675f, SplitterMath.Fade(into: 32f, fadeDistance: 64f, minFade: 0.35f), 3);
        // Past the distance the fade clamps rather than overshooting below minFade.
        Assert.Equal(0.35f, SplitterMath.Fade(into: 200f, fadeDistance: 64f, minFade: 0.35f), 3);
    }

    [Fact]
    public void Fade_ANonPositiveDistanceNeverFades()
        => Assert.Equal(1f, SplitterMath.Fade(into: 40f, fadeDistance: 0f, minFade: 0.35f));

    [Fact]
    public void ShouldCollapse_FiresExactlyAtForcePush()
    {
        // WaveeShell's ForcePush = 64 ⇒ collapse at FadeStart(240) − 64 = 176, just below the new 180 floor.
        Assert.False(SplitterMath.ShouldCollapse(into: 63.9f, forcePush: 64f));
        Assert.True(SplitterMath.ShouldCollapse(into: 64f, forcePush: 64f));
        Assert.True(SplitterMath.ShouldCollapse(into: 100f, forcePush: 64f));
    }

    [Fact]
    public void ShouldCollapse_ANonPositiveForcePushNeverCollapses()
        => Assert.False(SplitterMath.ShouldCollapse(into: 1000f, forcePush: 0f));

    [Fact]
    public void TheChosenDetents_CollapseBeforeReachingTheCompactRail()
    {
        // End-to-end sanity over WaveeShell's actual numbers: dragging from FadeStart down to the collapse point stays
        // ABOVE CompactRailW (56) the whole way, and the collapse point itself sits just under the new 180 floor.
        const float fadeStart = 240f, forcePush = 64f, compactRailW = 56f;
        float collapseAtRawW = fadeStart - forcePush;
        Assert.Equal(176f, collapseAtRawW);
        Assert.True(collapseAtRawW < 180f);
        Assert.True(collapseAtRawW > compactRailW);

        // And ReExpand sits a small hysteresis band above the collapse point, not immediately at it — otherwise the
        // pane would flicker open/closed right at the collapse threshold.
        const float reExpand = 190f;
        Assert.True(reExpand > collapseAtRawW);
        Assert.True(reExpand < fadeStart);
    }
}
