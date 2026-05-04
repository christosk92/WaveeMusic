using System.Collections.Generic;
using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to start playback with context.
/// </summary>
public sealed record PlayCommand : ConnectCommand
{
    /// <summary>
    /// Context URI (playlist, album, etc.).
    /// </summary>
    public string? ContextUri { get; init; }

    /// <summary>
    /// Specific track URI to play within context (from options.skip_to.track_uri).
    /// </summary>
    public string? TrackUri { get; init; }

    /// <summary>
    /// Specific track UID to play within context (from options.skip_to.track_uid).
    /// Higher priority than TrackUri.
    /// </summary>
    public string? TrackUid { get; init; }

    /// <summary>
    /// Skip to this track index in context (from options.skip_to.track_index).
    /// Lowest priority - used only if TrackUri and TrackUid are not set.
    /// </summary>
    public int? SkipToIndex { get; init; }

    /// <summary>
    /// Start position in milliseconds (from options.seek_to).
    /// </summary>
    public long? PositionMs { get; init; }

    /// <summary>
    /// Pre-loaded tracks from context.pages (uri, uid pairs).
    /// Can be used to populate the queue without fetching from API.
    /// </summary>
    public IReadOnlyList<PageTrack>? PageTracks { get; init; }

    /// <summary>
    /// Play origin information.
    /// </summary>
    public PlayOrigin? PlayOrigin { get; init; }

    /// <summary>
    /// Initial player options (shuffle, repeat).
    /// </summary>
    public PlayerOptions? Options { get; init; }

    /// <summary>
    /// Human-readable subtitle for the context (e.g. artist name, playlist title).
    /// Flows into PlayerState.context_metadata["context_description"] so remote
    /// "Now Playing" cards can show it under the track title. Populate from
    /// the UI or ConnectCommandExecutor when the display name is known — leave
    /// null if unknown (server won't attempt to resolve it for us).
    /// </summary>
    public string? ContextDescription { get; init; }

    /// <summary>
    /// Cover-art URL for the context (playlist/album thumbnail).
    /// Flows into PlayerState.context_metadata["image_url"].
    /// </summary>
    public string? ContextImageUrl { get; init; }

    /// <summary>
    /// Short string describing the kind of context ("playlist", "album",
    /// "artist", "collection"). Drives PlayerState.play_origin.feature_identifier
    /// so remotes label the source correctly.
    /// </summary>
    public string? ContextFeature { get; init; }

    /// <summary>
    /// Total track count for the context. Emitted as
    /// context_metadata.playlist_number_of_tracks for playlists.
    /// </summary>
    public int? ContextTrackCount { get; init; }

    /// <summary>
    /// Context-level format attributes returned by the playlist service
    /// (<c>format</c>, <c>request_id</c>, <c>tag</c>, <c>source-loader</c>,
    /// <c>image_url</c>, session display names, etc.). Merged verbatim into
    /// <c>PlayerState.context_metadata</c> so remote clients see the same
    /// rich context as a native Spotify session.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ContextFormatAttributes { get; init; }

    internal static PlayCommand FromJson(DealerRequest request, JsonElement json)
    {
        // 1. Parse context (context.uri and context.pages)
        string? contextUri = null;
        List<PageTrack>? pageTracks = null;

        if (json.TryGetProperty("context", out var ctx))
        {
            // Get context URI if present
            if (ctx.TryGetProperty("uri", out var ctxUri))
                contextUri = ctxUri.GetString();

            // Extract tracks from pages (for pre-loading or when no URI)
            if (ctx.TryGetProperty("pages", out var pages) && pages.GetArrayLength() > 0)
            {
                pageTracks = new List<PageTrack>();
                foreach (var page in pages.EnumerateArray())
                {
                    if (page.TryGetProperty("tracks", out var tracks))
                    {
                        foreach (var track in tracks.EnumerateArray())
                        {
                            var uri = track.TryGetProperty("uri", out var u) ? u.GetString() : null;
                            var uid = track.TryGetProperty("uid", out var id) ? id.GetString() : null;
                            if (!string.IsNullOrEmpty(uri))
                            {
                                Dictionary<string, string>? trackMeta = null;
                                if (track.TryGetProperty("metadata", out var meta)
                                    && meta.ValueKind == JsonValueKind.Object)
                                {
                                    trackMeta = new Dictionary<string, string>();
                                    foreach (var kv in meta.EnumerateObject())
                                    {
                                        if (kv.Value.ValueKind == JsonValueKind.String)
                                            trackMeta[kv.Name] = kv.Value.GetString()!;
                                    }
                                }
                                pageTracks.Add(new PageTrack(uri!, uid ?? string.Empty)
                                {
                                    Metadata = trackMeta
                                });
                            }
                        }
                    }
                }
            }
        }

        // 2. Parse options (skip_to, seek_to, player_options_override)
        string? trackUri = null;
        string? trackUid = null;
        int? skipToIndex = null;
        long? posMs = null;
        bool shuffle = false, repeatContext = false, repeatTrack = false;

        if (json.TryGetProperty("options", out var options))
        {
            // Parse skip_to (priority: uid > uri > index)
            if (options.TryGetProperty("skip_to", out var skipTo))
            {
                if (skipTo.TryGetProperty("track_uid", out var uid))
                    trackUid = uid.GetString();
                if (skipTo.TryGetProperty("track_uri", out var uri))
                    trackUri = uri.GetString();
                if (skipTo.TryGetProperty("track_index", out var idx))
                    skipToIndex = idx.GetInt32();
            }

            // Parse seek position
            if (options.TryGetProperty("seek_to", out var seek))
                posMs = seek.GetInt64();

            // Parse player options override (nested under options)
            if (options.TryGetProperty("player_options_override", out var playerOpts))
            {
                if (playerOpts.TryGetProperty("shuffling_context", out var shuf))
                    shuffle = shuf.GetBoolean();
                if (playerOpts.TryGetProperty("repeating_context", out var rep))
                    repeatContext = rep.GetBoolean();
                if (playerOpts.TryGetProperty("repeating_track", out var repTrk))
                    repeatTrack = repTrk.GetBoolean();
            }
        }

        // 3. Parse play_origin
        PlayOrigin? playOrigin = null;
        if (json.TryGetProperty("play_origin", out var origin))
        {
            var featureId = origin.TryGetProperty("feature_identifier", out var fid) ? fid.GetString() : null;
            var featureVer = origin.TryGetProperty("feature_version", out var fver) ? fver.GetString() : null;

            playOrigin = new PlayOrigin
            {
                FeatureIdentifier = featureId ?? string.Empty,
                FeatureVersion = featureVer ?? string.Empty,
                ReferrerIdentifier = origin.TryGetProperty("referrer_identifier", out var refId)
                    ? refId.GetString() : null,
                DeviceIdentifier = origin.TryGetProperty("device_identifier", out var devId)
                    ? devId.GetString() : null
            };
        }

        // If no track URI from skip_to, fall back to first page track
        if (string.IsNullOrEmpty(trackUri) && pageTracks?.Count > 0)
        {
            trackUri = pageTracks[0].Uri;
            if (string.IsNullOrEmpty(trackUid))
                trackUid = pageTracks[0].Uid;
        }

        return new PlayCommand
        {
            Endpoint = "play",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            ContextUri = contextUri,
            TrackUri = trackUri,
            TrackUid = trackUid,
            SkipToIndex = skipToIndex,
            PositionMs = posMs,
            PageTracks = pageTracks,
            PlayOrigin = playOrigin,
            Options = new PlayerOptions
            {
                ShufflingContext = shuffle,
                RepeatingContext = repeatContext,
                RepeatingTrack = repeatTrack
            }
        };
    }
}

/// <summary>
/// A track from context.pages with URI, UID, and optional per-track metadata.
/// <see cref="Metadata"/> forwards verbatim into <c>ProvidedTrack.metadata</c>
/// when this track is published in <c>PlayerState</c> — used to reproduce
/// playlist-API recommender fields (item-score, decision_id, PROBABLY_IN_*,
/// core:list_uid, …) on remote Spotify clients.
/// </summary>
public sealed record PageTrack(string Uri, string Uid)
{
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Information about where playback was initiated from.
/// </summary>
public sealed record PlayOrigin
{
    /// <summary>
    /// Feature that initiated playback (e.g., "playlist", "album", "search").
    /// </summary>
    public required string FeatureIdentifier { get; init; }

    /// <summary>
    /// Version of the feature.
    /// </summary>
    public required string FeatureVersion { get; init; }

    /// <summary>
    /// Referrer feature identifier (what led to this playback).
    /// </summary>
    public string? ReferrerIdentifier { get; init; }

    /// <summary>
    /// Device identifier that initiated playback.
    /// </summary>
    public string? DeviceIdentifier { get; init; }
}

/// <summary>
/// Player options for playback.
/// </summary>
public sealed record PlayerOptions
{
    /// <summary>
    /// Whether shuffle is enabled for context.
    /// </summary>
    public bool ShufflingContext { get; init; }

    /// <summary>
    /// Whether context repeat is enabled.
    /// </summary>
    public bool RepeatingContext { get; init; }

    /// <summary>
    /// Whether single track repeat is enabled.
    /// </summary>
    public bool RepeatingTrack { get; init; }
}
