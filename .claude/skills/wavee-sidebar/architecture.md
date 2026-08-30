# Wavee sidebar — architecture

Every path, type and member below exists on disk. Paths are relative to the repo root
(`C:\wavee\fluent-gpu`).

---

## 1. Layer 1 — the document (`src/apps/Wavee.Core/Sidebar/`)

Framework-neutral by construction: no FluentGpu type appears in any shape (glyph *names* live here, glyph
*codepoints* app-side), so `Wavee.Tests` drives it without pulling the engine in.

| File | Owns |
|---|---|
| `SidebarLayoutModel.cs` | The payload model + all per-kind facts. `SidebarSectionKind` (0…12, `Extension = 12`), `SidebarSectionSpec`, `SidebarItemSpec`, `SidebarDisplayOptions`, `SidebarEntityQuery`, `SidebarCustomLayout`, `SidebarExtensionRef`, `SidebarActionBinding`, `SidebarJson`, `SidebarIconNames`, `SidebarIds`, and **`SidebarSectionKinds`** — the single-owner table of per-kind rules (`DefaultTitleLocKey`, `PaletteNameLocKey`, `DefaultDisplay`, `EmptyBehaviorFor`, `AcceptsItems`, `SupportsLibraryQuery`, `RequiresExtensionRef`, `ItemCapacity`, `AllowsDisplayField`, `IsNestable`, `IsKnown`/`MaxKnown`). |
| `SidebarLayoutCommands.cs` | The 18 command records + `SidebarUndoLabels` (20 loc-key consts — hidden/shown and collapsed/expanded each get their own label) + `SidebarCommandResult` + `SidebarRejectReason`. Commands are in-memory only and never serialized, which is why they *are* a record hierarchy while the payload model is not. |
| `SidebarLayoutReducer.cs` | The one pure `(layout, command) → SidebarCommandResult`. Enforces caps (`MaxSections`, `MaxItemsPerSection`, `SidebarExtensionRef.MaxConfigBytes`), depth-1 nesting, query legality repair, icon-name validation. |
| `SidebarUndo.cs` | The 50-step ring of **pre-image snapshots** (not inverse commands): the document is immutable records, so an edit rebuilds only the spine and structurally shares the rest. That is why `ApplyTemplate`/`ResetLayout` need no special machinery. |
| `SidebarTemplates.cs` | The five seed layouts: `Curated` (`"curated"`), `ClassicInspired` (`"classic"`), `V3Inspired` (`"library"`), `Minimal` (`"minimal"`), `Blank` (`"blank"`); `All`; `Build(templateId)` falls back to Curated for an unknown id. |
| `SidebarLayoutCompare.cs` | Structural diff, used by the reducer's NoChange detection and the tests. |

**Why the model is one closed record per section, discriminated by a `Kind` byte** (not a `[JsonDerivedType]`
hierarchy): it must survive AOT source-gen serialization with zero reflection risk, must round-trip a section kind
a *future* build introduces, and the property panel wants one uniform shape to edit.

`JsonElement` has no content equality (record `Equals` would compare the backing document by reference), so
`SidebarExtensionRef`, `SidebarActionBinding` and `SidebarEntityQuery` all declare `Equals`/`GetHashCode` **by
hand** and compare canonical JSON (`SidebarJson.Same`) / ordinal uri lists (`SidebarEntityQuery.SameUris`).
Without that, every load looked like an edit.

---

## 2. Layer 2 — the service (`Features/Sidebar/SidebarPreferences.cs`)

The one owner of sidebar state, provided at the **app root** (`WaveeApp.cs:242`,
`Ctx.Provide(SidebarPreferences.Slot, _services.Sidebar, …)`) and constructed in `App/Services.cs:239`:
`new SidebarPreferences(settings, SidebarLayoutStore.ForApp())`.

Its surface is deliberately **FLAT** (not grouped — `prefs.V3Filter`, never `prefs.V3.Filter`):

- design + geometry: `Design`, `Tiers`, `SwitchDesign(next)`, `Width`, `Collapsed`, `WidthUserSet`,
  `CommitWidthDrag`, `SetCollapsed`, `SetResponsiveWidth`, `ResetWidth`, `SetViewportWidth`
- Classic sections: `ClassicPinnedOpen`, `ClassicLibraryOpen`, `ClassicPlaylistsOpen`,
  `SetClassicSection(ClassicSection, bool open)`
- V3 view state: `V3Filter`, `V3Qualifier`, `V3Sort`, `V3Desc`, `V3View`, `V3GridSize`, `V3SearchOpen`,
  `V3Search` + `SetV3Filter/Qualifier/View/GridSize/Sort/SearchOpen`; local custom order via `CanReorderV3`,
  `V3CustomOrder`, `V3OrderVersion`, `V3RankOf(id)`, `SetV3CustomOrder(orderedIds)`
- folders: `IsFolderExpanded`, `ExpandedFolders`, `FolderVersion`, `SetFolderExpanded`, `ToggleFolder`
- pins (shared across all three designs): `Pins` (`SidebarPinStore`), `PinsVersion`, `IsPinned`, `Pin`,
  `Unpin`, `InsertPin`, `MovePin`, `TouchPin`
- the document: `Layout`, `LayoutVersion`, `Dispatch(command)` (alias `ApplyCurated`), `ApplyTemplateId`,
  `CanUndo`/`CanRedo`/`UndoLabel`/`RedoLabel`/`Undo()`/`Redo()`
- projection: `Entries` (`SidebarEntries` — `Buffer`/`Current`/`Version`/`State`/`QualifiersAvailable`/`PinCount`),
  `Binder`, `PublishFirstSeen`, `FirstSeen`
- health: `PersistenceHealth`, `Fault`/`FaultDetail` (load), `SaveFault`/`SaveFaultDetail` (save, does not latch),
  `DiscardCorruptDocument()`, `Flush()`

Per-mode remembered state (locked decision 3) lives in the pure helper `SidebarPaneState`
(snapshot outgoing / restore incoming) behind `SwitchDesign`; per-design width tiers live in `SidebarDesignInfo`
(`Slug`, `Tiers`, `MountKey`, `FromInt`, `Count`) in `Features/Sidebar/SidebarDesign.cs`.

---

## 3. Layer 3 — persistence (`Features/Sidebar/Persistence/`)

One versioned, source-generated JSON document at
`%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json` — beside `history.json` (locked decision 8).

| File | Owns |
|---|---|
| `SidebarLayoutDoc.cs` | The wire DTOs (`SidebarLayoutDocDto`, `SidebarPinDto`, `SidebarV3Dto`, `SidebarCuratedDto`, `SidebarSectionDto`, `SidebarItemDto`, `SidebarDisplayDto`, `SidebarQueryDto`, `SidebarExtensionDto`, `SidebarActionDto`), the AOT context `SidebarLayoutJsonCtx` (camelCase + `WhenWritingNull` + `WriteIndented`, declared on the context so no call site carries loose options), and **`SidebarLayoutWire`** — the one enum ⇄ string translation layer, plus `SidebarWireCarry`/`SidebarCuratedRead` (`ReadCurated`/`WriteCurated`). |
| `SidebarLayoutStore.cs` | `CurrentVersion = 2`, `MaxDocumentBytes = 2 MiB`, `DefaultPath()`, `ForApp()`, atomic temp→`File.Replace` with one rotated `.bak`, load-fault classification (`SidebarLoadFault`) and save-fault classification (`SidebarSaveFault`). |
| `SidebarLayoutMigrations.cs` | `Upgrade(dto)` — v1→v2 is an **identity** migration; an existing document loads unchanged and stamps `"version": 2` on its next ordinary save. |
| `SidebarLayoutDefaults.cs` | `CuratedLayout()` — the fallback document a probe/harness mount with no preference service uses. |

**Forward compatibility is a hard contract**, by two mechanisms:

1. Section kinds are **strings** on the wire (`"pinned"`, `"jumpBackIn"`, …, `"extension"`). An unrecognized kind
   string round-trips *untouched* as an opaque blob at its original index and renders as nothing.
2. Unknown **members** anywhere in the tree are captured by `[JsonExtensionData]` and re-attached on write,
   matched by the owning section/item id.

`SidebarPreferences` must thread the carry: `ReadCurated` on load (`_layout` + `_carry`) and `WriteCurated(_layout,
_carry)` on **every** snapshot. Drop the carry and you silently delete a newer build's data.

Missing `version` ⇒ treated as **malformed** (not v1). `version > CurrentVersion` ⇒ `TooNew`. A corrupt primary
falls back to `.bak`, then to the Curated default *in memory* — the bytes on disk are preserved, and the customizer
surfaces the fault. `SaveFault` does not latch: over-budget `Commit()` no-ops, so in-memory state runs ahead of
disk until the document shrinks.

Scalars (width/collapsed/design/V3 view state/onboarding markers) live in `IAppSettings` under the
`SidebarKeys` table, not in the JSON document. `App/SidebarBootstrap.cs` (`Run(settings)`, called from
`Program.cs:47`, `TargetVersion = 1`) does the fresh-install probe + legacy pane-key migration before `Services`
exists: fresh installs default to **Curated** and see the chooser once; existing installs (library.db /
credentials / onboarding marker) silently stay on **Classic** and never see it.

---

## 4. Layer 4 — the data pipeline (`Features/Sidebar/Data/` + `SidebarProjectionBinder.cs`)

### The engine-free half (source-included by `Wavee.Tests` — `Data\*.cs`, one level deep)

| File | Owns |
|---|---|
| `SidebarLibraryEntry.cs` | The unified row record every source produces (`Id`, `Uri`, `Kind`, `Name`, `Creator`, `Cover`, `MosaicTiles`, `Depth`, `FolderId`, `SourceOrder`, `SortStamp`, …). `ForRoute(routeKey, name)` mints an app-route row. |
| `SidebarProjection.cs` | `Build(buffer, …)` — the unified projection over `LibraryStore` + `HistoryStore`; `PinsFirst` (returns `PinCount`); `QualifiersAvailable(flavorMask)`; `MatchesQualifier`. |
| `SidebarSort.cs` | The five comparators (`Recents`, `RecentlyAdded`, `Alphabetical`, `Creator`, `CustomOrder`) behind `Apply`. |
| `SidebarSearch.cs` | Normalization + diacritics folding. `InvariantGlobalization=true` ⇒ collator probe + a Latin-1 fold fallback (folding is limited to Latin Extended-A in invariant mode — a stated limitation). |
| `SidebarRecency.cs`, `SidebarFirstSeen.cs`, `SidebarPinId.cs` | Visit recency; the first-projection stamp used as the playlist added-at proxy; the pin-id ⇄ kind/uri scheme. |
| `SidebarRowPlanner.cs` | **The planner.** `SidebarRowKind` (13 kinds), `SidebarRow` (POD — no string is allocated during planning), `SidebarProjectionInput`, `SidebarSourceState`, `SidebarSectionSlice`/`ISidebarSectionSlices`, `SidebarPlanBuffers`, `Build(doc, input, buffers)` and `BuildRail(…)` (`RailTileCap = 40`). |
| `RootlistSlotResolver.cs` | **The drop-slot GEOMETRY** — `SidebarDropKind`/`SidebarDropRefusal`/`SidebarRowFacts`/`SidebarDropSlot`, `Resolve`/`DepthRange`/`EdgeFor`/`PickDepth`, the cue geometry (`SidebarDropCue`) and the pre-move undo anchor (`RootlistUndoAnchors.TryResolve`). **No legality lives here** — see `RootlistDropDecision.cs`. See *Rootlist drag & drop* in §5. |
| `RootlistDropDecision.cs` | **The one drop decision**: `RootlistSlotTarget`, `RootlistSlotMapper.TryMap` (cue + the FULL projection tree → destination) and `RootlistDropDecision.Refine`/`Check`/`RefusalFor` — the single `RootlistOps.CheckMove` call site in the app and the single `RootlistMoveCheck → SidebarDropRefusal` table. |
| `RootlistTreeNav.cs` | The non-mouse half: `RootlistSiblingRun`, `SidebarTreeNavLayout.Decide`, `RootlistFolderChoice`, `PickerDestinations`/`HasDestinations`/`TryTopLevelAnchor`/`TryEntry`/`TryFolder`/`RefOf`. |
| `SidebarStageHold.cs` | The mid-drag parking bay (`TryHold`/`TryFlush`/`Discard`) — generic and state-only, so the freeze's state machine is testable without a pane. |
| `SidebarReorderClamp.cs` | `Offset(slot, from, to, extent)` — the displacement hint for a **clamped** reorder destination. |
| `SidebarDataSource.cs` | The contribution contracts: `ISidebarDataSource`, `SidebarDataSourceBase`, `ISidebarContributionHost`, `SidebarContributions`, `SidebarConfigSchema`/`SidebarConfigField`/`SidebarConfigFieldKind`, `SidebarSourceConfig` (never-throwing typed readers over opaque JSON), `SidebarSourceRequest`, `SidebarContributionAvailability`, and the declared-capability enums (`SidebarSourceItemType`/`Filters`/`Sorts`/`Paging`). |
| `SidebarSourceMap.cs` | The pure mappers every adapter is built out of: domain record → `SidebarLibraryEntry`, plus service-health → `SidebarSourceState`. Everything that can be *wrong* lives here so the tests reach it. |
| `SidebarBinderPipeline.cs` | The pure half of the binder: `SidebarBinderTriggers` (12 lanes + `Fold()`), the filter/qualifier/search compaction, the sort + pins-first shaping, contribution resolution (missing/disabled/incompatible verdicts), `SidebarContributionCache` (the last-good snapshot replay — M3's stale-badge seam), `SidebarExtensionSlices`, `SidebarSourceIndex`. |

### The impure orchestrator

`Features/Sidebar/SidebarProjectionBinder.cs` holds the stores, signals and the UI-thread marshaller and **nothing
that decides anything**. It is the missing `Entries` rebuild driver: one unified projection over `LibraryStore`'s
warm cells + `HistoryStore` recency + `PlayLogStore` + the pin store, rebuilt whenever any of them moves; the
published entry list; the first-seen commit; the contribution slices; and `CurrentInput` — the
`SidebarProjectionInput` every pane hands to the planner. `RecencyCap = 40`. All rebuild buffers are allocated once
and reused.

**Why a mounted pump.** A `ReactiveRuntime` is not reachable from a plain service, so a service cannot own an
`Effect` and cannot observe a `Signal<T>`. `binder.MountPoint()` therefore returns a zero-size always-mounted
component that *reads* every trigger signal in its render (subscription only) and calls `Sync()` from a
`UseEffect` keyed on `SidebarBinderTriggers.Fold()`. It is mounted **once at the app root**
(`WaveeShell.cs:782`), not inside the sidebar — the docked pane and the drawer come and go, the projection may not.

### Adapters (`Data/Sources/` — NOT source-included; they hold engine-bound services)

`WaveeBuiltInDataSources.cs` (`RegisterAll`, `Publish`, `Attach`, `ContributionHost`),
`SidebarProjectionSources.cs`, `SidebarFeedSources.cs`, `SidebarPlaybackSources.cs`. Nine first-party sources,
ids in `SidebarContributions`:

`wavee.library` · `wavee.history.visited` · `wavee.history.played` · `wavee.playlistTree` ·
`wavee.artist.topTracks` · `wavee.newReleases` · `wavee.concerts` · `wavee.queue` · `wavee.nowPlaying`

Wiring, verbatim from `App/Services.cs`:

```csharp
:239  Sidebar = new SidebarPreferences(settings, SidebarLayoutStore.ForApp());
:262  PlayLog.Init(PlayLogStore.DefaultPath());
:263  PlayLog.LoadFromDisk();
:264  Playback.AttachPlayLog(PlayLog);
:271  SidebarBinder = new SidebarProjectionBinder(Sidebar, LibraryStore, PlayLog, Playback);
:272  SidebarSources = WaveeBuiltInDataSources.RegisterAll(registrar: null, SidebarBinder, library,
                          ArtistPopularTracks, WhatsNew, Concerts, Playback);
:274  SidebarBinder.UseHost(new WaveeBuiltInDataSources.ContributionHost(SidebarSources), SidebarSources);
:275  Sidebar.Binder = SidebarBinder;
:313  public void RegisterSidebarSources(WaveeExtensionRegistry registry)
:314      => WaveeBuiltInDataSources.Publish(registry, SidebarSources);
```

**Threading:** UI thread only, unsynchronized. An adapter that completes an async fetch **must** marshal back
through the `post` the binder handed it before touching `State` or raising `Changed`. Re-entrancy is fenced: a
source that raises `Changed` mid-rebuild only marks the binder dirty.

---

## 5. Layer 5 — the ONE renderer (`Features/Sidebar/Pane/`)

| File | Owns |
|---|---|
| `SidebarPane.cs` | The component. Subscribes the epochs, plans in a `UseMemo` keyed on `PlanDep`, publishes the plan as a **plain field** to the bound slots, drives the count signal from a layout effect, owns reorder bands + drop-to-pin + **the published rootlist drop slot** (`_dropSlot`, `ResourceDropSpec`, `RootlistSlotFor`, `TryDecide`, `RefuseDrop`, the mid-drag freeze) + the collapse/expand choreography, and hosts the rail. It decides nothing itself: `TryDecide` is a call into `RootlistDropDecision.Refine`. |
| `SidebarPaneSlot.cs` | One bound slot and the whole row-kind vocabulary. A Component per slot, because `ItemsView.CreateBound` builds a slot once and recycles it by writing `scope.Index` — a render that reads `Index.Value` re-renders exactly on a recycle. |
| `SidebarPaneRail.cs` | The 56-DIP rail. Content is *data* (`ShowInRail` sections, per `BuildRail`), not code, so Classic's rail and Curated's rail cannot drift. Not virtualized — the planner caps it at 40 tiles. |
| `SidebarPaneText.cs` | The pure display rules: `TitleOf`, per-kind `SubtitleOf`, item lookup, icon fallbacks, the "never render a blank row" degradations. |
| `SidebarPaneInlineControls.cs` | An `EntityList` section's inline kind-chips + sort/view trigger, rendered as header chrome. Every edit rewrites *that section's persisted spec* through `SetQuery`/`SetDisplayOption`, so it is undoable and survives a restart. Suppressed when `ReadOnly`. |
| `SidebarPaneMetrics.cs` | `PanePad (8,8,8,12)`, `PaneInsetH 16`, `RowInset`, `SectionGap`, `HeaderBodyGap 2`, `EmptyHintHeight 32`, `GridCellMax 160`, `RowHeight(section)`, `ArtSize(section)`, `CardHeight`/`CardCover`. |
| `SidebarPaneConfig.cs` | The seam (below) + `SidebarPaneReorder` + `SidebarPaneReorderCommit.Default`. |
| `SidebarBuiltInDocuments.cs` | Classic as a locked document. |

### How a frame flows

1. The pane subscribes document + projection + pin + folder + search + **mode** epochs and re-plans in a `UseMemo`
   keyed on their fold (`PlanDep`). The planner is pure and reuses ONE caller-owned `SidebarPlanBuffers` **per
   pane** — the expanded pane and the rail each own their own instance, because a plan *aliases* its buffers.
2. The plan is published to the bound slots as a **plain field** (never a signal write from `Render` — the
   render-purity rule). Each slot re-reads it at *its* render time and calls `SidebarPane.SubscribeRowEpoch(index)` so a
   projection rebuild / customizer edit / section toggle / keystroke re-skins the realized window without the list
   rebuilding.
3. The row **count** is the one thing the frozen-at-mount `ItemsView` cannot read from a field: it rides
   `CountSignal`, written in a layout effect (a render-time write would be a backwards write, and the DEBUG
   `ReuseGuard` explicitly rejects a changed frozen `ItemCount`). The very first frame seeds it once, before the
   list exists — provably not a backwards write because nothing has read it yet.

### `SidebarPaneConfig` — the only mode seam

| Member | Type | Meaning |
|---|---|---|
| `Design` | `SidebarDesign` (required) | Log field + scroll/telemetry identity **only**. The renderer never branches on it. |
| `ScrollKeyPrefix` | `string` (required) | The pane appends `".drawer"` for the drawer mount so the two never fight over one saved offset. |
| `Document` | `Func<SidebarCustomLayout>` (required) | The live document. Invoked inside the pane's render. |
| `Input` | `Func<SidebarProjectionInput, SidebarProjectionInput>?` | Fold the mode's own filter/sort/search state into the planner input. The pane still applies its own search-head override *on top*. |
| `ModeEpoch` | `Func<int>?` | Mode-owned state folded into one int; read in the plan `DepKey` *and* the per-row epoch. Read signals with `.Value` here — that read **is** the subscription. |
| `SetSectionCollapsed` | `Action<string,bool>?` | Where collapse state lives. Null ⇒ non-collapsible headers. |
| `ReadOnly` | `bool` | Suppresses inline `EntityList` controls, the missing-entity "Remove" verb and the empty-pane customize CTA; `Dispatch` becomes a no-op. |
| `SearchHead` | `bool` | Render the pane-owned library-only search box (only when the document actually contains a visible `EntityList`). |
| `Head` | `Func<Element?>?` | Arbitrary mode chrome above the scroll surface (V3's header/toolbar/chips/breadcrumb). Rendered before `SearchHead`. |
| `ShowLayoutMenu` | `bool` (default true) | Hang the quick layout menu off the pane's **first** section header. |
| `RailLayoutMenu` | `bool` (default true) | Put it at the bottom of the rail too. |
| `RailFooter` | `Func<Element?>?` | An extra rail affordance after the planned tiles (Classic's create-playlist "+"). |
| `ActivateFolder` | `Action<string,string>?` | What activating a folder disclosure row does. Null ⇒ toggle the shared folder-expansion state. Replaces both the row's click **and** the expand/collapse verb in its context menu, so the two can never disagree. |
| `IsReorderableSection` | `Func<SidebarSectionKind,bool>?` | Default: `Pinned`/`StaticLinks`/`CustomGroup` — never `PlaylistTree` (V3's local custom order opts in). Folder CRUD is no longer locked (the old "locked decision 9" is **lifted**: see `FolderActions`), but the rootlist is written only through `WaveeResourceDrop.MoveRootlist` and `FolderActions`, never through a reorder band. |
| `TreeSortedNonCustom` | `Func<bool>?` | Null ⇒ false (Classic/Curated always show rootlist order). True ⇒ the drop resolver refuses `Before`/`After`/`EndOfList` with `drag.clearSortingToReorder` while `Into` stays legal. A live probe, never a value. See *Rootlist drag & drop* below. |
| `ClampReorderSlot` | `Func<SidebarSectionKind,int,int,int>?` | Constrain a LIVE reorder gesture's reachable slots. Null ⇒ every band slot is reachable. V3 supplies `LibraryV3View.ClampToSiblingRun` so the gap never opens across a folder boundary. Called from the displacement path — must stay allocation-free. |
| `CommitReorder` | `Action<SidebarPaneReorder>?` | Null ⇒ `SidebarPaneReorderCommit.Default` (Pinned → the shared pin store; every other reorderable kind → the undoable `MoveItem`). |
| `OnCustomize` | `Action?` | Null ⇒ those surfaces render without their action rather than with a dead one. |
| `HeaderCreate` | `bool` (default false) | Put the **"+"** in every `PlaylistTree` section header — a flyout ([New playlist · New folder]) and a drop destination (rootlist ⇒ a new folder holding what you dropped; a track set ⇒ a new playlist). Classic and Curated set it; V3 leaves it false because its own chrome carries a "+". It replaced the deleted `CreateAction` ROW. |
| `OnCreatePlaylist` | `Action?` | The plain-click half of the header/rail "+" and the first row of its flyout. Null ⇒ the header "+" is not offered at all, rather than offered dead. |
| `Edit` | `Func<SidebarEditState?>?` | **PHASE 2 / Decision B.** Non-null *return* ⇒ the pane is the CUSTOMIZE CANVAS: it plans through `SidebarRowPlanner.BuildEdit` (one `SectionCard` per top-level section + the one expanded section's real body) instead of `Build`. A delegate, never a snapshot — the session changes several times a minute while the config is frozen at mount. Only `CuratedSidebar` supplies one; the renderer still learns nothing about `Design`. |

There is **no** `NavBand` / `RailHead` member and there never should be again: Phase 1 materialises the shortcut band
as an ordinary `StaticLinks` section (`SidebarShortcutsSection.Prepend`) at the head of every design's document, so the
planner, the slot, the rail's `ShowInRail` pass, the reorder band and the selection indicator all serve it with no
band-specific code. That also removed the documented **selection hack**: a band tile for `"home"` plus a plan row for
`"home"` were two registrations under one route key, which forced the band to opt out of the pane's route-keyed
selection transaction. With one row, the problem ceases to exist.

`SidebarPaneReorder(Section, FromSlot, ToSlot, SlotCount, KeyAt)` carries everything a commit could need and
nothing about the widget: the renderer knows the geometry, only the mode knows where the order *lives*.

The band's ITEM edits route through `SidebarItemCommands` (Wavee.Core), the ONE Add/Move/Remove chooser: the sentinel
id `SidebarIds.TopBarSection` picks `AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem`, every real section id picks
`AddItem`/`MoveItem`/`RemoveItem`. Never re-decide that at a call site — a raw `MoveItem` at the sentinel is a silent
`UnknownSection` rejection, i.e. a drag that snaps back with no message.

### Selection and motion (one mechanism for every mode)

Rows recycle, so a node-handle map keyed by route is not stable and Classic's measured overlay pill
(`SidebarSelectionPill`) cannot work. Selection is drawn **inside** the row: the shared 4-state ramp
(`SidebarEntityRow`) plus a 3×16 accent indicator over the row's own 3-DIP selection gutter, cross-faded on
`MotionTok.ControlFaster`. This matches WinUI's per-`NavigationViewItem` SelectionIndicator.

| Gesture | Token / mechanism |
|---|---|
| design switch | `MotionTok.ControlFast` + `Enter(Opacity: 0)` on the keyed remount (`SidebarHost`) |
| section chevron | `SidebarChevron` — one glyph + animated `Rotation` on `MotionTok.ControlFast` |
| collapse / expand | `SidebarPane.Choreograph` seeds `EntranceOptions.ItemFadeFrom` (fade + 6-DIP rise, 16 ms/row stagger capped at 8) and `ItemFlipFrom` (FLIP glide for displaced rows), then bumps `_dispVersion` |
| reorder | `MotionTok.ItemPlacement` through `Reorderable` (`LiveProject = false`, `ShowInsertionLine = false`, displacement channel) |

Reduced motion is **not** branched on in authoring code — seeds go through the scheduler under a named token,
which reads the preference as a *value*.

**The pill's visibility and its motion have separate owners — do not merge them back.** The indicator's opacity is a
**bound** read of the slot's live state (`SidebarPillState.Opacity`, fed by `SidebarPaneSlot.PillState` = the snapshot
re-derived against the live route AND `SidebarRowResolve.SelectsRoute`), so it re-evaluates on the row's own epoch with
no re-render and no dependence on whether a transaction ran. `NavigationSelectionMotion` owns the **transform** (the
route→route slide), and `SidebarPane.RunSelectionTransaction` may only ever assert `visible: true` for a node
`PillDrawsSelected` agrees with — a force-complete settles at the derived verdict, and `TrySelectionPill` re-validates
the route→node registry (a recycled slot keeps its node live while drawing a different row). It used to be
`Opacity = selected ? 1f : 0f`, a mount-time literal, with the transaction writing the same channel: any write that lit
a node the row called dark simply stuck, which is how **two** pills were on screen at once — the open route plus the
previously-opened route, or the now-playing row that inherited its node. The pill means *"this is the open route"*;
playback is the `|||` glyph and is never an input to it (`RowSelectsRoute` is route-only). Pinned by
`SidebarChurnTests` §F3d.

### Shared primitives (`Features/Sidebar/Shared/`)

`SidebarEntityRow` (+ **`SidebarRowMetrics`** — the ONE height/art/indent ladder: `ClassicHeight 44`,
`HeightFor(density, hasSubtitle)` = 32/40|44/44|48, `ArtFor` = 20/32/40, `IndentFor(depth)` = 6 + depth×12 capped
at 4) · `SidebarCounts` (**the one quiet badge**: 11f tertiary right-aligned number + a 20×12 shimmer plate while
pending — `InfoBadge.Count` is gone) · `SidebarChevron` · `SidebarCover` (`S20…S64`, `Radius`, `ForEntry`,
`ForPin`, `Art`, `Glyph`, and the bucketed decode ladder) · `SidebarSectionHeader` · `SidebarPinDropZone`
(`RestHeight 56` / `ActiveHeight 72`) · `SidebarRailItem` (`Box 40`, `ArtEdge 36`) · `SidebarSkeletons` ·
`SidebarSelectionPill` (see the deletion candidates in [pitfalls.md](pitfalls.md)).

### Rootlist drag & drop — one resolver, one published slot, one commit

The sidebar tree's organisation gesture (re-ordering playlists and folders, filing them into folders, un-nesting
them). Reworked 2026-08-16; the **old zone model is gone** — `SidebarPane.RootlistPlacementFor` (a hidden 25/50/25
or 50/50 vertical hit-test), the boolean `IsResourceDropActive` and the `Signal<int> _resourceDropRow` were deleted
outright, along with the "before/after needs no caption" rationale they carried. Three outcomes used to share ONE
pixel-identical accent plate; pointer X was never read; the cycle/no-op guards lived three layers down in
`RootlistOps` where they could only fail silently.

**The shape now.** ONE pure function turns *(row facts, t, xInRow)* into a slot; ONE signal publishes it; the row
draws a **line** or a **plate**; the drop **consumes** the published slot instead of recomputing one.

```
pointer ──► SidebarPane.RootlistSlotFor        (viewport/scroll math → t, xInRow)
              └► RootlistSlotResolver.Resolve      (PURE: geometry + the payload-identity refusals)
                   └► RootlistDropDecision.Refine   (PURE: map over the FULL tree, then CHECK)
                        ├► RootlistSlotMapper.TryMap    (cue → RootlistSlotTarget, or NOT ARMED)
                        └► RootlistOps.CheckMove        (legality vs the STORE's marker stream)
                             └► Signal<SidebarDropSlot> _dropSlot      ONE write per hover
                                  ├► SidebarPaneSlot.InsertionLine     (line, bound props)
                                  ├► SidebarEntityRow plate            (Into only)
                                  ├► ResourceDropSpec.CaptionFor       (the chip sentence)
                                  └► CommitDrop → TryDecide → WaveeResourceDrop.MoveRootlist
```

**ONE AUTHORITY (2026-08-16).** "Is this legal" and "where does it land" used to be decided in **eight** places over
**three** tree models — a flattened `SidebarLibraryEntry` list with no end-group markers (`RootlistTreeMoves`), the
expansion-filtered VISIBLE plan (`SidebarPane.TryMapSlot`/`TryFolderEntry`/`SourceContainsRow`), and the real marker
stream. They disagreed, and the user hit all four consequences live: a legal *Into* refused as "Already there", a caret
armed on a slot nothing could map (the drop did nothing), an outdent that filed the item back INTO the folder, and a
success toast for a move that was never posted. All of it is deleted. There is now exactly ONE call to
`RootlistOps.CheckMove` in the app (`RootlistDropDecision.Check`, pinned by `RootlistDropScenarioTests`), reached by the
pane, the rail tile, the folder picker and the bridge (`LibraryBridge.CheckRootlistMove`, over `IStore.Rootlist()`).
Two rules follow and are tested as invariants:

* **An unmappable slot is never armed** — `Refine` disarms it as `Unavailable`, so line and plate go off and the chip
  carries a sentence.
* **A commit never returns quietly** — `CommitRootlistSlot` re-runs the same decision and, if it no longer resolves,
  logs it and shows the refusal (`SidebarPane.RefuseDrop`). One drop issues exactly one `MoveRootlistItemsAsync` —
  ONE delta, whether it carries one item or a whole multi-selection.

#### The resolver (`Features/Sidebar/Data/RootlistSlotResolver.cs` — engine-free, source-included by `Wavee.Tests`)

`SidebarDropKind {None, Before, After, Into, EndOfList}` ·
`SidebarDropRefusal {None, Self, IntoItself, IntoDescendant, NoOp, SortedList, NotLoaded, Unavailable}` ·
`SidebarRowFacts(IsFolder, FolderExpanded, FolderHasChildren, Depth, NextVisibleDepth, CenterAccepts, SourceIsSelf,
SortedNonCustom, RootlistLoaded)` + `{ IsListEnd }` ·
`SidebarDropSlot(PlanIndex, Kind, Depth, Refusal)` with `IsArmed` / `DrawsLine` / `DrawsPlate`.

**Invariant: a refused slot carries `Kind = None`**, so a refusal draws neither cue and the caption is the only
thing that speaks. `SidebarRowFacts` is built structurally by `SidebarPaneSlot.TreeRowFacts` (from `Plan.Rows` +
`Plan.Entries` + folder expansion) and completed **at hover** with the ones the payload decides (`SourceIsSelf`, and
`CenterAccepts` for a track-bearing payload) — hover is the first moment a payload exists, and it is a nine-field
struct copy, so it costs nothing inside the 0-alloc frame region. `SourceIsSelf` is the ONLY payload legality fact left
here, and only because "into MYSELF" and "before myself" need two different sentences where the marker stream reports
one `SameItem`; the cycle fact (`SourceIsAncestorOfRow`) is **deleted** — it was a third opinion about a question
`CheckMove` answers exactly.

**Geometry** (`Resolve(planIndex, t, xInRow, rowHeight, in facts, in previous)`):

| row | bands |
|---|---|
| any | `edge = EdgeFor(h) = clamp(0.30·h, 10, 16)`, further capped at `h/2`; `top = edge/h`, `bottom = 1 − top` |
| **folder** (any state) | `t < top` ⇒ `Before(folder)` · `t > bottom` ⇒ expanded-with-children ? `Before` at `Depth+1` (the precise "first child" slot) : `After` at the picked depth · else **`Into`** (append as last child) |
| **`CenterAccepts` playlist** (editable ∧ payload has tracks) | 30/40/30: `Before` / **`Into` = deposit tracks** / `After`. The retained copy gesture — the plate is what distinguishes it from the two edge lines. |
| anything else | 50/50 `Before` / `After` — no dead centre a row cannot honour |
| **`IsListEnd`** (the `TreeEnd` chrome row, `SidebarRowGeometry.TreeEndHeight` = 24 DIP) | the whole row is one `EndOfList` at depth 0; no bands |
| degenerate (`planIndex < 0`, non-finite `t`/`h`, dead viewport/scene) | `Kind = None, Refusal = Unavailable` — refuse with a reason, never guess |

**Depth** matters only for `After`/`EndOfList`. `DepthRange(facts) = (Min: NextVisibleDepth, Max: Depth)` clamped so
`Min ≤ Max`; a slot is ambiguous iff `Min < Max`, which is exactly "after the last visible child of a (possibly
nested) folder". The depth pick (private, reached through `Resolve`) reads x:
`round((xInRow − SidebarRowGeometry.RowInsetLeft) / IndentStep)` clamped to the range, with **4-DIP hysteresis**
(`DepthHysteresis`) around each boundary against the previous slot. **The default is `Max`** — the pointer over the
label sits far past the indent ladder, so a drag that never moves left stays at the row's own depth; travelling
LEFT outdents one step per `IndentStep` (12).

**Refusal table**, evaluated after the zone (two arms depend on which zone you are in) and in this order:

| condition | refusal | caption (`SidebarPane.RefusalSentence`) |
|---|---|---|
| `!RootlistLoaded` | `NotLoaded` | `drag.stillLoading` |
| `SourceIsSelf` ∧ `Into` | `IntoItself` | `drag.cantMoveIntoItself` |
| `SourceIsSelf` | `Self` | `drag.cantMoveHere` |
| `SortedNonCustom` ∧ kind ∈ {Before, After, EndOfList} | `SortedList` | `drag.clearSortingToReorder` (Into stays legal — a deposit needs no position) |
| the resolved destination is where the item already is | `NoOp` | `drag.alreadyThere` |
| degenerate geometry | `Unavailable` | `drag.cantMoveHere` |

The `NoOp` / `IntoDescendant` / `Self` / `Unavailable` arms of a MAPPED destination are **not** decided here at all:
`RootlistDropDecision.Refine` asks `RootlistOps.CheckMove(store.Rootlist(), source, target, placement)` — the real
marker stream, kind-2 end markers and all, the same index math `TryBuildMove` uses to build the op — and maps the
result through the ONE table:

| `RootlistMoveCheck` | `SidebarDropRefusal` | caption |
|---|---|---|
| `Ok` | `None` | — |
| `NoOp` | `NoOp` | `drag.alreadyThere` |
| `Cycle` | `IntoDescendant` | `drag.cantMoveIntoItself` |
| `SameItem` | `Self` | `drag.cantMoveHere` |
| `Missing` / `Invalid` | `Unavailable` | `drag.cantMoveHere` |

so "already there" and "into your own subtree" refuse **where the cue is drawn** instead of returning a silent `false`
three layers down. The flattened copy that used to answer this (`RootlistTreeMoves`) could not tell "after the folder"
from "after the folder's last child" — it had no end marker between them — which is exactly why a legal *Into* came
back as "Already there" (see
[wavee-playlist-mutations](../wavee/wavee-playlist-mutations/SKILL.md) §7).

#### The cue vocabulary

**Line ⟺ Before/After/EndOfList. Plate ⟺ Into. Never both, never neither-while-armed.** Pinned by
`SidebarDropCueTests`; `SidebarDropCue` owns the predicates (`DrawsLine`/`DrawsPlate` — `SidebarDropSlot`'s
properties delegate to them, so there is one rule) and the pure geometry.

- **Insertion line** — `SidebarPaneSlot.InsertionLine()`, mounted **once per row, always** (never conditionally) as a
  ZStack child beside the selection pill, `HitTestVisible = false`, every prop bound off the **LIVE slot index** —
  `int i = _scope.Index.Value; _ = _o.SubscribeRowEpoch(i); … _o.DropSlotFor(i)` — and the **live height**,
  `_o.RowExtentOf(i)` (the `SidebarSelectionPill`/`PillState` discipline; a conditional mount would need a re-render
  per pointer move).

  **The live index is not a style — it is the fix for the two-lines defect.** The line's key is the constant
  `"drop-line"`, so a recycled `ItemsView` slot pairs it by `(Key, type)` and **Updates** it while the entity row
  beside it (keyed by the row's key) genuinely remounts — and the reconciler wires a node's bound `Prop<T>` thunks at
  **mount only**. A thunk that captured `index`/`height` therefore kept answering for the row the slot was FIRST
  mounted with: after an auto-scrolled drag two carets were lit at once, and the slot holding index 0's stale binding
  could never draw "before the first row". Rule: every `Prop.Of(` in `InsertionLine`/`DropPlate`
  must read `_scope.Index.Value` (the source-scan test that pinned this was removed on 2026-08-22).

  2-DIP `Tok.AccentDefault`, corner 1, a 6-DIP terminal dot at the left cap (what
  makes a hairline read as an insertion caret rather than as a divider); it translates to
  **`SidebarRowGeometry.TreeContentX(depth)`** and `LineWidth = ContentWidth − TreeContentX(depth) − RowInsetRight`;
  `LineY` = 0 for Before/EndOfList, `h − LineThickness` for After. **The line's indent IS the resolved depth** — that
  is the whole depth cue.

  **`TreeContentX(depth)` is THE tree-content origin** (`SidebarRowGeometry`, engine-free): `IndentFor(0) 6 +
  SelGutterWidth 3 + depth·TreeGuideStep 12 + TreeChevronCell 16` → **25, 37, 49, 61, 73** (clamped at
  `MaxIndentDepth`). `SidebarEntityRow.TreeLeading` **consumes those same three constants**, so the row's art, the
  caret and `PickDepth` cannot disagree. It replaced `IndentFor(depth)` (6 + 12·d), which is the row's OUTER padding
  ladder and sits ~19 DIP to the left: the caret painted roughly one whole level off, and the depth-0 band needed
  x < 12 — unreachable in practice, so every outdent silently became "into the folder". On the real ladder the 1→0
  band is x < 31 (hysteresis 4 DIP), a deliberate slide the hand can make. Pinned by `SidebarRowGeometryTests`
  (`TreeContentX_*`, `TreeLeading_AndTheCaret_SpendTheSameConstants`) and `RootlistSlotResolverTests`
  (`DepthPick_TheOutdentBandIsReachable`).
- **Plate** — `SidebarPaneSlot.DropPlate()`: an always-mounted, `HitTestVisible = false` BoxEl placed **first** in the
  row's ZStack (`ZStack(plate, row, pill, line)`) — *under* the row, so text, artwork and the `|||` glyph are never
  tinted and the row's own hover/selected fills composite over it. Accent 0.18 α + a 1-DIP accent border, corner 4:
  the same pixels the row's `Fill` used to draw. Bound off the live slot exactly as the line is, gated on
  `SidebarDropCue.DrawsPlate` and nothing else.

  **`SidebarRowSpec.DropCue` is deleted and the row's `Fill`/`BorderColor` are STATIC values again.** They used to be
  `Prop.Of` thunks that folded the cue with `enabled && selected` — captured as values at the render that built them —
  and because bindings are mount-only, a same-keyed re-render (which is exactly what `RefreshSelection`'s epoch bump
  produces) left the old thunk in place: the resting **route** plate stayed on the previous route until the row
  scrolled out and remounted. Static fills also re-enter the 83 ms `BrushTransition` cross-fade for free. The boolean
  `SidebarRowSpec.DropActive` survives for the whole-row surfaces that are **not** recycled plan rows — the rail
  folder flyout's rows — and the rail tiles keep their own `SidebarRailItem` cue.
- **Captions** — Before/After **at the row's own depth say nothing** (the line already answers "where"); the two
  slots the line alone cannot disambiguate carry a sentence.

| slot | caption |
|---|---|
| `Into` folder | `drag.moveInto` "{name}" |
| `Into` playlist (the deposit) | `drag.addTo` "{name}" |
| `Before` / `After` at the row's own depth | *(none)* |
| `After` at a reduced depth | `drag.moveOutOf` "{parent}", or Your Library at top level |
| `EndOfList` | `drag.moveToEnd` |
| any refusal | the refusal table above |

- **Chip resting caption** — a rootlist payload gets `drag.organizeHint` ("Drop between playlists or onto a
  folder"); track-bearing non-rootlist payloads keep `drag.dragOntoPlaylist` (`WaveeResourceDrag.Chip`).
- **No scrim for rootlist payloads** — `spotlightWhen: s => Unwrap(s.Payload) is not { RootlistItem: true }` on
  both the row spec and the rail folder spec. An organisation drag must not dim the app it is happening inside; the
  detail page's insertion list took the same exemption for same-list drops first.

#### Multi-selection (the tree's, not the list's)

`Data/SidebarTreeSelection.cs` (pure, engine-free, source-included) owns it, **keyed by row id** and owned **per pane
instance** (the docked pane and the drawer are separate mounts). It ports `SelectionModel.OnInteractedAction`'s
EXTENDED arm rule for rule — Shift replaces with the anchor range, Ctrl toggles, a plain interaction clears-and-selects
only when the row was not already selected, every single-item op moves the anchor — but **not** its index addressing:
the tree re-flows under the user constantly, so an index selected one frame names a different playlist the next. The
visible order is an **argument** (`SidebarPane.TreeVisibleOrder`, rebuilt with every publish), never held state; `Prune`
runs with each publish. Driven directly by `SidebarTreeSelectionTests`.

| piece | where |
|---|---|
| the version signal + lane visibility | `SidebarPane.SelectionVersion` / `ChecksVisible` (a signal, because the lane's own visibility is a BOUND read on every realized row) |
| the ONE write | `SidebarPane.MutateSelection` — bumps the row epochs of the **union** of the before/after sets (not the symmetric difference: a row's drag payload is "the whole selection when I am in it", so a third row joining makes the first two's payloads stale), or every realized tree row when the LANE flips |
| gestures | `SidebarRowSpec.OnActivate(KeyModifiers)` replaces `OnClick` on tree rows; the row wires `OnPointerReleased` and applies `SelectorVisualsBound.MultiSelectMods` (a plain tap toggles while the lane is up; a **double** click always activates plainly). `OnEscape` clears. Enter activates, Space toggles — one `OnKeyDown` handler, beside F2 and Alt+↑/↓ |
| the lane | `SidebarRowSpec.CheckLane` = `SelectorVisualsBound.BoundCheckLane(...)`, rendered as the row's FIRST child; bound reads, so a selection change never re-renders a row. Rule 4 survives it — the lane changes WIDTH, never height |
| the plate | `SidebarRowSpec.MultiSelected` ⇒ `Tok.FillSubtleSecondary`. The accent `SelectedRest` plate and the pill stay **route-only** |
| entering / leaving | the row menu's **Select** (`sidebar.select`), absent once the lane is up; Escape, or clearing the last item |
| the menu inside a ≥2 selection | `NavExtras` swaps Move up/down/Move to folder… for **Move {n} to folder…** (`menu.moveManyToFolder`) → `RootlistFolderPicker.Open(acts, ids)`. Alt+↑/↓ stays single-row |
| the drag | `SidebarPane.TreeDragPayload` — a row **in** the selection lifts the whole normalised selection (`RootlistSelection.Normalize` → `WaveeResourceDragPayload.FromEntries`); a row **outside** it lifts just that row (the detail-page rule) |

#### The "+" create affordance

One component, four surfaces: `Shared/SidebarPinDropZone.cs` → `SidebarCreateButton(onPlaylist, menu, drop,
dropActive, revealOpacity, box, glyph)`. Section header (`SidebarPaneConfig.HeaderCreate`), folder row trailing slot,
Classic/V3 rail footers, V3 header. The flyout is `Overlay.Service` + `MenuFlyout.Create` anchored at the button (the
`SidebarPaneSortButton` shape), built at OPEN time; the drop cue is a bound accent plate; `BlocksDragArm = true` keeps
it from being a handle for dragging the row under it. The folder-row instance is revealed by `HoverOpacity` on mouse
hover **and** by a bound base `Opacity` during a drag — hover flags are not updated while a drag is live, so the two
owners are both needed. Its drop targets:

| surface | rootlist payload | track set |
|---|---|---|
| section header (`SidebarPane.HeaderCreateDropSpec`) | new folder at the top level, `FolderActions.NewFolderWith(acts, null, refs)`, caption `drag.newFolderFromThis` | new playlist, `CreatePlaylistFromDrag` (moved verbatim off the deleted create row), caption `drag.newPlaylistFromThis` |
| folder row (`SidebarPaneSlot.FolderCreateDropSpec`) | new SUB-folder, `NewFolderWith(acts, folderId, refs)`, caption `drag.newFolderInside` | transparent |

#### Slot → mutation (`RootlistSlotMapper.TryMap`, the ONE owner — called at hover to publish and at drop to commit)

Resolved against the **FULL projection tree** (`SidebarProjectionInput.PlaylistTree`, collapsed subtrees included),
never the visible plan: reading the plan is what made "first child" and the outdent's ancestor walk fail whenever the
row they needed was inside a collapsed folder — and a failed map still armed the cue. **No mapping ⇒ not armed.**

| slot | target `RootlistItemRef` | placement |
|---|---|---|
| `Before(i)` | entry *i* | `Before` |
| `Before(i)` with `Depth > entry.Depth` (an expanded header's bottom band) | the folder's **first child in the full tree** (an EMPTY folder maps to the folder itself) | `Before` (`Inside` for the empty case) |
| `After(i)`, `Depth == entry.Depth` | entry *i* | `After` |
| `After(i)`, `Depth < entry.Depth` | the ancestor folder `entry.Depth − Depth` levels up (walks `ParentFolderId` over the **full tree**) | `After` — the outdent, identical in shape to `FolderActions.MoveOut` |
| `Into` folder | the folder | `Inside` (append last) |
| `Into` editable playlist | — | `WaveeResourceDrop.DepositTracks` (`RootlistSlotTarget.Deposit` — not a rootlist move at all) |
| `EndOfList` | the **last top-level entry** | `After` — the marker range's exclusive end lands after a trailing folder's whole subtree |

Each mapped target also carries the two NAMES the surfaces need, and they are different questions:
`DestinationName` = the folder the item ENDS UP IN (empty ⇒ "Your Library") — the confirmation toast's subject, computed
from the mapped target and **never** from the hovered row's parent; `AnchorName` = the entry the placement is expressed
against — the chip's "Move out of {name}". Getting the first one from the hovered row is what produced "Moved to root
folder" for a move that left it.

`CommitDrop`'s order is load-bearing: the **same-owner `ReorderPayload` guard is hoisted to the top** (it used to
sit below the track-deposit arm, so a pin-band or V3 custom-order reorder passing over an editable playlist copied
that playlist's songs on the way past). It then consumes `_dropSlot.Peek()` — a refused or unarmed slot commits
**nothing**, and there is no fallback placement, because guessing is what made "after the last child" land inside
the folder.

#### The mid-drag freeze

`SidebarPane.TryPublishStage` parks the newest plan stage in a `SidebarStageHold<PlanStage>` (last-writer-wins)
while `WaveeResourceDrag.LiveRootlistDrag()` is true — a re-projection arriving mid-gesture (a dealer push from
another device, our own optimistic ack, a background revalidate) would re-key the rows the drag is aiming at. It is
the sidebar's copy of `Features/Detail/PlaylistReorderDefer`. `SidebarDragPeekWatcher` (the pane's one
`UseDragState()` owner) flushes it from a **layout effect on the active edge**, so drop, cancel and Escape each
flush exactly once; `DiscardDeferredStage` covers an unmount mid-gesture, and any publish empties the bay. Two
publish-through exemptions:

1. **A live disclosure** (`_activeDisclosureKey is not null`) — spring-loading a collapsed folder mid-drag exists
   precisely to reveal its children, so that expansion has to reach the plan.
2. **`_publishThroughFreeze`**, a one-shot latch set by the pane's own reorder `Commit` — that stage is this
   gesture's own result, not a foreign projection, and holding it would snap the dropped row home for the whole
   settle window. A session end retires the latch whether or not the commit it was set for produced a stage.

Non-rootlist drags are never frozen: they aim at a row's *identity*, not at its position.

#### Library V3

- **Sorted, non-custom** (`SidebarPaneConfig.TreeSortedNonCustom`, supplied only by V3 as `!CanReorderCustom()`):
  the `SortedNonCustom` fact refuses Before/After/EndOfList with `drag.clearSortingToReorder`; `Into` stays legal.
- **Custom order** (`SidebarPaneConfig.ClampReorderSlot` → `LibraryV3View.ClampToSiblingRun`): V3's local overlay
  cannot move an item between folders, so the reachable slot is snapped to the nearest one in the source's sibling
  run **during the gesture** — the gap never opens across a folder boundary, and `CommitPaneReorder`'s `SameParent`
  bail becomes the invariant behind it rather than the user's only feedback. The run is a *set*, not a span (a
  top-level drag travels past an expanded folder's children; a child drag stays boxed inside its folder).
  `SidebarReorderClamp.Offset(slot, from, to, extent)` reproduces the displacement hint for the clamped
  destination, because the engine's `ReorderList.OffsetFor` computes it from the target it holds internally, which
  the app cannot set.

#### The rail (56 DIP)

Folder tiles are real destinations (`SidebarPane.RailFolderDropSpec`): **Into and only Into** — a 56-DIP strip has
nothing above or below a tile to be "before" or "after" — with the same `RootlistDropDecision.Check` legality, the same
pre-move undo anchor and the same commit. Playlist tiles go **transparent** for a folder drag through
`SidebarRailDropRules.TileTransparent(isRootlistItem, canCopyTracks)`: a folder has nothing it could add to a
playlist, and the landed answer "Nothing to add" was an accusation aimed at a drag that was only passing through.
The rail's cue is keyed by uri (`_railDropUri`), not by plan index — a rail tile has no row in the expanded plan.

#### The non-mouse verbs (a drag must never be the only way)

Pure answers in `Features/Sidebar/Data/RootlistTreeNav.cs` (engine-free, source-included), over the same flattened
tree: `RootlistSiblingRun` (position/count + the two neighbours; `CanMoveUp`/`CanMoveDown`),
`SidebarTreeNavLayout.Decide` (which verbs the menu offers — **absent at the run's ends, never disabled**),
`RootlistFolderChoice` + `PickerDestinations` / `HasDestinations` / `TryTopLevelAnchor`, `TryEntry` / `TryFolder`,
and `RefOf` (the ONE entry → `RootlistItemRef` rule; `RootlistUndoAnchors` delegates to it). Siblings are the
entries sharing a `ParentFolderId`, **not** "the entries at the same depth" — that would fuse two folders' children
into one run.

- **Row menu** — `SidebarPaneSlot.NavExtras`' PlaylistTree branch (additive `layoutExtras`, so `MenuGrammarTests`
  stays green) adds **Move up** / **Move down** / **Move to folder…**. Mutually exclusive with the reorder-band arm
  by construction (`TreeMoves` returns empty for a `Reorderable`-wrapped row), so "Move up" never appears twice.
  **Move out of {parent}** is *not* added here — it already lives in the entity menu on every surface that shows
  the row.
- **Alt+↑ / Alt+↓** — `SidebarRowSpec.OnMove` on a focused tree row, next to F2 in `SidebarEntityRow`'s one
  `OnKeyDown` handler; exactly `KeyModifiers.Alt` (Alt+Shift belongs to whoever claims it next). Same sibling
  moves, same announce, never on a `Reorderable`-wrapped row.
- **`RootlistFolderPicker`** — hosted in a `ContentDialog`, not an anchored flyout: it opens from a context menu
  that is already gone by invoke time, so there is no anchor node left to place against (the same reason
  `Menus.OpenPicker` hosts the playlist picker that way). It reuses the playlist picker's shell + search field.
  **Top level** is pinned first and is never filtered away by the search box (it is the anchor row, and hiding it
  would strand a user who typed a folder name and changed their mind); folders follow in tree order, indented by
  depth. The source's own subtree is excluded because the list is built from `RootlistDropDecision.Check` over the
  marker stream (`RootlistTreeNav` takes the stream alongside the tree: STRUCTURE from the tree, LEGALITY from the
  stream) — the picker cannot offer a destination a drag would refuse, or hide one it would accept. The destination list is a **snapshot taken at open** (props
  freeze at mount, and the projection is not a signal the panel can subscribe to); the **commit re-reads the live
  tree**, so a mid-flight desktop rootlist change resolves to nothing rather than to the wrong folder. Top level
  commits against `TryTopLevelAnchor` (last top-level entry, `After`), a folder against `(folderId, Inside)`.

#### One commit chokepoint

Every path — drop, rail drop, menu verb, Alt+arrow, picker — ends at **`WaveeResourceDrop.MoveRootlist` →
`LibraryBridge.MoveRootlistItemsAsync`**; the four non-mouse verbs funnel through **`FolderActions.Commit`**, which
resolves `(target, placement)` from `RootlistTreeNav` and captures the undo anchor before handing over. That is why
every move awaits, maps a failure by **verb** (`PlaylistEditVerb.Reorder` — never raw `ex.Message`), announces
through `Announcer`, and shows a `drag.movedTo` / `drag.movedToLibrary` / `drag.movedManyTo` success toast whose
**Undo** is the inverse move. One drop issues exactly ONE `MoveRootlistItemsAsync`, whatever the selection size.

The undo anchors are captured **before** the mutation (`RootlistUndoAnchors.TryResolveMany` — a batch resolves all
or none, because a partial Undo would scatter the rest of the selection; `TryResolve` is its N=1 sugar), because once the rootlist
has moved, where the item used to be is unknowable): the previous sibling (`After`), else the next sibling
(`Before`), else the parent folder (`Inside`) — and `false` when the item is the tree's only top-level member, in
which case the toast appears **without** an Undo rather than with one that would land somewhere else. The inverse
rides the very same `MoveRootlistItemsAsync` seam, so there is no second mutation path to keep in sync.

#### Deferred: the engine-level insertion path

The right end state is the engine's own tree insertion — `InsertionOptions` with a depth channel and a real
**displacement gap** — which would delete the row-owned line entirely. It is **blocked on `ItemsView`'s whole-list
insertion hit region**: insertion is armed for the list, not per row, so it cannot express "this row's bottom band,
at depth *d*". The resolver is deliberately shaped to survive that migration unchanged — it consumes
*(row facts, t, xInRow)* and emits a slot, and neither mentions how the cue is drawn.

---

## 6. The three modes

### Classic — a locked built-in document

`SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen)` returns
`SidebarCustomLayout` with `TemplateId = ClassicId = "classic.builtin"` (deliberately *not* in
`SidebarTemplates.All`) and **stable string section ids** — `classic.pinned`, `classic.library`,
`classic.playlists`, `classic.tools` + three dividers. Stable ids matter: the pane keys its reorder bands,
collapse routing and section identity off them, and `ClassicSectionOf(sectionId)` maps a header click back to the
right preference flag with no lookup table.

The section list is today's Classic IA verbatim: Pinned · rule · Your Library (albums/artists/liked/podcasts/local
with quiet counts) · rule · Playlists (artwork + song-count subtitle; its create affordance is the header "+",
`HeaderCreate = true`) · rule · the header-less
DevTools entry (`api-console`). The Display options are chosen so the ONE shared ladder reproduces Classic's
44-DIP rows exactly:

- Pinned / Playlists → `Entities` (Cozy + Subtitles) ⇒ 44 with 32-DIP artwork.
- Your Library / DevTools → `Shortcuts with { Density = Comfortable }` (no subtitle) ⇒ 44, matching what the
  retired `LibRow`/`DevToolsRow` hard-coded. `Artwork: false` keeps them 16-DIP glyph rows.

Classic's collapse state is **not** document state: three persisted preference flags, so the docked pane and the
drawer agree and the state survives a design round-trip. `WaveeSidebar.SectionEpoch()` folds them into
`ModeEpoch`.

The preservation contract (§3.1.1) is honoured at the **pixel** level, not the implementation level — the user's
explicit choice. Deliberate deviations, stated not hidden: selection is the per-row indicator; the pinned list is
virtualized and therefore uncapped (no "Show all (n)" row); pinned reorder uses the displacement channel rather
than a live projection; the rail order changed (pins → shortcuts → playlists ≤20 → API console → create → menu);
pane padding is fixed chrome (the top 8 DIP no longer scrolls away); and the PlaylistTree create affordance is the
section header's **"+"** plus the rail's, not a trailing row. (That inverts the earlier landing: the `CreateAction`
ROW is **deleted** — `SidebarRowKind.CreateAction`, `SidebarProjectionInput.SuppressTreeCreateRow` and
`SidebarPaneSlot.CreateRow` are all gone. A permanently mounted row at the end of a 10k-playlist tree cost the
section's rhythm, and it squatted the "top level, at the end" drop slot that belongs to the tree's own `TreeEnd`
gutter. Both create verbs moved to the header "+"'s flyout, which is also a drop destination.)

### Library V3 — a synthesized ephemeral document under its chrome

`LibraryV3Document.Build(in LibraryV3DocState)` is **pure and unit-tested**. `TemplateId = "v3.synth"`. Sections,
in order:

1. `"v3.pins"` / `Pinned` — only when `PinsBandVisible` (`HasPins && !Drilled && !Searching`).
2. `"v3.liked"` / `StaticLinks` — only when `LikedVisible`; one `Route` item at `liked` with icon `Heart`.
3. `"v3.library"` / `PlaylistTree` **or** `EntityList` — always. `PlaylistTree` when `FoldersApply` (list view,
   not searching, not drilled, All-or-Playlists filter); `EntityList` otherwise. `EmptyBehavior = HideBody`.

`LibraryV3DocState(Filter, Qualifier, Sort, Descending, View, GridColumns, Searching, DrillFolderId, HasPins,
LikedPinned, QualifiersAvailable)` maps onto `SidebarEntityQuery` (`KindsFor`, `SortFor` — Custom degrades to
Alphabetical outside the Playlists lens, `QualifierFor`) and `SidebarDisplayOptions`
(`PresentationFor`/`DensityFor`/`SubtitlesFor`/`ClampColumns` 2…4). `GridColumns` is *derived* from the measured
pane width, never chosen.

Its `SidebarPaneConfig`: `ScrollKeyPrefix = "sidebar.v3"`, `ReadOnly = true`, `SearchHead = false`,
`ShowLayoutMenu = false` (V3 embeds those rows in its own overflow menu), `Head = ChromeHead`,
`SetSectionCollapsed = null`, `ActivateFolder = session.ActivateFolder`, plus `IsReorderableSection` /
`CommitReorder` for the local custom order and a `RailFooter`. `OnCustomize` is never set — its document is
ephemeral, so there is nothing to customize.

`ShapeInput` sets `Pins`, clears `ExpandedFolders`, sets `SuppressTreeCreateRow = true`, and swaps
`session.View.Rows` into either `PlaylistTree` (grouped) or `Library` (flat) — **two windows over the one
published projection, no logic fork**.

`LibraryV3Session` owns the drill stack (`PushFolder`/`PopFolder`/`ResetDrill`, `DrillVersion`, `NarrowFolders`,
derived `Columns`), `ReadState()`, and the shared commands (`ActivateFolder`, `CreatePlaylist`, `ClearAllFilters`,
`Retry`, `Collapse`, `Expand`). `LibraryV3View` owns the **flat-sort → tree re-grouping** pass (a flat comparator
can put a nested playlist above its own folder), the drill slice, `SameParent`/`KeyAt`, and
`MaterializeOrder` for the local custom order. `LibraryV3Chrome` mounts the header band, toolbar, chip rails,
breadcrumb, retry banner and the actionable empty states through `Config.Head`.

Deliberate V3 losses in the unification (an R3.0 trade): the trailing pin marker, the folder-name caption on
search hits, the pin-band hairline, the grid pin badge, Alt+Shift reorder (the pane's keyboard lift replaces it),
per-view scroll keys (now one), the partial-load shimmer tail. The pin band dissolves while searching or drilled,
and V3 has no empty-pins drop card.

### Wavee Curated — the user's document + the customizer

`CuratedSidebar` is a thin shell: `Document = () => _prefs?.Layout ?? fallback` (prepending the materialised Shortcuts
section for the render path), `SetSectionCollapsed = (id, c) => _prefs?.Dispatch(new SetSectionCollapsed(id, c))`,
`ReadOnly = false`, `SearchHead = true`, `Edit = ReadEditSession`, `OnCustomize`, `OnCreatePlaylist`.

**The docked pane IS the editing canvas** (Decision B). There is no preview column and no outline, because the sidebar
beside the page is already rendering the exact document being edited — `SidebarLayoutMenu.Rows` switches to Curated
before navigating, which is what made the outline↔preview duplication removable at all. Two surfaces:

| Surface | Owns |
|---|---|
| the canvas — the live pane under `SidebarPaneConfig.Edit` | uniform `SectionCard` rows (grip · kind glyph · title · count · eye · "…"), ONE section expanded at a time, hidden sections dimmed-but-present, section reorder committing `MoveSection`, per-section options as a POPOVER anchored to the card's "…" (the generated control set from `Curated/SidebarPropertyPanel.cs`, re-hosted — not rewritten). Structural drag is armed ONLY here. |
| the companion page (`sidebar-customize`) | ONE scrolling column at every width: header (Back · title · Undo · Redo · Reset · Done) · design presets + the five templates · the PERSISTENT palette · hidden-section recovery · Advanced (reset + the `sidebar-layout.json` escape hatch). |

Registered in `Features/Shell/ContentHost.cs` and labelled in `Features/Shell/ShellNav.cs`.

**The four-tier region ladder is GONE.** `SidebarCustomizerTier`, `SidebarCustomizerLayout` (the 1320/1000/820
thresholds + `HysteresisDip`), the command-fit table and `SidebarOutlineRows`/`SidebarOutlineDrag` all died with the
surfaces they described — do not re-add them. What `Curated/SidebarCustomizerLayout.cs` still owns (pure,
source-included by the tests): the palette table + filter (`SidebarPalette`, now including the **Destinations** group —
`SidebarPinId.PinnableRoutes` ∪ `{settings, api-console, concerts}`, labelled through `ShellNav.Dest`, rendered
FIRST), `SidebarDisplayValues`, `SidebarQueryPanelShape`, `SidebarNumberEdit` and the opaque-config rewriter
(`SidebarConfigJson`).

The one section-drag translation that survives is **`SidebarEditPlan.ToMoveSection`** (with its sibling
`ToAddSection` for palette drops), in `Features/Sidebar/Data/SidebarEditPlan.cs` alongside `ShowsBody`,
`SectionsReorderable`, `Fold`, `CardCount`, `IsPinnedCard` and `SectionDragKind`. Its trap, spelled out because it is
the one thing that breaks silently: band slots enumerate the plan's `SectionCard` rows — built over the **render**
document, which carries the Shortcuts head at index 0 — while `MoveSection.NewIndex` / `AddSection.Index` index the
**persisted** document, which does not. Always hand those two methods `SidebarPreferences.Layout`, never the document
the pane planned from.

Extension sections' property controls are still **generated** from `ISidebarDataSource.ConfigSchema` and written back
through `SetExtensionConfig`; only their host changed.

---

## 7. The extension-ready layer

Registered contributions are the whole point of M1: **first-party is literally the trusted extension `"wavee"`.**
There is no privileged non-extension registration path.

### The registry

`Actions/Extensibility/WaveeExtensionRegistry.cs` — one registry for every contribution kind
(`WaveeRegistryTable<WaveeActionDescriptor>` + `WaveeRegistryTable<ISidebarDataSource>`).

- `static Context<WaveeExtensionRegistry?> Slot` · `static Current` · `static Build(ActionServices)`
- `Register(extensionId, IWaveeExtension)` / `Register(extensionId, Action<IWaveeExtensionRegistrar>)`
- `RegisterAction` / `RegisterDataSource` (both null-tolerant → a diagnostic, never an NRE)
- `Actions` / `Sources` / `Extensions` / `Diagnostics`
- `TryGetAction(key)` / `TryGetAction(binding)` / `TryGetSource(id)` / `HasAction` / `HasSource` / `KeyOf(binding)`
- `Resolve(services, binding)` → `WaveeActionTargetResolution` · `Execute(services, in binding)` →
  `WaveeActionUnavailable`

**Duplicate policy: first wins.** A second registration under a live key is refused and recorded in
`Diagnostics`; because `BuiltInExtensionTable` runs first, no third party can shadow a first-party contribution.
Nothing is ever *unregistered* — a disabled extension is filtered at the consumption site, and an unresolvable key
yields `WaveeActionUnavailable.ActionMissing` (a visible-but-disabled row with a reason).

**Threading:** UI thread only, unsynchronized, registration-then-read. No lock, no off-thread producer.

### The SDK seam

```csharp
public interface IWaveeExtension { void Register(IWaveeExtensionRegistrar registrar); }

public interface IWaveeExtensionRegistrar
{
    void RegisterAction(WaveeActionDescriptor descriptor);
    void RegisterDataSource(ISidebarDataSource source);
}
```

`Actions/Extensibility/BuiltInExtensionTable.cs` is the hand-written `RegisterAll(registrar, services)` — the M4
source generator emits this same call shape, so nothing needs rework. `ExtensionId =
WaveeExtensionKey.FirstPartyPublisher = "wavee"`. The 13 registered action keys:

`wavee.play` · `wavee.playNext` · `wavee.addToQueue` · `wavee.toggleLike` · `wavee.save` · `wavee.open` ·
`wavee.goToAlbum` · `wavee.goToArtist` · `wavee.copyLink` · `wavee.songRadio` · `wavee.artistRadio` ·
`wavee.pinToSidebar` · `wavee.unpinFromSidebar`

### `WaveeActionDescriptor` vs `AppAction` — different shapes, neither replaces the other

`AppAction` is the **context-menu** model: it acts on a live `ActionTarget` built at menu-open time, and its label
is count-aware. `WaveeActionDescriptor` is the **bound** model: it acts on a persisted `SidebarActionBinding`
whose target is a *mode plus a key*, so it must resolve the target itself, must be able to say **why** it cannot,
and must survive a restart. First-party descriptors therefore *wrap* the existing `ActionId` verbs (recorded in
`LegacyId`, diagnostics only); the `ActionId` enum stays internal and never appears on the wire.

Members: `Key`, `LabelLocKey`, `IconKey` (resolved through `ActionIcons.Resolve` — never a raw glyph),
`AcceptedTargets` (`WaveeActionTargetModes`), `ArgumentSchema` (opaque JSON), `IsEnabled`, `IsChecked`,
`Destructive`, `RequiresConfirmation` + `Confirm{Title,Body,Primary}LocKey`, `RequiredPermissions`
(`WaveePermissions.*` — recorded but **unenforced until M3**), `LegacyId`, `Run(services, binding, resolution)`.
`Resolve(services, binding, peek)` folds *every* reason a row can be disabled into one call, so the row's disabled
state and `Execute`'s refusal can never disagree. `RequiresConfirmation` routes through `SettingsShared.Confirm`
and **refuses** to run when there is no overlay — a null overlay never degrades into an unconfirmed run.

`WaveeActionUnavailable`: `None`, `ModeNotSupported`, `MissingTargetKey`, `NoNowPlaying`, `NoActiveRoute`,
`ActionMissing`, `HostUnavailable`, `NotApplicable`.

### Data sources

`ISidebarDataSource` declares its **capabilities** (`ItemType`, `SupportedFilters`, `SupportedSorts`, `Paging`)
so the customizer offers only facets the source honours rather than offering some and silently ignoring them; its
`ConfigSchema` is what the section's options popover generates controls from; and its `State`/`StateDetailLocKey`/`NeedsPrompt`
are the health the planner turns into rows. `Fill(into, in request)` appends on the rebuild path — no LINQ, no
closures, no per-row allocation, never a blocking wait.

Health is a plain property + a plain `Changed` event, **not** a `Signal<T>`, because `Data/` must stay engine-free
for the tests. `SidebarDataSourceBase` gives you `SetHealth` (publish + notify) and `SetHealthQuiet` (the only
setter a `Fill` may use — raising `Changed` from inside a rebuild would re-enter the binder).

Degraded-state → row mapping (the platform doc's failure matrix, collapsed into the planner):

| Availability / state | Planner emits |
|---|---|
| `Missing` / `Disabled` / `Incompatible` | one `PromptRow` ("Manage extension"), section keeps its spec |
| `NeedsPrompt` (e.g. Concerts with no location) | one `PromptRow` |
| `Pending` + 0 rows | `Skeleton` rows |
| `Ready` + 0 rows | `Empty` / `CompactHint` per `EmptyBehaviorFor` |
| `Cached` | last-good slice replayed (the M3 stale-badge seam) |

### Two key spaces — do not unify them

| Form | Example | Owner |
|---|---|---|
| **Contribution key** (slash) | `"wavee/artist.topTracks"` | `SidebarExtensionRef.ContributionKey` — the registry lookup key composed from the ref |
| **Source id** (dot) | `"wavee.artist.topTracks"` | `ISidebarDataSource.Id`, composed/split only by `SidebarContributions.SourceId` / `ContributionOf` |
| **Action key** (dot) | `"wavee.play"` | `SidebarActionBinding.ActionKey` = `ProviderId + "." + ActionId`; `WaveeExtensionKey.Compose` |

Both `SourceId` and `Compose` are **idempotent**: an already-fully-qualified id is taken as-is rather than
double-prefixed, so a hand-edited or older document that stored `"wavee.library"` in the contribution slot still
resolves.

`SidebarContributions.WaveeExtensionId` and `WaveeExtensionKey.FirstPartyPublisher` are the same literal `"wavee"`
declared twice. The code's stated reason (`SidebarDataSource.cs:295-300`) is the test build's source-include
direction: `Data/` may not depend on `Actions/Extensibility/`. The single-owner fix it names is to make
`FirstPartyPublisher` alias `WaveeExtensionId` — not the reverse. (Three files from `Actions/Extensibility/` are in
fact source-included today, so the constraint is looser than the comment implies; the fix direction still stands.)

### Budgets

| Budget | Value | Enforced by |
|---|---|---|
| per-section extension config | 64 KiB (`SidebarExtensionRef.MaxConfigBytes`) | the reducer (`ConfigTooLarge`), re-checked at save |
| whole document | 2 MiB (`SidebarLayoutStore.MaxDocumentBytes`) | the write path (a save **fault**, never a truncation) |
| registry key length | 128 (`WaveeExtensionKey.MaxLength`) | `IsValid` — a key is persisted, so unbounded is a document-size hazard |
| uris per include/exclude set | 500 (`MaxUrisPerSet`) | the reducer — **truncate**, not reject |
| sections per document | 40 (`SidebarLayoutReducer.MaxSections`, top level + children) | the reducer (`SectionCapReached`) |
| items per section | 500 (`MaxItemsPerSection`); `EntityEmbed` = 1 | `SidebarSectionKinds.ItemCapacity` |
| rail tiles | 40 (`SidebarRowPlanner.RailTileCap`); tree tiles capped at `RailTreeTiles = 20` per pane | the planner |
| recency feeds | 40 (`SidebarProjectionBinder.RecencyCap`) | the binder |
| undo ring | 50 steps, in memory only | `SidebarUndo` |

### Forward-compat guardrails (so M3–M5 bolt on without rework)

1. Registry interfaces are already the SDK's shape; a sandboxed extension never implements `IWaveeExtension`
   in-process — its manifest contributions are **replayed onto the same registrar** by the host.
2. No `AppActions.All` lookups. Section rendering resolves contribution ids through the host; the planner never
   sees an extension id and never switches on one.
3. Unknown refs / configs / kinds / members round-trip untouched.
4. The binder exposes a per-contribution cached-snapshot seam and per-source health — both already surfaced as
   planner states.
5. Runtime extension state and secrets never enter the layout document.
6. Permissions are declared honestly today and enforced in M3, so the table needs no re-authoring.

**What is NOT built** (and must not be claimed): M3's sandboxed extension host / worker isolation, M4's public SDK
+ the source generator that would emit `BuiltInExtensionTable`, M5's hardening. There is no extensions page —
"Manage extension" navigates to the customizer. `RequiredPermissions` is inert. `ArgumentSchema` is opaque. Nothing
untrusted executes in Wavee, and `docs/plans/wavee/wavee-sidebar-extension-platform.md`'s `## Boundaries` makes
built-in visual completion the gate on further extension-host work.
