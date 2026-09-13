using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Pure composition of the two-section device picker (plan §D1 DevicePickerItemsTests). Tests DevicePickerModel (engine-free)
// rather than the MenuFlyoutItem mapping, which lives in the engine-referencing PlayerBar (not compilable here).
public class DevicePickerItemsTests
{
    static LocalAudioDevice Local(string id, string name, bool def = false, bool cur = false) =>
        new(id, name, LocalAudioDeviceKind.Speakers, def, cur);

    static PlaybackDevice Connect(string id, string name, DeviceKind kind, bool active = false) =>
        new(id, name, kind, active, 50);

    static IReadOnlyList<DevicePickerRow> Build(
        IReadOnlyList<LocalAudioDevice> local, string? selectedLocal, bool supported, bool weActive,
        IReadOnlyList<PlaybackDevice> connect, string? activeConnect) =>
        DevicePickerModel.Build(local, selectedLocal, supported, weActive, connect, activeConnect,
            "This computer", "System default", "Spotify Connect", "Unavailable", "No devices", "Open Spotify elsewhere");

    [Fact]
    public void Sections_Headers_Separator_Present()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true) }, selectedLocal: null, supported: true, weActive: true,
            new[] { Connect("phone", "Phone", DeviceKind.Phone) }, activeConnect: null);

        Assert.Equal(2, rows.Count(r => r.Kind == DevicePickerRowKind.Header));
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.Header && r.Label == "This computer");
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.Header && r.Label == "Spotify Connect");
        Assert.Single(rows, r => r.Kind == DevicePickerRowKind.Separator);
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.LocalDefault);
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.LocalDevice && r.Label == "Speakers");
    }

    [Fact]
    public void ThisDeviceConnectRow_FilteredOut()
    {
        var rows = Build(
            System.Array.Empty<LocalAudioDevice>(), null, supported: true, weActive: true,
            new[] { Connect("us", "This PC", DeviceKind.ThisDevice), Connect("phone", "Phone", DeviceKind.Phone) }, activeConnect: null);

        Assert.DoesNotContain(rows, r => r.Kind == DevicePickerRowKind.ConnectDevice && r.Label == "This PC");
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.ConnectDevice && r.Label == "Phone");
    }

    [Fact]
    public void CheckOnCurrentLocalOutput_WhenWeAreActive()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true), Local("dac", "USB DAC") },
            selectedLocal: "dac", supported: true, weActive: true,
            System.Array.Empty<PlaybackDevice>(), activeConnect: null);

        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.LocalDevice && r.Label == "USB DAC" && r.IsChecked);
        Assert.DoesNotContain(rows, r => r.Kind == DevicePickerRowKind.LocalDefault && r.IsChecked);   // a device is current, not "System default"
    }

    [Fact]
    public void SystemDefaultChecked_WhenNoLocalSelected_AndWeActive()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true) }, selectedLocal: null, supported: true, weActive: true,
            System.Array.Empty<PlaybackDevice>(), activeConnect: null);
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.LocalDefault && r.IsChecked);
    }

    [Fact]
    public void Unsupported_DisablesLocalRows_WithUnavailableAccelerator()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true) }, selectedLocal: null, supported: false, weActive: true,
            System.Array.Empty<PlaybackDevice>(), activeConnect: null);

        foreach (var r in rows.Where(r => r.Kind is DevicePickerRowKind.LocalDefault or DevicePickerRowKind.LocalDevice))
        {
            Assert.False(r.Enabled);
            Assert.Equal("Unavailable", r.Accelerator);
            Assert.False(r.IsChecked);
        }
    }

    [Fact]
    public void RemoteActive_NoLocalRowChecked()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true) }, selectedLocal: null, supported: true, weActive: false,
            new[] { Connect("phone", "Phone", DeviceKind.Phone, active: true) }, activeConnect: "phone");

        Assert.DoesNotContain(rows, r => (r.Kind == DevicePickerRowKind.LocalDefault || r.Kind == DevicePickerRowKind.LocalDevice) && r.IsChecked);
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.ConnectDevice && r.Label == "Phone" && r.IsChecked);
    }

    [Fact]
    public void EmptyConnectRoster_ShowsNoDevicesRows()
    {
        var rows = Build(
            new[] { Local("spk", "Speakers", def: true) }, selectedLocal: null, supported: true, weActive: true,
            System.Array.Empty<PlaybackDevice>(), activeConnect: null);
        Assert.Contains(rows, r => r.Kind == DevicePickerRowKind.Empty && r.Label == "No devices");
    }

    // ── Display-name policy: DeviceDesc-first, adapter-suffix strip, width cap, display-audio filter ──

    [Theory]
    [InlineData("Speakers", "Speakers (Qualcomm(R) Aqstic(TM) Audio Adapter Device)", "Speakers")]
    [InlineData(null, "Speakers (Qualcomm(R) Aqstic(TM) Audio Adapter Device)", "Speakers")]
    [InlineData(null, "LC34G55T (2- Qualcomm(R) Aqstic(TM) ACX External Display Audio Device)", "LC34G55T")]
    [InlineData(null, "USB DAC", "USB DAC")]                       // no adapter suffix → unchanged
    [InlineData("  ", "Headphones (Realtek)", "Headphones")]       // blank desc → suffix strip
    [InlineData(null, null, null)]
    public void Shorten_PrefersDeviceDesc_ElseStripsAdapterSuffix(string? desc, string? friendly, string? expected) =>
        Assert.Equal(expected, Wavee.SpotifyLive.Audio.AudioDeviceNaming.Shorten(desc, friendly));

    [Fact]
    public void FilterForPicker_HidesDisplayAudio_AndDisambiguatesDuplicates()
    {
        var eps = new List<Wavee.SpotifyLive.Audio.AudioEndpointInfo>
        {
            new("spk1", "Speakers", Wavee.SpotifyLive.Audio.AudioEndpointFormFactor.Speakers, true, "Speakers (Qualcomm Adapter)"),
            new("mon", "LC34G55T", Wavee.SpotifyLive.Audio.AudioEndpointFormFactor.DigitalAudioDisplayDevice, false, "LC34G55T (ACX Display Audio)"),
            new("spk2", "Speakers", Wavee.SpotifyLive.Audio.AudioEndpointFormFactor.Speakers, false, "Speakers (USB Adapter)"),
        };
        var visible = LocalAudioDeviceService.FilterForPicker(eps);

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, e => e.Id == "mon");
        Assert.Equal("Speakers (Qualcomm Adapter)", visible[0].Name);   // duplicate short names → full names
        Assert.Equal("Speakers (USB Adapter)", visible[1].Name);
    }

    [Fact]
    public void LongLabels_AreCapped_WithEllipsis()
    {
        string longName = new string('x', 80);
        var rows = Build(
            new[] { Local("dev", longName) }, selectedLocal: null, supported: true, weActive: true,
            new[] { Connect("c", longName, DeviceKind.Speaker) }, activeConnect: null);

        foreach (var r in rows.Where(r => r.Kind is DevicePickerRowKind.LocalDevice or DevicePickerRowKind.ConnectDevice))
        {
            Assert.True(r.Label.Length <= DevicePickerModel.MaxLabelChars);
            Assert.EndsWith("…", r.Label);
        }
        Assert.Equal("Speakers", DevicePickerModel.CapLabel("Speakers"));   // short labels untouched
    }
}
