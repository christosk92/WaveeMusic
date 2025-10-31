namespace Wavee.Connect;

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
    /// Playback options (shuffle, repeat).
    /// </summary>
    public PlaybackOptions Options { get; init; } = new();

    /// <summary>
    /// Active device ID in the cluster.
    /// </summary>
    public string? ActiveDeviceId { get; init; }

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
    /// Album art image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

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

    /// <summary>All state changed (initial state or major update).</summary>
    All = Track | Position | Status | Context | Options | ActiveDevice | Source
}
