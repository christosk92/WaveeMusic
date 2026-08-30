using System;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The setup wizard's live model — a plain object living OUTSIDE the element tree (never itself an
/// <c>Embed.Comp</c> instance), so the pre-auth→post-auth remount (a later step: the real shell does not exist until
/// sign-in completes) can hand the SAME session to a second mount site without losing page/direction/apply state.
///
/// <para>Signals-first: every piece of state the dialog needs to react to lives on a <see cref="Signal{T}"/> here,
/// never a plain field the shell would have to poll for changes. <see cref="Dir"/> MUST be written before
/// <see cref="Page"/> in the same flush — see <see cref="Advance"/> and the load-bearing comment on
/// <c>ContentHost.cs</c> (Features/Shell) this mirrors: a motion-only write must never re-activate the current page,
/// so <c>SetupDialog</c>'s KeepAlive boundary reads <see cref="Dir"/> by <c>Peek()</c> inside its
/// <c>TransitionFor</c>, never by subscribing.</para></summary>
sealed class SetupSession
{
    // ── statics: the monotonic "someone wants the wizard open" request — the PlaybackRuntimeBannerState Epoch/Bump
    // idiom (Features/Shell/PlaybackRuntimeBanner.cs) — plus the one live session and the shell's blur flag. ─────────
    public static SetupSession? Current { get; set; }
    public static readonly Signal<int> OpenRequest = new(0);
    public static void Bump() => OpenRequest.Value++;

    /// <summary>The shell reads this to dim (<see cref="SetupCover.Dim"/>) or to stay a genuine live preview
    /// (<see cref="SetupCover.Live"/>); <see cref="SetupCover.None"/> = nothing to cover (pre-auth) or not covering.
    /// Set by <see cref="SetupDialog.Open"/> — only a <c>bare: false</c> mount ever leaves <c>None</c> (there IS a
    /// live shell behind it to cover); cleared back to <c>None</c> on every close path from the same method's
    /// <c>ClosedAction</c>.</summary>
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

    /// <summary>Why this run of the wizard exists. <c>FirstRun</c> = a fresh install walking all seven steps.
    /// <c>Rerun</c> = "Run setup again" from Settings on a signed-in app (SignIn is skipped, a live shell is behind).
    /// <c>Reauth</c> = setup was already completed once but the account is signed out, so the wizard opens straight
    /// on <see cref="SetupPage.SignIn"/> — it is Wavee's ONLY sign-in surface, and re-walking terms/appearance for
    /// someone who has already chosen them would be nonsense.</summary>
    public enum EntryPoint { FirstRun, Rerun, Reauth }

    public readonly Signal<SetupPage> Page = new(SetupPage.Welcome);
    public readonly Signal<NavTransitionKind> Dir = new(NavTransitionKind.Neutral);
    public readonly Signal<SetupApplyState> Apply = new(SetupApplyState.Idle);

    /// <summary>Written only by <see cref="PrimaryDone"/> (jumps straight to 4). The Done page derives its
    /// checklist from real observables (<c>SetupDoneSteps</c>) — this stage is not a progress source.</summary>
    public readonly Signal<int> ApplyStage = new(0);

    public readonly EntryPoint Entry;

    /// <summary>Is there a live shell behind this wizard? True only for <see cref="EntryPoint.Rerun"/> — the one run
    /// that has somewhere to go back to, which is why it is also the only one Escape/light-dismiss may close
    /// (<see cref="SetupGating.CanDismiss"/>) and the only one whose Welcome "Not now" closes instead of quitting.</summary>
    public bool IsRerun => Entry == EntryPoint.Rerun;

    /// <summary>Whether <see cref="SetupPage.SignIn"/> is skipped because the user is already authenticated (the
    /// Settings → "Run setup again" rerun path). Computed once at construction via <see cref="SetupGating.SkipSignIn"/>.</summary>
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
    /// <summary>"Not me" on the Done ("Is this you?") phase: sign this PC out so a different account can sign in —
    /// <c>Services.LogoutAsync</c> in the real app (clears the stored credential, flips the gate; the page drops back to
    /// Idle and mints a fresh code).</summary>
    public Action? SwitchAccount { get; set; }
    /// <summary>Wired by <c>WaveeApp.Render</c>: quit the app (the Terms "Decline" exit).</summary>
    public Action? QuitApp { get; set; }
    /// <summary>Set by <see cref="SetupDialog.Open"/> to the overlay handle's <c>Close</c> — the session can close its
    /// own shell without ever referencing an overlay type itself.</summary>
    public Action? RequestClose { get; set; }
    /// <summary>A page's in-place disclosure that Escape should close BEFORE the plate is ever asked to (the Terms
    /// page's full agreement). Set while the disclosure is open, cleared when it closes/unmounts; consumed by
    /// <see cref="SetupDialog"/>'s close veto via <see cref="SetupGating.EscapeClosesPlate"/>. Null = nothing nested.</summary>
    public Action? EscapeConsumer { get; set; }

    // ── Ambient plumbing, attached lazily by whichever page renders first (SetupPagePlaceholders.SetupPageCapture,
    // mounted around EVERY page) ─────────────────────────────────────────────────────────────────────────────────────
    // SetupSession.Primary()/Secondary()/BuildCtx() are plain methods invoked from OUTSIDE any component render (a
    // footer button's onClick, a keyboard shortcut) — they have no hook context of their own, so anything they need
    // from the ambient tree (settings, the theme-transition request, the live playback bridge, the runtime model) has
    // to already be sitting on the session by the time they run. Every attach below is idempotent/safe to call every
    // render; by the time a user can click a footer button the page underneath it has already rendered at least once.

    /// <summary>The live playback bridge — <see cref="PlaybackBridge.Login"/>/<see cref="PlaybackBridge.Auth"/> feed
    /// the SignIn facet in <see cref="BuildCtx"/>; <see cref="PlaybackBridge.RuntimeStatus"/> feeds the Done page's
    /// "warming local playback" applying step.</summary>
    public PlaybackBridge? Bridge { get; private set; }
    public void AttachBridge(PlaybackBridge bridge) => Bridge = bridge;

    /// <summary>The settings store — needed by <see cref="Secondary"/>'s "Decide for me" writers
    /// (<see cref="SetupWrites.DecideFor"/>) and by <see cref="Primary"/>'s terminal <see cref="SetupGating.MarkCompleted"/>.</summary>
    public IAppSettings? Settings { get; private set; }
    public void AttachSettings(IAppSettings settings) => Settings = settings;

    /// <summary>The live theme-transition trigger (<c>ThemeControl.Request</c>) — optional (a null caller just means
    /// "Decide for me" jumps the theme instead of cross-fading it).</summary>
    public Action<float>? RequestTheme { get; private set; }
    public void AttachRequestTheme(Action<float>? requestTheme) => RequestTheme = requestTheme;

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
            // Inside the wizard "close this page" means advance, and a diagnostics exit means leave the WHOLE wizard —
            // see the doc comments on PlaybackRuntimeSetupModel.OnClose/OnWizardExit.
            Runtime.OnClose = () => Advance(SetupGating.NextPage(SetupPage.LocalPlayback, SkipSignIn));
            Runtime.OnWizardExit = () => RequestClose?.Invoke();
        }
        return Runtime;
    }

    /// <summary>Set once the wizard's LocalPlayback page explicitly declines local playback ("Not now" while Offer or
    /// Failed). The Done page's "Warming local playback" applying step reads this so it completes immediately for a
    /// user who opted to stay remote-only, instead of waiting forever for a runtime that will never arrive.</summary>
    public bool RuntimeDeclined { get; private set; }
    public void DeclineRuntime() => RuntimeDeclined = true;

    public SetupSession(EntryPoint entry, bool alreadyAuthenticated, SetupPage startPage = SetupPage.Welcome)
    {
        Entry = entry;
        SkipSignIn = SetupGating.SkipSignIn(alreadyAuthenticated);
        if (startPage != SetupPage.Welcome) Page.Value = startPage;
    }

    /// <summary>Must the shell block dismissal (Escape / light-dismiss / programmatic close) right now? A running
    /// Apply blocks it outright; so does a live LocalPlayback catalog/download/verify (<see cref="PlaybackRuntimeSetupModel.IsBusy"/>)
    /// — exactly the veto the standalone dialog's own <c>Closing</c> handler already enforces, folded in here so the
    /// wizard's dismiss/Escape/light-dismiss paths honor it identically.</summary>
    public bool IsBusy => Apply.Peek() == SetupApplyState.Running || (Runtime?.IsBusy ?? false);

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
        return new SetupCtx(Page.Value, signIn, runtime, Apply.Value, SkipSignIn, IsRerun);
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

    /// <summary>The primary command. Every non-phase page (and SignIn/LocalPlayback's terminal states) is a bare
    /// advance via <see cref="SetupGating.NextPage"/>; SignIn/LocalPlayback route through their own phase machine
    /// while busy/failed/mid-flow, and Done drives (or closes out) the apply step.</summary>
    public void Primary()
    {
        var page = Page.Peek();
        switch (page)
        {
            case SetupPage.SignIn: PrimarySignIn(); break;
            case SetupPage.LocalPlayback: PrimaryLocalPlayback(); break;
            case SetupPage.Done: PrimaryDone(); break;

            // "Accept" is a CONSENT, so it has to leave a durable record: without this write the wizard asked, the
            // user answered, and nothing anywhere remembered it — a re-consent when the terms change (see
            // SetupBootstrap.RearmForTerms / SetupGating.NeedsTermsRearm) would then be impossible to target, because
            // every install looks equally un-asked. Written BEFORE the advance so a crash between the two pages
            // cannot lose an acceptance the user already gave.
            case SetupPage.Terms:
                if (Settings is { } termsSettings)
                    termsSettings.Set(WaveeSettings.TermsAcceptedVersion, SetupTermsPage.CurrentVersion);
                Advance(SetupGating.NextPage(page, SkipSignIn));
                break;

            default: Advance(SetupGating.NextPage(page, SkipSignIn)); break;
        }
    }

    /// <summary>The SignIn page carries no state of its own, so its primary is exactly the per-phase login action
    /// (<see cref="StartBrowser"/>/<see cref="RestartCode"/>), minus the Idle/AwaitingApproval screen's own always-live
    /// "Log in" (that affordance lives ON the browser option card too, but the footer's primary is the one
    /// <see cref="SetupCommands.Resolve"/> already gates).</summary>
    void PrimarySignIn()
    {
        switch (SignInPhase())
        {
            case SetupSignInPhase.Idle: StartBrowser?.Invoke(); break;
            case SetupSignInPhase.Done: Advance(SetupGating.NextPage(SetupPage.SignIn, SkipSignIn)); break;
            case SetupSignInPhase.Failed:
            case SetupSignInPhase.Expired: RestartCode?.Invoke(); break;
            case SetupSignInPhase.Premium: LoginView.OpenUrl("https://www.spotify.com/premium"); break;
            // Busy: SetupCommands.SignInRow gates PrimaryEnabled false — SetupWizardFooter never invokes this.
        }
    }

    SetupSignInPhase SignInPhase() => Bridge is { } b
        ? SetupCommands.Project(b.Login.Value.Phase, b.Login.Value.Step, b.Auth.Value)
        : SetupSignInPhase.Idle;

    /// <summary>Reuse-not-rebuild: every one of these is an EXISTING <see cref="PlaybackRuntimeSetupModel"/> method,
    /// the same ones <c>PlaybackRuntimeSetupCard</c>'s own standalone footer calls.</summary>
    void PrimaryLocalPlayback()
    {
        if (Runtime is not { } m) return;
        switch (m.PhaseSig.Value)
        {
            case PlaybackRuntimeSetupModel.Phase.Offer: m.StartDownload(); break;
            case PlaybackRuntimeSetupModel.Phase.Advanced: m.InstallSelected(); break;
            case PlaybackRuntimeSetupModel.Phase.Untrusted: m.ConfirmUntrusted(); break;
            // Close() runs OnClose (EnsureRuntime wired it to Advance) — inside the wizard "Done" means "next page",
            // never tearing down an overlay this model never opened.
            case PlaybackRuntimeSetupModel.Phase.Ready: m.Close(); break;
            case PlaybackRuntimeSetupModel.Phase.Failed: m.Retry(); break;
            // Catalog/Downloading/Verifying: SetupCommands.LocalPlaybackRow gates PrimaryEnabled false.
        }
    }

    /// <summary>Complete the wizard in the button action itself. Every required choice was already persisted by its
    /// page, while sidebar projection, library synchronization, and playback warming are explicitly background work;
    /// none may strand the user behind a disabled final button. Burn <see cref="SetupGating.MarkCompleted"/> BEFORE
    /// closing so it beats the close callback's deferred-marker path.</summary>
    void PrimaryDone()
    {
        switch (Apply.Peek())
        {
            case SetupApplyState.Idle:
            case SetupApplyState.Done:
                ApplyStage.Value = 4;
                Apply.Value = SetupApplyState.Done;
                if (Settings is { } settings) SetupGating.MarkCompleted(settings);
                RequestClose?.Invoke();
                break;
        }
    }

    /// <summary>The secondary command. Welcome's "Not now" QUITS (see below); Terms declines (quits — the wizard
    /// cannot proceed without accepting); the four "Decide for me" pages write their sensible defaults
    /// (<see cref="SetupWrites.DecideFor"/>) then advance like <see cref="Primary"/>; SignIn/LocalPlayback route
    /// through their own phase machine; Done's "Not now" finishes setup and opens the app (the prototype's own
    /// page-8 behaviour — both of its footer buttons call <c>finish</c>; "Not now" only skips watching the
    /// apply-progress list, it does not un-finish the wizard).</summary>
    public void Secondary()
    {
        var page = Page.Peek();
        switch (page)
        {
            case SetupPage.Welcome:
                // "Not now" on the FIRST page quits, and must not simply close. Wavee cannot be used without signing
                // in, and this wizard is the only place to do it, so there is nothing behind this dialog to fall back
                // to — closing would leave a bare titlebar over Mica. The honest options are "start setup" or "quit", and
                // the setup marker stays armed so the next launch resumes here (see SetupDialog.Open's ClosedAction).
                // On a RERUN there IS a live shell behind, so closing is the right move instead of quitting.
                if (IsRerun) RequestClose?.Invoke();
                else QuitApp?.Invoke();
                break;

            case SetupPage.Done:
                // Setup IS finished by the time this page is reachable — "Not now" declines the applying readout,
                // not the wizard. Burn the completion marker so a relaunch does not re-run setup.
                if (Settings is { } doneSettings) SetupGating.MarkCompleted(doneSettings);
                RequestClose?.Invoke();
                break;

            case SetupPage.Appearance:
            case SetupPage.Sidebar:
            case SetupPage.Sound:
            case SetupPage.Notifications:
                if (Settings is { } settings) SetupWrites.DecideFor(page, settings, RequestTheme);
                Advance(SetupGating.NextPage(page, SkipSignIn));
                break;

            case SetupPage.Terms:
                QuitApp?.Invoke();
                break;

            case SetupPage.SignIn:
                SecondarySignIn();
                break;

            case SetupPage.LocalPlayback:
                SecondaryLocalPlayback();
                break;
        }
    }

    /// <summary>Pre-auth, "giving up" on Idle/Failed/Expired means quitting — there is no shell to fall back to
    /// without an account, which is exactly why this session carries <see cref="QuitApp"/>. The two exceptions read
    /// off the button's OWN label, which is the whole point:
    /// <list type="bullet">
    /// <item>BUSY says "Cancel" (<c>SetupCommands.SignInRow</c>) — so it cancels the attempt
    /// (<see cref="CancelSignIn"/>) and lands back on Idle with the two option cards. It must never quit: the label
    /// promises to stop the sign-in, not to stop Wavee.</item>
    /// <item>PREMIUM says "Use a different account" — so it re-mints the device code.</item>
    /// <item>DONE says "Not me" on the "Is this you?" confirmation — so it signs this PC out
    /// (<see cref="SwitchAccount"/>) and the page drops back to Idle with a fresh code.</item>
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

    /// <summary>"Not now"/"Cancel"/"Back" per <see cref="PlaybackRuntimeSetupModel.Phase"/> — "Not now" ALSO burns
    /// <see cref="DeclineRuntime"/> so the Done page's "warming local playback" step knows not to wait for a runtime
    /// that was explicitly declined.</summary>
    void SecondaryLocalPlayback()
    {
        if (Runtime is not { } m) return;
        switch (m.PhaseSig.Value)
        {
            case PlaybackRuntimeSetupModel.Phase.Offer:
            case PlaybackRuntimeSetupModel.Phase.Failed:
                m.DismissSetting();
                DeclineRuntime();
                Advance(SetupGating.NextPage(SetupPage.LocalPlayback, SkipSignIn));
                break;
            case PlaybackRuntimeSetupModel.Phase.Advanced: m.Back(); break;
            case PlaybackRuntimeSetupModel.Phase.Untrusted: m.CancelUntrusted(); break;
            case PlaybackRuntimeSetupModel.Phase.FetchingCatalog:
            case PlaybackRuntimeSetupModel.Phase.Downloading: m.Cancel(); break;
            // Verifying: SetupCommands.LocalPlaybackRow's SecondaryKey is null — no button to invoke this.
        }
    }

    /// <summary>The plate's Back affordance (top-left icon button + Backspace) — always a bare previous-page walk.</summary>
    public void Back() => Advance(SetupGating.PrevPage(Page.Peek(), SkipSignIn));
}
