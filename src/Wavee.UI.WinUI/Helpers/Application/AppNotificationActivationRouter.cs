using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Helpers.Application;

/// <summary>
/// Routes <see cref="AppNotificationActivatedEventArgs.Arguments"/> dictionaries
/// from app-notification activations into actions on the running app.
///
/// Pure helper — no state, no side effects beyond what the routed action
/// itself does. Keeps <see cref="App"/>'s OnLaunched / NotificationInvoked
/// handlers thin.
///
/// Action vocabulary lives in <see cref="ActivationActions"/> alongside
/// <see cref="AiNotificationService"/> — the producer and consumer share one
/// strings file so they can't drift.
/// </summary>
public static class AppNotificationActivationRouter
{
    /// <summary>
    /// Inspects the activation arguments and returns true if the action was a
    /// "background" one that should NOT bring the window to the foreground.
    /// Caller checks the return value: if the app was cold-started solely to
    /// service a background action, the caller can <c>Application.Current.Exit()</c>
    /// without ever showing UI.
    /// </summary>
    /// <param name="arguments">The activation arguments dictionary
    /// (<see cref="AppNotificationActivatedEventArgs.Arguments"/>).</param>
    /// <param name="dispatcher">UI dispatcher used to marshal navigation calls
    /// onto the UI thread. Pass null for tests.</param>
    /// <returns>True if the action was background-only (no window required).</returns>
    public static bool Route(
        IReadOnlyDictionary<string, string>? arguments,
        DispatcherQueue? dispatcher)
    {
        if (arguments is null)
            return false;

        if (!arguments.TryGetValue("action", out var action) || string.IsNullOrEmpty(action))
            return false;

        switch (action)
        {
            case ActivationActions.CancelAiDownload:
                // Background action — fire the cancel without bringing the
                // window forward. Settings VM cleans up its own state.
                MarshalToUi(dispatcher, () =>
                {
                    var notifications = Ioc.Default.GetService<AiNotificationService>();
                    _ = notifications?.RemoveModelNotificationsAsync();

                    // We don't have a direct "cancel current preparation" hook
                    // from outside the AiSettingsViewModel because it's a
                    // transient VM created by SettingsPage. Flipping the
                    // master toggle off (which we DO have from settings)
                    // drives the same cancel path through OnAiFeaturesEnabledChanged.
                    var settings = Ioc.Default.GetService<Wavee.UI.WinUI.Data.Contracts.ISettingsService>();
                    settings?.Update(s => s.AiFeaturesEnabled = false);
                });
                return true; // background — caller may exit if cold-launched.

            case ActivationActions.OpenNowPlaying:
                MarshalToUi(dispatcher, () =>
                {
                    // The shell's "expand the now-playing view" entry point
                    // depends on the current shell state. For the pilot, we
                    // simply ensure the main window is foregrounded; a
                    // future revision can navigate to the lyrics surface
                    // explicitly.
                    Wavee.UI.WinUI.MainWindow.Instance?.Activate();
                });
                return false;

            case ActivationActions.RetryAiDownload:
            case ActivationActions.OpenAiSettings:
                MarshalToUi(dispatcher, () =>
                {
                    Wavee.UI.WinUI.MainWindow.Instance?.Activate();
                    // Specific Settings → AI section navigation can hook here
                    // once we wire a deep-link target on ShellViewModel.
                });
                return false;

            default:
                return false;
        }
    }

    private static void MarshalToUi(DispatcherQueue? dispatcher, Action action)
    {
        if (dispatcher is { } dq)
            dq.TryEnqueue(() => action());
        else
            action();
    }
}
