// ── Themes/Zune/Structure/Rules/ZunePolicies.Stage.cs — the fullscreen stage's policy under Zune (P5 Z4b, tiers ZT3) ─────────────────
//
// The core allocator budgets the stage from Zune's metrics (`ZuneStageTiers.Policy`, `Rules\Tiers.Stage.cs`): the full-bleed plate (gutter
// 0) at every tier, 152 (the Compact stack) / 96 / 112 / 120 / 280 (Huge 4K, read from the stage's own `stage.huge4k` rung through
// `StagePolicy.HugeWide`), the identity card and the on-demand gallery on a Compact window, the Up next column beside the lyrics at Huge
// and Huge 4K. The class edges are the core's. No structure parameter moves it.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void StagePolicyFor(ZuneParameters parameters, ref StagePolicy stage) => stage = ZuneStageTiers.Policy;
}
