// ── Entities/Mix.Recipe.Curves.cs — CORE (#48 Mix M6, reference §3.2, §3.3, §5.1 fields 2.6 to 2.14) ──────────────────
//
// A recipe's CURVE OVERRIDES: a lane whose curve the user drew by hand in place of its style's. The Preset message holds
// them per ingredient and side:
//
//   6 / 7    volumeOut / volumeIn          Curves
//   8 / 9    eqOut / eqIn                  {1 low, 2 mid, 3 high: Curves}
//   10 / 11  filterOut / filterIn          {1 cutoff, 2 resonance, 3 noiseColor: Curves}
//   13 / 14  fxOut / fxIn                  {1 echoDryWet, 2 reverbDryWet, 3 delayDryWet: Curves}
//
//   Curves { 1[] Curve; 3 minimum double; 4 maximum double }
//   Curve  { 1[] Point; 2 start double; 3 end double }       one segment over start..end of the window
//   Point  { 1 x double; 2 y double }                         x 0..1 inside the segment
//
// A Curve of 2 points is a line and of 4 a cubic Bézier P0..P3, exactly the Connect metadata's spelling (MixCurves.WriteJson);
// another count is read as straight lines through its points (no capture shows one). Wavee's reading, not the client's.
//
// LOSSLESS. The recipe codec models these fields so the planner can play them; the patcher copies each one byte for byte
// unless the model's value for it changed (an editor edit), and only then writes it from the model (an unknown field INSIDE a
// rewritten curve message is not kept: only an edited field is rewritten). Built at decode, never per frame.

using System.Text;

namespace Wavee;

/// <summary>One lane's hand-drawn curve: its segments in the curve primitives' form and the clamp of its Curves message.</summary>
public sealed class MixCurve(CurveSegment[] segments, double min = 0, double max = 0)
{
    public CurveSegment[] Segments { get; } = segments;
    /// <summary>Curves 3 / 4: the value clamp; both 0 (absent) = none.</summary>
    public double Min { get; } = min;
    public double Max { get; } = max;
    public bool Clamps => Max > Min;

    /// <summary>A raw value after the message's clamp.</summary>
    public float Clamp(float v) => Clamps ? (float)Math.Clamp(v, Min, Max) : v;

    public bool SameAs(MixCurve? o)
    {
        if (o is null || o.Segments.Length != Segments.Length) return false;
        if (BitConverter.DoubleToInt64Bits(Min) != BitConverter.DoubleToInt64Bits(o.Min) || BitConverter.DoubleToInt64Bits(Max) != BitConverter.DoubleToInt64Bits(o.Max)) return false;
        for (int i = 0; i < Segments.Length; i++) if (Segments[i] != o.Segments[i]) return false;
        return true;
    }

    /// <summary>The curve-editing model of reference §3.3, one segment between two points: a straight line when <paramref name="bend"/>
    /// is 0 and not <paramref name="smooth"/>; else a cubic whose controls sit at a third and two thirds of the value span, raised by
    /// <c>bend × 2/3</c> (clamped so they stay in 0..1); <paramref name="smooth"/> holds each end flat into its point (the S the style
    /// table draws). Wavee's reading of the client's model, which the reference states only in words.</summary>
    public static CurveSegment Segment(float start, float end, float y0, float y1, float bend, bool smooth)
    {
        if (bend == 0f && !smooth) return new CurveSegment(start, end, false, 0, y0, 0, 0, 0, 0, 1, y1);
        float lift = bend * 2f / 3f;
        float c1 = Math.Clamp((smooth ? y0 : y0 + (y1 - y0) / 3f) + lift, 0f, 1f);
        float c2 = Math.Clamp((smooth ? y1 : y0 + 2f * (y1 - y0) / 3f) + lift, 0f, 1f);
        return new CurveSegment(start, end, true, 0, y0, smooth ? .5f : 1f / 3f, c1, smooth ? .5f : 2f / 3f, c2, 1, y1);
    }
}

/// <summary>Every curve override of one recipe (Preset fields 6-11, 13, 14). Immutable: an edit makes a copy (<see cref="With"/>).</summary>
public sealed class MixCurveOverrides
{
    const int Slots = 20;   // MixCurveLane (10) × side (2)
    static readonly bool[] s_sides = [false, true];
    readonly MixCurve?[] _curves;

    MixCurveOverrides(MixCurve?[] curves) => _curves = curves;

    public static readonly MixCurveOverrides Empty = new(new MixCurve?[Slots]);

    static int Slot(MixCurveLane lane, bool incoming) => (int)lane * 2 + (incoming ? 1 : 0);

    public MixCurve? Get(MixCurveLane lane, bool incoming) => _curves[Slot(lane, incoming)];

    public bool IsEmpty
    {
        get
        {
            foreach (var c in _curves) if (c is not null) return false;
            return true;
        }
    }

    /// <summary>A copy with <paramref name="lane"/>'s curve on one side replaced (null removes the override).</summary>
    public MixCurveOverrides With(MixCurveLane lane, bool incoming, MixCurve? curve)
    {
        var copy = (MixCurve?[])_curves.Clone();
        copy[Slot(lane, incoming)] = curve;
        return new MixCurveOverrides(copy);
    }

    /// <summary>The Preset field (6-11, 13, 14) that holds a lane's override on one side, and its sub-field (0 = the Curves message
    /// is the field itself, 1-3 = the band, filter part or effect inside it).</summary>
    public static (int Field, int Sub) FieldOf(MixCurveLane lane, bool incoming) => lane switch
    {
        MixCurveLane.Volume => (incoming ? 7 : 6, 0),
        MixCurveLane.EqLow => (incoming ? 9 : 8, 1),
        MixCurveLane.EqMid => (incoming ? 9 : 8, 2),
        MixCurveLane.EqHigh => (incoming ? 9 : 8, 3),
        MixCurveLane.FilterCutoff => (incoming ? 11 : 10, 1),
        MixCurveLane.FilterResonance => (incoming ? 11 : 10, 2),
        MixCurveLane.NoiseColor => (incoming ? 11 : 10, 3),
        MixCurveLane.EchoDryWet => (incoming ? 14 : 13, 1),
        MixCurveLane.ReverbDryWet => (incoming ? 14 : 13, 2),
        _ => (incoming ? 14 : 13, 3),
    };

    public static bool IsCurveField(int presetField) => presetField is >= 6 and <= 11 or 13 or 14;

    /// <summary>The lanes one Preset field holds, with their side.</summary>
    static IEnumerable<(MixCurveLane Lane, bool Incoming, int Sub)> LanesOf(int field)
    {
        foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
            foreach (bool incoming in s_sides)
            {
                var (f, sub) = FieldOf(lane, incoming);
                if (f == field) yield return (lane, incoming, sub);
            }
    }

    /// <summary>The Preset fields this set holds an override in (bit n = field n), as <see cref="MixRecipe.CurveOverrides"/> spells them.</summary>
    public uint FieldBits()
    {
        uint bits = 0;
        foreach (int f in (int[])[6, 7, 8, 9, 10, 11, 13, 14]) if (HasField(f)) bits |= 1u << f;
        return bits;
    }

    /// <summary>Does this set hold any override in Preset field <paramref name="field"/>?</summary>
    public bool HasField(int field)
    {
        foreach (var (lane, incoming, _) in LanesOf(field)) if (Get(lane, incoming) is not null) return true;
        return false;
    }

    /// <summary>Do two sets hold the same overrides in Preset field <paramref name="field"/>? (null = none.)</summary>
    public static bool SameField(MixCurveOverrides? a, MixCurveOverrides? b, int field)
    {
        foreach (var (lane, incoming, _) in LanesOf(field))
        {
            var x = a?.Get(lane, incoming);
            var y = b?.Get(lane, incoming);
            if (x is null ? y is not null : !x.SameAs(y)) return false;
        }
        return true;
    }

    // ── decode ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Merge one Preset curve field (<paramref name="field"/>, its payload) into <paramref name="into"/> (null = none yet).</summary>
    internal static MixCurveOverrides? Decode(int field, ReadOnlySpan<byte> payload, MixCurveOverrides? into)
    {
        if (!IsCurveField(field)) return into;
        var curves = (MixCurve?[])(into ?? Empty)._curves.Clone();
        bool volume = field is 6 or 7;
        if (volume) Put(curves, field, 0, payload);
        else
            for (var r = new Spotify.Decode.ProtoReader(payload); r.Next();)
            {
                if (r.Field is >= 1 and <= 3 && r.Wire == 2) Put(curves, field, r.Field, r.Bytes());
                else r.Skip();
            }
        return new MixCurveOverrides(curves);

        static void Put(MixCurve?[] curves, int field, int sub, ReadOnlySpan<byte> curvesMessage)
        {
            foreach (var (lane, incoming, s) in LanesOf(field))
                if (s == sub) { curves[Slot(lane, incoming)] = DecodeCurves(curvesMessage); return; }
        }
    }

    /// <summary>A Curves message → its segments and clamp.</summary>
    static MixCurve DecodeCurves(ReadOnlySpan<byte> proto)
    {
        var segments = new List<CurveSegment>(4);
        double min = 0, max = 0;
        for (var r = new Spotify.Decode.ProtoReader(proto); r.Next();)
        {
            switch (r.Field)
            {
                case 1 when r.Wire == 2: AddCurve(segments, r.Bytes()); break;
                case 3 when r.Wire == 1: min = r.Double(); break;
                case 4 when r.Wire == 1: max = r.Double(); break;
                default: r.Skip(); break;
            }
        }
        return new MixCurve(segments.ToArray(), min, max);
    }

    /// <summary>One Curve message (its points, start, end) as segments: 2 points a line, 4 a cubic, any other count straight lines
    /// through its points.</summary>
    static void AddCurve(List<CurveSegment> into, ReadOnlySpan<byte> proto)
    {
        double start = 0, end = 0;
        Span<float> xs = stackalloc float[64];
        Span<float> ys = stackalloc float[64];
        int n = 0;
        for (var r = new Spotify.Decode.ProtoReader(proto); r.Next();)
        {
            switch (r.Field)
            {
                case 1 when r.Wire == 2:
                    double x = 0, y = 0;
                    for (var p = new Spotify.Decode.ProtoReader(r.Bytes()); p.Next();)
                    {
                        if (p.Field == 1 && p.Wire == 1) x = p.Double();
                        else if (p.Field == 2 && p.Wire == 1) y = p.Double();
                        else p.Skip();
                    }
                    if (n < xs.Length) { xs[n] = (float)x; ys[n] = (float)y; n++; }
                    break;
                case 2 when r.Wire == 1: start = r.Double(); break;
                case 3 when r.Wire == 1: end = r.Double(); break;
                default: r.Skip(); break;
            }
        }
        float s = (float)start, e = (float)end;
        if (n == 0) return;
        if (n == 1) { into.Add(CurveSegment.Flat(s, e, ys[0])); return; }
        if (n == 2) { into.Add(new CurveSegment(s, e, false, xs[0], ys[0], 0, 0, 0, 0, xs[1], ys[1])); return; }
        if (n == 4) { into.Add(new CurveSegment(s, e, true, xs[0], ys[0], xs[1], ys[1], xs[2], ys[2], xs[3], ys[3])); return; }
        for (int i = 0; i + 1 < n; i++)
        {
            float a = s + (e - s) * xs[i], b = s + (e - s) * xs[i + 1];
            if (b <= a) continue;
            into.Add(new CurveSegment(a, b, false, 0, ys[i], 0, 0, 0, 0, 1, ys[i + 1]));
        }
    }

    // ── encode ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Write Preset field <paramref name="field"/> (tag included) from the model; nothing when it holds no override.</summary>
    internal void WriteField(ref Spotify.Decode.ProtoWriter w, int field)
    {
        if (!HasField(field)) return;
        int m = w.Open(field);
        if (field is 6 or 7)
        {
            if (Get(MixCurveLane.Volume, field == 7) is { } v) WriteCurvesBody(ref w, v);
        }
        else
            foreach (var (lane, incoming, sub) in LanesOf(field))
            {
                if (Get(lane, incoming) is not { } c) continue;
                int inner = w.Open(sub);
                WriteCurvesBody(ref w, c);
                w.Close(inner);
            }
        w.Close(m);
    }

    /// <summary>A Curves body as the official encoder writes one (proto3: a zero value is left out).</summary>
    static void WriteCurvesBody(ref Spotify.Decode.ProtoWriter w, MixCurve c)
    {
        foreach (var s in c.Segments)
        {
            int curve = w.Open(1);
            Point(ref w, s.X0, s.Y0);
            if (s.Cubic) { Point(ref w, s.X1, s.Y1); Point(ref w, s.X2, s.Y2); }
            Point(ref w, s.X3, s.Y3);
            D(ref w, 2, s.Start);
            D(ref w, 3, s.End);
            w.Close(curve);
        }
        D(ref w, 3, c.Min);
        D(ref w, 4, c.Max);

        static void Point(ref Spotify.Decode.ProtoWriter w, float x, float y)
        {
            int p = w.Open(1);
            D(ref w, 1, x);
            D(ref w, 2, y);
            w.Close(p);
        }
    }

    static void D(ref Spotify.Decode.ProtoWriter w, int field, double v)
    {
        if (BitConverter.DoubleToInt64Bits(v) != 0) w.F64(field, v);
    }

    /// <summary>An upper bound on the bytes <see cref="WriteField"/> writes for every field.</summary>
    public int MaxBytes()
    {
        int n = 0;
        foreach (var c in _curves)
            if (c is not null) n += 48 + c.Segments.Length * (4 * 24 + 32);
        return n;
    }

    /// <summary>"volume.out:2 eqLow.in:1" — the overridden lanes and their segment counts ("-" when none).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (MixCurveLane lane in Enum.GetValues<MixCurveLane>())
            foreach (bool incoming in s_sides)
                if (Get(lane, incoming) is { } c)
                    sb.Append(sb.Length > 0 ? " " : "").Append(lane).Append(incoming ? ".in:" : ".out:").Append(c.Segments.Length);
        return sb.Length == 0 ? "-" : sb.ToString();
    }
}
