---
name: wavee-sidebar
description: Use when changing anything in Wavee's left sidebar: the Classic and Library layouts (catalogue, overlays, ops), the one PaneView renderer and SidebarRowPlanner, row geometry, the pane modes and window bands, Edit mode and the undo ring, menus, per-account pins and Spotify pin sync, sidebar.json / sidebar.acct-*.json persistence, or the Library head. Read before adding a section kind or item, a menu entry, a persisted field, or touching pane metrics.
---

# Wavee sidebar

Scope: `src/apps/Wavee/Shell/Sidebar*.cs` (the `Sidebar` partial, `PaneView`, the planner, the stores, the migration),
the `Sidebar*Tests.cs` files in `src/apps/Wavee.Tests/`, and `Spotify/Spotify.Encode.cs` (`LibraryPinSync`, the pin
bridge). The user-facing guide is [docs/guide/sidebar.md](../../../docs/guide/sidebar.md). General app architecture:
the [wavee](../wavee/SKILL.md) skill. Engine work: the engine repo's [fluentgpu](../../../../fluent-gpu/.claude/skills/fluentgpu/SKILL.md) skill. The sidebar
is app code on top of the engine; it never edits `src/FluentGpu.*`.

## The 30-second map

```
  SidebarOp ──► SidebarLayoutRules.Apply ──► SidebarLayoutState { Classic: LayoutOverlay, Library: LayoutOverlay }
                (pure; a refused op changes nothing)           │
                                                               ▼  Resolve, against SidebarCatalogue
                                                     SidebarLayoutDoc  (what the planner reads)
                                                               │
  SidebarProjectionBinder ──► SidebarProjectionInput ─────────►│ SidebarRowPlanner.Build   (pure, POD)
  (the one rebuild driver: playlists, folders, pins,           ▼
   feeds, library counts, recents)                     SidebarRowPlan  (rows + geometry)
                                                               │
                                                               ▼
                       PaneView  (the ONE renderer: ItemsView over PaneSlot rows)
                       hosted by ClassicMode or LibraryMode; entry points Sidebar.Pane() and Sidebar.DrawerPane()
```

## The rules that matter most

1. **One renderer, no layout branches inside it.** `PaneView` renders both layouts and all three pane modes.
   Layout differences live in the document (`SidebarLayoutDoc`) and in the Library head, never in an
   `if (layout == …)` inside the row or slot code.
2. **Every edit is a `SidebarOp`.** Menus, Edit mode, drag and drop, keyboard verbs and the migration all go
   through `SidebarLayoutRules.Apply`. One action pushes one inverse entry to `SidebarUndoRing` (50 entries, memory
   only, layout and pin edits; collapse toggles are never recorded).
3. **Decision logic is engine-free and unit-tested.** Planner, rules, geometry, resize, the menu model, the file store
   and the migration are plain C# with their own test files. Engine-bound code stays in `Shell/` and is verified by
   the build and by hand.
4. **Liked and Home are never pins.** They are fixed routes. `SidebarPinId` folds every Liked spelling to `liked`,
   which the fixed-route guard refuses. Only entity pins are written to the server pin set (ylpin).
5. **Pins belong to one account.** `SidebarPinStore` is per account. `LibraryPinSync` applies a server set only when
   the loaded account key equals the live one. Never write or apply pins across accounts.

## Deeper docs

- [architecture.md](architecture.md): the component tree, the pure classes, the state signals on `Sidebar`, the
  stores and the account swap, disclosures, and **Rootlist drag & drop**.
- [pitfalls.md](pitfalls.md): symptom → cause → rule for the traps that have cost time.
- [testing.md](testing.md): which test file covers what, the planner fixture, the temp-folder file store tests, and
  the "no source-text tests" rule.
- [where-to-change-what.md](where-to-change-what.md): task → file table, plus one line per `Shell/Sidebar*.cs` file.

## Verify

You do **not** run builds or tests. Christos builds and runs them. Hand back a checklist (see
[testing.md](testing.md)) instead of claiming a build passed.
