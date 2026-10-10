// ── Wavee.Tests/DetailTrackCommandBarLayoutTests.cs — the table command bar's fit ───────────────────────────────────
//
// The gate for `Track.CommandBarLayout` (Entities/Track.Rules.cs). The bar holds only LIST TOOLS: [Sort] · [Tune] ─ search ·
// [Insights] · ⋯. Tune, the Insights slot and "…" are mandatory; Sort is the one command placed by width — inline while it
// fits after the search affordance's reservation, otherwise evicted into "…". The bar PROMOTES, it does not shrink, with
// 16-DIP promotion hysteresis in every mode, and is never wider than its input.
//
// Pure: no scope, no engine.

using Xunit;
using Layout = Wavee.Track.CommandBarLayout;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly Track.CommandWidths Widths = new(92, 156);

    // Mandatory floor with Tune + Insights: ⋯ 32 + Tune 92 + Insights 32 + 2 gaps of 2, then SearchGap 8 + the 66 icon pair.
    // Sort then needs Gap 2 + 156 more.
    const float SortBudget = 32f + 92f + 32f + 4f + 8f + 66f + 2f + 156f;   // 392

    static float Used(Track.CommandBarFit fit, bool hasTune, bool hasInsights)
    {
        float mandatory = Layout.MoreWidth + (hasTune ? Widths.Tune : 0f) + (hasInsights ? Layout.InsightsWidth : 0f);
        int count = 1 + (hasTune ? 1 : 0) + (hasInsights ? 1 : 0);
        float used = mandatory + (count - 1) * Layout.Gap + Layout.SearchGap
            + (fit.SearchExpanded ? Layout.SearchMinExplicit : Layout.SearchIconWidth);
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
    }

    [Fact]
    public void SortMovesIntoMore_BelowItsBudget_AndIsInlineAtIt()
    {
        var at = Layout.Resolve(SortBudget, Widths, hasTune: true, hasInsights: true, explicitSearch: false);
        var below = Layout.Resolve(SortBudget - 0.5f, Widths, hasTune: true, hasInsights: true, explicitSearch: false);

        Assert.True(at.Has(Track.InlineCommand.Sort));
        Assert.False(below.Has(Track.InlineCommand.Sort));
        Assert.Equal(Track.InlineCommand.None, below.Inline);
    }

    [Fact]
    public void NarrowAlbum_PreservesTheMandatoryTargetsAndEvictsSort()
    {
        var fit = Layout.Resolve(150, Widths, hasTune: false, hasInsights: false, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Track.InlineCommand.None, fit.Inline);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void InsightsReservesItsSlotAndAGap()
    {
        // An album has no Insights slot, a playlist does: the width that holds Sort without it must not hold it with it.
        float without = 32f + 92f + 2f + 8f + 66f + 2f + 156f;   // ⋯ + Tune + 1 gap, search, Sort
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

/// <summary>The Liked Songs bar's ladder (<c>Track.LikedBarLayout</c>): chips shrink, Shuffle loses its label, Sort goes into
/// "...", the chips go into "...", Shuffle goes into "...", Insights goes into "...". Nominal widths: Shuffle 96, Sort 156.
/// Pure: no scope, no engine.</summary>
public class LikedBarLayoutTests
{
    const float Shuffle = 96f, Sort = 156f;
    static readonly Track.LikedBarLayout.Rung[] Rungs = Enum.GetValues<Track.LikedBarLayout.Rung>();

    static float W(Track.LikedBarLayout.Rung r, bool explicitSearch = false, bool chips = true)
        => Track.LikedBarLayout.RungWidth(r, Shuffle, Sort, explicitSearch, chips);

    static Track.LikedBarFit Fit(float available, bool explicitSearch = false, Track.LikedBarFit? previous = null, bool chips = true)
        => Track.LikedBarLayout.Resolve(available, Shuffle, Sort, explicitSearch, previous, chips);

    [Fact]
    public void RungWidths_StrictlyDecrease_FromFullToTheFloor()
    {
        for (int i = 1; i < Rungs.Length; i++)
            Assert.True(W(Rungs[i]) < W(Rungs[i - 1]), Rungs[i].ToString());
    }

    [Fact]
    public void TheFullRung_SumsEveryInlinePiece()
    {
        // Play 121 + more 32 + SearchGap 8 + search pair 66, then Insights 32, Shuffle 96, Sort 156, divider 17 and the 120 chip
        // slot, each behind one 2-DIP gap (the divider and the slot are two pieces), and one gap between Play and more.
        float expected = 121f + 2f + 32f + 8f + 66f + (2f + 32f) + (2f + 96f) + (2f + 156f) + (2f + 17f + 2f + 120f);
        Assert.Equal(expected, W(Track.LikedBarLayout.Rung.Full));
    }

    [Fact]
    public void ResolveAtARungsWidth_GivesThatRung_AndOnePixelLessGivesAPoorerOne()
    {
        foreach (var r in Rungs)
        {
            Assert.Equal(r, Fit(W(r)).Rung);
            if (r == Track.LikedBarLayout.Rung.InsightsInMore) continue;   // the floor: nothing poorer exists
            Assert.True(Fit(W(r) - 1f).Rung > r, r.ToString());
        }
    }

    [Fact]
    public void AboveTheFloor_TheResultIsNeverWiderThanAvailable()
    {
        float floor = W(Track.LikedBarLayout.Rung.InsightsInMore);
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
        Assert.Equal(Track.LikedBarLayout.Rung.InsightsInMore, fit.Rung);
        Assert.False(Track.LikedBarLayout.ChipsInline(fit.Rung));
        Assert.False(Track.LikedBarLayout.ShuffleInline(fit.Rung));
        Assert.False(Track.LikedBarLayout.InsightsInline(fit.Rung));
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
        Assert.Equal(Track.LikedBarLayout.Rung.SortInMore, Fit(W(shuffleIcon) - 1f, previous: Fit(W(full))).Rung);
    }

    [Fact]
    public void ExplicitSearch_MovesTheBarDownARung_ExactlyWhenTheExtraWidthDoesNotFit()
    {
        foreach (var r in Rungs)
        {
            float available = W(r);   // the rung fits with the search pair, exactly
            var open = Fit(available, explicitSearch: true);
            var expected = Rungs.FirstOrDefault(k => W(k, explicitSearch: true) <= available, Track.LikedBarLayout.Rung.InsightsInMore);
            Assert.Equal(expected, open.Rung);
            if (r != Track.LikedBarLayout.Rung.InsightsInMore)   // the floor holds whatever the search asks for
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
    public void ThePlaySplitFindAndMoreAreInEveryRungsWidth()
    {
        float floor = W(Track.LikedBarLayout.Rung.InsightsInMore);
        Assert.Equal(Track.LikedBarLayout.PlayW + Track.CommandBarLayout.Gap + Track.CommandBarLayout.MoreWidth
                     + Track.CommandBarLayout.SearchGap + Track.CommandBarLayout.SearchIconWidth, floor);
    }
}
