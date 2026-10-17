// ── Themes/Zune/Structure/Rules/ZuneBrowseRules.cs — the numbers of Zune's Browse directory and category pages (themes plan 6.3; P5 Z5) ─────
//
// Boards Z-Browse (its own row 2 is superseded by the band's Browse views, Q30) and Z-Browse-Category; the Compact / Small / Huge boards feed the
// tier records, which ZT4 tunes.
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
    public const float BannerTitleInset = 40f, BannerMetaInsetX = 48f, BannerMetaInsetY = 24f;

    /// <summary>The category page's top under the banner and its foot.</summary>
    public const float CategoryTop = 28f, CategoryBottom = 32f;

    /// <summary>A shimmer shelf's cards.</summary>
    public const int ShimmerCards = 6;
}

/// <summary>What Zune's Browse pages lay out in one CONTENT tier.</summary>
public sealed record ZuneBrowseLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The charts column stands beside For you (its width), or above it (the page's width).</summary>
    public required bool ChartsBeside { get; init; }

    /// <inheritdoc cref="ChartsBeside"/>
    public required float ChartsW { get; init; }

    /// <summary>The chart and Top plates' height, and the Top plates across.</summary>
    public required float BigPlateH { get; init; }

    /// <inheritdoc cref="BigPlateH"/>
    public required int TopCols { get; init; }

    /// <summary>For you's columns, the genres' and More's columns, the mood plates across and their height.</summary>
    public required int ForYouCols { get; init; }

    /// <inheritdoc cref="ForYouCols"/>
    public required int LinkCols { get; init; }

    /// <inheritdoc cref="ForYouCols"/>
    public required int MoodCols { get; init; }

    /// <inheritdoc cref="ForYouCols"/>
    public required float MoodH { get; init; }

    /// <summary>The category banner's height, and whether its title is the compact panorama.</summary>
    public required float BannerH { get; init; }

    /// <inheritdoc cref="BannerH"/>
    public required bool CompactTitle { get; init; }
}

/// <summary>Zune's Browse tier records (content tier).</summary>
public static class ZuneBrowseTiers
{
    private static ZuneBrowseLayout Make(WidthTier tier, bool chartsBeside, float chartsW, float bigPlateH, int topCols, int forYouCols, int linkCols,
        int moodCols, float moodH, float bannerH, bool compactTitle) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, ChartsBeside = chartsBeside, ChartsW = chartsW, BigPlateH = bigPlateH, TopCols = topCols,
        ForYouCols = forYouCols, LinkCols = linkCols, MoodCols = moodCols, MoodH = moodH, BannerH = bannerH, CompactTitle = compactTitle,
    };

    private static readonly ZuneBrowseLayout[] s_records =
    [
        // Compact (Z-Browse-Compact, Z-Browse-Category-Compact): everything stacks, two across, the 104 plates, a 140 banner with the compact title.
        Make(WidthTier.Compact, chartsBeside: false, chartsW: 0f, bigPlateH: 104f, topCols: 2, forYouCols: 1, linkCols: 2, moodCols: 2, moodH: 88f,
            bannerH: 140f, compactTitle: true),
        // Small (Z-Browse-Small): the charts above For you, two-column For you, four genre columns, five moods.
        Make(WidthTier.Small, chartsBeside: false, chartsW: 0f, bigPlateH: 104f, topCols: 2, forYouCols: 2, linkCols: 4, moodCols: 5, moodH: 80f,
            bannerH: 160f, compactTitle: true),
        // Standard (Z-Browse, Z-Browse-Category): the tuned page.
        Make(WidthTier.Standard, chartsBeside: true, chartsW: 420f, bigPlateH: 120f, topCols: 4, forYouCols: 3, linkCols: 6, moodCols: 6, moodH: 96f,
            bannerH: 200f, compactTitle: false),
        // Huge (Z-Browse-Huge, Z-Browse-Category-Huge): a 640 charts column, the 216 plates, eight across.
        Make(WidthTier.Huge, chartsBeside: true, chartsW: 640f, bigPlateH: 216f, topCols: 4, forYouCols: 4, linkCols: 8, moodCols: 8, moodH: 96f,
            bannerH: 260f, compactTitle: false),
        // Huge 4K: Huge's (pages cap at 2400, decision 2).
        Make(WidthTier.Huge4K, chartsBeside: true, chartsW: 640f, bigPlateH: 216f, topCols: 4, forYouCols: 4, linkCols: 8, moodCols: 8, moodH: 96f,
            bannerH: 260f, compactTitle: false),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneBrowseLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ZuneBrowseLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
