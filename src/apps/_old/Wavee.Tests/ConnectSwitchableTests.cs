using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The switchable facade: bind once, swap the backend live — delegates to the current inner and re-emits on swap so the
// bridge's existing subscriptions keep working against the new (live) backend.
public class ConnectSwitchableTests
{
    static ClusterDelta C(string trackUri) =>
        new("other", true, new RemoteTrack(trackUri, "T", "A", "spotify:artist:a", "Al", "spotify:album:al", null, 1000),
            "ctx", true, false, false, 0, 0, 0, 1000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    [Fact]
    public void SwitchableState_Delegates_ReEmitsOnSwap_AndForwardsInnerChanges()
    {
        var a = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        a.OnCluster(C("spotify:track:a"));
        var sw = new SwitchableState(a);

        int changes = 0;
        using var sub = sw.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => changes++));
        Assert.Equal("spotify:track:a", sw.CurrentTrack!.Uri);   // delegates to the initial inner

        var b = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        b.OnCluster(C("spotify:track:b"));
        sw.SetInner(b);
        Assert.True(changes >= 1);                                // re-emitted on swap
        Assert.Equal("spotify:track:b", sw.CurrentTrack!.Uri);   // now delegates to the live backend

        int before = changes;
        b.OnCluster(C("spotify:track:c"));                       // a change on the NEW inner flows through the switch
        Assert.True(changes > before);
        Assert.Equal("spotify:track:c", sw.CurrentTrack!.Uri);
    }

    [Fact]
    public void SwitchableDevices_Delegates_AndForwardsChangesFromTheNewInner()
    {
        var a = new LiveConnectDevices();
        var sw = new SwitchableDevices(a);
        IReadOnlyList<PlaybackDevice>? got = null;
        using var sub = sw.DevicesChanged.Subscribe(ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(x => got = x));

        var b = new LiveConnectDevices();
        sw.SetInner(b);
        b.Update(new[] { new ConnectDeviceRow("d", "Phone", DeviceKind.Phone, true, 0) });

        Assert.NotNull(got);
        Assert.Single(sw.Devices);
        Assert.Equal("Phone", sw.Devices[0].Name);
    }
}
