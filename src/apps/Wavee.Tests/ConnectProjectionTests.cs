using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage D — the cluster → IPlaybackState projection, reconciliation, and the device roster (proto-free, hand-built deltas).
public class ConnectProjectionTests
{
    static RemoteTrack Trk(string uri, string title, long dur) =>
        new(uri, title, "Artist", "spotify:artist:a", "Album", "spotify:album:al", "https://img/x", dur);

    static ClusterDelta Cluster(string active, bool playing, RemoteTrack? track = null, long pos = 0,
        IReadOnlyList<ConnectDeviceRow>? devices = null, long tsMs = 0, long serverTsMs = 0, double speed = 1.0) =>
        new(active, track is not null, track ?? default, "spotify:playlist:ctx",
            playing, !playing, false, pos, tsMs, serverTsMs, track?.DurationMs ?? 0, false, RepeatMode.Off,
            devices ?? Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>(), PlaybackSpeed: speed);

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
    public void Reconciliation_StaleClusterDoesNotRevertOptimisticLocalCommand()
    {
        long now = 0;
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => now);
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.True(p.IsPlaying);

        // local pause (optimistic) + the controller flags the in-flight command
        p.OnHostSignal(new AudioHostSignal(AudioHostSignalKind.Paused, 1234));
        p.NoteLocalCommand();
        Assert.False(p.IsPlaying);

        now = 1000;   // a STALE cluster (still shows playing) arrives within the window → must NOT revert
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.False(p.IsPlaying);

        now = 5000;   // past the window → the cluster is authoritative again
        p.OnCluster(Cluster("us", playing: true, Trk("spotify:track:t", "T", 100000)));
        Assert.True(p.IsPlaying);
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

        Assert.True(p.WeAreActive);
        Assert.Equal(QueueBucket.UserQueue, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:uq").Bucket);
        Assert.Equal(QueueBucket.NextUp, Assert.Single(p.Queue, e => e.Track.Uri == "spotify:track:cx").Bucket);
        Assert.True(p.IsShuffle);                       // cluster options applied (no local session to own them)
        Assert.Equal(RepeatMode.Context, p.Repeat);
    }
}
