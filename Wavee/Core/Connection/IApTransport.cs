namespace Wavee.Core.Connection;

/// <summary>
/// Interface for Spotify Access Point transport layer.
/// Provides packet send/receive operations with async support.
/// </summary>
public interface IApTransport : IAsyncDisposable
{
    /// <summary>
    /// Sends a packet to the Access Point.
    /// </summary>
    /// <param name="command">Command byte (packet type).</param>
    /// <param name="payload">Packet payload data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when packet is sent.</returns>
    ValueTask SendAsync(byte command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives a packet from the Access Point.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Tuple of (command, payload) if packet received, or null if connection closed.
    /// </returns>
    ValueTask<(byte command, byte[] payload)?> ReceiveAsync(CancellationToken cancellationToken = default);
}
