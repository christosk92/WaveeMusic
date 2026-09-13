using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

public readonly record struct ExtensionKey(string Locale, string Uri, Xm.ExtensionKind Kind);

public sealed record CachedExtension(
    string Uri,
    Xm.ExtensionKind Kind,
    string? Etag,
    long OfflineTtlSeconds,
    ByteString? Payload,
    bool Missing)
{
    static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);
    static readonly TimeSpan MissingTtl = TimeSpan.FromHours(24);

    public TimeSpan Ttl => Missing
        ? MissingTtl
        : OfflineTtlSeconds > 0 ? TimeSpan.FromSeconds(Math.Clamp(OfflineTtlSeconds, 60, 86_400)) : DefaultTtl;

    public static CachedExtension MissingValue(string uri, Xm.ExtensionKind kind, string? etag = null, long offlineTtlSeconds = 0)
        => new(uri, kind, etag, offlineTtlSeconds, null, Missing: true);
}

public sealed class ExtensionEtagCache
{
    readonly ExtendedMetadataSource _source;
    readonly Resource<ExtensionKey, CachedExtension> _resource;
    readonly SemaphoreSlim _batchGate = new(1, 1);
    readonly IExtensionCacheStore? _persistent;
    readonly string _locale;

    public ExtensionEtagCache(ExtendedMetadataSource source, Func<SessionContext> ctx, WaveeLogger log = default, int maxEntries = 2048,
        IExtensionCacheStore? persistent = null)
    {
        _source = source;
        _locale = SpotifyHeaders.NormalizeLanguage(ctx().Locale);
        _persistent = persistent is not null && string.Equals(persistent.MetadataLocale, _locale, StringComparison.OrdinalIgnoreCase)
            ? persistent : null;
        _resource = new Resource<ExtensionKey, CachedExtension>(
            async (key, _) =>
            {
                var fetched = await FetchBatchAsync(new[] { key }, CancellationToken.None).ConfigureAwait(false);
                // Absent-from-response is NOT a Missing outcome — inventing one would seal a 24h negative and wedge
                // recovery (S0). Fail the single-key fetch so Resource records an error instead of a fake miss.
                if (!fetched.TryGetValue(key, out var value))
                    throw new InvalidOperationException("extended-metadata omitted " + key.Uri);
                return value;
            },
            new FreshnessPolicy.Etag(TimeSpan.FromHours(6)),
            ctx,
            ttlOf: x => x.Ttl,
            maxEntries: maxEntries,
            name: "extended-metadata",
            debugLog: log);

        // NO bulk seed here — the ctor is O(1) and this runs on the go-live critical path. The cold tier is read
        // LAZILY, per miss, through HydrateFromCold. The old seed pulled the newest `maxEntries` rows out of
        // `localized_extension_cache` at construction, which was both the login stall (an unindexed
        // `ORDER BY updated_at DESC` = full SCAN + TEMP B-TREE; 34 MB read to keep 636 KB, measured) and a
        // correctness ceiling: only those 2048 rows were EVER readable, so ~97% of the persisted cache was
        // write-only and every browse outside the window re-fetched a payload that was already on disk.
    }

    /// <summary>Tier 2 — fold the persisted rows for the current misses into the LRU BEFORE touching the network.
    /// Point-read by primary key, so this is O(misses · log n) and its cost is the working set, never the table.
    /// An unexpired row satisfies the request outright; an expired one still lands, because its ETag is what turns
    /// the follow-up fetch into a 304 instead of a full body.</summary>
    void HydrateFromCold(List<ExtensionKey> misses)
    {
        if (_persistent is null || misses.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // One statement per distinct kind; a screenful is normally one or two kinds.
        var byKind = new Dictionary<int, List<string>>();
        foreach (var key in misses)
        {
            int kind = (int)key.Kind;
            if (!byKind.TryGetValue(kind, out var uris)) byKind[kind] = uris = new List<string>();
            uris.Add(key.Uri);
        }
        foreach (var (kind, uris) in byKind)
        {
            IReadOnlyList<ColdExtension> rows;
            // A cold-tier failure must never break the live fetch — fall through to the network.
            try { rows = _persistent.LoadExtensions(uris, kind); }
            catch (Exception) { continue; }
            // Stamp what we actually served so the v7 LRU sweep evicts by USE, not by write time. Debounced on a plain
            // tick check rather than a timer: this runs inside _batchGate on a pool thread, and a day-granularity guarded
            // UPDATE is a no-op for rows already touched today — so there is no lifetime to own and nothing to dispose.
            if (rows.Count > 0) TouchServed(kind, rows);
            foreach (var row in rows)
            {
                var key = new ExtensionKey(_locale, row.EntityUri, (Xm.ExtensionKind)row.ExtensionKind);
                var value = new CachedExtension(row.EntityUri, (Xm.ExtensionKind)row.ExtensionKind,
                    row.Missing ? null : row.Etag,
                    row.OfflineTtlSeconds, row.Payload is { Length: > 0 } bytes ? ByteString.CopyFrom(bytes) : null, row.Missing);
                // SeedPersisted, not Seed: a fetch that already landed must win, and a dealer MarkStale that arrived
                // while this read was in flight must survive it.
                _resource.SeedPersisted(key, value,
                    DateTimeOffset.FromUnixTimeSeconds(row.UpdatedAtUnixSeconds).UtcDateTime,
                    DateTimeOffset.FromUnixTimeSeconds(row.ExpiresAtUnixSeconds).UtcDateTime,
                    needsRevalidate: row.ExpiresAtUnixSeconds <= now);
            }
        }
    }

    // Pending last_access stamps, flushed when they get big enough or old enough. Guarded by _batchGate (its only caller
    // is HydrateFromCold, which always runs inside it), so no extra lock.
    readonly Dictionary<int, List<string>> _touchPending = new();
    int _touchCount;
    long _touchFlushedAt = Environment.TickCount64;
    const int TouchFlushRows = 512;
    const long TouchFlushIntervalMs = 60_000;

    void TouchServed(int kind, IReadOnlyList<ColdExtension> rows)
    {
        if (!_touchPending.TryGetValue(kind, out var pending)) _touchPending[kind] = pending = new List<string>();
        foreach (var row in rows) { pending.Add(row.EntityUri); _touchCount++; }
        long now = Environment.TickCount64;
        if (_touchCount < TouchFlushRows && now - _touchFlushedAt < TouchFlushIntervalMs) return;
        FlushTouches(now);
    }

    void FlushTouches(long now)
    {
        _touchFlushedAt = now;
        _touchCount = 0;
        if (_persistent is null) { _touchPending.Clear(); return; }
        // Midnight-truncated, matching the entity tier's TouchEntities convention so both compare against the same cutoffs.
        long day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        foreach (var (kind, uris) in _touchPending)
        {
            if (uris.Count == 0) continue;
            // Never let cache bookkeeping break a read.
            try { _persistent.TouchExtensions(uris, kind, day); } catch (Exception) { }
            uris.Clear();
        }
    }

    public int FetchCount => _resource.FetchCount;

    public void MarkStale(string uri, Xm.ExtensionKind kind)
        => _resource.MarkStale(new ExtensionKey(_locale, uri, kind));

    /// <param name="clientFeatureId">The per-surface <c>client-feature-id</c> attribution, forwarded to the fetch (the
    /// display-only <c>ExtensionReader</c> stamps it on EVERY arm, so the single-key read needs it too).</param>
    public async Task<ByteString?> GetPayloadAsync(string uri, Xm.ExtensionKind kind, CancellationToken ct = default,
        string? clientFeatureId = null)
    {
        var values = await GetAsync(new[] { (uri, kind) }, ct, clientFeatureId).ConfigureAwait(false);
        return values.TryGetValue((uri, kind), out var cached) && !cached.Missing ? cached.Payload : null;
    }

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>> GetPayloadsAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default)
    {
        var cached = await GetAsync(requests, ct).ConfigureAwait(false);
        var result = new Dictionary<(string, Xm.ExtensionKind), ByteString>(cached.Count);
        foreach (var (key, value) in cached)
            if (!value.Missing && value.Payload is { IsEmpty: false } payload)
                result[key] = payload;
        return result;
    }

    /// <param name="clientFeatureId">Optional <c>client-feature-id</c> attribution for whatever this call has to fetch.
    /// A cache/304 hit sends nothing, so the header only ever rides a real miss.</param>
    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> GetAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default,
        string? clientFeatureId = null)
    {
        if (requests.Count == 0)
            return new Dictionary<(string, Xm.ExtensionKind), CachedExtension>();

        var keys = Normalize(requests);
        var values = new Dictionary<(string, Xm.ExtensionKind), CachedExtension>(keys.Count);
        var misses = new List<ExtensionKey>();

        foreach (var key in keys)
        {
            var cached = _resource.Peek(key);
            if (cached.IsReady && !cached.IsStale && cached.Value is { } value)
            {
                values[(key.Uri, key.Kind)] = value;
                continue;
            }
            misses.Add(key);
        }

        if (misses.Count > 0)
        {
            await _batchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                misses.Clear();
                foreach (var key in keys)
                {
                    var cached = _resource.Peek(key);
                    if (cached.IsReady && !cached.IsStale && cached.Value is { } value)
                    {
                        values[(key.Uri, key.Kind)] = value;
                        continue;
                    }
                    misses.Add(key);
                }

                if (misses.Count > 0)
                {
                    // Consult the durable tier before the wire: a hit is one indexed lookup instead of an HTTP
                    // round-trip, and even a miss-with-ETag downgrades the fetch below to a 304.
                    HydrateFromCold(misses);
                    misses.Clear();
                    foreach (var key in keys)
                    {
                        var cached = _resource.Peek(key);
                        if (cached.IsReady && !cached.IsStale && cached.Value is { } value)
                        {
                            values[(key.Uri, key.Kind)] = value;
                            continue;
                        }
                        misses.Add(key);
                    }
                }

                if (misses.Count > 0)
                {
                    IReadOnlyDictionary<ExtensionKey, CachedExtension> fetched;
                    try { fetched = await FetchBatchAsync(misses, ct, clientFeatureId).ConfigureAwait(false); }
                    catch
                    {
                        // Offline/SWR: an expired exact-locale row remains usable. Never substitute another locale's raw
                        // extension or ETag; if any requested key has no stale value, preserve the original failure.
                        bool allHaveStale = true;
                        foreach (var key in misses) if (!_resource.Peek(key).IsReady) { allHaveStale = false; break; }
                        if (!allHaveStale) throw;
                        fetched = new Dictionary<ExtensionKey, CachedExtension>();
                    }
                    foreach (var key in misses)
                    {
                        // Only Seed explicit wire outcomes (keys present in `fetched`). Absent-from-response stays
                        // unsealed so the next hydrate retries; never invent a MissingValue here (that was the 24h wedge).
                        if (!fetched.TryGetValue(key, out var value))
                        {
                            if (_resource.Peek(key).Value is { } stale)
                                values[(key.Uri, key.Kind)] = stale;
                            continue;
                        }
                        _resource.Seed(key, value);
                        values[(key.Uri, key.Kind)] = value;
                    }
                }
            }
            finally
            {
                _batchGate.Release();
            }
        }

        return values;
    }

    async Task<IReadOnlyDictionary<ExtensionKey, CachedExtension>> FetchBatchAsync(
        IReadOnlyList<ExtensionKey> keys, CancellationToken ct, string? clientFeatureId = null)
    {
        var reqs = new (string Uri, Xm.ExtensionKind Kind, string? Etag)[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var cached = _resource.Peek(keys[i]);
            // Never conditional-GET a Missing row — its ETag (if any) would 304-forever the miss past TTL.
            string? etag = cached is { IsReady: true, Value: { Missing: false, Etag: { Length: > 0 } e } } ? e : null;
            reqs[i] = (keys[i].Uri, keys[i].Kind, etag);
        }

        var response = await _source.GetExtensionsWithHeadersAsync(reqs, ct, clientFeatureId).ConfigureAwait(false);
        var result = new Dictionary<ExtensionKey, CachedExtension>(keys.Count);
        foreach (var key in keys)
        {
            if (!response.TryGetValue((key.Uri, key.Kind), out var wire)) continue;   // absent — not an outcome
            if (wire.Status is not (200 or 304 or 404)) continue;
            var existing = _resource.Peek(key);
            var prior = existing.IsReady ? existing.Value : null;
            // 304 can only reconfirm a real payload; a Missing+ETag 304 must not reseal past TTL (need a full body).
            if (wire.Status == 304 && prior is not { Missing: false }) continue;
            var folded = Fold(key, prior, wire);
            result[key] = folded;
            Persist(folded);
        }
        return result;
    }

    void Persist(CachedExtension value)
    {
        if (_persistent is null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long ttl = Math.Max(1, (long)value.Ttl.TotalSeconds);
        // Never persist an ETag onto a Missing row — cold restore would otherwise 304-forever the miss.
        _persistent.UpsertExtension(new ColdExtension(value.Uri, (int)value.Kind,
            value.Payload is { IsEmpty: false } payload ? payload.ToByteArray() : null,
            value.Missing ? null : value.Etag, value.OfflineTtlSeconds, value.Missing, now + ttl, now));
    }

    static CachedExtension Fold(ExtensionKey key, CachedExtension? existing, ExtendedMetadataSource.ExtensionResult wire)
    {
        return wire.Status switch
        {
            200 when wire.Payload is { IsEmpty: false } payload =>
                new CachedExtension(key.Uri, key.Kind, wire.Etag ?? existing?.Etag, wire.OfflineTtlSeconds, payload, Missing: false),
            304 when existing is { Missing: false } =>
                existing with
                {
                    Etag = wire.Etag ?? existing.Etag,
                    OfflineTtlSeconds = wire.OfflineTtlSeconds > 0 ? wire.OfflineTtlSeconds : existing.OfflineTtlSeconds,
                },
            // Explicit negative outcomes: never adopt the wire ETag onto Missing (304-forever loop).
            404 => CachedExtension.MissingValue(key.Uri, key.Kind, etag: null, wire.OfflineTtlSeconds),
            200 => CachedExtension.MissingValue(key.Uri, key.Kind, etag: null, wire.OfflineTtlSeconds),
            _ when existing is not null => existing,
            _ => CachedExtension.MissingValue(key.Uri, key.Kind),
        };
    }

    List<ExtensionKey> Normalize(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests)
    {
        var keys = new List<ExtensionKey>(requests.Count);
        var seen = new HashSet<ExtensionKey>();
        foreach (var (uri, kind) in requests)
        {
            if (string.IsNullOrEmpty(uri) || kind == Xm.ExtensionKind.UnknownExtension) continue;
            var key = new ExtensionKey(_locale, uri, kind);
            if (seen.Add(key)) keys.Add(key);
        }
        return keys;
    }
}
