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

    // ── the layer view (the lesson's teaching mode: MixTimelineModel.Heard / .Layers); every value has a Fluent default ──

    /// <summary>A song lane's height in the layer view, and a layer lane's (scaled by each lane's share).</summary>
    public float SongHeight { get; init; } = 76f;
    public float LayerHeight { get; init; } = 46f;
    /// <summary>The label column left of the lanes (each lane's name and readout); 0: the names sit on plates inside the lanes.</summary>
    public float LabelColumn { get; init; }
    /// <summary>The room between two song lanes, between two layer lanes, and between the songs and the layers (the "Layers" caption sits in it).</summary>
    public float SongGap { get; init; } = 4f;
    public float LayerGap { get; init; } = 6f;
    public float SectionGap { get; init; } = 24f;
    /// <summary>The ruler under the lanes.</summary>
    public float RulerHeight { get; init; } = 22f;
    /// <summary>The ink of a layer that is off (what it would leave alone), the bar lines, the overlap's wash, the ruler, the playhead.</summary>
    public ColorF? Muted { get; init; }
    public ColorF? Grid { get; init; }
    public ColorF? Overlap { get; init; }
    public ColorF? Ruler { get; init; }
    public ColorF? Playhead { get; init; }
    /// <summary>A tip's plate (the "Drag here" callout) and its edge.</summary>
    public ColorF? TipFill { get; init; }
    public ColorF? TipStroke { get; init; }
    /// <summary>The handle's plate, its edge, and its size; the hint colour's beacon size.</summary>
    public ColorF? HandlePlate { get; init; }
    public float LayerHandle { get; init; } = 22f;
    public float BeaconSize { get; init; } = 26f;
    /// <summary>Null: round handles, number badges and beacons (Fluent). A value: squares with this corner (a theme that squares its controls).</summary>
    public float? SquareCorner { get; init; }
    /// <summary>The theme's text for a lane's name, its readout and a caption (null: Fluent's body-strong, caption, caption).</summary>
    public Func<string, FluentGpu.Dsl.TextEl>? NameText { get; init; }
    public Func<string, FluentGpu.Dsl.TextEl>? ReadoutText { get; init; }
    public Func<string, FluentGpu.Dsl.TextEl>? CaptionText { get; init; }
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
    /// <summary>The layer view: the plot's side padding, a curve's stroke, the line from a handle to its target (dash, gap), the draw-in of a
    /// layer that turns on and the fade of one that turns off (ms), the waveform's crossfade (ms), a handle's nudge (one every NudgeMs).</summary>
    public const float PlotPad = 10f, LayerStroke = 2.2f, GuideDash = 5f, GuideGap = 5f, DrawInMs = 650f, FadeOutMs = 420f, HeardFadeMs = 180f, NudgeMs = 3000f;
    /// <summary>An outer waveform's alpha (its core is the hue at full), the loudness curve's exponent (quiet parts still read), the echo's alpha.</summary>
    public const float OuterAlpha = 0.5f, Loudness = 0.55f, EchoAlpha = 0.85f;
    /// <summary>A dimmed lane's opacity; a focused lane's ground tint and edge (shares of its hue).</summary>
    public const float DimOpacity = 0.45f, FocusTint = 0.07f, FocusEdge = 0.35f, DimMs = 380f;
    /// <summary>The layer view's small parts: the playhead's width, the gap in a label row and on a plate, a song's swatch and its corner, an echo
    /// ring's stroke, a mark's label box, the tip (its dot's ring, gap, height, side padding, the box it centres in).</summary>
    public const float PlayheadWidth = 2f, LabelGap = 8f, PlateGap = 4f, Swatch = 10f, SwatchCorner = 2f, RingStroke = 1.6f, MarkLabel = 40f,
        TipRing = 2f, TipGap = 6f, TipHeight = 28f, TipPadX = 10f, TipWrap = 180f;
    /// <summary>A lane's number badge (beside its name; on its plate at a narrow width) and its numeral (the type floor's 11).</summary>
    public const float Badge = 18f, BadgeSmall = 16f, BadgeText = 11f;
    /// <summary>The measured-width key of the timeline (one per editor).</summary>
    public const string WidthKey = "mix:timeline";
}
