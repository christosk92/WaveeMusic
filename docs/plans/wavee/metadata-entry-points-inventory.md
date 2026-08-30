# Wavee — metadata entry-point inventory (research only, 2026-08-15)

Scope: `src/apps/Wavee`, `Wavee.Core`, `Wavee.Tests`, `docs/`. Every path that **fetches, hydrates, enriches or writes**
catalog metadata (album · artist · track · playlist · show/episode · user), the layers around them, and where they overlap.
Line numbers are from the working tree at the time of the sweep (includes today's 185/183/186 work). Kinds are
extended-metadata (`XM`) kinds from `SpotifyLive/Protos/extension_kind.proto`; `PF` = Pathfinder GraphQL persisted op.

---

## 0. TL;DR — the shape of the problem

There are **two transports** (XM `POST /extended-metadata/v0/extended-metadata` and Pathfinder GraphQL, plus a handful of
spclient REST GETs and the dealer socket), **one store** (`IStore` → `CachedStore` hot/cold), and **one intended
chokepoint** (`MetadataService.SyncAllAsync`). Around that core, the code has grown:

| Dimension | Count today | Where the sprawl is |
|---|---|---|
| Album fetch/write entry points | **21** | 5 of them are static helpers in `LiveSessionHost` (`EnsureAlbumAsync`, `FillAlbumAdornments`, `FetchAlbumAsync`, `DetectContainerVideos`, `PagedHydrateAsync`); getAlbum is issued from 2 places; `SpotifyAlbumEnrichmentService` reaches back into `LiveSessionHost.FetchAlbumAsync` |
| Artist fetch/write entry points | **16** | `queryArtistOverview` issued by 2 services with different freshness gates; 3 stats-only artist writers; `Artist.FetchedAt` carries 3 meanings |
| Track fetch/write entry points | **37** | 7 uncoordinated thin-track writers; 4 per-trait services (222/6/179, 99/182/212, 185, 186) + expansion (98/5/237) + pre-release (138) each with its own batch cap, negative memo and etag-cache/raw fallback copy |
| Playlist entry points | **25** | two disjoint "open a container" paths (`Sync` vs `OnDemandFetch`); two independent LIST_METADATA_V2 projectors |
| Show/episode + user | **13** | shows ride the playlist membership plane; 3 copies of the profile fetch |
| Predicates for "is this entity cold enough to fetch?" | **≥6**, none shared | `EnsureFetchedAsync` inline ×4, `IsAlbumOpenReady`, `IsAlbumComplete` (dead in prod), `NowPlayingReady` (+ a divergent twin in `PlaybackProjection`), `LibrarySync` "unnamed ⇒ cold" |
| Hook chains that fan out "detect video + adorn + plays" | **3** (`DetectHook` ×4 wirings, `DetectContainerVideos` ×2 wirings, `sync.OnPlaylistHydrated`) | plus 2 copies of the "does this surface want plays" policy |
| Metadata logic living in the composition root (`LiveSessionHost.cs`, 1,499 lines) | **14 static helpers** + a 260-line go-live wiring block | not injectable, not unit-testable, one dead (`FetchSuggestAsync`) |
| Copies of "300 entities per POST" | 7 | none shared; `MetadataChunking` chunks by bytes only |
| Copies of "etag-cache preferred, raw source fallback" | 7 | with inconsistent `client-feature-id` |
| Per-service negative memos over the same 24 h `ExtensionEtagCache` Missing row | 6 | different caps, one unbounded |
| Seams: `Switchable*` + `Null*` | 18 | but the chokepoint itself (`Services.Metadata`), `TrackAdornments`, `TrackPlayCounts` are bare nullable concretes — the exact shape `wiring-discipline.md` forbids |
| Go-live hooks with **no** teardown in `GoOffline` | `AlbumEnrichment`, `Video`, all 9 `StoreLibrarySource` hooks, `Playback.DetectVideos/ResolveVideoSource/RepublishConnectState`, `CoverColorPlane.Current.Filler`, `HomeFacet` | after logout `LiveSearch` still routes into a torn-down pipeline |
| Read caches with independent invalidation | 3 (`LibraryStore`, `CachedStore` hot tier, `Resource` caches) + 2 pages subscribing the raw store with their own debounce | `DetailPage`, `RecentsPage` |

The intended model (`docs/plans/wavee/entity-data-layer-redesign.md`: one chokepoint, outcome-seeded freshness, protective
merges, "the fetch-gating predicates die") is **implemented at the store/merge/freshness core** and **not implemented at
the surface/orchestration edge**, which is where the ~7 entry points per entity live.

---

## 1. The layers (what exists, bottom → top)

```
 UI (Features/*)  ──reads──►  IMusicLibrary (20 members, Wavee.Core/Library/Library.cs:109)  ──►  AggregateCatalog (only impl)
        │                        └── StoreLibrarySource (real; 9 mutable live hooks: OnDemandFetch, Sync, HydrateMembers,
        │                             DetectVideos, LiveHomeFetch, LiveSearch, LiveSuggest, LiveSuggestRich, UserProfiles)
        │  also reads:  LibraryStore (Loadable cells + 48-entry LRU)  ·  svc.RealStore directly (DetailPage/RecentsPage/… 10 sites)
        │
 Services seams (App/Services.cs): 18 Switchable*Service (+Null*), 3 bare nullable concretes (Metadata, TrackAdornments, TrackPlayCounts),
        │           ~20 Real*/live-only fields; installed by LiveSessionHost.StartAsync L366–625, partly reset by GoOffline L576–622
        │
 Enrichment / trait services (SpotifyLive/*): store-writing (AlbumEnrichment, ArtistStats, ArtistPopularTracks, UserTop, TrackAdornment,
        │           Video, TrackPlayCountHydrator, AlbumPublishingSource, ArtistDiscography, DiscographyPrefetcher)  vs
        │           return-only (Browse, Concerts, WhatsNew, Popcount, ContentFilters, TrackCredits, PreRelease, HomeSections, RecentsFetcher, Friends)
        │           — same folder, no signal which is which
        │
 Composition-root statics (SpotifyLive/LiveSessionHost.cs): EnsureAlbumAsync 1310 · FillAlbumAdornments 1362 · FetchAlbumAsync 1380 ·
        │           ResolveNowPlayingTrackAsync 1418 · PagedHydrateAsync 798 · DetectHook 819 · DetectContainerVideos 869 ·
        │           FetchSearchAsync 931 · HydratePlaylistHeadersAsync 708 · FetchHomeAsync 1188 · LiveHomeCache 1223 · FetchProfileAsync 1142 · BuildLiveLyrics 1046 · FetchSuggestAsync 1163 (dead)
        │
 Chokepoint: MetadataService.SyncAllAsync (Backend/Metadata/MetadataService.cs:64/84) — per-uri Resource<> Etag 1 h, seeds only LANDED uris,
        │           conditional arm via ExtensionEtagCache, ref-closure RunClosureAsync (once/session/uri, ≤900/pass)
        │           (MetadataService.Use / EnsureAsync: ZERO callers)
        │
 Transports: ExtendedMetadataSource (XM: GzipRequest bulk / GetExtensionsAsync / GetExtensionsWithHeadersAsync; ProjectTrack/Album/Artist/Show/Episode/Playlist)
        │    ExtensionEtagCache (LRU 2048 → SQLite localized_extension_cache → HTTP w/ etag; Missing 24 h; offline_ttl clamp 60 s–24 h)
        │    PathfinderResource (TtlFor table; search* uncached; key = locale+op+hash+body+platform, cap 128)
        │    PlaylistFetcher / CollectionFetcher / RecentsFetcher / popcount / artist-top-tracks-extensions / user-profile-view (spclient REST)
        │    DealerRouter (decode-only → LibrarySync commands; never writes the store, never MarkStale)
        │
 Store: IStore → CachedStore (hot InMemoryStore LRU + cold SqliteColdStore, pin-reachability write gate, Full→Tracks demotion on persist,
        │    artist core/facets split, write-behind lane) ; StoreEntityMerge.{Track,Album,Artist,Playlist,Show,Episode} ; EntityCacheGc (6 h pass)
```

---

## 2. ALBUM entry points (21)

| # | Entry point (file:line) | Trigger | Transport | Writes | Freshness/dedup gate | Blocking? |
|---|---|---|---|---|---|---|
| A1 | `ExtendedMetadataSource.ProjectAlbum` `Backend/Metadata/ExtendedMetadataSource.cs:414` | every ALBUM_V4 payload | XM 9 | `UpsertAlbum(…Hydration=Tracks)` **+ one `UpsertTrack` per disc track** (:440) | none (caller seals) | — |
| A2 | `MetadataService.SyncAllAsync` `…/MetadataService.cs:64/84` | THE chokepoint (album open, `ArtistDiscography`, `DiscographyPrefetcher`, `PlaylistFetcher`, `CollectionFetcher`, `PagedHydrateAsync`, `LiveContextResolver`, `ArtistPopularTracks`, `RecentsPage`, show open, now-playing) | XM | → A1/B1/T-A5 | per-uri `Resource<MetadataKey,long>` Etag **1 h**; seals only landed uris | per caller |
| A3 | `SyncAllConditionalAsync` + `ProjectCachedExtensions` `:157/:205` | A2 whenever `_extensionCache` set (always live) | XM w/ per-(uri,kind) ETag | re-serializes cached payloads → A1 | `ExtensionEtagCache` (6 h / offline_ttl / Missing 24 h) + SQLite | — |
| A4 | `RunClosureAsync` `:120` | fire-and-forget after every `SyncAllAsync(closeRefs:true)` | A2 (`closeRefs:false`) | re-fetches album refs with blank names → A1 | `_closureAttempted` (unbounded, once/session), 300/batch, ≤900/pass | best-effort |
| A5 | **`LiveSessionHost.EnsureAlbumAsync` `SpotifyLive/LiveSessionHost.cs:1310`** | album arm of `libSrc.OnDemandFetch` (:578) ⇐ `StoreLibrarySource.EnsureFetchedAsync` ⇐ album page open | XM 9 (+10 repair) → 185 + 183 → PF getAlbum fallback | via A1; `UpsertAlbum(album with {Tracks=rebuilt})` :1336 | `IsAlbumOpenReady` (:1316, :1345) | **blocking** |
| A6 | `LiveSessionHost.FillAlbumAdornments` `:1362` | A5 already-open-ready path | XM 185+183 | via A8/A9 | their memos | unawaited |
| A7 | `LiveSessionHost.FetchAlbumAsync` `:1380` (internal) | (a) A5 fallback (V4 empty/unnamed) (b) **below-the-fold Full upgrade from `SpotifyAlbumEnrichmentService.GetRecommendedPlaylistsAsync:129`** | PF `getAlbum` (`limit:50`, WebPlayer) | `UpsertArtist` ×ArtistsDetailed :1393, `UpsertTrack` per row :1404, `UpsertAlbum` Full :1405 | PathfinderResource 10 min | blocking in (a) |
| A8 | `AlbumPublishingSource.EnsureAsync` `Backend/Metadata/AlbumPublishing.cs:67` | A5/A6 only | XM **183** | `UpsertAlbum(with Copyright/ReleaseDate/Precision)` null-coalesce, never mints | `_missing`+`_applied` memos (cap 8192), TCS in-flight, etag cache | awaited in A5 |
| A9 | `TrackPlayCountHydrator.EnsureAsync` `Backend/Metadata/TrackPlayCounts.cs:205` | A5/A6, `DetectContainerVideos` album arm (always), `DetectHook` when `PlaysColumn`, Plays toggle (`DetailShell.cs:317`) | XM **185** (`track_metadata_loader`) | `UpsertTrack(row with PlayCount)` decorate-only | skip `PlayCount>0`; `_noCount` (cap 20k); 300/slice; etag cache | awaited in A5 |
| A10 | `StoreLibrarySource.GetAlbumAsync` `Backend/Library/StoreLibrarySource.cs:143` | `DetailPage.LoadAlbumDetailAsync` | — | none (read) | `EnsureFetchedAsync` album arm `!IsAlbumOpenReady` (:220) | blocking |
| A11 | `StoreLibrarySource.StreamTracksAsync` `:290` | play-from-context | — | none; yields ONE page (streaming is vestigial) | same | blocking |
| A12 | `SpotifyAlbumEnrichmentService.GetRecommendedPlaylistsAsync` `SpotifyLive/SpotifyAlbumEnrichmentService.cs:121` | `DetailTrailing.LoadTrailingAsync:142` | XM 151 → 205 (+ A7(b) first) | `UpsertPlaylist` per rec (:177/:186) | `Hydration < Full` gate :128; etag cache; no service memo | best-effort |
| A13 | `GetMerchAsync` `:97` | `DetailTrailing:146` | PF `queryAlbumMerch` | display-only | PF 1 h | best-effort |
| A14 | `GetSimilarAlbumsAsync` `:105` | `DetailTrailing:150` (seed = highest-play track) | PF `similarAlbumsBasedOnThisTrack` | **`UpsertAlbum` per similar album** :114 (thin cards) | PF 30 min | best-effort |
| A15 | `GetTrackContextAsync` `:89` | `DetailTrailing.FansAsync:332` | PF `getTrack` | none | PF 10 min | best-effort |
| A16 | `SpotifyPreReleaseService.ResolveAsync` `SpotifyLive/SpotifyPreReleaseService.cs:48` | `DetailPage.LoadAlbumDetailAsync:279/286`, `SaveButton.cs:121` | XM 138 | none (returns link) | `_byUri` incl. negatives (unbounded), `_inFlight` (latent stranded-slot bug — see §8) | blocking on prerelease route |
| A17 | `DiscographyPrefetcher.RunAsync` waves 2/3 `Backend/Metadata/DiscographyPrefetcher.cs:33/42` | post-`InitialHydrate` (`LiveSessionHost.cs:417-429`) | XM 9 then 10, 500/batch | via A1; `UpsertAlbum(with Tracks=rebuilt)` :45 | A2 TTL | background |
| A18 | `CollectionFetcher.HydrateAsync` `Backend/Collections/CollectionFetcher.cs:134` (set `albums`) | sync loop InitialHydrate/delta | spclient collection → A2 | via A1 | revision token + A2 | background |
| A19 | `LiveSessionHost.PagedHydrateAsync` `:798` | post-InitialHydrate liked (:424); `libSrc.HydrateMembers` (Liked open, show open) | A2 in 300 pages | via A1 + A4 | A2 | fire-and-forget |
| A20 | `LiveSessionHost.DetectContainerVideos` album arm `:889` | `sync.OnPlaylistHydrated` (:568) + tail of `OnDemandFetch` (:588) | XM 99/182 + 179/222/6 + 185 | video plane, tempo/tags, plays | per-service caches; **second 185 ask right after A5** (comment :584) | fire-and-forget |
| A21 | `CachedStore.GetAlbum` cold fallback `Backend/Persistence/CachedStore.cs:269` | hot miss | SQLite | promote cold blob → hot | payload-hash elision, LRU | sync |

## 3. ARTIST entry points (16)

| # | Entry point | Trigger | Transport | Writes | Gate | Blocking? |
|---|---|---|---|---|---|---|
| B1 | `ExtendedMetadataSource.ProjectArtist` `:453` | every ARTIST_V4 | XM 8 | `UpsertArtist(Name, Image, TopAlbums=stubs, per-facet totals = group counts, AppearsOn stubs, Bio)`; deliberately **no TopTracks** (:484) | — | — |
| B2 | `ArtistDiscography.EnsureAsync` `Backend/Metadata/ArtistDiscography.cs:20` | artist arm of `OnDemandFetch` (:580, `hydrateAppearsOn:true`) ⇐ artist page / library pane; `DiscographyPrefetcher` wave 1 | XM 8, then 9 for un-hydrated stubs (`AppearsOnHydrateCap=20`) | B1 + `Assemble` | stub-need scan :29; A2 TTL | **blocking** |
| B3 | `ArtistDiscography.Assemble` `:40` | B2, prefetcher, **`CachedStore.ColdFallbackArtist:320`** | — | `UpsertArtist(with TopAlbums=resident cards, sorted, stripped)` | `s_refattening` guard | sync |
| B4 | `StoreLibrarySource.GetArtistAsync` `:149` | `ArtistPage.Render:85`, library pane | — | none | `EnsureFetchedAsync` artist arm :222-233: **no TTL** — refetch when null / TopAlbums empty / `TopAlbums[0].Name==""` / totals > held | blocking |
| B5 | `GetDiscographyAsync` `:158` | `DiscoVc.Make` (page 60), `DiscographyPage.SeedProbe` (limit 0) | — | none; heals TrackCount in the returned copy; **fires `DetectVideos` with ALBUM uris** :179-184 (both video and adornment services drop non-track uris → documented no-op) | in-memory slice | blocking on B4 |
| B6 | `SpotifyArtistStatsService.EnsureStatsAsync` `SpotifyLive/SpotifyArtistStatsService.cs:16` | standalone `ArtistPage:86`, Home top-artist expander | PF **`queryArtistOverview`** (WebPlayer, `preReleaseV2:true`) | **stats-only `UpsertArtist`** (TopAlbums/AppearsOn nulled, totals 0, `FetchedAt=UtcNow`) :38 + `UpsertTrack` per top track :57 | `ArtistStatsCache.IsFresh` (12 h + facet presence); PF 30 min | blocking |
| B7 | `SpotifyArtistPopularTracksService.EnsureExtendedAsync` `SpotifyLive/SpotifyArtistPopularTracksService.cs:61` | `ArtistPopular` chart | spclient `artist-top-tracks-extensions` → A2 (TrackV4) → XM 185 | `UpsertArtist(with TopTracks=merged)` :137 (FetchedAt untouched); `UpsertTrack(with PlayCount)` :191 | `FreshExtended` = count>10 **AND B6's `Artist.FetchedAt` ≤12 h**; `_inFlight` per artist | blocking on chart |
| B8 | `SpotifyAlbumEnrichmentService.GetRelatedArtistsAsync` `:64` | `DetailTrailing.FansAsync:337` (album page) | PF `queryArtistOverview` — **same op/hash/vars/platform as B6** | second stats-only `UpsertArtist` :80 | `Extras.Related` non-empty, **no TTL** (empty related ⇒ re-query on every album open forever) | best-effort |
| B9 | `GetNowPlayingInfoAsync` / `GetAboutArtistAsync` `:36/:61` | album "About the artist", `NowPlayingPanel:87`, `TrackCreditsDialog:56` | PF `queryNpvArtist` (Desktop, `contributorsLimit:10`) | raw `UpsertArtist(ArtistFromNpv)` :56 (listeners/followers/rank/bio/verified/header/Extras); FetchedAt default | PF 30 min only | best-effort |
| B10 | `SpotifyUserTopService.LoadAsync` `SpotifyLive/SpotifyUserTopService.cs:75` | Home top-artist row | PF `userTopContent` | thin `UpsertArtist` :117 + `UpsertTrack` :120 | own snapshot 30 min / 60 s negative; PF 30 min | best-effort |
| B11 | `SpotifyConcertService.GetArtistScheduleAsync` `SpotifyLive/SpotifyConcertService.cs:18` | `ArtistSchedulePage:66` | PF `artistConcerts` | display-only | PF default | page awaits |
| B12 | `DiscographyPrefetcher` wave 1 `:26` | post-InitialHydrate | XM 8, 500/batch | B1 | A2 | background |
| B13 | `CollectionFetcher.HydrateAsync` (set `artists`) | sync loop | spclient + A2 | B1 | revision + A2 | background |
| B14 | `DetectContainerVideos` artist arm `:898` | `OnDemandFetch` tail after artist open | XM 99/179/222 over TopTracks | track planes only (`wantPlays=false`) | etag caches | fire-and-forget |
| B15 | `CachedStore.ColdFallbackArtist` `:303` / `Replay:233` | hot miss / warm replay | SQLite `entity` + `artist_overview` | `UpsertArtist(ArtistSplit.Refatten)` then `Assemble` | `s_refattening`, hash elision | sync |
| B16 | `RecentsPage.HydrateAsync` `Features/Recents/RecentsPage.cs:2304` | recents viewport scroll | A2 with `mdata_esperanto` + `headerTraits:true` (178/179/220 fetched, cached, **never projected**) | via B1/A1 | `_inflight` set + A2 + etag cache | fire-and-forget |

## 4. TRACK entry points (37)

| # | Entry point | Trigger | Transport | Writes | Gate | Blocking? |
|---|---|---|---|---|---|---|
| T1 | `MetadataService.SyncAllAsync` `:64/:84` | chokepoint (all bulk callers) | XM kind per `KindFor` (:238), optional `client-feature-id`/`headerTraits` | → T5 | per-uri Resource 1 h; seals landed only | per caller |
| T2 | `MetadataService.Use/EnsureAsync` `:49/:50` | **no callers** | — | — | Resource SWR | — |
| T3 | `RunClosureAsync` `:120` | after every closeRefs sync | recursive T1 | via T5 | `RefNeedsName`/`TrackNeedsData`; `_closureAttempted` unbounded | best-effort |
| T4 | `SyncAllConditionalAsync` `:157` | live always | etag cache → `GetExtensionsWithHeadersAsync` | synthetic response → `ProjectResponse` | etag cache tiers | — |
| T5 | `ExtendedMetadataSource.ProjectTrack` `:362` | every TRACK_V4 | — | `UpsertTrack(Id, Uri, Title, Artists, AlbumRef, Duration, Explicit, Image, Availability(files), AvailableAt, Isrc, CanonicalUri)` | — | — |
| T6 | `ProjectAlbum` disc rows `:440` | ALBUM_V4 | — | `UpsertTrack` per disc track (album cover stamped) | — | — |
| T7 | `PagedHydrateAsync` `LiveSessionHost.cs:798` | post-InitialHydrate liked; `HydrateMembers` (Liked/show open) | 300-pages → T1 | via T5 | T1 | fire-and-forget |
| T8 | `PlaylistFetcher.HydrateAsync/HydrateUrisAsync` `Backend/Playlists/PlaylistFetcher.cs:238/187` | playlist full-GET, diff adds, dealer apply | T1 (`hydrate` wired :372) | via T5/episode | T1 | **awaited** in `FetchPlaylistAsync` |
| T9 | `CollectionFetcher.HydrateAsync` `:134` | sync loop | T1 | via T5/T6 | T1 | awaited on loop |
| T10 | `LiveContextResolver.HydrateAsync` `SpotifyLive/LiveContextResolver.cs:350` | **playback** context resolve | T1 | via T5; misses → in-memory `Placeholder` (:367), not upserted | T1 | awaited on play path |
| T11 | `LiveSessionHost.ResolveNowPlayingTrackAsync` `:1418` (→ `connect.Projection.TrackResolver` :615) | every cluster push/local event via `MaybeEnrichCurrent` | store → T1 single → **PF `getTrack`** | `UpsertTrack(TrackFromUnion)` :1432 | `NowPlayingReady` ×2 (:1422/:1426); PF 10 min; `_resolvingUri` | fire-and-forget upstream |
| T12 | `NowPlayingProjection.MaybeEnrichCurrent/ResolveAsync` `Backend/PlaybackProjection.cs:481/501` | cluster fold, local snapshot/event | calls T11 | **no store write** — folds onto in-memory `_track` | `thin = !NowPlayingReady || Album.Uri empty` (**differs from T11's test**) | best-effort |
| T13 | `NowPlayingProjection.OnCluster → MapTrack` `:388/:651` | dealer cluster | dealer | no store write; runs `StoreEntityMerge.Track` in-memory :420 (the only merge outside the store) | 2500 ms stale window | — |
| T14 | `SpotifyTrackAdornmentService.EnsureAsync` `SpotifyLive/SpotifyTrackAdornmentService.cs:73` | every `DetectHook`/`DetectContainerVideos` | XM **179+222+6**, ≤300/POST, etag cache pref. | `UpsertTrack(with TempoBpm/MusicalKey/Camelot*/Tags)` :176 (existing rows only); colour → `CoverColorPlane` (image-keyed) | skip if `TempoBpm != null` (:86 — one mark for 3 kinds); `_noAdornment` cap 20k | fire-and-forget |
| T15 | `SpotifyVideoService.DetectAsync` `SpotifyLive/SpotifyVideoService.cs:45` | same hooks | XM **99+182**, ≤300 | `UpsertVideoAssociation` (plane) | plane `IsFresh` (neg 30 min); etag | fire-and-forget |
| T16 | `SpotifyVideoService.RecoverCanonicalAsync` `:218` | 99 miss contradicted by 182 | XM full TRACK_V4 + **212**, then 99 on canonical | `UpsertTrack(with CanonicalUri)` :269 + plane under alias :395; `MarkStale(alias,99)` | `NeedsRecovery` | fire-and-forget |
| T17 | `SpotifyVideoService.GetAsync/FetchOneAsync` `:311/:323` | now-playing warm (:618) | XM 99+182 | plane | `_inflight` per uri | fire-and-forget |
| T18 | `SpotifyTrackExpansionService.GetAsync/LoadAsync` `SpotifyLive/SpotifyTrackExpansionService.cs:60/78` | row expand, format split button | XM **99+98+5+237** then TRACK_V4+222 per target (≤300) — **no etag cache** (ctor has none) | `SpotifyVideoService.Fold` → plane; no Track write | `_cache` per uri | awaited by drawer |
| T19 | `TrackPlayCountSource.GetAsync` `Backend/Metadata/TrackPlayCounts.cs:51` | T20, `SpotifyArtistPopularTracksService` | XM **185** | none | track-only guard; etag | awaited |
| T20 | `TrackPlayCountHydrator.EnsureAsync` `:205` | album open, hooks when PlaysColumn, Plays toggle | T19 | `UpsertTrack(with PlayCount)` | skip `PlayCount>0`; `_noCount`; 300 | mixed |
| T21 | `SpotifyTrackCreditsService.GetAsync` `SpotifyLive/SpotifyTrackCreditsService.cs:52` | Now Playing rail, credits dialog | XM **186** (`track_metadata_loader` raw arm only) | display-only | `_byUri` incl. negatives (unbounded); `_inFlight` | awaited |
| T22 | `SpotifyPreReleaseService.ResolveAsync` `:48` | album/artist/prerelease/pre-save | XM 138 | display-only | `_byUri`; `_inFlight` | awaited |
| T23 | `SpotifyArtistPopularTracksService.LoadAsync` `:90` | artist chart | REST → T1 → T19 | `UpsertTrack(with PlayCount)` :191; `UpsertArtist(TopTracks)` :137 | `FreshExtended`; `_inFlight` | awaited |
| T24 | `SpotifyArtistStatsService` `:57` | ArtistPage | PF `queryArtistOverview` | `UpsertTrack` per overview top track (name-less AlbumRef + PlayCount) | `IsFresh` | awaited |
| T25 | `SpotifyUserTopService` `:120` | Home | PF `userTopContent` | thin `UpsertTrack` | snapshot | best-effort |
| T26 | `LiveSessionHost.FetchAlbumAsync` `:1380` | getAlbum fallback / Full upgrade | PF `getAlbum` | `UpsertTrack` per row :1404 (Availability/PlayCount) | see A7 | see A7 |
| T27 | `EnsureAlbumAsync` `:1310` | album open | see A5 | `UpsertAlbum(with Tracks=rebuilt)` | `IsAlbumOpenReady` | blocking |
| T28 | `SpotifyAlbumEnrichmentService.GetTrackContextAsync` `:89` | album drawer/context | PF `getTrack` | display-only | `QueryAsync` (no resource dedup) | awaited |
| T29 | `GetNowPlayingInfoAsync` `:36` | NPV | PF `queryNpvArtist` | `UpsertArtist` only | PF | awaited |
| T30 | `PlaylistMutationSource.InsertTracksCoreAsync` `Backend/Playlists/PlaylistMutationSource.cs:95` | drag/drop, add-to-playlist | local | `UpsertTrack(supplied entity)` unconditional (so `JoinMembership` keeps the optimistic row) | none | sync, UI path |
| T31 | `AudioPlaybackStack` fetchers `SpotifyLive/Audio/AudioPlaybackStack.cs:87-91` | playback file resolution | XM **10/5/12 raw, no etag cache** | none; `_metaCache`/`_cdnCache` | per-uri task cache | blocking on play |
| T32 | `SpotifyMediaProvider.ResolveWireMetaAsync` `:44` | Connect publish | delegates T31 | none | — | blocking |
| T33 | `PlaybackBridge.BumpQueueRevision → DetectVideos` `App/PlaybackBridge.cs:1073/1082` | queue content change | hook → T14+T15 | planes | `QueueContentFold` | fire-and-forget |
| T34 | `libSrc.LiveSearch` detect arm `LiveSessionHost.cs:596-607` | online search | PF search ops → `detectSearch(trackUris)` | search rows are transient (never store joins); planes only | 8 s CTS | fire-and-forget |
| T35 | `RecentsPage.HydrateAsync` `:2304` | viewport realize/expand | T1 `mdata_esperanto`+`headerTraits` | via T5 (+show/episode/playlist) | `_inflight` | fire-and-forget |
| T36 | `CachedStore.GetTrack` cold fallback `:267` | hot miss | SQLite | promote | `HasEvictedEntities` | sync |
| T37 | `Backend/Scaffold.cs:49` | demo | local | `UpsertTrack` ×2 | — | — |

**Thin-track writers (uncoordinated, kept honest only by `StoreEntityMerge.Track`):** T5, T6, T26, T24, T25, T30, plus field-scoped `row with {…}` from T14/T16/T20/T23 — and T13's merge that runs outside the store.

## 5. PLAYLIST entry points (25)

| # | Entry point | Trigger | Transport | Writes | Gate | Blocking? |
|---|---|---|---|---|---|---|
| P1 | `PlaylistFetcher.FetchPlaylistAsync` `Backend/Playlists/PlaylistFetcher.cs:41` | first open, attr-less heal, diff fallbacks, signal reconcile | spclient `GET /playlist/v2/{path}?decorate=…` | `AdoptSnapshot:200` under BeginBulk: `UpsertPlaylist(HeaderOf)` full header, `SetMembership(rev)`, `Bump` | `StorableRevision` | **blocking** first open |
| P2 | `FetchPlaylistHeaderAsync` `:51` | rootlist header hydration (`HydratePlaylistHeadersAsync:708`), `UpdateList` in diff/push, capability heal, `LiveHomeCache` daylist | same GET | header only | skips resident header (:719); one BeginBulk | mixed |
| P3 | `FetchPlaylistRevisionAsync` `:64` | `HomeDaylistHydrator` probe | `?decorate=revision` | **nothing** | 5 s coalesce | awaited |
| P4 | `FetchPlaylistDiffAsync` `:97` | `LibrarySync.PlaylistRevalidateAsync` (open SWR ≥5 min, dirty, reconnect, post-drain) | `/diff?revision=…` | `SetMembership` + hydrate added + Bump; `UpdateList` → P2 | 304/up_to_date; torn → P1 | sync loop |
| P5 | `FetchRootlistAsync` `:74` | InitialHydrate, RootlistPush fallback, ReconnectResync | rootlist GET | `SetRootlist` (2-arg iff well-formed rev) | I1 gate | loop |
| P6 | `LibrarySync.OpenPlaylistHandlerAsync/OpenPlaylistCoreAsync` `Backend/Sync/LibrarySync.cs:796/809` | **page open** via `EnsureFetchedAsync:198` | P1/P2/P4 | as above; fires `OnPlaylistHydrated` → `DetectContainerVideos` | `_openInFlight`, 5 min window, `_dirtyPlaylists`, `_attrHealForced`, capability heal | blocking iff no membership |
| P7 | `PlaylistPushAsync` `:522` | dealer `hm://playlist/…` | dealer → P4/P1 | in-place `SetMembership` + hydrate; `UpdateList` → P2 | 6 gates (echo/tombstone/pending/new-head/parent/open-vs-dirty) | loop |
| P8 | `RootlistPushAsync` `:323` | dealer rootlist | dealer + P5 | `SetRootlist` + `FoldRootlistIntoSavedSet` | echo ring, well-formed head | loop |
| P9 | `ApplyTombstone` `:617` | delete observed | local | `SetRootlist`, `SetSaved(false)`, `SetMembership([])`, `UpsertPlaylist(DeletedByOwner)` | idempotent | loop |
| P10 | `PermissionPushAsync` `:638` | dealer permission | dealer | `UpsertPlaylist(IsPublic/BasePermissionRevision/Collab)` | cold header ignored | loop |
| P11 | `SeedPermissionAsync` `:661` | `SetOpenContext` on OWNED playlist | permission GET | `UpsertPlaylist(IsPublic/BaseRev)` | owner-only | loop |
| P12 | `ApplyPlaylistSignalAsync` `:388` | tuning chip | signals POST → AdoptSnapshot | full replace + hydrate | revision/roster staleness | awaited |
| P13 | `HydratePlaylistAsync` `:494` | retry ladder | T8 | Bump | ≤3 attempts | loop |
| P14 | `ExtendedMetadataSource.ProjectPlaylist` `:543` | LIST_METADATA_V2 (205) via T1 (recents/sidebar/pointers) | XM 205 | `UpsertPlaylist(Name/Description/OwnerName/Cover, all else carried from resident)`; returns false → unsealed if empty | — | in-batch |
| P15 | `SpotifyAlbumEnrichmentService.GetRecommendedPlaylistsAsync` `:121` | album below-the-fold | XM 151→205 | `UpsertPlaylist` (3-field carry :177, or **mints** `Playlist(...,TrackCount:0)` :186) | ≤12, etag | best-effort |
| P16 | `PlaylistMutationSource.CreatePlaylistAsync` `:43` | create | `POST /playlist/v2/playlist` + rootlist ops | `UpsertPlaylist`, `SetMembership([])`, `SetSaved`, `Bump("rootlist")` | rootlist lane | awaited |
| P17 | `OpRebaseStrategy.TryApply/ApplyHeaderPatch` `Backend/Mutation.cs:108/119` | optimistic apply + replay | local then `/changes` | `SetMembership`; **authoritative** `UpsertPlaylist(Name/Description/Cover/Capabilities)` — the writer `StoreEntityMerge.Playlist` is designed for | torn on OOR | sync optimistic |
| P18 | `MutationEngine.AdoptSnapshot`/rollback `:286/294/297/548/683/703` | 200 capture, dead-letter rollback | local | `SetMembership`, `UpsertPlaylist(snapshot)` | storability | drain |
| P19 | permission write `PlaylistMutationSource:405` | public/private toggle | spclient | `UpsertPlaylist` | — | awaited |
| P20 | `RecentsFetcher.FetchAsync/FetchDiffAsync` `Backend/Playlists/RecentsFetcher.cs:38/67` | Recents load + 2 s post-play | `/playlist/v2/list/recents/page[/diff]` zstd | **nothing** (snapshot; page hydrates via T35) | 304/up_to_date/zero-op | awaited |
| P21 | `SpotifyPlaylistPopcountService.GetSaveCountAsync` `:59` | header render | `/popcount/v2/playlist/{id}/count` | display-only | 6 h cache incl. negatives (uncapped); `_inFlight` | best-effort |
| P22 | `StoreLibrarySource.GetPlaylistAsync` `:98` | detail read | — | read-model only (`JoinMembership`, mosaic, `OverlayOwner`) | — | awaited |
| P23 | `LiveHomeCache` `LiveSessionHost.cs:1223` | Home | PF `home` → daylist hydrator → P3/P2 | headers via P2 | PF TTL, `_hydrator.Hydrated` filter | awaited |
| P24 | `HomeBaselinePreviews.Prime` `SpotifyLive/HomeBaselinePreviews.cs:39` | hover peek | PF `feedBaselineLookup` ≤20 | display-only static | process-lifetime | fire-and-forget |
| P25 | `CachedStore.SetMembership/Membership` `:351/:366` | every membership write/read | SQLite | dual-write `playlist_items` + `base_rev`; flush playlist + every member | `HasMembership` consults revision | write-behind |

## 6. SHOW / EPISODE + USER entry points (13)

| # | Entry point | Trigger | Transport | Writes | Gate | Blocking? |
|---|---|---|---|---|---|---|
| S1 | `ExtendedMetadataSource.ProjectShow` `:501` | SHOW_V4 | XM 11 | `UpsertShow`; episode gids → **`SetMembership(showUri,…)` (playlist membership plane reused)** :519 | episodes>0 | — |
| S2 | `ProjectEpisode` `:522` | EPISODE_V4 | XM 12 | `UpsertEpisode` (ProgressMs always 0) | — | — |
| S3 | `OnDemandFetch` show arm `LiveSessionHost.cs:581` | show open | T1 → S1 | S1 | `GetShow null || !HasMembership` (:234) | **blocking** |
| S4 | show episode paging `StoreLibrarySource.cs:242-252` | after S3 | `HydrateMembers` → T7 (EPISODE_V4) | S2 | **first 300 only, no further paging** | fire-and-forget |
| S5 | `GetShowAsync` `:595` | read | — | read-model join | — | awaited |
| S6 | `PlaylistFetcher.HydrateAsync` episode filter `:244` | playlist w/ episodes | T1 | S2 | — | with P1 |
| S7 | `LibrarySync` shows/episodes sets `:50` | InitialHydrate/CollectionPush/Reconnect | collection delta/paging | `SetSaved` + hydrate → S1/S2 | sync token | loop |
| U1 | `LiveSessionHost.FetchProfileAsync` `:1142` | login pre-go-live | `GET /user-profile-view/v3/profile/{u}` | none; feeds session + `userProfiles.Seed` (:540) | — | **blocking splash** |
| U2 | `SpotifyUserProfileService.Prefetch/ResolveBatchAsync` `SpotifyLive/SpotifyUserProfileService.cs:52/68` | owner/added_by resolution (`OverlayOwner:665`, `PrefetchPlaylistUsers:677`, recents owners) | XM **15** batched + REST fallback (SemaphoreSlim 4) | **not the store** — private cache + `Changed` → `StoreLibrarySource.OnProfileChanged:741` → `store.Bump(playlistUri)` | `_cache` no TTL; `_inflight`; 404 → cached null | fire-and-forget |
| U3 | `Seed` `:39` | go-live | — | in-memory | — | sync |
| U4 | `PlaylistFetcher.HeaderOf` owner chip `:270` | header write | — | `Owner(owner,owner,null)` seed | — | sync |
| U5 | `SpotifyFriendActivityService` | presence | dealer `hm://presence2` + seed | display-only | session | fire-and-forget |
| S8 | `CachedStore` show/episode persist `:481/487/629/635` | every upsert | SQLite `entity` (+ show→episode refs) | blob | pin gate | write-behind |

---

## 7. The wiring map

### 7.1 `Services` seams (`App/Services.cs`)

| Property | Decl | Kind | Offline impl | Go-live (`LiveSessionHost.cs`) | GoOffline reset |
|---|---|---|---|---|---|
| `AlbumEnrichment` | 116 | Switchable | `CatalogAlbumEnrichmentService` | :494 | **missing** |
| `ArtistStats` | 120 | Switchable | Null | :498 | 597 |
| `ArtistPopularTracks` | 125 | Switchable | Null | :515 | 598 |
| `UserTop` | 130 | Switchable | Null | :519 | 599 |
| `PlaylistPopcount` | 134 | Switchable | Null | :520 | 600 |
| `PreRelease` | 138 | Switchable | Null | :524 | 601 |
| `TrackCredits` | 142 | Switchable | Null | :527 | 602 |
| `ContentFilters` | 146 | Switchable | Null | :521 | 603 |
| `Video` | 149 | Switchable | `NoVideoService` | :530 | **missing** |
| `UserProfiles` | 167 | Switchable | Null | :544 | 585 |
| `Friends`/`SpotifyNotifications`/`WhatsNew`/`Concerts`/`Browse`/`TrackExpansion`/`Recents`/`HomeSections` | 170–202 | Switchable | Null | :632/:639/:489/:463/:465/:470/:376/:468 | 586–593 |
| **`Metadata`** | 164 | **bare `MetadataService?`** | null | :381 | 596 |
| **`TrackAdornments`** | 153 | **bare concrete?** | null | :559 | 594 |
| **`TrackPlayCounts`** | 157 | **bare concrete?** | null | :509 | 595 |
| `RealStore`/`RealLibrarySource`/`RealCold`/`CacheGc` | 25/29/41/45 | live-only | — | ctor | never |
| not on `Services` at all | — | — | — | `TrackPlayCountSource` :504, `AlbumPublishingSource` :513 (locals captured by `OnDemandFetch`) | die with host |
| non-`Services` globals set at go-live, never reset | — | — | — | `CoverColorPlane.Current.Filler` :474, `svc.Playback.DetectVideos` :563, `.ResolveVideoSource` :536, `.RepublishConnectState` :551, `sync.OnPlaylistHydrated` :568, all 9 `StoreLibrarySource` hooks :571–610, `HomeFacet` | — |

### 7.2 Hook chains

```
DetectHook(video, adorn, ct, surface, log, plays?, playsWanted?)   LiveSessionHost.cs:819
   → video.DetectAsync (99+182) → adorn.EnsureAsync (179+222+6) → plays.EnsureAsync (185 iff playsWanted())
   wired: :562 artist.popular · :563 queue · :564 search · :593 library/Liked (plays + playsWanted)
DetectContainerVideos(video, adorn, store, uri, …)                 :869   (resolves uris itself; wantPlays: album=true, playlist=setting, artist=false)
   wired: :568 sync.OnPlaylistHydrated (live playlist opens) · :588 tail of libSrc.OnDemandFetch (album/artist/show opens)
libSrc.OnDemandFetch :572   playlist→fetcher.FetchPlaylistAsync (UNREACHABLE live: EnsureFetchedAsync returns into Sync first)
                            album→EnsureAlbumAsync :578 · artist→ArtistDiscography.EnsureAsync :580 · show→SyncAllAsync :582 · tail→DetectContainerVideos :588
libSrc.HydrateMembers :594 → PagedHydrateAsync (Liked open, show open)
connect.Projection.TrackResolver :615 → svc.Video.GetAsync + ResolveNowPlayingTrackAsync
```

### 7.3 Freshness / persistence infrastructure (facts)

- `Resource<K,V>` instances: `MetadataService` (Etag 1 h), `ExtensionEtagCache` (Etag 6 h + per-row ttl, cap 2048), `PathfinderResource` (PollWhole 15 min + `TtlFor`, cap 128), 4 audio caches (`Immutable`). `FreshnessPolicy.RevisionDelta`/`SnapshotRevision` and `MarkStale` anti-herd: **no production users** — the dealer never calls `MarkStale`; invalidation is `LibrarySync` writing the store.
- `PathfinderResource.TtlFor`: Home/HomeSection 15 min · GetAlbum/GetTrack 10 min · Similar 30 min · Merch 1 h · NpvArtist 30 min · ArtistOverview 30 min · WhatsNew 5 min · UserTop 30 min · BrowseAll 6 h · BrowsePage/Section 30 min · `search*` **0** · default 10 min.
- `ExtensionEtagCache`: LRU → cold point-read (`SeedPersisted`, debounced `last_access` touch) → HTTP w/ etag (never for Missing rows); `Fold` 200/304/404/empty-200; absent-from-response is not an outcome. Table `localized_extension_cache` PK `(entity_uri, locale, extension_kind)`.
- `CachedStore`: hot always, cold iff pin-reachable; `PersistAlbum` strips Tracks/MoreBy/ArtistsDetailed/OtherVersions and **caps Full→Tracks**; `PersistArtist` splits core (`entity`) / facets (`artist_overview`) and side-persists `TopTracks` rows **bypassing the hot merge** (:614-621 admits the hazard); ctor still does two O(library) loads (`LoadAllVideoAssociations`, `LoadAllSaved`).
- `EntityCacheGc` (6 h): membership 14 d → pin table → expired extensions (+7 d grace) → `artist_overview` 7 d → unpinned entities 30 d → byte budget (extensions ≤ max(8 MB, budget/4)) → vacuum.
- Merges (`Backend/Store.cs`): Track :107 (Title placeholder rule, PlayCount/Duration >0, nullable adornments coalesce), Album :157 (Hydration=max, DiscCount/IsPreRelease authoritative only when incoming Full), Artist :187 (`TopTracks` = incoming-if-non-empty — why overview shrinks the chart; `FetchedAt` = max), Playlist :230 (**authoritative** Name/Description/Cover/Capabilities — why 205 projectors must hand-carry), Show :257, Episode :272 (`ProgressMs` unconditional).

---

## 8. Findings — overlaps, duplications, inconsistencies (with evidence)

Grouped by theme; each item is independently verifiable at the cited lines.

### 8.1 Where the SAME question is answered in several places
1. **Two `KindFor` maps** — `MetadataService.cs:238` vs `ExtendedMetadataSource.cs:304`, byte-identical, comment at `MetadataService.cs:246` admits a divergence "would silently send playlists down the uncached arm". Not enforced.
2. **≥6 "is it cold?" predicates, none shared** — `StoreLibrarySource.EnsureFetchedAsync:208-238` (4 inline per-kind gates), `IsAlbumOpenReady:261`, `IsAlbumComplete:267` (**dead in production** — only tests + comments reference it, verified), `EnsureAlbumAsync:1316/:1345` (re-runs open-ready twice), `StoreEntityGaps.NowPlayingReady` (`Store.cs:389`), `LibrarySync.cs:823` ("unnamed track ⇒ cold"). The redesign doc budgeted these to die.
3. **Two "now-playing is thin" tests that must agree and don't** — `PlaybackProjection.cs:494` `!NowPlayingReady || Album.Uri empty` vs `LiveSessionHost.cs:1422/1426` `NowPlayingReady` only; the code's own comment (:1414-1417) says both sites must agree. Result: a row with album name but no album uri re-fires the resolver on every cluster push (cheap, but a loop).
4. **Two "does this surface want play counts?" policies** — `DetectHook` `playsWanted` (:839) vs `DetectContainerVideos` `wantPlays` (:878-897), plus the toggle handler (`DetailShell.cs:317-325`).
5. **Two `queryArtistOverview` callers with identical wire request and different freshness** — `SpotifyArtistStatsService.cs:29-43` (12 h + facet presence) vs `SpotifyAlbumEnrichmentService.cs:70-85` (`Extras.Related` non-empty, no TTL → empty related re-queries on every album open).
6. **Two LIST_METADATA_V2 (205) projectors** — `ExtendedMetadataSource.ProjectPlaylist:543` (11-field carry-through, refuses to mint) vs `SpotifyAlbumEnrichmentService.cs:174-187` (3-field carry, mints `Playlist(...,TrackCount:0)`); two cover pickers (`ListCover:582` vs `Cover:202`).
7. **Two album-track-uri walkers** — `LiveSessionHost.AlbumTrackUris:1370` vs `DetectContainerVideos:889-897`; the double 185 ask per album open is documented at :584-587 and kept.
8. **Two `canonical_uri` decoders** — `ExtendedMetadataSource.CanonicalUriOf:379` (lean parser, null-if-self) vs `SpotifyVideoService.cs:423-425` (full parser; its comment claiming the lean view discards field 36 is stale).
9. **Two per-facet artist totals sources** — `ProjectArtist` (V4 group counts, `ExtendedMetadataSource.cs:471`) vs `MapArtist` (`discography.*.totalCount`, `SpotifyExportMapper.cs:1493`); `EnsureFetchedAsync:232` uses `totals > TopAlbums.Count` as a refetch trigger, which is why stats writers must zero totals (`SpotifyAlbumEnrichmentService.cs:77-79` recounts the bug).
10. **Three profile fetches** — `LiveSessionHost.FetchProfileAsync:1142` and `SpotifyUserProfileService.ResolveRestAsync:109` hit the same REST endpoint with two JSON parsers; the service's kind-15 batch arm is a third; the first result is hand-seeded into the second (:539-543).
11. **Six `IdOf`/`IdFromUri` implementations** — `ExtendedMetadataSource:598`, `SpotifyTrackExpansionService:280`, `SpotifyAlbumEnrichmentService:212`, `PlaylistFetcher:376`, `PlaybackProjection:710`, `LiveContextResolver:367`; `SpotifyExportMapper.IdFromUri` exists.
12. **Seven copies of "300 per POST"** and **seven copies of "etag-cache preferred / raw fallback"** with inconsistent `client-feature-id` (credits passes it only on the raw arm — `SpotifyTrackCreditsService.cs:77-78`); **six per-service negative memos** over the same 24 h Missing row (caps 20k/20k/8192/uncapped/uncapped/6 h; `_closureAttempted` unbounded).

### 8.2 Where logic lives in the wrong place
13. **14 static metadata helpers in the composition root** (`LiveSessionHost.cs`; list in §1) — not injectable, not unit-testable (`Wavee.Tests.csproj` does not compile the file); one dead (`FetchSuggestAsync:1163`, verified no callers); `RunAsync:1440` is a CLI smoke test beside production wiring.
14. **A service reaches back into the composition root** — `SpotifyAlbumEnrichmentService.cs:129` calls `LiveSessionHost.FetchAlbumAsync` (internal).
15. **`AlbumPublishingSource` / `TrackPlayCountSource` are locals** captured by the `OnDemandFetch` closure (:504/:513) — unreachable from any surface, no `GoOffline` reset.
16. **The chokepoint has no seam** — `Services.Metadata`, `TrackAdornments`, `TrackPlayCounts` are bare nullable concretes with `?.` guards at consumers (`DetailShell.cs:321`, `RecentsPage.cs:255`) — the shape `.claude/skills/wavee/wiring-discipline.md` forbids; every other capability got `Switchable*`+`Null*`.
17. **A read source writes** — `StoreLibrarySource.OnProfileChanged:741-753` calls `store.Bump(playlistUri)`.
18. **`GoOffline` misses** `AlbumEnrichment`, `Video` (verified: no `SetInner` for either anywhere in `Services.cs`), all 9 `StoreLibrarySource` hooks, `Playback.DetectVideos/ResolveVideoSource/RepublishConnectState`, `CoverColorPlane.Current.Filler`, `HomeFacet`.

### 8.3 Duplicate / disjoint pipelines for one effect
19. **Two "open a container" paths keyed by uri prefix** — `EnsureFetchedAsync:198-203` routes playlists into `Sync` and returns; everything else goes to `OnDemandFetch`. Consequence: `OnDemandFetch`'s playlist arm (:574-577) and the `!HasMembership` gate (:208) are **dead live**, and a second hook (`sync.OnPlaylistHydrated`) exists only to give playlists the same `DetectContainerVideos` tail.
20. **Three hook chains** for one fan-out (§7.2) with per-surface diagnostic tags but independent plays policy and uri collection.
21. **`GetDiscographyAsync` fires the track hook with album uris** (:179-184) — both the video and adornment services drop non-track uris (`SpotifyTrackAdornmentService.cs:81`), so the documented "resolves each card's cover tint" is a no-op; album tint comes from `CoverColorPlane`.
22. **Album tracks fanned out as entities by three writers** with different shapes — `ProjectAlbum:440`, `FetchAlbumAsync:1404`, `CachedStore.PersistArtist:617` (bypasses merge).
23. **`Artist.FetchedAt` carries three meanings** — overview landed (B6/B8), extended-chart freshness (`SpotifyArtistPopularTracksService.FreshExtended:86`), and `MergeExtras`' `overviewAuthoritative` discriminator (`Store.cs:213`); B7 and B9 deliberately don't stamp it.
24. **Three overlapping read caches** (`LibraryStore`, `CachedStore` hot, `Resource`s) plus `DetailPage` (:83-137, 50 ms debounce, 4-branch relevance) and `RecentsPage` (:290-300) subscribing the raw store with their own coalescing.
25. **`TrackExpansion` is the only trait reader without the etag cache** (ctor `LiveSessionHost.cs:470` passes none; kind 237 ≈38 KB re-fetched raw after re-login).
26. **`Recents` `headerTraits` (178/179/220)** are fetched, cached to SQLite, budgeted by GC, and never projected (`MetadataService.cs:214-217`, `ExtendedMetadataSource.cs:353`) — the only surface using `Services.Metadata`.
27. **Adornment "already fetched" mark is one field for three kinds** (`TempoBpm != null`, `SpotifyTrackAdornmentService.cs:86`) — a track with tags+tint but no tempo re-requests all three kinds every realize; negative memo only when all three empty (:164-168).
28. **Shows ride the playlist membership plane** (`ExtendedMetadataSource.cs:507-519`) with a stated requirement ("opened shows should land in `recent_surfaces`") that no show-open path fulfils (`GetShowAsync:595` never `RecordRecentSurface`) — eligible for the 14 d membership purge. Show episode paging stops at 300 (`StoreLibrarySource.cs:247`).
29. **`StreamTracksAsync` "skeleton-then-stream"** yields exactly one page (:290-297).
30. **Search/Home never upsert albums/artists** (mapper output only) while `GetSimilarAlbumsAsync` does (`:114`) — one policy, one exception.
31. **Latent stranded-slot bug in the `_inFlight.GetOrAdd(uri, LoadAsync)` + `finally TryRemove` pattern** when the load completes synchronously (cache hit / fake): `SpotifyPreReleaseService.cs:54/104` (benign only because it caches every answer). `AlbumPublishingSource` fixed it with a TCS slot; the credits/expansion services should be checked for the same shape.

### 8.4 Dead surface / doc drift
32. `MetadataService.Use/EnsureAsync` — zero callers (verified). `FreshnessPolicy.RevisionDelta/SnapshotRevision` — zero users. `IsAlbumComplete` — production-dead. `SpotifyAlbumEnrichmentService.Excerpt:222` — unused.
33. `docs/architecture.md` is cited from production files and does not exist; §9 status matrix predates Pathfinder/local audio/video/adornments/Browse; `SKILL.md:17` and `architecture.md` name different hub docs. `wavee-data-gaps.md` introduced `OnDemandFetch` as a temporary hook; it has since gained 8 siblings.
    - **[P3-C 2026-08-16] Docs half fixed; code half outstanding.** The count was understated: `docs/architecture.md` is cited **33 times across 22 production files** (`Wavee.Core/{Sources,Spotify,Fakes,Library,Domain}/**`, `Wavee/{App,Components,Features}/**`), not five, and `docs/plans/wavee/architecture.md` itself never cited it. Fixed in docs: `architecture.md` now states in its header that it IS the doc those comments mean and that `wavee-native-backend-architecture.md` is a different doc; §9 is refreshed (Pathfinder, local audio, video, adornments, Browse, podcasts-are-real) and carries the hydration rows; `.claude/skills/wavee/SKILL.md` now points at `docs/plans/wavee/architecture.md` as the seam canon. The 33 **code comments** are unfixed — a mechanical `docs/architecture.md` → `docs/plans/wavee/architecture.md` sweep for whoever next owns those files.

---

## 9. What "organized" would look like (recommendation, not action)

Everything below is a shape, not a plan; nothing here was changed.

1. **One `EntityHydrator` per entity kind (album/artist/track/playlist/show), owning its cold-predicate, its ladder and its adornment set** — e.g. `AlbumHydrator.EnsureOpenReadyAsync(uri)` = V4 → TrackV4 repair → {185, 183} → getAlbum fallback, `EnsureFullAsync(uri)` = getAlbum envelope. `EnsureAlbumAsync`/`FillAlbumAdornments`/`FetchAlbumAsync`/`ArtistDiscography.EnsureAsync`/`ResolveNowPlayingTrackAsync` move out of `LiveSessionHost` into these; `StoreLibrarySource.EnsureFetchedAsync`'s per-kind gates collapse to `hydrator.IsOpenReady(entity)`. `SpotifyAlbumEnrichmentService` calls `AlbumHydrator.EnsureFullAsync`, not `LiveSessionHost`.
2. **One `TrackTraitBundle` (row-bundle) service** replacing the three hook chains: `EnsureAsync(uris, surface, wants: Video|Adorn|Plays)` with the 300-cap, the etag-cache-preferred branch, the per-kind negative memo and the `client-feature-id` in exactly one place; the four current trait services become kind projectors behind it (or stay, but only it batches). `playsWanted` lives here once.
3. **The chokepoint gets a seam**: `IMetadataHydrator` (+Null) replacing bare `Services.Metadata/TrackAdornments/TrackPlayCounts`; `AlbumPublishingSource`/`TrackPlayCountSource` become fields on it or on `Services`; `GoOffline` resets everything a `Register(go-live)` list installed (a symmetric install/uninstall table instead of a hand-maintained method).
4. **One `KindFor`, one `IdOf`, one `BatchCap`** (`MetadataChunking` chunks by entity count as well as bytes), one 205 projector, one `queryArtistOverview` caller (`ArtistStats` service; album page asks it), one `NowPlayingReady` used by both the projection and the resolver.
5. **`Artist.FetchedAt` split** into `OverviewFetchedAt` (stats) and the chart's own stamp; the artist-chart service stops borrowing it.
6. **Prune**: `IsAlbumComplete` (or make the Full-upgrade gate use it), `MetadataService.Use/EnsureAsync`, `Resource` revision policies, `FetchSuggestAsync`, `Excerpt`, `OnDemandFetch`'s playlist arm; fix the five stale doc pointers.
7. **Store-writing vs return-only services** get a namespace/naming signal (`…Hydrator` writes the store; `…Service` returns), so the census in §4/§5 stops being needed.

Order of value: (1)+(3) remove the composition-root logic and the missing seams (the "4-5 entry points for album" the user noticed); (2) removes the duplicated trait plumbing that every new kind (185, 186, 183 today) had to re-copy; (4)–(7) are cleanup.
