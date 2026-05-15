using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Single decision point for "should an on-device AI affordance be shown / invoked".
/// Combines three independent gates:
///   1. Hardware/runtime: <c>Microsoft.Windows.AI.Text.LanguageModel.GetReadyState()</c>
///      reports whether Phi Silica is available on this machine. Only Copilot+
///      PCs (Snapdragon X / Intel Core Ultra Series 2 / AMD Ryzen AI 300+ on
///      Windows 11 24H2) return Ready or NotReady; everything else returns
///      NotSupportedOnCurrentSystem / NotCompatibleWithSystemHardware /
///      OSUpdateNeeded / DisabledByUser / CapabilityMissing.
///   2. Region: Phi Silica + Image Description are unavailable in China; affordances
///      are hidden when the system region is CN.
///   3. User consent: <see cref="AppSettings.AiFeaturesEnabled"/> is the master
///      opt-in toggle. Default off — until the user flips it on in Settings, this
///      class refuses regardless of hardware.
///
/// All AI calls go through this class so the typed surface is centralized here.
/// Consumers see only bool / Task results — they don't reference
/// Microsoft.Windows.AI.* types directly.
///
/// Hardware probe is wrapped in a try/catch for TypeLoadException /
/// FileNotFoundException because on machines that don't ship the AI projection
/// DLLs at all (older WinAppSDK targets, custom strip configurations) the type
/// loader fails before our gate runs. We treat any throw as "not available".
/// </summary>
public sealed class AiCapabilities
{
    private readonly ISettingsService _settings;
    private readonly ILogger? _logger;

    /// <summary>
    /// Region check is a one-shot read at construction. WinUI runs in a single
    /// region per session and switching it requires an app restart anyway.
    /// </summary>
    private readonly bool _regionAllowed;

    private bool? _runtimeAvailableCache;

    // Limited Access Feature unlock — required by Windows AI APIs for
    // packaged apps. Token + attribution are issued by Microsoft per package
    // (Package Family Name suffix s6dvdzhx5m6rm). The unlock is process-wide
    // and idempotent; we still cache the outcome so repeated probes don't
    // re-enter the WinRT call.
    //
    // Source of values: Microsoft LAF Access Request response, 2026-05-01,
    // TrackingID #2605010040005804.
    private const string LanguageModelLafFeatureId = "com.microsoft.windows.ai.languagemodel";
    private const string LanguageModelLafToken = "4+g4v/xx6B81Wc6Z0sO0bg==";
    private const string LanguageModelLafAttribution =
        "s6dvdzhx5m6rm has registered their use of com.microsoft.windows.ai.languagemodel with Microsoft and agrees to the terms of use.";

    private static readonly object LafUnlockGate = new();
    private static bool? _lafUnlockedCache;
    private static string? _lafUnlockStatusLabel;

    public AiCapabilities(ISettingsService settings, ILogger<AiCapabilities>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;

        try
        {
            var region = RegionInfo.CurrentRegion?.TwoLetterISORegionName;
            _regionAllowed = !string.Equals(region, "CN", StringComparison.OrdinalIgnoreCase);
            if (!_regionAllowed)
                _logger?.LogInformation("AI features disabled: region {Region} not supported by Microsoft Foundry on Windows.", region);
        }
        catch
        {
            // If region detection fails for any reason, default to allowed; the
            // hardware gate below will still keep things off on non-Copilot+ PCs.
            _regionAllowed = true;
        }
    }

    /// <summary>
    /// True if Phi Silica is callable on this hardware/OS. Cached per-process —
    /// the answer can't change without a reboot.
    ///
    /// Note: this only calls <c>GetReadyState()</c>, which does NOT require
    /// the Limited Access Feature unlock. The LAF unlock is deferred until
    /// the user actually opts in and we attempt to consume the model
    /// (<see cref="EnsureLanguageModelReadyAsync"/>) — that way we don't
    /// invoke a privileged WinRT API for users who never enable AI.
    /// </summary>
    public bool LanguageModelHardwareAvailable
    {
        get
        {
            if (_runtimeAvailableCache.HasValue)
                return _runtimeAvailableCache.Value;

            _runtimeAvailableCache = ProbeLanguageModelAvailable();
            return _runtimeAvailableCache.Value;
        }
    }

    /// <summary>True if the user's region permits Phi Silica.</summary>
    public bool RegionAllowed => _regionAllowed;

    /// <summary>True if the user has flipped the master AI toggle on in Settings.</summary>
    public bool UserOptedIn => _settings.Settings.AiFeaturesEnabled;

    /// <summary>
    /// One-line composite check the UI binds against. Affordance is hidden unless
    /// every gate is open — hardware available, region allowed, user opted in.
    /// </summary>
    public bool IsAiAvailableAndEnabled =>
        LanguageModelHardwareAvailable && _regionAllowed && UserOptedIn;

    /// <summary>
    /// Per-feature gate: lyrics line "explain" affordance.
    /// </summary>
    public bool IsLyricsExplainEnabled =>
        IsAiAvailableAndEnabled && _settings.Settings.AiLyricsExplainEnabled;

    /// <summary>
    /// Per-feature gate: header "summarize song" affordance.
    /// </summary>
    public bool IsLyricsSummarizeEnabled =>
        IsAiAvailableAndEnabled && _settings.Settings.AiLyricsSummarizeEnabled;

    /// <summary>
    /// Per-feature gate: artist-page "About this artist" excerpt synthesised on-device
    /// when Spotify's ArtistOverview returns no biography. The artist page hides the
    /// excerpt entirely when this gate is closed (so the surface is honest about why
    /// it can't summarise — no half-rendered placeholder).
    /// </summary>
    public bool IsArtistBioSummarizeEnabled =>
        IsAiAvailableAndEnabled && _settings.Settings.AiBioSummarizeEnabled;

    /// <summary>
    /// Triggers the Phi Silica model download/init if not already ready. Called
    /// on the first opt-in click. Returns true if the model is ready to serve
    /// requests; false if the user is on incompatible hardware or the install
    /// failed. Safe to call repeatedly — the underlying API is idempotent.
    ///
    /// <paramref name="progress"/> receives values in [0.0, 1.0] during the
    /// download, fired from a background thread. Pass null if you don't need
    /// progress (the underlying op still reports — we just don't propagate).
    /// </summary>
    public async Task<bool> EnsureLanguageModelReadyAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!LanguageModelHardwareAvailable || !_regionAllowed)
            return false;
        // First call to actually consume the model — unlock the LAF here
        // (and only here) so we never invoke this privileged WinRT API for
        // users who never opted into AI. The unlock is process-wide and
        // cached after the first success.
        if (!UserOptedIn)
            return false;
        if (!EnsureLanguageModelLafUnlocked())
            return false;

        try
        {
            return await EnsureReadyCore(progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TypeLoadException ex)
        {
            _logger?.LogWarning(ex, "EnsureLanguageModelReadyAsync hit TypeLoadException — AI projection assembly missing at runtime.");
            return false;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "EnsureLanguageModelReadyAsync hit FileNotFoundException — AI projection assembly missing at runtime.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "EnsureLanguageModelReadyAsync hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EnsureLanguageModelReadyAsync failed.");
            return false;
        }
    }

    /// <summary>
    /// Status string suitable for display under the master toggle in Settings.
    /// One of: "Ready", "Available — first use will download the model",
    /// "Requires a Copilot+ PC", "Not available in your region".
    /// </summary>
    public string DescribeStatus()
    {
        if (!_regionAllowed)
            return "Not available in your region";
        if (!LanguageModelHardwareAvailable)
            return "Requires a Copilot+ PC";
        // If the user has already opted in and we attempted the LAF unlock,
        // surface a failed unlock as the reason — it's a different story
        // from "no Copilot+ PC" and points at a manifest / package-family-
        // name mismatch rather than hardware.
        if (_lafUnlockedCache == false && !string.IsNullOrEmpty(_lafUnlockStatusLabel))
            return _lafUnlockStatusLabel!;
        return ReadyStateLabel();
    }

    public string DescribeDiagnosticState()
        => $"hardware={LanguageModelHardwareAvailable}, regionAllowed={_regionAllowed}, userOptedIn={UserOptedIn}, " +
           $"lyricsExplain={_settings.Settings.AiLyricsExplainEnabled}, lyricsMeaning={_settings.Settings.AiLyricsSummarizeEnabled}, " +
           $"artistBio={_settings.Settings.AiBioSummarizeEnabled}, " +
           $"laf={_lafUnlockStatusLabel ?? "<not probed>"}, " +
           $"status=\"{DescribeStatus()}\"";

    // ── Reflection-isolated bridges ──────────────────────────────────────────
    //
    // These methods are the ONLY place we touch Microsoft.Windows.AI.* types.
    // Wrapping them in a try/catch with a cached negative result means a
    // missing assembly (Win10 1809 box, AI payload stripped) just collapses
    // to "not available" without crashing. As Microsoft.Windows.AI ships in
    // the WinAppSDK references on machines that don't physically have it,
    // direct typed access would still link OK — but the FileNotFoundException
    // surfaces at call time. Cached so we only pay the JIT once.

    // ──────────────────────────────────────────────────────────────────────
    // JIT type-load isolation pattern.
    //
    // The Microsoft.Windows.AI.* projection assemblies may be missing at
    // runtime on machines that don't have the matching WinAppSDK runtime
    // installed (or where our manifest minimum version doesn't line up).
    // When that happens, the CLR's JIT throws FileNotFoundException /
    // TypeLoadException **when it compiles a method that references those
    // types** — *before* the method's IL begins executing. A try/catch
    // INSIDE such a method is therefore unreachable; the exception fires at
    // the call site, not inside the method body.
    //
    // The fix: isolate every Microsoft.Windows.AI.* reference into a
    // separate method marked [MethodImpl(MethodImplOptions.NoInlining)].
    // The outer method puts a try/catch around the call, where the JIT
    // failure surfaces as a regular catchable exception.
    //
    // NoInlining is critical — without it the JIT can inline the inner
    // method into the outer one, dragging the AI metadata back into the
    // outer method's compile unit and bypassing the catch.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Process-wide one-shot unlock of the
    /// <c>com.microsoft.windows.ai.languagemodel</c> Limited Access Feature.
    /// Required for packaged WinUI 3 apps that consume Phi Silica — without
    /// the unlock, every Microsoft.Windows.AI.Text.LanguageModel call throws
    /// UnauthorizedAccessException. Idempotent and cheap; the underlying
    /// WinRT call short-circuits after the first success per process.
    /// </summary>
    private bool EnsureLanguageModelLafUnlocked()
    {
        if (_lafUnlockedCache.HasValue)
            return _lafUnlockedCache.Value;

        lock (LafUnlockGate)
        {
            if (_lafUnlockedCache.HasValue)
                return _lafUnlockedCache.Value;

            try
            {
                var unlocked = TryUnlockLanguageModelLafCore(out var statusLabel);
                _lafUnlockStatusLabel = statusLabel;
                _lafUnlockedCache = unlocked;
                if (unlocked)
                    _logger?.LogInformation("LimitedAccessFeature {FeatureId} unlocked: {Status}",
                        LanguageModelLafFeatureId, statusLabel);
                else
                    _logger?.LogWarning("LimitedAccessFeature {FeatureId} unlock denied: {Status}",
                        LanguageModelLafFeatureId, statusLabel);
                return unlocked;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "LimitedAccessFeatures.TryUnlockFeature threw — treating as unavailable.");
                _lafUnlockStatusLabel = "Limited Access Feature unlock failed";
                _lafUnlockedCache = false;
                return false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryUnlockLanguageModelLafCore(out string statusLabel)
    {
        var access = Windows.ApplicationModel.LimitedAccessFeatures.TryUnlockFeature(
            LanguageModelLafFeatureId,
            LanguageModelLafToken,
            LanguageModelLafAttribution);

        switch (access.Status)
        {
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Available:
                statusLabel = "Available";
                return true;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.AvailableWithoutToken:
                statusLabel = "AvailableWithoutToken";
                return true;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Unavailable:
                statusLabel = "Unavailable";
                return false;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Unknown:
                statusLabel = "User not eligible";
                return false;
            default:
                statusLabel = $"Unknown LAF status ({(int)access.Status})";
                return false;
        }
    }

    private bool ProbeLanguageModelAvailable()
    {
        try
        {
            return ProbeLanguageModelAvailableCore();
        }
        catch (TypeLoadException)
        {
            _logger?.LogWarning("Phi Silica probe failed: AI projection type could not load.");
            return false;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "Phi Silica probe failed: AI projection assembly missing at runtime.");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Phi Silica probe threw unexpectedly; treating as unavailable.");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeLanguageModelAvailableCore()
    {
        // Per Microsoft's AIFeatureReadyState reference, Ready and NotReady
        // both indicate "the model is supported on this machine; NotReady
        // just means it hasn't been downloaded yet". Everything else means we
        // can't use it today.
        var state = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
        return state == Microsoft.Windows.AI.AIFeatureReadyState.Ready
            || state == Microsoft.Windows.AI.AIFeatureReadyState.NotReady;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<bool> EnsureReadyCore(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var state = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
        if (state == Microsoft.Windows.AI.AIFeatureReadyState.Ready)
        {
            progress?.Report(1.0);
            return true;
        }
        if (state == Microsoft.Windows.AI.AIFeatureReadyState.NotReady)
        {
            // EnsureReadyAsync returns IAsyncOperationWithProgress<AIFeatureReadyResult, double>.
            // Wire up cancellation + progress reporting before awaiting.
            var op = Microsoft.Windows.AI.Text.LanguageModel.EnsureReadyAsync();

            if (progress != null)
            {
                op.Progress = (_, p) =>
                {
                    // p is 0.0 – 1.0; clamp defensively in case the runtime ever
                    // overshoots.
                    progress.Report(Math.Clamp(p, 0.0, 1.0));
                };
            }

            using var ctReg = cancellationToken.Register(() =>
            {
                try { op.Cancel(); } catch { /* op may have completed */ }
            });

            var result = await op;
            var finalState = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
            var succeeded = result.Status == Microsoft.Windows.AI.AIFeatureReadyResultState.Success;

            if (succeeded)
            {
                progress?.Report(1.0);
                if (finalState == Microsoft.Windows.AI.AIFeatureReadyState.Ready)
                {
                    _logger?.LogInformation("LanguageModel.EnsureReadyAsync prepared Phi Silica successfully.");
                }
                else
                {
                    _logger?.LogWarning(
                        "LanguageModel.EnsureReadyAsync reported Success but GetReadyState still reports {FinalState}; proceeding with model creation per Microsoft guidance.",
                        finalState);
                }

                return true;
            }

            _logger?.LogWarning(
                "LanguageModel.EnsureReadyAsync did not prepare Phi Silica. initialState={InitialState}, resultStatus={ResultStatus}, finalState={FinalState}, errorDisplayText={ErrorDisplayText}, error={Error}, extendedError={ExtendedError}",
                state,
                result.Status,
                finalState,
                string.IsNullOrWhiteSpace(result.ErrorDisplayText) ? "<empty>" : result.ErrorDisplayText,
                DescribeException(result.Error),
                DescribeException(result.ExtendedError));

            return false;
        }

        _logger?.LogWarning("LanguageModel cannot be prepared from readyState={ReadyState}.", state);
        return false;
    }

    private static string DescribeException(Exception? ex)
        => ex is null
            ? "<none>"
            : $"{ex.GetType().Name}: 0x{ex.HResult:X8} {ex.Message}";

    private static string ReadyStateLabel()
    {
        try
        {
            return ReadyStateLabelCore();
        }
        catch
        {
            return "Requires a Copilot+ PC";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadyStateLabelCore()
    {
        return Microsoft.Windows.AI.Text.LanguageModel.GetReadyState() switch
        {
            Microsoft.Windows.AI.AIFeatureReadyState.Ready => "Ready",
            Microsoft.Windows.AI.AIFeatureReadyState.NotReady => "Available — first use will download the model",
            Microsoft.Windows.AI.AIFeatureReadyState.DisabledByUser => "Disabled in Windows AI settings",
            Microsoft.Windows.AI.AIFeatureReadyState.OSUpdateNeeded => "A Windows update is required",
            Microsoft.Windows.AI.AIFeatureReadyState.NotCompatibleWithSystemHardware => "Requires a Copilot+ PC",
            Microsoft.Windows.AI.AIFeatureReadyState.NotSupportedOnCurrentSystem => "Requires a Copilot+ PC",
            Microsoft.Windows.AI.AIFeatureReadyState.CapabilityMissing => "AI capability missing",
            _ => "Requires a Copilot+ PC",
        };
    }
}
