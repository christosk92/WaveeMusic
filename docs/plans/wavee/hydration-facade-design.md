# Wavee hydration façade — the design (contracts + engine + ladders + traits)

> Companion to `hydration-facade-plan.md` (phases, ownership, gates) and `metadata-entry-points-inventory.md` (what exists
> today). This is the doc every implementing agent reads. Where the plan says *what*, this says *exactly which shapes*.
> Repo vocabulary: `docs/plans/wavee/architecture.md` §4 (ports / ACL / SourceRegistry), `.claude/skills/wavee/wiring-discipline.md`
> (no nullable seams, symmetric go-live/offline). Layering (binding): **ports in `Wavee.Core`** (zero deps), **engine in
> `src/apps/Wavee/Backend/Hydration`** (engine-free, `IStore`-level, compiled into `Wavee.Tests` by the `Backend\**` glob),
> **Spotify adapters in `src/apps/Wavee/SpotifyLive/Hydration`** (engine-free; test csproj gets one glob for that folder).

---

## 1. Ports — `src/apps/Wavee.Core/Hydration/`

### 1.1 `EntityUri.cs`

```csharp
namespace Wavee.Core;

public enum EntityKind : byte
{
    Unknown, Track, Episode, Album, Artist, Playlist, Show, User,
    Collection,   // spotify:collection:tracks (Liked), spotify:user:<u>:collection, spotify:collection:{albums|artists|shows|episodes}
    Prerelease,   // spotify:prerelease:<id>
    Concert,      // spotify:concert:<id>
}

/// The one uri parser. Alloc-free (span walk); Provider decides routing (SourceRegistry.Owns), Kind decides the ladder.
public readonly record struct EntityUri(string Uri, string Provider, EntityKind Kind, string Id)
{
    public static EntityUri Parse(string uri);     // "spotify:*"→"spotify"; "local:*"/"wavee:local:*"→"local"; "wavee:playlist:*"→"user";
                                                   // "wavee:show:*"/"wavee:episode:*"→"wavee-podcast"; "fake:*" or legacy tr|al|pl{N}→"fake"; else ("", Unknown, "")
    public static EntityKind KindOf(string uri);
    public static string IdOf(string uri);         // THE IdOf: trailing segment after the last ':' ("spotify:user:x:playlist:y" → "y"); "" for empty
    public bool IsPlayable => Kind is EntityKind.Track or EntityKind.Episode;
    public bool IsContainer => Kind is EntityKind.Album or EntityKind.Playlist or EntityKind.Show or EntityKind.Collection or EntityKind.Artist;
    public bool IsSpotify => Provider == "spotify";
}
```
Rules: no production code compares `"spotify:track:"` etc. outside this type (a grep-test enforces it); routing sites use `Kind`; the six `IdOf` copies die.

### 1.2 `HydrationLevel.cs`

```csharp
public enum HydrationLevel : byte { None = 0, Identity = 1, Open = 2, Rich = 3, Full = 4 }

/// Presence-only per-kind predicates (pure; no store). Freshness (age) is the engine's ledger + Artist.OverviewFetchedAt
/// + Artist.ChartFetchedAt (the chart transport's own clock — presence cannot express "the chart step ran", because a
/// niche artist's real chart is shorter than OverviewSeedCap and can therefore never satisfy the Full predicate).
public static class HydrationLevels
{
    public static HydrationLevel Of(Track? t);      // Identity: row ∧ !TitleMissing · Open: + artists named ∧ Album.Name!="" ∧ image usable ∧ DurationMs>0 · Rich≡Open · Full: + Availability != null
    public static HydrationLevel Of(Episode? e);    // Identity: title · Open: + ShowName ∧ image ∧ duration · Rich≡Open · Full: + Description
    public static HydrationLevel Of(Album? a);      // Identity: Name · Open: Hydration>=Tracks ∧ Tracks>0 ∧ no unnamed track
                                                    // Full: Open ∧ Hydration==Full (tested FIRST) · Rich: Open ∧ (Copyright ∨ ReleaseDate ∨ Hydration==Full)
                                                    // The order is load-bearing: a Full getAlbum envelope for a release with NO publishing facet
                                                    // used to fall out of a Rich-first short-circuit and report Open, so DetailTrailing's Full ask
                                                    // could never see its own answer and re-ran getAlbum every AlbumFullTtl forever.
    public static HydrationLevel Of(Artist? a);     // Identity: Name · Open: TopAlbums>0 ∧ TopAlbums[0].Name!="" ∧ AlbumsTotal+SinglesTotal+CompilationsTotal <= TopAlbums.Count
                                                    // Rich: + TopTracks>0 ∧ (LatestRelease ∨ PopularReleases>0) · Full: + TopTracks.Count > ArtistPopularTracks.OverviewSeedCap
    public static HydrationLevel Of(Playlist? p, bool hasMembership);   // Identity: Name · Open: + hasMembership · Rich≡Full≡Open
    public static HydrationLevel Of(Show? s, bool hasMembership, int residentOpenEpisodes, int memberCount);   // Open: first min(300,memberCount) resident · Full: all
    public static HydrationLevel Of(Owner? o);      // Identity: Name; all rungs ≡
    // Row-gap primitives (moved from StoreEntityGaps / StoreEntityMerge so there is ONE copy):
    public static bool TitleMissing(string? title, string uri);   // blank OR title == uri (the synthetic placeholder)
    public static bool TrackUnnamed(Track t);
    public static bool RefNeedsName(in AlbumRef r);
}
```

### 1.3 `IEntityHydrator.cs`

```csharp
public enum HydrationMode : byte { Blocking, Background }
public readonly record struct HydrationOptions(HydrationMode Mode = HydrationMode.Blocking, bool Revalidate = false,
                                               TraitSurface Surface = TraitSurface.None, int Priority = 0)
{ public static readonly HydrationOptions Default = new(); public static readonly HydrationOptions Prefetch = new(HydrationMode.Background, Priority: -1, Surface: TraitSurface.Prefetch); }
public enum HydrationStatus : byte { Reached, Partial /*ladder ran, level not reached (sealed Exhausted)*/, Failed, Cancelled, Unsupported /*offline, no owner, kind not hydratable*/ }
public readonly record struct HydrationOutcome(HydrationLevel Reached, HydrationStatus Status, string? Error = null) { public bool Ok => Status == HydrationStatus.Reached; }
public readonly record struct HydrationBatchOutcome(IReadOnlyCollection<string> Reached, IReadOnlyCollection<string> Missing, HydrationStatus Status);

public interface IEntityHydrator
{
    HydrationLevel LevelOf(string uri);                                                                     // presence-only, sync, store-backed
    Task<HydrationOutcome>      EnsureAsync(string uri, HydrationLevel level, HydrationOptions opts = default, CancellationToken ct = default);
    Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level, HydrationOptions opts = default, CancellationToken ct = default);
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default);          // TraitPolicy picks the TraitSet
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default);
    void Invalidate(string uri);                                                                              // dealer / video recovery: unseal all levels
}
```
Semantics: `Blocking` returns when the level is reached **or the ladder is exhausted** (never hangs on background continuations); `Background` enqueues on the pump and returns `(LevelOf(uri), Reached|Partial)` immediately — callers read the store later via `IStore.Changes`. Transport failures never throw out of `EnsureTraitsAsync`; `EnsureAsync` reports `Failed`.

Implementations (Core): `CompleteEntityHydrator.Instance` (complete-at-construction sources: SpotifyExport/Local/Fake/UserPlaylist/test fakes → `Reached` at the requested level), `NotOwnedEntityHydrator.Instance` (`Unsupported`), `SwitchableEntityHydrator` (`SetInner`, volatile). Backend: `OfflineEntityHydrator(IStore)` (touches `store.GetX` → cold promotion; `Reached` iff resident satisfies, else `Unsupported`; never networks/throws), `HydrationRouter`, `SpotifyProviderHydrator`. **No nullable hydrator anywhere.**

`ICatalogSource` gets `IEntityHydrator Hydrator => CompleteEntityHydrator.Instance;` (default interface member). `ICatalogSource`/`IMusicLibrary`/`AggregateCatalog` single-item reads gain `HydrationLevel level = HydrationLevel.Open` (defaulted, last positional before `ct`).

### 1.4 `Traits.cs`

```csharp
[Flags] public enum TraitSet : ushort
{
    None = 0,
    Video           = 1 << 0,   // 99 VIDEO_ASSOCIATIONS (+182 CONSUMPTION_EXPERIENCE companion; TRACK_V4/212 recovery follow-up) → VideoAssociation plane
    AudioAttributes = 1 << 1,   // 222 → Track.TempoBpm/MusicalKey/Camelot*
    Descriptors     = 1 << 2,   // 6   → Track.Tags (empty list writes [])
    VisualIdentity  = 1 << 3,   // 179 → CoverColorPlane (image-keyed)
    PlayCount       = 1 << 4,   // 185 → Track.PlayCount (track f3)
    Publishing      = 1 << 5,   // 183 → Album.Copyright/ReleaseDate/Precision
    IdentityTraits  = 1 << 6,   // 178 + 220 — wire fidelity, no projection
    RowBundle = Video | AudioAttributes | Descriptors | VisualIdentity,
}
public enum TraitSurface : byte { None, AlbumOpen, PlaylistOpen, LikedSongs, ShowOpen, ArtistPopular, Queue, Search, Recents, NowPlaying, PlaysToggle, TrackExpansion, Credits, PreRelease, UserProfiles, Prefetch, Context }
```

### 1.5 `EpisodeAsTrack.cs` (Domain) — the playable projection used by `JoinMembership`, `LiveContextResolver`, `EmptyContextResolver`, Recents
```csharp
public static class EpisodeAsTrack
{   // Id = episode id (TrackRow.StateOf compares Identity.Track.Id); Artists = []; Album = new AlbumRef("", showUri, ShowName) (uri-less "" if unknown);
    // Image, DurationMs; IsExplicit=false; Availability=null; Origin=Streamed; Source="podcast". ProgressMs has no home on Track (out of scope).
    public static Track? From(Episode? e, string? showUri = null);   // showUri ?? e.ShowUri ?? "" — the caller's knowledge wins
}
```
The show uri is not an argument the callers have to find: `Episode` carries `ShowUri` (P4-B, optional/last so persisted blobs
default to null), stamped by `ExtendedMetadataSource.ProjectEpisode` from EpisodeV4's embedded show ref (`LeanEpisode.Show.Gid`
→ `spotify:show:<id>`) and carried by `StoreEntityMerge.Episode` with an additive `incoming.ShowUri ?? current.ShowUri` so a
gid-less writer never strips the link. `HydrationLevels.Of(Episode)` does NOT read it — the rung is title/show-name/image/duration,
and an episode whose show ref was name-only must not be stuck below Open forever.

---

## 2. Engine — `src/apps/Wavee/Backend/Hydration/`

### 2.1 Router, ledger, pump, policy

```csharp
public sealed class HydrationRouter : IEntityHydrator   // Wavee.Core/Hydration (it only needs SourceRegistry + ICatalogSource.Hydrator)
{   // EnsureAsync: owner = registry.OwnerOf(uri) → owner.Hydrator (else NotOwned). EnsureManyAsync: group by owner, forward per group, merge outcomes.
    // Registration order = SourceRegistry order. FakeSource.Owns := Provider=="fake" (+ SourceCapabilities.Fallback; AggregateCatalog does the explicit fallback step).
}

readonly record struct HydrationKey(string Locale, string Uri, HydrationLevel Level);
sealed class HydrationLedger   // Backend/Hydration — THE session freshness authority for the port (replaces MetadataService's Resource + all per-service negative memos)
{
    readonly Resource<HydrationKey, HydrationOutcome> _res;   // FreshnessPolicy.Etag(base) + ttlOf: o => policy.Ttl(kind, level, o.Ok)
    public bool IsFresh(in EntityUri u, HydrationLevel l);
    public HydrationClaims Claim(IReadOnlyList<EntityUri> work, HydrationLevel l);   // CLAIM-then-run: takes the (uri,l) slots nobody else holds
    public void Seal(in EntityUri u, HydrationLevel reachedUpTo, HydrationOutcome o, bool transient = false);  // seals every level ≤ reached (outcome seeding)
    public void Invalidate(string uri);                                                                       // MarkStale all levels
}
sealed class HydrationClaims : IDisposable   // one caller's share of a pass
{   // ClaimedUris = EXACTLY what this caller must fetch (never a uri another caller already claimed — no double-fetch on a partial overlap);
    // Waits = its own slots + the runs it joined; Publish(outcomeOf, wasTransient) seals+answers; Fail(status, err) seals nothing and answers a STATUS
    // (a joiner never catches the owner's exception); Dispose is the belt-and-braces release so an abandoned claim cannot wedge a uri.
}
sealed class HydrationPump { public void Enqueue(int priority, Func<CancellationToken, Task> work); public CancellationToken Token { get; } }
// Bounded (4096, sheds the last job in run order — lowest priority, latest arrival), session ct, low priority = prefetch. `Token` is what a SHARED
// ladder pass runs on: the caller that won the claim race contributes no lifetime, so a nav-away cancels its own wait and nobody else's.
// Dispose CANCELS ONLY (never disposes the CTS): the drain loop and a running job both touch the token after Dispose returns.
public sealed record HydrationPolicy(TimeSpan IdentityTtl = 1h, TimeSpan OpenTtl = 1h, TimeSpan ArtistRichTtl = 12h, TimeSpan AlbumFullTtl = 10min,
                                     TimeSpan ExhaustedPlayableTtl = 10min, TimeSpan ExhaustedAlbumRichTtl = 24h);
// Ttl(kind, level, ok, transient): a TRANSIENT exhausted seal never takes the long "genuinely absent" window. A ladder step that swallowed a transport
// failure reports it on `HydrationRunScope` (ctx.ReportTransient(uri), one scope per pass, handed to the ladders as ctx.ForRun(scope)); the hydrator
// then seals that uri short. Without it a single 503 on the album trait pass cost the ©/℗ line + the RowBundle for ExhaustedAlbumRichTtl = 24 h.
public static class OpenPolicy   // kind → (blocking level, background level) for a page open — the ONE table
{   // Album: (Rich, Full-only-when-asked) · Artist: (Open, Rich-only-when-asked) · Playlist: (Open blocking iff !HasMembership, else Revalidate) · Show: (Open, Full) · Playable: (Open, -) · Collection: (Open background)
}
// BOTH arms are the caller's contract, not a suggestion: GetPlaylistAsync and GetShowAsync each await `Blocking` and
// fire-and-forget `Background`. A read that consults the table and then ignores `Background` is the same bug twice —
// the Show open dropped it, so a 700-episode show sat at its first 300 rows until the user tapped "Load more".
```
Playlist Open is never TTL-sealed by the ledger (LibrarySync's `_openInFlight` + 5-min window + dirty set stay authoritative); the ledger only dedupes concurrent callers.

### 2.2 Ports (transport seams, `Backend/Hydration/Ports.cs`; implemented in `SpotifyLive/Hydration/`)

```csharp
public interface ICatalogFetch      // XmCatalogFetch: MetadataService.SyncAllConditionalAsync + ProjectCachedExtensions + ExtendedMetadataSource.FetchAsync fused; ONE XmKinds.CatalogKindOf(EntityKind)
{   /// One conditional (etag) POST per MetadataChunking chunk (300 entities / 4 MB), mixed kinds; extraKinds fused under the same uri group. Returns uris whose PROJECTION wrote.
    Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris, IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct);
}
public interface IEnvelopeFetch     // PathfinderEnvelopeFetch — returns MAPPED domain objects; the ladder writes the store
{   Task<Album?> AlbumAsync(string uri, CancellationToken ct);            // getAlbum (limit 50) — LiveSessionHost.FetchAlbumAsync body
    Task<Track?> TrackAsync(string uri, CancellationToken ct);            // getTrack
    Task<Artist?> ArtistOverviewAsync(string uri, CancellationToken ct);  // queryArtistOverview — the ONE caller
}
public interface IArtistChartFetch { Task<IReadOnlyList<string>> TopTrackUrisAsync(string artistUri, CancellationToken ct); }   // spclient artist-top-tracks-extensions
public interface IPlaylistOpener   { Task OpenAsync(string uri, CancellationToken ct); void Revalidate(string uri); Task HeaderAsync(string uri, CancellationToken ct); }  // LibrarySync/PlaylistFetcher
public interface IUserProfileFetch { Task<IReadOnlyDictionary<string, Owner?>> ResolveAsync(IReadOnlyList<string> userIds, CancellationToken ct); }        // kind 15 batch + REST fallback
```

### 2.3 `SpotifyProviderHydrator : IEntityHydrator` (Backend/Hydration; ctor takes IStore, the ports, TraitPipeline, TraitPolicy, HydrationPolicy, WaveeLogger)
`EnsureManyAsync`: parse → drop Unknown → filter (LevelOf ≥ level ∧ ledger fresh) → step 0 `ICatalogFetch.FetchAsync(misses, extraKindsForLevel)` (ONE XM POST for mixed kinds) → per-kind continuations (`ladder.ContinueManyAsync`) → seal each uri at `LevelOf` → post-steps on the pump.

Ladders (steps to reach a rung; every ladder's `Of` is `HydrationLevels.Of`):
- **AlbumHydration** — Identity/Open: `[XM AlbumV4]` → `[TrackV4 repair of unnamed disc rows; rebuild Album.Tracks from store rows]` → `[IEnvelopeFetch.AlbumAsync ONLY if still !Open]` (V4-empty fallback). Rich: same, with `(uri, 183)` fused into the first POST, then **awaited** `EnsureTraitsAsync(trackUris, AlbumOpen)` (RowBundle|PlayCount in ONE trait POST — replaces plays+publishing+FillAlbumAdornments+the second 185). Full: Rich → `AlbumAsync` (getAlbum) → `UpsertArtist × ArtistsDetailed`, `UpsertTrack` per row, `UpsertAlbum(Full)`. Post (pump): nothing extra (RowBundle already in Rich).
- **ArtistHydration** — freshness: Rich iff `now - OverviewFetchedAt ≤ ArtistRichTtl`; the Full/chart step iff `now - ChartFetchedAt ≤ ArtistRichTtl` (its OWN stamp, written whenever `TopTrackUrisAsync` answers — including an empty or short answer — and never on a throw). Identity `[XM ArtistV4]`; Open → `[XM AlbumV4 for own-discography stubs with Name=="" or no resident row]` → `ArtistDiscography.Assemble`; Rich → `[XM AlbumV4 appears-on stubs ≤ 20]` → `[ArtistOverviewAsync → stats-only UpsertArtist(TopAlbums=null, AppearsOn=null, totals=0, OverviewFetchedAt=now) + UpsertTrack per top track]` → Assemble; Full → `[IArtistChartFetch.TopTrackUrisAsync]` → `EnsureManyAsync(uris, Identity)` → `ArtistPopularTracks.Merge(head=store TopTracks, resolved)` → `UpsertArtist(TopTracks=merged)` → awaited `EnsureTraitsAsync(chartUris, ArtistPopular)` → `ArtistPopularTracks.WithPlayCounts` from store rows. Age: Rich/Full fresh iff `now - OverviewFetchedAt ≤ ArtistRichTtl`.
- **PlayableHydration** (Track|Episode) — Identity `[XM TrackV4 | EpisodeV4]`; Open → track: `[IEnvelopeFetch.TrackAsync ONLY if still !Open]` (getTrack repair; Exhausted seal 10 min replaces the heartbeat gate); episode: none. Full (track): getTrack. Post (pump, depth-bounded by construction — album Identity has no post-step): ref-closure `RefNeedsName(Album)` → `EnsureManyAsync(albumUris, Identity, Background)`; `TrackUnnamed` → `EnsureManyAsync(uris, Open, Background)`; ≤300/batch, ≤900/pass; the ledger dedupes.
- **PlaylistHydration** — Identity: rootlist member ? `opener.HeaderAsync` : XM 205 via `ICatalogFetch` (`ExtendedMetadataSource.ProjectPlaylist` = THE 205 projector). Open: `!HasMembership ? await opener.OpenAsync(uri) : opener.Revalidate(uri)`; post: `EnsureTraitsAsync(memberUris incl. episodes, PlaylistOpen)`. Never writes membership.
- **ShowHydration** — Identity `[XM ShowV4]` (header + `SetMembership(showUri, episodes)`); Open → awaited `EnsureManyAsync(members[..300], Open)` + `store.RecordRecentSurface(showUri)`; Full → remaining members paged 300 on the pump; load-more → `EnsureManyAsync(members[from..from+300], Open)` returning the NEW cursor.
  The load-more gate is a CURSOR (`Show.PagedThrough`, a read-model int = the membership offset already asked for; `hasMore ⇔ PagedThrough < TotalEpisodes`, `from = PagedThrough`), never resident-count-vs-membership-count: a member that can never hydrate (withdrawn/region-locked) holds the resident count permanently short, which pinned the pill on screen and re-asked the same block on every tap. `StoreLibrarySource` stamps it as `max(one-past-the-last-resident-member, the offsets it has asked for)`.
- **CollectionHydration** — Open: `EnsureManyAsync(SavedUris(set), Identity, Background, pages 300)` + `EnsureTraitsAsync(rows, LikedSongs)`.
- **UserHydration** — Identity: `IUserProfileFetch.ResolveAsync(ids)` → `IStore.UpsertOwner(Owner)` (new hot+cold entity) under `BeginBulk`.

### 2.4 Trait pipeline — `TraitPipeline.cs`, `ITraitProjector.cs`, `TraitPolicy.cs`, `NegativeMemo.cs`, `Projectors/*`

```csharp
public enum TraitOutcome { Applied, Unchanged, Negative, NotResident }   // Negative/Unchanged → memo; NotResident never memoized, never minted
public interface ITraitProjector
{
    TraitSet Trait { get; }  Xm.ExtensionKind Kind { get; }  ReadOnlySpan<Xm.ExtensionKind> Companions => default;
    bool AppliesTo(EntityKind kind);                                    // TraitApplicability table; unknown ⇒ true (ask once, honor 404)
    bool AlreadyHas(IStore store, string uri, DateTimeOffset now);       // per-kind mark: 222→TempoBpm, 6→Tags, 179→CoverColorPlane.HasFreshDark(rowImageUrl), 99→plane IsFresh, 185→PlayCount>0, 183→Copyright∧ReleaseDate, 178/220→false
    TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads);   // inside the page's lazy BeginBulk; never mint a row
    ValueTask CompleteBatchAsync(TraitBatch batch, CancellationToken ct) => default;   // Video: canonical recovery (once per alias; Missing never downgrades a resident HasVideo:true)
}
public sealed class TraitBatch : IDisposable { IStore Store; DateTimeOffset Now; TraitSurface Surface; WaveeLogger Log; void Write(Action<IStore>) /*opens BeginBulk lazily*/; List<string> FollowUp; }
public readonly struct TraitPayloads { CachedExtension? Get(kind); bool Missing(kind); ByteString? Payload(kind); }

public interface ITraitPipeline { Task EnsureAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default); }
// TraitPipeline: plan (applies ∧ !AlreadyHas ∧ !memo) → ONE ExtensionEtagCache.GetAsync per ≤MetadataChunking.MaxEntitiesPerRequest uris carrying every wanted kind (+companions) under each uri,
//   clientFeatureId = TraitSurfaces.ClientFeatureId(surface) → project under one lazy BeginBulk per page → memoize Negative/Unchanged → CompleteBatchAsync → log traits.batch (per-kind/per-EntityKind negatives).
//   ExtensionEtagCache is REQUIRED (no raw fallback). No in-flight coalescer (the cache serializes misses under _batchGate; duplicate projection is idempotent).
public sealed class NegativeMemo { bool Contains(uri, kind); void Add(uri, kind); /* cap 65_536, stop-adding past cap, session-scoped, shared with ExtensionReader */ }
public sealed class TraitPolicy(Func<bool> playsColumnOn) { public TraitSet For(TraitSurface s); }   // AlbumOpen=RowBundle|PlayCount|Publishing · Playlist/Liked=RowBundle|(PlayCount iff on) · ShowOpen=RowBundle · ArtistPopular=RowBundle|PlayCount · Queue/Search=RowBundle · Recents=IdentityTraits|VisualIdentity · NowPlaying=Video · PlaysToggle=PlayCount
public static class TraitSurfaces { public static string? ClientFeatureId(this TraitSurface s); }   // Recents→"mdata_esperanto"; PreRelease/UserProfiles→null; else "track_metadata_loader"
public static class TraitProjectors { public static IReadOnlyList<ITraitProjector> Default(IExtensionReader reader, Func<CoverColorPlane?> plane); }
```
Applicability (pinned from the probe; episode = ask-once): 99/182, 222, 6, 185 → Track (Episode ask-once; others ✗); 179 → all (Show/Episode ask-once); 183 → Album only; 178/220 → all.
Projectors move code verbatim: `VideoProjector` (from `SpotifyVideoService.Fold/Project/RecoverCanonicalAsync/DetectTally/LogSlice`), `AudioAttributesProjector` (222 parse + `row with {TempoBpm,…}`), `DescriptorProjector` (6), `VisualIdentityProjector` (179 → `CoverColorPlane`; SpotifyLive), `PlayCountProjector` (185; `OnPlatformReputation` decoder), `PublishingProjector` (183; `AlbumPublishing.Apply/JoinCopyrightLines`), `IdentityTraitsProjector` (no-op).

### 2.5 `ExtensionReader` (display-only XM reads)
```csharp
public readonly record struct ReadOptions(bool Revalidate = false);
public interface IExtensionReader
{
    Task<T?> ReadAsync<T>(string uri, Xm.ExtensionKind kind, Func<ByteString, T?> parse, TraitSurface surface, CancellationToken ct = default, ReadOptions options = default) where T : class;
    Task<IReadOnlyDictionary<string, T>> ReadManyAsync<T>(IReadOnlyList<string> uris, Xm.ExtensionKind kind, Func<ByteString, T?> parse, TraitSurface surface, CancellationToken ct = default) where T : class;
    Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> ReadRawAsync(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> reqs, TraitSurface surface, CancellationToken ct = default, ReadOptions options = default);
    void Seed<T>(string uri, Xm.ExtensionKind kind, T? answer) where T : class;
}
// ExtensionReader(ExtensionEtagCache cache, NegativeMemo negatives, WaveeLogger log, int parsedCap = 1024): parsed-answer LRU incl. null; TCS slot published BEFORE the load
// (GetOrAdd + finally TryRemove(key,value)) so a synchronous completion never strands the slot; WaitAsync(ct) on the shared task; a 404/empty/undecodable = null answer memoized;
// transport failure NOT memoized. Thin services over it: SpotifyTrackCreditsService (186), SpotifyPreReleaseService (138 + Seed under both uris), SpotifyTrackExpansionService
// (ReadRawAsync 99/98/5/237 with Revalidate; targets via ReadManyAsync TrackV4; the dead 222 target ask dropped), SpotifyUserProfileFetch (15, REST fallback for the rest).
```

### 2.6 `LiveWiring` (`Backend/Wiring/LiveWiring.cs`)
```csharp
public sealed class LiveWiring
{
    public void Set(string name, Action install, Action uninstall);                                          // installs NOW, records the inverse
    public void Swap<T>(string name, Action<T> setInner, T live, Func<T> offline) => Set(name, () => setInner(live), () => setInner(offline()));
    public IReadOnlyList<string> Installed { get; }
    public void Uninstall();                       // reverse order, idempotent, each guarded (exceptions logged, not thrown)
    public void AssertCovers(IEnumerable<string> required);   // throws naming the missing seams
}
```
`LiveSessionHost.StartAsync` = build transports → construct `SpotifyProviderHydrator` → a list of `wiring.Swap/Set` lines → `wiring.AssertCovers(Services.LiveSeams)`. `Services.GoOffline()` = `LiveHost?.Wiring.Uninstall()` (+ the GoLive inverses registered the same way). No install without a teardown by construction.

**The go-live FAILURE path is a rollback.** Everything past `new LiveWiring` mutates a process-wide `Services`, and the block can throw after the first install has landed (`transport.Start()`, the profile fetch, `store.Rootlist()`, a service ctor, and `AssertCovers` itself, whose whole job is to throw). `LiveSessionHost.GoLiveAttempt` collects this attempt's ledger + transports + host as they are built; `StartAsync` wraps the core in try/catch and, on a throw (and on the supersede bail), replays the ledger, disposes the transports through the host, and drops `Services.Wiring` / `Services.LiveHost` **only when they are still this attempt's** (`Services.DetachWiring`/`DetachLive` take the instance and compare by reference — two logins race on one shared ct, and a loser must not tear down the winner). Without it, a failed bootstrap left every installed seam pointing at a dead session and the user's retry orphaned the first ledger.

Seam inverses that write a UI-thread `Signal` go through `postUi`: `Uninstall()` runs on whatever thread called `GoOffline` (`LogoutAsync` awaits with `ConfigureAwait(false)`, so a pool thread) — the same rule the installs already followed.

### 2.7 `IOnlineCatalog` (`Wavee.Core/Library/OnlineCatalog.cs`; impl `SpotifyLive/Hydration/SpotifyOnlineCatalog.cs`)
Search / suggest / suggest-rich / home reads (from `LiveSessionHost.FetchSearchAsync`, `FetchSuggestRichAsync`, `FetchHomeAsync`/`LiveHomeCache`). `SwitchableOnlineCatalog` + `OfflineOnlineCatalog` (search → store index; suggest → empty; home → null). `StoreLibrarySource` takes it in the ctor; installed/uninstalled through `LiveWiring`.

---

## 3. After-shapes of the touched seams

- `Services`: `IEntityHydrator Hydrator { get; }` (router over the registry; the Spotify source's `SwitchableEntityHydrator` inner = `SpotifyProviderHydrator` live / `OfflineEntityHydrator` offline); `IOnlineCatalog OnlineCatalog`. Removed: `Metadata`, `TrackAdornments`, `TrackPlayCounts`, `Video`, `ArtistStats`, `ArtistPopularTracks`. Kept return-only: `AlbumEnrichment` (no writers, no 205 projector, no `LiveSessionHost` back-call, no `Excerpt`), `PreRelease`, `TrackCredits`, `TrackExpansion`, `UserProfiles` (thin over the reader; the Owner cache moves to the store in P4), `PlaylistPopcount`, `ContentFilters`, `Concerts`, `Browse`, `WhatsNew`, `HomeSections`, `Recents`, `Friends`, `UserTop` (return-only).
- `StoreLibrarySource(IStore, SwitchableEntityHydrator, IOnlineCatalog)`: `GetXAsync(uri, level=Open)` = `await hydration.EnsureAsync(uri, level, new(Surface: …))` then read; `JoinMembership` = `GetTrack ?? EpisodeAsTrack.From(GetEpisode)`; `GetLikedSongsAsync` = background `EnsureAsync("spotify:collection:tracks", Open)` + join; `GetShowAsync` load-more; no hooks, no predicates, no `EnsureFetchedAsync`.
- `NowPlayingProjection(IEntityHydrator, IStore)`: `if (HydrationLevels.Of(t) >= Open) return; _ = EnsureAsync(uri, Open, new(Surface: NowPlaying, Priority: 1))` → fold `store.GetTrack(uri)`; `EnsureTraitsAsync([uri], NowPlaying)`. `TrackResolver` Func deleted.
- `PlaybackBridge.BumpQueueRevision` → `EnsureTraitsAsync(queueUris, Queue)`; `LiveContextResolver(IEntityHydrator)` → `EnsureManyAsync(uris, Identity, new(Surface: Context))` blocking; fallback `GetTrack ?? EpisodeAsTrack.From(GetEpisode) ?? Placeholder`.
- `DetailPage`: `GetAlbumAsync(uri, Rich)`; `DetailTrailing`: `GetAlbumAsync(uri, Full)`; `DetailModel.Level`; `DetailShell.SetPlaysColumn(true)` → `EnsureTraitsAsync(uris, PlaysToggle)`; `RecentsPage` → `Task.WhenAll(EnsureManyAsync(uris, Identity, new(Surface: Recents)), EnsureTraitsAsync(uris, Recents))`.
- `PlaylistFetcher`/`CollectionFetcher` hydrate delegates: `(uris, ct) => hydrator.EnsureManyAsync(uris, Identity, opts, ct)`.

## 4. Testing conventions
- Engine + ladders + projectors + reader + wiring live under `Backend\**` (auto-compiled) or `SpotifyLive\Hydration\**` (new glob line). Fakes: `RecordingEntityHydrator` (records `(uri, level, surface)`), `FakeExchange`-backed `ExtendedMetadataSource` + `ExtensionEtagCache` (the ApiWaste harness) for wire-shape assertions (decode the gzipped `BatchedEntityRequest`), `InMemoryStore`.
- Request-count pins (`ApiWaste/HydrationWasteTests`): album open cold = V4(+repair) + 1 trait POST; warm = 0; Full = 1 getAlbum (10-min cached); Liked = 1 trait POST per 300; queue bump = only new uris; now-playing thin = TrackV4 → getTrack once, never re-fires; two surfaces × same uris = one in flight; episode in playlist gets EpisodeV4 + the ask-once traits in the same bundle.
- Grep gates: no `StartsWith("spotify:track:")`/`IdOf` outside `EntityUri`; `LiveSessionHost.cs` has no `static … Async` metadata helper; `LiveWiring.AssertCovers(Services.LiveSeams)` passes.

## 5. Coding rules for implementers
Repo comment voice (explain WHY, cite this doc/plan/probe); **no legacy or compat paths — replace outright, delete the old**; `TreatWarningsAsErrors`; NativeAOT-clean (no reflection; new persisted records ride the existing `EntityJson` source-gen); engine-free under `Backend/**` and `SpotifyLive/Hydration/**`; wiring-discipline (required deps, named offline impls, symmetric teardown); component-props-freeze rules unchanged in UI files.
