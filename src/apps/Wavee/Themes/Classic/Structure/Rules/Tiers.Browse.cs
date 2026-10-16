// ── Themes/Classic/Structure/Rules/Tiers.Browse.cs — Classic's Browse directory, category page and genre links per content tier (themes plan 3.6; T10a2, TC4) ──
//
// The three column functions `Entities\Browse.Rules.cs`'s `BrowseLayout` held (0.2.9 `BrowseTiles.cs:286-358`; T10a2 moved them here: the Genres 720 / 380 bands
// and the Mood and More floor-fits) are now RECORD data: one `ClassicBrowseLayout` per CONTENT tier, read by the views through
// `ClassicBrowseTiers.Of(ctx.Tier())`. Nothing compares a width any more (WVTH013): a grid's column count is the record's `ClassicGridBand` (fixed, or a floor-fit
// INSIDE the tier at the band's own measured width), and the first frame of every width-driven box is seeded from `ClassicRegion.EstimateWidth`, not a fixed 900 / 1100.
//
//   tier      genres   mood           more         charts                                   category shelf                          chips (Top / For you)
//   Compact   2        2 (fit 132)    2 (fit 168)  stacked, 136 high, 92 covers             148 art strip (164 cell), title 40     40 high pills / one 40 high strip
//   Small     2        5 (fit 132)    3            stacked, 140 high, 100 covers            176 cell strip, 4 gap                  32 / wrapping
//   Standard  3        fit 132        4 (fit 168)  2 up, 176 high, 124 covers               fit 148-188 (7 at 1368)                32 / wrapping (tuned)
//   Huge      6        12             8            4 up, 176 high, 124 covers               10 columns, "See all"                  32 / wrapping, band gap 28
//
// Boards: C-Browse, C-Browse-Compact, C-Browse-Small, C-Browse-Huge, C-BrowseCategory (+ -Compact, -Small, -Huge). Huge 4K is Huge (no 4K band; the grids stop at
// 2400 on the Home page's rule).
//
// Public, because `BrowseLayoutTests` pins the records.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The Fold deck's tile geometry (Browse's Charts band) in one tier.</summary>
/// <param name="Stacked">The tiles stack in one column (Compact, Small) instead of a paged two-up deck.</param>
/// <param name="MaxColumns">The most tiles side by side in the paged deck.</param>
/// <param name="TileH">The tile's height.</param>
/// <param name="Cover">The fanned covers' edge.</param>
public readonly record struct ClassicChartsBand(bool Stacked, int MaxColumns, float TileH, float Cover);

/// <summary>What Classic's Browse pages are in one content tier.</summary>
public sealed record ClassicBrowseLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The step between the directory's bands.</summary>
    public required float BandGap { get; init; }

    /// <summary>Top's chip height (0 keeps the stock 32).</summary>
    public required float TopChipH { get; init; }

    /// <summary>Top's chip corner radius; negative keeps the stock 4 (Compact's are pills).</summary>
    public required float TopChipRadius { get; init; }

    /// <summary>Top's chip side padding; negative keeps the stock.</summary>
    public required float TopChipPadX { get; init; }

    /// <summary>For you's chip height (0 keeps the stock 32).</summary>
    public required float ForYouChipH { get; init; }

    /// <summary>For you is ONE scrolling strip with a faded edge instead of a wrapping block (Compact).</summary>
    public required bool ForYouStrip { get; init; }

    /// <summary>The Genres grid (also a category page's link blocks and Search's genre links): fixed columns.</summary>
    public required ClassicGridBand Genres { get; init; }

    /// <summary>A genre link's minimum height (40 at Compact: the target floor; 0 keeps the cell's own).</summary>
    public required float LinkMinH { get; init; }

    /// <summary>The gap between two genre rows.</summary>
    public required float LinkRowGap { get; init; }

    /// <summary>The Mood plates' columns.</summary>
    public required ClassicGridBand Moods { get; init; }

    /// <summary>The More plates' columns.</summary>
    public required ClassicGridBand More { get; init; }

    /// <summary>The Charts band.</summary>
    public required ClassicChartsBand Charts { get; init; }

    /// <summary>A category page's shelf.</summary>
    public required ClassicShelfBand Shelf { get; init; }

    /// <summary>A category page's shelf header: the title's slot height (40 at Compact: the target floor; 0 keeps the header's own).</summary>
    public required float ShelfHeaderH { get; init; }

    /// <summary>A category page's shelf header carries a "See all" link beside its title (Huge).</summary>
    public required bool ShelfSeeAll { get; init; }

    /// <summary>A category page's flattened grid: the narrowest and widest card and the most columns (0 is unlimited).</summary>
    public required ClassicShelfBand Grid { get; init; }

    /// <summary>The Mood grid's columns at <paramref name="width"/>: <c>floor(width / 132)</c> held to the tier's cap, as before tiers.</summary>
    public int MoodColumns(float width) => Moods.Columns(width);

    /// <summary>The More grid's columns at <paramref name="width"/>: <c>floor(width / 168)</c> held to the tier's cap, as before tiers.</summary>
    public int MoreColumns(float width) => More.Columns(width);

    /// <summary>The Genres grid's columns (fixed per tier).</summary>
    public int GenreColumns() => Genres.Columns(0f);
}

/// <summary>Classic's Browse records.</summary>
public static class ClassicBrowseTiers
{
    /// <summary>The Mood grid's column floor (the board's 132).</summary>
    public const float BarColMin = 132f;

    /// <summary>The More grid's column floor (the board's 168).</summary>
    public const float MoreColMin = 168f;

    /// <summary>The record for <paramref name="facts"/>' content tier; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicBrowseLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's).</summary>
    public static ClassicBrowseLayout For(WidthTier content) => (WidthTier)Math.Min((int)content, (int)WidthTier.Huge) switch
    {
        WidthTier.Compact => new ClassicBrowseLayout
        {
            Tier = WidthTier.Compact, BandGap = 14f,
            TopChipH = 40f, TopChipRadius = 20f, TopChipPadX = 16f, ForYouChipH = 40f, ForYouStrip = true,
            Genres = ClassicGridBand.Fixed(2), LinkMinH = 40f, LinkRowGap = 0f,
            Moods = new ClassicGridBand(BarColMin, 2), More = new ClassicGridBand(MoreColMin, 2),
            Charts = new ClassicChartsBand(Stacked: true, MaxColumns: 1, TileH: 136f, Cover: 92f),
            Shelf = new ClassicShelfBand(148f, 188f, 0, FixedCardW: 164f, Gap: 0f, Pager: false), ShelfHeaderH = 40f, ShelfSeeAll = false,
            Grid = new ClassicShelfBand(124f, 188f, 0, FixedCardW: 0f, Gap: Spacing.M, Pager: false),
        },
        WidthTier.Small => new ClassicBrowseLayout
        {
            Tier = WidthTier.Small, BandGap = Spacing.L,
            TopChipH = 0f, TopChipRadius = -1f, TopChipPadX = -1f, ForYouChipH = 0f, ForYouStrip = false,
            Genres = ClassicGridBand.Fixed(2), LinkMinH = 0f, LinkRowGap = Spacing.S,
            Moods = new ClassicGridBand(BarColMin, 5), More = new ClassicGridBand(MoreColMin, 3),
            Charts = new ClassicChartsBand(Stacked: true, MaxColumns: 1, TileH: 140f, Cover: 100f),
            Shelf = new ClassicShelfBand(176f, 176f, 0, FixedCardW: 176f, Gap: 4f, Pager: false), ShelfHeaderH = 0f, ShelfSeeAll = false,
            Grid = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: Spacing.M, Pager: false),
        },
        WidthTier.Standard => new ClassicBrowseLayout
        {
            Tier = WidthTier.Standard, BandGap = Spacing.L,
            TopChipH = 0f, TopChipRadius = -1f, TopChipPadX = -1f, ForYouChipH = 0f, ForYouStrip = false,
            Genres = ClassicGridBand.Fixed(3), LinkMinH = 0f, LinkRowGap = Spacing.S,
            Moods = new ClassicGridBand(BarColMin, int.MaxValue), More = new ClassicGridBand(MoreColMin, 4),
            Charts = new ClassicChartsBand(Stacked: false, MaxColumns: 2, TileH: HomeModuleLayout.FoldCardHeight, Cover: HomeModuleLayout.FoldCover),
            Shelf = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            ShelfHeaderH = 0f, ShelfSeeAll = false,
            Grid = new ClassicShelfBand(R.Space.ShelfCardMin, R.Space.ShelfCardMax, 0, FixedCardW: 0f, Gap: Spacing.M, Pager: false),
        },
        _ => new ClassicBrowseLayout
        {
            Tier = WidthTier.Huge, BandGap = 28f,
            TopChipH = 0f, TopChipRadius = -1f, TopChipPadX = -1f, ForYouChipH = 0f, ForYouStrip = false,
            Genres = ClassicGridBand.Fixed(6), LinkMinH = 0f, LinkRowGap = Spacing.S,
            Moods = new ClassicGridBand(BarColMin, 12), More = new ClassicGridBand(MoreColMin, 8),
            Charts = new ClassicChartsBand(Stacked: false, MaxColumns: 4, TileH: HomeModuleLayout.FoldCardHeight, Cover: HomeModuleLayout.FoldCover),
            Shelf = new ClassicShelfBand(R.Space.ShelfCardMin, 9999f, 10, FixedCardW: 0f, Gap: HomeModuleLayout.ShelfGap, Pager: true),
            ShelfHeaderH = 0f, ShelfSeeAll = true,
            Grid = new ClassicShelfBand(R.Space.ShelfCardMin, 9999f, 10, FixedCardW: 0f, Gap: Spacing.M, Pager: false),
        },
    };
}
