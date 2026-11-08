// ── Themes/Classic/Structure/Views/Panel/ClassicBleed.cs — "Tideline": Classic's edge-to-edge canvas ───────────────────────────────────
//
// The Canvas player style at rest fills the panel from its very top (`NowPlayingHeroProps.BleedVideo`, core) and ends, instead of a straight
// fade, in a slowly moving wave: three swells drift at different speeds over the canvas's lower part (a violet one, a warm one tinted from the
// art, and the front one in the panel's ground with a light crest line), the ground under the crest deepening toward the panel's foot. The
// ground is a deep tint taken from the art: no brand green anywhere. Board: canvas-design/project/BleedClassic.dc.html (its generator's
// `wave()` is the geometry below). Reduced motion: the swells stand still (`Deck.Drift`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The colours the canvas's ending and its meta take from the playing track's art.</summary>
internal readonly record struct ClassicBleedInk(ColorF Ground, ColorF Deep, ColorF Warm)
{
    /// <summary>The ground (the panel's base tinted toward the art's accent), its deeper foot and the warm accent (the art's accent, lifted).
    /// The palette watch is read inside, so a late grading re-renders the reader.</summary>
    public static ClassicBleedInk Of(string? url)
    {
        if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        ColorF accent = Design.SchemeFor(url) is { } s ? ColorScience.Accent(s) : Tok.TextSecondary;
        ColorF ground = ColorF.Lerp(Tok.FillSolidBase, accent, ClassicHeroLayout.GroundMix) with { A = 1f };
        ColorF deep = ColorF.Lerp(ground, ClassicHeroLayout.GroundFloor, ClassicHeroLayout.GroundDeepen) with { A = 1f };
        return new ClassicBleedInk(ground, deep, ColorScience.Lift(accent));
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
                    Gradient = Ui.GradientDown(new GradientStop(0f, ink.Ground with { A = 0f }), new GradientStop(1f, ink.Ground with { A = ClassicHeroLayout.BleedDarkA })),
                },
                Layer(w, top, ClassicHeroLayout.WaveBackPeriods, ClassicHeroLayout.WaveBackMs,
                    new PathEl { Geometry = _back, Fill = ClassicHeroLayout.WaveViolet with { A = ClassicHeroLayout.WaveBackA }, Width = SwellWidth(w, ClassicHeroLayout.WaveBackPeriods), Height = band }),
                Layer(w, top, ClassicHeroLayout.WaveMidPeriods, ClassicHeroLayout.WaveMidMs,
                    new PathEl { Geometry = _mid, Fill = ink.Warm with { A = ClassicHeroLayout.WaveMidA }, Width = SwellWidth(w, ClassicHeroLayout.WaveMidPeriods), Height = band }),
                Layer(w, top, ClassicHeroLayout.WaveFrontPeriods, ClassicHeroLayout.WaveFrontMs, new BoxEl
                {
                    ZStack = true, Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band,
                    Children =
                    [
                        new PathEl { Geometry = _front, Fill = ink.Ground, Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band },
                        new PathEl
                        {
                            Geometry = _crest, StrokeColor = ink.Warm with { A = ClassicHeroLayout.BleedCrestA }, Stroke = new StrokeStyle(ClassicHeroLayout.BleedCrestLine),
                            Width = SwellWidth(w, ClassicHeroLayout.WaveFrontPeriods), Height = band,
                        },
                    ],
                }),
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, top + band - 1f, 0f, 0f),
                    Gradient = Ui.GradientDown(new GradientStop(0f, ink.Ground), new GradientStop(1f, ink.Deep)),
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Height = ClassicHeroLayout.BleedHeadScrimH,
                    Gradient = Ui.GradientDown(new GradientStop(0f, ink.Ground with { A = ClassicHeroLayout.BleedHeadScrimA }), new GradientStop(1f, ink.Ground with { A = 0f })),
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
