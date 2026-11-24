// ── Themes/WinUi/Structure/Rules/WinUiSettingsRules.cs — the numbers the Settings page owns (themes plan 6.1, T11b) ───────────────────────────
//
// From `Screens\Settings.UI*.cs`: the page's measures (the 1000-DIP left-aligned column, the 4-DIP card spacing, the section glyphs, the tab swap), the widths of
// the combos and number boxes, the theme picker's fixed card, the miniatures the sidebar, track-list and page-layout cards draw, the Storage tab's hues and meter,
// the Privacy tab's report rows, the About hero, the attached-videos manager. WVTH003 allows literal looks only here. The page's TIERS (the column cap, the two Huge
// columns, the stacked Compact rows, the picker's forms) are `Tiers.Settings.cs` (TC8): nothing here is compared with a window width (WVTH013).

using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>Classic's Settings numbers.</summary>
internal static class WinUiSettingsRules
{
    // ── the page (N1, N2, W1) ──

    /// <summary>N2: cards 4 DIP apart.</summary>
    public const float CardSpacing = 4f;

    /// <summary>A tab switch swaps the tab's body with the page cross-fade (the neutral page swap's recipe), the leaving body fading under the
    /// arriving one.</summary>
    public static LayoutTransition TabSwap => Design.Nav.RecipeFor(Design.NavTransitionKind.Neutral);

    /// <summary>The tab strip's first label sits on the page gutter: the item's own 12-DIP padding pulled back out (board: margin 0 0 0 -12).</summary>
    public const float TabLeadingInset = -12f;

    /// <summary>A section header's glyph and how far it drops to sit on the title's first line.</summary>
    public const float SectionGlyph = 16f, SectionGlyphDrop = 2f;

    /// <summary>A one-row section header (Small, Huge: C-SettingsSections-Small / -Huge): the gap between the title and the caption on its line.</summary>
    public const float SectionInlineGap = 8f;

    /// <summary>The page title's gear glyph where the tier draws one (C-Settings-Compact / -Small / -Huge: 24, 12 before the title).</summary>
    public const float TitleGlyph = 24f, TitleGlyphGap = 12f;

    /// <summary>The keyboard page's key chip: its padding.</summary>
    public const float KeyChipPadX = 8f, KeyChipPadY = 2f;

    // ── the registry's page (settings rework phase 4: the concept boards Main, W-Search, W-Arrival, W-Compact) ──

    /// <summary>A link's arrival (design 4.9, 7.2): the row takes a 2-DIP accent ring over the accent's subtle fill, holds while the row glides in, then both
    /// fade back over <see cref="HighlightMs"/> (Windows Settings' "jump to setting"). The page's policy carries the duration to the core
    /// (<c>WinUiPolicies</c> → <c>SettingsPagePolicy.HighlightMs</c> → <c>SettingsArrival.HighlightMs</c>). Reduced motion holds for the whole duration and cuts.</summary>
    public const int HighlightMs = 1600;

    /// <summary>The arrival ring's width, and how long the highlight holds at full strength before its fade (the glide's own length).</summary>
    public const float HighlightBorder = 2f, HighlightHoldMs = 450f;

    /// <summary>The ring goes back to rest a beat after its fade lands (the next press's hover then cross-fades at the card's own speed).</summary>
    public const float HighlightSettleMs = 60f;

    /// <summary>The strip's selected item glides into its sideways scroller (Compact, Small) with this much of the strip kept beyond it, so the next
    /// category's word shows too (a minimal move: nothing scrolls while the item and its gutter are in view).</summary>
    public const float StripGutter = 64f;

    /// <summary>Where the arrival puts its row in the scroller (design 4.9: a third of the way down).</summary>
    public const float ArrivalAlign = 0.3f;

    /// <summary>How long a parent's expander takes to open before the arrival gives up on a child row that never drew (the AI lyrics card before its setup:
    /// the parent row takes the highlight instead). The expander's own reveal is about 260 ms.</summary>
    public const float ArrivalFallbackMs = 400f;

    /// <summary>A clickable row card's focus ring sits 3 DIP outside it (SettingsCard's own).</summary>
    public static readonly Edges4 CardFocusMargin = new(-3f, -3f, -3f, -3f);

    /// <summary>The "Find a setting" box's field height (the stock AutoSuggestBox, as the title bar's omnibar), and at a tier that asks for a hit target (Compact).</summary>
    public const float SearchH = 32f, SearchTouchH = 40f;

    /// <summary>The box's query button reaches a little past its 32 x 28 plate (the omnibar's slop).</summary>
    public static readonly Edges4 SearchQueryHitSlop = new(1f, 5f, 1f, 5f);

    /// <summary>The box's suggestion list: the top results it shows (design 7.2: eight), a row's height (glyph, title over breadcrumb), its glyph and the gap
    /// between the glyph and the words.</summary>
    public const int SuggestionRows = 8;
    public const float SuggestionRowH = 48f, SuggestionGlyph = 16f, SuggestionGlyphGap = 12f;

    /// <summary>The suggestion row's padding inside the list plate (the stock list item's 12 lead) and its plate inset.</summary>
    public static readonly Edges4 SuggestionPadding = new(12f, 6f, 12f, 6f), SuggestionInset = new(4f, 2f, 4f, 2f);

    /// <summary>The results page: the heading (Subtitle 20 / 28), the gap under its count line, and the breadcrumb's gap above its card.</summary>
    public const float ResultsTitleSize = 20f, ResultsTitleLine = 28f, ResultsHeadGap = 4f, ResultBreadcrumbGap = 6f;

    /// <summary>A result's breadcrumb (a link to the dial in its category) reaches the Compact 40 target past its 12-DIP line.</summary>
    public static readonly Edges4 BreadcrumbTouchSlop = new(10f, 10f, 10f, 10f);

    /// <summary>The space between the count line and the first result (the concept board W-Search: 22).</summary>
    public const float ResultsListGap = 20f;

    /// <summary>The space between one result (its breadcrumb and card) and the next.</summary>
    public const float ResultGap = 16f;

    /// <summary>The no-match card: a dashed-looking frame is no Fluent shape, so it is a plain card with the line, the "did you mean" links and the app search.</summary>
    public const float NoMatchGap = 8f;

    /// <summary>The gap between the Settings home's "At a glance" cards in their grid (the concept board's 8).</summary>
    public const float GlanceGap = 8f;

    /// <summary>A Recently changed card's "when" column.</summary>
    public const float RecentWhenGap = 12f;

    /// <summary>The explanation badge (design 6.5): the info glyph after a row's label, its cell, the gap after the label, and the hit slop that lifts the
    /// 20-DIP cell to the 32 floor (Small and up) and to the Compact 40 target without growing the label's line. At Compact it reaches further down
    /// than up: the label sits 8 under an expander item's top edge, where the expander's clip would cut a slop that reached 10 above it.</summary>
    public const float ExplainGlyph = 14f, ExplainCell = 20f, ExplainGap = 6f;
    public static readonly Edges4 ExplainTouchSlop = new(10f, 6f, 10f, 14f), ExplainSlop = new(6f, 6f, 6f, 6f);

    /// <summary>The explanation tip's clip: 320 x 180 (the clips are recorded at twice that), its play glyph over the poster or the placeholder.</summary>
    public const float ExplainClipW = 320f, ExplainClipH = 180f, ExplainPlayGlyph = 28f, ExplainPlayCell = 56f;

    /// <summary>The "Also in" footer of a row: its padding (the card's 16 lead) and the gap between its words.</summary>
    public static readonly Edges4 AlsoInPadding = new(16f, 6f, 16f, 6f);
    public const float AlsoInGap = 8f;

    /// <summary>The episode speed's slider: the speed menus' range and step (0.5x to 2x by quarters).</summary>
    public const float EpisodeSpeedMin = 0.5f, EpisodeSpeedMax = 2f, EpisodeSpeedStep = 0.25f;


    /// <summary>The title glyph arrives and leaves on a tier crossing with the form fade (opacity over <c>Design.Motion.Fast</c>), while the title beside it
    /// slides (<see cref="HeadSlide"/>).</summary>
    public static LayoutTransition HeadFade => new(TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentAccelerate));

    /// <summary>What moves inside the head and the section headers on a tier crossing (the title behind its glyph, a caption between its two slots) slides there.</summary>
    public static LayoutTransition HeadSlide => new(TransitionChannels.Position, TransitionDynamics.Tween(Design.Motion.Standard, Easing.SmoothOut));

    /// <summary>N4: the collapsed picker group's answer in an expander header.</summary>
    public const float ValueTagSize = 14f;

    /// <summary>The caption size of the small text the cards carry (12).</summary>
    public const float CaptionSize = 12f;

    /// <summary>The pill tags' text (Active, Developer).</summary>
    public const float TagSize = 11f;

    /// <summary>An expander item's text column starts here (the glyph's column plus its gap): group labels, report rows, footers.</summary>
    public const float TextColumnInset = 58f;

    /// <summary>A radio item's caption hangs under the label (the radio's box plus its gap).</summary>
    public const float RadioCaptionInset = 28f;

    // ── the screens' own marks (W8b: W-Settings, W-ThemeOptions) ──

    /// <summary>The gap between the cards' column and the preview column at Huge.</summary>
    public const float AsideGap = 48f;

    /// <summary>The preview column is never narrower than this (the column beside a 1200 card column at 1920 wide).</summary>
    public const float AsideMinWidth = 320f;

    /// <summary>The preview's title (Subtitle 20 / 28) and the aspect of the window mock and of a fluid picker card's still (the board's 16 : 9).</summary>
    public const float PreviewTitleSize = 20f, PreviewTitleLine = 28f, PreviewAspect = 16f / 9f;

    /// <summary>A theme card's padding (board: 8), the Small row card's, and the card's caption lines.</summary>
    public const float CardPad = 8f, RowCardPad = 8f;

    /// <summary>A theme card's caption lines (board: two).</summary>
    public const int CardDescriptionLines = 2;

    /// <summary>The Small row card's thumbnail width (board: 48 x 32).</summary>
    public const float RowThumbW = 48f;

    /// <summary>The selected card's check mark and an option's note glyph.</summary>
    public const float CheckGlyph = 14f, NoteGlyph = 14f;

    /// <summary>The window mock's hairline (the Huge preview column).</summary>
    public const float MiniHairline = 1f;

    /// <summary>A nested option card is at least this tall (board: 56).</summary>
    public const float OptionCardMinHeight = 56f;

    /// <summary>An option's combo (board: 180 wide).</summary>
    public const float OptionComboWidth = 180f;

    // ── the controls' widths ──

    /// <summary>General: the language and tray combos.</summary>
    public const float GeneralComboWidth = 260f;

    /// <summary>General: the render-GPU combo.</summary>
    public const float GpuComboWidth = 300f;

    /// <summary>Notifications: a quiet-hours bound.</summary>
    public const float HourComboWidth = 120f;

    /// <summary>Appearance: the zoom combo and the sidebar density combo.</summary>
    public const float ZoomComboWidth = 160f;

    /// <summary>Appearance: the visualizer, layout and daylist-clock combos, and a generated option's select.</summary>
    public const float StageComboWidth = 220f;

    /// <summary>Appearance: the player-style combo.</summary>
    public const float NpvComboWidth = 180f;

    /// <summary>A slider's lane (lyrics blur, the stage's sensitivity and sync, a generated option's range).</summary>
    public const float SliderLength = 180f;

    /// <summary>Playback: the quality, metered-cap and video combos.</summary>
    public const float WideComboWidth = 280f;

    /// <summary>Playback: the normalization-mode and equalizer-preset combos.</summary>
    public const float NarrowComboWidth = 200f;

    /// <summary>Playback: the crossfade's slider and its number box.</summary>
    public const float CrossSliderLength = 220f, CrossBoxWidth = 96f;

    /// <summary>Storage: the budget's preset and the metadata budget combos, and its number boxes.</summary>
    public const float BudgetComboWidth = 120f, BudgetBoxWidth = 150f;

    /// <summary>Privacy: the two log-level combos.</summary>
    public const float LevelComboWidth = 160f;

    // ── the theme picker and the generated options ──

    /// <summary>The decode width of a picker card's preview image (twice the widest card: the tier records carry the card's own width).</summary>
    public const float PreviewDecodePx = 416f;

    /// <summary>A picker card's details: the padding of the tuned card (name over a two-line description), of the Small strip card and of the Compact grid card
    /// (the name row alone, 36 tall), and the strip card's one-line description.</summary>
    public static readonly Edges4 ThemeDetailsPadding = new(12f, 8f, 12f, 12f), ThemeStripDetailsPadding = new(10f, 8f, 10f, 10f), ThemeGridDetailsPadding = new(10f, 6f, 10f, 6f);

    /// <summary>The strip card's description is one line.</summary>
    public const int ThemeStripDescriptionLines = 1;

    /// <summary>The preview still's fade over the thumbnail's plate when its decode lands.</summary>
    public const float PreviewFadeMs = 140f;

    /// <summary>A generated option's text card.</summary>
    public const float OptionCardWidth = 116f, OptionCardHeight = 56f;

    /// <summary>A colour swatch's circle.</summary>
    public const float SwatchSize = 30f;

    /// <summary>A free colour picker's seed when the option has no value yet.</summary>
    public static ColorF FreePickerSeed => ColorF.FromRgba(0, 0, 0);

    // ── Storage (N9, W16, W17) ──

    /// <summary>N9: nine hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark), indexed in `StorageRules.Parts` order:
    /// library, runtime, logs, local store, audio bodies, license keys, images, AI models (pink), other app data (mustard).</summary>
    public static readonly ColorF[] StorageHues =
    [
        ColorF.FromRgba(0x4A, 0x90, 0xD9), ColorF.FromRgba(0x9B, 0x59, 0xB6), ColorF.FromRgba(0xF5, 0xA6, 0x23),
        ColorF.FromRgba(0x27, 0xAE, 0x60), ColorF.FromRgba(0x1A, 0xBC, 0x9C), ColorF.FromRgba(0x95, 0xA5, 0xA6),
        ColorF.FromRgba(0xE7, 0x4C, 0x3C), ColorF.FromRgba(0xE0, 0x56, 0xA0), ColorF.FromRgba(0xC9, 0xA2, 0x27),
    ];

    /// <summary>A category row's hue accent.</summary>
    public const float StorageAccentWidth = 3f;

    /// <summary>The census spinner.</summary>
    public const float SpinnerSize = 20f;

    /// <summary>W16's usage card: the 18/700 total, the 10-DIP segmented bar with its 2-DIP seams, the legend's swatch (the bar's height), a legend
    /// cell's minimum width and the percent column.</summary>
    public const float TotalFigureSize = 18f, MeterHeight = 10f, MeterGap = 2f, LegendMinWidth = 200f, PercentColumnWidth = 36f;

    /// <summary>W17's budget meter and the gap between its lines.</summary>
    public const float BudgetMeterWidth = 300f, BudgetGap = 6f;

    // ── Music on this PC (#240 S3; board boards240/Main "1 · Settings › Storage › Music on this PC") ──

    /// <summary>A card whose header wraps its control (<c>Card.WrapControl</c>): the glyph and the words keep this much beside it before it drops under them, and the gap
    /// between the two lines.</summary>
    public const float WrapLabelBasis = 240f, WrapLineGap = 8f;

    /// <summary>A folder row: 52 tall (the empty sentence reserves the same), its state mark's 20-DIP cell and 16-DIP mark, the path column, the
    /// row's 13-DIP type and the stacked (Compact) form's line gap.</summary>
    public const float MusicRowHeight = 52f, MusicMarkCell = 20f, MusicMarkSize = 16f, MusicPathWidth = 300f, MusicTextSize = 13f, MusicStackGap = 2f;

    /// <summary>The row's "⋯" and "✕" buttons (32 square; the Compact target lifts them to 40) and their glyphs.</summary>
    public const float MusicIconButton = 32f, MusicMoreGlyph = 16f, MusicRemoveGlyph = 12f;

    /// <summary>A network folder's row (S13): 64 tall, the network glyph's gap to the path, the 12-DIP words under the path, the scan's bar under the
    /// state line and its gap, and the two lines Compact keeps for that line in every state.</summary>
    public const float MusicNetRowHeight = 64f, MusicNetGlyphGap = 10f, MusicSubSize = 12f, MusicNetBarWidth = 240f, MusicNetBarGap = 6f, MusicTwoLines = 36f;

    /// <summary>The slot a network folder's one verb (Sign in, Retry) stands in at every state: wide enough for either word in English, Dutch
    /// and Korean, so a verb that comes or goes resizes nothing.</summary>
    public const float MusicVerbSlotW = 96f;

    /// <summary>The status row under the folders: 64 tall, the time-left aside's 12-DIP type, the bar's width (Compact a narrower one) and its gap under
    /// the line.</summary>
    public const float MusicStatusHeight = 64f, MusicAsideSize = 12f, MusicBarWidth = 320f, MusicBarWidthCompact = 200f, MusicBarGap = 8f;

    /// <summary>"Scanned 2 minutes ago" re-reads its age this often.</summary>
    public const float MusicAgoTickMs = 30_000f;

    /// <summary>The folder path's face (the board's monospace path).</summary>
    public const string MusicPathFont = "Cascadia Mono";

    // ── Privacy & diagnostics ──

    /// <summary>A saved report's row: its height, the kind dot, the stacked stamp and id, and the fixed columns.</summary>
    public const float ReportRowHeight = 44f, ReportDotSize = 8f, ReportStackGap = 2f, ReportStampWidth = 150f, ReportKindWidth = 56f, ReportStateWidth = 110f;

    /// <summary>A saved report's type sizes: the kind and what (13), the short id (11).</summary>
    public const float ReportTextSize = 13f, ReportIdSize = 11f;

    /// <summary>The log viewer card's counter interval.</summary>
    public const float LogTickMs = 750f;

    // ── About ──

    /// <summary>The receipts' sampling interval (N12).</summary>
    public const float ReceiptsTickMs = 5000f;

    /// <summary>W19's hero: the 64² tile, the 24/600 version line, the gaps, the tile's radius and glyph, the hero's padding.</summary>
    public const float HeroTile = 64f, HeroVersionSize = 24f, HeroGap = 18f, HeroTextGap = 6f, HeroTileRadius = 16f, HeroGlyph = 30f;
    public static readonly Edges4 HeroPadding = new(24f, 22f, 24f, 22f);

    /// <summary>A license expander's body item: padding 16, 12, 16, 16 (a flat never-wrapping card).</summary>
    public static readonly Edges4 LicensePadding = new(16f, 12f, 16f, 16f);

    /// <summary>The update status card's progress bar.</summary>
    public const float UpdateProgressWidth = 180f;

    // ── the on-device AI lyrics card ──

    /// <summary>The download, paused and preparing arms' progress bar.</summary>
    public const float AiBarWidth = 180f;

    /// <summary>A language row of the Ready expander: its height, the fixed columns (name, status, action) and the name's type.</summary>
    public const float AiLanguageRowHeight = 32f, AiLanguageNameWidth = 88f, AiLanguageStatusWidth = 84f, AiLanguageActionWidth = 96f;

    /// <summary>The language name's size and line height.</summary>
    public const float AiLanguageNameSize = 14f, AiLanguageNameLineHeight = 20f;

    // ── the attached-videos manager flyout (ch 24 W20-W22) ──

    /// <summary>The flyout's width.</summary>
    public const float ManagerWidth = 420f;

    /// <summary>The rows' heights: the "Browse all" door, a glance row and a hint, the leaf's header; the back button's edge.</summary>
    public const float ManagerBrowseRowHeight = 40f, ManagerCompactRowHeight = 44f, ManagerLeafHeaderHeight = 36f, ManagerBackSize = 28f;

    /// <summary>The lists' caps (results, the newest few, the whole roster) and the glance list's gap.</summary>
    public const float ManagerResultsMaxHeight = 380f, ManagerRecentMaxHeight = 280f, ManagerLeafMaxHeight = 400f, ManagerCompactGap = 2f;
}
