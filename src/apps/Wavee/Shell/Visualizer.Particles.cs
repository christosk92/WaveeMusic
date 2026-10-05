// ── Shell/Visualizer.Particles.cs ──────────────────────────────────────────────────────────────────────────────────
// Visualizer.Particles: the alloc-free bodies behind the moving Classics and iTunes faces — Warp (projective stars),
// Magneto (band-charged particles, two orbiting cores, nearest rays), Ambience (orbs on slow orbits), Kaleido (one
// wedge, twelve copies), Tunnel (the frame phase) — and the feedback warp/decay arithmetic of Drift and Flow
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 460 lines
// Spec: viz-app-plan.md §2.9-§2.12, §2.14, §2.18-§2.19, §6.1
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A BODY IS. Each simulation owns its arrays (allocated once in the constructor), advances on `Step(dtSec, …)` and
// writes the face's preallocated `Sprite[]` (the engine's 32-byte instance, F5) on `Write…` — returning the count it
// wrote. The UI's per-frame driver (Visualizer.Classics.UI.cs) Peeks the slab, steps, writes and publishes ONE
// `InstanceSource`; a preview or a reduced-motion stage calls `Write…` from a bound thunk without stepping (the rest
// pose, still sized by the levels). Every constant is the prototype's (stage-visualizers.html `F.warp`, `F.magneto`, …)
// and is named here ONCE.
//
// FRAME-RATE INDEPENDENT. The prototype's numbers are per millisecond or per 60 Hz frame; here every advance is exact in
// dt: linear rates integrate linearly (Tunnel, Kaleido), the Warp speed's exponential approach is integrated in closed
// form, the orbits are closed-form functions of time (Ambience), and the Magneto physics runs the prototype's 60 Hz
// step on a fixed accumulator, interpolated for drawing. The same wall time at 30, 60 or 144 Hz lands on the same
// picture (VisualizerParticlesTests pins it).
//
// LIGHT AND DARK. On the dark arm the glow is light (the face draws these sprites Additive); on the light arm the same
// sprite is a soft, darker tint of the colour drawn SrcOver — `Tints.Dark` picks, the face picks the blend.
//
// Rules: `System` plus the engine's POD value types (`Sprite`, `ColorF`, `Affine2D`); `Step`/`Write` allocate nothing;
// no LINQ, no closures, no async, no boxing. `public` because Wavee.Tests is a ProjectReference.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Visualizer
{
    public static class Particles
    {
        // ── shared ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The prototype's frame: the Magneto physics step and the unit the per-frame constants are written in.</summary>
        public const float StepSec = 1f / 60f;
        /// <summary>A tick longer than this (a stall, a resumed ticker) advances by the cap, never by the whole gap.</summary>
        public const float MaxDtSec = 0.1f;
        /// <summary>A gap longer than this is a restart (the ticker was unmounted while paused): one nominal frame.</summary>
        public const long RestartGapMs = 250;

        /// <summary>The driver's dt: the frame-time delta, clamped; the first tick and a restart get one nominal frame.</summary>
        public static float Dt(long lastMs, long nowMs)
        {
            if (lastMs == 0L || nowMs - lastMs > RestartGapMs) return StepSec;
            return Math.Clamp((nowMs - lastMs) / 1000f, 0f, MaxDtSec);
        }

        /// <summary>The prototype's <c>rnd(i, k)</c>: a deterministic 0..1 hash (double math, so it is the same everywhere).</summary>
        public static float Hash(int i, int k)
        {
            double x = Math.Sin(i * 127.1 + k * 311.7) * 43758.5453;
            return (float)(x - Math.Floor(x));
        }

        /// <summary>The fraction a per-60 Hz-frame ease <paramref name="k60"/> covers in <paramref name="dtSec"/>.</summary>
        public static float Approach(float k60, float dtSec) => 1f - MathF.Pow(1f - k60, dtSec * 60f);

        /// <summary>The three cover colours a body paints with (Palette A/B/C, cross-faded by the slab) and the arm.</summary>
        public readonly record struct Tints(ColorF A, ColorF B, ColorF C, bool Dark)
        {
            /// <summary>The prototype's <c>pal(C, i)</c>: A, B, C by index.</summary>
            public ColorF Pick(int i) => (i % 3) switch { 0 => A, 1 => B, _ => C };
            /// <summary>The halo: the colour as light on the dark arm, a darker tint at ~70 % alpha on the light arm.</summary>
            public ColorF Glow(in ColorF c, float alpha) => Dark ? c with { A = alpha } : Mix(c, 0f, 0.25f) with { A = alpha * 0.7f };
            /// <summary>The hot centre: lightened toward white on the dark arm (never white itself), the colour itself on light.</summary>
            public ColorF Hot(in ColorF c, float lighten, float alpha) => Dark ? Mix(c, 1f, lighten) with { A = alpha } : c with { A = alpha * 0.85f };
            /// <summary>A plain stroke colour: the colour on dark, darkened a little and at 80 % alpha on light.</summary>
            public ColorF Line(in ColorF c, float alpha) => Dark ? c with { A = alpha } : Mix(c, 0f, 0.2f) with { A = alpha * 0.8f };
            static ColorF Mix(in ColorF c, float to, float k) => new(c.R + (to - c.R) * k, c.G + (to - c.G) * k, c.B + (to - c.B) * k, c.A);
        }

        /// <summary>Write one sprite at <paramref name="n"/> and advance it; a full buffer drops the sprite (never throws).</summary>
        public static void Put(Sprite[] dst, ref int n, float x, float y, float w, float h, float rot, float soft, in ColorF c)
        {
            if ((uint)n >= (uint)dst.Length) return;
            ref var s = ref dst[n++];
            s.X = x; s.Y = y; s.W = w; s.H = h; s.Rot = rot; s.Soft = soft; s.Rgba = Sprite.Pack(c);
        }

        static float Band(ReadOnlySpan<float> bands, int i) => bands.IsEmpty ? 0f : bands[Math.Clamp(i, 0, bands.Length - 1)];

        // ── Warp: AVS's starfield — streaks whose length is the speed, the speed is the bass + the kick ────────────────

        public sealed class Warp
        {
            public const int Count = 220, WeakCount = 120, PreviewCount = 70;
            /// <summary>Speed target in depth per ms: <c>0.00018 + 0.0011·Low + 0.0009·Kick</c>, eased 0.12 per 60 Hz frame.</summary>
            public const float BaseSpeed = 0.00018f, LowGain = 0.0011f, KickGain = 0.0009f, Ease60 = 0.12f;
            public const float NearZ = 0.02f, MaxLen = 140f, LenGain = 9000f, Width = 2.6f;
            /// <summary>The prototype's face is ≈ 1500 DIP wide; lengths and widths scale with the box.</summary>
            public const float RefSize = 1500f;
            static readonly float EaseRate = -MathF.Log(1f - Ease60) * 60f;   // per second

            public readonly float[] X, Y, Z;
            readonly int[] _gen;
            public readonly int N;
            /// <summary>Depth per millisecond.</summary>
            public float Speed;

            public Warp(int n)
            {
                N = n; X = new float[n]; Y = new float[n]; Z = new float[n]; _gen = new int[n];
                for (int i = 0; i < n; i++) { X[i] = Hash(i, 1) * 2f - 1f; Y[i] = Hash(i, 2) * 2f - 1f; Z[i] = NearZ + Hash(i, 3) * (1f - NearZ); }
            }

            public static float Target(float low, float kick) => BaseSpeed + LowGain * low + KickGain * kick;

            /// <summary>Ease the speed toward the target (closed form) and fly every star that far; a star past the eye
            /// wraps to the back by the SAME distance (z += 0.98), so its phase — and the picture — is rate-independent.</summary>
            public void Step(float dtSec, float low, float kick)
            {
                if (dtSec <= 0f) return;
                float target = Target(low, kick), e = MathF.Exp(-EaseRate * dtSec);
                // ∫ speed dt for an exponential approach: target·dt + (s0 − target)(1 − e^(−k·dt))/k
                float travel = (target * dtSec + (Speed - target) * (1f - e) / EaseRate) * 1000f;
                Speed = target + (Speed - target) * e;
                for (int i = 0; i < N; i++)
                {
                    float z = Z[i] - travel;
                    while (z <= NearZ)
                    {
                        z += 1f - NearZ;
                        int g = ++_gen[i];
                        X[i] = Hash(i, 1000 + g) * 2f - 1f; Y[i] = Hash(i + 99, 1000 + g) * 2f - 1f;
                    }
                    Z[i] = z;
                }
            }

            /// <summary>The rest pose (preview, reduced motion): nothing flies, the streak length follows the level.</summary>
            public void Settle(float low, float kick) => Speed = Target(low, kick);

            /// <summary>One Streak per visible star (head outward, the alpha ramp toward the centre); off-box stars are skipped.</summary>
            public int Write(Sprite[] dst, int count, float w, float h, float cx, float cy, in Tints t, float lenK)
            {
                int n = 0;
                float sc = MathF.Max(w, h) * 0.3f, k = MathF.Max(w, h) / RefSize;
                for (int i = 0; i < Math.Min(count, N); i++)
                {
                    float z = Z[i], px = cx + X[i] / z * sc, py = cy + Y[i] / z * sc;
                    if (px < 0f || px > w || py < 0f || py > h) continue;
                    float len = (MathF.Min(MaxLen, Speed * LenGain / z) * lenK + 2f) * k, wd = MathF.Max(0.8f, Width * (1.2f - z) * k);
                    float dx = px - cx, dy = py - cy, d = MathF.Sqrt(dx * dx + dy * dy), a = MathF.Atan2(dy, dx);
                    float ux = d > 0f ? dx / d : 1f, uy = d > 0f ? dy / d : 0f;
                    var c = t.Line(t.Pick(i), MathF.Min(1f, 1.3f - z));
                    Put(dst, ref n, px - ux * len * 0.5f, py - uy * len * 0.5f, len, wd, a, 1f, in c);
                }
                return n;
            }
        }

        // ── Magneto: Magnetosphere — each particle's band is its CHARGE, loud repels from two orbiting cores ─────────

        public sealed class Magneto
        {
            public const int Count = 72, WeakCount = 48, PreviewCount = 12, BandSpan = 46;
            public const float Repel = 0.00006f, Pull = 0.0009f, Swirl = 0.0004f, Damping = 0.94f, Soft2 = 0.002f, Threshold = 0.35f;
            public const float BoundX = 0.6f, BoundY = 0.45f, Spread = 1.6f, RayReach = 0.45f;
            const int MaxSteps = 8;

            public readonly float[] X, Y, VX, VY;
            readonly float[] _px, _py;
            readonly int[] _band;
            public readonly int N;
            double _acc, _stepT;   // the accumulator and the physics clock (seconds)

            public Magneto(int n)
            {
                N = n; X = new float[n]; Y = new float[n]; VX = new float[n]; VY = new float[n]; _px = new float[n]; _py = new float[n]; _band = new int[n];
                for (int i = 0; i < n; i++)
                {
                    X[i] = _px[i] = (Hash(i, 51) - 0.5f) * 0.8f;
                    Y[i] = _py[i] = (Hash(i, 52) - 0.5f) * 0.6f;
                    _band[i] = i * BandSpan / n;
                }
            }

            /// <summary>Fraction of the next step already elapsed — the drawing interpolates prev → current by it.</summary>
            public float Alpha => (float)(_acc / StepSec);
            /// <summary>The time the drawn picture shows (one step behind the physics, like the interpolated bodies); 0 until
            /// the first step has run, never negative.</summary>
            public float Time => (float)Math.Max(0.0, _stepT + _acc - StepSec);
            public int BandOf(int i) => _band[i];

            /// <summary>Core <paramref name="k"/> (0/1) in normalised face units at time <paramref name="t"/> seconds.</summary>
            public static (float X, float Y) Core(int k, float t)
            {
                float ph = k == 0 ? 0f : MathF.PI;
                return (MathF.Cos(t * 0.4f + ph) * 0.16f, MathF.Sin(t * 0.5f + ph) * 0.1f);
            }

            public void Step(float dtSec, ReadOnlySpan<float> bands, float mid)
            {
                if (dtSec <= 0f) return;
                _acc += dtSec;
                int steps = 0;
                while (_acc >= StepSec && steps < MaxSteps) { _acc -= StepSec; steps++; Advance(bands, mid); }
                if (_acc >= StepSec) _acc = StepSec * 0.999;   // past the cap: drop the backlog, never spiral
            }

            void Advance(ReadOnlySpan<float> bands, float mid)
            {
                float t = (float)_stepT;
                var (k0x, k0y) = Core(0, t); var (k1x, k1y) = Core(1, t);
                float swirl = Swirl * (0.5f + mid);
                for (int i = 0; i < N; i++)
                {
                    float x = X[i], y = Y[i], vx = VX[i], vy = VY[i];
                    _px[i] = x; _py[i] = y;
                    float charge = Band(bands, _band[i]) - Threshold;   // loud ⇒ positive ⇒ pushed out
                    Pole(x, y, k0x, k0y, charge, ref vx, ref vy);
                    Pole(x, y, k1x, k1y, charge, ref vx, ref vy);
                    vx += -y * swirl; vy += x * swirl;                   // a slow swirl from the mids
                    vx *= Damping; vy *= Damping;
                    X[i] = Math.Clamp(x + vx, -BoundX, BoundX); Y[i] = Math.Clamp(y + vy, -BoundY, BoundY);
                    VX[i] = vx; VY[i] = vy;
                }
                _stepT += StepSec;
            }

            static void Pole(float x, float y, float kx, float ky, float charge, ref float vx, ref float vy)
            {
                float dx = x - kx, dy = y - ky, f = charge * Repel / (dx * dx + dy * dy + Soft2);
                vx += dx * f - dx * Pull; vy += dy * f - dy * Pull;
            }

            float DrawX(int i, float a) => _px[i] + (X[i] - _px[i]) * a;
            float DrawY(int i, float a) => _py[i] + (Y[i] - _py[i]) * a;

            /// <summary>Discs: per particle a soft halo (radius 3R) + a hot centre (R), R = m·(0.006 + 0.022·e); then the two
            /// cores (halo 2.5R + centre), R = 0.045·m·(1 + 0.25·kick). <paramref name="count"/> particles at most.</summary>
            public int WriteBodies(Sprite[] dst, int count, float cx, float cy, float m, ReadOnlySpan<float> bands, float kick, in Tints t, float sizeK)
            {
                int n = 0;
                float a = Alpha, s = m * Spread;
                for (int i = 0; i < Math.Min(count, N); i++)
                {
                    float px = cx + DrawX(i, a) * s, py = cy + DrawY(i, a) * s, e = Band(bands, _band[i]);
                    float r = m * (0.006f + e * 0.022f) * sizeK;
                    var col = t.Pick(i);
                    var halo = t.Glow(col, 0.6f); var hot = t.Hot(col, 0.5f, 0.95f);
                    Put(dst, ref n, px, py, r * 3f, r * 3f, 0f, 1f, in halo);
                    Put(dst, ref n, px, py, r, r, 0f, 0.5f, in hot);
                }
                float time = Time, rc = m * 0.045f * sizeK * (1f + kick * 0.25f);
                var coreHalo = t.Glow(t.A, 0.8f); var coreHot = t.Hot(t.A, 0.6f, 0.95f);
                for (int k = 0; k < 2; k++)
                {
                    var (kx, ky) = Core(k, time);
                    float x = cx + kx * s, y = cy + ky * s;
                    Put(dst, ref n, x, y, rc * 2.5f, rc * 2.5f, 0f, 1f, in coreHalo);
                    Put(dst, ref n, x, y, rc * 0.8f, rc * 0.8f, 0f, 0.6f, in coreHot);
                }
                return n;
            }

            /// <summary>Segments from each core to every other particle within 0.45·m, alpha (1 − d/reach)·0.35·(0.4 + e).</summary>
            public int WriteRays(Sprite[] dst, float cx, float cy, float m, ReadOnlySpan<float> bands, in Tints t, float lineW)
            {
                int n = 0;
                float a = Alpha, s = m * Spread, reach = m * RayReach, time = Time;
                for (int k = 0; k < 2; k++)
                {
                    var (kx, ky) = Core(k, time);
                    float x0 = cx + kx * s, y0 = cy + ky * s;
                    for (int i = 0; i < N; i += 2)
                    {
                        float px = cx + DrawX(i, a) * s, py = cy + DrawY(i, a) * s, dx = px - x0, dy = py - y0;
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d > reach) continue;
                        var c = t.Line(t.Pick(i), (1f - d / reach) * 0.35f * (0.4f + Band(bands, _band[i])));
                        Put(dst, ref n, x0, y0, px, py, lineW, 0f, in c);
                    }
                }
                return n;
            }
        }

        // ── Ambience: WMP's glowing orbs on slow orbits, each owning a band ──────────────────────────────────────────

        public sealed class Ambience
        {
            public const int Count = 34, WeakCount = 20, PreviewCount = 12, BandSpan = 44;

            readonly float[] _angle, _radius, _speed, _wobble;
            readonly int[] _band;
            public readonly int N;
            /// <summary>Orbit time in ms — every position is a closed-form function of it.</summary>
            public double TimeMs;

            public Ambience(int n)
            {
                N = n; _angle = new float[n]; _radius = new float[n]; _speed = new float[n]; _wobble = new float[n]; _band = new int[n];
                for (int i = 0; i < n; i++)
                {
                    _angle[i] = Hash(i, 7) * MathF.Tau; _radius[i] = 0.12f + Hash(i, 8) * 0.34f; _speed[i] = (Hash(i, 9) - 0.5f) * 0.0006f;
                    _band[i] = (int)(Hash(i, 10) * BandSpan); _wobble[i] = Hash(i, 11) * 6f;
                }
            }

            public void Step(float dtSec) { if (dtSec > 0f) TimeMs += dtSec * 1000.0; }

            /// <summary>Orb <paramref name="i"/>'s centre offset from the face centre (DIP) for a face of min side <paramref name="m"/>.</summary>
            public (float X, float Y) Offset(int i, float m)
            {
                double ang = _angle[i] + TimeMs * _speed[i];
                float wob = (float)Math.Sin(TimeMs * 0.0007 + _wobble[i]) * 0.04f, r = m * (_radius[i] + wob);
                return ((float)Math.Cos(ang) * r * 1.3f, (float)Math.Sin(ang * 1.3) * r * 0.8f);
            }

            /// <summary>Per orb a soft halo (radius 2.4R) + a hot centre; R = m·(0.018 + 0.07·e)·sizeK.</summary>
            public int Write(Sprite[] dst, int count, float cx, float cy, float m, ReadOnlySpan<float> bands, in Tints t, float sizeK)
            {
                int n = 0;
                for (int i = 0; i < Math.Min(count, N); i++)
                {
                    var (ox, oy) = Offset(i, m);
                    float r = m * (0.018f + Band(bands, _band[i]) * 0.07f) * sizeK;
                    var col = t.Pick(i);
                    var halo = t.Glow(col, 0.7f); var hot = t.Hot(col, 0.45f, 0.95f);
                    Put(dst, ref n, cx + ox, cy + oy, r * 2.4f, r * 2.4f, 0f, 1f, in halo);
                    Put(dst, ref n, cx + ox, cy + oy, r * 0.6f, r * 0.6f, 0f, 0.6f, in hot);
                }
                return n;
            }
        }

        // ── Kaleido: WMP Alchemy — one wedge of capsules on the bands, rotated and mirrored ─────────────────────────

        public sealed class Kaleido
        {
            public const int Parts = 8, Copies = 12, PreviewParts = 4, PreviewCopies = 6, BandStep = 5;
            /// <summary>Spin per ms: <c>0.00008 + 0.0003·Mid</c>.</summary>
            public const float BaseSpin = 0.00008f, MidSpin = 0.0003f;

            public float Rot;
            public double TimeMs;

            public void Step(float dtSec, float mid)
            {
                if (dtSec <= 0f) return;
                float ms = dtSec * 1000f;
                Rot = (Rot + ms * (BaseSpin + MidSpin * mid)) % MathF.Tau;
                TimeMs += ms;
            }

            /// <summary><paramref name="copies"/> (even: rotations × mirror) of a <paramref name="parts"/>-capsule wedge.</summary>
            public int Write(Sprite[] dst, float cx, float cy, float m, ReadOnlySpan<float> bands, in Tints t, int parts, int copies)
            {
                int n = 0, rotations = Math.Max(1, copies / 2), bandStep = BandStep * Parts / Math.Max(1, parts);
                float step = MathF.Tau / rotations;
                for (int sym = 0; sym < copies; sym++)
                {
                    float baseA = sym / 2 * step + Rot, flip = (sym & 1) == 1 ? -1f : 1f;
                    for (int p = 0; p < parts; p++)
                    {
                        float e = Band(bands, p * bandStep), r = m * (0.06f + p * 0.045f * Parts / parts);
                        float a = baseA + flip * (0.12f + p * 0.05f + (float)Math.Sin(TimeMs * 0.0004 + p) * 0.08f);
                        float len = m * (0.03f + e * 0.12f), wd = m * (0.012f + e * 0.01f);
                        var c = t.Line(t.Pick(p), 0.45f + e * 0.55f);
                        Put(dst, ref n, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r, len, wd, a + flip * (0.6f + e), 0f, in c);
                    }
                }
                return n;
            }
        }

        // ── Tunnel: the frame phase — rounded frames fly outward, the kick pushes them ───────────────────────────────

        public sealed class Tunnel
        {
            public const int Frames = 14, PreviewFrames = 8;
            /// <summary>Frames per ms: <c>0.00022 + 0.0007·Low + 0.0006·Kick</c>; the spin is 0.00005 rad per ms.</summary>
            public const float BaseRate = 0.00022f, LowRate = 0.0007f, KickRate = 0.0006f, SpinRate = 0.00005f, Twist = 0.9f;

            public float Phase, Spin;

            public void Step(float dtSec, float low, float kick)
            {
                if (dtSec <= 0f) return;
                float ms = dtSec * 1000f;
                Phase = (Phase + ms * (BaseRate + LowRate * low + KickRate * kick)) % Frames;
                Spin = (Spin + ms * SpinRate) % MathF.Tau;
            }

            /// <summary>Frame <paramref name="k"/>'s depth 0 (far, tiny) … 1 (near, past the edges) of <paramref name="n"/>.</summary>
            public static float Depth(int k, float phase, int n) { float d = (k + phase) % n / n; return d < 0f ? d + 1f : d; }
            /// <summary>The frame's side as a fraction of its full side (1.6·min): <c>d^2.2</c>.</summary>
            public static float Size(float d) => MathF.Pow(d, 2.2f);
            /// <summary>Fades in from the far end and thins as it passes: <c>min(1, 1.4·d)·(1 − 0.55·d)</c>.</summary>
            public static float Alpha(float d) => MathF.Min(1f, d * 1.4f) * (1f - d * 0.55f);
            public static float Turn(float d, float spin) => spin + Twist * d;
        }

        // ── Feedback: the per-advance warp and decay of Drift (MilkDrop) and Flow (G-Force) ─────────────────────────

        public static class Feedback
        {
            public const float DriftDecay = 0.06f, FlowDecay = 0.05f;

            /// <summary>dt in 60 Hz frames (dt / 16.7 ms), capped: the engine applies the warp and the decay per ADVANCE.</summary>
            public static float Advances(float dtSec) => Math.Clamp(dtSec * 60f, 0f, 6f);

            /// <summary>Drift's ring: sample <paramref name="k"/> of <paramref name="last"/> + 1 reads band 0 → 40 → 0 around the
            /// loop (mirrored), so the closed ring has no seam: the first and last samples read the same band.</summary>
            public static int RingBand(int k, int last) => (int)MathF.Round((1f - MathF.Abs(1f - 2f * k / last)) * 40f);

            /// <summary>A per-60 Hz-frame decay as the decay of one advance <paramref name="dtSec"/> long.</summary>
            public static float Decay(float decay60, float dtSec) => 1f - MathF.Pow(1f - decay60, Advances(dtSec));

            /// <summary>Rotate by <paramref name="angle"/> after scaling (sx, sy), about the pivot (ox, oy) given as an offset
            /// from the box centre: the pivot maps to itself.</summary>
            public static Affine2D About(float angle, float sx, float sy, float ox, float oy)
            {
                float c = MathF.Cos(angle), s = MathF.Sin(angle);
                float m11 = c * sx, m12 = s * sx, m21 = -s * sy, m22 = c * sy;
                return new Affine2D(m11, m12, m21, m22, ox - (m11 * ox + m21 * oy), oy - (m12 * ox + m22 * oy));
            }

            /// <summary>Drift: turn <c>0.006 + 0.01·Mid</c> and zoom <c>1.018 + 0.02·Kick</c> per 60 Hz frame, about the ring centre.</summary>
            public static Affine2D Drift(float mid, float kick, float dtSec, float ox, float oy)
            {
                float a = Advances(dtSec), z = MathF.Pow(1.018f + 0.02f * kick, a);
                return About((0.006f + 0.01f * mid) * a, z, z, ox, oy);
            }

            /// <summary>Flow: turn <c>sin(0.2t)·0.02 + 0.012·Mid</c> and zoom (1.012, 1.006) + 0.02·Low per 60 Hz frame, about
            /// a centre drifting <c>(cos 0.3t · 0.12w, sin 0.23t · 0.1h)</c> from the box centre.</summary>
            public static Affine2D Flow(float tSec, float mid, float low, float dtSec, float w, float h)
            {
                float a = Advances(dtSec);
                float ang = (MathF.Sin(tSec * 0.2f) * 0.02f + mid * 0.012f) * a;
                return About(ang, MathF.Pow(1.012f + low * 0.02f, a), MathF.Pow(1.006f + low * 0.02f, a),
                             MathF.Cos(tSec * 0.3f) * w * 0.12f, MathF.Sin(tSec * 0.23f) * h * 0.1f);
            }
        }
    }
}
