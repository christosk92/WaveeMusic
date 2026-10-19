// ── Themes/WinUi/Structure/Rules/WinUiNavGeometry.cs — WinUI's navigation row ladder (P6 W2b) ─────────────────────────────────────────
//
// The numbers of the board `Shell-Nav` (NavigationView's pane): 36-px rows in a 4,2 margin, a 16-px glyph or thumbnail at slot x 12 (pane 16),
// the label 16 past it (pane 48), 28-px folder indents, the 3x16 r2 pill at the row's own left edge, 40-px section heads whose title sits on
// the bottom line (16 in, 6 up), a 24 "+" on Playlists, the 48 rail's 40x36 tiles and its 1-px separators. EVERY row is one line (the board
// has no two-line or cover-32 rows); a section's shape from the layout document (Classic's density) never reaches WinUI's rows: the rows read
// `OneLine`. Literals are allowed here (WVTH003 exempts `Rules\**`), so the row slot, the planner's extents and the pill read their sizes from
// this class. Engine-free so Wavee.Tests pins every one. Derived from Classic's `SidebarGeometry` (a theme may not reference another theme).

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>THE ONE ROW LADDER, drawn to the board `Shell-Nav`: 36-px rows in a 4,2 margin, the 16-px glyph or thumbnail at slot x 12 (pane 16), the
/// label at slot x 44 (pane 48), a 12-px chevron column at the row's end, 28-px folder indents and the 3x16 r2 pill at the row's own left edge. Numbers
/// are SLOT space (the list's one 4-px inset is <see cref="PaneEdge"/>). Engine-free so Wavee.Tests pins every one.</summary>
public static class WinUiNavGeometry
{
    // ── the pane ──
    /// <summary>The list's horizontal inset (4) — WinUI's item margin X, applied ONCE around the list
    /// (<see cref="ListInset"/>), never per row.</summary>
    public const float PaneEdge = 4f;
    /// <summary>The pane's top inset (the board's 4,0 root padding).</summary>
    public const float PaneTopInset = 4f;
    /// <summary>The vertical half of the 4,2 item margin, carried by every row.</summary>
    public const float RowMarginY = 2f;
    /// <summary>The compact rail (TR:208 NavigationViewCompactPaneLength).</summary>
    public const float RailWidth = 48f;
    /// <summary>The expanded pane's width from the Huge window tier on while the user never resized it (C-Shell-Huge and every Huge board: 320; the Standard
    /// boards draw 280, the store's own).</summary>
    public const float HugeRestWidth = 320f;
    /// <summary>A compact-rail row: the 48 rail less the two 4-px insets.</summary>
    public const float TileWidth = 40f;

    // ── rows ──
    public const float RowHeight = 36f;
    public const float TwoLineRowHeight = 36f;
    /// <summary>The leading visual's box: the widest art (32); a glyph or the compact 24 art is centred in it.</summary>
    public const float ArtSlot = 16f;
    /// <summary>Slot x of the art's left edge: 7 clear of the 3-px selection pill, and the x the section headers' titles
    /// share, so a header, a cover and a glyph's column start on one line.</summary>
    public const float LeadInset = 12f;
    public const float IconColumn = LeadInset + ArtSlot;                         // 28
    public const float GlyphSize = 16f;
    /// <summary>Between the icon column and the label: 8, so a glyph's label sits 16 past the glyph (as WinUI's 48-column
    /// item) and a cover's 8 past the cover.</summary>
    public const float LabelGap = 16f;
    /// <summary>A row's right padding where it has no chevron.</summary>
    public const float TrailingPad = 8f;
    /// <summary>The chevron column (TR:617): its −14 margin cancels <see cref="TrailingPad"/>.</summary>
    public const float ChevronColumn = 24f;
    /// <summary>The folder chevron's glyph (the board: 12).</summary>
    public const float ChevronGlyph = 12f;
    public const float IndentStep = 28f;
    public const int MaxIndentDepth = 3;
    /// <summary>The trailing cluster's gap (Unpin button · equalizer · count).</summary>
    public const float TrailingGap = 6f;

    // ── text rows (S0, S3, S4: covers off, board `C-Sidebar` covers false) ──
    /// <summary>A text row's lead inset: the folder glyph, or the label of a row without one, starts at slot x 12 (pane 16). A text row is
    /// <see cref="RowHeight"/> tall at pitch 40 (S3).</summary>
    public const float TextLead = 12f;
    /// <summary>A text row's leading glyph (a folder): 16, then the <see cref="LabelGap"/> 8, so its label sits at slot x 36 (pane 40). A child
    /// row is indented <see cref="IndentStep"/> per level, so depth 1 starts at slot x 43 (S4).</summary>
    public const float TextGlyph = GlyphSize;

    // ── chrome rows ──
    public const float HeaderHeight = 40f;
    /// <summary>The header title's x: the rows' <see cref="LeadInset"/> (pane 14), so a title starts where the covers do.</summary>
    public const float HeaderTextX = LeadInset;
    /// <summary>A header's inline button (chevron, ⋯, +): 24×24.</summary>
    public const float HeaderButton = 24f;
    /// <summary>A separator: the 1-px rule plus its 0,3,0,4 margin (TR:223, TR:247), full pane width.</summary>
    public const float SeparatorHeight = 17f;
    public const float SeparatorLineTop = 8f;
    /// <summary>A quiet one-line hint (an empty Playlists, a search with no match): 40 tall in the 4,2 margin.</summary>
    public const float EmptyHintHeight = 40f;
    /// <summary>The tree's closing drop gutter.</summary>
    public const float TreeEndHeight = 24f;

    /// <summary>A thumbnail's corner radius (the board's 2) and its decode bucket.</summary>
    public const float ThumbRadius = 2f;
    public const int ThumbDecodePx = 64;

    /// <summary>The Playlists head's "+" glyph.</summary>
    public const float HeaderPlusGlyph = 12f;

    /// <summary>A rail separator's horizontal inset inside the list (the board's 8 from the pane edge, less the list's own 4).</summary>
    public const float SeparatorInset = 4f;

    /// <summary>A section head: the title sits this far above the head's foot (the board's 6).</summary>
    public const float HeaderFootInset = 6f;

    /// <summary>A section head's right padding inside the list (the board's 8 from the pane edge, less the list's own 4).</summary>
    public const float HeaderEndInset = 4f;

    // ── the pill ──
    public const float PillW = 3f, PillH = 16f, PillRadius = 2f;

    /// <summary>Every WinUI row is one line, 36 tall, whatever shape the layout document gives its section.</summary>
    public static float HeightOf(SidebarRowShape shape) => RowHeight;

    /// <summary>A row's slot extent: its height plus the 2 + 2 margin (40 / 44 / 40).</summary>
    public static float PitchOf(SidebarRowShape shape) => HeightOf(shape) + 2f * RowMarginY;

    /// <summary>The leading visual's edge: glyph 16 · art 32 (Default) · art 24 (Compact, and a text row's rail tile: the rail always shows
    /// covers).</summary>
    public static float ArtOf(SidebarRowShape shape) => GlyphSize;

    public static int ClampDepth(int depth) => depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;

    /// <summary>The content indent for a nesting depth: 28 per level, capped at 3 (93). The FILL stays full width; only
    /// pill, icon and label move (NavigationViewItem.cpp:894-902).</summary>
    public static float IndentFor(int depth) => IndentStep * ClampDepth(depth);

    /// <summary>The pill's x in slot space: the row's own left edge at every depth (the board draws it at left 0 on a child row too).</summary>
    public static float PillX(int depth) => 0f;

    /// <summary>The pill's y inside a slot whose row is <paramref name="rowHeight"/> tall: centred on the row, below its
    /// 2-px top margin.</summary>
    public static float PillTop(float rowHeight) => RowMarginY + (rowHeight - PillH) * 0.5f;

    /// <summary>Where the drop caret for depth <paramref name="depth"/> starts (slot space): the same x as the pill, so
    /// "insert here at this depth" lines up with the row it describes. Read backwards by the drop resolver.</summary>
    public static float TreeContentX(int depth) => LeadInset + IndentFor(depth);

    /// <summary>A text row (covers off): no art in the pane, a glyph only on a folder or a route pin.</summary>
    public static bool IsText(SidebarRowShape shape) => false;

    /// <summary>The label's slot x at depth 0: a text row's lead (+ its glyph and gap when it has one), every other row's icon column and
    /// gap (slot 50).</summary>
    public static float LabelX(SidebarRowShape shape, bool glyph)
        => IsText(shape) ? TextLead + (glyph ? TextGlyph + LabelGap : 0f) : IconColumn + LabelGap;

    /// <summary>A subtitle line is drawn only in a two-line row and only when there is text.</summary>
    public static bool SubtitleVisible(SidebarRowShape shape, string? subtitle) => false;

    // ── pane-level numbers the row slot and the grid strip read ──

    /// <summary>THE PANE'S ONE INSET: the list's 4-px WinUI item margin and the 3-px content-grid top, around the virtualized list and
    /// nowhere else. Every band above or below the list reproduces its horizontal 4. Core reads it as <c>NavRowKit.ListInset</c>.</summary>
    public static readonly Edges4 ListInset = new(PaneEdge, PaneTopInset, PaneEdge, 0f);

    /// <summary>The list's two horizontal insets (4 + 4): the width a row's content has less than the pane.</summary>
    public const float PaneInsetH = PaneEdge * 2f;


    // ── the trailing cluster's widths, for the label-fit estimate (S8) ──

    /// <summary>The count's slot (board `C-Sidebar`: the count and the hover "…" share one 26-wide slot).</summary>
    public const float CountSlot = 26f;
    /// <summary>The playing row's equalizer: three 2.5-DIP bars at gap 2, rounded up.</summary>
    public const float EqualizerWidth = 12f;
    /// <summary>A folder row's own "+" (laid out always; it only fades in on hover or under a drag): 20 box, 12 glyph.</summary>
    public const float FolderPlusBox = 20f, FolderPlusGlyph = 12f;

    /// <summary>The width the trailing cluster takes from the label (S8: the tooltip estimate counts every part the row lays out,
    /// the Unpin button and the count slot included): the cluster's lead margin, each part, and the gaps between them. 0 for no
    /// part. <paramref name="trailing"/> is the width of the row's own trailing content (a count slot, a mark), 0 for none.</summary>
    public static float TrailingWidth(bool playing, bool pinned, float trailing)
    {
        int parts = (playing ? 1 : 0) + (pinned ? 1 : 0) + (trailing > 0f ? 1 : 0);
        if (parts == 0) return 0f;
        return TrailingGap * parts                       // the lead margin + the gaps between the parts
               + (playing ? EqualizerWidth : 0f) + (pinned ? PinButton : 0f) + MathF.Max(0f, trailing);
    }

    /// <summary>S2/S5: does a row with a hover "…" keep an EMPTY count slot? The "…" takes the 26-wide count slot on hover; a row with no
    /// count but an Unpin button or an equalizer keeps the slot anyway, so the "…" never lands on them (the button must stay clickable).</summary>
    public static bool ReservesSlot(bool overflow, bool hasTrailing, bool pinned, bool playing)
        => overflow && !hasTrailing && (pinned || playing);

    /// <summary>A folder's 40-px chevron column replaces the 14-px trailing pad: the trailing width its label gives up.</summary>
    public const float FolderTrail = ChevronColumn - TrailingPad;

    // ── the row primitive (`WinUiEntityRow`) ──

    /// <summary>The hover "…" box. It overlays the row's count slot and costs the label nothing: the count (or a trailing mark) cross-fades
    /// with it on hover (S2); the Unpin button and the equalizer stay. Its radius is half its edge; its glyph is 14.</summary>
    public const float OverflowButton = 26f, OverflowButtonCorner = 13f, OverflowGlyph = 14f;

    /// <summary>A disabled row fades as a whole (TR:501).</summary>
    public const float DisabledOpacity = 0.3f;

    /// <summary>The title's line height in a two-line row.</summary>
    public const float TwoLineLabelHeight = 18f;

    /// <summary>S5: a pinned entry's pin is a real 24-px "Unpin" button (board `C-Sidebar` `s.pin`: 24 box, r4, 12 glyph, tertiary) before
    /// the equalizer and the count slot.</summary>
    public const float PinButton = 24f, PinGlyph = 12f;

    /// <summary>An unavailable pin's warning mark (its trailing content, in the count slot).</summary>
    public const float WarningMark = 12f;

    // ── the offline pin (S9, board `C-Sidebar` state offline) ──

    /// <summary>An offline pin's mark: the cloud-off glyph (Segoe Fluent Icons U+EC9C, absent from the engine's glyph table) in a
    /// 24 box, 12 glyph, tertiary. It takes the Unpin button's place.</summary>
    public const string OfflineGlyph = "";
    public const float OfflineMarkBox = 24f, OfflineMarkGlyph = 12f;
    /// <summary>An offline pin's cover is kept, quieted (board: opacity 0.45); its title is tertiary.</summary>
    public const float OfflineArtOpacity = 0.45f;

    /// <summary>A track row's cover scrim and its ▶: the scrim is dark in BOTH themes, so the glyph is literal white (never
    /// <c>TextOnAccentPrimary</c>, which is black in dark). The glyph is 14, 10 in a Compact (one-line) row.</summary>
    public static ColorF TrackScrim => new(0f, 0f, 0f, 0.55f);
    public static ColorF TrackPlayInk => ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    public const float TrackPlayGlyph = 14f, TrackPlayGlyphCompact = 10f;

    // ── the pending row (`WinUiSkeletons`) ──

    /// <summary>The bars: 140×12 (+ 80×10 at gap 4 in a two-line row), r4.</summary>
    public const float SkeletonTitleW = 140f, SkeletonTitleH = 12f, SkeletonSubtitleW = 80f, SkeletonSubtitleH = 10f,
        SkeletonGap = 4f, SkeletonBarRadius = 4f;

    /// <summary>The label column of a row in a pane <paramref name="paneWidth"/> wide at <paramref name="depth"/>, with
    /// <paramref name="trailing"/> DIP of trailing content: the pane less its list insets, the row's indent, its label x (<see cref="LabelX"/>:
    /// the icon column and label gap, or a text row's lead) and trailing pad. The row decides from it whether to carry its label as a tooltip
    /// (<c>SidebarLabelFit.Overflows</c>).</summary>
    public static float LabelWidth(float paneWidth, int depth, float trailing, float labelX = IconColumn + LabelGap)
        => paneWidth - PaneInsetH - IndentFor(depth) - labelX - TrailingPad - trailing;

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

    // ── the touch ladder (the Compact window tier: targets are at least 40) ──

    /// <summary>A touch row: 40 tall and flush (the same 40 pitch as 36 + 2 + 2), so the hit target clears the Compact floor.</summary>
    public const float TouchRowHeight = 40f, TouchMarginY = 0f;

    /// <summary>A touch section head's "+" is the head's whole 40.</summary>
    public const float TouchHeaderButton = 40f;
}

/// <summary>The row height and vertical margin of one pane: <see cref="Standard"/> 36 + 2 (the board), <see cref="Touch"/> 40 + 0 (the Compact tier). The
/// pitch is 40 either way, so the planner's extents and the reorder bands do not care which a pane draws.</summary>
public readonly record struct WinUiNavMetrics(float RowHeight, float MarginY, float HeaderButton, float FolderPlus)
{
    public static WinUiNavMetrics Standard { get; } = new(WinUiNavGeometry.RowHeight, WinUiNavGeometry.RowMarginY, WinUiNavGeometry.HeaderButton, WinUiNavGeometry.FolderPlusBox);

    public static WinUiNavMetrics Touch { get; } = new(WinUiNavGeometry.TouchRowHeight, WinUiNavGeometry.TouchMarginY, WinUiNavGeometry.TouchHeaderButton, WinUiNavGeometry.TouchRowHeight);

    /// <summary>The pill's y inside a slot whose row is <see cref="RowHeight"/> tall: centred on the row, below its top margin.</summary>
    public float PillTop => MarginY + (RowHeight - WinUiNavGeometry.PillH) * 0.5f;
}
