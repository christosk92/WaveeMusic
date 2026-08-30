# Wavee sidebar — where to change what

Each task lists the files **in edit order**, the tests that guard it, and the blast radius. Paths are relative to
the repo root. Nothing here runs a build — see [testing.md](testing.md) for the verify checklist you hand back.

---

## Add a section kind

A section kind is a cross-cutting artifact: the model, the wire, the planner, the renderer, the palette and the
loc table all have to agree.

| # | File | Change |
|---|---|---|
| 1 | `src/apps/Wavee.Core/Sidebar/SidebarLayoutModel.cs` | Append to `SidebarSectionKind` (**never renumber**), bump `SidebarSectionKinds.MaxKnown`, then add the arm to *every* table in `SidebarSectionKinds`: `DefaultTitleLocKey`, `PaletteNameLocKey`, `DefaultDisplay`, `EmptyBehaviorFor`, `AcceptsItems`, `SupportsLibraryQuery`, `RequiresExtensionRef`, `ItemCapacity`, `AllowsDisplayField`, `IsNestable`. Missing one is the classic drift bug. |
| 2 | `src/apps/Wavee/Features/Sidebar/Persistence/SidebarLayoutDoc.cs` | `SidebarLayoutWire.KindName` **and** `TryParseKind` (the string is the persisted identity — pick it once, never rename). Add a DTO member only if the kind carries new payload. |
| 3 | `src/apps/Wavee/Features/Sidebar/Data/SidebarRowPlanner.cs` | A `Plan<Kind>` arm in `Build`'s switch, and a `BuildRail` arm if it can contribute a tile. Rows stay POD — no strings allocated during planning. |
| 4 | `src/apps/Wavee/Features/Sidebar/Pane/SidebarPaneSlot.cs` / `SidebarPaneText.cs` | Only if the kind needs a *new row kind*; otherwise reuse an existing `SidebarRowKind` and add the title/subtitle arm in `SidebarPaneText`. |
| 5 | `src/apps/Wavee/Features/Sidebar/Curated/SidebarCustomizerLayout.cs` | A `SidebarPalette.All` entry (group, `SidebarPaletteAdd`, name/description loc keys, glyph name). |
| 6 | `src/apps/Wavee/assets/loc/{en-US,nl,ko-KR}.json` | `sidebar.section.<kind>` + `…Sub` (palette) and the default-title key. All three locales. |
| 7 | Tests | `SidebarLayoutJsonTests` (the exact camelCase kind string + unknown-kind round-trip), `SidebarLayoutReducerTests` (`AddSection` legality), `SidebarRowPlannerTests` (the row *sequence*), `SidebarCustomizerLayoutTests` (palette + display projection). |

Blast radius: every mode inherits it. A kind added without a planner arm plans zero rows and looks like a bug.

---

## Add a first-party data source (a contributed section)

| # | File | Change |
|---|---|---|
| 1 | `src/apps/Wavee/Features/Sidebar/Data/SidebarDataSource.cs` | Add the id const to `SidebarContributions` (`"wavee.<contribution>"`) **and** to `SidebarContributions.FirstParty` (that array is the customizer's Extensions palette group). |
| 2 | `src/apps/Wavee/Features/Sidebar/Data/SidebarSourceMap.cs` | The pure domain-record → `SidebarLibraryEntry` mapper and the service-health → `SidebarSourceState` rule. **Everything that can be wrong goes here**, because this file is source-included by the tests and the adapter is not. |
| 3 | `src/apps/Wavee/Features/Sidebar/Data/Sources/*.cs` | The adapter: derive from `SidebarDataSourceBase`, declare `ConfigSchema` / `ItemType` / `SupportedFilters` / `SupportedSorts` / `Paging` honestly, implement `EnsureFresh` (idempotent, non-blocking, never throws) and `Fill` (append-only, no LINQ/closures/allocation, never blocks). Health: `SetHealth` from a callback, `SetHealthQuiet` from inside `Fill` only. |
| 4 | `src/apps/Wavee/Features/Sidebar/Data/Sources/WaveeBuiltInDataSources.cs` | Construct it in `RegisterAll`, publish it in `Publish`, and give it the binder's `post` in `Attach` if it completes anything asynchronously. |
| 5 | `src/apps/Wavee/App/Services.cs` | Only if it needs a service the `RegisterAll(...)` call does not already pass. |
| 6 | `src/apps/Wavee/assets/loc/*.json` | Its `ConfigSchema` field `LabelLocKey`s + any `StateDetailLocKey`. All three locales. |
| 7 | Tests | `SidebarDataSourceTests` (config readers, id scheme, host resolution, mappers), `SidebarProjectionBinderTests` (slice windows + availability verdicts — it has a `StubSource : SidebarDataSourceBase` to copy). |

A user adds it as an `Extension` section whose `SidebarExtensionRef` names `("wavee", "<contribution>")`. You do
**not** add a `SidebarSectionKind` for it, and you do **not** teach any UI to switch on the id.

---

## Add a bindable action

| # | File | Change |
|---|---|---|
| 1 | `src/apps/Wavee/Actions/Extensibility/BuiltInExtensionTable.cs` | A `KeyX = "wavee.x"` const + the `WaveeActionDescriptor` in `RegisterAll`. Set `AcceptedTargets` to exactly what it supports, declare `RequiredPermissions` honestly (unenforced until M3), set `RequiresConfirmation` for anything destructive. `Run` receives the **already-resolved** target — never re-resolve. |
| 2 | `src/apps/Wavee/Actions/ActionIcons.cs` | Add the semantic `IconKey` const + the `Resolve` arm if no existing one fits. `IconKey` is never a raw glyph. |
| 3 | `src/apps/Wavee/assets/loc/*.json` | `LabelLocKey` + any `Confirm{Title,Body,Primary}LocKey`. All three locales. |
| 4 | `src/apps/Wavee/Actions/Extensibility/WaveeActionTargeting.cs` | Only if you need a *new target mode* — which also means a new `SidebarActionTargetMode` value (append only), a `SidebarLayoutWire.TargetModeName`/`ParseTargetMode` pair, and a `WaveeActionUnavailable` reason. |
| 5 | Tests | `WaveeExtensionRegistryTests` (key validity, first-wins duplicates, targeting resolution, `PinRowRule`). |

The customizer's action picker (`Curated/SidebarItemPickers.cs`, `SidebarActionPicker`) enumerates
`registry.Actions` and offers exactly `AcceptedTargets`, rendering unavailable rows visible-but-disabled with a
reason — so a correctly declared descriptor needs **no** picker change.

---

## Change pane rendering or metrics

Everything visual is in one place now. **Do not** add a metric to a mode component.

| Want to change | Edit |
|---|---|
| pane padding / the row inset | `Pane/SidebarPaneMetrics.cs` — `PanePad`, `PaneInsetH`, `RowInset`. One owner; no row/band/card/strip may add a second horizontal inset. |
| row heights, artwork sizes, indent | `Shared/SidebarEntityRow.cs` → `SidebarRowMetrics.{ClassicHeight, HeightFor, ArtFor, IndentFor, SubtitleVisible}` (the ONE ladder). `SidebarPaneMetrics.RowHeight/ArtSize` only project it per section. |
| section rhythm | `SidebarPaneMetrics.{SectionGap, HeaderBodyGap}`, applied renderer-side by `SidebarPaneSlot.Banded` as **padding on a wrapper** — padding is unambiguously part of the slot's measured height, so `RepeatLayout.VariableList`'s extent stays honest and scroll anchoring cannot drift. (Suppressed for the pane's first row and directly after a `Divider`/`HeaderLabel`.) |
| count badges | `Shared/SidebarCounts.cs` (`Badge`/`Number`/`Pending`, `PlateW/PlateH`). The one quiet number. |
| the empty-pinned drop zone | `Shared/SidebarPinDropZone.cs` (`RestHeight 56` / `ActiveHeight 72`) — and re-check the mount's VariableList extent follows the 56↔72 change. |
| a row kind's layout | `Pane/SidebarPaneSlot.cs` (the kind switch at the top of `Render`) |
| the rootlist DROP CUES (line + Into plate) | `Pane/SidebarPaneSlot.cs` → `InsertionLine()` / `DropPlate()`. **Every `Prop.Of` in them reads `_scope.Index.Value`** — bindings are mount-only, so a captured index draws for the row the slot first mounted with (see [pitfalls.md](pitfalls.md)). Guarded by `SidebarPaneInvariantTests` |
| the "+" create affordance (any surface) | `Shared/SidebarPinDropZone.cs` → `SidebarCreateButton`. Turn it on in a header with `SidebarPaneConfig.HeaderCreate`; its drop specs live where the drop DECISION does — `SidebarPane.HeaderCreateDropSpec` / `SidebarPaneSlot.FolderCreateDropSpec` |
| the tree MULTI-SELECTION | rules: `Data/SidebarTreeSelection.cs` (pure, `SidebarTreeSelectionTests`). Ownership + epochs: `Pane/SidebarPane.cs` (`TreeSelection`, `SelectionVersion`, `ChecksVisible`, `MutateSelection`, `TreeVisibleOrder`, `TreeDragPayload`). Row wiring: `Pane/SidebarPaneSlot.cs` → `ApplyTreeSelection` + `Shared/SidebarEntityRow.cs` (`OnActivate`, `OnEscape`, `ChecksVisible`, `CheckLane`, `MultiSelected`) |
| a row's text / subtitle / icon fallback | `Pane/SidebarPaneText.cs` |
| the rail | `Pane/SidebarPaneRail.cs` + `Shared/SidebarRailItem.cs`; *content* is decided by `SidebarRowPlanner.BuildRail` (`ShowInRail`, per-kind caps, `RailTileCap = 40`) |
| skeletons | `Shared/SidebarSkeletons.cs` |
| covers / artwork decode | `Shared/SidebarCover.cs` — the single `decodePx` owner (see [pitfalls.md](pitfalls.md)) |
| chevron rotation | `Shared/SidebarChevron.cs` |
| collapse/expand or reorder motion | `Pane/SidebarPane.cs` — `Choreograph`, `Displacement`, `RowPlacement`, `_dispVersion` |
| EntityEmbed card ladder | `SidebarPaneMetrics.{CardHeight, CardCover}` |
| V3 chrome band heights | `Modes/LibraryV3/LibraryV3Metrics.cs` (chrome only — never row metrics) |

Tests: `SidebarPaneInvariantTests` (settled pane width is 56 or a valid expanded width),
`SidebarBuiltInDocumentTests` (Classic's IA + the density intent behind the 44-DIP rows), `SidebarRowPlannerTests`
/ `SidebarRailPlannerTests` (row + tile sequences). Extent expectations in those tests may be updated
**deliberately**; semantics must never be loosened.

---

## Add a display option

| # | File | Change |
|---|---|---|
| 1 | `src/apps/Wavee.Core/Sidebar/SidebarLayoutModel.cs` | Append to `SidebarDisplayField` (never renumber), add the field to `SidebarDisplayOptions` **with a default that makes a fresh section usable**, and add the arm to `SidebarSectionKinds.AllowsDisplayField` (an inapplicable field must be a `NoChange`, never a silent write). |
| 2 | `src/apps/Wavee.Core/Sidebar/SidebarLayoutReducer.cs` | The `SetDisplayOption` decode + clamp (the command carries `(field, int value)`; bools encode 0/1). |
| 3 | `src/apps/Wavee/Features/Sidebar/Persistence/SidebarLayoutDoc.cs` | A nullable `SidebarDisplayDto` member + read/write. Only options that **differ from the default** are written. |
| 4 | `src/apps/Wavee/Features/Sidebar/Curated/SidebarCustomizerLayout.cs` | `SidebarDisplayValues` (order, labels, choices) so the panel picks it up. |
| 5 | `src/apps/Wavee/Features/Sidebar/Curated/SidebarPropertyPanel.cs` | Only if it needs a control shape `CzToggleRow`/`CzSelectorRow`/`CzSliderRow`/`CzNumberRow` do not cover. |
| 6 | Consumers | The planner arm and/or `SidebarPaneSlot` that actually honours it. |
| 7 | `src/apps/Wavee/assets/loc/*.json` | `sidebar.option.<name>` (+ `…Sub` if the row has a sublabel). All three locales. |
| 8 | Tests | `SidebarLayoutReducerTests` (legality + clamp), `SidebarLayoutJsonTests` (round-trip), `SidebarCustomizerLayoutTests` (display projection). |

---

## Add a template

`src/apps/Wavee.Core/Sidebar/SidebarTemplates.cs`: a const id, an entry in `All`, a `Build` arm, and a builder that
mints ids via `SidebarIds.NewSection()`/`NewItem()`. Add `sidebar.template.<id>` (+ description) to all three
locales. Guard it in `SidebarTemplateTests` — that suite pins each template's composition **row for row**, so a
template change is a deliberate test edit.

`SidebarBuiltInDocuments.Classic` is *not* a template: it is not in `All`, its ids are stable strings, and it must
never appear in the customizer's template palette.

---

## Persistence + migration

| Change | Do this |
|---|---|
| add a field to an existing DTO | Make it nullable, read it with a default, write it only when it differs. That is a non-breaking change in both directions — **no** version bump. |
| a shape change an old build cannot read | Bump `SidebarLayoutStore.CurrentVersion` and add the arm to `SidebarLayoutMigrations.Upgrade`. v1→v2 is the identity-migration precedent. |
| anything | Never drop an unknown kind or member. `SidebarWireCarry` must survive: `ReadCurated` on load, `WriteCurated(_layout, _carry)` on **every** snapshot. |
| budgets | 64 KiB per section config, 2 MiB per document — over-cap is a *fault*, never a truncation. `SaveFault` does not latch. |

Tests: `SidebarLayoutJsonTests`, `SidebarLayoutStoreTests` (atomic write, one rotated `.bak`, fault
classification, `.bak` recovery, preserve-don't-destroy), `SidebarLayoutV2MigrationTests`, `SidebarBootstrapTests`.

Scalars go in `IAppSettings` under `SidebarKeys` instead (`src/apps/Wavee/Platform/AppSettings.cs`) — and the
tests' mirror of that table is `src/apps/Wavee.Tests/TestAppSettingsShim.cs`, which must be kept in sync by hand.

---

## Add loc keys

1. Add the key to **all three** files: `src/apps/Wavee/assets/loc/en-US.json`, `nl.json`, `ko-KR.json`. `nl`/`ko`
   are deliberate **partial overrides** (per-key fallback to en-US), so a key missing from them is legal but a key
   missing from en-US renders visibly as `[key]`.
2. Keep the files **CRLF, UTF-8 without BOM**.
3. Consume it as the generated `Strings.<Path>` const where one exists (the value of the const *is* the dotted
   key), or as a literal key const in a local `…Loc` class when the file is source-included by the tests and cannot
   see the generated table (`CzLoc` in `Curated/SidebarCustomizerPage.cs`, `SidebarUndoLabels` in Wavee.Core, and
   the literal `"sidebar-customize"` in `Features/Shell/ShellNav.cs` are the three landed precedents).
4. Read the loc-generator traps in [pitfalls.md](pitfalls.md) before choosing a key name.

---

## The customizer

There are **two** surfaces, not four regions: the docked pane in edit mode (the canvas) and a one-column companion
page. The tier ladder, the outline and the docked inspector are **deleted** — do not look for them, and do not
re-introduce a width below which the preview disappears (the whole point was to make the canvas unconditional).

| Want to change | Edit |
|---|---|
| whether the pane is in edit mode at all | `Pane/SidebarPaneConfig.cs` — the single `Func<SidebarEditState?>? Edit` member; supplied only by `Modes/CuratedSidebar.cs` |
| the session state behind that delegate (expanded section, "show contents", the open popover's subject, dispatch + reject messaging) | `Features/Sidebar/SidebarEditSession.cs` (`SidebarEditSession` / `ISidebarEditHost`) |
| the pure edit RULES (which sections reveal a body, when section drag is armed, the plan fold, card counts, band-slot → `MoveSection` / palette-drop → `AddSection`) | `Data/SidebarEditPlan.cs` — pure, so `SidebarEditPlanTests` covers it. **Hand `ToMoveSection`/`ToAddSection` the PERSISTED document**, never the render-path one (the Shortcuts head makes every index one too high) |
| what the edit projection PLANS | `Data/SidebarRowPlanner.cs` → `BuildEdit` (+ the `SidebarRowKind.SectionCard` row) |
| the section card itself + the options popover host | `Pane/SidebarPaneEditCard.cs` (the popover mounts `SidebarPropertyPanel`, 320 wide) |
| the card band's drag wiring | `Pane/SidebarPane.cs` (`SectionReorder`, `TryEditSectionBand`) + `Pane/SidebarPaneSlot.cs` (the card wrap site) |
| the companion page shell, presets, undo/redo, hidden list, dispatch | `Curated/SidebarCustomizerPage.cs` (also owns `RejectEpoch` and `CzLoc`) |
| the palette / template list / Destinations rendering | `Curated/SidebarCustomizerPalette.cs` + the `SidebarPalette` tables in `Curated/SidebarCustomizerLayout.cs` |
| the Destinations SET (which pages are offered) | `Data/SidebarPinId.PinnableRoutes` (the source of truth) + `SidebarPalette.ExtraDestinationRoutes`; labels come from `ShellNav.Dest`, never from a loc key on the entry |
| property rows + generated extension config rows | `Curated/SidebarPropertyPanel.cs` |
| the shared row/control vocabulary | `Curated/SidebarCustomizerControls.cs` (`CzRow.{Group, Prop, Wide, Ranged, Choice, Danger, Subject, Epoch}`, `CzToggleRow`, `CzSelectorRow`, `CzSliderRow`, `CzNumberRow`, `CzMenuButton`) |
| the item / action pickers | `Curated/SidebarItemPickers.cs` |
| the route itself | `SidebarLayoutMenu.CustomizeRoute`; registered in `Features/Shell/ContentHost.cs` and labelled in `Features/Shell/ShellNav.cs` |

Editing the **Shortcuts** section's items is not a special case at the call site — it is
`SidebarItemCommands.Add/Move/Remove` (Wavee.Core), which routes the sentinel id `SidebarIds.TopBarSection` to
`AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem`. Never hand-write that branch again; there is no
`Shared/SidebarNavBand.cs` and no `SidebarPaneConfig.NavBand`/`RailHead` to wire either. (`Shared/SidebarNavBandModel.cs`
survives as the band's pure SHAPING model + its tests, and has **no** production caller.)

---

## Entry points and wiring (read before touching composition)

| Where | Line | What |
|---|---|---|
| `src/apps/Wavee/Program.cs` | 47 | `SidebarBootstrap.Run(settings);` — before anything constructs `Services` |
| `src/apps/Wavee/App/Services.cs` | 239 / 262-264 / 271-275 / 313 | prefs + store; `PlayLogStore` init + `Playback.AttachPlayLog`; binder + sources + host; `RegisterSidebarSources` |
| `src/apps/Wavee/WaveeApp.cs` | 242 | `Ctx.Provide(SidebarPreferences.Slot, _services.Sidebar, …)` — **app root**, above the login gate |
| `src/apps/Wavee/Features/Shell/WaveeShell.cs` | 405 / 411-413 | `_actions.Sidebar = _sidebar;` · `WaveeExtensionRegistry.Build(_actions)` → `RegisterSidebarSources(registry)` → `_actions.Extensions = registry` (ONE build, one RegisterAll path — never double-register) |
| | 498 | the docked `SidebarHost` mount |
| | 782 | `_actions.Svc?.SidebarBinder.MountPoint()` — the projection pump, **at the app shell root, not inside the sidebar** |
| | 779 | `SidebarOnboardingChrome` — the one-time chooser gate |
| | 1085 | the narrow-drawer `SidebarHost` mount (`inDrawer: true`) |
| `src/apps/Wavee/App/PlaybackBridge.cs` | `PushState` | the play-log hook, right after the `Identity.Value = new PlaybackIdentity(...)` write |

`SidebarLayoutMenu` public surface: `CustomizeRoute`, `Button`, `Rows(prefs, go)` (the menu **model** — call at open
time only), `Model(prefs, go)` (the pane-background context menu). `HeaderButton` is **deleted** (defect 15).

Selection UX: `SidebarDesignPicker.{Row, Apply, Open}` · `SidebarDesignGating.{ShouldShowChooser, MarkChooserSeen,
ActiveDesign, OffersCustomize, CanCustomize, IndexOf, FromIndex, TitleKey, SubtitleKey}` (all pure) ·
`SidebarOnboardingChrome(settings)` · the Settings → General card group in
`src/apps/Wavee/Features/Settings/SettingsPage.General.cs`.
