using FluentGpu.Dsl;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    [Theory]
    // Thresholds are the shipped 880/600/360 (lowered from 1040/760/480 — "the stacked layout triggered way too
    // soon"), hysteresis 24: hold rows sit just inside a tier's keep-band, promote/demote rows just outside it.
    [InlineData(380f, ArtistHeroTier.Narrow, ArtistHeroTier.Narrow)]
    [InlineData(504f, ArtistHeroTier.Narrow, ArtistHeroTier.Compact)]
    [InlineData(610f, ArtistHeroTier.Compact, ArtistHeroTier.Compact)]
    [InlineData(700f, ArtistHeroTier.Compact, ArtistHeroTier.Medium)]
    [InlineData(850f, ArtistHeroTier.Wide, ArtistHeroTier.Medium)]
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
        var compact = ArtistHeroLayout.For(500f, ArtistHeroTier.Compact);
        var narrow = ArtistHeroLayout.For(320f, ArtistHeroTier.Narrow);

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
        // Lowered again (D82: the header was taller stacked than wide) — bio 2->1 line, one horizontal meta row at
        // Compact — but the identity band still reserves a real anatomy, not a bare title.
        Assert.True(ArtistHeroLayout.CompactExpandedIdentityHeight > 200f);
        Assert.True(ArtistHeroLayout.NarrowExpandedIdentityHeight > 240f);
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
        Assert.Equal(Spacing.PageNarrow, ArtistHeroLayout.PageGutterFor(320f));
        Assert.Equal(Spacing.L, ArtistHeroLayout.PageGutterFor(420f));
        Assert.Equal(Spacing.XXXL, ArtistHeroLayout.PageGutterFor(700f));
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

    /// <summary>D81: the pre-fix stacked totals (Compact 644, Narrow 628) ran 1.6-1.9x MediumHeight (384) — "half
    /// again larger" than the horizontal tiers either side of them. The cut (2-line name on every tier, one
    /// horizontal meta row at Compact, End-justified identity slack) brings both down to ~450-480. This pins them to
    /// a generous band around Medium so a future re-inflation of either constant — the exact defect this item
    /// fixed — fails here instead of only being noticed by eye.</summary>
    [Fact]
    public void StackedTotals_StayRoughlyInLineWithMediumHeightInsteadOfHalfAgainLarger()
    {
        Assert.InRange(ArtistHeroLayout.CompactHeight,
            ArtistHeroLayout.MediumHeight, ArtistHeroLayout.MediumHeight * 1.25f);
        // Narrow legitimately runs a little ahead of Compact — it alone keeps the two-row action stack — so it gets
        // a slightly wider band, still nowhere near the old "half again larger" totals.
        Assert.InRange(ArtistHeroLayout.NarrowHeight,
            ArtistHeroLayout.MediumHeight, ArtistHeroLayout.MediumHeight * 1.35f);
    }

    // ── the viewport cap (§3, D83) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroOrAbsentViewport_LeavesHeightsUnclamped()
    {
        var wide = ArtistHeroLayout.For(1440f, pageViewportHeight: 0f, ArtistHeroTier.Wide);
        Assert.Equal(ArtistHeroLayout.WideHeight, wide.MinHeight);
    }

    [Fact]
    public void HorizontalTiers_ClampToTheViewportFraction_NeverBelowTheCopyBudget()
    {
        // 900x600 -> a page viewport of ~520 DIP (window height minus the shell's chrome). cap = 520*0.45 = 234,
        // below WideHeight (360) -> the hero clamps, but never under the copy's own worst-case budget.
        var m = ArtistHeroLayout.For(1440f, pageViewportHeight: 520f, ArtistHeroTier.Wide);
        Assert.True(m.MinHeight < ArtistHeroLayout.WideHeight);
        Assert.True(m.MinHeight >= ArtistHeroLayout.CopyBudgetFor(ArtistHeroTier.Wide));
    }

    [Fact]
    public void StackedTiers_ClampThePhotoBandButKeepTheFullIdentityBand()
    {
        var m = ArtistHeroLayout.For(320f, pageViewportHeight: 300f, ArtistHeroTier.Narrow);
        Assert.True(m.MinHeight < ArtistHeroLayout.NarrowHeight);
        // The identity band's full anatomy survives the clamp; only the photo band above it gives.
        float photo = ArtistHeroLayout.PhotoHeightFor(m);
        Assert.True(photo >= ArtistHeroLayout.MinPhotoHeight);
        Assert.True(m.MinHeight - photo >= ArtistHeroLayout.IdentityHeightFor(ArtistHeroTier.Narrow) - 0.5f);
    }

    [Fact]
    public void PhotoHeightFor_NeverBelowMinPhotoHeight()
    {
        foreach (var tier in new[] { ArtistHeroTier.Narrow, ArtistHeroTier.Compact, ArtistHeroTier.Medium, ArtistHeroTier.Wide })
        {
            float width = tier switch
            {
                ArtistHeroTier.Narrow => 320f,
                ArtistHeroTier.Compact => 500f,
                ArtistHeroTier.Medium => 700f,
                _ => 1440f,
            };
            var m = ArtistHeroLayout.For(width, pageViewportHeight: 50f, tier);
            Assert.True(ArtistHeroLayout.PhotoHeightFor(m) >= ArtistHeroLayout.MinPhotoHeight);
        }
    }

    [Fact]
    public void GenerousViewport_NeverClampsBelowTheBaseHeight()
    {
        var m = ArtistHeroLayout.For(1440f, pageViewportHeight: 4000f, ArtistHeroTier.Wide);
        Assert.Equal(ArtistHeroLayout.WideHeight, m.MinHeight);
    }

    [Fact]
    public void HeroHeightForOverloads_AgreeWithForsMinHeight()
    {
        Assert.Equal(
            ArtistHeroLayout.For(1440f, 520f, ArtistHeroTier.Wide).MinHeight,
            ArtistHeroLayout.HeroHeightFor(1440f, 520f));
        float h = ArtistHeroLayout.HeroHeightFor(1440f, 520f);
        Assert.Equal(h + ArtistHeroLayout.ContentBlendTail, ArtistHeroLayout.BlendBackdropHeightFor(1440f, 520f));
        Assert.Equal(h / (h + ArtistHeroLayout.ContentBlendTail), ArtistHeroLayout.BlendBoundaryFor(1440f, 520f));
    }
}
