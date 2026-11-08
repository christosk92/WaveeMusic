// ── Themes/Classic/Structure/Views/Panel/ClassicBleed.cs — "Tideline": Classic's edge-to-edge canvas ───────────────────────────────────
//
// The Canvas player style at rest fills the panel from its very top (`NowPlayingHeroProps.BleedVideo`, core) and ends, instead of a straight
// fade, in a slowly moving wave: three swells drift at different speeds over the canvas's lower part (two in the art's colours, the cover's
// dominant hue behind its second one, and the front one in the panel's own ground with a crest line in the art's hue). Under the crest the
// panel is PLAIN, the same ground it shows with the canvas off (the owner, 2026-11-08: "theres too much color. it should just be plain and
// also the waves are not colored from the album art"): the art lives in the waves, the meta's accents and the cover's glow, never in a wash.
// No brand green anywhere. Board: canvas-design/project/BleedClassic.dc.html (its generator's `wave()` is the geometry below). Reduced
// motion: the swells stand still (`Deck.Drift`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The colours of the canvas's ending and its meta: the plain panel <paramref name="Ground"/>, the back and middle swells in the art's
/// two colours (<paramref name="Back"/>, <paramref name="Mid"/>), the crest line (<paramref name="Crest"/>) and the meta's accent text
/// (<paramref name="Text"/>: the artist, the heart).</summary>
internal readonly record struct ClassicBleedInk(ColorF Ground, ColorF Back, ColorF Mid, ColorF Crest, ColorF Text)
{
    /// <summary>The colours for the cover at <paramref name="url"/>. The ground is the panel's own (the content layer over the shell's ground,
    /// flattened: what the panel shows with the canvas off). The art's two colours come from the cover's grading (the house palette): its
    /// dominant hue (<see cref="ColorScience.Accent(in Scheme)"/>) and its second one (the most saturated other role a hue apart, else the
    /// dominant again), each at its own saturation (capped) and at the kind's swell lightness, so the waves sit on the plain panel in both kinds;
    /// the crest line and the accent text are the dominant hue solved against the ground. A cover with no grading yet paints the waves in the
    /// neutral ink. The palette watch is read inside, so a late grading re-renders the reader.</summary>
    public static ClassicBleedInk Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        bool light = R.IsLight;
        ColorF ground = ColorContrast.Flatten(R.Layer.FileArea, R.Layer.ShellGround);
        ColorF dominant = ColorContrast.Flatten(Tok.TextSecondary, ground), second = dominant;   // the neutral ink, opaque (the solves keep its alpha)
        if (url is { Length: > 0 } && (Palette.TryScheme(url, light, out Scheme s) || Palette.TryScheme(url, !light, out s)))
            (dominant, second) = ArtPair(s);
        ThemeKind kind = light ? ThemeKind.Light : ThemeKind.Dark;
        ColorF back = Swell(dominant, light ? ClassicHeroLayout.WaveBackLightL : ClassicHeroLayout.WaveBackDarkL);
        ColorF mid = Swell(second, light ? ClassicHeroLayout.WaveMidLightL : ClassicHeroLayout.WaveMidDarkL);
        // The accent text: the dominant hue solved as text on the ground, a step past AA (the rendered glyphs blend a little under the solve).
        ColorF text = ColorScience.Hairline(dominant, kind, ground, ClassicHeroLayout.ArtTextContrast);
        if (!ColorContrast.MeetsAaText(text, ground)) text = ColorContrast.PickContrast(ground);
        return new ClassicBleedInk(ground, back, mid, ColorScience.Hairline(dominant, kind, ground), text);
    }

    /// <summary>The cover's dominant hue and its second one: of the grading's other roles, the most saturated whose hue stands at least
    /// <see cref="ClassicHeroLayout.WaveHueApart"/> degrees from the dominant one; the dominant again when none does (a one-hue cover).</summary>
    static (ColorF Dominant, ColorF Second) ArtPair(in Scheme s)
    {
        ColorF dominant = ColorScience.Accent(s), second = dominant;
        var (dh, ds, _) = dominant.ToHsv();
        if (ds <= ColorScience.NeutralS) return (dominant, second);   // a hueless cover has no second hue either
        float best = ColorScience.NeutralS;
        Span<uint> roles = [s.BackgroundTintedBase, s.BackgroundBase, s.TextSubdued, s.TextBrightAccent];
        foreach (uint role in roles)
        {
            ColorF c = ColorScience.ToColor(role);
            var (h, sat, _) = c.ToHsv();
            float apart = MathF.Abs(h - dh);
            apart = MathF.Min(apart, 360f - apart);
            if (sat > best && apart >= ClassicHeroLayout.WaveHueApart) { best = sat; second = c; }
        }
        return (dominant, second);
    }

    /// <summary>A swell's colour: <paramref name="art"/>'s hue at its own saturation (capped at <see cref="ClassicHeroLayout.WaveSatMax"/>) and
    /// the lightness <paramref name="l"/>.</summary>
    static ColorF Swell(ColorF art, float l)
    {
        var (h, sat, _) = ColorScience.ToHsl(art);
        return ColorScience.FromHsl(h, MathF.Min(sat, ClassicHeroLayout.WaveSatMax), l);
    }
}

internal sealed record ClassicBleedProps(NowPlayingHeroProps NowPlaying, float Width);

/// <summary>The edge-to-edge canvas with its tide (a component: the art's colours follow the playing track without re-rendering the panel).</summary>
internal sealed class ClassicBleed : Component
{
    static readonly EnterExit Fade = new(Opacity: 0f, Active: true);

    // The swells' geometry, rebuilt only when the panel's width changes.
    float _builtFor = float.NaN;
    PathData? _back, _mid, _front, _crest;

    public override Element Render()
    {
        var p = UseProps<ClassicBleedProps>();
        var np = p.NowPlaying;
        float w = MathF.Max(1f, p.Width);
        float vh = MathF.Round(w * ClassicHeroLayout.BleedAspect), ph = np.RailHeight.Value;
        float crest = MathF.Min(vh - ClassicHeroLayout.BleedCrestLift, (ph > 0f ? ph : vh) - ClassicHeroLayout.BleedCrestRoom);
        float band = ClassicHeroLayout.BleedWaveH, top = crest - 0.5f * band;
        var ink = ClassicBleedInk.Of(np.NowArtUrl());
        // Over the canvas the scrims darken toward the media scope's shade (Deck.Bleed.cs); the front swell ends it in the plain ground.
        ColorF shade = Deck.BleedShade(ink.Ground);
        if (w != _builtFor)
        {
            _builtFor = w;
            _back = Swell(w, ClassicHeroLayout.WaveBackAmp, ClassicHeroLayout.WaveBackBase, ClassicHeroLayout.WaveBackPeriods, closed: true);
            _mid = Swell(w, ClassicHeroLayout.WaveMidAmp, ClassicHeroLayout.WaveMidBase, ClassicHeroLayout.WaveMidPeriods, closed: true);
            _front = Swell(w, ClassicHeroLayout.WaveFrontAmp, ClassicHeroLayout.WaveFrontBase, ClassicHeroLayout.WaveFrontPeriods, closed: true);
            _crest = Swell(w, ClassicHeroLayout.WaveFrontAmp, ClassicHeroLayout.WaveFrontBase, ClassicHeroLayout.WaveFrontPeriods, closed: false);
        }
        return Flow.Show(np.Bleeds, new BoxEl
        {
            // Shrink, no minimum and a clip: a canvas taller than the panel must not grow the panel's stack (it is cut at the panel's foot).
            ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, HitTestVisible = false,
            Enter = Fade, Exit = Fade, Transition = MotionTok.StandardEnter,
            Children =
            [
                new BoxEl { Grow = 1f, Fill = ink.Ground },
                np.BleedVideo(w),
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, MathF.Max(0f, crest - ClassicHeroLayout.BleedDarkBand), 0f, 0f),
                    Height = MathF.Min(crest, ClassicHeroLayout.BleedDarkBand),
                    Gradient = Ui.GradientDown(new GradientStop(0f, shade with { A = 0f }), new GradientStop(1f, shade with { A = ClassicHeroLayout.BleedDarkA })),
                },
                Layer(w, top, ClassicHeroLayout.WaveBackPeriods, ClassicHeroLayout.WaveBackMs,
                    new PathEl { Geometry = _back, Fill = ink.Back with { A = ClassicHeroLayout.WaveBackA }, Width = SwellWidth(w, ClassicHeroLayout.WaveBackPeriods), Height = band }),
                Layer(w, top, ClassicHeroLayout.WaveMidPeriods, ClassicHeroLayout.WaveMidMs,
                    new PathEl { Geometry = _mid, Fill = ink.Mid with { A = ClassicHeroLayout.WaveMidA }, Width = SwellWidth(w, ClassicHeroLayout.WaveMidPeriods), Height = band }),
                Layer(w, top, ClassicHeroLayout.WaveFrontPeriods, ClassicHeroLayout.WaveFrontMs, new BoxEl
                {
                    ZStack = true, Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band,
                    Children =
                    [
                        new PathEl { Geometry = _front, Fill = ink.Ground, Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band },
                        new PathEl
                        {
                            Geometry = _crest, StrokeColor = ink.Crest with { A = ClassicHeroLayout.BleedCrestA }, Stroke = new StrokeStyle(ClassicHeroLayout.BleedCrestLine),
                            Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band,
                        },
                    ],
                }),
                // Under the swells the plain ground covers the canvas's last stretch (and the panel below it), as the panel is with the canvas off.
                new BoxEl { AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, top + band - 1f, 0f, 0f), Fill = ink.Ground },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Height = ClassicHeroLayout.BleedHeadScrimH,
                    Gradient = Ui.GradientDown(new GradientStop(0f, shade with { A = ClassicHeroLayout.BleedHeadScrimA }), new GradientStop(1f, shade with { A = 0f })),
                },
            ],
        });
    }

    /// <summary>A swell's band at the crest, drifting a panel's width per <paramref name="ms"/> (its geometry repeats every period, so the loop
    /// is seamless).</summary>
    static Element Layer(float w, float top, float periods, float ms, Element swell) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, Direction = 0, Margin = new Edges4(0f, top, 0f, 0f), MinWidth = 0f, HitTestVisible = false,
        Children = [Deck.Drift(swell, w / periods, ms / periods, pingPong: false)],
    };

    /// <summary>A swell is drawn over the panel's width and one more, so the drift of one period never shows its end.</summary>
    static float SwellWidth(float w, float periods) => w + w / periods;

    /// <summary>The generator's wave: <paramref name="periods"/> sine-like periods per panel width (a cubic rise and fall each), baseline
    /// <paramref name="baseline"/> in the 140 band; closed down to the band's foot for a fill, open along the crest for the line.</summary>
    static PathData Swell(float w, float amp, float baseline, float periods, bool closed)
    {
        float period = w / periods, end = SwellWidth(w, periods), h = ClassicHeroLayout.BleedWaveH;
        var b = new PathBuilder();
        b.MoveTo(0f, baseline);
        for (float x = 0f; x < end - 0.5f; x += period)
        {
            b.CubicTo(x + 0.25f * period, baseline - amp, x + 0.25f * period, baseline - amp, x + 0.5f * period, baseline);
            b.CubicTo(x + 0.75f * period, baseline + amp, x + 0.75f * period, baseline + amp, x + period, baseline);
        }
        if (closed)
        {
            b.LineTo(end, h);
            b.LineTo(0f, h);
            b.Close();
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }
}
