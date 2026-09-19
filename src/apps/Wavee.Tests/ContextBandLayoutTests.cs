// ── Wavee.Tests/ContextBandLayoutTests.cs — the text-chrome context band: geometry, estimator, scroll spy, clip ─────
//
// Ported from _old/Wavee.Tests/ContextBandLayoutTests.cs onto `Detail.BandLayout` (Entities/Detail.cs, A1: the band's
// geometry is detail-frame arithmetic). Every assertion is 0.2.9's; only the call shape changed.

using Xunit;
using BandLayout = Wavee.Detail.BandLayout;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class ContextBandLayoutTests
{
    // ── the band's fixed geometry ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is 56 DIP and it is the SAME 56 the detail collapse ladder targets.</summary>
    [Fact]
    public void BandHeight_IsTheOneCollapseFloor()
    {
        Assert.Equal(56f, BandLayout.Height);
        Assert.Equal(VerticalLayout.CompactIdentityHeight, BandLayout.Height);
    }

    /// <summary>ONE hairline, and the active mark is the tab strip's 2-DIP rung.</summary>
    [Fact]
    public void BandEdges_AreOneHairlineAndOneTwoDipMark()
    {
        Assert.Equal(1f, BandLayout.HairlineHeight);
        Assert.Equal(2f, BandLayout.UnderlineHeight);
    }

    // ── the estimator ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LabelEstimate_IsMonotoneAndCountsBothPaddings()
    {
        float pad = BandLayout.PivotPadX;
        Assert.Equal(2f * pad, BandLayout.EstimateLabelWidth("", pad));
        Assert.Equal(2f * pad, BandLayout.EstimateLabelWidth((string?)null, pad));
        float last = 0f;
        for (int n = 0; n <= 40; n++)
        {
            float w = BandLayout.EstimateLabelWidth(n, pad);
            Assert.True(w >= last, $"estimate went backwards at {n}");
            last = w;
        }
        // A negative length is arithmetic noise, never a negative slot.
        Assert.Equal(2f * pad, BandLayout.EstimateLabelWidth(-5, pad));
    }

    [Fact]
    public void ActionsWidth_IsTheSumPlusTheGapsBetween()
    {
        Assert.Equal(0f, BandLayout.ActionsWidth(ReadOnlySpan<float>.Empty));
        Assert.Equal(40f, BandLayout.ActionsWidth([40f]));
        Assert.Equal(40f + 60f + BandLayout.ActionGap, BandLayout.ActionsWidth([40f, 60f]));
        Assert.Equal(30f + 30f + 30f + 2f * BandLayout.ActionGap, BandLayout.ActionsWidth([30f, 30f, 30f]));
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At the top of the page the FIRST section is the answer, not "none".</summary>
    [Fact]
    public void AtRest_TheFirstSectionIsActive()
        => Assert.Equal(0, BandLayout.ActiveSection([600f, 1200f, 1800f], BandLayout.Height, 800f, atScrollEnd: false));

    /// <summary>Arrival is measured at the upper quarter of the usable region below the band, plus the probe.</summary>
    [Fact]
    public void ArrivalIsMeasuredAtTheUpperQuarterOfTheUsableViewport()
    {
        float band = BandLayout.Height;
        const float viewport = 800f;
        float line = BandLayout.SpyLine(band, viewport);
        Assert.Equal(250f, line);
        Assert.Equal(0, BandLayout.ActiveSection([-400f, line + 1f, 900f], band, viewport, atScrollEnd: false));
        Assert.Equal(1, BandLayout.ActiveSection([-400f, line, 900f], band, viewport, atScrollEnd: false));
    }

    [Fact]
    public void ActivationLine_TracksViewportHeightAndFailsSoftBeforeMeasurement()
    {
        float band = BandLayout.Height;
        Assert.Equal(150f, BandLayout.SpyLine(band, 400f));
        Assert.Equal(250f, BandLayout.SpyLine(band, 800f));
        Assert.Equal(band + BandLayout.SpyProbe, BandLayout.SpyLine(band, 0f));
    }

    [Fact]
    public void ScrollEnd_RequiresRealOverflowAndMovement()
    {
        Assert.False(BandLayout.IsAtScrollEnd(0f, 800f, 800f));
        Assert.False(BandLayout.IsAtScrollEnd(0f, 800f, 1200f));
        Assert.False(BandLayout.IsAtScrollEnd(380f, 800f, 1200f));
        Assert.True(BandLayout.IsAtScrollEnd(392f, 800f, 1200f));
        Assert.True(BandLayout.IsAtScrollEnd(400f, 800f, 1200f));
    }

    /// <summary>At the real lower limit a short final shelf owns the page; an unrealized tail cannot be invented.</summary>
    [Fact]
    public void AtScrollEnd_TheLastMeasuredSectionWinsBelowTheQuarterLine()
    {
        float band = BandLayout.Height;
        const float viewport = 800f;
        float belowLine = BandLayout.SpyLine(band, viewport) + 160f;

        Assert.Equal(1, BandLayout.ActiveSection([-700f, -40f, belowLine], band, viewport, atScrollEnd: false));
        Assert.Equal(2, BandLayout.ActiveSection([-700f, -40f, belowLine], band, viewport, atScrollEnd: true));
        Assert.Equal(1, BandLayout.ActiveSection([-700f, -40f, float.NaN], band, viewport, atScrollEnd: true));
    }

    /// <summary>Walking a page top to bottom: the index is non-decreasing and lands on the last section.</summary>
    [Fact]
    public void ScrollingDown_AdvancesMonotonicallyAndEndsOnTheLastSection()
    {
        float[] contentTops = [0f, 700f, 1500f, 2100f, 2600f];
        float band = BandLayout.Height;
        var tops = new float[contentTops.Length];
        int previous = 0;
        for (float offset = 0f; offset <= 2800f; offset += 5f)
        {
            for (int i = 0; i < tops.Length; i++) tops[i] = contentTops[i] - offset;
            int at = BandLayout.ActiveSection(tops, band, 800f, atScrollEnd: false);
            Assert.True(at >= previous, $"active index went backwards at offset {offset}");
            previous = at;
        }
        Assert.Equal(contentTops.Length - 1, previous);
    }

    /// <summary>An unmeasured section (NaN) stops the scan instead of counting as arrived.</summary>
    [Fact]
    public void AnUnrealizedSection_StopsTheScan()
        => Assert.Equal(1, BandLayout.ActiveSection([-900f, -100f, float.NaN, float.NaN], BandLayout.Height, 800f, atScrollEnd: false));

    /// <summary>A scan that learned NOTHING reports −1 — "no answer, hold what you had" — never 0 (D40's tell).</summary>
    [Fact]
    public void AScanThatLearnedNothing_HoldsTheLastAnswerInsteadOfSnappingToTheFirst()
    {
        Assert.Equal(-1, BandLayout.ActiveSection([float.NaN, float.NaN], BandLayout.Height, 800f, atScrollEnd: false));
        Assert.Equal(-1, BandLayout.ActiveSection([float.NaN, -900f], BandLayout.Height, 800f, atScrollEnd: true));
        // …but ONE realized section is evidence, and it answers normally.
        Assert.Equal(0, BandLayout.ActiveSection([-900f, float.NaN], BandLayout.Height, 800f, atScrollEnd: true));
    }

    [Fact]
    public void AnEmptyPivot_HasNoActiveSection()
        => Assert.Equal(-1, BandLayout.ActiveSection(ReadOnlySpan<float>.Empty, BandLayout.Height, 800f, atScrollEnd: true));

    [Fact]
    public void ScrollTarget_ParksTheSectionUnderTheBandAndNeverGoesNegative()
    {
        Assert.Equal(944f, BandLayout.ScrollTargetFor(400f, 600f, BandLayout.Height));
        Assert.Equal(0f, BandLayout.ScrollTargetFor(0f, -400f, BandLayout.Height));
    }

    /// <summary>The band paints nothing, so the page owes it a CLIP covering its WHOLE height: the artist arm at the
    /// identity row alone (56), the detail arm at identity row + column header + the one hairline.</summary>
    [Fact]
    public void TheClipInset_CoversTheWholeBand()
    {
        Assert.Equal(BandLayout.Height, BandLayout.ClipInset);

        Assert.Equal(BandLayout.Height + BandLayout.HairlineHeight + VerticalLayout.ChromeHeaderHeight,
                     VerticalLayout.StickyClipInset());
        Assert.True(VerticalLayout.StickyClipInset() > BandLayout.ClipInset);

        Assert.Equal(VerticalLayout.StickyClipInset() + 48f, VerticalLayout.StickyClipInset(contentFilterExtent: 48f));

        Assert.Equal(VerticalLayout.StickyFadeBand, BandLayout.ClipFadeBand);
        Assert.True(BandLayout.ClipFadeBand > 0f);
    }
}
