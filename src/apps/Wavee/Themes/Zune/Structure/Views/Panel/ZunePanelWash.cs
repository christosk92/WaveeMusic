// ── Themes/Zune/Structure/Views/Panel/ZunePanelWash.cs — the side panel's surface: design "B, one surface" (owner decision over Z-Part-Panel's flat plate) ──
//
// The panel draws no plate of its own where it docks. The PAGE's ground and wash (the material layer's cover tint from the window's top, `ZunePageWash`) run
// on under it, so the page and the panel read as one sheet: the panel's words float on the page as the library reader's do (the owner's variant B,
// 2026-10-24: no scrim, no cover glow, no divider).
//
//   DOCKED    open: the panel is transparent and nothing is painted for it (no full-height surface): the title bar's bell, window buttons and the end of
//             the search pill sit on the page's ground as everywhere. Sliding in or out it paints the SAME picture as a card (the ground, and the page
//             wash below its top edge, so the colour lines up with the page beside it), so a slide never flashes another look.
//   CARD      floating or the Compact sheet: the panel floats over content, so it keeps a card's surface, opaque: the ground, the page wash below its own
//             top edge, a soft scrim (`ZunePanelWashRules.ScrimOf`), the playing cover's glow at the head (the cover's HUE at the rules' saturation and
//             value, easing out by `ZunePanelWashRules.GlowReach` of the panel's height; a hueless cover, nothing playing or no grading: none) and a
//             1-DIP divider down its left edge. The background option at "none" leaves the ground under the scrim.
//
// The glow is ONE layer per surface, so switching pivots never flashes; a new track mounts a fresh keyed layer that FADES IN over the old one (a gradient
// cannot brush-fade: the same recipe as the window's wash). The head's inks are judged against what lies under the head (`HeadColour`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>The panel surface's layers.</summary>
internal static class ZunePanelWash
{
    /// <summary>The cover glow for <paramref name="url"/> (null: no glow) and its peak colour.</summary>
    public static Element? Glow(string? url, out ColorF? peak)
    {
        peak = null;
        if (url is not { Length: > 0 } || ZuneOptions.Background() == ZuneBackground.None) return null;
        _ = Palette.Watch(url).Value;   // the grading may land late: this render re-runs when it does
        if (Design.SchemeFor(url) is not { } scheme || Glow(scheme, R.IsLight) is not { } p) return null;
        peak = p;
        return new BoxEl
        {
            // keyed on the colour: a new cover mounts a fresh glow that fades in over the old one
            Key = ZunePanelWashRules.WashKey + ":" + p.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
            Gradient = Falloff(p),
            Enter = ZuneBandRules.MaterialFade, Exit = ZuneBandRules.MaterialFade,
        };
    }

    /// <summary>The scrim over the page wash (stretches over its surface).</summary>
    public static Element Scrim() => new BoxEl
    {
        Key = ZunePanelWashRules.ScrimKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
        Fill = ZunePanelWashRules.ScrimOf(R.IsLight),
    };

    /// <summary>The 1-DIP divider down the surface's left edge.</summary>
    public static Element Edge() => new BoxEl
    {
        Key = ZunePanelWashRules.EdgeKey, Width = ZunePanelWashRules.EdgeW, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start, HitTestVisible = false,
        Fill = Tok.StrokeDividerDefault,
    };

    /// <summary>A card's own opaque surface whose top edge lies <paramref name="top"/> DIP below the window's top: the ground, the page wash below that line,
    /// and where it floats (<paramref name="plate"/>: floating, the sheet) the scrim, the glow and the edge; a docked panel sliding in or out paints the ground
    /// and the wash alone, the page it is part of.</summary>
    public static Element[] Card(ZunePageWash? wash, float top, Element? glow, bool plate)
    {
        var kids = new List<Element>(5)
        {
            new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false, Fill = R.Layer.ShellGround },
        };
        if (wash?.Below(top) is { } part)
            kids.Add(new BoxEl
            {
                // The card's top moves on a crossing (floating under the band, docked from the window's top: the 1008 edge) while the host's top spacer
                // eases (`ZunePanelTiers.TopMotion`): the wash's slot eases its height with it. The keyed wash inside cross-fades on a cover change,
                // as before (an enter on the slot itself would grow every new wash from nothing).
                Key = ZunePanelWashRules.CardWashKey, Height = part.Height, AlignSelf = FlexAlign.Start, ZStack = true, HitTestVisible = false,
                Animate = Design.Reduced ? null : ZunePanelTiers.TopMotion,
                Children =
                [
                    new BoxEl
                    {
                        Key = ZunePanelWashRules.CardWashKey + ":" + wash.Tint.GetHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = GradientDown(part.Stops),
                        Enter = ZuneBandRules.MaterialFade, Exit = ZuneBandRules.MaterialFade,
                    },
                ],
            });
        if (!plate) return kids.ToArray();
        kids.Add(Scrim());
        if (glow is not null) kids.Add(glow.Value);
        kids.Add(Edge());
        return kids.ToArray();
    }

    /// <summary>The surface's colour where the panel's head lies (its strongest tint: the worst case for the inks): the page wash at <paramref name="y"/> DIP
    /// below the window's top over the ground, then, on a floating card's plate (<paramref name="plate"/>), the scrim and the glow's peak. Docked, the head
    /// lies on the page itself.</summary>
    public static ColorF HeadColour(ZunePageWash? wash, float y, ColorF? glow, bool plate)
    {
        var c = R.Layer.ShellGround;
        if (wash is not null) c = ColorContrast.Flatten(wash.At(y), c);
        if (!plate) return c;
        c = ColorContrast.Flatten(ZunePanelWashRules.ScrimOf(R.IsLight), c);
        if (glow is { } g) c = ColorContrast.Flatten(g, c);
        return c;
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
    /// holds over the head, then thins softly; the stops past the reach hold transparent, the surface shows).</summary>
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

/// <summary>The ink of the panel's text on its surface (provided to the panel's bodies by the frame). The head's words and the queue's first rows (the
/// transport options, "playing from", the playing row) sit where the surface is most tinted (the page wash's top, the glow's peak), so the words that read
/// tertiary take the secondary ink and the accent lifts toward white (dark) or black (light) until it clears <see cref="ZuneFrameRules.PanelInkRatio"/>
/// against that colour (the worst case on the panel; below it the same inks read on the plainer surface).</summary>
/// <param name="Tertiary">The ink for the words that read tertiary off the wash.</param>
/// <param name="Accent">The accent ink, lifted.</param>
internal sealed record ZunePanelInk(ColorF Tertiary, ColorF Accent)
{
    /// <summary>The ink in force in the panel's subtree.</summary>
    public static readonly FluentGpu.Hooks.Context<ZunePanelInk?> Current = new(null);

    /// <summary>The ink for the panel whose surface reads <paramref name="head"/> (opaque) where its head lies.</summary>
    public static ZunePanelInk For(ColorF head)
        => new(Tok.TextSecondary, Lifted(Tok.AccentTextPrimary, head, ZuneFrameRules.PanelInkLiftToward(R.IsLight)));

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
