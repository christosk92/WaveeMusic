using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

public sealed class QueueQueryDefinition(QueueQuery query, IQueueOccurrenceSource source, PlaybackQueueProjection projection)
    : IQueryDefinition<QueueQuerySnapshot>
{
    public QueryReadResult<QueueQuerySnapshot> Pending => new(new([], null, null, null, 0), 0, false);
    public const string Dependency = "playback:queue";
    readonly CatalogReadView _view = projection.ReadView(query.Scope);
    readonly Dictionary<QueueItemId, (Track Base, QueueRuntimeOverride? Runtime, QueueEntry Row)> _rows = new();
    public QueryReadResult<QueueQuerySnapshot> Read(QueryReadContext read)
    {
        read.DependOnReplica(Dependency);
        var snapshot = source.Current;
        var rows = new QueueEntry[snapshot.Rows.Length];
        var keep = new HashSet<QueueItemId>();
        for (int i = 0; i < rows.Length; i++)
        {
            var row = snapshot.Rows[i];
            var track = projection.ReadTrack(read, _view, row.EntityUri);
            var runtime = row.Bucket == QueueBucket.NowPlaying ? snapshot.Runtime : null;
            if (_rows.TryGetValue(row.ItemId, out var cached) && ReferenceEquals(cached.Base, track)
                && Equals(cached.Runtime, runtime) && cached.Row.Bucket == row.Bucket && cached.Row.Provider == row.Provider
                && cached.Row.Uid == row.Uid && Equals(cached.Row.Metadata, row.WireMetadata)) rows[i] = cached.Row;
            else rows[i] = new QueueEntry(row.ItemId, "i" + row.ItemId.Value,
                PlaybackQueueProjection.ApplyOverride(track, row, runtime), row.Bucket,
                row.Provider, row.Provider == QueueProvider.Autoplay, row.Uid, row.WireMetadata);
            _rows[row.ItemId] = (track, runtime, rows[i]); keep.Add(row.ItemId);
        }
        foreach (var id in _rows.Keys.Where(id => !keep.Contains(id)).ToArray()) _rows.Remove(id);
        var current = rows.FirstOrDefault(row => row.Bucket == QueueBucket.NowPlaying)?.Track;
        string? contextName = snapshot.ContextUri is { Length: > 0 } context ? ContextName(read, context) : null;
        if (current is not null) read.Read(_view.Key(current.Uri, FacetKind.VideoAssociation));
        return new(new QueueQuerySnapshot(rows, current, snapshot.ContextUri, contextName, snapshot.StructuralRevision),
            snapshot.StructuralRevision, current is not null || rows.Length > 0);
    }
    string? ContextName(QueryReadContext read, string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Playlist => _view.Playlist(read, uri).Name,
        EntityKind.Album => read.Read<AlbumIdentityValue>(_view.Key(uri, FacetKind.AlbumIdentity))?.Name,
        EntityKind.Artist => read.Read<ArtistIdentityValue>(_view.Key(uri, FacetKind.ArtistIdentity))?.Name,
        EntityKind.Show => read.Read<ShowIdentityValue>(_view.Key(uri, FacetKind.ShowIdentity))?.Name,
        _ => null,
    };
    readonly HashSet<ResourceKey> _requiredSeen = [];
    readonly List<ResourceKey> _requiredBuffer = [];
    ResourceKey[] _requiredKeys = [];
    IReadOnlyList<QueueEntry>? _requiredRows;
    Track? _requiredCurrent;
    string? _requiredContext;
    IReadOnlyList<FacetKind>? _requiredFacets;

    public QueryRequirements Requirements(QueueQuerySnapshot value, QueryDemand demand)
    {
        if (_requiredFacets is not null && ReferenceEquals(_requiredRows, value.Rows)
            && ReferenceEquals(_requiredCurrent, value.Current)
            && _requiredContext == value.ContextUri && ReferenceEquals(_requiredFacets, demand.Facets))
            return new(_requiredKeys, []);
        _requiredRows = value.Rows;
        _requiredCurrent = value.Current;
        _requiredContext = value.ContextUri;
        _requiredFacets = demand.Facets;
        _requiredSeen.Clear();
        _requiredBuffer.Clear();
        void Add(Track track)
        {
            AddKey(_view.Key(track.Uri, CatalogReadView.IdentityFacet(track.Uri)));
            if (track.Album.Uri.Length > 0) AddKey(_view.Key(track.Album.Uri, CatalogReadView.IdentityFacet(track.Album.Uri)));
            foreach (var artist in track.Artists) if (artist.Uri.Length > 0) AddKey(_view.Key(artist.Uri, FacetKind.ArtistIdentity));
            foreach (var facet in demand.Facets) AddKey(_view.Key(track.Uri, facet));
        }
        if (value.Current is { } current) Add(current);
        foreach (var row in value.Rows) Add(row.Track);
        if (value.ContextUri is { Length: > 0 } context && EntityUri.KindOf(context) is EntityKind.Playlist or EntityKind.Album or EntityKind.Artist or EntityKind.Show)
            AddKey(_view.Key(context, CatalogReadView.IdentityFacet(context)));
        _requiredKeys = _requiredBuffer.Count == 0 ? [] : _requiredBuffer.ToArray();
        return new(_requiredKeys, []);

        void AddKey(ResourceKey key)
        {
            if (_requiredSeen.Add(key)) _requiredBuffer.Add(key);
        }
    }
}
