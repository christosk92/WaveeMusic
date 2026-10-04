// ── Shell/Visualizer.Palette.cs ────────────────────────────────────────────────────────────────────────────────────
// Visualizer.Palette — the colours every face paints with (the cover's REAL roles, per stage arm, over a contrast floor),
// its rotation for a moment, its lerp, and the ONE fade state machine the clock runs for a track change or a moment
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 240 lines
// Spec: viz-app-plan §1.1, §3.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// REAL COVER COLOURS ONLY. The previous palette's second colour was the accent's hue + 150° — a synthetic complement (the
// "salmon") that appeared on no cover. Now:
//
//   A     the accent (the stage's art-derived accent: the most saturated graded role, Design.Palette.Accent)
//   B     the next graded role at least 28° of hue away from A; with none, A moved in VALUE — a tint of a real colour
//   C     the next distinct role; else the HSV mid between A and B, moved in value until it is distinct
//   Deep  BackgroundBase, clamped deep on the dark arm and pale on the light one (the backdrop's floor tone)
//
// Every paint colour (A, B, C) clears `StrokeContrast` (3:1) against the arm's veil: on the light arm it is darkened, on
// the dark arm lifted, at constant hue — a light stage never gets a highlighter. Large soft washes may go under the floor
// but cap their alpha at `WashA`. Two colours are DISTINCT when they are ≥ 20° of hue apart OR ≥ 0.1 of value apart.
//
// Rules: pure — no signal, no entity read, no theme read (the arm is a parameter), no allocation (spans are stackalloc'd).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Visualizer
{
    /// <summary>The colours a face paints with, derived once per (cover, theme) in Stage.UI.cs (<c>StageCtx.BasePalette</c>)
    /// and rotated by the moments clock (<c>StageCtx.Palette</c>, the live one). Faces bind the slab's cross-faded
    /// <c>A/B/C/Deep</c> signals for solid fills; a value of this record is for what cannot bind (gradient stops), keyed by
    /// <c>Slab.PaletteEpoch</c>.</summary>
    public readonly record struct Palette(ColorF Accent, ColorF A, ColorF B, ColorF C, ColorF Deep, bool Dark)
    {
        /// <summary>The paint contrast floor over the veil (bars, strokes, caps).</summary>
        public const float StrokeContrast = 3f;
        /// <summary>B must sit this far in hue from A to count as a second colour.</summary>
        public const float MinHueGap = 28f;
        /// <summary>Two colours are distinct when this far apart in hue OR in value.</summary>
        public const float DistinctHue = 20f, DistinctValue = 0.1f;
        /// <summary>A large soft fill's alpha cap (blobs, nebulae, washes — exempt from the contrast floor).</summary>
        public const float WashAlphaDark = 0.75f, WashAlphaLight = 0.6f;

        public float WashA => Dark ? WashAlphaDark : WashAlphaLight;

        /// <summary>A, B, C by index (a face colouring its parts "by index" cycles the three).</summary>
        public ColorF Pick(int i)
        {
            int k = ((i % 3) + 3) % 3;
            return k == 0 ? A : k == 1 ? B : C;
        }

        /// <summary>A moment: (A, B, C) → (B, C, A). Accent and Deep stay.</summary>
        public Palette Rotated() => this with { A = B, B = C, C = A };

        public static Palette Lerp(in Palette a, in Palette b, float t)
        {
            if (!(t > 0f)) return a;                         // the ends are EXACT (a fade lands on its target, bit for bit)
            if (t >= 1f) return b;
            return new Palette(ColorF.Lerp(a.Accent, b.Accent, t), ColorF.Lerp(a.A, b.A, t), ColorF.Lerp(a.B, b.B, t),
                ColorF.Lerp(a.C, b.C, t), ColorF.Lerp(a.Deep, b.Deep, t), t < 0.5f ? a.Dark : b.Dark);
        }

        /// <summary>Derive from the stage accent and the cover's graded scheme (null/empty = no grading yet) for one arm.</summary>
        public static Palette From(ColorF accent, Scheme? scheme, bool dark)
        {
            var ground = Design.StageInk.Arm(dark ? ThemeKind.Dark : ThemeKind.Light).Veil;
            var a = Floor(accent with { A = 1f }, ground, dark);

            // the graded roles, raw (TextBrightAccent is the contrast-graded INK — pure white/black — never a colour)
            Span<ColorF> roles = stackalloc ColorF[3];
            int count = 0;
            if (scheme is { IsEmpty: false } sc)
            {
                roles[count++] = Design.Palette.ToColor(sc.BackgroundTintedBase) with { A = 1f };
                roles[count++] = Design.Palette.ToColor(sc.BackgroundBase) with { A = 1f };
                roles[count++] = Design.Palette.ToColor(sc.TextSubdued) with { A = 1f };
            }

            // B: the first chromatic role far enough round the wheel from A
            ColorF b = default;
            int bRole = -1;
            for (int i = 0; i < count; i++)
            {
                if (!Chromatic(roles[i]) || HueGap(roles[i], a) < MinHueGap) continue;
                var paint = Floor(Paint(roles[i]), ground, dark);
                if (!Distinct(paint, a)) continue;
                b = paint; bRole = i; break;
            }
            if (bRole < 0) b = Separate(ValueTint(a, dark), ground, dark, a, a);

            // C: the next chromatic role distinct from both; else the HSV mid of A and B, separated
            ColorF c = default;
            bool haveC = false;
            for (int i = 0; i < count && !haveC; i++)
            {
                if (i == bRole || !Chromatic(roles[i])) continue;
                var paint = Floor(Paint(roles[i]), ground, dark);
                if (Distinct(paint, a) && Distinct(paint, b)) { c = paint; haveC = true; }
            }
            if (!haveC) c = Separate(Mid(a, b), ground, dark, a, b);

            return new Palette(accent, a, b, c, DeepOf(scheme, a, dark), dark);
        }

        // ── the arithmetic (public for Wavee.Tests) ─────────────────────────────────────────────────────────────────

        /// <summary>Hue distance on the wheel, 0..180; 0 when either colour has no hue to speak of.</summary>
        public static float HueGap(ColorF x, ColorF y)
        {
            var (hx, sx, _) = x.ToHsv();
            var (hy, sy, _) = y.ToHsv();
            if (sx <= Design.Palette.NeutralS || sy <= Design.Palette.NeutralS) return 0f;
            float d = MathF.Abs(hx - hy) % 360f;
            return d > 180f ? 360f - d : d;
        }

        /// <summary>≥ 20° of hue apart OR ≥ 0.1 of value apart.</summary>
        public static bool Distinct(ColorF x, ColorF y)
            => HueGap(x, y) >= DistinctHue || MathF.Abs(x.ToHsv().V - y.ToHsv().V) >= DistinctValue;

        /// <summary>The colour at constant hue, value-solved until it holds <see cref="StrokeContrast"/> over
        /// <paramref name="ground"/>: lifted on the dark arm (desaturated only if full value is not enough), darkened on the
        /// light arm. A colour that already clears the floor is returned unchanged.</summary>
        public static ColorF Floor(ColorF c, ColorF ground, bool dark)
        {
            if (ColorContrast.Ratio(c, ground) >= StrokeContrast) return c;
            var (h, s, v) = c.ToHsv();
            if (dark)
            {
                if (ColorContrast.Ratio(ColorF.FromHsv(h, s, 1f), ground) < StrokeContrast)
                {
                    for (int i = 0; i < 10 && s > 0f; i++)
                    {
                        s = MathF.Max(0f, s - 0.1f);
                        if (ColorContrast.Ratio(ColorF.FromHsv(h, s, 1f), ground) >= StrokeContrast) break;
                    }
                }
                float lo = v, hi = 1f;
                for (int i = 0; i < 14; i++) { float mid = (lo + hi) * 0.5f; if (ColorContrast.Ratio(ColorF.FromHsv(h, s, mid), ground) >= StrokeContrast) hi = mid; else lo = mid; }
                return ColorF.FromHsv(h, s, hi);
            }
            else
            {
                float lo = 0f, hi = v;
                for (int i = 0; i < 14; i++) { float mid = (lo + hi) * 0.5f; if (ColorContrast.Ratio(ColorF.FromHsv(h, s, mid), ground) >= StrokeContrast) lo = mid; else hi = mid; }
                return ColorF.FromHsv(h, s, lo);
            }
        }

        static bool Chromatic(ColorF c) => c.ToHsv().S > Design.Palette.NeutralS;

        /// <summary>A raw graded role → a paint colour: brightness-lifted and saturation-floored (the chrome accent's own
        /// recipe), so a near-black background role becomes its colour rather than a smudge.</summary>
        static ColorF Paint(ColorF role) => Design.Palette.Vivid(Design.Palette.Lift(role));

        /// <summary>A moved in value: darker on the dark arm, lighter on the light arm (then floored and separated).</summary>
        static ColorF ValueTint(ColorF a, bool dark)
        {
            var (h, s, v) = a.ToHsv();
            return dark ? ColorF.FromHsv(h, s * 0.85f, v * 0.70f) : ColorF.FromHsv(h, s * 0.70f, MathF.Min(1f, v * 1.3f + 0.08f));
        }

        /// <summary>The HSV mid of two colours (hue along the shorter arc).</summary>
        static ColorF Mid(ColorF x, ColorF y)
        {
            var (hx, sx, vx) = x.ToHsv();
            var (hy, sy, vy) = y.ToHsv();
            float d = hy - hx;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return ColorF.FromHsv(hx + d * 0.5f, (sx + sy) * 0.5f, (vx + vy) * 0.5f);
        }

        /// <summary>Floor <paramref name="c"/>, then step its value away (±0.14, ±0.24, ±0.34) until it is distinct from both
        /// <paramref name="x"/> and <paramref name="y"/>; the floored colour if no step separates it (a degenerate cover).</summary>
        static ColorF Separate(ColorF c, ColorF ground, bool dark, ColorF x, ColorF y)
        {
            var floored = Floor(c, ground, dark);
            if (Distinct(floored, x) && Distinct(floored, y)) return floored;
            var (h, s, v) = c.ToHsv();
            ReadOnlySpan<float> steps = [0.14f, -0.14f, 0.24f, -0.24f, 0.34f, -0.34f, 0.46f, -0.46f];
            foreach (float step in steps)
            {
                var t = Floor(ColorF.FromHsv(h, s, Math.Clamp(v + step, 0.05f, 1f)), ground, dark);
                if (Distinct(t, x) && Distinct(t, y)) return t;
            }
            return floored;
        }

        /// <summary>The deep tone: BackgroundBase clamped deep (V ≤ 0.28) on the dark arm, pale (V ≥ 0.86, S ≤ 0.35) on the
        /// light one; without a grading, A's hue at those values.</summary>
        static ColorF DeepOf(Scheme? scheme, ColorF a, bool dark)
        {
            var (h, s, v) = scheme is { IsEmpty: false } sc ? Design.Palette.ToColor(sc.BackgroundBase).ToHsv() : a.ToHsv();
            if (scheme is not { IsEmpty: false }) { s *= dark ? 0.7f : 0.25f; v = dark ? 0.20f : 0.92f; }
            return dark ? ColorF.FromHsv(h, s, Math.Clamp(v, 0.08f, 0.28f)) : ColorF.FromHsv(h, MathF.Min(s, 0.35f), MathF.Max(v, 0.86f));
        }

        // ── the fade (the clock's ONE state machine for a track change and a moment) ────────────────────────────────

        /// <summary>A LINEAR fade from the colours CAPTURED at its start to the target over a fixed duration, landing
        /// exactly on the target at <c>start + duration</c> whatever the tick rate (an exponential step never lands —
        /// V-U17). <see cref="Moment"/> marks a moment rotation (the clock drives <c>Slab.MomentMix</c> only then; a track
        /// fade resets it to 0 at its start, so a gradient follows MOMENTS only — solid fills cross-fade on a track).</summary>
        public struct Fade
        {
            public Palette From, To;
            long _startMs;
            float _durationMs;
            public bool Active { get; private set; }
            public bool Moment { get; private set; }

            public void Begin(in Palette from, in Palette to, long nowMs, float durationMs, bool moment)
            {
                From = from; To = to; _startMs = nowMs; _durationMs = durationMs; Active = true; Moment = moment;
            }

            /// <summary>0 at the start, 1 at start + duration and after (and whenever no fade runs).</summary>
            public readonly float Progress(long nowMs)
                => !Active || _durationMs <= 0f ? 1f : Math.Clamp((nowMs - _startMs) / _durationMs, 0f, 1f);

            public readonly Palette At(long nowMs) => Lerp(From, To, Progress(nowMs));

            public void Land() { Active = false; Moment = false; }
        }
    }
}
