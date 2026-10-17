// ── Themes/Zune/Structure/Rules/ZunePolicies.Stage.cs — the fullscreen stage's policy under Zune (P5 Z4b) ─────────────────────────
//
// The core allocator budgets the stage from Zune's metrics (`ZuneStageTiers.Policy`): the full-bleed 112 plate (gutter 0) on the desktop
// tiers, the core's identity card and stacked card on a Compact window. The class edges are the core's. No structure parameter moves it.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void StagePolicyFor(ZuneParameters parameters, ref StagePolicy stage) => stage = ZuneStageTiers.Policy;
}
