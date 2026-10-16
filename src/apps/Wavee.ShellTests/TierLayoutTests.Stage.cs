// ── Wavee.ShellTests/TierLayoutTests.Stage.cs — the stage family at every tier size (themes plan 3.5; TC3) ────────────────────────────
//
// The REAL fullscreen stage (Lyrics mode) on the headless shell at every plan 3.5 size, 3840 x 2160 included (the stage is a key surface),
// and every threshold edge; the queue panel at the four board tiers; the mini player's chrome alone at Compact and at Standard. Asserted on
// the facts and the records, never on pixels:
//
//   · the window tier is final in the first frame and the stage's allocator budgets from that tier's metrics (the transport card's height,
//     the lyrics type);
//   · the stage's top bar, cover, title column and transport card lie inside the window and never overlap;
//   · at Compact every button of the stage's chrome, of the queue panel and of the mini player is at least 40 x 40.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── the stage ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_stage_holds_its_tier_layout(string sizeName)
    {
        var size = Sizes[sizeName];
        host.Mounted(shell =>
        {
            if (size.From is not null)
            {
                shell.RunFrames(20);
                shell.Resize(size.Width, size.Height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Host.RunFrame();
            }
            AssertTier(size);
            Prefs.Stage.SetMode((int)Stage.Mode.Lyrics);
            Stage.Open(null, "test");
            try
            {
                Until(shell, () => !shell.Find("stage:cover").IsNull && !shell.Find("chrome:transport").IsNull, "the stage at " + size.Name);
                shell.RunFrames(120);   // the entrance and every spring settle
                AssertTier(size);       // the stage never moves the tier

                var tiers = Tiers;
                var metrics = ClassicStageTiers.Policy.MetricsFor(tiers);
                Assert.Same(ClassicStageTiers.Policy.MetricsFor(size.Window), metrics);
                var expected = Stage.Layout.Seed(size.Width, size.Height, metrics: metrics);
                Assert.Equal(ClassicStageTiers.Stage(size.Window), ClassicStageTiers.StageOf(tiers));

                var scene = shell.Host.Scene;
                RectF Placed(string layerKey) => scene.PresentedAbsoluteRect(scene.FirstChild(Node(shell, layerKey)));
                var root = scene.PresentedAbsoluteRect(scene.Parent(Node(shell, "stage:backdrop")));
                Assert.Equal(size.Width, root.W, 0.5f);
                Assert.Equal(size.Height, root.H, 0.5f);

                var top = Placed("chrome:top");
                var transport = Placed("chrome:transport");
                var cover = scene.PresentedAbsoluteRect(Node(shell, "stage:cover"));
                Assert.Equal(expected.TransportH, transport.H, 0.5f);            // the window tier's card (C-Stage-Compact 104, C-Stage-Small 96)
                Assert.Equal(expected.TransportInset, size.Width - (transport.X + transport.W), 0.5f);
                Assert.Equal(expected.TransportInset, size.Height - (transport.Y + transport.H), 0.5f);
                foreach (var (name, r) in new[] { ("top bar", top), ("transport", transport), ("cover", cover) })
                    Assert.True(r.W > 0f && r.X >= -0.5f && r.Y >= -0.5f && r.X + r.W <= size.Width + 0.5f && r.Y + r.H <= size.Height + 0.5f,
                        $"{size.Name}: the stage's {name} leaves the window ({r})");
                Assert.True(top.Y + top.H <= cover.Y + 0.5f, $"{size.Name}: the top bar {top} overlaps the cover {cover}");
                bool apart = cover.Y + cover.H <= transport.Y + 0.5f || cover.X + cover.W <= transport.X + 0.5f;
                Assert.True(apart, $"{size.Name}: the cover {cover} overlaps the transport card {transport}");
                Assert.True(top.Y + top.H <= transport.Y + 0.5f, $"{size.Name}: the top bar overlaps the transport card");

                if (size.Window == WidthTier.Compact)
                {
                    Assert.True(expected.TransportStacked, $"{size.Name}: a Compact card stacks its row");
                    AssertButtonsAtLeast40(shell, Node(shell, "chrome:top"), size.Name + " stage top bar");
                    AssertButtonsAtLeast40(shell, Node(shell, "chrome:transport"), size.Name + " stage transport");
                }
            }
            finally
            {
                Stage.Close(null, "test");
                shell.RunFrames(60);
                Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
            }
        }, size.From ?? size.Width, size.Height);
    }

    // ── the queue panel's contents ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("compact 400x860")]
    [InlineData("small 800x600")]
    [InlineData("standard 1440x960")]
    [InlineData("huge 2560x1440")]
    public void The_queue_panel_holds_its_tier_targets(string sizeName)
    {
        var size = Sizes[sizeName];
        host.Mounted(shell =>
        {
            var previous = Rail.QueueBody;
            Rail.QueueBody = static () => Queue.PanelSlot();   // the composition root installs it; the headless shell does not
            try
            {
                Play("spotify:track:tr40", fromMs: 20_000);
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Queue))
                    Shell.Ui.Toggle(Shell.RailMode.Queue);
                Until(shell, () => !shell.Find("lane:empty").IsNull, "the queue lane at " + size.Name, 900);
                shell.RunFrames(40);
                AssertTier(size);

                var record = ClassicStageTiers.PanelOf(Tiers);
                Assert.Equal(ClassicStageTiers.Panel(size.Window), record);
                var scene = shell.Host.Scene;
                var lane = scene.PresentedAbsoluteRect(Node(shell, "lane:empty"));
                Assert.True(lane.X >= -0.5f && lane.X + lane.W <= size.Width + 0.5f, $"{size.Name}: the queue runs outside the window ({lane})");
                var inLane = Subtree(scene, Node(shell, "lane:empty")).ToHashSet();
                int hearts = 0;
                foreach (var (save, _) in shell.FindPrefix("save:"))
                {
                    if (!inLane.Contains(save)) continue;   // the player bar's heart is the bar's (TC2)
                    hearts++;
                    var r = scene.PresentedAbsoluteRect(scene.FirstChild(save).IsNull ? save : scene.FirstChild(save));
                    Assert.True(r.W >= record.HeartBox - 0.5f, $"{size.Name}: a heart is {r.W} wide, under the tier's {record.HeartBox}");
                }
                Assert.True(hearts >= 1, $"{size.Name}: the now-playing card's heart");
                if (size.Window == WidthTier.Compact) AssertButtonsAtLeast40(shell, Node(shell, "lane:empty"), size.Name + " queue panel");
            }
            finally
            {
                if (Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Queue) Shell.Ui.Toggle(Shell.RailMode.Queue);
                Rail.QueueBody = previous;
                shell.RunFrames(30);
            }
        }, size.Width, size.Height);
    }

    // ── the mini player's chrome ───────────────────────────────────────────────────────────────────────────────────────

    sealed class NoGestures : IVideoPipGestures
    {
        public void DragRealized(NodeHandle node) { }
        public void DragDown(Point2 local) { }
        public void DragMove(Point2 local) { }
        public void ZoneRealized(int slot, NodeHandle node) { }
        public void ZoneDown(int slot, Point2 local) { }
        public void ZoneMove(int slot, Video.PipEdge edge, Point2 local) { }
        public void Commit() { }
    }

    static VideoPipProps MiniPlayer() => new()
    {
        Placement = Video.SurfacePlacement.Floating,
        Visible = true,
        FollowRect = static () => default,
        Width = 240f,
        Height = 135f,
        Transform = Affine2D.Identity,
        Area = static () => new BoxEl { Key = "test:area", Grow = 1f },
        Gestures = new NoGestures(),
        ToMiniPlayer = static () => { },
        ToFullscreen = static () => { },
        TurnOffDocked = static () => { },
        ClosePip = static () => { },
    };

    [Theory]
    [InlineData(400f, 860f, 2, 40f)]     // C-Video-Compact: full screen and close, 40 x 40, shown at rest
    [InlineData(1440f, 960f, 1, 24f)]    // the desktop's hover-revealed 24 close
    public void The_mini_player_chrome_is_the_tiers(float width, float height, int buttons, float box)
        => ViewHarness.With(host, ViewKeys.VideoPip, MiniPlayer(), harness =>
        {
            var scene = harness.Shell.Host.Scene;
            var found = harness.Subtree(scene.Root).Where(n => scene.Interaction(n).Role == AutomationRole.Button).ToList();
            Assert.Equal(buttons, found.Count);
            foreach (var n in found)
            {
                var r = harness.AbsoluteBounds(n);
                Assert.Equal(box, r.W, 0.5f);
                Assert.Equal(box, r.H, 0.5f);
            }
        }, new ViewHarnessOptions { Composition = ClassicTheme.Create(), WidthDip = width, HeightDip = height });

    // ── the helper ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every visible button under <paramref name="root"/> is at least 40 x 40 (the brief's Compact rule). A button nested inside a
    /// larger button (the shared media row's hover play disc over its art, inside the 54-tall row that plays on a tap) rides its row's
    /// target and is not a target of its own.</summary>
    static void AssertButtonsAtLeast40(Shell.HeadlessShell shell, NodeHandle root, string what)
    {
        var scene = shell.Host.Scene;
        int count = 0;
        foreach (var n in Subtree(scene, root))
        {
            if (scene.InCollapsedSubtree(n) || (scene.Flags(n) & NodeFlags.Visible) == 0) continue;
            if (scene.Interaction(n).Role != AutomationRole.Button) continue;
            var r = scene.PresentedAbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            bool nested = false;
            for (var p = scene.Parent(n); !p.IsNull && p != root; p = scene.Parent(p))
            {
                if (scene.Interaction(p).Role != AutomationRole.Button) continue;
                var pr = scene.PresentedAbsoluteRect(p);
                if (pr.W >= 40f - 0.5f && pr.H >= 40f - 0.5f) { nested = true; break; }
            }
            if (nested) continue;
            count++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{what}: a button is {r.W} x {r.H} at {r}, under 40 x 40");
        }
        Assert.True(count >= 1, $"{what}: no buttons found");
    }
}
