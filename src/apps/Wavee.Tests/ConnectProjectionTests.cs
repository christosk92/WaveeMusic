using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage D — the cluster → IPlaybackState projection, reconciliation, and the device roster (proto-free, hand-built deltas).
// Reconciliation is the Connect OWNER's (Backend/PlaybackOwnership.cs, pinned row-by-row in PlaybackOwnershipTests): these
// pin what the display does under each owner — Us shows the local session + host, Foreign the cluster (local writers and
// host signals fold nothing), Nobody the local session unless a device just left.
public class ConnectProjectionTests
{
    static RemoteTrack Trk(string uri, string title, long dur) =>
        new(uri, title, "Artist", "spotify:artist:a", "Album", "spotify:album:al", "https://img/x", dur);

    static ClusterDelta Cluster(string active, bool playing, RemoteTrack? track = null, long pos = 0,
        IReadOnlyList<ConnectDeviceRow>? devices = null, long tsMs = 0, long serverTsMs = 0, double speed = 1.0) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, tsMs, serverTsMs, track?.DurationMs ?? 0, false, RepeatMode.Off,
            devices ?? Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(), PlaybackSpeed: speed);

    static Track Local(string uri, string title = "Local", long dur = 180_000) =>
        new(uri[(uri.LastIndexOf(':') + 1)..], uri, title,
            new[] { new ArtistRef("a", "spotify:artist:a", "A") }, new AlbumRef("al", "spotify:album:al", "Al"),
            dur, false, null);

    // The local session's snapshot, the shape the controller publishes through ApplyLocalSnapshot.
    static QueueSnapshot Snap(Track current) => new(
        Revision: 1, ContextUri: "spotify:playlist:local", AutoplayContextUri: null,
        Current: new QueueEntry(QueueItemId.None, "now", current, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
        History: ImmutableArray<QueueEntry>.Empty, UserQueue: ImmutableArray<QueueEntry>.Empty,
        Upcoming: ImmutableArray<QueueEntry>.Empty, Shuffle: false, Repeat: RepeatMode.Off,
        ClusterQueueRevision: "", ContextCursor: 0);

    // Counts publishes AFTER subscribing (SimpleSubject replays its last value to a new subscriber).
    sealed class ChangeCounter : IDisposable
    {
        readonly IDisposable _sub;
        public int Count;
        public bool? LastPlaying;
        public ChangeCounter(NowPlayingProjection p)
        {
            _sub = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(s => { Count++; LastPlaying = s.IsPlaying; }));
            Count = 0;
            LastPlaying = null;
        }
        public void Dispose() => _sub.Dispose();
    }

    [Fact]
    public void OnCluster_ViewerMode_FoldsTrackPlayStateContext_AndAnchorsPosition()
    {
        long now = 1000;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        int changes = 0;
        using var s = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => changes++));

        p.OnCluster(Cluster("other-device", playing: true, Trk("spotify:track:t1", "Song", 200000), pos: 5000));

        Assert.Equal("spotify:track:t1", p.CurrentTrack!.Uri);
        Assert.Equal("Song", p.CurrentTrack.Title);
        Assert.Equal("Artist", p.CurrentTrack.Artists[0].Name);
        Assert.True(p.IsPlaying);
        Assert.Equal("spotify:playlist:ctx", p.ContextUri);
        Assert.False(p.WeAreActive);
        Assert.True(changes >= 1);

        now = 3000;
        Assert.Equal(7000, p.PositionMs);   // 5000 + (3000-1000), anchored at receipt
    }

    [Fact]
    public void OnCluster_AgesSnapshotByServerSideDelta()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        // position 5000 sampled at ts=1000, cluster emitted at serverTs=3000 → 2000ms stale at fold (no clock sync needed).
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:t1", "Song", 200000), pos: 5000, tsMs: 1000, serverTsMs: 3000));
        Assert.Equal(7000, p.PositionMs);   // 5000 + serverSideAge(2000); no monotonic elapse yet
    }

    [Fact]
    public void OnCluster_SyncedServerClock_AddsNetworkTransit()
    {
        long now = 0, serverNow = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now, () => serverNow);
        serverNow = 3500;   // synced clock says server-now is 500ms past the cluster's emit time → +500 transit
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:t", "T", 200000), pos: 5000, tsMs: 1000, serverTsMs: 3000));
        Assert.Equal(7500, p.PositionMs);   // 5000 + serverSideAge(2000) + networkAge(500)
    }

    [Fact]
    public void OnCluster_NewTrackNearZero_IgnoresStaleTimestamp()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:a", "A", 200000), pos: 120000, tsMs: 1000, serverTsMs: 2000));
        // New track starts at ~0 but its Timestamp lags badly → must anchor at the snapshot, not jump forward by the Δ.
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:b", "B", 200000), pos: 300, tsMs: 1000, serverTsMs: 60000));
        Assert.Equal(300, p.PositionMs);
    }

    [Fact]
    public void Pos_AppliesPlaybackSpeed()
    {
        long now = 1000;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:t", "T", 600000), pos: 10000, speed: 2.0));
        now = 3000;   // 2000ms monotonic elapse at 2× → +4000
        Assert.Equal(14000, p.PositionMs);
    }

    [Fact]
    public void Pos_ClampsToDuration()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:t", "T", 5000), pos: 4000));
        now = 10000;   // would be 14000 but duration is 5000
        Assert.Equal(5000, p.PositionMs);
    }

    [Fact]
    public void Pos_Paused_ReturnsFrozenSnapshot_NoAging()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        // Paused remote, with a large server-side Δ: must NOT age (frozen) and must not interpolate.
        p.OnCluster(Cluster("other", playing: false, Trk("spotify:track:t", "T", 200000), pos: 8000, tsMs: 1000, serverTsMs: 5000));
        now = 100000;
        Assert.Equal(8000, p.PositionMs);
    }

    [Fact]
    public void OnCluster_NoActiveDevice_ClampsToPaused()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        p.OnCluster(Cluster("", playing: true, Trk("spotify:track:x", "X", 1000)));
        Assert.False(p.IsPlaying);   // nobody active → we are not playing
    }

    [Fact]
    public void Reconciliation_WhileWeOwnPlayback_TheClusterNeverRevertsTheLocalPlayState()
    {
        long now = 0;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.Ownership.Claim(ClaimCause.UserPlay);   // no publisher attached → Us/Unadopted at once
        var t = Local("spotify:track:t", "T", 100_000);
        p.ApplyLocalSnapshot(Snap(t), new PlaybackEvent(EvKind.Started, t, 0));
        Assert.True(p.IsPlaying);

        // local pause (optimistic) + the controller flags the in-flight command
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1234));
        p.NoteLocalCommand();
        Assert.False(p.IsPlaying);

        now = 1000;   // a STALE cluster naming us (still shows playing) arrives within the window → must NOT revert
        Assert.True(p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000))));
        Assert.False(p.IsPlaying);

        // …and not after it either: while WE own playback the host is the truth and the cluster only echoes what we
        // published. (This used to flip back to "playing" once the 2.5 s window lapsed — a window was the only thing
        // standing between a stale echo and the display.)
        now = 5000;
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.False(p.IsPlaying);
        Assert.Equal(1234, p.PositionMs);
    }

    [Fact]
    public void DeviceRoster_MapsRows_WithVolumePercent_AndActiveFlag()
    {
        var d = new LiveConnectDevices();
        IReadOnlyList<PlaybackDevice>? got = null;
        using var s = d.DevicesChanged.Subscribe(ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(x => got = x));
        d.Update(new[]
        {
            new ConnectDeviceRow("d1", "Phone", DeviceKind.Phone, true, 32768),
            new ConnectDeviceRow("d2", "Speaker", DeviceKind.Speaker, false, 0),
        });
        Assert.NotNull(got);
        Assert.Equal(2, got!.Count);
        Assert.Equal("Phone", got[0].Name);
        Assert.True(got[0].IsActive);
        Assert.Equal(50, got[0].VolumePercent);   // 32768 / 655.35 ≈ 50%
        Assert.Equal(0, got[1].VolumePercent);
    }

    [Fact]
    public void LocalEvent_DrivesSlab_AndFiresChanges()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var track = new Track("t", "spotify:track:t", "Local",
            new[] { new ArtistRef("a", "spotify:artist:a", "A") }, new AlbumRef("al", "spotify:album:al", "Al"),
            60000, false, null);
        bool fired = false;
        using var s = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => fired = true));
        p.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
        Assert.True(p.IsPlaying);
        Assert.Equal("Local", p.CurrentTrack!.Title);
        Assert.True(fired);
    }

    [Fact]
    public void LocalEvent_FoldsTrackDuration()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var track = new Track("t", "spotify:track:t", "Local",
            new[] { new ArtistRef("a", "spotify:artist:a", "A") }, new AlbumRef("al", "spotify:album:al", "Al"),
            151000, false, null);
        p.OnEvent(new PlaybackEvent(EvKind.Started, track, 0));
        Assert.Equal(151000, p.DurationMs);   // the seek bar scales scrub fractions by this — must follow the local track
    }

    [Fact]
    public void LocalTrackChange_ReplacesStaleDuration_FromPriorClusterTrack()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        // remote plays a 3:34 track…
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:old", "Old", 214000)));
        Assert.Equal(214000, p.DurationMs);
        // …then LOCAL playback advances to a 2:31 track: duration must follow (no cluster echo needed —
        // offline/PlayPlay-local playback never gets one, which froze the label AND corrupted seek targets).
        var next = new Track("n", "spotify:track:new", "New",
            new[] { new ArtistRef("a", "spotify:artist:a", "A") }, new AlbumRef("al", "spotify:album:al", "Al"),
            151000, false, null);
        p.OnEvent(new PlaybackEvent(EvKind.TrackChanged, next, 0));
        Assert.Equal(151000, p.DurationMs);
        Assert.Equal("spotify:track:new", p.CurrentTrack!.Uri);
    }

    [Fact]
    public void OnCluster_Restrictions_GateSkipAndSeek_AndVolumeFollowsActiveDevice()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        p.OnCluster(new ClusterDelta("other", true, Trk("spotify:ad:x", "Ad", 30000), "ctx",
            true, false, false, 0, 0, 0, 30000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(),
            DisallowSkipPrev: true, DisallowSkipNext: true, DisallowSeeking: true, OurVolume0_65535: 16384, ActiveVolume0_65535: 16384));
        Assert.False(p.CanSkipNext);    // ad → skip disabled
        Assert.False(p.CanSkipPrev);
        Assert.False(p.CanSeek);
        Assert.Equal(0.25, p.Volume, 2);   // 16384/65535 ≈ 0.25 (the active device's volume)
    }

    [Fact]
    public void OnCluster_ViewerQueue_SplitsProviders_AndDropsDelimiters()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        p.OnCluster(Cluster("other", playing: true, Trk("spotify:track:now", "Now", 1000)) with
        {
            NextTracks = new[]
            {
                new RemoteTrack("spotify:track:uq", "Queued", "Artist", "spotify:artist:a", "Album", "spotify:album:al", null, 0, Uid: "u1", Provider: "queue"),
                new RemoteTrack("spotify:delimiter", "", "", "", "", "", null, 0, Uid: "", Provider: "context"),
                new RemoteTrack("spotify:track:cx", "Context", "Artist", "spotify:artist:a", "Album", "spotify:album:al", null, 0, Uid: "u2", Provider: "context"),
                new RemoteTrack("spotify:track:ap", "Radio", "Artist", "spotify:artist:a", "Album", "spotify:album:al", null, 0, Uid: "u3", Provider: "autoplay"),
            },
            PrevTracks = new[]
            {
                new RemoteTrack("spotify:track:h1", "Old", "Artist", "spotify:artist:a", "Album", "spotify:album:al", null, 0, Uid: "u0", Provider: "context"),
            },
        });

        Assert.Equal(QueueBucket.NowPlaying, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:now").Bucket);
        Assert.Equal(QueueBucket.UserQueue, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:uq").Bucket);
        Assert.Equal(QueueBucket.NextUp, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:cx").Bucket);
        var autoplay = Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:ap");
        Assert.Equal(QueueBucket.NextUp, autoplay.Bucket);
        Assert.True(autoplay.IsAutoplay);
        // prev_tracks surface as the viewer's History tail (playback-restore fix §2 — the viewer half of the same bug
        // ReplaceFromCluster had), listed before NowPlaying like the local WindowQueue.
        var history = Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:h1");
        Assert.Equal(QueueBucket.History, history.Bucket);
        Assert.Equal("u0", history.Uid);
        Assert.True(p.Queue[0].Track.Uri == "spotify:track:h1");   // history precedes now-playing in panel order
        Assert.DoesNotContain(p.Queue, e => e.Track.Uri == "spotify:delimiter");
    }

    // Test 5 (playback-restore findings §9) — a cold start whose cluster still (stale) names US active must not hide the
    // queue: without a local session the fold takes MapQueue + the cluster's shuffle/repeat exactly like a viewer fold.
    [Fact]
    public void OnCluster_WeAreStaleActive_FillsQueueFromCluster()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        p.OnCluster(new ClusterDelta(
            "us", true, Trk("spotify:track:now", "Now", 200000), "spotify:playlist:ctx",
            false, true, false, 1000, 0, 0, 200000, Shuffle: true, RepeatMode.Context,
            Array.Empty<ConnectDeviceRow>(),
            new[]
            {
                new RemoteTrack("spotify:track:uq", "Queued", "", "", "", "", null, 0, Uid: "q1", Provider: "queue"),
                new RemoteTrack("spotify:track:cx", "Ctx", "", "", "", "", null, 0, Uid: "u2", Provider: "context"),
            }));

        // Our previous process's last state named us — that is NOT ownership (F2: no claim in this process).
        Assert.False(p.WeAreActive);
        Assert.Equal(NobodyCause.StaleSelf, p.Ownership.Current.Cause);
        Assert.Equal(QueueBucket.UserQueue, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:uq").Bucket);
        Assert.Equal(QueueBucket.NextUp, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:cx").Bucket);
        Assert.True(p.IsShuffle);                       // cluster options applied (no local session to own them)
        Assert.Equal(RepeatMode.Context, p.Repeat);
    }

    // ── the Connect owner decides the display ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Foreign_HostTicks_MoveNothing()
    {
        long now = 1_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        Assert.True(p.OnCluster(Cluster("phone", playing: false, Trk("spotify:track:phone", "Phone Song", 200_000),
            pos: 42_000, serverTsMs: 100)));
        Assert.Equal(OwnerKind.Foreign, p.Ownership.Current.Kind);
        using var changes = new ChangeCounter(p);
        int ticks = 0;
        using var tickSub = p.PositionTicks.Subscribe(ConnectHarness.Obs<PositionSample>(_ => ticks++));

        // A local host that should not be running (the controller stops it on every fold) still reports.
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 7_000));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 7_200));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Buffering, 7_200));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 7_200));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Ended, 7_400));
        now = 4_000;

        Assert.Equal(0, changes.Count);   // no publish
        Assert.Equal(0, ticks);           // no position tick onto the lyrics / seek-bar clock
        Assert.False(p.IsPlaying);        // the phone is paused, whatever our host says
        Assert.False(p.IsBuffering);
        Assert.Equal(42_000, p.PositionMs);
        Assert.Equal("spotify:track:phone", p.CurrentTrack!.Uri);
    }

    // The PLAY-glyph incident's shape (handoff §3.4): we were playing, the phone took over paused, and our host's ticks —
    // still in flight before the controller's StopHost lands — kept flipping the published state back.
    [Fact]
    public void Foreign_PausedFrame_ThenHostTick_PublishesFalseOnce()
    {
        long now = 0;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.Ownership.Claim(ClaimCause.UserPlay);
        var ours = Local("spotify:track:ours");
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Started, ours, 0));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 1_000));
        Assert.True(p.IsPlaying);
        using var changes = new ChangeCounter(p);

        // The phone takes over, paused — newer than our claim's fence (A2).
        Assert.True(p.OnCluster(Cluster("phone", playing: false, Trk("spotify:track:phone", "Phone Song", 200_000),
            pos: 57_150, serverTsMs: 100)));
        Assert.Equal(OwnerKind.Foreign, p.Ownership.Current.Kind);
        Assert.Equal(1, changes.Count);   // the takeover AND the new display: one publish
        Assert.False(changes.LastPlaying);

        for (int i = 1; i <= 3; i++)
        {
            now += 200;
            p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1_000 + i * 200));
        }

        Assert.Equal(1, changes.Count);   // "not playing" published once; the host never flips it back
        Assert.False(p.IsPlaying);
        Assert.Equal("spotify:track:phone", p.CurrentTrack!.Uri);
        Assert.Equal(57_150, p.PositionMs);   // the phone's paused playhead, not our host's
    }

    // The published-state memory is committed by EVERY publish. It used to be written only by OnHostSignal, so a publish
    // of "not playing" from any other writer left it saying "playing" — and the next still-playing host tick compared
    // equal, never fired, and the bar kept a PLAY glyph over audible playback.
    [Fact]
    public void LastPublished_CommittedInFireChanges()
    {
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        p.Ownership.Claim(ClaimCause.UserPlay);
        var ours = Local("spotify:track:ours");
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Started, ours, 0));
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Playing, 500));   // published: playing

        p.OnEvent(new PlaybackEvent(EvKind.Paused, ours, 600));                  // another writer publishes: not playing
        Assert.False(p.IsPlaying);
        using var changes = new ChangeCounter(p);

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 800));   // …but the host plays on
        Assert.Equal(1, changes.Count);   // the tick disagreed with what was PUBLISHED → corrected at once
        Assert.True(changes.LastPlaying);

        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.PositionTick, 1_000));
        Assert.Equal(1, changes.Count);   // a steady tick publishes nothing structural
    }

    // 14:58:14.65 (reason DEVICES_DISAPPEARED): nobody active, the player_state still the phone's. The old fold took
    // "nobody active" as local ownership and repainted our stale session (at the phone's playhead) — the flip loop.
    [Fact]
    public void DevicesDisappeared_KeepsForeignSnapshotPaused()
    {
        long now = 1_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        var ours = Local("spotify:track:6vfQ");
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Paused, ours, 72_777));   // a restored local session
        var phoneTrack = Trk("spotify:track:40x5K8", "Lost on You", 268_000);
        Assert.True(p.OnCluster(Cluster("phone", playing: true, phoneTrack, pos: 2_578, serverTsMs: 100)));

        Assert.True(p.OnCluster(Cluster("", playing: true, phoneTrack, pos: 2_578, serverTsMs: 110) with { UpdateReason = 1 }));

        var owner = p.Ownership.Current;
        Assert.Equal(OwnerKind.Nobody, owner.Kind);
        Assert.Equal(NobodyCause.FromForeign, owner.Cause);
        Assert.Equal("phone", owner.DeviceId);
        Assert.Equal("", p.ActiveDeviceId);
        Assert.Equal("spotify:track:40x5K8", p.CurrentTrack!.Uri);   // the phone's track, not our stale session
        Assert.False(p.IsPlaying);                                   // nobody is playing it
        now += 30_000;
        Assert.Equal(2_578, p.PositionMs);                           // frozen where the phone left it

        // …and the local session still cannot repaint over it (its revision is all that is recorded).
        p.ApplyLocalSnapshot(Snap(ours) with { Revision = 2 }, new PlaybackEvent(EvKind.Paused, ours, 72_777));
        Assert.Equal("spotify:track:40x5K8", p.CurrentTrack!.Uri);
        Assert.Equal(2, p.LocalRevision);
    }

    [Fact]
    public void ProtectedClaim_ForeignPushKeepsLocalTrack()
    {
        long now = 0;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        var phoneTrack = Trk("spotify:track:phone", "Phone Song", 120_000);
        p.OnCluster(Cluster("phone", playing: true, phoneTrack, pos: 5_000, serverTsMs: 100));
        p.Ownership.AttachAcknowledger();   // a publisher is attached: the claim waits for the server's verdict
        p.Ownership.Claim(ClaimCause.UserPlay);
        Assert.Equal(ClaimPhase.Protected, p.Ownership.Current.Claim);
        var ours = Local("spotify:track:ours", dur: 200_000);
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Started, ours, 0));

        // A push the server built before it heard our claim still names the phone (P2): recorded, not acted on.
        Assert.True(p.OnCluster(Cluster("phone", playing: true, phoneTrack, pos: 9_000, serverTsMs: 150)));

        Assert.Equal(OwnerKind.Us, p.Ownership.Current.Kind);
        Assert.Equal("spotify:track:ours", p.CurrentTrack!.Uri);
        Assert.True(p.IsPlaying);
        Assert.Equal(0, p.PositionMs);          // our playhead, not the phone's 9 s
        Assert.Equal(200_000, p.DurationMs);    // the phone's 2:00 length does not leak onto our track
        Assert.Equal("us", p.ActiveDeviceId);   // the owner…
        Assert.Equal("phone", p.ClusterActiveDeviceId);   // …not the raw cluster field
    }

    [Fact]
    public void InboundTransferStarted_WhileClusterNamesPhone_ShowsLocal()
    {
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        p.Ownership.AttachAcknowledger();
        var phoneTrack = Trk("spotify:track:phone", "Phone Song", 120_000);
        p.OnCluster(Cluster("phone", playing: true, phoneTrack, pos: 5_000, serverTsMs: 100));
        Assert.Equal("spotify:track:phone", p.CurrentTrack!.Uri);

        // The inbound transfer CLAIMS before it loads, so its Started lands under Us even though the last cluster (and
        // the next one, which has not caught up) still names the phone. The old fold needed a 5 s wall-clock "pending"
        // window for this; the claim is the fact now.
        p.Ownership.Claim(ClaimCause.InboundTransfer);
        var moved = Local("spotify:track:moved", "Moved");
        p.ApplyLocalSnapshot(Snap(moved), new PlaybackEvent(EvKind.Started, moved, 5_300));

        Assert.Equal("spotify:track:moved", p.CurrentTrack!.Uri);
        Assert.Equal("spotify:playlist:local", p.ContextUri);
        Assert.True(p.IsPlaying);
        Assert.Equal("us", p.ActiveDeviceId);

        p.OnCluster(Cluster("phone", playing: true, phoneTrack, pos: 5_400, serverTsMs: 120));
        Assert.Equal("spotify:track:moved", p.CurrentTrack!.Uri);
        Assert.Equal("spotify:playlist:local", p.ContextUri);
        Assert.True(p.IsPlaying);
    }

    [Fact]
    public void LocalSnapshot_WhileForeign_DisplayUntouched()
    {
        long now = 1_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        Assert.True(p.OnCluster(Cluster("phone", playing: true, Trk("spotify:track:phone", "Phone Song", 120_000),
            pos: 5_000, serverTsMs: 100) with { Shuffle = true }));
        using var changes = new ChangeCounter(p);

        // The controller keeps its own session warm while the phone plays (a continuation fetch republishes it) — none of
        // the local writers may repaint what the user is looking at.
        var ours = Local("spotify:track:ours", dur: 200_000);
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.TrackChanged, ours, 0));
        p.ApplyLocalSnapshot(Snap(ours));
        p.OnEvent(new PlaybackEvent(EvKind.Started, ours, 0));
        p.SetLocalQueue(new[] { new QueueEntry(QueueItemId.None, "now", ours, QueueBucket.NowPlaying, QueueProvider.Context, false, "u") });
        p.SetLocalOptions(false, RepeatMode.Track);

        Assert.Equal(0, changes.Count);
        Assert.Equal("spotify:track:phone", p.CurrentTrack!.Uri);
        Assert.Equal(120_000, p.DurationMs);
        Assert.True(p.IsPlaying);
        Assert.Equal(5_000, p.PositionMs);
        Assert.Equal("spotify:playlist:ctx", p.ContextUri);
        Assert.True(p.IsShuffle);
        Assert.Equal(RepeatMode.Off, p.Repeat);
        Assert.Contains(p.Queue, e => e.Track.Uri == "spotify:track:phone");
        Assert.DoesNotContain(p.Queue, e => e.Track.Uri == "spotify:track:ours");
        Assert.Equal("phone", p.ActiveDeviceId);
    }

    // Agent A's version of this asserted OUR track repainting over the phone's on Ended — the wrong way round: under
    // another device's display, our session ending (or the takeover's own BecameInactive) is not what the user sees.
    [Fact]
    public void LocalEnded_WhileForeign_DisplayUntouched()
    {
        long now = 1_000;
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.Ownership.Claim(ClaimCause.UserPlay);
        var ours = Local("spotify:track:ours");
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Started, ours, 0));
        Assert.True(p.OnCluster(Cluster("phone", playing: true, Trk("spotify:track:phone", "Phone Song", 120_000),
            pos: 5_000, serverTsMs: 100)));                                                  // the phone takes over (A2)
        Assert.Equal(OwnerKind.Foreign, p.Ownership.Current.Kind);

        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.BecameInactive, ours, 3_000));   // the takeover's own
        p.ApplyLocalSnapshot(Snap(ours), new PlaybackEvent(EvKind.Ended, ours, 3_000));            // a late Ended
        p.OnEvent(new PlaybackEvent(EvKind.Ended, ours, 3_000));

        Assert.Equal("spotify:track:phone", p.CurrentTrack!.Uri);
        Assert.True(p.IsPlaying);
        Assert.Equal(5_000, p.PositionMs);
        now = 3_000;
        Assert.Equal(7_000, p.PositionMs);   // still the phone's playhead, still advancing
    }

    [Fact]
    public void ActiveDeviceId_IsOwnerDerived()
    {
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        Assert.Equal("", p.ActiveDeviceId);

        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:mine", "Mine", 200_000), serverTsMs: 100));
        Assert.Equal("", p.ActiveDeviceId);              // our previous process's state is not ownership (F2)
        Assert.Equal("us", p.ClusterActiveDeviceId);
        Assert.False(p.WeAreActive);
        Assert.False(p.IsPlaying);                       // …and nobody is playing it

        p.OnCluster(Cluster("phone", playing: true, Trk("spotify:track:p", "P", 200_000), serverTsMs: 110));
        Assert.Equal("phone", p.ActiveDeviceId);
        using var changes = new ChangeCounter(p);

        p.Ownership.Claim(ClaimCause.UserPlay);
        Assert.Equal("us", p.ActiveDeviceId);            // the claim, before any cluster names us
        Assert.Equal("phone", p.ClusterActiveDeviceId);
        Assert.True(p.WeAreActive);
        Assert.Equal(1, changes.Count);                  // the transition itself publishes (the bar re-reads the owner)

        p.Ownership.Release(ReleaseCause.EndOfContext);
        Assert.Equal("", p.ActiveDeviceId);
        Assert.False(p.WeAreActive);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void StaleFrame_ReturnsFalse()
    {
        using var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        Assert.True(p.OnCluster(Cluster("phone", playing: true, Trk("spotify:track:a", "A", 200_000), serverTsMs: 200)));
        var last = p.LastCluster;
        using var changes = new ChangeCounter(p);

        // A slow PUT's answer landing after a newer push: dropped whole (F0) — and the caller skips the roster too.
        Assert.False(p.OnCluster(Cluster("speaker", playing: false, Trk("spotify:track:b", "B", 200_000), serverTsMs: 150)));

        Assert.Equal(0, changes.Count);
        Assert.Same(last, p.LastCluster);
        Assert.Equal("phone", p.ActiveDeviceId);
        Assert.Equal("phone", p.ClusterActiveDeviceId);
        Assert.Equal("spotify:track:a", p.CurrentTrack!.Uri);
        Assert.True(p.IsPlaying);

        // A frame with no server time has nothing to be ordered by — never dropped.
        Assert.True(p.OnCluster(Cluster("speaker", playing: false, Trk("spotify:track:b", "B", 200_000))));
        Assert.Equal("speaker", p.ActiveDeviceId);
    }
}
