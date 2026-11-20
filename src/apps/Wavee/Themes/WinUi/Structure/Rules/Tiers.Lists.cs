// ── Themes/WinUi/Structure/Rules/Tiers.Lists.cs — WinUI's list ladder (themes plan 3.2, 6.2; P6 W3) ──────────────────────────────────────
//
// THE TABLE LADDER (`DetailLadders.Table`, measured on the LIST column: the table's own width, not the window's, so a docked queue or insights pane that
// narrows the list moves it). The core's table tier is an index 0 (the widest) ... 6 (the narrowest) that gates its lanes (`Track.Table.SetFor`: Album and
// Added-by below 2 / 1, Date and Plays below 3, the heart and the art below 5, the "..." below 6), so WinUI's three bands map onto three of the seven tiers
// (tier 1, not 0: tier 0 alone adds the Added-by lane, which no WinUI board draws) and the others are unreachable (an absent edge, `TierEdge.None`, leaves
// the tier above it out of reach):
//
//     list column         tier   lanes (| between lanes; "title / artists · album" is the two-line title cell)
//     >= 860              1      # | art | title / artists | album | date | plays | heart | time | "..." | chevron   (all)
//     504 - 859           2      # | art | title / artists · album | date | plays | heart | time | "..." | chevron   (the album rides the subline)
//     < 504               4      # | art | title / artists · album | heart | time | "..." | chevron                  (Date and Plays dropped too)
//
// A lane with another home yields at the design's edge; a lane with none stays while it fits. The ALBUM shares the title's star and has a second home, the
// title's subline ("artists · album", as the Compact row says it: `WinUiTrackTable.Subline`), so below W-Playlist's 860 it folds there and the title takes its
// width. DATE ADDED has no other home, so its edge is where it stops fitting: the list column whose row keeps the title its floor beside it (`DateFrom`, 504
// at the standard density), never a board number. W-Tiers drew "560-859 drops Plays and Date added"; the owner overruled it (2026-11-20, a 842-DIP list with
// the queue docked: "date added. it has plenty of space, why hidden!"). Plays rides the same tier (the core gates Date and Plays together); where both do not
// fit beside the title's floor (a playlist with its Plays lane on, near the bottom of the band) the core's relief ladder yields Plays first, as anywhere.
//
// The two edges hold 24 on WIDENING (a lane yields at once and must re-earn the width, the table ladder's own grammar, shared with Classic and Zune), so a
// drag across either changes a lane once, never twice (`crossing`). The two LOW edges below zero are "always crossed" (the idiom `DetailLadders.AlwaysTop`
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
    /// <summary>The Album lane from this list column (W-Playlist at 1440 draws it); below it the album rides the title's subline.</summary>
    public const float AlbumFrom = 860f;

    /// <summary>The cover lane and the grid's column gap the date edge is measured with: the standard density's 40 cover (`WinUiDensityOption.Rows`) and the
    /// core's 12 gap between lanes (`Track.RowMetrics.ColGapFor`, WinUI's Spacing.M).</summary>
    public const float Cover = 40f, ColGap = 12f;

    /// <summary>Date added (and Plays) from this list column: the narrowest whose list row keeps the title its floor (`Track.Lane.TitleFloor`, ~14 characters)
    /// beside the Date lane. The grid's two insets, then # · cover · title · date · heart · time · "..." and the six gaps between them: 504.</summary>
    public const float DateFrom = 2f * WinUiListRules.RowPad + Track.Lane.Num + Cover + Track.Lane.TitleFloor + Track.Lane.Date + Track.Lane.HeartTrailing
                                  + WinUiListRules.TimeLane + Track.Lane.Actions + 6f * ColGap;

    /// <summary>The widening hold of both edges.</summary>
    public const float Hold = 24f;

    /// <summary>The table tiers the three bands use (the core's gates: Album below 2, Date and Plays below 3, the heart and the art below 5).</summary>
    public const int TierWide = 1, TierMid = 2, TierNarrow = 4;

    /// <summary>The table-tier ladder (index = 6 - tier): two always-crossed low edges, an absent one (tier 3 is out of reach), the date edge (index 4 = tier 2),
    /// the album edge (index 5 = tier 1: the widest tier that does not add Added-by, which WinUI's boards never draw).</summary>
    public static TierLadder Table { get; } = TierLadder.Of(
        new TierEdge(-2f), new TierEdge(-1f),
        TierEdge.None,
        new TierEdge(DateFrom, NarrowHold: 0f, WidenHold: Hold),
        new TierEdge(AlbumFrom, NarrowHold: 0f, WidenHold: Hold));

    /// <summary>The table tier of a <paramref name="width"/>-wide list column, held against <paramref name="previous"/> once <paramref name="initialized"/> (a fresh
    /// resolve uses the edges themselves). An unmeasured width keeps the previous tier.</summary>
    public static int TierFor(float width, int previous, bool initialized = true)
    {
        if (!(width > 0f)) return previous;
        return DetailLadders.TierOf(Table.Resolve(width, initialized ? DetailLadders.IndexOfTier(previous) : -1));
    }
}
