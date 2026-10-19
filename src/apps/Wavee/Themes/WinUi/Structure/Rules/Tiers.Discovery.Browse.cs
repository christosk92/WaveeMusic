// ── Themes/WinUi/Structure/Rules/Tiers.Discovery.Browse.cs — WinUI's Browse directory and category page per content tier (themes plan 3.6, 6.2; P6 W5) ──
//
// ONE record per CONTENT tier, read through `WinUiBrowseTiers.Of(ctx.Tier())`. Nothing compares a width (WVTH013): a grid's column count is the record's
// `WinUiGridBand` (fixed, or a floor-fit INSIDE the tier at the band's own measured width). Boards W-Browse, W-Browse-Compact / -Small / -Huge,
// W-BrowseCategory (+ -Compact, -Small, -Huge).
//
//   tier      Top (colour tiles)       For you (chips)              Genres / Mood / More        Charts                       category shelf
//   Compact   2 cols, 96 high          one strip, 44 high           2 cols, 84 high, 12 apart   stacked, 112 high            2 columns, 12 apart
//   Small     4 cols, 88 high          5 chips, 56 high             3 cols, 88 high, 12 apart   stacked, 96 high            4 columns
//   Standard  4 cols, 112 high         5 chips, 56 high, 16 apart   6 cols, 88 high, 16 apart   3 up, 72 high                6 columns, 16 apart
//   Huge      4 cols (2 : 1 with       2 cols, 56 high, 12 apart,   10 cols, 120 high, 20 apart 5 up, 72 high                10 columns, 20 apart
//             For you), 200 high       "Explore all categories" the sixth cell
//
// NO PAGE CAP at Huge or 4K (owner Q2): the tile grids fit as many columns as the region holds (a floor-fit inside the tier, so 3840 holds more than 2560).
// A column change FLIPs over 250 ms: every cell carries `WinUiDiscoveryMotion.Shove`.
//
// Public, because the tests pin the records.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The Fold deck's tile geometry (Browse's Charts band) in one tier.</summary>
/// <param name="Stacked">The tiles stack in one column (Compact, Small) instead of a paged deck.</param>
/// <param name="MaxColumns">The most tiles side by side in the paged deck.</param>
/// <param name="TileH">The tile's height.</param>
/// <param name="Cover">The fanned covers' edge.</param>
public readonly record struct WinUiChartsBand(bool Stacked, int MaxColumns, float TileH, float Cover);

/// <summary>What WinUI's Browse pages are in one content tier.</summary>
public sealed record WinUiBrowseLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The step between the directory's bands (24; 16 at Compact).</summary>
    public required float BandGap { get; init; }

    /// <summary>The step from a band's heading to its body (12).</summary>
    public required float HeaderGap { get; init; }

    /// <summary>Top's tiles: the columns, the tile's height, the 40 (56 at Huge) icon box, the tile's padding and the gap between tiles.</summary>
    public required WinUiGridBand Top { get; init; }
    public required float TopH { get; init; }
    public required float TopDisc { get; init; }
    public required float TopPad { get; init; }
    public required float TopGap { get; init; }

    /// <summary>Top and For you share one row, 2 : 1 (Huge); false stacks them.</summary>
    public required bool TopBeside { get; init; }

    /// <summary>For you's chips: the columns, the height (56; 44 at Compact), the gap, and whether it is ONE scrolling strip with a faded edge (Compact).</summary>
    public required WinUiGridBand ForYou { get; init; }
    public required float ForYouH { get; init; }
    public required float ForYouGap { get; init; }
    public required bool ForYouStrip { get; init; }

    /// <summary>"Explore all categories" is the For you grid's last cell (Huge) instead of a link on the Genres heading.</summary>
    public required bool ExploreCell { get; init; }

    /// <summary>The Genres, Mood and More tiles (and a category page's tile blocks): the columns, the height, the padding, the gap.</summary>
    public required WinUiGridBand Tiles { get; init; }
    public required float TileH { get; init; }
    public required float TilePad { get; init; }
    public required float TileGap { get; init; }

    /// <summary>The Charts band.</summary>
    public required WinUiChartsBand Charts { get; init; }

    /// <summary>A category page's shelf (W-BrowseCategory: the same covers as Home's shelves) and its header's tools height.</summary>
    public required WinUiShelfBand Shelf { get; init; }

    /// <summary>A category page's flattened grid: the narrowest and widest card and the most columns (0 is unlimited).</summary>
    public required WinUiShelfBand Grid { get; init; }

    /// <summary>The step between a category page's bands (24; 16 at Compact).</summary>
    public required float ShelfGap { get; init; }

    /// <summary>A shelf header's link and tools are 40 tall at Compact (the target floor), 32 elsewhere.</summary>
    public required float HeaderToolsH { get; init; }

    /// <summary>The Tiles grid's columns (fixed or fit at <paramref name="width"/>).</summary>
    public int TileColumns(float width) => Tiles.Columns(width, TileGap);
}

/// <summary>WinUI's Browse records.</summary>
public static class WinUiBrowseTiers
{
    /// <summary>The Charts band's two forms, keyed apart: the paged deck (Standard, Huge) and the stack (Compact, Small) are different trees, so the crossing
    /// between them cross-fades one into the other inside the band's reflow; within a form the part survives.</summary>
    public const string ChartsDeckKey = "browse:charts:deck", ChartsStackKey = "browse:charts:stack";

    /// <summary>The record for <paramref name="facts"/>' content tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiBrowseLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's: more columns fit).</summary>
    public static WinUiBrowseLayout For(WidthTier content) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => new WinUiBrowseLayout
        {
            Tier = WidthTier.Compact, BandGap = 16f, HeaderGap = 12f,
            Top = WinUiGridBand.Fixed(2), TopH = 96f, TopDisc = 32f, TopPad = 12f, TopGap = 12f, TopBeside = false,
            ForYou = WinUiGridBand.Fixed(5), ForYouH = 44f, ForYouGap = 8f, ForYouStrip = true, ExploreCell = false,
            Tiles = WinUiGridBand.Fixed(2), TileH = 84f, TilePad = 12f, TileGap = 12f,
            Charts = new WinUiChartsBand(Stacked: true, MaxColumns: 1, TileH: 112f, Cover: 80f),
            Shelf = new WinUiShelfBand(120f, 9999f, 2, FixedCardW: 0f, Gap: 12f, Pager: false),
            Grid = new WinUiShelfBand(120f, 9999f, 2, FixedCardW: 0f, Gap: 12f, Pager: false),
            ShelfGap = 16f, HeaderToolsH = 40f,
        },
        WidthTier.Small => new WinUiBrowseLayout
        {
            Tier = WidthTier.Small, BandGap = 16f, HeaderGap = 12f,
            Top = WinUiGridBand.Fixed(4), TopH = 88f, TopDisc = 32f, TopPad = 12f, TopGap = 12f, TopBeside = false,
            ForYou = WinUiGridBand.Fixed(5), ForYouH = 56f, ForYouGap = 12f, ForYouStrip = false, ExploreCell = false,
            Tiles = new WinUiGridBand(150f, 3), TileH = 88f, TilePad = 12f, TileGap = 12f,
            Charts = new WinUiChartsBand(Stacked: true, MaxColumns: 1, TileH: 96f, Cover: 72f),
            Shelf = new WinUiShelfBand(120f, 9999f, 4, FixedCardW: 0f, Gap: 12f, Pager: true),
            Grid = new WinUiShelfBand(120f, 9999f, 4, FixedCardW: 0f, Gap: 12f, Pager: false),
            ShelfGap = 16f, HeaderToolsH = 32f,
        },
        WidthTier.Standard => new WinUiBrowseLayout
        {
            Tier = WidthTier.Standard, BandGap = 24f, HeaderGap = 12f,
            Top = WinUiGridBand.Fixed(4), TopH = 112f, TopDisc = 40f, TopPad = 12f, TopGap = 16f, TopBeside = false,
            ForYou = WinUiGridBand.Fixed(5), ForYouH = 56f, ForYouGap = 16f, ForYouStrip = false, ExploreCell = false,
            Tiles = new WinUiGridBand(120f, 6), TileH = 88f, TilePad = 12f, TileGap = 16f,
            Charts = new WinUiChartsBand(Stacked: false, MaxColumns: 3, TileH: 72f, Cover: 56f),
            Shelf = new WinUiShelfBand(150f, 9999f, 6, FixedCardW: 0f, Gap: 16f, Pager: false),
            Grid = new WinUiShelfBand(150f, 9999f, 6, FixedCardW: 0f, Gap: 16f, Pager: false),
            ShelfGap = 24f, HeaderToolsH = 32f,
        },
        _ => new WinUiBrowseLayout
        {
            Tier = WidthTier.Huge, BandGap = 24f, HeaderGap = 12f,
            Top = WinUiGridBand.Fixed(4), TopH = 200f, TopDisc = 56f, TopPad = 16f, TopGap = 20f, TopBeside = true,
            ForYou = WinUiGridBand.Fixed(2), ForYouH = 56f, ForYouGap = 12f, ForYouStrip = false, ExploreCell = true,
            Tiles = new WinUiGridBand(150f, 10), TileH = 120f, TilePad = 16f, TileGap = 20f,
            Charts = new WinUiChartsBand(Stacked: false, MaxColumns: 5, TileH: 72f, Cover: 56f),
            Shelf = new WinUiShelfBand(190f, 9999f, 10, FixedCardW: 0f, Gap: 20f, Pager: false),
            Grid = new WinUiShelfBand(190f, 9999f, 10, FixedCardW: 0f, Gap: 20f, Pager: false),
            ShelfGap = 24f, HeaderToolsH = 32f,
        },
    };

    /// <summary>The Top and For you row at Huge: Top 2 shares of the row to For you's 1, 24 apart (W-Browse-Huge).</summary>
    public const float TopShare = 2f, TopRowGap = 24f;
}
