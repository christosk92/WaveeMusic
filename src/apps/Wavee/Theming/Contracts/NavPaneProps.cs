// ── Theming/Contracts/NavPaneProps.cs — the props of `Shell.NavPane` (themes plan 6.0, T5a) ────────────────────────────────
//
// THE PANE IS A CORE HOST AND A THEME VIEW. The core pane (`Sidebar.PaneHost` -> the layout mode -> `PaneView`) keeps every
// hook, effect and state machine of the sidebar: the binder pump, the account edge, the plan -> publish -> per-row epoch
// pipeline, the selection travel, disclosures, reorder bands, the drag peek and its freeze, the heads, flyouts, menus, Edit
// mode and the drop cues. What the structure supplies is the LIST (`NavRowKit`: planner, diff, extents, freeze and the row
// slot). So this record carries one thing the template needs: how to mount the pane around the kit the template hands it.
//
// The record is a static instance per mount point (docked and drawer), so it is equal across re-pushes and an equal record skips
// the template. Members are init-only; the delegate is a cached static.

using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellNavPane"/>.</summary>
public sealed record NavPaneProps
{
    /// <summary>Is this the narrow-window drawer's pane (always expanded, its own scroll key, no rail layer)? The docked pane is not.</summary>
    public required bool InDrawer { get; init; }

    /// <summary>Mounts the pane around the structure's rows: the core builds the pane, the template supplies the kit.</summary>
    public required Func<NavRowKit, Element> Mount { get; init; }
}
