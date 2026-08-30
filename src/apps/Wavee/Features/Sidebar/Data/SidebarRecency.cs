using System;
using System.Collections.Generic;

namespace Wavee;

// Route-key → last-visited ticks, built from HistoryStore (F.7.6). Engine-free (System only), source-included by
// src/apps/Wavee.Tests.
//
// SEMANTICS, stated plainly for the surface authors: HistoryStore logs NAVIGATION, so "Recents" means recently OPENED —
// not recently played and not recently added. That is exactly what Spotify's own library "Recents" sort means, so the
// label is honest; a surface must NOT relabel it "Recently played".

/// <summary>One navigation observation: the route key that was opened and when (UTC ticks). The route key is the entry /
/// pin id (F.5.4), so the recency join is an identity lookup — no prefix stripping, no per-row parsing.</summary>
public readonly record struct SidebarVisit(string RouteKey, long TicksUtc);

public sealed class SidebarRecency
{
    /// <summary>The shared "nothing was ever visited" instance (the seed a surface renders before history loads).</summary>
    public static readonly SidebarRecency Empty = new(new Dictionary<string, long>(0, StringComparer.Ordinal));

    readonly Dictionary<string, long> _last;

    SidebarRecency(Dictionary<string, long> last) => _last = last;

    public int Count => _last.Count;

    /// <summary>Last-visited UTC ticks for an entry/pin id; 0 = never visited.</summary>
    public long LastVisitedTicks(string? id) => id is not null && _last.TryGetValue(id, out long t) ? t : 0L;

    /// <summary>Build from an oldest-first visit log (HistoryStore's own order). Walks BACKWARDS so the FIRST hit per key
    /// wins — i.e. the newest visit — which makes the pass O(n) with no comparisons and no per-key max().</summary>
    public static SidebarRecency Build(IReadOnlyList<SidebarVisit> visitsOldestFirst)
    {
        if (visitsOldestFirst.Count == 0) return Empty;
        var map = new Dictionary<string, long>(visitsOldestFirst.Count, StringComparer.Ordinal);
        for (int i = visitsOldestFirst.Count - 1; i >= 0; i--)
        {
            var v = visitsOldestFirst[i];
            if (v.RouteKey is { Length: > 0 }) map.TryAdd(v.RouteKey, v.TicksUtc);
        }
        return new SidebarRecency(map);
    }

    /// <summary>Build straight off an oldest-first log of any row type — the shape the app uses over
    /// <c>HistoryStore.Entries</c>:
    /// <code>SidebarRecency.Build(store.Entries, static e => e.Route.Name, static e => e.VisitedAt.ToUniversalTime().Ticks)</code>
    /// The accessors keep <c>HistoryEntry</c> (which lives in an engine-bound file) out of this layer, so the whole Data/
    /// folder stays source-includable by the test assembly. Pass STATIC lambdas — they are cached by the compiler, so a
    /// rebuild allocates nothing but the dictionary.</summary>
    public static SidebarRecency Build<T>(IReadOnlyList<T> entriesOldestFirst, Func<T, string> keyOf, Func<T, long> ticksUtcOf)
    {
        if (entriesOldestFirst.Count == 0) return Empty;
        var map = new Dictionary<string, long>(entriesOldestFirst.Count, StringComparer.Ordinal);
        for (int i = entriesOldestFirst.Count - 1; i >= 0; i--)
        {
            var key = keyOf(entriesOldestFirst[i]);
            if (key is { Length: > 0 }) map.TryAdd(key, ticksUtcOf(entriesOldestFirst[i]));
        }
        return new SidebarRecency(map);
    }
}
