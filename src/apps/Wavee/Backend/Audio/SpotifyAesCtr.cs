using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wavee.Backend.Audio;

/// <summary>AES-128-CTR primitives for Spotify audio. Public IV; keystream = AES-ECB(IV + blockIndex) XOR ciphertext.
/// Pure, unit-testable, and shared by the in-process decrypt streams.</summary>
public static class SpotifyAesCtr
{
    public static ReadOnlySpan<byte> PublicIv =>
    [
        0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
        0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93
    ];

    public const int SpotifyHeaderSize = 0xa7;
    public const int OggMagicOffset = 0xa7;
    public static ReadOnlySpan<byte> OggMagic => "OggS"u8;

    /// <summary>Decrypt <paramref name="cipher"/> in-place from <paramref name="streamOffset"/> using CTR.</summary>
    public static void DecryptInPlace(Span<byte> buffer, ReadOnlySpan<byte> key, long streamOffset)
    {
        if (key.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes", nameof(key));
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key.ToArray();
        aes.Padding = PaddingMode.None;

        const int blockSize = 16;
        var keystream = new byte[blockSize];
        long pos = streamOffset;
        var offset = 0;
        while (offset < buffer.Length)
        {
            var blockIndex = pos / blockSize;
            var inBlock = (int)(pos % blockSize);
            GenerateKeystreamBlock(aes, blockIndex, keystream);
            var n = Math.Min(buffer.Length - offset, blockSize - inBlock);
            XorBytes(buffer.Slice(offset, n), keystream.AsSpan(inBlock, n));
            offset += n;
            pos += n;
        }
    }

    /// <summary>Decrypt a single range without mutating source.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> key, long streamOffset)
    {
        var plain = cipher.ToArray();
        DecryptInPlace(plain, key, streamOffset);
        return plain;
    }

    /// <summary>Runtime key-validation gate: decrypt body bytes [0,~0xc0) and assert OggS@0xa7.</summary>
    public static bool ValidateKeyOnBodyRange(ReadOnlySpan<byte> encryptedBodyPrefix, ReadOnlySpan<byte> key)
    {
        if (encryptedBodyPrefix.Length < OggMagicOffset + OggMagic.Length) return false;
        var plain = Decrypt(encryptedBodyPrefix[..Math.Min(encryptedBodyPrefix.Length, 0xc0)], key, 0);
        return plain.AsSpan().Length >= OggMagicOffset + OggMagic.Length && plain.AsSpan(OggMagicOffset, 4).SequenceEqual(OggMagic);
    }

    static void GenerateKeystreamBlock(Aes aes, long blockIndex, Span<byte> output)
    {
        Span<byte> counter = stackalloc byte[16];
        PublicIv.CopyTo(counter);
        AddBigEndian(counter, blockIndex);
        aes.EncryptEcb(counter, output, PaddingMode.None);
    }

    static void AddBigEndian(Span<byte> buffer, long value)
    {
        var carry = (ulong)value;
        for (var i = 15; i >= 0 && carry > 0; i--)
        {
            var sum = buffer[i] + carry;
            buffer[i] = (byte)sum;
            carry = sum >> 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void XorBytes(Span<byte> dst, ReadOnlySpan<byte> key)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] ^= key[i];
    }
}
