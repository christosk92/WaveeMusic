using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wavee.Protocol;

namespace Wavee.Core.Authentication;

/// <summary>
/// Internal utility for decrypting Spotify reusable credential blobs.
/// </summary>
/// <remarks>
/// Blob Encryption Algorithm (Spotify proprietary):
/// <list type="number">
/// <item>Derive 20-byte secret from SHA-1(deviceId)</item>
/// <item>Derive 24-byte AES key via PBKDF2-HMAC-SHA1(secret, username, 256 iterations)</item>
/// <item>Hash the key again: key[0..20] = SHA-1(key[0..20])</item>
/// <item>Append key length: key[20..24] = 20 (big endian)</item>
/// <item>Base64 decode the blob</item>
/// <item>Decrypt using AES-192-ECB (no padding)</item>
/// <item>XOR unrolling: blob[i] ^= blob[i - 16] for i in (len-16)..len</item>
/// <item>Parse blob structure to extract auth type and data</item>
/// </list>
///
/// This algorithm must match Spotify's implementation exactly.
/// Uses high-performance patterns: stackalloc for keys, ArrayPool for buffers.
/// </remarks>
internal static class BlobDecryptor
{
    private const int Pbkdf2Iterations = 0x100; // 256
    private const int AesKeySize = 24; // AES-192
    private const int Sha1Size = 20;
    private const int AesBlockSize = 16;

    /// <summary>
    /// Decrypts an encrypted credential blob.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="encryptedBlob">Base64-encoded encrypted blob.</param>
    /// <param name="deviceId">Device ID used to encrypt the blob.</param>
    /// <returns>Decrypted credentials.</returns>
    /// <exception cref="AuthenticationException">Thrown if decryption fails.</exception>
    public static Credentials Decrypt(
        string username,
        ReadOnlySpan<byte> encryptedBlob,
        ReadOnlySpan<byte> deviceId)
    {
        // Step 1: Derive secret from device ID
        Span<byte> secret = stackalloc byte[Sha1Size];
        SHA1.HashData(deviceId, secret);

        // Step 2: Derive AES-192 key (24 bytes)
        Span<byte> aesKey = stackalloc byte[AesKeySize];
        DeriveAesKey(secret, username, aesKey);

        // Step 3-8: Decode and decrypt blob
        return DecryptAndParseBlob(username, encryptedBlob, aesKey);
    }

    /// <summary>
    /// Derives the AES-192 key using PBKDF2-HMAC-SHA1.
    /// </summary>
    private static void DeriveAesKey(ReadOnlySpan<byte> secret, string username, Span<byte> aesKey)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(username);

        // Derive first 20 bytes via PBKDF2
        Rfc2898DeriveBytes.Pbkdf2(
            password: secret,
            salt: usernameBytes,
            iterations: Pbkdf2Iterations,
            hashAlgorithm: HashAlgorithmName.SHA1,
            destination: aesKey[..Sha1Size]);

        // Hash the derived key
        Span<byte> keyHash = stackalloc byte[Sha1Size];
        SHA1.HashData(aesKey[..Sha1Size], keyHash);
        keyHash.CopyTo(aesKey[..Sha1Size]);

        // Append key length (big endian uint32: 20)
        BinaryPrimitives.WriteUInt32BigEndian(aesKey[Sha1Size..], Sha1Size);
    }

    /// <summary>
    /// Decodes, decrypts, and parses the blob.
    /// </summary>
    private static Credentials DecryptAndParseBlob(
        string username,
        ReadOnlySpan<byte> encryptedBlob,
        ReadOnlySpan<byte> aesKey)
    {
        // Step 5: Base64 decode blob
        var maxDecodedSize = (encryptedBlob.Length * 3 / 4) + 3; // Conservative estimate
        var decodedBlob = ArrayPool<byte>.Shared.Rent(maxDecodedSize);

        try
        {
            // Convert bytes to string for Base64 decoding
            var base64String = Encoding.UTF8.GetString(encryptedBlob);
            if (!Convert.TryFromBase64String(base64String, decodedBlob, out var bytesWritten))
            {
                throw new AuthenticationException(
                    AuthenticationFailureReason.InvalidBlob,
                    "Failed to decode Base64 blob");
            }

            var blob = decodedBlob.AsSpan(0, bytesWritten);

            // Step 6: Decrypt using AES-192-ECB
            DecryptAes192Ecb(aesKey, blob);

            // Step 7: XOR unrolling (reverse encryption's CBC-like step)
            UnrollXor(blob);

            // Step 8: Parse blob structure
            return ParseBlob(blob, username);
        }
        finally
        {
            // Security: Clear sensitive data from rented buffer
            ArrayPool<byte>.Shared.Return(decodedBlob, clearArray: true);
        }
    }

    /// <summary>
    /// Decrypts data in-place using AES-192-ECB mode.
    /// </summary>
    private static void DecryptAes192Ecb(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var aes = Aes.Create();
        aes.KeySize = 192;
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();

        // Decrypt in-place (ECB processes blocks independently)
        for (int offset = 0; offset < data.Length; offset += AesBlockSize)
        {
            var blockSize = Math.Min(AesBlockSize, data.Length - offset);
            if (blockSize == AesBlockSize)
            {
                var block = data.Slice(offset, AesBlockSize).ToArray();
                decryptor.TransformBlock(block, 0, AesBlockSize, block, 0);
                block.CopyTo(data.Slice(offset));
            }
        }
    }

    /// <summary>
    /// Performs XOR unrolling to reverse the encryption's chaining step.
    /// Each byte is XORed with the byte 16 positions earlier.
    /// </summary>
    private static void UnrollXor(Span<byte> blob)
    {
        int len = blob.Length;
        for (int i = 0; i < len - AesBlockSize; i++)
        {
            blob[len - i - 1] ^= blob[len - i - 1 - AesBlockSize];
        }
    }

    /// <summary>
    /// Parses the decrypted blob structure to extract credentials.
    /// </summary>
    private static Credentials ParseBlob(ReadOnlySpan<byte> blob, string username)
    {
        try
        {
            var reader = new BlobReader(blob);

            // Parse blob structure (based on librespot)
            reader.ReadU8();        // Skip unknown byte
            reader.ReadBytes();     // Skip unknown bytes
            reader.ReadU8();        // Skip unknown byte
            var authTypeInt = reader.ReadInt();
            reader.ReadU8();        // Skip unknown byte
            var authData = reader.ReadBytes();

            // Validate auth type
            if (!Enum.IsDefined(typeof(AuthenticationType), authTypeInt))
            {
                throw new AuthenticationException(
                    AuthenticationFailureReason.InvalidBlob,
                    $"Invalid auth type in blob: {authTypeInt}");
            }

            var authType = (AuthenticationType)authTypeInt;

            return new Credentials
            {
                Username = username,
                AuthType = authType,
                AuthData = authData.ToArray()
            };
        }
        catch (Exception ex) when (ex is not AuthenticationException)
        {
            throw new AuthenticationException(
                AuthenticationFailureReason.InvalidBlob,
                "Failed to parse blob structure",
                ex);
        }
    }

    /// <summary>
    /// Simple blob reader for parsing varint-encoded fields.
    /// Uses ref struct for zero-allocation stack-based reading.
    /// </summary>
    private ref struct BlobReader
    {
        private ReadOnlySpan<byte> _data;
        private int _position;

        public BlobReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>
        /// Reads a single byte.
        /// </summary>
        public byte ReadU8()
        {
            if (_position >= _data.Length)
            {
                throw new AuthenticationException(
                    AuthenticationFailureReason.InvalidBlob,
                    "Unexpected end of blob");
            }
            return _data[_position++];
        }

        /// <summary>
        /// Reads a varint-encoded integer (1 or 2 bytes).
        /// </summary>
        public int ReadInt()
        {
            var lo = (int)ReadU8();
            if ((lo & 0x80) == 0)
                return lo;

            var hi = (int)ReadU8();
            return (lo & 0x7F) | (hi << 7);
        }

        /// <summary>
        /// Reads a length-prefixed byte array.
        /// </summary>
        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadInt();
            if (_position + length > _data.Length)
            {
                throw new AuthenticationException(
                    AuthenticationFailureReason.InvalidBlob,
                    $"Invalid blob length: expected {length} bytes, only {_data.Length - _position} available");
            }

            var result = _data.Slice(_position, length);
            _position += length;
            return result;
        }
    }
}
