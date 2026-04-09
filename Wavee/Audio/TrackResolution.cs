using Wavee.Playback.Contracts;

namespace Wavee.Audio;

/// <summary>
/// Result of resolving a track with deferred CDN — contains head data for instant start
/// and background tasks for CDN URL + audio key that complete asynchronously.
/// </summary>
public sealed class TrackResolution
{
    public required string TrackUri { get; init; }
    public required string Codec { get; init; }
    public int BitrateKbps { get; init; }
    public byte[]? HeadData { get; init; }
    public NormalizationData Normalization { get; init; }
    public required TrackMetadataDto Metadata { get; init; }
    public long DurationMs { get; init; }

    /// <summary>Audio key — completes asynchronously after head data is sent.</summary>
    public required Task<byte[]> AudioKeyTask { get; init; }

    /// <summary>CDN URL — completes asynchronously after head data is sent.</summary>
    public required Task<string> CdnUrlTask { get; init; }

    /// <summary>File size — completes asynchronously (via HTTP HEAD on CDN URL).</summary>
    public required Task<long> FileSizeTask { get; init; }
}
