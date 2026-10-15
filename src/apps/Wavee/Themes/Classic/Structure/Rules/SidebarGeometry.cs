// ── Themes/Classic/Structure/Rules/SidebarGeometry.cs — Classic's sidebar row ladder (themes plan 5.2 BP P5, 6.1, T5a) ─────
//
// Moved out of core by T5a from `Shell/Sidebar.cs` (`SidebarRowGeometry`). The numbers are the structure's: a tuned-Classic PR
// edits them HERE. Literals are allowed in this file (WVTH003 exempts `Rules\**`), so the row slot (`Views\Nav\ClassicNavRow.cs`),
// the planner's analytic extents (`SidebarPlanner.cs`) and the pill read their sizes from this class and never inline them.
//
// THERE IS NO CORE COPY (T5f ended the T5a mirror). Core's pane asks the kit for the few ladder answers it needs
// (`NavRowKit.TreeLadder`, `ListInset`, `PitchOf`, `PillHeight`, `GridColumns`; `ClassicNavRows` answers from here) and the nav-pane policy
// for the rail's width (`NavPanePolicy.RailWidth`, `ClassicPolicies.NavPane`). What stayed in core is no pixels: the plan queries
// (`SidebarPlanQueries`). The band widths are not here either: they are `NavPanePolicy` (T3).

using FluentGpu.Foundation;

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
    /// (<see cref="ListInset"/>), never per row.</summary>
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

    /// <summary>A subtitle line is drawn only in a two-line row and only when there is text.</summary>
    public static bool SubtitleVisible(SidebarRowShape shape, string? subtitle)
        => shape == SidebarRowShape.EntityTwoLine && subtitle is { Length: > 0 };

    // ── pane-level numbers the row slot and the grid strip read ──

    /// <summary>THE PANE'S ONE INSET: the list's 4-px WinUI item margin and the 3-px content-grid top, around the virtualized list and
    /// nowhere else. Every band above or below the list reproduces its horizontal 4. Core reads it as <c>NavRowKit.ListInset</c>.</summary>
    public static readonly Edges4 ListInset = new(PaneEdge, PaneTopInset, PaneEdge, 0f);

    /// <summary>The list's two horizontal insets (4 + 4): the width a row's content has less than the pane.</summary>
    public const float PaneInsetH = PaneEdge * 2f;

    /// <summary>Grid cells stay media-card sized at the 460-DIP maximum.</summary>
    public const float GridCellMax = 160f;

    // ── the trailing cluster's widths, for the label-fit estimate (S8) ──

    /// <summary>The count's slot (board `C-Sidebar`: the count and the hover "…" share one 26-wide slot).</summary>
    public const float CountSlot = 26f;
    /// <summary>The playing row's equalizer: three 2.5-DIP bars at gap 2, rounded up.</summary>
    public const float EqualizerWidth = 12f;
    /// <summary>A folder row's own "+" (laid out always; it only fades in on hover or under a drag): 20 box, 12 glyph.</summary>
    public const float FolderPlusBox = 20f, FolderPlusGlyph = 12f;

    /// <summary>The width the trailing cluster takes from the label (S8: the tooltip estimate counts every part the row lays out,
    /// the pin mark and the count slot included): the cluster's lead margin, each part, and the gaps between them. 0 for no
    /// part. <paramref name="trailing"/> is the width of the row's own trailing content (a count slot, a mark), 0 for none.</summary>
    public static float TrailingWidth(bool playing, bool pinned, float trailing)
    {
        int parts = (playing ? 1 : 0) + (pinned ? 1 : 0) + (trailing > 0f ? 1 : 0);
        if (parts == 0) return 0f;
        return TrailingGap * parts                       // the lead margin + the gaps between the parts
               + (playing ? EqualizerWidth : 0f) + (pinned ? PinMarkSize : 0f) + MathF.Max(0f, trailing);
    }

    /// <summary>A folder's 40-px chevron column replaces the 14-px trailing pad: the trailing width its label gives up.</summary>
    public const float FolderTrail = ChevronColumn - TrailingPad;

    // ── the row primitive (`ClassicEntityRow`) ──

    /// <summary>The hover "…" box. It overlays the row and costs the label nothing: a trailing count or pin mark fades out under it on
    /// hover. Its radius is half its edge; its glyph is 14.</summary>
    public const float OverflowButton = 26f, OverflowButtonCorner = 13f, OverflowGlyph = 14f;

    /// <summary>A disabled row fades as a whole (TR:501).</summary>
    public const float DisabledOpacity = 0.3f;

    /// <summary>The title's line height in a two-line row.</summary>
    public const float TwoLineLabelHeight = 18f;

    /// <summary>The quiet pin mark beside a pinned entry's count.</summary>
    public const float PinMarkSize = 12f;

    // ── the offline pin (S9, board `C-Sidebar` state offline) ──

    /// <summary>An offline pin's mark: the cloud-off glyph (Segoe Fluent Icons U+EC9C, absent from the engine's glyph table) in a
    /// 24 box, 12 glyph, tertiary. It takes the pin mark's place.</summary>
    public const string OfflineGlyph = "";
    public const float OfflineMarkBox = 24f, OfflineMarkGlyph = 12f;
    /// <summary>An offline pin's cover is kept, quieted (board: opacity 0.45); its title is tertiary.</summary>
    public const float OfflineArtOpacity = 0.45f;

    /// <summary>A track row's cover scrim and its ▶: the scrim is dark in BOTH themes, so the glyph is literal white (never
    /// <c>TextOnAccentPrimary</c>, which is black in dark). The glyph is 14, 10 in a Compact (one-line) row.</summary>
    public static ColorF TrackScrim => new(0f, 0f, 0f, 0.55f);
    public static ColorF TrackPlayInk => ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    public const float TrackPlayGlyph = 14f, TrackPlayGlyphCompact = 10f;

    // ── the pending row (`ClassicSkeletons`) ──

    /// <summary>The bars: 140×12 (+ 80×10 at gap 4 in a two-line row), r4.</summary>
    public const float SkeletonTitleW = 140f, SkeletonTitleH = 12f, SkeletonSubtitleW = 80f, SkeletonSubtitleH = 10f,
        SkeletonGap = 4f, SkeletonBarRadius = 4f;

    /// <summary>The label column of a row in a pane <paramref name="paneWidth"/> wide at <paramref name="depth"/>, with
    /// <paramref name="trailing"/> DIP of trailing content: the pane less its list insets, the row's indent, icon column, label gap and
    /// trailing pad. The row decides from it whether to carry its label as a tooltip (<c>SidebarLabelFit.Overflows</c>).</summary>
    public static float LabelWidth(float paneWidth, int depth, float trailing)
        => paneWidth - PaneInsetH - IndentFor(depth) - IconColumn - LabelGap - TrailingPad - trailing;

    // ── the pinned band's drop cue (the empty Pinned band while a pinnable drag is live) ──

    /// <summary>The dashed ring's dash and gap (1-px AccentDefault).</summary>
    public const float DropBandDashOn = 4f, DropBandDashOff = 2f;

    // ── the "into" plate (W16) ──

    /// <summary>The plate's radius and the accent's alpha behind it (accent at 0.18 with a 1-DIP accent border).</summary>
    public const float DropPlateRadius = 4f, DropPlateAlpha = 0.18f;

    /// <summary>The plate's border width (1 DIP, accent).</summary>
    public const float DropPlateBorderWidth = 1f;

    // ── the insertion caret (W16) ──

    /// <summary>The caret's stroke (2 DIP).</summary>
    public const float DropLineThickness = 2f;

    /// <summary>Its corner radius: enough to round a 2-DIP bar's ends without reading as a pill.</summary>
    public const float DropLineCorner = 1f;

    /// <summary>The terminal dot at the left cap (6 DIP): what makes a hairline read as an insertion caret rather than a divider.</summary>
    public const float DropDotSize = 6f;

    /// <summary>The caret's width: the row's content lane from <see cref="TreeContentX"/> to the row's right edge. ONE origin with
    /// the caret's transform and with the resolver's <c>PickDepth</c>.</summary>
    public static float DropLineWidth(float contentWidth, int depth)
    {
        float w = contentWidth - TreeContentX(depth);
        return w > 0f ? w : 0f;
    }

    /// <summary>The caret's Y inside its row: the TOP edge for Before (and the tree's end marker, whose whole row is the slot), the
    /// bottom edge for After.</summary>
    public static float DropLineY(SidebarDropKind kind, float rowHeight)
        => kind is SidebarDropKind.Before or SidebarDropKind.EndOfList
            ? 0f
            : MathF.Max(0f, rowHeight - DropLineThickness);

    // ── the Library grid's columns (design P.2) ──

    /// <summary>2–4 columns from the presented pane width, no size picker: as many <see cref="GridMinCell"/>-wide cells with
    /// <see cref="GridGap"/> gaps as the lane fits, clamped to [2, 4]. The lane is the pane less the list's 4+4 pad and the strip's 8+8
    /// inset. Core asks it through <c>NavRowKit.GridColumns</c> and hands the count to the planner.</summary>
    public const float GridMinCell = 116f, GridGap = 8f, GridStripInsetH = 16f;
    public const int GridMinColumns = 2, GridMaxColumns = 4;

    public static int GridColumns(float paneWidth)
    {
        float lane = paneWidth - PaneInsetH - GridStripInsetH;
        if (!float.IsFinite(lane) || lane <= 0f) return GridMinColumns;
        int n = (int)MathF.Floor((lane + GridGap) / (GridMinCell + GridGap));
        return Math.Clamp(n, GridMinColumns, GridMaxColumns);
    }

    /// <summary>A grid tile's cover: the cell less the shared grid card's plate padding on both sides. The cover is a fixed
    /// square (it is built from a <c>Cover</c> factory, not a fluid image), so it is sized from the cell the strip derived.</summary>
    public static float TileCover(float cell) => MathF.Max(0f, cell - 2f * SurfaceGeometry.ShelfPlatePad);

    /// <summary>The glyph a no-art tile's cover carries.</summary>
    public const float GridTileGlyph = 24f;

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
