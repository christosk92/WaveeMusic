using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins <see cref="WaveeLottieRecolor.Apply(ColorF, ColorF, ColorF)"/> — the setup wizard's Lottie hero
/// recolour rule (plan §7) — against a deliberately un-accent-like accent pair, so a test that accidentally left the
/// source colour unchanged would still fail.</summary>
// Tok is one process-global palette/theme; LightModeOverhaulTests.WithTheme swaps it mid-test, and the assertions here read it
// live (Tok.AccentDefault twice around a call). xunit runs classes in parallel, so those four classes serialise on one
// collection — the same discipline EntranceStaggerTests/MotionSystemTests use for the motion globals.
[Collection("wavee-tok-global")]
public class WaveeLottieRecolorTests
{
    // A green accent pair, chosen specifically because green's hue (~140°) is nowhere near the source #0078D4 blue
    // (~206°) or its #002B67 navy sibling (~215°) — a bug that leaves either untouched would still show up as a
    // colour mismatch rather than accidentally passing.
    static readonly ColorF Accent = ColorF.FromRgba(0x1E, 0xB9, 0x5A);
    static readonly ColorF AccentDeep = ColorF.FromRgba(0x0B, 0x5C, 0x28);

    static (float H, float S, float L) Hsl(ColorF c) => WaveePalette.ToHsl(c);

    [Fact]
    public void ExactSourceAccent_MapsToAccent_AlphaFromSource()
    {
        var c = ColorF.FromRgba(0x00, 0x78, 0xD4, 0x80);
        var got = WaveeLottieRecolor.Apply(c, Accent, AccentDeep);

        Assert.Equal(Accent.R, got.R, 3);
        Assert.Equal(Accent.G, got.G, 3);
        Assert.Equal(Accent.B, got.B, 3);
        Assert.Equal(c.A, got.A, 3);
    }

    [Fact]
    public void ExactNavy_MapsToAccentDeep_AlphaFromSource()
    {
        var c = ColorF.FromRgba(0x00, 0x2B, 0x67, 0x40);
        var got = WaveeLottieRecolor.Apply(c, Accent, AccentDeep);

        Assert.Equal(AccentDeep.R, got.R, 3);
        Assert.Equal(AccentDeep.G, got.G, 3);
        Assert.Equal(AccentDeep.B, got.B, 3);
        Assert.Equal(c.A, got.A, 3);
    }

    [Fact]
    public void HueBand_KeepsSaturationAndLightness_ButRotatesHue()
    {
        // The gradient mid-stops' own blue-violet (~0x2E5DD3-ish, well inside H 195-285/S>=.35) and not identical to
        // either exact source colour, so this exercises the hue-rotation branch specifically.
        var c = ColorF.FromRgba(0x2E, 0x5D, 0xD3);
        var (h, s, l) = Hsl(c);
        Assert.InRange(h, 195f, 285f);
        Assert.True(s >= 0.35f);

        var got = WaveeLottieRecolor.Apply(c, Accent, AccentDeep);
        var (gh, gs, gl) = Hsl(got);

        Assert.Equal(s, gs, 2);
        Assert.Equal(l, gl, 2);
        Assert.NotEqual(h, gh, 1);
    }

    [Fact]
    public void Neutrals_AreUntouched()
    {
        foreach (var c in new[]
        {
            ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            ColorF.FromRgba(0xEE, 0xEE, 0xEE),
            ColorF.FromRgba(0xF1, 0xF0, 0xEF),
            ColorF.FromRgba(0xE0, 0xDE, 0xDC),
            ColorF.FromRgba(0, 0, 0),
        })
            Assert.Equal(c, WaveeLottieRecolor.Apply(c, Accent, AccentDeep));
    }

    [Fact]
    public void Teal_OutsideTheHueBand_IsUntouched()
    {
        // The Patch asset's mini-square teal (0x96E9DC) sits at H ~ 165deg — below the 195deg band floor.
        var teal = ColorF.FromRgba(0x96, 0xE9, 0xDC);
        var (h, s, _) = Hsl(teal);
        Assert.True(h < 195f || s < 0.35f);

        Assert.Equal(teal, WaveeLottieRecolor.Apply(teal, Accent, AccentDeep));
    }

    [Fact]
    public void Alpha_IsAlwaysPreservedFromTheSource()
    {
        var exact = ColorF.FromRgba(0x00, 0x78, 0xD4, 0x33);
        Assert.Equal(exact.A, WaveeLottieRecolor.Apply(exact, Accent, AccentDeep).A);

        var band = ColorF.FromRgba(0x2E, 0x5D, 0xD3, 0x99);
        Assert.Equal(band.A, WaveeLottieRecolor.Apply(band, Accent, AccentDeep).A);

        var neutral = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xCC);
        Assert.Equal(neutral.A, WaveeLottieRecolor.Apply(neutral, Accent, AccentDeep).A);
    }

    [Fact]
    public void LiveOverload_ReadsCurrentTokens()
    {
        // The single-argument overload (the real WaveeLottie.Options seam) must delegate to the same rule as the
        // testable overload, using the engine's live accent tokens.
        var c = ColorF.FromRgba(0x00, 0x78, 0xD4);
        var expected = WaveeLottieRecolor.Apply(c, FluentGpu.Dsl.Tok.AccentDefault, FluentGpu.Dsl.Tok.AccentTextPrimary);
        Assert.Equal(expected, WaveeLottieRecolor.Apply(c));
    }
}
