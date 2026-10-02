// ── Platform/EqCurve.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────
// The equalizer curve's GEOMETRY and its pure RULES: where band i sits for a measured surface, the gain <-> y mapping the
// painter and the pointer share, which axis labels fit, where the value badge goes and which band it names.
//
// Role: CORE
// Owner: L
// Wave: 4
// Budget: 220 lines
// Spec: DERIVED; ch 27 W8 (the curve's geometry), §0 N10 (the curve)
//
// ── WHY THIS FILE IS PURE ────────────────────────────────────────────────────────────────────────────────────────────
//
// The curve (`Controls.EqualizerCurve`, Controls.Picker.cs) draws every node, gridline, label, spline sample and the
// badge through THESE functions, and maps a pointer back through the same ones. So "the label sits under its node",
// "the curve and the nodes share one x mapping", "the badge never leaves the plot" and "a drag reads the size it is
// handed" are facts in EqCurveRulesTests, not properties of a render nobody can run headless. No engine type is named.

namespace Wavee;

/// <summary>The curve's layout for ONE surface size. <see cref="Width"/> is the MEASURED width of the card lane (the curve
/// never imposes a width of its own), <see cref="Height"/> the surface height. Every x the curve draws is
/// <see cref="X"/> and every y is <see cref="Y"/> — nodes, gridlines, labels, spline, fill and badge alike.</summary>
public readonly record struct EqCurveGeometry(float Width, float Height)
{
    public const int BandCount = 10;
    public const int LastBand = BandCount - 1;
    public const float MinGain = -12f, MaxGain = 12f;

    /// <summary>The plot's insets inside the surface: the left gutter carries the dB labels, the bottom the band labels.</summary>
    public const float PadLeft = 40f, PadRight = 16f, PadTop = 18f, PadBottom = 34f;

    public const float HeightPerWidth = 0.38f, MinAutoHeight = 252f, MaxAutoHeight = 360f;
    /// <summary>The width the FIRST frame's height is seeded from, before layout has measured the lane.</summary>
    public const float SeedWidth = 720f;

    /// <summary>A band label's box: centred on its gridline, the text centred inside it. Twice <see cref="PadRight"/>, so
    /// the 16k label's box ends exactly at the surface edge.</summary>
    public const float BandLabelWidth = 32f, LabelHeight = 14f, BandLabelRise = 24f;
    /// <summary>The gap between a dB label's right edge and the plot origin.</summary>
    public const float GainLabelGap = 6f;

    public const float BadgeWidth = 104f, BadgeHeight = 28f;
    /// <summary>Node centre to the badge's near edge: the hot node's radius (9) plus a 5-DIP breath.</summary>
    public const float BadgeClearance = 14f;
    public const float EdgeInset = 6f;

    /// <summary>A sanitized geometry: a non-finite or non-positive width is 0 (nothing to plot); a null or non-positive
    /// <paramref name="height"/> takes <see cref="AutoHeight"/>.</summary>
    public static EqCurveGeometry For(float width, float? height = null)
    {
        float w = float.IsFinite(width) && width > 0f ? width : 0f;
        float h = height is { } fixedH && float.IsFinite(fixedH) && fixedH > 0f ? fixedH : AutoHeight(w);
        return new EqCurveGeometry(w, h);
    }

    /// <summary>The width-derived height: 38 % of the width, held inside [252, 360].</summary>
    public static float AutoHeight(float width) => Math.Clamp(width * HeightPerWidth, MinAutoHeight, MaxAutoHeight);

    public float PlotLeft => PadLeft;
    public float PlotTop => PadTop;
    public float PlotWidth => MathF.Max(0f, Width - PadLeft - PadRight);
    public float PlotHeight => MathF.Max(0f, Height - PadTop - PadBottom);
    public float PlotRight => PlotLeft + PlotWidth;
    public float PlotBottom => PlotTop + PlotHeight;
    /// <summary>The distance between two neighbouring bands' gridlines.</summary>
    public float BandPitch => PlotWidth / LastBand;

    /// <summary>THE x mapping: the spline parameter <paramref name="u"/> ∈ [0, 9] (band i is u = i) to surface x.</summary>
    public float X(float u) => PlotLeft + Math.Clamp(u, 0f, LastBand) / LastBand * PlotWidth;

    public float BandX(int band) => X(band);

    /// <summary>THE y mapping: +12 dB is the plot's top edge, −12 its bottom, out-of-range gains clamp.</summary>
    public float Y(float gain)
        => PlotTop + (MaxGain - Math.Clamp(gain, MinGain, MaxGain)) / (MaxGain - MinGain) * PlotHeight;

    public float ZeroY => Y(0f);

    /// <summary>The inverse of <see cref="Y"/>, clamped to the gain range (a drag above the plot is +12).</summary>
    public float GainAt(float y)
        => PlotHeight <= 0f || !float.IsFinite(y)
            ? 0f
            : MaxGain - Math.Clamp((y - PlotTop) / PlotHeight, 0f, 1f) * (MaxGain - MinGain);

    /// <summary><see cref="GainAt"/> quantized to half a decibel: a drag reports every pointer sample, and the ear cannot
    /// tell 1.47 dB from 1.5.</summary>
    public float SnappedGainAt(float y) => MathF.Round(GainAt(y) * 2f) * 0.5f;

    /// <summary>The band whose gridline is nearest to <paramref name="x"/>, clamped to the band range.</summary>
    public int NearestBand(float x)
        => PlotWidth <= 0f || !float.IsFinite(x)
            ? 0
            : Math.Clamp((int)MathF.Round(Math.Clamp((x - PlotLeft) / PlotWidth, 0f, 1f) * LastBand), 0, LastBand);

    /// <summary>Every band is labelled while two neighbouring label boxes cannot touch; below that every THIRD band (31,
    /// 250, 2k, 16k — the stride that divides the nine intervals evenly), and on a sliver only the two ends. The last
    /// band is labelled at every width: the right edge is what tells the user the axis is complete.</summary>
    public int BandLabelStride
        => BandPitch >= BandLabelWidth ? 1 : BandPitch * 3f >= BandLabelWidth ? 3 : LastBand;

    public bool ShowsBandLabel(int band) => (uint)band < BandCount && band % BandLabelStride == 0;

    /// <summary>The label box's left edge: centred on the band's gridline, held inside the surface.</summary>
    public float BandLabelLeft(int band)
        => Math.Clamp(BandX(band) - BandLabelWidth * 0.5f, 0f, MathF.Max(0f, Width - BandLabelWidth));

    public float BandLabelTop => Height - BandLabelRise;

    /// <summary>The dB labels' gutter: from the surface's left edge to <see cref="GainLabelGap"/> short of the plot origin
    /// (the text is right-aligned in it, so every label ends at the same x).</summary>
    public float GainLabelWidth => MathF.Max(0f, PlotLeft - GainLabelGap);

    /// <summary>A dB label's box top: centred on its rung's line.</summary>
    public float GainLabelTop(float gain) => Y(gain) - LabelHeight * 0.5f;

    /// <summary>True when the badge for a node at <paramref name="gain"/> cannot sit ABOVE it without leaving the
    /// surface's top edge, so it goes below the node instead.</summary>
    public bool BadgeBelow(float gain) => Y(gain) - BadgeClearance - BadgeHeight < EdgeInset;

    /// <summary>The badge's top-left: centred above the band's node (below it near the top edge), then clamped so it
    /// never leaves the plot horizontally — a band-0 or band-9 badge slides inward instead of hanging off the card — and
    /// never drops into the band labels. A plot narrower than the badge falls back to the surface's inset.</summary>
    public (float X, float Y) BadgeAt(int band, float gain)
    {
        float lo = PlotLeft, hi = PlotRight - BadgeWidth;
        if (hi < lo) { lo = EdgeInset; hi = Width - BadgeWidth - EdgeInset; }
        float x = BandX(Math.Clamp(band, 0, LastBand)) - BadgeWidth * 0.5f;
        x = hi >= lo ? Math.Clamp(x, lo, hi) : lo;

        float node = Y(gain);
        float y = BadgeBelow(gain) ? node + BadgeClearance : node - BadgeClearance - BadgeHeight;
        return (x, Math.Clamp(y, EdgeInset, MathF.Max(EdgeInset, PlotBottom - BadgeHeight)));
    }
}

/// <summary>The curve's state rules and its spline — everything that is not a coordinate.</summary>
public static class EqCurveRules
{
    /// <summary>The band the keyboard starts on (1k): the middle of the range.</summary>
    public const int DefaultActiveBand = 5;

    /// <summary>The five dB rungs the grid draws, top to bottom, and their labels.</summary>
    public static readonly float[] GainRungs = [12f, 6f, 0f, -6f, -12f];
    public static readonly string[] GainRungLabels = ["+12", "+6", "0 dB", "-6", "-12"];

    /// <summary>Which band the value badge names: the band under the pointer (a hover, or a press/drag, which also writes
    /// the hover) wins; with no pointer on the curve it rests on the ACTIVE band — the keyboard's target, the band the
    /// description's "use arrow keys on the active band" refers to, and the one node that stays hot at rest. A disabled
    /// curve has no hot node, so it has no badge either. −1 = no badge.</summary>
    public static int BadgeBand(int hover, int active, bool enabled)
        => !enabled ? -1
            : (uint)hover < EqCurveGeometry.BandCount ? hover
            : (uint)active < EqCurveGeometry.BandCount ? active
            : -1;

    /// <summary>A node is drawn HOT (bigger, lifted) while it is the active band or the hovered one — never when disabled.</summary>
    public static bool IsHot(int band, int hover, int active, bool enabled) => enabled && (band == active || band == hover);

    /// <summary>The band's gain, clamped to ±12; a missing band or a non-finite value reads 0 dB.</summary>
    public static float GainAt(ReadOnlySpan<float> gains, int band)
    {
        if ((uint)band >= EqCurveGeometry.BandCount || band >= gains.Length) return 0f;
        float g = gains[band];
        return float.IsFinite(g) ? Math.Clamp(g, EqCurveGeometry.MinGain, EqCurveGeometry.MaxGain) : 0f;
    }

    /// <summary>A CATMULL-ROM sample at <paramref name="u"/> ∈ [0, 9], clamped to the gain range. It passes THROUGH every
    /// node (u = i returns band i's gain), which is why the curve and the nodes can share one x mapping. A straight
    /// polyline would read as hinges rather than as a filter response.</summary>
    public static float Sample(ReadOnlySpan<float> gains, float u)
    {
        if (!(u > 0f)) return GainAt(gains, 0);
        if (u >= EqCurveGeometry.LastBand) return GainAt(gains, EqCurveGeometry.LastBand);
        int i = Math.Clamp((int)MathF.Floor(u), 0, EqCurveGeometry.LastBand - 1);
        float t = u - i;
        float p0 = GainAt(gains, Math.Max(0, i - 1));
        float p1 = GainAt(gains, i);
        float p2 = GainAt(gains, i + 1);
        float p3 = GainAt(gains, Math.Min(EqCurveGeometry.LastBand, i + 2));
        float t2 = t * t, t3 = t2 * t;
        return Math.Clamp(
            0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3),
            EqCurveGeometry.MinGain, EqCurveGeometry.MaxGain);
    }

    // One string per half-decibel step in [−12, +12], built on first use: the badge re-reads it on every drag sample.
    static readonly string?[] s_dbText = new string?[(int)((EqCurveGeometry.MaxGain - EqCurveGeometry.MinGain) * 2f) + 1];

    /// <summary>The badge's value: "+2 dB", "-1.5 dB", and "0 dB" with no sign. Half-decibel values are cached.</summary>
    public static string DbText(float gain)
    {
        if (!float.IsFinite(gain)) gain = 0f;
        float steps = (gain - EqCurveGeometry.MinGain) * 2f;
        int idx = (int)MathF.Round(steps);
        if ((uint)idx < (uint)s_dbText.Length && MathF.Abs(steps - idx) < 1e-3f)
            return s_dbText[idx] ??= Format(EqCurveGeometry.MinGain + idx * 0.5f);
        return Format(gain);

        static string Format(float g) => g.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
    }
}
