using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailLayoutBreakpointTests
{
    [Fact]
    public void ContentWidthFloor_IsRemovedOnlyForVerticalMode()
    {
        Assert.Equal(0f, DetailLayoutBreakpoints.ContentMinWidthForMode(DetailLayoutBreakpoints.VerticalMode));
        Assert.Equal(300f, DetailLayoutBreakpoints.ContentMinWidthForMode(0));
        Assert.Equal(300f, DetailLayoutBreakpoints.ContentMinWidthForMode(2));
    }

    [Fact]
    public void FirstFrameAt360_SeedsVerticalCompactTierBeforeMeasurement()
    {
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode,
            DetailLayoutBreakpoints.InitialModeForViewport(360f));
        Assert.Equal(4, DetailLayoutBreakpoints.InitialTierForViewport(360f));
        // …and the hero seeds STACKED (artwork above the copy) at that width, which is the only flow decision left.
        Assert.False(DetailVerticalLayout.RowFlow(360f));
    }

    [Fact]
    public void TierFor_Oscillates860PlusMinus24_HoldsTier1Until884()
    {
        int tier = DetailLayoutBreakpoints.TierFor(850f, 1);
        Assert.Equal(1, tier);

        tier = DetailLayoutBreakpoints.TierFor(836f, tier);
        Assert.Equal(1, tier);   // 836 nominal tier 1 — hold while prev is 1

        tier = DetailLayoutBreakpoints.TierFor(884f, tier);
        Assert.Equal(0, tier);   // widen back only after w - 24 crosses 860

        tier = DetailLayoutBreakpoints.TierFor(835f, tier);
        Assert.Equal(1, tier);   // narrow from tier 0 drops immediately at 860 boundary
    }

    [Fact]
    public void TierFor_FirstMeasure_TakesNominalTierWithoutHysteresis()
    {
        // Pre-measure, `prev` is a construction default / a viewport seed — not a tier the user has seen — so the first
        // real width must win outright in BOTH directions. 884 would otherwise be needed to widen off a seeded tier 1,
        // and 836 would otherwise be held by the dip band.
        Assert.Equal(0, DetailLayoutBreakpoints.TierFor(870f, prev: 1, initialized: false));
        Assert.Equal(3, DetailLayoutBreakpoints.TierFor(500f, prev: 0, initialized: false));
        // Hysteresis engages from the SECOND measure on (the default is the measured case).
        Assert.Equal(1, DetailLayoutBreakpoints.TierFor(870f, prev: 1, initialized: true));
        // A zero/absent width never overrides the caller's current tier, measured or not.
        Assert.Equal(2, DetailLayoutBreakpoints.TierFor(0f, prev: 2, initialized: false));
    }

    [Fact]
    public void TierFor_MultiTierJump_WidensImmediately()
    {
        int tier = DetailLayoutBreakpoints.TierFor(500f, 5);
        Assert.Equal(3, tier);

        tier = DetailLayoutBreakpoints.TierFor(900f, tier);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void NominalTierFor_Tier6_BelowThreshold()
    {
        Assert.Equal(4, DetailLayoutBreakpoints.NominalTierFor(340f));   // boundary sanity
        Assert.Equal(5, DetailLayoutBreakpoints.NominalTierFor(300f));   // ≥300 → 5
        Assert.Equal(6, DetailLayoutBreakpoints.NominalTierFor(299f));   // < 300 → ultra-compact 6
    }

    [Fact]
    public void TierFor_NarrowsToTier6Immediately_WidensAfterHysteresis()
    {
        int tier = DetailLayoutBreakpoints.TierFor(310f, 5);
        Assert.Equal(5, tier);

        tier = DetailLayoutBreakpoints.TierFor(290f, tier);
        Assert.Equal(6, tier);   // narrowing to a narrower tier applies immediately

        tier = DetailLayoutBreakpoints.TierFor(310f, tier);
        Assert.Equal(6, tier);   // hold tier 6 until w - 24 crosses 300

        tier = DetailLayoutBreakpoints.TierFor(324f, tier);
        Assert.Equal(5, tier);   // widen back to 5 once w - 24 ≥ 300
    }

    [Fact]
    public void ModeFor_Oscillates820PlusMinus24_HoldsMidUntil844()
    {
        int mode = DetailLayoutBreakpoints.ModeFor(810f, 1, initialized: true);
        Assert.Equal(1, mode);

        mode = DetailLayoutBreakpoints.ModeFor(796f, mode, initialized: true);
        Assert.Equal(1, mode);   // still nominal mid while prev is 1

        mode = DetailLayoutBreakpoints.ModeFor(844f, mode, initialized: true);
        Assert.Equal(0, mode);   // widen to wide only after w - 24 crosses 820

        mode = DetailLayoutBreakpoints.ModeFor(795f, 0, initialized: true);
        Assert.Equal(1, mode);   // narrow from wide drops at 820 boundary
    }

    [Fact]
    public void ModeFor_VerticalBand540580_Unchanged()
    {
        int mode = DetailLayoutBreakpoints.ModeFor(600f, 2, initialized: true);
        Assert.Equal(2, mode);

        mode = DetailLayoutBreakpoints.ModeFor(530f, mode, initialized: true);
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode, mode);

        mode = DetailLayoutBreakpoints.ModeFor(570f, mode, initialized: true);
        Assert.Equal(DetailLayoutBreakpoints.VerticalMode, mode);

        mode = DetailLayoutBreakpoints.ModeFor(590f, mode, initialized: true);
        Assert.Equal(2, mode);
    }
}
