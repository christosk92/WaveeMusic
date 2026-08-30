# Surfaces — one placement system for video, lyrics, queue, now‑playing, and everything after them

**Status:** design, decided. Supersedes the bespoke video-placement wiring (`VideoPlacement`, `VideoPlacementLogic`, `VideoPlacementHost`, `InWindowVideoPip`) and the bespoke rail wiring (`ShellUi.RailOpen/Mode/Toggle/RailFits`).
**Author:** lead architect / UX.
**Grounded in current `main` @ `3893b056`** — every file and line reference below was read, not recalled.

> ### Amendment — 2026-08-19: **Docked becomes the video default; Fullscreen is honored; two new declared rules**
>
> The docked-video plan (the accompanying implementation plan and its two Mica mockups) lands this spec's
> **M5-for-video** and **M6** directly on the app-side `PlacementCore`, ahead of M3's generic engine `SurfaceHost`
> primitive. That ordering is deliberate, not a shortcut: the pure core is deliberately app-local but has ZERO app
> dependencies today (`src/apps/Wavee/App/PlacementCore.cs:187-190`), so the later extraction to
> `FluentGpu.Engine/Surfaces/PlacementCore.cs` is still mechanical. The cost is real and is named here plainly: video
> is now a SECOND bespoke surface arriving before the primitive this document wanted built first, so for one more
> milestone the rail and the video dock are still two hand-wired systems, not two descriptors over one host.
>
> **What changed.** `PlacementPolicy.Video` widens from `Floating | Detached` to all four placements
> (`PlacementCore.cs:45-46`), and `Default` moves from `Floating` to `Docked` — the least-committing placement that is
> actually **visible** (Docked reserves layout, Floating is a framed overlay; both are "no OS window", but Docked no
> longer bleeds over page content once the rail fits). **Existing users are unaffected**: only a `""`-stored
> preference (never placed video before) picks up the new default; a persisted `"floating"` string keeps opening the
> mini player exactly as before (`LoadPlacement` reads the stored name unchanged).
>
> **§2.1's placement table** — the `Video` row — changes from `— | default | ✅ | ✅` (Docked / Floating / Detached /
> Fullscreen) to **`default | ✅ | ✅ | ✅`**: Docked is now the default, and Fullscreen — reserved since this document
> was written — is honored. **§3.2**'s "First ever use = `DefaultPlacement`, which is the lowest-commitment placement
> the surface supports: **Floating for video**" is superseded: read it as **Docked for video**, Floating remaining
> the fallback the moment the rail does not fit (narrow window ⇒ `Docked ∉ Available` ⇒ `FirstAvailable` walks up to
> Floating, so the pre-existing behavior survives unchanged as the degraded path, not the default one). The
> `DefaultPlacement = SurfacePlacement.Floating` line in **§5.4**'s illustrative `Video` `SurfaceDescriptor` (the
> future M3 registration shape) carries the same correction if and when that registration is written.
>
> **Nothing is deleted this time.** Unlike 2026-07-26, this amendment only widens a policy and adds one pure
> transition; the per-content-dismiss machinery that amendment removed stays removed.
>
> **New rules worth pinning, not previously expressible:**
> - **`Demote`** — a new pure transition, distinct from both `OpenAt` (writes `Preferred`) and the sticky-off
>   `HostClosed` path (`PlacementCore.cs:348`). The rail closing while video is docked is an AMBIENT change, not the
>   user closing the feature: `Demote` moves `Requested` down the ladder (to `Floating`, or `None` if nothing is
>   available) while leaving `Preferred` untouched, so re-opening the rail re-docks. `OpenAt` and `HostClosed` are
>   unchanged.
> - **A docked card's own ✕ is still sticky off** — it is a user-initiated close, so it goes through the unchanged
>   `HostClosed` → non-Detached → `TurnOff` path this document's 2026-07-26 amendment already established. `Demote`
>   is for the rail taking the surface away from under the user, never for the user asking to close it.
> - **A trackless video body leaves the panel, not the rail.** When the current track has no video while the rail is
>   showing the video body, the video card unmounts (freeing the decode surface — video still holds a scarce
>   resource, so it does not get the lyrics treatment of simply going empty) but the **rail panel stays open**,
>   showing track meta / Up next plus a "No video for this song" empty state. This borrows the **lyrics** empty-state
>   precedent from this document's own §1.2 ("the panel stays and shows 'No lyrics for this song'"), and is a
>   deliberate, narrow departure from §1.2's blanket claim that "video hides entirely" — that claim continues to hold
>   for the whole PANEL surfacing decision (an unavailable video body does not force the rail open, and closing the
>   rail is unaffected); it just no longer holds for "the specific pixels the card occupies" once the rail is already
>   open in Video mode for other reasons.
>
> **A bug fix riding along, not a design change:** `LadderIndex(Fullscreen)` returned `Ladder.Length` — one past the
> end of `[Docked, Floating, Detached]` — so `FirstAvailable(Fullscreen, …)`'s downward walk started past the end of
> the ladder and, if the walk kept failing, continued upward into spawning a detached OS window nobody asked for. A
> momentarily-unavailable Fullscreen (no `InputHooks.WindowSetFullscreen`, headless, a detached child host) must
> never escalate to a new window; `FirstAvailable` now special-cases `Fullscreen` to walk the ladder from the top and
> fall all the way to `None` rather than up past Detached.
>
> This amendment supersedes §2.1's `Video` row, §3.2's default-placement sentence, and (contingently, on landing)
> §5.4's illustrative snippet. It does not touch §2.2–§2.5, §7, or §9's milestone descriptions beyond the M5/M6
> ordering note above.

> ### Amendment — 2026-07-26: **closing a surface is STICKY OFF; the per-content dismiss is deleted**
>
> The user reported the consequence of the rule this document designed: close the video, and the next song that has one
> re-opened it. Per-content dismissal was working exactly as specified (`DismissedGen == ContentGen`, expiring on the next
> `ContentChanged`) — the *rule* was wrong. "I closed the video" is a statement about the feature, not about the song.
>
> **New rule.** Every user-initiated close — the surface's own ✕, the picker's "turn off video", the primary's "switch to
> audio" — is `TurnOff`: `Requested = None`, globally and stickily. No later track re-opens the surface; only an explicit
> re-enable does (player-bar primary, placement picker, or the attach-a-video-and-reveal flow). The one exception is
> unchanged: closing the **detached** window still falls down the commitment ladder to the mini player, because the user
> is still watching — closing *that* is then the close that turns video off.
>
> **What was deleted, not merely bypassed** (`src/apps/Wavee/App/PlacementCore.cs`): `PlacementState.{ContentGen,
> DismissedGen, NotDismissed}`, `PlacementCore.{DismissForContent, Restore, ContentChanged}`, the `Dismiss`/`Restore`/
> `ContentChanged` command kinds, `PlaybackBridge.{DismissVideoForCurrentTrack, RestoreVideo}`, and the `ContentChanged`
> bump in `PlaybackBridge.PushState`. Keeping the fields would have left "off, but it will come back" expressible, which
> is the whole class of bug. A track change now touches placement state **only** through the availability recompute.
>
> Everything below that describes per-content dismissal — §2.2's `DismissForCurrentContent`/`Restore` intents, §2.3's
> state shape, §2.4's `DismissForContent`/`ContentChanged` rows, §2.5's "dismissal is content-scoped by construction",
> §5's "Hide for this song" chrome tooltip, §7's per-content-dismiss persistence row, and §9's
> `ContentChanged_ExpiresDismissal` test — is **superseded by this amendment**. `PlacementCoreTests` carries the new rule
> (`Regression_ClosedVideoReopenedOnTheNextTrack` and the sticky-off section).

---

## 0. The verdict, in one page

The video feature is not sloppy. It is *bespoke*, and it is the **second** hand-rolled placement system in the app (the right rail is the first), plus there is a **third** hidden inside `MediaPlayerElement.ToggleFullscreen` (`src/FluentGpu.Controls/Media/MediaPlayerElement.cs:196-219`) that the bridge cannot even see. Three systems, three vocabularies, three answers to "what does clicking this button do", zero persistence. That is the "band aid on band aid" the user is feeling — not any single bug.

Four decisions frame everything below:

1. **One state value, not five signals.** Today placement is the *product* of `PreferVideo × CurrentTrackHasVideo × VideoPlacement × _dismissedForTrackGen × PopOutVideoSource` (`src/apps/Wavee/App/PlaybackBridge.cs:151-206`), and every consumer must recombine it correctly. It becomes **one** `readonly record struct PlacementState` behind **one** signal, with **one** reducer and **one** derived "where must this be mounted" memo. Placement is an *enum*, so "mounted in two places at once" — the thing that would double-pump the MF session — is unrepresentable.
2. **Intent and reality are separate fields.** `Requested`/`Preferred` (what the user wants) vs `Live` (where it actually is, written only by the host). Every observed pain — stuck toggle, no fallback on OS close — is intent and reality disagreeing with no place to say so.
3. **The primary click is the lowest-commitment action.** Today's primary spawns a brand-new always-on-top OS window (`PlayerBar.cs:500-510`). That is the *escalation*, and it belongs behind the chevron. First click = the in-window mini player.
4. **The surface never owns the player.** The single worst defect in the tree today is not a UX defect: `ShouldPlayAsVideo` / `LoadCurrentVideoAsync` are never assigned anywhere in `src/` (only in the `TODO(B-wire)` comment at `src/apps/Wavee/SpotifyLive/LiveConnect.cs:93-100`), so clicking "watch video" starts the music video's **own soundtrack on top of the still-playing song**, and every placement flip rebuilds a fresh `MediaPlayer` (`PopOutVideoStage`, `PopOutVideoWindow.cs:59-80`) so the video **restarts from 0**. Fix the ownership (backend host owns the player, surfaces only present it) and both defects die together.

---

## 1. The user story

### 1.1 Video

Mira is playing a playlist. A track with a music video starts. In the player bar a **movie glyph** appears in its neutral state — present, not lit — tooltip **"Watch video"**.

She clicks it once. Nothing steals focus and no taskbar entry appears. A small framed panel **grows out of the button** into the bottom-right of the window. It shows the album art, dimmed, with the track title and a thin progress ring — never a black rectangle — and within a second the video is playing. The page content below has shifted its bottom-right corner in by the panel's size, so the panel is not sitting on top of anything she was reading. The song does not double up: the audio she hears is the video's own soundtrack, because Wavee **switched media**, and the player bar's play/pause/seek now drive the video's timeline (which is longer than the song — there's a spoken intro, and the scrubber shows that honestly).

She wants it bigger and out of the way. She opens the chevron beside the glyph: **● Mini player** (radio-checked) · **○ Separate window** · **○ Full screen** · ─── · **Turn off video**. She picks **Separate window**. The panel shrinks away as a real OS window opens at 480×270, bottom-right of her monitor, always-on-top. Playback continues from where it was — no restart, no re-buffer from zero — because the player object lives in the backend, not in the panel; only the presenting surface changed.

She drags the window to her second monitor and makes it bigger. She alt-tabs away for twenty minutes; playback keeps advancing (the detached window is its own AppHost with its own render loop).

She closes that window with its own **✕**. She never said "turn video off", so video does not turn off — it **falls back one tier** to the in-window mini player, and the player-bar glyph stays lit, because the glyph's lit state and the surface's existence are literally the same value. There is no state in which the glyph is lit and nothing is on screen.

The track ends; the next track has no video. The mini player disappears by itself. Her *preference* is untouched — she never clicked "off". Three tracks later a video track comes on and the mini player reappears, in the mini-player placement, unasked.

She hits the mini player's own **✕**. Video hides *for this track only*; audio keeps playing. Next track with a video: it's back.

She clicks the glyph once more while it's lit. That always means **off**. Glyph unlit, sticky preference cleared. It does **not** forget that she likes video in a separate window on her second monitor.

Next morning she relaunches. The glyph is present and unlit — Wavee does not silently spin up a DRM/decode session she didn't ask for. She clicks it, and it opens **in a separate window, on the monitor, at the size she left it** — because *where she likes to work* was remembered even though *whether it was running* deliberately was not. Her second monitor is unplugged that afternoon; the window opens bottom-right of the monitor she still has, and that fallback position is *not* written back as her new preference.

### 1.2 The same story, lyrics

Mira clicks **Lyrics**. The right rail opens docked, taking real layout width. She clicks Lyrics again: it closes. She clicks **Queue** while Lyrics is open: Queue replaces Lyrics — one docked slot, one occupant.

She narrows the window until the rail no longer fits beside the sidebar and a usable content column. Lyrics does not vanish and does not squash the page: it **degrades one tier** to the floating panel, the same tier the video mini player uses, with the same frame, drag, and resize. That is not a special rail heuristic any more — it is the generic "your preferred placement is unavailable, use the fallback chain" path, the exact same code path as "this machine can't open a second swapchain, so Detached is unavailable".

She opens the chevron on the Lyrics button: **● Docked** · **○ Floating** · **○ Separate window** · **○ Full screen** · **Close**. She picks **Separate window** and parks lyrics on the second monitor for the rest of the evening. She never wrote a line of new feature code to get that; lyrics inherited it.

On an instrumental track lyrics do **not** pop shut — the panel stays and shows "No lyrics for this song". (Video hides entirely instead, because video holds a scarce resource and lyrics don't. That difference is one declared field, not a per-feature argument.)

Next launch: the rail comes back open on Queue, because docked panels are cheap workspace layout and restoring layout is what every IDE does. Video does not come back playing, because that's a running session. Same system, two policies, one declared field.

### 1.3 Why this reads as "designed"

Every surface in the app now answers the same five questions the same way: *where can I live · where do I want to live · where am I actually living · what happens when you click me · what do you remember about me*. The user learns the interaction once.

---

## 2. The generic model

### 2.1 Placements

```
None        mounted nowhere
Docked      inline in shell chrome; reserves real layout width (today's right rail)
Floating    in-window overlay panel: framed, draggable, resizable, pass-through elsewhere (today's PiP)
Detached    a second top-level OS window = its own AppHost + swapchain (today's pop-out)
Fullscreen  exclusive takeover of the main window (borderless), or of the detached window in place
```

Each surface **declares the subset it supports** (`PlacementSet Allowed`). The reducer can never park a surface outside its own allowed set, which is the invariant today's enum comment (`VideoPlacementModel.cs:3-7`) enforces by hand-written prohibition.

| Feature | Docked | Floating | Detached | Fullscreen |
|---|---|---|---|---|
| Video | — | **default** | ✅ | ✅ |
| Lyrics | **default** | ✅ | ✅ | ✅ |
| Queue | **default** | ✅ | ✅ | — |
| Now playing / details | **default** | — | ✅ | ✅ |
| Friends | **default** | — | — | — |
| Local-file video (future) | — | ✅ | ✅ | ✅ |
| Mini/compact player mode | — | — | ✅ (whole player shell) | — |

### 2.2 The one state value

```csharp
readonly record struct PlacementState(
    bool             Requested,     // sticky: the user wants this surface (survives content changes)
    SurfacePlacement Preferred,     // where they last CHOSE it (the persisted axis)
    SurfacePlacement ReturnTo,      // the non-fullscreen home, so Esc has somewhere to go
    SurfacePlacement Live,          // where it IS mounted. Written ONLY by the host.
    PlacementSet     Available,     // hostCapabilities & contentAvailability & Allowed
    long             ContentGen,    // monotonic content identity (a track change bumps it)
    long             DismissedGen)  // the ContentGen the user dismissed for; -1 = none
{
    public bool DismissedForContent => DismissedGen == ContentGen;
    public bool WantsVisible => Requested && !DismissedForContent && Available != PlacementSet.None;
}
```

`Resolved = PlacementCore.Resolve(state, descriptor.Fallback)` is a `Memo<SurfacePlacement>`. **Every** surface renderer and **every** affordance reads that one memo and `State`. Nothing else.

### 2.3 Single-owner rule

Exactly three writer categories, and they are physically distinct methods:

| Writer | Methods | Who calls |
|---|---|---|
| **User intent** | `Toggle`, `Request`, `MoveTo`, `Hide`, `DismissForCurrentContent`, `Restore`, `ExitFullscreen` | affordances, menus, the surface's own ✕, keyboard |
| **Environment** | `SetAvailability`, `NotifyContentChanged` | the registry (host capabilities), `PlaybackBridge` (has-video, track gen) |
| **Reality** | `NotifyMounted`, `NotifyExternalClose` — `internal` | `SurfaceHost` / `SurfaceOutlet` only |

No view holds a window handle. No view holds an "am I open" bool. The affordance stores nothing.

### 2.4 The full transition table

`allowed = descriptor.Allowed`; `Pick(p) = allowed.Has(p) ? p : current`.

| Event | Preconditions | `Requested` | `Preferred` | `ReturnTo` | `Live` | `DismissedGen` | Notes |
|---|---|---|---|---|---|---|---|
| `Request(p)` | — | `true` | `Pick(p)` | unchanged | — | `-1` | Clears a prior dismissal, so a re-click after ✕ *does something*. |
| `Toggle(p)` | `WantsVisible && Preferred == p` | `false` | unchanged | unchanged | — | — | Symmetric off. |
| `Toggle(p)` | otherwise | → `Request(p)` | | | | | Click a different placement while visible ⇒ move, don't close. |
| `MoveTo(p)`, `p != Fullscreen` | `allowed.Has(p)` | `true` | `p` | unchanged | — | `-1` | |
| `MoveTo(Fullscreen)` | `allowed.Has(Fullscreen)` | `true` | `Fullscreen` | `s.Preferred` (if not already FS) | — | `-1` | |
| `ExitFullscreen` | `Preferred == Fullscreen` | unchanged | `ReturnTo` | unchanged | — | — | Esc. |
| `Hide` | — | `false` | unchanged | unchanged | — | — | The master switch. Preference + geometry survive. |
| `DismissForContent` | — | unchanged | unchanged | unchanged | — | `ContentGen` | The surface's own ✕. Audio keeps playing. |
| `Restore` | — | unchanged | unchanged | unchanged | — | `-1` | |
| `ContentChanged(g)` | — | unchanged | unchanged | unchanged | — | `-1` | New track. Intent survives; dismissal expires. |
| `AvailabilityChanged(a)` | — | unchanged | unchanged | unchanged | — | — | `Available = a & allowed` |
| `ExternalClose(p)` | `Live == p ‖ p == None` | unchanged | `Demote(p, Preferred)` | unchanged | `None` | — | OS closed the window. Intent is NOT cleared. |
| `ExternalClose(p)` | `Live != p` | no-op | | | | | Stale callback from a superseded window. |
| `Mounted(p)` | host only | — | — | — | `p` | — | Never persisted (reality is not preference). |

`Demote(lost, pref) = pref != lost ? pref : lost is Detached or Fullscreen ? Floating : pref` — after an OS close, the remembered preference must not be the thing you just closed.

**Resolution** (`Resolve`):

```
if (!WantsVisible)                     → None
if (Available.Has(Preferred))          → Preferred
foreach p in descriptor.Fallback       → first p with Available.Has(p)
otherwise                              → None
```

**Reconcile** (what the host does this frame): `desired == live → None`; `live == None → Mount`; `desired == None → Unmount`; else `Relocate`.

### 2.5 Availability, dismissal, content change

- `Available = hostCapabilities & contentAvailability & Allowed`, recomputed reactively.
  - *hostCapabilities*: `Docked|Floating` always; `+Detached` iff the new `InputHooks.CanOpenDetachedWindow()` is true (today `AppHost.OpenDetachedWindow` silently returns `null` on headless / no secondary swapchains / a child host — `AppHost.cs:825-826` — so the button is drawn enabled and dead-clicks); `+Fullscreen` iff `InputHooks.WindowSetFullscreen is not null`.
  - *contentAvailability*: the feature's own answer. Video: `CurrentTrackHasVideo ? All : None`.
- **Dismissal is content-scoped by construction**: `DismissedGen == ContentGen`. A track change bumps `ContentGen`, so the dismissal expires without anyone remembering to clear it. This generalizes `_dismissedForTrackGen` and removes `RestoreVideo()`'s global `-1` reset (`PlaybackBridge.cs:189`), which breaks the moment there are two dismissable surfaces.
- **Async content resolve is fenced**: `PlacementCore.ShouldAdopt(capturedGen, currentGen)` — the generalization of `ShouldPublishResolve`. Additionally the content subtree stays keyed on the content identity (`Key = "stage:" + src.Key`, `PopOutVideoWindow.cs:44`), so even a mis-ordered publish cannot paint a stale frame.
- **`ContentGen` must be reactive.** Today `_trackGen` is a plain field (`PlaybackBridge.cs:169`) while `_dismissedForTrackGen` is a signal; `VideoActive()` reads both, so a bare `_trackGen++` does not notify — it works only because `PushState` happens to write three other signals in the same batch (`:462-468`). In the new model `ContentGen` is a field *of the one state struct*, so a change is always a notifying, equality-gated write.

### 2.6 Every known pain, resolved structurally

| Pain | Resolution | Why it cannot come back |
|---|---|---|
| **Toggle stuck highlighted after closing the pop-out** | `SurfaceButton` computes `IsChecked = WantsVisible && Preferred == p` from the same memo the surface mounts from. It stores nothing. | Two sources of truth are needed to desync; there is one. |
| **Closing the detached window didn't fall back to the mini player** | `ExternalClose` clears `Live`, keeps `Requested`, demotes `Preferred`; the next `Resolve` returns `Floating`. | It's a reducer row with a unit test, not a callback that a refactor can drop. Plus the primitive — not each feature — owns the stale-callback identity guard, the intentional-vs-external distinction (`OnClosed = null` before `Close()`), and the unmount disposer that today live hand-written in `VideoPlacementHost.cs:56-88`. |
| **Stale video after a track change** | `ContentChanged` + `ShouldAdopt` + content-keyed subtree. | One fence for every surface, tested exhaustively. |
| **Floating panel overlapped / bled into content** | Two rules. (1) *Frame discipline*: `SurfaceOpacityMode.TransparentHole` is a declared contract — `FloatingFrame` paints its chrome bar, border, shadow, and letterbox plate **opaque** and punches only the content rect, which is what `InWindowVideoPip.cs:75-78,166-168` discovered the hard way. (2) *Reserve while anchored*: a descriptor with `ReservesLayoutWhenAnchored = true` publishes its rect as an inset through `Surfaces.FloatingReserve`; the shell's content region adds it as padding **while the panel is still at its default anchor**. The instant the user drags it, the reservation drops (they took ownership of that space) — the same `_placed` semantic the PiP already has (`:40`), promoted to a layout contract. | The overlap was a missing *layout* concept, not a z-order bug; now it's a declared field. |
| **Long buffering with a black frame** | `SurfacePhase { Idle, Resolving, Buffering, Ready, Failed }` on the descriptor; `FloatingFrame`/`SurfaceWindowRoot` render poster + progress for anything not `Ready`. `MediaPlayerElement` already draws `PosterContent` while `!videoReady` and a buffering overlay (`MediaPlayerElement.cs:83-84, 240-252`) — today nothing passes a poster and nothing reads `IsBuffering` for video. `VideoSurfaceContent` passes the album art + title as `PosterContent`; the frame shows "Resolving…" before a source even exists (today: a flat `Tok.MediaLetterbox` rectangle, `:168` / `PopOutVideoWindow.cs:41`). | Loading is a typed state every frame knows how to draw, for every surface. |
| **State split across owners** | One signal, one reducer, three writer categories. | — |
| **Two audio streams / video restarts from 0** | §2.7. | — |
| **Three competing placement systems** | The rail's docked↔floating heuristic (`ShellUi.RailFits`/`CanFitRail`) becomes `Fallback = [Floating]` + a `Docked` availability bit driven by the fit test. `MediaPlayerElement.ToggleFullscreen` stops opening its own overlay when it is mounted inside a surface and instead calls `controller.MoveTo(Fullscreen)` (via a new `Surfaces.Ambient` context carrying `(controller, placement)`); the standalone control keeps its current behavior when there is no ambient surface. | One owner of "where these pixels are". |

### 2.7 The resource rule that makes moves continuous

Two facts from the code, both binding:

- A detached window is a **separate `AppHost`** with its own reconciler, its own ambient map (`Reconciler.cs:338`), its own `VideoSurfaceRegistry` and `IVideoPresenter` (`AppHost.cs:857-863`). A composited video *surface binding* therefore **cannot** migrate across the boundary, and app `Ctx.Provide` chains **do not** cross it (which is why `PopOutVideoWindow` takes a frozen `IReadSignal<>` prop). The primitive answers the second with `SurfaceDescriptor.Envelope` (the app's provider chain, re-applied at the detached root).
- The **`IMediaPlayer` is a managed object** and *can* outlive a surface — if something other than the surface owns it.

So the rule is: **the surface presents a player it does not own.** `FluentVideoMediaHost.CurrentPlayer` + its `PlayerChanged` event (`src/apps/Wavee/SpotifyLive/Audio/FluentVideoMediaHost.cs:56,60`) were designed as exactly this seam and are currently unused; `PopOutVideoStage` builds its own player instead (`PopOutVideoWindow.cs:59-80`). Once the surface binds to `CurrentPlayer`:

- a placement move re-binds a presenter instead of rebuilding a player ⇒ **no restart from 0**, only a short `Buffering` phase while the new surface starts pumping (the MF session only advances while a mounted `MediaPlayerElement` pumps it, so the gap is real and is shown honestly as `Buffering` over the last poster);
- `MediaSwitchLogic.ShouldStopOutgoingHost` finally gets consulted ⇒ **one audio stream**;
- the transport bar drives one current host ⇒ play/pause/seek/`track_player` are truthful.

`ISurfaceResumable { TimeSpan Position; void ResumeAt(TimeSpan) }` remains in the primitive as the fallback for content that genuinely *must* be rebuilt (a clear-URL re-open, local files). **We do not claim seamless decoder migration** — it is physically impossible across two `IVideoPresenter`s; we claim position continuity plus an honest loading state.

**Mount ordering is an engine invariant, not luck:** a surface's subtree unmounts during reconcile; OS resources are acquired in a `UseSignalEffect`/`UseLayoutEffect` that runs *after* that reconcile. Consequence: on `Floating → Detached` the floating `MediaPlayerElement` is gone before the new window opens. There is never a frame with two pumps. `VideoPlacementHost.cs:34-74` gets this right by accident today; here it is documented and gated (`gate.surface.single-mount`).

---

## 3. Click / affordance spec

### 3.1 Where the control lives

One glyph per surface in the player-bar right cluster — the app's existing convention (`PlayerBar.cs:475-579`) — plus placement/close controls on the surface's own chrome. No new home is invented.

### 3.2 Semantics

| Gesture | Action |
|---|---|
| **Primary single click, unlit** | `Toggle(Preferred)` ⇒ open at the remembered placement. First ever use = `DefaultPlacement`, which is the **lowest-commitment** placement the surface supports: **Floating for video** (today it's `Detached` — `PlaybackBridge.cs:164`, `PlayerBar.cs:505`), Docked for rail panels. A bare click never spawns an OS window. |
| **Primary single click, lit** | `Hide()`. One click always means off, from any placement. (Today it means off *only* from Detached and reads as "pop out" from PiP — `PlayerBar.cs:500-510`.) |
| **Chevron click** | Opens the placement menu. **The chevron never lights.** (Today it lights on `pipActive` — `PlayerBar.cs:565` — a disclosure arrow used as a state indicator.) |
| **Right-click / Menu key / long-press on the primary** | Same menu, via `Element.OnContextRequested` (`src/FluentGpu.Engine/Dsl/Element.cs:160`) — the NN/g split-button mitigation, using an engine hook the app already relies on for `TrackRow`. |
| **Menu items** | `MenuFlyoutItem.RadioItem(label, isChecked: p == Preferred, () => c.MoveTo(p))` for each allowed placement, in the fixed order Docked · Floating · Separate window · Full screen; a `Separator`; then **"Turn off video"** → `Hide()`. Unavailable placements are shown **disabled with a reason**, never omitted. (Today the items are plain, stateless `MenuFlyoutItem`s — `PlayerBar.cs:540-546` — even though `RadioItem` and `Toggle` are used elsewhere in the same file at `:707-714, :887`.) |
| **Surface chrome ✕** | `DismissForCurrentContent()` — this track only, intent survives. Tooltip "Hide for this song". |
| **Menu "Turn off …"** | `Hide()` — the master switch. Two distinct verbs, two distinct labels; today both exist but the distinction lives only in a comment (`InWindowVideoPip.cs:143-144`). |
| **Drag-to-detach** | **Not in v1.** Firefox's tab-tear-off sensitivity complaint is a real hazard on this machine's touchpad. Revisit only with a distance+velocity threshold and a ghost-outline preview. The substrate exists (`Detached` + the factory) whenever `TabView` tear-out (`docs/plans/winui-parity-sweep.md:604`) wants it. |

**Label discipline.** Every control is named for its **destination**, never a bare verb. Today `Strings.Player.SwitchToVideo` is used for three different things — the primary tooltip, a menu item, *and the detached window's OS title* (`VideoPlacementHost.cs:48`) — which is precisely the band-aid texture. New keys in `src/apps/Wavee/assets/loc/en-US.json`: `Surface.WatchVideo`, `Surface.TurnOffVideo`, `Surface.MiniPlayer`, `Surface.SeparateWindow`, `Surface.FullScreen`, `Surface.Dock`, `Surface.HideForThisSong`, `Surface.UnavailableNoSecondWindow`, and a real window title `Surface.VideoWindowTitle`.

### 3.3 Concrete PlayerBar wiring

`Transport(...)` (`PlayerBar.cs:846-873`) stays the one button primitive: active = accent glyph + 3px accent dot, never a filled background. Two small additions to it: an `AutomationRole role = AutomationRole.Button` parameter (surface buttons pass `AutomationRole.ToggleButton`, which the enum already has — `src/FluentGpu.Engine/Foundation/AutomationRole.cs:12`) and an `Action<ContextRequestEventArgs>? onContext` forwarded to `OnContextRequested`.

```csharp
// src/FluentGpu.Controls/Surfaces/SurfaceAffordance.cs  (engine-side, host-agnostic pieces)
public static class SurfaceMenu
{
    // Pure-ish: reads only the controller's derived predicates; the app supplies localized labels.
    public static void CollectInto(SurfaceController c, List<MenuFlyoutItem> into, in SurfaceMenuLabels L);
}
public readonly record struct SurfaceMenuLabels(
    Func<SurfacePlacement, string> Label,
    Func<string> Off,
    Func<SurfacePlacement, string>? UnavailableReason = null);
```

```csharp
// src/apps/Wavee/Features/Shell/PlayerBar.cs — replaces lines 481-569 wholesale.
var video = surfaces?.TryGet(WaveeSurfaces.Video);
if (active && video is not null && video.IsRelevant)          // "content exists, or it's currently on"
{
    bool lit = video.IsVisible;                                // == Resolved.Value != None  (ONE read)
    void OpenMenu(Point2? at = null) { /* overlay svc + SurfaceMenu.CollectInto, TopEdgeAlignedLeft,
                                          FocusTrap + LightDismiss, ConstrainToRootBounds=false */ }

    rightKids.Add(new BoxEl
    {
        Key = "video", Direction = 0, AlignItems = FlexAlign.Center, Animate = ItemMotion,
        OnRealized = h => videoAnchor.Value = h,
        Children =
        [
            ToolTip.Wrap(
                Transport(Icons.Movie, () => video.Toggle(video.State.Value.Preferred),
                          enabled: true, active: lit, accent, buttonBox, buttonGlyph,
                          role: AutomationRole.ToggleButton,
                          onContext: e => OpenMenu(e.Point)),
                Loc.Get(lit ? Strings.Surface.TurnOffVideo : Strings.Surface.WatchVideo)),   // state-derived
            Transport(Icons.ChevronDownSmall, () => OpenMenu(), enabled: true,
                      active: false,                                     // a disclosure NEVER lights
                      accent, buttonBox * 0.55f, buttonGlyph * 0.62f),
        ],
    });

    // Breakpoint safety net — the video button is NOT registered today (contrast Queue at :440-441),
    // so at narrow widths it simply vanishes with no fallback. AppBarCommand.Flyout gives the overflow
    // row a real cascading placement sub-menu (CommandBarFlyout.cs:46).
    overflowCommands.Add(new AppBarCommand(Icons.Movie, Loc.Get(Strings.Surface.WatchVideo),
        Invoke: () => video.Toggle(video.State.Value.Preferred),
        Kind: AppBarCommandKind.ToggleButton, IsChecked: lit)
        { Flyout = SurfaceMenu.Items(video, VideoLabels), Accelerator = Keys.Ctrl | Keys.Shift | Keys.V });
}
```

Lyrics / Queue / Details keep their exact current shape, only swapping `ui.Toggle(RailMode.X)` for `surface.Toggle(surface.State.Value.Preferred)` and gaining the same state-derived tooltip they lack today.

### 3.4 Keyboard & accessibility

- `AppBarCommand.Accelerator` (`CommandBarFlyout.cs:43`) carries a real chord per surface: **Ctrl+Shift+V** video, **Ctrl+Shift+L** lyrics, **Ctrl+Shift+Q** queue. **F11** toggles Fullscreen for the focused/active surface, matching `MediaPlayerElement`'s existing `AcceleratorText = MediaStrings.F11` (`:516`). Verify each chord against `InputHooks`' live bindings before landing; a collision is a rename, not an override.
- **Esc** collapses the topmost active `Fullscreen`/`Floating` surface one tier (`ExitFullscreen`, then `Hide`), routed through the existing `InputHooks.KeyPreview` chain so an open flyout still wins.
- `Role = AutomationRole.ToggleButton` + the derived checked state feed one scene column, so announced state and painted state come from the same value. **Honest caveat:** `src/FluentGpu.Windows/Uia/Placeholder.cs` shows the UIA provider is not implemented yet — so this is the *correct contract for when it lands*, not a shipping screen-reader claim. Do not write release notes that say otherwise.
- **Focus never moves on an automatic open.** A surface reappearing because the next track has a video must not steal focus. Focus moves into a newly-opened surface only when the user's own keyboard activation opened it (the same rule `OverlayHost` already applies for `FocusTrap`).
- Detached windows get a real title (`Surface.VideoWindowTitle`), a `SetTitle` seam for track-aware titles, and a user-controllable always-on-top (today it is hard-wired `AlwaysOnTop: true` at `VideoPlacementHost.cs:49` despite `SetTopmost` existing at `Context.cs:81`).

---

## 4. Persistence spec

Uses the app's existing store unchanged: `IAppSettings` + `SettingKey<T>` (`src/apps/Wavee/Platform/AppSettings.cs:8-16`), reached via `UseContext(Services.Slot)?.Settings` or the constructor-injected `IAppSettings` the shell already threads (`WaveeShell.cs:143`). Backing store supports only `float/double/bool/int/long/string` (`AppDataSettings.Get/Set`, `:135-163`), and cannot round-trip `null` strings — so **enums persist as `int`** and **rects persist as a comma string** (the `EqualizerGains` precedent, `:67`), parsed defensively.

Keys are built per surface id at runtime — the `LibraryStateKeys` pattern (`:99-114`), AOT-clean plain record construction:

```csharp
// src/apps/Wavee/App/WaveeSurfacePersistence.cs
static SettingKey<int>    Placement(SurfaceId id) => new($"surface.{id.Name}.placement", -1);   // -1 = never set
static SettingKey<bool>   Requested(SurfaceId id) => new($"surface.{id.Name}.requested", false);
static SettingKey<string> FloatRect(SurfaceId id) => new($"surface.{id.Name}.floatRect", "");   // "x,y,w,h" DIP
static SettingKey<string> DetBounds(SurfaceId id) => new($"surface.{id.Name}.detBounds", "");   // "x,y,w,h" px
static SettingKey<bool>   Topmost(SurfaceId id)   => new($"surface.{id.Name}.topmost", true);
```

| What | Scope | Across track? | Across restart? | Rule |
|---|---|---|---|---|
| Per-content dismiss (surface ✕) | this content item | **No** (expires with `ContentGen`) | **Never written** | Only meaningful for content-scoped surfaces. |
| `Requested` ("it's on") | feature, this run | **Yes** | **Only if `RestorePolicy == RestoreVisible`** | Rail panels: yes (workspace layout, zero cost). **Video: no** — no reviewed product silently resumes a floating video on launch, and we must not spin a DRM/decode session unasked. |
| `Preferred` placement kind | feature | Yes | **Yes** | The workspace fact: "when I turn video on I like it in a separate window." Never cleared by `Hide()`. `Fullscreen` is coerced to `ReturnTo` before writing. |
| Floating rect | feature | Yes | **Yes, but only after the user actually drags/resizes once** | The first auto-anchored position must never be promoted to a preference. Re-clamped to the live viewport on every restore (reuse `ClampX/ClampY`, `InWindowVideoPip.cs:256-259`). |
| Detached bounds + topmost | feature | Yes | **Yes, revalidated against the live work area at open time** — not eagerly at launch | Requires the new `IDetachedWindow.BoundsPx` read-back; today only `SetBounds` exists (`Context.cs:83`) so this is literally unsavable. Off-screen ⇒ fall back to `AppHost.cs:834-848`'s work-area bottom-right. |
| Docked rail: which surface + rail width | app chrome | n/a | **Yes** (new — today `RailOpen`/`Mode`/`RailWidth` reset every launch) | Restoring panel layout is what VS Code and Notion do; it restores *layout*, never *a running session*. |
| Fullscreen | any | n/a | **Never** | Hard rule. Always a deliberate act. |
| A monitor-disconnect / clamp fallback | feature | n/a | **Not written back** | A transient hardware change must never silently become the user's permanent preference. Only a real user drag/resize re-commits. |

**Forget rules, explicitly.** `Hide()` clears `Requested` and nothing else. Uninstalling a monitor clears nothing. A `Preferred` value outside `Allowed` (a surface lost a capability between versions) is dropped at load and the default is used. A `Requested = true` restored for a surface whose content is unavailable resolves to `None` via the fallback chain — no special case.

**When we write.** Never per drag frame. Geometry goes through `UseDebouncedValue(..., 400f)` (`RenderContext.Timers.cs:171`) plus a commit on drag release (the `ColumnGrip(onReleased:)` idiom, `DetailShell.cs:442-459`) and once on unmount. `Preferred`/`Requested` write on the intent that changed them.

**When we read.** At **field construction**, not in an effect — the discipline `WaveeShell.cs:146-147, 55-58` documents: seeding at construction means the first layout already uses the saved value, so there is no launch-time pop-in or startup animation. `SurfaceController`'s constructor loads and validates.

**The one-line policy:** *persist where the user likes to work; never persist whether something is currently running.*

---

## 5. The API

### 5.1 Layer split

| Location | New files | Contents |
|---|---|---|
| `src/FluentGpu.Engine/Surfaces/` | **`PlacementCore.cs`** — **`using System;` ONLY** | `SurfacePlacement`, `PlacementSet`, `PlacementChain`, `PlacementState`, `SurfaceEvent`, `SurfaceAction`, `PlacementCore.{Reduce, Resolve, Reconcile, ShouldAdopt, IsChecked, IsEnabled, IsUnavailable}` |
| | `SurfaceModel.cs` | `SurfaceId`, `SurfaceDescriptor`, `SurfaceView`, `SurfaceChrome`, `SurfacePhase`, `SurfaceOpacityMode`, `RestorePolicy`, `FullscreenTarget`, `PersistedPlacement`, `ISurfacePersistence`, `IDetachedWindowFactory`, `ISurfaceResumable` |
| | `SurfaceController.cs` | one `Signal<PlacementState>`, one `Memo<SurfacePlacement>`, the intents, the single `Apply` |
| | `SurfaceRegistry.cs` | `Surfaces.Registry` / `Surfaces.Ambient` context channels, `Register`/`TryGet`, dock-group arbitration, availability composition, persistence fan-out, `FloatingReserve` |
| `src/FluentGpu.Controls/Surfaces/` | `SurfaceHost.cs` | the single owner of Floating layer + detached leases + fullscreen layer |
| | `FloatingFrame.cs` | the reusable framed/draggable/resizable/clamped panel — `InWindowVideoPip.cs:114-262` extracted verbatim |
| | `SurfaceOutlet.cs`, `SurfaceDockOutlet.cs` | the Docked renderer; the dock-group renderer (generalizes `RightRail`'s `mode switch`) |
| | `SurfaceAffordance.cs` | `SurfaceButton`, `SurfaceMenu`, `SurfaceMenuLabels` |
| `src/apps/Wavee/App/` | `WaveeSurfaces.cs` | the descriptor table (§5.4) |
| | `WaveeSurfacePersistence.cs` | `ISurfacePersistence` over `IAppSettings` |
| | `VideoAvailability.cs` | feature-only rules: has-video, content gen, resolve fencing (replaces `VideoPlacementModel.cs`) |
| `src/apps/Wavee/Features/Video/` | `VideoSurfaceContent.cs` | ~40 lines: presents `FluentVideoMediaHost.CurrentPlayer` in a `MediaPlayerElement` with a real poster |

**Deleted:** `VideoPlacementHost.cs` · `InWindowVideoPip.cs` · `PopOutVideoWindow.cs`'s window-root half · `VideoPlacement` enum · `VideoPlacementLogic.{VideoActive, DecideDetached, FallbackOnUserClose}` · `PlaybackBridge.{PreferVideo, VideoPlacement, _dismissedForTrackGen, VideoActive, DismissVideoForCurrentTrack, RestoreVideo}` · `ShellUi.{RailOpen, Mode, Toggle, RailFits, CanFitRail}`. `MediaSwitchLogic` **stays app-side unchanged** — it is about which decoder host plays what, which has no engine analogue.

### 5.2 The pure core (the part that carries the behavior)

```csharp
namespace FluentGpu.Surfaces;   // PlacementCore.cs — System-only, no Signal<T>, no Element, no RectF

public enum SurfacePlacement : byte { None = 0, Docked = 1, Floating = 2, Detached = 3, Fullscreen = 4 }

[Flags] public enum PlacementSet : byte
{
    None = 0,
    Docked = 1 << 1, Floating = 1 << 2, Detached = 1 << 3, Fullscreen = 1 << 4,
    InWindow = Docked | Floating | Fullscreen,
    All = Docked | Floating | Detached | Fullscreen,
}

/// An ordered degradation chain. THREE inline slots — POD, no array, no nibble packing:
/// readable in a debugger and exhaustively testable.
public readonly record struct PlacementChain(
    SurfacePlacement A, SurfacePlacement B = SurfacePlacement.None, SurfacePlacement C = SurfacePlacement.None)
{
    public int Length => C != SurfacePlacement.None ? 3 : B != SurfacePlacement.None ? 2 : A != SurfacePlacement.None ? 1 : 0;
    public SurfacePlacement At(int i) => i switch { 0 => A, 1 => B, _ => C };
}

public readonly record struct PlacementState(/* §2.2 */);
public enum SurfaceEventKind : byte { Request, Toggle, MoveTo, ExitFullscreen, Hide,
                                      DismissForContent, Restore, ContentChanged,
                                      AvailabilityChanged, ExternalClose, Mounted }
public readonly record struct SurfaceEvent(SurfaceEventKind Kind,
    SurfacePlacement Placement = SurfacePlacement.None,
    PlacementSet Available = PlacementSet.None, long ContentGen = 0);
public enum SurfaceAction : byte { None, Mount, Unmount, Relocate }

public static class PlacementCore
{
    public static PlacementState Reduce(in PlacementState s, in SurfaceEvent e, PlacementSet allowed);  // §2.4
    public static SurfacePlacement Resolve(in PlacementState s, in PlacementChain fallback);            // §2.4
    public static SurfaceAction Reconcile(SurfacePlacement desired, SurfacePlacement live);
    public static bool ShouldAdopt(long capturedGen, long currentGen) => capturedGen == currentGen;
    public static bool IsChecked(in PlacementState s, SurfacePlacement at);
    public static bool IsEnabled(in PlacementState s, PlacementSet allowed, SurfacePlacement at);
    public static bool IsUnavailable(in PlacementState s, PlacementSet allowed, SurfacePlacement at);
}
```

`Reduce` is total, pure, allocation-free. `readonly record struct` + `Signal<T>`'s `EqualityComparer<T>.Default` gate (`Signal.cs:60-68`) means one equality-checked write, no boxing, no torn intermediate. All of it is reconcile-time (phases 1–5) and never touches the zero-alloc paint half.

### 5.3 Controller / registry surface

```csharp
public sealed class SurfaceController
{
    public IReadSignal<PlacementState>  State    { get; }
    public IReadSignal<SurfacePlacement> Resolved { get; }   // the ONE thing surfaces read
    public bool IsVisible => Resolved.Value != SurfacePlacement.None;
    public bool IsRelevant => IsVisible || State.Value.Available != PlacementSet.None;
    public bool IsChecked(SurfacePlacement at); public bool IsEnabled(SurfacePlacement at);
    public bool IsUnavailable(SurfacePlacement at);

    public void Request(SurfacePlacement at); public void Toggle(SurfacePlacement at);
    public void MoveTo(SurfacePlacement at);  public void ExitFullscreen();
    public void Hide();  public void DismissForCurrentContent();  public void Restore();
    public void NotifyContentChanged(long contentGen);   public long ContentGen { get; }
    public void SetContentAvailability(PlacementSet available);

    internal void NotifyMounted(SurfacePlacement at);
    internal void NotifyExternalClose(SurfacePlacement from);
}

public static class Surfaces
{
    public static readonly Context<SurfaceRegistry?> Registry = new(null);
    /// The surface a subtree is currently rendering inside — how MediaPlayerElement's fullscreen
    /// button defers to SurfacePlacement.Fullscreen instead of opening its own overlay.
    public static readonly Context<SurfaceAmbient?> Ambient = new(null);
}
```

### 5.4 Registering a feature (this is the whole cost)

```csharp
// src/apps/Wavee/App/WaveeSurfaces.cs
reg.Register(new SurfaceDescriptor
{
    Id               = Video,
    Allowed          = PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen,
    DefaultPlacement = SurfacePlacement.Floating,                     // lowest commitment — the inversion
    Fallback         = new(SurfacePlacement.Floating),
    RestorePolicy    = RestorePolicy.RestorePlacementOnly,            // never auto-resume a stream
    Opacity          = SurfaceOpacityMode.TransparentHole,            // the DComp-hole rule, declared once
    FloatingAspect   = 16f / 9f,
    ReservesLayoutWhenAnchored = true,                                // no more bleeding over content
    Phase            = () => b.VideoPhase.Peek(),                     // Resolving → Buffering → Ready → Failed
    Envelope         = envelope,                                      // crosses PlaybackBridge.Slot into the detached host
    Chrome           = new(() => b.CurrentTrack.Peek()?.Name ?? Loc.Get(Strings.Player.NowPlaying), Glyph: Icons.Movie),
    Content          = v => Embed.Comp(() => new VideoSurfaceContent { View = v }),
});

reg.Register(new SurfaceDescriptor
{
    Id = Lyrics, DockGroup = "rail",
    Allowed = PlacementSet.All, DefaultPlacement = SurfacePlacement.Docked,
    Fallback = new(SurfacePlacement.Floating),          // ← this IS ShellUi.RailFits, generalized
    RestorePolicy = RestorePolicy.RestoreVisible,
    Chrome = new(() => Loc.Get(Strings.Player.Lyrics)),
    Content = v => Embed.Comp(() => new LyricsView(large: v.Placement is SurfacePlacement.Fullscreen)),
});
```

Note `LyricsView` already takes `(bool large, Func<bool>? visible)` (`src/apps/Wavee/Features/Player/LyricsView.cs:133`) — it was *already* placement-parameterized and only the rail ever used it.

Shell shape:

```csharp
Ctx.Provide(Surfaces.Registry, _surfaces,
  Ctx.Provide(ShellUi.Slot, _shellUi, /* … */
    OverlayHost.Create(SurfaceHost.Create(shellWithRailOutlet))))
```

`SurfaceHost` mounts **inside** `OverlayHost` (exactly where the PiP + placement host sit today, `WaveeShell.cs:570-582`) so flyouts still stack above surfaces. `SurfaceHost` is deliberately **not** a replacement for `IOverlayService`: overlays are transient/anchored/light-dismissed/stacked; surfaces are persistent/user-placed/remembered/singleton. Different lifetimes, different primitives.

### 5.5 The unit-test seam

`Wavee.Tests` deliberately does **not** reference `FluentGpu.Engine` (it would shadow the source-included Backend). It source-includes individual pure files — `VideoPlacementModel.cs`, `MediaSwitchLogic.cs`, and even `FluentGpu.Controls\VirtualCollection.cs` and `SelectionModel.cs`. `PlacementCore.cs` is `System`-only, so it joins that list with one line:

```xml
<!-- src/apps/Wavee.Tests/Wavee.Tests.csproj -->
<Compile Include="..\..\FluentGpu.Engine\Surfaces\PlacementCore.cs" Link="PlacementCore.cs" />
```

and, because it is engine code, it also gets its own engine gate: `src/FluentGpu.VerticalSlice/Suites/SurfaceSuite.cs`. CI must not depend on the app's test project for engine behavior.

### 5.6 Engine gaps to fill (small, named, additive)

| # | Gap | Evidence |
|---|---|---|
| G1 | `IDetachedVideoWindow` → **`IDetachedWindow`** + `RectF BoundsPx { get; }`, `Action<RectF>? BoundsChanged`, `bool IsTopmost`, `SetTitle`, `Activate()`, `SetFullscreen(bool)` | `Context.cs:76-89` — the video-specific *name* is itself the symptom; no bounds read-back exists, so geometry persistence is impossible. `IPlatformWindow` needs the matching getter (`Pal.cs:512` has `SetBoundsPx` only; `IPlatformPopupWindow` at `:395` sets the precedent). |
| G2 | `DetachedWindowRequest` += `RectF? InitialBoundsPx`, `Size2 MinClientSizeDip`, `bool ShowInTaskbar` | `Context.cs:72-73`; min size hard-coded 320×180 at `AppHost.cs:832`, position always work-area bottom-right at `:837-848`. |
| G3 | `InputHooks.CanOpenDetachedWindow` (`Func<bool>?`) | the preflight for `AppHost.cs:825-826`'s silent `null`. |
| G4 | `IPlatformApp` += `event Action? DisplaysChanged` (WM_DISPLAYCHANGE / SPI_SETWORKAREA) | `GetWorkArea` exists (`Pal.cs:361`) but nothing notifies, so a monitor unplugged *while running* strands a detached window. |
| G5 | `IDetachedWindowFactory` injection (default: `InputHooks`) | without it the entire detached lifecycle is unreachable from any automated gate (`AppHost.cs:825` returns null headless) — §7's `gate.surface.*` depend on this. |
| G6 | Document "**context does not cross the AppHost boundary**" | `Reconciler.SetAmbient` is per-reconciler (`Reconciler.cs:338`); each detached child builds its own (`AppHost.cs:857`). Correct but undocumented, and it will bite every future detached surface. Write it into `docs/design/subsystems/reconciler-hooks.md`; the API answer is `SurfaceDescriptor.Envelope`. |
| G7 | Canon registration | New cross-cutting contract ⇒ per `CLAUDE.md`: a new `docs/design/subsystems/surfaces.md` owner doc, rows in `SPEC-INDEX.md` §2 + `subsystems/README.md`, then `check-canon.ps1`. `Pal.cs:388` already forward-declares this as "the substrate for E10 tear-out windows"; `winui-parity-sweep.md:604,1130` tracks TabView tear-out as blocked on exactly this. |

---

## 6. Migration — replacement, not another layer

This is explicitly a **subtraction**. Nothing below adds a layer on top of the current wiring; every row deletes its predecessor.

| Today | After | Churn |
|---|---|---|
| `PlaybackBridge.{PreferVideo, VideoPlacement, _dismissedForTrackGen, VideoActive(), DismissVideoForCurrentTrack, RestoreVideo}` (`:151-195`) | `SurfaceController`. The bridge keeps only genuine playback: `CurrentTrackHasVideo → reg.SetContentAvailability(Video, …)`, `PushState → NotifyContentChanged(gen)`, plus `PopOutVideoSource` + `RequestPopOutSource`. | Deletes ~45 lines from the bridge and **fixes an ownership violation the app already documented**: `ShellUi.cs:9-13` says chrome state is "kept off `PlaybackBridge` so the bridge stays about playback, not chrome" — placement is chrome. |
| `VideoPlacementLogic.{VideoActive, DecideDetached, FallbackOnUserClose}` + `DetachedAction` | `PlacementCore.{WantsVisible/Resolve, Reconcile, Reduce(ExternalClose)}` — generic and strictly more cases. `ShouldPublishResolve` → `ShouldAdopt`. | The existing `VideoPlacementLogicTests` are **ported first, as parity tests**, before any behavior change. |
| `VideoPlacementHost.cs` (93 lines: lease + identity guard + intentional-close + unmount disposer) | `SurfaceHost.DetachedLeaseTable` — same three hazards, absorbed once for every surface. | File deleted. |
| `InWindowVideoPip.cs` (263 lines ≈ 110 geometry/drag/resize/clamp + 40 content + chrome) | `FloatingFrame` (generic, extracted **verbatim** — same `SidebarResizeGrip` idiom, same `local + scene.AbsoluteRect` reconstruction, same compositor-only `Affine2D.Translation`, same `ClampX/ClampY`) + `VideoSurfaceContent` (~40 lines). | Lyrics/queue/now-playing get drag-resize float **for free**. That is the proof of genericity. |
| `PopOutVideoWindow` + `PopOutVideoStage` | `SurfaceWindowRoot` (engine, applies `Envelope` + chrome + phase) + `VideoSurfaceContent`. `PopOutVideoStage`'s `UseMediaPlayer` **moves into `FluentVideoMediaHost`** (§2.7). | Both surfaces stop constructing players. |
| `PlayerBar.cs:481-569` — 5 hand-written handlers, 4 repeated 4-statement intent blocks, 2 derived booleans, a lying tooltip, a state-lit chevron, no overflow registration | §3.3. | Net ~60 lines deleted. |
| `ShellUi.{RailOpen, Mode, Toggle, RailFits, CanFitRail}` + `RightRail`'s `mode switch` | `DockGroup = "rail"` + `SurfaceDockOutlet { Group = "rail" }`. `ShellUi` shrinks to genuine layout state: `RailWidth`. | The rail's width/slide choreography (`RightRail.cs:34-59`, the four `LayoutTransition`s at `WaveeShell.cs:94-122`) **stays in the app** — the primitive owns *whether*, the app owns *where and how it animates*. |
| `MediaPlayerElement.ToggleFullscreen`'s private overlay path (`:196-219`) | when `Surfaces.Ambient` is present, defers to `controller.MoveTo(Fullscreen)`; standalone behavior unchanged. | Kills the third placement system. |
| nothing persisted | one `ISurfacePersistence`, uniform for all six surfaces. | greenfield, but on existing idioms. |

**Kept as-is, deliberately:** `MediaSwitchLogic` (app concern) · `OverlayHost`/`IOverlayService` (different lifetime) · `ArtistGalleryLightbox` (a modal viewer, not a placeable surface — though `Fullscreen` reuses its `Open(() => NodeHandle.Null, …, Modal)` primitive rather than inventing one) · all rail motion choreography · `SpotifyVideoService` and the association/resolve path (already wired and working, `LiveSessionHost.cs:407,416`).

---

## 7. Adjusted milestones

These **replace** the old remaining plan (old B = composition-root handoff, C = native demux of the video's own audio, D = buffering/streaming). Ordering rationale: the *lie* goes first (M0 — today "watch video" plays two audio streams, which no amount of placement polish can excuse), then the *feel* (M1, visible within days), then the generic primitive **before** any new surface piles on (M2–M3), then memory (M4), then the genericity proof (M5), then unification (M6), then the deep native work (M7) — which is properly last because it needs a stable phase model and a single owned player to land against.

Standing requirement for every milestone: **production-grade + testable**. `dotnet build src/FluentGpu.slnx` clean, `dotnet run --project src/FluentGpu.VerticalSlice` → ALL CHECKS PASSED (zero-alloc gates green), `--screenshot` for visual fidelity, `check-canon.ps1` after any `docs/design/` edit. **The user builds and runs; agents report a verify checklist, never "build-verified".** Note the two known pre-existing `VerticalSlice` failures on clean `main` (flick-seed-gap-invariant, ctx.invoke-anchors-source) — baseline them before attributing anything.

---

### M0 — Truth: one media, one host, one player

**Scope.** Wire the host swap that exists and is inert. Assign `ShouldPlayAsVideo` + `LoadCurrentVideoAsync` at the composition root; make `KindFor` able to return `Video`; consult `MediaSwitchLogic.ShouldStopOutgoingHost` so the audio host stops when video takes over; make `FluentVideoMediaHost` own the `IMediaPlayer` and expose it via `CurrentPlayer`/`PlayerChanged`; make the surfaces **present** that player instead of building one; make `ConnectStateBuilder`'s `track_player` derive from `_currentKind`.

**Files.** `src/apps/Wavee/SpotifyLive/LiveConnect.cs:73-100` · `Backend/PlaybackController.cs:113-116,200-241,1184-1191,1232-1259` · `SpotifyLive/Audio/FluentVideoMediaHost.cs:56,60,126-182` · `Features/Video/PopOutVideoWindow.cs:52-84` (stage stops owning the player) · `SpotifyLive/ConnectStateBuilder.cs:239-241`.

**Risk.** **Highest of the set** — it touches the live playback chokepoint. Mitigations: `MediaSwitchLogic` is already pure and unit-tested; the crossfade/stop rules go through `AllowCrossfade`/`ShouldStopOutgoingHost` rather than new inline logic. **No env-flag staging** — the hooks are wired unconditionally (env flags are not an accepted mechanism in this app); the residual gap is the silent DRM video until M7 demuxes the video's own audio.

**User-visible win.** Clicking "watch video" plays *the video*, with its own soundtrack, once — not on top of the song. Play/pause/seek/next work on it. Placement moves stop restarting from 0. Spotify Connect reports the truth.

**Tests.** `MediaSwitchLogicTests` extended: audio→video→audio host sequences, no-crossfade across a kind change, `ShouldStopOutgoingHost` on every transition, `HostChanges` idempotence. New `PlaybackControllerHostSwapTests` over two fake `IMediaHost`s: assert exactly one host is playing at any point, assert the transport routes to `_currentHost`, assert `KindFor` follows `ShouldPlayAsVideo`. New `ConnectStateBuilderTests` case: `track_player == "video"` iff `_currentKind == Video`.

---

### M1 — Feel: `PlacementCore` + the affordance/flow rewrite on today's two surfaces

**Scope.** Land `PlacementCore.cs` in its **final** shape (so nothing is written twice) and drive today's `InWindowVideoPip`/`VideoPlacementHost` from it via a thin adapter on the bridge. Then fix the flow: primary = symmetric `Toggle(Preferred)`; `DefaultPlacement = Floating`; chevron never lights; radio-checked menu with a separator + "Turn off video"; state-derived tooltips; the renamed loc keys incl. a real detached-window title; right-click/Menu-key opens the same menu; register the video command in `overflowCommands` with a cascading `Flyout`; `SurfacePhase` + `PosterContent` + a "Resolving…"/"Buffering…" strip so a black frame is impossible; `ReservesLayoutWhenAnchored` so the anchored panel stops bleeding over content. Delete `VideoPlacementLogic`.

**Files.** new `src/FluentGpu.Engine/Surfaces/PlacementCore.cs` · `src/apps/Wavee/App/PlaybackBridge.cs` (adapter; `_trackGen` folded into the state) · `Features/Shell/PlayerBar.cs:481-569,846-873` · `Features/Video/{InWindowVideoPip,PopOutVideoWindow,VideoPlacementHost}.cs` · `assets/loc/en-US.json` · `Features/Shell/WaveeShell.cs` (content inset) · delete `App/VideoPlacementModel.cs`.

**Risk.** Low-medium. The reducer is pure and the surfaces still self-gate. Main risk is the content-inset interaction with the shell's `LayoutTransition` set — gate it behind the descriptor flag and screenshot-diff the four rail/PiP combinations.

**User-visible win.** This is the milestone the user *feels*. One click = mini player, click again = off, from any placement. The menu tells you where you are. Nothing is ever lit with nothing on screen. No black rectangle. The panel no longer sits on top of what you're reading. The window has a name that isn't "Switch to video".

**Tests.** `PlacementCoreTests` (source-included, xUnit) — the full §2.4 table plus one named regression per historical bug: `ExternalClose_ClearsLiveKeepsIntent_ResolvesToFloating`, `Toggle_TwiceHides`, `Toggle_OtherPlacementMoves`, `Request_ClearsDismissal`, `ContentChanged_ExpiresDismissal`, `ShouldAdopt_RejectsStaleGen`, plus two property tests: *for any event sequence, `Preferred ∈ Allowed`* and *`Resolve` is a single value ∈ `{None} ∪ (Available ∩ Allowed)`*. Port every existing `VideoPlacementLogicTests` case first as a parity assertion. `--screenshot` goldens: anchored PiP vs dragged PiP vs rail-open, and the `Resolving`/`Buffering` poster state.

---

### M2 — Seams: the engine gaps (additive, no behavior change)

**Scope.** G1–G5 from §5.6, plus the `Transport(role:, onContext:)` parameters.

**Files.** `src/FluentGpu.Engine/Hooks/Context.cs:72-89,164-167` · `Hosting/AppHost.cs:820-894,963-976` · `Seams/Pal/Pal.cs:361,502-517` · `src/FluentGpu.Windows/Pal/*` (WM_EXITSIZEMOVE → `BoundsChanged`; WM_DISPLAYCHANGE → `DisplaysChanged`) · `src/apps/Wavee/Features/Video/VideoPlacementHost.cs` (rename only).

**Risk.** Low; purely additive surface. One rename (`IDetachedVideoWindow` → `IDetachedWindow`) with exactly one consumer in the repo.

**User-visible win.** None directly — but it is what makes geometry persistence possible at all, makes the button honest before the click instead of dead-clicking, and makes the detached lifecycle testable headlessly for the first time.

**Tests.** `SurfaceSuite` seam gates using the fake factory: `gate.surface.availability-preflight` (factory `CanOpen == false` ⇒ `Detached ∉ Available` ⇒ affordance reports `IsUnavailable`, never enabled); `gate.detached.bounds-roundtrip` (`SetBounds` → `BoundsPx` → `BoundsChanged` fires once per settle, not per pixel); `gate.detached.reap-once` (`OnClosed` exactly once, cleared after).

---

### M3 — The primitive: `SurfaceHost`, and video migrated onto it

**Scope.** `SurfaceModel`, `SurfaceController`, `SurfaceRegistry`, `SurfaceHost` (+ `DetachedLeaseTable`), `FloatingFrame` (verbatim extraction), `SurfaceOutlet`, `SurfaceButton`/`SurfaceMenu`. Migrate **video only**. Delete `VideoPlacementHost.cs`, `InWindowVideoPip.cs`, the `VideoPlacement` enum, and the bridge's placement fields.

**Files.** new `src/FluentGpu.Engine/Surfaces/{SurfaceModel,SurfaceController,SurfaceRegistry}.cs` · new `src/FluentGpu.Controls/Surfaces/{SurfaceHost,FloatingFrame,SurfaceOutlet,SurfaceAffordance}.cs` · new `src/apps/Wavee/App/WaveeSurfaces.cs`, `App/VideoAvailability.cs` · new `Features/Video/VideoSurfaceContent.cs` · `Features/Shell/WaveeShell.cs:570-582` · `PlaybackBridge.cs` · deletions above.

**Risk.** Medium — the largest code motion. Mitigations: `FloatingFrame` is a *verbatim* extraction (geometry maths unchanged), M1 already moved the semantics, and the acceptance bar is "video behaves identically to end-of-M1, with the six pains now structurally impossible."

**User-visible win.** Nothing new — that is the point. The win is that the next surface costs 20 lines.

**Tests.** New `src/FluentGpu.VerticalSlice/Suites/SurfaceSuite.cs`: `gate.surface.single-mount` (scripted flips: at most one non-`None` `Mounted` at any instant, **and unmount strictly precedes the next open** — the MF-pump invariant) · `gate.surface.detached-reap` (fake `OnClosed` ⇒ exactly-once, correct fallback, a superseded window's callback is a no-op) · `gate.surface.unmount-leak` (unmount `SurfaceHost` with a lease open ⇒ `Close()` with `OnClosed` nulled, no fallback fired) · `gate.surface.dockgroup-exclusive` (a second surface claiming a group demotes the first per its own chain) · `gate.surface.alloc-zero` (steady-state frames with a mounted floating surface ⇒ 0 bytes on the headless seams; the reducer is static, `SurfaceView` is a struct, lookups are mount-time). Plus a `--screenshot` golden set matching end-of-M1 exactly.

---

### M4 — Memory: persistence

**Scope.** `WaveeSurfacePersistence` over `IAppSettings`; `Preferred`, `Requested` (policy-gated), floating rect (only after a real drag), detached bounds + topmost (revalidated at open), rail occupant + width. Debounced settle + commit-on-release + unmount flush. Constructor-time seeding.

**Files.** new `src/apps/Wavee/App/WaveeSurfacePersistence.cs` · `Platform/AppSettings.cs` (no API change; keys are runtime-built) · `SurfaceController` ctor/`Snapshot` · `SurfaceHost` (debounce + clamp-on-restore) · `App/Services.cs` (inject).

**Risk.** Low-medium. Real hazards, all handled explicitly: no `null` strings (rects as `""`-defaulted comma strings), no enum keys (int, `-1` = never set), no launch-time pop-in (seed at field construction, `WaveeShell.cs:55-58` discipline), no fossilizing a fallback (only a user gesture commits).

**User-visible win.** "It opens where I left it." The single most-requested-feeling property of a desktop app, and today the app persists **zero** window geometry of any kind (verified: no `WindowBounds|SaveWindow|WindowPlacement` hits anywhere in `src/apps/Wavee`, `FluentGpu.Windows`, `FluentGpu.WindowsApi`).

**Tests.** `SurfacePersistenceTests` (fake `IAppSettings`): `Snapshot → TryLoad` round-trip; `Preferred ∉ Allowed` dropped at load; `Fullscreen` never written (coerced to `ReturnTo`); `Requested` written iff `RestoreVisible`; a never-dragged floating rect is not written; an off-screen restored rect clamps and the clamped value is **not** re-persisted; `Hide()` does not clear `Preferred`/geometry. `gate.surface.persist-debounce`: N scripted drag frames ⇒ exactly one `Save`.

---

### M5 — Genericity proof: the rail migrates (Lyrics · Queue · Now playing · Friends)

**Scope.** Four descriptors with `DockGroup = "rail"`; `SurfaceDockOutlet` replaces `RightRail`'s `mode switch`; the fit test becomes a `Docked` availability bit so "doesn't fit" degrades through the same chain as "can't detach"; `ShellUi` shrinks to `RailWidth` + the reflow lock. Lyrics and Queue gain Floating + Detached + persistence with **no new feature code**. Per-track lyrics get an in-panel empty state (panel stays) while video keeps hide-on-unavailable — the one declared difference.

**Files.** `src/apps/Wavee/App/{ShellUi,WaveeSurfaces}.cs` · `Features/Player/RightRail.cs` · `Features/Shell/{WaveeShell.cs:443-512, PlayerBar.cs:474-579}` · `Features/Player/{LyricsView,QueuePanel,NowPlayingPanel,FriendsPanel}.cs` (accept `SurfaceView`).

**Risk.** Medium — the rail's motion choreography is the most tuned animation in the shell (`RailSpacerAnim`/`RailOverlayAnim`/`ContentCardAnim`, `WaveeShell.cs:94-122`; the retained-subtree `TranslateX` slide in `RightRail.cs:34-59`). **The primitive must not touch it**: it decides *whether* Docked is resolved; the shell keeps every transition verbatim. Screenshot-diff open/close/switch at three breakpoints.

**User-visible win.** Lyrics in its own window on the second monitor. Queue as a floating panel. The rail remembers what it was showing. And every entry button in the player bar now behaves identically.

**Tests.** `PlacementCoreTests` dock-group cases: claiming an occupied group demotes the incumbent per *its* chain; a group can never have two `Docked`. `RailFitTests`: viewport width ⇒ `Docked` availability ⇒ `Resolve` returns `Floating` at exactly the widths `CanFitRail` used to (parity with the current heuristic). `gate.surface.dockgroup-exclusive`. Goldens for rail-docked / rail-floating / lyrics-detached.

---

### M6 — Unification: Fullscreen, and the mini-player mode

**Scope.** `SurfacePlacement.Fullscreen` becomes the single owner: `FullscreenTarget.PrimaryWindow` uses `InputHooks.WindowSetFullscreen` + an exclusive top-Z layer in `SurfaceHost`; `FullscreenTarget.OwnWindow` uses the new `IDetachedWindow.SetFullscreen`. `MediaPlayerElement` defers to `Surfaces.Ambient` when present (its `PresentationBinding` / `TransferOwnershipTo` single-writer arbitration, `:158-166`, is reused unchanged — it is already correct). Esc → `ExitFullscreen` → `ReturnTo`. Then the compact/mini **player** mode falls out as one more descriptor: `Detached` applied to a small player shell, not a new state.

**Files.** `src/FluentGpu.Controls/Media/MediaPlayerElement.cs:112,158-219,409,515-516` · `src/FluentGpu.Controls/Surfaces/SurfaceHost.cs` · `src/apps/Wavee/App/WaveeSurfaces.cs` · new `Features/Player/MiniPlayerSurface.cs`.

**Risk.** Medium — the video-surface ownership transfer across a fullscreen flip is the subtlest live code in the media stack. Do not rewrite it; only change *who asks*.

**User-visible win.** F11 does the same thing everywhere. Fullscreen exits back to where you were, not to nothing. A real mini player exists (today `ShowMiniPlayerHere` is a misnomer for the PiP; grep for `MiniPlayer|CompactPlayer` finds nothing else).

**Tests.** `PlacementCoreTests`: `MoveTo(Fullscreen)` captures `ReturnTo`; `ExitFullscreen` restores it; `Fullscreen` is never persisted; `ExternalClose(Fullscreen)` demotes to `Floating`. `gate.surface.fullscreen-single-owner`: a scripted enter/exit asserts exactly one video-surface owner at every step and no double-pump frame.

---

### M7 — Deep media: the video's own audio, and real buffering

**Scope.** The old milestones C and D, now landing on a stable surface + phase model: native demux of the music video's own audio track, real segmented streaming/buffering with a truthful `Buffering` phase (`IMediaPlayer.Buffering` already exists and is read by `MediaPlayerElement.cs:134,240`), progressive start, seek-during-buffer, and a `Failed` phase with a retry affordance in the frame.

**Files.** `src/apps/Wavee/SpotifyLive/Audio/FluentVideoMediaHost.cs` · the MF/PlayReady backend path (`ProtectedMediaBackend`) · `Backend/PlaybackController.cs` position/duration projection · `VideoSurfaceContent`.

**Risk.** Highest technically, lowest structurally — by this point it changes *one* content component and *one* host, behind a phase enum every surface already renders. **It is last on purpose**: doing it before M0 would have meant building a streaming pipeline for a player that a placement flip threw away.

**User-visible win.** Video that starts fast, scrubs, survives a network dip with a real spinner over a poster instead of a black hole, and fails legibly.

**Tests.** Buffering state machine unit tests (Idle→Resolving→Buffering→Ready→Failed→Ready, no illegal edges) with a fake backend; a phase-projection test asserting the frame's rendered state for each phase; seek-during-buffer ordering; `--screenshot` goldens for each phase. `gate.surface.phase-render` in `SurfaceSuite` asserts no phase renders an empty/transparent content rect (the black-frame regression, permanently).

---

## 8. Open questions (small, and deliberately deferred)

1. **Docked scope**: the rail is shell-global today, while pages are keyed per `TabId` (`ContentHost.cs:19-43`). A future route-scoped docked surface must auto-promote to Floating on navigate-away (the reducer row exists; nothing exercises it yet).
2. **Floating stack cap**: two concurrent Floating surfaces before the newest demotes the oldest. Chosen by judgment, not measurement — revisit once lyrics-floating ships.
3. **Battery**: pausing decode when the main window *and* every video surface are simultaneously invisible is a power decision, not a placement one. Keep it out of the reducer.
4. **Video entry points beyond the player bar**: `TrackRow.cs:180-181,253-257` already shows a passive video badge and `DetailTracks.cs:178-184` has a `VideosOnly` filter, but `src/apps/Wavee/Actions/` has zero video references. A "Watch video" context-menu action is a natural M5+ addition and needs nothing new from the primitive.