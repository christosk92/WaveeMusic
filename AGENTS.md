# Wavee Agent Docs

Component-specific agent docs live in `.agents/guides/`. Keep this file as a
small **index** so agents can find the right focused guide without loading every
component note by default. The detailed inventories belong in `.agents/guides/`,
not here.

## Index

Read the relevant guide before changing that area:

- **Track and episode UI** — every track/episode row, list, card, search cell,
  omnibar suggestion, queue row, home episode card, and now-playing surface.
  `.agents/guides/track-and-episode-ui.md`
- **Spotify Connect (dealer / device state / playback state / commands)** —
  dealer WebSocket, this-device announce (PutState), cluster state, remote
  commands, queue from cluster, device picker / volume / now-playing UI.
  `.agents/guides/connect-state.md`
- **User library and sync** — collection sync (tracks / albums / artists /
  shows / pins / listen-later), playlist cache, dealer-driven incremental
  updates, save / pin / follow write paths, library and pinned UI surfaces.
  `.agents/guides/library-and-sync.md`
- **Playback runtime** — orchestrator, queue + context, track resolution,
  AudioHost IPC, decode / decrypt / DSP / EQ, prefetch, local-file playback,
  video playback, UI playback service. `.agents/guides/playback.md`
- **Queue** — `PlaybackQueue` three buckets (user queue, context,
  post-context), cursor + shuffle + repeat, `NeedsMoreTracks` pagination
  signal, autoplay rollover, orchestrator queue ops, cluster sync
  (PutState prev/next + `QueueRevision`, `SetQueue` / `AddToQueue`
  commands), right-panel `QueueControl`, drag-drop, context-menu Play
  next / Add to queue. `.agents/guides/queue.md`
- **CompositionImage** — GPU-resident image primitive, cache (ImageCacheService
  + CachedImage), LoadedImageSurface lifecycle, suspension gate, retry
  behaviors, every consumer of `<imaging:CompositionImage>`.
  `.agents/guides/composition-image.md`
- **ContentCard** — reusable shelf / grid card across Home, Search, Browse,
  Library, Artist, Album, Show, Concert, Profile, Local-media. Card chrome,
  modes (square / circle / Tall / Wide / Backdrop), viewport gating, attached
  behaviors. `.agents/guides/content-card.md`

## How To Add A New Component Guide

1. Create `.agents/guides/<component-name>.md`.
2. Open the file with this frontmatter so every guide is uniformly machine-readable:

   ```
   ---
   guide: <component-name>
   scope: <one sentence — what this guide covers>
   last_verified: <YYYY-MM-DD>
   verified_by: <how, e.g. "read+grep over src/...">
   root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
   ---
   ```

3. Keep the guide scoped to one subsystem. Include, in order:
   - Scope (included / excluded surfaces).
   - A **Quick-find table** keyed by user-visible surface, with `file:line` host
     references, DTOs, and source bindings.
   - Shared controls / contracts (the things you'd edit to change every surface
     at once).
   - Per-surface notes for the non-obvious sites.
   - Change guidance — "if you want X, edit Y".
   - A short "keeping this guide current" section.

4. Add the guide to the **Index** in this file.
5. Add the same line to the *Agent component docs* section in `CLAUDE.md` so
   Claude Code picks it up too.
6. Re-verify and update `last_verified` whenever you touch the inventoried area.

## Protocol-isolation invariant

ViewModels and Controls in `Wavee.UI.WinUI` must NOT reference any of these
types directly. Always go through a `Wavee.UI/Services/*` (or
`Wavee.UI.WinUI/Data/Contexts/*` for the not-yet-moved services) interface:

- `Wavee.Core.Session.ISession` and its raw client surfaces
  (`SpClient`, `Pathfinder`, `Dealer`, `Mercury`, `AudioKeyManager`)
- `Wavee.Core.Http.SpClient`, `Wavee.Core.Http.IPathfinderClient`
- `Wavee.Protocol.*` (protobuf types)
- `Wavee.Connect.*` (dealer / connect-state wire types)
- `Wavee.Core.Authentication.*`

Allow-listed escapes (DI wiring + diagnostics + a handful of Control surfaces
that legitimately touch the wire shape):
- `Helpers/Application/AppLifecycleHelper` — DI composition root, naturally
  references `ISession` to construct the wrapping services.
- `ViewModels/DebugViewModel.cs` — raw HTTP / Pathfinder / dealer test bench.
- `ViewModels/ConnectStateViewModel.cs` — renders the raw `RemoteStateRecorder`
  dealer feed.
- `ViewModels/SettingsViewModel.cs` — exposes the session-clock NTP debug
  readout (touches `session.Clock`).
- `ViewModels/ArtistViewModel.cs` — single fully-qualified catch of
  `Wavee.Core.Session.SessionException` to drive the "Connecting…" hero state.
- `Controls/Playback/AudioOutputPicker.xaml.cs` — maps `Wavee.Connect.DeviceType`
  enum values to icon glyphs (the enum is the wire shape but used here as a
  domain enum).
- `Controls/Local/LinkSpotifyTrackFlyout.xaml.cs` — writes a `Wavee.Protocol.Metadata`
  blob onto a local file's overlay.
- `Controls/ContextMenu/Builders/LocalItemContextMenuBuilder.cs` — reads
  metadata-blob fields for the right-click menu on locally-linked Spotify tracks.

Anything else in `ViewModels/*` or `Controls/*` is a violation — add a service
method instead of leaking the raw client into the VM. The invariant is enforced
mechanically by `test/Wavee.UI.Tests/Architecture/ProtocolIsolationTests.cs`;
new violations fail the test suite, and the allow-list is the single source of
truth (kept in sync with the test).

## How This Pairs With CLAUDE.md

- `AGENTS.md` (this file) is the entry point Codex reads automatically.
- `CLAUDE.md` is the entry point Claude Code reads, and it mirrors the same
  index pointing at the same `.agents/guides/` files.
- General repository guidance (build commands, architecture, conventions) lives
  in `CLAUDE.md` — don't duplicate it here. This file stays a small index.
