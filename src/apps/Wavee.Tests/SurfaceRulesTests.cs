// ── Wavee.Tests/SurfaceRulesTests.cs — the shared media surface's pure rules ─────────────────────────────────────────
//
// Every decision the ONE surface (`Controls.Surface`, Platform/Surface.Host.cs) makes is a function in
// Platform/Surface.Rules.cs, and it is the function that is pinned here: who owns the click, the cursor, when the chrome
// mounts, where the "…" goes, the tooltip policy, the plate table, the presets' derived dials, the extents the renderer
// and every estimator share, the props/data equality the host gates on, and the two "is this text trimmed" proxies.
//
// No window, no loop, no element is rendered. Behaviour only the engine can establish (the roving tab stop, a slot-root
// tap invoking, hover within, routed focus) is the engine's gates' job (`gate.shelf.keyboard.*`, `gate.virt.*`).
// The CardChromeRules facts (Mounted / Hot / FocusWithin) live with the other card facts in ControlsTests.cs.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SurfaceRulesTests
{
    // ── ownership and the cursor ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ownership_free_with_click_owns_click_focus_and_role()
    {
        var m = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        Assert.Equal(new SurfaceOwnership(OwnsClick: true, OwnsFocus: true, InSlot: false), m);
        Assert.Equal(AutomationRole.Button, m.Role);
    }

    [Fact]
    public void Ownership_in_slot_owns_nothing_and_has_no_role()
    {
        // ONE RELEASE, ONE OWNER: the slot root is the gesture owner, the tab stop and the Button.
        var m = SurfaceRules.Ownership(inSlot: true, hasClick: true);
        Assert.Equal(new SurfaceOwnership(OwnsClick: false, OwnsFocus: false, InSlot: true), m);
        Assert.Equal(AutomationRole.None, m.Role);
    }

    [Fact]
    public void Ownership_free_without_click_is_display_only()
    {
        // `CardData.OnClick` null is what the host feeds as `hasClick: false` — the inert sidebar entry and the queue's
        // playing card with no page to open.
        var m = SurfaceRules.Ownership(inSlot: false, hasClick: false);
        Assert.Equal(new SurfaceOwnership(OwnsClick: false, OwnsFocus: false, InSlot: false), m);
        Assert.False(m.OwnsClick);                         // no click handler
        Assert.False(m.OwnsFocus);                         // no tab stop
        Assert.Equal(AutomationRole.None, m.Role);         // no Button role
        Assert.Equal(CursorId.Arrow, SurfaceRules.Cursor(in m));   // no hand
    }

    [Fact]
    public void Cursor_is_hand_whenever_invokable()
    {
        Assert.Equal(CursorId.Hand, SurfaceRules.Cursor(SurfaceRules.Ownership(inSlot: false, hasClick: true)));
        Assert.Equal(CursorId.Hand, SurfaceRules.Cursor(SurfaceRules.Ownership(inSlot: true, hasClick: true)));
        Assert.Equal(CursorId.Hand, SurfaceRules.Cursor(SurfaceRules.Ownership(inSlot: true, hasClick: false)));
        Assert.Equal(CursorId.Arrow, SurfaceRules.Cursor(SurfaceRules.Ownership(inSlot: false, hasClick: false)));
    }

    // ── chrome, menu placement, tooltip ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false, PlayReveal.Reveal, true, true)]     // hot
    [InlineData(false, true, PlayReveal.Reveal, true, true)]     // relating: the equalizer pill shows untouched
    [InlineData(false, false, PlayReveal.Reveal, true, false)]   // cold, unrelated: nothing mounted
    [InlineData(false, false, PlayReveal.Always, true, true)]    // a video: the FAB at rest
    [InlineData(false, false, PlayReveal.Always, false, false)]  // ...but only when there is something to play
    [InlineData(true, false, PlayReveal.Always, false, true)]
    public void Chrome_mounts_hot_or_relating_or_always_play(bool hot, bool relates, PlayReveal play, bool hasPlay,
                                                             bool mounted)
        => Assert.Equal(mounted, SurfaceRules.ChromeMounted(hot, relates, play, hasPlay));

    [Fact]
    public void Menu_placement_follows_the_shape()
    {
        Assert.True(SurfaceRules.ShowsMenuCorner(Shape.Grid, hasMenu: true, showMenu: true));
        Assert.False(SurfaceRules.ShowsMenuTrailing(Shape.Grid, hasMenu: true, showMenu: true));
        Assert.True(SurfaceRules.ShowsMenuTrailing(Shape.Row(48f), hasMenu: true, showMenu: true));
        Assert.False(SurfaceRules.ShowsMenuCorner(Shape.Row(48f), hasMenu: true, showMenu: true));
        Assert.False(SurfaceRules.ShowsMenuCorner(Shape.RailTile, hasMenu: true, showMenu: true));
        Assert.False(SurfaceRules.ShowsMenuTrailing(Shape.RailTile, hasMenu: true, showMenu: true));
        Assert.False(SurfaceRules.ShowsMenuCorner(Shape.Grid, hasMenu: true, showMenu: false));
        Assert.False(SurfaceRules.ShowsMenuTrailing(Shape.Row(48f), hasMenu: true, showMenu: false));
        Assert.False(SurfaceRules.ShowsMenuCorner(Shape.Shelf(), hasMenu: false, showMenu: true));
    }

    [Fact]
    public void Label_less_shapes_always_tip()
    {
        Assert.True(SurfaceRules.TitleTip(trimmed: false, hasLabels: false));
        Assert.False(SurfaceRules.TitleTip(trimmed: false, hasLabels: true));
        Assert.True(SurfaceRules.TitleTip(trimmed: true, hasLabels: true));
        Assert.False(Shape.RailTile.Labels);
    }

    // ── the plate ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plate_table()
    {
        Assert.Equal(new PlateRules(HasRootFill: true, HasStroke: true, Dashed: false, StrokeFollowsPlayback: true),
                     PlateRules.Of(PlateKind.Tile));
        Assert.Equal(new PlateRules(HasRootFill: false, HasStroke: true, Dashed: true, StrokeFollowsPlayback: false),
                     PlateRules.Of(PlateKind.Outline));
        Assert.Equal(new PlateRules(false, false, false, false), PlateRules.Of(PlateKind.CardPlate));
        Assert.Equal(new PlateRules(false, false, false, false), PlateRules.Of(PlateKind.ListRow));
    }

    [Theory]
    [InlineData(PlateKind.CardPlate)]
    [InlineData(PlateKind.ListRow)]
    [InlineData(PlateKind.Tile)]
    [InlineData(PlateKind.Outline)]
    public void The_fill_table_has_exactly_the_parts_its_rules_name(PlateKind kind)
    {
        var rules = PlateRules.Of(kind);
        var plate = SurfacePlate.For(kind);
        Assert.Equal(rules.HasRootFill, plate.RestOpacity == 1f);   // only the tile rests on an opaque fill
        Assert.Equal(rules.HasStroke, plate.StrokeWidth > 0f);
        Assert.Equal(rules.Dashed, plate.Dash > 0f);
        Assert.Equal(rules.StrokeFollowsPlayback, plate.FollowsPlayback);
        Assert.True(plate.PressedFill.A > 0f);                      // every kind deepens while held
    }

    // ── the presets ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Presets_derive_fab_and_floor_from_the_art_edge()
    {
        Assert.Equal(30f, Shape.FabFor(48f));
        Assert.Equal(30f, Shape.FabFor(56f));
        Assert.Equal(44f, Shape.FabFor(84f));
        // The shared row's 64 floor for every row up to a 48 square, then the art plus its 2 × 8 padding.
        Assert.Equal(64f, Shape.RowFloorFor(40f));
        Assert.Equal(64f, Shape.RowFloorFor(48f));
        Assert.Equal(72f, Shape.RowFloorFor(56f));
        Assert.Equal(100f, Shape.RowFloorFor(84f));
        Assert.Equal(64f, Shape.Row(40f).MinHeight);
        Assert.Equal(30f, Shape.Row(48f).Fab);
        // The hero row pins its own 112 floor over the derived one.
        Assert.Equal(112f, Shape.RowLarge.MinHeight);
        Assert.Equal(44f, Shape.RowLarge.Fab);
        Assert.True(Shape.RowLarge.IsLargeRow);
        Assert.Equal(72f, Shape.EpisodeRow.MinHeight);
        Assert.Equal(2, Shape.EpisodeRow.TitleLines);
        Assert.Equal(30f, Shape.EpisodeRow.Fab);
        Assert.False(Shape.EpisodeRow.IsLargeRow);
    }

    [Fact]
    public void Presets_are_values()
    {
        Assert.Equal(Shape.Row(48f), Shape.Row(48f));
        Assert.NotEqual(Shape.RowTile, Shape.Row(48f));
        Assert.Equal(PlateKind.Tile, Shape.RowTile.Plate);
        Assert.Equal(PlateKind.Outline, Shape.RowOutline.Plate);
        Assert.Equal(PlayReveal.Always, Shape.Video.Play);
        Assert.Equal(Shape.Grid, Shape.Grid with { });             // the NaN dials compare equal to themselves
        Assert.Equal(Shape.Grid, Shape.Shelf());
        Assert.Equal(Shape.Grid with { CaptionLines = 2, MetaLine = true }, Shape.Shelf(2, metaLine: true));
        Assert.False(Shape.Grid.IsRow);
        Assert.True(Shape.Row().IsRow);
    }

    // ── the extents ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(148f)]
    [InlineData(173.3f)]
    [InlineData(188f)]
    public void Shelf_extent_is_unchanged(float cardW)
    {
        // A shelf shape's extent IS the shelf height for its caption/meta reservation — the byte-identical numbers.
        Assert.Equal(cardW + 50f, SurfaceGeometry.StackExtent(Shape.Shelf(), cardW, 1f));
        Assert.Equal(cardW + 66f, SurfaceGeometry.StackExtent(Shape.Shelf(2), cardW, 1f));
        Assert.Equal(cardW + 32f, SurfaceGeometry.StackExtent(Shape.Shelf(0), cardW, 1f));
        Assert.Equal(SurfaceGeometry.ShelfHeight(cardW), SurfaceGeometry.StackExtent(Shape.Shelf(2), cardW, 1f));
    }

    [Fact]
    public void A_wide_shelf_tile_extent_is_unchanged()
        => Assert.Equal(232f + 84f, SurfaceGeometry.StackExtent(Shape.Shelf(1, metaLine: true), 428f, Design.Size.WideTileAspect));

    [Fact]
    public void A_wrapping_stack_title_reserves_its_extra_lines()
        => Assert.Equal(SurfaceGeometry.StackExtent(Shape.Shelf(), 160f, 1f) + SurfaceGeometry.CardTitleLineH,
                        SurfaceGeometry.StackExtent(Shape.Shelf() with { TitleLines = 2 }, 160f, 1f));

    [Fact]
    public void A_fluid_grid_card_leaves_its_extent_to_the_layout()
        => Assert.True(float.IsNaN(SurfaceGeometry.StackExtent(Shape.Grid, float.NaN, 1f)));

    [Fact]
    public void Grid_row_estimate_seeds_the_section_grid()
    {
        Assert.Equal(176f + 28f + 20f + 18f, SurfaceGeometry.GridRowEstimate(176f, Shape.Grid, hasSubtitle: true));
        Assert.Equal(176f + 28f + 40f, SurfaceGeometry.GridRowEstimate(176f, Shape.Grid with { TitleLines = 2 }, hasSubtitle: false));
    }

    [Fact]
    public void A_row_height_is_its_floor()
    {
        Assert.Equal(64f, SurfaceGeometry.RowHeight(Shape.Row(48f)));
        Assert.Equal(72f, SurfaceGeometry.RowHeight(Shape.EpisodeRow));
        Assert.Equal(112f, SurfaceGeometry.RowHeight(Shape.RowLarge));
    }

    [Fact]
    public void The_row_geometry_constants_are_the_spacing_tokens()
    {
        // SurfaceGeometry restates the tokens as whole-DIP literals (it is engine-free); these pin the two.
        Assert.Equal(Spacing.S, SurfaceGeometry.RowPad);
        Assert.Equal(Spacing.M, SurfaceGeometry.RowGap);
    }

    // ── the host's equality gates ────────────────────────────────────────────────────────────────────────────────────

    // Each call returns FRESH delegates and a FRESH subtitle element with the same data — the shape a re-rendering
    // adapter hands over.
    static Controls.CardData Card(string title = "Blue")
        => new("spotify:album:1", title, new TextEl("Joni Mitchell · 1971") { Size = 12f, MaxLines = 1 },
               "https://i.scdn.co/image/x", OnClick: () => { }, OnPlay: () => { });

    [Fact]
    public void SurfaceProps_equality_is_data_shape_width()
    {
        Assert.Equal(new Controls.SurfaceProps(Card(), Shape.Grid), new Controls.SurfaceProps(Card(), Shape.Grid));
        Assert.Equal(new Controls.SurfaceProps(Card(), Shape.Shelf(), 148f), new Controls.SurfaceProps(Card(), Shape.Shelf(), 148f));
        Assert.NotEqual(new Controls.SurfaceProps(Card(), Shape.Grid), new Controls.SurfaceProps(Card(), Shape.Row()));
        Assert.NotEqual(new Controls.SurfaceProps(Card(), Shape.Shelf(), 148f), new Controls.SurfaceProps(Card(), Shape.Shelf(), 172f));
        Assert.NotEqual(new Controls.SurfaceProps(Card(), Shape.Grid), new Controls.SurfaceProps(Card(title: "Clouds"), Shape.Grid));
        // NaN — every fluid/row shape's width — equals NaN, or every re-push of a grid card would render it.
        Assert.Equal(new Controls.SurfaceProps(Card(), Shape.Grid, float.NaN), new Controls.SurfaceProps(Card(), Shape.Grid));
        Assert.Equal(new Controls.SurfaceProps(Card(), Shape.Grid).GetHashCode(),
                     new Controls.SurfaceProps(Card(), Shape.Grid).GetHashCode());
    }

    [Fact]
    public void CardData_slots_and_highlight_are_data()
    {
        var d = Card();
        Assert.NotEqual(d, d with { Highlight = (0, 3) });
        Assert.Equal(d with { Highlight = (0, 3) }, Card() with { Highlight = (0, 3) });
        // Two equal eyebrow runs are equal data; a different run is not. So are the other three slots.
        Assert.Equal(d with { Eyebrow = new TextEl("Lyrics match") { Size = 11f } },
                     Card() with { Eyebrow = new TextEl("Lyrics match") { Size = 11f } });
        Assert.NotEqual(d with { Eyebrow = new TextEl("Lyrics match") }, d with { Eyebrow = new TextEl("Video") });
        Assert.NotEqual(d, d with { MetaRow = new TextEl("E") });
        Assert.NotEqual(d, d with { Trailing = new TextEl("3:42") });
        Assert.NotEqual(d, d with { Below = new TextEl("12 min left") });
        Assert.NotEqual(d, d with { IsSeed = true });
        // A drop target counts by PRESENCE: two fresh specs (fresh handlers) are the same data.
        Assert.Equal(d with { Drop = new DropTargetSpec(["track"]) }, Card() with { Drop = new DropTargetSpec(["track"]) });
        Assert.NotEqual(d, d with { Drop = new DropTargetSpec(["track"]) });
        Assert.Equal((d with { Highlight = (2, 4), Trailing = new TextEl("3:42") }).GetHashCode(),
                     (Card() with { Highlight = (2, 4), Trailing = new TextEl("3:42") }).GetHashCode());
    }

    [Fact]
    public void A_click_counts_by_presence_because_no_click_is_a_display_only_surface()
    {
        var d = Card();
        // A fresh closure is the same data (the host honours the newest through its trampoline); NO click is a different
        // surface — no hand, no role, no tab stop — so a rebind that loses or gains one must re-render the host.
        Assert.Equal(d, Card() with { OnClick = () => { } });
        Assert.NotEqual(d, d with { OnClick = null });
        Assert.Equal(d with { OnClick = null }, Card() with { OnClick = null });
        Assert.NotEqual(new Controls.SurfaceProps(d, Shape.Grid), new Controls.SurfaceProps(d with { OnClick = null }, Shape.Grid));
        Assert.Null(Controls.CardData.Seed.OnClick);       // a seed has no handlers at all
    }

    [Fact]
    public void The_seed_is_one_value()
    {
        Assert.True(Controls.CardData.Seed.IsSeed);
        Assert.Equal(Controls.CardData.Seed, Controls.CardData.Seed with { });
        Assert.NotEqual(Controls.CardData.Seed, Controls.CardData.Seed with { IsSeed = false });
    }

    // ── TextFit (the trim-tooltip proxies; library-reader plan §5) ───────────────────────────────────────────────────

    [Fact]
    public void A_single_line_run_is_trimmed_when_it_overflows_its_slot()
    {
        Assert.True(TextFit.Overflows(300f, 290f));
        Assert.False(TextFit.Overflows(289.6f, 290f));   // half a DIP of slack
        Assert.False(TextFit.Overflows(100f, 0f));        // an unmeasured slot trims nothing
    }

    [Fact]
    public void A_wrapping_run_is_trimmed_when_it_needs_more_lines_than_its_cap()
    {
        Assert.True(TextFit.Trimmed(3, 2));
        Assert.False(TextFit.Trimmed(2, 2));
        Assert.False(TextFit.Trimmed(1, 0));             // no cap, nothing to trim
    }
}
