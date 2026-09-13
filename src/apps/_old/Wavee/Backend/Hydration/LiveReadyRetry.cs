using System;

namespace Wavee.Backend.Hydration;

/// <summary>UI-thread readiness retry budget, independent of store-change notifications. A route opened offline
/// gets one attempt after go-live, deferred until active and its initial read has settled. Cancellation returns
/// that attempt; completion (including failure/authoritative empty) consumes it until another offline edge.</summary>
public sealed class LiveReadyRetry
{
    string? _route;
    bool _pending;
    long _generation;
    long _running;

    public long TryBegin(string route, bool live, bool active, bool initialFetching, bool complete)
    {
        if (!StringComparer.Ordinal.Equals(_route, route))
        {
            _route = route;
            _running = 0;
            _generation++;
            _pending = !live;
        }
        if (!live) _pending = true;
        if (!live || !active)
        {
            Cancel(_running);
            return 0;
        }
        if (initialFetching || !_pending || _running != 0) return 0;
        _pending = false;
        if (complete) return 0;
        return _running = ++_generation;
    }

    public bool IsCurrent(long ticket) => ticket != 0 && ticket == _running;

    public bool Complete(long ticket)
    {
        if (!IsCurrent(ticket)) return false;
        _running = 0;
        return true;
    }

    public void Cancel(long ticket)
    {
        if (!IsCurrent(ticket)) return;
        _running = 0;
        _pending = true;
    }
}
