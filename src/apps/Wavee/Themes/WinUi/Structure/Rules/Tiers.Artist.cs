// ── Themes/WinUi/Structure/Rules/Tiers.Artist.cs — WinUI's artist ladders and tier records (themes plan 3.2, 6.2; P6 W6a) ────────────────────────────
//
// THE HERO LADDER (`ArtistLadders.Hero`, the page's own width) is not declared: the core's default answers its top index (Wide) at every width, because the
// hero is one composition (a r8 card) whose height follows the WINDOW tier, not a sub-tier of the page: 200 Compact, 220 Small, 260 Standard, 420 Huge
// (W-Artist, -Compact, -Small, -Huge). The policy below hands the core those metrics: the card's height, the page inset as the gutter, the horizontal veil.
//
// WHAT THE POLICY ANSWERS (the core asks with the facts on screen):
//   · Hero              the card's metrics (above).
//   · ChartMaxColumns   one column; two at Small, where the chart spans the page (W-Artist-Small: rows 1-3 | 4-5).
//   · ChartRows         five; ten at Huge (the board lists ten, one column).
//   · BandClearance     the sticky pivot's 41 (the bar and its hairline): a pivot click parks a section's top exactly under it, and the scroll spy's band edge
//                       is the same line.
//   · SplitDetailTabs   "Appears on" and "About" are tabs of their own (Overview · Albums · Singles & EPs · Compilations · Appears on · About).
//
// Public, so `Wavee.Tests` pins the records.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The discography's geometry in one window tier: the grid's column floor and gap, the drawer's columns (0: the grid's own rule), row pitch and head allowance.</summary>
public readonly record struct WinUiDiscoLayout(float MinColumn, float Gap, int DrawerColumns, float DrawerPitch, float DrawerHead)
{
    /// <summary>The drawer's shape the core's verdict sizes.</summary>
    public Album.DrawerShape Drawer => new(DrawerColumns, DrawerPitch, DrawerHead);
}

/// <summary>The shelves' fit (the card range, the column cap, the gap, the pager).</summary>
public readonly record struct WinUiShelfBand(float MinCardW, float MaxCardW, int MaxColumns, float FixedCardW, float Gap, bool Pager);

/// <summary>WinUI's artist tier policy and records.</summary>
public static class WinUiArtistTiers
{
    /// <summary>The widest the hero's copy grows (the board's name is one line; a long name wraps or auto-fits).</summary>
    public const float CopyMax = 1120f;

    /// <summary>The Huge chart's rows in its one column.</summary>
    public const int HugeChartRows = 10;

    /// <summary>The policy WinUI publishes for the core artist and people pages.</summary>
    public static ArtistTierPolicy Policy { get; } = new WinUiArtistTierPolicy();

    /// <summary>The shelves' fit in window tier <paramref name="tier"/>: cards 150 to 200 wide 12 apart; a Compact strip of fixed 164 cards; ten columns at most at Huge.</summary>
    public static WinUiShelfBand ShelfOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new WinUiShelfBand(WinUiArtistRules.CompactCard, WinUiArtistRules.CompactCard, 0, WinUiArtistRules.CompactCard, 0f, false),
        WidthTier.Huge => new WinUiShelfBand(WinUiArtistRules.ShelfMin, 9999f, WinUiArtistRules.HugeShelfColumns, 0f, WinUiArtistRules.ShelfGap, true),
        _ => new WinUiShelfBand(WinUiArtistRules.ShelfMin, WinUiArtistRules.ShelfMax, 0, 0f, WinUiArtistRules.ShelfGap, true),
    };

    /// <summary>The overview's column count for a <paramref name="pageWidth"/>-wide page in window tier <paramref name="tier"/>: one below 1100 (held 24 once two),
    /// two from 1100, three at Huge. <paramref name="previous"/> 0 is a fresh resolve.</summary>
    public static int OverviewColumns(float pageWidth, WidthTier tier, int previous)
    {
        float edge = previous >= 2 ? WinUiArtistRules.TwoColumnFrom - WinUiArtistRules.ColumnHold : WinUiArtistRules.TwoColumnFrom;
        if (!(pageWidth >= edge)) return 1;
        return tier >= WidthTier.Huge ? 3 : 2;
    }

    /// <summary>The chart's widest column count in window tier <paramref name="tier"/>: one, two at Small.</summary>
    public static int ChartColumnsOf(WidthTier tier) => tier == WidthTier.Small ? 2 : 1;

    /// <summary>The chart's rows per column: five, ten at Huge.</summary>
    public static int ChartRowsOf(WidthTier tier) => tier >= WidthTier.Huge ? HugeChartRows : ArtistSections.ChartMaxRows;

    /// <summary>The discography's geometry in window tier <paramref name="tier"/> (W-Discography C / S / Std / H): the grid's column floor and gap, the album drawer's track
    /// columns (one at Compact, two at Small, the grid's own rule at Standard, four at Huge), the row pitch (36; the 40 target at Compact) and the head row.</summary>
    public static WinUiDiscoLayout DiscoLayoutOf(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new(WinUiDiscographyRules.CompactMinColumn, WinUiDiscographyRules.CompactGap, 1, WinUiDiscographyRules.DrawerPitchCompact, WinUiDiscographyRules.HeadRowCompact + WinUiDiscographyRules.DrawerHeadPad),
        WidthTier.Small => new(WinUiDiscographyRules.MinColumn, WinUiDiscographyRules.Gap, 2, WinUiDiscographyRules.DrawerPitch, WinUiDiscographyRules.HeadRow + WinUiDiscographyRules.DrawerHeadPad),
        WidthTier.Standard => new(WinUiDiscographyRules.MinColumn, WinUiDiscographyRules.Gap, 0, WinUiDiscographyRules.DrawerPitch, WinUiDiscographyRules.HeadRow + WinUiDiscographyRules.DrawerHeadPad),
        _ => new(WinUiDiscographyRules.MinColumn, WinUiDiscographyRules.Gap, 4, WinUiDiscographyRules.DrawerPitch, WinUiDiscographyRules.HeadRow + WinUiDiscographyRules.DrawerHeadPad),
    };

    /// <summary>The window tier of <paramref name="facts"/> (Standard where no frame resolved the tiers).</summary>
    public static WidthTier TierOf(in TierFacts facts) => facts.Policy is null ? WidthTier.Standard : facts.Window;

    /// <summary>The hero card's metrics in <paramref name="facts"/>.</summary>
    public static ArtistHeroMetrics HeroMetrics(in TierFacts facts)
        => new(ArtistHeroTier.Wide, WinUiArtistRules.HeroHeightOf(TierOf(in facts)), WinUiDetailRules.InsetOf(TierOf(in facts)), ArtistHeroVeilAxis.Horizontal, CopyMax);
}

/// <summary>WinUI's answers to the core's artist page questions.</summary>
public sealed class WinUiArtistTierPolicy : ArtistTierPolicy
{
    /// <inheritdoc/>
    public override ArtistHeroMetrics Hero(ArtistHeroTier tier, in TierFacts facts, float width) => WinUiArtistTiers.HeroMetrics(in facts);

    /// <inheritdoc/>
    public override int ChartMaxColumns(in TierFacts facts) => WinUiArtistTiers.ChartColumnsOf(WinUiArtistTiers.TierOf(in facts));

    /// <inheritdoc/>
    public override int ChartRows(in TierFacts facts) => WinUiArtistTiers.ChartRowsOf(WinUiArtistTiers.TierOf(in facts));

    /// <inheritdoc/>
    public override float BandClearance(in TierFacts facts) => WinUiArtistRules.PivotClearance;

    /// <inheritdoc/>
    public override bool SplitDetailTabs(in TierFacts facts) => true;
}
