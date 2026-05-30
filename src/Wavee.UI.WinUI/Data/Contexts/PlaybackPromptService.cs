using System;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Handles play action prompting via a single combined dialog.
/// Gets XamlRoot from MainWindow.
/// </summary>
internal sealed partial class PlaybackPromptService : IPlaybackPromptService
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

        // The prompt reads Window.Content / XamlRoot and shows a ContentDialog — both
        // UI-thread-only. PlayContextAsync is frequently invoked from a background
        // Task.Run (home cards, browse, hero), so a direct off-thread Window.Content read
        // throws RPC_E_WRONG_THREAD and silently faults the play. Marshal the whole UI
        // interaction onto the UI thread.
        var dispatcher = MainWindow.Instance?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            return await ShowPromptAndPersistAsync();

        var tcs = new TaskCompletionSource<PlayAction>();
        dispatcher.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await ShowPromptAndPersistAsync()); }
            catch (Exception) { tcs.TrySetResult(PlayAction.PlayAndClear); }
        });
        return await tcs.Task;
    }

    private async Task<PlayAction> ShowPromptAndPersistAsync()
    {
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