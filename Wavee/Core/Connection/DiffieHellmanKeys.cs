using System.Numerics;
using System.Security.Cryptography;

namespace Wavee.Core.Connection;

/// <summary>
/// Diffie-Hellman key pair for Spotify handshake.
/// Uses 768-bit DH (Oakley Group 1) with Spotify's parameters.
/// </summary>
public sealed class DiffieHellmanKeys
{
    private const int PrivateKeySize = 95; // 95 bytes for private key

    private readonly BigInteger _privateKey;
    private readonly BigInteger _publicKey;
    private readonly byte[] _publicKeyBytes;

    private DiffieHellmanKeys(BigInteger privateKey, BigInteger publicKey, byte[] publicKeyBytes)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
        _publicKeyBytes = publicKeyBytes;
    }

    /// <summary>
    /// Gets the public key as a big-endian byte array.
    /// </summary>
    public ReadOnlyMemory<byte> PublicKey => _publicKeyBytes;

    /// <summary>
    /// Generates a new random Diffie-Hellman key pair.
    /// </summary>
    /// <returns>A new <see cref="DiffieHellmanKeys"/> instance with randomly generated keys.</returns>
    public static DiffieHellmanKeys GenerateRandom()
    {
        // Generate 95 random bytes for the private key (same as librespot)
        Span<byte> privateKeyBytes = stackalloc byte[PrivateKeySize];
        RandomNumberGenerator.Fill(privateKeyBytes);

        // Convert to BigInteger (little-endian for .NET BigInteger)
        var privateKey = new BigInteger(privateKeyBytes, isUnsigned: true, isBigEndian: false);

        // Get DH parameters
        var generator = new BigInteger(ConnectionConstants.DiffieHellmanGenerator);
        var prime = new BigInteger(ConnectionConstants.DiffieHellmanPrime, isUnsigned: true, isBigEndian: true);

        // Compute public key: g^private mod p
        var publicKey = BigInteger.ModPow(generator, privateKey, prime);

        // Convert public key to big-endian byte array (Spotify protocol format)
        var publicKeyBytes = publicKey.ToByteArray(isUnsigned: true, isBigEndian: true);

        return new DiffieHellmanKeys(privateKey, publicKey, publicKeyBytes);
    }

    /// <summary>
    /// Computes the shared secret from the remote party's public key.
    /// </summary>
    /// <param name="remotePublicKey">The remote party's public key (big-endian).</param>
    /// <returns>The shared secret as a big-endian byte array.</returns>
    /// <exception cref="ArgumentException">Thrown if the remote public key is invalid.</exception>
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> remotePublicKey)
    {
        if (remotePublicKey.IsEmpty)
            throw new ArgumentException("Remote public key cannot be empty", nameof(remotePublicKey));

        // Convert remote public key from big-endian
        var remotePubKey = new BigInteger(remotePublicKey, isUnsigned: true, isBigEndian: true);
        var prime = new BigInteger(ConnectionConstants.DiffieHellmanPrime, isUnsigned: true, isBigEndian: true);

        // Compute shared secret: remote_public^private mod p
        var sharedSecret = BigInteger.ModPow(remotePubKey, _privateKey, prime);

        // Convert to big-endian byte array
        return sharedSecret.ToByteArray(isUnsigned: true, isBigEndian: true);
    }
}
