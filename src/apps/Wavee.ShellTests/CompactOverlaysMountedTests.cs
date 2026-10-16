// ── Wavee.ShellTests/CompactOverlaysMountedTests.cs — the Compact overlays in the real shell at 400 x 860 (the Compact overlays fix) ───
//
// What the owner saw in a narrow Classic window, each held here in the real frame:
//
//   wheel         a wheel over the open drawer, the now-playing sheet or their scrims scrolls the overlay's own list (when it can move) and
//                 never the page under it: the page is a sibling under the overlay, not an ancestor, so the router's containing-scroller
//                 scan found it until the scrims and the surfaces said `BlocksBackgroundScroll`
//   drawer        the drawer is the account row and then the whole nav pane at the window's height, its rows on screen (it was a 100-tall
//                 card: the pane sat in a component anchor, a default column, so a row's stretch never reached it)
//   sheet fit     the now-playing sheet's title, transport and Lyrics / Queue doors lie inside the window at 400 x 860 and 400 x 640; the art
//                 is what gives; every other panel kind's head and body lie inside the window too
//   drag          the sheet's head follows a scripted pointer drag 1:1 (the cursor the vertical-resize arrows), springs back short of the
//                 threshold, dismisses past it or on a fling, rubber-bands upward, and a tap moves nothing; the context sheet takes the same
//                 drag; Escape still closes the sheet and the keys go to it (focus moves in)

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using Wavee;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class CompactOverlaysMountedTests(ShellHost host)
{
    const float W = 400f, H = 860f;

    // ── input ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    uint _ms = 1000;

    void Move(Shell.HeadlessShell shell, Point2 p) => shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: _ms));

    void Down(Shell.HeadlessShell shell, Point2 p) => shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: _ms));

    void Up(Shell.HeadlessShell shell, Point2 p) => shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: _ms));

    void Frame(Shell.HeadlessShell shell)
    {
        shell.Host.RunFrame();
        _ms += 16;
    }

    /// <summary>Three wheel notches toward the content's end at <paramref name="at"/>, then the glide's frames.</summary>
    void Wheel(Shell.HeadlessShell shell, Point2 at)
    {
        Move(shell, at);
        Frame(shell);
        var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, at, 0f, 3f, 0, KeyModifiers.None);
        shell.Window.QueueInput(InputEvent.ForScroll(in wheel));
        for (int i = 0; i < 40; i++) Frame(shell);
    }

    /// <summary>Press at <paramref name="from"/>, move <paramref name="dy"/> over <paramref name="frames"/> frames (one move a frame), and
    /// hold there (no release).</summary>
    Point2 DragBy(Shell.HeadlessShell shell, Point2 from, float dy, int frames)
    {
        Move(shell, from);
        Frame(shell);
        Down(shell, from);
        Frame(shell);
        var at = from;
        for (int i = 1; i <= frames; i++)
        {
            at = new Point2(from.X, from.Y + dy * i / frames);
            Move(shell, at);
            Frame(shell);
        }
        return at;
    }

    // ── reading ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static double Offset(Shell.HeadlessShell shell, NodeHandle vp) => shell.Host.Scene.ScrollRef(vp).Offset;

    static double MaxOffset(Shell.HeadlessShell shell, NodeHandle vp) => shell.Host.Scene.ScrollRef(vp).MaxOffset;

    static RectF At(Shell.HeadlessShell shell, NodeHandle n) => shell.Host.Scene.AbsoluteRect(n);

    /// <summary>The page's own scroller (the first under the content card) on Home, once Home is on screen and overflows the card.</summary>
    static NodeHandle HomePage(Shell.HeadlessShell shell)
    {
        Until(shell, () => shell.FindPrefix("home:column").Count > 0, "Home on screen");
        shell.RunFrames(40);
        var page = ScrollUnder(shell, Node(shell, ClassicFrameLayout.ContentCardKey));
        Assert.False(page.IsNull, "Home has a scroller");
        Assert.True(MaxOffset(shell, page) > 100.0, $"Home overflows its card at {W} x {H} (max {MaxOffset(shell, page)})");
        return page;
    }

    /// <summary>The control: with no overlay up, the same wheel over the page scrolls it (so an unchanged page under an overlay is the
    /// overlay's doing, not a wheel that never arrived).</summary>
    void AssertTheWheelScrollsThePage(Shell.HeadlessShell shell, NodeHandle page)
    {
        var card = At(shell, Node(shell, ClassicFrameLayout.ContentCardKey));
        double before = Offset(shell, page);
        Wheel(shell, new Point2(card.X + card.W / 2f, card.Y + card.H / 2f));
        Assert.True(Offset(shell, page) > before, $"the wheel over the bare page scrolls it ({before} -> {Offset(shell, page)})");
    }

    static void OpenSheet(Shell.HeadlessShell shell, Shell.RailMode mode)
    {
        Play("spotify:track:tr40", fromMs: 20_000);
        Shell.PanelHost.Shared.SetMode(mode);
        shell.RunFrames(60);
        Assert.Equal(PanelPlacement.Sheet, Shell.Ui.PanelPlacement.Peek());
        Assert.True(Shell.Ui.RailOpen.Peek(), "the sheet is open");
    }

    static void CloseSheet(Shell.HeadlessShell shell)
    {
        Shell.PanelHost.Shared.Close();
        shell.RunFrames(30);
    }

    static void AssertInside(Shell.HeadlessShell shell, string key, float height)
    {
        var r = At(shell, Node(shell, key));
        Assert.True(r.W > 0f && r.H > 0f, $"{key} has a size ({r})");
        Assert.True(r.Y >= -0.5f && r.Y + r.H <= height + 0.5f && r.X >= -0.5f && r.X + r.W <= W + 0.5f, $"{key} {r} leaves the {W} x {height} window");
    }

    // ── the wheel ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wheel_over_the_open_drawer_scrolls_the_drawer_and_never_the_page_under_it()
        => host.Mounted(shell =>
        {
            var page = HomePage(shell);
            AssertTheWheelScrollsThePage(shell, page);
            try
            {
                Sidebar.OverlayOpen.Value = true;
                shell.RunFrames(40);
                var pane = At(shell, Node(shell, "drawer-pane"));
                var nav = ScrollUnder(shell, Node(shell, "drawer-pane"));
                Assert.False(nav.IsNull, "the drawer's nav pane has its own scroller");
                double pageBefore = Offset(shell, page), navBefore = Offset(shell, nav);

                Wheel(shell, new Point2(pane.X + pane.W / 2f, pane.Y + pane.H * 0.6f));          // over the pane's rows
                Assert.Equal(pageBefore, Offset(shell, page));
                if (MaxOffset(shell, nav) > 0.5) Assert.True(Offset(shell, nav) > navBefore, "the drawer's own list took the wheel");

                Wheel(shell, new Point2(pane.X + pane.W / 2f, pane.Y + 20f));                    // over the account row
                Assert.Equal(pageBefore, Offset(shell, page));
                Wheel(shell, new Point2(pane.X + pane.W + (W - pane.X - pane.W) / 2f, pane.Y + pane.H / 2f));   // over the scrim
                Assert.Equal(pageBefore, Offset(shell, page));
            }
            finally
            {
                Sidebar.OverlayOpen.Value = false;
                shell.RunFrames(30);
            }
        }, W, H);

    [Fact]
    public void A_wheel_over_the_open_now_playing_sheet_scrolls_the_sheet_and_never_the_page_under_it()
        => host.Mounted(shell =>
        {
            var page = HomePage(shell);
            AssertTheWheelScrollsThePage(shell, page);
            try
            {
                OpenSheet(shell, Shell.RailMode.NowPlaying);
                var sheetScroll = Node(shell, ClassicPanelTiers.SheetScrollKey);
                var r = At(shell, sheetScroll);
                double pageBefore = Offset(shell, page), sheetBefore = Offset(shell, sheetScroll);

                Wheel(shell, new Point2(W / 2f, r.Y + r.H / 2f));                                 // over the sheet's column
                Assert.Equal(pageBefore, Offset(shell, page));
                if (MaxOffset(shell, sheetScroll) > 0.5) Assert.True(Offset(shell, sheetScroll) > sheetBefore, "the sheet's own column took the wheel");

                var head = At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey));
                Wheel(shell, new Point2(W / 2f, head.Y + head.H / 2f));                           // over the head
                Assert.Equal(pageBefore, Offset(shell, page));
                Wheel(shell, new Point2(W / 2f, (ClassicSheetRules.ChromeRow + head.Y) / 2f));    // over the scrim strip above the sheet
                Assert.Equal(pageBefore, Offset(shell, page));
            }
            finally { CloseSheet(shell); }
        }, W, H);

    [Fact]
    public void The_keys_go_to_the_open_sheet_and_Escape_closes_it()
        => host.Mounted(shell =>
        {
            var page = HomePage(shell);
            try
            {
                OpenSheet(shell, Shell.RailMode.NowPlaying);
                double pageBefore = Offset(shell, page);
                Key(shell, Keys.PageDown);
                shell.RunFrames(30);
                Assert.Equal(pageBefore, Offset(shell, page));

                Key(shell, Keys.Escape);
                shell.RunFrames(3);
                Assert.False(Shell.Ui.RailOpen.Peek(), "Escape closes the sheet");
            }
            finally { CloseSheet(shell); }
        }, W, H);

    // ── the drawer ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_drawer_is_the_account_row_over_the_whole_nav_pane_at_the_windows_height()
        => host.Mounted(shell =>
        {
            HomePage(shell);
            try
            {
                Sidebar.OverlayOpen.Value = true;
                shell.RunFrames(40);
                var scene = shell.Host.Scene;
                var paneNode = Node(shell, "drawer-pane");
                var pane = At(shell, paneNode);
                var card = At(shell, Node(shell, ClassicFrameLayout.ContentCardKey));
                Assert.True(pane.H >= card.H - 1f, $"the drawer is the content region's height ({pane.H} of {card.H}), not a card");
                var account = At(shell, Node(shell, "drawer-account"));
                Assert.True(account.Y <= pane.Y + 1f, "the account row leads the pane");

                var nav = ScrollUnder(shell, paneNode);
                Assert.False(nav.IsNull, "the nav pane is mounted under the account row");
                var list = At(shell, nav);
                Assert.True(list.Y >= account.Y + account.H - 1f && list.H > pane.H * 0.5f, $"the nav list fills the rest ({list} under {account} in {pane})");

                // Its rows are on screen: Home, inside the pane, at a real size.
                var inPane = Subtree(scene, paneNode).ToHashSet();
                var home = shell.FindPrefix(SidebarCatalogue.HomeRoute).Where(f => f.Key == SidebarCatalogue.HomeRoute && inPane.Contains(f.Node)).ToList();
                Assert.NotEmpty(home);
                var row = At(shell, home[0].Node);
                Assert.True(row.H >= 32f && row.Y >= list.Y - 0.5f && row.Y + row.H <= list.Y + list.H + 0.5f, $"the Home row {row} stands in the list {list}");
            }
            finally
            {
                Sidebar.OverlayOpen.Value = false;
                shell.RunFrames(30);
            }
        }, W, H);

    // ── the sheet fits ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(860f)]
    [InlineData(640f)]
    public void The_now_playing_sheets_title_transport_and_doors_lie_inside_the_window(float height)
        => host.Mounted(shell =>
        {
            shell.RunFrames(20);
            try
            {
                OpenSheet(shell, Shell.RailMode.NowPlaying);
                AssertInside(shell, ClassicPanelTiers.SheetTitleKey, height);
                AssertInside(shell, ClassicPanelTiers.SheetTransportKey, height);
                AssertInside(shell, ClassicPanelTiers.SheetDoorsKey, height);
                var head = At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey));
                // The head's header row is laid out across the sheet: its eyebrow has a width (it once sat in a row wrapper at 0 wide).
                var scene = shell.Host.Scene;
                var texts = Subtree(scene, Node(shell, ClassicPanelTiers.SheetHeadKey)).Where(n => scene.Bounds(n).H > 0f && scene.ElementTypeId(n) == new TextEl("").ElementTypeId).ToList();
                Assert.NotEmpty(texts);
                Assert.All(texts, n => Assert.True(scene.Bounds(n).W > 20f, $"a head text is {scene.Bounds(n).W} wide"));
                float side = ClassicPanelTiers.SheetArtSide(W, height, head.H);
                Assert.True(side <= ClassicPanelTiers.SheetArtMax && side < W, $"the art gives ({side})");
                var cover = shell.FindPrefix("cover").Where(f => f.Key == "cover").Select(f => At(shell, f.Node)).Where(r => r.Y > head.Y).ToList();
                Assert.NotEmpty(cover);
                Assert.Equal(side, cover[0].W, 1f);
                Assert.True(cover[0].X >= 0f && cover[0].X + cover[0].W <= W, "the art stands inside the sheet");
            }
            finally { CloseSheet(shell); }
        }, W, height);

    [Theory]
    [InlineData(Shell.RailMode.Lyrics)]
    [InlineData(Shell.RailMode.Queue)]
    [InlineData(Shell.RailMode.Friends)]
    public void Every_panel_kind_as_a_sheet_keeps_its_head_and_body_inside_the_window(Shell.RailMode mode)
        => host.Mounted(shell =>
        {
            shell.RunFrames(20);
            try
            {
                OpenSheet(shell, mode);
                AssertInside(shell, ClassicPanelTiers.SheetHeadKey, H);
                var box = At(shell, Node(shell, ClassicPanelTiers.HostKey));
                Assert.Equal(H, box.Y + box.H, 0.5f);
            }
            finally { CloseSheet(shell); }
        }, W, H);

    // ── the drag ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sheets_head_drags_it_springs_back_short_of_the_threshold_and_dismisses_past_it()
        => host.Mounted(shell =>
        {
            shell.RunFrames(20);
            try
            {
                OpenSheet(shell, Shell.RailMode.Queue);
                var rest = At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey));
                var grip = new Point2(rest.X + 24f, rest.Y + ClassicPanelTiers.GrabStripH / 2f);

                // Hover: the vertical-resize arrows.
                Move(shell, grip);
                Frame(shell);
                Assert.Equal(CursorId.SizeNS, shell.Window.LastCursor);

                // A tap moves nothing and closes nothing.
                Down(shell, grip); Frame(shell);
                Up(shell, grip); for (int i = 0; i < 30; i++) Frame(shell);
                Assert.True(Shell.Ui.RailOpen.Peek(), "a tap on the handle closes nothing");
                Assert.Equal(rest.Y, At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y, 0.5f);

                // A slow, short drag follows 1:1 and springs back.
                var at = DragBy(shell, grip, 60f, 20);
                Frame(shell);
                Assert.Equal(rest.Y + 60f, At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y, 1f);
                Assert.Equal(CursorId.SizeNS, shell.Window.LastCursor);
                Up(shell, at);
                for (int i = 0; i < 60; i++) Frame(shell);
                Assert.True(Shell.Ui.RailOpen.Peek(), "short of the threshold the sheet stays");
                Assert.Equal(rest.Y, At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y, 1f);

                // Up past the rest it rubber-bands: it gives, at most the 8 DIP to the title bar's edge.
                at = DragBy(shell, grip, -200f, 10);
                Frame(shell);
                float lift = rest.Y - At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y;
                Assert.True(lift > 1f && lift <= ClassicPanelTiers.Gap + 0.5f, $"the sheet gives {lift} upward, at most {ClassicPanelTiers.Gap}");
                Up(shell, at);
                for (int i = 0; i < 60; i++) Frame(shell);
                Assert.Equal(rest.Y, At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y, 1f);

                // A slow drag past the threshold dismisses.
                at = DragBy(shell, grip, 300f, 30);
                Up(shell, at);
                Frame(shell);
                Assert.False(Shell.Ui.RailOpen.Peek(), "past the threshold the release closes the sheet");
                for (int i = 0; i < 40; i++) Frame(shell);
                Assert.True(At(shell, Node(shell, ClassicPanelTiers.SheetHeadKey)).Y >= H - 1f, "the sheet slid out");

                // A short fling dismisses too.
                OpenSheet(shell, Shell.RailMode.Queue);
                at = DragBy(shell, grip, 40f, 2);
                Up(shell, at);
                Frame(shell);
                Assert.False(Shell.Ui.RailOpen.Peek(), "a downward fling closes the sheet");
            }
            finally { CloseSheet(shell); }
        }, W, H);

    [Fact]
    public void The_context_sheet_takes_the_same_drag()
    {
        int closes = 0;
        var props = new ContextSheetProps
        {
            Header = new ContextMenuHeader(new BoxEl { Width = 38f, Height = 38f }, "Loser", "Tame Impala"),
            Primary = [],
            Rows = [new MenuFlyoutItem("Row", default, true, static () => { }), new MenuFlyoutItem("Other", default, true, static () => { })],
            Close = () => closes++,
        };
        ViewHarness.With(host, ViewKeys.OverlayContextSheet, props, h =>
        {
            var shell = h.Shell;
            h.RunFrames(30);
            var rest = At(shell, h.Node("context-sheet"));
            var grip = new Point2(rest.X + rest.W / 2f, rest.Y + ClassicSheetRules.GrabberZone / 2f);

            var at = DragBy(shell, grip, 40f, 20);
            Frame(shell);
            Assert.Equal(rest.Y + 40f, At(shell, h.Node("context-sheet")).Y, 1f);
            Up(shell, at);
            for (int i = 0; i < 60; i++) Frame(shell);
            Assert.Equal(0, closes);
            Assert.Equal(rest.Y, At(shell, h.Node("context-sheet")).Y, 1f);

            at = DragBy(shell, grip, rest.H * 0.6f, 30);
            Up(shell, at);
            Frame(shell);
            Assert.Equal(1, closes);
        }, new ViewHarnessOptions { Composition = ClassicTheme.Create(), WidthDip = W, HeightDip = H });
    }
}
