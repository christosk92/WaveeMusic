// ── Screens/Setup.UI.Runtime.cs ────────────────────────────────────────────────────────────────────────────────────
// the 10-phase runtime provisioning card BODY (PlaybackRuntimeSetupCard 1,030), rendered by the shell's banner in
// Wave 4 and by the wizard's Local-playback page in Wave 6 — written by I in Wave 4, mounted by R in Wave 6 (the
// Settings.UI.Video.cs precedent, inverted)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 1100 lines
// Spec: ch 19 §9.4 (which budgets it 1,100 under Screens/Setup.UI.cs)
//
// WHAT IS HERE. The card's state holder (`Setup.RuntimeModel`, one per open, shared by the body and the footer), the
// process-wide cells the banner gate and the other doors read (`Setup.Runtime`), the host SEAM the SHELL half fills
// (`Setup.RuntimeHost` — owner R's `Setup.Host.cs`, Wave 6), the body arms for every phase, the one command row, and
// the dialog that hosts them at rung 2 of the modal width ladder. Every decision the card draws is `Setup.RuntimeRules`
// in `Setup.cs` (CORE) — this file is layout and wiring. (The nested signature sheet is plan §2's: the overlay file.)
//
// WHAT IS NOT HERE, ON PURPOSE. The host seam speaks only the card's own UI vocabulary — a catalog of packs with a
// version/arch/hash/size, a download with bytes, a verify verdict, a signature summary. It names no runtime internals:
// how a pack is fetched, verified or loaded is the SHELL half's business, reached only through these delegates. With no
// host installed (a public-only build, or before Wave 6 wires it) every verb answers exactly as 0.2.9's
// `provisioner is null` path did: the Failed phase with "Local audio is not active.".
//
// THE MOUNT POINTS. `Setup.RuntimeCard()` (the body) and `Setup.RuntimeCardFooter()` (the command row) read the active
// model from `Setup.Runtime.Active`; `Setup.OpenRuntimeCard(overlay, post)` is the standalone dialog the shell banner
// opens. The wizard (Wave 6) calls `Setup.Runtime.Begin(post, onClose, onWizardExit)` and mounts the body and the
// named pieces (`RuntimeVersionPicker`, `RuntimeVerifyFacts`, `RuntimeReadyFacts`, `RuntimeLocalSourceRows`, …).

using System.Threading;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Setup
{
    // ══ 1. THE HOST SEAM (the SHELL half fills it) ═════════════════════════════════════════════════════════════════

    /// <summary>A catalog answer: the packs this device can install (index 0 is the recommended one), the best pick,
    /// and whether packs exist only for ANOTHER architecture (the "wrong arch" sentence).</summary>
    public readonly record struct RuntimeCatalogAnswer(IReadOnlyList<RuntimePack> Supported, RuntimePack? Best, bool AnyForOtherArch);

    /// <summary>One progress report: bytes so far, the total (0 = unknown), and whether the host moved on to verifying.</summary>
    public readonly record struct RuntimeProgress(long Received, long Total, bool Verifying);

    /// <summary>The verdict of an install or a directory registration, with the status as it now stands.
    /// <paramref name="Error"/> is the user-facing sentence the host composed (null = the card's own fallback).</summary>
    public readonly record struct RuntimeVerify(bool Ok, bool NeedsUntrustedConfirmation, string? Directory,
                                                RuntimeFacts Facts, string? Error);

    /// <summary>The runtime provisioning HOST. Delegates only (the CORE/SHELL seam shape); a null member makes the verb
    /// that needs it answer "Local audio is not active." rather than throw.</summary>
    public sealed class RuntimeHost
    {
        public Func<CancellationToken, Task<RuntimeCatalogAnswer?>>? FetchCatalog { get; init; }
        /// <summary>Download, verify and activate one pack. Progress is reported from any thread; the card marshals it.</summary>
        public Func<RuntimePack, bool, Action<RuntimeProgress>, CancellationToken, Task<RuntimeVerify>>? Install { get; init; }
        /// <summary>Recognize and activate a user-supplied directory, synchronously (the card shows Verifying meanwhile).</summary>
        public Func<string, bool, RuntimeVerify>? RegisterDirectory { get; init; }
        /// <summary>The installed desktop Spotify's directory, or null when there is none to reuse.</summary>
        public Func<string?>? InstalledDirectory { get; init; }
        /// <summary>Forget the active runtime (the Ready view's Remove).</summary>
        public Action? ClearActive { get; init; }
        /// <summary>Re-open playback against the new runtime after a genuine success (not after "already up to date").</summary>
        public Func<Task>? RefreshAfterSuccess { get; init; }
    }

    // ══ 2. THE PROCESS-WIDE CELLS ═════════════════════════════════════════════════════════════════════════════════

    public static class Runtime
    {
        /// <summary>The runtime status the banner gate and the Ready fact box read. Published WHOLE by the host (and by
        /// this card after a verdict); NotApplicable until then, so the banner never flashes before anything is known.</summary>
        public static readonly Signal<RuntimeFacts> Status = new(RuntimeFacts.NotApplicable);

        /// <summary>Settings writes are not signals: anything that flips "dismissed" bumps this so the banner
        /// re-evaluates on the next frame.</summary>
        public static readonly Signal<int> BannerEpoch = new(0);

        /// <summary>The other doors into the card (a toast's "Set up", Settings ▸ Playback) bump this; the shell
        /// overlay's watcher opens the dialog.</summary>
        public static readonly Signal<int> OpenRequest = new(0);

        /// <summary>True while the first-run wizard's own Local-playback page is up (owner R writes it): the banner
        /// must not ask the same question twice.</summary>
        public static readonly Signal<bool> WizardCovering = new(false);

        /// <summary>The model the mounted body and footer render. Null ⇒ the "Local audio is not active" body.</summary>
        public static readonly Signal<RuntimeModel?> Active = new(null);

        /// <summary>The host. Installed once by the SHELL half.</summary>
        public static RuntimeHost? Host { get; set; }

        public static void BumpBanner() => BannerEpoch.Value = BannerEpoch.Peek() + 1;

        public static void RequestOpen() => OpenRequest.Value = OpenRequest.Peek() + 1;

        /// <summary>Stop offering (banner included): the user stays remote-only until they ask.</summary>
        public static void Dismiss(string reason)
        {
            Log.Event(WaveeLogLevel.Info, "ui", reason == "banner" ? "runtime.banner.dismiss" : "runtime.setup.dismiss",
                "Playback runtime setup dismissed", null, -1, null,
                WaveeLogField.Of("issue", Status.Peek().Issue.ToString()));
            Platform.Settings.Set(Platform.Keys.PlaybackRuntimeSetupDismissed, true);
            BumpBanner();
        }

        /// <summary>Start a card session: a fresh model at the status's opening phase, made the active one.
        /// <paramref name="onClose"/> / <paramref name="onWizardExit"/> are the wizard's hooks (null standalone).</summary>
        public static RuntimeModel Begin(Action<Action> post, Action? onClose = null, Action? onWizardExit = null)
        {
            Active.Peek()?.Dispose();
            var model = new RuntimeModel(post) { OnClose = onClose, OnWizardExit = onWizardExit };
            Active.Value = model;
            return model;
        }

        /// <summary>End a card session (the dialog closed, the wizard left the page): cancel in-flight work.</summary>
        public static void End(RuntimeModel model)
        {
            model.Dispose();
            if (ReferenceEquals(Active.Peek(), model)) Active.Value = null;
        }
    }

    // ══ 3. THE MODEL (one per open, shared by the body and the footer) ═══════════════════════════════════════════════

    /// <summary>All card state + the provisioning verbs. Background work marshals every signal write through the post,
    /// so the UI thread is the only writer.</summary>
    public sealed class RuntimeModel
    {
        public readonly Signal<RuntimePhase> Phase;
        public readonly Signal<RuntimeCatalog> Catalog = new(RuntimeCatalog.NotFetched);
        public readonly Signal<string?> Error = new(null);
        public readonly Signal<long> Received = new(0);
        public readonly Signal<long> Total = new(0);
        public readonly Signal<string?> DownloadLabel = new(null);
        public readonly Signal<int> SelectedPack = new(0);
        /// <summary>Ready because a check found the ALREADY-active pack, not a fresh install (distinct wording).</summary>
        public readonly Signal<bool> UpToDate = new(false);

        public RuntimePack? ActiveEntry { get; private set; }
        public IReadOnlyList<RuntimePack> SupportedPacks { get; private set; } = [];
        public bool AnyForOtherArch { get; private set; }

        /// <summary>The wizard's "close this page" (null standalone, where the dialog handle closes).</summary>
        public Action? OnClose { get; init; }
        /// <summary>The wizard's escape hatch out of the WHOLE flow (it owns the deferral marker).</summary>
        public Action? OnWizardExit { get; init; }
        public bool WizardHosted => OnWizardExit is not null;

        readonly Action<Action> _post;
        CancellationTokenSource? _cts;
        string? _untrustedDir;
        internal OverlayHandle? Handle;

        internal RuntimeModel(Action<Action> post)
        {
            _post = post;
            Phase = new(RuntimeRules.InitialPhase(Runtime.Status.Peek().IsReady));
            Log(WaveeLogLevel.Debug, "runtime.setup.model", "Playback runtime setup model created",
                WaveeLogField.Of("initialPhase", Phase.Peek().ToString()),
                WaveeLogField.Of("hasHost", Runtime.Host is not null));
        }

        public bool IsBusy => RuntimeRules.IsBusy(Phase.Peek());
        public RuntimeFacts Status => Runtime.Status.Value;

        internal void Dispose() => _cts?.Cancel();

        public void Close() { if (OnClose is { } c) c(); else Handle?.Close(); }

        public void NotNow() { Runtime.Dismiss("setup"); Close(); }

        public void Back()
        {
            UpToDate.Value = false;
            SetPhase(RuntimeRules.BackTarget(Runtime.Status.Peek().IsReady), "back");
        }

        public void Cancel() => _cts?.Cancel();

        public void Retry() => StartDownload();

        public void CheckForUpdate() => StartDownload();

        CancellationTokenSource NewCts()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            return _cts;
        }

        // ── the network path ────────────────────────────────────────────────────────────────────────────────────────

        public void StartDownload()
        {
            if (Runtime.Host is not { FetchCatalog: { } fetch, Install: not null } host)
            { Fail(Loc.Get(Strings.Playback.Runtime.NotActive), "no-host"); return; }
            Log(WaveeLogLevel.Info, "runtime.setup.download.start", "Runtime setup download flow started");
            Error.Value = null;
            SetPhase(RuntimePhase.FetchingCatalog, "start-download");
            SetCatalog(RuntimeCatalog.Fetching, "start-download");
            var facts = Runtime.Status.Peek();
            var cts = NewCts();
            _ = Task.Run(async () =>
            {
                try
                {
                    var answer = await fetch(cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;
                    if (answer is not { } a)
                    {
                        _post(() => { SetCatalog(RuntimeCatalog.Failed, "catalog-null"); Fail(Loc.Get(Strings.Playback.Runtime.DownloadFailed), "catalog-null"); });
                        return;
                    }
                    _post(() => { AnyForOtherArch = a.AnyForOtherArch; SupportedPacks = a.Supported; SetCatalog(RuntimeCatalog.Loaded, "download-catalog-loaded"); });
                    if (a.Best is not { } best)
                    {
                        _post(() => Fail(Loc.Get(a.AnyForOtherArch ? Strings.Playback.Runtime.WrongArch : Strings.Playback.Runtime.NoPack), "no-best"));
                        return;
                    }
                    // The newest pack is often the one already active; reinstalling it would overwrite the loaded file.
                    if (RuntimeRules.AlreadyCurrent(facts.IsReady, facts.PackId, best.PackId)) { _post(AlreadyUpToDate); return; }
                    await DownloadAsync(host, best, cts).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _post(() =>
                    {
                        if (Catalog.Peek() == RuntimeCatalog.Fetching) SetCatalog(RuntimeCatalog.NotFetched, "download-cancelled");
                        SetPhase(RuntimeRules.CancelTarget(RuntimeCancel.Download), "download-cancelled");
                    });
                }
                catch (Exception ex) { _post(() => Fail(ex.Message, "download-exception")); }
            });
        }

        public void InstallSelected()
        {
            if (Runtime.Host is not { Install: not null } host) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive), "no-host"); return; }
            int idx = SelectedPack.Peek();
            if ((uint)idx >= (uint)SupportedPacks.Count) return;
            var entry = SupportedPacks[idx];
            Log(WaveeLogLevel.Info, "runtime.setup.install_selected", "Installing the selected runtime pack",
                WaveeLogField.Of("index", idx), WaveeLogField.Of("pack", entry.PackId),
                WaveeLogField.Of("version", entry.Version), WaveeLogField.Of("arch", entry.Arch));
            var facts = Runtime.Status.Peek();
            if (RuntimeRules.AlreadyCurrent(facts.IsReady, facts.PackId, entry.PackId)) { AlreadyUpToDate(); return; }
            var cts = NewCts();
            _ = Task.Run(async () =>
            {
                try { await DownloadAsync(host, entry, cts).ConfigureAwait(false); }
                catch (OperationCanceledException) { _post(() => SetPhase(RuntimeRules.CancelTarget(RuntimeCancel.InstallSelected), "install-cancelled")); }
                catch (Exception ex) { _post(() => Fail(ex.Message, "install-exception")); }
            });
        }

        async Task DownloadAsync(RuntimeHost host, RuntimePack entry, CancellationTokenSource cts)
        {
            _post(() =>
            {
                Received.Value = 0;
                Total.Value = entry.SizeBytes;
                DownloadLabel.Value = Strings.Runtime.Sig.PackLabel(entry.Version, entry.Arch);
                ActiveEntry = entry;
                SetPhase(RuntimePhase.Downloading, "download-entry");
            });
            var verify = await host.Install!(entry, false, p => _post(() => OnProgress(p)), cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            _post(() => ApplyVerify(verify, "download"));
        }

        void OnProgress(RuntimeProgress p)
        {
            if (p.Verifying) { SetPhase(RuntimePhase.Verifying, "download-progress"); return; }
            Received.Value = p.Received;
            if (p.Total > 0) Total.Value = p.Total;
            if (Phase.Peek() != RuntimePhase.Downloading) SetPhase(RuntimePhase.Downloading, "download-progress");
        }

        void ApplyVerify(in RuntimeVerify verify, string reason)
        {
            Runtime.Status.Value = verify.Facts;
            if (verify.NeedsUntrustedConfirmation)
            {
                _untrustedDir = verify.Directory;
                SetPhase(RuntimePhase.Untrusted, reason + "-needs-untrusted");
                return;
            }
            if (verify.Ok) { Succeed(); return; }
            Fail(verify.Error ?? Loc.Get(Strings.Playback.Runtime.DownloadFailed), reason + "-failed");
        }

        // ── Advanced ────────────────────────────────────────────────────────────────────────────────────────────────

        public void ShowAdvanced()
        {
            Log(WaveeLogLevel.Info, "runtime.setup.advanced", "Playback runtime setup advanced view opened");
            Error.Value = null;
            SetPhase(RuntimePhase.Advanced, "advanced");
            EnsureCatalog();
        }

        void EnsureCatalog()
        {
            if (!RuntimeRules.ShouldFetchCatalog(Catalog.Peek(), Runtime.Host?.FetchCatalog is not null)) return;
            var fetch = Runtime.Host!.FetchCatalog!;
            SetCatalog(RuntimeCatalog.Fetching, "ensure-catalog");
            var cts = NewCts();
            _ = Task.Run(async () =>
            {
                try
                {
                    var answer = await fetch(cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;
                    _post(() =>
                    {
                        if (answer is not { } a) { SetCatalog(RuntimeCatalog.Failed, "advanced-catalog-null"); return; }
                        AnyForOtherArch = a.AnyForOtherArch;
                        SupportedPacks = a.Supported;
                        SelectedPack.Value = 0;
                        SetCatalog(RuntimeCatalog.Loaded, "advanced-catalog-loaded");
                    });
                }
                catch (OperationCanceledException)
                {
                    _post(() => { if (Catalog.Peek() == RuntimeCatalog.Fetching) SetCatalog(RuntimeCatalog.NotFetched, "advanced-cancelled"); });
                }
                catch (Exception ex)
                {
                    _post(() =>
                    {
                        SetCatalog(RuntimeCatalog.Failed, "advanced-exception");
                        Log(WaveeLogLevel.Warning, "runtime.setup.catalog.failed", "Advanced catalog fetch failed",
                            WaveeLogField.Of("error", ex.GetType().Name), WaveeLogField.Of("detail", ex.Message));
                    });
                }
            });
        }

        /// <summary>"Choose a Spotify.dll…": a folder picker, falling back to a file picker filtered to the DLL.</summary>
        public void PickFolder()
        {
            nint owner = FluentApp.WindowHandle;
            string title = Loc.Get(Strings.Playback.Runtime.SelectDll);
            string? dir = FilePicker.PickFolder(owner, title);
            if (string.IsNullOrWhiteSpace(dir))
            {
                string? dll = FilePicker.OpenFile(owner, title, ("Spotify DLL", "Spotify.dll"), ("All files", "*.*"));
                if (string.IsNullOrWhiteSpace(dll)) return;
                dir = System.IO.Path.GetDirectoryName(dll);
                if (string.IsNullOrEmpty(dir)) return;
            }
            RegisterDir(dir, allowUntrusted: false);
        }

        /// <summary>"Use installed Spotify": present and clickable on every build; a build with nothing to reuse answers
        /// with the Failed phase and says why (a row that silently does nothing is worse).</summary>
        public void UseInstalled()
        {
            if (Runtime.Host?.InstalledDirectory is not { } installed) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive), "no-installed-seam"); return; }
            if (installed() is not { Length: > 0 } dir) { Fail(Loc.Get(Strings.Playback.Runtime.InstalledNotFound), "installed-not-found"); return; }
            RegisterDir(dir, allowUntrusted: false);
        }

        /// <summary>Synchronous by contract: the Verifying screen flashes for as long as the registration takes.</summary>
        void RegisterDir(string dir, bool allowUntrusted)
        {
            if (Runtime.Host?.RegisterDirectory is not { } register) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive), "no-host"); return; }
            Log(WaveeLogLevel.Info, "runtime.setup.register_dir", "Registering the selected runtime directory",
                WaveeLogField.Of("dir", dir), WaveeLogField.Of("allowUntrusted", allowUntrusted));
            Error.Value = null;
            SetPhase(RuntimePhase.Verifying, "register-dir");
            var verify = register(dir, allowUntrusted);
            if (!verify.Ok && !verify.NeedsUntrustedConfirmation && verify.Error is null)
                verify = verify with { Error = Loc.Get(Strings.Playback.Runtime.NotFound) };
            ApplyVerify(verify with { Directory = verify.Directory ?? dir }, "register");
        }

        // ── Untrusted ───────────────────────────────────────────────────────────────────────────────────────────────

        public void ConfirmUntrusted()
        {
            if (Runtime.Host?.RegisterDirectory is not { } register || _untrustedDir is not { } dir)
            { SetPhase(RuntimeRules.CancelTarget(RuntimeCancel.Untrusted), "confirm-untrusted-missing"); return; }
            Log(WaveeLogLevel.Warning, "runtime.setup.untrusted.confirm", "User confirmed an untrusted runtime",
                WaveeLogField.Of("dir", dir));
            SetPhase(RuntimePhase.Verifying, "confirm-untrusted");
            _ = Task.Run(() =>
            {
                var verify = register(dir, true);
                _post(() => ApplyVerify(verify with { NeedsUntrustedConfirmation = false }, "confirm-untrusted"));
            });
        }

        public void CancelUntrusted() => SetPhase(RuntimeRules.CancelTarget(RuntimeCancel.Untrusted), "cancel-untrusted");

        // ── Ready ───────────────────────────────────────────────────────────────────────────────────────────────────

        public void Remove()
        {
            Log(WaveeLogLevel.Warning, "runtime.setup.remove", "Removing the active runtime pointer");
            Runtime.Host?.ClearActive?.Invoke();
            Platform.Settings.Set(Platform.Keys.PlaybackRuntimeSetupDismissed, false);   // re-enable the banner
            Runtime.BumpBanner();
            Runtime.Status.Value = new RuntimeFacts(false, RuntimeIssue.Missing);
            Close();
        }

        /// <summary>"View diagnostics": leave for the full locate/verify report. Wizard-hosted, this departs the WHOLE
        /// flow (the wizard's exit owns its deferral marker); standalone, it closes the dialog.</summary>
        public void OpenDiagnostics()
        {
            Log(WaveeLogLevel.Info, "runtime.setup.diagnostics", "Playback runtime diagnostics opened from setup",
                WaveeLogField.Of("phase", Phase.Peek().ToString()));
            if (OnWizardExit is { } exit) exit(); else Close();
            Shell.GoTo(new Shell.Route(Shell.RouteKind.PlaybackDiagnostics));
        }

        void AlreadyUpToDate()
        {
            UpToDate.Value = true;
            Log(WaveeLogLevel.Info, "runtime.setup.already_current", "Selected pack matches the installed runtime; skipped reinstall");
            SetPhase(RuntimePhase.Ready, "already-up-to-date");
        }

        void Succeed()
        {
            UpToDate.Value = false;
            SetPhase(RuntimePhase.Ready, "success");
            if (Runtime.Host?.RefreshAfterSuccess is { } refresh)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await refresh().ConfigureAwait(false);
                        Log(WaveeLogLevel.Info, "runtime.setup.refresh_after_success", "Refreshed the runtime after setup success");
                    }
                    catch (Exception ex)
                    {
                        Log(WaveeLogLevel.Warning, "runtime.setup.retry_failed", "Refresh after runtime setup failed",
                            WaveeLogField.Of("error", ex.GetType().Name), WaveeLogField.Of("detail", ex.Message));
                    }
                });
            if (RuntimeRules.ShowsReadyToast(WizardHosted))
                Notify.Say(Loc.Get(Strings.Playback.Runtime.Ready), InfoBarSeverity.Success);
        }

        void Fail(string message, string reason)
        {
            Error.Value = message;
            Log(WaveeLogLevel.Warning, "runtime.setup.failed", "Playback runtime setup failed",
                WaveeLogField.Of("reason", reason), WaveeLogField.Of("detail", message));
            SetPhase(RuntimePhase.Failed, reason);
        }

        void SetPhase(RuntimePhase phase, string reason)
        {
            var prev = Phase.Peek();
            if (prev == phase) return;
            Phase.Value = phase;
            Log(WaveeLogLevel.Debug, "runtime.setup.phase", "Playback runtime setup phase changed",
                WaveeLogField.Of("from", prev.ToString()), WaveeLogField.Of("to", phase.ToString()),
                WaveeLogField.Of("reason", reason));
        }

        void SetCatalog(RuntimeCatalog state, string reason)
        {
            var prev = Catalog.Peek();
            if (prev == state) return;
            Catalog.Value = state;
            Log(WaveeLogLevel.Debug, "runtime.setup.catalog", "Playback runtime catalog state changed",
                WaveeLogField.Of("from", prev.ToString()), WaveeLogField.Of("to", state.ToString()),
                WaveeLogField.Of("reason", reason));
        }

        static void Log(WaveeLogLevel level, string eventId, string message, params ReadOnlySpan<WaveeLogField> fields)
            => Wavee.Log.Event(level, "ui", eventId, message, null, -1, null, fields);
    }

    // ══ 4. THE DIALOG + THE MOUNT POINTS ════════════════════════════════════════════════════════════════════════════

    /// <summary>The standalone setup dialog (the banner's "Set up", the other doors through
    /// <see cref="Runtime.OpenRequest"/>). ContentDialog at rung 2; its built-in buttons are cleared and the Footer owns
    /// the ONE command row, so a phase swap never re-opens the dialog; dismissal is vetoed while busy.</summary>
    public static OverlayHandle OpenRuntimeCard(IOverlayService overlay, Action<Action> post)
    {
        Log.Event(WaveeLogLevel.Info, "ui", "runtime.setup.open", "Playback runtime setup opened", null, -1, null,
            WaveeLogField.Of("issue", Runtime.Status.Peek().Issue.ToString()));
        var model = Runtime.Begin(post);
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Playback.Runtime.Title);
            d.DialogWidth = RuntimeRules.DialogWidth;
            d.PrimaryText = "";                  // PrimaryText defaults to "OK": clear all three or a phantom OK appears
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = RuntimeCard();
            d.Footer = RuntimeCardFooter();
            d.Closing = a => { if (model.IsBusy) a.Cancel = true; };
            d.Closed = _ => Runtime.End(model);
        });
        model.Handle = handle;
        return handle;
    }

    /// <summary>The card BODY over the active model.</summary>
    // MOUNT POINT (stage B contract)
    public static Element RuntimeCard() => Embed.Comp(() => new RuntimeBody());

    /// <summary>The card's ONE command row over the active model.</summary>
    // MOUNT POINT (stage B contract)
    public static Element RuntimeCardFooter() => Embed.Comp(() => new RuntimeFooterView());

    // ══ 5. THE BODY ════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class RuntimeBody : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            if (Runtime.Active.Value is not { } m) return RuntimeText(Loc.Get(Strings.Playback.Runtime.NotActive));
            return m.Phase.Value switch
            {
                RuntimePhase.Offer => RuntimeText(Loc.Get(Strings.Playback.Runtime.OfferBody)),
                RuntimePhase.FetchingCatalog => RuntimeCatalogWaiting(),
                RuntimePhase.Downloading => RuntimeDownloading(m),
                RuntimePhase.Verifying => RuntimeColumn(
                    RuntimeLead(Loc.Get(Strings.Playback.Runtime.Verifying)),
                    RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.VerifyingCaption), RuntimeRules.VerifySize(m.Total.Value)),
                    ProgressBar.Indeterminate(RuntimeRules.ProgressWidth),
                    RuntimeVerifyFacts(m)),
                RuntimePhase.Untrusted => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                    Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody)),
                RuntimePhase.Ready => Ready(m, overlay),
                RuntimePhase.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                    Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack)),
                RuntimePhase.Advanced => Advanced(m),
                _ => new BoxEl(),
            };
        }

        static Element Ready(RuntimeModel m, IOverlayService overlay)
        {
            var facts = m.Status;
            var kids = new List<Element>(4) { RuntimeReadyBadge() };
            if (m.UpToDate.Value) kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.UpToDate)));
            kids.Add(RuntimeReadyFacts(facts, overlay));
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Replace), m.ShowAdvanced),
                    HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Remove), m.Remove),
                ],
            });
            return new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() };
        }

        static Element Advanced(RuntimeModel m)
        {
            var kids = new List<Element>(6) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
            switch (RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
            {
                case RuntimeAdvancedArm.Busy:
                    kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport)));
                    break;
                case RuntimeAdvancedArm.Picker:
                    kids.Add(RuntimeVersionPicker(m, Loc.Get(Strings.Playback.Runtime.ChooseVersion)));
                    break;
                case RuntimeAdvancedArm.NoPack:
                    kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack)));
                    break;
                case RuntimeAdvancedArm.Unreachable:
                    kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable)));
                    break;
                // NotFetched: no arm at all — no busy row, no copy (ported, W32).
            }
            kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0f, 4f, 0f, 4f) });
            kids.AddRange(RuntimeLocalSourceRows(m));
            return new BoxEl { Direction = 1, Gap = Spacing.S, Children = kids.ToArray() };
        }
    }

    // ── the named pieces (the wizard mounts these too) ─────────────────────────────────────────────────────────────

    static Element RuntimeColumn(params Element[] kids) => new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids };

    /// <summary>Body copy: 13, TextSecondary unless overridden, wrapping.</summary>
    public static Element RuntimeText(string text, ColorF? color = null)
        => new TextEl(text) { Size = 13f, LineHeight = 18f, Color = color ?? Tok.TextSecondary, Wrap = TextWrap.Wrap };

    /// <summary>The lead sentence: 16 TextPrimary, wrapping.</summary>
    public static Element RuntimeLead(string text)
        => new TextEl(text) { Size = 16f, LineHeight = 22f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };

    /// <summary>An inline status block — an 18-DIP severity glyph beside a 14/600 heading over wrapped body copy
    /// (clean dialog content, not a box-in-a-box InfoBar). The Failed body may be an arbitrary sentence.</summary>
    public static Element RuntimeStatus(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = glyphColor, Shrink = 0f, Margin = new Edges4(0f, 1f, 0f, 0f) },
            new BoxEl
            {
                Direction = 1, Gap = 4f, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(heading) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    new TextEl(body) { Size = 13f, LineHeight = 18f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    static Element RuntimeBusyRow(string message) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 48f,
        Children = [ProgressRing.Indeterminate(), RuntimeText(message, Tok.TextPrimary)],
    };

    /// <summary>A label (13/600, one line) beside its value (12 TextSecondary, never shrinks).</summary>
    static Element RuntimeMetricRow(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label)
            {
                Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary,
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            new TextEl(value) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Shrink = 0f },
        ],
    };

    /// <summary>FetchingCatalog: one calm status line, the process architecture, the real indeterminate bar.</summary>
    public static Element RuntimeCatalogWaiting(float barWidth = RuntimeRules.ProgressWidth) => RuntimeColumn(
        RuntimeLead(Loc.Get(Strings.Playback.Runtime.CheckingSupport)),
        RuntimeMetricRow(Loc.Get(Strings.Playback.Runtime.ReachingCatalog),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
        ProgressBar.Indeterminate(barWidth));

    /// <summary>Downloading: the label, "12.3 / 84.0 MB", a determinate bar (indeterminate while the total is unknown)
    /// and the "you can't leave this step" caption.</summary>
    public static Element RuntimeDownloading(RuntimeModel m, float barWidth = RuntimeRules.ProgressWidth)
    {
        long received = m.Received.Value, total = m.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(RuntimeRules.ProgressFraction(received, total), barWidth)
            : ProgressBar.Indeterminate(barWidth);
        return RuntimeColumn(
            RuntimeLead(Loc.Get(Strings.Playback.Runtime.Downloading)),
            RuntimeMetricRow(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                RuntimeRules.DownloadBytes(received, total)),
            bar,
            new TextEl(Loc.Get(Strings.Playback.Runtime.DownloadBlocking))
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
            });
    }

    static BoxEl RuntimeFactBox(float pad, float gap, params Element[] rows) => new()
    {
        Direction = 1, Gap = gap, Padding = Edges4.All(pad),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = rows,
    };

    /// <summary>A fact row: a 12 TextSecondary label in a fixed lane, a 12 TextPrimary one-line value ("—" when unknown).</summary>
    public static Element RuntimeFactRow(string label, string? value, float labelWidth = RuntimeRules.DetailLabelWidth) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            new TextEl(RuntimeRules.OrDash(value))
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The Verifying (and wizard Untrusted) fact box: Version · Architecture · SHA-256 (4 + "…" + 4).</summary>
    public static Element RuntimeVerifyFacts(RuntimeModel m)
    {
        var entry = m.ActiveEntry;
        return RuntimeFactBox(Spacing.S, Spacing.XS,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), entry?.Version),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch),
                entry?.Arch ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailSha256), entry is null ? null : RuntimeRules.ShortHash(entry.Sha256)));
    }

    /// <summary>"✓ Local playback is ready".</summary>
    public static Element RuntimeReadyBadge() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            InfoBadge.Icon(InfoBadgeSeverity.Success),
            new TextEl(Loc.Get(Strings.Playback.Runtime.Ready)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
        ],
    };

    /// <summary>The Ready fact box: Version · Architecture · Signature (+ the [Signature] button when details exist) ·
    /// Location.</summary>
    public static Element RuntimeReadyFacts(RuntimeFacts facts, IOverlayService overlay,
                                            float labelWidth = RuntimeRules.DetailLabelWidth)
    {
        var sigKids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Playback.Runtime.DetailSignature))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            new TextEl(RuntimeSignatureSummary(facts))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
        };
        if (facts.Signature is not null)
            sigKids.Add(Button.Standard(Loc.Get(Strings.Playback.Runtime.DetailSignature), () => Shell.OpenSignatureDialog(overlay, facts)) with
            {
                MinWidth = 86f, Height = 28f, MinHeight = 28f, Justify = FlexJustify.Center,
            });
        return RuntimeFactBox(12f, 6f,
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), facts.Version, labelWidth),
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch, labelWidth),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = sigKids.ToArray() },
            RuntimeFactRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location, labelWidth));
    }

    /// <summary>The signature summary sentence (the ladder is <see cref="RuntimeRules.SignatureLine"/>; the wording is
    /// localized — 0.2.9 hard-coded it, ch 19 §6.10).</summary>
    public static string RuntimeSignatureSummary(RuntimeFacts facts)
    {
        string subject = facts.Signature?.Subject ?? "";
        return RuntimeRules.SignatureLine(facts) switch
        {
            RuntimeSignatureLine.SignedTrusted => Strings.Runtime.Sig.SignedBy(subject),
            RuntimeSignatureLine.SignedPinned => Strings.Runtime.Sig.SignedByPinned(subject),
            RuntimeSignatureLine.SignedOther => Strings.Runtime.Sig.SignedByTrust(subject,
                Loc.Get(RuntimeRules.TrustLocKey(facts.Signature?.Trust ?? RuntimeTrust.Unknown))),
            RuntimeSignatureLine.PinnedFingerprint => Loc.Get(Strings.Runtime.Sig.TrustedByFingerprint),
            RuntimeSignatureLine.VerifiedFingerprint => Loc.Get(Strings.Runtime.Sig.VerifiedFingerprint),
            RuntimeSignatureLine.NotTrustedOverride => Loc.Get(Strings.Runtime.Sig.NotTrustedOverride),
            RuntimeSignatureLine.CheckUnavailable => Loc.Get(Strings.Runtime.Sig.CheckUnavailable),
            _ => Loc.Get(Strings.Runtime.Sig.SignatureUnknown),
        };
    }

    /// <summary>The Advanced version list: "Spotify {v} · {arch}", the first carrying "  (Recommended)" (two spaces).</summary>
    public static Element RuntimeVersionPicker(RuntimeModel m, string? header)
    {
        var packs = m.SupportedPacks;
        var labels = new string[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            labels[i] = Strings.Runtime.Sig.PackLabel(packs[i].Version, packs[i].Arch);
            if (i == 0) labels[i] += "  (" + Loc.Get(Strings.Playback.Runtime.Recommended) + ")";
        }
        return RadioButtons.Create(labels, m.SelectedPack, header: header);
    }

    /// <summary>The two offline sources under Advanced: "Choose a Spotify.dll…" and "Use installed Spotify".</summary>
    public static Element[] RuntimeLocalSourceRows(RuntimeModel m) =>
    [
        RuntimeSettingRow(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder),
            Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption), m.PickFolder),
        RuntimeSettingRow(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled),
            Loc.Get(Strings.Playback.Runtime.UseInstalledCaption), m.UseInstalled),
    ];

    static Element RuntimeSettingRow(string glyph, string title, string caption, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 13f, Padding = new Edges4(12f, 9f, 12f, 9f),
        Corners = CornerRadius4.All(Radii.Control), Role = AutomationRole.Button, Focusable = true,
        FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
        Children =
        [
            new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = 1f,
                Children =
                [
                    new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl(caption) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
            new TextEl(Icons.ChevronRightMed) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    // ══ 6. THE COMMAND ROW ═════════════════════════════════════════════════════════════════════════════════════════
    //
    // (The nested digital-signature sheet the Ready view's [Signature] button opens is `Shell.OpenSignatureDialog` in
    // `+Shell.Overlays.UI.cs` — plan §2 files the dialog there, beside the banner that leads to it.)

    sealed class RuntimeFooterView : Component
    {
        const float BtnMinW = 96f, BtnH = 32f;

        public override Element Render()
        {
            if (Runtime.Active.Value is not { } m) return Row(null, Btn(Loc.Get(Strings.Common.Dismiss), () => { }, false));
            var footer = RuntimeRules.FooterFor(m.Phase.Value, m.Catalog.Value, m.SupportedPacks.Count,
                Shell.PageFor(new Shell.Route(Shell.RouteKind.PlaybackDiagnostics)) is not null);

            Element? left = footer.Link == RuntimeVerb.None ? null
                : footer.Link2 == RuntimeVerb.None ? Link(m, footer.Link, flush: true)
                : new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    // Only the FIRST link takes the flush-left margin; the rest keep their own 11-DIP padding.
                    Children = [Link(m, footer.Link, flush: true), Link(m, footer.Link2, flush: false)],
                };
            var right = new List<Element>(2);
            if (footer.Standard != RuntimeVerb.None) right.Add(Btn(Label(footer.Standard), Verb(m, footer.Standard), footer.StandardEnabled));
            if (footer.Accent != RuntimeVerb.None) right.Add(AccentBtn(Label(footer.Accent), Verb(m, footer.Accent), footer.AccentEnabled));
            return Row(left, right.ToArray());
        }

        static string Label(RuntimeVerb verb) => Loc.Get(verb switch
        {
            RuntimeVerb.Advanced => Strings.Playback.Runtime.Advanced,
            RuntimeVerb.ViewDiagnostics => Strings.Playback.Runtime.ViewDiagnostics,
            RuntimeVerb.NotNow => Strings.Playback.Runtime.NotNow,
            RuntimeVerb.DownloadSetup => Strings.Playback.Runtime.DownloadSetup,
            RuntimeVerb.Cancel or RuntimeVerb.CancelUntrusted => Strings.Auth.Cancel,
            RuntimeVerb.LoadAnyway => Strings.Playback.Runtime.LoadAnyway,
            RuntimeVerb.CheckUpdate => Strings.Playback.Runtime.CheckUpdate,
            RuntimeVerb.Done => Strings.Playback.Runtime.Done,
            RuntimeVerb.TryAgain => Strings.Playback.Runtime.TryAgain,
            RuntimeVerb.Back => Strings.Playback.Runtime.Back,
            RuntimeVerb.Install => Strings.Playback.Runtime.Install,
            _ => Strings.Common.Dismiss,
        });

        static Action Verb(RuntimeModel m, RuntimeVerb verb) => verb switch
        {
            RuntimeVerb.Advanced => m.ShowAdvanced,
            RuntimeVerb.ViewDiagnostics => m.OpenDiagnostics,
            RuntimeVerb.NotNow => m.NotNow,
            RuntimeVerb.DownloadSetup or RuntimeVerb.CheckUpdate => m.StartDownload,
            RuntimeVerb.TryAgain => m.Retry,
            RuntimeVerb.Cancel => m.Cancel,
            RuntimeVerb.CancelUntrusted => m.CancelUntrusted,
            RuntimeVerb.LoadAnyway => m.ConfirmUntrusted,
            RuntimeVerb.Back => m.Back,
            RuntimeVerb.Install => m.InstallSelected,
            _ => m.Close,
        };

        static Element Link(RuntimeModel m, RuntimeVerb verb, bool flush)
        {
            var link = HyperlinkButton.Create(Label(verb), Verb(m, verb));
            // The hyperlink carries its own 11-DIP inner padding; −11 puts the first link's TEXT flush with the copy.
            return flush ? link with { Margin = new Edges4(-11f, 0f, 0f, 0f) } : link;
        }

        static Element Btn(string text, Action onClick, bool enabled) =>
            Button.Standard(text, onClick, isEnabled: enabled) with
            { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center };

        static Element AccentBtn(string text, Action onClick, bool enabled) =>
            Button.Accent(text, onClick, isEnabled: enabled) with
            { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1 };

        static Element Row(Element? left, params Element[] right)
        {
            var kids = new List<Element>(right.Length + 2);
            if (left is not null) kids.Add(left);
            kids.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
            kids.AddRange(right);
            return new BoxEl { Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
        }
    }
}
