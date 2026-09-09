using System;
using FluentGpu.Dsl;

namespace Wavee;

internal enum HomeHeroTier : byte { Narrow, Medium, Wide }
internal enum HomeHeroDensity : byte { Full, Compact }

/// <summary>One exact geometry contract shared by the Daylist renderer and Home's virtual-row estimator.</summary>
internal readonly record struct HomeHeroMetrics(
    HomeHeroTier Tier, HomeHeroDensity Density,
    float Height, float CopyPaddingX, float CopyPaddingY, float ArtworkSize,
    int TitleLines, bool ShowTags, bool ShowPulse, float Width)
{
    public bool Stacked => Tier == HomeHeroTier.Narrow;
}

/// <summary>Viewport-aware, density-tiered hero geometry. Width alone used to fix the height (a 336-DIP card on a
/// 520-DIP page viewport at 900x600 — ~65% of what was visible); this file adds a second axis, the PAGE viewport
/// height (<see cref="Wavee.Features.Shell.ShellViewport.PageHeightFor"/>), which can only ever shrink the hero: it
/// picks a Compact density (a 1-line title, no tag row, a tighter meta rung) before it ever clamps the height
/// outright, and the pulse row (the daylist flip-countdown) is reserved only when the card actually carries one.</summary>
internal static class HomeHeroLayout
{
    public const float MediumWidth = 700f;
    public const float WideWidth = 980f;
    public const float TierHysteresis = 24f;                       // same band as ArtistHeroLayout / DetailLayoutBreakpoints

    /// <summary>Below this PAGE viewport height (window height minus the shell's title bar + player dock) the hero
    /// takes its Compact density regardless of width; Narrow is always Compact.</summary>
    public const float CompactViewportHeight = 720f;
    /// <summary>The hero never takes more than this fraction of the page viewport; the copy budget shrinks first
    /// (density), then the height clamps and the artwork (a square whose edge is the height) follows.</summary>
    public const float MaxViewportFraction = 0.42f;
    public const float MinHeight = 168f;                           // Compact, no tags, 1-line title, no pulse: 2*24 + 24 + 36 + 16 + 28 + 32 = 184 -> floor a rung under it

    // Flatten the old SectionBand inset (16) and inner hero padding (32x28) without moving the copy by one DIP.
    public const float CopyPaddingX = Spacing.L + Spacing.XXXL;    // 48
    public const float CompactCopyPaddingX = Spacing.XXL;          // 24
    public const float CopyPaddingY = Spacing.L + Spacing.XXL + Spacing.S;   // 48 (#106 — was 44)
    public const float CompactCopyPaddingY = Spacing.XXL;          // 24
    public const float ArtworkFade = Spacing.XXXL * 3f;            // 96

    // Authored maximum blocks. Every non-token value is a LINE HEIGHT off the engine ramp — never free-hand spacing —
    // and every gap below it is a Spacing rung, so this file and HomeCards.HeroBand state the same geometry twice in
    // the same vocabulary rather than one of them drifting into hand-picked numbers.
    const float EyebrowBlock = 16f + Spacing.S;                    // Caption 12/16 + an 8 margin
    const float WideTitleLine = 60f;                               // WaveeType.ArtistTitle 48/60
    const float MediumTitleLine = 40f;                             // WaveeType.ArtistCompactTitle 32/40
    const float NarrowTitleLine = 36f;                             // WaveeType.PageHero 28/36
    const float TitleMargin = Spacing.L;                           // #106 — was M
    // A tag is Caption 12/16 inside a 2-DIP vertical padding = 20, plus the row's 12 margin.
    const float TagsBlock = 20f + Spacing.M;
    // Body 14/20 plus a 16 margin (Full). Compact keeps one line and a tighter 8-DIP margin.
    const float MetaBlock = 20f + Spacing.XL;                      // #106 — was L
    const float CompactMetaBlock = 20f + Spacing.S;
    // The 28-DIP flip-countdown digit row (FlipCountdown.HeroRowHeight, restated — this file is engine-free and
    // test-included, so it cannot reference the component) plus a 12 margin — reserved ONLY when the card is a
    // daylist; non-daylist heroes collapse this slot to an empty BoxEl and neither renderer nor estimator reserve it.
    const float PulseBlock = 28f + Spacing.L;                      // 44 (#106 — was 40)
    const float ActionsBlock = Spacing.XXXL;                       // 32, the hero button row

    /// <summary>Narrow immediately; widen back only once past threshold + hysteresis (the ArtistHeroLayout shape).</summary>
    public static HomeHeroTier TierFor(float width, HomeHeroTier previous)
    {
        if (previous == HomeHeroTier.Wide && width >= WideWidth - TierHysteresis) return previous;
        if (previous == HomeHeroTier.Medium && width >= MediumWidth - TierHysteresis && width < WideWidth + TierHysteresis) return previous;
        if (width >= WideWidth + (previous < HomeHeroTier.Wide ? TierHysteresis : 0f)) return HomeHeroTier.Wide;
        if (width >= MediumWidth + (previous < HomeHeroTier.Medium ? TierHysteresis : 0f)) return HomeHeroTier.Medium;
        return HomeHeroTier.Narrow;
    }

    public static HomeHeroDensity DensityFor(HomeHeroTier tier, float pageViewportHeight)
        => tier == HomeHeroTier.Narrow || (pageViewportHeight > 0f && pageViewportHeight < CompactViewportHeight)
            ? HomeHeroDensity.Compact : HomeHeroDensity.Full;

    public static HomeHeroMetrics For(float width, float pageViewportHeight, bool hasPulse, HomeHeroTier previous)
    {
        var tier = TierFor(width, previous);
        var density = DensityFor(tier, pageViewportHeight);
        bool compact = density == HomeHeroDensity.Compact;
        int titleLines = compact ? 1 : 2;
        bool showTags = !compact;
        float content = ContentHeight(tier, density, titleLines, showTags, hasPulse);
        float cap = pageViewportHeight > 0f ? MathF.Max(MinHeight, pageViewportHeight * MaxViewportFraction) : content;
        float height = MathF.Min(content, cap);
        float padX = compact ? CompactCopyPaddingX : CopyPaddingX;
        float padY = compact ? CompactCopyPaddingY : CopyPaddingY;
        return new HomeHeroMetrics(tier, density, height, padX, padY, height, titleLines, showTags, hasPulse, width);
    }

    public static float HeightFor(float width, float pageViewportHeight, bool hasPulse, HomeHeroTier previous)
        => For(width, pageViewportHeight, hasPulse, previous).Height;

    public static float ContentHeight(HomeHeroTier tier, HomeHeroDensity density, int titleLines, bool showTags, bool hasPulse)
    {
        float line = tier switch { HomeHeroTier.Wide => WideTitleLine, HomeHeroTier.Medium => MediumTitleLine, _ => NarrowTitleLine };
        bool compact = density == HomeHeroDensity.Compact;
        return 2f * (compact ? CompactCopyPaddingY : CopyPaddingY)
             + EyebrowBlock + titleLines * line + TitleMargin
             + (showTags ? TagsBlock : 0f)
             + (compact ? CompactMetaBlock : MetaBlock)
             + (hasPulse ? PulseBlock : 0f)
             + ActionsBlock;
    }
}
