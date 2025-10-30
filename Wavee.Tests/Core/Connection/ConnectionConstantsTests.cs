using FluentAssertions;
using Wavee.Core.Connection;
using Xunit;

namespace Wavee.Tests.Core.Connection;

/// <summary>
/// Tests for ConnectionConstants.
/// Validates hardcoded cryptographic parameters used in Spotify handshake.
/// </summary>
public class ConnectionConstantsTests
{
    [Fact]
    public void ServerPublicKey_ShouldBe256Bytes()
    {
        // ============================================================
        // WHY: Spotify's RSA public key is 2048 bits (256 bytes).
        //      Used to verify server signatures during handshake.
        //      Incorrect size indicates wrong key data.
        // ============================================================

        // Act
        var keyLength = ConnectionConstants.ServerPublicKey.Length;

        // Assert
        keyLength.Should().Be(256,
            "Spotify's RSA public key is 2048 bits (256 bytes)");
    }

    [Fact]
    public void ServerPublicKey_ShouldNotBeAllZeros()
    {
        // ============================================================
        // WHY: An all-zero key would be invalid and fail signature
        //      verification. This ensures the hardcoded key contains
        //      actual data.
        // ============================================================

        // Act
        var hasNonZeroByte = false;
        foreach (var b in ConnectionConstants.ServerPublicKey)
        {
            if (b != 0)
            {
                hasNonZeroByte = true;
                break;
            }
        }

        // Assert
        hasNonZeroByte.Should().BeTrue(
            "RSA public key must contain actual key data, not all zeros");
    }

    [Fact]
    public void DiffieHellmanPrime_ShouldBe96Bytes()
    {
        // ============================================================
        // WHY: Spotify uses 768-bit Diffie-Hellman (Oakley Group 1).
        //      The prime number is 96 bytes.
        // ============================================================

        // Act
        var primeLength = ConnectionConstants.DiffieHellmanPrime.Length;

        // Assert
        primeLength.Should().Be(96,
            "Spotify DH prime is 768 bits (96 bytes)");
    }

    [Fact]
    public void DiffieHellmanGenerator_ShouldBe2()
    {
        // ============================================================
        // WHY: Oakley Group 1 uses generator g=2.
        //      This is standard for this DH group.
        // ============================================================

        // Act
        var generator = ConnectionConstants.DiffieHellmanGenerator;

        // Assert
        generator.Should().Be(2,
            "Oakley Group 1 uses generator g=2");
    }
}
