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
    static readonly Track.CommandWidths Widths = new(120, 92, 96, 156);

    [Fact]
    public void WidePlaylist_LeavesEveryOptionalCommandInline_WithoutAutoExpandingSearch()
    {
        var fit = Layout.Resolve(1000, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(Track.InlineCommand.Shuffle));
        Assert.True(fit.Has(Track.InlineCommand.Sort));
        Assert.True(fit.Has(Track.InlineCommand.Filter));
        Assert.True(fit.Has(Track.InlineCommand.FilterLabel));
    }

    /// <summary>Tuned Classic D3 (D-6 / D-10): Row size and Select are never bar commands — they live in "…" — so the fit has no
    /// rung for them at any width.</summary>
    [Fact]
    public void RowSizeAndSelect_AreNeverOnTheBar()
        => Assert.Equal(Track.InlineCommand.Shuffle | Track.InlineCommand.Sort | Track.InlineCommand.Filter | Track.InlineCommand.FilterLabel,
                        Layout.Resolve(5000, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false).Inline);

    /// <summary>The Insights button is a fixed 32 + gap in the right group: a facts page's bar evicts exactly that much sooner.</summary>
    [Fact]
    public void TheInsightsButton_CostsItsOwnWidth()
    {
        float edge = 0f;
        for (float w = 1000f; w > 0f; w -= 1f)
            if (!Layout.Resolve(w, Widths, vertical: false, hasTune: false, hasInsights: false, explicitSearch: false).Has(Track.InlineCommand.FilterLabel)) { edge = w + 1f; break; }
        Assert.True(Layout.Resolve(edge, Widths, vertical: false, hasTune: false, hasInsights: false, explicitSearch: false).Has(Track.InlineCommand.FilterLabel));
        Assert.False(Layout.Resolve(edge + Layout.InsightsWidth + Layout.Gap - 1f, Widths, vertical: false, hasTune: false, hasInsights: true, explicitSearch: false).Has(Track.InlineCommand.FilterLabel));
        Assert.True(Layout.Resolve(edge + Layout.InsightsWidth + Layout.Gap, Widths, vertical: false, hasTune: false, hasInsights: true, explicitSearch: false).Has(Track.InlineCommand.FilterLabel));
    }

    [Fact]
    public void NarrowAlbum_PreservesRequiredTargetsAndOverflowsOptionalCommands()
    {
        var fit = Layout.Resolve(150, Widths, vertical: false, hasTune: false, hasInsights: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Track.InlineCommand.None, fit.Inline);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = Layout.Resolve(240, Widths, vertical: true, hasTune: false, hasInsights: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= Layout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        var rich = Layout.Resolve(800, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false);
        var near = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false, rich);

        var fresh = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false);
        Assert.True(near.Richness <= fresh.Richness);
    }

    /// <summary>The regression pin for the search box "jumping around" as it expands. Hysteresis used to be skipped
    /// whenever <c>explicitSearch</c> was set — precisely when evicted commands re-measure mid-animation and feed back in
    /// as slightly different widths. Jittering measurements must converge on ONE answer.</summary>
    [Fact]
    public void ExplicitSearch_DoesNotOscillate_WhenMeasuredWidthsJitter()
    {
        const float Available = 700f;
        var fit = Layout.Resolve(Available, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: true);

        // Sub-pixel measurement noise of the kind a bounds callback actually publishes mid-animation.
        float[] jitter = [0f, 0.4f, -0.6f, 0.9f, -0.3f, 0.7f, -0.8f, 0.2f];
        for (int i = 0; i < jitter.Length; i++)
        {
            float j = jitter[i];
            var noisy = new Track.CommandWidths(
                Widths.Play + j, Widths.Tune - j, Widths.Shuffle + j, Widths.Sort - j, Widths.Filter + j);
            var next = Layout.Resolve(Available, noisy, vertical: false, hasTune: true, hasInsights: true,
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
        var wide = Layout.Resolve(900, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: true);
        var narrow = Layout.Resolve(360, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: true, wide);

        Assert.True(narrow.Richness < wide.Richness);
        Assert.True(narrow.SearchExpanded);
    }

    // ── the Filter command's ladder: Filter + Sort labelled, Filter loses its word, Sort into "…", Filter into "…" ──

    [Fact]
    public void Filter_LeadsTheViewCommands_AndKeepsItsWordOnAWideBar()
    {
        var fit = Layout.Resolve(1000, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false);
        Assert.True(fit.Has(Track.InlineCommand.Filter));
        Assert.True(fit.Has(Track.InlineCommand.FilterLabel));
    }

    /// <summary>Vertical arm with Tune, no Insights: Find 8 + 32, "…" 2 + 32, Tune 92, the least group gap 8 = 174; the rungs cost
    /// 34 (funnel), +158 (Sort), +68 (the word and its 22 DIP badge slot instead of the funnel).</summary>
    [Theory]
    [InlineData(434f, true, true, true)]
    [InlineData(433f, true, false, true)]
    [InlineData(366f, true, false, true)]
    [InlineData(365f, true, false, false)]
    [InlineData(208f, true, false, false)]
    [InlineData(207f, false, false, false)]
    public void Filter_RungsFitAtTheirExactBudgets(float available, bool filter, bool label, bool sort)
    {
        var fit = Layout.Resolve(available, Widths, vertical: true, hasTune: true, hasInsights: false, explicitSearch: false);
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
            var fit = Layout.Resolve(w, Widths, vertical: false, hasTune: true, hasInsights: true, explicitSearch: false);
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
        Assert.Equal(1, f.ActiveCountFor(searching: true));
    }

    // ── D6: the Sort button wears the accent only off the page's own default ──

    [Fact]
    public void Sort_IsQuietOnThePagesDefault_AndAccentOffIt()
    {
        var album = Track.SortSpec.Default;
        var liked = new Track.SortSpec(Track.SortColumn.DateAdded, true);
        Assert.False(Track.TableRules.SortDiffersFromDefault(album, album));
        Assert.False(Track.TableRules.SortDiffersFromDefault(liked, liked));
        // Liked sorted by its own Date added (newest first) is NOT a change; the album's order on Liked is.
        Assert.True(Track.TableRules.SortDiffersFromDefault(album, liked));
        Assert.True(Track.TableRules.SortDiffersFromDefault(new Track.SortSpec(Track.SortColumn.Title, false), album));
        // The direction counts: an inverted album order is not the album's order.
        Assert.True(Track.TableRules.SortDiffersFromDefault(album with { Descending = true }, album));
    }

    // ── V10: the header rule shares the row plates' left edge ──

    [Fact]
    public void HeaderRule_StartsAtTheRowPlatesLeftEdge()
    {
        float padX = Track.RowMetrics.PadXFor(0);
        Assert.Equal(Track.RowMetrics.RowInset, Wavee.Themes.Classic.ClassicTrackChromeLayout.HeaderRuleInset(classicRows: false, vertical: false, padX));
        Assert.Equal(0f, Wavee.Themes.Classic.ClassicTrackChromeLayout.HeaderRuleInset(classicRows: true, vertical: false, padX));
        Assert.Equal(padX, Wavee.Themes.Classic.ClassicTrackChromeLayout.HeaderRuleInset(classicRows: false, vertical: true, padX));
    }

    // ── D4: the rail's filtered count ──

    [Fact]
    public void FilteredMeta_SpeaksOnlyWhileTheListIsNarrowed()
    {
        Assert.Null(Detail.Text.FilteredMeta(new Detail.ListCount(48, 48, 0L, false, false)));
        var line = Detail.Text.FilteredMeta(new Detail.ListCount(3, 48, 11L * 60_000L, true, true));
        Assert.NotNull(line);
        Assert.False(string.IsNullOrEmpty(line!.Value.Count));
        Assert.False(string.IsNullOrEmpty(line.Value.Mark));
        // A thin row drops the length segment rather than summing a lie (the words themselves are the loc table's).
        var thin = Detail.Text.FilteredMeta(new Detail.ListCount(3, 48, 11L * 60_000L, false, true));
        Assert.NotEqual(line.Value.Count, thin!.Value.Count);
    }
}
