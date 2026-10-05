// ── Wavee.Tests/VisualizerPaletteTests.cs — the face palette (Shell/Visualizer.Palette.cs) ─────────────────────────────
//
// Pure: `Visualizer.Palette.From` takes the accent, the graded scheme and the arm (no theme read), so both arms run here.
// Pins viz-app-plan §1.1: real cover roles only (no synthetic complement), three DISTINCT paint colours, the 3:1 floor
// over each arm's veil, the deep tone's clamp, the moment rotation and the lerp/fade that lands exactly on time.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

using P = Wavee.Visualizer.Palette;

namespace Wavee.Tests;

public class VisualizerPaletteTests
{
    /// <summary>Gradings shaped like the provider's (dark tinted base, dark base, subdued text, the white ink role).</summary>
    static readonly Scheme[] Schemes =
    [
        new(0xFF1A1440, 0xFF3B2A8C, 0xFFD64A9E, 0xFFB0B0C0, 0xFFFFFFFF),   // violet · indigo · magenta
        new(0xFF0F2A3A, 0xFF1F6E8C, 0xFFE2BE28, 0xFFA0A0A0, 0xFFFFFFFF),   // teal · blue · yellow
        new(0xFF101C24, 0xFF00968A, 0xFFFF7043, 0xFF9E9E9E, 0xFFFFFFFF),   // night · teal · orange
        new(0xFF2A0A0A, 0xFF8C1F1F, 0xFFD08030, 0xFFC0A090, 0xFFFFFFFF),   // red · rust · amber
        new(0xFF202020, 0xFF303030, 0xFF909090, 0xFFC0C0C0, 0xFFFFFFFF),   // greyscale
    ];

    static ColorF AccentOf(in Scheme s)
    {
        var a = Design.Palette.Accent(in s);
        return a.ToHsv().S <= Design.Palette.NeutralS ? ColorF.FromRgba(0x00, 0x78, 0xD4) : Design.Palette.Vivid(Design.Palette.Lift(a));
    }

    static ColorF Veil(bool dark) => Design.StageInk.Arm(dark ? ThemeKind.Dark : ThemeKind.Light).Veil;

    [Fact]
    public void Paint_colours_are_distinct_on_both_arms()
    {
        foreach (var s in Schemes)
            foreach (bool dark in new[] { true, false })
            {
                var p = P.From(AccentOf(s), s, dark);
                Assert.True(P.Distinct(p.A, p.B), $"A/B {s} dark={dark}: {p.A} {p.B}");
                Assert.True(P.Distinct(p.B, p.C), $"B/C {s} dark={dark}: {p.B} {p.C}");
                Assert.True(P.Distinct(p.A, p.C), $"A/C {s} dark={dark}: {p.A} {p.C}");
                Assert.Equal(dark, p.Dark);
            }
    }

    [Fact]
    public void Every_paint_colour_clears_the_floor_over_its_arms_veil()
    {
        foreach (var s in Schemes)
            foreach (bool dark in new[] { true, false })
            {
                var p = P.From(AccentOf(s), s, dark);
                var veil = Veil(dark);
                foreach (var c in new[] { p.A, p.B, p.C })
                    Assert.True(ColorContrast.Ratio(c, veil) >= P.StrokeContrast - 0.01f, $"{c} on {veil} (dark={dark})");
            }
        // the light arm darkens (a pale accent never becomes a highlighter), the dark arm lifts
        var pale = ColorF.FromRgba(0xFF, 0xE0, 0x80);
        Assert.True(P.Floor(pale, Veil(false), dark: false).ToHsv().V < pale.ToHsv().V);
        var murky = ColorF.FromRgba(0x20, 0x10, 0x40);
        Assert.True(P.Floor(murky, Veil(true), dark: true).ToHsv().V > murky.ToHsv().V);
        Assert.Equal(pale, P.Floor(pale, Veil(true), dark: true));       // already clears the floor: untouched
    }

    [Fact]
    public void No_synthetic_complement_B_is_a_real_role_or_a_tint_of_A()
    {
        // a chromatic scheme: B's hue is one of the graded roles' hues (the paint recipe keeps hue), never A + 150°
        var s = Schemes[1];
        var p = P.From(AccentOf(s), s, dark: true);
        float bHue = p.B.ToHsv().H;
        float best = float.MaxValue;
        foreach (uint role in new[] { s.BackgroundTintedBase, s.BackgroundBase, s.TextSubdued })
        {
            float h = Design.Palette.ToColor(role).ToHsv().H, d = MathF.Abs(h - bHue) % 360f;
            best = MathF.Min(best, d > 180f ? 360f - d : d);
        }
        Assert.True(best < 3f, $"B's hue {bHue} is no role's hue (closest {best}°)");

        // a greyscale cover (or none at all): the semantic accent plus VALUE tints of it — one hue, three values
        foreach (Scheme? grey in new Scheme?[] { Schemes[4], null })
            foreach (bool dark in new[] { true, false })
            {
                var g = P.From(ColorF.FromRgba(0x00, 0x78, 0xD4), grey, dark);
                Assert.True(P.HueGap(g.A, g.B) < 5f && P.HueGap(g.A, g.C) < 5f, $"{g} dark={dark}");
                Assert.True(P.Distinct(g.A, g.B) && P.Distinct(g.B, g.C) && P.Distinct(g.A, g.C), $"{g} dark={dark}");
            }
    }

    [Fact]
    public void Deep_is_deep_on_dark_and_pale_on_light()
    {
        foreach (Scheme? s in new Scheme?[] { Schemes[0], Schemes[2], null })
        {
            var d = P.From(ColorF.FromRgba(0xD6, 0x4A, 0x9E), s, dark: true);
            var l = P.From(ColorF.FromRgba(0xD6, 0x4A, 0x9E), s, dark: false);
            Assert.InRange(d.Deep.ToHsv().V, 0.079f, 0.281f);
            Assert.True(l.Deep.ToHsv().V >= 0.859f);
            Assert.True(l.Deep.ToHsv().S <= 0.351f);
        }
    }

    [Fact]
    public void The_accent_is_kept_and_alpha_is_opaque()
    {
        var accent = ColorF.FromRgba(0xD6, 0x4A, 0x9E);
        var p = P.From(accent, Schemes[0], dark: true);
        Assert.Equal(accent, p.Accent);
        Assert.Equal(1f, p.A.A); Assert.Equal(1f, p.B.A); Assert.Equal(1f, p.C.A); Assert.Equal(1f, p.Deep.A);
        Assert.Equal(P.WashAlphaDark, p.WashA);
        Assert.Equal(P.WashAlphaLight, P.From(accent, Schemes[0], dark: false).WashA);
    }

    [Fact]
    public void Rotated_is_a_three_cycle_that_keeps_accent_and_deep()
    {
        var p = P.From(ColorF.FromRgba(0xD6, 0x4A, 0x9E), Schemes[0], dark: true);
        var r = p.Rotated();
        Assert.Equal(p.B, r.A);
        Assert.Equal(p.C, r.B);
        Assert.Equal(p.A, r.C);
        Assert.Equal(p.Accent, r.Accent);
        Assert.Equal(p.Deep, r.Deep);
        Assert.Equal(p, r.Rotated().Rotated());
        Assert.Equal(p.A, p.Pick(0)); Assert.Equal(p.B, p.Pick(4)); Assert.Equal(p.C, p.Pick(-1));
    }

    [Fact]
    public void Lerp_lands_on_both_ends_and_clamps()
    {
        var a = P.From(ColorF.FromRgba(0xD6, 0x4A, 0x9E), Schemes[0], dark: true);
        var b = P.From(ColorF.FromRgba(0x00, 0x96, 0x8A), Schemes[2], dark: false);
        Assert.Equal(a, P.Lerp(a, b, 0f));
        Assert.Equal(b, P.Lerp(a, b, 1f));
        Assert.Equal(b, P.Lerp(a, b, 7f));
        Assert.Equal(a, P.Lerp(a, b, -1f));
        var mid = P.Lerp(a, b, 0.5f);
        Assert.Equal(ColorF.Lerp(a.A, b.A, 0.5f), mid.A);
    }

    [Fact]
    public void Fade_lands_exactly_on_time_at_any_tick_rate()
    {
        var from = P.From(ColorF.FromRgba(0xD6, 0x4A, 0x9E), Schemes[0], dark: true);
        var to = from.Rotated();
        foreach (long step in new long[] { 1, 7, 16, 17, 33, 50, 100 })
        {
            var fade = new P.Fade();
            fade.Begin(from, to, 10_000, Stage.Tone.MomentFadeMs, moment: true);
            Assert.True(fade.Active && fade.Moment);
            float last = 0f;
            long t = 10_000;
            while (fade.Progress(t) < 1f)
            {
                float p = fade.Progress(t);
                Assert.True(p >= last);
                last = p;
                t += step;
            }
            Assert.True(t >= 10_000 + (long)Stage.Tone.MomentFadeMs && t < 10_000 + (long)Stage.Tone.MomentFadeMs + step);   // lands at 900 ms, never later
            Assert.Equal(to, fade.At(t));
            fade.Land();
            Assert.False(fade.Active);
            Assert.Equal(1f, fade.Progress(t));
        }
    }
}
