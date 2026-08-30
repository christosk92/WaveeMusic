# Wavee session restore

Reopen where you left off: the last nav stack **and** the playback session round-trip through `%LOCALAPPDATA%\Wavee\WaveeMusic\session.json`, beside `history.json` / `play-log.json`.

Source: `src/apps/Wavee/App/SessionSnapshotStore.cs`. Shell wiring: `src/apps/Wavee/Features/Shell/WaveeShell.cs`. Playback writer/reader: `src/apps/Wavee/App/PlaybackBridge.cs` + `src/apps/Wavee/Backend/PlaybackController.cs`. Audio hand-off (a different concern): [audio-handoff.md](audio-handoff.md). Deep-link verbs: [deep-linking.md](deep-linking.md).

**The one rule that governs everything here: a launch never starts music.** Every restore path — cluster, snapshot, transfer — lands *paused*. The snapshot is even written with `paused: true` unconditionally, so a crash mid-song relaunches silent.

## Snapshot schema (v1)

JSON, camelCase, nulls omitted. `version` is `1`.

```json
{
  "version": 1,
  "nav": {
    "active": { "name": "album:spotify:album:…", "arg": "Random Access Memories" },
    "back": [ { "name": "home" }, { "name": "search", "arg": "daft" } ],
    "forward": [],
    "activeTabId": 3
  },
  "playback": {
    "contextUri": "spotify:album:…",
    "contextKind": "album",
    "trackUri": "spotify:track:…",
    "trackUid": "…",
    "trackIndex": 3,
    "positionMs": 123456,
    "paused": false,
    "shuffle": true,
    "repeatMode": "context",
    "userQueueUris": ["spotify:track:…"],
    "autoplayActive": false,
    "capturedAtUnixMs": 1700000000000
  }
}
```

| Section | Fields | Notes |
|---|---|---|
| `nav.active` | `name`, optional `arg` | Opaque Wavee route key (`home`, `search`, `album:<uri>`, `pl:<uri>`, …) — same pair as `FluentGpu.Controls.Route`. |
| `nav.back` / `nav.forward` | route arrays, **oldest first** | Cap **50**. Overflow drops the oldest (FIFO). In-memory shell stacks stay at 200. |
| `nav.activeTabId` | `OpenTab.Id` | Process-lifetime int. On cold start IDs are reassigned by pinned-tab restore; a miss keeps the pinned selection and still applies the route to the selected tab. |
| `playback` | see JSON | **Optional / nullable.** `repeatMode` is a string (`off` / `context` / `track`). `contextKind` is a string (`album` / `playlist` / …). `paused` is always written `true`. `trackIndex` is the context cursor (-1 = the current stood outside the context spine). |

A missing `playback` object is not a fault. Nav-only saves must not wipe a previously persisted playback section (the store keeps the last loaded/written playback DTO in memory).

**Deliberately NOT persisted:** History (it rebuilds from the cluster's `prev_tracks`, or stays empty and Previous restarts the track), and the full Upcoming spine (it heals from the context uri). The user queue is the one set the cluster cannot supply when it is empty, which is why only *its* uris are written.

## Write gates

| API | When | Debounce |
|---|---|---|
| `UpdateNav(route, back, forward, tabId)` | `WaveeShell.Go` / `Back` / `Forward` **after** their existing bookkeeping (`CaptureNav`). Pass the live lists — the store copies into reused 50-slot buffers (zero alloc on the call after warmup). | 2 s (`SaveDebounceMs`), coalesced. |
| `UpdatePlayback(dto)` | `PlaybackBridge.MaybeWritePlaybackSnapshot` on **track boundary**, **pause edge**, or **queue-content change** — never a position tick. Gated on `PlaybackController.HasLocalSession` so a viewer-mode push cannot overwrite the persisted local session. | Same 2 s debounce. |
| `Flush()` / `FlushActive()` | `Program.cs` after the app run returns (process exit), `PowerBridge.OnSuspendingUi` (OS suspend), and shell unmount. **Synchronous** best-effort (cancel debounce, fsync, tmp→replace on the caller thread). No-op when nothing is dirty — a corrupt file is never replaced by an empty document. | none |

Atomic write: serialize → `session.json.tmp` → `Flush(true)` → `File.Move(..., overwrite: true)`. UI thread snapshots; the timer path writes the frozen DTO on the pool (PlayLogStore contract). `Flush` writes on the caller.

The shell's unmount `UseEffect` does **not** run on a normal window close (`AppHost.Dispose` never unmounts the tree), which is why `Program.cs` owns the process-exit flush.

## Restore order

Normal windowed boot only (probes / `--screenshot` skip the shell path as today):

1. **Settings** — `IAppSettings` is already loaded when `WaveeShell` is constructed.
2. **Pinned tabs** — `RestorePinnedWorkspace()` (ordinary tabs are session-only; pins are the cross-session subset).
3. **Session nav** — `RestoreSessionNav()`: `Load()` then `TryApplyNav` onto `_history` / `_forwardHistory` / `_route` / `_canBack` / `_canForward`, and the matching tab if `activeTabId` still exists.
4. **History log** — `HistoryStore.LoadFromDisk` + `Add` of the landing route (this session's first visit).
5. **Deep links** — first `Render` drains `DeepLinkChannel` (a cold-start `wavee://open` **wins** over the restored route).
6. **Playback** — independent of the shell, driven by the first cluster fold (below), not by a shell step.

Wrapped in try/catch: any restore failure falls back to `home` and empty stacks. Routes self-load from args, so a plain restore is the common path.

## Playback restore order (`PlaybackController`)

Recovery is scheduled from `OnProjectionChanged` (a cluster fold fires one) and runs on its own task — it takes the controller lock, never inside the projection callback. It arms once; an attempt that finds nothing to seed re-arms so a later fold (a reconnect that still has no local session) retries.

**Entry gate:** another device is active → we stay a viewer, nothing is seeded. Nobody active, *or* the cluster's `active_device_id` is a stale echo of us → recover.

1. **Cluster ghost-resume (server truth).** `ReplaceFromCluster` rebuilds all four buckets — Current from `player_state`, UserQueue from `provider:"queue"` **or** `metadata.is_queued:"true"`, Upcoming from the context+autoplay rows, and **History from `prev_tracks`** (oldest→newest, uids kept) so Previous works. Publishes **Paused** at the extrapolated position via `ApplyLocalSnapshot` — viewer-only, no wire announce (the PutState fan-out waits for a real Play). Then a **background heal** re-resolves the context to extend Upcoming and restore `_nextPageUrl` / the station continuation.
2. **Local snapshot (empty cluster).** `TryRestoreFromSnapshotAsync`: resolve the context, place the current via the ladder below, re-enqueue the user queue, apply shuffle/repeat, then `LoadAndPlayCurrentAsync(..., initiallyPaused: true)`.
3. **Idle.** Nothing to restore — a quiet no-op, not an empty player.

The first `Resume` after a *seeded-but-unloaded* session (`_restorePendingLoad`) goes through `LoadAndPlayCurrentAsync`, which buys fast-start, continuation prefetch and prepared-next. Restore is **audio-first** (`_restoreAudioFirst`): video restores placement only, never live playback.

### Identity ladder (`ContextResolve.ResolveRestoreIndex`)

One function, three callers (transfer, recovery heal, snapshot restore):

**uid → uri → saved index (in range *and* a playable row) → context head.**

- A **uid** beats a divergent saved index — an index is meaningless across a regenerated mix or an edited playlist (F2).
- The **head rung is opt-in only** (`allowContextHead`), and for a transfer it additionally requires that the sender named *no* current at all. `always_play_something` means "play something rather than nothing", never "prefer the head over the track the sender said is playing".
- A full miss **patches the saved current in outside the context spine** and parks the cursor at `savedIndex - 1`, so `Next()` advances to the successor of where it sat instead of wrapping to `context[0]`.
- A resolve/host failure on the restored current skips to `PreviewNext()` **once**, then surfaces `ReportPlaybackError` — never a silent dead player.

Episode rule, both paths: **the cluster/transfer position wins when > 0**; `EpisodeResumeMicros` is consulted only when it is 0.

## Fail-soft rules

`Load()` never throws.

| On-disk | Result | File | Subsequent writes |
|---|---|---|---|
| Missing | `null` (first run) | untouched | enabled; first `Update*` creates the file |
| Corrupt / unreadable / `version < 1` | `null` | **left in place** (not moved to `.corrupt`, not deleted) | enabled; first successful save **replaces** it |
| `version > 1` (too new) | `null` | left in place | **blocked** (`WritesBlocked`) — a newer build owns the document |

Do not rewrite a corrupt file from `Load`. Do not empty-Flush over a file that was never successfully adopted this process.

## Store API

```csharp
var store = new SessionSnapshotStore();          // optional IWaveeLog
store.Init(SessionSnapshotStore.DefaultPath());  // or a temp path in tests
SessionSnapshotDto? snap = store.Load();         // sync, fail-soft

store.UpdateNav(route, back, forward, tabId);    // internal; WaveeShell is the caller. Debounce 2 s.
store.UpdatePlayback(dto);                       // PlaybackBridge is the caller. Debounce 2 s.
store.Flush();                                   // sync shutdown / tests (SaveAndWait aliases this)

SessionSnapshotStore.TryApplyNav(snap?.Nav, back, forward, out Route active, out int tabId);
```

Caps: `CurrentVersion = 1`, `MaxStack = 50`, `SaveDebounceMs = 2000`. AOT: `SessionSnapshotJsonCtx` in the same file.

## Deep-link consumer (shell)

`DeepLink.cs` + `DeepLinkChannel` already exist. WaveeShell drains on `Pending` (monotonic `Signal<int>`):

| Verb | Handling |
|---|---|
| `open` | Compose the opaque nav key (`album` + `spotify:album:…` → `album:spotify:album:…`; a full key is passed through) then `GoNav`. |
| `play` | `HandleDeepLinkPlayback` → `PlaybackBridge.Player.PlayAsync(ctx)`. |
| `resume` | `HandleDeepLinkPlayback` → `PlaybackBridge.Player.ResumeAsync()`. |

`HandleDeepLinkPlayback` is the marked playback hook. It no-ops when the playback context is not mounted yet (the effect is registered after `UseContext(PlaybackBridge.Slot)`). Parser / intake contract: [deep-linking.md](deep-linking.md).
