---
name: wavee
description: Wavee Spotify desktop client under src/apps/ (Wavee, Wavee.Core). Use for app architecture, seams, playlist mutations, and build/verify commands. Engine work belongs in the repo-root fluentgpu skill.
---

# Wavee app

Scope: `src/apps/Wavee/**`, `src/apps/Wavee.Core/**` only.

> **⛔ HARD RULES for subagents (agents dispatched by an orchestrator).** (1) **Never `git stash`** — or any
> other git state change (no commit/checkout/reset/restore/stash pop): the working tree is shared and a stash
> wipes every parallel agent's in-progress edits. (2) **Never run builds** — no `dotnet build` or check-canon: parallel builds on the shared `obj/bin` collide and exhaust the
> machine. Write code only; the orchestrator runs the full verification once after the whole batch.
> (Working solo, i.e. not as a dispatched subagent? Then the Build & verify commands below are yours to run.)

## Build & verify

```powershell
dotnet build src/apps/Wavee/Wavee.csproj
```

There are no tests. Verify UI with the headless inspector (`docs/guide/agent-inspect.md`: `render`, `trace`, `crossing`, `lint`).

A `dotnet run`, a VS publish profile (`bin\...\publish-profiles-*`) or a plain `publish-wavee-aot.ps1` is unstamped:
Settings › Privacy & diagnostics › Developer › "Send a test crash report" says *no crash service set up* and the update checker never runs
(channel `dev`). To exercise either for real, use the verify builds in the `releasing` skill
(§ Verify builds: `-CrashService`, `-Channel stable -Quad …`).

## Architecture hub

`docs/plans/wavee/` — and within it **`architecture.md` is the seam canon**: the ports (`ICatalogSource`,
`IEntityHydrator`, `IOnlineCatalog`, the playback/remote/session/lyrics/mutation ports), the `SourceRegistry` /
`AggregateCatalog` federation, the ACL rule (no GraphQL/proto type crosses a port), the §9 status matrix and the
§10 file map. Start there for "where does this belong?".

Two pointers that used to conflict, now reconciled:

- `docs/plans/wavee-native-backend-architecture.md` is a **different** doc — the *live Spotify backend* (transport,
  dealer, session, audio). It does not supersede `architecture.md`; read it for wire/session questions.
- Production comments across `Wavee.Core/Sources/**`, `App/**` and `Features/**` cite `docs/architecture.md`, which
  does not exist. They mean `docs/plans/wavee/architecture.md`; the §-numbers still line up.

**Hydration** (every catalog metadata fetch in the app goes through one façade):
[hydration.md](hydration.md) is the how-to. Canon: `docs/plans/wavee/hydration-facade-design.md` (shapes),
`hydration-facade-plan.md` (phases + status), `metadata-entry-points-inventory.md` (what it replaced),
`xm-kind-probe-overview.md` + `xm-playcount-handoff.md` (which extension kind carries what).

## Wiring discipline (mandatory)

Read [wiring-discipline.md](wiring-discipline.md) before any seam/composition-root change. **Never** use optional nullable dependencies with `?? Task.CompletedTask` or empty-string defaults on hot paths.

## Sub-skills

- [hydration.md](hydration.md) — **the metadata façade**: `IEntityHydrator`, the five levels, the per-kind ladders,
  traits + surfaces, the display-only extension reader, and the rules (no `spotify:track:` string tests, no
  per-service memos/caps/etag forks, store-writing = ladder/projector vs return-only = service). Read before adding
  ANY fetch of catalog metadata, a new trait, a new extension read, or a second provider.
- [wiring-discipline.md](wiring-discipline.md) — required deps, fail-loud stubs, go-live hooks
- [home-layout.md](home-layout.md) — Home visibility + order (`home-layout.json`, reducer,
  `HomeLandingProjection`, `HomeCustomizerPage`). Read before adding a landing module.
- [session-restore.md](session-restore.md) — `session.json`: nav stacks **and** the playback session. Write gates,
  the cluster → snapshot → paused restore order, and the uid→uri→index→head identity ladder. Read before touching
  restore, `PlaybackController`'s recovery paths, or anything that decides what plays at launch.
- [audio-handoff.md](audio-handoff.md) — the audio pipeline end to end: continuity (MMCSS threads, the 2 s / 1 s rings,
  shallow-silence starvation recovery), the three seek paths and seek generations, scrub grains, the resampler, the
  limiter-before-volume and normalization modes, gapless & crossfade (the 0 ms butt-join vs the overlap path, codec
  pre-roll trim), and how to read `audio.glitch` / `[gapless]`. Read before touching `Playback/Playback.Audio*.cs`, the
  engine's `Media/Playback/Audio/**`, or prepared-next scheduling.
- [playback-modules.md](playback-modules.md) — **playback modules**: sources that ship as independently updatable
  out-of-process exes (YouTube, Twitch, radio; Spotify next) speaking JSON-RPC over stdio. The three layers (SDK in
  `Wavee.Sdk`, host in `Backend/Modules`, modules in `src/apps/modules`), the `wavee-module.json` manifest and the
  two discovery roots, the rules that are easy to break (dev `.dll` vs published `.exe` entry, stdout is the wire,
  `-32601` = capability absent, declared-never-probed caps), the diagnostics section, and
  the dev/publish/MSIX layouts. Read before touching `Backend/Modules/**`, `src/apps/Wavee.Sdk/**`,
  `src/apps/modules/**`, or the module bits of `Wavee.csproj` / `ops/build`. Full doc:
  `docs/guide/playback-modules.md`.
- [notifications.md](notifications.md) — the two channels (bell / Windows), the Off→In-app→Windows ladder, the 8 topic
  dials, quiet hours, the live-escalation watermark and the scheduled-toast reconcile rules. Read before adding a
  notification of any kind.
- [palette-shortcuts.md](palette-shortcuts.md) — the Ctrl+K command palette registry, the shortcut table, `Announcer`.
- [bridges.md](bridges.md) — the OS-mirror bridges hanging off `PlaybackBridge` (SMTC, taskbar, jump list).
- [deep-linking.md](deep-linking.md) — the `wavee://` verb map and the one activation entry every surface routes through.
- [receipts.md](receipts.md) — the About "Wavee right now" perf receipts and the GPU-vs-app memory split.
- [focus-pitfalls.md](focus-pitfalls.md) — programmatic focus: focus the editable node, not its chrome.
- [ui-pitfalls.md](ui-pitfalls.md) — "updates only after a resize" = a memo key missing an input (the title bar's
  `ChromeContentVersion`); pill rows need `Wrap = true`; check the running exe's path before diagnosing.
- [scrolling.md](scrolling.md) — **Scrolling in Wavee**: one `ScrollHandle` + a `ScrollKey` composed under
  `Shell.PageScrollScope` per scroller, `ItemsView` `ScrollOptions`, `WheelTarget`, sticky/clip with scopes and
  `engaged:` signals, hero `Collapse`/`StretchFromTop`/`Parallax`/`Fade`, lyrics `ScrollMove.Follow` + `MeasureAll`, the
  Diagnostics Scroll + Tiles cards and the `scroll.frames` / `scroll.burst` log lines. **No app-side workarounds for
  engine scroll behaviour** — reproduce, then fix in `..\fluent-gpu` (its `fluentgpu-scroll` skill). Guide:
  `docs/guide/scrolling.md`.
- **The window frame and the content states are the structure's** (themes plan T4a): `Shell.FrameRoot` (`Shell/Shell.UI.cs`)
  is a `[CoreHost]` that wires the window and mounts `Shell.Frame`; the column, the layers and the content region are
  `Themes/Classic/Structure/Views/Frame/ClassicFrame.cs`. `ContentHost` decides the page / empty body / not-found / offline-lane
  state (`Shell.FrameRules.ContentBodyFor` / `ShowsOfflineStrip`) and mounts `Shell.ContentState` (`ClassicContentState.cs`). Edit the picture there,
  the decision and the wiring in `Shell.UI.cs`; a new surface follows the six steps of `docs/guide/theming.md` section 2b.
- **The frame's small views are the structure's too** (themes plan T4c): the masthead band + `Page.Head` (`Shell.MastheadBand` in
  `Shell/Shell.Masthead.UI.cs` is the core host, it publishes a `PageChrome`), the material layer and the four cover leaves
  (`Platform/ShellMaterial.cs` keeps the channel and the hand-over rule; `Entities/Palette.Host.cs` mounts the leaves by view key), the
  narrow drawer (`Shell.NarrowDrawer` host keeps the Escape/key-preview chain), the command palette (`Shell.PaletteSession` is the model the
  card reads; `Shell.CommandTable` is unchanged), the history menu, the file-drop cue, the drag chip layer, the setup cover scrim and the
  frame-rate HUD (`Diagnostics.FpsOverlayOn` is the rule). Pictures: `Themes/Classic/Structure/Views/{Masthead,Material,Drawer,Palette,FrameBits}`;
  numbers: `Rules/ClassicFrameBitsRules.cs`; keys: `Theming/Contracts/ViewKeys.FrameBits.cs`.
- **The overlays and the dialog bodies are the structure's** (themes plan T11a): the account chip and menu, the notification panel, the
  runtime banner, the update toast's download card, the offline strip, the context menu's identity tile, and the bodies of the log-out
  card, the play-a-link card, the signature sheet, the destination picker and the credits sheet. The core keeps the overlay handles, dismiss
  rules, focus traps, the toast queue (the engine's `ToastHost`), the dialog shells (the engine's `ContentDialog`) and every verb: hosts in
  `Shell/Shell.Overlays.UI.cs` (chip, banner, play-a-link, log-out, signature opener), `Shell/Shell.UI.cs` (the notification panel host and
  `NotificationsHost`), `Platform/Actions.UI.cs` (the picker's filter, the menu header), `Entities/Track.Drawer.cs` (the credits host).
  Pictures: `Themes/Classic/Structure/Views/{Overlays,Dialogs}`; numbers: `Rules/Classic{Overlay,Notification,Dialog}Rules.cs`; keys:
  `Theming/Contracts/ViewKeys.Overlays.cs` (props in `OverlayProps.cs`, `NotificationsProps.cs`, `DialogProps.cs`). Confirm, prompt (rename) and
  "reset everything" are engine ContentDialog configurations with no picture of the app's own: they have no key.
- **Full-screen Now Playing + visualizers** — the stage, the eight faces, the demand tiers and fallback ladder, the
  `stage.*` / `viz.*` log events and the Diagnostics card. Guide: `docs/guide/fullscreen-visualizers.md`.
- [probes.md](probes.md) — the windowless CLI probe arms (`--log-sessions` and the others `Probe.TryRunCliArm`
  dispatches), how to build/run one against the live profile, and the credential-store caveat. Read before running
  or adding a probe.
- **`wavee-sidebar` skill** (`.claude/skills/wavee-sidebar/`) — the left sidebar: Classic and Library layouts, one
  renderer, pane modes, Edit mode, per-account pins. Read it before touching `Shell/Sidebar*.cs` or
  `docs/guide/sidebar.md`'s subjects.
- **`dnd` skill** (`.claude/skills/dnd/`) — **drag & drop**, engine and app. Read it before touching
  `Features/DragDrop/**` (`WaveeResourceDrag` payloads/commit seams, the engine-free `WaveeDragRules` /
  `TabDropRules` decision tables, the chip model, the insertion preview) or any surface that declares a
  `Drag.Source` / `Drop.Target` / `InsertionOptions` / `Reorderable` — the detail track list, the tab strip, the
  player bar, the queue panel and the sidebar rows all do.
- [wavee-playlist-mutations/SKILL.md](wavee-playlist-mutations/SKILL.md) — the playlist/rootlist **write path**: the
  desktop-verified `/changes` wire (keyed REM/MOV, minted `item_id`s, the 8-B create base, folder markers, the
  permission proto dialect), the dealer push gate trees, invariants I1–I8, the durable outbox, and the edit
  affordances/failure copy on the playlist page. Read before changing anything in `Backend/Playlists/**`,
  `Backend/Mutation.cs`, the sync/dealer playlist arms, or `IPlaylistMutationSource`.
