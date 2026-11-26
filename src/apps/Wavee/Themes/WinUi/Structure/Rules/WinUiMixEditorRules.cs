// ── Themes/WinUi/Structure/Rules/WinUiMixEditorRules.cs — the Mix transition editor's and chips' measures and look (WinUI, #48) ───────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's transition editor (the right pane's "Edit transition", and the same editor full-page) and the transition chips in a mixed
/// playlist's list.</summary>
internal static class WinUiMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The pane's padding, the gap between its rows, the gaps inside a row, between chips, between the ingredient cards.</summary>
    public const float Pad = 16f, Gap = 12f, RowGap = 8f, ChipGap = 8f, PickerGap = 4f, LineGap = 2f;
    /// <summary>The head's height (the pane's tab row's 48) and the song lines' cover.</summary>
    public const float HeadH = 48f, Cover = 32f, CoverGap = 12f;
    /// <summary>The key's plate: its side padding and corner.</summary>
    public const float KeyPadX = 6f, KeyCorner = 4f;
    /// <summary>An ingredient card (a SettingsCard's row): its least height and side padding; the full page lays them out in this many columns.</summary>
    public const float CardMinH = 48f, CardPadX = 16f;
    public const int PageColumns = 3;
    /// <summary>The overlap stepper's value cell (its widest readout, "35.0 s · 32 bars", reserved so a step never moves the buttons).</summary>
    public const float StepperValue = 132f;
    /// <summary>The touch target at Compact (WinUI's 40) and the standard control height.</summary>
    public const float CompactTarget = 40f, Target = 32f;

    /// <summary>The timeline: lane height (the pane's and the page's), gap, the room between the lanes (the Hear it button), corner, curve stroke,
    /// the handle's dot and hit box, the band grip, the label inset.</summary>
    public const float LaneHeight = 60f, PageLaneHeight = 112f, LaneGap = 0f, CentreGap = 44f, LaneCorner = 4f, Stroke = 2f, HandleSize = 12f, HandleHit = 24f,
        GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    /// <summary>The timeline's washes: the band, the waveforms, the ticks, the dimmed stretches, the cutoff.</summary>
    public const float BandAlpha = 0.18f, WaveAlpha = 0.5f, TickAlpha = 0.28f, DownbeatAlpha = 0.5f, DimAlpha = 0.62f, CutoffAlpha = 0.75f;

    // ── the chip between two rows ──

    /// <summary>The chip: its height, side padding, glyph, the gap after the glyph; the connector's stroke.</summary>
    public const float ChipH = 24f, ChipPadX = 8f, ChipGlyph = 12f, ChipGlyphGap = 6f, ChipEnterScale = 0.9f;
    /// <summary>The list's timeline: its stroke, and its inks (quiet; the accent where the pair is edited).</summary>
    public const float LineStroke = 1.5f, LineAlpha = 0.3f, LineEditedAlpha = 0.7f;
    public static ColorF Line => Tok.TextSecondary with { A = LineAlpha };
    public static ColorF LineEdited => Tok.AccentDefault with { A = LineEditedAlpha };

    /// <summary>The lane spans the row's slot, the core's indents are the row skin's: WinUI's row skin draws its artwork 2 DIP left of the core's art-centre indent, so the timeline lands on the artwork's centre
    /// and the chip on its left edge (measured in the inspector, every tier).</summary>
    public const float SeamInset = -2f;

    /// <summary>Disclosures ease in and out as the app's do (the layout lands once, the presented height springs).</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    public static float TargetFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : Target;

    /// <summary>The timeline in WinUI's tokens: the accent band and incoming curve, the primary ink for the outgoing curve, the subtle layer under the lanes.</summary>
    public static MixTimelineLook Look(WidthTier window, bool page) => new()
    {
        LaneHeight = page ? PageLaneHeight : LaneHeight, LaneGap = LaneGap, CentreGap = CentreGap, Corner = LaneCorner,
        LaneFill = Tok.FillSubtleSecondary, WaveA = Tok.TextSecondary with { A = WaveAlpha }, WaveB = Tok.TextSecondary with { A = WaveAlpha },
        Tick = Tok.TextSecondary with { A = TickAlpha }, Downbeat = Tok.TextSecondary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentDefault, Cutoff = Tok.TextTertiary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.FillControlSolid, HandleRing = Tok.AccentDefault, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = Stroke, HandleSize = HandleSize, HandleHit = window == WidthTier.Compact ? CompactTarget : HandleHit,
        GripWidth = window == WidthTier.Compact ? CompactTarget : GripWidth, LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
