// ── Wavee.ShellTests/ContentStateMountedTests.cs — the Classic frame and its content states, mounted (themes plan 6.1, T4a) ──
//
// The frame is the structure's since T4a: the core frame root (a [CoreHost]) wires the window and mounts `Shell.Frame`; the
// content host decides which state the content region is in and mounts `Shell.ContentState`. What only a mounted tree can say:
//
//   the registered frame   Classic registers both keys; the real shell mounted on the installed theme is drawn by that template
//                          (its realized-node captures and regions are the template's), and a page, the not-found page and an
//                          empty body each stand under the keys the shell mounted before the move
//   the offline lane       stands above the kept content while the session is down and leaves when it is back
//   each state's picture   `ViewHarness` mounts the content-state view alone under the Classic composition with fixture props:
//                          the body boxes fill the card, the not-found page is the glyph, the line and the action, centred, and
//                          the lane is the strip in the page gutter, 16 under the card's top (tuned Classic X1)
//
// The decision itself (every state and transition edge) is `Wavee.Tests\ContentStateTests.cs`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ContentStateMountedTests(ShellHost host)
{
    static RootState State(string name) => RootState.All.Single(s => s.Name == name);

    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static int ChildCount(Shell.HeadlessShell shell, NodeHandle node)
    {
        var scene = shell.Host.Scene;
        int count = 0;
        for (var child = scene.FirstChild(node); !child.IsNull; child = scene.NextSibling(child)) count++;
        return count;
    }

    /// <summary>The content card: the highest ancestor of <paramref name="page"/> that does not hold the sidebar column, so what
    /// it holds is the content host (the page, the masthead, the lane) and the card's own fills.</summary>
    static NodeHandle ContentCardOf(Shell.HeadlessShell shell, NodeHandle page, NodeHandle sidebarColumn)
    {
        var scene = shell.Host.Scene;
        var top = page;
        for (var parent = scene.Parent(top); !parent.IsNull && !Subtree(scene, parent).Contains(sidebarColumn); parent = scene.Parent(parent))
            top = parent;
        return top;
    }

    // ── the registered frame ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_frame_and_content_state()
        => host.Run(() =>
        {
            Assert.Equal("classic.shell.frame", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellFrame.Id));
            Assert.Equal("classic.shell.content-state", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellContentState.Id));
        });

    [Fact]
    public void The_mounted_shell_is_drawn_by_the_classic_frame_template()
        => host.Mounted(shell =>
        {
            shell.RunFrames(10);
            var scene = shell.Host.Scene;

            // The template wired the host's callbacks: the realized-node captures the probes and the scrim clip read.
            var scope = RootScope.Current;
            var region = Assert.IsType<NodeHandle>(scope.Peek(State("Shell.ContentRegion")));
            var column = Assert.IsType<NodeHandle>(scope.Peek(State("Shell.SidebarColumn")));
            Assert.False(region.IsNull);
            Assert.False(column.IsNull);
            Assert.True(scene.IsLive(region));
            Assert.True(scene.IsLive(column));

            // The regions stand where the frame always put them: the pane column at the window's left edge, as wide as the
            // pane presents, and the content region across the window's width, under the chrome row.
            var columnRect = scene.AbsoluteRect(column);
            Assert.Equal(0f, columnRect.X, 0.5f);
            Assert.Equal(Sidebar.PresentedWidth.Peek(), columnRect.W, 0.5f);
            var regionRect = scene.AbsoluteRect(region);
            Assert.Equal(0f, regionRect.X, 0.5f);
            Assert.Equal(1180f, regionRect.W, 0.5f);
            Assert.True(regionRect.Y > 0f, "the chrome row stands above the content region");

            Assert.NotEmpty(shell.FindPrefix("sidebar."));
        });

    // ── the states, in the real shell ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_registered_page_stands_in_a_body_box_keyed_by_its_route()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            var route = new Shell.Route(Shell.RouteKind.Liked);
            string key = "page:" + Shell.NameOf(route);

            Shell.GoTo(route);
            Until(shell, () => !shell.Find(key).IsNull, "the page's body box " + key);

            var body = Node(shell, key);
            Assert.Equal(1, ChildCount(shell, body));   // the page is its only child
            Assert.True(shell.Host.Scene.AbsoluteRect(body).H > 0f);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        });

    [Fact]
    public void A_route_nothing_claims_stands_as_the_not_found_page()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Assert.False(FrameFacts.DeveloperNow, "the test host runs with developer mode off");
            var route = new Shell.Route(Shell.RouteKind.PlaybackDiagnostics);   // developer-only: unknown while developer mode is off
            string key = "page-notfound:" + Shell.NameOf(route) + "|";

            Shell.GoTo(route);
            Until(shell, () => !shell.Find(key).IsNull, "the not-found page " + key);

            Assert.True(shell.Find("page:" + Shell.NameOf(route)).IsNull, "no page stands for the route");
            Assert.Equal(3, ChildCount(shell, Node(shell, key)));   // the glyph, the line, the action
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        });

    [Fact]
    public void A_known_destination_without_a_page_is_an_empty_body_not_the_not_found_page()
        => host.Run(() =>
        {
            var route = new Shell.Route(Shell.RouteKind.Settings);
            Shell.PageFactory? registered = Shell.PageFor(route);
            Assert.NotNull(registered);
            Shell.SetPage(route.Kind, null!);   // the page is not registered (yet)
            try
            {
                using var shell = Shell.Headless.Mount();
                shell.RunFrames(5);
                string empty = "page-empty:" + Shell.NameOf(route);
                Shell.GoTo(route);
                Until(shell, () => !shell.Find(empty).IsNull, "the empty body " + empty);

                Assert.Equal(0, ChildCount(shell, Node(shell, empty)));
                Assert.True(shell.Find("page-notfound:" + Shell.NameOf(route) + "|").IsNull);
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                ShellHost.AssertNoNetwork();
            }
            finally { Shell.SetPage(route.Kind, registered!); }
        });

    [Fact]
    public void A_quick_run_of_route_changes_settles_on_the_last_routes_state()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            var page = new Shell.Route(Shell.RouteKind.Liked);
            var missing = new Shell.Route(Shell.RouteKind.PlaybackDiagnostics);

            Shell.GoTo(page);
            Shell.GoTo(missing);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
            Shell.GoTo(missing);
            Shell.GoTo(page);   // no frame between the changes

            string key = "page:" + Shell.NameOf(page);
            Until(shell, () => !shell.Find(key).IsNull && Shell.Shown.Peek().Kind == Shell.RouteKind.Liked, "the last route's page");
            Assert.Equal(Shell.RouteKind.Liked, Shell.Current.Peek().Kind);
            Assert.Equal(1, ChildCount(shell, Node(shell, key)));
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        });

    [Fact]
    public void The_offline_lane_stands_above_the_content_while_the_session_is_down_and_leaves_when_it_is_back()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            var column = Assert.IsType<NodeHandle>(RootScope.Current.Peek(State("Shell.SidebarColumn")));
            var missing = new Shell.Route(Shell.RouteKind.PlaybackDiagnostics);   // a static page: the count moves only with the lane
            string key = "page-notfound:" + Shell.NameOf(missing) + "|";
            Shell.GoTo(missing);
            Until(shell, () => !shell.Find(key).IsNull, "the not-found page " + key);
            var card = ContentCardOf(shell, Node(shell, key), column);

            var original = Shell.Auth.Peek();
            try
            {
                Shell.Auth.Value = Shell.AuthState.Live;
                shell.RunFrames(10);
                int without = CountUnder(shell, card);

                Shell.Auth.Value = Shell.AuthState.Offline;
                shell.RunFrames(10);
                int with = CountUnder(shell, card);
                Assert.True(with >= without + 4, "the lane, its strip and the strip's parts are mounted: " + without + " -> " + with);
                var lane = shell.Host.Scene.AbsoluteRect(Node(shell, "offline-lane"));
                var page = shell.Host.Scene.AbsoluteRect(Node(shell, key));
                Assert.True(page.Y >= lane.Y + lane.H - 0.5f, "the page opens under the lane, not under a floating card");

                Shell.Auth.Value = Shell.AuthState.Live;
                shell.RunFrames(10);
                Assert.Equal(without, CountUnder(shell, card));
            }
            finally
            {
                Shell.Auth.Value = original;
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            }
        });

    // ── each state's picture (the view alone, under the Classic composition) ───────────────────────────────────────────────────

    [Fact]
    public void A_page_body_fills_the_card_and_holds_the_page()
        => ViewHarness.With(host, ViewKeys.ShellContentState,
            ContentStateProps.ForPage("test:page", new BoxEl { Key = "test:inner", Grow = 1f }),
            harness =>
            {
                var body = harness.AbsoluteBounds(harness.Node("test:page"));
                Assert.Equal(1180f, body.W, 0.5f);
                Assert.Equal(760f, body.H, 0.5f);
                var inner = harness.AbsoluteBounds(harness.Node("test:inner"));
                Assert.Equal(body.W, inner.W, 0.5f);
                Assert.Equal(body.H, inner.H, 0.5f);
            }, UnderClassic());

    [Fact]
    public void An_empty_body_fills_the_card_and_holds_nothing()
        => ViewHarness.With(host, ViewKeys.ShellContentState, ContentStateProps.ForEmptyBody("test:empty"),
            harness =>
            {
                var body = harness.AbsoluteBounds(harness.Node("test:empty"));
                Assert.Equal(1180f, body.W, 0.5f);
                Assert.Equal(760f, body.H, 0.5f);
                Assert.Single(harness.Subtree(harness.Node("test:empty")));   // the box alone
            }, UnderClassic());

    [Fact]
    public void The_not_found_page_is_the_glyph_the_line_and_the_action_centred()
        => ViewHarness.With(host, ViewKeys.ShellContentState, ContentStateProps.ForNotFound("test:missing", "", static () => { }),
            harness =>
            {
                var scene = harness.Shell.Host.Scene;
                var page = harness.Node("test:missing");
                var rect = harness.AbsoluteBounds(page);
                Assert.Equal(1180f, rect.W, 0.5f);
                Assert.Equal(760f, rect.H, 0.5f);

                var children = new List<NodeHandle>();
                for (var child = scene.FirstChild(page); !child.IsNull; child = scene.NextSibling(child)) children.Add(child);
                Assert.Equal(3, children.Count);
                foreach (var child in children)
                {
                    var r = harness.AbsoluteBounds(child);
                    Assert.Equal(rect.X + rect.W / 2f, r.X + r.W / 2f, 1f);   // centred across the card
                }
                Assert.True(harness.AbsoluteBounds(children[0]).Y < harness.AbsoluteBounds(children[2]).Y, "the glyph stands above the action");
            }, UnderClassic());

    [Fact]
    public void The_offline_lane_is_the_36_tall_strip_in_the_page_gutter_16_under_the_top()
        => ViewHarness.With(host, ViewKeys.ShellContentState,
            ContentStateProps.ForOfflineLane(new OfflineStripProps { Message = new string('x', 240) }),   // longer than the window: it trims
            harness =>
            {
                var strip = harness.AbsoluteBounds(harness.Node("offline-strip"));
                Assert.Equal(ClassicOverlayRules.OfflineStripH, strip.H, 0.5f);
                Assert.Equal(16f, strip.Y, 0.5f);                                          // the lane's top inset
                Assert.Equal(FluentGpu.Dsl.Spacing.PageWide, strip.X, 0.5f);              // the page gutter on the left …
                Assert.Equal(1180f - FluentGpu.Dsl.Spacing.PageWide, strip.X + strip.W, 0.5f);   // … and on the right of the 1180 window
            }, UnderClassic());
}
