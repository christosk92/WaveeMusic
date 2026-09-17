// ── Wavee.Tests/UpdateHostTests.cs — the updater's rules and its state machine (Platform/Update.Host.cs) ──────────────
//
// Ported from _old/Wavee.Tests/{AppInstallerUpdateServiceTests, AppLaunchVersionTests, AppUpdateSeamTests,
// ShutdownUpdatePolicyTests}.cs against the 0.3 names. The service is driven over a scripted HttpMessageHandler and a fake
// IPackageUpdater: nothing here touches the network, the registry, the deployment API, Platform.Version or the engine
// loop. The classes that exercise code which writes the app log carry the platform collection (Log is process state).

using System.Net;
using System.Text;
using FluentGpu.WindowsApi.Packaging;
using Xunit;

namespace Wavee.Tests;

// ── the launch-version arming (0.2.9 AppLaunchVersionTests) ──────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class UpdateLaunchVersionTests
{
    [Fact]
    public void FirstEverLaunch_IsSilent_ButStillWritesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        string from = Update.LaunchVersion.Arm(settings, isDev: false, "0.2.0.5", "unpackaged", "");
        Assert.Equal("", from);
        Assert.False(settings.WasWritten(Platform.Keys.ReleaseNotesPendingFrom));
        Assert.False(settings.WasWritten(Platform.Keys.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.0.5", settings.Get(Platform.Keys.LastRunVersion));
    }

    [Fact]
    public void UnchangedVersion_IsSilent_ButStillWritesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.0.5");
        string from = Update.LaunchVersion.Arm(settings, isDev: false, "0.2.0.5", "unpackaged", "");
        Assert.Equal("", from);
        Assert.False(settings.WasWritten(Platform.Keys.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0.5", settings.Get(Platform.Keys.LastRunVersion));
    }

    [Fact]
    public void ChangedVersion_ArmsThePlate_AndAdvancesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.0.4");
        string from = Update.LaunchVersion.Arm(settings, isDev: false, "0.2.1.5", "unpackaged", "");
        Assert.Equal("0.2.0.4", from);
        // Written TWICE on purpose: the one-shot the plate consumes, and the durable fact nobody clears.
        Assert.Equal("0.2.0.4", settings.Get(Platform.Keys.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0.4", settings.Get(Platform.Keys.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.1.5", settings.Get(Platform.Keys.LastRunVersion));
    }

    [Fact]
    public void Downgrade_IsStillAnUpdate_TheNoticeIsAboutTheChangeNotItsDirection()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.1.5");
        string from = Update.LaunchVersion.Arm(settings, isDev: false, "0.2.0.4", "unpackaged", "");
        Assert.Equal("0.2.1.5", from);
        Assert.Equal("0.2.1.5", settings.Get(Platform.Keys.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0.4", settings.Get(Platform.Keys.LastRunVersion));
    }

    [Fact]
    public void DevBuild_NeverClaimsAnUpdate_ButStillAdvancesLastRunVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.1.9-dev");
        string from = Update.LaunchVersion.Arm(settings, isDev: true, "0.2.0-dev", "unpackaged", "");
        Assert.Equal("", from);
        Assert.False(settings.WasWritten(Platform.Keys.ReleaseNotesPendingFrom));
        Assert.Equal("0.2.0-dev", settings.Get(Platform.Keys.LastRunVersion));
    }

    [Fact]
    public void NullSettings_AreRejected()
        => Assert.Throws<ArgumentNullException>(() => { Update.LaunchVersion.Arm(null!, false, "0.2.0.5", "unpackaged", ""); });
}

// ── the pure rules ───────────────────────────────────────────────────────────────────────────────────────────────────

public class UpdateRulesTests
{
    const string GitHubRoot = "https://github.com/christosk92/WaveeMusic/releases/download/";

    [Fact]
    public void FeedUrl_UsesTheStampedFeedReleaseAndArch()
        => Assert.Equal(GitHubRoot + "wavee-stable-test/Wavee.arm64.appinstaller",
            Update.FeedUrlFor(GitHubRoot, "wavee-stable-test", "stable", "arm64"));

    [Fact]
    public void FeedUrl_BetaChannelUsesTheBetaAssetPrefix()
        => Assert.EndsWith("/wavee-beta/Wavee.Beta.x64.appinstaller", Update.FeedUrlFor(GitHubRoot, "wavee-beta", "beta", "x64"));

    [Fact]
    public void FeedUrl_IsBuiltUnderTheStampedBaseUrl()
        // The local E2E packs a build whose feed is a loopback server: the SAME code path, a different stamp.
        => Assert.Equal("http://127.0.0.1:8099/wavee-local/Wavee.x64.appinstaller",
            Update.FeedUrlFor("http://127.0.0.1:8099/", "wavee-local", "stable", "x64"));

    [Fact]
    public void FeedUrl_AnUnknownArchIsX64()
        => Assert.EndsWith("/Wavee.x64.appinstaller", Update.FeedUrlFor(GitHubRoot, "wavee-stable", "stable", " "));

    [Fact]
    public void ReleasePage_IsTheTagOfTheVersionTheSnapshotNames()
    {
        var withSemver = AppUpdateSnapshot.Idle with { State = AppUpdateState.Failed, TargetQuad = "0.3.0.17", TargetSemVer = "0.3.0" };
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.3.0", Update.ReleasePageUrl(withSemver));
        var quadOnly = AppUpdateSnapshot.Idle with { State = AppUpdateState.Available, TargetQuad = "0.2.1.6" };
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.1", Update.ReleasePageUrl(quadOnly));
    }

    [Fact]
    public void ReleasePage_WithNothingToNameIsTheListing()
    {
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases", Update.ReleasePageUrl(null));
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases", Update.ReleasePageUrl(AppUpdateSnapshot.Idle));
    }

    [Fact]
    public void StorePage_IsTheStoreAppProductPage()
        => Assert.Equal("ms-windows-store://pdp/?productid=9NJPVWTQPT9H", Update.StorePageUrl("9NJPVWTQPT9H"));

    [Fact]
    public void ExceptionCode_IsTheTypeAndHResult_NeverTheMessage()
    {
        var ex = new InvalidOperationException("this text does not exist in a trimmed build");
        string code = Update.ExceptionCode(ex);
        Assert.Equal("InvalidOperationException 0x" + ex.HResult.ToString("X8"), code);
        Assert.DoesNotContain("does not exist", code);
    }

    [Theory]
    [InlineData(null, "0.2.0.5", false, "", Update.CheckVerdict.NoVersion)]
    [InlineData("", "0.2.0.5", false, "", Update.CheckVerdict.NoVersion)]
    [InlineData("0.2.1.6", "0.2.0.5", false, "", Update.CheckVerdict.Available)]
    [InlineData("0.2.1.6", "0.2.0.5", false, "0.2.1.6", Update.CheckVerdict.Snoozed)]
    [InlineData("0.2.2.7", "0.2.0.5", false, "0.2.1.6", Update.CheckVerdict.Available)]   // snooze is per version
    [InlineData("9.9.9.9", "", true, "", Update.CheckVerdict.UpToDate)]                  // a dev build is never prompted
    [InlineData("0.2.0.5", "0.2.0.5", false, "", Update.CheckVerdict.UpToDate)]
    [InlineData("0.2.0.4", "0.2.0.5", false, "", Update.CheckVerdict.UpToDate)]          // never backwards
    public void Judge_MapsAFeedAnswerToAVerdict(string? remote, string running, bool isDev, string snoozed, Update.CheckVerdict expected)
        => Assert.Equal(expected, Update.Judge(remote, running, isDev, snoozed));

    [Theory]
    [InlineData(AppUpdateState.Available, true)]
    [InlineData(AppUpdateState.Snoozed, true)]
    [InlineData(AppUpdateState.Failed, true)]
    [InlineData(AppUpdateState.None, false)]
    [InlineData(AppUpdateState.Checking, false)]
    [InlineData(AppUpdateState.Downloading, false)]
    [InlineData(AppUpdateState.Installing, false)]
    [InlineData(AppUpdateState.Completed, false)]
    public void CanApply_OnlyAPendingOrFailedTarget(AppUpdateState state, bool expected)
        => Assert.Equal(expected, Update.CanApply(state));

    [Fact]
    public void Metered_BlocksOnlyWithoutConsent()
    {
        Assert.True(Update.MeteredBlocks(metered: true, allowOnMetered: false));
        Assert.False(Update.MeteredBlocks(metered: true, allowOnMetered: true));
        Assert.False(Update.MeteredBlocks(metered: false, allowOnMetered: false));
    }

    [Fact]
    public void Route_TheOneDoorForToastRowAndAbout()
    {
        var failedCheck = AppUpdateSnapshot.Idle with { State = AppUpdateState.Failed };
        var failedApply = failedCheck with { TargetQuad = "0.2.1.6" };
        Assert.Equal(Update.Verb.Apply, Update.Route(UpdateRowAction.UpdateNow, failedApply, isStore: false));
        Assert.Equal(Update.Verb.OpenStorePage, Update.Route(UpdateRowAction.UpdateNow, AppUpdateSnapshot.Idle, isStore: true));
        // Retry re-does what FAILED: a check failure has no target to apply.
        Assert.Equal(Update.Verb.Check, Update.Route(UpdateRowAction.Retry, failedCheck, isStore: false));
        Assert.Equal(Update.Verb.Apply, Update.Route(UpdateRowAction.Retry, failedApply, isStore: false));
        Assert.Equal(Update.Verb.Snooze, Update.Route(UpdateRowAction.Later, failedApply, isStore: false));
        Assert.Equal(Update.Verb.Acknowledge, Update.Route(UpdateRowAction.Dismiss, failedApply, isStore: false));
        Assert.Equal(Update.Verb.OpenReleasePage, Update.Route(UpdateRowAction.OpenReleasePage, failedApply, isStore: false));
        Assert.Equal(Update.Verb.None, Update.Route(UpdateRowAction.WhatsNew, failedApply, isStore: false));   // the shell navigates
    }

    [Fact]
    public void Schedule_TheLaunchCheckHasAnHourCooldown()
    {
        const long now = 1_800_000_000_000;
        Assert.True(Update.Schedule.ShouldCheckAtLaunch(0, now));                                   // never checked
        Assert.False(Update.Schedule.ShouldCheckAtLaunch(now - 5 * 60_000, now));                    // five restarts ≠ five feed hits
        Assert.True(Update.Schedule.ShouldCheckAtLaunch(now - Update.Schedule.LaunchCheckCooldownMs, now));
        Assert.True(Update.Schedule.ShouldCheckAtLaunch(now + 60_000, now));                        // the clock went backwards
        Assert.Equal(TimeSpan.FromSeconds(30), Update.Schedule.FirstDelay);
        Assert.Equal(TimeSpan.FromHours(24), Update.Schedule.Interval);
    }

    // 0.2.9 AppUpdateSeamTests: None contributes nothing; every other state is ONE pinned, unread row.
    [Fact]
    public void FeedRow_None_ContributesNoNotification()
    {
        Assert.Null(Update.FeedRow(AppUpdateSnapshot.Idle));
        var feed = Notify.Merge(Update.FeedRow(AppUpdateSnapshot.Idle), [], 0, [], 0, []);
        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.Unread);
    }

    [Theory]
    [InlineData(AppUpdateState.Checking)]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    [InlineData(AppUpdateState.Downloading)]
    [InlineData(AppUpdateState.Installing)]
    [InlineData(AppUpdateState.Completed)]
    [InlineData(AppUpdateState.Failed)]
    public void FeedRow_EachState_IsAPinnedUnreadRow(AppUpdateState state)
    {
        var snapshot = AppUpdateSnapshot.Idle with
        {
            State = state, TargetQuad = "9.9.9.9", TargetSemVer = "9.9.9",
            Failure = state == AppUpdateState.Failed ? new AppUpdateFailure(AppUpdateFailureKind.Network, 0, "network") : null,
        };
        var feed = Notify.Merge(Update.FeedRow(snapshot), [], 0, [], 0, []);
        var row = Assert.Single(feed.Items);
        Assert.Equal(NotifyCategory.AppUpdate, row.Category);
        Assert.Equal(NotifyRows.UpdatePin, row.TimestampMs);
        Assert.True(row.IsUnread);
        Assert.Equal(state, row.Update?.State);
        Assert.Equal("9.9.9.9", row.Update?.TargetQuad);
        Assert.Equal(1, feed.Unread);
    }

    [Theory]
    [InlineData(PackageUpdateFailureKind.Network, AppUpdateFailureKind.Network)]
    [InlineData(PackageUpdateFailureKind.Metered, AppUpdateFailureKind.Metered)]
    [InlineData(PackageUpdateFailureKind.PackagesInUse, AppUpdateFailureKind.PackagesInUse)]
    [InlineData(PackageUpdateFailureKind.VersionConflict, AppUpdateFailureKind.VersionConflict)]
    [InlineData(PackageUpdateFailureKind.SideloadPolicy, AppUpdateFailureKind.SideloadPolicy)]
    [InlineData(PackageUpdateFailureKind.AppInstallerOutdated, AppUpdateFailureKind.AppInstallerOutdated)]
    [InlineData(PackageUpdateFailureKind.NotAssociated, AppUpdateFailureKind.NotAssociated)]
    [InlineData(PackageUpdateFailureKind.Unknown, AppUpdateFailureKind.Unknown)]
    public void MapFailure_IsTotalAcrossBothTaxonomies(PackageUpdateFailureKind kind, AppUpdateFailureKind expected)
        => Assert.Equal(expected, Update.MapFailure(kind));

    [Fact]
    public void MapFailure_CoversEveryDeploymentKind_ByName()
    {
        foreach (PackageUpdateFailureKind kind in Enum.GetValues<PackageUpdateFailureKind>())
            Assert.Equal(kind.ToString(), Update.MapFailure(kind).ToString());
    }
}

// ── the developer simulation's walk (0.2.9 FakeAppUpdateService) ─────────────────────────────────────────────────────

public class UpdateSimulationTests
{
    [Fact]
    public void TheWalk_IsCheckingAvailableDownloadingInstallingCompleted()
    {
        var states = new List<AppUpdateState>();
        var progress = new List<int>();
        int totalMs = 0;
        for (int step = 0; step < 100; step++)
        {
            var s = Update.Simulation.At(step, 1_000);
            states.Add(s.State);
            if (s.State == AppUpdateState.Downloading) progress.Add(s.ProgressPercent);
            int delay = Update.Simulation.DelayAfterMs(step);
            if (delay < 0) break;
            totalMs += delay;
        }
        Assert.Equal(AppUpdateState.Checking, states[0]);
        Assert.Equal(AppUpdateState.Available, states[1]);
        Assert.Equal(AppUpdateState.Installing, states[^2]);
        Assert.Equal(AppUpdateState.Completed, states[^1]);
        Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 5), progress);   // 0…100 by 5
        Assert.Equal(Update.Simulation.CompletedStep, states.Count - 1);
        Assert.Equal(600 + 1500 + 21 * 150 + 900, totalMs);
    }

    [Fact]
    public void TheWalk_NamesAnImplausibleBuild_AndUpdateNowJumpsToTheDownload()
    {
        var completed = Update.Simulation.At(Update.Simulation.CompletedStep, 42);
        Assert.Equal("99.9.9.999", completed.TargetQuad);
        Assert.Equal("Simulated", completed.TargetCodename);
        Assert.True(completed.AutoUpdateAssociated);
        Assert.Equal(42, completed.LastCheckedMs);
        var apply = Update.Simulation.At(Update.Simulation.ApplyStep, 42);
        Assert.Equal(AppUpdateState.Downloading, apply.State);
        Assert.Equal(0, apply.ProgressPercent);
    }
}

// ── the install-on-quit decision, the full 0.2.9 fact set ────────────────────────────────────────────────────────────

public class UpdateShutdownPolicyTests
{
    [Theory]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    public void On_WithAWaitingTarget_Applies(AppUpdateState state)
        => Assert.True(Notify.ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, state));

    [Theory]
    [InlineData(AppUpdateState.None)]
    [InlineData(AppUpdateState.Checking)]
    [InlineData(AppUpdateState.Downloading)]
    [InlineData(AppUpdateState.Installing)]
    [InlineData(AppUpdateState.Completed)]
    [InlineData(AppUpdateState.Failed)]   // a quiet retry that fails again is invisible
    public void On_WithNothingWaiting_DoesNothing(AppUpdateState state)
        => Assert.False(Notify.ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, state));

    [Theory]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    [InlineData(AppUpdateState.Failed)]
    [InlineData(AppUpdateState.None)]
    public void Off_NeverApplies(AppUpdateState state)
        => Assert.False(Notify.ShutdownUpdatePolicy.ShouldApply(installOnQuit: false, state));

    [Theory]
    [InlineData(AppUpdateState.Installing, true)]
    [InlineData(AppUpdateState.Failed, true)]
    [InlineData(AppUpdateState.Available, false)]     // the bounded wait gave up; still offered on the next launch
    [InlineData(AppUpdateState.Downloading, false)]
    [InlineData(AppUpdateState.None, false)]
    public void IsSettled_OnlyForTheTwoEndings(AppUpdateState state, bool settled)
        => Assert.Equal(settled, Notify.ShutdownUpdatePolicy.IsSettled(state));
}

// ── the version arithmetic the check decides with (the 0.2.9 cases NotifyTests' condensed port does not carry) ──────

public class UpdateFeedVersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.1.1.42", "0.1.1.7")]        // the 4th part compares numerically, not lexically
    [InlineData("0.1.10", "0.1.9")]
    [InlineData("2.0.0.0", "1.99.99.99")]
    [InlineData("0.1.2", "0.1.1-dev")]         // a dev build still learns a release is out
    [InlineData("0.1.2+abc123", "0.1.1")]
    [InlineData("0.1.2-rc.1", "0.1.1")]
    [InlineData("  0.1.2  ", "0.1.1")]
    public void IsNewer_True(string remote, string current) => Assert.True(Notify.AppUpdateVersion.IsNewer(remote, current));

    [Theory]
    [InlineData("0.1.1.0", "0.1.1")]
    [InlineData("0.1.1", "0.1.1.1")]
    [InlineData("0.1.1-dev", "0.1.1")]
    [InlineData("0.1.1", "0.1.1-dev")]
    [InlineData("0.1.2", "")]
    [InlineData("0.1.2", null)]
    [InlineData("0.1.2", "1.x.3")]
    [InlineData("0.1.2", "1..2")]
    [InlineData(null, "0.1.1")]
    [InlineData("1.2.3.", "0.1.1")]
    [InlineData("-1.2.3", "0.1.1")]
    [InlineData("dev", "dev")]
    public void IsNewer_False(string? remote, string? current) => Assert.False(Notify.AppUpdateVersion.IsNewer(remote, current));

    [Theory]
    [InlineData("0.1.1-dev", "0.1.1")]
    [InlineData("v2.3.4+meta", "2.3.4")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("dev", "dev")]
    [InlineData("", "")]
    public void ReleaseTagVersion(string version, string expected)
        => Assert.Equal(expected, Notify.AppUpdateVersion.ReleaseTagVersion(version));

    [Fact]
    public void FirstRunAfterUpdate_IsAnExactStampCompare_InBothDirections()
    {
        Assert.True(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("0.1.2", "0.1.1"));
        Assert.True(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.1.0"));
        Assert.False(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", ""));
        Assert.False(Notify.AppUpdateVersion.IsFirstRunAfterUpdate(null, "0.1.1"));
    }
}

// ── the service (0.2.9 AppInstallerUpdateServiceTests), over a scripted feed and a fake deployment seam ───────────────

[Collection(PlatformCollection.Name)]
public class UpdateServiceTests
{
    const string Root = "https://github.com/christosk92/WaveeMusic/releases/download/";

    // ── the one-shot "you were updated" notice ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_RaisesCompleted_WhenTheQuadMovedSinceTheLastRun()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.0.4");
        var svc = Build(out _, out _, out _, settingsIn: settings, quad: "0.2.1.5", core: "0.2.1");
        Assert.Equal(AppUpdateState.Completed, svc.Current.State);
        Assert.Equal("0.2.1.5", svc.Current.TargetQuad);
        Assert.Equal("0.2.1", svc.Current.TargetSemVer);
        Assert.Equal("Breaker", svc.Current.TargetCodename);
    }

    [Fact]
    public void Ctor_LeavesPreviousVersionAloneWhenNothingChanged()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.0.5");
        settings.Set(Platform.Keys.ReleaseNotesPreviousVersion, "0.1.9.3");
        var svc = Build(out _, out _, out _, settingsIn: settings, quad: "0.2.0.5");
        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("0.1.9.3", settings.Get(Platform.Keys.ReleaseNotesPreviousVersion));
        Assert.Equal("", settings.Get(Platform.Keys.ReleaseNotesPendingFrom));
    }

    [Fact]
    public void Ctor_ADevBuildNeverClaimsItWasUpdated()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.1.9-dev");
        var svc = Build(out _, out _, out _, settingsIn: settings, isDev: true, quad: "", core: "0.2.0");
        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("0.2.0-dev", settings.Get(Platform.Keys.LastRunVersion));
    }

    // ── check ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_NewerFeed_IsAvailable()
    {
        var svc = Build(out var http, out var settings, out _, quad: "0.2.0.5");
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Available, svc.Current.State);
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
        Assert.Null(svc.Current.Failure);
        Assert.True(settings.Get(Platform.Keys.UpdateLastCheckedMs) > 0);
        Assert.Equal(svc.FeedUrl, http.LastUrl);
    }

    [Fact]
    public async Task Check_SnoozedQuad_IsSnoozedNotAvailable()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out _, settingsIn: settings);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Snoozed, svc.Current.State);
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
    }

    [Fact]
    public async Task Check_SnoozeIsPerVersion_ANewerReleaseStillShouts()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out _, settingsIn: settings);
        http.Feed("0.2.2.7");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Available, svc.Current.State);
    }

    [Fact]
    public async Task Check_ADevBuildIsNeverPrompted()
    {
        var svc = Build(out var http, out _, out _, isDev: true, quad: "", core: "0.2.0");
        http.Feed("9.9.9.9");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Theory]
    [InlineData(UpdateCheckOrigin.Scheduled, true)]
    [InlineData(UpdateCheckOrigin.User, false)]
    public async Task Check_FeedMissing_IsANetworkFailure_QuietOnlyWhenScheduled(UpdateCheckOrigin origin, bool quiet)
    {
        var svc = Build(out var http, out _, out _);
        http.Answer(HttpStatusCode.NotFound, "not found");
        await svc.CheckAsync(origin, CancellationToken.None);
        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
        Assert.Equal(quiet, svc.Current.Quiet);
    }

    [Fact]
    public async Task Check_MalformedFeed_Fails()
    {
        var svc = Build(out var http, out _, out _);
        http.Answer(HttpStatusCode.OK, "<AppInstaller Version=");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
    }

    [Fact]
    public async Task Check_WrongRootElement_IsANetworkFailure()
    {
        var svc = Build(out var http, out _, out _);
        http.Answer(HttpStatusCode.OK, "<Error><Code>NoSuchKey</Code></Error>");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
    }

    [Fact]
    public async Task Check_UpToDate_DoesNotEatTheCompletedNotice()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastRunVersion, "0.2.0.4");
        var svc = Build(out var http, out _, out _, settingsIn: settings, quad: "0.2.1.5");
        http.Feed("0.2.1.5");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Completed, svc.Current.State);
    }

    [Fact]
    public async Task Check_UpToDateFromIdle_IsIdle()
    {
        var svc = Build(out var http, out _, out _, quad: "0.2.1.5");
        http.Feed("0.2.1.5");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Fact]
    public async Task Check_AFailure_RecordsTheExceptionCode_NotAResourceKey()
    {
        var svc = Build(out var http, out _, out _);
        http.Throw(new HttpRequestException("boom"));
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
        Assert.StartsWith("HttpRequestException 0x", svc.Current.Failure!.Message);
    }

    [Fact]
    public async Task Check_Cancelled_RestoresWhatWasKnownBeforeTheSpinner()
    {
        var svc = Build(out var http, out _, out _);
        http.Feed("0.2.1.6");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await svc.CheckAsync(UpdateCheckOrigin.User, cts.Token);
        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Fact]
    public async Task Check_TheIndexNamesTheRelease_AndTheNotesArePrefetchedOnlyWhenUnmetered()
    {
        var prefetched = new List<string>();
        foreach (bool metered in new[] { false, true })
        {
            var svc = Build(out var http, out _, out _, metered: metered,
                peekIndex: q => new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.2.1", Name = "Comet", PackageVersion = q },
                prefetchNotes: prefetched.Add);
            http.Feed("0.2.1.6");
            await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
            Assert.Equal("0.2.1", svc.Current.TargetSemVer);
            Assert.Equal("Comet", svc.Current.TargetCodename);
        }
        Assert.Equal("0.2.1.6", Assert.Single(prefetched));
    }

    // ── apply ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_Registered_EndsInInstallingAndClearsTheSnooze()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out var updater, settingsIn: settings);
        updater.ProgressTicks = [10, 55, 100];
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
        Assert.Equal(100, svc.Current.ProgressPercent);
        Assert.Equal("", settings.Get(Platform.Keys.UpdateSnoozedVersion));
        Assert.Equal(1, updater.ApplyCalls);
        Assert.Equal(svc.FeedUrl, updater.LastFeed?.ToString());
    }

    [Fact]
    public async Task Apply_SuccessHResultButNothingRegistered_IsAFailure()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out var updater, settingsIn: settings);
        updater.Result = new PackageDeploymentResult(false, 0, "");
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Unknown, svc.Current.Failure!.Kind);
        Assert.Equal(0, svc.Current.Failure!.HResult);
        Assert.NotEqual("", svc.Current.Failure!.Message);
        Assert.Equal("0.2.1.6", settings.Get(Platform.Keys.UpdateSnoozedVersion));   // nothing was staged
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
    }

    [Fact]
    public async Task Apply_EveryPublishCarriesTheTargetTheAttemptStartedWith_AndProgressArrivesInOrder()
    {
        var published = new List<AppUpdateSnapshot>();
        var svc = Build(out var http, out _, out var updater, published: published);
        updater.ProgressTicks = [0, 37, 88];
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        published.Clear();

        await svc.ApplyAsync(CancellationToken.None);

        Assert.All(published, s => Assert.Equal("0.2.1.6", s.TargetQuad));
        Assert.Equal(new[] { 0, 0, 37, 88 },
            published.Where(s => s.State == AppUpdateState.Downloading).Select(s => s.ProgressPercent));
        Assert.Equal(AppUpdateState.Installing, published[^1].State);
    }

    [Theory]
    [InlineData(unchecked((int)0x80073D02), AppUpdateFailureKind.PackagesInUse)]
    [InlineData(unchecked((int)0x80073D06), AppUpdateFailureKind.VersionConflict)]
    [InlineData(unchecked((int)0x80073CFB), AppUpdateFailureKind.VersionConflict)]
    [InlineData(unchecked((int)0x80073CFF), AppUpdateFailureKind.SideloadPolicy)]
    [InlineData(unchecked((int)0x80072F76), AppUpdateFailureKind.Network)]
    [InlineData(unchecked((int)0x80072EE7), AppUpdateFailureKind.Network)]
    [InlineData(unchecked((int)0x80072EFD), AppUpdateFailureKind.Network)]
    [InlineData(unchecked((int)0x80070057), AppUpdateFailureKind.AppInstallerOutdated)]
    [InlineData(unchecked((int)0x8007000B), AppUpdateFailureKind.Unknown)]
    public async Task Apply_MapsEveryDeploymentHResultToItsUserFacingKind(int hresult, AppUpdateFailureKind expected)
    {
        var svc = Build(out var http, out _, out var updater);
        updater.Result = new PackageDeploymentResult(false, hresult, "deployment said no");
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(expected, svc.Current.Failure!.Kind);
        Assert.Equal(hresult, svc.Current.Failure!.HResult);
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);   // Retry must know what it is retrying
    }

    [Fact]
    public async Task Apply_OnAMeteredLinkWithoutConsent_RefusesBeforeTouchingDeployment()
    {
        var svc = Build(out var http, out _, out var updater, metered: true);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        await svc.ApplyAsync(CancellationToken.None);
        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Metered, svc.Current.Failure!.Kind);
        Assert.Equal(0, updater.ApplyCalls);
    }

    [Fact]
    public async Task Apply_OnAMeteredLinkWithConsent_Proceeds()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.UpdateOnMetered, true);
        var svc = Build(out var http, out _, out var updater, settingsIn: settings, metered: true);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        await svc.ApplyAsync(CancellationToken.None);
        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
        Assert.Equal(1, updater.ApplyCalls);
    }

    [Fact]
    public async Task Apply_Unpackaged_OpensTheReleasePageAndDeploysNothing()
    {
        var opened = new List<string>();
        var svc = Build(out var http, out _, out var updater, supported: false, openUrl: opened.Add);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        await svc.ApplyAsync(CancellationToken.None);
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.1", Assert.Single(opened));
        Assert.Equal(0, updater.ApplyCalls);
        Assert.Equal(AppUpdateState.Available, svc.Current.State);   // nothing happened here, so nothing is claimed
    }

    [Fact]
    public async Task Apply_FromIdle_DoesNothing()
    {
        var svc = Build(out _, out _, out var updater);
        await svc.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, updater.ApplyCalls);
        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Fact]
    public async Task Apply_TearsDownThePackageChildrenFirst_AndATeardownFailureIsNotFatal()
    {
        int teardowns = 0;
        var svc = Build(out var http, out _, out var updater, beforeApply: () => { teardowns++; throw new InvalidOperationException("module host"); });
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        await svc.ApplyAsync(CancellationToken.None);
        Assert.Equal(1, teardowns);
        Assert.Equal(1, updater.ApplyCalls);
        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
    }

    // ── user gestures ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snooze_PersistsTheTargetQuad()
    {
        var svc = Build(out var http, out var settings, out _);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        svc.Snooze();
        Assert.Equal(AppUpdateState.Snoozed, svc.Current.State);
        Assert.Equal("0.2.1.6", settings.Get(Platform.Keys.UpdateSnoozedVersion));
    }

    [Fact]
    public async Task Acknowledge_ClearsTheObservationButKeepsWhatTheOsKnows()
    {
        var svc = Build(out var http, out _, out var updater);
        updater.Info = new AppInstallerInfo(new Uri("https://example.invalid/f.appinstaller"),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, true);
        http.Feed("0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.True(svc.Current.AutoUpdateAssociated);

        svc.Acknowledge();

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Null(svc.Current.TargetQuad);
        Assert.True(svc.Current.AutoUpdateAssociated);
        Assert.True(svc.Current.LastCheckedMs > 0);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static Update.Service Build(out FeedHandler http, out MemoryAppSettings settings, out FakePackageUpdater updater,
        MemoryAppSettings? settingsIn = null, bool isDev = false, string core = "0.2.0", string quad = "0.2.0.5",
        bool metered = false, bool supported = true, Action<string>? openUrl = null, List<AppUpdateSnapshot>? published = null,
        Func<string, ReleaseNotes.ReleaseNotesIndexEntry?>? peekIndex = null, Action<string>? prefetchNotes = null,
        Action? beforeApply = null)
    {
        http = new FeedHandler();
        settings = settingsIn ?? new MemoryAppSettings();
        updater = new FakePackageUpdater { IsSupported = supported };
        // Production arms the launch version BEFORE the service exists (Update.Host.Start); the harness keeps that order.
        string updatedFrom = Update.LaunchVersion.Arm(settings, isDev, isDev ? core + "-dev" : quad, "unpackaged", "");
        var me = new Update.Identity(quad, core, "Breaker", isDev, Update.FeedUrlFor(Root, "wavee-stable", "stable", "x64"));
        return new Update.Service(me, settings, new HttpClient(http), updater, updatedFrom,
            isMetered: () => metered, openUrl: openUrl ?? (static _ => { }),
            published: published is null ? null : new Action<AppUpdateSnapshot>(published.Add),
            peekIndex: peekIndex, prefetchNotes: prefetchNotes, beforeApply: beforeApply);
    }

    /// <summary>The feed GET, scripted: first-and-only rule wins, and it remembers the URL it was asked for.</summary>
    sealed class FeedHandler : HttpMessageHandler
    {
        Func<HttpRequestMessage, HttpResponseMessage> _respond = static _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public string? LastUrl { get; private set; }

        public void Feed(string version) => Answer(HttpStatusCode.OK,
            "<AppInstaller xmlns=\"http://schemas.microsoft.com/appx/appinstaller/2018\" Version=\"" + version
            + "\" Uri=\"https://example.invalid/f.appinstaller\"><MainPackage Name=\"cproducts.Wavee\" Version=\"" + version
            + "\" Publisher=\"CN=x\" ProcessorArchitecture=\"x64\" Uri=\"https://example.invalid/w.msix\" /></AppInstaller>");

        public void Answer(HttpStatusCode status, string body)
            => _respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

        public void Throw(Exception ex) => _respond = _ => throw ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>The deployment seam, scripted: records what the service asked for and answers with a canned result.</summary>
    sealed class FakePackageUpdater : IPackageUpdater
    {
        public bool IsSupported { get; set; } = true;
        public AppInstallerInfo? Info { get; set; }
        public PackageDeploymentResult Result { get; set; } = new(true, 0, "");
        public int[] ProgressTicks { get; set; } = [];
        public int ApplyCalls { get; private set; }
        public Uri? LastFeed { get; private set; }

        public AppInstallerInfo? GetAppInstallerInfo() => Info;

        public Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken ct)
            => Task.FromResult(PackageUpdateAvailability.Unknown);

        public Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken ct)
        {
            ApplyCalls++;
            LastFeed = feed;
            foreach (int pct in ProgressTicks) progress(pct);
            return Task.FromResult(Result);
        }
    }
}
