using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>The Browse directory's loading-skeleton seed categories. Top / For you / Genres / Mood &amp; activity
/// mirror <see cref="BrowseTaxonomy"/>'s own map ENTRY FOR ENTRY (not a representative subset) plus a few unmapped
/// uris so <see cref="BrowseTaxonomy.Grouped"/> still buckets items into <see cref="BrowseGroup.More"/>. Titles are
/// a single space — the same placeholder <c>HomeBrowseCards.ChartDeckSeed</c> uses — because the rendered skeleton
/// is marked <c>.Skeletonized(true)</c>; the text itself is never shown.
///
/// A THREE-item-per-band placeholder used to stand in here (evidence: Batch D / va S3 #16) — cheap to author, but
/// For you (really 10 destinations) and Genres (really 25) then wrap to one shimmer row while the loaded page wraps
/// to three or four, so the whole page height jumps the instant real data lands. The taxonomy Map is a COMPILE-TIME
/// constant (uri-keyed, never server-driven — see BrowseTaxonomy's own doc comment), so the true per-band count is
/// knowable up front and reserving it costs nothing: mirroring it here is what makes the skeleton's height the
/// loaded page's own height instead of a guess. The one band that genuinely CANNOT be predicted is More — anything
/// unmapped, so its live count depends on whatever browseAll returns this session — and it keeps a representative
/// few placeholders rather than a fabricated exact count.
///
/// Split out of <see cref="BrowseDirectory"/> (engine-free by construction — System + Wavee.Core only) so it can be
/// source-included into Wavee.Tests without dragging in the engine-bound component: the skeleton's SHAPE is these
/// seeds' taxonomy grouping, and <c>BrowseTaxonomyTests</c> pins the per-band counts so an unrelated edit to
/// <see cref="BrowseTaxonomy"/>'s Map cannot silently reshape the loading directory (a category moving bands would
/// change how many skeleton rows each band shows, with nothing in a diff of <c>BrowseDirectory.cs</c> to say so).</summary>
internal static class BrowseDirectorySeeds
{
    internal static readonly IReadOnlyList<BrowseCategory> Categories =
    [
        // ── Top (4 — the complete set; matches BrowseTaxonomy.Map's Top entries) ────────────────────────────────
        new BrowseCategory("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", " ", null),                // Top: Music
        new BrowseCategory("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", " ", null),                // Top: Podcasts
        new BrowseCategory("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", " ", null),                // Top: Audiobooks
        new BrowseCategory("spotify:concerts", " ", null, IsClientFeature: true),            // Top: Live Events

        // ── For you (10 — the complete set) ─────────────────────────────────────────────────────────────────────
        new BrowseCategory("spotify:page:0JQ5DAtOnAEpjOgUKwXyxj", " ", null),                // For you: Discover
        new BrowseCategory("spotify:page:0JQ5DAqbMKFPw634sFwguI", " ", null),                // For you: EQUAL
        new BrowseCategory("spotify:page:0JQ5DAqbMKFImHYGo3eTSg", " ", null),                // For you: Fresh Finds
        new BrowseCategory("spotify:page:0JQ5DAqbMKFGnsSfvg90Wo", " ", null),                // For you: GLOW
        new BrowseCategory("spotify:page:0JQ5DAt0tbjZptfcdMSKl3", " ", null),                // For you: Made For You
        new BrowseCategory("spotify:page:0JQ5DAqbMKFz6FAsUtgAab", " ", null),                // For you: New Releases
        new BrowseCategory("spotify:page:0JQ5DAqbMKFOOxftoKZxod", " ", null),                // For you: RADAR
        new BrowseCategory("spotify:page:0JQ5DAqbMKFDBgllo2cUIN", " ", null),                // For you: Spotify Singles
        new BrowseCategory("spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm", " ", null),                // For you: Tastemakers
        new BrowseCategory("spotify:page:0JQ5DAqbMKFQIL0AXnG5AK", " ", null),                // For you: Trending

        // ── Genres (25 — the complete set) ──────────────────────────────────────────────────────────────────────
        new BrowseCategory("spotify:page:0JQ5DAqbMKFNQ0fGp4byGU", " ", null),                // Genres: Afro
        new BrowseCategory("spotify:page:0JQ5DAqbMKFFtlLYUHv8bT", " ", null),                // Genres: Alternative
        new BrowseCategory("spotify:page:0JQ5DAqbMKFLjmiZRss79w", " ", null),                // Genres: Ambient
        new BrowseCategory("spotify:page:0JQ5DAqbMKFQ1UFISXj59F", " ", null),                // Genres: Arab
        new BrowseCategory("spotify:page:0JQ5DAqbMKFQiK2EHwyjcU", " ", null),                // Genres: Blues
        new BrowseCategory("spotify:page:0JQ5DAqbMKFObNLOHydSW8", " ", null),                // Genres: Caribbean
        new BrowseCategory("spotify:page:0JQ5DAqbMKFPrEiAOxgac3", " ", null),                // Genres: Classical
        new BrowseCategory("spotify:page:0JQ5DAqbMKFKLfwjuJMoNC", " ", null),                // Genres: Country
        new BrowseCategory("spotify:page:0JQ5DAqbMKFHOzuVTgTizF", " ", null),                // Genres: Dance/Electronic
        new BrowseCategory("spotify:page:0JQ5DAqbMKFCLroFGPFVr5", " ", null),                // Genres: Dutch music
        new BrowseCategory("spotify:page:0JQ5DAqbMKFy78wprEpAjl", " ", null),                // Genres: Folk & Acoustic
        new BrowseCategory("spotify:page:0JQ5DAqbMKFFsW9N8maB6z", " ", null),                // Genres: Funk & Disco
        new BrowseCategory("spotify:page:0JQ5DAqbMKFQ00XGBls6ym", " ", null),                // Genres: Hip-Hop
        new BrowseCategory("spotify:page:0JQ5DAqbMKFCWjUTdzaG0e", " ", null),                // Genres: Indie
        new BrowseCategory("spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m", " ", null),                // Genres: Jazz
        new BrowseCategory("spotify:page:0JQ5DAqbMKFGvOw3O4nLAf", " ", null),                // Genres: K-pop
        new BrowseCategory("spotify:page:0JQ5DAqbMKFxXaXKP7zcDp", " ", null),                // Genres: Latin
        new BrowseCategory("spotify:page:0JQ5DAqbMKFDkd668ypn6O", " ", null),                // Genres: Metal
        new BrowseCategory("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", " ", null),                // Genres: Pop
        new BrowseCategory("spotify:page:0JQ5DAqbMKFAjfauKLOZiv", " ", null),                // Genres: Punk
        new BrowseCategory("spotify:page:0JQ5DAqbMKFEZPnFQSFB1T", " ", null),                // Genres: R&B
        new BrowseCategory("spotify:page:0JQ5DAqbMKFJKoGyUMo2hE", " ", null),                // Genres: Reggae
        new BrowseCategory("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", " ", null),                // Genres: Rock
        new BrowseCategory("spotify:page:0JQ5DAqbMKFIpEuaCnimBj", " ", null),                // Genres: Soul
        new BrowseCategory("spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O", " ", null),                // Genres: Songwriters

        // ── Mood & activity (14 — the complete set) ─────────────────────────────────────────────────────────────
        new BrowseCategory("spotify:page:0JQ5DAqbMKFx0uLQR2okcc", " ", null),                // Mood & activity: At Home
        new BrowseCategory("spotify:page:0JQ5DAqbMKFFzDl7qN9Apr", " ", null),                // Mood & activity: Chill
        new BrowseCategory("spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0", " ", null),                // Mood & activity: Cooking & Dining
        new BrowseCategory("spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx", " ", null),                // Mood & activity: Fitness
        new BrowseCategory("spotify:page:0JQ5DAqbMKFCbimwdOYlsl", " ", null),                // Mood & activity: Focus
        new BrowseCategory("spotify:page:0JQ5DAqbMKFIRybaNTYXXy", " ", null),                // Mood & activity: In the car
        new BrowseCategory("spotify:page:0JQ5DAqbMKFAUsdyVjCQuL", " ", null),                // Mood & activity: Love
        new BrowseCategory("spotify:page:0JQ5DAqbMKFzHmL4tf05da", " ", null),                // Mood & activity: Mood
        new BrowseCategory("spotify:page:0JQ5DAqbMKFI3pNLtYMD9S", " ", null),                // Mood & activity: Nature & Noise
        new BrowseCategory("spotify:page:0JQ5DAqbMKFA6SOHvT3gck", " ", null),                // Mood & activity: Party
        new BrowseCategory("spotify:page:0JQ5DAqbMKFCuoRTxhYWow", " ", null),                // Mood & activity: Sleep
        new BrowseCategory("spotify:page:0JQ5DAqbMKFAQy4HL4XU2D", " ", null),                // Mood & activity: Travel
        new BrowseCategory("spotify:page:0JQ5DAqbMKFLb2EqgLtpjC", " ", null),                // Mood & activity: Wellness
        new BrowseCategory("spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4", " ", null),                // Mood & activity: Workout Music

        // ── More (3 — a representative few; the live count is genuinely unknowable, see the class doc-comment) ───
        new BrowseCategory("spotify:page:skeleton-more-1", " ", null),                       // More (deliberately unmapped)
        new BrowseCategory("spotify:page:skeleton-more-2", " ", null),
        new BrowseCategory("spotify:page:skeleton-more-3", " ", null),
    ];
}
