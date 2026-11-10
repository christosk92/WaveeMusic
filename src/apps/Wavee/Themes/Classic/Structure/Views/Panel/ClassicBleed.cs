// ── Themes/Classic/Structure/Views/Panel/ClassicBleed.cs — Classic's edge-to-edge canvas ───────────────────────────────────────────────
//
// The Canvas player style at rest fills the panel from its very top (`NowPlayingHeroProps.BleedVideo`, core) and dissolves into the panel:
// over its last stretch the picture fades, on an eased curve, into the panel's own plain ground, the same ground the panel shows with the
// canvas off (the owner, 2026-11-08: "theres too much color. it should just be plain"). Nothing moves there and nothing is tinted by the art
// (the owner, 2026-11-10: "get rid of the wave effect, just fade it beautifully"): the art lives in the picture and in the meta's accent text.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The colours of the canvas's ending and its meta: the plain panel <paramref name="Ground"/> the picture fades into, and the meta's
/// accent text (<paramref name="Text"/>: the artist, the heart).</summary>
internal readonly record struct ClassicBleedInk(ColorF Ground, ColorF Text)
{
    /// <summary>The colours for the cover at <paramref name="url"/>. The ground is the panel's own (the content layer over the shell's ground,
    /// flattened: what the panel shows with the canvas off). The accent text is the cover's dominant hue (its grading, the house palette)
    /// solved as text on that ground; a cover with no grading yet reads in the neutral ink. The palette watch is read inside, so a late
    /// grading re-renders the reader.</summary>
    public static ClassicBleedInk Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        bool light = R.IsLight;
        ColorF ground = ColorContrast.Flatten(R.Layer.FileArea, R.Layer.ShellGround);
        ColorF dominant = ColorContrast.Flatten(Tok.TextSecondary, ground);   // the neutral ink, opaque (the solve keeps its alpha)
        if (url is { Length: > 0 } && (Palette.TryScheme(url, light, out Scheme s) || Palette.TryScheme(url, !light, out s)))
            dominant = ColorScience.Accent(s);
        // The accent text: the dominant hue solved as text on the ground, a step past AA (the rendered glyphs blend a little under the solve).
        ColorF text = ColorScience.Hairline(dominant, light ? ThemeKind.Light : ThemeKind.Dark, ground, ClassicHeroLayout.ArtTextContrast);
        if (!ColorContrast.MeetsAaText(text, ground)) text = ColorContrast.PickContrast(ground);
        return new ClassicBleedInk(ground, text);
    }
}

internal sealed record ClassicBleedProps(NowPlayingHeroProps NowPlaying, float Width);

/// <summary>The edge-to-edge canvas with its fade (a component: the ground follows the theme without re-rendering the panel).</summary>
internal sealed class ClassicBleed : Component
{
    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    public override Element Render()
    {
        var p = UseProps<ClassicBleedProps>();
        var np = p.NowPlaying;
        float w = MathF.Max(1f, p.Width);
        float vh = MathF.Round(w * ClassicHeroLayout.BleedAspect), ph = np.RailHeight.Value;
        // Where the picture has fully given way to the ground: the meta starts BleedMetaDrop under it (ClassicNowPlayingHero).
        float end = ClassicHeroLayout.BleedEnd(vh, ph);
        float fade = MathF.Min(end, ClassicHeroLayout.BleedFadeH), half = MathF.Round(0.5f * fade);
        var ink = ClassicBleedInk.Of(np.NowArtUrl());
        ColorF ground = ink.Ground, shade = Deck.BleedShade(ground);
        return Flow.Show(np.Bleeds, new BoxEl
        {
            // Shrink, no minimum and a clip: a canvas taller than the panel must not grow the panel's stack (it is cut at the panel's foot).
            ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, HitTestVisible = false,
            Enter = Fade, Exit = Fade, Transition = MotionTok.StandardEnter,
            Children =
            [
                new BoxEl { Grow = 1f, Fill = ground },
                np.BleedVideo(w),
                // The dissolve: the ground's alpha rises on an eased curve (slow off the picture, slow into the ground), so neither end
                // of the fade shows an edge. Two gradients, the curve's two halves (a gradient holds four stops).
                Half(ground, end - fade, half, ClassicHeroLayout.BleedFadeA0, ClassicHeroLayout.BleedFadeA1, ClassicHeroLayout.BleedFadeA2, ClassicHeroLayout.BleedFadeA3),
                Half(ground, end - fade + half, fade - half, ClassicHeroLayout.BleedFadeA3, ClassicHeroLayout.BleedFadeA4, ClassicHeroLayout.BleedFadeA5, ClassicHeroLayout.BleedFadeA6),
                // Under the fade the plain ground covers the canvas's last stretch (and the panel below it), as the panel is with the canvas off.
                new BoxEl { AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, end, 0f, 0f), Fill = ground },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Height = ClassicHeroLayout.BleedHeadScrimH,
                    Gradient = Ui.GradientDown(new GradientStop(0f, shade with { A = ClassicHeroLayout.BleedHeadScrimA }), new GradientStop(1f, shade with { A = 0f })),
                },
            ],
        });
    }

    /// <summary>One half of the fade: <paramref name="h"/> tall from <paramref name="top"/>, the ground at four alphas a third apart.</summary>
    static Element Half(ColorF ground, float top, float h, float a0, float a1, float a2, float a3) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, top, 0f, 0f), Height = h, HitTestVisible = false,
        Gradient = Ui.GradientDown(
            new GradientStop(0f, ground with { A = a0 }), new GradientStop(ClassicHeroLayout.BleedFadeThird, ground with { A = a1 }),
            new GradientStop(ClassicHeroLayout.BleedFadeTwoThirds, ground with { A = a2 }), new GradientStop(1f, ground with { A = a3 })),
    };
}
