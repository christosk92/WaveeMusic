// ── Shell/Sidebar.Planner.cs ───────────────────────────────────────────────────────────────────────────────────────
// what a row plan is FOR: the options a pane plans with
//
// Role: CORE
// Spec: sidebar-rework-implementation.md §P3.5 · design V.3, V.7, V.9, P.1, P.2a
//
// T5a moved the PLANNER itself (`SidebarRowPlanner`: (layout document × projection) → ONE flat row list, for the expanded pane
// and the compact rail alike) to the structure: `Themes/Classic/Structure/Rules/SidebarPlanner.cs`, reached by the pane through
// `NavRowKit`. The vocabulary it speaks stays core, because the pane and the Library mode decide the options and read the
// plan: `SidebarRow`, `SidebarRowPlan`, `SidebarProjectionInput`, `SidebarPlanBuffers` (Sidebar.cs) and this record.

namespace Wavee;

/// <summary>What the plan is FOR, beyond the document and the projection.</summary>
/// <param name="Mode">Compact plans the rail: no headers, a collapsed section is one <see cref="SidebarRowKind.SectionTile"/>,
/// trees show their top level only (a folder is a flyout).</param>
/// <param name="PinDropArmed">A pinnable drag is live: an empty Pinned plans its drop band.</param>
/// <param name="Filter">Library's active chip.</param>
/// <param name="GridColumns">Library's grid column count (derived from the pane width by the caller).</param>
/// <param name="FoldersInline">Library: an expanded PINNED folder lists its children under it (the wide pane, not drilled).
/// False while the narrow/drawer pane drills into folders instead (its folder click drills, so nothing expands inline).
/// Classic ignores it.</param>
/// <param name="Drilled">Library: the list shows ONE folder level (the narrow pane or the drawer drilled in). That level is
/// the folder's children only: no pin rows, no "Drop here to pin" band, no Liked Songs row — today's V3 hides both while
/// drilled (<c>PinsBandVisible</c> / <c>LikedVisible</c> are <c>!Drilled</c>), and a pinned playlist inside the folder
/// would otherwise show twice (the shaped level does not skip pins while drilled). Classic ignores it.</param>
public readonly record struct SidebarPlanOptions(
    SidebarPaneMode Mode = SidebarPaneMode.Expanded,
    bool PinDropArmed = false,
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    int GridColumns = 2,
    bool FoldersInline = true,
    bool Drilled = false);
