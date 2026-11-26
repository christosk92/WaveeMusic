// ── Themes/Zune/Structure/Rules/ZuneMixEditorRules.cs — the Mix transition editor's measures and look (Zune, #48 M6) ──────────────────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;

namespace Wavee.Themes.Zune;

/// <summary>Zune's transition editor: a flat band in Zune's voice (the accent bar at its lead, presets as large lowercase words, Customize a second band,
/// commands as words), and the timeline flat on the ground.</summary>
internal static class ZuneMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The lead bar's width and the gap after it; the gaps between the band's rows, inside a row, between preset words, between Customize's rows.</summary>
    public const float BarW = 3f, BarGap = 16f, Gap = 14f, RowGap = 8f, WordGap = 20f, PickerGap = 4f, LineGap = 2f;
    /// <summary>A command word's side padding; a Customize row's label column.</summary>
    public const float WordPadX = 8f, LabelColumn = 112f, Glyph = 14f, GlyphGap = 6f;
    /// <summary>The overlap stepper's value cell (its widest readout reserved).</summary>
    public const float StepperValue = 136f;

    /// <summary>The timeline: lanes, gap, square corners (Zune is flat), the curve's stroke, the handle's dot and hit box, the grip, the label inset.</summary>
    public const float LaneHeight = 64f, LaneGap = 2f, LaneCorner = 0f, Stroke = 2f, HandleSize = 12f, HandleHit = 32f, GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    public const float BandAlpha = 0.22f, WaveAlpha = 0.45f, TickAlpha = 0.3f, DownbeatAlpha = 0.55f, DimAlpha = 0.7f, CutoffAlpha = 0.8f, LaneAlpha = 0.04f;

    /// <summary>Customize's band eases in and out as the app's disclosures do.</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    /// <summary>The timeline in Zune's tokens: flat lanes, the accent band and incoming curve, the primary ink for the outgoing one.</summary>
    public static MixTimelineLook Look(float target) => new()
    {
        LaneHeight = LaneHeight, LaneGap = LaneGap, Corner = LaneCorner,
        LaneFill = Tok.TextPrimary with { A = LaneAlpha }, WaveA = Tok.TextPrimary with { A = WaveAlpha }, WaveB = Tok.TextPrimary with { A = WaveAlpha },
        Tick = Tok.TextPrimary with { A = TickAlpha }, Downbeat = Tok.TextPrimary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentDefault, Cutoff = Tok.TextSecondary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.AccentDefault, HandleRing = Tok.TextPrimary, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = Stroke, HandleSize = HandleSize, HandleHit = MathF.Max(HandleHit, target), GripWidth = MathF.Max(GripWidth, target), LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
