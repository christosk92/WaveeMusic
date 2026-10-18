// ── Wavee.ShellTests/ZuneSharedEdgeMountedTests.cs — the band, the page column and the title bar share one left edge (P5 Zune fidelity round 3) ─────
//
// A docked panel narrows the CONTENT area but not the window: at a Standard window (1280 wide) with the 340 queue panel docked, the content is 940 wide
// (Small), so the page lays out on the Small gutter (24) while the window is still Standard (gutter 48). The band's lead inset and the title bar's
// wordmark used to read the window tier, standing 24 DIP right of the page ("verified artist", the name, the rows). They read the content tier now:
// the first pivot, the title bar's wordmark and the artist page's copy start on the same x, with the panel closed (Standard gutter) and open (Small's).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneSharedEdgeMountedTests(ShellHost host)
{
    const float W = 1280f, H = 900f;

    static float X(Shell.HeadlessShell shell, string key) => shell.Host.Scene.AbsoluteRect(Node(shell, key)).X;

    static void AssertShared(Shell.HeadlessShell shell, float gutter, string when)
    {
        float pivot = X(shell, ZuneBandRules.PivotKeyPrefix + "home");
        float caption = X(shell, ZuneArtistRules.CaptionKey);
        float wordmark = X(shell, ZuneFrameRules.WordmarkKey);
        Assert.True(MathF.Abs(pivot - gutter) < 1.5f, $"{when}: the first pivot stands on the gutter {gutter} ({pivot})");
        Assert.True(MathF.Abs(caption - pivot) < 1.5f, $"{when}: the page copy ({caption}) starts where the band's lead does ({pivot})");
        Assert.True(MathF.Abs(wordmark - pivot) < 2.5f, $"{when}: the wordmark ({wordmark}) is on the same edge ({pivot})");
    }

    [Fact]
    public void A_docked_panel_leaves_the_band_the_page_and_the_title_bar_on_one_left_edge()
        => ZuneHost.Mounted(host, shell =>
        {
            if (Shell.Ui.RailOpen.Peek()) { Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue); shell.RunFrames(60); }   // the shared host may carry an open panel
            if (Shell.Ui.RailOpen.Peek()) { Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue); shell.RunFrames(60); }
            Shell.GoTo(Shell.For(EntityUri.Parse("spotify:artist:ar0"), "Artist"));
            UntilBackground(shell, () => !shell.Find(ZuneArtistRules.CaptionKey).IsNull, "the artist's hero copy");
            shell.RunFrames(40);
            var closed = ThemeHost.Facts.Peek().Tiers;
            Assert.True(closed.Window == WidthTier.Standard, $"window {closed.Window} content {closed.Content}/{closed.ContentWidth} viewport {closed.ViewportWidth} rail {Shell.Ui.RailOpen.Peek()}/{Shell.Ui.RailWidth.Peek()} beside {Shell.Ui.QueueBeside.Peek()} sidebar {Sidebar.Width.Peek()} collapsed {Sidebar.UserCollapsed.Peek()}");
            Assert.True(closed.Content == WidthTier.Standard, $"content {closed.Content} {closed.ContentWidth} rail {Shell.Ui.RailOpen.Peek()} {Shell.Ui.RailWidth.Peek()} beside {Shell.Ui.QueueBeside.Peek()} sidebar {Sidebar.Width.Peek()} collapsed {Sidebar.UserCollapsed.Peek()}");
            AssertShared(shell, ZuneTiers.Frame(WidthTier.Standard).Gutter, "panel closed");

            Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
            try
            {
                shell.RunFrames(90);
                var docked = ThemeHost.Facts.Peek().Tiers;
                Assert.Equal(WidthTier.Standard, docked.Window);
                Assert.True(docked.Content < WidthTier.Standard, "the docked panel narrows the content under Standard (" + docked.Content + ")");
                AssertShared(shell, ZuneTiers.Frame(docked.Content).Gutter, "panel docked");
            }
            finally
            {
                Shell.PanelHost.Shared.Toggle(Shell.RailMode.Queue);
                shell.RunFrames(40);
            }
        }, W, H);
}
