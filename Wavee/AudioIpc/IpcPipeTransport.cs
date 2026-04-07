using System.Buffers;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioIpc;

/// <summary>
/// Length-prefixed JSON message framing over a named pipe stream.
/// Protocol: [4 bytes big-endian payload length][UTF-8 JSON payload]
/// Thread-safe for concurrent read/write (separate locks).
/// </summary>
public sealed class IpcPipeTransport : IAsyncDisposable
{
    private readonly PipeStream _pipe;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly byte[] _writeLengthBuffer = new byte[4];
    private readonly byte[] _readLengthBuffer = new byte[4];
    private bool _disposed;

    public IpcPipeTransport(PipeStream pipe, ILogger? logger = null)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _logger = logger;
    }

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>
    /// Sends a typed message as a length-prefixed JSON frame.
    /// </summary>
    public async Task SendAsync(string messageType, JsonElement payload, long id = 0, CancellationToken ct = default)
    {
        var envelope = new IpcMessage { Type = messageType, Id = id, Payload = payload };
        await SendRawAsync(envelope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a typed payload by first serializing it to a JsonElement.
    /// Callers should use the source-generated serializer.
    /// </summary>
    public async Task SendAsync(string messageType, byte[] payloadJsonUtf8, long id = 0, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(payloadJsonUtf8);
        var envelope = new IpcMessage { Type = messageType, Id = id, Payload = doc.RootElement.Clone() };
        await SendRawAsync(envelope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a simple message with no payload.
    /// </summary>
    public async Task SendAsync(string messageType, long id = 0, CancellationToken ct = default)
    {
        var envelope = new IpcMessage { Type = messageType, Id = id };
        await SendRawAsync(envelope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the next message from the pipe. Returns null on disconnect.
    /// </summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read 4-byte length prefix
            var bytesRead = await ReadExactAsync(_readLengthBuffer, 0, 4, ct).ConfigureAwait(false);
            if (bytesRead < 4) return null; // pipe closed

            var length = (int)(
                (_readLengthBuffer[0] << 24) |
                (_readLengthBuffer[1] << 16) |
                (_readLengthBuffer[2] << 8) |
                _readLengthBuffer[3]);

            if (length <= 0 || length > 4 * 1024 * 1024) // max 4MB
            {
                _logger?.LogError("IPC: Invalid message length {Length}", length);
                return null;
            }

            // Read payload
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                bytesRead = await ReadExactAsync(buffer, 0, length, ct).ConfigureAwait(false);
                if (bytesRead < length) return null;

                return JsonSerializer.Deserialize(
                    buffer.AsSpan(0, length),
                    IpcJsonContext.Default.IpcMessage);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task SendRawAsync(IpcMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);

        // Timeout the write lock acquisition — if a previous write is stuck, don't queue forever
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        await _writeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            // Write length prefix (big-endian)
            var len = json.Length;
            _writeLengthBuffer[0] = (byte)(len >> 24);
            _writeLengthBuffer[1] = (byte)(len >> 16);
            _writeLengthBuffer[2] = (byte)(len >> 8);
            _writeLengthBuffer[3] = (byte)len;

            await _pipe.WriteAsync(_writeLengthBuffer.AsMemory(0, 4), timeoutCts.Token).ConfigureAwait(false);
            await _pipe.WriteAsync(json, timeoutCts.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = await _pipe.ReadAsync(
                buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) return totalRead; // pipe closed
            totalRead += read;
        }
        return totalRead;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_pipe is NamedPipeServerStream server && server.IsConnected)
                server.Disconnect();
        }
        catch { /* best-effort */ }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _readLock.Dispose();
    }
}

/// <summary>
/// AOT-safe helpers for serializing/deserializing IPC payloads via the source-generated context.
/// </summary>
public static class IpcPayloadHelper
{
    public static T? Deserialize<T>(IpcMessage message) where T : class
    {
        if (message.Payload is not { } element) return default;
        return (T?)element.Deserialize(typeof(T), IpcJsonContext.Default);
    }

    /// <summary>
    /// Serializes a payload to UTF-8 bytes using the AOT-safe source-generated context.
    /// </summary>
    public static byte[] SerializeToUtf8<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), IpcJsonContext.Default);
    }
}
