// ── Themes/Classic/Structure/Views/Nav/WinUiNavPane.cs — Classic's `Shell.NavPane` (themes plan 6.1, T5a) ────────────────
//
// The pane is a core host and a theme view. The core pane (`Sidebar.PaneHost` -> the layout mode -> `PaneView`) keeps every hook,
// effect and state machine of the sidebar (see `NavPaneProps`); the structure supplies the LIST, and this template is where it
// does: it hands the core builder Classic's `NavRowKit` (`WinUiNavRows`: the planner, the diff, the analytic extents, the
// mid-drag freeze, the row slot and the boxes the pane is built in) and returns what the core builds around it. Nothing is drawn
// here -- the rows are `WinUiNavRow`, the heads `WinUiNavHead` / `WinUiNavLibraryHead`, the pane's layer, root, context-menu
// shield and list inset `WinUiNavFrame`.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's navigation pane.</summary>
internal static class WinUiNavPane
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellNavPane"/>.</summary>
    public static Element Template(NavPaneProps props, in ViewContext ctx)
        => props.Mount(props.InDrawer && ctx.Tier().Window == WidthTier.Compact ? WinUiNavRows.TouchKit : WinUiNavRows.Kit);
}
