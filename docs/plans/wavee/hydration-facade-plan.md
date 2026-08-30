# Wavee — one hydration entry point (centralize all metadata fetching/enrichment)

## Status (2026-08-16)

| Phase | State |
|---|---|
| **P0** Identity + contracts | ✅ landed — one `EntityUri`/`EntityKind`/`IdOf`, one batch cap, all ports in `Wavee.Core/Hydration` |
| **P1** Façade + ladders + single hook | ✅ landed — `SpotifyProviderHydrator` + 7 kind ladders + ledger + pump + `OpenPolicy`/`HydrationPolicy`; `LiveSessionHost`'s metadata statics, the 9 `StoreLibrarySource` hooks, `MetadataService`, `IMetadataSource`, `EntityRef`, `StoreEntityGaps`, both artist services and `DiscographyPrefetcher` are deleted |
| **P2** Trait pipeline + reader | ✅ landed — `TraitPipeline` + `ITraitProjector` + 7 projectors + `TraitPolicy` + shared `NegativeMemo` + `ExtensionReader`; the four trait services and `Services.{Video,Metadata,TrackAdornments,TrackPlayCounts,ArtistStats,ArtistPopularTracks}` are gone |
| **P3** Wiring + online catalog + cleanup + docs | ✅ landed — `Backend/Wiring/LiveWiring.cs` + `LiveSeams` (46 named seams, `GoOffline` = `Uninstall()`, `AssertCovers` at the end of go-live; 12 previously-missing teardowns incl. a WASAPI monitor + an arena closure that leaked per login), `IOnlineCatalog`/`SpotifyOnlineCatalog` (search/suggest/home; the last 4 `StoreLibrarySource` hooks gone), docs + the `wavee` skill's `hydration.md` |
| **P4** Multi-source + episodes data + shows + owners | ✅ landed — `HydrationRouter` over `SourceRegistry.OwnerOf` (`Services.Hydrator` in both Create paths; `FakeSource.Owns` = fake provider + `Fallback` capability; real mode has no fallback source), every `// P4:` gate resolved (episodes: deep-link play, autoplay → `autopodcast` (previously dead), audio-format probe; track-only kept for versions drawer / song radio), `Episode.ShowUri` (from EpisodeV4's show ref) so episode rows link to their show, owners in the store (`IStore.UpsertOwner/GetOwner`, `UserHydration`, `SpotifyUserProfileFetch`; `IUserProfileService`/`OnProfileChanged`/`FetchProfileAsync` deleted), show "Load more episodes" paging past 300 |
| **P5** UI: episode rows in playlist lists | ✅ landed — show name subtitle (→ `show:` route via `RichText.RouteForUri`), EPISODE token, no versions chevron, "Go to podcast" (no "Go to album"), episode drag chip, "N songs · M episodes" header |

Final gate 2026-08-16 (after the final review + final-fix waves: go-live rollback, show background paging + load-more cursor, `?.SetInner` hardening, UI-thread signal writes, `MemoryGovernor` race, `PlaybackRuntimeStatus` seam, `Artist.ChartFetchedAt`, `Of(Album)` ordering, `OwnerOf` allocations, cold deep-link permission seed): `dotnet build src/FluentGpu.slnx` Debug + Release clean; `dotnet test src/apps/Wavee.Tests` **4356 passed / 0 failed** (from 3893 at the start); grep gates: no `StartsWith("spotify:track:")` outside `FakeData`, no `IdOf` copies, `LiveSessionHost.cs` holds no metadata helper (two audio probes remain), `LiveSeams` fully covered. Two adversarial review passes (after P1 and after P5) folded in. Note: `FluentGpu.VerticalSlice` shows 3 pre-existing failures from uncommitted engine work in the working tree (`ScrollIntegrator`/`DrawList`/`SceneRecorder`/`GlyphRenderer`) — unrelated to `src/apps`; a clean tree passes.

### Review fixes folded into P1/P2 (beyond the plan as written)

- **Shared runs, not per-caller runs.** `HydrationLedger.Claim(work, level)` returns a `HydrationClaims`: the uris
  this caller CLAIMED (nobody else is fetching them) and the tasks it JOINED. Two partially overlapping callers fetch
  the union once, never the overlap twice. The pass runs **detached**, on the pump's session-linked token, and each
  caller applies its own token only to its *wait* — so a nav-away cancels that caller's await, not the shared pass,
  and every joiner still gets its outcome and its seal. Disposing/failing a claim releases its in-flight slots, so an
  abandoned claim cannot wedge a uri for the session.
- **A transient failure must not earn a genuine-absence seal.** `HydrationRunScope` is the per-run failure channel: a
  best-effort ladder step that swallowed a transport error calls `ctx.ReportTransient(uri)`, and `HydrationPolicy.Ttl`
  then takes the SHORT exhausted window instead of the 24-hour album-Rich one. Without this, one 503 cost a day of
  ©/℗ and row bundles. The scope is explicit on `HydrationContext` (`ForRun`), never ambient — a pump post-step
  outlives the run that queued it.
- **The pump is bounded.** The plan's queue had no ceiling, which is only safe while every producer is a page open —
  and it is not (the ref-closure and `EnsureManyAsync`'s Background mode both enqueue from inside jobs). It now sheds
  at a hard cap (`DefaultCapacity = 4096`), lowest priority first.
- **Background mode re-PLANS at pump time** instead of replaying the plan that was made when it was enqueued: the
  queue is a delay, and a job that went straight to the ladder would re-run a whole pass against a uri another caller
  already sealed. The re-plan is Blocking, so it cannot enqueue again.
- **`TraitBatch` opens its bulk scope lazily** on the first write, so a fully warm page publishes no store change at
  all (the waste tests assert `Writes == 0`).
- **`NegativeMemo` refuses to grow past its cap** rather than evicting — every entry is equally valid forever, so an
  eviction policy would be a (wrong) cache; degrading to "the extension cache's durable 24h negative answers it"
  costs no request either.
- **`TraitApplicability` is ask-once for uncovered pairings.** A pairing the wire probe never covered is asked and the
  404 honoured, not guessed as "never" — guessing is what left every episode with no traits.

### Deviations from the plan as written

1. **Kind 183 rides the album's trait POST, not step 0.** The plan said "V4(+183 fused)"; `AlbumHydration.ExtraCatalogKinds`
   is empty and the trait pass is what lands the publishing facets — so the trait pass is also what carries the album
   from `Open` to `Rich`. Same request count (1 catalogue POST + 1 trait POST), one fewer special case in step 0.
2. **`NowPlayingProjection` takes its dependencies as required constructor params** (`IEntityHydrator`, `IStore`),
   per wiring-discipline: no nullable seam, no `?? Task.CompletedTask`. The bespoke `TrackResolver` Func and its
   divergent thinness predicate are deleted.
3. **`HydrationLedger.Claim` / `HydrationClaims`** are new surface the plan did not name (see the review fixes above).
4. **The pump is bounded and sheds** — the plan described it as "bounded" but specified no capacity or shed policy.
5. **`SpotifyVideoManifestResolver` stayed in `SpotifyLive/` proper**, not `SpotifyLive/Hydration/`: it returns
   FluentGpu media types, and both hydration folders must stay engine-free (they are compiled into `Wavee.Tests`).
   The video *trait* half is `Backend/Hydration/Projectors/VideoProjector.cs`, which is engine-free.
6. **`Services.Hydrator` is still the Spotify `SwitchableEntityHydrator` directly**, not a router — P4 replaces it.
   Until then `SpotifyProviderHydrator` enforces the provider boundary itself (a non-`spotify:` uri is `Unsupported`,
   never sent to spclient — which matters because a local import's uri IS its file path, base64url-encoded).

## Context

The inventory (`scratchpad/wavee-metadata-entry-points-inventory.md`, 2026-08-15) found ~110 fetch/enrich/write entry
points for catalog metadata: 21 album, 16 artist, 37 track, 25 playlist, 13 show/user. The core (one store with
protective merges, `MetadataService.SyncAllAsync`, `ExtensionEtagCache`, `PathfinderResource`) is sound; the sprawl is at the
edge: 14 static helpers in `LiveSessionHost` (`EnsureAlbumAsync`, `FetchAlbumAsync`, `ResolveNowPlayingTrackAsync`,
`DetectHook`, `DetectContainerVideos`, …), 9 mutable hooks on `StoreLibrarySource`, ≥6 unshared "is it cold?" predicates,
2 `KindFor`s, 2 `queryArtistOverview` callers, 3 hook chains for one fan-out, 7 copies of "300 per POST", 7 copies of
"etag-cache-preferred/raw fallback", 6 per-service negative memos, bare nullable seams (`Services.Metadata`,
`TrackAdornments`, `TrackPlayCounts`), a `GoOffline` that misses `AlbumEnrichment`/`Video`/all 9 hooks, and 7 services
that drop anything not `spotify:track:` — so episodes (already playables in the queue) get no hydration, no traits, and
never render in playlists (`JoinMembership` joins `GetTrack` only).

**Goal (user):** ONE point of entry for all metadata hydration + enrichment; caching/speed (batch, dedupe, SWR, one POST
per uri-set carrying every wanted kind); future multi-source; no hardcoded `spotify:track:` gating — episodes are
playables; **delete ALL legacy/duplicate paths outright** (breaking is fine, no shims); a proper "upgrades" model
(hydration levels); it lives in the non-UI layer — **ports in `Wavee.Core`, engine in `src/apps/Wavee/Backend/Hydration`
(engine-free, IStore-level, test-compiled by the `Backend\**` glob), Spotify transports/projectors in `SpotifyLive/Hydration`**
— so queue/now-playing enrichment (`PlaybackProjection`, Backend) uses the same façade and enriched rows flow store → UI.
Decided with the user: XM display-only reads (credits 186, prerelease 138, expansion 98/5/237, user profile 15) go through
ONE `IExtensionReader`; GraphQL display services (merch/similar/NPV/concerts/browse) stay return-only and untouched;
**episode rows also render in playlist lists** (UI phase at the end). Execution: parallel Opus subagents with disjoint file
ownership, coordinator merges shared files, every phase lands green.

---

## 1. Target architecture

```
UI (Features/*)  ──reads store / LibraryStore; asks──►  IMusicLibrary.GetXAsync(uri, HydrationLevel)  ──►  AggregateCatalog ──► StoreLibrarySource(IStore, SwitchableEntityHydrator, IOnlineCatalog, IUserProfileService)
Playback (Backend): PlaybackProjection / PlaybackBridge / LiveContextResolver ─────────────────────────────►  IEntityHydrator (same façade)
                                                                                                                │
                                              Wavee.Core/Hydration (ports): EntityUri·EntityKind, HydrationLevel + HydrationLevels.Of(entity), IEntityHydrator,
                                                                            HydrationOptions/Outcome, TraitSet, TraitSurface, ICatalogSource.Hydrator (DIM)
                                                                                                                │
                                              Backend/Hydration (engine): HydrationRouter (by owner) · HydrationLedger (Resource<(uri,level)>) · HydrationPump ·
                                                                          HydrationPolicy/OpenPolicy · SpotifyProviderHydrator + ladders {Album,Artist,Playable,Playlist,Show,Collection,User}
                                                                          TraitPipeline · ITraitProjector + projectors · TraitPolicy · NegativeMemo · ExtensionReader · LiveWiring
                                                                          ports: ICatalogFetch · IEnvelopeFetch · IArtistChartFetch · IPlaylistOpener · IUserProfileFetch
                                                                                                                │
                                              SpotifyLive/Hydration (adapters): XmCatalogFetch (from MetadataService+ExtendedMetadataSource.FetchAsync), PathfinderEnvelopeFetch,
                                                                                SpclientArtistChartFetch, LibrarySyncPlaylistOpener, SpotifyUserProfileFetch, VisualIdentityProjector,
                                                                                SpotifyOnlineCatalog (search/suggest/home from LiveSessionHost statics), SpotifyVideoManifestResolver
                                              transports kept: ExtendedMetadataSource (Project*), ExtensionEtagCache (REQUIRED), PathfinderResource, LibrarySync, PlaylistFetcher, CollectionFetcher
```

### 1.1 Ports (`Wavee.Core/Hydration/*`)

```csharp
public enum EntityKind : byte { Unknown, Track, Episode, Album, Artist, Playlist, Show, User, Collection, Prerelease, Concert }
public readonly record struct EntityUri(string Uri, string Provider, EntityKind Kind, string Id)   // alloc-free Parse; Provider "spotify"|"local"|"fake"|"user"|"wavee-podcast"|""
{ static Parse/KindOf/IdOf (THE IdOf); bool IsPlayable => Kind is Track or Episode; bool IsContainer; }

public enum HydrationLevel : byte { None, Identity, Open, Rich, Full }
public static class HydrationLevels { Of(Track?) Of(Episode?) Of(Album?) Of(Artist?) Of(Playlist?, bool hasMembership) Of(Show?, …) Of(Owner?); TitleMissing/TrackUnnamed/RefNeedsName (from StoreEntityGaps/StoreEntityMerge) }

[Flags] public enum TraitSet : ushort { None, Video/*99+182(+212 recovery)*/, AudioAttributes/*222*/, Descriptors/*6*/, VisualIdentity/*179*/, PlayCount/*185*/, Publishing/*183 album*/, IdentityTraits/*178+220 wire-fidelity*/, RowBundle = Video|AudioAttributes|Descriptors|VisualIdentity }
public enum TraitSurface { AlbumOpen, PlaylistOpen, LikedSongs, ShowOpen, ArtistPopular, Queue, Search, Recents, NowPlaying, PlaysToggle, TrackExpansion, Credits, PreRelease, UserProfiles }

public enum HydrationMode : byte { Blocking, Background }
public readonly record struct HydrationOptions(HydrationMode Mode = Blocking, bool Revalidate = false, TraitSurface Surface = default, int Priority = 0);
public enum HydrationStatus : byte { Reached, Partial /*ladder ran, level not reached → sealed Exhausted*/, Failed, Cancelled, Unsupported /*offline / no owner / kind not hydratable*/ }
public readonly record struct HydrationOutcome(HydrationLevel Reached, HydrationStatus Status, string? Error = null);
public readonly record struct HydrationBatchOutcome(IReadOnlyCollection<string> Reached, IReadOnlyCollection<string> Missing, HydrationStatus Status);

public interface IEntityHydrator
{
    HydrationLevel LevelOf(string uri);                                                                     // presence-only, store-backed, sync
    Task<HydrationOutcome>      EnsureAsync(string uri, HydrationLevel level, HydrationOptions opts = default, CancellationToken ct = default);
    Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level, HydrationOptions opts = default, CancellationToken ct = default);
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default);          // policy picks the TraitSet
    Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default);
    void Invalidate(string uri);                                                                              // known-better outcome (dealer, video recovery) unseals
}
// Impls: CompleteEntityHydrator.Instance (Core; complete-at-construction sources: SpotifyExport/Local/Fake/UserPlaylist/test fakes → Reached),
//        NotOwnedEntityHydrator (Core), OfflineEntityHydrator(IStore) (Backend; touches store → cold promotion; Reached iff resident satisfies, else Partial/Unsupported(Offline) — never networks, never throws),
//        SwitchableEntityHydrator (Core), HydrationRouter (Core/Backend), SpotifyProviderHydrator (Backend). No nullable seam anywhere (wiring-discipline).
```

`ICatalogSource` gains `IEntityHydrator Hydrator => CompleteEntityHydrator.Instance;` (default interface member — every fake/test source keeps compiling); the four single-item reads on `ICatalogSource`/`IMusicLibrary`/`AggregateCatalog` gain a defaulted `HydrationLevel level = HydrationLevel.Open` parameter.

### 1.2 Levels — one meaning per rung, fixed per kind by `HydrationLevels.Of` (subsumes `IsAlbumOpenReady`, `IsAlbumComplete`, the 4-clause artist gate, `HasMembership`, `IsAttributeLess`, `NowPlayingReady` (both copies), `ArtistStatsCache.IsFresh`, `LibrarySync`'s "unnamed ⇒ cold")

| Kind | Identity | Open (own surface paints its primary content) | Rich (second-transport header facets) | Full (complete envelope) |
|---|---|---|---|---|
| Track | row, `!TitleMissing` | Identity ∧ artists named ∧ `Album.Name != ""` ∧ image usable ∧ duration>0 (= NowPlayingReady + duration) | ≡ Open | Open ∧ `Availability != null` (getTrack/TrackV4 files verdict) |
| Episode | title | Identity ∧ ShowName ∧ image ∧ duration | ≡ Open | Open ∧ Description |
| Album | Name | `Hydration>=Tracks` ∧ tracks>0 ∧ no unnamed track (= IsAlbumOpenReady) | Open ∧ (Copyright ∨ ReleaseDate) (183) | Open ∧ `Hydration==Full` (getAlbum) — restored albums are demoted Full→Tracks by `CachedStore` → Of()=Rich (deliberate) |
| Artist | Name | assembled discography (`TopAlbums[0].Name!=""` ∧ totals ≤ held) | Open ∧ TopTracks>0 ∧ (LatestRelease ∨ PopularReleases) — the overview; age via `Artist.OverviewFetchedAt` ≤12h | Rich ∧ `TopTracks.Count > OverviewSeedCap` (extended chart) |
| Playlist | header Name | Identity ∧ `HasMembership` (LibrarySync stays the freshness authority; ledger never TTL-seals playlist Open) | ≡ | ≡ |
| Show | Name | Identity ∧ HasMembership ∧ first ≤300 episodes at Episode.Open | ≡ | all members at Episode.Open (paged) |
| Collection (Liked, `spotify:user:x:collection`, `spotify:collection:*`) | always | members at Playable.Identity, paged 300, background | ≡ | ≡ |
| User (Owner) | `Owner.Name` | ≡ | ≡ | ≡ |
| Prerelease/Concert/Unknown | `Unsupported` — return-only services stay | | | |

Freshness = presence (`Of`) **and** age (ledger TTL per (kind, level): Identity/Open 1 h (was MetadataService Etag 1h), Artist Rich/Full 12 h, Album Full 10 min, Exhausted 10 min playables / 24 h album Rich). `Artist.FetchedAt`'s three meanings split: `OverviewFetchedAt` (overview landed = merge's `overviewAuthoritative`); the chart's freshness becomes structural (overview refresh shrinks TopTracks → Of()<Full → chart step re-runs); `FetchedAt` remains max-of for persistence only.

### 1.3 Engine (`Backend/Hydration/*`)

- **`HydrationRouter : IEntityHydrator`** — over `SourceRegistry`: routes each uri to the FIRST `ICatalogSource.Owns(uri)` and uses its `Hydrator`; groups a mixed batch by owner then kind; unowned → `NotOwnedEntityHydrator`. `FakeSource.Owns` becomes `EntityUri.Provider == "fake"` (+ `SourceCapabilities.Fallback` and an explicit `AggregateCatalog` fallback step) — kills the `!spotify:` catch-all. `Services.Hydrator` = the router in both `CreateReal`/`CreateFake`.
- **`SpotifyProviderHydrator : IEntityHydrator`** (the Spotify source's hydrator; the inner of `StoreLibrarySource`'s `SwitchableEntityHydrator`, offline inner = `OfflineEntityHydrator`): `HydrationLedger` (`Resource<HydrationKey(locale,uri,level), HydrationOutcome>` — in-flight dedupe, seal on outcome for every level ≤ reached, `Invalidate` = MarkStale; replaces MetadataService's per-uri Resource and ALL six per-service negative memos), `HydrationPump` (bounded background queue, priority, session ct — replaces DiscographyPrefetcher's loop, PagedHydrateAsync, the closure's Task.Run), `HydrationPolicy` (TTL table + `OpenPolicy` kind → (blocking level, background level)), and per-kind ladders. `EnsureManyAsync` step 0 is always the shared `ICatalogFetch` (one XM POST for mixed kinds), then per-kind continuations, then post-steps on the pump.
- **Ladders** (each = ordered steps to a rung + the `Of` predicate):
  - `AlbumHydration`: Identity/Open `[XM AlbumV4] → [TrackV4 repair of unnamed disc rows, rebuild Album.Tracks] → [getAlbum ONLY if still !Open]`; Rich = Open with (uri,AlbumV4)+(uri,183) fused in the first POST + awaited `EnsureTraitsAsync(trackUris, AlbumOpen)` (185 etc. in ONE trait POST — kills the double 185); Full = Rich → `IEnvelopeFetch.AlbumAsync` (getAlbum, `FetchAlbumAsync` body verbatim: UpsertArtist×ArtistsDetailed, UpsertTrack per row, UpsertAlbum Full); post: RowBundle traits on the pump. Below-the-fold: `DetailTrailing` → `GetAlbumAsync(uri, Full)` (Background) — `SpotifyAlbumEnrichmentService` stops calling `LiveSessionHost.FetchAlbumAsync`.
  - `ArtistHydration`: Identity `[XM ArtistV4]`; Open → own-discography stub AlbumV4 + `ArtistDiscography.Assemble`; Rich → appears-on ≤20 + `IEnvelopeFetch.ArtistOverviewAsync` (THE one `queryArtistOverview` caller; stats-only upsert as `SpotifyArtistStatsService` does today, `OverviewFetchedAt=now`) + Assemble; Full → `IArtistChartFetch.TopTrackUrisAsync` → `EnsureManyAsync(uris, Identity)` → `ArtistPopularTracks.Merge` → `UpsertArtist(TopTracks)` → `EnsureTraitsAsync(chartUris, ArtistPopular)` (185 in the same POST). Consumers: library pane Open, ArtistPage Rich, `ArtistPopular` Full, Home expander Rich, album "fans also like" → `GetArtistAsync(lead, Rich)` then `Extras.Related` (kills the second overview caller). Prefetch = `EnsureManyAsync(savedArtists, Open, Prefetch)` + `EnsureManyAsync(ownAlbums, Open, Prefetch)`.
  - `PlayableHydration` (track + episode, no `spotify:track:` gating): Identity `[XM TrackV4|EpisodeV4]`; Open → track: `getTrack` ONLY if still !Open (the now-playing repair; Exhausted seal 10 min replaces the heartbeat gate — one predicate for projection AND resolver); episode: no second transport; Full (track) = getTrack. Post-step (pump, depth-bounded by construction): the ref-closure — `RefNeedsName(Album)` → `EnsureManyAsync(albumUris, Identity, Background)`; `TrackUnnamed` → `EnsureManyAsync(uris, Open, Background)`; 300/batch ≤900/pass; the ledger is the session dedupe.
  - `PlaylistHydration`: Identity = rootlist member ? `IPlaylistOpener.HeaderAsync` : XM 205 via `ICatalogFetch` (`ExtendedMetadataSource.ProjectPlaylist` = THE one 205 projector); Open = `!HasMembership ? await opener.OpenAsync (LibrarySync.OpenPlaylistAsync, blocking) : opener.Revalidate` (LibrarySync's 5-min/dirty gates); post: `EnsureTraitsAsync(members incl. episodes, PlaylistOpen)`. The hydrator never writes membership (LibrarySync owns the plane); `sync.OnPlaylistHydrated` deleted.
  - `ShowHydration`: Identity `[XM ShowV4]` (header + membership); Open = `EnsureManyAsync(members[..300], Open)` awaited + `RecordRecentSurface(showUri)` (fixes the membership-GC purge); Full = remaining members paged 300 on the pump; `GetShowAsync` load-more → `EnsureManyAsync(members[300k..], Open)`.
  - `CollectionHydration`: Open = `EnsureManyAsync(SavedUris(set), Identity, Background, pages 300)` + `EnsureTraitsAsync(rows, LikedSongs)` (= today's PagedHydrateAsync(liked) + libSrc.HydrateMembers + libSrc.DetectVideos, addressed by uri).
  - `UserHydration`: Identity = `IUserProfileFetch.ResolveAsync(ids)` (kind 15 batch + REST fallback) → **`IStore.UpsertOwner/GetOwner`** (new hot+cold entity, ~40 lines) under BeginBulk; `StoreLibrarySource.OverlayOwner/PrefetchPlaylistUsers` read `GetOwner` and fire `EnsureManyAsync(userUris, Identity, Background)`; deletes `IUserProfileService`'s private cache, `Changed`, `OnProfileChanged`'s `store.Bump` (the read-source-that-writes), and the 3rd profile fetch (`FetchProfileAsync` → `EnsureAsync("spotify:user:"+me, Identity)`).
- **`TraitPipeline`** (`ITraitPipeline.EnsureAsync(uris, TraitSet, TraitSurface, ct)`): per uri, the projectors that apply (`TraitApplicability` table — track/album/artist pinned from the probe; **episode = ask-once, honor the 404**), aren't marked (`ITraitProjector.AlreadyHas` per-kind store mark: 222→TempoBpm, 6→Tags (empty list writes `Tags=[]`), 179→`CoverColorPlane.HasFreshDark(rowImageUrl)`, 99→plane fresh, 185→PlayCount>0, 183→Copyright∧ReleaseDate), and aren't in the ONE `NegativeMemo` (bounded 64k, shared with the reader). Then ONE `ExtensionEtagCache.GetAsync` per ≤`MetadataChunking.MaxEntitiesPerRequest`(300) uris carrying every wanted kind (+companions, e.g. 182 with 99) under each EntityRequest, `client-feature-id` from `TraitSurfaces.ClientFeatureId(surface)` (Recents→`mdata_esperanto`, track traits→`track_metadata_loader`), projection under ONE lazy `BeginBulk` per page (`TraitBatch.Write` — an all-hits page emits no store change), `TraitOutcome {Applied, Unchanged, Negative, NotResident}` (Negative/Unchanged → memo; NotResident never memoized, never minted), then `CompleteBatchAsync` (Video canonical recovery via the reader, once per alias — a Missing never downgrades a resident `HasVideo:true`), structured `traits.batch` log with per-kind/per-EntityKind negatives (how the episode table gets pinned later). **`ExtensionEtagCache` becomes required** — the raw-fallback branch dies everywhere. `TraitPolicy(Func<bool> playsColumnOn).For(surface)` is the ONE surface→TraitSet table (AlbumOpen = RowBundle|PlayCount|Publishing; Playlist/Liked = RowBundle|(PlayCount iff setting); ShowOpen = RowBundle; ArtistPopular = RowBundle|PlayCount; Queue/Search = RowBundle; Recents = IdentityTraits|VisualIdentity; NowPlaying = Video; PlaysToggle = PlayCount).
- **Projectors** (`Backend/Hydration/Projectors/*`, code moves verbatim): `VideoProjector` (99+182; `SpotifyVideoService.Fold/Project/RecoverCanonicalAsync/DetectTally`), `AudioAttributesProjector` (222), `DescriptorProjector` (6), `PlayCountProjector` (185; `OnPlatformReputation` decoder), `PublishingProjector` (183; `AlbumPublishing.Apply`), `IdentityTraitsProjector` (178/220 no-op), and `SpotifyLive/Hydration/VisualIdentityProjector` (179 → CoverColorPlane). `TraitProjectors.Default(reader, plane)` is the single registry factory (go-live + tests).
- **`ExtensionReader : IExtensionReader`** — `ReadAsync<T>(uri, kind, parse, surface, ct, ReadOptions{Revalidate})`, `ReadManyAsync<T>`, `ReadRawAsync(reqs)` (multi-kind, e.g. expansion 99/98/5/237 in one POST), `Seed<T>` (prerelease's 3-key publish); parsed-answer LRU incl. null answers, correct TCS-slot in-flight coalescer (fixes the stranded-slot bug), negatives in the shared memo, `client-feature-id` on every arm. Credits/PreRelease/Expansion/UserProfileFetch become thin parsers over it (expansion finally gets the etag cache; its dead 222 target ask is dropped).
- **`LiveWiring`** (`Backend/Wiring/LiveWiring.cs`): `Set(name, install, uninstall)` / `Swap<T>(name, setInner, live, offlineFactory)` record every go-live install with its inverse; `Uninstall()` reverse-order, idempotent, exception-isolated; `AssertCovers(Services.LiveSeams)` at the end of `StartAsync`. `GoOffline` = `Wiring.Uninstall()`. Every install today lacking a teardown (`AlbumEnrichment`, `Video`, the 9 hooks, `Playback.DetectVideos/ResolveVideoSource/RepublishConnectState`, `CoverColorPlane.Filler`, `HomeFacet`, `HomeFeedRevalidate`, …) is registered.
- **`IOnlineCatalog`** (Core; `SwitchableOnlineCatalog` + `OfflineOnlineCatalog`: search → store index, suggest → empty, home → null) replaces `StoreLibrarySource.LiveSearch/LiveSuggest/LiveSuggestRich/LiveHomeFetch`; `SpotifyLive/Hydration/SpotifyOnlineCatalog` absorbs `FetchSearchAsync`, `FetchSuggestRichAsync`, `FetchHomeAsync`/`LiveHomeCache`. (Reads, not hydration — kept as their own port; a future `ISearchSource` on the registry is compatible.)

### 1.4 `StoreLibrarySource` after

```csharp
public StoreLibrarySource(IStore store, SwitchableEntityHydrator hydration, IOnlineCatalog online)   // all REQUIRED
GetAlbumAsync(uri, level=Open) { await _hydration.EnsureAsync(uri, level, new(Surface: TraitSurface.AlbumOpen), ct); return _store.GetAlbum(uri); }   // same for Artist/Playlist/Show
StreamTracksAsync(uri)  → EnsureAsync(uri, Open)  (play path never waits on Rich)
GetLikedSongsAsync()    → _ = EnsureAsync("spotify:collection:tracks", Open, Background); join
JoinMembership          → _store.GetTrack(uri) ?? EpisodeAsTrack.From(_store.GetEpisode(uri))   // episodes in playlists (count, mosaic, play context)
// EnsureFetchedAsync, IsAlbumOpenReady/IsAlbumComplete/HasUnnamedTrack/HasAnyPlayCount, OnDemandFetch/Sync/HydrateMembers/DetectVideos/UserProfiles hooks, GetDiscographyAsync's album-uri detect call: DELETED
```

`OpenPolicy` (one table) fixes blocking vs background per kind: album open awaits Rich (parity with today: star + ©/℗ at first paint, fewer POSTs), Full only from `DetailTrailing`; artist Open blocking, Rich for the standalone page; playlist Open blocking iff no baseline; show Open blocking (ShowV4 + first 300 episodes).

### 1.5 Consumers
- `NowPlayingProjection(IEntityHydrator, IStore)`: `if (HydrationLevels.Of(t) >= Open) return; _ = EnsureAsync(uri, Open, new(Surface: NowPlaying, Priority: 1))` then fold `store.GetTrack(uri)`; `TrackResolver` Func + its divergent predicate deleted; `EnsureTraitsAsync([uri], NowPlaying)` for the video warm. Episodes flow through the same call.
- `PlaybackBridge.BumpQueueRevision` → `EnsureTraitsAsync(queueUris, Queue)`; thin queue rows → `EnsureManyAsync(uris, Open, Background)`. `LiveContextResolver(IEntityHydrator)`: `EnsureManyAsync(uris, Identity)` blocking; `HydrateAsync` fallback `GetTrack ?? EpisodeAsTrack.From(GetEpisode) ?? Placeholder`.
- `DetailPage`: `GetAlbumAsync(uri, Rich)`; `DetailTrailing`: `GetAlbumAsync(uri, Full)`; `DetailModel.Level = svc.Hydrator.LevelOf(uri)` so `Skel.Region` shimmers only while `Level < Open` and repaints in place on upgrade (existing store-change re-map). `DetailShell.SetPlaysColumn(true)` → `EnsureTraitsAsync(uris, PlaysToggle)`. `RecentsPage.HydrateAsync` → `Task.WhenAll(EnsureManyAsync(uris, Identity, new(Surface: Recents)), EnsureTraitsAsync(uris, Recents))` (178/220 kept for wire fidelity, 179 finally projected; `headerTraits`/`HeaderTraitKinds` deleted).
- Sidebar `wavee.artist.topTracks` source, Home top-artist expander, `ArtistPage`, `ArtistPopular` → `GetArtistAsync(uri, level)`.

### 1.6 What survives / what dies

**Survives (as internals):** `ExtendedMetadataSource` (transport + `Project*`, `CanonicalUriOf` = the one decoder), `ExtensionEtagCache` (required; `GetPayloadAsync` gains `clientFeatureId`), `MetadataChunking` (+ `MaxEntitiesPerRequest=300`, `ExtensionRanges` moved in — entities AND bytes), `PathfinderResource`, `LibrarySync`/`PlaylistFetcher`/`CollectionFetcher` (hydrate delegates typed `IEntityHydrator`), `ArtistDiscography.Assemble`, `ArtistPopularTracks.Merge/WithPlayCounts/UrisWithoutPlayCount` (pure), `SpotifyExportMapper` (mappers), `StoreEntityMerge` (+ `OverviewFetchedAt`), `Resource<,>` (`Etag`/`PollWhole`/`Immutable`), return-only services (`AlbumEnrichment` minus its writers/205 projector/`Excerpt`, `PreRelease`, `TrackCredits`, `TrackExpansion`, `Concerts`, `Browse`, `WhatsNew`, `Popcount`, `ContentFilters`, `HomeSections`, `Recents`, `Friends`, `UserTop` made return-only), `SpotifyVideoService` shrunk to `SpotifyVideoManifestResolver` (playable resolve).

**Deleted outright:** `LiveSessionHost` statics `EnsureAlbumAsync`, `FillAlbumAdornments`, `AlbumTrackUris`, `FetchAlbumAsync`, `ResolveNowPlayingTrackAsync`, `PagedHydrateAsync`, `DetectHook`, `LogDetectSurface`, `DetectContainerVideos`, `HydratePlaylistHeadersAsync`, `FetchProfileAsync`, `FetchSuggestAsync` (dead), `FetchSearchAsync`/`FetchSuggestRichAsync`/`FetchHomeAsync`/`LiveHomeCache` (→ `SpotifyOnlineCatalog`), `RunAsync` CLI demo (or moved to a probe file); `StoreLibrarySource` hooks + `EnsureFetchedAsync` + all predicates; `MetadataService` (whole class: `Use/EnsureAsync` zero callers, per-uri Resource, second `KindFor`; its conditional arm + `ProjectCachedExtensions` → `XmCatalogFetch`), `EntityRef`, `IMetadataSource` (→ `ICatalogFetch`), `FreshnessPolicy.RevisionDelta/SnapshotRevision`; `SpotifyArtistStatsService`, `SpotifyArtistPopularTracksService`, `IArtistStatsService`/`ArtistStatsCache`/`IArtistPopularTracksService` + Switchable/Null; `ArtistDiscography.EnsureAsync`, `DiscographyPrefetcher`; `SpotifyTrackAdornmentService`, `TrackPlayCounts.cs` (source+hydrator; decoder → projector), `AlbumPublishing.cs` (→ projector), `SpotifyVideoService` detect/get/fold/recover (→ projector), `IVideoService`/`SwitchableVideoService`/`NoVideoService`/`Services.Video`; `Services.Metadata/TrackAdornments/TrackPlayCounts/ArtistStats/ArtistPopularTracks`; `IUserProfileService`'s cache/`Changed`/`OnProfileChanged`; `PlaybackBridge.DetectVideos`, `LibrarySync.OnPlaylistHydrated`, `NowPlayingProjection.TrackResolver`; `StoreEntityGaps` (→ `HydrationLevels`); the 2nd 205 projector + `Cover` picker + `Excerpt`; the 2nd `queryArtistOverview` caller; the 6+ `IdOf`s; the 7 "300" constants; the 7 etag-or-raw copies; the 6 negative memos; `VideoAssociation.RevalidationEtag`; `headerTraits`/`HeaderTraitKinds`; every production `StartsWith("spotify:track:")` gate (a grep-test enforces none outside `EntityUri`).

---

## 2. Phases (each lands green; Opus agents with disjoint files; coordinator owns `Services.cs`, `LiveSessionHost.StartAsync`, `Wavee.Tests.csproj`)

Gate per phase: `dotnet build src/FluentGpu.slnx` (Debug **and** Release) + `dotnet test src/apps/Wavee.Tests` (baseline 3893; every deleted test named with its replacement) + an Opus review agent over the phase diff. Agents work in worktrees; merge order fixed; coordinator applies shared-file wiring last.

| Phase | Goal | Agents (owned files) | Deletes | Tests |
|---|---|---|---|---|
| **P0 Identity + contracts** (~1 day) | one `EntityUri`/`EntityKind`/`IdOf`; one batch cap; land all ports with no consumers | **P0-A** `Wavee.Core/Hydration/{EntityUri,HydrationLevel(+HydrationLevels),IEntityHydrator,Traits,Hydrators(Complete/NotOwned/Switchable)}.cs`, `Backend/Hydration/{OfflineEntityHydrator,OpenPolicy}.cs`, `Backend/Metadata/Metadata.cs` (`EntityRef.Parse`=`EntityUri`, `MaxEntitiesPerRequest`, `ExtensionRanges` in), `EntityUriTests`, `MetadataChunkingTests` · **P0-B** sweep `Backend/**` `StartsWith("spotify:` → `EntityUri` · **P0-C** sweep `SpotifyLive/**`+`App/**` · **P0-D** sweep `Features/**`,`Components/**`,`Actions/**`,`Wavee.Core/**` (`Owns` → `EntityUri.Provider`, `SpotifyExportMapper.IdFromUri` → `EntityUri.IdOf`) | 6+ `IdOf`, `EntityRef.KindOf`, one `KindFor` | `EntityUriTests` (every scheme incl. episode/user/local/wavee/fake; `IsPlayable`; alloc-free), chunking entity-cap case |
| **P1 Façade + ladders + single hook** (~5 days) | every open/hydrate path calls `IEntityHydrator`; `LiveSessionHost` metadata statics gone; bare seams gone; existing trait services survive temporarily as rung executors | **P1-A** Album+Artist: `Backend/Hydration/{AlbumHydration,ArtistHydration}.cs`, `HydrationLevels` album/artist arms, `Backend/Hydration/Ports.cs` (`IEnvelopeFetch`,`IArtistChartFetch`), `SpotifyLive/Hydration/{PathfinderEnvelopeFetch,SpclientArtistChartFetch}.cs`, `SpotifyAlbumEnrichmentService.cs` (strip writers/205/Excerpt/back-call), `Wavee.Core/Domain/Models.cs` (`OverviewFetchedAt`), `Store.cs` artist merge, delete `ArtistStats*`/`ArtistPopularTracksService`/`ArtistDiscography.EnsureAsync`/`DiscographyPrefetcher`; tests `AlbumHydrationTests`,`ArtistHydrationTests` (absorb `DiscographyPaginationTests.AlbumGate_*`, `ArtistStatsPlayCountTests` wire cases, `ArtistPopularTracksTests.Ensure_*`) · **P1-B** Playable+Playlist+Show+Collection + engine: `Backend/Hydration/{HydrationRouter(spotify-only until P4),HydrationLedger,HydrationPump,HydrationPolicy,SpotifyProviderHydrator,PlayableHydration,PlaylistHydration,ShowHydration,CollectionHydration}.cs`, `Ports.cs` (`ICatalogFetch`,`IPlaylistOpener`), `Backend/Metadata/XmCatalogFetch.cs` (from `MetadataService` + `ExtendedMetadataSource.FetchAsync`; ONE `XmKinds.CatalogKindOf`), `SpotifyLive/Hydration/LibrarySyncPlaylistOpener.cs`, `Backend/PlaybackProjection.cs`, `SpotifyLive/LiveContextResolver.cs`, `PlaylistFetcher`/`CollectionFetcher` delegates, delete `MetadataService.cs`/`EntityRef`/`IMetadataSource`/`StoreEntityGaps`; tests `HydrationRouterTests`,`HydrationLedgerTests`,`PlayableHydrationTests`,`PlaylistHydrationTests`,`ShowHydrationTests`,`CollectionHydrationTests`,`XmCatalogFetchTests` (absorb `MetadataTests` seal/partial-cache/MarkStale, `MetadataSourceTests` recents-flavour), `LiveContextResolverTests` · **P1-C** Consumers: `Backend/Library/StoreLibrarySource.cs` (ctor + `GetXAsync(level)` + JoinMembership fallback; keep online hooks until P3), `Wavee.Core/Sources/{ICatalogSource,AggregateCatalog}.cs` (`level` param, `Hydrator` DIM), `App/PlaybackBridge.cs`, `Features/Detail/{DetailShell,DetailTrailing,DetailPage,ArtistPage,ArtistPopular}.cs`, `Features/Home/HomeModules.Artists.cs`, `Features/Recents/RecentsPage.cs`, `Features/Sidebar/Data/*`; `StoreLibrarySourceTests` (hooks → `RecordingEntityHydrator` asserting (uri, level, surface)) · **Coordinator** `Services.cs` (ctor `IEntityHydrator`, delete 5 seams, `GoOffline` resets the switchable), `LiveSessionHost.StartAsync` (build `SpotifyProviderHydrator`, `SetInner`, delete DetectHook×4/DetectContainerVideos×2/hooks/OnPlaylistHydrated/TrackResolver), csproj (add `SpotifyLive\Hydration\**` glob; drop deleted files; explicit include for the return-only `SpotifyAlbumEnrichmentService.cs`) | LiveSessionHost statics 798–1436, the 9 hooks, predicates, `MetadataService`, both stats services, prefetcher, `Services.Metadata/ArtistStats/ArtistPopularTracks` | + `ApiWaste/HydrationWasteTests` (album open cold ≤ V4 + repair + traits; warm = 0; now-playing thin resolves once, never re-fires; two surfaces same uris → one in-flight) |
| **P2 Trait pipeline + reader** (~4 days; dev overlaps P1 in worktrees) | ONE batching/etag/negative-memo/300-cap/feature-id path for per-playable XM traits; the 4 trait services deleted; `Services.Video` deleted | **P2-A** `Backend/Hydration/{TraitPipeline,ITraitProjector,TraitPolicy,NegativeMemo,ExtensionReader,TraitProjectors}.cs`, `ExtensionEtagCache.cs` (`GetPayloadAsync` cfid; required), `Wavee.Core/Domain/Video.cs` (drop `RevalidationEtag`); `TraitPipelineTests`,`ExtensionReaderTests` · **P2-B** `Backend/Hydration/Projectors/*.cs`, `SpotifyLive/Hydration/{VisualIdentityProjector,SpotifyVideoManifestResolver}.cs`, `CoverColorPlane.HasFreshDark`; delete `SpotifyTrackAdornmentService.cs`, `TrackPlayCounts.cs`, `AlbumPublishing.cs`, `SpotifyVideoService` detect half, `Wavee.Core/Library/VideoService.cs`; migrate `VideoAssociationTests`→`VideoProjectorTests`, `TrackPlayCountHydratorTests`→`PlayCountProjectorTests`, `AlbumPublishingTests`→`PublishingProjectorTests`, `TrackPlayCountTests` (decoder only) · **P2-C** `SpotifyTrackCreditsService`, `SpotifyPreReleaseService`, `SpotifyTrackExpansionService`, `SpotifyUserProfileService` (thin over `IExtensionReader`); `TrackCreditsTests`/`PreReleaseWireTests` (+coalescing) · **Coordinator** ladders' trait rungs → `TraitPipeline` (album Rich = one merged POST); `Services.Video` removed; go-live constructs pipeline+reader+policy; csproj protos for projectors | the 3 hook chains' remains, `playsWanted` ×2, 7 caps, 7 etag-or-raw copies, 6 memos, `headerTraits`, `RevalidationEtag` | `TraitPipelineTests`: one POST carries all kinds per uri; marks suppress; album gets 183 / tracks never; episodes asked once + 404 honored; memo shared+bounded; cfid per surface; 301 uris ⇒ 2 POSTs/2 bulks; all-hits ⇒ no bulk; never mints; failure memoizes nothing. `HydrationWasteTests` tightened: album open = 2 XM POSTs; Liked = 1 per 300; queue bump only new uris |
| **P3 Wiring + online catalog + cleanup + docs** (~3 days) | symmetric install/uninstall; search/home off `StoreLibrarySource`; dead code + docs | **P3-A** `Backend/Wiring/LiveWiring.cs` (+`LiveWiringTests`); sole editor of `LiveSessionHost.StartAsync` this phase (every install → `wiring.Swap/Set`, `AssertCovers(Services.LiveSeams)`), `Services.cs` (`GoOffline` = `Uninstall()`; `LiveSeams`) · **P3-B** `Wavee.Core/Library/OnlineCatalog.cs`, `SpotifyLive/Hydration/SpotifyOnlineCatalog.cs` (from the LiveSessionHost search/home statics), `StoreLibrarySource` (4 hooks → ctor `IOnlineCatalog`), `SpotifyUserTopService` return-only; `OnlineCatalogTests` · **P3-C** `Backend/Resource.cs` (delete dead policies + tests), docs: `docs/plans/wavee/architecture.md` (§4.2 ports `IEntityHydrator`/`IOnlineCatalog`, §9 matrix, §10 file map), `entity-data-layer-redesign.md` status, `.claude/skills/wavee/SKILL.md` hub + new `hydration.md` sub-skill (façade, levels, surfaces, LiveWiring rules), fix the 5 stale `docs/architecture.md` pointers, `wavee-data-gaps.md` note, `xm-kind-probe-overview.md`/`xm-playcount-handoff.md` status | `FetchSearchAsync`+friends, `FetchSuggestAsync`, `RunAsync`, dead `Resource` policies, `ResourceFreshnessTests` (3) | `LiveWiringTests` (reverse order, idempotent, exception isolation, `AssertCovers` names the missing seam) |
| **P4 Multi-source + episodes data + shows + owners** (~3 days) | router over the registry; Fake fix; episode join; show paging; owners in the store | **P4-A** `Wavee.Core/Hydration/HydrationRouter.cs` over `SourceRegistry`, `SourceCapabilities.Fallback`, `FakeSource.Owns`, `AggregateCatalog` fallback, `Services.Hydrator = router` in both Create paths; `HydrationRouterTests`,`FakeSourceOwnsTests`,`AggregateFallbackTests` · **P4-B** `Wavee.Core/Domain/EpisodeAsTrack.cs` (`From(Episode)`: Id=episode id, `Artists=[]`, `Album=AlbumRef("", showUri, ShowName)`, image, duration, `Source="podcast"`), `StoreLibrarySource.JoinMembership` fallback, `LiveContextResolver.HydrateAsync` + `EmptyContextResolver` fallback, `PlayableHydration` episode arms; `EpisodeInPlaylistJoinTests`, `PlaybackProjection`/`PlaybackBridge` residue → `IsPlayable` · **P4-C** `ShowHydration` Full paging + `GetShowAsync` load-more + `RecordRecentSurface`; `UserHydration` + `IStore.UpsertOwner/GetOwner` (InMemoryStore/CachedStore/SqliteColdStore ~40 lines) + `StoreLibrarySource.OverlayOwner`; delete `IUserProfileService` cache/`Changed`/`OnProfileChanged`; `ShowEpisodePagingTests`,`UserHydrationTests` | `FakeSource` catch-all, `IUserProfileService` internals, `FetchProfileAsync` | as listed |
| **P5 UI: episode rows in playlist lists** (~2 days) | a playlist with episodes renders and plays its episode rows | **P5-A** `Components/TrackRow.cs` (`MetadataLine`/`ArtistLinks`/`AlbumLink` route by `EntityUri.Kind`: show uri → `show:` route; type token `Strings.Detail.Release.Episode`/`Strings.Podcast.Show`; published date in the Date lane when `AddedAt` absent), `Features/Detail/DetailTracks.cs` (expand chevron gated `IsPlayable && Kind==Track` at ~L3000; drag chip `WaveeResourceKind.Episode`), `Actions/Menus.cs` ("Go to podcast" when episode), `DetailPage.MapPlaylist` mixed meta line (`podcast.episodeCount`) · **P5-B** `Features/Detail/EpisodeList.cs` paging trigger (from P4-C), Recents/Search reuse checks | — | `RowAffordanceGrammarTests` symmetric; UI smoke on screen. Progress/"played" state stays out (no `Episode.ProgressMs` writer exists — Herodotus only at play time) |

Serialization rules: P0 before P1/P2 (both key on `EntityUri`); P1 before P2's coordinator swap (P2 agents start in worktrees during P1); P3 after P2; P4/P5 after P3. Coordinator-only files: `App/Services.cs`, `LiveSessionHost.StartAsync` (L62–660), `Wavee.Tests.csproj`; agents put their wiring lines in their report. Statics below L700 in `LiveSessionHost` are deleted by the agent owning the replacement (disjoint hunks).

---

## 3. Efficiency after (what the request-count tests pin)

| Open | Today | After |
|---|---|---|
| Album (V4-first) | V4 + 185 + 183 + video + adorn + second 185 = up to 6 POSTs (+ getAlbum fallback) | V4(+183 fused) + ONE trait POST (99/182/179/222/6/185 × tracks) = 2; warm = 0; Full = 1 getAlbum from below-the-fold, 10-min cached |
| Liked / playlist 10k | 2–3 POSTs per 300 rows (68–102) | 1 per 300 (34); re-open with marks warm: 0 |
| Queue change / search | 2 | 1 (only uris not fresh) |
| Artist page | V4 + overview (+ REST + V4 + 185 + video + adorn for the chart) | Open/Rich unchanged; chart REST + V4 + 1 trait POST; album "fans also like" −1 overview |
| Show open (300 episodes) | hooks drop all uris (0 traits) | first ever 1 POST of ask-once kinds → 404s memoized+persisted; then 0 |
| Recents pump (≤64) | 1 | 2 concurrent (V4/205; traits) — 179 finally projected |
| Now-playing thin | TrackV4 → getTrack, can re-fire per heartbeat | TrackV4 → getTrack once; Exhausted seal, both predicates unified |
| Store change signals | 1 per service per slice | 1 lazy bulk per trait page (none on all-hits) |

---

## 4. Risks (decided)

1. **Blocking vs background** — `OpenPolicy` is the ONE table; album open awaits Rich (parity), Full below-the-fold; flip to "await Open, background Rich" later is a one-line change + measurement.
2. **LibrarySync owns the playlist plane** — `PlaylistHydration` never writes membership; ledger never TTL-seals playlist Open (test pins it).
3. **`CachedStore` Full→Tracks demotion** — restored album `Of()`=Rich; Full re-fetched only below-the-fold within the 10-min PF cache; if it bites, persist Label/ArtistsDetailed and stop demoting (predicate is the only place to change).
4. **`Artist.FetchedAt` split** — `OverviewFetchedAt` as optional record param; old cold blobs → one extra overview after upgrade; no schema migration.
5. **ApiWaste tests** pin batching — updated only in the phase changing the wire (P2), counts stated in the PR; P1 adds "no (uri,kind) requested twice per session".
6. **`Services.CreateFake`/`SpotifyExportSource`** — every complete source exposes `CompleteEntityHydrator`; router returns `Reached`; no nullable seam.
7. **Episode applicability** — ask-once + 404 memo (24 h persisted Missing); first show open pays one POST; per-kind/per-EntityKind negatives logged so the table can be pinned later.
8. **Exhausted seals hide fixable data for their TTL** — `Invalidate(uri)` is the escape hatch; dealer/video recovery call it where they `MarkStale` today.
9. **Big-bang signatures** (`GetXAsync(level)`, `LiveContextResolver`, `NowPlayingProjection`, fetcher delegates) — breaking is allowed; phases keep every step compiling.
10. **NativeAOT + TreatWarningsAsErrors** — the gate builds Debug and Release; `LiveWiring` is lambdas; DIMs are AOT-fine; new `Owner` entity rides the existing `EntityJson` source-gen context.
11. **Test compile coverage** — all hydration logic lands under `Backend\**` (glob) or `SpotifyLive\Hydration\**` (new glob, engine-free); `LiveSessionHost` stops holding logic.

## 5. Verification (per phase + final)
```powershell
dotnet build src/FluentGpu.slnx ; dotnet build src/FluentGpu.slnx -c Release      # both clean
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj                                # 0 failed; deleted tests named with replacements
```
Grep gates (tests): no production `StartsWith("spotify:track:")` outside `EntityUri`; no `IdOf` outside `EntityUri`; `LiveWiring.AssertCovers(Services.LiveSeams)` passes; `LiveSessionHost.cs` contains no `static … Async` metadata helper.
On screen (live login): album cold open paints rows+plays+©/℗ (2 XM POSTs in the metadata log), About/OtherVersions arrive below the fold; artist page + chart; playlist with episodes shows episode rows (show name, EPISODE token, plays via the playlist context, "Go to podcast"); Liked with the Plays column on fills in ONE POST per 300; queue rows tint/tempo/video from one POST; now-playing thin episode resolves without a raw-uri title; logout → `GoOffline` leaves no live hook (search falls back to the store index, no exceptions); re-login re-installs. `--screenshot` is unusable for Wavee (captures black).
