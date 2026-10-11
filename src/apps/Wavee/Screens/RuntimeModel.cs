// ── Screens/RuntimeModel.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the runtime provisioning card's MODEL: the host seam the shell fills, the process-wide cells the banner gate reads, and
// the state holder with every provisioning verb (catalog, download, verify, untrusted confirm, advanced sources, remove)
//
// Role: CORE (engine-free: no element, component or overlay type; signals, tasks, settings writes and the log)
// Spec: docs/themes/plan/technical-plan.md §5.1 C14 (X33)
//
// Moved out of `Setup.UI.Runtime.cs` unchanged (sections 1-3 of that file) apart from one seam: the model no longer holds
// an `OverlayHandle`; the standalone dialog hands it `CloseHost` (the handle's Close) instead. The card BODY, the named
// pieces and the command row stay in `Setup.UI.Runtime.cs`; the wizard's Local-playback page is driven by
// `WizardModel` (`Screens/WizardModel.cs`), which owns one of these.

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;

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
        /// <summary>How the standalone dialog closes itself (the dialog handle's Close; null while wizard-hosted, where
        /// <see cref="OnClose"/> answers). Set by <c>OpenRuntimeCard</c>.</summary>
        internal Action? CloseHost;

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

        public void Close() { if (OnClose is { } c) c(); else CloseHost?.Invoke(); }

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

        /// <summary>"Choose a Spotify.dll…": a folder picker, falling back to a file picker filtered to the DLL.
        /// Both dialogs run on their own thread (Pickers, #155) and answer on the UI thread, one after the other.</summary>
        public void PickFolder()
        {
            string title = Loc.Get(Strings.Playback.Runtime.SelectDll);
            Pickers.Pick(PickerRequest.Folder(title),
                done: dir =>
                {
                    if (!string.IsNullOrWhiteSpace(dir)) { RegisterDir(dir, allowUntrusted: false); return; }
                    Pickers.Pick(PickerRequest.Open(title, ("Spotify DLL", "Spotify.dll"), ("All files", "*.*")),
                        done: dll =>
                        {
                            if (string.IsNullOrWhiteSpace(dll)) return;
                            string? folder = System.IO.Path.GetDirectoryName(dll);
                            if (string.IsNullOrEmpty(folder)) return;
                            RegisterDir(folder, allowUntrusted: false);
                        },
                        failed: PickerFailed);
                },
                failed: PickerFailed);
        }

        void PickerFailed(Exception ex)
            => Log(WaveeLogLevel.Warning, "runtime.setup.picker.failed", "The runtime folder/file picker failed",
                WaveeLogField.Of("error", ex.GetType().Name), WaveeLogField.Of("detail", ex.Message));

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
}
