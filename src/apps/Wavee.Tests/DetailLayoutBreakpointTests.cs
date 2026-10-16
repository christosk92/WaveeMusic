// ── Wavee.Tests/DetailLayoutBreakpointTests.cs — the tier and mode ladders and their hysteresis ────────────────────
//
// Ported from _old/Wavee.Tests/DetailLayoutBreakpointTests.cs onto `Detail.Breakpoints`, then (TC5) onto Classic's detail sub-ladders
// (`ClassicDetailTiers.ModeFor` / `TierFor` over `Themes/Classic/Structure/Rules/Tiers.Detail.cs`'s `TierLadder`s): every hold assertion is
// 0.2.9's and still holds; the one deliberate change is the vertical exit, which now takes the widening holds of the edges it crosses
// (`ModeFor_VerticalExit_TakesTheHoldsOfTheEdgesItCrosses`).

using Wavee.Themes.Classic;
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
        Assert.Equal(Breakpoints.VerticalMode, ClassicDetailTiers.ModeFor(360f, 0, initialized: false));
        Assert.Equal(4, ClassicDetailTiers.TierFor(360f, 0, initialized: false));
        // …and the hero seeds STACKED at that width, which is the only flow decision left.
        Assert.False(VerticalLayout.RowFlow(360f));
    }

    [Fact]
    public void TierFor_Oscillates860PlusMinus24_HoldsTier1Until884()
    {
        int tier = ClassicDetailTiers.TierFor(850f, 1);
        Assert.Equal(1, tier);

        tier = ClassicDetailTiers.TierFor(836f, tier);
        Assert.Equal(1, tier);   // 836 nominal tier 1 — hold while prev is 1

        tier = ClassicDetailTiers.TierFor(884f, tier);
        Assert.Equal(0, tier);   // widen back only after w - 24 crosses 860

        tier = ClassicDetailTiers.TierFor(835f, tier);
        Assert.Equal(1, tier);   // narrow from tier 0 drops immediately at 860 boundary
    }

    [Fact]
    public void TierFor_FirstMeasure_TakesNominalTierWithoutHysteresis()
    {
        // Pre-measure, `prev` is a construction default / a viewport seed — the first real width wins outright.
        Assert.Equal(0, ClassicDetailTiers.TierFor(870f, previous: 1, initialized: false));
        Assert.Equal(3, ClassicDetailTiers.TierFor(500f, previous: 0, initialized: false));
        // Hysteresis engages from the SECOND measure on (the default is the measured case).
        Assert.Equal(1, ClassicDetailTiers.TierFor(870f, previous: 1, initialized: true));
        // A zero/absent width never overrides the caller's current tier, measured or not.
        Assert.Equal(2, ClassicDetailTiers.TierFor(0f, previous: 2, initialized: false));
    }

    [Fact]
    public void TierFor_MultiTierJump_WidensImmediately()
    {
        int tier = ClassicDetailTiers.TierFor(500f, 5);
        Assert.Equal(3, tier);

        tier = ClassicDetailTiers.TierFor(900f, tier);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void NominalTierFor_Tier6_BelowThreshold()
    {
        Assert.Equal(4, ClassicDetailTiers.TierFor(340f, 0, initialized: false));   // boundary sanity
        Assert.Equal(5, ClassicDetailTiers.TierFor(300f, 0, initialized: false));   // ≥300 → 5
        Assert.Equal(6, ClassicDetailTiers.TierFor(299f, 0, initialized: false));   // < 300 → ultra-compact 6
    }

    [Fact]
    public void TierFor_NarrowsToTier6Immediately_WidensAfterHysteresis()
    {
        int tier = ClassicDetailTiers.TierFor(310f, 5);
        Assert.Equal(5, tier);

        tier = ClassicDetailTiers.TierFor(290f, tier);
        Assert.Equal(6, tier);   // narrowing to a narrower tier applies immediately

        tier = ClassicDetailTiers.TierFor(310f, tier);
        Assert.Equal(6, tier);   // hold tier 6 until w - 24 crosses 300

        tier = ClassicDetailTiers.TierFor(324f, tier);
        Assert.Equal(5, tier);   // widen back to 5 once w - 24 ≥ 300
    }

    [Fact]
    public void ModeFor_Oscillates820PlusMinus24_HoldsMidUntil844()
    {
        int mode = ClassicDetailTiers.ModeFor(810f, 1, initialized: true);
        Assert.Equal(1, mode);

        mode = ClassicDetailTiers.ModeFor(796f, mode, initialized: true);
        Assert.Equal(1, mode);   // still nominal mid while prev is 1

        mode = ClassicDetailTiers.ModeFor(844f, mode, initialized: true);
        Assert.Equal(0, mode);   // widen to wide only after w - 24 crosses 820

        mode = ClassicDetailTiers.ModeFor(795f, 0, initialized: true);
        Assert.Equal(1, mode);   // narrow from wide drops at 820 boundary
    }

    [Fact]
    public void ModeFor_VerticalBand540580_Unchanged()
    {
        int mode = ClassicDetailTiers.ModeFor(600f, 2, initialized: true);
        Assert.Equal(2, mode);

        mode = ClassicDetailTiers.ModeFor(530f, mode, initialized: true);
        Assert.Equal(Breakpoints.VerticalMode, mode);

        mode = ClassicDetailTiers.ModeFor(570f, mode, initialized: true);
        Assert.Equal(Breakpoints.VerticalMode, mode);

        mode = ClassicDetailTiers.ModeFor(590f, mode, initialized: true);
        Assert.Equal(2, mode);
    }

    [Fact]
    public void ModeFor_VerticalExit_TakesTheHoldsOfTheEdgesItCrosses()
    {
        // The old core ladder left the vertical arm for the NOMINAL mode (830 → the wide arm). Through the ladder an exit is a widening like
        // any other: 830 is past 580 and 660 + 24 but not 820 + 24, so it lands in mode 1; 844 reaches the wide arm.
        Assert.Equal(1, ClassicDetailTiers.ModeFor(830f, Breakpoints.VerticalMode));
        Assert.Equal(0, ClassicDetailTiers.ModeFor(844f, Breakpoints.VerticalMode));
        Assert.Equal(2, ClassicDetailTiers.ModeFor(670f, Breakpoints.VerticalMode));
        // A fresh resolve (a launch, a navigation) uses the edges themselves.
        Assert.Equal(0, ClassicDetailTiers.ModeFor(830f, Breakpoints.VerticalMode, initialized: false));
    }
}
