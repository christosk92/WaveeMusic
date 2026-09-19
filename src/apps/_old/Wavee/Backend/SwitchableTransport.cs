using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

// ── Switchable transport facade (RC2) ────────────────────────────────────────────────────────────────────────────────
// Mirrors SwitchableConnectivity/SwitchableState (Switchable.cs): the mutation drain + dealer wiring bind to this stable
// facade once, and go-live/logout swap the inner transport (stub → live dealer → stub) via SetInner without rebuilding.
// Every member delegates to the CURRENT inner per-call; Events/Requests subscribe the inner live at subscribe time — a
// SetInner does NOT re-home existing subscriptions (same contract as the other Switchables). The mutation drain and the
// DealerRouter both live behind go-live, so no pre-swap subscription needs migration.
public sealed class SwitchableTransport : ITransport
{
    volatile ITransport _inner;

    public SwitchableTransport(ITransport initial) => _inner = initial;

    public ITransport Inner => _inner;
    public void SetInner(ITransport t) => _inner = t;

    public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
        string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        => _inner.Request(ch, route, body, ct, method, headers);

    public IObservable<WireEvent> Events(string topicPrefix) => _inner.Events(topicPrefix);
    public IObservable<WireRequest> Requests(string identPrefix) => _inner.Requests(identPrefix);
    public Task Reply(string requestId, RequestResult result) => _inner.Reply(requestId, result);
    public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
        => _inner.Publish(deviceId, connectionId, putState, ct);
}
