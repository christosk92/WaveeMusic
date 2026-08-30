using FluentGpu.Dsl;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    [Theory]
    [InlineData(420f, ArtistHeroTier.Narrow, ArtistHeroTier.Narrow)]
    [InlineData(504f, ArtistHeroTier.Narrow, ArtistHeroTier.Compact)]
    [InlineData(700f, ArtistHeroTier.Compact, ArtistHeroTier.Compact)]
    [InlineData(784f, ArtistHeroTier.Compact, ArtistHeroTier.Medium)]
    [InlineData(1000f, ArtistHeroTier.Wide, ArtistHeroTier.Medium)]
    [InlineData(1016f, ArtistHeroTier.Wide, ArtistHeroTier.Wide)]
    [InlineData(1064f, ArtistHeroTier.Medium, ArtistHeroTier.Wide)]
    public void TierFor_UsesTheDetailLayoutHysteresis(float width, ArtistHeroTier previous, ArtistHeroTier expected)
        => Assert.Equal(expected, ArtistHeroLayout.TierFor(width, previous));

    [Fact]
    public void WideAndMedium_UseHorizontalVeils()
    {
        var wide = ArtistHeroLayout.For(1440f, ArtistHeroTier.Wide);
        var medium = ArtistHeroLayout.For(900f, ArtistHeroTier.Medium);

        Assert.Equal(ArtistHeroVeilAxis.Horizontal, wide.VeilAxis);
        Assert.Equal(ArtistHeroVeilAxis.Horizontal, medium.VeilAxis);
        Assert.False(wide.Stacked);
        Assert.False(medium.Stacked);
        Assert.Equal(ArtistHeroLayout.WideHeight, wide.MinHeight);
        Assert.Equal(ArtistHeroLayout.MediumHeight, medium.MinHeight);
        Assert.True(wide.CopyMaxWidth > medium.CopyMaxWidth);
    }

    // Stacked tiers no longer paint an overlay veil at all: the photograph is a top band and the identity column sits
    // below it on the page surface, so Vertical here means "stacked layout", not "vertical gradient over the photo".
    [Fact]
    public void CompactAndNarrow_StackThePhotoAboveTheIdentityColumn()
    {
        var compact = ArtistHeroLayout.For(720f, ArtistHeroTier.Compact);
        var narrow = ArtistHeroLayout.For(420f, ArtistHeroTier.Narrow);

        Assert.Equal(ArtistHeroVeilAxis.Vertical, compact.VeilAxis);
        Assert.Equal(ArtistHeroVeilAxis.Vertical, narrow.VeilAxis);
        Assert.True(compact.Stacked);
        Assert.True(narrow.Stacked);

        // The photo band is a strict top slice — the identity column keeps a real share of the fixed hero height.
        Assert.Equal(ArtistHeroLayout.CompactPhotoHeight, ArtistHeroLayout.PhotoHeightFor(compact));
        Assert.Equal(ArtistHeroLayout.NarrowPhotoHeight, ArtistHeroLayout.PhotoHeightFor(narrow));
        Assert.True(ArtistHeroLayout.CompactPhotoHeight < compact.MinHeight);
        Assert.True(ArtistHeroLayout.NarrowPhotoHeight < narrow.MinHeight);
    }

    /// <summary>The fixed stacked hero must reserve the complete identity anatomy it declares. The old 240/276-DIP
    /// remainders ended at the top of the Play pill once a real artist carried bio + rank + both audience figures.</summary>
    [Fact]
    public void StackedHeroes_ReserveTheirFullIdentityAndActionBands()
    {
        Assert.Equal(ArtistHeroLayout.CompactPhotoHeight + ArtistHeroLayout.CompactExpandedIdentityHeight,
            ArtistHeroLayout.CompactHeight);
        Assert.Equal(ArtistHeroLayout.NarrowPhotoHeight + ArtistHeroLayout.NarrowExpandedIdentityHeight,
            ArtistHeroLayout.NarrowHeight);
        Assert.True(ArtistHeroLayout.CompactExpandedIdentityHeight > 240f);
        Assert.True(ArtistHeroLayout.NarrowExpandedIdentityHeight > 276f);
    }

    [Fact]
    public void WideAndMedium_PhotoOwnsTheWholeHero()
    {
        var wide = ArtistHeroLayout.For(1440f, ArtistHeroTier.Wide);
        var medium = ArtistHeroLayout.For(900f, ArtistHeroTier.Medium);

        Assert.Equal(wide.MinHeight, ArtistHeroLayout.PhotoHeightFor(wide));
        Assert.Equal(medium.MinHeight, ArtistHeroLayout.PhotoHeightFor(medium));
    }

    [Fact]
    public void PageGutter_UsesOnlySemanticSpacingTokens()
    {
        Assert.Equal(Spacing.PageNarrow, ArtistHeroLayout.PageGutterFor(420f));
        Assert.Equal(Spacing.L, ArtistHeroLayout.PageGutterFor(700f));
        Assert.Equal(Spacing.XXXL, ArtistHeroLayout.PageGutterFor(900f));
        Assert.Equal(Spacing.PageWide, ArtistHeroLayout.PageGutterFor(1200f));
    }

    [Fact]
    public void FadeBand_AlwaysCoversTheParallaxLag()
    {
        foreach (float height in new[] { ArtistHeroLayout.MediumHeight, ArtistHeroLayout.WideHeight,
                                        ArtistHeroLayout.NarrowHeight, ArtistHeroLayout.CompactHeight })
            Assert.True(ArtistHeroLayout.PhotoFadeBandFor(height)
                        > height * ArtistHeroLayout.PhotoParallaxFraction);
    }

    [Fact]
    public void CollapseDistance_LeavesTheSharedCompactIdentityFloor()
    {
        foreach (float height in new[] { ArtistHeroLayout.MediumHeight, ArtistHeroLayout.WideHeight,
                                        ArtistHeroLayout.NarrowHeight, ArtistHeroLayout.CompactHeight })
        {
            float distance = ArtistHeroLayout.CollapseDistance(height);
            Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, height - distance);
            Assert.Equal(DetailVerticalLayout.CompactIdentityHeight, ArtistHeroLayout.CompactIdentityHeight);
        }
    }

    // The tier-driven "drop Follow" policy is gone with the capsule bar it protected — the context band's actions are
    // words that always fit, and the PIVOT is what yields under width pressure (ContextBandLayoutTests).
}
