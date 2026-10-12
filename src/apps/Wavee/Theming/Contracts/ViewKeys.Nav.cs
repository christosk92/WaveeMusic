// ── Theming/Contracts/ViewKeys.Nav.cs — the Nav family of published view keys (themes plan 6.0, T5a, T5b) ─────────────────
//
// T5a publishes the pane's key. The sidebar is one pane over six sub-surfaces; T5a moves the ROWS (the planner, the diff, the
// extents, the freeze and the row slot) and mounts the pane through the key. T5b moves the HEADS: the section header's chrome and
// the filter box ride the row slot (no key of their own: they are part of a row), and the two heads that stand beside the list get
// a key each (Your Library's head, the empty body). The flyouts and menus, the Edit outline, the drop cues and the library grid
// (T5c-f) stay core and reachable from the row slot through `INavRowHost` until each is given to the structure.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The navigation pane: the docked sidebar and the narrow-window drawer's copy. Mounted by the core frame (docked) and
    /// the core drawer (<see cref="NavPaneProps.InDrawer"/>). Props: <see cref="NavPaneProps"/>.</summary>
    public static readonly ViewKey<NavPaneProps> ShellNavPane = new("Shell.NavPane");

    /// <summary>Your Library's fixed head above the list (Home, the page dropdown, the chips, the toolbar, the breadcrumb, the retry
    /// banner and the empty band; the compact rail's form is Home, the search tile and the separator). Mounted by the Library
    /// layout's session. Props: <see cref="NavLibraryHeadProps"/>.</summary>
    public static readonly ViewKey<NavLibraryHeadProps> ShellNavLibraryHead = new("Shell.NavLibraryHead");

    /// <summary>The pane body when the plan has no rows: the authored-empty pane, or the rail's spacer. Mounted by the core pane.
    /// Props: <see cref="NavEmptyProps"/>.</summary>
    public static readonly ViewKey<NavEmptyProps> ShellNavEmpty = new("Shell.NavEmpty");
}
