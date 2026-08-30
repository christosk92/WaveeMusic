# Wavee music-domain architecture — the source-agnostic provider seam

> **Status:** living architecture doc. This is the canonical reference for the layer **between the UI and
> the underlying data/services** in the Wavee-fluent app. It wires the **catalog** surface (Home, Library/Sidebar,
> playlist/album/**artist**/**show** detail, **search**, **library grids**) from real Spotify export JSON + synthesized
> peers, plus **mutations** (save/like/follow with an optimistic+persisted outbox), a **local-files** peer source, and
> **podcasts**. The **session** facet registers in the source list; **playback / Connect-remote / lyrics** are NOT
> in-process sources anymore — local in-process audio plays through `AudioPlaybackStack` once a live session exists (§9);
> `UnsupportedPlaybackPlayer` is only the pre-login stub, and the Connect device roster comes from the live cluster.
> Per-facet **federation** (`Federated*`) stays the documented `registry.OfCapability` hook, deferred until a 2nd real
> source (§4.3). Keep this file honest: when a capability moves, update the matrix (§9).
>
> **Pointer note.** This file — `docs/plans/wavee/architecture.md` — is the **seam canon**, and the one the
> `wavee` skill points at. Production comments across `Wavee.Core/Sources/**`, `App/**` and `Features/**` cite a
> `docs/architecture.md` that does not exist; they mean this file, and the §-numbers still line up.
> `docs/plans/wavee-native-backend-architecture.md` is a *different* doc (the live Spotify backend: transport,
> dealer, audio) and does not supersede this one.

This design is grounded in a 14-domain, 292-capability functional inventory of the production **WaveeMusic**
client (full report archived alongside the planning session) and in industry best practice:
Hexagonal **Ports & Adapters** + an **Anti-Corruption Layer**
([AWS](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/hexagonal-architecture.html),
[ACL/DDD](http://tpierrain.blogspot.com/2020/04/adapters-are-true-heralds-of-ddd.html)), and **Music
Assistant's** two-axis provider model + provider-mappings + per-provider feature declaration
([Music Assistant](https://www.music-assistant.io/music-providers/),
[dev docs](https://developers.music-assistant.io/)).

We deliberately do **not** copy WaveeMusic's structure (god-interfaces, one mega-DB, UI-thread orchestration,
booleans-for-availability). We keep its *functionality* and adopt a cleaner seam — see §8.

---

## 1. Purpose & the core bet

A music client is a **federation of sources**. A "source" is more than a catalog: a real Spotify account is
catalog **and** playback **and** remote/Connect state **and** session/auth **and** lyrics. Local files are a
peer source with their own catalog + playback. The UI must not know or care which source an item came from.

The bet: define **narrow, capability-segregated ports** (one per facet), let each source implement only the
facets it supports and **declare** them, and put a thin **aggregate** in front that routes single-item reads
to the owning source and merges collection reads across sources. Source-specific shapes (Spotify GraphQL,
local tags) are translated to clean domain models inside each adapter (the ACL) and never leak upward.

```
                 ┌──────────────────────────── UI (Wavee app: Home, Detail, Sidebar, PlayerBar) ───────────────────────────┐
                 │  reads engine Signals; talks to Services slots (IMusicLibrary, PlaybackBridge, …) — unchanged shapes     │
                 └───────────────────────────────────────────────┬───────────────────────────────────────────────────────┘
                                                                  │  (ports — the seam)
   ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
   │  AggregateCatalog : IMusicLibrary   (façade)        FederatedPlayback / FederatedRemote / FederatedSession (future)    │
   │   • route single-item reads by source.Owns(uri)     • route play(contextUri) to the OWNING source's player            │
   │   • merge collections (library / home / search)      • surface a unified active state to PlaybackBridge                │
   │   • provider-mappings + fallback chain (seam)        • merge device lists; route transfer to owner                     │
   └──────────────────────────────────────────────────────────────┬───────────────────────────────────────────────────────┘
                                                                  │  SourceRegistry — ordered list; each declares SourceCapabilities
   ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
   │  SpotifyExportSource   owns spotify:*     [Catalog,Home,Search]   ACL: GraphQL JSON → domain   (IMPLEMENTED)           │
   │  FakeSource            owns fake/synth     [Catalog,…]            wraps FakeData (synth/fallback) (IMPLEMENTED)        │
   │  LocalSource           owns local:*        [Catalog,Search,LocalDecode]  synth imported library    (IMPLEMENTED)      │
   │  FakePodcastSource     owns wavee:show:*    [Podcasts]                  synth shows/episodes       (IMPLEMENTED)      │
   │  LocalMutationSource   (cross-cutting)      [Mutations]                 optimistic + persisted outbox (IMPLEMENTED)   │
   │  FakePlayback/Session/ConnectDevices        [Playback|Lyrics / Session / Remote]  in-proc fakes implement seam ports │
   │  (future) SpotifyLiveSource  spotify:*     [Catalog,Playback,Remote,Session,Lyrics,Mutations]   real backend          │
   └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. The functional capability catalog (the requirements menu)

What a full client does, decoupled from any code structure. This is the menu the seam must be *able* to
express; the current pass implements the **Catalog** rows. Derived from the WaveeMusic inventory.

**Playback / audio.** Decode (OggVorbis/MP3/FLAC/AAC/…); per-source decrypt (Spotify AES-128-CTR vs local
none); instant-start (head-file) + progressive CDN download + seek with range refetch; **gapless** (prepare
next during current) + **crossfade**; loudness **normalization**/ReplayGain, **10-band EQ**, compressor,
limiter, volume; **quality tiers** (96/160/320, FLAC) with format **fallback**; audio-device enumeration +
hot-swap; underrun detection; preview-clip analysis; music-**video** switching.

**Queue & orchestration.** 3-bucket queue (**user-queue** → **context** → **post-context/autoplay**);
context resolution + pagination of long contexts; **autoplay/radio rollover** when a context ends; shuffle
(permutation) + repeat (off/context/track); **play-next** vs **add-to-queue** vs **skip-to**; natural
track-finish auto-advance; prefetch next; source-agnostic `QueueTrack` carrying a stable per-item `uid`.

**Spotify Connect / remote.** Device discovery + list (type, active); **transfer** playback to/from a device;
remote-control another device; **cluster state** sync (websocket); PutState cadence; per-device volume/seek
restrictions; playback attribution (session/playback ids); local↔cluster state merge with position smoothing.

**Library & saved-state.** Liked songs, saved albums/artists, followed artists/users, followed shows, pinned
items, recently played; full library sync + revision tokens; **optimistic** save/follow/pin with an **outbox**
+ retry + revision-conflict (409) fallback; **real-time deltas** (added/removed) over websocket; counts/badges;
liked-songs filters.

**Playlists, mutations & folders.** Detail + **skeleton-then-stream** track metadata with a known total up
front; **capabilities** (CanView / CanEditItems / CanEditMetadata / CanDelete / admin) + base permission
(Viewer/Contributor/Owner); collaborative **added-by**; create / add / remove / **reorder** / rename /
describe / set-cover / public-private / follow-unfollow; **folders/rootlist** create/move/rename/delete;
recommendations ("recommended songs"); session-control chips; revision-based mutations.

**Album & artist.** Album (cover, type, multi-disc, release date, pre-release, **alternate/deluxe editions**,
copyrights, label, related, merch, similar, **palette**, partial→full hydration). Artist (bio, monthly
listeners, followers, world rank, verified, latest release, **top tracks w/ playcount**, discography paging,
related, appears-on, **concerts**, merch, gallery, music videos, social links, pinned/watch-feed).

**Podcasts / shows / episodes.** Show (publisher, rating, topics, trailer, consumption order, video/mixed);
episode (**resume/progress** tracking, paywalled/preview, **transcripts** + languages, chapters); episodes
paging; your-episodes; recently-played episodes; **playback speed**; comments/reactions/replies (paged); save
progress (consumed within ~90s of end).

**Search, browse & home.** Search-all merged + per-chip (tracks/artists/albums/playlists/podcasts/users/
genres) with offset/limit + `hasMore`; **autocomplete** suggestions; **recent searches** (server-persisted);
**home feed** of personalized sections/shelves + facets + per-section item limits; **browse-all** categories;
browse pages (genres/moods/charts); feed-baseline lookup.

**Local files (peer source).** Watched-folder **scan**; **tag/metadata** extraction; content classification
(music/music-video/tv/movie); **enrichment** (TMDB / MusicBrainz / CoverArt / LrcLib); `local:`/`wavee:local:*`
URIs; **direct-decode** playback (no CDN/decrypt); subtitles; resume; local search scopes (local-only vs
all-cached); Spotify **linking/dedup**; rescan/last-scan/errors.

**Session / account.** Auth (OAuth PKCE / device-code / encrypted blob cache); token refresh; credential cache
(DPAPI); connection state machine; **account tier** (free/premium) gating; **market/region** restrictions;
locale; server-clock sync; per-user isolation, multi-account.

**Lyrics & transcripts.** Multi-provider fetch (Spotify/Musixmatch/LrcLib/…); line + **syllable** + character
timing; primary/secondary/tertiary text (translation); romanization; language detection; section headers;
playback-synced highlight.

**Now-playing extras & social.** Canvas/short-video loops; **color/palette theming** from art; Now-Playing-View
data; **friend activity/presence**; track **credits** by role; queue panel; connect/device bar; share links.

**Storage / offline.** Metadata cache (instant paint); audio cache; **cache-first + background refresh**;
partial snapshots; outbox; schema/migrations; cache-first browsing offline (playback blocked, mutations queued).

**Settings / misc.** Quality / crossfade / gapless / normalization / EQ toggles; explicit-content filter;
sleep timer; downloads/offline; media-key/SMTC; mini-player; notifications. (Several are gaps even in Wavee —
see the research report's §XV; treat as future.)

---

## 3. Cross-cutting behaviors (adopt these everywhere)

- **Cache-first instant paint** + a stale indicator while a background refresh runs.
- **Skeleton-then-stream**: render a header + placeholder rows immediately; stream per-item metadata in,
  rebinding rows in place (never block on a full fetch). The app already has this via `Loadable<T>` +
  `Skel.Region`; the seam's `StreamTracksAsync` feeds it.
- **Optimistic updates + outbox**: mutate locally, reconcile in the background with retry + conflict fallback.
  (Catalog is read-only this pass; this is the contract for `IMutationSource` later.)
- **Real-time sync**: a source may push deltas (library/cluster/queue) — modeled as `IObservable<…>` crossing
  into engine `Signal`s at the bridge (see §6).
- **Prefetch / autoplay rollover**, **palette extraction**, **partial→full hydration**.
- **Availability/playability gating** is applied at *queue time*, not buried in the UI.
- **Multi-source URIs** everywhere — never assume `spotify:`.

---

## 4. The seam — ports, adapters, ACL

### 4.1 Two provider axes (Music Assistant)

- **Source providers** = a *source of content + the operations on it* (Spotify, Local, Fake). They own a URI
  namespace and implement catalog/playback/remote/session/lyrics facets they support.
- **Player/target providers** = a *target of playback* (the local audio engine, a Connect device, a future
  Chromecast). Distinct axis. For now the only target is the local engine (the fake player); Connect devices
  are targets surfaced via `IRemoteSource`. Keep the axes separate so "where it plays" ≠ "where it came from".

### 4.2 Capability-segregated ports (no god-interface)

`Wavee.Core/Sources/`:

- `interface ISource { string Id; bool Owns(string uri); SourceCapabilities Capabilities; }`
- `interface ICatalogSource : ISource` — **implemented now** (matches `Sources/ICatalogSource.cs`):
  `Task<Playlist?> GetPlaylistAsync(uri)`, `IAsyncEnumerable<TrackPage> StreamTracksAsync(uri)`,
  `Task<Album?> GetAlbumAsync(uri)`, `Task<Artist?> GetArtistAsync(uri)`,
  `Task<IReadOnlyList<LibraryItem>> GetLibraryAsync()`, `Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync()`,
  `Task<IReadOnlyList<Album>> GetAlbumsAsync()`, `Task<IReadOnlyList<Artist>> GetArtistsAsync()`,
  `Task<IReadOnlyList<Track>> GetLikedSongsAsync()`, `Task<SearchResults> SearchAsync(query)`,
  `Task<HomeContribution> GetHomeAsync()`, `Task<LibraryStats> GetStatsAsync()`.
  The three single-item reads take a **hydration rung**: `GetXAsync(uri, HydrationLevel level = Open, ct)` — the
  source ensures that rung through its own `Hydrator` and only then reads (`IMusicLibrary` carries the same
  parameter, plus `GetShowAsync`).
- `ICatalogSource.Hydrator` (**DIM**, implemented now) — `IEntityHydrator Hydrator => CompleteEntityHydrator.Instance;`.
  A *default interface member* on purpose: a complete-at-construction source (export, local files, the fake,
  user playlists, every test fake) has nothing to fetch, so every rung is already reached and it implements
  nothing. Only a networked source overrides it. **There is no nullable hydrator anywhere.**

**The hydration façade (`Wavee.Core/Hydration/`) — implemented now.** ONE entry point for every catalog
metadata fetch/enrich in the app; the UI asks the catalog, playback asks the façade directly, and both land in the
same provider hydrator → per-kind ladder → store. Design: `hydration-facade-design.md`; phases:
`hydration-facade-plan.md`; how-to: `.claude/skills/wavee/hydration.md`.

- `interface IEntityHydrator` — `HydrationLevel LevelOf(uri)` (presence-only, synchronous, store-backed),
  `Task<HydrationOutcome> EnsureAsync(uri, level, opts, ct)`,
  `Task<HydrationBatchOutcome> EnsureManyAsync(uris, level, opts, ct)`,
  `Task EnsureTraitsAsync(uris, TraitSurface|TraitSet, …)`, `void Invalidate(uri)`.
  A transport failure is an **outcome** (`HydrationStatus.{Reached,Partial,Failed,Cancelled,Unsupported}`), never
  an exception out of the façade; traits are polish and swallow failures entirely. `HydrationOptions` carries
  `Mode` (Blocking/Background), `Revalidate`, `Surface`, `Priority`.
- `enum HydrationLevel { None, Identity, Open, Rich, Full }` + `static HydrationLevels.Of(entity)` — the per-kind
  rung predicates, **pure** (no store, no clock, no I/O). This is the ONE "is it cold?" predicate: it subsumes
  `IsAlbumOpenReady`, `IsAlbumComplete`, the four-clause artist gate, `HasMembership`, both `NowPlayingReady`
  copies, `ArtistStatsCache.IsFresh` and LibrarySync's "unnamed ⇒ cold" (§6, §9).
- `TraitSet` (flags) + `TraitSurface` (which screen asked) — a **trait** is a per-entity extended-metadata facet
  that decorates a row already in the store, never a row's identity. One POST carries every wanted kind for a uri
  set; `TraitPolicy` is the surface → bundle table, `TraitSurfaces.ClientFeatureId` the surface → attribution one.
- The three portable implementations (`Hydrators.cs`): `CompleteEntityHydrator` (nothing to fetch),
  `NotOwnedEntityHydrator` (no source owns this uri — `Unsupported`, not an error, so a mixed batch still
  completes), `SwitchableEntityHydrator` (the go-live/offline seam: one stable reference whose volatile inner
  flips between `SpotifyProviderHydrator` and `OfflineEntityHydrator`).
- `interface IOnlineCatalog` (`Wavee.Core/Library/OnlineCatalog.cs`; impl `SpotifyLive/Hydration/SpotifyOnlineCatalog.cs`)
  — the **online-read** half that is not hydration: search / suggest / home. It exists because those are
  return-only reads that never write the store, so they must not ride the hydration ladder. `StoreLibrarySource`
  takes it as a constructor dependency (it replaced four mutable hooks).
- **Seam ports (defined now, not implemented this pass)** — the contracts Connect/playback land on:
  - `interface IPlaybackSource : ISource` — `Task PlayAsync(contextUri, startIndex, ct)`,
    transport (pause/resume/next/prev/seek), `SetShuffle/SetRepeat/SetVolume`, `IPlaybackState State`,
    `Task<StreamHandle> ResolveAsync(trackUri)` (CDN/key/format for streamed; file path for local).
  - `interface IRemoteSource : ISource` — `IReadOnlyList<PlaybackDevice> Devices`, `IObservable<…> DevicesChanged`,
    `Task TransferAsync(deviceId)`, remote command surface, cluster state stream.
  - `interface ISessionSource : ISource` — auth/login, token, current user/profile, account tier, market, locale,
    connection state.
  - `interface ILyricsSource : ISource` — `Task<LyricsDocument?> GetLyricsAsync(trackUri)`.
  - `interface IMutationSource : ISource` — save/follow/pin, playlist create/add/remove/reorder/rename, folders
    (optimistic + outbox).
- `SourceCapabilities` (flags / the `supported_features` analog): `Catalog, Playback, Remote, Session, Lyrics,
  Search, Home, Podcasts, Mutations, LocalDecode`. The aggregate routes only to capable sources; **the UI gates
  affordances on declared capability** (don't hardcode buttons to a backend).

### 4.3 The aggregate / federation

- `SourceRegistry` — ordered `ISource[]`; lookups `OwnerOf(uri)` and `OfCapability(cap)`.
- **Hydration router (P4-A, landed).** `Services.Hydrator` is a `HydrationRouter : IEntityHydrator`
  (`Wavee.Core/Hydration/HydrationRouter.cs`) in BOTH `CreateReal` and `CreateFake`: it groups a batch by
  `SourceRegistry.OwnerOf(uri)` and forwards each group to that source's `ICatalogSource.Hydrator` — the same
  ownership answer routing already uses for single-item reads, so there is no second notion of "who owns this
  uri". Groups are forwarded in REGISTRY order, uris keep their first-seen order inside a group, and the merged
  `HydrationBatchOutcome` is (Reached ∪, Missing ∪, worst status). A uri no source owns gets
  `NotOwnedEntityHydrator` (`Unsupported`), which is why a mixed batch (a Spotify playlist holding a local import)
  completes instead of throwing. `StoreLibrarySource.Hydrator` is the very `SwitchableEntityHydrator` its own reads
  use, so a `spotify:` uri still lands in the offline→live seam; `SpotifyProviderHydrator` keeps enforcing the
  boundary itself as a second line of defence.
- `AggregateCatalog : IMusicLibrary` (the UI-facing façade, **implemented now**): single-item reads → owning
  source; collection reads → **merge** across capable sources (Library = union; Home = merge `HomeContribution`s
  by priority then cap; Search = merge per chip; Stats = sum). Unknown URI (or an owner with no data) → an EXPLICIT
  final step: the first source declaring `SourceCapabilities.Fallback` (the demo backend's `FakeSource`; the real
  backend registers none, so a real account never invents an entity). Being the last resort is a capability, not an
  ownership claim — `FakeSource.Owns` is `fake:`/legacy ids only, so routing and hydration reach the peer source
  that actually holds a `local:` / `wavee:playlist:` uri.
- `FederatedPlayback` / `FederatedRemote` / `FederatedSession` (**future**): route `play(contextUri)` to the
  owning source's `IPlaybackSource`; expose a **unified active state** to `PlaybackBridge`; merge devices; route
  transfer to the active source. With one real source today there is nothing to federate yet — these are the
  hooks Connect/playback attach to without touching the UI.
- **Playback federation now exists — as modules.** A source no longer has to live in this repo to be playable: a
  **playback module** is an out-of-process exe (JSON-RPC over stdio, written against `Wavee.Sdk`) that owns the
  uri namespace `wavee:module:<id>:<b64url(playableId)>` and enters the app the ordinary way — one
  `ModuleMediaProvider : IPlayableMediaProvider` per installed module, appended to `MediaProviderRegistry` after
  the built-ins, so the registry order still *is* the routing table and nothing between play-intent and a host
  names a source type. The manifest's `capabilities` become `MediaProviderCaps` (declared, never probed; absent =
  the simpler path), and the paste-a-link entry point is a `ModuleRouter.MatchAsync` prefilter over the manifests,
  not a hard-coded list. YouTube, Twitch and internet radio ship this way today; Spotify playback migrates to the
  same seam next (the *session* stays here — Connect, catalog, search and library need it, and the module reaches
  it through permission-gated host services). v1 module facets are **playback + paste-url ownership + metadata**;
  `search`/`browse` are declared in the manifest but not yet mapped onto `ICatalogSource`/`IOnlineCatalog`.
  How-to: `docs/guide/playback-modules.md`.

### 4.4 Anti-Corruption Layer

Each adapter owns its mapping. `SpotifyExportSource` holds `SpotifyExportMapper` which converts
`playlistV2` / `libraryV3` / `home` GraphQL JSON into the clean domain records below. No GraphQL/proto/HTTP
type crosses the port. A future `LocalSource` maps ATL tags the same way. This is where source-specific
weirdness (paging cursors, snapshot revisions, `__typename` unions) is absorbed.

---

## 5. Domain model (clean, source-neutral) — availability & permissions first-class

`Wavee.Core/Domain/Models.cs` (additive; all new fields defaulted):

- `Image(Url, Width?, Height?, BlurHash?)` — `Url` may be a local path **or** an `https://` CDN URL; the engine
  image pipeline (`DefaultImageFetcher` + `DiskImageCache`) fetches+caches either transparently.
- `enum TrackOrigin { Streamed, Local }`, `enum Availability { Playable, Unavailable }` (extensible to a
  `UnavailableReason { GeoBlocked, PremiumOnly, Delisted, … }` later).
- `Track(... , TrackOrigin Origin = Streamed, Availability Availability = Playable, string? Source = null)`.
- `Owner(Id, Name, Image? Avatar)`.
- `PlaylistCapabilities(CanView=true, CanEditItems=false, CanEditMetadata=false, IsCollaborative=false, IsOwner=false)`.
- `Playlist(... , Owner? Owner = null, PlaylistCapabilities Capabilities = default, string? Format = null, string? Source = null)`.
- **Provider-mappings (seam):** one logical item may exist in multiple sources. Modeled later as a
  `ProviderRef[]` + a preferred-source policy + fallback chain (Music Assistant). Not needed with one real
  source; the `Source`/`Origin`/`Availability` fields are the groundwork. Playback resolves availability at
  queue time and falls through the chain (Spotify → Local → error) when it exists.

`PlayableTrack` (future): the queue/playback layer operates on a track + resolved availability + preferred
provider, so geo/tier/region checks are baked in rather than re-derived in the UI.

---

## 6. Async / reactive model

- `Loadable<T>` (Pending/Ready/Failed) + `UseAsyncResource` load once at mount; `Skel.Region` **derives** the shimmer
  from the real content (`content(seed)` or a `rowTemplate`) and branches shimmer/content/empty/error at the reconcile
  edge — no hand-built skeletons, no `StatefulRegion` wrapper (see AGENTS.md "Async loading & skeletons").
- **Streamed tracks**: detail header renders immediately; the track list is a `Loadable<Track[]>` filled
  page-by-page from `ICatalogSource.StreamTracksAsync` (skeleton-then-stream).
- **`HydrationLevel` is not `Loadable`, and the two never overlap.** `Loadable<T>` is the state of ONE UI read
  (Pending / Ready / Failed) — a fact about a component's await. `HydrationLevel` is how complete the *entity in
  the store* is (`None → Identity → Open → Rich → Full`) — a fact about the data, shared by every surface showing
  it, and **presence-only**: age lives in the hydration ledger's per-`(kind, level)` TTL, never as a field on the
  enum. So a page is `Ready` with an `Identity`-rung album (name only, rows still landing) and later repaints at
  `Rich` off `IStore.Changes` without its `Loadable` ever leaving `Ready`. Rule of thumb: **`Loadable` decides
  shimmer-vs-content; the rung decides how much content there is.** A surface picks its rung when it asks
  (`GetAlbumAsync(uri, Rich)`); `OpenPolicy.For(kind)` is the one table saying which rung a page open *blocks* on
  and which it merely enqueues on the background pump.
- **Repaint on store change, not on the await.** `EnsureAsync(..., Background)` returns the rung resident at the
  caller's instant and enqueues the work; the caller re-reads through `IStore.Changes`. Everything a page does not
  block on (show episode pages past the first, below-the-fold album envelopes, prefetch waves, trait fills) goes
  on the one bounded, priority-ordered `HydrationPump` — negative priority = prefetch, and the queue sheds
  lowest-priority-first at its cap rather than growing.
- **Core → engine bridge**: framework-neutral sources expose `IObservable<T>`; `PlaybackBridge` subscribes and
  marshals each callback onto the UI thread via the `post` delegate, writing engine `Signal`s. This is the one
  boundary; replicate it for future remote/library delta streams (a `LibraryBridge`, a `RemoteBridge`).

---

## 7. URI namespacing & identity

- `spotify:track|album|artist|playlist:<base62>`, `spotify:collection:tracks` (Liked), `spotify:show|episode:…`.
- `local:` / `wavee:local:track:<hash>` for imported files.
- `fake:` (or the legacy `tr/al/pl{N}`) for the synthetic fallback.
- **Key by full URI** — real IDs are base-62, so never parse "trailing digits" for real entities. Synthetic
  tracks for a real-but-trackless entity seed deterministically off a **stable hash of the URI**.
- Cross-source identity (dedup/merge) keys on MusicBrainz MBID / ISRC when provider-mappings arrive.

---

## 8. What we do DIFFERENTLY from WaveeMusic (principles)

1. **No god-interface.** Capability-segregated ports; a source implements only what it supports and *declares*
   it. (Wavee has broad services wired as global singletons.)
2. **Availability & permissions are domain types**, resolved at queue/render time — not booleans sprinkled
   through DTOs and re-checked in views.
3. **UI affordances are capability-driven.** Edit/add/reorder render only when the playlist's
   `Capabilities.CanEditItems` (and the source's `Mutations` capability) say so. No dead UI stubs.
4. **A source registry/factory + provider-mappings** for routing, fallback, and dedup — instead of
   provider-specific branching in the pipeline.
5. **ACL at every adapter.** Source shapes never leak; the domain stays clean and testable.
6. **Keep the UI thread free** (async sources, the `post`-marshalled bridge).
7. **Defer (don't build now), because they're moot while playback is the in-process fake:** off-thread
   playback orchestration, multiple databases, event-sourced library deltas. Revisit when a real player/Connect
   source lands — the ports above make that additive.

---

## 9. Implementation status matrix (keep current)

| Capability / facet | Port | Status | Notes |
|---|---|---|---|
| Catalog: library, playlists, album, **artist** | `ICatalogSource` | **Implemented** | SpotifyExportSource (real JSON) + FakeSource; artist detail is a real page now (`ArtistPage`) |
| Home feed (grouped/condensed) | `ICatalogSource.GetHomeAsync` | **Implemented** | `SpotifyHomeComposer` collapses 22 baseline sections → 1 "Made for you" grid; caps shelves |
| Streamed tracks (skeleton-then-stream) | `StreamTracksAsync` | **Implemented** | Iced Americano = 15 real; others synthesized (seeded by URI) |
| Playlist permissions | `PlaylistCapabilities` | **Implemented** | from `currentUserCapabilities`; gates UI edit affordances |
| Per-track availability/origin | `Availability`/`TrackOrigin` | **Implemented** | from `playability`; local tracks carry `TrackOrigin.Local` |
| Real cover art | image pipeline | **Implemented** | real `i.scdn.co` URLs; HTTP/2 fetch + disk cache |
| Search | `ICatalogSource.SearchAsync` / `IOnlineCatalog` | **Implemented** | federated `SearchAsync` + a per-facet results page (`SearchPage`), live as-you-type from the omnibar; online search/suggest is a Pathfinder read behind `IOnlineCatalog` (P3), offline falls back to the store's full-text index |
| Browse (genre/mood directory) | `SpotifyBrowseService` | **Implemented** | `Features/Browse/*` — the browse directory + taxonomy pages over the Pathfinder browse queries |
| Pathfinder / GraphQL transport | `PathfinderClient` + `PathfinderResource` | **Implemented** | the layer `wavee-data-gaps.md` called "genuinely absent": `getAlbum`, `getTrack`, `queryArtistOverview`, browse, concerts. Reached only through the hydration ladders' `IEnvelopeFetch` (never called ad hoc) |
| **Hydration façade (levels / ladders / ledger / pump)** | `IEntityHydrator` | **Implemented** | `Wavee.Core/Hydration` ports + `Backend/Hydration` engine: `SpotifyProviderHydrator` over 7 kind ladders, `HydrationLedger` (`(locale, uri, level)` seals: Reached vs Exhausted; `Claim` shares one pass between overlapping callers), the bounded priority `HydrationPump`, `OpenPolicy`/`HydrationPolicy`. `MetadataService`/`IMetadataSource`/`EntityRef`/`StoreEntityGaps`/`DiscographyPrefetcher` and every per-service fetch helper are deleted |
| **Trait pipeline (one POST per surface, projectors)** | `ITraitProjector` / `TraitPolicy` | **Implemented** | `TraitPipeline` plans → ONE conditional POST per 300 uris → one lazy bulk window; 7 projectors (video 99/182, audio attributes 222, descriptors 6, visual identity 179, play counts 185, publishing 183, identity 178/220) + a shared `NegativeMemo`. Replaces the four trait services, their 7 caps, 7 etag-or-raw forks and 6 memos |
| **Extension reader (credits / prerelease / expansion / profiles)** | `IExtensionReader` | **Implemented** | the display-only XM read path (186 / 138 / 99+98+5+237 / 15): parsed answers cached *including null*, concurrent opens share one load, a nav-away detaches only that caller. Shares the negative memo with the trait pipeline |
| **Online catalog seam** | `IOnlineCatalog` | **Implemented (P3)** | search / suggest / home lifted out of `LiveSessionHost` statics and off `StoreLibrarySource`'s mutable hooks into a constructor dependency (`SpotifyOnlineCatalog`) |
| Video (music videos / canvas / PiP) | `VideoProjector` + `CompositeVideoResolver` | **Implemented** | detection is a trait (99 + 182, TRACK_V4/212 canonical recovery on the reader's shared load); playback surfaces are `Features/Video/*` (in-window PiP + pop-out) plus user `VideoOverrideService` overrides that work with no Spotify session |
| Row adornments (tempo / key / tags / cover tint) | `TraitSet.RowBundle` | **Implemented** | was `SpotifyTrackAdornmentService`; now four projectors on the shared trait POST — no separate service, cap or memo |
| **LiveWiring symmetric install/uninstall** | `Backend/Wiring/LiveWiring.cs` | **Implemented** | landed with P3 (`hydration-facade-plan.md`, 2026-08-16) — `Backend/Wiring/LiveWiring.cs` + `Backend/Wiring/LiveSeams.cs`: every go-live install registers its teardown; `GoOffline` = `Uninstall()` in reverse order (`App/Services.cs`), `AssertCovers(Services.LiveSeams)` names any seam that was installed without one |
| **Multi-source hydration router** | `HydrationRouter` over `SourceRegistry` | **Implemented** | landed with P4 (`hydration-facade-plan.md`, 2026-08-16) — `Wavee.Core/Hydration/HydrationRouter.cs` groups a batch by `SourceRegistry.OwnerOf(uri)` → that source's `ICatalogSource.Hydrator`; unowned = `NotOwnedEntityHydrator`, and `Services.Hydrator` is the router in both Create paths |
| **Episodes as playables in playlists** | `EpisodeAsTrack` + `JoinMembership` | **Implemented** | landed with P4/P5 (`hydration-facade-plan.md`, 2026-08-16) — `Wavee.Core/Domain/EpisodeAsTrack.cs` joined in `StoreLibrarySource.JoinMembership` and `Backend/PlaybackProjection.cs`, show paging + owners in the store, and episode rows render and play in playlist lists (show-name subtitle → `show:` route, EPISODE token, "Go to podcast" in `Actions/Menus.cs`) |
| Library collection pages (albums / artists / podcasts) | collection reads | **Implemented** | `LibraryGridPage` over `GetAlbums/Artists/ShowsAsync` (these routes were "Coming soon") |
| Mutations: save / like / follow | `IMutationSource` | **Implemented** | `LocalMutationSource` (optimistic + persisted outbox) + `LibraryBridge`; hearts/follow wired everywhere, capability-gated |
| Local files as a source | `LocalSource` | **Implemented** | owns `local:` / `wavee:local:*`; `TrackOrigin.Local`; opens via the sidebar Local row through the shared detail surface |
| Podcasts / shows / episodes | `IPodcastSource` | **Implemented (real)** | live: `StoreLibrarySource` declares Podcasts and serves real shows — ShowV4's `episode[]` lands as header + ordered membership, `ShowHydration` pages the first 300 episodes to Open and the rest on the pump. `FakePodcastSource` survives only in the pre-login fake registry (`wavee:show:*` / `wavee:episode:*`) |
| Playback (transport, resolve, gapless, DSP) | `IPlaybackPlayer` | **Implemented (live session)** | in-process local audio after login: `AudioPlaybackStack` (`FluentMediaAudioHost`, key/head-file resolve, gapless butt-join + crossfade, body disk cache). Pre-login `UnsupportedPlaybackPlayer` still toasts "choose a remote device"; Connect transfer to another device remains available |
| Spotify Connect / remote / devices | `IConnectDevices` | **Live-only** | pre-login `NoConnectDevices` (empty roster); the device picker + roster come from the live Connect cluster (`LiveConnectDevices`); no in-process `IRemoteSource` |
| Session / auth / account tier / market | `ISessionSource` | **Seam port live** | `FakeSpotifySession` implements `ISessionSource` + registered (Session) |
| Lyrics | `ILyricsProvider` | **Live-only** | pre-login `NoLyricsProvider`; the live `AggregatingLyricsProvider` swaps in on login (no in-process `ILyricsSource`) |
| Provider-mappings / dedup / fallback | `ProviderRef` / `ProviderPolicy` | **Model + hooks** | `ProviderRef`/`ProviderMapping`/`ProviderPolicy`/`PlayableTrack` groundwork; federation = `registry.OfCapability` |
| Mutations: playlist create / add / queue | `UserPlaylistSource` + `EnqueueAsync` | **Implemented** | create (sidebar +), add-to-playlist (default target), add-to-queue, batch selection actions — all wired with toasts; add-to-queue / play-next forward to the active remote device (else the "choose a remote device" toast) |
| Mutations: playlist picker / reorder / folders | `IMutationSource` | **Implemented** | the "choose which playlist" flyout is `Features/Detail/PlaylistPicker.cs` (+ the "More playlists…" dialog in `Actions/Menus.cs`); drag-reorder and folder CRUD are `Backend/Playlists/RootlistOps.cs` (`TryBuildMove`/`TryBuildMoves`/`BuildCreateFolder`) driven through `PlaylistMutationSource` |
| Federated playback / remote / session | `Federated*` | **Seam (deferred)** | per §4.3, deferred until a 2nd real source; the hook is `registry.OfCapability(cap)`, now exercised by the registered facet sources |

---

## 10. File map

- **Seam & domain (Wavee.Core):** `Domain/Models.cs` (incl. `Show`/`Episode`, `Owner`, and the provider-mapping
  groundwork); `Library/Library.cs` (IMusicLibrary + `TrackPage` + `StreamTracksAsync` + `GetHomeAsync` +
  `GetShows/ShowAsync`); `Library/HomeFeed.cs`; `Sources/SourceCapabilities.cs`; `Sources/ICatalogSource.cs` (defines
  `ISource` + `ICatalogSource`); `Sources/SeamPorts.cs` (defines `IPlaybackSource`/`IRemoteSource`/`ISessionSource`/
  `ILyricsSource`/`IMutationSource`/`IPodcastSource`); `Sources/SourceRegistry.cs`; `Sources/AggregateCatalog.cs`;
  `Sources/LocalMutationSource.cs` (Mutations); `Sources/UserPlaylistSource.cs` (user playlists); `Sources/LocalSource.cs`
  (local files); `Sources/FakePodcastSource.cs` (podcasts); `Sources/ProviderMappings.cs` (`ProviderRef`/`ProviderPolicy`/
  `PlayableTrack`); `Sources/CollectionEvents.cs` (the `ICollectionEvents` off-page library-delta seam, §3/§6).
- **Hydration ports (Wavee.Core/Hydration):** `EntityUri.cs` (THE uri vocabulary — `EntityKind`, `IdOf`, `Provider`,
  `IsPlayable`; no other file parses a uri); `HydrationLevel.cs` (`HydrationLevel` + the pure per-kind
  `HydrationLevels.Of` predicates + the row-gap primitives `TitleMissing`/`TrackUnnamed`/`RefNeedsName`);
  `IEntityHydrator.cs` (`HydrationMode`/`HydrationOptions`/`HydrationStatus`/`HydrationOutcome`/`HydrationBatchOutcome`);
  `Traits.cs` (`TraitSet` + `TraitSurface`); `Hydrators.cs` (`CompleteEntityHydrator` / `NotOwnedEntityHydrator` /
  `SwitchableEntityHydrator`). Also `Library/OnlineCatalog.cs` (`IOnlineCatalog`).
- **Hydration engine (`src/apps/Wavee/Backend/Hydration` — engine-free, `IStore`-level, compiled into `Wavee.Tests`
  by the `Backend\**` glob):** `Ports.cs` (the transport seams `ICatalogFetch`/`IEnvelopeFetch`/`IArtistChartFetch`/
  `IPlaylistOpener`/`IUserProfileFetch`/`ITraitPipeline`/`IKindHydration`, plus `HydrationContext` and the
  `HydrationRunScope` failure channel); `SpotifyProviderHydrator.cs`; `HydrationLedger.cs` (seals + `Claim`/
  `HydrationClaims`); `HydrationPump.cs`; `OpenPolicy.cs` (`OpenPlan` + the `HydrationPolicy` TTL table); the ladders
  `{Album,Artist,Playable,Playlist,Show,Collection}Hydration.cs` (`PlayableHydration` is registered twice — Track and
  Episode — which is why episodes are no longer dropped); `OfflineEntityHydrator.cs`; `TraitPipeline.cs` +
  `ITraitProjector.cs` (+ `TraitBatch`/`TraitPayloads`/`TraitApplicability`) + `TraitPolicy.cs` (+
  `TraitSurfaces.ClientFeatureId`) + `TraitProjectors.cs` (the registry) + `Projectors/*.cs`; `NegativeMemo.cs`;
  `ExtensionReader.cs`. Siblings: `Backend/Metadata/XmCatalogFetch.cs`, `Backend/Metadata/ExtensionEtagCache.cs`,
  `Backend/Wiring/LiveWiring.cs` (P3).
- **Spotify hydration adapters (`src/apps/Wavee/SpotifyLive/Hydration` — engine-free, its own test glob):**
  `PathfinderEnvelopeFetch.cs`, `SpclientArtistChartFetch.cs`, `LibrarySyncPlaylistOpener.cs`,
  `VisualIdentityProjector.cs` (lives here, not in `Backend`, because its target plane is the Spotify
  `CoverColorPlane`), `SpotifyOnlineCatalog.cs` (P3). `SpotifyVideoManifestResolver.cs` stays in `SpotifyLive/`
  proper — it returns FluentGpu media types, so it cannot live under an engine-free glob.
- **Hydration tests (Wavee.Tests):** `HydrationLevelsTests`, `HydrationLedgerTests`, `HydrationPumpTests`,
  `HydrationSharedRunTests`, `HydrationTransientFailureTests`, the per-ladder `*HydrationTests`,
  `Hydration/{TraitPipelineTests,ExtensionReaderTests}`, and the request-count pins in
  `ApiWaste/HydrationWasteTests` (which run the real stack down to a fake exchange and decode the gzipped
  `BatchedEntityRequest`).
- **Spotify adapter (Wavee.Core/Spotify):** `SpotifyExport.cs` (parse), `SpotifyExportMapper.cs` (ACL),
  `SpotifyHomeComposer.cs` (grouping), `SpotifyExportSource.cs`.
- **Fake adapter:** `Wavee.Core/Fakes/FakeSource.cs` (wraps `FakeData`); `FakeSpotifySession` = Session (the one
  in-process facet source still registered). Playback/Remote/Lyrics are no longer in-process sources — the pre-login
  stubs are `UnsupportedPlaybackPlayer` / `NoConnectDevices` / `NoLyricsProvider` (`Wavee.Core/Playback/UnsupportedPlayback.cs`),
  swapped for the live Connect stack on login.
- **Wiring/assets:** `src/apps/Wavee/App/Services.cs` (the unified `SourceRegistry`); `src/apps/Wavee/App/LibraryBridge.cs`
  (the Mutations Core→Signal bridge); `src/apps/Wavee/App/LibraryStore.cs` (the root collection + per-entity detail cache —
  cache-first instant navigation + off-page freshness, §3/§6); `src/apps/Wavee/Wavee.csproj`; `src/apps/Wavee/assets/spotify/*.json`;
  `assets/loc/en-US.json`.
- **UI:** `Features/Home/HomePage.cs`; the ONE shared detail surface `Features/Detail/{DetailPage, DetailShell,
  DetailRail, DetailTracks, DetailConfig, EpisodeList}.cs` — album / playlist / liked / local / user-playlist AND
  **podcast shows** (`DetailKind.Show` renders `EpisodeList` instead of the track table); the separate `ArtistPage.cs`
  (the magazine layout, the one exception); `Features/Search/SearchPage.cs` (filter chips + unified results +
  browse-all); `Features/Library/LibraryPage.cs` (the Albums / Artists / Podcasts **master–detail**, right pane reuses
  the shared detail surface); `Components/{NavPreview, SaveButton}.cs`. Reuse: `Loadable<T>`, `LibraryStore`,
  `Skel.Region`, `Surfaces.Artwork`, `PagedShelf`/`AutoGrid`/`SelectorBar`, `PlaybackBridge`, `LibraryBridge`.

---

## 11. References

- **Hydration:** `hydration-facade-design.md` (contracts + engine + ladders + traits — the shapes),
  `hydration-facade-plan.md` (phases, ownership, gates, status), `metadata-entry-points-inventory.md` (the ~110
  entry points this replaced), `.claude/skills/wavee/hydration.md` (how to hydrate an entity / add a trait / add a
  ladder), `.claude/skills/wavee/wiring-discipline.md` (no nullable seams, symmetric go-live/offline),
  `xm-kind-probe-overview.md` + `xm-playcount-handoff.md` (which extension kind carries what).

- WaveeMusic functional inventory (292 capabilities, 14 domains) — workflow research report (archived in the
  planning session transcript; sections XVI "Implications for a source-agnostic seam" and XVII "What to do
  differently from Wavee").
- Music Assistant providers: <https://www.music-assistant.io/music-providers/> · <https://developers.music-assistant.io/>
- Hexagonal / Ports & Adapters: <https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/hexagonal-architecture.html>
- Anti-Corruption Layer (DDD): <http://tpierrain.blogspot.com/2020/04/adapters-are-true-heralds-of-ddd.html>
