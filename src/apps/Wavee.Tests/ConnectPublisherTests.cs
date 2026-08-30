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
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
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
        // proj + publisher both see the event (the controller fans to both in production)
        public void Play(string trackUri, EvKind kind = EvKind.Started)
        {
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

    // ── bug 2: OwnsSession() must not answer true off a bare "CurrentTrack is not null" — that is also what a passive
    // VIEWER's mirrored fold of another device's row looks like. Without the ActiveDeviceId check, a Wavee that never
    // played still announced isActive=true (and a player_state built from the phone's track) the moment a dealer
    // connection landed.
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
            transport, "us", projection, connection, () => currentConnection,
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
}
