// ── Wavee.ShellTests/RootScopeTests.cs — root-lifetime state goes with the root, process state stays ──────────────────
//
// The C17 pins (themes plan §5.6, owner Q26: nothing is carried across a theme switch). A switch unmounts the root and
// mounts a new one, so the new tree must start like a fresh navigation: no handle, overlay, instance or page pivot of the
// frozen tree, while the process (route and history, the omnibar's typed text, playback, settings) is untouched.
//
// What each fact does on the real mounted frame (`Shell.Headless`):
//   a single mount        adopts one scope, fills it with the tree's own captures, keeps it for its whole life (the behaviour the
//                         statics had)
//   a remount             mount, change page and process state, dispose, mount again: the gap holds nothing of the old tree, every
//                         root slot is recreated, every node handle is live in the NEW scene, the page pivot and the overlay set
//                         are fresh, the omnibar's mounted-form facts are put back, and the process state survived
//   the scope lifecycle   `RootScope.Begin` / `End` on a slot of the test's own: ambient writes are adopted, a second Begin retires
//                         the first, End is idempotent, dispose callbacks run last-first
//
// Not pinned here, on purpose: the scroll offset. Scroll is production's `ScrollPositionMemory` keyed by the route (§5.6): a
// remounted page lands where a fresh navigation to it lands, and nothing in C17 changes that. The harness stops playback when a
// mount is disposed, so the playback fact is a mode flag the stop does not touch.

using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class RootScopeTests(ShellHost host)
{
    // The test's own slots: they register like production's, so the facts below also cover `RootState.All`.
    static readonly RootState<int> s_probe = new("Test.Probe");
    static readonly RootState<List<string>> s_list = new("Test.List", static () => new List<string>());

    static RootState State(string name) => RootState.All.Single(s => s.Name == name);

    /// <summary>A value that is an object of one tree: not a handle or a flag (compared by liveness), not the engine's process-wide
    /// hooks default and not a delegate (a poster may legitimately be one shared instance).</summary>
    static bool IsTreeObject(object? v) => v is not null and not ValueType and not InputHooks and not Delegate;

    // ── one mount ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_single_mount_adopts_one_scope_and_keeps_it()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            var scope = RootScope.Current;
            Assert.True(scope.IsMounted);
            Assert.False(scope.IsDisposed);
            Assert.Equal(RootScope.Mounts, scope.Generation);

            // The frame's captures are the mounted tree's own.
            Assert.Same(shell.Host.Scene, scope.Peek(State("Shell.Scene")));
            Assert.NotNull(scope.Peek(State("Shell.Hooks")));
            foreach (string name in new[] { "Shell.ContentRegion", "Shell.SidebarColumn" })
            {
                var node = Assert.IsType<NodeHandle>(scope.Peek(State(name)));
                Assert.False(node.IsNull, name + " was never realized");
                Assert.True(shell.Host.Scene.IsLive(node), name + " is not live in the mounted scene");
            }

            // Navigation, frames and a root-state write all stay in the same scope (nothing remounts the root).
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            shell.RunFrames(20);
            Assert.Equal(Shell.RouteKind.Liked, Shell.Current.Peek().Kind);
            s_probe.Value = 7;
            shell.RunFrames(5);
            Assert.Same(scope, RootScope.Current);
            Assert.Equal(7, s_probe.Value);
            Assert.True(scope.IsMounted);
        });

    [Fact]
    public void Every_root_slot_has_its_own_name()
    {
        // Touch the owners the shell mounts so their slots are declared, then check the names are unique.
        host.Mounted(shell =>
        {
            shell.RunFrames(5);
            _ = OverlayService.Shared;   // declares the overlay set's slot (its owner initializes on first use)
            // Declare every slot in the assembly, not just the ones this mount touched: a theme that copies another's view (WinUI's copies of Classic's)
            // must not reuse its slot names, and without this the clash only showed when an earlier test had loaded both themes.
            foreach (var type in typeof(RootState).Assembly.GetTypes())
                if (type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                        .Any(f => typeof(RootState).IsAssignableFrom(f.FieldType)))
                    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            var names = RootState.All.Select(s => s.Name).ToList();
            var clashes = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(clashes.Count == 0, "root slots declared twice: " + string.Join(", ", clashes));
            Assert.Contains("Shell.Scene", names);
            Assert.Contains("Shell.TransientOverlays", names);
        });
    }

    // ── a remount ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remounting_the_root_recreates_every_root_state_and_none_points_at_a_destroyed_node()
        => host.Run(() =>
        {
            RootScope.EndMounted();
            var before = new Dictionary<string, object?>(StringComparer.Ordinal);
            RootScope first;
            using (var a = Shell.Headless.Mount())
            {
                a.RunFrames(10);
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
                a.RunFrames(20);
                first = RootScope.Current;
                s_probe.Value = 41;
                s_list.Value.Add("old tree");
                foreach (var s in RootState.All)
                    if (first.Has(s)) before[s.Name] = first.Peek(s);
                Assert.Contains("Shell.Scene", before.Keys);
                Assert.Contains("Shell.ContentRegion", before.Keys);
            }

            // The gap: the old scope is retired and the ambient one holds nothing of its tree.
            Assert.True(first.IsDisposed);
            Assert.NotSame(first, RootScope.Current);
            Assert.False(RootScope.Current.IsMounted);
            AssertNothingOfTheOldTree(RootScope.Current, before);

            using (var b = Shell.Headless.Mount())
            {
                b.RunFrames(10);
                var second = RootScope.Current;
                Assert.NotSame(first, second);
                Assert.True(second.IsMounted);
                Assert.True(second.Generation > first.Generation);

                // Every slot both trees touched holds a NEW object (the engine's process-wide hooks default is the one shared value).
                foreach (var s in RootState.All)
                {
                    if (!second.Has(s) || !before.TryGetValue(s.Name, out var old)) continue;
                    if (!IsTreeObject(old)) continue;
                    Assert.NotSame(old, second.Peek(s));
                }

                // No handle points at a destroyed node: the captures are the NEW tree's and live in the NEW scene.
                Assert.Same(b.Host.Scene, second.Peek(State("Shell.Scene")));
                Assert.NotSame(before["Shell.Scene"], second.Peek(State("Shell.Scene")));
                foreach (string name in new[] { "Shell.ContentRegion", "Shell.SidebarColumn" })
                {
                    var node = Assert.IsType<NodeHandle>(second.Peek(State(name)));
                    Assert.False(node.IsNull, name);
                    Assert.True(b.Host.Scene.IsLive(node), name + " is not live in the new scene");
                }
                Assert.Equal(0, s_probe.Value);
                Assert.Empty(s_list.Value);
            }
            ShellHost.AssertNoNetwork();
        });

    static void AssertNothingOfTheOldTree(RootScope ambient, Dictionary<string, object?> before)
    {
        var oldObjects = before.Values.Where(IsTreeObject).ToList();
        foreach (var s in RootState.All)
        {
            if (!ambient.Has(s)) continue;
            var value = ambient.Peek(s);
            if (value is NodeHandle node) Assert.True(node.IsNull, s.Name + " keeps a node of the old tree");
            else if (IsTreeObject(value))
                Assert.DoesNotContain(oldObjects, o => ReferenceEquals(o, value));
        }
    }

    [Fact]
    public void A_remount_gives_every_page_fresh_ui_state_and_keeps_process_state()
        => host.Run(() =>
        {
            RootScope.EndMounted();
            int layoutBefore = Platform.Settings.Get(Platform.Keys.StageLayout);
            bool shuffleBefore = Playback.Shuffle.Peek();
            string searchBefore = Shell.SearchText.Peek();
            try
            {
                OverlayService sharedA;
                IOverlayService? settingsOverlayA;
                int tabA;
                using (var a = Shell.Headless.Mount())
                {
                    a.RunFrames(10);
                    Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
                    a.RunFrames(10);

                    // PROCESS state: settings, playback mode, the omnibar's typed text.
                    Prefs.Stage.SetLayout((int)Stage.VizLayout.LargeArt);
                    Playback.SetShuffle(true);   // the engine owns the mode; the signal only mirrors it
                    UntilBackground(a, () => Playback.Shuffle.Peek(), "the player took shuffle on (its inbox drains off the UI thread)");

                    // PAGE UI state: the Settings pivot, the overlay set, what a mounted omnibar form holds.
                    Settings.Open(Settings.Tab.Appearance);
                    Until(a, () => SettingsContext.Overlay is not null, "the Settings page rendered and published its overlay host");
                    a.RunFrames(10);
                    Shell.SearchText.Value = "queen";   // after the navigation: a navigation clears the typed text, a remount must not
                    tabA = ((Signal<int>)RootScope.Current.Peek(State("Settings.Tab"))!).Peek();
                    Assert.Equal((int)Settings.Tab.Appearance, tabA);

                    settingsOverlayA = SettingsContext.Overlay;
                    var handle = settingsOverlayA!.Open(static () => NodeHandle.Null, static () => new BoxEl { Width = 40f, Height = 40f },
                        FlyoutPlacement.BottomLeft, new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Static));
                    OverlayService.Track(handle);
                    a.RunFrames(2);
                    sharedA = OverlayService.Shared;
                    Assert.Equal(1, sharedA.OpenCount);
                    Shell.SetOmnibarFactsForTests(new Shell.OmnibarFacts(SearchFocused: true, FlyoutOpen: true, Highlight: 2));
                }

                // The gap: the mounted-form facts are put back, the root-lifetime slots are empty, the process state is untouched.
                Assert.Equal(new Shell.OmnibarFacts(false, false, -1), Shell.OmnibarFactsForTests());
                Assert.Null(SettingsContext.Overlay);
                Assert.NotSame(sharedA, OverlayService.Shared);
                Assert.Equal(0, OverlayService.Shared.OpenCount);
                AssertProcessState();

                using (var b = Shell.Headless.Mount())
                {
                    // The route is process state, so the new root opens on Settings again; the page mounts like a fresh navigation.
                    Until(b, () => SettingsContext.Overlay is not null, "the remounted Settings page published its overlay host");
                    b.RunFrames(10);
                    Assert.Equal(Shell.RouteKind.Settings, Shell.Current.Peek().Kind);
                    Assert.NotSame(settingsOverlayA, SettingsContext.Overlay);
                    // The open section is the ROUTE's (contract 1.35: the settings route carries it), and the route is process state: the page opens on the pivot the route names,
                    // on a fresh slot of the new root (never the old root's signal).
                    Assert.Equal((int)Settings.Tab.Appearance, ((Signal<int>)RootScope.Current.Peek(State("Settings.Tab"))!).Peek());
                    Assert.Equal(new Shell.OmnibarFacts(false, false, -1), Shell.OmnibarFactsForTests());
                    Assert.Equal(0, OverlayService.Shared.OpenCount);
                    AssertProcessState();
                }
                ShellHost.AssertNoNetwork();
            }
            finally
            {
                Prefs.Stage.SetLayout(layoutBefore);
                Playback.SetShuffle(shuffleBefore);
                Shell.SearchText.Value = searchBefore;
                Shell.SetOmnibarFactsForTests(new Shell.OmnibarFacts(false, false, -1));
            }

            static void AssertProcessState()
            {
                Assert.Equal(Shell.RouteKind.Settings, Shell.Current.Peek().Kind);   // the route and its history are the process's
                Assert.True(Shell.CanBack.Peek());
                Assert.Equal("queen", Shell.SearchText.Peek());                       // the omnibar's typed text (§5.6)
                Assert.True(Playback.Shuffle.Peek());                                 // playback mode
                Assert.Equal((int)Stage.VizLayout.LargeArt, Platform.Settings.Get(Platform.Keys.StageLayout));   // a persisted setting
            }
        });

    /// <summary>A pool thread that posts playback input while a mount ends (the fake audio graph, a ticker) must not wedge
    /// playback for the next mount. Posts that land on the ending host's queue used to be dropped with it; a dropped drain
    /// left the inbox's "a wake is queued" latch set, so every later Play / SetShuffle waited forever (the remount and
    /// lyrics-blur tests then failed after 10 s in the same process, about once in 25 runs).</summary>
    [Fact]
    public void A_post_racing_a_mount_teardown_never_wedges_playback_for_the_next_mount()
        => host.Run(() =>
        {
            RootScope.EndMounted();
            bool before = Playback.Shuffle.Peek();
            int stop = 0;
            Thread poster;
            using (var a = Shell.Headless.Mount())
            {
                a.RunFrames(4);
                // A stale epoch: the reducer ignores it, but every report is a post that can land on the ending host's queue.
                poster = new Thread(() => { while (Volatile.Read(ref stop) == 0) { Playback.ReportPosition(0xFFFF_FFFFu, 0); Thread.Yield(); } })
                { IsBackground = true, Name = "test-teardown-poster" };
                poster.Start();
                Thread.Sleep(20);
            }
            Volatile.Write(ref stop, 1);
            poster.Join();
            try
            {
                using var b = Shell.Headless.Mount();
                b.RunFrames(4);
                Playback.SetShuffle(!before);
                UntilBackground(b, () => Playback.Shuffle.Peek() == !before, "the next mount's SetShuffle reached the player", 3_000);
            }
            finally
            {
                Playback.SetShuffle(before);
            }
        });

    // ── the scope lifecycle ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_write_before_the_first_mount_is_adopted_and_a_retired_scope_releases_its_state()
        => host.Run(() =>
        {
            RootScope.EndMounted();
            var ambient = RootScope.Current;
            Assert.False(ambient.IsMounted);
            s_probe.Value = 5;   // a deep link or a test writing before any root exists

            var scope = RootScope.Begin();
            Assert.Same(ambient, scope);
            Assert.True(scope.IsMounted);
            Assert.Equal(5, s_probe.Value);   // the root adopts what the ambient scope held

            var order = new List<int>();
            scope.OnDispose(() => order.Add(1));
            scope.OnDispose(() => order.Add(2));
            RootScope.End(scope);
            Assert.Equal(new[] { 2, 1 }, order);   // last registered runs first
            Assert.True(scope.IsDisposed);
            Assert.NotSame(scope, RootScope.Current);
            Assert.False(RootScope.Current.IsMounted);
            Assert.Equal(0, s_probe.Value);   // the next scope starts fresh

            RootScope.End(scope);   // idempotent
            Assert.Equal(new[] { 2, 1 }, order);
        });

    [Fact]
    public void A_second_begin_retires_the_scope_that_is_still_mounted()
        => host.Run(() =>
        {
            RootScope.EndMounted();
            var s1 = RootScope.Begin();
            s_probe.Value = 9;
            s_list.Value.Add("first root");
            var s2 = RootScope.Begin();   // a remount that rendered before the old root's cleanup ran
            try
            {
                Assert.NotSame(s1, s2);
                Assert.True(s1.IsDisposed);
                Assert.True(s2.IsMounted);
                Assert.Same(s2, RootScope.Current);
                Assert.Equal(0, s_probe.Value);
                Assert.Empty(s_list.Value);

                RootScope.End(s1);   // the old root's late cleanup must not touch the new root
                Assert.True(s2.IsMounted);
                Assert.Same(s2, RootScope.Current);
            }
            finally { RootScope.End(s2); }
            Assert.False(RootScope.Current.IsMounted);
        });
}
