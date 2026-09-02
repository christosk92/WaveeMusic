using System;
using FluentGpu.Signals;
using Wavee.Backend.Audio;

namespace Wavee;

/// <summary>The setup wizard's live model — a plain object living OUTSIDE the element tree (never itself an
/// <c>Embed.Comp</c> instance), so the pre-auth→post-auth remount (the real shell does not exist until sign-in
/// completes) can hand the SAME session to a second mount site without losing page/direction state.
///
/// <para>Signals-first: every piece of state the dialog needs to react to lives on a <see cref="Signal{T}"/> here,
/// never a plain field the shell would have to poll for changes. <see cref="Dir"/> MUST be written before
/// <see cref="Page"/> in the same flush — see <see cref="Advance"/> and the load-bearing comment on
/// <c>ContentHost.cs</c> (Features/Shell) this mirrors: a motion-only write must never re-activate the current page,
/// so <c>SetupDialog</c>'s KeepAlive boundary reads <see cref="Dir"/> by <c>Peek()</c> inside its
/// <c>TransitionFor</c>, never by subscribing.</para></summary>
sealed class SetupSession
{
    // ── statics: the one live session and the shell's blur flag. ───────────────────────────────────────────────────
    public static SetupSession? Current { get; set; }

    /// <summary>The shell reads this to dim (<see cref="SetupCover.Dim"/>) behind the plate; <see cref="SetupCover.None"/>
    /// = nothing to cover (pre-auth) or not covering. Set by <see cref="SetupDialog.Open"/> — only a <c>bare: false</c>
    /// mount ever leaves <c>None</c> (there IS a live shell behind it to cover); cleared back to <c>None</c> on every
    /// close path from the same method's <c>ClosedAction</c>.</summary>
    public static readonly Signal<SetupCover> Covering = new(SetupCover.None);

    /// <summary>Monotonic "the wizard's pending/completed marker may just have changed" signal, bumped by
    /// <see cref="SetupDialog.Open"/>'s <c>ClosedAction</c> on every close path (defer OR complete). Exists because
    /// <c>WaveeApp</c>'s login gate reads <see cref="SetupGating.IsPending"/> off plain <c>IAppSettings</c> — not a
    /// signal — so without this, closing the wizard's <c>bare: true</c> pre-auth mount would leave
    /// <c>SetupPreAuthRoot</c> mounted forever — a titlebar over a transparent body, no dialog, and nothing behind it
    /// to fall back to: <c>WaveeApp</c> subscribes to this so the gate re-evaluates immediately instead of waiting for
    /// some unrelated re-render.</summary>
    public static readonly Signal<int> MarkerEpoch = new(0);
    public static void BumpMarker() => MarkerEpoch.Value++;

    public readonly Signal<SetupPage> Page = new(SetupPage.Terms);
    public readonly Signal<NavTransitionKind> Dir = new(NavTransitionKind.Neutral);

    /// <summary>Why this run of the wizard exists — see <see cref="SetupEntryPoint"/>'s own doc comment.</summary>
    public readonly SetupEntryPoint Entry;

    /// <summary>Whether <see cref="SetupPage.SignIn"/> is skipped because the user is already authenticated
    /// (<see cref="SetupEntryPoint.TermsRearm"/>, or a carried-over session that reached the shell already signed
    /// in). Computed once at construction via <see cref="SetupGating.SkipSignIn"/>.</summary>
    public readonly bool SkipSignIn;

    /// <summary>Wired by <c>WaveeApp.Render</c> (the only place the login takeover's own intents live): open the
    /// system browser for the PKCE login.</summary>
    public Action? StartBrowser { get; set; }
    /// <summary>Wired by <c>WaveeApp.Render</c>: request a fresh device-code pairing after Expired.</summary>
    public Action? RestartCode { get; set; }
    /// <summary>Wired by <c>WaveeApp.Render</c>: abandon the in-flight sign-in attempt (cancel the shared session CTS,
    /// drop the login snapshot back to <c>LoggedOut</c>) WITHOUT quitting. This is what the Busy footer's "Cancel"
    /// means — the button says Cancel, so it must cancel; routing it to <see cref="QuitApp"/> (as it did) killed the
    /// app out from under a user who only wanted to stop waiting on a pairing code.</summary>
    public Action? CancelSignIn { get; set; }
    /// <summary>SignIn's Done-facet "Not me": sign this PC out so a different account can sign in —
    /// <c>Services.LogoutAsync</c> in the real app (clears the stored credential, flips the gate; the SignIn page
    /// drops back to Idle and mints a fresh code).</summary>
    public Action? SwitchAccount { get; set; }
    /// <summary>Wired by <c>WaveeApp.Render</c>: quit the app (Terms's "Decline" exit on FirstRun/Reauth).</summary>
    public Action? QuitApp { get; set; }
    /// <summary>Set by <see cref="SetupDialog.Open"/> to the overlay handle's <c>Close</c> — the session can close its
    /// own shell without ever referencing an overlay type itself.</summary>
    public Action? RequestClose { get; set; }

    // ── Ambient plumbing, attached lazily by whichever page renders first (SetupPagePlaceholders.SetupPageCapture,
    // mounted around EVERY page) ─────────────────────────────────────────────────────────────────────────────────────
    // SetupSession.Primary()/Secondary()/BuildCtx() are plain methods invoked from OUTSIDE any component render (a
    // footer button's onClick, a keyboard shortcut) — they have no hook context of their own, so anything they need
    // from the ambient tree (settings, the live playback bridge, the runtime model) has to already be sitting on the
    // session by the time they run. Every attach below is idempotent/safe to call every render; by the time a user
    // can click a footer button the page underneath it has already rendered at least once.

    /// <summary>The live playback bridge — <see cref="PlaybackBridge.Login"/>/<see cref="PlaybackBridge.Auth"/> feed
    /// the SignIn facet in <see cref="BuildCtx"/>; <see cref="PlaybackBridge.RuntimeStatus"/> feeds
    /// <see cref="SetupGating.SkipsLocalPlayback"/> after auto-advancing past SignIn.</summary>
    public PlaybackBridge? Bridge { get; private set; }
    public void AttachBridge(PlaybackBridge bridge) => Bridge = bridge;

    /// <summary>The settings store — needed by <see cref="Primary"/>/<see cref="Secondary"/>'s terminal
    /// <see cref="SetupGating.MarkCompleted"/> writes and Terms's terms-acceptance write.</summary>
    public IAppSettings? Settings { get; private set; }
    public void AttachSettings(IAppSettings settings) => Settings = settings;

    /// <summary>The local-playback runtime provisioning model (<c>PlaybackRuntimeSetupCard.cs</c>'s
    /// <see cref="PlaybackRuntimeSetupModel"/>), lazily constructed ONCE and shared by the LocalPlayback page's body
    /// AND this session's own <see cref="Primary"/>/<see cref="Secondary"/>/<see cref="BuildCtx"/> — the same
    /// SetupBody/SetupFooter "one model reference, two readers" pattern, moved down to page scope.</summary>
    public PlaybackRuntimeSetupModel? Runtime { get; private set; }

    public PlaybackRuntimeSetupModel EnsureRuntime(Services services, IAppSettings settings, PlaybackBridge bridge, Action<Action> post)
    {
        if (Runtime is null)
        {
            Runtime = new PlaybackRuntimeSetupModel(services, settings, bridge, () => services.PlayPlayProvisioner, post);
            // LocalPlayback is the wizard's LAST page (SetupGating.IsLastPage) — "close this page" (Ready's primary,
            // "Open Wavee") means finish the wizard outright, not advance into a Done page that no longer exists.
            Runtime.OnClose = () =>
            {
                if (Settings is { } s) SetupGating.MarkCompleted(s);
                RequestClose?.Invoke();
            };
            // The escape hatch to close the ENTIRE wizard (not just this page) — e.g. a diagnostics hand-off.
            Runtime.OnWizardExit = () => RequestClose?.Invoke();
        }
        return Runtime;
    }

    /// <summary>Set once the wizard's LocalPlayback page explicitly declines local playback ("Not now" while Offer or
    /// Failed). Recorded for the same reason the standalone dialog records a decline — an explicit "no" is a settled
    /// choice, not a still-pending one — even though nothing in the 3-screen wizard itself reads it back today.</summary>
    public bool RuntimeDeclined { get; private set; }
    public void DeclineRuntime() => RuntimeDeclined = true;

    public SetupSession(SetupEntryPoint entry, bool alreadyAuthenticated, SetupPage startPage = SetupPage.Terms)
    {
        Entry = entry;
        SkipSignIn = SetupGating.SkipSignIn(alreadyAuthenticated);
        if (startPage != SetupPage.Terms) Page.Value = startPage;
    }

    /// <summary>Must the shell block dismissal (Escape / light-dismiss / programmatic close) right now? A live
    /// LocalPlayback catalog/download/verify (<see cref="PlaybackRuntimeSetupModel.IsBusy"/>) blocks it — exactly the
    /// veto the standalone dialog's own <c>Closing</c> handler already enforces, folded in here so the wizard's
    /// dismiss/Escape/light-dismiss paths honor it identically.</summary>
    public bool IsBusy => Runtime?.IsBusy ?? false;

    static SetupRuntimeFacet RuntimeFacetFor(PlaybackRuntimeSetupModel.Phase phase) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.Offer => SetupRuntimeFacet.Offer,
        PlaybackRuntimeSetupModel.Phase.FetchingCatalog => SetupRuntimeFacet.Catalog,
        PlaybackRuntimeSetupModel.Phase.Downloading => SetupRuntimeFacet.Downloading,
        PlaybackRuntimeSetupModel.Phase.Verifying => SetupRuntimeFacet.Verifying,
        PlaybackRuntimeSetupModel.Phase.Untrusted => SetupRuntimeFacet.Untrusted,
        PlaybackRuntimeSetupModel.Phase.Ready => SetupRuntimeFacet.Ready,
        PlaybackRuntimeSetupModel.Phase.Failed => SetupRuntimeFacet.Failed,
        PlaybackRuntimeSetupModel.Phase.Advanced => SetupRuntimeFacet.Versions,
        _ => SetupRuntimeFacet.Offer,
    };

    /// <summary>Assemble the current <see cref="SetupCtx"/> for the footer. SignIn/Runtime read off the bridge/model
    /// once attached (<see cref="AttachBridge"/>/<see cref="EnsureRuntime"/>) — before either page has ever rendered,
    /// both fold to the same Idle/Offer default those types themselves start from, so there is nothing to desync.</summary>
    public SetupCtx BuildCtx()
    {
        var signIn = Bridge is { } b
            ? SetupCommands.Project(b.Login.Value.Phase, b.Login.Value.Step, b.Auth.Value)
            : SetupSignInPhase.Idle;
        var runtime = Runtime is { } m ? RuntimeFacetFor(m.PhaseSig.Value) : SetupRuntimeFacet.Offer;
        return new SetupCtx(Page.Value, signIn, runtime);
    }

    /// <summary>Move to <paramref name="to"/>, writing <see cref="Dir"/> BEFORE <see cref="Page"/> in the same
    /// flush — see the class doc-comment; the direction must already be correct by the time the page write re-runs
    /// the KeepAlive boundary.</summary>
    public void Advance(SetupPage to)
    {
        var from = Page.Peek();
        Dir.Value = to == from ? NavTransitionKind.Neutral
                  : (int)to > (int)from ? NavTransitionKind.Forward : NavTransitionKind.Back;
        Page.Value = to;
    }

    /// <summary>Called by <see cref="PrimarySignIn"/>'s Done arm once the user presses "Yes, continue" on the "Is
    /// this you?" confirmation page (Authenticated + Premium) — moving on is the user's own click, never automatic.
    /// Skipping Local playback (a Reauth whose runtime is already Ready, <see cref="SetupGating.SkipsLocalPlayback"/>)
    /// finishes the wizard outright instead of showing a page with nothing left to offer.</summary>
    public void FinishSignIn(bool skipLocalPlayback)
    {
        if (skipLocalPlayback)
        {
            if (Settings is { } s) SetupGating.MarkCompleted(s);
            RequestClose?.Invoke();
        }
        else Advance(SetupPage.LocalPlayback);
    }

    /// <summary>The primary command. Terms (Accept) writes the terms acceptance then advances (or, for
    /// <see cref="SetupEntryPoint.TermsRearm"/>, finishes outright — there is nothing else left to ask a completed,
    /// signed-in install); SignIn/LocalPlayback route through their own phase machine while busy/failed/mid-flow.</summary>
    public void Primary()
    {
        switch (Page.Peek())
        {
            case SetupPage.Terms: PrimaryTerms(); break;
            case SetupPage.SignIn: PrimarySignIn(); break;
            case SetupPage.LocalPlayback: PrimaryLocalPlayback(); break;
        }
    }

    /// <summary>Accept is a CONSENT, so it has to leave a durable record: without this write the wizard asked, the
    /// user answered, and nothing anywhere remembered it — a re-consent when the terms change
    /// (<see cref="SetupGating.NeedsTermsRearm"/>) would then be impossible to target, because every install looks
    /// equally un-asked. Written BEFORE the advance/close so a crash in between cannot lose an acceptance the user
    /// already gave.</summary>
    void PrimaryTerms()
    {
        if (Settings is { } settings) settings.Set(WaveeSettings.TermsAcceptedVersion, SetupGating.TermsVersion);
        if (Entry == SetupEntryPoint.TermsRearm)
        {
            if (Settings is { } s) SetupGating.MarkCompleted(s);
            RequestClose?.Invoke();
        }
        else Advance(SetupGating.NextPage(SetupPage.Terms, SkipSignIn));
    }

    /// <summary>The SignIn page carries no state of its own, so its primary is exactly the per-phase login action
    /// (<see cref="StartBrowser"/>/<see cref="RestartCode"/>). <see cref="SetupSignInPhase.Done"/> — "Is this you?"
    /// — is a real, user-clicked confirmation: no auto-advance skips past it any more, so this "Yes, continue" arm is
    /// exactly what moves the wizard on.</summary>
    void PrimarySignIn()
    {
        switch (SignInPhase())
        {
            case SetupSignInPhase.Idle: StartBrowser?.Invoke(); break;
            case SetupSignInPhase.Done: FinishSignIn(SkipsLocalPlaybackNow()); break;
            case SetupSignInPhase.Failed:
            case SetupSignInPhase.Expired: RestartCode?.Invoke(); break;
            case SetupSignInPhase.Premium: LoginView.OpenUrl("https://www.spotify.com/premium"); break;
            // Busy: SetupCommands.SignInRow gates PrimaryEnabled false — SetupWizardFooter never invokes this.
        }
    }

    SetupSignInPhase SignInPhase() => Bridge is { } b
        ? SetupCommands.Project(b.Login.Value.Phase, b.Login.Value.Step, b.Auth.Value)
        : SetupSignInPhase.Idle;

    bool SkipsLocalPlaybackNow() =>
        SetupGating.SkipsLocalPlayback(Entry, Bridge?.RuntimeStatus.Peek().Outcome ?? ProvisioningOutcome.NeverAttempted);

    /// <summary>Reuse-not-rebuild: every one of these is an EXISTING <see cref="PlaybackRuntimeSetupModel"/> method,
    /// the same ones <c>PlaybackRuntimeSetupCard</c>'s own standalone footer calls. Ready's <c>Close()</c> runs
    /// <c>OnClose</c> (<see cref="EnsureRuntime"/> wires it to finish the wizard — this is the LAST page, there is no
    /// Done page to advance into).</summary>
    void PrimaryLocalPlayback()
    {
        if (Runtime is not { } m) return;
        switch (m.PhaseSig.Value)
        {
            case PlaybackRuntimeSetupModel.Phase.Offer: m.StartDownload(); break;
            case PlaybackRuntimeSetupModel.Phase.Advanced: m.InstallSelected(); break;
            case PlaybackRuntimeSetupModel.Phase.Untrusted: m.ConfirmUntrusted(); break;
            case PlaybackRuntimeSetupModel.Phase.Ready: m.Close(); break;
            case PlaybackRuntimeSetupModel.Phase.Failed: m.Retry(); break;
            // Catalog/Downloading/Verifying: SetupCommands.LocalPlaybackRow gates PrimaryEnabled false.
        }
    }

    /// <summary>The secondary command. Terms's "Decline" quits (or just closes, for a TermsRearm run — see below);
    /// SignIn/LocalPlayback route through their own phase machine.</summary>
    public void Secondary()
    {
        switch (Page.Peek())
        {
            case SetupPage.Terms: SecondaryTerms(); break;
            case SetupPage.SignIn: SecondarySignIn(); break;
            case SetupPage.LocalPlayback: SecondaryLocalPlayback(); break;
        }
    }

    /// <summary>"Decline" on FirstRun/Reauth QUITS — Wavee cannot be used without signing in, and this wizard is the
    /// only place to do it, so there is nothing behind this dialog to fall back to; the setup marker stays armed so
    /// the next launch resumes here (see <c>SetupDialog.Open</c>'s <c>ClosedAction</c>). A <see cref="SetupEntryPoint.TermsRearm"/>
    /// run DOES have a live shell behind it (a completed, signed-in install), so its own "Decline" button just closes
    /// instead — see <see cref="SetupGating.CanDismiss"/>'s remarks for the same distinction on Escape.</summary>
    void SecondaryTerms()
    {
        if (Entry == SetupEntryPoint.TermsRearm) RequestClose?.Invoke();
        else QuitApp?.Invoke();
    }

    /// <summary>Pre-auth, "giving up" on Idle/Failed/Expired means quitting — there is no shell to fall back to
    /// without an account, which is exactly why this session carries <see cref="QuitApp"/>. The two exceptions read
    /// off the button's OWN label, which is the whole point:
    /// <list type="bullet">
    /// <item>BUSY says "Cancel" (<c>SetupCommands.SignInRow</c>) — so it cancels the attempt
    /// (<see cref="CancelSignIn"/>) and lands back on Idle with the two option cards. It must never quit: the label
    /// promises to stop the sign-in, not to stop Wavee.</item>
    /// <item>PREMIUM says "Use a different account" — so it re-mints the device code.</item>
    /// <item>DONE says "Not me" (reached only by the fake backend / a slow post(), like its Primary counterpart) —
    /// so it signs this PC out (<see cref="SwitchAccount"/>) and the page drops back to Idle with a fresh code.</item>
    /// </list></summary>
    void SecondarySignIn()
    {
        switch (SignInPhase())
        {
            case SetupSignInPhase.Busy: CancelSignIn?.Invoke(); break;
            case SetupSignInPhase.Premium: RestartCode?.Invoke(); break;
            case SetupSignInPhase.Done: SwitchAccount?.Invoke(); break;
            default: QuitApp?.Invoke(); break;
        }
    }

    /// <summary>"Not now"/"Cancel"/"Back" per <see cref="PlaybackRuntimeSetupModel.Phase"/>. "Not now" on Offer/Failed
    /// finishes the wizard outright (there is no Done page to advance into any more) — it ALSO burns
    /// <see cref="DeclineRuntime"/> so a later diagnostic knows the user explicitly declined rather than never having
    /// been asked.</summary>
    void SecondaryLocalPlayback()
    {
        if (Runtime is not { } m) return;
        switch (m.PhaseSig.Value)
        {
            case PlaybackRuntimeSetupModel.Phase.Offer:
            case PlaybackRuntimeSetupModel.Phase.Failed:
                m.DismissSetting();
                DeclineRuntime();
                if (Settings is { } s) SetupGating.MarkCompleted(s);
                RequestClose?.Invoke();
                break;
            case PlaybackRuntimeSetupModel.Phase.Advanced: m.Back(); break;
            case PlaybackRuntimeSetupModel.Phase.Untrusted: m.CancelUntrusted(); break;
            case PlaybackRuntimeSetupModel.Phase.FetchingCatalog:
            case PlaybackRuntimeSetupModel.Phase.Downloading: m.Cancel(); break;
            // Verifying: SetupCommands.LocalPlaybackRow's SecondaryKey is null — no button to invoke this.
        }
    }

    /// <summary>The plate's Back affordance (top-left icon button + Backspace) — always a bare previous-page walk.
    /// Only ever shown on LocalPlayback (<see cref="SetupCommands.Resolve"/>'s <c>ShowBack</c>).</summary>
    public void Back() => Advance(SetupGating.PrevPage(Page.Peek(), SkipSignIn));
}
