// ── Themes/WinUi/Structure/Rules/WinUiNavDrawerRules.cs — the numbers of WinUI's narrow drawer (P6 W2b) ───────────────────────────
//
// The board `W-Narrow` (the right window) and `W-Shell-Compact` / `W-Shell-Small`: the 280 pane slides over the page from the toolbar down to the
// player as a light-dismiss overlay (acrylic, the flyout shadow, a 1-px edge, the content side's top corner rounded 8), never pushing the page.
// `Views\Drawer\WinUiDrawer.cs` reads them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's narrow drawer.</summary>
internal static class WinUiNavDrawerRules
{
    /// <summary>The scrim of an open drawer: a light smoke (the page stays readable behind the pane), and the non-dismissing one of a PINNED drawer
    /// (editing in a forced band: the page behind stays usable).</summary>
    public static readonly ColorF Scrim = ColorF.FromRgba(0, 0, 0, 0x33);
    public static readonly ColorF PinnedScrim = new(0f, 0f, 0f, 0.2f);

    /// <summary>The pane's corners: square on the window edge and at the foot, 8 at its top content side (the board's border-top-right-radius).</summary>
    public static CornerRadius4 PaneCorners => new(0f, 8f, 0f, 0f);

    /// <summary>The pane's edge.</summary>
    public const float PaneBorderWidth = 1f;
}
