using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Core;
using P = Wavee.Protocol.Player;

namespace Wavee.SpotifyLive;

// The REAL hm:// dealer transport behind Wavee.Backend.ITransport: ONE WebSocket firehose (Events + Requests) + spclient
// HTTP (Request / Publish) over the shared pipeline. Connects wss://{dealer}/?access_token={token}, parses frames
// (DealerFrameParser), emits MESSAGE pushes (with headers — the Spotify-Connection-Id rides there) and REQUEST commands to
// subscribers, answers server pings + sends a 30s keepalive, and reconnects with a fresh token + exponential backoff on any
// drop. The socket is live-only (the user's run verifies it); the frame decode + the routing it feeds are unit-tested.
public sealed class LiveDealerTransport : ITransport, IDisposable
{
    readonly IReadOnlyList<string> _dealerHosts;
    readonly Func<CancellationToken, Task<string>> _accessToken;
    readonly Func<CancellationToken, Task<string>>? _forceRefreshToken;   // G6 — force-mint after a failed wss handshake
    readonly IHttpExchange _spclient;
    readonly Func<string> _spclientBaseUrl;
    readonly WaveeLogger _log;
    readonly Connectivity? _conn;
    readonly SimpleSubject<WireEvent> _events = new();
    readonly SimpleSubject<WireRequest> _requests = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    ClientWebSocket? _ws;
    Task? _loop;
    long _lastRecvTick;   // monotonic ms of the last frame received (any type) — the half-open watchdog reads this

    // Half-open watchdog thresholds. A dead TCP socket can leave ReceiveAsync blocked with NO exception (the "not even
    // observed" case); we ping every 30 s, and if NO frame (not even the pong) arrives within DeadAfterMs the link is
    // dead → abort it (unblocks ReceiveAsync → reconnect).
    const int PingIntervalMs = 30_000;
    const int DeadAfterMs = 70_000;

    public LiveDealerTransport(IReadOnlyList<string> dealerHosts, Func<CancellationToken, Task<string>> accessToken,
        IHttpExchange spclient, Func<string> spclientBaseUrl, WaveeLogger log = default, Connectivity? connectivity = null,
        Func<CancellationToken, Task<string>>? forceRefreshToken = null)
    {
        _dealerHosts = dealerHosts;
        _accessToken = accessToken;
        _forceRefreshToken = forceRefreshToken;
        _spclient = spclient;
        _spclientBaseUrl = spclientBaseUrl;
        _log = log;
        _conn = connectivity;
    }

    /// <summary>Start the connect + receive loop (idempotent). Reconnects with a fresh token + exponential backoff.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public IObservable<WireEvent> Events(string topicPrefix) => new FilteredEvents(_events, topicPrefix);
    public IObservable<WireRequest> Requests(string identPrefix) => new FilteredRequests(_requests, identPrefix);

    public async Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var url = (ch == Channel.SpclientWg ? "https://spclient.wg.spotify.com" : _spclientBaseUrl()) + route;
        var verb = method ?? (body.IsEmpty ? "GET" : "POST");
        var reqHeaders = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (!body.IsEmpty && !reqHeaders.ContainsKey("Content-Type")) reqHeaders["Content-Type"] = "application/protobuf";
        using var resp = await _spclient.SendAsync(new HttpReq(verb, url, reqHeaders, body.IsEmpty ? null : body.ToArray()), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        return new Resp(resp.Status is >= 200 and < 300, ms.ToArray(), resp.Status);
    }

    /// <summary>Ack a dealer REQUEST. The key is JSON-escaped by the writer (never concatenated raw). success=Success.</summary>
    public Task Reply(string requestId, RequestResult result)
    {
        bool success = result == RequestResult.Success;
        var buf = new ArrayBufferWriter<byte>(96);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "reply");
            w.WriteString("key", requestId);            // escaped
            w.WriteStartObject("payload");
            w.WriteBoolean("success", success);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return SendTextAsync(buf.WrittenMemory);
    }

    /// <summary>The Connect device-state announce: PUT /connect-state/v1/devices/{deviceId} with the connection-id header.
    /// Returns the server's Cluster body (the announce response) so the announcer can fold it back into the projection.</summary>
    public async Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
    {
        var url = _spclientBaseUrl() + "/connect-state/v1/devices/" + deviceId;
        var gzipped = HttpCompression.Gzip(putState.Span);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Spotify-Connection-Id"] = connectionId,
            ["Content-Type"] = "application/protobuf",
            ["X-Transfer-Encoding"] = "gzip",
        };
        using var resp = await _spclient.SendAsync(new HttpReq("PUT", url, headers, gzipped), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        var body = ms.ToArray();
        body = MaybeDecompressCluster(body);
        return new Resp(resp.Status is >= 200 and < 300, body, resp.Status);
    }

    /// <summary>connect-state Cluster responses are brotli-compressed with no Content-Type — HttpClient won't auto-inflate.</summary>
    static byte[] MaybeDecompressCluster(byte[] body)
    {
        if (body.Length == 0) return body;
        try { P.Cluster.Parser.ParseFrom(body); return body; }
        catch (InvalidProtocolBufferException) { }
        try { return HttpCompression.BrotliDecompress(body); }
        catch { return body; }
    }

    async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        bool forceToken = false;   // a failed HANDSHAKE may be an expired/invalid token (G6) — force-mint once before the retry
        while (!ct.IsCancellationRequested)
        {
            string host = HostAt(attempt);   // rotate hosts across attempts (failover) — a bad dealer doesn't pin us
            _conn?.Set(attempt == 0 ? ConnectionStatus.Connecting : ConnectionStatus.Reconnecting);
            try
            {
                var token = forceToken && _forceRefreshToken is { } force
                    ? await force(ct).ConfigureAwait(false)
                    : await _accessToken(ct).ConfigureAwait(false);
                forceToken = false;
                using var ws = new ClientWebSocket();
                _ws = ws;
                try
                {
                    await ws.ConnectAsync(new Uri($"wss://{host}/?access_token={Uri.EscapeDataString(token)}"), ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // The wss handshake itself failed — indistinguishable from a rejected (expired) token at this layer, and
                    // the plain provider only re-mints near expiry. Force a refresh for the NEXT attempt (once per failure).
                    forceToken = _forceRefreshToken is not null;
                    throw;
                }
                attempt = 0;   // a clean connect resets the backoff ladder + host rotation
                System.Threading.Volatile.Write(ref _lastRecvTick, Environment.TickCount64);
                _conn?.Set(ConnectionStatus.Online);
                _log.Info("dealer connected (" + host + ")");
                using var keepalive = StartKeepalive(ws, ct);
                await ReceiveLoop(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Warn("dealer disconnected: " + ex.Message, ex); }
            if (ct.IsCancellationRequested) break;
            _conn?.Set(ConnectionStatus.Reconnecting);
            // exponential backoff: 3, 6, 12, 24 → cap 30s (a fresh token is minted on the next loop via _accessToken).
            int secs = System.Math.Min(30, 3 * (1 << System.Math.Min(attempt, 4)));
            attempt++;
            try { await Task.Delay(TimeSpan.FromSeconds(secs), ct).ConfigureAwait(false); } catch { break; }
        }
        _conn?.Set(ConnectionStatus.Offline);
    }

    // Round-robin the dealer hosts across reconnect attempts (strip any :port). Falls back to a sane default if empty.
    string HostAt(int attempt) =>
        _dealerHosts.Count == 0 ? "dealer.spotify.com" : _dealerHosts[attempt % _dealerHosts.Count].Split(':')[0];

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var frame = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            System.Threading.Volatile.Write(ref _lastRecvTick, Environment.TickCount64);   // any frame = the link is alive
            var utf8 = frame.GetBuffer().AsSpan(0, (int)frame.Length);
            var f = DealerFrameParser.Parse(utf8);
            DealerArchive.Instance.RecordInbound(utf8, f);
            switch (f.Type)
            {
                case DealerFrameType.Ping:
                    await SendTextAsync("{\"type\":\"pong\"}").ConfigureAwait(false);
                    break;
                case DealerFrameType.Pong:
                    break;   // our keepalive's pong — the activity timestamp above already refreshed the watchdog
                case DealerFrameType.Message when f.Uri is { Length: > 0 } uri:
                    _events.OnNext(new WireEvent(uri, f.Payload, f.Headers));
                    break;
                case DealerFrameType.Request when f.Key is { Length: > 0 } key:
                    _requests.OnNext(new WireRequest(key, f.MessageIdent ?? "", f.Payload,
                        f.Headers ?? EmptyHeaders));
                    break;
            }
        }
    }

    static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    IDisposable StartKeepalive(ClientWebSocket ws, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(PingIntervalMs, cts.Token).ConfigureAwait(false);
                    // Half-open watchdog: no frame at all (not even a pong to our pings) in DeadAfterMs ⇒ the TCP socket is
                    // dead-but-not-closed. Abort it → the blocked ReceiveAsync throws → RunAsync reconnects (Reconnecting).
                    if (Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastRecvTick) > DeadAfterMs)
                    {
                        _log.Warn("dealer half-open (no traffic) — forcing reconnect");
                        try { ws.Abort(); } catch { }
                        return;
                    }
                    await SendTextAsync("{\"type\":\"ping\"}").ConfigureAwait(false);
                    DealerArchive.Instance.RecordKeepalive(DealerFrameType.Ping);
                }
            }
            catch { /* loop ends on disconnect/cancel */ }
        }, cts.Token);
        return cts;
    }

    Task SendTextAsync(string json) => SendTextAsync((ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(json));

    async Task SendTextAsync(ReadOnlyMemory<byte> utf8)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await ws.SendAsync(utf8, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false); }
        catch { /* a failed send drops the connection → the receive loop exits and we reconnect */ }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _conn?.Set(ConnectionStatus.Offline);
        _cts.Cancel();
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }

    // Forward only the hm:// MESSAGE topics matching the requested prefix (the library router subscribes "hm://").
    sealed class FilteredEvents(IObservable<WireEvent> src, string prefix) : IObservable<WireEvent>
    {
        public IDisposable Subscribe(IObserver<WireEvent> observer) => src.Subscribe(new Obs(observer, prefix));
        sealed class Obs(IObserver<WireEvent> inner, string prefix) : IObserver<WireEvent>
        {
            public void OnNext(WireEvent e) { if (e.Topic.StartsWith(prefix, StringComparison.Ordinal)) inner.OnNext(e); }
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception ex) => inner.OnError(ex);
        }
    }

    // Forward only the REQUEST frames whose message_ident matches the requested prefix (the Connect router subscribes
    // "hm://connect-state/v1/").
    sealed class FilteredRequests(IObservable<WireRequest> src, string prefix) : IObservable<WireRequest>
    {
        public IDisposable Subscribe(IObserver<WireRequest> observer) => src.Subscribe(new Obs(observer, prefix));
        sealed class Obs(IObserver<WireRequest> inner, string prefix) : IObserver<WireRequest>
        {
            public void OnNext(WireRequest r) { if (r.MessageIdent.StartsWith(prefix, StringComparison.Ordinal)) inner.OnNext(r); }
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception ex) => inner.OnError(ex);
        }
    }
}
