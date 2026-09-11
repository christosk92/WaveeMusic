# Wavee `feat/library-v3-1` — consolidated findings and remediation plan

Date: 2026-09-06. Branch: `feat/library-v3-1` (the uncommitted hydration-facade refactor, ~350 files, ~9k+/41k- lines).
Status of the tree at the time of writing: Debug + Release build clean; `Wavee.Tests` 7,332 passed / 0 failed / 1 skipped.
Nothing is committed. Plan mode: this document is findings + plan only; no code is written here.

On execution this file is copied verbatim to `docs/plans/wavee/library-v3-1-findings-2026-09-06.md`.

---

## Context

The user rebuilt the branch after the first remediation round (migration removed, playback epoch fixed, fetch
throughput raised, page mapping moved off the UI thread) and reported it is still unshippable:

- the app **crashes ~6 s after launch** (`UseRequiredContext<Services>` with no provider in scope), reproducibly;
- the **sidebar shows raw Spotify ids** as row titles (dimmed) although the log shows those identities fetched;
- the **restored album page shows "Something went wrong. Check your connection"** while the header says
  "Connecting…", and later shows 22 shimmer rows, "Length —", and empty artist avatars with a bare ",";
- an **artist page's Top tracks render with empty titles** and "—" durations although the log shows the 12 track
  identities fetched (`ready=24`);
- opening one artist page produced **36 sequential single-key extended-metadata batches** for the same subject;
- the **artist hero clips its action row** when the bio wraps to two lines (ARASHI);
- page navigation feels laggy; the user wants the structural cause fixed, not measured (see memory
  `structural-fixes-over-measurement`).

Standard: "it has to be perfect". The user asked for one document with ALL findings including the original review.

---

## Part 1 — What was changed today (already in the tree, verified building + green)

| Lane | Change | Files |
|---|---|---|
| A | **v11 cache migration deleted; schema reset instead.** `CurrentSchemaVersion = 12`; a `library.db` at any other version has every catalog/replica table (and `outbox`/`dead_letter`) dropped and recreated empty; `video_override`, `recent_surfaces`, `activity_log`, `cache_budget_bytes` survive; newer version throws; `ResetFromSchema` reports what was replaced. ~1,100 lines of migration + recovery tables + `InitializeCatalogAsync` gates removed. | `Backend/Persistence/SqliteColdStore.cs`, `SqliteReplicaPersistence.cs`, `SqliteCatalogPersistence.cs`, `SqliteCatalogSearch.cs`, `CatalogCacheMaintenance.cs`, `PayloadCodec.cs`; deleted `SqliteCatalogMigration*.cs`, `SqliteReplicaMigration.cs`, `CatalogMigrationModels.cs`, `ArtistOverview.cs`; tests `SqliteSchemaResetTests.cs` (new), `CatalogTestDb.cs` (new) |
| B | Migration dialog/gate/strings/docs removed; reset logged at open (`library.db schema v11 → v12: …`). | `WaveeApp.cs`, `App/Services.cs`, `assets/loc/en-US.json`, `docs/plans/wavee/catalog-state-replacement-implementation.md`, `CHANGELOG.md`, `.claude/skills/wavee/catalog-state.md`; deleted `Features/Shell/CatalogMigrationDialog.cs`, `CatalogMigrationPresentation.cs` |
| C | **Track clicks silently doing nothing — fixed.** `NowPlayingProjection` captured the catalog epoch at construction; the live session installs afterwards and bumps it, so every resolved-track seed threw "belongs to a previous catalog session" and the intent was retired (the lone `Adopt` in the log). Now reads `_catalog.Epoch` at seed time; `ExecutePlayAsync` logs any failed play intent. | `Backend/PlaybackProjection.cs`, `Backend/PlaybackController.cs`, `Wavee.Tests/PlaybackCatalogEpochTests.cs` |
| D | **Fetch throughput.** `WorkerCount 2 → 4`; `BatchGroup` per facet (`extended-metadata`, `album-envelope`, `envelope:<Facet>`) instead of per subject; album documents + envelopes fetched with parallelism 4 inside a batch; transport-cache reads parallel; always-on `catalog.fetch.batch` log line per batch. | `Backend/Catalog/ResourceCoordinator.cs`, `SpotifyLive/Catalog/SpotifyCatalogResourceProvider.cs`, `SpotifyCatalogVideo.cs`; tests `ResourceCoordinatorTests.cs`, `QueryDemandLifetimeTests.cs` |
| E | **Page model mapping off the UI thread.** New `QuerySignalBinding<T, TModel>`: every projection (subscribe replay, reactivation, background publish) runs on the thread pool, latest-wins with superseded snapshots skipped unprojected; status-only republications reuse the previous model by reference equality. `DetailPage` and `QueryHooks.UseMapped` use it. | `App/Queries/QuerySignalBinding.cs`, `Features/Detail/DetailPage.cs`, `App/Queries/QueryHooks.cs`; tests `QuerySignalBindingTests.cs` |
| — | Dead code sweep (603 names across persistence/catalog/queries/sync): removed `KindForLogicalSet`, `IsAttributeLess`, `TryMarkAttrHealForced` + `_attrHealForced` (`LibrarySync.cs`), `IMutationOutbox` (`Mutation.cs`); stale `EntityJson` comments fixed. | |
| — | Engine design written (not started): `..\fluent-gpu\docs\plans\render-thread-animation-design.md`. | |

The user's 18:23 launch (`Wavee.exe` 18:20:59) DOES contain lanes A–E: the log shows the reset line (18:18) and
`catalog.fetch.batch` lines. So the remaining symptoms are real, not a stale binary.

---

## Part 2 — Original code review (`/code-review medium`, hydration + playback), all findings

Eight CONFIRMED findings (ranked), then the ones cut by the cap, then plausible-only. None is fixed yet.

### 2.1 Confirmed

1. **`FluentMediaAudioHost.Transport.cs:150` — seek during a deferred load plays the previous track.** `ApplySeekAsync` binds whatever `_session`/`_activeBytes` are installed; `LoadFastStartAsync`'s empty-head branch (`FluentMediaAudioHost.cs:791-805`) leaves the previous track's session in place while the new body is pending, so a seek reopens and audibly plays track A at the seek target while the UI shows track B loading.
2. **`Backend/Mutation.cs:272` — transport failure on a queued write is never resent.** Any non-`PlaylistMutationException` (or status 0/5xx) is classified as an ambiguous Verify; `LibrarySync.VerifyIntentAsync` (`LibrarySync.cs:374-394`) counts each failed offline verification as an attempt and flips the intent to `NeedsAttention` on the 3rd (~7 s offline), after which `CanReplay` (`LibraryReplicaCoordinator.cs:183-184`) is false forever. HEAD kept the op Pending with backoff up to 10 attempts. An offline like is silently lost while the replica shows it saved.
3. **`Backend/Sync/LibrarySync.cs:928` — one unreachable playlist aborts the whole outbox drain.** The pre-drain baseline-recovery loop awaits `FullRootlistFetchAsync`/`FetchPlaylistSnapshotAsync` with no try/catch; `PlaylistFetcher.GetAsync` (`PlaylistFetcher.cs:158`) throws on non-200; the drain throws before `_mutations.Drain` (936) and before `ScheduleDrainReenqueue` (949); `_drainReenqueueScheduled` was cleared at 927 and never re-armed. Every pending like/follow/edit is stuck until that playlist reappears; `ReconnectResyncAsync` also throws out of step 1.
4. **`FluentMediaAudioHost.Transport.cs:139` — a seek before the body attaches raises an error toast and stops playback.** `ApplySeekAsync` throws when no `PcmAudioSession` is loaded / source non-seekable / no `ReopenBody`; `Submit`'s catch-all (97) turns it into `AudioHostSignal.Fault`; `PlaybackController.OnHostSignal` (3666) → `ReportPlaybackError` (691-708) stops, phase Failed, toast. Trigger: Previous at >3 s on a cluster-recovered paused session, or any seek (seek bar, lyrics, SMTC) in the first seconds of a track. The old host's `Seek()` silently returned on a null session.
5. **`FluentMediaAudioHost.cs:1149` — audio device change can stop playback.** `SoftReloadAsync` has no catch and no `ReopenBody`/`CanSeek` guard; `ReopenSourceAsync` throws for a non-seekable module stream or during the fast-start window; the mailbox hands it to `onError` → Fault → controller stops with a toast. HEAD treated these as benign no-ops. Even without the Fault, `_clockStale` stays true and the old session is never faded back in (silence).
6. **`PlaybackController.cs:2835` — a parked seek + a pause during resolve makes the eventual Load a no-op.** `loadCommand = _pendingSeek?.Id ?? NextTransportCommand()` reuses an OLDER sequence; `PauseAsync` (1017-1030) mints two newer ones; `FluentMediaAudioHost.Load` (643-647) sees `IsOlder` and returns silently; no media opens, no signal, no watchdog; the UI shows the track paused at the seek target over an empty host.
7. **`Backend/Persistence/SqliteCatalogMaintenance.cs:57` — rootlist playlist members are never pinned.** `BuildCatalogPins` lost HEAD's `playlist_items` leg; playlists never appear as `catalog_relation_item` parents so the single relation hop (capped at a GLOBAL `LIMIT 5000` across all roots, 68-70) reaches none of their tracks; only pinned rows get `last_access` refreshed (72-73); member `TrackIdentity` rows age into the 30-day TTL / LRU sweep and take their `catalog_search` row with them (150). Offline, followed playlists render empty rows and library search stops finding them. (Less urgent today: the reset made everything fresh, but it is a 30-day time bomb.)
8. **`Backend/Persistence/SqliteCatalogPersistence.cs:109` — one corrupt catalog row poisons every read touching its key.** `ReadCatalog` throws on `payload_version` mismatch / undecodable payload instead of treating the row as a miss; `CatalogRepository.LoadCoreAsync` (442-445) neither catches nor marks Loaded, so every `ReadManyAsync`/`AcceptAsync` batch containing that key fails and the fresh value is never persisted; nothing purges by `payload_version`. HEAD's `CachedStore.ColdFallback` treated a corrupt row as a re-fetchable miss.

### 2.2 Confirmed but cut by the 8-finding cap

9. **`PlaybackProjection.cs:869`** — a remote Connect seek never leaves `Accepted` when the device clamps/ignores it; the seek bar freezes at the target.
10. **`SpotifyCatalogEnvelopes.cs:149`** — `ArtistOverview` seeds `ArtistPopular` under `Arguments=default` while `RelationProjection` reads `Arguments(0,50)`: orphan rows; top tracks empty offline. (Likely related to Part 4 Problem B.)
11. **`ResourceCoordinator.cs:63`** — `EnsureAsync` holds the global `_admission` permit across four `DataCommitQueue` round trips (serializes every caller behind persistence).
12. `IMutationOutbox` dead interface — **removed today.**

### 2.3 Plausible only

13. Post-retry Transport errors are sticky with no reconnect/Retry escape hatch (`ResourceCoordinator.cs:97`). (Likely relevant to Part 4 symptom 2.)
14. A stale `catalog_migration_pending` cursor faults outside the recovery catch — **moot, migration deleted.**
15. The 16 MiB `DataCommitQueue` byte cap is a latent cliff for large native adopts.
16. The Ended hold is now bounded only by network timeouts (~90 s) instead of ~1 s.

---

## Part 3 — Diagnoses from the first round (mechanisms, with evidence)

- **Migration**: converted only the cache; 69 s on a real install per its own plan note; deleted (Part 1 A/B).
- **Track click did nothing**: log pattern `play intent … route=local` → `command … action=Adopt` and never `play resolved`; cause = constructor-captured catalog epoch (Part 1 C). Fixed.
- **First open of album/sidebar shows blanks/raw ids for seconds**: 2 workers × per-subject batch groups → 33 playlist headers = 33 sequential-ish round trips ahead of the album's identity wave (Part 1 D). Improved; but see Part 4 — a deeper cause remains.
- **Navigation lag**: verified UI-thread work per navigation: `Queries.Acquire` runs the first projection synchronously under the catalog publication lock; each coalesced UI post re-mapped the whole page model (`MapPlaylist`/`MapAlbum`, every track) and re-rendered the page; the cold skeleton renders the real shell twice. Lane E removes the mapping from the UI thread. The engine ticks animations at phase 7 of the UI-thread frame, so any remaining UI-thread stall (reconcile of a 300-row page, lock waits) freezes motion — engine design in Part 6.
- **Artist hero clipped (ARASHI)**: `ArtistHeroLayout.cs` — hero height is a constant per tier (`WideHeight = 392`, budget = verified 16 + 2-line name 80 + ONE bio line 20 + meta 20 + actions 36 + gaps + padding), then clamped to 45% of the viewport; `ArtistPage.Hero.cs:72` allows `MaxLines = 2` for the bio on the Wide tier while the budget reserves one line; the container is `ClipToBounds = true` (line ~199), so the overflow clips the actions row. Fix (Part 5): size the hero from the measured copy block with the tier height as the floor, or reserve two bio lines in `WideCopyBudget` and let the clamp reduce photo air first.

---

## Part 4 — New investigation (2026-09-06 18:23 build): pending results

Three read-only investigators (Opus; the Fable ones hit a session limit) are running. Their findings are inserted
below when they report. Placeholders describe exactly what each is establishing.

### 4.1 Fatal `UseRequiredContext<Services>` ~6 s after launch

Evidence: crash reports 18:23:54 and 17:38:49 (the 17:38 one predates today's edits, so this is a branch bug, not a
regression from today). Only three call sites use the required variant: `Features/Detail/AlbumRelatedSections.cs:25,62`,
`Features/Detail/PlaylistPicker.cs:53`; everything else uses `UseContext(Services.Slot)` with a null check.
Timing (~5–6 s, after the sidebar sync) points at the restored album route's related sections mounting inside a
subtree that is rendered outside the root provider chain (skeleton derivation / overlay / KeepAlive park).

**Result: CONFIRMED — an engine bug in KeepAlive parking, first exposed by this refactor.**

Symbolization was impossible: the published `Wavee.exe` (18:20) has no CodeView debug directory entry at all
(`llvm-readobj` shows only a POGO entry), and the only `Wavee.pdb` (12:29) is from a different link. The release
script's symbols zip path is what produces a usable PDB; the plain publish does not. Everything below is by code
reading; the RVA pattern (`0x1e38fc` three times with adjacent `0x1e407c`/`0x1e3efc`) is consistent with the
reconciler's `Mount → MountComponent → RunComponent → ReconcileSingleChild → Mount` recursion on `tid=1`.

Failing component: `AlbumMoreByList` (`Features/Detail/AlbumRelatedSections.cs:25`) — the only required-context
consumer that mounts without a click (the other two, `AlbumVersionsMenu` and `PlaylistPickerPanel`, need a click).
Mount path: `WaveeApp` root providers → `WaveeShell` → `ContentHost` `Flow.KeepAlive` (8 page slots,
`Features/Shell/ContentHost.cs:82`) → `DetailPage` → `DetailShell` → `TrackList` → `AlbumTrailing`
(`DetailTracks.cs:1867`) → the hand-built `SkelRegionEl` in `DetailTrailing.cs:81-105` → `TrailingSections`
(`DetailTrailing.cs:126-127`) → `Embed.Comp(new AlbumMoreByList.Props(…))`.

Sequence in the log: the album page's trailing loads run (`album-envelope`, two `envelope:ArtistPopular` +
`envelope:ArtistOverview` batches), a *different* page is already showing (`playlist.open.state … 37i9dQZF1EP6YuccBxUcC1`
at seq 90), and the fatal lands on `tid=1` the instant the last enrichment batch settles — i.e. the skeleton region
swaps to real content **while the album page is KeepAlive-parked**.

Mechanism (engine, `C:\wavee\fluent-gpu`): `NodeFlags.Parked` is inherited only by component nodes and only from
their immediate parent (`Reconciler.cs:931,939`); `SceneStore.CreateNode` resets every fresh node's flags
(`SceneStore.cs:331`) and neither `Mount` (`Reconciler.cs:621`) nor `AppendChild` re-marks it. A parked page is
detached from the scene (`Reconciler.cs:1513-1514`) yet still `IsLive`, so `ReconcileSkeletonRegion`
(`Reconciler.cs:1157`, no parked gate) fires while parked; `ReplaceSingleChild` (`:908`) mounts `TrailingSections`'
wrapper `BoxEl` as a new, unmarked node; `AlbumMoreByList` one level below computes `parked = false`, skips the
`RunComponent` defer (`:981`), renders its first frame inside a subtree with no path to the scene root;
`ResolveContext` (`:611-617`) walks `_scene.Parent` up to the detached page root, finds no provider, and
`UseRequiredContext` throws (`RenderContext.cs:746-756`). The detached-fallback cache (`RenderContext.cs:750`) is
empty for a freshly mounted component. Today any plain `UseContext` in that position silently returns the default
value instead — a silent variant of the same bug.

Provenance: `AlbumRelatedSections.cs` is untracked (new in this refactor); `DetailTrailing.cs:126-127` replaced a
pure-element `AlbumList(...)` with the context-reading component; `PlaylistPicker.cs:53` swapped
`UseContext(LibraryStore.Slot)` for `UseRequiredContext(Services.Slot)`. The engine latency existed all along; the
refactor put the first context-requiring component under a skeleton boundary inside a parkable page.

Fix (Part 5 item 1): **engine** — in `Reconciler.Mount` (`Reconciler.cs:621`), before the element-kind dispatch,
inherit `Parked` from the parent for every node kind, so `MountComponent` sees `parked = true`, defers the render,
and the documented un-park replay (`Reconciler.cs:1593-1595`, "now attached, so context resolves") runs it. This
also covers `Flow.Show`/`Flow.For`/virtual-row realization under a parked page (same `Mount` path). App-side
stopgap alone (tolerant `UseContext` + `if (svc is null) return`) is NOT sufficient: the failed resolve subscribes to
nothing and no replay is queued, so the section stays permanently empty unless paired with
`UseActivation(onActivated: RequestRerender)`. Do the engine fix; keep the app sites tolerant as defence in depth.

Not determined: no symbolized stack (artifacts cannot produce one); the "album page was parked at that instant" is
inferred from the log, not proven (a Diag park trace or a repro — open an album, navigate away within ~2 s — settles it).

### 4.2 Raw ids in the sidebar; "No saved copy is available offline" on the restored album

Evidence: `W [ui] Surface error shown: No saved copy is available offline.` at 0.2 s AND again at 0.7 s (after
`session-installed`); the album's id stays raw in the sidebar minutes later although `catalog.fetch.batch` shows its
identity `ready`. Hypothesis under test: queries acquired before the live session installs are keyed by the pre-login
`CatalogScope` (`ContextKnown=false`) and never re-acquire under the installed scope; the repository's
`SetSessionCore` flips other-scope entries to `Offline`, `ActiveScope`/`RequestAllowed` refuse them, and the
`QueryPresentationRules.InitialFailure` "offline" verdict sticks.

**Result: CONFIRMED, with a sharper cause than the hypothesis — the UI's scope handover is a single, late,
silently-skippable `postUi` callback with no retry, no inverse and no log line.**

Cause 1 (persistent state). `LiveSessionHost.cs:600-607` is the ONLY writer of `CatalogScopeSignal` in the app:

```
postUi(() => {
    if (svc.Data.Catalog.Epoch != installedDataEpoch || svc.Data.Catalog.Scope != catalogScope) return;
    svc.CatalogScopeSignal.Value = catalogScope;
    svc.Playback.AttachQueueQueries(...); svc.LibraryStore.RebindScope(...); svc.SidebarBinder.RebindScope(...);
});
```

The repository session is installed 70 lines earlier (`LiveSessionHost.cs:530` → `CatalogRuntime.SetSessionAsync` →
`CatalogRepository.SetSessionCore`, `:155-180`). Between them: `RefreshAsync`, `SetProtocolSessionAsync` (throws at
`:560` if false), `LibrarySync`, the dealer router, ~10 `wiring.Set` calls. Three independent ways the handover
never lands: (1) any throw between `:530` and `:600` — rollback via `EndCatalogSessionAsync` → `SetOfflineCore` does
NOT restore the pre-login scope; (2) the epoch guard silently no-ops if anything bumped `Catalog.Epoch` between
enqueue and UI drain (a reconnect, a racing sibling go-live, an end) — and the user launched five processes in
20 s sharing one `library.db`; (3) no inverse — not registered through `wiring.Set`, invisible to
`LiveSeams.AssertCovers`, never reset on logout/reconnect.

Once diverged it is total: `ResourceKey` carries `Scope`; `SetSessionCore` (`:172`) pins every other-scope entry to
`ResourceActivity.Offline`; `ActiveScope` (`:514`) / `RequestAllowed` (`:520`) deny its requests; the node reports
`Status.IsOffline` (`QueryService.cs:311`) forever; `RecomputeCatalog` on every `Session` publication (`:111`) re-reads
the same dead keys. The only escape is a UI re-acquire. Meanwhile several fetch paths take the live scope straight
from the closure — `LiveSessionHost.cs:536` `ScopeForSubject`, `SpotifyAlbumEnrichmentService.cs:143,153`,
`LibrarySync` seeds, `NativeCatalogBootstrap` — which is exactly why the log shows `ready=25` for artists the
sidebar renders as raw ids: fetched under the session scope, read under the pre-login scope.

Cause 2 (the launch flash, on the happy path too). `SetSessionCore` publishes `CatalogChangeKind.Session`
synchronously at `:530`; the UI re-acquire is queued at `:600`. In between `QueryService.OnCatalogChange`
(`:106-113`) recomputes every node, the still-attached pre-login node sees all keys `Offline`, publishes
`IsOffline`, `InitialFailure` (`QuerySignalBinding.cs:429`) turns it into "No saved copy is available offline.",
`DetailPage.cs:77-79` calls `SetFailed`. Exact trace: the second error lands 63–68 ms after `session-installed` in
all three sessions. The first error (~0.2 s) is the honest pre-login state (`Services.cs:642` builds the runtime with
an empty scope, `_online == false`) — dishonest only in presentation: a hard "Something went wrong" while the header
says "Connecting…".

Symptom → cause. Symptom 1 (raw-id sidebar rows) = Cause 1: `SidebarProjectionBinder.RebindScope` (`:182-196`) is the
only thing moving `SidebarLibraryQuery` + pin `EntityCardQuery` nodes to the session scope; without it
`LibraryDefinition.Read` (`CatalogQueryDefinitions.cs:513-526`) → `View.Album/Artist/Playlist/Show` return
`identity?.Name ?? ""` under the dead scope; `SidebarPaneSlot.cs:327-332` renders `ShortUri` dimmed; replicas are
keyed by `ReplicaScope` (`CatalogRuntime.cs:41-43`) so rows exist with the right uris/kinds and no names
("Playlist · 0 songs" = null header). Named pins (Savage Garden, Michael Jackson, Phil Collins, rosie) show because
`ResolveUnlistedPin` (`SidebarBinderPipeline.cs:214-231`) starts from the persisted `FallbackTitle` written by an
earlier `TouchPin`; pins newly synced from `ylpin` have none. Symptom 2 (album error, empty artists, shimmer rows)
= Cause 2 for the flash, Cause 1 for persistence: a header can appear because `AlbumIdentity` was seeded under
the session scope by a non-UI path, while `ArtistRef` (`CatalogReadView.cs:47`) and `AlbumTracks` are read under
the dead scope and `CanRequest` (`:123`) refuses their requests.

Refuted: a stale-scope request poisoning a key with sticky `Forbidden` (`AcceptAsync` `:287-288` drops it as
`Superseded`, never written); queries acquired with the new scope before the repository accepts it (repository is
always ahead); non-reactive acquire sites (every site IS reactive — `DetailPage.cs:63,140`, `QueryHooks.cs:62` via
record-typed `QuerySpec`, `SidebarPane.cs:470-472`, `RecentsPage.cs:239,259`, `SidebarArtistTopTracksSource.cs:50-51`
which is the model pattern; they are reactive to a signal one callback may never write).

Late identity arrival re-projects correctly end to end (`PublishKeys` → `OnCatalogChange` → `DependsOn` →
`RecomputeCatalog` → `Publish` → binder `_sourceEpoch` → `Rebuild`); not the bug.

Fixes (Part 5 item 2): (1) delete the `postUi` at `LiveSessionHost.cs:600-607`; derive the UI scope from the
`CatalogChangeKind.Session` publication in the `data.Catalog.Changes` subscription that `Services.cs:272-283`
already holds (idempotent — all three `RebindScope`s early-return on equal scope; no epoch guard needed; also gives
logout/reconnect their missing inverse); (2) a superseded scope is not "offline": either suppress `SetFailed` while
`svc.CatalogScope != svc.Data.Catalog.Scope` (`DetailPage.cs:77`, `QueryHooks.cs:51`) or, cleaner, a `Superseded`
bit on `QueryStatus` when `ActiveScope` is false that `InitialFailure` returns null for; and the pre-login deep
link renders a connecting state, not "Something went wrong"; (3) one log line at the actual scope flip (pre/post
scope); (4) a Debug-only gate in `QueryService.Acquire` (`:51`) rejecting a spec whose scope is not the repository's
(engine-free, unit-testable).

Not determined: whether the handover failed in the user's surviving session (all three logged sessions crashed
before the sidebar settled; the "minutes later" observation is from a process not in this log — fix 3 settles it);
this agent also flagged the `AlbumVersionsMenu` popup as a crash candidate — superseded by 4.1's finding that
`AlbumMoreByList` is the only required-context consumer that mounts without a click, and that the mechanism is the
parked-page mount; and it independently noticed the artist re-fetch thrash, resolved in 4.3 as relation paging (with
one extra lead: `RelationProjection.cs:42,61,64` revalidation is based on a GLOBAL repository revision that every
accept bumps, so `attempted != basis` can stay true — worth a bounded-revalidation guard).

### 4.3 36 single-key batches for one artist; titled rows rendering empty

Evidence: 36 × `extended-metadata keys=1 subjects=1 first=spotify:artist:3eVa5w3URK5duf6eyVDbu9` in ~2 s; Top tracks
rows empty despite `keys=24 subjects=12 … ready=24`; album shows 22 shimmer rows despite `album-envelope … ready=2`.
**Result A: CONFIRMED — relation paging, one page per round trip, each re-downloading the same document.**

Log-reading correction first: each `catalog.fetch.batch` line prints the message AND the field list, so every
`group=… keys=…` substring appears twice per line; the real count is **18** single-key batches, not 36. The
mechanism is unchanged. All 18 are `ArtistV4` extended-metadata reads of the same artist because
`SpotifyCatalogDecoder.ExtensionFor` (`SpotifyCatalogDecoder.cs:30`) maps FOUR facets — `ArtistIdentity`,
`ArtistDiscography`, `ArtistAppearsOn` — onto one extension, so every discography/appears-on PAGE key is an
extended-metadata request. The keys are relation pages `Arguments(offset, 50, null, filter)` with offset marching
50 at a time for four pagers: `ArtistDiscography` × `"Albums"`, `"Singles"`, `"Compilations"`, and
`ArtistAppearsOn`. Page 0 of all four was the one `keys=4 subjects=1` batch; every later page is its own POST.

Why one at a time (exact path):
1. `RelationProjection.Read` (`Backend/Queries/RelationProjection.cs:53-73`) walks pages from offset 0 and BREAKS at
   the first unloaded page, setting `_nextPage`; `Require` (`:100-106`) returns the loaded prefix plus at most that
   one key. Requirements can never contain page n+2.
2. `ArtistReleasesDefinition.Requirements` (`CatalogQueryDefinitions.cs:285`) and `ArtistDefinition` (`:238`) already
   ask for the whole relation (`Require(Math.Max(End(demand), Total ?? 50))`); `_requestedEnd` is right, the
   projection simply refuses to name the keys.
3. `RunDemandAsync` (`QueryService.cs:507-525`) awaits each wave fully; with one new key per `Recompute`, `Take(256)`
   never engages — one key per iteration.
4. `TakeBatchLocked` (`ResourceCoordinator.cs:226-249`) can only batch what is already pending; each pager is
   blocked on its single awaited `EnsureAsync`, so the ready set is one job → `keys=1 subjects=1`.
5. Each POST re-downloads the same blob: `FetchAsync` (`SpotifyCatalogResourceProvider.cs:66-73`) collapses requests to
   distinct `(Subject, ExtensionKind)` pairs — 18 page keys arriving together would be ONE `(uri, ArtistV4)` ask —
   and `SpotifyCatalogDecoder.Page` (`:247-255`) slices the complete item list by `Offset/Limit`. The document
   already contains every page.

Not revalidation (`superseded=0`, `failed=0`, no key repeats), not `Superseded` re-admission.

Fix (Part 5 item 3a): in `RelationProjection.Require`, once page 0 is loaded and `Total` is known, emit EVERY page
key up to `min(_requestedEnd, Total)` (page n is deterministically `_key(new(n*50, 50, null, _filter))` because
`PageKey` (`:91-98`) normalizes a numeric cursor equal to the offset to `null`); keep the one-at-a-time walk only for
opaque non-numeric cursors. Then all pages land in one `EnsureAsync` → one batch → ONE POST.
Adjacent: `SpotifyCatalogDecoder.CreateDocumentDecoder` (`:48`, "one immutable transport body may supply several
finite facets/pages") is **dead code with zero call sites** — wiring it into the XM path lets one decoded
`LeanArtist` satisfy all pages of all four facets. And three `DiscographySection`s mount eagerly with
`_expanded = true` (`ArtistPage.AlbumExpand.cs:661,678`) demanding the ENTIRE discography regardless of the visible
window — bounding `ArtistReleasesDefinition.Requirements` by `End(demand)` (the `("albums", first, last)` window it
already sets at `AlbumExpand.cs:684`) removes most of the traffic outright.

**Result B: CONFIRMED — three distinct defects; no titled row is ever stuck in Loading.**

B0. `TrackMetadataReadiness.Title` (`Wavee.Core/Catalog/TrackMetadataReadiness.cs:8-19`) checks `track.Title` FIRST
(`:11-12` → Ready), so a titled identity never shimmers regardless of `Activity`/`Provenance`. Every shimmer row
has an EMPTY joined `Track.Title` (`CatalogReadView.cs:87`, `identity?.Title ?? ""`). The trap is the missing escape
at `:15-18`: `Unavailable` (the only branch with a Retry button, `DetailTracks.cs:2853`) requires
`Provenance == Provider`; an entry that is `Knowledge.Present` via an **inline seed** with a null title falls through
to `Loading` forever with no retry. That state is manufactured by `CatalogRepository.ReduceSeed` (`:390-397`,
promotes Unknown → Present with `Provenance = InlineSeed`) + `SpotifyCatalogDecoder.AlbumTracks` (`:174-200`, one
`TrackIdentity` seed per disc track, `Title: Field(track.HasName, …)` — a gid-only `LeanAlbum` disc track seeds
Present-with-null-Title) and `ArtistRefs` (`:257-268`, a gid-only artist ref seeds a NAMELESS `ArtistIdentityValue`
→ `CatalogReadView.ArtistRef` (`:44`) renders `""` → the empty avatars joined by a bare ","). Correction to the
brief: these seeds do NOT block the fetch — `IsFresh` (`Resources.cs:99-101`) requires `Provenance == Provider`, so
inline seeds are always re-requested (log: `keys=62 subjects=31 … ready=62` for the album). The window is transient;
the UI just cannot tell "seeded but nameless" from "never asked".
Activity lifecycle: `CatalogRepository.Reduce` (`:399`) resets `Activity = Idle` on every accept — fine. One real
leak: `ResourceCoordinator.CompleteLocked` (`:416-421`) does not clear activity, so the `Superseded` path in
`TakeBatchLocked` (`:228-229`, stale generation) leaves an entry stuck at `Queued`/`Fetching` with no in-flight job.

B1. **The artist Top-tracks chart has NO readiness gate at all.** `TrackMetadataReadiness` is used in five places
(`AlbumReleaseFactsRules.cs:67`, `DetailPage.cs:317/459/489`, `DetailTracks.cs:2818`) — `ArtistPopular.cs` is not
one of them. `ArtistPopular.cs:433` renders `new TextEl(t.Title)` unconditionally; `:352` renders
`DurationCell(t.DurationMs)` → "—" for 0; the heart is wired off `track.Uri.Length > 0` (`:529`), which is why hearts
survive. Exactly the reported symptom, by design.

B2. **The `ArtistOverview` orphan seed** (review 2.2 #10) is why the window is visible at all:
`SpotifyCatalogEnvelopes.cs:148-153` seeds the popular relation under `Arguments = default` while
`RelationProjection` reads/requires `Arguments(0, 50, null, null)` (`RelationProjection.cs:97,103`); `ResourceArguments`
is part of `ResourceKey` identity (`Resources.cs:69`), so the seed is dead. The overview's ten popular tracks ARE
titled-seeded right above (`:146`), but never back the chart's first paint; the chart's rows come only from the
separate `artist-top-tracks-extensions` call (`FetchChartAsync`, `:176-193`) which returns URIs only, `seeds: []`.

B3 (third, independent, found on the way). `ArtistPopular.cs:119` and `SidebarArtistTopTracksSource.cs:76` lease the
window collection `"popular"`, but `ArtistDefinition` only honours `"rows"`/`"tracks"` (`CatalogQueryDefinitions.cs:79-80`,
`CollectionDemand(demand, "tracks")` at `:239`) — those leases contribute ZERO row keys. Only `ArtistPage.cs:79`'s
`("tracks", 0, 10)` does, so chart rows 11..`ExtendedCap` are never identity-joined by any lease. Very likely why a
wider chart page stays blank.

Fixes (Part 5 item 3b–e): (b) `SpotifyCatalogEnvelopes.cs:149` seed under `Arguments = new(0, RelationProjection.PageSize)`;
(c) `ArtistPopular.cs` gates rows through `TrackMetadataReadiness.Title` and renders its own derivable skeleton for
Loading and the "unavailable + Retry" line otherwise; (d) `TrackMetadataReadiness.cs:15-17` — Present + null title +
`Activity == Idle` + non-Provider provenance → `Unavailable` (Retry), or better, `ReduceSeed` refuses to promote
Unknown → Present when the patch contributes no readiness field; (e) `CompleteLocked` clears `Activity` to `Idle`
like `AwaitJobAsync`'s finally (`:199-201`); (f) `ArtistDefinition` honours the `"popular"` collection (or the two
leases use `"tracks"`).

Not determined: exact page counts per pager (the log prints only `first=<subject>`, not facet/offset — add
facet/offset/filter to the batch line for single-key batches); whether the artist's ten track identities were
queued behind the storm or never entered `Requirements` within the 1.9 s logged window (B3 makes "never" likely for
rows > 10); the "22 rows" album screenshot vs the logged 31-track decode (`tracks=31`, `ready=62`) could not be
reconciled — possibly a different album version via `AlbumVersions`.

---

## Part 5 — Remediation plan (to execute after approval; no code in this document)

Ordered by user impact. Each item names the file(s) and the pure-class test that pins it (no source-text tests).

1. **Crash** (4.1) — **engine**: `Reconciler.Mount` inherits `NodeFlags.Parked` from the parent for every node
   kind (`..\fluent-gpu\src\FluentGpu.Engine\Reconciler\Reconciler.cs:621`); gate in the engine repo (Debug +
   Release build, VerticalSlice "ALL CHECKS PASSED") with a new VerticalSlice probe: a KeepAlive-parked page whose
   skeleton region swaps to a context-reading component while parked must defer, then resolve the context on
   un-park. **App**: the three `UseRequiredContext(Services.Slot)` sites (`AlbumRelatedSections.cs:25,62`,
   `PlaylistPicker.cs:53`) become the tolerant `UseContext` pattern as defence in depth. Also: the publish must emit
   a CodeView debug entry / ship the symbols zip so the next crash symbolizes (release-tooling item, `ops/release`).
2. **Scope handover** (4.2) —
   (a) delete the fire-and-forget `postUi` at `SpotifyLive/LiveSessionHost.cs:600-607`; publish the UI scope from the
   `CatalogChangeKind.Session` observer already in `App/Services.cs:272-283` (set `CatalogScopeSignal`,
   `Playback.AttachQueueQueries`, `LibraryStore.RebindScope`, `SidebarBinder.RebindScope`; idempotent; covers
   logout/reconnect);
   (b) a superseded scope is not "offline": a `Superseded` status bit in `QueryService` (`:311`) when the key's scope
   is no longer `ActiveScope`, ignored by `QueryPresentationRules.InitialFailure` (`QuerySignalBinding.cs:423-430`);
   the pre-login restored deep link renders the connecting state, not "Something went wrong";
   (c) one always-on log line at the scope flip (pre/post scope, epoch);
   (d) Debug-gated assert in `QueryService.Acquire` (`:51`) that a spec's scope is the repository's scope.
   Tests: `CatalogRuntimeSessionTests` — after `SetSessionAsync` the signal/rebind path runs from the publication
   with no bootstrap code; `QueryServiceBehaviorTests` — a node under scope A reports Superseded (not Offline) after
   the session moves to B, and a re-acquire under B fetches; `QueryPresentationRulesTests` — Superseded yields no
   failure.
3. **Fetch storm + empty rows** (4.3) —
   (a) `RelationProjection.Require` names every offset-addressable page up to `min(requestedEnd, Total)` once page 0
   is loaded (`Backend/Queries/RelationProjection.cs:100-106`); wire the dead `CreateDocumentDecoder`
   (`SpotifyCatalogDecoder.cs:48`) into the extended-metadata path so one decoded document serves all pages of all
   facets; bound `ArtistReleasesDefinition.Requirements` by the visible window instead of `Total`
   (`CatalogQueryDefinitions.cs:285`, window from `ArtistPage.AlbumExpand.cs:684`).
   (b) `SpotifyCatalogEnvelopes.cs:149` seeds `ArtistPopular` under `Arguments(0, PageSize)`.
   (c) `ArtistPopular.cs` gates each row through `TrackMetadataReadiness.Title` (shimmer / unavailable+Retry) like
   `DetailTracks`.
   (d) `TrackMetadataReadiness.cs:15-18` — Present + null title + Idle + non-Provider → `Unavailable`; and
   `CatalogRepository.ReduceSeed` (`:390-397`) does not promote Unknown → Present when the patch carries no
   readiness field (no title/name).
   (e) `ResourceCoordinator.CompleteLocked` (`:416-421`) resets `Activity` to `Idle` on Superseded/Deferred.
   (f) `ArtistDefinition` honours the `"popular"` window collection (`CatalogQueryDefinitions.cs:79-80,239`) so
   chart rows beyond 10 get identity keys.
   (g) The `catalog.fetch.batch` line adds facet/offset/filter for single-key batches.
   Tests: `CatalogDemandWasteTests` (artist open = one ArtistV4 POST for all discography pages), a new
   `RelationProjectionTests` case (all pages named once Total is known; opaque cursors stay sequential),
   `TrackMetadataReadinessTests` (seeded-nameless → Unavailable, never eternal Loading), `ResourceCoordinatorTests`
   (Superseded clears activity), a `CatalogQueryDefinitions` test that a `"popular"` window yields row keys.
4. **Playback review findings** (2.1 #1, #4, #5, #6, and #9) — one lane on `FluentMediaAudioHost*.cs` +
   `PlaybackController.cs`: guard `ApplySeekAsync` against a session that is not the in-flight load; seek with no
   session parks instead of faulting; `SoftReloadAsync` catches and keeps the old session (and fades it back in);
   `Load` never reuses an older command id; remote seek leaves `Accepted` on clamp. Tests in `Wavee.Tests/Audio/*`.
5. **Outbox review findings** (2.1 #2, #3) — one lane on `Mutation.cs` + `LibrarySync.cs`: transport failures stay
   Pending with backoff (restore HEAD's 10-attempt policy); per-playlist try/catch in the baseline recovery loop and
   always re-arm the re-drain. Tests: `DurableOutboxTests`, `LibrarySyncTests`.
6. **Persistence review findings** (2.1 #7, #8, 2.2 #11) — restore the `playlist_items` pin leg (per-root limit, not
   global); treat an undecodable/version-mismatched row as a miss and delete it; release `_admission` before the
   persistence round trips. Tests: `CatalogRetentionTests`, `SqliteSchemaResetTests`.
7. **Artist hero** (Part 3) — measure-driven height with the tier height as floor. Test: `ArtistHeroLayoutTests`
   (`MinHeight >= CopyBudgetFor(tier)` with a two-line bio).
8. **Engine** (Part 6) — separate approval; not part of this execution.

### Execution order and lanes (parallel subagents on disjoint files; orchestrator alone builds/tests/launches)

| Order | Lane | Repo | Files (disjoint) | Model |
|---|---|---|---|---|
| 1 | Engine parked-mount fix + VerticalSlice probe | `..\fluent-gpu` | `Reconciler.cs` (Mount), `VerticalSlice/Suites/*` | Opus |
| 1 | Scope handover (2a–2d) | Wavee | `LiveSessionHost.cs`, `App/Services.cs`, `Backend/Queries/QueryService.cs`, `App/Queries/QuerySignalBinding.cs` (rules only), `Features/Detail/DetailPage.cs` (failure gate), `App/Queries/QueryHooks.cs`; tests `CatalogRuntimeSessionTests`, `QueryServiceBehaviorTests` | Sonnet |
| 1 | Relation paging + chart readiness (3a–3g) | Wavee | `Backend/Queries/RelationProjection.cs`, `CatalogQueryDefinitions.cs`, `SpotifyLive/Catalog/SpotifyCatalogEnvelopes.cs`, `SpotifyCatalogResourceProvider.cs` (document decoder), `Backend/Catalog/SpotifyCatalogDecoder.cs`, `CatalogRepository.cs` (ReduceSeed), `ResourceCoordinator.cs` (CompleteLocked + log fields), `Wavee.Core/Catalog/TrackMetadataReadiness.cs`, `Features/Detail/ArtistPopular.cs`, `ArtistPage.AlbumExpand.cs`; tests listed in item 3 | Sonnet |
| 1 | App-side tolerant contexts (item 1, app half) | Wavee | `Features/Detail/AlbumRelatedSections.cs`, `PlaylistPicker.cs` | Sonnet |
| 2 | Playback review findings (item 4) | Wavee | `SpotifyLive/Audio/FluentMediaAudioHost*.cs`, `Backend/PlaybackController.cs`, `PlaybackProjection.cs`; tests `Wavee.Tests/Audio/*`, `PlaybackCatalog*` | Sonnet |
| 2 | Outbox review findings (item 5) | Wavee | `Backend/Mutation.cs`, `Backend/Sync/LibrarySync.cs`, `LibraryReplicaCoordinator.cs`; tests `DurableOutboxTests`, `LibrarySyncTests` | Sonnet |
| 2 | Persistence review findings (item 6) | Wavee | `Backend/Persistence/SqliteCatalogMaintenance.cs`, `SqliteCatalogPersistence.cs`, `Backend/Catalog/ResourceCoordinator.cs` (admission — coordinate with lane 3 on ordering: same file, run after) | Sonnet |
| 3 | Artist hero (item 7) | Wavee | `Features/Detail/ArtistHeroLayout.cs`, `ArtistPage.Hero.cs`; `ArtistHeroLayoutTests` | Sonnet |
| — | Release tooling: publish emits CodeView / symbols zip for dev publishes | Wavee | `ops/release/*`, `Wavee.csproj` publish props | Sonnet |

Order 1 lanes run together; order 2 after order 1 builds green; order 3 any time. The engine lane is gated in the
engine repo first (Debug + Release + VerticalSlice); the app is rebuilt against it.

### Verification (end to end)

1. Engine: `dotnet build src/FluentGpu.slnx` Debug + Release clean; `dotnet run --project src/FluentGpu.VerticalSlice`
   "ALL CHECKS PASSED" including the new parked-mount probe.
2. App: `dotnet build Wavee.slnx` Debug + Release clean (`TreatWarningsAsErrors`); `dotnet test
   src/apps/Wavee.Tests/Wavee.Tests.csproj` green (baseline 7,332 passed).
3. Launch the Release publish with the user's real `library.db` and read `%LOCALAPPDATA%\Wavee\logs\wavee-<date>.log`:
   - no `Fatal error in the app loop`, no crash report, across open-album → navigate-away-within-2-s → wait 10 s;
   - exactly one scope-flip line after `session-installed`, and NO `Surface error shown: No saved copy is available
     offline.` after it on a connected launch; the pre-login deep link shows a connecting state, not an error;
   - an artist open produces ONE `extended-metadata` batch for `ArtistV4` (all discography pages), not 18;
   - sidebar rows show names within one fetch wave (no raw ids); album header artists named; album rows and Top
     tracks titled; any row that cannot resolve shows "unavailable + Retry", never an eternal shimmer.
4. Visual: the ARASHI hero shows its full action row with a two-line bio.
5. Playback: seek during load, Previous on a cluster-recovered paused session, seek in the first second, and an
   audio-device switch mid-track — none stops playback or toasts; an offline like is sent on reconnect.

### Findings verification note

Before writing Part 5, the orchestrator spot-checked each investigator's load-bearing claim against the source:
`RelationProjection.Read` breaks at the first unloaded page (`:60`); `Services.cs:272-283` already subscribes to
`Catalog.Changes` and `LiveSessionHost.cs:600-607` is the sole, epoch-guarded `postUi` handover;
`Reconciler.MountComponent` (`:925-940`) inherits `Parked` only for components while `Mount` (`:621`) does not;
`ArtistPopular.cs:433` renders `t.Title` ungated; `SpotifyCatalogEnvelopes.cs:149` seeds with `Arguments = default`;
`CatalogQueryDefinitions.cs:79,180,237-239` honour only `"rows"`/`"tracks"`. All confirmed.

---

## Part 6 — Engine: animations that survive a busy UI thread

Design written at `C:\wavee\fluent-gpu\docs\plans\render-thread-animation-design.md` (787 lines).
Established facts: the canon's time-sliced `ReconcileSlicer`/`RenderPriorityPolicy` (`threading-render-seam.md`
§12.1) and `SnapshotColumns` are NOT implemented; `_anim.Tick` is phase 7 on the UI thread (`AppHost.cs:3349`); the
render thread only submits/presents (Cut A). Recommendation: name the 16 ms budget (cheap half of §12.1), then the
**compositor-group variant** — render thread owns compositor-channel `AnimValue` rows, ticks on the display clock,
re-submits the last published DrawList with a per-group transform/opacity apply table (no `SceneRecorder` rewrite,
no Cut B). 29–41 engineer-days in 7 gated steps; first commitment 6–9 days (step 0 + a spike with a kill condition:
opacity may not factor out of the recorder walk). Top risks: group-apply equivalence, widening the canon's single
lock-free surface, and everything that implicitly relied on "an animation means a UI frame" (caret, crossfades,
scrollbar chrome, repeat button, gesture timers). Two canon-drift findings: `DrawListArenaRing` no longer exists in
`src/` but is named in four canon places; §11.1's `PhaseGateBlocks` is as-built `AppHost.ProductionGateBlocks()`.
Awaiting the user's go.

---

## Part 7 — Measurement run (2026-09-07) and what it changed

Tooling added (always on, no switches): `ops/tools/nav-measure.ps1` drives ten deep-link navigations (home, two
albums, two artists, one playlist; cold then warm) against the Release build and collects: `[ui] nav.frames` (one
line per route change: frames, fps, avg/worst frame with the worst frame's phase split, counts over 33/100 ms, time
to first frame), `[ui] frame.stall` (any frame ≥ 100 ms), `[catalog] catalog.demand.wave` (per query wave: what a
page asked for, how each key came back), `[hydration] hydration.gaps` / `hydration.settled` (rows without titles,
the state of their identity resources, and the time to a fully titled page). Engine: `AppHost.FrameCompleted` /
`FluentApp.FrameCompleted` relay each rendered frame's `FrameStats` (phase times were already computed there).

### Run 1 (before the demand fix; catalog cold)

| Route (cold) | worst frame | slow ≥33 ms | rows never titled in 7 s |
|---|---|---|---|
| restored album (startup) | 268 ms (first paint) | 5 | 1 of 1 |
| home | 62 ms (reactive flush 38) | 3 | — |
| album 5Qg2… (31 rows) | 27 ms | 0 | **18 of 31** |
| artist 3eVa… (Phil Collins) | 108 ms (reactive 41, layout 12; 11.3 MB alloc) | 4 | 2 of 12 |
| playlist 37i9… (50 rows) | 23 ms | 0 | **20 of 50** |
| album 7kFy… | 23 ms | 0 | no rows published |
| artist 4lxf… (50 chart rows) | 68 ms (reactive 48; 9.2 MB) | 3 | **16 of 50** |

Only one 28-key track-identity batch was fetched in the whole run.

### Run 2 (demand log added) — the mechanism

The playlist's first wave asked for all 50 rows (`keys=151`: identity + play count + audio attributes) and **90 of
those came back Deferred**: the page's demand window is "all rows" while membership is unknown, then the reveal
ramp realizes a few rows at a time, `PlanDemand` shrank `_desiredResources` to those, the queued waiters were
cancelled (`QueryService.PlanDemand` obsolete-waiter cancellation), and the rows past the window were never asked
again unless scrolled into view. Rows that did resolve came from unrelated seed commits one at a time (30
publications in 230 ms, each a full page re-render). Warm pages settled in 43–315 ms, proving fetch throughput was
no longer the limit.

### Fix (this run) and Run 3

`CatalogQueryDefinitions.Indices`: identity demand looks 300 rows ahead of (and 50 behind) each window — one
extended-metadata POST carries 300 subjects, so the cost is one round trip either way. `QueryService.PlanDemand`: a
window move never cancels queued requests; only parking/disposal releases waiters (a fetch for a row that scrolled
away completes into the cache).

| Route | time to fully titled | worst frame | slow ≥33 ms |
|---|---|---|---|
| restored playlist (startup) | settled | 320 ms (first paint) | 8 |
| home | — | 65 ms (reactive 49) | 3 |
| album 5Qg2… | **98 ms** | 21 ms | 0 |
| artist 3eVa… | **103 ms** | 118 ms (reactive 87; 11 MB) | **23** |
| playlist 37i9… (warm) | settled | 12 ms | 0 |
| album 7kFy… | **44 ms** | 25 ms | 0 |
| artist 4lxf… | **115 ms** | 76 ms (reactive 58) | 5 |
| warm re-opens | — | 7–19 ms | 0 |

No identity key is deferred any more; identities travel in batches of 211 / 59 / 22 keys.

### What remains (measured)

1. **Render cost per publication on the artist and home pages**: 58–87 ms of `reactive` flush (component
   re-renders on the UI thread) with 5–11 MB allocated in one frame; the artist page re-renders its whole body on
   every publication of any of its queries, and the sidebar / Liked Songs queries re-plan and republish on every
   navigation (17-key and 124-key waves, three times within 30 ms). Investigation and fix design in progress.
2. **Startup first paint** 320–380 ms, of which only ~80 ms is in the logged phases (startup-only warmup —
   image/glyph/pipeline — to be named).
3. "fps" in `nav.frames` counts rendered frames per wall second; an idle page reads low by design. Use slow33 /
   stall100 / worst for feel.

---

## Part 8 — Render cost on cold artist / home opens (investigated 2026-09-07; fixes in flight)

Measured: cold artist open = 23 frames ≥ 33 ms in 4 s, worst 118 ms (`reactive` 87 ms = component re-renders on
the UI thread), 11.4 MB allocated in that frame; home worst 65 ms (`reactive` 49). Album pages 21–27 ms.

Causes (file:line verified by a read-only investigation):

1. `ArtistPage.cs:~79` evaluates `PendingArtist(uri)` on EVERY render — `FakeData.Artist(...)` builds six albums
   with 6–24 tracks each, covers and synthesized extras, dead after the first Ready frame. Largest pure-waste alloc.
2. `Body` is a `Func<Element>` under `Skel.Region` with no memo (`ArtistPage.cs:~148-156`); one page render rebuilds
   all 14 sections and the hero. LINQ chains per render at `:~97-99` and `:~217-220`.
3. Every shelf's props carry fresh closures (`cardAt`, `header` Element, `onVisibleRange` —
   `ArtistPage.Shelves.cs:~29-34, 68-73, 87-90, 207-211, 225-229`; `PagedShelf.Create` is
   `Embed.Comp(new ShelfProps<T>(...))`, `Responsive.Of` likewise), so the reconciler's value-equality props gate
   never holds and all 8 shelves + 2 responsive bands re-render on every Body rebuild.
4. `ArtistPage.AlbumExpand.cs:~686` `UseComputed(() => …Items.ToArray())` returns a new array each recompute, so
   the memo's equality cut-off never fires; three sections re-render and re-plan eras on every publication.
5. `ArtistPage.cs:~78` and `ArtistPopular.cs:~121-124` acquire the same `ArtistDetailQuery` — two bindings, two posts,
   two renders per publication.
6. Whole-bio reparses per render (`RichText.Of`, `FirstSentence`/`StripHtml`) and per-card `MenuAttach`/`DragSource`
   allocations.
7. Re-render triggers per cold open (estimated from the subscription graph): 10–25 from `SavedArtistsQuery`
   republishing per fans-also-like identity commit, 6–10 from `ArtistDetailQuery`, up to 5 shelf visible-range
   signals, 2–4 hero measures. ≈ 25–45 page renders × (1 page + 2 bands + 8 shelves + cards).
8. Sidebar: `SidebarProjectionBinder.Epoch` (`:~796-801`) folds `snapshot.Revision`, which `QueryService.Recompute`
   bumps on ANY change including an unrelated identity commit, so the full `Rebuild()` runs on every navigation
   two to three times. `QueryService.OnCatalogChange` recomputes every node, inactive leases included, and allocates
   a node-table snapshot per change set.
9. Per-frame coalescing across the page's queries already exists in the engine (UI posts drain inside one reactive
   batch per frame); the cost is that the deliveries land on different frames and each one costs a whole page.

Fixes being implemented: (1) memoize the pending seed; (2) counted passes into exact-size arrays and `UseComputed`
slices; (3) cached delegate fields + memoized headers so shelf/band props gate; (4) memo returns the list
reference; (5) `ArtistPopular` takes the page's presentation; (6) memoized bio parse; (7)+(9) section-scoped
subscriptions with delegate-free props records (last, if time allows); (8) sidebar trigger folds the projected
value's identity, and `OnCatalogChange` skips inactive nodes without a per-change allocation. `nav.frames` now also
prints `comps=` (components rendered), `imagePump=`, `realizeCatchup=` and `unaccounted=` per frame.

Startup first paint (320–380 ms): the whole time is inside one `Paint`; `submit`, `fenceWait`, `present`, `gpu`
are tiny on the stall line, so the residue is outside the stamped phases (post-submit drains, passive effects,
first-frame warmups) — `unaccounted=` now measures it directly.
