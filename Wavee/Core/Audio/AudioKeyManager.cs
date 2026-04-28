using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.Core.Storage;

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
    /// Timeout per AudioKey attempt. Set shorter than librespot's 5 s because the
    /// prefetch path in <c>PlaybackOrchestrator</c> already covers the healthy-but-slow
    /// case (keys are requested well before they're needed, so a 2-second server delay
    /// under HTTP fan-out is invisible). What we optimize for here is the *stuck*
    /// case — when the server silently stops answering 0x0C packets for a specific
    /// FileId. Two consecutive 2.5 s timeouts gets us into recovery after 5 s instead
    /// of 10 s.
    /// </summary>
    private static readonly TimeSpan KeyResponseTimeout = TimeSpan.FromMilliseconds(2500);

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
    /// <summary>
    /// Optional disk-backed cache. When supplied, keys survive app restarts
    /// (ICacheService writes through to SQLite). Otherwise we fall back to
    /// the in-process dictionary only, matching legacy behaviour.
    /// </summary>
    private readonly ICacheService? _cacheService;
    // PlayPlay is Spotify property. Optional fallback for permanent AP errors
    // and timeout exhaustion; null = AP-only.
    private readonly IPlayPlayKeyDeriver? _playPlayKeyDeriver;
    private readonly ConcurrentDictionary<uint, (TaskCompletionSource<byte[]> Tcs, FileId FileId)> _pending = new();
    private readonly ConcurrentDictionary<FileId, byte[]> _keyCache = new();
    private uint _sequence;
    private bool _disposed;

    /// <summary>
    /// Raised right before the AudioKey loop triggers an AP reconnect because the
    /// key channel has gone silent for a specific FileId. Fires on the AP dispatcher
    /// thread — UI consumers must marshal to their dispatcher. Distinct from
    /// <c>IPlaybackEngine.Errors</c>, which represents terminal failure; this is a
    /// lifecycle hint ("I'm recovering, show a warning") rather than an error.
    /// </summary>
    public event EventHandler<AudioKeyRecoveryEventArgs>? RecoveryStarted;

    /// <summary>
    /// Raised after the recovery reconnect call returns, regardless of outcome.
    /// Consumers that showed an indeterminate spinner on <see cref="RecoveryStarted"/>
    /// can dismiss it here. The following retry may still fail — hard failures flow
    /// through the regular error path.
    /// </summary>
    public event EventHandler<AudioKeyRecoveryEventArgs>? RecoveryEnded;

    /// <summary>
    /// Creates a new AudioKeyManager.
    /// </summary>
    /// <param name="session">Active Spotify session.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cacheService">
    /// Optional disk-backed cache for persisting AudioKeys across app restarts.
    /// When null, keys are only held in process memory (legacy behaviour).
    /// </param>
    public AudioKeyManager(
        ISession session,
        ILogger? logger = null,
        ICacheService? cacheService = null,
        IPlayPlayKeyDeriver? playPlayKeyDeriver = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _logger = logger;
        _cacheService = cacheService;
        _playPlayKeyDeriver = playPlayKeyDeriver;
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

        // Check in-memory cache first (hot path, O(1))
        if (_keyCache.TryGetValue(fileId, out var cachedKey))
        {
            _logger?.LogDebug("AudioKey cache hit for file {FileId}", fileId.ToBase16());
            return cachedKey;
        }

        // Disk-backed cache: keys persisted to SQLite by previous sessions stay
        // valid forever (FileIds are immutable). A hit here saves a round-trip
        // to the AP and works even if the audio-key channel is currently stuck.
        if (_cacheService != null)
        {
            try
            {
                var persisted = await _cacheService.GetAudioKeyAsync(
                    trackId.ToUri(), fileId, cancellationToken);
                if (persisted != null)
                {
                    _keyCache.TryAdd(fileId, persisted);
                    _logger?.LogInformation(
                        "AudioKey disk hit for file {FileId} — skipping AP round-trip",
                        fileId.ToBase16());
                    return persisted;
                }
            }
            catch (Exception ex)
            {
                // Never let cache lookup break the request path.
                _logger?.LogDebug(ex, "AudioKey disk lookup failed, falling through to AP request");
            }
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
                // Permanent error (e.g. 0x0001 entitlement denial). The AP will not
                // change its mind on retry, so try the PlayPlay HTTP path instead
                // before surfacing the failure. If no deriver is registered, behave
                // exactly as before and re-throw.
                _logger?.LogWarning(
                    "AudioKey permanent error code=0x{Code:X4}, not retrying",
                    ex.ErrorCode ?? 0);

                var fallbackKey = await TryPlayPlayFallbackAsync(
                    trackId, fileId, "permanent AP error", cancellationToken);
                if (fallbackKey is not null)
                    return fallbackKey;

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
                // Attempts 1 & 2: retry on the same connection — the timeout is usually
                // just slow response under HTTP fan-out contention, and reconnecting
                // there would turn ~1 s of latency into ~5 s of silence.
                //
                // Attempt 3 (after two full timeouts): something's wrong with the
                // audio-key channel specifically — the server has stopped responding
                // to our 0x0C packets for this FileId while the rest of the socket
                // still works (PING/PONG, dealer REQUESTs). Reconnecting the AP once
                // recovers it. Librespot does the equivalent.
                lastException = ex;
                _logger?.LogWarning("AudioKey attempt {Attempt}/{Max} timed out", attempt + 1, MaxRetries);

                if (!reconnectAttempted && attempt + 1 >= 2)
                {
                    reconnectAttempted = true;
                    var args = new AudioKeyRecoveryEventArgs(fileId, attempt + 1);

                    // Let the UI put up a "reconnecting…" warning before we spend
                    // several seconds handshaking.
                    try { RecoveryStarted?.Invoke(this, args); }
                    catch (Exception hx) { _logger?.LogDebug(hx, "RecoveryStarted handler threw"); }

                    try
                    {
                        _logger?.LogInformation(
                            "AudioKey channel appears stuck after {Attempts} consecutive timeouts, " +
                            "reconnecting AP to recover", attempt + 1);
                        using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await _session.ReconnectApAsync(reconnectCts.Token);
                        _logger?.LogInformation("AP reconnection successful, retrying AudioKey on fresh socket");
                    }
                    catch (Exception reconnectEx)
                    {
                        _logger?.LogWarning(reconnectEx,
                            "AP reconnection failed, continuing with same-connection retries");
                    }
                    finally
                    {
                        try { RecoveryEnded?.Invoke(this, args); }
                        catch (Exception hx) { _logger?.LogDebug(hx, "RecoveryEnded handler threw"); }
                    }
                }
            }
        }

        // Last resort before surfacing the timeout: try PlayPlay. The audio-key
        // channel has been silent through every retry (and likely a reconnect) —
        // PlayPlay routes through spclient HTTPS instead, so it bypasses whatever
        // is wedged on the AP socket.
        {
            var fallbackKey = await TryPlayPlayFallbackAsync(
                trackId, fileId, "AP timeout exhaustion", cancellationToken);
            if (fallbackKey is not null)
                return fallbackKey;
        }

        throw new AudioKeyException(
            AudioKeyFailureReason.Timeout,
            $"AudioKey request failed after {MaxRetries} attempts",
            lastException!);
    }

    /// <summary>
    /// PlayPlay is Spotify property. AP-failure fallback; in-memory cache only.
    /// </summary>
    private async Task<byte[]?> TryPlayPlayFallbackAsync(
        SpotifyId trackId,
        FileId fileId,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (_playPlayKeyDeriver is null)
            return null;

        try
        {
            _logger?.LogInformation(
                "AudioKey {Trigger} for {FileId} — attempting PlayPlay fallback",
                trigger, fileId.ToBase16());

            var key = await _playPlayKeyDeriver.DeriveAsync(trackId, fileId, cancellationToken);

            if (key is null || key.Length != 16)
            {
                _logger?.LogWarning(
                    "PlayPlay fallback returned unexpected key length {Length} for {FileId}",
                    key?.Length ?? 0, fileId.ToBase16());
                return null;
            }

            // In-memory only. The derived key never touches disk on this path —
            // a fresh session has to re-fetch the obfuscated key and re-run the
            // cipher, so cold-start playback still goes through the proper
            // entitlement check.
            _keyCache.TryAdd(fileId, key);

            return key;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate without swallowing. Do not log as a
            // PlayPlay failure: it isn't one.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "PlayPlay fallback failed for {FileId}; surfacing original AP failure",
                fileId.ToBase16());
            return null;
        }
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

                // Cache the key for future use (in-memory + disk through CacheService)
                _keyCache.TryAdd(fileId, key);

                if (_cacheService != null)
                {
                    // Fire-and-forget disk write. Failure here must not block the
                    // decryption path — in-memory cache still holds the key for
                    // the current session.
                    // trackUri is unknown in this context (request dispatch is
                    // per-FileId). CacheService uses trackUri for its hot-cache
                    // lookup key but the DB lookup is by FileId, so a placeholder
                    // works — subsequent GetAudioKey calls keyed by trackUri will
                    // hit the DB even if the hot-cache entry is absent.
                    var keyCopy = key;
                    var fileIdCopy = fileId;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _cacheService.SetAudioKeyAsync(
                                trackUri: $"spotify:track:{fileIdCopy.ToBase16()}",
                                fileId: fileIdCopy,
                                key: keyCopy).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "AudioKey disk write failed (non-fatal)");
                        }
                    });
                }

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
/// Payload for <see cref="AudioKeyManager.RecoveryStarted"/> / <see cref="AudioKeyManager.RecoveryEnded"/>.
/// </summary>
/// <param name="FileId">The file whose key request triggered the recovery.</param>
/// <param name="AttemptsBeforeRecovery">How many consecutive timeouts preceded the recovery (always ≥ 2 today).</param>
public sealed record AudioKeyRecoveryEventArgs(FileId FileId, int AttemptsBeforeRecovery);

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
