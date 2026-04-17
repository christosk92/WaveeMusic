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
    /// Timeout for AudioKey requests. Matches librespot's <c>KEY_RESPONSE_TIMEOUT</c>
    /// (see <c>core/src/audio_key.rs</c>). Going longer does not make a healthy AP
    /// respond — it only delays the reconnect path the retry loop already triggers.
    /// </summary>
    private static readonly TimeSpan KeyResponseTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Maximum number of retry attempts for AudioKey requests.
    /// </summary>
    private const int MaxRetries = 5;

    /// <summary>
    /// Delays between retry attempts (exponential backoff).
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,                      // First attempt: immediate
        TimeSpan.FromMilliseconds(500),     // Retry 1: 500ms delay
        TimeSpan.FromMilliseconds(1000),    // Retry 2: 1000ms delay
        TimeSpan.FromMilliseconds(2000),    // Retry 3: 2000ms delay
        TimeSpan.FromMilliseconds(3000)     // Retry 4: 3000ms delay
    ];

    private readonly ISession _session;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<uint, (TaskCompletionSource<byte[]> Tcs, FileId FileId)> _pending = new();
    private readonly ConcurrentDictionary<FileId, byte[]> _keyCache = new();
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

        // Check cache first
        if (_keyCache.TryGetValue(fileId, out var cachedKey))
        {
            _logger?.LogDebug("AudioKey cache hit for file {FileId}", fileId.ToBase16());
            return cachedKey;
        }

        if (!_session.IsConnected())
            throw new AudioKeyException(AudioKeyFailureReason.NotConnected, "Session is not connected");

        Exception? lastException = null;
        var reconnectAttempted = false;

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
                return await RequestAudioKeyInternalAsync(trackId, fileId, attempt, cancellationToken);
            }
            catch (AudioKeyException ex) when (ex.Reason == AudioKeyFailureReason.KeyError && !IsTransient(ex.ErrorCode))
            {
                // Permanent error (e.g. 0x0001 entitlement denial). Do not retry; surface fast.
                _logger?.LogWarning(
                    "AudioKey permanent error code=0x{Code:X4}, not retrying",
                    ex.ErrorCode ?? 0);
                throw;
            }
            catch (AudioKeyException ex) when (ex.Reason == AudioKeyFailureReason.KeyError && IsTransient(ex.ErrorCode))
            {
                lastException = ex;
                _logger?.LogWarning(
                    "AudioKey transient error code=0x{Code:X4} (attempt {Attempt}/{Max}), retrying",
                    ex.ErrorCode ?? 0, attempt + 1, MaxRetries);
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                _logger?.LogWarning("AudioKey attempt {Attempt}/{Max} timed out", attempt + 1, MaxRetries);

                // On first timeout, try reconnecting to AP (connection may be stale).
                // Use a separate CTS so user actions (skip, pause) don't cancel the reconnect.
                if (!reconnectAttempted)
                {
                    reconnectAttempted = true;
                    try
                    {
                        _logger?.LogInformation("AudioKey timeout, attempting AP reconnection");
                        using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await _session.ReconnectApAsync(reconnectCts.Token);
                        _logger?.LogInformation("AP reconnection successful, retrying AudioKey");
                        // Continue loop to retry with fresh connection
                    }
                    catch (Exception reconnectEx)
                    {
                        _logger?.LogWarning(reconnectEx, "AP reconnection failed, continuing with retries");
                        // Continue with normal retries
                    }
                }
            }
        }

        throw new AudioKeyException(
            AudioKeyFailureReason.Timeout,
            $"AudioKey request failed after {MaxRetries} attempts",
            lastException!);
    }

    /// <summary>
    /// Classifies an AudioKey error code as transient (worth retrying) or permanent.
    /// Librespot does not decode these bytes; community reading of issue #1348 treats
    /// 0x0002 as a transient AP backend failure (HTTP 502 upstream) and 0x0001 as an
    /// entitlement denial (region / device / account tier) that will not succeed on retry.
    /// Default to permanent for unknown codes — we can relax once logs give us data.
    /// </summary>
    private static bool IsTransient(ushort? errorCode) => errorCode switch
    {
        0x0002 => true,
        _ => false,
    };

    /// <summary>
    /// Internal single-attempt AudioKey request.
    /// </summary>
    private async Task<byte[]> RequestAudioKeyInternalAsync(
        SpotifyId trackId,
        FileId fileId,
        int attempt,
        CancellationToken cancellationToken)
    {
        // Generate sequence number
        var seq = Interlocked.Increment(ref _sequence);

        // Create completion source for this request
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(seq, (tcs, fileId)))
        {
            _logger?.LogError("Failed to register pending request: seq={Seq} already exists. Pending count: {Count}",
                seq, _pending.Count);
            throw new AudioKeyException(AudioKeyFailureReason.InternalError, "Failed to register pending request");
        }

        var startedUtc = DateTime.UtcNow;

        try
        {
            // Build and send request packet
            await SendKeyRequestAsync(seq, trackId, fileId, cancellationToken);

            _logger?.LogInformation(
                "AudioKey request sent: seq={Seq} attempt={Attempt}/{Max} trackId={TrackId} fileId={FileId} pending={Pending}",
                seq, attempt + 1, MaxRetries, trackId.ToBase62(), fileId.ToBase16(), _pending.Count);

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(KeyResponseTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                var key = await tcs.Task.WaitAsync(linkedCts.Token);
                _logger?.LogInformation(
                    "AudioKey received: seq={Seq} fileId={FileId} elapsedMs={ElapsedMs}",
                    seq, fileId.ToBase16(), (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds);
                return key;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger?.LogError(
                    "AudioKey timeout: seq={Seq} fileId={FileId} attempt={Attempt}/{Max} elapsedMs={ElapsedMs} pending={Pending}",
                    seq, fileId.ToBase16(), attempt + 1, MaxRetries,
                    (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds, _pending.Count);
                throw new TimeoutException($"AudioKey response timeout after {KeyResponseTimeout.TotalMilliseconds}ms");
            }
        }
        finally
        {
            // Clean up pending request
            if (_pending.TryRemove(seq, out _))
            {
                _logger?.LogDebug("Removed pending AudioKey request: seq={Seq}, pending count={Count}",
                    seq, _pending.Count);
            }
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
        _logger?.LogDebug("DispatchPacket called: type={PacketType}, length={Length}", packetType, payload.Length);

        if (payload.Length < 4)
        {
            _logger?.LogWarning("Received malformed AudioKey packet: too short ({Length} bytes)", payload.Length);
            return;
        }

        // Extract sequence number (first 4 bytes, big-endian)
        var seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
        _logger?.LogDebug("AudioKey response: seq={Seq}, type={PacketType}, pending count={Count}",
            seq, packetType, _pending.Count);

        if (!_pending.TryRemove(seq, out var pending))
        {
            // Snapshot outstanding seqs so we can see whether a reconnect
            // wiped them (empty) or the seq truly doesn't belong to us (skew).
            uint minSeq = 0, maxSeq = 0;
            var count = 0;
            foreach (var kvp in _pending)
            {
                if (count == 0) { minSeq = kvp.Key; maxSeq = kvp.Key; }
                else
                {
                    if (kvp.Key < minSeq) minSeq = kvp.Key;
                    if (kvp.Key > maxSeq) maxSeq = kvp.Key;
                }
                count++;
            }
            _logger?.LogWarning(
                "AudioKey response for unknown seq={Seq} type={PacketType} pending={Pending} pendingRange=[{Min}..{Max}]",
                seq, packetType, count, minSeq, maxSeq);
            return;
        }

        var (tcs, fileId) = pending;

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

                // Cache the key for future use
                _keyCache.TryAdd(fileId, key);

                _logger?.LogDebug("Received AudioKey: seq={Seq}, cached for file {FileId}", seq, fileId.ToBase16());
                tcs.TrySetResult(key);
                break;

            case PacketType.AesKeyError:
                var errorCode = payload.Length >= 6
                    ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4))
                    : (ushort)0;

                _logger?.LogError(
                    "AudioKey error: seq={Seq} code=0x{ErrorCode:X4} fileId={FileId}",
                    seq, errorCode, fileId.ToBase16());
                tcs.TrySetException(new AudioKeyException(
                    AudioKeyFailureReason.KeyError,
                    $"AudioKey error code 0x{errorCode:X4}",
                    errorCode));
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

        // Log packet hex for debugging (verbose)
        _logger?.LogTrace("AudioKey request packet hex: {PacketHex}", Convert.ToHexString(packet));

        await _session.SendAsync(PacketType.RequestKey, packet, cancellationToken);
    }

    /// <summary>
    /// Resets the sequence number and cancels all pending requests.
    /// Called when reconnecting to AP due to stale connection.
    /// </summary>
    public void ResetSequence()
    {
        _sequence = 0;
        CancelAllPending();
        _logger?.LogInformation("AudioKeyManager sequence reset to 0, pending requests cancelled");
    }

    /// <summary>
    /// Cancels all pending requests.
    /// </summary>
    private void CancelAllPending()
    {
        var count = _pending.Count;
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var pending))
            {
                pending.Tcs.TrySetCanceled();
            }
        }
        if (count > 0)
        {
            _logger?.LogDebug("Cancelled {Count} pending AudioKey requests", count);
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

    /// <summary>
    /// The raw 2-byte error code returned by the AP, when <see cref="Reason"/> is
    /// <see cref="AudioKeyFailureReason.KeyError"/>. Null otherwise. Librespot does
    /// not decode these bytes; community reading of issue #1348 treats 0x0002 as a
    /// transient backend failure (retry) and 0x0001 as entitlement denial (permanent).
    /// </summary>
    public ushort? ErrorCode { get; }

    public AudioKeyException(AudioKeyFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public AudioKeyException(AudioKeyFailureReason reason, string message, ushort errorCode)
        : base(message)
    {
        Reason = reason;
        ErrorCode = errorCode;
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
