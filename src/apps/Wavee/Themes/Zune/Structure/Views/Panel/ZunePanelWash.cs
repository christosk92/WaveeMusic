// â”€â”€ Themes/Zune/Structure/Views/Panel/ZunePanelWash.cs â€” the side panel's cover glow (owner decision over Z-Part-Panel's flat plate) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
//
// The panel (docked or floating) keeps the board's flat plate as its body; the playing track's cover lights only its HEAD: a soft glow of the cover's
// hue behind the pivots and the transport-option row that eases out to nothing by `ZunePanelWashRules.GlowReach` of the panel's height, so the lyrics
// and the queue's rows sit on the board's black (light: its pale plate). The glow takes the cover's HUE (`ColorScience.Accent`, the most saturated
// graded role) at the rules' own saturation and value: a rich, darker tint at a low alpha, never the graded background role at a mid alpha (that is
// what washed the whole panel a flat grey-plum). It is ONE layer for the whole panel, so switching pivots (queue, lyrics, now playing, friends) never
// flashes. A new track mounts a fresh keyed layer that FADES IN over the old one (a gradient cannot brush-fade: the same recipe as the window's
// wash). With nothing playing, no art, no grading yet, a neutral (hueless) cover, or the background option at "none", there is no layer and the plate
// is the board's flat plate, unchanged.
//
// The panel's edge stays readable against the page by a 1-DIP divider down its left side, drawn only while the glow is.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>The panel surface's glow layers.</summary>
internal static class ZunePanelWash
{
    static readonly Element[] s_none = [];

    /// <summary>The layers drawn over the panel's plate for the cover <paramref name="url"/>: the glow and the left edge, or none. <paramref name="ink"/> is the
    /// ink the panel's text takes while the glow is drawn (null with no glow: the roles' own inks stand).</summary>
    public static Element[] Layers(string? url, out ZunePanelInk? ink)
    {
        ink = null;
        if (url is not { Length: > 0 } || ZuneOptions.Background() == ZuneBackground.None) return s_none;
        _ = Palette.Watch(url).Value;   // the grading may land late: this render re-runs when it does
        if (Design.SchemeFor(url) is not { } scheme || Glow(scheme, R.IsLight) is not { } peak) return s_none;
        ink = ZunePanelInk.For(peak);
        return
        [
            new BoxEl
            {
                // keyed on the colour: a new cover mounts a fresh glow that fades in over the old one
                Key = ZunePanelWashRules.WashKey + ":" + peak.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = Falloff(peak),
                Enter = ZuneBandRules.MaterialFade, Exit = ZuneBandRules.MaterialFade,
            },
            new BoxEl
            {
                Key = ZunePanelWashRules.EdgeKey, Width = ZunePanelWashRules.EdgeW, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                Fill = Tok.StrokeDividerDefault,
            },
        ];
    }

    /// <summary>The glow's colour at its peak (the panel's top) for a graded <paramref name="scheme"/>: the cover's hue at the kind's saturation, value and
    /// alpha; null for a hueless cover (a grey glow is only a lighter plate).</summary>
    internal static ColorF? Glow(in Scheme scheme, bool light)
    {
        var (h, s, _) = ColorScience.Accent(scheme).ToHsv();
        if (s <= ColorScience.NeutralS) return null;
        return ZunePanelWashRules.GlowOf(h, light);
    }

    /// <summary>The glow's fall from <paramref name="peak"/> at the top to nothing at <see cref="ZunePanelWashRules.GlowReach"/> of the panel (an ease-out: it
    /// holds over the head, then thins softly; the stops past the reach hold transparent, the plate shows).</summary>
    internal static GradientSpec Falloff(ColorF peak)
    {
        float reach = ZunePanelWashRules.GlowReach;
        var stops = ZunePanelWashRules.GlowStops;
        return new GradientSpec(GradientShape.Radial, 0f,
        [
            new GradientStop(0f, peak),
            new GradientStop(stops[0].At, peak with { A = peak.A * stops[0].Strength }),
            new GradientStop(stops[1].At, peak with { A = peak.A * stops[1].Strength }),
            new GradientStop(1f, peak with { A = 0f }),
        ])
        { RadialCenter = ZunePanelWashRules.GlowCentre, RadialRadius = new(ZunePanelWashRules.GlowWidth, reach) };
    }
}

/// <summary>The ink of the panel's text while its cover glow is drawn (provided to the panel's bodies by the frame; absent without a glow). The head's words and
/// the queue's first rows (the transport options, "playing from", the playing row) sit IN the glow, so the words that read tertiary take the secondary ink and
/// the accent lifts toward white (dark) or black (light) until it clears <see cref="ZuneFrameRules.PanelInkRatio"/> against the glow's peak over the plate (the
/// strongest tint: the worst case on the panel; below the glow the same inks read on the plain plate).</summary>
/// <param name="Tertiary">The ink for the words that read tertiary off the wash.</param>
/// <param name="Accent">The accent ink, lifted.</param>
internal sealed record ZunePanelInk(ColorF Tertiary, ColorF Accent)
{
    /// <summary>The ink in force in the panel's subtree.</summary>
    public static readonly FluentGpu.Hooks.Context<ZunePanelInk?> Current = new(null);

    /// <summary>The ink for a glow whose peak colour (translucent) lies over the panel's plate.</summary>
    public static ZunePanelInk For(ColorF head)
    {
        var plate = ColorContrast.Flatten(head, R.Layer.FloatingPane);
        return new ZunePanelInk(Tok.TextSecondary, Lifted(Tok.AccentTextPrimary, plate, ZuneFrameRules.PanelInkLiftToward(R.IsLight)));
    }

    /// <summary>The least lift of <paramref name="ink"/> toward <paramref name="toward"/> that reads <see cref="ZuneFrameRules.PanelInkRatio"/> on <paramref name="plate"/>.</summary>
    static ColorF Lifted(ColorF ink, ColorF plate, ColorF toward)
    {
        if (ColorContrast.Ratio(ink, plate) >= ZuneFrameRules.PanelInkRatio) return ink;
        float lo = 0f, hi = 1f;
        for (int i = 0; i < ZuneFrameRules.PanelInkLiftSteps; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (ColorContrast.Ratio(ColorF.Lerp(ink, toward, mid), plate) >= ZuneFrameRules.PanelInkRatio) hi = mid; else lo = mid;
        }
        return ColorF.Lerp(ink, toward, hi);
    }
}
