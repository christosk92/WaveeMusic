using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Backend.Queries;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class PlaylistMutationConsistencyTests : IAsyncLifetime
{
    const string PlaylistUri = "spotify:playlist:p";
    static readonly SessionContext Ctx = new("alice", "NL", "premium", "en", Tier.Premium, false);

    sealed class FailingTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(false, Array.Empty<byte>(), 409));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    readonly List<ReplicaTestHost> _hosts = [];
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() { foreach (var host in _hosts) await host.DisposeAsync(); }
    static byte[] Revision() { var result = new byte[24]; result[23] = 1; return result; }
    static byte[] Ack() => new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Revision()) }.ToByteArray();
    static Playlist ReadPlaylist(ReplicaTestHost host) => new CatalogReadView(host.Catalog.Scope, host.Replicas, _ => "spotify")
        .Playlist(new QueryReadContext(host.Catalog), PlaylistUri, includeRows: true);
    static Track ReadTrack(ReplicaTestHost host, string uri) => new CatalogReadView(host.Catalog.Scope, host.Replicas, _ => "spotify")
        .Track(new QueryReadContext(host.Catalog), uri);

    async Task<(InMemoryStore Store, MutationEngine Mutations, PlaylistMutationSource Source, ReplicaTestHost Host, LibrarySync Sync)> Create(ITransport transport)
    {
        var host = new ReplicaTestHost(account: "alice"); _hosts.Add(host);
        await host.SeedPlaylistAsync(PlaylistUri, [], Revision(),
            new Playlist("p", PlaylistUri, "New playlist", null, "alice", null, 0));
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), []));
        var sync = host.AttachSync(http, transport);
        var source = new PlaylistMutationSource(host.Mutations, transport, http, () => host.Context,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), host.Store, host.Replicas, sync);
        return ((InMemoryStore)host.Store, host.Mutations, source, host, sync);
    }

    [Fact]
    public async Task AddRecommendedTrack_SeedsCanonicalEntityWithOptimisticMembership()
    {
        var transport = new RecordingTransport();
        var (store, mutations, source, host, sync) = await Create(transport);
        var track = new Track("t", "spotify:track:t", "Recommended", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 123_000, false, null);

        await source.AddTracksAsync(PlaylistUri, new[] { track }, TestContext.Current.CancellationToken);

        Assert.Equal(track.Title, ReadTrack(host, track.Uri).Title);
        Assert.Equal(track.DurationMs, ReadTrack(host, track.Uri).DurationMs);
        Assert.Equal(track.Uri, Assert.Single(store.Membership(PlaylistUri)).ItemUri);
        Assert.Equal(0, mutations.Pending);
        Assert.Equal("POST", transport.LastRequestMethod);
        Assert.Equal("/playlist/v2/playlist/p/changes", transport.LastRequestRoute);
    }

    [Fact]
    public async Task ProtocolQueue_IsAwaitedBeforeMutationReportsSuccess()
    {
        var transport = new RecordingTransport();
        var (store, mutations, source, host, sync) = await Create(transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = sync.ExecuteRootlistAsync(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await entered.Task;
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = host.Replicas.Changes.Subscribe(Observers.From<ReplicaChange>(_ =>
        { if (ReadPlaylist(host).Name == "Renamed") staged.TrySetResult(); }));
        var save = source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken);
        await staged.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(save.IsCompleted);
        Assert.Equal("Renamed", ReadPlaylist(host).Name);
        Assert.Null(transport.LastRequestRoute);
        release.SetResult();
        await Task.WhenAll(held, save);
        Assert.Equal(0, mutations.Pending);
    }

    // The durable optimistic transaction publishes membership and supplied identity before its POST completes.
    /// <summary>An ITransport whose every request parks on a gate - "the server has not answered yet".</summary>
    sealed class RecordingTransport : ITransport
    {
        readonly Task _gate;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastRequestRoute, LastRequestMethod;
        public readonly List<string> Routes = new();
        public RecordingTransport(Task? gate = null) => _gate = gate ?? Task.CompletedTask;

        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            lock (Routes) Routes.Add(route);
            LastRequestRoute = route; LastRequestMethod = method ?? "POST"; Entered.TrySetResult();
            return Wait();
            async Task<Resp> Wait()
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                return new Resp(true, Ack(), 200);
            }
        }

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static (List<ReplicaChange> Seen, IDisposable Sub) Watch(ReplicaTestHost host)
    {
        var seen = new List<ReplicaChange>();
        var sub = host.Replicas.Changes.Subscribe(Observers.From<ReplicaChange>(c => { lock (seen) seen.Add(c); }));
        return (seen, sub);
    }

    static bool Saw(List<ReplicaChange> seen, string uri)
    {
        lock (seen) return seen.Exists(c => c.AggregateId == uri);
    }

    [Fact]
    public async Task OptimisticInsert_PublishesReplicaBeforeTheDrainIsAnswered()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(gate.Task);
        var (store, mutations, source, host, sync) = await Create(transport);
        var (seen, sub) = Watch(host);
        using var _ = sub;
        var track = new Track("t", "spotify:track:t", "Recommended", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 123_000, false, null);

        var write = source.InsertTracksAsync(PlaylistUri, new[] { track }, toIndex: 0);
        await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The POST is still in flight...
        Assert.False(write.IsCompleted);
        // Both occurrence membership and canonical identity are readable before the transport reply.
        Assert.Equal(track.Title, ReadTrack(host, track.Uri).Title);
        Assert.Equal(track.Uri, Assert.Single(store.Membership(PlaylistUri)).ItemUri);
        Assert.True(Saw(seen, PlaylistUri), "the optimistic membership write must publish a replica change before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task OptimisticMove_PublishesBeforeTheDrain_AndKeepsEveryRowIdentity()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(gate.Task);
        var (store, mutations, source, host, sync) = await Create(transport);
        var seedRows = new[]
        {
            new PlaylistMember("aaaaaaaaaaaaaa01", "spotify:track:a", "alice", 1),
            new PlaylistMember("aaaaaaaaaaaaaa02", "spotify:track:b", "alice", 2),
            new PlaylistMember("aaaaaaaaaaaaaa03", "spotify:track:c", "alice", 3),
        };
        await host.SeedPlaylistAsync(PlaylistUri, seedRows, Revision());
        var (seen, sub) = Watch(host);
        using var _ = sub;

        // Drag row 0 to the end (the pre-move insertion convention: "insert before the row currently at index 3").
        var write = source.MoveRowsAsync(PlaylistUri, new[] { new PlaylistRowRef(0, "spotify:track:a", "aaaaaaaaaaaaaa01") }, toIndex: 3);
        await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(write.IsCompleted, write.IsFaulted ? write.Exception!.ToString() : "the write completed without reaching the wire");
        var rows = store.Membership(PlaylistUri);
        Assert.Equal(new[] { "aaaaaaaaaaaaaa02", "aaaaaaaaaaaaaa03", "aaaaaaaaaaaaaa01" }, rows.Select(r => r.ItemId).ToArray());
        // No BLANK slot: every row still names a real entity AND keeps its stable item id, so the list can key the
        // moved row across the swap instead of rendering an empty band where it used to be.
        Assert.All(rows, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.ItemId));
            Assert.False(string.IsNullOrEmpty(r.ItemUri));
        });
        Assert.True(Saw(seen, PlaylistUri), "the optimistic move must publish a replica change before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task OptimisticRemove_PublishesBeforeTheDrain()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(gate.Task);
        var (store, mutations, source, host, sync) = await Create(transport);
        await host.SeedPlaylistAsync(PlaylistUri, new[]
        {
            new PlaylistMember("aaaaaaaaaaaaaa01", "spotify:track:a", "alice", 1),
            new PlaylistMember("aaaaaaaaaaaaaa02", "spotify:track:b", "alice", 2),
        }, Revision());
        var (seen, sub) = Watch(host);
        using var _ = sub;

        var write = source.RemoveRowsAsync(PlaylistUri, new[] { new PlaylistRowRef(0, "spotify:track:a", "aaaaaaaaaaaaaa01") });
        await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(write.IsCompleted, write.IsFaulted ? write.Exception!.ToString() : "the write completed without reaching the wire");
        Assert.Equal("aaaaaaaaaaaaaa02", Assert.Single(store.Membership(PlaylistUri)).ItemId);
        Assert.True(Saw(seen, PlaylistUri), "the optimistic remove must publish a replica change before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task FailedServerAttempt_DoesNotReportConfirmedSuccess()
    {
        var (_, mutations, source, _, _) = await Create(new FailingTransport());
        // P1: the ONE failure type the seam surfaces. A write that is still queued after its drain is Pending — never a
        // bare InvalidOperationException whose message the UI would have to sniff.
        var error = await Assert.ThrowsAsync<PlaylistMutationException>(() =>
            source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(PlaylistMutationFailure.Pending, error.Kind);
        Assert.Equal(1, mutations.Pending); // durable retry remains queued
    }
}
