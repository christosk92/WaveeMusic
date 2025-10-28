using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Connection;

/// <summary>
/// High-level transport for Spotify Access Point connections.
/// Wraps a stream with ApCodec for automatic packet framing and buffering.
/// </summary>
/// <remarks>
/// This transport provides:
/// - Automatic buffer management via System.IO.Pipelines
/// - Backpressure handling
/// - Efficient memory pooling (ArrayPool)
/// - Clean async/await API
/// - Proper cancellation support
///
/// Usage:
/// <code>
/// var stream = await ConnectToSpotifyAsync();
/// var transport = await Handshake.PerformHandshakeAsync(stream);
///
/// // Send a packet
/// await transport.SendAsync(0xAB, payload);
///
/// // Receive packets
/// while (true)
/// {
///     var packet = await transport.ReceiveAsync();
///     if (packet == null) break; // Connection closed
///     ProcessPacket(packet.Value.command, packet.Value.payload);
/// }
/// </code>
/// </remarks>
public sealed class ApTransport : IApTransport
{
    private readonly Stream _stream;
    private readonly ApCodec _codec;
    private readonly ILogger? _logger;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    private ApTransport(Stream stream, ApCodec codec, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(codec);

        _stream = stream;
        _codec = codec;
        _logger = logger;

        // Wrap the stream with Pipelines for efficient buffering
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: false));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: false));
    }

    /// <summary>
    /// Creates a transport from a network stream and codec.
    /// Typically called by Handshake.PerformHandshakeAsync after completing the handshake.
    /// </summary>
    /// <param name="stream">The connected network stream.</param>
    /// <param name="codec">The configured ApCodec with encryption keys.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <returns>A ready-to-use transport.</returns>
    public static ApTransport Create(Stream stream, ApCodec codec, ILogger? logger = null)
    {
        return new ApTransport(stream, codec, logger);
    }

    /// <summary>
    /// Sends a packet to the Spotify server.
    /// </summary>
    /// <param name="command">The command byte.</param>
    /// <param name="payload">The payload data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the transport has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation was canceled.</exception>
    public async ValueTask SendAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCts.Token);

        // Encode packet into the pipe writer
        _codec.Encode(_writer, command, payload.Span);

        // Flush to network
        await _writer.FlushAsync(linkedCts.Token);
    }

    /// <summary>
    /// Receives a packet from the Spotify server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple containing the command byte and payload, or null if the connection was closed.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown if the transport has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation was canceled.</exception>
    /// <exception cref="ApCodecException">Thrown if packet decoding fails or MAC verification fails.</exception>
    public async ValueTask<(byte command, byte[] payload)?> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCts.Token);

        while (true)
        {
            // Read data from the pipe
            var result = await _reader.ReadAsync(linkedCts.Token);
            var buffer = result.Buffer;

            try
            {
                // Try to decode a complete packet
                var decodeSuccess = _codec.TryDecode(ref buffer, out var consumed, out var command, out var payload);
                if (decodeSuccess)
                {
                    // Success! Mark the consumed bytes
                    _reader.AdvanceTo(consumed);
                    return (command, payload);
                }

                // Partial decode: consume what the codec says is safe (e.g., header)
                // while allowing the pipe to continue buffering more data.
                _reader.AdvanceTo(consumed, buffer.End);

                // Check if the connection was closed
                if (result.IsCompleted)
                {
                    // No more data coming and we couldn't decode a packet
                    if (buffer.Length > 0)
                    {
                        // Unexpected: partial packet at end of stream
                        _logger?.LogWarning("Connection closed with {RemainingBytes} bytes remaining (incomplete packet)", buffer.Length);
                        throw new ApCodecException($"Connection closed with {buffer.Length} bytes remaining (incomplete packet)");
                    }

                    _logger?.LogDebug("Connection closed gracefully");
                    return null; // Clean connection close
                }
            }
            catch (ApCodecException)
            {
                // On codec error, mark entire buffer as consumed and rethrow
                _reader.AdvanceTo(buffer.End);
                throw;
            }
        }
    }

    /// <summary>
    /// Disposes the transport and underlying resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _logger?.LogTrace("Disposing transport");

        // Signal cancellation to any pending operations
        _disposeCts.Cancel();

        try
        {
            // Complete the pipes gracefully
            await _reader.CompleteAsync();
            await _writer.CompleteAsync();

            // Dispose underlying stream
            await _stream.DisposeAsync();
        }
        finally
        {
            _codec.Dispose();
            _disposeCts.Dispose();
        }
    }
}
