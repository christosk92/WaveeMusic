namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Represents a loaded track with audio stream and metadata.
/// </summary>
public interface ITrackStream : IAsyncDisposable
{
    /// <summary>
    /// Gets the audio data stream (may be compressed, decoder will handle it).
    /// </summary>
    Stream AudioStream { get; }

    /// <summary>
    /// Gets track metadata.
    /// </summary>
    TrackMetadata Metadata { get; }

    /// <summary>
    /// Gets the known audio format, if available.
    /// Null means format detection is needed.
    /// </summary>
    AudioFormat? KnownFormat { get; }

    /// <summary>
    /// Gets whether seeking is supported in the audio stream.
    /// </summary>
    bool CanSeek { get; }
}
