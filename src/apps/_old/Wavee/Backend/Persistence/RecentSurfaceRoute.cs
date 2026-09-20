using System;
using Wavee.Backend.Metadata;

namespace Wavee.Backend.Persistence;

// ── which navigations are a `recent_surfaces` PIN REASON (Addendum A5) ────────────────────────────────────────────────
// Opening a detail surface is one of the §A.3 pin reasons: the newest 50 opened surfaces are exempt from the cache-tier
// TTL/budget, so a restart repaints them offline. This is the one place that decides which route names qualify.
//
// It is a NARROWER question than the shell's page routing, which is why it is its own function rather than a reuse of
// `DetailPage.ParseDetail` / `ArtistPage.UriOf` / `ContentHost.IsDetail` (those decide WHICH PAGE renders and must map
// every route, including the two that pin nothing):
//   • `liked` is a SET, not an entity — it is pinned by `collection_items` itself and has no entity row to keep;
//   • `local` resolves to the synthetic uri `wavee:local:all`, which no cold row and no pin table ever contains.
// Recording either would burn one of the 50 LRU slots on a row that can never be read back. Everything else the detail
// surfaces render (`album:` / `pl:` / `show:` / `artist:`) is a real entity uri and is recorded.
//   • `prerelease:` (an upcoming release, which renders the ordinary album page) is deliberately left unclassified: the
//     ALBUM uri is the thing worth pinning, and it is pinned the moment the user reaches the `album:` route — pinning
//     the prerelease uri too would spend a second LRU slot on an entity the cold store has no row for.
//
// Lives in Backend (not Features) because it is pure route→entity mapping with no UI dependency — which is also what
// makes it unit-testable without the shell.
public static class RecentSurfaceRoute
{
    /// <summary>Classify a route NAME (the <c>Route.Name</c> the shell writes) into the (uri, kind) pair
    /// <see cref="CachedStore.RecordRecentSurface"/> stores. False for every route that pins nothing.</summary>
    public static bool TryClassify(string? routeName, out string uri, out EntityKind kind)
    {
        if (Take(routeName, "album:", out uri)) { kind = EntityKind.Album; return true; }
        if (Take(routeName, "pl:", out uri)) { kind = EntityKind.Playlist; return true; }
        if (Take(routeName, "artist:", out uri)) { kind = EntityKind.Artist; return true; }
        if (Take(routeName, "show:", out uri)) { kind = EntityKind.Show; return true; }
        kind = EntityKind.Unknown;
        return false;
    }

    static bool Take(string? routeName, string prefix, out string uri)
    {
        if (routeName is not null && routeName.Length > prefix.Length && routeName.StartsWith(prefix, StringComparison.Ordinal))
        {
            uri = routeName[prefix.Length..];
            return uri.Length > 0;
        }
        uri = "";
        return false;
    }
}
