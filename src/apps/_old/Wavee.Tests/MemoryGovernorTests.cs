using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Residency;
using Xunit;

namespace Wavee.Tests;

// The cross-arena shedding coordinator: arenas shed in a FIXED priority, escalating with pressure, so pinned working sets
// survive. The concrete arenas (engine ImageCache, Store eviction) plug in at the app level; the ordering is the governor's.
public class MemoryGovernorTests
{
    [Fact]
    public void Trim_ShedsArenas_InPriorityOrder_UpToPressureLevel()
    {
        var order = new List<string>();
        var gov = new MemoryGovernor();
        gov.Register(1, "prefetch-art", () => { order.Add("art"); return 10; });
        gov.Register(3, "drop-entities", () => { order.Add("entities"); return 30; });   // registered out of order
        gov.Register(2, "warm-demote", () => { order.Add("warm"); return 20; });

        long freed = gov.Trim(MemoryPressure.Moderate);                 // priority <= 2
        Assert.Equal(new[] { "art", "warm" }, order);                  // entities NOT shed at Moderate; order is by priority
        Assert.Equal(30, freed);

        order.Clear();
        freed = gov.Trim(MemoryPressure.Critical);                      // all priorities
        Assert.Equal(new[] { "art", "warm", "entities" }, order);
        Assert.Equal(60, freed);
    }

    [Fact]
    public void Trim_Normal_OnlyShedsTheCheapestArena()
    {
        var order = new List<string>();
        var gov = new MemoryGovernor();
        gov.Register(1, "prefetch-art", () => { order.Add("art"); return 5; });
        gov.Register(2, "warm-demote", () => { order.Add("warm"); return 5; });

        gov.Trim(MemoryPressure.Normal);                               // routine self-trim — priority 1 only
        Assert.Equal(new[] { "art" }, order);
    }

    // The registry is written by GO-LIVE and LOGOUT (the live audio body-disk arena registers on one and unregisters on
    // the other, from whatever thread the bootstrap / LiveWiring.Uninstall is on) and READ by the app's periodic trim
    // timer. A plain List<> mutated under an in-flight foreach throws "collection was modified" and takes the poll down
    // with it. Copy-on-write makes that impossible; this is the gate.
    [Fact]
    public async Task RegisterUnregisterAndTrim_AreSafeConcurrently()
    {
        var gov = new MemoryGovernor();
        gov.Register(1, "pinned", static () => 1);     // one permanent arena so Trim always has work to walk
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;

        void Run(Action body) { try { while (!stop.IsCancellationRequested) body(); } catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); } }

        var churn = Task.Run(() => Run(() =>
        {
            for (int i = 0; i < 32; i++) gov.Register(i % 4 + 1, "session-" + i, static () => 2);
            for (int i = 0; i < 32; i++) gov.Unregister("session-" + i);
        }));
        var trims = Task.Run(() => Run(() => gov.Trim(MemoryPressure.Critical)));

        await Task.WhenAll(churn, trims);

        Assert.Null(failure);
        // …and the registry is intact afterwards: only the permanent arena, sheddable exactly once.
        Assert.Equal(1, gov.Trim(MemoryPressure.Critical));
    }

    // ── MemoryPressurePolicy: the reading that decides which of the arenas above actually shed ──────────────────────
    // The level used to come from whole-machine memory load against the CLR's high-memory threshold, which on a
    // 16-32 GB laptop sits near zero while this process holds a gigabyte — so the governor never left Normal however
    // much Wavee itself was using. Pressure is read from the PROCESS footprint now, the thing shedding actually
    // changes, with machine load kept only as a ceiling.

    [Fact]
    public void Pressure_IsDrivenByThisProcessFootprint_NotWholeMachineLoad()
    {
        // A quiet machine (load 0) and a fat process: the old reading said Normal forever; this one escalates.
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

    [Fact]
    public void Normal_ShedsPriorityOne_WhichIsTheLevelProductionLeftEmpty()
    {
        // The governor maps Normal to maxPriority 1. With only priorities 2 and 3 registered — which is what shipped —
        // every Normal tick walked the array and shed nothing, forever. This is that bug as a test.
        var withoutPriorityOne = new MemoryGovernor();
        withoutPriorityOne.Register(2, "detail-cache", static () => 20);
        withoutPriorityOne.Register(3, "entity-store", static () => 30);
        Assert.Equal(0, withoutPriorityOne.Trim(MemoryPressure.Normal));

        var withPriorityOne = new MemoryGovernor();
        withPriorityOne.Register(1, "prefetch-art", static () => 10);
        withPriorityOne.Register(2, "detail-cache", static () => 20);
        Assert.Equal(10, withPriorityOne.Trim(MemoryPressure.Normal));
    }
}
