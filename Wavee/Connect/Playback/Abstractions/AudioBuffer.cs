namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Represents a buffer of PCM audio data with timing information.
/// </summary>
/// <param name="Data">Raw PCM audio data (interleaved if multi-channel).</param>
/// <param name="PositionMs">Position of this buffer in the track timeline (milliseconds).</param>
public sealed record AudioBuffer(ReadOnlyMemory<byte> Data, long PositionMs)
{
    /// <summary>
    /// Creates an empty audio buffer.
    /// </summary>
    public static AudioBuffer Empty => new(ReadOnlyMemory<byte>.Empty, 0);

    /// <summary>
    /// Gets whether this buffer contains any data.
    /// </summary>
    public bool IsEmpty => Data.Length == 0;

    /// <summary>
    /// Gets the size of the buffer in bytes.
    /// </summary>
    public int SizeBytes => Data.Length;
}
