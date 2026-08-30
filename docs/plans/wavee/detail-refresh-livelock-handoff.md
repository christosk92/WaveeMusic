# Detail-page ↔ hydration refresh livelock — handoff

Self-contained. Diagnosis is **verified**; the fix is **Implemented** — `src/apps/Wavee/Features/Detail/DetailLiveRefresh.cs`
(the trailing-settle refresh, `SettleMs`) replaced the cancel-and-restart debounce in `DetailPage.cs`, and the parked-page
bump is released by `LibrarySync.ClearOpenContext` (`Backend/Sync/LibrarySync.cs`, called from `DetailPage.cs` on close).
Date: 2026-08-16. The out-of-scope items in §5 and the residual-memory note in §6 are still open follow-ups.

| Field | Value |
|---|---|
| Symptom | Wavee working set 1.2 GB peak, settled ~600–900 MB; FPS collapse while it lasted |
| Root cause | A self-sustaining store-change → detail refresh → hydration → store bump loop, run concurrently by every KeepAlive-parked detail page, amplified by per-pass Info logging |
| Not the cause | GPU residency (flat ~360–390 MB, unified memory), managed leak, `ImageCache`, `MemoryGovernor` (see §6) |
| Evidence | `%LOCALAPPDATA%\Wavee\logs\wavee-20260816*.log` (10 MB rolls at 12:54:45–12:56:01, 14:26:32; current file) |
| Working tree | `src/apps/Wavee/Features/Detail/DetailLiveRefresh.cs` is **new + untracked**; `DetailPage.cs` diff swaps the old cancel-and-restart debounce for it. Nothing under `Backend/Hydration`, `Backend/Library`, `Backend/Store.cs` differs from HEAD |
| Plan file | `C:\Users\ChristosKarapasias\.claude-work\plans\there-was-a-logical-scone.md` (same content as §4 here) |

---

## 1. What the logs show

Storm session (pid 47344, 12:54–12:56):
- 161,753 `[detail] video.assoc.page` lines (one per detail re-map) across **20 distinct** `contextUri`s.
- 78,822 `[hydration] hydration.pump.full` warnings; ≥ 99,733 hydration jobs dropped after the 4,096-job queue filled
  (`priority=0` newcomers evicting `droppedPriority=-1` trait jobs — the loop preferentially sheds its own work).
- 76.7 MB of log across the retained rolls; a 10 MB file every 2–3 s.

Current session (pid 32020, later restart) — **the loop recurred without any code change**:
- 7,232 `video.assoc.page` re-maps in 919 s across 7 parked playlist pages + 1 album:
  `2pnt79m9…` 1851, `37i9dQZF1DWXqpDKK4ed9O` 1620, `4VC1Y6RR…` 1553, `37i9dQZF1E8OpR3bZBXBqF` 957, …
- Shape: a burst of ~2,700 in the first 10 s (~270/s), decaying to ~160 per 10 s (~16/s), dying out after ~40 s, then
  re-igniting ~14 min later on the next store change (track change → hydration → bump). Not exponential, not infinite:
  a **bounded livelock per page** at roughly 1 pass / (load latency + 50 ms), × pages.

Useful one-liners (PowerShell):
```powershell
$log = Join-Path $env:LOCALAPPDATA 'Wavee\logs\wavee-20260816.log'
Select-String -LiteralPath $log -Pattern 'video\.assoc\.page' | % { [regex]::Match($_.Line,'contextUri=(\S+)').Groups[1].Value } | group | sort Count -desc
Select-String -LiteralPath $log -Pattern 'hydration\.pump\.full' | measure
```

## 2. The loop (every edge verified in code)

```
StoreChange(Uri==pid | IsBulk)                          Store.cs:676-686  UpsertPlaylist → Bump(pid)  ◄──┐
   │ DetailPage.cs:182-206  (subscription, relevance predicate)                                            │
   ▼                                                                                                       │
DetailLiveRefresh.Request()  → pass → DetailPage.LoadAsync           DetailPage.cs:152-181, 330-336        │
   │ playlist: svc.Library.GetPlaylistAsync(uri)   ← HydrationLevel.Open (default)  DetailPage.cs:367-368  │
   ▼                                                                                                       │
StoreLibrarySource.GetPlaylistAsync   StoreLibrarySource.cs:73-104                                         │
   OpenPolicy.For(Playlist, hasBaseline:true) = (Blocking None, Background Open, Revalidate:true)          │
   → _ = _hydration.EnsureAsync(uri, Open, Background+Revalidate)   ← on EVERY call, no freshness gate     │
   ▼                                                                                                       │
SpotifyProviderHydrator: Revalidate bypasses the ledger (:109); playlist Open is never sealable anyway     │
   (HydrationLedger.cs:126-127 SealsLevel) → full ladder every time                                        │
   step 0: XmCatalogFetch → ExtensionEtagCache (in-memory HIT, no network) → ExtendedMetadataSource        │
   .ProjectPlaylist (:438-473) → store.UpsertPlaylist(header)  ── unconditional, no value compare ─────────┘
   + PlaylistHydration.cs:74-83 enqueues a trait pass over EVERY member (ungated) → pump saturation
   + LibrarySyncPlaylistOpener.Revalidate → _sync.Enqueue with no in-flight dedupe (5-min gate only guards /diff)
```

Key corrections vs the first-pass hypothesis:
- The **closing edge is the per-URI `Bump(playlistUri)`**, matched by `c.Uri == pid` — the `IsBulk` term is not needed.
- The trait pass does **not** always emit a `Bulk`: `TraitBatch` opens its bulk scope lazily on first write
  (`ITraitProjector.cs:109-116`) and `TraitPipeline.Plan()` is empty for a warm set (`TraitPipeline.cs:81, 96-124`).
  `Store.EndBulk` (`Store.cs:928-945`) does fire with zero writes once a scope is open, but `TraitBatch` never opens one.
- `LogVideoSweep` / `video.assoc.page` (`DetailPage.cs:488-518`) is a **read-only diagnostic** — it does not write or
  enqueue; it is the amplifier, not a driver.

Amplifiers:
- `Flow.KeepAlive(MaxEntries: 8)` (`ContentHost.cs:43-50`). Park = `SetSubtreeParked` (`Reconciler.cs:1428-1441`,
  `1507-1541`): defers the render-effect, flips the activation signal, detaches the scene — **the component scope, its
  `UseSignalEffect`s and their `Reactive.OnCleanup`s stay alive**; cleanup only runs on LRU eviction
  (`FreeKeepAliveEntry` → `UnmountSubtree`, `Reconciler.cs:1463-1487, 3276`) or unmount. So every parked detail page runs
  its own copy of the loop.
- `DetailLiveRefresh` (single-flight + coalesce, first request immediate, 50 ms settle only *between* dirty passes)
  fixed the "optimistic edit never shows" starvation bug but removed the accidental throttle: a pass is never abandoned,
  so every pass reaches `GetPlaylistAsync(Open)`.
- Logging: `video.assoc.page` at Info per pass (StringBuilder + 8 fields), `hydration.pump.full` at Warning per drop.
- Fan-out: `StoreLibrarySource.OnStoreChange` (`:696-704`) turns one `Bulk` into 5 `CollectionsChanged` → `LibraryStore`
  re-runs `GetLikedSongsAsync` etc. (`App/LibraryStore.cs:134-154`) — converges (Liked is ledger-sealed) but is load.

Latent bug found on the way — `LibrarySync.SetOpenContext` (`LibrarySync.cs:166-203`) is a single last-writer-wins
slot. A parked page's eventual eviction cleanup calls `SetOpenContext(null)` (`DetailPage.cs:212`) and clobbers the
visible page's context (its pushes stop revalidating eagerly, `IsOpen` at `:578/:622`, and it drops out of the resync
target list `:960-967`); returning to a parked page never re-sets it (mount-once effects).

## 3. Engine facts you need (verified)

- `UseIsActive()` / `UseActivation(onActivated, onDeactivated)` — `RenderContext.cs:790-847`. "Active" =
  not-KeepAlive-parked **and** window visible. Backed by a standalone `Effect`, so it keeps observing while parked;
  `UseActivation` fires on transitions only (silent at mount/unmount).
- `Context.UseSignalEffect` (`RenderContext.cs:626-634`) creates its `Effect` once on first render; re-runs whenever a
  signal read *during the body* changes, running `OnCleanup`s first (`ReactiveCore.cs:205-209`). The current DetailPage
  effect reads no signals → runs once per mount. Reading `active.Value` inside it makes park/activate re-run it — that is
  the whole mechanism for the fix.
- Reference pattern: `UseInterval` (`RenderContext.Timers.cs:317-340`) pauses on `UseIsActive`; app-side
  `HomePage.cs:108-142` / `RecentsPage.cs:331-356` use `UseActivation` (refresh-on-reactivation).
- Engine gates: `NavSuite.cs:141` (50a KeepAlive), `:364` `gate.reconciler.park-before-render` (outgoing page parks
  **before** the incoming renders — ordering matters for the open-context handoff), `:722` (50b UseActivation).

## 4. The fix (planned; not started)

### 4.1 `HydrationLevel.None` = strict cache-only read — `apps/Wavee/Backend/Library/StoreLibrarySource.cs`
- `GetPlaylistAsync`: `level == None` ⇒ skip `OpenPolicy` (no blocking/background `EnsureAsync`, no
  `PrefetchPlaylistUsers`), compose from the store exactly as lines 85-103 do today. Factor that composition into a
  private helper so hydrated and cache-only reads are identical.
- `GetShowAsync`: same (skip both plan arms; keep composition at 481-501).
- `GetLikedSongsAsync`: change to `GetLikedSongsAsync(HydrationLevel level = Open, CancellationToken ct = default)` on
  `ICatalogSource` (`Wavee.Core/Sources/ICatalogSource.cs:62`) and `IMusicLibrary` (`Wavee.Core/Library/Library.cs:136`);
  `AggregateCatalog.cs:144` forwards; `StoreLibrarySource` fires the Liked background ask only when `level != None`.
  Update impls (`SpotifyExportSource`, `FakeSource`, `LocalSource`, `UserPlaylistSource` — ignore `level`) and callers
  (`DetailPage`, `App/LibraryStore.cs`, tests). No legacy overload.
- `GetAlbumAsync(uri, None)` already no-ops (`SpotifyProviderHydrator.cs:74`).
- Document on `HydrationLevel.None` (`Wavee.Core/Hydration/HydrationLevel.cs:12`): as a *request* level = resident
  store only, no ledger ask / revalidate / pump enqueue / I/O.

### 4.2 Store-change refresh is cache-only — `apps/Wavee/Features/Detail/DetailPage.cs`
- Keep `LoadAsync` for the initial `UseResource` load. Add `RefreshAsync(svc, kind, id, ct)` for the live pass:
  playlist → `GetPlaylistAsync(uri, None)` + existing popcount (TTL 6 h, in-flight coalesced, never touches the store —
  keep so `MapPlaylist(p, count)` is unchanged); Liked → `GetLikedSongsAsync(None)`; Show → `GetShowAsync(id, None)`;
  Album → `LoadAlbumDetailAsync` with the album read at `None` (prerelease resolve is reader-cached).
- `ReloadPlaylistDetailAsync` (`:370`) is a mutation-side reload — leave on `Open`.
- Everything else in the pass stays (route re-resolve / nav-away drop, `WithOwners`, `WithNotice`, `PreferVisible`
  cover latch, `PlaylistReorderDefer.TryHold`).

### 4.3 Live work scoped to activation — `DetailPage.cs:145-214`
Replace `UseEffect(SetOpenContext, route.Name)` + the mount-lifetime `UseSignalEffect` with ONE
`Context.UseSignalEffect` that reads `active.Value` (`var active = UseIsActive();`):
- `if (realStore is null || !active.Value) return;`
- create the `DetailLiveRefresh`, subscribe `realStore.Changes` (same predicate), take the open-context lease;
  `Reactive.OnCleanup` disposes sub + pump and releases the lease. Runs at mount and on every reactivation; park runs
  the cleanup.
- Catch-up: `UseRef<bool>` "everActivated"; on a *re*-activation only, `pump.Request()` once (cache-only ⇒ cannot loop).

### 4.4 Owner-gated open context — `apps/Wavee/Backend/Sync/LibrarySync.cs:166-203`
- Add `ClearOpenContext(string uri)` (clears only if `_openUri == uri`). DetailPage sets on activation, clears with
  `ClearOpenContext(id)` in cleanup. Delete the unconditional `SetOpenContext(null)` path.

### 4.5 `DetailLiveRefresh` cooldown + tripwire — `apps/Wavee/Features/Detail/DetailLiveRefresh.cs`
- Hold `_running` through a `SettleMs` cooldown after **every** pass; requests during pass or cooldown fold into one
  trailing pass; first request after ≥ `SettleMs` idle still runs immediately. Bounds a page at ≤ 20 passes/s.
- Tripwire: > 40 passes in any 10 s window ⇒ ONE `detail.refresh.storm` Warning per window (uri, passes).

### 4.6 Log amplification
- `LogVideoSweep`: demote to Debug, gate on `IsEnabled(Debug)` before allocating.
- `HydrationPump.Enqueue` (`HydrationPump.cs:71-106`): keep the exact `Dropped` counter; log `hydration.pump.full`
  only for the first drop and every 256th (include running total).

Out of scope / follow-ups (not needed to break the loop): value-compare in `UpsertPlaylist` before `Bump`; gate
`PlaylistHydration`'s member trait enqueue on membership movement; once-flag the `LibrarySync.cs:853-862` header heal
(re-fetches `/playlist/v2/{id}` per revalidate for collaborative-not-owned lists); in-flight dedupe on
`LibrarySyncPlaylistOpener.Revalidate`; precise URI sets on `StoreChange.Bulk`.

## 5. Tests + verification

- `Wavee.Tests/DetailLiveRefreshTests.cs` (existing 5: immediate first pass, coalesce-during-pass, fresh pass after
  idle, faulted pass, dispose): add cooldown-delays-and-coalesces, sustained ≤ 1 pass / `SettleMs`, immediate after
  full idle; adjust existing for the post-pass cooldown via the `delay` seam.
- `Wavee.Tests/ApiWaste/HydrationWasteTests.cs` (rig: real `SpotifyProviderHydrator` + `HydrationPump` + wire capture,
  `Build()` at :110): after a warm open, `Get{Playlist,Show,LikedSongs}Async(…, None)` ⇒ resident model, zero POSTs,
  zero trait passes, zero pump enqueues; warm `Open` still schedules the background arm (documents the difference).
- `Wavee.Tests/LibrarySyncTests.cs` (SetOpenContext tests at :864/:895/:945): `ClearOpenContext(other)` keeps a newer
  context; `ClearOpenContext(same)` clears.
- `dotnet build src/FluentGpu.slnx` (Debug + `-c Release`), `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`
  (baseline 4509/0, two known flaky), `dotnet run --project src/FluentGpu.VerticalSlice` (3 pre-existing FAILs from
  another session's engine work; nothing here touches the engine).
- Manual: open ~8 detail pages, then play from a playlist / like / drag a track in. Expect no `video.assoc.page` at
  Info, no log rolling, no `detail.refresh.storm`, flat working set + FPS, optimistic edit still next-frame.

## 6. Ruled out (so nobody re-checks)

- GPU: `GPU Process Memory\Local Usage` for the pid sat at 360–390 MB and did not climb at idle; `DiagResourceTotals`
  is only wired under `FG_MEM`/MemCensus (`AppHost.cs:492`, `FluentApp.cs:300`), not active for the run.
- No Application/System event-log entries (resource exhaustion, .NET runtime) in the window.
- Largest process on the box was Rider's Roslyn worker (~3.1 GB) — unrelated.
- The remaining ~600 MB after the storm is consistent with GC/native heap segments retained after an allocation storm
  plus the ~360 MB GPU shared allocation; not a live growth.
- Other `IStore.Changes` subscribers that react to `IsBulk` — `RecentsPage.cs:293` (bails when parked/empty),
  `SettingsPage.VideoOverrides.cs:48`, `PlaybackBridge.cs:848`, `StoreLibrarySource.cs:58`, `Seam.cs:33`
  (`EngineMutationSource`) — none call a hydrating read per change; DetailPage is the only closer.
