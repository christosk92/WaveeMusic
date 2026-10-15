// ── Wavee.Tests/ClassicSkeletonGeometryTests.cs — every skeleton stands where its final layout lands (tuned Classic X2) ──────────
//
// The skeletons CT8 re-cut after CT3a-CT7 moved the geometry, pinned as arithmetic over the SAME constants the loaded views read:
//
//   library pane header   the four stand-in slots add up to the real text column's budget (16 + 36 + 20 + 16 + three 3-DIP gaps = 97),
//                         which fits the 128 cover, so the bars stand where the words land
//   library navigator     the shimmer row's plate and margin make the bound row's pitch (60, compact 44)
//   vertical list bar     the skeleton resolves the bar's own fit over the first-frame widths: the vertical arm never puts Play next or
//                         Shuffle on the bar, and what the fit keeps inline is never wider than the pane
//   library pane rows     the counted shimmer's lane set is the row style's own (a Classic set, a Classic compilation's Artist lane) and its
//                         width tracks are the table's (follow-up: a Classic pane shimmered Modern's 48 rows under Modern's 36 header)
//   rail actions          the rail skeleton reserves the fixed group the page will lay out: a page without a web link (a local album) reserves
//                         three squares, which fit beside the split on one 36 line at a 256 cover where the old four wrapped to 76
//   library grid          the grid shimmer's cell is the fitted grid's own (the cell minimum per view and size, the GridFit column formula)
//
// Pure: no scope, no engine.

using FluentGpu.Dsl;
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
    [InlineData(false, false, 5)]   // # · ♥ · Title · ⏱ · …
    [InlineData(false, true, 5)]    // Modern folds a compilation's artist into the title's subline
    [InlineData(true, false, 5)]
    [InlineData(true, true, 6)]     // a Classic compilation's Artist lane
    public void The_library_pane_shimmer_lanes_are_the_row_styles_own(bool classic, bool compilation, int lanes)
    {
        var set = Track.EmbeddedColumns(classic, compilation);
        Assert.Equal(classic, set.Classic);
        Assert.Equal(classic && compilation, set.Artist);
        Assert.Equal(Track.TableRules.IdentityColumns(classic, false, false, compilation, Track.EmbeddedTier).Artist, set.Artist);
        var tracks = Track.EmbeddedTracks(in set);
        Assert.Equal(lanes, tracks.Length);
        Assert.Same(Track.TracksFor(in set, Track.RowMetrics.ThumbSize), tracks);   // the table's own cached array
        // the shimmer's rows and header are the style's: 40 under a 32 header (Classic), 48 under a 36 one (Modern)
        Assert.Equal(classic ? 32f : 36f, Track.TableRules.HeaderHeightFor(classic));
        var standard = Wavee.Theming.TrackRowMetrics.Standard;
        Assert.Equal(classic ? 40f : 48f, classic ? standard.ClassicRow : standard.ModernRow);
    }

    [Fact]
    public void The_rail_skeleton_reserves_no_share_square_on_a_page_without_a_link()
    {
        // A local album: Shuffle · heart · ⋯ (no web link, so no Share). The plan the skeleton reserves is the loaded rail's, row for row.
        var reserved = Detail.Skeleton.RailPlanFor(DetailKind.Album, BadgeStyle.TypeYear, fixedActions: 3, descriptionMaxLines: 6);
        var loaded = Detail.RailLayout.RowsFor(DetailKind.Album, BadgeStyle.TypeYear, eyebrowText: true, ownerKnown: false, artists: true,
                                               metaShown: false, fabs: 3, description: false, descriptionMaxLines: 6, default);
        Assert.Equal(loaded, reserved);
        // at a 280 rail (cover 256) the split 121 + 4 and three 8 + 32 squares are 245: one line, where the old fourth square wrapped
        Assert.Equal(4f + 32f, Detail.RailLayout.CtaHeight(reserved, 256f));
        Assert.Equal(4f + 32f + 8f + 32f, Detail.RailLayout.CtaHeight(reserved with { Fabs = 4 }, 256f));
    }

    [Theory]
    [InlineData(3, 1, 324f)]   // grid, M: 140-DIP cells in a 340 navigator (two columns)
    [InlineData(2, 0, 324f)]   // compact grid, S: 88-DIP cells (three columns)
    [InlineData(3, 2, 600f)]   // grid, L: 164-DIP cells
    public void The_grid_shimmer_cells_are_the_fitted_grids(int view, int size, float inner)
    {
        float min = LibraryNavMount.CellMinFor(view, size);
        var mount = LibraryNavMount.For("albums", view, size, alphabetical: false, lettersKey: 0);
        Assert.Equal(mount.CellMin, min);
        Assert.Equal(LibraryNavLayoutKind.GridFit, mount.Layout);
        // the shimmer's auto-fill grid and the bound GridFit share ONE column formula, so their cells are the same width
        int cols = GridEl.AutoFillColumnCount(inner, min, mount.Gap);
        Assert.Equal(System.Math.Max(1, (int)((inner + mount.Gap) / (min + mount.Gap))), cols);
        Assert.True((inner - (cols - 1) * mount.Gap) / cols >= min, "a cell is never under the minimum");
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
