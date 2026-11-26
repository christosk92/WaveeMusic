// ── Themes/WinUi/Structure/Rules/WinUiMixEditorRules.cs — the Mix transition editor's measures and look (WinUI, #48 M6) ─────────────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's transition editor: an inset card under the Transitions list (an expander card's body), its rows, the timeline's look.</summary>
internal static class WinUiMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The card's padding, the gap between its rows, the gaps inside a row, between chips and between Customize's pickers.</summary>
    public const float Pad = 16f, Gap = 12f, RowGap = 8f, ChipGap = 8f, PickerGap = 12f, PickerLabelGap = 4f, LineGap = 2f;
    /// <summary>A Customize picker's smallest width (the pickers wrap below it).</summary>
    public const float PickerMin = 168f;
    /// <summary>The hyperlink button's own text inset (its words pulled back onto the column they lead).</summary>
    public const float LinkInset = 11f;
    /// <summary>The overlap stepper's value cell (its widest readout, "35.0 s · 32 bars", reserved so a step never moves the buttons).</summary>
    public const float StepperValue = 132f;
    /// <summary>Customize's chevron, trailing its label as WinUI's Expander places it: the 12-DIP ChevronDown / ChevronUp glyph in its own box, a
    /// control gap after the words.</summary>
    public const float ChevronBox = 12f, ChevronGlyph = 12f, ChevronGap = 8f;
    /// <summary>The touch target at Compact (WinUI's 40) and the standard control height.</summary>
    public const float CompactTarget = 40f, Target = 32f;

    /// <summary>The timeline: lane height, gap, corner, curve stroke, the handle's dot and hit box, the band grip, the label inset.</summary>
    public const float LaneHeight = 60f, LaneGap = 4f, LaneCorner = 4f, Stroke = 2f, HandleSize = 12f, HandleHit = 24f, GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    /// <summary>The timeline's washes: the band, the waveforms, the ticks, the dimmed stretches, the cutoff.</summary>
    public const float BandAlpha = 0.18f, WaveAlpha = 0.5f, TickAlpha = 0.28f, DownbeatAlpha = 0.5f, DimAlpha = 0.62f, CutoffAlpha = 0.75f;

    /// <summary>Customize's body eases in and out as the app's disclosures do (the layout lands once, the presented height springs).</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    public static float TargetFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : Target;

    /// <summary>The timeline in WinUI's tokens: the accent band and incoming curve, the primary ink for the outgoing curve, the subtle layer under the lanes.</summary>
    public static MixTimelineLook Look(WidthTier window) => new()
    {
        LaneHeight = LaneHeight, LaneGap = LaneGap, Corner = LaneCorner,
        LaneFill = Tok.FillSubtleSecondary, WaveA = Tok.TextSecondary with { A = WaveAlpha }, WaveB = Tok.TextSecondary with { A = WaveAlpha },
        Tick = Tok.TextSecondary with { A = TickAlpha }, Downbeat = Tok.TextSecondary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentDefault, Cutoff = Tok.TextTertiary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.FillControlSolid, HandleRing = Tok.AccentDefault, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = Stroke, HandleSize = HandleSize, HandleHit = window == WidthTier.Compact ? CompactTarget : HandleHit,
        GripWidth = window == WidthTier.Compact ? CompactTarget : GripWidth, LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
