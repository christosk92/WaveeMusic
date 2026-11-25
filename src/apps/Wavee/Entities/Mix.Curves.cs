// ── Entities/Mix.Curves.cs — CORE (#48 Mix, plan §3.4) ────────────────────────────────────────────────────────────────
//
// One join's timing (MixTiming, the single value every layer reads) and the preset curves as data (MixCurves). A
// preset is a table of Bézier segments per lane and side, exactly as the Connect metadata spells it (capture report
// §4.3): start/end are 0..1 of the window, a 2-point segment is a line, a 4-point one a cubic Bézier P0..P3 with x 0..1
// inside the segment. The engine never sees a curve: the pump samples these into tables (MixRender) off the render
// thread. Pure and allocation-free except WriteJson's caller buffer.

using System.Globalization;

namespace Wavee;

/// <summary>The wire's preset id (recipe 2.1). Only the captured ids; an id Wavee does not know renders as Fade.</summary>
public enum MixPreset : byte { None = 0, Fade = 1, Rise = 3 }

/// <summary>Where a join's timing came from.</summary>
public enum MixSource : byte { None, Auto, Saved }

/// <summary>Why a pair gets the base hand-off instead of a mix (logged, and shown by diagnostics). <see cref="NoTransition"/>: the
/// pair's Spotify recipe is "no transition" (no preset id): A plays to its end and B from its start.</summary>
public enum MixWhyNot : byte
{
    None, NotMixed, ListenWithoutMix, NotInMixedList, PairOptOut, NotMixable, LocalFile, CrossKind, NoData, TooShort, Late,
    UserQueued, NoTransition,
}

/// <summary>One join, in milliseconds: the recipe's timing when the pair was edited, else Wavee's Auto. The Transitions
/// view's row, the prepare's start offset, the Connect metadata, the seek bar's end and the pump all read this value.</summary>
public readonly record struct MixTiming(
    MixSource Source, MixWhyNot WhyNot, byte Preset,
    uint OutStartMs,      // A: where the transition starts (audio.fade_out_start_time)
    uint OutDurationMs,   // A's curves run over this (audio.fade_out_duration = audio.fade_overlap)
    uint InStartMs,       // B: the source position it enters at (audio.fade_in_start_time)
    uint InDurationMs)    // B's curves run over this (audio.fade_in_duration); may differ from OutDurationMs
{
    public bool Mix => Source != MixSource.None;
    /// <summary>A's audible end: where the outgoing track is over and the player flips (decision D14 (b)).</summary>
    public uint AudibleEndMs => OutStartMs + OutDurationMs;
    public static MixTiming Refused(MixWhyNot why) => new(MixSource.None, why, 0, 0, 0, 0, 0);
}

/// <summary>One segment of a fade curve (report §4.3).</summary>
public readonly record struct CurveSegment(float Start, float End, bool Cubic,
    float X0, float Y0, float X1, float Y1, float X2, float Y2, float X3, float Y3)
{
    public static CurveSegment Flat(float start, float end, float y) => new(start, end, false, 0, y, 0, 0, 0, 0, 1, y);
    /// <summary>A ramp, spelled as the official client spells it (capture: the filter sweeps): a cubic <c>(0,y0)(0,y0)(1,y1)(1,y1)</c>,
    /// whose y is linear in x.</summary>
    public static CurveSegment Ramp(float start, float end, float y0, float y1) => new(start, end, true, 0, y0, 0, y0, 1, y1, 1, y1);
    /// <summary>The S the capture draws everywhere: <c>(0,y0)(.5,y0)(.5,y1)(1,y1)</c>.</summary>
    public static CurveSegment S(float start, float end, float y0, float y1) => new(start, end, true, 0, y0, .5f, y0, .5f, y1, 1, y1);
}

/// <summary>Which automated quantity a curve drives.</summary>
public enum MixLane : byte { Volume, LowEq, FilterCutoff, FilterResonance }

/// <summary>The preset → curve tables (plan §3.4, curve ids C1-C16 from the report).</summary>
public static class MixCurves
{
    /// <summary>The window's unit count: 8 under 8 s, else 16 — the rule that fits the paired values (0.998 / 0.999,
    /// 0.46875 / 0.484) of the capture (plan O10).</summary>
    public static int UnitsFor(uint windowMs) => windowMs < 8_000 ? 8 : 16;

    /// <summary>The preset's curve for one lane and side, for a window of <paramref name="units"/>. Writes at most 4
    /// segments; returns the count (0 = the lane is unused: its metadata key is omitted and the engine lane is off).</summary>
    public static int Segments(byte preset, MixLane lane, bool incoming, int units, Span<CurveSegment> into)
    {
        float swap = units >= 16 ? 1f / 64 : 1f / 32;       // the bass swap's length
        float snap = units >= 16 ? 1f / 1024 : 1f / 512;    // the overlap's fast volume S
        bool rise = preset == (byte)MixPreset.Rise;
        switch (lane)
        {
            case MixLane.Volume when !rise:                  // C1 / C3: one smooth cubic over the window
                into[0] = incoming ? new CurveSegment(0, 1, true, 0, 0, .4f, 0, .4f, 1, 1, 1)
                                   : new CurveSegment(0, 1, true, 0, 1, .6f, 1, .6f, 0, 1, 0);
                return 1;
            case MixLane.Volume:                             // C5 / C8 (C10 / C12): full overlap, a fast S at the edge
                if (incoming) { into[0] = CurveSegment.S(0, snap, 0, 1); into[1] = CurveSegment.Flat(snap, 1, 1); }
                else { into[0] = CurveSegment.Flat(0, 1 - snap, 1); into[1] = CurveSegment.S(1 - snap, 1, 1, 0); }
                return 2;
            case MixLane.LowEq when !rise:                   // C2 / C4 (C11 / C13): the bass hands over at the middle
                if (incoming)
                {
                    into[0] = CurveSegment.Flat(0, .5f - swap, 0);
                    into[1] = CurveSegment.S(.5f - swap, .5f, 0, .5f);
                    into[2] = CurveSegment.Flat(.5f, 1, .5f);
                }
                else
                {
                    into[0] = CurveSegment.Flat(0, .5f - swap, .5f);
                    into[1] = CurveSegment.S(.5f - swap, .5f, .5f, 0);
                    into[2] = CurveSegment.Flat(.5f, 1, 0);
                }
                return 3;
            case MixLane.LowEq:                              // C14 / C16: the bass swaps at the end
                if (incoming) { into[0] = CurveSegment.Flat(0, 1 - swap, 0); into[1] = CurveSegment.S(1 - swap, 1, 0, .5f); }
                else { into[0] = CurveSegment.Flat(0, 1 - swap, .5f); into[1] = CurveSegment.S(1 - swap, 1, .5f, 0); }
                return 2;
            case MixLane.FilterCutoff when rise:             // C15 out (high-pass sweep) / C9 in (low-pass opening)
                if (incoming) { into[0] = CurveSegment.Ramp(0, .5f, 0, .5f); into[1] = CurveSegment.Flat(.5f, 1, .5f); }
                else { into[0] = CurveSegment.Flat(0, .5f, .5f); into[1] = CurveSegment.Ramp(.5f, 1, .5f, 1); }
                return 2;
            case MixLane.FilterResonance when rise:          // C7: flat default
                into[0] = CurveSegment.Flat(0, 1, .5f);
                return 1;
            default:
                return 0;
        }
    }

    /// <summary>y at window position <paramref name="x"/> (0..1). A cubic segment is solved for t by bisection on x (the
    /// curves' x is monotone). Before the first segment the first value holds, after the last the last. Pure.</summary>
    public static float Evaluate(ReadOnlySpan<CurveSegment> curve, float x)
    {
        if (curve.IsEmpty) return 0f;
        if (x <= curve[0].Start) return curve[0].Y0;
        for (int i = 0; i < curve.Length; i++)
        {
            ref readonly var c = ref curve[i];
            if (x > c.End && i < curve.Length - 1) continue;
            float span = c.End - c.Start;
            float u = span <= 0 ? 1f : Math.Clamp((x - c.Start) / span, 0f, 1f);
            if (!c.Cubic) return c.Y0 + (c.Y3 - c.Y0) * u;
            float lo = 0f, hi = 1f, t = u;
            for (int k = 0; k < 24; k++)
            {
                t = (lo + hi) * 0.5f;
                if (Bez(c.X0, c.X1, c.X2, c.X3, t) < u) lo = t; else hi = t;
            }
            return Bez(c.Y0, c.Y1, c.Y2, c.Y3, t);
        }
        return curve[^1].Y3;
    }

    static float Bez(float p0, float p1, float p2, float p3, float t)
    {
        float m = 1 - t;
        return m * m * m * p0 + 3 * m * m * t * p1 + 3 * m * t * t * p2 + t * t * t * p3;
    }

    /// <summary>Sample a curve into a table spread evenly over the window (first = start, last = end).</summary>
    public static void Sample(ReadOnlySpan<CurveSegment> curve, Span<float> table)
    {
        int n = table.Length;
        for (int i = 0; i < n; i++) table[i] = Evaluate(curve, n == 1 ? 0f : i / (float)(n - 1));
    }

    /// <summary>The Connect metadata value: <c>[{"start_point":…,"end_point":…,"fade_curve":[{"x":…,"y":…},…]},…]</c>.
    /// Returns the byte count written into <paramref name="utf8"/> (size it at 160 bytes per segment).</summary>
    public static int WriteJson(ReadOnlySpan<CurveSegment> curve, Span<byte> utf8)
    {
        var sb = new System.Text.StringBuilder(160 * Math.Max(1, curve.Length));
        sb.Append('[');
        for (int i = 0; i < curve.Length; i++)
        {
            ref readonly var c = ref curve[i];
            if (i > 0) sb.Append(',');
            // The keys in the official client's order (alphabetical: end_point, fade_curve, start_point), so the strings match its own.
            sb.Append("{\"end_point\":").Append(F(c.End)).Append(",\"fade_curve\":[");
            Point(sb, c.X0, c.Y0); sb.Append(',');
            if (c.Cubic) { Point(sb, c.X1, c.Y1); sb.Append(','); Point(sb, c.X2, c.Y2); sb.Append(','); }
            Point(sb, c.X3, c.Y3);
            sb.Append("],\"start_point\":").Append(F(c.Start)).Append('}');
        }
        sb.Append(']');
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString(), utf8);

        static void Point(System.Text.StringBuilder sb, float x, float y) => sb.Append("{\"x\":").Append(F(x)).Append(",\"y\":").Append(F(y)).Append('}');
        // The official client's spelling: an exactly representable short binary fraction in full (1 − 2⁻⁹ is 0.998046875, 2⁻¹⁰ is
        // 0.0009765625), anything else at the float's shortest round-trip form (0.6, not 0.6000000238).
        static string F(float v)
        {
            double d = v;
            string exact = d.ToString("0.############", CultureInfo.InvariantCulture);
            return double.Parse(exact, CultureInfo.InvariantCulture) == d ? exact : v.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    // ── the curve → sound mapping (plan §3.4; tuned by ear later, one table) ──────────────────────────────────────────

    public const float LowShelfHz = 200f, LowShelfFloorDb = -26f;

    /// <summary>Low EQ y (0.5 flat, 0 killed) → low-shelf gain in dB, clamped to [−26, 0].</summary>
    public static float LowShelfDb(float y) => Math.Clamp((y - 0.5f) * 2f * 26f, LowShelfFloorDb, 0f);

    /// <summary>Cutoff y (0 full low-pass, 0.5 bypass, 1 full high-pass) and resonance r → mode (0 bypass, 1 LP, 2 HP),
    /// frequency and Q.</summary>
    public static (byte Mode, float Hz, float Q) Sweep(float y, float resonance)
    {
        float q = 0.707f * MathF.Pow(4f, resonance - 0.5f);
        if (MathF.Abs(y - 0.5f) < 1e-4f) return (0, 1000f, q);
        if (y < 0.5f) return (1, 120f * MathF.Pow(20000f / 120f, Math.Clamp(y / 0.5f, 0f, 1f)), q);
        return (2, 20f * MathF.Pow(6000f / 20f, Math.Clamp((y - 0.5f) / 0.5f, 0f, 1f)), q);
    }
}
