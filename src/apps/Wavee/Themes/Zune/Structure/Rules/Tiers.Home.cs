// ── Themes/Zune/Structure/Rules/Tiers.Home.cs — Zune's discovery rungs (themes plan 3.2, 6.3; P5 ZT4) ────────────────────────────────
//
// ONE board mark that is not a tier: the HEIGHT at which Home's daylist stops being a strip and folds to one 56 row (Z-Shell-Short: "330 tall:
// band collapses to 52 at the first scroll, daylist to one row"). The Short height tier (below 700) is too coarse for it: the Small board is 800 x
// 600 (Short by the ladder) and keeps its 112 daylist row, while the 330 board folds it. So the daylist reads a window-height RUNG,
// `ZuneHomeRules.DaylistRung`, crossed from 480 on widening and held 24 on shrinking (a lane assumption between the two boards); the record that
// answers it is `ZuneHomeTiers.Of`.
//
// A second, on the CONTENT axis: the width at which the daylist's wide strip (art | copy | clock) has room for its copy, `ZuneHomeRules.DaylistStripRung`
// (1280; under it the Standard record bleeds the art under a full-width copy, as Small does).
//
// Home's, Browse's and Search's width records are content-tier records (`ZuneHomeTiers`, `ZuneBrowseTiers`, `ZuneSearchTiers`); they add no
// ladder. This hook runs inside `ZuneTiers.Policy`'s initializer, so it reads only constants of `ZuneHomeRules` (inlined at compile time): it must
// never touch a class whose initializer reads `ZuneTiers` (`ZuneHomeTiers`, the frame records).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZuneTiers
{
    static partial void RungsDiscovery(ImmutableArray<NamedRung>.Builder rungs)
    {
        rungs.Add(new NamedRung(ZuneHomeRules.DaylistRung, new TierEdge(ZuneHomeRules.DaylistFullFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Height));
        rungs.Add(new NamedRung(ZuneHomeRules.DaylistStripRung, new TierEdge(ZuneHomeRules.DaylistStripFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Content));
    }
}
