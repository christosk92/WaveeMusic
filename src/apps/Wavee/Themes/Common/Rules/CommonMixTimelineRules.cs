// ── Themes/Common/Rules/CommonMixTimelineRules.cs — the Mix editor's shared timeline: its look record and fixed numbers (#48 M6) ───────
//
// Each theme draws the timeline (Themes/Common/CommonMixTimeline.cs) in its own look: it builds a `MixTimelineLook` in its template from its own
// tokens and Rules. The numbers below are the timeline's own geometry that no theme changes.

using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>How a theme draws the Mix timeline: the lanes' size and plate, the waveform, the beat ticks, the transition band, the curves and
/// their handles, the dimmed stretches (A after its end, B before its in-point) and the lane labels.</summary>
public sealed record MixTimelineLook
{
    public required float LaneHeight { get; init; }
    public required float LaneGap { get; init; }
    /// <summary>The room between the two lanes, where the theme's Hear it button sits (the band runs through it).</summary>
    public required float CentreGap { get; init; }
    public required float Corner { get; init; }
    public required ColorF LaneFill { get; init; }
    public required ColorF WaveA { get; init; }
    public required ColorF WaveB { get; init; }
    public required ColorF Tick { get; init; }
    public required ColorF Downbeat { get; init; }
    public required ColorF Band { get; init; }
    public required ColorF BandEdge { get; init; }
    public required ColorF CurveOut { get; init; }
    public required ColorF CurveIn { get; init; }
    public required ColorF Cutoff { get; init; }
    public required ColorF Dim { get; init; }
    public required ColorF Handle { get; init; }
    public required ColorF HandleRing { get; init; }
    public required ColorF Label { get; init; }
    /// <summary>The plate under a lane's label, so it reads over the waveform.</summary>
    public required ColorF LabelPlate { get; init; }
    /// <summary>The curve's stroke and the band edge's width.</summary>
    public required float Stroke { get; init; }
    public required float HandleSize { get; init; }
    /// <summary>The handle's hit box (the visible dot sits in its middle): at least the touch target at Compact.</summary>
    public required float HandleHit { get; init; }
    /// <summary>The band's end grip: its hit width (the visible edge is <see cref="Stroke"/> wide inside it).</summary>
    public required float GripWidth { get; init; }
    public required float LabelInset { get; init; }
    public required float LabelPadX { get; init; }
}

public static class CommonMixTimelineRules
{
    /// <summary>The span the view shows around the transition: the overlap times this, at least <see cref="MinSpanMs"/>.</summary>
    public const float SpanPerOverlap = 2.6f;
    public const float MinSpanMs = 12_000f, MaxSpanMs = 90_000f;
    /// <summary>Where the transition band starts, as a share of the view (the outgoing song's lead-in on its left).</summary>
    public const float BandLead = 0.38f;
    /// <summary>The waveform's column step (DIP) and its share of the lane's half height.</summary>
    public const float WaveStep = 5f, WaveDepth = 0.6f;
    /// <summary>How far past each side of the lane the waveform is drawn (a share of the width), a column's stroke as a share of the step, and the
    /// half height under which a column draws nothing.</summary>
    public const float WaveReach = 0.5f, WaveBar = 0.6f, WaveFloor = 0.5f;
    /// <summary>A beat tick's height as a share of the lane (a downbeat runs the full lane).</summary>
    public const float TickShare = 0.22f;
    public const float TickWidth = 1f;
    /// <summary>The curve's inset from the lane's top and bottom.</summary>
    public const float CurveInset = 6f;
    /// <summary>The bend a handle drag of one lane height makes (−1..1 over 2/3 of the lane).</summary>
    public const float BendPerLane = 1.5f;
    /// <summary>A dragged bend is previewed in these steps (a re-sample per step, not per pointer move).</summary>
    public const float BendStep = 1f / 64f;
    /// <summary>The full-page overview strip: its height as a share of a lane, and its waveform's column step.</summary>
    public const float OverviewShare = 0.75f, OverviewStep = 3f;
    /// <summary>The list's timeline drawing itself in (Mix turned on): each segment's draw and the chip's arrival, in ms.</summary>
    public const float SeamDrawMs = 167f, SeamChipMs = 250f;
    /// <summary>The measured-width key of the timeline (one per editor).</summary>
    public const string WidthKey = "mix:timeline";
}
