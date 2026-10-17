// ── Themes/Zune/Structure/Rules/ZuneSearchRules.cs — the numbers of Zune's Search results page (themes plan 6.3; P5 Z5) ──────────────────────
//
// Board Z-Search (Standard; the Compact / Small / Huge boards feed the tier records, which ZT4 tunes). The facet strip is the band's row 2 (the
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

namespace Wavee.Themes.Zune;

/// <summary>The numbers and node keys of Zune's Search page.</summary>
public static class ZuneSearchRules
{
    /// <summary>The page, the top result, best matches, the playlist shelf, the genre and related sections.</summary>
    public const string PageKey = "zune-search-page", TopKey = "zune-search-top", BestKey = "zune-search-best", RailKey = "zune-search-rail",
        GenresKey = "zune-search-genres", RelatedKey = "zune-search-related";

    /// <summary>The top result: the gap between its art and the copy, the copy's line gap, the gap to best matches (the art is the tier's).</summary>
    public const float HeroGap = 20f, HeroLineGap = 6f, TopGap = 56f;

    /// <summary>Best matches: the row, its art, the gap between columns, the narrowest column.</summary>
    public const float BestRowH = 56f, BestArt = 40f, BestColGap = 40f, BestMinCol = 280f;

    /// <summary>At most this many best matches.</summary>
    public const int BestMax = 8;

    /// <summary>The inline word runs (genres, related searches): the gap between words, between the two runs.</summary>
    public const float WordGap = 20f, RunsGap = 48f;

    /// <summary>The shimmer's rows and its seed genres.</summary>
    public const int ShimmerRows = 8, ShimmerGenres = 4;
}

/// <summary>What Zune's Search page lays out in one CONTENT tier.</summary>
public sealed record ZuneSearchLayout : ITierLayoutRecord
{
    /// <inheritdoc />
    public required WidthTier Tier { get; init; }

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions { get; init; } = ImmutableArray<TierRegion>.Empty;

    /// <summary>The page's gutter (the frame's).</summary>
    public required float Gutter { get; init; }

    /// <summary>The top result stands beside best matches (its width), or over them.</summary>
    public required bool TopBeside { get; init; }

    /// <inheritdoc cref="TopBeside"/>
    public required float TopW { get; init; }

    /// <summary>The top result's art.</summary>
    public required float HeroArt { get; init; }

    /// <summary>Best matches' and the songs' columns at most.</summary>
    public required int BestCols { get; init; }

    /// <summary>The genres and related searches stand side by side.</summary>
    public required bool RunsBeside { get; init; }

    /// <summary>The Genres facet's link columns.</summary>
    public required int GenreCols { get; init; }
}

/// <summary>Zune's Search tier records (content tier).</summary>
public static class ZuneSearchTiers
{
    private static ZuneSearchLayout Make(WidthTier tier, bool topBeside, float topW, float heroArt, int bestCols, bool runsBeside, int genreCols) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, TopBeside = topBeside, TopW = topW, HeroArt = heroArt, BestCols = bestCols,
        RunsBeside = runsBeside, GenreCols = genreCols,
    };

    private static readonly ZuneSearchLayout[] s_records =
    [
        // Compact (Z-Search-Compact): everything stacks, one column.
        Make(WidthTier.Compact, topBeside: false, topW: 0f, heroArt: 96f, bestCols: 1, runsBeside: false, genreCols: 2),
        // Small (Z-Search-Small): the top result over best matches, two columns.
        Make(WidthTier.Small, topBeside: false, topW: 0f, heroArt: 140f, bestCols: 2, runsBeside: false, genreCols: 3),
        // Standard (Z-Search): the tuned page.
        Make(WidthTier.Standard, topBeside: true, topW: 440f, heroArt: 180f, bestCols: 2, runsBeside: true, genreCols: 6),
        // Huge (Z-Search-Huge): three best-match columns.
        Make(WidthTier.Huge, topBeside: true, topW: 520f, heroArt: 220f, bestCols: 3, runsBeside: true, genreCols: 8),
        // Huge 4K: Huge's (pages cap at 2400, decision 2).
        Make(WidthTier.Huge4K, topBeside: true, topW: 520f, heroArt: 220f, bestCols: 3, runsBeside: true, genreCols: 8),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneSearchLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ZuneSearchLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}
