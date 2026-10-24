// ── Themes/WinUi/Structure/Rules/WinUiSetupRecolor.cs — the wizard's Lottie hero recolour (themes plan 6.1, T11c, P13) ──────────────
//
// What `Screens\Setup.cs` held as `Setup.Recolor`: the authored blue of the Rise heroes mapped onto the live accent. The authored colours and the hue band are the picture's, so the rule is
// Classic's (a colour built from components may live only under `Rules\**`, WVTH003). Pinned by `Wavee.Tests\SetupWizardTests.cs`.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The Lottie hero recolour: exact #0078D4 → the accent, exact #002B67 → the deep accent, the H 195–285° /
/// S ≥ .35 blue-violet band hue-rotated by the accent's own delta, everything else as authored. Alpha is always the
/// shape's own. The hero applies it once per mount (a theme change does not repaint a mounted hero — 0.2.9's rule).</summary>
public static class WinUiSetupRecolor
{
    private static readonly ColorF SourceAccent = ColorF.FromRgba(0x00, 0x78, 0xD4);
    private static readonly ColorF SourceDeep = ColorF.FromRgba(0x00, 0x2B, 0x67);
    private static readonly float SourceAccentHue = ColorScience.ToHsl(SourceAccent).H;

    private const float HueBandMin = 195f, HueBandMax = 285f, HueBandMinSaturation = 0.35f;

    /// <summary>Generous enough for a byte/255 round trip, tight enough that the band's nearest neighbour (#2741AB) never
    /// qualifies as exact.</summary>
    private const float ExactTolerance = 1f / 64f;

    /// <summary>The recoloured <paramref name="c"/> for the live <paramref name="accent"/> and its deep partner.</summary>
    public static ColorF Apply(ColorF c, ColorF accent, ColorF accentDeep)
    {
        if (IsClose(c, SourceAccent)) return accent with { A = c.A };
        if (IsClose(c, SourceDeep)) return accentDeep with { A = c.A };
        var (h, s, l) = ColorScience.ToHsl(c);
        if (s < HueBandMinSaturation || h < HueBandMin || h > HueBandMax) return c;
        return ColorScience.FromHsl(h + (ColorScience.ToHsl(accent).H - SourceAccentHue), s, l, c.A);
    }

    private static bool IsClose(ColorF a, ColorF b) =>
        MathF.Abs(a.R - b.R) <= ExactTolerance && MathF.Abs(a.G - b.G) <= ExactTolerance && MathF.Abs(a.B - b.B) <= ExactTolerance;
}
