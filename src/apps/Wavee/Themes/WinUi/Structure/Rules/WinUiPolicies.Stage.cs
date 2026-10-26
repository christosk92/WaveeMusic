// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Stage.cs — the fullscreen stage's policy under WinUI (P6 W4b) ─────────────────────────────
//
// The core allocator budgets the stage from WinUI's metrics (`WinUiStageTiers.Policy`, `Rules\Tiers.Stage.cs`): the 196 stacked transport in
// the Compact sheet, the 104 / 112 / 128 card at 16 / 24 / 32, the lyrics at 20 / 22 / 28 / 36, the Up next column beside the lyrics at Huge,
// the gallery only on request at every tier, and the layouts each window tier allows (W-Tiers: Compact = Card, Small = Card | Large art,
// Standard and Huge every layout). The class edges are the core's. No structure parameter moves it. W4c (Centered, Artist, video) extends the
// record, not this hook (a partial method has one body).

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void StagePolicyFor(WinUiParameters parameters, ref StagePolicy stage) => stage = WinUiStageTiers.Policy;
}
