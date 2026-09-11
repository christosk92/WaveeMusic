# Wavee music-domain architecture

Current ownership reference for the seam between UI and providers. The approved change contract and implementer
wireframes live in [catalog-state-replacement-implementation.md](catalog-state-replacement-implementation.md).
Implementation status is established by the orchestrator's build/test report, not historical gate counts.
The old hydration facade and native-backend proposal remain historical research; their API shapes are superseded.

## 1. Purpose & the core bet

Each fact has one canonical owner. Views observe typed immutable projections and declare which resources they need.
Providers translate finite wire responses into owned catalog patches. Playlist/rootlist/collection synchronization
owns confirmed state plus durable pending operations. UI components own interaction and presentation lifetime.

```text
Viewport/lifecycle                     User command
   | acquire + set demand                  | stage intent
   v                                       v
QueryService <---- pure reads ---- LibraryReplicaCoordinator
   |  |                               confirmed + pending
   |  +--> ResourceCoordinator                 |
   |          | provider request              |
   |          v                               |
   |       decoded patches/seeds              |
   |          |                               |
   |          +-------- DataCommitQueue ------+
   |                    persist -> publish all owners
   |                              |
   +<-- dependencies ------ CatalogRepository
   |                        normalized facet facts
   v
QuerySignalBinding -- UI post --> stable Signal<QuerySnapshot<T>>
   |                                  |
   +-- park/unmount demand             v
                                BoundItems / live props
                                retained rows + focus + pager
```

This is an application of ports/adapters and normalized observable state, sized to concrete Wavee queries. It is
not a new generic query language. The backend has no FluentGpu dependency; engine-facing signals and hooks stay
under `App`/`Features`. Provider protocol types stop at the decoder boundary.

## 2. The functional capability catalog (the requirements menu)

What a full client does, decoupled from any code structure. This is the menu the seam must be *able* to
express; these rows describe the product capability vocabulary, not an implementation status matrix. Derived from the WaveeMusic inventory.

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

## 3. Cross-cutting behaviors

- Resource identity includes provider, authenticated account and response-shaping context, subject, facet, and page
  arguments. Locale/market/account switches cannot share incompatible cached answers or accept stale responses.
- Knowledge (`Unknown`, `Present`, `Absent`, `Unsupported`), activity, freshness and error are separate fields.
  A failed fetch can coexist with a present last-good value. An unanswered facet is not an authoritative absence.
- Inline data fills unknown fields with explicit provenance. It cannot overwrite newer authoritative fields or mark
  an incomplete child object fresh. Provider field patches distinguish omitted fields from explicit clears/zeroes.
- Duplicate occurrences remain distinct from entity URI. Metadata publication does not become an order change.
- Local disk work, persistence and transport are asynchronous. Pure projection reads use a short consistent
  publication boundary; no await/network is allowed under that boundary.

## 4. The seam - ports, adapters, ACL

### 4.1 Provider capabilities

`SourceRegistry` identifies the owning source and its declared capabilities. `ICatalogSource` is a finite native
source input; `NativeCatalogResourceProvider` normalizes its results. Live Spotify uses its catalog resource
provider and decoders. `AggregateCatalog` exposes finite library reads through typed queries. Source selection
must not rely on a second metadata cache or on one provider's fallback answering another provider's resources.

Playback, remote devices, session, lyrics, browse and mutation capabilities retain their narrow domain ports.
Playback modules use the documented module host boundary in [playback-modules.md](../../guide/playback-modules.md).
Transport resources may return finite display-only data where no canonical catalog fact is being produced.

### 4.2 Catalog and query ports

Authoritative shapes are in `Wavee.Core/Catalog/Resources.cs`, `Values.cs`, `Patches.cs` and `Queries.cs`.

| Contract | Responsibility |
|---|---|
| `CatalogRepository` | Resident normalized facets, exact-context cold reads, provenance-aware reduction, fenced acceptance |
| `ICatalogResourceProvider` | Finite provider request batch -> owned patches, explicit outcome, inline child seeds |
| `IResourceCoordinator` | Request admission, union/coalescing, priority, freshness, bounded retry and cancellation |
| `QuerySpec<T>` | Concrete domain query identity, including current `CatalogScope` |
| `IQueryHandle<T>` | Current/replayed immutable result, leased demand, explicit refresh, disposal |
| `IQueryDefinition<T>` | Pure dependency-tracked `Read` and pure resource/replica `Requirements` |
| `IAsyncQueryDefinition<T>` | Finite local preparation returning a new immutable pure definition |
| `IQueryReplicaDemand` | Route explicit query needs to lazy replica cache and session protocol reads |

`Acquire` is passive observation and may read consumed local cache entries. `SetDemand` authorizes network work.
`ReadOnceAsync` waits for finite preparation, cold reads and the requested dependency closure. `RefreshAsync`
explicitly revalidates that closure. Optional failures stay in the snapshot; no success-shaped empty fallback.

### 4.3 Replica and durable publication ownership

`LibraryReplicaCoordinator` owns confirmed baselines, revision heads, pending intent order and the effective fold.
The mutation engine stages commands durably before optimistic publication. The protocol sender owns HTTP/Dealer
interpretation, retry/rebase/verification decisions and acknowledgements; it does not maintain a second effective
playlist. Native mutation providers use the same staged lifecycle.

`DataCommitQueue` serializes staging/reduction/persistence and publishes the owners only after durability succeeds.
Catalog observations and matching replica/intent changes commit together. All state assignments finish before
`NotifyAfterPublish` invokes observers, including a synchronous observer re-entering a query. Observer exceptions
cannot roll back or disguise a completed durable commit. Queries share the same short publication gate for reads.

Bootstrap loads only the actual authenticated owner's thin roots/collection state. Deep playlist baselines load
lazily with `EnsurePlaylistCachedAsync`. Unknown and known-empty collections have different readiness. Curation,
confirmed replica data, pending intents, and query demand pins are not disposable metadata cache entries.

### 4.4 Anti-corruption boundary

Spotify protobuf, Pathfinder JSON, etags and transport bytes remain provider-side. Decoders emit closed domain
values and patches. Provider-specific payload facts may be retained as typed facets; views do not decode wire
bytes or decide transport freshness. A facet's ownership and absence semantics belong beside its decoder/policy,
not in per-surface fetch helpers. `EntityUri.Parse` remains the shared URI vocabulary.

## 5. Domain model - identity, occurrences, knowledge

A materialized `Track`, `Playlist`, `Album`, `Artist`, `Show` or Home card is a read result, not a writable cache row.
Queries join identities and relationships from normalized facts. Playlist/queue occurrence identifiers, contributor
and context data belong to the occurrence and survive title/artwork changes. Owner/contributor profiles join by
normalized identity; an active playlist demand asks for every row's `AddedBy` with the rest of the model.

`QuerySnapshot.Resources` is copied snapshot knowledge for all consumed/requested resources. `Problems` represents
actual error/absence/unsupported outcomes. `HasPrimaryData` is a query-specific predicate: empty known membership
is ready, unknown membership is pending, and a playlist header may paint before `MembershipLoaded` is true.
Unloaded optional play counts/descriptors do not hide an otherwise usable page. Runtime playback overrides remain
playback projection state; they do not rewrite canonical catalog identity.

## 6. Async / reactive model

1. A mounted view acquires one typed handle with the current scope. It subscribes through `QuerySignalBinding`,
   which coalesces background publications into one queued UI post and retains one stable signal.
2. The page publishes one whole-model `QueryDemand(Active, Priority, Facets)`. Facets are the displayed/sort/filter
   fields applied to every row. The catalog fetches required keys in concurrent 300-subject batches (max 4 in flight).
3. The query's pure read joins current facts and records dependencies. A catalog/replica publication recomputes
   affected queries; it never calls network from the read function. Unchanged rows/order can be structurally shared.
4. Resource planning unions requirements of active leases. The coordinator coalesces transports and applies its
   central policy. A canceled waiter does not cancel a different view's requirement.
5. Park sends `QueryDemand.None`, invalidates queued delivery, and suppresses UI updates. Resume delivers the latest
   snapshot once and restores the saved demand. Unmount disposes the subscription/lease. Scope changes reacquire.

Cold reads count as loading until they finish. Offline unknown data surfaces an unavailable cached-copy state;
known cached data stays visible. Explicit refresh failures preserve previous primary data. Unexpected terminated
query observables expose `QuerySignalBinding.Failure` on the UI thread instead of being silently swallowed.

For FluentGpu, plain `Embed.Comp` factory fields are mount seeds. Explicit re-pushed props are live. `PagedShelf`
accepts an immutable item snapshot and internally retains one `BoundItemsSource`; callbacks receive the current
item. Metadata changes retain row occurrence keys, pager, viewport, popup and focus. `Responsive` re-pushes its
builder. Direct virtualized lists use `BoundItems`/`ItemsView.CreateBound`; a source revision is not a remount key.
The engine owns keyed tree reconciliation and layout; application metadata repair loops are removed.

## 7. URI namespacing & identity

Use `EntityUri` to parse provider, kind, id and playability. Full resource keys also include catalog scope and
canonical page arguments. Redundant numeric next cursors equal to the offset normalize to offset-only relation
keys. Opaque provider cursors retain their identity. A repeated URI is not a duplicate occurrence to discard.

A generated playlist can change `Edition`/`NextUpdateAt` without changing its URI or title. Home stores ordered
references and joins the latest playlist header. `ActiveCatalogDemand` shares expiry/revision work across active
views; `DaylistNotifier` observes accepted header facts for OS scheduling. No Home overlay, timer per card, content
fingerprint remount, or whole-feed refresh is needed for that header change.

## 8. Implementation rules

- Replace obsolete owners outright. No hydrator compatibility adapter, levels, per-view entity dictionary, or
  refresh/reconcile helper that copies canonical metadata between models.
- Preserve necessary reducers: protocol replica folding, playback state transitions, engine tree reconciliation,
  and OS toast schedule reconciliation each reconcile their own distinct external or interaction state.
- `RelationProjection` holds a prior coherent result while matching replacement pages arrive; never append an old
  snapshot's tail. HomeSection has no wire snapshot token, so its local first-page generation forces and fences
  replacement tail requests. It cannot claim stronger remote snapshot consistency than the protocol supplies.
- The metadata cache is normalized and disposable under `CatalogCacheMaintenance`. Current leased demand keys are
  pins. Playlist replicas, outbox and user video attachment curation have separate durability ownership.
- Backend tests use real catalog/query/replica behavior with fake clock/transport/persistence. UI lifetime and
  retained props require mounted engine gates. Never test by reading production source text.

## 9. Implementation status and verification

The replacement code paths are the current implementation; the approved plan defines the full acceptance work.
Do not inherit test counts or `Implemented` claims from the 2026-08 hydration plan. The orchestrator runs Debug and
Release builds, Wavee tests, engine gates for engine changes, and required release tests. Subagents write code and
behavioral tests but never build/test/launch against the shared output directories.

Key suites include `CatalogRepositoryTests`, `QueryServiceBehaviorTests`, `RelationProjectionTests`,
`HomeSectionQueryTests`, `ActiveCatalogDemandTests`, `DataPublicationTests`, `HomeQueryReadinessTests`,
`DetailQuerySubscriptionTests`, `QuerySignalBindingTests`, and real replica/outbox vertical tests. The engine's
shelf binding gates cover same-count updates, retained actions/focus/pager, park/resume and measurement.

## 10. File map

| Location | Responsibility |
|---|---|
| `src/apps/Wavee.Core/Catalog/` | Closed resource/value/patch/query contracts |
| `src/apps/Wavee.Core/Sources/` | Source registration, capability ports and finite aggregate library API |
| `src/apps/Wavee/Backend/Catalog/` | Repository, coordinator/policy, ingress/decoder, shared commit queue, active deadlines |
| `src/apps/Wavee/Backend/Queries/` | Typed definitions, consistent read joins, relation projections and demand routing |
| `src/apps/Wavee/Backend/Sync/` | Replica coordinator and protocol synchronization |
| `src/apps/Wavee/Backend/Persistence/` | Normalized catalog/replica/curation persistence and cache maintenance |
| `src/apps/Wavee/SpotifyLive/Catalog/` | Spotify live envelope transport and provider integration |
| `src/apps/Wavee/App/Queries/` | Engine signal and view-lifecycle adapters |
| `src/apps/Wavee/Features/{Detail,Home,Library,Search,Recents,Sidebar}/` | Query consumption and interaction state |
| sibling `fluent-gpu/src/FluentGpu.Controls/PagedShelf.cs` | Re-pushed immutable shelf input over retained bound items |

## 11. References

- [Approved implementation plan](catalog-state-replacement-implementation.md): migration contract, code sketches,
  wireframes and verification obligations.
- [Catalog state skill](../../../.claude/skills/wavee/catalog-state.md): practical read/demand/ingress/lifecycle rules.
- [Sidebar platform](../../guide/sidebar-extension-platform.md): sidebar layout documents and extension boundaries.
- [Playback modules](../../guide/playback-modules.md): module protocol and source capability boundaries.
- Sibling engine `docs/design/subsystems/component-props-contract.md`: live props versus mount seeds;
  `virtualization.md`: bound item identity/revision and retained viewport behavior.
