using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Wavee.Core.Crypto;

/// <summary>
/// AES-128-CTR decryption stream with Big Endian counter mode for Spotify audio files.
/// Based on librespot implementation (audio/src/decrypt.rs).
///
/// This stream wraps an encrypted audio source and provides transparent decryption
/// with full seeking support. CTR mode allows random access to any position in the stream.
/// </summary>
/// <remarks>
/// Implementation details:
/// - Algorithm: AES-128 in CTR mode with Big Endian counter increment
/// - IV: Fixed 16-byte value (same as librespot)
/// - Counter: IV + (position / 16) in big endian
/// - Some Spotify audio files are unencrypted; pass null key for pass-through mode
/// </remarks>
public sealed class AudioDecryptStream : Stream
{
    /// <summary>
    /// Fixed initialization vector for all Spotify audio decryption.
    /// This matches librespot's AUDIO_AESIV constant.
    /// </summary>
    private static readonly byte[] AUDIO_AESIV =
    [
        0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
        0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93
    ];

    private const int AES_BLOCK_SIZE = 16; // AES block size in bytes

    private readonly Stream _baseStream;
    private readonly Aes? _aes;

    // Current position in the decrypted stream
    private long _position;

    // Cached keystream block for the current position
    private readonly byte[] _keystreamBlock = new byte[AES_BLOCK_SIZE];
    private long _keystreamBlockPosition = -1; // Position of cached keystream block

    private bool _disposed;

    /// <summary>
    /// Initializes a new audio decryption stream.
    /// </summary>
    /// <param name="key">16-byte AES-128 key, or null for unencrypted pass-through</param>
    /// <param name="baseStream">The encrypted audio stream to wrap</param>
    /// <exception cref="ArgumentNullException">Thrown if baseStream is null</exception>
    /// <exception cref="ArgumentException">Thrown if key is not 16 bytes or stream is not readable</exception>
    public AudioDecryptStream(byte[]? key, Stream baseStream)
    {
        ArgumentNullException.ThrowIfNull(baseStream);

        if (!baseStream.CanRead)
            throw new ArgumentException("Base stream must be readable", nameof(baseStream));

        _baseStream = baseStream;
        _position = 0;

        // Handle unencrypted files (pass-through mode)
        if (key is null)
        {
            _aes = null;
            return;
        }

        if (key.Length != 16)
            throw new ArgumentException("AES-128 key must be exactly 16 bytes", nameof(key));

        // Initialize AES cipher for CTR mode (manual implementation using ECB)
        _aes = Aes.Create();
        _aes.KeySize = 128;
        _aes.Key = key;
        _aes.Padding = PaddingMode.None;
    }

    #region Stream Implementation

    public override bool CanRead => true;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _baseStream.Length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return 0;

        // Pass-through mode for unencrypted files
        if (_aes is null)
        {
            int bytesRead = _baseStream.Read(buffer);
            _position += bytesRead;
            return bytesRead;
        }

        // Ensure base stream is at the correct position
        if (_baseStream.Position != _position)
            _baseStream.Position = _position;

        // Read encrypted data from base stream
        int totalRead = _baseStream.Read(buffer);
        if (totalRead == 0)
            return 0;

        // Decrypt the data using CTR mode
        DecryptCtr(buffer[..totalRead], _position);

        _position += totalRead;
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking");

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };

        if (newPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot seek before the beginning of the stream");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("Cannot set length of a read-only stream");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Cannot write to a read-only stream");

    public override void Flush()
    {
        // Read-only stream, nothing to flush
    }

    #endregion

    #region CTR Mode Implementation

    /// <summary>
    /// Decrypts data using AES-128-CTR mode with Big Endian counter.
    /// </summary>
    /// <param name="buffer">Buffer containing encrypted data to decrypt in-place</param>
    /// <param name="streamPosition">Position in the stream where this buffer starts</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecryptCtr(Span<byte> buffer, long streamPosition)
    {
        if (_aes is null)
            return;

        long currentPosition = streamPosition;
        int bufferOffset = 0;

        while (bufferOffset < buffer.Length)
        {
            // Calculate which 16-byte block we're in
            long blockIndex = currentPosition / AES_BLOCK_SIZE;
            int offsetInBlock = (int)(currentPosition % AES_BLOCK_SIZE);

            // Generate keystream for this block if not cached
            if (_keystreamBlockPosition != blockIndex)
            {
                GenerateKeystreamBlock(blockIndex, _keystreamBlock);
                _keystreamBlockPosition = blockIndex;
            }

            // XOR buffer with keystream
            int bytesToProcess = Math.Min(buffer.Length - bufferOffset, AES_BLOCK_SIZE - offsetInBlock);

            for (int i = 0; i < bytesToProcess; i++)
            {
                buffer[bufferOffset + i] ^= _keystreamBlock[offsetInBlock + i];
            }

            bufferOffset += bytesToProcess;
            currentPosition += bytesToProcess;
        }
    }

    /// <summary>
    /// Generates a keystream block for the given block index.
    /// The counter is IV + blockIndex, incremented in Big Endian format.
    /// </summary>
    /// <param name="blockIndex">The block index (position / 16)</param>
    /// <param name="output">Buffer to write the 16-byte keystream block</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GenerateKeystreamBlock(long blockIndex, Span<byte> output)
    {
        if (_aes is null)
            return;

        // Create counter block: IV + blockIndex (Big Endian arithmetic)
        Span<byte> counterBlock = stackalloc byte[AES_BLOCK_SIZE];
        AUDIO_AESIV.CopyTo(counterBlock);

        // Add blockIndex to the counter in Big Endian format
        // We treat the IV as a 128-bit big-endian integer and add blockIndex to it
        AddBigEndianSpan(counterBlock, blockIndex);

        // Encrypt the counter block to generate keystream using modern Span-based API
        // EncryptEcb is a zero-allocation operation with Span support
        _aes.EncryptEcb(counterBlock, output, PaddingMode.None);
    }

    /// <summary>
    /// Adds a value to a 128-bit big-endian integer represented as a Span.
    /// This implements the counter increment for CTR mode.
    /// </summary>
    /// <param name="buffer">16-byte buffer containing the big-endian integer</param>
    /// <param name="value">Value to add</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBigEndianSpan(Span<byte> buffer, long value)
    {
        // Add value to the big-endian 128-bit counter
        // We work from the least significant byte (rightmost) backwards
        ulong carry = (ulong)value;

        for (int i = AES_BLOCK_SIZE - 1; i >= 0 && carry > 0; i--)
        {
            ulong sum = buffer[i] + carry;
            buffer[i] = (byte)sum;
            carry = sum >> 8; // Carry to next byte
        }
    }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _aes?.Dispose();
            _baseStream?.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _aes?.Dispose();

        if (_baseStream is not null)
            await _baseStream.DisposeAsync().ConfigureAwait(false);

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    #endregion
}
