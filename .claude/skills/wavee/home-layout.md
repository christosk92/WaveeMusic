---
name: wavee-home-layout
description: Use when changing Home layout preferences — visibility, module order, home-layout.json, HomeLandingProjection hide/reorder, HomeCustomizerPage, or HomePreferences.
---

# Home layout

Home's customizer is the **sidebar layout pattern** applied to a closed set of
landing modules. Read this before adding a module, a command, or a persistence
field.

Lineage (copy these, do not reinvent):

| Sidebar | Home |
|---|---|
| `Wavee.Core/Sidebar/SidebarLayoutModel.cs` | `Wavee.Core/Home/HomeLayoutModel.cs` |
| `SidebarLayoutCommands` + `SidebarLayoutReducer` | `HomeLayoutCommands` + `HomeLayoutReducer` |
| `Features/Sidebar/Persistence/SidebarLayoutStore.cs` | `Features/Home/Persistence/HomeLayoutStore.cs` |
| `SidebarPreferences` | `HomePreferences` |
| `sidebar-customize` + `SidebarCustomizerPage` | `home-customize` + `HomeCustomizerPage` |

Same rules: engine-free Core model, no polymorphic JSON, unknown kinds/fields
round-trip via a wire carry, fail-soft corrupt files (preserve-don't-destroy,
writes blocked until `DiscardCorrupt`), atomic tmp → `File.Replace` + one `.bak`.

Product contract: [docs/plans/wavee/calm-contract.md](../../../docs/plans/wavee/calm-contract.md).

## Document

`%LOCALAPPDATA%\Wavee\WaveeMusic\home-layout.json` — beside `sidebar-layout.json`
(`HomeLayoutStore.DefaultPath` / `SidebarLayoutStore.DefaultPath`).

v1 schema (`HomeLayoutDocDto`, `HomeLayoutJsonCtx`):

```json
{
  "version": 1,
  "updatedAtMs": 0,
  "appVersion": "…",
  "modules": [
    { "kind": "hero", "hidden": true }
  ],
  "deckOrder": ["spotify:section:…"]
}
```

- `modules` — fixed landing kinds in user order. `hidden: true` is omitted when
  false (`WhenWritingNull`). Kind strings live in `HomeLayoutModules.KindName` /
  `TryParseKind` (append only, never rename).
- `deckOrder` — ordered dynamic section-deck ids. **v1 UI does not edit this.**
  The field is on the schema so a later customizer can reorder the deck without
  a migration.
- Unknown members (`[JsonExtensionData]`) and unknown `kind` strings survive in
  `HomeLayoutWireCarry`.

v1 customizer covers the kinds `HomeLandingProjection.Project` already
materializes (`HomeLayoutModules.DefaultOrder`):

`hero`, `weeklyPair`, `quickGrid`, `recents`, `mixBand`, `chipCards`,
`radioDial`, `queueList`, `ratedShelf`, `podcastShelf`, `featured`,
`discoverFeed`.

Not in v1: `shelf` / `topic` / `sectionEntry` (source-section presentations),
and chrome rows (chips, artists, timeline, sections deck, tail).

## Reducer / preferences

All mutation goes through `HomePreferences.Dispatch(command)` →
`HomeLayoutReducer.Apply` → version bump → `HomeLayoutStore.Commit`.

Commands (in-memory only):

- `SetHomeModuleHidden(kind, hidden)`
- `MoveHomeModule(from, to)` — `to` is **after removal** (`Reorderable.OnReorder`)
- `ResetHomeLayout`

Caps: `HomeLayoutReducer.MaxModules` (24). Over-cap is a rejection, never a
truncation. A kind this build does not treat as a fixed landing is
`UnknownModule`.

`HomePreferences` is constructed in `Services` and provided at the app root
(`HomePreferences.Slot`). Load is fail-soft: missing file = default in memory;
corrupt / too-new / unreadable = default in memory, file untouched, writes
blocked.

## Projection (hide + reorder BEFORE rows)

`HomeLandingProjection.Project(feed, titles, layout)`:

1. Materialize modules from the feed (unchanged).
2. `ApplyLayout` clears hidden kinds so `Get(kind)` is null.
3. Builds `HomeLanding.Rows` from `layout.VisibleFixedModules()` plus chrome
   anchors (Chips first, Artists after MixBand, Timeline+Sections after
   Podcasts, Tail last). Adjacent `queueList`+`ratedShelf` collapse to
   `HomeRow.EpisodesAndBooks`; pulled-apart pairs use `HomeRow.Queue` /
   `HomeRow.Books`.

`HomePage` / `HomeFeedVirtualLayout` consume `landing.Rows` /
`homeLayout.Rows`. Do **not** keep a parallel hardcoded `HomeFeedVirtualLayout.Rows`
table — that is what used to fight the layout.

A hidden Hero must not steal the greeting: `GreetingBlock` reads
`landing.Get(Hero)`, not the raw feed.

## Customizer + nav

- Page: `HomeCustomizerPage` (`Create()` factory). v1 = visibility
  `ToggleSwitch` + `Reorderable` drag order. Tokens only.
- Route key: **`home-customize`**. Navigate with the existing Home nav
  callback (`HistoryStore.NavCtx`). Do **not** add the `WaveeShell` /
  `ContentHost` case from this skill — that is a shell-owner edit.
- Entry: Home greeting/chips overflow (`HomeCustomizeAffordance`) — not a FAB.

## How to add a module

1. Add the `HomeGroupKind` (append only) and teach the composer to emit it.
2. Append the kind to `HomeLayoutModules.DefaultOrder` and `KindName` /
   `TryParseKind`.
3. Map it in `HomeLandingProjection.RowOf` + `HomePage.FeedGroup` / `RenderRow`
   / `HomeFeedVirtualLayout.Estimate`.
4. Add a label in `HomeCustomizeLabels` (reuse `HomeModuleCopy` when the copy
   already exists).
5. Old documents pick the new kind up automatically: `HomeLayoutWire.Read`
   appends missing default kinds as visible. No migration, no `FG_*` flag.

Do not add a module by hardcoding another `HomeRow` into a static table.
