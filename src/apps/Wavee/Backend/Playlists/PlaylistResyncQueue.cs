using System;
using System.Collections.Generic;

namespace Wavee.Backend.Playlists;

/// <summary>Invariant I4 — "never advance a revision past ops you did not apply", carried across the drain boundary,
/// and now the ONE record of whether that revalidation has happened yet.
/// <para>Lifecycle per uri: <see cref="Phase.Marked"/> (a /changes reply could not be folded) → <see cref="Phase.Revalidating"/>
/// (the loop took it) → gone (<see cref="Resolve"/> — ANY path that adopts a fresh snapshot/diff for the uri), or
/// <see cref="Phase.Failed"/> (the revalidate threw; attempts counted; the loop retries with backoff, and the page can
/// show it). <see cref="Changed"/> fires on the calling thread — the sync loop or a pool thread — so subscribers marshal.</para>
/// <para>ONE instance is shared by <c>OpRebaseStrategy</c> and <c>LibrarySync</c> at the composition root — a required
/// ctor dependency on both, never an optional/nullable one (an unwired queue would silently lose convergence).</para></summary>
public sealed class PlaylistResyncQueue
{
    public enum Phase : byte { None = 0, Marked = 1, Revalidating = 2, Failed = 3 }

    /// <summary>What the UI needs: the phase, when the uri was first marked (unix ms), how many revalidates failed.</summary>
    public readonly record struct Entry(Phase Phase, long SinceUtcMs, int Attempts)
    {
        public static readonly Entry None = default;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Func<long> _clock;

    /// <summary>Off-thread notification: the uri whose entry changed. One subscriber (LibraryBridge) — an Action, not an
    /// event, mirroring <c>PlaylistFetcher.onRevisionChanged</c>.</summary>
    public Action<string>? Changed;

    public PlaylistResyncQueue(Func<long>? clock = null)
        => _clock = clock ?? DefaultClock;

    static long DefaultClock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A /changes reply for this uri could not be folded in place. Idempotent: a uri already tracked keeps its
    /// original stamp and attempts (so "how long has this been unresolved" is honest across repeated marks).</summary>
    public void Mark(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;
        lock (_gate)
        {
            if (_entries.TryGetValue(playlistUri, out var e))
            {
                if (e.Phase == Phase.Marked) return;                      // nothing changed
                _entries[playlistUri] = e with { Phase = Phase.Marked };  // Failed → Marked (a retry) / Revalidating → Marked
            }
            else _entries[playlistUri] = new Entry(Phase.Marked, _clock(), 0);
        }
        Changed?.Invoke(playlistUri);
    }

    /// <summary>Take every MARKED uri for revalidation. They stay tracked as <see cref="Phase.Revalidating"/> until
    /// <see cref="Resolve"/> or <see cref="Fail"/>. Empty is the common case (allocation-free early-out).</summary>
    public IReadOnlyList<string> TakeAll()
    {
        List<string>? taken = null;
        lock (_gate)
        {
            foreach (var (uri, e) in _entries)
                if (e.Phase == Phase.Marked) (taken ??= new List<string>()).Add(uri);
            if (taken is null) return Array.Empty<string>();
            for (int i = 0; i < taken.Count; i++) _entries[taken[i]] = _entries[taken[i]] with { Phase = Phase.Revalidating };
        }
        for (int i = 0; i < taken.Count; i++) Changed?.Invoke(taken[i]);
        return taken;
    }

    /// <summary>A retry is starting for a FAILED uri (the loop's scheduled retry or the page's Retry button).</summary>
    public bool TryBeginRetry(string playlistUri)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistUri, out var e) || e.Phase != Phase.Failed) return false;
            _entries[playlistUri] = e with { Phase = Phase.Revalidating };
        }
        Changed?.Invoke(playlistUri);
        return true;
    }

    /// <summary>The uri converged (a snapshot or diff landed — whichever path did it). No-op when untracked.</summary>
    public void Resolve(string playlistUri)
    {
        bool removed;
        lock (_gate) removed = _entries.Remove(playlistUri);
        if (removed) Changed?.Invoke(playlistUri);
    }

    /// <summary>The revalidate threw. Returns the new attempt count (0 when the uri was not tracked).</summary>
    public int Fail(string playlistUri)
    {
        int attempts;
        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistUri, out var e)) return 0;
            attempts = e.Attempts + 1;
            _entries[playlistUri] = e with { Phase = Phase.Failed, Attempts = attempts };
        }
        Changed?.Invoke(playlistUri);
        return attempts;
    }

    public Entry Get(string playlistUri)
    {
        lock (_gate) return _entries.TryGetValue(playlistUri, out var e) ? e : Entry.None;
    }
}
