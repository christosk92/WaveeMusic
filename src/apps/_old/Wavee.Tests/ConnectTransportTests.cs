using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Backend;
using Wavee.Backend.Realtime;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage A — the widened transport seam: header-carrying frames, REQUEST surfacing + typed/escaped Reply, the Publish PUT
// hook, and the silent audio-host position math. The frame decode is the hardest-to-rediscover wire asset, so it is pinned.
public class ConnectTransportTests
{
    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    [Fact]
    public void MessageFrame_SurfacesHeaders_AndGunzipsPayload()
    {
        var payload = Encoding.UTF8.GetBytes("hello-cluster");
        var json = ConnectHarness.MessageFrame("hm://connect-state/v1/cluster", payload, gzip: true,
            headers: new[] { ("Spotify-Connection-Id", "conn-123") });
        var f = DealerFrameParser.Parse(json);
        Assert.Equal(DealerFrameType.Message, f.Type);
        Assert.Equal("hm://connect-state/v1/cluster", f.Uri);
        Assert.Equal("hello-cluster", Encoding.UTF8.GetString(f.Payload));   // gunzipped
        Assert.NotNull(f.Headers);
        Assert.Equal("conn-123", f.Headers!["Spotify-Connection-Id"]);
    }

    [Fact]
    public void PusherFrame_CarriesConnectionId_InHeader()
    {
        // The connection_id arrives ONLY in the header (the URI tail is unreliable) — this is the announce gate.
        var json = ConnectHarness.MessageFrame("hm://pusher/v1/connections/", Array.Empty<byte>(),
            headers: new[] { ("Spotify-Connection-Id", "the-conn-id") });
        var f = DealerFrameParser.Parse(json);
        Assert.Equal("the-conn-id", f.Headers!["Spotify-Connection-Id"]);
    }

    [Fact]
    public void RequestFrame_ParsesKeyIdent_AndGunzipsSingularPayload()
    {
        const string cmd = "{\"command\":{\"endpoint\":\"pause\"}}";
        var json = ConnectHarness.RequestFrame("msgid-1/dev-2", "hm://connect-state/v1/player/command", cmd);
        var f = DealerFrameParser.Parse(json);
        Assert.Equal(DealerFrameType.Request, f.Type);
        Assert.Equal("msgid-1/dev-2", f.Key);
        Assert.Equal("hm://connect-state/v1/player/command", f.MessageIdent);
        Assert.Equal(cmd, Encoding.UTF8.GetString(f.Payload));   // singular payload.compressed → gunzip
    }

    [Fact]
    public void PingFrame_IsClassified()
    {
        var f = DealerFrameParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"));
        Assert.Equal(DealerFrameType.Ping, f.Type);
    }

    [Fact]
    public void StubTransport_RequestsFilteredByIdentPrefix()
    {
        var t = new StubTransport();
        var got = new List<string>();
        using var sub = t.Requests("hm://connect-state/v1/").Subscribe(ConnectHarness.Obs<WireRequest>(r => got.Add(r.RequestId)));
        t.PushRequest(new WireRequest("k1", "hm://connect-state/v1/player/command", Encoding.UTF8.GetBytes("{}"), NoHeaders));
        t.PushRequest(new WireRequest("k2", "hm://other/v1/x", Array.Empty<byte>(), NoHeaders));   // filtered out
        Assert.Equal(new[] { "k1" }, got);
    }

    [Fact]
    public void StubTransport_EventsFilteredByTopicPrefix()
    {
        var t = new StubTransport();
        var got = new List<string>();
        using var sub = t.Events("hm://connect-state/").Subscribe(ConnectHarness.Obs<WireEvent>(e => got.Add(e.Topic)));
        t.PushEvent(new WireEvent("hm://connect-state/v1/cluster", Array.Empty<byte>()));
        t.PushEvent(new WireEvent("hm://playlist/v2/foo", Array.Empty<byte>()));   // filtered out
        Assert.Equal(new[] { "hm://connect-state/v1/cluster" }, got);
    }

    [Fact]
    public async System.Threading.Tasks.Task StubTransport_ReplyTyped_AndPublishRecords()
    {
        var t = new StubTransport { PublishResponse = new byte[] { 1, 2, 3 } };
        await t.Reply("k", RequestResult.DeviceDoesNotSupportCommand);
        Assert.Equal(RequestResult.DeviceDoesNotSupportCommand, t.LastReply);

        var resp = await t.Publish("dev-1", "conn-1", new byte[] { 9 });
        Assert.True(resp.Ok);
        Assert.Equal(new byte[] { 1, 2, 3 }, resp.Body);   // the announce response (Cluster) is handed back
        Assert.Equal(1, t.PublishCount);
        Assert.Equal(new byte[] { 9 }, t.LastPublishBody);
    }

    [Fact]
    public void SilentAudioHost_PositionMath_AnchorsAndClamps()
    {
        long now = 1000;
        var host = new SilentAudioHost(() => now);
        host.Load(new AudioStreamHandle("spotify:track:x", "fid", "cdn", default, AudioFormat.OggVorbis320, 5000, 0f));
        Assert.Equal(0, host.PositionMs);

        host.Play();                 // anchorWall=1000
        now = 3000;
        Assert.Equal(2000, host.PositionMs);   // 0 + (3000-1000)

        host.Seek(4000, SeekMode.Accurate);             // anchorPos=4000, anchorWall=3000
        now = 3500;
        Assert.Equal(4500, host.PositionMs);   // 4000 + (3500-3000)

        now = 99999;
        Assert.Equal(5000, host.PositionMs);   // clamped to duration

        host.Pause();
        now = 99999999;
        Assert.Equal(5000, host.PositionMs);   // frozen at pause
        Assert.False(host.IsPlaying);
    }

    [Fact]
    public void SilentAudioHost_PositionGetter_IsZeroAlloc()
    {
        long now = 0;
        var host = new SilentAudioHost(() => now);
        host.Load(new AudioStreamHandle("u", "f", "c", default, AudioFormat.OggVorbis160, 10000, 0f));
        host.Play();
        long delta = ConnectHarness.AllocDelta(() => { now += 10; _ = host.PositionMs; }, iters: 1000);
        Assert.True(delta < 1024, $"position getter should be ~zero-alloc on the hot path, was {delta} bytes / 1000 reads");
    }
}
