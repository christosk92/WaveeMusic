using Wavee.Connect.Playback.Abstractions;
using Wavee.Core.Audio;
using Wavee.Core.Audio.Download;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Track stream for Spotify audio files.
/// </summary>
/// <remarks>
/// Combines the decrypted audio stream with metadata and normalization data.
/// The stream is backed by a ProgressiveDownloader for efficient streaming/seeking.
/// </remarks>
public sealed class SpotifyTrackStream : ITrackStream
{
    private readonly Stream _audioStream;
    private readonly ProgressiveDownloader? _downloader;
    private bool _disposed;

    /// <summary>
    /// Creates a new SpotifyTrackStream.
    /// </summary>
    /// <param name="audioStream">Decrypted audio stream.</param>
    /// <param name="metadata">Track metadata.</param>
    /// <param name="normalizationData">Normalization data for volume adjustment.</param>
    /// <param name="downloader">Optional progressive downloader for monitoring.</param>
    public SpotifyTrackStream(
        Stream audioStream,
        TrackMetadata metadata,
        NormalizationData normalizationData,
        ProgressiveDownloader? downloader = null)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        ArgumentNullException.ThrowIfNull(metadata);

        _audioStream = audioStream;
        _downloader = downloader;
        Metadata = metadata;
        NormalizationData = normalizationData;
    }

    /// <inheritdoc />
    public Stream AudioStream => _audioStream;

    /// <inheritdoc />
    public TrackMetadata Metadata { get; }

    /// <inheritdoc />
    public AudioFormat? KnownFormat => null; // Decoder will detect

    /// <inheritdoc />
    public bool CanSeek => _audioStream.CanSeek;

    /// <summary>
    /// Gets the normalization data for this track.
    /// </summary>
    public NormalizationData NormalizationData { get; }

    /// <summary>
    /// Gets the audio file format (codec).
    /// </summary>
    public AudioFileFormat AudioFormat { get; init; }

    /// <summary>
    /// Gets the file ID.
    /// </summary>
    public FileId FileId { get; init; }

    /// <summary>
    /// Gets buffer status from the progressive downloader.
    /// </summary>
    public BufferStatus? GetBufferStatus() => _downloader?.GetBufferStatus();

    /// <inheritdoc />
    public async Task PrefetchForSeekAsync(TimeSpan targetPosition, CancellationToken cancellationToken = default)
    {
        // Estimate byte position from time position
        var durationMs = Metadata.DurationMs;
        if (durationMs <= 0)
            return; // Can't estimate without duration

        var targetMs = targetPosition.TotalMilliseconds;
        var fileSize = _audioStream.Length;

        // Linear estimation: bytePosition = (targetMs / durationMs) * fileSize
        var estimatedBytePosition = (long)((targetMs / durationMs) * fileSize);

        // Prefetch 256KB around the estimated position (64KB before, 192KB after)
        // This covers OGG page boundaries that NVorbis needs to read
        var prefetchStart = Math.Max(0, estimatedBytePosition - 64 * 1024);
        const int prefetchLength = 256 * 1024;

        // Check if audio stream is a LazyProgressiveDownloader (instant start case)
        if (_audioStream is LazyProgressiveDownloader lazyDownloader)
        {
            await lazyDownloader.PrefetchRangeAsync(prefetchStart, prefetchLength, cancellationToken);
        }
        // For regular ProgressiveDownloader wrapped in AudioDecryptStream, use the downloader directly
        else if (_downloader != null)
        {
            await _downloader.FetchRangeAsync(prefetchStart, prefetchStart + prefetchLength, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _audioStream.DisposeAsync();

        if (_downloader != null)
        {
            await _downloader.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
