// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Overlays.cs — what WinUI decides for the overlays the core composes (themes plan 5.5, 6.2; P6 W8a) ───
//
// The OVERLAYS hook of `WinUiPolicies` (see its header): the ContentDialog card per window band (full screen below 641, capped at 588; the report form
// 760 at Standard, 588 at Small, the two-pane 1040 at Huge), the toast lane's dock above the player bar (the Mini 64 at Compact), the vacancy's fit, the
// standalone selection bar, and context menus as a bottom sheet at Compact. The records are `WinUiOverlayTiers`; `OverlayFit` is where the core reads them.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void OverlaysPolicy(WinUiParameters parameters, ref OverlayPolicy overlays) => overlays = WinUiOverlayTiers.Policy;
}
