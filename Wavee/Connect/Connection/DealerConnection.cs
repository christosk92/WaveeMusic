using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Wavee.Connect.Connection;

/// <summary>
/// High-performance WebSocket connection using System.IO.Pipelines.
/// Zero-copy message streaming with backpressure control.
/// </summary>
internal sealed class DealerConnection : IDealerConnection
{
    private readonly ILogger? _logger;
    private Pipe _receivePipe;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _processTask;

    /// <summary>
    /// Raised when a complete WebSocket message is received.
    /// Handler receives UTF-8 encoded message bytes.
    /// </summary>
    public event Func<ReadOnlyMemory<byte>, ValueTask>? MessageReceived;

    /// <summary>
    /// Raised when the WebSocket connection is closed.
    /// </summary>
    public event EventHandler<WebSocketCloseStatus?>? Closed;

    /// <summary>
    /// Raised when an error occurs during receive/process.
    /// </summary>
    public event EventHandler<Exception>? Error;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState State { get; private set; }

    public DealerConnection(ILogger? logger = null)
    {
        _logger = logger;

        // Configure pipe with backpressure thresholds
        _receivePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,    // Pause at 1MB
            resumeWriterThreshold: 512 * 1024,     // Resume at 512KB
            minimumSegmentSize: 4096,              // 4KB segments
            useSynchronizationContext: false));    // Don't capture sync context

        State = ConnectionState.Disconnected;
    }

    /// <summary>
    /// Connects to the WebSocket endpoint.
    /// </summary>
    /// <param name="wsUrl">WebSocket URL (wss://...).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="DealerConnectionException">Connection failed.</exception>
    public async ValueTask ConnectAsync(string wsUrl, CancellationToken cancellationToken = default)
    {
        if (State == ConnectionState.Connected)
            throw new InvalidOperationException("Already connected");

        // If there are lingering tasks from a previous connection, clean them up
        // This happens during reconnection attempts
        if (_receiveTask != null || _processTask != null)
        {
            _logger?.LogDebug("Cleaning up lingering pipe tasks from previous connection");
            await CleanupPipeAsync();

            // Reset the pipe for reuse (both reader and writer are now completed)
            _receivePipe.Reset();
            _logger?.LogDebug("Pipe reset for reconnection");
        }

        // Dispose old resources if they exist
        _webSocket?.Dispose();
        _cts?.Dispose();

        State = ConnectionState.Connecting;

        _webSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource();

        try
        {
            await _webSocket.ConnectAsync(new Uri(wsUrl), cancellationToken);
            State = ConnectionState.Connected;

            _logger?.LogDebug("WebSocket connected to {Url}", wsUrl);

            // Start pipeline tasks
            _receiveTask = FillPipeAsync(_cts.Token);
            _processTask = ProcessPipeAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _webSocket?.Dispose();
            _webSocket = null;
            throw new DealerConnectionException("Failed to connect to WebSocket", ex);
        }
    }

    /// <summary>
    /// Sends a UTF-8 encoded message.
    /// Uses Memory to avoid allocations.
    /// </summary>
    /// <param name="utf8Message">UTF-8 encoded message bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8Message, CancellationToken cancellationToken = default)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        return _webSocket.SendAsync(utf8Message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>
    /// Sends a string message (encodes to UTF-8 using ArrayPool).
    /// </summary>
    /// <param name="message">String message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
    {
        // Rent buffer for UTF-8 encoding
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(message.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(maxByteCount);

        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(message.AsSpan(), buffer);
            await SendAsync(buffer.AsMemory(0, bytesWritten), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Closes the WebSocket connection gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client closing",
                cancellationToken);
        }

        State = ConnectionState.Disconnected;
    }

    /// <summary>
    /// Cleans up pipe tasks from a previous connection.
    /// Waits for both FillPipeAsync and ProcessPipeAsync to complete.
    /// Must be called before resetting or recreating the pipe.
    /// </summary>
    private async ValueTask CleanupPipeAsync()
    {
        // Wait for receive task to complete (it will call writer.CompleteAsync in finally)
        if (_receiveTask != null)
        {
            await _receiveTask;
            _receiveTask = null;
        }

        // Wait for process task to complete (it will call reader.CompleteAsync in finally)
        if (_processTask != null)
        {
            await _processTask;
            _processTask = null;
        }

        _logger?.LogDebug("Pipe tasks cleaned up");
    }

    /// <summary>
    /// Fills the pipe with data from WebSocket (producer).
    /// </summary>
    private async Task FillPipeAsync(CancellationToken cancellationToken)
    {
        var writer = _receivePipe.Writer;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _webSocket?.State == WebSocketState.Open)
            {
                // Get memory from pipe (zero-allocation)
                var memory = writer.GetMemory(4096);

                var result = await _webSocket.ReceiveAsync(memory, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger?.LogInformation("WebSocket closed by server: {Status}", _webSocket.CloseStatus);
                    Closed?.Invoke(this, _webSocket.CloseStatus);
                    break;
                }

                writer.Advance(result.Count);

                // Flush when message is complete
                if (result.EndOfMessage)
                {
                    var flushResult = await writer.FlushAsync(cancellationToken);
                    if (flushResult.IsCompleted)
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
            _logger?.LogDebug("Receive loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in WebSocket receive loop");
            Error?.Invoke(this, ex);
        }
        finally
        {
            await writer.CompleteAsync();
            State = ConnectionState.Disconnected;
        }
    }

    /// <summary>
    /// Processes complete messages from the pipe (consumer).
    /// </summary>
    private async Task ProcessPipeAsync(CancellationToken cancellationToken)
    {
        var reader = _receivePipe.Reader;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;

                // Process complete messages (delimited by flush)
                while (TryReadMessage(ref buffer, out var message))
                {
                    if (MessageReceived != null)
                    {
                        await MessageReceived.Invoke(message);
                    }
                }

                // Tell the pipe how much we consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
            _logger?.LogDebug("Process loop cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in message process loop");
            Error?.Invoke(this, ex);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    /// <summary>
    /// Tries to read a complete message from the buffer.
    /// Each flush represents one complete WebSocket message.
    /// </summary>
    private static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message)
    {
        if (buffer.IsEmpty)
        {
            message = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        // WebSocket guarantees message boundaries via EndOfMessage
        // Each pipe flush represents one complete message
        if (buffer.IsSingleSegment)
        {
            // Fast path: single segment
            message = buffer.First;
        }
        else
        {
            // Slow path: multiple segments, must copy
            message = buffer.ToArray();
        }

        buffer = buffer.Slice(buffer.End);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client shutdown",
                        CancellationToken.None);
                }
                catch
                {
                    // Best effort
                }
            }
            _webSocket.Dispose();
        }

        _cts?.Dispose();

        // Wait for pipe tasks to complete
        await CleanupPipeAsync();

        _logger?.LogDebug("DealerConnection disposed");
    }
}
