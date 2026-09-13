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
// Still the Wave-6 skeleton for owner R, EXCEPT the region below: the runtime provisioning card's pure phase rules,
// written by owner I in Wave 4 (A18) because `+Screens/Setup.UI.Runtime.cs` renders the card for the Wave-4 shell
// banner. The phase ENUM itself is `Platform.cs`'s `RuntimePhase` (one enum for the whole app, ch 28 §8) — not here.

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
}
