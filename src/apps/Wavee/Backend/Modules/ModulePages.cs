using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── MODULE-PROVIDED PAGES — the cache, and the process-wide sync answers built on it ─────────────────────────────────
// A module is out of process, so a "page" is a declarative DOCUMENT the app renders (the sidebar extension platform's
// posture: an untrusted contribution stays declarative and host-rendered, never code). This file owns the two things
// that are NOT rendering:
//
//   1. the per-uri `ModulePageDoc` cache, with the module's own `ExpiresAtUnixMs` (10 minutes when it says nothing);
//   2. the ROUTE ALGEBRA — `module:<wavee:module:…>` — and the sync, allocation-free lookups the player bar and the
//      immersive stage need on a render path that cannot await an RPC.
//
// It is the exact twin of `ModulePlayables` next door: one ordinal dictionary probe, no await, and an expired entry
// reads as ABSENT rather than being deleted eagerly, so the answer stays honest until the next fetch replaces it.

/// <summary>The per-uri cache of module page documents.</summary>
public sealed class ModulePageCache
{
    /// <summary>How long a document lives when the module states no expiry of its own.</summary>
    public const long DefaultTtlMs = 10 * 60 * 1000;

    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Func<long> _nowUnixMs;

    readonly record struct Entry(ModulePageDoc Doc, long ExpiresAtUnixMs);

    /// <summary>Build a cache.</summary>
    /// <param name="nowUnixMs">Clock seam (tests); null uses the wall clock.</param>
    public ModulePageCache(Func<long>? nowUnixMs = null)
        => _nowUnixMs = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Store (or replace) the document for one page uri.</summary>
    /// <param name="pageUri">The <c>wavee:module:&lt;id&gt;:&lt;b64(entityId)&gt;</c> uri.</param>
    /// <param name="doc">The module's answer.</param>
    public void Put(string pageUri, ModulePageDoc doc)
    {
        if (string.IsNullOrEmpty(pageUri)) return;
        ArgumentNullException.ThrowIfNull(doc);
        // A module that states an expiry owns it; one that says nothing gets the default TTL rather than an immortal
        // entry — a page carries view counts and "live now" rows, which are exactly the facts that rot.
        long expires = doc.ExpiresAtUnixMs is { } exp && exp > 0 ? exp : _nowUnixMs() + DefaultTtlMs;
        _entries[pageUri] = new Entry(doc, expires);
    }

    /// <summary>The unexpired document for this uri, or null.</summary>
    /// <param name="pageUri">The page uri.</param>
    public ModulePageDoc? Get(string? pageUri)
    {
        if (pageUri is not { Length: > 0 }) return null;
        if (!_entries.TryGetValue(pageUri, out Entry e)) return null;
        return _nowUnixMs() >= e.ExpiresAtUnixMs ? null : e.Doc;
    }

    /// <summary>Is there an entry for this uri that has passed its expiry?</summary>
    /// <param name="pageUri">The page uri.</param>
    public bool IsExpired(string? pageUri)
        => pageUri is { Length: > 0 } && _entries.TryGetValue(pageUri, out Entry e) && _nowUnixMs() >= e.ExpiresAtUnixMs;

    /// <summary>Drop one entry (a forced refresh).</summary>
    /// <param name="pageUri">The page uri.</param>
    public void Invalidate(string? pageUri)
    {
        if (pageUri is { Length: > 0 }) _entries.TryRemove(pageUri, out _);
    }

    /// <summary>Drop every page belonging to one module (it crashed, or was stopped).</summary>
    /// <param name="moduleId">The module id.</param>
    public void InvalidateModule(string moduleId)
    {
        string prefix = ModuleUri.Prefix(moduleId);
        foreach (KeyValuePair<string, Entry> e in _entries)
            if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(e.Key, out _);
    }

    /// <summary>How many entries are held (diagnostics).</summary>
    public int Count => _entries.Count;
}

/// <summary>
/// The PROCESS-WIDE module-page answers — the <see cref="ModulePlayables"/> pattern. Surfaces that must answer
/// synchronously on a render/decision path (the player bar's identity cluster, the stage's meta link) read these and
/// nothing else; the composition root attaches the cache once and never re-points it.
/// </summary>
public static class ModulePages
{
    /// <summary>The app route FAMILY a module page lives in: <c>module:&lt;wavee:module:…&gt;</c>. Same shape as
    /// <c>album:spotify:album:…</c> — a family prefix in front of the entity uri, so one route key carries both the
    /// owning module and the module-private entity id with nothing to look up.</summary>
    public const string RoutePrefix = "module:";

    /// <summary>The manifest capability a module declares to answer <c>module/page</c>. Capabilities are DECLARED,
    /// never probed (the composition rule); the token is spelled ONCE, here, beside the surface that needs it.</summary>
    public const string PagesCapability = "pages";

    static ModulePageCache? _cache;

    /// <summary>Attach the cache (composition root). Null detaches — every answer is then "no page", which is exactly
    /// what a build with no module host can honestly say.</summary>
    /// <param name="cache">The cache owned by the <see cref="ModuleHost"/>, or null.</param>
    public static void Attach(ModulePageCache? cache) => _cache = cache;

    /// <summary>The attached cache, for callers that need more than the lookups below.</summary>
    public static ModulePageCache? Cache => _cache;

    /// <summary>The cached document for one page uri, or null.</summary>
    /// <param name="pageUri">The <c>wavee:module:</c> page uri.</param>
    public static ModulePageDoc? Get(string? pageUri) => _cache?.Get(pageUri);

    // ── the route algebra ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is this route key a module page?</summary>
    /// <param name="routeKey">The route key.</param>
    public static bool IsRoute(string? routeKey)
        => routeKey is { Length: > 0 } && routeKey.Length > RoutePrefix.Length
           && routeKey.StartsWith(RoutePrefix, StringComparison.Ordinal);

    /// <summary>The page uri a route key addresses, or null when the key is not a module page route.</summary>
    /// <param name="routeKey">The route key.</param>
    public static string? UriOf(string? routeKey)
        => IsRoute(routeKey) ? routeKey![RoutePrefix.Length..] : null;

    /// <summary>Split a module page ROUTE into its module id and module-private entity id.</summary>
    /// <param name="routeKey">The route key.</param>
    /// <param name="moduleId">Receives the module id, or the empty string.</param>
    /// <param name="entityId">Receives the module-private entity id, or the empty string.</param>
    /// <returns>True when the key is a well-formed module page route.</returns>
    public static bool TryParseRoute(string? routeKey, out string moduleId, out string entityId)
    {
        moduleId = string.Empty;
        entityId = string.Empty;
        return UriOf(routeKey) is { } uri && ModuleUri.TryDecode(uri, out moduleId, out entityId);
    }

    /// <summary>The route key for one module-namespaced entity id (<c>video:abc</c>, <c>channel:UC…</c>,
    /// <c>station:https://…</c>). Null when either half is missing — a link with nowhere to go must be INERT, not a
    /// route that paints as the "Your Library" fallback.</summary>
    /// <param name="moduleId">The owning module's id.</param>
    /// <param name="entityId">The module-private entity id.</param>
    public static string? RouteForEntity(string? moduleId, string? entityId)
        => moduleId is { Length: > 0 } && entityId is { Length: > 0 }
            ? RoutePrefix + ModuleUri.Encode(moduleId, entityId)
            : null;

    /// <summary>The route key a MODULE playable's identity cluster links to, from the resolve cache. Null for every
    /// non-module uri, and null when the module stated no page for that slot (so the caller leaves the span inert).</summary>
    /// <param name="playableUri">The playable uri (any uri; non-module ones answer null).</param>
    /// <param name="slot">Which part of the identity cluster was clicked.</param>
    public static string? RouteFor(string? playableUri, LinkSlot slot)
    {
        if (!ModuleUri.TryDecode(playableUri, out string moduleId, out _)) return null;
        if (ModulePlayables.Get(playableUri) is not { } resolved) return null;
        // Art and Title both stand for the PLAYABLE, so they open its own page; the subtitle stands for whoever made
        // it (a channel, a station), which is a different entity and therefore a different id.
        string? entityId = slot == LinkSlot.Artist ? resolved.SubtitleEntityId : resolved.PageEntityId;
        return RouteForEntity(moduleId, entityId);
    }

    /// <summary>The route key a module TRACK's identity cluster links to. Null for a Spotify/local track.</summary>
    /// <param name="track">The track.</param>
    /// <param name="slot">Which part of the identity cluster was clicked.</param>
    public static string? RouteFor(Track? track, LinkSlot slot)
        => track is null ? null : RouteFor(track.Uri, slot);
}
