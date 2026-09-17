using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins the Storage and About tabs' engine-free decisions (ch 27 W16, W17, W19, W20): the usage bar's category
/// order and zero-total rule, the budget meter, the About hero's state matrix, and the provenance line's three shapes.
/// Plain data — no engine, no settings store, no disk.</summary>
public class SettingsStorageTests
{
    static Settings.StorageSnapshot Snapshot(long library = 0, long runtime = 0, long logs = 0, long store = 0,
                                             long audio = 0, long keys = 0, long images = 0)
        => new(library, runtime, logs, 3, store, audio, keys, images, library + runtime + logs + store + audio + keys + images);

    [Fact]
    public void Parts_FollowTheSevenHueOrder()
    {
        var s = Snapshot(library: 1, runtime: 2, logs: 3, store: 4, audio: 5, keys: 6, images: 7);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, Settings.StorageRules.Parts(s));
    }

    [Fact]
    public void ShowsBar_OnlyWhenSomeCategoryHasBytes()
    {
        Assert.False(Settings.StorageRules.ShowsBar(Snapshot()));
        Assert.True(Settings.StorageRules.ShowsBar(Snapshot(images: 1)));
        Assert.False(Settings.StorageRules.ShowsBar(Snapshot(library: -5)));   // a negative size is not a segment
    }

    [Fact]
    public void BudgetMeter_UnlimitedOrUnknown_IsEmptyAndNeverOver()
    {
        Assert.Equal((0f, false), Settings.StorageRules.BudgetMeter(5L << 30, null));
        Assert.Equal((0f, false), Settings.StorageRules.BudgetMeter(0, 1L << 30));
    }

    [Fact]
    public void BudgetMeter_IsTheUsedShare_ClampedAndErrorWhenOver()
    {
        Assert.Equal((0.5f, false), Settings.StorageRules.BudgetMeter(512L << 20, 1L << 30));
        Assert.Equal((1f, false), Settings.StorageRules.BudgetMeter(1L << 30, 1L << 30));
        Assert.Equal((1f, true), Settings.StorageRules.BudgetMeter(2L << 30, 1L << 30));
    }

    [Fact]
    public void Hero_EveryStateHasAPillAndAButton()
    {
        foreach (var state in Enum.GetValues<AppUpdateState>())
        foreach (bool inert in new[] { false, true })
        foreach (bool store in new[] { false, true })
        {
            var shape = Settings.AboutRules.Hero(state, inert, store);
            Assert.False(string.IsNullOrEmpty(shape.PillKey), $"{state} inert={inert} store={store} has no pill");
            Assert.False(string.IsNullOrEmpty(shape.ButtonKey), $"{state} inert={inert} store={store} has no button");
            Assert.Equal(shape.Tone == Settings.AboutRules.HeroTone.Disabled, shape.Verb == Settings.AboutRules.HeroVerb.None);
        }
    }

    [Fact]
    public void Hero_StateMatrix_MatchesW20()
    {
        var H = Settings.AboutRules.HeroTone.Accent;
        var S = Settings.AboutRules.HeroTone.Standard;
        var D = Settings.AboutRules.HeroTone.Disabled;
        Expect(AppUpdateState.None, Strings.Update.About.PillUpToDate, false, Strings.Update.Action.Check, S, Settings.AboutRules.HeroVerb.Check);
        Expect(AppUpdateState.Checking, Strings.Update.About.PillChecking, false, Strings.Update.State.Checking, D, Settings.AboutRules.HeroVerb.None);
        Expect(AppUpdateState.Available, Strings.Update.About.PillAvailable, true, Strings.Update.Action.UpdateNow, H, Settings.AboutRules.HeroVerb.UpdateNow);
        Expect(AppUpdateState.Snoozed, Strings.Update.About.PillSnoozed, false, Strings.Update.Action.UpdateNow, H, Settings.AboutRules.HeroVerb.UpdateNow);
        Expect(AppUpdateState.Downloading, Strings.Update.About.PillDownloading, true, Strings.Update.State.Installing, D, Settings.AboutRules.HeroVerb.None);
        Expect(AppUpdateState.Installing, Strings.Update.About.PillInstalling, true, Strings.Update.State.Installing, D, Settings.AboutRules.HeroVerb.None);
        Expect(AppUpdateState.Completed, Strings.Update.About.PillUpdated, false, Strings.Update.Action.Check, S, Settings.AboutRules.HeroVerb.Check);
        Expect(AppUpdateState.Failed, Strings.Update.About.PillFailed, false, Strings.Update.Action.Retry, H, Settings.AboutRules.HeroVerb.Retry);

        static void Expect(AppUpdateState state, string pill, bool accent, string button,
                           Settings.AboutRules.HeroTone tone, Settings.AboutRules.HeroVerb verb)
            => Assert.Equal(new Settings.AboutRules.HeroShape(pill, accent, button, tone, verb),
                            Settings.AboutRules.Hero(state, inert: false, isStore: false));
    }

    [Fact]
    public void Hero_DevBuildIsInert_AndBeatsTheStateAndTheStore()
    {
        foreach (var state in Enum.GetValues<AppUpdateState>())
            Assert.Equal(new Settings.AboutRules.HeroShape(Strings.Settings.About.DevBuild, false, Strings.Update.Action.Check,
                    Settings.AboutRules.HeroTone.Disabled, Settings.AboutRules.HeroVerb.None),
                Settings.AboutRules.Hero(state, inert: true, isStore: true));
    }

    [Fact]
    public void Hero_StoreBuild_SaysNotCheckedYet_AndOpensTheStore_WhateverTheState()
    {
        foreach (var state in Enum.GetValues<AppUpdateState>())
            Assert.Equal(new Settings.AboutRules.HeroShape(Strings.Update.About.NeverChecked, false, Strings.Update.Store.Open,
                    Settings.AboutRules.HeroTone.Standard, Settings.AboutRules.HeroVerb.UpdateNow),
                Settings.AboutRules.Hero(state, inert: false, isStore: true));
    }

    [Fact]
    public void Provenance_HasThreeShapes_AndNeverALeadingSeparator()
    {
        static string Built(string d) => "Built " + d;
        static string Checked(string w) => "last checked " + w;
        const long ms = 1_788_000_000_000;

        Assert.Equal("not checked yet", Settings.AboutRules.Provenance("", 0, Built, Checked, "not checked yet"));
        Assert.Equal("not checked yet", Settings.AboutRules.Provenance(null, 0, Built, Checked, "not checked yet"));
        Assert.Equal("Built 2026-08-29  ·  not checked yet", Settings.AboutRules.Provenance("2026-08-29", 0, Built, Checked, "not checked yet"));
        Assert.Equal("Built 2026-08-29  ·  last checked " + Settings.AboutRules.Stamp(ms),
            Settings.AboutRules.Provenance("2026-08-29", ms, Built, Checked, "not checked yet"));
        Assert.Equal("last checked " + Settings.AboutRules.Stamp(ms), Settings.AboutRules.Provenance("", ms, Built, Checked, "never"));
    }
}
