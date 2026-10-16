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
//   Huge      24 / 16                   32 button     the tuned strip                   the tuned row                                     44       16
//
// The page gutter is the page's own (`ClassicTiers.PageOf`: 16 / 32 / 36 / 36). Not built (reported): Huge's Overview as a docked aside beside a 1100 list (the calendar
// stays the semantic zoom's zoomed-out view at every tier) and the Compact row's day rail (Compact has none).
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
        Stacked = false, ChevronLane = 24f, ChildRowH = 40f, Rail = true, CalendarGap = 16f,
    };

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
