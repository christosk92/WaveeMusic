// ── Wavee.Tests/ClassicQueueRulesTests.cs — Classic's queue row decisions (themes plan 6.1, T7d) ───────────────────────────────
//
// The rail's Modern queue rows and its now-playing card are the shared media surface (`Controls.Surface` over `Track.RowData` /
// `Episode.RowData`, `Themes/Classic/Structure/Views/Queue/ClassicQueue.cs`); everything the structure DECIDES lives in `ClassicQueueRules` —
// the row's height under each skin (the reorder lane's slot maths needs it exact, and the surface's floor is not it), the autoplay dim, which
// adapter feeds a row and where a click on the playing card goes — and that is what is pinned here. What the core owns (the ✕, the row's own
// drag, no "…" button) is `QueueRowRulesTests`. The element trees (the plate, the trailing cluster, the seed face) are the surface's own facts
// (SurfaceRulesTests) and the live check's.
//
// No window, no loop, no table is touched.

using FluentGpu.Dsl;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class ClassicQueueRulesTests
{
    // ── the row's height ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_text_column_is_a_title_line_the_label_gap_and_a_caption_line()
    {
        // 20 + 2 + 16: what a row with artists stacks beside its art, from the surface's own geometry constants.
        Assert.Equal(38f, ClassicQueueRules.TextColumn);
    }

    [Theory]
    [InlineData(32f)]   // the hidden-artwork play-glyph square
    [InlineData(34f)]   // the queue's art
    [InlineData(38f)]   // exactly the text column
    public void A_small_art_leaves_the_text_column_to_govern_the_row_height(float art)
    {
        // 8 padding above and below the 38 text column.
        Assert.Equal(54f, ClassicQueueRules.SurfaceExtent(art));
    }

    [Fact]
    public void A_large_art_governs_the_row_height()
    {
        Assert.Equal(76f, ClassicQueueRules.SurfaceExtent(60f));
    }

    [Fact]
    public void The_surface_default_floor_is_taller_than_the_queue_row_so_the_shape_states_its_own()
    {
        // `Shape.Row(34)` floors at 64: a queue row built on the preset alone would be 10 taller than the lane's slots.
        Assert.True(Shape.RowFloorFor(34f) > ClassicQueueRules.SurfaceExtent(34f));
        var shape = Shape.Row(34f) with { MinHeight = ClassicQueueRules.SurfaceExtent(34f) };
        Assert.Equal(54f, shape.MinHeight);
    }

    [Fact]
    public void The_extent_follows_the_skin()
    {
        Assert.Equal(44f, ClassicQueueRules.Extent(hairline: true, artEdge: 34f));
        Assert.Equal(ClassicQueueRules.HairlineExtent, ClassicQueueRules.Extent(hairline: true, artEdge: 60f));
        Assert.Equal(54f, ClassicQueueRules.Extent(hairline: false, artEdge: 34f));
    }

    [Fact]
    public void The_autoplay_header_adds_its_hint_line_to_the_header_slot()
    {
        Assert.Equal(Spacing.M + ClassicQueueRules.HeaderRowH + Spacing.XS, ClassicQueueRules.HeaderExtent);
        Assert.Equal(ClassicQueueRules.HeaderExtent + Spacing.XXS + 16f, ClassicQueueRules.HeaderSubExtent);
    }

    // ── the dim ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(QueueSection.Queue, 1f)]
    [InlineData(QueueSection.NextUp, 1f)]
    [InlineData(QueueSection.Autoplay, 0.72f)]
    public void Only_the_autoplay_section_is_dimmed(QueueSection section, float opacity)
    {
        Assert.Equal(opacity, ClassicQueueRules.Dim(section));
    }

    // ── the "…" button ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_queue_shapes_show_no_trailing_menu_even_with_a_menu_attached()
    {
        // What the surface does with the rule: the menu stays attached (right-click), the "…" — overlay or lane — does not
        // exist. The row shape, the now-playing card's tile shape and the NPV's 52-floor Next up shape.
        SurfaceShape[] shapes =
        [
            Shape.Row(34f) with { MinHeight = ClassicQueueRules.SurfaceExtent(34f) },
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
        Assert.Equal(ClassicQueueRules.Face.Seed, ClassicQueueRules.FaceOf(kind, thin: true));
    }

    [Fact]
    public void A_known_row_goes_through_its_own_entity_adapter()
    {
        Assert.Equal(ClassicQueueRules.Face.Track, ClassicQueueRules.FaceOf(EntityKind.Track, thin: false));
        Assert.Equal(ClassicQueueRules.Face.Episode, ClassicQueueRules.FaceOf(EntityKind.Episode, thin: false));
    }

    [Fact]
    public void The_playing_card_opens_its_context_when_it_has_a_page_and_is_display_only_when_it_has_none()
    {
        Assert.Equal(ClassicQueueRules.NowPlayingClick.OpenContext, ClassicQueueRules.NowPlayingClickOf(hasPage: true));
        Assert.Equal(ClassicQueueRules.NowPlayingClick.None, ClassicQueueRules.NowPlayingClickOf(hasPage: false));
    }

    // ── the stage column ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stage_rows_are_authored_at_the_boards_sizes()
    {
        Assert.Equal(56f, ClassicQueueRules.StageRowH);
        Assert.Equal(104f, ClassicQueueRules.StageNextH);
        Assert.Equal(40f, ClassicQueueRules.StageHeaderH);
        Assert.Equal(0.68f, ClassicQueueRules.StageAutoplayDim);
    }
}
