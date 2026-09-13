using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// SpotifyFriendActivityService: the live presence feed. Seeds from init-friend-feed on each dealer connection id and
// applies payload-less dealer deltas per friend. HTTP + transport + clock are all faked; the JSON parse, the coalescing,
// the stale-drop generation guard, and the watchdog are exercised for real. No network, no wall clock in logic paths.
public class FriendActivityServiceTests
{
    const string BaseUrl = "https://spclient.test";

    // ── Seed ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Seed_OnConnectionId_RowsSortedByTimestampDesc()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(
            req.Url.Contains("init-friend-feed")
                ? Ok(SeedJson(
                    Friend("spotify:user:alice", "Alice", 100, "spotify:track:1", "S1"),
                    Friend("spotify:user:bob", "Bob", 300, "spotify:track:2", "S2"),
                    Friend("spotify:user:carol", "Carol", 200, "spotify:track:3", "S3")))
                : Code(404)));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated);

        Assert.Equal(3, svc.Snapshot.Count);
        Assert.Equal(new[] { "spotify:user:bob", "spotify:user:carol", "spotify:user:alice" },
            new[] { svc.Snapshot[0].UserUri, svc.Snapshot[1].UserUri, svc.Snapshot[2].UserUri });
        Assert.Equal(300, svc.Snapshot[0].TimestampMs);
    }

    [Fact]
    public void Seed_EmptyFriends_SetsEmpty()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(Ok(SeedJson())));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Empty);

        Assert.Empty(svc.Snapshot);
        Assert.Equal(FriendFeedState.Empty, svc.State);
    }

    [Fact]
    public void Seed_404_SetsEmpty()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(Code(404)));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Empty);

        Assert.Empty(svc.Snapshot);
        Assert.Equal(FriendFeedState.Empty, svc.State);
    }

    [Fact]
    public void Seed_ConnectionId_IsUrlEscapedInRoute()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(Ok(SeedJson())));
        using var svc = Make(http, t, conn);

        const string raw = "cid /+=";
        conn.OnNext(raw);
        WaitUntil(() => http.SeedCalls == 1);

        var url = http.Urls[0];
        Assert.Equal(BaseUrl + "/presence-view/v2/init-friend-feed/" + Uri.EscapeDataString(raw), url);
        Assert.DoesNotContain(raw, url);   // the raw (space-bearing) id must never appear un-escaped in the route
    }

    // ── Deltas ───────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Delta_Upsert_ReplacesSameUserRow_AndReorders()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(
            req.Url.Contains("/presence-view/v1/user/")
                ? Ok(Friend("spotify:user:alice", "Alice", 300, "spotify:track:new", "Newer"))   // alice jumps to the top
                : Ok(SeedJson(
                    Friend("spotify:user:alice", "Alice", 100, "spotify:track:old", "Older"),
                    Friend("spotify:user:bob", "Bob", 200, "spotify:track:b", "B")))));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated && svc.Snapshot.Count == 2);
        Assert.Equal("spotify:user:bob", svc.Snapshot[0].UserUri);   // bob (200) leads before the upsert

        t.PushEvent(Presence("alice"));
        WaitUntil(() => svc.Snapshot.Count == 2 && svc.Snapshot[0].UserUri == "spotify:user:alice");

        Assert.Equal(2, svc.Snapshot.Count);                          // replaced, not duplicated
        Assert.Equal(300, svc.Snapshot[0].TimestampMs);
        Assert.Equal("Newer", svc.Snapshot[0].TrackName);
        Assert.Equal("spotify:user:bob", svc.Snapshot[1].UserUri);
    }

    [Fact]
    public void Delta_404or403_RemovesRow_LastRemovalGoesEmpty()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) =>
        {
            if (req.Url.EndsWith("/user/alice", StringComparison.Ordinal)) return Task.FromResult(Code(404));
            if (req.Url.EndsWith("/user/bob", StringComparison.Ordinal)) return Task.FromResult(Code(403));
            return Task.FromResult(Ok(SeedJson(
                Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A"),
                Friend("spotify:user:bob", "Bob", 200, "spotify:track:b", "B"))));
        });
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.Snapshot.Count == 2);

        t.PushEvent(Presence("alice"));   // 404 → drop alice, bob stays → still Populated
        WaitUntil(() => svc.Snapshot.Count == 1);
        Assert.Equal("spotify:user:bob", svc.Snapshot[0].UserUri);
        Assert.Equal(FriendFeedState.Populated, svc.State);

        t.PushEvent(Presence("bob"));     // 403 → drop the last row → Empty
        WaitUntil(() => svc.State == FriendFeedState.Empty);
        Assert.Empty(svc.Snapshot);
    }

    [Fact]
    public void Delta_PerUser_SecondPushCancelsInflightFetch()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var gate = new TaskCompletionSource<HttpResp>(TaskCreationOptions.RunContinuationsAsynchronously);
        int userCalls = 0;
        bool firstCancelled = false;

        var http = new ScriptExchange((req, ct) =>
        {
            if (req.Url.Contains("/presence-view/v1/user/"))
            {
                int n = Interlocked.Increment(ref userCalls);
                if (n == 1)
                {
                    ct.Register(() => { firstCancelled = true; gate.TrySetCanceled(); });   // coalescing cancels this token
                    return gate.Task;   // hold the first fetch open until it is cancelled
                }
                return Task.FromResult(Ok(Friend("spotify:user:alice", "Alice", 2, "spotify:track:new", "Newer")));
            }
            return Task.FromResult(Ok(SeedJson(Friend("spotify:user:alice", "Alice", 1, "spotify:track:old", "Older"))));
        });
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated);

        t.PushEvent(Presence("alice"));                 // fetch #1 → held open
        WaitUntil(() => userCalls == 1);
        t.PushEvent(Presence("alice"));                 // fetch #2 → cancels #1, applies "Newer"
        WaitUntil(() => firstCancelled && svc.Snapshot.Count == 1 && svc.Snapshot[0].TrackName == "Newer");

        Assert.True(firstCancelled, "the older in-flight fetch was not cancelled");
        Assert.Equal(2, userCalls);
        Assert.Single(svc.Snapshot);
        Assert.Equal("Newer", svc.Snapshot[0].TrackName);   // only the newer fetch's row survived
    }

    // ── Re-seed / connection-id lifecycle ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ConnectionId_New_ReSeeds_SameId_Deduped()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) =>
        {
            if (req.Url.EndsWith("conn-1", StringComparison.Ordinal))
                return Task.FromResult(Ok(SeedJson(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A"))));
            if (req.Url.EndsWith("conn-2", StringComparison.Ordinal))
                return Task.FromResult(Ok(SeedJson(Friend("spotify:user:carol", "Carol", 100, "spotify:track:c", "C"))));
            return Task.FromResult(Code(404));
        });
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.Snapshot.Count == 1 && svc.Snapshot[0].UserUri == "spotify:user:alice");
        Assert.Equal(1, http.SeedCalls);

        conn.OnNext("conn-1");                        // identical replayed id → deduped, no new seed
        Assert.Equal(1, http.SeedCalls);

        conn.OnNext("conn-2");                        // a genuinely new id → re-seed
        WaitUntil(() => svc.Snapshot.Count == 1 && svc.Snapshot[0].UserUri == "spotify:user:carol");
        Assert.Equal(2, http.SeedCalls);
    }

    [Fact]
    public void ConnectionId_Null_SetsOffline()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(
            Ok(SeedJson(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A")))));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated);

        conn.OnNext(null);                            // dealer disconnected
        Assert.Equal(FriendFeedState.Offline, svc.State);
    }

    [Fact]
    public void Seed_StaleResponse_DroppedWhenConnectionIdChangedMidFlight()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        // conn-1's seed is held open (and NOT tied to the cancellation token) so we exercise the stale-generation guard,
        // not the cancellation path: the response arrives late but must be dropped because conn-2 already superseded it.
        var gate1 = new TaskCompletionSource<HttpResp>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new ScriptExchange((req, _) =>
        {
            if (req.Url.EndsWith("conn-1", StringComparison.Ordinal)) return gate1.Task;
            if (req.Url.EndsWith("conn-2", StringComparison.Ordinal))
                return Task.FromResult(Ok(SeedJson(Friend("spotify:user:carol", "Carol", 200, "spotify:track:c", "C"))));
            return Task.FromResult(Code(404));
        });
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Loading);

        conn.OnNext("conn-2");
        WaitUntil(() => svc.Snapshot.Count == 1 && svc.Snapshot[0].UserUri == "spotify:user:carol");

        // conn-1's seed finally answers — with a DIFFERENT friend — but it is stale and must be ignored.
        gate1.SetResult(Ok(SeedJson(Friend("spotify:user:alice", "Alice", 999, "spotify:track:a", "A"))));
        Thread.Sleep(40);   // let the (async) stale-drop continuation run

        Assert.Single(svc.Snapshot);
        Assert.Equal("spotify:user:carol", svc.Snapshot[0].UserUri);   // conn-1's late alice never applied
    }

    // ── Watchdog ─────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Watchdog_ReSeeds_WhenNoRecentPush()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        long now = 1_000_000;
        var http = new ScriptExchange((req, _) =>
        {
            if (req.Url.Contains("/presence-view/v1/user/"))
                return Task.FromResult(Ok(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A")));
            return Task.FromResult(Ok(SeedJson(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A"))));
        });
        using var svc = Make(http, t, conn, () => Volatile.Read(ref now), TimeSpan.FromMilliseconds(40));

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated);
        Assert.Equal(1, http.SeedCalls);

        svc.SetActive(true);
        Volatile.Write(ref now, 1_000_000 + 40);   // push the logical clock past the staleness threshold; no push arrived
        WaitUntil(() => http.SeedCalls >= 2, 5000);
        svc.SetActive(false);

        Assert.True(http.SeedCalls >= 2);
    }

    [Fact]
    public void Watchdog_DoesNotReSeed_WhenAPushWasRecent()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        long now = 1_000_000;
        var http = new ScriptExchange((req, _) =>
        {
            if (req.Url.Contains("/presence-view/v1/user/"))
                return Task.FromResult(Ok(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A")));
            return Task.FromResult(Ok(SeedJson(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A"))));
        });
        using var svc = Make(http, t, conn, () => Volatile.Read(ref now), TimeSpan.FromMilliseconds(40));

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Populated);
        Assert.Equal(1, http.SeedCalls);

        Volatile.Write(ref now, 1_000_000 + 500);   // lots of logical time elapsed...
        t.PushEvent(Presence("alice"));             // ...but a push just landed, re-stamping _lastPushAtMs = clock()
        WaitUntil(() => http.UserCalls == 1);
        svc.SetActive(true);
        Thread.Sleep(250);                          // several watchdog periods
        svc.SetActive(false);

        Assert.Equal(1, http.SeedCalls);            // the recent push suppressed the watchdog re-seed
    }

    // ── Seed failure ─────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SeedFailure_WhenEmpty_SetsError()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(Code(500)));
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.State == FriendFeedState.Error);

        Assert.Equal(FriendFeedState.Error, svc.State);
        Assert.False(string.IsNullOrEmpty(svc.LastError));
        Assert.Empty(svc.Snapshot);
    }

    [Fact]
    public void SeedFailure_WithExistingRows_KeepsRows_StaysPopulated()
    {
        var t = new StubTransport();
        var conn = new SimpleSubject<string?>();
        var http = new ScriptExchange((req, _) => Task.FromResult(
            req.Url.EndsWith("conn-1", StringComparison.Ordinal)
                ? Ok(SeedJson(Friend("spotify:user:alice", "Alice", 100, "spotify:track:a", "A")))
                : Code(500)));   // any later seed (conn-2) fails
        using var svc = Make(http, t, conn);

        conn.OnNext("conn-1");
        WaitUntil(() => svc.Snapshot.Count == 1);

        conn.OnNext("conn-2");                       // re-seed fails 500 — must keep the stale rows
        WaitUntil(() => !string.IsNullOrEmpty(svc.LastError));

        Assert.Equal(FriendFeedState.Populated, svc.State);   // NOT Error — rows were present
        Assert.Single(svc.Snapshot);
        Assert.Equal("spotify:user:alice", svc.Snapshot[0].UserUri);
    }

    // ── Switchable / Null (Wavee.Core) ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Switchable_ReEmitsChanged_OnSetInner()
    {
        using var sw = new SwitchableFriendActivityService(new NullFriendActivityService());
        int changes = 0;
        using var sub = sw.Changed.Subscribe(ConnectHarness.Obs<int>(_ => Interlocked.Increment(ref changes)));

        sw.SetInner(new NullFriendActivityService());

        Assert.True(changes >= 1, "SetInner must bump Changed so a go-live/go-offline swap re-renders");
    }

    [Fact]
    public void Null_ReportsOffline()
    {
        var n = new NullFriendActivityService();
        Assert.Equal(FriendFeedState.Offline, n.State);
        Assert.Empty(n.Snapshot);
        Assert.Null(n.LastError);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────────────────────
    static SpotifyFriendActivityService Make(
        IHttpExchange http, StubTransport t, SimpleSubject<string?> conn,
        Func<long>? clock = null, TimeSpan? watchdog = null)
        => new(t, http, () => BaseUrl, conn, () => conn.Current, log: default, clock: clock, watchdogInterval: watchdog);

    static WireEvent Presence(string userId) => new("hm://presence2/user/" + userId, Array.Empty<byte>());

    static HttpResp Ok(string json) => new(200, Headers(), Encoding.UTF8.GetBytes(json));
    static HttpResp Code(int status) => new(status, Headers(), Array.Empty<byte>());
    static Dictionary<string, string> Headers() => new(StringComparer.OrdinalIgnoreCase);

    static string SeedJson(params string[] friends) => "{\"friends\":[" + string.Join(",", friends) + "]}";

    // One friend object exactly in the wire shape the service parses ({ timestamp, user{}, track{album,artist,context} }).
    static string Friend(string userUri, string userName, long ts, string trackUri, string trackName) =>
        "{\"timestamp\":" + ts +
        ",\"user\":{\"uri\":\"" + userUri + "\",\"name\":\"" + userName + "\",\"imageUrl\":\"https://img/" + userName + "\"}" +
        ",\"track\":{\"uri\":\"" + trackUri + "\",\"name\":\"" + trackName + "\",\"imageUrl\":\"https://img/t\"" +
        ",\"album\":{\"uri\":\"spotify:album:al\",\"name\":\"Album\"}" +
        ",\"artist\":{\"uri\":\"spotify:artist:ar\",\"name\":\"Artist\"}" +
        ",\"context\":{\"uri\":\"spotify:playlist:pl\",\"name\":\"Playlist\"}}}";

    // Bounded poll — the seed/delta work is fire-and-forget off the transport/connection callbacks. Sibling tests use small
    // Task.Delay waits; this spins on a predicate with a ceiling so a genuine failure surfaces as a clear assertion, fast.
    static void WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
        Assert.True(cond(), "condition not met within " + timeoutMs + "ms");
    }

    // A per-request scripted IHttpExchange (async — so a fetch can be held open via a TaskCompletionSource). Records every
    // requested URL so route shape / call counts are assertable. Grown from the CountingExchange in the profile-service tests.
    sealed class ScriptExchange : IHttpExchange
    {
        readonly Func<HttpReq, CancellationToken, Task<HttpResp>> _fn;
        readonly List<string> _urls = new();

        public ScriptExchange(Func<HttpReq, CancellationToken, Task<HttpResp>> fn) => _fn = fn;

        public string[] Urls { get { lock (_urls) return _urls.ToArray(); } }
        public int SeedCalls => CountContaining("/presence-view/v2/init-friend-feed/");
        public int UserCalls => CountContaining("/presence-view/v1/user/");

        int CountContaining(string frag)
        {
            lock (_urls)
            {
                int c = 0;
                foreach (var u in _urls) if (u.Contains(frag)) c++;
                return c;
            }
        }

        public Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            lock (_urls) _urls.Add(req.Url);
            return _fn(req, ct);
        }
    }
}
