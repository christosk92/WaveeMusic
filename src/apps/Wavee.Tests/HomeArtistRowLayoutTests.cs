using Wavee.Features.Home;
using Xunit;

namespace Wavee.Tests;

public class HomeArtistRowLayoutTests
{
    [Fact]
    public void NominalTierFor_Boundary900_SeparatesWideFromSpine()
    {
        Assert.Equal(HomeArtistRowLayout.TierWide, HomeArtistRowLayout.NominalTierFor(900f));   // ≥900 → wide
        Assert.Equal(HomeArtistRowLayout.TierSpine, HomeArtistRowLayout.NominalTierFor(899f));  // <900 → spine
    }

    [Fact]
    public void NominalTierFor_ZeroOrNegativeWidth_ReadsAsWide()
    {
        // A zero/absent (or nonsense negative) width is "not measured", not "narrow" — TierFor owns the pre-measure
        // case, so the nominal function must not invent a spine arm here.
        Assert.Equal(HomeArtistRowLayout.TierWide, HomeArtistRowLayout.NominalTierFor(0f));
        Assert.Equal(HomeArtistRowLayout.TierWide, HomeArtistRowLayout.NominalTierFor(-5f));
    }

    [Fact]
    public void TierFor_Oscillates900PlusMinus24_HoldsSpineUntil924()
    {
        int tier = HomeArtistRowLayout.TierFor(905f, HomeArtistRowLayout.TierSpine, initialized: true);
        Assert.Equal(HomeArtistRowLayout.TierSpine, tier);   // nominal says wide, but w - 24 has not cleared 900

        tier = HomeArtistRowLayout.TierFor(923f, tier, initialized: true);
        Assert.Equal(HomeArtistRowLayout.TierSpine, tier);   // still inside the dip band

        tier = HomeArtistRowLayout.TierFor(924f, tier, initialized: true);
        Assert.Equal(HomeArtistRowLayout.TierWide, tier);    // widen back only once w - 24 ≥ 900
    }

    [Fact]
    public void TierFor_Narrowing_AppliesImmediately()
    {
        // No hysteresis in the narrowing direction: the side-by-side row simply cannot hold below the boundary.
        Assert.Equal(HomeArtistRowLayout.TierSpine,
            HomeArtistRowLayout.TierFor(899f, HomeArtistRowLayout.TierWide, initialized: true));
    }

    [Fact]
    public void TierFor_FirstMeasure_TakesNominalTierWithoutHysteresis()
    {
        // Pre-measure, `prev` is a construction default / a viewport seed — not a tier the user has seen — so the
        // first real width wins outright in BOTH directions.
        Assert.Equal(HomeArtistRowLayout.TierWide,
            HomeArtistRowLayout.TierFor(905f, prev: HomeArtistRowLayout.TierSpine, initialized: false));
        Assert.Equal(HomeArtistRowLayout.TierSpine,
            HomeArtistRowLayout.TierFor(700f, prev: HomeArtistRowLayout.TierWide, initialized: false));
        // A zero/absent width never overrides the caller's current tier, measured or not.
        Assert.Equal(HomeArtistRowLayout.TierSpine,
            HomeArtistRowLayout.TierFor(0f, prev: HomeArtistRowLayout.TierSpine, initialized: false));
    }

    [Fact]
    public void InitialTierForViewport_ProxiesNominalTier()
    {
        Assert.Equal(HomeArtistRowLayout.NominalTierFor(1280f), HomeArtistRowLayout.InitialTierForViewport(1280f));
        Assert.Equal(HomeArtistRowLayout.NominalTierFor(720f), HomeArtistRowLayout.InitialTierForViewport(720f));
        Assert.Equal(HomeArtistRowLayout.TierWide, HomeArtistRowLayout.InitialTierForViewport(1280f));
        Assert.Equal(HomeArtistRowLayout.TierSpine, HomeArtistRowLayout.InitialTierForViewport(720f));
    }
}
