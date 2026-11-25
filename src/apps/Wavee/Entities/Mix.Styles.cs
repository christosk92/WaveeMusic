// ── Entities/Mix.Styles.cs — CORE (#48 Mix, full-support plan §2.3.1; reference §1-§3) ──────────────────────────────────
//
// The style table: Spotify's 22 presets expanded to their six ingredient styles, and every volume, EQ, filter and effect
// style as a curve generator of the transition's length in bars. The client computes its curves from (style id, bars);
// these generators are the rules behind its own tables (desktop 1.3.10, style table version 8, every curve at 1, 2, 4, 8,
// 16, 32 and 100 bars, the effects at 60-180 bpm x 2-16 bars). `mixstyles check=<dir>` compares them with those tables,
// which stay outside the repo.
//
// One unit drives every bar-dependent shape: b = clamp(bars, 2, 32). The tables at 1 bar equal those at 2, the tables at
// 100 equal those at 32, and the official player's two unmatched Fade seams (durationBars 0) place the bass swap where a
// 2-bar window has it (protocol §6.5). In window fractions:
//
//   bar = 1/b      beat = 1/(4b)      q = 1/(16b) (a sixteenth of a bar)      e = 1/(256b)
//
//   volume  Cut                     out 1 to 0 over [1/2, 1/2+q] (a line); in 0 to 1 over [1/2-q, 1/2]
//           Crossfade               one line over the window
//           SlowInFastOut           out as Cut; in a line to 1 over the first half
//           FastInSlowOut           out a line to 0 over the second half; in as Cut
//           SlowInSlowOut           out a line over the second half; in a line over the first half
//           CrossShape              one cubic (0,1)(.6,1)(.6,0)(1,0) / (0,0)(.4,0)(.4,1)(1,1); no bar dependence
//           FullOverlap             out an S to 0 over the last e; in an S to 1 over the first e
//           Switcheroo              flat steps alternating 0/1 (out starts at 0, in at 1): two per bar, then the last three
//                                   bars in beats, half beats and quarter beats
//           SlowInFastOutAtEnd      out as FullOverlap; in as SlowInSlowOut
//           FastInAtStartSlowOut    out as SlowInSlowOut; in as FullOverlap
//           SlowInSemiFastOutAtEnd  out a line to 0 over the last bar; in as SlowInSlowOut
//   EQ      (0.5 = 0 dB; a swap takes the bass to 0 = kill, mid and high to 0.2)
//           BassSwap / MidSwap / HiSwap / BassAndMidSwap   an S over [1/2-q, 1/2] on those bands
//           EqSwap                  low as BassSwap; mid one S over the window; high an S over the first half
//           BassSwapAtEnd           an S over the last q;  BassSwapAtStart an S over the first e
//           BassCuts                out an S over [1-bar, 1-bar+q]; in as BassSwapAtEnd
//           NoBass                  out as BassSwapAtStart; in as BassSwapAtEnd
//           BassFadeIn              out all bands flat; in the bass a line 0 to 0.5
//           BassCrossfade           the bass one line each way
//           StartFadeOut, BassFadeOut   out the bass (0,.5)(.75,.5)(.75,0)(1,0) over the first half; in all bands flat
//   filter  (cutoff 0.5 = open, 0 = low-pass shut, 1 = high-pass shut; resonance flat 0.5 wherever a cutoff moves)
//           a sweep out holds the first half open and ramps over the second; a sweep in ramps over the first half
//           HighPassFilterHalfOut   out a line 0.5 to 0.7784 over the second half
//           NoiseOutEnd             noise colour out: flat, then 0.5 to 0.62 over one beat from the centre, then to 0.83
//           Echo, Reverb, Riser, ReverbRiser, EchoAtEnd   no curves (the client tables none)
//   effect  one dry/wet lane, out only, independent of bpm and bars (20 of 20 table combinations identical):
//           OutCentre a line to 0.5 over the first half; OutEnd a line to 0.5 over the second half; CutEnd the same line
//           to 0.99999, then 0
//
// Pure and allocation-free: every generator writes into the caller's span; the tables are built when a transition is
// planned, never per frame.

using V = Wavee.MixVolumeStyle;
using E = Wavee.MixEqStyle;
using F = Wavee.MixFilterStyle;
using X = Wavee.MixFxStyle;
using L = Wavee.MixCurveLane;

namespace Wavee;

/// <summary>Recipe field 2.1, the preset id. The nine offered presets carry Spotify's UI names (the client's enum names in
/// the comments); the internal ones keep their enum names. They occur in Auto and possibly in stored recipes.</summary>
public enum MixPresetId : sbyte
{
    Unknown = -1, None = 0,
    Fade = 1,                       // SimpleFade
    Blend = 2,                      // EqSwap
    Rise = 3,                       // BassSwap
    Melt = 4,                       // BassSwapFilterIn
    BassSwapFilterOut = 5, BassSwapEcho = 6, BassAndMidSwap = 7, FilterFadeOut = 8, FilterFadeIn = 9, FilterFadeInOut = 10,
    Slam = 11,                      // SimpleCut
    ReverbCut = 12, RiserCut = 13, ReverbRiserCut = 14, EchoCut = 15, BassSwapAtEnd = 16,
    Wave = 17,                      // BassSwapLowPassFilterIn
    BassSwapLowPassFilterOut = 18, LowPassFilterFadeInOut = 19,
    Dissolve = 20, Surge = 21, Afterglow = 22,
}

/// <summary>Volume styles (recipe 2.3).</summary>
public enum MixVolumeStyle : sbyte
{
    Unknown = -1, None = 0, Cut = 1, Crossfade = 2, SlowInFastOut = 3, FastInSlowOut = 4, SlowInSlowOut = 5, CrossShape = 6,
    FullOverlap = 7, Switcheroo = 8, SlowInFastOutAtEnd = 9, FastInAtStartSlowOut = 10, SlowInSemiFastOutAtEnd = 11,
}

/// <summary>EQ styles (recipe 2.4).</summary>
public enum MixEqStyle : sbyte
{
    Unknown = -1, None = 0, EqSwap = 1, BassFadeIn = 2, BassCrossfade = 3, BassSwap = 4, BassAndMidSwap = 7, MidSwap = 8,
    HiSwap = 9, BassSwapAtEnd = 10, BassSwapAtStart = 11, BassCuts = 12, NoBass = 13, StartFadeOut = 14, BassFadeOut = 15,
}

/// <summary>Filter styles (recipe 2.5, the client's <c>filterFx</c>).</summary>
public enum MixFilterStyle : sbyte
{
    Unknown = -1, None = 0, Echo = 1, Reverb = 2, Riser = 3, ReverbRiser = 4, FilterIn = 5, FilterOut = 6, FilterInFilterOut = 7,
    EchoAtEnd = 8, LowPassFilterIn = 9, LowPassFilterOut = 10, LowPassFilterInLowPassFilterOut = 11, FilterInLowPassFilterOut = 12,
    LowPassFilterInFilterOut = 13, HighPassFilterHalfOut = 14, NoiseOutEnd = 15,
}

/// <summary>Effect styles (recipe 2.12; style table version 6 and up).</summary>
public enum MixFxStyle : sbyte
{
    Unknown = -1, None = 0, ReverbOutCentre = 1, ReverbCutEnd = 2, ReverbOutEnd = 3, EchoHalfCutEnd = 4, EchoHalfOutEnd = 5,
    Echo3QCutEnd = 6, Echo3QOutEnd = 7, DelayHalfCutEnd = 8, Delay3QCutEnd = 9, Echo1CutEnd = 10, Echo1OutEnd = 11,
}

/// <summary>Looping styles (recipe 2.16; style table version 8).</summary>
public enum MixLoopingStyle : sbyte { Unknown = -1, None = 0, TwoBeat = 1, FourBeat = 2, EightBeat = 3, SixteenBeat = 4, HalvingEnd = 5, OneBeat = 8 }

/// <summary>A loop block's type (recipe Block field 1). No tabled style uses Spinback or VinylStop.</summary>
public enum MixLoopBlockType : byte { Loop = 1, Spinback = 2, VinylStop = 3 }

/// <summary>Every automated quantity a style drives, one curve per side.</summary>
public enum MixCurveLane : byte
{
    Volume, EqLow, EqMid, EqHigh, FilterCutoff, FilterResonance, NoiseColor, EchoDryWet, ReverbDryWet, DelayDryWet,
}

/// <summary>A preset expanded: one style per ingredient. <see cref="Jogwheel"/> stays a raw id (no jog-wheel style list is
/// known; every preset names 0).</summary>
public readonly record struct MixStyleSet(MixVolumeStyle Volume, MixEqStyle Eq, MixFilterStyle Filter, MixFxStyle Fx, int Jogwheel,
    MixLoopingStyle Looping)
{
    /// <summary>"No transition": no ingredient moves anything.</summary>
    public bool IsNone => Volume == V.None && Eq == E.None && Filter == F.None && Fx == X.None && Looping == MixLoopingStyle.None;
}

/// <summary>A recipe's style overrides (2.3, 2.4, 2.5, 2.12, 2.15, 2.16); null = the field is absent and the preset's style holds.</summary>
public readonly record struct MixStyleOverrides(MixVolumeStyle? Volume = null, MixEqStyle? Eq = null, MixFilterStyle? Filter = null,
    MixFxStyle? Fx = null, int? Jogwheel = null, MixLoopingStyle? Looping = null);

/// <summary>One loop block (recipe Block): loop <see cref="Beats"/> beats from <see cref="Start"/> to <see cref="End"/> of the window.</summary>
public readonly record struct MixLoopBlock(MixLoopBlockType Type, float Beats, float Start, float End);

/// <summary>The preset table and the curve generators (see the file header for the rule behind each style).</summary>
public static class MixStyles
{
    /// <summary>The longest transition the client allows, ms.</summary>
    public const int MaxTransitionMs = 35_000;
    /// <summary>The client's beatmatch opt-out bound, ms (what it bounds is open, reference §7.3).</summary>
    public const int BeatmatchOptOutMs = 5_000;
    /// <summary>The engine's envelope table: 1025 points spread evenly over the window, first = start, last = end.</summary>
    public const int TableSize = 1025;
    /// <summary>The most segments one lane writes: Switcheroo at 32 bars (29 bars of two steps, then 4 + 8 + 16).</summary>
    public const int MaxSegments = 86;
    /// <summary>The most loop blocks one looping style writes (HalvingEnd).</summary>
    public const int MaxLoopBlocks = 3;
    /// <summary>The value a swap takes the mid and high bands to (the client's 0.19999999999999998).</summary>
    public const float EqBandDown = 0.2f;
    /// <summary>Where an effect's CutEnd shape drops its wet level to 0.</summary>
    public const float FxCutAt = 0.99999f;

    /// <summary>The offered presets in the editor's order (style table version 8); "No transition" follows them.</summary>
    public static readonly MixPresetId[] EditorOrder =
    [
        MixPresetId.Fade, MixPresetId.Rise, MixPresetId.Blend, MixPresetId.Wave, MixPresetId.Melt, MixPresetId.Slam,
        MixPresetId.Dissolve, MixPresetId.Surge, MixPresetId.Afterglow,
    ];

    /// <summary>The bar count every bar-dependent shape is built for: 0 (not beatmatched) and 1 use the 2-bar layout, above
    /// 32 the 32-bar layout.</summary>
    public static int UnitBars(int bars) => Math.Clamp(bars, 2, 32);

    /// <summary>A preset id the table knows (1..22). Another id renders as Fade; 0 is "No transition".</summary>
    public static bool IsKnown(int presetId) => presetId is >= 1 and <= 22;

    /// <summary>A preset's six styles (the client's preset table). 0 = "No transition" (every style None); an id the table
    /// does not know renders as Fade.</summary>
    public static MixStyleSet Preset(int presetId) => presetId switch
    {
        0 => default,
        1 => Set(V.CrossShape, E.BassSwap, F.None, X.None),
        2 => Set(V.FullOverlap, E.EqSwap, F.None, X.None),
        3 => Set(V.FullOverlap, E.BassSwapAtEnd, F.LowPassFilterInFilterOut, X.None),
        4 => Set(V.SlowInSlowOut, E.BassSwap, F.FilterInFilterOut, X.None),
        5 => Set(V.SlowInFastOutAtEnd, E.BassSwap, F.FilterOut, X.None),
        6 => Set(V.FullOverlap, E.BassSwapAtEnd, F.LowPassFilterInFilterOut, X.None),
        7 => Set(V.SlowInSlowOut, E.BassAndMidSwap, F.None, X.None),
        8 => Set(V.SlowInFastOutAtEnd, E.BassSwap, F.FilterOut, X.None),
        9 => Set(V.FastInAtStartSlowOut, E.BassSwap, F.FilterIn, X.None),
        10 => Set(V.FullOverlap, E.BassSwap, F.FilterInFilterOut, X.None),
        11 or 12 or 13 or 14 or 15 => Set(V.Cut, E.None, F.None, X.None),
        16 => Set(V.SlowInFastOutAtEnd, E.BassSwapAtEnd, F.None, X.None),
        17 => Set(V.FullOverlap, E.BassSwap, F.LowPassFilterInLowPassFilterOut, X.None),
        18 => Set(V.SlowInFastOutAtEnd, E.BassSwap, F.LowPassFilterOut, X.None),
        19 => Set(V.FullOverlap, E.BassSwap, F.LowPassFilterInLowPassFilterOut, X.None),
        20 => Set(V.SlowInFastOutAtEnd, E.BassSwapAtEnd, F.HighPassFilterHalfOut, X.EchoHalfOutEnd),
        21 => Set(V.SlowInFastOutAtEnd, E.BassSwapAtEnd, F.HighPassFilterHalfOut, X.ReverbOutEnd),
        22 => Set(V.CrossShape, E.BassSwap, F.None, X.ReverbOutCentre),
        _ => Set(V.CrossShape, E.BassSwap, F.None, X.None),
    };

    static MixStyleSet Set(V v, E e, F f, X x) => new(v, e, f, x, 0, MixLoopingStyle.None);

    /// <summary>The styles a recipe plays: its preset's, with each present override in place of the preset's own.</summary>
    public static MixStyleSet Resolve(int presetId, in MixStyleOverrides overrides)
    {
        var p = Preset(presetId);
        return new(overrides.Volume ?? p.Volume, overrides.Eq ?? p.Eq, overrides.Filter ?? p.Filter, overrides.Fx ?? p.Fx,
            overrides.Jogwheel ?? p.Jogwheel, overrides.Looping ?? p.Looping);
    }

    // ── curves ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One lane's curve for one side, for a window of <paramref name="bars"/> (0 = not beatmatched). Writes at most
    /// <see cref="MaxSegments"/> segments; returns the count (0 = the style leaves the lane alone: its metadata key is
    /// omitted and the engine lane stays off).</summary>
    public static int Segments(in MixStyleSet set, MixCurveLane lane, bool incoming, int bars, Span<CurveSegment> into) => lane switch
    {
        L.Volume => Volume(set.Volume, incoming, bars, into),
        L.EqLow or L.EqMid or L.EqHigh => Eq(set.Eq, lane, incoming, bars, into),
        L.FilterCutoff or L.FilterResonance or L.NoiseColor => Filter(set.Filter, lane, incoming, bars, into),
        _ => Fx(set.Fx, lane, incoming, into),
    };

    /// <summary>Sample one lane into the engine's table (<see cref="TableSize"/> points, by <see cref="MixCurves.Sample"/>).
    /// False when the style leaves the lane alone (the table is untouched).</summary>
    public static bool Sample(in MixStyleSet set, MixCurveLane lane, bool incoming, int bars, Span<float> table)
    {
        Span<CurveSegment> seg = stackalloc CurveSegment[MaxSegments];
        int n = Segments(in set, lane, incoming, bars, seg);
        if (n == 0) return false;
        MixCurves.Sample(seg[..n], table);
        return true;
    }

    /// <summary>A volume style's curve.</summary>
    public static int Volume(MixVolumeStyle style, bool incoming, int bars, Span<CurveSegment> into)
    {
        int b = UnitBars(bars);
        float q = 1f / (16 * b), e = 1f / (256 * b);
        switch (style)
        {
            case V.Cut: return incoming ? Step(into, .5f - q, q, 0, 1, false) : Step(into, .5f, q, 1, 0, false);
            case V.Crossfade: into[0] = incoming ? Line(0, 1, 0, 1) : Line(0, 1, 1, 0); return 1;
            case V.SlowInFastOut: return incoming ? SlowIn(into) : Step(into, .5f, q, 1, 0, false);
            case V.FastInSlowOut: return incoming ? Step(into, .5f - q, q, 0, 1, false) : SlowOut(into, .5f);
            case V.SlowInSlowOut: return incoming ? SlowIn(into) : SlowOut(into, .5f);
            case V.CrossShape:
                into[0] = incoming ? new CurveSegment(0, 1, true, 0, 0, .4f, 0, .4f, 1, 1, 1) : new CurveSegment(0, 1, true, 0, 1, .6f, 1, .6f, 0, 1, 0);
                return 1;
            case V.FullOverlap: return incoming ? Step(into, 0, e, 0, 1, true) : Step(into, 1 - e, e, 1, 0, true);
            case V.Switcheroo: return Switcheroo(into, incoming, b);
            case V.SlowInFastOutAtEnd: return incoming ? SlowIn(into) : Step(into, 1 - e, e, 1, 0, true);
            case V.FastInAtStartSlowOut: return incoming ? Step(into, 0, e, 0, 1, true) : SlowOut(into, .5f);
            case V.SlowInSemiFastOutAtEnd: return incoming ? SlowIn(into) : SlowOut(into, 1 - 1f / b);
            default: return 0;
        }

        static int SlowIn(Span<CurveSegment> into) { into[0] = Line(0, .5f, 0, 1); into[1] = CurveSegment.Flat(.5f, 1, 1); return 2; }
        static int SlowOut(Span<CurveSegment> into, float at) { into[0] = CurveSegment.Flat(0, at, 1); into[1] = Line(at, 1, 1, 0); return 2; }
    }

    /// <summary>Switcheroo: flat steps alternating between the decks (A starts silent, B loud), two per bar, then the last
    /// three bars in beats, half beats and quarter beats.</summary>
    static int Switcheroo(Span<CurveSegment> into, bool incoming, int b)
    {
        int n = 0;
        float at = 0;
        for (int bar = 0; bar < b; bar++)
        {
            int left = b - bar, steps = left == 1 ? 16 : left == 2 ? 8 : left == 3 ? 4 : 2;
            for (int k = 0; k < steps; k++, n++)
            {
                float end = (bar * steps + k + 1) / (float)(b * steps);
                into[n] = CurveSegment.Flat(at, end, ((n & 1) == 0) == incoming ? 1f : 0f);
                at = end;
            }
        }
        return n;
    }

    /// <summary>An EQ style's curve for one band (<see cref="MixCurveLane.EqLow"/>, <see cref="MixCurveLane.EqMid"/> or
    /// <see cref="MixCurveLane.EqHigh"/>).</summary>
    public static int Eq(MixEqStyle style, MixCurveLane band, bool incoming, int bars, Span<CurveSegment> into)
    {
        if (band is not (L.EqLow or L.EqMid or L.EqHigh)) return 0;
        int b = UnitBars(bars);
        float q = 1f / (16 * b), e = 1f / (256 * b);
        bool low = band == L.EqLow, mid = band == L.EqMid, high = band == L.EqHigh;
        float down = low ? 0f : EqBandDown;
        float from = incoming ? down : .5f, to = incoming ? .5f : down;
        switch (style)
        {
            case E.EqSwap:
                if (low) return Step(into, .5f - q, q, from, to, true);
                if (mid) { into[0] = CurveSegment.S(0, 1, from, to); return 1; }
                into[0] = CurveSegment.S(0, .5f, from, to); into[1] = CurveSegment.Flat(.5f, 1, to); return 2;
            case E.BassFadeIn:
                if (!incoming) { into[0] = CurveSegment.Flat(0, 1, .5f); return 1; }
                if (!low) return 0;
                into[0] = Line(0, 1, 0, .5f); return 1;
            case E.BassCrossfade:
                if (!low) return 0;
                into[0] = Line(0, 1, from, to); return 1;
            case E.BassSwap: return low ? Step(into, .5f - q, q, from, to, true) : 0;
            case E.BassAndMidSwap: return low || mid ? Step(into, .5f - q, q, from, to, true) : 0;
            case E.MidSwap: return mid ? Step(into, .5f - q, q, from, to, true) : 0;
            case E.HiSwap: return high ? Step(into, .5f - q, q, from, to, true) : 0;
            case E.BassSwapAtEnd: return low ? Step(into, 1 - q, q, from, to, true) : 0;
            case E.BassSwapAtStart: return low ? Step(into, 0, e, from, to, true) : 0;
            case E.BassCuts: return !low ? 0 : incoming ? Step(into, 1 - q, q, from, to, true) : Step(into, 1 - 1f / b, q, from, to, true);
            case E.NoBass: return !low ? 0 : incoming ? Step(into, 1 - q, q, from, to, true) : Step(into, 0, e, from, to, true);
            case E.StartFadeOut or E.BassFadeOut:
                if (incoming) { into[0] = CurveSegment.Flat(0, 1, .5f); return 1; }
                if (!low) return 0;
                into[0] = new CurveSegment(0, .5f, true, 0, .5f, .75f, .5f, .75f, 0, 1, 0);
                into[1] = CurveSegment.Flat(.5f, 1, 0);
                return 2;
            default: return 0;
        }
    }

    /// <summary>A filter style's curve for <see cref="MixCurveLane.FilterCutoff"/>, <see cref="MixCurveLane.FilterResonance"/>
    /// or <see cref="MixCurveLane.NoiseColor"/>.</summary>
    public static int Filter(MixFilterStyle style, MixCurveLane lane, bool incoming, int bars, Span<CurveSegment> into)
    {
        if (style == F.NoiseOutEnd)
        {
            if (lane != L.NoiseColor) return 0;
            if (incoming) { into[0] = CurveSegment.Flat(0, 1, .5f); return 1; }
            float beat = 1f / (4 * UnitBars(bars));
            into[0] = CurveSegment.Flat(0, .5f, .5f);
            into[1] = Line(.5f, .5f + beat, .5f, .62f);
            into[2] = Line(.5f + beat, 1, .62f, .83f);
            return 3;
        }
        // The cutoff's end value on each side: 0.5 = open (flat), 0 = a low-pass sweep, 1 = a high-pass sweep.
        (float Out, float In) sweep = style switch
        {
            F.FilterIn => (.5f, 1f),
            F.FilterOut => (1f, .5f),
            F.FilterInFilterOut => (1f, 1f),
            F.LowPassFilterIn => (.5f, 0f),
            F.LowPassFilterOut => (0f, .5f),
            F.LowPassFilterInLowPassFilterOut => (0f, 0f),
            F.FilterInLowPassFilterOut => (0f, 1f),
            F.LowPassFilterInFilterOut => (1f, 0f),
            F.HighPassFilterHalfOut => (.7784f, .5f),
            _ => (-1f, -1f),
        };
        if (sweep.Out < 0 || lane is not (L.FilterCutoff or L.FilterResonance)) return 0;
        float end = incoming ? sweep.In : sweep.Out;
        if (lane == L.FilterResonance || end == .5f) { into[0] = CurveSegment.Flat(0, 1, .5f); return 1; }
        if (incoming) { into[0] = CurveSegment.Ramp(0, .5f, end, .5f); into[1] = CurveSegment.Flat(.5f, 1, .5f); return 2; }
        into[0] = CurveSegment.Flat(0, .5f, .5f);
        into[1] = style == F.HighPassFilterHalfOut ? Line(.5f, 1, .5f, end) : CurveSegment.Ramp(.5f, 1, .5f, end);
        return 2;
    }

    /// <summary>The dry/wet lane an effect style drives; null for None.</summary>
    public static MixCurveLane? FxLane(MixFxStyle style) => style switch
    {
        X.ReverbOutCentre or X.ReverbCutEnd or X.ReverbOutEnd => L.ReverbDryWet,
        X.EchoHalfCutEnd or X.EchoHalfOutEnd or X.Echo3QCutEnd or X.Echo3QOutEnd or X.Echo1CutEnd or X.Echo1OutEnd => L.EchoDryWet,
        X.DelayHalfCutEnd or X.Delay3QCutEnd => L.DelayDryWet,
        _ => null,
    };

    /// <summary>The echo or delay time in beats of A's tempo (½, ¾ or 1; 0 for reverb and None): the engine's delay line,
    /// not a curve.</summary>
    public static float FxBeats(MixFxStyle style) => style switch
    {
        X.EchoHalfCutEnd or X.EchoHalfOutEnd or X.DelayHalfCutEnd => .5f,
        X.Echo3QCutEnd or X.Echo3QOutEnd or X.Delay3QCutEnd => .75f,
        X.Echo1CutEnd or X.Echo1OutEnd => 1f,
        _ => 0f,
    };

    /// <summary>True when the style drops its wet level to 0 at the very end (the CutEnd styles).</summary>
    public static bool FxCutsAtEnd(MixFxStyle style) =>
        style is X.ReverbCutEnd or X.EchoHalfCutEnd or X.Echo3QCutEnd or X.DelayHalfCutEnd or X.Delay3QCutEnd or X.Echo1CutEnd;

    /// <summary>An effect style's dry/wet curve: outgoing side only, on the style's one lane. It does not depend on the tempo
    /// or the bars (the tempo sets the echo time, <see cref="FxBeats"/>).</summary>
    public static int Fx(MixFxStyle style, MixCurveLane lane, bool incoming, Span<CurveSegment> into)
    {
        if (incoming || lane != FxLane(style)) return 0;
        if (style == X.ReverbOutCentre) { into[0] = Line(0, .5f, 0, .5f); into[1] = CurveSegment.Flat(.5f, 1, .5f); return 2; }
        into[0] = CurveSegment.Flat(0, .5f, 0);
        if (!FxCutsAtEnd(style)) { into[1] = Line(.5f, 1, 0, .5f); return 2; }
        into[1] = Line(.5f, FxCutAt, 0, .5f);
        into[2] = CurveSegment.Flat(FxCutAt, 1, 0);
        return 3;
    }

    /// <summary>A looping style's blocks (outgoing side; the client tables no incoming blocks).</summary>
    public static int LoopBlocks(MixLoopingStyle style, Span<MixLoopBlock> into)
    {
        float beats = style switch
        {
            MixLoopingStyle.OneBeat => 1, MixLoopingStyle.TwoBeat => 2, MixLoopingStyle.FourBeat => 4, MixLoopingStyle.EightBeat => 8,
            MixLoopingStyle.SixteenBeat => 16, _ => 0,
        };
        if (beats > 0) { into[0] = new(MixLoopBlockType.Loop, beats, 0, 1); return 1; }
        if (style != MixLoopingStyle.HalvingEnd) return 0;
        into[0] = new(MixLoopBlockType.Loop, 1, .5f, .75f);
        into[1] = new(MixLoopBlockType.Loop, .5f, .75f, .875f);
        into[2] = new(MixLoopBlockType.Loop, .25f, .875f, 1);
        return 3;
    }

    /// <summary>The Connect metadata key's lane part (<c>audio.fade_{out,in}_</c> + this), for the lanes the official player
    /// was seen publishing; null for noise and the effects, whose keys are open (protocol §6.3).</summary>
    public static string? ConnectKey(MixCurveLane lane) => lane switch
    {
        L.Volume => "curves",
        L.EqLow => "eq_low_gain_curves",
        L.EqMid => "eq_mid_gain_curves",
        L.EqHigh => "eq_high_gain_curves",
        L.FilterCutoff => "filter_cutoff_curves",
        L.FilterResonance => "filter_resonance_curves",
        _ => null,
    };

    // ── segment shapes ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 2-point line <c>(0,y0)(1,y1)</c>.</summary>
    static CurveSegment Line(float start, float end, float y0, float y1) => new(start, end, false, 0, y0, 0, 0, 0, 0, 1, y1);

    /// <summary>A change from <paramref name="from"/> to <paramref name="to"/> over [at, at+len] (an S or a line), held flat
    /// before and after.</summary>
    static int Step(Span<CurveSegment> into, float at, float len, float from, float to, bool s)
    {
        int n = 0;
        if (at > 0) into[n++] = CurveSegment.Flat(0, at, from);
        into[n++] = s ? CurveSegment.S(at, at + len, from, to) : Line(at, at + len, from, to);
        if (at + len < 1) into[n++] = CurveSegment.Flat(at + len, 1, to);
        return n;
    }
}
