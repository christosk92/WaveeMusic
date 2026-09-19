using System;

namespace Wavee.Backend.Collections;

// ── §7.1 — the collection self-write echo registry ────────────────────────────────────────────────────────────────────
// A small fixed-size ring of the most recent client_update_ids from ACCEPTED /collection/v2/write calls. The write strategy
// records on r.Ok; LibrarySync checks it on an inbound PubSubUpdate and drops our own echo before any store work (cheaper
// than the pending-op shield, and it survives after the outbox row is gone). Lock-guarded: recorded on the drain thread,
// read on the sync loop. 32 entries is ample for burst self-echo (the dealer redelivers within seconds).
public sealed class CollectionEchoRing
{
    const int Capacity = 32;
    readonly string[] _ids = new string[Capacity];
    readonly object _gate = new();
    int _next;

    public void Record(string clientUpdateId)
    {
        if (string.IsNullOrEmpty(clientUpdateId)) return;
        lock (_gate) { _ids[_next] = clientUpdateId; _next = (_next + 1) % Capacity; }
    }

    public bool Contains(string clientUpdateId)
    {
        if (string.IsNullOrEmpty(clientUpdateId)) return false;
        lock (_gate)
            for (int i = 0; i < Capacity; i++)
                if (_ids[i] == clientUpdateId) return true;
        return false;
    }
}
