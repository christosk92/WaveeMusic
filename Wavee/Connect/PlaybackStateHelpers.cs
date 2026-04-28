using System.IO.Compression;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Wavee.Audio.Queue;
using Wavee.Connect.Protocol;
using Wavee.Core;
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
    /// Extracts a list of <see cref="QueueTrack"/> from a repeated ProvidedTrack field.
    /// Filters out control markers (spotify:meta:*, spotify:delimiter).
    /// </summary>
    public static IReadOnlyList<IQueueItem> ExtractQueueItems(RepeatedField<ProvidedTrack> providedTracks)
    {
        var result = new List<IQueueItem>(providedTracks.Count);
        foreach (var pt in providedTracks)
        {
            if (string.IsNullOrEmpty(pt.Uri))
                continue;

            var metadata = pt.Metadata;
            var provider = !string.IsNullOrEmpty(pt.Provider) ? pt.Provider : "context";

            // Spotify marks look-ahead queue items (subsequent repeat-context iterations,
            // autoplay pre-seeds, etc.) with metadata.hidden="true". These are not meant
            // to be surfaced in the UI — without filtering, a 5-track "internal queue"
            // balloons to ~80 entries (12 iterations × 6 items w/ delimiters).
            if (metadata.TryGetValue("hidden", out var hidden)
                && string.Equals(hidden, "true", StringComparison.OrdinalIgnoreCase))
                continue;

            // Page marker: spotify:meta:page:N
            if (pt.Uri.StartsWith("spotify:meta:page:", StringComparison.Ordinal))
            {
                var pageStr = pt.Uri["spotify:meta:page:".Length..];
                int.TryParse(pageStr, out var pageNum);
                result.Add(new QueuePageMarker(pageNum, provider));
                continue;
            }

            // Delimiter: spotify:delimiter
            if (pt.Uri.StartsWith("spotify:delimiter", StringComparison.Ordinal))
            {
                metadata.TryGetValue("actions.advancing_past_track", out var advanceAction);
                metadata.TryGetValue("actions.skipping_next_past_track", out var skipAction);
                result.Add(new QueueDelimiter(advanceAction ?? "pause", skipAction ?? "pause", provider));
                continue;
            }

            // Regular track
            metadata.TryGetValue("title", out var title);
            metadata.TryGetValue("artist_name", out var artist);
            metadata.TryGetValue("album_title", out var album);

            metadata.TryGetValue("image_url", out var imageUrl);
            imageUrl ??= metadata.GetValueOrDefault("image_xlarge_url")
                      ?? metadata.GetValueOrDefault("image_large_url")
                      ?? metadata.GetValueOrDefault("image_small_url");

            metadata.TryGetValue("duration", out var durationStr);
            int.TryParse(durationStr, out var durationMs);

            if (metadata.TryGetValue("is_queued", out var isQueued) && isQueued == "true")
                provider = "queue";
            else if (metadata.TryGetValue("autoplay.is_autoplay", out var isAuto) && isAuto == "true")
                provider = "autoplay";

            result.Add(new QueueTrack(
                Uri: pt.Uri,
                Uid: pt.Uid,
                Title: title,
                Artist: artist,
                Album: album,
                AlbumUri: !string.IsNullOrEmpty(pt.AlbumUri) ? pt.AlbumUri : metadata.GetValueOrDefault("album_uri"),
                ArtistUri: !string.IsNullOrEmpty(pt.ArtistUri) ? pt.ArtistUri : metadata.GetValueOrDefault("artist_uri"),
                DurationMs: durationMs > 0 ? durationMs : null,
                ImageUrl: imageUrl,
                IsUserQueued: provider == "queue",
                Provider: provider
            ));
        }
        return result;
    }

    /// <summary>
    /// Converts a Cluster protobuf to a PlaybackState domain model.
    /// </summary>
    /// <param name="cluster">Cluster from dealer message or PUT state response.</param>
    /// <param name="previousState">Previous state for change detection (null for initial state).</param>
    /// <returns>New PlaybackState with change flags set.</returns>
    public static PlaybackState ClusterToPlaybackState(Cluster cluster, PlaybackState? previousState, ILogger? logger = null)
    {
        var ps = cluster.PlayerState;
        var prev = previousState ?? PlaybackState.Empty;

        // Look up active device info from cluster device map
        string? activeDeviceName = null;
        uint volume = 0;
        bool isVolumeRestricted = false;
        Core.Session.DeviceType activeDeviceType = Core.Session.DeviceType.Computer;
        if (!string.IsNullOrEmpty(cluster.ActiveDeviceId) &&
            cluster.Device.TryGetValue(cluster.ActiveDeviceId, out var activeDeviceInfo))
        {
            activeDeviceName = activeDeviceInfo.Name;
            volume = activeDeviceInfo.Volume;
            isVolumeRestricted = activeDeviceInfo.Capabilities?.DisableVolume ?? false;
            activeDeviceType = (Core.Session.DeviceType)(int)activeDeviceInfo.DeviceType;
        }

        // Build the list of Spotify Connect devices visible in this cluster.
        // Consumers filter out the local device themselves when presenting the UI.
        var availableConnectDevices = ExtractConnectDevices(cluster);

        // Extract rich queue tracks from cluster state
        logger?.LogDebug("ClusterToPlaybackState: ps null={PsNull}, ps.PrevTracks={PrevCount}, ps.NextTracks={NextCount}",
            ps == null, ps?.PrevTracks?.Count ?? -1, ps?.NextTracks?.Count ?? -1);

        var prevQueue = ps?.PrevTracks.Count > 0 ? ExtractQueueItems(ps.PrevTracks) : prev.PrevQueue;
        var nextQueue = ps?.NextTracks.Count > 0 ? ExtractQueueItems(ps.NextTracks) : prev.NextQueue;

        logger?.LogDebug("Queue extraction: prevQueue={PrevCount}, nextQueue={NextCount}",
            prevQueue.Count, nextQueue.Count);

        // Derive thin TrackReference lists from tracks only (skip page markers/delimiters)
        var prevTracks = prevQueue.Count > 0
            ? prevQueue.OfType<QueueTrack>().Select(q => new TrackReference(q.Uri, q.Uid ?? "", q.AlbumUri, q.ArtistUri, q.IsUserQueued)).ToList()
            : (IReadOnlyList<TrackReference>)prev.PrevTracks;
        var nextTracks = nextQueue.Count > 0
            ? nextQueue.OfType<QueueTrack>().Select(q => new TrackReference(q.Uri, q.Uid ?? "", q.AlbumUri, q.ArtistUri, q.IsUserQueued)).ToList()
            : (IReadOnlyList<TrackReference>)prev.NextTracks;

        // Merge: start from previous state, override only fields with real values
        var newState = prev with
        {
            Track = ps?.Track != null ? ExtractTrackInfo(ps.Track) : prev.Track,
            Status = ps != null ? DeterminePlaybackStatus(ps) : PlaybackStatus.Stopped,
            PositionMs = ps?.PositionAsOfTimestamp ?? prev.PositionMs,
            DurationMs = ps?.Duration > 0 ? ps.Duration : prev.DurationMs,
            ContextUri = !string.IsNullOrEmpty(ps?.ContextUri) ? ps!.ContextUri : prev.ContextUri,
            CurrentIndex = (int)(ps?.Index?.Track ?? (uint)prev.CurrentIndex),
            PrevTracks = prevTracks,
            NextTracks = nextTracks,
            PrevQueue = prevQueue,
            NextQueue = nextQueue,
            QueueRevision = !string.IsNullOrEmpty(ps?.QueueRevision) ? ps!.QueueRevision : prev.QueueRevision,
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
            ActiveDeviceType = activeDeviceType,
            AvailableConnectDevices = availableConnectDevices,
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
    public static PlaybackState ClusterToPlaybackState(ClusterUpdate clusterUpdate, PlaybackState? previousState, ILogger? logger = null)
    {
        return ClusterToPlaybackState(clusterUpdate.Cluster, previousState, logger);
    }

    /// <summary>
    /// Extracts the list of Spotify Connect devices visible in a cluster snapshot.
    /// Exposed so <see cref="PlaybackStateManager"/> can propagate device-list changes
    /// independently of the rest of the playback state (which is often suppressed when
    /// the local engine is the authoritative source).
    /// </summary>
    public static IReadOnlyList<ConnectDevice> ExtractConnectDevices(Cluster cluster)
    {
        var list = new List<ConnectDevice>(cluster.Device.Count);
        foreach (var kvp in cluster.Device)
        {
            list.Add(new ConnectDevice(
                DeviceId: kvp.Key,
                Name: kvp.Value.Name ?? string.Empty,
                Type: (Core.Session.DeviceType)(int)kvp.Value.DeviceType,
                IsActive: kvp.Key == cluster.ActiveDeviceId));
        }
        return list;
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

        // Stable IDs per librespot-java semantics:
        //   • SessionId  → one per CONTEXT (album/playlist/artist). Regenerated
        //                  only when the context URI changes. Stays stable
        //                  across the dozens of tracks within a single playlist.
        //   • PlaybackId → one per TRACK. Regenerated on every track change.
        // Both must be stable across the many PutState publishes that happen
        // for a single track (8+/min during normal playback). If either churns
        // per-publish, Spotify's backend can't reconcile the stream into a
        // coherent play and Recently Played / play counts silently break.
        // Empty/whitespace context is treated as "no real context" so internal
        // queue plays keep getting fresh session ids on every flip.
        var newContextUri = localState.ContextUri ?? string.Empty;
        var prevContextUri = prev.ContextUri ?? string.Empty;
        var isContextChanged = !string.IsNullOrEmpty(newContextUri)
                               && !string.Equals(newContextUri, prevContextUri, StringComparison.Ordinal);
        var sessionId = isContextChanged || string.IsNullOrEmpty(prev.SessionId)
            ? Guid.NewGuid().ToString("N")
            : prev.SessionId;

        var isNewTrack = prev.Track?.Uri != localState.TrackUri;
        var playbackId = isNewTrack || string.IsNullOrEmpty(prev.PlaybackId)
            ? Guid.NewGuid().ToString("N")
            : prev.PlaybackId;

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
            PrevTracks = localState.PrevTracks.Count > 0 ? localState.PrevTracks : prev.PrevTracks,
            NextTracks = localState.NextTracks.Count > 0 ? localState.NextTracks : prev.NextTracks,
            PrevQueue = localState.PrevQueueItems.Count > 0 ? localState.PrevQueueItems : prev.PrevQueue,
            NextQueue = localState.NextQueueItems.Count > 0 ? localState.NextQueueItems : prev.NextQueue,
            QueueRevision = !string.IsNullOrEmpty(localState.QueueRevision) ? localState.QueueRevision : prev.QueueRevision,
            Options = new PlaybackOptions
            {
                Shuffling = localState.Shuffling,
                RepeatingContext = localState.RepeatingContext,
                RepeatingTrack = localState.RepeatingTrack
            },
            ActiveDeviceId = !string.IsNullOrEmpty(localState.ActiveDeviceId)
                ? localState.ActiveDeviceId
                : activeDeviceId,
            ActiveDeviceName = localState.ActiveDeviceName ?? prev.ActiveDeviceName,
            ActiveAudioDeviceName = localState.ActiveAudioDeviceName ?? prev.ActiveAudioDeviceName,
            AvailableAudioDevices = localState.AvailableAudioDevices ?? prev.AvailableAudioDevices,
            Volume = localState.Volume != 0 || prev.Volume == 0
                ? localState.Volume
                : prev.Volume,
            IsVolumeRestricted = localState.IsVolumeRestricted,
            Timestamp = localState.Timestamp,
            Source = localState.Source ?? StateSource.Local,
            SessionId = sessionId,
            PlaybackId = playbackId,
            CanSeek = localState.CanSeek,
            IsSystemInitiated = localState.IsSystemInitiated,
            ContextDescription = !string.IsNullOrEmpty(localState.ContextDescription)
                ? localState.ContextDescription
                : prev.ContextDescription,
            ContextImageUrl = !string.IsNullOrEmpty(localState.ContextImageUrl)
                ? localState.ContextImageUrl
                : prev.ContextImageUrl,
            ContextFeature = !string.IsNullOrEmpty(localState.ContextFeature)
                ? localState.ContextFeature
                : prev.ContextFeature,
            ContextTrackCount = localState.ContextTrackCount ?? prev.ContextTrackCount,
            ContextFormatAttributes = localState.ContextFormatAttributes ?? prev.ContextFormatAttributes,
            // localState carries the orchestrator's authoritative page count
            // (set from ContextLoadResult.PageCount). Overwrite unconditionally
            // — on PlayAsync the value is reset to 1, then advanced once the
            // resolver responds with the actual page count.
            ContextPageCount = localState.ContextPageCount
        };

        // The AudioHost supplies UpstreamChanges as a hint derived from its own
        // _lastSentState bookkeeping. That bookkeeping is stale on the very first
        // PublishState after cold start (no prior snapshot to diff against), so it
        // silently misses the Track flag on the first local PlayAsync — that caused
        // the "first track change doesn't update UI" bug. Always OR the fresh local
        // diff with the upstream hint so track/status transitions can never be lost,
        // regardless of which side's comparator saw them first.
        var detected = DetectChanges(previousState, newState);
        var changes = (localState.UpstreamChanges ?? StateChanges.None) | detected;
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
        var contextUri = state.ContextUri ?? state.Track?.Uri ?? string.Empty;

        var playerState = new PlayerState
        {
            Timestamp = state.Timestamp,
            // When no playlist/album context, the track itself is the context
            ContextUri = contextUri,
            // context://<uri> — librespot emits this; web player uses it for deep-links.
            // Prefer engine-supplied ContextUrl, otherwise synthesize from ContextUri.
            ContextUrl = !string.IsNullOrEmpty(state.ContextUrl)
                ? state.ContextUrl
                : (!string.IsNullOrEmpty(contextUri) ? $"context://{contextUri}" : string.Empty),
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

            // CRITICAL: Required fields that librespot sets (missing causes Spotify to ignore state).
            // PlaybackId / SessionId now flow from PlaybackState — stable per-track
            // and per-context respectively (see merge logic in MergeWithLocalState).
            // Spotify's backend reconciles play history by these IDs; minting fresh
            // values per publish silently breaks Recently Played and play counts.
            // Fallback Guid generation here only kicks in for synthetic/test states
            // that never went through MergeWithLocalState — never in normal flow.
            PlaybackId = state.PlaybackId ?? Guid.NewGuid().ToString("N"),
            SessionId = state.SessionId ?? Guid.NewGuid().ToString("N"),
            QueueRevision = state.QueueRevision ?? string.Empty,
            // Only true for autoplay rollover / transfer resume. User-initiated play is false.
            IsSystemInitiated = state.IsSystemInitiated,

            // PlayOrigin tells Spotify *where the user initiated playback from*.
            // Verified from 2026-04-28 desktop SAZ capture: feature_identifier
            // is the canonical short name ("album", "playlist", "artist",
            // "your_library", "home", "search"), NOT a "-page" suffixed form.
            // Recently Played attribution lives off this — unknown identifiers
            // fall outside the server's allowlist and the play is not credited.
            // feature_version must be non-empty (desktop sends an xpui build
            // hash; Wavee uses a stable build identifier).
            // view_uri is the navigation route the user came from; we use the
            // context URI itself as a sensible default when no explicit route
            // tracker is wired through PlaybackState.
            PlayOrigin = new PlayOrigin
            {
                FeatureIdentifier = MapContextFeatureToPlayOrigin(state.ContextFeature),
                FeatureVersion = "wavee_" + SpotifyClientIdentity.DesktopSemver,
                ViewUri = !string.IsNullOrEmpty(state.ContextUri) ? state.ContextUri : "home",
                ReferrerIdentifier = MapContextFeatureToPlayOrigin(state.ContextFeature)
            },

            // Context index - actual position in queue
            Index = new ContextIndex { Page = 0, Track = (uint)Math.Max(0, state.CurrentIndex) },

            // Suppressions - required empty message
            Suppressions = new Suppressions(),

            // Track/player-level restrictions (pause/skip/seek gating).
            Restrictions = BuildRestrictions(state),
            // Context-level disables (DJ lock, ads, radio). Empty by default —
            // populated only when the engine reports a real context-level constraint.
            ContextRestrictions = BuildContextRestrictions(state),

            Options = new ContextPlayerOptions
            {
                ShufflingContext = state.Options.Shuffling,
                RepeatingContext = state.Options.RepeatingContext,
                RepeatingTrack = state.Options.RepeatingTrack
                // TODO: ContextPlayerOptions.modes (context_enhancement/media/jam) is a newer
                // Spotify field not yet in our player.proto. Adding it requires the correct
                // field number from Spotify's internal proto; picking one blindly risks
                // breaking server-side parsing. Leave until field numbers can be confirmed.
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
            // TODO: signals / session_command_id / sleep_timer are newer Spotify
            // fields not present in our player.proto. Adding them requires correct
            // field numbers from Spotify's internal proto — deferred until verified.
        };

        // context_metadata — parity with what Spotify desktop emits so remote
        // "Now Playing" cards can show the context subtitle.
        playerState.ContextMetadata["player.arch"] = "2";
        playerState.ContextMetadata["mixer_enabled"] = state.MixerEnabled ? "true" : "false";
        if (!string.IsNullOrEmpty(state.ContextDescription))
        {
            playerState.ContextMetadata["context_description"] = state.ContextDescription;
        }
        if (!string.IsNullOrEmpty(state.ContextImageUrl))
        {
            playerState.ContextMetadata["image_url"] = state.ContextImageUrl;
        }
        if (state.ContextTrackCount is { } trackCount
            && !string.IsNullOrEmpty(state.ContextUri)
            && state.ContextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            // Spotify desktop only sets this key on playlists. Albums/artists
            // omit it and let clients resolve the length themselves.
            playerState.ContextMetadata["playlist_number_of_tracks"] = trackCount.ToString();
            playerState.ContextMetadata["playlist_number_of_episodes"] = "0";
        }
        // Merge playlist-service format attributes verbatim (format, request_id,
        // tag, source-loader, image_url, session_control_display.*, etc.). We
        // don't overwrite keys we set above — if the API supplied an image_url
        // of its own, prefer the typed ContextImageUrl we just wrote.
        if (state.ContextFormatAttributes is { Count: > 0 } contextAttrs)
        {
            foreach (var (key, value) in contextAttrs)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (playerState.ContextMetadata.ContainsKey(key)) continue;
                playerState.ContextMetadata[key] = value ?? string.Empty;
            }
        }

        // Add track if present
        if (state.Track != null)
        {
            // NOTE: intentionally do NOT set top-level AlbumUri/ArtistUri on ProvidedTrack —
            // Spotify desktop keeps those only in the metadata map. Some clients
            // double-read otherwise. Metadata-map population happens below.
            playerState.Track = new ProvidedTrack
            {
                Uri = state.Track.Uri,
                Uid = state.Track.Uid ?? string.Empty,
                Provider = "context"  // Required: indicates track source
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

        // Add previous tracks (up to 16) - prefer rich queue data.
        // view_index counts down from (currentIndex - 1) to 0 for prev tracks.
        {
            var prevItems = state.PrevQueue.Take(16).ToList();
            var viewIndexStart = Math.Max(0, state.CurrentIndex - prevItems.Count);
            for (var i = 0; i < prevItems.Count; i++)
            {
                var pt = QueueItemToProvidedTrack(prevItems[i], contextUri);
                if (!pt.Metadata.ContainsKey("hidden"))
                    pt.Metadata["view_index"] = (viewIndexStart + i).ToString();
                playerState.PrevTracks.Add(pt);
            }
        }

        // Fallback: use thin TrackReference if PrevQueue is empty
        if (playerState.PrevTracks.Count == 0)
        {
            var thinPrev = state.PrevTracks.Take(16).ToList();
            var viewIndexStart = Math.Max(0, state.CurrentIndex - thinPrev.Count);
            for (var i = 0; i < thinPrev.Count; i++)
            {
                var track = thinPrev[i];
                var pt = new ProvidedTrack
                {
                    Uri = track.Uri, Uid = track.Uid,
                    Provider = track.IsUserQueued ? "queue" : "context"
                };
                if (!string.IsNullOrEmpty(track.AlbumUri)) pt.Metadata["album_uri"] = track.AlbumUri;
                if (!string.IsNullOrEmpty(track.ArtistUri)) pt.Metadata["artist_uri"] = track.ArtistUri;
                if (!string.IsNullOrEmpty(contextUri))
                {
                    pt.Metadata["context_uri"] = contextUri;
                    pt.Metadata["entity_uri"] = contextUri;
                }
                pt.Metadata["track_player"] = "audio";
                pt.Metadata["iteration"] = "0";
                pt.Metadata["view_index"] = (viewIndexStart + i).ToString();
                playerState.PrevTracks.Add(pt);
            }
        }

        // Add next tracks (up to 48) - prefer rich queue data.
        // view_index continues from (currentIndex + 1) upward.
        {
            var nextItems = state.NextQueue.Take(48).ToList();
            for (var i = 0; i < nextItems.Count; i++)
            {
                var pt = QueueItemToProvidedTrack(nextItems[i], contextUri);
                if (!pt.Metadata.ContainsKey("hidden"))
                    pt.Metadata["view_index"] = (state.CurrentIndex + 1 + i).ToString();
                playerState.NextTracks.Add(pt);
            }
        }

        // Fallback: use thin TrackReference if NextQueue is empty
        if (playerState.NextTracks.Count == 0)
        {
            var thinNext = state.NextTracks.Take(48).ToList();
            for (var i = 0; i < thinNext.Count; i++)
            {
                var track = thinNext[i];
                var pt = new ProvidedTrack
                {
                    Uri = track.Uri, Uid = track.Uid,
                    Provider = track.IsUserQueued ? "queue" : "context"
                };
                if (!string.IsNullOrEmpty(track.AlbumUri)) pt.Metadata["album_uri"] = track.AlbumUri;
                if (!string.IsNullOrEmpty(track.ArtistUri)) pt.Metadata["artist_uri"] = track.ArtistUri;
                if (!string.IsNullOrEmpty(contextUri))
                {
                    pt.Metadata["context_uri"] = contextUri;
                    pt.Metadata["entity_uri"] = contextUri;
                }
                pt.Metadata["track_player"] = "audio";
                pt.Metadata["iteration"] = "0";
                pt.Metadata["view_index"] = (state.CurrentIndex + 1 + i).ToString();
                playerState.NextTracks.Add(pt);
            }
        }

        // Pagination hints — Spotify desktop appends hidden spotify:meta:page:N
        // markers so paged clients know where the queue "cuts." Emit pages
        // 2..ContextPageCount. Default is 1 → no stubs for single-page contexts
        // (playlists, albums, internal queue). Previously hardcoded to 64 which
        // made every context look like it had 63 trailing pages even when it
        // didn't. Populating ContextPageCount from context-resolve is gated
        // behind the pagination work (LoadMoreTracksAsync TODO in
        // PlaybackOrchestrator) — without real auto-pagination, emitting stubs
        // for pages we can't actually serve would be a lie.
        for (var page = 2; page <= state.ContextPageCount; page++)
        {
            playerState.NextTracks.Add(new ProvidedTrack
            {
                Uri = $"spotify:meta:page:{page}",
                Uid = $"page{page}_0",
                Provider = "context",
                Metadata = { ["hidden"] = "true", ["iteration"] = "0" }
            });
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
    /// Converts any IQueueItem to a ProvidedTrack protobuf.
    /// </summary>
    private static ProvidedTrack QueueItemToProvidedTrack(IQueueItem item, string? contextUri = null) => item switch
    {
        QueueTrack track => QueueTrackToProvidedTrack(track, contextUri),
        QueuePageMarker marker => new ProvidedTrack
        {
            Uri = marker.Uri,
            Uid = marker.Uid ?? string.Empty,
            Provider = marker.Provider,
            Metadata = { ["hidden"] = "true", ["iteration"] = "0", ["autoplay.is_autoplay"] = "true" }
        },
        QueueDelimiter delim => new ProvidedTrack
        {
            Uri = delim.Uri,
            Uid = delim.Uid ?? string.Empty,
            Provider = delim.Provider,
            Metadata =
            {
                ["hidden"] = "true",
                ["actions.advancing_past_track"] = delim.AdvanceAction,
                ["actions.skipping_next_past_track"] = delim.SkipAction,
                ["autoplay.is_autoplay"] = "true"
            },
            Removed = { "context/delimiter" }
        },
        _ => new ProvidedTrack { Uri = item.Uri, Uid = item.Uid ?? string.Empty, Provider = item.Provider }
    };

    /// <summary>
    /// Converts a QueueTrack to a ProvidedTrack protobuf with metadata.
    /// Populates the metadata map richly so remote devices render queue rows
    /// with title/artist/album/art instead of blanks. Matches the shape Spotify
    /// desktop emits for prev/next_tracks.
    /// NOTE: does NOT set top-level AlbumUri/ArtistUri — those live in the map only.
    /// </summary>
    private static ProvidedTrack QueueTrackToProvidedTrack(QueueTrack track, string? contextUri = null)
    {
        var pt = new ProvidedTrack
        {
            Uri = track.Uri,
            Uid = track.Uid ?? string.Empty,
            Provider = track.Provider
        };

        var meta = pt.Metadata;
        if (track.Title != null) meta["title"] = track.Title;
        if (track.Artist != null) meta["artist_name"] = track.Artist;
        if (track.Album != null) meta["album_title"] = track.Album;
        if (!string.IsNullOrEmpty(track.AlbumUri)) meta["album_uri"] = track.AlbumUri;
        if (!string.IsNullOrEmpty(track.ArtistUri)) meta["artist_uri"] = track.ArtistUri;
        // Queue-wide context URI is written only if the track's per-track
        // Metadata doesn't already carry one. This preserves the original
        // context URI on prev_tracks after an autoplay switchover (played
        // album tracks keep "spotify:album:xyz"; autoplay tracks and the
        // current track use the station URI).
        var trackHasContextUri = track.Metadata is { } tm && tm.ContainsKey("context_uri");
        if (!string.IsNullOrEmpty(contextUri) && !trackHasContextUri)
        {
            meta["context_uri"] = contextUri;
            meta["entity_uri"] = contextUri;
        }
        if (track.ImageUrl != null)
        {
            meta["image_url"] = track.ImageUrl;
            meta["image_small_url"] = track.ImageUrl;
            meta["image_large_url"] = track.ImageUrl;
            meta["image_xlarge_url"] = track.ImageUrl;
        }
        if (track.IsUserQueued) meta["is_queued"] = "true";
        if (track.IsAutoplay) meta["autoplay.is_autoplay"] = "true";

        meta["track_player"] = "audio";
        meta["iteration"] = "0";

        // Merge per-track format attributes from the playlist API — item-score,
        // decision_id, core:list_uid, core:added_at, PROBABLY_IN_*, etc. Do not
        // overwrite keys we've already set; those are our source of truth.
        if (track.Metadata is { Count: > 0 } extra)
        {
            foreach (var (key, value) in extra)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (meta.ContainsKey(key)) continue;
                meta[key] = value ?? string.Empty;
            }
        }

        return pt;
    }

    /// <summary>
    /// Converts a ProvidedTrack protobuf to a QueueTrack domain model.
    /// Used for transfer command processing.
    /// </summary>
    public static QueueTrack ProvidedTrackToQueueTrack(ProvidedTrack pt)
    {
        var metadata = pt.Metadata;
        metadata.TryGetValue("title", out var title);
        metadata.TryGetValue("artist_name", out var artist);
        metadata.TryGetValue("album_title", out var album);
        metadata.TryGetValue("image_url", out var imageUrl);
        imageUrl ??= metadata.GetValueOrDefault("image_xlarge_url")
                  ?? metadata.GetValueOrDefault("image_large_url")
                  ?? metadata.GetValueOrDefault("image_small_url");

        var provider = !string.IsNullOrEmpty(pt.Provider) ? pt.Provider : "context";
        if (metadata.TryGetValue("is_queued", out var isQueued) && isQueued == "true")
            provider = "queue";
        else if (metadata.TryGetValue("autoplay.is_autoplay", out var isAuto) && isAuto == "true")
            provider = "autoplay";

        return new QueueTrack(
            Uri: pt.Uri,
            Uid: pt.Uid,
            Title: title,
            Artist: artist,
            Album: album,
            AlbumUri: !string.IsNullOrEmpty(pt.AlbumUri) ? pt.AlbumUri : metadata.GetValueOrDefault("album_uri"),
            ArtistUri: !string.IsNullOrEmpty(pt.ArtistUri) ? pt.ArtistUri : metadata.GetValueOrDefault("artist_uri"),
            ImageUrl: imageUrl,
            IsUserQueued: provider == "queue",
            Provider: provider
        );
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

        // Active device changed (id, type, or the visible device list)
        if (previous.ActiveDeviceId != current.ActiveDeviceId ||
            previous.ActiveDeviceType != current.ActiveDeviceType ||
            previous.AvailableConnectDevices.Count != current.AvailableConnectDevices.Count)
            changes |= StateChanges.ActiveDevice;

        // Source changed (cluster → local or vice versa)
        if (previous.Source != current.Source)
            changes |= StateChanges.Source;

        // Volume changed
        if (previous.Volume != current.Volume || previous.IsVolumeRestricted != current.IsVolumeRestricted)
            changes |= StateChanges.Volume;

        // Queue changed (revision is the primary signal; count is a fallback)
        if (!string.Equals(previous.QueueRevision, current.QueueRevision, StringComparison.Ordinal))
            changes |= StateChanges.Queue;
        else if (previous.NextQueue.Count != current.NextQueue.Count ||
                 previous.PrevQueue.Count != current.PrevQueue.Count)
            changes |= StateChanges.Queue;

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
    /// <summary>
    /// Maps Wavee's <see cref="PlaybackState.ContextFeature"/> (which carries
    /// the context KIND — "playlist"/"album"/"artist"/"collection") to the
    /// matching Spotify desktop play-origin UI-page name. Spotify uses the
    /// play_origin to attribute Recently Played and the "you played X from Y"
    /// surface, so this string has to match a known UI page or play history
    /// won't link the play to anything.
    /// </summary>
    private static string MapContextFeatureToPlayOrigin(string? contextFeature) => contextFeature switch
    {
        "album"      => "album",
        "playlist"   => "playlist",
        "artist"     => "artist",
        "collection" => "your_library",
        "search"     => "search",
        _            => "home",
    };

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

    /// <summary>
    /// Context-level restrictions — things that can't be changed for the whole
    /// context (DJ-session lock, ad-break, radio skip caps, pre-roll, etc.).
    /// Distinct from track/player-level <see cref="BuildRestrictions"/>; those
    /// gate buttons like pause/resume/skip against the *current* track state.
    /// Conflating the two (as we used to) made `not_paused` leak into
    /// context_restrictions and confused remote devices.
    /// </summary>
    private static Restrictions BuildContextRestrictions(PlaybackState state)
    {
        // Default: no context-level disables. Populate here once the engine
        // reports DJ/ad/radio state.
        _ = state;
        return new Restrictions();
    }
}
