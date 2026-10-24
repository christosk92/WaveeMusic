// ── Themes/WinUi/Structure/Rules/WinUiPolicies.Lists.cs — what WinUI decides for the core's track lists (themes plan 5.5, 6.2; P6 W3) ─────────
//
// Two family hooks of `WinUiPolicies` (see its header):
//
//   · LISTS: `WinUiListRules.ListsFor`: the artwork follows `artworkInRows`; ON the MODERN skin (the two-line row with a cover), OFF the CLASSIC skin (the
//     one-line 40 row with the artist in its own lane). The core's lane sets, header height, list key and the artist chart's metrics read it.
//   · DETAIL TIERS: the detail table's record per window tier (`WinUiListRules.DetailTiers`): the Compact row form at Compact, the header table with no
//     chevron lane at Small, every lane the list column admits at Standard, the cozy rows at Huge. W6a (the detail pages) extends THAT record rather than
//     implementing the hook a second time (a partial method has one body): the hero, the rail and the asides are its numbers.

using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiPolicies
{
    static partial void ListsPolicy(WinUiParameters parameters, ref ListPolicy lists) => lists = WinUiListRules.ListsFor(parameters.ArtworkInRows);

    static partial void DetailPolicyFor(WinUiParameters parameters, ref DetailPolicy detail, ref DetailTierPolicy detailTiers)
    {
        // W6a: the hero over the table at every width (the board draws no rail), and the records with the page inset, the 48 band and the Huge aside.
        detail = new DetailPolicy(DetailPageLayout.Hero, RailUniform: false);
        detailTiers = WinUiListRules.DetailTiers;
    }
}
