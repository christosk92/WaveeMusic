// ── Theming/Roles/SpaceRoles.cs — the page-rhythm, shelf and flyout geometry a style decides (themes plan 5.4) ───────────
//
// What was `Design.Size`'s page-level half and `Design.Dock.Reserve`. A space role is a FLOAT a view reads where it builds
// its layout (`R.Space.SectionGap`), never into a `const` or a field: a different style publishes a different table and the
// next render lays out with it.
//
// WHAT STAYED IN `Design.Size` (and why). The rest of the old ladder is read in places that need a compile-time number: a
// default parameter (`Controls.IconButtonSize`, `Track.RowMetrics.ThumbSize`), a persisted-key default (the two rail widths,
// pinned to `Platform.Keys` by a test), a `const` alias (`Detail.Breakpoints.ShellChromeAllowanceDip`, `Zones.TileArt`), a
// BCL-only twin of the zoom policy (`DesignW`/`DesignH`) or a `static readonly` table (the track-row lanes). Those are control,
// row, pane, rail, thumb and player-bar geometry: each leaves `Design.Size` with the family that owns it (T3 policies and
// metrics, T5 nav, T6 player, T8 rows, T9 rails), which takes it from a policy or a metric and not from a role.

namespace Wavee.Theming.Roles;

/// <summary>The space roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="SectionGap">The vertical rhythm BETWEEN page sections and feed modules at desktop widths. Home's module gap and the
/// artist page's section stack read the same role, so a section boundary is the same distance in both places.</param>
/// <param name="SectionGapWide">The wide rung of the same rhythm (Home's module stack).</param>
/// <param name="PageMaxW">The widest a page's content column grows before it stops tracking the window. The detail frame, the
/// artist page and Home all cap their rows here, so the three line up on an ultra-wide display.</param>
/// <param name="ShelfCardMin">The horizontal shelf's card-width band, lower edge.</param>
/// <param name="ShelfCardMax">The horizontal shelf's card-width band, upper edge.</param>
/// <param name="GridGap">The gap of a card grid.</param>
/// <param name="WideTileMin">The WIDE tile's card-width band (a 16:9 header image over the same card plate), lower edge.</param>
/// <param name="WideTileMax">The WIDE tile's card-width band, upper edge.</param>
/// <param name="WideTileAspect">The WIDE tile's cover ratio.</param>
/// <param name="FadeChip">The edge feather of a chip rail.</param>
/// <param name="FadeShelf">The edge feather of a card shelf.</param>
/// <param name="FadeRail">The edge feather of the right rail (the engine default).</param>
/// <param name="FacePileFlyoutW">The "...and N more" attribution flyout hung off a face pile: its width.</param>
/// <param name="FacePileFlyoutH">The flyout's height.</param>
/// <param name="FacePileListW">The flyout's list width.</param>
/// <param name="FacePileListH">The flyout's list height.</param>
/// <param name="DockReserve">The height the bottom player dock covers. A page reserves it under its last row so that row
/// clears the transport; it is also the wash host's bottom margin.</param>
public sealed record SpaceRoles(
    float SectionGap, float SectionGapWide, float PageMaxW,
    float ShelfCardMin, float ShelfCardMax, float GridGap,
    float WideTileMin, float WideTileMax, float WideTileAspect,
    float FadeChip, float FadeShelf, float FadeRail,
    float FacePileFlyoutW, float FacePileFlyoutH, float FacePileListW, float FacePileListH,
    float DockReserve);
