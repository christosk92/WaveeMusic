// ── Wavee.ShellTests/ZuneSelectionDockMountedTests.cs — Zune's docked selection bar (P5 Zune fidelity round 2; contract 1.47) ─────────────────
//
// The app mounted under Zune at 1440 x 900 (Standard) on the Liked page: two rows picked with Ctrl-click put the selection bar in the FRAME slot above the
// player bar (`zune.selection-dock`), not in flow in the table's command lane; the slot's height is the page's reservation (the list's viewport is that
// much shorter), the bar carries every verb of the in-flow bar (play, play next, add to queue, like), select all and done; "done" clears the selection and
// the reservation eases back to 0. Classic provides no host: its in-flow bar still draws (checked by `commandbar-mode:selection`).

using FluentGpu.Foundation;
using FluentGpu.Pal;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneSelectionDockMountedTests(ShellHost host)
{
    const float W = 1440f, H = 900f;

    static void ClickAt(Shell.HeadlessShell shell, Point2 p, KeyModifiers mods = KeyModifiers.None, uint at = 1000)
    {
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, mods));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, mods, TimestampMs: at));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, mods, TimestampMs: at + 10));
        shell.RunFrames(2);
    }

    static Point2 CenterOf(Shell.HeadlessShell shell, string key)
    {
        var r = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, key));
        return new Point2(r.X + r.W * 0.5f, r.Y + r.H * 0.5f);
    }

    static Point2 RowPoint(Shell.HeadlessShell shell, int index)
    {
        var rows = shell.FindPrefix("row").Where(r => r.Key == "row").ToList();
        var scene = shell.Host.Scene;
        var rect = scene.PresentedAbsoluteRect(rows[index].Node);
        return new Point2(rect.X + rect.W * 0.5f, rect.Y + rect.H * 0.5f);
    }

    static void PickTwo(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
        Until(shell, () => shell.FindPrefix("list:").Count == 1 && shell.FindPrefix("row").Count > 4, "the liked list and its rows");
        shell.RunFrames(30);
        ClickAt(shell, RowPoint(shell, 2));
        ClickAt(shell, RowPoint(shell, 3), KeyModifiers.Ctrl, at: 2000);
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(4f, 4f), 0, 0));
        shell.RunFrames(60);
    }

    [Fact]
    public void A_selection_docks_its_bar_in_the_frame_and_reserves_the_height_under_the_page()
        => ZuneHost.Mounted(host, shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
            float before = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, ZuneSelectionDockRules.SlotKey)).H;
            Assert.True(before < 0.5f, $"no selection: the slot reserves nothing ({before})");

            PickTwo(shell);
            Assert.NotEmpty(shell.FindPrefix(ZuneSelectionDockRules.BarKey));
            Assert.Empty(shell.FindPrefix("commandbar-mode:selection"));   // no in-flow bar: the docked one is the bar
            float slot = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, ZuneSelectionDockRules.SlotKey)).H;
            Assert.True(MathF.Abs(slot - ZuneSelectionDockRules.HeightFor(WidthTier.Standard)) < 1f, $"the slot is the bar's height ({slot})");

            // every verb of the in-flow bar, then the two ends
            foreach (string verb in new[] { "dock:Play", "dock:PlayNext", "dock:AddToQueue", "dock:ToggleLike", "dock:select-all", "dock:done" })
                Assert.False(shell.Find(verb).IsNull, verb + " is on the docked bar");

            // the bar is the page's foot: nothing above it is covered, it sits right over the player bar
            var bar = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, ZuneSelectionDockRules.SlotKey));
            var player = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, ZuneFrameLayout.PlayerBarKey));
            Assert.True(MathF.Abs(bar.Bottom - player.Y) < 1f, $"the bar ends where the player bar starts ({bar.Bottom} / {player.Y})");

            ClickAt(shell, CenterOf(shell, "dock:done"), at: 4000);
            shell.RunFrames(60);
            float after = shell.Host.Scene.PresentedAbsoluteRect(Node(shell, ZuneSelectionDockRules.SlotKey)).H;
            Assert.True(after < 0.5f, $"done: the reservation eases back to 0 ({after})");
        }, W, H);
}
