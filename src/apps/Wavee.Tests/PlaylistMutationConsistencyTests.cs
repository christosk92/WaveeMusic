using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class PlaylistMutationConsistencyTests
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

    static (InMemoryStore Store, MutationEngine Mutations, PlaylistMutationSource Source) Create(ITransport transport)
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p", PlaylistUri, "New playlist", null, "alice", null, 0));
        store.SetMembership(PlaylistUri, Array.Empty<PlaylistMember>(), new byte[] { 1 });
        var mutations = new MutationEngine(store,
            new IMutationStrategy[] { new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", new PlaylistResyncQueue()) });
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        var source = new PlaylistMutationSource(mutations, transport, http, () => Ctx,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), new RootlistLane(), store);
        return (store, mutations, source);
    }

    [Fact]
    public async Task AddRecommendedTrack_HydratesEntityBeforeOptimisticMembership()
    {
        var transport = new StubTransport();
        var (store, mutations, source) = Create(transport);
        var track = new Track("t", "spotify:track:t", "Recommended", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 123_000, false, null);

        await source.AddTracksAsync(PlaylistUri, new[] { track }, TestContext.Current.CancellationToken);

        Assert.Same(track, store.GetTrack(track.Uri));
        Assert.Equal(track.Uri, Assert.Single(store.Membership(PlaylistUri)).ItemUri);
        Assert.Equal(0, mutations.Pending);
        Assert.Equal("POST", transport.LastRequestMethod);
        Assert.Equal("/playlist/v2/playlist/p/changes", transport.LastRequestRoute);
    }

    [Fact]
    public async Task ScheduledDrain_IsAwaitedBeforeMutationReportsSuccess()
    {
        var transport = new StubTransport();
        var (store, mutations, source) = Create(transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ScheduleDrain = ct => release.Task.WaitAsync(ct);

        var save = source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken);

        Assert.False(save.IsCompleted);
        Assert.Equal("Renamed", store.GetPlaylist(PlaylistUri)!.Name); // optimistic header is already live
        await mutations.Drain(transport, Ctx, TestContext.Current.CancellationToken);
        release.SetResult();
        await save;
        Assert.Equal(0, mutations.Pending);
    }

    // -- The optimistic half is publishable BEFORE the network half (the "the row appears seconds later" report) -------
    // The page's live refresh is driven by IStore.Changes. If the optimistic apply did not BUMP before the drain, no
    // amount of fixing the page could make an edit appear within a frame - so pin the ordering at the seam, with the
    // POST held open for the whole assertion window.

    /// <summary>An ITransport whose every request parks on a gate - "the server has not answered yet".</summary>
    sealed class GatedTransport : ITransport
    {
        readonly Task _gate;
        public readonly List<string> Routes = new();
        public GatedTransport(Task gate) => _gate = gate;

        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            lock (Routes) Routes.Add(route);
            return Wait();
            async Task<Resp> Wait()
            {
                await _gate.ConfigureAwait(false);
                return new Resp(true, Array.Empty<byte>(), 200);
            }
        }

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static (List<StoreChange> Seen, IDisposable Sub) Watch(InMemoryStore store)
    {
        var seen = new List<StoreChange>();
        var sub = store.Changes.Subscribe(Observers.From<StoreChange>(c => { lock (seen) seen.Add(c); }));
        return (seen, sub);
    }

    static bool Saw(List<StoreChange> seen, string uri)
    {
        lock (seen) return seen.Exists(c => c.Uri == uri);
    }

    [Fact]
    public async Task OptimisticInsert_BumpsTheStore_BeforeTheDrainIsAnswered()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (store, mutations, source) = Create(new GatedTransport(gate.Task));
        var (seen, sub) = Watch(store);
        using var _ = sub;
        var track = new Track("t", "spotify:track:t", "Recommended", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 123_000, false, null);

        var write = source.InsertTracksAsync(PlaylistUri, new[] { track }, toIndex: 0);

        // The POST is still in flight...
        Assert.False(write.IsCompleted);
        // ...and the read model the open page joins is ALREADY complete: the entity is resident (so JoinMembership
        // cannot drop the row), the membership carries it, and the store has ALREADY SAID SO on Changes. Everything the
        // page needs to paint the new row exists here, one synchronous call after the drop.
        Assert.NotNull(store.GetTrack(track.Uri));
        Assert.Equal(track.Uri, Assert.Single(store.Membership(PlaylistUri)).ItemUri);
        Assert.True(Saw(seen, PlaylistUri), "the optimistic membership write must publish a StoreChange before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task OptimisticMove_BumpsBeforeTheDrain_AndKeepsEveryRowIdentity()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (store, mutations, source) = Create(new GatedTransport(gate.Task));
        var seedRows = new[]
        {
            new PlaylistMember("aaaaaaaaaaaaaa01", "spotify:track:a", "alice", 1),
            new PlaylistMember("aaaaaaaaaaaaaa02", "spotify:track:b", "alice", 2),
            new PlaylistMember("aaaaaaaaaaaaaa03", "spotify:track:c", "alice", 3),
        };
        store.SetMembership(PlaylistUri, seedRows, new byte[] { 1 });
        var (seen, sub) = Watch(store);
        using var _ = sub;

        // Drag row 0 to the end (the pre-move insertion convention: "insert before the row currently at index 3").
        var write = source.MoveRowsAsync(PlaylistUri, new[] { new PlaylistRowRef(0, "spotify:track:a", "aaaaaaaaaaaaaa01") }, toIndex: 3);

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
        Assert.True(Saw(seen, PlaylistUri), "the optimistic move must publish a StoreChange before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task OptimisticRemove_BumpsBeforeTheDrain()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (store, mutations, source) = Create(new GatedTransport(gate.Task));
        store.SetMembership(PlaylistUri, new[]
        {
            new PlaylistMember("aaaaaaaaaaaaaa01", "spotify:track:a", "alice", 1),
            new PlaylistMember("aaaaaaaaaaaaaa02", "spotify:track:b", "alice", 2),
        }, new byte[] { 1 });
        var (seen, sub) = Watch(store);
        using var _ = sub;

        var write = source.RemoveRowsAsync(PlaylistUri, new[] { new PlaylistRowRef(0, "spotify:track:a", "aaaaaaaaaaaaaa01") });

        Assert.False(write.IsCompleted, write.IsFaulted ? write.Exception!.ToString() : "the write completed without reaching the wire");
        Assert.Equal("aaaaaaaaaaaaaa02", Assert.Single(store.Membership(PlaylistUri)).ItemId);
        Assert.True(Saw(seen, PlaylistUri), "the optimistic remove must publish a StoreChange before the drain");

        gate.SetResult();
        await write;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task FailedServerAttempt_DoesNotReportConfirmedSuccess()
    {
        var (_, mutations, source) = Create(new FailingTransport());
        // P1: the ONE failure type the seam surfaces. A write that is still queued after its drain is Pending — never a
        // bare InvalidOperationException whose message the UI would have to sniff.
        var error = await Assert.ThrowsAsync<PlaylistMutationException>(() =>
            source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(PlaylistMutationFailure.Pending, error.Kind);
        Assert.Equal(1, mutations.Pending); // durable retry remains queued
    }
}
