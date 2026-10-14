// ── Wavee.ShellTests/NavPaneMountedTests.cs — the navigation pane's rows and heads, mounted (themes plan 6.1, T5a, T5b) ──
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
//   the heads (T5b)       Classic registers Your Library's head and the empty body; the real shell in the Library layout draws
//                         the head through its key (Home, the page dropdown and the toolbar stand under it), and the empty body
//                         is the pane's sentence, or the rail's bare spacer
//
//   the flyouts (T5c)     Classic registers the rail's two flyout panels (the pane opens and dismisses them; the structure draws them)
//   the outline (T5d)     Edit swaps the pane for the registered Outline (its bar's Done, its section heads at the edit rules'
//                         height) and Done swaps the pane back
//   the frame (P3)        the kit's frame, list box and mode host are the boxes the core pane used to build (the layer, the menu
//                         shell over a childless shield only with an overlay, the inset, the mode's fade); in the real shell the
//                         list stands in the layer beside the one childless shield
//
// The planner, the diff, the extents, the freeze and the ladder are pure and pinned in `Wavee.Tests` (`SidebarPlannerTests`,
// `SidebarWiringTests`, `SidebarRowGeometryTests`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
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

    [Fact]
    public void The_list_stands_inside_the_inset_the_kit_answers()
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
                var inset = ClassicNavRows.Kit.ListInset;
                var pad = shell.Host.Scene.AbsoluteRect(Node(shell, "plan-pad"));
                var home = shell.Host.Scene.AbsoluteRect(HomeRowsIn(shell, mode)[0]);
                Assert.Equal(pad.X + inset.Left, home.X, 0.5f);
                Assert.Equal(pad.W - inset.Left - inset.Right, home.W, 0.5f);
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

    // ── the pane's frame (P3) ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_kits_frame_puts_the_menu_on_a_shell_over_a_childless_shield_only_when_there_is_an_overlay()
        => host.Run(() =>
        {
            Element[] layerKids = [new BoxEl { Key = "test:body" }];
            var watcher = new BoxEl { Key = "test:watcher" };
            var args = new NavPaneFrameArgs
            {
                Width = Prop.Of(static () => 240f), HitTestVisible = true, Layer = layerKids, DragWatcher = watcher,
                OnKeyDown = static _ => { }, Menu = static () => null,
            };

            // No overlay (a headless mount): the root is the frame, the layer first and the watcher beside it.
            var bare = Assert.IsType<BoxEl>(ClassicNavRows.Kit.Frame(in args));
            Assert.True(bare.ZStack);
            Assert.Equal(2, bare.Children.Length);
            var layer = Assert.IsType<BoxEl>(bare.Children[0]);
            Assert.Equal("pane-layer", layer.Key);
            Assert.Same(layerKids, layer.Children);
            Assert.Same(watcher, bare.Children[1]);
            Assert.NotNull(bare.OnKeyDown);

            // With an overlay: a ZStack shell over the childless shield, the root (no longer growing) on top of it.
            var shell = Assert.IsType<BoxEl>(ClassicNavRows.Kit.Frame(args with { MenuOverlay = FluentGpu.Controls.Overlay.Service.Default }));
            Assert.Equal(2, shell.Children.Length);
            var shield = Assert.IsType<BoxEl>(shell.Children[0]);
            Assert.Equal(ClassicNavFrame.ContextShieldKey, shield.Key);
            Assert.Empty(shield.Children);
            var root = Assert.IsType<BoxEl>(shell.Children[1]);
            Assert.Equal(0f, root.Grow);
            Assert.Equal("pane-layer", root.Children[0].Key);
        });

    [Fact]
    public void The_kits_list_box_is_the_inset_and_its_mode_host_fades_the_keyed_mode_in()
        => host.Run(() =>
        {
            var list = new BoxEl { Key = "test:list" };
            var pad = Assert.IsType<BoxEl>(ClassicNavRows.Kit.ListBox(list));
            Assert.Equal("plan-pad", pad.Key);
            Assert.Equal(ClassicNavRows.Kit.ListInset, pad.Padding);
            Assert.Same(list, Assert.Single(pad.Children));

            var modeHost = Assert.IsType<BoxEl>(ClassicNavRows.Kit.ModeHost(new BoxEl { Key = "sidebar.classic" }));
            var mode = Assert.Single(modeHost.Children);
            Assert.Equal("sidebar.classic", mode.Key);
            Assert.Equal(new EnterExit(Opacity: 0f, Active: true), mode.Enter);
        });

    [Fact]
    public void The_mounted_pane_stands_in_the_kits_layer_beside_its_childless_shield()
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
                var inMode = Subtree(shell.Host.Scene, mode).ToHashSet();
                var layers = shell.FindPrefix("pane-layer").Where(f => f.Key == "pane-layer" && inMode.Contains(f.Node)).ToList();
                Assert.Single(layers);
                Assert.Contains(Node(shell, "plan-pad"), Subtree(shell.Host.Scene, layers[0].Node).ToHashSet());
                var shields = shell.FindPrefix(ClassicNavFrame.ContextShieldKey).Where(f => inMode.Contains(f.Node)).ToList();
                var shield = Assert.Single(shields);
                Assert.Single(Subtree(shell.Host.Scene, shield.Node));   // childless: a menu cascade from it reaches nothing
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

    // ── the drop cues (T5e) ──

    [Fact]
    public void The_kits_drop_look_is_the_production_drag_presentation()
        => host.Run(() =>
        {
            var look = ClassicNavRows.Kit.DropLook;
            Assert.Equal(DropTargetVisualPolicy.Spotlight, look.TargetPolicy);
            Assert.False(look.ReorderInsertionLine);
            Assert.Equal(DragLift.Stationary, look.ReorderDragStyle.Lift);
            Assert.Equal(0f, look.ReorderDragStyle.Opacity);
        });

    // ── the heads (T5b) ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_heads()
        => host.Run(() =>
        {
            Assert.Equal("classic.shell.nav-library-head", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavLibraryHead.Id));
            Assert.Equal("classic.shell.nav-empty", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavEmpty.Id));
        });

    [Fact]
    public void The_empty_body_is_the_panes_sentence_and_the_rails_bare_spacer()
    {
        ViewHarness.With(host, ViewKeys.ShellNavEmpty, NavEmptyProps.Pane, harness =>
        {
            var body = harness.Node("empty");
            Assert.True(harness.Subtree(body).Count() > 1, "the expanded empty pane names the state: a glyph and a sentence");
        });
        ViewHarness.With(host, ViewKeys.ShellNavEmpty, NavEmptyProps.Rail, harness =>
        {
            var body = harness.Node("empty");
            Assert.Single(harness.Subtree(body));
        });
    }

    [Fact]
    public void The_library_layouts_head_is_drawn_through_its_key_and_its_ring_stands()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            var original = Sidebar.Layout.Peek();
            try
            {
                if (original != SidebarLayoutId.Library)
                {
                    Sidebar.SwitchLayout(SidebarLayoutId.Library);
                    shell.RunFrames(60);
                }
                var mode = Node(shell, "sidebar.library");
                var head = Node(shell, "library-head");
                var inHead = Subtree(shell.Host.Scene, head).ToHashSet();
                Assert.Contains(Node(shell, "lib-home-ring"), inHead);
                Assert.Contains(Node(shell, "library-dropdown-row"), inHead);
                Assert.Contains(Node(shell, "library-toolbar"), inHead);
                Assert.Contains(head, Subtree(shell.Host.Scene, mode).ToHashSet());
            }
            finally
            {
                if (original != SidebarLayoutId.Library)
                {
                    Sidebar.SwitchLayout(original);
                    shell.RunFrames(60);
                }
            }
        });

    // ── the rail's flyouts (T5c) ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_rails_flyout_panels()
        => host.Run(() =>
        {
            Assert.Equal("classic.shell.nav-section-flyout", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavSectionFlyout.Id));
            Assert.Equal("classic.shell.nav-folder-flyout", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavFolderFlyout.Id));
        });

    // ── the Edit outline (T5d) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_edit_outline()
        => host.Run(() => Assert.Equal("classic.shell.nav-edit", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavEdit.Id)));

    [Fact]
    public void Edit_mode_swaps_the_pane_for_the_registered_outline_and_done_swaps_it_back()
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
                Sidebar.EnterEdit();
                Until(shell, () => !shell.Find("edit-pane").IsNull, "the Outline mounted through Shell.NavEdit");
                shell.RunFrames(60);
                Assert.False(shell.Find("edit-done").IsNull, "the edit bar has no Done");
                var heads = shell.FindPrefix("head:");
                Assert.NotEmpty(heads);
                foreach (var (node, key) in heads)
                    Assert.Equal(SidebarEditRules.SectionHeaderHeight, shell.Host.Scene.AbsoluteRect(node).H, 0.5f);

                Sidebar.ExitEdit();
                Until(shell, () => shell.Find("edit-pane").IsNull, "Done took the Outline down");
                Assert.False(shell.Find("sidebar.classic").IsNull, "the pane came back");
            }
            finally
            {
                if (Sidebar.Editing.Peek()) Sidebar.ExitEdit();
                shell.RunFrames(30);
                if (original != SidebarLayoutId.Classic)
                {
                    Sidebar.SwitchLayout(original);
                    shell.RunFrames(60);
                }
            }
        });
}
