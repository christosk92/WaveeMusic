// ── Themes/Zune/Structure/Rules/ZunePolicies.Lists.cs — what Zune decides for the core's track lists (themes plan 5.5, 6.3; P5 Z3) ──
//
// Two family hooks of `ZunePolicies` (see its header):
//
//   · LISTS: `ZuneListRules.Lists`: track artwork on and ONE row skin, Modern (the core's lane sets, header height, list key and the artist
//     chart's metrics read it). Zune has no row-style or hide-artwork option, so no structure value moves it.
//   · DETAIL TIERS: the detail table's record per window tier (`ZuneListRules.DetailTiers`): the Compact row form at Compact, no Album or Plays
//     lane at Small, every lane the width admits from Standard, the density's rows everywhere ("Standard rows"). The rail, the insights and the
//     aside of the same records are the detail lanes' (Z6a1, Z6b1, ZT5): they extend `ZuneListRules.DetailTiers`' layouts (or move this hook
//     into their own file, deleting it here) rather than implementing the hook a second time (a partial method has one body).
//
// `detail` (the page layout policy) is left at the core's value: it is the detail frame lane's (Z6a1).

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void ListsPolicy(ZuneParameters parameters, ref ListPolicy lists) => lists = ZuneListRules.Lists;

    static partial void DetailPolicyFor(ZuneParameters parameters, ref DetailPolicy detail, ref DetailTierPolicy detailTiers)
        => detailTiers = ZuneListRules.DetailTiers;
}
