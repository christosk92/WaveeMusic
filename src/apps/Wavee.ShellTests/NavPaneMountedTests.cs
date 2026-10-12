// ── Wavee.ShellTests/NavPaneMountedTests.cs — the navigation pane's rows, mounted (themes plan 6.1, T5a) ──────────────────
//
// The pane is a core host and a theme view since T5a: the core pane (`Sidebar.PaneHost` -> the layout mode -> `PaneView`) plans,
// publishes and sweeps; the structure's `Shell.NavPane` template hands it the rows (`NavRowKit`: planner, diff, extents, freeze and
// the row slot). What only a mounted tree can say:
//
//   the registered pane   Classic registers the key; the real shell mounted on the installed theme is drawn through it (the
//                         mode node stands under the frame's sidebar column, the list scrolls, a Home row stands at the
//                         ladder's row height)
//   the drawer's copy     the narrow-window drawer mounts the SAME key with `InDrawer` set and its own props
//   the kit               the kit Classic hands the pane plans with Classic's planner (a plan built through it equals one built
//                         through the planner)
//
// The planner, the diff, the extents, the freeze and the ladder are pure and pinned in `Wavee.Tests` (`SidebarPlannerTests`,
// `SidebarWiringTests`, `SidebarRowGeometryTests`).

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class NavPaneMountedTests(ShellHost host)
{
    // ── the registered pane ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_nav_pane()
        => host.Run(() => Assert.Equal("classic.shell.nav-pane", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavPane.Id)));

    [Fact]
    public void The_docked_and_the_drawer_props_are_two_mount_points_of_the_one_key()
        => host.Run(() =>
        {
            Assert.False(Sidebar.DockedPaneProps.InDrawer);
            Assert.True(Sidebar.DrawerPaneProps.InDrawer);
            Assert.NotEqual(Sidebar.DockedPaneProps, Sidebar.DrawerPaneProps);
        });

    [Fact]
    public void The_mounted_shell_draws_the_pane_through_the_key_and_its_list_stands()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var original = Sidebar.Layout.Peek();
            try
            {
                if (original != SidebarLayoutId.Classic)
                {
                    Sidebar.SwitchLayout(SidebarLayoutId.Classic);
                    shell.RunFrames(60);
                }
                var mode = Node(shell, "sidebar.classic");
                var list = ScrollUnder(shell, mode);
                Assert.False(list.IsNull, "the pane's list does not scroll");
                Assert.True(shell.Host.Scene.TryGetScroll(list, out var scroll));
                Assert.True(scroll.ContentH > 0f, "the plan published no rows");
            }
            finally
            {
                if (original != SidebarLayoutId.Classic)
                {
                    Sidebar.SwitchLayout(original);
                    shell.RunFrames(60);
                }
            }
        });

    [Fact]
    public void The_home_row_stands_at_the_ladders_row_height_in_the_slot_the_kit_built()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var original = Sidebar.Layout.Peek();
            try
            {
                if (original != SidebarLayoutId.Classic)
                {
                    Sidebar.SwitchLayout(SidebarLayoutId.Classic);
                    shell.RunFrames(60);
                }
                var mode = Node(shell, "sidebar.classic");
                Until(shell, () => HomeRowsIn(shell, mode).Count == 1, "the pane's Home row " + SidebarCatalogue.HomeRoute);
                var home = shell.Host.Scene.AbsoluteRect(HomeRowsIn(shell, mode)[0]);
                Assert.Equal(SidebarGeometry.RowHeight, home.H, 0.5f);
                Assert.True(home.W > SidebarGeometry.TileWidth, "an expanded row is wider than a rail tile");
            }
            finally
            {
                if (original != SidebarLayoutId.Classic)
                {
                    Sidebar.SwitchLayout(original);
                    shell.RunFrames(60);
                }
            }
        });

    /// <summary>The nodes keyed as the Home route inside the pane (the title bar has a Home of its own).</summary>
    static List<NodeHandle> HomeRowsIn(Shell.HeadlessShell shell, NodeHandle mode)
    {
        var inPane = Subtree(shell.Host.Scene, mode).ToHashSet();
        return shell.FindPrefix(SidebarCatalogue.HomeRoute)
            .Where(f => f.Key == SidebarCatalogue.HomeRoute && inPane.Contains(f.Node))
            .Select(f => f.Node)
            .ToList();
    }

    // ── the kit ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_kits_planner_is_the_classic_planner()
        => host.Run(() =>
        {
            var doc = SidebarLayoutRules.Resolve(SidebarLayoutState.Default, SidebarLayoutId.Classic, SidebarDensity.Default);
            var input = new SidebarProjectionInput();
            var options = new SidebarPlanOptions(Mode: SidebarPaneMode.Expanded);
            var viaKit = ClassicNavRows.Kit.Plan(doc, in input, in options, new SidebarPlanBuffers());
            var direct = SidebarRowPlanner.Build(doc, in input, in options, new SidebarPlanBuffers());
            Assert.Equal(direct.Rows.Count, viaKit.Rows.Count);
            for (int i = 0; i < direct.Rows.Count; i++) Assert.Equal(direct.Rows[i], viaKit.Rows[i]);
        });
}
