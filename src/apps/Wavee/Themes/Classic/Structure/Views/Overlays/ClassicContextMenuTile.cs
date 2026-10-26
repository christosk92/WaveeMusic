// ── Themes/Classic/Structure/Views/Overlays/ClassicContextMenuTile.cs — Classic's `Menu.Context` (themes plan 6.1, T11a) ─────────────
//
// The identity strip above a context menu: a 38-DIP tile (r6, or a 19 circle for an artist) beside a title and a subtitle. The menu itself
// (the command bar or the plain vertical menu, its rows, its header strip's text) is the engine's `ContextMenu` / `MenuFlyout`; the tile is
// the one picture the app hands it (`Actions.Menu.Header`). A caller with its own art (the Liked Songs treatment) hands a leading element and
// never reaches this key; a null url draws no tile at all.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's context menu tile.</summary>
internal static class ClassicContextMenuTile
{
    /// <summary>The registered template of <see cref="ViewKeys.MenuContext"/>.</summary>
    public static Element Template(ContextMenuTileProps props, in ViewContext ctx)
        => Controls.Artwork(props.ArtUrl, ClassicOverlayRules.MenuTile, ClassicOverlayRules.MenuTile,
            props.Circular ? ClassicOverlayRules.MenuTileCircleRadius : ClassicOverlayRules.MenuTileRadius,
            decodePx: ClassicOverlayRules.MenuTileDecodePx);
}
