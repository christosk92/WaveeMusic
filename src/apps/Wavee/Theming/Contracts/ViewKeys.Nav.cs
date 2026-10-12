// ── Theming/Contracts/ViewKeys.Nav.cs — the Nav family of published view keys (themes plan 6.0, T5a) ──────────────────────
//
// T5a publishes the pane's key. The sidebar is one pane over six sub-surfaces; T5a moves the ROWS (the planner, the diff, the
// extents, the freeze and the row slot) and mounts the pane through the key. The heads, the flyouts and menus, the Edit
// outline, the drop cues and the library grid (T5b-f) stay core and reachable from the row slot through `INavRowHost` until
// each is given to the structure.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The navigation pane: the docked sidebar and the narrow-window drawer's copy. Mounted by the core frame (docked) and
    /// the core drawer (<see cref="NavPaneProps.InDrawer"/>). Props: <see cref="NavPaneProps"/>.</summary>
    public static readonly ViewKey<NavPaneProps> ShellNavPane = new("Shell.NavPane");
}
