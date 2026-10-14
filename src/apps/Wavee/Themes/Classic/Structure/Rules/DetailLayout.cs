// ── Themes/Classic/Structure/Rules/DetailLayout.cs — the numbers of Classic's detail frame (themes plan 6.1, T9a) ─────────────────────────────────
//
// What `Entities\Detail.UI.cs`, `Detail.UI.Hero.cs` and `Detail.Insights.cs` held as literals and file-level constants beside the pictures they size,
// moved with those pictures: the rail's cover bucket and floor, the collapsed strip's cover floor and chevron height, the vertical show header's
// cover and info measure, the attribution run's type, and the insights sheet's geometry (grip, toggle, close, shadow, spline, header title, the
// scrim's cast). They are data, not decisions (WVTH003 allows literals only in `Rules\**`).
//
// WHAT IS NOT HERE, AND WHY. The width bands of the rail (the breakpoint ladder `Detail.Breakpoints`, the rest, minimum, maximum and page-aware cap of
// `Detail.RailPolicy`, the collapse detent) and the hero's arithmetic (`Detail.VerticalLayout`, `BandLayout`, `Skeleton.RailPlanFor`, `RailLayout`)
// are DECISIONS the core host and the table both read and the skeleton must agree with row for row, so they stay core, pure and unit-tested
// (`DetailRailPolicyTests`, `DetailSkeletonGeometryTests`); the views are handed their results through the props, and no view compares a width with
// a number (WVTH013). M8 moves the rail policy and its state into Classic's own options (plan §5.3); the hero's per-kind knobs
// (`SlimHead` / `HoverHeart` / `PlainRows`) are Liked's (T9c) and are not on this branch yet.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's detail frame views.</summary>
public static class ClassicDetailLayout
{
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

    // ── the insights sheet ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The header row's vertical padding.</summary>
    public static float SheetHeaderPadY => Spacing.M;

    /// <summary>The sheet's horizontal padding.</summary>
    public static float SheetPadX => Spacing.L;

    /// <summary>The body's top padding.</summary>
    public static float SheetBodyPadTop => Spacing.M;

    /// <summary>The body's bottom padding.</summary>
    public static float SheetBodyPadBottom => Spacing.XL;

    /// <summary>The inert left-edge grip (drawn, never wired): width and height.</summary>
    public const float SheetGripW = 4f, SheetGripH = 36f;

    /// <summary>The grip's opacity.</summary>
    public const float SheetGripOpacity = 0.5f;

    /// <summary>The hero toolbar's toggle: its edge and its glyph.</summary>
    public const float SheetToggleEdge = 32f, SheetToggleGlyph = 15f;

    /// <summary>The header's × button: its edge and its glyph.</summary>
    public const float SheetCloseEdge = 28f, SheetCloseGlyph = 11f;

    /// <summary>The sheet's title: size and line height.</summary>
    public const float SheetTitleSize = 16f, SheetTitleLine = 22f;

    /// <summary>The scrim's cast under the sheet's left edge: the prototype's <c>-18px 0 48px</c> as a gradient band (a real drop shadow on the pane
    /// would be clipped by the control's own pane clip).</summary>
    public const float SheetShadowW = 28f;

    /// <summary>The WinUI sheet spline (0.1, 0.9, 0.2, 1.0): the slide, the scrim fade and the cast.</summary>
    public static readonly EasingSpec SheetSpline = EasingSpec.CubicBezier(0.1f, 0.9f, 0.2f, 1.0f);

    /// <summary>The cast's gradient: clear at its left edge, 24 % black at the sheet's edge.</summary>
    public static ColorF SheetCastClear => ColorF.FromRgba(0, 0, 0, 0);

    /// <inheritdoc cref="SheetCastClear"/>
    public static ColorF SheetCastEdge => ColorF.FromRgba(0, 0, 0, 0x3D);
}
