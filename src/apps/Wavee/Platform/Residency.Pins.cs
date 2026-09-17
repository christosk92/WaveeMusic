// ── Platform/Residency.Pins.cs — the concrete arenas + the shell's MemoryPins ───────────────────────────────────────
//
// The generic half (the governor, the pressure policy) is `Residency.cs`. This is the app-specific half: WHICH
// arenas exist, and WHO is reachable right now — the one genuinely new piece, because 0.3's entity graph (unlike
// 0.2.10's `LibraryStore`/`CachedStore`) has no reverse index from a slot to the pages pointing at it yet
// (`Entities/Store.cs`'s `MemoryPins` doc block explains why the trim is opt-in until a pin source exists).
//
// Two arenas, matching the plan:
//   Priority 1 "prefetch-art"  — the engine's image cache, trimmed to its own budget (ports verbatim from 0.2.10).
//   Priority 3 "entity-store"  — `Store.TrimMemory(Entities.Current)` over `ShellPins` below. 0.2.10's priority 2
//                                 ("detail-cache", `LibraryStore.ShedDetails`) has no 0.3 replacement: the type it
//                                 shed no longer exists, and the plan folds what is left into one line at priority 3.
//
// `ShellPins` answers "is anything live pointing at this row" by rebuilding a snapshot of every slot the shell can
// currently reach, once per trim tick (not once per candidate row — `IsPinned` is a pure O(1) lookup against that
// snapshot, called once per live slot in the table by `Store.TrimMemory`). It pins:
//   - the now-playing row (`Playback.State.Current`) and its identity, context and next-up row;
//   - every row in the playback queue (`Entities.Current.Edges.Queue`, the session parent `Queue.Session`);
//   - the nav stack's route subjects — current, back and forward (`Shell.CollectNavSubjects`, Shell.Host.cs);
//   - the sidebar's pinned rows (`Sidebar.Pins`).
// A row not in any of those has nothing live pointing at it and is exactly what the entity-store arena exists to
// shed under CRITICAL pressure.
//
// UI THREAD ONLY: `Refresh` calls `EntityId.Parse` (interns, C1) and reads UI-thread-affine state (`Playback.Snap`,
// `Entities.Current`, `Shell`'s nav stack, `Sidebar.Pins`); `Store.TrimMemory` already asserts the UI thread's scope
// via `ReferenceEquals(scope, Entities.Current)`, and the governor's poll is posted there for exactly this reason
// (Shell.Host.cs's `ArmMemoryGovernor`). Never call this from a background thread.

using System;
using System.Collections.Generic;
using System.Threading;

using FluentGpu;

namespace Wavee;

/// <summary>The shell's <see cref="MemoryPins"/>: a snapshot of every (kind, slot) something live is currently
/// pointing at, rebuilt by <see cref="Refresh"/> right before a trim and consulted by <see cref="IsPinned"/> — once
/// per candidate row, so the snapshot (not the individual lookups) is where any allocation happens.</summary>
sealed class ShellPins : MemoryPins
{
    readonly HashSet<(EntityKind Kind, int Slot)> _pinned = new();
    readonly List<EntityId> _navBuf = new(16);

    /// <summary>Rebuild the snapshot against the CURRENT scope. Called once per entity-store trim tick
    /// (<see cref="Residency.Install"/>'s priority-3 registration), never per candidate row.</summary>
    public void Refresh()
    {
        _pinned.Clear();

        // 1. the now-playing row, its identity, its context and the prefetched next-up row.
        Playback.State state = Playback.Snap();
        if (!state.Current.IsNone) _pinned.Add((state.Current.Kind, state.Current.Slot));
        AddId(state.CurrentId);
        AddId(state.Context);
        AddId(state.NextId);

        // 2. every row the playback queue is holding (history + now-playing + user queue + next-up).
        ReadOnlySpan<int> queue = Entities.Current.Edges.Queue.Targets(Queue.Session);
        for (int i = 0; i < queue.Length; i++)
        {
            EntityRef row = Queue.Unpack(queue[i]);
            if (!row.IsNone) _pinned.Add((row.Kind, row.Slot));
        }

        // 3. the nav stack's route subjects — current, back and forward.
        _navBuf.Clear();
        Shell.CollectNavSubjects(_navBuf);
        for (int i = 0; i < _navBuf.Count; i++) AddId(_navBuf[i]);

        // 4. the sidebar's pinned rows.
        var pins = Sidebar.Pins;
        for (int i = 0; i < pins.Count; i++)
        {
            string uri = pins[i].Uri;
            if (!string.IsNullOrEmpty(uri)) AddId(EntityId.Parse(uri.AsSpan()));
        }
    }

    /// <inheritdoc/>
    public override bool IsPinned(EntityKind kind, int slot) => _pinned.Contains((kind, slot));

    /// <summary>Resolve an identity to its live slot (if any is allocated) and add it — never allocates a row: an id
    /// nobody has fetched yet has nothing to pin.</summary>
    void AddId(EntityId id)
    {
        if (id.IsEmpty) return;
        if (Entities.TableFor(id.Kind) is { } table && table.TryGetSlot(id, out int slot)) _pinned.Add((id.Kind, slot));
    }
}

public static partial class Residency
{
    static readonly ShellPins s_pins = new();
    static int s_installed;

    /// <summary>Wire the two concrete arenas and the shell's pin source. Idempotent (the install latch below), so
    /// <c>Shell.Host.cs</c>'s <c>InstallMarshallers</c> can call it unconditionally the same way it installs every
    /// other cross-thread seam. Safe to call before boot: both shed callbacks no-op until there is something to
    /// shed (no engine host yet, or <see cref="Entities.Current"/> still unset).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0) return;

        Store.Pins = s_pins;

        // Priority 1 — the cheapest thing to lose: unpinned (off-screen) prefetched cover art. Trimming the engine's
        // image cache to its own budget evicts exactly those; a pinned (on-screen) tile is the cache's own concern,
        // never this governor's (`ImageCache.EvictToBudget`'s LRU already protects it).
        Register(1, "prefetch-art", () =>
        {
            if (FluentApp.EngineImages is not { } images) return 0L;
            long before = images.UsedBytes + images.DerivedUsedBytes;
            images.TrimToBudget();
            return Math.Max(0L, before - (images.UsedBytes + images.DerivedUsedBytes));
        });

        // Priority 3 — the entity store's unpinned rows, shed only under CRITICAL pressure. The pin snapshot is
        // rebuilt fresh on every tick (not cached across ticks): now-playing, the queue and the nav stack all move
        // between 30s polls, and a stale snapshot could shed a row a page is bound to right now.
        Register(3, "entity-store", () =>
        {
            if (Entities.Current is not { } scope) return 0L;
            s_pins.Refresh();
            return Store.TrimMemory(scope) * (long)Store.MemoryBytesPerRow;
        });
    }
}
