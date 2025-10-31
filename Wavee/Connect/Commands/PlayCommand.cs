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
    /// Specific track URI to play within context.
    /// </summary>
    public string? TrackUri { get; init; }

    /// <summary>
    /// Skip to this track index in context.
    /// </summary>
    public int? SkipToIndex { get; init; }

    /// <summary>
    /// Start position in milliseconds.
    /// </summary>
    public long? PositionMs { get; init; }

    /// <summary>
    /// Play origin information.
    /// </summary>
    public PlayOrigin? PlayOrigin { get; init; }

    /// <summary>
    /// Initial player options (shuffle, repeat).
    /// </summary>
    public PlayerOptions? Options { get; init; }

    internal static PlayCommand FromJson(DealerRequest request, JsonElement json)
    {
        var contextUri = json.TryGetProperty("context_uri", out var ctx)
            ? ctx.GetString() : null;
        var trackUri = json.TryGetProperty("track", out var trk)
            ? trk.GetProperty("uri").GetString() : null;
        var skipTo = json.TryGetProperty("skip_to", out var skip)
            ? skip.GetProperty("track_index").GetInt32() : (int?)null;
        var posMs = json.TryGetProperty("seek_to", out var seek)
            ? seek.GetInt64() : (long?)null;

        // Parse play_origin
        PlayOrigin? playOrigin = null;
        if (json.TryGetProperty("play_origin", out var origin))
        {
            playOrigin = new PlayOrigin
            {
                FeatureIdentifier = origin.GetProperty("feature_identifier").GetString() ?? string.Empty,
                FeatureVersion = origin.GetProperty("feature_version").GetString() ?? string.Empty,
                ReferrerIdentifier = origin.TryGetProperty("referrer_identifier", out var refId)
                    ? refId.GetString() : null,
                DeviceIdentifier = origin.TryGetProperty("device_identifier", out var devId)
                    ? devId.GetString() : null
            };
        }

        // Parse options
        PlayerOptions? options = null;
        if (json.TryGetProperty("options", out var opts))
        {
            options = new PlayerOptions
            {
                ShufflingContext = opts.TryGetProperty("shuffling_context", out var shuf) && shuf.GetBoolean(),
                RepeatingContext = opts.TryGetProperty("repeating_context", out var repCtx) && repCtx.GetBoolean(),
                RepeatingTrack = opts.TryGetProperty("repeating_track", out var repTrk) && repTrk.GetBoolean()
            };
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
            SkipToIndex = skipTo,
            PositionMs = posMs,
            PlayOrigin = playOrigin,
            Options = options
        };
    }
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
