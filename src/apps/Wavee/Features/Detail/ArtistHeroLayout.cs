using System;
using FluentGpu.Dsl;

namespace Wavee.Features.Detail;

public enum ArtistHeroTier : byte { Narrow, Compact, Medium, Wide }
public enum ArtistHeroVeilAxis : byte { Horizontal, Vertical }

public readonly record struct ArtistHeroMetrics(
    ArtistHeroTier Tier,
    float MinHeight,
    float Gutter,
    ArtistHeroVeilAxis VeilAxis,
    float CopyMaxWidth)
{
    public bool Stacked => VeilAxis == ArtistHeroVeilAxis.Vertical;
}

/// <summary>Responsive geometry for the full-bleed artist hero. The photo always fills the hero; pressure changes only
/// the copy placement and veil direction. Tier selection retains the detail layout's 24-DIP recovery band.</summary>
public static class ArtistHeroLayout
{
    public const float CompactWidth = 480f;
    public const float MediumWidth = 760f;
    public const float WideWidth = 1040f;
    public const float TierHysteresis = 24f;

    public const float WideHeight = 440f;
    public const float MediumHeight = 384f;

    // Stacked (Compact/Narrow) photo band: the photograph is a FIELD at the top of the hero and the identity column
    // sits BELOW it on the page surface — copy never floats over the picture on these tiers, so there is no overlay
    // veil to size.
    public const float CompactPhotoHeight = 300f;
    public const float NarrowPhotoHeight = 240f;

    // The stacked identity band has a declared WORST-CASE anatomy and its fixed hero must actually reserve it:
    // verified caption (16) + compact title (3 × 40) + bio (2 × 20) + three vertical meta rows (3 × 20 + 2 × 4)
    // + the four 8-DIP inter-block gaps + 12/20 vertical padding + actions. Compact actions are one 36-DIP row;
    // Narrow owns two such rows plus their 8-DIP gap. The former 240/276 identity remainders only fit a short artist
    // and clipped Maroon 5's Play pill at the hero boundary once rank + listeners + followers were present.
    public const float CompactExpandedIdentityHeight = 344f;
    public const float NarrowExpandedIdentityHeight = 388f;
    public const float CompactHeight = CompactPhotoHeight + CompactExpandedIdentityHeight;
    public const float NarrowHeight = NarrowPhotoHeight + NarrowExpandedIdentityHeight;

    /// <summary>The photograph's own extent inside the hero: the full hero on horizontal tiers, the top slice on
    /// stacked tiers. Both the banner's media box and <c>HeroArt</c> derive from THIS, so they cannot disagree.</summary>
    public static float PhotoHeightFor(in ArtistHeroMetrics m) => !m.Stacked ? m.MinHeight
        : m.Tier == ArtistHeroTier.Compact ? CompactPhotoHeight
        : NarrowPhotoHeight;

    public const float WideCopyMaxWidth = 1120f;
    public const float MediumCopyMaxWidth = 760f;
    public const float CompactCopyMaxWidth = 640f;
    public const float NarrowCopyMaxWidth = 520f;
    public const float PhotoParallaxFraction = 0.15f;
    public const float ContentBlendTail = Spacing.XXXL * 3f;
    public const float CompactIdentityHeight = DetailVerticalLayout.CompactIdentityHeight;

    public static ArtistHeroTier TierFor(float width, ArtistHeroTier previous)
    {
        if (previous == ArtistHeroTier.Wide && width >= WideWidth - TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Medium && width >= MediumWidth - TierHysteresis && width < WideWidth + TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Compact && width >= CompactWidth - TierHysteresis && width < MediumWidth + TierHysteresis) return previous;

        if (width >= WideWidth + ((byte)previous < (byte)ArtistHeroTier.Wide ? TierHysteresis : 0f)) return ArtistHeroTier.Wide;
        if (width >= MediumWidth + ((byte)previous < (byte)ArtistHeroTier.Medium ? TierHysteresis : 0f)) return ArtistHeroTier.Medium;
        if (width >= CompactWidth + ((byte)previous < (byte)ArtistHeroTier.Compact ? TierHysteresis : 0f)) return ArtistHeroTier.Compact;
        return ArtistHeroTier.Narrow;
    }

    public static ArtistHeroMetrics For(float width, ArtistHeroTier previous)
    {
        var tier = TierFor(width, previous);
        return tier switch
        {
            ArtistHeroTier.Wide => new(tier, WideHeight, Spacing.PageWide, ArtistHeroVeilAxis.Horizontal, WideCopyMaxWidth),
            ArtistHeroTier.Medium => new(tier, MediumHeight, Spacing.XXXL, ArtistHeroVeilAxis.Horizontal, MediumCopyMaxWidth),
            ArtistHeroTier.Compact => new(tier, CompactHeight, Spacing.L, ArtistHeroVeilAxis.Vertical, CompactCopyMaxWidth),
            _ => new(tier, NarrowHeight, Spacing.PageNarrow, ArtistHeroVeilAxis.Vertical, NarrowCopyMaxWidth),
        };
    }

    public static float PageGutterFor(float width) => width >= WideWidth ? Spacing.PageWide
        : width >= MediumWidth ? Spacing.XXXL
        : width >= CompactWidth ? Spacing.L
        : Spacing.PageNarrow;

    public static float PhotoFadeBandFor(float height) => Math.Clamp(height * 0.28f, 120f, 180f);
    public static float CollapseDistance(float height) => MathF.Max(1f, height - CompactIdentityHeight);
    public static float ExpandedFadeStart(float collapseDistance) => DetailVerticalLayout.ExpandedFadeStart(collapseDistance);
    public static float CompactRevealStart(float collapseDistance) => DetailVerticalLayout.CompactRevealStart(collapseDistance);

    // NO compact-bar policy any more. The old tinted bar carried an avatar, the name and two CAPSULES, so Follow had
    // to be dropped under width pressure to keep the row from clipping. The text-chrome context band that replaced it
    // has no capsules: its actions are words, they are the cheapest things in the row, and they never drop — what
    // yields under pressure is the PIVOT, from the right, which ContextBandLayout owns.

    public static float HeroHeightFor(float width) => width >= WideWidth ? WideHeight
        : width >= MediumWidth ? MediumHeight
        : width >= CompactWidth ? CompactHeight
        : NarrowHeight;
    public static float BlendBackdropHeightFor(float width) => HeroHeightFor(width) + ContentBlendTail;
    public static float BlendBoundaryFor(float width)
    {
        float height = HeroHeightFor(width);
        return height / (height + ContentBlendTail);
    }
}
