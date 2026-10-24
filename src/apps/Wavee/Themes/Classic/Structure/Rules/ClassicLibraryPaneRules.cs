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

    /// <summary>The pane header's STATED height: the cover plus its own padding (128 + the 16 pane inset + 12 = 156). Stated, not content-derived, because a content-derived header is a header that MOVES: the
    /// text column reaches past the cover the moment a title wraps to two lines, and everything under it — the command row, the column header, the first row — slid ~9 DIP down on those
    /// albums and back up on the next one. With one number the rows below start on the same DIP for every release, and the column that has to fit inside it is sized to fit.</summary>
    public static float PaneHeaderHeight => PaneCover + ClassicTiers.PaneInset + Spacing.M;

    /// <summary>The gap between the cover and the text column.</summary>
    public const float HeaderGap = 18f;

    /// <summary>The air under the header's cover (the tuned header: <see cref="Spacing.M"/>, 12).</summary>
    public static float HeaderPadBottom => Spacing.M;

    /// <summary>The Small header (C-Library-Small): a 96 cover bottom-aligned in a 120 band, 16 from the words, 10 over the command row (the board's flex gap).</summary>
    public const float SmallCover = 96f, SmallBand = 120f, SmallHeaderGap = 16f, SmallHeaderPadBottom = 10f;

    /// <summary>The Small pane's left inset (C-Library-Small: the section pads 8 on the left, beside the 16 divider column) and its title's rung (24 / 30 semibold).</summary>
    public const float SmallInsetLeft = 8f, SmallTitleSize = 24f, SmallTitleLine = 30f;

    /// <summary>The Small header's STATED height: the 120 band under the pane inset and over the 10 of air = 146.</summary>
    public static float SmallPaneHeaderHeight => SmallBand + ClassicTiers.PaneInset + SmallHeaderPadBottom;

    /// <summary>Between the header's text lines.</summary>
    public const float PaneHeaderGap = 3f;

    /// <summary>The meta line, 12.5 / 16.</summary>
    public const float PaneHeaderMetaSize = 12.5f, PaneHeaderMetaLine = 16f;

    /// <summary>The loading twin's four stand-in bars (tuned Classic X2): widths (eyebrow, title, attribution, meta) and heights (the small ones, the title).</summary>
    public const float SkeletonEyebrowW = 80f, SkeletonTitleW = 220f, SkeletonAttributionW = 120f, SkeletonMetaW = 140f, SkeletonSmallBarH = 11f, SkeletonTitleBarH = 26f;

    /// <summary>The line slots each bar is centred in: the real header's rungs (eyebrow 16, the 28/36 title, attribution 20; meta is
    /// <see cref="PaneHeaderMetaLine"/>), so the bars stand where the words land: 16 + 36 + 20 + 16 + three 3-DIP gaps = 97.</summary>
    public const float SkeletonEyebrowLine = 16f, SkeletonTitleLine = 36f, SkeletonAttributionLine = 20f;

    // ── the command row ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the verbs, the "Open album" link's own gap and vertical padding, and its glyph.</summary>
    public const float CommandsGap = 10f, OpenLinkGap = 6f, OpenLinkPadY = 6f, OpenGlyph = 14f;

    /// <summary>The save and ⋯ circles' box (the Compact tier's is 40: a target).</summary>
    public const float CommandBox = 36f, CompactCommandBox = 40f;

    // ── the selection swap ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The entering pane rises 6 DIP over 180 ms while the leaving one fades over 120 ms (faster out than in).</summary>
    public const float SwapInMs = 180f, SwapOutMs = 120f, SwapDy = 6f;

    // ── the counted shimmer ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The # bar (width), the clock bar (width), a Classic compilation's artist bar (width) and the bars' height; and the clock glyph of the
    /// header shim.</summary>
    public const float ShimNumW = 14f, ShimClockW = 30f, ShimArtistW = 120f, ShimBarH = 11f, ShimClockGlyph = 14f;

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

    // ── the Huge "Also by" aside (C-Library-Huge) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The aside's covers (140), the air over its heading (4) and between the heading and the tiles (12).</summary>
    public const float AlsoByAsideArt = 140f, AlsoByAsideTop = 4f, AlsoByAsideTitleGap = 12f;

    /// <summary>A tile's width: the 140 cover in the surface's 8-DIP plate padding on each side.</summary>
    public static float AlsoByAsideCardW => AlsoByAsideArt + 2f * SurfaceGeometry.ShelfPlatePad;

    /// <summary>The tiles' column gap: the board's 20 between covers less the two plates' padding between them.</summary>
    public static float AlsoByAsideColGap => 20f - 2f * SurfaceGeometry.ShelfPlatePad;

    /// <summary>A tile's pinned shell height: the shelf extent of the 156 card less the shelf gutter over and under the plate (one caption line).</summary>
    public static float AlsoByAsideCardH => SurfaceGeometry.ShelfHeight(AlsoByAsideCardW, 1f, captionLines: 1, metaLine: false)
                                            - SurfaceGeometry.ShelfGutterTop - SurfaceGeometry.ShelfGutterBottom;

    /// <summary>The aside column's whole width for the record's <paramref name="aside"/> (300) and <paramref name="gap"/> (32): the gap, the covers' column and the
    /// pane's right inset. The tiles' plates bleed 8 past the covers on each side, so the column pads <see cref="AlsoByAsideLead"/> before them and
    /// <see cref="AlsoByAsideTrail"/> after: the covers stand exactly <paramref name="gap"/> right of the rows and the column is <paramref name="aside"/> wide at the covers' edges.</summary>
    public static float AlsoByAsideOuter(float aside, float gap) => aside <= 0f ? 0f : gap + aside + ClassicTiers.PaneInset;

    /// <summary>The aside's lead padding before the plates (the gap less a plate's bleed).</summary>
    public static float AlsoByAsideLead(float gap) => gap - SurfaceGeometry.ShelfPlatePad;

    /// <summary>The aside's trailing padding after the plates (the pane inset less a plate's bleed).</summary>
    public static float AlsoByAsideTrail => ClassicTiers.PaneInset - SurfaceGeometry.ShelfPlatePad;
}
