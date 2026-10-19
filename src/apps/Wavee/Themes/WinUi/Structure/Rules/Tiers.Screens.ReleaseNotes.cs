// ── Themes/WinUi/Structure/Rules/Tiers.ReleaseNotes.cs — Classic's What's-new page and its overlays per tier (themes plan 3.6, TC8) ───────────────────
//
// TWO records. The PAGE's, per CONTENT tier (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows) with a Short variant of each
// (`ctx.Tier().Height`, 330-679 tall); and the OVERLAYS', per WINDOW tier (the highlight viewer and the after-update plate size against the window). A view reads
// `WinUiReleaseNotesTiers.Of(ctx.Tier())` / `.Overlay(ctx.Tier())` and never compares a width with a number (WVTH013). Standard is the tuned design (P3b) and its record is
// exactly the numbers the page held before tiers; the other tiers follow `C-ReleaseNotes-Compact`, `-Small` and `-Huge`.
//
//   tier      release switch          title row            hero                              highlights                              changelog
//   Compact   40 picker under title   no glyph, 40/52      24 type, actions a 2-col 40 grid  one column of ROW cards (120x68 poster)   rows >= 44
//   Small     32 picker in the title  glyph, 40/52         28 type, 32 actions below         2 columns of ROW cards                  rows >= 40
//   Standard  208 rail (tuned)        glyph, 40/52         30 type, small actions beside     3 cards (tuned, <= 420)                 tuned rows
//   Huge      280 rail                glyph, 40/52         30 type, actions beside as a row  3 cards <= 420 under a 20/28 heading    3 section columns, rows >= 40
//   Short     page top 8, title bottom 4, no hero meta line (the page scrolls; nothing else gives way)
//
// The body column is capped at 1600 at Huge and LEFT-aligned (never centred), the rail sits right after it (`C-ReleaseNotes-Huge`). The overlays (no board draws
// them; built from Classic's parts, owner Q18): at Compact the after-update plate fills the window less 16 with its cards as ROW cards in one column and a stacked footer, its
// body scrolling; a Short window scrolls it too. Every control of the viewer and the plate is >= 40 at Compact.
//
// HUGE 4K. Classic draws no 4K band, so 3840 reads the Huge records.
//
// Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>How the page lets the reader pick another release.</summary>
public enum WinUiReleaseRailForm : byte
{
    /// <summary>The release rail: a column beside the body (Standard, Huge).</summary>
    Rail,
    /// <summary>A picker button (Compact under the title, Small in the title row): the choices open as a list.</summary>
    Picker,
}

/// <summary>How the highlight cards sit.</summary>
public enum WinUiReleaseHighlightForm : byte
{
    /// <summary>One row of up to three poster-over-copy cards, each at most 420 wide (Standard, Huge).</summary>
    Strip,
    /// <summary>Two columns of ROW cards: a 120 x 68 poster beside the pill, the title and Read more (Small).</summary>
    Grid2,
    /// <summary>One column of ROW cards (Compact).</summary>
    Column,
}

/// <summary>Where the hero's two actions sit.</summary>
public enum WinUiReleaseHeroActions : byte
{
    /// <summary>A stack of two small buttons beside the copy (Standard, tuned).</summary>
    StackBeside,
    /// <summary>A row of two buttons beside the copy, at the copy's foot (Huge).</summary>
    RowBeside,
    /// <summary>A row of two buttons under the copy (Small).</summary>
    RowBelow,
    /// <summary>A two-column grid of equal 40-high buttons under the copy (Compact).</summary>
    GridBelow,
}

/// <summary>What Classic's What's-new page is in one content tier.</summary>
public sealed record WinUiReleaseNotesLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall): the page top is 8, the title's bottom 4 and the hero drops its meta line.</summary>
    public required bool Short { get; init; }

    /// <summary>The release switch: the rail (Standard, Huge) or a picker button (Compact, Small).</summary>
    public required WinUiReleaseRailForm RailForm { get; init; }

    /// <summary>The rail's width (208, Huge 280); 0 where the form is the picker.</summary>
    public required float RailWidth { get; init; }

    /// <summary>The gap between the body column and the rail (14, Huge 32); 0 with no rail.</summary>
    public required float FrameGap { get; init; }

    /// <summary>The picker sits in the title row (Small) instead of under the title (Compact); meaningless for the rail.</summary>
    public required bool PickerInTitleRow { get; init; }

    /// <summary>The picker button's height (Compact 40, Small 32); 0 for the rail.</summary>
    public required float PickerHeight { get; init; }

    /// <summary>The least hit target of every control on the page: 40 at Compact, 0 (each control's own size) elsewhere.</summary>
    public required float Target { get; init; }

    /// <summary>The title row carries the tag glyph (every tier but Compact).</summary>
    public required bool TitleGlyph { get; init; }

    /// <summary>The air over the title row (12 / 16 / 16 / 24; 8 in a Short window).</summary>
    public required float PageTop { get; init; }

    /// <summary>The air between the title row and what is under it (8 / 12; 4 in a Short window).</summary>
    public required float TitleBottom { get; init; }

    /// <summary>The body column's widest; <see cref="float.NaN"/> fills the region (1600 at Huge, left-aligned).</summary>
    public required float BodyMaxW { get; init; }

    /// <summary>The gap between the body's blocks (12 / 12 / 16 / 20).</summary>
    public required float BodyGap { get; init; }

    /// <summary>The hero headline's size (24 / 28 / 30 / 30).</summary>
    public required float HeadlineSize { get; init; }

    /// <summary>The hero's padding (left, top, right, bottom).</summary>
    public required Edges4 HeroPadding { get; init; }

    /// <summary>Where the hero's actions sit.</summary>
    public required WinUiReleaseHeroActions HeroActions { get; init; }

    /// <summary>The hero's buttons' size (Small at Standard, Medium at Small and Huge, Large at Compact).</summary>
    public required ControlSize ActionSize { get; init; }

    /// <summary>The hero's Released / Requires / Tag meta line is shown (false in a Short window).</summary>
    public required bool HeroMeta { get; init; }

    /// <summary>How the highlight cards sit.</summary>
    public required WinUiReleaseHighlightForm HighlightForm { get; init; }

    /// <summary>The highlights carry a 20/28 heading instead of the eyebrow (Huge).</summary>
    public required bool HighlightsHeading { get; init; }

    /// <summary>The gap between highlight cards (8 / 12 / 10 / 16).</summary>
    public required float HighlightGap { get; init; }

    /// <summary>A row card's title size (Compact 14, Small 13.5); the strip cards keep their own.</summary>
    public required float RowTitleSize { get; init; }

    /// <summary>A row card's title line height (Compact 20, Small 18).</summary>
    public required float RowTitleLine { get; init; }

    /// <summary>The changelog's sections side by side: 1 stacks them, 3 sets them in a row (Huge).</summary>
    public required int SectionColumns { get; init; }

    /// <summary>The gap between side-by-side sections (24).</summary>
    public required float SectionColumnGap { get; init; }

    /// <summary>A changelog row's least height (Compact 44, Small and Huge 40); 0 keeps the tuned row.</summary>
    public required float RowMinH { get; init; }

    /// <summary>The release switch is a rail.</summary>
    public bool HasRail => RailForm == WinUiReleaseRailForm.Rail;

    /// <summary>The hero's actions sit beside the copy (a row), not under it.</summary>
    public bool ActionsBeside => HeroActions is WinUiReleaseHeroActions.StackBeside or WinUiReleaseHeroActions.RowBeside;
}

/// <summary>What the highlight viewer and the after-update plate are in one WINDOW tier.</summary>
public sealed record WinUiReleaseOverlayLayout
{
    /// <summary>The window tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall).</summary>
    public required bool Short { get; init; }

    /// <summary>The least hit target of every control (40 at Compact, 0 elsewhere).</summary>
    public required float Target { get; init; }

    /// <summary>The viewer's chrome circles (36; 40 at Compact).</summary>
    public required float ChromeCircle { get; init; }

    /// <summary>The pager row's height (24; 40 at Compact, where each pip carries a 40-wide hit box).</summary>
    public required float PagerRowH { get; init; }

    /// <summary>The window margin the plate keeps on each side (16).</summary>
    public required float PlateMargin { get; init; }

    /// <summary>The plate's side padding (26; 16 at Compact).</summary>
    public required float PlatePadX { get; init; }

    /// <summary>The plate's cards are ROW cards in one column (Compact) instead of one row of poster cards.</summary>
    public required bool CardsColumn { get; init; }

    /// <summary>The plate's footer stacks: the check box over a two-column grid of 40-high buttons (Compact).</summary>
    public required bool FooterStacked { get; init; }

    /// <summary>The plate's body (hero and cards) scrolls above a pinned footer (Compact, and any Short window).</summary>
    public required bool ScrollBody { get; init; }

    /// <summary>The plate's width in a window <paramref name="viewportWidth"/> wide: its 720, never wider than the window less the margins.</summary>
    public float PlateWidth(float viewportWidth)
        => MathF.Min(WinUiReleaseNotesRules.Card.PlateWidth, MathF.Max(1f, viewportWidth - 2f * PlateMargin));

    /// <summary>The plate's tallest in a window <paramref name="viewportHeight"/> tall: its 620 cap, never taller than the window less the margins.</summary>
    public float PlateMaxHeight(float viewportHeight)
        => MathF.Min(WinUiReleaseNotesRules.Card.PlateMaxHeight, MathF.Max(1f, viewportHeight - 2f * PlateMargin));
}

/// <summary>Classic's release-notes records.</summary>
public static class WinUiReleaseNotesTiers
{
    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The record for <paramref name="facts"/>: the content tier's, the Short variant when the window is Short. Standard's where no frame resolved the tiers
    /// (a view mounted alone).</summary>
    public static WinUiReleaseNotesLayout Of(in TierFacts facts)
        => facts.Policy is null ? For(WidthTier.Standard, short_: false) : For(facts.Content, facts.Height == HeightTier.Short);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's).</summary>
    public static WinUiReleaseNotesLayout For(WidthTier content, bool short_)
        => s_pages[Math.Min((int)content, (int)WidthTier.Huge) * 2 + (short_ ? 1 : 0)];

    /// <summary>The page-top air and the title's bottom air in a Short window.</summary>
    public const float ShortPageTop = 8f, ShortTitleBottom = 4f;

    static readonly WinUiReleaseNotesLayout s_compact = new()
    {
        Tier = WidthTier.Compact, Short = false,
        RailForm = WinUiReleaseRailForm.Picker, RailWidth = 0f, FrameGap = 0f, PickerInTitleRow = false, PickerHeight = 40f,
        Target = 40f, TitleGlyph = false, PageTop = 12f, TitleBottom = 8f,
        BodyMaxW = float.NaN, BodyGap = 12f,
        HeadlineSize = 24f, HeroPadding = Edges4.All(16f), HeroActions = WinUiReleaseHeroActions.GridBelow, ActionSize = ControlSize.Large, HeroMeta = true,
        HighlightForm = WinUiReleaseHighlightForm.Column, HighlightsHeading = false, HighlightGap = 8f, RowTitleSize = 14f, RowTitleLine = 20f,
        SectionColumns = 1, SectionColumnGap = 0f, RowMinH = 44f,
    };

    static readonly WinUiReleaseNotesLayout s_small = new()
    {
        Tier = WidthTier.Small, Short = false,
        RailForm = WinUiReleaseRailForm.Picker, RailWidth = 0f, FrameGap = 0f, PickerInTitleRow = true, PickerHeight = 32f,
        Target = 0f, TitleGlyph = true, PageTop = 16f, TitleBottom = 12f,
        BodyMaxW = float.NaN, BodyGap = 12f,
        HeadlineSize = 28f, HeroPadding = new Edges4(20f, 16f, 20f, 16f), HeroActions = WinUiReleaseHeroActions.RowBelow, ActionSize = ControlSize.Medium, HeroMeta = true,
        HighlightForm = WinUiReleaseHighlightForm.Grid2, HighlightsHeading = false, HighlightGap = 12f, RowTitleSize = 13.5f, RowTitleLine = 18f,
        SectionColumns = 1, SectionColumnGap = 0f, RowMinH = 40f,
    };

    // The tuned page: exactly what it drew before tiers (the rail 208 and its 14 gap, the 16 gap between blocks, the three-card strip, the stacked small buttons).
    static readonly WinUiReleaseNotesLayout s_standard = new()
    {
        Tier = WidthTier.Standard, Short = false,
        RailForm = WinUiReleaseRailForm.Rail, RailWidth = 208f, FrameGap = 14f, PickerInTitleRow = false, PickerHeight = 0f,
        Target = 0f, TitleGlyph = true, PageTop = 16f, TitleBottom = 12f,
        BodyMaxW = float.NaN, BodyGap = 16f,
        HeadlineSize = 30f, HeroPadding = new Edges4(24f, 22f, 24f, 20f), HeroActions = WinUiReleaseHeroActions.StackBeside, ActionSize = ControlSize.Small, HeroMeta = true,
        HighlightForm = WinUiReleaseHighlightForm.Strip, HighlightsHeading = false, HighlightGap = 10f, RowTitleSize = 13.5f, RowTitleLine = 18f,
        SectionColumns = 1, SectionColumnGap = 0f, RowMinH = 0f,
    };

    static readonly WinUiReleaseNotesLayout s_huge = new()
    {
        Tier = WidthTier.Huge, Short = false,
        RailForm = WinUiReleaseRailForm.Rail, RailWidth = 280f, FrameGap = 32f, PickerInTitleRow = false, PickerHeight = 0f,
        Target = 0f, TitleGlyph = true, PageTop = 24f, TitleBottom = 12f,
        BodyMaxW = 1600f, BodyGap = 20f,
        HeadlineSize = 30f, HeroPadding = new Edges4(24f, 22f, 24f, 20f), HeroActions = WinUiReleaseHeroActions.RowBeside, ActionSize = ControlSize.Medium, HeroMeta = true,
        HighlightForm = WinUiReleaseHighlightForm.Strip, HighlightsHeading = true, HighlightGap = 16f, RowTitleSize = 13.5f, RowTitleLine = 18f,
        SectionColumns = 3, SectionColumnGap = 24f, RowMinH = 40f,
    };

    static WinUiReleaseNotesLayout ShortOf(WinUiReleaseNotesLayout l)
        => l with { Short = true, PageTop = ShortPageTop, TitleBottom = ShortTitleBottom, HeroMeta = false };

    static readonly WinUiReleaseNotesLayout[] s_pages =
    [
        s_compact, ShortOf(s_compact), s_small, ShortOf(s_small), s_standard, ShortOf(s_standard), s_huge, ShortOf(s_huge),
    ];

    // ── the overlays ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overlay record for <paramref name="facts"/>: the window tier's, the Short variant when the window is Short. Standard's where no frame resolved the tiers.</summary>
    public static WinUiReleaseOverlayLayout Overlay(in TierFacts facts)
        => facts.Policy is null ? OverlayFor(WidthTier.Standard, short_: false) : OverlayFor(facts.Window, facts.Height == HeightTier.Short);

    /// <summary>The overlay record for window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static WinUiReleaseOverlayLayout OverlayFor(WidthTier window, bool short_)
        => s_overlays[Math.Min((int)window, (int)WidthTier.Huge) * 2 + (short_ ? 1 : 0)];

    static WinUiReleaseOverlayLayout MakeOverlay(WidthTier tier, bool short_)
    {
        bool compact = tier == WidthTier.Compact;
        return new WinUiReleaseOverlayLayout
        {
            Tier = tier, Short = short_,
            Target = compact ? 40f : 0f,
            ChromeCircle = compact ? 40f : WinUiReleaseNotesRules.Viewer.ChromeCircle,
            PagerRowH = compact ? 40f : WinUiReleaseNotesRules.PagerRowHeight,
            PlateMargin = 16f,
            PlatePadX = compact ? 16f : WinUiReleaseNotesRules.Card.PlatePadX,
            CardsColumn = compact, FooterStacked = compact, ScrollBody = compact || short_,
        };
    }

    static readonly WinUiReleaseOverlayLayout[] s_overlays =
    [
        MakeOverlay(WidthTier.Compact, false), MakeOverlay(WidthTier.Compact, true),
        MakeOverlay(WidthTier.Small, false), MakeOverlay(WidthTier.Small, true),
        MakeOverlay(WidthTier.Standard, false), MakeOverlay(WidthTier.Standard, true),
        MakeOverlay(WidthTier.Huge, false), MakeOverlay(WidthTier.Huge, true),
    ];
}
