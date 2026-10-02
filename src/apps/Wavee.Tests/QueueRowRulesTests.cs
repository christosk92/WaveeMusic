// ── Wavee.Tests/QueueRowRulesTests.cs — the rail queue row's pure decisions ─────────────────────────────────────────────
//
// The rail's Modern queue rows and its now-playing card are the shared media surface (`Controls.Surface` over
// `Track.RowData` / `Episode.RowData`, Entities/Queue.UI.cs); everything the builders DECIDE lives in `QueueRowRules` —
// the row's height under each skin (the reorder lane's slot maths needs it exact, and the surface's floor is not it), the
// autoplay dim, which row has a ✕, which row drags on its own, that no row has a "…" button, which adapter feeds a row and
// where a click on the playing card goes — and that is what is pinned here. The element trees (the plate, the trailing cluster, the seed face) are the
// surface's own facts (SurfaceRulesTests) and the live check's.
//
// No window, no loop, no table is touched.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueRowRulesTests
{
    // ── the row's height ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_text_column_is_a_title_line_the_label_gap_and_a_caption_line()
    {
        // 20 + 2 + 16: what a row with artists stacks beside its art, from the surface's own geometry constants.
        Assert.Equal(38f, QueueRowRules.TextColumn);
    }

    [Theory]
    [InlineData(32f)]   // the hidden-artwork play-glyph square
    [InlineData(34f)]   // the queue's art
    [InlineData(38f)]   // exactly the text column
    public void A_small_art_leaves_the_text_column_to_govern_the_row_height(float art)
    {
        // 8 padding above and below the 38 text column.
        Assert.Equal(54f, QueueRowRules.SurfaceExtent(art));
    }

    [Fact]
    public void A_large_art_governs_the_row_height()
    {
        Assert.Equal(76f, QueueRowRules.SurfaceExtent(60f));
    }

    [Fact]
    public void The_surface_default_floor_is_taller_than_the_queue_row_so_the_shape_states_its_own()
    {
        // `Shape.Row(34)` floors at 64: a queue row built on the preset alone would be 10 taller than the lane's slots.
        Assert.True(Shape.RowFloorFor(34f) > QueueRowRules.SurfaceExtent(34f));
        var shape = Shape.Row(34f) with { MinHeight = QueueRowRules.SurfaceExtent(34f) };
        Assert.Equal(54f, shape.MinHeight);
    }

    [Fact]
    public void The_extent_follows_the_skin()
    {
        Assert.Equal(44f, QueueRowRules.Extent(classic: true, artEdge: 34f));
        Assert.Equal(QueueRowRules.ClassicExtent, QueueRowRules.Extent(classic: true, artEdge: 60f));
        Assert.Equal(54f, QueueRowRules.Extent(classic: false, artEdge: 34f));
    }

    // ── the dim, the ✕ and the drag ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(QueueSection.Queue, 1f)]
    [InlineData(QueueSection.NextUp, 1f)]
    [InlineData(QueueSection.Autoplay, 0.72f)]
    public void Only_the_autoplay_section_is_dimmed(QueueSection section, float opacity)
    {
        Assert.Equal(opacity, QueueRowRules.Dim(section));
    }

    [Theory]
    [InlineData(false, 7UL, true)]    // our queue, an entry with an item id: removable
    [InlineData(false, 0UL, false)]   // no item id to name it by
    [InlineData(true, 7UL, false)]    // a remote device's mirror is read-only
    [InlineData(true, 0UL, false)]
    public void A_row_has_an_x_when_the_queue_is_ours_and_the_entry_is_named(bool viewer, ulong itemId, bool removable)
    {
        Assert.Equal(removable, QueueRowRules.Removable(viewer, itemId));
    }

    [Fact]
    public void A_row_drags_on_its_own_only_when_no_reorder_lane_does()
    {
        Assert.False(QueueRowRules.RowDrags(viewer: false));   // the lane's wrapper is the drag source
        Assert.True(QueueRowRules.RowDrags(viewer: true));     // a viewed queue has no lane
    }

    // ── the "…" button ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_queue_surface_grows_a_menu_button()
    {
        // The rows (both skins), the now-playing card and the NPV's Next up are right-click only: the surface's "…"
        // overlays the row's end, which here is the ✕ or the heart.
        Assert.False(QueueRowRules.ShowsMenuButton);
    }

    [Fact]
    public void The_queue_shapes_show_no_trailing_menu_even_with_a_menu_attached()
    {
        // What the surface does with the rule: the menu stays attached (right-click), the "…" — overlay or lane — does not
        // exist. The row shape, the now-playing card's tile shape and the NPV's 52-floor Next up shape.
        SurfaceShape[] shapes =
        [
            Shape.Row(34f) with { MinHeight = QueueRowRules.SurfaceExtent(34f) },
            Shape.RowTile with { ArtEdge = 44f },
            Shape.Row(40f) with { MinHeight = 52f },
        ];
        foreach (var shape in shapes)
        {
            Assert.False(SurfaceRules.ShowsMenuTrailing(shape, hasMenu: true, showMenu: QueueRowRules.ShowsMenuButton));
            Assert.False(SurfaceRules.MenuReservesWidth(shape, hasMenu: true, showMenu: QueueRowRules.ShowsMenuButton));
        }
    }

    // ── which adapter, which click ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EntityKind.Track)]
    [InlineData(EntityKind.Episode)]
    [InlineData(EntityKind.Unknown)]
    public void A_row_whose_title_is_unknown_is_a_seed_face_whatever_it_is(EntityKind kind)
    {
        Assert.Equal(QueueRowRules.Face.Seed, QueueRowRules.FaceOf(kind, thin: true));
    }

    [Fact]
    public void A_known_row_goes_through_its_own_entity_adapter()
    {
        Assert.Equal(QueueRowRules.Face.Track, QueueRowRules.FaceOf(EntityKind.Track, thin: false));
        Assert.Equal(QueueRowRules.Face.Episode, QueueRowRules.FaceOf(EntityKind.Episode, thin: false));
    }

    [Fact]
    public void The_playing_card_opens_its_context_when_it_has_a_page_and_is_display_only_when_it_has_none()
    {
        Assert.Equal(QueueRowRules.NowPlayingClick.OpenContext, QueueRowRules.NowPlayingClickOf(hasPage: true));
        Assert.Equal(QueueRowRules.NowPlayingClick.None, QueueRowRules.NowPlayingClickOf(hasPage: false));
    }
}
