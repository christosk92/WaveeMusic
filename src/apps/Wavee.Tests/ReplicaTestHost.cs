using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Tests;

/// <summary>Real queue, coordinator and strategies; explicit in-memory durability and display sink for protocol tests.</summary>
internal sealed class ReplicaTestHost : IAsyncDisposable
{
    public DataCommitQueue Queue { get; } = new();
    public IStore Store { get; }
    public CatalogRepository Catalog { get; }
    public LibraryReplicaCoordinator Replicas { get; }
    public MutationEngine Mutations { get; }
    public CollectionEchoRing Echo { get; } = new();
    public SessionContext Context { get; }
    public ReplicaScope Scope { get; }
    readonly List<LibrarySync> _syncs = [];
    readonly CancellationTokenSource _lifetime = new();

    public ReplicaTestHost(string account = "bob", IStore? store = null, IReplicaPersistence? persistence = null,
        ReplicaBootstrap? bootstrap = null, Func<DateTime>? clock = null)
    {
        Store = store ?? new InMemoryStore();
        Scope = new ReplicaScope(account, 1);
        Context = new SessionContext(account, "US", "premium", "en", Tier.Premium, false);
        Catalog = new CatalogRepository(Queue, persistence as ICatalogPersistence ?? new CatalogMemoryPersistence(),
            TimeProvider.System, new CatalogScope("spotify", account, "en", "US", "premium", 1, false), account);
        Replicas = new LibraryReplicaCoordinator(Queue, persistence ?? new ReplicaMemoryPersistence(),
            Store as IReplicaProjectionSink ?? new TestProjectionSink(Store), () => Scope,
            bootstrap ?? ReplicaBootstrap.Empty, Catalog);
        Mutations = new MutationEngine(Replicas,
            [new SetReplayStrategy(Echo), new OpRebaseStrategy(Replicas, () => "https://spclient.test"),
                new CreatePlaylistStrategy(Replicas, () => "https://spclient.test"),
                new RootlistFollowStrategy(Replicas, () => "https://spclient.test")], clock);
    }

    public Task SeedHeaderAsync(Playlist header) => Replicas.AdoptHeaderAsync(header.Uri, header);
    public Playlist? ReadHeader(string uri) => Replicas.ReadConfirmedPlaylist(uri).Header;
    public Task SeedTrackAsync(Track track)
    {
        var seeds = new List<CatalogSeed>();
        CatalogDomainSeeds.Track(Catalog.Scope, track, seeds);
        return Catalog.SeedManyAsync(seeds, Catalog.Epoch);
    }
    public Track? ReadTrack(string uri)
    {
        if (Catalog.Peek(new ResourceKey(Catalog.Scope, uri, FacetKind.TrackIdentity)).Value is null) return null;
        return new CatalogReadView(Catalog.Scope, Replicas, _ => "spotify").Track(new QueryReadContext(Catalog), uri);
    }
    public Task SeedSavedAsync(string setId, string uri, bool saved, SyncState state = SyncState.Confirmed, long addedAtMs = 0)
        => state == SyncState.Pending ? Mutations.SaveAsync(setId, uri, saved)
            : Replicas.ApplyCollectionPushAsync(CollectionSets.WireSet(setId), [new CollectionItem(uri, !saved, addedAtMs)]);
    public Task SeedPlaylistAsync(string uri, IReadOnlyList<PlaylistMember> members, byte[]? revision, Playlist? header = null)
        => Replicas.AdoptPlaylistAsync(new PlaylistReadResult(uri, PlaylistReadKind.Snapshot, null, revision,
            members.ToImmutableArray(), [], header));
    public Task SeedRootlistAsync(IReadOnlyList<RootlistEntry> entries, byte[]? revision)
        => Replicas.AdoptRootlistAsync(new RootlistReadResult(entries.ToImmutableArray(), revision));
    public Task SeedCollectionAsync(string wireSet, IReadOnlyList<CollectionItem> items, string token = "seed")
        => Replicas.AdoptCollectionAsync(new CollectionReadResult(wireSet, true, true, 0, null, token, items.ToImmutableArray()));

    public LibrarySync AttachSync(IHttpExchange http, ITransport transport, PlaylistSignalsClient? signals = null,
        PlaylistResyncQueue? resync = null, WaveeLogger log = default)
    {
        var sync = new LibrarySync(Store, Replicas, new PlaylistFetcher(http, () => "https://spclient.test", () => Scope.Account!),
            new CollectionFetcher(http, () => "https://spclient.test", () => Scope.Account!, log), Mutations,
            resync ?? new PlaylistResyncQueue(), transport, () => Context, () => Scope.Account!, log, _lifetime.Token, Echo, signals);
        _syncs.Add(sync);
        return sync;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (var sync in _syncs) await sync.DisposeAsync();
        Mutations.Dispose();
        await Queue.DisposeAsync();
        _lifetime.Dispose();
    }

    sealed class TestProjectionSink(IStore store) : IReplicaProjectionSink
    {
        public void Publish(ReplicaProjection projection)
        {
            using var bulk = store.BeginBulk();
            foreach (var playlist in projection.Playlists)
            {
                store.SetMembership(playlist.Uri, playlist.Members, playlist.Revision);
            }
            if (projection.Rootlist is { } root) store.SetRootlist(root.Entries, root.Revision);
            foreach (var collection in projection.Collections)
            {
                var present = collection.Items.Select(x => x.Uri).ToHashSet(StringComparer.Ordinal);
                foreach (var uri in store.SavedUris(collection.SetId).ToArray())
                    if (!present.Contains(uri)) store.SetSaved(collection.SetId, uri, false,
                        collection.PendingUris.Contains(uri) ? SyncState.Pending : SyncState.Confirmed);
                foreach (var row in collection.Items) store.SetSaved(collection.SetId, row.Uri, true,
                    collection.PendingUris.Contains(row.Uri) ? SyncState.Pending : SyncState.Confirmed, row.AddedAtMs);
            }
        }
    }
}

internal sealed class ReplicaMemoryPersistence : IReplicaPersistence
{
    readonly Wavee.Backend.Persistence.MemoryDataPersistence _inner = new();
    public List<ReplicaTransaction> Transactions { get; } = [];
    public ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct) => _inner.LoadAsync(scope, ct);
    public ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct) => _inner.LoadPlaylistAsync(scope, uri, ct);
    public async ValueTask CommitAsync(ReplicaTransaction transaction, CancellationToken ct)
    {
        await _inner.CommitAsync(transaction, ct);
        Transactions.Add(transaction);
    }
}
