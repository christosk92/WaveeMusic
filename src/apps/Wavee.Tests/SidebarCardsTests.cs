// ── Wavee.Tests/SidebarCardsTests.cs — the sidebar's media cards: what each entry kind answers ──────────────────────────
//
// The hero card, the grid tile and the collapsed-rail tile are each one `Controls.Surface` fed by `SidebarCards`
// (Shell/Sidebar.Cards.cs), and every decision those adapters make is a function in `SidebarCardRules`: whether the card draws
// selected, what activating it does, whether it carries a play affordance, whether it is a drop destination (and whether
// that drop is a deposit or a refusal), whether it is a drag source, where the "…" lives, the title gate, and the hero's
// pinned geometry. They are pinned here per entry kind. No window, no loop, no element is rendered.
//
// What only the engine can show (the hand cursor, the focus ring, the hover plate, the now-playing pill, the drop wash,
// the tooltip on the label-less tile) is the live check's job — the surface rules themselves are SurfaceRulesTests.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarCardsTests
{
    // ── builders ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Entry(SidebarEntryKind kind, string uri = "spotify:thing:1", string name = "Name",
                                     bool canEdit = false, bool identityKnown = true, string? id = null)
        => new(id ?? ("route:" + kind), kind, uri, name, "", StringId.Empty, null, 0, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None) { CanEdit = canEdit, IdentityKnown = identityKnown };

    static readonly SidebarCardSurface[] AllSurfaces = [SidebarCardSurface.Hero, SidebarCardSurface.Tile, SidebarCardSurface.Rail];

    // ── selected ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarEntryKind.Playlist)]
    [InlineData(SidebarEntryKind.Album)]
    [InlineData(SidebarEntryKind.Artist)]
    [InlineData(SidebarEntryKind.Show)]
    [InlineData(SidebarEntryKind.AppRoute)]
    public void A_routed_entry_is_selected_exactly_when_its_route_is_the_live_route(SidebarEntryKind kind)
    {
        var e = Entry(kind);
        Assert.True(SidebarCardRules.Selected(in e, e.Id));
        Assert.False(SidebarCardRules.Selected(in e, "some:other:route"));
        Assert.False(SidebarCardRules.Selected(in e, ""));
    }

    [Theory]
    [InlineData(SidebarEntryKind.Folder)]
    [InlineData(SidebarEntryKind.Track)]
    public void A_folder_and_a_track_are_never_selected_even_when_the_route_equals_their_id(SidebarEntryKind kind)
    {
        // Neither has a nav route (a folder expands in place, a track plays), so no route can select them.
        var e = Entry(kind);
        Assert.False(SidebarCardRules.Selected(in e, e.Id));
    }

    // ── activation ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarEntryKind.Track, SidebarCardActivation.Play)]
    [InlineData(SidebarEntryKind.Playlist, SidebarCardActivation.Navigate)]
    [InlineData(SidebarEntryKind.Album, SidebarCardActivation.Navigate)]
    [InlineData(SidebarEntryKind.Artist, SidebarCardActivation.Navigate)]
    [InlineData(SidebarEntryKind.Show, SidebarCardActivation.Navigate)]
    [InlineData(SidebarEntryKind.AppRoute, SidebarCardActivation.Navigate)]
    [InlineData(SidebarEntryKind.Folder, SidebarCardActivation.None)]
    public void Activation_is_today_s_rule_per_kind(SidebarEntryKind kind, SidebarCardActivation expected)
        => Assert.Equal(expected, SidebarCardRules.Activation(Entry(kind)));

    // ── play ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarEntryKind.Playlist, true)]
    [InlineData(SidebarEntryKind.Album, true)]
    [InlineData(SidebarEntryKind.Show, true)]
    [InlineData(SidebarEntryKind.Track, true)]
    [InlineData(SidebarEntryKind.Artist, false)]
    [InlineData(SidebarEntryKind.Folder, false)]
    [InlineData(SidebarEntryKind.AppRoute, false)]
    public void Play_exists_for_a_playable_context_on_the_hero_and_the_tile(SidebarEntryKind kind, bool playable)
    {
        var e = Entry(kind);
        Assert.Equal(playable, SidebarCardRules.HasPlay(SidebarCardSurface.Hero, in e, playButton: true));
        Assert.Equal(playable, SidebarCardRules.HasPlay(SidebarCardSurface.Tile, in e, playButton: true));
    }

    [Theory]
    [InlineData(SidebarEntryKind.Playlist)]
    [InlineData(SidebarEntryKind.Album)]
    [InlineData(SidebarEntryKind.Show)]
    [InlineData(SidebarEntryKind.Track)]
    public void The_hero_honours_the_sections_play_button_option_and_the_tile_does_not_ask(SidebarEntryKind kind)
    {
        var e = Entry(kind);
        Assert.False(SidebarCardRules.HasPlay(SidebarCardSurface.Hero, in e, playButton: false));
        // PlayButton is an EntityEmbed-only option: a grid cell has no such switch.
        Assert.True(SidebarCardRules.HasPlay(SidebarCardSurface.Tile, in e, playButton: false));
    }

    [Fact]
    public void The_rail_tile_never_carries_a_play_affordance()
    {
        foreach (var kind in Enum.GetValues<SidebarEntryKind>())
        {
            var e = Entry(kind);
            Assert.False(SidebarCardRules.HasPlay(SidebarCardSurface.Rail, in e, playButton: true));
        }
    }

    [Fact]
    public void An_entry_with_no_uri_has_nothing_to_play()
    {
        var e = Entry(SidebarEntryKind.Playlist, uri: "");
        Assert.False(SidebarCardRules.HasPlay(SidebarCardSurface.Hero, in e, playButton: true));
        Assert.False(SidebarCardRules.HasPlay(SidebarCardSurface.Tile, in e, playButton: true));
    }

    // ── drop ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_editable_playlist_is_a_track_deposit_on_every_surface()
    {
        var e = Entry(SidebarEntryKind.Playlist, canEdit: true);
        foreach (var surface in AllSurfaces)
            Assert.Equal(SidebarCardDrop.Deposit, SidebarCardRules.Drop(surface, in e));
    }

    [Fact]
    public void A_read_only_playlist_refuses_with_a_reason_when_expanded_and_stays_transparent_on_the_rail()
    {
        var e = Entry(SidebarEntryKind.Playlist, canEdit: false);
        Assert.Equal(SidebarCardDrop.Refuse, SidebarCardRules.Drop(SidebarCardSurface.Hero, in e));
        Assert.Equal(SidebarCardDrop.Refuse, SidebarCardRules.Drop(SidebarCardSurface.Tile, in e));
        Assert.Equal(SidebarCardDrop.None, SidebarCardRules.Drop(SidebarCardSurface.Rail, in e));
    }

    [Theory]
    [InlineData(SidebarEntryKind.Album)]
    [InlineData(SidebarEntryKind.Artist)]
    [InlineData(SidebarEntryKind.Show)]
    [InlineData(SidebarEntryKind.Track)]
    [InlineData(SidebarEntryKind.Folder)]
    [InlineData(SidebarEntryKind.AppRoute)]
    public void Only_a_playlist_is_ever_a_drop_destination(SidebarEntryKind kind)
    {
        // `canEdit` is stamped for playlists only, but even a stray flag must not make another kind a deposit.
        var e = Entry(kind, canEdit: true);
        foreach (var surface in AllSurfaces)
            Assert.Equal(SidebarCardDrop.None, SidebarCardRules.Drop(surface, in e));
    }

    // ── drag ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarEntryKind.Playlist, true)]
    [InlineData(SidebarEntryKind.Album, true)]
    [InlineData(SidebarEntryKind.Artist, true)]
    [InlineData(SidebarEntryKind.Show, true)]
    [InlineData(SidebarEntryKind.Folder, true)]
    [InlineData(SidebarEntryKind.AppRoute, true)]
    [InlineData(SidebarEntryKind.Track, false)]
    public void Everything_but_a_track_is_a_drag_source(SidebarEntryKind kind, bool draggable)
        => Assert.Equal(draggable, SidebarCardRules.CanDrag(Entry(kind)));

    [Theory]
    [InlineData(SidebarEntryKind.Playlist, true)]
    [InlineData(SidebarEntryKind.Folder, true)]
    [InlineData(SidebarEntryKind.Album, false)]
    [InlineData(SidebarEntryKind.Artist, false)]
    [InlineData(SidebarEntryKind.Show, false)]
    [InlineData(SidebarEntryKind.AppRoute, false)]
    public void A_playlist_and_a_folder_are_the_rootlist_members(SidebarEntryKind kind, bool member)
        => Assert.Equal(member, SidebarCardRules.RootlistMember(Entry(kind)));

    // ── the menu glyph ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_hero_row_shows_the_menu_glyph_and_the_tile_and_rail_keep_the_menu_on_right_click()
    {
        Assert.True(SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Hero));
        Assert.False(SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Tile));
        Assert.False(SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Rail));
    }

    // ── the title gate ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_named_entry_shows_its_name()
        => Assert.Equal("Road Trip", SidebarCardRules.TitleOf(Entry(SidebarEntryKind.Playlist, name: "Road Trip")));

    [Fact]
    public void A_nameless_resolved_entry_gets_the_honest_short_uri()
    {
        var e = Entry(SidebarEntryKind.Playlist, uri: "spotify:playlist:3fMbdgg4jU18AjLCKBhRSm", name: "", identityKnown: true);
        Assert.Equal("3fMbdgg4jU18AjLCKBhRSm", SidebarCardRules.TitleOf(in e));
        Assert.False(SidebarCardRules.IsPending(in e));
    }

    [Fact]
    public void An_entry_whose_identity_has_not_landed_shows_nothing_and_is_pending()
    {
        // Trap 5: never the raw uri fragment as a title. On the label-less rail tile that is a bone, not an empty tooltip.
        var e = Entry(SidebarEntryKind.Playlist, uri: "spotify:playlist:3fMbdgg4jU18AjLCKBhRSm", name: "", identityKnown: false);
        Assert.Equal("", SidebarCardRules.TitleOf(in e));
        Assert.True(SidebarCardRules.IsPending(in e));
    }

    [Fact]
    public void The_hero_title_prefers_alias_then_name_then_cache_then_the_key()
    {
        Assert.Equal("Alias", SidebarCardRules.HeroTitle("Alias", resolved: true, "Name", "Cached", "spotify:album:abc"));
        Assert.Equal("Name", SidebarCardRules.HeroTitle(null, resolved: true, "Name", "Cached", "spotify:album:abc"));
        Assert.Equal("Cached", SidebarCardRules.HeroTitle(null, resolved: true, "", "Cached", "spotify:album:abc"));
        Assert.Equal("Cached", SidebarCardRules.HeroTitle("", resolved: false, "Name", "Cached", "spotify:album:abc"));
        Assert.Equal("abc", SidebarCardRules.HeroTitle(null, resolved: false, null, null, "spotify:album:abc"));
    }

    // ── geometry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarDensity.Compact, 56f, 40f)]
    [InlineData(SidebarDensity.Cozy, 72f, 56f)]
    [InlineData(SidebarDensity.Comfortable, 88f, 72f)]
    public void The_hero_surface_is_exactly_the_sections_one_card_height(SidebarDensity density, float height, float cover)
    {
        float card = SidebarRowGeometry.CardHeightFor(density);
        Assert.Equal(height, card);
        Assert.Equal(cover, SidebarCardRules.HeroCover(card));
        // The surface pads 8 on every side, so cover + 2 x pad is the pinned height: the planner's analytic extent holds.
        Assert.Equal(card, SidebarCardRules.HeroCover(card) + 2f * SurfaceGeometry.RowPad);

        var shape = SidebarCardRules.HeroShape(card);
        Assert.True(shape.IsRow);
        Assert.Equal(card, SurfaceGeometry.RowHeight(in shape));
        Assert.Equal(cover, shape.ArtEdge);
        Assert.Equal(28f, shape.Fab);
    }

    [Fact]
    public void The_tile_cover_is_the_cell_less_the_plate_padding_on_both_sides()
    {
        Assert.Equal(53f, SidebarCardRules.TileCover(69f));
        Assert.Equal(24f, SidebarCardRules.TileCover(40f));
        Assert.Equal(0f, SidebarCardRules.TileCover(10f));
    }

    [Fact]
    public void The_rail_tile_is_label_less_so_its_title_is_always_the_tooltip()
    {
        Assert.False(global::Wavee.Shape.RailTile.Labels);
        Assert.True(SurfaceRules.TitleTip(trimmed: false, hasLabels: global::Wavee.Shape.RailTile.Labels));
        // …and it has neither a FAB nor a "…" slot: play and the menu are the context menu's.
        Assert.Equal(0f, global::Wavee.Shape.RailTile.Fab);
        Assert.Equal(MenuPlacement.None, global::Wavee.Shape.RailTile.Menu);
    }
}
