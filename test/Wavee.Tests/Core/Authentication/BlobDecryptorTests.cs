using System;
using FluentAssertions;
using Wavee.Core.Authentication;
using Wavee.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for BlobDecryptor class.
/// Validates cryptographic blob decryption, error handling, and security.
/// </summary>
public class BlobDecryptorTests
{
    [Fact]
    public void Decrypt_WithEmptyBlob_ShouldThrowException()
    {
        // ============================================================
        // WHY: Empty blobs should be rejected during Base64 decode
        //      or early cryptographic operations.
        // ============================================================

        // Arrange
        var username = "testuser";
        var emptyBlob = Array.Empty<byte>();
        var deviceId = TestHelpers.GenerateRandomBytes(16);

        // Act & Assert
        // Empty blob will fail during Base64 decode or AES decryption
        Assert.ThrowsAny<Exception>(() =>
            BlobDecryptor.Decrypt(username, emptyBlob, deviceId));
    }

    [Fact]
    public void Decrypt_WithInvalidBase64_ShouldThrowAuthenticationException()
    {
        // ============================================================
        // WHY: Corrupted or non-Base64 data should fail gracefully
        //      with a clear error message.
        // ============================================================

        // Arrange
        var username = "testuser";
        var invalidBase64 = "This is not valid Base64!!!"u8.ToArray();
        var deviceId = TestHelpers.GenerateRandomBytes(16);

        // Act & Assert
        var exception = Assert.Throws<AuthenticationException>(() =>
            BlobDecryptor.Decrypt(username, invalidBase64, deviceId));

        exception.Reason.Should().Be(AuthenticationFailureReason.InvalidBlob);
        exception.Message.Should().Contain("decode");
    }

    [Fact]
    public void Decrypt_WithEmptyDeviceId_ShouldThrowAuthenticationException()
    {
        // ============================================================
        // WHY: Device ID is required for deriving decryption key.
        //      Empty device ID should be rejected.
        // ============================================================

        // Arrange
        var username = "testuser";
        var blob = Convert.ToBase64String(TestHelpers.GenerateRandomBytes(32));
        var emptyDeviceId = Array.Empty<byte>();

        // Act & Assert
        var exception = Assert.Throws<AuthenticationException>(() =>
            BlobDecryptor.Decrypt(username, System.Text.Encoding.UTF8.GetBytes(blob), emptyDeviceId));

        // Exception will occur during decryption process
        exception.Reason.Should().Be(AuthenticationFailureReason.InvalidBlob);
    }

    [Fact]
    public void Decrypt_WithTooShortBlob_ShouldThrowAuthenticationException()
    {
        // ============================================================
        // WHY: Blobs must be at least 16 bytes (AES block size).
        //      Short blobs indicate corruption or invalid data.
        // ============================================================

        // Arrange
        var username = "testuser";
        var shortBlob = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var deviceId = TestHelpers.GenerateRandomBytes(16);

        // Act & Assert
        var exception = Assert.Throws<AuthenticationException>(() =>
            BlobDecryptor.Decrypt(username, System.Text.Encoding.UTF8.GetBytes(shortBlob), deviceId));

        exception.Reason.Should().Be(AuthenticationFailureReason.InvalidBlob);
    }

    [Fact]
    public void Decrypt_WithCorruptedBlobStructure_ShouldThrowException()
    {
        // ============================================================
        // WHY: Random data should fail during Base64 decode,
        //      AES decryption, or blob structure parsing.
        // ============================================================

        // Arrange
        var username = "testuser";
        // Create a blob with valid Base64 but invalid structure after decryption
        var corruptedData = TestHelpers.GenerateRandomBytes(32);
        var corruptedBlob = Convert.ToBase64String(corruptedData);
        var deviceId = TestHelpers.GenerateRandomBytes(16);

        // Act & Assert
        // Corrupted data will fail at some stage (decode, decrypt, or parse)
        Assert.ThrowsAny<Exception>(() =>
            BlobDecryptor.Decrypt(username, System.Text.Encoding.UTF8.GetBytes(corruptedBlob), deviceId));
    }

    [Fact]
    public void Decrypt_WithNullUsername_ShouldThrowException()
    {
        // ============================================================
        // WHY: Username is required for PBKDF2 key derivation.
        //      Null username should be rejected.
        // ============================================================

        // Arrange
        string? nullUsername = null;
        var blob = Convert.ToBase64String(TestHelpers.GenerateRandomBytes(32));
        var deviceId = TestHelpers.GenerateRandomBytes(16);

        // Act & Assert
        // Will throw either ArgumentNullException or AuthenticationException
        Assert.ThrowsAny<Exception>(() =>
            BlobDecryptor.Decrypt(nullUsername!, System.Text.Encoding.UTF8.GetBytes(blob), deviceId));
    }
}
