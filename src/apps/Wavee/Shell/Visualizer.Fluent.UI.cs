// ── Shell/Visualizer.Fluent.UI.cs ──────────────────────────────────────────────────────────────────────────────────
// The Fluent six — Bloom, Bars, Ring, Orbit, Aurora, Timeline (stage + preview) — and their pure arithmetic (FluentGeometry)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 700 lines
// Spec: stage visualizers app plan §1 (design system), §2.2-§2.7 (the six faces)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FLUENT FAMILY. Six faces built from round geometry and the cover's own three colours. Every face is a static
// builder run ONCE per host render (the face contract, Visualizer.UI.cs §3): every dimension a fraction of the spec,
// everything that moves a bound channel over the slab.
//
// TWO KINDS OF COLOUR. Solid paint binds the slab's cross-faded `A/B/C/Deep` (a track change and a moment both fade
// with no remount). Gradients cannot bind a colour, so a gradient carries the palette it was built with (`pal`) AND
// the palette after the next moment (`GradientTo = pal.Rotated()`) and binds `GradientMix` to `slab.MomentMix`: the
// moment fades the stops, and the host rebuilds the face on the new palette when the fade lands. Ink is ink:
// `StageInk` at an alpha rung, never a literal.
//
// INSTANCED CAPSULES. Bars (40 + dots + glow), Ring (96), Orbit (the three sweeps) and Timeline (2 × 360 + ticks) are
// ONE `SpriteFieldEl` each, refilled by a `Prop.Of` thunk over the slab: the thunk runs only when a signal it reads
// moved (at most once per clock batch), so a settled stage costs nothing — cheaper than a per-frame ticker, which
// would wake every frame to find the same bands. A capsule's LENGTH is its SDF length, so a short bar stays round
// (never a scaled rounded rect). The thunk keeps a PLAIN version counter (`FluentSpriteBuffer`), not a signal: a
// bind that wrote a signal it also read would re-fire itself.
//
// SILENCE, CONNECT, CALM. Without a live FFT the slab already carries the precomputed spread (kind-237 bands spread
// over 48), so no face branches on the source. Reduced motion is a VALUE (`FluentMotion` = 0): amplitudes go to their
// rest pose, nothing is hidden. Every GPU tier gets the same face (no weak-tier cuts: the measured governor is the only GPU throttle); a preview drops
// the cover, the glow, the ticks and half the parts.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

using Geo = Wavee.Visualizer.FluentGeometry;
using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    /// <summary>Reduced motion as a value: every amplitude a face binds is multiplied by this (0 ⇒ the rest pose).</summary>
    static float FluentMotion => Design.Reduced ? 0f : 1f;

    const float FluentHalfPi = MathF.PI * 0.5f;

    // ══ BLOOM ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Three radial clouds (A on the lows, B on the mids, C on the highs) over a Deep one, in ONE low-res
    /// repaint boundary that SCREENs onto the stage (dark arm) — and the cover floating in front, its coloured shadow
    /// swelling on the kick. Preview: three unblurred discs, no cover.</summary>
    public static Element BloomFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H, m = MathF.Min(w, h);
        bool pv = spec.Preview, dark = pal.Dark;
        float motion = FluentMotion;
        var to = pal.Rotated();
        float a0 = dark ? 0.75f : 0.6f, a1 = dark ? 0.25f : 0.18f, k = pv ? 0.9f : 1f;
        ColorF[] now = [pal.A, pal.B, pal.C], next = [to.A, to.B, to.C];
        FloatSignal[] energy = [slab.Low, slab.Mid, slab.High];

        var clouds = new List<CanvasChild>(4);
        for (int i = 0; i < 3; i++)
            clouds.Add(BloomCloud(slab, i, w, h, m * k, now[i], next[i], energy[i], a0, a1, motion, drift: !pv));
        if (pv) return FaceFrame(spec, clouds);

        // the Deep cloud: the room the light sits in — static, it does not breathe with the music
        float dd = m;
        clouds.Add(new CanvasChild(0.2f * w - 0.5f * dd, 0.78f * h - 0.5f * dd, new BoxEl
        {
            Width = dd, Height = dd, Corners = Radii.Circle(dd), HitTestVisible = false, Opacity = Geo.CloudOpacity(0.2f, 1f),
            Gradient = FluentRadial(pal.Deep, a0, a1),
        }));

        // The clouds are pure radial gradients that drift and breathe every tick: a RepaintBoundary keeps that motion in
        // this slice, rastered at a quarter scale (Field's trick, Visualizer.UI.cs FieldFace) and SCREENed onto the stage
        // so the light adds instead of veiling. The one blur layer this face spends.
        var kids = new List<CanvasChild>(3)
        {
            new CanvasChild(0f, 0f, new BoxEl
            {
                Width = w, Height = h, HitTestVisible = false, RepaintBoundary = true, RasterScale = 0.25f,
                LayerBlend = dark ? LayerBlend.Screen : LayerBlend.SrcOver, Blur = 40f,
                Children = [Canvas.Create(w, h, clouds)],
            }),
        };

        // behind a layout (Ambient) the layout owns the cover: the clouds alone, atmosphere only
        if (spec.Ambient) return FaceFrame(spec, kids);

        // the cover: 0.42·min, corners 8, lifted 6 % above centre; its shadow is a TWIN box behind it (transparent fill,
        // shadow only) so the shadow's alpha and swell bind without touching the cover. Light arm: depth, not light.
        float s = 0.42f * m, x = 0.5f * (w - s), y = 0.5f * (h - s) - 0.06f * h, r = 8f;
        var shadow = dark ? new ShadowSpec(60f, 18f, 0f, pal.A with { A = 0.75f })
                          : new ShadowSpec(40f, 14f, 0f, Ink.Ink with { A = 0.25f });
        var kick = slab.Kick; var low = slab.Low;
        kids.Add(new CanvasChild(x, y, new BoxEl
        {
            Width = s, Height = s, Corners = CornerRadius4.All(r), Shadow = shadow, HitTestVisible = false,
            Opacity = Prop.Of(() => Geo.BloomShadowOpacity(low.Value, motion)),
            Transform = Prop.Of(() => { float z = 1f + 0.05f * kick.Value * motion; return Affine2D.Scale(z, z); }),
        }));
        kids.Add(new CanvasChild(x, y, new BoxEl
        {
            Width = s, Height = s, Corners = CornerRadius4.All(r), ClipToBounds = true, HitTestVisible = false,
            Transform = Prop.Of(() => { float z = 1f + 0.018f * kick.Value * motion; return Affine2D.Scale(z, z); }),
            Children = [Controls.Artwork(spec.CoverUrl, s, s, r, decodePx: 512)],
        }));
        return FaceFrame(spec, kids);
    }

    /// <summary>One breathing cloud: a radial disc authored at its LARGEST radius and scaled down (a circle, so a uniform
    /// scale is exact), its alpha bound to the band energy, drifting on a 28–36 s keyframe loop (zero ticks).</summary>
    static CanvasChild BloomCloud(Slab slab, int i, float w, float h, float m, ColorF c, ColorF to, FloatSignal e,
                                  float a0, float a1, float motion, bool drift)
    {
        var spec = Geo.Clouds[i];
        float max = spec.Base + spec.Gain, d = 2f * m * max, rest = spec.Base, gain = spec.Gain;
        var disc = new BoxEl
        {
            Width = d, Height = d, Corners = Radii.Circle(d), HitTestVisible = false,
            Gradient = FluentRadial(c, a0, a1), GradientTo = FluentRadial(to, a0, a1), GradientMix = slab.MomentMix,
            Transform = Prop.Of(() => { float z = Geo.CloudScale(e.Value, rest, gain, motion) / max; return Affine2D.Scale(z, z); }),
            Opacity = Prop.Of(() => Geo.CloudOpacity(e.Value, motion)),
        };
        Element el = drift
            ? Embed.Comp(new BloomDrift.Props(i, d, spec.DriftX * w, spec.DriftY * h, spec.PeriodSec * 1000f, disc), static () => new BloomDrift()) with { Key = "bloom:cloud:" + i }
            : disc;
        return new CanvasChild(spec.Fx * w - 0.5f * d, spec.Fy * h - 0.5f * d, el);
    }

    /// <summary>The cloud's drift: ping-pong translate keyframes on this component's own root (the hooks' HostNode
    /// contract), the bound scale/opacity on the disc INSIDE it — two nodes, so the keyframes and the bind never write
    /// the same transform. Reduced motion: REST keys (the engine does not snap looping keyframes).</summary>
    sealed class BloomDrift : Component
    {
        public sealed record Props(int Index, float Diameter, float Dx, float Dy, float PeriodMs, Element Disc);
        static readonly Keyframe[] s_rest = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];
        public override Element Render()
        {
            var p = UseProps<Props>();
            bool reduced = Design.Reduced;
            var key = DepKey.From(p.Index, (int)p.Diameter, (int)p.Dx, reduced ? 1 : 0);
            UseKeyframes(AnimChannel.TranslateX, reduced ? s_rest : [new Keyframe(0f, 0f), new Keyframe(0.5f, p.Dx, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], p.PeriodMs, loop: !reduced, key);
            UseKeyframes(AnimChannel.TranslateY, reduced ? s_rest : [new Keyframe(0f, 0f), new Keyframe(0.5f, p.Dy, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], p.PeriodMs, loop: !reduced, key);
            Context.UseAmbientPause(AmbientMotion.Translate, key, loops: !reduced);   // paused playback: the cloud stands still
            return new BoxEl { Width = p.Diameter, Height = p.Diameter, HitTestVisible = false, Children = [p.Disc] };
        }
    }

    // ══ BARS ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>40 mirrored capsules about the centre line (bands mirrored from the middle out), a peak dot in ink above
    /// each, and a soft glow capsule under each on the dark arm. A bar's colour moves from A toward B as it grows — the
    /// prototype's "reveal, don't stretch" field, per capsule. ONE sprite field. Preview: 20 bars, no dots, no glow.</summary>
    public static Element BarsFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H;
        bool pv = spec.Preview, glow = !pv && pal.Dark;
        float motion = FluentMotion;
        int n = pv ? 20 : 40;
        float gap = pv ? 3f : MathF.Max(4f, 0.0066f * w), x0 = 0.11f * w;
        float bw = MathF.Max(1f, (0.78f * w - gap * (n - 1)) / n), mid = 0.5f * h, maxH = (pv ? 0.42f : 0.30f) * h;
        ColorF dotInk = Ink.Ink with { A = pal.Dark ? 0.85f : 0.7f };
        var map = new int[n];
        for (int k = 0; k < n; k++) map[k] = Geo.BarBand(k, n);
        // layout of the buffer: [glow × n][bodies × n][dots × n] — glow under everything, dots over
        int bodies = glow ? n : 0, dots = bodies + n, count = pv ? n : dots + n;
        var buf = new FluentSpriteBuffer(count);
        var bands = slab.Bands; var peaks = slab.Peaks; var sa = slab.A; var sb = slab.B;
        return FaceFrame(spec,
        [
            new CanvasChild(0f, 0f, new SpriteFieldEl
            {
                Width = w, Height = h, Kernel = SpriteKernel.Capsule,
                Instances = Prop.Of(() =>
                {
                    ColorF a = sa.Value, b = sb.Value;
                    var s = buf.Buffer;
                    for (int k = 0; k < n; k++)
                    {
                        float v = Geo.BarLevel(bands[map[k]].Value, motion), len = MathF.Max(2f * v * maxH, bw);
                        float x = x0 + k * (bw + gap) + 0.5f * bw;
                        var tint = ColorF.Lerp(a, b, Geo.BarTint(v));
                        if (glow) s[k] = FluentSprite(x, mid, len + bw, 2.4f * bw, FluentHalfPi, 1f, tint with { A = 0.26f });
                        s[bodies + k] = FluentSprite(x, mid, len, bw, FluentHalfPi, 0f, tint);
                        if (pv) continue;
                        float top = MathF.Max(Geo.BarLevel(peaks[map[k]].Value, motion) * maxH, 0.5f * len);
                        s[dots + k] = FluentSprite(x, mid - top - 0.45f * bw - 4f, 0.9f * bw, 0.9f * bw, FluentHalfPi, 0f, dotInk);
                    }
                    return buf.Next(count);
                }),
            }),
        ]);
    }

    // ══ RING ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The halo (A, alpha on the lows), up to four ripple rings (the slab's ripple ages — a kick or a downbeat
    /// spawns one, FluentDecelerate out), 96 radial capsules mirrored about the top (colour A → B by band), and the round
    /// cover with a coloured shadow, breathing 3 % on the kick. Preview: 36 capsules, the youngest ripple, an A disc.</summary>
    public static Element RingFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H, m = MathF.Min(w, h);
        bool pv = spec.Preview, dark = pal.Dark;
        float motion = FluentMotion;
        float cx = 0.5f * w, cy = 0.5f * h - (pv ? 0f : 0.04f * h), r = (pv ? 0.26f : 0.19f) * m;
        int n = pv ? 36 : 96;
        float bw = pv ? MathF.Max(1.5f, 0.022f * m) : MathF.Max(3f, 0.0076f * m);
        float inset = (pv ? 0.05f : 0.015f) * m, len0 = (pv ? 0.05f : 0.013f) * m, gain = (pv ? 0.16f : 0.13f) * m;
        var kids = new List<CanvasChild>(8);

        if (!pv)
        {
            float hd = 0.9f * m, opLo = dark ? 0.16f : 0.10f, opHi = dark ? 0.56f : 0.35f;
            var low = slab.Low;
            kids.Add(new CanvasChild(cx - 0.5f * hd, cy - 0.5f * hd, new BoxEl
            {
                Width = hd, Height = hd, Corners = Radii.Circle(hd), HitTestVisible = false,
                Gradient = FluentRadial(pal.A, 1f, 0.45f), GradientTo = FluentRadial(pal.Rotated().A, 1f, 0.45f), GradientMix = slab.MomentMix,
                Opacity = Prop.Of(() => opLo + (opHi - opLo) * Math.Clamp(low.Value * motion, 0f, 1f)),
            }));
        }

        // ripples: a full ring authored at its LAST radius and scaled up from 1/2.1 (a circle: uniform scale is exact;
        // the stroke thickens as it fades, which reads as the wave spreading)
        float thick = pv ? 1.5f : MathF.Max(2f, 0.0035f * m), rMax = r * Geo.RippleGrowth, rd = 2f * rMax + thick;
        var ripples = slab.Ripples;
        int shown = pv ? 1 : ripples.Length;
        for (int i = 0; i < shown; i++)
        {
            var age = ripples[i];
            Prop<float> opacity = pv
                ? Prop.Of(() => Geo.RippleOpacity(FluentYoungest(ripples), motion))
                : Prop.Of(() => Geo.RippleOpacity(age.Value, motion));
            Prop<Affine2D> grow = pv
                ? Prop.Of(() => { float z = Geo.RippleScale(FluentYoungest(ripples)) / Geo.RippleGrowth; return Affine2D.Scale(z, z); })
                : Prop.Of(() => { float z = Geo.RippleScale(age.Value) / Geo.RippleGrowth; return Affine2D.Scale(z, z); });
            kids.Add(new CanvasChild(cx - 0.5f * rd, cy - 0.5f * rd, new BoxEl
            {
                Width = rd, Height = rd, HitTestVisible = false, Arc = new ArcSpec(pal.A, thick, 0f, 360f, RoundCaps: false),
                Opacity = opacity, Transform = grow,
            }));
        }

        // the capsules: angle, cos/sin and band per capsule computed ONCE here; the thunk only reads bands and colours
        var angle = new float[n]; var cos = new float[n]; var sin = new float[n]; var map = new int[n];
        for (int k = 0; k < n; k++) { angle[k] = Geo.RingAngle(k, n); cos[k] = MathF.Cos(angle[k]); sin[k] = MathF.Sin(angle[k]); map[k] = Geo.RingBand(k, n); }
        var buf = new FluentSpriteBuffer(n);
        var bands = slab.Bands; var sa = slab.A; var sb = slab.B;
        kids.Add(new CanvasChild(0f, 0f, new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                ColorF a = sa.Value, b = sb.Value;
                var s = buf.Buffer;
                for (int k = 0; k < n; k++)
                {
                    int band = map[k];
                    float len = Geo.RingLength(bands[band].Value, len0, gain, motion), d = r + inset + 0.5f * len;
                    s[k] = FluentSprite(cx + cos[k] * d, cy + sin[k] * d, len, bw, angle[k], 0f, ColorF.Lerp(a, b, band / (float)Bands.Count) with { A = 0.95f });
                }
                return buf.Next(n);
            }),
        }));

        if (pv)
        {
            float dd = 1.4f * r;
            kids.Add(new CanvasChild(cx - 0.5f * dd, cy - 0.5f * dd, new BoxEl { Width = dd, Height = dd, Corners = Radii.Circle(dd), Fill = Prop.Bind(slab.A), Opacity = 0.9f, HitTestVisible = false }));
            return FaceFrame(spec, kids);
        }
        if (spec.Ambient) return FaceFrame(spec, kids);   // behind a layout the layout owns the cover: the ring alone
        float cover = 1.5f * r;
        var shadow = dark ? new ShadowSpec(50f, 0f, 0f, pal.A with { A = 0.5f })
                          : new ShadowSpec(36f, 10f, 0f, Ink.Ink with { A = 0.22f });
        var kick = slab.Kick;
        kids.Add(new CanvasChild(cx - 0.5f * cover, cy - 0.5f * cover, new BoxEl
        {
            Width = cover, Height = cover, Corners = Radii.Circle(cover), ClipToBounds = true, Shadow = shadow, HitTestVisible = false,
            Transform = Prop.Of(() => { float z = 1f + 0.03f * kick.Value * motion; return Affine2D.Scale(z, z); }),
            Children = [Controls.Artwork(spec.CoverUrl, cover, cover, 0.5f * cover, decodePx: 512)],
        }));
        return FaceFrame(spec, kids);
    }

    /// <summary>The youngest live ripple's age (1 = none) — the preview shows one ring, whichever fired last.</summary>
    static float FluentYoungest(FloatSignal[] ripples)
    {
        float a = 1f;
        foreach (var r in ripples) a = MathF.Min(a, r.Value);
        return a;
    }

    // ══ ORBIT ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Three rings — lows (A, outer), mids (B), highs (C, inner) — each a faint ink track and a live sweep of
    /// <c>0.08 + 0.8·e</c> of the turn, round-capped, spinning beat-locked at three speeds (still when the music is), with
    /// a soft glow twin under it; the BPM in the middle. Preview: thinner, no glow, no digits.
    /// <para>The tracks are <see cref="ArcSpec"/> rings. The sweeps are CAPSULE CHAINS in the face's one sprite field: an
    /// ArcSpec's sweep and colour are static, so a bound sweep would mean a remount per tick and a colour that snaps at a
    /// moment; a chain of short overlapping capsules along the circle IS a round-capped arc of any length, in the slab's
    /// cross-faded colours, for one draw.</para></summary>
    public static Element OrbitFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H, m = MathF.Min(w, h);
        bool pv = spec.Preview, glow = !pv, dark = pal.Dark;
        float motion = FluentMotion;
        float cx = 0.5f * w, cy = 0.5f * h - (pv ? 0f : 0.04f * h);
        float t = pv ? MathF.Max(2f, 0.06f * m) : MathF.Max(6f, 0.024f * m), g = 1.55f * t, half = pv ? 0.2f : 0.07f;
        var radii = new float[3];
        for (int i = 0; i < 3; i++) radii[i] = Geo.OrbitRadii[i] * m * (pv ? 1.15f : 1f);

        var kids = new List<CanvasChild>(6);
        ColorF track = Ink.Ink with { A = dark ? 0.08f : 0.07f };
        for (int i = 0; i < 3; i++)
        {
            float d = 2f * radii[i] + t;
            kids.Add(new CanvasChild(cx - 0.5f * d, cy - 0.5f * d, new BoxEl { Width = d, Height = d, HitTestVisible = false, Arc = new ArcSpec(track, t, 0f, 360f, RoundCaps: false) }));
        }

        int perRing = Geo.ArcChainCount((Geo.OrbitSweepMin + Geo.OrbitSweepGain) * MathF.Tau, half) + 1;   // + 1: float slack at the full sweep
        var buf = new FluentSpriteBuffer(3 * perRing * (glow ? 2 : 1));
        var lo = slab.Low; var mi = slab.Mid; var hi = slab.High; var beat = slab.BeatIndex; var phase = slab.BeatPhase;
        var sa = slab.A; var sb = slab.B; var sc = slab.C;
        ColorF underInk = Ink.Ink with { A = 0.07f };   // light arm: the glow becomes a soft ink under-arc (depth, not light)
        kids.Add(new CanvasChild(0f, 0f, new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                int bi = beat.Value; float ph = phase.Value;
                float e0 = lo.Value, e1 = mi.Value, e2 = hi.Value;
                ColorF c0 = sa.Value, c1 = sb.Value, c2 = sc.Value;
                var s = buf.Buffer; int at = 0;
                for (int pass = glow ? 0 : 1; pass < 2; pass++)   // every glow under every live sweep
                    for (int i = 0; i < 3; i++)
                    {
                        float e = i == 0 ? e0 : i == 1 ? e1 : e2;
                        ColorF c = i == 0 ? c0 : i == 1 ? c1 : c2;
                        float sweep = Geo.OrbitSweep(e, motion) * MathF.Tau;
                        float start = (Geo.SpinDeg(bi, ph, Geo.OrbitDegPerBeat[i] * motion) + 120f * i) * (MathF.PI / 180f);
                        at = pass == 0
                            ? FluentArcChain(s, at, cx, cy, radii[i], start, sweep, half, g, 1f, dark ? c with { A = 0.22f } : underInk)
                            : FluentArcChain(s, at, cx, cy, radii[i], start, sweep, half, t, 0f, c);
                    }
                return buf.Next(at);
            }),
        }));

        if (!pv)
        {
            // the tempo: kind-222's ×10 BPM, read when the track moves or the track table changes (the audio fields land
            // after the track does — paused too, so no beat heartbeat). One string per tempo change, never per tick.
            var current = Playback.Current;
            ushort lastTempo = ushort.MaxValue; string label = Geo.BpmLabel(0);
            float size = Math.Clamp(0.061f * m, 28f, 72f), boxW = 0.3f * m, boxH = size * 1.25f + 22f;
            kids.Add(new CanvasChild(cx - 0.5f * boxW, cy - 0.5f * boxH, new BoxEl
            {
                Width = boxW, Height = boxH, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Children =
                [
                    new TextEl(Prop.Of(() =>
                    {
                        _ = Entities.ScopeEpoch.Value;                // FIRST: a scope switch re-points the table read below
                        _ = Entities.Current.Tracks.Changed.Value;    // the audio group landing re-reads the tempo
                        var cur = current.Value;
                        ushort tempo = 0;
                        if (cur.Kind == EntityKind.Track && !cur.IsNone) { var tr = new Track(cur.Slot); if (tr.IsValid && tr.Knows(TrackFields.Audio)) tempo = tr.Tempo; }
                        if (tempo != lastTempo) { lastTempo = tempo; label = Geo.BpmLabel(tempo); }
                        return label;
                    }))
                    { Size = size, LineHeight = size * 1.25f, Weight = 600, FontFamily = Design.Type.DisplayFace, Color = Ink.Ink with { A = 0.9f }, MinSize = 24f, MaxLines = 1 },
                    new TextEl("BPM") { Size = 14f, LineHeight = 20f, Color = Ink.InkSecondary, MaxLines = 1 },
                ],
            }));
        }
        return FaceFrame(spec, kids);
    }

    /// <summary>Append one arc as a chain of capsules centred on the circle (0 rad = 12 o'clock, clockwise), tangent to
    /// it: the first and last straight parts end exactly at the arc's ends and their round caps overhang by half the
    /// width — an <see cref="ArcSpec"/> with round caps. Returns the next free index.</summary>
    static int FluentArcChain(Sprite[] into, int at, float cx, float cy, float radius, float startRad, float sweepRad, float half,
                              float width, float soft, ColorF c)
    {
        int count = Geo.ArcChainCount(sweepRad, half);
        float len = Geo.ArcChainStraight(count, sweepRad, half) * radius + width;
        uint rgba = Sprite.Pack(c);
        for (int j = 0; j < count; j++)
        {
            float th = Geo.ArcChainAngle(j, count, startRad, sweepRad, half);
            into[at++] = new Sprite { X = cx + radius * MathF.Sin(th), Y = cy - radius * MathF.Cos(th), W = len, H = width, Rot = th, Soft = soft, Rgba = rgba };
        }
        return at;
    }

    // ══ AURORA ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Three curtains (A lows, B mids, C highs): a baseline series whose top edge is the wave, bright at the edge
    /// and fading to nothing toward its foot (a gradient BY AMPLITUDE), additive on the dark arm. The drift speed is
    /// energy-scaled — <c>base·(0.15 + e)</c> — so silence is nearly still and a paused stage is still. 256 points
    /// (48 preview).</summary>
    public static Element AuroraFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H;
        bool pv = spec.Preview, dark = pal.Dark;
        int points = pv ? 48 : 256;
        var to = pal.Rotated();
        float alpha = dark ? 0.6f : 0.55f, motion = FluentMotion;
        return FaceFrame(spec,
        [
            AuroraCurtain(slab, 0, w, h, points, pal.A, to.A, slab.Low, alpha, dark, motion),
            AuroraCurtain(slab, 1, w, h, points, pal.B, to.B, slab.Mid, alpha, dark, motion),
            AuroraCurtain(slab, 2, w, h, points, pal.C, to.C, slab.High, alpha, dark, motion),
        ]);
    }

    /// <summary>One curtain: its box runs from the face top to 10 % below its base line; the samples are the top edge as a
    /// fraction of that box, refilled by the bind. The phase integrates the drift with dt from the frame clock (clamped),
    /// so the speed follows the energy and a frozen slab freezes the curtain. The beat phase is the heartbeat that keeps
    /// the bind firing through a steady note.</summary>
    static CanvasChild AuroraCurtain(Slab slab, int layer, float w, float h, int points, ColorF c, ColorF to, FloatSignal e,
                                     float alpha, bool dark, float motion)
    {
        float baseY = Geo.AuroraBase[layer] * h, boxH = baseY + 0.10f * h;
        float floor = 0.10f * h / boxH, scale = h / boxH, speed = Geo.AuroraSpeed[layer];
        var stops = Geo.AuroraStops(floor, (Geo.AuroraAmpMin + Geo.AuroraAmpGain) * scale);
        var buf = new float[points];
        uint version = 0; float phase = 0f; long last = 0;
        var heartbeat = slab.BeatPhase;
        return new CanvasChild(0f, 0f, new SeriesEl
        {
            Width = w, Height = boxH, Shape = SeriesShape.Baseline, Amplitude = 1f,
            Gradient = FluentCurtain(c, stops, alpha), GradientTo = FluentCurtain(to, stops, alpha), GradientMix = slab.MomentMix,
            Blend = dark ? PaintBlend.Additive : PaintBlend.SrcOver,
            Samples = Prop.Of(() =>
            {
                float ev = e.Value;
                _ = heartbeat.Value;
                long now = Design.FrameTime.NowMs;
                float dt = last == 0 ? 0f : Math.Clamp(now - last, 0L, 50L);
                last = now;
                phase = Geo.AuroraAdvance(phase, dt, speed, ev, motion);
                Geo.AuroraRibbon(buf, floor, Geo.AuroraAmp(ev, motion) * scale, phase, layer);
                return new SeriesSamples(buf, points, ++version);
            }),
        });
    }

    // ══ TIMELINE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The WHOLE song as 360 mirrored capsules (kind-237, resampled once per track by the clock): ink for what is
    /// left, A for what was played — the A field is a twin clipped to the playhead by a translated clip (two bound
    /// translates, no relayout). Under it a beat ruler of ±16 beats that slides with the music (downbeats taller, in B),
    /// and the playhead: an ink capsule with a soft A glow that follows the level. No waveform (a podcast, a local file):
    /// a dotted ink rule, ticks and playhead still honest. Preview: 90 capsules, no ruler, no playhead.</summary>
    public static Element TimelineFace(Slab slab, in Palette pal, in FaceSpec spec)
    {
        float w = spec.W, h = spec.H;
        bool pv = spec.Preview, dark = pal.Dark, glow = !pv;
        float motion = FluentMotion;
        int p = pv ? 90 : 360;
        float x0 = 0.06f * w, tw = 0.88f * w, mid = 0.5f * h, amp = (pv ? 0.36f : 0.2f) * h, bw = MathF.Max(1f, tw / p * 0.62f);
        var lo = slab.TimelineLow; var mi = slab.TimelineMid; var hi = slab.TimelineHigh;
        var sa = slab.A; var progress = slab.Progress;
        ColorF rest = Ink.Ink with { A = dark ? 0.22f : 0.18f };
        var inkBuf = new FluentSpriteBuffer(p); var playedBuf = new FluentSpriteBuffer(p);

        var unplayed = new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                TimelineFill(inkBuf.Buffer, lo.Current, mi.Current, hi.Current, p, x0, tw, mid, amp, bw, rest);
                return inkBuf.Next(p);
            }),
        };
        var played = new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                TimelineFill(playedBuf.Buffer, lo.Current, mi.Current, hi.Current, p, x0, tw, mid, amp, bw, sa.Value);
                return playedBuf.Next(p);
            }),
        };
        // the clip: a face-sized ClipToBounds box translated so its RIGHT edge is the playhead, holding a box translated
        // back by the same amount — the capsules stay put, the window over them grows (§1.3: move a clip, never scale)
        var kids = new List<CanvasChild>(3)
        {
            new CanvasChild(0f, 0f, unplayed),
            new CanvasChild(0f, 0f, new BoxEl
            {
                Width = w, Height = h, ClipToBounds = true, HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(Geo.PlayedX(progress.Value, x0, tw) - w, 0f)),
                Children =
                [
                    new BoxEl
                    {
                        Width = w, Height = h, HitTestVisible = false,
                        Transform = Prop.Of(() => Affine2D.Translation(w - Geo.PlayedX(progress.Value, x0, tw), 0f)),
                        Children = [played],
                    },
                ],
            }),
        };
        if (pv) return FaceFrame(spec, kids);

        // the ruler + playhead: ONE small field rebuilt per clock batch (≤ 35 sprites)
        int ticks = 2 * Geo.TickSpan + 1;
        var overlay = new FluentSpriteBuffer(ticks + 2);
        float rulerHalf = MathF.Min(0.12f * tw, 220f), step = rulerHalf / Geo.TickSpan;
        float tickTop = mid + amp + MathF.Max(12f, 0.017f * h), tickLen = MathF.Max(5f, 0.0068f * h), headLen = 2f * amp + MathF.Max(32f, 0.044f * h);
        ColorF tickInk = Ink.Ink with { A = 0.35f }, headInk = Ink.Ink with { A = 0.95f };
        var barPhase = slab.BarPhase; var beatPhase = slab.BeatPhase; var level = slab.Level; var sb = slab.B;
        var bar = slab.Bar; var beat = slab.BeatIndex;
        var bars = new Geo.BarCounter();   // the bar's real beat count, learned from the bar edges (never assumes 4/4)
        kids.Add(new CanvasChild(0f, 0f, new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                float px = Geo.PlayedX(progress.Value, x0, tw), ph = beatPhase.Value, bp = barPhase.Value;
                int bi = beat.Value;
                bars.Observe(bar.Value, bi, bp);
                int inBar = bars.BeatInBar(bi, bp, ph), span = bars.Span;
                ColorF a = sa.Value, b = sb.Value;
                var s = overlay.Buffer; int at = 0;
                if (glow) s[at++] = FluentSprite(px, mid, headLen + 16f, 22f, FluentHalfPi, 1f, a with { A = Geo.PlayheadGlow(level.Value, motion, dark) });
                for (int j = -Geo.TickSpan; j <= Geo.TickSpan; j++)
                {
                    float x = px + (j - ph) * step;
                    if (x < x0 || x > x0 + tw) continue;
                    bool down = Geo.IsDownbeat(inBar, j, span);
                    float len = down ? 2f * tickLen : tickLen, fade = Geo.TickFade(j - ph, Geo.TickSpan);
                    s[at++] = FluentSprite(x, tickTop + 0.5f * len, len, 2f, FluentHalfPi, 0f, down ? b with { A = 0.85f * fade } : tickInk with { A = tickInk.A * fade });
                }
                s[at++] = FluentSprite(px, mid, headLen, 4f, FluentHalfPi, 0f, headInk);
                return overlay.Next(at);
            }),
        }));
        return FaceFrame(spec, kids);
    }

    /// <summary>The 360 (90) song capsules from the three timeline series: bucket-max per capsule (peaks survive the
    /// preview's 4:1), mirrored about the mid line, never shorter than round.</summary>
    static void TimelineFill(Sprite[] into, SeriesSamples lo, SeriesSamples mi, SeriesSamples hi, int p, float x0, float tw, float mid, float amp, float bw, ColorF c)
    {
        uint rgba = Sprite.Pack(c);
        ReadOnlySpan<float> l = lo.AsSpan(), m = mi.AsSpan(), hh = hi.AsSpan();
        for (int k = 0; k < p; k++)
        {
            float v = Geo.TimelineHeight(Geo.Bucket(l, k, p), Geo.Bucket(m, k, p), Geo.Bucket(hh, k, p));
            into[k] = new Sprite { X = x0 + tw * (k + 0.5f) / p, Y = mid, W = MathF.Max(2f * v * amp, bw), H = bw, Rot = FluentHalfPi, Soft = 0f, Rgba = rgba };
        }
    }

    // ── shared parts ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A reused sprite buffer + a PLAIN version counter for a <c>SpriteFieldEl</c> whose Instances THUNK refills
    /// it. Not <see cref="InstanceSource"/>: that one's version is a signal for producers outside the bind (a ticker);
    /// a bind that bumped a signal it also read would re-fire itself.</summary>
    sealed class FluentSpriteBuffer(int capacity)
    {
        public readonly Sprite[] Buffer = new Sprite[capacity];
        uint _version;
        public SpriteInstances Next(int count) => new(Buffer, count, ++_version);
    }

    static Sprite FluentSprite(float x, float y, float length, float width, float rot, float soft, ColorF c)
        => new() { X = x, Y = y, W = length, H = width, Rot = rot, Soft = soft, Rgba = Sprite.Pack(c) };

    /// <summary>A cloud/halo disc: full colour at <paramref name="a0"/>, <paramref name="a1"/> at 60 %, gone at the rim.</summary>
    static GradientSpec FluentRadial(ColorF c, float a0, float a1)
        => new(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = a0 }), new GradientStop(0.6f, c with { A = a1 }), new GradientStop(1f, c with { A = 0f })]);

    /// <summary>A curtain's fade BY AMPLITUDE: nothing at the foot, faint at the base line, full at the wave's reach,
    /// thinning above it.</summary>
    static GradientSpec FluentCurtain(ColorF c, (float Floor, float Peak) stops, float alpha)
        => new(GradientShape.Linear, 0f,
        [
            new GradientStop(0f, c with { A = 0f }), new GradientStop(stops.Floor, c with { A = 0.3f * alpha }),
            new GradientStop(stops.Peak, c with { A = alpha }), new GradientStop(1f, c with { A = 0.3f * alpha }),
        ]);

    // ══ THE PURE ARITHMETIC (Wavee.Tests: VisualizerFluentGeometryTests) ═════════════════════════════════════════════

    /// <summary>Every number the Fluent faces compute: band maps, levels, ripple easing, arc chains, beat spin, the aurora
    /// wave, the timeline resample, the beat ruler (and its bar counter), the BPM label. No engine type, no signal, no
    /// allocation (except the BPM string and one BarCounter per face build). Public: this assembly has no InternalsVisibleTo.</summary>
    public static class FluentGeometry
    {
        public const float RestBar = 0.03f, RippleGrowth = 2.1f;
        public const float OrbitSweepMin = 0.08f, OrbitSweepGain = 0.8f, OrbitSweepRest = 0.3f;
        public const float AuroraAmpMin = 0.05f, AuroraAmpGain = 0.22f;
        /// <summary>Arc chains place capsule centres at most this many half-straights apart (2 would only touch).</summary>
        public const float ChainStep = 1.2f;
        public const int TickSpan = 16, BeatsPerBar = 4;

        /// <summary>Bloom's three lit clouds (A, B, C): centre as a fraction of the box, radius rest + gain·e as a fraction
        /// of min(W, H), drift as a fraction of the box, loop period.</summary>
        public static readonly (float Fx, float Fy, float Base, float Gain, float DriftX, float DriftY, float PeriodSec)[] Clouds =
        [
            (0.30f, 0.38f, 0.55f, 0.35f, 0.05f, -0.04f, 28f),
            (0.66f, 0.58f, 0.48f, 0.30f, -0.06f, 0.05f, 32f),
            (0.48f, 0.80f, 0.42f, 0.30f, 0.04f, 0.05f, 36f),
        ];
        /// <summary>Orbit: ring radius as a fraction of min(W, H) (lows outer), and the beat-locked spin (the prototype's
        /// 0.00012 / −0.00018 / 0.00026 rad/ms at its 117 BPM, per beat).</summary>
        public static readonly float[] OrbitRadii = [0.36f, 0.28f, 0.20f], OrbitDegPerBeat = [3.5f, -5.3f, 7.6f];
        /// <summary>Aurora: base line as a fraction of the height, drift speed in rad/ms at full energy.</summary>
        public static readonly float[] AuroraBase = [0.55f, 0.62f, 0.70f], AuroraSpeed = [0.0004f, -0.0006f, 0.0009f];

        static float Unit(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

        // ── Bloom ──
        /// <summary>A cloud's radius fraction: rest + gain·e; reduced motion rests at mid.</summary>
        public static float CloudScale(float e, float rest, float gain, float motion) => rest + gain * (motion > 0f ? Unit(e) : 0.5f);
        /// <summary>A cloud's alpha: 0.55 + 0.6·e, capped at 1; reduced motion rests at mid.</summary>
        public static float CloudOpacity(float e, float motion) => MathF.Min(1f, 0.55f + 0.6f * (motion > 0f ? Unit(e) : 0.5f));
        /// <summary>The cover shadow's alpha relative to its authored 0.75: (0.45 + 0.3·low) / 0.75.</summary>
        public static float BloomShadowOpacity(float low, float motion) => (0.45f + 0.3f * Unit(low) * motion) / 0.75f;

        // ── Bars ──
        /// <summary>Bar k of n → band, mirrored from the centre out: the middle pair is the bass, the edges the treble.</summary>
        public static int BarBand(int k, int n)
        {
            float half = 0.5f * n;
            int band = (int)MathF.Round(MathF.Abs(k - half + 0.5f) / half * (Bands.Count - 8));
            return Math.Clamp(band, 0, Bands.Count - 1);
        }
        /// <summary>A bar's level, never below the floor; reduced motion = the floor.</summary>
        public static float BarLevel(float level, float motion) => MathF.Max(RestBar, Unit(level) * motion);
        /// <summary>How far a bar's colour has moved from A toward B: none until 0.35, 0.7 at full height.</summary>
        public static float BarTint(float v) => 0.7f * Unit((v - 0.35f) / 0.65f);

        // ── Ring ──
        /// <summary>Capsule k of n → band, mirrored about the top (k = 0): the bass at 12 o'clock, the treble at 6.</summary>
        public static int RingBand(int k, int n)
        {
            int steps = Math.Min(k, n - k);   // whole steps from the top, so k and n − k map EXACTLY alike
            return Math.Clamp((int)MathF.Round(2f * steps / n * (Bands.Count - 10)), 0, Bands.Count - 1);
        }
        /// <summary>Capsule k's screen angle (radians, y down): k = 0 points straight up, then clockwise.</summary>
        public static float RingAngle(int k, int n) => k / (float)n * MathF.Tau - MathF.PI * 0.5f;
        public static float RingLength(float level, float rest, float gain, float motion) => rest + Unit(level) * gain * motion;
        /// <summary>A ripple's radius in ring radii at age 0..1: FluentDecelerate-like 1 − (1 − t)³ out to 2.1.</summary>
        public static float RippleScale(float age)
        {
            float t = 1f - Unit(age);
            return 1f + (1f - t * t * t) * (RippleGrowth - 1f);
        }
        /// <summary>0.45 at birth, 0 at age 1 (and for "none"); reduced motion: no ripples.</summary>
        public static float RippleOpacity(float age, float motion) => motion <= 0f || !float.IsFinite(age) || age >= 1f ? 0f : 0.45f * (1f - Unit(age));

        // ── Orbit ──
        /// <summary>A ring's sweep as a fraction of the turn: 0.08 + 0.8·e; reduced motion rests at 0.3.</summary>
        public static float OrbitSweep(float e, float motion) => motion > 0f ? OrbitSweepMin + OrbitSweepGain * Unit(e) : OrbitSweepRest;
        /// <summary>The beat-locked spin in degrees, wrapped to (−360, 360): (beat + phase)·degPerBeat in double, so a long
        /// track's beat count never costs precision.</summary>
        public static float SpinDeg(int beatIndex, float beatPhase, float degPerBeat)
            => (float)(((beatIndex + (double)Unit(beatPhase)) * degPerBeat) % 360.0);
        /// <summary>Capsules in a chain for an arc of <paramref name="sweepRad"/> whose capsules have straight parts of
        /// 2·<paramref name="half"/> radians: one when the arc is no longer than one straight part.</summary>
        public static int ArcChainCount(float sweepRad, float half)
        {
            if (!(sweepRad > 2f * half) || half <= 0f) return 1;
            return (int)MathF.Ceiling((sweepRad - 2f * half) / (ChainStep * half)) + 1;
        }
        /// <summary>The straight part of each capsule in radians: the whole sweep for a single capsule, else 2·half.</summary>
        public static float ArcChainStraight(int count, float sweepRad, float half) => count <= 1 ? MathF.Max(0f, sweepRad) : 2f * half;
        /// <summary>Capsule j's centre angle: the first straight part starts at the arc's start, the last ends at its end.</summary>
        public static float ArcChainAngle(int j, int count, float startRad, float sweepRad, float half)
            => count <= 1 ? startRad + 0.5f * MathF.Max(0f, sweepRad) : startRad + half + j * (sweepRad - 2f * half) / (count - 1);

        /// <summary>The digits under Orbit's rings: kind-222's tempo ×10, rounded to whole BPM; an em dash when unknown.</summary>
        public static string BpmLabel(ushort tempoX10) => tempoX10 == 0 ? "—" : ((tempoX10 + 5) / 10).ToString(CultureInfo.InvariantCulture);

        // ── Aurora ──
        /// <summary>A curtain's reach as a fraction of the face height: 0.05 + 0.22·e; reduced motion = 0.05.</summary>
        public static float AuroraAmp(float e, float motion) => AuroraAmpMin + AuroraAmpGain * Unit(e) * motion;
        /// <summary>One drift step: phase += dt·speed·(0.15 + e), wrapped where both waves (×1 and ×1.7) repeat (20π).</summary>
        public static float AuroraAdvance(float phase, float dtMs, float speed, float e, float motion)
        {
            float p = phase + dtMs * speed * (0.15f + Unit(e)) * motion;
            const float Wrap = 20f * MathF.PI;
            return float.IsFinite(p) ? p % Wrap : 0f;
        }
        /// <summary>A curtain's top edge as a fraction of its box: the floor (the base line) plus the prototype's two-sine
        /// wave, enveloped by sin(πu) so both ends meet the base line.</summary>
        public static void AuroraRibbon(Span<float> into, float floorFrac, float ampFrac, float phase, int layer)
        {
            int n = into.Length;
            for (int k = 0; k < n; k++)
            {
                float u = n > 1 ? k / (float)(n - 1) : 0.5f;
                float wave = MathF.Sin(u * 6.2f + phase + layer) * 0.55f + MathF.Sin(u * 13.1f - phase * 1.7f) * 0.25f + 0.3f;
                into[k] = Unit(floorFrac + ampFrac * wave * MathF.Sin(MathF.PI * u));
            }
        }
        /// <summary>The curtain gradient's two inner offsets (by amplitude): the base line, and 80 % of the full reach above
        /// it — strictly inside (0, 1) and increasing.</summary>
        public static (float Floor, float Peak) AuroraStops(float floorFrac, float reachFrac)
        {
            float f = Math.Clamp(float.IsFinite(floorFrac) ? floorFrac : 0.1f, 0.01f, 0.9f);
            float p = Math.Clamp(f + 0.8f * (float.IsFinite(reachFrac) ? reachFrac : 0f), f + 0.01f, 0.97f);
            return (f, p);
        }

        // ── Timeline ──
        /// <summary>One capsule's height (0..1) from the three kind-237 bands: a bass-weighted mix, square-rooted so the
        /// quiet passages still read.</summary>
        public static float TimelineHeight(float lo, float mi, float hi) => MathF.Sqrt(Unit(0.55f * Unit(lo) + 0.3f * Unit(mi) + 0.15f * Unit(hi)));
        /// <summary>The max over capsule k's share of <paramref name="src"/> (p capsules cover every sample once); 0 empty.</summary>
        public static float Bucket(ReadOnlySpan<float> src, int k, int p)
        {
            int n = src.Length;
            if (n == 0 || p <= 0) return 0f;
            int a = (int)((long)k * n / p), b = Math.Max(a + 1, (int)((long)(k + 1) * n / p));
            float v = 0f;
            for (int i = a; i < b && i < n; i++) v = MathF.Max(v, src[i]);
            return v;
        }
        /// <summary>The playhead's x: the timeline's left edge plus the clamped progress of its width.</summary>
        public static float PlayedX(float progress, float x0, float width) => x0 + Unit(progress) * width;
        /// <summary>Which beat of the bar is the current one (0 = the downbeat), from the bar and beat phases.</summary>
        public static int BeatInBar(float barPhase, float beatPhase) => BeatInBar(barPhase, beatPhase, BeatsPerBar);
        /// <summary>The same over a bar of <paramref name="beatsPerBar"/> beats (the phase is the beat's place over the bar's
        /// real beat count — Visualizer.Model's BarOfGrid).</summary>
        public static int BeatInBar(float barPhase, float beatPhase, int beatsPerBar)
        {
            int n = Math.Max(1, beatsPerBar);
            int b = (int)MathF.Round(Unit(barPhase) * n - Unit(beatPhase));
            return ((b % n) + n) % n;
        }
        /// <summary>Is the tick <paramref name="j"/> beats from now a bar start?</summary>
        public static bool IsDownbeat(int beatInBar, int j) => IsDownbeat(beatInBar, j, BeatsPerBar);
        /// <summary>The same over bars of <paramref name="beatsPerBar"/> beats.</summary>
        public static bool IsDownbeat(int beatInBar, int j, int beatsPerBar)
        {
            int n = Math.Max(1, beatsPerBar);
            return (((beatInBar + j) % n) + n) % n == 0;
        }

        /// <summary>The longest bar the ruler believes (a longer gap between two downbeats is a seek, not a bar).</summary>
        public const int MaxBeatsPerBar = 12;
        /// <summary>A bar edge counts as a CROSSING only while the new bar's phase is under this (a seek lands anywhere).</summary>
        public const float EdgePhase = 0.25f;

        /// <summary>The beat ruler's bar clock: the beats per bar LEARNED from the slab's bar edges (the beats between two
        /// consecutive downbeats crossed — a 3/4 or 6/8 grid marks its own bars), and the beat inside the bar counted from
        /// the last downbeat crossed. Until an edge anchors it (a mount, a seek) it reads the bar phase over the last
        /// learned span (4 to start). Allocation-free; one per Timeline face, fed by its bind.</summary>
        public sealed class BarCounter
        {
            int _bar = int.MinValue, _anchorBeat = -1, _span = BeatsPerBar;

            /// <summary>The beats per bar the ruler marks.</summary>
            public int Span => _span;

            /// <summary>Feed the slab's bar, beat index and bar phase. An edge to the NEXT bar that lands near its start (a
            /// crossing) anchors the count; two anchored edges in a row measure the span (2..<see cref="MaxBeatsPerBar"/>).
            /// Any other bar change (a seek) drops the anchor and keeps the span.</summary>
            public void Observe(int bar, int beat, float barPhase)
            {
                if (bar == _bar) return;
                bool edge = _bar != int.MinValue && bar == _bar + 1 && Unit(barPhase) < EdgePhase;
                if (edge)
                {
                    int span = beat - _anchorBeat;
                    if (_anchorBeat >= 0 && span >= 2 && span <= MaxBeatsPerBar) _span = span;
                    _anchorBeat = beat;
                }
                else _anchorBeat = -1;
                _bar = bar;
            }

            /// <summary>The current beat's place in its bar (0 = the downbeat): counted from the anchor, else the phases.</summary>
            public int BeatInBar(int beat, float barPhase, float beatPhase)
            {
                int k = beat - _anchorBeat;
                return _anchorBeat >= 0 && k >= 0 ? k % _span : FluentGeometry.BeatInBar(barPhase, beatPhase, _span);
            }
        }
        /// <summary>A ruler tick's alpha by its distance from the playhead in beats: 1 at the head, fading to 0 past the span.</summary>
        public static float TickFade(float beatsAway, int span)
        {
            float d = MathF.Abs(beatsAway) / (span + 1);
            return Unit(1f - d * d);
        }
        /// <summary>The playhead glow's alpha: a resting light plus the level; reduced motion holds the rest.</summary>
        public static float PlayheadGlow(float level, float motion, bool dark) => dark ? 0.30f + 0.45f * Unit(level) * motion : 0.18f + 0.25f * Unit(level) * motion;
    }
}
