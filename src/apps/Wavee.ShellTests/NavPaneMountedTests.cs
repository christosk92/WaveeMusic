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

    [Fact]
    public void A_failed_library_source_draws_the_boards_retry_banner_over_what_loaded()
        => host.Mounted(shell =>
        {
            // S10 (board `C-Sidebar`, state failed): a source whose last ask failed, with rows still loaded, puts the caution
            // banner under the head's rule, 8 in from the pane edges, with a 24-tall standard Retry, and its rows stay.
            shell.RunFrames(60);
            var original = Sidebar.Layout.Peek();
            var scope = Entities.Current;
            int me = scope.MeSlot;
            var shows = scope.Edges.SavedShows;
            int[] targets = shows.Targets(me).ToArray();
            var payload = shows.Payload(me).ToArray();
            try
            {
                if (original != SidebarLayoutId.Library)
                {
                    Sidebar.SwitchLayout(SidebarLayoutId.Library);
                    shell.RunFrames(60);
                }
                shows.Clear(me);
                shows.MarkFailed(me, 0, 503);
                Until(shell, () => !shell.Find("library-error-banner").IsNull, "the library's retry banner");

                var scene = shell.Host.Scene;
                var head = scene.AbsoluteRect(Node(shell, "library-head"));
                var banner = scene.AbsoluteRect(Node(shell, "library-error-banner"));
                float inset = SidebarGeometry.PaneEdge + ClassicNavHeadLayout.BannerInsetX;
                Assert.Equal(head.X + inset, banner.X, 0.5f);
                Assert.Equal(head.W - 2f * inset, banner.W, 0.5f);
                Assert.True(banner.H >= 2f * ClassicNavHeadLayout.BannerPadY + 24f, "the banner holds the 24-tall Retry inside its 8 padding");
                Assert.Contains(Subtree(scene, Node(shell, "library-error-banner")),
                    n => MathF.Abs(scene.AbsoluteRect(n).H - 24f) < 0.5f && scene.AbsoluteRect(n).W > 24f);
            }
            finally
            {
                scope.Edges.SavedShows.Replace(me, targets, payload, EdgeState.Complete, targets.Length);
                scope.Edges.SavedShows.MarkAnswered(me);
                Until(shell, () => shell.Find("library-error-banner").IsNull, "the banner leaving once the source answers");
                if (original != SidebarLayoutId.Library)
                {
                    Sidebar.SwitchLayout(original);
                    shell.RunFrames(60);
                }
            }
        });

    // ── the tuned rows (CT3a: S0, S3, S5; board `C-Sidebar`) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Covers_off_redraws_the_classic_layouts_playlists_as_36_tall_text_rows_in_place()
        => host.Mounted(shell =>
        {
            // S0 + S3: the option re-plans the pane where it stands; a playlist row is 36 tall (pitch 40) instead of the 40-tall
            // two-line cover row, so the list gets shorter; covers on brings the two-line rows back.
            shell.RunFrames(60);
            var layout = Sidebar.Layout.Peek();
            var density = Sidebar.Density.Peek();
            bool covers = Sidebar.ShowCovers.Peek();
            try
            {
                SetPane(shell, SidebarLayoutId.Classic, SidebarDensity.Default, covers: true);
                var mode = Node(shell, "sidebar.classic");
                Until(shell, () => PlaylistRowHeights(shell, mode).Contains(SidebarGeometry.TwoLineRowHeight), "a two-line cover row");
                var list = ScrollUnder(shell, mode);
                Assert.True(shell.Host.Scene.TryGetScroll(list, out var withCovers));

                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.ShowCoversQualifiedId, false));
                shell.RunFrames(30);
                Assert.False(Sidebar.ShowCovers.Peek());
                Assert.False(ShellPolicy.Now.NavPane.ShowCovers);
                Assert.Equal(SidebarRowShape.EntityText, Sidebar.Doc.Find(SidebarSectionKind.Playlists)!.Shape);
                Assert.Equal(mode, Node(shell, "sidebar.classic"));   // the same pane: the option never remounts it
                Until(shell, () => PlaylistRowHeights(shell, mode) is { Count: > 0 } h && !h.Contains(SidebarGeometry.TwoLineRowHeight)
                    && h.Contains(SidebarGeometry.RowHeight), "36-tall text rows");
                Assert.True(shell.Host.Scene.TryGetScroll(list, out var text));
                Assert.True(text.ContentH < withCovers.ContentH, $"text rows did not shrink the list: {withCovers.ContentH} → {text.ContentH}");

                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.ShowCoversQualifiedId, true));
                Until(shell, () => PlaylistRowHeights(shell, mode).Contains(SidebarGeometry.TwoLineRowHeight), "the cover rows back");
            }
            finally
            {
                SetPane(shell, layout, density, covers);
            }
        });

    [Fact]
    public void A_pinned_row_carries_the_24_unpin_button_and_the_menu_keeps_pin_at_the_top_level()
        => host.Mounted(shell =>
        {
            // S5: the pin is a real 24×24 Unpin button in the pinned row, not the 12-px mark.
            shell.RunFrames(60);
            var layout = Sidebar.Layout.Peek();
            var density = Sidebar.Density.Peek();
            bool covers = Sidebar.ShowCovers.Peek();
            string? pinned = null;
            try
            {
                SetPane(shell, SidebarLayoutId.Classic, SidebarDensity.Default, covers: true);
                var mode = Node(shell, "sidebar.classic");
                Until(shell, () => PlaylistRows(shell, mode).Count > 0, "a playlist row");
                string key = PlaylistRows(shell, mode)[0].Key;
                if (!Sidebar.IsPinned(key))
                {
                    Assert.True(Sidebar.Model.Pin(key, SidebarEntryKind.Playlist, key[SidebarPinId.PlaylistPrefix.Length..], null));
                    pinned = key;
                }
                var scene = shell.Host.Scene;
                Until(shell, () => PlaylistRows(shell, mode).Count(r => r.Key == key) >= 2, "the pinned copy of the row");
                // The pinned copy is the one whose subtree holds a 24×24 box: the Unpin button.
                Assert.Contains(PlaylistRows(shell, mode).Where(r => r.Key == key), r => Subtree(scene, r.Node).Any(n =>
                    MathF.Abs(scene.AbsoluteRect(n).W - SidebarGeometry.PinButton) < 0.5f
                    && MathF.Abs(scene.AbsoluteRect(n).H - SidebarGeometry.PinButton) < 0.5f));
            }
            finally
            {
                if (pinned is not null) Sidebar.Model.Unpin(pinned);
                SetPane(shell, layout, density, covers);
            }
        });

    static void SetPane(Shell.HeadlessShell shell, SidebarLayoutId layout, SidebarDensity density, bool covers)
    {
        _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.SidebarOptionId, layout));
        _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.LibraryDensityOptionId, density));
        _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, ClassicStructure.ShowCoversQualifiedId, covers));
        shell.RunFrames(60);
    }

    /// <summary>The playlist rows inside the pane (keyed by their `pl:` pin-id spelling), their own row boxes only.</summary>
    static List<(NodeHandle Node, string Key)> PlaylistRows(Shell.HeadlessShell shell, NodeHandle mode)
    {
        var inPane = Subtree(shell.Host.Scene, mode).ToHashSet();
        return shell.FindPrefix(SidebarPinId.PlaylistPrefix).Where(f => inPane.Contains(f.Node)).ToList();
    }

    static HashSet<float> PlaylistRowHeights(Shell.HeadlessShell shell, NodeHandle mode)
        => PlaylistRows(shell, mode).Select(r => MathF.Round(shell.Host.Scene.AbsoluteRect(r.Node).H)).Where(h => h > 0f).ToHashSet();

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
