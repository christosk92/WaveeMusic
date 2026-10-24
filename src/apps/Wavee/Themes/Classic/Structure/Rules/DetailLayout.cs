// ── Themes/Classic/Structure/Rules/DetailLayout.cs — the numbers of Classic's detail frame (themes plan 6.1, T9a) ─────────────────────────────────
//
// What `Entities\Detail.UI.cs`, `Detail.UI.Hero.cs` and `Detail.Insights.cs` held as literals and file-level constants beside the pictures they size,
// moved with those pictures: the rail's cover bucket and floor, the collapsed strip's cover floor and chevron height, the vertical show header's
// cover and info measure, the attribution run's type, and the insights sheet's geometry (width, padding, toggle, close, header title, the cast,
// the spline). They are data, not decisions (WVTH003 allows literals only in `Rules\**`).
//
// WHAT IS NOT HERE, AND WHY. The page-mode, table and height LADDERS and the per-tier records are `Tiers.Detail.cs` (TC5). The width bands of the rail (the rest, minimum, maximum and page-aware cap of
// `Detail.RailPolicy`, the collapse detent) and the hero's arithmetic (`Detail.VerticalLayout`, `BandLayout`, `Skeleton.RailPlanFor`, `RailLayout`)
// are DECISIONS the core host and the table both read and the skeleton must agree with row for row, so they stay core, pure and unit-tested
// (`DetailRailPolicyTests`, `DetailSkeletonGeometryTests`); the views are handed their results through the props, and no view compares a width with
// a number (WVTH013). M8 made the page layout, the rails' uniformity and the reset Classic's options (`ClassicStructure.PageLayout` / `RailUniform` / `ClearRememberedSizes`) and the remembered sizes Classic's theme state (`DetailRailPrefs`); the width arithmetic stays core; the hero's per-kind knobs
// (`SlimHead` / `HoverHeart` / `PlainRows`) were never ported to this branch (T9c found no members to move).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's detail frame views.</summary>
public static class ClassicDetailLayout
{
    // ── the arms' cross-fade (a page-mode crossing, themes plan 3.3) ───────────────────────────────────────────────────

    /// <summary>An arm enters and leaves on its opacity alone: the shared cross-fade of a tier swap.</summary>
    public static readonly EnterExit ArmFade = new(Opacity: 0f, Active: true);

    /// <summary>The cross-fade's timing (the plan's ~160 ms, <c>Design.Motion.Fast</c>).</summary>
    public static MotionTokenDef ArmFadeMotion => MotionTok.ControlFast;

    // ── the rail and its collapsed strip ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The decode bucket every rail / strip / show-header cover asks for (the shelf card's 256 bucket: a warm texture on arrival).</summary>
    public const int CoverDecodePx = 256;

    /// <summary>The rail cover's floor: the cover fills the column less its side padding, never below this.</summary>
    public const float RailCoverMin = 80f;

    /// <summary>The collapsed strip's cover floor (the strip is 96 DIP wide less its padding).</summary>
    public const float CompactCoverMin = 48f;

    /// <summary>The collapsed strip's expand chevron hit box height.</summary>
    public const float CompactExpandHeight = 28f;

    /// <summary>The collapsed strip's two-line title: size and line height.</summary>
    public const float CompactTitleSize = 12f, CompactTitleLine = 16f;

    /// <summary>The shimmer SHAPE of the meta line ("00 songs · 0 hr 00 min"): sized by the engine's deriver, never painted as text.</summary>
    public const string MetaShimmerText = "00 songs · 0 hr 00 min";

    // ── the vertical show header ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The show header's cover edge (the one arm without the 1.18 saturation boost).</summary>
    public const float ShowCover = 140f;

    /// <summary>The info column's nominal measure while the page is unmeasured, and the width a page slot is handed there.</summary>
    public const float ShowInfoWidth = 600f;

    /// <summary>The show header's title: size and line height (the PageHero ramp's 28 / 36).</summary>
    public const float ShowTitleSize = 28f, ShowTitleLine = 36f;

    // ── the attribution run (the owner line of the rail and of the hero) ──────────────────────────────────────────────

    /// <summary>The owner avatar of the default owner row.</summary>
    public const float OwnerAvatar = 24f;

    /// <summary>The owner / artists text: size and line height.</summary>
    public const float AttributionSize = 12f, AttributionLine = 16f;

    // ── the head of a page with no rail (tuned Classic D16, board C-Liked) ─────────────────────────────────────────────

    // The head's words sit in the page gutter (the content tier's, `ClassicTiers.PageOf`: the board's 36 at Standard), with the head rhythm's
    // 24 of air above the title and the meta line 4 under it (`ClassicTiers.HeadTop`, `ClassicTiers.HeadMetaGap`, tuned Classic F3).

    /// <summary>The air under the meta line: 4, so with the list chrome's own 8 of top air the bar sits 12 under the meta
    /// (24 + 52 + 4 + 16 + 4 + 8 + the 44 bar + 4 = the board's 156).</summary>
    public const float HeadPadBottom = 4f;

    /// <summary>The title's line (the 40/52 display face) and the meta line, one 16-DIP line (a loading meta shimmers in the same slot).</summary>
    public const float HeadTitleLine = 52f, HeadMetaLine = 16f;

    /// <summary>The head's loading bars (audit B9: the one-tree head shimmers with the list while the page has not answered): the measure the bars take
    /// their fractions of, and the title bar's height inside its 52 line box.</summary>
    public const float HeadSkeletonMeasure = 400f, HeadSkeletonTitleBar = 28f;

    /// <summary>The list's inset under the head: the page <paramref name="gutter"/> less the row plates' 8, so the plates start 8 before the
    /// head's words.</summary>
    public static float HeadListInset(float gutter) => gutter - Track.RowMetrics.RowInset;

    // ── the insights sheet (tuned Classic I1, board C-Liked) ──────────────────────────────────────────────────────────

    /// <summary>The sheet's width (the board's 320; production's 390 vertical-arm sheet is gone) …</summary>
    public const float SheetWidth = 320f;

    /// <summary>… or this share of what it covers when that is narrower, so a strip of the list stays visible beside it.</summary>
    public const float SheetCoverFraction = 0.76f;

    /// <summary>The sheet's width over a region this wide: <see cref="SheetWidth"/>, else <see cref="SheetCoverFraction"/> of the region. An
    /// UNMEASURED region (≤ 0 or NaN) answers <see cref="SheetWidth"/>; quantized to whole DIP so a per-pixel resize cannot churn the pane.</summary>
    public static float SheetWidthFor(float coveredWidth)
        => !(coveredWidth > 0f) ? SheetWidth : MathF.Max(1f, MathF.Min(SheetWidth, MathF.Floor(coveredWidth * SheetCoverFraction)));

    /// <summary>Where the two-column arm's sheet starts in the list column: under the list bar (the chrome's 8-DIP top air + the 44 bar), so
    /// the bar's Insights button stays reachable while the sheet is open.</summary>
    public static float SheetListTop => Spacing.S + Detail.VerticalLayout.ToolbarRowHeight;

    /// <summary>The sheet's padding on every side and the gap between its header row and its body.</summary>
    public static float SheetPad => ClassicTiers.PaneInset;

    /// <inheritdoc cref="SheetPad"/>
    public static float SheetGap => Spacing.M;

    /// <summary>The list bar's glyph toggle: its edge and its glyph.</summary>
    public const float SheetToggleEdge = 32f, SheetToggleGlyph = 15f;

    /// <summary>The header's × button: its edge and its glyph.</summary>
    public const float SheetCloseEdge = 32f, SheetCloseGlyph = 14f;

    /// <summary>The sheet's title: size and line height.</summary>
    public const float SheetTitleSize = 16f, SheetTitleLine = 22f;

    /// <summary>The cast under the sheet's left edge: the board's <c>-12px 0 32px rgba(0,0,0,.35)</c> as a gradient band (a real drop shadow
    /// on the pane would be clipped by the control's own pane clip).</summary>
    public const float SheetShadowW = 32f;

    /// <summary>The WinUI sheet spline (0.1, 0.9, 0.2, 1.0): the slide and the cast.</summary>
    public static readonly EasingSpec SheetSpline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);

    /// <summary>The docked pane's padding around the sheet's own 16 (board C-Liked-Huge: 24 / 20 overall): what the docked column adds.</summary>
    public static readonly Edges4 DockPadding = new(4f, 8f, 4f, 8f);

    /// <summary>The docked pane's slot easing its width open and closed on the sheet's timings (350 ms open, 120 ms close, the sheet spline), revealing
    /// the full-width column inside it, so the list beside it yields smoothly.</summary>
    public static readonly LayoutTransition DockReveal = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(350f, SheetSpline), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(120f, SheetSpline));

    /// <summary>The cast's gradient: clear at its left edge, 35 % black at the sheet's edge.</summary>
    public static ColorF SheetCastClear => ColorF.FromRgba(0, 0, 0, 0);

    /// <inheritdoc cref="SheetCastClear"/>
    public static ColorF SheetCastEdge => ColorF.FromRgba(0, 0, 0, 0x59);
}
