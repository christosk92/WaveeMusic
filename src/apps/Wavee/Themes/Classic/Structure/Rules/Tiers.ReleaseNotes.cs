// ── Themes/Classic/Structure/Rules/Tiers.ReleaseNotes.cs — Classic's What's-new page and its overlays per tier (themes plan 3.6, TC8) ───────────────────
//
// TWO records. The PAGE's, per CONTENT tier (`ctx.Tier().Content`: the region the page lives in, which is what the page gutter follows) with a Short variant of each
// (`ctx.Tier().Height`, 330-679 tall); and the OVERLAYS', per WINDOW tier (the highlight viewer and the after-update plate size against the window). A view reads
// `ClassicReleaseNotesTiers.Of(ctx.Tier())` / `.Overlay(ctx.Tier())` and never compares a width with a number (WVTH013). Standard is the tuned design (P3b) and its record is
// exactly the numbers the page held before tiers; the other tiers follow `C-ReleaseNotes-Compact`, `-Small` and `-Huge`.
//
//   tier      release switch          hero                              highlights                                     changelog
//   Compact   40 picker under title   24 title, actions a 2-col 40 grid hero over the tiles (16)                          one stock Expander per change
//   Small     32 picker in the title  28 title, actions in a row under  hero over the tiles (18), hero described       (every tier: a group heading
//   Standard  208 rail                28 title, actions in a row under  hero beside the stacked tiles (20), hero described  per section, a SelectorBar
//   Huge      280 rail                28 title, actions in a row under  hero beside the stacked tiles (24), all described   over them to show one)
//   (the highlights are the Microsoft Store's home: a big hero tile and smaller ones, the title in a frosted band at the foot; a description not
//   shown at rest opens on hover)
//   Short     page top 8, title bottom 4, no hero meta line (the page scrolls; nothing else gives way)
//
// WinUI's parts throughout (2026-10-26, the owner: "grounded in WinUI patterns, less eyebrows and dots"): the Fluent type ramp, a plain release head (no plate,
// no pills), cards, the stock SelectorBar and Expander, a list rail with the selection pill. The body column is capped at 1200 at Huge and LEFT-aligned (never
// centred), the rail sits right after it. The overlays (no board draws
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

namespace Wavee.Themes.Classic;

/// <summary>How the page lets the reader pick another release.</summary>
public enum ClassicReleaseRailForm : byte
{
    /// <summary>The release rail: a column beside the body (Standard, Huge).</summary>
    Rail,
    /// <summary>A picker button (Compact under the title, Small in the title row): the choices open as a list.</summary>
    Picker,
}

/// <summary>How the highlight tiles sit.</summary>
public enum ClassicReleaseHighlightForm : byte
{
    /// <summary>The hero two thirds wide, the other tiles stacked beside it (Standard, Huge).</summary>
    Bento,
    /// <summary>The hero across the top, the other tiles side by side under it (Compact, Small).</summary>
    Stacked,
}

/// <summary>Where the release head's two actions sit.</summary>
public enum ClassicReleaseHeroActions : byte
{
    /// <summary>A row of two buttons under the words (Small, Standard, Huge).</summary>
    RowBelow,
    /// <summary>A two-column grid of equal 40-high buttons under the copy (Compact).</summary>
    GridBelow,
}

/// <summary>What Classic's What's-new page is in one content tier.</summary>
public sealed record ClassicReleaseNotesLayout
{
    /// <summary>The content tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall): the page top is 8, the title's bottom 4 and the hero drops its meta line.</summary>
    public required bool Short { get; init; }

    /// <summary>The release switch: the rail (Standard, Huge) or a picker button (Compact, Small).</summary>
    public required ClassicReleaseRailForm RailForm { get; init; }

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

    /// <summary>The air over the title row (12 / 16 / 16 / 24; 8 in a Short window).</summary>
    public required float PageTop { get; init; }

    /// <summary>The air between the title row and what is under it (8 / 12; 4 in a Short window).</summary>
    public required float TitleBottom { get; init; }

    /// <summary>The body column's widest; <see cref="float.NaN"/> fills the region (1200 at Huge, left-aligned).</summary>
    public required float BodyMaxW { get; init; }

    /// <summary>The gap between the body's blocks (12 / 12 / 16 / 20).</summary>
    public required float BodyGap { get; init; }

    /// <summary>The release head's title size: WinUI's Title (28), 24 at Compact.</summary>
    public required float HeadlineSize { get; init; }

    /// <summary>Where the head's actions sit.</summary>
    public required ClassicReleaseHeroActions HeroActions { get; init; }

    /// <summary>The head's buttons' size (Medium; Large at Compact).</summary>
    public required ControlSize ActionSize { get; init; }

    /// <summary>The hero's Released / Requires / Tag meta line is shown (false in a Short window).</summary>
    public required bool HeroMeta { get; init; }

    /// <summary>How the highlight tiles sit.</summary>
    public required ClassicReleaseHighlightForm HighlightForm { get; init; }

    /// <summary>The gap between highlight tiles (8; 12 at Huge).</summary>
    public required float HighlightGap { get; init; }

    /// <summary>The hero tile's title size (16 / 18 / 20 / 24); the smaller tiles' is one size (<c>ClassicReleaseNotesRules.TileTitleSize</c>).</summary>
    public required float HeroTitleSize { get; init; }

    /// <summary>Every tile shows its description without a hover (Huge: the room is there); elsewhere it opens while the pointer is on the tile.</summary>
    public required bool DescriptionAlways { get; init; }

    /// <summary>The hero shows its description without a hover (every tier but Compact: the hero is big enough to carry it at rest).</summary>
    public required bool HeroDescriptionAlways { get; init; }

    /// <summary>The hero's padding around its words (12 / 16 / 16 / 20).</summary>
    public required float HeroPad { get; init; }

    /// <summary>The after-update plate's ROW card title size at Compact (14, the plate's cards); the page's cards use the type ramp.</summary>
    public required float RowTitleSize { get; init; }

    /// <summary>The after-update plate's ROW card title line height at Compact (20).</summary>
    public required float RowTitleLine { get; init; }

    /// <summary>The release switch is a rail.</summary>
    public bool HasRail => RailForm == ClassicReleaseRailForm.Rail;
}

/// <summary>What the highlight viewer and the after-update plate are in one WINDOW tier.</summary>
public sealed record ClassicReleaseOverlayLayout
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
        => MathF.Min(ClassicReleaseNotesRules.Card.PlateWidth, MathF.Max(1f, viewportWidth - 2f * PlateMargin));

    /// <summary>The plate's tallest in a window <paramref name="viewportHeight"/> tall: its 620 cap, never taller than the window less the margins.</summary>
    public float PlateMaxHeight(float viewportHeight)
        => MathF.Min(ClassicReleaseNotesRules.Card.PlateMaxHeight, MathF.Max(1f, viewportHeight - 2f * PlateMargin));
}

/// <summary>Classic's release-notes records.</summary>
public static class ClassicReleaseNotesTiers
{
    // ── the page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The record for <paramref name="facts"/>: the content tier's, the Short variant when the window is Short. Standard's where no frame resolved the tiers
    /// (a view mounted alone).</summary>
    public static ClassicReleaseNotesLayout Of(in TierFacts facts)
        => facts.Policy is null ? For(WidthTier.Standard, short_: false) : For(facts.Content, facts.Height == HeightTier.Short);

    /// <summary>The record for content tier <paramref name="content"/> (Huge 4K reads Huge's).</summary>
    public static ClassicReleaseNotesLayout For(WidthTier content, bool short_)
        => s_pages[Math.Min((int)content, (int)WidthTier.Huge) * 2 + (short_ ? 1 : 0)];

    /// <summary>The page-top air and the title's bottom air in a Short window.</summary>
    public const float ShortPageTop = 8f, ShortTitleBottom = 4f;

    static readonly ClassicReleaseNotesLayout s_compact = new()
    {
        Tier = WidthTier.Compact, Short = false,
        RailForm = ClassicReleaseRailForm.Picker, RailWidth = 0f, FrameGap = 0f, PickerInTitleRow = false, PickerHeight = 40f,
        Target = 40f, PageTop = 12f, TitleBottom = 8f,
        BodyMaxW = float.NaN, BodyGap = 12f,
        HeadlineSize = 24f, HeroActions = ClassicReleaseHeroActions.GridBelow, ActionSize = ControlSize.Large, HeroMeta = true,
        HighlightForm = ClassicReleaseHighlightForm.Stacked, HighlightGap = 8f, HeroTitleSize = 16f, DescriptionAlways = false, HeroDescriptionAlways = false, HeroPad = 12f, RowTitleSize = 14f, RowTitleLine = 20f,
    };

    static readonly ClassicReleaseNotesLayout s_small = new()
    {
        Tier = WidthTier.Small, Short = false,
        RailForm = ClassicReleaseRailForm.Picker, RailWidth = 0f, FrameGap = 0f, PickerInTitleRow = true, PickerHeight = 32f,
        Target = 0f, PageTop = 16f, TitleBottom = 12f,
        BodyMaxW = float.NaN, BodyGap = 12f,
        HeadlineSize = 28f, HeroActions = ClassicReleaseHeroActions.RowBelow, ActionSize = ControlSize.Medium, HeroMeta = true,
        HighlightForm = ClassicReleaseHighlightForm.Stacked, HighlightGap = 8f, HeroTitleSize = 18f, DescriptionAlways = false, HeroDescriptionAlways = true, HeroPad = 16f, RowTitleSize = 13.5f, RowTitleLine = 18f,
    };

    static readonly ClassicReleaseNotesLayout s_standard = new()
    {
        Tier = WidthTier.Standard, Short = false,
        RailForm = ClassicReleaseRailForm.Rail, RailWidth = 208f, FrameGap = 14f, PickerInTitleRow = false, PickerHeight = 0f,
        Target = 0f, PageTop = 16f, TitleBottom = 12f,
        BodyMaxW = float.NaN, BodyGap = 16f,
        HeadlineSize = 28f, HeroActions = ClassicReleaseHeroActions.RowBelow, ActionSize = ControlSize.Medium, HeroMeta = true,
        HighlightForm = ClassicReleaseHighlightForm.Bento, HighlightGap = 8f, HeroTitleSize = 20f, DescriptionAlways = false, HeroDescriptionAlways = true, HeroPad = 16f, RowTitleSize = 13.5f, RowTitleLine = 18f,
    };

    static readonly ClassicReleaseNotesLayout s_huge = new()
    {
        Tier = WidthTier.Huge, Short = false,
        RailForm = ClassicReleaseRailForm.Rail, RailWidth = 280f, FrameGap = 32f, PickerInTitleRow = false, PickerHeight = 0f,
        Target = 0f, PageTop = 24f, TitleBottom = 12f,
        BodyMaxW = 1200f, BodyGap = 20f,
        HeadlineSize = 28f, HeroActions = ClassicReleaseHeroActions.RowBelow, ActionSize = ControlSize.Medium, HeroMeta = true,
        HighlightForm = ClassicReleaseHighlightForm.Bento, HighlightGap = 12f, HeroTitleSize = 24f, DescriptionAlways = true, HeroDescriptionAlways = true, HeroPad = 20f, RowTitleSize = 13.5f, RowTitleLine = 18f,
    };

    static ClassicReleaseNotesLayout ShortOf(ClassicReleaseNotesLayout l)
        => l with { Short = true, PageTop = ShortPageTop, TitleBottom = ShortTitleBottom, HeroMeta = false };

    static readonly ClassicReleaseNotesLayout[] s_pages =
    [
        s_compact, ShortOf(s_compact), s_small, ShortOf(s_small), s_standard, ShortOf(s_standard), s_huge, ShortOf(s_huge),
    ];

    // ── the overlays ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overlay record for <paramref name="facts"/>: the window tier's, the Short variant when the window is Short. Standard's where no frame resolved the tiers.</summary>
    public static ClassicReleaseOverlayLayout Overlay(in TierFacts facts)
        => facts.Policy is null ? OverlayFor(WidthTier.Standard, short_: false) : OverlayFor(facts.Window, facts.Height == HeightTier.Short);

    /// <summary>The overlay record for window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static ClassicReleaseOverlayLayout OverlayFor(WidthTier window, bool short_)
        => s_overlays[Math.Min((int)window, (int)WidthTier.Huge) * 2 + (short_ ? 1 : 0)];

    static ClassicReleaseOverlayLayout MakeOverlay(WidthTier tier, bool short_)
    {
        bool compact = tier == WidthTier.Compact;
        return new ClassicReleaseOverlayLayout
        {
            Tier = tier, Short = short_,
            Target = compact ? 40f : 0f,
            ChromeCircle = compact ? 40f : ClassicReleaseNotesRules.Viewer.ChromeCircle,
            PagerRowH = compact ? 40f : ClassicReleaseNotesRules.PagerRowHeight,
            PlateMargin = 16f,
            PlatePadX = compact ? 16f : ClassicReleaseNotesRules.Card.PlatePadX,
            CardsColumn = compact, FooterStacked = compact, ScrollBody = compact || short_,
        };
    }

    static readonly ClassicReleaseOverlayLayout[] s_overlays =
    [
        MakeOverlay(WidthTier.Compact, false), MakeOverlay(WidthTier.Compact, true),
        MakeOverlay(WidthTier.Small, false), MakeOverlay(WidthTier.Small, true),
        MakeOverlay(WidthTier.Standard, false), MakeOverlay(WidthTier.Standard, true),
        MakeOverlay(WidthTier.Huge, false), MakeOverlay(WidthTier.Huge, true),
    ];
}
