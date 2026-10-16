// ── Wavee.ShellTests/TierLayoutTests.Player.cs — the player bar and the side panels at every tier size (themes plan 3.5; TC2) ──────────
//
// TC2's partial of `TierLayoutTests`: the REAL Classic frame with Home on screen, at every plan 3.5 size and threshold edge, with no panel,
// one panel (lyrics) and two (lyrics and the queue beside it). On the records, never on pixels:
//
//   · the bar is its tier's record: the 56 mini bar at Compact (every target in it at least 40 x 40), 72 elsewhere (Short included);
//   · the panel is placed by its tier's record: a sheet across the window at Compact, floating 340 wide 8 in from the right at Small,
//     docked beside the page at Standard and Huge (the content card ends where the panel's band begins: no overlap);
//   · two panels dock only at Huge (D3), side by side, each at the tier's floor (380 at 2560, 420 at 3840); elsewhere the queue beside
//     the panel is not shown;
//   · the panels lie inside the window and the content region never runs under a docked one.

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
    [Theory, MemberData(nameof(FrameSizes))]
    public void The_player_bar_is_its_tiers_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        var record = ClassicPlayerTiers.BarOf(tiers);
        Assert.Equal(s.Window == WidthTier.Compact ? ClassicBarForm.Mini : s.Window >= WidthTier.Huge ? ClassicBarForm.Huge : ClassicBarForm.Regular, record.Form);
        var bar = shell.Host.Scene.AbsoluteRect(Node(shell, ClassicFrameLayout.PlayerBarKey));
        Assert.Equal(record.Height, bar.H, 0.5f);
        Assert.Equal(s.Window == WidthTier.Compact ? 56f : 72f, bar.H, 0.5f);   // a Short window keeps its tier's bar (C-Shell-Short: 72)
        if (record.Form == ClassicBarForm.Mini)
        {
            Assert.False(shell.Find("mini-door").IsNull, $"{s.Name}: the mini bar's identity door");
            AssertTargetsAtLeast40(shell, Node(shell, ClassicFrameLayout.PlayerBarKey), s.Name);
        }
        else Assert.True(shell.Find("mini-door").IsNull, $"{s.Name}: no mini door outside Compact");
    });

    [Theory, MemberData(nameof(FrameSizes))]
    public void One_panel_is_placed_by_its_tiers_record(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            shell.RunFrames(40);
            var rule = ClassicPanelTiers.Of(tiers);
            var placement = rule.Rule.Placement;
            Assert.Equal(placement, Shell.Ui.PanelPlacement.Peek());
            Assert.Equal(placement == PanelPlacement.Dock, Shell.Ui.RailFits.Peek());
            Assert.False(Shell.PanelHost.Shared.SecondOpenNow());

            var scene = shell.Host.Scene;
            var host = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.HostKey));
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            var title = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.TitleBarKey));
            var bar = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.PlayerBarKey));
            Assert.True(host.X >= -0.5f && host.X + host.W <= s.Width + 0.5f, $"{s.Name}: the panel runs outside the window ({host})");
            Assert.True(host.Y >= title.Y + title.H - 0.5f && host.Y + host.H <= s.Height + 0.5f, $"{s.Name}: the panel runs outside the body ({host})");
            switch (placement)
            {
                case PanelPlacement.Sheet:     // C-Queue-Compact: the window's width, 8 under the title bar, down over the mini bar
                    Assert.Equal(s.Width, host.W, 0.5f);
                    Assert.Equal(title.Y + title.H + rule.Inset, host.Y, 0.5f);
                    Assert.Equal(s.Height, host.Y + host.H, 0.5f);
                    break;
                case PanelPlacement.Float:     // C-Queue-Small: 340, 8 from the right edge, the title bar and the bar; the page keeps its width
                    Assert.Equal(rule.FloatWidth, host.W, 0.5f);
                    Assert.Equal(s.Width - rule.Inset, host.X + host.W, 0.5f);
                    Assert.Equal(bar.Y - rule.Inset, host.Y + host.H, 0.5f);
                    Assert.Equal(s.Width, card.X + card.W, 0.5f);
                    break;
                default:                       // docked: the page ends where the panel's 8-DIP gap begins
                    Assert.True(card.X + card.W + Shell.FrameRules.RailGapW <= host.X + 0.5f, $"{s.Name}: the content card runs under the docked panel");
                    Assert.Equal(s.Width, host.X + host.W, 0.5f);
                    Assert.Equal(bar.Y, host.Y + host.H, 0.5f);
                    Assert.Equal(MathF.Max(Shell.Ui.RailWidth.Peek(), rule.Rule.FloorFor(s.Width)), host.W, 0.5f);
                    break;
            }
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            shell.RunFrames(30);
        }
    });

    [Theory, MemberData(nameof(FrameSizes))]
    public void Two_panels_dock_side_by_side_only_at_huge(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        try
        {
            Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
            Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
            shell.RunFrames(40);
            bool two = ClassicPanelTiers.Of(tiers).Rule.DocksTwo;
            Assert.Equal(s.Window >= WidthTier.Huge, two);
            Assert.Equal(two, Shell.PanelHost.Shared.SecondOpenNow());
            if (!two)
            {
                // One panel shows where one docks: the queue's door switched the panel to the queue.
                Assert.Equal(Shell.RailMode.Queue, Shell.Ui.Mode.Peek());
                return;
            }
            Assert.Equal(Shell.RailMode.Lyrics, Shell.Ui.Mode.Peek());   // Lyrics + Queue (C-Shell-Huge)
            var scene = shell.Host.Scene;
            var primary = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.HostKey));
            var second = scene.AbsoluteRect(Node(shell, ClassicPanelTiers.SecondHostKey));
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            float floor = ClassicPanelTiers.Huge.Rule.FloorFor(s.Width);
            Assert.Equal(floor, second.W, 0.5f);
            Assert.True(primary.W >= floor - 0.5f, $"{s.Name}: the primary pane is {primary.W}, under the floor {floor}");
            Assert.Equal(s.Width, second.X + second.W, 0.5f);
            Assert.Equal(second.X - Shell.FrameRules.RailGapW, primary.X + primary.W, 0.5f);   // 8 between the two panes
            Assert.True(card.X + card.W + Shell.FrameRules.RailGapW <= primary.X + 0.5f, $"{s.Name}: the content card runs under the panes");
            Assert.True(card.W > 0f, $"{s.Name}: the page keeps a width beside two panes");
        }
        finally
        {
            Shell.PanelHost.Shared.Close();
            Shell.PanelHost.Shared.CloseQueuePane();
            shell.RunFrames(30);
        }
    });

    /// <summary>Every focusable, visible node under <paramref name="root"/> is at least 40 x 40 (the Compact target floor).</summary>
    static void AssertTargetsAtLeast40(Shell.HeadlessShell shell, NodeHandle root, string sizeName)
    {
        var scene = shell.Host.Scene;
        int targets = 0;
        foreach (var n in Subtree(scene, root))
        {
            if (n.Equals(root) || scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = scene.AbsoluteRect(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            targets++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{sizeName}: a mini-bar target is {r.W} x {r.H}, under 40 x 40");
        }
        Assert.True(targets >= 2, $"{sizeName}: the mini bar offers its targets (found {targets})");
    }
}
