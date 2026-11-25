// ── Entities/Mix.Curves.cs — CORE (#48 Mix, plan §3.4; full-support plan §2.3) ─────────────────────────────────────────
//
// One join's plan (MixTiming, the single value every layer reads: its timing, the styles it plays and the bars they are
// laid out for) and the curve primitives: a curve is a list of Bézier segments per lane and side, exactly as the Connect
// metadata spells it (capture report §4.3): start/end are 0..1 of the window, a 2-point segment is a line, a 4-point one
// a cubic Bézier P0..P3 with x 0..1 inside the segment. The curves themselves come from the style table (Mix.Styles.cs).
// The engine never sees a curve: the pump samples them into tables (MixRender) off the render thread. Pure and
// allocation-free except WriteJson's caller buffer.

using System.Globalization;

namespace Wavee;

/// <summary>Where a join's timing came from.</summary>
public enum MixSource : byte { None, Auto, Saved }

/// <summary>What the planner chose beyond the recipe or the rule, for the logs and diagnostics.</summary>
[Flags]
public enum MixPlanNotes : byte
{
    None = 0,
    /// <summary>A's last end candidate (kind 28 field 4) is missing (not analysed yet, a 502): the overlap is the rule's
    /// answer for a candidate 10 ms before A's end, 2510 ms.</summary>
    OutFallback = 1 << 0,
    /// <summary>B's first start candidate (kind 28 field 3) is missing: B's fade-in is the rule's answer for a candidate at
    /// 10 ms, 2510 ms.</summary>
    InFallback = 1 << 1,
    // 1 << 2 was BeatmatchNotApplied (a saved beatmatched recipe played B unstretched until the engine had a per-voice rate, M4).
    /// <summary>A window was cut to the longest transition the client allows (35 s).</summary>
    Capped = 1 << 3,
    /// <summary>B's window was cut at B's end.</summary>
    InClipped = 1 << 4,
    /// <summary>The recipe carries curve overrides (Preset 2.6 to 2.14): the table's curves play (the editor phase renders them).</summary>
    CurveOverridesNotApplied = 1 << 5,
}

/// <summary>Why a pair gets the base hand-off instead of a mix (logged, and shown by diagnostics). <see cref="NoTransition"/>: the
/// pair's Spotify recipe is "no transition" (no preset id): A plays to its end and B from its start.</summary>
public enum MixWhyNot : byte
{
    None, NotMixed, ListenWithoutMix, NotInMixedList, PairOptOut, NotMixable, LocalFile, CrossKind, TooShort, Late,
    UserQueued, NoTransition,
}

/// <summary>One join's plan: the recipe's timing when the pair was edited, else Spotify's Auto, and the styles it plays. The
/// Transitions view's row, the prepare's start offset, the Connect metadata, the seek bar's end and the pump all read this
/// value. A's and B's curves start at the same instant and run over their own lengths (<see cref="OutDurationMs"/>,
/// <see cref="InDurationMs"/>). Every lane the styles move is resolved here, including the ones the engine cannot render yet
/// (<see cref="Playback.MixRender"/> renders every lane the engine has; only a looping or jog-wheel style the table does not know is
/// skipped).</summary>
public readonly record struct MixTiming(
    MixSource Source, MixWhyNot WhyNot,
    byte Preset,          // recipe 2.1 as saved (1 for Auto); an id the table does not know plays as Fade (Styles says what plays)
    uint OutStartMs,      // A: where the transition starts (audio.fade_out_start_time)
    uint OutDurationMs,   // A's curves run over this (audio.fade_out_duration = audio.fade_overlap)
    uint InStartMs,       // B: the source position it enters at (audio.fade_in_start_time)
    uint InDurationMs,    // B's curves run over this, in B's MEDIA time (audio.fade_in_duration); may differ from OutDurationMs. Beatmatched:
                          // OutDurationMs × SpeedB (B covers that much of itself in the overlap); the pump maps it to mixer frames by B's rate plan
    MixStyleSet Styles,   // the preset's styles with the recipe's overrides in place (MixStyles.Resolve)
    byte Bars,            // recipe 1.8 durationBars, the curves' layout (0 = not beatmatched: the 2-bar layout, MixStyles.UnitBars)
    float SpeedB,         // recipe 1.7, B's beatmatch speed (1 for a pair that is not beatmatched): B plays at it from its first frame and
                          // glides back to 1 after its fade-in (protocol §6.2; MixRender.RatePlan)
    float BpmA,           // A's tempo (recipe 1.16, else kind 222): the effects' echo and delay time (MixStyles.FxBeats)
    MixPlanNotes Notes)
{
    public bool Mix => Source != MixSource.None;
    /// <summary>A's audible end: where the outgoing track is over and the player flips (decision D14 (b)).</summary>
    public uint AudibleEndMs => OutStartMs + OutDurationMs;
    public static MixTiming Refused(MixWhyNot why) => new(MixSource.None, why, 0, 0, 0, 0, 0, default, 0, 1f, 0f, MixPlanNotes.None);

    /// <summary>A plan for preset <paramref name="presetId"/> with <paramref name="overrides"/> resolved through the style table.</summary>
    public static MixTiming Of(MixSource source, int presetId, in MixStyleOverrides overrides, int bars, uint outStartMs, uint outDurationMs,
        uint inStartMs, uint inDurationMs, float speedB = 1f, float bpmA = 0f, MixPlanNotes notes = MixPlanNotes.None)
        => new(source, MixWhyNot.None, (byte)Math.Clamp(presetId, 0, 255), outStartMs, outDurationMs, inStartMs, inDurationMs,
               MixStyles.Resolve(presetId, in overrides), (byte)Math.Clamp(bars, 0, 255), speedB, bpmA, notes);
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

/// <summary>The curve primitives: evaluate, sample into an engine table, spell as the Connect metadata, and the official client's
/// value map. The curves come from the style table (<see cref="MixStyles"/>).</summary>
public static class MixCurves
{
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

    // ── the curve → sound mapping: the official client's own evaluator (its value-semantics table, plan UPDATE 11-25) ──────
    //
    // A curve value is clamped to 0..1; an exact special case wins; otherwise the piecewise "signed geometric" segment:
    // out = start × (end / start)^t over the segment's t. EQ gain: 0 = Kill, (0, 0.5) −48 → −1.5 dB, 0.5 = 0 dB, (0.5, 1]
    // +0.638943 → +6 dB. Filter cutoff: below 0.5 a low-pass 120 → 16000 Hz, 0.5 bypass, above it a high-pass 20 → 9000 Hz.
    // Resonance has no published semantics, so its Q map is Wavee's. The engine's automation lanes take dB, Hz and Q.

    /// <summary>The client's "Kill" (raw 0, which it shows as −∞ dB): the band is removed. The engine's 3-band EQ (E5, a
    /// Linkwitz-Riley split, not a shelf) takes −∞ as a kill (EqLaneSpec.KillDb or below), so the curve's 0 is a real band cut; what
    /// remains at the band's frequencies is the neighbouring bands' crossover skirt.</summary>
    public const float EqKillDb = float.NegativeInfinity;

    const float EqCutFromDb = -48f, EqCutToDb = -1.5f, EqBoostFromDb = 0.638943076133728f, EqBoostToDb = 6f;
    const float LowPassFromHz = 120f, LowPassToHz = 16_000f, HighPassFromHz = 20f, HighPassToHz = 9_000f;
    /// <summary>The client's special case is raw == 0.5 exactly; this absorbs only the float rounding of the Bézier solver.</summary>
    const float Centre = 1e-6f;

    /// <summary>An EQ curve value (0..1) → band gain in dB, as the official client evaluates it: 0 kills (<see cref="EqKillDb"/>),
    /// 0.5 is 0 dB, below it −48 × (1.5/48)^(raw/0.5) (0.25 → −8.49 dB), above it 0.638943 × (6/0.638943)^((raw−0.5)/0.5)
    /// (1 → +6 dB).</summary>
    public static float EqGainDb(float raw)
    {
        raw = Math.Clamp(raw, 0f, 1f);
        if (raw <= 0f) return EqKillDb;
        if (MathF.Abs(raw - 0.5f) < Centre) return 0f;
        return raw < 0.5f ? EqCutFromDb * MathF.Pow(EqCutToDb / EqCutFromDb, raw / 0.5f)
                          : EqBoostFromDb * MathF.Pow(EqBoostToDb / EqBoostFromDb, (raw - 0.5f) / 0.5f);
    }

    /// <summary>A cutoff curve value y (0..1) and a resonance value r → mode (0 bypass, 1 LP, 2 HP), frequency and Q. The cutoff as
    /// the official client evaluates it: below 0.5 a low-pass at 120 × (16000/120)^(y/0.5) Hz (0.25 → 1386 Hz), 0.5 bypass, above
    /// it a high-pass at 20 × (9000/20)^((y−0.5)/0.5) Hz (0.75 → 424 Hz). Q = 0.707 × 4^(r−0.5) (Wavee's; the client publishes none).</summary>
    public static (byte Mode, float Hz, float Q) Sweep(float y, float resonance)
    {
        float q = 0.707f * MathF.Pow(4f, resonance - 0.5f);
        y = Math.Clamp(y, 0f, 1f);
        if (MathF.Abs(y - 0.5f) < Centre) return (0, 1000f, q);
        if (y < 0.5f) return (1, LowPassFromHz * MathF.Pow(LowPassToHz / LowPassFromHz, y / 0.5f), q);
        return (2, HighPassFromHz * MathF.Pow(HighPassToHz / HighPassFromHz, (y - 0.5f) / 0.5f), q);
    }

    // ── noise (filter style NoiseOutEnd; the client's kind 5 entry) and the effects' dry/wet (kind 4) ──────────────────────

    const double NoiseLowFromHz = 100.48416305247163, NoiseLowToHz = 29436.773229308717;
    const double NoiseHighFromHz = 37.33756354910836, NoiseHighToHz = 15214.43784823654;
    const double NoiseEase = 1.7799999713897705;
    const float NoiseOffBelow = 0.4950000001117587f, NoiseOffAbove = 0.5049999998882413f;

    /// <summary>The noise level while the colour curve is outside its "off" band. The client's value table has no level entry
    /// (kind 5 is the colour only; its one special case, 0.495 &lt; raw &lt; 0.505, is "Off"), so the level is Wavee's choice:
    /// −20 dBFS RMS of the white noise before the colour filter, about 6 to 10 dB under a mastered track, the level the engine
    /// bench measures the lane at. The voice's own volume curve fades it with the voice (the lane runs before the envelope).</summary>
    public const float NoiseLevelDb = -20f;

    /// <summary>True when a noise colour value is the client's "Off" (strictly inside 0.495..0.505): no noise.</summary>
    public static bool NoiseOff(float raw)
    {
        raw = Math.Clamp(raw, 0f, 1f);
        return raw > NoiseOffBelow && raw < NoiseOffAbove;
    }

    /// <summary>A noise colour value (0..1) → the colour filter over the white noise (mode 1 low-pass, 2 high-pass), by the client's
    /// kind 5 entry: below 0.5 <c>100.48 × (29436.8/100.48)^((raw/0.5)^1.78)</c> Hz (powerIn), from 0.5 up
    /// <c>37.34 × (15214.4/37.34)^(1 − (1 − (raw−0.5)/0.5)^1.78)</c> Hz (powerOut). The entry's segments carry outputMode 0 (no
    /// filter kind), so the kind is Wavee's reading, the cutoff lane's halves: low-pass below the centre, high-pass above (0.62, where
    /// NoiseOutEnd's first ramp ends: a high-pass at 380 Hz; 0.83: 6.3 kHz). Q 0.707. Call <see cref="NoiseOff"/> first.</summary>
    public static (byte Mode, float Hz) NoiseColour(float raw)
    {
        raw = Math.Clamp(raw, 0f, 1f);
        if (raw < 0.5f)
        {
            double t = Math.Pow(raw / 0.5, NoiseEase);
            return (1, (float)(NoiseLowFromHz * Math.Pow(NoiseLowToHz / NoiseLowFromHz, t)));
        }
        double u = 1 - Math.Pow(1 - (raw - 0.5) / 0.5, NoiseEase);
        return (2, (float)(NoiseHighFromHz * Math.Pow(NoiseHighToHz / NoiseHighFromHz, u)));
    }

    /// <summary>An effect's dry/wet curve value → the engine's 0..1 wet: the client's kind 4 entry is <c>100 × raw</c> %, 0 = off,
    /// so the wet is <c>% / 100</c> = raw.</summary>
    public static float FxWet(float raw) => Math.Clamp(raw, 0f, 1f);

    /// <summary>The tempo an echo or delay is timed by when the plan knows none (no recipe 1.16, no kind 222).</summary>
    public const float FallbackBpm = 120f;

    /// <summary>An echo or delay of <paramref name="beats"/> beats at <paramref name="bpm"/> (recipe 1.16, A's tempo), in frames at
    /// <paramref name="sampleRate"/>: <c>beats × 60 × rate / bpm</c>.</summary>
    public static int FxDelayFrames(float beats, float bpm, int sampleRate)
        => (int)Math.Round(beats * 60.0 * sampleRate / (bpm > 0 ? bpm : FallbackBpm));
}
