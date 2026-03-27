using CommunityToolkit.Mvvm.Messaging.Messages;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Messages;

// --- Playback Messages ---

/// <summary>
/// Sent when the currently playing track changes.
/// </summary>
public sealed class TrackChangedMessage(string? trackId)
    : ValueChangedMessage<string?>(trackId);

/// <summary>
/// Sent when playback starts or pauses.
/// </summary>
public sealed class PlaybackStateChangedMessage(bool isPlaying)
    : ValueChangedMessage<bool>(isPlaying);

/// <summary>
/// Sent when the playback context changes (e.g. switched from playlist to album).
/// </summary>
public sealed class PlaybackContextChangedMessage(PlaybackContextInfo? context)
    : ValueChangedMessage<PlaybackContextInfo?>(context);

// --- Auth Messages ---

/// <summary>
/// Sent when the authentication status changes.
/// </summary>
public sealed class AuthStatusChangedMessage(AuthStatus status)
    : ValueChangedMessage<AuthStatus>(status);

/// <summary>
/// Sent when the user profile data is updated.
/// </summary>
public sealed class UserProfileUpdatedMessage(UserData? user)
    : ValueChangedMessage<UserData?>(user);

// --- Notification Messages ---

/// <summary>
/// Sent when a notification is requested from any part of the app.
/// </summary>
public sealed class NotificationRequestedMessage(NotificationInfo notification)
    : ValueChangedMessage<NotificationInfo>(notification);

/// <summary>
/// Sent when the current notification is dismissed.
/// </summary>
public sealed class NotificationDismissedMessage;

// --- Connectivity Messages ---

/// <summary>
/// Sent when connectivity status changes.
/// </summary>
public sealed class ConnectivityChangedMessage(bool isConnected)
    : ValueChangedMessage<bool>(isConnected);

// --- Playback Extended Messages ---

/// <summary>
/// Sent when the buffering state changes during playback.
/// </summary>
public sealed class PlaybackBufferingChangedMessage(bool isBuffering)
    : ValueChangedMessage<bool>(isBuffering);

/// <summary>
/// Sent when a playback command encounters an error.
/// </summary>
public sealed class PlaybackErrorOccurredMessage(Models.PlaybackErrorEvent error)
    : ValueChangedMessage<Models.PlaybackErrorEvent>(error);

/// <summary>
/// Sent when the active playback device changes.
/// </summary>
public sealed class ActiveDeviceChangedMessage(string? deviceId)
    : ValueChangedMessage<string?>(deviceId);
