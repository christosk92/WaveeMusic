// ── Themes/Zune/Structure/Rules/Tiers.Detail.cs — Zune's detail ladders (themes plan 3.6; P5 Z6a1) ─────────────────────────────────────
//
// The three ladders the core's detail frame and table resolve by id (`DetailLadders.Mode`, `.Table`, `.Height`): the page mode on the page
// column (the vertical arm below 640), the table tiers on the list column, the height rungs on the window. The numbers live in
// `ZuneDetailRules` (its header says why each edge is where it is); this hook only names them in Zune's policy. It reads no static of
// `ZuneTiers` (the partial files' initialisation order is undefined): its ladders are `ZuneDetailRules`' own.

using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZuneTiers
{
    static partial void LaddersDetail(ImmutableArray<NamedLadder>.Builder ladders) => ladders.AddRange(ZuneDetailRules.Ladders);
}
