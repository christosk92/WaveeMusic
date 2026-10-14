// ── Themes/Classic/Structure/Rules/ClassicLibraryRules.cs — the numbers of Classic's library page (themes plan 6.1, T10c1; P22) ──────────────────
//
// What `Entities\User.Page.Library.cs` and `Entities\User.UI.cs` held beside the pictures they size: the two column grips' ranges, the collapsed drill's motion, the toolbar's filter box and
// count, the view toggle and its trimmed panel, the navigator's shimmer, row, card and search-row type, and the keyed layout transition of a hit. They are data, not decisions (WVTH003 allows
// literals only in `Rules\**`); the collapse LADDER is `Tiers.Library.cs`, the navigator's remount identity and its row extents (`LibraryNavMount`, `User.NavRowPlate`) stay core because the
// shape memo, the letters and the layout seed read them.
//
// The persisted column widths (`library.<kind>.leftw/midw`) are the HOST's state; the ranges the grips clamp them to are the structure's.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's library page.</summary>
public static class ClassicLibraryRules
{
    // ── the column seams ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The navigator column's grip range (the persisted <c>LeftW</c> is clamped to it by the splitter).</summary>
    public const float LeftMin = 240f, LeftMax = 560f;

    /// <summary>The search layout's middle column grip range.</summary>
    public const float MidMin = 300f, MidMax = 620f;

    /// <summary>The 1-DIP hairline centred behind the splitter's hover thumb.</summary>
    public const float SeamHairline = 1f;

    /// <summary>The search layout's album column floor while the right rail is open, closed; and the track column's floor.</summary>
    public const float SearchAlbumsMinRailOpen = 220f, SearchAlbumsMin = 300f, SearchTracksMin = 220f;

    // ── the collapsed drill ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drill's fade (opacity only) and the arriving leaf's slide in from the right.</summary>
    public const float DrillMs = 160f, DrillDx = 12f;

    // ── the toolbar ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The filter box's floor height and its fill cap.</summary>
    public const float FilterMinHeight = 32f, FilterMaxFill = 9999f;

    /// <summary>The count beside the page title (14/20 tertiary); the search message shares the rung.</summary>
    public const float CountSize = 14f, CountLine = 20f;

    /// <summary>The view toggle's three cells: the edge, the corner and the glyph.</summary>
    public const float ViewIconEdge = 28f, ViewIconCorner = 4f, ViewIconGlyph = 15f;

    /// <summary>The trimmed panel's minimum width, the dimmed Size bank's opacity while a list view is on, and the four view cells (width, height, corner, gap).</summary>
    public const float PanelMinWidth = 200f, SizeBankDim = 0.4f, ViewCellW = 40f, ViewCellH = 30f, ViewCellCorner = 5f, ViewCellGap = 4f;

    /// <summary>The view cells' row padding.</summary>
    public static Edges4 ViewCellsPad => new(2f, 2f, 2f, 4f);

    /// <summary>The four view cells' glyph sizes (0 compact list, 1 list, 2 compact grid, 3 grid).</summary>
    public static readonly float[] ViewGlyphSizes = [14f, 16f, 12f, 15f];

    /// <summary>The reader's sort rail has three words (newest · oldest · a–z).</summary>
    public const int ReaderSortWords = 3;

    // ── the navigator ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The loading state's shimmer rows.</summary>
    public const int NavSkelRows = 8;

    /// <summary>The shimmer row's height (the compact row's plate).</summary>
    public const float NavSkelRowHeight = 40f;

    /// <summary>The list row's art edge and its album / show corner (an artist is a circle), the title rung and the subtitle rung (shared with the search rows).</summary>
    public const float RowArt = 40f, RowArtCorner = 5f, RowTitleSize = 14f, RowTitleLine = 20f, RowSubtitleSize = 12f, RowSubtitleLine = 16f;

    /// <summary>The grid card's padding around a round artist cover, the album cover corner, the title rung and the decode size of the fill-width cover.</summary>
    public const float CardPadRound = 16f, CardArtCorner = 6f, CardTitleSize = 12f, CardTitleLine = 16f, CardDecodePx = 256f;

    // ── the search columns ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hit columns' shimmer row count.</summary>
    public const int SkelRows = 6;

    /// <summary>The gap between hit rows.</summary>
    public const float HitGap = 2f;

    /// <summary>A hit's layout transition: 90 ms in (rising 3 DIP), 70 ms out.</summary>
    public const float HitEnterMs = 90f, HitEnterDy = 3f, HitExitMs = 70f;

    /// <summary>The facet count's ink fades to this fraction while a newer answer is coming.</summary>
    public const float RefiningAlpha = 0.4f;

    // ── Your Episodes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cold seed's rows.</summary>
    public const int SavedSeedRows = 6;
}
