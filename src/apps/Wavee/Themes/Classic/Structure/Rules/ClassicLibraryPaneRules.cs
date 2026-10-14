// ── Themes/Classic/Structure/Rules/ClassicLibraryPaneRules.cs — the numbers of Classic's library panes (themes plan 6.1, T10c1; P22) ──────────────
//
// What `Entities\Album.UI.cs` §4 (the library pane statics, library rework §5.5), `Album.Pane.cs` and `Album.Page.cs`'s `SurfaceMetrics` held beside the pictures they size: the pane header's STATED height
// and its type (the title and meta lines and the gap between them are numbers the header lays itself out FROM), the command row, the selection swap, the counted shimmer's bars and the "Also by" strip.
//
// Public, because `AlbumPageRulesTests` and `AlbumSurfaceRulesTests` pin the arithmetic.

using FluentGpu.Dsl;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's library album and show panes.</summary>
public static class ClassicLibraryPaneRules
{
    // ── the header ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane covers' one edge — 128, the show pane's too (it renders this same header).</summary>
    public const float PaneCover = 128f;

    /// <summary>The cover's decode size.</summary>
    public const int CoverDecodePx = 256;

    /// <summary>The pane header's STATED height: the cover plus its own padding (128 + 20 + 12 = 160). Stated, not content-derived, because a content-derived header is a header that MOVES: the
    /// text column reaches past the cover the moment a title wraps to two lines, and everything under it — the command row, the column header, the first row — slid ~9 DIP down on those
    /// albums and back up on the next one. With one number the rows below start on the same DIP for every release, and the column that has to fit inside it is sized to fit.</summary>
    public static float PaneHeaderHeight => PaneCover + Spacing.XL + Spacing.M;

    /// <summary>The gap between the cover and the text column.</summary>
    public const float HeaderGap = 18f;

    /// <summary>Between the header's text lines.</summary>
    public const float PaneHeaderGap = 3f;

    /// <summary>The title link. 24 / 30 and not the prototype's 28 / 34: at 34 two lines alone spent 129 of the 128 the cover leaves. A one-line title — the common case — reads the same at either size.</summary>
    public const float PaneTitleSize = 24f, PaneTitleLine = 30f;

    /// <summary>The title wraps to two lines and ellipsizes after them.</summary>
    public const int PaneTitleMaxLines = 2;

    /// <summary>The meta line, 12.5 / 16.</summary>
    public const float PaneHeaderMetaSize = 12.5f, PaneHeaderMetaLine = 16f;

    /// <summary>The loading twin's three stand-in bars: widths (eyebrow, title, attribution) and heights (the two small ones, the title).</summary>
    public const float SkeletonEyebrowW = 80f, SkeletonTitleW = 220f, SkeletonAttributionW = 120f, SkeletonSmallBarH = 11f, SkeletonTitleBarH = 26f;

    // ── the command row ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the verbs, the "Open album" link's own gap and vertical padding, and its glyph.</summary>
    public const float CommandsGap = 10f, OpenLinkGap = 6f, OpenLinkPadY = 6f, OpenGlyph = 14f;

    /// <summary>The save and ⋯ circles' box and the ⋯ glyph.</summary>
    public const float CommandBox = 36f;

    // ── the selection swap ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The entering pane rises 6 DIP over 180 ms while the leaving one fades over 120 ms (faster out than in).</summary>
    public const float SwapInMs = 180f, SwapOutMs = 120f, SwapDy = 6f;

    // ── the counted shimmer ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The # bar (width), the clock bar (width) and the bars' height; and the clock glyph of the header shim.</summary>
    public const float ShimNumW = 14f, ShimClockW = 30f, ShimBarH = 11f, ShimClockGlyph = 14f;

    /// <summary>The title bar in four lengths in rotation, so a column of rows reads as text and not as a fence.</summary>
    public static float ShimTitleW(int i) => (i & 3) switch { 0 => 208f, 1 => 264f, 2 => 176f, _ => 232f };

    // ── "Also by … in your library" ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "Also by" tile keeps its 96-DIP art: the surface adds its 8-DIP plate padding on every side, so the card is 96 + 16 wide and its strip is the surface's own shelf extent (not a
    /// stated 134).</summary>
    public const float AlsoByArt = 96f;

    /// <summary>The tile's width.</summary>
    public static float AlsoByCardW => AlsoByArt + 2f * SurfaceGeometry.ShelfPlatePad;

    /// <summary>The strip's height: the surface's own shelf extent.</summary>
    public static float AlsoByStripH => SurfaceGeometry.ShelfHeight(AlsoByCardW, 1f, captionLines: 1, metaLine: false);

    /// <summary>The surface's pinned shell height: the strip less the shelf gutter over and under the plate.</summary>
    public static float AlsoByCardH => AlsoByStripH - SurfaceGeometry.ShelfGutterTop - SurfaceGeometry.ShelfGutterBottom;

    /// <summary>The gap between the strip's eyebrow and its tiles, and the strip's edge fade.</summary>
    public const float AlsoByGap = 10f, AlsoByFadeBand = 24f;
}
