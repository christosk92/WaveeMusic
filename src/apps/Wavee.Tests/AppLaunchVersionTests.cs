using System;
using Wavee;
using Wavee.Core;
using Wavee.Tests;
using Xunit;

// AppLaunchVersion.Arm (App/AppLaunchVersion.cs) — the pure "was this launch an update" decision both the Store and
// the AppInstaller composition paths call, once, before either updater is constructed. AppInstallerUpdateServiceTests
// drives the same decision indirectly through the updater's ctor (which now just consumes Arm's return); these tests
// drive Arm directly, including the ONE behaviour that ctor-level test never had to prove on its own: LastRunVersion
// is written on EVERY call, whether or not this launch turns out to be an update.
public class AppLaunchVersionTests
{
    static WaveeVersionInfo Me(string channel = "stable", string core = "0.2.0", string quad = "0.2.0.5", string codename = "Breaker")
    {
        string semver = channel == "dev" ? core + "-dev" : core;
        return new WaveeVersionInfo(semver, core, null, quad, codename, channel, "abc1234", "2026-08-01T00:00:00Z");
    }

    [Fact]
    public void FirstEverLaunch_IsSilent_ButStillWritesLastRunVersion()
    {
        var settings = new MemoryAppSettings();   // LastRunVersion defaults to "" — nothing has ever run before.

        string from = AppLaunchVersion.Arm(settings, Me(quad: "0.2.0.5"), new CapturingWaveeLog(), "unpackaged");

        Assert.Equal("", from);
        // A fresh install must not greet the user with "Wavee was updated" — nothing was written to arm the plate.
        Assert.False(settings.WasWritten(WaveeSettings.ReleaseNotesPendingFrom));
        Assert.False(settings.WasWritten(WaveeSettings.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.0.5", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void UnchangedVersion_IsSilent_ButStillWritesLastRunVersion()
    {
        // A relaunch of the SAME build: the write happens again (with the same value) but nothing is armed.
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.0.5");

        string from = AppLaunchVersion.Arm(settings, Me(quad: "0.2.0.5"), new CapturingWaveeLog(), "unpackaged");

        Assert.Equal("", from);
        Assert.False(settings.WasWritten(WaveeSettings.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0.5", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void ChangedVersion_ArmsThePlate_AndAdvancesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.0.4");

        string from = AppLaunchVersion.Arm(settings, Me(quad: "0.2.1.5", core: "0.2.1"), new CapturingWaveeLog(), "unpackaged");

        Assert.Equal("0.2.0.4", from);
        // Written TWICE on purpose (see AppLaunchVersion.Arm's remarks): one one-shot arm, one durable fact.
        Assert.Equal("0.2.0.4", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0.4", settings.Get(WaveeSettings.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.1.5", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void Downgrade_IsStillAnUpdate_TheNoticeIsAboutTheChangeNotItsDirection()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.1.5");

        string from = AppLaunchVersion.Arm(settings, Me(quad: "0.2.0.4", core: "0.2.0"), new CapturingWaveeLog(), "unpackaged");

        Assert.Equal("0.2.1.5", from);
        Assert.Equal("0.2.1.5", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.1.5", settings.Get(WaveeSettings.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.0.4", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void DevBuild_NeverClaimsAnUpdate_ButStillAdvancesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.1.9-dev");

        string from = AppLaunchVersion.Arm(settings, Me(channel: "dev", core: "0.2.0", quad: ""), new CapturingWaveeLog(), "unpackaged");

        Assert.Equal("", from);
        Assert.False(settings.WasWritten(WaveeSettings.ReleaseNotesPendingFrom));
        // A dev build's LastRunKey is its semver (Quad is empty), so two dev builds of the same version never look
        // like an update to each other on the NEXT launch either.
        Assert.Equal("0.2.0-dev", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        var settings = new MemoryAppSettings();
        var log = new CapturingWaveeLog();
        var me = Me();
        // Statement bodies, not expression bodies: Arm returns a string, and a value-returning lambda binds to xunit's
        // obsolete Func<> overload ("call Assert.ThrowsAsync") instead of the Action one.
        Assert.Throws<ArgumentNullException>(() => { AppLaunchVersion.Arm(null!, me, log, "unpackaged"); });
        Assert.Throws<ArgumentNullException>(() => { AppLaunchVersion.Arm(settings, null!, log, "unpackaged"); });
        Assert.Throws<ArgumentNullException>(() => { AppLaunchVersion.Arm(settings, me, null!, "unpackaged"); });
    }
}
