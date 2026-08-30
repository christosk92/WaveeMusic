using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The now-playing equalizer — three bottom-anchored bars, looping + phase-staggered while PLAYING, settled at a low
// static height when paused. Shared by the track rows (#-cell) AND the content cards' now-playing overlay. Keyed by
// `animate` (play↔pause of the TRACK) so that flip remounts; hover-pause does NOT change the Key (no restart / churn).
//
// Motion is a pixel-quantized UseInterval + bound Transform (SeekBar idiom) — NOT UseKeyframes on the anim slab.
// When the bars sit under a HoverOpacity=0 reveal, pass the SAME hover signal that drives that fade as `paused` so
// they stop ticking while invisible (do not flip animate/Key on hover).
//
// ONE ticker for all three bars, owned by the HOST. Per-bar intervals were three independent 15 Hz timers with
// arbitrary phase — up to 45 distinct wake instants/s, each moving ONE bar, so every fire dirtied the scene and
// presented, and skip-submit could almost never see a byte-identical frame (a DIFFERENT bar moved each time).
// Measured: ~80% of the whole playing-state wake budget. Batching the three writes also collapses them into ONE
// FrameRequested (ReactiveCore.Batch), instead of one per bar.
public static class WaveeEqualizer
{
    public static Element Of(bool animate, ColorF color, float height = 13f, IReadSignal<bool>? paused = null)
        => Embed.Comp(new EqHostProps(animate, static () => default, height, paused, color), () => new EqHost());

    public static Element Of(bool animate, Func<ColorF> color, float height = 13f, IReadSignal<bool>? paused = null)
        => Embed.Comp(new EqHostProps(animate, color, height, paused, null), () => new EqHost());

    sealed record EqHostProps(bool Animate, Func<ColorF> Color, float Height, IReadSignal<bool>? Paused, ColorF? FrozenColor);
    // The bar is a pure consumer: it binds the signal the host ticks. No timer, no pattern, no phase of its own.
    sealed record EqBarProps(FloatSignal ScaleY, Func<ColorF> Color, float Height, ColorF? FrozenColor);

    sealed class EqHost : Component
    {
        const float LoopMs = 850f;
        // ~30 Hz. Be honest about the trade: there is no partial repaint, so every visible ScaleY change costs a
        // FULL-WINDOW Present — motion IS presents, and the tick rate *is* the present rate for this widget. 15 Hz with
        // 2-device-px steps read as a visibly choppy VU meter; 30 Hz with 1-px steps is the smoothness floor that still
        // costs a quarter of the original 120 Hz continuous-float track. The real fix for "smooth AND cheap" is making a
        // frame cheap (opaque-PSO eligibility for row chrome), not throttling this further.
        const float TickMs = 1000f / 30f;

        static readonly float[][] Patterns =
        [
            [0.35f, 0.95f, 0.45f, 1.00f, 0.35f],
            [0.85f, 0.40f, 1.00f, 0.55f, 0.85f],
            [0.50f, 1.00f, 0.35f, 0.80f, 0.50f],
        ];

        // Stable for the host's lifetime — the bars bind these, so a play↔pause Key flip remounts the bars without
        // disturbing the signals they read.
        readonly FloatSignal[] _scaleY = [new(0.4f), new(0.4f), new(0.4f)];
        long _startMs;

        public override Element Render()
        {
            var p = UsePropsOrDefault<EqHostProps>();
            if (p is null) return new BoxEl();
            bool animate = p.Animate;
            bool paused = p.Paused?.Value ?? false;   // subscribe — pause without remount
            float scale = UseContext(Viewport.Scale);
            if (scale <= 0f) scale = 1f;

            UseEffect(() =>
            {
                if (!animate) { WriteAll(0.4f); return; }
                _startMs = Environment.TickCount64;
                Tick(p.Height, scale);
            });
            UseInterval(() => Tick(p.Height, scale), TickMs, enabled: animate && !paused);

            return new BoxEl
            {
                Key = animate ? "eq-play" : "eq-pause",
                Direction = 0, AlignItems = FlexAlign.End, Justify = FlexJustify.Center, Gap = 2f, Height = p.Height,
                Children =
                [
                    Embed.Comp(new EqBarProps(_scaleY[0], p.Color, p.Height, p.FrozenColor), () => new EqBar()),
                    Embed.Comp(new EqBarProps(_scaleY[1], p.Color, p.Height, p.FrozenColor), () => new EqBar()),
                    Embed.Comp(new EqBarProps(_scaleY[2], p.Color, p.Height, p.FrozenColor), () => new EqBar()),
                ],
            };
        }

        // One wall-clock sample drives all three bars. Writes are batched so the three signal sets coalesce into a
        // SINGLE FrameRequested + one effect flush, instead of one wake per bar.
        void Tick(float heightDip, float scale)
        {
            float u = (Environment.TickCount64 - _startMs) / LoopMs;
            u -= MathF.Floor(u);
            // Snap to WHOLE device pixels of the laid-out bar: crisper edges than a fractional height, and a tick that
            // lands on the same pixel for all three bars is a true no-op (below) so skip-submit can elide it.
            float hPx = heightDip * scale;
            Span<float> next = stackalloc float[3];
            bool anyChanged = false;
            for (int i = 0; i < 3; i++)
            {
                float sy = Sample(Patterns[i], u);
                float q = hPx > 1f ? MathF.Round(sy * hPx) / hPx : sy;
                next[i] = q;
                if (q != _scaleY[i].Peek()) anyChanged = true;
            }
            if (!anyChanged) return;   // nothing crossed a step this tick — leave the scene clean so skip-submit elides
            float n0 = next[0], n1 = next[1], n2 = next[2];
            void WriteChanged()
            {
                if (n0 != _scaleY[0].Peek()) _scaleY[0].Value = n0;
                if (n1 != _scaleY[1].Peek()) _scaleY[1].Value = n1;
                if (n2 != _scaleY[2].Peek()) _scaleY[2].Value = n2;
            }
            if (Context.Runtime is { } rt) rt.Batch(WriteChanged); else WriteChanged();
        }

        void WriteAll(float v)
        {
            void Write()
            {
                for (int i = 0; i < 3; i++) if (v != _scaleY[i].Peek()) _scaleY[i].Value = v;
            }
            if (Context.Runtime is { } rt) rt.Batch(Write); else Write();
        }

        static float Sample(float[] keys, float u)
        {
            float t = u * 4f;
            int i = (int)MathF.Floor(t);
            if (i >= 4) return keys[4];
            float f = t - i;
            return keys[i] + (keys[i + 1] - keys[i]) * f;
        }
    }

    sealed class EqBar : Component
    {
        public override Element Render()
        {
            var p = UsePropsOrDefault<EqBarProps>();
            if (p is null) return new BoxEl();
            var sig = p.ScaleY;
            Func<Affine2D> bind = () => Affine2D.Scale(1f, MathF.Max(sig.Value, 1e-3f));
            ColorF fill = p.FrozenColor ?? p.Color();
            return new BoxEl
            {
                Width = 2.5f, Height = p.Height, Corners = CornerRadius4.All(1.25f), Fill = fill,
                AlignSelf = FlexAlign.End, TransformOriginY = 1f,
                Transform = bind,
            };
        }
    }
}
