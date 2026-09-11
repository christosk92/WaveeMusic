using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The outbound DeviceStatePublisher: NewConnection announce on the connection-id + local player_state on playback changes,
// with stable session/playback ids + dedup. Proto-building is delegated (here a string encoding for assertions).
public class ConnectPublisherTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    static ClusterDelta ContextCluster(string contextUri) =>
        new("us", false, default, contextUri, false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    sealed class Harness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj = new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        public readonly SimpleSubject<string?> ConnId = new(null);
        public string? CurrentConnId;
        public readonly List<string> Built = new();
        public LocalPlaybackSnapshot? LastSnapshot;
        public readonly DeviceStatePublisher Publisher;

        public Harness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, Proj.Ownership, ConnId, () => CurrentConnId,
                (reason, snap, mid, active) =>
                {
                    LastSnapshot = snap;
                    var s = reason + "|" + active + "|" + (snap?.Track.Uri ?? "-") + "|" + (snap?.SessionId ?? "");
                    Built.Add(s);
                    return Encoding.UTF8.GetBytes(s);
                },
                onCluster: null, clock: () => 1000);
        }

        public void Connect(string id) { CurrentConnId = id; ConnId.OnNext(id); }
        // proj + publisher both see the event (the controller fans to both in production) — and, since a real local
        // play/resume always claims ownership BEFORE the controller emits its event, so does this harness (the ONE
        // authority behind is_active now; nothing here answers "am I active" off a bare CurrentTrack any more).
        public void Play(string trackUri, EvKind kind = EvKind.Started)
        {
            Proj.Ownership.Claim(ClaimCause.UserPlay);
            var e = new PlaybackEvent(kind, T(trackUri), 0);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }

        // Emit a state event for the CURRENT track (mirrors the controller's EmitState).
        public void Emit(EvKind kind, long atMs = 0)
        {
            var e = new PlaybackEvent(kind, Proj.CurrentTrack, atMs);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }
        public void SetOptions(bool shuffle, RepeatMode repeat) => Proj.SetLocalOptions(shuffle, repeat);
        public void SetVolume(double v) => Proj.SetLocalVolume(v);
        public void SetQueue(params QueueEntry[] q) => Proj.SetLocalQueue(q);
    }

    sealed class BlockingTransport : ITransport
    {
        readonly SimpleSubject<WireEvent> _events = new();
        readonly SimpleSubject<WireRequest> _requests = new();
        readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _calls;
        int _inflight;
        int _maxInflight;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxInflight => Volatile.Read(ref _maxInflight);
        public Task FirstEntered => _firstEntered.Task;
        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(true, [], 200));
        public IObservable<WireEvent> Events(string topicPrefix) => _events;
        public IObservable<WireRequest> Requests(string identPrefix) => _requests;
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;

        public async Task<Resp> Publish(
            string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
        {
            int call = Interlocked.Increment(ref _calls);
            int inflight = Interlocked.Increment(ref _inflight);
            int observed;
            while (inflight > (observed = Volatile.Read(ref _maxInflight)))
                if (Interlocked.CompareExchange(ref _maxInflight, inflight, observed) == observed) break;
            try
            {
                if (call == 1)
                {
                    _firstEntered.TrySetResult();
                    await _releaseFirst.Task.WaitAsync(ct);
                }
                return new Resp(true, [], 200);
            }
            finally { Interlocked.Decrement(ref _inflight); }
        }
    }

    [Fact]
    public async Task OnConnectionId_AnnouncesNewConnection()
    {
        var h = new Harness();
        h.Connect("c1");
        await Task.Delay(20);
        Assert.Equal(1, h.Transport.PublishCount);
        Assert.StartsWith("NewConnection|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    // ── bug 2: is_active must not answer true off a bare "CurrentTrack is not null" — that is also what a passive
    // VIEWER's mirrored fold of another device's row looks like. Structurally guaranteed now (not merely accidental):
    // folding a cluster naming a foreign device transitions the ownership authority to Foreign (F3), and
    // PlaybackOwnership.IsActiveOnWire is false for anything but Us — a Wavee that never played can no longer announce
    // isActive=true (nor a player_state built from the phone's track) the moment a dealer connection lands.
    [Fact]
    public async Task NewConnection_ViewerEcho_PublishesInactive_NeverClaimsOwnership()
    {
        var h = new Harness();
        var remote = new RemoteTrack("spotify:track:remote", "Title", "Artist", "spotify:artist:a",
            "Album", "spotify:album:al", null, 200_000);
        h.Proj.OnCluster(new ClusterDelta("phone", true, remote, "spotify:playlist:p",
            true, false, false, 5_000, 0, 0, remote.DurationMs, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        h.Connect("c1");
        await Task.Delay(20);

        Assert.Equal(1, h.Transport.PublishCount);
        Assert.StartsWith("NewConnection|False|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task BeforeConnectionId_DoesNotPublish()
    {
        var h = new Harness();
        h.Play("spotify:track:a");   // no connection id yet → can't PUT
        await Task.Delay(20);
        Assert.Equal(0, h.Transport.PublishCount);
    }

    [Fact]
    public async Task Publishes_AreSerialized_InMessageOrder()
    {
        var transport = new BlockingTransport();
        var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var connection = new SimpleSubject<string?>(null);
        string? currentConnection = null;
        using var publisher = new DeviceStatePublisher(
            transport, "us", projection, projection.Ownership, connection, () => currentConnection,
            (reason, _, mid, _) => Encoding.UTF8.GetBytes(reason + "|" + mid));

        currentConnection = "c1";
        connection.OnNext("c1");
        await transport.FirstEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var started = new PlaybackEvent(EvKind.Started, T("spotify:track:a"), 0);
        projection.OnEvent(started);
        publisher.OnEvent(started);
        await Task.Delay(40);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.MaxInflight);

        transport.ReleaseFirst();
        await WaitUntilAsync(() => transport.Calls == 2);
        Assert.Equal(1, transport.MaxInflight);
    }

    [Fact]
    public async Task LocalPlay_PublishesPlayerStateChanged_Active()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a");
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);   // NewConnection + PlayerStateChanged
        Assert.Contains("PlayerStateChanged|True|spotify:track:a", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task DedupsIdenticalPlayerState()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a", EvKind.Started);
        h.Play("spotify:track:a", EvKind.Resumed);   // same salient state → deduped
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);    // NewConnection + one PlayerStateChanged
    }

    [Fact]
    public async Task NewContext_MintsDifferentSessionId()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Proj.OnCluster(ContextCluster("spotify:playlist:A"));
        h.Play("spotify:track:a");
        h.Proj.OnCluster(ContextCluster("spotify:playlist:B"));
        h.Play("spotify:track:b");
        await Task.Delay(20);

        var sessions = h.Built.FindAll(b => b.StartsWith("PlayerStateChanged")).ConvertAll(b => b.Split('|')[3]);
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0], sessions[1]);   // different context → different session id
    }

    // ── Phase C: PutState now publishes on EVERY salient local change (not just track boundaries) ─────────────────────
    [Fact]
    public async Task Pause_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.Paused);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // NewConnection + Started + Paused
    }

    [Fact]
    public async Task InitiallyPausedTransfer_MintsPlaybackIds()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a", EvKind.Paused);
        await Task.Delay(20);

        var snapshot = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.False(string.IsNullOrEmpty(snapshot.SessionId));
        Assert.False(string.IsNullOrEmpty(snapshot.PlaybackId));
        Assert.True(snapshot.IsPaused);
    }

    [Fact]
    public async Task Seek_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.Seeked, 5000);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // position jumped → not deduped
    }

    [Fact]
    public async Task OptionsChange_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetOptions(true, RepeatMode.Context); h.Emit(EvKind.OptionsChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // shuffle/repeat changed → not deduped
    }

    [Fact]
    public async Task VolumeChange_Publishes_WithVolumeChangedReason()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetVolume(0.25); h.Emit(EvKind.VolumeChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);
        Assert.StartsWith("VolumeChanged|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    [Fact]
    public async Task QueueChange_Publishes()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a");
        h.SetQueue(new QueueEntry(QueueItemId.None, "now", T("spotify:track:a"), QueueBucket.NowPlaying, QueueProvider.Context, false, "u0"),
                   new QueueEntry(QueueItemId.None, "q0", T("spotify:track:q"), QueueBucket.UserQueue, QueueProvider.Queue, false, "uq"));
        h.Emit(EvKind.QueueChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);   // up-next changed → not deduped
    }

    [Fact]
    public async Task QueueSnapshot_CapsWireTracks_AndPublishesHistoryAsPrevTracks()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:now");
        var queue = new List<QueueEntry>();
        for (int i = 0; i < 55; i++)
            queue.Add(new QueueEntry(QueueItemId.None, "h" + i, T("spotify:track:h" + i), QueueBucket.History, QueueProvider.Context, false, "uh" + i));
        queue.Add(new QueueEntry(QueueItemId.None, "now", T("spotify:track:now"), QueueBucket.NowPlaying, QueueProvider.Context, false, "unow"));
        for (int i = 0; i < 55; i++)
            queue.Add(new QueueEntry(QueueItemId.None, "n" + i, T("spotify:track:n" + i), QueueBucket.NextUp, QueueProvider.Context, false, "un" + i));

        h.SetQueue(queue.ToArray());
        h.Emit(EvKind.QueueChanged);
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        // Local history IS published as prev_tracks (playback-restore fix §2) — it's what a later cold-start cluster
        // hands back for History recovery. Capped to the newest 50 like next_tracks.
        Assert.Equal(50, snap.PrevTracks.Count);
        Assert.Equal("spotify:track:h5", snap.PrevTracks[0].Uri);     // oldest kept after the cap
        Assert.Equal("spotify:track:h54", snap.PrevTracks[49].Uri);   // newest last
        Assert.Equal(50, snap.NextTracks.Count);
        Assert.Equal("spotify:track:n0", snap.NextTracks[0].Uri);
        Assert.Equal("spotify:track:n49", snap.NextTracks[49].Uri);
    }

    [Fact]
    public async Task BecameInactive_Publishes_IsActiveFalse()
    {
        var h = new Harness();
        h.Connect("c1"); h.Play("spotify:track:a"); h.Emit(EvKind.BecameInactive);
        await Task.Delay(20);
        Assert.StartsWith("BecameInactive|False|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    // ── bug 6: the change-gate key must fold the live media kind — an audio↔video toggle on the SAME track, same
    // wall-second, with an EMPTY up-next queue (NextSig too returns its "0" constant either way) must not be
    // swallowed by the steady-state dedup, or the wire never learns the host flipped.
    [Fact]
    public async Task MediaKindToggle_WithEmptyQueue_StillPublishes()
    {
        var h = new Harness();
        h.Connect("c1");
        var kind = PlayableKind.Audio;
        h.Publisher.CurrentMediaKind = () => kind;

        h.Play("spotify:track:a");   // NewConnection + PlayerStateChanged(Audio)
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);

        kind = PlayableKind.Video;   // same track/position/shuffle/repeat/queue — ONLY the media kind changed
        h.Emit(EvKind.OptionsChanged);
        await Task.Delay(20);
        Assert.Equal(3, h.Transport.PublishCount);
    }

    [Fact]
    public async Task NoOpRepeatOfSameState_StaysDeduped()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a", EvKind.Started);
        h.Emit(EvKind.OptionsChanged);   // options unchanged (default) + same track/pos → identical key
        await Task.Delay(20);
        Assert.Equal(2, h.Transport.PublishCount);   // NewConnection + the one Started; the no-op OptionsChanged collapses
    }

    // ── Step C / contract item 1-2: is_active is derived from ConnectOwnership, not a local transport fact ─────────────

    // Replaces the old PublishInactive()-based "retired" test in DeviceStateRepublishTests.cs: a badge republish must
    // stay muted for as long as the OWNERSHIP AUTHORITY says we are not the owner — not merely for as long as somebody
    // remembered to call the low-level PublishInactive() primitive.
    [Fact]
    public async Task BadgeRepublish_WhileNotUs_PublishesNothing()
    {
        var h = new Harness();
        h.Connect("c1");
        h.Play("spotify:track:a");
        await Task.Delay(20);

        h.Proj.Ownership.Release(ReleaseCause.EndOfContext);   // ownership given up — no longer Us
        await Task.Delay(20);
        int before = h.Transport.PublishCount;

        h.Publisher.PublishStateChanged();
        await Task.Delay(20);

        Assert.Equal(before, h.Transport.PublishCount);   // a badge landing while not the owner must never claim the wire
    }

    // ── bug 5 (launch slot steal): a session-recovery seed sets CurrentTrack WITHOUT ever claiming ownership (no user
    // or inbound intent asked for it) — the exact precondition the 15:21:32 "SLOT STEAL" traced back to
    // (RecomputeHasVideo → PublishStateChanged → literal is_active=true). A fresh NewConnection announce over that seed
    // must still say inactive.
    [Fact]
    public async Task NewConnection_AfterRecoverySeed_IsInactive()
    {
        var h = new Harness();
        h.Proj.OnEvent(new PlaybackEvent(EvKind.Started, T("spotify:track:a"), 0));   // the projection ONLY — no claim

        h.Connect("c1");
        await Task.Delay(20);

        Assert.Equal(1, h.Transport.PublishCount);
        Assert.StartsWith("NewConnection|False|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    // ── contract item 2: started_playing_at is the CLAIM's stamp (fresh per claim), and has_been_playing_for_ms — long
    // computed, never sent — now reaches the wire.
    [Fact]
    public async Task Claim_StampsStartedAt_PerClaim_AndWritesHasBeenPlayingFor()
    {
        long now = 10_000;
        var transport = new StubTransport();
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(),
            clock: () => now, serverNowUnixMs: () => now);
        var connId = new SimpleSubject<string?>(null);
        string? currentConnId = null;
        LocalPlaybackSnapshot? lastSnapshot = null;
        using var publisher = new DeviceStatePublisher(transport, "us", proj, proj.Ownership, connId, () => currentConnId,
            (reason, snap, mid, active) => { lastSnapshot = snap; return Encoding.UTF8.GetBytes(reason + "|" + active); },
            onCluster: null, clock: () => now);

        currentConnId = "c1"; connId.OnNext("c1");

        proj.Ownership.Claim(ClaimCause.UserPlay);
        var e1 = new PlaybackEvent(EvKind.Started, T("spotify:track:a"), 0);
        proj.OnEvent(e1);
        publisher.OnEvent(e1);
        await Task.Delay(20);

        var snap1 = Assert.IsType<LocalPlaybackSnapshot>(lastSnapshot);
        Assert.Equal(10_000, snap1.StartedPlayingAtMs);
        Assert.Equal(0, snap1.HasBeenPlayingForMs);

        now += 5_000;
        var e2 = new PlaybackEvent(EvKind.Seeked, T("spotify:track:a"), 3000);
        proj.OnEvent(e2);
        publisher.OnEvent(e2);
        await Task.Delay(20);

        var snap2 = Assert.IsType<LocalPlaybackSnapshot>(lastSnapshot);
        Assert.Equal(10_000, snap2.StartedPlayingAtMs);      // same claim → the stamp does not move
        Assert.Equal(5_000, snap2.HasBeenPlayingForMs);      // …but has_been_playing_for_ms now reaches the wire

        now += 1_000;
        proj.Ownership.Claim(ClaimCause.UserPlay);           // a fresh user-play (e.g. a skip) restamps per claim
        var e3 = new PlaybackEvent(EvKind.TrackChanged, T("spotify:track:b"), 0);
        proj.OnEvent(e3);
        publisher.OnEvent(e3);
        await Task.Delay(20);

        var snap3 = Assert.IsType<LocalPlaybackSnapshot>(lastSnapshot);
        Assert.Equal(16_000, snap3.StartedPlayingAtMs);
        Assert.Equal(0, snap3.HasBeenPlayingForMs);
    }

    // ── contract item 4/PlaybackOwnership P1/P3: the put-state RESPONSE is the verdict on a claim, folded with
    // Origin=PutResponse and the claim's own msgId (bound by OnPutSent inside PublishAsync).
    [Fact]
    public async Task PutResponse_BindsTheClaim_AdoptedOrRejected()
    {
        // Adopted: the response names us.
        {
            var h = new Harness();
            h.Connect("c1");
            h.Play("spotify:track:a");
            await Task.Delay(20);
            uint msgId = h.Proj.Ownership.Current.ClaimMsgId;
            Assert.NotEqual(0u, msgId);

            h.Proj.Ownership.OnCluster(new ClusterFrame(ClusterOrigin.PutResponse, msgId, "us", 500));

            Assert.Equal(OwnerKind.Us, h.Proj.Ownership.Current.Kind);
            Assert.Equal(ClaimPhase.Adopted, h.Proj.Ownership.Current.Claim);
        }
        // Rejected: the response names another device — the claim never held the slot.
        {
            var h = new Harness();
            h.Connect("c1");
            h.Play("spotify:track:a");
            await Task.Delay(20);
            uint msgId = h.Proj.Ownership.Current.ClaimMsgId;
            Assert.NotEqual(0u, msgId);

            h.Proj.Ownership.OnCluster(new ClusterFrame(ClusterOrigin.PutResponse, msgId, "phone", 500));

            Assert.Equal(OwnerKind.Foreign, h.Proj.Ownership.Current.Kind);
            Assert.Equal("phone", h.Proj.Ownership.Current.DeviceId);
        }
    }

    // ── contract item 2 / librespot: while we are NOT the active device the PUT carries an IDLE player_state (no
    // mirrored foreign row) plus our own device volume, supplied SEPARATELY from the (null) snapshot.
    [Fact]
    public async Task NonUs_PublishesAnIdlePlayerState_WithOurVolume()
    {
        var transport = new StubTransport();
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0,
            initialVolume01: 0.42);
        var connId = new SimpleSubject<string?>(null);
        string? currentConnId = null;
        LocalPlaybackSnapshot? lastSnapshot = null;
        double lastOwnVolume = -1;
        bool lastIsActive = true;
        using var publisher = new DeviceStatePublisher(transport, "us", proj, proj.Ownership, connId, () => currentConnId,
            (reason, snap, mid, active, ownVolume, attribution) =>
            {
                lastSnapshot = snap; lastOwnVolume = ownVolume; lastIsActive = active;
                return Encoding.UTF8.GetBytes(reason + "|" + active);
            },
            onCluster: null, clock: () => 1000);

        // A foreign device is active — we fold it as a passive viewer, never claiming ownership.
        proj.OnCluster(new ClusterDelta("phone", true,
            new RemoteTrack("spotify:track:remote", "Title", "Artist", "spotify:artist:a", "Album", "spotify:album:al", null, 200_000),
            "spotify:playlist:p", true, false, false, 5_000, 0, 100, 200_000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        currentConnId = "c1"; connId.OnNext("c1");
        await Task.Delay(20);

        Assert.False(lastIsActive);
        Assert.Null(lastSnapshot);
        Assert.Equal(0.42, lastOwnVolume, 3);
    }

    // ── contract item 1: a lost/rejected claim PUT settles Unadopted (keep playing; the next announce/reconnect
    // re-asserts is_active) rather than leaving the claim stuck Protected forever.
    [Fact]
    public async Task PutFailed_SettlesTheClaimUnadopted()
    {
        var transport = new FailingTransport();
        var proj = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var connId = new SimpleSubject<string?>(null);
        string? currentConnId = "c1";
        using var publisher = new DeviceStatePublisher(transport, "us", proj, proj.Ownership, connId, () => currentConnId,
            (reason, snap, mid, active) => Encoding.UTF8.GetBytes(reason + "|" + active),
            onCluster: null, clock: () => 1000);
        connId.OnNext("c1");
        await Task.Delay(20);

        proj.Ownership.Claim(ClaimCause.UserPlay);
        var e = new PlaybackEvent(EvKind.Started, T("spotify:track:a"), 0);
        proj.OnEvent(e);
        publisher.OnEvent(e);
        await Task.Delay(20);

        Assert.Equal(OwnerKind.Us, proj.Ownership.Current.Kind);
        Assert.Equal(ClaimPhase.Unadopted, proj.Ownership.Current.Claim);
    }

    // A transport whose every PUT is rejected — StubTransport has no such hook (Publish always answers 200 Ok).
    sealed class FailingTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(false, Array.Empty<byte>(), 500));
    }
}
