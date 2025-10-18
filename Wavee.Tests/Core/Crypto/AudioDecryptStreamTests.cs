using System.Security.Cryptography;
using Wavee.Core.Crypto;
using Xunit;

namespace Wavee.Tests.Core.Crypto;

/// <summary>
/// Unit tests for AudioDecryptStream - AES-128-CTR Big Endian decryption.
/// Verifies correctness of decryption, seeking, and random access capabilities.
/// </summary>
public class AudioDecryptStreamTests
{
    private readonly ITestOutputHelper _output;

    public AudioDecryptStreamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Basic Functionality Tests

    [Fact]
    public void Constructor_WithNullKey_ShouldCreatePassthroughStream()
    {
        // Arrange
        byte[] data = "Hello, World!"u8.ToArray();
        using var baseStream = new MemoryStream(data);

        // Act
        using var decryptStream = new AudioDecryptStream(null, baseStream);
        byte[] output = new byte[data.Length];
        int bytesRead = decryptStream.Read(output, 0, output.Length);

        // Assert
        Assert.Equal(data.Length, bytesRead);
        Assert.Equal(data, output);
    }

    [Fact]
    public void Constructor_WithInvalidKeySize_ShouldThrow()
    {
        // Arrange
        byte[] invalidKey = new byte[32]; // Should be 16 bytes
        using var baseStream = new MemoryStream();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new AudioDecryptStream(invalidKey, baseStream));
    }

    [Fact]
    public void Constructor_WithNullBaseStream_ShouldThrow()
    {
        // Arrange
        byte[] key = new byte[16];

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AudioDecryptStream(key, null!));
    }

    [Fact]
    public void Read_WithValidKey_ShouldDecryptData()
    {
        // Arrange
        byte[] key = GenerateTestKey(0);
        byte[] plaintext = "This is test data for AES-CTR decryption!"u8.ToArray();

        // Encrypt the plaintext manually for testing
        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);

        // Act
        using var decryptStream = new AudioDecryptStream(key, baseStream);
        byte[] decrypted = new byte[plaintext.Length];
        int bytesRead = decryptStream.Read(decrypted, 0, decrypted.Length);

        // Assert
        Assert.Equal(plaintext.Length, bytesRead);
        Assert.Equal(plaintext, decrypted);

        _output.WriteLine($"Plaintext:  {BytesToHex(plaintext)}");
        _output.WriteLine($"Encrypted:  {BytesToHex(encrypted)}");
        _output.WriteLine($"Decrypted:  {BytesToHex(decrypted)}");
    }

    #endregion

    #region Seeking Tests

    [Fact]
    public void Seek_ToBeginning_ShouldResetPosition()
    {
        // Arrange
        byte[] key = GenerateTestKey(1);
        byte[] data = new byte[1024];
        RandomNumberGenerator.Fill(data);

        byte[] encrypted = EncryptWithAesCtr(data, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Read some data first
        byte[] buffer1 = new byte[100];
        decryptStream.Read(buffer1, 0, buffer1.Length);

        // Seek back to beginning
        long newPos = decryptStream.Seek(0, SeekOrigin.Begin);
        byte[] buffer2 = new byte[100];
        decryptStream.Read(buffer2, 0, buffer2.Length);

        // Assert
        Assert.Equal(0, newPos);
        Assert.Equal(buffer1, buffer2);
    }

    [Fact]
    public void Seek_ToMiddle_ShouldDecryptCorrectly()
    {
        // Arrange
        byte[] key = GenerateTestKey(2);
        byte[] plaintext = new byte[1024];
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)(i & 0xFF);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Seek to position 500
        const int seekPos = 500;
        decryptStream.Seek(seekPos, SeekOrigin.Begin);

        byte[] decrypted = new byte[100];
        decryptStream.Read(decrypted, 0, decrypted.Length);

        // Assert - Should match plaintext from position 500
        byte[] expected = plaintext.AsSpan(seekPos, 100).ToArray();
        Assert.Equal(expected, decrypted);

        _output.WriteLine($"Seeked to position {seekPos}");
        _output.WriteLine($"Expected: {BytesToHex(expected)}");
        _output.WriteLine($"Decrypted: {BytesToHex(decrypted)}");
    }

    [Fact]
    public void Seek_AcrossBlockBoundaries_ShouldDecryptCorrectly()
    {
        // Arrange
        byte[] key = GenerateTestKey(3);
        byte[] plaintext = new byte[256];
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)i;

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act & Assert - Test seeking to various positions across 16-byte block boundaries
        int[] testPositions = [0, 15, 16, 17, 31, 32, 48, 64, 127, 128, 200];

        foreach (int pos in testPositions)
        {
            decryptStream.Seek(pos, SeekOrigin.Begin);
            byte[] buffer = new byte[1];
            int bytesRead = decryptStream.Read(buffer, 0, 1);

            Assert.Equal(1, bytesRead);
            Assert.Equal(plaintext[pos], buffer[0]);

            _output.WriteLine($"Position {pos}: Expected={plaintext[pos]:X2}, Got={buffer[0]:X2}");
        }
    }

    [Fact]
    public void Seek_RandomAccess_ShouldProduceConsistentResults()
    {
        // Arrange
        byte[] key = GenerateTestKey(4);
        byte[] plaintext = new byte[2048];
        RandomNumberGenerator.Fill(plaintext);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);

        // Act - Read the same position multiple times with seeks in between
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        const int testPos = 1000;
        const int testLength = 100;

        byte[] read1 = new byte[testLength];
        byte[] read2 = new byte[testLength];
        byte[] read3 = new byte[testLength];

        decryptStream.Seek(testPos, SeekOrigin.Begin);
        decryptStream.Read(read1, 0, testLength);

        decryptStream.Seek(500, SeekOrigin.Begin); // Seek somewhere else
        decryptStream.Read(new byte[50], 0, 50);   // Read some data

        decryptStream.Seek(testPos, SeekOrigin.Begin);
        decryptStream.Read(read2, 0, testLength);

        decryptStream.Seek(testPos, SeekOrigin.Begin);
        decryptStream.Read(read3, 0, testLength);

        // Assert - All reads should be identical
        Assert.Equal(read1, read2);
        Assert.Equal(read2, read3);
        Assert.Equal(plaintext.AsSpan(testPos, testLength).ToArray(), read1);
    }

    [Fact]
    public void Seek_FromCurrentPosition_ShouldWork()
    {
        // Arrange
        byte[] key = GenerateTestKey(5);
        byte[] plaintext = new byte[500];
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)(i & 0xFF);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act
        decryptStream.Read(new byte[100], 0, 100); // Position = 100
        decryptStream.Seek(50, SeekOrigin.Current); // Position = 150

        byte[] buffer = new byte[10];
        decryptStream.Read(buffer, 0, 10);

        // Assert
        Assert.Equal(plaintext.AsSpan(150, 10).ToArray(), buffer);
        Assert.Equal(160, decryptStream.Position);
    }

    [Fact]
    public void Seek_FromEnd_ShouldWork()
    {
        // Arrange
        byte[] key = GenerateTestKey(6);
        byte[] plaintext = new byte[500];
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)(i & 0xFF);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Seek to 50 bytes before end
        decryptStream.Seek(-50, SeekOrigin.End);

        byte[] buffer = new byte[10];
        decryptStream.Read(buffer, 0, 10);

        // Assert
        Assert.Equal(plaintext.AsSpan(450, 10).ToArray(), buffer);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Read_PartialBlocks_ShouldDecryptCorrectly()
    {
        // Arrange - Create data that's not a multiple of 16 bytes
        byte[] key = GenerateTestKey(7);
        byte[] plaintext = new byte[37]; // Not a multiple of 16
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)(i + 100);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Read in small chunks
        byte[] decrypted = new byte[plaintext.Length];
        int totalRead = 0;

        while (totalRead < plaintext.Length)
        {
            int toRead = Math.Min(5, plaintext.Length - totalRead); // Read 5 bytes at a time
            int bytesRead = decryptStream.Read(decrypted, totalRead, toRead);
            totalRead += bytesRead;

            if (bytesRead == 0)
                break;
        }

        // Assert
        Assert.Equal(plaintext.Length, totalRead);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Read_EmptyBuffer_ShouldReturnZero()
    {
        // Arrange
        byte[] key = GenerateTestKey(8);
        using var baseStream = new MemoryStream(new byte[100]);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act
        int bytesRead = decryptStream.Read(new byte[0], 0, 0);

        // Assert
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void Read_BeyondEnd_ShouldReturnZero()
    {
        // Arrange
        byte[] key = GenerateTestKey(9);
        byte[] data = new byte[100];
        byte[] encrypted = EncryptWithAesCtr(data, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Read all data
        decryptStream.Read(new byte[100], 0, 100);

        // Try to read more
        int bytesRead = decryptStream.Read(new byte[10], 0, 10);

        // Assert
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void Position_Property_ShouldTrackCorrectly()
    {
        // Arrange
        byte[] key = GenerateTestKey(10);
        using var baseStream = new MemoryStream(new byte[1000]);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act & Assert
        Assert.Equal(0, decryptStream.Position);

        decryptStream.Read(new byte[100], 0, 100);
        Assert.Equal(100, decryptStream.Position);

        decryptStream.Seek(500, SeekOrigin.Begin);
        Assert.Equal(500, decryptStream.Position);

        decryptStream.Position = 250;
        Assert.Equal(250, decryptStream.Position);
    }

    #endregion

    #region Large Data Tests

    [Fact]
    public void Read_LargeFile_ShouldDecryptCorrectly()
    {
        // Arrange - Simulate a large audio file (1 MB)
        byte[] key = GenerateTestKey(11);
        byte[] plaintext = new byte[1024 * 1024]; // 1 MB
        RandomNumberGenerator.Fill(plaintext);

        _output.WriteLine($"Encrypting {plaintext.Length} bytes...");
        byte[] encrypted = EncryptWithAesCtr(plaintext, key);

        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Decrypt the entire file
        _output.WriteLine("Decrypting...");
        byte[] decrypted = new byte[plaintext.Length];
        int totalRead = 0;

        while (totalRead < plaintext.Length)
        {
            int bytesRead = decryptStream.Read(decrypted, totalRead, plaintext.Length - totalRead);
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }

        // Assert
        Assert.Equal(plaintext.Length, totalRead);
        Assert.Equal(plaintext, decrypted);
        _output.WriteLine("Large file decryption successful!");
    }

    [Fact]
    public void Seek_InLargeFile_ShouldDecryptCorrectly()
    {
        // Arrange
        byte[] key = GenerateTestKey(12);
        byte[] plaintext = new byte[1024 * 512]; // 512 KB
        for (int i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)((i * 7) & 0xFF);

        byte[] encrypted = EncryptWithAesCtr(plaintext, key);
        using var baseStream = new MemoryStream(encrypted);
        using var decryptStream = new AudioDecryptStream(key, baseStream);

        // Act - Seek to various positions in the large file
        int[] testPositions = [0, 1000, 10000, 100000, 300000, 500000];

        foreach (int pos in testPositions)
        {
            decryptStream.Seek(pos, SeekOrigin.Begin);
            byte[] buffer = new byte[100];
            int bytesRead = decryptStream.Read(buffer, 0, buffer.Length);

            // Assert
            Assert.Equal(100, bytesRead);
            Assert.Equal(plaintext.AsSpan(pos, 100).ToArray(), buffer);

            _output.WriteLine($"Position {pos}: Decryption correct");
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Generates a deterministic test key based on a seed.
    /// </summary>
    private static byte[] GenerateTestKey(int seed)
    {
        byte[] key = new byte[16];
        for (int i = 0; i < 16; i++)
            key[i] = (byte)((seed * 17 + i * 31) & 0xFF);
        return key;
    }

    /// <summary>
    /// Encrypts data using AES-128-CTR with the same implementation as AudioDecryptStream.
    /// This is used to create test data for decryption tests.
    /// </summary>
    private static byte[] EncryptWithAesCtr(byte[] plaintext, byte[] key)
    {
        byte[] encrypted = new byte[plaintext.Length];

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();

        // Same IV as AudioDecryptStream
        byte[] iv = [0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
                     0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93];

        for (int i = 0; i < plaintext.Length; i += 16)
        {
            // Generate counter block
            byte[] counterBlock = new byte[16];
            iv.CopyTo(counterBlock, 0);

            // Add block index in big endian
            long blockIndex = i / 16;
            AddBigEndian(counterBlock, blockIndex);

            // Generate keystream
            byte[] keystream = new byte[16];
            encryptor.TransformBlock(counterBlock, 0, 16, keystream, 0);

            // XOR with plaintext
            int blockSize = Math.Min(16, plaintext.Length - i);
            for (int j = 0; j < blockSize; j++)
            {
                encrypted[i + j] = (byte)(plaintext[i + j] ^ keystream[j]);
            }
        }

        return encrypted;
    }

    /// <summary>
    /// Adds a value to a 128-bit big-endian integer.
    /// </summary>
    private static void AddBigEndian(byte[] buffer, long value)
    {
        ulong carry = (ulong)value;

        for (int i = buffer.Length - 1; i >= 0 && carry > 0; i--)
        {
            ulong sum = buffer[i] + carry;
            buffer[i] = (byte)sum;
            carry = sum >> 8;
        }
    }

    /// <summary>
    /// Converts a byte array to a hexadecimal string for debugging.
    /// </summary>
    private static string BytesToHex(byte[] bytes)
    {
        if (bytes.Length > 32)
            return BitConverter.ToString(bytes[..32]).Replace("-", " ") + "...";

        return BitConverter.ToString(bytes).Replace("-", " ");
    }

    #endregion
}
