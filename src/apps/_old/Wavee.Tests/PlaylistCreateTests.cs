using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// ── P3: create-via-/changes ──────────────────────────────────────────────────────────────────────────────────────────
// A playlist is created by POSTing a /changes body to a CLIENT-MINTED id against the fixed 8-byte create base — there is
// no create endpoint any more. What this pins:
//   (a) the body is byte-identical to desktop's (golden a031),
//   (b) the store row exists BEFORE any network call (the UI navigates on it),
//   (c) the network is three ORDERED durable ops on one entity key: create → rootlist ADD → seed tracks,
//   (d) a rootlist ADD that fails transiently stays queued (no orphan playlist), while a 4xx create dead-letters,
//       rolls the whole optimistic row back, drops the rest of the recipe, and FAULTS the create completion.
public class PlaylistCreateTests
{
    static readonly SessionContext Ctx = new("bob", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Records every request and scripts the response by (route, method, body, 1-based call#).
    sealed class RecTransport(Func<string, string, byte[], int, Resp> respond) : ITransport
    {
        public readonly List<(string Route, string Method, byte[] Body, IReadOnlyDictionary<string, string>? Headers)> Sent = new();
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var m = method ?? (body.IsEmpty ? "GET" : "POST");
            var b = body.ToArray();
            Sent.Add((route, m, b, headers is null ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(respond(route, m, b, Sent.Count));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static byte[] Rev24(byte tag) { var r = new byte[24]; r[3] = tag; r[23] = tag; return r; }
    static byte[] SlcRev(byte[] rev) => new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) }.ToByteArray();
    static bool IsCreate(string route, byte[] body)
        => route.StartsWith("/playlist/v2/playlist/", StringComparison.Ordinal)
        && Pl.ListChanges.Parser.ParseFrom(body).BaseRevision.Length == 8;
    static bool IsRootlist(string route) => route.EndsWith("/rootlist/changes", StringComparison.Ordinal);

    static (InMemoryStore Store, MutationEngine Engine, PlaylistMutationSource Source) Harness(ITransport transport)
    {
        var store = new InMemoryStore();
        store.SetRootlist(Array.Empty<RootlistEntry>(), Rev24(9));   // a stored rev → the rootlist op needs no bootstrap
        var lane = new RootlistLane();
        var resync = new PlaylistResyncQueue();
        var engine = new MutationEngine(store, new IMutationStrategy[]
        {
            new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com", resync),
            new CreatePlaylistStrategy(store, () => "https://spclient.wg.spotify.com", resync),
            new RootlistFollowStrategy(store, lane),
        });
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        var source = new PlaylistMutationSource(engine, transport, http, () => Ctx,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), lane, store)
        {
            // The seam kicks a detached drain; tests drive the drain themselves so the assertions are deterministic.
            ScheduleDrain = _ => Task.CompletedTask,
        };
        return (store, engine, source);
    }

    // ── 1. the wire ──────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CreateChanges_GoldenA031_ByteExact()
    {
        var captured = Golden.Bytes("a031-create-p1");
        var changes = Golden.Changes("a031-create-p1");
        var delta = Assert.Single(changes.Deltas);
        string name = Assert.Single(delta.Ops).UpdateListAttributes.NewAttributes.Values.Name;

        var rebuilt = PlaylistWireMapper.BuildCreateChanges(name, delta.Info.User, delta.Info.Timestamp, nonce: 1);

        Assert.Equal(captured, rebuilt);
        // and the base really is the 8-byte create base, never a 24-byte head (I1)
        Assert.Equal(PlaylistRevisions.NewCreateBase(), Pl.ListChanges.Parser.ParseFrom(rebuilt).BaseRevision.ToByteArray());
        Assert.False(PlaylistRevisions.IsWellFormed(PlaylistRevisions.NewCreateBase()));
    }

    [Fact]
    public void CreateHeaders_CarryTheCreateSyncReason()
    {
        var headers = SpotifyHeaders.PlaylistV2Create("en", "https://spclient.wg.spotify.com");
        Assert.Equal("CAw=", headers["spotify-playlist-sync-reason"]);                 // 12 = create (edit is CAk= / 9)
        Assert.Equal("application/x-www-form-urlencoded", headers["Content-Type"]);
        Assert.Equal("CAk=", SpotifyHeaders.PlaylistV2Mutation("en")["spotify-playlist-sync-reason"]);
    }

    // ── 2. the optimistic row is complete before anything is sent ────────────────────────────────────────────────────
    [Fact]
    public void Create_OptimisticRowVisibleImmediately()
    {
        var t = new RecTransport((_, _, _, _) => new Resp(true, Array.Empty<byte>(), 200));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);

        Assert.Empty(t.Sent);                                        // nothing on the wire yet
        Assert.StartsWith("spotify:playlist:", created.Uri, StringComparison.Ordinal);
        var header = Assert.IsType<Playlist>(store.GetPlaylist(created.Uri));
        Assert.Equal("Road trip", header.Name);
        Assert.Equal("bob", header.Owner!.Id);
        Assert.True(header.Capabilities is { IsOwner: true, CanEditItems: true, CanEditMetadata: true, CanAdministratePermissions: true });
        Assert.True(header.IsPublic);
        Assert.True(store.HasMembership(created.Uri));
        Assert.Empty(store.Membership(created.Uri));
        Assert.Equal(created.Uri, Assert.Single(store.Rootlist()).Uri);
        Assert.True(store.IsSaved("playlists", created.Uri));
        Assert.False(created.Completion.IsCompleted);                // …but it is not real yet
        Assert.Equal(2, engine.PendingFor(created.Uri));             // create + the rootlist ADD
    }

    [Fact]
    public async Task Create_PostsChangesToTheMintedId_ThenTheRootlistAdd()
    {
        var t = new RecTransport((route, _, _, _) => new Resp(true, IsRootlist(route) ? Array.Empty<byte>() : SlcRev(Rev24(1)), 200));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);
        await engine.Drain(t, Ctx, Ct);
        await created.Completion;

        string id = created.Uri["spotify:playlist:".Length..];
        Assert.Equal(2, t.Sent.Count);
        Assert.Equal($"/playlist/v2/playlist/{id}/changes", t.Sent[0].Route);
        Assert.Equal("POST", t.Sent[0].Method);
        Assert.Equal("CAw=", t.Sent[0].Headers!["spotify-playlist-sync-reason"]);
        var body = Pl.ListChanges.Parser.ParseFrom(t.Sent[0].Body);
        Assert.Equal(PlaylistRevisions.NewCreateBase(), body.BaseRevision.ToByteArray());
        Assert.Equal("Road trip", Assert.Single(Assert.Single(body.Deltas).Ops).UpdateListAttributes.NewAttributes.Values.Name);

        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", t.Sent[1].Route);
        var add = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.Add, add.Kind);
        Assert.Equal(0, add.Add.FromIndex);
        var item = Assert.Single(add.Add.Items);                     // the a042 shape: uri + attrs{timestamp, public}
        Assert.Equal(created.Uri, item.Uri);
        Assert.True(item.Attributes.Timestamp > 0);
        Assert.True(item.Attributes is { HasPublic: true, Public: true });

        Assert.Equal(0, engine.PendingFor(created.Uri));
        Assert.True(store.IsSaved("playlists", created.Uri));
    }

    // ── 3. a rootlist ADD that fails transiently must not leave an orphan ────────────────────────────────────────────
    [Fact]
    public async Task Create_RootlistAddFails_StaysQueued_NoOrphan()
    {
        var t = new RecTransport((route, _, _, _) => IsRootlist(route)
            ? new Resp(false, Array.Empty<byte>(), 503)              // transient: the op is still valid
            : new Resp(true, SlcRev(Rev24(1)), 200));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);
        await engine.Drain(t, Ctx, Ct);
        await created.Completion;                                    // the CREATE itself landed

        Assert.Equal(1, engine.PendingFor(created.Uri));             // …and the rootlist ADD is still queued
        Assert.Empty(engine.DeadLetter);
        Assert.Equal(created.Uri, Assert.Single(store.Rootlist()).Uri);   // the optimistic row survives (no orphan)
        Assert.NotNull(store.GetPlaylist(created.Uri));
        Assert.True(store.IsSaved("playlists", created.Uri));
    }

    // ── 4. a 4xx create is terminal: rollback + the whole recipe is dropped + the completion faults ──────────────────
    [Fact]
    public async Task Create_4xx_DeadLettersAndRollsBack_CompletionFaults()
    {
        var t = new RecTransport((route, _, _, _) => IsRootlist(route)
            ? new Resp(true, Array.Empty<byte>(), 200)
            : new Resp(false, Array.Empty<byte>(), 400));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);
        await engine.Drain(t, Ctx, Ct);

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(() => created.Completion);
        Assert.Equal(PlaylistMutationFailure.Unknown, ex.Kind);
        Assert.Single(t.Sent);                                       // the rootlist ADD never went out
        Assert.Empty(store.Rootlist());
        Assert.False(store.IsSaved("playlists", created.Uri));
        Assert.Empty(store.Membership(created.Uri));
        Assert.Equal(0, engine.PendingFor(created.Uri));             // the queued rootlist ADD was dropped with it
        Assert.Equal(2, engine.DeadLetter.Count);                    // create + its orphaned rootlist ADD
    }

    // ── 5. placement: a create inside a folder lands right after that folder's start marker ──────────────────────────
    [Fact]
    public async Task Create_InFolder_PlacesAfterStartGroup()
    {
        var t = new RecTransport((route, _, _, _) => new Resp(true, IsRootlist(route) ? Array.Empty<byte>() : SlcRev(Rev24(1)), 200));
        var (store, engine, source) = Harness(t);
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[]
        {
            "spotify:playlist:before",
            "spotify:start-group:g1:Trips",
            "spotify:playlist:inside",
            "spotify:end-group:g1",
        }), Rev24(9));

        var created = source.CreatePlaylist("Road trip", new RootlistPlacement("g1"));

        // optimistic: directly after the start marker (index 2), i.e. the folder's first child
        Assert.Equal(created.Uri, store.Rootlist()[2].Uri);
        Assert.Equal(1, store.Rootlist()[2].Depth);

        await engine.Drain(t, Ctx, Ct);
        await created.Completion;

        var add = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas).Ops);
        Assert.Equal(2, add.Add.FromIndex);                          // resolved from the folder at REPLAY time
    }

    [Fact]
    public async Task Create_InFolder_FolderVanishedBeforeReplay_AddsAtTheTop()
    {
        var t = new RecTransport((route, _, _, _) => new Resp(true, IsRootlist(route) ? Array.Empty<byte>() : SlcRev(Rev24(1)), 200));
        var (store, engine, source) = Harness(t);
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { "spotify:start-group:g1:Trips", "spotify:end-group:g1" }), Rev24(9));

        var created = source.CreatePlaylist("Road trip", new RootlistPlacement("g1"));
        // someone deletes the folder while the create is queued
        store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(new[] { created.Uri }), Rev24(9));
        await engine.Drain(t, Ctx, Ct);
        await created.Completion;

        var add = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[1].Body).Deltas).Ops);
        Assert.Equal(0, add.Add.FromIndex);                          // top level, not a stale index
    }

    // ── 6. ordering: seed tracks can never reach the wire before the playlist exists ─────────────────────────────────
    [Fact]
    public async Task Create_ThenSeedTracks_OrderedInOutbox()
    {
        var t = new RecTransport((route, _, _, _) => new Resp(true, IsRootlist(route) ? Array.Empty<byte>() : SlcRev(Rev24(1)), 200));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);
        var track = new Track("t1", "spotify:track:t1", "Seed", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);
        source.ScheduleDrain = c => engine.Drain(t, Ctx, c);              // from here the seam's own drain is live
        await source.AddTracksAsync(created.Uri, new[] { track }, Ct);   // enqueues behind the create, then drains

        await created.Completion;
        Assert.Equal(3, t.Sent.Count);
        Assert.True(IsCreate(t.Sent[0].Route, t.Sent[0].Body));          // 1. create (8-byte base)
        Assert.True(IsRootlist(t.Sent[1].Route));                        // 2. rootlist ADD
        Assert.False(IsCreate(t.Sent[2].Route, t.Sent[2].Body));         // 3. the track ADD, against the 24-byte head
        var seed = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(t.Sent[2].Body).Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.Add, seed.Kind);
        Assert.Equal("spotify:track:t1", Assert.Single(seed.Add.Items).Uri);
        Assert.Equal(0, engine.PendingFor(created.Uri));
    }

    [Fact]
    public async Task Create_Failing_BlocksItsSeedTracksInTheSamePass()
    {
        // The create keeps failing transiently; nothing else on that entity may go out — a track ADD against a playlist
        // the server has never seen is a 404 storm, and a rootlist ADD would name a playlist that does not exist.
        var t = new RecTransport((route, _, _, _) => IsCreateRoute(route)
            ? new Resp(false, Array.Empty<byte>(), 503)
            : new Resp(true, Array.Empty<byte>(), 200));
        var (store, engine, source) = Harness(t);

        var created = source.CreatePlaylist("Road trip", default);
        var track = new Track("t1", "spotify:track:t1", "Seed", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);
        source.ScheduleDrain = c => engine.Drain(t, Ctx, c);
        var pending = await Assert.ThrowsAsync<PlaylistMutationException>(() => source.AddTracksAsync(created.Uri, new[] { track }, Ct));

        Assert.Equal(PlaylistMutationFailure.Pending, pending.Kind);
        Assert.Single(t.Sent);                                            // only the failed create attempt
        Assert.False(created.Completion.IsCompleted);
        Assert.Equal(3, engine.PendingFor(created.Uri));                  // create + rootlist ADD + the seed edit

        static bool IsCreateRoute(string route) => route.StartsWith("/playlist/v2/playlist/", StringComparison.Ordinal);
    }
}
