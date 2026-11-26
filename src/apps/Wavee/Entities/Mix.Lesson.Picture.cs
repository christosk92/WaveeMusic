// ── Entities/Mix.Lesson.Picture.cs — #48 Mix, what the lesson's editor draws: what you hear and the layer lanes ──────────────────
//
// The same functions the sound is rendered from (Mix.Lesson.Audio.cs) drawn as the editor's layer view (MixTimelineModel.Heard, .Layers):
//
//   what you hear   Night Drive and Sunrise after every layer that is on: each song's whole sound and its lows (the core), from the
//                   songs' scores (their kick, hat, bass and clap hits as a level every 20 ms, the picture the concept drew before its
//                   songs were rendered), Sunrise placed by its tempo; the echo's repeats as fading copies of Night Drive's last beat
//   Volume          the two gain curves and the fade's stretch (off: the hard cut's steps)
//   Bass            each song's bass as a bar, handed over at the swap (off: both bars over the overlap, the clash washed)
//   Filter          Night Drive's low-pass closing over the overlap, on a log scale (off: open)
//   Echo            the beat it catches and one ring per repeat, each quieter (off: a flat line)
//   Tempo           Night Drive's 124 dashed, Sunrise's tempo while both play and its glide back (off: Sunrise at 128)
//
// Each lane's handle and target are the value and Auto's value at the same place; a drag maps the pointer back through the same scale and
// snaps as the concept does (a beat, a semitone of cutoff, a whole repeat, half a BPM).

using System.Collections.Immutable;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The lesson's fixed numbers: its data hues (ARGB, painted through each theme's data-hue ink), timings and audio settings.</summary>
public static class MixLessonRules
{
    /// <summary>The songs' hues: Night Drive blue, Sunrise orange (their waveforms, their curves, their bass bars, their tempo lines).</summary>
    public const uint HueA = 0xFF8F9FE3, HueB = 0xFFE9A57A;
    /// <summary>Each layer's hue: colour means a layer and nothing else.</summary>
    public const uint HueVolume = 0xFFD0D0D0, HueBass = 0xFFE6C04F, HueFilter = 0xFFB59AF0, HueEcho = 0xFF4FC9B0, HueTempo = 0xFFEF8A7A;
    /// <summary>The hint colour: a target, its guide and the guide's "marker" words (no song, layer or accent uses it).</summary>
    public const uint HueTarget = 0xFFFF5CC8;
    /// <summary>The overlap's two basses piling up (the Bass lane while it is off).</summary>
    public const uint HueClash = 0xFFFF6E6E;

    public static uint Hue(MixLessonLayer l) => l switch
    {
        MixLessonLayer.Volume => HueVolume, MixLessonLayer.Bass => HueBass, MixLessonLayer.Filter => HueFilter, MixLessonLayer.Echo => HueEcho, _ => HueTempo,
    };

    /// <summary>Each lane's height as a share of the theme's layer lane (the concept's 54 / 40 / 60 / 42 / 52).</summary>
    public static float Share(MixLessonLayer l) => l switch
    {
        MixLessonLayer.Volume => 1.08f, MixLessonLayer.Bass => 0.8f, MixLessonLayer.Filter => 1.2f, MixLessonLayer.Echo => 0.84f, _ => 1.04f,
    };

    /// <summary>The take-apart's beat (ms): one layer off per beat, after a first pause.</summary>
    public const int PeelStepMs = 430, PeelFirstMs = 520;
    /// <summary>A reached target says "Done" this long before it goes.</summary>
    public const int DoneHoldMs = 1600;
    /// <summary>The page's ticker while something plays or moves (ms).</summary>
    public const int TickMs = 33;

    /// <summary>The comparison's switch at the playhead (ms).</summary>
    public const float CompareCrossfadeMs = 50f;
    /// <summary>Takes kept (a view's audio, re-used when the learner plays it again).</summary>
    public const int CachedTakes = 12;
    /// <summary>The gain, EQ, filter and echo tables' points.</summary>
    public const int TablePoints = 4097;
    /// <summary>Sunrise's glide back to its own tempo after the overlap, in this many steps.</summary>
    public const int GlideSteps = 16;
    public const float FilterQ = 0.9f;
    /// <summary>The echo's repeats fall to this level after the chosen number of them; each repeat is darker (its high cut).</summary>
    public const double EchoFloor = 0.05;
    public const float EchoHighCutHz = 3400f;
    public const float MasterGain = 0.85f;
    /// <summary>A take starts this long before bar 4 (the earliest any act plays from), so its filters and echo are settled.</summary>
    public const double PreRoll = 1.0;
    /// <summary>The EQ's crossover slope (dB/oct): the gentler split costs half and is plenty for a bass swap and "only the lows".</summary>
    public const int EqSlope = 24;

    /// <summary>The picture's level step (ms) and the score's own peak step (s).</summary>
    public const int LevelStepMs = 20;
}

/// <summary>The lesson's picture (see the file header). Pure functions of a view.</summary>
public static class MixLessonPicture
{
    const double Pk = MixLessonRules.LevelStepMs / 1000.0;
    static readonly double LnLo = Math.Log(MixLesson.FilterLow), LnHi = Math.Log(MixLesson.FilterHigh);

    static double Smooth(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

    /// <summary>The bass hand-over at <paramref name="t"/>: 0 (Night Drive's lows) → 1 (Sunrise's), over 120 ms around the swap.</summary>
    public static float Swap(double t, in MixLessonView v) => (float)Smooth((t - v.Values.SwapAt + 0.06) / 0.12);

    /// <summary>Night Drive's low-pass at <paramref name="t"/> (Hz): open until bar 5, closing on a log scale to the value by bar 9.</summary>
    public static double Cutoff(double t, in MixLessonView v)
    {
        if (!v.Has(MixLessonLayer.Filter) || t <= MixLesson.Ov0) return MixLesson.FilterHigh;
        double x = Math.Clamp((t - MixLesson.Ov0) / (MixLesson.Ov1 - MixLesson.Ov0), 0, 1);
        return MixLesson.FilterHigh * Math.Pow(v.Values.Cutoff / MixLesson.FilterHigh, x);
    }

    /// <summary>Sunrise's speed at <paramref name="t"/>: Night Drive's tempo while both play, gliding back to its own over two bars.</summary>
    public static double Rate(double t, in MixLessonView v)
    {
        if (!v.Has(MixLessonLayer.Tempo)) return 1;
        double r = v.Values.Bpm / MixLesson.BpmB;
        if (t <= MixLesson.Ov1) return r;
        if (t >= MixLesson.Ov1 + MixLesson.Glide) return 1;
        return r + (1 - r) * (t - MixLesson.Ov1) / MixLesson.Glide;
    }

    // ── the scale of each lane (0 = top) ─────────────────────────────────────────────────────────────────────────────

    public static float YGain(float g) => 0.12f + (1f - g) * 0.76f;
    public static float YFreq(double f) => (float)(0.1 + (1 - (Math.Log(f) - LnLo) / (LnHi - LnLo)) * 0.8);
    public static double FreqAt(float y) => Math.Exp(LnLo + Math.Clamp(1 - (y - 0.1) / 0.8, 0, 1) * (LnHi - LnLo));
    public static float YBpm(double bpm) => (float)(0.1 + (1 - (bpm - 120) / 10) * 0.8);
    public static double BpmAt(float y) => 120 + 10 * (1 - (y - 0.1) / 0.8);

    // ── the score's levels (what the concept drew before its songs were rendered) ─────────────────────────────────────

    static (float[] Af, float[] Ab, float[] Bf, float[] Bb)? s_score;

    static (float[] Af, float[] Ab, float[] Bf, float[] Bb) Score()
    {
        if (s_score is { } s) return s;
        double beatA = MixLesson.Beat, beatB = 60.0 / MixLesson.BpmB;
        float[] Arr(double len) => new float[(int)Math.Ceiling(len / Pk)];
        float[] af = Arr(8 * MixLesson.Bar + 0.6), ab = Arr(8 * MixLesson.Bar + 0.6), bf = Arr(32 * beatB + 0.6), bb = Arr(32 * beatB + 0.6);
        static void Hit(float[] arr, double t0, double amp, double dec)
        {
            for (int i = (int)Math.Floor(t0 / Pk); i < arr.Length; i++)
            {
                double dt = i * Pk - t0;
                if (dt < 0) continue;
                double v = amp * Math.Exp(-dt / dec);
                if (v < 0.01) break;
                if (v > arr[i]) arr[i] = (float)v;
            }
        }
        for (int k = 0; k < 32; k++)
        {
            double t = k * beatA;
            Hit(af, t, 1, 0.13); Hit(ab, t, 0.95, 0.13); Hit(af, t + beatA / 2, 0.55, 0.1); Hit(ab, t + beatA / 2, 0.42, 0.12);
        }
        for (int i = 0; i < af.Length; i++) af[i] = MathF.Max(af[i], 0.16f + 0.04f * MathF.Sin(i * 0.7f));
        for (int k = 0; k < 32; k++)
        {
            double t = k * beatB;
            Hit(bf, t, 0.95, 0.12); Hit(bb, t, 0.9, 0.12);
            if (k % 2 == 1) Hit(bf, t, 0.6, 0.08);
            Hit(bb, t + beatB / 2, 0.3, 0.1);
            for (int q = 0; q < 4; q++) Hit(bf, t + q * beatB / 4, 0.36, 0.05);
        }
        s_score = (af, ab, bf, bb);
        return s_score.Value;
    }

    static float Peak(float[] arr, double t0, double t1)
    {
        int i0 = Math.Max(0, (int)Math.Floor(t0 / Pk)), i1 = Math.Min(arr.Length - 1, (int)Math.Floor(t1 / Pk));
        float m = 0f;
        for (int i = i0; i <= i1; i++) if (arr[i] > m) m = arr[i];
        return i1 < i0 ? 0f : m;
    }

    static int s_version;

    /// <summary>What you hear in <paramref name="v"/>: every level from 0 s to the end of the view, in A's time.</summary>
    public static MixHeardLevels Heard(in MixLessonView v)
    {
        var (af, ab, bf, bb) = Score();
        double end = MixLesson.V1 + 0.1, dt = Pk;
        int n = (int)Math.Ceiling(end / dt);
        float[] ao = new float[n], ac = new float[n], bo = new float[n], bc = new float[n], e = new float[n];
        double fb = Math.Pow(MixLessonRules.EchoFloor, 1.0 / Math.Max(1, v.Values.Repeats));
        bool eq = v.Has(MixLessonLayer.Bass), filter = v.Has(MixLessonLayer.Filter), echo = v.Has(MixLessonLayer.Echo);
        double pos = 0, tb = MixLesson.Ov0;
        const double step = 1.0 / 200;
        for (int i = 0; i < n; i++)
        {
            double t = i * dt, t1 = t + dt;
            double hiOpen = v.LowsOnly ? 0 : filter ? Math.Pow(Math.Clamp((Math.Log(Cutoff(t, in v)) - LnLo) / (LnHi - LnLo), 0, 1), 1.6) : 1;
            if (t < MixLesson.Ov1)
            {
                float lo = Peak(ab, t, t1), hi = MathF.Max(0f, Peak(af, t, t1) - lo), g = MixLessonAudio.GainA(t, in v), keep = eq ? 1f - Swap(t, in v) : 1f;
                ao[i] = (float)((lo * keep + hi * hiOpen) * g);
                ac[i] = lo * keep * g;
            }
            if (t >= MixLesson.Ov0)
            {
                for (; tb < t; tb += step) pos += Rate(tb, in v) * Math.Min(step, t - tb);
                double p1 = pos + dt * Rate(t, in v);
                float lo = Peak(bb, pos, p1), hi = MathF.Max(0f, Peak(bf, pos, p1) - lo), g = MixLessonAudio.GainB(t, in v), keep = eq ? Swap(t, in v) : 1f;
                bo[i] = (lo * keep + (v.LowsOnly ? 0f : hi)) * g;
                bc[i] = lo * keep * g;
            }
            if (echo)
            {
                double sum = 0, amp = 1;
                for (int k = 1; k <= 12; k++)
                {
                    amp *= fb;
                    if (amp < 0.03) break;
                    double src = t - k * MixLesson.Delay;
                    if (src >= MixLesson.EchoAt && src < MixLesson.EchoAt + MixLesson.Beat * 0.5) sum += amp * Peak(af, src, src + dt) * 0.9;
                }
                e[i] = (float)Math.Min(1, sum);
            }
        }
        string noteA = v.LowsOnly ? Loc.Get(Strings.Mix.Lesson.Note.LowsOnly) : "";
        string noteB = v.Volume == MixLessonVolume.AOnly ? Loc.Get(Strings.Mix.Lesson.Note.Muted) : "";
        return new MixHeardLevels(ao, ac, bo, bc, e, MixLessonRules.LevelStepMs, MixLessonRules.HueA, MixLessonRules.HueB, MixLessonRules.HueEcho,
            noteA, noteB, ++s_version);
    }

    /// <summary>The beat grids as heard (ms in A's time; downbeats flagged): Night Drive's to bar 9, Sunrise's from bar 5 placed by its tempo.</summary>
    public static (uint[] A, bool[] DownA, uint[] B, bool[] DownB) Beats(in MixLessonView v)
    {
        var a = new List<uint>(40);
        var da = new List<bool>(40);
        for (int k = 0; k * MixLesson.Beat <= MixLesson.Ov1 + 1e-6; k++)
        {
            a.Add((uint)Math.Round(k * MixLesson.Beat * 1000));
            da.Add(k % 4 == 0);
        }
        var b = new List<uint>(40);
        var db = new List<bool>(40);
        double p = 0, next = 0, beatB = 60.0 / MixLesson.BpmB;
        int kb = 0;
        const double step = 1.0 / 200;
        for (double t = MixLesson.Ov0; t <= MixLesson.V1; t += step)
        {
            if (p >= next - 1e-9) { b.Add((uint)Math.Round(t * 1000)); db.Add(kb % 4 == 0); kb++; next += beatB; }
            p += Rate(t, in v) * step;
        }
        return (a.ToArray(), da.ToArray(), b.ToArray(), db.ToArray());
    }

    // ── the layer lanes ──────────────────────────────────────────────────────────────────────────────────────────────

    static float Ms(double t) => (float)(t * 1000);

    static float[] Sample(Func<double, float> f, double from, double to, int points)
    {
        var ys = new float[points];
        for (int i = 0; i < points; i++) ys[i] = f(from + (to - from) * i / (points - 1));
        return ys;
    }

    /// <summary>A layer's marks in <paramref name="v"/>: its picture in its hue when it is shown as on, else the quiet picture of what it would
    /// leave alone.</summary>
    public static ImmutableArray<MixLayerMark> Marks(MixLessonLayer l, in MixLessonView v)
    {
        var view = v;
        var vals = v.Values;
        bool on = v.Shows(l);
        var m = ImmutableArray.CreateBuilder<MixLayerMark>(8);
        const double v0 = MixLesson.V0, v1 = MixLesson.V1, ov0 = MixLesson.Ov0, ov1 = MixLesson.Ov1, mid = MixLesson.Mid;
        switch (l)
        {
            case MixLessonLayer.Volume:
                if (on)
                {
                    var fade = view with { Volume = MixLessonVolume.Fade };
                    double s = vals.FadeStart, e = 2 * mid - s;
                    if (e - s > 0.01) m.Add(new(MixMarkKind.Block, MixLessonRules.HueVolume, Ms(s), Ms(e), 0.06f, 0.94f, Alpha: 0.08f));
                    m.Add(new(MixMarkKind.Line, MixLessonRules.HueA, Ms(v0), Ms(ov1), 0f, Ys: Sample(t => YGain(MixLessonAudio.GainA(t, in fade)), v0, ov1, 97)));
                    m.Add(new(MixMarkKind.Line, MixLessonRules.HueB, Ms(ov0), Ms(v1), 0f, Ys: Sample(t => YGain(MixLessonAudio.GainB(t, in fade)), ov0, v1, 97)));
                }
                else
                {
                    m.Add(new(MixMarkKind.Line, 0, Ms(v0), Ms(mid), YGain(1), YGain(1)));
                    m.Add(new(MixMarkKind.Rule, 0, Ms(mid), 0, YGain(1), YGain(0), 1.5f));
                    m.Add(new(MixMarkKind.Line, 0, Ms(mid), Ms(ov1), YGain(0), YGain(0)));
                    m.Add(new(MixMarkKind.Line, 0, Ms(ov0), Ms(mid), YGain(0), YGain(0)));
                    m.Add(new(MixMarkKind.Line, 0, Ms(mid), Ms(v1), YGain(1), YGain(1)));
                }
                break;
            case MixLessonLayer.Bass:
                if (on)
                {
                    double swap = vals.SwapAt;
                    m.Add(new(MixMarkKind.Bar, MixLessonRules.HueA, Ms(v0), Ms(Math.Min(swap, ov1)), 0.36f, Size: 7f, Alpha: 0.85f));
                    m.Add(new(MixMarkKind.Bar, MixLessonRules.HueB, Ms(Math.Max(swap, ov0)), Ms(v1), 0.64f, Size: 7f, Alpha: 0.85f));
                    m.Add(new(MixMarkKind.Rule, MixLessonRules.HueBass, Ms(swap), 0, 0.08f, 0.92f, 2.5f));
                }
                else
                {
                    m.Add(new(MixMarkKind.Block, MixLessonRules.HueClash, Ms(ov0), Ms(ov1), 0.06f, 0.94f, Alpha: 0.1f));
                    m.Add(new(MixMarkKind.Bar, 0, Ms(v0), Ms(ov1), 0.36f, Size: 7f));
                    m.Add(new(MixMarkKind.Bar, 0, Ms(ov0), Ms(v1), 0.64f, Size: 7f));
                }
                break;
            case MixLessonLayer.Filter:
                if (on)
                {
                    var f = view with { On = (byte)(view.On | (1 << (int)MixLessonLayer.Filter)) };
                    var ys = Sample(t => YFreq(Cutoff(t, in f)), v0, ov1, 97);
                    m.Add(new(MixMarkKind.Area, MixLessonRules.HueFilter, Ms(v0), Ms(ov1), 0f, Ys: ys, Alpha: 0.14f));
                    m.Add(new(MixMarkKind.Line, MixLessonRules.HueFilter, Ms(v0), Ms(ov1), 0f, Ys: ys));
                }
                else m.Add(new(MixMarkKind.Line, 0, Ms(v0), Ms(ov1), YFreq(MixLesson.FilterHigh), YFreq(MixLesson.FilterHigh)));
                break;
            case MixLessonLayer.Echo:
                if (on)
                {
                    m.Add(new(MixMarkKind.Block, MixLessonRules.HueEcho, Ms(MixLesson.EchoAt), Ms(MixLesson.EchoAt + MixLesson.Beat / 2), 0.14f, 0.86f, Alpha: 0.5f));
                    double fb = Math.Pow(MixLessonRules.EchoFloor, 1.0 / Math.Max(1, vals.Repeats)), amp = 1;
                    for (int k = 1; k <= vals.Repeats; k++)
                    {
                        amp *= fb;
                        double x = MixLesson.EchoAt + k * MixLesson.Delay + MixLesson.Beat / 4;
                        m.Add(new(MixMarkKind.Ring, MixLessonRules.HueEcho, Ms(x), 0, 0.5f, Size: (float)(0.14 + 0.72 * Math.Pow(amp, 0.6)),
                            Alpha: (float)(0.3 + 0.7 * Math.Pow(amp, 0.5))));
                    }
                }
                else m.Add(new(MixMarkKind.Line, 0, Ms(v0), Ms(v1), 0.5f, 0.5f, Alpha: 0.8f));
                break;
            default:
                m.Add(new(MixMarkKind.Dash, MixLessonRules.HueA, Ms(v0), Ms(ov1), YBpm(MixLesson.BpmA), YBpm(MixLesson.BpmA), Alpha: 0.75f));
                m.Add(new(MixMarkKind.Label, 0, Ms(ov1), 0, YBpm(MixLesson.BpmA) + 0.16f, Text: "124"));
                if (on)
                {
                    var tv = view with { On = (byte)(view.On | (1 << (int)MixLessonLayer.Tempo)) };
                    m.Add(new(MixMarkKind.Line, MixLessonRules.HueB, Ms(ov0), Ms(v1), 0f, Ys: Sample(t => YBpm(MixLesson.BpmB * Rate(t, in tv)), ov0, v1, 97)));
                }
                else m.Add(new(MixMarkKind.Line, 0, Ms(ov0), Ms(v1), YBpm(MixLesson.BpmB), YBpm(MixLesson.BpmB)));
                break;
        }
        return m.ToImmutable();
    }

    /// <summary>Where a layer's handle is for <paramref name="v"/>'s values (A's ms, the lane's 0..1 height) and which way it drags.</summary>
    public static (float Ms, float Y, MixLayerAxis Axis) HandleAt(MixLessonLayer l, in MixLessonValues v) => l switch
    {
        MixLessonLayer.Volume => (Ms(v.FadeStart), 0.5f, MixLayerAxis.Time),
        MixLessonLayer.Bass => (Ms(v.SwapAt), 0.5f, MixLayerAxis.Time),
        MixLessonLayer.Filter => (Ms(MixLesson.Ov1), YFreq(v.Cutoff), MixLayerAxis.Value),
        MixLessonLayer.Echo => (Ms(MixLesson.EchoAt + v.Repeats * MixLesson.Delay + MixLesson.Beat / 4), 0.5f, MixLayerAxis.Time),
        _ => (Ms(MixLesson.Mid), YBpm(v.Bpm), MixLayerAxis.Value),
    };

    /// <summary>The values with layer <paramref name="l"/> set from a pointer at A's <paramref name="ms"/> and the lane's <paramref name="y"/>.</summary>
    public static MixLessonValues FromPoint(MixLessonLayer l, MixLessonValues v, float ms, float y)
    {
        double t = ms / 1000.0;
        switch (l)
        {
            case MixLessonLayer.Volume: return v with { FadeStart = Math.Clamp(Math.Round(t / MixLesson.Beat) * MixLesson.Beat, MixLesson.Ov0, MixLesson.Mid) };
            case MixLessonLayer.Bass: return v with { SwapAt = Math.Clamp(Math.Round(t / MixLesson.Beat) * MixLesson.Beat, MixLesson.Ov0, MixLesson.Ov1) };
            case MixLessonLayer.Filter: return v with { Cutoff = Semitone(FreqAt(y)) };
            case MixLessonLayer.Echo: return v with { Repeats = (int)Math.Clamp(Math.Round((t - MixLesson.EchoAt - MixLesson.Beat / 4) / MixLesson.Delay), 1, 10) };
            default: return v with { Bpm = Math.Clamp(Math.Round(BpmAt(y) * 2) / 2, 120, 130) };
        }
    }

    /// <summary>A key on a handle: a step (a beat, a semitone of cutoff, a repeat, half a BPM), or a big one (four beats, an octave, two BPM).</summary>
    public static MixLessonValues Stepped(MixLessonLayer l, MixLessonValues v, int dir, bool big) => l switch
    {
        MixLessonLayer.Volume => v with { FadeStart = Math.Clamp(v.FadeStart + dir * MixLesson.Beat * (big ? 4 : 1), MixLesson.Ov0, MixLesson.Mid) },
        MixLessonLayer.Bass => v with { SwapAt = Math.Clamp(v.SwapAt + dir * MixLesson.Beat * (big ? 4 : 1), MixLesson.Ov0, MixLesson.Ov1) },
        MixLessonLayer.Filter => v with { Cutoff = Semitone(v.Cutoff * Math.Pow(2, dir * (big ? 1.0 : 1.0 / 12))) },
        MixLessonLayer.Echo => v with { Repeats = Math.Clamp(v.Repeats + dir, 1, 10) },
        _ => v with { Bpm = Math.Clamp(v.Bpm + dir * (big ? 2 : 0.5), 120, 130) },
    };

    /// <summary>A cutoff snapped to the semitone grid from the filter's floor, in range.</summary>
    static double Semitone(double f)
    {
        f = Math.Clamp(f, MixLesson.FilterLow, MixLesson.FilterHigh);
        double k = Math.Round(12 * Math.Log2(f / MixLesson.FilterLow));
        return Math.Clamp(MixLesson.FilterLow * Math.Pow(2, k / 12), MixLesson.FilterLow, MixLesson.FilterHigh);
    }
}
