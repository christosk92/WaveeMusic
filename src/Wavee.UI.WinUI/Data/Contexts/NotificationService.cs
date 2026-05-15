using System;
using System.Threading.Tasks;
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
    private readonly IActivityService? _activityService;
    private DispatcherTimer? _autoDismissTimer;
    private Func<Task>? _currentAction;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private NotificationSeverity _severity = NotificationSeverity.Error;

    [ObservableProperty]
    private string? _actionLabel;

    [ObservableProperty]
    private bool _isActionBusy;

    public NotificationService(IMessenger messenger, IActivityService? activityService = null)
    {
        _messenger = messenger;
        _activityService = activityService;
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
        IsActionBusy = false;
        IsOpen = true;

        _messenger.Send(new NotificationRequestedMessage(notification));

        // Also post to the activity bell so it persists in history
        PostToActivityBell(notification);

        // Auto-dismiss is the default for the new floating-toast UX. If the
        // caller didn't pick a duration, give actionable toasts a longer window
        // so the user can read + reach the action button before it closes.
        var dismissAfter = notification.AutoDismissAfter
            ?? (notification.ActionLabel != null
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromSeconds(5));

        _autoDismissTimer = new DispatcherTimer { Interval = dismissAfter };
        _autoDismissTimer.Tick += OnAutoDismissTimerTick;
        _autoDismissTimer.Start();
    }

    private void PostToActivityBell(NotificationInfo notification)
    {
        if (_activityService == null) return;

        var status = notification.Severity switch
        {
            NotificationSeverity.Error => ActivityStatus.Failed,
            NotificationSeverity.Warning => ActivityStatus.Info,
            NotificationSeverity.Success => ActivityStatus.Completed,
            _ => ActivityStatus.Info
        };

        var iconGlyph = notification.Severity switch
        {
            NotificationSeverity.Error => Styles.FluentGlyphs.ErrorBadge,
            NotificationSeverity.Warning => Styles.FluentGlyphs.Warning,
            NotificationSeverity.Success => Styles.FluentGlyphs.CheckMark,
            _ => Styles.FluentGlyphs.Info
        };

        if (notification.Action != null && notification.ActionLabel != null)
        {
            var actions = new[] { new ActivityAction(notification.ActionLabel, null, notification.Action) };
            _activityService.Post("app", notification.Message, actions,
                iconGlyph: iconGlyph, status: status);
        }
        else
        {
            _activityService.Post("app", notification.Message,
                iconGlyph: iconGlyph, status: status);
        }
    }

    public void Dismiss()
    {
        StopAutoDismissTimer();

        IsOpen = false;
        Message = null;
        ActionLabel = null;
        _currentAction = null;
        IsActionBusy = false;

        _messenger.Send(new NotificationDismissedMessage());
    }

    /// <summary>
    /// Invokes the current notification's async action callback, if any.
    /// Disables the action button while the task is running.
    /// </summary>
    public async Task InvokeActionAsync()
    {
        if (_currentAction == null || IsActionBusy) return;

        try
        {
            IsActionBusy = true;
            await _currentAction();
        }
        finally
        {
            IsActionBusy = false;
        }
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
