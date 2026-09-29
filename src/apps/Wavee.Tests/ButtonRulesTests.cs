// ── Wavee.Tests/ButtonRulesTests.cs — the button-standardisation colour/geometry math (Workstream B, Group A) ───────
//
// Pure `ColorF` math (FollowTint) plus one Style-assembly fact (FollowStyle) — no source-text reads, no engine host.
// NEVER `Tok.Use(...)`: `Tok` is process-wide shared state and this assembly runs its facts in parallel, so every
// assertion here either takes `light` as a plain argument (the pure path) or compares two values computed back-to-back
// under whatever the ambient live theme already is (the Style-assembly facts) — the same discipline `DesignTests`
// documents for why its own palette facts take a theme PARAMETER instead of mutating `Tok.Theme`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ButtonRulesTests
{
    static readonly ColorF Blue = ColorF.FromRgba(0x33, 0x66, 0x99);

    [Fact]
    public void Primary_width_agrees_with_the_button_grammar_constant()
        // Convergence test (ch 00 §9.6's pattern): a drift here is a Primary CTA one edge narrower than its own floor.
        => Assert.Equal(Controls.PrimaryMinWidth, ButtonRules.PrimaryWidthNominal);

    [Fact]
    public void Dark_tint_alphas_match_the_state_table()
    {
        var t = ButtonRules.FollowTint(Blue, light: false);
        Assert.Equal(0.14f, t.RestFill.A, 3);
        Assert.Equal(0.20f, t.Hover.A, 3);
        Assert.Equal(0.10f, t.Pressed.A, 3);
        Assert.Equal(0.50f, t.Stroke.A, 3);
        Assert.Equal(0.35f, t.PressedStroke.A, 3);
    }

    [Fact]
    public void Light_tint_alphas_match_the_state_table()
    {
        var t = ButtonRules.FollowTint(Blue, light: true);
        Assert.Equal(0.10f, t.RestFill.A, 3);
        Assert.Equal(0.15f, t.Hover.A, 3);
        Assert.Equal(0.07f, t.Pressed.A, 3);
        Assert.Equal(0.60f, t.Stroke.A, 3);
        Assert.Equal(0.42f, t.PressedStroke.A, 3);
    }

    [Fact]
    public void Every_tint_swatch_carries_the_accents_own_colour()
    {
        // Only the alpha channel is tiered; a caller's RGB rides through untouched on every swatch.
        var t = ButtonRules.FollowTint(Blue, light: false);
        foreach (var swatch in new[] { t.RestFill, t.Hover, t.Pressed, t.Stroke, t.PressedStroke })
        {
            Assert.Equal(Blue.R, swatch.R, 3);
            Assert.Equal(Blue.G, swatch.G, 3);
            Assert.Equal(Blue.B, swatch.B, 3);
        }
    }

    [Fact]
    public void Heart_ink_keeps_the_accent_when_it_clears_contrast_on_dark()
    {
        // White on the dark backdrop's near-black resting fill clears 3:1 easily.
        var white = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
        var t = ButtonRules.FollowTint(white, light: false);
        Assert.Equal(white with { A = 1f }, t.HeartInk);
    }

    [Fact]
    public void Heart_ink_falls_back_when_the_accent_cannot_clear_contrast_on_dark()
    {
        // Near-black on the dark backdrop's near-black resting fill (14% of near-black over near-black) reads as
        // near-black on near-black — nowhere near 3:1 — so the fallback ink wins. The fallback is the BAKED dark
        // token (Tok.Dark), matching `light: false`, never the live ambient theme.
        var t = ButtonRules.FollowTint(ColorF.FromRgba(0x10, 0x10, 0x10), light: false);
        Assert.Equal(Tok.Dark.AccentTextPrimary, t.HeartInk);
    }

    [Fact]
    public void Heart_ink_falls_back_when_the_accent_cannot_clear_contrast_on_light()
    {
        // White on the light backdrop's near-white resting fill is the light-theme mirror of the dark fact above.
        var t = ButtonRules.FollowTint(ColorF.FromRgba(0xFF, 0xFF, 0xFF), light: true);
        Assert.Equal(Tok.Light.AccentTextPrimary, t.HeartInk);
    }

    [Fact]
    public void Follow_style_is_32_r4_and_carries_the_pop_and_reflow_knobs()
    {
        var s = ButtonRules.FollowStyle(Blue, light: false);
        Assert.Equal(Controls.ButtonHeight, s.MinHeight);
        Assert.Equal(Radii.Control, s.CornerRadius);
        Assert.Equal(1.18f, s.CheckedPopScale);
        Assert.Equal(250f, s.CheckedPopMs);
        Assert.Equal(14f, s.GlyphSize);
        Assert.Equal(FluentGpu.Dsl.Spacing.XS, s.GlyphGap);
        Assert.NotNull(s.ContentReflow);
        Assert.NotNull(s.LabelSwap);
    }

    [Fact]
    public void Follow_style_on_background_is_the_tints_rest_swatch()
    {
        var t = ButtonRules.FollowTint(Blue, light: false);
        var s = ButtonRules.FollowStyle(Blue, light: false);
        Assert.Equal(t.RestFill, s.OnBackground);
        Assert.Equal(t.Hover, s.OnHover);
        Assert.Equal(t.Pressed, s.OnPressed);
    }

    [Fact]
    public void Follow_style_off_arm_is_the_stock_toggle_default()
    {
        // The plan's off row IS the stock Standard ramp — FollowStyle must not touch it. Comparing two values
        // computed back-to-back under whatever the ambient live theme is (never asserting an absolute colour here),
        // so this fact needs no theme control of its own.
        var s = ButtonRules.FollowStyle(Blue, light: false);
        var stock = ToggleButton.DefaultStyle;
        Assert.Equal(stock.OffBackground, s.OffBackground);
        Assert.Equal(stock.OffHover, s.OffHover);
        Assert.Equal(stock.OffPressed, s.OffPressed);
        Assert.Equal(stock.OffForeground, s.OffForeground);
    }
}
