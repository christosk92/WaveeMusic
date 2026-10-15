// ── Themes/Classic/Structure/Views/Panel/ClassicHeroWash.cs — the cover-derived wash Classic's panel paints ─────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.Classic;

/// <summary>THE hero wash: one function for the now-playing hero's cover placeholder and the deck flyout's "Album colour" swatch, so the
/// two can never grade the artwork differently. The palette watch is read INSIDE, so a late grading repaints a bound placeholder
/// without re-rendering anything.</summary>
internal static class ClassicHeroWash
{
    public static ColorF Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}
