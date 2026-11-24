// ── Theming/Colours/ColourTokens.cs — a ladder onto the engine's token set and accent ramp (#184, plan 3.6) ─────────────────
//
// The part every theme shares. `Apply` rewrites EVERY colour of a style's `TokenSet` for one mode from the ladder; the style then lays its
// own look on top (its alphas, its hairlines, its own on-accent rule), exactly as it does over the engine's preset today. Three rules:
//   · An OPAQUE member is the rung it names (the page is the ground, the pane the well, a flyout the raised plate, text the inks).
//   · A TRANSLUCENT member keeps its alpha and takes the ladder's colour of the same kind: an ink-like wash (white on dark, black on light:
//     a hover, a hairline, a rail) becomes the ladder's ink at that alpha; a ground-like veil (a dark plate on dark, a light one on light: the
//     focused text box, the scrim of a control on an image) becomes the ground at that alpha. So each theme keeps its own state ladder.
//   · What is not the user's to recolour stays: the caption close red, the dialog smoke, the on-accent strokes, and the accent and the ink on
//     it unless the palette owns the accent (`ColourPair.OwnsAccent`).
// The members are listed one by one (no reflection: NativeAOT), so a member the engine adds fails the build here, never silently.
//
// `Ramp` builds the accent ramp that makes `Tok.AccentDefault` and `Tok.AccentTextPrimary` the ladders' exact colours in both modes. The
// engine reads the fill as Light2 and the text as Light3 in dark, Dark1 and Dark2 in light (`Tok.AccentFillShade`), Zune's trick
// (`ZuneAccent.RampOf`). A mode with no ladder keeps the theme's own shades, so a palette by night and Windows' accent by day both hold.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Colours;

/// <summary>Maps a <see cref="ColourLadder"/> onto the engine's token set and accent ramp (see the file header).</summary>
public static class ColourTokens
{
    // A severity's tinted plate (the InfoBar ground): the colour over the ground at WinUI's strength.
    private const float SeverityPlateDark = 0.16f, SeverityPlateLight = 0.12f;

    /// <summary><paramref name="baseSet"/> with every colour-carrying member taken from <paramref name="l"/> (see the file header).
    /// <paramref name="ownsAccent"/>: the accent and the ink on it come from the ladder too.</summary>
    public static TokenSet Apply(TokenSet baseSet, ColourLadder l, bool ownsAccent)
    {
        ArgumentNullException.ThrowIfNull(baseSet);
        ArgumentNullException.ThrowIfNull(l);
        bool dark = l.Kind == ThemeKind.Dark;
        TokenSet t = baseSet;
        ColorF W(ColorF c) => Rewash(c, l, dark);
        ColorF plate = dark ? l.Ground : l.Raised;   // where a severity's tinted plate sits

        t = t with
        {
            // fills: washes keep their alpha; the solid rungs are the ladder's
            FillControlDefault = W(t.FillControlDefault),
            FillControlSecondary = W(t.FillControlSecondary),
            FillControlTertiary = W(t.FillControlTertiary),
            FillControlDisabled = W(t.FillControlDisabled),
            FillControlStrong = W(t.FillControlStrong),
            FillControlStrongDisabled = W(t.FillControlStrongDisabled),
            FillControlSolid = Opaque(t.FillControlSolid) ? l.Raised : W(t.FillControlSolid),
            FillControlOnImage = t.FillControlOnImage,
            FillSubtleTransparent = W(t.FillSubtleTransparent),
            FillSubtleSecondary = W(t.FillSubtleSecondary),
            FillSubtleTertiary = W(t.FillSubtleTertiary),
            FillCardDefault = l.Raised,
            FillCardSecondary = W(t.FillCardSecondary),
            FillLayerDefault = l.Panel,
            FillLayerAlt = l.Dialog,
            LayerOnMicaBaseAlt = l.Well,
            FillSolidBase = l.Ground,
            FillSolidBaseAlt = l.Well,
            FillSolidSecondary = l.Panel,
            FillSolidTertiary = l.Raised,
            FillSmoke = t.FillSmoke,
            FillControlAltSecondary = W(t.FillControlAltSecondary),
            FillControlAltTertiary = W(t.FillControlAltTertiary),
            FillControlAltQuaternary = W(t.FillControlAltQuaternary),
            FillControlAltDisabled = W(t.FillControlAltDisabled),
            FillControlInputActive = Opaque(t.FillControlInputActive) ? l.Raised : W(t.FillControlInputActive),

            // strokes: washes of the ink (the on-accent strokes belong to the accent fill, not the ground)
            StrokeControlDefault = W(t.StrokeControlDefault),
            StrokeControlSecondary = W(t.StrokeControlSecondary),
            StrokeCardDefault = W(t.StrokeCardDefault),
            StrokeDividerDefault = W(t.StrokeDividerDefault),
            StrokeSurfaceDefault = W(t.StrokeSurfaceDefault),
            StrokeFlyoutDefault = W(t.StrokeFlyoutDefault),
            StrokeControlStrongDefault = W(t.StrokeControlStrongDefault),
            StrokeControlStrongDisabled = W(t.StrokeControlStrongDisabled),

            // text
            TextPrimary = l.Ink,
            TextSecondary = l.InkSecondary,
            TextTertiary = l.InkTertiary,
            TextDisabled = l.InkDisabled,
            TextControlHeaderForeground = l.Ink,
            TextControlHeaderForegroundDisabled = l.InkDisabled,
            TextControlDescriptionForeground = l.InkSecondary,
            TextControlForegroundDisabled = l.InkDisabled,

            // focus, scroll, acrylic, hero
            FocusOuter = l.Ink,
            FocusInner = l.Ground,
            ScrollThumb = Opaque(t.ScrollThumb) ? l.InkTertiary : W(t.ScrollThumb),
            AcrylicTint = l.Raised,
            AcrylicBase = l.Raised,
            AcrylicFlyout = t.AcrylicFlyout with { Tint = l.Raised, Fallback = l.Raised },
            HeroGradientTop = l.Panel,
            HeroGradientBottom = l.Ground,

            // severity: the ladder's when the palette names one, the theme's own otherwise
            SystemFillCritical = l.Critical ?? t.SystemFillCritical,
            SystemFillCaution = l.Caution ?? t.SystemFillCaution,
            SystemFillSuccess = l.Success ?? t.SystemFillSuccess,
            SystemFillCriticalBackground = l.Critical is { } cr ? SeverityPlate(cr, plate, dark) : t.SystemFillCriticalBackground,
            SystemFillCautionBackground = l.Caution is { } ca ? SeverityPlate(ca, plate, dark) : t.SystemFillCautionBackground,
            SystemFillSuccessBackground = l.Success is { } su ? SeverityPlate(su, plate, dark) : t.SystemFillSuccessBackground,
            SystemFillAttentionBackground = W(t.SystemFillAttentionBackground),
            SystemFillSolidNeutral = l.InkTertiary,

            WindowBackground = l.Ground,
        };

        if (ownsAccent)
        {
            t = t with
            {
                AccentDefault = l.AccentFill,
                AccentSecondary = l.AccentFill with { A = 0.90f },
                AccentTertiary = l.AccentFill with { A = 0.80f },
                AccentSubtle = l.AccentFill with { A = 0.16f },
                AccentTextPrimary = l.AccentInk,
                AccentTextSecondary = l.AccentInk,
                AccentTextTertiary = l.AccentFill,
                AccentSelectedTextBackground = l.AccentFill,
                TextOnAccentPrimary = l.OnAccent,
                TextOnAccentSecondary = l.OnAccent with { A = 0.70f },
                TextOnAccentSelectedText = l.OnAccent,
            };
        }
        return t;
    }

    /// <summary>The accent ramp for the pair (see the file header): <paramref name="themeRamp"/> when the palette does not own the accent, else
    /// the ladders' shades on the side of each mode that has one and the theme's shades on the side of a mode that has none.</summary>
    public static AccentRamp Ramp(in ColourPair colours, AccentRamp themeRamp)
    {
        if (!colours.OwnsAccent) return themeRamp;
        ColourLadder? dark = colours.Dark, light = colours.Light;
        return new AccentRamp(
            Base: dark?.AccentFill ?? themeRamp.Base,
            Light1: dark?.AccentFill ?? themeRamp.Light1,
            Light2: dark?.AccentFill ?? themeRamp.Light2,
            Light3: dark?.AccentInk ?? themeRamp.Light3,
            Dark1: light?.AccentFill ?? themeRamp.Dark1,
            Dark2: light?.AccentInk ?? themeRamp.Dark2,
            Dark3: light is { } l ? AccentRamp.Derive(l.AccentInk).Dark1 : themeRamp.Dark3);
    }

    /// <summary><paramref name="baseShell"/> (the engine's shell palette for one mode) in the ladder's colours: every translucent rung re-washed at
    /// its own alpha (the row zebra, hover and press, the translucent bands), an opaque one the ground.</summary>
    public static ShellPalette Shell(ShellPalette baseShell, ColourLadder l)
    {
        ArgumentNullException.ThrowIfNull(baseShell);
        ArgumentNullException.ThrowIfNull(l);
        bool dark = l.Kind == ThemeKind.Dark;
        ColorF S(ColorF c) => Opaque(c) ? l.Ground : Rewash(c, l, dark);
        return new ShellPalette(
            Toolbar: S(baseShell.Toolbar), Sidebar: S(baseShell.Sidebar), PlayerBar: S(baseShell.PlayerBar), FileArea: S(baseShell.FileArea),
            Content: S(baseShell.Content), ContentAlt: S(baseShell.ContentAlt), RowZebra: S(baseShell.RowZebra), RowHover: S(baseShell.RowHover),
            RowHoverZebra: S(baseShell.RowHoverZebra), RowPressed: S(baseShell.RowPressed), RowPressedZebra: S(baseShell.RowPressedZebra));
    }

    private static bool Opaque(ColorF c) => c.A >= 0.999f;

    /// <summary>A translucent member at its own alpha in the ladder's colour of its kind: the ink for an ink-like wash, the ground for a
    /// ground-like veil. An opaque member is returned as it is (the caller names its rung).</summary>
    private static ColorF Rewash(ColorF c, ColourLadder l, bool dark)
    {
        if (Opaque(c) || c.A <= 0f) return c;
        float lum = ColorContrast.RelativeLuminance(c with { A = 1f });
        bool inkLike = dark ? lum > 0.18f : lum < 0.18f;
        return (inkLike ? l.WashInk : l.Ground) with { A = c.A };
    }

    private static ColorF SeverityPlate(ColorF severity, ColorF plate, bool dark)
        => ColorContrast.Flatten(severity with { A = dark ? SeverityPlateDark : SeverityPlateLight }, plate);
}
