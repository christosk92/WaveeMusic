// ── Themes/Zune/Style/ZuneAccent.cs — the accent of the Zune style: five swatches, a light step, the ramp (themes plan 6.3, Z1) ──
//
// `style.accent` is one of five swatches (Z-Tokens, Z-Theme-Options): pink (the default), orange, blue, green, purple. On the LIGHT
// ground the accent darkens one step (Z-Light-*: pink #ff3ca6 becomes #e0197f), so every swatch is a PAIR, light and dark. The dark
// arm is the board's swatch. The light arm starts from the board's step and keeps going down in LIGHTNESS alone (hue and saturation
// kept) until accent text on the light ground reads 4.5 : 1 (owner decision 2026-10-24, audit B5a): the board's step reads 3.6 to
// 4.2 : 1 there, and the accent draws words (links, "#362 in the world", the lit pivot, the caption) as well as fills. Purple already
// reads, so it stays the board's. A colour that is not a swatch (the option is a colour, a later picker may hand one in) takes the
// engine's own "one step darker" (`AccentRamp.Derive(c).Dark1`) as its step, floored the same way.
//
// `style.useWindowsAccent` replaces the swatch with the OS ramp the composition is handed (`IStyleLayer.Definition(values, osAccent)`).
//
// THE RAMP. The engine resolves its accent tokens from the definition's ramp theme-aware: the FILL is `Light2` in dark and `Dark1` in
// light, the TEXT `Light3` in dark and `Dark2` in light (`Tok.AccentFillShade`, `Tok.AccentTextPrimary`). The boards paint the swatch
// itself as fill AND as ink (play, the playing row, the switch), so Zune's ramp puts the dark swatch on every dark shade and the light
// swatch on every light shade the engine reads: `Tok.AccentDefault` is exactly the board's colour in both kinds.

using FluentGpu.Foundation;
using AccentRamp = FluentGpu.Dsl.AccentRamp;

namespace Wavee.Themes.Zune;

/// <summary>One accent of the Zune style: the colour on the dark ground, the FILL on the light ground, and the INK on the light ground.</summary>
/// <param name="Dark">The accent on the dark ground (the swatch as Z-Tokens draws it), as fill and as ink.</param>
/// <param name="Light">The accent on the light ground as FILL and as a word on the ground itself (the play disc, a selected chip, the progress
/// line, an accent ring, a link): the board's step, darkened in lightness to 4.5 : 1 on the ground (<see cref="ZuneAccent.Floored"/>). White on
/// it reads 4.9 : 1 or better.</param>
/// <param name="LightInk">The accent INK on the light ground, for accent-coloured text and thin glyphs: the fill darkened (one factor on all three
/// channels) to 4.5 : 1 on the ground, the well (#ebebeb), a selected row and its own wash. The board's step reads 3.3 to 4.2 : 1 as text.</param>
public readonly record struct ZuneAccentPair(ColorF Dark, ColorF Light, ColorF LightInk);

/// <summary>The accent swatches of the Zune style and the ramp the definition carries.</summary>
public static class ZuneAccent
{
    /// <summary>Pink, the default (Z-Tokens: #ff3ca6; the board's light step #e0197f floors to about #d51878; ink #bd156b).</summary>
    public static ZuneAccentPair Pink { get; } = Of(ColorF.FromRgba(0xFF, 0x3C, 0xA6), ColorF.FromRgba(0xE0, 0x19, 0x7F), ColorF.FromRgba(0xBD, 0x15, 0x6B));

    /// <summary>Orange (#ff7a1a; light step #d65a00 floors to about #bc4f00; ink #a94700).</summary>
    public static ZuneAccentPair Orange { get; } = Of(ColorF.FromRgba(0xFF, 0x7A, 0x1A), ColorF.FromRgba(0xD6, 0x5A, 0x00), ColorF.FromRgba(0xA9, 0x47, 0x00));

    /// <summary>Blue (#60cdff; light step #0a7fb5 floors to about #0977a9; ink #086b99).</summary>
    public static ZuneAccentPair Blue { get; } = Of(ColorF.FromRgba(0x60, 0xCD, 0xFF), ColorF.FromRgba(0x0A, 0x7F, 0xB5), ColorF.FromRgba(0x08, 0x6B, 0x99));

    /// <summary>Green (#8fd14f; light step #4f8f1a floors to about #457e17; ink #3e7115).</summary>
    public static ZuneAccentPair Green { get; } = Of(ColorF.FromRgba(0x8F, 0xD1, 0x4F), ColorF.FromRgba(0x4F, 0x8F, 0x1A), ColorF.FromRgba(0x3E, 0x71, 0x15));

    /// <summary>Purple (#b983ff; light step #8a4fd6, which already reads 4.6 : 1; ink #7e48c3).</summary>
    public static ZuneAccentPair Purple { get; } = Of(ColorF.FromRgba(0xB9, 0x83, 0xFF), ColorF.FromRgba(0x8A, 0x4F, 0xD6), ColorF.FromRgba(0x7E, 0x48, 0xC3));

    // A swatch: the board's dark swatch, the board's light step floored on the ground, and the ink.
    private static ZuneAccentPair Of(ColorF dark, ColorF boardStep, ColorF ink) => new(dark, Floored(boardStep), ink);

    /// <summary>The five swatches in the order the option lists them (Z-Theme-Options): pink, orange, blue, green, purple.</summary>
    public static IReadOnlyList<ZuneAccentPair> Swatches { get; } = [Pink, Orange, Blue, Green, Purple];

    /// <summary>The pair for the dark-ground colour <paramref name="swatch"/>: the board's light step for a swatch, the engine's one-step
    /// darker shade (<c>AccentRamp.Derive(c).Dark1</c>) for any other colour. Alpha is ignored (an accent is opaque).</summary>
    public static ZuneAccentPair PairOf(ColorF swatch)
    {
        var opaque = swatch with { A = 1f };
        foreach (var pair in Swatches)
            if (Same(pair.Dark, opaque)) return pair;
        var fill = Floored(AccentRamp.Derive(opaque).Dark1);
        return new ZuneAccentPair(opaque, fill, InkOf(fill));
    }

    /// <summary>The light-ground accent for the step <paramref name="step"/>: its lightness lowered (hue and saturation kept) until it reads
    /// <see cref="InkFloor"/> : 1 as text on the light ground. A step that already does is returned as it is.</summary>
    public static ColorF Floored(ColorF step)
    {
        var c = step with { A = 1f };
        var (h, s, l) = ColorScience.ToHsl(c);
        for (int i = 1; i <= 200 && ColorContrast.Ratio(c, ZuneTokens.LightGround) < InkFloor; i++)
            c = ColorScience.FromHsl(h, s, l - 0.005f * i);
        return c;
    }

    /// <summary>The ink of a light-ground accent FILL: the fill darkened by one factor until it reads <see cref="InkFloor"/> : 1 on the light plates
    /// accent text sits on (the selected row's grey, the accent's own wash over the ground). A fill that already does is returned as it is.</summary>
    public static ColorF InkOf(ColorF fill)
    {
        var plates = new[] { ZuneTokens.LightGround, ColorF.FromRgba(0xE2, 0xE2, 0xE2), ColorF.Lerp(ZuneTokens.LightGround, fill, 0.12f) };
        var ink = fill with { A = 1f };
        for (int step = 0; step < 200 && !Reads(ink, plates); step++)
        {
            float k = 1f - 0.005f * (step + 1);
            ink = new ColorF(fill.R * k, fill.G * k, fill.B * k, 1f);
        }
        return ink;
    }

    /// <summary>The contrast an accent word needs on a light plate.</summary>
    public const float InkFloor = 4.5f;

    private static bool Reads(ColorF ink, ColorF[] plates)
    {
        foreach (var p in plates) if (ColorContrast.Ratio(ink, p) < InkFloor) return false;
        return true;
    }

    /// <summary>The ramp the definition carries for <paramref name="pair"/>: the dark swatch on every shade the engine reads in dark
    /// (<c>Light1..3</c>, <c>Base</c>) and the light swatch on every shade it reads in light (<c>Dark1</c>, <c>Dark2</c>); <c>Dark3</c> one
    /// further step down. So <c>Tok.AccentDefault</c> and <c>Tok.AccentTextPrimary</c> are the board's colour in both kinds.</summary>
    public static AccentRamp RampOf(ZuneAccentPair pair) => new(
        Base: pair.Dark,
        Light1: pair.Dark, Light2: pair.Dark, Light3: pair.Dark,
        Dark1: pair.Light, Dark2: pair.LightInk, Dark3: AccentRamp.Derive(pair.LightInk).Dark1);

    // Byte-exact: the stored option is packed ARGB (8 bits a channel), so a swatch read back from the store compares equal.
    private static bool Same(ColorF a, ColorF b) => Byte(a.R) == Byte(b.R) && Byte(a.G) == Byte(b.G) && Byte(a.B) == Byte(b.B);

    private static int Byte(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
