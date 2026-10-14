// ── Themes/Classic/Structure/Rules/NavLibraryHeadLayout.cs — the numbers of Classic's Your Library head (themes plan 6.1, T5f) ────
//
// Moved from core's `SidebarLibraryHeadRules` (`Shell/Sidebar.Library.cs`) by T5f: the head's rows, chips and toolbar are pictures, so
// their heights, insets and widths are the structure's, and so is the answer to "which toolbar shape fits this pane" (it is measured
// in the toolbar's own control widths). What stayed core is what DECIDES: the pages, the chips, the sort rules, the focus ring and
// the Tab order (`SidebarLibraryHeadRules`, which also owns the `ToolbarShape` vocabulary the Tab order reads). The Library grid's
// columns are `SidebarGeometry.GridColumns`, asked through `NavRowKit.GridColumns`.

namespace Wavee.Themes.Classic;

/// <summary>The constants and the toolbar-shape rule of Classic's Your Library head.</summary>
public static class ClassicLibraryHeadLayout
{
    public const float HomeRowHeight = 40f;       // row A 36 + 2·2 margin
    public const float SeparatorHeight = 8f;
    public const float DropdownRowHeight = 40f;
    public const float ChipRowHeight = 40f;
    public const float ChipHeight = 32f;
    public const float ChipGap = 8f;
    public const float ToolbarHeight = 40f;
    public const float ToolbarControl = 32f;
    public const float RuleHeight = 1f;

    /// <summary>The dropdown title's x in pane space (design P.2a: "at the header's x", pane 14) and its 8-px hit inset.</summary>
    public const float TitleX = SidebarGeometry.PaneEdge + SidebarGeometry.HeaderTextX;
    public const float TitleHitInset = 8f;
    public const float TitleChevron = 10f;

    /// <summary>The chip row's and the toolbar's lead/trail inset in pane space (the chips' first edge sits under the title).</summary>
    public const float BandInsetX = 12f;

    /// <summary>The whole head above the list: 3 (top inset) + 40 + 8 + 40 + 40 + 40 + 1 = 172.</summary>
    public const float HeadHeight = SidebarGeometry.PaneTopInset + HomeRowHeight + SeparatorHeight + DropdownRowHeight
                                    + ChipRowHeight + ToolbarHeight + RuleHeight;

    // ── the toolbar's shape at the pane width (design: the sort/view row keeps icons only below 240) ─────────────────

    /// <summary>IconButton's <c>ControlSize.Small</c> box (search, the icon-only sort, ⋯).</summary>
    public const float ToolbarIconButton = 28f;
    public const float ToolbarGap = 4f;

    /// <summary>The labelled sort button's chrome around its label: 8 + 16 glyph + 8 gap + 8.</summary>
    public const float SortButtonChrome = 40f;

    /// <summary>Below this pane width List/Grid leave the toolbar (they stay in ⋯ › View, which LibraryOptions always has).</summary>
    public const float FoldViewBelow = 240f;

    /// <summary>The toolbar's content lane: the pane minus the head's 4+4 pad and the toolbar's own lead inset (8).</summary>
    public static float ToolbarLane(float paneWidth) => paneWidth - 2f * SidebarGeometry.PaneEdge - (BandInsetX - SidebarGeometry.PaneEdge);

    /// <summary>The summed control widths plus gaps for a shape (the spacer is 0 at the minimum). The label is measured
    /// with the row label's estimate (<see cref="SidebarLabelFit.AverageCharWidth"/>).</summary>
    public static float ToolbarWidth(SidebarLibraryHeadRules.ToolbarShape shape, string sortLabel)
    {
        float sort = shape == SidebarLibraryHeadRules.ToolbarShape.Full ? SortButtonChrome + sortLabel.Length * SidebarLabelFit.AverageCharWidth : ToolbarIconButton;
        float view = shape == SidebarLibraryHeadRules.ToolbarShape.Folded ? 0f : 2f * ToolbarControl;
        int controls = shape == SidebarLibraryHeadRules.ToolbarShape.Folded ? 4 : 6;   // search, sort, [List, Grid,] +, ⋯
        return ToolbarIconButton + sort + view + ToolbarControl + ToolbarIconButton + (controls - 1) * ToolbarGap;
    }

    /// <summary>The widest shape that fits: Folded below <see cref="FoldViewBelow"/>; the label only when the whole row
    /// with it fits the lane ("Recently added" at the 320 default does not: icon-only there).</summary>
    public static SidebarLibraryHeadRules.ToolbarShape ShapeOf(float paneWidth, string sortLabel)
    {
        if (!float.IsFinite(paneWidth) || paneWidth < FoldViewBelow) return SidebarLibraryHeadRules.ToolbarShape.Folded;
        return ToolbarWidth(SidebarLibraryHeadRules.ToolbarShape.Full, sortLabel) <= ToolbarLane(paneWidth)
            ? SidebarLibraryHeadRules.ToolbarShape.Full
            : SidebarLibraryHeadRules.ToolbarShape.IconSort;
    }
}
