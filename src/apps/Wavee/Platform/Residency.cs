// ── Platform/Residency.cs — the memory governor + the pressure policy ──────────────────────────────────────────────
//
// 0.2.10 shipped a memory governor and 0.3 deleted it outright with nothing in its place: the in-memory entity graph
// is never trimmed, and `Entities/Store.cs:143` still documents a governor that does not exist. This ports it back,
// almost verbatim (0.2.10's `Backend.Residency.MemoryPressurePolicy` / `MemoryGovernor`), as the generic half — the
// pressure reading and the priority-ordered shedding coordinator. The APP-SPECIFIC half (which arenas exist, and the
// shell's pin source over `Entities/Store.cs`'s `MemoryPins`) is the named partial `Residency.Pins.cs`.
//
// Ported member-for-member except for one shape change: `MemoryGovernor` was an INSTANCE in 0.2.10 (one per
// composition root, held by `Services.Residency`). 0.3 has exactly one composition root and exactly one governor for
// the life of the process — the same reasoning `Entities/Queue.cs` gives for being `static` rather than a handle —
// so this is `static` outright: no `new MemoryGovernor()` anywhere, one registry, armed once from `Shell.Host.cs`.
//
// Rules: single writer (Register/Unregister), any thread (see the threading note on the registry below); Trim/Poll
// run on the UI thread only, because a shed callback (Store.TrimMemory) asserts it (C1). No environment-variable
// switches — the poll is always on, and its output is one log line, not a toggle.

using System;
using System.Diagnostics;

namespace Wavee;

/// <summary>How pressed this process is for memory, coarse enough that an arena's shed callback needs no finer
/// signal than "am I in the set this level sheds".</summary>
public enum MemoryPressure { Normal, Moderate, Critical }

/// <summary>Turns a memory reading into a <see cref="MemoryPressure"/> level. Pure — no clock, no GC API, no
/// globals — so the decision can be unit-tested at every boundary instead of inferred from a running app.
/// <para><b>Why this exists.</b> The obvious reading is <c>GC.GetGCMemoryInfo()</c>'s
/// <c>MemoryLoadBytes / HighMemoryLoadThresholdBytes</c>, which is WHOLE-MACHINE load against the CLR's high-memory
/// threshold (~90% of physical). On a 16-32 GB machine that ratio sits near zero while this process holds a
/// gigabyte, so a governor keyed to it could never leave Normal no matter how much Wavee itself was using — a signal
/// the app cannot move. Pressure is read from the process's OWN footprint instead, which is the thing shedding an
/// arena actually changes.</para>
/// <para>Machine load is still consulted, as a ceiling rather than the primary signal: if the whole machine is under
/// real pressure, this process should shed even when its own footprint looks modest.</para></summary>
public static class MemoryPressurePolicy
{
    /// <summary>Process private bytes at which the cheapest arenas start shedding.</summary>
    public const long ModerateBytes = 600L * 1024 * 1024;

    /// <summary>Process private bytes at which every arena sheds, including the expensive ones.</summary>
    public const long CriticalBytes = 900L * 1024 * 1024;

    /// <summary>Whole-machine load (0..1 of the CLR's high-memory threshold) that forces the matching level
    /// regardless of this process's own size.</summary>
    public const double MachineModerateLoad = 0.85;
    public const double MachineCriticalLoad = 1.0;

    /// <param name="processPrivateBytes">This process's private bytes — what shedding actually reduces.</param>
    /// <param name="machineLoad">Whole-machine memory load as a fraction of the CLR's high-memory threshold, or 0
    /// when it cannot be determined. Only ever RAISES the level.</param>
    public static MemoryPressure From(long processPrivateBytes, double machineLoad)
    {
        if (processPrivateBytes >= CriticalBytes || machineLoad >= MachineCriticalLoad) return MemoryPressure.Critical;
        if (processPrivateBytes >= ModerateBytes || machineLoad >= MachineModerateLoad) return MemoryPressure.Moderate;
        return MemoryPressure.Normal;
    }
}

/// <summary>The cross-arena shedding coordinator (0.2.10's <c>MemoryGovernor</c>, the plan's "memory governor").
/// Arenas register a shed action with a priority; <see cref="Trim"/> sheds them in priority order, escalating with
/// pressure, so a higher level sheds everything a lower one does PLUS more — and pinned working sets survive because
/// they are simply not registered as sheddable (or, for the entity store, because <c>Entities/Store.cs</c>'s
/// <c>MemoryPins</c> says so — see <c>Residency.Pins.cs</c>). The concrete arenas plug in at the app level; the
/// ordering and escalation are the governor's, and unit-tested here with no engine and no store.
///
/// <para><b>STATIC, not an instance</b> (see the file header): there is exactly one governor for the process, armed
/// once from <c>Shell.Host.cs</c>.</para>
///
/// <para><b>THREADING.</b> The registry is COPY-ON-WRITE, and it has to be. <see cref="Register"/> /
/// <see cref="Unregister"/> can run on whatever thread a feature's go-live or teardown is on, while <see cref="Trim"/>
/// runs on the app's periodic UI-thread poll (<see cref="Poll"/>, armed by <c>Shell.Host.cs</c>). A plain
/// <c>List&lt;&gt;</c> mutated by one and enumerated by the other is an <c>InvalidOperationException</c>
/// ("collection was modified") at best and a torn read at worst. Writes take a lock and publish a NEW array; Trim
/// reads the array reference once and walks it, so a shed already in flight completes against a consistent
/// snapshot (an arena that unregistered mid-trim may still be shed one last time, which is harmless — shedding is
/// idempotent by contract).</para></summary>
public static partial class Residency
{
    static readonly object s_gate = new();

    /// <summary>Immutable snapshot, pre-sorted by priority so <see cref="Trim"/> allocates nothing and needs no
    /// OrderBy.</summary>
    static volatile (int Priority, string Name, Func<long> Shed)[] s_arenas = Array.Empty<(int, string, Func<long>)>();

    /// <summary>Register a sheddable arena. <paramref name="priority"/> 1 = cheapest/first (prefetch art), higher =
    /// shed only under greater pressure (3 = the entity store). <paramref name="shed"/> returns the bytes it freed.
    /// <para><b>Idempotent by name.</b> Registering a name that is already present REPLACES its priority and shed
    /// callback rather than adding a second entry — a re-entrant install (idempotent by contract, like every other
    /// one-shot arm in <c>Shell.Host.cs</c>) must not make <see cref="Trim"/> shed the same arena twice.</para></summary>
    public static void Register(int priority, string name, Func<long> shed)
    {
        lock (s_gate)
        {
            var current = s_arenas;
            int existing = Array.FindIndex(current, a => string.Equals(a.Name, name, StringComparison.Ordinal));
            (int Priority, string Name, Func<long> Shed)[] without;
            if (existing >= 0)
            {
                without = new (int, string, Func<long>)[current.Length - 1];
                Array.Copy(current, 0, without, 0, existing);
                Array.Copy(current, existing + 1, without, existing, current.Length - existing - 1);
            }
            else without = current;

            // Insert in priority order, AFTER every equal priority — an ordered insert rather than Array.Sort because
            // Array.Sort is not stable, and equal-priority arenas must keep registration order (what makes a trim's
            // shed sequence reproducible, and what the ordering test below pins).
            int at = without.Length;
            while (at > 0 && without[at - 1].Priority > priority) at--;
            var next = new (int Priority, string Name, Func<long> Shed)[without.Length + 1];
            Array.Copy(without, 0, next, 0, at);
            next[at] = (priority, name, shed);
            Array.Copy(without, at, next, at + 1, without.Length - at);
            s_arenas = next;
        }
    }

    /// <summary>Drop a registered arena by name. A session-scoped arena that registers on go-live MUST unregister on
    /// teardown, or every cycle leaves another closure over a dead source in the list. Returns true if one was
    /// removed.</summary>
    public static bool Unregister(string name)
    {
        lock (s_gate)
        {
            var current = s_arenas;
            for (int i = 0; i < current.Length; i++)
                if (string.Equals(current[i].Name, name, StringComparison.Ordinal))
                {
                    var next = new (int Priority, string Name, Func<long> Shed)[current.Length - 1];
                    Array.Copy(current, 0, next, 0, i);
                    Array.Copy(current, i + 1, next, i, current.Length - i - 1);
                    s_arenas = next;
                    return true;
                }
            return false;
        }
    }

    /// <summary>Shed every arena whose priority is within the pressure level (Normal=1, Moderate=2, Critical=4), in
    /// ascending priority order — so shedding STOPS once the level is satisfied rather than walking arenas the level
    /// does not call for. Returns total bytes freed.</summary>
    public static long Trim(MemoryPressure level)
    {
        int maxPriority = level switch
        {
            MemoryPressure.Normal => 1,
            MemoryPressure.Moderate => 2,
            MemoryPressure.Critical => 4,
            _ => 1,
        };
        var arenas = s_arenas;   // ONE volatile read: the snapshot this trim runs against, whatever else registers meanwhile
        long freed = 0;
        for (int i = 0; i < arenas.Length; i++)
            if (arenas[i].Priority <= maxPriority)
                freed += arenas[i].Shed();
        return freed;
    }

    /// <summary>The governor's periodic tick — what <c>Shell.Host.cs</c>'s 30s poll timer posts to the UI thread.
    /// Reads THIS process's private bytes (what shedding actually reduces) plus the whole-machine load as a ceiling,
    /// turns that into a <see cref="MemoryPressure"/> level, and <see cref="Trim"/>s. Always on (house rule: no
    /// environment-variable switches) — its only externally visible effect at rest (nothing registered, or nothing
    /// over budget) is one log line.</summary>
    public static long Poll()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        double machineLoad = info.HighMemoryLoadThresholdBytes > 0
            ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes
            : 0.0;
        long privateBytes;
        using (var self = Process.GetCurrentProcess()) privateBytes = self.PrivateMemorySize64;
        var level = MemoryPressurePolicy.From(privateBytes, machineLoad);
        long freed = Trim(level);
        Log.Info("memory", $"governor.trim level={level} private={privateBytes}B machineLoad={machineLoad:0.00} freed={freed}B");
        return freed;
    }
}
