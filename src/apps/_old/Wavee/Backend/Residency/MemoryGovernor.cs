using System;


namespace Wavee.Backend.Residency;

public enum MemoryPressure { Normal, Moderate, Critical }

// ── The cross-arena shedding coordinator (the plan's MemoryGovernor) ─────────────────────────────────────────────────
// Arenas register a shed action with a priority; Trim() sheds them in priority order, escalating with OS memory pressure,
// so a higher-pressure level sheds everything a lower one does PLUS more — and pinned working sets (rootlist, set indexes,
// visible-row entities/art) survive because they are simply not registered as sheddable. The concrete arenas plug in at
// the app level (engine ImageCache prefetch-lane drop / EvictToBudget; WARM membership demotion; unpinned entity drop);
// the ordering + escalation are the governor's, and unit-tested here without the GPU.
//
// THREADING: the registry is COPY-ON-WRITE, and it has to be. Register/Unregister run on whatever thread go-live or
// logout is on (the live bootstrap's continuation, the logout pool thread through LiveWiring.Uninstall), while Trim runs
// on the app's periodic UI-thread timer. A plain List<> mutated by one and enumerated by the other is an
// InvalidOperationException ("collection was modified") at best and a torn read at worst — a logout that happened to
// land inside a trim tick would take the poll down. Writes take a lock and publish a NEW array; Trim reads the array
// reference once and walks it, so a shed already in flight completes against a consistent snapshot (an arena that
// unregistered mid-trim may still be shed one last time, which is harmless — shedding is idempotent by contract).
public sealed class MemoryGovernor
{
    readonly object _gate = new();
    /// <summary>Immutable snapshot, pre-sorted by priority so <see cref="Trim"/> allocates nothing and needs no OrderBy.</summary>
    volatile (int Priority, string Name, Func<long> Shed)[] _arenas = Array.Empty<(int, string, Func<long>)>();

    /// <summary>Register a sheddable arena. <paramref name="priority"/> 1 = cheapest/first (prefetch art), higher = shed
    /// only under greater pressure (2 = WARM demotion, 3 = unpinned entities, 4 = emergency clear). <paramref name="shed"/>
    /// returns the bytes it freed.</summary>
    public void Register(int priority, string name, Func<long> shed)
    {
        lock (_gate)
        {
            var current = _arenas;
            // Insert in priority order, AFTER every equal priority — an ordered insert rather than List.Sort because
            // List.Sort is not stable, and equal-priority arenas must keep registration order (that is the contract the
            // ordering test pins, and what makes a trim's shed sequence reproducible).
            int at = current.Length;
            while (at > 0 && current[at - 1].Priority > priority) at--;
            var next = new (int Priority, string Name, Func<long> Shed)[current.Length + 1];
            Array.Copy(current, 0, next, 0, at);
            next[at] = (priority, name, shed);
            Array.Copy(current, at, next, at + 1, current.Length - at);
            _arenas = next;
        }
    }

    /// <summary>Drop a registered arena by name. Session-scoped arenas (the live audio body-disk cache) register on
    /// go-live and MUST unregister on logout — otherwise every login/logout cycle leaves another closure over a dead
    /// cache in the list and Trim() sheds against them forever. Returns true if an arena was removed.</summary>
    public bool Unregister(string name)
    {
        lock (_gate)
        {
            var current = _arenas;
            for (int i = 0; i < current.Length; i++)
                if (string.Equals(current[i].Name, name, StringComparison.Ordinal))
                {
                    var next = new (int Priority, string Name, Func<long> Shed)[current.Length - 1];
                    Array.Copy(current, 0, next, 0, i);
                    Array.Copy(current, i + 1, next, i, current.Length - i - 1);
                    _arenas = next;
                    return true;
                }
            return false;
        }
    }

    /// <summary>Shed every arena whose priority is within the pressure level (Normal=1, Moderate=2, Critical=4), in
    /// ascending priority order. Returns total bytes freed.</summary>
    public long Trim(MemoryPressure level)
    {
        int maxPriority = level switch
        {
            MemoryPressure.Normal => 1,
            MemoryPressure.Moderate => 2,
            MemoryPressure.Critical => 4,
            _ => 1,
        };
        var arenas = _arenas;   // ONE volatile read: the snapshot this trim runs against, whatever else registers meanwhile
        long freed = 0;
        for (int i = 0; i < arenas.Length; i++)
            if (arenas[i].Priority <= maxPriority)
                freed += arenas[i].Shed();
        return freed;
    }
}
