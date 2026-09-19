using System;
using System.Collections.Generic;

namespace Wavee;

// The bounded FIRST-OBSERVATION stamp map (F.7.5 / F.7.10) — the honest playlist "date added" proxy.
// Engine-free (System only), source-included by src/apps/Wavee.Tests.
//
// WHY THIS EXISTS: playlists have no add timestamp anywhere. The rootlist is an ordered marker stream, not a timestamped
// SavedItem set, so there is nothing to sort "Recently added" by. Instead the projection records the first time it ever
// observes a playlist id and sorts by that. On the very first run every playlist gets the SAME stamp and ties break by
// SourceOrder ascending — which is Spotify's own newest-first rootlist order — and from then on every newly added
// playlist gets a genuinely correct relative position. The sort is therefore honest-but-approximate, and the surface's
// label must not promise more than that.

public sealed class SidebarFirstSeen
{
    /// <summary>Id cap (F.7.5). Beyond it the OLDEST stamp is evicted to admit a new id, so the map can never grow
    /// without bound even if pruning never runs.</summary>
    public const int Cap = 2000;

    /// <summary>A shared, FROZEN instance: it never records and never mutates, so it is safe as a default argument for a
    /// projection that must not persist anything (a preview, a test that does not care).</summary>
    public static readonly SidebarFirstSeen Frozen = new(frozen: true);

    readonly Dictionary<string, long> _first = new(StringComparer.Ordinal);
    readonly Func<long> _clock;
    readonly bool _frozen;

    /// <summary>Ids stamped since the last <see cref="ResetNewCount"/> — the projection reports this as
    /// <c>SidebarProjectionResult.NewFirstSeenStamps</c>, and a non-zero value is what triggers a document commit.</summary>
    public int NewStamps { get; private set; }

    public SidebarFirstSeen(Func<long>? nowUnixMs = null)
    {
        _clock = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _frozen = false;
    }

    SidebarFirstSeen(bool frozen)
    {
        _clock = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _frozen = frozen;
    }

    /// <summary>Rehydrate from the persisted document (invalid rows are skipped; the newest stamp wins on a duplicate id).</summary>
    public void Load(IReadOnlyList<KeyValuePair<string, long>> stamps)
    {
        if (_frozen) return;
        for (int i = 0; i < stamps.Count; i++)
        {
            var kv = stamps[i];
            if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
            if (!_first.TryGetValue(kv.Key, out long cur) || kv.Value < cur) _first[kv.Key] = kv.Value;
        }
        NewStamps = 0;
    }

    public int Count => _first.Count;

    /// <summary>The stamp for an id, WITHOUT recording one (0 = never observed).</summary>
    public long Peek(string id) => _first.TryGetValue(id, out long ms) ? ms : 0L;

    /// <summary>The stamp for an id, recording "now" the first time the id is ever seen. This is the call the projection
    /// makes per playlist row; <see cref="NewStamps"/> counts the fresh records so the caller knows to persist.</summary>
    public long Stamp(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0L;
        if (_first.TryGetValue(id, out long ms)) return ms;
        long now = _clock();
        if (_frozen) return now;                      // behave as "just seen" without mutating the shared instance
        if (_first.Count >= Cap) EvictOldest();
        _first[id] = now;
        NewStamps++;
        return now;
    }

    public void ResetNewCount() => NewStamps = 0;

    /// <summary>Drop stamps for ids that are no longer in the library (called on save, per F.7.5). Returns the number of
    /// entries removed. <paramref name="live"/> is the id set the projection just produced.</summary>
    public int PruneTo(IReadOnlyCollection<string> live)
    {
        if (_frozen || _first.Count == 0) return 0;
        List<string>? dead = null;
        foreach (var id in _first.Keys)
        {
            bool found = false;
            foreach (var l in live) if (string.Equals(l, id, StringComparison.Ordinal)) { found = true; break; }
            if (!found) (dead ??= new List<string>()).Add(id);
        }
        if (dead is null) return 0;
        for (int i = 0; i < dead.Count; i++) _first.Remove(dead[i]);
        return dead.Count;
    }

    /// <summary>Snapshot for persistence (append-into; the caller owns the list). Order is unspecified — the document is
    /// a map, not a sequence.</summary>
    public void CopyTo(List<KeyValuePair<string, long>> into)
    {
        foreach (var kv in _first) into.Add(kv);
    }

    // O(n) and only ever at the cap — a 2000-entry scan, on the UI thread, at most once per newly observed id.
    void EvictOldest()
    {
        string? oldestId = null;
        long oldest = long.MaxValue;
        foreach (var kv in _first)
            if (kv.Value < oldest) { oldest = kv.Value; oldestId = kv.Key; }
        if (oldestId is not null) _first.Remove(oldestId);
    }
}
