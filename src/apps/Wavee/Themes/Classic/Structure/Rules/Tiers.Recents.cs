// ── Themes/Classic/Structure/Rules/Tiers.Recents.cs — Classic's Recents page per content tier (themes plan 3.6, TC7) ────────────────────────────────
//
// ONE record per CONTENT tier, read by the page through `ClassicRecentsTiers.Of(ctx.Tier())`. Boards: `C-Recents` (Standard, the tuned design: its numbers are the
// ones the page held before tiers), `C-Recents-Compact`, `C-Recents-Small`, `C-Recents-Huge`.
//
//   tier      masthead (top / bottom)   Overview      kind pivots                       rows                                              rail     calendar gap
//   Compact   12 / 0                    40 button     tabs 40 tall, 8 pad, gap 4        stacked: kind + time under the sub line, 40       none     16
//                                                                                       chevron, no hover "..." (child rows 44)
//   Small     16 / 8                    24 button     the tuned strip (gap 20)          the tuned row (chip, time, chevron)               44       24
//   Standard  24 / 16                   24 button     the tuned strip                   the tuned row                                     44       36 (the old PageWide)
//   Huge      24 / 16                   32 toggle     the tuned strip                   the tuned row                                     44       16
//
// HUGE DOCKS THE OVERVIEW (C-Recents-Huge): the page's column (masthead, pivots, list) caps at an 1100 list (`ListMax`, plus the page gutters) and the Overview stands
// 48 right of it (`OverviewGap`) as a 660 aside (`OverviewAside`: two plated 322 month cards 16 apart) with its own "Overview" head and the heat key. The masthead's Overview
// button becomes the aside's toggle (pressed while it shows); a day in it brings the list to that day. The aside is in the page's row at every tier, width 0 below Huge,
// so a crossing eases it and the column beside it is never re-parented. The semantic zoom's calendar stays the zoomed-out view (a day header still zooms out to it).
//
// The page gutter is the page's own (`ClassicTiers.PageOf`: 16 / 32 / 36 / 36). Not built (reported): the Compact row's day rail (Compact has none).
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's Recents page is in one content tier.</summary>
public sealed record ClassicRecentsLayout
{
    /// <summary>The content tier the record is for (Huge 4K reads Huge's).</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The air over the masthead (the page's first row), and under it before the kind pivots.</summary>
    public required float HeadTop { get; init; }

    /// <summary>See <see cref="HeadTop"/>.</summary>
    public required float HeadBottom { get; init; }

    /// <summary>The Overview button's height: 40 at Compact, the Small control's 24, Huge's 32.</summary>
    public required float OverviewH { get; init; }

    /// <summary>The kind pivots' height (a 40 target at Compact; 0 is the tuned tab, which sizes to its label and underline), the gap between tabs and a tab's side padding.</summary>
    public required float PivotH { get; init; }

    /// <summary>See <see cref="PivotH"/>.</summary>
    public required float PivotGap { get; init; }

    /// <summary>See <see cref="PivotH"/>.</summary>
    public required float PivotPadX { get; init; }

    /// <summary>The row folds its kind and played-at stamp under the sub line (Compact): the trailing cluster is the 40 chevron alone and the hover "..." is gone.</summary>
    public required bool Stacked { get; init; }

    /// <summary>The expand chevron's lane: 40 at Compact, the tuned 24 elsewhere.</summary>
    public required float ChevronLane { get; init; }

    /// <summary>A drawer child's row height: 44 at Compact (C-Recents-Compact), the tuned 40 elsewhere.</summary>
    public required float ChildRowH { get; init; }

    /// <summary>The annotated day rail (the list's scrollbar, 44) stands beside the list; Compact has none.</summary>
    public required bool Rail { get; init; }

    /// <summary>The gap between the calendar's month cards (its grid's own rule: it used to borrow <c>Spacing.PageWide</c>, the page gutter's old name).</summary>
    public required float CalendarGap { get; init; }

    /// <summary>The list column's cap (C-Recents-Huge: 1100, the page gutters outside it); NaN: the column fills the page.</summary>
    public float ListMax { get; init; } = float.NaN;

    /// <summary>The docked Overview aside's width (C-Recents-Huge: 660, two plated month cards); 0: no aside, the Overview is the semantic zoom's alone.</summary>
    public float OverviewAside { get; init; }

    /// <summary>The air between the list column and the aside (C-Recents-Huge: 48).</summary>
    public float OverviewGap { get; init; }

    /// <summary>The Overview docks as an aside (<see cref="OverviewAside"/> is not 0): the masthead's button toggles it.</summary>
    public bool OverviewDocked => OverviewAside > 0f;
}

/// <summary>Classic's Recents records.</summary>
public static class ClassicRecentsTiers
{
    static readonly ClassicRecentsLayout s_compact = new()
    {
        Tier = WidthTier.Compact, HeadTop = 12f, HeadBottom = 0f, OverviewH = 40f, PivotH = 40f, PivotGap = 4f, PivotPadX = 8f,
        Stacked = true, ChevronLane = 40f, ChildRowH = 44f, Rail = false, CalendarGap = 16f,
    };

    static readonly ClassicRecentsLayout s_small = new()
    {
        Tier = WidthTier.Small, HeadTop = 16f, HeadBottom = 8f, OverviewH = 24f, PivotH = 0f, PivotGap = 20f, PivotPadX = 0f,
        Stacked = false, ChevronLane = 24f, ChildRowH = 40f, Rail = true, CalendarGap = 24f,
    };

    static readonly ClassicRecentsLayout s_standard = new()
    {
        Tier = WidthTier.Standard, HeadTop = 24f, HeadBottom = 16f, OverviewH = 24f, PivotH = 0f, PivotGap = 20f, PivotPadX = 0f,
        Stacked = false, ChevronLane = 24f, ChildRowH = 40f, Rail = true, CalendarGap = 36f,
    };

    static readonly ClassicRecentsLayout s_huge = new()
    {
        Tier = WidthTier.Huge, HeadTop = 24f, HeadBottom = 16f, OverviewH = 32f, PivotH = 0f, PivotGap = 20f, PivotPadX = 0f,
        Stacked = false, ChevronLane = 24f, ChildRowH = 40f, Rail = true, CalendarGap = HugeCalendarGap,
        ListMax = HugeListMax, OverviewAside = HugeOverviewWidth, OverviewGap = HugeOverviewGap,
    };

    /// <summary>C-Recents-Huge: the month cards 16 apart, the list capped at 1100, the aside 48 right of it.</summary>
    public const float HugeCalendarGap = 16f, HugeListMax = 1100f, HugeOverviewGap = 48f;

    /// <summary>The Huge aside's width: two plated month cards (<see cref="ClassicRecentsRules.OverviewCardW"/>, 322) and the cards' gap between them = 660.</summary>
    public static float HugeOverviewWidth => 2f * ClassicRecentsRules.OverviewCardW + HugeCalendarGap;

    /// <summary>The record for <paramref name="facts"/>' content tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicRecentsLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's).</summary>
    public static ClassicRecentsLayout For(WidthTier content) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        WidthTier.Standard => s_standard,
        _ => s_huge,
    };
}
