using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Wavee.Backend.Wiring;
using Xunit;

namespace Wavee.Tests.Wiring;

// ── The go-live install ledger (hydration-facade-design.md §2.6) ─────────────────────────────────────────────────────
// LiveWiring exists so an install and its undo cannot drift apart. These pin the four properties that makes true:
// reverse-order teardown, an idempotent Uninstall, one failing inverse not stranding the rest, and AssertCovers NAMING
// what went missing. The offline-factory-laziness test is the one that catches the subtle regression: capturing the
// offline value at install time would freeze a stale stand-in for the whole session.
public class LiveWiringTests
{
    static LiveWiring New(out CapturingWaveeLog log)
    {
        log = new CapturingWaveeLog();
        return new LiveWiring(new WaveeLogger(log, "wiring"));
    }

    [Fact]
    public void Set_InstallsImmediately_AndRecordsTheName()
    {
        var w = New(out _);
        bool installed = false;
        w.Set("A", () => installed = true, () => installed = false);

        Assert.True(installed);
        Assert.Equal(new[] { "A" }, w.Installed);
    }

    [Fact]
    public void Uninstall_RunsInverses_InReverseInstallOrder()
    {
        var w = New(out _);
        var order = new List<string>();
        w.Set("A", () => { }, () => order.Add("A"));
        w.Set("B", () => { }, () => order.Add("B"));
        w.Set("C", () => { }, () => order.Add("C"));

        w.Uninstall();

        // A seam built ON an earlier one must come down first — that is the whole reason the ledger is ordered.
        Assert.Equal(new[] { "C", "B", "A" }, order);
    }

    [Fact]
    public void Uninstall_IsIdempotent_SoGoOfflineAndDisposeAsyncCanBothCallIt()
    {
        var w = New(out _);
        int downs = 0;
        w.Set("A", () => { }, () => downs++);

        w.Uninstall();
        w.Uninstall();
        w.Uninstall();

        Assert.Equal(1, downs);
        Assert.Empty(w.Installed);
    }

    [Fact]
    public void Uninstall_IsolatesAThrowingInverse_LogsIt_AndStillRunsTheRest()
    {
        var w = New(out var log);
        var order = new List<string>();
        w.Set("A", () => { }, () => order.Add("A"));
        w.Set("Bad", () => { }, () => throw new InvalidOperationException("boom"));
        w.Set("C", () => { }, () => order.Add("C"));

        w.Uninstall();   // must NOT throw: a logout that half-fails still has to reach the offline state

        Assert.Equal(new[] { "C", "A" }, order);
        var err = Assert.Single(log.Entries, e => e.Level == WaveeLogLevel.Error);
        Assert.Contains("Bad", err.Message);
    }

    [Fact]
    public void Swap_PointsTheSeamAtTheLiveInner_AndTheInverseAtAFreshOfflineOne()
    {
        var w = New(out _);
        string current = "offline-0";
        int offlineBuilds = 0;
        string MakeOffline() { offlineBuilds++; return "offline-" + offlineBuilds; }

        w.Swap<string>("Seam", v => current = v, "live", MakeOffline);

        Assert.Equal("live", current);
        // THE point of a factory: the offline stand-in is NOT built at install time (a stale instance would then be held
        // for the whole live session), only when the teardown actually needs one.
        Assert.Equal(0, offlineBuilds);

        w.Uninstall();

        Assert.Equal(1, offlineBuilds);
        Assert.Equal("offline-1", current);
    }

    [Fact]
    public void Set_RejectsADuplicateName()
    {
        var w = New(out _);
        w.Set("A", () => { }, () => { });

        // Rejected rather than replace-with-teardown: two installs of one seam in a single go-live means two owners
        // racing it, and silently keeping the last would also silently move it in the reverse-order teardown.
        var ex = Assert.Throws<InvalidOperationException>(() => w.Set("A", () => { }, () => { }));
        Assert.Contains("A", ex.Message);
        Assert.Equal(new[] { "A" }, w.Installed);
    }

    [Fact]
    public void Set_RecordsTheInverse_EvenWhenTheInstallItselfThrows()
    {
        var w = New(out _);
        bool undone = false;

        Assert.Throws<NotSupportedException>(() =>
            w.Set("Partial", () => throw new NotSupportedException(), () => undone = true));

        // The half-built seam still has to be undoable — otherwise a bootstrap that fails mid-install leaks it.
        w.Uninstall();
        Assert.True(undone);
    }

    [Fact]
    public void AssertCovers_PassesWhenEveryRequiredSeamWasInstalled()
    {
        var w = New(out _);
        w.Set("A", () => { }, () => { });
        w.Set("B", () => { }, () => { });

        w.AssertCovers(new[] { "A", "B" });   // no throw
    }

    [Fact]
    public void AssertCovers_ThrowsNamingEveryMissingSeam()
    {
        var w = New(out _);
        w.Set("A", () => { }, () => { });

        var ex = Assert.Throws<InvalidOperationException>(() => w.AssertCovers(new[] { "A", "B", "C" }));

        Assert.Contains("B", ex.Message);
        Assert.Contains("C", ex.Message);
        Assert.DoesNotContain(" A,", ex.Message);   // the one that IS covered is not named
    }

    [Fact]
    public void AssertCovers_FailsAfterUninstall_BecauseTheLedgerIsEmptyAgain()
    {
        var w = New(out _);
        w.Set("A", () => { }, () => { });
        w.Uninstall();

        Assert.Throws<InvalidOperationException>(() => w.AssertCovers(new[] { "A" }));
    }

    [Fact]
    public void Set_RejectsNullsAndBlankNames()
    {
        var w = New(out _);
        Assert.Throws<ArgumentException>(() => w.Set("", () => { }, () => { }));
        Assert.Throws<ArgumentNullException>(() => w.Set("A", null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => w.Set("A", () => { }, null!));
    }

    /// <summary>A `Set` that lands AFTER `Uninstall` must not go live. The go-live block keeps installing right up to
    /// its `AssertCovers`, while `svc.GoLive` makes the logout menu reachable long before that — so a logout (or a
    /// failed bootstrap's teardown) can genuinely overtake a still-running install. Recording it into a ledger that has
    /// already been replayed left the seam LIVE after logout with nothing left to undo it, which is the one failure this
    /// whole type exists to make impossible.</summary>
    [Fact]
    public void Set_AfterUninstall_LandsOnTheOfflineValue_AndIsNotRecorded()
    {
        var w = New(out var log);
        int installed = 0, uninstalled = 0;
        w.Uninstall();

        w.Set("Late", () => installed++, () => uninstalled++);

        Assert.Equal(0, installed);                 // never went live…
        Assert.Equal(1, uninstalled);               // …and was put straight onto its offline value
        Assert.Empty(w.Installed);                  // nothing recorded — there is nothing left to undo
        Assert.Throws<InvalidOperationException>(() => w.AssertCovers(new[] { "Late" }));

        // And a spent ledger stays spent: a second late arrival of the SAME name is not a duplicate-owner bug.
        w.Set("Late", () => installed++, () => uninstalled++);
        Assert.Equal(0, installed);
        Assert.Equal(2, uninstalled);

        // Replaying the teardown afterwards is still a no-op (nothing was recorded to replay).
        w.Uninstall();
        Assert.Equal(2, uninstalled);
    }
}
