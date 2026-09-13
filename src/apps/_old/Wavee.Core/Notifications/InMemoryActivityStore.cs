using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>The fake-backend activity store: a plain in-memory list (no persistence). Newest entries appended last;
/// <see cref="LoadRecent"/> returns them newest-first.</summary>
public sealed class InMemoryActivityStore : IActivityStore
{
    readonly object _gate = new();
    readonly List<ActivityEntry> _entries = new();   // oldest-first

    public void Append(ActivityEntry entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public void SetStatus(long id, ActivityStatus status)
    {
        lock (_gate)
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Id == id) { _entries[i] = _entries[i] with { Status = status }; return; }
    }

    public void MarkAllRead()
    {
        lock (_gate)
            for (int i = 0; i < _entries.Count; i++)
                if (!_entries[i].Read) _entries[i] = _entries[i] with { Read = true };
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public IReadOnlyList<ActivityEntry> LoadRecent(int limit)
    {
        lock (_gate)
        {
            int n = Math.Min(limit, _entries.Count);
            var list = new List<ActivityEntry>(n);
            for (int i = _entries.Count - 1; i >= 0 && list.Count < n; i--) list.Add(_entries[i]);
            return list;
        }
    }

    public void Prune(int maxCount, long maxAgeMs)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - maxAgeMs;
        lock (_gate)
        {
            _entries.RemoveAll(e => e.TimestampMs < cutoff);
            if (_entries.Count > maxCount) _entries.RemoveRange(0, _entries.Count - maxCount);
        }
    }
}
