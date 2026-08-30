---
name: wavee-sidebar
description: Use when changing anything in Wavee's left sidebar — the three designs (Classic / Library V3 / Wavee Curated), the one SidebarPane renderer, the layout document + reducer + templates, the projection/binder/planner data pipeline, sidebar-layout.json persistence, the full-page customizer, pins, or the extension-platform registries (WaveeExtensionRegistry / ISidebarDataSource / WaveeActionDescriptor). Read before adding a section kind, a data source, a bindable action, a display option, or touching pane metrics.
---

# Wavee sidebar — the unified extension platform

Scope: `src/apps/Wavee.Core/Sidebar/**`, `src/apps/Wavee/Features/Sidebar/**`,
`src/apps/Wavee/Actions/Extensibility/**` + `Actions/{PinActions,WaveeActionDescriptor,ActionIcons}.cs`,
and the sidebar test files in `src/apps/Wavee.Tests/`. General app architecture: the [wavee](../wavee/SKILL.md)
skill. Engine work: the repo-root [fluentgpu](../fluentgpu/SKILL.md) skill.

> **The sidebar shipped as THREE separate pane containers and was unified into ONE.** Classic used to be a
> hand-built body (`WaveeSidebar.ExpandedBody`), V3 had its own `LibraryV3Index/List/Row/Rail` stack, Curated had
> its own planner+slots. They shared only leaf primitives, so paddings, count badges, section rhythm and motion
> tripled and drifted (four different left insets, an accent count pill in one mode and quiet numbers in another).
> There is now **one renderer** — `Features/Sidebar/Pane/SidebarPane.cs` — and every mode is a **document + a
> `SidebarPaneConfig`**. If you find yourself adding a mode-specific branch inside the renderer, you are
> re-creating the bug this architecture exists to prevent.

## The 30-second architecture map

```
                    Wavee.Core.Sidebar (engine-free, framework-neutral)
  SidebarCommand ──► SidebarLayoutReducer ──► SidebarCustomLayout {TemplateId, SidebarSectionSpec[]}
  (18 records/20      (pure; pre-image undo)   sections carry Display / Items / Query / Extension
   undo labels)
                                    │
              SidebarPreferences.Dispatch  ── undo push ─► LayoutVersion++ ─► autosave
                                    │
  ── app side ──────────────────────┼────────────────────────────────────────────────────────────
  LibraryStore / HistoryStore /     │
  PlayLogStore / PlaybackBridge     │        registries
  Spotify feed services             │   WaveeExtensionRegistry
        │                           │    ├─ WaveeActionDescriptor  (bindable actions, "wavee.play")
   ISidebarDataSource adapters      │    └─ ISidebarDataSource      (row producers, "wavee.library")
        │                           │
  SidebarProjectionBinder ──► SidebarProjectionInput ──► SidebarRowPlanner.Build ──► SidebarRow[]
   (the ONE rebuild driver)     (aliases binder buffers)   (pure, POD, 13 row kinds)
                                                                   │
                       ┌───────────────────────────────────────────┴──────────────┐
                       │            SidebarPane  (the ONE renderer)               │
                       │  ItemsView.CreateBound → SidebarPaneSlot (per-row Comp)  │
                       │  + SidebarPaneRail (56-DIP) + SidebarPaneConfig seam      │
                       └──────────────────────────────────────────────────────────┘
      Classic                       Library V3                    Curated
   locked built-in doc         synthesized ephemeral doc       the user's doc
   SidebarBuiltInDocuments      LibraryV3Document.Build        prefs.Layout (persisted)
   + read-only config           + LibraryV3Chrome as Head      + the customizer page
```

**Three modes, three kinds of document, one renderer.**

| Mode | Document | Editable? | Mode component |
|---|---|---|---|
| Classic (`SidebarDesign.Classic`) | `SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen)` — rebuilt from code every read, never persisted | no (`ReadOnly = true`) | `Features/Sidebar/WaveeSidebar.cs` (133 lines) |
| Library V3 (`SidebarDesign.LibraryV3`) | `LibraryV3Document.Build(in LibraryV3DocState)` — ephemeral, synthesized from filter/qualifier/sort/view/search/drill | no (`ReadOnly = true`; its chrome owns the state) | `Modes/LibraryV3Sidebar.cs` + `Modes/LibraryV3/*` |
| Wavee Curated (`SidebarDesign.Curated`) | `SidebarPreferences.Layout` — the persisted user document in `sidebar-layout.json` | yes | `Modes/CuratedSidebar.cs` (98 lines) + `Curated/*` customizer |

`Features/Sidebar/SidebarHost.cs` is the **one mount seam**: it reads `prefs.Design.Value` and mounts the mode
component under `SidebarDesignInfo.MountKey(design)` so a design switch is a genuine remount (fresh hooks, fresh
section/scroll state), cross-faded on `MotionTok.ControlFast`. It is mounted twice — the docked pane
(`WaveeShell.cs:498`) and the narrow overlay drawer (`WaveeShell.cs:1085`, `inDrawer: true`).

## Iron rules

1. **`SidebarPaneConfig` is the ONLY mode seam.** `SidebarPane`/`SidebarPaneSlot`/`SidebarPaneRail` must never
   branch on `Config.Design` (it exists for the log field and scroll identity only). Anything a mode needs that
   the renderer lacks becomes a config member or a `SidebarProjectionInput` option — never a fork.
2. **Every config member is a delegate or a flag, never a snapshot.** The config is built once in
   `UseMemo(…, DepKey.Empty)` and frozen into the pane's ctor (props freeze at mount), so a value member would pin
   frame 1's state forever. `Document`/`Input`/`ModeEpoch` are invoked inside the *pane's* render — that is also
   what makes the signals they read subscribe the pane.
3. **The document is not rendered section-by-section.** `SidebarRowPlanner` flattens (document × projection) into
   ONE `SidebarRow[]`, rendered by ONE `ItemsView.CreateBound` over a measured variable-extent layout. No nested
   scrollers, no `Flow.For` over the projection. That is what lets a 10k-entry list virtualize.
4. **One height per SECTION, not per row.** `SidebarPaneMetrics.RowHeight(section)` derives from the section's
   density + *subtitle intent*. `Reorderable`'s slot pitch and the virtualizing host's extent both assume a
   uniform pitch inside a band; a mixed 40/44 list silently breaks both.
5. **One inset owner.** `SidebarPaneMetrics.PanePad = (8,8,8,12)` is applied once, around the virtualized list.
   No row, band, card or strip may add a second horizontal inset — use `SidebarPaneMetrics.RowInset`.
6. **All Curated mutation goes through `SidebarPreferences.Dispatch(command)`.** Reduce → if `Changed`, push the
   pre-image to undo, clear redo, bump `LayoutVersion`, autosave. The customizer never talks to the renderer.
7. **Never `switch` on an extension id, and never look up `AppActions.All` from new UI.** Bound actions resolve
   through `WaveeExtensionRegistry.TryGetAction`; contributed sections resolve through `TryGetSource` /
   `ISidebarContributionHost`. (`AppActions.All` currently has *zero* code references — do not add the first.)
8. **Persisted enums are append-only, never renumbered**, and unknown section kinds / unknown members / unknown
   extension refs must round-trip untouched (`SidebarWireCarry`). Preserve, don't destroy.
9. **A missing entity or unresolvable binding renders visible-but-disabled with a reason.** Never auto-remove a
   user's row; only an explicit `RemoveItem` deletes.
10. **Rootlist drag & drop goes through ONE resolver and ONE commit.** `RootlistSlotResolver.Resolve` decides
    *(kind, depth, refusal)*; `SidebarPane._dropSlot` publishes it once per hover; the row draws a **line**
    (Before/After/EndOfList, indented to the resolved depth) **or** a **plate** (Into) — never both; every drop
    *consumes* the published slot instead of recomputing one; and every move — drop, rail drop, menu verb,
    Alt+↑/↓, the "Move to folder…" picker — funnels into `WaveeResourceDrop.MoveRootlist`. Never add a second
    placement computation or a second mutation path. Full contract:
    [architecture.md § Rootlist drag & drop](architecture.md#rootlist-drag--drop--one-resolver-one-published-slot-one-commit).
11. **The engine is off-limits from sidebar work.** No `src/FluentGpu.*` edits — if the sidebar needs an engine
    change, hand it off. (Sidebar-only changes therefore owe **no** VerticalSlice gate run.)

## Verify

You do **not** run builds or tests — Christos does. Report a verify checklist instead
(see [testing.md](testing.md) for the exact commands, the filter, the hang caveat and the probe flags).

## Deeper docs

- [architecture.md](architecture.md) — the full pipeline with real type names; `SidebarPaneConfig` member by
  member; **the rootlist drag & drop contract** (resolver, cue vocabulary, slot → mutation, the mid-drag freeze,
  the non-mouse verbs, the deferred engine path); the extension-ready layer (registries, contracts, the wire,
  budgets, forward-compat guardrails).
- [where-to-change-what.md](where-to-change-what.md) — task → files map, with the test file and the change's
  blast radius for each.
- [pitfalls.md](pitfalls.md) — the traps that actually cost this build time (loc generator quirks, struct-default
  polarity, frozen props, uniform-pitch geometry, buffer aliasing, controlled controls) **plus the honest
  KNOWN ISSUES / deletion-candidate list**. Read before debugging "why doesn't X update".
- [testing.md](testing.md) — the test inventory, the canonical filter, the screenshot probes, what does and does
  not owe an engine gate run.
- Developer-facing companion (for humans, and for the JSON wire format + a worked example):
  `docs/guide/sidebar-extension-platform.md`.
- The user's platform/remediation brief: `docs/plans/wavee/wavee-sidebar-extension-platform.md` (it now holds the
  visual-remediation spec and the `## Boundaries` that gate further extension-host work).
