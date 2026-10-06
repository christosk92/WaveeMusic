// ── Shell/Visualizer.Strip.UI.cs ───────────────────────────────────────────────────────────────────────────────────────
// The spectrum the visualizer LAYOUTS draw (Stage.Layouts.cs): StripBars (the Main board's 72 accent bars), StripLine (the hero
// boards' 120 / 140 ink bars) and RingAround (the Centered board's ring of 96 radial capsules about the cover)
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 220 lines
// Spec: Main.dc.html (72 bars · gap 4 · top radius 3 · height 10 + 104·env·(1 − 0.5x) of 120 · alpha 0.22 + 0.38·env),
//       Hero.dc.html (120 bars · gap 3 · 4 + 44·env of 56 · ink at 0.12 + 0.33·env), HeroLyrics.dc.html (140 bars · 36 tall),
//       Centered.dc.html (96 capsules · r = 232 of a 320 cover · len 6 + 38·env · width 4 · alpha 0.3 + 0.5·env)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE small RepaintBoundary per strip, ONE SpriteFieldEl in it, refilled by a Prop.Of thunk over the slab's band signals — the
// Bars / Ring faces' contract (Visualizer.Fluent.UI.cs): the thunk fires at most once per clock batch, so a settled stage costs
// nothing, and a step re-rasters only this box (the strip's rect or the ring's square), never the lyrics or the photo around it.
// Reduced motion is a VALUE (FluentMotion = 0): every bar rests at its floor (and the lease is None, so nothing ticks anyway).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

using Geo = Wavee.Visualizer.FluentGeometry;
using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Visualizer
{
    /// <summary>The Main board's bar count and the hero boards' line counts.</summary>
    public const int StripBarCount = 72, StripLineCount = 120, StripLineLyricsCount = 140, StripRingCount = 96;

    /// <summary>The strip box: a clipped, hit-transparent, repainting-on-its-own box of exactly the strip's rect.</summary>
    static Element StripFrame(float w, float h, SpriteFieldEl field) => new BoxEl
    {
        Width = w, Height = h, ClipToBounds = true, HitTestVisible = false, RepaintBoundary = true,
        Children = [field],
    };

    /// <summary>Band value at <paramref name="x01"/> (0..1 across the strip), linearly interpolated between the 48 bands.</summary>
    static float StripBand(ReadOnlySpan<float> bands, float x01)
    {
        float pos = Math.Clamp(x01, 0f, 1f) * (Bands.Count - 1);
        int lo = (int)pos, hi = Math.Min(lo + 1, Bands.Count - 1);
        float f = pos - lo;
        return bands[lo] + (bands[hi] - bands[lo]) * f;
    }

    /// <summary>The Main board's strip: <see cref="StripBarCount"/> bars filling the strip (gap ≈ 0.3 % of its width), bottom-anchored,
    /// the slab's accent at alpha 0.22 + 0.38·v, each bar's band interpolated from the 48 and tapered by (1 − 0.5x) like the board.</summary>
    public static Element StripBars(Slab slab, float w, float h)
    {
        int n = StripBarCount;
        float motion = FluentMotion;
        float gap = MathF.Max(3f, 0.003f * w), bw = MathF.Max(2f, (w - gap * (n - 1)) / n), r = 0.5f * bw;
        var buf = new FluentSpriteBuffer(n);
        var bands = slab.Bands; var accent = slab.Accent;
        return StripFrame(w, h, new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                ColorF c = accent.Value;
                Span<float> b = stackalloc float[Bands.Count];
                for (int i = 0; i < b.Length; i++) b[i] = bands[i].Value;
                var s = buf.Buffer;
                for (int i = 0; i < n; i++)
                {
                    float x01 = i / (float)(n - 1);
                    float v = Geo.BarLevel(StripBand(b, x01), motion) * (1f - 0.5f * x01);
                    float hgt = MathF.Min(h, h * (0.083f + 0.867f * v));
                    // the foot is flat on the board: the capsule's rounded foot hangs r below the clipped box
                    float len = hgt + r, cy = h - hgt + 0.5f * len;
                    s[i] = FluentSprite(0.5f * bw + i * (bw + gap), cy, len, bw, FluentHalfPi, 0f, c with { A = 0.22f + 0.38f * v });
                }
                return buf.Next(n);
            }),
        });
    }

    /// <summary>The hero boards' strip: <see cref="StripLineCount"/> (<see cref="StripLineLyricsCount"/> under the lyrics pane) ink
    /// bars on a 3-DIP gap, centred on the strip's midline, height 7 % + 80 % of the strip, ink at alpha 0.12 + 0.33·v.</summary>
    public static Element StripLine(Slab slab, float w, float h, bool lyrics)
    {
        int n = lyrics ? StripLineLyricsCount : StripLineCount;
        float motion = FluentMotion;
        float gap = 3f, bw = MathF.Max(1.5f, (w - gap * (n - 1)) / n), mid = 0.5f * h;
        ColorF ink = Ink.Ink;
        var buf = new FluentSpriteBuffer(n);
        var bands = slab.Bands;
        return StripFrame(w, h, new SpriteFieldEl
        {
            Width = w, Height = h, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                Span<float> b = stackalloc float[Bands.Count];
                for (int i = 0; i < b.Length; i++) b[i] = bands[i].Value;
                var s = buf.Buffer;
                for (int i = 0; i < n; i++)
                {
                    float v = Geo.BarLevel(StripBand(b, i / (float)(n - 1)), motion);
                    float len = MathF.Max(bw, h * (0.07f + 0.80f * v));
                    s[i] = FluentSprite(0.5f * bw + i * (bw + gap), mid, len, bw, FluentHalfPi, 0f, ink with { A = 0.12f + 0.33f * v });
                }
                return buf.Next(n);
            }),
        });
    }

    /// <summary>The Centered board's ring: <see cref="StripRingCount"/> radial capsules about the centre of a square box of
    /// <see cref="Stage.Layout.RingBoxRatio"/> × <paramref name="cover"/>, inner radius 0.725 × cover, width 1.25 % of the cover, length
    /// 1.9 % + 12 % × v (the board's 6 + 38 of a 320 cover), the accent at alpha 0.3 + 0.5·v. The cover itself is the Hero's node.</summary>
    public static Element RingAround(Slab slab, float cover)
    {
        int n = StripRingCount;
        float motion = FluentMotion;
        float box = Stage.Layout.RingBoxRatio * cover, c0 = 0.5f * box;
        var (radius, bw, len0, gain) = Stage.Layout.RingMetrics(cover);
        var angle = new float[n]; var cos = new float[n]; var sin = new float[n]; var map = new int[n];
        for (int k = 0; k < n; k++) { angle[k] = Geo.RingAngle(k, n); cos[k] = MathF.Cos(angle[k]); sin[k] = MathF.Sin(angle[k]); map[k] = Geo.RingBand(k, n); }
        var buf = new FluentSpriteBuffer(n);
        var bands = slab.Bands; var accent = slab.Accent;
        return StripFrame(box, box, new SpriteFieldEl
        {
            Width = box, Height = box, Kernel = SpriteKernel.Capsule,
            Instances = Prop.Of(() =>
            {
                ColorF a = accent.Value;
                var s = buf.Buffer;
                for (int k = 0; k < n; k++)
                {
                    float level = bands[map[k]].Value;
                    float v = Geo.BarLevel(level, motion);
                    float len = Geo.RingLength(level, len0, gain, motion), d = radius + 0.5f * len;
                    s[k] = FluentSprite(c0 + cos[k] * d, c0 + sin[k] * d, len, bw, angle[k], 0f, a with { A = 0.3f + 0.5f * v });
                }
                return buf.Next(n);
            }),
        });
    }
}
