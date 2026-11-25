// ── Themes/Classic/Structure/Rules/ClassicMixEditorRules.cs — the Mix transition editor's measures and look (Classic, #48 M6) ─────────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Classic;

/// <summary>Classic's transition editor: a plate under the Transitions list in the Tune strip's form (a layer with a hairline), the presets as a wrapping chip
/// set, Customize as a disclosure with its pickers in a wrapping grid, and the timeline on the layer.</summary>
internal static class ClassicMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The plate's padding and stroke, the gaps between its rows, inside a row, between chips, between Customize's pickers.</summary>
    public const float Pad = 14f, Stroke = 1f, Gap = 12f, RowGap = 8f, ChipGap = 8f, PickerGap = 12f, PickerLabelGap = 4f;
    /// <summary>A Customize picker's smallest width (the grid wraps below it); the overlap stepper's value cell.</summary>
    public const float PickerMin = 172f, StepperValue = 132f;
    /// <summary>The hyperlink button's own text inset (its words pulled back onto the column they lead).</summary>
    public const float LinkInset = 11f;
    /// <summary>The standard control height and Classic's touch target at Compact.</summary>
    public const float Target = 32f, CompactTarget = 40f;

    /// <summary>The timeline: lanes, gap, corner, the curve's stroke, the handle's dot and hit box, the grip, the label inset.</summary>
    public const float LaneHeight = 56f, LaneGap = 4f, LaneCorner = 6f, CurveStroke = 2f, HandleSize = 12f, HandleHit = 24f, GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    public const float BandAlpha = 0.2f, WaveAlpha = 0.5f, TickAlpha = 0.3f, DownbeatAlpha = 0.5f, DimAlpha = 0.6f, CutoffAlpha = 0.75f;

    /// <summary>Customize's body eases in and out as the app's disclosures do.</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    public static float TargetFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : Target;

    public static MixTimelineLook Look(WidthTier window) => new()
    {
        LaneHeight = LaneHeight, LaneGap = LaneGap, Corner = LaneCorner,
        LaneFill = Tok.FillControlAltSecondary, WaveA = Tok.TextSecondary with { A = WaveAlpha }, WaveB = Tok.TextSecondary with { A = WaveAlpha },
        Tick = Tok.TextSecondary with { A = TickAlpha }, Downbeat = Tok.TextSecondary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentTextPrimary, Cutoff = Tok.TextTertiary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.FillControlSolid, HandleRing = Tok.AccentDefault, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = CurveStroke, HandleSize = HandleSize, HandleHit = window == WidthTier.Compact ? CompactTarget : HandleHit,
        GripWidth = window == WidthTier.Compact ? CompactTarget : GripWidth, LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
