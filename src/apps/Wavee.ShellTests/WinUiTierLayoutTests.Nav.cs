// ── Wavee.ShellTests/WinUiTierLayoutTests.Nav.cs — the nav pane at every tier (P6 W2b) ─────────────────────────────────────────────────
//
// What only a mounted shell can say about WinUI's pane, on the facts and the rects, never on pixels:
//
//   · the Settings footer stands at the pane's foot (the docked pane at Standard and Huge, the rail at Small) and does not scroll with the list;
//   · the Small rail's tiles are the board's 40 wide, the Standard pane's rows 36 tall (the board's ladder);
//   · the overlay (the hamburger at Compact and Small) never pushes the page: the content card does not move or resize while it is open, and the
//     overlay pane is the 280 the policy fixes;
//   · `navigationPane = Compact` holds the 48 rail at Standard and Huge too (the core seam `NavPanePolicy.WideMode`), and the overlay opens over it
//     without moving the page;
//   · the overlay's targets at Compact are at least 40 x 40 (the touch ladder).

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class WinUiTierLayoutTests
{
    static RectF ContentCard(Shell.HeadlessShell shell) => shell.Host.Scene.AbsoluteRect(Node(shell, WinUiFrameLayout.ContentKey));

    static void Settle(Shell.HeadlessShell shell)
    {
        Until(shell, () => shell.FindPrefix(Home.ReadyKeyPrefix).Count > 0, "Home on screen under WinUI");
        shell.RunFrames(60);
    }

    static void OpenOverlay(Shell.HeadlessShell shell)
    {
        Assert.True(SidebarPaneModeRules.HasOverlay(Sidebar.Band.Peek(), ShellPolicy.Now.NavPane), "the band has an overlay to open");
        Sidebar.OverlayOpen.Value = true;
        shell.RunFrames(60);
    }

    [Theory]
    [InlineData(1440f, 960f)]
    [InlineData(2560f, 1440f)]
    public void The_docked_pane_ends_in_the_settings_footer_at_its_foot(float width, float height)
        => Mounted(shell =>
        {
            Settle(shell);
            var scene = shell.Host.Scene;
            var footer = shell.FindPrefix("winui-nav-settings");
            Assert.Single(footer);
            var row = scene.AbsoluteRect(footer[0].Node);
            Assert.Equal(WinUiTiers.PaneW, scene.AbsoluteRect(Node(shell, "pane-layer")).W, 0.5f);
            var layer = scene.AbsoluteRect(Node(shell, "pane-layer"));
            Assert.InRange(row.Y + row.H, layer.Y + layer.H - WinUiNavGeometry.PaneEdge - WinUiNavGeometry.RowMarginY - 0.5f, layer.Y + layer.H + 0.5f);   // the foot, not under the list
            Assert.Equal(36f, row.H, 0.5f);                                                                                    // the board's row
            Assert.Equal(WinUiTiers.PaneW - 2f * WinUiNavGeometry.PaneEdge, row.W, 0.5f);
            var home = scene.AbsoluteRect(shell.FindPrefix("home").First(n => n.Key == "home").Node);
            Assert.Equal(36f, home.H, 0.5f);
            Assert.True(home.Y + home.H < row.Y, "the footer is under the list");
        }, width, height);

    [Fact]
    public void The_small_rail_draws_40_wide_tiles_and_the_footer_tile()
        => Mounted(shell =>
        {
            Settle(shell);
            var scene = shell.Host.Scene;
            Assert.Equal(WinUiTiers.RailW, Sidebar.PresentedWidth.Peek(), 0.5f);
            Assert.Equal(WinUiNavGeometry.TileWidth, scene.AbsoluteRect(shell.FindPrefix("home").First(n => n.Key == "home").Node).W, 0.5f);
            Assert.Equal(WinUiNavGeometry.TileWidth, scene.AbsoluteRect(shell.FindPrefix("winui-nav-settings").First().Node).W, 0.5f);
        }, 800f, 600f);

    [Theory]
    [InlineData(400f, 860f)]
    [InlineData(800f, 600f)]
    public void The_overlay_never_pushes_the_page(float width, float height)
        => Mounted(shell =>
        {
            Settle(shell);
            var before = ContentCard(shell);
            OpenOverlay(shell);
            var after = ContentCard(shell);
            Assert.Equal(before.X, after.X, 0.5f);
            Assert.Equal(before.W, after.W, 0.5f);
            Assert.Equal(before.Y, after.Y, 0.5f);
            Assert.Equal(before.H, after.H, 0.5f);
            var pane = shell.Host.Scene.AbsoluteRect(Node(shell, "drawer-pane"));
            Assert.Equal(WinUiTiers.PaneW, pane.W, 0.5f);
            Assert.True(pane.X >= -0.5f, "the open pane is on screen");
            Sidebar.OverlayOpen.Value = false;
            shell.RunFrames(60);
            var closed = ContentCard(shell);
            Assert.Equal(before.X, closed.X, 0.5f);
            Assert.Equal(before.W, closed.W, 0.5f);
        }, width, height);

    [Fact]
    public void The_overlays_targets_at_Compact_are_at_least_40()
        => Mounted(shell =>
        {
            Settle(shell);
            OpenOverlay(shell);
            var scene = shell.Host.Scene;
            int checkedTargets = 0;
            foreach (var n in Subtree(scene, Node(shell, "drawer-pane")))
            {
                if (scene.InCollapsedSubtree(n)) continue;
                var flags = scene.Flags(n);
                if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
                var r = scene.AbsoluteRect(n);
                if (r.W <= 0f || r.H <= 0f || r.W < 20f) continue;   // the list's own scrollbar track is the engine's, not a nav target
                Assert.True(r.W >= 39.5f && r.H >= 39.5f, $"a nav target is {r.W} x {r.H}, under 40 x 40");
                checkedTargets++;
            }
            Assert.True(checkedTargets >= 8, $"only {checkedTargets} focusable nav targets were found in the overlay");
        }, 400f, 860f);

    [Theory]
    [InlineData(1440f, 960f)]
    [InlineData(2560f, 1440f)]
    public void The_compact_option_holds_the_rail_at_Standard_and_Huge_and_the_overlay_opens_over_it(float width, float height)
        => Mounted(shell =>
        {
            Settle(shell);
            Assert.Equal(WinUiTiers.PaneW, Sidebar.PresentedWidth.Peek(), 0.5f);
            var t = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiStructure.NavigationPaneQualifiedId, WinUiNavigationPane.Compact));
            shell.RunFrames(90);
            Assert.True(t.IsCompletedSuccessfully);
            Assert.Equal(WinUiTiers.RailW, Sidebar.PresentedWidth.Peek(), 0.5f);
            Assert.Equal(SidebarPaneMode.Compact, Sidebar.Mode.Peek());
            var before = ContentCard(shell);
            OpenOverlay(shell);
            var after = ContentCard(shell);
            Assert.Equal(before.X, after.X, 0.5f);
            Assert.Equal(before.W, after.W, 0.5f);
            Sidebar.OverlayOpen.Value = false;
            var back = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiStructure.NavigationPaneQualifiedId, WinUiNavigationPane.Automatic));
            shell.RunFrames(90);
            Assert.True(back.IsCompletedSuccessfully);
            Assert.Equal(WinUiTiers.PaneW, Sidebar.PresentedWidth.Peek(), 0.5f);
        }, width, height);
}
