using System;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// The motor behind every rotating deck part (docs/plans/wavee/npv-player-styles-implementation.md, Part 3):
/// a first-order velocity lag with separate rise/fall time constants. The record platter's numbers are the
/// SL-1200's — 0.7 s to 33 1/3 (3 x tau-up 0.23 s) and a ~1.6 s electronic brake (3 x tau-down 0.53 s) — which is
/// exactly why the two constants cannot be one.
/// </summary>
public class SpinIntegratorTests
{
    const float Tick = 1f / 30f;                 // the deck ticker's cadence
    const float Rpm33 = 33.333f * 6f;            // 200 deg/s
    const float Rpm45 = 45f * 6f;                // 270 deg/s

    static SpinIntegrator Platter() => new(0.23f, 0.53f);

    static void Run(ref SpinIntegrator s, float target, float seconds)
    {
        int steps = (int)MathF.Round(seconds / Tick);
        for (int i = 0; i < steps; i++) s.Step(target, Tick);
    }

    [Fact]
    public void SpinUp_Reaches95PercentOf33InSevenHundredMs()
    {
        var s = Platter();
        Run(ref s, Rpm33, 0.700f);
        Assert.True(s.Omega >= 0.95f * Rpm33, $"omega={s.Omega} of {Rpm33}");
        Assert.True(s.Omega < Rpm33, "a first-order lag never overshoots its target");
    }

    [Fact]
    public void SpinUp_IsNotInstant_AtTheFirstTick()
    {
        var s = Platter();
        s.Step(Rpm33, Tick);
        // One 33 ms tick is ~0.145 tau: a seventh of the way there, not all of it.
        Assert.InRange(s.Omega, 0.10f * Rpm33, 0.20f * Rpm33);
    }

    [Fact]
    public void SpinDown_Is95PercentBrakedInSixteenHundredMs()
    {
        var s = Platter();
        Run(ref s, Rpm33, 3f);              // fully up to speed first
        Run(ref s, 0f, 1.600f);
        Assert.True(s.Omega <= 0.05f * Rpm33, $"omega={s.Omega} of {Rpm33}");
    }

    [Fact]
    public void SpinDown_SettlesToAnExactZero_SoTheTickerCanStop()
    {
        var s = Platter();
        Run(ref s, Rpm33, 3f);
        Assert.False(s.AtRest);
        Run(ref s, 0f, 6f);
        Assert.Equal(0f, s.Omega);
        Assert.True(s.AtRest);
    }

    [Fact]
    public void SpinDown_UsesTheSlowerConstant_SoBrakingLagsSpinUp()
    {
        var up = Platter();
        Run(ref up, Rpm33, 0.5f);
        var down = Platter();
        down.Omega = Rpm33;
        Run(ref down, 0f, 0.5f);
        // After the same half second: spun UP past 88% but braked only DOWN to ~39% — the asymmetry is the point.
        Assert.True(up.Omega / Rpm33 > 1f - down.Omega / Rpm33, $"up={up.Omega} down={down.Omega}");
    }

    [Fact]
    public void SteadyState_IsTheTargetDegreesPerSecond()
    {
        var s = Platter();
        Run(ref s, Rpm45, 5f);
        Assert.Equal(Rpm45, s.Omega, 1);
    }

    [Fact]
    public void Angle_AlwaysWrapsIntoZeroToThreeSixty()
    {
        var s = Platter();
        for (int i = 0; i < 600; i++)
        {
            s.Step(Rpm45, Tick);
            Assert.InRange(s.Angle, 0f, 360f);
            Assert.True(s.Angle < 360f, $"angle={s.Angle}");
        }
    }

    [Fact]
    public void Angle_WrapsAtTheSeam_RatherThanGrowingForever()
    {
        var s = new SpinIntegrator(0.001f, 0.001f) { Angle = 359f, Omega = Rpm33 };
        s.Step(Rpm33, Tick);   // 200 deg/s for 1/30 s = 6.67 deg past the seam
        Assert.Equal(5.667f, s.Angle, 2);
    }

    [Fact]
    public void ReverseDirection_StillWrapsPositive()
    {
        var s = new SpinIntegrator(0.001f, 0.001f) { Angle = 0f, Omega = Rpm33 };
        s.Step(Rpm33, Tick, dir: -1);
        Assert.Equal(353.333f, s.Angle, 2);
    }

    [Fact]
    public void DefaultConstructed_HasNoLag_SoAnUnconfiguredPartStillTracks()
    {
        var s = default(SpinIntegrator);
        s.Step(Rpm33, Tick);
        Assert.Equal(Rpm33, s.Omega, 3);
    }

    [Fact]
    public void RestEpsilon_IsWhatMakesAtRestReachable()
    {
        var s = Platter();
        s.Omega = SpinIntegrator.RestEpsilonDegPerSec * 0.5f;
        s.Step(0f, Tick);
        Assert.True(s.AtRest);
    }
}
