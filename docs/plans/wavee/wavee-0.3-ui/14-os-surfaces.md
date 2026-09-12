# OS surfaces (SMTC, taskbar, jump list, Windows toasts) — 0.3 visual fidelity contract

> **0.2.9 sources** (all paths relative to `src/apps/Wavee/`; after Wave 0 the same relative paths under
> `src/apps/_old/Wavee/`):
> `App/SystemMediaControlsBridge.cs` (229) · `App/SmtcTimelineCoalescer.cs` (67) · `App/TaskbarBridge.cs` (248) ·
> `App/JumpListBridge.cs` (314) · `App/WaveeAppIcon.cs` (27) — **885 playback-mirror lines**.
> `App/ToastEscalator.cs` (255) · `App/DaylistNotifier.cs` (129) · `App/ReleaseNotifier.cs` (234) ·
> `App/WaveeNativeBoot.cs` (78) · `App/NotificationPrefs.cs` (96) · `App/AppUpdateToasts.cs` (153) ·
> `App/NotificationSimulator.cs` (193) — **1,138 toast lines**.
> Partial owner: `App/NotificationCenterBridge.cs` (377) — only `:55-58, :201-206, :216-287, :289-353` (**~159 raw
> lines**, not the ~135 first written here: the escalation call, the in-app update card, the simulator injection) belong
> here; the merge/filter/read-state half is chapter 19's. Wiring: `App/PlaybackBridge.cs:122-126, 1013-1029, 1443,
> 1506-1508, 1762-1763` · `WaveeApp.cs:212-213` (the two notifiers' composition-root attach) ·
> `Features/Shell/WaveeShell.cs:561-562` (the jump list's two late binds) ·
> `Features/Shell/SettingsPage.Notifications.cs:270-271` (the dial-change reconcile) ·
> `Program.cs:483-488` (cold-launch + redirected activation → `DeepLinkChannel` + `WakeWindow`) — (**~22 lines**).
> Pure rules already engine-free and in Core: `../Wavee.Core/Notifications/NotificationPolicy.cs` (136) —
> `NotifyLevel`, `NotifyTopic`, `QuietHours`, `NotificationPolicy`.
> **Total in scope: ~2,064 lines**, none of which is `Element`-shaped — this is the one chapter whose "UI" is painted by
> Explorer, the Shell and the Action Center.
>
> **0.3 target: settled (A9, plan §2/§9.5).** `Playback/Playback.Os.cs` keeps SMTC, taskbar (button + thumbnail
> toolbar), jump list, the app-icon resolver, media keys and power — **owner H, Wave 3**, budget raised to **840**
> lines (ch 14 §9's own re-budget; §2 previously said 1,100 before this chapter's audit, which itself over-shot by
> naming two things with no 0.2.9 code: media keys are not separately handled — they arrive *through* SMTC; power has
> no code at all). The toast/notification stack moves out entirely: `Platform/Notify.cs` (CORE) +
> `Platform/Notify.Host.cs` (SHELL) — **owner I, Wave 4** (A9, shared with `19-shell-overlays.md`), because its
> dependencies (`LibraryBridge`, `HomeDaylistHydrator`, `HistoryStore`, `IAppSettings`) do not exist until Waves 4-6.

Cross-references (do not re-specify here): the in-app notification panel, the bell, the unread badge, the filter pills
and every notification ROW → `19-shell-overlays.md` (this chapter owns only what leaves the process). The in-app
`Toast.Show` card geometry, stacking and dedupe → `19-shell-overlays.md`. Settings ▸ Notifications (the dials, quiet
hours pickers, the blocked-by-Windows banner, the Send-event affordance's result line) → `27-settings-and-diagnostics.md`.
The "What's new" page the Updated toast deep-links at → `28-setup-whatsnew-feedback.md`. The player bar whose transport
these surfaces mirror → `20-player-bar.md`. Deep-link routing and `ShellRoutes.IsKnown` → `18-shell-frame.md`.
Tokens / type ramp / colour tokens for the ONE in-window element this chapter owns (the sticky download card) →
`00-design-system.md`.

---

## 0. The non-negotiables

1. **Every OS surface reads `PlaybackBridge`'s unified signals, never the engine `IMediaPlayer`.** `CurrentTrack`,
   `IsPlaying`, `IsBuffering`, `CanSkipNext`, `CanSkipPrev`, `PositionMs`, `DurationMs`, `IsLive` — all `.Peek()`ed on
   the UI thread (`SystemMediaControlsBridge.cs:96-134`, `TaskbarBridge.cs:87-91`, `JumpListBridge.cs:102-103`). This
   is the only source that is correct while a *remote Connect device* owns playback; the engine's own `NowPlaying` is
   dark then, and a lock screen that goes blank when the user transfers to their phone is the failure this prevents.
2. **Every OS call is edge-deduped, and every high-rate call is coalesced.** Metadata pushes only on a `Uri` change
   (`SystemMediaControlsBridge.cs:98`), status only on a `MediaPlaybackStatus` change (`:122`), button enablement only
   on a `canNext`/`canPrev` change (`:129`); the taskbar overlay only on an `OverlayKind` change
   (`TaskbarBridge.cs:95`), the progress MODE only on a `ProgressKind` change (`:111`), the thumb buttons only on a
   4-tuple change (`:126-127`). Position ticks go through `SmtcTimelineCoalescer` in **both** bridges
   (`SystemMediaControlsBridge.cs:151`, `TaskbarBridge.cs:144`) — newest-wins latch, one posted flush per burst, plus a
   whole-second gate (`SmtcTimelineCoalescer.cs:60-63`). One `UpdateTimeline` is a WinRT activation plus a
   **cross-process COM RPC (~1 ms)**; N queued ticks must cost one, not N.
3. **The SMTC timeline is zeroed exactly once per transition into LIVE, then silent — and the latch is always
   consumed.** `dur <= 0 && IsLive` ⇒ consume the latch, push `UpdateTimeline(Zero, Zero)` if
   `!_liveTimelineCleared`, set the flag, return (`SystemMediaControlsBridge.cs:167-174`). Without it the Win11 flyout
   keeps the previous track's 3:47 with a thumb frozen mid-bar while a six-hour stream plays; leaving the flag latched
   forever is the other failure, which `:175` prevents by clearing it on the first non-live flush. Underneath,
   `TryTake` **always** clears the scheduled bit (`SmtcTimelineCoalescer.cs:56`, `SystemMediaControlsBridge.cs:169`):
   a bail-out — disposed bridge, zero duration, dedupe hit — must never wedge the latch armed, or the timeline stops
   updating for the rest of the process.
4. **The taskbar button has exactly three states, and Playing carries NO overlay.**
   `overlay = hasTrack && !playing ? Pause : None` (`TaskbarBridge.cs:94`) — the determinate progress fill *is* the
   playing cue, so a play glyph on top of it is redundant. Paused-with-a-track is `pause.ico` with the description
   `"Paused"` (`:103`) plus `TaskbarProgressState.Paused` (yellow, `:121`); Playing is no overlay plus
   `TaskbarProgressState.Normal` (green, `:119-120`); no track is `SetOverlayIcon(hwnd, null, "")` plus
   `ClearProgress` (`:101`, `:117`), and position ticks are *dropped entirely* while `Idle` (`:143`) so a stale
   duration can never repaint a bar that should be gone.
5. **The thumbnail toolbar is added ONCE per HWND, re-added on an Explorer restart, and degrades to tooltips.**
   `SetThumbButtons` first (`TaskbarBridge.cs:168`), `UpdateThumbButton` ×3 after (`:173-175`) — the shell forbids a
   second add. `FluentApp.TaskbarButtonCreated` ⇒ `NotifyTaskbarButtonCreated` + `_thumbsAdded = false` +
   `ApplyThumbs(forceAdd: true)` (`:207-217`); drop that arm and the toolbar is gone for the session after any
   `explorer.exe` restart. A missing `.ico` returns null from `ResolveIcon` (`:219-229`) and a throwing add retries
   with three `IconPath: null` buttons so the tooltips still land (`:180-189`); a null overlay path is an explicit
   clear, never a throw (`:104-105`).
6. **The Jump List rebuild is capped at one per 60 s for track boundaries, and uncapped for a play/pause edge.**
   `RebuildMinIntervalMs = 60_000` (`JumpListBridge.cs:38`); the throttle is skipped when `playChanged`
   (`:115`) because the Pause/Resume task is the one verb that must match the transport, not last minute's skip. A
   skip-storm must not hammer `ICustomDestinationList` (a Begin/Append/Commit COM transaction per rebuild).
7. **The Jump List is published against the AUMID the toast layer registered, never the process default.**
   `ToastNotifier.Default.Aumid` when non-empty, else null (`JumpListBridge.cs:145`). The shell keys a custom
   destination list by AUMID; a mismatch writes the list for an identity the taskbar button does not have and it
   **silently never appears**.
8. **Jump List rows are `wavee://` verbs with resolved titles and `.ico` glyphs — never `spotify:`, never a raw URI,
   never a cover.** Destinations are `wavee://` only (`JumpListBridge.cs:135-138`, `:169`, `:183`); every title walks
   stored play-log title → warm `LibraryStore` → the now-playing track → the kind word (`:190-201`);
   `IShellLinkW.SetIconLocation` accepts only `.ico`/`.exe`/`.dll` (`:28-32`), so recents carry
   `assets/AppIcon/appicon.ico` and the playback task carries `assets/taskbar/{play,pause}.ico` (`:127`, `:132`).
   Album art in the Jump List needs an on-disk icon cache this bridge does not own — do not invent one in 0.3 without
   adding that cache.
9. **The toast watermark is the whole escalation design.** `WaveeSettings.NotifyLastToastedMs` advances past
    everything **CONSIDERED**, not just what was raised (`ToastEscalator.cs:50-54`, `:86`), and a zero watermark
    (first run) raises nothing at all (`:64-65`). Drop either half and enabling notifications replays the whole feed as
    banners — the loudest possible bug in a notification system.
10. **At most 3 individual banners per rebuild; the remainder collapse into one summary.** `MaxPerRebuild = 3`
    (`ToastEscalator.cs:31`, `:80-83`). The loop walks **oldest → newest** (`:69`) so a truncated burst keeps the
    FRESHEST rows and the summary absorbs the stale ones.
11. **An app update is STATE, not an event.** Its row pins `Timestamp = long.MaxValue`, which `TimestampOf` folds to
    "now" *keyed on the sentinel, not on the type* (`ToastEscalator.cs:95-96`), and it escalates only when
    `state:targetQuad` actually changed (`:112-119`). Progress is **never** part of that identity: a moving download
    drives the already-showing toast through `ToastNotifier.Update` at a 5-percentage-point granularity
    (`:107`, `:125-132`), it never raises a second banner.
12. **`AppUpdateState.Available` deliberately raises NO Windows toast** (`ToastEscalator.cs:223-226`, `:238`). The
    in-app strip already offers it; an unprompted Action Center banner for something the user has not started is the
    noisiest version of this feature. Only `Downloading`, `Installing` and `Completed` reach the OS.
13. **Quiet hours SUPPRESS a live banner and SHIFT a scheduled one.** `RaisesToastNow` returns false inside the window
    (`NotificationPolicy.cs:124-125`); `ScheduleAt` returns `Quiet.NextAudible(due)` (`:129-133`). A pre-saved album is
    still out at 03:00 — the user just hears about it at `ToHour`.
14. **Scheduled toasts are re-derived, never trusted, and never near-immediate.** `ReleaseNotifier` rebuilds the whole
    held set from the live saved-set on every launch, saved-set change and dial change (`ReleaseNotifier.cs:103-113`,
    `:129-159`), always `Unschedule`-then-`Schedule` so a slipped date replaces rather than duplicates (`:170`);
    `DaylistNotifier` is keyed on the WINDOW END, so the same window twice is idempotent and a new window supersedes
    (`DaylistNotifier.cs:46`, `:66`). Windows accepts a near-immediate schedule and then silently never paints it, so
    every path enforces a floor: `ReleaseNotifier.MinLead = 1 min` (`:35`, `:97`, `:156`),
    `DaylistNotifier.MinLead = 2 min` (`:23`, `:62`), `NotificationSimulator.ScheduleLead = 3 min`
    (`NotificationSimulator.cs:43`).
15. **No toast exists until `Register` succeeded, and that same registration is the AUMID the jump list uses.**
    `ToastNotifier.Show` **throws** `InvalidOperationException` when `Register` was never called
    (`ToastNotifier.cs:225-226`), and every raise site swallows it (`ToastEscalator.cs:181`) — so a failed or skipped
    registration is not an error anywhere, it is **total silence**. `WaveeNativeBoot.Install` registers only on
    Win10+ and only when `IsSupported` (`WaveeNativeBoot.cs:43-44`); the display name it registers is the literal
    `"Wavee"` (`:53`), which is the name Windows prints in the Action Center header and in Settings ▸ Notifications.
16. **Every OS call is fail-soft.** SMTC construction, every `smtc.*` put, every taskbar call, the whole Jump List
    transaction, every toast raise/schedule/update is wrapped and swallowed
    (`SystemMediaControlsBridge.cs:82-86, 115, 125, 133, 172, 178`; `TaskbarBridge.cs:73-78, 107, 123, 152, 178-189`;
    `JumpListBridge.cs:148-151, 308-312`; `ToastEscalator.cs:148, 181-184, 199`; `ReleaseNotifier.cs:206-210`;
    `DaylistNotifier.cs:81-84`). A banner that fails is never worth failing a feed refresh over; a taskbar that
    refuses is never worth failing playback over.

---

## 1. Anatomy

### 1a. 0.2.9 composition

There is no `Element` tree here. The "component tree" is the fan-out from ONE call site — `PlaybackBridge.PushState` /
`PushPosition` — into four OS surfaces, plus a second, independent fan-out from the notification centre's `Rebuild`.

```
PlaybackBridge.Activate(post)                                       App/PlaybackBridge.cs:1013-1023
│   guard: OperatingSystem.IsWindowsVersionAtLeast(8, 0)            :1013
├─ SystemMediaControlsBridge(this, _player, post)                   :1017   — owner of the media overlay card
│   └─ .Activate(FluentApp.WindowHandle)                            :1018
│       ├─ SystemMediaControls.GetForWindow(hwnd)                   SystemMediaControlsBridge.cs:70
│       ├─ ButtonDispatcher = post          (OS worker → UI thread) :71
│       ├─ ButtonPressed += OnButton        (media keys, headset)   :72, :183-199
│       ├─ PositionChangeRequested += OnSeek(seconds)               :73, :203-213
│       ├─ SetEnabledButtons(play,pause,next,prev = all true)       :74
│       ├─ PlaybackRate = 1.0               (must be > 0)           :75
│       ├─ IsEnabled = true                                         :76
│       └─ seed: OnStateChanged() + OnPositionChanged(Peek())       :79-80
├─ TaskbarBridge(this, _player, post)                               :1019   — owner of the taskbar button
│   └─ .Activate(FluentApp.WindowHandle)                            :1020
│       ├─ ResolveIcon × 4 → prev/play/pause/next .ico              TaskbarBridge.cs:60-63, :219-229
│       ├─ FluentApp.ThumbButtonClicked += OnThumbClick             :66, :193-205
│       ├─ FluentApp.TaskbarButtonCreated += OnTaskbarCreated       :67, :207-217
│       ├─ ApplyThumbs(forceAdd: true)                              :68, :155-191
│       └─ seed: OnStateChanged() + OnPositionChanged(Peek())       :70-71  (after _active = true, :69)
├─ JumpListBridge(this, _player, post)                              :1021   — owner of the jump list
│   └─ .Activate()                                                  :1022  (STA; no HWND — AUMID-keyed)
│       └─ Rebuild()                                                JumpListBridge.cs:120-152
│           ├─ bail: Environment.ProcessPath empty ⇒ no list        :125-126
│           ├─ tasks[2]  Pause|Resume · Search                      :133-139
│           ├─ BuildCategory(exe, icon) → ≤ 6 items                 :140, :154-188
│           └─ JumpList.SetCategory("Jump back in", items, tasks, aumid)  :146
│               └─ AppendCategory is SKIPPED when the array is empty  JumpList.cs:230-236
├─ WaveeNativeBoot.Install(post)                                    :1023   — owner of the toast identity
│   ├─ ToastNotifier.Default.ActivationDispatcher = post            WaveeNativeBoot.cs:39
│   ├─ ToastNotifier.Default.Activated += OnActivated               :40, :55-62
│   ├─ gate: IsWindowsVersionAtLeast(10,0) && IsSupported           :43-44
│   └─ Register(ToastActivatorClsid, "Wavee", WaveeAppIcon.Path())  :53
│       └─ CLSID C8E4A91B-3D52-4F07-9B6A-1E7C4D8F2A30               :23
└─ AppUpdateScheduler.Start(updater, settings)                       :1024-1029  — the cadence behind W22/W23/W26-28
    └─ first check 30 s after launch, then every 24 h                AppUpdateScheduler.cs:20-21, :38, :50

Late binds — NOT in the Activate fan-out (the shell does not exist yet when it runs)
├─ WaveeShell.cs:561-562  → JumpList.AttachHistory(HistoryStore) / AttachLibrary(LibraryStore)
├─ WaveeApp.cs:212-213    → ReleaseNotifier.Attach(settings, library, preRelease) · DaylistNotifier.Attach(settings)
└─ SettingsPage.Notifications.cs:270-271 → both notifiers’ RequestReconcile on any dial change

PlaybackBridge.PushState(s)                                         App/PlaybackBridge.cs:1506-1508
├─ _smtc?.OnStateChanged()      metadata / status / prev-next       SystemMediaControlsBridge.cs:92-135
├─ _taskbar?.OnStateChanged()   overlay / progress mode / thumbs    TaskbarBridge.cs:83-136
└─ _jumpList?.OnStateChanged()  dirty + 60 s throttle               JumpListBridge.cs:99-118

PlaybackBridge.PushPosition(sample)                                 App/PlaybackBridge.cs:1762-1763
├─ _smtc?.OnPositionChanged(ms) → SmtcTimelineCoalescer.Push        SystemMediaControlsBridge.cs:148-152
└─ _taskbar?.OnPositionChanged(ms) → its own coalescer              TaskbarBridge.cs:140-145

NotificationCenterBridge.Rebuild()                                  App/NotificationCenterBridge.cs:178-206
└─ _lastEscalated = ToastEscalator.Consider(_settings, items)       :203
    ├─ gate: ToastNotifier.IsSupported (== !elevated)               ToastEscalator.cs:42
    ├─ watermark scan (newest real timestamp)                       :50-54
    ├─ live download toast update (before the loop)                 :59-61, :125-132
    ├─ escalation loop oldest→newest, ≤ MaxPerRebuild(3)            :69-82
    │   └─ TryRaise → ToastBuilder → ToastNotifier.Default.Show     :151-185
    ├─ RaiseSummary(suppressed)                                     :83, :187-200
    └─ settings.Set(NotifyLastToastedMs, newest)                    :86

NotificationCenterBridge.ConsiderUpdateToast(next)                  :216-242   (IN-APP card, not an OS toast)
├─ UpdateProgress.Value = clamp(pct/100)                            :223
├─ AppUpdateToasts.Plan(previous, next) → ToastPlan?                :225   (pure; AppUpdateToasts.cs:55-110)
├─ gate: NotificationPrefs.Level(AppUpdates) != Off                 :227
└─ Toast.Show(plan.Body, ToastOptions{…, CustomContent})            :232-241 → ch. 19

DaylistNotifier.Note(ctxUri, expiresAtUnixMs, title)                DaylistNotifier.cs:40-85
└─ fed by SpotifyLive.HomeDaylistHydrator.WindowObserved            :35   (a DATA path, never a render)

ReleaseNotifier.Attach / OnSavedChanged / RequestReconcile          ReleaseNotifier.cs:46-62, :64-83, :103-113
└─ fed by LibraryBridge.SetSaved (the one save chokepoint)          :64-65
```

### 1b. 0.3 composition

Props do not freeze here — there are no components. What replaces the props contract is the **push contract**: every
surface is a *sink* that is called from the playback drain or the notification rebuild, and holds its own last-pushed
snapshot as private fields. Live data reaches a sink in exactly one of three shapes, and 0.3 must keep all three.

| Sink | Fed by | Live-data shape | Holds |
|---|---|---|---|
| `Playback.Os.Smtc` | `Playback` drain → `PushState`/`PushPosition` | direct method call on the UI thread | `_lastUri`, `_lastStatus`, `_lastCanNext`/`_lastCanPrev`, `_timeline` (struct latch), `_liveTimelineCleared` |
| `Playback.Os.Taskbar` | same drain | direct method call | `_lastOverlay`, `_lastProgress`, `_lastCanPrev`/`_lastCanNext`/`_lastPlaying`/`_lastHasTrack`, `_haveThumbState`, `_thumbsAdded`, `_timeline`, four resolved `.ico` paths |
| `Playback.Os.JumpList` | same drain **+** `AttachHistory`/`AttachLibrary` late-binding | direct call + two optional store handles that may arrive before or after `Activate` | `_dirty`, `_lastRebuildTick`, `_lastTrackUri`, `_lastPlaying`, `_havePlayState` |
| `Notify.Escalator` | `Notify.Center.Rebuild()` → one `Consider(settings, items)` | a whole freshly-merged list, newest-first | `static s_lastUpdateRaised`, `static s_lastProgressPushed` (process lifetime, deliberately) |
| `Notify.Drops` / `Notify.Daylist` | `Library.SetSaved` / the home daylist hydrate | a single fact (uri+saved / windowEnd+title) | `static HashSet<string> Scheduled`, `static long _scheduledFor` — **process-local and never persisted**, while the OS entry they describe outlives the process (DATA GAP 10) |

```
Playback/Playback.cs           CORE  + SmtcTimelineCoalescer (67, verbatim)
Playback/Playback.Os.cs        SHELL   Smtc (229) · Taskbar (248) · JumpList (314) · AppIcon (27)      ~830
Platform/Notify.cs             CORE    NotificationPolicy (136) · NotificationPrefs (96) ·
                                       AppUpdateToasts (153) · EscalationPlan (new)  owner I, Wave 4  ~445
Platform/Notify.Host.cs        SHELL   Escalator (255) · Daylist (129) · Drops (234) ·
                                       ToastBoot (78)                              owner I, Wave 4  ~715
Screens/Diagnostics.Probe.cs   SHELL   NotificationSimulator (193)                → ch. 27             ~195
Shell/Shell.UI.cs              UI      the in-app update card arms of the centre  → ch. 19             ~135
```

**One notification stack, and it is not this chapter’s alone** (arbitration 2026-09-12). `Platform/Notify.cs` +
`Platform/Notify.Host.cs` are the ONLY home for policy, prefs, escalation, the feed’s merge/filter/read-state and
`AppUpdateToasts`: there is no `Screens/ReleaseNotes.cs` copy of the update-toast table and no second
`Entities/Notification.*` pair. `Notify.cs`’s **~445** above is this chapter’s half of that one CORE file — ch. 19’s
feed fold (merge, read-ids, the row model, the topic-visibility recount) is the other ~555, so the whole file is
**~1,000**. `Notify.Host.cs`’s **~715** is the whole file; ch. 19 adds nothing to it. The bell, the panel and its rows
stay in `Shell/Shell.UI.cs` (ch. 19) — which is why both halves are **owner I in Wave 4**, not owner S in Wave 6.

---

## 2. Wireframes

**Geometry caveat, stated once.** Explorer, the Shell and the Action Center own every pixel of W1-W25. Wavee supplies
*fields*, not a layout: a title, an artist, an album, an image reference, a status enum, four enable bits, a
(position, duration) pair, an `.ico` path, a tooltip string, a `<toast>` XML document ≤ 5,120 bytes
(`ToastBuilder.cs:62`). The boxes below are **schematic at the width noted**, and every annotation marks which field
Wavee actually sets and from where. Where a dimension is the OS's it is written `OS-owned`. Nothing in this section
may be re-derived as an app-side layout constant.

**Long text, stated once.** Wavee truncates NOTHING on these surfaces and must not start: the SMTC card's three
strings, a toast's title and body, a jump list row's label and a category title are all handed over whole and
ellipsised by the Shell at whatever width it happens to be. The two real caps are other people's:
a thumb-button tooltip is truncated to **259 chars** by the engine (`TaskbarManager.cs:18`, the `THUMBBUTTON.szTip`
ceiling) and the whole `<toast>` XML must stay under **5,120 bytes** (`ToastBuilder.cs:62`, which *throws* past it —
inside `TryRaise`'s catch, so an over-long payload is a silently dropped banner). A 0.3 that adds an app-side
`Ellipsize`/`Substring` to any field in this chapter is a regression, not a polish pass.

### W1 — SMTC media overlay card, Playing, seekable track @ 360 schematic (OS-owned)

```
┌──────────────────────────────────────────────────────────────────────┐
│  ┌────────────┐   Midnight City                    ← Title           │  Title  = track.Title ?? ""
│  │            │   M83                              ← Artist          │           SystemMediaControlsBridge.cs:112
│  │  ARTWORK   │   Hurry Up, We're Dreaming         ← AlbumTitle      │  Artist = track.Artists[0].Name      :109
│  │            │                                                      │           (FIRST artist ONLY — never joined)
│  └────────────┘                                                      │  Album  = "" ⇒ null                 :110
│   put_Thumbnail                                                      │  Art    = track.Image.Url ⇒ null if ""  :111
│   RandomAccessStreamReference.CreateFromUri(track.Image.Url)         │       NOTE: Image.Url, never LargestUrl
│   SystemMediaControls.cs:520-538 — the OS fetches+decodes the bytes  │       (Models.cs:28-45 — Url is the small
│   asynchronously; NO app-side decode, NO app-side size              │        rendition; LargestUrl is untouched)
│                                                                      │
│      ⏮          ⏸/▶          ⏭                                       │  SetEnabledButtons(play:true, pause:true,
│   prev:ENABLED  play/pause  next:ENABLED                             │    next:CanSkipNext, previous:CanSkipPrev)
│                                                                      │                                     :133
│  ├──────────────●───────────────────────────────────┤  1:24 / 4:03   │  UpdateTimeline(pos, dur)           :178
│  scrub bar: StartTime=0 EndTime=dur Position=pos                     │  put_StartTime/EndTime/Position/
│             MinSeekTime=StartTime MaxSeekTime=EndTime                │  MinSeekTime/MaxSeekTime
│             SystemMediaControls.cs:271-275                           │  cadence: ≤ 1 Hz (whole-second gate)
└──────────────────────────────────────────────────────────────────────┘
   PlaybackStatus = Playing (MediaPlaybackStatus)                         SystemMediaControlsBridge.cs:120
   PlaybackRate   = 1.0                                                   :75
```

### W2 — SMTC card, Paused @ 360 schematic

```
┌──────────────────────────────────────────────────────────────────────┐
│  [ART]  Midnight City / M83 / Hurry Up, We're Dreaming               │
│      ⏮          ▶          ⏭                                         │  Only DIFFERENCE from W1:
│  ├──────────────●───────────────────────────────────┤  1:24 / 4:03   │  SetPlaybackStatus(Paused)          :121
└──────────────────────────────────────────────────────────────────────┘  ONE WinRT call — nothing else is re-pushed
                                                                          (the uri did not change ⇒ :98 skips)
```

### W3 — SMTC card, Buffering / track change in flight @ 360 schematic

```
┌──────────────────────────────────────────────────────────────────────┐
│  [ART]  …                                                            │  status = IsBuffering ? Changing : …
│      ⏮          ◌          ⏭                                         │                                     :119
│  the flyout renders Changing as a transport in flight                │  Ordering matters: Changing is tested
└──────────────────────────────────────────────────────────────────────┘  BEFORE IsPlaying (:118-121), so a
                                                                          buffering-while-playing tick is Changing
```

### W4 — SMTC card, LIVE broadcast (module stream) @ 360 schematic

```
┌──────────────────────────────────────────────────────────────────────┐
│  [ART]  <stream title> / <creator>                                   │
│      ⏮          ⏸          ⏭                                         │
│  ├──────────────────────────────────────────────────┤   —   /   —    │  UpdateTimeline(Zero, Zero) — ONCE
│  StartTime=0 EndTime=0 Position=0                                    │  SystemMediaControlsBridge.cs:172
│  then SILENCE for the whole stream (_liveTimelineCleared = true)     │  :167-174
└──────────────────────────────────────────────────────────────────────┘  The failure this prevents: the PREVIOUS
                                                                          track's 3:47 frozen mid-bar for six hours
```

### W5 — SMTC card, no track @ 360 schematic

```
┌──────────────────────────────────────────────────────────────────────┐
│                (no Wavee card in the flyout at all)                  │  track is null ⇒ smtc.ClearDisplay()  :105
│                                                                      │  status = MediaPlaybackStatus.Closed  :118
└──────────────────────────────────────────────────────────────────────┘  IsEnabled stays TRUE (:76) — the session
                                                                          is alive, it just has nothing to show
```

### W6 — SMTC card, first track of a queue (no previous) @ 360 schematic

```
┌──────────────────────────────────────────────────────────────────────┐
│  [ART]  Track 1 / Artist                                             │
│      ⏮(grey)      ⏸          ⏭                                       │  SetEnabledButtons(play:true, pause:true,
│                                                                      │    next:true, previous:false)        :133
│  Grey ⇒ the hardware PREV key does nothing, by design                │  fires ONLY when CanSkipPrev flipped :129
└──────────────────────────────────────────────────────────────────────┘
```

### W7 — Taskbar button, Playing @ 44×40 px button, 100% DPI (OS-owned)

```
        ┌──────────┐
        │          │   ← app icon (window class icon; assets/AppIcon/appicon.ico, 7 frames:
        │  [icon]  │      16,24,32,48,64,128,256 @ 32 bpp, 122,347 B — read from the file's
        │          │      ICONDIR; generated by assets/AppIcon/generate-appicon.ps1:77.
        │          │      WaveeAppIcon.cs is 27 lines and only RESOLVES the path (:15-26))
        │          │
        │▓▓▓▓▓░░░░░│   ← determinate progress, SetProgressState(Normal) + SetProgress(pos, dur)
        └──────────┘      TaskbarBridge.cs:119-120, :152
         no overlay       overlay = None (:94) — the fill IS the playing cue (non-negotiable 4)
         fill = pos/dur, refreshed at ≤ 1 Hz through the SAME coalescer as SMTC (:144)
```

### W8 — Taskbar button, Paused @ 44×40 px, 100% DPI

```
        ┌──────────┐
        │  [icon]  │
        │        ⏸ │   ← overlay icon: assets/taskbar/pause.ico, description "Paused"
        │▒▒▒▒▒░░░░░│      TaskbarBridge.cs:103
        └──────────┘      loaded LoadImageW(IMAGE_ICON, 0, 0, LR_LOADFROMFILE|LR_DEFAULTSIZE)
         SetProgressState(Paused)   TaskbarManager.cs:286-288 ⇒ SM_CXICON (32×32 @ 100%), NOT SM_CXSMICON
         TaskbarBridge.cs:121       pause.ico frames: 16×16 (1,128 B) + 32×32 (4,264 B), 32 bpp
                                    ⇒ above 200% DPI the 32×32 frame is the ceiling and Windows UPSCALES
```

### W9 — Taskbar button, no track @ 44×40 px, 100% DPI

```
        ┌──────────┐
        │  [icon]  │   overlay: SetOverlayIcon(hwnd, null, "")   TaskbarBridge.cs:101
        │          │   progress: ClearProgress(hwnd)             :117
        └──────────┘   position ticks are DROPPED (:143) — a stale duration can never repaint the bar
```

### W10 — Thumbnail toolbar, Playing, everything available @ OS-owned (thumb ≈ 200×120 px)

```
        ┌───────────────────────────────┐
        │                               │
        │      (window thumbnail)       │
        │                               │
        ├───────────────────────────────┤
        │        ⏮      ⏸      ⏭        │   3 of the shell's 7-button cap (TaskbarManager.cs:93)
        └───────────────────────────────┘
           id=1    id=2    id=3            TaskbarBridge.cs:24  IdPrev=1, IdPlayPause=2, IdNext=3
           prev.ico pause.ico next.ico     :160-163
           "Previous" "Pause" "Next"       tooltips — hard-coded English at the CALL SITE even though
                                           taskbar.previous/play/pause/next already exist in
                                           assets/loc/en-US.json (see §7 DATA GAP 3)
           enabled: CanSkipPrev / hasTrack / CanSkipNext          :160-163
           glyph frames per .ico: 16×16 (1,128 B) + 32×32 (4,264 B), 32 bpp
           tooltip cap 259 chars (THUMBBUTTON.szTip — TaskbarManager.cs:18)
           DismissOnClick defaults FALSE and the bridge never sets it (TaskbarBridge.cs:160-163;
             ThumbButton — TaskbarManager.cs:22): the thumbnail flyout STAYS OPEN after a click, which is
             what makes prev→prev→next work without re-hovering. Hover/pressed/focus are OS-drawn; the only
             app-settable visual state is Enabled (THBF_DISABLED).
```

### W11 — Thumbnail toolbar, Paused, no previous @ OS-owned

```
        ├───────────────────────────────┤
        │        ⏮      ▶      ⏭        │   mid glyph = play.ico, tooltip "Play"   TaskbarBridge.cs:161-162
        └───────────────────────────────┘   ⏮ is THBF_DISABLED (Enabled: _lastCanPrev == false)   :160
           grey    live   live
```

### W12 — Thumbnail toolbar, no track @ OS-owned

```
        ├───────────────────────────────┤
        │        ⏮      ▶      ⏭        │   mid: Enabled = hasTrack == false ⇒ disabled   TaskbarBridge.cs:162
        └───────────────────────────────┘   outer two follow CanSkipPrev / CanSkipNext, which the bridge does NOT
           grey    grey   grey              force to false — they are whatever the unified state says
```

### W13 — Thumbnail toolbar, icon load failed (dev tree without the content copy) @ OS-owned

```
        ├───────────────────────────────┤
        │       [ ]     [ ]     [ ]     │   ResolveIcon returned null (file absent)   TaskbarBridge.cs:219-229
        └───────────────────────────────┘   OR SetThumbButtons threw ⇒ retry with IconPath: null   :183-188
         "Previous"  "Pause"  "Next"        Tooltips and CLICKS still work. This is a DEGRADED state, not an error
                                            state — nothing is logged, nothing is surfaced.
```

### W14 — Jump list, playing, full category @ OS-owned (list ≈ 260 wide)

```
┌────────────────────────────────────────────┐
│  Jump back in                              │  category title — HARD-CODED ENGLISH   JumpListBridge.cs:146
│  ┌──┐                                      │  cap: CategoryCap = 6                  :37
│  │▣ │ Hurry Up, We're Dreaming             │  icon = WaveeAppIcon.Path() ?? exe     :127
│  │  │   (hover tooltip = the RAW spotify: uri)│  the 5th JumpListItem arg is Description, and the bridge
│  │  │                                        │  passes row.Uri (play log) / the route name (history)
│  │  │                                        │  — :169, :183; JumpList.cs:44-45. Non-negotiable 8 holds
│  │  │                                        │  for the LABEL only; the tooltip IS a raw uri today.
│  ├──┤   arg wavee://open?route=album:spotify:album:…    :169, TryRoute :292
│  │▣ │ Discover Weekly                      │  title: play-log stored title → LibraryStore → now-playing → kind
│  ├──┤   arg wavee://open?route=pl:spotify:playlist:…    :190-201, :294
│  │▣ │ M83                                  │  arg …route=artist:spotify:artist:…    :295
│  ├──┤                                      │
│  │▣ │ Liked Songs                          │  arg …route=liked  (loc: detail.likedSongs)   :296, :230
│  ├──┤                                      │
│  │▣ │ Radiolab                             │  arg …route=show:spotify:show:…        :296
│  ├──┤                                      │
│  │▣ │ Deep Focus                           │  6th and last — sources in order:
│  └──┘                                      │    1. PlayLog.RecentContexts(16), context-collapsed, None/Other
│                                            │       skipped, deduped by ROUTE           :162-170
│  ─────────────────────────────────────     │    2. HistoryStore.Entries walked BACKWARD (newest first),
│  Tasks                                     │       RecentSurfaceRoute.TryClassify-able only, deduped by
│  ┌──┐                                      │       route NAME                          :176-185
│  │⏸ │                                       │    both halves share ONE `seen` set and the SAME key space
│  │  │                                       │    ("album:<uri>"), so a surface visited AND played appears once
│  │  │                                       │    (:156, :168, :181). "liked" is play-log only — TryClassify
│  │  │                                       │    knows album/pl/artist/show and nothing else (RecentSurfaceRoute.cs:27-35)
│  │⏸ │ Pause            "Pause playback"    │  task 1: assets/taskbar/pause.ico, wavee://pause    :135-137
│  ├──┤                                      │
│  │▣ │ Search           "Search"            │  task 2: appicon.ico, wavee://open?route=search     :138
│  └──┘                                      │  ALL FIVE strings hard-coded English at the call site, though
│                                            │  jumplist.pause/resume/pausePlayback/resumePlayback/search/
│                                            │  jumpBackIn already exist in the catalogue (§7 DATA GAP 3)
│  ─────────────────────────────────────     │
│  Pin / Remove from this list                │  OS-owned. A user-removed item MUST NOT be re-added in the same
└────────────────────────────────────────────┘  transaction or CommitList fails — JumpList.cs filters by
                                                GetArguments string (JumpList.cs:81-88), which can shrink the
                                                published array below 6 — or to ZERO, which drops the heading.
                                                BeginList also reports the shell's visible slot count (maxSlots,
                                                JumpList.cs:209-211) and NOTHING reads it: a user whose "number of
                                                recent items" is set low sees fewer than 6 rows and the bridge
                                                never learns. Do not "fix" this without deciding what 6 means.
```

### W15 — Jump list, paused @ OS-owned

```
│  Tasks                                     │  ONLY the first task changes:
│  ┌──┐                                      │    title "Resume", icon play.ico, arg wavee://resume,
│  │▶ │ Resume          "Resume playback"    │    description "Resume playback"       JumpListBridge.cs:135-137
│  ├──┤                                      │  Rebuilt IMMEDIATELY (the 60 s throttle is skipped on a play/pause
│  │▣ │ Search          "Search"             │  edge — :115), because this verb must match the transport.
```

### W16 — Jump list, cold start (no play log, no history) @ OS-owned

```
┌────────────────────────────────────────────┐
│  Tasks                                     │  BuildCategory returns [] (:187) ⇒ SetCategory is called with an
│  ┌──┐                                      │  EMPTY array, and `AppendCategory` is then NEVER called
│  │▶ │ Resume                               │  (JumpList.cs:230-236 guards on count > 0) — so the heading is NOT
│  ├──┤                                      │  drawn at all. This was written UNVERIFIED; the engine answers it.
│  │▣ │ Search                               │  Parity item 22 keeps the visual confirmation, not the question.
│  ├──┤                                      │
│  │▣ │ Search                               │
│  └──┘                                      │
└────────────────────────────────────────────┘
```

### W17 — Windows toast: new ALBUM release @ 364×~100 px (OS-owned ToastGeneric)

```
┌──────────────────────────────────────────────────────────────┐
│ ┌────┐  Fantasma                            ← text[0] Title  │  Title = r.Name             ToastEscalator.cs:208
│ │ ▣  │  New release — Cornelius             ← text[1] Body   │  Body  = "New release — " + r.CreatorName   :209
│ └────┘                                                       │        HARD-CODED ENGLISH, em-dash joined
│  appLogoOverride, square (circle: false)                     │  Image = r.ImageUrl → ToastImageCache.Localize
│  ToastImageCache.Default.Localize(url)   :173                │        :173, ToastImageCache.cs:65-85
│  packaged ⇒ https:// passthrough (ToastImageCache.cs:133)    │  Launch= wavee://play?ctx={escaped r.Uri}   :210
│  unpackaged ⇒ downloaded once to                             │  Tag   = "live:" + n.Id   (= the entity uri)  :244
│  %LOCALAPPDATA%\FluentGpu\toastimg\{sha256(url)}{ext}        │        e.g. "live:spotify:album:4aawyAB9…" (41 ch)
│  and referenced as file:///…  (:49, :146-157)                │  Group = "wavee.live"                       :33
│  NO app-side decode, NO app-side resize                      │  Silent when policy.Sound == false          :168
└──────────────────────────────────────────────────────────────┘  Clicking the BODY fires Launch (whole-toast
                                                                   activation) — this toast has no buttons.
   NO IMAGE (ImageUrl null/empty, or Localize threw): `AppLogo` is simply never called (`:170`, `:174`) and the
   banner is the two text lines alone, left-aligned where the square would have been. This is the COMMON case for
   a feed row whose art field the server omitted — not an error state, nothing is logged.
   LOCALIZE FAILED (unpackaged, CDN down): `Localize` returns the ORIGINAL https URL (`ToastImageCache.cs:82-83`),
   which the unpackaged platform silently drops — same picture as "no image", by a different road.
```

### W18 — Windows toast: new EPISODE @ 364×~100 px

```
┌──────────────────────────────────────────────────────────────┐
│ ┌────┐  The Cave                                             │  Identical shape to W17; ONLY the body differs:
│ │ ▣  │  New episode — Radiolab                               │  r.Kind == Episode ⇒ "New episode — "  :209
│ └────┘                                                       │  Topic = NotifyTopic.NewEpisodes (its OWN dial)
└──────────────────────────────────────────────────────────────┘         NotificationPrefs.cs:91 (TopicOf)
```

### W19 — Windows toast: concert announcement @ 364×~100 px

```
┌──────────────────────────────────────────────────────────────┐
│ ┌────┐  New Keenan Te show just announced near you           │  Title = s.Title VERBATIM — the feed's title is
│ │ ▣  │  Keenan Te                                            │          already a finished, server-LOCALIZED
│ └────┘                                                       │          sentence (ToastEscalator.cs:215-217)
│  square appLogo (topic == Concerts ⇒ circle: false)   :173   │  Body  = SpotifyUpdates.ActName(s) ?? ""    :217
│  ⚠ the real title usually LEADS WITH A GLYPH:                │  `SpotifyUpdates.CleanTitle` (SpotifyUpdates.cs:74-96)
│    "🎵 New Keenan Te show just announced near you"           │  strips it — and is called by Home's timeline ONLY
│    "⏰ Just days away: Porter Robinson live in New York"      │  (HomeModules.Timeline.cs:110). The toast (:216) and
│                                                              │  the centre row take s.Title RAW, so the same event
│                                                              │  reads with an emoji here and without one on Home.
│                                                              │  Launch= "wavee://open?route=" + escape(ActionUri)
└──────────────────────────────────────────────────────────────┘          :218-220  ← 0.2.9 SEE §7 DATA GAP 1: this is
                                                                          a RAW spotify:concert: / https uri, not a
                                                                          route key. ShellRoutes.IsKnown rejects it.
                                                                          0.3 (Q2, 2026-09-12): routed through
                                                                          RichText.RouteForUri first — a deliberate
                                                                          fix, (#n), not a straight port.
```

### W20 — Windows toast: new follower @ 364×~100 px

```
┌──────────────────────────────────────────────────────────────┐
│  ╭────╮  Ada started following you                           │  circle: topic == NotifyTopic.Followers
│  │ ☺  │  Ada                                                 │  ToastEscalator.cs:173 — hint-crop='circle'
│  ╰────╯                                                      │  (ToastBuilder.cs:145-149)
│  appLogoOverride hint-crop='circle'                          │  Launch: only when ActionType == Navigate AND
│  THE ONLY SURFACE IN WAVEE THAT CROPS A CIRCLE               │  ActionUri is non-empty; otherwise null (no
└──────────────────────────────────────────────────────────────┘  launch arg ⇒ the toast just opens the app)  :218
```

### W21 — Windows toast: burst summary @ 364×~84 px

```
┌──────────────────────────────────────────────────────────────┐
│  4 more updates in Wavee                                     │  Title: more == 1 ? "1 more update in Wavee"
│  Open Wavee to see them.                                     │         : more + " more updates in Wavee"   :192
│                                                              │  Body : "Open Wavee to see them."           :193
│  no image                                                    │  Launch: wavee://open?route=home            :194
│  Tag = "live-summary", Group = "wavee.live"          :195    │  Raised ONCE per rebuild, after the loop    :83
└──────────────────────────────────────────────────────────────┘  Hard-coded English + a hand-rolled plural at the
                                                                  CALL SITE — while `toast.moreUpdates` in the
                                                                  catalogue is already a proper ICU plural
                                                                  ("{n, plural, one {1 more update in Wavee}
                                                                  other {# more updates in Wavee}}") and
                                                                  `toast.openToSee` is the body. §7 DATA GAP 3.
```

### W22 — Windows toast: app update DOWNLOADING, data-bound progress @ 364×~120 px

```
┌──────────────────────────────────────────────────────────────┐
│  Updating Wavee                                    ← text[0] │  Title = Loc.Get(update.os.downloading)
│                                                              │          "Updating Wavee"  ToastEscalator.cs:230
│  ├████████████░░░░░░░░░░░░░░░░░░░░░░░░░┤                     │  <progress> dataBound: true                :164
│  Downloading Breaker…                                        │  progressValue  = (pct/100).ToString("0.###",
│                                                              │                     InvariantCulture)      :141
│  bound placeholders: {progressValue} {progressStatus}        │  progressStatus = pct >= 100
│  ToastBuilder.cs:92 — BindValue / BindStatus                 │      ? Loc.Get(update.state.installing)
│                                                              │      : Strings.Update.Toast.Downloading(name)
│  pushed via ToastNotifier.Update(values, tag, group)  :138   │        (update.toast.downloading)          :142-145
│  tag "live:update", group "wavee.live"                       │  cadence: 5 percentage points, plus an always-
│  granularity ProgressStepPercent = 5                  :107   │  allowed push at exactly 100               :129
│  no image, no buttons                                        │  NotificationNotFound (expired/dismissed) is NOT
└──────────────────────────────────────────────────────────────┘  an error and is NOT re-raised          :122-124
   The progress push runs BEFORE the gate ladder (`:59-61` vs `:65`), so it is NOT gated by the master dial, the
   watermark or quiet hours. What gates it instead is `s_lastProgressPushed < 0` (`:128`) — the latch that only a
   successful, fully-gated raise arms (`:165`, `:177-178`) and that `UpdateChanged` disarms on the way out of
   Downloading (`:117`). Ports that "tidy" the order or the latch push into a toast that was never raised.
```

### W23 — Windows toast: app update COMPLETED @ 364×~100 px

```
┌──────────────────────────────────────────────────────────────┐
│  Wavee updated to Breaker                                    │  Title = Strings.Update.Os.Updated(name)
│  See what's new                                              │          "Wavee updated to {name}" (update.os.updated)
│                                                              │  Body  = Loc.Get(update.os.seeWhatsNew)  :234-235
│  no image, no buttons                                        │  Launch= wavee://open?route=whatsnew[&arg={semver}]
│                                                              │          :236, WhatsNewArg :248-254
└──────────────────────────────────────────────────────────────┘  arg omitted entirely when unknown, so the route
                                                                  lands on the NEWEST release rather than on nothing.
   Available: NO OS TOAST AT ALL (:223-226, :238)   Failed / Snoozed / Checking / None: NO OS TOAST (:238)
```

### W23b — Windows toast: app update INSTALLING @ 364×~84 px

```
┌──────────────────────────────────────────────────────────────┐
│  Restarting to finish updating…                              │  Title = Loc.Get(update.state.installing)
│                                                              │          ToastEscalator.cs:231-232
│  no body, no image, no buttons, NO progress element          │  Body = "", Launch = null, Image = null
│                                                              │  Reached only when the state MOVES to Installing
└──────────────────────────────────────────────────────────────┘  (UpdateChanged, :112-119), which also resets
                                                                  s_lastProgressPushed to -1 (:117) so the dead
                                                                  Downloading toast stops being written to.
   The third of the three states that reach the OS (non-negotiable 12) and the one this chapter had no frame for.
   Note the Windows banner and the IN-APP card disagree on purpose: the card puts the same sentence in the BODY
   with an empty title (W27's sibling — AppUpdateToasts.cs:84-89), the banner puts it in the TITLE.
```

### W24 — Scheduled toast: pre-saved album is out @ 364×~200 px (hero)

```
┌──────────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────────────────────────┐ │  heroImage = link.Cover.Url → Localize()
│ │                                                          │ │             ReleaseNotifier.cs:188-192
│ │                    HERO COVER                            │ │  (a Hero, not an appLogo — the only Wavee toast
│ │                                                          │ │   that uses one; ToastBuilder.cs:155)
│ └──────────────────────────────────────────────────────────┘ │
│  Fantasma                                                    │  Title = link.Name, else "New release"    :172
│  Cornelius — out now                                         │  Body  = artist + " — out now", else "Out now"  :180
│                                                              │  Launch= wavee://open?route=album&arg={AlbumUri}
│  ┌───────────┐  ┌───────────┐                                │          :181  ← route+arg form, composed by the
│  │   Play    │  │  Dismiss  │                                │          shell into "album:<uri>" (WaveeShell.cs:1812)
│  └───────────┘  └───────────┘                                │  Button "Play" → wavee://play?ctx={AlbumUri}  :182
│                                                              │  DismissButton() default label "Dismiss"      :183
│  Tag = "drop:" + preReleaseUri   :233                        │  Group = "wavee.release-drops"                :31
│  delivery = Quiet.NextAudible(due.ToLocalTime())  :200       │  Strings hard-coded at the call site; `toast.outNow`
│                                                              │  ("{artist} — out now"), `toast.outNowGeneric`
│                                                              │  ("Out now") and `toast.play` already exist. The
│                                                              │  title fallback "New release" has NO key. "Dismiss"
│                                                              │  is the ENGINE's default parameter, not Wavee's
│                                                              │  string (ToastBuilder.cs:221) — localizing it means
│                                                              │  passing DismissButton(Loc.Get(update.action.dismiss)).
└──────────────────────────────────────────────────────────────┘  The ALBUM uri is what plays, not the prerelease id
                                                                   (they are unrelated ids) — :174-176
   NO HERO when `link.Cover?.Url` is empty or `Localize` throws (`:188-192`): a text-only drop banner with two
   buttons. Also note this is the toast that in 0.2.9 is the ONE that never calls `Silent()` (DATA GAP 4) — 0.3
   fixes this (Q3, 2026-09-12): `ReleaseNotifier` gains the same `policy.Sound` check as the other two schedulers,
   a deliberate divergence, `(#n)` — and the one whose delivery instant is `NextAudible(due)` rather than `due`.
```

### W25 — Scheduled toast: daylist rolled over @ 364×~100 px

```
┌──────────────────────────────────────────────────────────────┐
│  Your daylist has refreshed                                  │  Title = "Your daylist has refreshed"
│  It moved on from monday morning gentle indie                │           DaylistNotifier.cs:68
│                                                              │  Body  = title is non-empty
│  no image, no buttons                                        │          ? "It moved on from " + title
│                                                              │          : "A new mix is waiting."          :71
│  Tag = "daylist-roll", Group = "wavee.daylist"   :18-19      │  the NEXT window's name is unknowable at schedule
│  replace-on-learn: Unschedule(Tag, Group) first   :66        │  time, so the body names the window that ENDED
│  Launch = wavee://open?route=pl&arg={ctxUri}                 │  ALL THREE strings hard-coded AND with no key
│                                                              │  anywhere in assets/loc/en-US.json — the only
│                                                              │  group in this chapter that needs NEW keys
│        or wavee://open?route=home                     :72-74 │
└──────────────────────────────────────────────────────────────┘
```

### W26 — In-app update toast, Available @ engine Toast width (ch. 19 owns the card)

```
┌──────────────────────────────────────────────────────────────┐
│  Wavee Breaker is available                     ← Title      │  Title = Strings.Update.Toast.Available(name)
│  0.2.9.10                                       ← Body       │  Body  = TargetQuad, but ONLY when quad != name
│                                            [ Update now ]    │          (else "") — AppUpdateToasts.cs:69-72
│                                                              │  Severity = Informational,  Sticky = false
│  DurationMs = 5000f    NotificationCenterBridge.cs:236       │  Actions  = [UpdateNow, WhatsNew, Later]     :75
│  DedupeKey = "update"                              :237      │  The toast renders only Actions[0] as its single
│  ActionLabel = AppUpdateToasts.Label(Actions[0])   :238      │  button (:229); the notification-centre row
│              = Loc.Get(update.action.updateNow)              │  renders all three  → ch. 19
└──────────────────────────────────────────────────────────────┘  NO Windows toast accompanies this (W23 note)
```

### W27 — In-app update toast, Downloading (sticky, custom content) @ engine Toast width

```
┌──────────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────────────────────────┐ │  CustomContent = UpdateProgressCard
│ │  Downloading Breaker…                      13/600        │ │  NotificationCenterBridge.cs:240, :246-256
│ │                                                          │ │  BoxEl Direction=1, Gap=8f,
│ │  ├███████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░┤  240 DIP     │ │  Padding=Edges4(16,14,16,14)     :248-250
│ └──────────────────────────────────────────────────────────┘ │  TextEl(_updateProgressTitle) Size=13f Weight=600
│  DurationMs = 0f (sticky — plan.Sticky == true)              │                                   :253
│  AppUpdateToasts.cs:81                                       │  ProgressBar.Create(UpdateProgress, 240f)  :254
│  ActionLabel = null (Actions is empty — :82)                 │  The bar binds a FloatSignal: 20 progress ticks
└──────────────────────────────────────────────────────────────┘  = 20 float writes, ZERO reconciles   (:55-58)
```

### W28 — In-app update toast, Failed (sticky, error) @ engine Toast width

```
┌──────────────────────────────────────────────────────────────┐
│  Close other Wavee windows and try again.       ← Body       │  Title = "" (AppUpdateToasts.cs:99)
│                                                  [ Retry ]   │  Body  = FailureText(failure)  :100, :125-139
│                                                              │          one of eight sentences, all localized
│  Severity = Error, Sticky = true                 :102        │          (update.failure.*), the 8th interpolating
│  Actions = Metered ? [Retry] : [Retry, OpenReleasePage]      │          "0x%08X"                     :137, :151
│           :103-105                                           │  Quiet failures (a scheduled poll that could not
└──────────────────────────────────────────────────────────────┘  reach the feed) plan NOTHING at all      :63
```

---

## 3. Tokens

"Token" here means *the constant the OS is handed*. There are no colour tokens, no radii and no type styles on W1-W25 —
the Shell owns them. The one in-window surface this chapter contributes to (W27's card) is the only row with app tokens.

| element | size | padding/gap | radius | type style | colour token | material/elevation | file:line |
|---|---|---|---|---|---|---|---|
| SMTC thumbnail | OS-decoded from `track.Image.Url`; **no app-side size** | OS | OS | — | — | OS (Win11 flyout / lock screen) | `SystemMediaControlsBridge.cs:111-112`; `SystemMediaControls.cs:520-538` |
| SMTC title / artist / album | 3 strings; artist is `Artists[0].Name` only | OS | OS | OS | OS | OS | `SystemMediaControlsBridge.cs:109-112` |
| SMTC timeline | `TimeSpan` pos + dur; Min/MaxSeek = Start/End | — | — | — | — | — | `:178`; `SystemMediaControls.cs:271-275` |
| SMTC cadence | ≥ 1,000 ms between pushes (whole-second gate) | — | — | — | — | — | `SmtcTimelineCoalescer.cs:60-63` |
| SMTC playback rate | `1.0` (must be > 0) | — | — | — | — | — | `SystemMediaControlsBridge.cs:75` |
| taskbar overlay icon | `pause.ico` — frames 16×16 (1,128 B) + 32×32 (4,264 B), 32 bpp; loaded at `SM_CXICON` via `LR_DEFAULTSIZE` | OS | OS | — | — | OS | `TaskbarBridge.cs:103`; `TaskbarManager.cs:286-288` |
| taskbar overlay description | `"Paused"` (accessibility name) | — | — | — | — | — | `TaskbarBridge.cs:103` |
| taskbar progress | `SetProgress(pos, dur)` as `ulong`; state `Normal` \| `Paused` | — | — | — | shell green / shell yellow | OS | `:119-121`, `:152` |
| taskbar progress cadence | same coalescer ⇒ ≥ 1,000 ms; dropped entirely while `Idle` | — | — | — | — | — | `:143-144` |
| thumb button count | 3 (shell cap 7) | OS | OS | — | — | OS | `:24`; `TaskbarManager.cs:93` |
| thumb glyphs | `prev/play/pause/next.ico`, 16×16 + 32×32, 32 bpp, 5,430 B each | — | — | — | — | — | `TaskbarBridge.cs:60-63` |
| thumb tooltips | `"Previous"` / `"Play"`\|`"Pause"` / `"Next"`, cap 259 chars | — | — | — | — | — | `:160-163`; `TaskbarManager.cs:18` |
| jump list category cap | `CategoryCap = 6` | — | — | — | — | — | `JumpListBridge.cs:37` |
| jump list source scan | `RecentContexts(16)` then history backward | — | — | — | — | — | `:162`, `:177` |
| jump list rebuild floor | `RebuildMinIntervalMs = 60_000` ms | — | — | — | — | — | `:38`, `:115` |
| jump list item icon | `assets/AppIcon/appicon.ico` — 7 frames 16/24/32/48/64/128/256 @ 32 bpp (PNG-encoded), 122,347 B | — | — | — | — | — | `:127`; the file's own ICONDIR; `assets/AppIcon/generate-appicon.ps1:77` (`WaveeAppIcon.cs` is 27 lines — it only resolves the path) |
| jump list task icon | `assets/taskbar/{play,pause}.ico`, else the app icon | — | — | — | — | — | `:132`, `:302-313` |
| toast payload cap | 5,120 bytes XML | — | — | — | — | — | `ToastBuilder.cs:62` |
| toast text elements | ≤ 3 (`<text>`); Wavee uses 1-2 | — | — | — | — | — | `ToastBuilder.cs:63`, `:105-106` |
| toast buttons | ≤ 5; Wavee uses 0 (live) or 2 (drop: Play + Dismiss) | — | — | — | — | — | `ToastBuilder.cs:64`; `ReleaseNotifier.cs:182-183` |
| toast appLogo | `hint-crop='circle'` iff topic == `Followers` | — | circle when cropped | — | — | — | `ToastEscalator.cs:173`; `ToastBuilder.cs:145-149` |
| toast image localization | `%LOCALAPPDATA%\FluentGpu\toastimg\{sha256(url)}{ext}` — **no resize, no re-encode** | — | — | — | — | — | `ToastImageCache.cs:49`, `:57`, `:146-157` |
| toast progress granularity | 5 percentage points, plus an unconditional push at 100 | — | — | — | — | — | `ToastEscalator.cs:107`, `:129` |
| banners per rebuild | `MaxPerRebuild = 3` | — | — | — | — | — | `:31`, `:80` |
| simulated-row cap | `MaxInjected = 10` (oldest dropped) | — | — | — | — | — | `NotificationCenterBridge.cs:299`, `:338-342` |
| schedule minimum lead | drops 1 min · daylist 2 min · simulator 3 min | — | — | — | — | — | `ReleaseNotifier.cs:35`; `DaylistNotifier.cs:23`; `NotificationSimulator.cs:43` |
| in-app download card | Gap `8f`; Padding `Edges4(16,14,16,14)`; title `13f/600`; bar `240f` wide | 16/14 · gap 8 | ch. 19 | 13/600 | ch. 19 | ch. 19 (Toast card) | `NotificationCenterBridge.cs:248-254` |
| in-app toast duration | `5000f` (non-sticky) \| `0f` (sticky) | — | — | — | — | — | `:236`; `AppUpdateToasts.cs:74, 81, 88, 95, 102` |
| in-app toast dedupe key | `"update"` — one card for the whole lifecycle | — | — | — | — | — | `NotificationCenterBridge.cs:237` |
| thumb button dismiss-on-click | `false` (never set) — the thumbnail flyout stays open after a click | — | — | — | — | — | `TaskbarBridge.cs:160-163`; `TaskbarManager.cs:22` |
| jump list row tooltip | the row's `Description` = the RAW `spotify:` uri (play log) / the route name (history) | — | — | — | — | — | `JumpListBridge.cs:169`, `:183`; `JumpList.cs:44-45` |
| jump list visible slots | `BeginList` reports `maxSlots`; **nothing reads it** — 6 is published regardless | — | — | — | — | — | `JumpList.cs:209-211` |
| toast activator identity | CLSID `C8E4A91B-…`, display name the literal `"Wavee"`, icon `appicon.ico` | — | — | — | — | — | `WaveeNativeBoot.cs:23`, `:53` |
| app-update poll cadence | first check **30 s** after launch, then every **24 h** — the clock behind W22/W23/W23b/W26-W28 | — | — | — | — | — | `AppUpdateScheduler.cs:20-21`; started at `PlaybackBridge.cs:1024-1029` |

---

## 4. Colour & material

**Wavee paints no colour on any surface in W1-W25.** Every fill, stroke, blur and acrylic in the SMTC flyout, on the
taskbar button, in the jump list and in an Action Center banner belongs to Explorer and follows the user's Windows
theme and accent, not Wavee's. Consequences that ARE contractual:

1. **The taskbar progress colour is a state, not a colour.** `TaskbarProgressState.Normal` is the shell's green,
   `Paused` its yellow (`TaskbarBridge.cs:119-121`). 0.3 must keep choosing `Paused` for the paused state rather than
   leaving `Normal` and simply freezing the value — the yellow is the only colour cue that distinguishes "paused" from
   "stalled" on a taskbar button, and it costs one enum.
2. **The overlay glyph must read against BOTH themes at 16×16.** The four `.ico`s ship 16 and 32 px frames only
   (§3), and the overlay is composited over the app icon's bottom-right corner by the shell with no scrim of its own.
   There is no app-side tint. If a 0.3 redraw of these glyphs loses the 16 px frame, Windows downscales the 32 px one
   and the paused badge turns to mush at 100% DPI.
3. **Circle-cropping is a topic decision, not a style.** `hint-crop='circle'` is applied iff
   `topic == NotifyTopic.Followers` (`ToastEscalator.cs:173`) — a person gets a circle, an album gets a square. This
   is the one visual rule this chapter owns end to end, and it is the one that silently disappears if the topic
   plumbing is simplified.
4. **A drop toast gets a HERO, every other toast gets an appLogo** (`ReleaseNotifier.cs:190` vs
   `ToastEscalator.cs:173`). The hero is the full-width image at the top of the banner; the appLogo is the small square
   at the left. That difference is the whole reason a release-day banner reads as an event and a feed banner reads as a
   line item.
5. **Sound is a material property here.** `policy.Sound == false` ⇒ `toast.Silent()` (`<audio silent='true'/>`) on
   every raise and every schedule (`ToastEscalator.cs:168`, `:196`; `DaylistNotifier.cs:76`) — **in 0.2.9, except the
   release drop, which never calls it (DATA GAP 4); 0.3 fixes this (Q3, 2026-09-12) so the drop toast is silenced
   too**. Wavee never picks a custom `ToastSound` and never sets a
   `ToastScenario`: no Wavee toast is a Reminder, an Alarm, an IncomingCall or Urgent (`ToastEnums.cs:12-24` — all
   unused). A 0.3 that "improves" a release drop into `scenario='reminder'` makes it stay on screen until dismissed,
   which is a behaviour change, not a style change.
6. **Every image on these surfaces is handed over at whatever size the feed gave it.** SMTC passes a URL and the OS
   decodes (`SystemMediaControls.cs:518-545`); a toast image is downloaded byte-for-byte and re-referenced as
   `file:///…` with **no resize and no re-encode** (`ToastImageCache.cs:65-85`). Both read `Image.Url`, the SMALL
   rendition, never `LargestUrl` (`../Wavee.Core/Domain/Models.cs:28-32` vs `:38-48`) — including for W24's
   **full-width hero**, which is the one place a small rendition can visibly soften. Changing that is a deliberate
   decision with a bandwidth cost, not a cleanup.
7. **The one in-window surface** (W27's progress card) inherits the Toast card's own material from chapter 19; the
   card contributes only a 13/600 line and a 240-DIP `ProgressBar`. It must not grow a background, a border or a
   radius of its own.

---

## 5. Motion

Nothing here animates in Wavee's renderer. What this chapter owns instead is **cadence** — the rate at which a value
moves on a surface someone else paints. Dropping any row turns a live surface into a frozen one, which is exactly the
class of regression the chapter exists to prevent.

| trigger | target | property | from→to | duration | easing | delay/stagger | reduced-motion | file:line |
|---|---|---|---|---|---|---|---|---|
| position tick (engine rate) | SMTC scrub bar | `Position` | previous whole second → new whole second | instant | — | one posted flush per burst; floor 1,000 ms between pushes | n/a (OS chrome) | `SystemMediaControlsBridge.cs:148-152`, `:177-178`; `SmtcTimelineCoalescer.cs:42-66` |
| position tick | taskbar progress fill | `SetProgressValue(pos, dur)` | previous fill → new fill | shell-animated | shell | same coalescer, same 1,000 ms floor | n/a | `TaskbarBridge.cs:140-152` |
| transition into LIVE | SMTC scrub bar | whole timeline | last track's bar → zeroed bar | instant | — | **once** per transition (`_liveTimelineCleared`) | n/a | `SystemMediaControlsBridge.cs:167-175` |
| track boundary | SMTC card | thumbnail + 3 strings | old track → new track | OS cross-fade | OS | fires only on a `Uri` change | n/a | `:98-116` |
| play/pause | SMTC transport | `PlaybackStatus` | `Playing` ⇄ `Paused` | instant | — | fires only on a status change | n/a | `:122-125` |
| play/pause | taskbar button | overlay + progress state | none ⇄ `pause.ico`; `Normal` ⇄ `Paused` | instant | — | two independent edge tests | n/a | `TaskbarBridge.cs:94-124` |
| play/pause | jump list task | title/icon/arg | `Pause` ⇄ `Resume` | one COM transaction | — | **no throttle** on this edge | n/a | `JumpListBridge.cs:115`, `:135-137` |
| track boundary | jump list category | 6 rows | previous set → new set | one COM transaction | — | **≥ 60,000 ms** since the last rebuild | n/a | `:38`, `:115` |
| `AttachHistory` / `AttachLibrary` | jump list | whole list | — | one transaction | — | posted immediately, ignores the throttle | n/a | `:62-77`, `:79-83` |
| Explorer restart | thumbnail toolbar | 3 buttons | absent → present | one add | — | none | n/a | `TaskbarBridge.cs:207-217` |
| download progress tick | Windows toast `<progress>` | `progressValue`, `progressStatus` | previous step → new step | OS | OS | **5 percentage points**; 100 always passes | n/a | `ToastEscalator.cs:107`, `:125-132` |
| download progress tick | in-app card bar | `FloatSignal` width | previous → new | ch. 19's bar | ch. 19 | every tick (no reconcile) | ch. 19 | `NotificationCenterBridge.cs:223`, `:254` |
| feed rebuild | Action Center | ≤ 3 banners + 1 summary | none → banners | OS | OS | one `Consider` per rebuild; oldest→newest | n/a | `ToastEscalator.cs:69-83` |
| daylist window learned | OS schedule | held entry | old window → new window | — | — | idempotent per window end; `MinLead` 2 min | n/a | `DaylistNotifier.cs:46`, `:62`, `:66` |
| saved-set change / launch | OS schedule set | held entries | previous set → re-derived set | — | — | unschedule-then-schedule per uri; `MinLead` 1 min | n/a | `ReleaseNotifier.cs:129-159`, `:170` |
| launch, then daily | the whole update-toast family | `AppUpdateState` | `None` → `Available`/`Downloading`/… | — | — | **30 s** after `Activate`, then a `PeriodicTimer` of **24 h** | n/a | `AppUpdateScheduler.cs:20-21`, `:38`, `:50`; `PlaybackBridge.cs:1024-1029` |
| plan transition | in-app update card | the card itself | absent → shown → auto-dismissed | `5000 ms`, or **sticky** (`0f`) for Downloading/Installing/Failed | ch. 19 | one card, dedupe key `"update"` | ch. 19 | `NotificationCenterBridge.cs:236-237`; `AppUpdateToasts.cs:74, 81, 88, 95, 102` |
| dirty jump list, throttle window elapsed | jump list | 6 rows | stale set → current set | one COM transaction | — | **there is no timer**: `_dirty` is flushed by the NEXT `OnStateChanged` after the window (`:112-117`), so a paused, idle app can hold a stale list indefinitely | n/a | `JumpListBridge.cs:96-97`, `:112-117` |

---

## 6. Interaction

### SMTC (media overlay card, lock screen, hardware keys, headset AVRCP)

| input | route | result | file:line |
|---|---|---|---|
| `MediaButton.Play` | `ButtonDispatcher` → UI thread → `_player.ResumeAsync()` | resume | `SystemMediaControlsBridge.cs:71`, `:192` |
| `MediaButton.Pause` | → `_player.PauseAsync()` | pause | `:193` |
| `MediaButton.Next` | → `_player.NextAsync()` | skip | `:194` |
| `MediaButton.Previous` | → `_player.PreviousAsync()` | back | `:195` |
| `MediaButton.Stop` | → `_player.PauseAsync()` | pause (there is no Stop verb) | `:196` |
| Record / FF / Rewind / Channel / Unknown | ignored | nothing | `:197` |
| every button press | `WaveeLog.Instance.Info("playback", "smtc transport button: " + button)` | **always-on** attribution for a transport change with nothing on screen | `:189` |
| OS scrub-bar drag, on release | `PositionChangeRequested(seconds)` → UI thread → `_bridge.CommitSeek(ms)` | seek, **with the seek latch armed** | `:73`, `:203-213` |

Two rules that look like details and are not: the seek arrives in **seconds** and is rounded and clamped to the known
duration (`:206-208`); and it must go through `CommitSeek`, not `_player.SeekAsync` — without the latch a lock-screen
scrub while paused snaps back to the pre-seek position until the next authoritative tick (`:209-212`).

| app exit | `Dispose` unsubscribes both events and sets `IsEnabled = false` | the card disappears from the flyout — note it deliberately does **not** `ClearDisplay` first (the session dies with the process) | `:215-228` |

**Media keys have no separate registration.** There is no `RegisterHotKey`, no media `WM_APPCOMMAND` handling anywhere
in Wavee or the engine (the engine's `WM_APPCOMMAND` arm maps `BROWSER_BACKWARD`/`FORWARD` only —
`FluentGpu.Windows/Pal/Win32Platform.cs:675`). A keyboard media key reaches Wavee **because** the SMTC session exists.
Disable the session and the keys go dead.

### Taskbar

| input | route | result | file:line |
|---|---|---|---|
| thumb button id 1 | `FluentApp.ThumbButtonClicked` → `_player.PreviousAsync()` | back | `TaskbarBridge.cs:198` |
| thumb button id 2 | `IsPlaying.Peek()` ? `PauseAsync` : `ResumeAsync` | toggle — **reads the live signal, not `_lastPlaying`** | `:199-202` |
| thumb button id 3 | `_player.NextAsync()` | skip | `:203` |
| `TaskbarButtonCreated` (Explorer restart) | `NotifyTaskbarButtonCreated` + re-add | toolbar restored | `:207-217` |
| app exit | `Dispose` clears overlay + progress | no ghost badge on a closed window | `:231-244` |

### Jump list

| input | result | file:line |
|---|---|---|
| Tasks ▸ Pause | `wavee://pause` → `DeepLinkKind.Pause` → `HandleDeepLinkPlayback` | `JumpListBridge.cs:136`; `DeepLinkParse.cs:62-66`; `WaveeShell.cs:1788-1791` |
| Tasks ▸ Resume | `wavee://resume` → `DeepLinkKind.Resume` | `:136`; `DeepLinkParse.cs:57-61` |
| Tasks ▸ Search | `wavee://open?route=search` → `GoDeepLinkOpen("search", "")` | `:138`; `WaveeShell.cs:1802` |
| category row | `wavee://open?route={album\|pl\|artist\|show}:{uri}` or `route=liked` — a FULL route key, checked by `ShellRoutes.IsKnown` before it can become a tab | `:169`, `:288-300`; `WaveeShell.cs:1812-1819` |
| user removes a row | the shell records it; the next rebuild must NOT re-add it or `CommitList` fails — `JumpList` filters by the `GetArguments` string | `JumpList.cs:81-88`, `:303-332` |
| Tasks ▸ Pause while running | the window **is** brought forward: every redirected activation calls `WakeWindow` before the verb is applied, regardless of verb | `Program.cs:485-488`; `WaveeShell.cs:1836-1853` |

### Windows toasts

| input | route | file:line |
|---|---|---|
| click a toast body | `ToastNotifier.Activated` → `ActivationDispatcher` (UI thread) → `OnActivated` → `DeepLinkChannel.Post` + `DeepLink.WakeWindow()` | `WaveeNativeBoot.cs:39-40`, `:55-70` |
| `Play` button on a drop toast | its own `wavee://play?ctx={albumUri}` argument, same route | `ReleaseNotifier.cs:182` |
| `Dismiss` button on a drop toast | `activationType='system' arguments='dismiss'` — closes, no activation | `:183`; `ToastBuilder.cs:221` |
| a cold launch from a toast | `Program.cs` posts the toast-activated command-line args into `DeepLinkChannel` before the bridge exists | `WaveeNativeBoot.cs:12-13`; `Program.cs:483-484` |
| a jump list row / task while Wavee is ALREADY running | the shell launches `exe + args` — a SECOND process — which the single-instance gate redirects: `FluentApp.ActivationRedirected` → `DeepLinkChannel.Post(raw)` + `DeepLink.WakeWindow()` | `Program.cs:485-488` |
| argument scan | `args.Argument` first, then every `args.Arguments` value, then every key; the first that contains `wavee://` or `wavee:` wins | `:55-62`, `:72-77` |

### The gate ladder, in evaluation order

Every live banner passes all five, in this order, and each has its own reason — after a **gate 0 that is not in this
file at all**: `ToastNotifier.Register` must have succeeded at boot, or `Show` throws
(`ToastNotifier.cs:225-226`) straight into `TryRaise`'s catch (`ToastEscalator.cs:181`) and every banner in the app
is silently gone (`WaveeNativeBoot.cs:41-49` swallows the registration failure too).

1. `ToastNotifier.IsSupported` — `!ProcessElevation.IsElevated()` (`ToastEscalator.cs:42`; `ToastNotifier.cs:123`).
   An elevated process cannot raise a toast at all.
2. `watermark > 0` — never toast on first run (`:64`).
3. `policy.WindowsEnabled` — the master dial (`:65`; `NotificationPrefs.cs:62-72`, which reads
   `WaveeSettings.NotifyWindows` at `:66`; a null settings object yields `WindowsEnabled: false, Sound: true`, `:64`).
4. `ts > watermark && n.IsUnread` — it is new and it is unread (`:73`).
5. `policy.RaisesToastNow(level, now)` — the topic is dialled to `Windows` **and** the moment is outside quiet hours
   (`:75`; `NotificationPolicy.cs:124-125`).

Then the two content gates: an `AppUpdateNotification` must have genuinely `UpdateChanged` (`:79`), and `raised` must
still be under `MaxPerRebuild` (`:80`). What Windows itself thinks is a **sixth** gate the app can only observe, never
control: `ToastNotifier.Setting` (`ToastDeliverySetting.DisabledForApplication` / `ForUser` / `ByGroupPolicy`) — read
and surfaced by Settings, not by this chapter (`SettingsPage.Notifications.cs:233-257` → ch. 27). `Show` still
can still report success in that case — `Show` is `showHr >= 0`, and the engine says outright that "a
disabled/suppressed toast can also land here on some builds" (`ToastNotifier.cs:240-243`) — so the escalator counts a
banner nobody saw and the watermark still advances. That is deliberate — the alternative is re-raising forever — but it means "raised" in this
chapter always means *handed over*, never *painted*.

**The SCHEDULED path has its own, shorter ladder**, and it is NOT this one: `ReleaseNotifier.Allowed` =
`policy.WindowsEnabled && Level(ReleaseDrops) == Windows` (`ReleaseNotifier.cs:87-89`), then `IsSupported`
(`:73`, `:109`), then the `MinLead` floor (`:97`, `:156`); quiet hours never gate it, they only SHIFT the delivery
instant (`:198-200`). `DaylistNotifier` folds all of that into one `policy.ScheduleAt(level, due)` call
(`DaylistNotifier.cs:57`) whose null answer means "revoke what we hold" (`:59`), not "skip". There is no watermark,
no `MaxPerRebuild` and no unread test on this path at all.

**The live progress path passes NO gate.** `UpdateProgressToast` runs before the ladder (`ToastEscalator.cs:59-61`)
and is guarded only by `s_lastProgressPushed >= 0` (`:128`) — the latch a fully-gated raise arms (`:165`). A port
that moves the call, or that arms the latch anywhere else, writes progress into a toast that was never allowed.

### Reconcile rules (the ones that revoke)

- **`DaylistNotifier.RequestReconcile` can only ever REVOKE** (`DaylistNotifier.cs:109-117`). Re-scheduling needs a
  window end, which arrives with the next feed resolve — so turning the dial back up re-arms quietly instead of
  guessing at a stale expiry.
- **`ReleaseNotifier.RequestReconcile` re-derives the whole set** from the live saved-set, dropping stale entries
  first so a slipped date is re-scheduled rather than duplicated (`ReleaseNotifier.cs:129-159`).
- **A simulate never reaches a notifier's disallowed branch.** `NotificationSimulator` checks the dial itself and
  returns `RecordedInApp` rather than calling through, because the notifiers' disallowed branch REVOKES a genuinely
  pending real toast (`NotificationSimulator.cs:119-121`; `DaylistNotifier.cs:88-90`).
- **`MarkAllRead` clears simulated rows** — a real update row is state-driven and outlives a read, a simulated one has
  no state behind it and would otherwise sit in the centre forever (`NotificationCenterBridge.cs:111-117`).

---

## 7. Data & readiness in 0.3 terms

| surface | 0.3 read | readiness | what "not ready" paints |
|---|---|---|---|
| SMTC card | `Playback` signals via the drain | `CurrentTrack` null ⇒ `ClearDisplay` + `Closed` | no card at all (W5) — never a half-populated card |
| SMTC timeline | `DurationMs.Peek()` at FLUSH time, not at push time | `dur <= 0` ⇒ `TryTake` returns false | the bar is not pushed (stale bar stays) — **except** the LIVE arm, which zeroes it once |
| taskbar progress | same | `!hasTrack` ⇒ `Idle` ⇒ `ClearProgress`, and ticks are dropped | no bar |
| thumb enablement | `CanSkipPrev`/`CanSkipNext`/`IsPlaying`/`hasTrack` | always defined | disabled buttons, never hidden ones |
| jump list category | `PlayLog.RecentContexts(16)` + `HistoryStore.Entries` + `LibraryStore.{Playlists,Albums,Artists,Shows}` | each store is optional and may attach late (`AttachHistory`/`AttachLibrary`) | fewer rows, or an empty category — never a placeholder row |
| jump list titles | stored title → warm library → now-playing track → `KindLabel` | four-step fallback, total | the kind word ("Album"), never a raw URI |
| toast escalation | the merged `Items` list (ch. 19) + `IAppSettings` | `ToastNotifier.IsSupported`, watermark, policy | silence |
| drop toasts | `LibraryBridge.Saved` + `IPreReleaseService.ResolveAsync` | `link.IsUpcoming && link.ReleaseAt is not null` | nothing scheduled; the next launch reconciles |
| daylist toast | `HomeDaylistHydrator.WindowObserved` | `expiresAtUnixMs > 0` and ≥ 2 min out | nothing scheduled |
| in-app update card | `IAppUpdateService.Changed` → `AppUpdateToasts.Plan` | `Plan` returns null for a non-transition | no card |
| every Windows banner | `ToastNotifier.Default` | `Register` succeeded at boot (`WaveeNativeBoot.cs:43-44`) **and** `ToastNotifier.Setting` is not `DisabledFor*` | silence — no log line, no fallback; only Settings ▸ Notifications shows the blocked banner (ch. 27) |
| jump list, at all | `Environment.ProcessPath` | non-empty | nothing is published; the shell keeps the previous list (`JumpListBridge.cs:125-126`) |

In 0.3 terms: `Playback.Os.*` reads **only** what `Playback.Host.cs` already publishes — it must not acquire its own
entity handles, must not touch `Entities`, and must not query the store. `Notify.Host.cs` reads the notification list
it is handed plus `IAppSettings`; the only handle-shaped dependency in the whole chapter is the jump list's three
optional store references, and those must stay *late-bindable* (attach before or after `Activate`, each triggering one
posted rebuild) because the shell does not exist when `PlaybackBridge.Activate` runs.

### DATA GAPS

1. **A concert / social banner's click almost certainly lands nowhere in 0.2.9 — DECIDED 2026-09-12 (Christos, plan
   §9.6 Q2): fixed, not ported verbatim.** `ToastEscalator` builds
   `"wavee://open?route=" + Uri.EscapeDataString(s.ActionUri)` (`:218-220`), but `ActionUri` is a
   `spotify:concert:<id>` or an `https://concerts.spotify.com/...` (`SpotifyUpdates.cs:47-54`) — **not** a route key.
   `GoDeepLinkOpen` does not compose (the value contains `:`) and `ShellRoutes.IsKnown` rejects it: the concert route
   key is `concert:<id>` (`ConcertRoutes.cs:11`), the album/artist keys are `album:`/`artist:`
   (`Components/RichText.cs:80-100`). The in-app panel row does it correctly — `RichText.RouteForUri(uri)` first
   (`NotificationPanel.cs:332`). **0.3 ports the line *through* `RouteForUri`**, the same way the panel row does, so
   the click resolves. This is a deliberate divergence from 0.2.9, not a silent fix: it needs a GitHub issue and a
   `(#n)` CHANGELOG bullet (neither filed yet), and parity item 29 is restated as "differs from 0.2.9, deliberately"
   rather than left as a straight port, so a side-by-side reviewer does not file it as a regression. Still
   **UNVERIFIED at runtime** (needs a live gander concert row, which `--fake` cannot produce) — verify with the
   simulator (§10 item 29) once ported.
2. **No art decode size is specified anywhere, on any surface.** SMTC hands the OS a URL and the OS decodes it
   (`SystemMediaControls.cs:520-538`); `ToastImageCache` downloads bytes and re-references them with **no resize and
   and no re-encode** (`ToastImageCache.cs:65-85`). Both use `Image.Url` — the *small* rendition — never `LargestUrl`
   (`../Wavee.Core/Domain/Models.cs:28-32` and `:38-48`; the earlier `Models.cs:38-40` cite named no real file).
   This bites hardest on W24's full-width **hero**, which is drawn from the same small URL. Windows' own documented appLogo/hero ceilings are not asserted anywhere in this codebase.
   The pixel dimensions Spotify's `i.scdn.co/image/{id}` actually serves for a given id are **UNVERIFIED**.
3. **The loc keys mostly EXIST ALREADY — nothing calls them.** This chapter first recorded these strings as "no loc
   key, add an `os.*` namespace". That is **wrong**: `assets/loc/en-US.json` already carries
   **`jumplist.*` (11 keys)**, **`taskbar.*` (5 keys)** and **`toast.*` (10 keys)**, and the source generator emits
   them into `Strings` (`Wavee.csproj:199-201`) — but `grep Strings.Jumplist|Strings.Taskbar|Strings.Toast` over the
   whole app returns **zero call sites**. The bridges hard-code the English instead:
   - taskbar `"Previous"` / `"Play"` / `"Pause"` / `"Next"` / `"Paused"` (`TaskbarBridge.cs:103`, `:160-163`,
     `:184-186`) ⇄ `taskbar.previous` / `.play` / `.pause` / `.next` / `.paused` — **all five exist**.
   - jump list `"Pause"` / `"Resume"` / `"Pause playback"` / `"Resume playback"` / `"Search"` / `"Jump back in"` and
     `KindLabel`'s `"Album"` / `"Playlist"` / `"Artist"` / `"Show"` / `"Wavee"` (`JumpListBridge.cs:135-138`, `:146`,
     `:269-277`) ⇄ `jumplist.pause` / `.resume` / `.pausePlayback` / `.resumePlayback` / `.search` / `.jumpBackIn` /
     `.kindAlbum` / `.kindPlaylist` / `.kindArtist` / `.kindShow` / `.kindApp` — **all eleven exist**.
   - toast bodies `"New release — "` / `"New episode — "` (`ToastEscalator.cs:209`) ⇄ `toast.newRelease` /
     `toast.newEpisode`, both already parameterised on `{creator}`.
   - the summary (`:192-193`) ⇄ `toast.moreUpdates`, which is already a **proper ICU plural**, plus `toast.openToSee`.
     The hand-rolled `more == 1 ? … : …` is not a missing-ICU problem, it is an unused-ICU problem.
   - the drop toast (`ReleaseNotifier.cs:180-183`) ⇄ `toast.outNow` / `toast.outNowGeneric` / `toast.play`. Its title
     fallback `"New release"` (`:172`) has **no key**, and `"Dismiss"` is the ENGINE's default argument
     (`ToastBuilder.cs:221`), not a Wavee string.
   - the daylist toast's three sentences (`DaylistNotifier.cs:68-71`) have **no keys anywhere** — the only group that
     genuinely needs new ones.
   Already localized on these surfaces: `detail.likedSongs` (`JumpListBridge.cs:230`, `:240`, `:275`) and the whole
   `update.os.*` / `update.toast.*` / `update.state.*` / `update.failure.*` / `update.action.*` family.
   **0.3's job is to WIRE the existing keys** (and add ~4 new ones for the daylist body and the drop-title fallback) —
   not to invent `os.*`, which would orphan a catalogue section a second time. Today a Spanish user's jump list still
   says "Jump back in" even though the key to fix it has been sitting in the catalogue.
4. **`ReleaseNotifier` never calls `Silent()` in 0.2.9 — DECIDED 2026-09-12 (Christos, plan §9.6 Q3): fixed.**
   `DaylistNotifier` honours `policy.Sound` (`:76`) and `ToastEscalator` honours it on both the raise and the summary
   (`:168`, `:196`), but the drop toast is built without the check (`ReleaseNotifier.cs:178-185`). With "Play a
   sound" off, a release drop still chimes. **0.3's `ReleaseNotifier` gains the same `policy.Sound` check** the other
   two schedulers already have. Same treatment as DATA GAP 1 (Q2): this is a deliberate divergence from 0.2.9, so it
   needs a GitHub issue and a `(#n)` CHANGELOG bullet (neither filed yet), and parity item 31 is restated as "differs
   from 0.2.9, deliberately" so a side-by-side reviewer does not file it as a regression.
5. **`ToastImageCache.Default` is rooted at `%LOCALAPPDATA%\FluentGpu\toastimg`, not `Wavee`**
   (`ToastImageCache.cs:49`) — the engine's default folder, shared with any other FluentGpu app, and **never cleared
   on sign-out** (Wavee never calls `Clear()`; `:114-123` has no caller in this repo). Size is unbounded.
6. **Toast tag length is unasserted.** `TagFor(n) = "live:" + n.Id` where `Id` is the entity URI for a new-release row
   (`NotificationModels.cs:42-45` — `Id` is the record's first positional member; `SpotifyWhatsNewService.cs:175`) — 41 characters for a typical album. `put_Tag` throws
   on an over-long tag and `TryRaise`'s catch turns that into a **silently dropped banner** (`ToastEscalator.cs:181`;
   `ToastNotifier.cs:546`). The platform cap (64 chars on 1709+, 16 before) is not enforced or truncated anywhere.
   No observed Wavee id exceeds it; a longer feed id would.
7. **The scheduled-toast count cap is unasserted.** `ReleaseNotifier` schedules one per pre-saved album with no upper
   bound on the saved set (`:146-158`). Windows' documented per-app limit is not encoded anywhere and is
   **UNVERIFIED** in this codebase.
8. **Nothing in this chapter is wired to sign-out — now VERIFIED, not suspected.** `JumpList.Clear(aumid)` exists
   (`JumpList.cs:169-186`) and has **no caller anywhere in `src/apps`**; a signed-out Wavee keeps showing the last
   user's six recents in the taskbar. `ReleaseNotifier.UnscheduleAll` (`:116-127`) has exactly one caller and it is
   `ReleaseNotifier.RequestReconcile` itself (`:111`) — i.e. the DIAL going off, never a sign-out.
   `DaylistNotifier.Unschedule` (`:120-128`) is likewise only reached from `Note`/`RequestReconcile`. The two
   `RequestReconcile` entry points in the app are `SettingsPage.Notifications.cs:270-271` (a dial changed) and
   `ReleaseNotifier.Attach` (`:61`, launch). So after sign-out the previous account's scheduled drop toasts stay
   armed in the OS and fire with the new user signed in. 0.3 must either wire a teardown (~15 lines: `JumpList.Clear`
   + `UnscheduleAll` + `DaylistNotifier.Unschedule` + `ToastImageCache.Default.Clear()`) or state that it does not.
9. **`ToastNotifier.Show`'s doc comment is stale** — it says tag/group are "reserved" (`ToastNotifier.cs:214-217`)
   while the body applies them via `IToastNotification2` (`:238`, `:537-549`). The live progress path depends on the
   body, not the comment. Port the code; do not port the comment.

10. **A pre-save revoked while Wavee was CLOSED is never unscheduled.** `ReleaseNotifier.Scheduled` is a `static
    HashSet` (`:38`) that is **not persisted**, while the OS entry it describes outlives the process. At launch the
    set is empty, so `ReconcileAsync`'s stale sweep — which iterates `Scheduled` and drops what the saved-set no
    longer contains (`:136-144`) — finds nothing to drop. Entries still in the saved set are harmlessly re-scheduled
    under their stable tag (`:170`), but an album the user un-pre-saved on their phone keeps its toast forever.
    Fixing it needs the held tags on disk (a settings key) or a `RemoveGroup("wavee.release-drops")` before the
    re-derive. **UNVERIFIED** at runtime (needs a real prerelease + two sessions).
11. **The concert/social banner keeps the feed's leading emoji; Home strips it.** `SpotifyUpdates.CleanTitle`
    (`../Wavee.Core/Notifications/SpotifyUpdates.cs:74-96`, tested at `HomeTimelineMergeTests.cs:239-253`) exists
    precisely to drop a leading `⏰`/`🎵` from a server-localized title, and its ONLY caller is
    `HomeModules.Timeline.cs:110`. `ToastEscalator` passes `s.Title` raw (`:216`), so the same event reads
    "🎵 New Keenan Te show just announced near you" as a Windows banner and "New Keenan Te show…" on Home. Decide in
    0.3 which is right and apply it in one place; do not port the divergence by accident.
12. **A jump list row's hover tooltip is a raw `spotify:` URI.** The bridge passes `row.Uri` / the route name into
    `JumpListItem`'s fifth positional parameter, which is `Description` — the shell's tooltip
    (`JumpListBridge.cs:169`, `:183`; `JumpList.cs:44-45`). Non-negotiable 8's "never a raw URI" is true of the
    LABEL and false of the tooltip. If the Description was meant to be an identity field, it is the wrong one —
    identity for the removed-items filter is `Arguments`, not `Description` (`JumpList.cs:303-332`).
13. **The 6-row cap is the app's, not the shell's.** `BeginList` hands back `maxSlots` (the user's "number of recent
    items to display in Jump Lists" setting) and nothing reads it (`JumpList.cs:209-211`), so on a machine set to 4
    the bottom two rows are silently dropped by Explorer. Harmless today; it becomes a bug the moment 0.3 uses the
    published count for anything (a "showing 6 of N" affordance, a telemetry line).
---

## 8. Pure rules to port verbatim

| name | file | decides | tests under `src/apps/Wavee.Tests` | 0.3 destination |
|---|---|---|---|---|
| `SmtcTimelineCoalescer` | `App/SmtcTimelineCoalescer.cs` (67) | newest-wins latch; one flush per burst; whole-second dedupe; clamp to duration; `dur <= 0` drop; latch always cleared | `SmtcTimelineCoalescerTests.cs` — 9 facts (`:20` first push schedules, `:30` burst = one flush, `:42` newest wins, `:54` re-arm, `:67` same-second dedupe, `:84` second 0 not deduped, `:95` clamp, `:109` unknown duration, `:119` bail-out clears) | `Playback/Playback.cs` (CORE) — **verbatim**, `System`-only, already source-included by the test project (`Wavee.Tests.csproj:315-317`; the earlier `:25` cite pointed at nothing) |
| `NotifyLevel` / `NotifyTopic` | `../Wavee.Core/Notifications/NotificationPolicy.cs:8-45` | the persisted ladder and the eight topics; **append-only** | `NotificationPolicyTests.cs:50` (ceiling clamp), `:60` (defaults) | `Platform/Notify.cs` (CORE) — verbatim |
| `QuietHours` | same file `:50-81` | half-open `[From, To)` local window, wraps midnight; `From == To` ⇒ no window; corrupt hours clamped; `NextAudible` idempotent | `NotificationPolicyTests.cs:83, 95, 106, 116, 122, 178` | `Platform/Notify.cs` (CORE) — verbatim |
| `NotificationPolicy` | same file `:85-135` | `DefaultFor`, `CeilingFor`, `IsScheduled`, `Clamp`, `ShowsInApp`, `RaisesToastNow`, `ScheduleAt` (shift, never suppress) | `NotificationPolicyTests.cs:19, 30, 40, 71, 132, 141, 156, 169` | `Platform/Notify.cs` (CORE) — verbatim |
| `NotificationPrefs` | `App/NotificationPrefs.cs` (96) | settings → level/policy; `AllTopics` declaration order IS the settings-page order (`:21-31`); `TopicOf` (`:89-95`, the fine answer the display category cannot give); `ShowsCategory` (`:77-85`, a category survives while ANY of its topics is above Off) | no direct file; exercised through `NotificationPolicyTests` and `SimulatedNotificationsTests` — **0.3 should add one** | `Platform/Notify.cs` (CORE) — **but it is NOT pure**: `Epoch` is a `FluentGpu.Signals.Signal<int>` (`:15-17`) and every setter `Bump()`s it, so this file drags the engine into "CORE". Either the signal moves to the host half or `Notify.cs` is accepted as engine-referencing |
| `AppUpdateToasts.Plan` | `App/AppUpdateToasts.cs:55-110` | `(previous, next)` → toast or nothing; only a genuine state move (or a changed failure reason) plans; quiet failures plan nothing; progress ticks never plan a second card | `AppUpdateToastsTests.cs` — **12** `Plan` facts (`:17`, `:24`, `:37`, `:43`, `:54`, `:72`, `:82`, `:92`, `:103`, `:118`, `:127`, `:139`); 15 `[Fact]`/`[Theory]` in the file counting the three below | `Platform/Notify.cs` (CORE) — verbatim |
| `AppUpdateToasts.Label` / `FailureText` / `ReleaseName` | `:113-152` | one label per action; one sentence per failure kind; codename → semver → quad, never empty | `AppUpdateToastsTests.cs:151, 165, 175` | `Platform/Notify.cs` (CORE) — verbatim |
| `SimulatedNotifications` | `../Wavee.Core/Notifications/SimulatedNotifications.cs` | the synthetic rows the Send-event affordance injects, and `NextTimestamp`'s watermark arithmetic | `SimulatedNotificationsTests.cs` (192) | `Platform/Notify.cs` (CORE) → ch. 27 |
| `SpotifyUpdates` | `../Wavee.Core/Notifications/SpotifyUpdates.cs` | `KindOf`/`IsConcert` (wire type, else a concert action target — `:29-54`), `ActName` (`:58-65`), `CleanTitle` (`:74-96`) | `HomeTimelineMergeTests.cs:239-253` (CleanTitle) | `Platform/Notify.cs` (CORE) — shared with ch. 19 and ch. 10; the toast's topic AND its title both come from here (DATA GAP 11) |
| `RecentSurfaceRoute.TryClassify` | `Backend/Persistence/RecentSurfaceRoute.cs:27-46` (album/pl/artist/show only — never `liked`) | route name → (uri, kind) for the jump list's history half | via `Backend` tests | `Entities/User.cs` or `Shell/Shell.cs` (CORE) — one owner, shared with recents (ch. 16) |
| `JumpListBridge.FromTrack` | `App/JumpListBridge.cs:205-235` | best-effort context display name from the track that just started — **also called by `PlaybackBridge:1443`** to stamp the play log's stored title | none — **0.3 must add one** (already `static` and engine-free apart from `Loc`) | `Playback/Playback.cs` (CORE) |
| `JumpListBridge.TryRoute` / `KindLabel` / `ToPlayKind` | `:269-300` | kind → route prefix; the four fallback words; the transport→routing enum bridge | none — **0.3 must add one** | `Playback/Playback.cs` (CORE) |
| `ToastEscalator`'s watermark arithmetic | `App/ToastEscalator.cs:39-96` | first-run silence, advance-past-considered, the `long.MaxValue` sentinel fold, `MaxPerRebuild` truncation keeping the FRESHEST | none — **the whole escalation policy is untested** (see §9) | extract `Notify.EscalationPlan` (CORE) in `Platform/Notify.cs`; the WinRT half stays in `Notify.Host.cs` |

`ToastCoalescingTests.cs` (124) covers the **in-app** `Toast` dedupe, not this chapter's — do not mistake it for
escalation coverage. `NotificationAggregationTests.cs` (104) covers `NotificationMerge`, which is chapter 19's.

---

## 9. Re-author notes

### Must not be simplified

- **Two coalescers, not one shared instance.** SMTC and the taskbar each own a `SmtcTimelineCoalescer` field
  (`SystemMediaControlsBridge.cs:47`, `TaskbarBridge.cs:39`). They have different bail-out conditions (the taskbar
  drops every tick while `Idle`, `:143`; SMTC has the LIVE arm, `:167`), so one shared latch would let one bridge
  consume the other's scheduled flush and the other surface would freeze. Each also caches its own flush delegate
  (`SystemMediaControlsBridge.cs:48`, `TaskbarBridge.cs:40`) precisely so the per-tick path allocates nothing.
- **`_lastTrackUri = "\0"`** (`JumpListBridge.cs:46`). The sentinel guarantees the first `OnStateChanged` reads as a
  boundary even when nothing is playing (`uri == ""`). An initialiser of `""` or `null` makes the very first rebuild
  never happen.
- **`_havePlayState`** (`:48`, `:105`). Without it the first tick's `playing == false` matches the default `false`
  and the play/pause edge is missed.
- **`_hasLast` in the coalescer** (`SmtcTimelineCoalescer.cs:32`, `:61`). `default(struct)` must not dedupe against
  second 0, or a track that starts at 0:00 never pushes its first timeline.
- **The two-phase watermark scan.** The newest timestamp is found in a FIRST pass over everything
  (`ToastEscalator.cs:50-54`) and written unconditionally at the end (`:86`), independently of what was raised. Fusing
  the two loops is the bug where a topic the user silenced comes back as a backlog the moment they re-enable it.
- **`TimestampOf` is keyed on the SENTINEL, not on the type** (`:95-96`). Folding every app-update row to "now" made a
  *simulated* update (which carries a real timestamp) beat the watermark on every unrelated rebuild — a banner storm.
  The comment at `:92-94` exists because this was actually shipped once.
- **`s_lastUpdateRaised` and `s_lastProgressPushed` are `static` and process-lifetime** (`:100`, `:102`). That is the
  lifetime of the condition they describe. Making them instance fields on a bridge that can be rebuilt re-raises the
  banner on every rebuild.
- **The progress push happens BEFORE the escalation loop** (`:59-61`). After it, a 5%-per-tick stream becomes twenty
  identical banners.
- **`UpdateProgressToast` is a no-op until a toast has actually been raised** (`s_lastProgressPushed < 0`, `:128`),
  and the first values are pushed immediately after a successful `Show` because `Show` carries no initial data
  (`:177-178`). Both halves are load-bearing.
- **`UpdateChanged` resets the progress latch on the way OUT of Downloading** (`ToastEscalator.cs:117`:
  `if (state != Downloading) s_lastProgressPushed = -1`). Without it, `UpdateProgressToast` keeps calling
  `ToastNotifier.Update` against a tag whose toast is gone — harmless-looking (`NotificationNotFound` is swallowed),
  but it also means a LATER download would resume pushing at the old percentage instead of being armed by its own
  raise. The latch is armed in exactly one place, inside a gated raise (`:165`), and that is the whole design.
- **The taskbar's `Activate` seeds AFTER `_active = true`** (`TaskbarBridge.cs:69-71`). `OnStateChanged` and
  `OnPositionChanged` both return early on `!_active`, so seeding before the flag is a silent no-op and the button
  stays blank until the next push. The SMTC bridge has the same shape (`SystemMediaControlsBridge.cs:77-80`).
- **`JumpListBridge.Rebuild` re-reads the LIVE play state** (`:131`) rather than trusting `_lastPlaying`, for the
  same reason the thumb click does: the rebuild is posted, so the cached value can be a frame stale by the time the
  COM transaction runs — and this is the one verb a user compares against the app in front of them.
- **`DaylistNotifier.SimulateSchedule` checks the dial itself** (`:96-98`) rather than letting `Note` do it, because
  `Note`'s disallowed branch calls `Unschedule` — a simulate would destroy a genuinely pending real rollover toast
  (`:88-90`). Same reasoning in `NotificationSimulator.SendScheduled` (`:119-121`).
- **`SimulateSchedule` nudges the window by 1 ms when it equals the held one** (`DaylistNotifier.cs:101`), so a repeat
  press of the Send-event button is not silently a no-op.
- **`_liveTimelineCleared = false` on the first non-live flush** (`SystemMediaControlsBridge.cs:175`). Without it the
  next real track after a live stream never gets a timeline.
- **The jump list's AUMID argument** (`:145`). It looks like defensive plumbing; it is the difference between a list
  that appears and one that silently does not.
- **Thumb-click reads `_bridge.IsPlaying.Peek()`, not `_lastPlaying`** (`TaskbarBridge.cs:200`). The cached field is
  the last *pushed* state, which can lag the actual transport by a frame.
- **`ApplyThumbs`'s glyph-less retry** (`:180-189`). A dev tree without the content copy, or an add-too-early, must
  still produce a working toolbar.

### Traps

- The `EntityKind` alias at `JumpListBridge.cs:11` disambiguates two same-named enums (`Wavee.Core.EntityKind` is the
  routing vocabulary, `Wavee.Backend.Metadata.EntityKind` is the persisted transport one). A 0.3 that merges the two
  namespaces must decide which one `ToPlayKind` takes; silently picking the wrong one compiles and routes nothing.
- `JumpListBridge` walks `HistoryStore.Entries` **backward** (`:177`) because the list is oldest-first. Forward is a
  jump list of the user's oldest six visits.
- `ToastEscalator`'s loop is `for (int i = items.Count - 1; i >= 0; i--)` (`:69`) over a **newest-first** list, i.e.
  oldest→newest. Reversing it truncates the burst at the wrong end.
- `NotificationPrefs.AllTopics` declaration order **is** the settings-page order (`:172-184`). A new topic added to the
  enum without a row here is invisible in Settings.
- `ToastImageCache.Localize` is **synchronous with a blocking `GetAwaiter().GetResult()`** on the cold path
  (`ToastImageCache.cs:76`). `TryRaise` calls it on the UI thread (`ToastEscalator.cs:173`). A slow CDN stalls the
  frame. There is an async overload (`:88`) with no caller. 0.3 should use it, but that is a **behaviour change**
  (the raise would become async) — decide deliberately, do not drift into it.
  **Qualification the first draft missed:** `ShouldLocalize` returns false for a **packaged** process
  (`ToastImageCache.cs:133`), and the shipped Wavee is MSIX. So the stall is real only in an unpackaged/dev run,
  and conversely the *shipped* build hands the platform a bare `https://` URL and lets the Shell fetch it — which
  is also why a packaged-vs-unpackaged difference in whether art appears is expected, not a bug.
- The two `EntityKind`s and the two "route" vocabularies are not the only near-collision: `JumpListBridge` dedupes
  the play-log half by the composed ROUTE (`:168`) and the history half by the route NAME (`:181`), which happen to
  be the same string ("album:<uri>") — that is why one `seen` set works. Change either composition and the two
  halves stop deduping against each other, and a surface both played and visited appears twice.
- `WaveeNativeBoot.Install` re-assigns `ActivationDispatcher` even on the second call (`:33-37`) so a re-entry updates
  the dispatcher without re-registering. Collapsing that into a plain `if (installed) return;` leaves a stale
  dispatcher pointing at a dead post delegate.

### Where design intent and code disagree (code wins; drift noted)

- The `TaskbarBridge` header says progress is coalesced "so a backlog of position ticks pays one
  `ITaskbarList3.SetProgressValue` (~1 Hz)" (`:16-18`) — accurate. The `SystemMediaControlsBridge` header says the
  timeline is "throttled to whole-second changes" (`:138-139`) — accurate, but its surrounding prose describes the
  dedupe as living in `_lastTimelineSec`, a field that no longer exists (`:45-46` acknowledges the move). Port the code.
- `ToastNotifier.Show`'s `tag`/`group` doc says "reserved" (`ToastNotifier.cs:214-217`); the body applies them
  (`:238`). See DATA GAP 9.
- `JumpListBridge`'s class header claims titles come from "the play log's stored context name, then the warm
  `LibraryStore`, then the now-playing track" (`:25-27`) — correct, and there is a fourth step it does not mention
  (`KindLabel`, `:200`).

### Where the 0.3 plan is wrong or too thin for this surface

- **§2's budget was ~47% short when this chapter was first written.** `Playback.Os.cs` was given 1,100 lines (`:68`)
  for what is ~2,064 lines of real code across eleven files plus a Core policy file. Even the narrow reading (SMTC +
  taskbar + jump list + app icon only) is **818**, and that reading left the entire toast stack homeless — there was
  no file anywhere in §2 that could hold `ToastEscalator`, `ReleaseNotifier`, `DaylistNotifier`, `NotificationPrefs`,
  `AppUpdateToasts` or the AUMID/activator registration. **Settled**: `Playback.Os.cs` is now budgeted **840** (this
  chapter's own re-budget, §9 below) for the narrow SMTC/taskbar/jump-list/app-icon reading, owner H, Wave 3; the
  toast stack moved out to `Platform/Notify.cs` + `Notify.Host.cs`, owner I, Wave 4 (A9).
- **§2 names two things that do not exist.** "media keys" is not separate code — media keys arrive through
  `SMTC.ButtonPressed` (`SystemMediaControlsBridge.cs:72`, `:186-189`) and there is no `RegisterHotKey` or media
  `WM_APPCOMMAND` handling in Wavee or the engine. "power" has **no 0.2.9 code at all**. Either they are new work
  (then they need a spec and a budget of their own) or they are aspiration in a file header (then they should be
  struck, because a 1,100-line budget that silently includes two unwritten features is how the real 2,040 lines get
  squeezed).
- **§5's wave ordering makes owner H's job impossible.** Wave 3 (`:735`) hands H `Playback.Os.cs`, but
  `JumpListBridge` compiles against `PlayLogStore`, `HistoryStore`, `LibraryStore` and `RecentSurfaceRoute` — the
  first lands in Wave 1 (Entities/Store), the second and third in Wave 4 (Shell/Sidebar). A Wave 3 owner can write the
  SMTC and taskbar halves and *cannot* write the jump list half. Either the jump list moves to Wave 4/5, or Wave 3
  ships it behind the same optional late-binding seam it already has (`AttachHistory`/`AttachLibrary`, which is
  exactly why that seam exists) with the category empty until the stores attach.
- **§4's "no design contract" is literally true for this wave.** There is no wireframe, no token table and no parity
  list anywhere in the plan for anything outside the window. That is what this chapter is.
- **§6's test-migration table has no row for these files, and its second bucket actively endangers them.**
  `SmtcTimelineCoalescerTests`, `AppUpdateToastsTests` and `NotificationPolicyTests` are exactly the "pure-decision
  tests of files that survive as core sections" category (`wavee-0.3-implementation.md:773`) and must be named in it.
  The very next row — *"Tests of interfaces, Switchable/Null/fakes, **bridges**, wiring → Delete (the subject is
  gone)"* (`:775`) — is how a file literally named `SmtcTimelineCoalescer**Bridge**`-adjacent gets swept: the
  coalescer is the pure half of a bridge, it is `System`-only, and its nine facts are the only thing standing between
  0.3 and a frozen scrub bar. Name it explicitly in the PORT row.
- **§2 has no file that can hold the toast stack, and adding two changes the count.** `Platform/` is listed as
  **7 files, ~8,600 lines** — `Platform.cs Platform.Host.cs Modules.cs Modules.UI.cs Modules.Host.cs Design.cs
  Controls.cs` (`wavee-0.3-implementation.md:78-79`). `Notify.cs` + `Notify.Host.cs` make it **9 / ~10,300**
  (~1,000 CORE with ch. 19’s fold + ~715 SHELL, both owner I in Wave 4). The words "notification" and "toast" do not
  appear anywhere in the plan (verified by grep), so this is not an oversight in one row — the whole feature is
  absent from the tree.

### Who should own these in 0.3

**Split the chapter across two owners along the HWND line, and say so in §2.**

| file | contents | owner | why |
|---|---|---|---|
| `Playback/Playback.cs` (+67) | `SmtcTimelineCoalescer` | **H** (Wave 3) | a `System`-only struct that both bridges latch into; it belongs with the other pure playback rules, and `Wavee.Tests` already source-includes it |
| `Playback/Playback.Os.cs` (~830) | SMTC bridge, taskbar bridge, jump list bridge, app icon resolver | **H** (Wave 3) | every one of these is keyed on `FluentApp.WindowHandle` **and** on the unified playback state. They are the playback surface, mirrored outward. Splitting SMTC from the taskbar across owners is how the two coalescer call sites drift. |
| `Platform/Notify.cs` (~445 of ~1,000, CORE) | `NotifyLevel`/`NotifyTopic`/`QuietHours`/`NotificationPolicy` (moved from `Wavee.Core`), `NotificationPrefs`, `AppUpdateToasts`, the extracted `Notify.EscalationPlan`; ch. 19 folds the feed’s merge/read-state into the SAME file | **I** (Wave 4) | nothing here touches playback or a window handle — but it IS the model the in-app notification panel renders, and that panel is Wave-4 shell chrome (ch. 19). A Wave-6 home leaves owner I’s panel with no feed for two waves. The **`NotificationPrefs.Epoch` engine `Signal` (§8)** either moves to the host half or `Notify.cs` is accepted as engine-referencing |
| `Platform/Notify.Host.cs` (~715, SHELL) | `ToastEscalator`'s WinRT half, `DaylistNotifier`, `ReleaseNotifier`, the AUMID + activator registration and the activation→deep-link hop | **I** (Wave 4) | one owner for both halves of one stack. `LibraryBridge`, `IPreReleaseService`, the home daylist hydrator and `IAppSettings` all arrive through late binds of the same shape the jump list already uses, so the schedulers ship in Wave 4 with their feeds attaching later |
| `Screens/Diagnostics.Probe.cs` (+195) | `NotificationSimulator` | **S** (Wave 6) → ch. 27 | it is the Send-event affordance's engine, and it injects into a stack that exists by then |
| `Shell/Shell.UI.cs` (+135) | the in-app update card arms of the notification centre | **I** (Wave 4) → ch. 19 | it is a `Toast.Show` call and an `Element`; it is in-window, and it belongs beside the panel and its rows |

**Do NOT give the toast stack to H.** H's Wave-3 gate is "10 s of a track through the real pump"; a toast stack that
depends on the library, the home feed and the settings store cannot be exercised by that gate at all, and would sit
un-run for three waves. **Do NOT give SMTC/taskbar/jump list to I** either: they must be alive from the moment
playback is, and their only inputs are `Playback`'s own signals. That is the whole split, and the HWND line is where
it falls: what a window handle drives is H’s, in Wave 3; what a notification drives is I’s, in Wave 4.

**Why the toast half is owner I in Wave 4 rather than owner S in Wave 6** (arbitration 2026-09-12). The first draft of
this table sent `Notify.*` to Wave 6 on the grounds that it is settings arithmetic and a decision table. It is also
the **model behind the in-app notification panel**, which is Wave-4 shell chrome (`Features/Shell/NotificationPanel.cs`,
640 lines, ch. 19), and the one `Consider(settings, items)` call that raises a banner runs inside the same rebuild that
produces the panel’s rows and its unread count (`NotificationCenterBridge.cs:201-206`). Split across two waves, either
the panel ships with no feed or the feed is written twice. One stack, one owner, Wave 4.

### What breaks if the cadence rules are dropped

Each of these is a shipped bug that the current code fixed; each returns the moment its rule is dropped.

| rule dropped | what the user sees |
|---|---|
| the coalescer (either bridge) | a frame that drains a backlog of position ticks pays a **cross-process COM RPC per tick, synchronously, on the UI thread** (`SmtcTimelineCoalescer.cs:9-13`). At ~1 ms each this is a visible stall on every catch-up drain — the `frame.slow` signature. |
| `TryTake` always clearing `_flushQueued` | one bail-out wedges the latch armed forever: **the scrub bar and the taskbar bar stop moving for the rest of the process**, with no error anywhere. |
| the whole-second gate | ~20-60 `UpdateTimeline`/`SetProgressValue` RPCs per second instead of 1. |
| the `Idle` guard on taskbar ticks (`:143`) | a stale duration repaints a progress bar on a button that should have none. |
| the LIVE zero-once rule | the **previous track's 3:47** stays on the Win11 flyout with a frozen thumb for the whole six-hour stream. |
| `_liveTimelineCleared` reset | the first real track after a live stream gets **no timeline at all**. |
| the jump list 60 s floor | every skip is a Begin/Append/Commit COM transaction — a skip-storm hammers `ICustomDestinationList` on the UI thread. |
| the play/pause throttle EXEMPTION | the Tasks section says "Pause" while the app is paused, for up to a minute. |
| the toast watermark | **every rebuild and every relaunch re-toasts the entire feed.** |
| `MaxPerRebuild` | a feed that returns a hundred new rows produces a hundred banners. |
| `UpdateChanged` | the app-update banner re-raises on **every single rebuild**. |
| `ProgressStepPercent` | a 1%-per-tick stream of live-toast updates that Windows throttles anyway and the user cannot see. |
| the `MinLead` floors | Windows **accepts** the near-immediate schedule and then silently never paints it — the feature looks broken and nothing is logged. |
| the edge-dedupes | a string allocation and a WinRT/COM call on every no-op state tick, i.e. on every position push that also carries state. |

### Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's files | **~2,064** (885 playback-mirror + 1,138 toast + ~159 centre arms + ~22 wiring − ~140 double-counted headers/usings) |
| plan target (`Playback.Os.cs`), settled | **840** (§2, owner H, Wave 3) — originally 1,100 when this chapter was first written, and that figure also claimed media keys + power |
| honest estimate, split as proposed | `Playback.cs` +67 · `Playback.Os.cs` **840** · `Notify.cs` **445** · `Notify.Host.cs` **715** · `Diagnostics.Probe.cs` +195 · `Shell.UI.cs` +159 = **~2,421** |

Two arithmetic notes the first draft got wrong. (a) `Playback.Os.cs`'s "narrow reading" is **818** raw lines
(229 + 248 + 314 + 27), not 830; 840 is that plus the `Loc.Get` wiring below. (b) `Notify.cs` cannot be 390: the
three files it absorbs are already 385 (136 + 96 + 153), so the new `EscalationPlan` has to be added on top, not
absorbed — **445**. §1’s tree now carries 445 too, and names the ~555 ch. 19 folds into the same CORE file.

The estimate is *above* 0.2.9 because three things must be **added**, not removed: `Loc.Get` call sites for the
string groups whose keys already exist plus ~4 genuinely new keys (DATA GAP 3, ~40 lines plus catalogue entries), a
test-visible `Notify.EscalationPlan` extraction so the watermark/cap/sentinel rules stop being untestable (~60 lines),
and the sign-out teardown nothing in this chapter has (DATA GAP 8, ~15 lines). Nothing here is a candidate for
deletion: there is no legacy path, no dead branch and no duplicated rule in the ~2,064 lines.

---

## 10. Parity checklist

Every item is binary and is verified against the kept 0.2.9 build
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe` launched as
`Wavee.exe --fake` (no login, no network). **Do not launch it from an agent shell while the owner's own instance is
running** — these surfaces are process-wide and two instances fight over the same AUMID and the same taskbar button.
Items marked **[SIM]** need Settings ▸ Notifications ▸ *Send a test event*; items marked **[LIVE]** cannot be produced
in `--fake` and must be deferred to a live session (say so in §11 rather than guessing).

1. Start `--fake`, press Play. Open the Windows media flyout. A Wavee card is present with **three** lines: title, a
   **single** artist name, album name. 0.3 shows the same three, and the artist line is the first artist only — never
   a comma-joined list.
2. The card's artwork is present and matches the player bar's artwork for the same track.
3. Press pause in the app. The card's transport flips to play within one frame and **nothing else on the card changes**
   (same art, same three lines).
4. Skip to the first track of the fake queue. The card's previous button is greyed; next stays live. Skip forward
   once: previous becomes live.
5. Let a track play 30 s. The card's scrub bar advances in **whole-second steps** and its right-hand total matches the
   track duration. It must not stutter or jump backwards.
6. Drag the card's scrub bar to ~75% and release. Playback moves there and **stays** there (it must not snap back to
   the pre-seek position on the next tick). Repeat while paused — same result.
7. Press the keyboard's Play/Pause media key with Wavee unfocused. Playback toggles. Check
   `%LOCALAPPDATA%\Wavee\logs` for a line `smtc transport button: Play` (or `Pause`) — one line per press.
8. Press the Next media key twice quickly. Two log lines, two skips, no duplicated skip.
9. Stop playback so no track is loaded (or start `--fake` and do not press play). The Wavee card is **absent** from
   the flyout — not present-and-empty.
10. **[LIVE]** Play a module live stream. The card's scrub bar shows no progress and no total; the previous track's
    duration is gone.
11. While playing, look at the taskbar button: a **determinate green fill** advances, and there is **no glyph overlay**
    in the corner.
12. Pause: the fill turns **yellow** and a **pause glyph** appears in the button's corner. The overlay's accessible
    name is "Paused".
13. Stop/clear the track: the fill is gone and the overlay is gone.
14. Hover the taskbar button to raise the thumbnail. Three buttons are present under it, in the order previous /
    play-pause / next, with tooltips "Previous", "Pause"/"Play", "Next".
15. On the first track of the queue, the thumbnail's previous button is visibly **disabled** (greyed), not hidden.
16. With no track loaded, the thumbnail's middle button is **disabled**.
17. Click each of the three thumbnail buttons: back, toggle, skip. The middle button toggles correctly even when
    clicked twice fast (it reads the live state, not a cached one).
18. Kill and restart `explorer.exe`. Hover the taskbar button again: the three thumbnail buttons are **still there**.
19. Right-click the taskbar button. A **Tasks** section shows two entries: "Pause" (or "Resume") with a transport
    glyph, and "Search" with the Wavee icon.
20. Toggle play/pause in the app, then right-click the taskbar button again **immediately**. The first task's label and
    glyph have already flipped (no minute-long lag).
21. Navigate to three or four albums/playlists/artists in `--fake`, then right-click the taskbar button. A
    **"Jump back in"** category lists at most **6** rows, newest first, each labelled with the entity's real name (never
    a raw `spotify:` URI, never a bare "Album"/"Playlist" unless the name is genuinely unknown).
22. **(21b)** On a fresh profile with no history and no play log, right-click the taskbar button: the "Jump back in"
    heading is **absent**, not drawn empty (`AppendCategory` is skipped on an empty array — `JumpList.cs:230-236`).
    Only Tasks is shown. 0.3 must match. Repeat after removing every row by hand (right-click a row ▸ Remove from
    this list, six times): the same — the removed-args filter can empty the array too.
23. Click a "Jump back in" row. Wavee comes to the foreground and opens that exact page.
24. Click Tasks ▸ Search. Wavee comes to the foreground on the search route.
25. Click Tasks ▸ Pause (while playing). Playback pauses **and the window comes forward** — the task launches a second
    process, the single-instance gate redirects it, and every redirected activation calls `DeepLink.WakeWindow()`
    before the verb is applied (`Program.cs:485-488`). 0.3 must match. Also click it while Wavee is minimised and
    while it is closed (cold launch → the app starts and immediately resumes/pauses).
26. Skip tracks ten times in twenty seconds. The Jump List does **not** change more than once in that window (the
    60 s floor). Confirm by right-clicking the taskbar button between skips.
27. **[SIM]** Settings ▸ Notifications: "Windows notifications" ON, "New albums and singles" = **Windows**, quiet hours
    OFF. Press *Send event* on that row. A Windows banner appears with the album's cover as a **square** appLogo, the
    album name as the title and "New release — {artist}" as the body. The result line reads
    "Added to the bell and shown as a Windows banner."
28. **[SIM]** Same, on "New followers". The banner's image is **circle-cropped**.
29. **[SIM]** Same, on "Concerts and live shows". Click the banner. **Differs from 0.2.9, deliberately — (#n)
    (plan §9.6 Q2, 2026-09-12).** In 0.2.9 the click almost certainly lands nowhere (DATA GAP 1: `ActionUri` is not a
    route key and `IsKnown` rejects it). 0.3 fixes this: the click routes the `ActionUri` through
    `RichText.RouteForUri` first, the same way the in-app panel row already does, so it opens the concert page. A
    reviewer diffing against the golden 0.2.9 capture should see this as the recorded fix, not file it as a
    regression.
30. **[SIM]** Set quiet hours to cover the current hour. Press *Send event* on "New albums". **No banner**; the result
    line says the banner is held until {time}, and the row still appears in the in-app bell.
31. **[SIM]** Turn "Play a sound" OFF. Press *Send event* on "New albums" — silent banner. Then on "Pre-saved albums,
    on release day" (dialled to Windows), close Wavee and wait 3 minutes — record whether the drop toast chimes.
    **Differs from 0.2.9, deliberately — (#n) (plan §9.6 Q3, 2026-09-12).** In 0.2.9 this is DATA GAP 4: the drop
    toast never calls `Silent()`, so it chimes regardless of the preference. 0.3 fixes it: `ReleaseNotifier` gains
    the same `policy.Sound` check `DaylistNotifier` and `ToastEscalator` already have, so with "Play a sound" off the
    drop toast is silent too. A reviewer diffing against the golden 0.2.9 capture should see this as the recorded
    fix, not file it as a regression.
32. **[SIM]** "Pre-saved albums, on release day", dialled to Windows: press *Send event*. The result line reads
    "Scheduled for {time}". Close Wavee. Three minutes later a banner appears with a **hero** cover, the album name,
    "{artist} — out now", and two buttons: **Play** and **Dismiss**.
33. Click that banner's **Play** button: Wavee launches (cold) and starts playing the album.
34. Repeat item 32 and click the banner **body** instead: Wavee launches and opens the album page without playing.
35. **[SIM]** "Your daylist refreshed", dialled to Windows: press *Send event*, close Wavee, wait 3 minutes. A banner
    reads "Your daylist has refreshed" with a body naming the window that ended.
36. **[SIM]** Dial "New albums" to **In Wavee**. Press *Send event*: **no banner**, the row is in the bell, the result
    line reads "Added to the bell in Wavee. No Windows banner, as configured."
37. **[SIM]** Dial it to **Off**. Press *Send event*: nothing anywhere, result line "Dropped — this kind is switched
    off, so nothing was delivered." The bell count does not change.
38. **[SIM]** Press *Send event* on "New albums" **five times** in a row. At most **3** individual banners, plus one
    summary banner reading "2 more updates in Wavee" / "Open Wavee to see them."
39. **[SIM]** Press *Send event* on "Your own library activity". The row appears in the bell; **no banner ever**;
    the result line says this kind never banners.
40. **[SIM]** Press *Send event* on "Wavee updates". An **in-app** card appears titled "Wavee Simulated is available"
    with a single **Update now** button, auto-dismissing after ~5 s. **No Windows banner** accompanies it.
41. Relaunch `--fake` immediately after item 38. **No banners replay** on startup (the watermark held).
42. Clear `NotifyLastToastedMs` (or use a fresh profile) and relaunch with a populated feed: the first rebuild raises
    **nothing**.
43. Sign out (or close the app) and right-click the taskbar button: record whether "Jump back in" still lists the
    previous session's rows. This is DATA GAP 8 — 0.3 must match or deliberately fix.
44. Run the 0.2.9 exe **elevated**. No toast ever appears (`IsSupported == !IsElevated`); the SMTC card, taskbar
    button and jump list still work.
45. Delete `assets\taskbar\pause.ico` from the publish folder and relaunch. The thumbnail toolbar still shows three
    **working** buttons with correct tooltips (glyphs may be missing); the paused overlay is simply absent. Nothing
    crashes and nothing is logged as an error.
46. Close Wavee while paused. The taskbar button's overlay and progress are cleared before the window disappears (no
    ghost badge on a re-pinned button).
47. **[LIVE]** With a real update available, let it download. **One** Action Center banner titled "Updating Wavee"
    with a progress bar that advances in ~5% steps — never a second banner per tick. When it completes, one banner
    "Wavee updated to {name}" / "See what's new" whose click opens the What's new page for that version.
48. **[LIVE]** Confirm that `AppUpdateState.Available` produces **no** Action Center banner at all — only the in-app
    card.
49. Play a track whose title, artist and album are all very long (a classical recording is the easy case). The SMTC
    card ellipsises them itself and Wavee sends them whole — no app-side truncation anywhere. Same for a jump list row
    whose playlist name is 120 chars: the shell truncates the label; the row still launches the right page.
50. Play a track with NO artwork (a local file in `--fake`). The SMTC card shows the OS placeholder and no art; the
    taskbar and jump list are unaffected. Nothing is logged.
51. **[SIM]** With a feed row that has no image, press *Send event* on "New albums": the banner is **text only**, left
    aligned, no empty square. Not an error state.
52. **[SIM]** Turn Wavee's notifications OFF in **Windows** Settings ▸ System ▸ Notifications, then press *Send event*
    on "New albums": no banner, the row still lands in the bell, nothing is logged, and Settings ▸ Notifications shows
    the blocked-by-Windows banner (ch. 27). Confirm the result line does NOT claim a banner was shown.
53. **[SIM]** Press *Send event* on "Concerts and live shows" and read the banner's TITLE character by character: if
    the feed's title leads with an emoji, the banner keeps it while the same row on Home does not (DATA GAP 11).
    Record which one 0.3 adopts.
54. Hover a "Jump back in" row for two seconds: today the tooltip is the raw `spotify:…` uri (DATA GAP 12). Record it;
    0.3 must match or deliberately fix.
55. Click a thumbnail-toolbar button and check the thumbnail flyout is **still open** afterwards (DismissOnClick is
    false), so prev→prev→next works without re-hovering.
56. Set Windows' "number of recent items to display in Jump Lists" (Settings ▸ Personalization ▸ Start) to 3, then
    right-click the taskbar button: Explorer shows fewer rows than the 6 Wavee published. Wavee does not adapt and
    must not start (DATA GAP 13).
57. **[LIVE]** While an update is installing, confirm the Action Center shows the **Installing** banner (W23b:
    "Restarting to finish updating…" as the TITLE, no body, no progress bar) and that the Downloading banner is not
    still being written to.
58. Sign out and right-click the taskbar button, then wait for a previously scheduled drop time: both the recents and
    the scheduled toasts survive the sign-out (DATA GAP 8). Record it; 0.3 must match or deliberately fix.

---

## 11. Audit log

Adversarial fidelity audit, 2026-09-12. Every line below was checked against the 0.2.9 sources in
`src/apps/Wavee/**` and the engine in `..\fluent-gpu\src\FluentGpu.WindowsApi\**`; "wrong" means the chapter's
number or citation did not match the code.

- **WRONG** §2 W7 / §3: `WaveeAppIcon.cs:268` cited for the app icon's 7 frames — that file is **27 lines** and only
  resolves a path (`:15-26`). The frame list itself is correct (16/24/32/48/64/128/256 @ 32 bpp, 122,347 B, verified
  by reading the ICONDIR); re-cited to the `.ico` itself and to `assets/AppIcon/generate-appicon.ps1:77`.
- **WRONG** §2 W18: `NotificationPrefs.cs:244` for `TopicOf` — the file is 96 lines; corrected to `:91`.
- **WRONG** §6 gate 3: `NotificationPrefs.cs:219` for the master dial — corrected to `:62-72` (`:66` reads
  `NotifyWindows`), and the null-settings default (`WindowsEnabled: false, Sound: true`, `:64`) added.
- **WRONG** §9 trap: `NotificationPrefs.AllTopics` "declaration order (`:172-184`)" — it is `:21-31`. §8's row now
  carries the real ranges for `AllTopics`, `TopicOf` and `ShowsCategory`.
- **WRONG** §8: "already source-included by the test project (`:25`)" — the include is `Wavee.Tests.csproj:317`
  (comment `:315-316`).
- **WRONG** §8: `AppUpdateToastsTests` "13 facts" with 12 line cites — there are **12** `Plan` facts and 15
  `[Fact]`/`[Theory]` in the file.
- **WRONG** §7 gap 6: `NotificationModels.cs:41` for `NewReleaseNotification.Id` — `:41` is the `NewReleaseKind`
  enum; the record is `:42-45`.
- **WRONG** §7 gap 2: `Models.cs:38-40` names no file in the tree — the type is
  `../Wavee.Core/Domain/Models.cs` (`Url` `:28-32`, `LargestUrl` `:38-48`).
- **WRONG (overclaim)** §7 gap 3: "Ten visible string groups have no loc key … 0.3 must add keys (namespace `os.*`)".
  `assets/loc/en-US.json` **already carries** `jumplist.*` (11), `taskbar.*` (5) and `toast.*` (10) — including an ICU
  plural for the burst summary — and **nothing calls them** (`grep Strings.Jumplist|Strings.Taskbar|Strings.Toast`
  = 0 hits). Rewritten: 0.3 wires the existing keys and adds ~4 new ones (the daylist body trio, the drop-title
  fallback). "Dismiss" is the engine's default argument (`ToastBuilder.cs:221`), not a Wavee string.
- **WRONG (arithmetic)** §9 budget: `Notify.cs` "~390" is below the 385 lines it already absorbs, so the new
  `EscalationPlan` had nowhere to live → **445**; `Playback.Os.cs`'s narrow reading is **818** raw, not 830. Totals
  restated (0.2.9 ~2,064; estimate ~2,421; the plan is ~47% short, not 46%).
- **WRONG (undercount)** header: the notification-centre arms are **~159** raw lines (`:55-58, :201-206, :216-287,
  :289-353`), not ~135; wiring is ~22 lines, not ~14, once `WaveeApp.cs:212-213`, `WaveeShell.cs:561-562`,
  `SettingsPage.Notifications.cs:270-271` and `Program.cs:483-488` are counted.
- **UNVERIFIED → RESOLVED** §2 W16 / item 22: whether Explorer draws an empty "Jump back in" heading. It never gets
  the chance — `AppendCategory` is guarded on `count > 0` (`JumpList.cs:230-236`), so an empty category is not
  published. W16 and parity item 22 rewritten as a statement plus a visual confirmation.
- **UNVERIFIED → RESOLVED** §2 item 25: whether Tasks ▸ Pause brings the window forward. It does — every redirected
  activation calls `DeepLink.WakeWindow()` before the verb runs (`Program.cs:485-488`).
- **UNVERIFIED → RESOLVED** §7 gap 8: whether the sign-out teardown is wired. It is not, anywhere:
  `JumpList.Clear` has no caller in `src/apps`, `ReleaseNotifier.UnscheduleAll`'s only caller is its own
  `RequestReconcile` (`:111`), and `ToastImageCache.Clear` has none either.
- **MISSING (state)** No wireframe for the **Installing** Windows banner, which non-negotiable 12 says reaches the
  OS — added as **W23b** (`ToastEscalator.cs:231-232`), including the deliberate title/body inversion against the
  in-app card (`AppUpdateToasts.cs:84-89`).
- **MISSING (state)** No "no image" arm on W17/W18/W24 and no localize-failure arm: `AppLogo`/`Hero` are simply never
  called (`ToastEscalator.cs:170-175`; `ReleaseNotifier.cs:188-192`), and a failed download returns the original
  https URL which the unpackaged platform drops (`ToastImageCache.cs:82-83`). Added, plus parity items 50-51.
- **MISSING (state)** No long-text rule anywhere: added to §2's caveat — Wavee truncates nothing; the only caps are
  the engine's 259-char tooltip (`TaskbarManager.cs:18`) and the 5,120-byte payload that *throws* inside
  `TryRaise`'s catch (`ToastBuilder.cs:62`). Parity item 49.
- **MISSING (element)** A jump list row's hover tooltip is the raw `spotify:` uri — the 5th positional arg of
  `JumpListItem` is `Description` (`JumpListBridge.cs:169`, `:183`; `JumpList.cs:44-45`). Added to W14, §3 and as
  DATA GAP 12 + parity item 54.
- **MISSING (element)** The concert banner keeps the feed's leading emoji while Home strips it via
  `SpotifyUpdates.CleanTitle` (`SpotifyUpdates.cs:74-96`, sole caller `HomeModules.Timeline.cs:110`). Added to W19,
  DATA GAP 11, §8's new `SpotifyUpdates` row and parity item 53.
- **MISSING (element)** `ThumbButton.DismissOnClick` is never set, so the thumbnail flyout stays open on click
  (`TaskbarManager.cs:22`; `TaskbarBridge.cs:160-163`). Added to W10, §3 and parity item 55.
- **MISSING (motion)** Four cadences absent from §5: the app-update poll that drives every update toast (30 s then
  24 h — `AppUpdateScheduler.cs:20-21`, started at `PlaybackBridge.cs:1024-1029`), the in-app card's 5,000 ms /
  sticky duration, and the fact that the jump list's 60 s floor has **no timer** — a pending `_dirty` waits for the
  next state push (`JumpListBridge.cs:112-117`).
- **MISSING (gate)** The gate ladder had no gate 0: `Show` **throws** when `Register` never succeeded
  (`ToastNotifier.cs:225-226`) and `TryRaise` swallows it, so a failed registration is total silence. Added, together
  with the note that `Show` can report success for a toast Windows suppresses (`:240-243`), the **scheduled** path's
  own shorter ladder (`ReleaseNotifier.Allowed`, `:87-89`), and the fact that the progress push is gated only by its
  latch, not by the ladder (`ToastEscalator.cs:59-61`, `:128`, `:165`).
- **MISSING (interaction)** `SystemMediaControlsBridge.Dispose` (`:215-228`) had no row — it unsubscribes and sets
  `IsEnabled = false` without a `ClearDisplay`. Added, alongside the second-process → single-instance-gate hop that
  makes every jump list and toast activation reach the running window (`Program.cs:485-488`).
- **MISSING (readiness)** `Rebuild` bails out entirely when `Environment.ProcessPath` is empty
  (`JumpListBridge.cs:125-126`), and every banner depends on a successful `Register` — both added to §7's table.
- **NEW GAP 10** `ReleaseNotifier.Scheduled` is a non-persisted `static` set (`:38`) while the OS entry outlives the
  process, so the stale sweep (`:136-144`) finds nothing at launch: an album un-pre-saved while Wavee was closed
  keeps its toast forever.
- **NEW GAP 13** `BeginList`'s `maxSlots` is read and ignored (`JumpList.cs:209-211`): the 6-row cap is Wavee's, and
  Explorer may show fewer.
- **ADDED (§9)** Three more load-bearing halves the re-author must keep: `UpdateChanged` resetting the progress latch
  on the way out of Downloading (`ToastEscalator.cs:117`), the taskbar seeding **after** `_active = true`
  (`TaskbarBridge.cs:69-71`), and `Rebuild` re-reading the live play state rather than `_lastPlaying` (`:131`).
  Also: the two jump list halves share one `seen` set only because both keys spell `album:<uri>` (`:168`, `:181`).
- **ADDED (§9)** The plan's Wave-6 test bucket *"tests of … bridges, wiring → Delete"*
  (`wavee-0.3-implementation.md:775`) is a live hazard for `SmtcTimelineCoalescerTests`; and `Platform/` is listed as
  7 files (`:78-79`) with no room for `Notify.cs` + `Notify.Host.cs`. The words "notification" and "toast" appear
  nowhere in the plan.
- **QUALIFIED** §9's `ToastImageCache.Localize` stall trap: `ShouldLocalize` returns false for a packaged process
  (`:133`) and the shipped Wavee is MSIX, so the blocking download is a dev/unpackaged path only.
- **CONFIRMED, no change** every number in non-negotiables 1-14; the `SmtcTimelineCoalescer` and
  `NotificationPolicy` test-line lists in §8 (all 9 and all 16 line cites match); the `.ico` byte sizes and frame
  counts in §3; `ToastBuilder.cs:62/63/64/92/105-106/145-149/155/221`; `TaskbarManager.cs:18/93/286-288`;
  `SystemMediaControls.cs:271-275`; `ToastImageCache.cs:49/57/65-85/76/88/133/146-157`; `ToastNotifier.cs:123`;
  `SettingsPage.Notifications.cs:233-257`; `Win32Platform.cs:675` (no `RegisterHotKey`, no media `WM_APPCOMMAND`
  anywhere — the media-keys claim holds); and every `PlaybackBridge.cs` wiring line.

**arbitration 2026-09-12:** one notification stack, owner **I**, **Wave 4**. `AppUpdateToasts` (A8), the whole feed
and its prefs/policy (A9) and `ToastEscalator`’s two halves (A11) live in `Platform/Notify.cs` (CORE) +
`Platform/Notify.Host.cs` (SHELL) and nowhere else — not `Screens/ReleaseNotes.cs`, not `Platform/Platform.cs`, not a
second `Entities/Notification.*` pair. §1’s tree and §9’s ownership table were rewritten from **owner S / Wave 6** to
**owner I / Wave 4** (the in-app panel is shell chrome and shares the model, `NotificationCenterBridge.cs:201-206`),
the update-card row moved from `Shell/Shell.cs` to `Shell/Shell.UI.cs` where the panel and its rows live (ch. 19),
and §1’s `Notify.cs` budget was corrected 390 → 445 to agree with §9’s own arithmetic, with the whole CORE file
stated once as ~1,000 (this chapter’s ~445 + ch. 19’s ~555 feed fold) so the two chapters cannot double-count it.
The HWND-line split itself is unchanged: SMTC, taskbar, jump list and the app icon stay owner **H** in Wave 3.

**consistency 2026-09-12:** header still proposed owner S for the notification stack and cited `Playback.Os.cs`'s
original 1,100-line budget / "~47% short" as current. Per A9 the toast/notification stack is owner **I**, **Wave 4**
(`Platform/Notify.cs` + `Notify.Host.cs`); SMTC/taskbar/jump-list/app-icon stay owner **H**, **Wave 3**, budget
**840** (already reflected in this chapter's own §9 re-budget and arbitration note above). Header rewritten to state
the settlement; the "where the plan is wrong" bullet and the line-budget table's plan-target row restated against
the plan's 840, with the original 1,100 kept as historical context. §11's existing WRONG-arithmetic entries (dated
before the settlement) were left untouched as the historical record.

**answers 2026-09-12: Q2 and Q3 are both DECIDED, fixed rather than ported verbatim.** DATA GAP 1 (the concert/social
toast's launch argument) is fixed by routing `ActionUri` through `RichText.RouteForUri` before composing the deep
link, the way the in-app panel row already does; DATA GAP 4 (`ReleaseNotifier` never calling `Silent()`) is fixed by
giving it the same `policy.Sound` check `DaylistNotifier` and `ToastEscalator` already have. Both are deliberate
divergences from 0.2.9, each needing a GitHub issue and a `(#n)` CHANGELOG bullet — **neither issue is filed yet**.
Parity items 29 and 31 are restated as "differs from 0.2.9, deliberately — (#n)" rather than left as open questions,
so a side-by-side reviewer records the difference as the intended fix instead of filing it as a regression.
