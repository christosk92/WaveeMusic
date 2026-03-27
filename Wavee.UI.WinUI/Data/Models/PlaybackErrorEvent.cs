namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// Represents a playback error event for observable error streams and notifications.
/// </summary>
public sealed record PlaybackErrorEvent(
    PlaybackErrorKind Kind,
    string Message,
    string? CommandName = null);
