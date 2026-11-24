// ── Theming/Colours/ColourSolver.cs — seeds in, a contrast-floored ladder out (#184, plan 3.3) ─────────────────────────────
//
// Pure: no token reads, no state. Every colour a user can pick passes through here, which is where the contrast guarantee lives:
// a style handed a ladder never has to check one. The rules, in order:
//   1. MODE. A dark set's ground has a relative luminance under 0.18, a light set's over 0.60 (`Fits`): a "dark" palette on a white
//      ground would put the app's dark tokens, the caption colour and the on-media ladder on the wrong polarity.
//   2. GROUNDS. A missing well steps the ground away from the raised plate (darker on dark, darker on light: the lower plate), the deep
//      band steps the well once more; the panel is a third of the way from the ground to the raised plate, the dialog two thirds; the
//      placeholder is the raised plate one step further from the ground.
//   3. INKS. The ink is floored to 7 : 1 on the ground and 4.5 : 1 on the raised plate; the secondary and tertiary inks to 4.5 : 1 on
//      the ground, the well and the raised plate; the secondary is pushed toward the ink when it would read as the tertiary's voice, never past
//      the midpoint to the ink, so the order ink, secondary, tertiary always holds.
//   4. ACCENT. The fill is floored to 3 : 1 on the ground; the accent ink to 4.5 : 1 on the ground, the well, the raised plate and its
//      own selection wash (12 % on dark, 6 % on light: Zune's rule); the ink on the fill is whichever of near-black and white reads, and
//      pure black when a mid-grey fill leaves near-black under 4.5 : 1.
//   5. SEVERITY. A given colour is floored like the accent ink; a missing one stays the theme's own (null).
// A floor walks HSL lightness, hue and saturation kept, in bounded 0.005 steps (the walk `ZuneAccent.Floored` uses).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Colours;

/// <summary>Resolves a <see cref="ColourSpec"/> and an accent into a <see cref="ColourLadder"/> (see the file header).</summary>
public static class ColourSolver
{
    /// <summary>The primary ink on the ground (WCAG AAA body text).</summary>
    public const float InkFloor = 7.0f;
    /// <summary>Any text on any plate it sits on (WCAG AA).</summary>
    public const float TextFloor = 4.5f;
    /// <summary>A non-text UI component (WCAG 1.4.11): the accent as a fill.</summary>
    public const float FillFloor = 3.0f;
    /// <summary>A dark set's ground is darker than this (relative luminance).</summary>
    public const float DarkGroundMax = 0.18f;
    /// <summary>A light set's ground is lighter than this (relative luminance).</summary>
    public const float LightGroundMin = 0.60f;
    /// <summary>The selection wash of the accent on dark (Zune's 12 %).</summary>
    public const float SelectionWashDark = 0.12f;
    /// <summary>The selection wash of the accent on light (Zune's 6 %).</summary>
    public const float SelectionWashLight = 0.06f;

    // How far a derived plate steps in HSL lightness: Zune's dark #161616 → #111111 and light #f5f5f5 → #ebebeb.
    private const float StepDark = 0.020f, StepLight = 0.039f;
    // The least HSL lightness between the secondary and the tertiary ink, so the two stay distinct voices.
    private const float VoiceGap = 0.04f;
    private const float WalkStep = 0.005f;
    private const int WalkSteps = 200;

    /// <summary>True when <paramref name="ground"/> has the polarity of <paramref name="kind"/> (rule 1).</summary>
    public static bool Fits(ThemeKind kind, ColorF ground)
    {
        float l = ColorContrast.RelativeLuminance(ground with { A = 1f });
        return kind == ThemeKind.Dark ? l < DarkGroundMax : l > LightGroundMin;
    }

    /// <summary>The ladder of <paramref name="spec"/> in its mode with <paramref name="accent"/> (the palette's default, a named accent or
    /// the user's own colour) as the accent. The accent is floored here, so no caller can hand a style an unreadable one. Throws when the
    /// spec's ground does not fit its mode (rule 1): the catalog and the custom parser never produce one.</summary>
    public static ColourLadder Resolve(ColourSpec spec, ColorF accent)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThemeKind kind = spec.Kind;
        bool dark = kind == ThemeKind.Dark;
        ColourSeeds s = spec.Seeds;
        ColorF ground = Opaque(s.Ground);
        if (!Fits(kind, ground))
            throw new ArgumentException($"Colour set '{spec.Id}': its ground does not fit {kind} (relative luminance {ColorContrast.RelativeLuminance(ground):0.###}).", nameof(spec));

        // 2. grounds
        ColorF raised = Opaque(s.Surface);
        float step = dark ? StepDark : StepLight;
        ColorF well = s.Well is { } w ? Opaque(w) : StepAway(ground, raised, step);
        ColorF deep = s.Deep is { } d ? Opaque(d) : StepAway(well, raised, step);
        ColorF panel = ColorF.Lerp(ground, raised, 1f / 3f);
        ColorF dialog = ColorF.Lerp(ground, raised, 2f / 3f);
        ColorF placeholder = StepAway(raised, ground, step);

        // 3. inks: dark sets walk their inks lighter, light sets darker
        bool lighter = dark;
        ColorF ink = Floor(Opaque(s.Ink), lighter, (ground, InkFloor), (raised, TextFloor));
        ColorF secondary = Floor(Opaque(s.InkMuted), lighter, (ground, TextFloor), (well, TextFloor), (raised, TextFloor));
        ColorF faintSeed = s.InkFaint is { } f ? Opaque(f) : ColorF.Lerp(secondary, ground, 0.35f);
        ColorF tertiary = Floor(faintSeed, lighter, (ground, TextFloor), (well, TextFloor), (raised, TextFloor));
        secondary = Apart(secondary, tertiary, ink, lighter);
        ColorF disabled = ColorF.Lerp(ground, ink, 0.40f);

        // 4. accent
        ColorF accentFill = Floor(Opaque(accent), lighter, (ground, FillFloor));
        ColorF wash = ColorContrast.Flatten(accentFill with { A = dark ? SelectionWashDark : SelectionWashLight }, ground);
        ColorF accentInk = Floor(accentFill, lighter, (ground, TextFloor), (well, TextFloor), (raised, TextFloor), (wash, TextFloor));
        ColorF onAccent = ColorContrast.PickContrast(accentFill);
        if (ColorContrast.Ratio(onAccent, accentFill) < TextFloor) onAccent = ColorContrast.PickContrast(accentFill, Black, White);

        // 5. severity
        ColorF? critical = Severity(s.Critical, lighter, ground, well, raised);
        ColorF? caution = Severity(s.Caution, lighter, ground, well, raised);
        ColorF? success = Severity(s.Success, lighter, ground, well, raised);

        return new ColourLadder(spec.Id, kind,
            Deep: deep, Well: well, Ground: ground, Panel: panel, Raised: raised, Dialog: dialog, Placeholder: placeholder,
            Ink: ink, InkSecondary: secondary, InkTertiary: tertiary, InkDisabled: disabled, WashInk: ink,
            AccentFill: accentFill, AccentInk: accentInk, OnAccent: onAccent,
            Critical: critical, Caution: caution, Success: success);
    }

    private static ColorF? Severity(ColorF? seed, bool lighter, ColorF ground, ColorF well, ColorF raised)
        => seed is { } c ? Floor(Opaque(c), lighter, (ground, TextFloor), (well, TextFloor), (raised, TextFloor)) : null;

    private static ColorF Opaque(ColorF c) => c with { A = 1f };

    private static readonly ColorF Black = ColorF.FromRgba(0, 0, 0), White = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    /// <summary><paramref name="from"/> one <paramref name="step"/> of HSL lightness away from <paramref name="other"/> (toward black when
    /// they are as light, or when there is no room the other way).</summary>
    private static ColorF StepAway(ColorF from, ColorF other, float step)
    {
        var (h, s, l) = ColorScience.ToHsl(from);
        var (_, _, lo) = ColorScience.ToHsl(other);
        float target = lo > l ? l - step : l + step;
        if (target > 1f || (lo <= l && l + step > 1f)) target = l - step;
        return ColorScience.FromHsl(h, s, Math.Clamp(target, 0f, 1f));
    }

    /// <summary>Walks <paramref name="c"/>'s HSL lightness (lighter or darker) until it reads each plate at that plate's floor. A colour
    /// that already does is returned as it is.</summary>
    private static ColorF Floor(ColorF c, bool lighter, params ReadOnlySpan<(ColorF Plate, float Min)> plates)
    {
        if (Reads(c, plates)) return c;
        var (h, s, l) = ColorScience.ToHsl(c);
        ColorF walked = c;
        for (int i = 1; i <= WalkSteps; i++)
        {
            float nl = lighter ? l + WalkStep * i : l - WalkStep * i;
            walked = ColorScience.FromHsl(h, s, Math.Clamp(nl, 0f, 1f));
            if (Reads(walked, plates) || nl <= 0f || nl >= 1f) break;
        }
        return walked;
    }

    private static bool Reads(ColorF ink, ReadOnlySpan<(ColorF Plate, float Min)> plates)
    {
        foreach (var (plate, min) in plates)
            if (ColorContrast.Ratio(ink, plate) < min) return false;
        return true;
    }

    /// <summary>The secondary ink pushed toward the ink until it is <see cref="VoiceGap"/> of lightness from the tertiary (the tertiary sits at
    /// its floor; the secondary has room toward the ink), but never past the midpoint between the tertiary and the ink: where the palette leaves
    /// less room than two gaps, the three voices share it evenly and keep their order.</summary>
    private static ColorF Apart(ColorF secondary, ColorF tertiary, ColorF ink, bool lighter)
    {
        var (h, s, l) = ColorScience.ToHsl(secondary);
        var (_, _, lt) = ColorScience.ToHsl(tertiary);
        var (_, _, li) = ColorScience.ToHsl(ink);
        float gap = lighter ? l - lt : lt - l;
        if (gap >= VoiceGap) return secondary;
        float target = lighter ? lt + VoiceGap : lt - VoiceGap;
        float mid = (lt + li) * 0.5f;
        target = lighter ? MathF.Min(target, mid) : MathF.Max(target, mid);
        // Only ever toward the ink: a secondary already further than the target (impossible here, but cheap to hold) stays.
        if (lighter ? target <= l : target >= l) return secondary;
        return ColorScience.FromHsl(h, s, Math.Clamp(target, 0f, 1f));
    }
}
