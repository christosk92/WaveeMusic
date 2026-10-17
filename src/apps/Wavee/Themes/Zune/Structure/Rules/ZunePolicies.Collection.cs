// ── Themes/Zune/Structure/Rules/ZunePolicies.Collection.cs — what Zune decides for the library navigator (themes plan 6.3; P5 Z7) ────
//
// The collection hook of `ZunePolicies` (see its header): the library tier policy, `ZuneLibraryTiers.Policy` (the grid's gaps per content tier and Small's
// cell floor; the navigator is the flexible column, so no resting width is stated).

using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZunePolicies
{
    static partial void CollectionPolicy(ZuneParameters parameters, ref LibraryTierPolicy libraryTiers) => libraryTiers = ZuneLibraryTiers.Policy;
}
