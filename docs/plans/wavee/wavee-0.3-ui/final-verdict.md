# Final verdict — what Wavee 0.3 will look like, surface by surface

> **What this is.** The single visual verdict for the 0.3 rebuild, derived from the 32-chapter UI fidelity contract
> (`docs/plans/wavee/wavee-0.3-ui/00-design-system.md` … `31-fake-data.md`, mapped by `00-index.md`: 119 surfaces,
> 792 wireframes, 2,650 parity items) and from the implementation plan as revised on 2026-09-12
> (`docs/plans/wavee/wavee-0.3-implementation.md` §2 tree, §5 waves incl. Wave 4.5, §8 gates G1-G6, §9 arbitrations
> A1-A18 and the nine open questions). It is a review artefact, not a fourth copy of the contract: one canonical
> frame per surface, copied **verbatim** from its chapter (the `file:lines` under each frame is where it was cut
> from), the 0.3 file and owner that will build it, and the handful of non-negotiables that must survive the port.
> The one sentence that matters: **0.3 reproduces 0.2.9 surface by surface, and these are the frames it will be
> checked against** — side by side with the kept 0.2.9 Release build
> (`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`), at the widths
> each frame names, against the golden captures Wave 0 takes before anything moves.

How to read a surface entry: a heading with the surface name and its route key (or "no route"); one line with
the owning 0.3 file(s), wave / owner and chapter; the frame; then 3-6 bullets of "what must not be lost", taken
from that chapter's §0 non-negotiables — shortened, never softened, with the numbers and `file:line` citations
kept. Where a surface has two genuinely different compositions (the detail pages' Automatic vs Hero arms, the
sidebar's three designs, a page that reflows rather than shrinks when narrow) both are shown. Where a chapter has
20+ frames, one is shown and the chapter is named for the rest. Owner letters are the plan's: I shell · J sidebar ·
K rail/stage/deck/lyrics/video · L design/controls/drag · M detail frame + track + album/show · N artist/concerts ·
O playlist/user · P home/search/browse/recents · Q queue + seed · R settings/setup/notes/feedback · S diagnostics/
platform · T modules · H playback OS surfaces.

---

## 1. The shell map

One window at the default 1600 × 900, rail closed. Every region below is a paint-site **omission** over live Mica
except the content card and (when open) the rail band — that is the single most visible property of the frame.

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48  chrome row (no fill: live Mica)
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋      [ 🔎 Search songs, artists, albums...  ]      ◉ Christos  🔔 👥 📌 ⚙│  — │ 48 tall, ExpandedHeight
├──────────────┬─────────────────────────────────────────────────────────────────────────────┬───────┤    ⌄ theme toggle then ─ □ ✕
│              │╭────────────────────────────────────────────────────────────────────────────╮       │
│  sidebar     ││ content region: FileArea over Mica, 1px stroke on LEFT+TOP, corner 8,0,0,0 │       │
│  280 DIP     ││                                                                            │       │
│  (Mid tier,  ││   ← the page (ch 03/05/10/…) lives here, flush to all four edges            │       │
│   no fill)   ││                                                                            │       │ 780 = 900 − 48 − 72
│              ││                                                                            │       │
│              │╰────────────────────────────────────────────────────────────────────────────╯       │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤
│  player bar — 72 DIP, paints nothing (Mica), ch 20                                                  │ 72
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ↑ sidebar 280           ↑ content 1320 (= 1600 − 280)                                       rail: closed ⇒ page is flush to the window edge
```

*ch 18 W1 — wide, loaded, rail closed @ 1600×900 (1 char ≈ 16 DIP)* — `18-shell-frame.md:206-221`

| Region in the frame | What it is | 0.3 file | Wave / owner |
|---|---|---|---|
| 48-DIP chrome row (`☰ ◀ ▶ │Home│… ＋ [🔎 Search] ◉ Christos 🔔 👥 📌 ⚙ ⌄ ─ □ ✕`) | the window's title bar: nav cluster, text tabs, omnibar, trailing identity island, theme toggle, caption buttons | `Shell/Shell.UI.cs` (frame, tab strip, drill trail) + `+Shell.Masthead.UI.cs` (omnibar + suggestion popup) | 4 / I |
| masthead band (`Home › Browse › X`, not in this frame — Browse/Search/Concerts families only) | one overlay above the keep-alive boundary, paints nothing | `+Shell.Masthead.UI.cs` | 4 / I |
| sidebar 280 (Mid tier; 240 below 1400) | the pane: Classic / Library V3 / Wavee Curated over ONE renderer; the 56-DIP collapsed rail | `Shell/Sidebar.UI.cs` (+ `Sidebar.cs`, `Sidebar.Doc.cs`, `Sidebar.Host.cs`, `Sidebar.Customizer.UI.cs`) | 4 / J |
| content region 1320 (`FileArea` + 1-px stroke LEFT+TOP, corner 8,0,0,0, no shadow) | where every routed page lives, flush to all four edges; the page-swap fade-through; the material tint/washes behind it | `Shell/Shell.UI.cs` + `Shell/Shell.cs` (routes, nav, material ownership) + `Platform/Design.cs` (tokens, washes) | 4 / I + L |
| right rail band 340 (closed here; W8 below shows it inline) | docked NPV / queue / friends / lyrics / video; floating when `!RailFits` | `Shell/Rail.UI.cs` (+ `Rail.cs`, `+Rail.Styles.UI.cs`), `Entities/Queue.UI.cs`, `Shell/Lyrics.UI.cs`, `Shell/Deck.UI.cs`, `Shell/Video.UI.cs` | 4 / K (queue 5 / Q) |
| player bar 72 (paints nothing; one 1-DIP hairline on top) | transport, seek bar, volume, device picker, the four rail toggles | `+Shell.PlayerBar.UI.cs` + `Shell/Shell.cs` (`Shell.Ui` rail state) | 4 / I |
| overlay lanes (not visible at rest) | command palette, profile menu, notification panel, toasts, tips, runtime banner + setup card, dialogs, the stage, fullscreen video, PiP | `Shell/Shell.Palette.cs`, `+Shell.Overlays.UI.cs`, `Shell/Shell.UI.cs` (panel), `Platform/Controls.cs` (dialog helpers), `Shell/Stage.UI.cs`, `Shell/Video.UI.cs` | 4 / I, L, K |

What must not be lost (ch 18 §0):

- ONE 48-DIP chrome row, and it is the title bar — no second toolbar row, no plate, no hairline under it
  (`TitleBar.ExpandedHeight = 48`, `WaveeShell.cs:943-977`).
- The chrome, the sidebar band and the player dock paint NOTHING; only the content region and the rail band paint
  `WaveeColors.FileArea` (dark `#4C3A3A3A`, light `#80FFFFFF`) with a 1-px stroke on left+top and corners `8,0,0,0`
  (`WaveeShell.cs:1082-1087, :150`). A page must never read darker than the chrome around it.
- Sidebar collapse and the content card move on ONE 300 ms `cubic-bezier(0, 0.35, 0.15, 1)` transition, FLIPping
  against `"shell.content-row"` (`WaveeShell.cs:144-147, :1126-1135`).
- Page swaps are a fade-through: exit 120 ms `EaseOut` in place, enter from 90 ms at `Dx = 8` over 250 ms
  `SmoothOut`; a page hosting composited video takes the translate-only pair (`PageNavMotion.cs:49-68, :96-121`).
- The chrome row is allocated by arithmetic, not a threshold table (`MergedChromeLayout.Resolve`,
  `MergedChromeLayout.cs:51-69`): promotions wait 40 DIP, demotions are immediate, every published width is
  quantised to 10 DIP; the search field yields all the way to a 44-DIP magnifier before a single tab is shed.
- Three retained pages keyed by tab + route; Back shows a page exactly as it was, scroll and selection included
  (`ContentHost.cs:96-108`).

---

## 2. Wave 3 — the four OS surfaces (owner H, `Playback/Playback.Os.cs`)

Painted by Explorer, the Shell and the Action Center, not by Wavee. Checked at the Wave 3 gate (SMTC, taskbar) and
re-checked in Wave 5 (jump list, once history and library attach). Chapter 14 is the whole contract — 29 frames.

### SMTC media overlay card — no route

`Playback/Playback.Os.cs` · Wave 3 / owner H · chapter 14

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

*ch 14 W1 — SMTC media overlay card, Playing, seekable track @ 360 schematic (OS-owned)* — `14-os-surfaces.md:268-290`

### Taskbar button — no route

`Playback/Playback.Os.cs` · Wave 3 / owner H · chapter 14 (W10-W13 draw the thumbnail toolbar: ⏮ ⏸ ⏭, re-added on an
Explorer restart, degrading to tooltips when an `.ico` is missing)

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

*ch 14 W7 — Taskbar button, Playing @ 44×40 px button, 100% DPI (OS-owned)* — `14-os-surfaces.md:350-361`

### Jump list — no route

`Playback/Playback.Os.cs` (behind the `AttachHistory` / `AttachLibrary` late-binding seam) · Wave 3 / owner H · chapter 14

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

*ch 14 W14 — Jump list, playing, full category @ OS-owned (list ≈ 260 wide)* — `14-os-surfaces.md:439-482`

### Windows toasts / Action Center — no route

`Platform/Notify.cs` (CORE) + `Platform/Notify.Host.cs` (SHELL) — **owner I, Wave 4** by A9, not H · chapter 14
(W18-W25 draw the other nine: episode, concert, follower, burst summary, update ×3, pre-save hero, daylist)

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

*ch 14 W17 — Windows toast: new ALBUM release @ 364×~100 px (OS-owned ToastGeneric)* — `14-os-surfaces.md:511-530`

What must not be lost (ch 14 §0):

- Every OS surface reads `PlaybackBridge`'s unified signals, never the engine `IMediaPlayer` — the only source that
  is right while a remote Connect device owns playback (`SystemMediaControlsBridge.cs:96-134`, `TaskbarBridge.cs:87-91`).
- Every OS call is edge-deduped and every high-rate call coalesced: position ticks go through
  `SmtcTimelineCoalescer` in BOTH bridges with a whole-second gate (`SmtcTimelineCoalescer.cs:60-63`); one
  `UpdateTimeline` is a cross-process COM RPC (~1 ms), N queued ticks must cost one.
- The taskbar button has exactly three states and Playing carries NO overlay — the green determinate fill IS the
  cue; paused is `pause.ico` + yellow `Paused`; no track clears both (`TaskbarBridge.cs:94-121`).
- Jump List rows are `wavee://` verbs with resolved titles and `.ico` glyphs — never `spotify:`, never a cover
  (`JumpListBridge.cs:135-138, :190-201`); rebuilds capped at one per 60 s except on a play/pause edge (`:38, :115`);
  published against the toast layer's AUMID or it silently never appears (`:145`).
- The toast watermark advances past everything CONSIDERED, and a zero watermark raises nothing — drop either half
  and enabling notifications replays the whole feed (`ToastEscalator.cs:50-54, :64-65`). At most 3 banners per
  rebuild, oldest → newest, the rest collapse into one summary (`:31, :69-83`).
- `AppUpdateState.Available` raises NO Windows toast (`:223-226`); an update is STATE with `Timestamp = long.MaxValue`,
  progress drives the showing toast through `ToastNotifier.Update` at 5-point granularity (`:95-132`). Quiet hours
  suppress a live banner and SHIFT a scheduled one (`NotificationPolicy.cs:124-133`).

---

## 3. Wave 4 — the shell chrome, player bar and overlays (owner I)

Files: `Shell/Shell.cs`, `Shell.UI.cs`, `+Shell.Masthead.UI.cs`, `+Shell.PlayerBar.UI.cs`, `+Shell.Overlays.UI.cs`,
`Shell.Palette.cs`, `+Shell.History.UI.cs`, `Shell.Host.cs`, `Platform/Actions.cs` + `Actions.UI.cs` (A7),
`Platform/Notify.cs` + `Notify.Host.cs` (A9), `+Screens/Setup.UI.Runtime.cs` (A18). Chapters 18, 19, 20, 16 (history),
29 (network chrome, file-drop, first run).

### Window frame with the right rail inline — all routes

`Shell/Shell.UI.cs` · Wave 4 / owner I · chapter 18 (26 frames; W2-W7 are the chrome-row shedding ladder 1360 → 420,
W5/W6 the narrow shell and its drawer at ≤ 720, W23 restored/maximized/snapped)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48 chrome
├──────────────┬──────────────────────────────────────────────────────────┬─┬─────────────────────────┤
│ sidebar 280  │ content 972 = 1600 − 280 − 8 − 340                       │ │ rail band 340           │
│              │ FileArea + stroke                                        │8│ FileArea + corner 8,0,0,0│
│              │                                                          │ │ (RightRail paints       │
│              │                                                          │g│  Transparent while docked)│
├──────────────┴──────────────────────────────────────────────────────────┴─┴─────────────────────────┤
│ player bar 72                                                                                        │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
   RailFits ⇔ sidebar + rail + 480 ≤ viewport   (ShellUi.CanFitRail :88-90 → ShellResponsiveLayout.cs:164-165)
   RailWidth clamp 200…500, default 340 (:126)   the 8-DIP gap exists ONLY while inline (WaveeShell.cs:1147-1151)
```

*ch 18 W8 — right rail open, inline @ 1600 (1 char ≈ 16 DIP)* — `18-shell-frame.md:377-389`

- `RailFits ⇔ sidebar + rail + 480 ≤ viewport` (`ShellResponsiveLayout.cs:164-165`); rail width clamps 200…500,
  default 340; the 8-DIP gap exists ONLY while inline (`WaveeShell.cs:1147-1151`). Below `RailFits` the rail
  floats over the page with its own `FileArea` + ring + `Elevation.Flyout` (ch 21 W8).
- Full-screen video UNMOUNTS the chrome row and the player bar — not opacity-0, not off-screen — from one derived
  predicate (`WaveeShell.cs:843-849, :1347, :1460-1463`).
- Nothing in the frame re-renders on navigation except what must: the shell component renders once; the route
  reaches the chrome through signals, the material through its own component, the page through the keep-alive
  boundary (`WaveeShell.cs:18-23`, `ContentHost.cs:96-108`).
- The narrow drawer (viewport ≤ 720, ch 18 W6): pane width `clamp(_sidebarWidth, 240, viewport−32)`, acrylic,
  corners 0,8,8,0, scrim `#33000000`, translate −width→0 over 300 ms `SmoothOut`; a SECOND `SidebarHost` mount over
  the same state.

### Masthead band and drill trail — `browse`, `browse:<uri>`, `home-section:`, `browse-section:`, `search`, `concerts` families

`+Shell.Masthead.UI.cs` · Wave 4 / owner I · chapter 18 — no frame is copied here: ch 18 W13 draws the band alone at 1280
(`Home ›  Browse ›  Weekly Song Charts … [ Show all ]`, x = 36, top 32, reserve 84), and the Browse directory / category
frames under Wave 5 below show it over its pages

- Mounted ONCE, as an overlay above the keep-alive boundary; never takes column height, never double-exposes during
  a swap, fades opacity (120 ms) on leaving the family instead of collapsing to 0 (`ContentHost.cs:111-118`,
  `ShellMastheadBand.cs:23-26, :64-73`).
- Reserve = `Spacing.XXXL 32 + 52 = 84` (`BrowseMastheadMetrics.cs:12-13`); the page body scrolls UNDER it and clips
  itself at 84 with a 24-DIP top `EdgeFade` that arms only once the clip engages (ch 13 §0.14).
- The title is `TitleLarge 40/52`, Display face, weight 400, −12/1000 em; crumbs are `TextTertiary`, clickable,
  hover → `TextSecondary`; "Show all" is a `Button.Subtle/Small` at the band's right edge (ch 12 §0.7).

### Omnibar and suggestion popup — all routes

`+Shell.Masthead.UI.cs` (the field, the popup) + `Entities/Search.cs` (`OmnibarSuggestQuery`, owner P) — a cross-owner
contract · Wave 4 / owner I (+ 5 / P) · chapters 18 + 13 (ch 13 W13/W14 draw the Results / Pending / Empty / Failed
states)

```
        ┌ field: AutoSuggestBox, minHeight 32, Radii.Control, standard chrome, width = SearchWidth (≤ 420)
        │  [ 🔍  bad bunny|ny                                        ]        ← ghost completion (SearchSuggestions.GhostFor)
        ├──────────────────────────────────────────────────────────────┐  popup width = max(field, 400); MaxHeight 560
        │ ▂▂▂▂▂ indeterminate progress bar (only while Pending)         │
        │ 🔍 bad bunny                        (query rows, max 6)       │  row: MinHeight ItemMinHeight, pad 12/8, margin 4/2,
        │ 🔍 bad bunny tickets                                          │       Radii.Control, match segment weight 700/TextPrimary,
        ├───────────────── 1px divider, margin 16/4 ────────────────────┤       rest weight 400/TextSecondary
        │ ▢ 44  Bad Bunny                    ▶      ⋯  [ Artist ]       │  rich row: 58 tall, gap 12, art 44 (circle r22 for artist/user,
        │        Artist                                                 │           radius 5 otherwise), title 14/600, sub 12/secondary,
        │ ▢ 44  Tití Me Preguntó             ▶  ♡  ⋯  [ Song   ]        │           trailing 28×28 circular buttons (gap 2) + type pill
        └───────────────────────────────────────────────────────────────┘           (pad 9/2, radius 10, FillSubtleSecondary, Eyebrow ink)
```

*ch 18 W15 — omnibar focused, suggestions open @ 1600 (1 char ≈ 8 DIP)* — `18-shell-frame.md:581-593`

- The search field never evicts a tab: tabs are measured against the 44-DIP magnifier, not the field
  (`MergedChromeLayout.cs:128-143`); `PreferredSearchWidth = quantise10↓(clamp(W × 0.28, 280, 420))`.
- The popup only says "No results found" for a confirmed empty answer: `SuggestState` is Idle / Pending / Results /
  Empty / Failed; Pending shows an indeterminate bar over the PREVIOUS answer's rows, Failed offers Retry
  (`OmnibarSuggestQuery.cs:8-22`, `ShellToolbar.cs:398-435`).
- An empty committed query is the Browse directory, not an empty Search page (`NavRouteNormalizer.cs:20-21`).

### Player bar — all routes

`+Shell.PlayerBar.UI.cs` + `Shell/Shell.cs` (tier + picker rules, the `Shell.Ui` rail-state section) · Wave 4 / owner I ·
chapter 20 (23 frames; W2-W6 are the six responsive tiers 1150 → 360, W12/W13 the live rails, W18 the video split button)

```
╞══════════════════════════════════════════ 1-DIP hairline, full bleed (Tok.StrokeDividerDefault / #0F000000) ══════════════════════════════════════════╡
│ 12 │<──────────────── left 260 ────────────────>│8│<──────────────────── centre 752 ────────────────────>│8│<────────── right 388 ──────────>│ 12 │
│    ┌────────┐                                    ┃                                                       ┃                                        │
│    │ cover  │  Song title that is long enough…   ┃  ⏮   ▶/⏸   ⏭   1:07 ├──────●≈≈≈≈≈≈≈──────┤ -2:34        ┃  🔀  🔁  🔊 ├────●────┤  ♪  🎬▾  ☰  🖳  ⌃ │
│    │  48    │  Artist One, Artist Two        ♥   ┃  32   40   32   44        seek 544         44          ┃  32  32  32     96      32  52  32 32 32│
│    └────────┘                                    ┃  └── gap 0 ──┘ ↔4↔ ↔6↔              ↔6↔               ┃  ↔2↔ between every right-cluster child   │
│     ↔8↔  meta 164 (title 14/700 · gap 2 · artists 12/16)                                                  ┃                                        │
```

*ch 20 W1 — Full, playing, all commands inline @ 1440 DIP* — `20-player-bar.md:180-188`

### Device picker — player bar

`+Shell.PlayerBar.UI.cs` (the roster is a read of `Spotify.Connect`'s cluster, not a new store) · Wave 4 / owner I · chapter 20

```
                       ┌─────────────────────────────────┐
                       │ This computer            (dim)  │  Header row = a DISABLED command row
                       │ ● 🖳 System default             │  radio, checked when we are the output & nothing picked
                       │ ○ 🔈 Speakers (Realtek)         │
                       │ ○ 🎧 WH-1000XM5                 │
                       │ ───────────────────────────────  │  Separator
                       │ Spotify Connect          (dim)  │
                       │ ○ 📱 iPhone                     │
                       │ ● 📺 Living Room TV             │  checked = the ACTIVE connect device
                       └─────────────────────────────────┘
                                              ▲ anchored on the 🖳 button, opens UPWARD
empty Connect roster (the --fake case):
                       │ Spotify Connect          (dim)  │
                       │ No devices found         (dim)  │
                       │ Open Spotify on another device  │
unsupported local playback: every "This computer" row is DISABLED and carries the accelerator text "Unavailable"
a Connect device owns playback: NO "This computer" row is checked — every local check is
    `localSupported && weAreActiveOutput && …` (DevicePickerModel.cs:61,65), and `weAreActiveOutput`
    is `RemoteDevice(b) is null` (PlayerBar.cs:770)
LONG roster: the flyout is a plain MenuFlyout with NO max height and NO scroll cap — 20 Connect
    devices is a 20-row menu; see §9 "Traps"
```

*ch 20 W16 — Device picker open (`FlyoutPlacement.TopEdgeAlignedRight`, light-dismiss, focus trap)* — `20-player-bar.md:394-416`

What must not be lost (ch 20 §0):

- The dock is 72 DIP and paints NOTHING — no fill, no shadow, no ring; the only ink is ONE 1-DIP hairline
  (`PlayerBar.cs:626-632, :646-660`, `WaveeSize.PlayerBarH = 72`).
- One centred row, three clusters, degrading by DROPPING commands never by shrinking buttons: secondaries 32×32 with
  a 16 glyph at every tier incl. the 300-DIP floor; primary 40/20 from Medium up, 36/18 below
  (`PlayerBarResponsiveLayout.cs:146-149`). Identity (art 48→40, title, artist, heart) survives to the floor.
- The top edge is the global activity cue, SEPARATE from the seek bar: a hairline at rest, an indeterminate
  `ProgressBar` sweeping `max(2400, barWidth)` over 2000 ms while loading / buffering / reconnecting (`:626-627`).
- The playhead is extrapolated on a pixel-due ticker between ~1 Hz reports and never snaps back under the finger
  (`SeekBar.cs:74-120, :373-411`); nothing hot re-renders the bar — seek fill/thumb are compositor binds on one
  `FloatSignal` (`PlayerBar.cs:24-27`).
- "Playing on <device>" is ONE authority: `RemoteDevice(b)` from the owner-derived `ActiveDeviceId`, no fallback to a
  device's own `IsActive` (`PlayerBar.cs:744-756`). **0.3 must drop the picker's `|| d.IsActive` second opinion**
  (`DevicePickerModel.cs:78`) and pin it in `DevicePickerItemsTests`.
- Live is a different rail: DVR maps the seekable window with an accent live-edge tick; radio replaces the rail
  with a 2-DIP breathing line; the right slot keeps a fixed 104-DIP reservation (`SeekBar.cs:216-233, :476-511`).
- Widening commits instantly; narrowing holds through a 24-DIP dip (`PlayerBarResponsiveLayout.cs:37-44`).

### Command palette (Ctrl+K) — overlay

`Shell/Shell.Palette.cs` · Wave 4 / owner I · chapter 19 (34 frames; W2/W3 the typed and `>`-only states)

```
                        anchor: BoxEl 560 x 0, AlignItems Center  →  x = (1440-560)/2 = 440
   y = 0  ┌ shell ZStack lane ────────────────────────────────────────────────────────────────┐
          │                    (HitTestVisible = false — the page below stays clickable)       │
   y = 64 │        ╔══════════════════════════════════════════════════════════════════════╗   │  ← Pad top 64 = Spacing.XXXL*2
          │        ║ acrylic Tok.AcrylicFlyout · 1px StrokeFlyoutDefault · r8              ║   │     WaveePalette.cs:61
          │        ║ Elevation.Flyout (blur 16 / dy 8 / #00000042 dark, #00000024 light)   ║   │     FlyoutSurface non-menu branch
          │        ║ ┌── pad 8 ──────────────────────────────────────────────────────────┐ ║   │     Padding = Spacing.S all round
          │        ║ │ ┌──────────────────────────────────────────────────────────────┐  │ ║   │
          │        ║ │ │ Search commands — type > for commands only                   │  │ ║   │  TextBox 544 x 32, r4
          │        ║ │ └──────────────────────────────────────────────────────────────┘  │ ║   │  placeholder, no header
          │        ║ │  ▲ 2  (BoxEl Height = Spacing.XXS)                                │ ║   │
          │        ║ │ ┌──────────────────────────────────────────────────────────────┐  │ ║   │  rows column, Gap 1
          │        ║ │ │  Home                                                  Enter │  │ ║   │  h32 r4, Fill FillSubtleSecondary
          │        ║ │ ├──────────────────────────────────────────────────────────────┤  │ ║   │  glyph AccentDefault @14
          │        ║ │ │  Search                                                      │  │ ║   │  glyph TextTertiary @14
          │        ║ │ │  Your Library                                                │  │ ║   │  label 14 TextPrimary, 1 line
          │        ║ │ │  Recents                                                     │  │ ║   │  pad (16,4,16,4), gap 8
          │        ║ │ │  Settings                                                    │  │ ║   │
          │        ║ │ │  Play                                                        │  │ ║   │  8 rows max (MaxResults)
          │        ║ │ │  Next                                                        │  │ ║   │  8*32 + 7*1 = 263
          │        ║ │ │  Previous                                                    │  │ ║   │
          │        ║ │ └──────────────────────────────────────────────────────────────┘  │ ║   │
          │        ║ └───────────────────────────────────────────────────────────────────┘ ║   │
          │        ╚══════════════════════════════════════════════════════════════════════╝   │
          │           560                                             total h = 8+32+2+2+2+263+8 = 317 (cap 460)
                                                       (pad 8 · field 32 · gap 2 · spacer 2 · gap 2 · rows 263 · pad 8)
```

*ch 19 W1 — Command palette, open, empty query @ 1440* — `19-shell-overlays.md:252-279`

- 560-DIP acrylic card, top edge exactly 64 DIP below the overlay lane, horizontally centred, never moves; a ZStack
  sibling lane, not an overlay entry (`WaveePalette.cs:27, :61`, `WaveeShell.cs:1475`).
- The selected row shows `Enter` at 11 px on its right edge, flips the glyph to `Tok.AccentDefault` and paints
  `Tok.FillSubtleSecondary` — three tells for one selection (`WaveePalette.cs:164-177`).
- Never scrolls, never exceeds 8 rows (`WaveeCommands.cs:18`), a caller-owned `dest` array with a linear scored
  insert — no LINQ, no per-keystroke allocation; `>` restricts to commands (`:92, :121, :259`).

### Profile chip, profile menu and the `Play ▸` cascade — chrome

`+Shell.Overlays.UI.cs` · Wave 4 / owner I · chapter 19 (W4 the chip's three ladder forms, W6 the folded menu at 1100 with
bell/friends rows, W7 the cascade, W8 the logout confirm)

```
                                                    anchored BottomEdgeAlignedRight to the chip
 ╔══════════════════════════════════════════╗  ← windowed popup: DWM transient acrylic + system shadow + rounded
 ║ presenter pad (0,2,0,2)                  ║    ConstrainToRootBounds = false   ProfileMenu.cs:171
 ║ ┌── body pad (0,6,0,6) ────────────────┐ ║    body MinW = MaxW = 304          ProfileMenu.cs:234-238
 ║ │                                      │ ║
 ║ │  ╭────╮                              │ ║  AccountHeader  Pad(14,10,14,10), Gap 12, AlignItems Center
 ║ │  │ CK │  Christos Karapasias         │ ║    PersonPicture 40 (initials "" → falls through to displayName)
 ║ │  ╰────╯  ★ Spotify Premium           │ ║    name   14/600 TextPrimary, 1 line, char-ellipsis
 ║ │          ohsnap2502@gmail.com        │ ║    tier   Gap 5 · star E734 @10 · badge 12
 ║ │                                      │ ║           premium ink #E6C26C dark / #8A6312 light  ProfileMenu.cs:29,300
 ║ │  ────────────────────────────────    │ ║           free    → Tok.TextSecondary "Spotify Free"
 ║ │                                      │ ║    email  12 TextTertiary, 1 line, char-ellipsis (omitted when null)
 ║ │   E77B  Account                      │ ║  HeaderSeparator h1, Margin(8,4,8,4), StrokeDividerDefault
 ║ │   E713  Settings                     │ ║
 ║ │   EC4F  Play                       › │ ║  MenuFlyout rows: h36, Margin(4,2,4,2), Pad(11,8,11,9), r4
 ║ │  ────────────────────────────────    │ ║    icon column 28 wide, glyph 16; label 14; chevron E974 @12
 ║ │   E706  Light theme                  │ ║    separator: Pad(0,1,0,1) + 1px line bled −4 each side  → 3 tall
 ║ │  ────────────────────────────────    │ ║
 ║ │   F3B1  Log out                      │ ║  glyph/label swap with the TARGET theme: Dark → Sun+“Light theme”
 ║ │                                      │ ║                                       Light → Moon+“Dark theme”
 ║ └──────────────────────────────────────┘ ║                                       ProfileMenu.cs:224-226
 ╚══════════════════════════════════════════╝
   304 + 2 border                              h = 2 + 6 + 76 + 9 + (5*40 + 2*3) + 6 + 2 ≈ 307
                                               (FIVE rows here — Account · Settings · Play · Theme · Log out)
```

*ch 19 W5 — Profile menu flyout @ 1440 (actions in the ROW, so no bell/friends rows)* — `19-shell-overlays.md:395-420`

- The chip's name caption is hard-capped at 76 DIP (`ProfileMenu.cs:38`, issue #88).
- Bell and Friends exist in exactly one place at a time: trailing-island buttons ≥ 1200 DIP, profile-menu rows
  below (`MergedChromeRow.cs:151-152`, `ProfileMenu.cs:213-219`); they arrive at 1240 on a widening window.
- The flyout takes MENU chrome (`PopupChrome.Flyout`, `ConstrainToRootBounds = false`) — the anchored 250 ms
  unfold over a windowed DWM transient-acrylic popup, not the 83 ms popup fade (`ProfileMenu.cs:169`).

### Notification panel (the bell) — flyout

`Shell/Shell.UI.cs` (panel + rows) over `Platform/Notify.cs` (policy, prefs, merge / filter / read-state, `AppUpdateToasts`)
+ `Platform/Notify.Host.cs` (WinRT half) — ONE stack by A9; `Entities/Notification.*` is NOT created · Wave 4 / owner I ·
chapter 19 (W15 the update row's Downloading / Installing, W16 the expanded activity card, W17 the empty ladder)

```
 ╔═══════════════════════════════════════════════════╗  380 wide · acrylic + 1px StrokeFlyoutDefault + r8
 ║ Notifications                     Mark all read   ║  header Pad(14,12,8,8), Gap 8
 ║                                                   ║   title 15/700 TextPrimary Grow 1
 ║ ( All ) (Updates) (Spotify) ( New ) (Activity)    ║   LinkButton 12/600 AccentTextPrimary, h28, r6, Pad(8,3,8,3)
 ║                                                   ║  pills Pad(12,2,12,8) Gap 6 · h26 r13 Pad(11,3,11,3)
 ╟───────────────────────────────────────────────────╢   selected: Fill AccentDefault + TextOnAccentPrimary
 ║ ┌───────────────────────────────────────────────┐ ║   rest:     Fill FillSubtleSecondary + TextSecondary
 ║ │ ╭────╮  An update is available            •   │ ║  scroller MaxHeight 460, AutoEdgeFade, content Pad(6,4,6,8)
 ║ │ │ ⬇  │  Wavee 0.3.0 “Crest” is available.     │ ║  card: MinHeight 56, Pad(10,8,10,8), r8, Gap 10
 ║ │ ╰────╯  [Update now] [What's new] [Later]     │ ║   GlyphChip 36x36 r18 FillSubtleSecondary, glyph 16
 ║ └───────────────────────────────────────────────┘ ║   title 13.5/700 1 line · body 12 TextSecondary wrap ≤3
 ║ ┌───────────────────────────────────────────────┐ ║   pill buttons h28 r14 Pad(12,4,12,4), 12/600
 ║ │  ( )   New Keenan Te show just announced  •   │ ║  social: 40 circle art (r20) + title 13 wrap ≤2
 ║ │        near you                               │ ║          + reltime 11/600 TextTertiary
 ║ │        2h ago                                 │ ║
 ║ └───────────────────────────────────────────────┘ ║
 ║ ┌───────────────────────────────────────────────┐ ║
 ║ │ ▭▭▭▭   Midnight Drive           [ SINGLE ]    │ ║  new release: 44x44 r5 art
 ║ │ ▭▭▭▭   Keenan Te                              │ ║   name 13.5/600 1 line · creator 12 TextSecondary 1 line
 ║ └───────────────────────────────────────────────┘ ║   TypePill Pad(9,2,9,2) r10 FillSubtleSecondary, Eyebrow
 ║ ┌───────────────────────────────────────────────┐ ║             12/16/600 + 30/1000em tracking, TextTertiary
 ║ │ ╭────╮  Added 3 songs to Late Night    12m    │ ║  activity: GlyphChip tint TextSecondary
 ║ │ │ +  │                              [Undo]   │ ║   summary 13 wrap ≤2 · right column Gap 4, AlignItems End
 ║ │ ╰────╯                                       │ ║   reltime 11/600 TextTertiary + Undo pill (non-accent)
 ║ └───────────────────────────────────────────────┘ ║
 ╚═══════════════════════════════════════════════════╝  panel MaxHeight 520
```

*ch 19 W14 — Notification panel, All filter, mixed rows @ any width (anchored to the bell)* — `19-shell-overlays.md:535-562`

- 380 wide, the feed scrolls at most 460, the panel caps at 520; header, filter pills and the pending-sync line are
  OUTSIDE the scroller (`NotificationPanel.cs:47, :85, :92`).
- Every row carries a trailing 8-DIP slot whether or not it is unread — text never reflows when a row is read
  (`:202-204`). Rows enter `Dy 6`, leave `Dy −4`, reorder by spring (`:196-198`).
- The update toast is one card for the whole lifecycle: `DedupeKey = "update"`, the download bar binds to
  `UpdateProgress` (`NotificationCenterBridge.cs:58, :237, :254`).

### Toast strip — overlay

`+Shell.Overlays.UI.cs` (decision sites) + `Platform/Controls.cs` (`Notify.Say`) over the engine `Toast` · Wave 4 / owner I
(+ L) · chapters 19 + 29 — no frame is copied here: ch 19 W19 draws the strip (bottom-right, 3 visible, MinW 300 / MaxW 380,
`FillSolidTertiary` r4 + `Elevation.Flyout`, newest nearest the dock, 8-DIP gap, 96 DIP above the window edge), W20 the
update download custom card, ch 29 W11 the toast card and its severity ramp

- A toast never covers the player bar: `Toast.EdgeInset = 72` on top of the engine's 24-DIP dock → 96 DIP bottom
  padding, bottom-right, max 3 visible, 8-DIP gap (`WaveeShell.cs:482`, `Toast.cs:82, :299, :318`).
- One failed play is one toast: `FailureToastKey = "wavee.play.failed"`; the FIRST sentence survives, the newer
  action is adopted (`PlayLinkActions.cs:115`, `Toast.cs:179-199`).
- One teaching tip at a time, once per launch, never again once acknowledged; it never scrims and never steals
  focus, scheduled through a double post so it rises over a painted page (`WaveeTipsCore.cs:94`, `WaveeTips.cs:30, :129`).
- The runtime banner (ch 19 W22) floats top-centred at `y = 56`, `MaxWidth 560`, an opaque `Tok.FillSolidBase`
  plate under the InfoBar's caution tint + `Elevation.Flyout`, `HitTestPassThrough` lane — it never reflows the
  page (`WaveeShell.cs:1396-1405`). Its `[Set up]` opens the 10-phase setup card (ch 19 W24-W33), whose body is
  `+Screens/Setup.UI.Runtime.cs` (A18): ONE command row always, buttons change with the phase, the 412-DIP bar is
  exactly the 460 dialog's content width (`PlaybackRuntimeSetupCard.cs:588, :938-994`).

### History page — `history`

`+Shell.History.UI.cs` (page) + `Shell/Shell.cs` (`Shell.History`: the log, kinds, filters, grouping) + `Shell/Shell.Host.cs`
(`history.json` / `play-log.json` / `play-recency.json`) · Wave 4 / owner I · chapter 16 (W18 Most visited, W19 the three
empty copies, W21 the Clear-all confirm)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ←36→                                                                                          ↑16          │
│  🕐  History                                              (12 visits) (5 unique)  [ Clear all ]            │  Clock 22 · PageHero 28/36/600
│                                                                                          ↓12 (Gap M)       │  StatPill ×2 · Button.Standard
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │
│  │ 🔍 Search history…                                                                                 │    │  AutoSuggestBox, minH 36, r4
│  └────────────────────────────────────────────────────────────────────────────────────────────────────┘    │
│   All   Playlists   Podcasts   Library   Search   Pages                        [ Most recent    ⌄ ]        │  SelectorBar ← → ComboBox w160
│   ▃▃▃                                                                                                      │  pill 16×3 (4×4 scaleX)
│                                                                                          ↓8               │
│ ╌╌╌ scroll region: pad (36, 12, 36, 24), Gap 16 ╌╌╌                                                        │
│  Today ────────────────────────────────────────────────────────────────────────────────  8 visits          │  Eyebrow 12/16/600 +30 tracking
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │  card r8 FillCardSecondary
│  │  ┌──┐                                                                                              │    │  border 1 StrokeCardDefault
│  │  │🎵│   Discover Weekly                                                        14:22          ✕    │    │  row h56, pad (12,0,8,0)
│  │  └──┘   Playlist · spotify:playlist:37i9…                                                          │    │  icon box 36×36 r4
│  │         ─────────────────────────────────────────────────────────────────────────────────────      │    │  divider 1, left margin 60
│  │  ┌──┐                                                                                              │    │
│  │  │🔍│   drake                                                                   13:58          ✕    │    │
│  │  └──┘   Search · drake                                                                              │    │
│  │         ─────────────────────────────────────────────────────────────────────────────────────      │    │
│  │  ┌──┐                                                                                              │    │
│  │  │♥ │   Liked Songs                                                             13:10          ✕    │    │
│  │  └──┘   Library                                                                                     │    │
│  └────────────────────────────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                          ↓16               │
│  Yesterday ────────────────────────────────────────────────────────────────────────────  4 visits          │
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐    │
│  │  ┌──┐   Blonde                                                        Yesterday, 23:41         ✕    │    │
│  │  │💿│   Page · album:spotify:album:3mH6…                                                            │    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 16 W17 — History, loaded, Most recent @ 880* — `16-recents-and-history.md:497-529`

- Card-grouped, not a flat list: eyebrow + rule + count-pill header over a rounded `FillCardSecondary` card with
  hairline dividers inset past the 36-DIP icon column, rows 56 DIP (`HistoryPage.cs:331-365, :483-504`).
- A dead route stays in the log at opacity 0.6, inert, with its delete button still live (`:414-418, :487-492`);
  nothing is ever removed to make room.
- The back/forward history flyout (ch 18 W16): right-click / touch-hold either button, up to 8 entries most recent
  first, "View all history" only when the stack exceeds 8; choosing a row calls `Go`, it does NOT pop the stack
  (`ShellToolbar.cs:54, :76-82`).

### File-drop target and cue — the whole window

`Shell/Shell.UI.cs` / `Shell.Host.cs` (`LocalFileActions`, `LocalPlayables` carried over unchanged) · Wave 4 / owner I ·
chapter 29 (ch 15 W27 and ch 18 W19 show the same cue over a library page and the shell)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋      [ 🔎 Search... ]           ◉ Christos  🔔 👥 📌 ⚙│  ⌄  ─ □ ✕   │ ← FULLY LIT
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│  sidebar     │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│  FULLY LIT   │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒┌──────────────────────────────┐▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│   Drop a file to play it     │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒└──────────────────────────────┘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤ 72
│  ◀◀  ▶  ▶▶     Weird Fishes · Radiohead      ━━━━●────────      🔊 ━━━━●──   📺 ♥ ⋯               │ ← FULLY LIT
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ▒ = the ENGINE's spotlight scrim, clipped to the content region's absolute rect, which the shell
      publishes on realize and on every re-arrange:  sc.SpotlightScrimClip = AbsoluteRect(contentRegion)
      (WaveeShell.cs:414-421, wired at :1341-1342)

  the PILL:  Padding 18/10, Corners Radii.Control, Fill Tok.FillSolidBase, Border 1 · Tok.AccentDefault,
             Shadow Elevation.Dialog, text 14 Tok.TextPrimary, Strings.LocalFile.DropHint  (:1425-1430)
  the LAYER: Grow=1, HitTestPassThrough, centred both axes,
             Opacity = Prop.Of(() => _fileDropOver.Value ? 1 : 0)  ← BOUND: compositor-only  (:1418-1432)
  the TARGET: DropTargetSpec([DropKinds.Files]) on the shell ROOT, so the whole window accepts;
              OnDrop → LocalFileActions.PlayDropped(paths)  (:1351-1359)
```

*ch 29 W21 — the OS-file drop cue and its scrim scope @ 1600×900 (1 char ≈ 16 DIP)* — `29-cross-cutting.md:1482-1505`

- The engine's spotlight scrim is clipped to the content region's absolute rect, published on realize and every
  re-arrange (`WaveeShell.cs:414-421, :1341-1342`); chrome, sidebar and player bar stay FULLY LIT.
- The layer's opacity is a BOUND `Prop.Of(() => _fileDropOver.Value ? 1 : 0)` — compositor-only (`:1418-1432`);
  the target is `DropTargetSpec([DropKinds.Files])` on the shell ROOT (`:1351-1359`).

### Not-found page, network chrome, first-run composite — fall-through / chrome / first launch

`Shell/Shell.UI.cs` (not-found: ch 18 W21 — a centred column, destination glyph 40 `TextTertiary`, `PageHero` "Page not
found", `Button.Standard` "Go home", `ContentHost.cs:290-309`) · `Platform/Platform.cs` + one `NetworkChrome` section in
`Shell.UI.cs` (ch 29 W9: offline chip with an ACCENT `Reconnect` button, the bar's `Reconnecting` title + indeterminate
top edge, and the page's own error state — three layers legible together, `MergedChromeRow.cs:216-219`,
`PlayerBar.cs:171, :627, :736`) · `Shell/Shell.cs` + `Screens/Setup.cs` (first run, ch 29 W24 — shown under Wave 6 below).
Wave 4 / owner I (+ L). No further frame is copied here; open ch 18 W21 and ch 29 W9.

---

## 4. Wave 4 — the sidebar (owner J)

Files: `Shell/Sidebar.cs` (CORE 5,000), `Sidebar.Doc.cs` (4,000), `Sidebar.UI.cs` (7,500), `Sidebar.Customizer.UI.cs`
(4,500, **sequenced last**), `Sidebar.Host.cs` (2,500) — 23,500 by A4. Chapters 25 (25 frames) and 26 (22 frames).
Three designs, ONE renderer — all three are shown because that is the point of the review.

### Sidebar pane, Classic — all routes

`Shell/Sidebar.UI.cs` · Wave 4 / owner J · chapter 25

```
 x=0   8  12 15 21           53 59                                264 272  280
 │     │   │  │  │            │  │                                  │   │    │
 ├─────┴───┴──┴──┴────────────┴──┴──────────────────────────────────┴───┴────┤  ← PanePad.Top 8
 │  ▌ [⌂] Home                                                               │  44  the materialised Shortcuts band:
 │    [🔍] Search                                                            │  44  NO header row is planned for it
 │                                                                           │   8 section gap                (SidebarRowPlanner.cs:322)
 │    Pinned                                                       ⧉  ⌄      │  28  ⧉ = layout-menu button (24) — it hangs off
 │                                                                           │      the pane's FIRST SectionHeader row
 │                                                                           │   2
 │    [▩32] Chill Mix                                             ♪ 📌       │  44  art 32 · title 14/Body · sub 12/Caption
 │          50 songs                                                         │      trailing: equalizer 12 (playing) + pin 12
 │    [◯32] Hans Zimmer                                                      │  44  circular art (artist)
 │          Artist                                                           │
 │  ────────────────────────────────────────────────────────────             │  16 explicit Divider (hairline x 12→264)
 │    Your Library                                                 ⌄         │  28 (+8 band-top suppressed after Divider)
 │                                                                           │   2
 │    [▤] Albums                                                    64       │  40  glyph row (Subtitles:false ⇒ 40)
 │    [👤] Artists                                                  93       │  40  DOCUMENT ORDER, exactly:
 │    [♥] Liked Songs                                              128       │  40  albums · artists · liked · podcasts
 │    [📻] Podcasts                                                  7       │  40  · local  (SidebarBuiltInDocuments.cs:93-97)
 │    [📁] Local files                                                       │  40  local carries no count
 │  ────────────────────────────────────────────────────────────             │  16
 │    Playlists                                                    +  ⌄      │  28  + = SidebarCreateButton (24)
 │                                                                           │   2
 │    [📂32] Road trip                                           ⌄     +     │  44  FolderHeader — the disclosure chevron is
 │          3 items                                                          │      TRAILING, then (CountBadges only) a count,
 │    │  [▩32] Desert                                                        │  44  then the folder's own "+" (20 box, glyph 12)
 │    │  │     12 songs                                                      │      depth 1: one 12-DIP connector cell
 │    └─ [▩32] Coast                                                         │  44  elbow + half-height guide
 │          31 songs                                                         │
 │    [▩32] Deep Focus                                                       │  44
 │          88 songs                                                         │
 │  (24-DIP TreeEnd gutter — invisible, owns the "top level, at the end" drop slot)
 ├───────────────────────────────────────────────────────────────────────────┤  ← PanePad.Bottom 12
```

*ch 25 W1 — Classic, docked, fully loaded @280 (the MID tier — viewport 1400–1799; < 1400 is the 240 narrow tier)* — `25-sidebar.md:192-227`

### Sidebar pane, Wavee Curated — all routes

`Shell/Sidebar.UI.cs` · Wave 4 / owner J · chapter 25 (W8 authored-empty, W9/W10 the customize canvas at `sidebar-customize`)

```
 │  ▌ [⌂] Home                                                     │  44   (the Shortcuts band plans no header row)
 │                                                                 │   8
 │    Pinned                                               ⧉  ⌄    │  28
 │    [▩32] Chill Mix                                        📌    │  44
 │          50 songs                                               │
 │  ────────────────────────────────────────────────────           │  16   authored Divider
 │    Jump back in                                         ⌄       │  28
 │  ┌──────────────┐ ┌──────────────┐                              │       GridStrip: 2 cols @ 320
 │  │ ▩▩▩▩▩▩▩▩ 140│ │ ▩▩▩▩▩▩▩▩ 140│                              │       cell edge = min(160,(304−8)/2)=148
 │  │ ▩▩▩▩▩▩▩▩    │ │ ▩▩▩▩▩▩▩▩    │                              │ ~186  art = edge − 8 = 140
 │  │ Deep Focus   │ │ Road trip    │                              │       label 12/1 line, sub 11 tertiary
 │  └──────────────┘ └──────────────┘                              │       card: Radii.Card, Elevation.Card, pad 4, gap 4
 │    New releases                                         ⌄       │  28
 │    [▩32] Ekhidna                                        2d      │  44   age badge 11 tertiary
 │          Album · Ryuichi Sakamoto                               │
 │    Concerts near you                                    ⌄       │  28
 │  ┌───────────────────────────────────────────────────┐          │  48   PromptRow (no reason line)
 │  │ [📅28]  Set your location to see concerts      ›   │          │       12/600 + ChevronRight 10, Elevation.Card
 │  └───────────────────────────────────────────────────┘          │
 │    Playlists                                            +  ⌄    │  28
 │    … tree …                                                     │
```

*ch 25 W7 — Wavee Curated, loaded @320* — `25-sidebar.md:380-402`

### Sidebar pane, Library V3 — all routes

`Shell/Sidebar.UI.cs` · Wave 4 / owner J · chapter 25 (W12 the 240 narrow tier, W13 grid view, W22 the destination word rail scrolled)

```
 │ x=0  8   13    21                                      284 292 300
 ├──────────────────────────────────────────────────────────────────┤  chrome padding (0,8,0,0)
 │   Liked Songs 128   Albums   Artists   Podcasts   Local fil⋯     │  30  DestinationRail: 13.5 px words, gap 14,
 │   ▔▔▔▔▔▔▔▔▔▔▔                                          ◄fade►   │      2-DIP accent underline on the active word,
 │                                                                  │      count on the ACTIVE word only, 20-DIP edge fade
 │  ▌ [⌂] Home                                                      │  40  nav rows (prefs.TopBar)
 │  ──────────────────────────────────────────────────────────      │   1 + 4/4 margins
 │   [▤32] Your Library                        [+28] [⋯28] [‹28]    │  44  title 15/600; buttons ControlSize.Small
 │   [🔍  Search in Your Library          ✕ ]        Recents ⇅│≡     │  36  inline field (pane ≥ 300) + full sort pill
 │   (Playlists) (Podcasts) (Albums) (Artists)                      │  40  28-DIP pills, gap 6, horizontal scroll
 │  ──────────────────────────────────────────────────────────      │   1 + 4/4
 ├──────────────────────────────────────────────────────────────────┤  ← the padded list starts here (PanePad 8/8/8/12)
 │    [▩32] Chill Mix                                    📌         │  44  Cozy+subtitles = 44 (View = List)
 │          50 songs                                                │
 │    [📂32] Road trip                                        ⌄     │  44  folder (wide pane ⇒ inline disclosure);
 │          3 items                                                 │      chevron is TRAILING, never leading
 │    [◯32] Hans Zimmer                                             │  44
 │          Artist                                                  │
```

*ch 25 W11 — Library V3, list view @300 (inline search)* — `25-sidebar.md:462-481`

### Collapsed rail (56 DIP), folder flyout, drag cues — all routes

`Shell/Sidebar.UI.cs` · Wave 4 / owner J · chapter 25 (W6 the 300-DIP folder flyout, W16 the five tree drag outcomes, W17
multi-selection, W24 the "Move to folder…" `ContentDialog` at 320)

```
 │  56  │
 ├──────┤  ← padding (0,8,0,12), AlignItems=Center, Gap 6
 │ ╭──╮ │  40×40 tile, corners 6 (Icon) — Home
 │ │⌂ │ │  glyph 16, TextSecondary; SELECTED tile: fill SelectedRest + glyph TextPrimary
 │ ╰──╯ │
 │ ╭──╮ │  40×40 — Search
 │ │🔍│ │
 │ ╰──╯ │
 │ ──── │  24×1 divider, TextTertiary @ A=0.30, margin 4/4
 │ ┌──┐ │  40×40 tile, corners 8 (Art) — pinned playlist cover, art edge 36 inset 2
 │ │▩ │ │  SELECTED: 2-DIP AccentDefault ring; armed drop: same ring + accent@0.35 wash over the cover
 │ └──┘ │
 │ ┌──┐ │
 │ │◯ │ │  artist → circular art
 │ └──┘ │
 │ ──── │
 │ ╭──╮ │  library shortcuts (glyph tiles), then up to 20 playlist-tree tiles (depth 0 only)
 │ │♥ │ │
 │ ╰──╯ │
 │ ╭──╮ │  folder tile → opens the side flyout (W6); tooltip = "Name · N items"
 │ │📁│ │
 │ ╰──╯ │
 │ ──── │
 │ ╭──╮ │  RailFooter: Classic's create "+" (box 40, glyph 16)
 │ │+ │ │
 │ ╰──╯ │
 │ ──── │
 │ ╭──╮ │  quick layout menu (box 40)
 │ │⧉ │ │
 │ ╰──╯ │
```

*ch 25 W5 — Rail (collapsed) @56 — Classic* — `25-sidebar.md:308-339`

What must not be lost (ch 25 §0):

- One renderer, three documents: Classic, Library V3 and Curated are a `SidebarCustomLayout` + `SidebarPaneConfig`
  over ONE `SidebarPane` (`Pane/SidebarPane.cs:52`); an `if (design == …)` inside the row or rail builder is the
  four-left-edges bug returning.
- One inset owner: `PanePad = (8,8,8,12)` applied once; every row's art/glyph column starts at pane x = **21**
  (`Data/SidebarRowGeometry.cs:114`) — verify with a ruler on a screenshot.
- One height per SECTION, never per row: Compact 32 · Cozy 40/44 · Comfortable 44/48 (`SidebarRowGeometry.cs:57-62`);
  one flat `SidebarRow[]` of 15 kinds through one `ItemsView.CreateBound` so a 10k rootlist virtualizes.
- Selection is the 3×16 pill with the WinUI NavigationView 600 ms paired flight; its opacity is a BOUND read of
  `SidebarPillState` or two pills light at once (`Shared/SidebarSelectionPill.cs:28-29`, `Data/SidebarPillState.cs:39`).
  A selected row must DARKEN on hover (`WaveeTokens.cs:272-274`).
- Both layers always mounted — expanded (measured at the persisted OPEN width) and the 56-DIP rail — cross-faded by
  opacity + `HitTestVisible` (`Pane/SidebarPane.cs:523-596`); the rail is a real surface: 40-DIP tiles, a folder tile
  opening a 300-DIP flyout, playlist tiles accepting deposits, a 250 ms drag-peek dwell (`:2440-2460`).
- Nothing ever vanishes: a missing entity, an unresolvable action, a lost folder pin render at 0.55 with a reason
  tooltip and a one-verb menu (`Pane/SidebarPaneSlot.cs:554, :703, :769`); counts are 11 px tertiary or a 20×12
  shimmer, never Classic's accent `InfoBadge`.

### Sidebar customizer — `sidebar-customize`

`Shell/Sidebar.Customizer.UI.cs` (sequenced LAST in the wave) + `Sidebar.Doc.cs` + `Sidebar.Host.cs` · Wave 4 / owner J ·
chapter 26 (W2 the same page at 640 — no breakpoint; W11-W14 the template confirm, item picker and action picker;
W15 the per-section options popover, reused by the pane's "…")

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ h=64  pad L8 R16  gap 8  align center                                                                             │
│ ┌──┐  ┌────────────────────────────────────────────────┐                    ● Saved locally  ┌──┐┌──┐┌─────┐┌────┐│
│ │ ← │  │ WAVEE CURATED            ← Eyebrow 12/16/600   │                    6px  11f tert   │↶ ││↷ ││Reset││Done││
│ └──┘  │ Customize sidebar        ← 16f/600 TextPrimary │                                     └──┘└──┘└─────┘└────┘│
│ 24×24 └────────────────────────────────────────────────┘                      Small: h≥24, 12f, r4             Accent│
├───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│                                                                                                                   │
│ ←16→┌──────────────────────────────────────────────────────────────────────────────────────┐  ← MaxWidth 720      │
│ ↑12 │ SIDEBAR DESIGN                                        ← GroupLabel 12/16/600 tert    │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │  card r4             │
│     │ │ Customizing edits the Custom design, so opening this page   ← Wide: label above  │ │  FillCardSecondary   │
│     │ │ switched to it. Pick another to switch back…       13f, 2 lines max               │ │  1px StrokeCardDef   │
│     │ │  ┌──────────────┬──────────────┬──────────────┐   STOCK Segmented h34/14f/min52  │ │  pad 12/10/12/10     │
│     │ │  │   Classic    │   Library    │  ▣ Custom    │   accent pill 24×3 VISIBLE under │ │  MinHeight 44        │
│     │ │  └──────────────┴───────────────────▁▁▁▁──────┘   the selected segment (:775)    │ │                      │
│     │ ├──────────────────────────────────────────────────────────────────────────────────┤ │                      │
│     │ │ Show section contents                                              (  ●)  Toggle  │ │  Prop row           │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ START FROM A TEMPLATE                                                                 │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ ◉  Wavee Curated                                                              ✓  │ │ active: Fill=       │
│     │ │    Pinned, Jump back in, library shortcuts and folder-aware playlists.            │ │ FillSubtleSecondary │
│     │ │ ▦  Classic-inspired                                                               │ │ pad 12/8/12/8, gap 8│
│     │ │    The familiar Wavee sidebar, now editable.                                      │ │ 13f/600 + 11f tert  │
│     │ │ ▦  Library-inspired                                                               │ │ radio glyph 14f     │
│     │ │    One filterable library list with pins on top.                                  │ │ inactive tail 16w   │
│     │ │ ▦  Minimal   · Three shortcuts and a compact playlist list.                        │ │                      │
│     │ │ ▦  Blank     · Start from nothing and add only what you want.                      │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ ADD A SECTION                                                            12/40       │  gap 4 between head, │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │  box and card        │
│     │ │ Search sections                                                           h 30    │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │ card r4, pad 4       │
│     │ │  PAGES                                            ← group header, margin 4/8/4/2 │ │                      │
│     │ │  ┌──┐ Home                                                                  ┌─┐  │ │ row pad 4/6/4/6      │
│     │ │  │⌂ │ Add a shortcut to this page                                           │+│  │ │ gap 8, r4            │
│     │ │  └──┘                                                                       └─┘  │ │ 24×24 plate / chip   │
│     │ │  ┌──┐ Search                                                                ┌─┐  │ │ chip Opacity .45     │
│     │ │  │⌕ │ Add a shortcut to this page                                           │+│  │ │                      │
│     │ │  └──┘                                                                       └─┘  │ │                      │
│     │ │  … Albums · Artists · Liked Songs · Podcasts · Local files · History · Recents    │ │ 9 pinnable +         │
│     │ │  … Settings · Concerts                     (API console only in developer mode)  │ │ 3 extra = 12 rows    │
│     │ │  NAVIGATION                                                                       │ │                      │
│     │ │  ┌──┐ Pinned            The items you pinned, in your order              ┌─┐      │ │                      │
│     │ │  │📌│                                                                    │+│      │ │                      │
│     │ │  └──┘                                                                    └─┘      │ │                      │
│     │ │  … Library shortcuts · Liked Songs · Links                                        │ │                      │
│     │ │  LIBRARY      Playlists · Library list · Spotlight                                 │ │                      │
│     │ │  PLAYBACK     Recently played · Queue · Now Playing                                │ │                      │
│     │ │  DYNAMIC      Jump back in · Artist top tracks · New releases · Concerts near you  │ │                      │
│     │ │  LAYOUT       Group · Heading · Divider                                            │ │                      │
│     │ │  ACTIONS      Action shortcut                                                      │ │                      │
│     │ │  EXTENSIONS   Extension                                                            │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↕16 │ HIDDEN SECTIONS                                       Hiding never removes anything  │  group caption 11f   │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ Nothing is hidden.                                              (enabled=false,   │ │ Opacity .4           │
│     │ └──────────────────────────────────────────────────── Opacity 0.4, IsEnabled=false)─┘ │                      │
│ ↕16 │ ADVANCED                                                                              │                      │
│     │ ┌──────────────────────────────────────────────────────────────────────────────────┐ │                      │
│     │ │ Reset to template                                                     [ Reset ]  │ │ Standard · Small     │
│     │ │ This restores "Wavee Curated" and discards your changes. You can undo it.         │ │ sub 11f tertiary     │
│     │ ├──────────────────────────────────────────────────────────────────────────────────┤ │                      │
│     │ │ Layout file                                                                       │ │ Wide row             │
│     │ │ Your sidebar is saved here. Editing it by hand is unsupported…                    │ │                      │
│     │ │ C:\Users\…\AppData\Local\Wavee\WaveeMusic\sidebar-layout.json    11f tert, 2 lines │ │ gap 8 below label    │
│     │ │ [ Show in Explorer ]  [ Copy path ]                        Standard · Subtle       │ │                      │
│     │ └──────────────────────────────────────────────────────────────────────────────────┘ │                      │
│ ↓20 └──────────────────────────────────────────────────────────────────────────────────────┘                      │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 26 W1 — fully loaded, healthy, nothing selected @ 1280 window (content host ≈ 976 DIP)* — `26-sidebar-customizer-and-pipeline.md:353-426`

- One scrolling column at every width, left-aligned, capped at 720 (`SidebarCustomizerPage.cs:50, :383-398`); the
  old four-tier ladder that hid Reset and the preview under ~1600 DIP must not return.
- The live docked sidebar IS the canvas and IS the preview: no copy, no apply step, no dirty state; every edit
  lands in the pane in the SAME frame (`:36-38`).
- Every rejection says something inline for 4 s (`RejectLocKey` over all 13 `SidebarRejectReason` members + 5
  overrides, `:439-460, :170`), and a rejected edit visibly snaps the control back
  (`CzRow.Epoch = LayoutVersion*397 + RejectEpoch`, `SidebarCustomizerControls.cs:247-248`). 0.3 should give the
  pane's popover the same sentence rather than inherit its silence.
- The section budget `{used}/{max}` is always on screen, `SystemFillCritical` at 40/40 (`SidebarLayoutReducer.cs:14`);
  the Hidden group offers one Show per hidden section, kinds this build does not understand included (`:816-857`).
- A bound action row is VISIBLE-BUT-DISABLED with one of exactly 7 reason sentences, never absent; the registry
  NEVER unregisters (`WaveeActionTargeting.cs:36-38`, `WaveeRegistryTable.cs:121-157`). `BuiltInExtensionTable`'s
  exclusions are a design decision — port the comment with the table (`BuiltInExtensionTable.cs:16-30`).

---

## 5. Wave 4 — right rail, now-playing, queue, stage, lyrics, decks, video (owner K)

Files: `Shell/Rail.cs`, `Rail.UI.cs`, `+Rail.Styles.UI.cs`, `Stage.cs`, `Stage.UI.cs` (A12), `Deck.cs`, `Deck.UI.cs`,
`+Deck.Faces.cs`, `Lyrics.cs`, `Lyrics.UI.cs`, `Lyrics.Host.cs`, `Video.cs`, `Video.UI.cs`, `Video.Host.cs` (A13),
`+Screens/Settings.UI.Video.cs`. `Entities/Queue.cs` + `Queue.UI.cs` are owner Q's in Wave 5 over K's stage skin.
Chapters 21, 22, 23, 24.

### Right rail, docked, Details / Cover — rail

`Shell/Rail.UI.cs` · Wave 4 / owner K · chapter 21 (17 frames; W2 the Player deck in the same slot, W7 Video mode,
W7b the 200 · 340 · 500 width ladder, W9 the player-style flyout, W10 the artwork context menu)

```
 ← 8 gap →┌─────────────────────────────────────────┐  ← 1-DIP StrokeCardDefault, LEFT+TOP only
          │ ▒▒▒▒ this corner only: r8 ▒▒▒           │     surface Fill = Transparent (docked)
          │ 8│                                    │8│     no shadow docked
          │  │Now playing  [▣ Cover│◉ Record]  ⚙  │ │ ← NpvHeaderRow  h36  gap 4
          │  │12/16/600 +30tr       pill 30       │ │    gear 32² r4 glyph16, tip "Player style"
          │  │↑ SENTENCE CASE. WaveeType.Eyebrow  │ │    (Loc player.nowPlaying = "Now playing";
          │  │  never .ToUpper()s a loc string.   │ │     NpvHeaderRow.cs:84-91, :75)
          │ 8│                                    │ │ ← Gap 8  (ArtTop = 8+36+8 = 52)
          │  ┌────────────────────────────────────┐ │
          │  │                          ┌────────┐│ │ ← ArtVideoToggle h24 r4
          │  │                          │ ▣ │ ▶  ││ │    Fill GlassHover; sel half @0.16
          │  │                          └────────┘│ │    Margin(0, 56, 12, 0)
          │  │            324 × 324               │ │
          │  │          cover art  r8             │ │ ← HeroArt: Cover fit, aspect 1,
          │  │       Placeholder = HeroWash       │ │    DecodePx 512, BlurHash
          │  │                                    │ │
          │  └────────────────────────────────────┘ │
          ├─ ─ ─ ─ ─ ─ ─ ─ pinned ends ─ ─ ─ ─ ─ ─ ─┤ ← everything below is inside ScrollView
          │ 8│ Pad top 12                        │8│    { Grow 1, AutoEdgeFade }
          │  │Track title wraps to three         │ │ ← Subtitle 20/28/600, MaxLines 3
          │  │lines maximum, then ellipsis…  ┌──┐│ │    SaveButton box 36, glyph 16
          │  │                               │ ♡ ││ │    Gap between column and button: 12
          │  │Artist One, Artist Two         └──┘│ │ ← 14/20  TextSecondary  (links)
          │  │Album Name                          │ │ ← 12/16  TextTertiary  (link)   gap 2
          │  │                                    │ │ ← Gap 12
          │  │▌ The line that is being sung now   │ │ ← spine w3 r1.5 AccentDefault
          │  │▌                                   │ │    active slot h56, Opacity 1
          │  │▌ and the next one, faded           │ │    peek slot   h56, Opacity 0.38
          │  │ Pad bottom 16                      │ │
          │ 12│ sections: Gap 16, Pad(12,0,12,12) │12    ← note: sections take 12, hero takes 8
          │  │About the artist    20/28/600       │ │
          │  ┌────────────────────────────────────┐ │ ← card r8, Fill FillCardSecondary,
          │  │▓▓▓▓▓▓▓ artist header 132 ▓▓▓▓▓▓▓▓▓▓│ │    1-DIP StrokeCardDefault
          │  │▓▓ EdgeFade bottom 56 + scrim ▓▓▓▓▓▓│ │    image aspect 2.6, decode 320
          │  ├────────────────────────────────────┤ │
          │  │ Artist Name              ✓         │ │ ← 18/24/600 + Check 12 AccentText
          │  │ ┌────────┐┌────────┐┌────────┐     │ │ ← Fact pills r4 Pad(8,4,8,4) Gap 8
          │  │ │12,345,6││  1,234 ││   #42  │     │ │    value 12/16/600, label 12/16 T3
          │  │ │monthly ││followers││# in the│     │ │    the rank LABEL is literally
          │  │ └────────┘└────────┘└─world──┘     │ │    Strings.Artist.WorldRank("").Trim()
          │  │ Bio text, 14/20 secondary, five    │ │    = "# in the world" (§9.3 #9)
          │  │ lines maximum then ellipsis…       │ │    each pill renders only when its
          │  │ [ Follow ]                         │ │    value > 0; the row Wraps
          │  └────────────────────────────────────┘ │ ← FollowButton, Key "follow:"+artistUri
          │     no header image ⇒ the 132 band is   │    (NowPlayingPanel.cs:287)
          │     a 72-DIP FillSubtleSecondary strip  │    :261-268
          │     holding a 56-DIP PersonPicture      │
          │  │Listened to most in 20/28/600       │ │ ← Loc artist.listenedMostIn, verbatim
          │  │ Los Angeles, US            412,203 │ │ ← 12/16/600 T1  ·  12/16 T3
          │  │ ████████████████████░░░░░░░░░░░░░░ │ │ ← bar h4 r2; fill 260·frac AccentDefault
          │  │ London, UK                 298,110 │ │    frac = clamp(n/max, 0.08, 1)
          │  │ ████████████████░░░░░░░░░░░░░░░░░░ │ │    max 5 cities
          │  │Credits            20/28/600        │ │
          │  │PERFORMED BY        eyebrow 12/16/600│ │ ← group key = RoleGroup, FALLING BACK to
          │  │ Name (link, accent)      Vocals    │ │    Role when RoleGroup is blank (:342);
          │  │ Source: Label Ltd.                 │ │    server's own casing, never .ToUpper()
          │  │Next up             20/28/600       │ │ ← 12/16 T3, MaxLines 2
          │  │ [▣] Track  · Artist                │ │ ← TrackRow.ArtCard(Rail) art 40, gap 2
          │  │ [▣] Track  · Artist                │ │    max 5, no duration, no explicit badge,
          └──┴────────────────────────────────────┴─┘    ColumnSet Video:true (the video glyph
                                                          column IS on), each row in a r4 wrapper
                                                          with HoverFill FillSubtleSecondary
                                                          (:37, :420-434)
```

*ch 21 W1 — Rail, docked, Details / Cover, fully loaded @ 340* — `21-right-rail-npv-queue-stage.md:283-347`

### Queue panel (rail arm) — rail

`Entities/Queue.UI.cs` + `Queue.cs` (CORE: `QueueSlots`, `QueueMovePlan`, `QueueOrder`) · Wave 5 / owner Q, over K's rail ·
chapter 21 (W4 empty, W5 row hover + drag, W12 the stage's queue pane)

```
          ┌─────────────────────────────────────────┐
          │Queue                                ✕  │ ← header h44, Pad(12,0,8,0)
          │20/28/600, NoWrap + ellipsis   32² r4    │    title = Loc player.queue here; per mode:
          ├─────────────────────────────────────────┤    Lyrics / Queue / Friend Activity / Video /
          │                                         │    Now playing (RightRail.Title, :436-443)
          │                                         │    (no separator line — this is a gap)
          │14│ Pad top 4                        │14│
          │ ┌──────┐ ┌──────┐ ┌──────────┐        │ ← Pills row Gap 8, Pad(0,4,0,10)
          │ │⤨ Shuf│ │⟳ Repe│ │ ∞ Autopla│        │    each h32 r16 Pad(12,0,12,0) gap 6
          │ └──────┘ └──────┘ └──────────┘        │    glyph 12 / label 12/16/600
          │  off: FillCardSecondary   on: accent   │    on → TextOnAccentPrimary
          │                                        │
          │ Playing from Daily Mix 3            ›  │ ← h28 r4 Pad(8,2,8,8) hover RowHover
          │ 12/16  T2 + T1@700              chev12 │
          │ ┌────────────────────────────────────┐ │ ← NowPlayingCard r8 FillCardDefault
          │ │ ┌────┐              Track title    │ │    1-DIP StrokeCardDefault, Pad 10
          │ │ │ ▣▶ │   gap 16     Artist links  ♡│ │    MinHeight 64, Margin bottom 10
          │ │ │ 44 │              14/20/600 ACC  │ │    art 44 r4 + centred 28 FAB
          │ │ └────┘              12/16 T2   30w │ │
          │ └────────────────────────────────────┘ │
          │ NEXT IN QUEUE   3            Clear     │ ← header slot h36  Pad(8,12,8,4)
          │ eyebrow T3      12/16/600 T3  pill r4  │    eyebrow row MinHeight 20
          │ ♡ [▣]  Song title             ⋯   ✕   │ ← row slot h44  Pad(8,0,4,0) gap 8
          │ 26  34  14/20/600 T1         32²  32² │    heart slot 26 · art 34 r4
          │        12/16 T2 artist links           │    ⋯ glyph Icons.More 14, Opacity 0 →
          │                                        │      HoverOpacity 1, HoverFill RowPressed
          │                                        │    ✕ glyph ChromeClose 12, HoverFill
          │                                        │      RowPressed — **ALWAYS PAINTED**, it
          │                                        │      carries NO hover-opacity on the RAIL
          │                                        │      (:661 vs :677-689). The STAGE's ✕ is
          │                                        │      the hover-revealed one (W12).
          │                                        │    both TextTertiary → TextPrimary
          │ ♡ [▣]  Song title                     │
          │ ♡ [▣]  Song title                     │
          │ NEXT UP        12                      │ ← no Clear on Next up
          │ ♡ [▣]  Song title                     │
          │      …                                 │
          │ ┌────────────────────────────────────┐ │ ← ShowMore h40 r4 FillCardSecondary
          │ │  ⌄  Show next 100  ·  63 more      │ │    Margin(0,4,0,2), Gap 8
          │ └────────────────────────────────────┘ │    12/16/600 T1 + 12/16 T3
          │ AUTOPLAY       8                       │ ← header slot h54 (has a hint line)
          │ Similar music will keep playing        │ ← 12/16 T3
          │ ♡ [▣]  Song title            Opacity   │ ← autoplay rows: whole row Opacity 0.72
          │ ♡ [▣]  Song title              0.72    │
          │14│ Pad bottom 14                   │14│
          └─────────────────────────────────────────┘
```

*ch 21 W3 — Rail, docked, Queue, loaded @ 340* — `21-right-rail-npv-queue-stage.md:362-409`

### Friends panel — rail

`Shell/Rail.UI.cs` (the panel) · the friends **edge and columns** land with owner B in Wave 1 (plan §9.6 Q5 — the split is
not yet confirmed) · Wave 4 / owner K · chapter 21 — no frame is copied here: ch 21 W6 draws rows / skeleton / offline side
by side (40 circle avatar, h56, a 12-DIP accent presence dot cut into the avatar and a live equalizer for a friend ≤ 120 s
old, five shimmer rows, and the centred "No friend activity yet" empty state with `[ Retry ]` on error)

What must not be lost (ch 21 §0):

- The rail is ONE rung with the page, not a card on it: docked, `RightRail` paints `Transparent` and the shell's
  reservation band paints the single `FileArea` coat with ONE left+top hairline drawn topmost; no shadow docked;
  floating flips to its own `FileArea`, a uniform ring and `Elevation.Flyout` (`RightRail.cs:96-110, :150-154`).
- Open/close is ONE translate track, 300 ms, no opacity (`RightRail.cs:73-78`); a fading full-height panel is the
  "ghost rail" defect.
- The Details hero is PINNED above the scroller (`Shrink = 0`) because a composited video renders BLACK inside an
  offscreen layer (`NowPlayingPanel.cs:24-29, :486-505`); the video is the same card at the same width in every
  rail body (`RightRail.DockedCap`, `:218-257`).
- The queue's current track is a pinned bordered CARD, never a row; no history section — `Queue → Next up →
  Autoplay` (`QueuePanel.cs:18-30, :361-425`); every slot is a plain keyed child with Enter/Exit/Slide so a consumed
  row exits and the rest slide (`:693-716`); ONE `Reorderable` spans headers and rows, a cross-section drop is a
  told refusal (`QueueMovePlan.cs:93-132`).
- The player-style flyout STAYS OPEN across picks (`PlayerStyleFlyout.cs:17-19`); a friend ≤ 120 s old shows a
  12-DIP presence dot and a live equalizer, advanced by a 30 s frame-clock tick (`FriendsPanel.cs:23-25, :135-138`).

### Immersive stage, wide and compact — overlay

`Shell/Stage.cs` (CORE: `StageLayout`, `StageArm`, `StageInk`, `StagePane`) + `Shell/Stage.UI.cs` (`StageChrome`, `StageIdentity`,
`StagePanes`) · the lyrics pane's BODY is `Lyrics.UI.cs` (A12) · Wave 4 / owner K · chapter 21 (W12 the queue pane, W14 the
height-folded ladder at 680 / 640, W16 document absent / pending)

```
 ┌──────────────────────────────────────────────────────────────────────────────────────────┐
 │  (48 DIP window caption strip — reserved, pass-through)                                   │
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │ scrim 0.76 at y=0 …………… feathers to the 0.46 plateau at 22% of the body ………              │
 │                                                                         ┌──┐  ┌───┐       │ ← TopBar h88
 │   column shade: 0.26 held to x=352, feathered to EXACTLY 0 at x=612     │🌐│  │ ⌄ │       │   Pad(16,16,16,12)
 │                                                                         └──┘  └───┘       │   ScrimFab 40 · ExitFab 44
 │  ┌────────────────────┐              ← Gap 56 →                                           │
 │  │                    │   Lyric line one, left-anchored, max 700 wide                      │
 │  │                    │                                                                    │
 │  │   cover 300 × 300  │   Lyric line two — the sung one                                    │
 │  │   r8               │                                                                    │
 │  │   Elevation.Dialog │   Lyric line three (blurred by distance — Ch.22)                   │
 │  │                    │                                                                    │
 │  └────────────────────┘                                                                    │
 │   ↕ 18                                                                                     │
 │   Track Title              22/28/650 Display face, Ink                                     │
 │   Artist — Album  ♡  ⋯     14/20/400 InkSecondary (underlines on hover) · 32² · 32²        │
 │   ↕ 18                                                                                     │
 │   ▬▬▬▬▬▬●───────────────   SeekBar (Ch.20), full 304 wide                                  │
 │   0:47        FLAC    −2:13   12 InkTertiary  ·  11.5/16/600 InkTertiary  ·  12            │
 │   ↕ 8                                                                                      │
 │      ⤨    ◀◀   ( ▶ )   ▶▶   ⟳      32 · 40 · 56 · 40 · 32, Gap 6, centred                  │
 │   ↕ 18                                                                                     │
 │   🔊 ▬▬▬▬▬▬▬▬●─────────    glyph 32² + Slider length 264, thickness 20                     │
 │   ↕ 8                                                                                      │
 │   🎧 Headphones (Realtek)   MinHeight 24, 12/16/400 InkTertiary → Ink on hover              │
 │                                                                                            │
 │  ←24→ column content 304 ←24→                                    scrim deepens from 62%    │
 │                                                          ┌────────┬────────┐               │ ← Pivot h72
 │                                                          │ Lyrics │ Queue  │               │   End/End Gap 16
 │                                                          │ ══════ │        │               │   Pad(24,0,24,16)
 │                                                          └────────┴────────┘               │   underline 2 accent
 ├──────────────────────────────────────────────────────────────────────────────────────────┤
 │  (72 DIP player bar — reserved, pass-through)                                              │
 └──────────────────────────────────────────────────────────────────────────────────────────┘
   |← 352 column box →|← 56 gap →|← pane region (Grow 1), reading column max 700, 48 trailing gutter →|
```

*ch 21 W11 — Stage, WIDE, lyrics pane @ 1440 × 900 (1 char ≈ 16 DIP)* — `21-right-rail-npv-queue-stage.md:644-682`

```
 ┌───────────────────────────────────────┐
 │  (48 caption)                          │
 ├───────────────────────────────────────┤
 │                          ┌──┐  ┌───┐   │ ← TopBar h56 (CompactTopBandH), same Pad
 │                          │🌐│  │ ⌄ │   │
 │  Pad(16,8,16,8)                        │
 │ ┌────┐  Track Title       ♡   ◀◀ ▶ ▶▶ ⋯│ ← art 64 r4 Elevation.Card, decode 192
 │ │ 64 │  16/22/650                      │   identity Grow 1; heart 32²
 │ └────┘  Artist — Album   32²  32 40 32 │   prev/next 32 glyph15, play 40 glyph17
 │                                     32²│   overflow "…" 32² (ALWAYS present here)
 │ ↕ 4                                    │
 │ ▬▬▬▬▬▬●──────────────────────────────  │ ← the seek NEVER folds
 │ 0:47            FLAC            −2:13  │
 ├───────────────────────────────────────┤
 │                                        │
 │   Lyric line (full-width pane)          │ ← the pane takes the whole surface; Gap 0
 │   Lyric line                            │
 │                                        │
 │                     ┌────────┬────────┐│
 │                     │ Lyrics │ Queue  ││ ← pivot unchanged: h72, End/End
 │                     │ ══════ │        ││
 ├───────────────────────────────────────┤
 │  (72 player bar)                       │
 └───────────────────────────────────────┘
   The "…" carries, in this order (only the folded ones):
     ☑ Shuffle        (Toggle, Icons.Shuffle)
     ☑ Repeat         (Toggle, Icons.RepeatOne | RepeatAll)
       Mute / Unmute  (Icons.Volume | Icons.Mute)
     ───────────────  (separator, only if anything precedes)
       …the whole two-section DevicePickerMenu item list
   Built at OPEN time (`StageIdentity.cs:665-699`), anchored BottomEdgeAlignedRight, FocusTrap + LightDismiss,
   ConstrainToRootBounds = false. If every folded control happens to be absent the button opens NOTHING — an
   `items.Count == 0` early return, not an empty menu (`:691`). Tooltip: `player.nowPlaying`.
```

*ch 21 W13 — Stage, COMPACT @ 560 × 900 (1 char ≈ 16 DIP)* — `21-right-rail-npv-queue-stage.md:741-775`

- ONE tree with ONE reflow flag: `StageLayout.Wide` (600, promotion hysteresis 40) decides row direction, art size,
  every transport box and the folded set; a fold is immediate, an unfold needs reserve (`StageLayout.cs:317-346`).
- The scrim is TWO full-bleed layers with no locatable edge — a vertical 0.76 → 0.46 → 0.70 gradient and a
  left-anchored column shade 0.26 held to 352, feathered to exactly 0 across 260 DIP; no region brings a boxed veil
  (`StageChrome.cs:85-108`).
- Every colour is mixed from `StageInk.Veil` / `StageInk.Ink`; the dark arm is `WaveeOnMedia` verbatim, the light arm
  mirrors its alphas; no renderer names a theme (`StageArm.cs`, `StageInk.cs`).
- The 44-DIP `ExitFab` (plate of INK @ 0.14, `Elevation.Card`) outranks the 40-DIP `ScrimFab` — deliberately not a
  matched pair (`StageChrome.cs:186-226`); both panes stay mounted, the switch is a 250 ms opacity cross-fade
  (`StagePanes.cs:57-74`).

### Lyrics — rail arm and the stage's reading column

`Shell/Lyrics.cs` (CORE 3,700) + `Lyrics.UI.cs` (3,500, incl. the stage pane) + `Lyrics.Host.cs` (SHELL 2,300) · Wave 4 / owner K ·
chapter 22 (22 frames; W2-W9 loading / empty / unsynced / video / detached-resync / interlude, W20 the NPV peek, W23
where the retired env seams land)

```
┌────────────────────────────────────────────┐ ← rail, w 340, Corners (8,0,0,0), 1px StrokeCardDefault L+T
│ Lyrics                      🌐  </>  ⛶  ✕ │ header h 44, pad L12 R8, gap 4; title Subtitle 20/28/600
├────────────────────────────────────────────┤ each glyph button 32×32, r4, glyph 16
│                                            │  ← TopPad = viewportH·0.40 − 47/2 = 238 − 23 = 215 DIP
│                                            │
│  ░░░░░░░░░░░░░░░░░░░░░░░  σ6.5 · α0.10     │ bucket 6 rows (past side)
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒       σ4.0 · α0.10     │ dist 3 past
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓     σ2.5 · α0.13     │ dist 2 past
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓          σ1.25· α0.19     │ dist 1 past  (scale 0.98, origin x=0)
│                                            │
│  Every night in my dreams                  │ ★ ACTIVE, y-centre = 0.40·596 = 238 DIP
│  ███████████▒▒▒▒▒▒▒▒▒▒▒                    │   26/33/700, sung Primary | unsung Primary@0.58
│                                            │   row = 33 + 2·7 = 47 DIP, side pad 22, wrap box 296
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓      σ1.25· α0.45     │ dist 1 future
│  ▓▓▓▓▓▓▓▓▓▓▓▓            σ2.5 · α0.24     │ dist 2
│  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒    σ4.0 · α0.14     │ dist 3
│  ░░░░░░░░░░░░░░░         σ5.5 · α0.11     │ dist 4
│  ░░░░░░░░░░░░░░░░░░      σ6.5 · α0.10     │ dist ≥5
│                                            │
│░░░░░░░░░░░░ AutoEdgeFade band 40 ░░░░░░░░░░│ top+bottom feather, scrollbar suppressed
└────────────────────────────────────────────┘
```

*ch 22 W1 — Rail · timed word-by-word · playing · Following @340 (8 DIP/char)* — `22-lyrics.md:212-234`

```
╔═══════════════════════════════════════════════════════════════════════╗
║  caption band, h 48 — pass-through to the shell's own titlebar         ║
╠═══════════════════════════════════════════════════════════════════════╣ body h = 760−48−72 = 640
║ ▒▒▒▒ σ80 baked-blur cover, ×1.30 paint scale, drifting ▒▒▒▒▒▒▒▒▒▒▒▒▒▒ ║ scrim 0.76 at y=0
║ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒ [🌐40] [⌄44]║ top band h 88, pad (16,16,16,12)
║ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░                                    ║ column shade: 0.26 held to
║ ░░ ┌───────────────┐ ░░░  Every night in my dreams                    ║ x=352, feathered to 0 at 612
║ ░  │               │  ░   ██████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒                   ║ ← ACTIVE, 36/46/700
║ ░  │  cover 232²   │  ░                                               ║   y-centre = 0.38·408 = 155
║ ░  │               │  ░   I see you, I feel you                       ║   (inside the lyrics viewport)
║ ░  └───────────────┘  ░   ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  σ1.2 α0.45              ║
║ ░   Title · artist ♥ ⋯ ░   ▒▒▒▒▒▒▒▒▒▒▒▒▒     σ2.0 α0.24              ║ stage σ ladder is FLATTER
║ ░   ──────●────────── ░    ░░░░░░░░░░░░░░    σ2.6 α0.14              ║ (0/1.2/2.0/2.6/3.0)
║ ░   1:02        −2:14 ░    ░░░░░░░░░        σ3.0 α0.11              ║
║ ░    ⤨  ⏮  ( ▶ )  ⏭  ⟳ ░                                             ║ identity column: chapter 21
║ ░   ── volume ──────  ░                                               ║ box w 352, gap 56, pad X 24
║ ░   ♪ This computer   ░                                               ║ pane region w = 1180−408 = 772
║ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓                       Lyrics · Queue         ║ pivot band h 72, End-justified,
╠═══════════════════════════════════════════════════════════════════════╣ scrim 0.70 at y=1
║  player-bar band, h 72 — pass-through to the docked player bar         ║
╚═══════════════════════════════════════════════════════════════════════╝
```

*ch 22 W11 — Immersive stage · WIDE @1180×760 (16 DIP per char)* — `22-lyrics.md:387-409`

What must not be lost (ch 22 §0):

- One document, two surfaces, one ink seam: the same `LyricsView` renders the 340 rail and the stage; only `large`
  and `onMedia` differ (`LyricsView.cs:299-323`, `LyricsInk.cs:23-91`).
- The active line is the only crisp full-brightness line; distance is OPACITY + per-line Gaussian self-blur, never
  scale (every inactive row a flat 0.98, `TransformOriginX = 0`); past ≠ future — sung 0.19 / 0.13 / 0.10 vs
  upcoming 0.45 / 0.24 / 0.14 / 0.11 / 0.10 (`:2936-2943`); rail σ 0 / 1.25 / 2.5 / 4 / 5.5 / 6.5, stage σ 0 / 1.2 /
  2.0 / 2.6 / 3.0 (`LyricsFx`, `:38-57`).
- The karaoke fill is a narrow soft edge on a two-colour glyph run (`Primary` | `Primary @ 0.58`, feather 5 rail / 7
  stage, `:2957-2973`); the eye leads the voice by 140 ms, the fill does not — two indices, neither drives the other
  (`:106, :2105-2111`).
- A handoff is N springs on the LINES, not one on the viewport: latch instantly, 60 ms stagger per rank (cap 4),
  converging at 0.48 s, ζ = 1 (`:1713-1837`). Motion samples `FrameTime.NowQpc`; `LyricsMediaClock` maps
  `(position, sampleQpc)` with a 250 ms snap and ≤ 5 % slew (`LyricsMediaClock.cs:40-50`).
- A ≥ 5 s instrumental break retires the line and raises three breathing dots (`:586-621, :739-815`); a wheel/touch
  drag detaches the follow, blurs go to 0, a "Resync" pill with a 4 s ring appears (`:513-549, :1929-1962`).
- In 0.3 NOTHING here is env-var-gated: the `WAVEE_LYRICS_DEBUG` **environment variable** is deleted, but the debug
  pill it gated is **KEPT** — plan §9.6 Q1 (2026-09-12), contrary to ch 22's own proposal to delete it as a subset of
  the inspector — re-homed on the persisted `diag.developerMode` flag; `FG_STAGE_RECTS` becomes a developer-mode
  signal, the advance probe becomes `--lyrics-advance-probe` in `Screens/Diagnostics.Probe.cs`; the blur STRENGTH is
  a real setting with an Auto tier (`LyricsBlurPolicy.cs:20-36`).

### Deck faces (the twelve NPV players) — rail ▸ Player

`Shell/Deck.cs` (CORE: the 13 model classes verbatim) + `Deck.UI.cs` (host, clock, art, gesture, the record family) +
`+Deck.Faces.cs` (turntable, Zune, cassette, reel, CD, MD, iPod, VU, Winamp, WMP, canvas) · Wave 4 / owner K · chapter 23
(28 frames; W2-W7 the record's pause / buffer / scrub / change-record / run-out / error poses, W8-W21 the other eleven
faces, W22 the rail width breakpoints, W27 the six mount poses)

```
 x:0        77.8                        304.6      formula (side = s)
 ┌────────────────────────────────────────┐  y 0    deck  BoxEl s×s, Corners 8, ClipToBounds, IsolateLayout
 │                                  ▮     │  19.4   rest post .03s×.09s at (.845s,.06s), Corners 3
 │   ┌──────────────────────┐      ╭┼╮    │  38.9   sleeve .64s at (.06s,.12s), rot −2.5°, Corners 4
 │   │ sleeve art (.64s)    │      │┃│    │         arm box .22s × .92s at (.75s,.02s), origin (.5,.08)
 │   │  ╭───────────────────┼──╮   │┃│    │  51.8   disc D = .70s at (.24s,.16s)   [7": .52s at (.33s,.25s)]
 │   │  │      ·grooves·    │  │   │┃│    │         pivot ⌀ .44·armW centred on the origin
 │   │  │   ╭───────────╮   │  │   │┃│    │
 │   │  │   │  label    │   │  │   │┃│    │  116    label = .34·D, cover art, ring 2 px Black(.6)
 │   │  │   │  ⊙ .05D   │   │  │   │┃│    │  155    spindle .05D #E6E9EF (7": .20D hole = deck ground)
 │   │  │   ╰───────────╯   │  │   │┃│    │
 │   └──┼───────────────────╯  │   │┃│    │  194
 │      │      ·sheen·         │   │▚│    │         sheen radial (.30,.25) White .10 → 0 → White .06
 │      ╰─────────────────────╯    │▚│    │  246    headshell .22·armW × .12·armH at (.39,.69), rot −18°
 │                                 ╰┴╯    │  278.6  ↑ hit box .44·armW × .20·armH at (.28,.64) = 31×60 DIP
 │                                        │
 └────────────────────────────────────────┘  y 324
   arm angle = −20 + 17·frac   (rest −34)      platter ω = rpm·6 → 200 °/s @33⅓, 270 °/s @45
```

*ch 23 W1 — Record (default preset), playing, 12" LP, sleeve on, black finish @ side 324* — `23-deck-faces.md:315-334`

- A deck is a machine, not an animation: the platter is a first-order lag (τ↑ 0.23 s / τ↓ 0.53 s = an SL-1200,
  `RecordModel.cs:17-21`), tape hubs run at constant LINEAR speed, the CD spindle is CLV 3000 → 1200 °/s, VU needles
  are ballistic (τ 0.3 / 0.05 s). A linear tween in any of these is the regression.
- The tonearm has 20 phases and 28 authored durations (`TonearmMachine.cs:10-52`): a pause is a 450 ms lift then a
  brake; a new album is lift → 1200 return → 600 sleeve-in → 350 cover swap → 600 slide-out. A deck mounted mid-song
  was already playing (`Seed`, `:131-143`).
- The face is built exactly once per mount; the 30 Hz path writes value-gated signals only (angle at one rim pixel
  ≈ 0.505° at side 324, progress at 1/1024, `DeckClock.cs:244-254`); one ticker per deck gated on
  `!reduced && railOpen && (playing || buffering || !settled)` (`:128-129`), and it stops.
- Artwork changes when the MECHANISM says so — on the `SleeveIn → CoverSwap` edge, mid-eject, or the album edge —
  and cover leaves are KEYED on the generation so the remount IS the 300 ms cross-fade (`RecordDeck.cs:41-44, :578-588`).
- The deck is a layout firewall (`Width = Height = side`, `ClipToBounds`, `IsolateLayout`, `DeckHost.cs:76-82`);
  palettes are literal hex — exactly four things are theme- or cover-bound.

### Video: docked cap and in-window PiP — rail / overlay

`Shell/Video.UI.cs` (the four surfaces, the shared stage, the placement menu, the override-manager body) + `Video.cs`
(CORE: `PlacementCore` + the nine pure rule classes) + `Video.Host.cs` (the pop-out window) · `Playback/Playback.Video.cs`
stays the decode/host only (H, Wave 3) · Wave 4 / owner K · chapter 24 (27 frames; W3 the aspect ladder, W7 the watch
page's `PageStage` face, W14/W14b/W15 the pop-out window at 640×360 and borderless fullscreen, W16-W18 the main-window
fullscreen surface — chrome and bar UNMOUNTED, a childless Shield first in the ZStack — W19 the placement menu,
W20-W22 the override manager flyout)

```
   rail inner width = 340 DIP = 42 chars
   ┌──────────────────────────────────────────┐ ← card top; the RAIL clips its own rounded top-left
   │                                          │   BoxEl ZStack ClipToBounds Shrink=0 MinWidth=0
   │                                          │   Height = ShellUi.DockedVideoHeight (FloatSignal)
   │            [ live video hole ]           │   Fill = Tok.MediaLetterbox (#000 opaque)
   │         MediaPlayerElement, full-bleed   │   CornerRadius = 0, no border, NO shadow
   │         Stretch = Uniform (Fit)          │   ShowLetterboxBars = true
   │                                          │   H = FitDockedVideoHeight(340,1920,1080)
   │                                          │     = 340 × 1080/1920 = 191.25 DIP
   └──────────────────────────────────────────┘
                                           ↑ the bottom 16 DIP carry the rail's vertical Splitter
                                             (ShowIndicator=false, HitTestPassThrough wrapper)
```

*ch 24 W1 — Docked cap, Cap face, 16:9 source, at rest @ rail 340* — `24-video-surfaces.md:341-354`

```
  window 1440 × 900 (client), player bar 72 at the bottom
  ┌──────────────────────────────────────────────────────────────────────────────────┐
  │  page content — ContentHost insets its BOTTOM by FloatingSurfaceReserve          │
  │                 = _h + Margin = 202 + 16 = 218 DIP                               │
  │                                                    ┌──────────────────────────┐  │
  │                                                    │                          │  │  PiP surface
  │                                                    │   [ live video hole ]    │  │  W = 360, H = 202
  │                                                    │   pure video at rest     │  │  x = 1440−360−16 = 1064
  │                                                    │                          │  │  y = 900−202−72−16 = 610
  │                                                    └──────────────────────────┘  │  Corners = Radii.Card(8)
  ├──────────────────────────────────────────────────────────────────────────────────┤  Border 1 Tok.StrokeCardDefault
  │  player bar, 72 DIP — STILL RENDERING (TransportOwner.Docked is admitted by it)  │  Shadow = Elevation.Flyout
  └──────────────────────────────────────────────────────────────────────────────────┘  Fill = Transparent (the hole!)
   16 DIP gap from every window edge (Margin); the anchor is DERIVED each frame from the viewport,
   so a window resize moves the card with the corner — until the first drag sets _placed = true.
```

*ch 24 W8 — In-window PiP, anchored home, at rest @ viewport 1440×900* — `24-video-surfaces.md:459-475`

- No `LayoutTransition`, no `Opacity`, no `OpacityGroup` / blur / edge-fade on ANY ancestor of a video hole —
  `DrawOp.DrawVideo` is a `DestOut` erase; an ancestor opacity washes it out, an offscreen RT makes the hole vanish
  silently (`DockedVideoSurface.cs:70-80`, `VideoFullscreenSurface.cs:40-49`).
- The docked card is the content's own shape: `FitDockedVideoHeight(railW, naturalW, naturalH)` — 16:9 in a 340 rail is
  191.25 DIP, 4:3 is 255, 2.35:1 is 143.44 (`ShellResponsiveLayout.cs:158-162`). Black bars are a regression.
- Nothing ever restarts playback; every surface binds `PlaybackBridge.VideoPlayer`, stage key = player identity only,
  so a video→video skip keeps the element mounted and the engine cross-fades (`DockedVideoSurface.cs:55-57`).
- Exactly one mounted surface from one value (`PlacementCore.Resolve(VideoSurface)`) and exactly one transport per
  window (`TransportOwnerFor`, `PlacementCore.cs:451-457`); the 72-DIP bar keeps rendering under the docked card and
  the PiP; fullscreen UNMOUNTS chrome and bar.
- No-player is never a black rectangle on three of four surfaces: artwork @ 0.4 over `Tok.MediaLetterbox`, a 20-DIP
  ring, "Loading…" (`:409-441`). **The pop-out is a 0.2.9 defect — give it the same poster in 0.3** (W14b).
- Every surface draws `MediaPlayerElement`'s OWN 8-DIP rounded, hairline-bordered frame — including "full-bleed"
  fullscreen (`MediaPlayerElement.cs:154-160, :1057-1058`). Reproduce it; do not invent an app-side radius.
- The placement menu is reachable from the picture itself; unavailable rungs are DISABLED with their reason in the
  accelerator column, never hidden (`VideoPlacementMenu.cs:43-58`). In-app ✕ → `NotifyVideoSurfaceClosed`; the menu's
  "Turn off video" → `TurnVideoOff` directly (`:73`).

---

## 6. Wave 4 — design system, controls, drag, dialogs (owner L)

Files: `Platform/Design.cs` (3,000), `Controls.cs` + `+Controls.Cta.cs` + `+Controls.Art.cs` + `+Controls.Picker.cs` (A16
envelope 4,500-6,000), `Drag.cs` (700, **first week**, A6), `Prefs.cs`, `Entities/Palette.Host.cs` (A5). Chapters 00, 02,
01 (the shared primitives), 29.

### The shell material stack — every surface

`Platform/Design.cs` (tokens, ramp, colour, materials, motion, wash geometry, `MorphKeys`, focus insets, ambient cadence)
· Wave 4 / owner L · chapter 00 (18 frames; W7 the same stack LIGHT, W8 Home's three-wash placement, W1/W2 the type and
spacing ramps, W4 the CTA geometry table, W13 the zoom ladder)

```
 window  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
 y=0     │ ░░░ live DWM Mica (base layer — the shell root paints NOTHING) ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │
         │┌──────────────────────────────────────────────────────────────────────────────────────────────────┐│
         ││ MATERIAL  ShellMaterialLayer → Tint: ONE flat full-bleed rect, Fill = published tint or           ││
         ││           NeutralGround (ShellGround @ α 0.03), BrushTransitionMs 250                             ││
         ││   detail tint = WaveePalette.TintedDark(scheme) with A = 0.14        (CoverPaletteLeaves.cs:245)  ││
         │└──────────────────────────────────────────────────────────────────────────────────────────────────┘│
 y=48    │ ═══ merged title row (UNPAINTED: material + Mica show through) ══════════════════════════════ ─ □ × │
         │┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈╭─────────────────────────────────────────────────────────────────────────────────│
         │  sidebar 240      │ ▏CONTENT REGION                                                                 │
         │  (UNPAINTED)      │ ▏ Fill = WaveeColors.FileArea  #3A3A3A4C                                        │
         │                   │ ▏ + 1px Tok.StrokeCardDefault #00000019 on LEFT + TOP edges ONLY                │
         │                   │ ▏ + ContentPaneCorners (8, 0, 0, 0) — ONE rounded corner, facing the pane       │
         │                   │ ▏ + NO shadow                                                                   │
         │                   │ ▏                                                                               │
         │                   │ ▏ PAGE TONE PLANE (a page-root SIBLING of the scrolling page, not a child)      │
         │                   │ ▏   Fill = PageTone(scheme, Dark) with A = 0.20   ⇒ ~80 % of Mica survives      │
         │                   │ ▏   ClipToBounds + ContentPaneCorners, HitTestVisible = false                   │
         │                   │ ▏   BrushTransitionMs 250 ⇒ a grading ARRIVAL cross-fades, never snaps          │
         │                   │ ▏                                                                               │
 y=828   │┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┴─────────────────────────────────────────────────────────────────────────────────│
         │ ▁▁▁ player dock 72 (UNPAINTED — whatever the material layer paints under it IS the dock) ▁▁▁▁▁▁▁▁▁▁ │
 y=900   └────────────────────────────────────────────────────────────────────────────────────────────────────┘
  Opaque stand-ins (floating panes, the login view, the flatten base for every contrast gate):
     ShellGround  dark #202020  ·  ContentSurface dark #282828 (one step, lift 0.0364 toward white)
     ContentLayer dark = white @ α 9/255 — the LAYER equivalent: Over(ContentLayer, ShellGround) == ContentSurface
```

*ch 00 W6 — The shell material stack, DARK, a detail page @ 1600 × 900 (16 DIP/char)* — `00-design-system.md:441-468`

- Every text node resolves size AND line height AND weight from one of eight ramp rungs (12/16 · 14/20 ·
  14/20-600 · 18/24 · 20/28-600 · 28/36-600 · 40/52-600 · 68/92-600); weight is 400 or 600 with exactly six named
  divergences (three 700 display faces, three SemiLight 350) — a seventh is a regression (`WaveeType.cs:155-254`).
- The accent has three jobs that never mix — Action (one solid plate per screenful), Selection (a short bar/pill),
  Decor (ink or wash) — and is NEVER structure (`WaveeTokens.cs:10-51`).
- The detail ground is hue-from-the-record, L forced 0.15 dark / 0.94 light, S capped 0.30 / 0.16, painted at α 0.20
  dark / 0.30 light (`WaveePalette.cs:192/204`, `CoverPaletteLeaves.cs:139`); no blurred cover band, ever.
- Interaction scale is three tiers (1.02/0.98 · 1.04/0.96 · 1.07/0.92) returning `1f` under reduced motion; durations
  are 83 / 167 / 250 ms and nothing between (`WaveeMotion.cs:39-59`).
- Every art slot is a solid cover-tinted tile before its bitmap lands (neutral lerped 0.55 toward the graded colour,
  `Surfaces.cs:57-111`); a cover ≥ 80 DIP breathes 1.0↔0.5 over 1 s while loading, below 80 it is a static tile.
- App zoom is one global multiplier (`OS DPI × zoom`), auto-derived against a 1600 × 900 design box, snapped DOWN to
  a plateau rung (`ZoomAutoPolicy.cs`); `MigrateMode` runs once in `Main` or a user on 125 % wakes at 150 %.
- No app node paints a focus ring: the engine draws the dual 2 + 1 DIP ring only for KEYBOARD focus; Wavee owns
  exactly two insets, `FocusInsetBordered = 2f` / `FocusInsetRow = 1f` (ch 29 §0.3).

### Media cards and shelves — every page

`+Controls.Art.cs` (`MediaCard`, `PagedShelf`, chips, stat tiles, countdowns, face piles, rich text) + `Card()` factories in each
`Entities/X.UI.cs` · Wave 4 / owner L (+ 5 / M, N, O, P) · chapter 02 (29 frames; W2 hovered, W3 circular, W4 the
virtualized shelf and its fit table, W6-W8 grid cards, W9-W14 media rows, W15 the 16:9 video card, W21 the filter
chip rail, W24/W25 the countdowns, W26 the card context menu)

```
scale: 1 char = 8 DIP
        <------------------- 173.3 -------------------->
   ^    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·      4  gutter top   (Spacing.XS)
   |    ┌───────────────────────────────────────────────┐   card root: radius 8, NO fill, NO stroke, NO shadow
   |    │                                               │   ^ 8 pad
   |    │   ┌───────────────────────────────────────┐   │
   |    │   │                                       │   │   cover 157.3 x 157.3, radius 8, ClipToBounds
   |    │   │              c o v e r                │   │   decode 256 px square, ImageFit.Cover
 245.3  │   │          (Shimmer tile beneath)       │   │   shimmer = cover tint @0.55 over neutral, breathing
   |    │   │                                       │   │
   |    │   └───────────────────────────────────────┘   │
   |    │                                               │   8  content gap (Spacing.S)
   |    │   Album or playlist title…                    │   BodyStrong 14/20/600, Width=157.3, 1 line, ellipsis
   |    │                                               │   2  (Spacing.XXS)
   |    │   Artist · 2024 second line wraps to two       │   Caption 12/16, Width=157.3, 2 lines, ellipsis
   |    │   lines and then ellipsises…                  │
   |    │                                               │   12 pad bottom (Spacing.M)
   |    └───────────────────────────────────────────────┘
   v    ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·  ·      2  gutter bottom (Spacing.XXS)

Height = 4 + 8 + (cardW-16) + 8 + 20 + 2 + 32 + 12 + 2  =  cardW + 72   (MediaCard.cs:212)
```

*ch 02 W1 - Shelf card, resting, square (album/playlist) @ cardW 173.3 (shelf 1100)* — `02-cards-and-controls.md:236-258`

- One hover plate, REVEALED — every rectangular card rests borderless and unfilled; the plate fades in over 83 ms
  (`MediaCard.cs:83-98`). A grid at rest is covers and labels floating on the page, not a wall of boxes.
- Lift −4 DIP, press scale 0.99 + −1 DIP, identical on every surface (`ApplyCardPhysics`, `:68-74`); the shadow band
  never changes on hover; the artwork zooms 1.04 inside the clip, the root does not scale (`:1150-1156`).
- Shape is the type tell: artist = circle, album/playlist/show = `Radii.Card` 8, video = 16:9 at 4. FAB bottom-right,
  "…" top-right, equalizer bottom-left, 8-DIP inset, never rearranged.
- A card with no play route renders no FAB — and in 0.3 every factory reads `Playback.CanPlay(handle)`, uniformly
  (0.2.9 only `Shelf` could say "no play route", `:201` vs `:224, :882, :921, :956`).
- Hover does not arm from a stationary pointer (`HoverMotionGate`, `WaveeMotion.cs:141-159`); countdowns tick off
  the frame clock (`FlipCountdown.cs:52-78`); a card subtitle is HTML through `RichText`.

### Empty / error / offline vocabulary — every surface

`Platform/Controls.cs` (the one vacancy grammar) · Wave 4 / owner L · chapters 02 + 29 — no frame is copied here: ch 02 W18
is the PAGE-scale empty state (centred column, `PageHero` 28/36/600 headline, `Caption` 12/16 secondary, a 16 spacer, one
`Button.Standard`, Padding 24, NO glyph), W19 the rail-scale `Compact`, W20 error + the offline banner; ch 29 W13 the four
readiness states side by side

- Display-face headline + one caption + at most one QUIET action; no glyph, ever; `Button.Standard`, never Accent;
  an error is an empty state with a reason (`EmptyState.cs:8-29`).
- "In flight", "the answer was nothing", "failed" and "no network" are four states and 0.2.9 has vocabulary for
  three — `OfflineBanner` exists with zero call sites (ch 29 §0.17); a skeleton suppresses the scroll rail, an empty
  state does not (`SceneRecorder.cs:3402`); a surface crosses skeleton → empty once per account, never back.

### Track context menu — every list surface

`Platform/Actions.cs` (the one action table, descriptor, targeting — owner I by A7) + `Platform/Actions.UI.cs` (menu
vocabulary, `ActionIcons`) rendered through `Entities/Track.UI.cs` · Wave 4 / owner I + L · chapters 01 + 29 (ch 01 W20 the
multi-selection menu, ch 02 W26 the card menu, ch 29 W12 a bound action's four visual states)

```
 ┌────────────────────────────────────────────┐
 │ [▨38] Midnight City                        │  header: art 38x38 r6 (19 if circular), title, subtitle
 │       M83 · Hurry Up, We're Dreaming        │  subtitle = artists · album, HTML-flattened
 ├────────────────────────────────────────────┤
 │  ▶      ⏵        ⏭          ♡              │  PRIMARY STRIP (labeled command bar)
 │ Play  Play next Play after  Save           │  = TrackActions.Play / PlayNext / AddToQueue / ToggleLike
 ├────────────────────────────────────────────┤  ToggleLike checked → "Saved" + accent-filled HeartFill
 │ ＋ Add to playlist                       ▸ │  New playlist · ≤10 MRU-ordered editable playlists · ─ · More playlists…
 │ →  Move to playlist                      ▸ │  ONLY in an editable playlist host (else the row is absent)
 │ ▤  Go to album        (or ⌁ Go to podcast) │  single target; absent on album pages (showGoToAlbum: false)
 │ ☺  Go to artist       (or Go to artists ▸) │  1 navigable artist → row; ≥2 → cascade, one row per artist
 │ ↗  Share                                 ▸ │  Copy link · [Copy Spotify URI · Open in Spotify Web] (single only)
 │ ▤  View credits                            │  single track with a primary artist URI
 │ ⌁  Go to song radio                        │  single spotify:track:*
 │ 🎬 Video                                 ▸ │  Attach… / Replace… + Locate… + Show in Explorer + ─ + Remove video
 ├────────────────────────────────────────────┤
 │ (surface extras: Move up / Move down —      │  queue rows;  "Track details" — the ultra-compact-tier drawer fallback
 │  Track details)                             │
 ├────────────────────────────────────────────┤
 │ ✕  Remove from this playlist                │  destructive LAST, behind its own separator
 │ ✕  Remove from queue                        │
 └────────────────────────────────────────────┘
```

*ch 01 W19 — track context menu, single track (right-click / Menu key / long-press / the "…")* — `01-track-row.md:649-672`

- A bound action that cannot run is VISIBLE and EXPLAINED, never hidden: `Resolve` folds every reason into one call
  so the row's disabled state and `Execute`'s refusal cannot disagree; seven reasons, each with a loc key
  (`WaveeActionDescriptor.cs:96-116`, `WaveeActionTargeting.cs:140-146`).
- A confirmation-required action REFUSES to run when there is no overlay (`HostUnavailable`, `:109-110, :134`).
- The picker continues the submenu, never reshuffles it: `PlaylistDepositTargets.Order` is the ONE ordering
  (MRU-first, then rootlist) for "Add to playlist ▸", "Move to playlist ▸" and the picker (ch 06 §0.14).

### Drag chip, insertion preview, spring-load, refusals — every surface

`Platform/Drag.cs` (payload record, chip resolver, every drop rule, insertion preview) — shipped in the FIRST week of
Wave 4 · Wave 4 / owner L · chapters 01 + 29 (ch 01 W22 drop refused, ch 29 W17-W20 chip states, insertion line,
spring-load waypoint, the five playlist refusal captions)

```
 the chip (cursor-anchored, HitTestVisible false, MaxWidth 280)
   ┌────────────────────────────────┐  ← two 4 DIP-offset stack cards behind it when Count ≥ 2
  ┌┤ [▨ 40]  Midnight City       (4)│  ← count badge at the card's top-trailing corner (Padding 0/−6/−6/0)
 ┌┤│          M83                   │     title 13/600 TextPrimary, subtitle 12 TextSecondary
 └┤│          Add 4 songs           │     caption 12/16 TextSecondary (resting verb, or the live target's caption)
  └└────────────────────────────────┘     Fill FillSolidTertiary (OPAQUE) · Border 1 StrokeSurfaceDefault
     Padding 8/8/12/8 · Gap 10 · Corners Radii.Overlay 8 · Shadow Elevation.Flyout
     pickup FLASH once per gesture: Rotation 4° (DragChip.TiltDeg) + Scale 1.02 (PickupScale) → flat in
     150 ms (PickupFlashMs); NOT a resting pose.  Enter: Sx/Sy 0.92, Opacity 0
   source row stays in its slot at Opacity 0.4 (Drag.SourceDimOpacity) — never a lifted full-width snapshot

 NO COVER IN HAND (a tab drag, a folder, a cover-less playlist): the art box becomes a 40 DIP kind-GLYPH tile —
   Fill FillSubtleSecondary, Corners Radii.Control, the sidebar's own marks (WaveeResourceDrag.GlyphFor :366):
   Playlist → MusicNote · Album → Album · Artist → Contact · Show/Episode → RadioTower · Folder → Folder · Route → Home
 LIKED SONGS drags wearing its OWN cover: a pre-built `LikedChipArt` element (LikedSongsArtwork.Dynamic at
   DragChip.ArtSize) held in a static field, because Chip() runs inside the 0-alloc frame region (:352-362).
 A SECOND chip kind rides the same resolver: the sidebar customizer's palette chip (SidebarEditPlan.SectionDragKind →
   label + CzGlyphs mark + "Drop here"). One resolver, because DragPreviewLayer.Of takes exactly one (:307-318).
 SPRING-LOAD: a drag resting 500 ms (WaveeResourceDrag.SpringLoadMs) over a container opens it — a collapsed sidebar
   folder expands, a tab activates. Long enough that merely travelling ACROSS a folder never opens it.

 the insertion gap (a playlist track list accepting the drop)
 ──────────────────────────────────────────────────────  ← insertion line (framework-owned)
 ┌──────────────────────────────────────────────────┐
 │ [▨32]  Midnight City                             │   card: Fill FillSolidSecondary, Border 1 AccentDefault,
 │        M83                                       │         Shadow Elevation.Card, Corners 4,
 └──────────────────────────────────────────────────┘         Margin L/R 8 (RowInset), Padding L/R 8 (PadX−RowInset),
 ┌──────────────────────────────────────────────────┐         Height = the list's own rowH, HitTestVisible false
 │ [▨32]  Wait                            ( +37 )   │   title 14/600 TextPrimary, subtitle 12 TextSecondary
 └──────────────────────────────────────────────────┘   "+N" pill: Padding 8/2/8/2, Radii.Pill, Fill AccentSubtle,
 ──────────────────────────────────────────────────────      text 12/600 AccentTextPrimary  (cap = 3 cards)
   a SAME-LIST reorder never dims the app (SpotlightWhen = !IsSameListDrop); a cross-list deposit keeps the scrim
   NO TRACK SNAPSHOT (an album/playlist card dragged over a list — the resolver runs only after the drop): ONE card,
     title = payload.Name, subtitle empty, art = a 32 DIP FillSubtleSecondary tile with Icon MusicNote 16 TextSecondary
     (PlaylistInsertionPreview.cs:42-47). `total` falls back to 1, so no "+N" pill.
   hide-artwork: `showArtwork:false` drops the art child entirely — the card is title + subtitle only (:48-50).
```

*ch 01 W21 — drag in progress: the chip and the insertion gap* — `01-track-row.md:695-732`

- The chip is the ONLY moving visual and the only caption surface; a drag kind the resolver does not recognise draws
  nothing (`WaveeResourceDrag.cs:307-318`); a refusal always has a sentence from one table
  (`PlaylistDropRefusalRules.Evaluate`, `WaveeDragRules.cs:126-142`).
- The chip says what it will DO — a resting verb, superseded by a live target's caption, superseded by a refusal +
  the blocked glyph (`WaveeResourceDrag.cs:342-346`, `DragChip.cs:110-152`); the source row stays in its slot at 0.4.
- Every affordance inside a row blocks the row's drag arm (`BlocksDragArm = true`: heart, "…", video cell, "+",
  chevron, `TrackRow.cs:944-1042`) or a press on the heart arms a drag and the like never fires.
- Spring-load at 500 ms; a same-list reorder never dims the app (`SpotlightWhen = !IsSameListDrop`).

### Dialog width ladder and the destructive confirm — modal

`Platform/Controls.cs` (the three dialog helpers) · the two raw plates stay raw (setup 762 × 490, after-update 720 — R's in
Wave 6) · Wave 4 / owner L · chapter 29 — no frame is copied here: ch 29 W22 draws the four rungs to scale in width
(320 rename / credits / add-to-playlist / every destructive confirm · 460 the runtime setup card · 480 the three-button
Storage pair + the sidebar pickers · 548 report dialog / lyrics inspector / runtime diagnostics) and W23 the
destructive-confirm shape

- One dialog family, one width ladder — 320 / 460 / 480 / 548; 480 is also what a three-button dialog picks on its own
  (`ContentDialog.cs:110-117, :283`). Nothing else invents a width.
- A destructive confirm defaults to Close, not Primary (`SettingsShared.Confirm`, `SettingsShared.cs:38`) — the
  focus trap focuses the accent button first, so a reflexive Enter must cancel. Thirteen call sites app-wide.
- A toast's severity is not a Wavee decision: glyph, icon plate, inverse foreground and card ground come from
  `SeverityVisuals` shared with `InfoBar` (`SeverityVisuals.cs:19-26`); default 5 000 ms, `0` sticky, `MaxVisible = 3`;
  a repeated toast REFRESHES, never stacks (`Toast.cs:38-46, :169-193`).

---

## 7. Wave 4.5 — the shared detail frame and the track surface (owner M)

The slot the plan did not have. `Entities/Detail.cs` (CORE 900) + `Detail.UI.cs` (2,600) by A1; `Track.cs` (1,200) +
`Track.UI.cs` (2,600) + `Track.Table.cs` (2,600) by A2 (`Track.Drawer.cs` 700 follows in Wave 5). Owner O configures the
table through `TableProfile` and never edits it. **Wave 5 does not open until the fake album renders through the real
frame in both layout arms** (ch 03 §10 item 67). Chapters 03, 04, 01, and 30 for every preference these files read.

### Shared detail frame, Automatic arm (two-column, mode 0) — `album:` `pl:` `liked` `local` `show:` `prerelease:`

`Entities/Detail.UI.cs` (`Frame`, `Hero`, `Rail`, `CompactRail`, `ContextBand`, `Skeleton`, `NoticeBar`, `TonePlane`) +
`Detail.cs` · Wave 4.5 / owner M · chapter 03 (27 frames; W2/W3 modes 1 and 2 at 700 / 600, W4 the rail collapsed to
the 96-DIP strip, W9-W11 skeleton and reveal, W12-W14 the merged band scrolled / in selection / in search, W15 the
notice strip, W22 the show composition, W25 the vertical show header)

```
◀── page 1280 ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────▶
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ (notice strip: 0 height when Notice==None — DetailShell.cs:669)                                                                               │
├─ rail 280 ──────────────────────┬16┬─ right column  (Grow 1, MinWidth 300)  ────────────────────────────────────────────────────────────────┤
│ pad 16 L / 8 R / 24 T / 24 B    │gr│  chrome (toolbar 44 + chips? + lens? + column header 36 + hairline 1)                                  │
│ ┌────── cover 256 ───────────┐  │ip│  ┌──────────────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │  Radii.Card 8              │  │  │  │ [▶ Play next ▾] [⇄ Shuffle] │ [↕ Sort] [≣ Row size] [☑ Select] [⋯]        [ 🔍 Find … 240 ]     │ │
│ │  Elevation.Card            │  │  │  └── CommandBarSurface h=44, pad 6/5 ───────────────────────────────────────────────────────────────┘ │
│ │  saturation 1.18           │  │  │  #   TITLE                                     ALBUM              ♥     ⏱                            │
│ │  decodePx 256              │  │  │ ──────────────────────────────────────────────────────── 1 DIP StrokeDividerDefault ─────────────── │
│ └────────────────────────────┘  │  │  1   Give Life Back to Music        Daft Punk    Random Access…   ♡   4:35                           │
│ ↕14 gap (DetailRail.cs:264)     │  │  2   The Game of Love                            Random Access…   ♡   5:22                           │
│ ALBUM · 2013            eyebrow │  │  3   Giorgio by Moroder                          Random Access…   ♥   9:04                           │
│ ┌ Random Access ─────────────┐  │  │  …                                                                                                    │
│ │ Memories        40/52/600  │  │  │                                                                                                       │
│ └ (winH≥900 ⇒ 40, else 28)───┘  │  │                                                                                                       │
│ ●●+2  Daft Punk      facepile   │  │                                                                                                       │
│ [ ▶ Play  36 ]  (◯40)(◯40)(◯40) │  │                                                                                                       │
│   accent capsule   ♡   ↗   ⋯    │  │                                                                                                       │
│ About this release   (eyebrow)  │  │                                                                                                       │
│ ┌ Songs ─┐ ┌ Length ┐           │  │                                                                                                       │
│ │  13    │ │ 74 min │           │  │                                                                                                       │
│ └────────┘ └────────┘           │  │                                                                                                       │
│ ┌ Released ───────────────────┐ │  │                                                                                                       │
│ │ May 17, 2013                │ │  │                                                                                                       │
│ └─────────────────────────────┘ │  │                                                                                                       │
│ Label: Columbia   (11px note)   │  │                                                                                                       │
│ ℗ 2013 Daft Life Ltd.           │  │                                                                                                       │
│ ▽ rail ScrollView (hidden bar)  │  │  ── AlbumTrailing (HasTrailing) scrolls with the rows ──                                              │
│    Fill = FillLayerDefault      │  │  About the artist · Fans also like · More by · Featured on · Merch · Similar albums                   │
└─────────────────────────────────┴──┴───────────────────────────────────────────────────────────────────────────────────────────────────────┘
     cover = railW − SidePadL 16 − SidePadR 8 = 256          (DetailRail.cs:96)
```

*ch 03 W1 - two-column, mode 0 (wide), album, fully loaded @ page width 1280* — `03-detail-frame.md:174-207`

### Shared detail frame, vertical / Hero arm — the same routes when `page < 540` or `Track page layout = Hero`

`Entities/Detail.UI.cs` · Wave 4.5 / owner M · chapter 03 (W7 the STACKED flow at 380: art 280 over a one-column identity;
ch 30 W1-W14 walk every kind through Automatic modes 0-3 and Hero)

```
┌───────────────── page 520 ─────────────────┐
│ pad 24                                      │
│ ┌── art 197 ──┐ gap24 ┌ identity (Grow 1) ─┐│
│ │  Radii.Card │       │ ALBUM · 2013  16px ││  ← hero-eyebrow (late, FadeUp)
│ │  Elev.Card  │       │ Random Access      ││  ← hero-title 32/43/600 × 2 lines, Display face,
│ │  sat 1.18   │       │ Memories           ││     −20/1000 em, LineHeight prop = NaN
│ │  decode 512 │       │ ▄▄▄▄  20×2 accent  ││  ← hero-rule (Surfaces.AccentRule)
│ │             │       │ Daft Punk  12/600  ││  ← hero-attribution (late)
│ │             │       │ 13 songs · 1 hr 14 ││  ← hero-meta 12/16, MaxLines 2 (late)
│ │             │       │ ┌────────┐(32)(32) ││  ← hero-actions: Play pill 36 + satellites 32
│ │             │       │ │▶ Play  │(32)(32) ││     Gap 8, Wrap true, Margin top 4
│ └─────────────┘       └────────────────────┘│
│ identity MinHeight = art (197) in row flow   │
│ pad-bottom 8                                 │
├──── toolbar host: pad L/R = compactLeft ─────┤
│ [▶][⇄] │ [↕][≣][☑][⋯]        [🔍 240]   h44  │
├──────────────────────────────────────────────┤
│ #   TITLE                        ♥    ⏱   36 │  ← column header + 1 hairline
├──────────────────────────────────────────────┤
│ 1  Give Life Back to Music       ♡  4:35     │
│ 2  The Game of Love              ♡  5:22     │
└──────────────────────────────────────────────┘
 band height = 24 + max(197, identity) + 8 + 8 + 44 + 4     DetailVerticalLayout.cs:552-565
```

*ch 03 W6 - vertical / hero system, ROW FLOW @ page width 520* — `03-detail-frame.md:266-290`

```
┌──────────────────────────────── page 1100 ───────────────────────────────────────┐
│ ┌ art 240 ┐ gap24 ┌ identity Grow1, MinHeight 240 ────────────────────────────┐  │
│ │         │       │ PLAYLIST · Collaborative                                   │  │
│ │         │       │ Discover Weekly                     96/128/600 (real title)│  │
│ │         │       │ ▄▄▄▄                                                       │  │
│ │         │       │ ●●● +4 collaborators      (CollaboratorFacePile)           │  │
│ │         │       │ 30 songs · 18.7M saves · 2 hr 11 min      (wraps to 2)     │  │
│ │         │       │ ┌ fill block Grow 1 — surplus opens HERE ┐                 │  │
│ │         │       │ [▶ Play](32)(32)(32)(32)                                   │  │
│ └─────────┘       │ description ≤3 lines, measure 640                          │  │
│                   └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────────┘
 identity gap = IdentityGapFor(...) — resting 4, widened (even 2-DIP steps) up to 12 when short
```

*ch 03 W8 - "Track page layout = Hero" at a WIDE window (page 1100)* — `03-detail-frame.md:328-342`

What must not be lost (ch 03 §0):

- ONE art-derived ground, mostly Mica: a flat `PageTone` plane at α 0.20 dark / 0.30 light, cross-fading 250 ms when
  the grading lands (`CoverPaletteLeaves.cs:139, :115`); no blurred artwork band (`:59-73` is the tombstone).
- The loading band is the loaded band: the skeleton composes the hero from the SAME resolver at the SAME sizes
  (`DetailSkeleton.cs:39-130`), so arriving content never shoves the toolbar or the rows (D49); a cold list reveals
  12 real rows per frame, each crossing a 280 ms fade (`DetailRevealRamp.cs:11`).
- Resize never flips twice: mode 820/660 ± 24, vertical 540 enter / 580 exit, hero flow 424 enter / 400 leave, title
  size ±2 up / 1 down (`DetailLayoutBreakpoints.cs:8, :59-60, :75-87`, `DetailVerticalLayout.cs:47-50, :503-510`).
- The sticky context band is typography, not a plate — 56 DIP, NO fill, content clipped at `56 + 36 + 1 = 93` with a
  24-DIP feather (`ContextBand.cs:71-96`); it exists in the VERTICAL arm only (`DetailVerticalHero.Build :393`) —
  do not generalise it to the rail arm; its `onStuck` flips hit-test ownership (`DetailVerticalHero.cs:70, :425, :435`).
- The rail is a user-owned column: 180…480, a resist zone below 180 fading content to 0.35, collapse at ~136 raw,
  re-open at 220, four persisted scopes + a fifth uniform (`DetailShell.cs:200-206`, `DetailRailPolicy.cs:25`); collapsed
  is a 96-DIP identity strip, never gone.
- The hero cover never animates (`DetailRail.cs:143-144`); late rows fade up + `Shove` FLIP (`:52-70`); bad news is a
  strip between header and list, never an error page (`DetailNoticeBar.cs:45-88`); the page's tint reaches the window
  chrome as a hand-over, never a clear (`CoverPaletteLeaves.cs:242-246`).

### Detail track table — `album:` `pl:` `liked` `local` (+ the Library embed)

`Entities/Track.Table.cs` (chrome, command bar, header, tiers, list arms, choreography, drag, selection, recs, filters, the
drawer's mount) · Wave 4.5 / owner M · chapter 04 (22 frames; W2 the album set, W3/W4 tiers 3 and 6 at 500 / 280, W5/W6
cold load and reveal, W12-W14 selection / search expanded / the filter flyout, W15 the drawer open, W16 drag insertion,
W18 the Liked chrome, W19 "Recommended songs")

```
│◀16▶                                                                                                        ◀16▶│
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ ← chrome pad (16, 8, 16, 0)
│ [▸ Play next|▾] [⤮ Shuffle]  │  [⇅ Custom order ▾] [≣ Default ▾] [☑ Select]   [⋯]          [🔍  ▽ ]           │ 44 DIP CommandBarSurface (pad 6/5)
│                                                                                                       ◀ 66 ▶   │ commands 32 high, gap 2, sep 17,
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 8 before the search host; 4 margin-bottom
│  #  ♥   ▣  Title                                       Album                      Date added   🕐     ⋯    ⌄   │ 36 DIP header row (⌄ lane: no label)
│ 28  28  32 ◀────────── 365 (star 1) ──────────▶  ◀───── 273 (star .75) ─────▶      88          52     40   26  │ colgap 12 · padX 16
├────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│  1  ♡  [▣] Sunset Boulevard                            Neon Horizon               3 days ago   3:41   ⋯    ▸   │ row 48, zebra OFF (even)
│      ▐                                                                                                        │
│  2  ♥  [▣] Weird Fishes                                In Rainbows                Sep 28       4:12   ⋯    ▸   │ row 48, zebra ON  (odd)
│                                                        ↑ subline: artist · album only when the Album lane is off
│  3  ♡  [▣] An Ending (Ascent)                          Apollo                     Jul 30       4:35   ⋯    ▸   │
│            └ 14/20/600 TextPrimary; 12/16 Caption subline (artists as per-artist link spans)                   │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Lanes (playlist, `ShowVersions: true` → the chevron lane is up at every tier < 6, `DetailConfig.cs:200`):
  # 28 · ♥ 28 · art 32 · Title* · Album* · Date 88 · Duration 52 · Actions 40 · Expand 26  = 9 columns, 8 gaps
Star pool = 1060 − 2·16 (padX) − 8·12 (gaps) − (28+28+32+88+52+40+26) = 638 → Title 364.6 · Album 273.4
(The trailing lane is Actions 40 **or** Video 28 — never both: `TrackRow.MoreButton` rides INSIDE the film lane
when a video exists, `DetailTrackTableRules.cs:68-70`. `--fake` has no video, so it is Actions here.)
```

*ch 04 W1 — Playlist, fully loaded, tier 0 @ right pane 1060 DIP (window ≈1400)* — `04-detail-track-table.md:230-251`

### Detail track table, Classic skin — the same routes with `TrackRowStyle = 1`

`Entities/Track.Table.cs` + `Track.UI.cs` · Wave 4.5 / owner M · chapter 04 (ch 30 W17 crosses density × style × hidden artwork)

```
│ #   TITLE                                ARTIST              ALBUM            DATE ADDED   PLAYS   TIME   ⋯ │ 32 DIP header
│     11 px UPPERCASE, +30 tracking, TextTertiary / TextSecondary when active                                 │
├────────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 1   Sunset Boulevard  ·  Radiohead                            Neon Horizon     Sep 28       12.0M   3:41  ⋯ │ 40 DIP row (density 1)
├────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ per-row 1-DIP divider, inset padX
│ 2   Weird Fishes  🎞  ·  Radiohead        Radiohead            In Rainbows      Sep 27       30.0M   4:12  ⋯ │ inline film glyph (tier < 4)
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Classic: no art thumb ever, no zebra, no pill, square corners, 14/20 factual cells, dedicated Artist lane to tier 3,
selection = RowHover fill, EXPLICIT drawn as a word-mark pinned to the Title lane's right edge.

Four more Classic-only facts the Modern wireframes do not show:
* **The `#` header cell is EMPTY** — `set.Classic ? new BoxEl() : IndexSortCell(sort)` (`DetailTracks.cs:2400`).
  Classic has no clickable `#`, no caret slots and therefore no header route back to the original order; the Sort
  flyout's "Custom order" is it.
* **The now-playing rest state is a speaker glyph, not the equalizer** — `Icon(Icons.Volume, 13, accent)`
  (`TrackRow.cs:1071`). No top-track ★ and no chart ▲▼ glyph either: both are Modern-only arms of the same switch.
  The whole row's factual ink turns `AccentTextPrimary` instead (`TrackRow.cs:227-229`).
* **The trailing "…" is fully hidden at rest** (opacity 0 → 1 on row hover), and it carries no hover/press scale
  (`TrackRow.cs:975-995`). Modern's is quiet-but-present at `MoreRestOpacity` 0.45.
* **The row grid pays the full `padX`** with no skin margin (`TrackRow.cs:334-335`, `DetailTracks.cs:3491`), and the
  row's own 1-DIP divider is inset by that same `padX` (`:3594-3595`).
```

*ch 04 W22 — Classic skin (`WaveeSettings.TrackRowStyle = 1`), tier 0 @ 1060* — `04-detail-track-table.md:544-566`

What must not be lost (ch 04 §0):

- Header and rows are built from ONE `TrackSize[]` and ONE `ColumnSet` with the same `ColGapFor(tier)` and `padX`
  (`DetailTracks.cs:2429-2434`, `TrackRow.cs:330-337`); a heading must sit over its own values at every width, skin
  and density. Nothing else may compute a lane width.
- Two ladders decide lanes, in order, and only subtract: the width TIER (860/720/560/440/340/300) then the
  identity-first RELIEF ladder Plays → BPM·Key → Added by → Date added → Album → Artist → thumb → ♥ until Title clears
  120 (`DetailTrackTableRules.cs:154-216`). `#`, Title, Duration and the trailing lane never yield.
- A breakpoint cross re-skins realized rows in place — the tier is NOT in the list key (`:1074-1080`) — so the
  viewport survives opening the rail; only filter / density / reset remount and throw scroll away (`:1089-1090`).
- Zebra is `DisplayIndex() % 2 != 0`; the ONLY selection cue is the 3×16 accent pill (Classic: fill tint instead)
  (`:3512-3532, :3582-3589`); rows are zero-allocation on a steady scroll frame; the marquee exists on the now-playing
  row ONLY (`:2695-2706`).
- The command bar promotes, never shrinks: icon+label commands EVICT into "…" with 16-DIP hysteresis and a latch
  while search is open (`DetailTrackCommandBarLayout.Resolve`, `:1950-1958`); search is a 66-DIP disclosure that
  reflows the row over 260 ms (`:226-260`).
- The first membership landing is a load and must NOT choreograph; later landings FLIP survivors and fade adds with
  a 20 ms/row stagger capped at 8 (`:862-873, :1546-1598`). One drawer at a time keyed by ROW identity
  (`MembershipDiff.RowKey`). Every refusal owes a sentence (`:1252-1266, :1346-1354`).

### Track row — every list surface

`Entities/Track.UI.cs` (the ROW: grid + 11 lanes, the `#` state machine, both skins, cells, `ArtCard`, 14 call-site
variants) + `Track.cs` (CORE: lane table, ladders, sort cycle, filter model, reorder rules, number formats) · Wave 4.5 /
owner M · chapter 01 (31 frames; W2-W6 hovered / playing / buffering / chart / not-yet-out, W7 the density ladder, W8
Classic, W9 the width ladder, W12b the third "plain" skin, W13 the selection command bar, W14 the drawer — Wave 5's
`Track.Drawer.cs`, W16 `ArtCard`, W17/W18 the Home and Artist eager rows)

```
  margin 8 ┆ pad 8                                                                        pad 8 ┆ margin 8
 ┌─────────┼────────────────────────────────────────────────────────────────────────────────────┼─────────┐
 │         │ #    ♥   [art]  Title ...................  Album .........  Added by ....  Date ..  │         │
 │         │ 28   28   32     150 (star 1.0)             112 (star .75)   132            88      │         │
 │         │                                                                                     │         │
 │         │  ...  Plays  0:00   ▣    ⌄                                                          │         │
 │         │        52     52    28   26                                                         │         │
 └─────────┼────────────────────────────────────────────────────────────────────────────────────┼─────────┘
   row skin: MinHeight 48 · Corners 6 · Margin L/R 8 · Border 1 (zebra rows only) · ZStack
   grid:     Padding L/R (PadX 16 − RowInset 8) = 8 · ColGap 12 · RowHeight 48

 laid out (one line, proportional; ┊ = 12 DIP column gap):
 ┌──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┐
 │ 1┊ ♡┊[▨] ┊Midnight City         ┊Hurry Up...  ┊ (◉) christos   ┊3 days ago┊ 1.85B┊  4:03┊ ▣ ┊ ⌄│
 │  ┊  ┊    ┊M83                   ┊             ┊                ┊          ┊      ┊      ┊   ┊  │
 └──┊──┊────┊──────────────────────┊─────────────┊────────────────┊──────────┊──────┊──────┊───┊──┘
   28  28  32          150               112            132           88       52     52    28  26

 Title   BodyStrong 14/20/600 TextPrimary · NoWrap · CharacterEllipsis · MinWidth 0
 Subline Caption 12/16/400 TextSecondary (artist links) · gap 4 · own row, column Gap = Spacing.XXS (2)
 Album/Date/By  Caption 12/16 TextSecondary   Plays  Caption 12/16 TextTertiary   Duration  Caption 12/16 TextSecondary
 ♥  28x28 circle, Icon Heart 14 TextTertiary (outline) / HeartFill 14 AccentTextPrimary (saved)
 ▣  the trailing Video/More lane — Icon Movie 13 TextTertiary at rest
 ⌄  ExpandChevron 24x24, Icon ChevronRight 12 TextSecondary (closed)

 WHY 150 / 112 AT 880 (and why this does not contradict W9). This set has the BPM·key lane OFF (tempo is opt-in), so
 its fixed total is 28+28+32+132+88+52+52+28+26 = 466 over 11 columns → min width 466+120+90 +10·12 +2·16 = 828.
 At 880 the star pool is 880 − 32 pad − 120 gaps − 466 = 262, split 1 : 0.75 → Title 149.7, Album 112.3. W9's worked
 example adds the 80-DIP Tempo lane, which is exactly what pushes the same set's minimum to 920 and relieves Plays
 at 880. Both are right; they are different lane sets.
```

*ch 01 W1 — Detail table row, Modern, tier 0, density Default (48), playlist @ 880 DIP (right column)* — `01-track-row.md:246-277`

- One cell, every surface: `TrackRow.Grid` (`TrackRow.cs:198`) is the single definition; album, playlist, Liked, show,
  Recents, the artist drawer and Home's top tracks all call it, varying only `ColumnSet` and container skin.
- The `#` cell is a state machine: number / live equalizer / settled equalizer / star / spinner / number+chart glyph;
  on ROW hover it fades to a 24 DIP play-or-pause transport (`:1092-1105`).
- The heart is painted at rest on every row (filled `AccentTextPrimary` saved, outline `TextTertiary` not,
  `:935-957`); the "…" is quiet, never absent (`MoreRestOpacity = 0.45f`, `:989-997`, user report 2026-08-10).
- Two type steps per row: `BodyStrong 14/20/600` title, `Caption 12/16/400` every factual cell (Classic: 14/20);
  unknown is an em dash, never a zero (`Dash = "—"`, `:166`); a not-yet-out row prints its date in the duration
  lane and dims the title column to 0.45 (`:267, :306-308`).
- Now-playing is carried by CONTENT (accent title + equalizer), never by a wash; a wide row never press-scales
  (`:379-383`); the equalizer stops ticking when unseen and settles to a still non-uniform shape under reduced
  motion (`EqualizerMotionPolicy.cs:26-27`).
- THREE skins: Modern inset pill, Classic hairline, and PLAIN — the hero layout's stacked flow drops pill, border
  and zebra (`DetailTracks.cs:3484`); hide-artwork removes the art LANE, not just the picture (`DetailTrackTableRules.cs:38`).

---

## 8. Wave 5 — entity UI and pages (owners M, N, O, P, Q)

Gate (G2 stage 1): `--fake` renders every route's LOADED state from `Entities.SeedFake()` — including `home-customize`,
`home-section:`, `browse-section:`, `disco:`, `prerelease:`, `local`, `history`, `recents`, `sidebar-customize` — plus the
surface half (a PLAYING bar, rail/NPV/deck, queue, lyrics, friends, notifications, device picker); `ReuseGuard` silent;
zero allocation on a scroll frame.

### Album — `album:<uri>` (owner M)

`Entities/Album.cs`, `Album.UI.cs`, `Album.Page.cs` (the two arms, the release panel, the trailing block, **the whole
`prerelease:` surface**) · Wave 5 / owner M · chapter 05 (20 frames; W2 the rail's exact geometry, W3/W4 mid-narrow
and collapsed, W5/W6 the vertical hero row-flow at 560 and stacked at 400, W11 "About this release" in every state,
W12 the trailing sections, W14 the drawer, W19 the album drawer on the artist page)

```
├────────────────────────── 1040 page ─────────────────────────────────────────────────────────────────────────────────┤
┌── rail 280 (Fill=Tok.FillLayerDefault, own ScrollView) ──┬─┬── right column (Grow, MinWidth 300) ───────────────────┐
│ pad 16 L / 8 R / 24 T / 24 B      gap 14 between rows    │g│ chrome padX 16, padTop 8                              │
│ ┌────────────────── cover 256×256 ─────────────────────┐ │r│ ┌─ command bar (CommandBarSurface h 44, pills 32) ──┐ │
│ │  Radii.Card 8 · Elevation.Card · saturation 1.18     │ │i│ │ [▷ Play next ▾] [⤮ Shuffle] │ [⇅ Sort][▤ Row size]│ │
│ │  decodePx 256 · Draggable(whole album)               │ │p│ │                              [☰ Select]  [🔍 Find]│ │
│ └──────────────────────────────────────────────────────┘ │ │ └───────────────────────────────────────────────────┘ │
│  ALBUM · 2019            Caption 12/16/600 +30 tracking  │6│ ┌ column header h 36 ───────────────────────────────┐ │
│                          Tok.TextTertiary, 1 line        │ │ │  #   TITLE                       PLAYS    ⏱      │ │
│  Random Access            Title 40/52/600 display face   │ │ ├───────────────────────────────────────────────────┤ │
│  Memories                 (28/36 when window H < 900)    │ │ │  1  ♡  Give Life Back to Music   12,345,678  4:35 │ │ 48
│                           −20/1000 tracking, ≤3 lines    │ │ │  2  ♡  The Game of Love           9,876,543  5:22 │ │ 48
│  ((●))((●))(●) +7  Daft Punk, …   face pile 32 frames    │ │ │  ★  ♡  Get Lucky                 1.85B       6:09 │ │ zebra
│                       names 14/700 AccentTextPrimary     │ │ │  4  ♡  Within                     3,210,987  3:48 │ │
│  ┌─ Play ─────┐ ( ♡ ) ( ⤴ )     pill h36 r-full          │ │ │  …                                                │ │
│  │ ▶  Play    │  40   40        gap 12, wrap as a unit   │ │ │                                                   │ │
│  └────────────┘                                          │ │ │  (rows continue; the list is Grow=0 inside the    │ │
│                                                          │ │ │   trailing scroller — the page scrolls, not it)   │ │
│  About this release      Eyebrow, TextTertiary           │ │ │                                                   │ │
│  ┌──────────────┐┌──────────────┐   gap 8               │ │ ├─── trailing band (one skeleton region) ───────────┤ │
│  │ 13           ││ 74 min       │   StatTile 18/800     │ │ │  WATCH THE OFFICIAL VIDEO  (short releases only)  │ │
│  │ Songs        ││ Length       │   caption 11          │ │ │  About the artist   [84 avatar] Daft Punk  [♡ Fol]│ │
│  └──────────────┘└──────────────┘   r4 FillCardSecondary│ │ │  Fans also like     (chip)(chip)(chip)(chip)…     │ │
│  ┌────────────────────────────────┐  full width, wrap 2  │ │ │  More by Daft Punk  [48][row] ×5   Show all 12   │ │
│  │ May 17, 2013                   │  lines               │ │ │  Featured on        [48][row] ×5                  │ │
│  │ Released                       │                      │ │ │  Merch              [48][name ……………… €24.99]      │ │
│  └────────────────────────────────┘                      │ │ │  Similar albums     [48][row] ×5                  │ │
│  Label: Columbia          11 px TextTertiary, gap 3      │ │ └───────────────────────────────────────────────────┘ │
│  ℗ 2013 Daft Life Ltd.    wrap ≤4 lines                  │ │                                                       │
│  © 2013 Daft Life Ltd.                                   │ │                                                       │
│  [ ♪ Other versions ▾ ]   DropDownButton (when present)  │ │                                                       │
└──────────────────────────────────────────────────────────┴─┴───────────────────────────────────────────────────────┘
```

*ch 05 W1 — Fully loaded, two-column @ page 1040 DIP (mode 0, rail 280)* — `05-album.md:187-220`

### Prerelease album — `prerelease:<uri>` (resolves into the album surface)

`Entities/Album.Page.cs` — the route, the kind-138 resolve, the countdown card, the pre-save heart swap, the greyed pending
rows, the "N of M songs" tile · Wave 5 / owner M · chapter 05

```
rail (or, on the vertical arm, after the rows)
┌ countdown card: padding 12, Radii.Card 8, FillCardDefault, 1px StrokeCardDefault, gap 12 ┐
│  ( ◜ )   Coming soon                     eyebrow in the page ACCENT                      │
│  ring34  ┌──────┐┌──────┐┌──────┐┌──────┐  wrap-grow tiles, gap 4                        │
│          │ 12   ││ 04   ││ 37   ││ 09   │  value 18/800 · caption 11                     │
│          │ DAYS ││ HRS  ││ MIN  ││ SEC  │  (2×2 in a narrow rail, one row when wide)     │
│          └──────┘└──────┘└──────┘└──────┘                                                │
└──────────────────────────────────────────────────────────────────────────────────────────┘
CTA cluster:   [▶ Play]   ( ♡ → saves spotify:prerelease:… )   ( ⤴ )

track table (partly released album)
│  1  ♡  Out Now Track                      1,234,567   3:41 │  full opacity
│  2  ♡  Pending Track                          —       4 Sep│  title column Opacity 0.45, Plays "—",
│  3  ♡  Pending Track                          —       4 Sep│  duration lane = ShortDate("4 Sep" /
│        ↑ hover play suppressed on these rows                │  "4 Sep 2027"), row click refuses to play
```

*ch 05 W13 — Prerelease / upcoming album* — `05-album.md:539-555`

What must not be lost (ch 05 §0):

- The wide album page is TWO COLUMNS, not a hero over a list: a 280 rail (180-480) with its own scroller and
  `Tok.FillLayerDefault` fill beside the table for the whole scroll (`DetailRail.cs:290-298`).
- The rail never shows a meta line — "About this release" states Songs/Length/Released instead (`:198-200`), in ONE
  shape from first paint: Songs + Length row 1, Released alone on row 2, Label/℗/© as 11-px note lines, gated on the
  Full rung so it appears once, complete (`DetailTrailing.cs:196-273`, `DetailPage.cs:737-740`).
- The billed-artist line is a stacked face pile: up to 4 × 28 DIP avatars, `+N = allDistinct − billed.Count`
  (`ArtistFacePile.cs:53-54`). **Known 0.2.9 defect to fix, not port**: the overflow is computed against
  `billed.Count` while only `min(MaxVisible, billed.Count)` faces draw (`:55` vs `:111`) — two artists vanish.
- An album that is not out yet says so three ways — countdown card, pending rows at 0.45 with the date in the
  duration lane, "8 of 12" in the Songs tile — all from wall-clock predicates that expire themselves; the heart saves
  the PRERELEASE entity, keyed on the target (`DetailRail.cs:229-234`).
- The trailing band is reserved from mount and swaps exactly once (`DetailTrailing.cs:74-106`); sections are
  VERTICAL, capped at 5 rows, "Show all N" lengthens the stack in place (`:436-489`). Album rows carry no thumb and
  no Album column but do carry Plays and a top-track star (`DetailConfig.cs:203-207`).
- The merch row (W1: `Merch [48][name … €24.99]`) has no table in the plan — **open question Q4**.

### Show / podcast — `show:<uri>` (owner M)

`Entities/Show.UI.cs`, `Show.Page.cs` (the show arm of the shared frame — episodes instead of tracks), `Episode.UI.cs`, `Show.cs`,
`Episode.cs` · Wave 5 / owner M · chapter 09 (21 frames; W2/W3 modes 1 and 2, W4 the vertical arm at 700 — `showToolbar =
FALSE`, the header is `DetailRail.BuildHeader` not the hero — W5 rail collapsed, W7 every episode-row state, W8 "Listen next",
W9 empty filter + load-more)

```
◀ page content 1040 ─────────────────────────────────────────────────────────────────────────────────────────────▶
┌ rail 280 ────────────────────┬╥┬ right column ≈ 754 (MinWidth 300) ────────────────────────────────────────────┐
│ pad L16 T24 R8 B24 · gap 14  │║│ ScrollView · pad L16 T12 R16 B96 · gap 12                                     │
│ ┌──────────────────────────┐ │║│ ┌─ Toolbar · Dir 0 · gap 12 · margin-bottom 4 ──────────────────────────────┐ │
│ │                          │ │║│ │ [ All │Unplayed│In progress│Played ]          ← spacer →  [ Newest│Oldest ]│ │
│ │      cover 256×256       │ │║│ └───────────────────────────────────────────────────────────────────────────┘ │
│ │      r8 · Elevation.Card │ │║│  Listen next                          ← RailHeader · Ui.Subtitle 20/28/600     │
│ │      saturation 1.18     │ │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ │                          │ │║│ │ ┌────────┐  CONTINUE LISTENING                                 ┌────────┐ │ │
│ └──────────────────────────┘ │║│ │ │ 72×72  │  Episode title, one line, ellipsised         15/700 │ ▶ Resume│ │ │
│ Podcast            ← eyebrow │║│ │ │  r8    │  Mar 14 · 47 min                              12/Sec └────────┘ │ │
│                              │║│ │ └────────┘  ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▃▃▃▃▃  3-DIP accent rule    r999 · h36      │ │
│ The Show Name                │║│ └─ pad L12 T12 R16 B12 · gap 16 · r8 · FillCardSecondary · 1px StrokeCard ───┘ │
│ Wraps to at most 3 lines     │║│  Episodes                             ← RailHeader · Ui.Subtitle 20/28/600     │
│ 28/36 (40/52 if winH ≥ 900)  │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│ display face · tracking −20  │║│ │ ┌──────┐ #128 · The Build Trap                               ⎧ 40 ⎫       │ │
│                              │║│ │ │56×56 │ In this episode: notes, tangents and a few hard-won ⎪ ▶  ⎪ accent│ │
│ ┏━━━━━━━━━┓ ⬡  ⬡  ⬡          │║│ │ │ r8   │ lessons.                                 12/16 Sec ⎩ r20⎭       │ │
│ ┃ ▶ Play  ┃ 40 40 40         │║│ │ └──────┘                                                                  │ │
│ ┗━━━━━━━━━┛ ♥  ⤴  ⋯          │║│ │ Mar 14 · 47 min                                        12 · TextTertiary  │ │
│  h36 r999   (OwnerMenu is    │║│ └─ pad 12 · gap 8 · r8 · 1px StrokeDividerDefault ────────────────────────────┘│
│  an empty box for a show)    │║│ ┌───────────────────────────────────────────────────────────────────────────┐ │
│                              │║│ │ ┌──────┐ #127 · Latency, Honestly                          ⎧ 40 ⎫       │ │
│ The show blurb, rich text,   │║│ │ │56×56 │ Two lines of description, clamped and ellipsised … ⎪ ▶  ⎪       │ │
│ clamped to 6 lines (3 when   │║│ │ └──────┘                                                    ⎩    ⎭       │ │
│ winH < 760). 12 · Secondary. │║│ │ Mar 7 · 33 min · In progress          12/600 AccentTextPrimary            │ │
│                              │║│ │ ▂▂▂▂▂▂▂▂▂▂▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃▃  h3 r2 FillSubtleTertiary       │ │
│ (rail scrolls on             │║│ └───────────────────────────────────────────────────────────────────────────┘│
│  FillLayerDefault)           │║│                                    ⋮  gap 12 between rows                     │
│                              │║│                     ┌──────────────────────┐                                  │
│                              │║│                     │  Load more episodes  │  Standard pill, row margin-L 8   │
└──────────────────────────────┴╨┴─────────────────────└──────────────────────┘──────────────────────────────────┘
   railW 280 (persisted 180–480)  grip Splitter.StripW        ▲ bottom pad 96 = PlayerDock.Reserve 72 + 24
```

*ch 09 W1 — Show page, two-column mode 0, fully loaded @ 1280 (page ≈ 1040)* — `09-show-episode-module.md:289-323`

- An episode is a CARD, not a table row: bordered, 8-DIP rounded, its own hairline, 12-DIP padding; no `#`, no zebra,
  no shared grid, no column header (`EpisodeList.cs:202-232`). A show page rendering `DetailTracks`-style rows has lost
  the surface.
- Two clamped lines of prose per episode (title 14/20 **700**, description 12/16 secondary, both `MaxLines = 2`,
  `:219-224`); the play affordance is a 40×40 accent circle, always visible, emphatic tier (`:247-253`).
- Resume progress is a 3-DIP rule (`:237-245`), only when `pct > 0.01`; "Listen next" is the page's second hero, picked
  from ALL episodes as the most-progressed in-progress one (`:78-81, :155-186`).
- Four status pills and a Newest/Oldest pair on opposite ends of one row (`:137-153`); "Load more episodes" is a pill at
  the END of the list gated on the paging cursor, never an infinite-scroll sentinel (`:65-72, :126-135`).

### Artist — `artist:<uri>` (owner N)

`Entities/Artist.cs`, `Artist.UI.cs` (+ `+Artist.UI.Chart.cs` named on day one), `Artist.Page.cs` · Wave 5 / owner N ·
chapter 08 (29 frames; W2 the Medium hero at 700, W4 Narrow at 340, W4b no image, W5/W6 skeleton and reveal, W7 the
context band engaged, W9-W11 the top-tracks chart stacked / row anatomy / pager, W14-W17 the discography facet and album
drawer, W18 biography, W21 the gallery lightbox, W28 the pivot under pressure)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ PHOTOGRAPH · full-bleed W×440 · ImageFit.Cover · FocusX .62 FocusY .34 · rest ScaleX/Y 1.05 (inner frame 1.08, OffsetY −17.6)                 │
│ ┌── veil: GradientRight over the whole 1160×440 — α .96 @0 → .92 @0.30 → .35 @0.62 → 0 @1.0, colour = Lerp(FillLayerDefault, wash, .16/.24)   │
│ │                                                                                                                                            │
│ │ ⟵36⟶ ✓ Verified Artist            ← InfoBadge.Icon(Accept, accent) 16 + Caption 12/16 TextSecondary, row Gap 8                    24 top pad│
│ │                                                                                                                        ↑                   │
│ │       Conan Gray                   ← ArtistDisplay 84/96/700, CS −28, MinSize 68, MaxLines 2, TextPrimary          Justify                  │
│ │                                                                                                                     Center                 │
│ │       Nobody has tapped into the thoughts and feelings of this era quite like Conan Gray.   ← Body 14/20, 2 lines, ellipsis, TextSecondary  │
│ │                                                                                                                        │                   │
│ │       #419 in the world    20,577,457 monthly listeners    13,357,890 followers   ← BodyStrong accent · Body · Body, row Gap 16             │
│ │                                                                                                                        ↓                   │
│ │       ╭─────────────╮ ╭────╮ ╭──────────────╮ ╭────╮      ← Play 36 capsule (accent fill) · Shuffle 36○ · Follow 36 pill · Radio 36○        │
│ │       │  ▶  Play    │ │ ⤨  │ │  ♡  Follow   │ │((· │        Gap 8, AlignItems Center. Order: Play, Shuffle, Follow, Radio.      24 bottom pad│
│ │       ╰─────────────╯ ╰────╯ ╰──────────────╯ ╰────╯                                                                                        │
│ └── photo EdgeFade Bottom, band clamp(440×.28,120,180) = 123 ──────────────────────────────────────────────────────────────────────────────  │
├──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP StrokeDividerDefault
│⟵36⟶                                                                                                                                    ⟵36⟶ │ inner top pad 12
│  ▌ Top tracks                                                                                        ‹  ● ●  ›   ← AccentHeader + ChartPager  │
│  ──                     ← AccentRule 20×2 accent, top margin 2 (header column Gap 2)                                                         │
│                                                                                                                                              │
│  ┌───── chart column: Grow 2, 712 ─────────────────────────┐ ⟵20⟶ ┌──── featured rail: Grow 1, 356 ────────┐                                 │
│  │ 1  [44] Heather                    ♥  3:18 │ 6 [44] …   │      │  ╭──────────────────────────────────╮  │                                 │
│  │       E · feat. X +2 · 2.4B plays          │            │      │  │ (32) Conan Gray                  │  │ ArtistPick, 8 r, FillCardDefault│
│  │ 2  [44] The Cut That Always Bleeds ♡  3:51 │ 7 [44] …   │      │  │      Artist pick                 │  │ + accent wash .16→.05→0         │
│  │ 3  [44] Memories                   ♡  4:08 │ 8 [44] …   │      │  │  “Out now — give it a listen.”   │  │ PickQuote 28/36/400, ≤4 lines   │
│  │ 4  [44] Vodka Cranberry            ♡  4:05 │ 9 [44] …   │      │  │  ┌────────────────────────────┐  │  │                                 │
│  │ 5  [44] Maniac                     ♡  3:05 │10 [44] …   │      │  │  │  photo 150 tall, cover     │  │  │ only when a wide image exists   │
│  └── rows 56 · gap 12 both axes · cell 350 ────────────────┘      │  │  └────────────────────────────┘  │  │                                 │
│                                                                   │  │  [44] Wishbone Deluxe   ╭─────╮  │  │ foot row: 44 cover · title ·    │
│                                                                   │  │       Album             │ ▶Play│  │  │ kind · Play (or Pre-save)      │
│                                                                   │  ╰──────────────────────────────────╯  │                                 │
│                                                                   └────────────────────────────────────────┘                                 │
│                                                    ⟵ section gap 32 ⟶                                                                        │
│  ▌ Latest release                                                                                                                            │
│  ──                                                                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│  │ [72] Album · Jun 12, 2026 · 17 tracks           ← Eyebrow 12/16/600 CS30 TextTertiary                    ╭────────╮ ╭──────╮            │  │
│  │      Wishbone Deluxe                            ← Ui.Subtitle 20/28/600, 1 line                          │ ▶ Play │ │ View │            │  │
│  └────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘  │
│   Pad 12 all · r8 · FillCardDefault · 1px StrokeCardDefault · Gap 12 · draggable (Album)                                                      │
│                                                    ⟵ section gap 32 ⟶                                                                        │
│  ▌ Albums  6 releases              ← facet header 40 tall, pinned at 56; spine 3×22 r-pill accent, Gap 8                                      │
│  ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐   cols = floor((1088+16)/196) = 5 · cellW = (1088−64)/5 = 204.8 · card h = 254.8          │
│  │ 204.8 │ │       │ │       │ │       │ │       │   row pitch = cellW + 70 (CardChrome 50 + RowGap 20) = 274.8 · column gap 16             │
│  │ cover │ │       │ │       │ │       │ │       │                                                                                           │
│  └───────┘ └───────┘ └───────┘ └───────┘ └───────┘                                                                                           │
│   Title 14/20/600                                                                                                                            │
│   2026 · 17 tracks 12/16                                                                                                                     │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                                                                    bottom pad = PlayerDock.Reserve 72 + 40
```

*ch 08 W1 — Wide, fully loaded, scroll 0 @ W=1160 (hero 440, gutter 36, copy max 1120 → 1088)* — `08-artist-and-discography.md:287-339`

### Artist, compact hero — the narrow arm (stacked, no veil)

`Entities/Artist.UI.cs` · Wave 5 / owner N · chapter 08

```
┌───────────────────────────────────────────────────┐
│ PHOTOGRAPH 500×200 — EdgeFade Bottom band 120     │  no veil: nothing is set on the picture
│                                                   │
├───────────────────────────────────────────────────┤  identity band 252, Justify End, Pad 16/12/16/20
│                                                   │
│ ✓ Verified Artist                                 │  Gap 8 between blocks
│ Conan Gray                ← ArtistCompactTitle    │  32/40/700, CS −12, MinSize 28, ≤2 lines
│ Nobody has tapped into the thoughts and feelings  │
│ of this era quite like Conan Gray.                │
│ #419 in the world  20,577,457 monthly  13,357,890 │  meta stays ONE horizontal row at Compact
│ ╭──────────╮ ╭───╮ ╭───────────╮ ╭───╮            │
│ │ ▶ Play   │ │ ⤨ │ │ ♡ Follow  │ │((·│            │  one 36 row
│ ╰──────────╯ ╰───╯ ╰───────────╯ ╰───╯     ↓20 pad│
└───────────────────────────────────────────────────┘
```

*ch 08 W3 — Compact hero @ W=500 (360 ≤ W < 600; stacked: photo band 200 + identity 252 = 452; gutter 16; copy 468)* — `08-artist-and-discography.md:359-374`

### Discography — `disco:<uri>` (owner N)

`Entities/Artist.Discography.cs` (the facet page, virtual grid, album drawer, verdict, era bands — in the route table by plan §9.5)
· Wave 5 / owner N · chapter 08

```
┌──────────────────────────────────────────────────────────────────────┐
│⟵36⟶ Conan Gray  ›  Albums                        ← BreadcrumbBar      │  content Gap 32, Pad 36/24/36/(72+36)
│                                                                      │
│ Albums                                           ← PageHero 28/36/600 │
│ ( Albums )( Singles )( Compilations )            ← SelectorBar        │
│                                                                      │
│ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐   the SAME DiscoGrid,
│ │       │ │       │ │       │ │       │ │       │   expandedTopInset 28
│ └───────┘ └───────┘ └───────┘ └───────┘ └───────┘   (no sticky facet band here)
│  Title                                              pages of 60 (DiscoVc)
│  2026 · 17 tracks                                   total probed with limit 0 ⇒ shimmer-up-to-N at once
└──────────────────────────────────────────────────────────────────────┘
 breadcrumb column Gap 8. SelectorBar takes a FRESH Signal<int>(FacetIndex(kind)) every render — the ROUTE, not the
 control, is the source of truth; onChange navigates and the next route re-seeds it (`DiscographyPage.cs:120-125`).
```

*ch 08 W22 — Discography page (`disco:0:{artistUri}`)* — `08-artist-and-discography.md:682-697`

What must not be lost (ch 08 §0):

- The photograph is the hero and it is full-bleed (`HeaderImage ?? Image`, `ImageFit.Cover`, focus 0.62 / 0.34, rest
  scale 1.05 in a 1.08 frame lifted −4 %, `ArtistPage.Hero.cs:286-289, :345-362`); no avatar, no framed portrait.
- Wide/Medium set the copy ON the picture behind a horizontal veil; Compact/Narrow put the picture on top and the copy
  below — `ArtistHeroVeilAxis`, never a scrim tweak (`ArtistHeroLayout.cs:85-89`). The name is display type: 84/96/700
  · 48/60/700 · 32/40/700, two lines max (`WaveeType.cs:155-187`).
- Scroll collapses the hero into the 56-DIP text-chrome band with a section PIVOT — title · pivot · Play/Follow as
  words, one hairline, no fill, no avatar (`ArtistCompactBar.cs`, `ContextBand.cs:71-96`); the active item carries a
  2-DIP accent underline and nothing else does.
- Top tracks is a two-column paged CHART capped at 5 rows with a `‹ ●● ›` pager; row key =
  `"row:" + uri + ("|art"|"|noart") + ("|classic"|"|modern")` because those settings change the row's child count
  (`ArtistPopular.cs:16-46, :246`).
- Clicking a discography card opens the album's tracks in place, full width, after that card's row, with a 16×8 caret
  and a 2-DIP accent border, parked under the sticky facet header; 200 ms, no shimmer for a warm album
  (`ArtistPage.AlbumExpand.cs:398-687`).
- One artist-tinted wash bleeds from the hero and stops at `heroHeight + 96` (`CoverPaletteLeaves.cs:171-196`);
  sections are dividers + accent-ruled headers, never cards; nothing pops in — where 0.2.9 still violates this
  (extras after Ready, the chart growing 10→50, late play counts) §7 says 0.3 must fix it.

### Concerts hub, artist schedule, concert detail — `concerts`, `artist-concerts:<id>`, `concert:<id>` (owner N)

`Entities/Concert.Page.cs` (hub, filter bar, schedule, month board, detail, shy pill, ticker, preloader) + `Concert.UI.cs` +
`Concert.cs` (807 ported pure rules) · Wave 5 / owner N · chapter 17 (23 frames; W6-W10 the filter bar's when/where
flyouts and location picker, W12 the hub at 600, W13 the artist schedule wide, W14 narrow, W15/W16 the month board and
the shy pill, W20 detail narrow)

```
╔══════════════════════════════════════════════════════════════════════════════╗ ← shell masthead overlay (paints nothing)
║ (84 DIP reserve spacer, then 24 DIP)                                          ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║ ← gutters 36 | 36
║ │ LIVE MUSIC                                          Eyebrow 12/16/600 acc │ ║   card: r8, FillCardDefault, 1px
║ │ Concerts                                            PageHero 28/36/600    │ ║   StrokeCardDefault, pad (20,16,20,16)
║ │ Live shows, festival dates, and artists playing near you.   Body 14/20 sec│ ║   gap 4
║ └──────────────────────────────────────────────────────────────────────────┘ ║
║                                       gap 16                                  ║
║ ┌──────────────────────────────────────────────────────────────────────────┐ ║   bar: r8, FillLayerDefault, 1px
║ │ FILTER BY                                              9,191  events      │ ║   StrokeSurfaceDefault, pad (12,8,12,8)
║ │ ┌────────────────────┐ │ ┌────────────┐ ┌──────────────┐ │ ┌───┐┌──────┐ │ ║   card column gap 4; caption ROW
║ │ │📍 New York City  ▾ │ │ │📅 Dates   ▾│ │ This weekend │ │ │All││ Rock │ │ ║   gap 8 (eyebrow · Grow spacer ·
║ │ └────────────────────┘ │ └────────────┘ └──────────────┘ │ └───┘└──────┘ │ ║   ticker); scroller H 44, outer row
║ └──────────────────────────────────────────────────────────────────────────┘ ║   gap 12, group inner gaps 8,
║                                       gap 16                                  ║   dividers 1×20
║ NEAR YOU                                             ← accent eyebrow, gap 8  ║   PagedShelf measured, maxItems 16
║ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐   ◀ ▶      ║   inner W = 1100 − 72 = 1028
║ │ [cover]│ │        │ │        │ │        │ │        │ │        │            ║   cardW = Fit(1028,150,200,12):
║ │ square │ │        │ │        │ │        │ │        │ │        │            ║   cols ⌊1040/162⌋ = 6 → 161.3
║ │ THU AUG│ │        │ │        │ │        │ │        │ │        │            ║   tile H = cardW + 70 = 231
║ │ Title  │ │        │ │        │ │        │ │        │ │        │            ║
║ │ Venue ·│ │        │ │        │ │        │ │        │ │        │            ║
║ └────────┘ └────────┘ └────────┘ └────────┘ └────────┘ └────────┘            ║
║                                       gap 20                                  ║
║ RECOMMENDED FOR YOU                                                           ║
║ ┌────────┐ ┌────────┐ …                                                       ║
║                                       gap 20                                  ║
║ PLAYLISTS FOR THE SCENE                                                       ║
║ ┌────────┐ ┌────────┐ …   (playlist promo: cover + 2-line title + source)     ║
║                                       gap 20                                  ║
║ ALL EVENTS                                                                    ║
║ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐      ║   LazyGrid minCol 240 gap 12
║ │               │ │               │ │               │ │               │      ║   cols = ⌊(1028+12)/252⌋ = 4
║ │               │ │               │ │               │ │               │      ║   cellW = (1028−36)/4 = 248
║ │ FRI SEP 5 · 20│ │               │ │               │ │               │      ║   rowH  = 248 + 86 = 334
║ │ Event title   │ │               │ │               │ │               │      ║   (tile 248+70 = 318 + a 16 gutter)
║ │ Venue · City  │ │               │ │               │ │               │      ║
║ └───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘      ║
║ … virtualized: only the scroll window ±3 rows is realized                     ║
║ (bottom padding 72 player reserve + 36)                                       ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

*ch 17 W1 — Hub, fully loaded @ content 1100 (1 char ≈ 14 DIP)* — `17-concerts.md:258-300`

```
║ (84 + 40 spacer)                                                              ║  gutters 32 | 32
║ ┌────────────────────────────────────────────────┐  ┌──────────────────────┐  ║  row gap 20, AlignItems Start
║ │ copy (Grow .56, pad 28)      │ media (Grow .44)│  │ TICKETS       accent │  ║  ticket rail Width 320 Shrink 0
║ │ CONCERT                      │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │  │ ┌──────────────────┐ │  ║  hero H 320
║ │ Arctic Monkeys               │ ▓             ▓ │  │ │ Ticketmaster     │ │  ║  offer card r8, pad 12,
║ │ ⟨ Cancelled ⟩  ← status pill │ ▓             ▓ │  │ │ Available        │ │  ║  gap 4, FillCardDefault + 1px
║ │ 📅 Saturday, March 14, 2026  │ ▓             ▓ │  │ │ 45 - 75 EUR      │ │  ║  caption + cards gap 8
║ │    · 19:30                   │ ▓             ▓ │  │ │ On sale 1 Jan …  │ │  ║
║ │ 🕐 Doors open 18:30          │ ▓             ▓ │  │ │ [ Buy tickets ]  │ │  ║  Button.Accent
║ │ 📍 O2 Academy Brixton ·      │ ▓             ▓ │  │ └──────────────────┘ │  ║
║ │    London, England, UK       │ ▓             ▓ │  │ ┌──────────────────┐ │  ║
║ │ ℹ Ages 14+                   │ ▓             ▓ │  │ │ Resale partner   │ │  ║
║ └──────────────────────────────┴─────────────────┘  │ │ Tickets not      │ │  ║  no URL ⇒ quiet TrackMeta,
║                                                     │ │ available online │ │  ║  never a dead button
║ ┌────────────────────────────────────────────────┐  │ └──────────────────┘ │  ║
║ │ LINEUP                            3 artists    │  └──────────────────────┘  ║  head pad (16,12,16,12)
║ ├────────────────────────────────────────────────┤                            ║  divider 1px
║ │ (●) Arctic Monkeys                          ›  │                            ║  row MinH 60, avatar 44 r22,
║ │ (●) The Hives                               ›  │                            ║  rows box pad (8,4,8,8)
║ │ (●) Billing-text-only support               │  │                            ║  ← no chevron, not clickable
║ └────────────────────────────────────────────────┘                            ║
║ RELATED CONCERTS                                                              ║  accent eyebrow, headerGap 8
║ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ …                                 ║  cell 0 = BrowseAllCard
║ │ 🗓  📍  │ │ cover  │ │        │ │        │                                   ║  (calendar + pin glyphs,
║ │ LIVE M.│ │ FRI …  │ │        │ │        │                                   ║   30/44/22 DIP, 30% secondary)
║ │Browse a│ │ Title  │ │        │ │        │                                   ║
║ └────────┘ └────────┘ └────────┘ └────────┘                                   ║
```

*ch 17 W19 — Concert detail, WIDE (content ≥ 920, leaves < 860) @ content 1100 (1 char ≈ 14 DIP)* — `17-concerts.md:665-693`

- Accent is the DATE, never the plate: eyebrow captions, the selected filter token's fill and the 3×32 near-you rail;
  no large saturated accent block anywhere (`ConcertUi.cs:23-24, :120`).
- The hub filter bar pins under the masthead (`.Sticky(84)`) and the header card above and the feed below are clipped
  SEPARATELY at the same line (`ConcertHubPage.cs:114-144`); the when-pill fuses in ONE reused node over 260 ms
  (`Key = "when-pill"`); filter tokens never shift their own label.
- The count counts: the events figure eases 380 ms inside a FIXED 64-DIP box (`CountTicker.cs:35-56`); every tile is
  uniform (`EventTile` height = cardW + 70) and single-line clamped.
- Copy never sits on a photo: the hero is a grounded split card with a short seam gradient (`ConcertUi.cs:256-326`);
  ticket buttons are external links only, else quiet text (`ConcertDetailPage.cs:234-242`); provider local time is
  preserved; no playback anywhere, no concert is a drag source except the promo card.

### Playlist — `pl:<uri>` incl. radio / mix / blend / daylist / chart (owner O)

`Entities/Playlist.UI.cs` (editors, owner block, collaborator pile, invite + access flyout, picker, chips, insertion preview)
+ `Playlist.Page.cs` (+ **the `local` arm**, `wavee:local:all`) + `Playlist.cs` · Wave 5 / owner O · chapter 06 (30 frames;
W2 editorial / followed, W3/W4 modes 1 and 2, W6 row flow at 520, W7 the collapsed rail, W10 empty OWNED with live
recommendations, W13 the notice strip, W14/W15 inline title / description edit, W17 invite & access, W21/W22 reorder and
refusal, W24 Tune, W26 daylist + chart rows)

```
┌ rail 240 ─────────────────────┬16┬ right (grow) ───────────────────────────────────────────┐
│ pad 16 ▸◂ 8   gap 14          │gr│  chrome: command bar 44 + column header 36              │
│ ┌───────────────────────────┐ │ip│ ┌─────────────────────────────────────────────────────┐ │
│ │                           │ │  │ │ [▶ Play next|⌄] [⤨ Shuffle] [✨ Tune] │ [⇅ Sort ⌄]  │ │
│ │      cover 216×216        │ │  │ │  [▤ Row size ⌄] [☑ Select] [🔍] [⋯]                 │ │
│ │      r8, Elevation.Card   │ │  │ ├─────────────────────────────────────────────────────┤ │
│ │      saturation 1.18      │ │  │ │ #   TITLE            ALBUM     [ADDED BY][DATE] ♥ ⏱│ │
│ └───────────────────────────┘ │  │ ├─────────────────────────────────────────────────────┤ │
│ (👤24) Christos      [👥 Invite]│ │ │ 1  ▓▓ Track one                                     │ │
│                                │  │ │     Artist          Album      (av) You   3d   ♡ 3:41│ │
│ Late Night Mix            ✎    │  │ │ 2  ▓▓ Track two …                              ♥ 3:12│ │
│  ↑ DetailHero 40/52/600,       │  │ │ …  (row 48 @ density 1, PadX 16, inset 8)           │ │
│    ≤3 lines, auto-fit to 18    │  │ │                                                     │ │
│ 47 songs · 2 hr 51 min         │  │ │ ── Recommended songs                        [↻]     │ │
│  ↑ TrackMeta, ≤2 lines, w=216  │  │ │ ▓▓ Suggested track            Artist       [+] 3:02 │ │
│ ┌──────────┐ ┌──┐┌──┐┌──┐      │  │ └─────────────────────────────────────────────────────┘ │
│ │ ▶  Play  │ │♡ ││⇗ ││⋯ │      │  │                                                          │
│ └──────────┘ └──┘└──┘└──┘      │  │                                                          │
│  36 capsule   40  40  40       │  │                                                          │
│  gap 12 / inner gap 8          │  │                                                          │
│ Late-night songs I keep coming │  │                                                          │
│ back to.  (12/Secondary, ≤6 l) │  │                                                          │
│ ┌───────────────────────────┐  │  │                                                          │
│ │ THE YEARS        2014-24  │  │  │   ← LikedFacts.Panel (07-liked-songs.md), gate:          │
│ │ 2019 ▁▃▅█▅▃▁  most tracks │  │  │      LikedFacts.Has(m, BadgeStyle.OwnerRow)              │
│ └───────────────────────────┘  │  │                                                          │
└────────────────────────────────┴──┴──────────────────────────────────────────────────────────┘
```

*ch 06 W1 — Owner playlist, fully loaded, mode 0 @ page 1040 (window 1280)* — `06-playlist.md:236-264`

```
┌ page 400 ────────────────────────────────────────┐
│ pad 16 (NarrowHeroPad, <420)                     │
│ ┌──────────────────────────────────────────────┐ │  artSize = clamp(400-32, 96, 280) = 280
│ │              cover 280×280                   │ │  r8, Elevation.Card, sat 1.18
│ └──────────────────────────────────────────────┘ │
│ gap 16 (NarrowHeroGap)                           │
│ PLAYLIST · COLLABORATIVE      ← Eyebrow/Tertiary │  DetailRail.EyebrowText, OwnerRow arm: ONE of
│                                                  │   "Playlist · Collaborative" / "Playlist · Private" /
│                                                  │   "Playlist" (collaborative wins; public+solo says
│                                                  │   nothing extra) — DetailRail.cs:581-583
│ Late Night Mix                ← type PLAN size   │  DetailVerticalLayout.TitleTypeFor; the EDITABLE arm
│                                                  │   passes NO maxLines ⇒ the 2-line displayFace default
│                                                  │   (PlaylistInlineEdit.cs:463), not titlePlan.Lines —
│                                                  │   and `TitleLinesMax = 2` caps the PLAN too, so the
│                                                  │   plan's Lines is 1 or 2 and the editable arm can only
│                                                  │   ever wrap MORE, never less (see §9's corrected entry)
│ ▂▂  ← Surfaces.AccentRule 20×2 accent            │
│ (👥👤+2) 4 collaborators  [👥 Invite]            │  CollaboratorFacePile, avatars 28+2 ring, −12 overlap
│ 47 songs · 2 hr 51 min                (≤2 lines) │
│ 02:41:09  Next update at 6:00 AM   ← daylist only│  FlipCountdown Compact:false, cells 13×28, 20/300
│ 3 new entries · Sep 8              ← chart only  │
│ [ ▶ Play ] [⤨] [♡] [⇗] [⋯]        gap 8, wrap   │  36 capsule + four 32×32 satellites
│ Late-night songs I keep coming back to.  (≤4 l)  │
├──────────────────────────────────────────────────┤
│ toolbar row (pad 16/8/16/4)                      │
│ # TITLE … (tier 6 table)                         │
└──────────────────────────────────────────────────┘
```

*ch 06 W5 — Vertical hero, stacked flow @ page 400 (`rowFlow` false)* — `06-playlist.md:306-334`

- One page, six identities, zero layout forks: owner, collaborator, collaborative-follower, editorial, chart and
  radio/mix/daylist all render `DetailConfig.Playlist` (`DetailConfig.cs:196-201`); only capability-gated affordances
  appear and disappear.
- The edit gate is ONE trio — `Editable` ×6, `EditableMetadata` ×8, `Live` ×3 call sites across 5 files
  (`PlaylistInlineEdit.cs:77-84`) — so "a notice mounts ⇒ every edit affordance disappears in the same frame" is one
  fact; the two owner affordances carry a second gate, `SpotifyEditsLive(svc)` (`:65-66`) — absent under `--fake`.
- A notice never un-renders the page (rows, cover and scroll stay; one `InfoBar` strip); "not loaded yet" and "empty"
  are different pictures — shimmer `"00 songs · 0 hr 00 min"` vs "Nothing here yet"; an empty OWNED playlist opens on
  "Recommended songs" (`DetailTracks.cs:958-994`).
- Inline title/description edit is an `AnimatedSwap` inside ONE node (300 ms `CardResize`), status chips live in a
  trailing row so the title's measure is invariant (`PlaylistInlineEdit.cs:106-110, :563-582`).
- A same-list reorder never dims the app and never re-keys under the pointer (`PlaylistReorderDefer.TryHold`); every
  refusal is a sentence; optimistic then honest (`PlaylistEditErrorKinds.KeyFor`); the accent is the cover's, never
  the page kind's (`DetailShell.cs:304-310`).

### Liked Songs — `liked` (owner O)

`Entities/User.Page.Liked.cs` (the page's composition of the shared frame) + `User.Cover.cs` (nine treatments, picker + flyout)
+ `User.Facts.UI.cs` (the facts bento) + `User.Liked.cs` + `User.Facts.cs` (CORE, `LikedFactsRules` verbatim) · Wave 5 /
owner O · chapter 07 (24 frames; W2/W3 the rail at its floor and collapsed, W4 the vertical hero at 520 with the facts
footer, W5-W13 the nine covers on the 304 canvas, W15 the cover-style flyout, W16 the facts shimmer, W17 the blend
card open, W19 empty library, W20 a 10 000+ library)

```
│◀ rail 240 ───────────────────────────▶│▮│◀ right column (Grow) ─────────────────────────────────────▶│
┌───────────────────────────────────────┐ ┌──────────────────────────────────────────────────────────────┐
│ pad 16 L / 8 R, 24 top, gap 14        │ │ Toolbar  [▶ Play][⇄ Shuffle] │ [⇅ Sort][≡ Rows]   [Find  ⌕▽]│
│ ┌───────────────────────────────────┐ │ ├──────────────────────────────────────────────────────────────┤
│ │                                   │ │ │ (All) (Pop) (Dance) (Indie) (Hip Hop) (Rock) (Electronic) →  │  ← chips 32h, rail 40h
│ │        cover 216 × 216            │ │ ├──────────────────────────────────────────────────────────────┤
│ │  = CoverEdge(240) = 240−16−8      │ │ │ ( Liked Jul 27 – Aug 3  ✕ ) ( vaultboy ✕ )       42 songs    │  ← lens header 28h + 8 gap
│ │  radius 8 · Elevation.Card        │ │ ├──────────────────────────────────────────────────────────────┤
│ │  clip · draggable(Hero)           │ │ │  #   TITLE                     ALBUM        ♥   ⏱            │
│ │             ┌──────────────────┐  │ │ ├──────────────────────────────────────────────────────────────┤
│ │             │ ✎ Cover style    │  │ │ │  1  ▮ Song title              Album name    ♥  3:41          │
│ │             └──────────────────┘  │ │ │  2  ▮ Song title              Album name    ♡  4:02          │
│ │      pill 30h, margin 10/10 BR    │ │ │ …                                                            │
│ └───────────────────────────────────┘ │ │                                                              │
│ Liked Songs           40/52/600 (28/36│ │                                                              │
│                        below 900 winH)│ │                                                              │
│ 166 songs · 9 hr 51 min      12/16 sec│ │                                                              │
│ ┌────────┐ ╭──╮╭──╮╭──╮               │ │                                                              │
│ │ ▶ Play │ │♡ ││⤴ ││⋯ │  40 FABs      │ │                                                              │
│ └────────┘ ╰──╯╰──╯╰──╯  gap 12/8     │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ THIS WEEK             last 12 wks ║ │ │                                                              │
│ ║ +12       ▁▃▂▅▃▁▄▂▆▄▅█  spark 38h ║ │ │                                                              │
│ ║ songs liked                       ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ TEMPO                   96 – 174  ║ │ │  ← trailing is "96 – 174 bpm" (tempoRange) only when EVERY   │
│ ║ 128      ╭─╮  ╭──╮   ╭╮   38h plot║ │ │    row is known; partial coverage prints "142 of 166"        │
│ ║ bpm · median  80  120  160        ║ │ │    (tempoCoverage) instead — LikedFactsPanel.cs:1598-1600    │
│ ╚═══════════════════════════════════╝ │ │    numeral col 72 fixed; no rug (RugDotMax 0), no hairlines  │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ MOST LIKED                        ║ │ │                                                              │
│ ║ (◍)(◍)(◍)(◍)(◍)(◍)(◍)(◍)(+32)     ║ │ │  ← the pile's "+N" and the row's "35 more" are DIFFERENT      │
│ ║   ↑ face count is RESPONSIVE:     ║ │ │    numbers: pileExtra = ranked − faceCount (faceCount is      │
│ ║   VisibleFaces(measuredCardW−24)  ║ │ │    measured), nameExtra = ranked − 5 (LikedFactsPanel.cs:     │
│ ║   first (unmeasured) frame keeps 5║ │ │    602-610). Both open the SAME flyout.                       │
│ ║ TOTO · 8                          ║ │ │                                                              │
│ ║ Billy Idol · 6                    ║ │ │                                                              │
│ ║ Clouseau · 5                      ║ │ │                                                              │
│ ║ aespa · 5                         ║ │ │                                                              │
│ ║ Adele · 4                         ║ │ │                                                              │
│ ║ ▸ 35 more                         ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ YOUR BLEND              272 songs ║ │ │                                                              │
│ ║ ████████▓▓▓▓▒▒▒░░▏▏▏▎▏▏▏  8h r4   ║ │ │                                                              │
│ ║ ▪Pop 20% ▪K-Pop 13% ▪Soundtrack.. ║ │ │                                                              │
│ ║ ▸ 44 more  47 %                   ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ ╔═══════════════════════════════════╗ │ │                                                              │
│ ║ REDISCOVER                        ║ │ │                                                              │
│ ║ 14 songs you liked   [ Play them ]║ │ │                                                              │
│ ║ this week last year               ║ │ │                                                              │
│ ╚═══════════════════════════════════╝ │ │                                                              │
│ Liking since March 2019 · mostly the  │ │                                                              │
│ 2010s · oldest like Africa            │ │                                                              │
└───────────────────────────────────────┘ └──────────────────────────────────────────────────────────────┘
  rail scroller: Grow 1, Shrink 1, MinHeight 0, NO Fill (DetailRail.cs:276-286)
```

*ch 07 W1 — Liked page, two-column, rail 240 @ page ≥ 820 DIP (mode 0), fully loaded* — `07-liked-songs.md:205-264`

- The cover is the user's own music, composed live: nine treatments on ONE 304-DIP canvas scaled by `size / 304`
  (`LikedCoverTreatments.cs:40, :111-117`); below a style's `MinTiles` floor (Lens 4 · Wall 8 · Rainbow 8 · Marquee 6 ·
  Feature 4 · Mosaic 4 · Tone 1 · Stack 3 · Stock 0) the answer is the bundled PNG, never a half grid.
- Nothing waits on a colour — every palette leaf paints from a neutral fallback and upgrades in place when
  `CoverColorPlane.Watch` answers (`LikedCoverLeaves.cs:15-24`); ink over imagery is `WaveeOnMedia`, never a theme token.
- Two ambient loops and they are SLOW: Wall drifts −46 DIP over 92 s, Marquee's three bands take 60 / 74 / 66 s so they
  never phase-lock; reduced motion is a zero-amplitude keyframe array (`:438-445, :548-553, :738-750`).
- A fact with no evidence is NOT MOUNTED — the blend needs 3 carriers, a distribution 10 values to speak and 20 to
  earn a chart (`LikedFactsRules.cs:665, :684-690`); every fact is a LENS writing `TrackFilterState`; the lens header
  names what is on per facet with its own ×, constant 36 DIP (`LikedFactsPanel.cs:1353, :1389-1441`).
- The facts settle before they speak: 250 ms of quiet, one summary, one blur-reveal; a shape may only UPGRADE
  (`:117-154`). The chips are one scrolling line, exclusive, evidenced chips physically FIRST (`ContentFilterTags.cs:23`).
- The Liked rail is unlayered — the ONLY difference from the other rails (`DetailRail.cs:271-298`); the page ground
  follows `Tiles[0]`, the newest like (`DetailShell.cs:278-291`).

### Library — `albums`, `artists`, `podcasts` (owner O)

`Entities/User.Page.Library.cs` (the master-detail page) + `User.UI.cs` (rows, count pill, sort/view flyout, crumbs, grip) +
`User.cs` (CORE rules + the matcher) · Wave 5 / owner O · chapter 15 (27 frames; W2 the artists' three columns, W3
podcasts list view, W4/W5 grid and compact navigators, W6 the sort/view flyout, W7-W10 skeletons and placeholders,
W11-W13 full-text search, W14-W17 the collapsed single-column breadcrumb drill-in under 640 — `CollapsedCrumbBar` on
`FillLayerDefault`, `Toolbar(title:false)`, tapping a row selects AND drills)

```
├───────────────────── 340 ─────────────────────┤│├──────────────────────────── 784 ─────────────────────────────────────────────────────────────────┤
┌───────────────────────────────────────────────┐│┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Tok.FillLayerDefault                          │││ Tok.FillCardDefault                                                                               │
│  ┌ pad 12 ─────────────────────────────────┐  │││                                                                                                   │
│  │                                          │ 16│   ┌ pad 20,20,20,12 ───────────────────────────────────────────────────────────────────────────┐  │
│  │  Albums                        PageHero  │ │ ││   │ ┌──────────┐  gap 16                                                                      │  │
│  │  28/36/600 · 1 line · ellipsis           │ │ ││   │ │          │   ALBUM                       Eyebrow 12/16/600 +30/1000em  TextTertiary      │  │
│  │                            pad-top 16    │ │ ││   │ │ 104×104  │   gap 3                                                                       │  │
│  │  gap 8                                   │ │ ││   │ │  r 8     │   In Rainbows                 TitleLink 28/36/600 TextPrimary, 2 lines        │  │
│  │  ┌──────────────────────────┐            │ │ ││   │ │ decode   │     hover → Tok.AccentTextPrimary (83 ms)                                     │  │
│  │  │⇅ Recents ⌃ │ ≡           │ h 32       │ │ ││   │ │  256px   │   Radiohead                   ArtistLinks 14/20/600                           │  │
│  │  └──────────────────────────┘ pad 10/0/8 │ │ ││   │ └──────────┘   2007 · 10 songs, 42 min     MetaLine 12/16 TextTertiary                     │  │
│  │  gap 8                                   │ │ ││   └───────────────────────────────────────────────────────────────────────────────────────────┘  │
│  │  ┌──────────────────────────────────────┐│ │ ││   ┌ pad 20,0,20,12 · gap 12 ───────────────────────────────────────────────────────────────────┐ │
│  │  │🔍 Filter                    minH 32  ││ │ ││   │ ( ▶ Play )  ( ⤨ )  ( ♡ )  [ View full album ]                                              │ │
│  │  │  r 4 · grow 1                        ││ │ ││   │  36 h        40    Save    Subtle/Small                                                    │ │
│  │  └──────────────────────────────────────┘│ │ ││   │  Radii.Full  circ                                                                          │ │
│  └──────────────────────────────────────────┘ │ ││   └───────────────────────────────────────────────────────────────────────────────────────────┘ │
│  ┌ ItemsView · Stack(60) · AccentPill ──────┐ │ ││   DetailNoticeBar.For(model)  — 0 height when silent                                             │
│  │ ┃ ▣  In Rainbows            SELECTED     │ │ ││  ┌ TrackList (embedded, no toolbar) ─────────────────────────────────────────────────────────┐   │
│  │ ┃ 40×40 Radiohead                        │ │ ││  │  #  Title                                              ♡        ⏱                        │   │
│  │ │    r 5   12/16 TextSecondary           │ │ ││  │  1  15 Step                                            ♡      3:57                       │   │
│  │ │ ▣  Kid A                  row h 60     │ │ ││  │  2  Bodysnatchers                                      ♡      4:02                       │   │
│  │ │    Radiohead              gap 12       │ │ ││  │  …                                                                                        │   │
│  │ │ ▣  Amnesiac               pad 8/0/8/0  │ │ ││  │                                                                                           │   │
│  │ │    Radiohead                           │ │ ││  │      see 04-detail-track-table.md                                                         │   │
│  │ └────────────────────────────────────────┘ │ ││  └───────────────────────────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────┘│└───────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                 ↑
                                      Grip 16 DIP: 1-DIP Tok.StrokeCardDefault hairline centred,
                                      HitTestVisible=false, behind a 2-DIP hover thumb (inset 4).
```

*ch 15 W1 - Albums, wide, fully loaded @ content 1140 (window 1440, sidebar 280, rail closed)* — `15-library.md:288-320`

### Local files — `local` (owner O)

`Entities/Playlist.Page.cs` — a `DetailKind.Playlist` arm of the shared frame, NOT a library page (`ContentHost.cs:178`,
`DetailPage.cs:78`); ch 15 owns the entry point (route, Folder glyph, "library" history facet, pinnability, the
never-leaks-into-navigators rule `LocalSource.cs:57-67`) · Wave 5 / owner O · chapters 15 + 03 + 06

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  ┌──────────┐   PLAYLIST                                                                            │
│  │ generated│   Local Files                                       ← Playlist.Name (LocalSource.cs:28)
│  │  gradient│   Music imported from this computer.                ← Description
│  │  cover   │   On this device · 14 songs                         ← Owner("local","On this device")
│  └──────────┘                                                        + TrackCount
│  ( ▶ Play ) ( ⤨ ) ( ⋯ )                                                                             │
│  # Title              Artist            Album             ⏱                                         │
│  1 Sunset Boulevard   Marble Sounds     Sunset Boulevard  2:30                                      │
│  … 14 rows (FakeData.LocalSeed, FakeData.cs:344-351)                                                │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  Capabilities: CanView, CanEditItems, CanEditMetadata, IsOwner = true; IsCollaborative = false
  Everything about this frame belongs to 03-detail-frame.md + 06-playlist.md.
```

*ch 15 W21 - Local Files (the detail frame, NOT this page) @ content 1140* — `15-library.md:756-770`

What must not be lost (ch 15 §0):

- A MASTER-DETAIL BROWSER, not a list that navigates: clicking re-skins the right pane(s) in place, never pushes a
  route; even a search hit commits into the browse selection (`LibraryPage.cs:606-627`).
- Three column rungs separated by a STROKE, not a fill — navigator `FillLayerDefault`, panes `FillCardDefault`, a 1-DIP
  hairline centred in every 16-DIP `Splitter` (`:352-365, :984-998`); in light that is a 2/255 ladder.
- The page title lives INSIDE the left column's toolbar (`:445-467`); column widths, sort, direction, view, size and the
  selected item persist per kind and seed the constructor — no default→saved flash (`:93-117`).
- Selection never remounts the list (`ItemsView` key = `view:size:OrderKey:FactsKey`, `:499`); "Recents" means
  recently PLAYED (`LibraryNavOrder.cs:40-47`); the library is the app's one accent-NEUTRAL surface (`:1180-1183`).
- Under 640 DIP the row collapses to a single-column breadcrumb drill-in with 24 DIP of hysteresis
  (`LibraryLayoutBreakpoints.cs:8-17`) and drops the page title; the navigator ALWAYS ends up with a selection once
  rows exist (`SyncNav`, `:558-564`) — the "Select an album…" placeholders are transient.

### Home landing — `home` (owner P)

`Entities/Home.Page.cs` (landing + extent table + `Home.SectionPage`) + `Home.UI.cs` (row shell, greeting, facet chips, hero
band, module shells, Fold tile, timeline) + `+Home.Cards.UI.cs` + `+Home.Artists.UI.cs` + `Home.cs` (CORE) + `Home.Customizer.cs`
+ `Home.Host.cs` — the settled seven files (A15) · Wave 5 / owner P · chapters 10 (20 frames), 11 (34: every module and
card skin — hero band tiers, weekly pair, jump back in, daily-mix band, the artist podium, chip cards, radio dial,
timeline, Fold deck), 12

```
├─36─┬──────────────────────────────────── A = 1088 ─────────────────────────────────────┬─36─┤
     │ New release from KIMMUSEUM              ← hero module header, ModuleHeader 20/28   │ 24 top inset (Spacing.XXL)
     │ ┌──────────────────────────────────────────────────────────────────────────────┐  │
     │ │·48·                                                        ╭ cover 384 sq ─╮ │  │ radius 8, 1px StrokeCardDefault
     │ │ Good morning, Christos          ← Caption 12/16/600 +30 tracking, tertiary  │ │  │ gradient: card→accent 10% top
     │ │ 44                                                          │ EdgeFade left │ │  │
     │ │ Afternoon focus                 ← ArtistTitle 48/60/700, -20 tracking, 2 ln │ │  │ 96 DIP, veil = horizontal
     │ │ rotation                                                    │               │ │  │ 0.96→0.92→0.35→0 alpha
     │ │ ┌ focus ┐┌ indie ┐┌ chill ┐     ← Tag: Caption 12/16, 8/2 pad, 1px stroke   │ │  │ 384
     │ │ 50 songs · by Spotify           ← Body 14/20 secondary, 2 lines max         │ │  │
     │ │ 04 : 27 : 11                    ← FlipCountdown 28 (daylist only)           │ │  │
     │ │ [ ▶ Play ][ ⇄ Shuffle ][ ♡ ][ ⋯ ]  ← WaveeCta 32 high, gap 8, wrap          │ │  │
     │ │·48·                                                         ╰───────────────╯ │  │
     │ └──────────────────────────────────────────────────────────────────────────────┘  │
     │                                   g = 40                                          │
     │ Discover Weekly & Release Radar                                                    │
     │ ┌───────────────────────────────────┐ 12 ┌───────────────────────────────────┐    │ weekly 88
     │ └───────────────────────────────────┘    └───────────────────────────────────┘    │
     │                                   g = 40                                          │
     │ Jump back in            Your 8 most-opened of 14   ← ModuleHeader(title, meta)     │ head 40
     │ ┌─────────┐ 12 ┌─────────┐ 12 ┌─────────┐ 12 ┌─────────┐                          │ 56
     │ ┌─────────┐    ┌─────────┐    ┌─────────┐    ┌─────────┐   row gap 12             │ 56
```

*ch 10 W3 — Loaded landing, top of page @ A ≈ 1088 (Wide hero tier)* — `10-home.md:265-288`

### Home landing, narrow tier — the reflow at `A ≈ 640`

`Entities/Home.Page.cs` + `Home.UI.cs` · Wave 5 / owner P · chapter 10 (W6 the Medium tier at 820; ch 11 W2/W3 the hero band Medium / Narrow)

```
├─36─┬────────────── A = 640 ──────────────┬─36─┤
     │ ┌────────────────────────────────┐  │
     │ │        ╭─ cover 336 sq ─╮      │  │ 336, JustifySelf Center
     │ │        ╰────────────────╯      │  │ veil vertical: 0 → .35 @45% → .78 @82% → 0 (dark)
     │ │ Good morning, Christos         │  │ copy Justify = End
     │ │ Afternoon focus rotation       │  │ PageHero 28/36/600, 2 lines
     │ │ [ ▶ Play ][ ⇄ ][ ♡ ][ ⋯ ]      │  │
     │ └────────────────────────────────┘  │
     │ quick grid 2 cols (A ≤ 780)         │
     │ mix band 3 cols (620 < A ≤ 1080)    │
     │ chip cards 1 col (A ≤ 680)          │
     │ radio 1 col (RadioColMin = 252)     │
```

*ch 10 W7 — Narrow tier @ A ≈ 640 (window ≈ 940)* — `10-home.md:416-429`

What must not be lost (ch 10 + 11 §0):

- Home reveals ONCE and everything reveals together: the skeleton holds until the feed AND the two chrome resources
  conclude or 1 500 ms after the feed settled (`HomeFeedReadiness.ChromeSettleMs`); 8 000 ms after mount it
  force-publishes the best answer seen (`ForceReleaseMs`, `HomePage.cs:929-934`). No row ever pops into a revealed page.
- The greeting is the hero's eyebrow, not a banner (`HomePage.cs:792-800`); the window carries the page's colour —
  THREE radial washes into the shell material, window-anchored, never scrolling (`ShellMaterialLayer.cs:44-77`); no
  invented colour, a null leg contributes no layer (`HomeWashSource.cs:59-71`).
- The facet strip is a tab strip, never pills; a sub-chip FUSES the parent into a segmented pill under the same node
  key (`HomeFacetStrip.cs:49-73`); a facet is a different DOCUMENT with its own extent table and scroll offset.
- Every row is capped at `PageMaxW = 1600` and centred, gutter 36 (`HomePage.cs:767-782`); the estimator and the
  renderer state the same geometry through `HomeModuleLayout` or the feed jumps under the cursor.
- Thirteen distinct card skins plus three shelf modules (`HomeCards.cs:14-21`); the 2-DIP SPINE of the item's own
  colour along a card's bottom edge on exactly six live skins; the daily-mix band is ONE surface divided into cells;
  numbers never reflow (fixed-width cells); the Fold tile's covers hang off the card and fan on hover.
- Charts is present in EVERY state — pending shimmers a Fold-shaped deck, Ready-empty says "No charts right now",
  Failed shows retry — the row never collapses to 0 (`HomePage.cs:738-746`).

### Home section drill-in — `home-section:<uri>`, `browse-section:<uri>` (owner P)

`Entities/Home.Page.cs` (`Home.SectionPage` — one class serving both prefixes, switching on the PREFIX) · Wave 5 / owner P ·
chapter 12 (21 frames; W1/W2 cold load and reveal, W6-W8 the Charts variant with its filter box and walk bar, W9/W10
empty and failed, W11 "Show all" armed / loading / disarmed, W19 the five degenerate routes and titles)

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Home  ›  Made For You                                                                                                     [Show all]  │
│                                                                                                                                        │
│ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐ ┌───────────────────┐  │
│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│ │┌─────────────────┐│  │
│ ││                 ││ ││                 ││ ││   ●  circular   ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││  cover 157×157  ││ ││                 ││ ││  (Artist kind)  ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││   radius 8      ││ ││                 ││ ││   radius Full   ││ ││                 ││ ││                 ││ ││                 ││  │
│ ││                 ││ ││                 ││ ││                 ││ ││                 ││ ││                 ││ ││                 ││  │
│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│ │└─────────────────┘│  │
│ │ Daily Mix 1       │ │ Discover Weekly   │ │      Björk        │ │ Release Radar     │ │ Chill Hits        │ │ Deep Focus        │  │
│ │ Taylor Swift, …   │ │ Your weekly mix   │ │      Artist       │ │ New for you       │ │ Spotify           │ │ Spotify           │  │
│ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘ └───────────────────┘  │
│  ◀──── 173.3 ────▶ 12                                                                                                                 │
│  cell 173.3 × 225.3   cover 157.3 square (cellW − 2×Pad 8)   pad (8,8,8,12)   cover→label gap 8                                       │
│  title 14/20 w600 TextPrimary 1 line char-ellipsis · gap 2 · meta 12/16 TextSecondary 1 line char-ellipsis                            │
│  2 DIP of trailing slack per cell (AspectGrid reserves 52; the label block consumes 50) — content does NOT grow into it                │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 12 W3 - Section page, loaded, seeded drill (no reveal) @ content 1100 (6 cols × 173.3)* — `12-home-section-and-customizer.md:370-389`

### Home customizer — `home-customize` (owner P)

`Entities/Home.Customizer.cs` (page + model, reducer, commands) + `Home.Host.cs` (`home-layout.json` off the UI thread) ·
Wave 5 / owner P · chapter 12 (W14 row states, W15 drag in progress, W16 the corrupt-file banner, W17 narrow)

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ ┌──┐                                                                        ┌─────┐ ┌──────┐      │  64 DIP
│ │◀ │        Your Home                                                       │Reset│ │ Done │      │  command bar
│ └──┘        Customize Home                                                  └─────┘ └──────┘      │
│  28×28      ▲Eyebrow 12/16 w600 +30 tracking AccentTextPrimary               Subtle   Accent      │
│  glyph 14   ▲Title   16/w600 TextPrimary                                     Small    Small       │
│ ◀8▶        ◀8▶  centre column grow1 basis0                                   gap 4        ◀─16─▶  │
├───────────────────────────────────────────────────────────────────────────────────────────────────┤ 1 DIP divider
│                                                                                                   │  StrokeDividerDefault
│ ◀─16─▶                                                                                            │
│   Hidden modules stay in this list so you can bring them back. Drag to reorder.                   │  12 / TextTertiary
│   ▲ maxLines 3                                                            ▲ top pad 12            │  maxLines 3
│                                                     ▼ gap 16                                      │
│   ⠿  Hero                                   ( ●━━ )                                               │  48 DIP row
│   ⠿  Discover Weekly & Release Radar         ( ●━━ )                                              │  48
│   ⠿  Jump back in                            ( ●━━ )                                              │  48
│   ⠿  Recents                                 ( ●━━ )                                              │  48
│   ⠿  Made for you                            ( ●━━ )                                              │  48
│   ⠿  Your top mixes                          (━━● ) ← OFF, row at Opacity 0.55                    │  48
│   ⠿  Radio                                   ( ●━━ )                                              │  48
│   ⠿  Up next                                 ( ●━━ )                                              │  48
│   ⠿  Audiobooks for you                      ( ●━━ )                                              │  48
│   ⠿  Podcasts for you                        ( ●━━ )                                              │  48
│   ⠿  Editors' picks                          ( ●━━ )                                              │  48
│   ⠿  Because you listened                    ( ●━━ )                                              │  48
│                                                                          ▼ bottom pad 20          │
│   ◀────────────────────── column MaxWidth 720 ──────────────────────▶     unused pane width       │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
 row internals: pad (8,0,8,0) · gripper box 12 wide (Icons.GripperBar E76F @12, TextTertiary,
 HitTestVisible=false) · gap 8 · label 14 TextPrimary grow1 basis0 maxLines 1 char-ellipsis · gap 8 ·
 ToggleSwitch (control box MinWidth 154 / MinHeight 40; PILL 40×20 r-circle sits at the box's LEFT edge,
 so the visible pill's right edge is ≈114 DIP inside the row's right edge)

 ⚠ CODE-DERIVED PREDICTION, now with the engine rule that settles it — still verify side by side (§9 trap 5,
   parity item 38), but expect the labels to be INVISIBLE, not merely narrow:
     · `Reorderable.Item` wraps the row in a `BoxEl` with **no Direction and no Grow** — a row whose single
       child therefore arranges at its own measured main size (`Reorderable.cs:329-352`);
     · `BoxEl.AlignItems` defaults to `Stretch` (`Element.cs:451`), so that wrapper DOES fill the 688-DIP
       column — but cross-axis stretch never touches the child's width inside a ROW;
     · in a row measured against a FINITE width, `Basis = 0` suppresses intrinsic width outright — the engine
       states it in so many words: "A row with a finite width is different: Basis=0 is the standard
       'flex: 1 1 0' contract and MUST suppress intrinsic width" (`FlexLayout.cs:590-604`). The grow-1/basis-0
       label therefore contributes 0 to the row's measure and receives 0 of its arrange.
   ⇒ the row measures 8 + 12 + 8 + **0** + 8 + 154 + 8 = 198 DIP, and what is on screen is a gripper and a
   toggle pill with an empty gap between them, twelve times over. The wide-layout drawing above is the
   INTENDED look, not the shipped one, and item 38 exists to say which of the two 0.3 must reproduce. Either
   way 0.3 MUST pass `Grow=1, Shrink=1, MinWidth=0` on the wrapped content exactly as
   `SidebarPaneSlot.cs:96-98, 130-132` does.
```

*ch 12 W13 - Customizer, wide (content pane ≥ ~790) — the column is capped at 720 and LEFT-aligned* — `12-home-section-and-customizer.md:666-715`

- The masthead title is real from frame one — the drill publishes the title it already knows (`HomeSectionPage.cs:100,
  :265`); a drill that carries a seed never shimmers at all (`HomeSectionNavigation.cs:11-26`).
- Covers are square, always (`AspectGridVirtualLayout`, `aspect = 1`, `HomeModules.cs:437`); one content column and
  gutter `(36, 100, 36, 16)` across the whole Browse family; no outer scroller — the grid owns its viewport.
- "Show all" lives in the shell band, not on the page, and disarms the instant the cursor ledger says nothing more
  (`:270-276`); infinite scroll is silent (`HomeSectionAppendPreloader.cs:72`); the skeleton's 8/8 ledger keeps the
  button off a shimmering page.
- The customizer is a page, not a dialog: 64-DIP command bar, 1-DIP divider, a 720-capped column of 48-DIP rows editing
  the LIVE document — no OK/Cancel (`HomeCustomizerPage.cs:21-23, :78-83`); hidden is dimmed at 0.55, never removed;
  reorder is displacement after the 200 ms dwell; every edit persists atomically with one `.bak`, and a corrupt file
  is never overwritten (`HomeLayoutStore.cs:55-86`).
- No undo, no toast, no confirmation, no reject feedback — the loc keys exist and nothing renders them
  (`HomePreferences.cs:36-45`); decide deliberately, do not invent an undo bar and do not delete the keys.
- ⚠ W13's wide row labels are a CODE-DERIVED PREDICTION: `Basis = 0` inside `Reorderable.Item`'s wrapper suppresses
  intrinsic width, so 0.2.9 may show gripper + toggle with an empty gap; parity item 38 decides which 0.3 reproduces.

### Search results — `search` (owner P)

`Entities/Search.Page.cs` + `Search.UI.cs` + `Search.cs` (`OmnibarSuggestQuery`, `SearchChipSkeletonPolicy`, facet / fallback /
column rules) · Wave 5 / owner P · chapter 13 (22 frames; W2 loading with the eleven-pill chip skeleton, W3 the chip row
wrapped at 960 and 560, W4 the top-result hero anatomy, W5-W10 every facet, W11 empty / error / offline, W12 the facet slide)

```
 |<----------------------------------------- pane 960 -------------------------------------------->|
 +--------------------------------------------------------------------------------------------------+
 |                                                                                                  |  <- Pad top 12 (Spacing.M)
 |  sleep                                                                                           |  SurfaceDisplay 40/52/400, lowercased, 1 line
 |                                                                                                  |  Gap 8 (Spacing.S)
 |  All   Playlists 75  Songs 67  Episodes 55  Genres 8  Podcasts 61  Albums 91  Audiobooks 50      |  FacetTab row, Wrap, AlignItems End
 |  ====                                                                                            |  <- 3 DIP accent underline under selected
 |  Artists 81  Authors 50  Profiles 50                                                             |  wrapped second row
 |  ______________________________________________________________________________________________  |  Pad bottom 8
 | <ScrollView ScrollKey "search:0">                                                                |
 |  +--------------------------------------------------------------------------------------+       |  resultBody Pad (16,8,16,96)
 |  | Top result . Playlist                                             .-------------.     | 228   |  SearchHero card, Radii.Card 8,
 |  |                                                                  /              /|    |       |  FillCardDefault + 1px StrokeCardDefault
 |  | Sleep                                                           /   cover      / |    |       |  radial accent wash centred (0.88, 0.40)
 |  |                                                                /   300x260    /  |    |       |  rotated -2.5deg, OffsetX +40, OffsetY -16
 |  | Gentle ambient piano . Spotify                                /_____________ /   |    |       |  PageHero 28/36/600, max 2 lines
 |  | (matched title) (Lyrics match)                               |              |    |    |       |  Chip: Radii.Full, 1px StrokeControlDefault
 |  | [ > Play ] [ Open page ] [ + ] [...]                         |              |    |    |       |  Play = WaveeCta.Play(accent) 36 DIP pill
 |  +--------------------------------------------------------------------------------------+       |
 |                                                                                          Gap 16  |
 |  | Best matches                                                        < >  o o . o o    |       |  TickHeader 3x14 + RailHeader 20/28/600
 |  +------------------------------------+------------------------------------+-------------+       |  SearchHitsGrid: 3 cols x 3 rows
 |  |[art] Title                    [+]  |[art] Title                    [+]  |[art] ...    | 64    |  MediaCard.Row(plated:false) cells, gap 12
 |  |      Artist . Album                |      Artist                        |             |       |
 |  +------------------------------------+------------------------------------+-------------+       |
 |  |[art] Title                    [+]  |[o]   Artist name          [Follow] |[art] ...    | 64    |
 |  +------------------------------------+------------------------------------+-------------+       |
 |  |[art] Title                    [+]  |[art] Podcast name        [Follow]  |[art] ...    | 64    |
 |  +------------------------------------+------------------------------------+-------------+       |
 |                                                                                          Gap 16  |
 |  | Playlists  >                                                        < >  o . o o      |       |  TickHeader with chevron -> SelectFacet(Playlists)
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                 |       |  SearchMediaGrid: GridCard 148-188 DIP
 |  | cover   | | cover   | | cover   | | cover   | | cover   | | cover   |                 | 220   |  ShelfHeight(148) = 148 + 72
 |  |         | |         | |         | |         | |         | |         |                 |       |
 |  | Title   | | Title   | | Title   | | Title   | | Title   | | Title   |                 |       |
 |  | Owner   | | Owner   | | Owner   | | Owner   | | Owner   | | Owner   |                 |       |
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                 |       |
 |                                                                                          Gap 16  |
 |  | |tick| Genres                                                                         |       |  TickHeader (no chevron), Gap 12 to the grid
 |  |  [#] Sleep            |  [#] Rain sounds       |  [#] Noise for sleep                  |       |  BrowseTiles.Link, 3-col StarGrid
 |  |  [#] White Noise      |  [#] Lullabies         |  [#] Sleep & Meditation               |       |  colGap 12, rowGap 8; pip ALWAYS AccentDefault
 |                                                                                          Gap 16  |
 |  | |tick| Related searches                                                                |       |  TickHeader (no chevron), Gap 8 to the links
 |  |  sleep music     sleep music for deep sleeping     sleep meditation     slaap          |       |  links: ModuleHeader 20/28 Weight 400 lowercase
 |  +--------------------------------------------------------------------------------------+       |
 |                                                                             Pad bottom 96        |  PlayerDock.Reserve 72 + Spacing.XXL 24
 +--------------------------------------------------------------------------------------------------+
```

*ch 13 W1 - Search results, All facet, fully loaded @ pane 960* — `13-search-and-browse.md:277-325`

### Browse directory and category page — `browse`, `browse:<uri>` (owner P)

`Entities/Browse.Page.cs` (`BrowsePage` + `BrowseDirectoryPage` + `BrowsePageHost`) + `Browse.UI.cs` (`BrowseTiles`, the bands) +
`Browse.cs` (taxonomy, seeds, layouts, masthead metrics) · Wave 5 / owner P · chapter 13 (W16 the directory loading, W17
the five cell densities at rest and on hover, W18 scrolled under the masthead, W20 the `FlattenOne` mode, W21/W22 loading
and empty / error)

```
 +--------------------------------------------------------------------------------------------------+
 |                                                                                                  |  32 = FrameTop (Spacing.XXXL)
 |  Browse                                                                                          |  ShellMastheadBand overlay - paints NOTHING
 |  ____________________________________________________________________________________ 84 = Reserve   SurfaceDisplay 40/52/400
 |                                                                                                  |  16 (Spacing.L) -> BodyTop 100
 | <ScrollView ScrollKey "browse">  spacer 100  then the clipped directory, Pad (36,0,36,96)         |
 |                                                                                                  |
 |  Top                                                        <- BandLabel = Eyebrow 12/16/600 +30/1000, Tok.TextSecondary
 |  ( Music )  ( Podcasts )  ( Audiobooks )  ( Live Events )   <- BrowseTiles.Word, 36 DIP pills, Gap 8, Wrap
 |                                                                                                  |  Gap 16 between bands
 |  |tick| Charts                                        < >  o . o                                 |  FoldDeck's OWN header (no BandLabel);
 |                                                                                                  |  the two Charts-mapped CATEGORIES never
 |                                                                                                  |  render as tiles - Body swallows the
 |                                                                                                  |  Charts bucket and injects the deck
 |                                                                                                  |  instead (BrowseDirectory.cs:130)
 |  +--------------------------------------+ +--------------------------------------+               |  HomeFoldTile, 440-9999 wide, 176 tall,
 |  |  Featured Charts        [cover stack]| |  Weekly Song Charts    [cover stack]  |               |  gap 12, 1 row, max 2 columns
 |  +--------------------------------------+ +--------------------------------------+               |
 |                                                                                                  |
 |  For you                                                                                         |
 |  (# Discover) (# EQUAL) (# Fresh Finds) (# GLOW) (# Made For You) (# New Releases)               |  BrowseTiles.Name, 32 DIP, pip + Body
 |  (# RADAR) (# Spotify Singles) (# Tastemakers) (# Trending)                                      |  Gap 8, Wrap
 |                                                                                                  |
 |  Genres                                                                                          |
 |   [#] Afro           |  [#] Alternative     |  [#] Ambient                                        |  BrowseTiles.Link, 3 cols
 |   [#] Arab           |  [#] Blues           |  [#] Caribbean                                      |  colGap 12, rowGap 8
 |   ... 25 entries, alphabetised, 9 rows                                                            |
 |                                                                                                  |
 |  Mood & activity                                                                                 |
 |  +-------------+ +-------------+ +-------------+ +-------------+ +-------------+ +-------------+  |  BrowseTiles.Bar, 52 DIP,
 |  ||  At Home   | ||  Chill     | ||Cooking &   | ||  Fitness   | ||  Focus     | ||In the car  |  |  6 cols at body 888
 |  +-------------+ +-------------+ +-------------+ +-------------+ +-------------+ +-------------+  |  colGap 4, rowGap 4
 |  ... 14 entries, alphabetised                                                                     |
 |                                                                                                  |
 |  More                                                                                            |
 |  +-------------------+ +-------------------+ +-------------------+ +-------------------+          |  BrowseTiles.Peek, 88 DIP,
 |  || Category name    | || Category name    | || Category name    | || Category name    |          |  4 cols (capped), gap 12/12
 |  ||               .--| ||               .--| ||               .--| ||               .--|          |  hanging art 80, -8 deg - ONLY when
 |  +------------------|--+------------------|--+------------------|--+------------------|-+         |  m.Artwork != null (BrowseTiles.cs:166);
 |                                                                                                   |  a coverless More card is a plain plate
 |                                                                                                   |  cardW handed to Peek = width / cols,
 |                                                                                                   |  gaps NOT subtracted (BrowseDirectory.cs:312)
 |                                                                                   Pad bottom 96   |
 +--------------------------------------------------------------------------------------------------+
```

*ch 13 W15 - Browse directory, fully loaded @ pane 960* — `13-search-and-browse.md:723-768`

```
 +--------------------------------------------------------------------------------------------------+
 |  Browse > Jazz                                                                     [ Show all ]  |  ShellMastheadBand: trail-as-title,
 |  ________________________________________________________________________________________  84   |  crumbs Tok.TextTertiary (hover Secondary)
 |                                                                                            16    |  + a "\u203a" separator at the same rung;
 | outer column: Pad (36,100,36,16), Gap 16                                                         |  tools button only in FlattenOne
 | <ScrollView ScrollKey "browse:spotify:page:...">  content Pad (0,0,0,96), Gap 16                  |
 |                                                                                                  |
 |  Jazz Essentials                                                        < >  o . o o             |  HomeModules.DrillHeader(title, open)
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                         |  PagedShelf: MediaCard.Shelf cards,
 |  | cover   | | cover   | | cover   | | cover   | | cover   | | cover   |                  220    |  minCardW 148, maxCardW 188, gap 12,
 |  | Title   | | Title   | | Title   | | Title   | | Title   | | Title   |                         |  edgeFade 24, Key "browse-shelf:<uri>"
 |  | Sub     | | Sub     | | Sub     | | Sub     | | Sub     | | Sub     |                         |
 |  +---------+ +---------+ +---------+ +---------+ +---------+ +---------+                         |
 |                                                                                                  |
 |  (an UNTITLED shelf renders with NO header row at all - BrowsePage.cs:671, and an untitled CategoryGrid /        |
 |   Related block likewise renders as the bare LinkGrid with no DrillHeader - BrowsePage.cs:687)                   |
 |  (a Related / CategoryGrid header is a LABEL: CategoryBlock passes `DrillHeader(title, null)` - no chevron,      |
 |   no click. Only a SHELF header drills, into `browse-section:` - BrowsePage.cs:643-646, 691)                     |
 |                                                                                                  |
 |  Related                                                                                         |  CategoryBlock: DrillHeader + Gap 8
 |   [#] Blues          |  [#] Funk & Disco    |  [#] Soul                                          |  Responsive LinkGrid (fb 900), 3 cols
 |                                                                                                  |
 |  Explore all categories                                                                          |  TrackMeta (Caption 12/16) in
 |                                                                                                  |  Tok.AccentTextPrimary, Pad (0,8,0,8),
 +--------------------------------------------------------------------------------------------------+  AlignSelf Start, Role Button
```

*ch 13 W19 - Browse category page, Shelves mode @ pane 960* — `13-search-and-browse.md:879-905`

What must not be lost (ch 13 §0):

- The query echo is the committed query, lowercased, `TitleLarge 40/52` at weight 400 (`SearchPage.cs:111`); the facet
  row reserves its real shape from frame one — eleven placeholder pills at `[40,68,76,74,88,68,96,116,76,76,76]`
  (`:227-247`); the selected underline grows from its LEFT edge (`TransformOriginX = 0`, 260 ms `SmoothOut`).
- The top result is a 228-DIP card with a 300×260 cover cropped off the right edge at −2.5°, a radial wash of its
  own accent (`SearchHero.cs:106-146`); every hit is the same unplated `MediaCard.Row` — except Artists
  (`ResultRow`, 60 DIP) and Genres (`BrowseTiles.Link`), the only two exceptions.
- A track hit gets the FULL track menu and a depositable drag payload (`:689-704, :791-798`); a track hit's art
  disappears under `HideTrackArtwork`, a non-track hit's never does (`:784`).
- Browse's five cell densities, one per band, cheapest to most expressive — Word 36 → Name 32 → Link → Bar 52 → Peek 88
  (`BrowseTiles.cs:17-20, :56-179`); a mood bar is the house card plate with the colour demoted to a 0.20 right-edge
  radial + a 3-DIP tick, never a full-bleed field; the More card's 80×80 art hangs out of the frame at −8°.
- The directory assembles itself band by band with a capped 40 ms stagger and exactly one entrance (`SkelReveal.None`,
  `BrowseDirectory.cs:92-93, :243`); the masthead paints nothing and the directory cuts itself under it at 84 with a
  24-DIP fade that arms only once the clip engages.

### Recents — `recents` (owner P)

`Entities/Recents.Page.cs` (shell, semantic zoom, annotated rail, sticky band, accent, calendar) + `Recents.UI.cs` + `Recents.cs`
(CORE: the 19-class `RecentsView` rule set) · Wave 5 / owner P · chapter 16 (25 frames; W1/W2 skeleton and reveal, W4 the
sticky push, W6 a group row's drawer, W10 the Podcasts pivot, W11-W15 the calendar overview and heat cells, W22 at 420)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                              ↑24           │
│   Recents                                                    [ 📅 Overview ]                               │ title row gap 12
│   1,708 items · 4 Aug – Today · grouped from 9,446 plays                                                   │ Caption 12/16 tertiary
│                                                                                              ↓16           │
│   All        Music        Podcasts        Artists                                                          │ gap 20 (Spacing.XL)
│   ▂▂▂                                                                                                      │ 2 DIP accent underline
│                                                                                              ↓8            │
│ ╔══ PINNED OVERLAY (h48, FillLayerDefault, HitTestPassThrough) ═════════════════════════════════════╗ ┌──┐ │
│ ║ Today  ────────────────────────────────────────────────────────────────────────────   14 items    ║ │Se│ │ ← 44 rail
│ ╚════════════════════════════════════════════════════════════════════════════════════════════════════╝ │p │ │
│  ┌────┐                                                                                              │ │▬▬│ │ thumb 30×3
│  │ ▨▨ │  Discover Weekly                                          [Playlist]    14:22   ⋯      ›     │ │ ·│ │ tick 3×3
│  │ ▨▨ │  Spotify · Played 23 tracks                                                                  │ │ ·│ │
│  └────┘  ← 48 art, r4                                                                        h64     │ │ ·│ │
│  ┌────┐                                                                                              │ │Au│ │
│  │ ▨▨ │  Blonde                                                      [Album]    13:58   ⋯      ›     │ │g │ │
│  │ ▨▨ │  Frank Ocean · Played 11 tracks                                                              │ │ ·│ │
│  └────┘                                                                                              │ │ ·│ │
│  ( ●● )  Frank Ocean                                                [Artist]    13:10   ⋯            │ │  │ │ circular art, no chevron
│  \    /  Played 4 tracks                                                                             │ │  │ │
│  ┌────┐                                                                                              │ │Ju│ │
│  │ ♥  │  Liked Songs                                                              12:40   ⋯      ›   │ │l │ │ no type chip
│  │    │  Played 31 tracks                                                                            │ │  │ │
│  └────┘                                                                                              │ └──┘ │
│   Yesterday ───────────────────────────────────────────────────────────────────────   9 items        │      │ inline h48 header
│ ←36→                                                                                        ←12→ 44 ←36→    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 16 W3 — Recents, loaded, at the top @ 880* — `16-recents-and-history.md:220-249`

- The masthead is one oversized light display word (`TitleLarge 40/52` at **400**, −12/1000 em) over one thin grey
  line, arriving 45 ms apart (`RecentsPage.cs:64`); the summary line never reflows (`MinWidth = 220`).
- Four fixed pivot tabs, always all four, in `PivotLabel` 19/25 SemiLight 350; a zero-count tab is disabled, never
  hidden (`:530-537`); only the selected tab mounts its underline.
- One measured virtual list with a pinned day band: rows 64, headers 48, rows band-clipped at 48 with a 24 feather; the
  pinned header is pushed out by the next one, quantised to 2 DIP (`:1283, :1383-1384`).
- The page's accent follows the viewport per DAY and glides over 250 ms, re-derived once per section crossing
  (`:1288-1301`); Recents publishes ONE `HomeWash` leg from the same row (`:315-330`).
- Zooming out is a real semantic zoom (`Flow.KeepAlive`, scale 1.08 ↔ 0.94 on the standard spring); the calendar is a
  heat-map of exact 38 × 32 cells, `weeks` rows never a fixed 6, a logarithmic five-level ramp with a legend built from
  the same function (`RecentsView.cs:514`). Nothing is ever removed to make room.

### Queue (stage pane) and the `--fake` seed — rail / stage / every route (owner Q)

`Entities/Queue.UI.cs` (the rail panel + the stage pane, one shared row builder, two skins — the rail arm is shown under
Wave 4 above; the stage pane is ch 21 W12) · `Entities/Entities.Fake.cs` (`Entities.SeedFake()`, 1,100; a first cut lands at
the start of Wave 4 as an orchestrator-owned file) · Wave 5 / owner Q · chapters 21 + 31 (17 frames)

```
 ROUTE                      0.2.9 --fake   source / reason                            0.3 seed must add
 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
 home                       ████           assets/spotify/home.json (31 sections,     ExpiresAt/CreatedAt on the
                                           3 homeChips, 1 daylist-format card)       daylist card that already
                                                                                      exists (§2 W1); ONE title
                                                                                      for "Jump back in" (§0.10g)
 home-customize             ████           the live home-layout document              —
 home-section:<uri>         ░░░░           NullHomeSectionService (Services.cs:364)   a pageable section + cursor
 browse                     ░░░░           NullBrowseService (:361)                   a directory, 5 bands
 browse:<uri>               ░░░░           NullBrowseService                          1 category page per
                                                                                      BrowsePageLayout mode (4)
 browse-section:<uri>       ░░░░           NullHomeSectionService                     as home-section
 search                     ▓▓░░           SpotifyExportSource.SearchAsync :142-155:  shows, episodes, audiobooks,
                                           tracks + albums + artists + playlists ONLY profiles, genres, chipOrder,
                                                                                      a top hit, suggestions
 albums / artists /
   podcasts                 ████           FakeSource 13/12 + FakePodcastSource 8     fix the 7-vs-8 stat (§0.10c)
 liked                      ████           166 rows via the export's PseudoPlaylist   AddedAt per row
   └ content-filter chips   ░░░░           NullContentFilterService (:324)            a curated chip set
 local                      ████           LocalSource + LocalTracks() (14)           —
 album:<uri>                ████           FakeData.Album(i), 4 kinds via AlbumShape   release facts; other
                                                                                      versions; a PRERELEASE
 prerelease:<uri>           ░░░░           no prerelease anywhere in the seed         one upcoming album + its
                                                                                      countdown
 pl:<uri>                   ▓▓░░           21 export headers + Iced (real tracks);    make FakeData.Playlist(i)
                                           spotify:playlist:pl{i} → the GENERIC arm   reachable for pl{i} (§0.10a)
 artist:<uri>               ████           FakeData.Artist(i) (+ 1 real export)       determinism (§0.4)
 disco:<n>:<uri>            ▓▓░░           2/3/1 albums, not hundreds (§0.10b)        wire Discography() in
 show:<uri>                 ████           8 shows × 8-12 episodes                    one show with a load-more
 module:<uri>               ────           Services.Modules is NULL on the fake       a module with a page + a
                                           backend                                    watch page (ch 09, ch 24)
 concerts / concert:<uri> /
   artist-concerts:<uri>    ░░░░           NullConcertService (:360)                  a hub feed, an artist
                                                                                      schedule, a detail + offers
 recents                    ░░░░           NullRecentsService (:363) → Empty          a grouped snapshot with
                                                                                      members + instants
 history                    ████           the shell's own log — navigate 10-12 first —
 whatsnew                   ████           the bundled release-notes document         —
 settings                   ████           the real settings store                    —
 sidebar-customize          ████           the live preference document + the         keep the fixtures index-
                                           miniature                                   addressable (§7.2)
 api-console                ████           developer-mode gated                       —
 playback-diagnostics       ████           honest "no playback session" arms          —
 connect-diagnostics        ████           renderable, but ShellRoutes.IsKnown omits  — (ch 18's defect, not the
                                           it (ch 18 §7)                               seed's)
 (unknown route)            ████           the not-found page                         —
 ───── SURFACES, not routes ────────────────────────────────────────────────────────────────────────────────────
 player bar                 ░░░░ RESTING   UnsupportedPlaybackPlayer (:589)           a PLAYING state (§7.4 note)
 right rail / NPV / deck    ░░░░ RESTING   nothing plays ⇒ no art, no clock, no motion ditto
 queue panel                ░░░░ EMPTY     DefaultQueue() exists (:569-576), NO CALLER wire it
 lyrics (rail + immersive)  ──── ABSENT    NoLyricsProvider (:624). Lyrics() builds a
                                           40-line WORD-SYNCED doc (:543-567), NO CALLER wire it
 video (4 surfaces)         ──── ABSENT    no modules, no overrides, no source        a module video + an override
 friends panel              ░░░░           NullFriendActivityService (:325)           a friends feed
 notifications centre       ░░░░           NullSpotifyNotificationsService (:358)     ≥ 4 rows, ≥ 2 unread
 device picker              ░░░░           NoConnectDevices (:590)                    a static roster (§7.4 D)
 track credits              ░░░░           NullTrackCreditsService (:323)             a credit list
 top tracks / popcount /
   pre-save rows            ░░░░           NullUserTopService / NullPlaylistPopcount /
                                           NullPreRelease (:320-322)                  ranked lists + a save count
 OS surfaces (all four)     ░░░░ IDLE      nothing plays ⇒ Clear* everywhere          follows from "something plays"
 ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
 SCORE, 0.2.9. The ROUTE half of this table is 27 rows covering 31 route keys (the albums/artists/podcasts
 row is three routes; the concerts row is three). Sixteen rows render LOADED · 3 PARTIAL · 7 EMPTY · 1 ABSENT —
 and inside the LOADED `home` row the Charts deck is a real FAILED state (the fail-loud arm, ch 10 W12).
 The SURFACE half is 11 rows, and ZERO of them render a loaded state — two (the queue's and the lyrics')
 have a fixture already written and no caller.
 "--fake opens every route" is satisfied by all 31 route keys. That is the whole problem.
```

*ch 31 W14 — The Wave 5 gate's real shape: every route, its `--fake` state, and what the seed must add* — `31-fake-data.md:661-730`

Note (outside the frame, not redrawn): the `api-console` row above is 0.2.9 history, quoted verbatim from ch 31 — §9.6
Q7 (2026-09-12) deletes that route in 0.3, so it no longer appears in the Wave 5/6 gate lists below.

- `--fake` is a SECOND source stack behind the same façade: `SeedFake()` writes staging columns and edges through the
  same commit path `Spotify.Decode` writes through, and then does nothing else; an `if (fake)` anywhere in `Entities/`,
  `Shell/` or a page is a defect (ch 31 §0.1).
- Index-addressable, total and pure — `SeedFake.Playlist(7)` is the same playlist in every process, forever, because
  `SidebarMiniature` indexes eight slots by hand (§0.3); 0.2.9 breaks this in exactly two places (`GetHashCode` fan-out
  at `FakeData.cs:147`, seven `DateTimeOffset.Now` anchors) and both are FIXED, not ported; time enters once as
  `SeedFake(long now0)` against a pinned `Clock.SeedEpoch`.
- A seeded surface renders its LOADED state or the gate does not count it; a state that cannot be faked (a live Connect
  transfer, a real DRM licence) belongs to the live checklist, not Wave 5 (§0.5-0.6).
- Home art in `--fake` needs the network (≈ 306 remote image urls across ten CDN hosts in `home.json`); every
  synthesized surface does not — a screenshot procedure must say which condition it ran under.
- The seed is tested: `Wavee.Tests/EntitiesFakeTests.cs` pins counts, indices, determinism and the cross-surface
  agreement rule (§0.12); `WAVEE_FAKE_CHALLENGE` becomes `Platform.Args.FakeChallenge`, not an env var.

---

## 9. Wave 6 — screens, diagnostics, modules (owners R, S, T)

Gate: the WHOLE G2 probe list re-runs, now including `settings`, `whatsnew`, `playback-diagnostics`, `connect-diagnostics`
and `module:` + the watch page (`api-console` dropped from the list, §9.6 Q7, 2026-09-12: the route no longer exists);
parity for chapters 14, 27, 28, 30 and the rest of 29 — all 32 checklists, 2,650 items, green and logged.

### Settings — `settings` (owner R)

`Screens/Settings.UI.cs` (shell, tabs, General, Notifications, the Logs mount) + `+Settings.UI.Appearance.cs` + `+Settings.UI.Playback.cs`
+ `+Settings.UI.Storage.cs` + `+Settings.UI.About.cs` + `Settings.cs` (`SettingsCatalog`, `SettingsGlyphs`) + `Settings.Host.cs`;
`+Settings.UI.Video.cs` is K's (Wave 4), mounted here · Wave 6 / owner R · chapter 27 (29 frames; W2 the Appearance tab with
its four collapsed picker groups and the `SettingsValueTag` answers in the headers, W3/W4 the expanders open
with their live preview cards, W5/W6 the wrapped card at 400 / 260, W7-W12 Playback with the EQ curve, crossfade, the
runtime PROBLEM InfoBar and the video-override manager, W13/W14 Notifications, W15-W18 Storage, W20 About's state matrix,
W27 the confirm dialogs, W28 scrolled — the chrome is PINNED)

```
┌ pane ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                                                        │
│ 36                                                                                                                     │
│ ⚙ 24   Settings                                                            ← Icon(Settings,24) + Ui.Title 28/36/600    │
│ ↕12                                        ← the masthead's BOTTOM pad is Spacing.M 12, not 16 (SettingsPage.cs:242)   │
│ General   Appearance   Playback   Notifications   Storage & cache   Logs   About                                       │
│ ▔▔▔▔▔                                                          ← 16×3 accent pill, flush with item bottom              │
│ ────────────────────────────────────────────────────────────────────────────────────────────────────────────── 1 DIP  │
│ ↕16                                                                                                                    │
│ 🌐 Language & region                       ← 16 DIP glyph · BodyStrong 14/20/600      margin-top 32, margin-bottom 8   │
│    The language Wavee's own interface uses ← Caption 12/16 TextSecondary, wrap, max 2 lines                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ card 1000 × ≥68    │
│ │ ⌨ 20     App language                                     ┌ System default            ▾ ┐ 260 │ r=4, 1 DIP stroke   │
│ │ ←16→ ←20→ Changes are applied the next time Wavee starts. └──────────────────────────────┘     │ Pad 16 all         │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕4                                                                                                                     │
│ 🔗 Links                                                                                                               │
│    What Wavee opens from other apps                                                                                    │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ ⧉  Open spotify: links in Wavee                                               (  ●) 40×20 track│                    │
│ │    Off by default. Turning this on makes Wavee the handler for spotify: links…                 │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕4                                                                                                                     │
│ 🖥 Graphics                                                                                                            │
│    Which GPU Wavee renders on                                                                                          │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐                    │
│ │ 🖳  Graphics adapter                          ┌ Automatic (recommended)             ▾ ┐ 300     │                    │
│ │     Switching applies immediately — the window may flicker briefly…                            │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ items: "Automatic" │
│          + one per adapter; the LIVE one reads `Gpu.InUse(name)` — "<name> (in use)" (General.cs:214-220)              │
│ ↕4                                                                                                                     │
│ ⟨⟩ Developer                                                                                                           │
│    Debugging and inspection tools for Wavee itself                                                                     │
│ ┌ ⚙  Developer mode           Shows the API console, lyrics inspector…                     (  ●) ┐                    │
│ ┌ 🕐 FPS overlay              Frame timing in the corner of the window            [disabled]    ┐ ← isEnabled: dev    │
│ ┌ 📄 Archive Spotify realtime traffic   Writes every dealer frame to disk…                 (  ●) ┐                    │
│ ┌ ⟳  Simulate an update       Walks the update state machine locally…       [ Run simulation ]  ┐ ← dev-only, ELIDED  │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕36 (bottom page padding)                                                                                              │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 27 W1 — General tab, fully loaded @ pane 1072 (card 1000)* — `27-settings-and-diagnostics.md:387-428`

```
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ hero card:         │
│ │ ┌──────┐  Wavee 0.2.9 "Breaker"                                            [ Update now ]      │ Pad (24,22,24,22)  │
│ │ │  ♪   │  ← 24/600 TextPrimary, wrap                                    What's new in Breaker  │ r = Radii.Card 8   │
│ │ │ 64²  │  ┌0.2.9.10┐ ┌Stable┐ ┌Update available┐  a1b2c3d · arm64    Show the update summary… │ Fill FillCardSecondary│
│ │ │ r=16 │  ← pills, Gap 8, Wrap; the quad pill is MONO; the stamp is a plain 12 DIP             │ Border 1 StrokeCard│
│ │ └──────┘  Built 2026-08-29  ·  last checked 29/08/2026 14:02   ← 12 TextTertiary, wrap         │ Gap 18, Align Center│
│ │  AccentSubtle fill, Icon(MusicNote,30,AccentTextPrimary)                                       │ right col Gap 8,    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ AlignItems End     │
│ ↕4                                                                                                                     │
│ ┌ ⟳ Update status      Wavee 0.3.0 "Comet" is available.                                        ┐ StatusCard          │
│ ┌ 🖥 Release channel    Beta installs side by side with Wavee…              Get Wavee Beta      ┐ ChannelCard (a LINK)│
│ ┌ ⤓ Install a waiting update when I quit Wavee   Wavee downloads it as it closes…         (  ●) ┐ InstallOnQuitCard   │
│ ┌ 📡 Download on metered connections    Off: waits for an unmetered network.              (  ●) ┐                     │
│ ┌ ✨ Show "What's new" after an update  A short summary the first time…                   (  ●) ┐                     │
│ ↕4                                                                                                                     │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ links card:        │
│ │ Open What's new ●          ← InfoBadge.Dot() while the running release's notes are unread      │ Alignment = Left,  │
│ │ Report a problem                                                                               │ content column     │
│ │ Suggest a feature                                                                              │ Gap 4,             │
│ │ All issues on GitHub                                                                           │ Margin (−12,0,0,0) │
│ │ Website                                                                                        │ (pulls the links   │
│ │ Privacy policy                                                                                 │  flush to the card │
│ │ Third-party notices                                                                            │  padding)          │
│ │ Copy diagnostics info                                                                          │                    │
│ │ Open data folder                                                                               │                    │
│ │ Wavee is an independent client and is not affiliated with or endorsed by Spotify AB. ← 12 Tert.│                    │
│ │ Microsoft Windows NT 10.0.26340.0 (Arm64)                                          ← 12 Second.│                    │
│ │ C:\Users\…\AppData\Local\Wavee                                        ← 12 Second., Cascadia   │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ┌ ⚠ Crash reports    The last 10 reports in the log folder                                   ⌄  ┐ CrashReportsCard    │
│ ↕32                                                                                                                    │
│ ⓘ Wavee right now                                                                                                      │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ receipts card:     │
│ │ GPU                         Apple M2  (Strong)              ← label 12 TextSecondary, Shrink 0 │ Alignment = Left,  │
│ │ Working set                 412.7 MB                          value 13/600 TextPrimary, Grow 1 │ Gap = Spacing.XS 4 │
│ │ Managed heap                 78.3 MB                                                           │                    │
│ │ Uptime                        2h 14m                                                           │ 5 000 ms UseInterval│
│ │ FPS                            120.0                                                           │                    │
│ │ Zoom                           125%                                                            │                    │
│ │ GPU assets                  188.2 MB local  ·  0.0 MB non-local                                │                    │
│ │ App memory excl. GPU assets 224.5 MB  (discrete)                                               │                    │
│ │ Local budget 8192.0 MB · non-local budget 16384.0 MB · tracked D3D12 …  ← 12 TextTertiary wrap │                    │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ↕32                                                                                                                    │
│ 📄 Licenses                                                                                                            │
│ ┌ Wavee              MIT                                                                    ⌄   ┐ expander, custom    │
│ ┌ Third-party notices  THIRD-PARTY-NOTICES.txt                                              ⌄   ┐ ItemCardStyle:      │
│                                                                    body = 12 DIP Cascadia Code,  Pad (16,12,16,16),   │
│                                                                    TextTertiary, wrap            MinH 0, r 0, no wrap │
```

*ch 27 W19 — About tab, update AVAILABLE @ pane 1072* — `27-settings-and-diagnostics.md:977-1027`

What must not be lost (ch 27 §0):

- A LEFT-aligned 1000-DIP column inside a 36-DIP gutter, never centred, never full-bleed (`SettingsPage.cs:23, :168,
  :192-198`); cards 4 DIP apart, groups 32 apart (`:24-25, :200-206, :231`) — that rhythm IS the page.
- On the four table-driven tabs every row carries its own glyph from `SettingsCatalog` and no row reuses its section's
  (`SettingsCatalog.cs:34-120`, `SettingsCatalogTests`); Notifications, About and Logs are deliberately outside the
  table; two rows render `Icons.Settings` ON PURPOSE (Developer mode, Player style). Do not "de-duplicate" the 17
  legitimate cross-section repeats.
- A picker never sits in an expander HEADER; the header carries the ANSWER (`SettingsValueTag`, `:275-278`) — density,
  track list style, page layout and sidebar design are COLLAPSED by default; Equalizer and Crossfade expand when on.
- Preview cards are live UI, never screenshots; the selected card tints its whole wireframe and grows its border
  1 → 2 DIP INWARD so nothing shifts (`WaveePicker.cs:32-34, :63-82`); the density miniature is the real numbers × 0.25.
- Tab switch is instant, each tab keeps its own scroll offset (`Key = "settings:scroll:" + slug`); Logs is the ONLY tab
  not scrolled by the page; the storage bar is seven fixed hues, never accent tints (`Storage.cs:36-42`).
- The equalizer is a direct-manipulation Catmull-Rom curve, not ten sliders (`WaveeEqualizerCurve.cs:61-152`); every
  destructive action confirms through `SettingsShared.Confirm` with `DefaultButton = Close` (eight sites here).
- **N12 must NOT be ported**: the Settings row-density write does not reach a mounted page (no `AppearancePrefs.Bump()`,
  `SettingsPage.Appearance.cs:242-247`) — in 0.3 both writers go through the same epoch (ch 30).

### Setup wizard — pre-shell, first run (owner R)

`Screens/Setup.UI.cs` (Terms / Sign in / Local playback, `QrGrid`, `LoginView`) + `Setup.cs` (`SetupGating`, `SetupLayout`,
`QrPlate`, `Qr`, `WaveeLottieRecolor`) + `Setup.Host.cs` (≈450 UNVERIFIED); the Local-playback page mounts I's
`+Setup.UI.Runtime.cs` · Wave 6 / owner R · chapter 28 (27 frames; W1/W2 Terms wide and compact, W3b the SettingsCard
WRAP band, W4-W7 sign-in challenge / busy / "Is this you?" / failed-expired-premium, W8-W11 Local playback offer →
ready, W12 the footer anatomy, W13 the pre-auth window, W27 the `--qr-dump` artefacts)

```
+-----------------------------------------------------------------------------------------------+
|                                                                                               | 24
|                              .--------------.       Sign in to Spotify                        | 36  Ui.Title
|                              |   LOTTIE     |                                                 |  4
|                              |  "connect"   |       Sign in with your Spotify(R) account on   | 40  Lead, 2 lines
|                              |  192 x 192   |       the Spotify(R) website - or scan the...   |
|                              |              |                                                 | 20  Stack gap
|                              '--------------'    +----------------------------------------+   | 68  SettingsCard MinHeight 68
|                                                  | (globe) Continue in your browser       |   |     HeaderIcon Globe (<=20)
|                                                  |         Opens accounts.spotify.com ... |   |     IsClickEnabled -> StartBrowser
|                                                  +----------------------------------------+   |
|                                                                                               | 20
|                                                  +----------------------------------------+   | 114 = 82 QR + 2x16 pad
|                                                  | (cam) Scan the code with your phone    |   |
|                                                  |       WZY5-Q6TX - spotify.com/pair     |   |  description, one line
|                                                  |       -  Expires in 04:37  <- 1 Hz     |   |
|                                                  |                     +--------------+   |   |
|                                                  |                     | ### ## # ### |   |   |  QrGrid 82 x 82
|                                                  |                     | # # ### # #  |   |   |  white r8 plate
|                                                  |                     | ### # ## ### |   |   |  pure-black modules
|                                                  |                     +--------------+   |   |  quiet zone 4 x cell = 8
|                                                  +----------------------------------------+   |
|                                                                                               | 20
|                                                  Wavee needs Spotify Premium.  Don't have a   | 32  ONE wrapping row:
|                                                  Spotify account? Sign up                     |     Body.Secondary + HyperlinkButton
|                                                                                               | 24
+-----------------------------------------------------------------------------------------------+  1
|      Step 1 of 2                  +-----------------------+ +-----------------------+         | 80
|      ============........50%      |        Log in         | |         Close         |         |
+-----------------------------------------------------------------------------------------------+
  body sum = 40 + 20 + 68 + 20 + 114 + 20 + 32 = 314 <= BodyLaneHeight(490) = 325   (SetupLayout.cs:80-84)
```

*ch 28 W3 - Setup / Sign in / Idle, wide (the 314-of-325 lane)* — `28-setup-whatsnew-feedback.md:500-532`

- The wizard plate is 762 × 490, never the engine's `ContentDialog` (which clamps at 548 × 756): `SetupPlate` reproduces
  that card's chrome by hand (`SetupDialog.cs:129-162`) so it reads as the same dialog family.
- The Lottie hero column is 192 DIP and recolours to the live accent — three Windows-OOBE scenes play the first half
  once and hold, zoomed 1.2× (`WaveeLottie.cs:47`, `WaveeLottieRecolor.cs:47-57`); a static PNG, a loop, or Windows blue
  are each a regression.
- The footer is primary LEFT, secondary RIGHT, with a 210-wide progress column that collapses with the icon column
  below the 770-DIP viewport breakpoint (`SetupDialog.cs:241-254`).
- The sign-in Idle body fits the 325-DIP lane at 314 (`SetupLayout.cs:80-84`) — only while the card is ≥ 476 wide;
  below `SettingsCard.WrapThreshold` the content wraps under the header (W3b) and below 286 the header icon drops;
  `AlwaysShowScrollbar` covers the overflow.
- The QR is pure black on a pure white `Radii.Card` plate, quiet zone 4 modules, no logo, no tint (`QrGrid.cs:46-66`);
  a v4 symbol asked for at 80 paints at 82 (`QrPlate.cs:20-22`).
- Escape cannot strand a user — only a `TermsRearm` may be dismissed; the honest exit from FirstRun / Reauth is
  "Decline" → quit (`SetupGating.cs:93-101`). The after-update plate, crash prompt and runtime toast all stand down
  while the wizard is pending or live (`SetupGating.cs:200`); `SidebarOnboardingChrome` waits for the NEXT launch on
  purpose.

### What's new — `whatsnew` (owner R)

`Screens/ReleaseNotes.UI.cs` (page, highlight viewer, after-update dialog) + `ReleaseNotes.cs` + `+ReleaseNotes.Model.cs` (the
partial `Wavee.ReleaseTool` cherry-picks — both names go in its `<Compile Include>` in Wave 0) + `ReleaseNotes.Host.cs`
(`ReleaseNotesStore`) · Wave 6 / owner R · chapter 28 (W15-W17 loading / empty / stacked, W18/W19 the highlight card
regular and store variants, W21 the viewer at 900 × 600 and the no-poster case, W22 the after-update dialog — a 720-wide
raw `PopupChrome.Modal` plate: `[Updated] [0.2.8 -> 0.2.9.10]` pills, a 26/600 "Welcome to Wavee 0.2.9" headline, three
216-wide highlight cards, a `FillLayerAlt` footer with "Don't show this after updates" + Full release notes / Got it;
heights 505 / 525 / 568 / 588 / 583 / 603 ≤ 620 pinned by `HighlightCardMetricsTests`; W23 the same dialog still loading)

```
<--36--><----------------------- body 842 ---------------------------><14><-- rail 208 --><--36-->
+------------------------------------------------------------------------------------------------+
| (tag22) What's new                                                                             | 16+36+12  header row,
|                                                                                                |           gap 12, PageHero
+------------------------------------------------------------------------------------------------+
| +-------------------------------------------------------------+   | RELEASES            |      | Eyebrow tertiary
| | [Latest] [Stable] [0.2.9.10]                 +-------------+ |   | (o) 0.2.9      YOU  |      | pad 8, r6, gap 8
| |                                              |Open on GitHub| |   |     Breaker - 11 Sep|      | bullet 9 r4.5 bw1.5
| | Wavee 0.2.9 "Breaker"            30/600      +-------------+ |   |                     |      |
| |                                              +-------------+ |   | ( ) 0.2.8        .  |      | unread dot 6 r3 accent
| | One Connect ownership authority, a frame-time |  Copy link  | |   |     Crest - 29 Aug  |      |
| | lyrics clock, visual continuity.   14 sec    +-------------+ |   |                     |      |
| |                                                             |   | ( ) 0.2.7   [BETA]  |      | BetaPill 9.5/700
| | Released 5 Sep 2026   Requires 10.0.19041.0   Tag wavee-v0.2.9 |   |     Drift - 14 Aug  |      |
| +-------------------------------------------------------------+   |         ...         |      | hero: pad 24/22/24/20
|   ^ Fill FillCardSecondary, border 1 StrokeCardDefault, r12         |                     |      |     AlignItems End
|                                                                    | Notes are fetched   |      |
| HIGHLIGHTS                                              Eyebrow    | once per release... |      | RailFoot 11.5 tertiary
| +------------------+ +------------------+ +------------------+     +---------------------+      |
| | [Rebuilt]        | | [New]            | | [New]        (>) |              ^ ScrollView       |
| |  poster 16:9     | |  poster 16:9     | |  poster 16:9     |              ScrollKey          |
| +------------------+ +------------------+ +------------------+              "whatsnew:rail"    |
| | Title, 2 lines   | | Title            | | Title            |                                  | gap 10, AlignItems
| | body 4 lines,    | | body...          | | body...          |                                  | Stretch, card widths
| | last one faded   | |                  | |                  |                                  | Grow 1 Basis 0 MaxW 420
| | Read more >      | | Read more >      | | Read more >      |                                  |
| +------------------+ +------------------+ +------------------+                                  |
|                                                                                                 | gap 16 (Spacing.L)
| (+) Added                       7                                                               | badge 22 r6 wash+ink
| +---------------------------------------------------------------+                               | card r8, FillCardDefault,
| | PLAYER   Docked video now follows the pop-out...  (#118) closed  (C)(J)                       | | border 1 StrokeCardDefault
| |----------------------------------------------------------------|                             | 1-px divider between rows
| | Lyrics ink is sampled from the cover palette...  (!430) merged  (C)                           | | row pad 12/9/12/9, gap 12
| +---------------------------------------------------------------+                               |
|                                                                                                 |
| (v) Fixed                      14        [ Show all 14 ]                                        | Button.Subtle Small
| +---------------------------------------------------------------+                               | fold = 8 rows
| | ...8 rows...                                                    |                             |
| +---------------------------------------------------------------+                               |
|                                                                                                 |
| Issue states as of 5 Sep 2026                            11.5 tertiary, margin-top 6            |
|                                                          + 24-DIP tail spacer                   |
+-------------------------------------------------------------------------------------------------+
  Frame: header pad (36,16,36,12) gap 12 - content row pad (36,0,36,0) gap 14 - body column gap 12
  ScrollView ScrollKey "whatsnew:<selectedVersion>", inner gap 16, pad-right 6   (ReleaseNotesPage.cs:129-167)
  Section order per release: notices (InfoBar) -> Added -> Changed -> Fixed -> Removed -> Deprecated -> Security -> Known
  (document order, not sorted - ReleaseNotesPage.cs:114-120; empty sections are skipped entirely, :116)
  Conditionals in the hero: the [Latest] pill only when this IS the index's newest release; the mono quad pill only
  when doc.PackageVersion is set; "Released" whenever ReleaseNotesText.Date(doc.Date) is non-empty — which is any
  non-blank Date, NOT only a parseable one: Date() ECHOES an unparseable string verbatim rather than blanking it
  (ReleaseNotesLinks.cs:58-64), so a hand-authored "Q3 2026" prints as "Released Q3 2026". The same rule governs the
  stacked-release divider and the rail subtitle. "Requires" only when doc.MinOs is set; "Tag"
  is unconditional. A release with no codename prints whatsNew.headlineBare ("Wavee 0.2.9", no empty quotes).
  [Latest] shows when the index names this as its newest release — AND whenever the index is null or empty
  (IsLatest's bare `return true`, ReleaseNotesPage.cs:172), i.e. on every offline load.
  A release with NO highlights drops the eyebrow AND the row - HighlightStrip returns a 0-height, hit-invisible box.
```

*ch 28 W14 - What's new / fully loaded, one release @ content host 1136 (scale ~12 DIP/char here)* — `28-setup-whatsnew-feedback.md:781-838`

```
 full-window veil rgba(0,0,0,184) - Fill == HoverFill == PressedFill (a dismiss surface must not tint under the cursor)
 root BoxEl Width = vp.W, Height = vp.H, ZStack, Justify/AlignItems Center, Focusable, OnKeyDown
::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
::            <------------------------------ plate 960 ----------------------------->                    ::
::            +-----------------------------------------------------------------------+                   ::
::            | [Rebuilt]  8,8 inset                                     (X) 12,12     |                   ::  close: 36 circle,
::            |                                                                       |                   ::  ChromeClose @ 12
::            |                                                                       |                   ::
::            | (<)                POSTER 960 x 540 (Cover, DecodePx 1200,       (>)   | 540               ::  chevrons: 36 circles,
::            |  12                 Corners (8,8,0,0), Fade(140) reveal)          12   |                   ::  glyph 16, inset 12,
::            |                                                                       |                   ::  vertically centred
::            |                     [ (play) Watch the video on GitHub ]  <- video only|                   ::
::            |                                                                       |                   ::
::            +-----------------------------------------------------------------------+                   ::
::            |                         .  ====  .                                    | 24 (+8 margin-top) ::  pager: dots 6, selected
::            |                         <-8-><-8->                                    |                   ::  capsule 14, gap 8,
::            +-----------------------------------------------------------------------+                   ::  centred cluster
::            |  Setup is three screens                          20/600 LH 28          | 24 pad-l/r,       ::
::            |                                                                       |  8 pad-top (pager)::
::            |  Terms, sign in, local playback - that is the whole wizard now, in a   | 14/20 TextSecondary::
::            |  plain WinUI dialog with an animated hero beside it. The full          | +8 margin-top     ::
::            |  agreement prints inline; the pairing code is minted the moment the    | selectable        ::
::            |  sign-in page mounts, so it cannot expire while you read.              |                   ::
::            |                                                                       | +16 margin-top    ::
::            |  +-------------+                                                      | 32                ::
::            |  |  Try it ->  |   Button.Accent, only when the highlight has an       |                   ::
::            |  +-------------+   Open deep link (or the store CTA - mutually exclusive) 24 pad-bottom    ::
::            +-----------------------------------------------------------------------+                   ::
::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
  plate: Fill FillSolidBase, Corners Radii.Overlay(8), Border 1 StrokeSurfaceDefault, ClipToBounds,
         Shadow(Blur 90, OffsetY 40, #00000099), MaxHeight = vpH - 64      (HighlightViewer.cs:182-196)
  W = round(min(max(320, min(960, vpW-96, (vpH-360)*16/9)), vpW-96)):
      1440x900 -> 960 | 1100x700 -> 604 | 900x600 -> 427 | 500x420 -> 320 | 320x600 -> 224 (the window edge WINS over the floor)
  Band height = round(W * 9/16) ALWAYS - poster or not (L4, issue #89). 960 -> 540, 427 -> 240, 224 -> 126.
```

*ch 28 W20 - Highlight viewer @ 1440 x 900 (plate 960, band 540)* — `28-setup-whatsnew-feedback.md:921-956`

- The page publishes one whole view or nothing — assembled off the UI thread, one signal write; until then a centred
  `ProgressRing(28)`; a failed load is the empty state with the GitHub link, never a hero with no sections
  (`ReleaseNotesPage.cs:23-29, :79-80, :264-268`).
- Reading the page marks the notes seen and `lastSeen` is captured BEFORE the load starts (`:62-77`), or the "since you
  last looked" banner and every unread dot become unreachable by construction.
- The highlight card's body is a fixed 68-DIP slot with a true alpha `EdgeFade` conditional on measured overflow
  (`HighlightCard.cs:273-314`); the viewer never bounces when paging — a ZStack of invisible sizers so the plate's
  height is `band + pager + tallest text` from the first frame (`HighlightViewer.cs:405-431`); chevrons and close never
  move, an end button dims in place; the pager is a hand-rolled dot strip whose selected dot stretches to a 14-DIP capsule.
- Band height = `round(W × 9/16)` ALWAYS, poster or not (issue #89); the after-update plate is 720 wide because
  `ContentDialog` clamps — a raw overlay with `PopupChrome.Modal` (`AfterUpdateDialog.cs:24-25`).

### Report dialog (Bug / Crash) and the crash-reports card — modal, Settings ▸ About (owner R)

`Screens/Feedback.UI.cs` (dialog, composer, chrome, the card mounted by `Settings.UI.cs`) + `Feedback.cs` (CORE: `ReportKinds`,
`ReportBundle`, `IssueFormUrl`, `ReportRedactor`) · Wave 6 / owner R · chapter 28 (W25 the Crash variant with the fixed kind)

```
+--------------------------------------------------------------+   <- ContentDialog card, MaxW 548, Pad 24,
| Report a problem                            20/600 (Title)   |      Fill/stroke/separator/footer = the stock card
+--------------------------------------------------------------+
| Opens a prefilled GitHub form in your browser and copies the  | 14 TextSecondary, wrap, MaxWidth 500
| redacted report to your clipboard. Nothing is sent until you  |
| click.                                                        |
|                                                               | gap 12 (Spacing.M) throughout
| +------+ +---------+ +----------+ +------+                    |    Segmented, bound to kindIndex
| | Bug  | | Feature | | Question | | Idea |                    |    (absent entirely in crash mode)
| +------+ +---------+ +----------+ +------+                    |
|                                                               |
| Title                                                         |    TextBox header
| +-----------------------------------------------------------+ |    Width 500
| | One line that says what's wrong                           | |    placeholder
| +-----------------------------------------------------------+ |
| What happened                                                 |
| +-----------------------------------------------------------+ |    AcceptsReturn, Height 72
| |                                                           | |
| +-----------------------------------------------------------+ |
| Steps to reproduce            [ ...72... ]                    |
| Expected behaviour            [ ...72... ]                    |
| Area                                                          |
| +------------------------------+                              |    ComboBox width 260,
| | Not sure                  v  |                              |    24 options, default = last ("Not sure").
|                                                               |    RAW ENGLISH SLUGS, never localized:
|                                                               |    playback, video, lyrics, player, connect,
|                                                               |    library, playlists, search, home, browse,
|                                                               |    concerts, detail-pages, sidebar, shell, auth,
|                                                               |    setup, updates, store, release-tooling,
|                                                               |    diagnostics, modules, i18n, engine, Not sure
|                                                               |    (ReportKinds.cs:43-48 — pinned to the issue
|                                                               |     form YAML by ReportChannelsTests; §0.12)
| +------------------------------+                              |
| [x] Include diagnostics and the last 200 log lines             |    CheckBox; default true for Crash/Bug
|                                                               |
| Report preview            [ Copy report ] [ Save as... ]      |    12/600 TextSecondary + Spacer + 2 Button.Subtle
| +-----------------------------------------------------------+ |    box 500 x 220, Fill FillSolidBase,
| | Wavee 0.2.9.10 - ARM64 - packaged - Windows 10.0.26340    | |    Border 1 StrokeDividerDefault,
| | ---------------------------------------------------------| |    Corners Radii.ControlAll (4), ClipToBounds
| | Title: Docked video flickers on resize                    | |    ScrollEl ScrollKey "report-preview",
| | What happened: ...                                        | |    EdgeCues None, content pad 10
| | [diagnostics block, already redacted once, off-thread]    | |    TextEl 11 "Cascadia Code" TextSecondary
| | [200 log lines]                                           | |    wrap, MaxWidth 480
| | … (37 KB more in the copied report)   <- PreviewChars cap  | |    cut at 12 000 chars, ENGLISH LITERAL tail
| +-----------------------------------------------------------+ |    (ReportBundle.cs:24, 188-190)
| Personal paths, account details and secrets are removed.      | 12 TextTertiary wrap
| Track names are kept.                                         |
+--------------------------------------------------------------+
|                       +------------------+ +---------------+  |    equal-width command buttons (the stock card)
|                       |   Open GitHub    | |    Cancel     |  |    DefaultButton = Primary
+--------------------------------------------------------------+
  Feature: Problem(72) / Proposal(72) / Area(combo 260) / Alternatives(72).   Question+Idea: Details(TextBox 140).
```

*ch 28 W24 - Report dialog / Bug (ContentDialog 548, content 500)* — `28-setup-whatsnew-feedback.md:1041-1094`

- The preview is monospace, 220 DIP tall, bordered, recomputed per keystroke — the reporter sees what leaves the
  machine, redacted, before pressing anything (`ReportDialog.cs:240-274`); it cuts at `PreviewChars = 12 000` and appends
  the English tail `"… (N KB more in the copied report)"` — keep the cap AND the tail.
- The three dropdowns (`When` 7, `Reproduces` 3, `Areas` 24) are VERBATIM English copied from
  `.github/ISSUE_TEMPLATE/*.yml` and pinned by `ReportChannelsTests`; translating them breaks the GitHub prefill.

### Playback runtime diagnostics, Connect diagnostics, ~~API console~~, logs, FPS overlay — `playback-diagnostics`, `connect-diagnostics`, ~~`api-console`~~, Settings ▸ Logs (owner S)

**~~API console~~ — struck 2026-09-12, not removed: §9.6 Q7 deletes it. The four `ApiDebug*` helpers and the
`ApiConsolePage` they served are dropped; the `api-console` route goes with them.**

`Screens/Diagnostics.UI.cs` (the three pages, the logs panel + log view, the FPS overlay, ~~the API console~~, **and the lyrics
inspector**, A14) + `Diagnostics.cs` + `Diagnostics.Host.cs` (`WaveeLogSessions`, export, crash files) + `Diagnostics.Probe.cs`
(the CLI probe arms incl. `--lyrics-advance-probe`, `--qr-dump`, the notification simulator) + ~~`+Diagnostics.Api.cs` (1,400,
Q7: keep or delete)~~ — **DELETED, §9.6 Q7, 2026-09-12** · Wave 6 / owner S · chapter 27 (W24 Connect diagnostics — five
read-only blocks, label width 160; ~~W25 the API console~~; W26 the FPS overlay — renders exactly once, two retained
`DynamicText` slots; W21 the Logs tab — the ONE tab not scrolled by the page, a `CommandBar` over a session `ComboBox`, a
filter row with the level `Segmented` and two headed level `ComboBox`es, a virtualized row list with fixed columns and a
"Showing 500 of 3 214 events" footer; W22 loading a past session)

```
│ ♪ 22  Local playback diagnostics                         ← PageHeader Pad (16,16,16,12), Icon 22 + Ui.Title          │
│ ↕16                                                                                                                   │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ Status block:      │
│ │ ✓ 18   Local playback support is compiled into this build                                      │ Pad 12 all,        │
│ │        The app can search for and load a local Spotify.dll. Anything below is about THAT search.│ Fill FillLayerAlt, │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘ r = Radii.Control 4,│
│ ↕12                                                                    glyph 18 DIP in Theme.IconFont, tinted:       │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────┐ Success / Critical/│
│ │ Current status                             ← 12/600 TextSecondary card title                   │ Attention          │
│ │ Outcome              Ready                 ← Row: label 12 TextSecondary Width 132 Shrink 0,   │ heading 14/600,    │
│ │ Detail               —                       value 12 TextPrimary wrap Grow 1, gap 8           │ body 13 TextSecondary│
│ │ Pack                 spotify-1.2.63.394                                                        │                    │
│ │ Spotify version      1.2.63.394                                                                │ Card: Dir 1, Gap 6,│
│ │ Architecture         Arm64                                                                     │ Pad 12, FillLayerAlt│
│ │ Runtime path         C:\Users\…\Wavee\playplay\runtimes\…                                      │ r 4, Border 1      │
│ │ Signature trust      Trusted                                                                   │ StrokeCardDefault  │
│ └────────────────────────────────────────────────────────────────────────────────────────────────┘                    │
│ ┌ Locate     Outcome / Reason                                                                    ┐                    │
│ ┌ Candidates  «source»  [✓ Spotify.dll] [✕ playplay-runtime.json]    ← Chip: Pad (6,1,6,1), r 4, │                    │
│ │             C:\…\path                     Fill SystemFillSuccessBackground / SystemFillCritical-│                    │
│ │             ──── 1 DIP between candidates ──── Background; text 11 DIP "✓ "/"✕ " + label        │                    │
│ ┌ Verify      outcome/detail/trust + publisher/issuer/thumbprint/reason/valid/file                ┐                    │
│ ┌ Playback modules  «DisplayName» [bundled] [Ready] · Id/Version/Publisher/Directory/Process/      ┐                    │
│ │                   Capabilities/Requests/Latency/Last error/Status · [Retry] Faulted/Crashed     │                    │
│ │                   "Refused" sub-list: dir + reason pairs                                        │                    │
│ ┌ Updates     Feed URL / Channel / Package version / State / Auto-update association / Last       ┐                    │
│ │             checked / Last failure + 6 release-notes receipts + "Repair auto-update" + hint      │                    │
│ ┌ [ Copy diagnostics ]  [ Open log folder ]  [ Refresh ]     ← HStack(8), first is Accent         ┐                    │
│   This report is what the provisioner already computed…      ← Caption 11 TextTertiary wrap       │                    │
```

*ch 27 W23 — Playback runtime diagnostics page @ pane 1072* — `27-settings-and-diagnostics.md:1157-1187`

### Lyrics inspector dialog — developer mode (owner S)

`Screens/Diagnostics.UI.cs` (`Diagnostics.LyricsInspector`, ≈600, moved out of the lyrics files by ch 22 (d) / A14) over K's
`Lyrics.Host.cs` report store · Wave 6 / owner S · chapters 22 + 27 (ch 22 W18/W19 the Raw and Parsed tabs, W19b the
non-report states)

```
┌──────────────────────────────────────────────────────────────────┐ ContentDialog, DialogWidth 548
│ Lyrics source inspector                                          │ title = player.lyricsInspector
├──────────────────────────────────────────────────────────────────┤ content width 492, gap 12
│ [Copy full report] [Save bundle…] [Refresh] [Re-fetch from prov…]│ Button.Standard ×3 + Button.Accent
│ Re-fetch drops this track from the memory AND disk lyric caches… │ 11/15 TextTertiary, wrapped
│ ┌ Providers ┬ Raw response ┬ Parsed ┐                            │ SelectorBar bound to _tab
│ └───────────┴──────────────┴────────┘                            │
│ amll won on timing (syllable) over lrclib                        │ 13/18/600 AccentTextPrimary
│ “Every night in my dreams” — Céline Dion                         │ 12/16 TextPrimary
│ album My Heart Will Go On · 274s · ISRC USSM19900123 · id 4X…    │ 11/15 TextTertiary
│ ───────────────────────────────────────────────────────────────  │ 1px StrokeCardDefault
│ ┌──────────────────────────────────────────────────────────────┐ │ card: pad 10, r6,
│ │ ● amll                              HIT · 412ms              │ │ Fill FillSubtleSecondary,
│ │ ★ CHOSEN — reranker score 0.93 (timing + text)               │ │ 1px border = AccentDefault
│ │ parsed: Syllable, 42 lines, 310 syllables, matched by         │ │ when winner, else StrokeCardDefault
│ │ Identity, prior 0.90                                          │ │ dot 8×8: Hit green (.30,.78,.45)
│ │ timing: clean                                                 │ │ Timeout amber, Error red,
│ │ [Raw (1)] [Parsed]                                            │ │ Skipped dim, Miss grey
│ └──────────────────────────────────────────────────────────────┘ │
│ ┌ ● lrclib   MISS · 980ms  not chosen — the provider had nothing… ┐│
├──────────────────────────────────────────────────────────────────┤
│                                                        [ Close ] │ CloseText = common.close
└──────────────────────────────────────────────────────────────────┘
```

*ch 22 W17 — Inspector dialog · Providers tab (8 DIP/char)* — `22-lyrics.md:501-525`

- Nothing in this surface reads a live clock per frame: About's receipts tick on a 5 000 ms interval, the log tail
  polls at 750 ms and bumps only when `WaveeLog.Instance.Version` moved, the FPS overlay renders ONCE
  (`FpsOverlay.cs:16-21, :37-42`) — an FPS HUD that re-renders per frame depresses the number it displays.
- `connect-diagnostics` is renderable by `PageFor` but absent from `ShellRoutes.s_exact` — its deep link is refused and
  its tab is labelled "Your Library"; §4.11's route table fixes it by construction but **the GitHub issue comes first**
  (Q6).
- The log row's severity dot is drawn ONLY for Warning and ≥ Error; Trace / Debug / Info get a blank 6×6 spacer so the
  timestamps stay aligned (`LogsPanel.cs:533-538`); Capture level / File log level are two headed `ComboBox`es in the
  filter row, not radio flyouts in the overflow (`:229-235`, the code wins over the logs-page plan).

### Module page and watch page — `module:<uri>` (owner T)

`Platform/Modules.UI.cs` (the module page and the watch page) + `Modules.cs` (CORE: `WatchPageModel`, `PlayableLinks`) +
`Modules.Host.cs` (the Sdk host, module items → tables, playables → `Playback.Open`) · Wave 6 / owner T · chapter 09
(W11 the derived skeleton, W12 FAILED, W14 the stage LIVE, W15 at 700 — the stage never demotes, W16 the playable row's
menu, W17 the `custom` template and the entity-hero fallback, W18 playing but hosted ELSEWHERE — poster while playing
is a legitimate state, W19-W21 minimal / cold / failed-with-cache)

```
   ▲ 84 DIP masthead reserve (root Padding.Top = BrowseLayout.MastheadReserve = Spacing.XXXL 32 + TitleLine 52)
┌ ScrollView · content pad L32 T40 R32 B112 ──────────────────────────────────────────────────────────┐
│ ┌ "module-hero" · Dir 0 · gap 20 · AlignItems Center · Wrap true ─────────────────────────────────┐ │
│ │ ┌──────────────────┐                                                                            │ │
│ │ │                  │  Channel                          eyebrow 12/16/600 tr30 TextTertiary      │ │
│ │ │   232 × 232      │  Entity title           ▭LIVE▭    Ui.Title 28/36/600  +  18-tall badge      │ │
│ │ │   r8 · dec 256   │  Owner name (link)                14/20 Secondary → Primary+underline hover │ │
│ │ │                  │  1.2M views · 2 days ago          Caption 12/16 secondary, ≤2 lines, wrap   │ │
│ │ └──────────────────┘  column: Grow 1 · Basis 0 · gap 8 · Justify Center                          │ │
│ └─────────────────────────────────────────────────────────────────────────────────────────────────┘ │
│  gap 20                                                                                             │
│ ┌ "module-actions" · Dir 0 · gap 8 · Wrap ─────────────────────────────────────────────────────────┐│
│ │ ▛▀▀▀▀▀▀▀▀▜ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜    Button.Accent (Primary) / Button.Standard — STOCK Fluent rectangles││
│ │ ▙  Play  ▟ ▙ Open in browser▟   (NOT WaveeCta pills: r4, MinHeight 32)                           ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│  gap 20                                                                                             │
│ ┌ sec:0:facts — Section shell: RailHeader + body, gap 12, Enter FadeUp, Layout Shove ──────────────┐│
│ │ About                                                                                            ││
│ │ ┌─────────┐ ┌─────────┐ ┌─────────┐   StatTile: Grow 1 Basis 0, pad (12,8,12,8), r4,             ││
│ │ │1.2M     │ │2 days   │ │12:04    │   Fill FillCardSecondary, 1px StrokeCardDefault,             ││
│ │ │Views    │ │Published│ │Length   │   value 18/800 TextPrimary, caption 11 TextSecondary         ││
│ │ └─────────┘ └─────────┘ └─────────┘   row: gap 8, Wrap true, Stagger 45 ms                       ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:1:text ──────────────────────────────────────────────────────────────────────────────────────┐│
│ │ Description                                                                                      ││
│ │ Prose at 14, Tok.TextSecondary, links Tok.AccentTextPrimary, clamped to 6 lines with an inline   ││
│ │ "… More" suffix on the last line (RichText.ExpandableFlex).                                      ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:2:playables ─────────────────────────────────────────────────────────────────────────────────┐│
│ │ Up next                                                                                          ││
│ │ ┌ TrackRow.ArtCard(Rail) in a r4 box, HoverFill FillSubtleSecondary, right-click = module menu ─┐││
│ │ │ ▣40  Item title                                                    (no duration, no heart)   │││
│ │ │      🎞 · Subtitle                                                                            │││
│ │ └──────────────────────────────────────────────────────────────────────────────────────────────┘││
│ │   row shell: MinHeight 52, gap 12, pad (4,2,4,2), art 40 (WaveeSize.ArtThumb) at r4             ││
│ │   state: TrackRow.StateOf(bridge, lib, track) — the module row DOES take the now-playing re-skin ││
│ │          (accent title + equalizer) and the paused variant; the wrapper adds the r4 hover veil   ││
│ │   the context menu is attached ONLY when acts AND overlay are both non-null (ModulePage.cs:568)  ││
│ │   rows: Dir 1, gap 2, revealed in DetailRevealRamp.Chunk(12)-sized slices                        ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:3:cards — Dir 0, gap 12, Wrap true; MediaCard.Shelf(cardW = 168) ───────────────────────────┐││
│ │ ┌────┐ ┌────┐ ┌────┐ ┌────┐ ┌────┐                                                              │││
│ │ │168 │ │168 │ │168 │ │168 │ │168 │   the wrap row measures the cards; MediaCard.ShelfHeight     │││
│ │ └────┘ └────┘ └────┘ └────┘ └────┘   (cardW + 72 = 240) is the VIRTUALIZED shelf's reserve and  │││
│ │   a cell whose uri is the item in the bar takes the      is not applied here (MediaCard.cs:212) │││
│ │   now-playing overlay; hover reveals the play FAB —                                             │││
│ │   LazyNowPlayingOverlay, keyed on cardUri = ModuleUri.Encode(moduleId, playableId)              │││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ sec:4:links — Dir 1, gap 2 ──────────────────────────────────────────────────────────────────────┐│
│ │ ┌ pad (12,8,12,8) r4 HoverFill Subtle² Cursor Hand Role Hyperlink Focusable ──────────────────┐  ││
│ │ │ Website                                                                             ↗ 14   │  ││
│ │ │ example.com                                                    12/16 TrackMeta             │  ││
│ │ └────────────────────────────────────────────────────────────────────────────────────────────┘  ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
└──────────────────────────────── bottom pad 112 = PlayerDock.Reserve 72 + 40 ────────────────────────┘
```

*ch 09 W10 — Module page, `entity` template, loaded @ 1280 (page ≈ 1040)* — `09-show-episode-module.md:550-606`

```
   ▲ 84 DIP masthead reserve
┌ "module-stage" — Shrink 0, AspectRatio 16/9, MaxHeight 560, Fill #000000FF, ClipToBounds ───────────┐
│                                                                                                     │
│                      poster: ArtworkFill at Opacity 0.40 over the black ground                      │
│                                                                                                     │
│                                        ⎛        ⎞                                                   │
│                                        ⎜   ▶    ⎟   64-DIP disc, Radii.Full, ButtonAppearance.Accent│
│                                        ⎝        ⎠   glyph box 16 DIP, tooltip "Play"                │
│                                                                                                     │
│  height h = pageWidth / (16/9); at 1040 → 585 → CLAMPED to 560, the ELEMENT pillarboxes inside      │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ ScrollView → content pad (32,40,32,112) → "watch-caption" Dir 1 gap 12 ─────────────────────────────┐
│ The video title, up to two lines, wrapping            Ui.Subtitle 20/28/600 (NOT PageHero)          │
│ ▭LIVE▭  1.2M views · 2 days ago                       badge h18 + Caption 12/16 secondary ≤2 lines  │
│ ⬤ 40   Channel name                                   PersonPicture 40 + BodyStrong 14/20/600       │
│   pad (8,4,12,4) · r999 · HoverFill Subtle² when the channel has an entity id                       │
│ ▛▀▀▀▀▀▀▀▀▜ ▛▀▀▀▀▀▀▀▀▀▀▀▀▀▜ ⊙   capsules h36 r999 · gap 8 · Wrap · AlignItems Center · then a 36 round │
│ ▙ ▶ Play ▟ ▙ ↗ Open on YT ▟ ⋯   "…" — which exists ONLY when a play chip carried a PlayableId AND     │
│                                 acts + overlay are non-null (WatchPageView.cs:286-294).              │
│                                 glyph per kind: play ⇒ Icons.Play, openUrl ⇒ Icons.OpenInNewWindow,   │
│                                 moduleAction ⇒ NO glyph (WatchPageView.cs:276-278). Accent only where │
│                                 the document said Primary; every other capsule is Standard.           │
│ ┌ "watch:description" pad (16,12,16,12) r8 FillCardSecondary 1px StrokeCardDefault gap 8 ──────────┐│
│ │ 1.2M views · 2 days ago                        Ui.Body 14/20 at Weight 600 — the DISSOLVED facts ││
│ │ The module's prose, 14, Tok.TextSecondary, clamped to 3 lines with "… More"                      ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌ "watch:shelf" — PagedShelf(measured: true, maxItems 16, header Surfaces.SectionHeader) ──────────┐│
│ │ Up next                                                                          ‹  ›  chevrons  ││
│ │ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐                                      ││
│ │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │ │ 16:9 ▣  │  MediaCard.VideoCard, cardW fitted   ││
│ │ │ Title   │ │ Title   │ │ Title   │ │ Title   │ │ Title   │  into [150, 200], gap 12             ││
│ │ │ Sub · m │ │ Sub · m │ │ Sub · m │ │ Sub · m │ │ Sub · m │  edgeFade 36                         ││
│ │ └─────────┘ └─────────┘ └─────────┘ └─────────┘ └─────────┘                                      ││
│ │   cell states: hover reveals a 44-DIP play FAB over the thumb (LazyNowPlayingOverlay, armed only  ││
│ │   after HoverMotionGate sees real travel) and the cell whose uri is the item in the bar takes the ││
│ │   now-playing overlay; title 1 line ellipsised; an item with no subtitle AND no meta drops the    ││
│ │   third line to an empty box (MediaCard.cs:908-914, WatchPageView.cs:392-399)                     ││
│ └──────────────────────────────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

*ch 09 W13 — Watch page, stage IDLE (poster) @ 1280 (page ≈ 1040)* — `09-show-episode-module.md:648-688`

- The module page is drawn in the app's own detail vocabulary and never switches on a module id — one hero (232 art,
  `Ui.Title`, eyebrow, link subtitle, meta), STOCK Fluent action buttons, typed sections; an unknown section kind is
  skipped (`ModulePage.cs:283-310, :482-484`); THREE templates — `entity`, `custom`, `watch` (`:286-293`).
- The loading state is DERIVED from the page against a figure-space seed — three U+2007 strings, no hand-authored
  second tree (`:65-72, :147-156`).
- The watch stage is a full-width 16:9 black box pinned ABOVE the scroller with zero motion of its own — outside
  `Skel.Region`, outside `Section()`, outside the `ScrollView` (`WatchPageView.cs:31-46, :121-140`); idle → live must not
  reflow, the one transition is `PosterMotion` dissolving `PosterGround`; the idle affordance is one 64-DIP accent disc.
- The stage's video mount is gated on the RESOLVED placement, not on "is this playing" (`DockedVideoHosting.cs:140`);
  already-playing states the fact with an inert "Playing" badge, never a dead button; nothing is invented — an action
  whose kind cannot be honoured is ABSENT, a name with no entity id is inert text; ONE item budget of 500 spent in
  document order (`:299-307`).

### First run, end to end — the composite a brand-new account meets (owners I + R)

`Shell/Shell.cs` (the first-run composite, `SetupGating`) + `Screens/Setup.cs` · Wave 4 + 6 / owners I + R · chapter 29 (W24)

```
 ── t = −1 : THE WIZARD OWNS THE WHOLE WINDOW ───────────────────────────────────────────────────────
   needsSignIn ⇒ the ONLY leaf is SetupPreAuthRoot (WaveeApp.cs:403-405). There is no shell behind it —
   no chrome row, no sidebar, no player bar. SetupSession(FirstRun) walks Terms → SignIn → LocalPlayback
   (SetupPage, SetupGating.cs:14). An install that COMPLETED setup and then signed out enters at
   SetupEntryPoint.Reauth, straight to the SignIn page — re-walking terms would be nonsense (:365-368).

 ── t = 0 : THE WIZARD CLOSES. THE SHELL MOUNTS. NOTHING HAS ARRIVED YET. ───────────────────────────
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48
│☰  ◀ ▶ │Home│ ＋            [ 🔎 Search songs, artists, albums... ]        ◉ <name>  🔔 👥 📌 ⚙│⌄ ─□✕│
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤
│ ▸ Home       │  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ← Home's SKELETON, derived from the HomeSeed      │
│ ▸ Search     │  ░░░░░  ░░░░░  ░░░░░  ░░░░░           silhouette: hero, weekly pair, quick grid,     │
│ ▸ Your Library│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░     recents, mix band, chips, radio, queue, books, │
│              │  ░░░░░  ░░░░░  ░░░░░  ░░░░░           featured, podcasts, topic, section, discover   │
│  ── Playlists│                                        (FakeData.cs:587-610 — real shape, blank text)│
│   (EMPTY —   │                                                                                      │
│    no rows,  │  every OTHER surface at t=0:                                                         │
│    no        │    right rail        CLOSED (session chrome restored from an empty session.json)     │
│    skeleton) │    tabs              exactly ONE tab, "Home", unpinned                               │
│              │    back / forward    both DISABLED (an empty history stack)                          │
│              │    masthead band     ABSENT (Home is not a masthead family)                          │
│              │    shell material    NEUTRAL — Home has published no wash yet (ContentHost.cs:51-55) │
│              │    notification bell  no badge                                                       │
│              │    focus             wherever the wizard left it — NOTHING moves it (W3 path 5)      │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤ 72
│  ◀◀  ▶  ▶▶     Nothing playing                     ──────────────      🔊 ━━━━●──   ♥ ⋯           │ ← MOUNTED and
└────────────────────────────────────────────────────────────────────────────────────────────────────┘   resting, not
  OS SURFACES AT t=0: idle in all four (ch 14 W5, W9, W12, W16 draw them).                               absent

 ── t = first catalog answer (the silent resume completes; rootlist + home land) ────────────────────
   sidebar   the playlist rows APPEAR. This is the ONE surface that goes straight from empty to
             populated with no skeleton in between; ch 25 W4 owns the PINNED band's own empty state.
   home      ONE staggered reveal, 40 ms apart, 8-DIP rise, blur→sharp (ch 10 parity 2-3). NOT a
             partial paint: skeleton → hold → one reveal.
   material  Home publishes its three washes; the chrome eases from neutral over 250 ms (W7 channel 3)
   bell      may gain a badge — and the FIRST feed rebuild raises NO toast, because the watermark is
             zero and a zero watermark only records where the feed was (ToastEscalator.cs:25-26, :64)

 ── t = steady (a genuinely EMPTY account: no playlists, no likes, no history, no recents) ──────────
   sidebar        its own empty state (ch 25 W4)          library pages    ch 15 W9/W10
   home           the server's own empty/greeting feed     recents          ch 16
   liked          ch 07 W19                                search           ch 13 W11
   history page   empty (nothing navigated yet)            jump list        ch 14 W16
   RULE: at no point does a surface show a SKELETON for data that will never arrive. The skeleton is
   for "a request is in flight"; the empty state is for "the answer was nothing". A new account crosses
   from one to the other exactly once per surface, and never back.  → §7.2 generalises it.

 ── the wizard re-entered POST-AUTH (Appearance / Sidebar pages, or a terms re-arm) ─────────────────
   The shell is up and the wizard sits over it. The shell paints its OWN scrim, because only the shell
   knows whether the current page wants an ordinary dim (SetupCover.Dim) or the lifted live-preview look
   (SetupCover.Live — where the wizard's promise is "this window IS the preview").
   SetupCoverScrim: Tok.FillSmoke, cross-faded over WaveeMotion.Standard, 0 ms under reduced motion,
   HitTestVisible=false, always mounted, reading the static SetupSession.Covering signal itself.
   (WaveeShell.cs:2272-2289 · the engine's popup scrim paints only for the BARE pre-auth mount)
```

*ch 29 W24 — first run: the composite frame a brand-new account meets (1 char ≈ 16 DIP)* — `29-cross-cutting.md:1585-1640`

- At `t = 0` the shell mounts with Home's SKELETON derived from the `HomeSeed` silhouette, the sidebar's Playlists
  band EMPTY (no rows, no skeleton — the one surface that goes straight from empty to populated), exactly one tab,
  Back/Forward disabled, the material NEUTRAL, the player bar MOUNTED and resting.
- At no point does a surface show a SKELETON for data that will never arrive; a new account crosses skeleton → empty
  exactly once per surface, and never back (ch 29 §0.19, §7.2).
- The FIRST feed rebuild raises NO toast — the zero watermark only records where the feed was
  (`ToastEscalator.cs:25-26, :64`).

---

## 10. The tree

Plan §2, final and chapter-derived — 136 files, 170,015 lines (revised 2026-09-12 per §9.6: `Entities/` +80 for the
album merch table (Q4), `Screens/` −1,400 for the deleted API console (Q7) and +200 for `SetupSession` folding into
`Setup.cs` (Q8)); `+` marks a named partial Wave 0 creates empty on day one; owner / wave / budget per file.
`Spotify/` and the non-UI half of `Playback/` are the plan's own numbers (no chapter covers them).

```
src/apps/Wavee/
├── App.cs                                     —   0    400   Main, window, composition order, Glyphs before FluentAppHarness.Run
│
├── Entities/                      57 files  59,050
│   ├── Entities.cs                            A   1  1,240   kinds, uri parse, columns/slabs, StringId, scope sets, signals, factories
│   ├── Edges.cs                               B   1    980   CSR edge tables + payload structs; Insert/Remove/Settle, ReplaceRun; + MerchTable + EdgeTable<NoEdge> AlbumMerch (Q4, 2026-09-12)
│   ├── Store.cs                               C   1  1,200   sqlite schema, Ensure(span), write-behind, GC
│   ├── Fetch.cs                               C   1    700   wanted & ~known & ~inflight planner, batches, epoch
│   ├── Palette.cs                             A   1    400   CORE (A5) the per-image graded schemes, TTL, Watch, Ensure
│   ├── Palette.Host.cs                        L   4    250   SHELL (A5) the debounced pump, the filler, the tone plane's mount
│   ├── Detail.cs                              M 4.5    900   CORE (A1) breakpoints, vertical arithmetic, rail policy, ContextBandLayout
│   ├── Detail.UI.cs                           M 4.5  2,600   UI (A1) Frame, Hero, Rail, CompactRail, ContextBand, Skeleton, NoticeBar, TonePlane
│   ├── Track.cs                               M 4.5  1,200   CORE (A2) handle, columns, field groups, every pure rule of the surface
│   ├── Track.UI.cs                            M 4.5  2,600   (A2) the ROW: grid + 11 lanes, the # machine, both skins, ArtCard, menus
│   ├── Track.Table.cs                         M 4.5  2,600   (A2) the detail table; O configures via TableProfile, never edits
│   ├── Track.Drawer.cs                        M   5    700   (A2) the expanded-row body: facts, versions, gutter rail, format ladder
│   ├── Album.cs / Album.UI.cs / Album.Page.cs M   5  400 / 800 / 1,500   incl. the whole prerelease: surface
│   ├── Artist.cs / Artist.UI.cs / Artist.Page.cs / Artist.Discography.cs
│   │                                          N   5  550 / 1,500 / 2,200 / 1,300   (+Artist.UI.Chart.cs named day one)
│   ├── Playlist.cs / Playlist.UI.cs / Playlist.Page.cs
│   │                                          O   5  900 / 1,400 / 2,300   incl. the local route's DetailKind.Playlist arm
│   ├── Show.cs / Show.UI.cs / Show.Page.cs    M   5  180 / 320 / 700
│   ├── Episode.cs / Episode.UI.cs             M   5  200 / 400
│   ├── User.cs / User.UI.cs / User.Page.Library.cs / User.Page.Liked.cs
│   │                                          O   5  800 / 600 / 1,300 / 700   (A3: no User.Page.Profile.cs, no RouteKind.User)
│   ├── User.Liked.cs / User.Facts.cs / User.Facts.UI.cs / User.Cover.cs
│   │                                          O   5  500 / 1,000 / 1,800 / 1,700
│   ├── Home.cs / Home.UI.cs / +Home.Cards.UI.cs / +Home.Artists.UI.cs
│   │                                          P   5  1,800 / 1,600 / 1,250 / 650   (A15, the seven-file Home set)
│   ├── Home.Page.cs / Home.Customizer.cs / Home.Host.cs
│   │                                          P   5  2,230 / 300 / 400   Home.SectionPage serves home-section: AND browse-section:
│   ├── Search.cs / Search.UI.cs / Search.Page.cs
│   │                                          P   5  450 / 1,100 / 550
│   ├── Browse.cs / Browse.UI.cs / Browse.Page.cs
│   │                                          P   5  500 / 650 / 700
│   ├── Concert.cs / Concert.UI.cs / Concert.Page.cs
│   │                                          N   5  1,050 / 900 / 1,900
│   ├── Queue.cs / Queue.UI.cs                 Q   5  250 / 900   the rail panel + the stage pane, two skins
│   ├── Recents.cs / Recents.UI.cs / Recents.Page.cs
│   │                                          P   5  650 / 550 / 1,150
│   └── Entities.Fake.cs                       Q   5  1,100   Entities.SeedFake() — SEED-CORE 600 + SEED-SURFACES 500; ch 31 is its spec
│
├── Spotify/                        8 files   9,420   (+ Protos/: 42 .proto, moved in Wave 0)
│   ├── Spotify.cs                             D   2  1,400   CORE: Session, Shannon, DH, hashcash, PKCE, base62, dealer frame
│   ├── Spotify.Session.cs                     D   2  1,600   SHELL: AP socket, login5, tokens, dealer loop, audio keys
│   ├── Spotify.Api.cs                         F   2  1,800   SHELL: one function per request
│   ├── Spotify.Decode.cs                      E   2  1,600   CORE: wire → staging; RecentsList.Group; the offline fixture path
│   ├── +Spotify.Decode.Pathfinder.cs          E   2    820   CORE: the Utf8JsonReader pathfinder folds
│   ├── Spotify.Audio.cs                       F   2  1,200   SHELL: fileId/format/key/CDN + AES-CTR stream
│   ├── Spotify.Connect.cs                     F   2    400   SHELL: spirc glue
│   └── Spotify.Telemetry.cs                   F   2    600   SHELL: Gabo, Herodotus
│
├── Playback/                       5 files   6,780
│   ├── Playback.cs                            G   3  1,790   CORE: State, Input, Effects, Step, ownership; SmtcTimelineCoalescer; TimeFormat/LiveRail
│   ├── Playback.Host.cs                       G   3    800   SHELL: Post loop, signals, Pending, ticker
│   ├── Playback.Audio.cs                      H   3  2,250   SHELL: the pump over FluentGpu.Media, AudioSource ×5, gapless; SilentSink for --fake
│   ├── Playback.Video.cs                      H   3  1,100   SHELL: the decode/host ONLY (A13) — not one pixel of UI
│   └── Playback.Os.cs                         H   3    840   SHELL: SMTC, taskbar + thumbnail toolbar, jump list, app icon; ch 14 is its contract
│
├── Shell/                         27 files  56,200
│   ├── Shell.cs                               I   4  2,100   CORE: nav, routes, deep links, layout, tint ownership, first run, WaveeTipsCore, Shell.History, Shell.Ui rail state
│   ├── Shell.UI.cs                            I   4  2,600   window frame, chrome row, tab strip, drill trail, drawer, flyout, not-found, network chrome, file-drop, the notification panel (A9)
│   ├── +Shell.Masthead.UI.cs                  I   4    950   masthead band, material layer, omnibar + suggestion popup
│   ├── +Shell.PlayerBar.UI.cs                 I   4  2,000   player bar, seek bar, tiers, device picker + the roster it reads, the four toggles
│   ├── +Shell.Overlays.UI.cs                  I   4  1,700   profile chip + menu + Play ▸, logout, play-a-link, runtime banner + gate, signature dialog, tips, toast sites, the Setup.UI.Runtime mount
│   ├── Shell.Palette.cs                       I   4    600   the command palette (Ctrl+K)
│   ├── +Shell.History.UI.cs                   I   4    400   the history page
│   ├── Shell.Host.cs                          I   4  1,000   SHELL: window, activation, session snapshot, history/play-log/recency json, the tips host, file-drop hook
│   ├── Sidebar.cs                             J   4  5,000   CORE (A4) projection / binder / planner / sources / geometry / drop / selection / edit; PinRowRule (A7)
│   ├── Sidebar.Doc.cs                         J   4  4,000   (A4) the sidebar-layout.json document + reducer + wire + migrations + defaults
│   ├── Sidebar.UI.cs                          J   4  7,500   (A4) the pane, the three designs, the rail, folder flyout, drag cues, multi-select, Move-to-folder, options popover
│   ├── Sidebar.Customizer.UI.cs               J   4  4,500   (A4) the sidebar-customize page — sequenced LAST
│   ├── Sidebar.Host.cs                        J   4  2,500   (A4) SHELL: file I/O + debounce, store/binder half, the four extension data-source hosts
│   ├── Rail.cs                                K   4    200   CORE: RailVideoCoupling, NpvPlayerCatalog, NpvPlayerPrefs, NpvDiagnostics
│   ├── Rail.UI.cs                             K   4  1,400   rail frame docked + floating, header, docked-cap body, NPV panel, hero tile, lyrics peek, friends, art menu
│   ├── +Rail.Styles.UI.cs                     K   4    600   player-style flyout, thumbnails, art menu, swatches
│   ├── Stage.cs                               K   4    500   CORE (A12) StageLayout, StageArm, StageInk, StagePane
│   ├── Stage.UI.cs                            K   4  1,150   (A12) StageChrome, StageIdentity, all of StagePanes; the lyrics pane's FRAME
│   ├── Deck.cs                                K   4  1,500   CORE: the 13 model classes verbatim + Fold + the catalog
│   ├── Deck.UI.cs                             K   4  1,500   host, clock, signals, art, gesture, dispatch, the record family
│   ├── +Deck.Faces.cs                         K   4  2,400   the eight non-record faces
│   ├── Lyrics.cs                              K   4  3,700   CORE: Fx, BlurPolicy, SyncGate, RowShape, MediaClock, PeekClock, Emphasis, Cascade, Prefs
│   ├── Lyrics.UI.cs                           K   4  3,500   the view, the row, the frame driver, ticker/stepper, the NPV peek, the stage's lyrics pane (A12)
│   ├── Lyrics.Host.cs                         K   4  2,300   SHELL: fetch aggregator, sources, rerank, disk cache, upgrades, per-track diagnostics
│   ├── Video.cs                               K   4    900   CORE (A13) the nine pure rule classes + PlacementCore / PlacementState
│   ├── Video.UI.cs                            K   4  1,450   (A13) docked cap, PiP (8 zones), fullscreen, the watch stage, the placement menu
│   └── Video.Host.cs                          K   4    250   (A13) SHELL: the pop-out window (own HWND), the IDetachedVideoWindow seam
│
├── Screens/                       22 files  19,000
│   ├── Settings.cs                            R   6    700   CORE: SettingsCatalog, SettingsGlyphs, the tab model
│   ├── Settings.UI.cs                         R   6  1,200   page shell, tabs, General, Notifications, the Logs mount
│   ├── +Settings.UI.Appearance.cs             R   6    700   three expanders, four collapsed picker groups, six writers, preview cards
│   ├── +Settings.UI.Playback.cs               R   6    800   equalizer, crossfade, the video-overrides card
│   ├── +Settings.UI.Storage.cs                R   6    350   the hue set, bar segments, row accents
│   ├── +Settings.UI.About.cs                  R   6    350   receipts, the GPU line, the crash-reports card mount
│   ├── +Settings.UI.Video.cs                  K   4    280   the video-override manager flyout BODY — written by K, mounted by R
│   ├── Settings.Host.cs                       R   6    700   SHELL: the settings store
│   ├── Diagnostics.cs                         S   6    600   CORE: LogView, LogCapturePolicy, BuildReport, GpuSummary, StageRects
│   ├── Diagnostics.UI.cs                      S   6  2,300   (A14) the two diagnostics pages (runtime, Connect — API console DELETED, Q7), logs panel + view, FPS overlay, the lyrics inspector
│   ├── Diagnostics.Host.cs                    S   6    500   SHELL: WaveeLogSessions, rolled-file discovery, export, CrashReportFiles.List
│   ├── Diagnostics.Probe.cs                   S   6    800   --perf-bench, --startup-bench, --crash-probe, --lyrics-advance-probe, --qr-dump, NotificationSimulator
│   │   (+Diagnostics.Api.cs — DELETED, §9.6 Q7, 2026-09-12: ApiDebug* + ApiConsolePage have no home in 0.3; the row is gone, not struck, per the plan's own tree)
│   ├── Setup.cs                               R   6  1,150   CORE: SetupGating, SetupCommands, SetupEntryPoint, SetupLayout, SetupBootstrap, QrPlate, Qr, WaveeLottieRecolor, SetupSession/SetupSession.MarkerEpoch (Q8, orchestrator's call)
│   ├── Setup.UI.cs                            R   6  1,250   the wizard (Terms / Sign in / Local playback), QrGrid, LoginView
│   ├── +Setup.UI.Runtime.cs                   I   4  1,100   (A18) the 10-phase runtime provisioning card BODY — written by I, mounted by R
│   ├── Setup.Host.cs                          R   6   ≈450   SHELL: the runtime provisioning half (UNVERIFIED)
│   ├── ReleaseNotes.cs                        R   6  1,000   CORE: parsers, links, validation
│   ├── +ReleaseNotes.Model.cs                 R   6    850   CORE: the document + index records (the ReleaseTool cherry-pick)
│   ├── ReleaseNotes.UI.cs                     R   6  1,950   the What's-new page, the highlight viewer, the after-update dialog
│   ├── ReleaseNotes.Host.cs                   R   6    520   SHELL: ReleaseNotesStore
│   ├── Feedback.cs                            R   6    650   CORE: ReportKinds, ReportBundle, IssueFormUrl, ReportRedactor, ReportKindIndex, ReportIdentity
│   └── Feedback.UI.cs                         R   6    800   the report dialog (Bug / Crash), composer, chrome, the crash-reports card
│
├── Platform/                      16 files  19,165
│   ├── Platform.cs                            S   6  1,100   CORE: settings key table + epochs, boot (orchestrator-owned from Wave 0), credentials, NetworkPolicy, ZoomAutoPolicy, locale, ambient power, --fake args + Clock.SeedEpoch, the WaveeLog seam, the runtime Phase enum
│   ├── Platform.Host.cs                       S   6    700   SHELL: Win32 seams, the detached-window owner, zoom/display bridges
│   ├── Prefs.cs                               L   4   ≈250   AppearancePrefs, LyricsPrefs, DetailHeroPrefs, NpvPlayerPrefs (UNVERIFIED)
│   ├── Design.cs                              L   4  3,000   tokens, ramp, colour, materials, motion, wash geometry, reveal ramp, MorphKeys, cover leaves, WaveeAccentCtx, focus insets, ambient cadence
│   ├── Controls.cs                            L   4  2,000   (A16) Surfaces, SearchHighlight, Equalizer, Save/PreSave/Follow, SelectionBar, RowSwipe, badges, the three dialog helpers, the vacancy grammar, Notify.Say
│   ├── +Controls.Cta.cs                       L   4  1,000   (A16) WaveeCta and the CTA ladders
│   ├── +Controls.Art.cs                       L   4  1,400   (A16) MediaCard, PagedShelf, chips, stat tiles, countdowns, face piles, rich text, the shimmer
│   ├── +Controls.Picker.cs                    L   4    850   (A16) WaveePicker, WaveeEqualizerCurve
│   ├── Drag.cs                                L   4    700   (A6) payload, chip resolver, every drop rule, insertion preview — FIRST week of Wave 4
│   ├── Actions.cs                             I   4    950   CORE (A7) descriptor, targeting matrix, the seven reasons, the one action table, the extension registry
│   ├── Actions.UI.cs                          I   4  1,200   (A7) menu vocabulary, ActionIcons, the action row, the picker, the reason caption — before J's picker
│   ├── Notify.cs                              I   4  1,000   CORE (A9) NotifyLevel/Topic/QuietHours, policy, prefs, escalation, merge/filter/read-state, AppUpdateToasts (A8)
│   ├── Notify.Host.cs                         I   4    715   SHELL (A9) WinRT toasts, Action Center, both schedulers, AUMID + activator
│   ├── Modules.cs                             T   6    400   CORE: WatchPageModel, PlayableLinks
│   ├── Modules.UI.cs                          T   6  1,400   the module page and the watch page
│   └── Modules.Host.cs                        T   6  2,500   SHELL: the Sdk host, module items → tables, playables → Playback.Open
│
└── assets/                                              fonts, loc/*.json, deck media, the 16 fixture covers + liked-songs-300.png + spotify/*.json, lottie/*, whatsnew/* — moved as-is

TOTAL                             136 files 170,015   App 400 · Entities 59,050 · Spotify 9,420 · Playback 6,780 · Shell 56,200 · Screens 19,000 · Platform 19,165
```

Against the contract: ≈141,000-143,000 honest de-duplicated lines for the surfaces the chapters map (the old §2 budgeted
64,900 — 2.2× under); the table reaches 170,015 (171,135 less 1,400 for the deleted API console, Q7, plus 200 for
`SetupSession` folding into `Setup.cs`, Q8, plus 80 for the merch table/edge in `Edges.cs`, Q4) because it also carries
`Spotify/`, the non-UI half of `Playback/`, the entity infrastructure and the module host. Partials are named on day
one; a Wave 4/5/6 owner adds any further partial on the first day of their wave, never mid-wave.

---

## 11. The order of work

Every wave: subagents on DISJOINT files; only the orchestrator builds, tests and launches; a wave closes when its gate is
green in Debug AND Release; every Wave 4/5/6 owner reads their chapters end to end before writing a line (G4).

- **Wave 0 — orchestrator, 2 days.** FIRST the golden captures (G1) of every route from the kept 0.2.9 Release build
  with `Drive-WaveeWindow.ps1` (`PrintWindow`; the harness `--screenshot` is black under Mica), at the width tiers the
  chapters name, under `docs/plans/wavee/wavee-0.3-ui/golden/` — this cannot be taken after Wave 0. Then
  `C:\WAVEE\wavee-0.3` on `feat/0.3-structure`, `git mv` the old tree to `src/apps/_old/`, create all 136 files empty
  with their header comment, fix `Wavee.ReleaseTool`'s include list for both `ReleaseNotes` partials, the engine PR
  `StringTable.Intern(ReadOnlySpan<byte>)`, the orchestrator-owned boot half of `Platform.cs`. Gate: captures stored and
  spot-checked against three chapters; Debug + Release build clean; `Wavee.Tests` compiles with zero tests.
- **Wave 1 — A, B, C: Entities core.** `Entities.cs` + `Palette.cs` + the entity CORE columns (A); `Edges.cs` + the
  synthetic-parent CORE columns + the friends edge (B); `Store.cs` + `Fetch.cs` (C). Gate: tests green; 10k tracks
  resident in < 1.5 MB managed and zero allocations in a 1k-row `Knows` sweep.
- **Wave 2 — D, E, F: Spotify.** Session (D); `Decode.cs` + the pathfinder partial, fixtures, the offline export path
  (E); Api / Audio / Telemetry / Connect (F). Gate: tests green; `--login-smoke` logs in and decodes one `getAlbum`
  into a Staging with the expected row counts (orchestrator only).
- **Wave 3 — G, H: Playback.** The reducer + host (G); Audio / Video host / OS surfaces (H, ch 14 is `Playback.Os.cs`'s
  contract; the jump list ships behind the late-binding seam). Gate: tests green; the login smoke plays 10 s through
  the real pump; the SMTC card and taskbar overlay match ch 14 W1-W9 against the golden captures.
- **Wave 4 — I, J, K, L: shell, sidebar, rail/stage/deck/lyrics/video, platform.** Sequenced: L ships `Drag.cs` in week
  one → I ships `Actions.*` before J's picker → J sequences the customizer last → K's stage and video after L's
  `Design.cs`/`Controls.*` → notifications and the runtime card body land here (A9, A18) → Q writes a first cut of the
  seed. Gate: tests green; `--fake` shows the frame with an empty content host, a working sidebar in all three designs,
  the player bar, the rail, the stage, one deck face, the lyrics rail arm and the notification panel with seeded rows;
  parity recorded for chapters 14 (W1-W13), 18-26 and the Wave-4 half of 00, 02, 29.
- **Wave 4.5 — M: the shared detail frame and the track surface.** `Detail.cs`, `Detail.UI.cs`, `Track.cs`, `Track.UI.cs`,
  `Track.Table.cs`, after reading 03, 04, 01 and 30 end to end. Gate: the fake album renders through the real frame in
  BOTH layout arms (ch 03 §10 item 67) and chapters 03, 04, 01 pass item by item. **Wave 5 does not open until this
  is green.**
- **Wave 5 — M, N, O, P, Q: entity UI and pages.** Album / Show / Episode + the drawer (M); Artist / Discography /
  Concerts (N); Playlist incl. `local` / User pages incl. Liked (O); Home ×7 / Search / Browse / Recents (P); Queue +
  `Entities.Fake.cs` (Q). Gate (G2 stage 1): every route whose page ships in Waves 1-5 RENDERS ITS LOADED STATE from
  the seed, plus the surface half; `ReuseGuard` silent; zero allocation on a scroll frame; parity for chapters 01, 03,
  04, 05-13, 15-17, 21 (queue), 31.
- **Wave 6 — R, S, T + orchestrator: screens, platform, modules, live, delete, ship.** Settings / Setup / ReleaseNotes /
  Feedback (R); Diagnostics ×5 + `Platform.cs` + `Platform.Host.cs` (S); Modules ×3 (T). The whole G2 list re-runs incl.
  the six Wave-6 routes; live login end to end; Connect transfer both ways; `ops/perf-tour` vs the 0.2.9 baseline;
  `local-update-e2e.ps1` both scenarios; `git rm -r src/apps/_old`; CHANGELOG with `(#n)`; `wavee-release.ps1 -DryRun`.
  Gate: all 32 checklists, 2,650 items, green and logged in each chapter's §11.

---

## 12. Decided, 2026-09-12 — Christos's answers to the nine questions

Plan §9.6, restated as a decisions record. All nine were open at the previous revision — Q1 blocked K's lyrics files
(Wave 4); Q2/Q3 blocked I's `Notify.*` (Wave 4); Q4 blocked M's `Album.Page.cs` trailing band (Wave 5); Q5 blocked B
(Wave 1) and K's friends panel (Wave 4); Q6 blocked I's route table (Wave 4); Q7 blocked S's `Screens/` file count
(Wave 6); Q8 blocked R's `Setup.cs` budget (Wave 6); Q9 blocked I in Wave 4 and R in Wave 6 — and Christos answered
all nine on 2026-09-12; every wave above is now unblocked. §9.6's own title made the same change, for the same
reason G3 never lets a parity item sit unchecked: a decision recorded and not applied everywhere it touches is the
exact failure this pass exists to prevent.

- [x] **Q1 — Lyrics env-var probes.** Delete `WAVEE_LYRICS_DEBUG` outright, or keep the pill it gates and turn
      `WAVEE_LYRICS_ADVANCE_PROBE` / `WAVEE_LYRICS_OPEN` / `WAVEE_LIVE_LYRICS_SCROLL_PROBE` into
      `Screens/Diagnostics.Probe.cs` CLI entries? **ANSWER: KEPT, contrary to ch 22's proposal.** The debug pill
      survives, re-homed on the persisted `diag.developerMode` flag; only the `WAVEE_LYRICS_DEBUG` **environment
      variable** is deleted (CLAUDE.md bans env-var switches, not the surface). The three verification env vars still
      become `Diagnostics.Probe.cs` CLI entries exactly as proposed. **CONSEQUENCE:** ch 22 keeps its W21 wireframe
      and its budget for the pill inside `Lyrics.cs`/`Lyrics.UI.cs` (no line-count change); W23 now hosts only the
      `FG_STAGE_RECTS` retirement, not the pill; the Lyrics section above (§5) is corrected to match.
- [x] **Q2 — The concert / social toast's launch argument.** `ToastEscalator` builds `"wavee://open?route=" +
      Escape(s.ActionUri)` with a `spotify:concert:<id>` / `https://concerts.spotify.com/…` that is not a route key —
      port the line verbatim, or through `RichText.RouteForUri` like the in-app row? **ANSWER: FIXED, not ported
      verbatim.** Route it through `RichText.RouteForUri` the way the in-app panel row already does, so the click
      resolves — a deliberate divergence from 0.2.9. **CONSEQUENCE:** needs a GitHub issue and a `(#n)` CHANGELOG
      bullet — **neither is filed yet**; ch 14's parity item 29 and DATA GAP 1 are restated as "differs from 0.2.9,
      deliberately — (#n)".
- [x] **Q3 — `ReleaseNotifier` never calls `Silent()`.** The release-drop toast always makes a sound regardless of the
      preference (`DaylistNotifier` honours it) — match 0.2.9, or fix? **ANSWER: FIXED.** `ReleaseNotifier` gains the
      `policy.Sound` check `DaylistNotifier` and `ToastEscalator` already have. **CONSEQUENCE:** same treatment as
      Q2 — issue, `(#n)` CHANGELOG bullet (neither filed yet), ch 14's parity item 31 restated as a deliberate
      divergence.
- [x] **Q4 — The album merch table** (ch 05 D8). Add a small `MerchTable` (`Name`, `Price`, `ImageId`, `ShopUrl`) +
      `EdgeTable<NoEdge> AlbumMerch`, or drop the merch row from the trailing band? **ANSWER: ADDED.** Both land in
      Wave 1 with the other tables and edges (owner B, inside `Edges.cs`); ch 05 D8 is the specification.
      **CONSEQUENCE:** `Edges.cs`'s §2/§10 row grows 900 → 980 (+80, UNVERIFIED — ch 05 D8 gives a shape, not a
      count); `Entities/`'s subtotal moves 58,970 → **59,050**. The album trailing band keeps its merch row and
      parity items unchanged.
- [x] **Q5 — The friends file.** Ch 21 proposes `EdgeTable<FriendEdge> Friends` + a `Signal<FriendFeedState>` on
      `Spotify.Connect` but names no file — confirm the plan's split (edge/columns in Wave 1 owner B, panel in Wave 4
      owner K), or name an `Entities/Friends.cs`? **ANSWER: SPLIT CONFIRMED**, exactly as the plan already has it.
      **CONSEQUENCE:** no `Entities/Friends.cs`; the edge was always folded into `Edges.cs`'s existing budget, so no
      arithmetic moves.
- [x] **Q6 — `connect-diagnostics` needs a GitHub issue before the fix.** It is renderable by `PageFor` but absent
      from `ShellRoutes.s_exact`; CLAUDE.md's "every fix references its issue" means the issue comes first, and none
      exists. **ANSWER: the orchestrator drafts it for Christos's approval; nothing has been filed.**
      **CONSEQUENCE:** recorded everywhere the fix is cited as "issue drafted, awaiting approval — no number yet",
      `(#n)` left as a placeholder. This is still an action for Christos to approve.
- [x] **Q7 — The API-console debug helpers: keep or delete?** `ApiDebugBodyBuilder` / `ApiDebugExecutor` /
      `ApiDebugProto` / `ApiDebugProtoDecomposer`, ~1,400 developer-only lines carried as `+Diagnostics.Api.cs`.
      **ANSWER: DELETED.** The four helpers and the console they serve (`ApiConsolePage`, 329 lines — it has no
      function without them) are both dropped. **CONSEQUENCE:** `+Diagnostics.Api.cs` removed from the tree (§10:
      `Screens/` 23 files/20,200 → **22 files/19,000**; the whole tree 137 → **136** files, 171,135 → **170,015**
      lines); the API console's surface entry above (§9) is struck, not removed; `api-console` dropped from the G2
      probe list and from every route/probe list in this document; ch 27's API-console parity items (72, 72a), the
      W25 wireframe and its route-table row are all STRUCK in the chapter with this reason and this date, never
      deleted.
- [x] **Q8 — `SetupSession` / `SetupSession.MarkerEpoch` has no named home** (324 lines,
      `Features/Setup/SetupSession.cs`, not inside ch 28's "rest of `Features/Setup`"). `Screens/Setup.cs` is the
      obvious home but no chapter says so. **ANSWER: the orchestrator's call**, under the plan's own rule that the
      plan decides where code lives: `Screens/Setup.cs`. **CONSEQUENCE:** `Setup.cs`'s row grows 950 → **1,150** and
      gains `SetupSession`/`SetupSession.MarkerEpoch`; `Screens/`'s subtotal absorbs the +200 (folded into the same
      20,200 → 19,000 move as Q7).
- [x] **Q9 — Confirm A18**: the 10-phase runtime provisioning card's body lives in `+Screens/Setup.UI.Runtime.cs`
      (1,100), written by I in Wave 4 because the Wave-4 shell gate needs it, mounted by R's wizard in Wave 6 — the
      alternative is two implementations of the same card. **ANSWER: CONFIRMED as written.** **CONSEQUENCE:** none —
      the runtime-provisioning-card row (§9.5 of the plan) now reads CONFIRMED rather than open.
