using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Protocol;

namespace Wavee.Core.Connection;

/// <summary>
/// Spotify connection handshake implementation using Diffie-Hellman key exchange.
/// </summary>
/// <remarks>
/// Handshake Process:
/// 1. Client sends ClientHello with DH public key
/// 2. Server responds with APResponseMessage containing server's DH public key and signature
/// 3. Client verifies server signature using hardcoded RSA public key (MITM protection)
/// 4. Both sides compute shared secret and derive encryption keys via HMAC-SHA1
/// 5. Client sends ClientResponsePlaintext with challenge response
/// 6. Connection upgraded to Shannon-encrypted codec
///
/// This implementation uses System.IO.Pipelines for efficient async I/O even during
/// the unencrypted handshake phase.
/// </remarks>
public static class Handshake
{
    private const ulong SpotifyVersion = 124200290; // Desktop client version
    private const int ClientNonceSize = 16;

    /// <summary>
    /// Performs the complete handshake with a Spotify Access Point server.
    /// </summary>
    /// <param name="stream">Connected network stream to Spotify AP.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Configured ApTransport ready for sending/receiving packets.</returns>
    /// <exception cref="HandshakeException">Thrown if handshake fails or server verification fails.</exception>
    public static async Task<ApTransport> PerformHandshakeAsync(
        Stream stream,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger?.LogDebug("Handshake starting");

            // Generate DH key pair
            var localKeys = DiffieHellmanKeys.GenerateRandom();
            logger?.LogTrace("Generated DH public key ({KeyLength} bytes)", localKeys.PublicKey.Length);

            // Send ClientHello and accumulate packets for key derivation
            logger?.LogDebug("Sending ClientHello");
            var accumulator = await SendClientHelloAsync(stream, localKeys.PublicKey, cancellationToken);

            // Receive server response
            logger?.LogDebug("Receiving server response");
            var response = await ReceiveServerResponseAsync(stream, accumulator, cancellationToken);
            logger?.LogDebug("Received server response ({ServerKeyLength} bytes)", response.Gs.Length);

            // Verify server signature to prevent MITM attacks
            logger?.LogDebug("Verifying server signature");
            VerifyServerSignature(response.Gs, response.GsSignature, logger);
            logger?.LogDebug("Server signature verified");

            // Compute shared secret and derive keys
            var sharedSecret = localKeys.ComputeSharedSecret(response.Gs);
            var (challenge, sendKey, receiveKey) = DeriveKeys(sharedSecret, accumulator);
            logger?.LogTrace("Derived encryption keys (send={SendKeyLength} bytes, receive={ReceiveKeyLength} bytes)",
                sendKey.Length, receiveKey.Length);

            // Send client response with challenge
            logger?.LogDebug("Sending ClientResponsePlaintext");
            await SendClientResponseAsync(stream, challenge, cancellationToken);

            // Create codec and wrap in transport
            var codec = new ApCodec(sendKey, receiveKey, logger);
            var transport = ApTransport.Create(stream, codec, logger);

            logger?.LogInformation("Handshake complete, connection encrypted");
            return transport;
        }
        catch (HandshakeException ex)
        {
            logger?.LogError(ex, "Handshake failed: {Reason}", ex.Reason);
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Handshake failed with unexpected error");
            throw new HandshakeException(HandshakeReason.ProtocolError, "Handshake failed", ex);
        }
    }

    private static async Task<List<byte>> SendClientHelloAsync(
        Stream stream,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken)
    {
        // Generate random client nonce
        var clientNonce = new byte[ClientNonceSize];
        RandomNumberGenerator.Fill(clientNonce);

        // Determine platform
        var platform = GetPlatform();

        // Build ClientHello message
        var packet = new ClientHello
        {
            BuildInfo = new BuildInfo
            {
                Product = Product.Client,
                ProductFlags = { ProductFlags.ProductFlagNone },
                Platform = platform,
                Version = SpotifyVersion
            },
            CryptosuitesSupported = { Cryptosuite.Shannon },
            LoginCryptoHello = new LoginCryptoHelloUnion
            {
                DiffieHellman = new LoginCryptoDiffieHellmanHello
                {
                    Gc = ByteString.CopyFrom(publicKey.Span),
                    ServerKeysKnown = 1
                }
            },
            ClientNonce = ByteString.CopyFrom(clientNonce),
            Padding = ByteString.CopyFrom([0x1e])
        };

        // Serialize message
        var messageBytes = packet.ToByteArray();
        var totalSize = 2 + 4 + messageBytes.Length;

        // Use PipeWriter for efficient async I/O
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        try
        {
            // Get memory from the pipe
            var memory = writer.GetMemory(totalSize);
            var span = memory.Span;

            // Write packet: [0x00, 0x04][size: 4 bytes BE][message bytes]
            span[0] = 0;
            span[1] = 4;
            BinaryPrimitives.WriteUInt32BigEndian(span[2..], (uint)(2 + 4 + messageBytes.Length));
            messageBytes.CopyTo(span[6..]);

            // Copy to accumulator before async operation (span becomes invalid after await)
            var accumulatorData = span[..totalSize].ToArray();

            // Advance and flush
            writer.Advance(totalSize);
            await writer.FlushAsync(cancellationToken);

            // Return accumulator for key derivation
            return [.. accumulatorData];
        }
        finally
        {
            // Complete the pipe without disposing the stream
            await writer.CompleteAsync();
        }
    }

    private static async Task<ServerResponse> ReceiveServerResponseAsync(
        Stream stream,
        List<byte> accumulator,
        CancellationToken cancellationToken)
    {
        // Use PipeReader for efficient async I/O
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            // Read size (4 bytes)
            var sizeBuffer = await ReadExactBytesAsync(reader, 4, accumulator, cancellationToken);
            var size = BinaryPrimitives.ReadUInt32BigEndian(sizeBuffer);

            // Read message (size - 4 bytes)
            var messageBuffer = await ReadExactBytesAsync(reader, (int)(size - 4), accumulator, cancellationToken);

            // Parse APResponseMessage
            var response = APResponseMessage.Parser.ParseFrom(messageBuffer);

            // Extract DH data
            var gs = response.Challenge?.LoginCryptoChallenge?.DiffieHellman?.Gs?.ToByteArray()
                ?? throw new HandshakeException(HandshakeReason.ProtocolError, "Server did not provide DH public key");

            var gsSignature = response.Challenge?.LoginCryptoChallenge?.DiffieHellman?.GsSignature?.ToByteArray()
                ?? throw new HandshakeException(HandshakeReason.ProtocolError, "Server did not provide DH signature");

            return new ServerResponse(gs, gsSignature);
        }
        finally
        {
            // Complete the pipe without disposing the stream
            await reader.CompleteAsync();
        }
    }

    private static async Task SendClientResponseAsync(
        Stream stream,
        byte[] challenge,
        CancellationToken cancellationToken)
    {
        var packet = new ClientResponsePlaintext
        {
            LoginCryptoResponse = new LoginCryptoResponseUnion
            {
                DiffieHellman = new LoginCryptoDiffieHellmanResponse
                {
                    Hmac = ByteString.CopyFrom(challenge)
                }
            },
            PowResponse = new PoWResponseUnion(),
            CryptoResponse = new CryptoResponseUnion()
        };

        // Serialize message
        var messageBytes = packet.ToByteArray();
        var totalSize = 4 + messageBytes.Length;

        // Use PipeWriter for efficient async I/O
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        try
        {
            // Get memory from the pipe
            var memory = writer.GetMemory(totalSize);
            var span = memory.Span;

            // Write packet: [size: 4 bytes BE][message bytes]
            BinaryPrimitives.WriteUInt32BigEndian(span, (uint)totalSize);
            messageBytes.CopyTo(span[4..]);

            // Advance and flush
            writer.Advance(totalSize);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            // Complete the pipe without disposing the stream
            await writer.CompleteAsync();
        }
    }

    /// <summary>
    /// Reads an exact number of bytes from the PipeReader.
    /// </summary>
    private static async Task<byte[]> ReadExactBytesAsync(
        PipeReader reader,
        int count,
        List<byte> accumulator,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        int totalRead = 0;

        while (totalRead < count)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var readBuffer = result.Buffer;

            if (readBuffer.IsEmpty && result.IsCompleted)
                throw new HandshakeException(HandshakeReason.NetworkError, "Connection closed during handshake");

            // Calculate how many bytes we can read
            int bytesToRead = Math.Min((int)readBuffer.Length, count - totalRead);

            // Copy bytes to our buffer
            var slice = readBuffer.Slice(0, bytesToRead);
            slice.CopyTo(buffer.AsSpan(totalRead));

            // Add to accumulator for key derivation
            if (accumulator != null)
            {
                foreach (var segment in slice)
                {
                    accumulator.AddRange(segment.ToArray());
                }
            }

            totalRead += bytesToRead;

            // Tell the reader how much we consumed
            reader.AdvanceTo(readBuffer.GetPosition(bytesToRead));

            if (totalRead >= count)
                break;
        }

        return buffer;
    }

    private static void VerifyServerSignature(byte[] serverPublicKey, byte[] signature, ILogger? logger)
    {
        try
        {
            // Import RSA public key from raw modulus bytes
            // The ServerPublicKey is the raw 2048-bit modulus in big-endian format
            // The exponent is hardcoded as 65537 (0x010001)
            using var rsa = RSA.Create();
            var parameters = new RSAParameters
            {
                Modulus = ConnectionConstants.ServerPublicKey.ToArray(),
                Exponent = [0x01, 0x00, 0x01] // 65537 in big-endian
            };
            rsa.ImportParameters(parameters);

            // Hash server's DH public key
            var hash = SHA1.HashData(serverPublicKey);

            // Verify signature (PKCS#1 v1.5 with SHA-1)
            bool isValid = rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

            if (!isValid)
            {
                logger?.LogWarning("Server signature verification failed - potential MITM attack");
                throw new HandshakeException(HandshakeReason.ServerVerificationFailed, "Server signature verification failed - potential MITM attack");
            }
        }
        catch (HandshakeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Exception during server signature verification");
            throw new HandshakeException(HandshakeReason.ServerVerificationFailed, "Failed to verify server signature", ex);
        }
    }

    private static (byte[] challenge, byte[] sendKey, byte[] receiveKey) DeriveKeys(byte[] sharedSecret, List<byte> packets)
    {
        try
        {
            // Generate data using HMAC-SHA1: 5 iterations
            var data = new byte[100]; // 5 * 20 bytes
            var packetBytes = packets.ToArray();

            for (int i = 1; i <= 5; i++)
            {
                using var hmac = new HMACSHA1(sharedSecret);
                hmac.TransformBlock(packetBytes, 0, packetBytes.Length, null, 0);
                hmac.TransformFinalBlock([(byte)i], 0, 1);
                var hash = hmac.Hash!;
                hash.CopyTo(data.AsSpan((i - 1) * 20, 20));
            }

            // Compute challenge: HMAC-SHA1(data[0..20], packets)
            byte[] challenge;
            using (var hmac = new HMACSHA1(data[..20]))
            {
                challenge = hmac.ComputeHash(packetBytes);
            }

            // Extract keys
            var sendKey = data[20..52];
            var receiveKey = data[52..84];

            return (challenge, sendKey, receiveKey);
        }
        catch (Exception ex)
        {
            throw new HandshakeException(HandshakeReason.ProtocolError, "Failed to derive encryption keys", ex);
        }
    }

    private static Platform GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? Platform.Win32X86  // Note: There's no Win32X8664 in the enum, using Win32X86 for Windows
                : Platform.Win32X86;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Platform.LinuxX8664,
                Architecture.Arm or Architecture.Arm64 => Platform.LinuxArm,
                _ => Platform.LinuxX86
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? Platform.OsxX8664
                : Platform.OsxX86;
        }

        // Default fallback
        return Platform.LinuxX86;
    }

    private readonly record struct ServerResponse(byte[] Gs, byte[] GsSignature);
}
