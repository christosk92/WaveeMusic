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
        var ps = cluster.PlayerState;
        var prev = previousState ?? PlaybackState.Empty;

        // Look up active device info from cluster device map
        string? activeDeviceName = null;
        uint volume = 0;
        bool isVolumeRestricted = false;
        if (!string.IsNullOrEmpty(cluster.ActiveDeviceId) &&
            cluster.Device.TryGetValue(cluster.ActiveDeviceId, out var activeDeviceInfo))
        {
            activeDeviceName = activeDeviceInfo.Name;
            volume = activeDeviceInfo.Volume;
            isVolumeRestricted = activeDeviceInfo.Capabilities?.DisableVolume ?? false;
        }

        // Merge: start from previous state, override only fields with real values
        var newState = prev with
        {
            Track = ps?.Track != null ? ExtractTrackInfo(ps.Track) : prev.Track,
            Status = ps != null ? DeterminePlaybackStatus(ps) : PlaybackStatus.Stopped,
            PositionMs = ps?.PositionAsOfTimestamp ?? prev.PositionMs,
            DurationMs = ps?.Duration > 0 ? ps.Duration : prev.DurationMs,
            ContextUri = !string.IsNullOrEmpty(ps?.ContextUri) ? ps!.ContextUri : prev.ContextUri,
            Options = ps?.Options != null
                ? new PlaybackOptions
                {
                    Shuffling = ps.Options.ShufflingContext,
                    RepeatingContext = ps.Options.RepeatingContext,
                    RepeatingTrack = ps.Options.RepeatingTrack
                }
                : prev.Options,
            // Always from cluster
            ActiveDeviceId = cluster.ActiveDeviceId,
            ActiveDeviceName = activeDeviceName,
            Volume = volume,
            IsVolumeRestricted = isVolumeRestricted,
            Timestamp = cluster.ChangedTimestampMs,
            Source = StateSource.Cluster,
        };

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

        // Create track info if track URI present, otherwise carry forward previous
        var prev = previousState ?? PlaybackState.Empty;
        var hasTrack = !string.IsNullOrEmpty(localState.TrackUri);

        // Build track info only if engine has a real track
        TrackInfo? track = hasTrack
            ? new TrackInfo
            {
                Uri = localState.TrackUri,
                Uid = localState.TrackUid,
                AlbumUri = localState.AlbumUri,
                ArtistUri = localState.ArtistUri,
                Title = localState.TrackTitle,
                Artist = localState.TrackArtist,
                Album = localState.TrackAlbum,
                ImageSmallUrl = localState.ImageSmallUrl,
                ImageUrl = localState.ImageUrl,
                ImageLargeUrl = localState.ImageLargeUrl,
                ImageXLargeUrl = localState.ImageXLargeUrl,
                Metadata = new Dictionary<string, string>
                {
                    ["title"] = localState.TrackTitle ?? "",
                    ["artist_name"] = localState.TrackArtist ?? "",
                    ["album_title"] = localState.TrackAlbum ?? "",
                }
            }
            : null;

        // Generate new SessionId when track changes, otherwise carry over
        var isNewTrack = prev.Track?.Uri != localState.TrackUri;
        var sessionId = isNewTrack || prev.SessionId == null
            ? Guid.NewGuid().ToString("N")
            : prev.SessionId;

        // Merge: start from previous state, override only fields with real values
        var newState = prev with
        {
            Track = track ?? prev.Track,
            PositionMs = hasTrack ? localState.PositionMs : prev.PositionMs,
            DurationMs = localState.DurationMs > 0 ? localState.DurationMs : prev.DurationMs,
            Status = status,
            ContextUri = !string.IsNullOrEmpty(localState.ContextUri) ? localState.ContextUri : prev.ContextUri,
            ContextUrl = !string.IsNullOrEmpty(localState.ContextUrl) ? localState.ContextUrl : prev.ContextUrl,
            CurrentIndex = localState.CurrentIndex,
            PrevTracks = localState.PrevTracks,
            NextTracks = localState.NextTracks,
            QueueRevision = localState.QueueRevision,
            Options = new PlaybackOptions
            {
                Shuffling = localState.Shuffling,
                RepeatingContext = localState.RepeatingContext,
                RepeatingTrack = localState.RepeatingTrack
            },
            ActiveDeviceId = activeDeviceId,
            Timestamp = localState.Timestamp,
            Source = StateSource.Local,
            SessionId = sessionId,
            CanSeek = localState.CanSeek
        };

        var changes = DetectChanges(previousState, newState);
        return newState with { Changes = changes };
    }

    /// <summary>
    /// Converts PlaybackState domain model to PlayerState protobuf for publishing.
    /// Used when publishing local state via PutStateAsync.
    /// CRITICAL: Uses triple-flag pattern for Spotify UI compatibility.
    /// </summary>
    /// <param name="state">Domain playback state.</param>
    /// <param name="deviceId">This device's ID for play_origin.</param>
    /// <returns>PlayerState protobuf message.</returns>
    public static PlayerState ToPlayerState(PlaybackState state, string deviceId)
    {
        var playerState = new PlayerState
        {
            Timestamp = state.Timestamp,
            // When no playlist/album context, the track itself is the context
            ContextUri = state.ContextUri ?? state.Track?.Uri ?? string.Empty,
            // ContextUrl format: "context://<uri>" (from librespot)
            ContextUrl = state.ContextUrl ?? string.Empty,
            Position = state.PositionMs,
            PositionAsOfTimestamp = state.PositionMs,
            Duration = state.DurationMs,

            // CRITICAL: Triple-flag pattern for Spotify UI compatibility
            // When paused, Spotify UI requires ALL flags to be true!
            // See librespot/connect/src/state.rs:337-348
            IsPlaying = state.Status == PlaybackStatus.Playing || state.Status == PlaybackStatus.Paused,
            IsPaused = state.Status == PlaybackStatus.Paused || state.Status == PlaybackStatus.Stopped,
            IsBuffering = state.Status == PlaybackStatus.Buffering || state.Status == PlaybackStatus.Paused,

            PlaybackSpeed = state.Status == PlaybackStatus.Paused ? 0.0 : 1.0,

            // CRITICAL: Required fields that librespot sets (missing causes Spotify to ignore state)
            PlaybackId = Guid.NewGuid().ToString("N"),  // 32-char hex, new per publish
            SessionId = state.SessionId ?? Guid.NewGuid().ToString("N"),
            QueueRevision = state.QueueRevision ?? string.Empty,
            IsSystemInitiated = true,

            // Play origin - required for Spotify to recognize source
            PlayOrigin = new PlayOrigin
            {
                DeviceIdentifier = deviceId,
                FeatureIdentifier = "wavee"
            },

            // Context index - actual position in queue
            Index = new ContextIndex { Page = 0, Track = (uint)Math.Max(0, state.CurrentIndex) },

            // Suppressions - required empty message
            Suppressions = new Suppressions(),

            // CRITICAL: Restrictions control which buttons are enabled on remote devices
            Restrictions = BuildRestrictions(state),
            ContextRestrictions = BuildRestrictions(state),

            Options = new ContextPlayerOptions
            {
                ShufflingContext = state.Options.Shuffling,
                RepeatingContext = state.Options.RepeatingContext,
                RepeatingTrack = state.Options.RepeatingTrack
            },

            // Playback quality info (prevents incorrect "Lossless" display on web player)
            PlaybackQuality = new PlaybackQuality
            {
                BitrateLevel = BitrateLevel.Veryhigh,      // 320kbps OGG Vorbis
                Strategy = BitrateStrategy.BestMatching,
                TargetBitrateLevel = BitrateLevel.Veryhigh,
                TargetBitrateAvailable = true,
                HifiStatus = HiFiStatus.Off                 // Not lossless
            }
        };

        // Add track if present
        if (state.Track != null)
        {
            playerState.Track = new ProvidedTrack
            {
                Uri = state.Track.Uri,
                Uid = state.Track.Uid ?? string.Empty,
                Provider = "context",  // Required: indicates track source
                AlbumUri = state.Track.AlbumUri ?? string.Empty,
                ArtistUri = state.Track.ArtistUri ?? string.Empty
            };

            // Add base metadata from state.Track.Metadata
            foreach (var (key, value) in state.Track.Metadata)
            {
                playerState.Track.Metadata[key] = value;
            }

            // Enrich with all expected metadata fields for Spotify Connect display
            var meta = playerState.Track.Metadata;

            // Basic track info (ensure populated even if already in Metadata)
            if (!string.IsNullOrEmpty(state.Track.Title))
                meta["title"] = state.Track.Title;
            if (!string.IsNullOrEmpty(state.Track.Artist))
                meta["artist_name"] = state.Track.Artist;
            if (!string.IsNullOrEmpty(state.Track.Album))
                meta["album_title"] = state.Track.Album;

            // URIs for rich display
            if (!string.IsNullOrEmpty(state.Track.AlbumUri))
                meta["album_uri"] = state.Track.AlbumUri;
            if (!string.IsNullOrEmpty(state.Track.ArtistUri))
                meta["artist_uri"] = state.Track.ArtistUri;
            if (!string.IsNullOrEmpty(state.ContextUri))
            {
                meta["context_uri"] = state.ContextUri;
                meta["entity_uri"] = state.ContextUri;
            }

            // Image URLs in spotify:image: format
            if (!string.IsNullOrEmpty(state.Track.ImageSmallUrl))
                meta["image_small_url"] = state.Track.ImageSmallUrl;
            if (!string.IsNullOrEmpty(state.Track.ImageUrl))
                meta["image_url"] = state.Track.ImageUrl;
            if (!string.IsNullOrEmpty(state.Track.ImageLargeUrl))
                meta["image_large_url"] = state.Track.ImageLargeUrl;
            if (!string.IsNullOrEmpty(state.Track.ImageXLargeUrl))
                meta["image_xlarge_url"] = state.Track.ImageXLargeUrl;

            // Playback hints
            meta["track_player"] = "audio";
            meta["view_index"] = state.CurrentIndex.ToString();
            meta["iteration"] = "0";
            meta["media.start_position"] = state.PositionMs.ToString();

            // Skip actions
            meta["actions.skipping_next_past_track"] = "resume";
            meta["actions.skipping_prev_past_track"] = "resume";
        }

        // Add previous tracks (up to 16)
        foreach (var track in state.PrevTracks.Take(16))
        {
            var pt = new ProvidedTrack
            {
                Uri = track.Uri,
                Uid = track.Uid,
                Provider = track.IsUserQueued ? "queue" : "context",
                AlbumUri = track.AlbumUri ?? string.Empty,
                ArtistUri = track.ArtistUri ?? string.Empty
            };
            playerState.PrevTracks.Add(pt);
        }

        // Add next tracks (user queue + up to 48 context)
        foreach (var track in state.NextTracks.Take(48))
        {
            var pt = new ProvidedTrack
            {
                Uri = track.Uri,
                Uid = track.Uid,
                Provider = track.IsUserQueued ? "queue" : "context",
                AlbumUri = track.AlbumUri ?? string.Empty,
                ArtistUri = track.ArtistUri ?? string.Empty
            };
            playerState.NextTracks.Add(pt);
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

        // Try alternative metadata keys
        title ??= metadata.GetValueOrDefault("track_name");
        artist ??= metadata.GetValueOrDefault("artist");
        album ??= metadata.GetValueOrDefault("album");

        // Extract all image size variants
        metadata.TryGetValue("image_url", out var imageUrl);
        metadata.TryGetValue("image_small_url", out var imageSmallUrl);
        metadata.TryGetValue("image_large_url", out var imageLargeUrl);
        metadata.TryGetValue("image_xlarge_url", out var imageXLargeUrl);

        // Fallback for default image
        imageUrl ??= imageXLargeUrl ?? imageLargeUrl ?? imageSmallUrl;

        return new TrackInfo
        {
            Uri = providedTrack.Uri,
            Uid = providedTrack.Uid,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumUri = !string.IsNullOrEmpty(providedTrack.AlbumUri)
                ? providedTrack.AlbumUri
                : metadata.GetValueOrDefault("album_uri"),
            ArtistUri = !string.IsNullOrEmpty(providedTrack.ArtistUri)
                ? providedTrack.ArtistUri
                : metadata.GetValueOrDefault("artist_uri"),
            ImageUrl = imageUrl,
            ImageSmallUrl = imageSmallUrl,
            ImageLargeUrl = imageLargeUrl,
            ImageXLargeUrl = imageXLargeUrl,
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
        // Log the raw flags for debugging
        System.Diagnostics.Debug.WriteLine(
            $"[PlaybackStatus] Raw flags: IsPlaying={playerState.IsPlaying}, IsPaused={playerState.IsPaused}, IsBuffering={playerState.IsBuffering}");

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

        // Volume changed
        if (previous.Volume != current.Volume || previous.IsVolumeRestricted != current.IsVolumeRestricted)
            changes |= StateChanges.Volume;

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
    /// <param name="nowMs">
    /// Optional corrected "now" timestamp in Unix milliseconds.
    /// When provided (e.g. from <see cref="Core.Time.SpotifyClockService.NowMs"/>),
    /// compensates for local-vs-server clock skew. Falls back to local clock if null.
    /// </param>
    /// <returns>Estimated current position in milliseconds.</returns>
    public static long CalculateCurrentPosition(PlaybackState state, long? nowMs = null)
    {
        if (state.Status != PlaybackStatus.Playing)
            return state.PositionMs;

        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsed = now - state.Timestamp;
        var estimatedPosition = state.PositionMs + elapsed;

        // Clamp to duration
        return Math.Min(estimatedPosition, state.DurationMs);
    }

    /// <summary>
    /// Builds restrictions based on playback state.
    /// Controls which buttons are enabled on remote devices.
    /// </summary>
    /// <param name="state">Current playback state.</param>
    /// <returns>Restrictions protobuf message.</returns>
    private static Restrictions BuildRestrictions(PlaybackState state)
    {
        var restrictions = new Restrictions();

        // Disallow resuming when playing (not paused)
        if (state.Status == PlaybackStatus.Playing)
        {
            restrictions.DisallowResumingReasons.Add("not_paused");
        }

        // Disable seeking for infinite streams (radio, live streams)
        if (!state.CanSeek)
        {
            restrictions.DisallowSeekingReasons.Add("streaming_rules");
        }

        // If at first track and not repeating context, disallow skip_prev
        if (state.CurrentIndex == 0 && !state.Options.RepeatingContext)
        {
            restrictions.DisallowSkippingPrevReasons.Add("no_prev_track");
        }

        // If at last track (no next tracks) and not repeating context, disallow skip_next
        if (state.NextTracks.Count == 0 && !state.Options.RepeatingContext)
        {
            restrictions.DisallowSkippingNextReasons.Add("no_next_track");
        }

        return restrictions;
    }
}
