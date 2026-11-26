// ── Themes/Classic/Structure/Rules/ClassicMixEditorRules.cs — the Mix transition editor's and chips' measures and look (Classic, #48) ─────────────

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Classic;

/// <summary>Classic's transition editor (the right panel's "Edit transition" under Classic's panel header, and the same editor full-page): the presets
/// as a wrapping chip set, the ingredients as a wrapping grid of pickers, the timeline on the control layer; and the transition chips in the list.</summary>
internal static class ClassicMixEditorRules
{
    public const string Key = "mix:ed";
    /// <summary>The panel's padding (the full page's), the head's height, the hairlines' stroke, the gaps between rows, inside a row, between chips,
    /// between the pickers.</summary>
    public const float Pad = 14f, PagePad = 24f, HeadH = 48f, Stroke = 1f, Gap = 12f, RowGap = 8f, LineGap = 2f, ChipGap = 8f, PickerGap = 12f, PickerLabelGap = 4f;
    /// <summary>A picker's smallest width (the grid wraps below it); the overlap stepper's value cell.</summary>
    public const float PickerMin = 172f, StepperValue = 132f;
    /// <summary>The hyperlink button's own text inset (its words pulled back onto the column they lead).</summary>
    public const float LinkInset = 11f;
    /// <summary>The songs' cover, a glyph, the key's side padding.</summary>
    public const float Cover = 32f, Glyph = 12f, KeyPadX = 6f;
    /// <summary>The standard control height and Classic's touch target at Compact.</summary>
    public const float Target = 32f, CompactTarget = 40f;

    /// <summary>The timeline: lanes (the panel's and the page's), the room between them, the gap, corner, the curve's stroke, the handle's dot and hit
    /// box, the grip, the label inset.</summary>
    public const float LaneHeight = 56f, PageLaneHeight = 104f, CentreGap = 44f, LaneGap = 0f, LaneCorner = 6f, CurveStroke = 2f, HandleSize = 12f, HandleHit = 24f,
        GripWidth = 16f, LabelInset = 8f, LabelPadX = 4f, PlateAlpha = 0.72f;
    public const float BandAlpha = 0.2f, WaveAlpha = 0.5f, TickAlpha = 0.3f, DownbeatAlpha = 0.5f, DimAlpha = 0.6f, CutoffAlpha = 0.75f;

    /// <summary>The chip between two rows: its height, side padding, glyph and the gap after it; the connector's stroke.</summary>
    public const float ChipH = 24f, ChipPadX = 8f, ChipGlyph = 12f, ChipGlyphGap = 6f, ChipEnterRise = 4f;
    /// <summary>The list's timeline: Classic's hairline, its inks (quiet; the accent where the pair is edited).</summary>
    public const float LineStroke = 1f, LineAlpha = 0.32f, LineEditedAlpha = 0.75f;
    public static ColorF Line => Tok.TextSecondary with { A = LineAlpha };
    public static ColorF LineEdited => Tok.AccentDefault with { A = LineEditedAlpha };

    /// <summary>The lane spans the row's slot, the core's indents are the row skin's: Classic's row skin sits a row inset (8) inside the slot the lane spans, so the timeline lands on the artwork's centre
    /// and the chip on its left edge (measured in the inspector, every tier).</summary>
    public const float SeamInset = 8f;

    /// <summary>Disclosures ease in and out as the app's do.</summary>
    public static LayoutTransition Reveal => Design.Reveal.Drawer;

    public static float TargetFor(WidthTier window) => window == WidthTier.Compact ? CompactTarget : Target;

    // ── the Mix lesson: the editor's teaching mode and the lesson's page (Themes/Common/CommonMixLesson.cs; Classic = WinUI's family) ──

    /// <summary>The teaching editor's layer view: the label column (0 below Standard), song and layer lanes (wide, narrow), the gaps; the editor
    /// card's padding and head; the status chip.</summary>
    public const float TeachLabelColumn = 132f, TeachSongH = 76f, TeachLayerH = 46f, TeachNarrowSongH = 52f, TeachNarrowLayerH = 34f,
        TeachSectionGap = 24f, TeachNarrowSectionGap = 10f, TeachLayerGap = 6f, TeachNarrowLayerGap = 4f, TeachPad = 16f, TeachNarrowPad = 10f,
        TeachHeadH = 52f, TeachChipH = 28f, TeachChipPadX = 10f, TeachMutedAlpha = 0.28f;
    /// <summary>The lesson page: the gap between the editor and the guide, the guide's width beside the editor (Standard, and wider).</summary>
    public const float LessonColumnGap = 28f, LessonGuide = 340f, LessonGuideWide = 392f;

    public static bool LessonStacked(WidthTier window) => window < WidthTier.Standard;

    /// <summary>The layer view in the lesson: its form follows the CONTENT tier (the page's own stacked or side-by-side form); the handle's hit box
    /// is the touch target at every tier (it never resizes on a crossing).</summary>
    public static MixTimelineLook TeachLook(WidthTier window, WidthTier content)
    {
        bool narrow = LessonStacked(content);
        return Look(window, page: true) with
        {
            LabelColumn = narrow ? 0f : TeachLabelColumn, SongHeight = narrow ? TeachNarrowSongH : TeachSongH, LayerHeight = narrow ? TeachNarrowLayerH : TeachLayerH,
            SectionGap = narrow ? TeachNarrowSectionGap : TeachSectionGap, LayerGap = narrow ? TeachNarrowLayerGap : TeachLayerGap,
            Muted = Tok.TextPrimary with { A = TeachMutedAlpha }, TipFill = Tok.FillSolidTertiary, TipStroke = Tok.StrokeCardDefault, HandlePlate = Tok.FillControlSolid,
            HandleHit = CompactTarget,
            NameText = static t => Ui.BodyStrong(t), ReadoutText = static t => Ui.Caption(t), CaptionText = static t => Ui.Caption(t),
        };
    }

    public static MixTeachLook Teach(WidthTier content) => new()
    {
        Card = Tok.FillCardDefault, CardStroke = Tok.StrokeCardDefault, Corner = Radii.Card, Pad = LessonStacked(content) ? TeachNarrowPad : TeachPad,
        HeadHeight = TeachHeadH, HeadRule = Tok.StrokeDividerDefault, ShowPair = !LessonStacked(content),
        Title = static t => R.Type.RailHeader(t), Meta = static t => Ui.Caption(t) with { Color = Tok.TextSecondary },
        ChipFill = Tok.FillControlAltSecondary, ChipStroke = Tok.StrokeCardDefault, ChipCorner = Radii.Control, ChipHeight = TeachChipH, ChipPadX = TeachChipPadX,
    };

    public static FluentLessonParts Lesson(WidthTier window) => new(PagePad, LessonColumnGap, window >= WidthTier.Standard ? LessonGuideWide : LessonGuide, Gap,
        Tok.FillCardDefault, Tok.StrokeCardDefault, Radii.Card, Tok.FillControlAltSecondary, Tok.TextDisabled, Tok.FillControlStrong,
        ClassicTierMotion.SwapFade, ClassicTierMotion.SwapFadeMotion);

    public static MixTimelineLook Look(WidthTier window, bool page) => new()
    {
        LaneHeight = page ? PageLaneHeight : LaneHeight, LaneGap = LaneGap, CentreGap = CentreGap, Corner = LaneCorner,
        LaneFill = Tok.FillControlAltSecondary, WaveA = Tok.TextSecondary with { A = WaveAlpha }, WaveB = Tok.TextSecondary with { A = WaveAlpha },
        Tick = Tok.TextSecondary with { A = TickAlpha }, Downbeat = Tok.TextSecondary with { A = DownbeatAlpha },
        Band = Tok.AccentDefault with { A = BandAlpha }, BandEdge = Tok.AccentDefault,
        CurveOut = Tok.TextPrimary, CurveIn = Tok.AccentTextPrimary, Cutoff = Tok.TextTertiary with { A = CutoffAlpha },
        Dim = Tok.FillSolidBase with { A = DimAlpha }, Handle = Tok.FillControlSolid, HandleRing = Tok.AccentDefault, Label = Tok.TextSecondary, LabelPlate = Tok.FillSolidBase with { A = PlateAlpha },
        Stroke = CurveStroke, HandleSize = HandleSize, HandleHit = window == WidthTier.Compact ? CompactTarget : HandleHit,
        GripWidth = window == WidthTier.Compact ? CompactTarget : GripWidth, LabelInset = LabelInset, LabelPadX = LabelPadX,
    };
}
