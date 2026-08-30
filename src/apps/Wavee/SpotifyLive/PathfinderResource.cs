using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

public readonly record struct PathfinderKey(
    string Locale,
    string OperationName,
    string Sha256Hash,
    string BodyHash,
    PathfinderClient.Platform Platform);

public sealed class PathfinderResource : IConcertPathfinder
{
    readonly PathfinderClient _client;
    readonly Resource<PathfinderKey, CachedJson> _resource;
    readonly ConcurrentDictionary<PathfinderKey, byte[]> _bodies = new();
    readonly Func<SessionContext> _ctx;

    readonly record struct CachedJson(byte[] Bytes, TimeSpan Ttl);

    public PathfinderResource(PathfinderClient client, Func<SessionContext> ctx, WaveeLogger log = default, int maxEntries = 128)
    {
        _client = client;
        _ctx = ctx;
        _resource = new Resource<PathfinderKey, CachedJson>(
            FetchAsync,
            new FreshnessPolicy.PollWhole(TimeSpan.FromMinutes(15), SuspendInPlayback: false),
            ctx,
            ttlOf: x => x.Ttl,
            maxEntries: maxEntries,
            name: "pathfinder",
            debugLog: log);
    }

    public int FetchCount => _resource.FetchCount;
    public string Locale => SpotifyHeaders.NormalizeLanguage(_ctx().Locale);

    /// <summary>Resident request-body count. Test/diagnostic visibility for the cache-HIT body-cleanup contract (a fresh
    /// hit must not strand its request blob in <c>_bodies</c>).</summary>
    internal int PendingBodyCount => _bodies.Count;

    public async Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var bytes = await GetBytesAsync(operationName, sha256Hash, writeVariables, platform, ct, ttl).ConfigureAwait(false);
        return bytes is null ? null : JsonDocument.Parse(bytes);
    }

    public async Task<JsonDocument?> UseQueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var ttlValue = ttl ?? TtlFor(operationName);
        if (ttlValue <= TimeSpan.Zero)
            return await QueryAsync(operationName, sha256Hash, writeVariables, platform, ct, ttlValue).ConfigureAwait(false);

        var key = BuildKey(SpotifyHeaders.NormalizeLanguage(_ctx().Locale), operationName, sha256Hash, writeVariables, platform, out var body);
        _bodies[key] = body;
        var loaded = _resource.Use(key);
        if (loaded.IsReady && loaded.Value is { } cached)
        {
            // Cache HIT: FetchAsync removes the body only on a fetch. A FRESH hit runs no fetch, so drop the body here or
            // it leaks one request blob per distinct key forever. A STALE hit's background revalidation still needs it (its
            // FetchAsync finally removes it), so leave that case alone.
            if (!loaded.IsStale) _bodies.TryRemove(key, out _);
            return JsonDocument.Parse(cached.Bytes);
        }

        return await QueryAsync(operationName, sha256Hash, writeVariables, platform, ct, ttlValue).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetBytesAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop,
        CancellationToken ct = default, TimeSpan? ttl = null)
    {
        var ttlValue = ttl ?? TtlFor(operationName);
        var key = BuildKey(SpotifyHeaders.NormalizeLanguage(_ctx().Locale), operationName, sha256Hash, writeVariables, platform, out var body);

        if (ttlValue <= TimeSpan.Zero)
            return await _client.QueryBodyBytesAsync(operationName, body, platform, ct).ConfigureAwait(false);

        _bodies[key] = body;
        var loaded = await _resource.GetAsync(key, ct).ConfigureAwait(false);
        // The awaited fetch (if any) already consumed + removed the body in its finally; on a fresh cache HIT no fetch ran,
        // so this removal is what plugs that leak. Idempotent after a fetch (TryRemove no-ops).
        _bodies.TryRemove(key, out _);
        return loaded.IsReady ? loaded.Value.Bytes : null;
    }

    /// <summary>Drop one exact operation/body/locale cache entry so the next query performs a network read. The
    /// variables are serialized through the same key builder as <see cref="UseQueryAsync"/>; callers therefore cannot
    /// accidentally invalidate a neighbouring facet or platform entry.</summary>
    public void Invalidate(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform = PathfinderClient.Platform.Desktop)
    {
        var key = BuildKey(SpotifyHeaders.NormalizeLanguage(_ctx().Locale), operationName, sha256Hash,
            writeVariables, platform, out _);
        _resource.Invalidate(key);
    }

    async Task<CachedJson> FetchAsync(PathfinderKey key, SessionContext ctx)
    {
        _ = ctx;
        if (!_bodies.TryGetValue(key, out var body))
            throw new InvalidOperationException("missing cached Pathfinder request body");

        try
        {
            var bytes = await _client.QueryBodyBytesAsync(key.OperationName, body, key.Platform, CancellationToken.None)
                .ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException("Pathfinder returned no payload for " + key.OperationName);
            return new CachedJson(bytes, TtlFor(key.OperationName));
        }
        finally
        {
            _bodies.TryRemove(key, out _);
        }
    }

    static PathfinderKey BuildKey(string locale, string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, PathfinderClient.Platform platform, out byte[] body)
    {
        body = PathfinderClient.BuildBody(operationName, sha256Hash, writeVariables);
        var hash = Convert.ToHexString(SHA256.HashData(body));
        return new PathfinderKey(locale, operationName, sha256Hash, hash, platform);
    }

    public static TimeSpan TtlFor(string operationName) => operationName switch
    {
        PathfinderOps.Home => TimeSpan.FromMinutes(15),
        // Same document, same freshness: a section page is a WINDOW onto the very feed `home` just returned, so letting
        // it fall through to the 10-minute default would make one Home turn over faster than the other. Explicit, not
        // defaulted — the two must move together whenever either is retuned.
        PathfinderOps.HomeSection => TimeSpan.FromMinutes(15),
        PathfinderOps.GetAlbum => TimeSpan.FromMinutes(10),
        PathfinderOps.GetTrack => TimeSpan.FromMinutes(10),
        PathfinderOps.SimilarAlbumsBasedOnThisTrack => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryAlbumMerch => TimeSpan.FromHours(1),
        PathfinderOps.QueryNpvArtist => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryArtistOverview => TimeSpan.FromMinutes(30),
        PathfinderOps.QueryWhatsNewFeed => TimeSpan.FromMinutes(5),
        // Affinity over a 4-week window: it cannot meaningfully change within a session, so this is the transport cache
        // that keeps Home's top-artist row from re-asking on every navigation back.
        PathfinderOps.UserTopContent => TimeSpan.FromMinutes(30),
        // Browse is editorial and turns over on a daily-ish cadence, so it caches hard. browseAll especially: it is the
        // directory backing a whole tab, and re-fetching 70 categories on every open would be pure waste.
        PathfinderOps.BrowseAll => TimeSpan.FromHours(6),
        PathfinderOps.BrowsePage => TimeSpan.FromMinutes(30),
        PathfinderOps.BrowseSection => TimeSpan.FromMinutes(30),
        // NOTE: this prefix rule would otherwise swallow searchAll-style names AND browse* is unaffected, but the
        // browse cases above must stay ABOVE it if either is ever renamed to start with "search".
        _ when operationName.StartsWith("search", StringComparison.OrdinalIgnoreCase) => TimeSpan.Zero,
        _ => TimeSpan.FromMinutes(10),
    };
}
