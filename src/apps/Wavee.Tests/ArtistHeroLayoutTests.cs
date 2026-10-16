// ── Wavee.Tests/ArtistHeroLayoutTests.cs — the full-bleed artist hero's sub-tiers, heights, gutters and collapse ─────────
//
// Ported from _old/Wavee.Tests/ArtistHeroLayoutTests.cs. Since TC6 the sub-tier ladder and the per-tier heights are Classic's tier records
// (`Rules\Tiers.Artist.cs`, `ClassicArtistTiers`); the core keeps the tier-free arithmetic (`ArtistHeroLayout`: the photograph's extent, its fade, the
// collapse). The tuned Standard numbers are pinned here unchanged.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Xunit;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    /// <summary>The Standard form of a fresh resolve at <paramref name="width"/> (or held against <paramref name="previous"/>), at the Standard gutter.</summary>
    static ArtistHeroMetrics At(float width, ArtistHeroTier? previous = null)
        => ClassicArtistTiers.StandardForm(ClassicArtistTiers.HeroTierFor(width, previous is { } p ? (int)p : -1)).Metrics(36f);

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
    public void The_hero_ladder_holds_24_either_way(float width, ArtistHeroTier previous, ArtistHeroTier expected)
        => Assert.Equal(expected, ClassicArtistTiers.HeroTierFor(width, (int)previous));

    [Theory]
    [InlineData(359f, ArtistHeroTier.Narrow)] [InlineData(360f, ArtistHeroTier.Compact)] [InlineData(599f, ArtistHeroTier.Compact)]
    [InlineData(600f, ArtistHeroTier.Medium)] [InlineData(879f, ArtistHeroTier.Medium)] [InlineData(880f, ArtistHeroTier.Wide)]
    public void A_fresh_resolve_uses_the_edges_themselves(float width, ArtistHeroTier expected)
        => Assert.Equal(expected, ClassicArtistTiers.HeroTierFor(width));

    [Fact]
    public void The_policy_names_the_hero_ladder_and_the_core_reads_it_by_id()
    {
        Assert.Same(ClassicArtistTiers.Hero, ArtistLadders.Of(ClassicTiers.Policy, ArtistLadders.Hero));
        Assert.Same(ClassicArtistTiers.Reader, ArtistLadders.Of(ClassicTiers.Policy, ArtistLadders.Reader));
        Assert.Same(ClassicProfileTiers.List, ArtistLadders.Of(ClassicTiers.Policy, ArtistLadders.ProfileList));
        Assert.Empty(ClassicTiers.Policy.Validate());
        // A structure that declares none: always the top index (the Wide hero, the shell before tiers).
        Assert.Equal(3, ArtistLadders.Of(null, ArtistLadders.Hero).Resolve(320f, -1));
    }

    [Fact]
    public void WideAndMedium_UseHorizontalVeils()
    {
        var wide = At(1440f);
        var medium = At(700f);
        Assert.Equal(ArtistHeroVeilAxis.Horizontal, wide.VeilAxis);
        Assert.Equal(ArtistHeroVeilAxis.Horizontal, medium.VeilAxis);
        Assert.False(wide.Stacked);
        Assert.False(medium.Stacked);
        Assert.Equal(440f, wide.MinHeight);
        Assert.Equal(384f, medium.MinHeight);
        Assert.True(wide.CopyMaxWidth > medium.CopyMaxWidth);
    }

    // Stacked tiers paint no overlay veil at Standard: the photograph is a top band and the identity column sits below it on the page surface, so
    // Vertical here means "stacked layout", not "vertical gradient over the photo".
    [Fact]
    public void CompactAndNarrow_StackThePhotoAboveTheIdentityColumn()
    {
        var compact = At(500f);
        var narrow = At(320f);
        Assert.True(compact.Stacked);
        Assert.True(narrow.Stacked);
        Assert.Equal(200f, ArtistHeroLayout.PhotoHeightFor(compact));
        Assert.Equal(176f, ArtistHeroLayout.PhotoHeightFor(narrow));
        // The fixed stacked hero reserves the complete identity anatomy it declares (the old 240/276 remainders ended at the top of the Play pill).
        Assert.Equal(200f + 248f, compact.MinHeight);
        Assert.Equal(176f + 292f, narrow.MinHeight);
    }

    [Fact]
    public void WideAndMedium_PhotoOwnsTheWholeHero()
    {
        var wide = At(1440f);
        var medium = At(700f);
        Assert.Equal(wide.MinHeight, ArtistHeroLayout.PhotoHeightFor(wide));
        Assert.Equal(medium.MinHeight, ArtistHeroLayout.PhotoHeightFor(medium));
    }

    [Fact]
    public void FadeBand_AlwaysCoversTheParallaxLag()
    {
        foreach (var tier in new[] { ArtistHeroTier.Narrow, ArtistHeroTier.Compact, ArtistHeroTier.Medium, ArtistHeroTier.Wide })
        {
            float height = ClassicArtistTiers.StandardForm(tier).Height;
            Assert.True(ArtistHeroLayout.PhotoFadeBandFor(height) > height * ArtistHeroLayout.PhotoParallaxFraction);
        }
    }

    [Fact]
    public void CollapseDistance_LeavesTheSharedCompactIdentityFloor()
    {
        foreach (var tier in new[] { ArtistHeroTier.Narrow, ArtistHeroTier.Compact, ArtistHeroTier.Medium, ArtistHeroTier.Wide })
        {
            float height = ClassicArtistTiers.StandardForm(tier).Height;
            float distance = ArtistHeroLayout.CollapseDistance(height);
            Assert.Equal(ArtistHeroLayout.CompactIdentityHeight, height - distance);
            Assert.Equal(VerticalLayout.CompactIdentityHeight, ArtistHeroLayout.CompactIdentityHeight);
        }
    }

    /// <summary>D81: the pre-fix stacked totals (Compact 644, Narrow 628) ran 1.6-1.9x MediumHeight (384). The cut brings
    /// both down to ~450-480; this pins them to a generous band around Medium so a re-inflation fails here.</summary>
    [Fact]
    public void StackedTotals_StayRoughlyInLineWithMediumHeightInsteadOfHalfAgainLarger()
    {
        float medium = ClassicArtistTiers.StandardForm(ArtistHeroTier.Medium).Height;
        Assert.InRange(ClassicArtistTiers.StandardForm(ArtistHeroTier.Compact).Height, medium, medium * 1.25f);
        // Narrow legitimately runs a little ahead of Compact — it alone keeps the two-row action stack.
        Assert.InRange(ClassicArtistTiers.StandardForm(ArtistHeroTier.Narrow).Height, medium, medium * 1.35f);
    }
}
