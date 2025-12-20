using System.IO.Compression;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Player;

namespace Wavee.Connect;

/// <summary>
/// Helper methods for playback state conversion and cluster message processing.
/// </summary>
/// <remarks>
/// WHY: Encapsulates protobuf → domain model conversion and gzip handling.
/// Keeps PlaybackStateManager focused on state management and observables.
///
/// PATTERN: Static helper class similar to ConnectStateHelpers.
/// </remarks>
public static class PlaybackStateHelpers
{
    // Position change threshold (100ms matches librespot for seek detection)
    private const long PositionChangeThresholdMs = 100;

    /// <summary>
    /// Tries to parse a dealer message as a ClusterUpdate protobuf.
    /// Handles gzip decompression if Transfer-Encoding header indicates compression.
    /// </summary>
    /// <param name="message">Dealer message from DealerClient.Messages.</param>
    /// <param name="clusterUpdate">Parsed ClusterUpdate (if successful).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseClusterUpdate(DealerMessage message, out ClusterUpdate? clusterUpdate)
    {
        clusterUpdate = null;

        try
        {
            var payload = message.Payload;

            // Check if payload is gzipped
            if (message.Headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                payload = DecompressGzip(payload);
            }

            // Parse protobuf
            clusterUpdate = ClusterUpdate.Parser.ParseFrom(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to parse a dealer message as a Cluster protobuf (used by PUT state responses).
    /// Handles gzip decompression if Transfer-Encoding header indicates compression.
    /// </summary>
    /// <param name="message">Dealer message from PUT state response.</param>
    /// <param name="cluster">Parsed Cluster (if successful).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseCluster(DealerMessage message, out Cluster? cluster)
    {
        cluster = null;

        try
        {
            var payload = message.Payload;

            // Check if payload is gzipped
            if (message.Headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                payload = DecompressGzip(payload);
            }

            // Parse protobuf as Cluster (not ClusterUpdate)
            cluster = Cluster.Parser.ParseFrom(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decompresses gzipped payload using GZipStream.
    /// </summary>
    /// <param name="gzippedData">Gzipped byte array.</param>
    /// <returns>Decompressed byte array.</returns>
    public static byte[] DecompressGzip(byte[] gzippedData)
    {
        using var inputStream = new MemoryStream(gzippedData);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();

        gzipStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    /// <summary>
    /// Converts a Cluster protobuf to a PlaybackState domain model.
    /// </summary>
    /// <param name="cluster">Cluster from dealer message or PUT state response.</param>
    /// <param name="previousState">Previous state for change detection (null for initial state).</param>
    /// <returns>New PlaybackState with change flags set.</returns>
    public static PlaybackState ClusterToPlaybackState(Cluster cluster, PlaybackState? previousState)
    {
        var playerState = cluster.PlayerState;

        // Extract track info (null if no track)
        var track = playerState?.Track != null
            ? ExtractTrackInfo(playerState.Track)
            : null;

        // Determine playback status
        var status = playerState != null
            ? DeterminePlaybackStatus(playerState)
            : PlaybackStatus.Stopped;

        // Extract playback options
        var options = playerState?.Options != null
            ? new PlaybackOptions
            {
                Shuffling = playerState.Options.ShufflingContext,
                RepeatingContext = playerState.Options.RepeatingContext,
                RepeatingTrack = playerState.Options.RepeatingTrack
            }
            : new PlaybackOptions();

        // Create new state
        var newState = new PlaybackState
        {
            Track = track,
            PositionMs = playerState?.PositionAsOfTimestamp ?? 0,
            DurationMs = playerState?.Duration ?? 0,
            Status = status,
            ContextUri = playerState?.ContextUri,
            Options = options,
            ActiveDeviceId = cluster.ActiveDeviceId,
            Timestamp = cluster.ChangedTimestampMs,
            Source = StateSource.Cluster
        };

        // Detect changes
        var changes = DetectChanges(previousState, newState);
        return newState with { Changes = changes };
    }

    /// <summary>
    /// Converts a ClusterUpdate protobuf to a PlaybackState domain model.
    /// This is a convenience overload that extracts the Cluster from ClusterUpdate.
    /// </summary>
    /// <param name="clusterUpdate">ClusterUpdate from dealer message.</param>
    /// <param name="previousState">Previous state for change detection (null for initial state).</param>
    /// <returns>New PlaybackState with change flags set.</returns>
    public static PlaybackState ClusterToPlaybackState(ClusterUpdate clusterUpdate, PlaybackState? previousState)
    {
        return ClusterToPlaybackState(clusterUpdate.Cluster, previousState);
    }

    /// <summary>
    /// Converts a LocalPlaybackState (from IPlaybackEngine) to PlaybackState domain model.
    /// </summary>
    /// <param name="localState">Local playback state from audio engine.</param>
    /// <param name="previousState">Previous state for change detection (null for initial state).</param>
    /// <param name="activeDeviceId">This device's ID.</param>
    /// <returns>New PlaybackState with change flags set.</returns>
    public static PlaybackState LocalToPlaybackState(
        LocalPlaybackState localState,
        PlaybackState? previousState,
        string activeDeviceId)
    {
        // Determine status from local flags
        var status = localState.IsBuffering ? PlaybackStatus.Buffering :
                     localState.IsPlaying ? PlaybackStatus.Playing :
                     localState.IsPaused ? PlaybackStatus.Paused :
                     PlaybackStatus.Stopped;

        // Create track info if track URI present
        TrackInfo? track = null;
        if (!string.IsNullOrEmpty(localState.TrackUri))
        {
            track = new TrackInfo
            {
                Uri = localState.TrackUri,
                Uid = localState.TrackUid,
                Metadata = new Dictionary<string, string>()
            };
        }

        var options = new PlaybackOptions
        {
            Shuffling = localState.Shuffling,
            RepeatingContext = localState.RepeatingContext,
            RepeatingTrack = localState.RepeatingTrack
        };

        var newState = new PlaybackState
        {
            Track = track,
            PositionMs = localState.PositionMs,
            DurationMs = localState.DurationMs,
            Status = status,
            ContextUri = localState.ContextUri,
            Options = options,
            ActiveDeviceId = activeDeviceId,
            Timestamp = localState.Timestamp,
            Source = StateSource.Local
        };

        // Detect changes
        var changes = DetectChanges(previousState, newState);
        return newState with { Changes = changes };
    }

    /// <summary>
    /// Converts PlaybackState domain model to PlayerState protobuf for publishing.
    /// Used when publishing local state via PutStateAsync.
    /// CRITICAL: Uses triple-flag pattern for Spotify UI compatibility.
    /// </summary>
    /// <param name="state">Domain playback state.</param>
    /// <returns>PlayerState protobuf message.</returns>
    public static PlayerState ToPlayerState(PlaybackState state)
    {
        var playerState = new PlayerState
        {
            Timestamp = state.Timestamp,
            ContextUri = state.ContextUri ?? string.Empty,
            PositionAsOfTimestamp = state.PositionMs,
            Duration = state.DurationMs,

            // CRITICAL: Triple-flag pattern for Spotify UI compatibility
            // When paused, Spotify UI requires ALL flags to be true!
            // See librespot/connect/src/state.rs:337-348
            IsPlaying = state.Status == PlaybackStatus.Playing || state.Status == PlaybackStatus.Paused,
            IsPaused = state.Status == PlaybackStatus.Paused || state.Status == PlaybackStatus.Stopped,
            IsBuffering = state.Status == PlaybackStatus.Buffering || state.Status == PlaybackStatus.Paused,

            PlaybackSpeed = state.Status == PlaybackStatus.Paused ? 0.0 : 1.0,
            Options = new ContextPlayerOptions
            {
                ShufflingContext = state.Options.Shuffling,
                RepeatingContext = state.Options.RepeatingContext,
                RepeatingTrack = state.Options.RepeatingTrack
            }
        };

        // Add track if present
        if (state.Track != null)
        {
            playerState.Track = new ProvidedTrack
            {
                Uri = state.Track.Uri,
                Uid = state.Track.Uid ?? string.Empty
            };

            // Add metadata if available
            foreach (var (key, value) in state.Track.Metadata)
            {
                playerState.Track.Metadata[key] = value;
            }
        }

        return playerState;
    }

    /// <summary>
    /// Extracts TrackInfo from ProvidedTrack protobuf.
    /// </summary>
    private static TrackInfo ExtractTrackInfo(ProvidedTrack providedTrack)
    {
        var metadata = providedTrack.Metadata;

        // Extract common metadata fields (Spotify uses various key names)
        metadata.TryGetValue("title", out var title);
        metadata.TryGetValue("artist_name", out var artist);
        metadata.TryGetValue("album_title", out var album);
        metadata.TryGetValue("image_url", out var imageUrl);

        // Try alternative metadata keys
        title ??= metadata.GetValueOrDefault("track_name");
        artist ??= metadata.GetValueOrDefault("artist");
        album ??= metadata.GetValueOrDefault("album");
        imageUrl ??= metadata.GetValueOrDefault("image_xlarge_url");
        imageUrl ??= metadata.GetValueOrDefault("image_large_url");

        return new TrackInfo
        {
            Uri = providedTrack.Uri,
            Uid = providedTrack.Uid,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumUri = providedTrack.AlbumUri,
            ArtistUri = providedTrack.ArtistUri,
            ImageUrl = imageUrl,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Determines playback status from PlayerState protobuf.
    /// CRITICAL: Spotify uses a triple-flag pattern for UI compatibility.
    /// When paused: IsPlaying=true, IsPaused=true, IsBuffering=true (all true!)
    /// See librespot/connect/src/state.rs:337-348 for details.
    /// </summary>
    private static PlaybackStatus DeterminePlaybackStatus(PlayerState playerState)
    {
        // CRITICAL: Check IsPaused FIRST before IsPlaying!
        // When paused, Spotify sets ALL three flags to true for UI compatibility.
        // Paused: is_playing=true, is_paused=true, is_buffering=true (triple-flag pattern)
        if (playerState.IsPaused)
            return PlaybackStatus.Paused;

        // Buffering during playback: is_playing=true, is_paused=false, is_buffering=true
        if (playerState.IsBuffering)
            return PlaybackStatus.Buffering;

        // Playing: is_playing=true, is_paused=false, is_buffering=false
        if (playerState.IsPlaying)
            return PlaybackStatus.Playing;

        // Stopped: is_playing=false
        return PlaybackStatus.Stopped;
    }

    /// <summary>
    /// Detects what changed between previous and new state.
    /// Returns change flags for efficient filtering in observables.
    /// </summary>
    /// <param name="previous">Previous state (null for initial state).</param>
    /// <param name="current">New current state.</param>
    /// <returns>Change flags indicating what changed.</returns>
    public static StateChanges DetectChanges(PlaybackState? previous, PlaybackState current)
    {
        // Initial state or no previous state
        if (previous == null)
            return StateChanges.All;

        var changes = StateChanges.None;

        // Track changed (different URI)
        if (previous.Track?.Uri != current.Track?.Uri)
            changes |= StateChanges.Track;

        // SMART POSITION DETECTION (librespot pattern)
        // Calculate "nominal start time" (when playback started) to detect seeks vs natural progression
        // During normal playback, nominal start time stays constant even as position advances
        // A seek causes nominal start time to change significantly
        var prevNominalStart = previous.Timestamp - previous.PositionMs;
        var currNominalStart = current.Timestamp - current.PositionMs;
        var nominalDelta = Math.Abs(currNominalStart - prevNominalStart);

        // Only detect position change if nominal start time changed significantly
        // This filters out natural playback progression (which keeps nominal start constant)
        if (nominalDelta > PositionChangeThresholdMs)
            changes |= StateChanges.Position;

        // Status changed
        if (previous.Status != current.Status)
            changes |= StateChanges.Status;

        // Context changed
        if (previous.ContextUri != current.ContextUri)
            changes |= StateChanges.Context;

        // Options changed
        if (previous.Options.Shuffling != current.Options.Shuffling ||
            previous.Options.RepeatingContext != current.Options.RepeatingContext ||
            previous.Options.RepeatingTrack != current.Options.RepeatingTrack)
            changes |= StateChanges.Options;

        // Active device changed
        if (previous.ActiveDeviceId != current.ActiveDeviceId)
            changes |= StateChanges.ActiveDevice;

        // Source changed (cluster → local or vice versa)
        if (previous.Source != current.Source)
            changes |= StateChanges.Source;

        // PRIORITY: If status changed, suppress position changes (status is more significant)
        // This prevents "Position" spam when pausing/resuming
        if (changes.HasFlag(StateChanges.Status))
            changes &= ~StateChanges.Position;

        return changes;
    }

    /// <summary>
    /// Calculates the current playback position based on state and elapsed time.
    /// Only valid when status is Playing.
    /// </summary>
    /// <param name="state">Current playback state.</param>
    /// <returns>Estimated current position in milliseconds.</returns>
    public static long CalculateCurrentPosition(PlaybackState state)
    {
        if (state.Status != PlaybackStatus.Playing)
            return state.PositionMs;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsed = now - state.Timestamp;
        var estimatedPosition = state.PositionMs + elapsed;

        // Clamp to duration
        return Math.Min(estimatedPosition, state.DurationMs);
    }
}
