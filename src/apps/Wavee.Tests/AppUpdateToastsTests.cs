using FluentGpu.Controls;
using Wavee;
using Wavee.Core;
using Xunit;

// The update toast decision table (App/AppUpdateToasts.cs). The rule worth pinning is the one every scattered call
// site gets wrong: an app update is STATE, not an event, so a card is planned when the state MOVED and never when a
// progress tick, a re-check or an unrelated rebuild republishes the same observation.
public class AppUpdateToastsTests
{
    static AppUpdateSnapshot Snap(AppUpdateState state, int pct = 0, AppUpdateFailureKind? failure = null)
        => new(state, "0.3.0.17", "0.3.0", "Crest", pct,
            failure is { } k ? new AppUpdateFailure(k, unchecked((int)0x80073D02), "boom") : null,
            AutoUpdateAssociated: true, LastCheckedMs: 1);

    [Fact]
    public void NoTransition_PlansNothing()
    {
        var s = Snap(AppUpdateState.Available);
        Assert.Null(AppUpdateToasts.Plan(s, s));
    }

    [Fact]
    public void ProgressTicks_DoNotPlanASecondCard()
    {
        // Twenty ticks would otherwise be twenty stacked cards. The bar the first card mounted is what moves.
        var first = AppUpdateToasts.Plan(Snap(AppUpdateState.Available), Snap(AppUpdateState.Downloading, 0));
        Assert.NotNull(first);
        Assert.Null(AppUpdateToasts.Plan(Snap(AppUpdateState.Downloading, 0), Snap(AppUpdateState.Downloading, 5)));
        Assert.Null(AppUpdateToasts.Plan(Snap(AppUpdateState.Downloading, 5), Snap(AppUpdateState.Downloading, 95)));
    }

    [Theory]
    [InlineData(AppUpdateState.None)]
    [InlineData(AppUpdateState.Checking)]
    [InlineData(AppUpdateState.Snoozed)]
    public void SilentStates_PlanNothing(AppUpdateState state)
        => Assert.Null(AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, Snap(state)));

    /// <summary>With a codename the body carries the quad; with nothing but the quad known the title already says it
    /// and the body stays empty â€” the card never repeats the same number twice.</summary>
    [Fact]
    public void Available_BodyNamesTheQuadOnlyWhenTheTitleCouldNot()
    {
        var named = AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, Snap(AppUpdateState.Available));
        Assert.Equal("0.3.0.17", named!.Value.Body);
        var bare = Snap(AppUpdateState.Available) with { TargetSemVer = null, TargetCodename = null };
        var plan = AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, bare);
        Assert.NotNull(plan);   // the title carries the quad through the loc format (not loaded here); the body must not repeat it
        Assert.Equal("", plan!.Value.Body);
    }

    [Fact]
    public void Available_OffersUpdateNowFirst()
    {
        var plan = AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, Snap(AppUpdateState.Available));

        Assert.NotNull(plan);
        Assert.Equal(InfoBarSeverity.Informational, plan!.Value.Severity);
        Assert.False(plan.Value.Sticky);
        Assert.Equal(
            new[] { ToastActionKind.UpdateNow, ToastActionKind.WhatsNew, ToastActionKind.Later },
            plan.Value.Actions);
        // The text itself is localized (this assembly has no catalog, so every key resolves to its own [key] marker) â€”
        // what is pinned here is that "available" and "updated" are DIFFERENT sentences, not the copy.
        Assert.NotEqual(
            AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, Snap(AppUpdateState.Completed))!.Value.Title,
            plan.Value.Title);
    }

    [Fact]
    public void Downloading_IsStickyAndOffersNoAction()
    {
        var plan = AppUpdateToasts.Plan(Snap(AppUpdateState.Available), Snap(AppUpdateState.Downloading, 12));

        Assert.NotNull(plan);
        Assert.True(plan!.Value.Sticky);
        Assert.Empty(plan.Value.Actions);
    }

    [Fact]
    public void Installing_IsSticky()
    {
        var plan = AppUpdateToasts.Plan(Snap(AppUpdateState.Downloading, 100), Snap(AppUpdateState.Installing, 100));

        Assert.NotNull(plan);
        Assert.True(plan!.Value.Sticky);
        Assert.Empty(plan.Value.Actions);
    }

    [Fact]
    public void Completed_IsSuccessAndOffersWhatsNew()
    {
        var plan = AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, Snap(AppUpdateState.Completed));

        Assert.NotNull(plan);
        Assert.Equal(InfoBarSeverity.Success, plan!.Value.Severity);
        Assert.False(plan.Value.Sticky);
        Assert.Equal(ToastActionKind.WhatsNew, Assert.Single(plan.Value.Actions));
    }

    [Fact]
    public void Failed_IsStickyErrorWithRetryFirst()
    {
        var plan = AppUpdateToasts.Plan(Snap(AppUpdateState.Downloading, 40),
            Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.PackagesInUse));

        Assert.NotNull(plan);
        Assert.Equal(InfoBarSeverity.Error, plan!.Value.Severity);
        Assert.True(plan.Value.Sticky);
        Assert.Equal(new[] { ToastActionKind.Retry, ToastActionKind.OpenReleasePage }, plan.Value.Actions);
    }

    /// <summary>A SCHEDULED poll that could not reach the feed is recorded (About shows it) but never announced: the
    /// first launch of a fresh install polls while the user is still in the setup wizard, and a link that is down
    /// for a minute is not something the user asked to dismiss. The same failure from a user-initiated check toasts.</summary>
    [Fact]
    public void Failed_QuietScheduledCheck_NeverToasts()
    {
        var quiet = Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.Network) with { Quiet = true };
        Assert.Null(AppUpdateToasts.Plan(Snap(AppUpdateState.None), quiet));
        var loud = Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.Network);
        Assert.NotNull(AppUpdateToasts.Plan(Snap(AppUpdateState.None), loud));
    }

    [Fact]
    public void Failed_Metered_OffersOnlyRetry()
    {
        // "Open release page" is the wrong escape here: the update is not broken, the network is billed. Retrying
        // (after unmetering, or after flipping the setting) is the whole answer.
        var plan = AppUpdateToasts.Plan(Snap(AppUpdateState.Available),
            Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.Metered));

        Assert.NotNull(plan);
        Assert.Equal(ToastActionKind.Retry, Assert.Single(plan!.Value.Actions));
    }

    [Fact]
    public void Failed_ANewReasonReplansEvenThoughTheStateDidNotMove()
    {
        // Failed â†’ Failed is not a transition, but "couldn't reach GitHub" â†’ "close the other windows" is a different
        // instruction and the user has to see it.
        var before = Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.Network);
        var after = Snap(AppUpdateState.Failed, failure: AppUpdateFailureKind.PackagesInUse);

        Assert.Null(AppUpdateToasts.Plan(before, before));
        Assert.NotNull(AppUpdateToasts.Plan(before, after));
    }

    [Fact]
    public void EveryFailureKindHasItsOwnSentence()
    {
        var kinds = Enum.GetValues<AppUpdateFailureKind>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            string text = AppUpdateToasts.FailureText(new AppUpdateFailure(kind, 5, ""));
            Assert.False(string.IsNullOrWhiteSpace(text));
            seen.Add(text);
        }
        Assert.Equal(kinds.Length, seen.Count);
    }

    [Fact]
    public void EveryActionHasALabel()
    {
        foreach (var kind in Enum.GetValues<ToastActionKind>())
            Assert.False(string.IsNullOrWhiteSpace(AppUpdateToasts.Label(kind)));
    }

    [Theory]
    [InlineData("Crest", "0.3.0", "0.3.0.17", "Crest")]
    [InlineData("", "0.3.0", "0.3.0.17", "0.3.0")]
    [InlineData("", "", "0.3.0.17", "0.3.0.17")]
    public void ReleaseName_DegradesCodenameToSemverToQuad(string codename, string semver, string quad, string expected)
    {
        var snapshot = new AppUpdateSnapshot(AppUpdateState.Available, quad,
            semver.Length == 0 ? null : semver, codename.Length == 0 ? null : codename,
            0, null, false, 0);
        Assert.Equal(expected, AppUpdateToasts.ReleaseName(snapshot));
    }
}
