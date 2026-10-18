// ── Themes/Zune/Structure/Rules/ZuneBrowseRules.cs — the numbers of Zune's Browse directory and category pages (themes plan 6.3; P5 Z5) ─────
//
// Boards Z-Browse (its own row 2 is superseded by the band's Browse views, Q30) and Z-Browse-Category; the Compact / Small / Huge boards feed the
// tier records (ZT4).
//
//   THE DIRECTORY (Standard): the frame's gutter, 8 under the band, sections 28 apart, each a 26/32 light heading 10 over its body.
//     top              the Top categories as flat plates 120 tall, four across (no board draws them: Z-Browse put them in its row 2)
//     charts | for you the chart categories as two 120 plates (their 22 light name) in a 420 column beside For you, 48 apart; For you a
//                      three-column list of 36 rows, a 10 square of the category's colour before its name (15)
//     genres           a six-column list of 32 rows, the names in the secondary ink (15)
//     mood & activity  six-across flat 96 plates in the category's colour, the name (15) at the bottom left
//     more             a six-column list of 32 rows
//   THE CATEGORY PAGE: a 200 banner in the accent with the category's name as a panorama (120/130 at 200) 40 in from its top left and its kind
//     13 at the bottom right (48 in, 24 up); then the page's sections 28 apart, 28 under the banner: shelves of 154 covers 16 apart with
//     "see all", tile blocks as six-across 96 plates.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers and node keys of Zune's Browse pages.</summary>
public static class ZuneBrowseRules
{
    /// <summary>The directory's column, a band's section (`zune-browse-band:<group>`), the category page's banner and its title, a category section.</summary>
    public const string DirectoryKey = "zune-browse-directory", BandPrefix = "zune-browse-band:", BannerKey = "zune-browse-banner",
        BannerTitleKey = "zune-browse-banner-title", CategoryKey = "zune-browse-category", CategorySectionPrefix = "zune-browse-section:";

    /// <summary>The gap between the charts column and For you.</summary>
    public const float PairGap = 48f;

    /// <summary>A plate's name in the chart and Top plates: the 22 light voice is the QuickplayHead role; the plates' gap.</summary>
    public const float PlateGap = 12f;

    /// <summary>The list rows: For you's (with its colour square), the genres' and More's; the gap between list columns.</summary>
    public const float ForYouRowH = 36f, LinkRowH = 32f, ListColGap = 24f, DotSize = 10f, DotGap = 12f;

    /// <summary>The narrowest list column and plate.</summary>
    public const float ListMinCol = 120f, PlateMinCol = 120f;

    /// <summary>The category banner: the title's inset from the top left, the kind's inset from the bottom right.</summary>
    public const float BannerTitleInset = 40f, BannerTitleInsetY = 26f, BannerMetaInsetX = 48f, BannerMetaInsetY = 24f;

    /// <summary>The category page's top under the banner and its foot.</summary>
    public const float CategoryTop = 28f, CategoryBottom = 32f;

    /// <summary>A shimmer shelf's cards.</summary>
    public const int ShimmerCards = 6;
}

/// <summary>What Zune's Browse pages lay out in one CONTENT tier. Every property defaults to Standard's (Z-Browse, Z-Browse-Category); a tier's record
/// states its differences.</summary>
public sealed record ZuneBrowseLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between the directory's bands; the plates' gap.</summary>
    public float ZoneGap { get; init; } = 28f;

    /// <inheritdoc cref="ZoneGap"/>
    public float PlateGap { get; init; } = ZuneBrowseRules.PlateGap;

    /// <summary>The charts column stands beside For you (its width, the gap between them), or above it (the page's width).</summary>
    public bool ChartsBeside { get; init; } = true;

    /// <inheritdoc cref="ChartsBeside"/>
    public float ChartsW { get; init; } = 420f;

    /// <inheritdoc cref="ChartsBeside"/>
    public float PairGap { get; init; } = ZuneBrowseRules.PairGap;

    /// <summary>The chart and Top plates' height, their name in the 40/48 voice (Huge) instead of the 22/28 one, and the Top plates across.</summary>
    public float BigPlateH { get; init; } = 120f;

    /// <inheritdoc cref="BigPlateH"/>
    public bool BigPlateHero { get; init; }

    /// <inheritdoc cref="BigPlateH"/>
    public int TopCols { get; init; } = 4;

    /// <summary>For you: its columns, its row, the square before each name (the dot at Standard, a 44 colour tile at Compact), and (Huge) plates
    /// of the given height instead of a list.</summary>
    public int ForYouCols { get; init; } = 3;

    /// <inheritdoc cref="ForYouCols"/>
    public float ForYouRowH { get; init; } = ZuneBrowseRules.ForYouRowH;

    /// <inheritdoc cref="ForYouCols"/>
    public float ForYouTile { get; init; } = ZuneBrowseRules.DotSize;

    /// <inheritdoc cref="ForYouCols"/>
    public bool ForYouPlates { get; init; }

    /// <inheritdoc cref="ForYouCols"/>
    public float ForYouPlateH { get; init; } = 100f;

    /// <summary>The genres' and More's columns and row; the mood plates across and their height.</summary>
    public int LinkCols { get; init; } = 6;

    /// <inheritdoc cref="LinkCols"/>
    public float LinkRowH { get; init; } = ZuneBrowseRules.LinkRowH;

    /// <inheritdoc cref="LinkCols"/>
    public int MoodCols { get; init; } = 6;

    /// <inheritdoc cref="LinkCols"/>
    public float MoodH { get; init; } = 96f;

    // ── the category page ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The category banner's height, and whether its title is the compact panorama (64/72 instead of 120/130).</summary>
    public float BannerH { get; init; } = 200f;

    /// <inheritdoc cref="BannerH"/>
    public bool CompactTitle { get; init; }

    /// <summary>The title's own size, line and tracking where the tier states them (Huge: 150/160/-3 on Z-Browse-Category-Huge); 0 = the role's.</summary>
    public float TitleSize { get; init; }

    /// <inheritdoc cref="TitleSize"/>
    public float TitleLine { get; init; }

    /// <inheritdoc cref="TitleSize"/>
    public float TitleTracking { get; init; }

    /// <summary>The title's inset from the banner's left and top; the kind's inset from its side (the left at Compact, else the right) and bottom.</summary>
    public float BannerInsetX { get; init; } = ZuneBrowseRules.BannerTitleInset;

    /// <inheritdoc cref="BannerInsetX"/>
    public float BannerInsetY { get; init; } = ZuneBrowseRules.BannerTitleInsetY;

    /// <inheritdoc cref="BannerInsetX"/>
    public float BannerMetaX { get; init; } = ZuneBrowseRules.BannerMetaInsetX;

    /// <inheritdoc cref="BannerInsetX"/>
    public float BannerMetaY { get; init; } = ZuneBrowseRules.BannerMetaInsetY;

    /// <inheritdoc cref="BannerInsetX"/>
    public bool BannerMetaStart { get; init; }

    /// <summary>The minimum hit target of a link word (40 at Compact, 0 = the word's own size elsewhere).</summary>
    public float LinkTarget { get; init; }

    /// <summary>The sections' top under the banner and the gap between them.</summary>
    public float CategoryTop { get; init; } = ZuneBrowseRules.CategoryTop;

    /// <inheritdoc cref="CategoryTop"/>
    public float CategoryGap { get; init; } = 28f;

    /// <summary>A shelf: 0 = the fixed 154 card (Standard), else it fits up to this many cards across, none narrower than the floor.</summary>
    public int ShelfCols { get; init; }

    /// <inheritdoc cref="ShelfCols"/>
    public float ShelfMinW { get; init; } = ZuneHomeRules.ShelfCardW;
}

/// <summary>Zune's Browse tier records (content tier; ZT4 tuned Compact, Small and Huge against Z-Browse-* and Z-Browse-Category-*).</summary>
public static class ZuneBrowseTiers
{
    private static ZuneBrowseLayout Std(WidthTier tier) => new() { Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter };

    // Huge's numbers (Z-Browse-Huge, Z-Browse-Category-Huge); Huge 4K is the same (pages cap at 2400, decision 2).
    private static ZuneBrowseLayout Huge(WidthTier tier) => Std(tier) with
    {
        ZoneGap = 40f, PlateGap = 16f, ChartsW = 640f, BigPlateH = 216f, BigPlateHero = true,
        ForYouCols = 5, ForYouPlates = true, ForYouPlateH = 100f, LinkCols = 8, LinkRowH = 40f, MoodCols = 8, MoodH = 140f,
        BannerH = 260f, TitleSize = 150f, TitleLine = 160f, TitleTracking = -3f, BannerInsetX = 38f, BannerInsetY = 28f, BannerMetaY = 28f, CategoryTop = 32f, CategoryGap = 36f, ShelfCols = 8, ShelfMinW = 140f,
    };

    private static readonly ZuneBrowseLayout[] s_records =
    [
        // Compact (Z-Browse-Compact, Z-Browse-Category-Compact): 22 between bands; everything stacks, two across (16 apart), 104 plates with the 22/28
        // name, For you as 56 rows with a 44 colour tile, 88 mood plates; a 140 banner (the 64/72 title 12 in, the kind at the bottom left), two-across shelves.
        Std(WidthTier.Compact) with
        {
            ZoneGap = 22f, PlateGap = 16f, ChartsBeside = false, BigPlateH = 104f, TopCols = 2, ForYouCols = 1, ForYouRowH = 56f, ForYouTile = 44f,
            LinkCols = 2, LinkRowH = 40f, MoodCols = 2, MoodH = 88f, LinkTarget = 40f,
            BannerH = 140f, CompactTitle = true, BannerInsetX = 12f, BannerInsetY = 15f, BannerMetaX = 16f, BannerMetaY = 14f, BannerMetaStart = true,
            CategoryTop = 20f, CategoryGap = 22f, ShelfCols = 2, ShelfMinW = 120f,
        },
        // Small (Z-Browse-Small, Z-Browse-Category-Small): 20 between bands; a 280 charts column BESIDE For you (32 apart; its two-column dot list), four
        // genre columns, five 80 moods; a 120 banner (the 64/72 title 18 in, the kind at the bottom right), five-across shelves.
        Std(WidthTier.Small) with
        {
            ZoneGap = 20f, ChartsW = 280f, PairGap = 32f, BigPlateH = 104f, TopCols = 2, ForYouCols = 2, LinkCols = 4, MoodCols = 5, MoodH = 80f,
            BannerH = 120f, CompactTitle = true, BannerInsetX = 18f, BannerInsetY = 17f, BannerMetaX = 24f, BannerMetaY = 16f,
            CategoryTop = 20f, CategoryGap = 20f, ShelfCols = 5, ShelfMinW = 100f,
        },
        // Standard (Z-Browse, Z-Browse-Category): the tuned page (the defaults).
        Std(WidthTier.Standard),
        // Huge (Z-Browse-Huge, Z-Browse-Category-Huge): 40 between bands; a 640 charts column of 216 plates (the 40/48 name) beside For you as five
        // 100 plates; eight genre columns of 40 rows, eight 140 moods; a 260 banner; eight-across shelves.
        Huge(WidthTier.Huge),
        Huge(WidthTier.Huge4K),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneBrowseLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share one left edge).</summary>
    public static ZuneBrowseLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static ZuneBrowseLayout OfContent(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
