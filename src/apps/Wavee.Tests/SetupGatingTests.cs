using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Wavee.Tests;

// The first-run setup wizard's pure decisions (App/SetupGating.cs + App/SetupBootstrap.cs): the arm/complete/defer
// state machine, the page-flow skip/clamp rules, and the footer progress mapping. Mirrors SidebarBootstrapTests /
// SidebarDesignGatingTests in shape — these drive the REAL production types (source-included), never a copy of them.
public class SetupGatingTests : IDisposable
{
    readonly string _local = Path.Combine(Path.GetTempPath(), "wavee-setup-gating-tests", Guid.NewGuid().ToString("n"));

    public SetupGatingTests() => Directory.CreateDirectory(_local);

    public void Dispose()
    {
        try { Directory.Delete(_local, recursive: true); } catch (Exception) { }
    }

    // ── SetupBootstrap.Run ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreshInstall_ArmsPending_AndSuppressesTheSidebarChooser()
    {
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupPending));
        Assert.False(settings.Get(WaveeSettings.SetupCompleted));
        // LOAD-BEARING: setup page 5 IS the sidebar-design chooser — a fresh install must not also arm the separate
        // one-time popup chooser, or both onboardings show on the same launch.
        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));
        Assert.Equal(SetupBootstrap.TargetVersion, settings.Get(WaveeSettings.SetupBootstrapVersion));
    }

    /// <summary>An existing install is marked COMPLETED (the wizard is never retro-fitted onto someone mid-use) — but
    /// it has still never recorded a terms acceptance, so the terms re-arm fires once and brings it back for that one
    /// page. The two rules compose: "don't re-onboard existing users" and "nobody streams without accepting the terms".</summary>
    [Fact]
    public void ExistingInstall_MarksCompleted_AndGrandfathersTerms()
    {
        // library.db existing is SidebarBootstrap.IsFreshInstall's own first witness — SetupBootstrap reuses it verbatim.
        var settings = ExistingInstall();
        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));   // never retro-fit the wizard onto a finished install
        Assert.Equal(SetupGating.TermsVersion, settings.Get(WaveeSettings.TermsAcceptedVersion));   // stamped, not re-shown
        // An existing install must not be affected by the setup wizard arming the sidebar chooser suppression either
        // way — SidebarBootstrap (not SetupBootstrap) owns that key for existing installs.
        Assert.False(settings.WasWritten(WaveeSettings.SidebarOnboardingSeen));
    }

    [Fact]
    public void ExistingInstall_WithCurrentTermsAccepted_IsNotArmed()
    {
        var settings = ExistingInstall();
        settings.Set(WaveeSettings.TermsAcceptedVersion, SetupGating.TermsVersion);

        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));
    }

    /// <summary>The re-arm must reach installs that burned <c>SetupBootstrapVersion</c> long ago — the trigger is a
    /// shipped terms revision, not a one-time migration, so <see cref="SetupBootstrap.Run"/>'s early return still has
    /// to evaluate it.</summary>
    [Fact]
    public void AlreadyBootstrapped_GrandfathersUnversionedAcceptance_AndReArmsOnlyForALaterBump()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupBootstrapVersion, SetupBootstrap.TargetVersion);
        settings.Set(WaveeSettings.SetupCompleted, true);
        settings.Set(WaveeSettings.SetupPending, false);
        settings.Set(WaveeSettings.TermsAcceptedVersion, 0);   // completed before terms were versioned

        SetupBootstrap.Run(settings, _local);

        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.Equal(SetupGating.TermsVersion, settings.Get(WaveeSettings.TermsAcceptedVersion));

        // The pure rules the bootstrap composes: a later TermsVersion bump re-arms; unversioned acceptance never does.
        Assert.True(SetupGating.NeedsTermsRearm(completed: true, accepted: SetupGating.TermsVersion, current: SetupGating.TermsVersion + 1));
        Assert.False(SetupGating.NeedsTermsRearm(completed: true, accepted: SetupGating.TermsVersion, current: SetupGating.TermsVersion));
        Assert.True(SetupGating.GrandfathersTerms(completed: true, accepted: 0));
        Assert.False(SetupGating.GrandfathersTerms(completed: false, accepted: 0));
        Assert.False(SetupGating.GrandfathersTerms(completed: true, accepted: 1));
    }

    /// <summary>Accepting the terms is what STOPS the re-arm: the wizard's Terms page writes the current version, and
    /// the next launch must then leave the install alone instead of re-opening the wizard forever.</summary>
    [Fact]
    public void AcceptingTheTerms_StopsTheReArm()
    {
        // Simulate a pending re-arm (a later terms bump) the way the bootstrap would leave it, then the wizard accepting.
        var settings = ExistingInstall();
        settings.Set(WaveeSettings.SetupCompleted, true);
        settings.Set(WaveeSettings.SetupPending, true);

        // What SetupSession.Primary()'s Terms case does, then the wizard completing.
        settings.Set(WaveeSettings.TermsAcceptedVersion, SetupGating.TermsVersion);
        SetupGating.MarkCompleted(settings);
        Assert.False(settings.Get(WaveeSettings.SetupPending));

        SetupBootstrap.Run(settings, _local);   // the next launch

        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.Equal(SetupGating.TermsVersion, settings.Get(WaveeSettings.TermsAcceptedVersion));
    }

    MemoryAppSettings ExistingInstall()
    {
        string waveeDir = Path.Combine(_local, "Wavee");
        Directory.CreateDirectory(waveeDir);
        File.WriteAllText(Path.Combine(waveeDir, "library.db"), "sqlite");
        return new MemoryAppSettings();
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);
        settings.Set(WaveeSettings.SetupPending, false);   // simulate the wizard having already been shown
        int before = settings.WrittenCount;

        SetupBootstrap.Run(settings, _local);   // a later launch — must touch nothing

        Assert.Equal(before, settings.WrittenCount);
        Assert.False(settings.Get(WaveeSettings.SetupPending));
    }

    [Fact]
    public void FactoryResetProfile_ReArmsTheWizard()
    {
        // Every key back at its default, in a fresh (empty) temp data root — exactly what a factory reset produces —
        // must look indistinguishable from a true first launch and re-arm the wizard.
        var settings = new MemoryAppSettings();
        SetupBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SetupPending));
        Assert.Equal(SetupBootstrap.TargetVersion, settings.Get(WaveeSettings.SetupBootstrapVersion));
    }

    // ── SetupGating.IsPending / IsCompleted ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsPending_IsCompleted_NullTolerant()
    {
        Assert.False(SetupGating.IsPending(null));
        Assert.False(SetupGating.IsCompleted(null));
    }

    [Fact]
    public void IsPending_IsCompleted_ReadTheKeys()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);
        Assert.True(SetupGating.IsPending(settings));
        Assert.False(SetupGating.IsCompleted(settings));

        settings.Set(WaveeSettings.SetupCompleted, true);
        Assert.True(SetupGating.IsCompleted(settings));
    }

    // ── MarkCompleted ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkCompleted_SetsCompleted_ClearsPending_ReturnsTheTransitionOnce()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkCompleted(settings));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));

        Assert.False(SetupGating.MarkCompleted(settings));   // idempotent: no second transition
        Assert.False(SetupGating.MarkCompleted(settings));
    }

    [Fact]
    public void MarkCompleted_ToleratesNoSettingsSeam()
    {
        Assert.False(SetupGating.MarkCompleted(null));
    }

    // ── MarkDeferred ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkDeferred_ClearsPending_WithoutSettingCompleted()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkDeferred(settings));
        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.False(settings.Get(WaveeSettings.SetupCompleted));
    }

    [Fact]
    public void MarkDeferred_AfterMarkCompleted_IsANoOp_AndCompletedStaysTrue()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);
        SetupGating.MarkCompleted(settings);

        Assert.False(SetupGating.MarkDeferred(settings));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.False(settings.Get(WaveeSettings.SetupPending));
    }

    [Fact]
    public void MarkDeferred_IsIdempotent()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupPending, true);

        Assert.True(SetupGating.MarkDeferred(settings));
        Assert.False(SetupGating.MarkDeferred(settings));   // already cleared — no second transition
    }

    /// <summary>An already-completed install that is re-armed for new terms reaches Done with <c>SetupCompleted</c>
    /// already true. <see cref="SetupGating.MarkCompleted"/> must STILL clear <c>SetupPending</c>, or the wizard
    /// re-opens on every launch with no way to satisfy it.</summary>
    [Fact]
    public void MarkCompleted_ClearsPending_EvenWhenAlreadyCompleted()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SetupCompleted, true);
        settings.Set(WaveeSettings.SetupPending, true);   // the terms re-arm

        Assert.False(SetupGating.MarkCompleted(settings));   // not a Completed transition — it was already completed
        Assert.False(settings.Get(WaveeSettings.SetupPending));
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
    }

    // ── CanDismiss ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Escape / light-dismiss may close ONLY a rerun, and only while nothing long-running is in flight. A
    /// first-run or re-auth wizard dismissed this way leaves a bare titlebar over Mica with no way back in — the
    /// wizard is Wavee's only sign-in surface, so there is genuinely nothing behind it.</summary>
    [Theory]
    [InlineData(true, false, true)]    // rerun, idle → the one dismissible case
    [InlineData(true, true, false)]    // rerun, but busy
    [InlineData(false, false, false)]  // first-run / re-auth: never
    [InlineData(false, true, false)]
    public void CanDismiss_OnlyARerunThatIsNotBusy(bool isRerun, bool busy, bool expected)
        => Assert.Equal(expected, SetupGating.CanDismiss(isRerun, busy));

    /// <summary>An in-place disclosure (the Terms agreement) spends the Escape: the plate never closes while one is
    /// open, on ANY entry point; with nothing nested the answer is exactly <see cref="SetupGating.CanDismiss"/>.</summary>
    [Theory]
    [InlineData(true, true, false, false)]    // nested open on a dismissible rerun → the disclosure closes, not the plate
    [InlineData(true, false, false, false)]   // nested open on a first run → same
    [InlineData(false, true, false, true)]    // nothing nested, dismissible rerun → the plate closes
    [InlineData(false, false, false, false)]  // nothing nested, first run → vetoed as before
    public void EscapeClosesPlate_NestedDisclosureSpendsTheKey(bool nestedOpen, bool isRerun, bool busy, bool expected)
        => Assert.Equal(expected, SetupGating.EscapeClosesPlate(nestedOpen, isRerun, busy));

    // ── NeedsTermsRearm ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 0, 1, true)]    // completed, never accepted → re-arm
    [InlineData(true, 1, 1, false)]   // accepted this revision → leave it alone
    [InlineData(true, 2, 1, false)]   // a downgrade must not re-ask
    [InlineData(false, 0, 1, false)]  // never completed → already pending; re-arming says nothing new
    public void NeedsTermsRearm_OnlyForACompletedInstallBehindTheCurrentRevision(
        bool completed, int accepted, int current, bool expected)
        => Assert.Equal(expected, SetupGating.NeedsTermsRearm(completed, accepted, current));

    // ── SkipSignIn ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SkipSignIn_MirrorsAuthedFlag(bool authed, bool expected)
        => Assert.Equal(expected, SetupGating.SkipSignIn(authed));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void CarriesAcrossAuthGate_RequiresBarePendingAuthenticated(
        bool bare, bool pending, bool authenticated, bool expected)
        => Assert.Equal(expected, SetupGating.CarriesAcrossAuthGate(bare, pending, authenticated));

    // ── NextPage / PrevPage ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextPage_SkipsSignIn_WhenSkipping()
    {
        Assert.Equal(SetupPage.LocalPlayback, SetupGating.NextPage(SetupPage.Terms, skipSignIn: true));
        Assert.Equal(SetupPage.SignIn, SetupGating.NextPage(SetupPage.Terms, skipSignIn: false));
    }

    [Fact]
    public void PrevPage_SkipsSignIn_WhenSkipping()
    {
        Assert.Equal(SetupPage.Terms, SetupGating.PrevPage(SetupPage.LocalPlayback, skipSignIn: true));
        Assert.Equal(SetupPage.SignIn, SetupGating.PrevPage(SetupPage.LocalPlayback, skipSignIn: false));
    }

    [Fact]
    public void NextPage_ClampsAtDone()
    {
        Assert.Equal(SetupPage.Done, SetupGating.NextPage(SetupPage.Done, skipSignIn: false));
        Assert.Equal(SetupPage.Done, SetupGating.NextPage(SetupPage.Notifications, skipSignIn: false));
    }

    [Fact]
    public void PrevPage_ClampsAtWelcome()
    {
        Assert.Equal(SetupPage.Welcome, SetupGating.PrevPage(SetupPage.Welcome, skipSignIn: false));
        Assert.Equal(SetupPage.Welcome, SetupGating.PrevPage(SetupPage.Terms, skipSignIn: false));
    }

    [Fact]
    public void NextThenPrev_WithSkip_RoundTrips()
    {
        // Walking forward-then-back over the skip must never leave you ON SignIn.
        var forward = SetupGating.NextPage(SetupPage.Terms, skipSignIn: true);
        var back = SetupGating.PrevPage(forward, skipSignIn: true);
        Assert.Equal(SetupPage.Terms, back);
        Assert.NotEqual(SetupPage.SignIn, forward);
    }

    // ── StepNumber / Progress ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepNumber_IsNullAtTheEnds()
    {
        Assert.Null(SetupGating.StepNumber(SetupPage.Welcome));
        Assert.Null(SetupGating.StepNumber(SetupPage.Done));
    }

    [Theory]
    [InlineData(SetupPage.Terms, 1)]
    [InlineData(SetupPage.SignIn, 2)]
    [InlineData(SetupPage.LocalPlayback, 3)]
    [InlineData(SetupPage.Appearance, 4)]
    [InlineData(SetupPage.Sidebar, 5)]
    [InlineData(SetupPage.Sound, 6)]
    [InlineData(SetupPage.Notifications, 7)]
    public void StepNumber_IsStepOfSeven(SetupPage page, int expectedStep)
    {
        var n = SetupGating.StepNumber(page);
        Assert.NotNull(n);
        Assert.Equal(expectedStep, n!.Value.Step);
        Assert.Equal(7, n!.Value.Total);
    }

    [Fact]
    public void StepNumber_DoesNotChange_WhetherOrNotSignInIsSkipped()
    {
        // The user's mental model is the same wizard whether or not they were already signed in — renumbering to 6
        // would make the two runs look like different products. StepNumber is keyed on page IDENTITY, never on a
        // running count of pages actually visited, so this holds trivially — pin it anyway.
        foreach (SetupPage page in new[]
                 {
                     SetupPage.Terms, SetupPage.LocalPlayback, SetupPage.Appearance,
                     SetupPage.Sidebar, SetupPage.Sound, SetupPage.Notifications,
                 })
        {
            Assert.Equal(SetupGating.StepNumber(page), SetupGating.StepNumber(page));
        }

        // The concrete regression: LocalPlayback is step 3 whether the wizard skipped SignIn to get there or not.
        var viaFullRun = SetupGating.NextPage(SetupGating.NextPage(SetupPage.Welcome, false), false);   // Terms -> SignIn... not reached, see below
        Assert.Equal(SetupPage.SignIn, viaFullRun);
        var viaSkippedRun = SetupGating.NextPage(SetupGating.NextPage(SetupPage.Welcome, true), true);
        Assert.Equal(SetupPage.LocalPlayback, viaSkippedRun);
        Assert.Equal(3, SetupGating.StepNumber(SetupPage.LocalPlayback)!.Value.Step);
    }

    [Fact]
    public void StepLabelKey_OnlyWelcomeAndDoneHaveAFixedLabel()
    {
        Assert.Equal(Strings.Setup.PreSetup, SetupGating.StepLabelKey(SetupPage.Welcome));
        Assert.Equal(Strings.Setup.Complete, SetupGating.StepLabelKey(SetupPage.Done));
        foreach (SetupPage page in new[]
                 {
                     SetupPage.Terms, SetupPage.SignIn, SetupPage.LocalPlayback, SetupPage.Appearance,
                     SetupPage.Sidebar, SetupPage.Sound, SetupPage.Notifications,
                 })
        {
            Assert.Null(SetupGating.StepLabelKey(page));
        }
    }

    [Theory]
    [InlineData(SetupPage.Welcome, 0f)]
    [InlineData(SetupPage.Terms, 1f / 7f)]
    [InlineData(SetupPage.Notifications, 7f / 7f)]
    [InlineData(SetupPage.Done, 1f)]
    public void Progress_MatchesTheLadder(SetupPage page, float expected)
        => Assert.Equal(expected, SetupGating.Progress(page), precision: 5);

    // ── RoadmapPages / RoadmapLabelKey / RoadmapIndexFor (work package A) ─────────────────────────────────────────────

    [Fact]
    public void RoadmapPages_IsExactlyTheSevenMiddlePages_InEnumOrder()
    {
        SetupPage[] expected =
        [
            SetupPage.Terms, SetupPage.SignIn, SetupPage.LocalPlayback, SetupPage.Appearance,
            SetupPage.Sidebar, SetupPage.Sound, SetupPage.Notifications,
        ];
        Assert.Equal(expected, SetupGating.RoadmapPages);
    }

    [Fact]
    public void RoadmapLabelKey_IsDistinctPerPage()
    {
        var keys = SetupGating.RoadmapPages.Select(SetupGating.RoadmapLabelKey).ToList();
        Assert.Equal(keys.Distinct().Count(), keys.Count);
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
    }

    [Fact]
    public void RoadmapLabelKey_ThrowsForNonRoadmapPages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupGating.RoadmapLabelKey(SetupPage.Welcome));
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupGating.RoadmapLabelKey(SetupPage.Done));
    }

    [Theory]
    [InlineData(SetupPage.Welcome, 0)]
    [InlineData(SetupPage.Terms, 0)]
    [InlineData(SetupPage.SignIn, 1)]
    [InlineData(SetupPage.LocalPlayback, 2)]
    [InlineData(SetupPage.Appearance, 3)]
    [InlineData(SetupPage.Sidebar, 4)]
    [InlineData(SetupPage.Sound, 5)]
    [InlineData(SetupPage.Notifications, 6)]
    [InlineData(SetupPage.Done, 7)]
    public void RoadmapIndexFor_MapsPagesOntoTheirRoadmapRow(SetupPage page, int expected)
        => Assert.Equal(expected, SetupGating.RoadmapIndexFor(page));
}
