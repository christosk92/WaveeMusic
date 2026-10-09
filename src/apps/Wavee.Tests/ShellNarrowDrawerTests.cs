// ── Wavee.Tests/ShellNarrowDrawerTests.cs — BUG E1: the narrow drawer's mount decision ─────────────────────────────────
//
// docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md §7 (E1): `Shell.UI.cs`'s `NarrowDrawer` used to mount its
// heavy subtree (a scrim plus a SECOND full `Sidebar.DrawerPane()` — a whole extra `PaneView` planning the sidebar
// on every invalidation, and a second `PumpBinder` registration) UNCONDITIONALLY, even on a desktop-width window
// where the drawer can never be shown. The fix extracts the mount decision into `Shell.NarrowDrawerMount.ShouldMount`
// and gates `NarrowDrawer.Render` on it.
//
// The one trap worth a named fact of its own: the decision must NOT also require `overlayOpen`. A version that only
// mounted while open would unmount the pane the instant it closes on a narrow window, so the NEXT open would have
// nothing already-mounted for the slide transition (`DrawerPane`'s `UseTransition`) to animate from — it would just
// pop in instead of sliding. Mounting is the overlay band alone; `overlayOpen` only ever affects hit-testing and the slide
// target once the pane exists.
//
// PURE. No engine, no components, no signals — this only exercises `Shell.NarrowDrawerMount.ShouldMount`.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class NarrowDrawerMountTests
{
    [Theory]
    [InlineData(false, false, false)]   // desktop, closed: THE WIN — nothing mounts, no second PaneView/PumpBinder.
    [InlineData(false, true, false)]    // desktop with a stale/leftover overlay open: still nothing mounts — the
                                         // band effect forces OverlayOpen false the instant the band returns to Wide,
                                         // but the mount decision does not rely on that ordering.
    [InlineData(true, false, true)]     // narrow, closed: MUST stay mounted, or the next open has nothing to
                                         // animate the reveal slide from.
    [InlineData(true, true, true)]      // narrow, open: mounted.
    public void ShouldMount_is_gated_on_the_overlay_band_alone(bool hasOverlay, bool overlayOpen, bool expected)
        => Assert.Equal(expected, Shell.NarrowDrawerMount.ShouldMount(hasOverlay, overlayOpen));

    /// <summary>Named separately: proves `overlayOpen` has NO effect on the decision in either direction — the
    /// regression this guards against is someone "optimizing" the gate to `hasOverlay && overlayOpen`, which reads
    /// as a further perf win but breaks the closed-narrow reveal-animation case (see file header).</summary>
    [Fact]
    public void OverlayOpen_never_changes_the_decision_while_narrow()
    {
        Assert.Equal(
            Shell.NarrowDrawerMount.ShouldMount(hasOverlay: true, overlayOpen: false),
            Shell.NarrowDrawerMount.ShouldMount(hasOverlay: true, overlayOpen: true));
    }

    [Fact]
    public void OverlayOpen_never_changes_the_decision_on_desktop()
    {
        Assert.Equal(
            Shell.NarrowDrawerMount.ShouldMount(hasOverlay: false, overlayOpen: false),
            Shell.NarrowDrawerMount.ShouldMount(hasOverlay: false, overlayOpen: true));
    }

    // The two pure guards the drawer uses. Esc deferral is the drawer's `!Sidebar.Editing.Peek()` check, pinned here
    // by the rule rather than by reading source.
    [Fact]
    public void Edit_mode_pins_the_overlay_and_a_leaf_invoke_does_not_close_it()
    {
        Assert.False(SidebarPaneModeRules.LeafInvokeClosesOverlay(editing: true));
        Assert.True(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Narrow, editing: true));
    }
}
