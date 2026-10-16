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
// Classic only: its window ladder is the board's (`C-Breakpoints`, TC1): Compact < 528, Small 528-1007, Standard 1008-1919, Huge from
// 1920, held 40 on widening at 528 and 1008 (567 and 1047 stay put from below) and on narrowing at 1920 (1880 holds Huge, 1879 does
// not). A theme switch changing no tier needs a second installed structure and lands with the first non-Classic theme. Every fact
// leaves the window at its mount size.

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
    [InlineData(1920f, WidthTier.Huge, SidebarWindowBand.Wide)]
    [InlineData(1180f, WidthTier.Standard, SidebarWindowBand.Wide)]
    [InlineData(1008f, WidthTier.Standard, SidebarWindowBand.Wide)]
    [InlineData(1007f, WidthTier.Small, SidebarWindowBand.Narrow)]
    [InlineData(700f, WidthTier.Small, SidebarWindowBand.Narrow)]     // the forced rail covers the whole Small band
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
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
            shell.RunFrames(60);
            AssertBand(WidthTier.Small, SidebarWindowBand.Narrow);
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
            foreach (float w in new[] { 1000f, 1040f, 1010f, 1047f, 1008f, 1009f, 1048f, 1040f, 1009f })
            {
                Settled(shell, w);
                seen.Add(Tiers.Window);
            }
            // Standard -> Small at 1000; Small holds through 1047 (it leaves only at 1048); Standard from 1048 holds down to 1008.
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

    // ── the board's holds, from each side (C-Breakpoints, NQ12) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 567f, WidthTier.Compact, 568f, WidthTier.Small)]       // widening past 528 holds Compact to 567
    [InlineData(800f, 1047f, WidthTier.Small, 1048f, WidthTier.Standard)]    // widening past 1008 holds Small to 1047
    [InlineData(2560f, 1880f, WidthTier.Huge, 1879f, WidthTier.Standard)]    // narrowing past 1920 holds Huge to 1880
    public void A_hold_edge_keeps_the_tier_the_window_came_from(float start, float held, WidthTier heldTier, float crossed, WidthTier crossedTier)
        => host.Mounted(shell =>
        {
            shell.RunFrames(30);
            Settled(shell, held);
            Assert.Equal(heldTier, Tiers.Window);                        // the hold: the window keeps its tier
            Settled(shell, crossed);
            Assert.Equal(crossedTier, Tiers.Window);                     // one DIP further it crosses, once
            shell.RunFrames(30);
            Assert.Equal(crossedTier, Tiers.Window);
        }, start, 760f);

    [Theory]
    [InlineData(567f, WidthTier.Small)]       // a FRESH launch inside a hold uses the edge itself (NQ12)
    [InlineData(1047f, WidthTier.Standard)]
    [InlineData(1879f, WidthTier.Standard)]
    public void A_fresh_launch_inside_a_hold_uses_the_edge_itself(float width, WidthTier tier)
        => host.Mounted(shell =>
        {
            Assert.Equal(tier, Tiers.Window);
            shell.RunFrames(3);
            Assert.Equal(tier, Tiers.Window);
        }, width, 760f);

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
            // The content tier is the gutter ladder's for the window less the pane (1180 less a 320 pane is 860: Small, 32).
            Assert.Equal((WidthTier)Wavee.Themes.Classic.ClassicTiers.Content.Resolve(facts.ContentWidth, -1), facts.Content);

            Settled(shell, 1440f);
            facts = Tiers;
            Assert.Equal(WidthTier.Standard, facts.Content);             // 1120: Standard (36), past 880 + its 24 hold

            Settled(shell, 600f);
            facts = Tiers;
            Assert.Equal(WidthTier.Small, facts.Window);
            Assert.True(facts.ContentWidth <= 600f);
            Assert.True(facts.ContentWidth >= 600f - 48f - 1f);          // the rail pane: the window less its 48-DIP column
            // The content tier crossed WITH the window tier (the estimate sizes the pane the new window tier presents), so a
            // settled resize is one crossing, not a window crossing followed by a content one. 552 is the gutter ladder's Compact (16).
            Assert.Equal(WidthTier.Compact, facts.Content);
            shell.RunFrames(60);
            Assert.Equal(facts, Tiers);                                   // and nothing moved after it
        }, 1180f, 760f);
}
