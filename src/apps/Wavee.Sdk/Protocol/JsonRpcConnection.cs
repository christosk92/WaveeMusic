using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wavee.Sdk.Protocol;

/// <summary>The payload of a binary response frame.</summary>
/// <param name="Bytes">The raw bytes.</param>
/// <param name="Eof">True when the payload reached the end of the underlying stream.</param>
public readonly record struct BinaryPayload(ReadOnlyMemory<byte> Bytes, bool Eof);

/// <summary>
/// A JSON-RPC 2.0 peer over a duplex pair of streams, used unchanged on both sides of the module boundary: the host
/// assigns positive request ids, the module negative ones, so ids never collide. Requests can be answered with JSON
/// or — for <c>stream/read</c> — with a raw binary frame correlated by <c>X-Wavee-Request</c>. Writes are
/// serialized; handler cancellation rides <c>$/cancelRequest</c>.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private delegate Task RequestDispatch(long id, JsonElement? rawParams, CancellationToken ct);

    private readonly Stream _output;
    private readonly JsonRpcFramer _framer;
    private readonly bool _negativeIds;
    private readonly Lock _writeLock = new();
    private readonly ArrayBufferWriter<byte> _writeBuffer = new(8 * 1024);
    private readonly Utf8JsonWriter _writer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, IPending> _pending = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inbound = new();
    private readonly ConcurrentDictionary<string, RequestDispatch> _requestHandlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Action<JsonElement?>> _notificationHandlers = new(StringComparer.Ordinal);

    private long _nextId;
    private bool _stopAfterWrite;
    private int _disposed;

    /// <summary>Creates a peer over an input (read) and an output (write) stream.</summary>
    /// <param name="input">Where frames arrive (the module's stdin, the host's read side of the pipe).</param>
    /// <param name="output">Where frames are written (the module's stdout, the host's write side).</param>
    /// <param name="negativeIds">
    /// True on the module side: its outbound request ids are negative so they never collide with the host's.
    /// </param>
    public JsonRpcConnection(Stream input, Stream output, bool negativeIds = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _framer = new JsonRpcFramer(input);
        _negativeIds = negativeIds;
        _writer = new Utf8JsonWriter(_writeBuffer, new JsonWriterOptions { SkipValidation = true });
    }

    /// <summary>How long <see cref="RequestAsync{TParams,TResult}(string,TParams,JsonTypeInfo{TParams},JsonTypeInfo{TResult},CancellationToken)"/>
    /// waits when no explicit timeout is given. Defaults to 30 seconds.</summary>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sends a request and awaits its JSON result, using <see cref="DefaultRequestTimeout"/>.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="resultInfo">Source-generated type info for the result.</param>
    /// <param name="ct">Cancels the request (and sends <c>$/cancelRequest</c>).</param>
    public Task<TResult> RequestAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo, CancellationToken ct)
        => RequestAsync(method, p, paramsInfo, resultInfo, DefaultRequestTimeout, ct);

    /// <summary>Sends a request and awaits its JSON result with an explicit timeout.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="resultInfo">Source-generated type info for the result.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="ct">Cancels the request (and sends <c>$/cancelRequest</c>).</param>
    /// <exception cref="TimeoutException">The peer did not answer in time.</exception>
    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        long id = NextId();
        var pending = new Pending<TResult>(resultInfo);
        _pending[id] = pending;
        try
        {
            SendRequest(id, method, p, paramsInfo);
            return await AwaitPendingAsync(pending.Task, id, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a request whose answer is a raw binary frame (the <c>stream/read</c> path).</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="ct">Cancels the request.</param>
    public Task<BinaryPayload> RequestBinaryAsync<TParams>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
        CancellationToken ct)
        => RequestBinaryAsync(method, p, paramsInfo, DefaultRequestTimeout, ct);

    /// <summary>Sends a request whose answer is a raw binary frame, with an explicit timeout.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<BinaryPayload> RequestBinaryAsync<TParams>(string method, TParams p,
        JsonTypeInfo<TParams> paramsInfo, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        long id = NextId();
        var pending = new PendingBinary();
        _pending[id] = pending;
        try
        {
            SendRequest(id, method, p, paramsInfo);
            return await AwaitPendingAsync(pending.Task, id, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification (no id, no answer).</summary>
    /// <typeparam name="T">Params shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    public void Notify<T>(string method, T p, JsonTypeInfo<T> paramsInfo)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (_disposed != 0) return;
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            BeginMessage();
            _writer.WriteString("method", method);
            _writer.WritePropertyName("params");
            JsonSerializer.Serialize(_writer, p, paramsInfo);
            EndMessage();
        }
    }

    /// <summary>Registers the handler for one inbound request method. Register before calling <see cref="RunAsync"/>.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="resultInfo">Source-generated type info for the result.</param>
    /// <param name="handler">The handler; its <see cref="CancellationToken"/> fires on <c>$/cancelRequest</c>.</param>
    public void OnRequest<TParams, TResult>(string method, JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo, Func<TParams, CancellationToken, ValueTask<TResult>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        _requestHandlers[method] = async (id, raw, ct) =>
        {
            TParams p = Read(raw, paramsInfo);
            TResult r = await handler(p, ct).ConfigureAwait(false);
            SendResult(id, r, resultInfo);
        };
    }

    /// <summary>Registers a handler whose answer is a raw binary frame (the <c>stream/read</c> path).</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="handler">The handler; the returned bytes are written as one binary frame.</param>
    public void OnBinaryRequest<TParams>(string method, JsonTypeInfo<TParams> paramsInfo,
        Func<TParams, CancellationToken, ValueTask<BinaryPayload>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        _requestHandlers[method] = async (id, raw, ct) =>
        {
            TParams p = Read(raw, paramsInfo);
            BinaryPayload r = await handler(p, ct).ConfigureAwait(false);
            SendBinary(id, r);
        };
    }

    /// <summary>Registers the handler for one inbound notification method.</summary>
    /// <typeparam name="T">Params shape.</typeparam>
    /// <param name="method">Method name.</param>
    /// <param name="paramsInfo">Source-generated type info for the params.</param>
    /// <param name="handler">The handler; exceptions it throws are swallowed (a notification has no reply).</param>
    public void OnNotification<T>(string method, JsonTypeInfo<T> paramsInfo, Action<T> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        _notificationHandlers[method] = raw => handler(Read(raw, paramsInfo));
    }

    /// <summary>True when a request handler is registered for <paramref name="method"/>.</summary>
    /// <param name="method">Method name.</param>
    public bool HandlesRequest(string method) => _requestHandlers.ContainsKey(method);

    /// <summary>
    /// Asks the read loop to finish once the response currently being produced has been written. Used by
    /// <c>module/shutdown</c>, which must answer before the process leaves.
    /// </summary>
    public void StopAfterCurrentResponse()
    {
        lock (_writeLock) _stopAfterWrite = true;
    }

    /// <summary>
    /// Runs the read/dispatch loop until the peer closes the connection, <paramref name="ct"/> fires, or
    /// <see cref="StopAfterCurrentResponse"/> takes effect. Never throws for a peer that simply went away.
    /// </summary>
    /// <param name="ct">Stops the loop.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        Exception? failure = null;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                JsonRpcFrame? frame;
                try
                {
                    frame = await _framer.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
                {
                    failure = ex;
                    break;
                }

                if (frame is null) break;
                if (frame.IsBinary) CompleteBinary(frame);
                else HandleJsonFrame(frame, linked.Token);
            }
        }
        finally
        {
            FailAllPending(failure ?? new IOException("The JSON-RPC connection was closed."));
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailAllPending(new ObjectDisposedException(nameof(JsonRpcConnection)));
        foreach (KeyValuePair<long, CancellationTokenSource> entry in _inbound)
        {
            try { await entry.Value.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        _inbound.Clear();
        lock (_writeLock) _writer.Dispose();
        _lifetime.Dispose();
    }

    // ---- inbound -------------------------------------------------------------------------------------------------

    private void HandleJsonFrame(JsonRpcFrame frame, CancellationToken ct)
    {
        JsonRpcMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize(frame.Payload, SdkJsonContext.Default.JsonRpcMessage);
        }
        catch (JsonException ex)
        {
            SendError(0, JsonRpcErrorCodes.ParseError, ex.Message, null);
            return;
        }

        if (msg is null) return;

        if (msg.Method is { Length: > 0 } method)
        {
            if (msg.Id is long requestId) DispatchRequest(requestId, method, msg.Params, ct);
            else DispatchNotification(method, msg.Params);
            return;
        }

        if (msg.Id is not long responseId) return;
        if (!_pending.TryRemove(responseId, out IPending? pending)) return;
        if (msg.Error is not null) pending.Fail(ToException(msg.Error));
        else pending.Complete(msg.Result);
    }

    private void DispatchRequest(long id, string method, JsonElement? rawParams, CancellationToken ct)
    {
        if (!_requestHandlers.TryGetValue(method, out RequestDispatch? handler))
        {
            SendError(id, JsonRpcErrorCodes.MethodNotFound, $"Unknown method '{method}'.", null);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _inbound[id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await handler(id, rawParams, cts.Token).ConfigureAwait(false);
            }
            catch (ModuleException ex)
            {
                SendError(id, (int)ex.Code, ex.Message, new JsonRpcErrorData(ex.Code, ex.RetryAfterMs, ex.Detail));
            }
            catch (OperationCanceledException)
            {
                SendError(id, JsonRpcErrorCodes.RequestCancelled, "The request was cancelled.", null);
            }
            catch (JsonException ex)
            {
                SendError(id, JsonRpcErrorCodes.InvalidParams, ex.Message, null);
            }
            catch (Exception ex)
            {
                SendError(id, JsonRpcErrorCodes.InternalError, ex.Message,
                    new JsonRpcErrorData(ModuleErrorCode.Transient, null, ex.GetType().Name));
            }
            finally
            {
                _inbound.TryRemove(id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void DispatchNotification(string method, JsonElement? rawParams)
    {
        if (string.Equals(method, ModuleMethods.CancelRequest, StringComparison.Ordinal))
        {
            try
            {
                CancelParams p = Read(rawParams, SdkJsonContext.Default.CancelParams);
                if (p is not null && _inbound.TryGetValue(p.Id, out CancellationTokenSource? cts)) cts.Cancel();
            }
            catch (Exception ex) when (ex is JsonException or ObjectDisposedException)
            {
                // a cancel for a request that already finished is normal
            }

            return;
        }

        if (!_notificationHandlers.TryGetValue(method, out Action<JsonElement?>? handler)) return;
        try
        {
            handler(rawParams);
        }
        catch (Exception)
        {
            // a notification has no reply; a broken handler must not take the loop down
        }
    }

    private void CompleteBinary(JsonRpcFrame frame)
    {
        if (_pending.TryRemove(frame.RequestId, out IPending? pending)) pending.CompleteBinary(frame.Payload, frame.Eof);
    }

    // ---- outbound ------------------------------------------------------------------------------------------------

    private long NextId()
    {
        long n = Interlocked.Increment(ref _nextId);
        return _negativeIds ? -n : n;
    }

    private void SendRequest<TParams>(long id, string method, TParams p, JsonTypeInfo<TParams> paramsInfo)
    {
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            BeginMessage();
            _writer.WriteNumber("id", id);
            _writer.WriteString("method", method);
            _writer.WritePropertyName("params");
            JsonSerializer.Serialize(_writer, p, paramsInfo);
            EndMessage();
        }
    }

    private void SendResult<TResult>(long id, TResult result, JsonTypeInfo<TResult> resultInfo)
    {
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            BeginMessage();
            _writer.WriteNumber("id", id);
            _writer.WritePropertyName("result");
            JsonSerializer.Serialize(_writer, result, resultInfo);
            EndMessage();
        }
    }

    private void SendError(long id, int code, string message, JsonRpcErrorData? data)
    {
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            BeginMessage();
            _writer.WriteNumber("id", id);
            _writer.WritePropertyName("error");
            JsonSerializer.Serialize(_writer, new JsonRpcError(code, message, data),
                SdkJsonContext.Default.JsonRpcError);
            EndMessage();
        }
    }

    private void SendBinary(long id, BinaryPayload payload)
    {
        lock (_writeLock)
        {
            if (_disposed != 0) return;
            JsonRpcFramer.WriteBinary(_output, id, payload.Eof, payload.Bytes.Span);
            AfterWrite();
        }
    }

    private void BeginMessage()
    {
        _writeBuffer.Clear();
        _writer.Reset(_writeBuffer);
        _writer.WriteStartObject();
        _writer.WriteString("jsonrpc", "2.0");
    }

    private void EndMessage()
    {
        _writer.WriteEndObject();
        _writer.Flush();
        if (_disposed == 0) JsonRpcFramer.WriteJson(_output, _writeBuffer.WrittenSpan);
        AfterWrite();
    }

    private void AfterWrite()
    {
        if (_stopAfterWrite) _lifetime.Cancel();
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    private async Task<T> AwaitPendingAsync<T>(Task<T> task, long id, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CancelRemote(id);
            throw;
        }
        catch (OperationCanceledException)
        {
            CancelRemote(id);
            throw;
        }
    }

    private void CancelRemote(long id)
    {
        try
        {
            Notify(ModuleMethods.CancelRequest, new CancelParams(id), SdkJsonContext.Default.CancelParams);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the peer is already gone
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (long key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out IPending? pending)) pending.Fail(ex);
        }
    }

    private static T Read<T>(JsonElement? raw, JsonTypeInfo<T> info)
    {
        if (raw is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element) return default!;
        return JsonSerializer.Deserialize(element, info)!;
    }

    private static Exception ToException(JsonRpcError error)
    {
        ModuleErrorCode? kind = error.Data?.Kind ?? (Enum.IsDefined((ModuleErrorCode)error.Code)
            ? (ModuleErrorCode)error.Code
            : null);

        if (kind is { } code)
        {
            return new ModuleException(code, error.Message)
            {
                RetryAfterMs = error.Data?.RetryAfterMs,
                Detail = error.Data?.Detail,
            };
        }

        return new JsonRpcException(error.Code, error.Message) { ErrorData = error.Data };
    }

    private interface IPending
    {
        void Complete(JsonElement? result);

        void CompleteBinary(ReadOnlyMemory<byte> payload, bool eof);

        void Fail(Exception ex);
    }

    private sealed class Pending<T>(JsonTypeInfo<T> info) : IPending
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public void Complete(JsonElement? result)
        {
            try
            {
                _tcs.TrySetResult(Read(result, info));
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void CompleteBinary(ReadOnlyMemory<byte> payload, bool eof)
            => _tcs.TrySetException(new InvalidDataException("Expected a JSON response but got a binary frame."));

        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    private sealed class PendingBinary : IPending
    {
        private readonly TaskCompletionSource<BinaryPayload> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BinaryPayload> Task => _tcs.Task;

        public void Complete(JsonElement? result)
            => _tcs.TrySetException(new InvalidDataException("Expected a binary frame but got a JSON response."));

        public void CompleteBinary(ReadOnlyMemory<byte> payload, bool eof)
            => _tcs.TrySetResult(new BinaryPayload(payload, eof));

        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }
}
