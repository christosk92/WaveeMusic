// ── Wavee.Tests/HomeHeroLayoutTests.cs — the hero's one geometry contract (Wave 5, owner P; ported from 0.2.9) ─────
//
// 0.2.9's facts, verbatim in shape. ONE NUMBER MOVED ON PURPOSE (ch 11 §9.2): the hero's action row is built from the
// media pill, whose height is `Controls.PillHeight` = 36, and 0.2.9's estimator reserved 32 (`Spacing.XXXL`) for it —
// so every tier grew by 4 DIP: 384/344/336 → 388/348/340. The renderer and the estimator now state the same row.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class HomeHeroLayoutTests
{
    [Theory]
    // 388/348/340 (0.2.9: 384/344/336) — the 40-DIP pulse block is reserved for every hero, and the actions block is the
    // 36-DIP pill row (Controls.PillHeight), not 0.2.9's 32.
    [InlineData(699.9f, (byte)0, 340f, true)]
    [InlineData(700f, (byte)1, 348f, false)]
    [InlineData(979.9f, (byte)1, 348f, false)]
    [InlineData(980f, (byte)2, 388f, false)]
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
    public void FlattenedSurface_PreservesTheRendererEstimatorArithmetic()
    {
        // 0.2.9: 384 / 344 / 336. +4 each: the pill row is 36, not 32 (file header).
        Assert.Equal(388f, HomeHeroLayout.ContentHeight(HomeHeroTier.Wide));
        Assert.Equal(348f, HomeHeroLayout.ContentHeight(HomeHeroTier.Medium));
        Assert.Equal(340f, HomeHeroLayout.ContentHeight(HomeHeroTier.Narrow));
        Assert.Equal(96f, HomeHeroLayout.ArtworkFade);
    }

    /// <summary>The tier heights are not a magic table: each is the sum of the SAME blocks the renderer stacks.</summary>
    [Theory]
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
        const float pulseBlock = 28f + 12f;  // the flip-countdown digit row + a 12 margin
        float actionsBlock = Controls.PillHeight;   // CHANGED (0.2.9: 32f) — the media pill the row is built from

        Assert.Equal(36f, actionsBlock);
        Assert.Equal(
            2f * copyPaddingY + eyebrowBlock + titleBlock + titleMargin + tagsBlock + metaBlock + pulseBlock + actionsBlock,
            HomeHeroLayout.ContentHeight(tier));
    }
}
