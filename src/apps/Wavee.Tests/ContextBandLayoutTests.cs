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

    /// <summary>At the top of the page, with no pivot section past the activation line yet, NO section is here: the
    /// hero and the Top tracks band sit above the first pivot section (Albums / Singles &amp; EPs), and lighting that
    /// section's underline while Top tracks fills the screen is the 2026-09-25 defect (E). "None" is its own answer —
    /// distinct from −1, "hold what you had" (D40).</summary>
    [Fact]
    public void AtRest_AboveTheFirstSection_NoSectionIsActive()
        => Assert.Equal(BandLayout.NoSection, BandLayout.ActiveSection([600f, 1200f, 1800f], BandLayout.Height, 800f, atScrollEnd: false));

    /// <summary>The owner's case: Top tracks on screen, the first pivot section's top at 900 DIP and the second at 1500,
    /// under the 56-DIP band in an 800-DIP viewport (activation line 250) ⇒ none.</summary>
    [Fact]
    public void TopTracksOnScreen_LightsNoPivotSection()
        => Assert.Equal(BandLayout.NoSection, BandLayout.ActiveSection([900f, 1500f], 56f, 800f, atScrollEnd: false));

    /// <summary>"None" draws no underline; a real answer draws its link's; "hold" never reaches the pivot (the caller
    /// keeps its last answer), and a past-the-end index clamps to the last link.</summary>
    [Fact]
    public void ThePivotDrawsNoUnderlineForNone()
    {
        Assert.Equal(-1, BandLayout.PivotCurrent(BandLayout.NoSection, 3));
        Assert.Equal(0, BandLayout.PivotCurrent(0, 3));
        Assert.Equal(2, BandLayout.PivotCurrent(2, 3));
        Assert.Equal(2, BandLayout.PivotCurrent(7, 3));
        Assert.Equal(-1, BandLayout.PivotCurrent(0, 0));
    }

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

    // ── ONE BASELINE (A2) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title, the tab and the action share ONE 20-DIP line centred in the same 32-DIP slot, so their text tops
    /// are the same y in the 56 band (18), and the underline is the slot's bottom 2 DIP.</summary>
    [Fact]
    public void TitleTabAndAction_ShareOneBaseline()
    {
        Assert.Equal(20f, BandLayout.TextLine);
        Assert.Equal(32f, BandLayout.ItemHeight);
        Assert.Equal(6f, BandLayout.TextTopIn(BandLayout.ItemHeight));
        Assert.Equal(18f, (BandLayout.Height - BandLayout.ItemHeight) / 2f + BandLayout.TextTopIn(BandLayout.ItemHeight));
        Assert.Equal(BandLayout.TextTopIn(BandLayout.Height), (BandLayout.Height - BandLayout.ItemHeight) / 2f + BandLayout.TextTopIn(BandLayout.ItemHeight));
    }

    [Fact]
    public void TheUnderline_SitsAtOneFixedYOnTheSlotsBottomEdge()
    {
        Assert.Equal(30f, BandLayout.UnderlineY);
        Assert.Equal(BandLayout.ItemHeight, BandLayout.UnderlineY + BandLayout.UnderlineHeight);
        Assert.Equal(BandLayout.UnderlineGap, BandLayout.UnderlineY - (BandLayout.TextTopIn(BandLayout.ItemHeight) + BandLayout.TextLine));
        Assert.Equal(BandLayout.ClusterGap * 0.5f, BandLayout.DividerGap);
        Assert.Equal(16f, BandLayout.DividerH);
    }

    /// <summary>The Zune band's row 2 and the in-page band have IDENTICAL text and underline geometry.</summary>
    [Fact]
    public void ZuneRow2_SharesTheBandsItemHeight()
        => Assert.Equal(BandLayout.ItemHeight, ZuneNavRules.SubRowHeight);

    // ── THE FLOOR (A2) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStuckHeight_IsTheBandInThePageAndZeroInRow2()
    {
        Assert.Equal(56f, BandLayout.StuckHeight(false));
        Assert.Equal(0f, BandLayout.StuckHeight(true));
        Assert.Equal(BandLayout.StuckHeight(false), BandLayout.ClipInsetFor(false));
        Assert.Equal(BandLayout.StuckHeight(true), BandLayout.ClipInsetFor(true));
    }

    [Fact]
    public void OnlyZune_WantsTheBandInRow2()
    {
        Assert.True(BandLayout.InRow2(ShellNavStyle.Zune));
        Assert.False(BandLayout.InRow2(ShellNavStyle.Classic));
        Assert.False(BandLayout.InRow2(ShellNavStyle.Library));
    }

    [Fact]
    public void TheFlipLine_IsWhereTheEarliestFloorDependentChannelStarts()
    {
        // H - 56 - 96: the expanded copy's fade at floor 56 starts before the band's reveal (H - 56 - 44).
        Assert.Equal(248f, BandLayout.FlipLine(400f));
        Assert.Equal(0f, BandLayout.FlipLine(30f));
        Assert.Equal(0f, BandLayout.FlipLine(120f));
    }

    /// <summary>Under the line the wanted placement is adopted; at or above it the latched one holds, so a nav-style switch while
    /// scrolled never snaps a stuck element.</summary>
    [Fact]
    public void TheFloorLatch_FlipsOnlyUnderTheLine()
    {
        Assert.True(BandLayout.FloorLatch(false, true, 100.0, 400f));
        Assert.False(BandLayout.FloorLatch(true, false, 100.0, 400f));
        Assert.False(BandLayout.FloorLatch(false, true, 248.0, 400f));
        Assert.False(BandLayout.FloorLatch(false, true, 900.0, 400f));
        Assert.True(BandLayout.FloorLatch(true, false, 248.0, 400f));
        Assert.True(BandLayout.FloorLatch(true, false, 900.0, 400f));
        // Nothing wanted differently: nothing changes anywhere.
        Assert.True(BandLayout.FloorLatch(true, true, 900.0, 400f));
        Assert.False(BandLayout.FloorLatch(false, false, 0.0, 400f));
    }

    static float Ramp(double offset, float from, float to)
        => to <= from ? (offset >= to ? 1f : 0f) : (float)Math.Clamp((offset - from) / (to - from), 0.0, 1.0);

    /// <summary>THE PROOF's arithmetic: under the flip line the presented height, the parallax, the expanded copy's fade and the
    /// band's reveal are the same whether the hero collapses to the 56 floor or to 0, and a Sticky(floor) element right below the
    /// hero is engaged in neither pairing.</summary>
    [Theory]
    [InlineData(400f)]
    [InlineData(300f)]
    [InlineData(520f)]
    public void UnderTheFlipLine_BothFloorsPresentTheSameChannels(float h)
    {
        float c56 = ArtistHeroLayout.CollapseDistance(h, 56f), c0 = ArtistHeroLayout.CollapseDistance(h, 0f);
        for (float offset = 0f; offset < BandLayout.FlipLine(h); offset += 4f)
        {
            float at56 = h - offset * (h - 56f) / c56;
            float at0 = h - offset * h / c0;
            Assert.Equal(at56, at0, 0.001f);
            // The parallax slope is -1 in both (0..collapse maps to 0..-collapse).
            Assert.Equal(offset * -c56 / c56, offset * -c0 / c0, 0.001f);
            // The expanded copy's fade: still fully opaque at both floors.
            Assert.Equal(Ramp(offset, ArtistHeroLayout.ExpandedFadeStart(c56), c56), Ramp(offset, ArtistHeroLayout.ExpandedFadeStart(c0), c0));
            Assert.Equal(0f, Ramp(offset, ArtistHeroLayout.ExpandedFadeStart(c56), c56));
            // The band's reveal at floor 56: not started, so the band composed there and the band absent at floor 0 look alike.
            Assert.Equal(0f, Ramp(offset, ArtistHeroLayout.CompactRevealStart(c56), c56));
            // The element right below the hero sits at h - offset, which is above BOTH floors: engaged in neither.
            Assert.True(h - offset > 56f, $"engaged at offset {offset}");
        }
    }

    /// <summary>The line is tight: the first offset at or past it already differs between the floors (the fade has begun at 56).</summary>
    [Fact]
    public void AtTheFlipLine_TheFloorsStartToDiffer()
    {
        const float h = 400f;
        float c56 = ArtistHeroLayout.CollapseDistance(h, 56f), c0 = ArtistHeroLayout.CollapseDistance(h, 0f);
        double o = BandLayout.FlipLine(h) + 20.0;
        Assert.NotEqual(Ramp(o, ArtistHeroLayout.ExpandedFadeStart(c56), c56), Ramp(o, ArtistHeroLayout.ExpandedFadeStart(c0), c0));
    }

    /// <summary>With the band in row 2 (band height 0) the spy rule is unchanged: the section whose top is above the quarter line.</summary>
    [Fact]
    public void TheSpy_WithNoStuckBand_AnswersTheSectionAboveTheQuarterLine()
    {
        float line = BandLayout.SpyLine(0f, 800f);
        Assert.Equal(208f, line);
        Assert.Equal(1, BandLayout.ActiveSection([-400f, line, 900f], 0f, 800f, atScrollEnd: false));
        Assert.Equal(0, BandLayout.ActiveSection([-400f, line + 1f, 900f], 0f, 800f, atScrollEnd: false));
    }
}
