# Wavee live data-layer — gap report & plan

*Ultracode audit (8 dimensions, verified against source 2026-06-27; full run: workflow `wavee-data-gap-audit`).*

> **Status note 2026-08-16 — the hook mechanism this plan introduced is gone.** `OnDemandFetch` (Stage 1's
> "inject a fetch hook into `StoreLibrarySource`") had grown to **nine** mutable hooks on that class by the time it
> was audited (`metadata-entry-points-inventory.md`), each with its own freshness rule, its own 300-cap and its own
> silent no-op when go-live never wired it. All nine are **deleted**, along with `MetadataService`, `IMetadataSource`,
> `EntityRef`, `StoreEntityGaps` and `DiscographyPrefetcher`. They are replaced by ONE constructor dependency:
> `IEntityHydrator` (`Wavee.Core/Hydration`), with the engine in `src/apps/Wavee/Backend/Hydration` and the Spotify
> transports in `SpotifyLive/Hydration`. `StoreLibrarySource.GetXAsync(uri, HydrationLevel)` now *awaits a rung* and
> then reads the store; the online-read hooks (search / suggest / home) became the separate `IOnlineCatalog`
> constructor dependency. Class B (Pathfinder) is also no longer absent — `PathfinderClient`/`PathfinderResource`
> ship, reached only through the ladders' `IEnvelopeFetch`.
> See `docs/plans/wavee/hydration-facade-design.md`, `hydration-facade-plan.md`, `docs/plans/wavee/architecture.md`
> §4.2/§9 and `.claude/skills/wavee/hydration.md`. The gap *table* below is still a useful record of what was broken;
> read the fix column as historical.

## Core verdict
Two distinct failure classes were conflated:

- **Class A — wiring bugs (no new layer).** Saved playlists/albums open EMPTY not because we lack data, but because the live GUI bootstrap never fetches on open. `StoreLibrarySource.GetPlaylistAsync` is a pure store read; `JoinMembership` inner-joins membership→hydrated tracks and **drops** members with no track row, so it returns `[]` until something both sets membership AND hydrates tracks. That something — `PlaylistFetcher.FetchPlaylistAsync` + `MetadataService` — **already exists and is proven by `--spotify-sync`**, but `LiveSessionHost` passes a **no-op hydrator** (`(_,_) => Task.CompletedTask`) and wires no metadata service / dealer router / fetch-on-open. **Fix = wiring (S).**
- **Class B — the Pathfinder/GraphQL layer is genuinely absent.** `Channel.Pathfinder` is dead (the transport ignores the channel). This blocks every discovery surface with no protobuf equivalent: **artist overview + discography, online search, editorial home, browse, album rich-parity.** **Fix = a new `PathfinderClient` subsystem (M–L).**
- **Class C — client-side only.** Mosaic covers: album art is already on hydrated tracks; needs derivation + a 4-tile DSL render. Reactive recompute is **free** via the existing `Store.Changes → CollectionsChanged` spine (just don't memoize tiles on the immutable header).

## Gap table (severity / needs-Pathfinder)
| Area | Missing | Sev | PF |
|---|---|---|---|
| **playlists** | tracks not fetched on open (no-op hydrator; FetchPlaylistAsync exists) | H | no |
| **albums** | no on-open fetch **+** lean proto discards `disc.track` (`LeanCountStub`) | H | no(+proto) |
| **artists** | overview never fetched; discography hardcoded to `FakeData`; no proto equivalent | H | **yes** |
| **mosaic covers** | cover-less playlists blank; no 2×2 compositor | H | no |
| **reactivity** | derived cover not recomputed on track change; detail cache not invalidated on push; HomePage effect leak | H/M/L | no |
| **search** | offline track-only; no online catalog search; `SearchResults` too narrow | H | **yes** |
| **home** | store re-shelf only; no editorial/personalized feed | H | **yes** |
| **profile** | display-name/avatar never fetched → greeting = user-id hash | H | no |
| **follow/save** | fabricated REST path + StubTransport → reverts on next sync | H | no |
| **radio/autoplay** | stops at EndOfContext; artist radio pill = plain Play | H | no(spclient) |
| **podcasts** | `LeanShow` discards `episode[]` | H | no(proto) |
| **lyrics** | fake provider never swapped on GoLive | M | no |

## Staged plan (highest-unblock first)
1. **Stage 1 — fetch tracks ON OPEN** (S playlists / M albums). Inject `OnDemandFetch` hook into `StoreLibrarySource`; build the real `MetadataService`+`PlaylistFetcher` in `LiveSessionHost` and wire the hook; `DealerRouter` in the live GUI; album on-open + widen `lean_metadata.proto` `LeanDisc.track`. **← the headline fix.**
2. **Stage 6a/6b early — profile + persistent follow/save** (S/M, no PF). Real `WaveeUser` from spclient `/user-profile-view`; rewrite `SetReplayStrategy` to POST `/collection/v2/write` with the wire set names + swap StubTransport on GoLive.
3. **Stage 3 — mosaic covers + reactive recompute** (S–M, no PF). `MosaicTiles` on `PlaylistSummary`; derive read-through from membership album-art; `Surfaces.Mosaic` 4-tile render; recompute is free (don't memoize). Fix the HomePage effect leak + detail-cache invalidation.
4. **Stage 2 — `PathfinderClient`** (M–L). POST `api-partner.spotify.com/pathfinder/v2/query`; reuse the login5 bearer + client-token (same audience — key de-risk); its OWN pipeline with per-op header matrix; **source-gen JSON** (AOT); a `PathfinderOperations` hash table with graceful 400/`PersistedQueryNotFound` fallback.
5. **Stage 4 → 5 — online search, then real home + artist pages + discography** (M each, consume PF; reuse the existing `SpotifyHomeComposer` / `SpotifyExportMapper` mappers — the bundled JSON proves the shape).
6. **Stage 6c–6g — lyrics, radio/autoplay, podcast episodes, recently-played, connect volume** as capacity allows.

Key files: `LiveSessionHost.cs:62` (no-op hydrator) · `StoreLibrarySource.cs:35-41,141-154` · `PlaylistFetcher.cs:32-39` · `SpotifyLibrarySync.cs:32-35,62-64` (proven wiring) · `Transport.cs:15`+`LiveDealerTransport.cs:49-59` (dead channel) · `Services.cs:127,134` · `Mutation.cs:50-55`.
