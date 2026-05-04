using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Posts and updates Windows toast notifications for the on-device AI lifecycle —
/// specifically the Phi Silica model download triggered when the user opts in
/// at Settings → On-device AI.
///
/// Why this exists: the in-Settings progress panel only helps when the user is
/// looking at Settings. The Phi Silica model is a multi-minute, hundreds-of-MB
/// OS-managed download — most users will switch to another window or minimize
/// the app while it runs. Microsoft Store sets the precedent for that scenario:
/// a system toast with a live progress bar that the user can monitor from
/// anywhere via the Action Center. This service emits exactly that.
///
/// Stability contract: a single fixed <see cref="ModelTag"/> is used for every
/// AI-related toast, so each call REPLACES the previous toast in the Action
/// Center instead of stacking three or four "AI" entries. Posting "preparing",
/// then "70%", then "ready" produces a single notification that mutates in
/// place. Match Windows Update / Microsoft Store behavior.
/// </summary>
public sealed class AiNotificationService
{
    /// <summary>
    /// Tag used for every notification this service posts. Stable across calls
    /// so updates collapse onto the same Action Center entry. Group is shared
    /// for any future AI-related toasts (e.g. generation completion).
    /// </summary>
    private const string ModelTag = "wavee.ai.model-prep";
    private const string AiGroup = "wavee.ai";

    private readonly ILogger? _logger;

    public AiNotificationService(ILogger<AiNotificationService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initial post when the user flips the master AI toggle on. Indeterminate
    /// progress + a "Cancel download" button. Subsequent
    /// <see cref="UpdateModelProgressAsync"/> calls fill in the determinate
    /// value.
    /// </summary>
    public void ShowModelPreparingNotification()
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", ActivationActions.OpenAiSettings)
                .AddText("Preparing on-device AI")
                .AddText("Downloading the Phi Silica model. You can keep using Wavee while this finishes.")
                .SetAppLogoOverride(new Uri("ms-appx:///Assets/Square150x150Logo.png"), AppNotificationImageCrop.Default)
                .AddProgressBar(new AppNotificationProgressBar()
                {
                    Title = "On-device AI",
                    Status = "Connecting…",
                    // Start at 0; UpdateModelProgressAsync replaces this with
                    // the live value within milliseconds of the download starting.
                    // (AppNotificationProgressBar has no IndeterminateValue
                    // constant in WinAppSDK 2.0; "indeterminate" requires
                    // bind-style data binding which we don't need here.)
                    Value = 0.0,
                    ValueStringOverride = "0%"
                })
                .AddButton(new AppNotificationButton("Cancel download")
                    .AddArgument("action", ActivationActions.CancelAiDownload))
                .SetTag(ModelTag)
                .SetGroup(AiGroup)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ShowModelPreparingNotification failed.");
        }
    }

    /// <summary>
    /// Updates the live progress value on the existing toast. Uses
    /// <see cref="AppNotificationManager.UpdateAsync(AppNotificationProgressData, string, string)"/>
    /// which mutates the existing notification in place rather than re-posting
    /// — important so the OS doesn't pop the toast back to the foreground on
    /// every progress tick.
    /// </summary>
    /// <param name="progress01">Progress in 0.0 – 1.0 (clamped).</param>
    /// <param name="status">Short status string ("Downloading model… 47.2%").</param>
    public async Task UpdateModelProgressAsync(double progress01, string status)
    {
        try
        {
            var clamped = Math.Clamp(progress01, 0.0, 1.0);
            var data = new AppNotificationProgressData(sequenceNumber: GetSequenceNumber())
            {
                Title = "On-device AI",
                Status = status ?? string.Empty,
                Value = clamped,
                ValueStringOverride = $"{clamped * 100:0.#}%"
            };

            // UpdateAsync against the same tag/group as the originally posted
            // notification.
            var result = await AppNotificationManager.Default.UpdateAsync(data, ModelTag, AiGroup);
            if (result != AppNotificationProgressResult.Succeeded)
            {
                _logger?.LogDebug("UpdateModelProgressAsync returned {Result} (no notification with tag {Tag}/{Group} present).",
                    result, ModelTag, AiGroup);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UpdateModelProgressAsync failed.");
        }
    }

    /// <summary>
    /// Replaces the preparing toast with a "Ready" toast that has a "Try it"
    /// button deep-linking to the now-playing view. Posting with the same tag
    /// replaces the previous notification in the Action Center.
    /// </summary>
    public void ShowModelReadyNotification()
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", ActivationActions.OpenNowPlaying)
                .AddText("On-device AI is ready")
                .AddText("Open a song with lyrics to summarize themes or explain individual lines.")
                .SetAppLogoOverride(new Uri("ms-appx:///Assets/Square150x150Logo.png"), AppNotificationImageCrop.Default)
                .AddButton(new AppNotificationButton("Try it")
                    .AddArgument("action", ActivationActions.OpenNowPlaying))
                .SetTag(ModelTag)
                .SetGroup(AiGroup)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ShowModelReadyNotification failed.");
        }
    }

    /// <summary>
    /// Replaces the preparing toast with an error toast with a "Retry" button
    /// that deep-links to Settings → On-device AI.
    /// </summary>
    public void ShowModelErrorNotification(string? message = null)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("action", ActivationActions.OpenAiSettings)
                .AddText("Couldn't prepare on-device AI")
                .AddText(string.IsNullOrWhiteSpace(message)
                    ? "The Phi Silica model didn't finish downloading. Open Settings to try again."
                    : message)
                .SetAppLogoOverride(new Uri("ms-appx:///Assets/Square150x150Logo.png"), AppNotificationImageCrop.Default)
                .AddButton(new AppNotificationButton("Retry")
                    .AddArgument("action", ActivationActions.RetryAiDownload))
                .SetTag(ModelTag)
                .SetGroup(AiGroup)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ShowModelErrorNotification failed.");
        }
    }

    /// <summary>
    /// Removes any AI model notification from the Action Center. Called when
    /// the user cancels the download or toggles the master AI toggle off.
    /// </summary>
    public async Task RemoveModelNotificationsAsync()
    {
        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(ModelTag, AiGroup);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "RemoveModelNotificationsAsync failed.");
        }
    }

    /// <summary>
    /// Sequence number for in-place updates. Must be monotonically increasing
    /// across UpdateAsync calls or the OS drops the update as stale.
    /// </summary>
    private static uint GetSequenceNumber() =>
        (uint)System.Threading.Interlocked.Increment(ref _sequence);

    private static int _sequence;
}

/// <summary>
/// Stable string vocabulary for notification activation arguments. Centralized
/// here so <see cref="AiNotificationService"/> (the producer) and
/// <c>AppNotificationActivationRouter</c> (the consumer) can't drift.
/// </summary>
public static class ActivationActions
{
    public const string OpenAiSettings = "open-ai-settings";
    public const string OpenNowPlaying = "open-now-playing";
    public const string CancelAiDownload = "cancel-ai-download";
    public const string RetryAiDownload = "retry-ai-download";
}
