using Xunit;

namespace Wavee.Tests;

public class HomeHeroLayoutTests
{
    // ── tier + density geometry, no viewport cap (pageViewportHeight <= 0) ──────────────────────────────────────

    [Theory]
    // (tier, hasPulse) -> content height, before any viewport cap.
    // #106: 2*48 padding + 24 eyebrow + title lines + 16 title margin + 32 tags + 40 meta (+44 pulse) + 32 actions.
    [InlineData((byte)HomeHeroTier.Wide, true, 404f)]
    [InlineData((byte)HomeHeroTier.Wide, false, 360f)]
    [InlineData((byte)HomeHeroTier.Medium, true, 364f)]
    [InlineData((byte)HomeHeroTier.Medium, false, 320f)]
    public void FullDensity_HeightIsExact(byte tierByte, bool hasPulse, float expected)
    {
        var tier = (HomeHeroTier)tierByte;
        var m = HomeHeroLayout.For(tier == HomeHeroTier.Wide ? 980f : 700f, pageViewportHeight: 0f, hasPulse, tier);
        Assert.Equal(tier, m.Tier);
        Assert.Equal(HomeHeroDensity.Full, m.Density);
        Assert.Equal(2, m.TitleLines);
        Assert.True(m.ShowTags);
        Assert.Equal(expected, m.Height);
        Assert.Equal(expected, m.ArtworkSize);
        Assert.Equal(HomeHeroLayout.CopyPaddingX, m.CopyPaddingX);
        Assert.Equal(HomeHeroLayout.CopyPaddingY, m.CopyPaddingY);
    }

    [Theory]
    // Narrow is ALWAYS Compact, regardless of viewport.
    // Compact: 2*24 + 24 + 36 + 16 + 28 (+44 pulse) + 32.
    [InlineData(true, 228f)]
    [InlineData(false, 184f)]
    public void Narrow_IsAlwaysCompact(bool hasPulse, float expected)
    {
        var m = HomeHeroLayout.For(600f, pageViewportHeight: 0f, hasPulse, HomeHeroTier.Narrow);
        Assert.Equal(HomeHeroTier.Narrow, m.Tier);
        Assert.Equal(HomeHeroDensity.Compact, m.Density);
        Assert.True(m.Stacked);
        Assert.Equal(1, m.TitleLines);
        Assert.False(m.ShowTags);
        Assert.Equal(expected, m.Height);
        Assert.Equal(HomeHeroLayout.CompactCopyPaddingX, m.CopyPaddingX);
        Assert.Equal(HomeHeroLayout.CompactCopyPaddingY, m.CopyPaddingY);
    }

    [Theory]
    // A short PAGE viewport forces Compact even at a wide/medium tier.
    // Compact at Medium: 2*24 + 24 + 40 + 16 + 28 (+44 pulse) + 32.
    [InlineData(true, 232f)]
    [InlineData(false, 188f)]
    public void ShortViewport_ForcesCompactAtMediumTier(bool hasPulse, float expected)
    {
        // pageViewportHeight below CompactViewportHeight (720) forces Compact, but 600 is generous enough that the
        // 0.42 viewport CAP (252) does not additionally clamp the Compact content height itself (184/224).
        var m = HomeHeroLayout.For(700f, pageViewportHeight: 600f, hasPulse, HomeHeroTier.Medium);
        Assert.Equal(HomeHeroDensity.Compact, m.Density);
        Assert.Equal(expected, m.Height);
    }

    [Fact]
    public void TallViewport_KeepsFullDensity()
    {
        // Large enough that 0.42*pageViewportHeight comfortably exceeds the Full content height (404) too — this
        // test is about DENSITY, not the cap (that is ViewportCap_NeverAppliesWhenContentIsAlreadySmaller's job).
        var m = HomeHeroLayout.For(980f, pageViewportHeight: 2000f, hasPulse: true, HomeHeroTier.Wide);
        Assert.Equal(HomeHeroDensity.Full, m.Density);
        Assert.Equal(404f, m.Height);   // 2*48 + 24 + 2*60 + 16 + 32 + 40 + 44 + 32
    }

    // ── DensityFor ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DensityFor_NarrowIsAlwaysCompact()
        => Assert.Equal(HomeHeroDensity.Compact, HomeHeroLayout.DensityFor(HomeHeroTier.Narrow, pageViewportHeight: 5000f));

    [Fact]
    public void DensityFor_BelowCompactViewportHeight_IsCompact()
        => Assert.Equal(HomeHeroDensity.Compact,
            HomeHeroLayout.DensityFor(HomeHeroTier.Wide, HomeHeroLayout.CompactViewportHeight - 1f));

    [Fact]
    public void DensityFor_AtOrAboveCompactViewportHeight_IsFull()
        => Assert.Equal(HomeHeroDensity.Full,
            HomeHeroLayout.DensityFor(HomeHeroTier.Wide, HomeHeroLayout.CompactViewportHeight));

    [Fact]
    public void DensityFor_ZeroOrAbsentViewport_IsFull()
        => Assert.Equal(HomeHeroDensity.Full, HomeHeroLayout.DensityFor(HomeHeroTier.Wide, pageViewportHeight: 0f));

    // ── the viewport cap ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ViewportCap_ShrinksTheDaylistHeroAt900x600()
    {
        // The reported case: a 900x600 window -> a page viewport of ~520 (window height minus the shell's chrome).
        // cap = max(MinHeight, 520 * 0.42) = max(168, 218.4) = 218.4 -> the daylist hero (Narrow, Compact, +pulse,
        // content 220) clamps down to the cap instead of the full 220.
        var m = HomeHeroLayout.For(620f, pageViewportHeight: 520f, hasPulse: true, HomeHeroTier.Narrow);
        Assert.Equal(520f * HomeHeroLayout.MaxViewportFraction, m.Height, 2);
        Assert.True(m.Height < 220f);
    }

    [Fact]
    public void ViewportCap_NeverBelowMinHeight()
    {
        var m = HomeHeroLayout.For(620f, pageViewportHeight: 50f, hasPulse: false, HomeHeroTier.Narrow);
        Assert.Equal(HomeHeroLayout.MinHeight, m.Height);
    }

    [Fact]
    public void ViewportCap_NeverAppliesWhenContentIsAlreadySmaller()
    {
        // A generous viewport whose cap exceeds the content height leaves the content height untouched.
        var m = HomeHeroLayout.For(980f, pageViewportHeight: 4000f, hasPulse: false, HomeHeroTier.Wide);
        Assert.Equal(360f, m.Height);   // the same minus the 44-DIP pulse block
    }

    // ── TierFor hysteresis (mirrors ArtistHeroLayoutTests) ──────────────────────────────────────────────────────────

    // HomeHeroTier is internal to the source-included app file, so the theory takes ordinals (xunit theories must be
    // public and CS0051 forbids an internal parameter type) — same idiom the tier-geometry theory above uses.
    [Theory]
    [InlineData(699.9f, (byte)HomeHeroTier.Wide, (byte)HomeHeroTier.Narrow)]     // narrowing applies immediately, skipping straight to Narrow
    [InlineData(699.9f, (byte)HomeHeroTier.Narrow, (byte)HomeHeroTier.Narrow)]
    [InlineData(956f, (byte)HomeHeroTier.Wide, (byte)HomeHeroTier.Wide)]         // inside the recovery band, holds Wide
    [InlineData(956f, (byte)HomeHeroTier.Medium, (byte)HomeHeroTier.Medium)]     // not yet cleared threshold+hysteresis, holds Medium
    [InlineData(1004f, (byte)HomeHeroTier.Medium, (byte)HomeHeroTier.Wide)]      // cleared 980+24, widens
    [InlineData(700f, (byte)HomeHeroTier.Wide, (byte)HomeHeroTier.Medium)]       // narrowing from Wide applies at the plain threshold
    [InlineData(676f, (byte)HomeHeroTier.Narrow, (byte)HomeHeroTier.Narrow)]
    [InlineData(724f, (byte)HomeHeroTier.Narrow, (byte)HomeHeroTier.Medium)]     // 700 + 24, widens out of narrow
    public void TierFor_UsesHysteresis(float width, byte previousByte, byte expectedByte)
        => Assert.Equal((HomeHeroTier)expectedByte, HomeHeroLayout.TierFor(width, (HomeHeroTier)previousByte));

    [Fact]
    public void HeightFor_MatchesForsHeight()
    {
        Assert.Equal(
            HomeHeroLayout.For(850f, 520f, true, HomeHeroTier.Medium).Height,
            HomeHeroLayout.HeightFor(850f, 520f, true, HomeHeroTier.Medium));
    }

    [Fact]
    public void ArtworkFade_Is96()
        => Assert.Equal(96f, HomeHeroLayout.ArtworkFade);

    /// <summary>The tier heights are not a magic table: each is the sum of the SAME blocks the renderer stacks, and
    /// each block is a ramp line height plus a Spacing rung. If someone re-hand-picks a size in HomeCards.HeroBand and
    /// forgets this file, the renderer and the virtual estimator disagree and the feed re-pins its scroll anchor
    /// mid-scroll — so the arithmetic is pinned here explicitly rather than only as a total.</summary>
    [Theory]
    [InlineData((int)HomeHeroTier.Wide, (byte)HomeHeroDensity.Full, 2, true, 2f * 60f)]
    [InlineData((int)HomeHeroTier.Medium, (byte)HomeHeroDensity.Full, 2, true, 2f * 40f)]
    [InlineData((int)HomeHeroTier.Narrow, (byte)HomeHeroDensity.Compact, 1, false, 36f)]
    public void ContentHeight_IsTheSumOfOnRampBlocks(int tierOrdinal, byte densityByte, int titleLines,
        bool showTags, float titleBlock)
    {
        var tier = (HomeHeroTier)tierOrdinal;
        var density = (HomeHeroDensity)densityByte;
        bool compact = density == HomeHeroDensity.Compact;
        float copyPaddingY = compact ? HomeHeroLayout.CompactCopyPaddingY : HomeHeroLayout.CopyPaddingY;
        const float eyebrowBlock = 16f + 8f;
        const float titleMargin = 16f;                              // #106 — was 12
        const float tagsBlock = 20f + 12f;
        float metaBlock = compact ? 20f + 8f : 20f + 20f;           // #106 — Full was 20 + 16
        const float actionsBlock = 32f;

        Assert.Equal(
            2f * copyPaddingY + eyebrowBlock + titleBlock + titleMargin + (showTags ? tagsBlock : 0f) + metaBlock + actionsBlock,
            HomeHeroLayout.ContentHeight(tier, density, titleLines, showTags, hasPulse: false));
    }
}
