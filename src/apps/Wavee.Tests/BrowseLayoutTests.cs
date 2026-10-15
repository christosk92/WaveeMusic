// ── Wavee.Tests/BrowseLayoutTests.cs — the directory cells' column math, the star grid and the tile model ──────────
//
// NEW in 0.3 (0.2.9 had these private in `Features/Browse/BrowseTiles.cs:286-358` with no test). The three column
// decisions a band makes — Genres' fixed bands (`LinkColumns`), Mood's uncapped floor-fit (`BarColumns`), More's capped
// floor-fit (`MoreColumns`), which T10a2 moved into Classic's `Rules\Tiers.Browse.cs` (`ClassicBrowseTiers`) — and the star grid's
// own track count (`ClassicBrowseCells.StarGrid`) with its empty case, plus the pure half of the cell factories
// (`BrowseTiles.ToModel` / `FeatureRoute` / `PageRoute`, still core).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class BrowseLayoutTests
{
    // ── the column functions ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1200f, 3)]
    [InlineData(721f, 3)]
    [InlineData(720f, 2)]     // bands are exclusive at their edge: > 720 is three
    [InlineData(381f, 2)]
    [InlineData(380f, 1)]
    [InlineData(0f, 1)]
    public void LinkColumns_AreFixedBands_NeverAFloorDivide(float width, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.LinkColumns(width));

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(131f, 1)]
    [InlineData(132f, 1)]
    [InlineData(264f, 2)]
    [InlineData(1320f, 10)]   // no cap
    public void BarColumns_FloorFitAt132_Uncapped(float width, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.BarColumns(width));

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(335f, 1)]
    [InlineData(336f, 2)]
    [InlineData(672f, 4)]
    [InlineData(5000f, 4)]    // capped at four
    public void MoreColumns_FloorFitAt168_CappedAtFour(float width, int expected)
        => Assert.Equal(expected, ClassicBrowseTiers.MoreColumns(width));

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
