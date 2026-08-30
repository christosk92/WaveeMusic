using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>The Browse directory's loading-skeleton seed categories — one representative uri per band, culled from
/// <see cref="BrowseTaxonomy"/>'s own map, plus a few unmapped uris so <see cref="BrowseTaxonomy.Grouped"/> still
/// buckets items into <see cref="BrowseGroup.More"/>. Titles are a single space — the same placeholder
/// <c>HomeBrowseCards.ChartDeckSeed</c> uses — because the rendered skeleton is marked <c>.Skeletonized(true)</c>;
/// the text itself is never shown.
///
/// Split out of <see cref="BrowseDirectory"/> (engine-free by construction — System + Wavee.Core only) so it can be
/// source-included into Wavee.Tests without dragging in the engine-bound component: the skeleton's SHAPE is these
/// seeds' taxonomy grouping, and <c>BrowseChartTaxonomyTests</c> pins the per-band counts so an unrelated edit to
/// <see cref="BrowseTaxonomy"/>'s Map cannot silently reshape the loading directory (a category moving bands would
/// change how many skeleton rows each band shows, with nothing in a diff of <c>BrowseDirectory.cs</c> to say so).</summary>
internal static class BrowseDirectorySeeds
{
    internal static readonly IReadOnlyList<BrowseCategory> Categories =
    [
        new BrowseCategory("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", " ", null),                // Top: Music
        new BrowseCategory("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", " ", null),                // Top: Podcasts
        new BrowseCategory("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", " ", null),                // Top: Audiobooks
        new BrowseCategory("spotify:concerts", " ", null, IsClientFeature: true),            // Top: Live Events
        new BrowseCategory("spotify:page:0JQ5DAtOnAEpjOgUKwXyxj", " ", null),                // For you: Discover
        new BrowseCategory("spotify:page:0JQ5DAqbMKFPw634sFwguI", " ", null),                // For you: EQUAL
        new BrowseCategory("spotify:page:0JQ5DAqbMKFImHYGo3eTSg", " ", null),                // For you: Fresh Finds
        new BrowseCategory("spotify:page:0JQ5DAqbMKFNQ0fGp4byGU", " ", null),                // Genres: Afro
        new BrowseCategory("spotify:page:0JQ5DAqbMKFFtlLYUHv8bT", " ", null),                // Genres: Alternative
        new BrowseCategory("spotify:page:0JQ5DAqbMKFLjmiZRss79w", " ", null),                // Genres: Ambient
        new BrowseCategory("spotify:page:0JQ5DAqbMKFx0uLQR2okcc", " ", null),                // Mood & activity: At Home
        new BrowseCategory("spotify:page:0JQ5DAqbMKFFzDl7qN9Apr", " ", null),                // Mood & activity: Chill
        new BrowseCategory("spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0", " ", null),                // Mood & activity: Cooking & Dining
        new BrowseCategory("spotify:page:skeleton-more-1", " ", null),                       // More (deliberately unmapped)
        new BrowseCategory("spotify:page:skeleton-more-2", " ", null),
        new BrowseCategory("spotify:page:skeleton-more-3", " ", null),
    ];
}
