// ── Themes/Classic/Structure/Rules/SidebarGeometry.cs — Classic's sidebar row ladder (themes plan 5.2 BP P5, 6.1, T5a) ─────
//
// Moved out of core by T5a from `Shell/Sidebar.cs` (`SidebarRowGeometry`). The numbers are the structure's: a tuned-Classic PR
// edits them HERE. Literals are allowed in this file (WVTH003 exempts `Rules\**`), so the row slot (`Views\Nav\ClassicNavRow.cs`),
// the planner's analytic extents (`SidebarPlanner.cs`) and the pill read their sizes from this class and never inline them.
//
// TRANSITIONAL MIRROR. The pane's other sub-surfaces are still core until T5c-f (the flyouts and menus, the Edit outline, the
// drop cues, the library grid; T5b moved the heads) and so are the shared row primitives in `Shell/Sidebar.UI.Rows.cs`, and they cannot
// name a structure. Core keeps `SidebarRowGeometry`: the plan queries (which are no pixels: `TrySectionBodyRange`,
// `FolderHeaderIndexOf`, ...) and a copy of exactly the ladder members those readers still use. `SidebarGeometryParityTests` pins
// every shared member equal, so the two cannot drift; the last core reader to move (T5f) deletes the copy. The band widths are
// not here: they are `NavPanePolicy` (T3).

namespace Wavee.Themes.Classic;

/// <summary>THE ONE ROW LADDER, drawn to WinUI NavigationView (NavigationView_themeresources.xaml, "TR"): 36-px rows in
/// a 4,2 margin (TR:217, TR:228), a 40-px icon column whose centre sits at pane x 24 (TR:612), the label at pane x 48
/// (TR:251), trailing content ending at pane W − 18 (TR:604), a 40-px chevron column at pane W − 44..W − 4 (TR:617), 31-px
/// folder indents (NavigationViewItemBase.h:63) and the 3×16 r2 pill at slot x 31·depth (TR:220-222). Numbers are SLOT
/// space (the list's one 4-px inset is <see cref="PaneEdge"/>). Engine-free so Wavee.Tests pins every one.</summary>
public static class SidebarGeometry
{
    // ── the pane ──
    /// <summary>The list's horizontal inset (4) — WinUI's item margin X, applied ONCE around the list
    /// (<c>PaneMetrics.PanePad</c>), never per row.</summary>
    public const float PaneEdge = 4f;
    /// <summary>The pane content grid's top margin (TR:233's −1,3 with the −1 dropped: no content border to tuck under).</summary>
    public const float PaneTopInset = 3f;
    /// <summary>The vertical half of the 4,2 item margin, carried by every row.</summary>
    public const float RowMarginY = 2f;
    /// <summary>The compact rail (TR:208 NavigationViewCompactPaneLength).</summary>
    public const float RailWidth = 48f;
    /// <summary>A compact-rail row: the 48 rail less the two 4-px insets.</summary>
    public const float TileWidth = 40f;

    // ── rows ──
    public const float RowHeight = 36f;
    public const float TwoLineRowHeight = 40f;
    /// <summary>The leading visual's box: the widest art (32); a glyph or the compact 24 art is centred in it.</summary>
    public const float ArtSlot = 32f;
    /// <summary>Slot x of the art's left edge: 7 clear of the 3-px selection pill, and the x the section headers' titles
    /// share, so a header, a cover and a glyph's column start on one line.</summary>
    public const float LeadInset = 10f;
    public const float IconColumn = LeadInset + ArtSlot;                         // 42
    public const float GlyphSize = 16f;
    /// <summary>Between the icon column and the label: 8, so a glyph's label sits 16 past the glyph (as WinUI's 48-column
    /// item) and a cover's 8 past the cover.</summary>
    public const float LabelGap = 8f;
    /// <summary>The ContentGrid's 14-px right margin (TR:604): trailing content ends at pane W − 18.</summary>
    public const float TrailingPad = 14f;
    /// <summary>The chevron column (TR:617): its −14 margin cancels <see cref="TrailingPad"/>.</summary>
    public const float ChevronColumn = 40f;
    public const float IndentStep = 31f;
    public const int MaxIndentDepth = 3;
    /// <summary>The trailing cluster's gap (count · pin mark · equalizer).</summary>
    public const float TrailingGap = 6f;

    // ── chrome rows ──
    public const float HeaderHeight = 40f;
    /// <summary>The header title's x: the rows' <see cref="LeadInset"/> (pane 14), so a title starts where the covers do.</summary>
    public const float HeaderTextX = LeadInset;
    /// <summary>A header's inline button (chevron, ⋯, +): 24×24.</summary>
    public const float HeaderButton = 24f;
    /// <summary>A separator: the 1-px rule plus its 0,3,0,4 margin (TR:223, TR:247), full pane width.</summary>
    public const float SeparatorHeight = 8f;
    public const float SeparatorLineTop = 3f;
    /// <summary>A quiet one-line hint (an empty Playlists, a search with no match): 40 tall in the 4,2 margin.</summary>
    public const float EmptyHintHeight = 40f;
    /// <summary>The tree's closing drop gutter.</summary>
    public const float TreeEndHeight = 24f;

    // ── the pill ──
    public const float PillW = 3f, PillH = 16f, PillRadius = 2f;

    public static float HeightOf(SidebarRowShape shape)
        => shape == SidebarRowShape.EntityTwoLine ? TwoLineRowHeight : RowHeight;

    /// <summary>A row's slot extent: its height plus the 2 + 2 margin (40 / 44 / 40).</summary>
    public static float PitchOf(SidebarRowShape shape) => HeightOf(shape) + 2f * RowMarginY;

    /// <summary>The leading visual's edge: glyph 16 · art 32 (Default) · art 24 (Compact).</summary>
    public static float ArtOf(SidebarRowShape shape) => shape switch
    {
        SidebarRowShape.Glyph => GlyphSize,
        SidebarRowShape.EntityTwoLine => 32f,
        _ => 24f,
    };

    public static int ClampDepth(int depth) => depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;

    /// <summary>The content indent for a nesting depth: 31 per level, capped at 3 (93). The FILL stays full width; only
    /// pill, icon and label move (NavigationViewItem.cpp:894-902).</summary>
    public static float IndentFor(int depth) => IndentStep * ClampDepth(depth);

    /// <summary>The pill's x in slot space (pane 4 + 31·depth).</summary>
    public static float PillX(int depth) => IndentFor(depth);

    /// <summary>The pill's y inside a slot whose row is <paramref name="rowHeight"/> tall: centred on the row, below its
    /// 2-px top margin.</summary>
    public static float PillTop(float rowHeight) => RowMarginY + (rowHeight - PillH) * 0.5f;

    /// <summary>Where the drop caret for depth <paramref name="depth"/> starts (slot space): the same x as the pill, so
    /// "insert here at this depth" lines up with the row it describes. Read backwards by the drop resolver.</summary>
    public static float TreeContentX(int depth) => IndentFor(depth);

    /// <summary>Pane-space rulers (diagnostics, tests).</summary>
    public const float IconCentreX = PaneEdge + LeadInset + ArtSlot * 0.5f;     // 30
    public const float LabelX = PaneEdge + IconColumn + LabelGap;               // 54
    public static float TrailingRight(float paneWidth) => paneWidth - PaneEdge - TrailingPad;   // W − 18
    public static float ChevronLeft(float paneWidth) => paneWidth - PaneEdge - ChevronColumn;   // W − 44

    /// <summary>A subtitle line is drawn only in a two-line row and only when there is text.</summary>
    public static bool SubtitleVisible(SidebarRowShape shape, string? subtitle)
        => shape == SidebarRowShape.EntityTwoLine && subtitle is { Length: > 0 };

    // ── pane-level numbers the row slot and the grid strip read ──

    /// <summary>The list's two horizontal insets (4 + 4): the width a row's content has less than the pane.</summary>
    public const float PaneInsetH = PaneEdge * 2f;

    /// <summary>Grid cells stay media-card sized at the 460-DIP maximum.</summary>
    public const float GridCellMax = 160f;

    /// <summary>The trailing width a row's label gives up to its count (the quiet badge).</summary>
    public const float CountTrail = 28f;

    /// <summary>A folder's 40-px chevron column replaces the 14-px trailing pad: the trailing width its label gives up.</summary>
    public const float FolderTrail = ChevronColumn - TrailingPad;

    // ── the pinned band's drop cue (the empty Pinned band while a pinnable drag is live) ──

    /// <summary>The dashed ring's dash and gap (1-px AccentDefault).</summary>
    public const float DropBandDashOn = 4f, DropBandDashOff = 2f;

    // ── the "into" plate (W16) ──

    /// <summary>The plate's radius and the accent's alpha behind it (accent at 0.18 with a 1-DIP accent border).</summary>
    public const float DropPlateRadius = 4f, DropPlateAlpha = 0.18f;

    // ── grid strip fallback (issue #84) ──
    // The planner owns the column COUNT; re-deriving it from width here would disagree with how the planner already
    // sliced entries into column-wide strips. What the 180-DIP floor (down from 240) broke is that the planned count
    // can no longer be honoured without shrinking cells under the 40-DIP art floor, so the SLOT clamps the column
    // count DOWN as a function of the width it has to render into, wrapping the strip to more lines instead.

    /// <summary>The largest column count in <c>[1, plannedCols]</c> whose cells still clear <paramref name="minCell"/>
    /// at <paramref name="availableWidth"/>. Never exceeds <paramref name="plannedCols"/> and never returns less
    /// than 1.</summary>
    public static int GridFallbackColumns(int plannedCols, float availableWidth, float gap, float minCell)
    {
        int cols = plannedCols < 1 ? 1 : plannedCols;
        for (int c = cols; c > 1; c--)
        {
            float edge = (availableWidth - gap * (c - 1)) / c;
            if (edge >= minCell) return c;
        }
        return 1;
    }
}
