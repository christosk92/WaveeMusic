using Xunit;

namespace Wavee.Tests;

public class HomeHeroLayoutTests
{
    [Theory]
    // 384/344/336 = the pre-daylist 344/304/296 plus the 40-DIP pulse block (the 28-DIP flip-countdown digit row +
    // its 12 margin) that HeroBand now reserves for every hero — the slot is always present so the virtual estimator
    // and the renderer state the same geometry; non-daylist heroes collapse it to an empty BoxEl.
    [InlineData(699.9f, (byte)0, 336f, true)]
    [InlineData(700f, (byte)1, 344f, false)]
    [InlineData(979.9f, (byte)1, 344f, false)]
    [InlineData(980f, (byte)2, 384f, false)]
    public void TierGeometry_IsExact(float width, byte tier, float height, bool stacked)
    {
        var metrics = HomeHeroLayout.For(width);
        Assert.Equal((HomeHeroTier)tier, metrics.Tier);
        Assert.Equal(height, metrics.Height);
        Assert.Equal(height, metrics.ArtworkSize);
        Assert.Equal(stacked, metrics.Stacked);
        Assert.Equal(48f, metrics.CopyPaddingX);
        Assert.Equal(44f, metrics.CopyPaddingY);
        Assert.Equal(height, HomeHeroLayout.HeightFor(width));
    }

    [Fact]
    public void FlattenedSurface_PreservesThePreviousRendererEstimatorArithmetic()
    {
        Assert.Equal(384f, HomeHeroLayout.ContentHeight(HomeHeroTier.Wide));
        Assert.Equal(344f, HomeHeroLayout.ContentHeight(HomeHeroTier.Medium));
        Assert.Equal(336f, HomeHeroLayout.ContentHeight(HomeHeroTier.Narrow));
        Assert.Equal(96f, HomeHeroLayout.ArtworkFade);
    }

    /// <summary>The tier heights are not a magic table: each is the sum of the SAME blocks the renderer stacks, and
    /// each block is a ramp line height plus a Spacing rung. If someone re-hand-picks a size in HomeCards.HeroBand and
    /// forgets this file, the renderer and the virtual estimator disagree and the feed re-pins its scroll anchor
    /// mid-scroll — so the arithmetic is pinned here explicitly rather than only as a total.</summary>
    [Theory]
    // HomeHeroTier is internal to the source-included app file, so the theory takes the ordinal (xunit theories must
    // be public and CS0051 forbids an internal parameter type).
    [InlineData((int)HomeHeroTier.Wide, 2f * 60f)]
    [InlineData((int)HomeHeroTier.Medium, 2f * 40f)]
    [InlineData((int)HomeHeroTier.Narrow, 2f * 36f)]
    public void ContentHeight_IsTheSumOfOnRampBlocks(int tierOrdinal, float titleBlock)
    {
        var tier = (HomeHeroTier)tierOrdinal;
        const float copyPaddingY = 44f;      // Spacing.L + Spacing.XXL + Spacing.XS
        const float eyebrowBlock = 16f + 8f; // Caption 12/16 + an 8 margin
        const float titleMargin = 12f;
        const float tagsBlock = 20f + 12f;   // Caption 12/16 + 2x2 padding, + a 12 margin
        const float metaBlock = 20f + 16f;   // Body 14/20 + a 16 margin
        const float pulseBlock = 28f + 12f;  // the flip-countdown digit row (FlipCountdown.HeroRowHeight) + a 12 margin
        const float actionsBlock = 32f;      // the hero button row

        Assert.Equal(
            2f * copyPaddingY + eyebrowBlock + titleBlock + titleMargin + tagsBlock + metaBlock + pulseBlock + actionsBlock,
            HomeHeroLayout.ContentHeight(tier));
    }
}
