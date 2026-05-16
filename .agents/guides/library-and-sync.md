---
guide: library-and-sync
scope: Every code path that reads, writes, syncs, or persists the user's Spotify library — collection sync, playlist cache, dealer-driven incremental updates, save/pin/follow write paths, and every library/playlist UI surface that reads from the database.
last_verified: 2026-05-16
verified_by: read+grep over src/Wavee/Core/Library/Spotify, src/Wavee/Core/Playlists, src/Wavee/Core/Storage, src/Wavee/Connect/LibraryChangeManager.cs, and src/Wavee.UI.WinUI/Data/Contexts
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Library And Sync Inventory

This guide is for agents changing anything in the user-library lifecycle:
collection sync (Liked Songs, Saved Albums, Followed Artists, Saved Shows,
Pinned items, Listen Later, Enhanced playlists, Bans), persistence to SQLite,
dealer-driven incremental updates, save / pin / follow / subscribe write
paths, and the library / playlists / pinned UI surfaces. Use it to answer
"where is this collection synced?", "where does this DTO come from?", "what
fires when the user pins an item from their phone?" without re-grepping.

Out of scope here:
- **How individual track / episode rows render** — that's
  `.agents/guides/track-and-episode-ui.md`. Cross-link from there for the row
  controls (`TrackItem`, `TrackDataGrid`, `TrackListView`, behaviours).
- **Spotify Connect / cluster state** — see
  `.agents/guides/connect-state.md`. The dealer pipeline is mentioned here
  only where it feeds library changes.
- **Playback orchestration, audio host IPC, local media** — different
  subsystems.

## How To Use This Guide

1. Skim the **Quick-find table** to locate your surface.
2. Read the **Core contracts** section before adding a new method on
   `ISpotifyLibraryService` / `ILibraryDataService` — almost every collection
   already has a sync + read + write parallel implementation.
3. The **Sync lifecycle** and **Dealer-driven liveness** sections describe
   the two complementary refresh paths. Most "library doesn't update" bugs
   live in one of those two flows.
4. If you add / remove a sync set or a write path, update this file (Quick-
   find table + frontmatter `last_verified`).

Useful re-verification commands:

```
rg -n "interface ISpotifyLibraryService|interface ILibraryDataService|interface IMetadataDatabase|interface IPlaylistCacheService|interface ITrackLikeService" src/Wavee src/Wavee.UI src/Wavee.UI.WinUI
rg -n "public Task SyncTracksAsync|public Task SyncAlbumsAsync|public Task SyncArtistsAsync|public Task SyncShowsAsync|public Task SyncListenLaterAsync|public Task SyncYlPinsAsync|public Task SyncBansAsync|public Task SyncArtistBansAsync|public Task SyncEnhancedAsync|public async Task SyncPlaylistsAsync" src/Wavee/Core/Library
rg -n "SpotifyLibraryItemType\." src/Wavee src/Wavee.UI.WinUI
rg -n "LibraryDataChangedMessage|PlaylistsChangedMessage|RequestLibrarySyncMessage|LibrarySyncStartedMessage|LibrarySyncCompletedMessage|LibrarySyncFailedMessage|LibrarySyncProgressMessage|AuthStatusChangedMessage" src/Wavee.UI.WinUI
rg -n "AddSingleton<I(SpotifyLibraryService|LibraryDataService|PlaylistCacheService|TrackLikeService|PlaylistPrefetcher|MetadataDatabase)>" src/Wavee.UI.WinUI
```

## Quick-find Table

| Surface | Host file:line | DTO / Contract | Source binding |
| --- | --- | --- | --- |
| Library service (sync + write + read) | `src/Wavee/Core/Library/Spotify/ISpotifyLibraryService.cs:15` | `ISpotifyLibraryService` | core protocol entry; one instance per `Session` |
| Concrete implementation | `src/Wavee/Core/Library/Spotify/SpotifyLibraryService.cs` | — | DI-registered singleton |
| Per-collection sync state | `src/Wavee/Core/Library/Spotify/SpotifySyncState.cs` | `SpotifySyncState`, `CollectionSyncState` | `ISpotifyLibraryService.GetSyncStateAsync` |
| Library item type enum | `src/Wavee/Core/Library/Spotify/SpotifyLibraryItemType.cs` | `SpotifyLibraryItemType` | DB foreign-key column on `spotify_library.item_type` |
| Outbox table for write retries | `MetadataDatabase.cs` (`library_outbox` schema) | `LibraryOutboxOperation` enum | `_database.EnqueueLibraryOpAsync` + `ProcessOutboxAsync` |
| Outbox processor | `SpotifyLibraryService.cs:~860` (look for `ProcessOutboxAsync`) | per-op retry up to 10× | fire-and-forget after every write |
| Metadata DB | `src/Wavee/Core/Storage/MetadataDatabase.cs` | `IMetadataDatabase` | SQLite, schema v21, DI singleton |
| Cached entity row | `MetadataDatabase.cs:4631` | `CachedEntity` | returned by `GetSpotifyLibraryItemsAsync` INNER JOIN |
| Library change manager (dealer push) | `src/Wavee/Connect/LibraryChangeManager.cs:46–50` | `LibraryChangeEvent`, `LibraryChangeItem` | subscribes to `DealerClient.Messages` matching `hm://collection/*`, `hm://playlist/*`, `*collection-update*` |
| Per-set URI classifier | `LibraryChangeManager.cs:261–320` (`DetermineSetFromUri`) | path-segment parse | maps `hm://collection/<set>/<user>` to the canonical set name |
| Playlist cache service | `src/Wavee/Core/Playlists/PlaylistCacheService.cs` | `IPlaylistCacheService`, `CachedPlaylist`, `RootlistSnapshot` | per-playlist + rootlist hot/warm/cold cache |
| Mercury ops applier | `PlaylistCacheService.cs` (`TryApplyMercuryOpsAsync`) | playlist `Op` protobuf list | dealer push when `FromRevision == cached.Revision` |
| Sync orchestrator | `src/Wavee.UI.WinUI/Data/Contexts/LibrarySyncOrchestrator.cs:21` | runs every `Sync*` on auth + reacts to dealer pushes | listens to `AuthStatusChangedMessage`, `RequestLibrarySyncMessage`, `concrete.LibraryChanged` |
| UI library data service | `src/Wavee.UI.WinUI/Data/Contexts/LibraryDataService.cs:35` | `ILibraryDataService` | DTO-returning facade; coalesces `DataChanged` / `PlaylistsChanged` events with 150 ms window |
| In-memory save state | `src/Wavee.UI.WinUI/Data/Contexts/TrackLikeService.cs:22` | `ITrackLikeService` | O(1) `IsSaved(type, uri)` lookup; drives heart-button glyph |
| Playlist prefetcher | `src/Wavee.UI.WinUI/Services/PlaylistPrefetchService.cs:25` | `IPlaylistPrefetcher` | post-auth warm-up; max 4 concurrent playlist fetches |
| Library page (Albums / Artists / Liked / Podcasts segments) | `src/Wavee.UI.WinUI/Views/LibraryPage.xaml` | `SegmentedItem` ⇄ view-model swap | `LibraryPageViewModel`; rows go through track-and-episode-ui guide |
| Liked Songs page | `src/Wavee.UI.WinUI/Views/LikedSongsView.xaml:60` | `LikedSongDto` via `ITrackItem` | `LikedSongsViewModel.FilteredSongs` |
| Your Episodes page | `src/Wavee.UI.WinUI/Views/YourEpisodesView.xaml:189, 658, 847` | `LibraryEpisodeDto` via `ITrackItem` | `YourEpisodesViewModel.VisibleEpisodes` / `Episodes` |
| Sidebar Pinned section | `src/Wavee.UI.WinUI/ViewModels/ShellViewModel.cs:666–678` | `PinnedItemDto` | `ILibraryDataService.GetPinnedItemsAsync` |
| Sidebar inline pin/unpin button | `src/Wavee.UI.WinUI/Controls/Sidebar/SidebarItem.cs` (`UpdatePinButton`) | `SidebarItemModel.ShowUnpinButton / ShowPinToggleButton / IsPinned` | `ShellViewModel.HandleSidebarPinButtonAsync` |
| Heart button on track rows | `src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml(.cs)` via `TrackBehavior` | `ITrackItem` | reads `ITrackLikeService.IsSaved(Track, uri)`; calls `ILibraryDataService.SaveTrackAsync` / `RemoveTrackAsync` |
| Save/Follow/Subscribe on entity pages | `Views/AlbumPage.xaml.cs`, `Views/ArtistPage.xaml.cs`, `Views/ShowPage.xaml.cs` | per-page VM | `SaveAlbumAsync` / `FollowArtistAsync` / `SubscribeShowAsync` on `ILibraryDataService` |

## Core Contracts

### `ISpotifyLibraryService` — `src/Wavee/Core/Library/Spotify/ISpotifyLibraryService.cs`

Top-level API for everything that touches Spotify's server-side library. One
instance per `Session`; DI-registered as a singleton in
`AppLifecycleHelper.ConfigureHost`.

Public surface, organized into three groups in the file:

**Sync operations** (idempotent, sync-token aware):
- `SyncAllAsync` — runs every other `Sync*Async` in sequence. Currently
  unused by the UI orchestrator (which inlines the loop with progress).
- `SyncTracksAsync`, `SyncAlbumsAsync` — both ride on Spotify's
  `collection` set (mixed Tracks + Albums) but use different revision keys
  (`collection:Track`, `collection:Album`).
- `SyncArtistsAsync` — the `artist` set.
- `SyncShowsAsync` — the `show` set.
- `SyncBansAsync`, `SyncArtistBansAsync` — banned tracks / artists. Stored
  but not currently surfaced in the UI.
- `SyncListenLaterAsync` — the `listenlater` set (saved episodes). Mixed-type
  (`spotify:episode:` URIs dominate).
- `SyncYlPinsAsync` — the `ylpin` set (Your Library Pinned). Mixed-type;
  runs `BackfillPinnedPlaylistMetadataAsync` after `SyncCollectionAsync` to
  resolve any pinned playlist whose `entities.title` is still the URI
  placeholder.
- `SyncEnhancedAsync` — the `enhanced` set (enhanced-playlist track
  augmentation). Defined but currently not called from
  `LibrarySyncOrchestrator`.
- `SyncPlaylistsAsync` — fetches the rootlist (`spotify:user:{u}:rootlist`)
  and every playlist referenced, including folder structure
  (`spotify:start-group:` / `spotify:end-group:` markers).
- `BackfillMissingMetadataAsync` — fixes INNER-JOIN gaps where
  `spotify_library` rows exist without a matching `entities` row.

**Read operations** (cheap; LINQ over DB):
- `GetLikedSongsAsync`, `GetSavedAlbumsAsync`, `GetPlaylistsAsync`,
  `GetFollowedArtistsAsync`.
- `IsTrackLikedAsync`, `IsAlbumSavedAsync`.

**Write operations** (optimistic local DB → server call → rollback on
failure pattern; see "Save / pin / follow write paths" below):
- `SaveTrackAsync` / `RemoveTrackAsync`
- `SaveAlbumAsync` / `RemoveAlbumAsync`
- `FollowArtistAsync` / `UnfollowArtistAsync`
- `SubscribeShowAsync` / `UnsubscribeShowAsync`
- `PinToSidebarAsync` / `UnpinFromSidebarAsync`

**Outbox & state**:
- `ProcessOutboxAsync()` — dequeue + retry pending writes; called
  fire-and-forget after each write and once at the end of
  `LibrarySyncOrchestrator.RunSyncAsync`.
- `GetSyncStateAsync` — returns `SpotifySyncState` (per-collection
  `CollectionSyncState`).
- `SyncProgress` observable — emits `SyncProgress(displayName, current,
  total, message)` during sync.

`SpotifyLibraryService` also exposes `IObservable<LibraryChangeEvent> LibraryChanged`
(internal-ish; consumed by `LibrarySyncOrchestrator.WireDealerChanges`).

### `ILibraryDataService` — `src/Wavee.UI.WinUI/Data/Contracts/ILibraryDataService.cs`

UI-facing facade. Returns DTOs (not `LibraryItem` / `CachedEntity`). Handles
event coalescing (150 ms window for `DataChanged` / `PlaylistsChanged`) so
the UI doesn't refresh once per save event in a burst.

Key methods:
- `GetStatsAsync` — counts for sidebar badges (`Albums`, `Artists`,
  `LikedSongs`, `Podcasts`).
- `GetAllItemsAsync`, `GetRecentlyPlayedAsync`.
- `GetUserPlaylistsAsync`, `TryGetUserPlaylistsFromCacheAsync` — playlist
  list for sidebar; the latter returns null on cold launch so the sidebar
  shows shimmer instead of an empty section.
- `GetAlbumsAsync`, `GetArtistsAsync`, `GetLikedSongsAsync`,
  `GetPodcastShowsAsync`, `GetYourEpisodesAsync`,
  `GetRecentlyPlayedPodcastEpisodesAsync`.
- `GetPinnedItemsAsync` — filters / sorts / synthesizes pseudo-URI titles
  for the sidebar's Pinned section.
- `PinAsync` / `UnpinAsync` / `IsPinned(uri)` — wraps the core
  `PinToSidebarAsync` / `UnpinFromSidebarAsync`, maintains an in-memory
  pinned-URI HashSet, fires `DataChanged` on success.
- `SetPlaylistFollowedAsync`, `CreatePlaylistAsync`, `CreateFolderAsync`,
  `DeletePlaylistAsync`, `RenamePlaylistAsync`,
  `UpdatePlaylistDescriptionAsync`, `UpdatePlaylistCoverAsync`,
  `SetPlaylistCollaborativeAsync`, member-management calls.
- `GetPlaylistRecommendationsAsync` - Spotify's `playlistextender/extendp`
  endpoint for owner-only Recommended Songs. Send bare track IDs in
  `trackSkipIDs`; the current response uses `recommendedTracks`.
- Podcast detail / comments / reactions / progress writes.
- Local-track playlist overlay ops (`AddLocalTracksToPlaylistAsync` etc.).

Events:
- `PlaylistsChanged` — sidebar-shape change (playlist added / removed /
  renamed / moved). Triggered only by rootlist updates.
- `DataChanged` — any library data change (sync complete, dealer delta, save
  state flip). Coalesced 150 ms.
- `PodcastEpisodeProgressChanged` — scoped event for podcast resume-point
  updates so consumers don't reload the whole library.
- `RequestSyncIfEmpty()` — called by Liked Songs page when it detects an
  empty local DB; ends up as `RequestLibrarySyncMessage` to the orchestrator.

### `IMetadataDatabase` — `src/Wavee/Core/Storage/Abstractions/IMetadataDatabase.cs`

Single SQLite database, schema v21. Singleton; constructed at host startup
in `AppLifecycleHelper`.

Tables (most relevant):
- `entities` — every Spotify entity we've seen (`uri`, `entity_type`,
  `source_type`, `title`, `artist_name`, `album_name`, `duration_ms`,
  `image_url`, `track_count`, `follower_count`, `publisher`,
  `episode_count`, `description`, `release_year`, `genre`, `added_at`,
  `updated_at`, `expires_at`, …). `localized_entities` mirrors this for
  Spotify entities so locale-specific fields don't pollute the canonical
  table.
- `spotify_library` — what's in the user's library, by type. Columns:
  `item_uri`, `item_type` (`SpotifyLibraryItemType` value), `added_at`.
  Used via INNER JOIN to `entities` to get titles + cover URLs.
- `spotify_playlists` — playlists from the rootlist sync. Owns
  `revision`, `folder_path`, `is_from_rootlist`, `is_owned`,
  `is_collaborative`, `image_url`, `track_count`. Separate from
  `entities` (so a pinned editorial playlist that's not followed gets
  an `entities` row but no `spotify_playlists` row).
- `library_outbox` — pending writes that haven't reached the server.
  Operation, URI, item type, retry count, last error.
- `sync_state` — last-sync timestamps + revision tokens per collection
  revision key. Keys take the shape `<set>` or `<set>:<item-type>` when
  the set is mixed (e.g. `collection:Track`, `collection:Album`).

Critical operations agents touch often:
- `AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct)` — optimistic save.
- `RemoveFromSpotifyLibraryAsync(uri, ct)` — by URI only (deletes every row,
  any type). Don't use for type-scoped unpins.
- `RemoveFromSpotifyLibraryAsync(uri, itemType, ct)` — type-scoped delete.
  Use this when one URI can legally exist in multiple sets (e.g. a track
  that's both Liked and Pinned).
- `GetSpotifyLibraryItemsAsync(itemType, limit, offset, ct)` — INNER JOIN
  with `entities`; returns `CachedEntity` rows. Pseudo-URIs survive only if
  a placeholder `entities` row was seated (see
  `FetchMixedTypeMetadataAsync`).
- `UpsertEntityAsync(...)` — write-through entity cache.
- `EnqueueLibraryOpAsync` / `CompleteLibraryOpAsync` / `FailLibraryOpAsync`
  — outbox writes.
- `SetSyncStateAsync(revisionKey, syncToken, itemCount, ct)` — store after
  every successful sync so the next run can go incremental.

### `ITrackLikeService` — `src/Wavee.UI.WinUI/Data/Contexts/TrackLikeService.cs`

Tiny in-memory mirror of saved state for fast UI lookups (heart-button glyph,
"Save / Remove" menu labels). Backed by per-type `HashSet<string>` (Track,
Album, Artist, Show).

- `IsSaved(SavedItemType, uri)` — O(1).
- `GetCount(SavedItemType)`.
- `GetSavedIds(SavedItemType)`.
- `InitializeAsync` — populates from SQLite at app start and after sign-in.
- `ReloadCacheAsync` — called after a full sync to pick up additions.
- `ClearCache` — called on sign-out.
- `SaveStateChanged` event — fires when any cached set mutates. Drives
  `LibraryDataService`'s `DataChanged` coalesce.

The service subscribes to `LibraryChangeManager.Changes` (via the
orchestrator's wiring) so dealer pushes update the in-memory cache
immediately.

### `IPlaylistCacheService` — `src/Wavee/Core/Playlists/PlaylistCacheService.cs`

Caches the rootlist + every individual playlist Wavee has loaded. Lives
between `SpotifyLibraryService` (which writes to `spotify_playlists`) and
the UI's `LibraryDataService.GetUserPlaylistsAsync` (which reads the
rootlist tree).

- Hot slot for the rootlist (in-memory), warm slot in `spotify_playlists`
  (per-playlist rows), cold path via `SpClient.GetPlaylistAsync`.
- Negative cache (~5 min TTL) for 404/403 so we don't hammer dead playlists.
- `Changes` observable emits a `PlaylistChangeEvent` whenever a cached
  playlist mutates. The rootlist URI represents sidebar-shape changes;
  individual playlist URIs represent content changes only.
- `TryApplyMercuryOpsAsync(playlistUri, ops, fromRevision)` — when a dealer
  push includes the full `Op` list AND the cached playlist's revision
  matches `fromRevision`, applies the diff locally with zero `/diff`
  round-trip. Falls back to a full fetch otherwise.

## Sets, item types, and what's in them

| Set name (server) | `SpotifyLibraryItemType` | URI shapes the set carries | Notes |
| --- | --- | --- | --- |
| `collection` | `Track` *and* `Album` | `spotify:track:*`, `spotify:album:*` | Mixed; `SyncCollectionAsync` is called twice with different `itemType` + `uriFilter`. Revision keys are `collection:Track` / `collection:Album`. |
| `artist` | `Artist` | `spotify:artist:*` | |
| `show` | `Show` | `spotify:show:*` | |
| `listenlater` | `ListenLater` | `spotify:episode:*` primarily; also `spotify:show:*` for the auto-added shows | Mixed-type; `FetchMixedTypeMetadataAsync` dispatches per URI kind. |
| `ylpin` | `YlPin` | playlist / album / artist / show entity URIs, **plus** pseudo-URIs `spotify:collection`, `spotify:collection:your-episodes`, `spotify:user:*:collection` | The pseudo-URIs are Spotify's pin-to-Liked-Songs / pin-to-Your-Episodes / legacy Liked Songs pointers. `FetchMixedTypeMetadataAsync` writes placeholder `entities` rows for them so the INNER JOIN read returns them. |
| `enhanced` | `Enhanced` | `spotify:track:*` (only the enhanced overlays for enhanced playlists) | Defined but currently not synced by `LibrarySyncOrchestrator`. |
| `ban` | `Ban` | `spotify:track:*` | Synced; not surfaced in the UI yet. |
| `artist-ban` | `ArtistBan` | `spotify:artist:*` | Synced; not surfaced in the UI yet. |

When extending: every new collection needs (1) an `SpotifyLibraryItemType`
enum value, (2) a `SyncXxxAsync` method that calls `SyncCollectionAsync`,
(3) an entry in `GetSetForItemType` so outbox writes route to the right
endpoint, (4) optionally a row in `LibrarySyncOrchestrator.collections` so
the bulk sync loop runs it.

## Sync lifecycle

Triggers (see `LibrarySyncOrchestrator`):
1. `AuthStatusChangedMessage` (status = `Authenticated`) on sign-in or
   stored-credential auto-login.
2. Cold-start race: orchestrator constructor catch-up if
   `_authState.Status == Authenticated` at construction time (otherwise the
   messenger registration would miss the only emit).
3. `RequestLibrarySyncMessage` from a UI surface that detected an empty
   local DB (e.g. Liked Songs view with zero rows).

`RunSyncAsync` is `SemaphoreSlim`-guarded (non-blocking: skips if already
running). It:
1. Sends `LibrarySyncStartedMessage`.
2. Iterates the inlined `collections` tuple
   (`tracks → albums → artists → shows → listen-later → ylpin`) emitting
   `LibrarySyncProgressMessage` per collection. Per-collection exceptions
   are caught (logged + flagged as partial failure) so one bad collection
   doesn't stop the rest.
3. Calls `_libraryService.BackfillMissingMetadataAsync()` to fill any
   `spotify_library` rows whose `entities` row went missing (rare, but the
   INNER-JOIN reads would silently drop them).
4. Reloads `_likeService` caches so heart buttons reflect the new state.
5. Drains `ProcessOutboxAsync` (writes queued while offline).
6. Sends `LibrarySyncCompletedMessage` with a delta summary, or
   `LibrarySyncFailedMessage` with a message string on hard failure.

Each `SyncXxxAsync` ultimately calls `SyncCollectionAsync(set, itemType,
displayName, uriFilter, ct)`, which:
- Reads the stored revision token for `<set>` (or `<set>:<itemType>` for
  mixed-set sub-syncs).
- Attempts incremental sync via `GetCollectionDeltaAsync(set, lastRevision)`.
  If the delta returns successfully:
  - For each item: if `IsRemoved`, `RemoveFromSpotifyLibraryAsync`; else
    `AddToSpotifyLibraryAsync`.
  - For *additions*: fetches metadata via `FetchAndStoreMetadataAsync` (for
    homogeneous sets) or `FetchMixedTypeMetadataAsync` (for ylpin /
    listenlater).
  - Stores the new sync token via `SetSyncStateAsync`.
- Falls back to `FullSyncAsync` if the delta fails (typically because the
  stored revision is too old).

`FullSyncAsync`:
1. Paginates `GetCollectionPageAsync` until exhausted.
2. Fetches metadata for *every* item (homogeneous bulk via extended
   metadata; mixed-type via per-kind dispatch).
3. `ClearSpotifyLibraryAsync(itemType)` to remove stale entries.
4. Re-inserts every URI with its `added_at`.
5. Stores the new sync token.

`FetchMixedTypeMetadataAsync` (`SpotifyLibraryService.cs:1349` area) is the
choke point for ylpin / listenlater. It groups URIs by prefix, calls
`FetchAndStoreMetadataAsync` per kind for tracks / albums / artists / shows
/ episodes, calls `ResolvePinnedPlaylistEntityAsync` per playlist URI
(which actually fetches name + cover via `SpClient.GetPlaylistAsync` —
playlists have no `ExtendedMetadata` bulk endpoint), and writes a placeholder
`entities` row for pseudo-URIs (`spotify:collection*`).
`BackfillPinnedPlaylistMetadataAsync` runs after `SyncCollectionAsync` for
ylpin to retroactively resolve any pinned playlist whose entity row still
has `title = uri` (legacy placeholder from before per-playlist resolution
existed, or from a metadata fetch that failed transiently).

## Dealer-driven liveness

Spotify dealer (Mercury) pushes notify Wavee that something in the library
changed. Path:

1. `DealerClient.Messages` emits `DealerMessage` for every URI matching
   `hm://collection/*`, `hm://playlist/*`, `*collection-update*`.
2. `LibraryChangeManager.OnLibraryMessage` (`src/Wavee/Connect/LibraryChangeManager.cs:59`)
   dispatches by URI shape:
   - `/rootlist` → `TryParseRootlistModInfo` → `LibraryChangeEvent`
     with `Set = "playlists"`, `IsRootlist = true`.
   - `/list/liked-songs-artist/` → `BuildLikedSongsArtistEvent`.
   - `/playlist/v2/playlist/<id>` → `TryParsePlaylistModInfo` (with the
     `Op` list + `FromRevision` for diff application).
   - `/collection/collection/` (Tracks/Albums PubSub) →
     `TryParsePubSubUpdate` (carries `Items`).
   - Anything else → basic event with empty `Items`, `Set` resolved by
     `DetermineSetFromUri` (path-segment walk: `ylpin`, `listenlater`,
     `artist`, `show`, `collection`, …).
3. `_changes` subject emits the event.
4. `SpotifyLibraryService.OnLibraryChange` (`SpotifyLibraryService.cs:1206`)
   forwards on `_libraryChanged` (so the orchestrator hears it) AND
   processes `Items` in the background: `AddToSpotifyLibraryAsync` /
   `RemoveFromSpotifyLibraryAsync` per item. Playlist pushes branch to
   `OnPlaylistChangedAsync` which re-fetches that one playlist.
5. `LibrarySyncOrchestrator.WireDealerChanges` subscribes to
   `concrete.LibraryChanged`:
   - Always sends `LibraryDataChangedMessage` so any UI listening for
     `DataChanged` refreshes.
   - For `Set == "playlists"`, also sends `PlaylistsChangedMessage`.
   - For `Set == "ylpin"` (which arrives with `Items = []` because the
     parser can't decode the payload), kicks off a background
     `SyncYlPinsAsync()` and re-broadcasts `LibraryDataChangedMessage`
     once the sync completes — this is what makes the sidebar's Pinned
     section update in real time when the user pins/unpins from Spotify
     mobile.
6. `LibraryDataService` registers for `LibraryDataChangedMessage` and
   `PlaylistsChangedMessage` and coalesces them into `DataChanged` /
   `PlaylistsChanged` events with a 150 ms window.
7. `ShellViewModel.OnLibraryDataChanged` / `OnPlaylistsChanged` refreshes
   badges, the Pinned section, and the playlists subtree.

**Why ylpin has its own branch:** the dealer payload for
`hm://collection/ylpin/<user>` doesn't carry the items; only an opaque
notification. Re-running the incremental sync (sync-token-aware, so
typically a few-hundred-byte delta call) is the cheapest way to pick up
the change. Other sets either carry items in the payload (Tracks/Albums via
PubSub) or have parsers (Playlists), so they don't need this fallback —
yet. The same pattern applies if you discover another set whose payload
isn't parsed.

## Save / pin / follow / subscribe write paths

Every write follows an **optimistic local-DB → server call → rollback on
failure** pattern. The exact entry point per kind:

| Action | Entry point | Pattern |
| --- | --- | --- |
| Save a track / unsave | `SpotifyLibraryService.SaveTrackAsync` / `RemoveTrackAsync` → `SaveItemAsync` / `RemoveItemAsync` | Local DB write → enqueue outbox → fire-and-forget `ProcessOutboxAsync`. No rollback; the outbox retries up to 10×. |
| Save / unsave an album | `SaveAlbumAsync` / `RemoveAlbumAsync` | Same as track. |
| Follow / unfollow an artist | `FollowArtistAsync` / `UnfollowArtistAsync` | Same. |
| Subscribe / unsubscribe a show | `SubscribeShowAsync` / `UnsubscribeShowAsync` | Same. |
| Pin / unpin from sidebar | `PinToSidebarAsync` / `UnpinFromSidebarAsync` | **Synchronous** server call (not outbox). On failure: rollback the local write + return false. UI surfaces a toast (`ShellViewModel.NotifyPinFailure`). |

Tracks / albums / artists / shows use the outbox so offline saves persist
across restarts. Pin/unpin uses synchronous + rollback because the UX is
"see it disappear" (small N, immediate feedback expected, no offline pin
ergonomics needed yet).

Metadata is resolved synchronously on save (`FetchAndStoreMetadataAsync` per
item type) so the INNER-JOIN reads return the saved row with a real title
on the next refresh. For pins, `ResolvePinnedEntityMetadataAsync` is the
choke point that fans out per URI kind, including writing placeholder
entity rows for pseudo-URIs.

## Messages (messenger-bus inventory)

All messages live in `src/Wavee.UI.WinUI/Data/Messages/AppMessages.cs`.

| Message | Sender | Receiver | Purpose |
| --- | --- | --- | --- |
| `AuthStatusChangedMessage` | `IAuthState` reactor | `LibrarySyncOrchestrator`, `ShellViewModel`, others | Auth state changed; trigger sync on `Authenticated`, clear caches on `LoggedOut`/`SessionExpired`. |
| `RequestLibrarySyncMessage` | UI surfaces (e.g. Liked Songs page) | `LibrarySyncOrchestrator` | Manual "sync now" request when local DB is empty. |
| `LibrarySyncStartedMessage` | `LibrarySyncOrchestrator` | `ShellViewModel` (clears badges + shows sign-in dialog progress) | Full sync starting. |
| `LibrarySyncProgressMessage` | `LibrarySyncOrchestrator` | `ShellViewModel` / sign-in dialog | Per-collection progress (`name`, `done`, `total`). |
| `LibrarySyncCompletedMessage` | `LibrarySyncOrchestrator` | `ShellViewModel` (kicks off `LoadLibraryDataAsync`) | Sync finished; carries `LibrarySyncSummary` (tracks / albums / artists added & removed). |
| `LibrarySyncFailedMessage` | `LibrarySyncOrchestrator` | `ShellViewModel` (shows notification) | Hard failure during sync. |
| `LibraryDataChangedMessage` | `LibrarySyncOrchestrator` (dealer hook + post-ylpin-sync), other UI write sites | `LibraryDataService` (coalesces into `DataChanged`) | Any library data changed. |
| `PlaylistsChangedMessage` | `LibrarySyncOrchestrator` (rootlist dealer push), playlist-cache subscription | `LibraryDataService` | Playlists tree (sidebar shape) changed. |
| `PlaylistPrefetchStartedMessage` / `PlaylistPrefetchProgressMessage` | `PlaylistPrefetchService` | Diagnostics / dev surfaces | Optional prefetch progress. |

## DTOs

UI-facing DTOs (in `src/Wavee.UI.WinUI/Data/DTOs/`):

| DTO | Used by | Source |
| --- | --- | --- |
| `LibraryItemDto` | Generic library item (legacy `GetAllItemsAsync`). | `LibraryDataService.GetAllItemsAsync`. |
| `LikedSongDto` | Liked Songs table rows. Implements `ITrackItem`. | `LikedSongsViewModel.FilteredSongs`. |
| `LibraryAlbumDto` | Saved albums grid (Library/Albums). | `LibraryDataService.GetAlbumsAsync`. |
| `LibraryArtistDto` | Followed artists grid (Library/Artists). | `LibraryDataService.GetArtistsAsync`. |
| `LibraryPodcastShowDto` | Followed shows row. | `LibraryDataService.GetPodcastShowsAsync`. |
| `LibraryEpisodeDto` | Your Episodes table rows. Implements `ITrackItem`. | `LibraryDataService.GetYourEpisodesAsync` and `GetRecentlyPlayedPodcastEpisodesAsync`. |
| `PinnedItemDto` | Sidebar Pinned section. | `LibraryDataService.GetPinnedItemsAsync`. |
| `PlaylistSummaryDto` | Sidebar playlists + cards. | `LibraryDataService.GetUserPlaylistsAsync`. |
| `LikedSongsFilterDto` | Pill filters at the top of Liked Songs. | `LibraryDataService.GetLikedSongFiltersAsync`. |

Page-detail DTOs are owned by their page VMs, not the library service —
e.g. `PlaylistDetailDto`, `AlbumDetailResult`, `ArtistOverviewResult`,
`PodcastEpisodeDetailDto`. The library service stops at the list-row DTO
boundary.

## Change Guidance

When changing **sync behaviour**:
- Don't add a new `SyncXxxAsync` to `ISpotifyLibraryService` unless you also
  add it to `LibrarySyncOrchestrator.collections` (otherwise the UI process
  will never call it — only `Wavee.Console` calls `SyncAllAsync`).
- Sync tokens are per revision key. Mixed sets like `collection` need a
  sub-key (`collection:Track` vs `collection:Album`); inherit the existing
  pattern.
- If `GetSpotifyLibraryItemsAsync` is returning fewer rows than
  `spotify_library` contains, the INNER JOIN to `entities` is the suspect.
  Run `BackfillMissingMetadataAsync` or confirm
  `FetchMixedTypeMetadataAsync` is writing a placeholder for the URI kind.

When changing **dealer behaviour**:
- Adding parser support for a new dealer URI shape: extend the switch in
  `LibraryChangeManager.OnLibraryMessage` and add a `TryParseXxx` helper
  that returns a `LibraryChangeEvent` with populated `Items`.
- Adding real-time refresh for a new set (similar to ylpin): extend the
  branch in `LibrarySyncOrchestrator.WireDealerChanges`. If the payload
  isn't parseable, follow the ylpin pattern (re-run the incremental sync
  for that set).
- `DetermineSetFromUri` uses path-segment parsing; the substring fallbacks
  below the segment branch are legacy and shouldn't be reached for known
  sets.

When changing **save / pin / follow / subscribe**:
- Reuse `SaveItemAsync` / `RemoveItemAsync` for outbox-based writes — they
  already do optimistic DB + metadata fetch + outbox enqueue.
- Pin/Unpin uses the dedicated synchronous-with-rollback path because
  Spotify desktop's Files-style UI expects immediate feedback or an error
  toast. Don't put pin/unpin in the outbox.
- A new pinnable kind needs: an entry in `ResolvePinnedEntityMetadataAsync`
  for metadata, a `PinnedItemKind` enum value, a glyph case in
  `ShellViewModel.CreatePinnedIconSource`, and a navigation case in
  `ShellPage.SidebarControl_ItemInvoked`. The existing canonical /
  pseudo-URI dispatch is in `LibraryDataService.GetPinnedItemsAsync`.

When changing **UI consumers**:
- Library page rows go through the **track-and-episode-ui** guide (rows are
  `TrackItem`-based). Touch this guide for the *data source* (where the DTO
  comes from); touch the track guide for the *rendering*.
- Sidebar shape changes (playlists added / removed / renamed) flow through
  `PlaylistsChanged`. Sidebar badge / pinned changes flow through
  `DataChanged`. They're separate so the heavy diff doesn't run on every
  save-state flip.
- The heart button on a track row drives `SaveTrackAsync` /
  `RemoveTrackAsync` and reads `ITrackLikeService.IsSaved(Track, uri)` for
  the glyph. Don't subscribe to `DataChanged` for heart state — use the
  service's `SaveStateChanged` event directly to avoid the 150 ms coalesce.

## Keeping This Guide Current

If you add, remove, or rename anything in the library lifecycle:
1. Update the relevant section above (and the **Quick-find table**).
2. Re-run the re-verification commands at the top.
3. Update `last_verified` in the frontmatter.
4. If a set / collection / write path stops being part of the library
   subsystem entirely, remove its row from the table and update the
   **Sets** table — don't leave stale entries.
