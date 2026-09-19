using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The Lottie hero recolour rule (plan §7 / <see cref="WaveeLottie.Options"/>'s <c>Recolor</c> seam): the
/// Windows-11-OOBE blue/violet family the three Rise assets paint their accent with is repainted to Wavee's live
/// accent tokens; everything else — the neutrals, the teal decoration — is left exactly as authored.
///
/// <para>Pure by construction (<see cref="ColorF"/> + <see cref="System"/> only, plus the already engine-free
/// <c>WaveePalette.ToHsl</c>/<c>FromHsl</c> pair this file shares with <c>WaveePalette.PageTone</c>), so
/// <c>WaveeLottieRecolorTests</c> can pin the rule against the real testable overload
/// (<see cref="Apply(ColorF, ColorF, ColorF)"/>) without a window, a theme or the engine's live <see cref="Tok"/>
/// state.</para></summary>
static class WaveeLottieRecolor
{
    // The two literal source colours the assets paint their accent family with (verified against the JSON): the
    // monitor/phone/arc/badge/shield/bytes/package/verify strokes are the exact Windows accent #0078D4; Patch's
    // package-drop layer is the exact deep navy #002B67.
    static readonly ColorF SourceAccent = ColorF.FromRgba(0x00, 0x78, 0xD4);
    static readonly ColorF SourceDeep = ColorF.FromRgba(0x00, 0x2B, 0x67);
    static readonly float SourceAccentHue = WaveePalette.ToHsl(SourceAccent).H;

    // The wider blue-violet family (the gradient mid-stops #6139DA/#5B41D3, Eula's #2741AB) that rides along with
    // the two exact colours above but isn't identical to either — hue-rotated instead of replaced outright so its
    // own internal contrast (it's never JUST the flat accent) survives the swap.
    const float HueBandMin = 195f, HueBandMax = 285f;
    const float HueBandMinSaturation = 0.35f;

    // Exact-match slack for the two literal source colours: generous enough to absorb a byte/255f round-trip
    // through whichever JSON→float path fed this colour, tight enough that nothing in the wider hue band spuriously
    // qualifies (that band's nearest neighbour, Eula's #2741AB, is ~0.09 away in the R channel alone).
    const float ExactTolerance = 1f / 64f;

    /// <summary>The live seam <see cref="WaveeLottie.Options"/> installs — reads the app's CURRENT accent tokens at
    /// call time. <see cref="LottieView"/> calls <c>Recolor</c> once per mount (not per frame), so re-reading
    /// <see cref="Tok"/> here is cheap and correctness (today's theme/accent, not a stale one) matters more.</summary>
    public static ColorF Apply(ColorF c) => Apply(c, Tok.AccentDefault, Tok.AccentTextPrimary);

    /// <summary>The testable core. <paramref name="accent"/> replaces the exact <c>#0078D4</c> source outright;
    /// <paramref name="accentDeep"/> replaces the exact <c>#002B67</c> navy outright; the wider blue-violet hue band
    /// (H 195–285°, S ≥ .35) is hue-rotated by <paramref name="accent"/>'s own hue delta from <c>#0078D4</c> while
    /// its saturation and lightness are kept as authored. Every other colour (neutrals, the teal) is returned
    /// unchanged. Alpha is always taken from <paramref name="c"/>, never from <paramref name="accent"/>/
    /// <paramref name="accentDeep"/> — a fill's opacity is the shape's own, not the token's.</summary>
    public static ColorF Apply(ColorF c, ColorF accent, ColorF accentDeep)
    {
        if (IsClose(c, SourceAccent)) return accent with { A = c.A };
        if (IsClose(c, SourceDeep)) return accentDeep with { A = c.A };

        var (h, s, l) = WaveePalette.ToHsl(c);
        if (s < HueBandMinSaturation || h < HueBandMin || h > HueBandMax) return c;

        float accentHue = WaveePalette.ToHsl(accent).H;
        return WaveePalette.FromHsl(h + (accentHue - SourceAccentHue), s, l, c.A);
    }

    static bool IsClose(ColorF a, ColorF b) =>
        MathF.Abs(a.R - b.R) <= ExactTolerance && MathF.Abs(a.G - b.G) <= ExactTolerance && MathF.Abs(a.B - b.B) <= ExactTolerance;
}
