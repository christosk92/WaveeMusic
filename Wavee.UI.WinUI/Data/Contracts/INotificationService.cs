using System;
using System.ComponentModel;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// App-wide notification service. Any service or ViewModel can inject this
/// to show global notifications in the shell's InfoBar.
/// </summary>
public interface INotificationService : INotifyPropertyChanged
{
    bool IsOpen { get; }
    string? Message { get; }
    NotificationSeverity Severity { get; }
    string? ActionLabel { get; }

    /// <summary>
    /// Shows a notification with the given message and severity.
    /// </summary>
    void Show(string message, NotificationSeverity severity = NotificationSeverity.Error,
              TimeSpan? autoDismissAfter = null);

    /// <summary>
    /// Shows a notification from a <see cref="NotificationInfo"/> descriptor.
    /// </summary>
    void Show(NotificationInfo notification);

    /// <summary>
    /// Dismisses the currently visible notification.
    /// </summary>
    void Dismiss();
}
