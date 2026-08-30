using System;
using System.Collections.Generic;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// ConnectService is now capture-only: it surfaces the dealer connection_id from the pusher hello header. (The device
// announce / PutState moved to DeviceStatePublisher — see ConnectPublisherTests.)
public class ConnectServiceTests
{
    static WireEvent Pusher(string connId) =>
        new("hm://pusher/v1/connections/gae2", Array.Empty<byte>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Spotify-Connection-Id"] = connId });

    [Fact]
    public void CapturesConnectionId_FromHeader()
    {
        var t = new StubTransport();
        using var svc = new ConnectService(t);
        string? observed = null;
        using var sub = svc.ConnectionId.Subscribe(ConnectHarness.Obs<string?>(id => observed = id));
        t.PushEvent(Pusher("conn-1"));
        Assert.Equal("conn-1", svc.CurrentConnectionId);
        Assert.Equal("conn-1", observed);
    }

    [Fact]
    public void IgnoresPusher_WithoutConnectionIdHeader()
    {
        var t = new StubTransport();
        using var svc = new ConnectService(t);
        t.PushEvent(new WireEvent("hm://pusher/v1/connections/x", Array.Empty<byte>()));   // no header
        Assert.Null(svc.CurrentConnectionId);
    }
}
