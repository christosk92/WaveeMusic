using System;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// The deck family's easing curves (docs/plans/wavee/npv-player-styles-implementation.md, Part 3). A cubic Bezier
/// is PARAMETRIC — y is not a closed form of x — so the implementation solves X(t) = x by Newton and only then
/// evaluates Y(t). These tests pin that solve against curves whose answer is known analytically, and pin the four
/// named curves' shape (endpoints, monotonicity, and where each one spends its travel).
/// </summary>
public class DeckEaseTests
{
    // With control abscissae at 1/3 and 2/3 the x-polynomial collapses to X(t) = t exactly, so the curve's y for a
    // given x is just the y-polynomial — here the classic smoothstep 3x^2 - 2x^3. An exact oracle for the solver.
    static float Smoothstep(float x) => 3f * x * x - 2f * x * x * x;

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(0.4f)]
    [InlineData(0.5f)]
    [InlineData(0.6f)]
    [InlineData(0.75f)]
    [InlineData(0.9f)]
    [InlineData(0.99f)]
    public void CubicBezier_SolvesTheParametricCurve_AgainstAnExactOracle(float x)
        => Assert.Equal(Smoothstep(x), DeckEase.CubicBezier(1f / 3f, 0f, 2f / 3f, 1f, x), 3);

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    public void CubicBezier_TheIdentityCurve_IsTheIdentity(float x)
        => Assert.Equal(x, DeckEase.CubicBezier(0.25f, 0.25f, 0.75f, 0.75f, x), 4);

    [Fact]
    public void CubicBezier_ConvergesWhereTheTangentIsFlatAtBothEnds()
    {
        // x1 = 0 and x2 = 1 give X'(0) = X'(1) = 0: the case that stalls a naive Newton and needs the bisection
        // fallback. The curve is symmetric, so the midpoint is exactly 0.5.
        Assert.Equal(0.5f, DeckEase.CubicBezier(0f, 0.5f, 1f, 0.5f, 0.5f), 3);
        Assert.Equal(0f, DeckEase.CubicBezier(0f, 0.5f, 1f, 0.5f, 0f), 4);
        Assert.Equal(1f, DeckEase.CubicBezier(0f, 0.5f, 1f, 0.5f, 1f), 4);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void CubicBezier_AtOrBelowZero_IsZero(float x)
        => Assert.Equal(0f, DeckEase.CubicBezier(0.4f, 0f, 0.2f, 1f, x));

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void CubicBezier_AtOrAboveOne_IsOne(float x)
        => Assert.Equal(1f, DeckEase.CubicBezier(0.4f, 0f, 0.2f, 1f, x));

    [Fact]
    public void CubicBezier_NaN_DoesNotEscape()
        => Assert.Equal(0f, DeckEase.CubicBezier(0.4f, 0f, 0.2f, 1f, float.NaN));

    // ── the four named curves ──────────────────────────────────────────────────────────────────────────────────

    static float Curve(string name, float t) => name switch
    {
        "std" => DeckEase.Std(t),
        "liftUp" => DeckEase.LiftUp(t),
        "damped" => DeckEase.Damped(t),
        "slideOut" => DeckEase.SlideOut(t),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown deck curve"),
    };

    [Theory]
    [InlineData("std")]
    [InlineData("liftUp")]
    [InlineData("damped")]
    [InlineData("slideOut")]
    public void NamedCurves_StartAtZeroAndEndAtOne(string name)
    {
        Assert.Equal(0f, Curve(name, 0f), 4);
        Assert.Equal(1f, Curve(name, 1f), 4);
    }

    [Theory]
    [InlineData("std")]
    [InlineData("liftUp")]
    [InlineData("damped")]
    [InlineData("slideOut")]
    public void NamedCurves_AreMonotoneAndStayInRange(string name)
    {
        float prev = 0f;
        for (int i = 0; i <= 100; i++)
        {
            float v = Curve(name, i / 100f);
            Assert.InRange(v, -1e-4f, 1f + 1e-4f);
            Assert.True(v >= prev - 1e-4f, $"{name} went backwards at t={i / 100f}: {prev} -> {v}");
            prev = v;
        }
    }

    [Fact]
    public void Std_IsTheSymmetricSwing()
    {
        // cubic-bezier(.4,0,.2,1) reaches its own halfway point at x = 0.35 (the x-polynomial's exact root for
        // t = 0.5) — the accelerate-then-settle asymmetry the arm's swings are authored around.
        Assert.Equal(0.5f, DeckEase.Std(0.35f), 3);
    }

    [Fact]
    public void LiftUp_LeavesImmediately()
    {
        // The cue lever is a solenoid: a quarter of the way through, the arm is already most of the way up.
        Assert.True(DeckEase.LiftUp(0.25f) > 0.5f, $"liftUp(.25) = {DeckEase.LiftUp(0.25f)}");
    }

    [Fact]
    public void Damped_DropsMostOfTheWayThenCreeps()
    {
        // Silicone damping: fast at first, then a long slow settle — half the time buys well over 80% of the travel,
        // and the second half of the time is a creep over the remainder (the needle lands, it does not slam).
        Assert.True(DeckEase.Damped(0.25f) > 0.35f, $"damped(.25) = {DeckEase.Damped(0.25f)}");
        Assert.True(DeckEase.Damped(0.5f) > 0.8f, $"damped(.5) = {DeckEase.Damped(0.5f)}");
        Assert.True(DeckEase.Damped(0.5f) < 0.97f, $"damped(.5) = {DeckEase.Damped(0.5f)}");
    }

    [Fact]
    public void SlideOut_IsFrontLoaded()
        => Assert.True(DeckEase.SlideOut(0.25f) > 0.35f, $"slideOut(.25) = {DeckEase.SlideOut(0.25f)}");

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.42f, 0.42f)]
    [InlineData(1f, 1f)]
    [InlineData(1.4f, 1f)]
    public void Clamp01_Clamps(float v, float expected) => Assert.Equal(expected, DeckEase.Clamp01(v));
}
