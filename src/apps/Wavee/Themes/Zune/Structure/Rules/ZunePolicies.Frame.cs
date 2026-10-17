// ── Themes/Zune/Structure/Rules/ZunePolicies.Frame.cs — the frame's policy hook (themes plan 5.5, 6.3; P5 ZT1) ──────────────────────
//
// The title row's budget per window tier (`ZuneFrameTiers.Chrome`): the core's allocator answers the form Zune's title bar draws in each tier
// (the magnifier at Compact and Small, the pill from Standard up), so the search focus survives the 1008 swap. See `Tiers.Frame.cs`.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void FramePolicy(ZuneParameters parameters, ref ChromePolicy chrome) => chrome = ZuneFrameTiers.Chrome;
}
