// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Player.cs — the play bar's and the panel's policies under WinUI (P6 W4a) ─────────────────
//
// Two family hooks of `WinUiPolicies` (see its header), both from `Tiers.Player.cs`:
//
//   · PLAYER BAR: `WinUiPlayerTiers.Policy`: Mini 0 / Compact 641 / Medium 860 / Full 1100 / Huge 1920 with the 24 hold, the reserved 72, and
//     the levels each window tier may use.
//   · PANEL: `WinUiPlayerTiers.Panel`: 340, resizable 280-480, a sheet at Compact, docked where nav + 340 + 480 fits at Small and Standard
//     (an acrylic overlay at the same width below that line), two linked tabbed panes at Huge.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void PlayerPolicy(WinUiParameters parameters, ref PlayerBarPolicy playerBar) => playerBar = WinUiPlayerTiers.Policy;

    static partial void PanelPolicyFor(WinUiParameters parameters, ref PanelPolicy panel) => panel = WinUiPlayerTiers.Panel;
}
