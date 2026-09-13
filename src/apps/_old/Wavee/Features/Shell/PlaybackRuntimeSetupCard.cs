using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Audio.Runtime;

namespace Wavee;

/// <summary>The local-playback setup modal on the WinUI-parity <see cref="ContentDialog"/>: the dialog owns the chrome /
/// focus-trap / motion; a reactive body + a reactive <see cref="ContentDialog.Footer"/> drive the phased state machine
/// (Offer → FetchingCatalog → Downloading → Verifying → Untrusted → Ready → Failed → Advanced). Exactly ONE command row,
/// always — its actions change with the phase. Network download is the primary path; folder-pick / installed-Spotify live
/// under Advanced as explained setting-rows.</summary>
static class PlaybackRuntimeSetupCard
{
    /// <summary>Open the setup modal. <paramref name="post"/> marshals background download progress + terminal state
    /// onto the UI thread (obtain from the caller's <c>UsePost()</c>).</summary>
    public static OverlayHandle Open(IOverlayService overlay, Action<Action> post, Services services, IAppSettings settings,
        PlaybackBridge bridge)
    {
        services.Log.Event(WaveeLogLevel.Info, "ui", "runtime.setup.open", "Playback runtime setup opened",
            fields: [WaveeLogField.Of("status", bridge.RuntimeStatus.Peek().Outcome.ToString())]);
        // The live-session seam is installed after authentication. Resolve it when an operation starts instead of
        // freezing today's value into the model: the first-run wizard itself is deliberately created pre-auth.
        var model = new PlaybackRuntimeSetupModel(services, settings, bridge, () => services.PlayPlayProvisioner, post);
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Playback.Runtime.Title);
            d.DialogWidth = 460f;
            // The footer owns ALL buttons — no built-in command buttons (PrimaryText defaults to "OK"; clear it).
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Embed.Comp(() => new SetupBody(model));
            d.Footer = Embed.Comp(() => new SetupFooter(model));
            // Block dismissal (Escape / programmatic) while a download/verify is in flight.
            d.Closing = a => { if (model.IsBusy) a.Cancel = true; };
            d.Closed = _ => model.Dispose();
        });
        model.Bind(handle);
        return handle;
    }
}

/// <summary>Owns all setup state + the async provisioning work; shared by the body and the footer. Background tasks
/// marshal every signal write through <c>post</c> so the UI thread is the only writer.</summary>
sealed partial class PlaybackRuntimeSetupModel
{
    public enum CatalogState { NotFetched, Fetching, Loaded, Failed }

    public readonly Signal<Phase> PhaseSig;
    public readonly Signal<CatalogState> CatalogSig = new(CatalogState.NotFetched);
    public readonly Signal<string?> Error = new(null);
    public readonly Signal<long> Received = new(0);
    public readonly Signal<long> Total = new(0);            // 0 = indeterminate
    public readonly Signal<string?> DownloadLabel = new(null);   // "Spotify 1.2.93.667 · Arm64" during download
    public PlayPlayRuntimeCatalogEntry? ActiveEntry { get; private set; }
    public readonly Signal<int> SelectedPackIndex = new(0);
    /// <summary>True while the Ready phase is showing because a check/install found the ALREADY-active pack rather
    /// than a fresh success — distinct wording, and distinct from a genuine install (no post-success runtime refresh).</summary>
    public readonly Signal<bool> UpToDate = new(false);
    public IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks = [];
    public bool AnyForOtherArch;

    readonly Services _services;
    readonly IAppSettings _settings;
    readonly PlaybackBridge _bridge;
    readonly Func<IPlayPlayProvisioner?> _getProvisioner;
    readonly Action<Action> _post;

    CancellationTokenSource? _cts;
    OverlayHandle? _handle;
    string? _untrustedDir;

    public PlaybackRuntimeSetupModel(Services services, IAppSettings settings, PlaybackBridge bridge,
        Func<IPlayPlayProvisioner?> getProvisioner, Action<Action> post)
    {
        _services = services;
        _settings = settings;
        _bridge = bridge;
        _getProvisioner = getProvisioner;
        _post = post;
        PhaseSig = new(bridge.RuntimeStatus.Value.IsReady ? Phase.Ready : Phase.Offer);
        Log(WaveeLogLevel.Debug, "runtime.setup.model", "Playback runtime setup model created",
            WaveeLogField.Of("initialPhase", PhaseSig.Peek().ToString()),
            WaveeLogField.Of("status", bridge.RuntimeStatus.Peek().Outcome.ToString()),
            WaveeLogField.Of("hasProvisioner", getProvisioner() is not null));
    }

    public PlaybackRuntimeStatus Status => _bridge.RuntimeStatus.Value;
    public bool IsBusy => PhaseSig.Value is Phase.FetchingCatalog or Phase.Downloading or Phase.Verifying;

    public void Bind(OverlayHandle handle) => _handle = handle;
    public void Dispose() => _cts?.Cancel();

    /// <summary>Set by the wizard (<c>SetupSession.EnsureRuntime</c>): inside the wizard, "close this page" means
    /// advance to the next page, not tear down an overlay this model never opened. Null outside the wizard, where
    /// <see cref="Close"/> falls back to the standalone dialog's own <see cref="_handle"/>.</summary>
    public Action? OnClose { get; set; }

    /// <summary>Set by the wizard: the escape hatch to close the ENTIRE wizard (not just this page) — wired to
    /// <c>SetupSession.RequestClose</c>. Null outside the wizard, where <see cref="OpenDiagnostics"/>'s plain
    /// <see cref="Close"/> already tears down the standalone dialog via its own handle.</summary>
    public Action? OnWizardExit { get; set; }

    public void Close() { if (OnClose is { } c) c(); else _handle?.Close(); }

    /// <summary>The two settings writes + the banner bump — "stop offering, stay remote-only" without closing
    /// anything. Split out of <see cref="NotNow"/> so the wizard's own "Not now" can dismiss the setting and then
    /// ADVANCE (the user stays remote-only but setup continues) instead of tearing down the whole flow.</summary>
    public void DismissSetting()
    {
        Log(WaveeLogLevel.Info, "runtime.setup.dismiss", "Playback runtime setup dismissed");
        _settings.Set(WaveeSettings.PlaybackRuntimeSetupDismissed, true);
        PlaybackRuntimeBannerState.Bump();
    }

    /// <summary>"Not now": stop offering (banner included) and close — the user stays remote-only until they ask.</summary>
    public void NotNow() { DismissSetting(); Close(); }

    public void Back()
    {
        UpToDate.Value = false;
        SetPhase(Status.IsReady ? Phase.Ready : Phase.Offer, "back");
    }

    CancellationTokenSource NewCts()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    // ── Primary network path ──────────────────────────────────────────────────────────────────────

    public void StartDownload()
    {
        var provisioner = _getProvisioner();
        if (provisioner is null) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive)); return; }
        Log(WaveeLogLevel.Info, "runtime.setup.download.start", "Runtime setup download flow started");
        Error.Value = null;
        SetPhase(Phase.FetchingCatalog, "start-download");
        SetCatalog(CatalogState.Fetching, "start-download");
        var cts = NewCts();
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await provisioner.FetchCatalogAsync(cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                if (catalog is null)
                {
                    _post(() => SetCatalog(CatalogState.Failed, "catalog-null"));
                    PostFail(provisioner, ProvisioningOutcome.PackDownloadFailed);
                    return;
                }
                var (best, anyOther) = provisioner.SelectBest(catalog);
                var supported = provisioner.SupportedPacks(catalog);
                _post(() =>
                {
                    AnyForOtherArch = anyOther;
                    SupportedPacks = supported;
                    SetCatalog(CatalogState.Loaded, "download-catalog-loaded");
                });
                if (best is null)
                {
                    PostFail(provisioner, anyOther ? ProvisioningOutcome.ArchUnsupported : ProvisioningOutcome.NoSupportedPack);
                    return;
                }
                // The newest catalog entry is often the one already installed (no genuinely newer pack exists yet).
                // Downloading it anyway would try to overwrite the runtime DLL this very process has loaded, which
                // Windows refuses — a self-inflicted, always-reproducible "download failed". Skip it.
                if (Status.IsReady && string.Equals(Status.PackId, best.PackId, StringComparison.Ordinal))
                {
                    _post(AlreadyUpToDate);
                    return;
                }
                await DownloadEntryAsync(provisioner, best, cts, allowUntrusted: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _post(() =>
                {
                    if (CatalogSig.Value == CatalogState.Fetching) SetCatalog(CatalogState.NotFetched, "download-cancelled");
                    SetPhase(Phase.Offer, "download-cancelled");
                });
            }
            catch (Exception ex) { _post(() => Fail(ex.Message)); }
        });
    }

    public void InstallSelected()
    {
        var provisioner = _getProvisioner();
        if (provisioner is null) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive)); return; }
        int idx = SelectedPackIndex.Value;
        if (idx < 0 || idx >= SupportedPacks.Count) return;
        var entry = SupportedPacks[idx];
        Log(WaveeLogLevel.Info, "runtime.setup.install_selected", "Installing selected PlayPlay runtime pack",
            WaveeLogField.Of("index", idx),
            WaveeLogField.Of("pack", entry.PackId),
            WaveeLogField.Of("version", entry.SpotifyVersion),
            WaveeLogField.Of("arch", entry.Arch));
        // Same self-overwrite hazard as StartDownload: the explicitly picked pack can still be the one
        // already active (e.g. re-confirming from the version list), and reinstalling it in place fails.
        if (Status.IsReady && string.Equals(Status.PackId, entry.PackId, StringComparison.Ordinal))
        {
            AlreadyUpToDate();
            return;
        }
        var cts = NewCts();
        _ = Task.Run(async () =>
        {
            try { await DownloadEntryAsync(provisioner, entry, cts, allowUntrusted: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { _post(() => SetPhase(Phase.Advanced, "install-cancelled")); }
            catch (Exception ex) { _post(() => Fail(ex.Message)); }
        });
    }

    async Task DownloadEntryAsync(IPlayPlayProvisioner provisioner, PlayPlayRuntimeCatalogEntry entry,
        CancellationTokenSource cts, bool allowUntrusted)
    {
        _post(() =>
        {
            Received.Value = 0;
            Total.Value = entry.DownloadSize ?? 0;
            DownloadLabel.Value = $"Spotify {entry.SpotifyVersion} · {entry.Arch}";
            ActiveEntry = entry;
            SetPhase(Phase.Downloading, "download-entry");
        });
        var progress = new CallbackProgress<PlayPlayDownloadProgress>(p => _post(() =>
        {
            if (p.Stage == PlayPlayDownloadStage.Verifying) { SetPhase(Phase.Verifying, "download-progress"); return; }
            Received.Value = p.BytesReceived;
            if (p.TotalBytes is { } t) Total.Value = t;
            if (PhaseSig.Value != Phase.Downloading) SetPhase(Phase.Downloading, "download-progress");
        }));
        var verify = await provisioner.DownloadAndInstallAsync(entry, allowUntrusted, progress, cts.Token).ConfigureAwait(false);
        if (cts.IsCancellationRequested) return;
        _post(() => ApplyVerify(provisioner, verify));
    }

    void ApplyVerify(IPlayPlayProvisioner provisioner, PlayPlayRuntimeVerifyResult verify)
    {
        _bridge.UpdateRuntimeStatus(provisioner.GetSnapshot());
        if (verify.NeedsUntrustedConfirmation)
        {
            _untrustedDir = verify.RuntimeDir;
            SetPhase(Phase.Untrusted, "verify-needs-untrusted");
            return;
        }
        if (verify.Ok) { Succeed(); return; }
        Error.Value = AudioFailureText.ToUserMessage(verify.Outcome, verify.Detail);
        SetPhase(Phase.Failed, "verify-failed");
    }

    public void Cancel() => _cts?.Cancel();

    public void Retry() => StartDownload();

    public void ShowAdvanced()
    {
        Log(WaveeLogLevel.Info, "runtime.setup.advanced", "Playback runtime setup advanced view opened");
        Error.Value = null;
        SetPhase(Phase.Advanced, "advanced");
        EnsureCatalog();
    }

    /// <summary>Fetch the catalog for the Advanced version picker (idempotent; re-tries after a failure).</summary>
    void EnsureCatalog()
    {
        var provisioner = _getProvisioner();
        if (provisioner is null) return;
        if (CatalogSig.Value is CatalogState.Fetching or CatalogState.Loaded) return;
        SetCatalog(CatalogState.Fetching, "ensure-catalog");
        var cts = NewCts();
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await provisioner.FetchCatalogAsync(cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                if (catalog is null) { _post(() => SetCatalog(CatalogState.Failed, "advanced-catalog-null")); return; }
                var (_, anyOther) = provisioner.SelectBest(catalog);
                var supported = provisioner.SupportedPacks(catalog);
                _post(() =>
                {
                    AnyForOtherArch = anyOther;
                    SupportedPacks = supported;
                    SelectedPackIndex.Value = 0;
                    SetCatalog(CatalogState.Loaded, "advanced-catalog-loaded");
                });
            }
            catch (OperationCanceledException)
            {
                _post(() => { if (CatalogSig.Value == CatalogState.Fetching) SetCatalog(CatalogState.NotFetched, "advanced-cancelled"); });
            }
            catch (Exception ex)
            {
                _post(() =>
                {
                    SetCatalog(CatalogState.Failed, "advanced-exception");
                    Log(WaveeLogLevel.Warning, "runtime.setup.catalog.failed", "Advanced catalog fetch failed",
                        WaveeLogField.Of("error", ex.GetType().Name),
                        WaveeLogField.Of("detail", ex.Message));
                });
            }
        });
    }

    public void CheckForUpdate() => StartDownload();

    // ── Untrusted confirmation (file already installed; re-verify allowing the untrusted signature) ─

    public void ConfirmUntrusted()
    {
        var provisioner = _getProvisioner();
        if (provisioner is null || _untrustedDir is null) { SetPhase(Phase.Offer, "confirm-untrusted-missing"); return; }
        var dir = _untrustedDir;
        Log(WaveeLogLevel.Warning, "runtime.setup.untrusted.confirm", "User confirmed untrusted PlayPlay runtime",
            WaveeLogField.Of("dir", dir));
        SetPhase(Phase.Verifying, "confirm-untrusted");
        _ = Task.Run(() =>
        {
            bool ok = provisioner.TryRegisterRuntime(dir, allowUntrustedSignature: true);
            var snap = provisioner.GetSnapshot();
            _post(() =>
            {
                _bridge.UpdateRuntimeStatus(snap);
                if (ok) Succeed();
                else { Error.Value = AudioFailureText.ToUserMessage(snap.Outcome, snap.Detail); SetPhase(Phase.Failed, "confirm-untrusted-failed"); }
            });
        });
    }

    public void CancelUntrusted() => SetPhase(Phase.Offer, "cancel-untrusted");

    /// <summary>"View diagnostics": close the modal and land on the full locate/verify report. Reachable in one click
    /// from the Failed phase, which is exactly where someone is looking at a sentence that doesn't explain itself.
    /// Inside the wizard (<see cref="OnWizardExit"/> set) this is a deliberate departure from the WHOLE flow, not
    /// just this page: burn the wizard's own deferred marker (exactly like Welcome's "Not now") before leaving,
    /// since a route change means the wizard is not coming back on its own.</summary>
    public void OpenDiagnostics(Action<string, string?> go)
    {
        Log(WaveeLogLevel.Info, "runtime.setup.diagnostics", "Playback runtime diagnostics opened from setup",
            WaveeLogField.Of("phase", PhaseSig.Peek().ToString()));
        if (OnWizardExit is { } exitWizard)
        {
            SetupGating.MarkDeferred(_settings);
            exitWizard();
        }
        else Close();
        go(PlaybackRuntimeDiagnosticsPage.Route, null);
    }

    // ── Advanced: folder pick / installed Spotify (offline fallbacks) ────────────────────────────

    public void PickFolder()
    {
        nint owner = FluentApp.WindowHandle;
        var dir = FilePicker.PickFolder(owner, Loc.Get(Strings.Playback.Runtime.SelectDll));
        if (string.IsNullOrWhiteSpace(dir))
        {
            var dll = FilePicker.OpenFile(owner, Loc.Get(Strings.Playback.Runtime.SelectDll),
                [("Spotify DLL", "Spotify.dll"), ("All files", "*.*")]);
            if (string.IsNullOrWhiteSpace(dll)) return;
            dir = Path.GetDirectoryName(dll)!;
        }
        RegisterDir(dir, allowUntrusted: false);
    }

    public void UseInstalled()
    {
#if WAVEE_PLAYPLAY_LOCAL
        var dll = PlayPlayRuntimePaths.InstalledSpotifyDll;
        if (!File.Exists(dll)) { Fail(Loc.Get(Strings.Playback.Runtime.InstalledNotFound)); return; }
        // No sibling manifest required — the store recognizes a supported build by the DLL's hash and synthesizes it.
        RegisterDir(Path.GetDirectoryName(dll)!, allowUntrusted: false);
#else
        Fail(Loc.Get(Strings.Playback.Runtime.NotActive));
#endif
    }

    void RegisterDir(string dir, bool allowUntrusted)
    {
        var provisioner = _getProvisioner();
        if (provisioner is null) { Fail(Loc.Get(Strings.Playback.Runtime.NotActive)); return; }
        Log(WaveeLogLevel.Info, "runtime.setup.register_dir", "Registering selected PlayPlay runtime directory",
            WaveeLogField.Of("dir", dir),
            WaveeLogField.Of("allowUntrusted", allowUntrusted));
        Error.Value = null;
        SetPhase(Phase.Verifying, "register-dir");
        bool ok = provisioner.TryRegisterRuntime(dir, allowUntrusted);
        var snap = provisioner.GetSnapshot();
        _bridge.UpdateRuntimeStatus(snap);
        if (!ok && snap.NeedsUntrustedConfirmation) { _untrustedDir = dir; SetPhase(Phase.Untrusted, "register-needs-untrusted"); return; }
        if (!ok) { Error.Value = AudioFailureText.ToUserMessage(snap.Outcome, snap.Detail); SetPhase(Phase.Failed, "register-failed"); return; }
        Succeed();
    }

    public void Remove()
    {
        Log(WaveeLogLevel.Warning, "runtime.setup.remove", "Removing active PlayPlay runtime pointer");
        _getProvisioner()?.ClearActivePointer();
        _settings.Set(WaveeSettings.PlaybackRuntimeSetupDismissed, false);
        PlaybackRuntimeBannerState.Bump();
        _bridge.UpdateRuntimeStatus(new PlaybackRuntimeStatus(ProvisioningOutcome.RuntimeUnavailable));
        Close();
    }

    public void SignatureDetailsOpened(DigitalSignatureInfo signature) =>
        Log(WaveeLogLevel.Debug, "runtime.setup.signature_details", "Playback runtime signature details opened",
            WaveeLogField.Of("subject", signature.Subject),
            WaveeLogField.Of("issuer", signature.Issuer),
            WaveeLogField.Of("trust", signature.Trust.ToString()));

    /// <summary>The selected/best catalog pack matches what's already installed and verified — skip the redundant
    /// download+reinstall (which would try to overwrite the currently loaded runtime DLL and fail) and just confirm.
    /// Deliberately NOT <see cref="Succeed"/>: nothing was reinstalled, so there is nothing to refresh/resume.</summary>
    void AlreadyUpToDate()
    {
        UpToDate.Value = true;
        Log(WaveeLogLevel.Info, "runtime.setup.already_current", "Selected pack matches the installed runtime; skipped reinstall");
        SetPhase(Phase.Ready, "already-up-to-date");
    }

    void Succeed()
    {
        UpToDate.Value = false;
        SetPhase(Phase.Ready, "success");
        bool hadPlaybackError = _bridge.Error.Peek() is { Length: > 0 };
        _ = Task.Run(async () =>
        {
            bool refreshed = _services.LiveHost?.Connect.Audio is { } audio &&
                await audio.TryRefreshPlayPlayRuntimeAsync().ConfigureAwait(false);
            Log(WaveeLogLevel.Info, "runtime.setup.refresh_after_success", "Refreshed PlayPlay runtime after setup success",
                WaveeLogField.Of("refreshed", refreshed),
                WaveeLogField.Of("hadPlaybackError", hadPlaybackError));
            if (refreshed && hadPlaybackError && _services.LiveHost?.Connect.Controller is { } controller)
            {
                try
                {
                    Log(WaveeLogLevel.Info, "runtime.setup.retry_current", "Retrying current track after runtime setup");
                    await controller.RetryCurrentAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log(WaveeLogLevel.Warning, "runtime.setup.retry_failed", "Retry after runtime setup failed",
                        WaveeLogField.Of("error", ex.GetType().Name),
                        WaveeLogField.Of("detail", ex.Message));
                }
            }
        });
        // Skip the toast when this model is wizard-hosted (OnWizardExit set): the wizard's own LocalPlayback page
        // already shows the Ready state in place, so a toast on top of it would repeat the news the user is already
        // looking at (SetupGating's onboarding-suppression rationale, pinned pure via ShowsReadyToast).
        if (PlaybackRuntimeSetupModel.ShowsReadyToast(OnWizardExit is not null))
            Toast.Show(Loc.Get(Strings.Playback.Runtime.Ready), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

    void Fail(string message)
    {
        Error.Value = message;
        Log(WaveeLogLevel.Warning, "runtime.setup.failed", "Playback runtime setup failed",
            WaveeLogField.Of("detail", message));
        SetPhase(Phase.Failed, "fail");
    }

    void PostFail(IPlayPlayProvisioner provisioner, ProvisioningOutcome outcome) => _post(() =>
    {
        var snap = provisioner.GetSnapshot();
        _bridge.UpdateRuntimeStatus(snap);
        Error.Value = AudioFailureText.ToUserMessage(outcome, snap.Detail);
        Log(WaveeLogLevel.Warning, "runtime.setup.failed_outcome", "Playback runtime setup failed",
            WaveeLogField.Of("outcome", outcome.ToString()),
            WaveeLogField.Of("detail", Error.Value ?? ""));
        SetPhase(Phase.Failed, "post-fail");
    });

    void SetPhase(Phase phase, string reason)
    {
        var prev = PhaseSig.Peek();
        if (prev == phase) return;
        PhaseSig.Value = phase;
        Log(WaveeLogLevel.Debug, "runtime.setup.phase", "Playback runtime setup phase changed",
            WaveeLogField.Of("from", prev.ToString()),
            WaveeLogField.Of("to", phase.ToString()),
            WaveeLogField.Of("reason", reason));
    }

    void SetCatalog(CatalogState state, string reason)
    {
        var prev = CatalogSig.Peek();
        if (prev == state) return;
        CatalogSig.Value = state;
        Log(WaveeLogLevel.Debug, "runtime.setup.catalog", "Playback runtime catalog state changed",
            WaveeLogField.Of("from", prev.ToString()),
            WaveeLogField.Of("to", state.ToString()),
            WaveeLogField.Of("reason", reason));
    }

    void Log(WaveeLogLevel level, string eventId, string message, params WaveeLogField[] fields) =>
        _services.Log.Event(level, "ui", eventId, message, fields: fields);

    sealed class CallbackProgress<T>(Action<T> cb) : IProgress<T>
    {
        public void Report(T value) => cb(value);
    }
}

/// <summary>The dialog body — message + at most one visual (progress / InfoBar / picker) per phase. Buttons live ONLY in
/// <see cref="SetupFooter"/>.</summary>
sealed class SetupBody : Component
{
    readonly PlaybackRuntimeSetupModel _m;
    public SetupBody(PlaybackRuntimeSetupModel m) => _m = m;

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        return _m.PhaseSig.Value switch
        {
            PlaybackRuntimeSetupModel.Phase.Offer => Body(Loc.Get(Strings.Playback.Runtime.OfferBody)),
            PlaybackRuntimeSetupModel.Phase.FetchingCatalog => CatalogWaiting(),
            PlaybackRuntimeSetupModel.Phase.Downloading => Downloading(),
            PlaybackRuntimeSetupModel.Phase.Verifying => Verifying(),
            PlaybackRuntimeSetupModel.Phase.Untrusted => Untrusted(),
            PlaybackRuntimeSetupModel.Phase.Ready => Ready(_m.Status, overlay),
            PlaybackRuntimeSetupModel.Phase.Failed => Failed(),
            PlaybackRuntimeSetupModel.Phase.Advanced => Advanced(),
            _ => new BoxEl(),
        };
    }

    internal static Element Column(params Element[] kids) => new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids };

    internal static Element Body(string text, ColorF? color = null) =>
        new TextEl(text) { Size = 13f, Color = color ?? Tok.TextSecondary, Wrap = TextWrap.Wrap };

    internal static Element Lead(string text) =>
        new TextEl(text) { Size = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };

    /// <summary>Inline status block — a coloured status glyph + heading, then wrapped body copy. Reads as clean dialog
    /// content instead of a box-in-a-box InfoBar.</summary>
    internal static Element Status(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = glyphColor, Shrink = 0f, Margin = new Edges4(0, 1f, 0, 0) },
            new BoxEl
            {
                Direction = 1, Gap = 4f, Grow = 1f,
                Children =
                [
                    new TextEl(heading) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    new TextEl(body) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };

    internal static Element BusyRow(string message, string? caption)
    {
        Element label = caption is null
            ? Body(message, Tok.TextPrimary)
            : new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children = [Body(message, Tok.TextPrimary), new TextEl(caption) { Size = 12f, Color = Tok.TextSecondary }],
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 48f,
            Children = [ProgressRing.Indeterminate(), label],
        };
    }

    const float RuntimeProgressWidth = 412f;

    /// <summary>The prototype's catalog phase: one calm status line and the real indeterminate ProgressBar animation,
    /// with the process architecture visible so the wait explains what is being selected. <paramref name="barWidth"/>
    /// defaults to the standalone dialog's own width — the setup wizard's local-playback page (Work package D) passes
    /// its own tier-scaled <c>SetupLayout.RuntimeBarWidth</c>/<c>StageInnerWidth</c> instead.</summary>
    internal static Element CatalogWaiting(float barWidth = RuntimeProgressWidth) => Column(
        Lead(Loc.Get(Strings.Playback.Runtime.CheckingSupport)),
        ProgressMetricRow(
            Loc.Get(Strings.Playback.Runtime.ReachingCatalog),
            RuntimeInformation.ProcessArchitecture.ToString()),
        ProgressBar.Indeterminate(width: barWidth));

    internal Element Downloading(float barWidth = RuntimeProgressWidth)
    {
        long received = _m.Received.Value;
        long total = _m.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(SetupRuntimePresentation.ProgressFraction(received, total), width: barWidth)
            : ProgressBar.Indeterminate(width: barWidth);
        string bytes = DownloadBytes(received, total);
        return Column(
            Lead(Loc.Get(Strings.Playback.Runtime.Downloading)),
            ProgressMetricRow(_m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading), bytes),
            bar,
            new TextEl(Loc.Get(Strings.Playback.Runtime.DownloadBlocking))
                { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
    }

    /// <summary>Verification remains indeterminate because the provisioner reports one real Verifying stage, not
    /// invented sub-percentages. The selected catalog entry still lets the page name exactly what is being checked.</summary>
    internal Element Verifying(float barWidth = RuntimeProgressWidth)
    {
        long total = _m.Total.Value;
        return Column(
            Lead(Loc.Get(Strings.Playback.Runtime.Verifying)),
            ProgressMetricRow(
                Loc.Get(Strings.Playback.Runtime.VerifyingCaption),
                total > 0 ? $"{total / 1_000_000.0:0.0} MB" : "—"),
            ProgressBar.Indeterminate(width: barWidth),
            VerifyDetailBox());
    }

    /// <summary>The version/arch/SHA-256 fact box promoted out of <see cref="Verifying"/> so the setup wizard's
    /// Untrusted facet (which shows this SAME box under a different caption, without the lead/metric-row/bar above
    /// it) can reuse it verbatim instead of re-deriving the same three fields.</summary>
    internal Element VerifyDetailBox()
    {
        var entry = _m.ActiveEntry;
        string version = entry?.SpotifyVersion ?? "—";
        string arch = entry?.Arch ?? RuntimeInformation.ProcessArchitecture.ToString();
        string hash = entry is null ? "—" : SetupRuntimePresentation.ShortHash(entry.DllSha256);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS,
            Padding = Edges4.All(Spacing.S),
            Fill = Tok.FillLayerAlt,
            Corners = CornerRadius4.All(Radii.Control),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), version),
                RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailArch), arch),
                RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailSha256), hash),
            ],
        };
    }

    static Element ProgressMetricRow(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label)
            {
                Size = 13f, Weight = 600, Color = Tok.TextPrimary,
                Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            new TextEl(value) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
        ],
    };

    static string DownloadBytes(long received, long total) => total > 0
        ? $"{received / 1_000_000.0:0.0} / {total / 1_000_000.0:0.0} MB"
        : $"{received / 1_000_000.0:0.0} MB";

    internal static Element Untrusted() => Status(
        Icons.StatusWarning, Tok.SystemFillCaution,
        Loc.Get(Strings.Playback.Runtime.SignatureInvalid),
        Loc.Get(Strings.Playback.Runtime.UntrustedBody));

    internal Element Ready(PlaybackRuntimeStatus status, IOverlayService overlay)
    {
        var kids = new List<Element> { ReadyBadge() };
        if (_m.UpToDate.Value)
            kids.Add(Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(ReadyDetailBox(status, overlay));
        kids.Add(ReadyLinks());
        return new BoxEl { Direction = 1, Gap = Spacing.M, Children = kids.ToArray() };
    }

    /// <summary>The "✓ Ready" badge row, promoted out of <see cref="Ready"/> so the setup wizard's stage panel can
    /// show it standalone (its own <c>ReadyDetailBox</c>/<c>ReadyLinks</c> sit in the wizard's decision column
    /// instead, per the stage/decision split).</summary>
    internal static Element ReadyBadge() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            InfoBadge.Icon(InfoBadgeSeverity.Success),
            new TextEl(Loc.Get(Strings.Playback.Runtime.Ready)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
        ],
    };

    /// <summary>The Version/Arch/Signature/Location fact box, promoted out of <see cref="Ready"/>.
    /// <paramref name="labelWidth"/> defaults to the standalone dialog's own 92-DIP label lane; the setup wizard's
    /// narrower 296-DIP stage panel passes a tighter one.</summary>
    internal Element ReadyDetailBox(PlaybackRuntimeStatus status, IOverlayService overlay, float labelWidth = 92f) => new BoxEl
    {
        Direction = 1, Gap = 6f, Padding = Edges4.All(12f),
        Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailVersion), status.SpotifyVersion, labelWidth),
            RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailArch), status.Arch?.ToString(), labelWidth),
            SignatureRow(status, overlay, labelWidth),
            RuntimeDetailRow(Loc.Get(Strings.Playback.Runtime.DetailLocation), status.RuntimePath, labelWidth),
        ],
    };

    /// <summary>The Replace/Remove link row, promoted out of <see cref="Ready"/>.</summary>
    internal Element ReadyLinks() => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Replace), _m.ShowAdvanced),
            HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.Remove), _m.Remove),
        ],
    };

    internal static Element RuntimeDetailRow(string label, string? value, float labelWidth = 92f) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            new TextEl(value ?? "—")
                { Size = 12f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
        ],
    };

    internal Element Failed() => Status(
        Icons.StatusError, Tok.SystemFillCritical,
        Loc.Get(Strings.Playback.Runtime.Missing),
        _m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack));

    internal Element SignatureRow(PlaybackRuntimeStatus status, IOverlayService overlay, float labelWidth = 92f)
    {
        var kids = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Playback.Runtime.DetailSignature))
                { Size = 12f, Color = Tok.TextSecondary, Width = labelWidth, Shrink = 0f },
            new TextEl(SignatureSummary(status))
                { Size = 12f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f },
        };
        if (status.SignatureInfo is not null)
        {
            kids.Add(Button.Standard("Signature", () => ShowSignatureDetails(overlay, status)) with
            {
                MinWidth = 86f,
                Height = 28f,
                MinHeight = 28f,
                Justify = FlexJustify.Center,
            });
        }
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }

    internal void ShowSignatureDetails(IOverlayService overlay, PlaybackRuntimeStatus status)
    {
        if (status.SignatureInfo is not { } signature) return;
        _m.SignatureDetailsOpened(signature);
        ContentDialog.Show(overlay, d =>
        {
            d.Title = "Digital signature";
            d.DialogWidth = 548f;
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = "Close";
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = SignatureDetailsContent(status, signature);
        });
    }

    internal static Element SignatureDetailsContent(PlaybackRuntimeStatus status, DigitalSignatureInfo signature) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    InfoBadge.Icon(signature.Trust == SignatureTrust.Trusted
                        ? InfoBadgeSeverity.Success
                        : InfoBadgeSeverity.Caution),
                    new TextEl(SignatureSummary(status)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                ],
            },
            new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
            DialogDetailRow("Publisher", signature.Subject),
            DialogDetailRow("Issuer", signature.Issuer),
            DialogDetailRow("Trust", TrustLabel(signature.Trust)),
            DialogDetailRow("Reason", signature.Reason),
            DialogDetailRow("Valid from", signature.ValidFrom.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            DialogDetailRow("Valid to", signature.ValidTo.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            DialogDetailRow("Thumbprint", signature.Thumbprint),
            DialogDetailRow("Pinned fingerprint", status.TrustedByPinnedFingerprint ? "Yes" : "No"),
            DialogDetailRow("File", signature.FilePath),
        ],
    };

    internal static Element DialogDetailRow(string label, string? value) => new BoxEl
    {
        Direction = 1, Gap = 2f,
        Children =
        [
            new TextEl(label) { Size = 11f, Color = Tok.TextSecondary },
            new TextEl(string.IsNullOrWhiteSpace(value) ? "-" : value)
                { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
        ],
    };

    internal static string SignatureSummary(PlaybackRuntimeStatus status)
    {
        if (status.SignatureInfo is { } info && !string.IsNullOrWhiteSpace(info.Subject))
        {
            if (info.Trust == SignatureTrust.Trusted)
                return $"Digitally signed by {info.Subject}";
            if (status.TrustedByPinnedFingerprint)
                return $"Signed by {info.Subject}; trusted by pinned fingerprint";
            return $"Signed by {info.Subject}; {TrustLabel(info.Trust)}";
        }

        if (status.TrustedByPinnedFingerprint) return "Trusted by pinned runtime fingerprint";
        return status.SignatureTrust switch
        {
            SignatureTrust.Trusted => "Verified runtime fingerprint",
            SignatureTrust.Untrusted => "Signature not trusted; manual override",
            SignatureTrust.UnsupportedPlatform => "Signature check unavailable on this platform",
            _ => "Signature unknown",
        };
    }

    internal static string TrustLabel(SignatureTrust trust) => trust switch
    {
        SignatureTrust.Trusted => "Trusted",
        SignatureTrust.Untrusted => "Not trusted",
        SignatureTrust.UnsupportedPlatform => "Unsupported platform",
        _ => "Unknown",
    };

    internal Element Advanced()
    {
        var kids = new List<Element> { Body(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };

        switch (_m.CatalogSig.Value)
        {
            case PlaybackRuntimeSetupModel.CatalogState.Fetching:
                kids.Add(BusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport), null));
                break;
            case PlaybackRuntimeSetupModel.CatalogState.Loaded when _m.SupportedPacks.Count > 0:
                kids.Add(VersionPicker(Loc.Get(Strings.Playback.Runtime.ChooseVersion)));
                break;
            case PlaybackRuntimeSetupModel.CatalogState.Loaded:
                kids.Add(Body(Loc.Get(Strings.Playback.Runtime.NoPack)));
                break;
            case PlaybackRuntimeSetupModel.CatalogState.Failed:
                kids.Add(Body(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable)));
                break;
        }

        kids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0, 4f, 0, 4f) });
        kids.AddRange(LocalSourceRows());
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = kids.ToArray() };
    }

    /// <summary>The RadioButtons version list, promoted out of <see cref="Advanced"/> so the setup wizard's own
    /// Advanced facet (which wraps this in its own <c>ScrollView</c> inside a 480-DIP decision column, with no
    /// header — the page already shows "Choose a version" as its own title) can reuse the exact same label
    /// formatting/recommended-tag/selection-signal wiring. <paramref name="header"/> null omits the RadioButtons
    /// group header entirely.</summary>
    internal Element VersionPicker(string? header)
    {
        var packs = _m.SupportedPacks;
        var labels = new string[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            labels[i] = $"Spotify {packs[i].SpotifyVersion} · {packs[i].Arch}";
            if (i == 0) labels[i] += $"  ({Loc.Get(Strings.Playback.Runtime.Recommended)})";
        }
        return RadioButtons.Create(labels, _m.SelectedPackIndex, header: header);
    }

    /// <summary>The two offline-fallback rows ("Choose a Spotify.dll…" / "Use installed Spotify"), promoted out of
    /// <see cref="Advanced"/>.</summary>
    internal Element[] LocalSourceRows() =>
    [
        SettingRow(Icons.Folder,
            Loc.Get(Strings.Playback.Runtime.InstallFromFolder),
            Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption),
            _m.PickFolder),
        SettingRow(Icons.MusicNote,
            Loc.Get(Strings.Playback.Runtime.UseInstalled),
            Loc.Get(Strings.Playback.Runtime.UseInstalledCaption),
            _m.UseInstalled),
    ];

    /// <summary>A clickable settings-card row: leading glyph, title + caption stack, trailing chevron — the
    /// ProfileMenu.MenuRow chrome (hover/pressed fills) + the LibraryPage two-line stack.</summary>
    internal static Element SettingRow(string glyph, string title, string caption, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 13f,
        Padding = new Edges4(12f, 9f, 12f, 9f),
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, OnClick = onClick,
        Children =
        [
            new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = 1f,
                Children =
                [
                    new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl(caption) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
            new TextEl(Icons.ChevronRightMed) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);
}

/// <summary>The dialog's single command row — optional left-aligned link (the secondary path), right-aligned buttons
/// (max two, accent = the obvious next step). Rendered inside the dialog's command space via
/// <see cref="ContentDialog.Footer"/>, so it swaps per phase without re-opening.</summary>
sealed class SetupFooter : Component
{
    readonly PlaybackRuntimeSetupModel _m;
    public SetupFooter(PlaybackRuntimeSetupModel m) => _m = m;

    public override Element Render()
    {
        var catalogState = _m.CatalogSig.Value;   // Install enablement in Advanced
        var go = UseContext(HistoryStore.NavCtx); // the Failed phase's "View diagnostics" destination
        return _m.PhaseSig.Value switch
        {
            PlaybackRuntimeSetupModel.Phase.Offer => Row(
                Link(Loc.Get(Strings.Playback.Runtime.Advanced), _m.ShowAdvanced),
                Btn(Loc.Get(Strings.Playback.Runtime.NotNow), _m.NotNow),
                AccentBtn(Loc.Get(Strings.Playback.Runtime.DownloadSetup), _m.StartDownload)),

            PlaybackRuntimeSetupModel.Phase.FetchingCatalog => Row(null,
                Btn(Loc.Get(Strings.Auth.Cancel), _m.Cancel)),

            PlaybackRuntimeSetupModel.Phase.Downloading => Row(null,
                Btn(Loc.Get(Strings.Auth.Cancel), _m.Cancel)),

            PlaybackRuntimeSetupModel.Phase.Verifying => Row(null,
                Btn(Loc.Get(Strings.Auth.Cancel), () => { }, enabled: false)),

            PlaybackRuntimeSetupModel.Phase.Untrusted => Row(null,
                Btn(Loc.Get(Strings.Auth.Cancel), _m.CancelUntrusted),
                AccentBtn(Loc.Get(Strings.Playback.Runtime.LoadAnyway), _m.ConfirmUntrusted)),

            PlaybackRuntimeSetupModel.Phase.Ready => Row(
                Link(Loc.Get(Strings.Playback.Runtime.CheckUpdate), _m.CheckForUpdate),
                AccentBtn(Loc.Get(Strings.Playback.Runtime.Done), _m.Close)),

            // The one phase that owes an explanation, so it gets the escape hatch: the full locate/verify report sits
            // beside "Advanced options" rather than behind a Settings tab the confused user would have to discover.
            // The diagnostics link itself only EXISTS when there is somewhere to navigate to (go is not null) —
            // a clickable-but-inert link is worse than no link.
            PlaybackRuntimeSetupModel.Phase.Failed => Row(
                go is not null
                    ? LeftLinks(
                        Link(Loc.Get(Strings.Playback.Runtime.Advanced), _m.ShowAdvanced),
                        HyperlinkButton.Create(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), () => _m.OpenDiagnostics(go)))
                    : Link(Loc.Get(Strings.Playback.Runtime.Advanced), _m.ShowAdvanced),
                Btn(Loc.Get(Strings.Playback.Runtime.NotNow), _m.NotNow),
                AccentBtn(Loc.Get(Strings.Playback.Runtime.TryAgain), _m.Retry)),

            PlaybackRuntimeSetupModel.Phase.Advanced => Row(
                Link(Loc.Get(Strings.Playback.Runtime.Back), _m.Back),
                AccentBtn(Loc.Get(Strings.Playback.Runtime.Install), _m.InstallSelected,
                    enabled: catalogState == PlaybackRuntimeSetupModel.CatalogState.Loaded && _m.SupportedPacks.Count > 0)),

            _ => Row(null, Btn(Loc.Get(Strings.Common.Dismiss), _m.Close)),
        };
    }

    const float BtnMinW = 96f, BtnH = 32f;

    static BoxEl Btn(string text, Action onClick, bool enabled = true) =>
        Button.Standard(text, onClick, isEnabled: enabled) with
        { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center };

    static BoxEl AccentBtn(string text, Action onClick, bool enabled = true) =>
        Button.Accent(text, onClick, isEnabled: enabled) with
        { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1 };

    // Negative left margin so the link's text sits flush with the dialog padding (the HyperlinkButton carries its own
    // 11px inner padding — same trick as InfoBar's HyperlinkActionMarginLeft).
    static Element Link(string text, Action onClick) =>
        HyperlinkButton.Create(text, onClick) with { Margin = new Edges4(-11f, 0, 0, 0) };

    // Two secondary paths in the left slot. Only the FIRST carries Link's flush-left negative margin; the rest keep
    // their own 11px padding, which is exactly the separation the row wants between them.
    static Element LeftLinks(params Element[] links) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Children = links,
    };

    static Element Row(Element? left, params Element[] right)
    {
        var kids = new List<Element>(right.Length + 2);
        if (left is not null) kids.Add(left);
        kids.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
        kids.AddRange(right);
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }
}
