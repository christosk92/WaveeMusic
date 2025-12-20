using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;

namespace Wavee.Core.Audio;

/// <summary>
/// Manages AudioKey requests for encrypted audio file decryption.
/// </summary>
/// <remarks>
/// AudioKey is a 16-byte AES key used to decrypt Spotify audio files.
/// Keys are requested via the AP protocol:
/// - Request: PacketType.RequestKey (0x0c) with FileId + TrackId + Seq + 0x0000
/// - Response: PacketType.AesKey (0x0d) with Seq + Key
/// - Error: PacketType.AesKeyError (0x0e) with Seq + ErrorCode
/// </remarks>
public sealed class AudioKeyManager : IAsyncDisposable
{
    /// <summary>
    /// Timeout for AudioKey requests.
    /// </summary>
    private static readonly TimeSpan KeyResponseTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Maximum number of retry attempts for AudioKey requests.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Delays between retry attempts (exponential backoff).
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,                      // First attempt: immediate
        TimeSpan.FromMilliseconds(500),     // Retry 1: 500ms delay
        TimeSpan.FromMilliseconds(1000)     // Retry 2: 1000ms delay
    ];

    private readonly ISession _session;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private uint _sequence;
    private bool _disposed;

    /// <summary>
    /// Creates a new AudioKeyManager.
    /// </summary>
    /// <param name="session">Active Spotify session.</param>
    /// <param name="logger">Optional logger.</param>
    public AudioKeyManager(ISession session, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Requests an AudioKey for decrypting an audio file.
    /// Includes automatic retry with exponential backoff on timeout.
    /// </summary>
    /// <param name="trackId">Track ID (identifies the track for licensing).</param>
    /// <param name="fileId">File ID (identifies the specific audio file).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>16-byte AES key for audio decryption.</returns>
    /// <exception cref="AudioKeyException">Thrown if the key request fails after all retries.</exception>
    public async Task<byte[]> RequestAudioKeyAsync(
        SpotifyId trackId,
        FileId fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_session.IsConnected())
            throw new AudioKeyException(AudioKeyFailureReason.NotConnected, "Session is not connected");

        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            // Apply retry delay (first attempt has no delay)
            if (attempt > 0)
            {
                _logger?.LogDebug("Retrying AudioKey request (attempt {Attempt}/{Max})",
                    attempt + 1, MaxRetries);
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }

            try
            {
                return await RequestAudioKeyInternalAsync(trackId, fileId, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                _logger?.LogWarning("AudioKey attempt {Attempt}/{Max} timed out", attempt + 1, MaxRetries);
            }
        }

        throw new AudioKeyException(
            AudioKeyFailureReason.Timeout,
            $"AudioKey request failed after {MaxRetries} attempts",
            lastException!);
    }

    /// <summary>
    /// Internal single-attempt AudioKey request.
    /// </summary>
    private async Task<byte[]> RequestAudioKeyInternalAsync(
        SpotifyId trackId,
        FileId fileId,
        CancellationToken cancellationToken)
    {
        // Generate sequence number
        var seq = Interlocked.Increment(ref _sequence);

        // Create completion source for this request
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(seq, tcs))
            throw new AudioKeyException(AudioKeyFailureReason.InternalError, "Failed to register pending request");

        try
        {
            // Build and send request packet
            await SendKeyRequestAsync(seq, trackId, fileId, cancellationToken);

            _logger?.LogDebug("Sent AudioKey request: seq={Seq}, track={TrackId}, file={FileId}",
                seq, trackId.ToBase62(), fileId.ToBase16());

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(KeyResponseTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                return await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger?.LogError("AudioKey request timeout: seq={Seq}", seq);
                throw new TimeoutException($"AudioKey response timeout after {KeyResponseTimeout.TotalMilliseconds}ms");
            }
        }
        finally
        {
            // Clean up pending request
            _pending.TryRemove(seq, out _);
        }
    }

    /// <summary>
    /// Dispatches a received AesKey or AesKeyError packet.
    /// Called by Session when these packet types are received.
    /// </summary>
    /// <param name="packetType">The packet type (AesKey or AesKeyError).</param>
    /// <param name="payload">The packet payload.</param>
    public void DispatchPacket(PacketType packetType, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            _logger?.LogWarning("Received malformed AudioKey packet: too short ({Length} bytes)", payload.Length);
            return;
        }

        // Extract sequence number (first 4 bytes, big-endian)
        var seq = BinaryPrimitives.ReadUInt32BigEndian(payload);

        if (!_pending.TryRemove(seq, out var tcs))
        {
            _logger?.LogWarning("Received AudioKey response for unknown sequence: {Seq}", seq);
            return;
        }

        switch (packetType)
        {
            case PacketType.AesKey:
                if (payload.Length < 4 + 16)
                {
                    tcs.TrySetException(new AudioKeyException(
                        AudioKeyFailureReason.MalformedResponse,
                        $"AesKey packet too short: {payload.Length} bytes"));
                    return;
                }

                // Extract 16-byte key
                var key = payload.Slice(4, 16).ToArray();
                _logger?.LogDebug("Received AudioKey: seq={Seq}", seq);
                tcs.TrySetResult(key);
                break;

            case PacketType.AesKeyError:
                var errorCode = payload.Length >= 6
                    ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4))
                    : (ushort)0;

                _logger?.LogError("Received AudioKeyError: seq={Seq}, errorCode=0x{ErrorCode:X4}", seq, errorCode);
                tcs.TrySetException(new AudioKeyException(
                    AudioKeyFailureReason.KeyError,
                    $"AudioKey error: 0x{errorCode:X4}"));
                break;

            default:
                _logger?.LogWarning("Unexpected packet type in AudioKeyManager: {PacketType}", packetType);
                tcs.TrySetException(new AudioKeyException(
                    AudioKeyFailureReason.UnexpectedPacket,
                    $"Unexpected packet type: {packetType}"));
                break;
        }
    }

    /// <summary>
    /// Sends a key request packet.
    /// </summary>
    private async ValueTask SendKeyRequestAsync(
        uint seq,
        SpotifyId trackId,
        FileId fileId,
        CancellationToken cancellationToken)
    {
        // Packet format:
        // - FileId: 20 bytes
        // - TrackId: 16 bytes
        // - Sequence: 4 bytes (big-endian)
        // - Zero: 2 bytes (0x0000)
        // Total: 42 bytes

        var packet = new byte[42];

        // FileId (20 bytes)
        fileId.WriteRaw(packet.AsSpan(0, FileId.Length));

        // TrackId raw (16 bytes)
        trackId.WriteRaw(packet.AsSpan(20, SpotifyId.RawLength));

        // Sequence (4 bytes, big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(36), seq);

        // Zero padding (2 bytes) - already zero from array allocation

        await _session.SendAsync(PacketType.RequestKey, packet, cancellationToken);
    }

    /// <summary>
    /// Cancels all pending requests.
    /// </summary>
    private void CancelAllPending()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        CancelAllPending();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Exception thrown when an AudioKey request fails.
/// </summary>
public sealed class AudioKeyException : Exception
{
    /// <summary>
    /// The reason for the failure.
    /// </summary>
    public AudioKeyFailureReason Reason { get; }

    public AudioKeyException(AudioKeyFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public AudioKeyException(AudioKeyFailureReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>
/// Reasons for AudioKey request failure.
/// </summary>
public enum AudioKeyFailureReason
{
    /// <summary>
    /// Session is not connected.
    /// </summary>
    NotConnected,

    /// <summary>
    /// Server returned an error response.
    /// </summary>
    KeyError,

    /// <summary>
    /// Response packet was malformed.
    /// </summary>
    MalformedResponse,

    /// <summary>
    /// Received unexpected packet type.
    /// </summary>
    UnexpectedPacket,

    /// <summary>
    /// Internal error (e.g., duplicate sequence).
    /// </summary>
    InternalError,

    /// <summary>
    /// Request timed out after all retry attempts.
    /// </summary>
    Timeout
}
