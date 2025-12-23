namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Interface for audio decoders (Vorbis, MP3, FLAC, WAV, etc.).
/// </summary>
public interface IAudioDecoder
{
    /// <summary>
    /// Gets the format name (e.g., "Vorbis", "MP3", "FLAC", "WAV").
    /// </summary>
    string FormatName { get; }

    /// <summary>
    /// Determines if this decoder can decode the given stream.
    /// Should check magic bytes/headers without consuming the stream.
    /// </summary>
    /// <param name="stream">Audio stream to check (will be reset after check).</param>
    /// <returns>True if this decoder can handle the stream.</returns>
    bool CanDecode(Stream stream);

    /// <summary>
    /// Gets the PCM audio format this decoder will output.
    /// </summary>
    /// <param name="stream">Audio stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Output PCM format.</returns>
    Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes the audio stream into PCM audio buffers.
    /// </summary>
    /// <param name="stream">Audio stream to decode.</param>
    /// <param name="startPositionMs">Start position in milliseconds (for seeking).</param>
    /// <param name="onMetadataReceived">Optional callback for ICY metadata updates (radio stream titles).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of PCM audio buffers.</returns>
    IAsyncEnumerable<AudioBuffer> DecodeAsync(
        Stream stream,
        long startPositionMs = 0,
        Action<string>? onMetadataReceived = null,
        CancellationToken cancellationToken = default);
}
