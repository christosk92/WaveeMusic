// ── Wavee.Tests/DetailTrackCommandBarLayoutTests.cs — the table command bar's fit ───────────────────────────────────
//
// The gate for `Track.CommandBarLayout` (Entities/Track.Rules.cs). The bar holds only LIST TOOLS: [Tune] ─ [Filter] · [Sort] ·
// search · [Insights] · ⋯ (Filter and Sort lead the right cluster; the search affordance is ONE 32-DIP button). Tune, the
// Insights slot and "…" are mandatory; Filter and Sort are placed by width on one ladder - Filter labelled + Sort, Filter loses
// its word, Sort moves into "…", Filter moves into "…". The bar PROMOTES, it does not shrink, with 16-DIP promotion hysteresis in
// every mode, and is never wider than its input.
//
// Pure: no scope, no engine.

using Xunit;
using Layout = Wavee.Track.CommandBarLayout;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly Track.CommandWidths Widths = new(92, 156, 92);

    // Mandatory floor with Tune + Insights: ⋯ 32 + Tune 92 + Insights 32 + 2 gaps of 2, then SearchGap 8 + the 32 search button.
    // The icon Filter then needs Gap 2 + 32 more, and Sort Gap 2 + 156 on top of that.
    const float FilterBudget = 32f + 92f + 32f + 4f + 8f + 32f + 2f + 32f;   // 234
    const float SortBudget = FilterBudget + 2f + 156f;                       // 392
    const float LabelBudget = SortBudget + (92f - 32f);                      // 452

    static float Used(Track.CommandBarFit fit, bool hasTune, bool hasInsights)
    {
        float mandatory = Layout.MoreWidth + (hasTune ? Widths.Tune : 0f) + (hasInsights ? Layout.InsightsWidth : 0f);
        int count = 1 + (hasTune ? 1 : 0) + (hasInsights ? 1 : 0);
        float used = mandatory + (count - 1) * Layout.Gap + Layout.SearchGap
            + (fit.SearchExpanded ? Layout.SearchMinExplicit : Layout.SearchIconWidth);
        if (fit.Has(Track.InlineCommand.Filter)) used += Layout.Gap + (fit.Has(Track.InlineCommand.FilterLabel) ? Widths.Filter : Layout.FilterIconWidth);
        if (fit.Has(Track.InlineCommand.Sort)) used += Layout.Gap + Widths.Sort;
        return used;
    }

    [Fact]
    public void WidePlaylist_KeepsSortInline_WithoutAutoExpandingSearch()
    {
        var fit = Layout.Resolve(760, Widths, hasTune: true, hasInsights: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(Track.InlineCommand.Sort));
        Assert.True(fit.Has(Track.InlineCommand.Filter));
        Assert.True(fit.Has(Track.InlineCommand.FilterLabel));
    }

    [Fact]
    public void TheSearchAffordance_IsOneButton_TheFilterIsItsOwn()
    {
        Assert.Equal(32f, Layout.SearchIconWidth);
        Assert.Equal(Layout.MoreWidth, Layout.SearchIconWidth);
    }

    /// <summary>Filter and Sort head the right cluster [Filter][Sort][find][Insights][⋯]: with no Tune Sort's budget is exactly the
    /// cluster's five members (Insights 32, ⋯ 32, find 32, the Filter icon 32, Sort 156) behind one gap each, plus the 8-DIP search gap.</summary>
    [Fact]
    public void SortLeadsTheRightCluster_ItsBudgetIsTheClusterSum()
    {
        float cluster = Layout.MoreWidth + Layout.InsightsWidth + Layout.Gap + Layout.SearchGap + Layout.SearchIconWidth
            + Layout.Gap + Layout.FilterIconWidth + Layout.Gap + Widths.Sort;
        Assert.Equal(298f, cluster);
        Assert.True(Layout.Resolve(cluster, Widths, hasTune: false, hasInsights: true, explicitSearch: false).Has(Track.InlineCommand.Sort));
        Assert.False(Layout.Resolve(cluster - 0.5f, Widths, hasTune: false, hasInsights: true, explicitSearch: false).Has(Track.InlineCommand.Sort));
    }

    [Fact]
    public void SortMovesIntoMore_BelowItsBudget_AndIsInlineAtIt()
    {
        var at = Layout.Resolve(SortBudget, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        var below = Layout.Resolve(SortBudget - 0.5f, Widths, hasTune: true, hasInsights: true, explicitSearch: false);

        Assert.True(at.Has(Track.InlineCommand.Sort));
        Assert.False(below.Has(Track.InlineCommand.Sort));
        Assert.Equal(Track.InlineCommand.Filter, below.Inline);   // the icon Filter stays until Sort is gone AND it no longer fits
    }

    /// <summary>The ladder, rung by rung: Filter loses its word BEFORE Sort moves into "…", and Filter moves into "…" only after Sort.</summary>
    [Fact]
    public void FilterLosesItsWord_BeforeSortLeaves_AndLeavesLast()
    {
        Track.InlineCommand At(float available) => Layout.Resolve(available, Widths, hasTune: true, hasInsights: true, explicitSearch: false).Inline;

        Assert.Equal(Track.InlineCommand.Filter | Track.InlineCommand.FilterLabel | Track.InlineCommand.Sort, At(LabelBudget));
        Assert.Equal(Track.InlineCommand.Filter | Track.InlineCommand.Sort, At(LabelBudget - 0.5f));
        Assert.Equal(Track.InlineCommand.Filter | Track.InlineCommand.Sort, At(SortBudget));
        Assert.Equal(Track.InlineCommand.Filter, At(SortBudget - 0.5f));
        Assert.Equal(Track.InlineCommand.Filter, At(FilterBudget));
        Assert.Equal(Track.InlineCommand.None, At(FilterBudget - 0.5f));
    }

    [Fact]
    public void TheFilterLabelIsOnlyEverImpliedByTheFilter()
    {
        for (float available = 0f; available <= 900f; available += 1f)
        {
            var fit = Layout.Resolve(available, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
            if (fit.Has(Track.InlineCommand.FilterLabel)) Assert.True(fit.Has(Track.InlineCommand.Filter), available.ToString());
            if (fit.Has(Track.InlineCommand.Sort)) Assert.True(fit.Has(Track.InlineCommand.Filter), available.ToString());
        }
    }

    [Fact]
    public void NarrowAlbum_PreservesTheMandatoryTargetsAndEvictsSort()
    {
        var fit = Layout.Resolve(150, Widths, hasTune: false, hasInsights: false, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Track.InlineCommand.Filter, fit.Inline);   // the icon Filter fits; Sort does not
        Assert.Equal(Track.InlineCommand.None, Layout.Resolve(100, Widths, hasTune: false, hasInsights: false, explicitSearch: false).Inline);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void InsightsReservesItsSlotAndAGap()
    {
        // An album has no Insights slot, a playlist does: the width that holds Sort without it must not hold it with it.
        float without = 32f + 92f + 2f + 8f + 32f + 2f + 32f + 2f + 156f;   // ⋯ + Tune + 1 gap, search, the Filter icon, Sort
        Assert.True(Layout.Resolve(without, Widths, hasTune: true, hasInsights: false, explicitSearch: false).Has(Track.InlineCommand.Sort));
        Assert.False(Layout.Resolve(without, Widths, hasTune: true, hasInsights: true, explicitSearch: false).Has(Track.InlineCommand.Sort));
        // …and the difference is exactly the toggle's 32 DIP plus the 2-DIP gap it adds.
        Assert.True(Layout.Resolve(without + Layout.InsightsWidth + Layout.Gap, Widths, hasTune: true, hasInsights: true,
                                   explicitSearch: false).Has(Track.InlineCommand.Sort));
    }

    [Theory]
    [InlineData(0f, false, false)]
    [InlineData(150f, true, true)]
    [InlineData(392f, true, true)]
    [InlineData(452f, true, true)]
    [InlineData(500f, false, true)]
    [InlineData(760f, true, true)]
    [InlineData(1400f, true, false)]
    public void TheFit_IsNeverWiderThanAvailable_WhenTheFloorFits(float available, bool hasTune, bool hasInsights)
    {
        foreach (bool search in new[] { false, true })
        {
            var fit = Layout.Resolve(available, Widths, hasTune, hasInsights, search);
            float floor = Used(new Track.CommandBarFit(Track.InlineCommand.None, search, 0f), hasTune, hasInsights);
            if (floor > available) continue;   // the mandatory floor itself does not fit: nothing is evictable below it
            Assert.True(Used(fit, hasTune, hasInsights) <= available + 0.001f, $"{available}/{hasTune}/{hasInsights}/{search}");
            if (search) Assert.InRange(fit.SearchWidth, Layout.SearchMinExplicit, Layout.SearchMax);
        }
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = Layout.Resolve(240, Widths, hasTune: false, hasInsights: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= Layout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        // Sort is evicted just under its budget; coming back from there it needs 16 DIP more than a fresh fit would.
        var evicted = Layout.Resolve(SortBudget - 1f, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        Assert.False(evicted.Has(Track.InlineCommand.Sort));

        var within = Layout.Resolve(SortBudget + Layout.PromotionHysteresis - 1f, Widths, hasTune: true, hasInsights: true,
                                    explicitSearch: false, evicted);
        var past = Layout.Resolve(SortBudget + Layout.PromotionHysteresis, Widths, hasTune: true, hasInsights: true,
                                  explicitSearch: false, evicted);
        var fresh = Layout.Resolve(SortBudget + Layout.PromotionHysteresis - 1f, Widths, hasTune: true, hasInsights: true,
                                   explicitSearch: false);

        Assert.False(within.Has(Track.InlineCommand.Sort));   // held back inside the 16-DIP band
        Assert.True(past.Has(Track.InlineCommand.Sort));
        Assert.True(fresh.Has(Track.InlineCommand.Sort));     // no history, no hysteresis
        Assert.True(within.Richness <= fresh.Richness);
    }

    [Fact]
    public void NarrowingIsImmediate()
    {
        var rich = Layout.Resolve(SortBudget + 40f, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        var narrow = Layout.Resolve(SortBudget - 1f, Widths, hasTune: true, hasInsights: true, explicitSearch: false, rich);
        Assert.True(rich.Has(Track.InlineCommand.Sort));
        Assert.False(narrow.Has(Track.InlineCommand.Sort));
    }

    /// <summary>The regression pin for the search box "jumping around" as it expands. Hysteresis used to be skipped
    /// whenever <c>explicitSearch</c> was set — precisely when evicted commands re-measure mid-animation and feed back in
    /// as slightly different widths. Jittering measurements must converge on ONE answer.</summary>
    [Fact]
    public void ExplicitSearch_DoesNotOscillate_WhenMeasuredWidthsJitter()
    {
        const float Available = 700f;
        var fit = Layout.Resolve(Available, Widths, hasTune: true, hasInsights: true, explicitSearch: true);

        // Sub-pixel measurement noise of the kind a bounds callback actually publishes mid-animation.
        float[] jitter = [0f, 0.4f, -0.6f, 0.9f, -0.3f, 0.7f, -0.8f, 0.2f];
        for (int i = 0; i < jitter.Length; i++)
        {
            float j = jitter[i];
            var noisy = new Track.CommandWidths(Widths.Tune - j, Widths.Sort + j);
            var next = Layout.Resolve(Available, noisy, hasTune: true, hasInsights: true, explicitSearch: true, fit);

            Assert.Equal(fit.Inline, next.Inline);
            Assert.True(next.Richness <= fit.Richness);
            fit = next;
        }
    }

    /// <summary>A genuine pane resize must still re-fit while search is open — the freeze is against measurement
    /// feedback, not against the window actually changing size.</summary>
    [Fact]
    public void ExplicitSearch_StillNarrowsWhenThePaneShrinks()
    {
        var wide = Layout.Resolve(900, Widths, hasTune: true, hasInsights: true, explicitSearch: true);
        var narrow = Layout.Resolve(360, Widths, hasTune: true, hasInsights: true, explicitSearch: true, wide);

        Assert.True(narrow.Richness < wide.Richness);
        Assert.True(narrow.SearchExpanded);
    }

    // ── the sort dropdown's word for the natural order ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DetailKind.Album, "detail.sort.albumOrder")]
    [InlineData(DetailKind.Playlist, "detail.sort.customOrder")]
    [InlineData(DetailKind.Liked, "detail.sort.customOrder")]
    [InlineData(DetailKind.Show, "detail.sort.customOrder")]
    public void TheNaturalOrder_IsAlbumOrderOnAnAlbum_AndCustomOrderElsewhere(DetailKind kind, string key)
        => Assert.Equal(key, Track.TableRules.IndexSortLabelKey(kind));

    // ── the list verbs that have no identity-row home ────────────────────────────────────────────────────────────

    /// <summary>Shuffle · Play next · Add to queue moved off the bar. Album and playlist carry them on the rail's Play split
    /// (and the vertical hero's satellite); Liked and Show have neither, so their "…" keeps them in the two-column arm.</summary>
    [Theory]
    [InlineData(DetailKind.Album, false, false)]
    [InlineData(DetailKind.Playlist, false, false)]
    [InlineData(DetailKind.Liked, false, true)]
    [InlineData(DetailKind.Show, false, true)]
    [InlineData(DetailKind.Liked, true, false)]
    [InlineData(DetailKind.Show, true, false)]
    [InlineData(DetailKind.Album, true, false)]
    public void MoreCarriesTheListVerbs_OnlyWhereTheIdentityRowHasNone(DetailKind kind, bool vertical, bool expected)
        => Assert.Equal(expected, Track.TableRules.MoreCarriesListVerbs(kind, vertical));

    // ── the toolbar's plate lines up with the row plates; the fit does not change ────────────────────────────────

    /// <summary>The left shift moves the command surface, never the box that measures it, so Resolve receives the width it
    /// always did (the measured slot less the 12-DIP inset) at every pane width.</summary>
    [Theory]
    [InlineData(360f)]
    [InlineData(640f)]
    [InlineData(960f)]
    public void ResolverWidth_IsUnchangedByTheToolbarLead(float paneWidth)
    {
        float before = MathF.Max(0f, paneWidth - 12f);   // the pre-change formula
        Assert.Equal(before, Layout.PaneWidth(paneWidth));

        // …and the fit it produces is the same one.
        var was = Layout.Resolve(before, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        var now = Layout.Resolve(Layout.PaneWidth(paneWidth), Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        Assert.Equal(was.Inline, now.Inline);
        Assert.Equal(was.SearchWidth, now.SearchWidth);
    }

    /// <summary>At every tier the first toolbar plate lands on the row plates' edge (RowInset Modern, 0 Classic): the chrome pads
    /// PadXFor(tier), the surface its own 6, and the lead takes the difference.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void ToolbarPlate_SitsOnTheRowPlateEdge(int tier, bool classic)
    {
        float lead = Track.RowMetrics.ToolbarLead(tier, classic);
        float plate = Track.RowMetrics.PadXFor(tier) - lead + Detail.VerticalLayout.ToolbarSurfacePadX;
        Assert.Equal(Track.RowMetrics.PlateX(classic), plate);
        Assert.True(lead > 0f);
    }
}

/// <summary>The Liked Songs bar's ladder (<c>Track.LikedBarLayout</c>): chips shrink, Shuffle loses its label, Filter loses its
/// label, Sort goes into "...", the chips go into "...", Shuffle goes into "...", Insights goes into "...", Filter goes into "...".
/// Filter and Sort ride the right cluster [Filter][Sort][find][Insights][...]. Nominal widths: Shuffle 96, Sort 156, Filter 92.
/// Pure: no scope, no engine.</summary>
public class LikedBarLayoutTests
{
    const float Shuffle = 96f, Sort = 156f, Filter = 92f;
    static readonly Track.LikedBarLayout.Rung[] Rungs = Enum.GetValues<Track.LikedBarLayout.Rung>();

    static float W(Track.LikedBarLayout.Rung r, bool explicitSearch = false, bool chips = true)
        => Track.LikedBarLayout.RungWidth(r, Shuffle, Sort, Filter, explicitSearch, chips);

    static Track.LikedBarFit Fit(float available, bool explicitSearch = false, Track.LikedBarFit? previous = null, bool chips = true,
                                 bool allowPromotion = true)
        => Track.LikedBarLayout.Resolve(available, Shuffle, Sort, Filter, explicitSearch, previous, chips, allowPromotion);

    // A pane where the open find costs rungs: Full fits closed with the hysteresis and 20 DIP to spare, so closing the find promotes.
    static float CollapsePane => W(Track.LikedBarLayout.Rung.Full) + Track.CommandBarLayout.PromotionHysteresis + 20f;

    [Fact]
    public void ACollapseHold_KeepsTheRung_AndTheFreedWidthGoesToTheChipSlot()
    {
        float pane = CollapsePane;
        var open = Fit(pane, explicitSearch: true);
        Assert.True(open.Rung > Track.LikedBarLayout.Rung.Full);
        Assert.True(Track.LikedBarLayout.ChipsInline(open.Rung));

        var held = Fit(pane, previous: open, allowPromotion: false);
        Assert.Equal(open.Rung, held.Rung);
        Assert.False(held.SearchExpanded);
        Assert.Equal(Track.LikedBarLayout.ChipSlotMinW + (pane - W(open.Rung)), held.ChipSlotW, 3);
        Assert.True(held.ChipSlotW > open.ChipSlotW);
    }

    [Fact]
    public void WithoutAHold_TheCollapsePromotes()
    {
        float pane = CollapsePane;
        var open = Fit(pane, explicitSearch: true);
        var closed = Fit(pane, previous: open);
        Assert.True(closed.Rung < open.Rung);
        Assert.Equal(Track.LikedBarLayout.Rung.Full, closed.Rung);
    }

    [Fact]
    public void NarrowingStepsDownAtOnce_WhileHeld()
    {
        float pane = CollapsePane;
        var held = Fit(pane, previous: Fit(pane, explicitSearch: true), allowPromotion: false);
        var narrower = Fit(W(Track.LikedBarLayout.Rung.InsightsInMore) + 1f, previous: held, allowPromotion: false);
        Assert.True(narrower.Rung > held.Rung);
        Assert.True(W(narrower.Rung) <= W(Track.LikedBarLayout.Rung.InsightsInMore) + 1f);
        Assert.Equal(Track.LikedBarLayout.Rung.FilterInMore, Fit(0f, previous: held, allowPromotion: false).Rung);
    }

    [Fact]
    public void ReleasingTheHold_PromotesToTheUnheldRung()
    {
        float pane = CollapsePane;
        var open = Fit(pane, explicitSearch: true);
        var held = Fit(pane, previous: open, allowPromotion: false);
        var released = Fit(pane, previous: held);
        Assert.Equal(Fit(pane, previous: open).Rung, released.Rung);
        Assert.True(released.Rung < held.Rung);
    }

    [Theory]
    [InlineData(true, false, true, true)]     // collapsed under the pointer
    [InlineData(true, false, false, false)]   // collapsed with the pointer elsewhere
    [InlineData(true, true, true, false)]     // still open
    [InlineData(false, false, true, false)]   // was never open
    [InlineData(false, true, true, false)]    // opening
    public void HoldsOnCollapse_TruthTable(bool wasOpen, bool open, bool over, bool expected)
        => Assert.Equal(expected, Track.LikedBarLayout.HoldsOnCollapse(wasOpen, open, over));

    [Theory]
    [InlineData(500f, 500f, false, true, false)]    // nothing changed: the hold stays
    [InlineData(500f, 500.5f, false, true, false)]  // within half a DIP
    [InlineData(500f, 501f, false, true, true)]     // the pane really changed
    [InlineData(500f, 500f, false, false, true)]    // the pointer left
    [InlineData(500f, 500f, true, true, true)]      // the find reopened
    public void HoldReleased_TruthTable(float holdPane, float pane, bool open, bool over, bool expected)
        => Assert.Equal(expected, Track.LikedBarLayout.HoldReleased(holdPane, pane, open, over));

    [Fact]
    public void PointerOver_IsTheBarsHalfOpenRect()
    {
        var bar = new FluentGpu.Foundation.RectF(10f, 20f, 100f, 44f);
        Assert.False(Track.LikedBarLayout.PointerOver(null, bar));
        Assert.True(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(10f, 20f), bar));
        Assert.True(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(60f, 40f), bar));
        Assert.False(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(110f, 40f), bar));   // right edge
        Assert.False(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(60f, 64f), bar));    // bottom edge
        Assert.False(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(9f, 40f), bar));
        Assert.False(Track.LikedBarLayout.PointerOver(new FluentGpu.Foundation.Point2(10f, 20f), new FluentGpu.Foundation.RectF(10f, 20f, 0f, 44f)));
    }

    [Fact]
    public void RungWidths_StrictlyDecrease_FromFullToTheFloor()
    {
        for (int i = 1; i < Rungs.Length; i++)
            Assert.True(W(Rungs[i]) < W(Rungs[i - 1]), Rungs[i].ToString());
    }

    [Fact]
    public void TheFullRung_SumsEveryInlinePiece()
    {
        // Play 121 + more 32 + SearchGap 8 + the 32 search button, then Insights 32, Filter 92, Sort 156, divider 17 and the
        // 120 chip slot (plus its 8-DIP trailing air before Filter), each behind one 2-DIP gap (the divider and the slot are two
        // pieces), one gap between Play and more, and Shuffle 96 behind the 8-DIP Play-Shuffle gap.
        float expected = 121f + 2f + 32f + 8f + 32f + (2f + 32f) + (8f + 96f) + (2f + 92f) + (2f + 156f) + (2f + 17f + 2f + 120f + 8f);
        Assert.Equal(expected, W(Track.LikedBarLayout.Rung.Full));
    }

    [Fact]
    public void ResolveAtARungsWidth_GivesThatRung_AndOnePixelLessGivesAPoorerOne()
    {
        foreach (var r in Rungs)
        {
            Assert.Equal(r, Fit(W(r)).Rung);
            if (r == Track.LikedBarLayout.Rung.FilterInMore) continue;   // the floor: nothing poorer exists
            Assert.True(Fit(W(r) - 1f).Rung > r, r.ToString());
        }
    }

    [Fact]
    public void AboveTheFloor_TheResultIsNeverWiderThanAvailable()
    {
        float floor = W(Track.LikedBarLayout.Rung.FilterInMore);
        Track.LikedBarFit? previous = null;
        for (float available = 1400f; available >= floor; available -= 1f)
        {
            var fit = Fit(available, previous: previous);
            Assert.True(W(fit.Rung) <= available, available.ToString());
            previous = fit;
        }
        previous = null;
        for (float available = floor; available <= 1400f; available += 1f)
        {
            var fit = Fit(available, previous: previous);
            Assert.True(W(fit.Rung) <= available, available.ToString());
            previous = fit;
        }
    }

    [Fact]
    public void BelowTheFloor_TheBarKeepsTheMandatoryFloor()
    {
        var fit = Fit(0f);
        Assert.Equal(Track.LikedBarLayout.Rung.FilterInMore, fit.Rung);
        Assert.False(Track.LikedBarLayout.ChipsInline(fit.Rung));
        Assert.False(Track.LikedBarLayout.ShuffleInline(fit.Rung));
        Assert.False(Track.LikedBarLayout.InsightsInline(fit.Rung));
        Assert.False(Track.LikedBarLayout.FilterInline(fit.Rung));
    }

    [Fact]
    public void APromotionWithinTheHysteresisHoldsThePreviousRung_AndOneBeyondItTakesTheRicherRung()
    {
        var shuffleIcon = Track.LikedBarLayout.Rung.ShuffleIcon;
        var full = Track.LikedBarLayout.Rung.Full;
        var previous = Fit(W(shuffleIcon));
        Assert.Equal(shuffleIcon, previous.Rung);

        Assert.Equal(shuffleIcon, Fit(W(full) + Track.CommandBarLayout.PromotionHysteresis - 1f, previous: previous).Rung);
        Assert.Equal(full, Fit(W(full) + Track.CommandBarLayout.PromotionHysteresis, previous: previous).Rung);
        // Narrowing is immediate.
        Assert.Equal(Track.LikedBarLayout.Rung.FilterIcon, Fit(W(shuffleIcon) - 1f, previous: Fit(W(full))).Rung);
    }

    [Fact]
    public void SortPromotesBackIntoTheRightCluster_OnlyWithTheHysteresisToSpare()
    {
        var filterIcon = Track.LikedBarLayout.Rung.FilterIcon;
        var sortInMore = Track.LikedBarLayout.Rung.SortInMore;
        // The step between the two rungs is exactly Sort and its gap.
        Assert.Equal(Track.CommandBarLayout.Gap + Sort, W(filterIcon) - W(sortInMore));

        var evicted = Fit(W(sortInMore));
        Assert.Equal(sortInMore, evicted.Rung);
        Assert.Equal(sortInMore, Fit(W(filterIcon) + Track.CommandBarLayout.PromotionHysteresis - 1f, previous: evicted).Rung);
        Assert.Equal(filterIcon, Fit(W(filterIcon) + Track.CommandBarLayout.PromotionHysteresis, previous: evicted).Rung);
        Assert.Equal(filterIcon, Fit(W(filterIcon) + Track.CommandBarLayout.PromotionHysteresis - 1f).Rung);   // no history, no hysteresis
    }

    /// <summary>The Filter's three steps: it loses its word (to the 32-DIP icon + badge) BEFORE Sort moves into "...", and it moves
    /// into "..." only after Insights has.</summary>
    [Fact]
    public void FilterLosesItsWordBeforeSortLeaves_AndLeavesAfterInsights()
    {
        Assert.True(Track.LikedBarLayout.FilterLabelled(Track.LikedBarLayout.Rung.Full));
        Assert.True(Track.LikedBarLayout.FilterLabelled(Track.LikedBarLayout.Rung.ShuffleIcon));
        Assert.False(Track.LikedBarLayout.FilterLabelled(Track.LikedBarLayout.Rung.FilterIcon));
        Assert.True(Track.LikedBarLayout.FilterInline(Track.LikedBarLayout.Rung.FilterIcon));
        Assert.True(Track.LikedBarLayout.SortInline(Track.LikedBarLayout.Rung.FilterIcon));      // the word went, Sort stayed
        Assert.False(Track.LikedBarLayout.SortInline(Track.LikedBarLayout.Rung.SortInMore));
        Assert.True(Track.LikedBarLayout.FilterInline(Track.LikedBarLayout.Rung.SortInMore));    // Filter outlives Sort
        Assert.True(Track.LikedBarLayout.FilterInline(Track.LikedBarLayout.Rung.InsightsInMore));
        Assert.False(Track.LikedBarLayout.InsightsInline(Track.LikedBarLayout.Rung.InsightsInMore));
        Assert.False(Track.LikedBarLayout.FilterInline(Track.LikedBarLayout.Rung.FilterInMore)); // ...and goes last

        // Each step's saving: the word, then Sort and its gap, then (last) the icon and its gap.
        Assert.Equal(Filter - Track.LikedBarLayout.FilterIconW, W(Track.LikedBarLayout.Rung.ShuffleIcon) - W(Track.LikedBarLayout.Rung.FilterIcon));
        Assert.Equal(Track.CommandBarLayout.Gap + Track.LikedBarLayout.FilterIconW,
                     W(Track.LikedBarLayout.Rung.InsightsInMore) - W(Track.LikedBarLayout.Rung.FilterInMore));
    }

    [Fact]
    public void ExplicitSearch_MovesTheBarDownARung_ExactlyWhenTheExtraWidthDoesNotFit()
    {
        foreach (var r in Rungs)
        {
            float available = W(r);   // the rung fits with the search pair, exactly
            var open = Fit(available, explicitSearch: true);
            var expected = Rungs.FirstOrDefault(k => W(k, explicitSearch: true) <= available, Track.LikedBarLayout.Rung.FilterInMore);
            Assert.Equal(expected, open.Rung);
            if (r != Track.LikedBarLayout.Rung.FilterInMore)   // the floor holds whatever the search asks for
                Assert.NotEqual(r, open.Rung);
            Assert.True(open.SearchExpanded);
        }
    }

    [Fact]
    public void TheChipSlot_TakesTheSlack_AndNeverGoesUnderItsMinimumWhileInline()
    {
        var full = Fit(W(Track.LikedBarLayout.Rung.Full) + 50f);
        Assert.Equal(Track.LikedBarLayout.Rung.Full, full.Rung);
        Assert.Equal(Track.LikedBarLayout.ChipSlotMinW + 50f, full.ChipSlotW, 3);
        foreach (var r in Rungs)
        {
            var fit = Fit(W(r));
            if (Track.LikedBarLayout.ChipsInline(fit.Rung)) Assert.True(fit.ChipSlotW >= Track.LikedBarLayout.ChipSlotMinW);
            else Assert.Equal(0f, fit.ChipSlotW);
        }
    }

    [Fact]
    public void WithoutAChipSet_NoRungReservesTheDividerOrTheSlot()
    {
        Assert.Equal(W(Track.LikedBarLayout.Rung.ChipsInMore), W(Track.LikedBarLayout.Rung.SortInMore, chips: false));
        var fit = Fit(W(Track.LikedBarLayout.Rung.ShuffleIcon, chips: false), chips: false);
        Assert.True(fit.Rung <= Track.LikedBarLayout.Rung.ShuffleIcon);
        Assert.Equal(0f, fit.ChipSlotW);
    }

    [Fact]
    public void TheChipSlot_PaysItsTrailingAirOnEveryRungThatShowsIt_AndNoOtherRung()
    {
        Assert.Equal(8f, Track.LikedBarLayout.ChipRailTrailGap);
        foreach (var r in Rungs)
        {
            float with = W(r), without = W(r, chips: false);
            Assert.Equal(Track.LikedBarLayout.ChipsInline(r)
                ? Track.CommandBarLayout.Gap + Track.LikedBarLayout.DividerW + Track.CommandBarLayout.Gap
                  + Track.LikedBarLayout.ChipSlotMinW + Track.LikedBarLayout.ChipRailTrailGap
                : 0f, with - without);
        }
    }

    [Fact]
    public void ThePlaySplitFindAndMoreAreInEveryRungsWidth()
    {
        float floor = W(Track.LikedBarLayout.Rung.FilterInMore);
        Assert.Equal(Track.LikedBarLayout.PlayW + Track.CommandBarLayout.Gap + Track.CommandBarLayout.MoreWidth
                     + Track.CommandBarLayout.SearchGap + Track.CommandBarLayout.SearchIconWidth, floor);
    }
}

/// <summary>The Sort command is lit only away from the profile's own default sort (Liked opens on Date added, descending).</summary>
public class SortIsActiveTests
{
    static readonly Track.SortSpec LikedDefault = new(Track.SortColumn.DateAdded, true);

    [Fact]
    public void TheProfileDefault_IsNeverLit()
    {
        Assert.False(Track.TableRules.SortIsActive(LikedDefault, LikedDefault));
        Assert.False(Track.TableRules.SortIsActive(Track.SortSpec.Default, Track.SortSpec.Default));
    }

    [Fact]
    public void Liked_IsLitAnywayAwayFromDateAddedDescending()
    {
        Assert.False(Track.TableRules.SortIsActive(new(Track.SortColumn.DateAdded, true), LikedDefault));
        Assert.True(Track.TableRules.SortIsActive(new(Track.SortColumn.DateAdded, false), LikedDefault));   // Date added ascending is away
        Assert.True(Track.TableRules.SortIsActive(new(Track.SortColumn.Title, false), LikedDefault));
        Assert.True(Track.TableRules.SortIsActive(Track.SortSpec.Default, LikedDefault));                   // the custom order is "away" on Liked
    }

    [Fact]
    public void OtherLists_AreLitAwayFromTheNaturalOrder()
    {
        var def = Track.SortSpec.Default;
        Assert.False(Track.TableRules.SortIsActive(def, def));
        Assert.True(Track.TableRules.SortIsActive(new(Track.SortColumn.Index, true), def));
        Assert.True(Track.TableRules.SortIsActive(new(Track.SortColumn.Duration, false), def));
    }
}
