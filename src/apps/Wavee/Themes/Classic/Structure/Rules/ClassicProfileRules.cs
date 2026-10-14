// ── Themes/Classic/Structure/Rules/ClassicProfileRules.cs — the numbers of Classic's profile page (themes plan 6.1, T10c2) ──────
//
// The literals that make the page look the way it does (WVTH003 keeps them out of the views): the magazine's dock reserve and the shelf's geometry.
//
// NOT here: the hero's tiers, heights, gutters, stat budget and collapse arithmetic. `ProfileHeroLayout` (`Entities\Profile.Rules.cs`) stays in core over the
// artist hero's tiers (`ArtistHeroLayout`): the artist page's host reads the same tiers.

namespace Wavee.Themes.Classic;

/// <summary>Classic's profile page numbers.</summary>
internal static class ClassicProfileRules
{
    /// <summary>Added to the player dock's reserve under the last section.</summary>
    public const float MagazineDockExtra = 40f;

    // The artist shelf's geometry (`ClassicArtistSections.ShelfOf` = PagedShelf's defaults) at a ONE-line caption.
    /// <summary>The narrowest a shelf card may get.</summary>
    public const float ShelfMinCard = 150f;

    /// <summary>The widest a shelf card may get.</summary>
    public const float ShelfMaxCard = 200f;

    /// <summary>The gap between shelf cards.</summary>
    public const float ShelfGap = 12f;

    /// <summary>The gap between a shelf's header and its row of cards (the seed's).</summary>
    public const float ShelfHeaderGap = 12f;

    /// <summary>The shelf's chevron button: the seed header's minimum height.</summary>
    public const float ShelfChevron = 32f;

    /// <summary>The rank numeral's column beside a top-artist card.</summary>
    public const float RankColumn = 36f;
}
