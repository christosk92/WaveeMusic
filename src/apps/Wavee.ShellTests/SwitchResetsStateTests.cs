// ── Wavee.ShellTests/SwitchResetsStateTests.cs — a theme switch resets page UI state like a fresh navigation (plan 5.6, A4.2) ──
//
// Owner Q26: nothing of a page's UI state is carried across a theme switch. The switch runs on the engine's theme transaction
// with the test-only ClassicAlt theme, on the real mounted shell, and what it leaves behind is checked against a fresh mount:
//
//   page state   find text in an album's track list, an open track drawer, an expanded Settings disclosure: after the switch the
//                remounted page has none of them
//   core state   the route, the right rail (the panel) and the full-screen stage are core state: they are exactly as before
//   scroll       a scrolled track list and a scrolled album keep their offsets in the settled frame the thaw dissolves to,
//                where a navigation back lands (the switch adds no scroll code: production's ScrollPositionMemory decides, by the
//                scrollers' ScrollKeys); an album evicted from KeepAlive reopens at its offset the same way
//
// Page UI state has no public setter, so the facts drive it the way a user does: pointer and character input on keyed nodes.

using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scroll.Runtime;
using Wavee;
using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class SwitchResetsStateTests(ShellHost host)
{
    const float W = 1600f, H = 900f;
    // al2: the fixture's twelve-track album (its first rows carry the track drawer's fixtures), long enough to scroll at 1600 x 500.
    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");

    void WithAlt(Action<Shell.HeadlessShell> body, float heightDip = H)
    {
        host.Run(static () => ThemeHost.Install(ClassicTheme.Create(), ClassicAlt.Create()));
        try { host.Mounted(body, W, heightDip); }
        finally { host.Run(ClassicAlt.RestoreClassic); }
    }

    static ThemeTransactionResult SwitchTo(Shell.HeadlessShell shell, string id)
    {
        var t = ThemeHost.Switch(id);
        for (int i = 0; i < 300 && !t.IsCompleted; i++) shell.RunFrames(1);
        Assert.True(t.IsCompletedSuccessfully, "the switch completed");
        Assert.Equal(ThemeTransactionOutcome.Committed, t.Result.Outcome);
        return t.Result;
    }

    static void Click(Shell.HeadlessShell shell, NodeHandle node, float dx = float.NaN)
    {
        var r = shell.Host.Scene.PresentedAbsoluteRect(node);
        ClickAt(shell, new Point2(float.IsNaN(dx) ? r.X + r.W / 2f : r.X + dx, r.Y + r.H / 2f));
    }

    static void ClickAt(Shell.HeadlessShell shell, Point2 p)
    {
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 1000));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 1010));
        shell.RunFrames(2);
    }

    static void Type(Shell.HeadlessShell shell, string text)
    {
        foreach (char c in text) shell.Window.QueueInput(new InputEvent(InputKind.Char, default, 0, c));
        shell.RunFrames(2);
    }

    /// <summary>Navigate to the album and wait for its SETTLED layout, not its shimmer: the page holds a reserve for its trailing
    /// sections until they are ready or for at most <c>PageRules.TrailingDeadlineMs</c> (400 ms), and under <c>--fake</c> some never
    /// are, so the content height changes once, at that deadline. Settled = the album's scroll extent unchanged for 40 frames
    /// (640 ms on the headless clock), well past the deadline.</summary>
    static void OpenAlbum(Shell.HeadlessShell shell)
    {
        Shell.GoTo(s_album);
        Until(shell, () => !shell.Find(Track.CellKey.Expand).IsNull && shell.FindPrefix("row").Count > 2 && !shell.Find("search:icon").IsNull
                           && shell.FindPrefix(AlbumTrail).Count == 1, "the album's track rows");
        WaitSettled(shell);
    }

    /// <summary>Run frames until the album's scroll extent has been the same for 40 frames in a row.</summary>
    static void WaitSettled(Shell.HeadlessShell shell)
    {
        double last = double.NaN;
        int stable = 0;
        for (int i = 0; i < 600 && stable < 40; i++)
        {
            shell.RunFrames(1);
            double max = shell.FindPrefix(AlbumTrail).Count == 1 ? AlbumScroll(shell).MaxOffset : double.NaN;
            stable = max.Equals(last) ? stable + 1 : 0;
            last = max;
        }
        Assert.True(stable >= 40, "the album's layout settled (extent " + last + ")");
    }

    // ── page state ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_text_and_an_open_drawer_are_gone_after_a_switch_while_route_panel_and_stage_stay()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);

            // The chevron lane: the header carries the lane's key; a row's chevron sits in the same column (the last), so click
            // the first row at that column.
            var lane = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, Track.CellKey.Expand));
            var rows = shell.FindPrefix("row").Where(k => k.Key == "row")
                .Select(k => shell.Host.Scene.PresentedAbsoluteRect(k.Node)).Where(r => r.H > 0f && r.Y > lane.Y).OrderBy(r => r.Y).ToList();
            Assert.NotEmpty(rows);
            ClickAt(shell, new Point2(lane.X + lane.W / 2f, rows[0].Y + rows[0].H / 2f));
            shell.RunFrames(30);
            Assert.True(shell.FindPrefix("drawer:").Count > 0, "a track drawer opened");

            // Find text: the find box opens and keeps its query.
            Click(shell, Node(shell, "search:icon"));
            Until(shell, () => !shell.Find("search:field").IsNull, "the find box opened");
            shell.RunFrames(3);
            Type(shell, "zzzq");   // a query that matches nothing: the rows filter away
            shell.RunFrames(20);
            Assert.False(shell.Find("search:field").IsNull);   // a query keeps it open

            var route = Shell.Current.Peek();
            bool rail = Shell.Ui.RailOpen.Peek();
            var railMode = Shell.Ui.Mode.Peek();
            bool stage = !shell.Find("stage:cover").IsNull;

            SwitchTo(shell, ClassicAlt.Id);
            Until(shell, () => !shell.Find("search:icon").IsNull, "the remounted album's toolbar");
            shell.RunFrames(10);

            Assert.True(shell.Find("search:field").IsNull, "the find box is closed: its query went with the old page");
            Assert.Empty(shell.FindPrefix("drawer:"));
            Assert.Equal(route, Shell.Current.Peek());
            Assert.Equal(rail, Shell.Ui.RailOpen.Peek());
            Assert.Equal(railMode, Shell.Ui.Mode.Peek());
            Assert.Equal(stage, !shell.Find("stage:cover").IsNull);
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void An_expanded_disclosure_is_collapsed_after_a_switch()
        => WithAlt(shell =>
        {
            bool crossfade = Platform.Settings.Get(Platform.Keys.CrossfadeEnabled);
            try
            {
                Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, false);   // the card's initial state is "collapsed"
                shell.RunFrames(10);
                Settings.Open(Settings.Tab.Playback);
                Until(shell, () => !shell.Find("playback.crossfade").IsNull, "the crossfade card");
                shell.RunFrames(30);
                Assert.True(shell.Find("crossfade-duration-off").IsNull, "the card starts collapsed");
                var header = shell.Host.Scene.FirstChild(Node(shell, "playback.crossfade"));
                Click(shell, header, dx: 60f);
                shell.RunFrames(30);
                Assert.False(shell.Find("crossfade-duration-off").IsNull, "the disclosure expanded");

                SwitchTo(shell, ClassicAlt.Id);
                shell.RunFrames(10);
                Settings.Open(Settings.Tab.Playback);
                Until(shell, () => !shell.Find("playback.crossfade").IsNull, "the remounted crossfade card");
                shell.RunFrames(30);
                Assert.True(shell.Find("crossfade-duration-off").IsNull, "the disclosure mounted collapsed, like a fresh navigation");
            }
            finally { Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, crossfade); }
        }, heightDip: 1600f);

    // ── scroll ───────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // A switch adds no scroll code: a remounted scroller lands where production's ScrollPositionMemory puts a navigation back to
    // it, in the first layout (EP12), so the frame the thaw dissolves to already has it. Every page scroller carries a ScrollKey
    // (a track list's viewport `list:`, the album page's outer scroller `trail:album:`), so the switch gives the remembered
    // offset back, the same offset a navigation away and back gives. Each fact waits for the SETTLED page (the album holds a
    // shimmer reserve for up to 400 ms after mount) and picks content long enough to scroll at its window size.

    [Fact]
    public void A_scrolled_track_list_keeps_its_remembered_offset_in_the_settled_frame()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Until(shell, () => shell.FindPrefix(LikedList).Count == 1 && ListScroll(shell).MaxOffset > 1000.0, "the Liked list with rows to scroll");
            shell.RunFrames(20);
            ListScroll(shell).ScrollTo(400.0, ScrollMove.Immediate);
            shell.RunFrames(5);
            Assert.Equal(400.0, ListScroll(shell).OffsetNow, 0.5);

            var (atThaw, _) = SwitchReading(shell, ListScroll);
            Assert.Equal(400.0, atThaw, 0.5);                            // restored in the settled frame, not after it
            shell.RunFrames(30);
            Assert.Equal(400.0, ListScroll(shell).OffsetNow, 0.5);       // and nothing moves it after the thaw
        }, heightDip: 500f);

    [Fact]
    public void A_scrolled_album_lands_where_a_fresh_navigation_lands_in_the_settled_frame()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);

            // The control: a fresh navigation to the scrolled album (away and back).
            OpenAlbum(shell);
            var handle = AlbumScroll(shell);
            Assert.True(handle.MaxOffset > 100.0, "the settled album scrolls at this window size (max " + handle.MaxOffset + ")");
            handle.ScrollTo(Math.Min(300.0, handle.MaxOffset), ScrollMove.Immediate);
            shell.RunFrames(5);
            Assert.True(AlbumScroll(shell).OffsetNow > 50.0, "the album scrolled");
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(30);
            OpenAlbum(shell);
            double fresh = AlbumScroll(shell).OffsetNow;
            Assert.True(fresh > 50.0, "a navigation back to the album keeps its offset (" + fresh + ")");

            // The switch, from the same scrolled state.
            AlbumScroll(shell).ScrollTo(Math.Min(300.0, AlbumScroll(shell).MaxOffset), ScrollMove.Immediate);
            shell.RunFrames(5);
            double scrolled = AlbumScroll(shell).OffsetNow;
            Assert.True(scrolled > 50.0, "the album scrolled again");
            var (atThaw, _) = SwitchReading(shell, AlbumScroll);
            Assert.Equal(scrolled, atThaw, 0.5);                        // restored in the settled frame (the album's ScrollKey)
            Assert.Equal(fresh, atThaw, 0.5);                           // ... which is where a navigation back lands
            WaitSettled(shell);
            Assert.Equal(fresh, AlbumScroll(shell).OffsetNow, 0.5);     // no jump after the thaw, nor at the reserve's deadline
        }, heightDip: 500f);

    [Fact]
    public void An_album_evicted_from_keep_alive_reopens_at_its_offset_in_its_first_layout()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);
            Assert.True(AlbumScroll(shell).MaxOffset > 100.0, "the settled album scrolls at this window size");
            AlbumScroll(shell).ScrollTo(Math.Min(300.0, AlbumScroll(shell).MaxOffset), ScrollMove.Immediate);
            shell.RunFrames(5);
            double scrolled = AlbumScroll(shell).OffsetNow;
            Assert.True(scrolled > 50.0);

            // More pages than the KeepAlive slots (3): the album's page is dropped, its scroller destroyed.
            foreach (var kind in new[] { Shell.RouteKind.Home, Shell.RouteKind.Liked, Shell.RouteKind.Browse, Shell.RouteKind.Recents })
            {
                Shell.GoTo(new Shell.Route(kind));
                shell.RunFrames(30);
            }
            Assert.Empty(shell.FindPrefix(AlbumTrail));

            Shell.GoTo(s_album);
            double first = double.NaN;
            for (int i = 0; i < 240 && double.IsNaN(first); i++)
            {
                shell.RunFrames(1);
                if (shell.FindPrefix(AlbumTrail).Count == 1) first = AlbumScroll(shell).OffsetNow;
            }
            Assert.Equal(scrolled, first, 0.5);                       // the first frame that has the scroller has the offset
            WaitSettled(shell);
            Assert.Equal(scrolled, AlbumScroll(shell).OffsetNow, 0.5);
        }, heightDip: 500f);

    /// <summary>Switch to ClassicAlt and read <paramref name="scroller"/>'s offset in the first frame of the dissolve: the settled
    /// frame the user sees.</summary>
    static (double AtThaw, ThemeTransactionResult Result) SwitchReading(Shell.HeadlessShell shell, Func<Shell.HeadlessShell, ScrollHandle> scroller)
    {
        double atThaw = double.NaN;
        var t = ThemeHost.Switch(ClassicAlt.Id);
        for (int i = 0; i < 300 && (!t.IsCompleted || double.IsNaN(atThaw)); i++)
        {
            shell.RunFrames(1);
            if (double.IsNaN(atThaw) && shell.Host.FreezeState == FreezePhase.Dissolving) atThaw = scroller(shell).OffsetNow;
        }
        Assert.True(t.IsCompletedSuccessfully);
        Assert.Equal(ThemeTransactionOutcome.Committed, t.Result.Outcome);
        Assert.False(double.IsNaN(atThaw), "the thaw dissolved");
        return (atThaw, t.Result);
    }

    static ScrollHandle ListScroll(Shell.HeadlessShell shell)
    {
        var vp = ScrollUnder(shell, Single(shell, LikedList).Node);
        Assert.False(vp.IsNull, "the list's viewport");
        return shell.Host.TryGetScrollHandle(vp) ?? throw new InvalidOperationException("no scroll handle on the list's viewport");
    }

    const string AlbumTrail = "trail:album:";
    const string LikedList = "list:liked:";

    static ScrollHandle AlbumScroll(Shell.HeadlessShell shell)
    {
        var root = Single(shell, AlbumTrail).Node;
        var vp = ScrollUnder(shell, root);
        Assert.False(vp.IsNull, "the album's scroller");
        return shell.Host.TryGetScrollHandle(vp) ?? throw new InvalidOperationException("no scroll handle on the album's scroller");
    }
}
