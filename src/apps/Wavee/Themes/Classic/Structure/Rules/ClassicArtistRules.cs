// ── Themes/Classic/Structure/Rules/ClassicArtistRules.cs — the numbers of Classic's artist page (themes plan 6.1, T10b1) ──────────
//
// The literals that make the page look the way it does (WVTH003 keeps them out of the views): the pick card's pieces, the magazine's dock reserve, the
// biography band's measure and tiles, the city bars, the upcoming card's cover, the tour badge and the latest-release cover.
//
// NOT here: the hero's tiers, heights, gutters, photo bands, copy maxima and collapse arithmetic. `ArtistHeroLayout` stays in core (`Entities\Artist.UI.cs`
// §1): the profile page reads the same tiers, and the page host decodes the photo at the size it names. The plan's `Rules\ArtistHero.cs` is that file until the
// profile page moves (T10c2).

namespace Wavee.Themes.Classic;

/// <summary>Classic's artist page numbers.</summary>
public static class ClassicArtistRules
{
    // ── the hero copy over the photograph (tuned Classic D18) ──
    /// <summary>The share of the hero's width the copy column ends at on the photo tiers: the photograph keeps the rest.</summary>
    public const float HeroCopyShare = 0.56f;

    /// <summary>The copy column's floor on the photo tiers, so a narrow Medium hero still sets a readable measure.</summary>
    public const float HeroCopyFloor = 360f;

    /// <summary>The copy column's width. Stacked tiers (no type on the photo) fill the band up to the tier's maximum. The photo tiers
    /// take <c>min(CopyMax, max(360, 0.56 w - gutter))</c>, so the copy ends near 56 % of the hero instead of running to 95 % of it
    /// over the bright half of the photo.</summary>
    public static float HeroCopyWidth(float width, in ArtistHeroMetrics m)
    {
        float band = MathF.Max(1f, width - 2f * m.Gutter);
        if (m.Stacked) return MathF.Min(m.CopyMaxWidth, band);
        return MathF.Min(MathF.Min(m.CopyMaxWidth, band), MathF.Max(HeroCopyFloor, HeroCopyShare * width - m.Gutter));
    }

    /// <summary>Where the veil's hold ends, as a fraction of the hero's width: the copy column's right edge, so the veil keeps the copy on
    /// its plate and fades over the photograph after it (<c>Controls.ArtistHeroVeil</c>). NaN on stacked tiers, which have no side veil.</summary>
    public static float HeroVeilHold(float width, in ArtistHeroMetrics m)
        => m.Stacked || width <= 1f ? float.NaN : (m.Gutter + HeroCopyWidth(width, in m)) / width;

    // ── the pick card ──
    /// <summary>The rail arm's photograph band height; the column supplies the width.</summary>
    public const float PickPhotoHeight = 150f;

    /// <summary>The band arm: photography as a right column, stretched to the panel.</summary>
    public const float PickPhotoColumnW = 300f;

    /// <summary>The photo's width / height (its decode height derives from it).</summary>
    public const float PickPhotoAspect = 1.6f;

    /// <summary>The photo's decode width.</summary>
    public const int PickPhotoDecodeW = 640;

    /// <summary>The artist avatar in the pick's header.</summary>
    public const float PickAvatar = 32f;

    /// <summary>The record's cover in the pick's footer.</summary>
    public const float PickCover = 44f;

    // ── the magazine ──
    /// <summary>Added to the player dock's reserve under the last section.</summary>
    public const float MagazineDockExtra = 40f;

    /// <summary>The skeleton's grid placeholders.</summary>
    public const int ShimmerCells = 5;

    // ── the biography band ──
    /// <summary>The biography text's size.</summary>
    public const float BioFontSize = 14f;

    /// <summary>The share of the band's width the biography text takes at the wide mode.</summary>
    public const float BioWideShare = 0.62f;

    /// <summary>The card's horizontal padding and border the text measure gives up.</summary>
    public const float BioTextInset = 60f;

    /// <summary>A profile-facts tile's flex basis.</summary>
    public const float FactBasis = 140f;

    // ── the city bars ──
    /// <summary>One city's row height.</summary>
    public const float CityRow = 34f;

    /// <summary>The gap between a city's name line and its bar.</summary>
    public const float CityGap = 4f;

    /// <summary>The accent bar's height.</summary>
    public const float CityBar = 4f;

    /// <summary>The city name's size.</summary>
    public const float CityNameSize = 14f;

    // ── the banners ──
    /// <summary>The upcoming card's cover (the band arm) and its decode width.</summary>
    public const float UpcomingCover = 88f;
    public const int UpcomingCoverDecode = 192;

    /// <summary>The latest-release banner's cover.</summary>
    public const float LatestCover = 72f;

    /// <summary>The tour banner's accent circle, its glyph, its headline, the eyebrow-to-headline gap and its chevron.</summary>
    public const float TourBadge = 44f;
    public const float TourIcon = 18f;
    public const float TourHeadline = 16f;
    public const float TourGap = 2f;
    public const float TourChevron = 16f;
}
