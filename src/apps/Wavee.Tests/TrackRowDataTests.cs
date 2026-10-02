// ── Wavee.Tests/TrackRowDataTests.cs — the track row adapter's pure decisions ────────────────────────────────────────
//
// `Track.RowData` (Entities/Track.UI.cs) turns one track into the shared media surface's `Controls.CardData`; everything it
// DECIDES lives in `TrackRowRules` — which affordances a row has, the art edge of its shape, which parts the artists subtitle
// holds, and whose queue item the menu addresses — and that is what is pinned here. The element tree itself (the subtitle's
// links, the cover, the drag payload) reads the track table, so it is the live check's, not a fact's.
//
// No window, no loop, no table is touched.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class TrackRowDataTests
{
    // ── affordances ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_row_with_no_track_behind_it_has_no_affordances(int slot)
    {
        // Table.None: no play handler (a FAB with nothing to start), no menu (Track.Menu returns null for it) and no drag.
        Assert.Equal(new TrackRowRules.Affordances(Play: false, Menu: false, Drag: false),
                     TrackRowRules.AffordancesOf(slot, draggable: true));
    }

    [Fact]
    public void A_real_row_plays_has_a_menu_and_drags_when_its_surface_wants_it()
    {
        Assert.Equal(new TrackRowRules.Affordances(Play: true, Menu: true, Drag: true),
                     TrackRowRules.AffordancesOf(slot: 7, draggable: true));
    }

    [Fact]
    public void A_row_that_owns_another_drag_still_plays_and_has_a_menu()
    {
        // The queue's rows are lifted by their Reorderable, not by a card drag: Draggable false drops ONLY the drag.
        Assert.Equal(new TrackRowRules.Affordances(Play: true, Menu: true, Drag: false),
                     TrackRowRules.AffordancesOf(slot: 7, draggable: false));
    }

    // ── the art edge ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Visible_artwork_keeps_the_callers_edge_and_hidden_artwork_is_the_play_glyph_square()
    {
        Assert.Equal(40f, TrackRowRules.ArtEdge(40f, showArtwork: true));
        Assert.Equal(32f, TrackRowRules.ArtEdge(40f, showArtwork: false));
        Assert.Equal(TrackRowRules.NoArtworkEdge, TrackRowRules.ArtEdge(48f, showArtwork: false));
    }

    [Fact]
    public void The_hidden_artwork_edge_keeps_the_rows_30_fab()
    {
        // The surface derives its FAB from the art edge: the 32 square raises the same 30 FAB the 40 art does, so hiding the
        // artwork changes what is under the FAB and never its size.
        Assert.Equal(Shape.Row(40f).Fab, Shape.Row(TrackRowRules.NoArtworkEdge).Fab);
        Assert.Equal(30f, Shape.Row(TrackRowRules.NoArtworkEdge).Fab);
    }

    // ── the artists subtitle ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_explicit_badge_needs_the_surface_to_want_it_and_the_track_to_be_explicit()
    {
        Assert.True(TrackRowRules.SubtitleOf(true, true, false, false, true).Badge);
        Assert.False(TrackRowRules.SubtitleOf(false, true, false, false, true).Badge);   // the rail passes ShowExplicit: false
        Assert.False(TrackRowRules.SubtitleOf(true, false, false, false, true).Badge);
    }

    [Fact]
    public void The_film_glyph_needs_the_surface_to_want_it_and_the_track_to_have_a_video()
    {
        Assert.True(TrackRowRules.SubtitleOf(false, false, true, true, true).Film);      // the rail passes ShowVideo: true
        Assert.False(TrackRowRules.SubtitleOf(false, false, false, true, true).Film);
        Assert.False(TrackRowRules.SubtitleOf(false, false, true, false, true).Film);
    }

    [Fact]
    public void The_artists_show_whenever_the_track_credits_any()
    {
        Assert.True(TrackRowRules.SubtitleOf(false, false, false, false, true).Artists);
        Assert.False(TrackRowRules.SubtitleOf(true, true, true, true, false).Artists);
    }

    [Theory]
    [InlineData(false, false, false, 0, 0)]   // nothing to say: no subtitle element at all
    [InlineData(false, false, true, 1, 1)]    // artists alone: one child, no dot
    [InlineData(true, false, true, 2, 3)]     // E · artists
    [InlineData(false, true, true, 2, 3)]     // film · artists
    [InlineData(true, true, false, 2, 3)]     // E · film
    [InlineData(true, true, true, 3, 5)]      // E · film · artists
    public void The_subtitle_holds_a_dot_between_each_pair_of_parts(bool badge, bool film, bool artists, int parts, int children)
    {
        var p = new TrackRowRules.SubtitleParts(badge, film, artists);
        Assert.Equal(parts, p.Parts);
        Assert.Equal(children, p.Children);
    }

    // ── the queue item the menu addresses ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_queue_rows_item_wins_over_the_menu_options_own()
    {
        Assert.Equal(42L, TrackRowRules.MenuQueueItem(42UL, argsQueueItemId: 7L));
    }

    [Theory]
    [InlineData(null, 0L)]
    [InlineData(null, 7L)]
    [InlineData(0UL, 0L)]
    [InlineData(0UL, 7L)]
    public void A_row_with_no_queue_item_yet_leaves_the_menu_options_alone(ulong? queueItem, long argsQueueItem)
    {
        // null = not a queue row; 0 = a degenerate snapshot row that has no item id yet (the drag still marks it a queue
        // row, but there is no entry the menu could remove). Either way the caller's own option stands.
        Assert.Equal(argsQueueItem, TrackRowRules.MenuQueueItem(queueItem, argsQueueItem));
    }
}
