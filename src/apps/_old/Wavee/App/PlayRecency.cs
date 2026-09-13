using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>uri → last-played unix ms over EVERY uri a play touches: the track, the context it was played from, its
/// album and each billed artist. This is the one "recently played" fact the library sorts on; it is derived here, at
/// the writer, never joined at read time (a read-time join would need the entity resident and would silently fail on
/// the fake backend). Max-merge: a stamp never moves backwards, so a server history older than a local play cannot
/// demote an artist you just listened to.</summary>
public sealed class PlayRecency
{
    /// <summary>Hard cap on distinct uris. 4 096 covers a few thousand artists/albums/tracks — far beyond any library
    /// pane — and keeps the sidecar file a few tens of KB.</summary>
    public const int Cap = 4096;
    /// <summary>Trim target once the cap is crossed: the oldest 256 stamps go in one pass, so a trim costs one sort
    /// per 256 NEW uris rather than one per append.</summary>
    public const int TrimTo = Cap - 256;

    readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Map => _last;
    public int Count => _last.Count;
    public long Of(string uri) => _last.TryGetValue(uri, out var ms) ? ms : 0;

    /// <summary>Stamp one uri. Returns true when the map changed (first sighting, or a NEWER play).</summary>
    public bool Stamp(string? uri, long atMs)
    {
        if (string.IsNullOrEmpty(uri) || atMs <= 0) return false;
        if (_last.TryGetValue(uri!, out var cur) && cur >= atMs) return false;
        _last[uri!] = atMs;
        if (_last.Count > Cap) Trim();
        return true;
    }

    /// <summary>Stamp everything one play names. A bare track play (no context) stamps the track alone; the album
    /// and artists are whatever the writer knew — rows persisted before those fields existed carry neither.</summary>
    public bool Stamp(in PlayLogEntry e)
    {
        bool changed = Stamp(e.TrackUri, e.PlayedAtMs);
        if (e.ContextKind != PlayContextKind.None) changed |= Stamp(e.ContextUri, e.PlayedAtMs);
        changed |= Stamp(e.AlbumUri, e.PlayedAtMs);
        if (e.ArtistUris is { } artists)
            for (int i = 0; i < artists.Count; i++) changed |= Stamp(artists[i], e.PlayedAtMs);
        return changed;
    }

    public bool Merge(IEnumerable<KeyValuePair<string, long>> stamps)
    {
        bool changed = false;
        foreach (var kv in stamps) changed |= Stamp(kv.Key, kv.Value);
        return changed;
    }

    public void Clear() => _last.Clear();

    /// <summary>Snapshot for the pool writer (the PlayLogStore.Snapshot contract: taken on the caller's thread).</summary>
    public KeyValuePair<string, long>[] Snapshot()
    {
        var arr = new KeyValuePair<string, long>[_last.Count];
        int i = 0;
        foreach (var kv in _last) arr[i++] = kv;
        return arr;
    }

    void Trim()
    {
        // Oldest-first drop down to TrimTo. Allocates once per trim (rare by construction — see TrimTo).
        var all = Snapshot();
        Array.Sort(all, static (a, b) => a.Value.CompareTo(b.Value));
        int drop = all.Length - TrimTo;
        for (int i = 0; i < drop; i++) _last.Remove(all[i].Key);
    }
}
