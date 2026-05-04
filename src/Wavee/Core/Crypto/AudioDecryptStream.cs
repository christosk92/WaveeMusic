using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wavee.Core.Crypto;

/// <summary>
/// AES-128-CTR decryption stream for Spotify audio files.
/// Wraps an encrypted audio source and provides transparent decryption with seeking support.
/// Pass null key for unencrypted pass-through mode.
/// </summary>
public sealed class AudioDecryptStream : Stream
{
    private static readonly byte[] AudioAesIv =
    [
        0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
        0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93
    ];

    private const int AesBlockSize = 16;

    private readonly Stream _baseStream;
    private readonly Aes? _aes;
    private readonly long _decryptionStartOffset;
    private readonly byte[] _keystreamBlock = new byte[AesBlockSize];
    private long _position;
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
        _decryptionStartOffset = decryptionStartOffset;

        if (key is null)
            return;

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
            var bytesRead = _baseStream.Read(buffer);
            _position += bytesRead;
            return bytesRead;
        }

        if (_baseStream.CanSeek && _baseStream.Position != _position)
            _baseStream.Position = _position;

        var totalRead = _baseStream.Read(buffer);
        if (totalRead == 0)
            return 0;

        var readEnd = _position + totalRead;
        if (readEnd > _decryptionStartOffset)
        {
            var decryptStart = Math.Max(_position, _decryptionStartOffset);
            var skipBytes = (int)(decryptStart - _position);
            var decryptLength = totalRead - skipBytes;
            DecryptCtr(buffer.Slice(skipBytes, decryptLength), decryptStart);
        }

        _position += totalRead;
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!CanSeek)
            throw new NotSupportedException("Stream does not support seeking");

        var newPosition = origin switch
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
        if (_aes is null)
            return;

        var currentPosition = streamPosition;
        var bufferOffset = 0;

        while (bufferOffset < buffer.Length)
        {
            var blockIndex = currentPosition / AesBlockSize;
            var offsetInBlock = (int)(currentPosition % AesBlockSize);

            if (_keystreamBlockPosition != blockIndex)
            {
                GenerateKeystreamBlock(blockIndex, _keystreamBlock);
                _keystreamBlockPosition = blockIndex;
            }

            var bytesToProcess = Math.Min(buffer.Length - bufferOffset, AesBlockSize - offsetInBlock);
            XorBytes(
                buffer.Slice(bufferOffset, bytesToProcess),
                MemoryMarshal.CreateReadOnlySpan(ref _keystreamBlock[offsetInBlock], bytesToProcess));

            bufferOffset += bytesToProcess;
            currentPosition += bytesToProcess;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void XorBytes(Span<byte> dst, ReadOnlySpan<byte> key)
    {
        var i = 0;
        var end = dst.Length - sizeof(ulong);
        ref var dRef = ref MemoryMarshal.GetReference(dst);
        ref var kRef = ref MemoryMarshal.GetReference(key);
        while (i <= end)
        {
            var d = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dRef, i));
            var k = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref kRef, i));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dRef, i), d ^ k);
            i += sizeof(ulong);
        }

        for (; i < dst.Length; i++)
            Unsafe.Add(ref dRef, i) ^= Unsafe.Add(ref kRef, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GenerateKeystreamBlock(long blockIndex, Span<byte> output)
    {
        if (_aes is null)
            return;

        Span<byte> counterBlock = stackalloc byte[AesBlockSize];
        AudioAesIv.CopyTo(counterBlock);
        AddBigEndianSpan(counterBlock, blockIndex);
        _aes.EncryptEcb(counterBlock, output, PaddingMode.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBigEndianSpan(Span<byte> buffer, long value)
    {
        var carry = (ulong)value;
        for (var i = AesBlockSize - 1; i >= 0 && carry > 0; i--)
        {
            var sum = buffer[i] + carry;
            buffer[i] = (byte)sum;
            carry = sum >> 8;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _aes?.Dispose();
            _baseStream.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _aes?.Dispose();
        await _baseStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
