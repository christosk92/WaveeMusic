using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.Core.Video;

/// <summary>
/// In-memory implementation of <see cref="IVideoManifestCache"/>. Plain
/// primitives — AOT-safe, no reflection.
/// </summary>
public sealed class InMemoryVideoManifestCache : IVideoManifestCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private const int DefaultCapacity = 16;

    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, CachedVideoManifest> _entries = new(StringComparer.Ordinal);

    public InMemoryVideoManifestCache(TimeSpan? ttl = null, int? capacity = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _capacity = capacity ?? DefaultCapacity;
    }

    public bool TryGet(string manifestId, out CachedVideoManifest entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(manifestId)) return false;

        if (!_entries.TryGetValue(manifestId, out var cached))
            return false;

        if (DateTimeOffset.UtcNow - cached.StoredAt > _ttl)
        {
            _entries.TryRemove(manifestId, out _);
            return false;
        }

        entry = cached;
        return true;
    }

    public void Store(string manifestId, string rawJson, SpotifyWebEmeVideoManifest parsed)
    {
        if (string.IsNullOrEmpty(manifestId)) return;
        if (parsed is null) return;

        var entry = new CachedVideoManifest(manifestId, rawJson ?? string.Empty, parsed, DateTimeOffset.UtcNow);
        _entries[manifestId] = entry;

        if (_entries.Count <= _capacity) return;

        // Capacity exceeded — evict the oldest entries until we're back under the limit.
        // Cheap because capacity is small (~16). Snapshot under lock to avoid concurrent eviction races.
        lock (_gate)
        {
            if (_entries.Count <= _capacity) return;

            var ordered = _entries.Values.OrderBy(e => e.StoredAt).ToList();
            var toEvict = ordered.Count - _capacity;
            for (var i = 0; i < toEvict; i++)
                _entries.TryRemove(ordered[i].ManifestId, out _);
        }
    }

    public void Invalidate(string manifestId)
    {
        if (string.IsNullOrEmpty(manifestId)) return;
        _entries.TryRemove(manifestId, out _);
    }
}
