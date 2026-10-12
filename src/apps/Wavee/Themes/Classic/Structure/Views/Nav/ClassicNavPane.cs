// ── Themes/Classic/Structure/Views/Nav/ClassicNavPane.cs — Classic's `Shell.NavPane` (themes plan 6.1, T5a) ────────────────
//
// The pane is a core host and a theme view. The core pane (`Sidebar.PaneHost` -> the layout mode -> `PaneView`) keeps every hook,
// effect and state machine of the sidebar (see `NavPaneProps`); the structure supplies the LIST, and this template is where it
// does: it hands the core builder Classic's `NavRowKit` (`ClassicNavRows`: the planner, the diff, the analytic extents, the
// mid-drag freeze and the row slot) and returns what the core builds around it. Nothing is drawn here -- the rows are
// `ClassicNavRow`, the pane's layer, head and footer are still the core's until T5b-f give them to the structure.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's navigation pane.</summary>
internal static class ClassicNavPane
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellNavPane"/>.</summary>
    public static Element Template(NavPaneProps props, in ViewContext ctx) => props.Mount(ClassicNavRows.Kit);
}
