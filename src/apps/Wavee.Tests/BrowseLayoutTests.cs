// ── Wavee.Tests/BrowseLayoutTests.cs — the directory cells' column math, the star grid and the tile model ──────────
//
// NEW in 0.3 (0.2.9 had these private in `Features/Browse/BrowseTiles.cs:286-358` with no test). The three column
// decisions a band makes — Genres' fixed count, Mood's floor-fit, More's capped floor-fit — are now one record per content tier
// (`ClassicBrowseTiers.For`, Classic's `Rules\Tiers.Browse.cs`, TC4; the Genres 720 / 380 bands are gone) — and the star grid's
// own track count (`ClassicBrowseCells.StarGrid`) with its empty case, plus the pure half of the cell factories
// (`BrowseTiles.ToModel` / `FeatureRoute` / `PageRoute`, still core).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class BrowseLayoutTests
{
    // ── the tier records (plan 3.6, TC4): the column rules the directory's bands read ───────────────────────────────────────────

    [Theory]
    [InlineData(WidthTier.Compact, 2)]
    [InlineData(WidthTier.Small, 2)]
    [InlineData(WidthTier.Standard, 3)]    // the tuned 3 (Standard's regions are always wider than the old 720 band)
    [InlineData(WidthTier.Huge, 6)]
    [InlineData(WidthTier.Huge4K, 6)]      // Classic draws no 4K band: Huge's record
    public void Genres_AreAFixedCountPerTier_NeverAFloorDivide(WidthTier tier, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.For(tier).GenreColumns());

    [Theory]
    [InlineData(WidthTier.Standard, 0f, 1)]
    [InlineData(WidthTier.Standard, 131f, 1)]
    [InlineData(WidthTier.Standard, 264f, 2)]
    [InlineData(WidthTier.Standard, 1320f, 10)]   // Standard: floor-fit at 132, no cap (as before tiers)
    [InlineData(WidthTier.Compact, 5000f, 2)]
    [InlineData(WidthTier.Small, 688f, 5)]        // C-Browse-Small: five at 800
    [InlineData(WidthTier.Small, 5000f, 5)]
    [InlineData(WidthTier.Huge, 2208f, 12)]       // C-Browse-Huge: twelve at 2560
    [InlineData(WidthTier.Huge, 5000f, 12)]
    public void Moods_FloorFitAt132_CappedPerTier(WidthTier tier, float width, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.For(tier).MoodColumns(width));

    [Theory]
    [InlineData(WidthTier.Standard, 0f, 1)]
    [InlineData(WidthTier.Standard, 335f, 1)]
    [InlineData(WidthTier.Standard, 336f, 2)]
    [InlineData(WidthTier.Standard, 672f, 4)]
    [InlineData(WidthTier.Standard, 5000f, 4)]    // Standard: capped at four (as before tiers)
    [InlineData(WidthTier.Compact, 5000f, 2)]
    [InlineData(WidthTier.Small, 5000f, 3)]
    [InlineData(WidthTier.Huge, 2208f, 8)]        // C-Browse-Huge: eight at 2560
    public void More_FloorFitAt168_CappedPerTier(WidthTier tier, float width, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.For(tier).MoreColumns(width));

    [Fact]
    public void Charts_StackAtCompactAndSmall_AndPageTwoUpThenFourUp()
    {
        var c = ClassicBrowseTiers.For(WidthTier.Compact).Charts;
        var s = ClassicBrowseTiers.For(WidthTier.Small).Charts;
        var d = ClassicBrowseTiers.For(WidthTier.Standard).Charts;
        var h = ClassicBrowseTiers.For(WidthTier.Huge).Charts;
        Assert.True(c.Stacked && s.Stacked && !d.Stacked && !h.Stacked);
        Assert.Equal((136f, 92f), (c.TileH, c.Cover));       // C-Browse-Compact
        Assert.Equal((140f, 100f), (s.TileH, s.Cover));      // C-Browse-Small
        Assert.Equal((2, 4), (d.MaxColumns, h.MaxColumns));  // two up (the tuned deck), four up at Huge
        Assert.Equal((HomeModuleLayout.FoldCardHeight, HomeModuleLayout.FoldCover), (d.TileH, d.Cover));
    }

    [Fact]
    public void CompactTargets_AreAtLeast40()
    {
        var c = ClassicBrowseTiers.For(WidthTier.Compact);
        Assert.True(c.TopChipH >= 40f && c.ForYouChipH >= 40f && c.LinkMinH >= 40f && c.ShelfHeaderH >= 40f);
        Assert.True(c.ForYouStrip);
    }

    [Fact]
    public void Standard_IsTheTunedDesign()
    {
        var d = ClassicBrowseTiers.For(WidthTier.Standard);
        Assert.Equal(Spacing.L, d.BandGap);
        Assert.Equal(0f, d.TopChipH);                  // the stock 32 chip
        Assert.Equal(-1f, d.TopChipRadius);
        Assert.False(d.ForYouStrip);
        Assert.Equal(0, d.Shelf.MaxColumns);           // the shelf fits 148-188 with no cap
        Assert.Equal(R.Space.ShelfCardMin, d.Shelf.MinCardW);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]        // never a zero-track grid
    [InlineData(-2, 1)]
    public void StarGrid_HasOneStarTrackPerColumn_AtLeastOne(int columns, int tracks)
    {
        Element[] cells = [new BoxEl(), new BoxEl(), new BoxEl(), new BoxEl(), new BoxEl()];
        var grid = Assert.IsType<GridEl>(ClassicBrowseCells.StarGrid(columns, Spacing.S, Spacing.M, cells));

        Assert.Equal(tracks, grid.Columns.Length);
        Assert.All(grid.Columns, t => Assert.Equal(TrackSize.Star(), t));
        Assert.Equal(5, grid.Children.Length);
        Assert.Equal(Spacing.S, grid.ColGap);
        Assert.Equal(Spacing.M, grid.RowGap);
        Assert.True(float.IsNaN(grid.RowHeight));                    // rows size to their tallest cell
        Assert.Equal(FlexAlign.Stretch, grid.AlignSelf);             // backfills a one-column band
    }

    [Fact]
    public void StarGrid_WithNoCells_IsABareBox()
        => Assert.IsType<BoxEl>(ClassicBrowseCells.StarGrid(3, Spacing.S, Spacing.S, []));

    // ── the link chip (D7) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Top_and_For_you_are_the_one_chip_plate_32_tall_radius_4_on_a_link()
    {
        // D7: no private pill — Top and For you are the stock chip's unchecked plate (the ToggleButton's own off ramp and geometry) on a link.
        var off = ToggleButton.DefaultStyle;
        foreach (var chip in new[]
                 {
                     Controls.LinkChip("spotify:page:music", "Music", static () => { }, strong: true),          // Top
                     Controls.LinkChip("spotify:page:mixes", "Mixes", static () => { }, new BoxEl { Width = 8f }), // For you, with its pip
                 })
        {
            var box = Assert.IsType<BoxEl>(chip);
            Assert.Equal(32f, box.MinHeight);
            Assert.Equal(CornerRadius4.All(4f), box.Corners);
            Assert.Equal(AutomationRole.Hyperlink, box.Role);
            Assert.Equal(off.OffBackground, box.Fill);
            Assert.Equal(off.OffHover, box.HoverFill);
            Assert.Equal(off.Padding, box.Padding);
        }
    }

    // ── the tile model (0.2.9 BrowseTiles.ToModel) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ToModel_WithNoHandlers_IsTheSharedInertNoop()
    {
        var model = BrowseTiles.ToModel(new BrowseCategory("spotify:page:x", "X", 0xFF102030u, "https://i/x"), null, null);
        Assert.Same(BrowseTiles.ToModelNoop, model.Open);
        Assert.Equal("X", model.Title);
        Assert.Equal("spotify:page:x", model.Uri);
        Assert.Equal((uint?)0xFF102030u, model.Color);
        Assert.Equal("https://i/x", model.Artwork);
        model.Open();                                                // inert, never a throw
    }

    [Fact]
    public void ToModel_ACategoryOpensItsPage_AFeatureOpensItsFeature()
    {
        string? page = null, pageTitle = null, feature = null;
        Action<string, string> openCategory = (u, t) => { page = u; pageTitle = t; };
        Action<string> openFeature = u => feature = u;

        BrowseTiles.ToModel(new BrowseCategory("spotify:page:pop", "Pop", null), openCategory, openFeature).Open();
        Assert.Equal("spotify:page:pop", page);
        Assert.Equal("Pop", pageTitle);
        Assert.Null(feature);

        page = null;
        BrowseTiles.ToModel(new BrowseCategory("spotify:concerts", "Live Events", null, null, IsClientFeature: true),
                            openCategory, openFeature).Open();
        Assert.Equal("spotify:concerts", feature);                  // a client feature never pushes a browse route
        Assert.Null(page);
    }

    [Fact]
    public void FeatureRoute_OnlyConcertsResolves()
    {
        Assert.Equal(Shell.RouteKind.Concerts, BrowseTiles.FeatureRoute("spotify:concerts").Kind);
        Assert.True(BrowseTiles.FeatureRoute("spotify:xlink:something").IsNone);
        Assert.True(BrowseTiles.FeatureRoute("").IsNone);
    }

    [Fact]
    public void PageRoute_CarriesThePageAsSubject_AndTheTrimmedTitleAsTheFrameOneArg()
    {
        TestScope.Fresh();
        var route = BrowseTiles.PageRoute("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "  Pop ");
        Assert.Equal(Shell.RouteKind.BrowseCategory, route.Kind);
        Assert.Equal("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", route.Subject.Text);
        Assert.Equal("Pop", Entities.Strings.Resolve(route.Arg));

        Assert.Equal(default(StringId), BrowseTiles.PageRoute("spotify:page:x", "   ").Arg);   // a blank title is no arg
        Assert.Equal(default(StringId), BrowseTiles.PageRoute("spotify:page:x", null).Arg);
    }
}
