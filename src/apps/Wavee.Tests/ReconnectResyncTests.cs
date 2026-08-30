using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// Phase 6 (§6.2, §6.3) — the reconnect convergence pass: drain FIRST (local intent wins), then rootlist, then token-gated
// per-set deltas, then /diff for the open resident playlist; rate-limited per window. Plus the §6 hardening: post-write
// drains route through the loop (ScheduleDrain) instead of racing inbound from the caller's thread.
public class ReconnectResyncTests
{
    static SessionContext Ctx => new("bob", "US", "premium", "en", Tier.Premium, false);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static byte[] Rev(int counter, params byte[] hash)
    {
        var b = new byte[4 + hash.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, counter);
        hash.CopyTo(b, 4);
        return b;
    }

    // Records the interleaved order of mutation-transport writes and HTTP fetches in one shared sequence log.
    sealed class SeqTransport(List<string> seq) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            lock (seq) seq.Add("write:" + route);
            return Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    sealed class Rig : IAsyncDisposable
    {
        public readonly InMemoryStore Store = new();
        public readonly List<string> Seq = new();
        public readonly MutationEngine Mut;
        public readonly LibrarySync Sync;
        readonly CancellationTokenSource _cts = new();

        public Rig()
        {
            var http = new FakeExchange((req, _) =>
            {
                string tag = req.Url.Contains("/diff?") ? "diff"
                    : req.Url.Contains("/rootlist") ? "rootlist"
                    : req.Url.Contains("/collection/v2/") ? "collection"
                    : "playlist";
                lock (Seq) Seq.Add(tag);
                if (tag == "rootlist") return new HttpResp(200, new Dictionary<string, string>(),
                    new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev(1, 0x01)) }.ToByteArray());
                if (tag == "collection") return new HttpResp(200, new Dictionary<string, string>(),
                    new Col.PageResponse { SyncToken = "tok", NextPageToken = "" }.ToByteArray());
                if (tag == "diff") return new HttpResp(200, new Dictionary<string, string>(),
                    new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
                return new HttpResp(200, new Dictionary<string, string>(), Array.Empty<byte>());
            });
            Task Hydrate(IReadOnlyList<string> uris, CancellationToken c) => Task.CompletedTask;
            var pf = new PlaylistFetcher(http, () => "https://x", Store, Hydrate, () => "");
            var revs = new Dictionary<string, string?>();
            var cf = new CollectionFetcher(http, () => "https://x", () => "bob", Store,
                s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, Hydrate);
            Mut = new MutationEngine(Store, new IMutationStrategy[] { new SetReplayStrategy() });
            Sync = new LibrarySync(Store, pf, cf, Mut, new PlaylistResyncQueue(), new SeqTransport(Seq),
                () => Ctx, () => "bob", default, _cts.Token);
        }

        public async ValueTask DisposeAsync() { await Sync.DisposeAsync(); _cts.Cancel(); _cts.Dispose(); }
    }

    [Fact]
    public async Task ReconnectResync_OrderedPass_DrainRootlistDeltasOpenDiff()
    {
        await using var rig = new Rig();
        // a pending like queued during the gap + an open resident playlist with a revision.
        rig.Mut.Save("liked", "spotify:track:t1", true);
        var rows = new List<PlaylistMember> { new("i1", "spotify:track:t1", null, 0) };
        rig.Store.SetMembership("spotify:playlist:open", rows, Rev(1, 0xAA));
        rig.Sync.SetOpenContext("spotify:playlist:open");

        rig.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await rig.Sync.WaitForIdleAsync();

        // order: the write drains FIRST, then the rootlist, then the 5 set fetches, then the open playlist's /diff.
        List<string> seq;
        lock (rig.Seq) seq = new List<string>(rig.Seq);
        int write = seq.FindIndex(s => s.StartsWith("write:"));
        int root = seq.IndexOf("rootlist");
        int firstCol = seq.IndexOf("collection");
        int diff = seq.IndexOf("diff");
        Assert.True(write >= 0 && root > write && firstCol > root && diff > firstCol,
            "expected write < rootlist < deltas < diff, got: " + string.Join(",", seq));
        Assert.Equal(5, seq.FindAll(s => s == "collection").Count);
        Assert.Equal(0, rig.Mut.Pending);                    // the like reconciled
        Assert.Equal(1, rig.Sync.ReconnectResyncs);
        Assert.Equal(1, rig.Sync.DiffUpToDate);

        // rate limit: a second resync inside the window is dropped, not re-run.
        int seqLen = seq.Count;
        rig.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await rig.Sync.WaitForIdleAsync();
        lock (rig.Seq) Assert.Equal(seqLen, rig.Seq.Count);
        Assert.Equal(1, rig.Sync.ReconnectResyncs);
        Assert.Equal(1, rig.Sync.ReconnectResyncsRateLimited);

        // outside the window it runs again.
        rig.Sync.ResyncWindow = TimeSpan.Zero;
        rig.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await rig.Sync.WaitForIdleAsync();
        Assert.Equal(2, rig.Sync.ReconnectResyncs);
    }

    [Fact]
    public async Task ReconnectResync_ColdDirtyPlaylist_StaysLazy()
    {
        await using var rig = new Rig();
        // a push for a NON-resident playlist marked it dirty; reconnect must NOT fetch it (anti-herd).
        rig.Sync.Enqueue(new SyncCommand(SyncKind.PlaylistPush, "spotify:playlist:cold", ParentRev: Rev(1, 0x01), NewRev: Rev(2, 0x02),
            Ops: Array.Empty<PlaylistOp>()));
        await rig.Sync.WaitForIdleAsync();
        Assert.Equal(1, rig.Sync.PushMarkedDirty);

        rig.Sync.Enqueue(new SyncCommand(SyncKind.ReconnectResync));
        await rig.Sync.WaitForIdleAsync();
        lock (rig.Seq) Assert.DoesNotContain(rig.Seq, s => s is "diff" or "playlist");   // nothing playlist-shaped fetched
        Assert.Equal(1, rig.Sync.ReconnectResyncs);
    }

    [Fact]
    public async Task ScheduleDrain_RoutesPostWriteDrainThroughTheLoop()
    {
        await using var rig = new Rig();
        var src = new EngineMutationSource(rig.Store, rig.Mut, new SeqTransport(rig.Seq), () => Ctx);
        src.ScheduleDrain = () => rig.Sync.Enqueue(new SyncCommand(SyncKind.DrainWrites));

        await src.SetSavedAsync("spotify:track:t1", true, Ct);   // returns WITHOUT draining inline
        Assert.True(rig.Store.IsSaved("liked", "spotify:track:t1"));   // optimistic applied inline
        // the drain happens on the loop:
        await rig.Sync.WaitForIdleAsync();
        Assert.Equal(0, rig.Mut.Pending);
        lock (rig.Seq) Assert.Contains(rig.Seq, s => s.StartsWith("write:/collection/v2/write"));
    }
}
