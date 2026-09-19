using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class ConnectDeviceRosterEqualityTests
{
    private static ConnectDeviceRow Phone => new("phone", "Phone", DeviceKind.Phone, true, 32768);
    private static ConnectDeviceRow Speaker => new("speaker", "Speaker", DeviceKind.Speaker, false, 0);

    [Fact]
    public void IdenticalHeartbeats_RetainArrayAndEntries_WithoutNotifying()
    {
        var devices = new LiveConnectDevices();
        int publications = 0;
        using var subscription = devices.DevicesChanged.Subscribe(
            ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(_ => publications++));
        devices.Update(new[] { Phone, Speaker });
        var first = devices.Devices;
        var firstPhone = first[0];
        int baseline = publications;
        for (int i = 0; i < 100; i++) devices.Update(new List<ConnectDeviceRow> { Phone, Speaker });
        Assert.Equal(baseline, publications);
        Assert.Same(first, devices.Devices);
        Assert.Same(firstPhone, devices.Devices[0]);
    }

    [Fact]
    public void EveryProjectedFieldChange_PublishesNewSnapshot()
    {
        ConnectDeviceRow[] changed =
        [
            Phone with { Id = "other-phone" },
            Phone with { Name = "Renamed phone" },
            Phone with { Kind = DeviceKind.Computer },
            Phone with { IsActive = false },
            Phone with { Volume0_65535 = 65535 },
        ];
        foreach (var row in changed)
        {
            var devices = new LiveConnectDevices();
            int publications = 0;
            using var subscription = devices.DevicesChanged.Subscribe(
                ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(_ => publications++));
            devices.Update(new[] { Phone });
            var previous = devices.Devices;
            int baseline = publications;
            devices.Update(new[] { row });
            Assert.Equal(baseline + 1, publications);
            Assert.NotSame(previous, devices.Devices);
            Assert.Equal(new PlaybackDevice(row.Id, row.Name, row.Kind, row.IsActive,
                (int)Math.Round(row.Volume0_65535 / 655.35)), devices.Devices[0]);
            Assert.Equal("Phone", previous[0].Name); // previously published snapshots remain immutable
        }
    }

    [Fact]
    public void MembershipAndOrderChanges_AreNeverCoalesced()
    {
        var devices = new LiveConnectDevices();
        int publications = 0;
        using var subscription = devices.DevicesChanged.Subscribe(
            ConnectHarness.Obs<IReadOnlyList<PlaybackDevice>>(_ => publications++));
        devices.Update(new[] { Phone, Speaker });
        int baseline = publications;
        devices.Update(new[] { Speaker, Phone });
        Assert.Equal(baseline + 1, publications);
        Assert.Equal("speaker", devices.Devices[0].Id);
        devices.Update(new[] { Phone });
        Assert.Equal(baseline + 2, publications);
        Assert.Single(devices.Devices);
        devices.Update(Array.Empty<ConnectDeviceRow>());
        Assert.Equal(baseline + 3, publications);
        Assert.Empty(devices.Devices);
        var empty = devices.Devices;
        devices.Update(Array.Empty<ConnectDeviceRow>());
        Assert.Equal(baseline + 3, publications);
        Assert.Same(empty, devices.Devices);
    }

    [Fact]
    public void VolumeComparison_UsesTheSamePublicRoundingAsMapping()
    {
        var devices = new LiveConnectDevices();
        devices.Update(new[] { Phone });
        var previous = devices.Devices;
        devices.Update(new[] { Phone with { Volume0_65535 = 32769 } });
        Assert.Same(previous, devices.Devices); // both protocol values project to 50 percent
        devices.Update(new[] { Phone with { Volume0_65535 = 33423 } });
        Assert.NotSame(previous, devices.Devices);
        Assert.Equal(51, devices.Devices[0].VolumePercent);
    }

    [Fact]
    public void RepeatedEquivalentUpdate_IsAllocationFreeAtPublisher()
    {
        var devices = new LiveConnectDevices();
        ConnectDeviceRow[] rows = [Phone, Speaker];
        devices.Update(rows);
        for (int i = 0; i < 1000; i++) devices.Update(rows);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) devices.Update(rows);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
