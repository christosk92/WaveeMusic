using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Wavee.Core.Crypto;

namespace Wavee.Core.Connection;

/// <summary>
/// Spotify Access Point (AP) codec for encoding and decoding Shannon-encrypted packets.
/// Implements framing protocol with 3-byte headers and 4-byte MACs.
/// </summary>
/// <remarks>
/// Packet Format:
/// - Header: [command: 1 byte][payload_length: 2 bytes big-endian]
/// - Payload: Variable length
/// - MAC: 4 bytes
///
/// All packets are encrypted with Shannon cipher using incrementing nonces.
/// The entire header and payload are encrypted, then a MAC is appended.
///
/// </remarks>
public sealed class ApCodec : IDisposable
{
    private const int HeaderSize = 3;
    private const int MacSize = 4;

    private readonly ShannonCipher _encodeCipher;
    private readonly ShannonCipher _decodeCipher;
    private readonly ILogger? _logger;
    private uint _encodeNonce;
    private uint _decodeNonce;

    // Decode state machine
    private DecodeState _state;
    private byte _pendingCommand;
    private int _pendingPayloadSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApCodec"/> class.
    /// </summary>
    /// <param name="sendKey">32-byte key for encoding (sending) packets.</param>
    /// <param name="receiveKey">32-byte key for decoding (receiving) packets.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentException">Thrown if keys are not 32 bytes.</exception>
    public ApCodec(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey, ILogger? logger = null)
    {
        _encodeCipher = new ShannonCipher(sendKey);
        _decodeCipher = new ShannonCipher(receiveKey);
        _logger = logger;
        _encodeNonce = 0;
        _decodeNonce = 0;
        _state = DecodeState.Header;
    }

    /// <summary>
    /// Encodes a command and payload into an encrypted packet and writes it to the buffer writer.
    /// </summary>
    /// <param name="writer">The buffer writer to write the encoded packet to.</param>
    /// <param name="command">The command byte.</param>
    /// <param name="payload">The payload data.</param>
    /// <exception cref="ArgumentException">Thrown if payload is too large (max 65535 bytes).</exception>
    public void Encode(IBufferWriter<byte> writer, byte command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentException($"Payload too large: {payload.Length} bytes (max {ushort.MaxValue})", nameof(payload));

        _logger?.LogTrace("Encoding packet (command=0x{Command:X2}, payload={PayloadSize} bytes)", command, payload.Length);

        int totalSize = HeaderSize + payload.Length + MacSize;

        // Get memory from the writer (may use ArrayPool or other efficient allocation)
        var buffer = writer.GetSpan(totalSize);

        // Write header: [command: 1 byte][size: 2 bytes big-endian]
        buffer[0] = command;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], (ushort)payload.Length);

        // Copy payload
        payload.CopyTo(buffer[HeaderSize..]);

        // Set nonce and encrypt header + payload
        _encodeCipher.NonceU32(_encodeNonce);
        _encodeNonce++;

        var dataToEncrypt = buffer[..(HeaderSize + payload.Length)];
        _encodeCipher.Encrypt(dataToEncrypt);

        // Generate and append MAC
        var mac = buffer.Slice(HeaderSize + payload.Length, MacSize);
        _encodeCipher.Finish(mac);

        // Commit the written data
        writer.Advance(totalSize);
    }

    /// <summary>
    /// Gets the size of the encoded packet for a given payload size.
    /// </summary>
    /// <param name="payloadSize">The size of the payload.</param>
    /// <returns>The total size of the encoded packet (header + payload + MAC).</returns>
    public static int GetEncodedSize(int payloadSize) => HeaderSize + payloadSize + MacSize;

    /// <summary>
    /// Attempts to decode a packet from the input buffer sequence.
    /// Handles non-contiguous buffers efficiently using SequenceReader.
    /// </summary>
    /// <param name="buffer">The buffer sequence containing received data (may be non-contiguous).</param>
    /// <param name="consumed">The position in the sequence up to which data was consumed.</param>
    /// <param name="command">The decoded command byte (if successful).</param>
    /// <param name="payload">The decoded payload data (if successful).</param>
    /// <returns>True if a complete packet was decoded; false if more data is needed.</returns>
    /// <exception cref="ApCodecException">Thrown if the packet is malformed or MAC verification fails.</exception>
    public bool TryDecode(
        ref ReadOnlySequence<byte> buffer,
        out SequencePosition consumed,
        out byte command,
        out byte[] payload)
    {
        var reader = new SequenceReader<byte>(buffer);
        consumed = buffer.Start;
        command = 0;
        payload = [];

        // State machine: Header -> Payload
        if (_state == DecodeState.Header)
        {
            // Need at least header bytes
            if (reader.Remaining < HeaderSize)
                return false;

            // Read and decrypt header
            Span<byte> header = stackalloc byte[HeaderSize];
            if (!reader.TryCopyTo(header))
                return false;

            _decodeCipher.NonceU32(_decodeNonce);
            _decodeNonce++;
            _decodeCipher.Decrypt(header);

            // Parse header
            _pendingCommand = header[0];
            _pendingPayloadSize = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);

            reader.Advance(HeaderSize);
            _state = DecodeState.Payload;
        }

        if (_state == DecodeState.Payload)
        {
            int requiredSize = _pendingPayloadSize + MacSize;
            if (reader.Remaining < requiredSize)
            {
                // We have already consumed and decrypted the header in this codec state,
                // but the caller's buffer may still contain those header bytes. Signal that
                // the header bytes can be consumed so subsequent calls start at the payload.
                consumed = reader.Position; // position is after header advance
                return false;
            }

            // Read and decrypt payload
            payload = new byte[_pendingPayloadSize];
            if (!reader.TryCopyTo(payload))
                throw new ApCodecException("Failed to read payload from buffer sequence");

            _decodeCipher.Decrypt(payload);
            reader.Advance(_pendingPayloadSize);

            // Verify MAC
            Span<byte> expectedMac = stackalloc byte[MacSize];
            _decodeCipher.Finish(expectedMac);

            Span<byte> receivedMac = stackalloc byte[MacSize];
            if (!reader.TryCopyTo(receivedMac))
                throw new ApCodecException("Failed to read MAC from buffer sequence");

            if (!receivedMac.SequenceEqual(expectedMac))
            {
                _logger?.LogWarning("MAC verification failed for packet (command=0x{Command:X2}, payload={PayloadSize} bytes)",
                    _pendingCommand, _pendingPayloadSize);
                throw new ApCodecException("MAC verification failed - packet corrupted or tampered");
            }

            reader.Advance(MacSize);

            // Success!
            command = _pendingCommand;
            consumed = reader.Position;
            _state = DecodeState.Header;

            _logger?.LogTrace("Decoded packet (command=0x{Command:X2}, payload={PayloadSize} bytes)", command, payload.Length);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases resources used by the codec.
    /// </summary>
    public void Dispose()
    {
        // ShannonCipher does not implement IDisposable
        // No resources to dispose
    }

    private enum DecodeState
    {
        Header,
        Payload
    }
}
