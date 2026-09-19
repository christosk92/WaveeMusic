using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Metadata;

namespace Wavee.Backend.Persistence;

// ── STEP 4 — the durable (cold) tier seam ────────────────────────────────────────────────────────────────────────────
// The persistent source of truth (offline-first). SQLite is the production impl; a memory fake backs unit tests.
//
// SINCE THE v5 REDESIGN THE COLD TIER IS THE SOURCE OF TRUTH AND THE IN-MEMORY TIER IS A BOUNDED CACHE OVER IT — the
// startup bulk-load is GONE. CachedStore's ctor reads only the IDENTITY tier, a background warm pass replays the
// bounded pin head-set, and every hot miss cold-falls-back through <see cref="IColdStore.GetEntity"/>. Nothing may
// assume full residency (that is why the offline search binds to LoadLibraryCandidates, not to the resident graph).
// Mutations still dual-write back, write-behind.

public readonly record struct ColdEntity(string Uri, EntityKind Kind, byte[] Payload);
public readonly record struct ColdExtension(
    string EntityUri,
    int ExtensionKind,
    byte[]? Payload,
    string? Etag,
    long OfflineTtlSeconds,
    bool Missing,
    long ExpiresAtUnixSeconds,
    long UpdatedAtUnixSeconds);
/// <summary>One persisted video↔audio association blob (JSON of <c>VideoAssociation</c>), keyed by entity uri in its
/// OWN table (it shares the track uri, so it can't live in the entity store).</summary>
public readonly record struct ColdVideoAssoc(string Uri, byte[] Payload);
public readonly record struct ColdSaved(string SetId, string Uri, SyncState Sync, long AddedAtMs = 0);
/// <summary>One ordered playlist-membership row: the stable per-row <paramref name="ItemId"/> (survives reorder),
/// the referenced entity <paramref name="ItemUri"/>, and the per-membership add facts. <paramref name="ChartStatus"/>/
/// <paramref name="ChartCurrentPos"/>/<paramref name="ChartPreviousPos"/>/<paramref name="ChartRank"/> are the chart
/// rank-movement scalars (<see cref="Wavee.Core.ChartEntryStatus"/> as a byte; all zero = no chart facts), so a cold-opened
/// chart playlist shows its arrows before the live re-fetch lands.</summary>
public readonly record struct ColdPlaylistItem(string ItemId, string ItemUri, string? AddedBy, long AddedAt,
    byte ChartStatus = 0, int ChartCurrentPos = 0, int ChartPreviousPos = 0, long ChartRank = 0);
/// <summary>One rootlist row: a playlist uri or a start/end-group marker. <paramref name="Kind"/> 0=item, 1=start-group, 2=end-group.</summary>
public readonly record struct ColdRootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth, long AddedAtMs = 0);

/// <summary>One recently-opened detail surface (schema v5, `recent_surfaces`). A pin REASON: the newest 50 opened
/// surfaces are exempt from the cache-tier TTL/budget so a restart repaints them offline.</summary>
public readonly record struct ColdRecentSurface(string Uri, int Kind, long LastOpenedUnixSeconds);

/// <summary>The fat artist facets split out of the Artist record (schema v5, `artist_overview`). <paramref name="Payload"/>
/// is the DECODED raw JSON (the store owns the fmt framing). Its own TTL: disposable, re-derived by an ArtistV4 SWR pass.</summary>
public readonly record struct ColdArtistOverview(string Uri, string Locale, byte[] Payload, long FetchedAtUnixSeconds);

/// <summary>One cheap cache-tier diagnostics snapshot (§G metrics) — the Settings → Storage readout and the GC report.
/// <paramref name="DbBytes"/>/<paramref name="ReclaimableBytes"/> are the whole file (identity tables included);
/// <paramref name="CacheBytes"/>/<paramref name="PinnedBytes"/> are the cache tier only.
/// <paramref name="EntityBytes"/> is SUM(size) over `entity` alone, so <see cref="EvictableBytes"/> — what the byte
/// budget actually governs (Wave F / K1) — is the part of the cache tier a GC pass is allowed to reclaim.</summary>
public readonly record struct EntityCacheStats(
    long DbBytes, long ReclaimableBytes, long CacheBytes, long PinnedBytes, long BudgetBytes,
    long EntityBytes, long EntityRows, long PinnedRows, long OverviewRows, long ExtensionRows)
{
    /// <summary>The cache bytes the budget sweep can actually reclaim: unpinned `entity` rows. (Slightly conservative
    /// versus a live pass — this uses the direct pin union without the one-level `entity_refs` closure.)</summary>
    public long EvictableBytes => Math.Max(0, EntityBytes - PinnedBytes);
}

public interface IColdStore : IDisposable
{
    IEnumerable<ColdEntity> LoadAllEntities();

    /// <summary>Load ONE persisted entity by uri, or null if the cold tier has no row for it. The rehydration seam for
    /// CachedStore's cold-fallback reads (a hot miss after an entity eviction). The default is a linear scan of
    /// <see cref="LoadAllEntities"/> (fine for the in-memory test fake); SQLite overrides it with an indexed single-row
    /// lookup. Sees only committed rows — an entity still queued in the write-behind lane may be missed, which is safe
    /// here: eviction only reaches entities resident long enough for the lane to have drained.</summary>
    ColdEntity? GetEntity(string uri)
    {
        foreach (var e in LoadAllEntities()) if (e.Uri == uri) return e;
        return null;
    }

    /// <summary>Load a BATCH of persisted entities by uri (the Wave-B warm set: saved sets ∪ recent surfaces ∪ rootlist
    /// headers). The default walks <see cref="GetEntity"/> per uri (fine for the in-memory test fake); SQLite overrides it
    /// with chunked <c>IN (…)</c> reads on the read connection. Missing uris are simply absent from the result.</summary>
    IEnumerable<ColdEntity> LoadEntities(IReadOnlyCollection<string> uris)
    {
        var list = new List<ColdEntity>(uris.Count);
        foreach (var uri in uris) if (GetEntity(uri) is { } e) list.Add(e);
        return list;
    }

    IEnumerable<ColdSaved> LoadAllSaved();   // unordered library-set membership (collection_items), per active account
    void UpsertEntity(string uri, EntityKind kind, byte[] payload);   // non-blocking (write-behind)
    void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0);   // 0 = preserve stored added_at

    // Video↔audio associations: their own keyed-by-uri table (the file-id map survives restarts). Write-behind like entities.
    IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations();
    void UpsertVideoAssociation(string uri, byte[] payload);   // non-blocking (write-behind)

    // User-attached local video overrides (`video_override`, schema v4): typed columns rather than a payload blob, because
    // the roster UI queries them by field. Deliberately NOT folded into video_assoc — Spotify's 6-hourly revalidation
    // full-replaces those rows and would erase the user's curation. The defaults keep pre-v4 / in-memory fakes compiling
    // (an override-free cold tier is simply an empty roster).
    IEnumerable<VideoOverride> LoadAllVideoOverrides() => Array.Empty<VideoOverride>();
    void UpsertVideoOverride(VideoOverride o) { }        // non-blocking (write-behind)
    void DeleteVideoOverride(string uri) { }             // non-blocking (write-behind)

    // Per-set sync token (the opaque collection delta cursor / playlist-style revision). null = never synced.
    string? GetCollectionRevision(string setId);
    void SetCollectionRevision(string setId, string? revision, long syncedAt);   // non-blocking (write-behind, ordered after its items)

    // The opaque rootlist revision (the rootlist's playlist-style base revision), stored in meta(key='rootlist_rev').
    // null = never synced / cleared. Synchronous + atomic (a coarse op, like the rootlist replace itself).
    byte[]? GetRootlistRevision();
    void SetRootlistRevision(byte[]? rev);

    // Ordered playlist membership + the opaque playlist revision. Replace is synchronous + atomic (bulk delete+insert+rev in one tx).
    IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri);
    void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev);
    byte[]? GetPlaylistRevision(string playlistUri);

    // The rootlist (flat ordered marker stream → tree at read). Replace is synchronous + atomic.
    IReadOnlyList<ColdRootlistEntry> LoadRootlist();
    void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries);

    // ── schema-v5 cache-tier surfaces (defaults keep the in-memory fakes compiling) ──────────────────────────────────
    /// <summary>The recently-opened detail surfaces — a PIN REASON the write gate mirrors in memory.</summary>
    IReadOnlyList<ColdRecentSurface> LoadRecentSurfaces() => Array.Empty<ColdRecentSurface>();
    /// <summary>Record a detail-page open (LRU-capped at 50 rows by the implementation).</summary>
    void UpsertRecentSurface(string uri, int kind, long nowUnixSeconds) { }
    /// <summary>The fat artist facets split out of the Artist record at persist time (decoded JSON), or null.</summary>
    ColdArtistOverview? GetArtistOverview(string uri) => null;
    /// <summary>Persist the artist overview facets. <paramref name="locale"/> "" ⇒ the store's own launch locale.</summary>
    void UpsertArtistOverview(string uri, string locale, byte[] payloadJson, long nowUnixSeconds) { }
    /// <summary>Replace the pin-closure edges out of <paramref name="parentUri"/> (album→artists, artist→albums).</summary>
    void ReplaceEntityRefs(string parentUri, IEnumerable<string> children) { }
    /// <summary>Batched last-access stamp at DAY granularity (§C.5 — the read path never writes; CachedStore accumulates
    /// touched uris and flushes them here on the writer lane every 60 s and at every GC). <paramref name="day"/> is a
    /// midnight-truncated unix-seconds value, so it compares directly against the TTL cutoffs.</summary>
    void TouchEntities(IReadOnlyCollection<string> uris, long day) { }
    /// <summary>Escape hatch (§G): drop every UNPINNED cache row + all artist overviews + all extension rows. Identity
    /// tables are never touched. Returns (rows, bytes) freed.</summary>
    (int Rows, long Bytes) ClearMetadataCache() => (0, 0);

    /// <summary>The offline-search candidate corpus for one scope (Addendum A4): THIN rows (no payload) joined out of the
    /// identity tables + `entity_refs`, so the library search is correct for a cold-but-not-resident library. The default
    /// answers "no SQL corpus" — the in-memory test fakes then fall through to the resident-graph walk unchanged.</summary>
    ColdCandidates LoadLibraryCandidates(ColdCandidateScope scope) => ColdCandidates.Empty;

    void Flush();   // block until queued writes are durable
}

/// <summary>Locale-scoped raw extended-metadata cache. Implementations are bound to one launch locale so ETags and
/// payloads can never be read across languages.</summary>
public interface IExtensionCacheStore
{
    string? MetadataLocale { get; }

    /// <summary>POINT-READ the persisted rows for a screenful of uris at one extension kind. This is the ONLY read the
    /// live path uses: it rides the (entity_uri, locale, extension_kind) primary key, so it is O(log n) per uri and its
    /// cost is the working set, never the table. Rows are returned in no particular order and missing keys are simply
    /// absent. An EXPIRED row is still returned — its ETag is what turns the follow-up fetch into a 304.</summary>
    IReadOnlyList<ColdExtension> LoadExtensions(IReadOnlyCollection<string> uris, int extensionKind);

    /// <summary>Stamp last_access (v7 LRU) for rows that were just SERVED from disk, at day granularity. The read path
    /// never writes — the caller batches and calls this — and a row already stamped today is a no-op.</summary>
    void TouchExtensions(IReadOnlyCollection<string> uris, int extensionKind, long day) { }

    /// <summary>Newest-first extension rows, capped at <paramref name="limit"/>. <c>limit &lt;= 0</c> = no cap.
    /// NOT on the live path — `WHERE locale=? ORDER BY updated_at DESC` has no index to serve it, so it is a full SCAN
    /// plus a TEMP B-TREE sort over every row (34 MB read to keep 636 KB, measured). Bulk-seeding a bounded LRU from it
    /// was the go-live stall; <see cref="LoadExtensions"/> replaced it. Retained for tests and offline tooling only.</summary>
    IEnumerable<ColdExtension> LoadAllExtensions(int limit = 2048);
    void UpsertExtension(ColdExtension extension);
}
