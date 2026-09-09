using System;
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
    public void RampScaleFor_ClampsAtTheFloorForAVeryNarrowRow()
    {
        // A very narrow row clamps at MinArtScale (0.5, roughly half the prototype ramp) rather than shrinking
        // further — the pods stop shrinking and the row clips at the trailing edge instead of wrapping.
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 20f, count: 10, podChrome: 8f);
        Assert.Equal(HomeArtistRowLayout.MinArtScale, scale);
    }

    [Fact]
    public void RampScaleFor_ShrinksBelowOneForAModeratelyNarrowRow()
    {
        // Between the old floor (1) and the new one (0.5): the ramp now shrinks instead of clamping back up to the
        // prototype's own sizing — this is the whole fix for "ten artists wrap on a narrow card".
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 44f, count: 10, podChrome: 8f);
        Assert.InRange(scale, HomeArtistRowLayout.MinArtScale, 1f);
        Assert.Equal(44f / 59.8f, scale, 2);
    }

    [Fact]
    public void RampScaleFor_GrowsToFillASpaciousCard()
    {
        // The reported screenshot: ten artists on a ~900-DIP card. Fit's own per-column arithmetic (forced to 10
        // equal columns) gives ~82.8 DIP each; the ramp's own average box width for ten pods (84 + 68 + 68 + 7×54,
        // ÷10) is 59.8 — BELOW the uniform column, so the ramp must GROW to use the room Fit found.
        float fittedColumnWidth = (900f - 9 * 8f) / 10f;
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth, count: 10, podChrome: 8f);
        Assert.True(scale > 1f);
        Assert.Equal(fittedColumnWidth / 59.8f, scale, 2);
    }

    [Fact]
    public void RampScaleFor_TotalScaledFootprintMatchesTheFittedWidth()
    {
        // The load-bearing identity RampScaleFor is built on: scale × (the ramp's own total default footprint) must
        // equal count × fittedColumnWidth — the same total width FillRowVirtualLayout.Fit already solved for. This is
        // what makes the strip actually FILL the card rather than merely stretch by some arbitrary amount.
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
    {
        // Three pods on a very wide card would otherwise solve for an enormous per-column width — the ceiling keeps
        // avatars from turning into portraits.
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnWidth: 500f, count: 3, podChrome: 8f);
        Assert.Equal(HomeArtistRowLayout.MaxArtScale, scale);
    }

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
    public void PodWidth_IsArtPlusChromeFlooredAtMinPodWidth()
    {
        Assert.Equal(84f, HomeArtistRowLayout.PodWidth(76f));
        Assert.Equal(HomeArtistRowLayout.MinPodWidth, HomeArtistRowLayout.PodWidth(23f));   // 23+8=31 < the 40 floor
        Assert.Equal(40f, HomeArtistRowLayout.PodWidth(32f));   // exactly at the floor
    }

    [Theory]
    [InlineData(1f, 2)]
    [InlineData(0.85f, 2)]
    [InlineData(0.84f, 1)]
    [InlineData(0.65f, 1)]
    [InlineData(0.5f, 0)]
    public void LabelLines_ShrinksThenHidesTheName(float scale, int expected)
        => Assert.Equal(expected, HomeArtistRowLayout.LabelLines(scale));

    [Theory]
    [InlineData(1f, 20f)]
    [InlineData(0.8f, 20f)]
    [InlineData(0.7f, 16f)]
    [InlineData(0.5f, 16f)]
    public void BadgeSize_ShrinksUnderPressure(float scale, float expected)
        => Assert.Equal(expected, HomeArtistRowLayout.BadgeSize(scale));

    [Fact]
    public void SlotHeight_IsRankOneSArtSize()
        => Assert.Equal(HomeArtistRowLayout.ArtSize(0, 1.2f), HomeArtistRowLayout.SlotHeight(1.2f));

    /// <summary>The whole guarantee behind "never wraps": for a range of realistic content widths, the scale the row
    /// would actually compute (mirroring HomeModules.Artists' own arithmetic) lays out ten pods, at that scale, into
    /// AT MOST ONE ROW's worth of overflow beyond the content width — RampScaleFor solves its scale against the
    /// ramp's own AVERAGE box width (chrome included in the average, but each pod's own +8 chrome does not itself
    /// shrink with the art), so a very narrow row can run the last pod a few DIP past the edge; ClipToBounds on the
    /// podium (HomeModules.Artists) takes it from there. What must never happen is a SECOND line — this only checks
    /// that no width, however narrow, produces an overflow anywhere near a whole extra pod's worth.</summary>
    [Theory]
    [InlineData(520f)]
    [InlineData(640f)]
    [InlineData(800f)]
    [InlineData(1000f)]
    public void TenPodsNeverOverflowByMoreThanOnePodWidth(float contentW)
    {
        const int count = 10;
        const float podChrome = 8f;   // Spacing.S
        const float gap = 8f;
        float podiumContentW = MathF.Max(0f, contentW - 2f * 16f);   // 2*Spacing.M
        float fittedColumnW = (podiumContentW - (count - 1) * gap) / count;
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnW, count, podChrome);

        float total = 0f;
        for (int i = 0; i < count; i++)
            total += HomeArtistRowLayout.PodWidth(HomeArtistRowLayout.ArtSize(i, scale));
        total += (count - 1) * gap;
        Assert.True(total <= podiumContentW + HomeArtistRowLayout.MinPodWidth,
            $"contentW={contentW}: ten pods at scale {scale} take {total}, content is only {podiumContentW}");
    }

    [Fact]
    public void TenPodsFitExactlyOnceTheRowIsComfortablyWide()
    {
        // Above the ramp's own average box width (~59.8 DIP/pod at scale 1), the scaled footprint fits with room to
        // spare — no clipping at all.
        const int count = 10;
        const float podChrome = 8f;
        const float gap = 8f;
        const float contentW = 1000f;
        float podiumContentW = contentW - 2f * 16f;
        float fittedColumnW = (podiumContentW - (count - 1) * gap) / count;
        float scale = HomeArtistRowLayout.RampScaleFor(fittedColumnW, count, podChrome);

        float total = 0f;
        for (int i = 0; i < count; i++)
            total += HomeArtistRowLayout.PodWidth(HomeArtistRowLayout.ArtSize(i, scale));
        total += (count - 1) * gap;
        Assert.True(total <= podiumContentW);
    }

    [Fact]
    public void ModuleGap_IsThisRowsOwnTierBoundary_NotTheSharedHelpers()
    {
        Assert.Equal(32f, HomeArtistRowLayout.ModuleGap(1080f));
        Assert.Equal(24f, HomeArtistRowLayout.ModuleGap(1079f));
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
}
