using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Centralized notification service. Any service or ViewModel can call
/// <see cref="Show(string, NotificationSeverity, TimeSpan?)"/> to display
/// a global notification in the shell's InfoBar.
/// </summary>
internal sealed partial class NotificationService : ObservableObject, INotificationService
{
    private readonly IMessenger _messenger;
    private DispatcherTimer? _autoDismissTimer;
    private Action? _currentAction;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private NotificationSeverity _severity = NotificationSeverity.Error;

    [ObservableProperty]
    private string? _actionLabel;

    public NotificationService(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public void Show(string message, NotificationSeverity severity = NotificationSeverity.Error,
                     TimeSpan? autoDismissAfter = null)
    {
        Show(new NotificationInfo
        {
            Message = message,
            Severity = severity,
            AutoDismissAfter = autoDismissAfter
        });
    }

    public void Show(NotificationInfo notification)
    {
        // Ensure UI thread — Show can be called from background threads (Task.Run playback commands)
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher == null)
        {
            // On background thread — dispatch to UI
            MainWindow.Instance?.DispatcherQueue?.TryEnqueue(() => Show(notification));
            return;
        }

        // Stop any pending auto-dismiss
        StopAutoDismissTimer();

        Message = notification.Message;
        Severity = notification.Severity;
        ActionLabel = notification.ActionLabel;
        _currentAction = notification.Action;
        IsOpen = true;

        _messenger.Send(new NotificationRequestedMessage(notification));

        // Set up auto-dismiss if requested
        if (notification.AutoDismissAfter.HasValue)
        {
            _autoDismissTimer = new DispatcherTimer
            {
                Interval = notification.AutoDismissAfter.Value
            };
            _autoDismissTimer.Tick += OnAutoDismissTimerTick;
            _autoDismissTimer.Start();
        }
    }

    public void Dismiss()
    {
        StopAutoDismissTimer();

        IsOpen = false;
        Message = null;
        ActionLabel = null;
        _currentAction = null;

        _messenger.Send(new NotificationDismissedMessage());
    }

    /// <summary>
    /// Invokes the current notification's action callback, if any.
    /// </summary>
    public void InvokeAction()
    {
        _currentAction?.Invoke();
    }

    private void OnAutoDismissTimerTick(object? sender, object e)
    {
        Dismiss();
    }

    private void StopAutoDismissTimer()
    {
        if (_autoDismissTimer != null)
        {
            _autoDismissTimer.Stop();
            _autoDismissTimer.Tick -= OnAutoDismissTimerTick;
            _autoDismissTimer = null;
        }
    }
}
