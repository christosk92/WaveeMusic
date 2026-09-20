using FluentGpu.Dsl;

namespace Wavee;

internal enum HomeHeroTier : byte { Narrow, Medium, Wide }

/// <summary>One exact geometry contract shared by the Daylist renderer and Home's virtual-row estimator.</summary>
internal readonly record struct HomeHeroMetrics(
    HomeHeroTier Tier,
    float Height,
    float CopyPaddingX,
    float CopyPaddingY,
    float ArtworkSize)
{
    public bool Stacked => Tier == HomeHeroTier.Narrow;
}

internal static class HomeHeroLayout
{
    public const float MediumWidth = 700f;
    public const float WideWidth = 980f;

    // Flatten the old SectionBand inset (16) and inner hero padding (32x28) without moving the copy by one DIP.
    public const float CopyPaddingX = Spacing.L + Spacing.XXXL;                 // 48
    public const float CopyPaddingY = Spacing.L + Spacing.XXL + Spacing.XS;    // 44
    public const float ArtworkFade = Spacing.XXXL * 3f;                        // 96

    // Authored maximum blocks. Every non-token value is a LINE HEIGHT off the engine ramp — never free-hand spacing —
    // and every gap below it is a Spacing rung, so this file and HomeCards.HeroBand state the same geometry twice in
    // the same vocabulary rather than one of them drifting into hand-picked numbers.
    const float EyebrowBlock = 16f + Spacing.S;                 // Caption 12/16 + an 8 margin
    const float WideTitleBlock = 2f * 60f;                      // WaveeType.ArtistTitle 48/60, two lines
    const float MediumTitleBlock = 2f * 40f;                    // WaveeType.ArtistCompactTitle 32/40, two lines
    const float NarrowTitleBlock = 2f * 36f;                    // WaveeType.PageHero 28/36, two lines
    const float TitleMargin = Spacing.M;
    // A tag is Caption 12/16 inside a 2-DIP vertical padding = 20, plus the row's 12 margin. Unchanged at 32: the tag
    // grew 18 → 20 as its padding went on-grid exactly as the row margin came down 14 → 12.
    const float TagsBlock = 20f + Spacing.M;
    // Body 14/20 (was a bespoke 13/19) plus a 16 margin (was 18). One DIP shorter than before, per tier.
    const float MetaBlock = 20f + Spacing.L;
    // The 28-DIP flip-countdown digit row (FlipCountdown.HeroRowHeight, restated — this file is engine-free and
    // test-included, so it cannot reference the component) plus a 12 margin — reserved for every hero so the
    // virtual-row estimator and HeroBand state the same geometry; non-daylist heroes collapse the slot to an empty BoxEl.
    const float PulseBlock = 28f + Spacing.M;
    const float ActionsBlock = Spacing.XXXL;                    // the 32-DIP hero button row

    public static HomeHeroMetrics For(float width)
    {
        var tier = width >= WideWidth ? HomeHeroTier.Wide
            : width >= MediumWidth ? HomeHeroTier.Medium
            : HomeHeroTier.Narrow;
        float height = ContentHeight(tier);
        // A square whose edge equals the surface height preserves the complete playlist cover. It is integrated by the
        // veil and edge fade, never by stretching or cropping the source into a banner it was not authored to be.
        return new HomeHeroMetrics(tier, height, CopyPaddingX, CopyPaddingY, height);
    }

    public static float HeightFor(float width) => For(width).Height;

    public static float ContentHeight(HomeHeroTier tier)
    {
        float title = tier switch
        {
            HomeHeroTier.Wide => WideTitleBlock,
            HomeHeroTier.Medium => MediumTitleBlock,
            _ => NarrowTitleBlock,
        };
        return 2f * CopyPaddingY + EyebrowBlock + title + TitleMargin + TagsBlock + MetaBlock + PulseBlock + ActionsBlock;
    }
}
