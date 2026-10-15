// ── Wavee.Tests/ClassicSkeletonGeometryTests.cs — every skeleton stands where its final layout lands (tuned Classic X2) ──────────
//
// The skeletons CT8 re-cut after CT3a-CT7 moved the geometry, pinned as arithmetic over the SAME constants the loaded views read:
//
//   library pane header   the four stand-in slots add up to the real text column's budget (16 + 36 + 20 + 16 + three 3-DIP gaps = 97),
//                         which fits the 128 cover, so the bars stand where the words land
//   library navigator     the shimmer row's plate and margin make the bound row's pitch (60, compact 44)
//   vertical list bar     the skeleton resolves the bar's own fit over the first-frame widths: the vertical arm never puts Play next or
//                         Shuffle on the bar, and what the fit keeps inline is never wider than the pane
//
// Pure: no scope, no engine.

using Wavee.Themes.Classic;
using Xunit;
using Layout = Wavee.Track.CommandBarLayout;

namespace Wavee.Tests;

public class ClassicSkeletonGeometryTests
{
    [Fact]
    public void The_pane_header_skeleton_slots_are_the_real_headers_text_budget()
    {
        float slots = ClassicLibraryPaneRules.SkeletonEyebrowLine + ClassicLibraryPaneRules.SkeletonTitleLine
                      + ClassicLibraryPaneRules.SkeletonAttributionLine + ClassicLibraryPaneRules.PaneHeaderMetaLine
                      + 3f * ClassicLibraryPaneRules.PaneHeaderGap;
        Assert.Equal(97f, slots);
        Assert.True(slots <= ClassicLibraryPaneRules.PaneCover, "the column fits beside the cover");
        Assert.True(ClassicLibraryPaneRules.SkeletonTitleBarH <= ClassicLibraryPaneRules.SkeletonTitleLine);
        Assert.True(ClassicLibraryPaneRules.SkeletonSmallBarH <= ClassicLibraryPaneRules.SkeletonEyebrowLine);
    }

    [Fact]
    public void The_navigator_shimmer_row_keeps_the_bound_rows_pitch()
    {
        Assert.Equal(User.NavRowExtent, User.NavRowPlate + 2f * User.NavRowMarginY);
        Assert.Equal(User.NavRowCompactExtent, User.NavRowCompactPlate + 2f * User.NavRowMarginY);
        Assert.True(ClassicLibraryRules.RowArt <= User.NavRowPlate, "the art fits the plate");
        Assert.True(ClassicLibraryRules.RowTitleLine + 1f + ClassicLibraryRules.RowSubtitleLine <= User.NavRowPlate, "the two lines fit the plate");
    }

    [Theory]
    [InlineData(320f, false)]
    [InlineData(520f, true)]
    [InlineData(900f, true)]
    public void The_vertical_list_bar_skeleton_draws_the_bars_own_fit(float pane, bool insights)
    {
        var fit = Layout.Resolve(pane, Layout.Nominal, vertical: true, hasTune: false, hasInsights: insights, explicitSearch: false);
        Assert.False(fit.Has(Track.InlineCommand.Shuffle));   // the hero carries Play and Shuffle in the vertical arm

        float width = Layout.SearchGap + Layout.SearchIconWidth + (insights ? Layout.Gap + Layout.InsightsWidth : 0f) + Layout.Gap + Layout.MoreWidth;
        if (fit.Has(Track.InlineCommand.Filter))
            width += Layout.Gap + (fit.Has(Track.InlineCommand.FilterLabel) ? Layout.Nominal.Filter : Layout.FilterIconWidth);
        if (fit.Has(Track.InlineCommand.Sort)) width += Layout.Gap + Layout.Nominal.Sort;
        Assert.True(width <= pane, $"the skeleton's right group ({width}) fits the pane ({pane})");
    }
}
