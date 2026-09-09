using System;
using System.Collections;
using System.Collections.Generic;

namespace Wavee.Core.Catalog;

/// <summary>Per-node pass counters for the query publication cost story. All fields are cheap increments taken
/// while a pass runs; nothing here reads the catalog or allocates on its own.</summary>
public readonly record struct QueryPassStats(int EntriesMaterialized, int ChunksCopied, int KeysObserved)
{
    public static QueryPassStats Empty { get; } = default;
}

/// <summary>A chunked, structurally-shared, copy-on-write map from <see cref="ResourceKey"/> to
/// <see cref="ResourceSnapshot"/>. Every key minted anywhere in a map's lineage keeps the SAME stable slot number
/// for the lifetime of the lineage (never reassigned, even after removal), so two generations derived from one
/// another share every chunk their pass did not touch: a lookup costs one dictionary probe (the slot) plus two
/// array indexes — exactly what a flat <see cref="Dictionary{TKey,TValue}"/> already cost — while a <see cref="Builder"/>
/// pass that changes a handful of keys copies only the (256-wide) chunks those keys live in, not the whole map.</summary>
public sealed class ResourceMap : IReadOnlyDictionary<ResourceKey, ResourceSnapshot>
{
    internal const int ChunkSize = 256;

    /// <summary>Shared, append-only key/slot assignment for a whole lineage of maps. Never removes a mapping —
    /// removing a key from a MAP only clears that map's own chunk slot to null; the key keeps its slot forever so
    /// every earlier/later generation can still address it at the same chunk/offset.</summary>
    internal sealed class Lineage
    {
        internal readonly Dictionary<ResourceKey, int> Slots = new();
        internal readonly List<ResourceKey> KeyBySlot = new();

        internal int SlotOf(ResourceKey key)
        {
            if (Slots.TryGetValue(key, out int slot)) return slot;
            slot = KeyBySlot.Count;
            Slots.Add(key, slot);
            KeyBySlot.Add(key);
            return slot;
        }
    }

    internal readonly Lineage _lineage;
    internal readonly ResourceSnapshot?[][] _chunks; // top-level array of 256-wide chunks; chunk == null means untouched (all-empty) in THIS generation
    internal readonly int _slotCount; // upper bound of slots this generation's _chunks can address
    readonly int _count;

    /// <summary>A FRESH empty map with its own private <see cref="Lineage"/> on every access — never a shared
    /// singleton. A lineage is mutated in place as keys mint slots (<see cref="Lineage.SlotOf"/>), so two
    /// unrelated call sites (two query nodes, two dependency scopes, two tests) starting "from empty" must never
    /// share one Lineage instance; sharing it would race Slots/KeyBySlot across threads and cross-contaminate slot
    /// numbering between otherwise-independent maps.</summary>
    public static ResourceMap Empty => new(new Lineage(), [], 0, 0);

    internal ResourceMap(Lineage lineage, ResourceSnapshot?[][] chunks, int slotCount, int count)
    {
        _lineage = lineage; _chunks = chunks; _slotCount = slotCount; _count = count;
    }

    public int Count => _count;

    public Builder ToBuilder() => new(this);

    public bool ContainsKey(ResourceKey key) => TryGetValue(key, out _);

    public bool TryGetValue(ResourceKey key, out ResourceSnapshot value)
    {
        if (_lineage.Slots.TryGetValue(key, out int slot) && slot < _slotCount)
        {
            var chunk = _chunks[slot / ChunkSize];
            if (chunk is not null)
            {
                var found = chunk[slot % ChunkSize];
                if (found is not null) { value = found; return true; }
            }
        }
        value = null!; return false;
    }

    public ResourceSnapshot this[ResourceKey key]
        => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

    public IEnumerable<ResourceKey> Keys
    {
        get { foreach (var pair in this) yield return pair.Key; }
    }

    public IEnumerable<ResourceSnapshot> Values
    {
        get { foreach (var pair in this) yield return pair.Value; }
    }

    public IEnumerator<KeyValuePair<ResourceKey, ResourceSnapshot>> GetEnumerator()
    {
        for (int slot = 0; slot < _slotCount; slot++)
        {
            var chunk = _chunks[slot / ChunkSize];
            if (chunk is null) continue;
            var value = chunk[slot % ChunkSize];
            if (value is null) continue;
            yield return new(_lineage.KeyBySlot[slot], value);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Per-node, reused across passes. <see cref="SetIfDifferent"/> is <c>ReferenceEquals</c>-gated so an
    /// unchanged snapshot never copies a chunk; <see cref="Commit"/> returns the SAME base instance when nothing in
    /// this pass changed, otherwise a new <see cref="ResourceMap"/> that shares every chunk this pass did not
    /// touch.</summary>
    public sealed class Builder : IReadOnlyDictionary<ResourceKey, ResourceSnapshot>
    {
        ResourceMap _base = null!;
        ResourceSnapshot?[][] _chunks = null!;
        int _slotCount;
        int _count;
        readonly HashSet<int> _touchedChunks = new();
        List<(ResourceKey Key, ResourceSnapshot? Old, ResourceSnapshot? New)>? _changes;
        int _materialized;

        internal Builder(ResourceMap map) => Reset(map);

        /// <summary>Rebases this reused builder onto a (possibly different) base map for the next pass, clearing
        /// the change list and touched-chunk set from the previous pass.</summary>
        public void Reset(ResourceMap map)
        {
            _base = map;
            _chunks = map._chunks.Length == 0 ? [] : (ResourceSnapshot?[][])map._chunks.Clone();
            _slotCount = map._slotCount;
            _count = map.Count;
            _touchedChunks.Clear();
            _changes?.Clear();
            _materialized = 0;
        }

        public bool TryGetValue(ResourceKey key, out ResourceSnapshot value)
        {
            if (_base._lineage.Slots.TryGetValue(key, out int slot) && slot < _slotCount)
            {
                var chunk = _chunks[slot / ChunkSize];
                if (chunk is not null)
                {
                    var found = chunk[slot % ChunkSize];
                    if (found is not null) { value = found; return true; }
                }
            }
            value = null!; return false;
        }

        public int Count => _count;
        public bool ContainsKey(ResourceKey key) => TryGetValue(key, out _);
        public ResourceSnapshot this[ResourceKey key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        public IEnumerable<ResourceKey> Keys { get { foreach (var pair in this) yield return pair.Key; } }
        public IEnumerable<ResourceSnapshot> Values { get { foreach (var pair in this) yield return pair.Value; } }

        public IEnumerator<KeyValuePair<ResourceKey, ResourceSnapshot>> GetEnumerator()
        {
            for (int slot = 0; slot < _slotCount; slot++)
            {
                var chunk = _chunks[slot / ChunkSize];
                if (chunk is null) continue;
                var value = chunk[slot % ChunkSize];
                if (value is null) continue;
                yield return new(_base._lineage.KeyBySlot[slot], value);
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Writes <paramref name="value"/> at <paramref name="key"/>'s stable slot, copying the touched
        /// 256-wide chunk exactly once per pass (a no-op when the slot already holds the SAME reference).</summary>
        public void SetIfDifferent(ResourceKey key, ResourceSnapshot value)
        {
            _materialized++;
            int slot = _base._lineage.SlotOf(key);
            EnsureCapacity(slot);
            int chunkIndex = slot / ChunkSize, offset = slot % ChunkSize;
            var chunk = _chunks[chunkIndex];
            var old = chunk?[offset];
            if (ReferenceEquals(old, value)) return;
            if (_touchedChunks.Add(chunkIndex))
            {
                chunk = chunk is null ? new ResourceSnapshot?[ChunkSize] : (ResourceSnapshot?[])chunk.Clone();
                _chunks[chunkIndex] = chunk;
            }
            chunk![offset] = value;
            if (old is null && value is not null) _count++;
            else if (old is not null && value is null) _count--;
            (_changes ??= new()).Add((key, old, value));
        }

        void EnsureCapacity(int slot)
        {
            if (slot < _slotCount) return;
            int neededChunks = slot / ChunkSize + 1;
            if (neededChunks > _chunks.Length)
            {
                var grown = new ResourceSnapshot?[neededChunks][];
                Array.Copy(_chunks, grown, _chunks.Length);
                _chunks = grown;
            }
            _slotCount = slot + 1;
        }

        /// <summary>The reused change list for this pass: (key, previous value or null, new value). Folded by the
        /// caller into per-node aggregates (refreshing/offline/superseded counts, problems) instead of a second
        /// O(n) scan of the committed map.</summary>
        public IReadOnlyList<(ResourceKey Key, ResourceSnapshot? Old, ResourceSnapshot? New)> Changes
            => (IReadOnlyList<(ResourceKey, ResourceSnapshot?, ResourceSnapshot?)>?)_changes ?? [];

        public QueryPassStats Stats => new(_materialized, _touchedChunks.Count, 0);

        /// <summary>Returns the SAME base instance when this pass changed nothing (a status-only republication
        /// that re-observed identical activity), otherwise a new map sharing every untouched chunk.</summary>
        public ResourceMap Commit()
        {
            if (_changes is null || _changes.Count == 0) return _base;
            return new ResourceMap(_base._lineage, _chunks, _slotCount, _count);
        }
    }
}
