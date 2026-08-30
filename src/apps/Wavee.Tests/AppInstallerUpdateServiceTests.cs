using System.Net;
using FluentGpu.WindowsApi.Packaging;
using Wavee;
using Wavee.Core;
using Wavee.Tests;
using Wavee.Tests.Modules;
using Xunit;

// The real updater (App/AppInstallerUpdateService.cs), driven end to end over a scripted transport and a fake
// deployment seam: the version arithmetic it decides with, the feed read, the state machine, and the failure map.
// Nothing here touches the network, the registry or the deployment API.
public class AppInstallerUpdateServiceTests
{
    // ── IsNewer: the ordinary ordering ──────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "0.1.1")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.1.1.1", "0.1.1.0")]
    [InlineData("0.1.1.42", "0.1.1.7")]        // 4th part compares numerically, not lexically
    [InlineData("0.1.10", "0.1.9")]            // ditto for the 3rd
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("2.0.0.0", "1.99.99.99")]
    public void IsNewer_True_WhenRemoteAhead(string remote, string current)
        => Assert.True(AppUpdateVersion.IsNewer(remote, current));

    [Theory]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("0.1.1", "0.1.2")]
    [InlineData("0.1.1.0", "0.1.1")]           // a missing part is 0, so these are the SAME version
    [InlineData("0.1.1", "0.1.1.0")]
    [InlineData("0.1.1", "0.1.1.1")]
    [InlineData("0.9.9", "1.0.0")]
    public void IsNewer_False_WhenRemoteNotAhead(string remote, string current)
        => Assert.False(AppUpdateVersion.IsNewer(remote, current));

    // ── IsNewer: normalization ──────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "0.1.1-dev")]         // a dev build still learns a release is out
    [InlineData("v0.1.2", "0.1.1")]            // a leading v is tolerated
    [InlineData("0.1.2+abc123", "0.1.1")]      // build metadata is not identity
    [InlineData("0.1.2-rc.1", "0.1.1")]        // pre-release suffix stripped: 0.1.2-rc.1 still beats 0.1.1
    [InlineData("  0.1.2  ", "0.1.1")]
    public void IsNewer_NormalizesBothSides(string remote, string current)
        => Assert.True(AppUpdateVersion.IsNewer(remote, current));

    [Fact]
    public void IsNewer_PreReleaseOfSameVersion_IsNotNewer()
    {
        // 0.1.1-dev normalizes to 0.1.1 -- equal, not ahead. A dev build of the version already shipped must not
        // prompt itself to "update" to the release it is a build of.
        Assert.False(AppUpdateVersion.IsNewer("0.1.1-dev", "0.1.1"));
        Assert.False(AppUpdateVersion.IsNewer("0.1.1", "0.1.1-dev"));
    }

    // ── IsNewer: anything unparsable is a refusal, never a prompt ───────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.2", "dev")]               // the unstamped-build sentinel
    [InlineData("0.1.2", "")]
    [InlineData("0.1.2", null)]
    [InlineData("0.1.2", "not-a-version")]
    [InlineData("0.1.2", "1.x.3")]
    [InlineData("0.1.2", "1..2")]
    [InlineData("0.1.2", "1.2.3.4.5")]         // more parts than a version has
    [InlineData("dev", "0.1.1")]
    [InlineData("", "0.1.1")]
    [InlineData(null, "0.1.1")]
    [InlineData("garbage", "0.1.1")]
    [InlineData("1.2.3.", "0.1.1")]
    [InlineData("-1.2.3", "0.1.1")]            // the '-' strips to an empty version
    public void IsNewer_False_WhenEitherSideUnparsable(string? remote, string? current)
        => Assert.False(AppUpdateVersion.IsNewer(remote, current));

    [Fact]
    public void IsNewer_UnparsableBothSides_IsFalse()
        => Assert.False(AppUpdateVersion.IsNewer("dev", "dev"));

    // ── the startup "you were updated" rule ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void FirstRunAfterUpdate_True_WhenLastRunDiffers()
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.2"));

    [Fact]
    public void FirstRunAfterUpdate_TrueOnDowngradeToo()
        // A rollback is still "the version changed under you" -- the notice is about the change, not its direction.
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.2", "0.1.1"));

    [Fact]
    public void FirstRunAfterUpdate_False_OnFirstEverLaunch()
    {
        // An empty LastRunVersion is a fresh install: greeting it with "Wavee was updated" would be a lie.
        Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("", "0.1.1"));
        Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate(null, "0.1.1"));
    }

    [Fact]
    public void FirstRunAfterUpdate_False_WhenUnchanged()
        => Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.1"));

    [Fact]
    public void FirstRunAfterUpdate_False_WhenCurrentUnknown()
        => Assert.False(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", ""));

    [Fact]
    public void FirstRunAfterUpdate_IsExactStringCompare()
        // Not a version comparison: 0.1.1 and 0.1.1.0 are DIFFERENT stamps, and a build that changed stamp changed.
        => Assert.True(AppUpdateVersion.IsFirstRunAfterUpdate("0.1.1", "0.1.1.0"));

    // ── the release-notes tag ───────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("0.1.1.42", "0.1.1")]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("0.1.1-dev", "0.1.1")]
    [InlineData("v2.3.4+meta", "2.3.4")]
    [InlineData("1.2", "1.2.0")]               // missing parts are 0, so the tag is still three-part
    public void ReleaseTagVersion_TakesFirstThreeParts(string version, string expected)
        => Assert.Equal(expected, AppUpdateVersion.ReleaseTagVersion(version));

    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ReleaseTagVersion_FallsBackToNormalizedInput(string? version, string expected)
        => Assert.Equal(expected, AppUpdateVersion.ReleaseTagVersion(version));

    // ── the feed URL is built from BUILD-TIME metadata, not from a runtime switch ────────────────────────────────
    [Fact]
    public void FeedUrl_UsesTheStampedFeedReleaseAndArch()
    {
        var svc = Build(out _, out _, out _, arch: "arm64", feedRelease: "wavee-stable-test");
        Assert.Equal(
            "https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable-test/Wavee.arm64.appinstaller",
            svc.FeedUrl);
    }

    [Fact]
    public void FeedUrl_BetaChannelUsesTheBetaAssetPrefix()
    {
        var svc = Build(out _, out _, out _, channel: "beta", feedRelease: "wavee-beta");
        Assert.Contains("/wavee-beta/Wavee.Beta.x64.appinstaller", svc.FeedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void FeedUrl_IsBuiltUnderTheStampedBaseUrl()
    {
        // The local end-to-end test packs a build whose feed is a loopback HTTP server. Nothing about the running
        // process changes: the base URL is assembly metadata, so this is the SAME code path the shipping build takes.
        var svc = Build(out _, out _, out _, feedRelease: "wavee-local", updateBaseUrl: "http://127.0.0.1:8099/");
        Assert.Equal("http://127.0.0.1:8099/wavee-local/Wavee.x64.appinstaller", svc.FeedUrl);
    }

    // ── the ctor's one-shot "you were updated" decision ─────────────────────────────────────────────────────────
    [Fact]
    public void Ctor_RaisesCompleted_WhenTheQuadMovedSinceTheLastRun()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.0.4");
        var svc = Build(out _, out _, out _, settingsIn: settings, quad: "0.2.1.5", core: "0.2.1", codename: "Breaker");

        Assert.Equal(AppUpdateState.Completed, svc.Current.State);
        Assert.Equal("0.2.1.5", svc.Current.TargetQuad);
        Assert.Equal("0.2.1", svc.Current.TargetSemVer);
        Assert.Equal("Breaker", svc.Current.TargetCodename);
        // The plate needs to know where the user came FROM, and this is the only moment that value exists.
        Assert.Equal("0.2.0.4", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
        // …and Settings › About's "Show the update summary again" needs it AFTER the plate has consumed the one-shot,
        // so the same value is also written to a key nothing ever clears.
        Assert.Equal("0.2.0.4", settings.Get(WaveeSettings.ReleaseNotesPreviousVersion));
        Assert.Equal("0.2.1.5", settings.Get(WaveeSettings.LastRunVersion));
    }

    [Fact]
    public void Ctor_LeavesPreviousVersionAloneWhenNothingChanged()
    {
        // A relaunch of the SAME build is not an update. The durable from-quad written by the real update must survive
        // it untouched — otherwise "Show the update summary again" forgets where the user came from on the second run.
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.0.5");
        settings.Set(WaveeSettings.ReleaseNotesPreviousVersion, "0.1.9.3");
        var svc = Build(out _, out _, out _, settingsIn: settings, quad: "0.2.0.5");

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("0.1.9.3", settings.Get(WaveeSettings.ReleaseNotesPreviousVersion));
        Assert.Equal("", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
    }

    [Fact]
    public void Ctor_IsSilentOnAFirstEverLaunch()
    {
        var settings = new MemoryAppSettings();
        var svc = Build(out _, out _, out _, settingsIn: settings);

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
    }

    [Fact]
    public void Ctor_ADevBuildNeverClaimsItWasUpdated()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.1.9-dev");
        var svc = Build(out _, out _, out _, settingsIn: settings, channel: "dev", quad: "", core: "0.2.0");

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Equal("", settings.Get(WaveeSettings.ReleaseNotesPendingFrom));
        // A dev build stamps its SEMVER, so two dev builds of the same version never look like an update to each other.
        Assert.Equal("0.2.0-dev", settings.Get(WaveeSettings.LastRunVersion));
    }

    // ── check ───────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Check_NewerFeed_IsAvailable()
    {
        var svc = Build(out var http, out var settings, out _, quad: "0.2.0.5");
        Feed(http, "0.2.1.6");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Available, svc.Current.State);
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
        Assert.Null(svc.Current.Failure);
        Assert.True(settings.Get(WaveeSettings.UpdateLastCheckedMs) > 0);
    }

    [Fact]
    public async Task Check_SnoozedQuad_IsSnoozedNotAvailable()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out _, settingsIn: settings, quad: "0.2.0.5");
        Feed(http, "0.2.1.6");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Snoozed, svc.Current.State);
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
    }

    [Fact]
    public async Task Check_SnoozeIsPerVersion_ANewerReleaseStillShouts()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out _, settingsIn: settings, quad: "0.2.0.5");
        Feed(http, "0.2.2.7");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Available, svc.Current.State);
    }

    [Fact]
    public async Task Check_ADevBuildIsNeverPrompted()
    {
        var svc = Build(out var http, out _, out _, channel: "dev", quad: "", core: "0.2.0");
        Feed(http, "9.9.9.9");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    [Fact]
    public async Task Check_FeedMissing_IsANetworkFailure()
    {
        var svc = Build(out var http, out _, out _);
        http.OnUrl(".appinstaller", HttpStatusCode.NotFound, "not found");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
    }

    /// <summary>The origin decides how a failure surfaces, never whether it is recorded: a scheduled poll publishes
    /// the same Network failure marked Quiet (no toast), a user check a loud one.</summary>
    [Theory]
    [InlineData(UpdateCheckOrigin.Scheduled, true)]
    [InlineData(UpdateCheckOrigin.User, false)]
    public async Task Check_FeedMissing_IsQuietOnlyWhenScheduled(UpdateCheckOrigin origin, bool quiet)
    {
        var svc = Build(out var http, out _, out _);
        http.OnUrl(".appinstaller", HttpStatusCode.NotFound, "not found");

        await svc.CheckAsync(origin, CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
        Assert.Equal(quiet, svc.Current.Quiet);
    }

    [Fact]
    public async Task Check_MalformedFeed_Fails()
    {
        var svc = Build(out var http, out _, out _);
        http.OnUrl(".appinstaller", HttpStatusCode.OK, "<AppInstaller Version=", "application/xml");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
    }

    [Fact]
    public async Task Check_WrongRootElement_IsANetworkFailure()
    {
        // A GitHub 404 page, an S3 error document, anything that is XML but is not a feed: refused, never parsed for a
        // Version attribute that would then be compared against ours.
        var svc = Build(out var http, out _, out _);
        http.OnUrl(".appinstaller", HttpStatusCode.OK, "<Error><Code>NoSuchKey</Code></Error>", "application/xml");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
    }

    [Fact]
    public async Task Check_UpToDate_DoesNotEatTheCompletedNotice()
    {
        // The poll runs 30 s after launch, long before the user has necessarily looked at the notification centre.
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LastRunVersion, "0.2.0.4");
        var svc = Build(out var http, out _, out _, settingsIn: settings, quad: "0.2.1.5");
        Assert.Equal(AppUpdateState.Completed, svc.Current.State);
        Feed(http, "0.2.1.5");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Completed, svc.Current.State);
    }

    [Fact]
    public async Task Check_UpToDateFromIdle_IsIdle()
    {
        var svc = Build(out var http, out _, out _, quad: "0.2.1.5");
        Feed(http, "0.2.1.5");

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.None, svc.Current.State);
    }

    // ── apply ───────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Apply_Registered_EndsInInstallingAndClearsTheSnooze()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out var updater, settingsIn: settings, quad: "0.2.0.5");
        updater.Result = new PackageDeploymentResult(true, 0, "");
        updater.ProgressTicks = [10, 55, 100];
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
        Assert.Equal(100, svc.Current.ProgressPercent);
        Assert.Equal("", settings.Get(WaveeSettings.UpdateSnoozedVersion));
        Assert.Equal(1, updater.ApplyCalls);
        Assert.Equal(svc.FeedUrl, updater.LastFeed?.ToString());
    }

    [Fact]
    public async Task Apply_SuccessHResultButNothingRegistered_IsAFailure()
    {
        // HResult 0 with IsRegistered false is what a deployment call that never actually ran looks like (an async
        // operation that completed Closed/Canceled, a seam that lost its result). It used to publish INSTALLING, so the
        // user sat waiting for a restart that could never come and the snooze was cleared for an update that had not
        // happened. REGISTERED is the only success.
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.UpdateSnoozedVersion, "0.2.1.6");
        var svc = Build(out var http, out _, out var updater, settingsIn: settings, quad: "0.2.0.5");
        updater.Result = new PackageDeploymentResult(false, 0, "");
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Unknown, svc.Current.Failure!.Kind);
        Assert.Equal(0, svc.Current.Failure!.HResult);
        Assert.NotEqual("", svc.Current.Failure!.Message);
        // The snooze is NOT cleared: nothing was staged, so "Later" still means what the user meant by it.
        Assert.Equal("0.2.1.6", settings.Get(WaveeSettings.UpdateSnoozedVersion));
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
    }

    [Fact]
    public async Task Apply_ProgressSnapshotsCarryTheTargetTheAttemptStartedWith()
    {
        // Every progress tick used to rebuild its snapshot from whatever Current held at that instant, so the attempt's
        // identity could be replaced mid-download. It is read ONCE now, before the first tick.
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5");
        updater.Result = new PackageDeploymentResult(true, 0, "");
        updater.ProgressTicks = [25, 50];
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        var seen = new List<(AppUpdateState State, string? Quad, int Pct)>();
        using var sub = svc.Changed.Subscribe(ConnectHarness.Obs<int>(
            _ => seen.Add((svc.Current.State, svc.Current.TargetQuad, svc.Current.ProgressPercent))));

        await svc.ApplyAsync(CancellationToken.None);

        Assert.All(seen, x => Assert.Equal("0.2.1.6", x.Quad));
        Assert.Contains(seen, x => x.State == AppUpdateState.Downloading && x.Pct == 50);
        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
    }

    // ── the failure-kind map ──────────────────────────────────────────────────────────────────

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
        => Assert.Equal(expected, AppInstallerUpdateService.MapFailure(kind));

    [Fact]
    public void MapFailure_CoversEveryDeploymentKind_AndNeverFallsThroughToUnknownByAccident()
    {
        // The map used to be Enum.TryParse over ToString(): a rename on either side would have silently started
        // answering Unknown for a kind it had always translated. This asserts the pairing by NAME, once, so a new
        // deployment kind added without a matching case fails here instead of in the field.
        foreach (PackageUpdateFailureKind kind in Enum.GetValues<PackageUpdateFailureKind>())
        {
            var mapped = AppInstallerUpdateService.MapFailure(kind);
            Assert.Equal(kind.ToString(), mapped.ToString());
        }
    }

    // ── what a BCL exception is allowed to contribute to a user-facing message ───────────────────────────

    [Fact]
    public void ExceptionCode_IsTheTypeAndHResult_NeverTheMessage()
    {
        // Under the publish leg's UseSystemResourceKeys=true, ex.Message collapses to a bare resource key
        // ("Arg_InvalidOperationException"), which looks like a diagnosis and is not one. The type name plus the
        // HRESULT is what actually survives the trim.
        var ex = new InvalidOperationException("this text does not exist in a trimmed build");
        string code = AppInstallerUpdateService.ExceptionCode(ex);

        Assert.StartsWith("InvalidOperationException 0x", code);
        Assert.DoesNotContain("does not exist", code);
        Assert.Equal("InvalidOperationException 0x" + ex.HResult.ToString("X8"), code);
    }

    [Fact]
    public async Task AFailedCheck_RecordsTheExceptionCode_NotAResourceKey()
    {
        var svc = Build(out var http, out _, out _, quad: "0.2.0.5");
        http.On(r => r.Url.Contains(".appinstaller", StringComparison.Ordinal),
                _ => throw new HttpRequestException("boom"));

        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(AppUpdateFailureKind.Network, svc.Current.Failure!.Kind);
        Assert.StartsWith("HttpRequestException 0x", svc.Current.Failure!.Message);
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
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5");
        updater.Result = new PackageDeploymentResult(false, hresult, "deployment said no");
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Failed, svc.Current.State);
        Assert.Equal(expected, svc.Current.Failure!.Kind);
        Assert.Equal(hresult, svc.Current.Failure!.HResult);
        // The target survives the failure: Retry must know what it is retrying.
        Assert.Equal("0.2.1.6", svc.Current.TargetQuad);
    }

    [Fact]
    public async Task Apply_OnAMeteredLinkWithoutConsent_RefusesBeforeTouchingDeployment()
    {
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5", metered: true);
        Feed(http, "0.2.1.6");
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
        settings.Set(WaveeSettings.UpdateOnMetered, true);
        var svc = Build(out var http, out _, out var updater, settingsIn: settings, quad: "0.2.0.5", metered: true);
        updater.Result = new PackageDeploymentResult(true, 0, "");
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal(AppUpdateState.Installing, svc.Current.State);
        Assert.Equal(1, updater.ApplyCalls);
    }

    [Fact]
    public async Task Apply_Unpackaged_OpensTheReleasePageAndDeploysNothing()
    {
        var opened = new List<string>();
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5", supported: false, openUrl: opened.Add);
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        await svc.ApplyAsync(CancellationToken.None);

        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.1", Assert.Single(opened));
        Assert.Equal(0, updater.ApplyCalls);
        // No fake progress, no fake "installing": nothing happened in this process, so nothing is claimed.
        Assert.Equal(AppUpdateState.Available, svc.Current.State);
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
    public async Task Apply_ReportsProgressAsItArrives()
    {
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5");
        updater.Result = new PackageDeploymentResult(true, 0, "");
        updater.ProgressTicks = [0, 37, 88];
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        var seen = new List<int>();
        using (svc.Changed.Subscribe(ConnectHarness.Obs<int>(_ =>
        {
            if (svc.Current.State == AppUpdateState.Downloading) seen.Add(svc.Current.ProgressPercent);
        })))
        {
            await svc.ApplyAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { 0, 0, 37, 88 }, seen);   // the initial Downloading publish, then one per tick
    }

    // ── user gestures ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Snooze_PersistsTheTargetQuad()
    {
        var svc = Build(out var http, out var settings, out _, quad: "0.2.0.5");
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);

        svc.Snooze();

        Assert.Equal(AppUpdateState.Snoozed, svc.Current.State);
        Assert.Equal("0.2.1.6", settings.Get(WaveeSettings.UpdateSnoozedVersion));
    }

    [Fact]
    public async Task Acknowledge_ClearsTheObservationButKeepsWhatTheOsKnows()
    {
        var svc = Build(out var http, out _, out var updater, quad: "0.2.0.5");
        updater.Info = new AppInstallerInfo(new Uri("https://example.invalid/f.appinstaller"),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, true);
        Feed(http, "0.2.1.6");
        await svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
        Assert.True(svc.Current.AutoUpdateAssociated);

        svc.Acknowledge();

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        Assert.Null(svc.Current.TargetQuad);
        Assert.True(svc.Current.AutoUpdateAssociated);
        Assert.True(svc.Current.LastCheckedMs > 0);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────

    static AppInstallerUpdateService Build(
        out ScriptedHttpHandler http,
        out MemoryAppSettings settings,
        out FakePackageUpdater updater,
        MemoryAppSettings? settingsIn = null,
        string channel = "stable",
        string core = "0.2.0",
        string quad = "0.2.0.5",
        string codename = "Breaker",
        string arch = "x64",
        string feedRelease = "wavee-stable",
        string? updateBaseUrl = null,
        bool metered = false,
        bool supported = true,
        Action<string>? openUrl = null)
    {
        http = new ScriptedHttpHandler();
        var scripted = http;
        settings = settingsIn ?? new MemoryAppSettings();
        updater = new FakePackageUpdater { IsSupported = supported };
        string semver = channel == "dev" ? core + "-dev" : core;
        var me = new WaveeVersionInfo(semver, core, null, quad, codename, channel, "abc1234", "2026-08-01T00:00:00Z",
            feedRelease, WaveeVersionInfo.NormalizeUpdateBaseUrl(updateBaseUrl));
        var svc = new AppInstallerUpdateService(settings, new HttpClient(scripted), me, arch, updater,
            new CapturingWaveeLog(), isMetered: () => metered, openUrl: openUrl ?? (_ => { }));
        return svc;
    }

    /// <summary>Script the feed GET with a root <c>Version</c>. The rule is inserted BEFORE the catch-all the harness
    /// appended, because <see cref="ScriptedHttpHandler"/> is first-match-wins.</summary>
    static void Feed(ScriptedHttpHandler http, string version)
        => http.On(r => r.Url.Contains(".appinstaller", StringComparison.Ordinal),
            _ => ScriptedHttpHandler.Respond(HttpStatusCode.OK,
                "<AppInstaller xmlns=\"http://schemas.microsoft.com/appx/appinstaller/2018\" "
                + "Version=\"" + version + "\" Uri=\"https://example.invalid/f.appinstaller\"><MainPackage "
                + "Name=\"cproducts.Wavee\" Version=\"" + version + "\" Publisher=\"CN=x\" ProcessorArchitecture=\"x64\" "
                + "Uri=\"https://example.invalid/w.msix\" /></AppInstaller>",
                "application/xml"));

    /// <summary>The deployment seam, scripted. Records what the service asked for and answers with a canned result —
    /// so the whole failure map is exercised without a single real HRESULT ever leaving Windows.</summary>
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
