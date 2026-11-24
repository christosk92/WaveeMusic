// ── Themes/Zune/Structure/Rules/ZuneSettingsRules.cs — the numbers and the tier records of Zune's Settings page (themes plan 6.3, P5 Z8b) ───────
//
// Everything the Settings views (`Views\Screens\ZuneSettings*.cs`) size beside the pictures they draw: the row grammar (the label column, the control column, the gaps), the
// controls' widths, the section list and its "find a setting" pill, the home's summaries, the search results, the arrival's highlight, the "see it" clip, the picker's tiles,
// the options' preview, the Storage census hues and the Playback video manager. Data, not decisions (WVTH003 allows literals only in `Rules\**`).
//
// ONE RECORD PER CONTENT TIER (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows). A view reads `ZuneSettingsTiers.Of(ctx.Tier())` and never
// compares a width with a number (WVTH013). Standard follows `Z-Settings-Appearance`, `Z-Settings-Playback`, `Z-Settings-Sections` and `Z-Theme-Options` (the 220 section list, the
// 660 column, the 200 | rest rows, the 150 x 100 theme tiles); ZT8 tuned Compact, Small and Huge against the -Compact, -Small and -Huge boards:
//
//   tier       title               find a setting   section list (word)                          column      rows             picker tiles (gap)                   swatch
//   Compact    64/72 (box 84)      40 tall, r20     the drill-in page: 60-tall 26/32 + a summary   fill        control UNDER    2-up fluid, 120 x 88 + a line (16)   40
//   Small      64/72               32 tall, r16     180 column, 30-tall 18/24                      520         140 | rest       4-up fluid, 118 x 68, name (12)      28
//   Standard   120/130 (box 156)   32 tall, r16     220 column, 40-tall 20/28                      660         200 | rest       150 x 100 + a line (16)              32
//   Huge       150/160 (box 192)   36 tall, r18     260 column, 52-tall 24/32 (40 gap)             760         220 | rest       4-up fluid, 176 x 120 + a line (16)  36
//   Huge 4K    150/160             Huge's           Huge's                                         760         220 | rest       Huge's                               36
//
// The ten words are the Settings home ("settings") and the nine categories (settings rework, owner decision 1); a long word wraps inside its target, never shrinks.
// The drill-in names keep the old sections page's 26/32 (the concept's Z-Compact draws 22/28; the display voice never shrinks, owner rule).
// The pill sits above the list (under the title at Compact); a search result is the board's two-line link, a tier's padding round it (`ResultPadY`).
//
// A short choice is a row of chips at every tier: the engine's segmented control, and at Compact (the board's wrapping 40-tall chips) a wrapping row of buttons that tall.
//
// The page gutter is the frame's (`ZuneTiers.Frame`: 16 / 24 / 48 / 48 / 64). TOUCH: `Target` is the Compact hit target (40); the record never decides what a control is.
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>What the Settings page lays out in one content tier.</summary>
public sealed record ZuneSettingsLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The page gutter (the frame's for the tier).</summary>
    public required float Gutter { get; init; }

    /// <summary>The hit target of the page's controls (Compact 40; 0 keeps each control's own size).</summary>
    public required float Target { get; init; }

    /// <summary>The title is the compact step of the panorama (64) instead of the panorama (120).</summary>
    public required bool CompactTitle { get; init; }

    /// <summary>The title box's height (the larger size's line, reserved).</summary>
    public required float TitleH { get; init; }

    /// <summary>The panorama's top relative to its box (the board sets it at -6, Huge -12): the line box is exactly the stated line height.</summary>
    public required float TitleTop { get; init; }

    /// <summary>The panorama's size and line where the tier sets one beyond the role's two steps (Huge: the board's 150 / 160); 0 keeps the role's.</summary>
    public required float TitleSize { get; init; }

    /// <summary>The line height that goes with <see cref="TitleSize"/>.</summary>
    public required float TitleLineH { get; init; }

    /// <summary>A section word's size and line (the display voice: 18/24 at Small, 20/28, 24/32 at Huge).</summary>
    public required float NavSize { get; init; }

    /// <summary>The line height that goes with <see cref="NavSize"/>.</summary>
    public required float NavLineH { get; init; }

    /// <summary>A colour swatch's edge (40 at a hit target, 28 / 32 / 36 up the desktop tiers).</summary>
    public required float SwatchSize { get; init; }

    /// <summary>The gap between picker tiles.</summary>
    public required float PickerGap { get; init; }

    /// <summary>The sections are the Compact drill-in page (a list of 60-tall names with their summaries, a section opening as its own page) instead
    /// of a column of words beside the lane.</summary>
    public required bool ListIsStrip { get; init; }

    /// <summary>The section list's width (0 where it is the drill-in page, which fills the page).</summary>
    public required float ListW { get; init; }

    /// <summary>A section word's least height (the 60-tall drill-in row at Compact, 30 at Small, 40 at Standard, 52 at Huge).</summary>
    public required float ListItemH { get; init; }

    /// <summary>The "find a setting" pill's height (40 at Compact, 32, 36 at Huge); its corner is half of it (a full round end: r20, r16, r18).</summary>
    public required float SearchH { get; init; }

    /// <inheritdoc cref="SearchH"/>
    public required float SearchR { get; init; }

    /// <summary>The air above the pill (Compact: under the title) and below it (to the list's first word).</summary>
    public required float SearchAbove { get; init; }

    /// <inheritdoc cref="SearchAbove"/>
    public required float SearchBelow { get; init; }

    /// <summary>A search result's padding above and below its two lines (the board's 8; 10 at Compact).</summary>
    public required float ResultPadY { get; init; }

    /// <summary>The lines a Compact drill-in row's summary may wrap to (0 where the list has no summaries).</summary>
    public required int SummaryLines { get; init; }

    /// <summary>The gap between the list and the column.</summary>
    public required float BodyGap { get; init; }

    /// <summary>The open section's column width (its basis and its cap).</summary>
    public required float ColumnW { get; init; }

    /// <summary>The column grows to the lane (Compact fills it) instead of stopping at <see cref="ColumnW"/>.</summary>
    public required bool ColumnGrows { get; init; }

    /// <summary>A row's control sits UNDER its label (full width) instead of beside it.</summary>
    public required bool StacksRows { get; init; }

    /// <summary>A row that does not stack still wraps its control under its label when the column is too narrow for both (Small: the 520 column shrinks to 380 in a 640 window, and a
    /// 220 combo or a slider with its value then no longer fits beside a 140 label).</summary>
    public required bool RowsWrap { get; init; }

    /// <summary>The label column's width where rows do not stack (200).</summary>
    public required float LabelW { get; init; }

    /// <summary>A slider's track length.</summary>
    public required float SliderLength { get; init; }

    /// <summary>The Appearance tab draws the options' preview (beside the column where there is room, under it where there is not).</summary>
    public required bool ShowsAside { get; init; }

    /// <summary>The picker is a grid of <see cref="ZuneSettingsRules.PickerGridColumns"/> tiles to a line, each an equal share of the lane, so the tiles follow the window
    /// (Compact: <see cref="PickerGridTileW"/>); elsewhere the tiles hold <see cref="PickerCardW"/> or grow from it along the line.</summary>
    public required bool PickerGrid { get; init; }

    /// <summary>The picker's tiles grow to share the line (fluid) instead of holding their width.</summary>
    public required bool PickerGrows { get; init; }

    /// <summary>A picker tile's width (its least where it grows, the narrowest share in the grid). A wrapping row sizes its items by their measured width (a flex basis
    /// plays no part there), so the picker gives it as the columns' minimum.</summary>
    public required float PickerCardW { get; init; }

    /// <summary>A picker tile's thumbnail height.</summary>
    public required float PickerThumbH { get; init; }

    /// <summary>The tile carries the theme's line under its name.</summary>
    public required bool PickerShowsText { get; init; }

    /// <summary>The aside's widest: the board's rest of the line beside the 660 column at Standard (368 at 1440), more where the Huge lane has room. The engine breaks a wrapping
    /// row on a child's MAXIMUM, so a larger cap than the room beside the column wraps the aside under it.</summary>
    public float AsideMaxW => Tier >= WidthTier.Huge ? ZuneSettingsRules.AsideMax : ZuneSettingsRules.AsideMaxStandard;

    /// <summary>The page lays its controls out for touch (the Compact rule: every hit target is at least 40).</summary>
    public bool Touch => Target > 0f;

    /// <summary>A grid tile's width in a window <paramref name="viewportWidth"/> wide: the lane (the window less the gutters) less the gaps between the columns, over the
    /// columns, never under <see cref="PickerCardW"/>. NaN outside the grid (the columns size the tiles there).</summary>
    public float PickerGridTileW(float viewportWidth) => PickerGrid
        ? MathF.Max(PickerCardW, MathF.Floor((viewportWidth - 2f * Gutter - (ZuneSettingsRules.PickerGridColumns - 1) * PickerGap) / ZuneSettingsRules.PickerGridColumns))
        : float.NaN;
}

/// <summary>The Settings page's tier records.</summary>
public static class ZuneSettingsTiers
{
    /// <summary>One record. The title is <paramref name="titleH"/> tall (the larger size's line, reserved); <paramref name="titleSize"/> 0 keeps the role's size.</summary>
    private static ZuneSettingsLayout Make(WidthTier tier, float target, bool compactTitle, float titleSize, float titleLineH, float titleH, float titleTop,
        bool strip, float listW, float listItemH, float navSize, float navLineH, float searchH, float searchAbove, float searchBelow, float resultPadY, int summaryLines,
        float bodyGap,
        float columnW, bool columnGrows, bool stacks, bool rowsWrap, float labelW, float sliderLength, bool aside,
        bool pickerGrid, bool pickerGrows, float cardW, float thumbH, float pickerGap, bool pickerText, float swatch) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, Target = target, CompactTitle = compactTitle,
        TitleSize = titleSize, TitleLineH = titleLineH, TitleH = titleH, TitleTop = titleTop,
        ListIsStrip = strip, ListW = listW, ListItemH = listItemH, NavSize = navSize, NavLineH = navLineH,
        SearchH = searchH, SearchR = searchH / 2f, SearchAbove = searchAbove, SearchBelow = searchBelow, ResultPadY = resultPadY, SummaryLines = summaryLines,
        BodyGap = bodyGap, ColumnW = columnW, ColumnGrows = columnGrows, StacksRows = stacks, RowsWrap = rowsWrap, LabelW = labelW,
        SliderLength = sliderLength, ShowsAside = aside, PickerGrid = pickerGrid, PickerGrows = pickerGrows, PickerCardW = cardW, PickerThumbH = thumbH, PickerGap = pickerGap,
        PickerShowsText = pickerText, SwatchSize = swatch,
    };

    private static readonly ZuneSettingsLayout[] s_records =
    [
        // Compact (Z-Settings-Sections-Compact, the settings concept's Z-Compact, -Appearance-Compact, Z-Theme-Options-Compact): the sections are the drill-in page (60-tall rows,
        // the name at 26/32/300 over its summary, a chevron) under a 40-tall "find a setting" pill, a section opens as its own page, the rows stack, the short choices are the
        // board's wrapping 40-tall chips, the picker is 2-up (88 tall, name and line), the swatches 40.
        Make(WidthTier.Compact, target: 40f, compactTitle: true, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleHCompact, titleTop: 0f,
            strip: true, listW: 0f, listItemH: 60f, navSize: 26f, navLineH: 32f, searchH: 40f, searchAbove: 8f, searchBelow: 12f, resultPadY: 10f, summaryLines: 2,
            bodyGap: 8f,
            columnW: 1000f, columnGrows: true, stacks: true, rowsWrap: false, labelW: ZuneSettingsRules.LabelW, sliderLength: 200f, aside: false,
            pickerGrid: true, pickerGrows: false, cardW: 120f, thumbH: 88f, pickerGap: 16f, pickerText: true, swatch: 40f),
        // Small (Z-Settings-Appearance-Small, Z-Theme-Options-Small): a 180 section column of 30-tall 18/24 words, a 520 column, rows beside their labels (140 | rest), the picker 4-up
        // (68 tall, the name alone, 12 apart), the swatches 28.
        Make(WidthTier.Small, target: 0f, compactTitle: true, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleZoneSmall, titleTop: ZuneSettingsRules.TitleTopSmall,
            strip: false, listW: 180f, listItemH: 30f, navSize: 18f, navLineH: 24f, searchH: 32f, searchAbove: 0f, searchBelow: 14f, resultPadY: 8f, summaryLines: 0,
            bodyGap: 32f,
            columnW: 520f, columnGrows: false, stacks: false, rowsWrap: true, labelW: 140f, sliderLength: 220f, aside: false,
            pickerGrid: false, pickerGrows: true, cardW: 118f, thumbH: 68f, pickerGap: 12f, pickerText: false, swatch: 28f),
        // Standard: the tuned design (the 32-tall pill over the 220 list of 40-tall 20/28 words, the 660 column, the 200 | rest rows, 150 x 100 tiles, 32 swatches).
        Make(WidthTier.Standard, target: 0f, compactTitle: false, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleZone, titleTop: ZuneSettingsRules.TitleTop,
            strip: false, listW: 220f, listItemH: 40f, navSize: 20f, navLineH: 28f, searchH: 32f, searchAbove: 0f, searchBelow: 14f, resultPadY: 8f, summaryLines: 0,
            bodyGap: 48f,
            columnW: 660f, columnGrows: false, stacks: false, rowsWrap: false, labelW: ZuneSettingsRules.LabelW, sliderLength: 220f, aside: true,
            pickerGrid: false, pickerGrows: false, cardW: 150f, thumbH: 100f, pickerGap: 16f, pickerText: true, swatch: 32f),
        // Huge (Z-Settings-Appearance-Huge, Z-Theme-Options-Huge): the 150 / 160 panorama, a 36-tall pill over a 260 list of 52-tall 24/32 words 40 from a 760 column, rows
        // 220 | rest, the picker 4-up (120 tall), the swatches 36.
        Make(WidthTier.Huge, target: 0f, compactTitle: false, titleSize: ZuneSettingsRules.TitleSizeHuge, titleLineH: ZuneSettingsRules.TitleLineHuge, titleH: ZuneSettingsRules.TitleZoneHuge, titleTop: ZuneSettingsRules.TitleTopHuge,
            strip: false, listW: 260f, listItemH: 52f, navSize: 24f, navLineH: 32f, searchH: 36f, searchAbove: 0f, searchBelow: 16f, resultPadY: 8f, summaryLines: 0,
            bodyGap: 40f,
            columnW: 760f, columnGrows: false, stacks: false, rowsWrap: false, labelW: 220f, sliderLength: 260f, aside: true,
            pickerGrid: false, pickerGrows: true, cardW: 176f, thumbH: 120f, pickerGap: 16f, pickerText: true, swatch: 36f),
        // Huge 4K: Huge's (pages cap at 2400, owner 6.2; the page holds its 760 column and the frame's 64 gutter).
        Make(WidthTier.Huge4K, target: 0f, compactTitle: false, titleSize: ZuneSettingsRules.TitleSizeHuge, titleLineH: ZuneSettingsRules.TitleLineHuge, titleH: ZuneSettingsRules.TitleZoneHuge, titleTop: ZuneSettingsRules.TitleTopHuge,
            strip: false, listW: 260f, listItemH: 52f, navSize: 24f, navLineH: 32f, searchH: 36f, searchAbove: 0f, searchBelow: 16f, resultPadY: 8f, summaryLines: 0,
            bodyGap: 40f,
            columnW: 760f, columnGrows: false, stacks: false, rowsWrap: false, labelW: 220f, sliderLength: 260f, aside: true,
            pickerGrid: false, pickerGrows: true, cardW: 176f, thumbH: 120f, pickerGap: 16f, pickerText: true, swatch: 36f),
    ];

    /// <summary>The record of content tier <paramref name="content"/>.</summary>
    public static ZuneSettingsLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    /// <summary>The record for <paramref name="facts"/>: its content tier's, on the FRAME's gutter (the window tier's: the band, the title bar's wordmark and every page share
    /// one left edge, so a docked panel that narrows the content never moves it).</summary>
    public static ZuneSettingsLayout Of(in TierFacts facts)
    {
        var record = OfContent(in facts);
        float gutter = ZuneTiers.GutterOf(in facts);
        return facts.Policy is null || record.Gutter == gutter ? record : record with { Gutter = gutter };
    }

    private static ZuneSettingsLayout OfContent(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Content);
}

/// <summary>The numbers of Zune's Settings views.</summary>
public static class ZuneSettingsRules
{
    // ── the row grammar ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The label column's width where rows do not stack (Z-Settings-Appearance: 200 | rest).</summary>
    public const float LabelW = 200f;

    /// <summary>The gap between a label and its hint, between a row's controls, and the gap between the label column and the control column.</summary>
    public const float LabelGap = 2f, ControlGap = 12f, RowColumnGap = 24f;

    /// <summary>The gap between a stacked row's label and its control, and between rows (the board's 18 to 22).</summary>
    public const float RowStackGap = 6f, RowGap = 20f;

    /// <summary>The gap between groups, and between a group's heading and the caption beside it.</summary>
    public const float SectionGap = 32f, HeadGap = 14f;

    /// <summary>A greyed row's opacity.</summary>
    public const float DisabledOpacity = 0.5f;

    /// <summary>A command's padding (its hit area beyond the word).</summary>
    public static readonly Edges4 CmdPadding = new(8f, 4f, 8f, 4f);

    /// <summary>The gap between the commands of a row of links.</summary>
    public const float LinkGap = 20f;

    /// <summary>The account card (Z-Settings-Sections: a 48 avatar beside the name and the plan) and its gap.</summary>
    public const float AccountAvatar = 48f, AccountGap = 16f;

    /// <summary>A key chip on the keyboard page (the board's small outlined cap): its padding and its stroke.</summary>
    public const float KeyChipPadX = 8f, KeyChipPadY = 2f, KeyChipStroke = 1f;

    /// <summary>One of the feedback actions (Z-Feedback: a 36 disc, the words, a chevron): the gap between them, the row's padding and its height.</summary>
    public const float ActionGap = 16f, ActionPadX = 12f, ActionPadY = 10f, ActionMinH = 60f, ActionGlyph = 16f;

    /// <summary>The report's contents list: the gap between the mark and its line, and the mark.</summary>
    public const float HoldsGap = 12f, HoldsMark = 14f;

    /// <summary>The round accent button: its diameter (Medium), its glyph, and the gap to the word beside it.</summary>
    public const float DiscSize = 36f, DiscGlyph = 14f, DiscGap = 10f;

    /// <summary>The monospace face the page uses for ids, versions and paths.</summary>
    public const string Mono = "Cascadia Code";

    /// <summary>"No value": the em dash.</summary>
    public const string Dash = "—";

    /// <summary>The value, or the dash where there is none.</summary>
    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? Dash : value;

    // ── the page ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title box's height: the panorama's (120/130) and its compact step's (64/72) lines plus the room their descenders need
    /// (size x 1.25 and a little: "settings" lost its g and its y in the 130 and 72 boxes, which clip). A 16 gap then parts the box from the body.</summary>
    public const float TitleHPanorama = 156f, TitleHCompact = 84f, TitleBodyGap = 16f;

    /// <summary>The Settings page's panorama zone (Z-Settings-*: the body starts at 140 / 70 / 176 under the band, the 16 gap after the zone): the panorama's own top
    /// sits inside it on its exact line (the boards set it at -6 / -6 / -12 in CSS, whose line box centres the glyphs differently from ours: measured against the PNGs, +8 / +3 / +12 more), its descenders hanging over the body's first rows.</summary>
    public const float TitleZone = 124f, TitleZoneSmall = 54f, TitleZoneHuge = 160f, TitleTop = 2f, TitleTopSmall = -3f, TitleTopHuge = 0f;

    /// <summary>The Huge panorama (Z-Settings-Appearance-Huge: 150 over a 160 line), whose box (192) reserves the descender room too.</summary>
    public const float TitleSizeHuge = 150f, TitleLineHuge = 160f, TitleHHuge = 192f;

    /// <summary>The panorama bleeds this far left of the gutter (the board draws it at 40 over a 48 gutter).</summary>
    public const float PanoramaBleed = 8f;

    /// <summary>The open section word's weight and the others': 400 and 300 (Z-Settings-Appearance; the drill-in names are all 300).</summary>
    public const ushort NavSelectedWeight = 400, NavRestWeight = 300;

    /// <summary>A drill-in row's padding above and below its two lines and the gap to its chevron (the concept's Z-Compact: 6 and 12).</summary>
    public const float DrillPadY = 6f, DrillGap = 12f;

    // ── the "find a setting" pill (Z-Settings-Sections, the concept's Z-Home and Z-Compact) ─────────────────────────────────────────────

    /// <summary>The pill's padding before the magnifier and after its last word, the gap between its parts, the magnifier, the field inside it (the
    /// pill's least height less its ring) and the field's type size.</summary>
    public const float SearchPadL = 12f, SearchPadR = 10f, SearchGap = 8f, SearchGlyph = 16f, SearchFieldH = 30f, SearchFontSize = 13f;

    /// <summary>The ring's ink at rest (the field rule's 18 %; the accent while the field has the caret) and its width.</summary>
    public const float SearchRingA = 0.18f, SearchRing = 1f;

    /// <summary>The "ctrl f" key hint's padding (Z-Part-TitleBar's chip).</summary>
    public static readonly Edges4 KeyHintPad = new(4f, 1f, 4f, 1f);

    // ── the home (the concept's Z-Home: "your setup", suggestions, recently changed) ────────────────────────────────────────────────────

    /// <summary>The air under the home's heading and under a block's heading, between blocks, between the summary rows, and a recent row's padding and the gap
    /// between its two lines.</summary>
    public const float HomeHeadGap = 14f, HomeBlockHeadGap = 6f, HomeBlockGap = 34f, HomeRowGap = 14f, RecentPadY = 8f, RecentLineGap = 2f;

    /// <summary>The Compact suggestion line's air under it (the concept's Z-Compact: 6).</summary>
    public const float SuggestBelow = 6f;

    // ── search results (the board's two-line links) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A result's side padding (its wash bleeds past the column by it), the gap between its two lines, how far a child result stands in under its
    /// parent, and the air under the count line.</summary>
    public const float ResultPadX = 12f, ResultLineGap = 2f, ResultUnderInset = 16f, ResultCountGap = 8f;

    // ── the arrival (design 4.9) and "see it" (design 6.5) ──────────────────────────────────────────────────────────────────────────

    /// <summary>How long a link's arrival marks its row's label in the accent before the ink returns (the structure's `SettingsPagePolicy.HighlightMs`,
    /// design 7.3: 1.6 s).</summary>
    public const int HighlightMs = 1600;

    /// <summary>How long the accent holds before its fade back to the ink starts.</summary>
    public const float HighlightLeadMs = 120f;

    /// <summary>Where the arrival brings its row in the viewport (design 4.9: 0.3 of the way down).</summary>
    public const float ArrivalAlign = 0.3f;

    /// <summary>The gap between a label and its "see it" word.</summary>
    public const float SeeItGap = 12f;

    /// <summary>The clip under a row (twice the tip's 320 x 180 in pixels, design 6.5), the air round it, the poster's ring and its ink, and the play glyph.</summary>
    public const float ClipW = 320f, ClipH = 180f, ClipGap = 8f, ClipRing = 1f, ClipRingA = 0.3f, ClipGlyph = 28f;

    /// <summary>The lane's air above and below the column.</summary>
    public const float LaneTop = 4f, LaneBottom = 32f;

    /// <summary>How far the lane's scroll clip reaches left of the column (the column itself does not move): a glyph whose ink hangs left
    /// of its advance at the gutter (a lowercase j's hook, "japanese" in the AI lyrics languages) is not cut by the clip.</summary>
    public const float LaneInk = 4f;

    /// <summary>The gap between the column and the preview, the preview's basis and its cap.</summary>
    public const float AsideGap = 48f, AsideBasis = 320f, AsideMax = 480f, AsideMaxStandard = 360f;

    /// <summary>A short choice stays a row of chips up to this many (past it, a combo).</summary>
    public const int MaxChips = 4, MaxChipChars = 14;

    /// <summary>A Compact chip's side padding (its plate is the target tall).</summary>
    public const float ChipPadX = 16f;

    /// <summary>A chip above Compact (Z-Theme-Options, Z-Settings-Appearance): 32 tall, 12 in on each side.</summary>
    public const float ChipH = 32f, ChipPadXRow = 12f;

    /// <summary>The drill-in row's chevron, the crumb's height, the gap inside them, and the lines a section's page title may wrap to.</summary>
    public const float SectionChevron = 14f, CrumbH = 40f, SectionRowGap = 8f;

    /// <inheritdoc cref="SectionChevron"/>
    public const int SectionTitleLines = 2;

    /// <summary>The gap between a chip and the next, in their wrapping row.</summary>
    public const float ChipGap = 8f;

    /// <summary>The lane swap: the page's neutral cross-fade (Q38), the lane keyed per category and for the results.</summary>
    public static LayoutTransition LaneSwap => Design.Nav.RecipeFor(Design.NavTransitionKind.Neutral);

    /// <summary>The gap under a theme option group's sub-heading (Z-Theme-Options' eyebrow).</summary>
    public const float SubgroupGap = 4f;

    // ── the controls ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The combo's widths: the stock, the wide (quality, visualizer) and the hour, level and budget fields.</summary>
    public const float ComboWidth = 220f, WideComboWidth = 280f, HourComboWidth = 120f, LevelComboWidth = 160f, BudgetComboWidth = 120f;

    /// <summary>A slider's value text width, its tick count, and the crossfade step.</summary>
    public const float SliderValueWidth = 48f, SliderTicks = 4f, CrossfadeStep = 0.5f;

    /// <summary>The lyrics blur (0 to 100 %), the Artist layout's photo dimming (0 to 100 %, in fives) and the episode speed (0.5x to 3x).</summary>
    public const float BlurMax = 100f, BlurStep = 1f, DimMax = 100f, DimStep = 5f, SpeedMin = 0.5f, SpeedMax = 3f, SpeedStep = 0.05f;

    /// <summary>A slider dial this page has no range for (a theme option brings its own).</summary>
    public const float SliderFallbackMax = 100f, SliderFallbackStep = 1f;

    /// <summary>The stage's sensitivity (30 % to 150 %) and sync offset (-500 to 500 ms) sliders.</summary>
    public const float SensitivityMin = 0.3f, SensitivityMax = 1.5f, SensitivityStep = 0.05f, SyncOffsetMin = -500f, SyncOffsetMax = 500f, SyncOffsetStep = 10f;

    /// <summary>The radio line's caption inset (under the radio's label).</summary>
    public const float RadioCaptionInset = 28f;

    // ── the AI lyrics rows ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The download bar's width, a language line's height, its name and status lanes and the gap between lines.</summary>
    public const float AiBarWidth = 180f, AiLanguageRowHeight = 32f, AiLanguageNameWidth = 88f, AiLanguageStatusWidth = 84f, AiLanguageGap = 2f;

    // ── Storage ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nine hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark), indexed in `StorageRules.Parts` order: library, runtime, logs, local store, audio
    /// bodies, license keys, images, AI models (pink), other app data (mustard).</summary>
    public static readonly ColorF[] StorageHues =
    [
        ColorF.FromRgba(0x4A, 0x90, 0xD9), ColorF.FromRgba(0x9B, 0x59, 0xB6), ColorF.FromRgba(0xF5, 0xA6, 0x23),
        ColorF.FromRgba(0x27, 0xAE, 0x60), ColorF.FromRgba(0x1A, 0xBC, 0x9C), ColorF.FromRgba(0x95, 0xA5, 0xA6),
        ColorF.FromRgba(0xE7, 0x4C, 0x3C), ColorF.FromRgba(0xE0, 0x56, 0xA0), ColorF.FromRgba(0xC9, 0xA2, 0x27),
    ];

    /// <summary>A census row's hue square, the census spinner, the meter's height and seam, the legend's basis, the census block's least height.</summary>
    public const float HueSize = 10f, SpinnerSize = 20f, MeterHeight = 10f, MeterGap = 2f, LegendWidth = 200f, CensusMinHeight = 48f;

    /// <summary>The budget editor: the number box's width, the meter's, the percent cap and the fixed budget's least and greatest GiB.</summary>
    public const float BudgetBoxWidth = 150f, BudgetMeterWidth = 300f;
    public const double BudgetPercentMax = 90, BudgetGibMin = 0.0625, BudgetGibMax = 1 << 20;

    // ── Music on this PC (#240 S3) ──

    /// <summary>A folder line's least height (the empty sentence holds the same), the words' column beside the path, the block's gaps (between its parts;
    /// the status line over its bar), the bar's width.</summary>
    public const float MusicRowH = 44f, MusicStatusW = 340f, MusicBlockGap = 12f, MusicStatusGap = 6f, MusicBarWidth = 300f;

    /// <summary>A network folder's line (S13): its least height and padding, the state column's width (and the bar's at Compact), the two lines that
    /// column keeps in every state, and the slot its one verb stands in. The state column, the slot and their gap take the local folders' words
    /// column (`MusicStatusW` = 232 + 12 + 96: either verb in English, Dutch and Korean), so every folder's words start on one line.</summary>
    public const float MusicNetRowH = 64f, MusicNetPadY = 6f, MusicNetStatusW = 232f, MusicNetBarWidthCompact = 200f, MusicTwoLines = 36f, MusicVerbW = 96f;

    /// <summary>A folder's options card: its width and padding.</summary>
    public const float MusicOptionsW = 240f;
    public static readonly Edges4 MusicOptionsPad = new(12f, 8f, 12f, 8f);

    /// <summary>"scanned 2 minutes ago" re-reads its age this often.</summary>
    public const float MusicAgoTickMs = 30_000f;

    // ── About, Privacy ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The update progress line's width, the log line's tick and the receipts' tick (ms), and a minute in ms.</summary>
    public const float UpdateProgressWidth = 180f, LogTickMs = 750f, ReceiptsTickMs = 5000f;
    public const long MillisPerMinute = 60_000;

    // ── the video manager flyout ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The manager's width, its list's tallest and the gap between its rows.</summary>
    public const float ManagerWidth = 420f, ManagerMaxHeight = 360f, ManagerRowGap = 4f;

    // ── the theme picker ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fluid tile's cap and the gap inside a tile (the gap between tiles is the tier's), and the lines the text may take.</summary>
    public const float PickerFluidMax = 220f, PickerCardGap = 8f;
    public const int PickerTextLines = 2;

    /// <summary>The Compact grid's tiles to a line (Z-Settings-Appearance-Compact: two up).</summary>
    public const int PickerGridColumns = 2;

    /// <summary>The selection ring (the thumbnail's border, clear at rest, so nothing shifts) and the gap it keeps from the picture.</summary>
    public const float SelectRing = 2f, SelectGap = 2f;

    /// <summary>The preview still's decode width and its fade over the thumbnail's plate when its decode lands.</summary>
    public const float PreviewDecodePx = 480f, PreviewFadeMs = 150f;

    /// <summary>The free colour picker's starting colour (Zune's pink).</summary>
    public static readonly ColorF FreePickerSeed = ColorF.FromRgba(0xFF, 0x3C, 0xA6);

    // ── the options' preview ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preview's height (the board's 300 frame: the pivots, the panorama, the cover with "play" under it beside the rows, then the bar), its bar's,
    /// a track row's, the number lane, the play disc, the cover, the open pivot and the playing row.</summary>
    public const float PreviewHeight = 300f, PreviewBarHeight = 26f, PreviewRowHeight = 22f, PreviewNumberWidth = 28f, PreviewDisc = 18f, PreviewCover = 72f;
    public const int PreviewOpenPivot = 2, PreviewPlayingRow = 3;
}
