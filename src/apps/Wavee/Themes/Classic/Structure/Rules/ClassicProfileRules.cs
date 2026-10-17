// ── Themes/Classic/Structure/Rules/ClassicProfileRules.cs — the numbers of Classic's profile page (themes plan 6.1, T10c2) ──────
//
// The literals that make the page look the way it does (WVTH003 keeps them out of the views): the magazine's dock reserve and the shelf's geometry.
//
// NOT here: the hero's sub-tiers, heights, avatars, stat budget and the shelves' fit per tier are the tier records (`Rules\Tiers.Profile.cs`, TC6); the
// collapse arithmetic is core (`ProfileHeroLayout`, `Entities\Profile.Rules.cs`).

namespace Wavee.Themes.Classic;

/// <summary>Classic's profile page numbers.</summary>
internal static class ClassicProfileRules
{
    /// <summary>Added to the player dock's reserve under the last section.</summary>
    public const float MagazineDockExtra = 40f;

    // The shelf's seed geometry at a ONE-line caption (the fit itself is the tier's: `ClassicProfileLayout.Shelf`).
    /// <summary>The gap between a shelf's header and its row of cards (the seed's).</summary>
    public const float ShelfHeaderGap = 12f;

    /// <summary>The shelf's chevron button: the seed header's minimum height.</summary>
    public const float ShelfChevron = 32f;

    /// <summary>The rank numeral's column beside a top-artist card.</summary>
    public const float RankColumn = 36f;
}
