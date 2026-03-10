using System;

namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// Describes a global notification to display in the app shell.
/// </summary>
public sealed record NotificationInfo
{
    public required string Message { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Error;

    /// <summary>
    /// If set, the notification will auto-dismiss after this duration.
    /// </summary>
    public TimeSpan? AutoDismissAfter { get; init; }

    /// <summary>
    /// Optional label for an action button (e.g. "Retry", "Undo").
    /// </summary>
    public string? ActionLabel { get; init; }

    /// <summary>
    /// Optional callback invoked when the action button is clicked.
    /// </summary>
    public Action? Action { get; init; }
}

/// <summary>
/// Severity levels for app notifications (UI-framework-agnostic).
/// </summary>
public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}
