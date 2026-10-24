// ── Themes/WinUi/Structure/Views/Overlays/WinUiContextMenuTile.cs — WinUI's `Menu.Context` (themes plan 6.1, T11a) ─────────────
//
// The identity strip above a context menu: a 38-DIP tile (r6, or a 19 circle for an artist) beside a title and a subtitle. The menu itself
// (the command bar or the plain vertical menu, its rows, its header strip's text) is the engine's `ContextMenu` / `MenuFlyout`; the tile is
// the one picture the app hands it (`Actions.Menu.Header`). A caller with its own art (the Liked Songs treatment) hands a leading element and
// never reaches this key; a null url draws no tile at all.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's context menu tile.</summary>
internal static class WinUiContextMenuTile
{
    /// <summary>The registered template of <see cref="ViewKeys.MenuContext"/>.</summary>
    public static Element Template(ContextMenuTileProps props, in ViewContext ctx)
        => Controls.Artwork(props.ArtUrl, WinUiOverlayRules.MenuTile, WinUiOverlayRules.MenuTile,
            props.Circular ? WinUiOverlayRules.MenuTileCircleRadius : WinUiOverlayRules.MenuTileRadius,
            decodePx: WinUiOverlayRules.MenuTileDecodePx);
}
