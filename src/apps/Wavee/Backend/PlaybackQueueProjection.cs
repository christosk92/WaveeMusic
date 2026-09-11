using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend;

/// <summary>One catalog join for session, Connect and queue display. No metadata fetch or private metadata owner.</summary>
public sealed class PlaybackQueueProjection : IQueueOccurrenceSource
{
    readonly CatalogRepository _catalog;
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string, string> _ownerProvider;
    readonly SimpleEvent<QueueOccurrenceSnapshot> _changes = new();
    readonly object _gate = new();
    readonly object _readGate = new();
    sealed record CachedTrack(Track Track, IReadOnlyDictionary<ResourceKey, ResourceSnapshot> Dependencies);
    readonly Dictionary<ResourceKey, CachedTrack> _tracks = new();
    long _owner;
    QueueOccurrenceSnapshot _current = QueueOccurrenceSnapshot.Empty;

    public PlaybackQueueProjection(CatalogRepository catalog, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
        => (_catalog, _replicas, _ownerProvider) = (catalog, replicas, ownerProvider);
    public long Epoch => _catalog.Epoch;
    public CatalogScope Scope => _catalog.Scope;
    public QueueOccurrenceSnapshot Current => Volatile.Read(ref _current);
    public IObservable<QueueOccurrenceSnapshot> Changes => _changes;
    public IObservable<CatalogChangeSet> CatalogChanges => _catalog.Changes;
    public bool IsOwner(long owner) { lock (_gate) return owner == _owner; }
    public long ActivateOwner()
    {
        QueueOccurrenceSnapshot next;
        long owner;
        lock (_gate) { owner = ++_owner; next = QueueOccurrenceSnapshot.Empty with { StructuralRevision = _current.StructuralRevision + 1 }; Volatile.Write(ref _current, next); }
        lock (_readGate) _tracks.Clear();
        _changes.OnNext(next);
        return owner;
    }
    public void ReleaseOwner(long owner)
    {
        QueueOccurrenceSnapshot next;
        lock (_gate)
        {
            if (owner != _owner) return;
            _owner++;
            next = QueueOccurrenceSnapshot.Empty with { StructuralRevision = _current.StructuralRevision + 1 };
            Volatile.Write(ref _current, next);
        }
        lock (_readGate) _tracks.Clear();
        _changes.OnNext(next);
    }
    public CatalogReadView ReadView(CatalogScope scope) => new(scope, _replicas, _ownerProvider);
    public Track ReadTrack(string uri) => _catalog.ReadConsistent(() =>
    {
        var scope = Scope;
        var provider = _ownerProvider(uri);
        var key = new ResourceKey(provider.Length == 0 || provider == scope.Provider ? scope : scope with { Provider = provider },
            uri, CatalogReadView.IdentityFacet(uri));
        lock (_readGate)
        {
            if (_tracks.TryGetValue(key, out var cached))
            {
                bool unchanged = true;
                foreach (var dependency in cached.Dependencies)
                {
                    var value = _catalog.TryPeek(dependency.Key, out var current) ? current.Value : null;
                    if (!ReferenceEquals(dependency.Value.Value, value)) { unchanged = false; break; }
                }
                if (unchanged) return cached.Track;
            }
        }
        return ReadTrackCore(new QueryReadContext(_catalog), ReadView(scope), uri);
    });
    public Track ReadTrack(QueryReadContext read, CatalogReadView view, string uri)
        => _catalog.ReadConsistent(() => ReadTrackCore(read, view, uri));
    Track ReadTrackCore(QueryReadContext read, CatalogReadView view, string uri)
    {
        var key = view.Key(uri, CatalogReadView.IdentityFacet(uri));
        lock (_readGate)
        {
            if (_tracks.TryGetValue(key, out var cached))
            {
                bool unchanged = true;
                foreach (var dependency in cached.Dependencies)
                    unchanged &= ReferenceEquals(dependency.Value.Value, read.Read(dependency.Key, allowColdRead: false).Value);
                if (unchanged) return cached.Track;
            }
            var isolated = new QueryReadContext(_catalog);
            var track = view.Track(isolated, uri);
            foreach (var dependency in isolated.Resources.Keys) read.Read(dependency, allowColdRead: false);
            _tracks[key] = new CachedTrack(track, new Dictionary<ResourceKey, ResourceSnapshot>(isolated.Resources));
            return track;
        }
    }
    public QueueEntry Materialize(QueueOccurrence row)
        => new(row.ItemId, "i" + row.ItemId.Value, ReadTrack(row.EntityUri), row.Bucket, row.Provider,
            row.Provider == QueueProvider.Autoplay, row.Uid, row.WireMetadata);
    public QueueEntry Materialize(QueueEntry row)
    {
        var track = ReadTrack(row.Track.Uri);
        return ReferenceEquals(track, row.Track) ? row : row with { Track = track };
    }
    ImmutableArray<QueueEntry> Materialize(ImmutableArray<QueueEntry> rows)
    {
        ImmutableArray<QueueEntry>.Builder? changed = null;
        for (int i = 0; i < rows.Length; i++)
        {
            var next = Materialize(rows[i]);
            if (!ReferenceEquals(next, rows[i])) (changed ??= rows.ToBuilder())[i] = next;
        }
        return changed?.ToImmutable() ?? rows;
    }
    public QueueSnapshot Materialize(QueueSnapshot snapshot)
        => _catalog.ReadConsistent(() => MaterializeCore(snapshot));
    QueueSnapshot MaterializeCore(QueueSnapshot snapshot)
    {
        var current = snapshot.Current is { } row ? Materialize(row) : null;
        var history = Materialize(snapshot.History);
        var user = Materialize(snapshot.UserQueue);
        var upcoming = Materialize(snapshot.Upcoming);
        return ReferenceEquals(current, snapshot.Current) && history == snapshot.History && user == snapshot.UserQueue && upcoming == snapshot.Upcoming
            ? snapshot : snapshot with { Current = current, History = history, UserQueue = user, Upcoming = upcoming };
    }

    public Task SeedTracksAsync(IReadOnlyList<Track> tracks, long expectedEpoch, CancellationToken ct = default)
    {
        var scope = Scope;
        var seeds = new List<CatalogSeed>();
        foreach (var track in tracks)
        {
            if (EntityUri.KindOf(track.Uri) == Wavee.Core.EntityKind.Episode)
                CatalogDomainSeeds.Episode(scope, new Episode(track.Id, track.Uri, track.Title, track.Album.Name, track.Image,
                    track.DurationMs, default, ShowUri: track.Album.Uri.Length > 0 ? track.Album.Uri : null), seeds);
            else CatalogDomainSeeds.Track(scope, track, seeds);
        }
        return _catalog.SeedManyAsync(seeds.Select(seed => seed with { Key = seed.Key with
            { Scope = scope with { Provider = _ownerProvider(seed.Key.Subject) is { Length: > 0 } provider ? provider : scope.Provider } } }).ToArray(), expectedEpoch, ct);
    }

    /// <summary>A resolved owner answer replaces this playable's facts; referenced artists/albums remain seeds.</summary>
    public async Task ObserveOwnerTrackAsync(Track track, long expectedEpoch, CancellationToken ct = default)
    {
        var scope = Scope;
        var seeds = new List<CatalogSeed>();
        CatalogDomainSeeds.Track(scope, track, seeds);
        var observations = seeds.Select(seed =>
        {
            var key = seed.Key with { Scope = scope with { Provider = _ownerProvider(seed.Key.Subject) } };
            CatalogPatch patch = seed.Patch;
            if (key.Subject == track.Uri && patch is TrackIdentityPatch identity)
                patch = identity with { DurationMs = FieldChange<long?>.Set(track.DurationMs > 0 ? track.DurationMs : null) };
            return new CatalogObservation(key, patch, FillUnknownOnly: key.Subject != track.Uri);
        }).ToArray();
        if (!await _catalog.ObserveAsync(observations, expectedEpoch, ct).ConfigureAwait(false))
            throw new OperationCanceledException("The playback catalog session changed.");
    }

    public void Publish(long owner, IReadOnlyList<QueueEntry> rows, string? contextUri, QueueRuntimeOverride? runtime = null)
    {
        QueueOccurrenceSnapshot next;
        bool changed;
        lock (_gate)
        {
        if (owner != _owner) return;
        var previous = Current;
        if (SameStructure(previous, rows, contextUri) && SameWireMetadata(previous, rows) && Equals(previous.Runtime, runtime)) return;
        var occurrences = rows.Select(row => new QueueOccurrence(row.ItemId, row.Uid, row.Track.Uri, row.Bucket,
            row.Provider, QueueRowKind.Playable, contextUri, row.Metadata)).ToImmutableArray();
        changed = !SameStructure(previous, occurrences, contextUri);
        next = new QueueOccurrenceSnapshot(previous.StructuralRevision + (changed ? 1 : 0), occurrences, contextUri, runtime);
        Volatile.Write(ref _current, next);
        changed |= !Equals(previous.Runtime, runtime);
        }
        if (changed)
        {
            var retained = next.Rows.Select(row => row.EntityUri).ToHashSet(StringComparer.Ordinal);
            var account = Scope.ProviderAccount;
            lock (_readGate)
                foreach (var key in _tracks.Keys.Where(key => !retained.Contains(key.Subject) || key.Scope.ProviderAccount != account).ToArray())
                    _tracks.Remove(key);
            _changes.OnNext(next);
        }
    }

    static bool SameStructure(QueueOccurrenceSnapshot previous, ImmutableArray<QueueOccurrence> next, string? context)
    {
        if (previous.ContextUri != context || previous.Rows.Length != next.Length) return false;
        for (int i = 0; i < next.Length; i++)
        {
            var a = previous.Rows[i]; var b = next[i];
            if (a.ItemId != b.ItemId || a.EntityUri != b.EntityUri || a.Uid != b.Uid || a.Bucket != b.Bucket || a.Provider != b.Provider) return false;
        }
        return true;
    }

    static bool SameStructure(QueueOccurrenceSnapshot previous, IReadOnlyList<QueueEntry> next, string? context)
    {
        if (previous.ContextUri != context || previous.Rows.Length != next.Count) return false;
        for (int i = 0; i < next.Count; i++)
        {
            var a = previous.Rows[i]; var b = next[i];
            if (a.ItemId != b.ItemId || a.EntityUri != b.Track.Uri || a.Uid != b.Uid || a.Bucket != b.Bucket || a.Provider != b.Provider) return false;
        }
        return true;
    }

    static bool SameWireMetadata(QueueOccurrenceSnapshot previous, IReadOnlyList<QueueEntry> next)
    {
        for (int i = 0; i < next.Count; i++)
            if (!ReferenceEquals(previous.Rows[i].WireMetadata, next[i].Metadata)) return false;
        return true;
    }

    public static Track ApplyOverride(Track track, QueueOccurrence row, QueueRuntimeOverride? runtime)
        => runtime is null || (runtime.Title is null && runtime.Artist is null && runtime.DurationMs is null) || runtime.ItemId != row.ItemId || row.Bucket != QueueBucket.NowPlaying ? track : track with
        {
            Title = runtime.Title ?? track.Title,
            Artists = runtime.Artist is { } artist ? [new ArtistRef("", "", artist)] : track.Artists,
            DurationMs = runtime.DurationMs ?? track.DurationMs,
        };
}
