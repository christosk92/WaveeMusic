// ── Themes/Zune/Structure/Views/Panel/ZunePanelWash.cs — the side panel's cover-tinted wash (owner decision over Z-Part-Panel's flat plate) ──────────
//
// The panel (docked or floating) no longer sits on a flat black plate: the playing track's cover tints its whole surface, the same graded roles the
// shell material tints the window from (`R.Wash.ShellTintFromScheme`, graded for the theme's polarity so the panel's ink stays legible: dark stays dark,
// light stays pale), as a vertical wash that is strongest at the head (the pivots' row) and thins toward the foot. It is ONE material for the whole
// panel, so switching pivots (queue, lyrics, now playing, friends) never flashes. A new track mounts a fresh keyed layer that FADES IN over the old one
// (a gradient cannot brush-fade: the same recipe as the window's wash). With nothing playing, no art, no grading yet, or the background option at "none",
// there is no layer and the plate is the board's flat plate, unchanged.
//
// The panel's edge stays readable against the page by a 1-DIP divider down its left side, drawn only while the wash is.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>The panel surface's wash layers.</summary>
internal static class ZunePanelWash
{
    static readonly Element[] s_none = [];

    /// <summary>The layers drawn over the panel's plate for the cover <paramref name="url"/>: the wash and the left edge, or none. <paramref name="ink"/> is the
    /// ink the panel's text takes while the wash is drawn (null with no wash: the roles' own inks stand).</summary>
    public static Element[] Layers(string? url, out ZunePanelInk? ink)
    {
        ink = null;
        if (url is not { Length: > 0 } || ZuneOptions.Background() == ZuneBackground.None) return s_none;
        _ = Palette.Watch(url).Value;   // the grading may land late: this render re-runs when it does
        if (Design.SchemeFor(url) is not { } scheme) return s_none;
        ColorF tint, head, foot;
        if (R.IsLight)
        {
            // light: the shell tint is a faint smear by design (it sits under the chrome); the panel is a plate the page's dark ink sits on, so it takes the PALE
            // half of the grading (graded for a light page) at a visible strength
            tint = ColorScience.ToColor(scheme.BackgroundTintedBase);
            head = tint with { A = ZunePanelWashRules.LightHeadA };
            foot = tint with { A = ZunePanelWashRules.LightFootA };
        }
        else
        {
            tint = R.Wash.ShellTintFromScheme(scheme, rich: false);
            head = tint with { A = MathF.Min(1f, tint.A * ZunePanelWashRules.HeadScale) };
            foot = tint with { A = MathF.Min(1f, tint.A * ZunePanelWashRules.FootScale) };
        }
        ink = ZunePanelInk.For(head);
        return
        [
            new BoxEl
            {
                // keyed on the colour: a new cover mounts a fresh wash that fades in over the old one
                Key = ZunePanelWashRules.WashKey + ":" + tint.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = GradientDown(new GradientStop(0f, head), new GradientStop(1f, foot)),
                Enter = ZuneBandRules.MaterialFade, Exit = ZuneBandRules.MaterialFade,
            },
            new BoxEl
            {
                Key = ZunePanelWashRules.EdgeKey, Width = ZunePanelWashRules.EdgeW, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                Fill = Tok.StrokeDividerDefault,
            },
        ];
    }
}

/// <summary>The ink of the panel's text while its cover wash is drawn (provided to the panel's bodies by the frame; absent without a wash). The roles' own inks
/// were measured on the wash: the tertiary words (the inactive pivots, "next up") 2.7 - 2.9 : 1 dark and 4.2 light, the accent words (the playing row, "autoplay") 3.7
/// dark and 4.1 light, so ON the wash the tertiary words take the secondary ink and the accent lifts toward white (dark) or black (light) until it clears
/// <see cref="ZuneFrameRules.PanelInkRatio"/> against the wash's head colour over the plate (the head is the strongest tint: the worst case on the panel).</summary>
/// <param name="Tertiary">The ink for the words that read tertiary off the wash.</param>
/// <param name="Accent">The accent ink, lifted.</param>
internal sealed record ZunePanelInk(ColorF Tertiary, ColorF Accent)
{
    /// <summary>The ink in force in the panel's subtree.</summary>
    public static readonly FluentGpu.Hooks.Context<ZunePanelInk?> Current = new(null);

    /// <summary>The ink for a wash whose head colour (translucent) lies over the panel's plate.</summary>
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

/// <summary>The panel wash's numbers: the tint's strength at the panel's head and foot (times the shell tint's own alpha), the edge.</summary>
public static class ZunePanelWashRules
{
    public const string WashKey = "zune-panel-wash", EdgeKey = "zune-panel-edge";

    public const float HeadScale = 2.5f, FootScale = 1f, EdgeW = 1f;

    /// <summary>Light: the graded pale background at this strength over the plate, at the head and the foot.</summary>
    public const float LightHeadA = 0.5f, LightFootA = 0.2f;

}
