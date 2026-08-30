# Wavee music-video placement — UX verdict + engineering plan

**Status:** plan, not implemented. **Scope:** *where and how* music video is shown in the Wavee app — the
surfaces (inline, fullscreen, in-window PiP, detached floating window, ambient/Canvas), their behavior, and the
sequenced build. Companion to `docs/plans/wavee-spotify-video-port-plan.md` (which owns the DRM + playback-
integration mechanics). This doc owns placement.

The verdict below is grounded in a research pass over real user sentiment (Spotify Community feature/complaint
boards, Spotify's own Document-PiP engineering writeup, Apple Music MiniPlayer, YouTube/YouTube Music, Tidal,
Chrome/Edge/macOS PiP, VLC/mpv) — what people *want*, *hate*, and how competitors are received.

---

## 1. The verdict (one paragraph)

Build a **control-first, audio-anchored** video system: **video is always available but never forced, and audio
is always the source of truth.** Every loud complaint across every product collapses to two failures — the app
forced the user *into* a video state they didn't choose (Canvas autoplay, silent auto-switch to "video mode"),
or forced the user *out of* their workflow *by* video (fullscreen-or-nothing; video that can't keep playing
while you work elsewhere). Win by never doing either. Ship an excellent **inline + sticky toggle + rock-solid
reliability** foundation first; make the **detached always-on-top floating window** the marquee fast-follow
(it's the single most-validated want and the one big engine project); treat **fullscreen** as a should-have
that is a known regression zone; treat **in-window PiP** as a cheap bridge that does *not* substitute for the
detached window; and **defer ambient/Canvas** (default-off if ever shipped).

## 2. What people want / hate (the evidence, distilled)

**Top wants (ranked):**
1. **Control that sticks** — a reliable audio↔video toggle that never silently reverts, plus a global "never
   autoplay video" default set once. The single most-litigated ask across years of Spotify threads.
2. **Multitasking** — an always-on-top, movable, **resizable** floating window that stays visible while working
   in *other* apps or on a second monitor. Validated by Spotify's own rebuild of its Miniplayer on the Document
   Picture-in-Picture API *specifically* for this, Apple's "keep MiniPlayer on top of all other windows,"
   Chrome/Edge auto-PiP, and the existence of third-party YouTube-PiP apps.
3. **Zero state loss** — audio survives minimize/background; toggling video mode or backgrounding never
   restarts, reseeks, or drops queue/position.
4. **A deliberate, user-invoked fullscreen/theatre** ("like MTV") that keeps lyrics/track info visible.
5. **Synced lyrics *with* the video** (overlay/subtitle-style) — repeatedly re-filed, still unmet, **no
   competitor delivers it**.
6. Core floating/PiP capability **not paywalled** (YouTube Premium-gating this drew backlash).

**Top hates (avoid these):**
1. Songs silently defaulting to / auto-switching into video after the user chose audio-only (the #1 complaint),
   and reverting after restart.
2. Forced autoplaying looping video (Canvas) with no easy, *sticky* off switch — distracting, layout-
   disrupting, battery/data drain, even nausea language.
3. Only two states — tiny fixed inline or full fullscreen — nothing in between; and forced auto-fullscreen that
   kills multitasking (a decade-long Apple/iTunes complaint).
4. Fixed-size, non-movable/non-resizable floating windows once resizable ones exist elsewhere; oversized or
   clipped mini-players.
5. Buggy mini-player fundamentals — dead play/pause, wrong default size, glitchy open/close animation.
   Reliability complaints track as strongly as feature requests.
6. Fullscreen-specific regressions (green-screen, dropped lyrics) and A/V-desync drift over long playback.

## 3. Placement priority (the decision)

| Placement | Priority | Why |
|---|---|---|
| **Inline now-playing video** | **must-have** | The foundation *and* the reliability baseline: small, non-forced, one-gesture dismissible, sticky per-session audio/video toggle + a global never-autoplay default. Toggling/ backgrounding must never touch the audio session, queue, or position. Get this wrong and you reproduce Spotify's two worst complaint clusters. |
| **Detached always-on-top OS window** (movable + resizable, survives app/space/monitor switches) | **must-have (in intent); sequenced as an early fast-follow** | Delivers the single most-validated want — video visible while using *other* apps / a second monitor — which an in-window PiP **structurally cannot**. It is the one large engine project (§5), so it needn't block v1; but when you build *any* floating surface, build *this* one. Users lived without Spotify's Miniplayer until 2024, so a loved v1 can ship without it — **but do not pretend in-window PiP closes this gap, and do not defer it indefinitely.** |
| **Fullscreen theatre** | **should-have** | Sustained multi-year demand, but a proven regression zone (every fullscreen complaint found was a rendering bug or dropped-lyrics regression). User-invoked only (never automatic); keep lyrics/track-info visible; budget extra QA for this path and its transitions. |
| **In-window movable/resizable PiP** | **should-have / cheap bridge** | Cheap to build and genuinely useful for in-app layout flexibility, ultrawide, and lyrics-alongside-video. But it is **not** a substitute for the detached window (can't stay visible over other apps). If the detached window is on the near roadmap, this is largely subsumed and can be skipped. **Sequencing risk:** shipping it as a stopgap can cannibalize the real solution and leave the top want permanently unmet. |
| **Ambient / Canvas looping visuals** | **defer** | The most divisive feature in the whole space and the loudest hate source. If shipped at all: default-off with a persistent global kill-switch that sticks across updates. Never force it. |

**Invest ahead of parity in one thing: lyrics-over-video** (synced overlay, subtitle-style, in *both* inline
and fullscreen). It is a repeatedly-refiled, still-unmet want that no competitor delivers, and it converts
fullscreen's lyrics-drop regression into a differentiator.

## 4. Design principles (what separates loved from hated)

1. **Audio is the source of truth; video is an optional visual layer** bound to the same transport. Enabling/
   disabling video never restarts, reseeks, or drops the audio session, queue, or position. *(FluentGpu note:
   the DRM plan's master-audio-synced design — `FluentMediaAudioHost` stays the clock, video slaves to it —
   satisfies this by construction; the video engine can start/stop without touching audio.)*
2. **Never force a video state the user didn't choose.** The audio/video choice is sticky per session with a
   global default; "audio only" / dismiss is always one gesture away.
3. **Never seize screen space the user didn't offer.** Inline stays small and dismissible; fullscreen and the
   floating window are user-invoked, never automatic.
4. **State-aware floating.** The floating window asserts always-on-top only while there's video worth watching
   and gets out of the way when idle/paused (the mpv pattern); it never fights the user's chosen size/position
   (avoid VLC's auto-resize-to-video-resolution takeover) and never steals focus.
5. **Reliability before richness.** Working transport controls, a correct default size, smooth open/close, no
   resize/fullscreen-transition stutter, no A/V drift. Buggy-mini-player complaints track as strongly as feature
   requests.
6. **Information parity across surfaces.** Any lyrics/track info available inline stays available in fullscreen
   and in the floating window. Solve lyrics-over-video rather than dropping lyrics when video appears.
7. **One floating surface, not two.** Build the detached always-on-top window as the floating primitive rather
   than shipping a separate limited in-window box users perceive as a downgrade.
8. **Don't paywall the core floating/PiP capability.**
9. **DRM/HDCP protected-frame behavior is baseline, not an edge case** — protected buffers capture black;
   communicate, don't surprise.

## 5. Engineering reality (FluentGpu today vs the gap)

The hard constraint: **video composites only under the *primary* window's swapchain.**
`DCompVideoPresenter.AttachChild` inserts every video child visual under
`D3D12Device.PrimarySwapchain.DcompRoot`, and `VideoBinding` is inert on a non-primary/non-composited window.

| Surface | Buildable on the shipped engine? | Notes |
|---|---|---|
| Inline now-playing | **Yes (app code)** | `MediaPlayerElement` (`FluentGpu.Controls/Media/`) + `VideoBinding.Place/SetViewport/SetContentSize`; fit modes done. |
| Fullscreen theatre | **Yes (app code)** | In-window modal `OverlayHost` view + OS borderless-fullscreen (`hooks.WindowSetFullscreen`); surface shared via `VideoSurfaceRegistry.TransferOwnership`. The shipped `MediaPlayerElement` already does this inline↔fullscreen. |
| In-window movable/resizable PiP | **Yes (app code)** | A top-Z `OverlayHost` child using `BoxEl.OnDrag`/`Transform`/`OffsetX/Y` (the `SidebarResizeGrip` pattern) driving `VideoBinding.Place(rect)` each frame; the DComp child follows because it stays under the primary swapchain. Persists across nav (retained registry slot). |
| **Detached always-on-top OS window** | **No — engine project** | Requires: a real **second top-level `Win32Window`** in hosting (today `FluentApp` is single-window by design); a **second swapchain + DComp root** in `D3D12Device`; a **per-window `IVideoPresenter`** (or one that targets a non-primary swapchain); **always-on-top** plumbing (`SetWindowPos(HWND_TOPMOST)`, state-aware); cross-monitor drag/resize + geometry persistence; and **protected-surface handling in the second window** (the native in-process handle is still valid — same process — but it must bind under the second window's DComp root, and HDCP/output-protection applies per output). Scope it as its own engine milestone. |

Arbitration across all in-window surfaces reuses `VideoSurfaceRegistry.TransferOwnership` (single-writer pump
ownership of one slot) — the engine already does this for inline↔fullscreen; extend it to inline↔PiP↔fullscreen.
The detached window, when built, becomes a *second* presenter/slot.

## 6. Sequenced roadmap

1. **P1 — Inline + control + reliability (the loved v1 foundation).** Inline now-playing surface; sticky
   per-session audio/video toggle; **global "never autoplay video" default, on from day one**; and the
   reliability contract: toggling video / minimizing / backgrounding never restarts, reseeks, or drops
   queue/position (naturally satisfied by the master-audio design). Ship the video button that already exists
   as a seam (`PlayerBar` `PreferVideo`).
2. **P2 — Fullscreen theatre**, user-invoked, lyrics/track-info retained, with the QA budget the regression
   history demands (rendering + transition stutter + A/V drift).
3. **P3 — Lyrics-over-video overlay** (the differentiator) in both inline and fullscreen.
4. **P4 — In-window PiP** *only if* the detached window is far off (a cheap bridge for ultrawide/layout). If
   P5 is on the near roadmap, **skip P4** to avoid cannibalizing it.
5. **P5 — Detached always-on-top floating window** (the engine project, §5). The marquee multitasking feature;
   sequence early, do not defer indefinitely.
6. **(optional) Ambient/Canvas** — default-off, sticky global kill-switch, or omit.

## 7. Open tensions (the user's call)

- **Detached window vs in-window PiP.** Value and cost point opposite ways: the cheap option (in-window PiP)
  does not satisfy the top validated want; the option that does (detached) is the big engine project. The
  recommendation is to build the detached window rather than settle on in-window PiP — but P4 is a legitimate
  bridge if P5 must wait. **The trap:** letting an in-window stopgap indefinitely deprioritize P5.
- **Fullscreen demand is disputed** in the evidence (broad boards show sustained demand; the PiP-focused search
  found no independent fullscreen signal). Resolution: should-have, ranked below the floating surface.
- **Canvas** pits engagement (autoplay demonstrably lifts engagement) against a vocal minority that hates it.
  Default-off is the UX-safe call at some engagement cost.
- **Always-on-top** is both the reason the floating window is loved and a hazard if it doesn't stay put, fights
  focus, or blocks work — hence the state-aware rule (principle 4).

## 8. Decision for the user

Given the verdict, the concrete question is **how far to commit to the detached floating window**:
- **(A) Foundation-first (recommended):** ship P1–P3 on the shipped engine now; commit the detached window
  (P5) as a *named near-term engine milestone*; **skip the in-window PiP (P4)** so it can't cannibalize P5.
- **(B) Bridge-first:** add the in-window PiP (P4) as a cheap early win, accepting the risk it satisfies
  stakeholders enough to stall P5 (the most-validated want left unmet).
- **(C) Everything, detached-included, up front:** treat the detached window as part of the initial video
  effort, absorbing the engine project into the critical path (largest scope, latest ship).
