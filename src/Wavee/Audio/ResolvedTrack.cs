using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// A fully resolved track ready to be sent to AudioHost.
/// Contains everything needed: CDN URL, decryption key, codec, normalization, metadata.
/// </summary>
public sealed record ResolvedTrack
{
    public required string TrackUri { get; init; }
    public string? TrackUid { get; init; }
    public required string CdnUrl { get; init; }
    public required byte[] AudioKey { get; init; }
    public required string FileId { get; init; }
    public required string Codec { get; init; }
    public int BitrateKbps { get; init; }
    public float? NormalizationGain { get; init; }
    public float? NormalizationPeak { get; init; }
    public long DurationMs { get; init; }
    public required TrackMetadataDto Metadata { get; init; }

    /// <summary>
    /// Maps this resolved track to an IPC command for AudioHost.
    /// </summary>
    public PlayResolvedTrackCommand ToIpcCommand(long positionMs = 0) => new()
    {
        CdnUrl = CdnUrl,
        AudioKey = Convert.ToBase64String(AudioKey),
        FileId = FileId,
        Codec = Codec,
        BitrateKbps = BitrateKbps,
        NormalizationGain = NormalizationGain,
        NormalizationPeak = NormalizationPeak,
        TrackUri = TrackUri,
        TrackUid = TrackUid,
        DurationMs = DurationMs,
        PositionMs = positionMs,
        Metadata = Metadata
    };
}
