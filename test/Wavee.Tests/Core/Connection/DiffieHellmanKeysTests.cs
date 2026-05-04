using FluentAssertions;
using System.Numerics;
using Wavee.Core.Connection;
using Xunit;

namespace Wavee.Tests.Core.Connection;

/// <summary>
/// Tests for DiffieHellmanKeys.
/// Validates 768-bit Diffie-Hellman key exchange cryptographic operations.
/// </summary>
public class DiffieHellmanKeysTests
{
    [Fact]
    public void GenerateRandom_ShouldCreateValidKeyPair()
    {
        // ============================================================
        // WHY: Ensures the DH key pair is correctly generated.
        //      Private key must be random, public key = g^private mod p
        // ============================================================

        // Act
        var keys = DiffieHellmanKeys.GenerateRandom();

        // Assert
        keys.Should().NotBeNull();
        keys.PublicKey.Length.Should().BeGreaterThan(0, "DH public key should not be empty");
    }

    [Fact]
    public void GenerateRandom_MultipleCalls_ShouldProduceDifferentKeys()
    {
        // ============================================================
        // WHY: Each connection must use a unique key pair for security.
        //      Two instances should generate different keys.
        // ============================================================

        // Act
        var keys1 = DiffieHellmanKeys.GenerateRandom();
        var keys2 = DiffieHellmanKeys.GenerateRandom();

        // Assert
        keys1.PublicKey.ToArray().Should().NotEqual(keys2.PublicKey.ToArray(),
            "Public keys must be unique for security");
    }

    [Fact]
    public void PublicKey_ShouldBeWithinValidRange()
    {
        // ============================================================
        // WHY: DH public key must satisfy 1 < public < prime.
        //      Invalid range indicates cryptographic error.
        // ============================================================

        // Act
        var keys = DiffieHellmanKeys.GenerateRandom();
        var publicKeyValue = new BigInteger(keys.PublicKey.Span, isUnsigned: true, isBigEndian: true);
        var prime = new BigInteger(ConnectionConstants.DiffieHellmanPrime, isUnsigned: true, isBigEndian: true);

        // Assert
        publicKeyValue.Should().BeGreaterThan(BigInteger.One,
            "DH public key must be greater than 1");
        publicKeyValue.Should().BeLessThan(prime,
            "DH public key must be less than the prime modulus");
    }

    [Fact]
    public void ComputeSharedSecret_WithValidServerKey_ShouldReturnValidSecret()
    {
        // ============================================================
        // WHY: Shared secret = server_public^private mod p.
        //      This is the core DH operation establishing encryption key.
        // ============================================================

        // Arrange
        var clientKeys = DiffieHellmanKeys.GenerateRandom();
        var serverKeys = DiffieHellmanKeys.GenerateRandom();

        // Act
        var clientSharedSecret = clientKeys.ComputeSharedSecret(serverKeys.PublicKey.Span);
        var serverSharedSecret = serverKeys.ComputeSharedSecret(clientKeys.PublicKey.Span);

        // Assert
        clientSharedSecret.Should().NotBeNull();
        clientSharedSecret.Length.Should().BeGreaterThan(0, "Shared secret should not be empty");

        // Both sides should compute the same shared secret
        clientSharedSecret.Should().Equal(serverSharedSecret,
            "DH property: (g^a mod p)^b mod p = (g^b mod p)^a mod p");
    }

    [Fact]
    public void ComputeSharedSecret_WithEmptyServerKey_ShouldThrow()
    {
        // ============================================================
        // WHY: Invalid server keys (empty, out of range) must
        //      be rejected to prevent cryptographic attacks.
        // ============================================================

        // Arrange
        var keys = DiffieHellmanKeys.GenerateRandom();
        var emptyKey = Array.Empty<byte>();

        // Act
        Action act = () => keys.ComputeSharedSecret(emptyKey);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateRandom_MultipleKeys_ShouldAllBeUnique()
    {
        // ============================================================
        // WHY: Verifies cryptographic randomness. Multiple key pairs
        //      must be unique (collision probability is negligible).
        // ============================================================

        // Act
        var keysList = new List<byte[]>();
        for (int i = 0; i < 5; i++)
        {
            var keys = DiffieHellmanKeys.GenerateRandom();
            keysList.Add(keys.PublicKey.ToArray());
        }

        // Assert
        for (int i = 0; i < keysList.Count; i++)
        {
            for (int j = i + 1; j < keysList.Count; j++)
            {
                keysList[i].Should().NotEqual(keysList[j],
                    $"Key pair {i} and {j} should be unique");
            }
        }
    }
}
