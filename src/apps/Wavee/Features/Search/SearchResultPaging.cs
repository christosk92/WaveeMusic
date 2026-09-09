using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Signals;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>Server-offset subscriptions for unbounded search results. Each handle is one SearchQuery page
/// (offset + limit on the spec); <see cref="SetRange"/> pages those server offsets — never a catalog demand window.</summary>
sealed class SearchResultPaging<TPage, TItem> : IDisposable where TItem : class
{
    readonly IQueryService _queries;
    readonly Func<int, int, QuerySpec<TPage>> _specification;
    readonly Func<TPage, IReadOnlyList<TItem>> _items;
    readonly Func<TPage, int> _total;
    readonly Action<Action> _post;
    readonly int _pageSize;
    readonly Dictionary<int, QuerySignalBinding<TPage>> _pages = [];
    readonly Dictionary<int, (QuerySnapshot<TPage> Snapshot, IReadOnlyList<TItem> Items)> _projected = [];
    readonly Signal<int> _changed = new(0);
    readonly IReadOnlyList<TItem> _preview;
    readonly int _previewTotal;
    bool _active, _disposed, _rangeApplied;
    int _first, _last;
    readonly TimeProvider _time;

    public SearchResultPaging(IQueryService queries, Func<int, int, QuerySpec<TPage>> specification,
        Func<TPage, IReadOnlyList<TItem>> items, Func<TPage, int> total, int pageSize, Action<Action> post,
        IReadOnlyList<TItem>? preview = null, int previewTotal = 0, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _queries = queries; _specification = specification; _items = items; _total = total;
        _pageSize = Math.Max(1, pageSize); _post = post;
        _preview = preview ?? []; _previewTotal = Math.Max(previewTotal, _preview.Count);
        _last = _pageSize;
        // Acquisition is passive. Mount activation or a viewport callback declares the initial demand.
        AddPage(0);
    }

    public int Count
    {
        get
        {
            _ = _changed.Value;
            if (_pages.TryGetValue(0, out var page) && page.Snapshot.Value is { Status.HasPrimaryData: true } head)
                return Math.Max(0, _total(head.Value));
            return _previewTotal > 0 ? _previewTotal : _pageSize;
        }
    }

    public TItem? ItemAt(int index)
    {
        _ = _changed.Value;
        if (index < 0) return null;
        int offset = index / _pageSize * _pageSize;
        if (_pages.TryGetValue(offset, out var page) && page.Snapshot.Value is { Status.HasPrimaryData: true } snapshot)
        {
            if (!_projected.TryGetValue(offset, out var projected) || !ReferenceEquals(projected.Snapshot, snapshot))
                _projected[offset] = projected = (snapshot, _items(snapshot.Value));
            var items = projected.Items;
            int local = index - offset;
            return (uint)local < (uint)items.Count ? items[local] : null;
        }
        return (uint)index < (uint)_preview.Count ? _preview[index] : null;
    }

    public Exception? ErrorAt(int index)
    {
        _ = _changed.Value;
        int offset = Math.Max(0, index) / _pageSize * _pageSize;
        if (!_pages.TryGetValue(offset, out var page)) return null;
        var snapshot = page.Snapshot.Value;
        return snapshot.Status.HasPrimaryData ? null
            : page.Failure.Value ?? QueryPresentationRules.InitialFailure(snapshot, "This page is not available.");
    }

    public void RetryAt(int index)
    {
        int offset = Math.Max(0, index) / _pageSize * _pageSize;
        if (!_disposed && _pages.TryGetValue(offset, out var page)) _ = page.RefreshAsync();
    }

    public void SetRange(int first, int lastExclusive)
    {
        if (_disposed) return;
        first = Math.Max(0, first); lastExclusive = Math.Max(first, lastExclusive);
        if (_rangeApplied && _first == first && _last == lastExclusive) return;
        _rangeApplied = true;
        _first = first; _last = lastExclusive;
        var needed = new HashSet<int> { 0 }; // one live count page anchors the complete scroll extent
        for (int offset = _first / _pageSize * _pageSize; offset < _last; offset += _pageSize) needed.Add(offset);
        foreach (int offset in _pages.Keys.Where(offset => !needed.Contains(offset)).ToArray())
        { _pages[offset].Dispose(); _pages.Remove(offset); _projected.Remove(offset); }
        foreach (int offset in needed)
        {
            if (!_pages.TryGetValue(offset, out var page)) page = AddPage(offset);
            // Server offset/limit live on the SearchQuery spec; this page demands its whole server page.
            page.SetDemand(new QueryDemand(true, QueryPriority.Visible, []));
            page.SetActive(_active);
        }
        _changed.Value = _changed.Peek() + 1;
    }

    QuerySignalBinding<TPage> AddPage(int offset)
    {
        var page = new QuerySignalBinding<TPage>(_queries.Acquire(_specification(offset, _pageSize)), _post,
            _ => _changed.Value = _changed.Peek() + 1, time: _time);
        _pages.Add(offset, page);
        return page;
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active && !_rangeApplied) SetRange(_first, _last);
        foreach (var page in _pages.Values) page.SetActive(active);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear(); _projected.Clear();
    }
}
