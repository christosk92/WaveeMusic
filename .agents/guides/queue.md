---
guide: queue
scope: Wavee's playback queue subsystem — the in-memory queue model (three buckets, cursor, shuffle, repeat, pagination signal), every orchestrator queue mutation, the autoplay handoff, the cluster-sync layer (PutState prev/next + QueueRevision), the UI bridge (`PlaybackStateService.RawNextQueue`), the right-panel `QueueControl`, drag-and-drop targets, context-menu affordances, and the SetQueue / AddToQueue dealer commands.
last_verified: 2026-05-21
verified_by: read+grep over src/Wavee/Audio/Queue, src/Wavee/Audio/PlaybackOrchestrator.cs, src/Wavee/Connect (PlaybackStateManager, ConnectCommandHandler, QueueCommands), src/Wavee.UI/Contracts, src/Wavee.UI.WinUI/Data/Contexts (PlaybackService, PlaybackStateService, ConnectCommandExecutor), src/Wavee.UI.WinUI/Controls/Queue, src/Wavee.UI.WinUI/Controls/RightPanel, src/Wavee.UI.Services/DragDrop/Handlers, src/Wavee.UI.WinUI/Controls/ContextMenu/Builders
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Queue Inventory

This guide is for agents changing anything in the queue subsystem — adding /
removing buckets, changing draining order, shuffle / repeat semantics,
pagination thresholds, autoplay rollover, prev/next emission to the Connect
cluster, the right-panel queue UI, drag-drop targets, or the context-menu
"Play next" / "Add to queue" affordances. Use it to answer "where does
PlayNext insert?", "why is the user queue draining before the post-context
bucket?", "how does the cluster see our queue?", "what does provider=autoplay
mean?", "where do I add a new queue command?" without grepping across three
projects.

Queue state lives **exclusively in the UI process** inside
`PlaybackOrchestrator._queue`. AudioHost never sees the queue — it only
receives `play_resolved` / `prepare_next` commands for individual tracks
(see `.agents/guides/playback.md`). The cluster sees a flattened
`prev_tracks` / `next_tracks` snapshot inside every PutState (see
`.agents/guides/connect-state.md`).

Out of scope here:
- **Audio engine, decoders, processing chain, DSP, IPC** — see
  `.agents/guides/playback.md`. That guide briefly mentions queue in its
  "Queue + context" sub-section; this one is the deep-dive.
- **Dealer cluster ingestion, PutState envelope, device picker, transfer** —
  see `.agents/guides/connect-state.md`. Where this guide meets that one is
  `PlaybackStateManager.PublishLocalState` and the `BuildSetQueueBody` helper
  in `ConnectCommandExecutor` — both documented here from the queue side.
- **How individual track rows render** — see
  `.agents/guides/track-and-episode-ui.md`. `QueueControl` reuses the shared
  `TrackTemplate` from that guide.
- **Drag-drop infrastructure** (the wider `IDragDropService` + handler
  registry) — only the `EnqueueTracksHandler` is in scope here.

## How To Use This Guide

1. Skim the **Quick-find table** to locate the file you need.
2. Read **The three buckets** if you're changing draining order, "play next",
   "add to queue", or anything cursor-related — most footguns live there.
3. **State publishing chain** is the authoritative trace of how a mutation
   leaves the queue and reaches the cluster + UI. Read this before adding a
   new queue-mutating command — every command must end in
   `PublishQueueState()` or the cluster + UI will silently desync.
4. **Cluster sync** documents what crosses the PutState boundary and what
   doesn't — `provider="queue"` items get persisted; `_postContextQueue`
   items live only locally.
5. If you add / remove a bucket, a provider tag, a queue command, or a UI
   surface, update this file and bump `last_verified` in the frontmatter.

Useful re-verification commands:

```
rg -n "class PlaybackQueue|record QueueTrack|interface IQueueItem|record QueueDelimiter|record QueuePageMarker|record QueueStateSnapshot" src/Wavee/Audio/Queue
rg -n "_queue\.(SetTracks|SetContext|AppendTracks|UpdateContext|MoveNext|MovePrevious|SkipTo|PlayNext|EnqueueAfterContext|RemoveFromQueue|SetShuffle|Clear|GetPrevTracks|GetNextTracks|GetQueueRevision|GetSnapshot|EnrichByUri|ReplaceCurrent)\b" src/Wavee/Audio/PlaybackOrchestrator.cs
rg -n "PublishQueueState|NotifyStateChanged|NeedsMoreTracks|StateChanged" src/Wavee/Audio
rg -n "RawNextQueue|_rawNextQueue|AddToQueue|PlayNext|class QueueControl|class EnqueueTracksHandler|SetQueueCommand|AddToQueueCommand|BuildSetQueueBody" src/Wavee.UI.WinUI src/Wavee.UI/Contracts src/Wavee.UI.Services src/Wavee/Connect
```

## Quick-find Table

| Surface | Host file:line | DTO / Contract | Notes |
| --- | --- | --- | --- |
| Queue core | `src/Wavee/Audio/Queue/PlaybackQueue.cs` | three buckets + cursor + shuffle + repeat + signals | Single owner of queue state. ~1190 lines. Thread-safe via a single `_lock`. Emits `NeedsMoreTracks` + `StateChanged` observables. |
| Queue item record | `src/Wavee/Audio/Queue/QueueTrack.cs:23` | `QueueTrack` | Carries Uri/Uid/title/artist/album/duration/provider/`Metadata`. `IsAutoplay` => `Provider=="autoplay"`. |
| Queue item interface | `src/Wavee/Audio/Queue/IQueueItem.cs:7` | `IQueueItem` | Common shape for tracks + non-track markers. |
| Page marker (non-playable) | `src/Wavee/Audio/Queue/IQueueItem.cs:20` | `QueuePageMarker(PageNumber)` | URI `spotify:meta:page:<n>`; UID `page<n>_0`. Pagination boundary in autoplay-style infinite contexts. |
| Delimiter (non-playable) | `src/Wavee/Audio/Queue/IQueueItem.cs:31` | `QueueDelimiter(AdvanceAction, SkipAction)` | URI `spotify:delimiter`; UID `delimiter0`. Drives end-of-queue pause vs continue. |
| Queue state snapshot | `src/Wavee/Audio/Queue/PlaybackQueue.cs:1184` | `QueueStateSnapshot` | `(Current, CurrentIndex, LoadedCount, IsShuffled, IsInfinite, UpcomingTracks, UserQueueTracks)`. Emitted by `StateChanged`. |
| Orchestrator queue ops | `src/Wavee/Audio/PlaybackOrchestrator.cs` | `PlayAsync`, `SwitchToContextAfterCurrentAsync`, `PlayNextAsync`, `EnqueueAsync`, `SetShuffleAsync`, `SetRepeatContextAsync`, `SetRepeatTrackAsync`, `OnTrackFinishedAsync`, `TryAdvanceOrAutoplayAsync`, `EndOfContextAsync`, `LoadMoreTracksAsync`, `TryTriggerAutoplayAsync`, `DoAutoplayFetchAsync`, `MaybeTriggerPrefetch`, `PublishQueueState` | All queue mutations go through methods on the orchestrator — never call `_queue` directly from outside. |
| Orchestrator metrics partial | `src/Wavee/Audio/PlaybackOrchestrator.Metrics.cs` | timings around queue transitions | Cross-link, doesn't own queue state. |
| Context resolver | `src/Wavee/Audio/ContextResolver.cs` | context URI → `IReadOnlyList<QueueTrack>` + `NextPageUrl` | Source of the initial bucket + every subsequent page. `FindTrackIndex` (line 355) used by orchestrator to position cursor on context switches. |
| Autoplay resolver | `src/Wavee/Audio/ContextResolver.cs` (`LoadAutoplayAsync` / `LoadRadioApolloAutoplayAsync`) | autoplay seed → station context | Called by `DoAutoplayFetchAsync`. Returns tracks tagged `provider="autoplay"`. |
| Connect command — set queue | `src/Wavee/Connect/Commands/QueueCommands.cs:9` | `SetQueueCommand(NextTracks)` | Inbound dealer command — replaces the entire upcoming queue. Currently parsed + emitted on `ConnectCommandHandler.SetQueueCommands` observable; no orchestrator subscriber yet. |
| Connect command — add to queue | `src/Wavee/Connect/Commands/QueueCommands.cs:38` | `AddToQueueCommand(TrackUri)` | Inbound dealer command — appends one track. Parsed + emitted; no orchestrator subscriber yet. |
| Connect command handler | `src/Wavee/Connect/ConnectCommandHandler.cs` | dispatcher | Acks the dealer request immediately; results land on observables. |
| PlaybackStateManager | `src/Wavee/Connect/PlaybackStateManager.cs` | cluster ↔ local | `OnClusterUpdate` parses inbound prev/next; `PublishLocalState` flattens queue into outbound `PutStateRequest`. Ghost-resume rebuilds from cluster prev/next when local engine is empty. |
| Engine local-state surface | `src/Wavee/Connect/IPlaybackEngine.cs:197+` | `LocalPlaybackState` | What the orchestrator emits per state change. Carries `PrevTracks` (≤16) + `NextTracks` (≤48) as `TrackReference` arrays + `QueueRevision`. |
| Track reference (lightweight) | `src/Wavee/Connect/IPlaybackEngine.cs:186` | `TrackReference(Uri, Uid, AlbumUri?, ArtistUri?, IsUserQueued)` | Shape of prev/next array entries in `LocalPlaybackState`. |
| Local play state DTO (UI) | `src/Wavee.UI/Models/QueueItem.cs:9` | `QueueItem` record | UI-side projection of `QueueTrack`. `QueueItem.FromQueueTrack(t)` is the conversion seam. |
| Playback context info | `src/Wavee.UI/Models/PlaybackContextInfo.cs` | `(ContextUri, Type, Name?, ImageUrl?, FormatAttributes?)` + `PlaybackContextType` enum | Used by the UI to identify the playing-from row. |
| Repeat enum | `src/Wavee.UI/Enums/RepeatMode.cs` | `Off`, `Context`, `Track` | UI enum; orchestrator splits into two booleans (`_repeatContext`, `_repeatTrack`) under mutual exclusion. |
| UI commands contract | `src/Wavee.UI/Contracts/IPlaybackService.cs` | `PlayNextAsync(trackUri)`, `AddToQueueAsync(trackUri)`, `SetShuffleAsync`, `SetRepeatModeAsync`, `SkipNextAsync`, `SkipPreviousAsync`, `SwitchToContextAfterCurrentAsync`, … | Framework-neutral; calling surface for view models. |
| UI state contract | `src/Wavee.UI/Contracts/IPlaybackStateService.cs` | `IsShuffle`, `RepeatMode`, `Queue`, `QueuePosition`, `CurrentContext`, `AddToQueue(string)`, `PlayNext(string)`, `LoadQueue`, `NotifyEndOfContext`, `DismissEndOfContext` | Bridges queue → INPC for view binding. Carries IsAtEndOfContext + the now-playing fields. |
| UI playback service | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackService.cs` | `IPlaybackService` impl + buffering / prompt orchestration | Routes commands through `ConnectCommandExecutor`. |
| UI state service (bridge) | `src/Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs` | `IPlaybackStateService` impl + INPC over the engine + cluster subjects | `_rawNextQueue` (line 59) is the canonical "what to render in the right panel" list — flat over all three buckets, with each item already typed (`QueueTrack` vs `QueueDelimiter`). `RawNextQueue` (line 177) exposes it. |
| Connect command executor | `src/Wavee.UI.WinUI/Data/Contexts/ConnectCommandExecutor.cs` | routes commands local vs remote | `PlayNextAsync` / `AddToQueueAsync` branch on target-device. Remote path goes through `BuildSetQueueBody` (line ~830) which materialises the librespot-style set_queue payload. |
| Right-panel queue tab host | `src/Wavee.UI.WinUI/Controls/RightPanel/QueueTabView.xaml` | thin shell | Hosts `<queue:QueueControl/>` inside the right-panel tab. |
| Right-panel tab pager | `src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelTabPager.xaml(.cs)` | tab registration | Queue is the first segment; default `SelectedMode=RightPanelMode.Queue`. |
| Queue UI control | `src/Wavee.UI.WinUI/Controls/Queue/QueueControl.xaml(.cs)` | five sections + pill toolbar + drag-drop targets | Now playing, playing-from, **Queue · n**, **Next up · n**, **Queued later · n**, **Autoplay**, plus a `QueueDelimiter` row + empty-state overlay. |
| Drag-into-queue handler | `src/Wavee.UI.Services/DragDrop/Handlers/EnqueueTracksHandler.cs` | drop target → enqueue calls | Shift modifier = Play next (insert reversed); no modifier = Add to queue. |
| Track context menu — Play next / Add to queue | `src/Wavee.UI.WinUI/Controls/ContextMenu/Builders/TrackContextMenuBuilder.cs` (Play next ~line 67, Add to queue ~line 83) | invocation seam | Defaults fall back to `IPlaybackStateService.PlayNext(track.Uri)` / `AddToQueue(track.Uri)`. Ctrl+Enter shortcut on Add to queue. |
| PlayerBar end-of-context hint | `src/Wavee.UI.WinUI/Controls/PlayerBar/PlayerBar.xaml` (~line 92) | visibility = `IsAtEndOfContext` | Inline bar with "You've reached the end of this queue — click Play to restart." Dismiss button calls `DismissEndOfContext`. |

## Core contracts

### `PlaybackQueue` — the canonical owner

`src/Wavee/Audio/Queue/PlaybackQueue.cs` is the only object that stores queue
state. Public surface (signatures abridged — re-confirm in code):

**Context bookkeeping**
- `SetContext(string contextUri, bool isInfinite, int? totalTracks = null)` (line 191) — sets the context URI + cardinality.
- `UpdateContext(string contextUri, bool isInfinite, int? totalTracks = null)` (line 213) — replaces the URI in place (used by the autoplay rollover so the cluster sees the new station URI).
- `Clear()` (line 353) — wipes all three buckets + cursor.

**Track loading**
- `SetTracks(IEnumerable<QueueTrack> tracks, int startIndex = 0)` (line 287) — replaces context tracks, parks the cursor at `startIndex`. Pass `-1` to park before track 0 — `MoveNext` then plays track 0 instead of skipping it. The cap math at line 293 is `Math.Min(startIndex, Math.Max(0, _contextTracks.Count - 1))`, which preserves `-1` as a sentinel.
- `AppendTracks(IEnumerable<QueueTrack> tracks)` (line 318) — used by pagination + autoplay; shuffle-aware insertion when `_isShuffled`.

**Navigation**
- `MoveNext() → QueueTrack?` (line 384) — drains user queue → context → post-context; returns null when nothing remains. Increments `_currentIndex` only when consuming a context item; user/post-context items go through `_currentNonContextTrack`.
- `MovePrevious() → QueueTrack?` (line 444) — context-only; user-queue / post-context items don't add to "previous" history.
- `SkipTo(int index) → QueueTrack?` (line 473) — parks cursor at an absolute context index (validated).

**User mutations**
- `PlayNext(QueueTrack track)` (line 702) — insert at head of user queue. UID becomes `q{_queueUidCounter++}`.
- `EnqueueAfterContext(QueueTrack track)` (line 725) — append to post-context. UID becomes `p{_postContextUidCounter++}`.
- `RemoveFromQueue(int index) → bool` (line 745) — for "X" buttons on queue rows.
- `ReorderWithinBucket(QueueReorderTarget target, int oldIndex, int newIndex) → bool` (line ~762) — drag-reorder one item within a single bucket. `UserQueue` / `PostContextQueue` move within those lists; `ContextUpcoming` moves within the upcoming context tail and is shuffle-aware (permutes `_shuffledIndices` when shuffled, leaving `_contextTracks` intact), bounded so played / current tracks are never touched. `q#` / `p#` UIDs and `Provider` ride with the moved record. `QueueReorderTarget` enum lives in `src/Wavee/Audio/Queue/QueueReorderTarget.cs`.
- `AddToQueue(QueueTrack track)` (line 680) — **legacy alias**; new callers should use `PlayNext` / `EnqueueAfterContext` directly. If you touch this, prefer deletion over keeping two names per memory `feedback_no_legacy_shims`.

**Lookup**
- `FindIndexByUri(string uri) → int` (line 600).
- `FindIndexByUid(string uid) → int` (line 636).

**Update-in-place**
- `EnrichByUri(string uri, Func<QueueTrack, QueueTrack> updater) → int` (line 535) — TMDB-style metadata back-fill across all three buckets; preserves `Uid` / `IsUserQueued` / `Provider`. Emits `StateChanged` if anything changed.
- `ReplaceCurrent(QueueTrack track) → bool` (line 578) — swap the current item.

**Shuffle**
- `SetShuffle(bool enabled)` (line 770) — toggle and regenerate `_shuffledIndices`. Anchors the current track at logical index 0 (line 831).

**Cluster-export helpers** (cluster guide owns the consumers, see "Cluster sync" below)
- `GetPrevTracks()` (line 1028) — context history up to ~16 items.
- `GetNextTracks()` (line 1040) — flat upcoming list: user queue → context tail → post-context; cap drives PutState `next_tracks` (≤48).
- `GetQueueRevision() → string` (line 1107) — FNV-1a hash over next-track URIs; the cluster uses this as a change-detection token so a no-op re-publish doesn't churn UI everywhere.
- `GetSnapshot() → QueueStateSnapshot` (line 1142) — debug / test surface.

**Observables**
- `NeedsMoreTracks: IObservable<Unit>` (line 174) — fired once when remaining-in-context ≤5 (line 911 threshold). Re-armed by `SetTracks` / `AppendTracks` / `UpdateContext`.
- `StateChanged: IObservable<QueueStateSnapshot>` (line 179) — fires on every mutation; `QueueControl` does *not* bind to this directly (it goes through `PlaybackStateService._rawNextQueue` instead — see UI bridge below).

**Disposal**
- `Dispose()` (line 1162) — completes the subjects.

### `QueueTrack`

`src/Wavee/Audio/Queue/QueueTrack.cs:23`:

```csharp
public record QueueTrack(
    string Uri,
    string? Uid = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumUri = null,
    string? ArtistUri = null,
    int? DurationMs = null,
    long? AddedAt = null,
    bool IsPlayable = true,
    bool IsExplicit = false,
    bool IsUserQueued = false,
    string Provider = "context",
    string? ImageUrl = null,
    bool IsPostContext = false
) : IQueueItem
{
    public bool IsTrack => true;
    public bool HasMetadata => !string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(Artist);
    public bool IsAutoplay => Provider == "autoplay";
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
```

- `Provider` is the **single source of truth** for "where did this come
  from" — `"context"` (the playlist / album we loaded), `"queue"` (the user
  pressed Play next / Add to queue), `"autoplay"` (the orchestrator's
  rollover from `DoAutoplayFetchAsync`). UI sections, queue revision, and
  the cluster's queue render all key off this.
- `IsPostContext` is a **local-only** flag (not surfaced to the cluster) —
  it's what lets `QueueControl` separate the "Next up" section from the
  "Queued later" section even though both are `provider="queue"`.
- `Metadata` is the freeform recommender bag from the playlist resolver —
  `item-score`, `decision_id`, `core:list_uid`, `PROBABLY_IN_*`. It rides
  straight through into the cluster's `ProvidedTrack.metadata`. Don't strip
  it.

### `IQueueItem` + markers

`src/Wavee/Audio/Queue/IQueueItem.cs:7`:

```csharp
public interface IQueueItem
{
    string Uri { get; }
    string? Uid { get; }
    string Provider { get; }
    bool IsTrack { get; }
}
```

Two non-track marker records share this interface:

| Marker | URI | UID | When emitted |
| --- | --- | --- | --- |
| `QueuePageMarker(PageNumber)` (line 20) | `spotify:meta:page:<n>` | `page<n>_0` | Reserved for paginated autoplay boundaries (the UI never renders these — they're filtered out). |
| `QueueDelimiter(AdvanceAction, SkipAction)` (line 31) | `spotify:delimiter` | `delimiter0` | End-of-queue signal. `AdvanceAction == "pause"` ⇒ "End of queue" row; otherwise "Queue continues…" — see `QueueControl` lines 256–261. |

`QueueStateSnapshot` (line 1184 of `PlaybackQueue.cs`) is the read-only
shape emitted by `StateChanged` and `GetSnapshot()`.

### `IPlaybackService` queue surface

`src/Wavee.UI/Contracts/IPlaybackService.cs`:

| Method | Notes |
| --- | --- |
| `PlayNextAsync(string trackUri, CancellationToken)` (~line 94) | Insert at head of user queue. |
| `ReorderQueueAsync(QueueReorderTarget, int oldIndex, int newIndex, CancellationToken)` | Drag-reorder one item within a bucket. Routes to `PlaybackOrchestrator.ReorderQueueAsync` → `_queue.ReorderWithinBucket` → `PublishQueueState()`. Local playback only — the executor returns `DeviceUnavailable` for a remote active device. |
| `SkipToQueueItemAsync(int upcomingIndex, CancellationToken)` | Skip playback to the upcoming next-tracks item at `upcomingIndex`. Routes to `PlaybackOrchestrator.SkipToUpcomingAsync` (advances the queue via `MoveNext` and plays). Local playback only. Used by the queue-row hover play button + context-menu Play. |
| `AddToQueueAsync(string trackUri, CancellationToken)` (~line 88) | Append to post-context bucket. |
| `SetShuffleAsync(bool enabled, CancellationToken)` (~line 76) | Toggle shuffle. |
| `SetRepeatModeAsync(RepeatMode mode, CancellationToken)` (~line 77) | Off / Context / Track. The executor splits the enum into the orchestrator's two booleans (mutually exclusive). |
| `SkipNextAsync`, `SkipPreviousAsync` (~line 70-71) | Both delegate to queue navigation. |
| `SwitchToContextAfterCurrentAsync(contextUri, currentTrackUri?, displayName?, ct)` (~line 59) | Context swap **without** interrupting the current track — engine keeps playing while the queue is re-seeded. See the radio path note below. |

### `IPlaybackStateService` queue surface

`src/Wavee.UI/Contracts/IPlaybackStateService.cs`:

| Member | Notes |
| --- | --- |
| `Queue: IReadOnlyList<QueueItem> { get; }` (~line 182) | The flat next-tracks projection. **Not** what `QueueControl` actually renders — see "UI bridge" below. |
| `QueuePosition: int { get; }` (~line 187) | Current cursor (mirrors `_queue.CurrentIndex`). |
| `IsShuffle: bool { get; }` (~line 169) | |
| `RepeatMode: RepeatMode { get; }` (~line 170) | |
| `CurrentContext: PlaybackContextInfo? { get; }` (~line 177) | Drives the "playing from" card. |
| `IsAtEndOfContext: bool { get; }` (~line 141 impl) | PlayerBar inline hint visibility. |
| `AddToQueue(string trackId)` / `AddToQueue(IEnumerable<string>)` | Sync sugar; calls into `IPlaybackService.AddToQueueAsync`. |
| `PlayNext(string trackId)` / `PlayNext(IEnumerable<string>)` | The multi-arg overload iterates in **reverse** so visible order matches payload order — head-insert pushes earlier items down. |
| `LoadQueue(IReadOnlyList<QueueItem>, PlaybackContextInfo, int startIndex)` | Used by surfaces that own their own pre-built tracklist (search, recents). |
| `NotifyEndOfContext()` / `DismissEndOfContext()` | Hooks for the PlayerBar inline hint. |

## The three buckets

The orchestrator's queue carries **three concurrent track lists**, drained
in a strict order:

```
┌─────────────────────────────────────────────────────────────────────┐
│ _userQueue       (Play Next inserts; UID "q0", "q1", …)             │ ← drained FIRST
├─────────────────────────────────────────────────────────────────────┤
│ _contextTracks   (the loaded playlist/album/artist/station)         │ ← drained as the
│                  + the autoplay rollover (Provider="autoplay")        cursor advances
├─────────────────────────────────────────────────────────────────────┤
│ _postContextQueue (Add to Queue appends; UID "p0", "p1", …)         │ ← drained LAST,
└─────────────────────────────────────────────────────────────────────┘    before autoplay
                                                                           kicks in
```

`MoveNext` (line 384) drains in that order. `MovePrevious` (line 444) only
walks `_contextTracks` — user-queued and post-context items are play-once.

### The cursor: `_currentIndex` + `_currentNonContextTrack`

Two fields together describe "what's playing":

- `_currentIndex` — logical index into `_contextTracks` (shuffle-aware via
  `_shuffledIndices`). Initial value: `-1` (line 31). `MoveNext` on context
  increments it; popping from a non-context bucket does *not* touch it.
- `_currentNonContextTrack` — set when the live current item is from
  `_userQueue` or `_postContextQueue`. Cleared when the cursor moves back
  into context. The `Current` property returns this when set (not the
  context track at `_currentIndex`).

This split exists so that a `MoveNext` consuming a Play-next pops the
inserted item *without* moving the context cursor — the next `MoveNext`
resumes context from the position you were already at, not one position
forward.

### `startIndex = -1` is a real sentinel

`PlaybackQueue.SetTracks` (line 287) caps with
`_currentIndex = Math.Min(startIndex, Math.Max(0, _contextTracks.Count - 1))`,
which **preserves a passed-in `-1`**. The first `MoveNext` then advances to
0 and plays track 0.

This is the lever the radio path in
`PlaybackOrchestrator.SwitchToContextAfterCurrentAsync` (line 712) uses to
distinguish:

- **Song-radio** (the currently playing track *is* in the new context, at
  radio[0]) — cursor parks on its found index; `MoveNext` advances past it.
- **Album / artist radio** (currently playing track is *not* in the new
  context) — cursor parks at `-1`; `MoveNext` plays radio[0] instead of
  silently skipping it.

If you add another "switch context, keep current track playing" caller, use
the same pattern: pass `currentTrackUri` and rely on the cursor sentinel
rather than guessing a start index.

### Provider tags + UID schemes

| Provider | UID | Where stamped | Purpose |
| --- | --- | --- | --- |
| `"context"` | from resolver (real Spotify UID or `null`) | `_contextTracks` (`SetTracks` / `AppendTracks`) | Default for everything the context resolver yields. |
| `"queue"` | `q<n>` for user queue, `p<n>` for post-context | `PlayNext` (line 702), `EnqueueAfterContext` (line 725) | Survives PutState — the cluster sees user-queued items even on remote devices. `IsUserQueued = true` on the record. |
| `"autoplay"` | from autoplay resolver | `DoAutoplayFetchAsync` → `AppendTracks` | Visible in `QueueControl`'s **Autoplay** section with the progressive opacity dim; cluster sees these as part of the next-tracks tail. |

Two UID counters (`_queueUidCounter`, `_postContextUidCounter`) drive the
`q#` / `p#` scheme — librespot convention. They're scoped to the queue
instance, not persisted.

### Context-URI stamping

`StampContextUri` (line 264, called by `SetTracks` and `AppendTracks`)
bakes the current `_contextUri` into each track's
`Metadata["context_uri"]` and `Metadata["entity_uri"]` at insertion time.
This means after the autoplay rollover **flips the context URI** via
`UpdateContext(stationUri, isInfinite: true)` (orchestrator line 1874), the
*original* context's tracks still carry their original origin in their
stamped metadata. The cluster sees a clean handoff: pre-station tracks
keep their album/playlist origin; post-station tracks point at the station.
Don't bypass `StampContextUri` when adding a new code path that mutates
the tracks list.

## Shuffle

Shuffle is owned by the queue, not the orchestrator. `SetShuffle(enabled)`
(line 770) regenerates `_shuffledIndices` (line 803, Fisher-Yates) and
anchors the *currently playing* track at logical index 0 (line 831). When
shuffle is disabled, the cursor is mapped back to its real position
(line 788, `_currentIndex = _shuffledIndices[_currentIndex]`). Navigation
methods read through `GetActualIndex` (line 892) when `_isShuffled`.

`AppendTracks` while shuffled (lines ~331-340 in `PlaybackQueue.cs`)
inserts new tracks at random positions *after* the current cursor — the
already-played stretch stays stable so prev-track history isn't disrupted.

## Repeat

Repeat lives on the orchestrator (`PlaybackOrchestrator.cs`), not the
queue:

- `_repeatContext: bool` (~line 35)
- `_repeatTrack: bool` (~line 36)
- `SetRepeatContextAsync(bool)` (line 647) — toggling on clears
  `_repeatTrack` (mutual exclusion).
- `SetRepeatTrackAsync(bool)` (line 655) — same, the other way around.

The two flags collapse to the UI's `RepeatMode` enum
(`Off` / `Context` / `Track`) — `PlaybackService.SetRepeatModeAsync` is
the splitter.

`OnTrackFinishedAsync` reads `_repeatTrack` first (replays current at 0
ms). `TryAdvanceOrAutoplayAsync` reads `_repeatContext` in Tier 1
(`SkipTo(0)` to loop the context). See "Advance ladder" below.

## NeedsMoreTracks + pagination

`PlaybackQueue.CheckNeedsMoreTracksInternal` (line 904) fires
`NeedsMoreTracks` once per context when the cursor is within **5 tracks**
of the end (line 911). The latch (`_needsMoreTracksRequested`) is cleared
by `SetTracks` / `AppendTracks` / `UpdateContext`, so each new page or
context cycle gets one fresh fire.

The orchestrator's constructor (`PlaybackOrchestrator.cs` ~line 183)
subscribes:

```csharp
_subs.Add(_queue.NeedsMoreTracks.Subscribe(_ => { _ = LoadMoreTracksAsync(); }));
```

`LoadMoreTracksAsync` (line 1710):
1. If `_currentNextPageUrl` is set → fetch next page from
   `ContextResolver`, then `_queue.AppendTracks(result.Tracks)` (line 1740).
2. Otherwise → call `TryTriggerAutoplayAsync()` (line 1749).

A `_loadMoreLock` semaphore guards against the queue firing twice during a
rapid skip burst.

When tuning the threshold (currently 5), watch out for two cliffs:
- Too low and a fast skip-burst can drain past the end before the fetch
  resolves (engine ends up paused on EndOfContext).
- Too high and we burn pagination on contexts the user is about to abandon
  by switching context.

## The advance ladder

When AudioHost emits `track_finished`, the orchestrator runs the
four-tier ladder in `TryAdvanceOrAutoplayAsync` (line 1606):

| Tier | Check | Action |
| --- | --- | --- |
| 0 | `_repeatTrack` true | Replay current at 0 ms (handled earlier in `OnTrackFinishedAsync` line 1583, before the ladder). |
| 0 | `_queue.MoveNext()` returns a track | Play it. Hits 99% of the time. |
| 1 | `_repeatContext` true | `_queue.SkipTo(0)` + play. Loops the loaded context. |
| 2 | A `_pendingAutoplayTask` is in flight | Await up to 3 s for it to land; play whatever it appended. |
| 3 | Autoplay hasn't been triggered yet | Call `TryTriggerAutoplayAsync()` synchronously and play the first appended track. |
| — | All four miss | Fall through to `EndOfContextAsync(ct)` (line 1682) — resets cursor to 0, stops the engine, emits `EndOfContextEvent`. |

`TryTriggerAutoplayAsync` (line 1808) gates on:
- `_autoplayTriggered` latch (once per context).
- `AutoplayEnabledProvider` callback (the user's setting).
- `_queue.HasPostContextItems` (line 1817) — **defers** autoplay if the
  user has "Add to queue" items waiting; those drain first, then autoplay
  fires.
- The current context isn't already an infinite station (don't autoplay
  off an autoplay context).

`DoAutoplayFetchAsync` (line 1841):
1. `_queue.GetRecentTrackUris(5)` — seeds the recommender.
2. Calls into `ContextResolver.LoadAutoplayAsync` or
   `LoadRadioApolloAutoplayAsync` depending on the route.
3. `_queue.UpdateContext(stationUri, isInfinite: true)` — context URI now
   points at the station; old `_contextTracks` keep their stamped
   `context_uri` so prev-history is preserved.
4. `_queue.AppendTracks(autoplay.Tracks)` — provider tag stays
   `"autoplay"` so the UI separates these into the dedicated section.

## State publishing chain

A queue mutation reaches both the local UI and the Connect cluster through
the orchestrator's `PublishQueueState` (`PlaybackOrchestrator.cs:2372`):

```
Caller (PlayNextAsync / EnqueueAsync / Set… / advance ladder / …)
        │
        ▼
_queue.<mutation>()                       (PlaybackQueue, under _lock)
        │
        ▼
PublishQueueState()                        (orchestrator)
        │
        ▼
OnProxyStateChanged(currentEngineState)    (line 1987)
        │
        ├── Enriches with queue snapshot:
        │     _queue.GetPrevTracks()                → ≤16 TrackReferences
        │     _queue.GetNextTracks()                → user queue + context + post-context
        │     _queue.GetQueueRevision()             → FNV-1a hash
        │     _queue.ContextUri / IsShuffled /
        │     CurrentIndex
        │
        ▼
_stateSubject.OnNext(enriched: LocalPlaybackState)
        │
        ├──────────────────────────────────────────────────────────┐
        ▼                                                          ▼
PlaybackStateService                                  PlaybackStateManager
  (UI bridge, INPC)                                   (Connect cluster sync)
  - updates CurrentTrackId/Title/...                  - decides if local is "active device"
  - updates _rawNextQueue                             - PublishLocalState(state) → PutStateRequest
  - OnPropertyChanged(nameof(RawNextQueue), …)        - debounces non-critical changes 750 ms
  - QueueControl rebinds                              - critical changes flush immediately
                                                      - submits to bounded queue (cap 10)
                                                      - worker calls SpClient.PutConnectStateAsync
```

Every queue-mutating orchestrator method **must** end in
`PublishQueueState()`; otherwise the cluster (and the right-panel UI) will
silently miss the change. Verify with a grep:
`rg -n "_queue\.(PlayNext|EnqueueAfterContext|SetShuffle|SkipTo|AppendTracks|UpdateContext|SetTracks|Clear|RemoveFromQueue|EnrichByUri|ReplaceCurrent)\b" src/Wavee/Audio/PlaybackOrchestrator.cs` — each hit should be followed by a publish call
in the same method.

### UI bridge: `_rawNextQueue`

`PlaybackStateService` holds **a parallel list** that `QueueControl` binds
to:

```csharp
private IReadOnlyList<Wavee.Audio.Queue.IQueueItem> _rawNextQueue = [];   // line 59
public  IReadOnlyList<Wavee.Audio.Queue.IQueueItem> RawNextQueue => _rawNextQueue; // line 177
```

It's repopulated from `state.NextQueue` whenever a new
`LocalPlaybackState` arrives (line 1107). `QueueControl` casts the bound
service to `PlaybackStateService` and reads this list directly — it does
*not* go through `IPlaybackStateService.Queue` (which is the older
`IReadOnlyList<QueueItem>` projection kept for legacy callers and tests).

`RawNextQueue` keeps the items as `IQueueItem` so the UI can switch on
`QueueTrack` vs `QueueDelimiter` and route to the right template.
`EnrichByUri` (and any other in-place mutation) re-publishes the whole
list via `OnPropertyChanged(nameof(RawNextQueue))` so the UI sees the
update.

## Cluster sync

`PlaybackStateManager` (`src/Wavee/Connect/PlaybackStateManager.cs`) owns
the cluster ↔ local handshake. From the queue's perspective there are two
edges:

### Outbound — `PublishLocalState`

`PublishLocalState(state)` (~line 951) builds a `PutStateRequest` from the
enriched `LocalPlaybackState`. The queue's contribution lands as:

- `prev_tracks[]` — `_queue.GetPrevTracks()` capped at 16.
- `next_tracks[]` — `_queue.GetNextTracks()` capped at 48. Order:
  - User queue (provider="queue", UID `q#`).
  - Remaining context tracks from `_currentIndex + 1`.
  - Post-context queue (also provider="queue", UID `p#`).
  - (No `QueuePageMarker` / `QueueDelimiter` is emitted across the wire;
    they're UI-only constructs.)
- `queue_revision` — `_queue.GetQueueRevision()` (FNV-1a over upcoming
  URIs).

The whole request is **deep-cloned** (~line 1041) before being submitted
to the bounded publisher queue (cap 10, oldest dropped). Don't skip the
clone — it freezes the snapshot against further queue mutations between
submit and HTTP-fire.

Debouncing (lines 899–946): the manager flushes Track / Status / Device /
Context changes immediately; queue mutations are debounced 750 ms so a
rapid Play-next burst collapses into one PutState.

### Inbound — `OnClusterUpdate`

When another device (or our own previous instance) publishes a cluster
update, `OnClusterUpdate` (~line 464) parses `PrevTracks` /
`NextTracks` / `Track` / `CurrentIndex` out of the proto. If we're not the
active device, this drives the **read-only** view in `PlaybackStateService`
— the local `PlaybackQueue` is *not* mutated; we just render what the
cluster says.

When we *are* the active device, cluster updates are no-ops (we sent
them); the manager guards against this at ~line 548 so an inbound echo
doesn't fight our own queue mutations.

Ghost-resume (~lines 250-295): when the local engine is empty but the
cluster says playback is in progress (e.g. resumed on this device after a
restart), the manager reconstructs `PageTrack`s from the cluster's
prev/next snapshots — tagging each with the right `provider` based on
`isUserQueued` / context-infinite-ness — and calls
`engine.PlayAsync(reconstructedCommand)`. This is the **only** way the
local queue can be seeded from outside `PlayAsync` / `SwitchToContext…`.

### Inbound Connect commands — `SetQueue` / `AddToQueue`

`src/Wavee/Connect/Commands/QueueCommands.cs`:

```csharp
public sealed record SetQueueCommand(IReadOnlyList<TrackReference> NextTracks);  // line 9
public sealed record AddToQueueCommand(string TrackUri);                          // line 38
```

`ConnectCommandHandler.ProcessCommandAsync` (~line 240) parses the dealer
REQUEST, acks immediately with `Success=true`, and re-emits onto:
- `_setQueueCommands.OnNext(...)` — observable `SetQueueCommands`.
- `_addToQueueCommands.OnNext(...)` — observable `AddToQueueCommands`.

**As of `last_verified`, no orchestrator subscriber exists for these two
observables.** The librespot pattern is that remote queue commands flow
into `_queue.PlayNext` / `EnqueueAfterContext`; wire one up if you need
remote-control parity. The hookup point is the orchestrator constructor
(near the existing `NeedsMoreTracks` subscription).

### Outbound to a *remote* device — `BuildSetQueueBody`

When the active device is **not** local (we're a controller, not the
target), `ConnectCommandExecutor.PlayNextAsync` / `AddToQueueAsync`
(~lines 741-750) skip the local engine and build a librespot-style
`set_queue` payload via `BuildSetQueueBody` (~line 830). This:

1. Reads the current cluster state's `NextTracks`.
2. Filters to entries where `IsUserQueued == true` (provider `"queue"`).
3. Constructs the new entry with `uid = "q0"` and prepends (Play next) or
   appends (Add to queue).
4. Sends as a `set_queue` dealer command to the active device.

Remote devices don't see our local `_postContextQueue` separately — both
buckets collapse into provider="queue" at the wire. The remote receiver
makes its own decision about ordering.

## UI surfaces

### `QueueControl` (right-panel queue tab)

`src/Wavee.UI.WinUI/Controls/Queue/QueueControl.xaml(.cs)`. Five rendered
sections, in screen order:

1. **Now playing card** (XAML ~line 204; CS `Refresh` ~line 146). Binds to
   `IPlaybackStateService.CurrentTrack{Title,ArtistName,AlbumArt,Id}` +
   `IsPlaying` (the equalizer animation).
2. **Playing-from breadcrumb** (XAML ~line 160; CS ~line 268). Renders
   when `CurrentContext` is one of the navigable types (Playlist, Album,
   Artist, Show, Episode, LikedSongs). Click opens the context page.
3. **Queue · n** (XAML ~line 247; CS ~line 209). User-queue items
   filtered from `_rawNextQueue` where `IsUserQueued == true`. "Clear"
   hyperlink is a stub (no-op; flagged for follow-up).
4. **Next up · n** (XAML ~line 276; CS ~line 221). Remaining context
   tracks: `_rawNextQueue` where `!IsPostContext && !IsUserQueued &&
   !IsAutoplay`.
5. **Queued later · n** (XAML ~line 291; CS ~line 233). Post-context
   bucket: `_rawNextQueue` where `IsPostContext == true`. Subhead "Plays
   after this context finishes."
6. **Autoplay** (XAML ~line 309; CS ~line 177). `Provider == "autoplay"`.
   Progressive opacity dim (`Math.Max(0.35, 0.90 - idx/6.0 * 0.55)`) so
   the section visually fades into infinity.
7. **Delimiter row** (XAML ~line 326; CS ~line 256). Renders if the queue
   carries a `QueueDelimiter`.
8. **Empty-state overlay** (XAML ~line 340). Shown when no current track
   and all four buckets are empty.

Each section is a `ListView` bound to a per-`Refresh` `List<QueueDisplayItem>`.
Rows use `QueueControl`'s own `TrackTemplate` — 40 × 40 art, a click-through
title (→ album) and artist (→ artist), the track duration, and a grip cue. Each
row is a drag source via `ManualDragAttachment` (attached in `TrackRoot_Loaded`)
— not `ListView.CanDragItems`, which the title/artist `HyperlinkButton`s would
swallow. The drag carries a real `TrackDragPayload`, so a queue row can be
dropped on a playlist (→ add tracks); the within-section reorder runs off
`_reorderSource`.

During the drag the shared `ReorderDropIndicator`
(`Controls/Reorder/ReorderDropIndicator.cs`, also used by `TrackDataGrid`) draws
a 2 px accent insertion line into the `DropIndicatorOverlay` Canvas — bright at
the slot under the pointer, faint at every other gap. `Section_Drop` →
`HandleReorderDrop` maps the resolved slot to a backend `(from, to)` and calls
`IPlaybackService.ReorderQueueAsync`; `QueueDisplayItem.ContextTailIndex` maps a
Next-up / Autoplay drop to an absolute context-tail index. The reorder is gated
on local playback in `Section_DragOver`/`_Drop` (`IsPlayingRemotely`). (There is
no "ghost gap" preview — the insertion line is the whole preview.)

Each row also has a **hover play button** over the 40×40 artwork
(`RowPlayButton`, shown by `TrackRow_PointerEntered`) and a **right-click
context menu** (`TrackRow_RightTapped`/`_Holding`). Both reuse shared pieces:
the menu is the standard `TrackContextMenuBuilder` fed a `QueueRowTrackItem`
(a lightweight `ITrackItem` adapter over `QueueDisplayItem`). Play / the menu's
Play both call `IPlaybackService.SkipToQueueItemAsync(QueueDisplayItem.QueueIndex)`
→ `PlaybackOrchestrator.SkipToUpcomingAsync` (advances the queue to that
next-tracks index and plays). `QueueIndex` is the flat 0-based position across
all four sections, assigned in `Refresh`.

Queue snapshots with zero next items are authoritative when they come from a
real cluster `PlayerState` or a local `LocalPlaybackState.QueueRevision`. Do
not preserve the previous queue just because `Count == 0` — that leaves stale
right-panel rows after standalone search plays and after the last queued item is
promoted to current.

**Toolbar pills** (XAML ~lines 95-150; CS `…Button_Click` handlers ~lines
420-509):
- Shuffle → `_playbackService.SetShuffleAsync(!IsShuffle)`.
- Repeat → cycles `Off → Context → Track → Off`.
- Autoplay (∞) → toggles `ISettingsService.Settings.AutoplayEnabled`,
  broadcasts `AutoplayEnabledChangedMessage`.
- Crossfade → currently a visual stub (no-op).

The queue toolbar uses regular `ToggleButton`s with their checked background
state intact. This is intentional: the foreground-only active treatment belongs
to the player bar transport controls, not this right-panel queue toolbar.
`Refresh` sets `IsChecked` on each queue toolbar button. Repeat is optimistic in
`QueueControl` (`_repeatVisualMode`) and local repeat updates go through
`PlaybackOrchestrator.SetRepeatModeAsync(bool repeatContext, bool repeatTrack)`
so Track → Off is one atomic flag update. Remote repeat sends both
`set_repeating_track` and `set_repeating_context` explicitly; sending only
context-off leaves `_repeatTrack` stuck and maps straight back to
`RepeatMode.Track`.

**Drag-drop targets**: only the "Queue · n" section accepts drops
(`UserQueue_DragOver` / `_Drop`, ~lines 449-492). Modifier-aware caption
("Play next" on Shift, "Add to queue" otherwise). Drop result toasts
through `INotificationService.Show` (~line 488) — 3 s, Informational on
success.

### `EnqueueTracksHandler` (drag-drop)

`src/Wavee.UI.Services/DragDrop/Handlers/EnqueueTracksHandler.cs`:

- `Shift` held → calls `PlayNextAsync(uri)` for each URI **in reverse
  order** so the visible top of the drop becomes the next-up track (~line
  29).
- No modifier → calls `AddToQueueAsync(uri)` sequentially (~line 38).
- Either way, the result message is rendered through the toast service
  (the drag-drop registry's standard return shape).

The drop target also accepts albums / playlists / artists — those resolve
to track lists via the same handler chain (registered up the stack, see
the drag-drop module).

### Track context menu — "Play next" / "Add to queue"

`src/Wavee.UI.WinUI/Controls/ContextMenu/Builders/TrackContextMenuBuilder.cs`:

- **Play next** (~line 67) — defaults to `IPlaybackStateService.PlayNext(track.Uri)` when no explicit command is bound.
- **Add to queue** (~line 83) — defaults to
  `IPlaybackStateService.AddToQueue(track.Uri)`. Ctrl+Enter keyboard
  shortcut.

Both are primary rows with accent icons (orange Wavee accent). Available
on every TrackItem / DataGrid row via the shared right-click pathway —
see `.agents/guides/track-and-episode-ui.md` for the row builder hookup.

### PlayerBar — end-of-context inline hint

`src/Wavee.UI.WinUI/Controls/PlayerBar/PlayerBar.xaml` (~line 92). Bound
visibility = `ViewModel.IsAtEndOfContext`. Source of the flag: the
orchestrator's `EndOfContextAsync` (line 1682) fires `EndOfContextEvent`,
which the WinUI app forwards into
`IPlaybackStateService.NotifyEndOfContext()`. Auto-clears on the next
Play / SkipNext; manually clearable via the bar's dismiss button.

## Mutation entry points (where queue changes start)

| Source | Call into | Lands at |
| --- | --- | --- |
| Track row "Play next" | `IPlaybackStateService.PlayNext(uri)` | `PlaybackOrchestrator.PlayNextAsync` → `_queue.PlayNext` |
| Track row "Add to queue" | `IPlaybackStateService.AddToQueue(uri)` | `PlaybackOrchestrator.EnqueueAsync` → `_queue.EnqueueAfterContext` |
| Right-panel drag-drop (no modifier) | `EnqueueTracksHandler` → `IPlaybackService.AddToQueueAsync` | same as above |
| Right-panel drag-drop (Shift) | `EnqueueTracksHandler` → `IPlaybackService.PlayNextAsync` | same as Play next |
| Right-panel queue row drag-reorder | `IPlaybackService.ReorderQueueAsync` | `PlaybackOrchestrator.ReorderQueueAsync` → `_queue.ReorderWithinBucket` (local only) |
| Shuffle pill | `IPlaybackService.SetShuffleAsync(b)` | `PlaybackOrchestrator.SetShuffleAsync` → `_queue.SetShuffle` |
| Repeat pill | `IPlaybackService.SetRepeatModeAsync(mode)` | orchestrator splits into `SetRepeatContextAsync` / `SetRepeatTrackAsync` (flags only — no queue mutation) |
| Album / artist / track "Start radio" | `IPlaybackStateService.StartRadioAsync(seedUri, …)` | resolves a radio playlist via SpClient, then routes through `SwitchToContextAfterCurrentAsync` (current track playing) or `PlayContextAsync` (cold start) — see the "Radio path" note below. |
| Track-end / SkipNext | `IPlaybackService.SkipNextAsync` or AudioHost `track_finished` event | `OnTrackFinishedAsync` → `TryAdvanceOrAutoplayAsync` ladder |
| SkipPrevious | `IPlaybackService.SkipPreviousAsync` | `_queue.MovePrevious` |
| Inbound remote `set_queue` / `add_to_queue` | `ConnectCommandHandler` observables | **no orchestrator subscriber yet** — wire up if needed |
| Inbound cluster update (we're not active device) | `PlaybackStateManager.OnClusterUpdate` | view-only — projects into `PlaybackStateService.RawNextQueue`; local `_queue` is untouched |
| Ghost resume (cluster says playing, local engine empty) | `PlaybackStateManager` (~lines 250-295) | rebuilds a `PlayCommand` and calls `PlaybackOrchestrator.PlayAsync` — only path that seeds the queue from the cluster |

### Radio path — special "switch context, don't interrupt"

`StartRadioAsync` (`PlaybackStateService.cs:1721`) is the public seam for
the album / artist / track-context-menu "Start radio" affordance. It
resolves the seed URI via `SpClient.GetInspiredByMixPlaylistAsync` and
then:

- If a track is currently playing →
  `IPlaybackService.SwitchToContextAfterCurrentAsync(playlistUri,
  currentTrackUri: <playing-track>, displayName)`. The orchestrator
  (`PlaybackOrchestrator.cs:689`) replaces the queue's context + tracks
  **without** stopping the audio engine. The cursor lands either on the
  found index of the currently playing track (so `MoveNext` advances
  past it — classic song radio: the seed track sat at radio[0], play it
  to the end, then radio[1] kicks in) or on `-1` (album / artist radio:
  current track isn't in the playlist, so the first `MoveNext` plays
  radio[0]).
- If nothing is playing →
  `IPlaybackService.PlayContextAsync(playlistUri, StartIndex=0)`.

The cursor sentinel (`-1` vs found index) is **the** lever that decides
whether radio[0] is skipped or played. If you add another "queue this
context to play after current" caller, follow the same pattern instead of
hardcoding a `StartIndex` — the orchestrator already handles "skip seed
only when seed is the currently playing track" automatically via
`MoveNext` semantics.

## Persistence

**The queue is not persisted across app restarts.** No SQLite migration,
no JSON snapshot, no setting. After a restart:

- If the cluster says playback is in progress, `PlaybackStateManager`
  ghost-resume reconstructs the queue from cluster prev/next.
- Otherwise the queue is empty until the user plays something.

Sign-out (`PlaybackStateService.TearDownRemoteState` ~line 227) clears
`_queue`, `_prevQueue`, and `_rawNextQueue`.

If you implement persistence, the natural seam is
`_queue.GetSnapshot()` ↔ `_queue.SetTracks` + `SetContext`. Don't try to
persist `_currentNonContextTrack` separately — restore it by re-inserting
the right entry into the right bucket and letting the natural `MoveNext`
flow promote it to current.

## Telemetry

No gabo / EventService dispatches in the queue paths as of
`last_verified`. If you add queue-action telemetry, the natural site is
the orchestrator entry methods (`PlayNextAsync`, `EnqueueAsync`,
`SetShuffleAsync`, …) so both UI and remote-issued mutations get
instrumented. Anchor every event to `_currentTrackId` /
`_originalContextUri` so the gabo context block doesn't get dropped by
anti-fraud — see `CLAUDE.md` "Telemetry (gabo events)" and memory
`reference_event_sender_proto_schemas` for the proto shapes.

## Tests

| File | Coverage |
| --- | --- |
| `test/Wavee.Tests/Connect/Commands/ConnectCommandHandlerTests.cs` | Verifies the `AddToQueueCommands` observable emits a parsed payload (line ~49). |
| `test/Wavee.Tests/Connect/PlaybackStateManagerTests.cs` | Cluster ingestion / publish; no queue-bucket-specific cases. |
| `test/Wavee.Tests/Helpers/PlaybackStateTestHelpers.cs` | Test fixtures for playback state. |

There is **no direct unit test for `PlaybackQueue` itself**. If you
change draining order, the cursor sentinel, the shuffle pivot, or the
NeedsMoreTracks threshold, add one — `PlaybackQueue` is plain C# with no
threading dependencies beyond its own `_lock`, so it's straightforward to
test in isolation.

## Framework split

| Assembly | Owns |
| --- | --- |
| `Wavee` | `PlaybackQueue`, `QueueTrack`, `IQueueItem`, `QueuePageMarker`, `QueueDelimiter`, `QueueStateSnapshot`, `PlaybackOrchestrator` (and its `.Metrics.cs` partial). `Connect/Commands/QueueCommands.cs`, `Connect/PlaybackStateManager.cs`, `Connect/ConnectCommandHandler.cs`. |
| `Wavee.UI` (framework-neutral) | `IPlaybackService` + `IPlaybackStateService` interfaces, `QueueItem` UI projection, `PlaybackContextInfo`, `RepeatMode` enum, `PlayContextOptions`. |
| `Wavee.UI.WinUI` | `PlaybackService`, `PlaybackStateService` (owns `_rawNextQueue`), `ConnectCommandExecutor` (owns `BuildSetQueueBody`), `QueueControl.xaml(.cs)`, `RightPanelTabPager` + `QueueTabView`. |
| `Wavee.UI.Services` | `EnqueueTracksHandler` (drag-drop). |
| `Wavee.Playback.Contracts` | **Nothing queue-related.** AudioHost doesn't model the queue — it only sees per-track `play_resolved` / `prepare_next` commands. |

When adding queue functionality:
- A new bucket / draining rule / UID scheme / provider tag → `Wavee` (`PlaybackQueue` + `QueueTrack`).
- A new orchestrator-level command (`PlayNextAsync`-shaped) → `Wavee` (`PlaybackOrchestrator`), then surface through `IPlaybackService` (`Wavee.UI`) and `PlaybackService` (`Wavee.UI.WinUI`).
- A new wire-level remote command → `Wavee/Connect/Commands/QueueCommands.cs`, register dispatch in `ConnectCommandHandler`, subscribe in the orchestrator constructor.
- A new UI section in the right panel → `QueueControl.xaml(.cs)` plus its `Refresh` filter rules.

## Change Guidance

**Adding a new bucket** (e.g. "saved for later"):
- Add the list + UID counter to `PlaybackQueue`.
- Decide draining position in `MoveNext` and how `GetNextTracks` orders it
  against the existing three buckets (which feeds PutState `next_tracks`).
- Decide whether it crosses the wire — does the cluster see it? If yes,
  pick a `Provider` value (and make sure remote devices interpret it
  sanely; new providers are silently dropped by older clients).
- Add a `QueueControl` section (or extend an existing one) with the right
  filter rule.
- Add a `PlaybackStateService.RawNextQueue` population branch.
- Re-run the publish grep above — your new bucket needs to come out of
  `PublishQueueState` everywhere its content can change.

**Changing draining order**:
- Edit `PlaybackQueue.MoveNext` (line 384). Re-check `GetNextTracks`
  (line 1040) — it must emit in the same order or the cluster's
  "what plays next" preview will diverge from local playback.
- Update the advance-ladder description above and the
  Per-area notes below.

**Tuning `NeedsMoreTracks` threshold**:
- `CheckNeedsMoreTracksInternal` (line 911) — currently `5`.
- Lower → fewer wasted pagination fetches when users abandon the context.
- Higher → smoother playback at the cost of bandwidth on abandoned
  contexts.
- Don't lower below 2 — a single skip-burst can sail past.

**Adding a queue command** (inbound from the dealer):
- Add the record to `Wavee/Connect/Commands/QueueCommands.cs`.
- Add a `Parse(...)` switch arm in `ConnectCommand.Parse` (in
  `ConnectCommandHandler` parsing path).
- Add an observable on `ConnectCommandHandler` and emit from
  `ProcessCommandAsync`.
- Subscribe in `PlaybackOrchestrator`'s constructor (next to the
  `NeedsMoreTracks` subscription) and call into the right `_queue.<…>`
  method.
- Add an outbound counterpart in `ConnectCommandExecutor` so we can
  *send* the command to remote devices when we're the controller.
- PutState already carries the resulting queue change — no additional
  outbound message needed.

**Persisting the queue across restarts**:
- The natural seam is `_queue.GetSnapshot()` for write,
  `SetContext` + `SetTracks(snapshot.UpcomingTracks)` + replay of
  `PlayNext` / `EnqueueAfterContext` for read.
- Don't serialize `_currentNonContextTrack` — recover it by ordering
  user-queue items first in the snapshot and re-issuing them as
  `PlayNext`.
- See "Persistence" above — current behavior is "no persistence", so be
  conscious you're adding a new state shape that future cluster updates
  will fight unless you guard against ghost-resume overwriting it.

**Adding a UI section in the right panel**:
- Extend `QueueControl.Refresh` (CS `~line 138`) with a new
  filter branch on `_rawNextQueue`.
- Add the XAML section block following the existing template (header +
  ItemsRepeater).
- If the section needs to be drag-droppable, follow the pattern at
  `UserQueue_DragOver` / `UserQueue_Drop` (~line 449) and register the
  appropriate `IDragDropHandler` route on the UI side.
- Verify the empty-state condition at `QueueControl.xaml` ~line 264
  still resolves correctly (no current track + all buckets empty + your
  new section empty).

**Hooking up remote `set_queue` / `add_to_queue`**:
- `ConnectCommandHandler.SetQueueCommands` and `AddToQueueCommands` are
  already emitted; just nobody listens.
- Subscribe in `PlaybackOrchestrator` constructor; route to
  `_queue.SetTracks` (set_queue, after building a `QueueTrack` list with
  `Provider="queue"`) or `_queue.PlayNext` / `EnqueueAfterContext`
  (add_to_queue).
- Re-publish via `PublishQueueState()` — the inbound command is *also* a
  source of truth that needs to round-trip back into the cluster.

## Keeping This Guide Current

If you change anything in the queue subsystem:
1. Update the relevant Quick-find row and the affected section.
2. Re-run the re-verification commands at the top — each should produce
   ≥1 hit. Zero-hit lines mean a method was renamed and the guide is
   stale.
3. Update `last_verified` in the frontmatter.
4. If a surface moves between projects (e.g. `EnqueueTracksHandler`
   migrates out of `Wavee.UI.Services`), update the **Framework split**
   table.
5. If the user-visible right-panel layout changes, re-screenshot the
   "Five rendered sections" list in `QueueControl`.
