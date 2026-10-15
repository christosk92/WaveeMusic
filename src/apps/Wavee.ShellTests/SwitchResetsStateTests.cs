// ── Wavee.ShellTests/SwitchResetsStateTests.cs — a theme switch resets page UI state like a fresh navigation (plan 5.6, A4.2) ──
//
// Owner Q26: nothing of a page's UI state is carried across a theme switch. The switch runs on the engine's theme transaction
// with the test-only ClassicAlt theme, on the real mounted shell, and what it leaves behind is checked against a fresh mount:
//
//   page state   find text in an album's track list, an open track drawer, an expanded Settings disclosure: after the switch the
//                remounted page has none of them
//   core state   the route, the right rail (the panel) and the full-screen stage are core state: they are exactly as before
//   scroll       a scrolled album lands where a fresh navigation to it lands, in the settled frame the thaw dissolves to (the
//                switch adds no scroll code; production's ScrollPositionMemory decides)
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
    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al0"), "Album");

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

    static void OpenAlbum(Shell.HeadlessShell shell)
    {
        Shell.GoTo(s_album);
        Until(shell, () => !shell.Find(Track.CellKey.Expand).IsNull && shell.FindPrefix("row").Count > 2 && !shell.Find("search:icon").IsNull, "the album's track rows");
        shell.RunFrames(20);
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

    [Fact]
    public void An_expanded_ai_lyrics_card_is_collapsed_after_a_switch()
        => WithAlt(shell =>
        {
            var before = AiLyrics.Current.Peek();
            try
            {
                AiLyrics.Current.Value = AiLyrics.Status.Unknown with
                {
                    Phase = AiLyrics.SetupPhase.Ready, Availability = AiLyrics.Availability.Available, Enabled = true,
                };
                shell.RunFrames(10);
                Settings.Open(Settings.Tab.Appearance);
                Until(shell, () => !shell.Find("ai:ready").IsNull, "the ready AI lyrics card");
                shell.RunFrames(60);
                var card = RevealAiCard(shell);
                float collapsed = shell.Host.Scene.PresentedAbsoluteRect(card).H;
                Click(shell, shell.Host.Scene.FirstChild(card), dx: 60f);
                shell.RunFrames(60);
                Assert.True(shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "ai:ready")).H > collapsed + 20f, "the AI card expanded");

                SwitchTo(shell, ClassicAlt.Id);
                shell.RunFrames(10);
                Settings.Open(Settings.Tab.Appearance);
                Until(shell, () => !shell.Find("ai:ready").IsNull, "the remounted AI lyrics card");
                shell.RunFrames(60);
                RevealAiCard(shell);
                Assert.Equal(collapsed, shell.Host.Scene.PresentedAbsoluteRect(Node(shell, "ai:ready")).H, 1f);
            }
            finally { AiLyrics.Current.Value = before; }
        }, heightDip: 1600f);

    /// <summary>The Ready AI card sits below the fold of the Appearance tab: scroll the page until it is on screen.</summary>
    static NodeHandle RevealAiCard(Shell.HeadlessShell shell)
    {
        var scene = shell.Host.Scene;
        var card = Node(shell, "ai:ready");
        var scroller = scene.Parent(card);
        while (!scroller.IsNull && !scene.HasScroll(scroller)) scroller = scene.Parent(scroller);
        Assert.False(scroller.IsNull, "the Settings page scrolls");
        var handle = shell.Host.TryGetScrollHandle(scroller) ?? throw new InvalidOperationException("no scroll handle on the Settings page");
        handle.ScrollTo(handle.OffsetNow + scene.PresentedAbsoluteRect(card).Y - scene.PresentedAbsoluteRect(scroller).Y - 100.0, ScrollMove.Immediate);
        shell.RunFrames(10);
        return Node(shell, "ai:ready");
    }

    // ── scroll ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_scrolled_album_lands_where_a_fresh_navigation_lands_in_the_settled_frame()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);
            var handle = AlbumScroll(shell);
            Assert.True(handle.MaxOffset > 100.0, "the album scrolls at this window size (max " + handle.MaxOffset + ")");
            handle.ScrollTo(Math.Min(400.0, handle.MaxOffset), ScrollMove.Immediate);
            shell.RunFrames(5);
            Assert.True(AlbumScroll(shell).OffsetNow > 50.0, "the album scrolled");

            // The offset in the frame the thaw dissolves to (read at commit: the settled, final frame).
            double atThaw = double.NaN;
            var t = ThemeHost.Switch(ClassicAlt.Id);
            for (int i = 0; i < 300 && !t.IsCompleted; i++)
            {
                shell.RunFrames(1);
                if (shell.Host.FreezeState == FreezePhase.Dissolving && double.IsNaN(atThaw)) atThaw = AlbumScroll(shell).OffsetNow;
            }
            Assert.Equal(ThemeTransactionOutcome.Committed, t.Result.Outcome);
            if (double.IsNaN(atThaw)) atThaw = AlbumScroll(shell).OffsetNow;
            shell.RunFrames(30);
            Assert.Equal(atThaw, AlbumScroll(shell).OffsetNow, 0.5);   // no "top, then jump" after the thaw

            // A fresh navigation to the same album (away and back), scrolled the same way first.
            AlbumScroll(shell).ScrollTo(Math.Min(400.0, AlbumScroll(shell).MaxOffset), ScrollMove.Immediate);
            shell.RunFrames(5);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(30);
            OpenAlbum(shell);
            double fresh = AlbumScroll(shell).OffsetNow;
            Assert.Equal(fresh, atThaw, 0.5);
        }, heightDip: 500f);

    static ScrollHandle AlbumScroll(Shell.HeadlessShell shell)
    {
        var root = Single(shell, "trail:").Node;
        var vp = ScrollUnder(shell, root);
        Assert.False(vp.IsNull, "the album's scroller");
        return shell.Host.TryGetScrollHandle(vp) ?? throw new InvalidOperationException("no scroll handle on the album's scroller");
    }
}
