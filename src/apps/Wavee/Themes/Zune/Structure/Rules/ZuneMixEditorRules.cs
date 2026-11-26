// ── Themes/Zune/Structure/Rules/ZuneMixEditorRules.cs — the Mix transition editor's and chips' measures and look (Zune, #48) ──────────────────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;

namespace Wavee.Themes.Zune;

/// <summary>Zune's transition editor (the right panel's, and full-page): flat, in Zune's voice (the heading, presets as large lowercase words, the
/// ingredients as label rows, commands as words), the timeline flat on the ground; and the flat transition chips in the list.</summary>
internal static class ZuneMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The panel's padding, the head's height, the gaps between the rows, inside a row, between preset words, between ingredient rows.</summary>
    public const float Pad = 16f, HeadH = 56f, Gap = 14f, RowGap = 8f, WordGap = 20f, PickerGap = 4f, LineGap = 2f;
    /// <summary>A command word's side padding; an ingredient row's label column (and its column's least width full-page), a glyph and its gap.</summary>
    public const float WordPadX = 8f, LabelColumn = 112f, PageColumn = 360f, Glyph = 14f, GlyphGap = 6f;
    /// <summary>The songs' square cover.</summary>
    public const float Cover = 36f;
    /// <summary>The overlap stepper's value cell (its widest readout reserved).</summary>
    public const float StepperValue = 136f;

    /// <summary>The timeline: lanes (the panel's and the page's), the room between them, gap, square corners (Zune is flat), the curve's stroke, the
    /// handle's dot and hit box, the grip, the label inset.</summary>
    public const float LaneHeight = 64f, PageLaneHeight = 120f, CentreGap = 44f, LaneGap = 0f, LaneCorner = 0f, Stroke = 2f, HandleSize = 12f, HandleHit = 32f,
        GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    public const float BandAlpha = 0.22f, WaveAlpha = 0.45f, TickAlpha = 0.3f, DownbeatAlpha = 0.55f, DimAlpha = 0.7f, CutoffAlpha = 0.8f, LaneAlpha = 0.04f;

    /// <summary>The chip between two rows: its height, side padding, glyph and the gap after it; the connector's stroke and ink.</summary>
    public const float ChipH = 24f, ChipPadX = 8f, ChipGlyph = 12f, ChipGlyphGap = 6f, CompactTarget = 40f, ChipEnterSlide = 16f;
    /// <summary>The list's timeline: its stroke and inks (the primary ink, receded; the accent itself where the pair is edited).</summary>
    public const float LineStroke = 2f, LineAlpha = 0.2f;
    public static ColorF Line => Tok.TextPrimary with { A = LineAlpha };
    public static ColorF LineEdited => Tok.AccentDefault;

    /// <summary>The lane spans the row's slot, the core's indents are the row skin's: Zune's row skin sits a row inset (8) inside the slot the lane spans, so the timeline lands on the artwork's centre
    /// and the chip on its left edge (measured in the inspector, every tier).</summary>
    public const float SeamInset = 8f;

    /// <summary>Disclosures ease in and out as the app's do.</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    /// <summary>The timeline in Zune's tokens: flat lanes, the accent band and incoming curve, the primary ink for the outgoing one.</summary>
    public static MixTimelineLook Look(float target, bool page) => new()
    {
        LaneHeight = page ? PageLaneHeight : LaneHeight, LaneGap = LaneGap, CentreGap = MathF.Max(CentreGap, target), Corner = LaneCorner,
        LaneFill = Tok.TextPrimary with { A = LaneAlpha }, WaveA = Tok.TextPrimary with { A = WaveAlpha }, WaveB = Tok.TextPrimary with { A = WaveAlpha },
        Tick = Tok.TextPrimary with { A = TickAlpha }, Downbeat = Tok.TextPrimary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentDefault, Cutoff = Tok.TextSecondary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.AccentDefault, HandleRing = Tok.TextPrimary, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = Stroke, HandleSize = HandleSize, HandleHit = MathF.Max(HandleHit, target), GripWidth = MathF.Max(GripWidth, target), LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
