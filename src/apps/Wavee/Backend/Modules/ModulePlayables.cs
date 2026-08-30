using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── THE RESOLVED-PLAYABLE CACHE, and the process-wide answers built on it ────────────────────────────────────────────
// A module's `playback/resolve` answer is needed by three surfaces that CANNOT await an RPC: the video tier's
// "does this playable have video?", the projection's "is it live?", and the composite resolver's "what url?". They all
// read the cache — one ordinal dictionary probe, no allocation, no await — exactly the way VideoPresence answers the
// has-video question off the association plane.
//
// Entries carry the module's own `expiresAtUnixMs`. An expired entry is treated as ABSENT rather than deleted eagerly:
// the sync answers stay honest and the next resolve replaces it.

/// <summary>The per-uri cache of module resolve answers.</summary>
public sealed class ModulePlayableCache
{
    readonly ConcurrentDictionary<string, ResolvedPlayable> _entries = new(StringComparer.Ordinal);
    readonly Func<long> _nowUnixMs;

    /// <summary>Build a cache.</summary>
    /// <param name="nowUnixMs">Clock seam (tests); null uses the wall clock.</param>
    public ModulePlayableCache(Func<long>? nowUnixMs = null)
        => _nowUnixMs = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Store (or replace) the answer for one playable uri.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    /// <param name="resolved">The module's answer.</param>
    public void Put(string playableUri, ResolvedPlayable resolved)
    {
        if (string.IsNullOrEmpty(playableUri)) return;
        ArgumentNullException.ThrowIfNull(resolved);
        _entries[playableUri] = resolved;
    }

    /// <summary>The unexpired answer for this uri, or null.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public ResolvedPlayable? Get(string? playableUri)
    {
        if (playableUri is not { Length: > 0 }) return null;
        if (!_entries.TryGetValue(playableUri, out ResolvedPlayable? entry)) return null;
        return IsExpired(entry) ? null : entry;
    }

    /// <summary>The answer for this uri EVEN IF it has expired — the re-resolve path needs to know what died.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public ResolvedPlayable? GetIncludingExpired(string? playableUri)
        => playableUri is { Length: > 0 } && _entries.TryGetValue(playableUri, out ResolvedPlayable? e) ? e : null;

    /// <summary>Has this entry passed its <c>expiresAtUnixMs</c>?</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public bool IsExpired(string? playableUri)
        => playableUri is { Length: > 0 } && _entries.TryGetValue(playableUri, out ResolvedPlayable? e) && IsExpired(e);

    /// <summary>Drop one entry (a <c>playback/expired</c> notification, or a forced re-resolve).</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public void Invalidate(string? playableUri)
    {
        if (playableUri is { Length: > 0 }) _entries.TryRemove(playableUri, out _);
    }

    /// <summary>Drop every entry belonging to one module (it crashed, or was stopped).</summary>
    /// <param name="moduleId">The module id.</param>
    public void InvalidateModule(string moduleId)
    {
        string prefix = ModuleUri.Prefix(moduleId);
        foreach (KeyValuePair<string, ResolvedPlayable> e in _entries)
            if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(e.Key, out _);
    }

    /// <summary>Does this playable play through the VIDEO host? Sync, one probe — the has-video hot path.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public bool HasVideo(string? playableUri) => Get(playableUri) is { Form: MediaForm.Video };

    /// <summary>Is this playable a live stream (no seeking, no auto-advance on a socket drop)?</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public bool IsLive(string? playableUri) => Get(playableUri) is { IsLive: true };

    /// <summary>The resolved locator for this playable, or null.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    public MediaLocator? Locator(string? playableUri) => Get(playableUri)?.Media;

    /// <summary>How many entries are held (diagnostics).</summary>
    public int Count => _entries.Count;

    bool IsExpired(ResolvedPlayable p) => p.ExpiresAtUnixMs is { } exp && exp > 0 && _nowUnixMs() >= exp;
}

/// <summary>
/// The PROCESS-WIDE, allocation-free module answers — the <c>VideoPresence</c> pattern. Surfaces that must answer
/// synchronously on a render/decision path (the video tier, the has-video predicate, the live projection) read these
/// and nothing else; the composition root attaches the cache once and never re-points it.
/// </summary>
public static class ModulePlayables
{
    static ModulePlayableCache? _cache;

    /// <summary>Attach the cache (composition root). Null detaches — every answer is then "no", which is exactly what a
    /// build with no module host can honestly say.</summary>
    /// <param name="cache">The cache owned by the <see cref="ModuleHost"/>, or null.</param>
    public static void Attach(ModulePlayableCache? cache) => _cache = cache;

    /// <summary>The attached cache, for callers that need more than the three predicates.</summary>
    public static ModulePlayableCache? Cache => _cache;

    /// <summary>Does this playable play through the video host?</summary>
    /// <param name="uri">The playable uri (any uri; non-module ones answer false).</param>
    public static bool HasVideo(string? uri) => _cache is { } c && c.HasVideo(uri);

    /// <summary>Is this playable live?</summary>
    /// <param name="uri">The playable uri (any uri; non-module ones answer false).</param>
    public static bool IsLive(string? uri) => _cache is { } c && c.IsLive(uri);

    /// <summary>The cached resolve answer for this playable, or null.</summary>
    /// <param name="uri">The playable uri.</param>
    public static ResolvedPlayable? Get(string? uri) => _cache?.Get(uri);
}
