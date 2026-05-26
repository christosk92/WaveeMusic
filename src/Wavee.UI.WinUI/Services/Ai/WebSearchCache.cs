using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// Tiny in-process LRU cache shared across AI grounding providers (DuckDuckGo,
/// Wikipedia, configurable JSON). Sliding 24-hour TTL and capacity-bounded so
/// the same artist/track navigation within a session never re-hits the
/// network. Generic payload so the same cache holds web-search lists and
/// Wikipedia summaries — providers tag their entries with a string prefix.
/// </summary>
public sealed class WebSearchCache
{
    private const int DefaultCapacity = 200;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _lru = new();

    public WebSearchCache(int capacity = DefaultCapacity, TimeSpan? ttl = null)
    {
        _capacity = Math.Max(8, capacity);
        _ttl = ttl ?? DefaultTtl;
        _entries = new Dictionary<string, LinkedListNode<Entry>>(_capacity, StringComparer.Ordinal);
    }

    public bool TryGet<T>(string providerTag, string query, out T value) where T : class
    {
        value = default!;
        var key = BuildKey(providerTag, query);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
                return false;

            if (now - node.Value.LastAccess > _ttl)
            {
                _lru.Remove(node);
                _entries.Remove(key);
                return false;
            }

            _lru.Remove(node);
            node.Value = node.Value with { LastAccess = now };
            _lru.AddFirst(node);

            if (node.Value.Payload is T typed)
            {
                value = typed;
                return true;
            }

            return false;
        }
    }

    public void Set(string providerTag, string query, object payload)
    {
        if (payload is null) return;
        var key = BuildKey(providerTag, query);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                existing.Value = new Entry(key, payload, now);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, payload, now));
            _lru.AddFirst(node);
            _entries[key] = node;

            while (_entries.Count > _capacity && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _entries.Remove(tail.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private static string BuildKey(string providerTag, string query)
        => providerTag + "|" + (query ?? string.Empty).Trim().ToLowerInvariant();

    private readonly record struct Entry(string Key, object Payload, DateTime LastAccess);
}
