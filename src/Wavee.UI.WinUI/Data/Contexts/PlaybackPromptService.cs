using System;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Handles play action prompting via a single combined dialog.
/// Gets XamlRoot from MainWindow.
/// </summary>
internal sealed class PlaybackPromptService : IPlaybackPromptService
{
    private readonly ISettingsService _settings;

    public PlaybackPromptService(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<PlayAction> ResolvePlayActionAsync()
    {
        // If not asking every time and already configured, use saved default
        if (_settings.Settings.PlayBehaviorConfigured && !_settings.Settings.AskPlayAction)
        {
            return Enum.TryParse<PlayAction>(_settings.Settings.DefaultPlayAction, out var saved)
                ? saved
                : PlayAction.PlayAndClear;
        }

        var xamlRoot = MainWindow.Instance.Content?.XamlRoot;
        if (xamlRoot == null) return PlayAction.PlayAndClear;

        var isFirstTime = !_settings.Settings.PlayBehaviorConfigured;

        var result = await PlayActionDialog.ShowAsync(
            xamlRoot,
            isFirstTime: isFirstTime,
            askEveryTime: _settings.Settings.AskPlayAction);

        // Persist all preferences
        _settings.Update(s =>
        {
            if (isFirstTime && result.TapMode != null)
            {
                s.TrackClickBehavior = result.TapMode;
                s.PlayBehaviorConfigured = true;
            }

            s.AskPlayAction = result.AskEveryTime;

            if (!result.AskEveryTime && result.Action != PlayAction.Cancelled)
                s.DefaultPlayAction = result.Action.ToString();
        });

        return result.Action;
    }
}
