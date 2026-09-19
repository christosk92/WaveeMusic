// ── Wavee.Tests/ShellNarrowDrawerTests.cs — BUG E1: the narrow drawer's mount decision ─────────────────────────────────
//
// docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md §7 (E1): `Shell.UI.cs`'s `NarrowDrawer` used to mount its
// heavy subtree (a scrim plus a SECOND full `Sidebar.DrawerPane()` — a whole extra `PaneView` planning the sidebar
// on every invalidation, and a second `PumpBinder` registration) UNCONDITIONALLY, even on a desktop-width window
// where the drawer can never be shown. The fix extracts the mount decision into `Shell.NarrowDrawerMount.ShouldMount`
// and gates `NarrowDrawer.Render` on it.
//
// The one trap worth a named fact of its own: the decision must NOT also require `drawerOpen`. A version that only
// mounted while open would unmount the pane the instant it closes on a narrow window, so the NEXT open would have
// nothing already-mounted for the slide transition (`DrawerPane`'s `UseTransition`) to animate from — it would just
// pop in instead of sliding. Mounting is narrowness alone; `drawerOpen` only ever affects hit-testing and the slide
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
    [InlineData(false, true, false)]    // desktop with a stale/leftover DrawerOpen=true: still nothing mounts — the
                                         // shell's own narrow-breakpoint effect forces DrawerOpen false the instant
                                         // NarrowShell flips, but the mount decision does not rely on that ordering.
    [InlineData(true, false, true)]     // narrow, closed: MUST stay mounted, or the next open has nothing to
                                         // animate the reveal slide from.
    [InlineData(true, true, true)]      // narrow, open: mounted.
    public void ShouldMount_is_gated_on_narrowness_alone(bool narrowShell, bool drawerOpen, bool expected)
        => Assert.Equal(expected, Shell.NarrowDrawerMount.ShouldMount(narrowShell, drawerOpen));

    /// <summary>Named separately: proves `drawerOpen` has NO effect on the decision in either direction — the
    /// regression this guards against is someone "optimizing" the gate to `narrowShell && drawerOpen`, which reads
    /// as a further perf win but breaks the closed-narrow reveal-animation case (see file header).</summary>
    [Fact]
    public void DrawerOpen_never_changes_the_decision_while_narrow()
    {
        Assert.Equal(
            Shell.NarrowDrawerMount.ShouldMount(narrowShell: true, drawerOpen: false),
            Shell.NarrowDrawerMount.ShouldMount(narrowShell: true, drawerOpen: true));
    }

    [Fact]
    public void DrawerOpen_never_changes_the_decision_on_desktop()
    {
        Assert.Equal(
            Shell.NarrowDrawerMount.ShouldMount(narrowShell: false, drawerOpen: false),
            Shell.NarrowDrawerMount.ShouldMount(narrowShell: false, drawerOpen: true));
    }
}
