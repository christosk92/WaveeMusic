using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Session;
using Wavee.UI.Models;

namespace Wavee.UI.Contracts;

/// <summary>
/// App-wide authentication state. Provides the current user's profile,
/// auth status, and account type information.
/// </summary>
public interface IAuthState : INotifyPropertyChanged
{
    AuthStatus Status { get; }
    UserData? CurrentUser { get; }
    AccountType? AccountType { get; }
    string? Username { get; }
    string? DisplayName { get; }
    string? ProfileImageUrl { get; }
    bool IsAuthenticated { get; }
    bool IsPremium { get; }
    string? ConnectionError { get; }

    /// <summary>
    /// Stable device-id for this app instance — the same value Spotify Connect
    /// uses as the device's <c>device_id</c>. Surfaced here so UI controls
    /// (e.g. the audio-output picker's "this device" check) don't need to
    /// reach for <c>Session.Config.DeviceId</c> directly.
    /// </summary>
    string? DeviceId { get; }

    /// <summary>
    /// Attempts to restore a previously cached session (with retry).
    /// </summary>
    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Manually retry connection after a failure.
    /// </summary>
    Task<bool> RetryConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Logs the user out and clears all auth state.
    /// </summary>
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>
    /// Logs in using the authorization code flow.
    /// </summary>
    Task LoginWithAuthorizationCodeAsync(CancellationToken ct = default);

    /// <summary>
    /// Logs in using the device code flow, invoking the callback when
    /// the device code is available for the user to enter.
    /// </summary>
    Task LoginWithDeviceCodeAsync(Action<DeviceCodeInfo> onDeviceCodeReceived, CancellationToken ct = default);

    /// <summary>
    /// Raised when the authentication status changes.
    /// </summary>
    event EventHandler<AuthStatus>? AuthStatusChanged;
}

/// <summary>
/// Authentication status for the current user session.
/// </summary>
public enum AuthStatus
{
    Unknown,
    Authenticating,
    Authenticated,
    SessionExpired,
    LoggedOut,
    Error
}