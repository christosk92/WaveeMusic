// ── Themes/Classic/Structure/Rules/ClassicSettingsRules.cs — the numbers the Settings page owns (themes plan 6.1, T11b) ───────────────────────────
//
// From `Screens\Settings.UI*.cs`: the page's measures (the 1000-DIP left-aligned column, the 4-DIP card spacing, the section glyphs, the tab swap), the widths of
// the combos and number boxes, the theme picker's fixed card, the miniatures the sidebar, track-list and page-layout cards draw, the Storage tab's hues and meter,
// the Privacy tab's report rows, the About hero, the attached-videos manager. WVTH003 allows literal looks only here. The page's TIERS (the column cap, the two Huge
// columns, the stacked Compact rows, the picker's forms) are `Tiers.Settings.cs` (TC8): nothing here is compared with a window width (WVTH013).

using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Settings numbers.</summary>
internal static class ClassicSettingsRules
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
    public const float TagSize = 9f;

    /// <summary>An expander item's text column starts here (the glyph's column plus its gap): group labels, report rows, footers.</summary>
    public const float TextColumnInset = 58f;

    /// <summary>A radio item's caption hangs under the label (the radio's box plus its gap).</summary>
    public const float RadioCaptionInset = 28f;

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

    /// <summary>The preview image's fade over the miniature.</summary>
    public const float PreviewFadeMs = 140f;

    /// <summary>A generated option's text card.</summary>
    public const float OptionCardWidth = 116f, OptionCardHeight = 56f;

    /// <summary>A colour swatch's circle.</summary>
    public const float SwatchSize = 30f;

    /// <summary>A free colour picker's seed when the option has no value yet.</summary>
    public static ColorF FreePickerSeed => ColorF.FromRgba(0, 0, 0);

    // ── the page-layout card's wireframe (ch 27 W4) ──

    /// <summary>The two page layouts the document's `pageLayout` row indexes (<c>DetailPageLayout</c>'s values: the page-layout option's choice index).</summary>
    public const int PageLayoutAutomatic = 0, PageLayoutHero = 1;

    /// <summary>A wireframe row bar, and the hero field of the Hero layout.</summary>
    public const float SketchRowBarHeight = 4f, SketchHeroHeight = 24f;

    /// <summary>The wireframe's gaps: between the rail and the rows, between its stacks, and the tight one inside an identity.</summary>
    public const float SketchColumnGap = 8f, SketchGap = 5f, SketchTightGap = 4f;

    // ── the Surfaces cards' preview (CT9, U1; board C-Settings "Surfaces (washes)") ──

    /// <summary>The preview's three lines (the chrome strip over two page lines) and the gap between them.</summary>
    public const float SurfacesLineHeight = 10f, SurfacesLineGap = 6f;

    /// <summary>The chrome strip's sample tint at Subtle (the board's 25 %); Rich scales it by the style's own Rich / Subtle tint ratio, so the
    /// preview shows the real step between the levels. A fixed sample hue: Settings itself claims the neutral ground, so no live page colour
    /// is on screen to preview with.</summary>
    public const float SurfacesPreviewAlpha = 0.25f;

    /// <summary>The sample artwork hue the Surfaces preview tints with (the board's mauve).</summary>
    public static ColorF SurfacesSampleHue => ColorF.FromRgba(176, 110, 190);

    // ── the sidebar layout card's miniature (ch 25 W21) ──

    /// <summary>The miniature's height; the gap between its lines; the gap between the chips of the Library layout's strip.</summary>
    public const float SidebarPreviewHeight = 96f, SidebarPreviewGap = 3f, SidebarMiniGap = 4f;

    /// <summary>The gap between a card's title and its Active tag, and between the cards and the "finish editing" note.</summary>
    public const float SidebarNoteGap = 6f;

    /// <summary>One text-free list row of the miniature: the cover tile's edge is its height.</summary>
    public const float MiniRowHeight = 10f;

    // ── Storage (N9, W16, W17) ──

    /// <summary>N9: seven hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark), indexed in `StorageRules.Parts` order:
    /// library, runtime, logs, local store, audio bodies, license keys, images.</summary>
    public static readonly ColorF[] StorageHues =
    [
        ColorF.FromRgba(0x4A, 0x90, 0xD9), ColorF.FromRgba(0x9B, 0x59, 0xB6), ColorF.FromRgba(0xF5, 0xA6, 0x23),
        ColorF.FromRgba(0x27, 0xAE, 0x60), ColorF.FromRgba(0x1A, 0xBC, 0x9C), ColorF.FromRgba(0x95, 0xA5, 0xA6),
        ColorF.FromRgba(0xE7, 0x4C, 0x3C),
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

    /// <summary>W19's hero: the 64² tile, the 24/600 version line, the gaps.</summary>
    public const float HeroTile = 64f, HeroVersionSize = 24f, HeroGap = 18f, HeroTextGap = 6f;

    /// <summary>The "What's new" link's unread dot gap.</summary>
    public const float AboutBadgeGap = 6f;

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
