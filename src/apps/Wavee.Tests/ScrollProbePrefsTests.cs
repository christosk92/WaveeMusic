// ── Wavee.Tests/ScrollProbePrefsTests.cs — the scroll probe's persisted settings (C15, X38) ──────────────────────────
//
// The decisions (clamp, profile index, file name) are `ScrollDiagRules` (ScrollDiagRulesTests); these facts pin the
// persistence around them: what boot reads back, and that each verb persists, then publishes, then applies. The probe
// level is engine process state, so the class joins the platform collection and restores it.

using FluentGpu.Scroll.Diag;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ScrollProbePrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();
    readonly ProbeLevel _probeLevel = ScrollProbe.Level;
    readonly int _level = ScrollProbePrefs.Level.Peek();
    readonly int _profile = ScrollProbePrefs.Profile.Peek();

    public ScrollProbePrefsTests() => Platform.UseSettings(_store);

    public void Dispose()
    {
        ScrollProbe.Level = _probeLevel;
        ScrollProbePrefs.Level.Value = _level;
        ScrollProbePrefs.Profile.Value = _profile;
        Platform.UseSettings(null);
    }

    [Fact]
    public void The_profile_names_are_the_engines_presets_in_order_and_never_empty()
    {
        var names = ScrollProbePrefs.ProfileNames;

        Assert.NotEmpty(names);
        Assert.Equal(ScrollDiagRules.DefaultProfile, names[0]);
    }

    [Fact]
    public void Boot_reads_the_stored_level_and_profile_and_applies_the_level_to_the_probe()
    {
        _store.Set(Platform.Keys.ScrollProbeLevel, ScrollDiagRules.LevelTrace);
        _store.Set(Platform.Keys.ScrollFeelProfile, ScrollProbePrefs.ProfileNames[0]);

        ScrollProbePrefs.Load(_store);

        Assert.Equal(ScrollDiagRules.LevelTrace, ScrollProbePrefs.Level.Peek());
        Assert.Equal(ProbeLevel.Trace, ScrollProbe.Level);
        Assert.Equal(0, ScrollProbePrefs.Profile.Peek());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void A_stored_level_outside_the_range_boots_as_summary(int stored)
    {
        _store.Set(Platform.Keys.ScrollProbeLevel, stored);

        ScrollProbePrefs.Load(_store);

        Assert.Equal(ScrollDiagRules.LevelSummary, ScrollProbePrefs.Level.Peek());
        Assert.Equal(ProbeLevel.Summary, ScrollProbe.Level);
    }

    [Fact]
    public void A_retired_profile_name_boots_as_the_first_profile()
    {
        _store.Set(Platform.Keys.ScrollFeelProfile, "WinUiExact");

        ScrollProbePrefs.Load(_store);

        Assert.Equal(0, ScrollProbePrefs.Profile.Peek());
    }

    [Fact]
    public void Setting_the_level_persists_the_clamped_value_publishes_it_and_applies_it()
    {
        ScrollProbePrefs.SetLevel(ScrollDiagRules.LevelOff);
        Assert.Equal(ScrollDiagRules.LevelOff, _store.Get(Platform.Keys.ScrollProbeLevel));
        Assert.Equal(ScrollDiagRules.LevelOff, ScrollProbePrefs.Level.Peek());
        Assert.Equal(ProbeLevel.Off, ScrollProbe.Level);

        ScrollProbePrefs.SetLevel(42);                                              // a hand-edited value never reaches the store
        Assert.Equal(ScrollDiagRules.LevelSummary, _store.Get(Platform.Keys.ScrollProbeLevel));
        Assert.Equal(ProbeLevel.Summary, ScrollProbe.Level);
    }

    [Fact]
    public void Setting_a_profile_persists_its_name_and_publishes_its_index()
    {
        ScrollProbePrefs.SetProfile(0);

        Assert.Equal(ScrollProbePrefs.ProfileNames[0], _store.Get(Platform.Keys.ScrollFeelProfile));
        Assert.Equal(0, ScrollProbePrefs.Profile.Peek());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void A_profile_index_outside_the_list_changes_nothing(int index)
    {
        ScrollProbePrefs.SetProfile(index);

        Assert.False(_store.WasWritten(Platform.Keys.ScrollFeelProfile));
    }
}
