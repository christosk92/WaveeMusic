using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // disambiguate: Wavee.Core.EntityKind is the ROUTING vocabulary; the PERSISTED column is the transport one

namespace Wavee.Backend.Persistence;

// ── the durable, offline-first IStore (schema v5 contract) ───────────────────────────────────────────────────────────
// The cold tier is the SOURCE OF TRUTH; the hot tier (InMemoryStore) is a BOUNDED CACHE over it. That inversion is the
// whole redesign, and it shows up here as four behaviors:
//
//  1. DEFERRED CTOR — the ctor loads only the IDENTITY tier (saved sets, the user's video curation, the video↔audio map,
//     the recent-surface + rootlist pin mirrors). No entity replay: first paint no longer waits on the catalog.
//  2. WARM TASK — a background pass replays the bounded PIN HEAD-SET (saved uris ∪ recent surfaces ∪ rootlist playlist
//     headers) under one BeginBulk scope, then completes `WarmComplete`.
//  3. UNCONDITIONAL COLD FALLBACK — every hot miss probes cold (one indexed PK read). This is the correctness backbone
//     now that hot is never fully resident; the old `HasEvictedEntities` gate is gone.
//  4. PIN-REACHABILITY WRITE GATE — a cold row is written only for entities the pin set can reach (§A.4.1). Transient
//     queue/radio/browse hydration stays HOT-ONLY, which is what stops the disk growing monotonically. Pin TRANSITIONS
//     (like / add-to-playlist / attach-video) flush the hot record to cold on a background lane, so an entity that
//     becomes pinned after its last write can never be stranded off-disk (critique #1).
public sealed class CachedStore : IStore, ILibraryCandidateStore, IDisposable
{
    readonly InMemoryStore _hot = new();
    readonly IColdStore _cold;

    // WARM tier: resident playlist membership baselines are bounded by a byte budget AND a count cap. On overflow the
    // least-recently-used baseline is evicted from the resident mirror (it stays in the cold tier and rehydrates on next
    // access). ~40 B/item is the SoA membership estimate. (HOT pinning — open/outbox-pending — is a later refinement;
    // the open list is touched constantly so it stays MRU.)
    const int BytesPerMembershipItem = 40;
    readonly int _maxResidentPlaylists;
    readonly long _maxResidentBytes;
    readonly object _lruGate = new();
    readonly Dictionary<string, (long Tick, long Bytes)> _resident = new();
    long _residentTick;
    long _residentBytes;

    // ── pin mirrors (the in-memory half of the §A.3 pin set) ─────────────────────────────────────────────────────────
    // Everything the write gate consults must be O(1) and thread-safe: the gate runs on whichever thread projected the
    // entity, and it must NEVER call Services.BuildPinSet (that snapshot is UI-thread-affine and belongs to GC, Wave C).
    // `_memberUris` and `_pinReferenced` are ADD-ONLY: a stale entry can only make the gate more generous (one extra
    // cold row that the GC's SQL-derived pin set will reclaim), never less — it can never strand an entity off disk.
    const int PinMirrorCap = 200_000;
    readonly object _pinGate = new();
    readonly HashSet<string> _recentSurfaces = new(StringComparer.Ordinal);   // recent_surfaces mirror
    readonly HashSet<string> _rootlistUris = new(StringComparer.Ordinal);     // rootlist playlist uris
    readonly HashSet<string> _adopted = new(StringComparer.Ordinal);          // playlists with an adopted membership (`playlists`)
    readonly HashSet<string> _memberUris = new(StringComparer.Ordinal);       // ∪ of every adopted playlist membership
    readonly HashSet<string> _pinReferenced = new(StringComparer.Ordinal);    // closure: children of a persisted pinned parent

    // ── cold-presence + no-op elision (critique #1b) ─────────────────────────────────────────────────────────────────
    // ONE map does both jobs: the KEY set is "this uri has a row in cold" and the VALUE is the 64-bit hash of the bytes
    // we last wrote for it. Skip the cold upsert iff the hash is unchanged AND the uri is cold-present — a payload-only
    // hash would strand a pinned entity forever the first time its bytes happened to repeat (that is exactly the bug
    // the critique found). Seeded from the warm read, every successful persist, and every cold-fallback hit — so it is
    // always a SUBSET of the rows that actually exist on disk. Capped: on overflow it is cleared wholesale, which costs
    // one redundant re-write per uri and can never lose data.
    const int ColdPresenceCap = 250_000;
    readonly object _coldGate = new();
    readonly Dictionary<string, ulong> _coldRows = new(StringComparer.Ordinal);
    readonly Dictionary<string, ulong> _coldOverviews = new(StringComparer.Ordinal);

    // ── last-access touch tracking (§C.5 — last_access without a write per read) ──────────────────────────────────────
    // The read path NEVER writes. A hot hit or a cold-fallback hit records the uri in a lock-free pending set, and the
    // writer lane flushes it as one batched `UPDATE entity SET last_access=$day` every 60 s (and at every GC).
    // Granularity is a DAY (midnight-truncated unix seconds, so it compares directly against the TTL cutoffs), and
    // `_touchDay` remembers what was last recorded per uri — so the STEADY-STATE fast path (an entity already touched
    // today) is exactly one ConcurrentDictionary lookup: no lock, no allocation, no LINQ, no closure. That matters
    // because these calls sit on the scroll/render read path.
    const int TouchPendingCap = 4096;      // overflow ⇒ flush early rather than grow unbounded
    const int TouchDayCap = 250_000;       // the recorded-today map is cleared wholesale on overflow (costs one re-stamp)
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _touchDay = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _touchPending = new(StringComparer.Ordinal);
    int _touchPendingCount;
    readonly System.Threading.Timer _touchTimer;

    // ── the background lanes ─────────────────────────────────────────────────────────────────────────────────────────
    readonly Channels.Channel<FlushOp> _lane = Channels.Channel.CreateUnbounded<FlushOp>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _laneLoop;
    readonly Task _warm;
    readonly TaskCompletionSource _warmDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _coldNotFullyResident = true;
    long _warmMillis;
    int _warmRows;
    long _savedMillis, _videoAssocMillis;
    int _savedRows, _videoAssocRows;

    public CachedStore(IColdStore cold, int maxResidentPlaylists = 128, long maxResidentBytes = 24L * 1024 * 1024)
    {
        _cold = cold;
        _maxResidentPlaylists = maxResidentPlaylists;
        _maxResidentBytes = maxResidentBytes;
        // IDENTITY tier only — tiny, and the very first render reads it. NO entity replay (that is WarmAsync's job).
        // video_assoc stays EAGER (locked decision 8): GetVideoAssociation is a hot-only reader and the video-edge code
        // is fragile — a false "no video" during the warm window flips the placement machine at a track edge.
        // §G startup marks: these two loops are the only O(LIBRARY) work before first paint — video_assoc is a full-table
        // scan plus a per-row JSON decode, and LoadAllSaved reads EVERY collection_items row into two uncapped maps. They
        // used to be pooled into one opaque `boot.identity_load_ms`; split so a regression names itself.
        long t = System.Environment.TickCount64;
        foreach (var v in _cold.LoadAllVideoAssociations()) { ReplayVideo(v); _videoAssocRows++; }
        _videoAssocMillis = System.Environment.TickCount64 - t;
        foreach (var o in _cold.LoadAllVideoOverrides()) _hot.UpsertVideoOverride(o);          // the user's video curation
        t = System.Environment.TickCount64;
        foreach (var s in _cold.LoadAllSaved()) { _hot.SetSaved(s.SetId, s.Uri, true, s.Sync, s.AddedAtMs); _savedRows++; }
        _savedMillis = System.Environment.TickCount64 - t;
        // The two identity-tier pin mirrors. Both are uri-only scans of tiny tables (≤50 rows / the sidebar), and the
        // gate needs them from the FIRST write, so they cannot wait for the warm pass.
        foreach (var r in _cold.LoadRecentSurfaces()) _recentSurfaces.Add(r.Uri);
        foreach (var e in _cold.LoadRootlist()) if (e.Kind == 0 && e.Uri.Length > 0) _rootlistUris.Add(e.Uri);
        _laneLoop = Task.Run(LaneLoopAsync);
        _warm = Task.Run(Warm);
        // The last-access flush cadence (§C.5). The Timer callback only ENQUEUES onto the lane — the actual UPDATE takes
        // the cold store's writer lock, which must never happen on a timer thread pool slot held for a whole batch.
        _touchTimer = new System.Threading.Timer(static s => ((CachedStore)s!).RequestTouchFlush(), this, 60_000, 60_000);
    }

    /// <summary>Completes when the background warm pass has replayed the pin head-set (or failed — it NEVER wedges a
    /// consumer; a miss is served by the cold fallback). Consumers that need the warm set resident await this.</summary>
    public Task WarmComplete => _warmDone.Task;

    /// <summary>True until the warm pass lands: the hot tier is a strict subset of cold by construction, so a hot miss is
    /// never proof of absence. (The cold fallback is unconditional either way — this is a diagnostic/readiness bit.)</summary>
    public bool ColdNotFullyResident => _coldNotFullyResident;

    /// <summary>How long the background warm pass took, and how many rows it replayed (§G startup marks
    /// <c>boot.warm_ms</c> / <c>boot.warm_rows</c>). Valid once <see cref="WarmComplete"/> has run.</summary>
    public long WarmMillis => System.Threading.Volatile.Read(ref _warmMillis);
    public int WarmRows => System.Threading.Volatile.Read(ref _warmRows);

    /// <summary>The two O(library) legs of the eager identity tier, split out of <c>boot.identity_load_ms</c> (§G startup
    /// marks). Both are read-only after the ctor, so no volatile read is needed. Watch the ROW counts, not just the ms:
    /// they are what grows, and the ms only follows once the working set stops fitting in the page cache.</summary>
    public long SavedMillis => _savedMillis;
    public int SavedRows => _savedRows;
    public long VideoAssocMillis => _videoAssocMillis;
    public int VideoAssocRows => _videoAssocRows;

    public int ResidentMembershipCount { get { lock (_lruGate) return _resident.Count; } }
    public long ResidentMembershipBytes { get { lock (_lruGate) return _residentBytes; } }
    public long MaxResidentBytes => _maxResidentBytes;
    public int MaxResidentPlaylists => _maxResidentPlaylists;

    // ── entity residency + census (see InMemoryStore) — passthroughs to the hot mirror ───────────────────────────────
    public (int Tracks, int Albums, int Artists, int Playlists, int Shows, int Episodes, int Versions) EntityCounts => _hot.EntityCounts;
    public long EstimatedEntityBytes => _hot.EstimatedEntityBytes;
    public bool HasEvictedEntities => _hot.HasEvictedEntities;
    /// <summary>Governor arena (priority 3): evict oldest-first unpinned entities down to <paramref name="maxResident"/>,
    /// returning estimated bytes freed. The cold tier keeps every PINNED entity, so eviction is safe — a later read
    /// cold-falls-back (and an unpinned entity was memory-only by policy, so it re-fetches).</summary>
    public long ShedEntities(ISet<string> pinned, int maxResident) => _hot.EvictEntities(pinned, maxResident);
    public void CollectSavedHeads(ISet<string> pins, int perSet) => _hot.CollectSavedHeads(pins, perSet);

    // ── the warm pass (design §B step 6) ─────────────────────────────────────────────────────────────────────────────
    // The BOUNDED head-set, never the whole catalog: every saved-set member (all sets), every recently-opened surface,
    // and the rootlist playlist headers. One batched cold read, one BeginBulk scope (so the 12k backstop cannot run its
    // O(n log n) shed per upsert), one Bulk change signal at the end.
    void Warm()
    {
        long startTicks = Environment.TickCount64;
        int rows = 0;
        try
        {
            var uris = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setId in _hot.SavedSetIds())
                foreach (var u in _hot.SavedUris(setId)) if (u.Length > 0) uris.Add(u);
            lock (_pinGate)
            {
                foreach (var u in _recentSurfaces) if (u.Length > 0) uris.Add(u);
                foreach (var u in _rootlistUris) uris.Add(u);
            }
            if (uris.Count > 0)
                using (_hot.BeginBulk())
                    foreach (var e in _cold.LoadEntities(uris))
                    {
                        NoteColdRow(e.Uri, Hash(e.Payload));   // warm rows ARE cold rows — seed the presence set FIRST,
                        Replay(e);                             // so a heal inside Replay owns the final elision hash
                        rows++;
                    }
        }
        catch (Exception)
        {
            // A failed warm is a slower session, never a broken one: the cold fallback still serves every miss.
        }
        finally
        {
            System.Threading.Volatile.Write(ref _warmRows, rows);
            System.Threading.Volatile.Write(ref _warmMillis, Environment.TickCount64 - startTicks);
            _coldNotFullyResident = false;
            _warmDone.TrySetResult();
        }
    }

    void TouchResident(string playlistUri, int itemCount)
    {
        lock (_lruGate)
        {
            long bytes = (long)itemCount * BytesPerMembershipItem;
            if (_resident.TryGetValue(playlistUri, out var prev)) _residentBytes -= prev.Bytes;
            _resident[playlistUri] = (++_residentTick, bytes);
            _residentBytes += bytes;
            // Evict LRU until under both budgets — but never the just-touched MRU (count > 1 guards that).
            while (_resident.Count > 1 && (_resident.Count > _maxResidentPlaylists || _residentBytes > _maxResidentBytes))
            {
                string? lru = null;
                long min = long.MaxValue;
                foreach (var kv in _resident) if (kv.Value.Tick < min) { min = kv.Value.Tick; lru = kv.Key; }
                if (lru is null) break;
                _residentBytes -= _resident[lru].Bytes;
                _resident.Remove(lru);
                _hot.EvictMembership(lru);
            }
        }
    }

    void Replay(in ColdEntity e)
    {
        try
        {
            switch (e.Kind)
            {
                case EntityKind.Track: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Track); if (v != null) _hot.UpsertTrack(v); break; }
                case EntityKind.Album: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Album); if (v != null) _hot.UpsertAlbum(v); break; }
                // An artist row is only the CORE — re-fatten it from `artist_overview` on the way in, exactly like the
                // cold fallback does, so a warmed saved artist is not a header with an empty discography (and so
                // SpotifyArtistStatsService's TopTracks-presence gate doesn't refetch every launch).
                case EntityKind.Artist:
                {
                    var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Artist);
                    if (v is null) break;
                    if (ReadOverview(v.Uri) is { } doc) { _hot.UpsertArtist(ArtistSplit.Refatten(v, doc, GetTrack)); break; }
                    _hot.UpsertArtist(v);
                    // MIGRATION HEAL (Wave F / K2). The v4→v5 migration copies an Artist payload VERBATIM — the thin
                    // split is a WRITE-path transform, so a saved artist that is never re-fetched online would keep its
                    // fat core (up to ~360 KB of JSON) on disk forever, and `artist_overview` would stay empty for it.
                    // The warm replay is the one place that already holds the fat record AND knows the row is
                    // pin-reachable (it came out of the pin head-set), so it splits it once, offline, for free. A row
                    // that is already thin projects to an empty document and writes nothing.
                    var projected = ArtistSplit.Project(v);
                    if (ArtistSplit.HasContent(projected)) PersistArtist(v);
                    break;
                }
                case EntityKind.Playlist: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Playlist); if (v != null) _hot.UpsertPlaylist(v); break; }
                case EntityKind.Show: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Show); if (v != null) _hot.UpsertShow(v); break; }
                case EntityKind.Episode: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Episode); if (v != null) _hot.UpsertEpisode(v); break; }
                case EntityKind.User: { var v = JsonSerializer.Deserialize(e.Payload, EntityJson.Default.Owner); if (v != null) _hot.UpsertOwner(v); break; }
            }
        }
        catch (JsonException) { /* skip a corrupt row — it's re-fetchable */ }
    }

    void ReplayVideo(in ColdVideoAssoc v)
    {
        try { var a = JsonSerializer.Deserialize(v.Payload, EntityJson.Default.VideoAssociation); if (a != null) _hot.UpsertVideoAssociation(a); }
        catch (JsonException) { /* skip a corrupt row — it's re-fetchable */ }
    }

    // reads → the hot mirror first, with an UNCONDITIONAL cold fallback for entity kinds: the hot tier is a bounded cache
    // over cold now (deferred ctor + warm head-set + eviction), so a hot miss is never proof of absence. One indexed PK
    // read, one deserialize, promote back into hot (re-stamping its LRU recency). Membership/rootlist keep their own lazy
    // cold-promotion paths below.
    public Track? GetTrack(string uri) { var v = _hot.GetTrack(uri); if (v is not null) { Touch(uri); return v; } return ColdFallback<Track>(uri, EntityKind.Track, EntityJson.Default.Track, static (h, x) => h.UpsertTrack(x)); }
    public IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200) => _hot.QueryTracks(text, sort, limit);
    public Album? GetAlbum(string uri) { var v = _hot.GetAlbum(uri); if (v is not null) { Touch(uri); return v; } return ColdFallback<Album>(uri, EntityKind.Album, EntityJson.Default.Album, static (h, x) => h.UpsertAlbum(x)); }
    public Artist? GetArtist(string uri) { var v = _hot.GetArtist(uri); if (v is not null) { Touch(uri); return v; } return ColdFallbackArtist(uri); }
    public Playlist? GetPlaylist(string uri) { var v = _hot.GetPlaylist(uri); if (v is not null) { Touch(uri); return v; } return ColdFallback<Playlist>(uri, EntityKind.Playlist, EntityJson.Default.Playlist, static (h, x) => h.UpsertPlaylist(x)); }
    public Show? GetShow(string uri) { var v = _hot.GetShow(uri); if (v is not null) { Touch(uri); return v; } return ColdFallback<Show>(uri, EntityKind.Show, EntityJson.Default.Show, static (h, x) => h.UpsertShow(x)); }
    public Episode? GetEpisode(string uri) { var v = _hot.GetEpisode(uri); if (v is not null) { Touch(uri); return v; } return ColdFallback<Episode>(uri, EntityKind.Episode, EntityJson.Default.Episode, static (h, x) => h.UpsertEpisode(x)); }
    // Owners key on the CANONICAL user uri on both tiers (UserProfileIds.Normalize), so the hot dictionary key IS the
    // cold `entity.uri` — a bare id and its `spotify:user:` spelling can never become two rows.
    public Owner? GetOwner(string userUriOrId)
    {
        var v = _hot.GetOwner(userUriOrId);
        if (v is not null) { if (UserProfileIds.Normalize(userUriOrId) is { } k) Touch(k); return v; }
        return UserProfileIds.Normalize(userUriOrId) is { } key
            ? ColdFallback<Owner>(key, EntityKind.User, EntityJson.Default.Owner, static (h, x) => h.UpsertOwner(x))
            : null;
    }
    public VideoAssociation? GetVideoAssociation(string uri) => _hot.GetVideoAssociation(uri);
    public VideoOverride? GetVideoOverride(string uri) => _hot.GetVideoOverride(uri);
    public IReadOnlyList<VideoOverride> VideoOverrides() => _hot.VideoOverrides();

    // Deserialize one entity from the cold tier and promote it back into hot. No gate: after the deferred ctor the hot
    // tier starts EMPTY, so gating this on "something was evicted" would answer null for the whole catalog.
    T? ColdFallback<T>(string uri, EntityKind kind, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> json, Action<InMemoryStore, T> promote) where T : class
    {
        if (ReadCold(uri, kind) is not { } payload) return null;
        T? v;
        try { v = JsonSerializer.Deserialize(payload, json); }
        catch (JsonException) { return null; }   // a corrupt row is re-fetchable — treat as a miss
        if (v is null) return null;
        promote(_hot, v);
        return v;
    }

    byte[]? ReadCold(string uri, EntityKind kind)
    {
        if (_cold.GetEntity(uri) is not { } ce || ce.Kind != kind) return null;
        NoteColdRow(uri, Hash(ce.Payload));   // a hit PROVES cold-presence (and pins the elision hash to what is stored)
        Touch(uri);                           // a cold-fallback hit is the strongest possible "still in use" signal
        return ce.Payload;
    }

    // The artist read is the one that has to un-do the thin split: load the core, then re-fatten it from `artist_overview`
    // (album REFS → stub cards, top-track uris → resident tracks) and hand the stubs to ArtistDiscography.Assemble, which
    // is the EXISTING upgrade-stubs-from-standalone-album-rows logic — deliberately reused, never duplicated.
    [ThreadStatic] static string? s_refattening;
    Artist? ColdFallbackArtist(string uri)
    {
        if (ReadCold(uri, EntityKind.Artist) is not { } payload) return null;
        Artist? core;
        try { core = JsonSerializer.Deserialize(payload, EntityJson.Default.Artist); }
        catch (JsonException) { return null; }
        if (core is null) return null;

        if (s_refattening == uri) { _hot.UpsertArtist(core); return core; }   // reentrancy guard (Assemble reads back)
        var doc = ReadOverview(uri);
        if (doc is null) { _hot.UpsertArtist(core); return core; }

        var prev = s_refattening;
        s_refattening = uri;
        try
        {
            _hot.UpsertArtist(ArtistSplit.Refatten(core, doc, GetTrack));
            ArtistDiscography.Assemble(this, uri);   // stubs → hydrated standalone album rows (DATE_DESC), the existing path
        }
        catch (Exception) { /* a broken overview must never make the artist unreadable */ }
        finally { s_refattening = prev; }
        return _hot.GetArtist(uri) ?? core;
    }

    ArtistOverviewDoc? ReadOverview(string uri)
    {
        try
        {
            if (_cold.GetArtistOverview(uri) is not { } row || row.Payload.Length == 0) return null;
            var doc = JsonSerializer.Deserialize(row.Payload, EntityJson.Default.ArtistOverviewDoc);
            if (doc is not null) NoteOverview(uri, Hash(row.Payload));
            return doc;
        }
        catch (JsonException) { return null; }
        catch (Exception) { return null; }
    }

    /// <summary>The SQL-backed offline-search corpus (Addendum A4). Deliberately NOT routed through the hot tier: after
    /// the redesign the hot tier is a bounded cache, so "what is in my library" can only be answered from the cold tier's
    /// thin columns joined to the identity tables. The consumers overlay the resident records on top (a hot record is at
    /// worst as fresh as its cold row, and may be fresher — the write-behind lane is asynchronous, Addendum A6).</summary>
    public ColdCandidates LoadLibraryCandidates(ColdCandidateScope scope) => _cold.LoadLibraryCandidates(scope);

    public bool IsSaved(string setId, string uri) => _hot.IsSaved(setId, uri);
    public IReadOnlyList<string> SavedUris(string setId) => _hot.SavedUris(setId);

    // Membership + rootlist: dual-write (synchronous cold replace), and lazy-load from cold into the resident mirror on a
    // miss (the COLD → resident promotion). Large playlists aren't bulk-loaded at startup — they hydrate on first access.
    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev)
    {
        _hot.SetMembership(playlistUri, rows, baseRev);
        _cold.ReplaceMembership(playlistUri, ToCold(rows), baseRev);
        TouchResident(playlistUri, rows.Count);
        NoteAdopted(playlistUri);
        NoteMembers(rows);
        // PIN TRANSITION: adopting a membership pins the playlist itself (`playlists` gets a row) AND every member. Any
        // of them already hot but not yet on disk (hydrated as transient browse/queue metadata) must be flushed, or the
        // post-restart membership × entity join silently drops it. AdoptSnapshot writes the HEADER before the membership,
        // so the header write itself was gate-rejected — this is what rescues it. Batched onto the lane, never
        // synchronous on the caller (this path runs on the UI thread).
        EnqueueFlush(playlistUri);
        for (int i = 0; i < rows.Count; i++) EnqueueFlush(rows[i].ItemUri);
    }
    public IReadOnlyList<PlaylistMember> Membership(string playlistUri)
    {
        var m = _hot.Membership(playlistUri);
        if (m.Count > 0) { TouchResident(playlistUri, m.Count); return m; }   // resident hit → bump recency
        var cold = _cold.LoadMembership(playlistUri);
        if (cold.Count == 0) return m;
        var rows = FromCold(cold);
        _hot.SetMembership(playlistUri, rows, _cold.GetPlaylistRevision(playlistUri));   // promote into the resident mirror
        TouchResident(playlistUri, rows.Count);
        NoteAdopted(playlistUri);
        NoteMembers(rows);   // these came FROM the pin tables — mirror them, but nothing to flush
        return rows;
    }
    public bool HasMembership(string playlistUri)
    {
        if (_hot.HasMembership(playlistUri)) return true;
        // A persisted revision proves that ReplaceMembership ran even when the valid list is empty. Older/null-revision
        // rows can still be detected by their contents; newly-created empty playlists remain exact in the hot mirror.
        return _cold.GetPlaylistRevision(playlistUri) is not null || _cold.LoadMembership(playlistUri).Count > 0;
    }
    public byte[]? PlaylistRevision(string playlistUri) => _hot.PlaylistRevision(playlistUri) ?? _cold.GetPlaylistRevision(playlistUri);
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        _hot.SetRootlist(entries);   // preserve the stored revision (header hydration path)
        _cold.ReplaceRootlist(ToColdRoot(entries));
        NoteRootlist(entries);
    }
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)
    {
        _hot.SetRootlist(entries, rev);
        _cold.ReplaceRootlist(ToColdRoot(entries));
        _cold.SetRootlistRevision(rev);   // dual-write the rev to meta
        NoteRootlist(entries);
    }
    public byte[]? RootlistRevision() => _hot.RootlistRevision() ?? _cold.GetRootlistRevision();
    public IReadOnlyList<RootlistEntry> Rootlist()
    {
        var r = _hot.Rootlist();
        if (r.Count > 0) return r;
        var mapped = FromColdRoot(_cold.LoadRootlist());
        if (mapped.Count > 0) { _hot.SetRootlist(mapped); NoteRootlist(mapped); }
        return mapped;
    }

    /// <summary>Record a detail-page open (the `recent_surfaces` pin reason). Updates the in-memory mirror synchronously —
    /// the write gate must see the new pin immediately — and writes the row + flushes the surface entity on the lane.</summary>
    public void RecordRecentSurface(string uri, int kind)
    {
        if (string.IsNullOrEmpty(uri)) return;
        lock (_pinGate) _recentSurfaces.Add(uri);
        _lane.Writer.TryWrite(FlushOp.Recent(uri, kind));
        EnqueueFlush(uri);
    }

    static IReadOnlyList<ColdPlaylistItem> ToCold(IReadOnlyList<PlaylistMember> rows)
    {
        var list = new List<ColdPlaylistItem>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var c = r.Chart;
            list.Add(new ColdPlaylistItem(r.ItemId, r.ItemUri, r.AddedBy, r.AddedAt,
                c is { } chart ? (byte)chart.Status : (byte)0, c?.CurrentPos ?? 0, c?.PreviousPos ?? 0, c?.Rank ?? 0));
        }
        return list;
    }
    static IReadOnlyList<PlaylistMember> FromCold(IReadOnlyList<ColdPlaylistItem> rows)
    {
        var list = new List<PlaylistMember>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            // status==Unknown(0) with every other chart scalar also zero is indistinguishable from "no chart facts were
            // ever stored" — both read back as null, which is the correct answer for a non-chart playlist's rows.
            ChartEntry? chart = r.ChartStatus == 0 && r.ChartCurrentPos == 0 && r.ChartPreviousPos == 0 && r.ChartRank == 0
                ? null
                : new ChartEntry((ChartEntryStatus)r.ChartStatus, r.ChartCurrentPos, r.ChartPreviousPos, r.ChartRank);
            list.Add(new PlaylistMember(r.ItemId, r.ItemUri, r.AddedBy, r.AddedAt, chart));
        }
        return list;
    }
    static IReadOnlyList<ColdRootlistEntry> ToColdRoot(IReadOnlyList<RootlistEntry> e)
    {
        var list = new List<ColdRootlistEntry>(e.Count);
        for (int i = 0; i < e.Count; i++) { var r = e[i]; list.Add(new ColdRootlistEntry(r.Position, r.Kind, r.Uri, r.GroupName, r.Depth, r.AddedAtMs)); }
        return list;
    }
    static IReadOnlyList<RootlistEntry> FromColdRoot(IReadOnlyList<ColdRootlistEntry> e)
    {
        var list = new List<RootlistEntry>(e.Count);
        for (int i = 0; i < e.Count; i++) { var r = e[i]; list.Add(new RootlistEntry(r.Position, r.Kind, r.Uri, r.GroupName, r.Depth, r.AddedAtMs)); }
        return list;
    }

    public long Version(string uri) => _hot.Version(uri);
    public IObservable<StoreChange> Changes => _hot.Changes;
    public void Bump(string uri, CollectionKind? kind = null) => _hot.Bump(uri, kind);
    public IDisposable BeginBulk() => _hot.BeginBulk();   // the cold tier is already write-behind batched

    // ── writes → hot ALWAYS, cold IFF PIN-REACHABLE ──────────────────────────────────────────────────────────────────
    // The six chokepoints keep their signatures, so no caller changes: the gate is entirely inside them. A non-pinned
    // write is memory-only and bounded by the existing 4000-entity governor arena + the 12k→8k upsert backstop, which is
    // exactly the intent — transient queue/radio/autoplay/browse hydration stops reaching disk from this wave on.
    // THE STUB GATE (on top of the pin gate above): a PINNED row that is still a synthetic placeholder — no title at
    // all — used to be serialized and restored VERBATIM, which made a blank row durable across restarts. This is not
    // hypothetical: a playlist-member / saved / recent write races ahead of the real hydration wave carrying nothing
    // but a uri, so the FIRST write for a newly-pinned playable is very often exactly that stub. `ColdPresent` already
    // self-pins ("already a cold row" counts as pinned, PinnedTrack line ~558/571/etc.), so skipping the stub loses
    // nothing: the FIRST NAMED write persists, and every later write refreshes it same as before. Gate on
    // TitleMissing, not TrackUnnamed — a row whose ARTISTS lack names is still worth persisting (a link/queue label
    // still needs the row on disk); only a title-less row is the placeholder with nothing to show for it.
    public void UpsertTrack(Track t)
    {
        _hot.UpsertTrack(t);
        var merged = _hot.GetTrack(t.Uri) ?? t;
        if (PinnedTrack(merged) && !HydrationLevels.TitleMissing(merged.Title, merged.Uri)) PersistTrack(merged);
    }
    // Persist the entity HEADER thin: a container's hydrated tracklist is a read-model (joined from membership × shared
    // entities at read), never baked into the entity blob — that would re-serialize a multi-MB LOH blob per edit and
    // duplicate every Track N times. The in-memory tier keeps whatever the caller passed; only the cold blob is thinned.
    public void UpsertAlbum(Album a)
    {
        _hot.UpsertAlbum(a);
        var merged = _hot.GetAlbum(a.Uri) ?? a;
        if (PinnedAlbum(a.Uri, merged.Artists) && merged.Name.Length > 0) PersistAlbum(merged);
    }
    public void UpsertArtist(Artist a)
    {
        _hot.UpsertArtist(a);
        var merged = _hot.GetArtist(a.Uri) ?? a;
        if (PinnedArtist(a.Uri) && merged.Name.Length > 0) PersistArtist(merged);
    }
    public void UpsertPlaylist(Playlist p)
    {
        _hot.UpsertPlaylist(p);
        var merged = _hot.GetPlaylist(p.Uri) ?? p;
        if (PinnedPlaylist(merged.Uri) && merged.Name.Length > 0) PersistPlaylist(merged);
    }
    public void UpsertShow(Show s)
    {
        _hot.UpsertShow(s);
        var merged = _hot.GetShow(s.Uri) ?? s;
        if (PinnedShowOrEpisode(s.Uri) && merged.Name.Length > 0) PersistShow(merged);
    }
    public void UpsertEpisode(Episode e)
    {
        _hot.UpsertEpisode(e);
        var merged = _hot.GetEpisode(e.Uri) ?? e;
        if (PinnedShowOrEpisode(e.Uri) && !HydrationLevels.TitleMissing(merged.Title, merged.Uri)) PersistEpisode(merged);
    }
    // OWNERS ARE ALWAYS PERSISTED — the one entity kind that bypasses the pin-reachability gate (decision, P4-C).
    // Three reasons: an owner row is ~150 B (the whole resolvable set for a real library is well under a megabyte, i.e.
    // noise against the entity budget); the pin question is unanswerable cheaply, because an owner is reachable through
    // any playlist HEADER or any membership row's `added_by` and neither mirror is keyed by user; and an owner that is
    // NOT on disk makes the library grid and every playlist byline render a raw base62 id on the next offline launch —
    // the exact regression the read-model cache used to hide behind a session-lifetime dictionary. They still age out
    // through the ordinary unpinned 30-day entity TTL (EntityCacheGc §C.3), and re-resolve for free from kind 15.
    public void UpsertOwner(Owner o)
    {
        _hot.UpsertOwner(o);
        if (UserProfileIds.Normalize(o?.Id) is not { } key) return;
        var merged = _hot.GetOwner(key) ?? o!;
        // Owners still bypass the PIN gate above (P4-C), but not the STUB gate: a nameless owner row is exactly the
        // synthetic placeholder every other kind now skips, and it renders nothing an unresolved base62 id doesn't
        // already render — so there is no offline win to persisting it, only a write that gets clobbered the moment
        // a real display name resolves.
        if (merged.Name.Length == 0) return;
        PersistEntity(key, EntityKind.User, JsonSerializer.SerializeToUtf8Bytes(merged, EntityJson.Default.Owner));
    }
    public void UpsertVideoAssociation(VideoAssociation a) { _hot.UpsertVideoAssociation(a); _cold.UpsertVideoAssociation(a.Uri, JsonSerializer.SerializeToUtf8Bytes(a, EntityJson.Default.VideoAssociation)); }
    // The user's video curation: typed columns, not a JSON blob (the roster UI queries them by field), so no serializer
    // hop. Attaching an override is a PIN TRANSITION — flush the playable it points at.
    public void UpsertVideoOverride(VideoOverride o) { _hot.UpsertVideoOverride(o); _cold.UpsertVideoOverride(o); EnqueueFlush(o.Uri); }
    public void RemoveVideoOverride(string uri) { _hot.RemoveVideoOverride(uri); _cold.DeleteVideoOverride(uri); }
    // Ask the hot tier whether the write actually changed state (§7.4 no-op elision) and skip the cold dual-write when it
    // didn't — so an idempotent echo/delta-overlap costs neither a change signal nor a SQLite round-trip. added_at rides
    // both tiers (0 = preserve-existing, resolved per tier); a pure timestamp refinement still reaches the cold tier.
    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => SetSaved(setId, uri, saved, sync, 0);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs)
    {
        bool changed = _hot.SetSavedCore(setId, uri, saved, sync, addedAtMs);
        if (changed || (saved && addedAtMs != 0)) _cold.UpsertSaved(setId, uri, saved, sync, addedAtMs);
        // PIN TRANSITION (critique #1): liking writes collection_items only — no Upsert* fires — so an entity hydrated
        // hot-only would never reach disk, and the payload-hash elision would block the retry forever. Flush it here.
        if (saved && changed) EnqueueFlush(uri);
    }
    public IReadOnlyList<SavedItem> SavedItems(string setId) => _hot.SavedItems(setId);

    // ── pin reachability (all O(1)/cheap, thread-safe, no UI-thread pin-set snapshot) ────────────────────────────────
    // "Already a cold row" counts as pinned: the write gate decides what ENTERS the cache tier, and the GC decides what
    // LEAVES it. Refusing to refresh a row that is already on disk would just serve stale offline data.
    bool PinnedTrack(Track t)
    {
        if (ColdPresent(t.Uri) || _hot.IsSavedAnywhere(t.Uri)) return true;              // collection_items (incl. "liked")
        if (_hot.GetVideoOverride(t.Uri) is not null) return true;                       // video_override
        lock (_pinGate)
            if (_memberUris.Contains(t.Uri) || _recentSurfaces.Contains(t.Uri) || _pinReferenced.Contains(t.Uri)) return true;
        // outbox.entity_key needs no mirror: every outbox intent is written alongside its OPTIMISTIC hot state (a save
        // does SetSaved(Pending) first, a playlist edit rewrites membership first), so its target is already covered by
        // the saved/membership legs above.
        return t.Album.Uri.Length > 0 && PinnedAlbum(t.Album.Uri, null);
    }

    bool PinnedAlbum(string uri, IReadOnlyList<ArtistRef>? artists)
    {
        if (uri.Length == 0) return false;
        if (ColdPresent(uri) || _hot.IsSavedAnywhere(uri)) return true;
        lock (_pinGate) if (_recentSurfaces.Contains(uri) || _pinReferenced.Contains(uri)) return true;
        // Addendum A2 — this leg is what keeps a SAVED ARTIST's discography offline: the prefetcher's album cards are in
        // no collection and referenced by no pinned track, so without it critique #7's regression is real.
        artists ??= _hot.GetAlbum(uri)?.Artists;
        if (artists is not null)
            for (int i = 0; i < artists.Count; i++)
                if (artists[i].Uri.Length > 0 && _hot.IsSaved("artists", artists[i].Uri)) return true;
        return false;
    }

    bool PinnedArtist(string uri)
    {
        if (uri.Length == 0) return false;
        if (ColdPresent(uri) || _hot.IsSavedAnywhere(uri)) return true;
        lock (_pinGate) return _recentSurfaces.Contains(uri) || _pinReferenced.Contains(uri);
    }

    bool PinnedPlaylist(string uri)
    {
        if (uri.Length == 0) return false;
        if (ColdPresent(uri) || _hot.IsSavedAnywhere(uri)) return true;
        // `_adopted` is the mirror of `SELECT uri FROM playlists` — a playlist whose membership was adopted is pinned by
        // the canonical §A.3 pin set even when it is not in the rootlist (an opened foreign/editorial playlist).
        lock (_pinGate)
            return _rootlistUris.Contains(uri) || _adopted.Contains(uri) || _recentSurfaces.Contains(uri) || _pinReferenced.Contains(uri);
    }

    // Shows and episodes share a rule; an Episode carries no show REF in the model, so its show closure arrives the other
    // way round — a persisted pinned Show notes its episode uris as pin-referenced.
    bool PinnedShowOrEpisode(string uri)
    {
        if (uri.Length == 0) return false;
        if (ColdPresent(uri) || _hot.IsSavedAnywhere(uri)) return true;
        lock (_pinGate) return _recentSurfaces.Contains(uri) || _pinReferenced.Contains(uri);
    }

    // ── persist (post-gate): serialize, elide, enqueue ───────────────────────────────────────────────────────────────
    void PersistTrack(Track t)
    {
        NoteRefs(t.Album.Uri);
        for (int i = 0; i < t.Artists.Count; i++) NoteRefs(t.Artists[i].Uri);
        PersistEntity(t.Uri, EntityKind.Track, JsonSerializer.SerializeToUtf8Bytes(t, EntityJson.Default.Track));
    }

    // The ALBUM thin split (design §D.1, deferred out of Wave B): the persisted blob keeps the core + the "About this
    // release" scalars and drops the three FACET LISTS — MoreByArtist / ArtistsDetailed / OtherVersions — each of which
    // is a list of whole Album/Artist records embedded in an album row (the single biggest album-row bloater; Tracks was
    // already stripped). They are re-derivable: MoreByArtist/OtherVersions come back from the standalone album rows +
    // the getAlbum refetch, ArtistsDetailed from the artist rows.
    //
    // Two rules keep this lossless where it matters:
    //  1. `StoreEntityMerge.Album` keeps `current`'s facet when the incoming one is empty/null, so a re-loaded THIN album
    //     can never clobber a FAT hot record (verified by AlbumFacetStrip tests) — the hot tier is untouched by all this.
    //  2. The persisted Hydration is capped at `Tracks`: `Full` is the flag SpotifyAlbumEnrichmentService reads to decide
    //     the below-the-fold getAlbum upgrade is unnecessary, and a Full row with no facets would suppress the very
    //     refetch that rebuilds them. Capping is safe because the merge keeps the HIGHER of the two levels.
    //
    // THE KNOWN COST, stated plainly (finding 15; the mirror of this note is on HydrationLevels.Of(Album)). `Tracks` is
    // stripped too, and the tracklist IS the Open predicate — so a RESTORED album reads back at Identity. Online that is
    // cheap and invisible: the first open re-runs the ladder and the extended-metadata cache answers the AlbumV4
    // conditionally. OFFLINE it is a real hole: an album the user saved, whose track rows are all individually on disk,
    // can never reach Open after a restart, so the page paints a header with an empty tracklist. This is deliberate for
    // now (the fix belongs to persistence, not to the rung predicate, and it is not free): the ESCAPE HATCH when we take
    // it is to persist the track URIS — not the whole Track records, which is what the strip exists to avoid — for
    // PINNED albums only, or to re-join the persisted track rows into `Album.Tracks` at restore time. Plan §4 risk 3.
    void PersistAlbum(Album a)
    {
        for (int i = 0; i < a.Artists.Count; i++) NoteRefs(a.Artists[i].Uri);
        var thin = a.Tracks is null && a.MoreByArtist is null && a.ArtistsDetailed is null
                   && a.OtherVersions is null && a.Hydration != AlbumHydrationLevel.Full
            ? a
            : a with
            {
                Tracks = null, MoreByArtist = null, ArtistsDetailed = null, OtherVersions = null,
                Hydration = a.Hydration == AlbumHydrationLevel.Full ? AlbumHydrationLevel.Tracks : a.Hydration,
            };
        if (PersistEntity(a.Uri, EntityKind.Album, JsonSerializer.SerializeToUtf8Bytes(thin, EntityJson.Default.Album)))
            _lane.Writer.TryWrite(FlushOp.Refs(a.Uri, ArtistUris(a.Artists)));   // album→artists edges (Addendum A3)
    }

    // The THIN SPLIT (locked decision 17): the core goes to `entity`, the fat facets to `artist_overview`. Both sides are
    // derived from the MERGED hot record, so StoreEntityMerge.Artist's SWR/clobber gates have already run — a thin write
    // can never clobber a fat record here. The overview write itself is folded onto the STORED document on the lane, so
    // the same "absent facet keeps the stored one" rule also survives a restart (when hot holds only the core).
    void PersistArtist(Artist a)
    {
        var doc = ArtistSplit.Project(a);
        foreach (var uri in ArtistSplit.ReferencedAlbums(doc)) NoteRefs(uri);
        if (doc.TopTracks is { } tops) foreach (var row in tops) NoteRefs(row.Uri);
        PersistEntity(a.Uri, EntityKind.Artist, JsonSerializer.SerializeToUtf8Bytes(ArtistSplit.Core(a), EntityJson.Default.Artist));
        // The artist's own Popular list rides along: it is ≤10 rows already in hand, and without it the re-fatten hands
        // back an artist with no TopTracks — which trips SpotifyArtistStatsService's freshness gate every launch.
        // These bypass the hot merge (they are written straight from the projection), so a play-count-bearing overview
        // row lands on disk here — but a LATER thin write of the same uri re-persists the merged hot row over it. The
        // counts only stay put because the stats service now also upserts them into the track plane.
        if (a.TopTracks is { Count: > 0 } topTracks)
            for (int i = 0; i < topTracks.Count; i++)
                PersistEntity(topTracks[i].Uri, EntityKind.Track, JsonSerializer.SerializeToUtf8Bytes(topTracks[i], EntityJson.Default.Track));
        if (ArtistSplit.HasContent(doc)) _lane.Writer.TryWrite(FlushOp.Overview(a.Uri, doc));
    }

    void PersistPlaylist(Playlist p)
    {
        var thin = p.Tracks is null ? p : p with { Tracks = null };
        PersistEntity(p.Uri, EntityKind.Playlist, JsonSerializer.SerializeToUtf8Bytes(thin, EntityJson.Default.Playlist));
    }

    void PersistShow(Show s)
    {
        if (s.Episodes is { } eps) for (int i = 0; i < eps.Count; i++) NoteRefs(eps[i].Uri);   // show → episode closure
        PersistEntity(s.Uri, EntityKind.Show, JsonSerializer.SerializeToUtf8Bytes(s, EntityJson.Default.Show));
    }

    void PersistEpisode(Episode e)
        => PersistEntity(e.Uri, EntityKind.Episode, JsonSerializer.SerializeToUtf8Bytes(e, EntityJson.Default.Episode));

    /// <summary>The single cold-write chokepoint: elide when the bytes are unchanged AND the row is known-present, then
    /// enqueue on the store's write-behind lane. Returns whether a write was actually issued.</summary>
    bool PersistEntity(string uri, EntityKind kind, byte[] payload)
    {
        if (uri.Length == 0) return false;
        ulong hash = Hash(payload);
        lock (_coldGate) if (_coldRows.TryGetValue(uri, out var prev) && prev == hash) return false;
        _cold.UpsertEntity(uri, kind, payload);
        NoteColdRow(uri, hash);
        return true;
    }

    // ── the pin-transition flush lane ────────────────────────────────────────────────────────────────────────────────
    // One serialized background consumer for everything that must not run on the caller's thread: the "this just became
    // pinned — is it on disk?" flush, the artist-overview read-merge-write, the entity_refs replaces, and the
    // recent_surfaces row (all three of the latter take the cold store's WRITER lock synchronously).
    void EnqueueFlush(string uri)
    {
        if (string.IsNullOrEmpty(uri) || ColdPresent(uri)) return;
        _lane.Writer.TryWrite(FlushOp.Entity(uri));
    }

    async Task LaneLoopAsync()
    {
        while (await _lane.Reader.WaitToReadAsync().ConfigureAwait(false))
            while (_lane.Reader.TryRead(out var op))
            {
                try
                {
                    switch (op.Kind)
                    {
                        case FlushKind.Entity: FlushPinned(op.Uri); break;
                        case FlushKind.Overview: WriteOverview(op.Uri, op.Doc!); break;
                        case FlushKind.Refs: _cold.ReplaceEntityRefs(op.Uri, op.Children!); break;
                        case FlushKind.Recent: _cold.UpsertRecentSurface(op.Uri, op.N, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); break;
                        case FlushKind.Touch: FlushTouches(); break;
                    }
                }
                catch (Exception) { /* a cache write is non-fatal: the data is in memory + re-fetchable */ }
                op.Done?.TrySetResult();
            }
    }

    // "Pinned now, but was it persisted?" — resolve the uri against the hot tier by kind and persist it if cold lacks it.
    // Deliberately bypasses the pin GATE (the caller already established the pin) but keeps the elision/presence bookkeeping.
    void FlushPinned(string uri)
    {
        if (ColdPresent(uri)) return;
        // Same STUB GATE as the Upsert* chokepoints (§ around UpsertTrack): this bypasses the PIN gate deliberately
        // (the caller already established the pin) but a title-less/nameless hot row is still just the placeholder a
        // pin-transition (a like landing before the real hydration wave) can race ahead of — falling through here
        // leaves it unresident, and the hydration that follows the pin writes the named row through the normal
        // Upsert* gate, same as the "not resident" case below.
        if (_hot.GetTrack(uri) is { } t)
        {
            if (HydrationLevels.TitleMissing(t.Title, t.Uri)) return;
            PersistTrack(t);
            // A pinned track drags its album + artists onto disk with it (the §A.3 P1 closure) — otherwise the restored
            // row renders with no album art and no artist link until an online refetch.
            EnqueueFlush(t.Album.Uri);
            for (int i = 0; i < t.Artists.Count; i++) EnqueueFlush(t.Artists[i].Uri);
            return;
        }
        if (_hot.GetAlbum(uri) is { } al) { if (al.Name.Length > 0) PersistAlbum(al); return; }
        if (_hot.GetArtist(uri) is { } ar) { if (ar.Name.Length > 0) PersistArtist(ar); return; }
        if (_hot.GetPlaylist(uri) is { } p) { if (p.Name.Length > 0) PersistPlaylist(p); return; }
        if (_hot.GetShow(uri) is { } sh) { if (sh.Name.Length > 0) PersistShow(sh); return; }
        if (_hot.GetEpisode(uri) is { } ep) { if (!HydrationLevels.TitleMissing(ep.Title, ep.Uri)) PersistEpisode(ep); return; }
        if (_hot.GetOwner(uri) is { } ow)
        {
            if (ow.Name.Length > 0)
                PersistEntity(uri, EntityKind.User, JsonSerializer.SerializeToUtf8Bytes(ow, EntityJson.Default.Owner));
            return;
        }
        // Not resident: nothing to flush. Either it is already cold (a later ColdFallback marks presence) or it will be
        // written by the hydration that follows the pin — the gate now sees the pin, so that write lands.
    }

    void WriteOverview(string uri, ArtistOverviewDoc incoming)
    {
        var merged = ArtistSplit.Merge(ReadOverview(uri), incoming);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(merged, EntityJson.Default.ArtistOverviewDoc);
        ulong hash = Hash(bytes);
        lock (_coldGate) if (_coldOverviews.TryGetValue(uri, out var prev) && prev == hash) return;
        _cold.UpsertArtistOverview(uri, "", bytes, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        NoteOverview(uri, hash);
        _cold.ReplaceEntityRefs(uri, ArtistSplit.ReferencedAlbums(merged));   // artist→albums edges (Addendum A3)
    }

    static List<string> ArtistUris(IReadOnlyList<ArtistRef> artists)
    {
        var list = new List<string>(artists.Count);
        for (int i = 0; i < artists.Count; i++) if (artists[i].Uri.Length > 0) list.Add(artists[i].Uri);
        return list;
    }

    // ── mirrors + presence bookkeeping ───────────────────────────────────────────────────────────────────────────────
    void NoteMembers(IReadOnlyList<PlaylistMember> rows)
    {
        lock (_pinGate)
        {
            if (_memberUris.Count + rows.Count > PinMirrorCap) _memberUris.Clear();
            for (int i = 0; i < rows.Count; i++) if (rows[i].ItemUri.Length > 0) _memberUris.Add(rows[i].ItemUri);
        }
    }

    void NoteAdopted(string playlistUri)
    {
        if (playlistUri.Length == 0) return;
        lock (_pinGate)
        {
            if (_adopted.Count >= PinMirrorCap) _adopted.Clear();
            _adopted.Add(playlistUri);
        }
    }

    void NoteRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        lock (_pinGate)
        {
            _rootlistUris.Clear();   // the rootlist replace IS authoritative (unlike membership, it is one small list)
            for (int i = 0; i < entries.Count; i++) if (entries[i].Kind == 0 && entries[i].Uri.Length > 0) _rootlistUris.Add(entries[i].Uri);
        }
    }

    void NoteRefs(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        lock (_pinGate)
        {
            if (_pinReferenced.Count >= PinMirrorCap) _pinReferenced.Clear();
            _pinReferenced.Add(uri);
        }
    }

    // ── last-access touch tracking (§C.5) ────────────────────────────────────────────────────────────────────────────
    /// <summary>Midnight-truncated unix seconds — DAY granularity, directly comparable with the GC's TTL cutoffs.</summary>
    internal static long TouchDayOf(long unixSeconds) => unixSeconds - (unixSeconds % 86_400);

    static long Today() => TouchDayOf(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // THE HOT READ PATH. Already-recorded-today is one ConcurrentDictionary lookup and nothing else: no lock, no
    // allocation, no LINQ, no closure. Everything expensive lives behind the (rare) first-touch-of-the-day branch.
    void Touch(string uri)
    {
        if (uri.Length == 0) return;
        long day = Today();
        if (_touchDay.TryGetValue(uri, out long recorded) && recorded == day) return;
        _touchDay[uri] = day;
        if (_touchDay.Count > TouchDayCap) _touchDay.Clear();   // bounded: costs one redundant re-stamp per uri
        if (_touchPending.TryAdd(uri, 0) && System.Threading.Interlocked.Increment(ref _touchPendingCount) >= TouchPendingCap)
            RequestTouchFlush();   // overflow ⇒ flush EARLY rather than let the pending set grow unbounded
    }

    void RequestTouchFlush()
    {
        if (System.Threading.Volatile.Read(ref _touchPendingCount) == 0) return;
        _lane.Writer.TryWrite(FlushOp.Touch());
    }

    /// <summary>Drain the pending last-access set onto the cold tier. Called from the writer lane (60 s cadence / on
    /// overflow) and synchronously by the GC before it computes TTL victims, so a page the user just scrolled past can
    /// never be evicted for staleness.</summary>
    public void FlushTouches()
    {
        if (System.Threading.Volatile.Read(ref _touchPendingCount) == 0) return;
        var batch = new List<string>(Math.Min(TouchPendingCap, _touchPending.Count));
        foreach (var kv in _touchPending)
            if (_touchPending.TryRemove(kv.Key, out _))
            {
                System.Threading.Interlocked.Decrement(ref _touchPendingCount);
                batch.Add(kv.Key);
            }
        if (batch.Count == 0) return;
        try { _cold.TouchEntities(batch, Today()); }
        catch (Exception) { /* a last-access stamp is advisory: losing one costs at most an early eviction */ }
    }

    // ── the GC's in-memory pin half (critique #10) ───────────────────────────────────────────────────────────────────
    /// <summary>Copy the pin MIRRORS into <paramref name="into"/> — the in-memory half of the §A.3 pin set that the SQL
    /// side cannot see mid-session (a surface opened but not yet flushed, an adopted membership, the rootlist). Fully
    /// thread-safe (it only touches `_pinGate`), so the GC can call it from its own thread; the UI-THREAD-AFFINE half
    /// (now-playing / queue / detail caches) is <c>Services.BuildPinSet</c> and is snapshotted separately.</summary>
    public void SnapshotPinMirrors(ISet<string> into)
    {
        lock (_pinGate)
        {
            foreach (var u in _recentSurfaces) into.Add(u);
            foreach (var u in _rootlistUris) into.Add(u);
            foreach (var u in _adopted) into.Add(u);
            foreach (var u in _memberUris) into.Add(u);
        }
    }

    /// <summary>Membership-GC resync: the cold tier just purged these playlists' `playlist_items` + header rows, so the
    /// mirrors that mirror them must shed the same rows or the write gate keeps re-persisting entities nothing pins any
    /// more. `_adopted` is pruned exactly; `_memberUris` (add-only by design) is REBUILT from the still-adopted
    /// playlists' resident membership — strictly tighter than before, and a miss can only cost one redundant cold write
    /// (the entity is already on disk: adoption flushed it), never a stranded entity.</summary>
    public void OnMembershipsPurged(IReadOnlyList<string> playlistUris)
    {
        if (playlistUris is null || playlistUris.Count == 0) return;
        List<string> remaining;
        lock (_pinGate)
        {
            for (int i = 0; i < playlistUris.Count; i++) _adopted.Remove(playlistUris[i]);
            remaining = new List<string>(_adopted);
        }
        // Query the hot tier OUTSIDE _pinGate (it takes InMemoryStore's own gate) — never nest the two.
        var rebuilt = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < remaining.Count; i++)
        {
            var rows = _hot.Membership(remaining[i]);
            for (int j = 0; j < rows.Count; j++) if (rows[j].ItemUri.Length > 0) rebuilt.Add(rows[j].ItemUri);
        }
        lock (_pinGate)
        {
            _memberUris.Clear();
            foreach (var u in rebuilt) _memberUris.Add(u);
        }
        for (int i = 0; i < playlistUris.Count; i++) _hot.EvictMembership(playlistUris[i]);
        lock (_lruGate) for (int i = 0; i < playlistUris.Count; i++)
            if (_resident.Remove(playlistUris[i], out var gone)) _residentBytes -= gone.Bytes;
    }

    /// <summary>GC resync (Wave F): the cache GC just DELETED these entity rows, so the cold-PRESENCE map must forget
    /// them. Without this the map keeps claiming a row that no longer exists, and both guards that depend on it fail
    /// closed: <see cref="EnqueueFlush"/> short-circuits ("already on disk") so a later pin transition — liking the
    /// track, adding it to a playlist, attaching a video — never re-persists it, AND the payload-hash elision in
    /// <see cref="PersistEntity"/> skips the ordinary re-write because the bytes are unchanged. The entity is then
    /// stranded off disk permanently and vanishes from the liked/membership join after the next restart. That is exactly
    /// critique #1, re-armed by the collector instead of by the write gate.
    ///
    /// <paramref name="uris"/> null (the GC evicted more rows than it is willing to track) ⇒ drop the whole map: the map
    /// is a pure optimization, so clearing it only costs one redundant re-write per uri and can never lose data.</summary>
    public void OnEntitiesEvicted(IReadOnlyCollection<string>? uris)
    {
        lock (_coldGate)
        {
            if (uris is null) { _coldRows.Clear(); return; }
            foreach (var u in uris) _coldRows.Remove(u);
        }
    }

    /// <summary>GC resync for `artist_overview` (Wave F): same argument as <see cref="OnEntitiesEvicted"/> for the
    /// overview elision map. Overviews are bounded by the followed-artist count, so the sweep clears wholesale.</summary>
    public void OnOverviewsEvicted() { lock (_coldGate) _coldOverviews.Clear(); }

    /// <summary>Escape hatch (§G): drop the whole unpinned cache tier + every artist overview + every extension row.
    /// The presence/elision maps MUST be cleared with it — otherwise the FNV-1a elision would refuse to re-persist a row
    /// whose bytes are unchanged but whose disk row is gone. The hot tier is deliberately left alone: it is still valid
    /// (it re-faults from network/cold on the next miss).</summary>
    public (int Rows, long Bytes) ClearMetadataCache()
    {
        var result = _cold.ClearMetadataCache();
        lock (_coldGate) { _coldRows.Clear(); _coldOverviews.Clear(); }
        _touchDay.Clear();
        return result;
    }

    bool ColdPresent(string uri) { lock (_coldGate) return _coldRows.ContainsKey(uri); }

    void NoteColdRow(string uri, ulong hash)
    {
        lock (_coldGate)
        {
            if (_coldRows.Count >= ColdPresenceCap && !_coldRows.ContainsKey(uri)) _coldRows.Clear();
            _coldRows[uri] = hash;
        }
    }

    void NoteOverview(string uri, ulong hash)
    {
        lock (_coldGate)
        {
            if (_coldOverviews.Count >= ColdPresenceCap && !_coldOverviews.ContainsKey(uri)) _coldOverviews.Clear();
            _coldOverviews[uri] = hash;
        }
    }

    /// <summary>FNV-1a 64 over the serialized payload — the elision key. No new package dependency (the repo references
    /// no System.IO.Hashing), and a collision costs at most one skipped refresh of an identical-sized row.</summary>
    static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Block until every queued write is durable — drains the pin-transition lane FIRST (its writes feed the
    /// cold store's own queue), then the cold write-behind queue.</summary>
    public void Flush()
    {
        FlushTouches();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_lane.Writer.TryWrite(FlushOp.Barrier(done)))
            try { done.Task.Wait(TimeSpan.FromSeconds(30)); } catch { }
        _cold.Flush();
    }

    public void Dispose()
    {
        _touchTimer.Dispose();
        FlushTouches();
        _lane.Writer.TryComplete();
        try { _warm.Wait(TimeSpan.FromSeconds(10)); } catch { }
        try { _laneLoop.Wait(TimeSpan.FromSeconds(10)); } catch { }
        _cold.Dispose();
    }

    enum FlushKind : byte { Entity, Overview, Refs, Recent, Touch, Barrier }

    readonly struct FlushOp
    {
        public readonly FlushKind Kind;
        public readonly string Uri;
        public readonly int N;
        public readonly ArtistOverviewDoc? Doc;
        public readonly IReadOnlyList<string>? Children;
        public readonly TaskCompletionSource? Done;

        FlushOp(FlushKind kind, string uri, int n, ArtistOverviewDoc? doc, IReadOnlyList<string>? children, TaskCompletionSource? done)
        { Kind = kind; Uri = uri; N = n; Doc = doc; Children = children; Done = done; }

        public static FlushOp Entity(string uri) => new(FlushKind.Entity, uri, 0, null, null, null);
        public static FlushOp Overview(string uri, ArtistOverviewDoc doc) => new(FlushKind.Overview, uri, 0, doc, null, null);
        public static FlushOp Refs(string uri, IReadOnlyList<string> children) => new(FlushKind.Refs, uri, 0, null, children, null);
        public static FlushOp Recent(string uri, int kind) => new(FlushKind.Recent, uri, kind, null, null, null);
        public static FlushOp Touch() => new(FlushKind.Touch, "", 0, null, null, null);
        public static FlushOp Barrier(TaskCompletionSource done) => new(FlushKind.Barrier, "", 0, null, null, done);
    }
}

// AOT-clean source-gen serialization for the persisted entities (the generator pulls in the nested refs automatically).
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Track))]
[JsonSerializable(typeof(Album))]
[JsonSerializable(typeof(Artist))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(Show))]
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(Owner))]
[JsonSerializable(typeof(VideoAssociation))]
[JsonSerializable(typeof(ArtistOverviewDoc))]
internal partial class EntityJson : JsonSerializerContext { }
