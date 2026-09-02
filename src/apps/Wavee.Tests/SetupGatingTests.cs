using System;
using System.IO;
using Wavee.Backend.Audio;
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
        // LOAD-BEARING: a fresh install must not also arm the separate one-time sidebar-design popup chooser, or
        // both onboardings show on the same launch.
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
        // ExistingInstall(), not a bare MemoryAppSettings: this is testing the terms re-arm, not the fresh-install
        // reset below — a disk witness (library.db) keeps IsFreshInstall honestly false so the two decisions don't
        // collide (an empty _local + SetupCompleted=true is exactly NeedsFreshInstallReset's wiped-folder case).
        var settings = ExistingInstall();
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

    /// <summary>Accepting the terms is what STOPS the re-arm: the wizard's Terms page writes the current version,
    /// and the next launch must then leave the install alone instead of re-opening the wizard forever.</summary>
    [Fact]
    public void AcceptingTheTerms_StopsTheReArm()
    {
        // Simulate a pending re-arm (a later terms bump) the way the bootstrap would leave it, then the wizard accepting.
        var settings = ExistingInstall();
        settings.Set(WaveeSettings.SetupCompleted, true);
        settings.Set(WaveeSettings.SetupPending, true);

        // What SetupSession.Primary()'s Terms case does on Accept (TermsRearm), then the wizard closing.
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

    // ── SetupGating.NeedsFreshInstallReset / the wiped-data-folder case ──────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, 0, false)]   // not fresh at all — never resets regardless of what the registry says
    [InlineData(false, true, 1, false)]
    [InlineData(true, false, 0, false)]    // a genuine first run: registry already at its defaults — nothing to reset
    [InlineData(true, true, 0, true)]      // fresh disk + a completed registry ⇒ the wiped-folder case
    [InlineData(true, false, 1, true)]     // fresh disk + a stray terms acceptance ⇒ also the wiped-folder case
    [InlineData(true, true, 1, true)]
    public void NeedsFreshInstallReset_OnlyWhenDiskIsFreshButRegistryRemembers(bool fresh, bool completed, int termsAccepted, bool expected)
        => Assert.Equal(expected, SetupGating.NeedsFreshInstallReset(fresh, completed, termsAccepted));

    /// <summary>The bug this rule exists to fix: settings live in the registry (<c>HKCU\Software\Wavee\Wavee\Settings</c>)
    /// but the library lives in <c>%LOCALAPPDATA%\Wavee</c> — wiping only the latter must not leave a "completed"
    /// install stuck reopening as Reauth at "Is this you?" with the Terms page never shown again.</summary>
    [Fact]
    public void Run_WithAWipedDataFolder_ResetsACompletedInstallToFresh()
    {
        // First launch: a genuine existing install (library.db on disk), completed, terms accepted.
        var settings = ExistingInstall();
        SetupBootstrap.Run(settings, _local);
        Assert.True(settings.Get(WaveeSettings.SetupCompleted));
        Assert.Equal(SetupGating.TermsVersion, settings.Get(WaveeSettings.TermsAcceptedVersion));

        // The user wipes %LOCALAPPDATA%\Wavee — every disk witness IsFreshInstall reads is gone — but the registry
        // (this same MemoryAppSettings) survives untouched, so SetupCompleted/TermsAcceptedVersion still say "done".
        Directory.Delete(Path.Combine(_local, "Wavee"), recursive: true);

        SetupBootstrap.Run(settings, _local);   // next launch, wiped folder

        Assert.True(settings.Get(WaveeSettings.SetupPending));
        Assert.False(settings.Get(WaveeSettings.SetupCompleted));
        Assert.Equal(0, settings.Get(WaveeSettings.TermsAcceptedVersion));
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

    /// <summary>An already-completed install that is re-armed for new terms reaches the end of the wizard with
    /// <c>SetupCompleted</c> already true. <see cref="SetupGating.MarkCompleted"/> must STILL clear
    /// <c>SetupPending</c>, or the wizard re-opens on every launch with no way to satisfy it.</summary>
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

    // ── CanDismiss / EscapeClosesPlate ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Escape / light-dismiss may close ONLY a TermsRearm run, and only while nothing long-running is in
    /// flight. FirstRun/Reauth dismissed this way leave a bare titlebar over Mica with no way back in — the wizard is
    /// Wavee's only sign-in surface, so there is genuinely nothing behind it for those two.</summary>
    [Theory]
    [InlineData(SetupEntryPoint.TermsRearm, false, true)]    // the one dismissible case
    [InlineData(SetupEntryPoint.TermsRearm, true, false)]    // TermsRearm, but busy
    [InlineData(SetupEntryPoint.FirstRun, false, false)]     // FirstRun/Reauth: never
    [InlineData(SetupEntryPoint.FirstRun, true, false)]
    [InlineData(SetupEntryPoint.Reauth, false, false)]
    [InlineData(SetupEntryPoint.Reauth, true, false)]
    public void CanDismiss_OnlyTermsRearmThatIsNotBusy(SetupEntryPoint entry, bool busy, bool expected)
        => Assert.Equal(expected, SetupGating.CanDismiss(entry, busy));

    /// <summary>A nested popup (kept as a parameter for a future one — Rise's own Terms page has no disclosure to
    /// close first any more) spends the Escape before the plate itself ever sees it; with nothing nested the answer
    /// is exactly <see cref="SetupGating.CanDismiss"/>.</summary>
    [Theory]
    [InlineData(true, SetupEntryPoint.TermsRearm, false, false)]   // nested open on a dismissible TermsRearm → the disclosure closes, not the plate
    [InlineData(true, SetupEntryPoint.FirstRun, false, false)]     // nested open on a first run → same
    [InlineData(false, SetupEntryPoint.TermsRearm, false, true)]   // nothing nested, dismissible TermsRearm → the plate closes
    [InlineData(false, SetupEntryPoint.FirstRun, false, false)]    // nothing nested, first run → vetoed as before
    public void EscapeClosesPlate_NestedDisclosureSpendsTheKey(bool nestedOpen, SetupEntryPoint entry, bool busy, bool expected)
        => Assert.Equal(expected, SetupGating.EscapeClosesPlate(nestedOpen, entry, busy));

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
    public void NextPage_ClampsAtLocalPlayback()
    {
        Assert.Equal(SetupPage.LocalPlayback, SetupGating.NextPage(SetupPage.LocalPlayback, skipSignIn: false));
        Assert.Equal(SetupPage.LocalPlayback, SetupGating.NextPage(SetupPage.SignIn, skipSignIn: false));
    }

    [Fact]
    public void PrevPage_ClampsAtTerms()
    {
        Assert.Equal(SetupPage.Terms, SetupGating.PrevPage(SetupPage.Terms, skipSignIn: false));
        Assert.Equal(SetupPage.Terms, SetupGating.PrevPage(SetupPage.SignIn, skipSignIn: false));
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

    // ── StepNumber / Progress / ShowsBack / BackSpacerApplies ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SetupPage.SignIn, 1)]
    [InlineData(SetupPage.LocalPlayback, 2)]
    public void StepNumber_IsStepOfTwo(SetupPage page, int expectedStep)
    {
        var n = SetupGating.StepNumber(page);
        Assert.NotNull(n);
        Assert.Equal(expectedStep, n!.Value.Step);
        Assert.Equal(2, n!.Value.Total);
    }

    [Fact]
    public void StepNumber_IsNull_ForTerms()
    {
        // A TermsRearm run only ever visits Terms, so this single case covers Rise's "Pre-setup" label for BOTH
        // FirstRun/Reauth's pre-setup page and a completed install's re-arm — no separate entry-point parameter.
        Assert.Null(SetupGating.StepNumber(SetupPage.Terms));
    }

    [Fact]
    public void StepNumber_DoesNotChange_WhetherOrNotSignInIsSkipped()
    {
        // The concrete regression: LocalPlayback is step 2 whether the wizard skipped SignIn to get there or not.
        var viaFullRun = SetupGating.NextPage(SetupPage.Terms, false);
        Assert.Equal(SetupPage.SignIn, viaFullRun);
        Assert.Equal(SetupPage.LocalPlayback, SetupGating.NextPage(viaFullRun, false));
        var viaSkippedRun = SetupGating.NextPage(SetupPage.Terms, true);
        Assert.Equal(SetupPage.LocalPlayback, viaSkippedRun);
        Assert.Equal(2, SetupGating.StepNumber(SetupPage.LocalPlayback)!.Value.Step);
    }

    [Theory]
    [InlineData(SetupPage.Terms, 0f)]
    [InlineData(SetupPage.SignIn, 0.5f)]
    [InlineData(SetupPage.LocalPlayback, 1f)]
    public void Progress_MatchesTheTwoPageLadder(SetupPage page, float expected)
        => Assert.Equal(expected, SetupGating.Progress(page), precision: 5);

    [Theory]
    [InlineData(SetupPage.Terms, false)]
    [InlineData(SetupPage.SignIn, false)]
    [InlineData(SetupPage.LocalPlayback, true)]
    public void ShowsBack_OnlyLocalPlayback(SetupPage page, bool expected)
        => Assert.Equal(expected, SetupGating.ShowsBack(page));

    [Theory]
    [InlineData(SetupPage.LocalPlayback, true, false)]   // icon column showing → the spacer never applies
    [InlineData(SetupPage.LocalPlayback, false, true)]   // icon column dropped → the spacer reserves room beside the title
    [InlineData(SetupPage.Terms, false, false)]          // Terms never shows back, regardless of the icon column
    [InlineData(SetupPage.SignIn, false, false)]
    public void BackSpacerApplies_OnlyWhenBackShowsAndTheIconIsGone(SetupPage page, bool iconShown, bool expected)
        => Assert.Equal(expected, SetupGating.BackSpacerApplies(page, iconShown));

    // ── SkipsLocalPlayback / IsLastPage / SuppressesRuntimePrompts ──────────────────────────────────────────────────

    [Theory]
    [InlineData(SetupEntryPoint.Reauth, ProvisioningOutcome.Ready, true)]
    [InlineData(SetupEntryPoint.Reauth, ProvisioningOutcome.NeverAttempted, false)]
    [InlineData(SetupEntryPoint.Reauth, ProvisioningOutcome.RuntimeUnavailable, false)]
    [InlineData(SetupEntryPoint.FirstRun, ProvisioningOutcome.Ready, false)]
    [InlineData(SetupEntryPoint.TermsRearm, ProvisioningOutcome.Ready, false)]
    public void SkipsLocalPlayback_OnlyReauthWithAReadyRuntime(SetupEntryPoint entry, ProvisioningOutcome outcome, bool expected)
        => Assert.Equal(expected, SetupGating.SkipsLocalPlayback(entry, outcome));

    [Theory]
    [InlineData(SetupPage.Terms, false)]
    [InlineData(SetupPage.SignIn, false)]
    [InlineData(SetupPage.LocalPlayback, true)]
    public void IsLastPage_OnlyLocalPlayback(SetupPage page, bool expected)
        => Assert.Equal(expected, SetupGating.IsLastPage(page));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SuppressesRuntimePrompts_WhilePendingOrOpen(bool pending, bool sessionOpen, bool expected)
        => Assert.Equal(expected, SetupGating.SuppressesRuntimePrompts(pending, sessionOpen));
}
