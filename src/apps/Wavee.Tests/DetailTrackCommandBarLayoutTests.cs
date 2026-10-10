// ── Wavee.Tests/DetailTrackCommandBarLayoutTests.cs — the table command bar's priority fit ──────────────────────────
//
// Wave 4.5's gate for `Track.CommandBarLayout` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// DetailTrackCommandBarLayoutTests (101 lines): DetailTrackCommandBarLayout → Track.CommandBarLayout,
// DetailTrackCommandWidths → Track.CommandWidths, DetailTrackInlineCommand → Track.InlineCommand. The command bar
// PROMOTES, it does not shrink (ch 04 §0 item 10): a command that does not fit is evicted into "…", with 16-DIP promotion
// hysteresis in every mode.
//
// Pure: no scope, no engine.

using Xunit;
using Layout = Wavee.Track.CommandBarLayout;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly Track.CommandWidths Widths = new(120, 92, 96, 156, 144, 82);

    [Fact]
    public void WidePlaylist_LeavesEveryOptionalCommandInline_WithoutAutoExpandingSearch()
    {
        var fit = Layout.Resolve(1000, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(Track.InlineCommand.Shuffle));
        Assert.True(fit.Has(Track.InlineCommand.Sort));
        Assert.True(fit.Has(Track.InlineCommand.Density));
        Assert.True(fit.Has(Track.InlineCommand.Select));
    }

    [Fact]
    public void NarrowAlbum_PreservesRequiredTargetsAndOverflowsOptionalCommands()
    {
        var fit = Layout.Resolve(150, Widths, vertical: false, hasTune: false, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Track.InlineCommand.None, fit.Inline);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = Layout.Resolve(240, Widths, vertical: true, hasTune: false, hasSelect: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= Layout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        var rich = Layout.Resolve(800, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        var near = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false, rich);

        var fresh = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.True(near.Richness <= fresh.Richness);
    }

    /// <summary>The regression pin for the search box "jumping around" as it expands. Hysteresis used to be skipped
    /// whenever <c>explicitSearch</c> was set — precisely when evicted commands re-measure mid-animation and feed back in
    /// as slightly different widths. Jittering measurements must converge on ONE answer.</summary>
    [Fact]
    public void ExplicitSearch_DoesNotOscillate_WhenMeasuredWidthsJitter()
    {
        const float Available = 700f;
        var fit = Layout.Resolve(Available, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);

        // Sub-pixel measurement noise of the kind a bounds callback actually publishes mid-animation.
        float[] jitter = [0f, 0.4f, -0.6f, 0.9f, -0.3f, 0.7f, -0.8f, 0.2f];
        for (int i = 0; i < jitter.Length; i++)
        {
            float j = jitter[i];
            var noisy = new Track.CommandWidths(
                Widths.Play + j, Widths.Tune - j, Widths.Shuffle + j,
                Widths.Sort - j, Widths.Density + j, Widths.Select - j);
            var next = Layout.Resolve(Available, noisy, vertical: false, hasTune: true, hasSelect: true,
                                      explicitSearch: true, fit);

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
        var wide = Layout.Resolve(900, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);
        var narrow = Layout.Resolve(360, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true, wide);

        Assert.True(narrow.Richness < wide.Richness);
        Assert.True(narrow.SearchExpanded);
    }

    // ── the Filter command's ladder: Filter + Sort labelled, Filter loses its word, Sort into "…", Filter into "…" ──

    [Fact]
    public void Filter_LeadsTheViewCommands_AndKeepsItsWordOnAWideBar()
    {
        var fit = Layout.Resolve(1000, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.True(fit.Has(Track.InlineCommand.Filter));
        Assert.True(fit.Has(Track.InlineCommand.FilterLabel));
    }

    /// <summary>Vertical arm with Tune: mandatory 32 + 92 + 2 = 126, search 8 + 32, the group separator 17: the rungs cost
    /// 34 (funnel), +158 (Sort), +68 (the word and its 22 DIP badge slot instead of the funnel).</summary>
    [Theory]
    [InlineData(443f, true, true, true)]
    [InlineData(442f, true, false, true)]
    [InlineData(375f, true, false, true)]
    [InlineData(374f, true, false, false)]
    [InlineData(217f, true, false, false)]
    [InlineData(216f, false, false, false)]
    public void Filter_RungsFitAtTheirExactBudgets(float available, bool filter, bool label, bool sort)
    {
        var fit = Layout.Resolve(available, Widths, vertical: true, hasTune: true, hasSelect: false, explicitSearch: false);
        Assert.Equal(filter, fit.Has(Track.InlineCommand.Filter));
        Assert.Equal(label, fit.Has(Track.InlineCommand.FilterLabel));
        Assert.Equal(sort, fit.Has(Track.InlineCommand.Sort));
    }

    /// <summary>Sweeping the pane down never keeps a poorer-ranked command while a richer one is gone, and richness only falls.</summary>
    [Fact]
    public void Filter_LadderIsMonotoneAsTheBarNarrows()
    {
        int last = int.MaxValue;
        for (float w = 1000f; w >= 0f; w -= 1f)
        {
            var fit = Layout.Resolve(w, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
            if (fit.Has(Track.InlineCommand.FilterLabel)) Assert.True(fit.Has(Track.InlineCommand.Filter));
            if (fit.Has(Track.InlineCommand.Sort)) Assert.True(fit.Has(Track.InlineCommand.Filter));
            Assert.True(fit.Richness <= last, $"richness rose at {w}");
            last = fit.Richness;
        }
    }

    /// <summary>A two-digit count ("12": 14 text + 4 + 4 margin) must fit the labelled button's reserved slot, so the button never
    /// changes width as the count grows; the nominal width already includes that slot.</summary>
    [Fact]
    public void TheLabelledFilterReservesRoomForATwoDigitBadge()
    {
        Assert.True(Track.CommandBarLayout.FilterBadgeSlot >= 22f);
        Assert.Equal(96f + (Track.CommandBarLayout.FilterBadgeSlot - 18f), Track.CommandBarLayout.FilterLabelledNominal);
    }

    [Fact]
    public void TheBadgeCount_CountsASearchScopeOnlyWhileSearching()
    {
        var f = Track.FilterState.Default with { SearchScope = Track.SearchScope.Title };
        Assert.Equal(0, f.ActiveCountFor(searching: false));
        Assert.Equal(1, f.ActiveCountFor(searching: true));
        Assert.Equal(1, f.ActiveCount);
    }
}
