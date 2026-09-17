// ── Wavee.Tests/SidebarRevisionTests.cs — BUG E2: the sidebar rebuild's revision-bump decision ────────────────────────
//
// docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md §7 (E2): `SidebarProjectionBinder.Rebuild` used to bump
// `_revision` UNCONDITIONALLY, even though the two things that trigger real downstream work — `Entries.Publish`'s
// byte-change shadow and `PublishInput`'s feed-moved shadow — are both carefully content-gated. `Revision` feeds
// `PaneView.PlanDep` (a full re-plan) and `V3Session.ShapeInput`'s `ViewEpoch` (a full re-bucket/re-group of the
// whole library), so an unconditional bump forced both on every rebuild wake regardless of whether anything
// published actually changed. The fix extracts the decision into `SidebarRevisionGate` (Sidebar.Host.cs) — a
// one-line pure function, but per CLAUDE.md's "no source-text tests" rule the decision still gets its own table
// rather than being asserted only indirectly through a live binder.
//
// PURE. No engine, no entities, no binder — this only exercises `SidebarRevisionGate.ShouldBump`.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarRevisionGateTests
{
    [Theory]
    [InlineData(false, false, false)]   // neither publish flipped its signal: no visible or planner-relevant change.
    [InlineData(true, false, true)]     // Entries.Publish flipped (library content changed): must re-plan.
    [InlineData(false, true, true)]     // PublishInput flipped (pins/recency/feed/contributed-section-only change,
                                         // library content unchanged): still a real change the plan must see.
    [InlineData(true, true, true)]      // both flipped (e.g. a scope switch): must re-plan.
    public void ShouldBump_is_an_OR_of_the_two_publish_gates(bool entriesChanged, bool inputChanged, bool expected)
        => Assert.Equal(expected, SidebarRevisionGate.ShouldBump(entriesChanged, inputChanged));

    /// <summary>The trap this fact guards against: a rebuild that reproduced byte-identical content on BOTH gates
    /// (a wake from a table change elsewhere in the app that folds to nothing, G-180) must not bump Revision — that
    /// was exactly the unconditional `_revision++` bug. Named separately from the theory above so a regression here
    /// reads as "the no-op case broke", not just a row in a table.</summary>
    [Fact]
    public void A_rebuild_that_published_nothing_new_does_not_bump()
        => Assert.False(SidebarRevisionGate.ShouldBump(entriesChanged: false, inputChanged: false));
}
