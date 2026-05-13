using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wavee.AudioHost.Audio.Streaming;

/// <summary>
/// AES-128-CTR decryption stream for Spotify audio files.
/// Wraps an encrypted audio source and provides transparent decryption with seeking support.
/// Pass null key for unencrypted pass-through mode.
/// </summary>
public sealed class AudioDecryptStream : Stream
{
    private static readonly byte[] AUDIO_AESIV =
    [
        0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
        0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93
    ];

    private const int AES_BLOCK_SIZE = 16;

    private readonly Stream _baseStream;
    private readonly Aes? _aes;
    private readonly long _decryptionStartOffset;
    private long _position;
    private readonly byte[] _keystreamBlock = new byte[AES_BLOCK_SIZE];
    private long _keystreamBlockPosition = -1;
    private bool _disposed;

    public AudioDecryptStream(byte[]? key, Stream baseStream, long decryptionStartOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(baseStream);

        if (!baseStream.CanRead)
            throw new ArgumentException("Base stream must be readable", nameof(baseStream));

        if (decryptionStartOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(decryptionStartOffset), "Offset must be non-negative");

        _baseStream = baseStream;
        _position = 0;
        _decryptionStartOffset = decryptionStartOffset;

        if (key is null)
        {
            _aes = null;
            return;
        }

        if (key.Length != 16)
            throw new ArgumentException("AES-128 key must be exactly 16 bytes", nameof(key));

        _aes = Aes.Create();
        _aes.KeySize = 128;
        _aes.Key = key;
        _aes.Padding = PaddingMode.None;
    }

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

        if (_aes is null)
        {
            int bytesRead = _baseStream.Read(buffer);
            _position += bytesRead;
            return bytesRead;
        }

        if (_baseStream.CanSeek && _baseStream.Position != _position)
            _baseStream.Position = _position;

        int totalRead = _baseStream.Read(buffer);
        if (totalRead == 0)
            return 0;

        long readEnd = _position + totalRead;

        if (readEnd > _decryptionStartOffset)
        {
            long decryptStart = Math.Max(_position, _decryptionStartOffset);
            int skipBytes = (int)(decryptStart - _position);
            int decryptLen = totalRead - skipBytes;
            DecryptCtr(buffer.Slice(skipBytes, decryptLen), decryptStart);
        }

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
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot seek before beginning of stream");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecryptCtr(Span<byte> buffer, long streamPosition)
    {
        if (_aes is null) return;

        ApplySpotifyCtr(
            _aes,
            buffer,
            streamPosition,
            _keystreamBlock,
            ref _keystreamBlockPosition);
    }

    /// <summary>
    /// Applies the Spotify audio AES-CTR transform in-place. CTR is symmetric,
    /// so callers can use this for both decrypting CDN bytes and re-encrypting
    /// clear head bytes before writing the persistent cache file.
    /// </summary>
    internal static void ApplySpotifyCtr(byte[] key, Span<byte> buffer, long streamPosition)
    {
        if (key.Length != AES_BLOCK_SIZE)
            throw new ArgumentException("AES-128 key must be exactly 16 bytes", nameof(key));

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.Padding = PaddingMode.None;

        Span<byte> keyStreamBlock = stackalloc byte[AES_BLOCK_SIZE];
        var keyStreamBlockPosition = -1L;
        ApplySpotifyCtr(aes, buffer, streamPosition, keyStreamBlock, ref keyStreamBlockPosition);
    }

    private static void ApplySpotifyCtr(
        Aes aes,
        Span<byte> buffer,
        long streamPosition,
        Span<byte> keyStreamBlock,
        ref long keyStreamBlockPosition)
    {
        long currentPosition = streamPosition;
        int bufferOffset = 0;

        while (bufferOffset < buffer.Length)
        {
            long blockIndex = currentPosition / AES_BLOCK_SIZE;
            int offsetInBlock = (int)(currentPosition % AES_BLOCK_SIZE);

            if (keyStreamBlockPosition != blockIndex)
            {
                GenerateKeystreamBlock(aes, blockIndex, keyStreamBlock);
                keyStreamBlockPosition = blockIndex;
            }

            int bytesToProcess = Math.Min(buffer.Length - bufferOffset, AES_BLOCK_SIZE - offsetInBlock);

            XorBytes(
                buffer.Slice(bufferOffset, bytesToProcess),
                MemoryMarshal.CreateReadOnlySpan(ref keyStreamBlock[offsetInBlock], bytesToProcess));

            bufferOffset += bytesToProcess;
            currentPosition += bytesToProcess;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void XorBytes(Span<byte> dst, ReadOnlySpan<byte> key)
    {
        // Fast path: aligned 8-byte chunks, then tail bytes. A full keystream
        // block is 16 bytes, so we usually take the ulong path twice + zero tail.
        int i = 0;
        int end = dst.Length - sizeof(ulong);
        ref byte dRef = ref MemoryMarshal.GetReference(dst);
        ref byte kRef = ref MemoryMarshal.GetReference(key);
        while (i <= end)
        {
            ulong d = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dRef, i));
            ulong k = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref kRef, i));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dRef, i), d ^ k);
            i += sizeof(ulong);
        }
        for (; i < dst.Length; i++)
            Unsafe.Add(ref dRef, i) ^= Unsafe.Add(ref kRef, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GenerateKeystreamBlock(Aes aes, long blockIndex, Span<byte> output)
    {
        Span<byte> counterBlock = stackalloc byte[AES_BLOCK_SIZE];
        AUDIO_AESIV.CopyTo(counterBlock);
        AddBigEndianSpan(counterBlock, blockIndex);
        aes.EncryptEcb(counterBlock, output, PaddingMode.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBigEndianSpan(Span<byte> buffer, long value)
    {
        ulong carry = (ulong)value;
        for (int i = AES_BLOCK_SIZE - 1; i >= 0 && carry > 0; i--)
        {
            ulong sum = buffer[i] + carry;
            buffer[i] = (byte)sum;
            carry = sum >> 8;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
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
        if (_disposed) return;
        _aes?.Dispose();
        if (_baseStream is not null)
            await _baseStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
