using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE LIGHT-MODE OVERHAUL (D44). The app's light theme was not one bad colour — the base palette is the WinUI light
/// dictionary verbatim and always was. It was a set of app-side mechanics each of which had been solved against a
/// DIFFERENT light surface than the one it ended up painting on, and whose light arms were in several cases the dark
/// arm's numbers copied across. These are the pins for the ones that are checkable as values:
/// <list type="bullet">
///   <item>the DATA-DOT ink — a wire hue authored for a dark surface, re-graded hue-dependently for a light one;</item>
///   <item>the SELECTION ladder — three rungs that only ever go up, replacing an inversion;</item>
///   <item>the LIGHT ROW ladder — hover/press/zebra raised and solved against the art-derived page tone;</item>
///   <item>the WARM palette being reachable at all;</item>
///   <item>the shell wash's bottom anchoring.</item>
/// </list>
/// The page tone's own clamp lives with its siblings in <see cref="DetailPageToneTests"/>.
/// </summary>
// Tok is one process-global palette/theme; LightModeOverhaulTests.WithTheme swaps it mid-test, and the assertions here read it
// live (Tok.AccentDefault twice around a call). xunit runs classes in parallel, so those four classes serialise on one
// collection — the same discipline EntranceStaggerTests/MotionSystemTests use for the motion globals.
[Collection("wavee-tok-global")]
public class LightModeOverhaulTests
{
    // ── D44.3 — the Camelot data dot ─────────────────────────────────────────────────────────────────────────────

    // Four inputs chosen for what each one proves, not for coverage. Yellow and cyan sit INSIDE the luminance band and
    // need the deep rung; red and magenta sit outside it and must NOT be darkened as far (that is what turns a colour
    // wheel into twelve browns). Grey has no hue at all.
    const uint WireYellow = 0xFFFFE119;   // ~52°
    const uint WireCyan   = 0xFF19E6E6;   // 180°
    const uint WireRed    = 0xFFE61919;   // 0°
    const uint WireGrey   = 0xFF9A9A9A;

    /// <summary>DARK IS A PASSTHROUGH. The wire colours already ARE the dark-surface answer — re-grading them would
    /// break the one property the Camelot wheel guarantees, which is that harmonically adjacent keys are adjacent
    /// hues.</summary>
    [Theory]
    [InlineData(WireYellow)]
    [InlineData(WireCyan)]
    [InlineData(WireRed)]
    [InlineData(WireGrey)]
    public void DataDotInk_IsAPassthroughInDark(uint argb)
        => Assert.Equal(WaveePalette.ToColor(argb), WaveePalette.DataDotInk(argb, ThemeKind.Dark));

    /// <summary>LIGHT DARKENS BY HUE BAND, which is the whole point: a single "darken by N" is the mistake this
    /// replaces. Yellow at L 0.5 is nearly invisible on a near-white row and needs about three rungs; red is already
    /// dark and needs about one. Both land at the S the wheel needs to stay legible as a wheel.</summary>
    [Theory]
    [InlineData(WireYellow, 0.30f)]
    [InlineData(WireCyan, 0.30f)]
    [InlineData(WireRed, 0.40f)]
    public void DataDotInk_ForcesTheHueBandsLightnessRung(uint argb, float expectedL)
    {
        var ink = WaveePalette.DataDotInk(argb, ThemeKind.Light);
        var (h, s, l) = WaveePalette.ToHsl(ink);
        Assert.Equal(expectedL, l, 3);
        Assert.Equal(0.65f, s, 3);
        // …and the HUE survives, or the dot has stopped identifying the key. Compared on the CIRCLE: red round-trips
        // through the HSL conversions as 359.99997°, which is the same hue as 0° and is not a drift.
        float drift = MathF.Abs(h - WaveePalette.ToHsl(WaveePalette.ToColor(argb)).H) % 360f;
        Assert.True(MathF.Min(drift, 360f - drift) < 0.5f, $"hue drifted by {drift}°");
    }

    /// <summary>The band members really are darker than the ones outside it — stated as the ordering rather than as two
    /// literals, because the ordering is the design and the literals are tuning.</summary>
    [Fact]
    public void DataDotInk_DarkensTheYellowCyanBandFurtherThanTheRest()
    {
        float yellow = WaveePalette.ToHsl(WaveePalette.DataDotInk(WireYellow, ThemeKind.Light)).L;
        float red = WaveePalette.ToHsl(WaveePalette.DataDotInk(WireRed, ThemeKind.Light)).L;
        Assert.True(yellow < red, $"yellow {yellow:F2} must darken further than red {red:F2}");
    }

    /// <summary>Every light dot clears a real legibility bar against the surface it lands on — the light page tone at
    /// its own forced lightness, which is the DARKEST light row host in the app. 3:1 is the WCAG non-text bar, which is
    /// the right one: this is a 6-DIP graphical token, not text.</summary>
    [Theory]
    [InlineData(WireYellow)]
    [InlineData(WireCyan)]
    [InlineData(WireRed)]
    [InlineData(WireGrey)]
    public void DataDotInk_ClearsTheNonTextContrastBarOnTheLightestRow(uint argb)
    {
        var host = WaveePalette.FromHsl(120f, WaveePalette.PageToneLightSMax, WaveePalette.PageToneLightL);
        float ratio = ColorContrast.Ratio(WaveePalette.DataDotInk(argb, ThemeKind.Light), host);
        Assert.True(ratio >= 3f, $"dot ratio {ratio:F2} on the light page tone is below the 3:1 non-text bar");
    }

    /// <summary>A greyscale swatch stays grey. Forcing S 0.65 on a hueless input would fabricate a red out of HSL's
    /// h == 0 fallback — the same "never invent a colour" rule the page tone and the chrome accent already keep.</summary>
    [Fact]
    public void DataDotInk_NeverInventsAHueForAGreySwatch()
    {
        var ink = WaveePalette.DataDotInk(WireGrey, ThemeKind.Light);
        Assert.Equal(ink.R, ink.G, 3);
        Assert.Equal(ink.G, ink.B, 3);
    }

    /// <summary>Alpha is carried through untouched — the dot's dimming is a node Opacity, not a colour property.</summary>
    [Fact]
    public void DataDotInk_PreservesAlpha()
        => Assert.Equal(0x80 / 255f, WaveePalette.DataDotInk(0x80FFE119, ThemeKind.Light).A, 3);

    // ── D44.5 — the selection ladder ─────────────────────────────────────────────────────────────────────────────

    /// <summary>THE ORDERING LAW, in both themes: hovering the row you are already on must read STRONGER than the row
    /// at rest, and pressing it must stay above rest too. The bug this replaces had selected-at-rest on the subtle
    /// SECONDARY rung and hovered-selected on the quieter TERTIARY one — pointing at your selection visibly deselected
    /// it. Measured as composited coverage over a common host, not as raw alpha, because the rungs are different inks.
    /// </summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void SelectionStates_OnlyEverGoUp(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            ColorF host = theme == ThemeKind.Light
                ? ColorF.FromRgba(0xFC, 0xFC, 0xFC) : ColorF.FromRgba(0x2C, 0x2C, 0x2C);
            float rest = Delta(WaveeColors.SelectedRest, host);
            float hover = Delta(WaveeColors.SelectedHover, host);
            float pressed = Delta(WaveeColors.SelectedPressed, host);

            Assert.True(hover > rest, $"{theme}: hovered-selected ({hover:F4}) must read stronger than at rest ({rest:F4})");
            Assert.True(pressed > rest, $"{theme}: pressed-selected ({pressed:F4}) must stay above rest ({rest:F4})");
            // WinUI's own shape: press DIPS below hover, it does not overshoot it.
            Assert.True(pressed < hover, $"{theme}: pressed ({pressed:F4}) must sit below hover ({hover:F4})");
            // …and an unselected row's hover must not out-shout the selection it sits beside.
            Assert.True(rest > Delta(Tok.FillSubtleSecondary, host),
                $"{theme}: selection at rest is quieter than an unselected row's hover");

            static float Delta(in ColorF top, in ColorF host)
                => ColorContrast.LuminanceDelta(ColorContrast.Flatten(top, host), host);
        });
    }

    /// <summary>Selection is the ACCENT plate (WaveeAccent role 2, "you are here") and the hovered/pressed rungs are
    /// COMPOSED over it rather than swapped for a neutral — a row paints ONE fill, so the only way a state can be
    /// "the plate plus a veil" is source-over.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void SelectionStates_AreTheAccentPlateWithTheStandardVeilsOverIt(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            Assert.Equal(Tok.AccentSubtle, WaveeColors.SelectedRest);
            Assert.Equal(ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle), WaveeColors.SelectedHover);
            Assert.Equal(ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle), WaveeColors.SelectedPressed);
        });
    }

    // ── D44.2 / D44.8 — the light row ladder ─────────────────────────────────────────────────────────────────────

    /// <summary>The light row rungs are RAISED and they are BLACK ink. They used to be WinUI's own 0x09/0x0C subtle
    /// values, which are correct against a near-white #FCFCFC pane and vanish against the art-derived page tone the
    /// detail lists actually sit on. The ordering (zebra &lt; hover &lt; pressed) is the part that must never move.</summary>
    [Fact]
    public void TheLightRowLadder_IsRaisedBlackInk_InOrder()
    {
        foreach (var palette in Tok.Presets)
        {
            var shell = palette.LightShell;
            Assert.True(shell.RowZebra.R < 0.5f && shell.RowHover.R < 0.5f && shell.RowPressed.R < 0.5f,
                $"{palette.Id}: the light row ladder must be black ink, not a white lift");
            Assert.True(shell.RowZebra.A < shell.RowHover.A, $"{palette.Id}: zebra is not below hover");
            Assert.True(shell.RowHover.A < shell.RowPressed.A, $"{palette.Id}: hover is not below pressed");
            Assert.True(shell.RowHover.A >= 0.045f, $"{palette.Id}: light hover α {shell.RowHover.A:F3} is back below the audible floor");
            // The merged rungs are COMPOSED, never eyeballed — source-over is associative, so painting one merged fill
            // is pixel-identical to stacking the two.
            Assert.Equal(ColorContrast.Over(shell.RowHover, shell.RowZebra), shell.RowHoverZebra);
            Assert.Equal(ColorContrast.Over(shell.RowPressed, shell.RowZebra), shell.RowPressedZebra);
        }
    }

    /// <summary>Every light row state stays legible against the DARKEST light host a row can land on — the art-derived
    /// page tone at its forced lightness. This is the host the rungs were re-solved against; the stock near-white pane
    /// is strictly easier.</summary>
    [Fact]
    public void TheLightRowStates_AreVisibleOnTheArtDerivedPageTone()
    {
        var tone = WaveePalette.FromHsl(120f, WaveePalette.PageToneLightSMax, WaveePalette.PageToneLightL);
        foreach (var palette in Tok.Presets)
        {
            var shell = palette.LightShell;
            float hover = ColorContrast.LuminanceDelta(ColorContrast.Flatten(shell.RowHover, tone), tone);
            Assert.True(hover >= 0.05f, $"{palette.Id}: hover moves the page tone by only {hover:P1}");
        }
    }

    // D44.7 ("the Warm palette is reachable") was the palette picker's own reachability test — the picker (Settings +
    // profile menu) is gone (Workstream B, "Settings regroup + removals"; Wavee always renders Tok.NeutralPalette now),
    // so there is no app-side id → palette resolution left to pin. Tok.PaletteById itself is an ENGINE api and stays
    // covered by the engine's own tests.

    // ── D44.4 — the shell wash geometry ──────────────────────────────────────────────────────────────────────────

    /// <summary>Only the Mix placement hangs off the bottom edge, which is what makes the inset safe: Hero and Weekly
    /// are TOP-anchored and therefore bit-for-bit unmoved by it, so Home's approved look above the dock is untouched.
    /// </summary>
    [Fact]
    public void OnlyTheMixWash_IsBottomAnchored()
    {
        Assert.True(ShellWashGeometry.Mix.AnchorBottom);
        Assert.False(ShellWashGeometry.Hero.AnchorBottom);
        Assert.False(ShellWashGeometry.Weekly.AnchorBottom);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void WithTheme(ThemeKind theme, Action body)
    {
        var priorPalette = Tok.Palette;
        var prior = Tok.Theme;
        try { Tok.Use(theme); body(); }
        finally { Tok.Use(priorPalette, prior); }
    }
}
