using System;
using Wavee.Core;

namespace Wavee.Backend;

// ── The Spotify Connect connection-id capture (single responsibility) ─────────────────────────────────────────────────
// Captures the dealer connection_id from the pusher hello HEADER (not the URI tail) and exposes it as a signal. The actual
// device announce / player_state PUT is the DeviceStatePublisher's job (the single PutState writer) — this class only
// surfaces the id it needs. (WaveeMusic folds capture + announce + publish into one 1365-LOC manager; we split them.)
public sealed class ConnectService : IDisposable
{
    readonly SimpleSubject<string?> _connectionId = new(null);
    readonly IDisposable _pusherSub;
    readonly object _gate = new();
    string? _connId;

    public ConnectService(ITransport transport)
    {
        _pusherSub = transport.Events("hm://pusher/v1/connections/").Subscribe(Observers.From<WireEvent>(OnPusher));
    }

    /// <summary>The dealer connection id (null until the first pusher hello / after a reconnect drop). Replays last value.</summary>
    public IObservable<string?> ConnectionId => _connectionId;
    public string? CurrentConnectionId { get { lock (_gate) return _connId; } }

    void OnPusher(WireEvent e)
    {
        if (e.Headers is null || !e.Headers.TryGetValue("Spotify-Connection-Id", out var id) || string.IsNullOrEmpty(id))
            return;
        lock (_gate) _connId = id;
        _connectionId.OnNext(id);
    }

    public void Dispose() => _pusherSub.Dispose();
}
