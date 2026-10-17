// ── Wavee.Tests/SettingsStoreProbeTests.cs — TryGet / Has / Remove on every settings backing ────────────────────────
//
// The store probes (themes plan §7.1, C0 folded into H0): a copy-only migration step writes a new key only when it is
// absent and the legacy key is present, so every backing must tell "never written" from "written as the default", and
// a removal must leave reads answering the default. One contract, run against the profile file, the registry (a scratch
// key, never Wavee's), the memory overlay and the defaults-only store; then what only one backing has: the file's
// removal surviving a reopen, the overlay's tombstone hiding the inner store's value, the facade announcing a removal.

using System;
using System.IO;
using Microsoft.Win32;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class SettingsStoreProbeTests : IDisposable
{
    static readonly SettingKey<int> Count = new("probe.count", 7);
    static readonly SettingKey<string> Name = new("probe.name", "none");
    static readonly SettingKey<bool> Flag = new("probe.flag", false);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-store-probe-tests", Guid.NewGuid().ToString("n"));
    readonly string _publisher = "WaveeStoreProbeTests-" + Guid.NewGuid().ToString("n");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + _publisher, throwOnMissingSubKey: false); } catch (Exception) { }
    }

    public static TheoryData<string> WritableBackings => ["file", "registry", "overlay"];

    IAppSettings Open(string backing) => backing switch
    {
        "file" => new FileAppSettings(Path.Combine(_dir, "settings.json")),
        "registry" => Platform.OpenRegistrySettingsForTests(_publisher, "Probe"),
        "overlay" => new Diagnostics.Headless.OverlaySettings(DefaultsOnlySettings.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(backing)),
    };

    [Theory]
    [MemberData(nameof(WritableBackings))]
    public void A_key_never_written_is_absent_and_reads_its_default(string backing)
    {
        var store = Open(backing);
        Assert.False(store.Has(Count.Name));
        Assert.False(store.TryGet(Count, out int value));
        Assert.Equal(Count.Default, value);
    }

    [Theory]
    [MemberData(nameof(WritableBackings))]
    public void A_written_key_is_present_with_its_value(string backing)
    {
        var store = Open(backing);
        store.Set(Count, 3);
        store.Set(Name, "x");
        store.Set(Flag, true);
        Assert.True(store.Has(Count.Name));
        Assert.True(store.TryGet(Count, out int count));
        Assert.Equal(3, count);
        Assert.True(store.TryGet(Name, out string name));
        Assert.Equal("x", name);
        Assert.True(store.TryGet(Flag, out bool flag));
        Assert.True(flag);
    }

    [Theory]
    [MemberData(nameof(WritableBackings))]
    public void A_key_written_as_its_default_is_still_present(string backing)
    {
        var store = Open(backing);
        store.Set(Count, Count.Default);
        Assert.True(store.Has(Count.Name));
        Assert.True(store.TryGet(Count, out int value));
        Assert.Equal(Count.Default, value);
    }

    [Theory]
    [MemberData(nameof(WritableBackings))]
    public void A_removed_key_is_absent_and_reads_its_default_and_other_keys_stay(string backing)
    {
        var store = Open(backing);
        store.Set(Count, 3);
        store.Set(Name, "kept");
        store.Remove(Count.Name);
        Assert.False(store.Has(Count.Name));
        Assert.False(store.TryGet(Count, out int value));
        Assert.Equal(Count.Default, value);
        Assert.Equal(Count.Default, store.Get(Count));
        Assert.Equal("kept", store.Get(Name));
        store.Set(Count, 4);
        Assert.Equal(4, store.Get(Count));
    }

    [Theory]
    [MemberData(nameof(WritableBackings))]
    public void Removing_an_absent_key_is_a_no_op(string backing)
    {
        var store = Open(backing);
        store.Remove(Count.Name);
        Assert.False(store.Has(Count.Name));
        Assert.Equal(Count.Default, store.Get(Count));
    }

    [Fact]
    public void The_defaults_only_store_has_nothing_and_removes_nothing()
    {
        var store = DefaultsOnlySettings.Instance;
        store.Set(Count, 3);
        Assert.False(store.Has(Count.Name));
        Assert.False(store.TryGet(Count, out int value));
        Assert.Equal(Count.Default, value);
        store.Remove(Count.Name);
    }

    [Fact]
    public void A_file_removal_survives_a_reopen()
    {
        string path = Path.Combine(_dir, "settings.json");
        var first = new FileAppSettings(path);
        first.Set(Count, 3);
        first.Set(Name, "kept");
        first.Remove(Count.Name);

        var reopened = new FileAppSettings(path);
        Assert.False(reopened.Has(Count.Name));
        Assert.True(reopened.Has(Name.Name));
        Assert.Equal("kept", reopened.Get(Name));
    }

    [Fact]
    public void An_overlay_removal_hides_the_inner_value_without_touching_the_inner_store()
    {
        var inner = new FileAppSettings(Path.Combine(_dir, "inner.json"));
        inner.Set(Count, 3);
        var overlay = new Diagnostics.Headless.OverlaySettings(inner);
        Assert.True(overlay.Has(Count.Name));
        Assert.True(overlay.TryGet(Count, out int seen));
        Assert.Equal(3, seen);

        overlay.Remove(Count.Name);
        Assert.False(overlay.Has(Count.Name));
        Assert.False(overlay.TryGet(Count, out int hidden));
        Assert.Equal(Count.Default, hidden);
        Assert.Equal(Count.Default, overlay.Get(Count));
        Assert.True(inner.Has(Count.Name));
        Assert.Equal(3, inner.Get(Count));

        overlay.Set(Count, 5);
        Assert.True(overlay.Has(Count.Name));
        Assert.Equal(5, overlay.Get(Count));
        Assert.Equal(3, inner.Get(Count));
    }

    [Fact]
    public void The_facade_probes_its_backing_and_announces_a_removal()
    {
        var backing = new MemoryAppSettings();
        Platform.UseSettings(backing);
        try
        {
            Assert.False(Platform.Settings.Has(Count.Name));
            Platform.Settings.Set(Count, 3);
            Assert.True(Platform.Settings.Has(Count.Name));
            Assert.True(Platform.Settings.TryGet(Count, out int value));
            Assert.Equal(3, value);

            uint before = Platform.SettingsEpoch;
            Platform.Settings.Remove(Count.Name);
            Assert.False(backing.Has(Count.Name));
            Assert.Equal(before + 1, Platform.SettingsEpoch);
            Assert.Equal(Platform.SettingsEpoch, Platform.SettingsChanged.Peek());
        }
        finally { Platform.UseSettings(null); }
    }

    [Fact]
    public void The_facade_without_a_backing_has_nothing()
    {
        Platform.UseSettings(null);
        Assert.False(Platform.Settings.Has(Count.Name));
        Assert.False(Platform.Settings.TryGet(Count, out int value));
        Assert.Equal(Count.Default, value);
    }
}
