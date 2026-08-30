using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── ENGINE ③ — Transport (multiplexed channels + middleware pipeline) ────────────────────────────────────────────────
// The real impl owns the AP/Shannon socket (Mercury/AudioKey/packets), the dealer WebSocket (events + REQUEST→reply +
// PutState), and the HTTPS channels (spclient/pathfinder/extended-metadata/login5/client-token) under a uniform
// middleware stack (auth-token · client-token · 401-refresh · 429-backoff · market/locale-stamp). The live impl is
// SpotifyLive/LiveDealerTransport; StubTransport drives the headless tests.

public enum Channel { ApMercury, Spclient, SpclientWg, Pathfinder, ExtendedMetadata, DealerWs, Login5, ClientToken }

public readonly record struct Resp(bool Ok, byte[] Body, int Status);

/// <summary>A dealer server→client MESSAGE (a push). <see cref="Headers"/> carries the frame headers — the
/// <c>Spotify-Connection-Id</c> on a <c>hm://pusher/v1/connections/</c> push is the Connect announce gate.</summary>
public sealed record WireEvent(string Topic, byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>A dealer server→client REQUEST (a remote player command targeting this device). <see cref="Command"/> is the
/// decoded (base64 → gunzip) command JSON; <see cref="RequestId"/> is the reply key; <see cref="MessageIdent"/> routes it
/// (e.g. <c>hm://connect-state/v1/player/command</c>). Must be acked via <see cref="ITransport.Reply"/> within ~10 s.</summary>
public readonly record struct WireRequest(
    string RequestId, string MessageIdent, byte[] Command, IReadOnlyDictionary<string, string> Headers);

/// <summary>The dealer reply result codes (the live Spotify <c>MessageType</c> values — note 4 is unused on the wire).
/// Per the locked decision, acks are ack-on-dispatch: <see cref="Success"/> means "received + dispatched", not "audibly
/// done"; a later failure surfaces via the cluster/PutState, not by withholding the ack (which would breach the 10 s SLA).</summary>
public enum RequestResult { Success = 0, DeviceNotFound = 1, ContextPlayerError = 2, DeviceDisappeared = 3, UpstreamError = 5, DeviceDoesNotSupportCommand = 6, RateLimited = 7 }

public interface ITransport
{
    /// <summary><paramref name="method"/> overrides the default (GET when body empty, else POST) — e.g. PUT for the
    /// connect/volume endpoint.</summary>
    Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null);
    IObservable<WireEvent> Events(string topicPrefix);          // dealer MESSAGE pushes by hm:// prefix
    IObservable<WireRequest> Requests(string identPrefix);      // dealer REQUEST frames by message_ident prefix
    Task Reply(string requestId, RequestResult result);         // ③a — the dealer server→client REQUEST→reply ack
    /// <summary>③b — the Connect device-state announce: PUT /connect-state/v1/devices/{deviceId} with the
    /// X-Spotify-Connection-Id header. Returns the server's Cluster body so the announcer can fold it back in.</summary>
    Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default);
}

public sealed class StubTransport : ITransport
{
    readonly SimpleSubject<WireEvent> _events = new();
    readonly SimpleSubject<WireRequest> _requests = new();
    int _requestCount;
    public int RequestCount => _requestCount;

    /// <summary>Test visibility: the last ack code sent + the count of announce PUTs.</summary>
    public RequestResult? LastReply { get; private set; }
    public int PublishCount { get; private set; }
    public byte[]? LastPublishBody { get; private set; }
    /// <summary>Test hook: the Cluster body Publish() hands back (the announce response). Default empty.</summary>
    public byte[] PublishResponse { get; set; } = Array.Empty<byte>();

    /// <summary>Test visibility: the last Request route/method/body (e.g. to assert the connect/volume PUT).</summary>
    public string? LastRequestRoute { get; private set; }
    public string? LastRequestMethod { get; private set; }
    public byte[]? LastRequestBody { get; private set; }
    public IReadOnlyDictionary<string, string>? LastRequestHeaders { get; private set; }

    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        Interlocked.Increment(ref _requestCount);
        LastRequestRoute = route;
        LastRequestMethod = method ?? (body.IsEmpty ? "GET" : "POST");
        LastRequestBody = body.ToArray();
        LastRequestHeaders = headers is null ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));   // stub: the request "succeeds" with no body
    }

    public IObservable<WireEvent> Events(string topicPrefix) => new Prefix<WireEvent>(_events, e => e.Topic, topicPrefix);
    public IObservable<WireRequest> Requests(string identPrefix) => new Prefix<WireRequest>(_requests, r => r.MessageIdent, identPrefix);
    public Task Reply(string requestId, RequestResult result) { LastReply = result; return Task.CompletedTask; }

    public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
    {
        PublishCount++;
        LastPublishBody = putState.ToArray();
        return Task.FromResult(new Resp(true, PublishResponse, 200));
    }

    /// <summary>Test hook: simulate a dealer push (a live revalidation trigger).</summary>
    public void PushEvent(WireEvent e) => _events.OnNext(e);
    /// <summary>Test hook: simulate a dealer REQUEST (a remote player command targeting this device).</summary>
    public void PushRequest(WireRequest r) => _requests.OnNext(r);

    // Faithful to LiveDealerTransport: Events/Requests deliver only items whose topic/ident matches the subscribed prefix.
    sealed class Prefix<T>(IObservable<T> src, Func<T, string> key, string prefix) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> o) => src.Subscribe(new Obs(o, key, prefix));
        sealed class Obs(IObserver<T> inner, Func<T, string> key, string prefix) : IObserver<T>
        {
            public void OnNext(T v) { if (key(v).StartsWith(prefix, StringComparison.Ordinal)) inner.OnNext(v); }
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception e) => inner.OnError(e);
        }
    }
}
