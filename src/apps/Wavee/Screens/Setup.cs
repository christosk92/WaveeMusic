// ── Screens/Setup.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// SetupGating, SetupCommands, SetupEntryPoint, SetupLayout, both presentations, SetupBootstrap, QrPlate, Qr,
// WaveeLottieRecolor, SetupSession/SetupSession.MarkerEpoch (the process-static marker epoch the app root subscribes
// to — §9.6 Q8, 2026-09-12: orchestrator's call, no chapter names a home)
//
// Role: CORE
// Owner: R
// Wave: 6
// Budget: 1150 lines
// Spec: ch 28 §9.5 + §9.6 Q8 (DERIVED: 950 + 200 for SetupSession, 324 lines of 0.2.9 ported and folded)
//
// Still the Wave-6 skeleton for owner R, EXCEPT two regions: the runtime provisioning card's pure phase rules,
// written by owner I in Wave 4 (A18) because `+Screens/Setup.UI.Runtime.cs` renders the card for the Wave-4 shell
// banner (the phase ENUM itself is `Platform.cs`'s `RuntimePhase` — one enum for the whole app, ch 28 §8 — not here);
// and the sign-in / gating / bootstrap region, pulled forward by decision D2 (gap batch B5: G-030, G-095) so a fresh
// profile can sign in before Wave 6. The wizard CHROME (the Terms / Sign in / Local playback pages, SetupCommands,
// SetupLayout, SetupSession) is still owner R's.

using System.Globalization;

namespace Wavee;

public static partial class Setup
{
    // ══ REGION — THE RUNTIME PROVISIONING CARD'S PURE RULES (owner I, Wave 4, A18) ══════════════════════════════════
    //
    // Everything the card DECIDES — what the banner says, whether it shows, which body arm and which footer a phase
    // gets, where each Cancel lands, the byte/hash/signature wording — as values, so the card body is layout only and
    // the owner-R wizard reads the same answers. The FACTS these rules fold are plain UI-facing records (the fields the
    // 0.2.9 card displayed); the host that fills them is the SHELL half and names no runtime internals here.

    /// <summary>The runtime catalog's fetch state (the Advanced view's arm).</summary>
    public enum RuntimeCatalog : byte { NotFetched, Fetching, Loaded, Failed }

    /// <summary>What the floating banner has to say, if anything. <see cref="None"/> = no banner (ready, not
    /// applicable to this build, or nothing known yet — the banner must never flash).</summary>
    public enum RuntimeIssue : byte { None, Missing, WrongArch, NoPack, Unsupported }

    /// <summary>A runtime file's signature trust verdict.</summary>
    public enum RuntimeTrust : byte { Unknown, Trusted, Untrusted, UnsupportedPlatform }

    /// <summary>The digital-signature facts the nested 548 sheet lists.</summary>
    public sealed record RuntimeSignature(
        string? Subject, string? Issuer, RuntimeTrust Trust, string? Reason,
        DateTimeOffset ValidFrom, DateTimeOffset ValidTo, string? Thumbprint, string? FilePath);

    /// <summary>The runtime status the banner gate and the Ready fact box read. Published whole by the host.</summary>
    public sealed record RuntimeFacts(
        bool IsReady,
        RuntimeIssue Issue,
        string? PackId = null,
        string? Version = null,
        string? Arch = null,
        string? Location = null,
        RuntimeSignature? Signature = null,
        RuntimeTrust Trust = RuntimeTrust.Unknown,
        bool PinnedFingerprint = false)
    {
        /// <summary>Nothing to set up and nothing to say: the state before a host publishes, and a build with no local
        /// runtime at all.</summary>
        public static readonly RuntimeFacts NotApplicable = new(false, RuntimeIssue.None);
    }

    /// <summary>One installable catalog entry, as the version picker and the verify fact box show it.</summary>
    public sealed record RuntimePack(string PackId, string Version, string Arch, string Sha256, long SizeBytes);

    /// <summary>Which Cancel was pressed — three buttons all labelled Cancel, three different screens after it.</summary>
    public enum RuntimeCancel : byte { Download, InstallSelected, Untrusted }

    /// <summary>The Advanced view's middle arm (ch 19 W31/W32).</summary>
    public enum RuntimeAdvancedArm : byte { Nothing, Busy, Picker, NoPack, Unreachable }

    /// <summary>A footer command. <see cref="None"/> = the slot is empty.</summary>
    public enum RuntimeVerb : byte
    {
        None, Advanced, ViewDiagnostics, NotNow, DownloadSetup, Cancel, CancelUntrusted, LoadAnyway,
        CheckUpdate, Done, TryAgain, Back, Install, Dismiss,
    }

    /// <summary>The ONE command row a phase gets: up to two left links, a standard button, an accent button.</summary>
    public readonly record struct RuntimeFooter(
        RuntimeVerb Link, RuntimeVerb Link2, RuntimeVerb Standard, RuntimeVerb Accent,
        bool StandardEnabled = true, bool AccentEnabled = true);

    /// <summary>The signature line's eight wordings, in ladder order.</summary>
    public enum RuntimeSignatureLine : byte
    {
        SignedTrusted, SignedPinned, SignedOther, PinnedFingerprint, VerifiedFingerprint, NotTrustedOverride,
        CheckUnavailable, Unknown,
    }

    public static class RuntimeRules
    {
        /// <summary>The setup dialog is rung 2 of the modal width ladder (ch 19 §3.1): one column of prose plus one
        /// full-width progress row.</summary>
        public const float DialogWidth = 460f;

        /// <summary>ContentDialog's padding, both sides.</summary>
        public const float DialogPadding = 24f;

        /// <summary>The bar spans the copy: the rung less the padding — DERIVED, never typed (460 → 412).</summary>
        public const float ProgressWidth = DialogWidth - 2f * DialogPadding;

        /// <summary>The nested signature sheet is rung 4, the engine maximum.</summary>
        public const float SignatureDialogWidth = 548f;

        /// <summary>The fact boxes' label lane.</summary>
        public const float DetailLabelWidth = 92f;

        /// <summary>The banner's inner cap, and its lane's top pad (the 48-DIP chrome row + 8).</summary>
        public const float BannerMaxWidth = 560f, BannerTopPad = 56f;

        /// <summary>The download bar's fill: clamped 0..1, 0 when the total is unknown.</summary>
        public static float ProgressFraction(long received, long total)
            => total <= 0 ? 0f : Math.Clamp((float)((double)received / total), 0f, 1f);

        /// <summary>A SHA-256 as the fact box shows it: first 4 + "…" + last 4; untouched at ≤ 8 characters.</summary>
        public static string ShortHash(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            return hash.Length > 8 ? string.Concat(hash.AsSpan(0, 4), "…", hash.AsSpan(hash.Length - 4, 4)) : hash;
        }

        /// <summary>"12.3 / 84.0 MB", or "12.3 MB" while the total is unknown (invariant, one decimal, 10^6 bytes).</summary>
        public static string DownloadBytes(long received, long total) => total > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} MB", received / 1_000_000.0, total / 1_000_000.0)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", received / 1_000_000.0);

        /// <summary>The Verifying row's size: "84.0 MB", or "—" with no total.</summary>
        public static string VerifySize(long total) => total > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", total / 1_000_000.0)
            : "—";

        /// <summary>A fact the card does not know prints an em dash, never an empty cell.</summary>
        public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

        /// <summary>Dismissal (Escape, programmatic) is blocked while the card is working.</summary>
        public static bool IsBusy(RuntimePhase phase)
            => phase is RuntimePhase.FetchingCatalog or RuntimePhase.Downloading or RuntimePhase.Verifying;

        /// <summary>A card opened on a machine that already has the runtime opens straight at Ready (W24).</summary>
        public static RuntimePhase InitialPhase(bool isReady) => isReady ? RuntimePhase.Ready : RuntimePhase.Offer;

        /// <summary>Advanced ▸ Back returns to whichever of Ready/Offer the status says.</summary>
        public static RuntimePhase BackTarget(bool isReady) => isReady ? RuntimePhase.Ready : RuntimePhase.Offer;

        /// <summary>W32b: the Offer flow's Cancel → Offer; Install's Cancel → Advanced; the Untrusted Cancel → Offer.</summary>
        public static RuntimePhase CancelTarget(RuntimeCancel which)
            => which == RuntimeCancel.InstallSelected ? RuntimePhase.Advanced : RuntimePhase.Offer;

        /// <summary>The "Local playback is ready" toast is skipped when the card is wizard-hosted — the wizard page
        /// already shows Ready in place.</summary>
        public static bool ShowsReadyToast(bool wizardHosted) => !wizardHosted;

        /// <summary>Windows refuses to overwrite the runtime this very process has loaded: a check/install whose
        /// candidate IS the active pack is "already up to date", never a download.</summary>
        public static bool AlreadyCurrent(bool isReady, string? activePackId, string candidatePackId)
            => isReady && string.Equals(activePackId, candidatePackId, StringComparison.Ordinal);

        /// <summary>Entering Advanced fetches the catalog — idempotent, and it RE-TRIES after a failure but not after a
        /// success; with no host there is nothing to ask.</summary>
        public static bool ShouldFetchCatalog(RuntimeCatalog state, bool hasHost)
            => hasHost && (state is RuntimeCatalog.NotFetched or RuntimeCatalog.Failed);

        /// <summary>The Advanced view's middle arm. NotFetched has NO arm (no busy row, no copy) — ported, not invented.</summary>
        public static RuntimeAdvancedArm AdvancedArm(RuntimeCatalog state, int packCount) => state switch
        {
            RuntimeCatalog.Fetching => RuntimeAdvancedArm.Busy,
            RuntimeCatalog.Loaded => packCount > 0 ? RuntimeAdvancedArm.Picker : RuntimeAdvancedArm.NoPack,
            RuntimeCatalog.Failed => RuntimeAdvancedArm.Unreachable,
            _ => RuntimeAdvancedArm.Nothing,
        };

        /// <summary>Install is enabled only with a loaded catalog that offers something.</summary>
        public static bool CanInstall(RuntimeCatalog state, int packCount) => state == RuntimeCatalog.Loaded && packCount > 0;

        /// <summary>The footer table (ch 19 W24–W32): exactly ONE command row, whose buttons change with the phase.
        /// Verifying keeps a DISABLED Cancel; Failed offers "View diagnostics" only where there is somewhere to go.</summary>
        public static RuntimeFooter FooterFor(RuntimePhase phase, RuntimeCatalog catalog, int packCount, bool canNavigate)
            => phase switch
            {
                RuntimePhase.Offer => new(RuntimeVerb.Advanced, RuntimeVerb.None, RuntimeVerb.NotNow, RuntimeVerb.DownloadSetup),
                RuntimePhase.FetchingCatalog or RuntimePhase.Downloading
                    => new(RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.Cancel, RuntimeVerb.None),
                RuntimePhase.Verifying
                    => new(RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.Cancel, RuntimeVerb.None, StandardEnabled: false),
                RuntimePhase.Untrusted => new(RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.CancelUntrusted, RuntimeVerb.LoadAnyway),
                RuntimePhase.Ready => new(RuntimeVerb.CheckUpdate, RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.Done),
                RuntimePhase.Failed => new(RuntimeVerb.Advanced, canNavigate ? RuntimeVerb.ViewDiagnostics : RuntimeVerb.None,
                    RuntimeVerb.NotNow, RuntimeVerb.TryAgain),
                RuntimePhase.Advanced => new(RuntimeVerb.Back, RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.Install,
                    AccentEnabled: CanInstall(catalog, packCount)),
                _ => new(RuntimeVerb.None, RuntimeVerb.None, RuntimeVerb.Dismiss, RuntimeVerb.None),
            };

        /// <summary>The banner gate: something to say, not dismissed, and neither the first-run wizard's own
        /// Local-playback page (covering) nor a still-pending wizard is asking the same question already.</summary>
        public static bool ShowsBanner(RuntimeIssue issue, bool dismissed, bool wizardCovering, bool setupPending)
            => issue != RuntimeIssue.None && !dismissed && !wizardCovering && !setupPending;

        /// <summary>The banner's message key per issue (ch 19 W22/W23): only "noPack" (62 characters) crosses the
        /// InfoBar's 60-character vertical rule, because the banner passes no available width.</summary>
        public static string BannerLocKey(RuntimeIssue issue) => issue switch
        {
            RuntimeIssue.WrongArch => Strings.Playback.Runtime.WrongArch,
            RuntimeIssue.NoPack => Strings.Playback.Runtime.NoPack,
            RuntimeIssue.Unsupported => Strings.Playback.Runtime.Unsupported,
            _ => Strings.Playback.Runtime.Missing,
        };

        /// <summary>The signature summary ladder (W33): a subject names the signer (trusted · pinned · anything else),
        /// then the subject-less pinned fingerprint, then the bare trust verdicts.</summary>
        public static RuntimeSignatureLine SignatureLine(RuntimeFacts facts)
        {
            if (facts.Signature is { } s && !string.IsNullOrWhiteSpace(s.Subject))
            {
                if (s.Trust == RuntimeTrust.Trusted) return RuntimeSignatureLine.SignedTrusted;
                return facts.PinnedFingerprint ? RuntimeSignatureLine.SignedPinned : RuntimeSignatureLine.SignedOther;
            }
            if (facts.PinnedFingerprint) return RuntimeSignatureLine.PinnedFingerprint;
            return facts.Trust switch
            {
                RuntimeTrust.Trusted => RuntimeSignatureLine.VerifiedFingerprint,
                RuntimeTrust.Untrusted => RuntimeSignatureLine.NotTrustedOverride,
                RuntimeTrust.UnsupportedPlatform => RuntimeSignatureLine.CheckUnavailable,
                _ => RuntimeSignatureLine.Unknown,
            };
        }

        /// <summary>A trust verdict's word key.</summary>
        public static string TrustLocKey(RuntimeTrust trust) => trust switch
        {
            RuntimeTrust.Trusted => Strings.Runtime.Sig.Trusted,
            RuntimeTrust.Untrusted => Strings.Runtime.Sig.NotTrusted,
            RuntimeTrust.UnsupportedPlatform => Strings.Runtime.Sig.UnsupportedPlatform,
            _ => Strings.Runtime.Sig.Unknown,
        };

        /// <summary>The signature sheet's dates: invariant "yyyy-MM-dd HH:mm:ss zzz" in local time.</summary>
        public static string SignatureDate(DateTimeOffset value)
            => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    // ══ END REGION (owner I) ════════════════════════════════════════════════════════════════════════════════════════

    // ══ REGION — SIGN-IN, GATING, BOOTSTRAP (gap batch B5, decision D2) ═════════════════════════════════════════════
    //
    // Pure, engine-free, and pinned by `SetupTests`: the wizard's arm/complete/defer markers and page ladder (ported from
    // 0.2.9 `App/SetupGating.cs`), the once-per-install arming (0.2.9 `App/SetupBootstrap.cs`, with the fresh-install probe
    // folded in from `SidebarBootstrap` per the sidebar miner's D6 — the disk half is `Setup.Host.cs`), the sign-in
    // surface's facet fold and door rules (0.2.9 `SetupCommands.Project` + `SetupSignInPresentation`), and the QR encoder
    // the pairing code renders through (0.2.9 `Features/Auth/Qr.cs` + `QrPlate.cs`, verbatim in behaviour).

    /// <summary>The wizard's pages in display order: Terms is "pre-setup", then Sign in (step 1 of 2), then Local playback
    /// (step 2 of 2). Not persisted, so renumbering is safe.</summary>
    public enum WizardPage : byte { Terms = 0, SignIn = 1, LocalPlayback = 2 }

    /// <summary>Why the wizard is open: a fresh install, a signed-out install that needs its account back, or a completed
    /// install re-armed because the terms changed.</summary>
    public enum WizardEntry : byte { FirstRun = 0, Reauth, TermsRearm }

    /// <summary>The wizard's markers and page ladder (ported from 0.2.9 <c>SetupGating</c>). Getting a marker wrong is
    /// unrecoverable per install — burned too early it denies the wizard to the fresh install it exists for, never written it
    /// re-shows a "one-time" wizard on every launch — so every rule is a fact in <c>SetupTests</c>.</summary>
    public static class Gating
    {
        /// <summary>The terms revision this build requires; a completed install that accepted an older one is re-armed.</summary>
        public const int TermsVersion = 1;

        /// <summary>Rise counts only Sign in and Local playback ("Step N of 2").</summary>
        public const int StepTotal = 2;

        /// <summary>Armed and not yet completed or deferred. No settings store ⇒ never (nothing could remember its exit).</summary>
        public static bool IsPending(IAppSettings? settings) => settings is not null && settings.Get(Platform.Keys.SetupPending);

        /// <summary>Reached the end at least once, ever. A deferred wizard leaves it false.</summary>
        public static bool IsCompleted(IAppSettings? settings) => settings is not null && settings.Get(Platform.Keys.SetupCompleted);

        /// <summary>Finish: Completed set, Pending cleared — two INDEPENDENT writes, because a terms re-arm finishes an install
        /// that is already completed and must still clear Pending. True only on the first completion.</summary>
        public static bool MarkCompleted(IAppSettings? settings)
        {
            if (settings is null) return false;
            bool first = !settings.Get(Platform.Keys.SetupCompleted);
            if (first) settings.Set(Platform.Keys.SetupCompleted, true);
            if (settings.Get(Platform.Keys.SetupPending)) settings.Set(Platform.Keys.SetupPending, false);
            return first;
        }

        /// <summary>Defer ("Not now", Escape, a shutdown-time close): clears Pending only, and never after completion.</summary>
        public static bool MarkDeferred(IAppSettings? settings)
        {
            if (settings is null || settings.Get(Platform.Keys.SetupCompleted) || !settings.Get(Platform.Keys.SetupPending)) return false;
            settings.Set(Platform.Keys.SetupPending, false);
            return true;
        }

        /// <summary>Skip the Sign in page for someone already signed in.</summary>
        public static bool SkipSignIn(bool authed) => authed;

        /// <summary>Only a terms re-arm, and only while nothing long-running is in flight, may be dismissed by Escape.</summary>
        public static bool CanDismiss(WizardEntry entry, bool busy) => entry == WizardEntry.TermsRearm && !busy;

        /// <summary>A completed install whose accepted terms are older than <paramref name="current"/>.</summary>
        public static bool NeedsTermsRearm(bool completed, int accepted, int current) => completed && accepted < current;

        /// <summary>A completed install that predates terms versioning is stamped, not re-shown.</summary>
        public static bool GrandfathersTerms(bool completed, int accepted) => completed && accepted == 0;

        /// <summary>The data folder is gone but the settings still remember a finished wizard: treat it as a fresh install.</summary>
        public static bool NeedsFreshInstallReset(bool fresh, bool completed, int termsAccepted) => fresh && (completed || termsAccepted > 0);

        /// <summary>The next page, skipping Sign in when asked, clamped at Local playback.</summary>
        public static WizardPage NextPage(WizardPage page, bool skipSignIn)
        {
            var next = (WizardPage)Math.Min((int)page + 1, (int)WizardPage.LocalPlayback);
            if (skipSignIn && next == WizardPage.SignIn) next = WizardPage.LocalPlayback;
            return next;
        }

        /// <summary>The previous page, skipping Sign in when asked, clamped at Terms.</summary>
        public static WizardPage PrevPage(WizardPage page, bool skipSignIn)
        {
            var prev = (WizardPage)Math.Max((int)page - 1, (int)WizardPage.Terms);
            if (skipSignIn && prev == WizardPage.SignIn) prev = WizardPage.Terms;
            return prev;
        }

        /// <summary>"Step N of 2", or null for Terms ("Pre-setup").</summary>
        public static (int Step, int Total)? StepNumber(WizardPage page) => page == WizardPage.Terms ? null : ((int)page, StepTotal);

        /// <summary>The footer's progress: Terms 0, Sign in .5, Local playback 1.</summary>
        public static float Progress(WizardPage page) => (int)page / (float)StepTotal;

        /// <summary>Back shows on the last page only; there is no Done page.</summary>
        public static bool ShowsBack(WizardPage page) => page == WizardPage.LocalPlayback;

        /// <summary>A re-auth on an install whose runtime is already Ready has nothing left to do after Sign in.</summary>
        public static bool SkipsLocalPlayback(WizardEntry entry, bool runtimeReady) => entry == WizardEntry.Reauth && runtimeReady;

        /// <summary>The runtime banner/toast stay silent while the wizard is armed or open — it asks the same question.</summary>
        public static bool SuppressesRuntimePrompts(bool pending, bool sessionOpen) => pending || sessionOpen;

        /// <summary>The entry a sign-in-required launch opens: a completed install re-authenticates, anything else is a first run.</summary>
        public static WizardEntry EntryFor(bool completed) => completed ? WizardEntry.Reauth : WizardEntry.FirstRun;
    }

    /// <summary>The "this app has run before" witnesses on disk, probed by <c>Setup.Host.cs</c> BEFORE <c>library.db</c> is
    /// opened (probing after would make every install look existing).</summary>
    public readonly record struct InstallWitnesses(bool LibraryDb, bool StoredCredential, bool History);

    /// <summary>The once-per-install arming (0.2.9 <c>SetupBootstrap</c>), over witnesses the SHELL probed.</summary>
    public static class Bootstrap
    {
        /// <summary>Bump AND add the new one-time work to <see cref="Run"/> when a release needs another startup step.</summary>
        public const int TargetVersion = 1;

        /// <summary>Fresh iff no witness exists: not the library, not a stored credential, not the navigation history, and no
        /// pane preference written by a build that predates the sidebar designs. One answer, shared by the wizard and the
        /// sidebar chooser (the sidebar miner's D6 folded 0.2.9's second detector into this one).</summary>
        public static bool IsFreshInstall(in InstallWitnesses disk, IAppSettings settings)
            => !disk.LibraryDb && !disk.StoredCredential && !disk.History
               && !settings.Get(Platform.Keys.SidebarWidthUserSetLegacy) && !settings.Get(Platform.Keys.SidebarCollapsedLegacy);

        /// <summary>Arm the wizard for a fresh install, suppress it for an existing one — ONCE per install — then the two
        /// every-launch checks: a wiped data folder resets a "completed" install, and a terms bump re-arms a completed one.
        /// Both branches of the one-time arm mark the sidebar chooser seen: on a fresh install the wizard is the one onboarding
        /// prompt (0.2.9's rule), and an existing install never saw the chooser to begin with.</summary>
        public static void Run(IAppSettings settings, in InstallWitnesses disk)
        {
            bool fresh = IsFreshInstall(in disk, settings);
            bool completed = settings.Get(Platform.Keys.SetupCompleted);
            int accepted = settings.Get(Platform.Keys.TermsAcceptedVersion);
            if (Gating.NeedsFreshInstallReset(fresh, completed, accepted))
            {
                settings.Set(Platform.Keys.SetupPending, true);
                settings.Set(Platform.Keys.SetupCompleted, false);
                settings.Set(Platform.Keys.TermsAcceptedVersion, 0);
                Log.Info("setup", "data folder is gone — treating this install as fresh (terms accepted was " + accepted + ")");
            }

            if (settings.Get(Platform.Keys.SetupBootstrapVersion) < TargetVersion)
            {
                settings.Set(Platform.Keys.SetupPending, fresh);
                settings.Set(Platform.Keys.SetupCompleted, !fresh);
                settings.Set(Platform.Keys.SidebarOnboardingSeen, true);
                settings.Set(Platform.Keys.SetupBootstrapVersion, TargetVersion);
                Log.Info("setup", fresh ? "fresh install: first-run setup wizard armed" : "existing install: first-run setup wizard suppressed");
            }

            RearmForTerms(settings);
        }

        static void RearmForTerms(IAppSettings settings)
        {
            bool completed = settings.Get(Platform.Keys.SetupCompleted);
            int accepted = settings.Get(Platform.Keys.TermsAcceptedVersion);
            if (Gating.GrandfathersTerms(completed, accepted))
            {
                settings.Set(Platform.Keys.TermsAcceptedVersion, Gating.TermsVersion);
                return;
            }
            if (!Gating.NeedsTermsRearm(completed, accepted, Gating.TermsVersion) || settings.Get(Platform.Keys.SetupPending)) return;
            settings.Set(Platform.Keys.SetupPending, true);
            Log.Info("setup", "terms revision changed since this install accepted (" + accepted + " < " + Gating.TermsVersion + ") — wizard re-armed");
        }
    }

    /// <summary>The sign-in surface's facets — six, where the two flows and the session have many more states.</summary>
    public enum SignInFacet : byte { Idle = 0, Busy, Done, Failed, Expired, Premium }

    /// <summary>What the sign-in surface shows and when its door opens. PURE over the sign-in state and the session.</summary>
    public static class SignInRules
    {
        /// <summary>The ContentDialog width rung the sign-in surface uses (the setup dialog's, ch 19 §3.1).</summary>
        public const float DialogWidth = 460f;

        /// <summary>The pairing QR's requested size (a hint: <see cref="QrPlate"/> snaps it to whole pixels per module).</summary>
        public const float QrSize = 96f;

        /// <summary>The fold. Online is Done; a Premium refusal wins over everything else; once a token was handed over the
        /// session's phase is the progress (Failed = retry in place); before that the browser's wait is Busy, a flow's failure
        /// (a browser that never came back included) shows over the Idle cards so either path can be retried in place, and a
        /// lapsed pairing code is Expired. A stored credential the AP
        /// definitively rejected opens the surface on Failed, which says why the user is being asked again.</summary>
        public static SignInFacet Project(in Spotify.SignInState st, Spotify.SessionPhase phase, Spotify.SessionFault fault)
        {
            if (phase == Spotify.SessionPhase.Online) return SignInFacet.Done;
            if (phase == Spotify.SessionPhase.Failed && fault == Spotify.SessionFault.NotPremium) return SignInFacet.Premium;
            if (st.Handed) return phase == Spotify.SessionPhase.Failed ? SignInFacet.Failed : SignInFacet.Busy;
            if (st.Browser is Spotify.SignInStage.Starting or Spotify.SignInStage.Waiting or Spotify.SignInStage.Exchanging)
                return SignInFacet.Busy;
            if (st.Browser is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied or Spotify.SignInStage.Expired
                || st.Code is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied)
                return SignInFacet.Failed;
            if (st.Code == Spotify.SignInStage.Expired) return SignInFacet.Expired;
            if (phase == Spotify.SessionPhase.Failed && fault == Spotify.SessionFault.CredentialRejected) return SignInFacet.Failed;
            return SignInFacet.Idle;
        }

        /// <summary>The Failed facet's sentence (a loc KEY). The session's verdict first, then the flows' reasons.</summary>
        public static string FailureKey(in Spotify.SignInState st, Spotify.SessionFault fault)
        {
            if (fault == Spotify.SessionFault.CredentialRejected) return LocRejected;
            if (st.Handed && fault is Spotify.SessionFault.LoginRefused) return LocRefused;
            if (st.Browser == Spotify.SignInStage.Denied || st.Code == Spotify.SignInStage.Denied) return LocDenied;
            if (st.Browser == Spotify.SignInStage.Expired) return LocBrowserTimedOut;
            return st.Error switch
            {
                Spotify.SignInError.BrowserUnavailable => LocBrowserUnavailable,
                Spotify.SignInError.Refused => Strings.Auth.GenericError,
                _ => Strings.Auth.NetworkError,
            };
        }

        /// <summary>Does the door open the surface? An explicit request always does (a "Sign in" with nothing to resume).
        /// Entering SignInRequired does only when nothing is stored — a resume behind the cache-first shell must never flash
        /// a login. Never a second surface over the first.</summary>
        public static bool OpensDoor(bool requested, bool enteredSignInRequired, bool hasStoredCredential, bool alreadyOpen)
            => !alreadyOpen && (requested || (enteredSignInRequired && !hasStoredCredential));

        /// <summary>Does the surface close itself? When the account is signed in and online.</summary>
        public static bool ClosesDoor(Spotify.SessionPhase phase) => phase == Spotify.SessionPhase.Online;

        /// <summary>Does the surface ask for a pairing code? When none is live, none is being asked for, and the last one did
        /// not just lapse or fail (those wait for the user's "Get a new code"), and no token was handed over yet.</summary>
        public static bool NeedsPairingCode(in Spotify.SignInState st)
            => !st.Handed && st.Code == Spotify.SignInStage.Idle;

        /// <summary>"mm:ss" left on a pairing code, never negative.</summary>
        public static string Countdown(long expiresAtMs, long nowMs)
        {
            long s = Math.Max(0L, (expiresAtMs - nowMs + 999) / 1000);
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // New keys (batch-loc B5.json). Literal until the orchestrator merges them into en-US.json.
        public const string LocRejected = "setup.signIn.rejected";
        public const string LocRefused = "setup.signIn.refused";
        public const string LocDenied = "setup.signIn.denied";
        public const string LocBrowserUnavailable = "setup.signIn.browserUnavailable";
        public const string LocBrowserTimedOut = "setup.signIn.browserTimedOut";
        public const string LocGetCodeFailed = "setup.signIn.codeUnavailable";
        public const string LocOpenPairPage = "setup.signIn.openPairPage";
    }

    /// <summary>QR plate arithmetic: a 4-module quiet zone each side, a whole number of DIPs per module (a fractional module
    /// blurs and stops scanning), floor 2.</summary>
    public static class QrPlate
    {
        public static int CellFor(float size, int modules) => Math.Max(2, (int)(size / (modules + 8)));
        public static int PlateFor(float size, int modules) => (modules + 8) * CellFor(size, modules);
    }

    /// <summary>A compact ISO/IEC 18004 QR encoder: byte mode, versions 1–10, GF(256) Reed–Solomon, best-of-8 masking by the
    /// penalty score. Ported from 0.2.9 <c>Features/Auth/Qr.cs</c> (its round-trip reader test came with it). Allocates —
    /// once per pairing code.</summary>
    public static class Qr
    {
        public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

        // GF(256) over the primitive polynomial 0x11D.
        static readonly int[] Exp = new int[512];
        static readonly int[] LogTable = new int[256];

        static Qr()
        {
            int x = 1;
            for (int i = 0; i < 255; i++) { Exp[i] = x; LogTable[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11D; }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[LogTable[a] + LogTable[b]];

        // { ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data } for versions 1..10.
        static readonly int[][] TableM =
        [
            [10,1,16,0,0], [16,1,28,0,0], [26,1,44,0,0], [18,2,32,0,0], [24,2,43,0,0],
            [16,4,27,0,0], [18,4,31,0,0], [22,2,38,2,39], [22,3,36,2,37], [26,4,43,1,44],
        ];
        static readonly int[][] TableL =
        [
            [7,1,19,0,0], [10,1,34,0,0], [15,1,55,0,0], [20,1,80,0,0], [26,1,108,0,0],
            [18,2,68,0,0], [20,2,78,0,0], [24,2,97,0,0], [30,2,116,0,0], [18,2,68,2,69],
        ];
        static readonly int[][] TableQ =
        [
            [13,1,13,0,0], [22,1,22,0,0], [18,2,17,0,0], [26,2,24,0,0], [18,2,15,2,16],
            [24,4,19,0,0], [18,2,14,4,15], [22,4,18,2,19], [20,4,16,4,17], [24,6,19,2,20],
        ];
        static readonly int[][] TableH =
        [
            [17,1,9,0,0], [28,1,16,0,0], [22,2,13,0,0], [16,4,9,0,0], [22,2,11,2,12],
            [28,4,15,0,0], [26,4,13,1,14], [26,4,14,2,15], [24,4,12,4,13], [28,6,15,2,16],
        ];

        static int[][] Table(Ecc e) => e switch { Ecc.L => TableL, Ecc.Q => TableQ, Ecc.H => TableH, _ => TableM };

        static readonly int[][] AlignPos =
        [
            [], [6,18], [6,22], [6,26], [6,30], [6,34], [6,22,38], [6,24,42], [6,26,46], [6,28,50],
        ];

        /// <summary>The module matrix, <c>[x, y]</c>, true = dark. Throws <see cref="ArgumentException"/> when the payload does
        /// not fit version 10 at <paramref name="ecc"/>.</summary>
        public static bool[,] Encode(string text, Ecc ecc)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int[][] tbl = Table(ecc);
            int version = -1, dataCodewords = 0;
            for (int v = 1; v <= 10; v++)
            {
                int[] t = tbl[v - 1];
                int dc = t[1] * t[2] + t[3] * t[4];
                if (4 + (v <= 9 ? 8 : 16) + bytes.Length * 8 <= dc * 8) { version = v; dataCodewords = dc; break; }
            }
            if (version < 0) throw new ArgumentException("QR payload too large for versions 1-10", nameof(text));

            var bits = new BitBuf();
            bits.Put(0b0100, 4);
            bits.Put(bytes.Length, version <= 9 ? 8 : 16);
            foreach (byte b in bytes) bits.Put(b, 8);
            int cap = dataCodewords * 8;
            for (int i = 0; i < 4 && bits.Len < cap; i++) bits.Put(0, 1);
            while (bits.Len % 8 != 0) bits.Put(0, 1);
            int[] data = bits.ToBytes();
            var padded = new int[dataCodewords];
            Array.Copy(data, padded, Math.Min(data.Length, dataCodewords));
            for (int i = data.Length, pad = 0; i < dataCodewords; i++, pad++) padded[i] = (pad & 1) == 0 ? 0xEC : 0x11;

            int[] spec = tbl[version - 1];
            int ecLen = spec[0], g1 = spec[1], g1d = spec[2], g2 = spec[3], g2d = spec[4];
            var blocksData = new List<int[]>();
            var blocksEc = new List<int[]>();
            int p = 0;
            for (int i = 0; i < g1; i++) { int[] blk = Slice(padded, ref p, g1d); blocksData.Add(blk); blocksEc.Add(ReedSolomon(blk, ecLen)); }
            for (int i = 0; i < g2; i++) { int[] blk = Slice(padded, ref p, g2d); blocksData.Add(blk); blocksEc.Add(ReedSolomon(blk, ecLen)); }

            var final = new List<int>();
            int maxData = Math.Max(g1d, g2d);
            for (int c = 0; c < maxData; c++) foreach (int[] blk in blocksData) if (c < blk.Length) final.Add(blk[c]);
            for (int c = 0; c < ecLen; c++) foreach (int[] blk in blocksEc) final.Add(blk[c]);

            int size = version * 4 + 17;
            var mod = new int[size, size];          // -1 unset, 0/1 data, 2/3 function light/dark
            for (int a = 0; a < size; a++) for (int b = 0; b < size; b++) mod[a, b] = -1;
            PlaceFinder(mod, 0, 0); PlaceFinder(mod, size - 7, 0); PlaceFinder(mod, 0, size - 7);
            for (int i = 8; i < size - 8; i++)
            {
                if (mod[i, 6] == -1) mod[i, 6] = (i % 2 == 0) ? 3 : 2;
                if (mod[6, i] == -1) mod[6, i] = (i % 2 == 0) ? 3 : 2;
            }
            foreach (int cx in AlignPos[version - 1])
                foreach (int cy in AlignPos[version - 1])
                {
                    if (mod[cx, cy] != -1) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            mod[cx + dx, cy + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1 ? 3 : 2;
                }
            mod[8, size - 8] = 3;
            for (int i = 0; i < 9; i++) { if (mod[i, 8] == -1) mod[i, 8] = 2; if (mod[8, i] == -1) mod[8, i] = 2; }
            for (int i = 0; i < 8; i++) { if (mod[size - 1 - i, 8] == -1) mod[size - 1 - i, 8] = 2; if (mod[8, size - 1 - i] == -1) mod[8, size - 1 - i] = 2; }
            if (version >= 7)
                for (int i = 0; i < 6; i++) for (int j = 0; j < 3; j++) { mod[i, size - 11 + j] = 2; mod[size - 11 + j, i] = 2; }

            PlaceData(mod, size, final);

            int bestPenalty = int.MaxValue;
            int[,]? best = null;
            for (int mask = 0; mask < 8; mask++)
            {
                var trial = (int[,])mod.Clone();
                ApplyMask(trial, size, mask);
                PlaceFormat(trial, size, ecc, mask);
                if (version >= 7) PlaceVersion(trial, size, version);
                int penalty = Penalty(trial, size);
                if (penalty < bestPenalty) { bestPenalty = penalty; best = trial; }
            }

            var outp = new bool[size, size];
            for (int a = 0; a < size; a++) for (int b = 0; b < size; b++) outp[a, b] = (best![a, b] & 1) == 1;
            return outp;
        }

        static int[] Slice(int[] src, ref int p, int len) { var r = new int[len]; Array.Copy(src, p, r, 0, len); p += len; return r; }

        static int[] ReedSolomon(int[] data, int ecLen)
        {
            var gen = new int[] { 1 };
            for (int i = 0; i < ecLen; i++)
            {
                var ng = new int[gen.Length + 1];
                for (int j = 0; j < gen.Length; j++) { ng[j] ^= gen[j]; ng[j + 1] ^= Mul(gen[j], Exp[i]); }
                gen = ng;
            }
            var res = new int[ecLen];
            foreach (int d in data)
            {
                int factor = d ^ res[0];
                Array.Copy(res, 1, res, 0, ecLen - 1);
                res[ecLen - 1] = 0;
                // gen is MONIC (gen[0] = 1): the divisor coefficients are gen[1..ecLen] — reading gen[i] was 0.2.9's
                // off-by-one that rendered a symbol no reader could decode.
                for (int i = 0; i < ecLen; i++) res[i] ^= Mul(gen[i + 1], factor);
            }
            return res;
        }

        static void PlaceFinder(int[,] m, int ox, int oy)
        {
            int n = m.GetLength(0);
            for (int dy = -1; dy <= 7; dy++)
                for (int dx = -1; dx <= 7; dx++)
                {
                    int x = ox + dx, y = oy + dy;
                    if (x < 0 || y < 0 || x >= n || y >= n) continue;
                    bool dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6
                        && (dx == 0 || dx == 6 || dy == 0 || dy == 6 || (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                    m[x, y] = dark ? 3 : 2;
                }
        }

        static void PlaceData(int[,] m, int size, List<int> codewords)
        {
            int bit = 0, total = codewords.Count * 8;
            bool up = true;
            for (int col = size - 1; col > 0; col -= 2, up = !up)
            {
                if (col == 6) col--;
                for (int row = 0; row < size; row++)
                {
                    int y = up ? size - 1 - row : row;
                    for (int c = 0; c < 2; c++)
                    {
                        int x = col - c;
                        if (m[x, y] != -1) continue;
                        int v = 0;
                        if (bit < total) { v = (codewords[bit >> 3] >> (7 - (bit & 7))) & 1; bit++; }
                        m[x, y] = v;
                    }
                }
            }
        }

        static void ApplyMask(int[,] m, int size, int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (m[x, y] is not (0 or 1)) continue;
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (y / 2 + x / 3) % 2 == 0,
                        5 => (x * y) % 2 + (x * y) % 3 == 0,
                        6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                        _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
                    };
                    if (invert) m[x, y] ^= 1;
                }
        }

        static readonly int[] EccFormat = [1, 0, 3, 2];   // L, M, Q, H → the format field's 01, 00, 11, 10

        static void PlaceFormat(int[,] m, int size, Ecc ecc, int mask)
        {
            int data = (EccFormat[(int)ecc] << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++) m[8, i] = Bit(bits, i) | 2;
            m[8, 7] = Bit(bits, 6) | 2; m[8, 8] = Bit(bits, 7) | 2; m[7, 8] = Bit(bits, 8) | 2;
            for (int i = 9; i < 15; i++) m[14 - i, 8] = Bit(bits, i) | 2;
            for (int i = 0; i < 8; i++) m[size - 1 - i, 8] = Bit(bits, i) | 2;
            for (int i = 8; i < 15; i++) m[8, size - 15 + i] = Bit(bits, i) | 2;
        }

        static readonly int[] VersionBits = [0, 0, 0, 0, 0, 0, 0, 0x07C94, 0x085BC, 0x09A99, 0x0A4D3];

        static void PlaceVersion(int[,] m, int size, int version)
        {
            int v = VersionBits[version];
            for (int i = 0; i < 18; i++)
            {
                int b = (v >> i) & 1, a = i / 3, c = i % 3;
                m[a, size - 11 + c] = b | 2;
                m[size - 11 + c, a] = b | 2;
            }
        }

        static int Bit(int v, int i) => (v >> i) & 1;

        static int Penalty(int[,] m, int size)
        {
            int score = 0;
            for (int line = 0; line < size; line++) { score += RunPenalty(m, size, line, true); score += RunPenalty(m, size, line, false); }
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    int v = m[x, y] & 1;
                    if ((m[x + 1, y] & 1) == v && (m[x, y + 1] & 1) == v && (m[x + 1, y + 1] & 1) == v) score += 3;
                }
            for (int y = 0; y < size; y++) for (int x = 0; x <= size - 11; x++) if (FinderLike(m, x, y, true)) score += 40;
            for (int x = 0; x < size; x++) for (int y = 0; y <= size - 11; y++) if (FinderLike(m, x, y, false)) score += 40;
            int dark = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) dark += m[x, y] & 1;
            int pct = dark * 100 / (size * size);
            score += Math.Min(Math.Abs(pct - 50) / 5, Math.Abs(pct - 50 + 4) / 5) * 10;
            return score;
        }

        static int RunPenalty(int[,] m, int size, int line, bool row)
        {
            int score = 0, run = 1, prev = -1;
            for (int i = 0; i < size; i++)
            {
                int v = row ? m[i, line] & 1 : m[line, i] & 1;
                if (v == prev) { run++; if (run == 5) score += 3; else if (run > 5) score += 1; }
                else { run = 1; prev = v; }
            }
            return score;
        }

        static readonly int[] FinderA = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
        static readonly int[] FinderB = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];

        static bool FinderLike(int[,] m, int x, int y, bool row)
        {
            bool a = true, b = true;
            for (int k = 0; k < 11; k++)
            {
                int v = row ? m[x + k, y] & 1 : m[x, y + k] & 1;
                if (v != FinderA[k]) a = false;
                if (v != FinderB[k]) b = false;
            }
            return a || b;
        }

        sealed class BitBuf
        {
            readonly List<byte> _bits = new();
            public int Len => _bits.Count;
            public void Put(int value, int width) { for (int i = width - 1; i >= 0; i--) _bits.Add((byte)((value >> i) & 1)); }
            public int[] ToBytes()
            {
                var r = new int[(_bits.Count + 7) / 8];
                for (int i = 0; i < _bits.Count; i++) r[i >> 3] |= _bits[i] << (7 - (i & 7));
                return r;
            }
        }
    }

    // ══ END REGION (gap batch B5) ═══════════════════════════════════════════════════════════════════════════════════
}
