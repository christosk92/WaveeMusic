using System.Text;
using Wavee.Core.Crypto;
using Xunit;

namespace Wavee.Tests.Core.Crypto;

/// <summary>
/// Unit tests for Shannon cipher implementation.
/// Verifies correctness of encryption, decryption, nonce handling, and MAC generation.
/// </summary>
public class ShannonCipherTests
{
    private readonly ITestOutputHelper _output;

    public ShannonCipherTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BasicEncryptDecrypt_ShouldBeReversible()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)i;

        var cipher = new ShannonCipher(key);
        cipher.NonceU32(0);

        byte[] plaintext = Encoding.UTF8.GetBytes("Hello, Shannon Cipher!");
        byte[] encrypted = (byte[])plaintext.Clone();

        // Act
        cipher.Encrypt(encrypted);

        _output.WriteLine($"Plaintext:  {BytesToHex(plaintext)}");
        _output.WriteLine($"Encrypted:  {BytesToHex(encrypted)}");

        // Create new cipher for decryption
        var decipher = new ShannonCipher(key);
        decipher.NonceU32(0);

        byte[] decrypted = (byte[])encrypted.Clone();
        decipher.Decrypt(decrypted);

        _output.WriteLine($"Decrypted:  {BytesToHex(decrypted)}");

        // Assert
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_ShouldModifyData()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 7);

        byte[] plaintext = Encoding.UTF8.GetBytes("Test data");
        byte[] encrypted = (byte[])plaintext.Clone();

        var cipher = new ShannonCipher(key);
        cipher.NonceU32(0);

        // Act
        cipher.Encrypt(encrypted);

        _output.WriteLine($"Plaintext: {BytesToHex(plaintext)}");
        _output.WriteLine($"Encrypted: {BytesToHex(encrypted)}");

        // Assert - encrypted should be different from plaintext
        Assert.NotEqual(plaintext, encrypted);
    }

    [Fact]
    public void DifferentNonces_ShouldProduceDifferentCiphertext()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 13);

        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Act - encrypt with nonce 0
        var cipher1 = new ShannonCipher(key);
        cipher1.NonceU32(0);
        byte[] encrypted1 = (byte[])data.Clone();
        cipher1.Encrypt(encrypted1);

        // Encrypt with nonce 1
        var cipher2 = new ShannonCipher(key);
        cipher2.NonceU32(1);
        byte[] encrypted2 = (byte[])data.Clone();
        cipher2.Encrypt(encrypted2);

        _output.WriteLine($"Original:  {BytesToHex(data)}");
        _output.WriteLine($"Nonce 0:   {BytesToHex(encrypted1)}");
        _output.WriteLine($"Nonce 1:   {BytesToHex(encrypted2)}");

        // Assert - different nonces should produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void BigEndianNonce_ShouldBeHandledCorrectly()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 17);

        byte[] data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        // Act - encrypt with different big-endian nonces
        var cipher1 = new ShannonCipher(key);
        cipher1.NonceU32(0x00000000);
        byte[] encrypted1 = (byte[])data.Clone();
        cipher1.Encrypt(encrypted1);

        var cipher2 = new ShannonCipher(key);
        cipher2.NonceU32(0x00000100); // Big-endian 256
        byte[] encrypted2 = (byte[])data.Clone();
        cipher2.Encrypt(encrypted2);

        var cipher3 = new ShannonCipher(key);
        cipher3.NonceU32(0x01000000); // Big-endian 16777216
        byte[] encrypted3 = (byte[])data.Clone();
        cipher3.Encrypt(encrypted3);

        _output.WriteLine($"Nonce 0x00000000: {BytesToHex(encrypted1)}");
        _output.WriteLine($"Nonce 0x00000100: {BytesToHex(encrypted2)}");
        _output.WriteLine($"Nonce 0x01000000: {BytesToHex(encrypted3)}");

        // Assert - all should be different
        Assert.NotEqual(encrypted1, encrypted2);
        Assert.NotEqual(encrypted1, encrypted3);
        Assert.NotEqual(encrypted2, encrypted3);
    }

    [Fact]
    public void MacGeneration_ShouldBeConsistent()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 19);

        byte[] data = Encoding.UTF8.GetBytes("Test MAC generation");

        var cipher = new ShannonCipher(key);
        cipher.NonceU32(0);

        // Act
        byte[] encrypted = (byte[])data.Clone();
        cipher.Encrypt(encrypted);

        byte[] mac1 = new byte[4];
        cipher.Finish(mac1);

        // Generate MAC again with same key and nonce
        var cipher2 = new ShannonCipher(key);
        cipher2.NonceU32(0);
        byte[] encrypted2 = (byte[])data.Clone();
        cipher2.Encrypt(encrypted2);

        byte[] mac2 = new byte[4];
        cipher2.Finish(mac2);

        _output.WriteLine($"Data:  {BytesToHex(data)}");
        _output.WriteLine($"MAC 1: {BytesToHex(mac1)}");
        _output.WriteLine($"MAC 2: {BytesToHex(mac2)}");

        // Assert - MACs should be identical
        Assert.Equal(mac1, mac2);
    }

    [Fact]
    public void MacVerification_WithCorrectMac_ShouldPass()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 23);

        byte[] plaintext = Encoding.UTF8.GetBytes("MAC verification test");

        // Encrypt and generate MAC
        var encryptor = new ShannonCipher(key);
        encryptor.NonceU32(42);

        byte[] ciphertext = (byte[])plaintext.Clone();
        encryptor.Encrypt(ciphertext);

        byte[] mac = new byte[4];
        encryptor.Finish(mac);

        _output.WriteLine($"Plaintext:  {BytesToHex(plaintext)}");
        _output.WriteLine($"Ciphertext: {BytesToHex(ciphertext)}");
        _output.WriteLine($"MAC:        {BytesToHex(mac)}");

        // Verify by decrypting (not encrypting again!)
        var decryptor = new ShannonCipher(key);
        decryptor.NonceU32(42);

        byte[] decrypted = (byte[])ciphertext.Clone();
        decryptor.Decrypt(decrypted);

        // Assert - should not throw
        var exception = Record.Exception(() => decryptor.CheckMac(mac));
        Assert.Null(exception);
        Assert.Equal(plaintext, decrypted); // Verify decryption worked

        _output.WriteLine("MAC verification: PASS");
    }

    [Fact]
    public void MacVerification_WithIncorrectMac_ShouldThrow()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 29);

        byte[] plaintext = Encoding.UTF8.GetBytes("MAC verification test");

        // Encrypt and generate MAC
        var encryptor = new ShannonCipher(key);
        encryptor.NonceU32(123);

        byte[] ciphertext = (byte[])plaintext.Clone();
        encryptor.Encrypt(ciphertext);

        byte[] mac = new byte[4];
        encryptor.Finish(mac);

        // Corrupt the MAC
        byte[] corruptedMac = (byte[])mac.Clone();
        corruptedMac[0] ^= 0xFF;

        _output.WriteLine($"Original MAC:  {BytesToHex(mac)}");
        _output.WriteLine($"Corrupted MAC: {BytesToHex(corruptedMac)}");

        // Verify with new cipher (decrypt the ciphertext)
        var decryptor = new ShannonCipher(key);
        decryptor.NonceU32(123);

        byte[] decrypted = (byte[])ciphertext.Clone();
        decryptor.Decrypt(decrypted);

        // Assert - should throw InvalidDataException
        Assert.Throws<InvalidDataException>(() => decryptor.CheckMac(corruptedMac));

        _output.WriteLine("MAC verification correctly threw exception");
    }

    [Fact]
    public void EncryptDecrypt_WithVariousDataSizes_ShouldWork()
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)(i * 31);

        int[] dataSizes = { 1, 3, 4, 7, 16, 31, 32, 64, 100, 255, 1024 };

        foreach (int size in dataSizes)
        {
            // Arrange
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
                data[i] = (byte)(i & 0xFF);

            // Act - encrypt
            var encryptor = new ShannonCipher(key);
            encryptor.NonceU32(0);
            byte[] encrypted = (byte[])data.Clone();
            encryptor.Encrypt(encrypted);

            // Decrypt
            var decryptor = new ShannonCipher(key);
            decryptor.NonceU32(0);
            decryptor.Decrypt(encrypted);

            // Assert
            Assert.Equal(data, encrypted);
            _output.WriteLine($"Size {size:D4}: PASS");
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(65535u)]
    [InlineData(0xFFFFFFFFu)]
    public void NonceU32_WithVariousValues_ShouldWork(uint nonce)
    {
        // Arrange
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
            key[i] = (byte)i;

        byte[] data = new byte[] { 0x12, 0x34, 0x56, 0x78 };

        // Act
        var cipher = new ShannonCipher(key);
        cipher.NonceU32(nonce);
        byte[] encrypted = (byte[])data.Clone();
        cipher.Encrypt(encrypted);

        // Decrypt
        var decipher = new ShannonCipher(key);
        decipher.NonceU32(nonce);
        decipher.Decrypt(encrypted);

        // Assert
        Assert.Equal(data, encrypted);
        _output.WriteLine($"Nonce 0x{nonce:X8}: PASS - Encrypted: {BytesToHex(encrypted)}");
    }

    [Fact]
    public void Constructor_WithInvalidKeyLength_ShouldThrow()
    {
        // Arrange
        byte[] shortKey = new byte[16];
        byte[] longKey = new byte[64];

        // Assert
        Assert.Throws<ArgumentException>(() => new ShannonCipher(shortKey));
        Assert.Throws<ArgumentException>(() => new ShannonCipher(longKey));

        _output.WriteLine("Invalid key lengths correctly rejected");
    }

    [Fact]
    public void Finish_WithInvalidMacLength_ShouldThrow()
    {
        // Arrange
        byte[] key = new byte[32];
        var cipher = new ShannonCipher(key);
        cipher.NonceU32(0);

        byte[] shortMac = new byte[2];
        byte[] longMac = new byte[8];

        // Assert
        Assert.Throws<ArgumentException>(() => cipher.Finish(shortMac));

        var cipher2 = new ShannonCipher(key);
        cipher2.NonceU32(0);
        Assert.Throws<ArgumentException>(() => cipher2.Finish(longMac));

        _output.WriteLine("Invalid MAC lengths correctly rejected");
    }

    #region Helper Methods

    private static string BytesToHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", " ");
    }

    #endregion
}
