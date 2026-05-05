using CommunityToolkit.Mvvm.Messaging.Messages;
using Wavee.Core.Session;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Messages;

// --- Playback Messages ---

/// <summary>
/// Sent when the currently playing track changes.
/// </summary>
public sealed class TrackChangedMessage(string? trackId)
    : ValueChangedMessage<string?>(trackId);

/// <summary>
/// Broadcast by <c>MusicVideoDiscoveryService</c> after Pathfinder NPV
/// resolves whether the current audio track has a music-video variant
/// (linked-URI pattern). Consumers gate the audio URI before applying so
/// stale discoveries from a previous track don't flip the current state.
/// </summary>
public sealed class MusicVideoAvailabilityMessage(string audioUri, bool hasVideo)
    : ValueChangedMessage<(string AudioUri, bool HasVideo)>((audioUri, hasVideo));

/// <summary>
/// Sent when playback starts or pauses.
/// </summary>
public sealed class PlaybackStateChangedMessage(bool isPlaying)
    : ValueChangedMessage<bool>(isPlaying);

/// <summary>
/// Broadcast by <see cref="Wavee.UI.WinUI.Controls.Track.Behaviors.TrackStateBehavior"/>
/// when any of (CurrentTrackId, IsPlaying, IsBuffering, BufferingTrackId) changes.
/// Subscribers (TrackItem, SearchResultHeroCard, lyrics surfaces, etc.) read the
/// current state via <c>TrackStateBehavior</c>'s static getters and refresh.
/// Replaces the prior <c>TrackStateBehavior.PlaybackStateChanged</c> static event,
/// which was a memory-leak hazard: every subscriber held a strong delegate, the
/// delegate's Target was the subscribing element, and the static event lived for
/// the app's lifetime — any unsubscribe miss leaked the element forever.
/// </summary>
public sealed class TrackStateRefreshMessage;

/// <summary>
/// Sent when the playback context changes (e.g. switched from playlist to album).
/// </summary>
public sealed class PlaybackContextChangedMessage(PlaybackContextInfo? context)
    : ValueChangedMessage<PlaybackContextInfo?>(context);

/// <summary>
/// Unified now-playing message carrying the context URI, the currently-playing
/// track's album URI, and play/pause state. Cards match on either URI —
/// ContextUri catches "played from this album/playlist"; AlbumUri catches "the
/// track happens to belong to this album even though the user launched it from
/// a playlist/search/queue/radio context".
/// </summary>
public sealed class NowPlayingChangedMessage(string? contextUri, string? albumUri, bool isPlaying)
    : ValueChangedMessage<(string? ContextUri, string? AlbumUri, bool IsPlaying)>((contextUri, albumUri, isPlaying));

// --- Auth Messages ---

/// <summary>
/// Sent when the authentication status changes.
/// </summary>
public sealed class AuthStatusChangedMessage(AuthStatus status)
    : ValueChangedMessage<AuthStatus>(status);

/// <summary>
/// Fine-grained sign-in progress for SpotifyConnectDialog. AuthStatus only says
/// "Authenticating"; this explains whether we are waiting for the browser,
/// exchanging the code, connecting the AP session, or hydrating the profile.
/// </summary>
public sealed class AuthProgressMessage(string mainText, string subText, double authProgress, bool showProgressPanel = true)
    : ValueChangedMessage<(string MainText, string SubText, double AuthProgress, bool ShowProgressPanel)>((mainText, subText, authProgress, showProgressPanel));

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
public sealed class PlaybackErrorOccurredMessage(PlaybackErrorEvent error)
    : ValueChangedMessage<PlaybackErrorEvent>(error);

/// <summary>
/// Sent when the active playback device changes.
/// </summary>
public sealed class ActiveDeviceChangedMessage(string? deviceId)
    : ValueChangedMessage<string?>(deviceId);

// --- Library Sync Messages ---

/// <summary>
/// Sent when library sync starts (clear stale UI, show loading state).
/// </summary>
public sealed class LibrarySyncStartedMessage;

/// <summary>
/// Sent when library sync completes successfully (refresh UI with real data).
/// </summary>
public sealed class LibrarySyncCompletedMessage(LibrarySyncSummary summary)
    : ValueChangedMessage<LibrarySyncSummary>(summary);

/// <summary>
/// Summary of what changed during library sync (delta, not totals).
/// Exposes changes as a list for WinUI rendering.
/// </summary>
public sealed record LibrarySyncSummary(
    int TracksAdded = 0,
    int TracksRemoved = 0,
    int AlbumsAdded = 0,
    int AlbumsRemoved = 0,
    int ArtistsAdded = 0,
    int ArtistsRemoved = 0,
    bool HadPartialFailure = false,
    string? PartialFailureReason = null)
{
    public bool HasChanges => TracksAdded + TracksRemoved + AlbumsAdded + AlbumsRemoved + ArtistsAdded + ArtistsRemoved > 0;

    /// <summary>Flat list of individual changes for rendering in ItemsControl.</summary>
    public System.Collections.Generic.IReadOnlyList<SyncDeltaEntry> Entries
    {
        get
        {
            var list = new System.Collections.Generic.List<SyncDeltaEntry>();
            if (TracksAdded > 0) list.Add(new("Tracks", TracksAdded, true, "\uE8D6"));
            if (TracksRemoved > 0) list.Add(new("Tracks", TracksRemoved, false, "\uE8D6"));
            if (AlbumsAdded > 0) list.Add(new("Albums", AlbumsAdded, true, "\uE93C"));
            if (AlbumsRemoved > 0) list.Add(new("Albums", AlbumsRemoved, false, "\uE93C"));
            if (ArtistsAdded > 0) list.Add(new("Artists", ArtistsAdded, true, "\uE77B"));
            if (ArtistsRemoved > 0) list.Add(new("Artists", ArtistsRemoved, false, "\uE77B"));
            return list;
        }
    }
}

/// <summary>A single delta entry: "+12 Tracks" or "-2 Albums".</summary>
public sealed record SyncDeltaEntry(string Label, int Count, bool IsAdded, string IconGlyph)
{
    public string CountText => IsAdded ? $"+{Count}" : $"-{Count}";
}

/// <summary>
/// Sent when library sync fails (show error notification).
/// </summary>
public sealed class LibrarySyncFailedMessage(string error)
    : ValueChangedMessage<string>(error);

/// <summary>
/// Per-collection progress fired by LibrarySyncOrchestrator while
/// SyncAllAsync iterates tracks → albums → artists → shows → listen-later.
/// Drives the live sub-text in SpotifyConnectDialog.
/// Collection is one of: "tracks", "albums", "artists", "shows", "listen-later".
/// </summary>
public sealed class LibrarySyncProgressMessage(string collection, int done, int total)
    : ValueChangedMessage<(string Collection, int Done, int Total)>((collection, done, total));

/// <summary>
/// Fired by PlaylistPrefetchService once it knows how many playlists
/// it's about to warm. Drives the "Loading your playlists" phase
/// transition in SpotifyConnectDialog.
/// </summary>
public sealed class PlaylistPrefetchStartedMessage(int total)
    : ValueChangedMessage<int>(total);

/// <summary>
/// Fired per playlist as PlaylistPrefetchService completes each fetch.
/// PlaylistName drives the sub-text; (done, total) drives the bar slice.
/// </summary>
public sealed class PlaylistPrefetchProgressMessage(string playlistName, int done, int total)
    : ValueChangedMessage<(string PlaylistName, int Done, int Total)>((playlistName, done, total));

/// <summary>
/// Fired by HomeViewModel after the first successful Pathfinder home
/// query has been parsed. Final phase of the sign-in dialog —
/// dialog auto-closes when this arrives.
/// </summary>
public sealed class HomeFeedLoadedMessage(int sectionCount, int itemCount)
    : ValueChangedMessage<(int Sections, int Items)>((sectionCount, itemCount));

/// <summary>
/// AudioProcessManager.StateChanged forwarded as a message so the
/// SpotifyConnectViewModel can react without taking a direct
/// dependency on the audio plumbing.
/// </summary>
public sealed class AudioProcessStateChangedMessage(string state, string? message)
    : ValueChangedMessage<(string State, string? Message)>((state, message));

/// <summary>
/// Sent when library data changes (sync complete, Dealer delta, user action).
/// </summary>
public sealed class LibraryDataChangedMessage;

/// <summary>
/// Sent when the user toggles whether local files appear as a Home shelf.
/// </summary>
public sealed class HomeLocalFilesVisibilityChangedMessage(bool isVisible)
    : ValueChangedMessage<bool>(isVisible);

/// <summary>
/// Sent when the user changes whether the docked player remains visible while
/// the player is popped out into a second window.
/// </summary>
public sealed class DockedPlayerWithFloatingPlayerVisibilityChangedMessage(bool isVisible)
    : ValueChangedMessage<bool>(isVisible);

/// <summary>
/// Sent when the main app window is reactivated after being deactivated.
/// </summary>
public sealed class MainWindowFocusReturnedMessage;

/// <summary>
/// Sent while the shell-owned mini-video teaching prompt is open so other
/// video prompts do not stack over it.
/// </summary>
public sealed class VideoMiniPlayerPromptStateChangedMessage(bool isOpen)
    : ValueChangedMessage<bool>(isOpen);

/// <summary>
/// Sent to request an immediate library sync (e.g. when the local DB appears empty).
/// LibrarySyncOrchestrator handles this; no-ops if a sync is already in progress.
/// </summary>
public sealed class RequestLibrarySyncMessage;

/// <summary>
/// Sent when playlists specifically change.
/// </summary>
public sealed class PlaylistsChangedMessage;

// --- Right Panel Messages ---

/// <summary>
/// Sent by PlayerBar to request toggling a right panel mode.
/// </summary>
public sealed class ToggleRightPanelMessage(RightPanelMode mode)
    : ValueChangedMessage<RightPanelMode>(mode);

/// <summary>
/// Sent by ShellViewModel to notify PlayerBar of the current right panel state.
/// </summary>
public sealed class RightPanelStateChangedMessage(bool isOpen, RightPanelMode mode)
    : ValueChangedMessage<(bool IsOpen, RightPanelMode Mode)>((isOpen, mode));
