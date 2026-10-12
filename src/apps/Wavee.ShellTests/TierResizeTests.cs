// ── Wavee.ShellTests/TierResizeTests.cs — a tier crossing never jumps (themes plan 3.3, 3.5; T3b) ───────────────────────
//
// The real shell frame on the headless host, with the window sized by hand the way the OS sizes it (the engine's own rig
// for this is `TierCrossingTests`; these facts assert the APP's side of the same contract on Classic):
//
//   · a first frame, a navigation and a launch at a size are already in their final tier (no corrective frame);
//   · a RESIZED frame (maximize, restore, a DPI change, a drag's settle paint) lays out at the new size but still publishes
//     the OLD tier and snaps; the first frame that is NOT resized commits the crossing, and the sidebar's column, which
//     declares a layout transition, gets animation tracks on that frame (it FLIPs instead of snapping);
//   · a composited edge drag paints nothing until mouse-up, so it produces exactly ONE crossing, at the settle;
//   · a window dragged back and forth inside a hysteresis band never flips between tiers.
//
// Classic only: its window ladder is the production one (528 / 568, 660 / 700), moved. A theme switch changing no tier needs
// a second installed structure and lands with the first non-Classic theme. Every fact leaves the window at its mount size.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class TierResizeTests(ShellHost host)
{
    // ── what the facts read ────────────────────────────────────────────────────────────────────────────────────────────

    static TierFacts Tiers => ThemeHost.Facts.Peek().Tiers;

    /// <summary>The sidebar's mounted pane (either layout's mode key).</summary>
    static NodeHandle Pane(Shell.HeadlessShell shell)
    {
        var node = shell.Find("sidebar.classic");
        if (node.IsNull) node = shell.Find("sidebar.library");
        Assert.False(node.IsNull, "the sidebar pane is not mounted");
        return node;
    }

    /// <summary>True when the pane or one of its ancestors up to the shell body carries animation tracks: the sidebar column
    /// (an ancestor of the pane) is the node that declares the pane's layout transition.</summary>
    static bool PaneColumnMoves(Shell.HeadlessShell shell)
    {
        var scene = shell.Host.Scene;
        int levels = 8;
        for (var n = Pane(shell); !n.IsNull && levels-- > 0; n = scene.Parent(n))
            if (shell.Host.Animation.HasTracks(n)) return true;
        return false;
    }

    /// <summary>WM_SIZE: the window changes size and one synchronous RESIZED paint follows.</summary>
    static void Resized(Shell.HeadlessShell shell, float widthDip, float heightDip = 760f)
    {
        shell.Resize(widthDip, heightDip);
        shell.Host.Paint(0, keepAlive: true);
    }

    /// <summary>A size change that settles: the resized paint, the frame that commits a crossing, and two to rest.</summary>
    static void Settled(Shell.HeadlessShell shell, float widthDip, float heightDip = 760f)
    {
        Resized(shell, widthDip, heightDip);
        shell.Host.RunFrame();
        shell.RunFrames(2);
    }

    static void AssertBand(WidthTier tier, SidebarWindowBand band)
    {
        Assert.Equal(tier, Tiers.Window);
        Assert.Equal(band, Sidebar.Band.Peek());
    }

    // ── the first frame ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1180f, WidthTier.Standard, SidebarWindowBand.Wide)]
    [InlineData(700f, WidthTier.Standard, SidebarWindowBand.Wide)]
    [InlineData(600f, WidthTier.Small, SidebarWindowBand.Narrow)]
    [InlineData(528f, WidthTier.Small, SidebarWindowBand.Narrow)]
    [InlineData(527f, WidthTier.Compact, SidebarWindowBand.Tiny)]
    [InlineData(400f, WidthTier.Compact, SidebarWindowBand.Tiny)]
    public void Mounting_at_a_size_resolves_its_tiers_in_the_first_frame(float width, WidthTier tier, SidebarWindowBand band)
        => host.Mounted(shell =>
        {
            // Mount ran the first frame already: the tier and the band are final, with no frame run since.
            AssertBand(tier, band);
            Assert.Equal(width, Tiers.ViewportWidth);
            Assert.NotNull(Tiers.Policy);
            Assert.False(Tiers.Resizing);
            shell.RunFrames(3);
            AssertBand(tier, band);   // and nothing corrects it
        }, width, 760f);

    [Fact]
    public void Navigating_at_a_narrow_size_renders_the_narrow_tier_in_its_first_frame()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            shell.Host.RunFrame();
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);
            shell.RunFrames(20);
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);
        }, 400f, 760f);

    // ── a resized frame keeps the old tier; the next frame crosses, animated ───────────────────────────────────────────

    [Fact]
    public void A_resize_across_an_edge_keeps_the_old_tier_on_the_resized_frame_then_crosses_with_tracks()
        => host.Mounted(shell =>
        {
            shell.RunFrames(90);
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            Assert.Equal(SidebarPaneMode.Expanded, Sidebar.Mode.Peek());
            Assert.False(PaneColumnMoves(shell), "the pane was still moving before the resize");

            Resized(shell, 600f);                                        // WM_SIZE: the resized frame
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);      // ... still publishes the OLD tier,
            Assert.Equal(SidebarPaneMode.Expanded, Sidebar.Mode.Peek());
            Assert.False(PaneColumnMoves(shell), "a resized frame snaps: no tracks");

            shell.Host.RunFrame();                                       // the first frame that is not resized commits it
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
            Assert.Equal(SidebarPaneMode.Compact, Sidebar.Mode.Peek());
            Assert.True(PaneColumnMoves(shell), "the crossing snapped the sidebar column instead of animating it");

            shell.RunFrames(90);                                         // and it settles
            Assert.False(PaneColumnMoves(shell));
            Assert.Equal(48f, Sidebar.PresentedWidth.Peek());
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
        }, 1180f, 760f);

    [Fact]
    public void Maximize_then_restore_cross_once_each_way()
        => host.Mounted(shell =>
        {
            shell.RunFrames(90);
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);

            Resized(shell, 1280f);                                       // maximize
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
            shell.Host.RunFrame();
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            Assert.True(PaneColumnMoves(shell));
            shell.RunFrames(90);
            Assert.Equal(SidebarPaneMode.Expanded, Sidebar.Mode.Peek());

            Resized(shell, 600f);                                        // restore
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            shell.Host.RunFrame();
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
            Assert.True(PaneColumnMoves(shell));
            shell.RunFrames(90);
        }, 600f, 760f);

    [Fact]
    public void A_dpi_change_is_a_resize_the_crossing_follows_it()
        => host.Mounted(shell =>
        {
            shell.RunFrames(60);
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);

            shell.Window.Scale = 0.5f;                                   // WM_DPICHANGED: 400 px at 0.5 is 800 DIP
            shell.Host.Paint(0, keepAlive: true);                        // the resized paint
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);

            shell.Host.Paint(0, keepAlive: true);                        // the second paint is not resized
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            shell.RunFrames(60);
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            shell.Window.Scale = 1f;                                     // put the mount's DPI back before it is disposed
            shell.Host.Paint(0, keepAlive: true);
            shell.RunFrames(10);
        }, 400f, 760f);

    // ── a composited edge drag produces one crossing, at the settle ────────────────────────────────────────────────────

    [Fact]
    public void A_composited_drag_paints_nothing_then_crosses_once_at_the_settle()
        => host.Mounted(shell =>
        {
            shell.RunFrames(90);
            shell.Window.Composited = true;
            shell.Window.InModalLoop = true;
            shell.Window.SizedInModalLoop = true;
            var published = new List<WidthTier> { Tiers.Window };

            foreach (float w in new[] { 1000f, 820f, 700f, 640f, 560f, 520f })
            {
                shell.Resize(w, 760f);
                shell.Host.Paint(0, keepAlive: true);                    // composited: the drag's paints are deferred
                if (Tiers.Window != published[^1]) published.Add(Tiers.Window);
                AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
                Assert.False(PaneColumnMoves(shell));
            }
            Assert.True(Tiers.Resizing);                                 // the modal size-move loop is a published fact

            shell.Window.InModalLoop = false;                            // WM_EXITSIZEMOVE
            shell.Window.SizedInModalLoop = false;
            shell.Host.Paint(0, keepAlive: true);                        // the settle paint: laid out at 520, a resized frame
            AssertBand(WidthTier.Standard, SidebarWindowBand.Wide);
            Assert.False(PaneColumnMoves(shell));

            shell.Host.Paint(0, keepAlive: true);                        // the next frame crosses
            if (Tiers.Window != published[^1]) published.Add(Tiers.Window);
            AssertBand(WidthTier.Compact, SidebarWindowBand.Tiny);
            Assert.False(Tiers.Resizing);
            Assert.Equal(new[] { WidthTier.Standard, WidthTier.Compact }, published.ToArray());   // ONE crossing for the whole drag
            shell.RunFrames(90);
        }, 1180f, 760f);

    // ── hysteresis ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_window_dragged_back_and_forth_inside_a_hysteresis_band_never_flips_tiers()
        => host.Mounted(shell =>
        {
            shell.RunFrames(90);
            var seen = new List<WidthTier>();
            foreach (float w in new[] { 650f, 690f, 655f, 699f, 660f, 661f, 700f, 690f, 661f })
            {
                Settled(shell, w);
                seen.Add(Tiers.Window);
            }
            // Standard -> Small at 650; Small holds through 699 (it leaves only at 700); Standard from 700 holds down to 660.
            Assert.Equal(
                new[] { WidthTier.Small, WidthTier.Small, WidthTier.Small, WidthTier.Small, WidthTier.Small, WidthTier.Small,
                        WidthTier.Standard, WidthTier.Standard, WidthTier.Standard },
                seen.ToArray());
            int crossings = 0;
            for (int i = 1; i < seen.Count; i++) if (seen[i] != seen[i - 1]) crossings++;
            Assert.Equal(1, crossings);
            Assert.Equal(SidebarWindowBand.Wide, Sidebar.Band.Peek());
            shell.RunFrames(60);
        }, 1180f, 760f);

    // ── the facts a view reads ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_content_tier_and_width_follow_the_pane_and_the_window()
        => host.Mounted(shell =>
        {
            shell.RunFrames(90);
            var facts = Tiers;
            // The content region is the window less the sidebar column (and an inline rail when one is open).
            Assert.True(facts.ContentWidth > 0f);
            Assert.True(facts.ContentWidth <= facts.ViewportWidth);
            Assert.Equal(WidthTier.Standard, facts.Content);

            Settled(shell, 600f);
            facts = Tiers;
            Assert.Equal(WidthTier.Small, facts.Window);
            Assert.True(facts.ContentWidth <= 600f);
            Assert.True(facts.ContentWidth >= 600f - 48f - 1f);          // the rail pane: the window less its 48-DIP column
            // The content tier crossed WITH the window tier (the estimate sizes the pane the new window tier presents), so a
            // settled resize is one crossing, not a window crossing followed by a content one.
            Assert.Equal(WidthTier.Small, facts.Content);
            shell.RunFrames(60);
            Assert.Equal(facts, Tiers);                                   // and nothing moved after it
        }, 1180f, 760f);
}
