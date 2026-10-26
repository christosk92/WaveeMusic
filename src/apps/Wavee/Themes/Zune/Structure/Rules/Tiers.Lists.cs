// ── Themes/Zune/Structure/Rules/Tiers.Lists.cs — Zune's list rungs ──────────────────────────────────────────────────────────────────
//
// ONE board mark that is not a tier: the CONTENT width from which the artist's popular chart is two columns (`ZuneArtistRules.ChartPairRung`).
// The Standard tier (1008-1920) draws the chart beside a 300 rail; just above 1008 two columns were squeezed to ~260 each, leaving a ~70 DIP
// title and a stacked third line. The rung commits like a tier (widening at the line, held 24 on narrowing), so the chart's column count, its
// reading order and the shelf's fit change together, once per crossing. This hook runs inside `ZuneTiers.Policy`'s initializer: it reads only
// constants (inlined at compile time).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

public static partial class ZuneTiers
{
    static partial void RungsLists(ImmutableArray<NamedRung>.Builder rungs)
        => rungs.Add(new NamedRung(ZuneArtistRules.ChartPairRung, new TierEdge(ZuneArtistRules.ChartPairFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Content));
}
