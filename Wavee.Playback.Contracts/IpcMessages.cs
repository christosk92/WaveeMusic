using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Playback.Contracts;

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

/// <summary>
/// Play a fully resolved audio track. The UI process handles all Spotify protocol
/// (track resolution, CDN URL, audio key, metadata) and sends everything AudioHost
/// needs to download, decrypt, decode, and play.
/// </summary>
public sealed class PlayResolvedTrackCommand
{
    [JsonPropertyName("cdnUrl")]
    public required string CdnUrl { get; init; }

    [JsonPropertyName("audioKey")]
    public required string AudioKey { get; init; }

    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; }

    [JsonPropertyName("normalizationGain")]
    public float? NormalizationGain { get; init; }

    [JsonPropertyName("normalizationPeak")]
    public float? NormalizationPeak { get; init; }

    [JsonPropertyName("trackUri")]
    public required string TrackUri { get; init; }

    [JsonPropertyName("trackUid")]
    public string? TrackUid { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; init; }

    [JsonPropertyName("metadata")]
    public TrackMetadataDto? Metadata { get; init; }
}

/// <summary>
/// Preload the next track for gapless/crossfade transition.
/// AudioHost begins downloading and buffering without starting playback.
/// </summary>
public sealed class PrepareNextTrackCommand
{
    [JsonPropertyName("cdnUrl")]
    public required string CdnUrl { get; init; }

    [JsonPropertyName("audioKey")]
    public required string AudioKey { get; init; }

    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; }

    [JsonPropertyName("normalizationGain")]
    public float? NormalizationGain { get; init; }

    [JsonPropertyName("normalizationPeak")]
    public float? NormalizationPeak { get; init; }

    [JsonPropertyName("trackUri")]
    public required string TrackUri { get; init; }

    [JsonPropertyName("trackUid")]
    public string? TrackUid { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("metadata")]
    public TrackMetadataDto? Metadata { get; init; }
}

/// <summary>
/// Lightweight track metadata for state reporting over IPC.
/// </summary>
public sealed class TrackMetadataDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("artist")]
    public string? Artist { get; init; }

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("albumUri")]
    public string? AlbumUri { get; init; }

    [JsonPropertyName("artistUri")]
    public string? ArtistUri { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("imageLargeUrl")]
    public string? ImageLargeUrl { get; init; }

    [JsonPropertyName("imageSmallUrl")]
    public string? ImageSmallUrl { get; init; }

    [JsonPropertyName("imageXLargeUrl")]
    public string? ImageXLargeUrl { get; init; }
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

/// <summary>
/// Switch the local PortAudio output device to the one at the given index.
/// </summary>
public sealed class SwitchAudioOutputCommand
{
    [JsonPropertyName("deviceIndex")]
    public int DeviceIndex { get; init; }
}

/// <summary>
/// Describes a local Windows audio output device enumerated by PortAudio.
/// </summary>
public sealed class AudioOutputDeviceDto
{
    [JsonPropertyName("deviceIndex")]
    public int DeviceIndex { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }
}

public sealed class StartPreviewAnalysisCommand
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("previewUrl")]
    public required string PreviewUrl { get; init; }
}

public sealed class StopPreviewAnalysisCommand
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}

// PlayPlay is Spotify property. UI → AudioHost RPC for AES-key derivation.
public sealed class DerivePlayPlayKeyCommand
{
    [JsonPropertyName("obfuscatedKeyHex")]
    public required string ObfuscatedKeyHex { get; init; }

    [JsonPropertyName("contentIdHex")]
    public required string ContentIdHex { get; init; }

    [JsonPropertyName("spotifyDllPath")]
    public required string SpotifyDllPath { get; init; }
}

public sealed class DerivePlayPlayKeyResult
{
    [JsonPropertyName("aesKeyHex")]
    public required string AesKeyHex { get; init; }
}

// ── Audio → UI State ──

public sealed class PlaybackStateSnapshot
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

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

    [JsonPropertyName("isVolumeRestricted")]
    public bool IsVolumeRestricted { get; init; }

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

    /// <summary>
    /// Friendly name of the current local Windows audio output device (from PortAudio).
    /// Null if the sink is not an <c>IDeviceSelectableSink</c>.
    /// </summary>
    [JsonPropertyName("activeAudioDeviceName")]
    public string? ActiveAudioDeviceName { get; init; }

    /// <summary>
    /// Full list of local audio output devices, included only when the list changes
    /// (e.g. device plug/unplug, initial handshake).
    /// </summary>
    [JsonPropertyName("availableAudioDevices")]
    public AudioOutputDeviceDto[]? AvailableAudioDevices { get; init; }
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

    // Optional structured payload for request/reply RPCs. Null otherwise.
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

/// <summary>
/// AudioHost tells UI that the current track finished playing naturally.
/// UI should resolve and send the next track.
/// </summary>
public sealed class TrackFinishedMessage
{
    [JsonPropertyName("trackUri")]
    public string? TrackUri { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed class PreviewVisualizationFrame
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("amplitudes")]
    public float[] Amplitudes { get; init; } = [];

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }
}

/// <summary>
/// Play a track with head data for instant start. CDN URL + audio key arrive
/// later via DeferredResolvedCommand, enabling gapless head→CDN transition.
/// </summary>
public sealed class PlayTrackCommand
{
    [JsonPropertyName("deferredId")]
    public required string DeferredId { get; init; }

    [JsonPropertyName("trackUri")]
    public required string TrackUri { get; init; }

    [JsonPropertyName("trackUid")]
    public string? TrackUid { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; init; }

    [JsonPropertyName("normalizationGain")]
    public float? NormalizationGain { get; init; }

    [JsonPropertyName("normalizationPeak")]
    public float? NormalizationPeak { get; init; }

    [JsonPropertyName("headData")]
    public string? HeadData { get; init; }

    [JsonPropertyName("metadata")]
    public TrackMetadataDto? Metadata { get; init; }
}

/// <summary>
/// Loudness normalization payload for local-file playback. Optional in v1
/// (ReplayGain ingestion is a v2 feature).
/// </summary>
public sealed class NormalizationDataDto
{
    [JsonPropertyName("trackGainDb")]
    public float? TrackGainDb { get; init; }

    [JsonPropertyName("trackPeak")]
    public float? TrackPeak { get; init; }

    [JsonPropertyName("albumGainDb")]
    public float? AlbumGainDb { get; init; }

    [JsonPropertyName("albumPeak")]
    public float? AlbumPeak { get; init; }
}

/// <summary>
/// Play a local audio file directly from disk. AudioHost opens the path with
/// BASS, no CDN / audio-key / Spotify metadata involved. The track URI is the
/// stable Wavee identity (<c>wavee:local:track:{hash}</c>).
/// </summary>
public sealed class PlayLocalFileCommand
{
    [JsonPropertyName("trackUri")]
    public required string TrackUri { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("startPositionMs")]
    public long StartPositionMs { get; init; }

    [JsonPropertyName("metadata")]
    public TrackMetadataDto? Metadata { get; init; }

    [JsonPropertyName("normalization")]
    public NormalizationDataDto? Normalization { get; init; }
}

/// <summary>
/// Completes a deferred CDN resolution. AudioHost's LazyProgressiveDownloader
/// uses this to seamlessly continue from CDN after head data is exhausted.
/// </summary>
public sealed class DeferredResolvedCommand
{
    [JsonPropertyName("deferredId")]
    public required string DeferredId { get; init; }

    /// <summary>CDN URL — null when reading from local audio cache.</summary>
    [JsonPropertyName("cdnUrl")]
    public string? CdnUrl { get; init; }

    [JsonPropertyName("audioKey")]
    public required string AudioKey { get; init; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    /// <summary>
    /// Spotify file ID (40-char hex). Sent with every deferred command so AudioHost
    /// can persist the download to the audio cache keyed by the real file ID.
    /// </summary>
    [JsonPropertyName("spotifyFileId")]
    public string? SpotifyFileId { get; init; }

    /// <summary>
    /// Set when the audio file is already fully cached on disk.
    /// AudioHost reads from <c>$cacheDir/audio/$LocalCacheFileId.enc</c>
    /// instead of downloading from CDN.
    /// </summary>
    [JsonPropertyName("localCacheFileId")]
    public string? LocalCacheFileId { get; init; }
}

// ── Startup handshake ──

public sealed class AudioHostReady
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("pipeName")]
    public required string PipeName { get; init; }
}

/// <summary>
/// Configuration sent from UI to AudioHost at startup (replaces old CredentialsHandshake).
/// </summary>
public sealed class AudioHostConfig
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("normalizationEnabled")]
    public bool NormalizationEnabled { get; init; }

    [JsonPropertyName("equalizerEnabled")]
    public bool EqualizerEnabled { get; init; }

    [JsonPropertyName("equalizerBandGains")]
    public double[]? EqualizerBandGains { get; init; }

    /// <summary>Volume (0–100) to pre-seed in the engine before first play. 0 means "not specified".</summary>
    [JsonPropertyName("initialVolumePercent")]
    public int InitialVolumePercent { get; init; }

    /// <summary>
    /// Directory where persistent audio cache files are stored.
    /// AudioHost writes fully downloaded tracks here so future plays skip CDN resolution.
    /// Null means caching is disabled.
    /// </summary>
    [JsonPropertyName("audioCacheDirectory")]
    public string? AudioCacheDirectory { get; init; }
}

// ── Well-known message types ──

public static class IpcMessageTypes
{
    // UI → Audio
    public const string PlayResolved = "play_resolved";
    public const string PlayTrack = "play_track";
    public const string PlayLocalFile = "play_local_file";
    public const string DeferredResolved = "deferred_resolved";
    public const string PrepareNext = "prepare_next";
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
    public const string SwitchAudioOutput = "switch_audio_output";
    public const string RefreshAudioDevices = "refresh_audio_devices";
    public const string StartPreviewAnalysis = "start_preview_analysis";
    public const string StopPreviewAnalysis = "stop_preview_analysis";
    public const string DerivePlayPlayKey = "derive_playplay_key";
    public const string Configure = "configure";
    public const string Shutdown = "shutdown";
    public const string Ping = "ping";

    // Audio → UI
    public const string StateUpdate = "state_update";
    public const string Error = "error";
    public const string CommandResult = "command_result";
    public const string TrackFinished = "track_finished";
    public const string PreviewVisualizationFrame = "preview_visualization_frame";
    public const string Ready = "ready";
    public const string Pong = "pong";
}

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible IPC serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(PlayResolvedTrackCommand))]
[JsonSerializable(typeof(PlayTrackCommand))]
[JsonSerializable(typeof(PlayLocalFileCommand))]
[JsonSerializable(typeof(NormalizationDataDto))]
[JsonSerializable(typeof(DeferredResolvedCommand))]
[JsonSerializable(typeof(PrepareNextTrackCommand))]
[JsonSerializable(typeof(TrackMetadataDto))]
[JsonSerializable(typeof(SeekCommand))]
[JsonSerializable(typeof(SetVolumeCommand))]
[JsonSerializable(typeof(SetShuffleCommand))]
[JsonSerializable(typeof(SetRepeatCommand))]
[JsonSerializable(typeof(AddToQueueCommand))]
[JsonSerializable(typeof(SetNormalizationCommand))]
[JsonSerializable(typeof(SwitchQualityCommand))]
[JsonSerializable(typeof(SetEqualizerCommand))]
[JsonSerializable(typeof(SwitchAudioOutputCommand))]
[JsonSerializable(typeof(AudioOutputDeviceDto))]
[JsonSerializable(typeof(AudioOutputDeviceDto[]))]
[JsonSerializable(typeof(StartPreviewAnalysisCommand))]
[JsonSerializable(typeof(StopPreviewAnalysisCommand))]
[JsonSerializable(typeof(DerivePlayPlayKeyCommand))]
[JsonSerializable(typeof(DerivePlayPlayKeyResult))]
[JsonSerializable(typeof(PlaybackStateSnapshot))]
[JsonSerializable(typeof(PlaybackErrorMessage))]
[JsonSerializable(typeof(CommandResultMessage))]
[JsonSerializable(typeof(TrackFinishedMessage))]
[JsonSerializable(typeof(PreviewVisualizationFrame))]
[JsonSerializable(typeof(AudioHostReady))]
[JsonSerializable(typeof(AudioHostConfig))]
public partial class IpcJsonContext : JsonSerializerContext;
