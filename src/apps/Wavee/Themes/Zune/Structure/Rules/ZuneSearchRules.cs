// ── Themes/Zune/Structure/Rules/ZuneSearchRules.cs — the numbers of Zune's Search results page (themes plan 6.3; P5 Z5) ──────────────────────
//
// Board Z-Search (Standard; the Compact / Small / Huge boards feed the tier records, ZT4's). The facet strip is the band's row 2 (the
// host publishes it with the counts), so the page is the facet body alone, on the frame's gutter, 8 under the band, its sections 28 apart:
//
//   top result | best matches   a 440 column (the "top result" heading and the hit's kind word, the 180 art beside the copy 20 apart: the name
//                               40/48 light, its line 13, the matched words 12 in the accent, the rings) 56 from best matches: two columns 40
//                               apart of 56 rows (40 art), at most eight hits
//   playlists                   a shelf of 154 covers 16 apart, its "see all" switching to the Playlists facet
//   genres · related searches   two inline runs of words (15) 20 apart, the two 48 apart

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The numbers and node keys of Zune's Search page.</summary>
public static class ZuneSearchRules
{
    /// <summary>The page, the top result, best matches, the playlist shelf, the genre and related sections.</summary>
    public const string PageKey = "zune-search-page", TopKey = "zune-search-top", BestKey = "zune-search-best", RailKey = "zune-search-rail",
        GenresKey = "zune-search-genres", RelatedKey = "zune-search-related";

    /// <summary>The top result: the gap between its art and the copy, the copy's line gap, the gap to best matches (the art is the tier's).</summary>
    public const float HeroGap = 20f, HeroLineGap = 6f, TopGap = 56f;

    /// <summary>The lines the top result's name may take: it keeps its role's size and wraps by whole words, the card hugging it.</summary>
    public const int NameLines = 2;

    /// <summary>Best matches: the row, its art, the gap between columns, the narrowest column.</summary>
    public const float BestRowH = 56f, BestArt = 40f, BestColGap = 40f, BestMinCol = 280f;

    /// <summary>At most this many best matches.</summary>
    public const int BestMax = 8;

    /// <summary>The inline word runs (genres, related searches): the gap between words, between the two runs.</summary>
    public const float WordGap = 20f, RunsGap = 48f;

    /// <summary>The shimmer's rows and its seed genres.</summary>
    public const int ShimmerRows = 8, ShimmerGenres = 4;
}

/// <summary>Where the top result's name stands: in the copy beside the art (Standard, Huge), over the art row (Compact) or under it (Small).</summary>
public enum ZuneSearchHeroName : byte
{
    /// <summary>The first line of the copy.</summary>
    InCopy,

    /// <summary>Over the art row (Z-Search-Compact).</summary>
    Above,

    /// <summary>Under the art row (Z-Search-Small).</summary>
    Below,
}

/// <summary>What Zune's Search page lays out in one CONTENT tier. Every property defaults to Standard's (Z-Search); a tier's record states its differences.</summary>
public sealed record ZuneSearchLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The gap between the page's sections.</summary>
    public float ZoneGap { get; init; } = 28f;

    /// <summary>The top result stands beside best matches (its width, the gap to them), or over them.</summary>
    public bool TopBeside { get; init; } = true;

    /// <inheritdoc cref="TopBeside"/>
    public float TopW { get; init; } = 440f;

    /// <inheritdoc cref="TopBeside"/>
    public float TopGap { get; init; } = ZuneSearchRules.TopGap;

    /// <summary>The top result's art; the art stands OVER its copy (Huge) instead of beside it; where its name stands; the name in the 64/72 hero voice;
    /// the ring disc and whether the rings carry their words.</summary>
    public float HeroArt { get; init; } = 180f;

    /// <inheritdoc cref="HeroArt"/>
    public bool HeroStacked { get; init; }

    /// <inheritdoc cref="HeroArt"/>
    public ZuneSearchHeroName HeroName { get; init; } = ZuneSearchHeroName.InCopy;

    /// <inheritdoc cref="HeroArt"/>
    public bool HeroNameHero { get; init; }

    /// <inheritdoc cref="HeroArt"/>
    public float RingSize { get; init; } = ZuneHomeRules.RingSize;

    /// <inheritdoc cref="HeroArt"/>
    public bool RingLabels { get; init; } = true;

    /// <summary>Best matches' and the songs' columns at most, the narrowest column, the row and its art.</summary>
    public int BestCols { get; init; } = 2;

    /// <inheritdoc cref="BestCols"/>
    public float BestMinCol { get; init; } = ZuneSearchRules.BestMinCol;

    /// <inheritdoc cref="BestCols"/>
    public float BestRowH { get; init; } = ZuneSearchRules.BestRowH;

    /// <inheritdoc cref="BestCols"/>
    public float BestArt { get; init; } = ZuneSearchRules.BestArt;

    /// <summary>The playlist shelf: 0 = the fixed 154 card (Standard), else it fits up to this many cards across, none narrower than the floor.</summary>
    public int ShelfCols { get; init; }

    /// <inheritdoc cref="ShelfCols"/>
    public float ShelfMinW { get; init; } = ZuneHomeRules.ShelfCardW;

    /// <summary>The genres and related searches stand side by side.</summary>
    public bool RunsBeside { get; init; } = true;

    /// <summary>The minimum hit target of a link word (40 at Compact, 0 = the word's own size elsewhere).</summary>
    public float LinkTarget { get; init; }

    /// <summary>The Genres facet's link columns and row.</summary>
    public int GenreCols { get; init; } = 6;

    /// <inheritdoc cref="GenreCols"/>
    public float GenreRowH { get; init; } = ZuneBrowseRules.LinkRowH;
}

/// <summary>Zune's Search tier records (content tier; ZT4 tuned Compact, Small and Huge against Z-Search-*).</summary>
public static class ZuneSearchTiers
{
    private static ZuneSearchLayout Std(WidthTier tier) => new() { Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter };

    // Huge's numbers (Z-Search-Huge); Huge 4K is the same (pages cap at 2400, decision 2).
    private static ZuneSearchLayout Huge(WidthTier tier) => Std(tier) with
    {
        ZoneGap = 40f, TopW = 560f, HeroArt = 240f, HeroStacked = true, HeroNameHero = true,
        BestCols = 3, BestRowH = 60f, BestArt = 48f, ShelfCols = 8, ShelfMinW = 140f, GenreCols = 8,
    };

    private static readonly ZuneSearchLayout[] s_records =
    [
        // Compact (Z-Search-Compact): 20 between sections, one column: the top result's 40/48 name over a 120 art and its copy (40 rings, no words),
        // best matches as 56 rows (44 art), the playlist shelf two across.
        Std(WidthTier.Compact) with
        {
            ZoneGap = 20f, TopBeside = false, HeroArt = 120f, HeroName = ZuneSearchHeroName.Above, RingSize = 40f, RingLabels = false,
            BestCols = 1, BestRowH = 56f, BestArt = 44f, ShelfCols = 2, ShelfMinW = 120f, RunsBeside = false, GenreCols = 2, GenreRowH = 40f, LinkTarget = 40f,
        },
        // Small (Z-Search-Small): 18 between sections; a 280 top-result column (a 120 art and its copy, the name UNDER them) beside one column of 52 rows
        // (40 art), 32 apart; the shelf five across.
        Std(WidthTier.Small) with
        {
            ZoneGap = 18f, TopW = 280f, TopGap = 32f, HeroArt = 120f, HeroName = ZuneSearchHeroName.Below, RingLabels = false,
            BestCols = 1, BestMinCol = 220f, BestRowH = 52f, ShelfCols = 5, ShelfMinW = 100f, RunsBeside = false, GenreCols = 3,
        },
        // Standard (Z-Search): the tuned page (the defaults).
        Std(WidthTier.Standard),
        // Huge (Z-Search-Huge): a 560 top-result column (wide enough for the 64/72 name; it was 480 and truncated) (a 240 art over the 64/72 name and its lines), three columns of 60 rows (48 art), eight-across shelf.
        Huge(WidthTier.Huge),
        Huge(WidthTier.Huge4K),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneSearchLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ZuneSearchLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
