// ── Screens/WizardModel.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the setup wizard's MODEL: the live session (page, swap direction, the Primary / Secondary / Back verbs and the facts the
// footer and pages read), the open decision made at boot, the process markers, the sign-in verbs, the Lottie hero loader
// and the crash-consent verb
//
// Role: CORE (engine-free except two data types: the `Prop` bind and the parsed `LottieSource`; no element, component or
//       overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C14 (X32)
//
// WHAT MOVED. `Setup.UI.cs` used to hold, beside the plate and the pages: the markers (`WizardMarkerEpoch`, `WizardOpen`,
// `Covering`), the open decision (re-derived from the settings inside the chrome's mount effect, then 1-2 posts), the
// whole `WizardSession` state machine, `LoadHero` (a disk read on the UI thread at first use), the sign-in start/cancel
// verbs and the crash-consent write. They are here now; the plate, the footer, the pages and the overlay handle stay in the
// view file, which reads this model and calls its verbs.
//
// THE OPEN IS DECIDED IN BOOT. `Setup.InstallWizard()` (called from `Settings.InstallScreens()`, after the install
// bootstrap armed or suppressed the wizard) runs `Wizard.Boot`: the settings and the stored credential decide whether the
// wizard is due this launch and with which entry (FirstRun / Reauth / TermsRearm), and the model exists before the first
// frame. The chrome view only OPENS it (it holds the overlay service): `ScheduleOpen` keeps the 1-2 posts exactly as they
// were (one for a first run / re-auth, so the plate appears WITH the window; two for a terms re-arm, so it rises over an
// already painted shell).

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Setup
{
    // ══ 1. THE PROCESS MARKERS ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Bumped at the end of EVERY wizard close (complete, decline, Escape). The report chrome and the after-update
    /// chrome subscribe to re-evaluate their wizard deferral in the same launch.</summary>
    public static readonly Signal<int> WizardMarkerEpoch = new(0);

    /// <summary>True while the wizard plate is live (from open until its close has finished).</summary>
    public static readonly Signal<bool> WizardOpen = new(false);

    /// <summary>What the shell paints behind the plate: <see cref="Cover.Dim"/> while it is up. The plate opens with the
    /// engine scrim OFF (a shell is always behind it in 0.3), so the shell's cover scrim — Tok.FillSmoke, 250 ms linear,
    /// 0 under reduced motion — is the only dim.</summary>
    public static readonly Signal<Cover> Covering = new(Cover.None);

    /// <summary>Set when the plate is opened (once per launch); read by the sign-in door, which stands down while the wizard
    /// owns sign-in (<see cref="WizardRules.OwnsSignIn"/>).</summary>
    static bool s_wizardShown;

    // ══ 2. THE BOOT DECISION ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The wizard's boot-time cell: whether it is due this launch, and the model that will run it.</summary>
    public static class Wizard
    {
        /// <summary>The model decided at boot, or null when the wizard is not due this launch. Never consumed: the open is
        /// guarded by the shown flag, so a remounted chrome cannot open a second plate.</summary>
        public static WizardModel? Due { get; private set; }

        /// <summary>PURE: is the wizard due, and which entry? Null when neither the pending marker nor a terms re-arm asks
        /// for it. <paramref name="signedIn"/> is whether a credential is stored (a completed install with none
        /// re-authenticates).</summary>
        public static WizardEntry? Decide(IAppSettings? settings, bool signedIn)
        {
            if (settings is null) return null;
            bool completed = Gating.IsCompleted(settings);
            if (!WizardRules.ShouldOpen(Gating.IsPending(settings), completed, settings.Get(Platform.Keys.TermsAcceptedVersion))) return null;
            return WizardRules.EntryFor(completed, signedIn);
        }

        /// <summary>Decide now and, when due, build the model. Called once per launch from <see cref="InstallWizard"/>.</summary>
        public static WizardModel? Boot(IAppSettings? settings, bool signedIn)
        {
            var entry = Decide(settings, signedIn);
            Due = entry is { } e ? new WizardModel(e, signedIn) : null;
            return Due;
        }

        /// <summary>Forget the boot decision and the shown flag (tests; a harness that boots more than once).</summary>
        public static void ResetForTests()
        {
            Due = null;
            s_wizardShown = false;
        }
    }

    /// <summary>Called once by <c>Settings.InstallScreens()</c>. The wizard needs no seam filled; its open is decided here, and
    /// when it is due this launch the Lottie heroes are loaded, parsed and compiled off the UI thread before the first page
    /// mounts one.</summary>
    public static void InstallWizard()
    {
        bool due = Wizard.Boot(Platform.Settings, Platform.HasStoredCredential()) is not null;
        if (due) WizardHeroes.Warm();
        Log.Info("setup", due ? "setup wizard installed — due this launch" : "setup wizard installed — not due");
    }

    // ══ 3. THE LIVE SESSION ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The wizard's live model: the page and the swap direction as signals (Direction is written BEFORE Page and only
    /// Peeked by the keep-alive, so a motion-only write never re-activates a page), and the Primary / Secondary / Back verbs
    /// every footer row and key maps to (ch 28 §6.1's two tables). It lives outside the tree; the pages and the footer read
    /// its signals.</summary>
    public sealed class WizardModel
    {
        public readonly Signal<WizardPage> Page;
        public readonly Signal<Design.NavTransitionKind> Direction = new(Design.NavTransitionKind.Neutral);
        public readonly WizardEntry Entry;
        public readonly bool SkipSignIn;
        public readonly bool SkipLayout;
        public Action? RequestClose;

        /// <summary>The pairing code the Sign in page shows ("" while none is live), as a bound value: reading it inside a bind
        /// thunk subscribes to the sign-in state, so a view can bind the code without re-rendering its page.</summary>
        public Prop<string> PairingCode { get; }

        Action<Action>? _post;
        Action<string>? _openUri;

        /// <summary>The runtime provisioning model, begun when the wizard first reaches Local playback; null while no
        /// host is installed (D3), which is the page's model-null arm.</summary>
        public RuntimeModel? Model { get; private set; }

        public WizardModel(WizardEntry entry, bool signedIn, Action<Action>? post = null)
        {
            Entry = entry;
            SkipSignIn = Gating.SkipSignIn(signedIn);
            SkipLayout = !Gating.ShowsLayout(entry);
            _post = post;
            Page = new(WizardRules.StartPage(entry));
            PairingCode = Prop.Of(PairingCodeNow);
        }

        /// <summary>Hand the model the UI poster (background work marshals through it) and the external-link opener. Called by
        /// the chrome before the open is scheduled; a model that was never bound posts inline and opens nothing.</summary>
        public void Bind(Action<Action> post, Action<string>? openUri)
        {
            _post = post;
            _openUri = openUri;
        }

        // ── the open ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>How many posts the open waits: one for a first run / re-auth, two for a terms re-arm.</summary>
        public int OpenPosts => WizardRules.OpenPosts(Entry);

        /// <summary>Run <paramref name="open"/> after <see cref="OpenPosts"/> posts, unless a plate was opened meanwhile.</summary>
        public void ScheduleOpen(Action open)
        {
            var post = _post ?? Inline;
            void Guarded() { if (!s_wizardShown) open(); }
            if (OpenPosts == 2) post(() => post(Guarded));
            else post(Guarded);
        }

        static void Inline(Action action) => action();

        /// <summary>The plate is about to open: the launch's one open is spent.</summary>
        public void BeginOpen()
        {
            s_wizardShown = true;
            Log.Info("setup", "setup wizard opened (" + Entry + ", on " + Page.Peek() + ")");
        }

        /// <summary>The plate is up: the shell dims behind it and the wizard owns sign-in.</summary>
        public void Opened()
        {
            Covering.Value = Layout.CoverFor(shellBehind: true);
            WizardOpen.Value = true;
        }

        /// <summary>The plate has closed (complete, decline, Escape): end the sessions, lift the dim, bump the marker epoch.</summary>
        public void Closed()
        {
            End();
            Covering.Value = Cover.None;
            Runtime.WizardCovering.SetIfChanged(false);
            WizardOpen.Value = false;
            WizardMarkerEpoch.Value = WizardMarkerEpoch.Peek() + 1;
            Log.Info("setup", "setup wizard closed (pending " + Gating.IsPending(Platform.Settings) + ")");
        }

        // ── facts the footer and the pages read ─────────────────────────────────────────────────────────────────────

        public bool IsBusy => Model?.IsBusy ?? false;

        /// <summary>"Step N of M" (M is 2 when Layout is skipped), or null on Terms ("Pre-setup"). Reads <see cref="Page"/>.</summary>
        public (int Step, int Total)? StepNumber() => Gating.StepNumber(Page.Value, SkipLayout);

        /// <summary>The number of steps this session shows.</summary>
        public int Count => SkipLayout ? 2 : Gating.StepTotal;

        /// <summary>The footer's progress over the pages this session shows. Reads <see cref="Page"/>.</summary>
        public float Progress() => Gating.Progress(Page.Value, SkipLayout);

        /// <summary>The sign-in facet over the live sign-in state, the session phase and the fault. Subscribing when rendered.</summary>
        public SignInFacet SignInFacetNow()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            return SignInRules.Project(in st, Spotify.Status.Value, Spotify.Fault.Value);
        }

        /// <summary>The Local playback phase, or null while there is no provisioning model. Subscribing when rendered.</summary>
        public RuntimePhase? RuntimePhaseNow() => Model is { } m ? m.Phase.Value : null;

        static string PairingCodeNow()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            return st.HasChallenge ? st.UserCode : "";
        }

        /// <summary>The footer row. Reads the page, the sign-in facet and the runtime phase — subscribing when rendered.</summary>
        public CommandRow Row()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            var ctx = new WizardCtx(Page.Value, SignInRules.Project(in st, Spotify.Status.Value, Spotify.Fault.Value),
                Model is { } m ? Commands.FacetFor(m.Phase.Value) : RuntimeFacet.Offer);
            return Commands.ResolveFor(in ctx, Model is not null);
        }

        // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────

        public void Advance(WizardPage to)
        {
            if (to == WizardPage.LocalPlayback && Model is null && Runtime.Host is not null)
                Model = Runtime.Begin(_post ?? Inline, onClose: Finish, onWizardExit: () => { Gating.MarkDeferred(Platform.Settings); RequestClose?.Invoke(); });
            Direction.Value = WizardRules.DirectionFor(Page.Peek(), to);
            Page.Value = to;
        }

        public void Back() => Advance(Gating.PrevPage(Page.Peek(), SkipSignIn, SkipLayout));

        /// <summary>Ends the runtime session and the sign-in flows (the plate's close).</summary>
        public void End()
        {
            if (Model is { } m) Runtime.End(m);
            Model = null;
            Spotify.SignIn.Reset();
        }

        void Finish()
        {
            Gating.MarkCompleted(Platform.Settings);
            RequestClose?.Invoke();
        }

        static void QuitApp()
        {
            Log.Info("setup", "setup wizard: quit from a page with nothing behind it");
            Tray.Host.Quit();
        }

        public void Primary()
        {
            switch (Page.Peek())
            {
                case WizardPage.Terms:
                    // A consent leaves a durable record BEFORE the advance, so a crash in between cannot lose it.
                    Platform.Settings.Set(Platform.Keys.TermsAcceptedVersion, Gating.TermsVersion);
                    WizardRules.MarkConsentAsked(Platform.Settings, Entry);   // crash-diagnostics §F: consent asked iff the card was shown
                    if (Entry == WizardEntry.TermsRearm) Finish();
                    else Advance(Gating.NextPage(WizardPage.Terms, SkipSignIn, SkipLayout));
                    break;
                case WizardPage.Layout:   // the two sidebar layouts apply at once; Next only moves on
                    Advance(Gating.NextPage(WizardPage.Layout, SkipSignIn, SkipLayout));
                    break;
                case WizardPage.SignIn:
                    Spotify.SignInState st = Spotify.SignIn.State.Peek();
                    switch (SignInRules.Project(in st, Spotify.Status.Peek(), Spotify.Fault.Peek()))
                    {
                        case SignInFacet.Idle: StartBrowser(); break;
                        case SignInFacet.Done:   // "Is this you?" — moving on is the user's own click, never automatic
                            if (Gating.SkipsLocalPlayback(Entry, Runtime.Status.Peek().IsReady)) Finish();
                            else Advance(Gating.NextPage(WizardPage.SignIn, SkipSignIn, SkipLayout));
                            break;
                        case SignInFacet.Failed: RetryFor(in st)(); break;
                        case SignInFacet.Expired: StartPairing(); break;
                        case SignInFacet.Premium: _openUri?.Invoke(SpotifyPremiumUrl); break;
                    }
                    break;
                default:
                    if (Model is not { } m) return;   // no host: the row offers the primary disabled
                    switch (m.Phase.Peek())
                    {
                        case RuntimePhase.Offer: m.StartDownload(); break;
                        case RuntimePhase.Advanced: m.InstallSelected(); break;
                        case RuntimePhase.Untrusted: m.ConfirmUntrusted(); break;
                        case RuntimePhase.Ready: m.Close(); break;   // OnClose = Finish: the last page has no Done page after it
                        case RuntimePhase.Failed: m.Retry(); break;
                    }
                    break;
            }
        }

        public void Secondary()
        {
            switch (Page.Peek())
            {
                case WizardPage.Terms:
                    // Pre-auth there is nothing behind a first run's plate: Decline quits and the marker stays armed. A re-arm
                    // has a live shell behind it and just closes.
                    if (Entry == WizardEntry.TermsRearm) RequestClose?.Invoke();
                    else QuitApp();
                    break;
                case WizardPage.SignIn:
                    Spotify.SignInState st = Spotify.SignIn.State.Peek();
                    switch (SignInRules.Project(in st, Spotify.Status.Peek(), Spotify.Fault.Peek()))
                    {
                        case SignInFacet.Busy:   // "Cancel" stops the sign-in, never Wavee
                            if (st.Handed) { Spotify.SignIn.Reset(); Spotify.Logout(); }
                            else CancelBrowser();
                            break;
                        case SignInFacet.Premium: Spotify.SignIn.Reset(); StartPairing(); break;   // "Use a different account"
                        case SignInFacet.Done: SwitchAccount(); break;                           // "Not me"
                        default: QuitApp(); break;                                               // Idle / Failed / Expired "Close"
                    }
                    break;
                default:
                    if (Model is not { } m || m.Phase.Peek() is RuntimePhase.Offer or RuntimePhase.Failed)
                    {
                        // "Not now": a settled "no" — the banner stays quiet, and the wizard finishes outright.
                        Runtime.Dismiss("setup");
                        Log.Info("setup", "setup wizard: local playback declined");
                        Finish();
                        return;
                    }
                    switch (m.Phase.Peek())
                    {
                        case RuntimePhase.Advanced: m.Back(); break;                 // a MODEL step, not a page walk
                        case RuntimePhase.Untrusted: m.CancelUntrusted(); break;
                        case RuntimePhase.FetchingCatalog or RuntimePhase.Downloading: m.Cancel(); break;
                    }
                    break;
            }
        }

        // ── the crash-reporting consent (the Terms page's opt-in card) ──────────────────────────────────────────────

        /// <summary>The radio index the consent card opens on: the stored <c>Crash.Reporting</c> mode, EFFECTIVE for a build
        /// that cannot send (a stored Auto reads Ask there). The offered list is a prefix of Off/Ask/Auto, so the index stays
        /// the stored value.</summary>
        public static int CrashReportingChoice(bool canSend)
            => (int)Crash.ConsentPolicy.Effective((Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting), canSend);

        /// <summary>The consent choice writes <see cref="Platform.Keys.CrashReporting"/> the instant it changes — never waits
        /// for Continue, so a crash between the click and the wizard's own advance cannot lose it. Consent-asked itself is
        /// stamped once, on Continue (<see cref="Primary"/>).</summary>
        public static void SetCrashReporting(int mode) => Platform.Settings.Set(Platform.Keys.CrashReporting, mode);
    }

    // ══ 4. THE SIGN-IN VERBS (the door and the wizard's Sign in page share them) ═════════════════════════════════════

    static void StartBrowser() => Spotify.SignIn.Start(Spotify.SignInMethod.Browser);
    static void StartPairing() => Spotify.SignIn.Start(Spotify.SignInMethod.DeviceCode);
    static void CancelBrowser() => Spotify.SignIn.Cancel(Spotify.SignInMethod.Browser);

    /// <summary>"Try again" retries the flow that failed: the pairing code when only it failed, the browser otherwise
    /// (a failed login after a hand-off included — the browser is the primary way in).</summary>
    static Action RetryFor(in Spotify.SignInState st)
    {
        bool codeFailed = st.Code is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied;
        bool browserFailed = st.Browser is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied or Spotify.SignInStage.Expired;
        return codeFailed && !browserFailed ? StartPairing : StartBrowser;
    }

    static void SwitchAccount()
    {
        Log.Info("setup", "setup wizard: not me — signing this PC out");
        Spotify.SignIn.Reset();
        Spotify.Logout();
    }

    // ══ 5. THE LOTTIE HEROES ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The four page heroes, loaded, parsed and compiled OFF the UI thread. <see cref="Warm"/> starts the one load task
    /// (boot, when the wizard is due); <see cref="Hero"/> answers the loaded source or null and never touches the disk — the
    /// page that asked re-renders once when the load lands (the chrome binds the UI poster for that one bump).</summary>
    public static class WizardHeroes
    {
        static readonly FluentGpu.Lottie.LottieSource?[] s_sources = new FluentGpu.Lottie.LottieSource?[4];
        static readonly Signal<int> s_epoch = new(0);
        static Action<Action>? s_post;
        static int s_started;
        static int s_done;

        /// <summary>Start loading (once per process). Safe to call from anywhere, any number of times.</summary>
        public static void Warm()
        {
            if (Interlocked.Exchange(ref s_started, 1) != 0) return;
            _ = Task.Run(static () =>
            {
                for (int i = 0; i < s_sources.Length; i++)
                {
                    var source = Load((WizardPage)i);
                    try { if (source is not null) _ = source.Plan; }
                    catch (Exception ex) { Log.Warn("setup", "wizard hero warm-up failed", ex); }
                    Volatile.Write(ref s_sources[i], source);
                    Interlocked.Increment(ref s_done);
                    Notify();
                }
            });
        }

        /// <summary>Hand the loader the UI poster. A load that finished before this call is announced now.</summary>
        public static void Bind(Action<Action> post)
        {
            Volatile.Write(ref s_post, post);
            if (Volatile.Read(ref s_done) > 0) post(Bump);
        }

        /// <summary>The page's hero, or null while it is still loading (or unavailable). Subscribes to the loader's epoch, so
        /// the asking component re-renders when a load lands; starts the load if nothing did.</summary>
        public static FluentGpu.Lottie.LottieSource? Hero(WizardPage page)
        {
            _ = s_epoch.Value;
            Warm();
            return Volatile.Read(ref s_sources[(int)page]);
        }

        static void Notify()
        {
            var post = Volatile.Read(ref s_post);
            post?.Invoke(Bump);
        }

        static void Bump() => s_epoch.Value = s_epoch.Peek() + 1;

        /// <summary>The asset's full path (<c>assets/lottie/&lt;scene&gt;.json</c> beside the executable).</summary>
        public static string PathFor(WizardPage page)
            => Path.Combine(AppContext.BaseDirectory, "assets", "lottie", Layout.HeroAsset(page) + ".json");

        static FluentGpu.Lottie.LottieSource? Load(WizardPage page)
        {
            try { return FluentGpu.Lottie.LottieSource.FromFile(PathFor(page)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("setup", "wizard hero '" + Layout.HeroAsset(page) + "' is unavailable — the icon column stays empty", ex);
                return null;
            }
        }
    }
}
