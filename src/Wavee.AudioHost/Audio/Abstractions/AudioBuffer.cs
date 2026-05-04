using System.Buffers;

namespace Wavee.AudioHost.Audio.Abstractions;

/// <summary>
/// Represents a buffer of PCM audio data with timing information.
/// </summary>
/// <param name="Data">Raw PCM audio data (interleaved if multi-channel).</param>
/// <param name="PositionMs">Position of this buffer in the track timeline (milliseconds).</param>
public sealed record AudioBuffer(ReadOnlyMemory<byte> Data, long PositionMs)
{
    /// <summary>
    /// The rented array backing this buffer, if allocated from ArrayPool.
    /// Null for non-pooled buffers (e.g. decoder output).
    /// </summary>
    private byte[]? _rentedArray;

    /// <summary>
    /// Creates a pooled audio buffer backed by an ArrayPool rental.
    /// The <paramref name="rentedArray"/> may be larger than <paramref name="dataLength"/>;
    /// only the first <paramref name="dataLength"/> bytes are exposed via <see cref="Data"/>.
    /// </summary>
    public AudioBuffer(byte[] rentedArray, int dataLength, long positionMs)
        : this(new ReadOnlyMemory<byte>(rentedArray, 0, dataLength), positionMs)
    {
        _rentedArray = rentedArray;
    }

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

    /// <summary>
    /// Whether this buffer is backed by a pooled array that must be returned.
    /// </summary>
    public bool IsPooled => _rentedArray != null;

    /// <summary>
    /// Returns the rented array to the pool. Safe to call multiple times or on non-pooled buffers.
    /// </summary>
    public void Return()
    {
        var arr = _rentedArray;
        if (arr != null)
        {
            _rentedArray = null;
            ArrayPool<byte>.Shared.Return(arr);
        }
    }
}
