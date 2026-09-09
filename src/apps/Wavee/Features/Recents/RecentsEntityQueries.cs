using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>The current history viewport owns identity subscriptions; canonical snapshots remain owned by queries.</summary>
sealed class RecentsEntityQueries(IQueryService queries, CatalogScope scope, Action<Action> post, Action<string> changed,
    TimeProvider? time = null)
    : IDisposable
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, QuerySignalBinding<EntityCardSnapshot>> _handles = new(StringComparer.Ordinal);
    readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    readonly List<string> _removed = [];
    bool _active, _disposed;

    public void SetUris(IReadOnlyList<string> uris)
    {
        if (_disposed) return;
        _wanted.Clear();
        foreach (string uri in uris) _wanted.Add(uri);
        _removed.Clear();
        foreach (var pair in _handles)
            if (!_wanted.Contains(pair.Key)) { pair.Value.Dispose(); _removed.Add(pair.Key); }
        foreach (string uri in _removed) _handles.Remove(uri);
        foreach (string uri in _wanted)
        {
            if (_handles.ContainsKey(uri)) continue;
            var binding = new QuerySignalBinding<EntityCardSnapshot>(queries.Acquire(new EntityCardQuery(scope, uri)),
                post, _ => changed(uri), time: _time);
            _handles.Add(uri, binding);
            binding.SetDemand(new QueryDemand(true, QueryPriority.Visible, []));
            binding.SetActive(_active);
        }
    }

    public EntityCardSnapshot? Read(string uri)
        => _handles.TryGetValue(uri, out var binding) && binding.Snapshot.Peek() is { Status.HasPrimaryData: true } snapshot
            ? snapshot.Value : null;

    public Track? Track(string uri) => Read(uri)?.Playable;

    public void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        foreach (var binding in _handles.Values) binding.SetActive(active);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _handles.Values) binding.Dispose();
        _handles.Clear(); _wanted.Clear(); _removed.Clear();
    }
}
