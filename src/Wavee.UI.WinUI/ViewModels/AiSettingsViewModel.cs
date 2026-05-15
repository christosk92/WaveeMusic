using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for the "On-device AI" settings section. Bridges the persisted
/// <see cref="Wavee.UI.WinUI.Data.Models.AppSettings"/> AI fields and the
/// runtime hardware/region/consent gates exposed by <see cref="AiCapabilities"/>.
///
/// Pattern mirrors <c>ConnectStateViewModel</c> — a dedicated VM per section
/// keeps the giant <c>SettingsViewModel</c> unchanged and keeps the AI surface
/// area discoverable in one file.
///
/// Model download UX: when the user flips the master <see cref="AiFeaturesEnabled"/>
/// toggle on, this VM immediately calls <see cref="AiCapabilities.EnsureLanguageModelReadyAsync"/>
/// with progress reporting. The bound <see cref="IsModelPreparing"/> /
/// <see cref="ModelPreparationProgress"/> / <see cref="ModelPreparationStatus"/>
/// drive a progress UI under the toggle so the user sees the model arriving
/// instead of staring at a "nothing happens" silence for several minutes on
/// the first opt-in.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AiCapabilities _capabilities;
    private readonly AiNotificationService? _notifications;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _activePrepCts;

    public AiSettingsViewModel(
        ISettingsService settings,
        AiCapabilities capabilities,
        AiNotificationService? notifications = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _notifications = notifications;

        // Capture the UI dispatcher so progress callbacks (which arrive on a
        // background thread from the underlying IAsyncOperationWithProgress)
        // can post property changes back to the bound TextBlocks/ProgressBars.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Snapshot the initial state for the bound props.
        _aiFeaturesEnabled = _settings.Settings.AiFeaturesEnabled;
        _aiLyricsExplainEnabled = _settings.Settings.AiLyricsExplainEnabled;
        _aiLyricsSummarizeEnabled = _settings.Settings.AiLyricsSummarizeEnabled;
        _aiBioSummarizeEnabled = _settings.Settings.AiBioSummarizeEnabled;

        // If the user already opted in on a previous session, reflect the
        // current model state in the bound status without kicking off a fresh
        // download (the OS-side install only needs to happen once).
        UpdateInitialPreparationStatus();
    }

    /// <summary>True when hardware + region permit Phi Silica. Affects whether
    /// the toggles are interactive at all.</summary>
    public bool CanUseAi =>
        _capabilities.LanguageModelHardwareAvailable && _capabilities.RegionAllowed;

    /// <summary>One-line status under the master toggle.</summary>
    public string StatusDescription => _capabilities.DescribeStatus();

    /// <summary>True if the per-feature toggles should be interactive
    /// (CanUseAi AND master toggle on AND model is ready).</summary>
    public bool ArePerFeatureTogglesEnabled =>
        CanUseAi && AiFeaturesEnabled && !IsModelPreparing;

    [ObservableProperty]
    private bool _aiFeaturesEnabled;

    partial void OnAiFeaturesEnabledChanged(bool value)
    {
        _settings.Update(s => s.AiFeaturesEnabled = value);
        OnPropertyChanged(nameof(ArePerFeatureTogglesEnabled));

        if (value)
        {
            // Kick off the model preparation immediately so the user sees
            // download progress instead of a silent multi-minute wait the
            // first time they click an affordance.
            BeginModelPreparation();
        }
        else
        {
            CancelModelPreparation();
            ResetPreparationState();
            // Drop any active toast so the user doesn't see a stale "preparing"
            // notification after they've turned the feature off.
            _ = _notifications?.RemoveModelNotificationsAsync();
        }
    }

    [ObservableProperty]
    private bool _aiLyricsExplainEnabled;

    partial void OnAiLyricsExplainEnabledChanged(bool value)
    {
        _settings.Update(s => s.AiLyricsExplainEnabled = value);
    }

    [ObservableProperty]
    private bool _aiLyricsSummarizeEnabled;

    partial void OnAiLyricsSummarizeEnabledChanged(bool value)
    {
        _settings.Update(s => s.AiLyricsSummarizeEnabled = value);
    }

    [ObservableProperty]
    private bool _aiBioSummarizeEnabled;

    partial void OnAiBioSummarizeEnabledChanged(bool value)
    {
        _settings.Update(s => s.AiBioSummarizeEnabled = value);
    }

    // ── Model preparation state ──────────────────────────────────────────

    /// <summary>True while EnsureReadyAsync is in flight (download / OS-side install).</summary>
    [ObservableProperty]
    private bool _isModelPreparing;

    /// <summary>Download progress 0–100. -1 means "indeterminate" (no progress yet).</summary>
    [ObservableProperty]
    private double _modelPreparationProgress = -1;

    /// <summary>One-line status for the prep UI ("Downloading…", "Ready", "Couldn't download", etc.).</summary>
    [ObservableProperty]
    private string _modelPreparationStatus = string.Empty;

    /// <summary>True when the per-feature toggles can show "Ready" badge.</summary>
    [ObservableProperty]
    private bool _isModelReady;

    [RelayCommand]
    private void RetryModelPreparation()
    {
        if (!CanUseAi) return;
        BeginModelPreparation();
    }

    private void UpdateInitialPreparationStatus()
    {
        if (!CanUseAi)
        {
            // Hardware / region gate is closed — the section status row already
            // says why. Don't render a separate prep state.
            return;
        }

        if (_capabilities.LanguageModelHardwareAvailable)
        {
            // We can't synchronously query Ready vs NotReady here without
            // touching the AI assembly more than necessary. The DescribeStatus
            // already says "Ready" or "Available — first use will download…",
            // so leave the prep state at its defaults until the user opts in.
            // If the user toggled in on a previous session AND the model is
            // already installed, opting in again is a no-op (EnsureReadyAsync
            // returns immediately with progress=1.0).
            if (_aiFeaturesEnabled)
                BeginModelPreparation();
        }
    }

    private void BeginModelPreparation()
    {
        // Tear down any previous prep run.
        CancelModelPreparation();

        var cts = _activePrepCts = new CancellationTokenSource();

        IsModelPreparing = true;
        IsModelReady = false;
        ModelPreparationProgress = -1;
        ModelPreparationStatus = "Preparing on-device AI…";
        OnPropertyChanged(nameof(ArePerFeatureTogglesEnabled));

        // Post the system toast so the user can see download progress from the
        // Action Center even after they navigate away from this Settings page.
        _notifications?.ShowModelPreparingNotification();

        var progress = new Progress<double>(p =>
        {
            // Progress<T> already marshals to the captured SynchronizationContext.
            // We update the bound property and a friendlier status string.
            ModelPreparationProgress = Math.Round(p * 100.0, 1);
            ModelPreparationStatus = p switch
            {
                <= 0.001 => "Connecting…",
                < 0.999 => $"Downloading model… {ModelPreparationProgress:0.#}%",
                _ => "Finalizing…"
            };

            // Fan progress out to the system toast as well — the same value
            // drives both surfaces.
            _ = _notifications?.UpdateModelProgressAsync(p, ModelPreparationStatus);
        });

        _ = RunPreparationAsync(progress, cts.Token);
    }

    private async Task RunPreparationAsync(IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            var ok = await _capabilities.EnsureLanguageModelReadyAsync(progress, ct);
            if (ct.IsCancellationRequested) return;

            // Marshal back to UI thread for the final state flip.
            PostToUi(() =>
            {
                IsModelPreparing = false;
                if (ok)
                {
                    IsModelReady = true;
                    ModelPreparationProgress = 100;
                    ModelPreparationStatus = "On-device AI is ready";
                    // Replace the in-flight progress toast with a "Ready" toast
                    // that has a "Try it" button deep-linking to now-playing.
                    _notifications?.ShowModelReadyNotification();
                }
                else
                {
                    IsModelReady = false;
                    ModelPreparationProgress = -1;
                    ModelPreparationStatus = "Couldn't prepare the on-device model. Tap Retry to try again.";
                    _notifications?.ShowModelErrorNotification();
                }
                OnPropertyChanged(nameof(ArePerFeatureTogglesEnabled));
            });
        }
        catch (OperationCanceledException)
        {
            PostToUi(ResetPreparationState);
            _ = _notifications?.RemoveModelNotificationsAsync();
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                IsModelPreparing = false;
                IsModelReady = false;
                ModelPreparationProgress = -1;
                ModelPreparationStatus = $"Couldn't prepare the on-device model: {ex.Message}";
                _notifications?.ShowModelErrorNotification(ex.Message);
                OnPropertyChanged(nameof(ArePerFeatureTogglesEnabled));
            });
        }
    }

    private void CancelModelPreparation()
    {
        try
        {
            _activePrepCts?.Cancel();
        }
        catch
        {
            // already disposed; harmless
        }
        _activePrepCts = null;
    }

    private void ResetPreparationState()
    {
        IsModelPreparing = false;
        IsModelReady = false;
        ModelPreparationProgress = -1;
        ModelPreparationStatus = string.Empty;
        OnPropertyChanged(nameof(ArePerFeatureTogglesEnabled));
    }

    private void PostToUi(Action action)
    {
        if (_dispatcher is { } dq)
            dq.TryEnqueue(() => action());
        else
            action();
    }
}
