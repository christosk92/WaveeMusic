using Wavee.Audio.Queue;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;

namespace Wavee.Connect;

/// <summary>
/// Lightweight snapshot of a Spotify Connect device as seen in the cluster.
/// </summary>
/// <param name="DeviceId">Spotify device identifier.</param>
/// <param name="Name">Human-readable device name (e.g. "Living Room Speaker").</param>
/// <param name="Type">Device type (computer, smartphone, speaker, TV, etc.).</param>
/// <param name="IsActive">True if this is the currently active device in the cluster.</param>
public sealed record ConnectDevice(
    string DeviceId,
    string Name,
    DeviceType Type,
    bool IsActive);

/// <summary>
/// Unified playback state model combining cluster updates and local playback.
/// Immutable snapshot of state at a point in time.
/// </summary>
/// <remarks>
/// WHY: Provides clean public API for consumers without protobuf complexity.
/// Unified model works for both remote (cluster) and local (IPlaybackEngine) state sources.
///
/// USAGE:
/// <code>
/// // Access current state
/// var title = stateManager.CurrentState.Track?.Title;
///
/// // Subscribe to changes
/// stateManager.StateChanges.Subscribe(state =>
///     Console.WriteLine($"Track: {state.Track?.Title}, Status: {state.Status}"));
///
/// // Filter specific changes
/// stateManager.TrackChanged.Subscribe(state =>
///     Console.WriteLine($"New track: {state.Track?.Title}"));
/// </code>
/// </remarks>
public sealed record PlaybackState
{
    /// <summary>
    /// Track information (null if no track playing).
    /// </summary>
    public TrackInfo? Track { get; init; }

    /// <summary>
    /// Current playback position in milliseconds.
    /// </summary>
    public long PositionMs { get; init; }

    /// <summary>
    /// Track duration in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Current playback status.
    /// </summary>
    public PlaybackStatus Status { get; init; }

    /// <summary>
    /// Context URI (e.g., "spotify:playlist:xxx", "spotify:album:xxx").
    /// </summary>
    public string? ContextUri { get; init; }

    /// <summary>
    /// Context URL (e.g., "context://spotify:playlist:xxx").
    /// </summary>
    public string? ContextUrl { get; init; }

    /// <summary>
    /// Current track index within context (0-based).
    /// </summary>
    public int CurrentIndex { get; init; }

    /// <summary>
    /// Previous tracks in context (up to 16).
    /// </summary>
    public IReadOnlyList<TrackReference> PrevTracks { get; init; } = [];

    /// <summary>
    /// Next tracks in context (user queue + up to 48 context tracks).
    /// </summary>
    public IReadOnlyList<TrackReference> NextTracks { get; init; } = [];

    /// <summary>
    /// Rich previous tracks with metadata (for UI display).
    /// Populated from both cluster state and local PlaybackQueue.
    /// </summary>
    public IReadOnlyList<IQueueItem> PrevQueue { get; init; } = [];

    /// <summary>
    /// Rich next tracks with metadata (user queue first, then context/autoplay).
    /// Contains QueueTrack, QueuePageMarker, and QueueDelimiter items.
    /// </summary>
    public IReadOnlyList<IQueueItem> NextQueue { get; init; } = [];

    /// <summary>
    /// Playback options (shuffle, repeat).
    /// </summary>
    public PlaybackOptions Options { get; init; } = new();

    /// <summary>
    /// Active device ID in the cluster.
    /// </summary>
    public string? ActiveDeviceId { get; init; }

    /// <summary>
    /// Display name of the active device (e.g. "iPhone", "Living Room Speaker").
    /// </summary>
    public string? ActiveDeviceName { get; init; }

    /// <summary>
    /// Device type of the currently active Spotify Connect device (computer, smartphone,
    /// speaker, TV, etc.). Defaults to <see cref="DeviceType.Computer"/> when no remote
    /// device is active.
    /// </summary>
    public DeviceType ActiveDeviceType { get; init; } = DeviceType.Computer;

    /// <summary>
    /// Full list of Spotify Connect devices visible in the user's cluster.
    /// Includes the local device as well as any other phones, speakers, TVs, etc.
    /// </summary>
    public IReadOnlyList<ConnectDevice> AvailableConnectDevices { get; init; } = [];

    /// <summary>
    /// Friendly name of the current local Windows audio output device (e.g. "Sony WH-1000XM5").
    /// Flows from PortAudio in the AudioHost process and is only meaningful when playing locally.
    /// </summary>
    public string? ActiveAudioDeviceName { get; init; }

    /// <summary>
    /// Full list of local audio output devices enumerated by PortAudio in the AudioHost process.
    /// </summary>
    public IReadOnlyList<AudioOutputDeviceDto> AvailableAudioDevices { get; init; } = [];

    /// <summary>
    /// Volume level from active device (0-65535 Spotify scale).
    /// </summary>
    public uint Volume { get; init; }

    /// <summary>
    /// True if the active device doesn't support volume control (Capabilities.DisableVolume).
    /// </summary>
    public bool IsVolumeRestricted { get; init; }

    /// <summary>
    /// Timestamp when this state was captured (Unix milliseconds).
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// State source (cluster update or local playback).
    /// </summary>
    public StateSource Source { get; init; }

    /// <summary>
    /// Change flags indicating what changed from previous state.
    /// </summary>
    public StateChanges Changes { get; init; } = StateChanges.None;

    /// <summary>
    /// Session ID for this playback session (persists across track changes).
    /// Used for Spotify Connect state publishing.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Queue revision hash for change detection.
    /// Used by Spotify web player to sync queue UI.
    /// </summary>
    public string? QueueRevision { get; init; }

    /// <summary>
    /// Whether seeking is supported for the current track.
    /// False for infinite streams (radio, live streams).
    /// </summary>
    public bool CanSeek { get; init; } = true;

    /// <summary>
    /// Creates an empty playback state (no playback).
    /// </summary>
    public static PlaybackState Empty => new()
    {
        Status = PlaybackStatus.Stopped,
        Source = StateSource.Cluster,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

/// <summary>
/// Track information extracted from PlayerState.
/// </summary>
public sealed record TrackInfo
{
    /// <summary>
    /// Spotify track URI (e.g., "spotify:track:xxx").
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Track unique ID.
    /// </summary>
    public string? Uid { get; init; }

    /// <summary>
    /// Track title/name.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Artist name (comma-separated if multiple).
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Album name.
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Album URI (e.g., "spotify:album:xxx").
    /// </summary>
    public string? AlbumUri { get; init; }

    /// <summary>
    /// Artist URI (e.g., "spotify:artist:xxx").
    /// </summary>
    public string? ArtistUri { get; init; }

    /// <summary>
    /// Medium album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Small album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageSmallUrl { get; init; }

    /// <summary>
    /// Large album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageLargeUrl { get; init; }

    /// <summary>
    /// Extra-large album art image URL (format: "spotify:image:{id}").
    /// </summary>
    public string? ImageXLargeUrl { get; init; }

    /// <summary>
    /// Full metadata dictionary from protobuf.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Playback status enumeration.
/// </summary>
public enum PlaybackStatus
{
    /// <summary>No playback or unknown state.</summary>
    Stopped,

    /// <summary>Track is currently playing.</summary>
    Playing,

    /// <summary>Track is paused.</summary>
    Paused,

    /// <summary>Track is buffering.</summary>
    Buffering
}

/// <summary>
/// Playback options (shuffle, repeat).
/// </summary>
public sealed record PlaybackOptions
{
    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    public bool Shuffling { get; init; }

    /// <summary>
    /// Whether repeat context (playlist/album) is enabled.
    /// </summary>
    public bool RepeatingContext { get; init; }

    /// <summary>
    /// Whether repeat track is enabled.
    /// </summary>
    public bool RepeatingTrack { get; init; }
}

/// <summary>
/// State source indicator.
/// </summary>
public enum StateSource
{
    /// <summary>State from cluster update (remote playback).</summary>
    Cluster,

    /// <summary>State from local playback engine.</summary>
    Local
}

/// <summary>
/// Flags indicating what changed in a state update.
/// </summary>
[Flags]
public enum StateChanges
{
    /// <summary>No changes.</summary>
    None = 0,

    /// <summary>Track changed (different URI).</summary>
    Track = 1 << 0,

    /// <summary>Playback position changed significantly.</summary>
    Position = 1 << 1,

    /// <summary>Playback status changed (playing/paused/stopped).</summary>
    Status = 1 << 2,

    /// <summary>Context changed (different playlist/album).</summary>
    Context = 1 << 3,

    /// <summary>Playback options changed (shuffle/repeat).</summary>
    Options = 1 << 4,

    /// <summary>Active device changed.</summary>
    ActiveDevice = 1 << 5,

    /// <summary>State source changed (cluster → local or vice versa).</summary>
    Source = 1 << 6,

    /// <summary>Queue changed (tracks added/removed from queue).</summary>
    Queue = 1 << 7,

    /// <summary>Volume changed on active device.</summary>
    Volume = 1 << 8,

    /// <summary>All state changed (initial state or major update).</summary>
    All = Track | Position | Status | Context | Options | ActiveDevice | Source | Queue | Volume
}
