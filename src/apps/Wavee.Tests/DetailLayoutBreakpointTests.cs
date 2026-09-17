// ── Wavee.Tests/DetailLayoutBreakpointTests.cs — the tier and mode ladders and their hysteresis ────────────────────
//
// Ported from _old/Wavee.Tests/DetailLayoutBreakpointTests.cs onto `Detail.Breakpoints` (Entities/Detail.cs). Every
// assertion is 0.2.9's; only the call shape changed.

using Xunit;
using Breakpoints = Wavee.Detail.Breakpoints;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailLayoutBreakpointTests
{
    [Fact]
    public void ContentWidthFloor_IsRemovedOnlyForVerticalMode()
    {
        Assert.Equal(0f, Breakpoints.ContentMinWidthForMode(Breakpoints.VerticalMode));
        Assert.Equal(300f, Breakpoints.ContentMinWidthForMode(0));
        Assert.Equal(300f, Breakpoints.ContentMinWidthForMode(2));
    }

    [Fact]
    public void FirstFrameAt360_SeedsVerticalCompactTierBeforeMeasurement()
    {
        Assert.Equal(Breakpoints.VerticalMode, Breakpoints.InitialModeForViewport(360f));
        Assert.Equal(4, Breakpoints.InitialTierForViewport(360f));
        // …and the hero seeds STACKED at that width, which is the only flow decision left.
        Assert.False(VerticalLayout.RowFlow(360f));
    }

    [Fact]
    public void TierFor_Oscillates860PlusMinus24_HoldsTier1Until884()
    {
        int tier = Breakpoints.TierFor(850f, 1);
        Assert.Equal(1, tier);

        tier = Breakpoints.TierFor(836f, tier);
        Assert.Equal(1, tier);   // 836 nominal tier 1 — hold while prev is 1

        tier = Breakpoints.TierFor(884f, tier);
        Assert.Equal(0, tier);   // widen back only after w - 24 crosses 860

        tier = Breakpoints.TierFor(835f, tier);
        Assert.Equal(1, tier);   // narrow from tier 0 drops immediately at 860 boundary
    }

    [Fact]
    public void TierFor_FirstMeasure_TakesNominalTierWithoutHysteresis()
    {
        // Pre-measure, `prev` is a construction default / a viewport seed — the first real width wins outright.
        Assert.Equal(0, Breakpoints.TierFor(870f, prev: 1, initialized: false));
        Assert.Equal(3, Breakpoints.TierFor(500f, prev: 0, initialized: false));
        // Hysteresis engages from the SECOND measure on (the default is the measured case).
        Assert.Equal(1, Breakpoints.TierFor(870f, prev: 1, initialized: true));
        // A zero/absent width never overrides the caller's current tier, measured or not.
        Assert.Equal(2, Breakpoints.TierFor(0f, prev: 2, initialized: false));
    }

    [Fact]
    public void TierFor_MultiTierJump_WidensImmediately()
    {
        int tier = Breakpoints.TierFor(500f, 5);
        Assert.Equal(3, tier);

        tier = Breakpoints.TierFor(900f, tier);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void NominalTierFor_Tier6_BelowThreshold()
    {
        Assert.Equal(4, Breakpoints.NominalTierFor(340f));   // boundary sanity
        Assert.Equal(5, Breakpoints.NominalTierFor(300f));   // ≥300 → 5
        Assert.Equal(6, Breakpoints.NominalTierFor(299f));   // < 300 → ultra-compact 6
    }

    [Fact]
    public void TierFor_NarrowsToTier6Immediately_WidensAfterHysteresis()
    {
        int tier = Breakpoints.TierFor(310f, 5);
        Assert.Equal(5, tier);

        tier = Breakpoints.TierFor(290f, tier);
        Assert.Equal(6, tier);   // narrowing to a narrower tier applies immediately

        tier = Breakpoints.TierFor(310f, tier);
        Assert.Equal(6, tier);   // hold tier 6 until w - 24 crosses 300

        tier = Breakpoints.TierFor(324f, tier);
        Assert.Equal(5, tier);   // widen back to 5 once w - 24 ≥ 300
    }

    [Fact]
    public void ModeFor_Oscillates820PlusMinus24_HoldsMidUntil844()
    {
        int mode = Breakpoints.ModeFor(810f, 1, initialized: true);
        Assert.Equal(1, mode);

        mode = Breakpoints.ModeFor(796f, mode, initialized: true);
        Assert.Equal(1, mode);   // still nominal mid while prev is 1

        mode = Breakpoints.ModeFor(844f, mode, initialized: true);
        Assert.Equal(0, mode);   // widen to wide only after w - 24 crosses 820

        mode = Breakpoints.ModeFor(795f, 0, initialized: true);
        Assert.Equal(1, mode);   // narrow from wide drops at 820 boundary
    }

    [Fact]
    public void ModeFor_VerticalBand540580_Unchanged()
    {
        int mode = Breakpoints.ModeFor(600f, 2, initialized: true);
        Assert.Equal(2, mode);

        mode = Breakpoints.ModeFor(530f, mode, initialized: true);
        Assert.Equal(Breakpoints.VerticalMode, mode);

        mode = Breakpoints.ModeFor(570f, mode, initialized: true);
        Assert.Equal(Breakpoints.VerticalMode, mode);

        mode = Breakpoints.ModeFor(590f, mode, initialized: true);
        Assert.Equal(2, mode);
    }
}
