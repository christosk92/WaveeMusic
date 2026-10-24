// ── Themes/Zune/Structure/Rules/ZuneSettingsRules.cs — the numbers and the tier records of Zune's Settings page (themes plan 6.3, P5 Z8b) ───────
//
// Everything the Settings views (`Views\Screens\ZuneSettings*.cs`) size beside the pictures they draw: the row grammar (the label column, the control column, the gaps), the
// controls' widths, the section list, the picker's tiles, the options' preview, the Storage census hues and the Playback video manager. Data, not decisions (WVTH003 allows
// literals only in `Rules\**`).
//
// ONE RECORD PER CONTENT TIER (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows). A view reads `ZuneSettingsTiers.Of(ctx.Tier())` and never
// compares a width with a number (WVTH013). Standard follows `Z-Settings-Appearance`, `Z-Settings-Playback`, `Z-Settings-Sections` and `Z-Theme-Options` (the 220 section list, the
// 660 column, the 200 | rest rows, the 150 x 100 theme tiles); ZT8 tuned Compact, Small and Huge against the -Compact, -Small and -Huge boards:
//
//   tier       title               section list (word)                  column      rows             picker tiles (gap)                   swatch
//   Compact    64/72 (box 84)      scrolling row of 40-tall 20/28       fill        control UNDER    2-up fluid, 120 x 88 + a line (16)   40
//   Small      64/72               180 column, 30-tall 18/24            520         140 | rest       4-up fluid, 118 x 68, name (12)      28
//   Standard   120/130 (box 156)   220 column, 40-tall 20/28            660         200 | rest       150 x 100 + a line (16)              32
//   Huge       150/160 (box 192)   260 column, 52-tall 24/32 (40 gap)   760         220 | rest       4-up fluid, 176 x 120 + a line (16)  36
//   Huge 4K    150/160             Huge's                               760         220 | rest       Huge's                               36
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

    /// <summary>The section list is a wrapping row above the column (Compact) instead of a column beside it.</summary>
    public required bool ListIsStrip { get; init; }

    /// <summary>The section list's width (0 where it is a row).</summary>
    public required float ListW { get; init; }

    /// <summary>A section word's least height (40 at a hit target and Standard, 30 at Small, 52 at Huge).</summary>
    public required float ListItemH { get; init; }

    /// <summary>The space after a section word where the list is a row.</summary>
    public required float ListItemPadRight { get; init; }

    /// <summary>The gap between the section words (0 in the column, 4 in the row).</summary>
    public required float ListGap { get; init; }

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

    /// <summary>The picker's tiles grow to share the line (fluid) instead of holding their width.</summary>
    public required bool PickerGrows { get; init; }

    /// <summary>A picker tile's width (its basis where it grows).</summary>
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
}

/// <summary>The Settings page's tier records.</summary>
public static class ZuneSettingsTiers
{
    /// <summary>One record. The title is <paramref name="titleH"/> tall (the larger size's line, reserved); <paramref name="titleSize"/> 0 keeps the role's size.</summary>
    private static ZuneSettingsLayout Make(WidthTier tier, float target, bool compactTitle, float titleSize, float titleLineH, float titleH, float titleTop,
        bool strip, float listW, float listItemH, float navSize, float navLineH, float listGap, float bodyGap,
        float columnW, bool columnGrows, bool stacks, bool rowsWrap, float labelW, float sliderLength, bool aside,
        bool pickerGrows, float cardW, float thumbH, float pickerGap, bool pickerText, float swatch) => new()
    {
        Tier = tier, Gutter = ZuneTiers.Frame(tier).Gutter, Target = target, CompactTitle = compactTitle,
        TitleSize = titleSize, TitleLineH = titleLineH, TitleH = titleH, TitleTop = titleTop,
        ListIsStrip = strip, ListW = listW, ListItemH = listItemH, NavSize = navSize, NavLineH = navLineH,
        ListItemPadRight = strip ? ZuneSettingsRules.ListItemPadRightRow : 0f,
        ListGap = listGap, BodyGap = bodyGap, ColumnW = columnW, ColumnGrows = columnGrows, StacksRows = stacks, RowsWrap = rowsWrap, LabelW = labelW,
        SliderLength = sliderLength, ShowsAside = aside, PickerGrows = pickerGrows, PickerCardW = cardW, PickerThumbH = thumbH, PickerGap = pickerGap,
        PickerShowsText = pickerText, SwatchSize = swatch,
    };

    private static readonly ZuneSettingsLayout[] s_records =
    [
        // Compact (Z-Settings-Appearance-Compact, -Playback-Compact, Z-Theme-Options-Compact): the sections are a wrapping row of 40-tall words above the column, the rows stack, the
        // short choices are the board's wrapping 40-tall chips, the picker is 2-up (88 tall, name and line), the swatches 40.
        Make(WidthTier.Compact, target: 40f, compactTitle: true, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleHCompact, titleTop: 0f,
            strip: true, listW: 0f, listItemH: 40f, navSize: 20f, navLineH: 28f, listGap: 4f, bodyGap: 8f,
            columnW: 1000f, columnGrows: true, stacks: true, rowsWrap: false, labelW: ZuneSettingsRules.LabelW, sliderLength: 200f, aside: false,
            pickerGrows: true, cardW: 120f, thumbH: 88f, pickerGap: 16f, pickerText: true, swatch: 40f),
        // Small (Z-Settings-Appearance-Small, Z-Theme-Options-Small): a 180 section column of 30-tall 18/24 words, a 520 column, rows beside their labels (140 | rest), the picker 4-up
        // (68 tall, the name alone, 12 apart), the swatches 28.
        Make(WidthTier.Small, target: 0f, compactTitle: true, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleZoneSmall, titleTop: ZuneSettingsRules.TitleTopSmall,
            strip: false, listW: 180f, listItemH: 30f, navSize: 18f, navLineH: 24f, listGap: 0f, bodyGap: 32f,
            columnW: 520f, columnGrows: false, stacks: false, rowsWrap: true, labelW: 140f, sliderLength: 220f, aside: false,
            pickerGrows: true, cardW: 118f, thumbH: 68f, pickerGap: 12f, pickerText: false, swatch: 28f),
        // Standard: the tuned design (the 220 list of 40-tall 20/28 words, the 660 column, the 200 | rest rows, 150 x 100 tiles, 32 swatches).
        Make(WidthTier.Standard, target: 0f, compactTitle: false, titleSize: 0f, titleLineH: 0f, titleH: ZuneSettingsRules.TitleZone, titleTop: ZuneSettingsRules.TitleTop,
            strip: false, listW: 220f, listItemH: 40f, navSize: 20f, navLineH: 28f, listGap: 0f, bodyGap: 48f,
            columnW: 660f, columnGrows: false, stacks: false, rowsWrap: false, labelW: ZuneSettingsRules.LabelW, sliderLength: 220f, aside: true,
            pickerGrows: false, cardW: 150f, thumbH: 100f, pickerGap: 16f, pickerText: true, swatch: 32f),
        // Huge (Z-Settings-Appearance-Huge, Z-Theme-Options-Huge): the 150 / 160 panorama, a 260 list of 52-tall 24/32 words 40 from a 760 column, rows 220 | rest, the picker 4-up
        // (120 tall), the swatches 36.
        Make(WidthTier.Huge, target: 0f, compactTitle: false, titleSize: ZuneSettingsRules.TitleSizeHuge, titleLineH: ZuneSettingsRules.TitleLineHuge, titleH: ZuneSettingsRules.TitleZoneHuge, titleTop: ZuneSettingsRules.TitleTopHuge,
            strip: false, listW: 260f, listItemH: 52f, navSize: 24f, navLineH: 32f, listGap: 0f, bodyGap: 40f,
            columnW: 760f, columnGrows: false, stacks: false, rowsWrap: false, labelW: 220f, sliderLength: 260f, aside: true,
            pickerGrows: true, cardW: 176f, thumbH: 120f, pickerGap: 16f, pickerText: true, swatch: 36f),
        // Huge 4K: Huge's (pages cap at 2400, owner 6.2; the page holds its 760 column and the frame's 64 gutter).
        Make(WidthTier.Huge4K, target: 0f, compactTitle: false, titleSize: ZuneSettingsRules.TitleSizeHuge, titleLineH: ZuneSettingsRules.TitleLineHuge, titleH: ZuneSettingsRules.TitleZoneHuge, titleTop: ZuneSettingsRules.TitleTopHuge,
            strip: false, listW: 260f, listItemH: 52f, navSize: 24f, navLineH: 32f, listGap: 0f, bodyGap: 40f,
            columnW: 760f, columnGrows: false, stacks: false, rowsWrap: false, labelW: 220f, sliderLength: 260f, aside: true,
            pickerGrows: true, cardW: 176f, thumbH: 120f, pickerGap: 16f, pickerText: true, swatch: 36f),
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

    /// <summary>The gap between the area's heading and the sentence that leads it (Z-Feedback: the heading's line, then 4).</summary>
    public const float SentenceGap = 4f;

    /// <summary>The report's contents list: the gap between the mark and its line; and the aside beside the feedback column (the board's rest of the line, 368 at 1440) with the
    /// space above it that lines its first heading up with the first action (Z-Feedback: 70).</summary>
    public const float HoldsGap = 12f, HoldsMark = 14f, HoldsAsideTop = 70f;

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

    /// <summary>The compact section row's edge fade (the band's A7 minimum): the row scrolls, its end dissolves where more sections wait.</summary>
    public const float NavFade = 48f;

    /// <summary>The panorama bleeds this far left of the gutter (the board draws it at 40 over a 48 gutter).</summary>
    public const float PanoramaBleed = 8f;

    /// <summary>The space after a section word where the list is a row.</summary>
    public const float ListItemPadRightRow = 16f;

    /// <summary>The open section word's weight and the others': 400 and 300 (Z-Settings-Appearance).</summary>
    public const ushort NavSelectedWeight = 400, NavRestWeight = 300;

    /// <summary>The lane's air above and below the column.</summary>
    public const float LaneTop = 4f, LaneBottom = 32f;

    /// <summary>The gap between the column and the preview, the preview's basis and its cap.</summary>
    public const float AsideGap = 48f, AsideBasis = 320f, AsideMax = 480f, AsideMaxStandard = 360f;

    /// <summary>A short choice stays a row of chips up to this many (past it, a combo).</summary>
    public const int MaxChips = 4, MaxChipChars = 14;

    /// <summary>A Compact chip's side padding (its plate is the target tall).</summary>
    public const float ChipPadX = 16f;

    /// <summary>The Compact sections page's row and chevron, the crumb's height, the gap inside them, and the lines a section's page title may wrap to.</summary>
    public const float SectionRowH = 56f, SectionChevron = 14f, CrumbH = 40f, SectionRowGap = 8f;

    /// <inheritdoc cref="SectionRowH"/>
    public const int SectionTitleLines = 2;

    /// <summary>The gap between a hit-target chip and the next, in the wrapping row that stands in for the segmented control at Compact.</summary>
    public const float ChipGap = 8f;

    /// <summary>How many topic rows the Notifications tab sizes its list for.</summary>
    public const int TopicCapacity = 12;

    /// <summary>The tab swap: the page's neutral cross-fade (Q38), the lane keyed per tab.</summary>
    public static LayoutTransition TabSwap => Design.Nav.RecipeFor(Design.NavTransitionKind.Neutral);

    // ── the controls ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The combo's widths: the stock, the wide (quality, visualizer) and the hour, level and budget fields.</summary>
    public const float ComboWidth = 220f, WideComboWidth = 280f, HourComboWidth = 120f, LevelComboWidth = 160f, BudgetComboWidth = 120f;

    /// <summary>A slider's value text width, its tick count, and the crossfade step.</summary>
    public const float SliderValueWidth = 48f, SliderTicks = 4f, CrossfadeStep = 0.5f;

    /// <summary>The stage's sensitivity (30 % to 150 %) and sync offset (-500 to 500 ms) sliders.</summary>
    public const float SensitivityMin = 0.3f, SensitivityMax = 1.5f, SensitivityStep = 0.05f, SyncOffsetMin = -500f, SyncOffsetMax = 500f, SyncOffsetStep = 10f;

    /// <summary>The radio line's caption inset (under the radio's label).</summary>
    public const float RadioCaptionInset = 28f;

    // ── the AI lyrics rows ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The download bar's width, a language line's height, its name and status lanes and the gap between lines.</summary>
    public const float AiBarWidth = 180f, AiLanguageRowHeight = 32f, AiLanguageNameWidth = 88f, AiLanguageStatusWidth = 84f, AiLanguageGap = 2f;

    /// <summary>The pack's languages, in the order the page lists them (any other follows in code order).</summary>
    public static readonly string[] AiLanguageOrder = ["en", "es", "nl", "ko"];

    // ── Storage ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seven hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark), indexed in `StorageRules.Parts` order: library, runtime, logs, local store, audio
    /// bodies, license keys, images.</summary>
    public static readonly ColorF[] StorageHues =
    [
        ColorF.FromRgba(0x4A, 0x90, 0xD9), ColorF.FromRgba(0x9B, 0x59, 0xB6), ColorF.FromRgba(0xF5, 0xA6, 0x23),
        ColorF.FromRgba(0x27, 0xAE, 0x60), ColorF.FromRgba(0x1A, 0xBC, 0x9C), ColorF.FromRgba(0x95, 0xA5, 0xA6),
        ColorF.FromRgba(0xE7, 0x4C, 0x3C),
    ];

    /// <summary>A census row's hue square, the census spinner, the meter's height and seam, the legend's basis, the census block's least height.</summary>
    public const float HueSize = 10f, SpinnerSize = 20f, MeterHeight = 10f, MeterGap = 2f, LegendWidth = 200f, CensusMinHeight = 48f;

    /// <summary>The budget editor: the number box's width, the meter's, the percent cap and the fixed budget's least and greatest GiB.</summary>
    public const float BudgetBoxWidth = 150f, BudgetMeterWidth = 300f;
    public const double BudgetPercentMax = 90, BudgetGibMin = 0.0625, BudgetGibMax = 1 << 20;

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

    /// <summary>The selection ring (the thumbnail's border, clear at rest, so nothing shifts) and the gap it keeps from the picture.</summary>
    public const float SelectRing = 2f, SelectGap = 2f;

    /// <summary>The preview image's decode width and fade.</summary>
    public const float PreviewDecodePx = 480f, PreviewFadeMs = 150f;

    /// <summary>The miniature: the title line's, the lines' proportions (fill : rest), the bar under it and the skeleton ink's alpha.</summary>
    public const float MiniTitleFill = 40f, MiniTitleRest = 60f, MiniLineFill = 80f, MiniLineRest = 20f, MiniLine2Fill = 55f, MiniLine2Rest = 45f, MiniBarHeight = 12f, SkeletonAlpha = 0.3f;

    /// <summary>The free colour picker's starting colour (Zune's pink).</summary>
    public static readonly ColorF FreePickerSeed = ColorF.FromRgba(0xFF, 0x3C, 0xA6);

    // ── the options' preview ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preview's height, its bar's, a track row's, the number lane, the play disc, the open pivot and the playing row.</summary>
    public const float PreviewHeight = 220f, PreviewBarHeight = 26f, PreviewRowHeight = 22f, PreviewNumberWidth = 28f, PreviewDisc = 18f;
    public const int PreviewOpenPivot = 2, PreviewPlayingRow = 3;
}
