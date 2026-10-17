// ── Themes/Zune/Style/ZuneAccent.cs — the accent of the Zune style: five swatches, a light step, the ramp (themes plan 6.3, Z1) ──
//
// `style.accent` is one of five swatches (Z-Tokens, Z-Theme-Options): pink (the default), orange, blue, green, purple. On the LIGHT
// ground the accent darkens one step (Z-Light-*: pink #ff3ca6 becomes #e0197f), so every swatch is a PAIR, light and dark, both
// written as the boards draw them. A colour that is not a swatch (the option is a colour, a later picker may hand one in) takes the
// engine's own "one step darker" (`AccentRamp.Derive(c).Dark1`) for its light arm.
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

/// <summary>One accent of the Zune style: the colour on the dark ground and the colour on the light ground.</summary>
/// <param name="Dark">The accent on the dark ground (the swatch as Z-Tokens draws it).</param>
/// <param name="Light">The accent on the light ground (one step darker, as Z-Light-* draws it).</param>
public readonly record struct ZuneAccentPair(ColorF Dark, ColorF Light);

/// <summary>The accent swatches of the Zune style and the ramp the definition carries.</summary>
public static class ZuneAccent
{
    /// <summary>Pink, the default (Z-Tokens: #ff3ca6; light #e0197f).</summary>
    public static ZuneAccentPair Pink { get; } = new(ColorF.FromRgba(0xFF, 0x3C, 0xA6), ColorF.FromRgba(0xE0, 0x19, 0x7F));

    /// <summary>Orange (#ff7a1a; light #d65a00).</summary>
    public static ZuneAccentPair Orange { get; } = new(ColorF.FromRgba(0xFF, 0x7A, 0x1A), ColorF.FromRgba(0xD6, 0x5A, 0x00));

    /// <summary>Blue (#60cdff; light #0a7fb5).</summary>
    public static ZuneAccentPair Blue { get; } = new(ColorF.FromRgba(0x60, 0xCD, 0xFF), ColorF.FromRgba(0x0A, 0x7F, 0xB5));

    /// <summary>Green (#8fd14f; light #4f8f1a).</summary>
    public static ZuneAccentPair Green { get; } = new(ColorF.FromRgba(0x8F, 0xD1, 0x4F), ColorF.FromRgba(0x4F, 0x8F, 0x1A));

    /// <summary>Purple (#b983ff; light #8a4fd6).</summary>
    public static ZuneAccentPair Purple { get; } = new(ColorF.FromRgba(0xB9, 0x83, 0xFF), ColorF.FromRgba(0x8A, 0x4F, 0xD6));

    /// <summary>The five swatches in the order the option lists them (Z-Theme-Options): pink, orange, blue, green, purple.</summary>
    public static IReadOnlyList<ZuneAccentPair> Swatches { get; } = [Pink, Orange, Blue, Green, Purple];

    /// <summary>The pair for the dark-ground colour <paramref name="swatch"/>: the board's light step for a swatch, the engine's one-step
    /// darker shade (<c>AccentRamp.Derive(c).Dark1</c>) for any other colour. Alpha is ignored (an accent is opaque).</summary>
    public static ZuneAccentPair PairOf(ColorF swatch)
    {
        var opaque = swatch with { A = 1f };
        foreach (var pair in Swatches)
            if (Same(pair.Dark, opaque)) return pair;
        return new ZuneAccentPair(opaque, AccentRamp.Derive(opaque).Dark1);
    }

    /// <summary>The ramp the definition carries for <paramref name="pair"/>: the dark swatch on every shade the engine reads in dark
    /// (<c>Light1..3</c>, <c>Base</c>) and the light swatch on every shade it reads in light (<c>Dark1</c>, <c>Dark2</c>); <c>Dark3</c> one
    /// further step down. So <c>Tok.AccentDefault</c> and <c>Tok.AccentTextPrimary</c> are the board's colour in both kinds.</summary>
    public static AccentRamp RampOf(ZuneAccentPair pair) => new(
        Base: pair.Dark,
        Light1: pair.Dark, Light2: pair.Dark, Light3: pair.Dark,
        Dark1: pair.Light, Dark2: pair.Light, Dark3: AccentRamp.Derive(pair.Light).Dark1);

    // Byte-exact: the stored option is packed ARGB (8 bits a channel), so a swatch read back from the store compares equal.
    private static bool Same(ColorF a, ColorF b) => Byte(a.R) == Byte(b.R) && Byte(a.G) == Byte(b.G) && Byte(a.B) == Byte(b.B);

    private static int Byte(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
