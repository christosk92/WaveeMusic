using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>Publishes contiguous pages from one provider snapshot. A replacement never borrows an old tail.</summary>
public sealed class RelationProjection
{
    public const int PageSize = 50;
    readonly Func<ResourceArguments, ResourceKey> _key;
    readonly string? _filter;
    IReadOnlyList<CatalogRelationItem> _items = [];
    IReadOnlyList<ResourceKey> _needed = [];
    string? _snapshot;
    int _requestedEnd = PageSize;
    ResourceKey? _nextPage;
    int _candidateCount;
    bool _replacementPending;
    int _readEnd;
    IReadOnlyList<(ResourceKey Key, RelationPageValue? Value)> _pageInputs = [];
    IReadOnlyDictionary<ResourceKey, long> _retryInputs = new Dictionary<ResourceKey, long>();
    public RelationProjection(Func<ResourceArguments, ResourceKey> key, string? filter = null)
    { _key = key; _filter = filter; }
    public IReadOnlyList<CatalogRelationItem> Items => _items;
    public int? Total { get; private set; }
    public bool Loaded { get; private set; }
    public bool Complete { get; private set; }
    public long OrderRevision { get; private set; }

    public void Read(QueryReadContext read)
    {
        if (_readEnd == _requestedEnd && _pageInputs.Count > 0)
        {
            bool unchanged = true;
            foreach (var input in _pageInputs)
                if (!ReferenceEquals(input.Value, read.Read<RelationPageValue>(input.Key))) unchanged = false;
            if (unchanged)
            {
                foreach (var retry in _retryInputs) read.RequireRevalidation(retry.Key, retry.Value);
                return;
            }
        }
        var needed = new List<ResourceKey>();
        var candidate = new List<CatalogRelationItem>();
        string? snapshot = null, cursor = null;
        long basisRevision = 0;
        _nextPage = null;
        bool complete = false;
        int? total = null;
        for (int pageIndex = 0; pageIndex < 2000; pageIndex++)
        {
            int offset = candidate.Count;
            var key = PageKey(offset, cursor);
            needed.Add(key);
            var page = read.Read<RelationPageValue>(key);
            if (page is null) { _nextPage = key; break; }
            if (page.Offset != offset)
            { read.RequireRevalidation(key, basisRevision == 0 ? read.Read(key).Revision : basisRevision); _nextPage = key; break; }
            if (snapshot is null) { snapshot = page.SnapshotId; basisRevision = read.Read(key).Revision; }
            if (!string.Equals(snapshot, page.SnapshotId, StringComparison.Ordinal))
            { read.RequireRevalidation(key, basisRevision); _nextPage = key; break; }
            candidate.AddRange(page.Items);
            total = page.Total;
            complete = page.Coverage == RelationCoverage.Complete
                || page.NextCursor is null && page.Total == candidate.Count;
            _nextPage = complete || page.Items.Count == 0 ? null
                : PageKey(candidate.Count, page.NextCursor);
            if (complete || candidate.Count >= _requestedEnd || page.Items.Count == 0) break;
            cursor = page.NextCursor;
        }
        _needed = needed;
        _readEnd = _requestedEnd;
        _pageInputs = needed.Select(key => (key, read.Read<RelationPageValue>(key))).ToArray();
        _retryInputs = read.RevalidationCandidates.Where(pair => needed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        _candidateCount = candidate.Count;
        if (snapshot is null) return;
        int replacementCoverage = Math.Min(_items.Count, _requestedEnd);
        _replacementPending = _snapshot is not null && snapshot != _snapshot && !complete && candidate.Count < replacementCoverage;
        if (_replacementPending) return;
        if (_snapshot == snapshot && candidate.Count < _items.Count && !complete) return;
        bool sameOrder = _items.Count == candidate.Count && _items.Zip(candidate).All(pair =>
            pair.First.OccurrenceKey == pair.Second.OccurrenceKey && pair.First.EntityUri == pair.Second.EntityUri);
        if (!sameOrder) OrderRevision++;
        if (!_items.SequenceEqual(candidate)) _items = candidate.ToArray();
        _snapshot = snapshot; Total = total; Loaded = true; Complete = complete;
    }

    ResourceKey PageKey(int offset, string? cursor)
    {
        // Native/XM providers encode the next offset as a cursor too. The offset-only and cursor-bearing
        // forms are one resource; opaque provider cursors retain their distinct identity.
        if (cursor is not null && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int numeric)
            && numeric == offset) cursor = null;
        return _key(new(offset, PageSize, cursor, _filter));
    }

    public IReadOnlyList<ResourceKey> Require(int end)
    {
        _requestedEnd = Math.Clamp(Math.Max(end, PageSize), PageSize, 100000);
        if (_needed.Count == 0) return [_key(new(0, PageSize, Filter: _filter))];
        if (_nextPage is not { } next) return _needed;
        if (!_replacementPending && _requestedEnd <= _candidateCount) return _needed;
        // An opaque provider cursor makes only the immediate next page's key knowable (its own PageKey is the
        // only one that can be predicted). A numeric/offset cursor — PageKey normalizes a numeric cursor equal
        // to the offset to null — makes every further page deterministic, so the whole remaining span up to
        // Total can be named in ONE wave (one batch, one document fetch) instead of a round trip per page.
        if (next.Arguments.Cursor is not null) return _needed.Append(next).Distinct().ToArray();
        // Without a reported Total the span's end is unknown, so "everything" (a caller's int.MaxValue) can only mean the
        // NEXT page — not 2000 speculative page keys (100k rows) for one subject, which is what turned a single
        // ArtistPopular envelope into a 10 s, 2000-key fetch. Once a page reports Total the whole remainder is named at once.
        int limit = Total is { } total ? Math.Min(_requestedEnd, total) : Math.Min(_requestedEnd, _candidateCount + PageSize);
        var all = new List<ResourceKey>(_needed);
        var seen = new HashSet<ResourceKey>(_needed);
        for (int offset = _candidateCount; offset < limit; offset += PageSize)
        {
            var key = PageKey(offset, null);
            if (seen.Add(key)) all.Add(key);
        }
        return all;
    }
}
