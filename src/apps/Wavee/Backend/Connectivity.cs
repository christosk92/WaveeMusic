using System;
using Wavee.Core;

namespace Wavee.Backend;

// ── Connectivity — the observable dealer-socket status (so a network drop is SEEN, not silent) ────────────────────────
// The live transport calls Set() on every socket transition (connect → Online, drop / half-open → Reconnecting, teardown
// → Offline); the UI binds IConnectivity. Signals-first (SimpleSubject), matching the rest of the seams.

/// <summary>A driven connection-status signal. Thread-safe — the transport drives it from its socket + keepalive tasks.</summary>
public sealed class Connectivity : IConnectivity
{
    readonly SimpleSubject<ConnectionStatus> _changed;
    readonly object _gate = new();

    public Connectivity(ConnectionStatus initial = ConnectionStatus.Offline)
    {
        Status = initial;
        _changed = new SimpleSubject<ConnectionStatus>(initial);
    }

    public ConnectionStatus Status { get; private set; }
    public IObservable<ConnectionStatus> StatusChanged => _changed;

    public void Set(ConnectionStatus s)
    {
        lock (_gate) { if (s == Status) return; Status = s; }
        _changed.OnNext(s);
    }
}

/// <summary>Switchable connectivity facade (offline fake → live transport on go-live), mirroring SwitchableState so the UI
/// binds once and the live socket status swaps in without a rebuild.</summary>
public sealed class SwitchableConnectivity : IConnectivity, IDisposable
{
    readonly SimpleSubject<ConnectionStatus> _changed;
    readonly object _gate = new();
    IConnectivity _inner;
    IDisposable? _sub;

    public SwitchableConnectivity(IConnectivity inner)
    {
        _inner = inner;
        _changed = new SimpleSubject<ConnectionStatus>(inner.Status);
        Wire(inner);
    }

    public void SetInner(IConnectivity inner)
    {
        lock (_gate) { _sub?.Dispose(); _inner = inner; Wire(inner); }
        _changed.OnNext(inner.Status);
    }

    void Wire(IConnectivity c) => _sub = c.StatusChanged.Subscribe(Observers.From<ConnectionStatus>(s => _changed.OnNext(s)));

    public ConnectionStatus Status { get { lock (_gate) return _inner.Status; } }
    public IObservable<ConnectionStatus> StatusChanged => _changed;
    public void Dispose() => _sub?.Dispose();
}
