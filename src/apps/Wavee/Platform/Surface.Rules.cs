// ── Platform/Surface.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────
// The shared media surface's VALUES and pure RULES: the shape dials and their presets, who owns a click, the cursor,
// when the chrome mounts, where the "…" goes, the plate table's engine-free twin, the extent maths the renderer and
// every estimator share, and the two "is this text trimmed" proxies.
//
// Role: CORE
// Owner: L
// Wave: 0 (shared media surface)
// Budget: 300 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.1, §3
//
// ── WHY THIS FILE IS PURE ────────────────────────────────────────────────────────────────────────────────────────────
//
// Every decision the ONE surface (`Controls.Surface`, Surface.Host.cs) makes is a function here, so the decision is
// pinned by a fact (SurfaceRulesTests) instead of by a source read. The only engine types it names are two plain
// values, `CursorId` and `AutomationRole` — the answer of the ownership rule IS a cursor and a role. Spacing literals are
// RESTATED as whole-DIP constants (`Spacing.XS` → 4) rather than imported, and a fact pins each against its token.
//
// What is NOT a dial (refused, §1.4): motion, colours, eager vs lazy chrome, the FAB size per caller, the "…" placement
// per caller, the cursor, the plate shape, an overlay slot, a tooltip policy, a selection variant. Those are derived from
// DATA presence (`CardData`) and the SHAPE below — a caller never flips an affordance off.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>How a media surface is laid out: a cover over its labels (a shelf or grid card) or art beside a text column
/// (a row). Everything else about the arrangement is a dial on <see cref="SurfaceShape"/>.</summary>
public enum SurfaceLayout : byte { Stack, Row }

/// <summary>The plate a surface sits on. ONE table (<c>SurfacePlate.For</c>) maps each kind to its fills and its stroke,
/// and ONE mechanism applies them: the root carries the stroke and the selected skin, a non-hit-testable sibling REVEAL
/// carries the hover/press fills. <see cref="CardPlate"/> = the subtle plate every card has; <see cref="ListRow"/> = the
/// same ramp on a row (the list hover every list has); <see cref="Tile"/> = the opaque card fill under a card hairline
/// that turns accent while the surface relates to playback; <see cref="Outline"/> = a dashed, fill-less "go somewhere"
/// frame.</summary>
public enum PlateKind : byte { CardPlate, ListRow, Tile, Outline }

/// <summary>Where the "…" lives: over the cover's top-right (<see cref="Corner"/>, stacks) or as a hot-revealed 32 icon
/// at the end of the trailing cluster (<see cref="Trailing"/>, rows). <see cref="None"/> = right-click only (the rail
/// tile has no room for one).</summary>
public enum MenuPlacement : byte { None, Corner, Trailing }

/// <summary><see cref="Reveal"/> = the play FAB fades in while the surface is hot (every card and row);
/// <see cref="Always"/> = the FAB is mounted and visible at rest (a video: play is the surface's whole point).</summary>
public enum PlayReveal : byte { Reveal, Always }

/// <summary>How a media surface is laid out. A VALUE: the presets on <see cref="Shape"/> are the only shapes the app
/// uses, and a caller composes <c>with</c> only for a dial that is genuinely per-site (a row's art edge, a grid's title
/// lines). Equality is the compiler's (a <c>float.Equals</c> per field, so the NaN dials compare equal to themselves).
/// <para><see cref="ArtEdge"/> is the row's art square; a stack's cover is the shelf's inner width or the grid's fluid
/// cell (NaN). <see cref="MinHeight"/> is the row's floor; NaN on a stack. <see cref="Labels"/> false = art only (the
/// rail tile): the title becomes the surface's tooltip.</para></summary>
public readonly record struct SurfaceShape(
    SurfaceLayout Layout,
    PlateKind Plate,
    float ArtEdge,
    int TitleLines,
    int CaptionLines,
    bool MetaLine,
    float Fab,
    MenuPlacement Menu,
    PlayReveal Play,
    float MinHeight,
    bool Labels)
{
    public bool IsRow => Layout == SurfaceLayout.Row;

    /// <summary>The "top result" hero row: an art square above <see cref="Shape.LargeRowEdge"/> takes the page-hero title
    /// and the wider text gap — the same threshold <see cref="Shape.FabFor"/> reads for its 44 FAB.</summary>
    public bool IsLargeRow => IsRow && ArtEdge > Shape.LargeRowEdge;
}

/// <summary>THE presets. The names are the inventory's shapes (plan §1.1); nothing else is a shape.</summary>
public static class Shape
{
    /// <summary>The fluid grid card: an <c>AspectRatio</c> cover filling its cell, one title line, one caption line, the
    /// 44 FAB and the corner "…".</summary>
    public static readonly SurfaceShape Grid = new(SurfaceLayout.Stack, PlateKind.CardPlate, float.NaN, 1, 1, false, 44f,
                                                   MenuPlacement.Corner, PlayReveal.Reveal, float.NaN, true);

    /// <summary>The shelf card — the grid card at a fixed width (the host's <c>width</c>). <paramref name="captionLines"/>
    /// and <paramref name="metaLine"/> are what the shelf's extent reserves (<see cref="SurfaceGeometry.ShelfHeight(float, float, int, bool)"/>).</summary>
    public static SurfaceShape Shelf(int captionLines = 1, bool metaLine = false)
        => Grid with { CaptionLines = captionLines, MetaLine = metaLine };

    /// <summary>A video card: the FAB at rest. The 16:9 cover is DATA (<c>CardData.CoverAspect</c>), not the shape.</summary>
    public static readonly SurfaceShape Video = Grid with { Play = PlayReveal.Always };

    /// <summary>A media row at an <paramref name="edge"/> art square: the list-row plate, the FAB and floor derived from
    /// the edge, the trailing "…".</summary>
    public static SurfaceShape Row(float edge = 48f)
        => new(SurfaceLayout.Row, PlateKind.ListRow, edge, 1, 1, false, FabFor(edge), MenuPlacement.Trailing,
               PlayReveal.Reveal, RowFloorFor(edge), true);

    /// <summary>The "top result" hero row: 84 art, the 44 FAB, the 112 floor.</summary>
    public static readonly SurfaceShape RowLarge = Row(84f) with { MinHeight = 112f, Fab = 44f, TitleLines = 1 };

    /// <summary>A row on the opaque card tile (Home's recents).</summary>
    public static readonly SurfaceShape RowTile = Row(48f) with { Plate = PlateKind.Tile };

    /// <summary>A row in the dashed outline (the listening-history tile).</summary>
    public static readonly SurfaceShape RowOutline = Row(48f) with { Plate = PlateKind.Outline };

    /// <summary>An episode row: 56 art, a two-line title, the 72 floor.</summary>
    public static readonly SurfaceShape EpisodeRow = Row(56f) with { TitleLines = 2, MinHeight = 72f };

    /// <summary>The sidebar's hero card row: 48 art, the 28 FAB, the 64 floor.</summary>
    public static readonly SurfaceShape SidebarHero = Row(48f) with { Fab = 28f, MinHeight = 64f };

    /// <summary>The sidebar's grid tile: the grid card with the 28 FAB.</summary>
    public static readonly SurfaceShape SidebarTile = Grid with { TitleLines = 1, CaptionLines = 1, Fab = 28f };

    /// <summary>The collapsed rail's tile: a 36 cover in a 40 square, no labels (the tooltip is the label), no FAB, no
    /// "…" (right-click only).</summary>
    public static readonly SurfaceShape RailTile = new(SurfaceLayout.Row, PlateKind.ListRow, 36f, 0, 0, false, 0f,
                                                       MenuPlacement.None, PlayReveal.Reveal, 40f, Labels: false);

    /// <summary>The art edge a row's 30 FAB stops at: above it the art is a hero's and carries the 44.</summary>
    public const float LargeRowEdge = 56f;

    /// <summary>The FAB a row's art carries: 30 up to a 56 square, 44 above (the "top result" hero).</summary>
    public static float FabFor(float artEdge) => artEdge > LargeRowEdge ? 44f : 30f;

    /// <summary>The row floor: the art plus the 2 × 8 padding, never under 64 — the floor every media row has always had
    /// (a 40-art cluster or module row is 64 tall, not 56). A shape that wants a tighter row states its own
    /// <see cref="SurfaceShape.MinHeight"/>.</summary>
    public static float RowFloorFor(float artEdge) => MathF.Max(RowFloor, artEdge + 2f * SurfaceGeometry.RowPad);

    /// <summary>The media row's floor: the 64 every row on <c>Controls.Surface</c> has.</summary>
    public const float RowFloor = 64f;
}

/// <summary>Who owns a surface's gesture, focus and role. <see cref="InSlot"/> = a bound <c>ItemsView</c>/<c>PagedShelf</c>
/// slot root owns them (ONE RELEASE, ONE OWNER — input-a11y.md §6.5), so the surface is click-less and focus-less inside
/// it; the slot root carries the Button role.</summary>
public readonly record struct SurfaceOwnership(bool OwnsClick, bool OwnsFocus, bool InSlot)
{
    public AutomationRole Role => OwnsClick ? AutomationRole.Button : AutomationRole.None;
}

/// <summary>The surface's ownership, cursor, chrome, menu-placement and tooltip rules — none of them a caller's choice.</summary>
public static class SurfaceRules
{
    /// <summary>Free + click = the surface owns click, focus and the Button role; in a slot = nothing (the slot root has
    /// them); free without a click = display-only (no role, no tab stop).</summary>
    public static SurfaceOwnership Ownership(bool inSlot, bool hasClick)
        => new(OwnsClick: !inSlot && hasClick, OwnsFocus: !inSlot && hasClick, InSlot: inSlot);

    /// <summary>Hand iff the surface is invokable — by itself, or through the slot root it sits in. ONE place in the app;
    /// the engine's "clickability does NOT imply the hand" stance is unchanged at the element level.</summary>
    public static CursorId Cursor(in SurfaceOwnership m) => m.OwnsClick || m.InSlot ? CursorId.Hand : CursorId.Arrow;

    /// <summary>The now-playing overlay mounts while the surface is hot or relates to playback
    /// (<see cref="Controls.CardChromeRules.Mounted"/>), and AT REST on an <see cref="PlayReveal.Always"/> shape that has
    /// something to play.</summary>
    public static bool ChromeMounted(bool hot, bool relates, PlayReveal play, bool hasPlay)
        => Controls.CardChromeRules.Mounted(hot, relates) || (play == PlayReveal.Always && hasPlay);

    /// <summary>The corner "…" exists on a <see cref="MenuPlacement.Corner"/> shape whose data has a menu (and a host to
    /// open it) and did not opt out (<c>CardData.ShowMenu</c>).</summary>
    public static bool ShowsMenuCorner(in SurfaceShape s, bool hasMenu, bool showMenu)
        => hasMenu && showMenu && s.Menu == MenuPlacement.Corner;

    /// <summary>The trailing "…" — the same gate on a <see cref="MenuPlacement.Trailing"/> shape.</summary>
    public static bool ShowsMenuTrailing(in SurfaceShape s, bool hasMenu, bool showMenu)
        => hasMenu && showMenu && s.Menu == MenuPlacement.Trailing;

    /// <summary>A title tooltip only when the title is trimmed — except on a label-less shape, where the tooltip IS the
    /// label and always exists.</summary>
    public static bool TitleTip(bool trimmed, bool hasLabels) => !hasLabels || trimmed;
}

/// <summary>The engine-free twin of <c>SurfacePlate.For</c>: which parts of the plate a kind has. The fills themselves
/// are theme tokens and live with the parts; THIS is what the tests pin.
/// <para><see cref="HasRootFill"/> = the surface rests on an opaque fill (painted as the plate sibling's REST leg, so the
/// hover leg replaces it rather than stacking over it); <see cref="StrokeFollowsPlayback"/> = the hairline turns accent
/// while the surface relates to what is playing.</para></summary>
public readonly record struct PlateRules(bool HasRootFill, bool HasStroke, bool Dashed, bool StrokeFollowsPlayback)
{
    public static PlateRules Of(PlateKind k) => k switch
    {
        PlateKind.Tile => new(true, true, false, true),
        PlateKind.Outline => new(false, true, true, false),
        _ => new(false, false, false, false),
    };
}

/// <summary>THE extent maths of the media surface. The RENDERER (<c>SurfaceParts</c>) stacks exactly these constants and
/// every ESTIMATOR (a shelf's <c>cardHeight</c>, a grid's row seed) calls these functions: an estimate that disagrees with
/// the rendered height makes a measured virtual list re-pin its scroll anchor mid-scroll, which reads as the feed
/// jumping under the cursor.</summary>
public static class SurfaceGeometry
{
    /// <summary>THE virtualized shelf's cross extent for a SQUARE-cover card whose subtitle may take TWO lines (the
    /// Artist / module / episode shelves pass subtitles capped at 2): <c>cardW + 66</c> — the general form below at
    /// aspect 1, two caption lines, no meta line: 4 gutter + 8 plate top + the square cover (w − 16) + 8 gap + 20 title +
    /// 2 + 32 subtitle + 8 plate bottom. The identity <c>ShelfHeight(w) == w + 66</c> is pinned by test.
    /// <para>The RENDERER and the ESTIMATOR must both call this. An estimate that disagrees with the rendered height
    /// makes a measured virtual list re-pin its scroll anchor mid-scroll, which reads as the feed jumping under the
    /// cursor.</para></summary>
    public static float ShelfHeight(float cardW) => ShelfHeight(cardW, 1f, captionLines: 2, metaLine: false);

    /// <summary>The EXACT shelf card extent for what the card shows — the renderer (<c>SurfaceParts.StackBody</c> +
    /// <c>SurfaceParts.Labels</c> inside <c>SurfaceParts.Gutter</c>) stacks precisely these, to the DIP:
    /// <c>ShelfGutterTop (4) + ShelfPlatePad (8) + cover + ShelfPlatePad (8) gap + title 20
    /// + (captionLines &gt; 0 ? 2 + 16·captionLines : 0) + (metaLine ? 2 + 16 : 0) + ShelfPlatePad (8) + ShelfGutterBottom (0)</c>.
    /// The cover is <paramref name="coverAspect"/> (width ÷ height) over the card's INNER width (the card less its
    /// 2 × 8 plate padding). <paramref name="captionLines"/> is the subtitle's line cap (an inline <c>CardData.Caption</c>
    /// carries its meta on those lines); <paramref name="metaLine"/> reserves the separate tertiary <c>CardData.Meta</c>
    /// line a wide tile shows. A card that shows less than its row reserves is merely shorter than the row (its plate
    /// stretches, the labels stay top-aligned).</summary>
    public static float ShelfHeight(float cardW, float coverAspect, int captionLines, bool metaLine)
    {
        // The chrome is whole DIP, summed FIRST so the cover is added exactly once (no float drift against `w + N`).
        float chrome = ShelfGutterTop + ShelfPlatePad + ShelfPlatePad + CardTitleLineH
                       + (captionLines > 0 ? CardLabelGap + CardCaptionLineH * captionLines : 0f)
                       + (metaLine ? CardLabelGap + CardCaptionLineH : 0f)
                       + ShelfPlatePad + ShelfGutterBottom;
        return CoverHeight(cardW - 2f * ShelfPlatePad, coverAspect) + chrome;
    }

    /// <summary>The shelf card's vertical chrome, read by BOTH <see cref="ShelfHeight(float, float, int, bool)"/> and the
    /// renderer so the two cannot drift: the outer gutter over the plate (4 = <c>Spacing.XS</c>) and under it (0 — the
    /// fill-only hover casts no halo that needs room), the plate's padding on every side (8 = <c>Spacing.S</c>, also the
    /// cover → labels gap), the title line (14/20), a caption line (12/16) and the labels' inter-line gap
    /// (2 = <c>Spacing.XXS</c>).</summary>
    public const float ShelfGutterTop = 4f, ShelfGutterBottom = 0f, ShelfPlatePad = 8f,
                       CardTitleLineH = 20f, CardCaptionLineH = 16f, CardLabelGap = 2f;

    /// <summary>The cover height a card's inner width derives at <paramref name="aspect"/> (width ÷ height): exactly
    /// the width when square, else rounded to whole DIP so a 16:9 tile's label block lands on the pixel grid.</summary>
    public static float CoverHeight(float inner, float aspect)
        => aspect != 1f ? MathF.Round(inner / aspect) : inner;

    /// <summary>The GRID cell's extra height above its square cover: the label block's own overhead. Shared by Home,
    /// Browse and Search, for the same estimator reason <see cref="ShelfHeight(float)"/> is.</summary>
    public static float GridCardChromeFor(int titleLines, bool hasSubtitle)
        => GridLabelOverhead + titleLines * GridTitleLineH + (hasSubtitle ? GridSubtitleBlockH : 0f);

    /// <inheritdoc cref="GridCardChromeFor"/>
    public const float GridTitleLineH = 20f, GridSubtitleBlockH = 18f, GridLabelOverhead = 28f;

    /// <summary>A row's padding on every side (8 = <c>Spacing.S</c>) and the gap between its art, its text and its
    /// trailing cluster (12 = <c>Spacing.M</c>) — the row shape's own on <c>Controls.Surface</c>.</summary>
    public const float RowPad = 8f, RowGap = 12f;

    /// <summary>A row's height for an estimator: its floor. A row whose text column runs taller (an eyebrow, two title
    /// lines, a meta row, a progress rung) grows past it, so a list of those measures instead of trusting this.</summary>
    public static float RowHeight(in SurfaceShape s) => s.MinHeight;

    /// <summary>A stack surface's extent at a known card width: <see cref="ShelfHeight(float, float, int, bool)"/> for the
    /// shape's caption/meta reservation, plus one title line per extra line a wrapping title is allowed. NaN for a fluid
    /// (grid) card — the layout measures it.</summary>
    public static float StackExtent(in SurfaceShape s, float cardW, float coverAspect)
        => float.IsNaN(cardW) ? float.NaN
           : ShelfHeight(cardW, coverAspect, s.CaptionLines, s.MetaLine) + (s.TitleLines > 1 ? (s.TitleLines - 1) * CardTitleLineH : 0f);

    /// <summary>The row-height SEED of a fluid grid of <paramref name="s"/> cards at its minimum column: the square cover
    /// plus the label chrome. The layout measures the real rows; this only keeps the first estimate honest.</summary>
    public static float GridRowEstimate(float minColW, in SurfaceShape s, bool hasSubtitle)
        => minColW + GridCardChromeFor(s.TitleLines, hasSubtitle);
}

/// <summary>The two measurable proxies for "is this text trimmed" — the engine's TextMetrics has no trimmed flag.</summary>
public static class TextFit
{
    /// <summary>A single-line run is trimmed when its natural width exceeds the slot (half a DIP of slack).</summary>
    public static bool Overflows(float naturalW, float slotW) => slotW > 0f && naturalW > slotW + 0.5f;

    /// <summary>A wrapping run is trimmed when laid out UNBOUNDED at the slot width it needs more lines than the cap.</summary>
    public static bool Trimmed(int unboundedLines, int maxLines) => maxLines > 0 && unboundedLines > maxLines;
}
