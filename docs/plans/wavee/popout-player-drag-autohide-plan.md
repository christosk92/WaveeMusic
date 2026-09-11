# Pop-out player: dragging a chromeless window, and control auto-hide that behaves like a real player

Status: **plan, not implemented** (2026-09-11). Scope: the detached pop-out video window. The auto-hide fix lands in
the engine's `MediaPlayerElement`, so every video surface gets it (docked card, fullscreen, pop-out, the gallery).
Two repos are involved:
- Engine: `C:\WAVEE\fluent-gpu`, branch `feat/ultra-fast-engine`, which has large uncommitted work in progress.
- App: `C:\WAVEE\wavee-0.2.9`, branch `release/0.2.9-perf`.

User report:
1. Pop-out players have no title bar now, and there is nowhere to grab them to move them.
2. The controls' auto-hide and auto-show is badly broken and not smooth like other media players.

---

## 0. TL;DR

| Problem | Root cause (evidence below) | Fix |
|---|---|---|
| Can't drag the pop-out | Engine commit `1f906c906` (2026-09-05) switched the detached window to `CustomFrame: true` (`AppHost.cs:1471-1475`), which removes the OS caption. Its message promises "a thin top drag band", but the diff adds none. Nothing ever calls `SetTitleBarRegions` for a detached window, so `WM_NCHITTEST` (`Win32Platform.cs:2054-2083`) never returns `HTCAPTION`. Only the 8 px `HTTOP` resize band and the L/R/B frame are non-client. | mpv's model. The video stays `HTCLIENT`. A press on the video that travels past the drag box (4 px) calls a new PAL seam, `IPlatformWindow.BeginSystemMove()`, which starts the real OS move loop (`WM_NCLBUTTONDOWN`+`HTCAPTION`). You get Aero Snap, the snap bar and shake. Click still reveals, double-click is still fullscreen, and right-click is still the player menu. |
| Fade is a cut, not a fade | The fade is seeded in a layout effect from `scene.Paint(node).Opacity`. By then the reconciler has already re-asserted the static terminal (`Reconciler.cs:4628`), so `from == to` (`MediaPlayerElement.cs:1005-1014`). | Seed `from` = the live row value (`AnimEngine.TryGetTrackValue`), otherwise the previous terminal. |
| Controls pop up by themselves | Every suppressor also reveals: `Reevaluate()` calls `ShowChrome()` whenever `!CanHide()` (`:356-363`). So a rebuffer, an ABR quality switch or an Opening grace force the chrome visible mid-playback (`:707-747`). | A pure state machine that separates activity (reveals) from holds (keep visible, never reveal). Buffering and opening do neither. |
| Slow mouse movement doesn't show them | The jitter threshold compares each sample with the previous one (`:493-497`). At per-frame or high-rate sampling, deliberate slow motion never exceeds 3 DIP per sample. | Measure the deadzone from the rest point where the chrome hid (VLC `qt-fs-sensitivity` 3 px). |
| They linger after the pointer leaves | `HandleExit` resets flags but never hides (`:1054-1062`). | Leaving hides after a 150 ms debounce (mpv, Chromium and Firefox all hide on leave). |
| The bar "jumps" as it hides | At the hide edge the seek bar switches to the disabled palette, shrinks its thumb and unmounts its ticker (`MediaSeekBar.cs:199,270-279,378`). The volume slider also unmounts (`:1314`). All of this happens at the start of the fade. | The palette follows the model state. Chrome visibility gates input only. |
| Cursor never hides over the pop-out | The pop-out inherits the inline default `CursorAutoHidePolicy.FullscreenOnly`. | The pop-out passes `Always` (mpv's windowed default, `cursor-autohide=1000`). |

---

## 1. Root causes

### 1.1 Dragging: why it stopped, and what the engine offers

**What changed.** The Wavee video files haven't changed since 0.2.6 (`git log -- src/apps/Wavee/Features/Video` lists only
`6a31e9c9`, `823a8e11` and `46efd4a7`). The regression came in through the engine sibling:

```
fluent-gpu 1f906c906  2026-09-05  "detached video window: borderless frame, live flyout anchors, toast scoping"
  "…opts into CustomFrame so it gets the same borderless chrome every other Wavee window has, with a thin top drag
   band (a custom-frame window reports no title-bar hit regions by default, so it would otherwise be undraggable) and a
   matching resize allowance."
  diff (AppHost.cs):  new WindowDesc(…, Composited: true)  →  new WindowDesc(…, Composited: true, CustomFrame: true)
```

The drag band described in the commit message was never written. The only region pusher in either repo is the
`TitleBar` control (`FluentGpu.Controls/TitleBar.cs:560-576`, which pushes islands, then the whole bar as `Caption`). The
pop-out renders no `TitleBar`, and Wavee has no `SetTitleBarRegions` call at all.

**Why that kills dragging.**

- `WM_NCCALCSIZE` with a custom frame gives the caption strip back to the client (`Win32Platform.cs:2034-2052`).
- The custom-frame `WM_NCHITTEST` (`Win32Platform.cs:2054-2083`) checks, in order:
  1. engine caption buttons;
  2. the top resize band (`pt.y < SM_CXPADDEDBORDER + SM_CYSIZEFRAME`, about 8 px) → `HTTOP*`;
  3. the engine-reported regions via `HitTestRegions` (`:1011-1031`). There are 0 of them for the detached window, because nobody calls `SetTitleBarRegions`.
- Otherwise the handler falls through to `DefWindowProc`, which answers `HTCLIENT` for the whole client.
- So no pixel is `HTCAPTION`, and `DefWindowProc` never enters the `SC_MOVE` loop.
- Before `1f906c906` the window was plain `WS_OVERLAPPEDWINDOW`. The OS caption was the drag surface, which is what the doc comment on
  `PopOutVideoWindow.cs:12-16` still says ("The OS window frame handles move/resize/close"). That comment is now stale.

**What the engine already offers for drag regions.**

- `InputHooks.SetTitleBarRegions(TitleBarRegion[] , count)` (`Context.cs:257-260`) → `IPlatformWindow.SetTitleBarRegions` (`Pal.cs:727-732`).
- It takes `TitleBarHit.{Client, Caption, MinButton, MaxButton, CloseButton}` (`Pal.cs:150-158`).
- `WM_NCHITTEST` honours it. It is wired for every `AppHost`, including the detached child (`AppHost.cs:2469`), so the app
  could push a `Caption` region today.

**It is the wrong tool for a video surface.** Turning the video into non-client area (Electron `app-region: drag`, or
Chrome PiP's `NonClientHitTest → HTCAPTION`) breaks everything the auto-hide and the player need:

| Consequence of `HTCAPTION` over the video | Where it bites here |
|---|---|
| Moves arrive as `WM_NC*MOVE`. Crossing into the caption counts as leaving the client (`WM_MOUSELEAVE` docs). | `NcHover` synthesizes a PointerMove to `OffscreenDip` on every client→NC crossing (`Win32Platform.cs:1072-1088`). The element would think the pointer left the whole video, so no auto-show. |
| The engine cursor only applies over `HTCLIENT` | `WM_SETCURSOR` → `ApplyCursor()` only `if hit == HTCLIENT` (`:2022-2025`), so the cursor could never hide over the video. |
| Right-click on the caption opens the system menu | `WM_NCRBUTTONUP` + `HTCAPTION` → `TrackPopupMenu(sys)` (`:2154-2174`), replacing the player's ⋯/context menu. |
| Double-click on the caption maximizes | `WM_NCLBUTTONDBLCLK` → `DefWindowProc` → `SC_MAXIMIZE` (MS docs), where a player expects fullscreen (mpv `MBTN_LEFT_DBL cycle fullscreen`, Firefox PiP `dblclick → fullscreenModeToggle`). |
| A press never reaches the client | No click-to-reveal, no touch tap toggle, no player double-click. |
| A click without movement stalls rendering | Chromium hit this: the move loop "appears to be peeking for mouse input only". Chrome defers `DefWindowProc` until a `WM_NCMOUSEMOVE` (codereview 1504333013). |

Chrome PiP pays all of these costs with views-level NC→client mouse forwarding. mpv avoids them.
`borderless_nchittest` returns `HTCLIENT` for the interior and `HT*` only on the edges. Window dragging is
`ReleaseCapture(); SendMessage(WM_NCLBUTTONDOWN, HTCAPTION, 0)`, issued only after the mouse moves `dragging_deadzone = 3`
px with the button held and not over the OSC (`w32_common.c`, `input.c`). **This plan adopts mpv's model.**

### 1.2 Auto-hide: how it works today (engine `MediaPlayerElement`)

```
                                ┌─────────────────────── Reevaluate()  (MediaPlayerElement.cs:356-366) ──────────────────┐
 pointer move (:488-508) ──┐    │  if !CanHide():  CancelHide(); ShowChrome(); ShowCursor();   ← EVERY suppressor REVEALS │
 press/release/exit ───────┤    │  else if visible && !_hideArmed:  ArmHide()  (_hideFast 3000 ms | _hideSlow 4000 ms)    │
 keys (:1551-1553) ────────┼───▶│                                                                                        │
 focus/menu/scrub ─────────┤    │  CanHide() (:343-348) = enabled ∧ ¬a11y ∧ ¬notPlaying ∧ ¬buffering ∧ ¬seekInFlight     │
 buffering/opening effect ─┘    │      ∧ ¬audioOnlyOrError ∧ ¬forceShow ∧ ¬overChrome ∧ ¬pointerDown ∧ ¬scrub ∧ ¬menu    │
   (:707-753)                   │      ∧ ¬focusInChrome ∧ ¬moveBurst                                                     │
                                └────────────────────────────────────────────────────────────────────────────────────────┘
 OnHideDue (:393-399): chromeVisible=false → _cursorTimer 400 ms → OnCursorDue (:401-406) hides cursor if policy allows
 Render reads chromeVisible.Value (:747) → WHOLE element re-renders on every flip:
   media-chrome (:985-998)  HitTestVisible = showChrome, Opacity = showChrome ? 1 : 0   (static terminal)
   BuildTransport(interactive: showChrome) → every button's Role/TabStop/IsEnabled flips (:1836-1881); the volume
   slider unmounts (:1314); MediaSeekBar.ChromeVisible → enabled=false → disabled palette + thumb shrink + ticker unmount
 UseLayoutEffect(showChrome) (:1005-1014): SeedEased(node, Opacity, from: scene.Paint(node).Opacity, to: terminal)
 Tokens (MotionTok.cs:127-153,179-180): dwell 3000 / touch 4000 / focus 4000, cursor +400, threshold 3 DIP,
   reveal 100 ms FluentDecelerate, conceal 200 ms FluentAccelerate (= Fluent exit curve cubic-bezier(1,0,1,1))
```

**Defects, each tied to a symptom:**

- **D1: the fade is a cut.**
  - Frame order: `FlushHosted` (render + reconcile, `AppHost.cs:3482`) → layout → `DrainLayoutEffects` (`:3549`) → animation tick.
  - The reconciler re-asserts static opacity on every patch: `if (!b.Opacity.IsBound) paint.Opacity = b.Opacity.Value;` (`Reconciler.cs:4626-4628`, from June). The fade code came later, in August (`e7cff4dd3`).
  - When the layout effect runs, `scene.Paint(node).Opacity` already equals the new terminal. So `SeedEased(from == to)`, and the chrome disappears (and appears) in one frame.
  - Under the WIP render-thread compositor animations, UI-side paint holds the authored base, not the live value, which is a second reason that read is wrong.
  - `AnimEngine.TryGetTrackValue` (`AnimScheduler.cs:449-456`) exists for exactly this: "so an interrupting tween departs from where it is".
  - No gate caught it. The `gate.media.el.*` checks only sample settled terminals (`ControlsSuite.cs:8797-8833`, `9143-9197`).

  ```
   frame N (hide edge)
    3–5  FlushHosted → Render → reconcile media-chrome → paint.Opacity = 0      (Reconciler.cs:4628)
    6    layout
    6.5  DrainLayoutEffects → SeedEased(from: Paint.Opacity (=0), to: 0)          (MediaPlayerElement.cs:1013)
    7    anim tick composes 0 → record → the transport vanished in ONE frame
  ```

- **D2: holds reveal.** `Reevaluate` shows the chrome for any `!CanHide()`.
  - The protected session maps Licensed+Buffering onto `PlaybackState.Buffering` and samples every 250 ms (comment `:703-705`).
  - ABR switches ("480p (Auto)" in the screenshot) report `BufferingReason.QualitySwitch`.
  - `bufferSuppress` → `_buffering` → effect `Reevaluate` → `ShowChrome`. The controls pop up with no user input and then idle away 3 s later.
  - The same path runs through the Opening grace (`_forceShow`, `:721-733`), `IsStoppedState(Ready)`, and `audioOnly` from a not-yet-known `NaturalSize`.
  - No reference player reveals on buffering: Chromium's `ShouldHideMediaControls` has no buffering clause, and Media3's timeout runs "with playback or buffering in progress".
- **D3: the per-sample deadzone.**
  - `if (hidden && dx²+dy² < 3²) { _last = p; return; }` compares with the previous sample (`:493-497`).
  - At 60 Hz coalesced moves, anything under about 180 DIP/s is invisible; at 1 kHz raw mouse samples, almost everything is.
  - Symptom: the controls don't come back unless you wiggle the mouse.
- **D4: leave doesn't hide.**
  - `HandleExit` (`:1054-1062`) only re-evaluates. The already-armed dwell keeps running, so the transport stays over the video of an always-on-top window you've moved away from, for up to 3 s.
  - mpv ("will hide … if the mouse leaves the window"), Chromium, Chrome PiP (`kMouseExited`) and Firefox all hide on leave.
- **D5: visual churn at the conceal edge.**
  - Seek rail: `RailFillDisabled`/`ValueFillDisabled`, `InnerDisabledScale`, and the ticker unmounted, all at the start of the fade (`MediaSeekBar.cs:198-199, 270-279, 378-379`).
  - The volume slider unmounts (`MediaPlayerElement.cs:1314`), so the control row reflows mid-fade.
- **D6: cursor.**
  - The pop-out keeps the inline default `CursorAutoHidePolicy.FullscreenOnly` (`:215-218`), so the cursor never hides over it.
- **D7: stuck press.**
  - `OnPointerReleased` fires only on a release over the same owner (`InputDispatcher.cs:1049-1054`); a drag or capture loss suppresses it (`Element.cs:173-175`).
  - `_pointerDown` then stays true, and S4 pins the chrome until an exit. Today that happens when you press on the video trying to drag the window and release outside it. The new drag gesture would hit it on every drag unless the move start is handled explicitly.
- **D8: conceal curve.**
  - `FluentAccelerate` (`cubic-bezier(1,0,1,1)`) over 200 ms stays nearly flat and then drops. Even when it runs, it reads as a pop.
- **Dead code:** `ChromeMotion` (`:71-76`) is declared and never used.

Related, but not in the pop-out: the fullscreen surface's title/exit band fades on hover (`HoverOpacity`,
`VideoFullscreenSurface.cs:297-313`), not on the element's idle state. Its doc says it "rides the SAME gate", which is not
true: the band stays up while the transport idles away. The P3 follow-up in §9.2 addresses it.

---

## 2. How mature players do it (research digest)

**Dragging and resizing**

| Topic | Evidence |
|---|---|
| Hit-test semantics | `HTCAPTION`=2 is "in a title bar". The message goes "to the window beneath the cursor" unless captured. Use `GET_X_LPARAM` for multi-monitor [MS WM_NCHITTEST]. Removing the standard frame loses move/resize, and you must return `HTCAPTION`/`HT*` yourself [MS Custom Window Frame Using DWM]. |
| What the system move loop gives | Snap to the edges, the snap bar when you drag to the top, and title-bar shake [support.microsoft.com Snap; Accessibility]. The loop is modal: "The operation is complete when DefWindowProc returns" [MS WM_ENTERSIZEMOVE]. Snap *layouts* need `HTMAXBUTTON`, or Win+Z [MS apply-snap-layout-menu]. |
| mpv (Windows) | The interior is `HTCLIENT`. `HT*` codes only on edges, band `SM_CXSIZEFRAME` (+`SM_CXPADDEDBORDER`). Dragging is `ReleaseCapture(); SendMessage(WM_NCLBUTTONDOWN, HTCAPTION, 0)`, skipped in fullscreen and over the OSC [w32_common.c]. `window-dragging` default on, `dragging_deadzone = 3`, and the double-click is cancelled once a drag begins [input.c]. `MBTN_LEFT_DBL cycle fullscreen`; click-to-pause was rejected [input.conf; PR #15405]. `keepaspect-window` handled via `WM_SIZING` [w32_common.c, options.c]. |
| Chrome video PiP | `HTCAPTION` everywhere except the controls. Resize band 10 px, corners 16 px, aspect locked via `SetAspectRatio` [video_overlay_window_views.cc]. |
| Firefox PiP | `#controls { -moz-window-dragging: drag }`, `.control-item { no-drag }`, double-click toggles fullscreen [player.css, player.js]. Aspect-locked resize on Windows [bug 1535437]. |
| MPC-HC | "move the player window by dragging the video area" [1.7.2 changelog; PR #181]. |
| VLC | Borderless: "you can't move the window" [third-party README]. |
| Pitfalls | DComp/`WS_EX_NOREDIRECTIONBITMAP` windows hit-test uniformly, so you do your own geometry [Kerr, MSDN 2014]. `WM_NCHITTEST` must be side-effect free [Old New Thing 2011]. Spurious `WM_MOUSEMOVE` on show/hide/move, so compare positions [Old New Thing 2003]. `SC_MOVE\|HTCAPTION` (0xF012) is undocumented [MS WM_SYSCOMMAND]. `SM_CXDRAG` is the drag box [MS GetSystemMetrics, DragDetect]. |

**Auto-hide**

| Player | Idle timeout | On leave | While paused | Hover over controls | Fades | Cursor | Notes |
|---|---|---|---|---|---|---|---|
| mpv OSC | `hidetimeout` **500 ms** | hides immediately (#17536, won't fix) | no documented exception | keeps it visible | `fadeduration` 200 ms, `fadein=no` | `cursor-autohide` 1000 ms, windowed too unless `-fs-only` | `minmousemove` 0 |
| Chromium media controls | **2.5 s** (`kTimeWithoutMouseMovementBeforeHidingMediaControls`, was 3 s) | pointerout → transparent immediately if playing | never hides (`paused()` early-return) | never hides; also never during `Seeking()` or with focus inside | in 250 ms, out 1 s, `cubic-bezier(.25,.1,.25,1)` | — | pointermove restarts the timer |
| Chrome PiP window | 2500 ms | `kMouseExited` hides | — | — | — | — | `kMouseEntered`/moved shows |
| Firefox inline | 2000 ms (+500 ms show-hover debounce) | fades immediately | still hides (bug 474833 WONTFIX) | — | 200 ms | — | no hide while `scrubber.isDragging` |
| Firefox PiP | **3000 ms** | hides unless SHOWING/KEYING/DONTHIDE | `revealControls(true)`, stays visible | — | 160 ms linear | — | — |
| VLC | `mouse-hide-timeout` 1000 ms (cursor + fs controller) | — | — | — | — | hidden together with the controller | `qt-fs-sensitivity` 3 px |
| Media3 (touch) | `show_timeout` 5000 ms | — | shown indefinitely when paused/ended/idle | — | — | — | `hide_on_touch` (tap toggles) |
| WinUI MTC | `ShowAndHideAutomatically` default true, "after a short period of no user interaction" (exact value not verified) | — | — | — | — | — | — |

Motion guidance:
- Fluent: 83 / 167 / 250 ms. Enter curve `cubic-bezier(0,0,0,1)`, exit curve `cubic-bezier(1,0,1,1)`.
- Material: desktop 150–200 ms; anything over 400 ms "may feel too slow".

What mature players agree on:
- Show on movement, not on presence.
- Ignore jitter.
- Never hide while hovered, scrubbing or with focus inside.
- Hide on leave.
- Pause keeps the controls up (Chromium, Firefox PiP, Media3; mpv differs).
- Tap toggles on touch.

---

## 3. Target behaviour (the spec the machine implements)

| # | Event (playing unless stated) | Controls | Cursor (pop-out `Always`; inline `FullscreenOnly`) | Basis |
|---|---|---|---|---|
| 1 | Surface mounts / pop-out opens | visible, **3 s** dwell | shown | Firefox PiP opens showing its controls |
| 2 | Pointer enters the player | reveal instantly (fade-in **150 ms**), 3 s dwell | shown | Chrome PiP `kMouseEntered`; mpv "whenever the mouse is moved inside" |
| 3 | Move ≥ **3 DIP** from where the pointer rested when the chrome hid | reveal, dwell restarts | shown | VLC `qt-fs-sensitivity` 3; mpv drag deadzone 3 |
| 4 | Jitter < 3 DIP from the rest point, or a same-point re-delivery | nothing | stays hidden | Old New Thing spurious moves |
| 5 | Any real move while visible | dwell restarts from this move | shown | Chromium: pointermove restarts the timer |
| 6 | 3 s without activity (touch reveal 4 s, keyboard reveal 4 s) | conceal (fade-out **400 ms**, ease-out) | hides when the fade ends (+400 ms), only if the pointer is inside | Firefox PiP 3000; Chromium 2500; fades between mpv 200 and Chromium 1000 |
| 7 | Pointer resting on the controls | never hides; leaving them restarts the dwell | shown | mpv, Chromium, Firefox |
| 8 | Pointer leaves the window, including onto the resize border or the 8 px top band, or the window blurs | conceal after **150 ms** | shown (it's outside) | mpv, Chromium, Chrome PiP, Firefox hide on leave; the 150 ms only absorbs a client↔NC-band crossing and overlay-hold races |
| 9 | Hover taken by an overlay scrim while the pointer is still over the player | nothing (clears the over-controls and pressed holds) | — | engine-specific: `OnPointerExit` also fires when a scrim covers the player |
| 10 | Primary press on the video, no travel | reveal; held while down; release restarts the dwell | shown | "reveal-only click" (existing rationale; mpv PR #15405) |
| 11 | Press plus travel > **4 px** on the video (pop-out, windowed, mouse/pen) | held during the OS move loop; when the loop ends, reveal and 3 s dwell | shown | mpv window-dragging; `SM_CXDRAG`=4 (`InputDispatcher.ClickSlopPx`) |
| 12 | Double-click on the video | fullscreen toggle (on this window's monitor), not maximize | — | mpv `MBTN_LEFT_DBL`; Firefox PiP |
| 13 | Press within 500 ms after a window move | never a double-click | — | mpv clears `last_doubleclick_time` on drag |
| 14 | Wheel over the video | reveal (volume; Shift = seek) | shown | existing |
| 15 | Handled key (Space/K/J/L/arrows/M/F/…) | reveal, 4 s dwell | shown | YouTube: controls reappear when the user "presses a key" |
| 16 | Tab focus into the controls | reveal, held while keyboard focus is inside | shown | Chromium focus clause; WCAG 2.4.7 |
| 17 | Menu or picker open (⋯, 1×, quality, CC, audio) | held; closing restarts the dwell | shown | Chromium panel `IsWanted`; Firefox DONTHIDE |
| 18 | Seek drag or seek in flight (until the player confirms) | held | shown | Chromium `Seeking()`; Firefox `isDragging` |
| 19 | Paused / Ended / Failed / audio-only | reveal, held (never auto-hides) | shown | Chromium `paused()`; Firefox PiP; Media3 "indefinitely" |
| 20 | Resume | fresh 3 s dwell from the resume edge | — | Media3 |
| 21 | Buffering, stall, ABR switch, Opening (new track) | **neither reveal nor hold**; the spinner speaks | unchanged | Chromium (no buffering clause); Media3 |
| 22 | Touch tap on the video | hidden → reveal (4 s); visible → conceal, unless a hard hold (menu, scrub, keyboard focus, AT) | — | Media3 `hide_on_touch`; Chromium `kGestureTap` |
| 23 | Screen reader / AT active | never hides | shown | existing S11 |
| 24 | Fullscreen entered while hidden | — | hides now (not on the next jiggle) | existing |
| 25 | Reduced motion | fades snap | — | engine policy (`MediaChrome*` tokens are SnapEnd) |

Interrupted fades retarget from the live value. Moving mid-conceal reverses from wherever the pixels are.

---

## 4. Design

### 4.1 Diagrams

**Pop-out wireframe (hit-test zones):**

```
 ┌─ HTTOP band (8 px, inside the client — Win32Platform.cs:2070-2078) ─────────────────────────────────────── [unchanged] ┐
 │ ░ L/R/B: DefWindowProc's thin resize frame (non-client, outside the client) ░                            [unchanged]   │
 │                                                                                                                         │
 │                        VIDEO — HTCLIENT (every hover move reaches the engine → auto-show works)                         │
 │           press ─┬─ release, no travel          → click = reveal         · 2nd click (≤500 ms) = fullscreen             │
 │                  └─ travel > 4 px (SM_CXDRAG)   → IPlatformWindow.BeginSystemMove() → OS SC_MOVE loop                   │
 │                                                    Aero Snap · snap bar · shake · monitor hop · drag-to-restore         │
 │ ┌─ media-chrome (HitTestPassThrough; hit-testable only while visible; opacity-only fade) ──────────────────────────────┐ │
 │ │ ━━━━━━━━━━━━━━━━━━━━━●━━━━━━━━━━━━━━━━━━━━━━━━━━  seek rail — its own press target: drag = seek, never a move       │ │
 │ │  ▶  −10  +10  🔊━━  0:25 / 4:24                                   1×   480p (Auto)   ⋯   ⛶                          │ │
 │ └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ │
 └─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   keyboard: Alt+Space → Move/Close still works (DefWindowProc SC_KEYMENU is not intercepted); Win+Z → snap layouts
```

**Component tree (pop-out):**

```
PopOutVideoWindow                         (detached child AppHost root)
└─ OverlayHost
   └─ PopOutVideoContent                  Fill = MediaLetterbox, viewport-sized
      └─ BoxEl ClipToBounds
         └─ PopOutVideoStage              key "stage:gen:N:f0|f1"
            └─ MediaPlayerElement         key "player:gen:N:t1:f0|f1"
                 DragMovesWindow = VideoStageInput.DragMovesWindow(PopOut, fullscreen)   ← NEW (true windowed)
                 CursorAutoHide  = VideoStageInput.Cursor(PopOut) = Always                ← NEW
               frame  Focusable · OnKeyDown · OnPointerMoveWithin · OnPointerPressed/Released/Exit · OnPointerWheel
                      OnPointerDown + OnDrag  (only when DragMovesWindow — the move gesture)  ← NEW
               ├─ videoArea (ZStack)   media-stage · media-hole (VideoHole) · media-poster · status · caption
               └─ media-chrome         HitTestVisible = visible · Opacity = terminal · fade seeded from the LIVE value
                  └─ transport         ScrimBottom · OnPointerMoveWithin/Exit → over-controls hold
                     ├─ media-seek-row → MediaSeekBar   palette from MODEL; input from chrome   ← CHANGED
                     └─ media-control-row  play · −10 · +10 · volume(+slider) · time · spacer · 1× · 480p · ⋯ · ⛶
```

**Chrome state machine:**

```
          activity: enter · move ≥3 DIP from rest point · press · wheel · handled key · tap(hidden) · kbd focus/AT gained
                    · Paused/Ended/Failed/AudioOnly entered · OS move loop start/end
      ┌─────────────────────────────────────────────────────────────────────────────────────────────┐
      ▼                                                                                             │
 ┌──────────┐  Tick: now ≥ hideAt ∧ Holds = ∅           ┌──────────┐  now ≥ cursorAt (+400 ms)   ┌──────────────────┐
 │ VISIBLE  │ ────────────────────────────────────────▶│  HIDDEN  │ ∧ pointer inside ─────────▶ │ HIDDEN + CURSOR  │
 │ hideAt = │  hideAt = lastActivity + dwell(3|4|4 s)   │  anchor  │ ∧ policy allows             │     HIDDEN       │
 │ act+dwell│  leave: hideAt = min(hideAt, now+150)     │ = rest pt│                             └──────────────────┘
 └──────────┘  tap while visible ∧ ¬hard hold ──────────▶                   activity ◀──────────────────┘
      │ Holds (never reveal; release ⇒ hideAt = max(hideAt, now+dwell)):
      │ Disabled · Accessibility · Stopped · OverControls · Pressed · Scrubbing · Menu · KeyboardFocus · WindowMove
```

**Drag sequence:**

```
 user          dispatcher (UI, frame N)            MediaPlayerElement                  Win32Window                     OS
 press video ─▶ _down = _dragTarget = frame ─▶ OnPointerDown(origin); OnPointerPressed → vis.SetPressed(true)
 move 2 px  ─▶ OnDrag(local)                ─▶ |Δ| ≤ 4 → nothing
 move 9 px  ─▶ OnDrag(local)                ─▶ vis.WindowMoveStarted; hooks.WindowBeginMove() ─▶ enqueue PointerCancel(id)
                                                                                                 ReleaseCapture()
                                                                                                 PostMessage(WM_NCLBUTTONDOWN,
                                                                                                   HTCAPTION, cursor)
 frame N+1:     PointerCancel → CancelWorkingContact → frame.OnPointerExit → vis.PointerCovered (not a leave)
                PumpInto → WndProc(WM_NCLBUTTONDOWN) → no engine button → DefWindowProc ───────────────▶ SC_MOVE loop
 drag/snap ◀────────────────────────────────────────────────────────────────────────────── (modal; DWM moves the DComp surface)
 release   ◀── WM_EXITSIZEMOVE → enqueue WindowMoveSizeEnded → dispatcher → hooks.WindowMoveSizeEndedObserved
                                              vis.WindowMoveEnded → reveal + 3 s dwell → Sync
```

### 4.2 Engine seams (fluent-gpu). Small and additive, in files that already have uncommitted WIP.

**`src/FluentGpu.Engine/Seams/Pal/Pal.cs`**, in `IPlatformWindow` beside `SetFullscreen`/`CloseWindow`:

```csharp
    /// <summary>Start the OS interactive MOVE loop for this window from the current pointer, exactly as if the user had
    /// pressed a caption the window does not draw — so Aero Snap, the Windows 11 snap bar, title-bar shake, monitor hops and
    /// drag-to-restore all work, because it IS the system loop. For chromeless windows whose content decides what is
    /// draggable (the pop-out video: a press on the picture that travels past the drag box). Call only while the primary
    /// mouse/pen button is held. ASYNCHRONOUS: the loop starts when the window next pumps messages, never re-entrantly
    /// inside the current frame; the backend also cancels the engine contact the loop is about to capture. No-op while
    /// fullscreen, and on backends without one (macOS maps to <c>-[NSWindow performWindowDragWithEvent:]</c>).</summary>
    void BeginSystemMove() { }
```

And in `InputKind` (after `ScrollEnd = 14`):

```csharp
    /// <summary>An OS move/size modal loop ended (Win32 WM_EXITSIZEMOVE) — EVERY loop, edge resizes included. Consumers
    /// that started one (<see cref="IPlatformWindow.BeginSystemMove"/>) use it as the gesture's end; others ignore it.
    /// Never coalesces.</summary>
    WindowMoveSizeEnded = 15,
```

**`src/FluentGpu.Windows/Pal/Win32Platform.cs`:**

```csharp
    // The primary mouse/pen contact currently down in the client (set/cleared in PointerDownUp, cleared on
    // WM_POINTERCAPTURECHANGED) — the contact an OS move loop started from it will capture.
    private uint _primaryDownId;
    private bool _primaryDown;
    private bool _engineMoveLoop;   // the current modal loop was started by BeginSystemMove (log pairing only)

    public void BeginSystemMove()
    {
        if (_fullscreen || _closed || !_primaryDown) return;
        POINT pt;
        if (GetCursorPos(&pt) == 0) return;
        // Release the ENGINE contact deterministically instead of trusting WM_POINTERCAPTURECHANGED to arrive once the loop
        // captures: a press the dispatcher never sees end keeps _down/_dragTarget set, which suppresses OnPointerMoveWithin
        // for the rest of the session (InputDispatcher.cs:905-906) — auto-show would die after the first drag. A second
        // cancel from the real capture change is a no-op on an idle slot.
        _queue.Enqueue(new InputEvent(InputKind.PointerCancel, default, 0, 0,
            Pointer: PointerKindOf(_primaryDownId), TimestampMs: Now(), PointerId: _primaryDownId));
        _primaryDown = false;
        _engineMoveLoop = true;
        ReleaseCapture();   // mpv's pair (w32_common.c begin_dragging); a no-op under mouse-in-pointer, kept for parity
        // POSTED, never sent: the caller is the dispatcher, mid-RunFrame; SendMessage would run the modal loop re-entrantly
        // under the frame. The message reaches DefWindowProc through the WM_NCLBUTTONDOWN case below (no engine caption
        // button under the point → NcPress returns false → DefWindowProc) — the documented caption press, not the
        // undocumented SC_MOVE|HTCAPTION (0xF012) syscommand.
        nint lp = (nint)(((uint)(ushort)(short)pt.y << 16) | (ushort)(short)pt.x);
        PostMessageW(_hwnd, WM_NCLBUTTONDOWN, (WPARAM)(nuint)HTCAPTION, (LPARAM)lp);
        Diag.Line($"[window.move] begin at=({pt.x},{pt.y})");   // always-on, per gesture (not per frame)
    }
```

In `WM_EXITSIZEMOVE` (`:1849-1858`), add:

```csharp
                _queue.Enqueue(new InputEvent(InputKind.WindowMoveSizeEnded, default, 0, 0, TimestampMs: Now()));
                if (_engineMoveLoop) { _engineMoveLoop = false; Diag.Line("[window.move] end"); }
```

In `PointerDownUp` (`:2234`): for a `PointerKind.Mouse`/`Pen` primary-button down, set `_primaryDownId = pointerId; _primaryDown = true;`.
On up, or on `WM_POINTERCAPTURECHANGED` for that id, set `_primaryDown = false`.

**`src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs`** (gate probe, next to `SetFullscreenCount`):

```csharp
    public int BeginSystemMoveCount { get; private set; }
    public void BeginSystemMove() => BeginSystemMoveCount++;
```

**`src/FluentGpu.Engine/Hooks/Context.cs`**, in `InputHooks`, in the custom-titlebar chrome seam block (`:239-266`):

```csharp
    /// <summary>Start the OS move loop for THIS window (host-wired to <c>IPlatformWindow.BeginSystemMove</c>). A control
    /// calls it from a press that travelled past the drag box on its draggable surface — the pop-out video. Null/no-op on
    /// headless, fullscreen, and backends without a move loop.</summary>
    public Action? WindowBeginMove;

    /// <summary>Raised when any OS move/size loop of THIS window ends (<see cref="FluentGpu.Pal.InputKind.WindowMoveSizeEnded"/>).
    /// Subscribe with a cached delegate; unsubscribe on unmount.</summary>
    public event Action? WindowMoveSizeEndedObserved;
    public void NotifyWindowMoveSizeEnded() => WindowMoveSizeEndedObserved?.Invoke();
```

**`src/FluentGpu.Engine/Input/InputDispatcher.cs`:** add `public Action? OnWindowMoveSizeEnded;` and a case next to `WindowStateChanged`:

```csharp
                case InputKind.WindowMoveSizeEnded:
                    OnWindowMoveSizeEnded?.Invoke();
                    break;
```

**`src/FluentGpu.Engine/Hosting/AppHost.cs`**, wiring next to `WindowSetFullscreen` (`:2464`). This runs in every host, so the detached child
wires its own window:

```csharp
        _inputHooks.WindowBeginMove = _window.BeginSystemMove;                        // chromeless windows: a travelled press moves the window
        _dispatcher.OnWindowMoveSizeEnded = _inputHooks.NotifyWindowMoveSizeEnded;    // …and learns when the OS loop is over
```

Optionally, `SampleDetachedBounds` (`:1584`) can also treat `WindowMoveSizeEnded` as the settle point instead of waiting for
10 still frames. That is a separate cleanup, not required.

**`src/FluentGpu.Engine/Hooks/RenderContext.Timers.cs`:** one timer with a computed deadline, on the host clock.

```csharp
public readonly struct TimerHandle
{
    …
    /// <summary>Re-arm from now for <paramref name="ms"/> instead of the hook's declared duration — for a caller whose
    /// deadline is COMPUTED (a pure policy's next wake). Generation-guarded exactly like <see cref="Restart"/>.</summary>
    public void RestartIn(float ms) => _ctl?.RestartIn(ms);

    /// <summary>The host timer clock this handle schedules on (ms). Feed it to pure time-based policies so their deadlines
    /// and this timer agree — the headless harness runs a virtual clock that <c>Environment.TickCount64</c> never sees.</summary>
    public double NowMs => _ctl?.NowMs ?? 0;
}

internal interface ITimerControl { void Cancel(); void Restart(); void RestartIn(float ms); double NowMs { get; } }

// TimeoutCell:
    public void RestartIn(float ms) { Cancel(); Arm(ms); }
    public double NowMs => Queue?.NowMs ?? 0;
```

**Canon.** Register these in `docs/design/SPEC-INDEX.md` §2 and the ownership map in `docs/design/subsystems/README.md`:
- `IPlatformWindow.BeginSystemMove`: owner `subsystems/pal-rhi.md`, in the "Modal loops" section.
- `InputKind.WindowMoveSizeEnded`: owner `subsystems/input-a11y.md`.
- `InputHooks.WindowBeginMove` and `WindowMoveSizeEndedObserved`: owner `input-a11y.md`.
- `TimerHandle.RestartIn` and `TimerHandle.NowMs`: owner `subsystems/reconciler-hooks.md` (line 97 lists `TimerHandle{Cancel,Restart}`).

Then run `powershell -File docs\design\check-canon.ps1`.

### 4.3 The pure state machine: `src/FluentGpu.Controls/Media/PlayerChromeVisibility.cs` (NEW)

It is engine-free: no scene, no hooks, no timers, and no allocation per input. It sits beside its only consumer.
Its tests live in `FluentGpu.Windows.Tests`, which already references Controls and hosts `MediaPlayerElementLogicTests`.

```csharp
using System;
using FluentGpu.Animation;

namespace FluentGpu.Controls.Media;

/// <summary>What revealed the chrome — it picks the dwell. Touch and keyboard reveals get the longer one: neither has a
/// hover that re-arms it while the user reads the controls.</summary>
public enum ChromeActivity : byte { Pointer, Touch, Keyboard }

/// <summary>The playback facts the chrome reacts to. Opening / Buffering / Stalled are deliberately ABSENT — the element
/// maps them to <see cref="Playing"/> (<see cref="MediaPlayerElement.ChromePlaybackOf"/>): a rebuffer, an ABR switch or a
/// slow open neither reveals nor pins the controls; the spinner speaks for them (Chromium hides through buffering; Media3's
/// timeout runs "with playback or buffering in progress").</summary>
public enum ChromePlayback : byte { Playing, Paused, Ended, Failed, AudioOnly }

/// <summary>Why VISIBLE chrome may not hide right now. A hold never reveals hidden chrome — only activity does; that split
/// is the whole fix for "the controls pop up by themselves".</summary>
[Flags]
public enum ChromeHold : ushort
{
    None = 0,
    Disabled = 1 << 0,        // auto-hide off, or no transport drawn on this surface
    Accessibility = 1 << 1,   // an AT client is navigating the controls
    Stopped = 1 << 2,         // paused / ended / failed / audio-only
    OverControls = 1 << 3,    // pointer resting on the control panel
    Pressed = 1 << 4,         // primary button held on the player
    Scrubbing = 1 << 5,       // seek drag, until the player confirms the committed seek
    Menu = 1 << 6,            // a picker / the ⋯ menu / any input-blocking overlay is up
    KeyboardFocus = 1 << 7,   // Tab focus inside the controls (WCAG 2.4.7)
    WindowMove = 1 << 8,      // the OS move loop a video drag started
}

/// <summary>The timing table (ms / DIP). <see cref="Default"/> reads <see cref="MotionTok"/>; a host overrides the
/// mouse dwell through <c>MediaPlayerElement.TransportControlsHideDelayMs</c>.</summary>
public readonly record struct PlayerChromeTiming(
    double IdleHideMs, double TouchIdleHideMs, double KeyboardIdleHideMs,
    double LeaveHideMs, double CursorTrailMs, float DeadzoneDip)
{
    public static PlayerChromeTiming Default { get; } = new(
        MotionTok.MediaChromeIdleDelayMs, MotionTok.MediaChromeIdleDelayTouchMs, MotionTok.MediaChromeIdleDelayAfterFocusMs,
        MotionTok.MediaChromeLeaveHideMs, MotionTok.MediaChromeFadeOutMs, MotionTok.MediaChromeMoveThresholdDip);
}

/// <summary>
/// The media transport's show/hide policy as a pure clocked state machine: inputs in (with the host timer clock's
/// <c>now</c>), <see cref="ChromeVisible"/> / <see cref="CursorHidden"/> / <see cref="NextWakeMs"/> out. No timers inside —
/// the owner arms ONE timer at <see cref="NextWakeMs"/> and calls <see cref="Tick"/> when it fires.
/// <list type="bullet">
/// <item>ACTIVITY reveals and restarts the dwell: entering, a move that leaves the rest-point deadzone, press, wheel, a
/// handled key, a tap on hidden chrome, keyboard focus / AT gained, a user-visible stop, an OS move loop's start/end.</item>
/// <item>HOLDS keep visible chrome up and never reveal hidden chrome; releasing one restarts the dwell.</item>
/// <item>Leaving hides after a short debounce; the cursor hides when the conceal fade ends, only inside, only if allowed.</item>
/// </list>
/// </summary>
public sealed class PlayerChromeVisibility
{
    private const ChromeHold HardHolds = ChromeHold.Disabled | ChromeHold.Accessibility | ChromeHold.Scrubbing
                                       | ChromeHold.Menu | ChromeHold.KeyboardFocus | ChromeHold.WindowMove;

    private readonly PlayerChromeTiming _t;
    private bool _visible = true, _cursorHidden;
    private bool _enabled = true, _a11y, _overControls, _pressed, _scrubbing, _menu, _keyboardFocus, _moving;
    private bool _pointerInside, _left, _cursorMayHide;
    private ChromePlayback _playback;                       // Playing
    private double _dwell, _hideAt, _cursorAt = double.PositiveInfinity;
    private float _lastX = float.NaN, _lastY = float.NaN;    // last sample — same-point re-delivery is not activity
    private float _anchorX = float.NaN, _anchorY = float.NaN; // where the pointer rested when the chrome hid

    public PlayerChromeVisibility(in PlayerChromeTiming timing, double nowMs)
    {
        _t = timing;
        _dwell = timing.IdleHideMs;
        _hideAt = nowMs + _dwell;   // a fresh surface shows its controls, then idles away like any reveal
    }

    public bool ChromeVisible => _visible;
    public bool CursorHidden => _cursorHidden;
    /// <summary>Why the last visibility edge happened — a static literal for the always-on <c>[media.chrome]</c> line.</summary>
    public string LastCause { get; private set; } = "mount";

    public ChromeHold Holds
    {
        get
        {
            var h = ChromeHold.None;
            if (!_enabled) h |= ChromeHold.Disabled;
            if (_a11y) h |= ChromeHold.Accessibility;
            if (_playback != ChromePlayback.Playing) h |= ChromeHold.Stopped;
            if (_overControls) h |= ChromeHold.OverControls;
            if (_pressed) h |= ChromeHold.Pressed;
            if (_scrubbing) h |= ChromeHold.Scrubbing;
            if (_menu) h |= ChromeHold.Menu;
            if (_keyboardFocus) h |= ChromeHold.KeyboardFocus;
            if (_moving) h |= ChromeHold.WindowMove;
            return h;
        }
    }

    /// <summary>The next instant <see cref="Tick"/> can change an output; +∞ = nothing pending (the loop may idle — a
    /// hold, or hidden chrome with a shown-and-staying cursor, schedules nothing).</summary>
    public double NextWakeMs
        => _visible
            ? (Holds == ChromeHold.None ? _hideAt : double.PositiveInfinity)
            : (!_cursorHidden && _cursorMayHide && _pointerInside ? _cursorAt : double.PositiveInfinity);

    // ── activity (the ONLY inputs that reveal) ───────────────────────────────────────────────────────────────────────

    /// <summary>A mouse/pen move over the player, in any stable coordinate space (the element passes frame-local DIP).</summary>
    public void PointerMoved(float x, float y, double nowMs)
    {
        if (x == _lastX && y == _lastY) return;          // re-delivered at the same point (show/hide/move, hover re-resolve)
        bool entering = !_pointerInside;
        _pointerInside = true; _left = false;
        _lastX = x; _lastY = y;
        if (!_visible && !entering)
        {
            // Jitter is measured from where the pointer RESTED when the chrome hid, never from the previous sample: slow,
            // deliberate motion is many sub-threshold samples, and a per-sample test ignores every one of them.
            if (float.IsNaN(_anchorX)) { _anchorX = x; _anchorY = y; return; }
            float dx = x - _anchorX, dy = y - _anchorY, dz = _t.DeadzoneDip;
            if (dx * dx + dy * dy < dz * dz) return;
        }
        Reveal(ChromeActivity.Pointer, nowMs, entering ? "enter" : "move");
    }

    /// <summary>Wheel, a handled key, a middle-click mute, an aspect/fullscreen command — explicit user activity.</summary>
    public void Activity(ChromeActivity kind, double nowMs)
        => Reveal(kind, nowMs, kind switch { ChromeActivity.Keyboard => "key", ChromeActivity.Touch => "tap", _ => "pointer" });

    /// <summary>A touch tap on the video TOGGLES (Media3 hide_on_touch, Chromium kGestureTap). A hard hold keeps the chrome
    /// up; paused is a SOFT hold — an explicit tap may still put the controls away.</summary>
    public void Tapped(double nowMs)
    {
        if (!_visible || (Holds & HardHolds) != 0) Reveal(ChromeActivity.Touch, nowMs, "tap");
        else Conceal(nowMs, "tap");
    }

    public void SetPressed(bool pressed, double nowMs)
    {
        if (pressed) { _pressed = true; Reveal(ChromeActivity.Pointer, nowMs, "press"); }
        else SetHold(ref _pressed, false, nowMs);
    }

    public void WindowMoveStarted(double nowMs)
    {
        _pressed = false;                                  // the OS loop owns the button now — no release will come
        _moving = true;
        Reveal(ChromeActivity.Pointer, nowMs, "window-move");
    }

    /// <summary>Every OS move/size loop ends through here (edge resizes too) — inert unless WE started one.</summary>
    public void WindowMoveEnded(double nowMs)
    {
        if (!_moving) return;
        _moving = false;
        Reveal(ChromeActivity.Pointer, nowMs, "window-move-end");
    }

    // ── pointer levels ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pointer really left the player: the window, onto the non-client resize band, or the window blurred.</summary>
    public void PointerLeft(double nowMs)
    {
        _pointerInside = false; _left = true;
        _lastX = _lastY = float.NaN;
        _overControls = false; _pressed = false;
        ShowCursor();                                      // never leave a hidden cursor behind outside the video
        if (_visible) _hideAt = Math.Min(_hideAt, nowMs + _t.LeaveHideMs);
    }

    /// <summary>Hover was taken (an overlay scrim, a capture cancel) while the pointer is still over the player: drop the
    /// pointer holds, schedule nothing.</summary>
    public void PointerCovered(double nowMs)
    {
        bool wasHeld = _overControls || _pressed;
        _overControls = false; _pressed = false;
        if (wasHeld) Rearm(nowMs);
    }

    public void SetPointerOverControls(bool over, double nowMs) => SetHold(ref _overControls, over, nowMs);

    // ── holds ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public void SetScrubbing(bool active, double nowMs) => SetHold(ref _scrubbing, active, nowMs);
    public void SetMenuOpen(bool open, double nowMs) => SetHold(ref _menu, open, nowMs);

    public void SetKeyboardFocusInControls(bool focused, double nowMs)
    {
        if (focused == _keyboardFocus) return;
        _keyboardFocus = focused;
        if (focused) Reveal(ChromeActivity.Keyboard, nowMs, "focus"); else Rearm(nowMs);
    }

    public void SetAccessibility(bool active, double nowMs)
    {
        if (active == _a11y) return;
        _a11y = active;
        if (active) Reveal(ChromeActivity.Keyboard, nowMs, "a11y"); else Rearm(nowMs);
    }

    public void SetEnabled(bool enabled, double nowMs)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled) { _visible = true; ShowCursor(); LastCause = "disabled"; }
        else Rearm(nowMs);
    }

    /// <summary>A user-visible stop REVEALS and pins (the user needs the controls — Chromium, Firefox PiP, Media3);
    /// resuming starts a fresh dwell from the resume edge.</summary>
    public void SetPlayback(ChromePlayback playback, double nowMs)
    {
        if (playback == _playback) return;
        _playback = playback;
        if (playback != ChromePlayback.Playing) Reveal(ChromeActivity.Pointer, nowMs, "stopped");
        else Rearm(nowMs);
    }

    /// <summary>Policy × presentation (the element computes it: Always, or FullscreenOnly ∧ presenting fullscreen).
    /// Allowing it while the chrome is already hidden hides the cursor NOW, not on the next jiggle.</summary>
    public void SetCursorMayHide(bool mayHide, double nowMs)
    {
        if (mayHide == _cursorMayHide) return;
        _cursorMayHide = mayHide;
        if (!mayHide) ShowCursor();
        else if (!_visible) _cursorAt = Math.Min(_cursorAt, nowMs);
    }

    // ── clock ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Advance to <paramref name="nowMs"/>. Idempotent; the owner calls it after every input and on every wake.
    /// Returns true when an output changed.</summary>
    public bool Tick(double nowMs)
    {
        bool visible = _visible, cursor = _cursorHidden;
        if (_visible && Holds == ChromeHold.None && nowMs >= _hideAt) Conceal(nowMs, _left ? "leave" : "idle");
        if (!_visible && !_cursorHidden && _cursorMayHide && _pointerInside && nowMs >= _cursorAt) _cursorHidden = true;
        return visible != _visible || cursor != _cursorHidden;
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Reveal(ChromeActivity kind, double nowMs, string cause)
    {
        _dwell = kind switch
        {
            ChromeActivity.Touch => _t.TouchIdleHideMs,
            ChromeActivity.Keyboard => _t.KeyboardIdleHideMs,
            _ => _t.IdleHideMs,
        };
        _hideAt = nowMs + _dwell;
        _anchorX = _anchorY = float.NaN;
        ShowCursor();
        if (!_visible) { _visible = true; LastCause = cause; }
    }

    private void Conceal(double nowMs, string cause)
    {
        _visible = false;
        LastCause = cause;
        _hideAt = double.PositiveInfinity;
        _anchorX = _lastX; _anchorY = _lastY;              // NaN if outside — then the next in-window sample is an ENTER
        _cursorAt = nowMs + _t.CursorTrailMs;              // the cursor goes when the conceal fade has finished
    }

    private void Rearm(double nowMs) { if (_visible) _hideAt = Math.Max(_hideAt, nowMs + _dwell); }

    private void SetHold(ref bool hold, bool on, double nowMs)
    {
        if (hold == on) return;
        hold = on;
        if (!on) Rearm(nowMs);
    }

    private void ShowCursor() { _cursorHidden = false; _cursorAt = double.PositiveInfinity; }
}
```

### 4.4 `MediaPlayerElement` wiring (engine, `src/FluentGpu.Controls/Media/MediaPlayerElement.cs`)

**Delete** (no legacy paths):
- The machine fields at `:312-338`:
  - `_hideFast`, `_hideSlow`, `_cursorTimer`, `_hideArmed`;
  - `_autoHideEnabled`, `_accessibilityActive`, `_notPlaying`, `_buffering`, `_seekInFlight`, `_audioOnlyOrError`, `_forceShow`;
  - `_pointerOverChrome`, `_pointerDown`, `_pointerInVideo`, `_menuOpen`, `_focusInChrome`, `_moveBurstActive`;
  - `_lastMoveX`/`_lastMoveY`, `_revealedByTouchOrFocus`, `_moveBurstPostQueued`, `_clearMoveBurst`.

  **Keep** `_chromeVisible`, `_hooks`, `_fullscreenState`, `_seekBar`, `_focusOutPending`, `_lastPumpScale` (the pump uses it), `_cursorHidden`, `_playerRoot` and `_resolveFocusOut`.
- The machine methods `CanHide`, `Reevaluate`, `ShowChrome`, `CancelHide`, `ArmHide`, `OnHideDue`, `OnCursorDue` and `ClearMoveBurst`. They are interleaved in `:343-437`; keep `CursorHidingAllowed`, `IsFullscreenNow`, `HideCursor`, `ShowCursor` and `IsKeyboardFocus`, which sit among them.
- `OnSuppressorExpired`/`OnOpeningGraceExpired` and the `_releaseBufferSuppress`/`_openingGrace` signals.
- The buffering-suppress and opening-grace blocks with their consts (`:94-99`, `:702-733`).
- `ShouldForceChrome` and `IsStoppedState` (replaced by `ChromePlaybackOf`).
- The dead `ChromeMotion` (`:71-76`).

**Rewrite:**
- `OnMounted` (`:538-543`) becomes `{ _startupPhase?.SetIfChanged(0); Sync(); }`. It keeps the startup-ladder reset and drops the old machine.
- Add `using FluentGpu.Input;`, which `InputDispatcher.ClickSlopPx`/`DoubleClickMs` need.

**Add:**

```csharp
    /// <summary>A press on the VIDEO (never on a control) that travels past the drag box moves the WINDOW through the OS
    /// move loop (<see cref="InputHooks.WindowBeginMove"/>) — Aero Snap, the snap bar, shake and monitor hops all work. A
    /// press that does not travel stays a click (reveal) / double-click (fullscreen). mpv's window-dragging (default on,
    /// 3 px deadzone). For a CHROMELESS host window (the pop-out) only — on an in-window card it would drag the MAIN window,
    /// and inside a scroller the OnDrag capture would steal touch pans. Ignored while presenting fullscreen.</summary>
    public bool DragMovesWindow { get; init; }

    /// <summary>The chrome's view of playback: only USER-VISIBLE stops reveal and hold. Opening/Buffering/Stalled/Ready
    /// with play intent is the stream on its way; NaturalSize=0 counts as audio-only only once actually Playing, so a
    /// video's not-yet-known size during Opening never pins the chrome.</summary>
    internal static ChromePlayback ChromePlaybackOf(bool playIntent, PlaybackState state, bool audioOnly) => state switch
    {
        PlaybackState.Failed => ChromePlayback.Failed,
        PlaybackState.Ended => ChromePlayback.Ended,
        PlaybackState.Paused => ChromePlayback.Paused,
        _ when !playIntent => ChromePlayback.Paused,       // intent wins: a pause request is a stop before the state lands
        PlaybackState.Playing when audioOnly => ChromePlayback.AudioOnly,
        _ => ChromePlayback.Playing,
    };

    private PlayerChromeVisibility? _vis;
    private TimerHandle _wake;
    private double _armedDueMs = double.PositiveInfinity;
    private readonly Action _onWake, _onMoveSizeEnded;
    private readonly Action<Point2> _onPointerMoved, _onMoveArm, _onMoveDrag;
    // window drag gesture
    private Point2 _moveOrigin;
    private bool _moveArmed, _pressIsTouch;
    private double _lastWindowMoveMs = double.NegativeInfinity;

    public MediaPlayerElement()
    {
        _drainPumpRequest = DrainPumpRequest;
        _resolveFocusOut = ResolveFocusOut;
        _onWake = OnWake;
        _onMoveSizeEnded = OnMoveSizeEnded;
        _onPointerMoved = OnPointerMoved;
        _onMoveArm = OnMoveArm;
        _onMoveDrag = OnMoveDrag;
    }

    private double Now() => _wake.NowMs;

    /// <summary>THE chokepoint (replaces Reevaluate/ArmHide/CancelHide/OnHideDue/OnCursorDue). Every input feeds the pure
    /// machine, then calls this: tick at the host timer clock → publish the ONE visibility signal (value-gated) → apply the
    /// cursor override → keep exactly one wake armed at the machine's next deadline.</summary>
    private void Sync()
    {
        if (_vis is not { } vis) return;
        double now = _wake.NowMs;
        vis.Tick(now);
        if (_chromeVisible is { } sig && sig.Peek() != vis.ChromeVisible)
        {
            sig.Value = vis.ChromeVisible;
            Diag.Line($"[media.chrome] {(vis.ChromeVisible ? "show" : "hide")} cause={vis.LastCause} holds=0x{(int)vis.Holds:X}");
        }
        if (vis.CursorHidden) HideCursor(); else ShowCursor();
        double due = vis.NextWakeMs;
        if (double.IsPositiveInfinity(due)) { _armedDueMs = double.PositiveInfinity; return; }   // a stale armed wake just finds nothing due
        // Re-arm only when the deadline moves EARLIER (the base::Timer trick): a pointer moving at 1 kHz pushes the deadline
        // LATER on every sample, and a heap insert per sample would fill the timer queue with stale generations. A later
        // deadline is picked up when the armed wake fires and Sync re-arms from the machine's current value.
        if (due < _armedDueMs - 1.0)
        {
            _armedDueMs = due;
            _wake.RestartIn((float)Math.Max(0.0, due - now));
        }
    }

    private void OnWake() { _armedDueMs = double.PositiveInfinity; Sync(); }
    private void OnMoveSizeEnded() { _vis?.WindowMoveEnded(Now()); Sync(); }
    private void OnPointerMoved(Point2 p) { _vis?.PointerMoved(p.X, p.Y, Now()); Sync(); }

    private void OnChromePointerMove(Point2 _) { _vis?.SetPointerOverControls(true, Now()); Sync(); }
    private void OnChromePointerExit() { _vis?.SetPointerOverControls(false, Now()); Sync(); }

    private void OnChromeFocusChanged(bool focused)
    {
        if (focused) { _focusOutPending = false; _vis?.SetKeyboardFocusInControls(IsKeyboardFocus(), Now()); Sync(); return; }
        _focusOutPending = true;
        if (_postToUi is { } post) post(_resolveFocusOut); else ResolveFocusOut();
    }
    private void ResolveFocusOut()
    {
        if (!_focusOutPending) return;
        _focusOutPending = false;
        _vis?.SetKeyboardFocusInControls(false, Now());
        Sync();
    }

    /// <summary>OnPointerExit on the frame fires for a real leave AND when an overlay scrim/capture cancel takes hover; only
    /// the former may schedule a hide. The dispatcher's pointer (window DIP, null when outside/blurred) tells them apart.</summary>
    private bool PointerStillOverPlayer()
    {
        if (_hooks?.GetPointerPosition?.Invoke() is not { } p) return false;
        var scene = _scene;
        return scene is not null && !_playerRoot.IsNull && scene.IsLive(_playerRoot) && scene.AbsoluteRect(_playerRoot).Contains(p);
    }

    // ── the window drag (DragMovesWindow) ──
    private void OnMoveArm(Point2 local) { _moveOrigin = local; _moveArmed = true; }

    private void OnMoveDrag(Point2 local)
    {
        if (!_moveArmed) return;
        float dx = local.X - _moveOrigin.X, dy = local.Y - _moveOrigin.Y;
        // The Windows drag box (SM_CXDRAG/SM_CYDRAG = 4, per axis) — the same slop a click tolerates, so a press that is
        // still a click never moves the window, and a press that moved the window is never a click.
        if (MathF.Abs(dx) <= InputDispatcher.ClickSlopPx && MathF.Abs(dy) <= InputDispatcher.ClickSlopPx) return;
        _moveArmed = false;                                               // one loop per press
        if (_pressIsTouch || IsFullscreenNow() || _hooks?.WindowBeginMove is not { } begin) return;
        double now = Now();
        _lastWindowMoveMs = now;
        _vis?.WindowMoveStarted(now);
        begin();                                                           // posted; the loop's capture cancels this contact
        Sync();
    }
```

**In `Render`**, this replaces `:702-786`, which covers the buffering/opening blocks, the suppressor publication, the menu effect, the timers and the cursor effect:

```csharp
        // ── the chrome machine: ONE pure policy, ONE timer, ONE sync ────────────────────────────────────────────────
        _wake = UseTimeout(_onWake, TransportControlsHideDelayMs, DepKey.Empty);   // arms once at mount — harmless (Sync is idempotent)
        // A LOCAL for the lambdas below: the field is nullable, and nullable flow analysis does not carry `??=` into a
        // lambda (CS8602 is an error under TreatWarningsAsErrors).
        var vis = _vis ??= new PlayerChromeVisibility(PlayerChromeTiming.Default with { IdleHideMs = TransportControlsHideDelayMs }, _wake.NowMs);

        bool autoHideArmed = AreTransportControlsEnabled && AutoHideTransportControls && !SuppressTransport && !IsDecorative;
        bool a11y = IsAccessibilityActive?.Invoke() ?? false;
        UseEffect(() => { double t = Now(); vis.SetEnabled(autoHideArmed, t); vis.SetAccessibility(a11y, t); Sync(); return (Action?)null; },
            HashCode.Combine(autoHideArmed, a11y));

        ChromePlayback chromePlayback = ChromePlaybackOf(playIntent, state, audioOnly);
        UseEffect(() => { vis.SetPlayback(chromePlayback, Now()); Sync(); return (Action?)null; }, (int)chromePlayback);

        // Scrub gate (lives in the seek bar; changes without re-rendering this element) and menu/overlay gate: effects only.
        UseSignalEffect(() => { bool scrubbing = seekBar.Scrubbing.Value; vis.SetScrubbing(scrubbing, Now()); Sync(); });
        UseSignalEffect(() =>
        {
            _ = overlayService.PinEpoch.Value;
            vis.SetMenuOpen(AnyInputBlockingOverlay(overlayService), Now());
            Sync();
        });

        bool presentedFullscreen = PresentingFullscreen || fullscreen.Value;   // subscribe: the cursor policy edge
        UseEffect(() => { vis.SetCursorMayHide(CursorHidingAllowed(), Now()); Sync(); return (Action?)null; }, presentedFullscreen ? 1 : 0);

        UseEffect(() =>
        {
            if (hooks is null) return (Action?)null;
            hooks.WindowMoveSizeEndedObserved += _onMoveSizeEnded;
            return () => hooks.WindowMoveSizeEndedObserved -= _onMoveSizeEnded;
        }, DepKey.Empty);
        UseEffect(() => (Action?)ReleaseCursorOverride, DepKey.Empty);

        bool showChrome = AreTransportControlsEnabled && !SuppressTransport && chromeVisible.Value;
```

The rest of the rewired call sites:
- `RevealChrome()`, used by `SetAspect` and `LeaveFullscreen`: `_vis!.Activity(ChromeActivity.Pointer, Now()); Sync();`
- `OpenPicker`: `_vis!.SetMenuOpen(true, Now()); Sync();` (replaces `_menuOpen = true; Reevaluate();`).
- `HandleKeyCore`, at the end (`:1551-1553`): `_vis!.Activity(ChromeActivity.Keyboard, Now()); Sync();`

The frame handlers (replacing `:1030-1098`):

```csharp
        void HandlePress(PointerEventArgs e)
        {
            double now = Now();
            _pressIsTouch = e.Kind == PointerKind.Touch;
            if (e.Button == 2) { Player.SetMuted(!Player.Muted.Peek()); _vis!.Activity(ChromeActivity.Pointer, now); Sync(); return; }
            // DOUBLE click = fullscreen (mpv MBTN_LEFT_DBL, Firefox PiP); SINGLE click only reveals (mpv #15405: a click that
            // raises/focuses the window must not pause it). A press that ended a window drag is never the first half of a
            // double-click (mpv clears last_doubleclick_time for the same reason).
            if (e.ClickCount >= 2 && now - _lastWindowMoveMs > InputDispatcher.DoubleClickMs) { ToggleFullscreen(); e.Handled = true; return; }
            if (_pressIsTouch) _vis!.Tapped(now); else _vis!.SetPressed(true, now);
            Sync();
        }

        void HandleRelease(PointerEventArgs e) { _moveArmed = false; _vis!.SetPressed(false, Now()); Sync(); }

        void HandleExit()
        {
            _moveArmed = false;
            if (PointerStillOverPlayer()) _vis!.PointerCovered(Now());   // a scrim / the move loop's capture cancel took hover
            else _vis!.PointerLeft(Now());                               // left the window, the NC resize band, or blur
            Sync();
        }

        void HandleWheel(WheelEventArgs e)
        {
            if ((e.Mods & KeyModifiers.Shift) != 0) seekBar.SeekBy(e.Delta > 0f ? 10f : -10f);
            else AdjustVolume(e.Delta > 0f ? VolumeStep : -VolumeStep);
            _vis!.Activity(ChromeActivity.Pointer, Now());
            Sync();
            e.Handled = true;
        }

        var frame = new BoxEl
        {
            …   // unchanged layout/corners/border/focus/realize
            OnKeyDown = HandleKey,
            OnPointerMoveWithin = _onPointerMoved,
            OnPointerPressed = HandlePress,
            OnPointerReleased = HandleRelease,
            OnPointerExit = HandleExit,
            OnPointerWheel = HandleWheel,
            // The move gesture: OnDrag gives the frame the engine's eager capture, so a travelling press is seen past the slop.
            // Buttons and the seek rail are their own interaction-gated press targets (InputDispatcher.cs:941-983), so a press
            // on a control never arms it. On capture loss the dispatcher fires this frame's OnPointerExit (:1226) → covered.
            OnPointerDown = DragMovesWindow ? _onMoveArm : null,
            OnDrag = DragMovesWindow ? _onMoveDrag : null,
            OnFocusChanged = focused => { if (focused && IsKeyboardFocus()) { _vis!.Activity(ChromeActivity.Keyboard, Now()); Sync(); } },
            Children = layers,
        };
```

### 4.5 The fade: opacity only, from the live value (`:1005-1014`)

The chrome stays mounted with a stable key, and visibility is carried by opacity + hit-test + focusability (the existing and correct
architecture). The static `Opacity = showChrome ? 1f : 0f` stays as the terminal, so an unrelated re-render (an ABR label, a
cue) always re-asserts the right end value. Only the seed changes:

```csharp
        UseLayoutEffect(() =>
        {
            var node = chromeRef.Value;
            var scene = Context.Scene;
            if (Context.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
            var tok = showChrome ? MotionTok.MediaChromeReveal : MotionTok.MediaChromeConceal;
            float to = showChrome ? 1f : 0f;
            float ms = tok.EffectiveDurationMs(AnimChannel.Opacity);
            if (ms <= 0f) return;                         // reduced motion: the static terminal already IS the value
            // FROM = where the pixels ARE. A layout effect runs AFTER the reconciler re-asserted the static terminal
            // (Reconciler: `paint.Opacity = b.Opacity.Value`, unconditional), so scene.Paint(node).Opacity reads `to` here and
            // the fade degenerated into a cut; under render-owned compositor rows the UI paint is the authored base anyway.
            // A live row (an interrupted fade) carries the real value; with no row the pixels sit at the PREVIOUS terminal.
            float from = anim.TryGetTrackValue(node, AnimChannel.Opacity, out float live) ? live : 1f - to;
            if (MathF.Abs(from - to) < 0.001f) return;
            anim.SeedEased(node, AnimChannel.Opacity, from, to, ms, tok.Easing);   // phase 7 composes `from` this very frame
        }, showChrome ? 1 : 0);
```

Apply the same `TryGetTrackValue ?? previous terminal` seed to the poster cross-fade at `:1018-1026`. It has the identical defect.

**No layout shifts during a fade:**
- `if (volumeOpen && interactive)` at `:1314` becomes `if (volumeOpen)`. Input is already gated by the chrome root's `HitTestVisible`, and unmounting the slider at the hide edge reflowed the row mid-fade.
- `CaptionMotion` at `:112-114` is decoupled from `MediaChromeFadeOutMs`: it becomes `TransitionDynamics.Tween(200f, Easing.FluentStandard)`, its current value. The conceal token moves to 400 ms, and captions shouldn't crawl.

### 4.6 `MediaSeekBar`: palette from the model, input from the chrome (`src/FluentGpu.Controls/Media/MediaSeekBar.cs`)

```csharp
        bool chromeUp = ChromeVisible?.Value ?? true;
        // TWO facts, not one. `enabled` is the MODEL (is there a scale to seek on) and drives every pixel; `interactive`
        // adds the chrome's visibility and gates only input and accessibility. Folding chromeUp into the palette greyed the
        // rail, shrank the thumb and stopped the playhead at the START of every conceal — the bar visibly jumped as it faded.
        bool enabled = (durSec > 0.0 || liveRail) && st is not (PlaybackState.Idle or PlaybackState.Failed);
        bool interactive = enabled && chromeUp;
```

What each flag drives:
- `enabled` (the model): the palette/scale lines at `:270-279`, and the spinner at `:358`.
- `interactive` (the model plus chrome visibility):
  - `HoverOpacity`/`PressedOpacity` (`:349`).
  - `Role`, `TabStop`, `Cursor`, `IsEnabled`, and the `OnPointerDown`/`OnDrag`/`OnClick`/`OnDragCanceled`/`OnPointerWheel` handlers (`:386-396`).
  - `canAdvance = interactive && playing && !buffering` (`:378`). Hidden chrome still ticks nothing, so the loop can idle.
  - `modelKey = HashCode.Combine(interactive, …)` (`:265`). A reveal still re-seeds the resting fill. If the new fade gate sees one stale frame, make that a `UseLayoutEffect`.

### 4.7 Motion tokens (`src/FluentGpu.Engine/Animation/MotionTok.cs:127-180`)

```csharp
    // Dwell: 3000 ms — Firefox PiP CONTROLS_FADE_TIMEOUT_MS 3000; Chromium media controls / Chrome PiP 2500; Firefox inline
    // 2000; mpv osc 500. (The previous "WinUI ControlPanelDisplayTimeoutInSecs = 3 s" citation could not be verified.)
    public const float MediaChromeIdleDelayMs = 3000f;
    public const float MediaChromeIdleDelayTouchMs = 4000f;         // Media3 show_timeout 5000 is the touch reference
    public const float MediaChromeIdleDelayAfterFocusMs = 4000f;
    /// <summary>Pointer left the player → hide after this. Every reference player hides on leave immediately (mpv osc,
    /// Chromium pointerout, Chrome PiP kMouseExited, Firefox PiP); 150 ms only absorbs a client↔resize-band crossing and
    /// an overlay scrim taking hover before its hold registers.</summary>
    public const float MediaChromeLeaveHideMs = 150f;
    /// <summary>Hidden-chrome jitter deadzone, measured from where the pointer RESTED when the chrome hid (VLC
    /// qt-fs-sensitivity 3 px; mpv dragging_deadzone 3). Never per-sample.</summary>
    public const float MediaChromeMoveThresholdDip = 3f;
    /// <summary>Conceal: 400 ms ease-out — between mpv (fadeduration 200) and Chromium (1 s, cubic-bezier(.25,.1,.25,1));
    /// an unattended disappearance must read as a fade, not a pop. The cursor hides when it ends.</summary>
    public const float MediaChromeFadeOutMs = 400f;
    /// <summary>Reveal: 150 ms Fluent decelerate (Fluent "fast" 167; Chromium 250; mpv instant) — answers the user fast.</summary>
    public const float MediaChromeFadeInMs = 150f;
    // MediaChromeCursorExtraDelayMs — DELETED (the cursor trail is the conceal duration: PlayerChromeTiming.CursorTrailMs)
    …
        MotionTokenId.MediaChromeReveal => MotionTokenDef.Eased(MediaChromeFadeInMs, Easing.FluentDecelerate),
        MotionTokenId.MediaChromeConceal => MotionTokenDef.Eased(MediaChromeFadeOutMs, Easing.EaseOut),   // was FluentAccelerate (1,0,1,1): flat, then a drop
```

### 4.8 App (Wavee): only the pop-out opts in

**`src/apps/Wavee/App/VideoStageInput.cs`** (NEW, pure, next to `DetachedFullscreenRule`/`PlacementCore`):

```csharp
using FluentGpu.Controls.Media;

namespace Wavee;

/// <summary>Which input affordances a video stage gets, by the surface it is in (<see cref="TransportOwner"/>). Pure —
/// the <see cref="PlacementCore"/> / <see cref="DetachedFullscreenRule"/> pattern; pinned by VideoStageInputTests.</summary>
public static class VideoStageInput
{
    /// <summary>Only the pop-out OWNS its window. Dragging the picture of the in-window mini player or the docked card must
    /// never move the MAIN window (and an OnDrag capture inside a scroller would steal touch pans); a fullscreen window
    /// has nowhere to go.</summary>
    public static bool DragMovesWindow(TransportOwner identity, bool hostFullscreen)
        => identity == TransportOwner.PopOut && !hostFullscreen;

    /// <summary>A dedicated video window hides the cursor with its idle chrome (mpv's windowed default, cursor-autohide
    /// 1000 ms); inline surfaces keep the page's cursor and hide it only in fullscreen.</summary>
    public static CursorAutoHidePolicy Cursor(TransportOwner identity)
        => identity == TransportOwner.PopOut ? CursorAutoHidePolicy.Always : CursorAutoHidePolicy.FullscreenOnly;
}
```

**`src/apps/Wavee/Features/Video/PopOutVideoWindow.cs`**, in `PopOutVideoStage.Render` (`:209-242`). Both values are pure functions of
`(Identity, IsHostFullscreen)`, which are already folded into the element key (`:f0/:f1`), so the props-freeze contract holds:

```csharp
        bool suppress = Host is { } h && h.Owner.Value != h.Identity;
        var fullscreen = Host?.FullscreenRequested;
        bool dragMovesWindow = Host is { } hd && VideoStageInput.DragMovesWindow(hd.Identity, IsHostFullscreen);
        var cursor = Host is { } hc ? VideoStageInput.Cursor(hc.Identity) : CursorAutoHidePolicy.FullscreenOnly;
        return Embed.Comp(() => new MediaPlayerElement
            {
                …                                   // unchanged
                DragMovesWindow = dragMovesWindow,  // the pop-out: drag the picture to move the window (Aero Snap included)
                CursorAutoHide = cursor,            // the pop-out: the cursor idles away with the chrome
            })
            with { Key = … };                       // unchanged
```

Also fix the stale type doc at `:12-16`. It should say: "The window is chromeless (`CustomFrame`): the OS frame keeps only the resize
borders; dragging the picture moves the window (`MediaPlayerElement.DragMovesWindow`); close via the ⋯ menu's 'Turn off
video', Alt+F4 or Alt+Space."

### 4.9 Rejected alternatives

- **Report a `Caption` region over the video** (Chrome PiP / Electron model): see the table in §1.1. It would need NC→client hover forwarding,
  NC cursor hiding, a double-click remap, an NC right-click remap, a click-stall workaround, and re-pushed regions on every chrome flip.
- **Re-enable the OS title bar** (`CustomFrame: false`): it brings back the caption over the video, which is what `1f906c906` removed on purpose.
- **Keep the old in-element machine and patch it**: the reveal/hold conflation (D2) is structural to `Reevaluate`, and D1/D3/D4
  need clocked state that the machine can't test headlessly. Only the pure class is unit-testable (the "no source-text tests" rule).

**Stopgap (only if 0.2.9 must ship before the engine work lands; not the end state).** This is an app-only top caption band. It gives drag back
in minutes, at the known costs from §1.1, which are limited to the band: hovering it hides the transport, and double-clicking it maximizes.
In `PopOutVideoContent.Render`:

```csharp
        var hooks = UseContext(InputHooks.Current);
        var regions = UseRef<TitleBarRegion[]>(new TitleBarRegion[1]);
        float w = vp.Value.Width;
        UseLayoutEffect(() =>
        {
            if (hooks?.SetTitleBarRegions is not { } push) return;
            regions.Value[0] = new TitleBarRegion(new RectF(0f, 0f, w, 32f), TitleBarHit.Caption);   // top 32 DIP = the drag band 1f906c906 promised
            push(regions.Value, 1);
        }, (int)w);
```

---

## 5. Files to change

| Repo | File | Change | Agent |
|---|---|---|---|
| engine | `src/FluentGpu.Engine/Seams/Pal/Pal.cs` | `IPlatformWindow.BeginSystemMove()`; `InputKind.WindowMoveSizeEnded = 15` | A |
| engine | `src/FluentGpu.Windows/Pal/Win32Platform.cs` (WIP file) | `BeginSystemMove`; primary-contact tracking; enqueue on `WM_EXITSIZEMOVE`; `[window.move]` lines | A |
| engine | `src/FluentGpu.Engine/Headless/Pal/HeadlessPlatform.cs` | `BeginSystemMoveCount` | A |
| engine | `src/FluentGpu.Engine/Hooks/Context.cs` (WIP file) | `InputHooks.WindowBeginMove`, `WindowMoveSizeEndedObserved`, `NotifyWindowMoveSizeEnded` | A |
| engine | `src/FluentGpu.Engine/Input/InputDispatcher.cs` | `OnWindowMoveSizeEnded` + case | A |
| engine | `src/FluentGpu.Engine/Hosting/AppHost.cs` (WIP file) | two wiring lines | A |
| engine | `src/FluentGpu.Engine/Hooks/RenderContext.Timers.cs` | `TimerHandle.RestartIn`, `TimerHandle.NowMs` | A |
| engine | `docs/design/{SPEC-INDEX.md, subsystems/README.md, subsystems/pal-rhi.md, subsystems/input-a11y.md, subsystems/reconciler-hooks.md}` | register and describe the four contracts; run `check-canon.ps1` | A |
| engine | `src/FluentGpu.Controls/Media/PlayerChromeVisibility.cs` | **NEW**: the pure machine (§4.3) | B |
| engine | `src/FluentGpu.Engine/Animation/MotionTok.cs` | timing table (§4.7) | B |
| engine | `src/FluentGpu.Windows.Tests/PlayerChromeVisibilityTests.cs` | **NEW** (§6.1) | B |
| engine | `src/FluentGpu.Controls/Media/MediaPlayerElement.cs` | rewire (§4.4), fade seed (§4.5), drag gesture, deletions | C |
| engine | `src/FluentGpu.Controls/Media/MediaSeekBar.cs` | enabled/interactive split (§4.6) | C |
| engine | `src/FluentGpu.Windows.Tests/MediaPlayerElementLogicTests.cs` | delete the `ShouldForceChrome` theory; add the `ChromePlaybackOf` theory | C |
| engine | `src/FluentGpu.VerticalSlice/Suites/ControlsSuite.cs`, `Probes/Probes.cs` | new gates (§6.2); `MediaPlayerHostProbe.DragMovesWindow` | D |
| app | `src/apps/Wavee/App/VideoStageInput.cs` | **NEW** (§4.8) | E |
| app | `src/apps/Wavee/Features/Video/PopOutVideoWindow.cs` | pass `DragMovesWindow` + `CursorAutoHide`; fix the stale doc | E |
| app | `src/apps/Wavee.Tests/VideoStageInputTests.cs` | **NEW** (§6.3) | E |
| app | `CHANGELOG.md` | two bullets ending ` (#n)`, with `Fixes #n` in the commit body | E |

Agents A–E touch disjoint files; the code in this plan is their shared contract.
- C compiles against A's and B's API as spelled out here.
- D and E only use public surface.
- Only the orchestrator builds, tests and launches.
- The engine files marked "WIP file" carry the user's uncommitted work. Edits there must be additive, and the WIP must never be reverted or reformatted.

---

## 6. Tests

### 6.1 Pure machine: `src/FluentGpu.Windows.Tests/PlayerChromeVisibilityTests.cs`

```csharp
using FluentGpu.Controls.Media;
using Xunit;

namespace FluentGpu.Tests;

public sealed class PlayerChromeVisibilityTests
{
    static readonly PlayerChromeTiming T = new(IdleHideMs: 3000, TouchIdleHideMs: 4000, KeyboardIdleHideMs: 4000,
        LeaveHideMs: 150, CursorTrailMs: 400, DeadzoneDip: 3f);

    /// Playing, cursor allowed, pointer resting inside at (100,100), revealed at t.
    static PlayerChromeVisibility Resting(double t = 0)
    {
        var m = new PlayerChromeVisibility(T, t);
        m.SetCursorMayHide(true, t);
        m.PointerMoved(100, 100, t);
        return m;
    }

    [Fact] public void AFreshSurfaceShowsItsControlsThenIdlesAway()
    {
        var m = new PlayerChromeVisibility(T, 0);
        Assert.True(m.ChromeVisible);
        Assert.Equal(3000, m.NextWakeMs);
        m.Tick(2999); Assert.True(m.ChromeVisible);
        Assert.True(m.Tick(3000)); Assert.False(m.ChromeVisible);
        Assert.Equal("idle", m.LastCause);
    }

    [Fact] public void EveryRealMoveRestartsTheDwell()
    {
        var m = Resting();
        m.PointerMoved(101, 100, 2500);
        m.Tick(3000); Assert.True(m.ChromeVisible);
        m.Tick(5500); Assert.False(m.ChromeVisible);
    }

    [Fact] public void ARedeliveredSamePointIsNotActivity()          // spurious WM_MOUSEMOVE on show/hide/move
    {
        var m = Resting();
        m.PointerMoved(100, 100, 2500);
        m.Tick(3000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void JitterInsideTheDeadzoneNeverReveals()
    {
        var m = Resting(); m.Tick(3000);                                // hidden; rest point (100,100)
        m.PointerMoved(101, 101, 3100); m.PointerMoved(99, 101, 3200); m.PointerMoved(102, 99, 3300);
        Assert.False(m.ChromeVisible);
    }

    [Fact] public void ASlowCreepRevealsOnceItLeavesTheRestPoint()   // D3: the per-sample test never fired here
    {
        var m = Resting(); m.Tick(3000);
        m.PointerMoved(101, 100, 3100); m.PointerMoved(102, 100, 3200);
        Assert.False(m.ChromeVisible);
        m.PointerMoved(103, 100, 3300);
        Assert.True(m.ChromeVisible);
    }

    [Fact] public void EnteringRevealsImmediately()
    {
        var m = Resting();
        m.PointerLeft(100); m.Tick(250); Assert.False(m.ChromeVisible);
        m.PointerMoved(5, 5, 1000);
        Assert.True(m.ChromeVisible);
    }

    [Fact] public void LeavingHidesAfterTheDebounceNotTheDwell()     // D4
    {
        var m = Resting();
        m.PointerLeft(500);
        Assert.Equal(650, m.NextWakeMs);
        m.Tick(649); Assert.True(m.ChromeVisible);
        m.Tick(650); Assert.False(m.ChromeVisible);
        Assert.Equal("leave", m.LastCause);
    }

    [Fact] public void HoldsNeverRevealHiddenChrome()                // D2: a suppressor used to REVEAL
    {
        var m = Resting(); m.Tick(3000);
        m.SetScrubbing(true, 3100); m.SetMenuOpen(true, 3200); m.SetPlayback(ChromePlayback.Playing, 3300);
        Assert.False(m.ChromeVisible);
    }

    [Fact] public void RestingOnTheControlsHoldsIndefinitely_LeavingThemRestartsTheDwell()
    {
        var m = Resting();
        m.SetPointerOverControls(true, 500);
        m.Tick(60_000); Assert.True(m.ChromeVisible);
        m.SetPointerOverControls(false, 60_000);
        m.Tick(62_999); Assert.True(m.ChromeVisible);
        m.Tick(63_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void MenuHoldsThroughALeave_CloseRestartsTheDwell() // windowed popup: the pointer left the client for it
    {
        var m = Resting();
        m.SetMenuOpen(true, 100); m.PointerLeft(200);
        m.Tick(10_000); Assert.True(m.ChromeVisible);
        m.SetMenuOpen(false, 10_000);
        m.Tick(12_999); Assert.True(m.ChromeVisible);
        m.Tick(13_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void PressHolds_ReleaseRestartsTheDwell()
    {
        var m = Resting();
        m.SetPressed(true, 1000);
        m.Tick(50_000); Assert.True(m.ChromeVisible);
        m.SetPressed(false, 50_000);
        m.Tick(52_999); Assert.True(m.ChromeVisible);
        m.Tick(53_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void PauseRevealsAndPins_ResumeStartsAFreshDwell()
    {
        var m = Resting(); m.Tick(3000);
        m.SetPlayback(ChromePlayback.Paused, 4000); Assert.True(m.ChromeVisible);
        m.Tick(1_000_000); Assert.True(m.ChromeVisible);
        Assert.Equal(double.PositiveInfinity, m.NextWakeMs);        // a hold schedules nothing — the loop may idle
        m.SetPlayback(ChromePlayback.Playing, 1_000_000);
        m.Tick(1_002_999); Assert.True(m.ChromeVisible);
        m.Tick(1_003_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void TapToggles_ButAHardHoldKeepsItUp()
    {
        var m = new PlayerChromeVisibility(T, 0);
        m.Tapped(100); Assert.False(m.ChromeVisible);
        m.Tapped(200); Assert.True(m.ChromeVisible);
        Assert.Equal(4200, m.NextWakeMs);                           // the touch dwell
        m.SetMenuOpen(true, 300);
        m.Tapped(400); Assert.True(m.ChromeVisible);                // a picker is open: not a dismiss
        m.SetMenuOpen(false, 500);
        m.SetPlayback(ChromePlayback.Paused, 600);
        m.Tapped(700); Assert.False(m.ChromeVisible);               // paused is SOFT — an explicit tap still hides
    }

    [Fact] public void KeyboardFocusRevealsWithTheLongDwell_AndHolds()
    {
        var m = Resting(); m.Tick(3000);
        m.SetKeyboardFocusInControls(true, 5000); Assert.True(m.ChromeVisible);
        m.Tick(100_000); Assert.True(m.ChromeVisible);
        m.SetKeyboardFocusInControls(false, 100_000);
        m.Tick(103_999); Assert.True(m.ChromeVisible);
        m.Tick(104_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void CursorHidesWhenTheFadeEnds_OnlyInsideAndOnlyWhenAllowed()
    {
        var m = Resting();
        m.Tick(3000); Assert.False(m.CursorHidden);
        Assert.Equal(3400, m.NextWakeMs);
        m.Tick(3400); Assert.True(m.CursorHidden);

        var inline = new PlayerChromeVisibility(T, 0);              // policy: no (inline, windowed)
        inline.PointerMoved(1, 1, 0);
        inline.Tick(3000); inline.Tick(10_000);
        Assert.False(inline.CursorHidden);
        Assert.Equal(double.PositiveInfinity, inline.NextWakeMs);
    }

    [Fact] public void CursorReturnsOnActivityAndOnLeave()
    {
        var m = Resting(); m.Tick(3000); m.Tick(3400);
        m.PointerMoved(110, 100, 5000); Assert.False(m.CursorHidden);
        m.Tick(8000); m.Tick(8400); Assert.True(m.CursorHidden);
        m.PointerLeft(9000); Assert.False(m.CursorHidden);
    }

    [Fact] public void AllowingTheCursorWhileHiddenHidesItNow()       // entering fullscreen with idle chrome
    {
        var m = new PlayerChromeVisibility(T, 0);
        m.PointerMoved(100, 100, 0); m.Tick(3000);
        m.SetCursorMayHide(true, 5000);
        Assert.True(m.NextWakeMs <= 5000);
        m.Tick(5000); Assert.True(m.CursorHidden);
    }

    [Fact] public void WindowMoveHoldsUntilTheLoopEnds_ThenDwells()  // D7: nothing stays pinned after a drag
    {
        var m = Resting();
        m.SetPressed(true, 100);
        m.WindowMoveStarted(150);
        m.PointerCovered(160);                                      // the loop's capture cancel — not a leave
        m.Tick(60_000); Assert.True(m.ChromeVisible);
        m.WindowMoveEnded(60_000);
        m.Tick(62_999); Assert.True(m.ChromeVisible);
        m.Tick(63_000); Assert.False(m.ChromeVisible);
    }

    [Fact] public void AStrayMoveSizeEndIsInert()                    // edge resizes raise the same event
    {
        var m = Resting(); m.Tick(3000);
        m.WindowMoveEnded(3500);
        Assert.False(m.ChromeVisible);
    }

    [Fact] public void DisabledPinsTheChromeAndNeverHidesTheCursor()
    {
        var m = Resting();
        m.SetEnabled(false, 0);
        m.Tick(100_000);
        Assert.True(m.ChromeVisible); Assert.False(m.CursorHidden);
        Assert.Equal(ChromeHold.Disabled, m.Holds);
    }
}
```

Plus, in `MediaPlayerElementLogicTests.cs`, this replaces `ChromePolicy_ForcesOnlyOnStopOrFailure`:

```csharp
    [Theory]
    [InlineData(true,  PlaybackState.Playing,   false, ChromePlayback.Playing)]
    [InlineData(true,  PlaybackState.Buffering, false, ChromePlayback.Playing)]    // a rebuffer / ABR switch is not a stop (D2)
    [InlineData(true,  PlaybackState.Stalled,   false, ChromePlayback.Playing)]
    [InlineData(true,  PlaybackState.Opening,   true,  ChromePlayback.Playing)]    // size unknown while opening ≠ audio-only
    [InlineData(true,  PlaybackState.Ready,     false, ChromePlayback.Playing)]    // opened, about to play
    [InlineData(true,  PlaybackState.Playing,   true,  ChromePlayback.AudioOnly)]
    [InlineData(false, PlaybackState.Playing,   false, ChromePlayback.Paused)]     // intent wins
    [InlineData(true,  PlaybackState.Paused,    false, ChromePlayback.Paused)]
    [InlineData(true,  PlaybackState.Ended,     false, ChromePlayback.Ended)]
    [InlineData(true,  PlaybackState.Failed,    false, ChromePlayback.Failed)]
    public void ChromePlaybackOf_MapsOnlyUserVisibleStops(bool intent, PlaybackState state, bool audioOnly, ChromePlayback expected)
        => Assert.Equal(expected, MediaPlayerElement.ChromePlaybackOf(intent, state, audioOnly));
```

### 6.2 VerticalSlice gates (`ControlsSuite.cs`, headless, deterministic clock)

These use the existing `MediaPlayerHostProbe` (plus a `DragMovesWindow` field), `PlayingPlayer(...)`, `window.QueueInput`, and
`host.Paint(0)` ≈ 16 ms per frame.

| Gate | Script | Asserts |
|---|---|---|
| `gate.media.el.chrome-fade-interpolates` | settle visible → let the dwell elapse → sample the chrome root (the nearest ancestor of the play button whose parent is the frame) one and three frames after the hide edge; same for a reveal | `0 < opacity < 1` mid-fade in both directions (catches D1 forever) |
| `gate.media.el.fade-retargets-live` | start a conceal → move the pointer mid-fade | the next frame's opacity is ≥ the mid-fade value (no jump to 0 or 1) |
| `gate.media.el.drag-moves-window` | `DragMovesWindow=true`: `PointerDown` on the video → move +2 px → move +12 px → move +30 px; then queue `PointerCancel` + `WindowMoveSizeEnded` and run the dwell plus conceal | `BeginSystemMoveCount`: 0 after +2, exactly 1 after +12/+30; chrome not pinned afterwards (`Buttons()==0` after dwell) |
| `gate.media.el.drag-click-still-a-click` | press/release with no travel; then press on the seek rail and travel 30 px; then `DragMovesWindow=false` and travel 30 px | count stays 0 each time; the click revealed; the rail seeked |
| `gate.media.el.leave-hides` | move inside → queue `PointerMove(OffscreenDip)` (the Win32 leave shape) | hidden within LeaveHideMs plus the conceal fade, not the dwell |
| `gate.media.el.buffering-no-reveal` | hidden → player reports buffering (QualitySwitch) for 1 s | stays hidden |
| `gate.media.el.slow-move-reveals` | hidden → ten +1 DIP moves, one per frame | revealed by the third move |
| `gate.media.el.seek-palette-stable` | sample the seek fill node's `Fill` just before and one frame after the hide edge | unchanged (D5) |
| existing `gate.media.el.pins-anchor-autohide` | unchanged | still green (the menu hold is the same contract) |

### 6.3 App: `src/apps/Wavee.Tests/VideoStageInputTests.cs`

```csharp
using FluentGpu.Controls.Media;
using Xunit;

namespace Wavee.Tests;

public class VideoStageInputTests
{
    [Fact] public void OnlyThePopOutMovesItsWindow()
    {
        Assert.True(VideoStageInput.DragMovesWindow(TransportOwner.PopOut, hostFullscreen: false));
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.Docked, false));      // would drag the MAIN window
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.Fullscreen, false));
        Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.GlobalBar, false));
    }

    [Fact] public void AFullscreenPopOutHasNowhereToGo()
        => Assert.False(VideoStageInput.DragMovesWindow(TransportOwner.PopOut, hostFullscreen: true));

    [Fact] public void OnlyTheDedicatedWindowHidesTheCursorWindowed()
    {
        Assert.Equal(CursorAutoHidePolicy.Always, VideoStageInput.Cursor(TransportOwner.PopOut));
        Assert.Equal(CursorAutoHidePolicy.FullscreenOnly, VideoStageInput.Cursor(TransportOwner.Docked));
        Assert.Equal(CursorAutoHidePolicy.FullscreenOnly, VideoStageInput.Cursor(TransportOwner.Fullscreen));
    }
}
```

`Win32Window.BeginSystemMove` itself needs a real HWND and a modal loop. The live run covers it (§8); no source-text test.

---

## 7. Risks

1. **The posted `WM_NCLBUTTONDOWN` under `EnableMouseInPointer`.**
   - The existing caption drag already relies on `DefWindowProc` running the move loop from the NC path under mouse-in-pointer (`Win32Platform.cs:2090-2091`, `:2145-2152`). A synthesized press is the documented caption press mpv uses.
   - Verify live. Fallback A: `PostMessageW(WM_SYSCOMMAND, SC_MOVE | HTCAPTION)` (undocumented 0xF012).
   - Fallback B: run `SendMessageW` from an `AppHost.Post` continuation, which drains between frames and is never inside dispatch.
2. **Capture handoff.** The explicit `PointerCancel` makes the engine side deterministic. Verify that a duplicate cancel from
   `WM_POINTERCAPTURECHANGED` is a no-op on an idle slot (`CancelPointerContact` → `CancelWorkingContact` on empty fields).
3. **Modal loop.** The UI frame loop is suspended during the move (the OS owns the pump).
   - Video keeps presenting through DComp, and the 8 ms keep-alive covers ambient animation. This is the same path the pre-`1f906c906` OS-caption drag took.
   - The `WindowMove` hold keeps the chrome up if keep-alive paints run timers; `WindowMoveSizeEnded` releases it.
4. **Touch.** A touch drag does not move the window in P1. Tap toggles, and double-tap is fullscreen. A manual-move fallback is P3 (§9.3).
5. **Behaviour change on every surface.** The docked card, fullscreen and gallery all gain leave-hides, no buffering reveal, the real fade,
   and 150/400 ms fades. That is the point, but tell the user.
6. **Leave-hides at the resize border.** Moving toward an edge to resize fades the chrome. mpv and Chrome PiP behave the same way. The 150 ms debounce stops flicker along the 8 px top band.
7. **The frame-order assumption** (reconcile → layout effects → anim tick) behind the fade seed is pinned by `chrome-fade-interpolates`.
8. **Engine WIP.** `AppHost.cs`, `Context.cs` and `Win32Platform.cs` have large uncommitted diffs, as do the `Animation/*` compositor rows.
   - Edits there are additive; rebase agents on the working tree and never on `HEAD`.
   - Under render-owned compositor rows, `TryGetTrackValue` returns the last render feedback pose (`ApplyCompositorFeedback`, ≤ 1 frame old), which is good enough for a retarget.
9. **Timer queue.** The machine is re-armed only when its deadline moves earlier, so a 1 kHz mouse doesn't push a heap entry per sample.
10. **Always-on logs.**
    - `[media.chrome]`: one line per visibility edge.
    - `[window.move]`: one line per gesture.
    - Both are edge-cadence `Diag.Line` calls, never per frame. `Program.cs:131` routes `Diag.Sink` into the Wavee log.
11. **Issue references.** Wavee's release gate refuses CHANGELOG bullets without ` (#n)`. File two issues (drag regression and auto-hide)
    through the `github-triage` skill. Every modifying `gh` call needs the user's approval first.

---

## 8. Verification

**Engine** (`C:\WAVEE\fluent-gpu`):
1. `dotnet build src/FluentGpu.slnx` and `dotnet build src/FluentGpu.slnx -c Release`. Both must be clean; `TreatWarningsAsErrors` is on.
2. `dotnet run --project src/FluentGpu.VerticalSlice` must print "ALL CHECKS PASSED", including the new `gate.media.el.*` gates and the zero-alloc gates.
3. `dotnet test src/FluentGpu.Windows.Tests` must run `PlayerChromeVisibilityTests` and `MediaPlayerElementLogicTests` green.
4. `powershell -File docs\design\check-canon.ps1` must exit 0.

**App** (`C:\WAVEE\wavee-0.2.9`):
1. `dotnet build Wavee.slnx` and `-c Release`. If the user's running Debug instance locks Debug output, verify with the Release build and `-o` to a verify folder.
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` must be green, including `VideoStageInputTests`.

**Live check.** Only the user can do this: it needs a real login and a track with a video. A second `Wavee.exe` hands off to the running one, so the user closes theirs and runs the verify build. Play a music video, then choose Player bar → video placement → "Pop-out window".

Drag:
- [ ] Drag the picture: the window follows. Drag it to a screen edge: it snaps. Drag it to the top: the snap bar appears. Shake: works if enabled in Settings.
- [ ] Win+Z opens snap layouts. Alt+Space opens the system menu (Move and Close work).
- [ ] Double-click the picture: the pop-out goes fullscreen on its own monitor; Escape restores it. A double-click never maximizes.
- [ ] A single click with no travel only reveals the controls. Dragging on the seek rail seeks, and the window does not move. Buttons behave as buttons.
- [ ] Resize from all four edges and the corners. The top 8 px band resizes.

After a drag:
- [ ] The controls idle away 3 s later and are not stuck.
- [ ] The log shows `[window.move] begin` / `[window.move] end`.

Auto-hide:
- [ ] Hover still: controls fade out after 3 s over about 400 ms. It is a visible fade, not a blink. The cursor disappears when the fade ends.
- [ ] Move slowly: the controls return, even with a slow creep. Resting still with a jittery mouse: nothing happens.
- [ ] Rest on the controls: they never hide. Open ⋯ or 480p (Auto): they never hide while the menu is open; after it closes, a 3 s dwell starts.
- [ ] Leave the window: the controls fade within about 0.5 s.
- [ ] Pause: the controls show and stay. Resume: they are gone 3 s later.
- [ ] An ABR switch or a rebuffer never pops the controls up; the spinner still shows.
- [ ] The seek rail keeps its colour and thumb through the fade; there is no grey flash and no jump. The volume slider doesn't collapse mid-fade.
- [ ] The log shows `[media.chrome] hide cause=idle|leave holds=0x0` and `[media.chrome] show cause=enter|move|press|key|stopped`. It never shows a show with `cause=` buffering.

Regression pass:
- [ ] Docked card, in-window mini player and fullscreen surface: the transport idles as before, now with real fades.
- [ ] Dragging the in-window mini player's picture never moves the main window.
- [ ] Logs are in `%LOCALAPPDATA%\Wavee\logs`, or the package's `LocalCache` for packaged runs.

---

## 9. Optional follow-ups (P3, after P1/P2 ship)

1. **Aspect-locked resize** (mpv `keepaspect-window` default on, Chrome PiP `SetAspectRatio`, Firefox PiP). Add
   `IPlatformWindow.SetAspectRatio(float clientAspect)` (0 = off) and `IDetachedVideoWindow.SetAspectRatio`, fed from the
   video's `NaturalSize`. Win32 handles `WM_SIZING`, following mpv's `handle_sizing`:
   - derive the client rect from the drag rect;
   - for `WMSZ_TOP`/`WMSZ_BOTTOM`, width = height × aspect; otherwise height = width / aspect;
   - clamp to the min/max track size;
   - anchor the edge opposite the one being dragged;
   - return TRUE.

   This removes the letterbox bars inside a free-sized pop-out.
2. **Host overlays follow the SAME machine.** Add `MediaPlayerElement.ChromeVisibilityOut : Signal<bool>?`, a controlled output that `Sync` writes.
   - The fullscreen surface's title/exit band (`VideoFullscreenSurface.cs:297-313`) seeds its fade from it instead of `HoverOpacity`, which fixes the "stays up while the transport idles" inconsistency.
   - The pop-out can then add a hover-revealed top-right ✕ and title, like Chrome PiP's close and back-to-tab buttons. It lost its only visible close button with the title bar.
3. **Touch drag.** Add `InputHooks.WindowMoveBy(Point2 deltaDip)` → `SetBoundsPx(outer + delta·scale)` from the element's `OnDrag` for touch. There is no snap, but the window follows the finger.

---

## Sources

Windows / Win32:
[WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest) ·
[Custom Window Frame Using DWM](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe) ·
[Snap layouts for desktop apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu) ·
[Snap your windows](https://support.microsoft.com/en-us/windows/snap-your-windows-885a9b1e-a983-a3b1-16cd-c531795e6241) ·
[Title bar window shake](https://support.microsoft.com/en-us/accessibility/windows/make-it-easier-to-focus-on-tasks) ·
[WM_NCLBUTTONDBLCLK](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nclbuttondblclk) ·
[WM_ENTERSIZEMOVE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove) ·
[WM_SIZING](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-sizing) ·
[WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand) ·
[WM_NCMOUSEMOVE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-ncmousemove) ·
[WM_MOUSELEAVE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseleave) ·
[GetSystemMetrics (SM_CXDRAG, SM_CXSIZEFRAME, SM_CXPADDEDBORDER)](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winuser/nf-winuser-getsystemmetrics.md) ·
[DragDetect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-dragdetect) ·
[Windows App SDK title bar / InputNonClientPointerSource](https://raw.githubusercontent.com/MicrosoftDocs/windows-dev-docs/docs/hub/apps/develop/title-bar.md) ·
[NonClientRegionKind](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.input.nonclientregionkind) ·
[InputNonClientPointerSource](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputnonclientpointersource) ·
[Old New Thing: HTCLIENT→HTCAPTION](https://devblogs.microsoft.com/oldnewthing/?p=24583) ·
[Old New Thing: WM_NCHITTEST side effects](https://devblogs.microsoft.com/oldnewthing/20110218-00/?p=11453) ·
[Old New Thing: spurious WM_MOUSEMOVE](https://devblogs.microsoft.com/oldnewthing/20031001-00/?p=42343) ·
[Kerr, DirectComposition window layering (MSDN 2014)](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine) ·
[melak47/BorderlessWindow](https://github.com/melak47/BorderlessWindow) ·
[Chromium: caption click stalls rendering](https://codereview.chromium.org/1504333013)

Players:
[mpv w32_common.c](https://raw.githubusercontent.com/mpv-player/mpv/master/video/out/w32_common.c) ·
[mpv input.c](https://raw.githubusercontent.com/mpv-player/mpv/master/input/input.c) ·
[mpv options.c](https://raw.githubusercontent.com/mpv-player/mpv/master/options/options.c) ·
[mpv input.conf](https://raw.githubusercontent.com/mpv-player/mpv/master/etc/input.conf) ·
[mpv input.rst (begin-vo-dragging)](https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/input.rst) ·
[mpv PR #15405](https://github.com/mpv-player/mpv/pull/15405) ·
[mpv osc.rst](https://raw.githubusercontent.com/mpv-player/mpv/master/DOCS/man/osc.rst) ·
[mpv osc.lua](https://raw.githubusercontent.com/mpv-player/mpv/master/player/lua/osc.lua) ·
[mpv #17536 (hide on leave)](https://github.com/mpv-player/mpv/issues/17536) ·
[Chrome video_overlay_window_views.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/ui/views/overlay/video_overlay_window_views.cc) ·
[Chromium media_controls_impl.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/third_party/blink/renderer/modules/media_controls/media_controls_impl.cc) ·
[Chromium mediaControls.css](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/third_party/blink/renderer/modules/media_controls/resources/mediaControls.css) ·
[Firefox PiP player.css](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/themes/shared/pictureinpicture/player.css) ·
[Firefox PiP player.js](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/components/pictureinpicture/content/player.js) ·
[Firefox videocontrols.js](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/content/widgets/videocontrols.js) ·
[Firefox bug 1535437 (PiP aspect lock)](https://bugzilla.mozilla.org/show_bug.cgi?id=1535437) ·
[Firefox bug 474833](https://bugzilla.mozilla.org/show_bug.cgi?id=474833) ·
[MPC-HC 1.7.x README](https://stable.mpc-hc.org/MPC%20HomeCinema%20-%20x64/MPC-HC_v1.7.3_x64/README.txt) ·
[MPC-HC PR #181](https://github.com/mpc-hc/mpc-hc/pull/181) ·
[VLC libvlc-module.c (mouse-hide-timeout)](https://raw.githubusercontent.com/videolan/vlc/3.0.x/src/libvlc-module.c) ·
[VLC qt.cpp (qt-fs-sensitivity)](https://raw.githubusercontent.com/videolan/vlc/3.0.x/modules/gui/qt/qt.cpp) ·
[YouTube player parameters (autohide)](https://developers.google.com/youtube/player_parameters) ·
[WinUI MediaTransportControls.ShowAndHideAutomatically](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.mediatransportcontrols.showandhideautomatically) ·
[MediaPlayerElement.AreTransportControlsEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.mediaplayerelement.aretransportcontrolsenabled?view=winrt-26100) ·
[Media3 PlayerControlView](https://raw.githubusercontent.com/androidx/media/release/libraries/ui/src/main/java/androidx/media3/ui/PlayerControlView.java) ·
[Media3 PlayerView](https://raw.githubusercontent.com/androidx/media/release/libraries/ui/src/main/java/androidx/media3/ui/PlayerView.java)

Motion:
[Fluent timing and easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing) ·
[Material motion duration and easing](https://m1.material.io/motion/duration-easing.html)
