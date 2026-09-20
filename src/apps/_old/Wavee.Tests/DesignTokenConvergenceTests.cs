using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>The design-system convergence gates. The app was found bypassing its own correct tokens roughly three
/// times for every time it used them — 13/17 titles, 11/14 captions, 3-and-5-and-6 radii, 36 hand-rolled on-media
/// colours — and the fix is only durable if the RULES are executable. These tests pin the rules, not the pixels:
/// <list type="bullet">
///   <item>every semantic type alias resolves a size AND a line height AND an allowed weight;</item>
///   <item>no alias carries an off-ramp size, except the three sanctioned display-face identity aliases;</item>
///   <item>the on-media ladder is ONE ladder, derived where the engine already owns the value;</item>
///   <item>the shared geometry constants (section rhythm, page measure, thumbnail ladder) are what the pages read.</item>
/// </list></summary>
public class DesignTokenConvergenceTests
{
    const float Eps = 0.005f;

    static void Near(float expected, float actual, string what) =>
        Assert.True(MathF.Abs(expected - actual) <= Eps, $"{what}: expected ≈{expected}, got {actual}");

    // The engine's WinUI type ramp (Dsl/Typography.cs): the ONLY size/line-height pairs an alias may resolve.
    static readonly (float Size, float LineHeight)[] Ramp =
    [
        (12f, 16f),   // Caption
        (14f, 20f),   // Body / BodyStrong
        (18f, 24f),   // BodyLarge
        (20f, 28f),   // Subtitle
        (28f, 36f),   // Title
        (40f, 52f),   // TitleLarge
        (68f, 92f),   // Display
    ];

    public static TheoryData<string, float, float, ushort> OnRampAliases() => new()
    {
        // name                size   line   weight
        { "TrackTitle",        14f,   20f,   600 },
        { "CardTitle",         14f,   20f,   600 },
        { "TrackMeta",         12f,   16f,   400 },
        { "Eyebrow",           12f,   16f,   600 },
        { "RailHeader",        20f,   28f,   600 },
        { "ModuleHeader",      20f,   28f,   600 },
        { "PageHero",          28f,   36f,   600 },
        // The library-surface masthead: one ramp rung above PageHero, in the display face at the LIGHT weight. It is on
        // the ramp on purpose — a fourth "sanctioned divergence" is exactly what this file exists to prevent.
        { "SurfaceDisplay",    40f,   52f,   400 },
        { "NowPlayingTitle",   20f,   28f,   600 },
    };

    static TextEl Alias(string name) => name switch
    {
        "TrackTitle" => WaveeType.TrackTitle("x"),
        "CardTitle" => WaveeType.CardTitle("x"),
        "TrackMeta" => WaveeType.TrackMeta("x"),
        "Eyebrow" => WaveeType.Eyebrow("x"),
        "RailHeader" => WaveeType.RailHeader("x"),
        "ModuleHeader" => WaveeType.ModuleHeader("x"),
        "PageHero" => WaveeType.PageHero("x"),
        "SurfaceDisplay" => WaveeType.SurfaceDisplay("x"),
        "NowPlayingTitle" => WaveeType.NowPlayingTitle("x"),
        "ArtistDisplay" => WaveeType.ArtistDisplay("x"),
        "ArtistTitle" => WaveeType.ArtistTitle("x"),
        "ArtistCompactTitle" => WaveeType.ArtistCompactTitle("x"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown alias"),
    };

    /// <summary>Line-height coverage was 10% before this wave — which IS the vertical-rhythm defect, because a bare
    /// <c>with { Size = 13f }</c> keeps whatever line box the previous rung published. Every alias must therefore carry
    /// the complete triple, so repointing a call site at one brings the line height with it.</summary>
    [Theory]
    [MemberData(nameof(OnRampAliases))]
    public void EveryAlias_ResolvesSizeLineHeightAndWeight(string name, float size, float line, ushort weight)
    {
        var el = Alias(name);
        Assert.False(float.IsNaN(el.LineHeight), $"{name} publishes no line height");
        Assert.Equal(size, el.Size);
        Assert.Equal(line, el.LineHeight);
        Assert.Equal(weight, el.ResolvedWeight);
    }

    /// <summary>No alias may carry an off-ramp size. This is the gate that stops the next 13/17 from being minted as a
    /// reusable alias rather than caught as a one-off literal.</summary>
    [Theory]
    [MemberData(nameof(OnRampAliases))]
    public void EveryAlias_LandsOnTheEngineRamp(string name, float size, float line, ushort weight)
    {
        _ = size; _ = line; _ = weight;
        var el = Alias(name);
        Assert.Contains(Ramp, r => r.Size == el.Size && r.LineHeight == el.LineHeight);
    }

    /// <summary>Weight policy: 400 and 600 only, everywhere except the sanctioned display-face divergence below.</summary>
    [Theory]
    [MemberData(nameof(OnRampAliases))]
    public void EveryAlias_UsesOnlyNormalOrSemibold(string name, float size, float line, ushort weight)
    {
        _ = size; _ = line; _ = weight;
        ushort w = Alias(name).ResolvedWeight;
        Assert.True(w is 400 or 600, $"{name} resolves weight {w}; only 400/600 are allowed off the display face");
    }

    /// <summary>The THREE sanctioned exceptions, pinned so the divergence stays a decision rather than a leak: the
    /// artist identity masthead keeps the display face at 700 and its own optical sizes. Nothing else may.</summary>
    [Theory]
    [InlineData("ArtistDisplay", 84f, 96f)]
    [InlineData("ArtistTitle", 48f, 60f)]
    [InlineData("ArtistCompactTitle", 32f, 40f)]
    public void DisplayFaceAliases_KeepTheirDocumented700(string name, float size, float line)
    {
        var el = Alias(name);
        Assert.Equal(size, el.Size);
        Assert.Equal(line, el.LineHeight);
        Assert.Equal((ushort)700, el.ResolvedWeight);
        Assert.Equal("Segoe UI Variable Display", el.FontFamily);
    }

    // ── The on-media ladder ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE scrim ladder. Three near-duplicate ladders (185/225/245, 132/190/220, 120/184/218) collapsed onto
    /// the MIDDLE values, except that REST adopts the engine's own <c>Tok.MediaScrim</c> — the engine already owns
    /// "the chip/pill/FAB scrim plate floated over media", and an app token 8 alpha steps away from it would be the
    /// very drift this file exists to remove.</summary>
    [Fact]
    public void OnMediaScrimLadder_IsOneLadder_RestOwnedByTheEngine()
    {
        Assert.Equal(Tok.MediaScrim, WaveeOnMedia.ScrimRest);
        Assert.Equal(ColorF.FromRgba(0, 0, 0, 190), WaveeOnMedia.ScrimHover);
        Assert.Equal(ColorF.FromRgba(0, 0, 0, 220), WaveeOnMedia.ScrimPressed);

        // Monotonic: a plate can only get darker rest → hover → pressed, and every rung is neutral black.
        Assert.True(WaveeOnMedia.ScrimRest.A < WaveeOnMedia.ScrimHover.A);
        Assert.True(WaveeOnMedia.ScrimHover.A < WaveeOnMedia.ScrimPressed.A);
        foreach (var c in new[] { WaveeOnMedia.ScrimRest, WaveeOnMedia.ScrimHover, WaveeOnMedia.ScrimPressed, WaveeOnMedia.CoverScrim })
        {
            Assert.Equal(0f, c.R);
            Assert.Equal(0f, c.G);
            Assert.Equal(0f, c.B);
        }
    }

    /// <summary>The full-cover veil is a DIFFERENT role from the small plate (it dims a whole square of artwork so a
    /// centred FAB reads on it), so it stays lighter than rest — deliberately, and that ordering is the pin.</summary>
    [Fact]
    public void CoverScrim_IsLighterThanTheSmallPlate()
    {
        Assert.Equal(ColorF.FromRgba(0, 0, 0, 110), WaveeOnMedia.CoverScrim);
        Assert.True(WaveeOnMedia.CoverScrim.A < WaveeOnMedia.ScrimRest.A);
    }

    /// <summary>The hairline: the middle of the old 70 / 58 / 55, which is also what two of the three sites already
    /// used. It doubles as the faint on-media RULE (the countdown ring's 64-alpha track).</summary>
    [Fact]
    public void OnMediaStroke_IsOneWhiteHairline()
    {
        Assert.Equal(ColorF.FromRgba(255, 255, 255, 58), WaveeOnMedia.Stroke);
    }

    /// <summary>Ink reuses the ENGINE's on-media tiers rather than minting app copies — the old 224/225/230/235 were
    /// four ways of writing "white" over a dark scrim, and 200 was one way of writing the 0.80 tier.</summary>
    [Fact]
    public void OnMediaInk_ReusesTheEngineTiers()
    {
        Assert.Equal(Tok.OnMediaPrimary, WaveeOnMedia.Ink);
        Assert.Equal(Tok.OnMediaSecondary, WaveeOnMedia.InkSecondary);
        Assert.Equal(Tok.OnMediaTertiary, WaveeOnMedia.InkTertiary);
        Assert.Equal(1f, WaveeOnMedia.Ink.A);
        Near(0.80f, WaveeOnMedia.InkSecondary.A, "on-media secondary alpha");
        Near(0.60f, WaveeOnMedia.InkTertiary.A, "on-media tertiary alpha");
    }

    /// <summary>The light on-media button ramp is DERIVED from the on-media ink, not hardcoded as three greys, so a
    /// palette preset that ever re-tints the on-media whites carries the whole button with it. The derivation must
    /// still land on the values it replaced (255 → 235 → 215) to within one 8-bit step.</summary>
    [Fact]
    public void LightOnMediaButton_IsDerivedFromInk_AndMatchesTheOldRamp()
    {
        Assert.Equal(Tok.OnMediaPrimary, WaveeOnMedia.LightButton);
        Near(235f / 255f, WaveeOnMedia.LightButtonHover.R, "light on-media hover");
        Near(215f / 255f, WaveeOnMedia.LightButtonPressed.R, "light on-media pressed");

        // Neutral, opaque, monotonically darker.
        Assert.Equal(WaveeOnMedia.LightButtonHover.R, WaveeOnMedia.LightButtonHover.B);
        Assert.Equal(1f, WaveeOnMedia.LightButtonHover.A);
        Assert.True(WaveeOnMedia.LightButtonPressed.R < WaveeOnMedia.LightButtonHover.R);
        Assert.True(WaveeOnMedia.LightButtonHover.R < WaveeOnMedia.LightButton.R);

        // The glyph ON it is the engine's opaque media stage, not one more hand-mixed near-black.
        Assert.Equal(Tok.MediaStage, WaveeOnMedia.LightButtonInk);
    }

    /// <summary>The two blurred-derivative dims disagreed by 0.03 alpha for no stated reason; one value now.</summary>
    [Fact]
    public void BackdropDim_IsOneValue()
    {
        Near(0.45f, WaveeOnMedia.BackdropDim.A, "backdrop dim alpha");
        Near(8f / 255f, WaveeOnMedia.BackdropDim.R, "backdrop dim red");
    }

    // ── Shared geometry ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The section rhythm lives in ONE place so Home's module gap and the artist page's section stack cannot
    /// drift apart. Both rungs are on the 4-grid.</summary>
    [Fact]
    public void SectionRhythm_IsSharedAndOnTheGrid()
    {
        Assert.Equal(32f, WaveeSize.SectionGap);
        Assert.Equal(40f, WaveeSize.SectionGapWide);
        Assert.True(WaveeSize.SectionGap < WaveeSize.SectionGapWide);
        Assert.Equal(0f, WaveeSize.SectionGap % 4f);
        Assert.Equal(0f, WaveeSize.SectionGapWide % 4f);
    }

    /// <summary>The page measure DetailShell, ArtistPage and Home all cap their content column at.</summary>
    [Fact]
    public void PageMeasure_IsOneNumber() => Assert.Equal(1600f, WaveeSize.PageMaxW);

    /// <summary>The thumbnail ladder: an 8-DIP ladder on the 4-grid. Everything between two rungs (36, 44, 52, 57) was
    /// snapped to its nearest rung, which is what stopped one page carrying five almost-identical art sizes.</summary>
    [Fact]
    public void ThumbnailLadder_IsAnEightStepLadderOnTheGrid()
    {
        float[] ladder = [WaveeSize.Thumb32, WaveeSize.Thumb40, WaveeSize.Thumb48, WaveeSize.Thumb56, WaveeSize.Thumb64];
        for (int i = 0; i < ladder.Length; i++)
        {
            Assert.Equal(32f + 8f * i, ladder[i]);
            Assert.Equal(0f, ladder[i] % 4f);
        }
    }

    /// <summary>The page gutter Home moved onto — the engine's own desktop <c>NavigationView</c> content margin, which
    /// every other page in the app already reads. Pinned here because Home's virtual-row shell AND its extent
    /// ESTIMATOR must take the identical value off both sides, or the feed re-pins its scroll anchor mid-scroll.</summary>
    [Fact]
    public void PageGutter_IsTheWideRung()
    {
        Assert.Equal(36f, Spacing.PageWide);
        Assert.Equal(24f, Spacing.Gutter);
        Assert.True(Spacing.PageWide > Spacing.Gutter);
    }

    /// <summary>The radii pair the app converged onto: 6 and 5 and 3 are gone; 4 / 8 / pill / circle remain. A circle is
    /// expressed as <c>Radii.Circle(diameter)</c> rather than a hand-computed half-height, so it survives the box
    /// changing size.</summary>
    [Fact]
    public void RadiiRamp_HasNoIntermediateRungs()
    {
        Assert.Equal(0f, Radii.None);
        Assert.Equal(4f, Radii.Control);
        Assert.Equal(8f, Radii.Card);
        Assert.Equal(16f, Radii.Pill);
        Assert.Equal(999f, Radii.Full);

        var circle = Radii.Circle(28f);
        Assert.True(circle.IsUniform);
        Assert.Equal(14f, circle.TopLeft);
    }
}
