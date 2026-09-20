// ── Wavee.Tests/MemoryGovernorTests.cs — the memory governor's ordering + the pressure policy's boundaries ─────────
//
// `Platform/Residency.cs` is 0.2.10's `MemoryGovernor` / `MemoryPressurePolicy` ported back (0.3 deleted them
// outright; `Entities/Store.cs:143` still documents a governor that did not exist until this). Both halves are pure
// enough to test with no engine and no store, per the house rule (no source-text tests): the ordering/escalation
// arithmetic never touches an arena's real implementation, and the pressure boundary never touches a real GC reading.
//
// `Residency` is STATIC (see its file header: one governor for the process, not a handle 0.2.10 could `new` up per
// test). Isolation here is therefore by NAME, not by instance: every test registers arena names unique to itself and
// unregisters them in `Dispose`, so a run never leaks an arena into the next test — the same shape `StoreTests`
// gives its `Store.Pins` teardown. No other test file registers anything with `Residency` (a `git grep` for
// `Residency.` outside this file turns up only `Platform/Residency*.cs` and `Shell/Shell.Host.cs`'s one-time
// `Residency.Install()`, which no test path calls), so the registry is empty at the start of every test here.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Wavee.Tests;

public class MemoryGovernorTests : IDisposable
{
    readonly List<string> _registered = new();

    /// <summary>Register through the fixture so <see cref="Dispose"/> can unregister it — the per-test isolation the
    /// static registry needs in place of 0.2.10's <c>new MemoryGovernor()</c>.</summary>
    void Reg(int priority, string name, Func<long> shed)
    {
        Residency.Register(priority, name, shed);
        _registered.Add(name);
    }

    public void Dispose()
    {
        foreach (string name in _registered) Residency.Unregister(name);
    }

    // ── Residency: ordering, escalation, idempotency ────────────────────────────────────────────────────────────────

    [Fact]
    public void Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel()
    {
        var order = new List<string>();
        Reg(1, nameof(Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel) + ".art", () => { order.Add("art"); return 10; });
        Reg(3, nameof(Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel) + ".entities", () => { order.Add("entities"); return 30; });   // registered out of order
        Reg(2, nameof(Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel) + ".warm", () => { order.Add("warm"); return 20; });

        long freed = Residency.Trim(MemoryPressure.Moderate);                 // priority <= 2
        Assert.Equal(new[] { "art", "warm" }, order);                        // entities NOT shed at Moderate; order is by priority
        Assert.Equal(30, freed);

        order.Clear();
        freed = Residency.Trim(MemoryPressure.Critical);                      // all priorities
        Assert.Equal(new[] { "art", "warm", "entities" }, order);
        Assert.Equal(60, freed);
    }

    [Fact]
    public void Trim_Normal_OnlyShedsTheCheapestArena()
    {
        var order = new List<string>();
        Reg(1, nameof(Trim_Normal_OnlyShedsTheCheapestArena) + ".art", () => { order.Add("art"); return 5; });
        Reg(2, nameof(Trim_Normal_OnlyShedsTheCheapestArena) + ".warm", () => { order.Add("warm"); return 5; });

        Residency.Trim(MemoryPressure.Normal);                                // routine self-trim — priority 1 only
        Assert.Equal(new[] { "art" }, order);
    }

    [Fact]
    public void Normal_ShedsOnlyPriorityOne_WhichIsWhatProductionRegistersThere()
    {
        // Production (Residency.Install, Platform/Residency.Pins.cs) registers exactly ONE priority-1 arena
        // ("prefetch-art") and one priority-3 arena ("entity-store") — 0.2.10's priority-2 tier ("detail-cache") has
        // no 0.3 replacement. A Normal trim over only the priority-3 arena sheds nothing; adding the priority-1 one
        // is what makes the routine (Normal) tick shed anything at all.
        var withoutPriorityOne = nameof(Normal_ShedsOnlyPriorityOne_WhichIsWhatProductionRegistersThere);
        Reg(3, withoutPriorityOne + ".entity-store", static () => 30);
        Assert.Equal(0, Residency.Trim(MemoryPressure.Normal));

        Reg(1, withoutPriorityOne + ".prefetch-art", static () => 10);
        Assert.Equal(10, Residency.Trim(MemoryPressure.Normal));
    }

    [Fact]
    public void Register_IsIdempotentByName_ReplacingRatherThanDuplicating()
    {
        string name = nameof(Register_IsIdempotentByName_ReplacingRatherThanDuplicating);
        int calls = 0;
        Reg(1, name, () => { calls++; return 1; });
        // Re-registering the SAME name must REPLACE the entry, not add a second one — otherwise a re-entrant install
        // (Shell.Host.cs's InstallMarshallers is itself idempotent, but a caller could still register twice) would
        // shed the same arena twice per trim and double-count its freed bytes.
        Residency.Register(1, name, () => { calls++; return 1; });

        long freed = Residency.Trim(MemoryPressure.Critical);
        Assert.Equal(1, calls);
        Assert.Equal(1, freed);
    }

    [Fact]
    public void Register_ReplacingByName_AlsoAdoptsTheNewPriority()
    {
        // The replacement is a full replace, not just the shed callback: re-registering a name at a HIGHER priority
        // must stop it shedding at a level the old priority would have.
        string name = nameof(Register_ReplacingByName_AlsoAdoptsTheNewPriority);
        Reg(1, name, static () => 1);
        Residency.Register(3, name, static () => 1);

        Assert.Equal(0, Residency.Trim(MemoryPressure.Moderate));   // priority 3 does not shed at Moderate anymore
        Assert.Equal(1, Residency.Trim(MemoryPressure.Critical));
    }

    [Fact]
    public void Unregister_RemovesTheArena_AndReturnsWhetherOneWasThere()
    {
        string name = nameof(Unregister_RemovesTheArena_AndReturnsWhetherOneWasThere);
        Residency.Register(1, name, static () => 1);
        Assert.True(Residency.Unregister(name));
        Assert.False(Residency.Unregister(name));                    // already gone — no double-report
        Assert.Equal(0, Residency.Trim(MemoryPressure.Critical));
    }

    // The registry is written by whatever thread a feature's go-live/teardown runs on and READ by the app's periodic
    // trim poll (Shell.Host.cs's `ArmMemoryGovernor`). A plain `List<>` mutated under an in-flight foreach throws
    // "collection was modified" and takes the poll down with it. Copy-on-write makes that impossible; this is the gate.
    [Fact]
    public async Task RegisterUnregisterAndTrim_AreSafeConcurrently()
    {
        string prefix = nameof(RegisterUnregisterAndTrim_AreSafeConcurrently);
        string pinned = prefix + ".pinned";
        Reg(1, pinned, static () => 1);     // one permanent arena so Trim always has work to walk
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;

        void Run(Action body) { try { while (!stop.IsCancellationRequested) body(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); } }

        var churn = Task.Run(() => Run(() =>
        {
            for (int i = 0; i < 32; i++) Residency.Register(i % 4 + 1, prefix + ".session-" + i, static () => 2);
            for (int i = 0; i < 32; i++) Residency.Unregister(prefix + ".session-" + i);
        }));
        var trims = Task.Run(() => Run(() => Residency.Trim(MemoryPressure.Critical)));

        await Task.WhenAll(churn, trims);

        Assert.Null(failure);
        // …and the registry is intact afterwards: only the permanent arena, sheddable exactly once.
        Assert.Equal(1, Residency.Trim(MemoryPressure.Critical));
    }

    // ── MemoryPressurePolicy: the reading that decides which of the arenas above actually shed ──────────────────────
    // The obvious reading is whole-machine memory load against the CLR's high-memory threshold, which on a 16-32 GB
    // machine sits near zero while this process holds a gigabyte — so a governor keyed to it would never leave
    // Normal however much Wavee itself was using. Pressure is read from the PROCESS footprint instead, the thing
    // shedding actually changes, with machine load kept only as a ceiling.

    [Fact]
    public void Pressure_IsDrivenByThisProcessFootprint_NotWholeMachineLoad()
    {
        // A quiet machine (load 0) and a fat process: the whole-machine reading would say Normal forever; this one
        // escalates off the process's own size.
        Assert.Equal(MemoryPressure.Normal, MemoryPressurePolicy.From(300L * 1024 * 1024, machineLoad: 0.0));
        Assert.Equal(MemoryPressure.Moderate, MemoryPressurePolicy.From(MemoryPressurePolicy.ModerateBytes, 0.0));
        Assert.Equal(MemoryPressure.Critical, MemoryPressurePolicy.From(MemoryPressurePolicy.CriticalBytes, 0.0));
    }

    [Fact]
    public void Pressure_MachineLoadOnlyEverRaisesTheLevel()
    {
        // A small process on a machine under real pressure still sheds…
        Assert.Equal(MemoryPressure.Moderate, MemoryPressurePolicy.From(10L * 1024 * 1024, MemoryPressurePolicy.MachineModerateLoad));
        Assert.Equal(MemoryPressure.Critical, MemoryPressurePolicy.From(10L * 1024 * 1024, MemoryPressurePolicy.MachineCriticalLoad));
        // …and a quiet machine never LOWERS a level the process footprint already earned.
        Assert.Equal(MemoryPressure.Critical, MemoryPressurePolicy.From(MemoryPressurePolicy.CriticalBytes, 0.0));
    }

    [Fact]
    public void Pressure_BoundariesAreInclusive_AndOneByteBelowIsNot()
    {
        Assert.Equal(MemoryPressure.Normal, MemoryPressurePolicy.From(MemoryPressurePolicy.ModerateBytes - 1, 0.0));
        Assert.Equal(MemoryPressure.Moderate, MemoryPressurePolicy.From(MemoryPressurePolicy.CriticalBytes - 1, 0.0));
    }
}
