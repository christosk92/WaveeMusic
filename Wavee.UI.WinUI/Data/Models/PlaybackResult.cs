using System;

namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// Result of a playback command execution.
/// </summary>
public sealed record PlaybackResult
{
    public bool IsSuccess { get; init; }
    public PlaybackErrorKind? ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }

    public static PlaybackResult Success() => new() { IsSuccess = true };

    public static PlaybackResult Failure(PlaybackErrorKind kind, string message, Exception? ex = null)
        => new() { IsSuccess = false, ErrorKind = kind, ErrorMessage = message, Exception = ex };
}

/// <summary>
/// Categories of playback errors for routing retry/UI logic.
/// </summary>
public enum PlaybackErrorKind
{
    Network,
    Unauthorized,
    NotFound,
    RateLimited,
    DeviceUnavailable,
    PremiumRequired,
    Unavailable,
    Unknown
}
