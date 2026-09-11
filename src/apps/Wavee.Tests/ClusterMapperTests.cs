using System;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using P = Wavee.Protocol.Player;

namespace Wavee.Tests;

// The cluster wire boundary (SpotifyLive/ClusterMapper.cs): ClusterMapper.Map (Cluster proto → the proto-free
// ClusterDelta) and ClusterIngest (dealer push / put-state response → the projection fold + device roster). Source
// included directly (Wavee.Tests.csproj — next to the existing ConnectStateBuilder.cs include) so these tests exercise
// production code, not a re-implementation of it.
public class ClusterMapperTests
{
    const string Us = "us";

    // ── ClusterMapper.Map ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Map_CarriesOriginReasonChangedDevicesStartedAt()
    {
        var cluster = new P.Cluster
        {
            ActiveDeviceId = Us,
            ServerTimestampMs = 12_345,
            StartedPlayingAtTimestamp = 98_765,   // field 11, optional — the server's newest-starter timestamp
        };
        var changed = new[] { "dev-a", "dev-b" };

        var delta = ClusterMapper.Map(cluster, Us, ClusterOrigin.Push, putMsgId: 0, updateReason: 4, changedDevices: changed);

        Assert.Equal(ClusterOrigin.Push, delta.Origin);
        Assert.Equal(0u, delta.PutMsgId);
        Assert.Equal(4, delta.UpdateReason);
        Assert.Equal(changed, delta.ChangedDevices);
        Assert.Equal(98_765, delta.ActiveStartedPlayingAt);
    }

    [Fact]
    public void Map_PutResponse_CarriesTheClaimsMsgId()
    {
        var cluster = new P.Cluster { ActiveDeviceId = Us, ServerTimestampMs = 500 };

        var delta = ClusterMapper.Map(cluster, Us, ClusterOrigin.PutResponse, putMsgId: 7);

        Assert.Equal(ClusterOrigin.PutResponse, delta.Origin);
        Assert.Equal(7u, delta.PutMsgId);
        Assert.Equal(0, delta.UpdateReason);          // a bare put-state response Cluster has no ClusterUpdate envelope
        Assert.Null(delta.ChangedDevices);
    }

    [Fact]
    public void Map_NoStartedPlayingAtTimestamp_DefaultsToZero()
    {
        // optional uint64 started_playing_at_timestamp = 11 — absent on most pushes (only the newest-starter frame
        // carries it); the raw proto's own presence bit (not "== 0") decides.
        var cluster = new P.Cluster { ActiveDeviceId = Us };

        var delta = ClusterMapper.Map(cluster, Us);

        Assert.Equal(0, delta.ActiveStartedPlayingAt);
    }

    // ── findings §3.2: the phone's frames for a self-titled/unlabelled track carry a title and an artist_uri, but NO
    // artist_name — mapping must keep the wire title verbatim and populate the artist uri without inventing a name. ──
    [Fact]
    public void Map_TitleWithoutArtistName_KeepsTitle_ArtistUriOnly()
    {
        var track = new P.ProvidedTrack { Uri = "spotify:track:x", ArtistUri = "spotify:artist:a1" };
        track.Metadata["title"] = "The First Time";
        var cluster = new P.Cluster
        {
            ActiveDeviceId = "phone",
            PlayerState = new P.PlayerState { Track = track, Duration = 200_000 },
        };

        var delta = ClusterMapper.Map(cluster, Us);

        Assert.True(delta.HasTrack);
        Assert.Equal("The First Time", delta.Track.Title);
        Assert.Equal("", delta.Track.ArtistName);              // never fabricated
        Assert.Equal("spotify:artist:a1", delta.Track.ArtistUri);
        Assert.Equal(200_000, delta.Track.DurationMs);
    }

    // ── ClusterIngest: fold-first, roster-only-on-fold ──────────────────────────────────────────────────────────────

    static P.ClusterUpdate BuildUpdate(string activeId, long serverTs, params (string Id, string Name)[] devices)
    {
        var cluster = new P.Cluster { ActiveDeviceId = activeId, ServerTimestampMs = serverTs };
        foreach (var (id, name) in devices)
            cluster.Device[id] = new P.DeviceInfo { DeviceId = id, Name = name, DeviceType = P.DeviceType.Computer };
        return new P.ClusterUpdate { Cluster = cluster };
    }

    static void Push(StubTransport transport, P.ClusterUpdate update)
        => transport.PushEvent(new WireEvent("hm://connect-state/v1/cluster", update.ToByteArray()));

    [Fact]
    public void Ingest_StaleFrame_DoesNotMoveTheRoster()
    {
        var transport = new StubTransport();
        var proj = new NowPlayingProjection(Us, NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var devices = new LiveConnectDevices();
        using var ingest = new ClusterIngest(transport, proj, devices, Us);

        Push(transport, BuildUpdate("phone", serverTs: 1000, ("phone", "iPhone")));
        Assert.Contains(devices.Devices, d => d.Id == "phone");

        // An OLDER server timestamp than the one already folded — F0 drops it (and the fold's own roster update with
        // it), even though this frame's device map disagrees with what is on screen.
        Push(transport, BuildUpdate("speaker", serverTs: 500, ("speaker", "Speaker")));

        Assert.DoesNotContain(devices.Devices, d => d.Id == "speaker");   // stale frame → roster untouched
        Assert.Contains(devices.Devices, d => d.Id == "phone");           // the last FOLDED roster still stands
    }

    [Fact]
    public void Ingest_NewerFrame_MovesTheRoster()
    {
        var transport = new StubTransport();
        var proj = new NowPlayingProjection(Us, NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        var devices = new LiveConnectDevices();
        using var ingest = new ClusterIngest(transport, proj, devices, Us);

        Push(transport, BuildUpdate("phone", serverTs: 1000, ("phone", "iPhone")));
        Push(transport, BuildUpdate("speaker", serverTs: 1500, ("speaker", "Speaker")));

        Assert.Contains(devices.Devices, d => d.Id == "speaker");
        Assert.DoesNotContain(devices.Devices, d => d.Id == "phone");
    }
}
