// ── Shell/Visualizer.ITunes.UI.cs ──────────────────────────────────────────────────────────────────────────────────
// The iTunes faces — Magneto (Magnetosphere's charged light) and Flow (G-Force's currents)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 360 lines
// Spec: viz-app-plan.md §2.18-§2.19, §1.4; viz-engine-design.md F3, F4, F5, F6
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Same contract as the Classics (Visualizer.Classics.UI.cs): a static builder, the simulation in a `FaceSim` whose
// paceable ticker is mounted only on a playing, visible stage, a bound rest pose for previews and reduced motion.
//
// MAGNETO is three layers: the nebula (three big radial clouds in a 1/8-scale slice composited with Screen on dark —
// light that brightens what is under it, never a grey veil), the bodies (72 band-charged particles + the two orbiting
// cores, Disc sprites, Additive on dark) and the rays (Segment sprites from each core to its nearest particles).
// FLOW is a feedback box (F6) whose warp turns and zooms about a drifting centre, so the ring wave drawn into it each
// advance smears into currents.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    // ══ 1. MAGNETO — Magnetosphere ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Each particle listens to one band and that band is its CHARGE: loud particles repel from the two orbiting
    /// cores, quiet ones fall back; the mids swirl them. 72 particles (48 weak, 12 preview), rays on a strong GPU stage
    /// only. Preview and reduced: the bodies hold their seeded places, sized by their bands, the cores by the kick.</summary>
    public static Element MagnetoFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, dark = pal.Dark, weak = GpuProfile.IsWeak, live = !pv && !Design.Reduced;
        int count = pv ? Particles.Magneto.PreviewCount : weak ? Particles.Magneto.WeakCount : Particles.Magneto.Count;
        bool rays = !pv && !weak;
        float m = MathF.Min(spec.W, spec.H), cx = spec.W * 0.5f, cy = spec.H * (pv ? 0.5f : 0.46f);
        var sim = new MagnetoSim(slab, dark, count, rays, cx, cy, m, pv ? 1.4f : 1f, MathF.Max(1f, 1.5f * SimScale(in spec)));
        var kids = new List<CanvasChild>(4)
        {
            new CanvasChild(0f, 0f, Nebula(slab, in pal, spec.W, spec.H, cx, cy, m, live ? sim.Time : null, dark, layer: !pv)),
        };
        var blend = GlowBlend(dark);
        if (rays)
            kids.Add(new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Segment, blend, live ? Prop.Of(() => sim.Rays.Current) : Prop.Of(() => sim.BoundRays()))));
        kids.Add(new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Disc, blend, live ? Prop.Of(() => sim.Bodies.Current) : Prop.Of(() => sim.BoundBodies()))));
        if (live) kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    /// <summary>Three slow clouds (A, B, C): radial gradients drifting on the sim clock and swelling with the bass. On a
    /// stage they raster at 1/8 scale (pure gradients survive the upsample) and composite with Screen on the dark arm;
    /// on the light arm SrcOver at a lower alpha. The preview draws them inline. Each cloud blends to the next moment's
    /// rotation on <c>slab.MomentMix</c>.</summary>
    static Element Nebula(Slab slab, in Palette pal, float w, float h, float cx, float cy, float m, FloatSignal? time, bool dark, bool layer)
    {
        var low = slab.Low;
        var rot = pal.Rotated();
        float alpha = dark ? 0.35f : 0.22f;
        var kids = new List<CanvasChild>(3);
        for (int i = 0; i < 3; i++)
        {
            var (col, to, nx, ny, r) = i switch { 0 => (pal.A, rot.A, -0.2f, -0.1f, 0.5f), 1 => (pal.B, rot.B, 0.25f, 0.15f, 0.45f), _ => (pal.C, rot.C, 0f, 0.25f, 0.4f) };
            float d = 2f * r * m;
            int cloud = i;
            kids.Add(new CanvasChild(cx + nx * m - d * 0.5f, cy + ny * m - d * 0.5f, new BoxEl
            {
                Width = d, Height = d, Corners = Radii.Circle(d), HitTestVisible = false, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Gradient = NebulaCloud(col, alpha), GradientTo = NebulaCloud(to, alpha), GradientMix = slab.MomentMix,
                Transform = Prop.Of(() =>
                {
                    float t = time?.Value ?? 0f, s = 1f + 0.25f * low.Value;
                    return new Affine2D(s, 0f, 0f, s, MathF.Sin(t * 0.1f + cloud) * 0.05f * m, MathF.Cos(t * 0.12f + cloud) * 0.04f * m);
                }),
            }));
        }
        return new BoxEl
        {
            Width = w, Height = h, HitTestVisible = false,
            RepaintBoundary = layer, RasterScale = layer ? 0.125f : 1f, LayerBlend = layer && dark ? LayerBlend.Screen : LayerBlend.SrcOver,
            Children = [Canvas.Create(w, h, kids)],
        };
    }

    static GradientSpec NebulaCloud(ColorF c, float alpha) => new(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = alpha }), new GradientStop(1f, c with { A = 0f })]);

    sealed class MagnetoSim(Slab slab, bool dark, int count, bool rays, float cx, float cy, float m, float sizeK, float lineW) : FaceSim(slab, dark)
    {
        readonly Particles.Magneto _body = new(count);
        public readonly InstanceSource Bodies = new(count * 2 + 4), Rays = new(rays ? count : 1);
        /// <summary>The drawn picture's clock (s): the nebula drifts on it.</summary>
        public readonly FloatSignal Time = new(0f);
        uint _bodiesVersion, _raysVersion;

        protected override void Advance(float dtSec)
        {
            PeekLevels();
            _body.Step(dtSec, Levels, Slab.Mid.Peek());
            var t = PeekTints();
            Bodies.Count = _body.WriteBodies(Bodies.Buffer, count, cx, cy, m, Levels, Slab.Kick.Peek(), in t, sizeK);
            Bodies.Publish();
            if (rays) { Rays.Count = _body.WriteRays(Rays.Buffer, cx, cy, m, Levels, in t, lineW); Rays.Publish(); }
            Set(Time, MathF.Round(_body.Time * 100f) / 100f);
        }

        public SpriteInstances BoundBodies()
        {
            ReadLevels();
            var t = ReadTints();
            int n = _body.WriteBodies(Bodies.Buffer, count, cx, cy, m, Levels, Slab.Kick.Value, in t, sizeK);
            return new SpriteInstances(Bodies.Buffer, n, ++_bodiesVersion);
        }

        public SpriteInstances BoundRays()
        {
            ReadLevels();
            var t = ReadTints();
            int n = _body.WriteRays(Rays.Buffer, cx, cy, m, Levels, in t, lineW);
            return new SpriteInstances(Rays.Buffer, n, ++_raysVersion);
        }
    }

    // ══ 2. FLOW — G-Force ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A feedback box (F6): each advance the last picture turns <c>sin(0.2t)·0.02 + 0.012·Mid</c> and zooms
    /// (1.012, 1.006) + 0.02·Low about a centre that drifts across the face, fading toward the transparent veil (0.05 per
    /// 60 Hz frame, scaled by dt). The fresh content is the scope's ring (<c>slab.ScopeRadial</c>, a square Polar ring stretched to 1.4 : 1)
    /// turning slowly, its colour drifting A ↔ C. Weak: the trail at quarter resolution. Preview: Scope's line.
    /// Reduced: the ring alone, still, answering the music.</summary>
    public static Element FlowFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, live = !pv && !Design.Reduced;
        var kids = new List<CanvasChild>(2);
        if (pv)
        {
            float px0 = spec.W * 0.06f, pamp = spec.H * 0.38f;
            kids.Add(new CanvasChild(px0, spec.H * 0.48f - pamp, ScopeLine(slab.Scope, slab.MomentMix, in pal, spec.W * 0.88f, 2f * pamp, 1.6f)));
            return FaceFrame(spec, kids);
        }
        // a Polar series draws a CIRCLE of min(W, H): the ring is a square series stretched by a static ScaleX of
        // FlowAspect on its own box (the prototype's 1.4 : 1 ellipse), the turn on the box around it
        float side = MathF.Min(spec.W / FlowAspect, spec.H) * 0.8f, cx = spec.W * 0.5f, cy = spec.H * 0.5f;
        float thick = MathF.Max(1.5f, 2.5f * SimScale(in spec));
        var radial = slab.ScopeRadial;
        if (!live)
        {
            kids.Add(new CanvasChild(cx - side * 0.5f, cy - side * 0.5f, FlowEllipse(side, new SeriesEl
            {
                Width = side, Height = side, Shape = SeriesShape.Polar, Thickness = thick, AntiAlias = true,
                Color = ColorF.Lerp(pal.A, pal.C, 0.5f), Samples = Prop.Of(() => radial.Current),
            })));
            return FaceFrame(spec, kids);
        }
        var sim = new FlowSim(slab, pal.Dark);
        var dt = sim.Dt; var time = sim.Time;
        float w = spec.W, h = spec.H;
        kids.Add(new CanvasChild(0f, 0f, new BoxEl
        {
            Width = w, Height = h, HitTestVisible = false, ClipToBounds = true,   // the trail covers the clip
            Feedback = new FeedbackSpec(Particles.Feedback.FlowDecay, GpuProfile.IsWeak ? 0.25f : 0.5f, Ink.Veil with { A = 0f }),
            FeedbackTransform = Prop.Of(() => Particles.Feedback.Flow(time.Value, slab.Mid.Value, slab.Low.Value, dt.Value, w, h)),
            FeedbackDecay = Prop.Of(() => Particles.Feedback.Decay(Particles.Feedback.FlowDecay, dt.Value)),
            Children =
            [
                Canvas.Create(w, h,
                [
                    new CanvasChild(cx - side * 0.5f, cy - side * 0.5f, new BoxEl
                    {
                        Width = side, Height = side, HitTestVisible = false, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => Affine2D.Rotation(time.Value * 0.2f)),
                        Children =
                        [
                            FlowEllipse(side, new SeriesEl
                            {
                                Width = side, Height = side, Shape = SeriesShape.Polar, Thickness = thick, AntiAlias = true, GradientAxis = SeriesGradientAxis.Along,
                                Gradient = SolidGradient(pal.A), GradientTo = SolidGradient(pal.C), GradientMix = Prop.Of(() => 0.5f + 0.5f * MathF.Sin(time.Value * 0.4f)),
                                Samples = Prop.Of(() => radial.Current),
                            }),
                        ],
                    }),
                ]),
            ],
        }));
        kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    /// <summary>Flow's ring is the prototype's 1.4 : 1 ellipse.</summary>
    const float FlowAspect = 1.4f;

    /// <summary>The square ring's box, stretched to the ellipse by a STATIC ScaleX about its centre — its own node, so the
    /// bound turn on the box around it stays that node's only transform (one transform owner per node).</summary>
    static BoxEl FlowEllipse(float side, SeriesEl ring) => new()
    {
        Width = side, Height = side, HitTestVisible = false, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
        ScaleX = FlowAspect, Children = [ring],
    };

    /// <summary>Flow's clock: the advance dt (warp and decay scale by it) and the drift time (s). The wave itself is the
    /// slab's ScopeRadial, published by the visualizer clock.</summary>
    sealed class FlowSim(Slab slab, bool dark) : FaceSim(slab, dark)
    {
        public readonly FloatSignal Dt = new(Particles.StepSec), Time = new(0f);
        double _t;

        protected override void Advance(float dtSec)
        {
            _t = (_t + dtSec) % 3600.0;
            Set(Dt, dtSec); Set(Time, (float)_t);
        }
    }
}
