// ── Screens/Setup.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────────
// the setup screens' core hosts: the sign-in door's watcher and dialog, the wizard's chrome, open funnel and plate host
//
// Role: CORE hosts (the pictures are Classic's: `Themes\Classic\Structure\Views\Screens\ClassicSetup*.cs`, themes plan T11c)
// Owner: R
// Wave: 6
// Spec: ch 28 §9.5
//
// TWO SURFACES, ONE SET OF SIGN-IN PARTS. (1) The SETUP WIZARD (WP-6.R: G-192, G-095's wizard half, G-030's wizard
// chrome): the hand-rolled Modal plate `Setup.WizardChrome()` opens on a pending (or terms-re-armed) install, drawn by the
// `Setup.Wizard` view over `WizardModel`. (2) The SIGN-IN DOOR (gap batch B5, D2): a 460 ContentDialog for every sign-in the
// wizard does not own, whose body and footer are the `Setup.SignInDoor` view. Both compose the same browser card, pairing
// card and QR grid; while the wizard is armed-and-unshown or open it OWNS sign-in and the door stands down
// (`WizardRules.OwnsSignIn`).
//
//   Shell.OverlaysLayer (overlay host)    mount order: Setup.WizardChrome() → Feedback.Chrome() → ReleaseNotes.AfterUpdateChrome()
//   ├ Setup.WizardChrome()   0×0, once per launch → overlay.Open(Modal, FocusTrap, ScrimVisual = false) → WizardPlate (host) → Setup.Wizard
//   └ Setup.SignInDoor()     0×0 → ContentDialog 460 "Sign in to Spotify" → Setup.SignInDoor (Body) + Setup.SignInDoor (Footer)
//
// THE SHELL'S HALF: `Setup.Covering` (Dim while the plate is up) is painted by the shell's cover scrim (owner I's chrome, not
// this file); `Setup.Runtime.WizardCovering` is written here while the Local playback page is up. Every decision is CORE
// (`Setup.Gating`, `Setup.SignInRules`, the wizard rules parked in `Setup.cs`); the hosts open, close and hand the views their props.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    // ══ REGION — THE SIGN-IN SURFACE (gap batch B5, D2) ═════════════════════════════════════════════════════════════

    /// <summary>The sign-in door: a zero-size watcher that opens the sign-in surface when something asks for it
    /// (<see cref="Spotify.SignIn.Requests"/> — every "Sign in" with nothing to resume) or when the shell enters
    /// <c>SignInRequired</c> with no credential stored, and closes it once the session is online. Stands down while the
    /// setup wizard owns sign-in. Must be mounted INSIDE the overlay host, beside the runtime banner.</summary>
    // MOUNT POINT (gap batch B5 — Shell.OverlaysLayer)
    public static Element SignInDoor()
        => Platform.Args.Fake ? new BoxEl() : Embed.Comp(static () => new SignInDoorView());   // --fake never signs in: its scope is seeded, not an account

    /// <summary>The open surface, or null. UI THREAD.</summary>
    static readonly RootState<OverlayHandle?> s_signInState = new("Setup.SignInDoor");   // ROOT-LIFETIME (RootScope.cs)
    static OverlayHandle? s_signIn { get => s_signInState.Value; set => s_signInState.Value = value; }

    const string SpotifySignUpUrl = "https://www.spotify.com/signup";
    const string SpotifyPremiumUrl = "https://www.spotify.com/premium/";
    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

    sealed class SignInDoorView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int request = Spotify.SignIn.Requests.Value;
            Shell.AuthState auth = Shell.Auth.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            var lastRequest = UseRef(request);
            var lastAuth = UseRef<Shell.AuthState?>(null);

            UseEffect(() =>
            {
                bool requested = request != lastRequest.Value;
                lastRequest.Value = request;
                bool entered = auth == Shell.AuthState.SignInRequired && lastAuth.Value != Shell.AuthState.SignInRequired;
                lastAuth.Value = auth;
                // WP-6.R: never a second sign-in surface beside the wizard's own Sign in page.
                if (!WizardRules.OwnsSignIn(Gating.IsPending(Platform.Settings), WizardOpen.Peek(), s_wizardShown)
                    && SignInRules.OpensDoor(requested, entered, Platform.HasStoredCredential(), s_signIn is { IsOpen: true }))
                    OpenSignIn(overlay, requested ? "request" : "gate");
            }, DepKey.From(request, (int)auth));

            UseEffect(() =>
            {
                if (SignInRules.ClosesDoor(phase) && s_signIn is { IsOpen: true } open) open.Close();
            }, DepKey.From((int)phase));

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    static readonly SetupSignInDoorProps s_doorBody = new() { Part = SetupDialogPart.Body, Host = SignInHost.Instance };
    static readonly SetupSignInDoorProps s_doorFooter = new() { Part = SetupDialogPart.Footer, Host = SignInHost.Instance };

    static void OpenSignIn(IOverlayService overlay, string door)
    {
        Log.Event(WaveeLogLevel.Info, "ui", "signin.open", "Sign-in surface opened", null, -1, null, WaveeLogField.Of("door", door));
        s_signIn = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Setup.SignIn.Title);
            d.DialogWidth = SignInRules.DialogWidth;
            d.PrimaryText = "";                  // PrimaryText defaults to "OK": clear all three or a phantom OK appears
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Slots.View(ViewKeys.SetupSignInDoor, s_doorBody);
            d.Footer = Slots.View(ViewKeys.SetupSignInDoor, s_doorFooter);
            d.Closed = static _ =>
            {
                s_signIn = null;
                Spotify.SignIn.Reset();
                Log.Event(WaveeLogLevel.Info, "ui", "signin.close", "Sign-in surface closed", null, -1, null,
                    WaveeLogField.Of("phase", Spotify.Status.Peek().ToString()));
            };
        });
    }

    static void CloseSignIn() => s_signIn?.Close();
    static void OpenExternal(string url) => InputHooks.Current.Default.OpenUri?.Invoke(url);
    // StartBrowser / StartPairing / CancelBrowser / RetryFor / SwitchAccount: the sign-in verbs, in WizardModel.cs (C14); the views reach them through `SignInHost` (Setup.SignInHost.cs).

    // ══ END REGION (gap batch B5) ═══════════════════════════════════════════════════════════════════════════════════

    // ══ REGION — THE SETUP WIZARD (WP-6.R: G-192, G-095's wizard half, G-030's wizard chrome) ═══════════════════════

    // The markers (`WizardMarkerEpoch`, `WizardOpen`, `Covering`), the boot decision (`Wizard.Boot` from `InstallWizard`),
    // the live session (`WizardModel`: page, direction, Primary / Secondary / Back), the sign-in verbs, the Lottie hero
    // loader and the crash-consent verb are CORE, in `WizardModel.cs` (C14). This file keeps the overlay handle and the plate host;
    // the plate, the footer and the pages are `Setup.Wizard` and its page keys.

    static readonly RootState<OverlayHandle?> s_wizardState = new("Setup.WizardPlate");   // ROOT-LIFETIME (RootScope.cs); the once-per-launch latch and the session are WizardModel's (process)
    static OverlayHandle? s_wizard { get => s_wizardState.Value; set => s_wizardState.Value = value; }

    /// <summary>Zero-size chrome, mounted in the overlay layer FIRST (before the report and after-update chromes). Once per
    /// launch it opens the plate when the boot decision (<see cref="Wizard.Due"/>) says the wizard is due: <c>setup.pending</c>
    /// armed or a terms re-arm.</summary>
    // MOUNT POINT (owner R contract — Shell.OverlaysLayer, first of the three chromes)
    public static Element WizardChrome() => Embed.Comp(static () => new WizardChromeView());

    sealed class WizardChromeView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            UseEffect(() =>
            {
                if (s_wizardShown || Wizard.Due is not { } model) return;
                model.Bind(post, OpenExternal);
                WizardHeroes.Bind(post);
                WizardHeroes.Warm();
                model.ScheduleOpen(() => OpenWizard(overlay, model));
            }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    /// <summary>THE open and THE close funnel. A programmatic close (the session's own "Open Wavee", "Decline" on a re-arm,
    /// a finish) always goes through; a user dismissal only on an idle terms re-arm. Every close lands in ClosedAction.</summary>
    static void OpenWizard(IOverlayService overlay, WizardModel session)
    {
        session.BeginOpen();
        OverlayHandle handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new WizardPlate(session)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal) { ScrimVisual = false });
        handle.ClosingAction = cause =>
            cause == OverlayCloseCause.Programmatic || WizardRules.EscapeClosesPlate(false, session.Entry, session.IsBusy);
        handle.ClosedAction = () =>
        {
            if (ReferenceEquals(s_wizard, handle)) s_wizard = null;
            session.Closed();
        };
        session.RequestClose = handle.Close;
        s_wizard = handle;
        session.Opened();
    }

    // ── the plate's host ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plate: the overlay's content. It keeps the one effect that is not a picture (the runtime banner stands down while the Local playback page is up) and mounts the
    /// <c>Setup.Wizard</c> view over the session.</summary>
    [CoreHost]
    sealed class WizardPlate(WizardModel session) : Component
    {
        SetupWizardProps? _props;

        public override Element Render()
        {
            WizardPage page = session.Page.Value;
            UseEffect(() => { Runtime.WizardCovering.SetIfChanged(page == WizardPage.LocalPlayback); }, DepKey.From((int)page));
            return Slots.View(ViewKeys.SetupWizard, _props ??= PropsFor(session));
        }
    }

    static SetupWizardProps PropsFor(WizardModel session)
    {
        bool canSend = Crash.Uploader.Configured;
        return new SetupWizardProps
        {
            Session = session,
            Terms = new SetupTermsProps
            {
                Session = session,
                OpenPrivacy = static () => OpenExternal(PrivacyUrl),
                Consent = new SetupConsent(Crash.ConsentPolicy.Offered(canSend).Count, () => WizardModel.CrashReportingChoice(canSend), WizardModel.SetCrashReporting),
            },
            SignIn = new SetupSignInPageProps { Session = session, Host = SignInHost.Instance },
            Layout = new SetupLayoutPageProps { Cards = static () => Settings.SidebarLayoutCards(Sidebar.Layout.Value, editing: false) },
            LocalPlayback = new SetupLocalPlaybackProps { Session = session, OpenSignature = OpenSignatureVerb, RevealFolder = RevealFolder },
        };
    }

    /// <summary>The nested digital-signature sheet (the shell's overlay file): the runtime views' verb. A method, not a field: the props initializers of the partial files may run in any order.</summary>
    static void OpenSignatureVerb(IOverlayService overlay, RuntimeFacts facts) => Shell.OpenSignatureDialog(overlay, facts);

    static void RevealFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("setup", "open the runtime folder failed", ex); }
    }

    // ══ END REGION (WP-6.R) ═════════════════════════════════════════════════════════════════════════════════════════
}
