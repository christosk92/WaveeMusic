// ── Themes/Zune/Structure/Rules/ZunePolicies.Player.cs — the play bar's and the panel's policies under Zune (P5 Z4a) ─────────────────
//
// Two family hooks of `ZunePolicies` (see its header):
//
//   · PLAYER BAR: `ZunePlayerTiers.Policy`: the six levels 0 / 640 / 760 / 1240 / 1920 / 3200 with the 24 hold, the reserved 72, and the
//     levels each window tier may use. The `seekTimes` parameter moves no level (the bar's view places the times), so the policy is one.
//   · PANEL: 340 wide and never resized (Min = Max, Z2a's hand-off), the full content height, the mini player's core bounds, and the
//     placement per window tier from `panelMode` and `maxDockedPanes` (`ZunePlayerTiers.PanelRules`: a sheet at Compact, floating at Small,
//     inline from Standard, the queue beside it from Huge; always floating with `AlwaysOverlay`). ZT2 replaces the arrangement per tier.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void PlayerPolicy(ZuneParameters parameters, ref PlayerBarPolicy playerBar) => playerBar = ZunePlayerTiers.Policy;

    static partial void PanelPolicyFor(ZuneParameters parameters, ref PanelPolicy panel)
        => panel = new PanelPolicy(ZunePlayerTiers.PanelW, ZunePlayerTiers.PanelW, FullHeight: true, PanelPolicy.Default.Pip)
        {
            Tiers = ZunePlayerTiers.PanelRules(parameters.PanelMode, parameters.MaxDockedPanes),
        };
}
