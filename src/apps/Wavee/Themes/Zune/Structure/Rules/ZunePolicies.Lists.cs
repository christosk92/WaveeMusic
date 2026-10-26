// ── Themes/Zune/Structure/Rules/ZunePolicies.Lists.cs — what Zune decides for the core's track lists (themes plan 5.5, 6.3; P5 Z3) ──
//
// Two family hooks of `ZunePolicies` (see its header):
//
//   · LISTS: `ZuneListRules.Lists`: track artwork on and ONE row skin, Modern (the core's lane sets, header height, list key and the artist
//     chart's metrics read it). Zune has no row-style or hide-artwork option, so no structure value moves it.
//   · DETAIL TIERS: the detail table's record per window tier (`ZuneListRules.DetailTiers`, ONE body, ZT5's finished table): the Compact row form
//     at Compact, the Album lane (never Plays) at Small, every lane the width admits from Standard, the density's rows everywhere. The aside
//     docks from Standard, the insights rest open from Huge (a column of the page), the identity column rests at 240 / 320 / 400 (Huge 4K is the
//     wide twin past the third pane's rung). A detail lane that needs another number extends `ZuneListRules.DetailTiers` rather than
//     implementing the hook a second time (a partial method has one body).
//
// `detail` (the page layout policy) is left at the core's value: Zune draws the two-column arm from the page mode ladder and the vertical arm
// below it (`Tiers.Detail.cs`), so neither the Hero layout nor the uniform rail is a choice it offers.

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void ListsPolicy(ZuneParameters parameters, ref ListPolicy lists) => lists = ZuneListRules.Lists;

    static partial void DetailPolicyFor(ZuneParameters parameters, ref DetailPolicy detail, ref DetailTierPolicy detailTiers)
        => detailTiers = ZuneListRules.DetailTiers;
}
