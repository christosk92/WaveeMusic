// ── Wavee.Tests/OmnibarSurfaceRowTests.cs — the search flyout's rich-row rules ───────────────────────────────────────
//
// Which entity a row stands for, which menu it carries, whether it drags, and its shape. The rows render on the shared
// media surface in slot mode; these are the decisions that surface is fed.

using Xunit;
using Kind = Wavee.Shell.Omnibar.ItemKind;

namespace Wavee.Tests;

public class OmnibarSurfaceRowTests
{
    [Theory]
    [InlineData(Kind.Track, EntityKind.Track)]
    [InlineData(Kind.Episode, EntityKind.Episode)]
    [InlineData(Kind.Album, EntityKind.Album)]
    [InlineData(Kind.Artist, EntityKind.Artist)]
    [InlineData(Kind.Playlist, EntityKind.Playlist)]
    [InlineData(Kind.Podcast, EntityKind.Show)]
    [InlineData(Kind.User, EntityKind.User)]
    [InlineData(Kind.Genre, EntityKind.Unknown)]
    [InlineData(Kind.Audiobook, EntityKind.Unknown)]
    public void Each_kind_maps_to_its_entity_kind(Kind kind, EntityKind expected)
        => Assert.Equal(expected, OmnibarRowRules.EntityKindOf(kind));

    [Theory]
    [InlineData(Kind.Track, OmnibarRowRules.MenuKind.Track)]
    [InlineData(Kind.Album, OmnibarRowRules.MenuKind.Container)]
    [InlineData(Kind.Artist, OmnibarRowRules.MenuKind.Container)]
    [InlineData(Kind.Playlist, OmnibarRowRules.MenuKind.Container)]
    [InlineData(Kind.Podcast, OmnibarRowRules.MenuKind.Container)]
    [InlineData(Kind.Episode, OmnibarRowRules.MenuKind.None)]
    [InlineData(Kind.User, OmnibarRowRules.MenuKind.None)]
    [InlineData(Kind.Genre, OmnibarRowRules.MenuKind.None)]
    [InlineData(Kind.Audiobook, OmnibarRowRules.MenuKind.None)]
    public void Menu_kind_follows_the_search_page(Kind kind, OmnibarRowRules.MenuKind expected)
        => Assert.Equal(expected, OmnibarRowRules.MenuOf(kind));

    [Theory]
    [InlineData(Kind.Track, true)]
    [InlineData(Kind.Album, true)]
    [InlineData(Kind.Podcast, true)]
    [InlineData(Kind.User, false)]
    [InlineData(Kind.Genre, false)]
    [InlineData(Kind.Audiobook, false)]
    public void Only_entities_with_a_resource_drag(Kind kind, bool expected)
        => Assert.Equal(expected, OmnibarRowRules.Drags(kind));

    [Fact]
    public void The_row_is_a_44_art_square_in_a_58_floor()
    {
        var shape = OmnibarRowRules.RowShape;
        Assert.True(shape.IsRow);
        Assert.Equal(44f, shape.ArtEdge);
        Assert.Equal(58f, shape.MinHeight);
    }

    [Fact]
    public void The_menu_button_takes_a_lane_beside_the_follow_pill_like_the_search_page()
    {
        // Every flyout row with a menu ends in the heart or the Follow pill: the "…" overlay would cover it ("Fol…").
        var shape = OmnibarRowRules.RowShape;
        Assert.Equal(SearchHitRules.RowMenu, shape.Menu);
        Assert.Equal(MenuPlacement.TrailingLane, shape.Menu);
        Assert.True(SurfaceRules.MenuReservesWidth(shape, hasMenu: true, showMenu: true));
    }
}
