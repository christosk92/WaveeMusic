// ── Themes/Zune/Structure/Rules/Tiers.Collection.cs — Zune's collection ladders (themes plan 3.2, 6.3; P5 Z7) ──────────────────────
//
// The library page's LAYOUT ladder (`LibraryLadders.Layout`, measured on the page's own width): index 0 the one-column breadcrumb drill-in, index 1 the
// split columns. The core host resolves it by id and never sees a number; the numbers and the records are `ZuneLibraryRules.cs`. Recents and Profile read
// the content tier alone and add no ladder.

using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZuneTiers
{
    static partial void LaddersCollection(ImmutableArray<NamedLadder>.Builder ladders) => ladders.AddRange(ZuneLibraryTiers.Ladders);
}
