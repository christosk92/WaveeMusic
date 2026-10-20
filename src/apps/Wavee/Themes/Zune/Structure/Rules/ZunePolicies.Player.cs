// ── Themes/Zune/Structure/Rules/ZunePolicies.Player.cs — the play bar's and the panel's policies under Zune (P5 Z4a, ZT2) ───────────
//
// Two family hooks of `ZunePolicies` (see its header):
//
//   · PLAYER BAR: `ZunePlayerTiers.Policy`: the six levels 0 / 640 / 760 / 1240 / 1920 / 3200 with the 24 hold, the reserved 72, and the
//     levels each window tier may use. The `seekTimes` parameter moves no level (the bar's view places the times), so the policy is one.
//   · PANEL: 340 wide and never resized (Min = Max, Z2a's hand-off), the full content height, the mini player's core bounds, and the
//     placement and the pane arrangement per window tier from `panelMode` and `maxDockedPanes` (`ZunePanelTiers.PanelRules`: a sheet at
//     Compact, floating at Small, inline at Standard, queue | lyrics · friends docked at Huge, queue | lyrics | friends at Huge 4K, each
//     column the 360 floor; always floating with `AlwaysOverlay`; `maxDockedPanes` caps the columns).

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void PlayerPolicy(ZuneParameters parameters, ref PlayerBarPolicy playerBar) => playerBar = ZunePlayerTiers.Policy;

    static partial void PanelPolicyFor(ZuneParameters parameters, ref PanelPolicy panel)
        => panel = new PanelPolicy(ZunePanelTiers.PanelW, ZunePanelTiers.PanelW, FullHeight: true, PanelPolicy.Default.Pip with { OverlaysContent = true })
        {
            Tiers = ZunePanelTiers.PanelRules(parameters.PanelMode, parameters.MaxDockedPanes),
            DockGap = ZunePanelTiers.DockGap, PaneGap = ZunePanelTiers.DockGap,
        };
}
