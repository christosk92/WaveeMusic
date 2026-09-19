using System;
using Wavee.Backend.Audio;

namespace Wavee;

/// <summary>The pages of the first-run setup wizard, in display order — Rise's own two-step ladder: Terms is
/// "pre-setup" (no step number), then Sign in (step 1 of 2), then Local playback (step 2 of 2). Renamed from
/// <c>Welcome</c>: the wizard's first page IS the terms page now (Rise's <c>TermsPage</c>), not a welcome screen with
/// a terms card folded in, and there is no pre-dialog splash in front of it: the dialog opens immediately on every
/// entry point (<see cref="SetupPreAuthOpener"/>). The numeric values are NOT persisted (nothing writes a
/// <see cref="SetupPage"/> to settings), so renumbering this enum is safe. <see cref="SetupGating.NextPage"/>/
/// <see cref="SetupGating.PrevPage"/> walk it; <see cref="SetupGating.StepNumber"/> maps it onto the footer's
/// "Step N of 2".</summary>
public enum SetupPage { Terms = 0, SignIn = 1, LocalPlayback = 2 }

/// <summary>§(setup wizard) — the PURE decisions behind the wizard shell: whether it is armed/completed, the two
/// exit-path writes (<see cref="MarkCompleted"/>/<see cref="MarkDeferred"/>), which page comes next/previous when
/// sign-in is being skipped, the footer's progress label/fraction, and the handful of "should this even show a
/// second prompt" gates the toast/banner and the sign-in auto-advance read.
///
/// ENGINE-FREE BY CONSTRUCTION (System + IAppSettings + the generated Strings consts, plus the equally engine-free
/// <see cref="SetupEntryPoint"/>/<see cref="SetupSignInPhase"/>/<see cref="ProvisioningOutcome"/>
/// enums). That is load-bearing exactly like <c>SidebarDesignGating</c>: this file is source-included by
/// <c>Wavee.Tests</c> (which has no FluentGpu.Engine reference), so <c>SetupGatingTests</c> drives the REAL state
/// machine instead of a copy of it. Nothing here may reference <c>Signal&lt;T&gt;</c>, <c>Element</c>, <c>Loc</c> or
/// any other engine type — the wizard's visuals and command labels live elsewhere (<c>SetupCommands.cs</c> for the
/// latter).
///
/// WHY A SEPARATE FILE. Exactly the <c>SidebarDesignGating</c> rationale: getting either the gate or the two markers
/// wrong is unrecoverable per install — a marker burned too early denies the wizard to the fresh install it exists
/// for, and a marker never written re-shows a "one-time" wizard on every launch. <see cref="MarkDeferred"/> being a
/// no-op once <see cref="MarkCompleted"/> has run is the one invariant most likely to be gotten backwards, and is
/// exactly the kind of thing a unit test pins and a code review does not.</summary>
static class SetupGating
{
    /// <summary>Is the wizard armed and not yet shown to completion or deferral? Mirrors
    /// <c>SidebarDesignGating.ShouldShowChooser</c>'s one-boolean-read shape. Null-tolerant: no settings store ⇒ the
    /// wizard never shows (nothing to persist its exit, so showing it would be a dialog with no memory).</summary>
    public static bool IsPending(IAppSettings? settings)
        => settings is not null && settings.Get(WaveeSettings.SetupPending);

    /// <summary>Has the user reached the end of the wizard at least once, ever? Independent of <see cref="IsPending"/>
    /// — a deferred wizard leaves this false forever until the user actually finishes it.</summary>
    public static bool IsCompleted(IAppSettings? settings)
        => settings is not null && settings.Get(WaveeSettings.SetupCompleted);

    /// <summary>Finishing the wizard. Sets <c>SetupCompleted</c> and clears <c>SetupPending</c>. Idempotent; returns
    /// true only on the Completed transition (the log line / a test's "flipped once" assertion). Completed beats a
    /// later <see cref="MarkDeferred"/> call by construction — deferring never un-sets Completed, so calling this
    /// first and deferring afterward (which should not happen, but must not corrupt state if it does) leaves
    /// Completed true.
    ///
    /// <para>The two writes are INDEPENDENT, and that is load-bearing: an already-completed install can be re-armed
    /// (<see cref="NeedsTermsRearm"/>), and it finishes with <c>SetupCompleted</c> already true. Short-circuiting on
    /// Completed — as an earlier revision did — would then leave <c>SetupPending</c> set forever, so the wizard would
    /// re-open on every single launch with no way to satisfy it.</para></summary>
    public static bool MarkCompleted(IAppSettings? settings)
    {
        if (settings is null) return false;
        bool firstCompletion = !settings.Get(WaveeSettings.SetupCompleted);
        if (firstCompletion) settings.Set(WaveeSettings.SetupCompleted, true);
        if (settings.Get(WaveeSettings.SetupPending)) settings.Set(WaveeSettings.SetupPending, false);
        return firstCompletion;
    }

    /// <summary>Deferring — "Not now", Escape, light-of-modal, a shutdown-time close. Clears <c>SetupPending</c> only;
    /// deliberately does NOT touch <c>SetupCompleted</c>. A "one-time" dialog that comes back on the next launch is the
    /// failure mode this whole file exists to prevent: deferred means "don't show again automatically". No-op once
    /// the wizard is already completed (idempotent either way) — a stray deferral after finishing must never make a
    /// finished wizard look unfinished.</summary>
    public static bool MarkDeferred(IAppSettings? settings)
    {
        if (settings is null || settings.Get(WaveeSettings.SetupCompleted)) return false;
        if (!settings.Get(WaveeSettings.SetupPending)) return false;
        settings.Set(WaveeSettings.SetupPending, false);
        return true;
    }

    /// <summary>Skip the SignIn page when the user is already authenticated — re-showing a login screen to someone
    /// already logged in (<see cref="SetupEntryPoint.Reauth"/> once it has already succeeded, or a carried-over
    /// FirstRun session that reached the shell already signed in) would be nonsensical.</summary>
    public static bool SkipSignIn(bool authed) => authed;

    /// <summary>May Escape / light-dismiss / a programmatic close actually dismiss the wizard right now? Only
    /// <see cref="SetupEntryPoint.TermsRearm"/> may be dismissed this way — it re-opens a COMPLETED, still-signed-in
    /// install that has a live shell behind it, so Escape just means "put me back in the app" and nothing is lost.
    /// <see cref="SetupEntryPoint.FirstRun"/>/<see cref="SetupEntryPoint.Reauth"/> are Wavee's ONLY sign-in surface
    /// with nothing behind them — dismissing one strands the user on <c>SetupPreAuthRoot</c>'s bare titlebar over
    /// Mica with no way back in. "Quit" is still offered on every page for an honest exit instead.
    ///
    /// <para><paramref name="busy"/> vetoes even TermsRearm: a live catalog/download/verify
    /// (<c>PlaybackRuntimeSetupModel.IsBusy</c>) must not be torn out from under itself.</para></summary>
    public static bool CanDismiss(SetupEntryPoint entry, bool busy) => entry == SetupEntryPoint.TermsRearm && !busy;

    /// <summary>What an Escape on the wizard plate does. Returns <c>true</c> when the plate itself may close;
    /// <c>false</c> when vetoed by <see cref="CanDismiss"/>. <paramref name="nestedOpen"/> is always false now — the
    /// Terms page prints the full agreement inline (Rise has no nested disclosure to close first) — kept as a
    /// parameter so a future nested popup (a signature-details dialog, say) has somewhere to plug in without another
    /// signature change.</summary>
    public static bool EscapeClosesPlate(bool nestedOpen, SetupEntryPoint entry, bool busy)
        => !nestedOpen && CanDismiss(entry, busy);

    /// <summary>The terms-acceptance revision this build requires. Written by <c>SetupSession</c>'s Terms primary
    /// on Accept (a consent, so it has to leave a durable record); compared against by <see cref="NeedsTermsRearm"/>
    /// and <see cref="SetupBootstrap"/>.</summary>
    public const int TermsVersion = 1;

    /// <summary>Must a COMPLETED install be re-armed because the terms it accepted are older than this build's
    /// (<see cref="TermsVersion"/>)? Deliberately gated on <paramref name="completed"/>: a wizard that has never
    /// finished is already pending, and re-arming it would be a no-op that muddies the "why is this pending" answer.
    /// The re-arm re-shows the wizard (<see cref="SetupEntryPoint.TermsRearm"/>), whose Terms page writes the new
    /// version on Accept; there is nothing else to re-walk for someone already signed in.</summary>
    public static bool NeedsTermsRearm(bool completed, int accepted, int current) => completed && accepted < current;

    /// <summary>A completed install that predates terms versioning (accepted == 0) accepted the terms as part of the
    /// wizard it already finished; it is stamped with <paramref name="current"/> rather than re-shown the wizard. Only a
    /// LATER bump of <see cref="TermsVersion"/> re-arms.</summary>
    public static bool GrandfathersTerms(bool completed, int accepted) => completed && accepted == 0;

    /// <summary>Must a "completed" install be treated as a brand-new one because its DATA folder — not its registry
    /// settings — is gone? Settings live in the registry (<c>HKCU\Software\Wavee\Wavee\Settings</c>) but the library
    /// lives in <c>%LOCALAPPDATA%\Wavee</c>; a user who wipes only the latter (or restores a machine image, or
    /// migrates to a new PC that copied the registry but not the data root) still has <c>SetupCompleted=true</c> even
    /// though <see cref="SidebarBootstrap.IsFreshInstall"/> now reads every "this app has run before" witness as
    /// absent. <paramref name="fresh"/> is that PRESENT-TENSE probe result — re-run every launch (unlike the
    /// once-per-install <see cref="SetupBootstrap.TargetVersion"/> gate) because the data folder can disappear on any
    /// launch, not just the first one ever. <paramref name="completed"/>/<paramref name="termsAccepted"/> are the
    /// registry's OWN memory of a wizard that (it claims) already ran — either one being non-default is enough to
    /// prove this "fresh" read is really a wiped folder, not a genuine first run (a genuine first run has both at
    /// their defaults, so this returns false for it — nothing to reset).</summary>
    public static bool NeedsFreshInstallReset(bool fresh, bool completed, int termsAccepted)
        => fresh && (completed || termsAccepted > 0);

    /// <summary>Whether closing the PRE-auth overlay is an auth-gate handoff rather than a real wizard dismissal.
    /// All three witnesses are required: a post-auth dialog close must clean up normally; a pending logged-out wizard
    /// must stay put; and a completed/re-auth flow has no unfinished first-run session to carry forward.</summary>
    public static bool CarriesAcrossAuthGate(bool bare, bool pending, bool authenticated)
        => bare && pending && authenticated;

    /// <summary>The next page from <paramref name="page"/>, skipping <see cref="SetupPage.SignIn"/> when
    /// <paramref name="skipSignIn"/>, clamped at <see cref="SetupPage.LocalPlayback"/> (the last page — there is no
    /// Done page to advance into; its own primary/secondary close the wizard outright, see <see cref="IsLastPage"/>).</summary>
    public static SetupPage NextPage(SetupPage page, bool skipSignIn)
    {
        var next = (SetupPage)Math.Min((int)page + 1, (int)SetupPage.LocalPlayback);
        if (skipSignIn && next == SetupPage.SignIn)
            next = (SetupPage)Math.Min((int)next + 1, (int)SetupPage.LocalPlayback);
        return next;
    }

    /// <summary>The previous page from <paramref name="page"/>, skipping <see cref="SetupPage.SignIn"/> when
    /// <paramref name="skipSignIn"/>, clamped at <see cref="SetupPage.Terms"/>.</summary>
    public static SetupPage PrevPage(SetupPage page, bool skipSignIn)
    {
        var prev = (SetupPage)Math.Max((int)page - 1, (int)SetupPage.Terms);
        if (skipSignIn && prev == SetupPage.SignIn)
            prev = (SetupPage)Math.Max((int)prev - 1, (int)SetupPage.Terms);
        return prev;
    }

    /// <summary>The two-step ladder's total — Rise counts only SignIn/LocalPlayback ("Step 1 of 2" / "Step 2 of 2");
    /// Terms is "Pre-setup" (<see cref="StepNumber"/> returns null for it).</summary>
    public const int StepTotal = 2;

    /// <summary>The footer's "Step N of 2" numbers — null for <see cref="SetupPage.Terms"/> (Rise's own "Pre-setup"
    /// label; a <see cref="SetupEntryPoint.TermsRearm"/> run only ever visits Terms, so this naturally covers it too,
    /// with no separate entry-point parameter needed any more).</summary>
    public static (int Step, int Total)? StepNumber(SetupPage page) =>
        page == SetupPage.Terms ? null : ((int)page, StepTotal);

    /// <summary>The footer's progress fraction (Rise: <c>ProgressBar.Value = progress / Maximum</c>) — Terms 0,
    /// SignIn .5, LocalPlayback 1.0.</summary>
    public static float Progress(SetupPage page) => (int)page / (float)StepTotal;

    /// <summary>Rise shows the back button once "progress > 1" — the wizard's last page only.</summary>
    public static bool ShowsBack(SetupPage page) => page == SetupPage.LocalPlayback;

    /// <summary>Rise's <c>PaddingRectangle</c> (the 42-wide back-button spacer beside the header) collapses once the
    /// icon column is showing (the back button then floats over the icon column instead, top-left of the content
    /// region) — it only needs to reserve room next to the TITLE when there is no icon column AND this page shows a
    /// back button. Terms/SignIn never show back, so the spacer never applies to them regardless of width.</summary>
    public static bool BackSpacerApplies(SetupPage page, bool iconShown) => !iconShown && ShowsBack(page);

    /// <summary>Reauth on an install whose runtime is already provisioned ⇒ nothing left to do after SignIn — close
    /// the wizard outright instead of showing Local playback for a runtime that's already Ready. FirstRun always
    /// shows Local playback (a fresh install has never been offered it).</summary>
    public static bool SkipsLocalPlayback(SetupEntryPoint entry, ProvisioningOutcome outcome)
        => entry == SetupEntryPoint.Reauth && outcome == ProvisioningOutcome.Ready;

    /// <summary>Is <paramref name="page"/> the wizard's last page? There is no Done page any more — the last page's
    /// OWN primary/secondary close the wizard outright (<c>SetupSession.Primary</c>/<c>Secondary</c>'s LocalPlayback
    /// arms).</summary>
    public static bool IsLastPage(SetupPage page) => page == SetupPage.LocalPlayback;

    /// <summary>Should the runtime toast/banner (and the standalone "runtime ready" toast,
    /// <c>PlaybackRuntimeSetupModel.ShowsReadyToast</c>) stay silent right now? True while the wizard is merely ARMED
    /// and about to open (<paramref name="pending"/>) or while it is actually open (<paramref name="sessionOpen"/>):
    /// the wizard's own Local playback page IS the runtime prompt, and popping a toast/banner over — or just before —
    /// it reads as the same ask made twice.</summary>
    public static bool SuppressesRuntimePrompts(bool pending, bool sessionOpen) => pending || sessionOpen;
}
