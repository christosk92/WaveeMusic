using System;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The text-chrome context band: fixed geometry, horizontal-overflow structure and the scroll spy (which section is
/// "here"), plus the arithmetic that keeps the pinned band and the page's clip inset in agreement.
/// </summary>
public class ContextBandLayoutTests
{
    // ── the band's fixed geometry ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band is 56 DIP and it is the SAME 56 the detail collapse ladder targets. If these two ever
    /// diverge, the artist hero's PresentedH bind lands on a height its band does not fill and the page shows a strip
    /// of raw content between the hero and the band.</summary>
    [Fact]
    public void BandHeight_IsTheOneCollapseFloor()
    {
        Assert.Equal(56f, ContextBandLayout.Height);
        Assert.Equal(DetailVerticalLayout.CompactIdentityHeight, ContextBandLayout.Height);
    }

    /// <summary>ONE hairline, and the active mark is the tab-strip's 2-DIP rung. Both are load-bearing: a second line
    /// would show the seam between the band's two pinned strata, and a mark thicker than the tab strip's would make
    /// page wayfinding louder than app wayfinding.</summary>
    [Fact]
    public void BandEdges_AreOneHairlineAndOneTwoDipMark()
    {
        Assert.Equal(1f, ContextBandLayout.HairlineHeight);
        Assert.Equal(2f, ContextBandLayout.UnderlineHeight);
    }

    // ── the estimator ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LabelEstimate_IsMonotoneAndCountsBothPaddings()
    {
        float pad = ContextBandLayout.PivotPadX;
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth("", pad));
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth((string?)null, pad));
        float last = 0f;
        for (int n = 0; n <= 40; n++)
        {
            float w = ContextBandLayout.EstimateLabelWidth(n, pad);
            Assert.True(w >= last, $"estimate went backwards at {n}");
            last = w;
        }
        // A negative length is arithmetic noise, never a negative slot.
        Assert.Equal(2f * pad, ContextBandLayout.EstimateLabelWidth(-5, pad));
    }

    [Fact]
    public void ActionsWidth_IsTheSumPlusTheGapsBetween()
    {
        Assert.Equal(0f, ContextBandLayout.ActionsWidth(ReadOnlySpan<float>.Empty));
        Assert.Equal(40f, ContextBandLayout.ActionsWidth([40f]));
        Assert.Equal(40f + 60f + ContextBandLayout.ActionGap, ContextBandLayout.ActionsWidth([40f, 60f]));
        Assert.Equal(30f + 30f + 30f + 2f * ContextBandLayout.ActionGap,
            ContextBandLayout.ActionsWidth([30f, 30f, 30f]));
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At the top of the page the FIRST section is the answer, not "none" — a pivot with no mark reads as
    /// broken, and the visitor genuinely is looking at section one.</summary>
    [Fact]
    public void AtRest_TheFirstSectionIsActive()
    {
        // Nothing has crossed: every top is below the band.
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [600f, 1200f, 1800f], ContextBandLayout.Height, 800f, atScrollEnd: false));
    }

    /// <summary>Arrival is early enough to describe what dominates the viewport: the incoming heading crosses the
    /// upper quarter of the usable region below the band, with the small probe retained as boundary tolerance.</summary>
    [Fact]
    public void ArrivalIsMeasuredAtTheUpperQuarterOfTheUsableViewport()
    {
        float band = ContextBandLayout.Height;
        const float viewport = 800f;
        float line = ContextBandLayout.SpyLine(band, viewport);
        Assert.Equal(250f, line);
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [-400f, line + 1f, 900f], band, viewport, atScrollEnd: false));
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-400f, line, 900f], band, viewport, atScrollEnd: false));
    }

    [Fact]
    public void ActivationLine_TracksViewportHeightAndFailsSoftBeforeMeasurement()
    {
        float band = ContextBandLayout.Height;
        Assert.Equal(150f, ContextBandLayout.SpyLine(band, 400f));
        Assert.Equal(250f, ContextBandLayout.SpyLine(band, 800f));
        Assert.Equal(band + ContextBandLayout.SpyProbe, ContextBandLayout.SpyLine(band, 0f));
    }

    [Fact]
    public void ScrollEnd_RequiresRealOverflowAndMovement()
    {
        Assert.False(ContextBandLayout.IsAtScrollEnd(0f, 800f, 800f));
        Assert.False(ContextBandLayout.IsAtScrollEnd(0f, 800f, 1200f));
        Assert.False(ContextBandLayout.IsAtScrollEnd(380f, 800f, 1200f));
        Assert.True(ContextBandLayout.IsAtScrollEnd(392f, 800f, 1200f));
        Assert.True(ContextBandLayout.IsAtScrollEnd(400f, 800f, 1200f));
    }

    /// <summary>A short final shelf cannot reach the quarter line when there is not enough content below it. At the
    /// real lower limit it nevertheless owns the page, while an unrealized tail still cannot be invented.</summary>
    [Fact]
    public void AtScrollEnd_TheLastMeasuredSectionWinsBelowTheQuarterLine()
    {
        float band = ContextBandLayout.Height;
        const float viewport = 800f;
        float belowLine = ContextBandLayout.SpyLine(band, viewport) + 160f;

        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-700f, -40f, belowLine], band, viewport, atScrollEnd: false));
        Assert.Equal(2, ContextBandLayout.ActiveSection(
            [-700f, -40f, belowLine], band, viewport, atScrollEnd: true));
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-700f, -40f, float.NaN], band, viewport, atScrollEnd: true));
    }

    /// <summary>Walking a whole page top to bottom: the active index is non-decreasing and lands on the last section.
    /// This is the property a hand-written "nearest section" scan gets wrong at the bottom of the page, where the last
    /// section is shorter than the viewport and never reaches the top edge.</summary>
    [Fact]
    public void ScrollingDown_AdvancesMonotonicallyAndEndsOnTheLastSection()
    {
        float[] contentTops = [0f, 700f, 1500f, 2100f, 2600f];
        float band = ContextBandLayout.Height;
        var tops = new float[contentTops.Length];
        int previous = 0;
        for (float offset = 0f; offset <= 2800f; offset += 5f)
        {
            for (int i = 0; i < tops.Length; i++) tops[i] = contentTops[i] - offset;
            int at = ContextBandLayout.ActiveSection(tops, band, 800f, atScrollEnd: false);
            Assert.True(at >= previous, $"active index went backwards at offset {offset}");
            previous = at;
        }
        Assert.Equal(contentTops.Length - 1, previous);
    }

    /// <summary>An unmeasured section (NaN) stops the scan instead of counting as arrived — otherwise a page whose
    /// lower half has not laid out yet would jump the mark to its final section on the first frame.</summary>
    [Fact]
    public void AnUnrealizedSection_StopsTheScan()
    {
        Assert.Equal(1, ContextBandLayout.ActiveSection(
            [-900f, -100f, float.NaN, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: false));
    }

    /// <summary>A scan that learned NOTHING (not even the first section has a measurement) reports −1 — "no answer,
    /// hold what you had" — rather than 0.
    ///
    /// <para>This is D40's tell, promoted to a contract. The spy's registry was being emptied after the frame that
    /// filled it, so every scan saw an all-unrealized page; answering 0 there published "you are in section one" as a
    /// positive fact derived from zero evidence, which made a DEAD spy look exactly like a working spy that was stuck
    /// on the first item. −1 is the honest answer and the live caller ignores it, so the mark holds instead of
    /// snapping home.</para></summary>
    [Fact]
    public void AScanThatLearnedNothing_HoldsTheLastAnswerInsteadOfSnappingToTheFirst()
    {
        Assert.Equal(-1, ContextBandLayout.ActiveSection(
            [float.NaN, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: false));
        Assert.Equal(-1, ContextBandLayout.ActiveSection(
            [float.NaN, -900f], ContextBandLayout.Height, 800f, atScrollEnd: true));
        // …but ONE realized section is evidence, and it answers normally.
        Assert.Equal(0, ContextBandLayout.ActiveSection(
            [-900f, float.NaN], ContextBandLayout.Height, 800f, atScrollEnd: true));
    }

    [Fact]
    public void AnEmptyPivot_HasNoActiveSection()
        => Assert.Equal(-1, ContextBandLayout.ActiveSection(
            ReadOnlySpan<float>.Empty, ContextBandLayout.Height, 800f, atScrollEnd: true));

    [Fact]
    public void ScrollTarget_ParksTheSectionUnderTheBandAndNeverGoesNegative()
    {
        Assert.Equal(944f, ContextBandLayout.ScrollTargetFor(400f, 600f, ContextBandLayout.Height));
        Assert.Equal(0f, ContextBandLayout.ScrollTargetFor(0f, -400f, ContextBandLayout.Height));
    }

    /// <summary>The other half of the contract: if the band paints nothing, the page owes it a CLIP, and that clip
    /// must cover the band's WHOLE height or content shows through the gap.
    ///
    /// <para>The artist band is the identity row alone (56). The detail band is the identity row PLUS the tracklist's
    /// column row and the shared hairline, which is exactly what <c>StickyClipInset</c> already sums — so the two
    /// pages clip at two different numbers for one reason, and that reason is arithmetic rather than taste.</para></summary>
    [Fact]
    public void TheClipInset_CoversTheWholeBand()
    {
        // The artist arm: the band IS the identity row.
        Assert.Equal(ContextBandLayout.Height, ContextBandLayout.ClipInset);

        // The detail arm: identity row + column header + the band's one hairline, with nothing left over.
        Assert.Equal(ContextBandLayout.Height + ContextBandLayout.HairlineHeight
                     + DetailVerticalLayout.ChromeHeaderHeight,
                     DetailVerticalLayout.StickyClipInset());
        Assert.True(DetailVerticalLayout.StickyClipInset() > ContextBandLayout.ClipInset);

        // …and it grows with the optional Liked filter rail, which is part of the same pinned plate.
        Assert.Equal(DetailVerticalLayout.StickyClipInset() + 48f,
                     DetailVerticalLayout.StickyClipInset(contentFilterExtent: 48f));

        // The cut is feathered, not guillotined, and both paths use the SAME band so they dissolve identically.
        Assert.Equal(DetailVerticalLayout.StickyFadeBand, ContextBandLayout.ClipFadeBand);
        Assert.True(ContextBandLayout.ClipFadeBand > 0f);
    }
}
