// ── Themes/Classic/Structure/Views/Panel/ClassicHeroWash.cs — the cover-derived wash Classic's panel paints ─────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.Classic;

/// <summary>THE hero wash: one function for the now-playing hero's cover placeholder and the deck flyout's "Album colour" swatch, so the
/// two can never grade the artwork differently. The palette watch is read INSIDE, so a late grading repaints a bound placeholder
/// without re-rendering anything. With Classic's "Now playing colours" off (CT9 U3) the theme accent stands in for the cover's (a tracked
/// read, so a flip repaints a bound placeholder).</summary>
internal static class ClassicHeroWash
{
    public static ColorF Of(string? url)
    {
        bool fromCover = ClassicOptions.NowPlayingColors();
        if (fromCover && url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = fromCover && Design.SchemeFor(url) is { } s ? ColorScience.Lift(ColorScience.Accent(s)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, R.Wash.CoverMix);
    }
}
