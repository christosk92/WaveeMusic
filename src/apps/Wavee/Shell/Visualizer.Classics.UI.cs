// ── Shell/Visualizer.Classics.UI.cs ────────────────────────────────────────────────────────────────────────────────
// The Classics faces — Classic (Winamp's lit glass), Warp (AVS stars), Tunnel, Ambience (WMP orbs), Kaleido (Alchemy),
// Scope (the oscilloscope), Drift (MilkDrop trails) — and the per-frame sim host the moving faces share
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 800 lines
// Spec: viz-app-plan.md §2.8-§2.14, §1.2-§1.4; viz-engine-design.md F3-F6
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FACE CONTRACT, WITH A SIMULATION. A face is a static builder run once per host render; everything that moves is a
// bound channel. The faces whose motion is a SIMULATION (Warp, Ambience, Kaleido, Tunnel, Drift; Magneto and Flow in
// Visualizer.ITunes.UI.cs) keep it in a small `FaceSim`: a nested `SimFrames` ticker (`Controls.FrameTicker`,
// paceable — the GPU governor may pace it) Peeks the slab, steps the CORE body (`Visualizer.Particles`), writes ONE
// preallocated `InstanceSource` and publishes it. The ticker is mounted only on the stage, only while playing and
// visible (`SimHost`); a preview and a reduced-motion stage bind the SAME body's `Write` through a thunk over the slab
// instead — the rest pose, still sized by the levels, recomputed only when a level moves.
//
// ONE NODE PER CLOUD. Stars, orbs, capsules, LEDs and particles are `SpriteFieldEl` instances (F5): one node, one bind,
// one ≤ 32 KB copy per tick. On the dark arm the glow faces draw Additive (light adds, never muddies); on the light arm
// the same sprites are soft darker tints drawn SrcOver (`Particles.Tints`). Rounded frames (Tunnel) only ever scale
// uniformly. Colours come from the slab's cross-faded A/B/C wherever a bind can carry them, from `pal` where the engine
// takes a static (gradients).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    // ══ 1. THE SIM HOST ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The per-frame half of a simulated face: dt from <c>Design.FrameTime</c> (clamped, a restart is one frame),
    /// then <see cref="Advance"/> — Peek the slab, step the body, write and publish. Built once per face build; the tick
    /// delegate is allocated here, never per frame.</summary>
    abstract class FaceSim
    {
        protected readonly Slab Slab;
        protected readonly bool Dark;
        protected readonly float[] Levels;
        public readonly Action Tick;
        long _lastMs;

        protected FaceSim(Slab slab, bool dark)
        {
            Slab = slab; Dark = dark; Levels = new float[slab.Bands.Length];
            Tick = () =>
            {
                long now = Design.FrameTime.NowMs;
                float dt = Particles.Dt(_lastMs, now);
                _lastMs = now;
                Advance(dt);
            };
        }

        protected abstract void Advance(float dtSec);

        /// <summary>The tick's colours and levels: Peeks (the ticker is untracked anyway).</summary>
        protected Particles.Tints PeekTints() => new(Slab.A.Peek(), Slab.B.Peek(), Slab.C.Peek(), Dark);
        protected void PeekLevels() { for (int i = 0; i < Levels.Length; i++) Levels[i] = Slab.Bands[i].Peek(); }
        /// <summary>The bound thunk's colours and levels: Value reads — they ARE the subscription.</summary>
        protected Particles.Tints ReadTints() => new(Slab.A.Value, Slab.B.Value, Slab.C.Value, Dark);
        protected void ReadLevels() { for (int i = 0; i < Levels.Length; i++) Levels[i] = Slab.Bands[i].Value; }

        protected static void Set(FloatSignal s, float v) { if (v != s.Peek()) s.Value = v; }
    }

    /// <summary>Mounts the face's ticker only while it can move: playing and the window visible (the clock's own gates;
    /// reduced motion never builds a host — the face binds its rest pose instead). Paused ⇒ unmounted ⇒ no frame wake.
    /// Each tick runs inside ONE <c>Runtime.Batch</c> (the clock's TickCore discipline): a sim that writes an instance
    /// publish and a clock signal or two still costs one frame request.</summary>
    sealed class SimHost(Action tick) : Component
    {
        Action? _batched;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            bool run = Playback.IsPlaying.Value && !(hooks.WindowOccluded?.Value ?? false);
            var batched = _batched ??= Batched;
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = run ? [Embed.Comp(() => new SimFrames(batched))] : [] };
        }

        void Batched() { if (Context.Runtime is { } rt) rt.Batch(tick); else tick(); }
    }

    /// <summary>A face simulation's per-frame tick, paceable by the GPU governor; named for the <c>[wake]</c> census.</summary>
    sealed class SimFrames(Action tick) : Controls.FrameTicker(tick, paceable: true);

    static CanvasChild SimChild(FaceSim sim) => new(0f, 0f, Embed.Comp(() => new SimHost(sim.Tick)));

    static PaintBlend GlowBlend(bool dark) => dark ? PaintBlend.Additive : PaintBlend.SrcOver;

    static SpriteFieldEl SimField(in FaceSpec spec, SpriteKernel kernel, PaintBlend blend, Prop<SpriteInstances> instances) => new()
    {
        Width = spec.W, Height = spec.H, Kernel = kernel, Blend = blend, Instances = instances,
    };

    /// <summary>The prototype's faces are drawn on a ≈ 1500-DIP free area; lengths and strokes scale with the box.</summary>
    static float SimScale(in FaceSpec spec) => MathF.Max(spec.W, spec.H) / Particles.Warp.RefSize;

    // ══ 2. CLASSIC — Winamp's spectrum as lit glass ══════════════════════════════════════════════════════════════════

    /// <summary>28 columns × 18 rows of rounded LED segments (16 × 9 preview) in ONE sprite field: a static
    /// frequency → colour ramp ACROSS the columns (C → A → B, not up each bar), lit rows brighten toward the top, the
    /// unlit rows are faint ink, the falling peak is an ink segment. A low A wash under the bottom 40 % (a gradient, no
    /// blur). No sim: the thunk reads the bands and peaks, so reduced motion is the same face.</summary>
    public static Element ClassicFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, dark = pal.Dark;
        int cols = pv ? 16 : 28, rows = pv ? 9 : 18;
        float k = pv ? 1f : SimScale(in spec);
        float gx = (pv ? 3f : 8f) * k, gy = (pv ? 2f : 5f) * k;
        float w = spec.W * (pv ? 0.86f : 0.7f), h = spec.H * (pv ? 0.8f : 0.5f);
        float x0 = (spec.W - w) * 0.5f, y0 = (spec.H - h) * 0.5f - (pv ? 0f : spec.H * 0.04f);
        var leds = new ClassicLeds(slab, cols, rows, x0, y0, w, h, gx, gy,
                                   Ink.Ink with { A = dark ? 0.06f : 0.05f }, Ink.Ink with { A = dark ? 0.9f : 0.75f });
        var kids = new List<CanvasChild>(2);
        if (!pv)
            kids.Add(new CanvasChild(x0, y0 + h * 0.6f, new BoxEl
            {
                Width = w, Height = h * 0.4f, HitTestVisible = false,
                Gradient = ClassicWash(pal.A), GradientTo = ClassicWash(pal.Rotated().A), GradientMix = slab.MomentMix,
            }));
        kids.Add(new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Capsule, PaintBlend.SrcOver, Prop.Of(() => leds.Bound()))));
        return FaceFrame(spec, kids);
    }

    /// <summary>The low wash: transparent at the top, the colour at 35 % at the bottom.</summary>
    static GradientSpec ClassicWash(ColorF c) => new(GradientShape.Linear, 90f, [new GradientStop(0f, c with { A = 0f }), new GradientStop(1f, c with { A = 0.35f })]);

    /// <summary>The LED field's bound writer. Column <c>c</c> reads band <c>round(c/(cols−1)·42)</c>; a row is lit below
    /// <c>round(level·rows)</c>, the peak row is <c>min(rows−1, round(peak·rows))</c> when it sits at or above the lit top.</summary>
    sealed class ClassicLeds
    {
        readonly Slab _slab;
        readonly Sprite[] _buf;
        readonly int[] _band;
        readonly int _cols, _rows;
        readonly float _x0, _bottom, _cw, _rh, _gx, _gy;
        readonly ColorF _unlit, _peak;
        uint _version;

        public ClassicLeds(Slab slab, int cols, int rows, float x0, float y0, float w, float h, float gx, float gy, ColorF unlit, ColorF peak)
        {
            _slab = slab; _cols = cols; _rows = rows; _x0 = x0; _bottom = y0 + h; _gx = gx; _gy = gy; _unlit = unlit; _peak = peak;
            _cw = (w - gx * (cols - 1)) / cols; _rh = (h - gy * (rows - 1)) / rows;
            _buf = new Sprite[cols * rows];
            _band = new int[cols];
            int top = slab.Bands.Length - 6;
            for (int c = 0; c < cols; c++) _band[c] = (int)MathF.Round(c / (float)(cols - 1) * top);
        }

        public SpriteInstances Bound()
        {
            ColorF a = _slab.A.Value, b = _slab.B.Value, cc = _slab.C.Value;
            int n = 0;
            for (int c = 0; c < _cols; c++)
            {
                int band = _band[c];
                int lit = (int)MathF.Round(_slab.Bands[band].Value * _rows);
                int pk = Math.Min(_rows - 1, (int)MathF.Round(_slab.Peaks[band].Value * _rows));
                float u = c / (float)(_cols - 1);
                ColorF col = u < 0.5f ? ColorF.Lerp(cc, a, u * 2f) : ColorF.Lerp(a, b, (u - 0.5f) * 2f);
                float x = _x0 + c * (_cw + _gx) + _cw * 0.5f;
                for (int r = 0; r < _rows; r++)
                {
                    float y = _bottom - (r + 1) * _rh - r * _gy + _rh * 0.5f;
                    ColorF fill = r < lit ? col with { A = 0.55f + 0.45f * (r / (float)_rows) } : r == pk && pk >= lit ? _peak : _unlit;
                    Particles.Put(_buf, ref n, x, y, _cw, _rh, 0f, 0f, in fill);
                }
            }
            return new SpriteInstances(_buf, n, ++_version);
        }
    }

    // ══ 3. WARP — AVS's starfield, calmed ════════════════════════════════════════════════════════════════════════════

    /// <summary>220 streaks (120 weak, 70 preview) in one Streak field: the speed is the bass + the kick, the length is the
    /// speed, colours A/B/C by star. Additive on dark. Preview and reduced: the stars hold still, length by the level.</summary>
    public static Element WarpFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, dark = pal.Dark, live = !pv && !Design.Reduced;
        int count = pv ? Particles.Warp.PreviewCount : Particles.Warp.Count;
        var sim = new WarpSim(slab, dark, count, spec.W, spec.H, spec.H * (pv ? 0.5f : 0.46f), pv ? 0.3f : 1f);
        var kids = new List<CanvasChild>(2)
        {
            new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Streak, GlowBlend(dark), live ? Prop.Of(() => sim.Src.Current) : Prop.Of(() => sim.Bound()))),
        };
        if (live) kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    sealed class WarpSim(Slab slab, bool dark, int count, float w, float h, float cy, float lenK) : FaceSim(slab, dark)
    {
        readonly Particles.Warp _body = new(count);
        public readonly InstanceSource Src = new(count);
        uint _version;

        protected override void Advance(float dtSec)
        {
            _body.Step(dtSec, Slab.Low.Peek(), Slab.Kick.Peek());
            var t = PeekTints();
            Src.Count = _body.Write(Src.Buffer, count, w, h, w * 0.5f, cy, in t, lenK);
            Src.Publish();
        }

        public SpriteInstances Bound()
        {
            _body.Settle(Slab.Low.Value, Slab.Kick.Value);
            var t = ReadTints();
            int n = _body.Write(Src.Buffer, count, w, h, w * 0.5f, cy, in t, lenK);
            return new SpriteInstances(Src.Buffer, n, ++_version);
        }
    }

    // ══ 4. TUNNEL — rounded frames fly past ══════════════════════════════════════════════════════════════════════════

    /// <summary>14 bordered rounded frames (8 preview), each bound to its depth: uniform scale <c>d^2.2</c> of the full
    /// 1.6·min side (a rounded square scales uniformly, so its corners stay round), a twist of <c>spin + 0.9·d</c>, a
    /// fade in from the far end. The kick and the bass push the phase; the level breathes the scale ≤ 3 % (so a preview
    /// and a reduced stage still answer the music). Colours A/B/C by frame, bound (cross-faded).</summary>
    public static Element TunnelFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, live = !pv && !Design.Reduced;
        int n = pv ? Particles.Tunnel.PreviewFrames : Particles.Tunnel.Frames;
        float m = MathF.Min(spec.W, spec.H), side = 1.6f * m, cx = spec.W * 0.5f, cy = spec.H * (pv ? 0.5f : 0.46f);
        float minSide = (pv ? 2f : 6f) * (pv ? 1f : SimScale(in spec)), border = MathF.Max(pv ? 1f : 1.5f, 0.006f * side);
        float arm = pal.Dark ? 1f : 0.8f;
        var sim = new TunnelSim(slab, pal.Dark);
        var phase = sim.Phase; var spin = sim.Spin; var level = slab.Level;
        var kids = new List<CanvasChild>(n + 1);
        for (int k = n - 1; k >= 0; k--)
        {
            int frame = k;
            var stroke = (k % 3) switch { 0 => slab.A, 1 => slab.B, _ => slab.C };
            kids.Add(new CanvasChild(cx - side * 0.5f, cy - side * 0.5f, new BoxEl
            {
                Width = side, Height = side, Corners = CornerRadius4.All(0.22f * side), BorderWidth = border, BorderColor = Prop.Bind(stroke),
                HitTestVisible = false, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Opacity = Prop.Of(() => arm * Particles.Tunnel.Alpha(Particles.Tunnel.Depth(frame, phase.Value, n))),
                Transform = Prop.Of(() =>
                {
                    float d = Particles.Tunnel.Depth(frame, phase.Value, n);
                    float f = (Particles.Tunnel.Size(d) * side + minSide) / side * (1f + 0.03f * level.Value);
                    float a = Particles.Tunnel.Turn(d, spin.Value), c = MathF.Cos(a) * f, s = MathF.Sin(a) * f;
                    return new Affine2D(c, s, -s, c, 0f, 0f);
                }),
            }));
        }
        if (live) kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    sealed class TunnelSim(Slab slab, bool dark) : FaceSim(slab, dark)
    {
        readonly Particles.Tunnel _body = new() { Phase = 0.5f };
        public readonly FloatSignal Phase = new(0.5f), Spin = new(0f);

        protected override void Advance(float dtSec)
        {
            _body.Step(dtSec, Slab.Low.Peek(), Slab.Kick.Peek());
            Set(Phase, _body.Phase); Set(Spin, _body.Spin);
        }
    }

    // ══ 5. AMBIENCE — WMP's glowing orbs ═════════════════════════════════════════════════════════════════════════════

    /// <summary>34 orbs (20 weak, 12 preview) on slow orbits, each owning a band and swelling with it: a soft Disc halo
    /// plus a hot centre (lightened, never white) per orb, Additive on dark; on light the centre is the colour itself
    /// and the halo a darker tint (coloured glass, not lamps). Preview / reduced: the orbs hold their orbit position.</summary>
    public static Element AmbienceFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, dark = pal.Dark, live = !pv && !Design.Reduced;
        int count = pv ? Particles.Ambience.PreviewCount : Particles.Ambience.Count;
        var sim = new AmbienceSim(slab, dark, count, spec.W * 0.5f, spec.H * (pv ? 0.5f : 0.46f), MathF.Min(spec.W, spec.H), pv ? 1.4f : 1f);
        var kids = new List<CanvasChild>(2)
        {
            new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Disc, GlowBlend(dark), live ? Prop.Of(() => sim.Src.Current) : Prop.Of(() => sim.Bound()))),
        };
        if (live) kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    sealed class AmbienceSim(Slab slab, bool dark, int count, float cx, float cy, float m, float sizeK) : FaceSim(slab, dark)
    {
        readonly Particles.Ambience _body = new(count);
        public readonly InstanceSource Src = new(count * 2);
        uint _version;

        protected override void Advance(float dtSec)
        {
            _body.Step(dtSec);
            PeekLevels();
            var t = PeekTints();
            Src.Count = _body.Write(Src.Buffer, count, cx, cy, m, Levels, in t, sizeK);
            Src.Publish();
        }

        public SpriteInstances Bound()
        {
            ReadLevels();
            var t = ReadTints();
            int n = _body.Write(Src.Buffer, count, cx, cy, m, Levels, in t, sizeK);
            return new SpriteInstances(Src.Buffer, n, ++_version);
        }
    }

    // ══ 6. KALEIDO — Alchemy's symmetry, kept geometric ══════════════════════════════════════════════════════════════

    /// <summary>One wedge of 8 band-driven capsules, rotated six times and mirrored (12 copies; 4 × 6 in the preview) in
    /// ONE Capsule field — a capsule's length is its own, never a scale, so every end stays round. The mids spin it.</summary>
    public static Element KaleidoFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, live = !pv && !Design.Reduced;
        int parts = pv ? Particles.Kaleido.PreviewParts : Particles.Kaleido.Parts, copies = pv ? Particles.Kaleido.PreviewCopies : Particles.Kaleido.Copies;
        var sim = new KaleidoSim(slab, pal.Dark, parts, copies, spec.W * 0.5f, spec.H * (pv ? 0.5f : 0.46f), MathF.Min(spec.W, spec.H) * (pv ? 1.2f : 1f));
        var kids = new List<CanvasChild>(2)
        {
            new CanvasChild(0f, 0f, SimField(in spec, SpriteKernel.Capsule, PaintBlend.SrcOver, live ? Prop.Of(() => sim.Src.Current) : Prop.Of(() => sim.Bound()))),
        };
        if (live) kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    sealed class KaleidoSim(Slab slab, bool dark, int parts, int copies, float cx, float cy, float m) : FaceSim(slab, dark)
    {
        readonly Particles.Kaleido _body = new();
        public readonly InstanceSource Src = new(parts * copies);
        uint _version;

        protected override void Advance(float dtSec)
        {
            _body.Step(dtSec, Slab.Mid.Peek());
            PeekLevels();
            var t = PeekTints();
            Src.Count = _body.Write(Src.Buffer, cx, cy, m, Levels, in t, parts, copies);
            Src.Publish();
        }

        public SpriteInstances Bound()
        {
            ReadLevels();
            var t = ReadTints();
            int n = _body.Write(Src.Buffer, cx, cy, m, Levels, in t, parts, copies);
            return new SpriteInstances(Src.Buffer, n, ++_version);
        }
    }

    // ══ 7. SCOPE — Winamp's oscilloscope as a soft ribbon ════════════════════════════════════════════════════════════

    /// <summary>The time-domain wave (<c>slab.Scope</c>, 0.5 = silence) as an anti-aliased stroke with a C → A → B gradient
    /// ALONG the line, over a wide glow twin rastered at a quarter scale and blurred (Additive on dark; a faint ink
    /// ribbon on light). Preview: the line alone.</summary>
    public static Element ScopeFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, dark = pal.Dark, glow = !pv;
        float k = SimScale(in spec), x0 = spec.W * 0.06f, w = spec.W * 0.88f, mid = spec.H * 0.48f, amp = spec.H * (pv ? 0.38f : 0.2f);
        var scope = slab.Scope;
        var kids = new List<CanvasChild>(2);
        if (glow)
        {
            float pad = 40f * k;
            kids.Add(new CanvasChild(x0 - pad, mid - amp - pad, new BoxEl
            {
                Width = w + 2f * pad, Height = 2f * amp + 2f * pad, Padding = new Edges4(pad, pad, pad, pad), HitTestVisible = false,
                RepaintBoundary = true, RasterScale = 0.25f, Blur = 18f * k,
                Children =
                [
                    new SeriesEl
                    {
                        Width = w, Height = 2f * amp, Shape = SeriesShape.Stroke, Thickness = 26f * k, AntiAlias = true,
                        Color = dark ? pal.A with { A = 0.45f } : Ink.Ink with { A = 0.10f }, Blend = GlowBlend(dark),
                        Samples = Prop.Of(() => scope.Current),
                    },
                ],
            }));
        }
        kids.Add(new CanvasChild(x0, mid - amp, ScopeLine(scope, slab.MomentMix, in pal, w, 2f * amp, pv ? 1.6f : MathF.Max(2f, 4f * k))));
        return FaceFrame(spec, kids);
    }

    /// <summary>The scope's line: a Stroke series (baseline at the bottom, amplitude 1 — so 0.5 sits mid-box), the C → A → B
    /// gradient along the samples, blending to the next moment's rotation on <paramref name="mix"/>. Also Flow's preview.</summary>
    static SeriesEl ScopeLine(SeriesSource scope, FloatSignal mix, in Palette pal, float w, float h, float thickness) => new()
    {
        Width = w, Height = h, Shape = SeriesShape.Stroke, Thickness = thickness, AntiAlias = true, GradientAxis = SeriesGradientAxis.Along,
        Gradient = ScopeRamp(pal), GradientTo = ScopeRamp(pal.Rotated()), GradientMix = mix,
        Samples = Prop.Of(() => scope.Current),
    };

    static GradientSpec ScopeRamp(in Palette p) => new(GradientShape.Linear, 0f, [new GradientStop(0f, p.C), new GradientStop(0.5f, p.A), new GradientStop(1f, p.B)]);

    // ══ 8. DRIFT — MilkDrop's trails ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>A feedback box (F6): each advance the last picture is turned <c>0.006 + 0.01·Mid</c> and zoomed
    /// <c>1.018 + 0.02·Kick</c> about the ring centre and fades toward the transparent veil (decay 0.06 per 60 Hz frame,
    /// scaled by dt); the fresh content on top is a Polar ring of the bands (mirrored, 65 samples) whose colour drifts A ↔ B.
    /// Weak: the trail at quarter resolution. Preview and reduced: the frozen trail — the ring plus two static echoes,
    /// each a uniform scale of the circle.</summary>
    public static Element DriftFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        bool pv = spec.Preview, live = !pv && !Design.Reduced;
        float m = MathF.Min(spec.W, spec.H), cx = spec.W * 0.5f, cy = spec.H * (pv ? 0.5f : 0.46f);
        float thick = pv ? 1.2f : MathF.Max(2f, 3f * SimScale(in spec));
        var ring = new DriftRing(slab);
        var kids = new List<CanvasChild>(4);
        if (!live)
        {
            var samples = Prop.Of(() => ring.Bound());
            ColorF col = ColorF.Lerp(pal.A, pal.B, 0.5f);
            ReadOnlySpan<float> echo = [1.26f, 1.12f, 1f];
            ReadOnlySpan<float> alpha = [0.2f, 0.45f, 1f];
            for (int e = 0; e < echo.Length; e++)
                kids.Add(new CanvasChild(cx - m * 0.5f, cy - m * 0.5f, new BoxEl
                {
                    Width = m, Height = m, ScaleX = echo[e], ScaleY = echo[e], Opacity = alpha[e], HitTestVisible = false,
                    Children = [new SeriesEl { Width = m, Height = m, Shape = SeriesShape.Polar, Thickness = thick, AntiAlias = true, Color = col, Samples = samples }],
                }));
            return FaceFrame(spec, kids);
        }
        var sim = new DriftSim(slab, pal.Dark, ring);
        var dt = sim.Dt; var time = sim.Time; var src = ring.Src;
        float pivotY = cy - spec.H * 0.5f;
        kids.Add(new CanvasChild(0f, 0f, new BoxEl
        {
            Width = spec.W, Height = spec.H, HitTestVisible = false, ClipToBounds = true,   // the trail covers the clip
            Feedback = new FeedbackSpec(Particles.Feedback.DriftDecay, 0.5f, Ink.Veil with { A = 0f }),
            FeedbackTransform = Prop.Of(() => Particles.Feedback.Drift(slab.Mid.Value, slab.Kick.Value, dt.Value, 0f, pivotY)),
            FeedbackDecay = Prop.Of(() => Particles.Feedback.Decay(Particles.Feedback.DriftDecay, dt.Value)),
            Children =
            [
                Canvas.Create(spec.W, spec.H,
                [
                    new CanvasChild(cx - m * 0.5f, cy - m * 0.5f, new SeriesEl
                    {
                        Width = m, Height = m, Shape = SeriesShape.Polar, Thickness = thick, AntiAlias = true, GradientAxis = SeriesGradientAxis.Along,
                        Gradient = SolidGradient(pal.A), GradientTo = SolidGradient(pal.B), GradientMix = Prop.Of(() => 0.5f + 0.5f * MathF.Sin(time.Value * 0.5f)),
                        Samples = Prop.Of(() => src.Current),
                    }),
                ]),
            ],
        }));
        kids.Add(SimChild(sim));
        return FaceFrame(spec, kids);
    }

    /// <summary>A two-stop gradient of one colour: the solid end of a <c>GradientTo</c>/<c>GradientMix</c> colour drift.</summary>
    static GradientSpec SolidGradient(ColorF c) => new(GradientShape.Linear, 0f, [new GradientStop(0f, c), new GradientStop(1f, c)]);

    /// <summary>Drift's ring: 65 radius fractions (the Polar shape's fraction of the half box), base 0.32 (= the
    /// prototype's 0.16·min radius) swelling 0.9× with the band. The ticker publishes it; a preview binds <see cref="Bound"/>.</summary>
    sealed class DriftRing(Slab slab)
    {
        public const int Points = 65;
        const float Base = 0.32f, Gain = 0.9f;
        public readonly SeriesSource Src = new(Points);
        uint _version;

        void Fill(bool track)
        {
            for (int k = 0; k < Points; k++)
            {
                var band = slab.Bands[Math.Min(slab.Bands.Length - 1, Particles.Feedback.RingBand(k, Points - 1))];
                Src.Buffer[k] = Base * (1f + Gain * (track ? band.Value : band.Peek()));
            }
        }

        public void Publish() { Fill(track: false); Src.Publish(); }
        public SeriesSamples Bound() { Fill(track: true); return new SeriesSamples(Src.Buffer, Points, ++_version); }
    }

    sealed class DriftSim(Slab slab, bool dark, DriftRing ring) : FaceSim(slab, dark)
    {
        /// <summary>The last advance's dt (the feedback warp and decay scale by it) and the colour drift's clock (s).</summary>
        public readonly FloatSignal Dt = new(Particles.StepSec), Time = new(0f);
        double _t;

        protected override void Advance(float dtSec)
        {
            _t = (_t + dtSec) % 3600.0;
            ring.Publish();
            Set(Dt, dtSec); Set(Time, (float)_t);
        }
    }
}
