using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wave 3 of the design-system convergence: the VOICE gates. Waves 1 and 2 put the app on one type ramp and
/// one motion ladder; what was still speaking with several voices was the app's shared IDIOMS — the same thing said
/// four ways on four surfaces. These tests pin the three that are checkable as values:
/// <list type="bullet">
///   <item><b>the eyebrow</b> — one rung, one weight, ONE tracking, carried by the alias rather than the call site;</item>
///   <item><b>zebra striping</b> — a DERIVED token off the subtle-fill ladder, quieter than hover by construction, whose
///   interacted rungs are the row state composited OVER the stripe;</item>
///   <item><b>the accent roles</b> — action, selection and decor exist as named values.</item>
/// </list></summary>
public class VoiceUnificationTests
{
    // ── The eyebrow ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE tracking, owned by the alias. Before this wave the eyebrow role carried NINE different letterspacing
    /// values across 58 call sites (10, 20, 30, 32, 40, 50, 60, 70, 80, 120) — a ladder nobody designed, which is why
    /// two eyebrows stacked on one page never looked like the same label.</summary>
    [Fact]
    public void EyebrowAlias_OwnsTheOneTracking()
    {
        Assert.Equal(30f, WaveeType.EyebrowTracking);
        var el = WaveeType.Eyebrow("x");
        Assert.Equal(WaveeType.EyebrowTracking, el.CharSpacing);

        // …and it is still the Caption rung at Semibold: the tracking rides ON the ramp, it does not replace it.
        Assert.Equal(12f, el.Size);
        Assert.Equal(16f, el.LineHeight);
        Assert.Equal((ushort)600, el.ResolvedWeight);
    }

    /// <summary>The alias carries no COLOUR of its own: an accent reason, a tertiary kind tag and an on-accent badge are
    /// the same type at three jobs, and the accent arm is deliberate identity (see <c>WaveeAccent</c>). Metrics and
    /// tracking belong to the alias; colour belongs to the call site.</summary>
    [Fact]
    public void EyebrowAlias_LeavesColourToTheCallSite()
        => Assert.Equal(Ui.Caption("x").Color, WaveeType.Eyebrow("x").Color);

    // ── Zebra ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stripe is DERIVED, not three hand-picked alphas per theme.
    ///
    /// <para>DARK takes the engine's subtle-fill ink at its quietest rung, for the reason below: the dark SHELL zebra is
    /// literally the dark hover fill, so a striped row would have had no hover.</para>
    /// <para>LIGHT now takes the shell's own row ladder, which since the light-mode overhaul is a BLACK-alpha stripe
    /// solved together with the light hover/press rungs (it used to be a white-alpha value the app could not use — the
    /// dead field that nevertheless fed the light text-contrast solve). Taking it here is what keeps the palette
    /// flattening the same value the app paints.</para></summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void RowZebra_IsTheSubtleFillLadder(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            var shell = theme == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;
            Assert.Equal(theme == ThemeKind.Light ? shell.RowZebra : Tok.FillSubtleTertiary, WaveeColors.RowZebra);
            // Either way the stripe is INK on the surface, never a lift off it: black in light, white in dark.
            Assert.True(theme == ThemeKind.Light ? WaveeColors.RowZebra.R < 0.5f : WaveeColors.RowZebra.R > 0.5f);
        });
    }

    /// <summary>The invariant the old literals BROKE in dark, where the stripe (0x0F) was exactly the hover fill: a
    /// stripe must be quieter than hover, or a striped row has no hover at all.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void RowZebra_IsQuieterThanHover(ThemeKind theme)
    {
        WithTheme(theme, () =>
            Assert.True(WaveeColors.RowZebra.A < WaveeColors.RowHover.A,
                $"{theme}: zebra α {WaveeColors.RowZebra.A} is not below hover α {WaveeColors.RowHover.A}"));
    }

    /// <summary>Hover/press ON a stripe is the row state SOURCE-OVER the stripe, collapsed to ONE translucent fill —
    /// a row paints a single <c>Fill</c>, never two stacked plates. <c>ColorContrast.Over</c> is associative, so the
    /// merged rung composites pixel-identically to painting the two rungs in sequence.</summary>
    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void ZebraStates_AreTheRowStateOverTheStripe(ThemeKind theme)
    {
        WithTheme(theme, () =>
        {
            Assert.Equal(ColorContrast.Over(WaveeColors.RowHover, WaveeColors.RowZebra), WaveeColors.RowHoverZebra);
            Assert.Equal(ColorContrast.Over(WaveeColors.RowPressed, WaveeColors.RowZebra), WaveeColors.RowPressedZebra);

            // Compositing can only ADD coverage: an interacted stripe is never lighter than the stripe alone.
            Assert.True(WaveeColors.RowHoverZebra.A > WaveeColors.RowZebra.A);
            Assert.True(WaveeColors.RowPressedZebra.A > WaveeColors.RowZebra.A);
        });
    }

    // ── The accent budget's two hard rules ───────────────────────────────────────────────────────────────────────

    /// <summary>The three accent ROLES exist as named values, so a paint site can declare which one it is playing
    /// rather than reaching for <c>Tok.AccentDefault</c> and meaning any of the three.</summary>
    [Fact]
    public void AccentRoles_AreNamed()
    {
        Assert.Equal(Tok.AccentDefault, WaveeAccent.Action);
        Assert.Equal(Tok.AccentDefault, WaveeAccent.Selection);
        Assert.Equal(Tok.AccentTextPrimary, WaveeAccent.Decor);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void WithTheme(ThemeKind theme, Action body)
    {
        var was = Tok.Theme;
        try { Tok.Use(theme); body(); }
        finally { Tok.Use(was); }
    }
}
