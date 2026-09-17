// ── Wavee.Tests/HomeArtistRowLayoutTests.cs — the podium's tier, ramp, gap and double click (ported from 0.2.9) ─────

using Wavee;
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
        // A zero/absent width is "not measured", not "narrow" — TierFor owns the pre-measure case.
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
        => Assert.Equal(HomeArtistRowLayout.TierSpine,
            HomeArtistRowLayout.TierFor(899f, HomeArtistRowLayout.TierWide, initialized: true));

    [Fact]
    public void TierFor_FirstMeasure_TakesNominalTierWithoutHysteresis()
    {
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

    // ── E — the podium ramp (#82) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BaseArtSize_IsTheRankEncodesScaleRamp()
    {
        Assert.Equal(76f, HomeArtistRowLayout.BaseArtSize(0));
        Assert.Equal(60f, HomeArtistRowLayout.BaseArtSize(1));
        Assert.Equal(60f, HomeArtistRowLayout.BaseArtSize(2));
        Assert.Equal(46f, HomeArtistRowLayout.BaseArtSize(3));
        Assert.Equal(46f, HomeArtistRowLayout.BaseArtSize(9));
    }

    [Fact]
    public void RampScaleFor_NeverShrinksBelowOne()
        => Assert.Equal(HomeArtistRowLayout.MinArtScale,
            HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 40f, count: 10, podChrome: 8f));

    [Fact]
    public void RampScaleFor_GrowsToFillASpaciousCard()
    {
        // Ten artists on a ~900-DIP card: ~82.8 DIP per fitted column against the ramp's own 59.8 average box.
        float fittedColumnWidth = (900f - 9 * 8f) / 10f;
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth, count: 10, podChrome: 8f);
        Assert.True(scale > 1f);
        Assert.Equal(fittedColumnWidth / 59.8f, scale, 2);
    }

    [Fact]
    public void RampScaleFor_TotalScaledFootprintMatchesTheFittedWidth()
    {
        const int count = 10;
        const float podChrome = 8f;
        float fittedColumnWidth = (900f - 9 * 8f) / count;
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth, count, podChrome);

        float totalDefault = 0f;
        for (int i = 0; i < count; i++) totalDefault += HomeArtistRowLayout.BaseArtSize(i) + podChrome;
        Assert.Equal(count * fittedColumnWidth, scale * totalDefault, 1);
    }

    [Fact]
    public void RampScaleFor_ClampsAtTheCeilingForAFewArtistsOnAWideCard()
        => Assert.Equal(HomeArtistRowLayout.MaxArtScale,
            HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 500f, count: 3, podChrome: 8f));

    [Fact]
    public void RampScaleFor_ZeroCount_ReadsAsTheFloor()
        => Assert.Equal(HomeArtistRowLayout.MinArtScale,
            HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 100f, count: 0, podChrome: 8f));

    [Fact]
    public void ArtSize_ScalesEveryRankByTheSameFactor()
    {
        Assert.Equal(76f * 1.25f, HomeArtistRowLayout.ArtSize(0, 1.25f), 3);
        Assert.Equal(60f * 1.25f, HomeArtistRowLayout.ArtSize(1, 1.25f), 3);
        Assert.Equal(46f * 1.25f, HomeArtistRowLayout.ArtSize(4, 1.25f), 3);
    }

    [Fact]
    public void ModuleGap_IsThisRowsOwnTierBoundary_NotTheSharedHelpers()
    {
        Assert.Equal(32f, HomeArtistRowLayout.ModuleGap(1080f));
        Assert.Equal(24f, HomeArtistRowLayout.ModuleGap(1079f));
        // …and it is NOT the shared module gap (ch 10 §9 real defect #2).
        Assert.NotEqual(HomeModuleLayout.Gap(1080f), HomeArtistRowLayout.ModuleGap(1080f));
    }

    // ── F — pod / Mixview-node double-click-to-navigate (#83) ───────────────────────────────────────────────────────

    [Fact]
    public void IsDoubleClick_SameUriWithinWindow_IsTrue()
    {
        Assert.True(HomeArtistRowLayout.IsDoubleClick("spotify:artist:1", "spotify:artist:1", 1000, 1300));
        Assert.True(HomeArtistRowLayout.IsDoubleClick("spotify:artist:1", "spotify:artist:1", 1000, 1400));   // exact edge
    }

    [Fact]
    public void IsDoubleClick_SameUriOutsideWindow_IsFalse()
        => Assert.False(HomeArtistRowLayout.IsDoubleClick("spotify:artist:1", "spotify:artist:1", 1000, 1401));

    [Fact]
    public void IsDoubleClick_DifferentUri_IsFalse()
        => Assert.False(HomeArtistRowLayout.IsDoubleClick("spotify:artist:2", "spotify:artist:1", 1000, 1100));

    [Fact]
    public void IsDoubleClick_NoPriorClick_IsFalse()
        => Assert.False(HomeArtistRowLayout.IsDoubleClick("spotify:artist:1", null, 0, 100));

    [Fact]
    public void IsDoubleClick_TicksOutOfOrder_NeverReadAsADouble()
        => Assert.False(HomeArtistRowLayout.IsDoubleClick("spotify:artist:1", "spotify:artist:1", 1400, 1000));
}
