// ── Wavee.Tests/SidebarCardsTests.cs — the sidebar's media cards: what each entry kind answers ──────────────────────────
//
// The grid tile is one `Controls.Surface` fed by `SidebarCards` (Shell/Sidebar.Cards.cs), and every decision that adapter
// makes is a function in `SidebarCardRules`: whether the card draws selected, what activating it does, whether it carries
// a play affordance, whether it is a drop destination (and whether that drop is a deposit or a refusal), whether it is a
// drag source, where the "…" lives and the title gate. They are pinned here per entry kind. No window, no loop, no
// element is rendered.
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

    static readonly SidebarCardSurface[] AllSurfaces = [SidebarCardSurface.Tile];

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

    [Fact]
    public void An_entry_with_no_uri_has_nothing_to_play()
    {
        var e = Entry(SidebarEntryKind.Playlist, uri: "");
        Assert.False(SidebarCardRules.HasPlay(in e));
    }

    // ── drop ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_editable_playlist_is_a_track_deposit_on_every_surface()
    {
        var e = Entry(SidebarEntryKind.Playlist, canEdit: true);
        foreach (var surface in AllSurfaces)
            Assert.Equal(SidebarCardDrop.Deposit, SidebarCardRules.Drop(surface, in e));
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
        // Trap 5: never the raw uri fragment as a title. That is a bone, not an empty tooltip.
        var e = Entry(SidebarEntryKind.Playlist, uri: "spotify:playlist:3fMbdgg4jU18AjLCKBhRSm", name: "", identityKnown: false);
        Assert.Equal("", SidebarCardRules.TitleOf(in e));
        Assert.True(SidebarCardRules.IsPending(in e));
    }

    // ── geometry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_tile_cover_is_the_cell_less_the_plate_padding_on_both_sides()
    {
        Assert.Equal(53f, SidebarCardRules.TileCover(69f));
        Assert.Equal(24f, SidebarCardRules.TileCover(40f));
        Assert.Equal(0f, SidebarCardRules.TileCover(10f));
    }
}
