using Wavee.Connect.Commands;

namespace Wavee.Connect;

/// <summary>
/// Interface for local audio playback engine.
/// Defines the contract for implementing audio decoding, playback, and state management.
/// </summary>
/// <remarks>
/// WHY: Decouples PlaybackStateManager from audio implementation details.
/// Enables testing with mock implementations and supports future audio pipeline development.
///
/// IMPLEMENTATION STATUS: **Interface only - no implementations exist yet.**
/// This will be implemented in Phase 6 (Audio Pipeline) of CONNECT_IMPLEMENTATION_PLAN.md.
///
/// WHEN TO IMPLEMENT:
/// - AudioPipeline.cs (~500 lines) - Main decoding/playback pipeline
/// - VorbisDecoder.cs (~200 lines) - Ogg Vorbis decoding
/// - AudioSink.cs (~200 lines) - Platform audio output
///
/// USAGE (Future):
/// <code>
/// var engine = new AudioPipeline(session, audioSink);
/// var stateManager = new PlaybackStateManager(dealerClient, engine, spClient, session);
///
/// // Commands automatically forwarded to engine
/// // Engine state changes automatically published to Spotify
/// </code>
/// </remarks>
public interface IPlaybackEngine
{
    /// <summary>
    /// Observable stream of local playback state changes.
    /// Emits when track changes, play/pause, position updates, etc.
    /// </summary>
    IObservable<LocalPlaybackState> StateChanges { get; }

    /// <summary>
    /// Observable stream of playback errors.
    /// Emits when playback fails (e.g., audio device disconnected, decode error).
    /// </summary>
    IObservable<PlaybackError> Errors { get; }

    /// <summary>
    /// Gets the current playback state (synchronous access).
    /// </summary>
    LocalPlaybackState CurrentState { get; }

    /// <summary>
    /// Starts playback from a play command (from Spotify app or local).
    /// </summary>
    /// <param name="command">Play command with context, track, position, options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when playback starts.</returns>
    Task PlayAsync(PlayCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses current playback.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when paused.</returns>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops playback completely and clears current track.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes paused playback.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when resumed.</returns>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeks to a specific position in current track.
    /// </summary>
    /// <param name="positionMs">Position in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when seek completes.</returns>
    Task SeekAsync(long positionMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skips to next track in queue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when skip completes.</returns>
    Task SkipNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Skips to previous track.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when skip completes.</returns>
    Task SkipPreviousAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets shuffle mode.
    /// </summary>
    /// <param name="enabled">Whether shuffle is enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when shuffle mode set.</returns>
    Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets repeat context mode (playlist/album repeat).
    /// </summary>
    /// <param name="enabled">Whether repeat context is enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when repeat mode set.</returns>
    Task SetRepeatContextAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets repeat track mode (single track repeat).
    /// </summary>
    /// <param name="enabled">Whether repeat track is enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when repeat mode set.</returns>
    Task SetRepeatTrackAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local playback state from audio engine.
/// Lightweight structure optimized for frequent updates.
/// </summary>
public sealed record LocalPlaybackState
{
    /// <summary>
    /// Current track URI (e.g., "spotify:track:xxx").
    /// </summary>
    public string? TrackUri { get; init; }

    /// <summary>
    /// Current track UID.
    /// </summary>
    public string? TrackUid { get; init; }

    /// <summary>
    /// Context URI (e.g., "spotify:playlist:xxx").
    /// </summary>
    public string? ContextUri { get; init; }

    /// <summary>
    /// Current playback position in milliseconds.
    /// </summary>
    public long PositionMs { get; init; }

    /// <summary>
    /// Track duration in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Whether playback is currently playing.
    /// </summary>
    public bool IsPlaying { get; init; }

    /// <summary>
    /// Whether playback is paused.
    /// </summary>
    public bool IsPaused { get; init; }

    /// <summary>
    /// Whether audio is buffering.
    /// </summary>
    public bool IsBuffering { get; init; }

    /// <summary>
    /// Playback speed (1.0 = normal, 2.0 = double speed).
    /// </summary>
    public double PlaybackSpeed { get; init; } = 1.0;

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

    /// <summary>
    /// Timestamp when this state was captured (Unix milliseconds).
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Creates an empty/stopped playback state.
    /// </summary>
    public static LocalPlaybackState Empty => new()
    {
        IsPlaying = false,
        IsPaused = false,
        IsBuffering = false,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

/// <summary>
/// Playback error information.
/// </summary>
/// <param name="ErrorType">Type of error that occurred.</param>
/// <param name="Message">Human-readable error message.</param>
/// <param name="Exception">Optional underlying exception.</param>
public sealed record PlaybackError(
    PlaybackErrorType ErrorType,
    string Message,
    Exception? Exception = null);

/// <summary>
/// Types of playback errors.
/// </summary>
public enum PlaybackErrorType
{
    /// <summary>Audio device not available (disconnected, busy, etc.).</summary>
    AudioDeviceUnavailable,

    /// <summary>Failed to decode audio stream.</summary>
    DecodeError,

    /// <summary>Network error loading track.</summary>
    NetworkError,

    /// <summary>Track not available (region restrictions, etc.).</summary>
    TrackUnavailable,

    /// <summary>Unknown error.</summary>
    Unknown
}
