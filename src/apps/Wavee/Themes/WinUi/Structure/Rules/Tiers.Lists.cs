// ── Themes/WinUi/Structure/Rules/Tiers.Lists.cs — WinUI's list ladder (themes plan 3.2, 6.2; P6 W3) ──────────────────────────────────────
//
// THE TABLE LADDER (`DetailLadders.Table`, measured on the LIST column: the table's own width, not the window's). W-Tiers / the lists row of the
// plan: "< 560 two-line, 560-859 drops Plays / Date, >= 860 all". The core's table tier is an index 0 (the widest) ... 6 (the narrowest) that gates its
// lanes (`Track.Table.SetFor`: Album and Added-by below 2 / 1, Date and Plays below 3, the heart and the art below 5, the "..." below 6), so WinUI's three
// bands map onto three of the seven tiers (tier 1, not 0: tier 0 alone adds the Added-by lane, which no WinUI board draws) and the others are unreachable (an absent edge, `TierEdge.None`, leaves the tier above it out of reach):
//
//     list column         tier   lanes
//     >= 860              1      # · art · title / artists · album · date · plays · heart · time · "..." · chevron   (all)
//     560 - 859           3      # · art · title / artists · heart · time · "..." · chevron             (Album, Date, Plays dropped)
//     < 560               4      the same two-line row (the window's Compact band replaces it with the Compact row form, no header)
//
// The two edges hold 24 on WIDENING (a lane yields at once and must re-earn the width, the table ladder's own grammar, shared with Classic and Zune), so a
// drag across 560 or 860 changes a lane once, never twice (`crossing`). The two LOW edges below zero are "always crossed" (the idiom `DetailLadders.AlwaysTop`
// uses): they lift the narrowest band from index 0 (tier 6, which drops the heart, the art and the "..." too) to index 2 (tier 4).
//
// The ladder is named in WinUI's policy by id (`DetailLadders.Table`); the detail frame's own two (`Mode`, `Height`) are W6a's. This hook reads no static of
// `WinUiTiers` (the partial files' initialisation order is undefined): the ladder is `WinUiListTiers`'.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiTiers
{
    static partial void LaddersLists(ImmutableArray<NamedLadder>.Builder ladders) => ladders.Add(new NamedLadder(DetailLadders.Table, WinUiListTiers.Table));
}

/// <summary>WinUI's list ladder and its pure reads.</summary>
public static class WinUiListTiers
{
    /// <summary>The list column's edges: Plays, Date and Album from 860 (W-Playlist at 1440 draws all; W-Album-Small at 704 draws none), two-line below 560.</summary>
    public const float TwoLineBelow = 560f, AllFrom = 860f;

    /// <summary>The widening hold of both edges.</summary>
    public const float Hold = 24f;

    /// <summary>The table tiers the three bands use (the core's gates: Album below 2, Date and Plays below 3, the heart and the art below 5).</summary>
    public const int TierWide = 1, TierMid = 3, TierNarrow = 4;

    /// <summary>The table-tier ladder (index = 6 - tier): two always-crossed low edges, the 560 edge, an absent one, the 860 edge (index 5 = tier 1: the widest tier that does not add Added-by, which WinUI's boards never draw).</summary>
    public static TierLadder Table { get; } = TierLadder.Of(
        new TierEdge(-2f), new TierEdge(-1f),
        new TierEdge(TwoLineBelow, NarrowHold: 0f, WidenHold: Hold),
        TierEdge.None,
        new TierEdge(AllFrom, NarrowHold: 0f, WidenHold: Hold));

    /// <summary>The table tier of a <paramref name="width"/>-wide list column, held against <paramref name="previous"/> once <paramref name="initialized"/> (a fresh
    /// resolve uses the edges themselves). An unmeasured width keeps the previous tier.</summary>
    public static int TierFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.TierOf(Table.Resolve(width, initialized ? DetailLadders.IndexOfTier(previous) : -1));
    }
}
