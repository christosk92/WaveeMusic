using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Queries;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// Actual transport decoding, durable replicas, normalized queries and dealer ingress share one persisted vertical.
public class LibraryVerticalTests
{
    const string PlaylistUri = "spotify:playlist:p";
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }
    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }

    static byte[] CraftPlaylist(byte rev, params (string Uri, string AddedBy)[] items)
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(rev)), Length = items.Length,
            Attributes = new Pl.ListAttributes { Name = "Mix" } };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        foreach (var it in items)
            contents.Items.Add(new Pl.Item { Uri = it.Uri, Attributes = new Pl.ItemAttributes { AddedBy = it.AddedBy } });
        slc.Contents = contents;
        return slc.ToByteArray();
    }

    sealed class Transport : ITransport
    {
        readonly SimpleSubject<WireEvent> _events = new();
        public int RequestsSent;
        public void Push(WireEvent value) => _events.OnNext(value);
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            RequestsSent++;
            return Task.FromResult(new Resp(true,
                new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev24(2)) }.ToByteArray(), 200));
        }
        public IObservable<WireEvent> Events(string prefix) => _events;
        public IObservable<WireRequest> Requests(string prefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> state, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, [], 200));
    }

    static async Task<ReplicaTestHost> Open(SqliteColdStore persistence, InMemoryStore store)
    {
        var bootstrap = await ((IReplicaPersistence)persistence).LoadAsync(new("bob", 1), TestContext.Current.CancellationToken);
        var host = new ReplicaTestHost(store: store, persistence: persistence, bootstrap: bootstrap);
        await host.Replicas.PublishInitialAsync();
        await host.Replicas.EnsurePlaylistCachedAsync(PlaylistUri);
        return host;
    }

    static QueryService Queries(ReplicaTestHost host, IResourceCoordinator resources)
    {
        var queries = new QueryService(host.Catalog, resources, new LibraryQueryDemand(() => null, host.Replicas), host.Replicas.Changes);
        CatalogQueryDefinitions.Register(queries, host.Replicas, _ => "spotify");
        return queries;
    }

    [Fact]
    public async Task FullVertical_Fetch_Query_Edit_Push_Persist()
    {
        var path = TempDb();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            using (var persistence = new SqliteColdStore(path))
            {
                await using var host = await Open(persistence, new InMemoryStore());
                await using var resources = new ResourceCoordinator(host.Catalog, [], TimeProvider.System);
                using var queries = Queries(host, resources);
                using var query = queries.Acquire(new PlaylistDetailQuery(host.Catalog.Scope, PlaylistUri));
                var published = new System.Collections.Concurrent.ConcurrentQueue<Playlist>();
                using var subscription = query.Changes.Subscribe(Observers.From<QuerySnapshot<Playlist>>(s => published.Enqueue(s.Value)));

                var http = new FakeExchange((_, _) => new HttpResp(200, new Dictionary<string, string>(),
                    CraftPlaylist(1, ("spotify:track:a", "alice"), ("spotify:track:b", "bob"))));
                var fetcher = new PlaylistFetcher(http, () => "https://spclient.test", () => "bob");
                await host.Replicas.AdoptPlaylistAsync(await fetcher.FetchPlaylistAsync(PlaylistUri, ct));
                await QueryPublication.Until(() => query.Current.Value.Tracks is { Count: 2 });
                Assert.Equal(2, query.Current.Value.Tracks!.Count); // unresolved entities retain their occurrence slots
                Assert.Equal("alice", query.Current.Value.Tracks[0].AddedBy);

                var seeds = new List<CatalogSeed>();
                foreach (var id in new[] { "a", "b" })
                    CatalogDomainSeeds.Track(host.Catalog.Scope,
                        new Track(id, "spotify:track:" + id, "T-" + id, [], new("", "", ""), 1000, false, null), seeds);
                await host.Catalog.SeedManyAsync(seeds, host.Catalog.Epoch, ct);
                await QueryPublication.Until(() => query.Current.Value.Tracks![0].Title == "T-a"
                    && published.Any(p => p.Tracks is { Count: 2 } rows && rows[0].Title == "T-a"));
                Assert.Equal("Mix", query.Current.Value.Name);
                Assert.Equal("T-a", query.Current.Value.Tracks![0].Title);
                Assert.Contains(published, p => p.Tracks is { Count: 2 } && p.Tracks[0].Title == "T-a");

                await host.Mutations.EditAsync(PlaylistUri, [new(PlaylistOpKind.Remove, FromIndex: 0, Length: 1)]);
                await QueryPublication.Until(() => query.Current.Value.Tracks is { Count: 1 });
                Assert.Equal("spotify:track:b", Assert.Single(query.Current.Value.Tracks!).Uri);
                var transport = new Transport();
                await host.Mutations.Drain(transport, host.Context, ct);
                Assert.Equal(0, host.Mutations.Pending);
                Assert.Equal(1, transport.RequestsSent);

                var sync = host.AttachSync(http, transport);
                using var router = new DealerRouter(transport, sync);
                var mod = new Pl.PlaylistModificationInfo { Uri = ByteString.CopyFromUtf8(PlaylistUri),
                    ParentRevision = ByteString.CopyFrom(Rev24(2)), NewRevision = ByteString.CopyFrom(Rev24(9)) };
                var add = new Pl.Add { AddLast = true };
                add.Items.Add(new Pl.Item { Uri = "spotify:track:b" });
                mod.Ops.Add(new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add });
                transport.Push(new("hm://playlist/v2/playlist/p", mod.ToByteArray()));
                await sync.WaitForIdleAsync();
                await QueryPublication.Until(() => query.Current.Value.Tracks is { Count: 2 });
                Assert.Equal(2, query.Current.Value.Tracks!.Count);
                Assert.All(query.Current.Value.Tracks, track => Assert.Equal("T-b", track.Title));
                Assert.Equal(Rev24(9), host.Replicas.ReadPlaylist(PlaylistUri).Revision);
                await host.Queue.FlushAsync();
            }

            using (var persistence = new SqliteColdStore(path))
            {
                await using var host = await Open(persistence, new InMemoryStore());
                await host.Catalog.SetSessionAsync(host.Catalog.Scope, "bob", online: false, ct);
                await using var resources = new ResourceCoordinator(host.Catalog, [], TimeProvider.System);
                using var queries = Queries(host, resources);
                var snapshot = await queries.ReadOnceAsync(new PlaylistDetailQuery(host.Catalog.Scope, PlaylistUri),
                    cancellationToken: ct);
                Assert.True(snapshot.Status.HasPrimaryData);
                Assert.True(snapshot.Status.IsOffline);
                Assert.Equal("Mix", snapshot.Value.Name);
                Assert.Equal(2, snapshot.Value.Tracks!.Count);
                Assert.Equal("spotify:track:b", snapshot.Value.Tracks[1].Uri);
                Assert.All(snapshot.Value.Tracks, track => Assert.Equal("T-b", track.Title));
                Assert.Equal(Rev24(9), host.Replicas.ReadPlaylist(PlaylistUri).Revision);
            }
        }
        finally { TryDelete(path); }
    }
}
