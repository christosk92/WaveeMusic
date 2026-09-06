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
/// the copy placement and veil direction. Tier selection retains the detail layout's 24-DIP recovery band.
///
/// <para>A second axis on top of width: the PAGE viewport height (<see cref="Wavee.Features.Shell.ShellViewport"/>).
/// On a short window the fixed per-tier height used to be paid regardless — on horizontal tiers that is dead air
/// around a centred copy block (Justify=Center already absorbs slack there, so the height itself can shrink); on
/// stacked tiers the photo band is what gives, down to <see cref="MinPhotoHeight"/>, because the identity column
/// below it has a real anatomy (name, bio, meta, actions) that cannot compress further without dropping content.</para></summary>
public static class ArtistHeroLayout
{
    // Lowered from 480/760/1040: the old thresholds dropped into the stacked (Compact/Narrow) presentation at
    // ordinary desktop widths — e.g. opening the Lyrics panel alongside a normal window was enough to trigger the
    // dramatic photo-on-top/identity-below layout. These trigger only once the content column is genuinely narrow.
    public const float CompactWidth = 360f;
    public const float MediumWidth = 600f;
    public const float WideWidth = 880f;
    public const float TierHysteresis = 24f;

    // #106 — the D81 cut (440 → 360, 384 → 320) left a two-line bio with no air between the name, the stats row and
    // the actions. +32 / +24 puts a breath back without returning to the pre-cut band.
    public const float WideHeight = 392f;              // was 360 (D81), 440 before that
    public const float MediumHeight = 344f;            // was 320 (D81), 384 before that

    // Stacked (Compact/Narrow) photo band: the photograph is a FIELD at the top of the hero and the identity column
    // sits BELOW it on the page surface — copy never floats over the picture on these tiers, so there is no overlay
    // veil to size.
    public const float CompactPhotoHeight = 160f;      // was 200
    public const float NarrowPhotoHeight = 136f;       // was 176

    // The stacked identity band has a declared WORST-CASE anatomy and its fixed hero must actually reserve it. The
    // former 252/300 were inflated for a THREE-line stacked title and a vertically-stacked meta row; the name is
    // capped at 2 lines on every tier and Compact's meta is one horizontal row, so the cut below still reserves:
    // verified caption (16) + 2-line compact title (2 x 40) + bio (1 line, 20) + meta (one 20-DIP row at Compact,
    // stacked rows at Narrow) + the inter-block gaps + 12/20 vertical padding + actions.
    public const float CompactExpandedIdentityHeight = 220f;   // was 252 — bio 2->1 line, meta one row, actions 36
    public const float NarrowExpandedIdentityHeight = 252f;    // was 300 — bio 1 line, meta stacked, two action rows
    public const float CompactHeight = CompactPhotoHeight + CompactExpandedIdentityHeight;   // 380
    public const float NarrowHeight = NarrowPhotoHeight + NarrowExpandedIdentityHeight;       // 388

    /// <summary>The hero never exceeds this fraction of the page viewport; on horizontal tiers the air around the
    /// centred copy gives first, on stacked tiers the PHOTO band shrinks to <see cref="MinPhotoHeight"/>.</summary>
    public const float MaxViewportFraction = 0.45f;
    public const float MinPhotoHeight = 96f;

    // The horizontal copy block's own worst case (verified 16 + 2-line name + 1-line bio + meta 20 + actions 36 +
    // 4 gaps of 8 + 2x24 padding) — the floor a horizontal tier's clamp cannot go under, spelled out as constants so
    // ArtistHeroLayoutTests can assert MinHeight >= CopyBudgetFor(tier) for every input.
    const float VerifiedCaption = 16f, NameTwoLine = 80f, BioOneLine = 20f, MetaRow = 20f, ActionsRow = 36f;
    const float CopyGaps = 4f * Spacing.M;              // #106 — the horizontal copy block's gap is 12, was 8
    const float CopyPadding = 2f * 24f;
    public const float WideCopyBudget = CopyPadding + VerifiedCaption + NameTwoLine + BioOneLine + MetaRow + ActionsRow + CopyGaps;
    public const float MediumCopyBudget = WideCopyBudget;

    public static float CopyBudgetFor(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Wide => WideCopyBudget,
        _ => MediumCopyBudget,
    };

    public static float IdentityHeightFor(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Narrow => NarrowExpandedIdentityHeight,
        _ => CompactExpandedIdentityHeight,
    };

    /// <summary>The photograph's own extent inside the hero: the full hero on horizontal tiers, the top slice on
    /// stacked tiers. Both the banner's media box and <c>HeroArt</c> derive from THIS, so they cannot disagree.</summary>
    public static float PhotoHeightFor(in ArtistHeroMetrics m) => !m.Stacked ? m.MinHeight
        : MathF.Max(MinPhotoHeight, m.MinHeight - IdentityHeightFor(m.Tier));

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

    static ArtistHeroMetrics Base(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Wide => new(tier, WideHeight, Spacing.PageWide, ArtistHeroVeilAxis.Horizontal, WideCopyMaxWidth),
        ArtistHeroTier.Medium => new(tier, MediumHeight, Spacing.XXXL, ArtistHeroVeilAxis.Horizontal, MediumCopyMaxWidth),
        ArtistHeroTier.Compact => new(tier, CompactHeight, Spacing.L, ArtistHeroVeilAxis.Vertical, CompactCopyMaxWidth),
        _ => new(tier, NarrowHeight, Spacing.PageNarrow, ArtistHeroVeilAxis.Vertical, NarrowCopyMaxWidth),
    };

    /// <summary>Width-only geometry, unclamped — kept for callers with no page viewport reading (none in the app
    /// today; retained so a future pre-measure caller has a stateless entry point identical to before this file
    /// gained the viewport axis).</summary>
    public static ArtistHeroMetrics For(float width, ArtistHeroTier previous) => For(width, 0f, previous);

    public static ArtistHeroMetrics For(float width, float pageViewportHeight, ArtistHeroTier previous)
    {
        var tier = TierFor(width, previous);
        var m = Base(tier);
        if (pageViewportHeight <= 0f) return m;
        float cap = pageViewportHeight * MaxViewportFraction;
        if (m.MinHeight <= cap) return m;
        // Horizontal: clamp the whole hero (the centred copy absorbs the removed slack). Stacked: clamp the photo
        // band down to MinPhotoHeight and keep the identity band's full anatomy — never below either floor.
        float floor = m.Stacked ? IdentityHeightFor(tier) + MinPhotoHeight : CopyBudgetFor(tier);
        return m with { MinHeight = MathF.Max(floor, cap) };
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

    public static float HeroHeightFor(float width) => HeroHeightFor(width, 0f);
    public static float HeroHeightFor(float width, float pageViewportHeight)
        => For(width, pageViewportHeight, ArtistHeroTier.Wide).MinHeight;

    public static float BlendBackdropHeightFor(float width) => BlendBackdropHeightFor(width, 0f);
    public static float BlendBackdropHeightFor(float width, float pageViewportHeight)
        => HeroHeightFor(width, pageViewportHeight) + ContentBlendTail;

    public static float BlendBoundaryFor(float width) => BlendBoundaryFor(width, 0f);
    public static float BlendBoundaryFor(float width, float pageViewportHeight)
    {
        float height = HeroHeightFor(width, pageViewportHeight);
        return height / (height + ContentBlendTail);
    }
}
