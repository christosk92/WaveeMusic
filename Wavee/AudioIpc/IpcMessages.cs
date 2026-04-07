using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.AudioIpc;

/// <summary>
/// Base envelope for all IPC messages between UI and Audio processes.
/// Length-prefixed JSON framing: [4 bytes big-endian length][UTF-8 JSON payload].
/// </summary>
public sealed class IpcMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

// ── UI → Audio Commands ──

public sealed class PlayContextCommand
{
    [JsonPropertyName("contextUri")]
    public required string ContextUri { get; init; }

    [JsonPropertyName("trackUri")]
    public string? TrackUri { get; init; }

    [JsonPropertyName("trackIndex")]
    public int? TrackIndex { get; init; }

    [JsonPropertyName("positionMs")]
    public long? PositionMs { get; init; }

    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; init; }

    [JsonPropertyName("pageTracks")]
    public IReadOnlyList<PageTrackDto>? PageTracks { get; init; }
}

public sealed class PageTrackDto
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("uid")]
    public string? Uid { get; init; }
}

public sealed class PlayTracksCommand
{
    [JsonPropertyName("trackUris")]
    public required IReadOnlyList<string> TrackUris { get; init; }

    [JsonPropertyName("startIndex")]
    public int StartIndex { get; init; }
}

public sealed class SeekCommand
{
    [JsonPropertyName("positionMs")]
    public long PositionMs { get; init; }
}

public sealed class SetVolumeCommand
{
    [JsonPropertyName("volumePercent")]
    public int VolumePercent { get; init; }
}

public sealed class SetShuffleCommand
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public sealed class SetRepeatCommand
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
}

public sealed class AddToQueueCommand
{
    [JsonPropertyName("trackUri")]
    public required string TrackUri { get; init; }
}

public sealed class SetNormalizationCommand
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public sealed class SwitchQualityCommand
{
    [JsonPropertyName("quality")]
    public required string Quality { get; init; }
}

public sealed class SetEqualizerCommand
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("bandGains")]
    public double[]? BandGains { get; init; }
}

// ── Audio → UI State ──

public sealed class PlaybackStateSnapshot
{
    [JsonPropertyName("trackUri")]
    public string? TrackUri { get; init; }

    [JsonPropertyName("trackUid")]
    public string? TrackUid { get; init; }

    [JsonPropertyName("trackTitle")]
    public string? TrackTitle { get; init; }

    [JsonPropertyName("trackArtist")]
    public string? TrackArtist { get; init; }

    [JsonPropertyName("trackAlbum")]
    public string? TrackAlbum { get; init; }

    [JsonPropertyName("albumUri")]
    public string? AlbumUri { get; init; }

    [JsonPropertyName("artistUri")]
    public string? ArtistUri { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("imageLargeUrl")]
    public string? ImageLargeUrl { get; init; }

    [JsonPropertyName("contextUri")]
    public string? ContextUri { get; init; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; init; }

    [JsonPropertyName("isPaused")]
    public bool IsPaused { get; init; }

    [JsonPropertyName("isBuffering")]
    public bool IsBuffering { get; init; }

    [JsonPropertyName("shuffling")]
    public bool Shuffling { get; init; }

    [JsonPropertyName("repeatingContext")]
    public bool RepeatingContext { get; init; }

    [JsonPropertyName("repeatingTrack")]
    public bool RepeatingTrack { get; init; }

    [JsonPropertyName("volume")]
    public uint Volume { get; init; }

    [JsonPropertyName("canSeek")]
    public bool CanSeek { get; init; } = true;

    [JsonPropertyName("changes")]
    public int Changes { get; init; }

    [JsonPropertyName("activeDeviceId")]
    public string? ActiveDeviceId { get; init; }

    [JsonPropertyName("activeDeviceName")]
    public string? ActiveDeviceName { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("underrunCount")]
    public long UnderrunCount { get; init; }
}

public sealed class PlaybackErrorMessage
{
    [JsonPropertyName("errorType")]
    public required string ErrorType { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed class CommandResultMessage
{
    [JsonPropertyName("requestId")]
    public long RequestId { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

// ── Startup handshake ──

public sealed class AudioHostReady
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("pipeName")]
    public required string PipeName { get; init; }
}

// ── Well-known message types ──

public static class IpcMessageTypes
{
    // UI → Audio
    public const string PlayContext = "play_context";
    public const string PlayTracks = "play_tracks";
    public const string Resume = "resume";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string SkipNext = "skip_next";
    public const string SkipPrevious = "skip_previous";
    public const string Seek = "seek";
    public const string SetVolume = "set_volume";
    public const string SetShuffle = "set_shuffle";
    public const string SetRepeat = "set_repeat";
    public const string AddToQueue = "add_to_queue";
    public const string SetNormalization = "set_normalization";
    public const string SwitchQuality = "switch_quality";
    public const string SetEqualizer = "set_equalizer";
    public const string Shutdown = "shutdown";
    public const string Ping = "ping";

    // Audio → UI
    public const string StateUpdate = "state_update";
    public const string Error = "error";
    public const string CommandResult = "command_result";
    public const string Ready = "ready";
    public const string Pong = "pong";
}

public sealed record CredentialsHandshake(string Username, string StoredCredential, string DeviceId);

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible IPC serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(PlayContextCommand))]
[JsonSerializable(typeof(PlayTracksCommand))]
[JsonSerializable(typeof(SeekCommand))]
[JsonSerializable(typeof(SetVolumeCommand))]
[JsonSerializable(typeof(SetShuffleCommand))]
[JsonSerializable(typeof(SetRepeatCommand))]
[JsonSerializable(typeof(AddToQueueCommand))]
[JsonSerializable(typeof(SetNormalizationCommand))]
[JsonSerializable(typeof(SwitchQualityCommand))]
[JsonSerializable(typeof(SetEqualizerCommand))]
[JsonSerializable(typeof(PlaybackStateSnapshot))]
[JsonSerializable(typeof(PlaybackErrorMessage))]
[JsonSerializable(typeof(CommandResultMessage))]
[JsonSerializable(typeof(AudioHostReady))]
[JsonSerializable(typeof(CredentialsHandshake))]
[JsonSerializable(typeof(PageTrackDto))]
public partial class IpcJsonContext : JsonSerializerContext;
