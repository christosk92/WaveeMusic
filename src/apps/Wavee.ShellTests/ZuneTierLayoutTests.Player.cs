// ── Wavee.ShellTests/ZuneTierLayoutTests.Player.cs — Zune's play bar and right panel at every tier size and edge (plan 3.5; P5 ZT2) ──
//
// On the mounted app under Zune, at every size of the harness (`FrameSizes`: the plan sizes and every edge), on the facts and the records:
//
//   THE BAR      its slot is the record's (the reserved 72, 88 at Huge 4K) at the window's foot, the bar in it the record's (the mini 64 at
//                Compact), full width; the level is one the window tier may use (and, at a size reached fresh, the one the ladder 0 / 640 /
//                760 / 1240 / 1920 / 3200 names), found by its identity column; the disc is the level's (40, 48 at Huge 4K).
//   THE PANEL    opened, it takes the tier's placement: a full-width sheet at Compact, a 340 overlay at Small that leaves the page its width,
//                inline 340 at Standard, docked 720 (queue | lyrics · friends) at Huge and 1080 (queue | lyrics | friends) at Huge 4K, the
//                columns flush right in that order, the page column meeting them; the primary's head leaves the docked bodies out and the
//                linked panes draw no close.
//   CROSSINGS    across 640, 1008, 1919/1920 (1895 from above) and 3199/3200 (3175 from above) with the panel open: the resized frame keeps
//                the old arrangement, the first frame that is not resized commits the new one, every host keeps its node, and a host that
//                moves or resizes glides on a size channel (its own, a spacer beside it or over it); the bar's slot eases 72 <-> 88.
//
// Pure rules (the panel per tier and the caps) are pinned here too, beside the mounted facts that read them.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    const string PlayerTrack = "spotify:track:tr40";

    // ── the rules ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_is_a_sheet_an_overlay_inline_then_two_and_three_docked_columns()
    {
        var panel = ZunePolicies.For(ZuneParameters.Default).Panel;
        Assert.Empty(panel.Validate());
        Assert.Equal(ZunePanelTiers.PanelW, panel.MinWidth);
        Assert.Equal(ZunePanelTiers.PanelW, panel.MaxWidth);
        Assert.Equal(PanelPlacement.Sheet, panel.For(WidthTier.Compact).Placement);
        Assert.Equal(PanelPlacement.Float, panel.For(WidthTier.Small).Placement);
        Assert.Equal(PanelPlacement.Dock, panel.For(WidthTier.Standard).Placement);
        Assert.Equal(0, panel.For(WidthTier.Standard).Arrangement.Count);
        Assert.Equal(0f, panel.For(WidthTier.Standard).FloorFor(1440f));

        // Huge: queue | lyrics · friends, linked, 2 x 360.
        var huge = panel.For(WidthTier.Huge);
        Assert.Equal(ZunePanelTiers.HugePanes, huge.Arrangement);
        Assert.Equal(new DockedPane(PanelMode.Queue, PaneSide.Before), huge.Arrangement.Second);
        Assert.True(huge.Arrangement.Linked);
        Assert.Equal(ZunePanelTiers.DockedPaneW, huge.FloorFor(1920f));
        Assert.Equal(ZunePanelTiers.DockedPaneW, huge.FloorFor(3199f));
        Assert.Equal(720f, ZunePanelTiers.Panel(WidthTier.Huge).DockedW);

        // Huge 4K: queue | lyrics | friends, linked, 3 x 360.
        var big = panel.For(WidthTier.Huge4K);
        Assert.True(big.DocksThree);
        Assert.Equal(new DockedPane(PanelMode.Friends, PaneSide.After), big.Arrangement.Third);
        Assert.Equal(1080f, ZunePanelTiers.Panel(WidthTier.Huge4K).DockedW);
        Assert.Equal(340f, ZunePanelTiers.Panel(WidthTier.Standard).DockedW);
        Assert.Equal(0f, ZunePanelTiers.Panel(WidthTier.Small).DockedW);

        // A pane sliding out on a crossing keeps its body and side.
        Assert.Equal(new DockedPane(PanelMode.Friends, PaneSide.After), panel.PaneAt(1));
        Assert.Equal(new DockedPane(PanelMode.Queue, PaneSide.Before), panel.PaneAt(0));
    }

    [Theory]
    [InlineData(3, 2, 3)]
    [InlineData(2, 2, 2)]   // Panorama: queue | lyrics · friends at Huge 4K too
    [InlineData(1, 1, 1)]   // the 340 panel at every docking tier
    [InlineData(9, 2, 3)]   // clamped to 1-3
    public void Max_docked_panes_caps_the_columns(int maxDockedPanes, int huge, int huge4K)
    {
        var panel = ZunePolicies.For(ZuneParameters.Default with { MaxDockedPanes = maxDockedPanes }).Panel;
        Assert.Empty(panel.Validate());
        Assert.Equal(huge, 1 + panel.For(WidthTier.Huge).Arrangement.Count);
        Assert.Equal(huge4K, 1 + panel.For(WidthTier.Huge4K).Arrangement.Count);
        Assert.Equal(huge > 1 ? ZunePanelTiers.DockedPaneW : 0f, panel.For(WidthTier.Huge).FloorFor(2560f));   // one column: the 340 panel, no floor
    }

    [Fact]
    public void Always_overlay_floats_at_every_desktop_tier_and_docks_nothing()
    {
        var panel = ZunePolicies.For(ZuneParameters.Default with { PanelMode = ZunePanelMode.AlwaysOverlay }).Panel;
        Assert.Empty(panel.Validate());
        Assert.Equal(PanelPlacement.Sheet, panel.For(WidthTier.Compact).Placement);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            Assert.Equal(PanelPlacement.Float, panel.For(tier).Placement);
            Assert.Equal(0, panel.For(tier).Arrangement.Count);
        }
    }

    // ── the bar at every size ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_play_bar_holds_its_tier_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        var scene = shell.Host.Scene;
        Play(PlayerTrack, fromMs: 20_000);
        UntilBackground(shell, () => !shell.Find(ZunePlayerLayout.DiscKey).IsNull && !shell.Find(ZunePlayerLayout.LeftKey).IsNull,
            $"{size}: the Zune bar's disc and identity mounted");
        shell.RunFrames(40);

        var rec = ZunePlayerTiers.BarOf(tiers);
        Assert.Equal(s.Window, rec.Tier);
        var slot = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.PlayerBarKey));
        var bar = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.BarKey));
        Assert.Equal(rec.SlotH, slot.H, 0.5f);                                     // the reserved 72 (88 at Huge 4K) at every tier
        Assert.Equal(s.Height, slot.Y + slot.H, 0.5f);                            // at the window's foot
        Assert.Equal(rec.BarH, bar.H, 0.5f);                                       // the mini 64 inside it at Compact
        Assert.Equal(slot.Y + slot.H, bar.Y + bar.H, 0.5f);                        // at the slot's foot
        Assert.Equal(s.Width, bar.W, 0.5f);

        var disc = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.DiscKey));
        var inBar = new HashSet<NodeHandle>(Subtree(scene, Node(shell, ZunePlayerLayout.BarKey)));
        RectF InBar(string key) => scene.AbsoluteRect(shell.FindPrefix(key).Single(f => f.Key == key && inBar.Contains(f.Node)).Node);

        // The rhythm (ZT2): the seek line lies on the top edge, so the row is the whole bar; the cover and the disc are centred in it.
        float barFoot = bar.Y + bar.H;
        Assert.Equal(bar.H, scene.AbsoluteRect(Node(shell, ZunePlayerLayout.RowKey)).H, 0.5f);
        var art = InBar("art");
        Assert.True(MathF.Abs((art.Y - bar.Y) - (barFoot - art.Y - art.H)) <= 1f, $"{size}: the cover sits {art.Y - bar.Y} / {barFoot - art.Y - art.H}");

        // The margins (ZT2): the cover starts on the frame's gutter (the mini bar's 16 at Compact).
        var leftCol = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.LeftKey));
        var lookNow = rec.Form == ZuneBarForm.Mini ? ZunePlayerTiers.Look(ZuneBarLevel.Mini) : null;
        if (rec.Form == ZuneBarForm.Mini)
        {
            Assert.Equal(ZuneBarLevel.Mini, rec.MaxLevel);
            Assert.Equal(ZunePlayerTiers.RowInsets(s.Window, lookNow!).Left, leftCol.X, 0.5f);
            Assert.Equal(ZuneTiers.FrameOf(tiers).Gutter, leftCol.X, 0.5f);
            Assert.True(disc.W >= 40f - 0.5f && disc.H >= 40f - 0.5f, $"{size}: the mini bar's disc is {disc.W} x {disc.H}, under 40 x 40");
            Assert.True(MathF.Abs((disc.Y - bar.Y) - (barFoot - disc.Y - disc.H)) <= 1f, $"{size}: the mini disc is off centre");
            return;
        }

        // The level, found by its identity column (each level's is distinct), is one the tier may use; fresh, the ladder's.
        float idW = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.LeftKey)).W;
        ZuneBarLook? look = null;
        for (var level = rec.MinLevel; level <= rec.MaxLevel; level++)
            if (MathF.Abs(ZunePlayerTiers.Look(level).IdW - idW) < 0.5f) look = ZunePlayerTiers.Look(level);
        Assert.True(look is not null, $"{size}: the identity column is {idW}, no level of {rec.MinLevel}-{rec.MaxLevel}");
        if (!s.Resized) Assert.Equal(ExpectedLevel(s.Width, rec), look!.Level);
        Assert.Equal(look!.Disc, disc.W, 0.5f);                                    // 40, 48 at Huge 4K
        Assert.Equal(disc.W, disc.H, 0.5f);

        // Left on the frame's gutter, the last tool's glyph on the gutter at the right.
        var insets = ZunePlayerTiers.RowInsets(s.Window, look);
        Assert.Equal(ZuneTiers.FrameOf(tiers).Gutter, insets.Left);
        Assert.Equal(insets.Left, leftCol.X, 0.5f);
        var tools = scene.AbsoluteRect(Node(shell, ZunePlayerLayout.RightKey));
        Assert.Equal(s.Width - insets.Right, tools.X + tools.W, 0.5f);
        Assert.Equal(ZuneTiers.FrameOf(tiers).Gutter, s.Width - (tools.X + tools.W) + (look.Button - look.Glyph) / 2f, 1f);

        // The transport cluster (T1). From full the times stand BESIDE the transport, 16 from its buttons in 40 slots, and the disc is
        // centred in the bar; at narrow and medium they stack under the 36 disc row (the row carries the gap as its top padding), the whole
        // cluster centred with 8 / 8 of air in the 72.
        float discTop = disc.Y - bar.Y, discBottom = barFoot - (disc.Y + disc.H);
        if (look.TimesBeside)
        {
            Assert.True(MathF.Abs(discTop - discBottom) <= 1f, $"{size}: the disc has {discTop} above and {discBottom} below");
            Assert.DoesNotContain(shell.FindPrefix("times"), f => f.Key == "times" && inBar.Contains(f.Node));
            var elapsed = InBar("elapsed-side");
            var remaining = InBar("remaining-side");
            var prev = InBar("prev");
            var next = InBar("next");
            Assert.Equal(ZuneTransportLayout.TimeSideGap, prev.X - (elapsed.X + elapsed.W), 0.75f);
            Assert.Equal(ZuneTransportLayout.TimeSideGap, remaining.X - (next.X + next.W), 0.75f);
            Assert.True(elapsed.W >= ZuneTransportLayout.TimeSlotW - 0.5f, $"{size}: the elapsed slot is {elapsed.W}, under 40");
            Assert.True(remaining.W >= ZuneTransportLayout.TimeSlotW - 0.5f, $"{size}: the remaining slot is {remaining.W}, under 40");
            // Both times sit on the disc's centre line.
            float discMid = disc.Y + disc.H / 2f;
            Assert.Equal(discMid, elapsed.Y + elapsed.H / 2f, 1f);
            Assert.Equal(discMid, remaining.Y + remaining.H / 2f, 1f);
        }
        else
        {
            var times = InBar("times");
            Assert.Equal(disc.Y + disc.H, times.Y, 0.75f);                                   // the row starts at the disc row's foot
            Assert.Equal(look.TimesGap + ZuneTransportLayout.TimeLine, times.H, 0.75f);     // its gap, then the 16 line
            float bottom = barFoot - (times.Y + times.H);
            Assert.True(MathF.Abs(discTop - bottom) <= 1f, $"{size}: the transport cluster has {discTop} above and {bottom} below");
            Assert.True(bottom >= 8f - 0.5f, $"{size}: the times sit {bottom} from the bar's foot");
        }
    });

    /// <summary>The level a window <paramref name="width"/> wide reaches fresh: the highest whose start it passes, clamped to the tier's range.</summary>
    static ZuneBarLevel ExpectedLevel(float width, ZunePlayerBarTier rec)
    {
        float[] from = [0f, ZunePlayerTiers.NarrowFrom, ZunePlayerTiers.MediumFrom, ZunePlayerTiers.FullFrom, ZunePlayerTiers.WideFrom, ZunePlayerTiers.BigFrom];
        int level = 0;
        for (int i = 0; i < from.Length; i++) if (width >= from[i]) level = i;
        return (ZuneBarLevel)Math.Clamp(level, (int)rec.MinLevel, (int)rec.MaxLevel);
    }

    // ── the panel at every size ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_panel_takes_its_tier_placement(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            shell.RunFrames(40);
            AssertPanelTier(shell, s.Width, ZunePanelTiers.Panel(tiers.Window));
            AssertRegions(shell, s);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    [Theory]
    [InlineData(2560f, 1440f, 2)]   // Z-Frame-Huge: queue | lyrics · friends
    [InlineData(3840f, 2160f, 3)]   // Z-Frame-Huge4K: queue | lyrics | friends
    public void The_huge_tiers_dock_their_columns_in_the_boards_order(float width, float height, int columns) => Mounted(shell =>
    {
        try
        {
            shell.RunFrames(20);
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            shell.RunFrames(40);
            var rec = ZunePanelTiers.Panel(Tiers.Window);
            Assert.Equal(columns, rec.Columns);
            Assert.Equal(rec.Rule.Arrangement, Shell.Ui.PanelPanes.Peek());          // the core published the tier's arrangement
            Assert.True(Shell.Ui.SecondPaneAllowed.Peek());
            AssertPanelTier(shell, width, rec);

            // The queue's door closes the whole panel: the queue is on screen in its pane.
            Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
            shell.RunFrames(40);
            Assert.False(Shell.Ui.RailOpen.Peek());
            Assert.False(Shell.PanelHost.Shared.SecondOpenNow());

            // Opened on the queue, the primary steps aside to lyrics (the board's docked2 with the queue picked).
            Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
            shell.RunFrames(40);
            Assert.True(Shell.PanelHost.Shared.SecondOpenNow());
            var primary = new HashSet<NodeHandle>(Subtree(shell.Host.Scene, Node(shell, ZuneFrameRules.PanelHostKey)));
            Assert.Contains(shell.FindPrefix(ZuneFrameRules.PanelPivotKeyPrefix + PanelMode.Lyrics), f => primary.Contains(f.Node));
            AssertPanelTier(shell, width, rec);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    }, width, height);

    /// <summary>The open panel at window width <paramref name="width"/> is laid out as <paramref name="rec"/> says.</summary>
    static void AssertPanelTier(Shell.HeadlessShell shell, float width, ZunePanelTier rec)
    {
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
        string what = $"{width} ({rec.Tier})";
        Assert.Equal(rec.Rule.Placement, Shell.Ui.PanelPlacement.Peek());
        Assert.True(Shell.Ui.RailOpen.Peek(), $"{what}: the panel is open");
        var primary = At(ZuneFrameRules.PanelHostKey);
        var content = At(ZuneFrameLayout.ContentKey);
        var panes = rec.Rule.Arrangement;

        switch (rec.Rule.Placement)
        {
            case PanelPlacement.Sheet:
                Assert.Equal(width, primary.W, 0.5f);                              // full width, from the left edge
                Assert.Equal(0f, primary.X, 0.5f);
                Assert.Equal(width, content.W, 0.5f);                              // nothing reserved
                break;
            case PanelPlacement.Float:
                Assert.Equal(ZunePanelTiers.PanelW, primary.W, 0.5f);              // the 340 overlay, flush right
                Assert.Equal(width, primary.X + primary.W, 0.5f);
                Assert.Equal(width, content.W, 0.5f);                              // the content does not move
                break;
            default:
                // Every column of the arrangement is open, the panel is the record's width and the page column meets it.
                int open = 1;
                for (int i = 0; i < panes.Count; i++) if (Shell.PanelHost.Shared.PaneOpenNow(i)) open++;
                Assert.True(rec.Columns == open, $"{what}: {open} columns open, not {rec.Columns}");
                Assert.Equal(width - rec.DockedW, content.X + content.W, 0.5f);

                // Left to right: the panes before the primary (nearest it last), the primary, the panes after it (nearest it first).
                var order = new List<(string Key, PanelMode? Mode)>();
                for (int i = panes.Count - 1; i >= 0; i--)
                    if (panes.At(i) is { Side: PaneSide.Before } before) order.Add((ZunePanelTiers.PaneHostKey(i), before.Mode));
                order.Add((ZuneFrameRules.PanelHostKey, null));
                for (int i = 0; i < panes.Count; i++)
                    if (panes.At(i) is { Side: PaneSide.After } after) order.Add((ZunePanelTiers.PaneHostKey(i), after.Mode));
                float column = rec.Columns > 1 ? ZunePanelTiers.DockedPaneW : ZunePanelTiers.PanelW;
                float x = width - rec.DockedW;
                foreach (var (key, mode) in order)
                {
                    var r = At(key);
                    Assert.True(MathF.Abs(r.X - x) < 0.5f && MathF.Abs(r.W - column) < 0.5f, $"{what}: {key} is {r}, not {column} wide at {x}");
                    x += column;
                    if (mode is not { } body) continue;
                    // A docked pane draws its one body's word and no close (it closes with the panel).
                    var pane = new HashSet<NodeHandle>(Subtree(scene, Node(shell, key)));
                    Assert.Contains(shell.FindPrefix(ZuneFrameRules.PanelPivotKeyPrefix + body), f => pane.Contains(f.Node));
                    Assert.DoesNotContain(shell.FindPrefix(ZuneFrameRules.PanelCloseKey), f => pane.Contains(f.Node));
                }
                Assert.Equal(width, x, 0.5f);

                // The primary's head leaves the docked bodies out; it keeps the one close.
                var inPrimary = new HashSet<NodeHandle>(Subtree(scene, Node(shell, ZuneFrameRules.PanelHostKey)));
                for (int i = 0; i < panes.Count; i++)
                {
                    var mode = panes.At(i)!.Value.Mode;
                    Assert.DoesNotContain(shell.FindPrefix(ZuneFrameRules.PanelPivotKeyPrefix + mode), f => inPrimary.Contains(f.Node));
                }
                Assert.Single(shell.FindPrefix(ZuneFrameRules.PanelCloseKey), f => f.Key == ZuneFrameRules.PanelCloseKey && inPrimary.Contains(f.Node));
                break;
        }
    }

    // ── crossings with the panel open ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The panel's crossings both ways: from, to, the height both sit at.</summary>
    public static TheoryData<float, float, float> PanelCrossings() => new()
    {
        { 400f, 800f, 860f }, { 800f, 615f, 860f },        // 640: sheet <-> overlay
        { 800f, 1440f, 960f }, { 1440f, 983f, 960f },      // 1008: overlay <-> inline
        { 1919f, 1920f, 1056f }, { 1920f, 1895f, 1056f },  // 1920: one column <-> queue | lyrics · friends
        { 3199f, 3200f, 1440f }, { 3200f, 3175f, 1440f },  // 3200: two columns <-> three; the bar's slot 72 <-> 88
    };

    [Theory, MemberData(nameof(PanelCrossings))]
    public void A_panel_crossing_keeps_its_panes_and_eases_them(float from, float to, float height) => Mounted(shell =>
    {
        var scene = shell.Host.Scene;
        try
        {
            shell.RunFrames(20);
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            shell.RunFrames(40);
            var before = Tiers;
            int columnsBefore = OpenColumns();
            Assert.Equal(Math.Max(1, ZunePanelTiers.Panel(before.Window).Columns), columnsBefore);

            string[] keys = [ZuneFrameRules.PanelHostKey, ZunePanelTiers.PaneHostKey(0), ZunePanelTiers.PaneHostKey(1), ZuneFrameLayout.PlayerBarKey];
            var nodes = keys.Select(k => Node(shell, k)).ToArray();

            // The resized frame: the old tier and the old arrangement, laid out at the new size.
            bool narrowing = to < from;
            shell.Window.Composited = true;
            if (narrowing)
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(to, height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Host.Paint(0, keepAlive: true);
            }
            else
            {
                shell.Resize(to, height);
                shell.Host.Paint(0, keepAlive: true);
            }
            Assert.Equal(before.Window, Tiers.Window);
            Assert.Equal(columnsBefore, OpenColumns());
            var rects = nodes.Select(n => scene.AbsoluteRect(n)).ToArray();   // the old tier's boxes at the new size

            // The crossing frame: the new tier and its arrangement commit here.
            if (narrowing) shell.Host.Paint(0, keepAlive: true); else shell.Host.RunFrame();
            var window = ZuneTierAt(to);
            Assert.True(window == Tiers.Window, $"the window tier is {Tiers.Window}, not {window}");
            Assert.Equal(Math.Max(1, ZunePanelTiers.Panel(window).Columns), OpenColumns());
            var gliding = nodes.Select(n => Glides(shell, n)).ToArray();

            // Every host is the same node (the panes keep their identity), and one that moved or resized glided on a size channel.
            string what = $"{from} -> {to}";
            shell.RunFrames(40);
            for (int i = 0; i < keys.Length; i++)
            {
                Assert.True(scene.IsLive(nodes[i]), $"{what}: {keys[i]} was thrown away on the crossing");
                Assert.True(shell.Find(keys[i]).Equals(nodes[i]), $"{what}: {keys[i]} was remounted on the crossing");
                var now = scene.AbsoluteRect(nodes[i]);
                bool resized = MathF.Abs(now.W - rects[i].W) > 0.5f || MathF.Abs(now.H - rects[i].H) > 0.5f;
                bool moved = MathF.Abs(now.X - rects[i].X) > 0.5f || MathF.Abs(now.Y - rects[i].Y) > 0.5f;
                bool open = i == 0 || i == keys.Length - 1 || Shell.PanelHost.Shared.PaneOpenNow(i - 1);
                if (Design.Reduced || !open || !(resized || moved)) continue;
                Assert.True(gliding[i], $"{what}: {keys[i]} snapped into its new box ({rects[i]} -> {now}) instead of gliding");
            }

            // Settled: the new tier's records.
            AssertPanelTier(shell, to, ZunePanelTiers.Panel(Tiers.Window));
            Assert.Equal(ZunePlayerTiers.BarOf(Tiers).SlotH, scene.AbsoluteRect(Node(shell, ZuneFrameLayout.PlayerBarKey)).H, 0.5f);
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    }, from, height);

    /// <summary>The columns on screen: the primary (open) and every docked pane that is open.</summary>
    static int OpenColumns()
    {
        int open = Shell.Ui.RailOpen.Peek() ? 1 : 0;
        for (int i = 0; i < 2; i++) if (Shell.PanelHost.Shared.PaneOpenNow(i)) open++;
        return open;
    }

    /// <summary>The host glides on the crossing frame: it, an ancestor, a box beside it in its row (the spacer that places it) or a box beside
    /// its row (the floating panel's top spacer) holds animation tracks.</summary>
    static bool Glides(Shell.HeadlessShell shell, NodeHandle host)
    {
        var scene = shell.Host.Scene;
        var anim = shell.Host.Animation;
        for (var n = host; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
            if (anim.HasTracks(n)) return true;
        foreach (var parent in new[] { scene.Parent(host), scene.Parent(scene.Parent(host)) })
        {
            if (parent.IsNull) continue;
            for (var c = scene.FirstChild(parent); !c.IsNull; c = scene.NextSibling(c))
                if (anim.HasTracks(c)) return true;
        }
        return false;
    }
}
