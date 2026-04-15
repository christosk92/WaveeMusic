using System.ComponentModel;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;

namespace Wavee.UI.Contracts;

/// <summary>
/// Centralized playback state. Wraps <see cref="Contexts.IPlayerContext"/> and adds
/// playback context, queue, and command methods.
/// </summary>
public interface IPlaybackStateService : INotifyPropertyChanged
{
    // --- Playback state ---
    bool IsPlaying { get; }

    /// <summary>
    /// True while a track is loading — from click until first audio plays (local) or ack received (remote).
    /// </summary>
    bool IsBuffering { get; }

    /// <summary>
    /// The track ID currently being loaded/buffered (for per-row loading indicators).
    /// Null when not buffering.
    /// </summary>
    string? BufferingTrackId { get; }

    string? CurrentTrackId { get; }
    string? CurrentTrackTitle { get; }
    string? CurrentArtistName { get; }
    string? CurrentAlbumArt { get; }
    string? CurrentAlbumArtLarge { get; }
    string? CurrentArtistId { get; }
    string? CurrentAlbumId { get; }
    IReadOnlyList<ArtistCredit>? CurrentArtists { get; }

    /// <summary>
    /// Theme-appropriate hex color extracted from the current album art.
    /// Uses DarkHex in dark theme, LightHex in light theme.
    /// </summary>
    string? CurrentAlbumArtColor { get; }

    /// <summary>
    /// True if playback is on a remote device (not this app).
    /// </summary>
    bool IsPlayingRemotely { get; }

    /// <summary>
    /// True if the active device doesn't support volume control.
    /// </summary>
    bool IsVolumeRestricted { get; }

    /// <summary>
    /// Display name of the active Spotify device, if remote.
    /// </summary>
    string? ActiveDeviceName { get; }

    /// <summary>
    /// Device type of the active Spotify Connect device (when remote) or Computer locally.
    /// Drives the icon shown in the output-device card.
    /// </summary>
    DeviceType ActiveDeviceType { get; }

    /// <summary>
    /// All Spotify Connect devices visible in the user's cluster (this device + others).
    /// </summary>
    IReadOnlyList<ConnectDevice> AvailableConnectDevices { get; }

    /// <summary>
    /// Friendly name of the current local Windows audio output device (from PortAudio via AudioHost).
    /// Null until the first state update from the audio engine arrives.
    /// </summary>
    string? ActiveAudioDeviceName { get; }

    /// <summary>
    /// Full list of local Windows audio output devices enumerated by PortAudio.
    /// </summary>
    IReadOnlyList<AudioOutputDeviceDto> AvailableAudioDevices { get; }

    /// <summary>
    /// True when the AudioHost IPC pipe is connected and the audio engine is responsive.
    /// </summary>
    bool IsAudioEngineAvailable { get; }
    double Position { get; set; }
    double Duration { get; }
    double Volume { get; set; }
    bool IsShuffle { get; }
    RepeatMode RepeatMode { get; }

    // --- Context & queue ---

    /// <summary>
    /// Describes what is currently being played (playlist, album, etc.).
    /// </summary>
    PlaybackContextInfo? CurrentContext { get; }

    /// <summary>
    /// The current playback queue.
    /// </summary>
    IReadOnlyList<QueueItem> Queue { get; }

    /// <summary>
    /// Index of the currently playing item within <see cref="Queue"/>.
    /// </summary>
    int QueuePosition { get; }

    // --- Commands ---
    void PlayPause();
    void Next();
    void Previous();
    void Seek(double positionMs);
    void SetShuffle(bool shuffle);
    void SetRepeatMode(RepeatMode mode);

    /// <summary>
    /// Begins playback of an entire context (playlist, album, etc.).
    /// </summary>
    void PlayContext(PlaybackContextInfo context, int startIndex = 0);

    /// <summary>
    /// Plays a single track, optionally within a context.
    /// </summary>
    void PlayTrack(string trackId, PlaybackContextInfo? context = null);

    /// <summary>
    /// Adds a track to the end of the user queue.
    /// </summary>
    void AddToQueue(string trackId);

    /// <summary>
    /// Adds multiple tracks to the end of the user queue.
    /// </summary>
    void AddToQueue(IEnumerable<string> trackIds);

    /// <summary>
    /// Replaces the queue, sets the playback context, and loads the track at startIndex as current.
    /// </summary>
    void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0);

    /// <summary>
    /// Sets the buffering indicator for a track ID. Call before async play commands.
    /// </summary>
    void NotifyBuffering(string? trackId);

    /// <summary>
    /// Clears the buffering indicator when an optimistic play command does not produce playback.
    /// </summary>
    void ClearBuffering();
}
