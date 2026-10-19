// ── Themes/WinUi/Structure/Rules/WinUiSurfaceLayout.cs — the numbers of WinUI's media surface (themes plan 6.2; P6 W3) ─────
//
// What `Platform/Surface.Parts.cs` wrote as a literal beside the selected skin, moved verbatim with the surface's picture.
// `Lists\WinUiSurface.cs` reads it (WVTH003 allows literals only here, in `Rules\**`). The shelf's and the grid's geometry the
// extents also read (`SurfaceGeometry`: the plate padding, the gutter, the label gap, the row's pad and gap) are the core's: the
// estimators, the seed face and the live body must agree to the DIP.

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's media surface.</summary>
internal static class WinUiSurfaceLayout
{
    /// <summary>The selected skin's accent border: 2 DIP on the shell root, over the brighter card fill.</summary>
    public const float SelectedBorder = 2f;
}
