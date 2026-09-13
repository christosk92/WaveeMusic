using System;
using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wave 2 of the design-system convergence: the MOTION gates. Hover/press motion everywhere is a deliberate
/// Wavee identity and is kept — but the audit found it authored as 69 call sites across 20 distinct scale values
/// (1.005 → 1.16 hover, 0.625 → 0.997 press), with only about five of them honouring reduced motion. These tests pin
/// the SYSTEM that replaced it:
/// <list type="bullet">
///   <item>exactly three interaction tiers, monotonically ordered, one press value per tier;</item>
///   <item>every tier accessor collapses to 1f under reduced motion — the property no call site can forget;</item>
///   <item>the duration ladder is the WinUI Common_themeresources ladder and nothing else.</item>
/// </list>
/// <para>Shares a collection with <c>EntranceStaggerTests</c>: both mutate the process-wide
/// <c>Motion.ReducedMotion</c>, so they must not run concurrently.</para></summary>
[Collection("wavee-motion-global")]
public class MotionSystemTests
{
    /// <summary>The three tiers by name, so a new tier cannot be added without landing in every gate below.</summary>
    public static TheoryData<string, float, float> Tiers() => new()
    {
        // name        hover   press
        { "Subtle",    1.02f,  0.98f },
        { "Standard",  1.04f,  0.96f },
        { "Emphatic",  1.07f,  0.92f },
    };

    static ScaleTier Tier(string name) => name switch
    {
        "Subtle" => WaveeMotion.ScaleSubtle,
        "Standard" => WaveeMotion.ScaleStandard,
        "Emphatic" => WaveeMotion.ScaleEmphatic,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown tier"),
    };

    // ── The tiers ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The values themselves. Standard's 1.04 is the value <c>WaveeCta</c>'s media pill contributed (it was
    /// the app's only already-systematic pair); its press deepened from that skin's local 0.97 to the ladder's 0.96 so
    /// there is exactly ONE press value per tier.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_CarriesItsPinnedPair(string name, float hover, float press)
    {
        var t = Tier(name);
        Assert.Equal(hover, t.HoverTarget);
        Assert.Equal(press, t.PressTarget);
    }

    /// <summary>A tier grows on hover and shrinks on press — never the reverse, never a no-op. A tier that resolved to
    /// 1f in either direction is a dead cue, which is exactly the <c>1f : 1f</c> defect the register logs as D4.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_GrowsOnHoverAndShrinksOnPress(string name, float hover, float press)
    {
        _ = hover; _ = press;
        var t = Tier(name);
        Assert.True(t.HoverTarget > 1f, $"{name} hover {t.HoverTarget} does not grow");
        Assert.True(t.PressTarget < 1f, $"{name} press {t.PressTarget} does not shrink");
    }

    /// <summary>The ladder is monotonic in BOTH directions: louder tier ⇒ more hover growth AND more press push. Three
    /// rungs, strictly ordered — so "which tier is louder" is answerable without reading the values.</summary>
    [Fact]
    public void TheThreeTiers_AreAStrictlyOrderedLadder()
    {
        var s = WaveeMotion.ScaleSubtle;
        var m = WaveeMotion.ScaleStandard;
        var e = WaveeMotion.ScaleEmphatic;

        Assert.True(s.HoverTarget < m.HoverTarget, "Subtle must grow less than Standard");
        Assert.True(m.HoverTarget < e.HoverTarget, "Standard must grow less than Emphatic");
        Assert.True(s.PressTarget > m.PressTarget, "Subtle must push less than Standard");
        Assert.True(m.PressTarget > e.PressTarget, "Standard must push less than Emphatic");

        // Sub-perceptual rungs are what the sweep DELETED (the 1.005/0.997 tour banner): every rung must clear the
        // recorder's own cull threshold (SceneRecorder skips the transform below |scale-1| = 0.0008) by a wide margin.
        foreach (var t in new[] { s, m, e })
        {
            Assert.True(MathF.Abs(t.HoverTarget - 1f) > 0.01f, "a tier below 1% is sub-perceptual, not a tier");
            Assert.True(MathF.Abs(t.PressTarget - 1f) > 0.01f, "a tier below 1% is sub-perceptual, not a tier");
        }
    }

    // ── Reduced motion ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE gate this wave exists for. The engine seeds hover/press progress through
    /// <c>AnimScheduler.SeedEased</c>, which — unlike <c>SeedMotion</c>/<c>KeyframesMotion</c> — carries no
    /// <c>ReducedMotionPolicy</c>, and <c>SceneRecorder</c> composites <c>1 + (HoverScale-1)·HoverT</c>
    /// unconditionally. So the interaction scale is the one animated channel the engine does NOT suppress, and the
    /// suppression has to be a property of the app's authored VALUE. Reading it in the accessor (never at the call
    /// site) is what makes it unforgettable — and returning exactly 1f is what makes the recorder skip the transform
    /// rather than animate to a visually-identical one.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void EveryTier_CollapsesToIdentity_UnderReducedMotion(string name, float hover, float press)
    {
        _ = hover; _ = press;
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            var t = Tier(name);
            Assert.Equal(t.HoverTarget, t.Hover);
            Assert.Equal(t.PressTarget, t.Press);

            Motion.ReducedMotion = true;
            Assert.Equal(1f, t.Hover);
            Assert.Equal(1f, t.Press);
            Assert.Equal(1f, t.HoverIf(true));
            Assert.Equal(1f, t.PressIf(true));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    /// <summary>The gated overloads: a dead affordance (a disabled transport button, an unavailable filter chip) must
    /// not answer the pointer at all, and must do so without a second reduced-motion read at the call site.</summary>
    [Theory]
    [MemberData(nameof(Tiers))]
    public void GatedAccessors_AreIdentityWhenDisabled(string name, float hover, float press)
    {
        bool saved = Motion.ReducedMotion;
        try
        {
            Motion.ReducedMotion = false;
            var t = Tier(name);
            Assert.Equal(1f, t.HoverIf(false));
            Assert.Equal(1f, t.PressIf(false));
            Assert.Equal(hover, t.HoverIf(true));
            Assert.Equal(press, t.PressIf(true));
        }
        finally { Motion.ReducedMotion = saved; }
    }

    // ── The duration ladder ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three rungs, the real WinUI Common_themeresources durations, strictly ordered. The sweep snapped
    /// 100/120/140/150/180 ms of hand-picked interaction timing onto them; anything that needs a different number is a
    /// STRUCTURAL transition (a page/pane/flyout tween) and deliberately does not live on this ladder.</summary>
    [Fact]
    public void DurationLadder_IsTheWinUiLadder()
    {
        Assert.Equal(83f, WaveeMotion.Faster);
        Assert.Equal(167f, WaveeMotion.Fast);
        Assert.Equal(250f, WaveeMotion.Standard);
        Assert.True(WaveeMotion.Faster < WaveeMotion.Fast);
        Assert.True(WaveeMotion.Fast < WaveeMotion.Standard);

        // Faster is the engine's own brush-transition duration — the app must not mint a second value for it.
        Assert.Equal(Motion.ControlFaster, WaveeMotion.Faster);
        Assert.Equal(Motion.ControlFast, WaveeMotion.Fast);
        Assert.Equal(Motion.ControlNormal, WaveeMotion.Standard);
    }

    /// <summary>The stagger rung — declared here in Wave 2, wired in Wave 5 through <c>WaveeEntrance</c> (the ladder,
    /// the cap and the reduced-motion collapse are pinned by <c>EntranceStaggerTests</c>). Pinned so the value
    /// is decided once, here, rather than re-picked at each entrance.</summary>
    [Fact]
    public void StaggerRung_IsOneDecidedValue() => Assert.Equal(40f, WaveeMotion.StaggerMs);
}
