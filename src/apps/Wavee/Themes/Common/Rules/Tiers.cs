// ── Themes/Common/Rules/Tiers.cs — the Common blocks' tier layouts (themes plan 3.2, 3.6 TC10) ───────────────────────────────────
//
// The Common blocks (history, logs, diagnostics, the Home customizer, the module page, the not-found page) are drawn by EVERY theme and no theme has a board
// for them (owner Q18: they are built from the host theme's own parts). So their tier decisions are the least a block can say without knowing which ladder
// its theme draws: they read the CONTENT tier (`ctx.Tier().Content`, the region the page is laid out in) and the height tier, never an edge, never a number of
// pixels. A theme moves its own edges (Classic 528 / 1008 / 1920, Zune 640 / 1008 / 1920 / 3200, ...) and the same block follows.
//
//   Compact    one column. A row that has a label and a value stacks them; a wide table becomes stacked rows (never a page-level scroll); every hit target is at
//              least 40 x 40 (the brief's Compact rule); the page gutter is the narrow one.
//   Small      the Standard arrangement, narrowed: the gutter steps down, a head that cannot fit one row (the log viewer's picker cluster) takes two.
//   Standard   today's layout, unchanged.
//   Huge, 4K   the Standard arrangement under a max content width (a fill rule: the column stops growing and stays at the page's left edge, as Settings' 1000
//              column and the release notes' 1600 column do on their Huge boards); lines of text never run across 3840 DIP.
//
// Short (below the structure's short height): a pinned head that would eat the page (history, the Home customizer) scrolls with the page instead.
//
// Spacing roles are read where a layout is MADE (`For`), never cached: a definition swap re-lays-out. No sub-ladder is needed (every decision is the tier),
// so nothing here compares a width; WVTH013 reports nothing in Common.
//
// Public, because `Wavee.Tests\CommonTiersTests` pins the records per tier (the assembly has no InternalsVisibleTo) and the mounted tests read the record a view used.

using FluentGpu.Dsl;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The decisions every Common block shares.</summary>
public static class CommonTiers
{
    /// <summary>The brief's Compact hit target: no button, icon button, link or field is smaller than this at Compact.</summary>
    public const float CompactTarget = 40f;

    /// <summary>A text box's width at Compact: the narrowest card interior (a 300 window, the narrow gutter, the card's padding and border) holds it.</summary>
    public const float CompactFieldWidth = 240f;

    /// <summary>A tree row's indent per depth at Compact.</summary>
    public const float CompactTreeStep = 8f;

    /// <summary>The widest a Common reading column grows on the Standard and Huge tiers: the Classic page column (<c>R.Space.PageMaxW</c>).</summary>
    public static float ColumnMax => R.Space.PageMaxW;

    /// <summary>True for the one tier that stacks.</summary>
    public static bool Stacks(WidthTier tier) => tier == WidthTier.Compact;

    /// <summary>The smallest hit target the tier allows (0 = the control's own size).</summary>
    public static float Target(WidthTier tier) => Stacks(tier) ? CompactTarget : 0f;

    /// <summary>The page gutter of a block whose Standard gutter is the NavigationView margin (36): 16 at Compact, 32 at Small, 36 above.
    /// (TC1's page gutter ladder owns the same three numbers for the structure's own pages; a Common block reads this until it is rewired.)</summary>
    public static float WideGutter(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Spacing.PageNarrow,
        WidthTier.Small => Spacing.XXXL,
        _ => Spacing.PageWide,
    };

    /// <summary>The gutter of a block whose Standard gutter is <paramref name="standard"/>: the narrow one at Compact, the block's own above.</summary>
    public static float Gutter(WidthTier tier, float standard) => Stacks(tier) ? Spacing.PageNarrow : standard;

    /// <summary>True when the height tier is Short.</summary>
    public static bool Short(HeightTier height) => height == HeightTier.Short;
}

/// <summary>The diagnostics pages' tier layout.</summary>
/// <param name="MaxWidth">The scroll column's widest extent.</param>
/// <param name="Stacked">A label over its value, a table row as stacked lines, a control row wrapping.</param>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
/// <param name="WireBoxWidth">The wire check's text boxes.</param>
/// <param name="PixelBoxWidth">The evidence card's x and y boxes.</param>
/// <param name="TagBoxWidth">The evidence card's tag box.</param>
/// <param name="TreeStep">A capture tree row's indent per depth.</param>
public readonly record struct CommonDiagnosticsLayout(
    float MaxWidth, bool Stacked, float Target, float WireBoxWidth, float PixelBoxWidth, float TagBoxWidth, float TreeStep)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonDiagnosticsLayout For(in TierFacts tier)
    {
        bool compact = CommonTiers.Stacks(tier.Content);
        return new CommonDiagnosticsLayout(
            CommonDiagnosticsRules.PageMaxWidth, compact, CommonTiers.Target(tier.Content),
            compact ? CommonTiers.CompactFieldWidth : CommonDiagnosticsRules.WireBoxWidth,
            CommonDiagnosticsRules.PixelBoxWidth,
            compact ? CommonTiers.CompactFieldWidth : CommonDiagnosticsRules.TagBoxWidth,
            compact ? CommonTiers.CompactTreeStep : CommonDiagnosticsRules.TreeDepthStep);
    }

    /// <summary>Standard, for a card that no page hosts (a unit test of a widget).</summary>
    public static CommonDiagnosticsLayout Standard => For(new TierFacts(WidthTier.Standard, HeightTier.Regular, WidthTier.Standard, 0f, 0f, 0f, false));
}

/// <summary>The log viewer's tier layout.</summary>
/// <param name="Gutter">The toolbar's and the list card's side gutter (the head's too).</param>
/// <param name="MaxWidth">The widest the page grows (0 = fill).</param>
/// <param name="StackHeader">The picker cluster (caption, session, open folder) under the title instead of beside it.</param>
/// <param name="BareHeader">Short: no crumbs and no caption, the title and the picker on one line.</param>
/// <param name="ShowCrumbs">The breadcrumb bar over the title (at Compact its crumbs are the stock Large ones, at least 40 x 40; the bare Short header goes without, the title bar's Back stays).</param>
/// <param name="TwoLineRows">A row is two lines (time, level, count over category and message) and the column header is gone.</param>
/// <param name="OneLineToolbar">Short: the toolbar is one scrolling line instead of a wrapping block.</param>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
/// <param name="RowExtent">One row's estimated extent before it is measured.</param>
/// <param name="DetailInset">The expanded details' left inset.</param>
public readonly record struct CommonLogsLayout(
    float Gutter, float MaxWidth, bool StackHeader, bool BareHeader, bool ShowCrumbs, bool TwoLineRows, bool OneLineToolbar, float Target, float RowExtent, float DetailInset)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonLogsLayout For(in TierFacts tier)
    {
        WidthTier width = tier.Content;
        bool compact = CommonTiers.Stacks(width);
        bool narrow = width <= WidthTier.Small;
        return new CommonLogsLayout(
            CommonTiers.WideGutter(width), CommonTiers.ColumnMax, narrow, narrow && CommonTiers.Short(tier.Height),
            !(narrow && CommonTiers.Short(tier.Height)), compact,
            narrow && CommonTiers.Short(tier.Height), CommonTiers.Target(width),
            compact ? CommonLogsRules.CompactRowExtent : CommonLogsRules.EstimatedRowExtent,
            compact ? CommonLogsRules.CompactDetailInset : CommonLogsRules.DetailInset);
    }
}

/// <summary>The history page's tier layout.</summary>
/// <param name="Gutter">The page's side gutter.</param>
/// <param name="MaxWidth">The widest the page grows (0 = fill).</param>
/// <param name="Compact">The head stacks (title row, stats row, full-width search and sort), the filter chips scroll, a row carries its time in its meta line.</param>
/// <param name="HeaderScrolls">Short: the head scrolls with the list instead of staying pinned over it.</param>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
public readonly record struct CommonHistoryLayout(float Gutter, float MaxWidth, bool Compact, bool HeaderScrolls, float Target)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonHistoryLayout For(in TierFacts tier)
        => new(CommonTiers.WideGutter(tier.Content), CommonTiers.ColumnMax, CommonTiers.Stacks(tier.Content),
            CommonTiers.Short(tier.Height) || CommonTiers.Stacks(tier.Content), CommonTiers.Target(tier.Content));
}

/// <summary>The Home customizer's tier layout.</summary>
/// <param name="Gutter">The page's side gutter.</param>
/// <param name="MaxWidth">The widest the page grows (0 = fill).</param>
/// <param name="StackHeader">Reset under the title instead of beside it.</param>
/// <param name="Scrolls">The head and the list scroll as one page (the list alone does not fit a Compact or Short window).</param>
/// <param name="RowHeight">One zone row's height, also the reorderable's item extent.</param>
/// <param name="RowGap">The gap between a row's gripper, label and controls.</param>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
public readonly record struct CommonCustomizeLayout(float Gutter, float MaxWidth, bool StackHeader, bool Scrolls, float RowHeight, float RowGap, float Target)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonCustomizeLayout For(in TierFacts tier)
    {
        bool compact = CommonTiers.Stacks(tier.Content);
        return new CommonCustomizeLayout(
            CommonTiers.Gutter(tier.Content, Spacing.XXXL), CommonTiers.ColumnMax, compact, compact || CommonTiers.Short(tier.Height),
            compact ? CommonCustomizeRules.CompactRowH : CommonCustomizeRules.CustomizeRowH,
            compact ? CommonCustomizeRules.CompactRowGap : CommonCustomizeRules.CustomizeRowGap, CommonTiers.Target(tier.Content));
    }
}

/// <summary>The module page's tier layout.</summary>
/// <param name="Gutter">The page's side gutter.</param>
/// <param name="MaxWidth">The widest the page body grows (0 = fill).</param>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
public readonly record struct CommonModuleLayout(float Gutter, float MaxWidth, float Target)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonModuleLayout For(in TierFacts tier)
        => new(CommonTiers.Gutter(tier.Content, CommonModuleRules.Gutter), CommonTiers.ColumnMax, CommonTiers.Target(tier.Content));
}

/// <summary>The not-found page's tier layout.</summary>
/// <param name="Target">The smallest hit target (0 = the control's own size).</param>
public readonly record struct CommonNotFoundLayout(float Target)
{
    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public static CommonNotFoundLayout For(in TierFacts tier) => new(CommonTiers.Target(tier.Content));
}
