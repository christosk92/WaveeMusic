# Sidebar rework: implementation plan

Status: implementation plan, 2026-10-08. Approved design: `sidebar-rework-design.md` (owner decisions of 2026-10-08,
P.2a supersedes the P.2 Library head). Visual reference: `sidebar-preview.html`, option **E** ("Pages + toolbar").
Branch `feat/sidebar-rework` in both repos (`C:\WAVEE\merge\fluent-gpu`, `C:\WAVEE\merge\WaveeMusic`).

This file lives in `docs/plans/wavee/` (not `docs/plans/`) because the app's `CLAUDE.md` puts every
`*-implementation.md` plan there.

**How to read it.** Every phase lists its work packages (WPs) at the end. A WP owns an exact, disjoint set of files and
names the sections of this plan it implements; its "Depends on" column lists the WPs of the same phase whose types or
members it uses (run it after them). Every new `.cs` file states its using header: the app has no global usings beyond
ImplicitUsings (`System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Net.Http`, `System.Threading`,
`System.Threading.Tasks`), so every `FluentGpu.*` namespace is spelled out. Code blocks are the code: transcribe them, keep the surrounding file's
comment density and idiom (the `///` summaries, the `// ──` banners, `Peek` vs `Value`, no LINQ in render paths).
Where a block says *"replace X with"*, delete X entirely. Where it says *"edit"*, the listed member changes and nothing
else does. WP coders never build, never run tests, never `git stash`, never touch `src\apps\Wavee.PlayPlay`; the
orchestrator builds Debug + Release, runs `Wavee.Tests` Debug + Release and, for engine phases, VerticalSlice + engine
tests, after every phase.

---

## 0. Decisions this plan implements

### 0.1 The owner's answers (binding)

| Q | Resolution in this plan |
|---|---|
| Q1a Liked as a server pin | **Filter `liked` and `home` out locally; never write them to `ylpin`.** `PinSyncRules.TryWireUri("liked")` returns null, `TryPinId("spotify:collection")` returns null, the store refuses fixed-route ids, the migration drops them from the v2 pins list (§P3.9). |
| Q1b Account swap | **Both.** `LibraryPinSync.ApplyServer` (and its local write path) refuses unless the loaded account key equals the live scope's key; `Spotify.Library.AfterPublish`'s scope-change branch calls `Sidebar.EnsureAccount` synchronously before any pin work and drops the old account's pending pin-write marks only when the account key really changed (`PinWriteMarks.Rebind`; a market/locale/tier switch keeps them) (§P3.8). |
| Q2 Library order | Fixed in Library: Home, page dropdown, chips, toolbar, pins, list (P.2a). Classic stays movable. The search filters pins too (a route pin follows the text with no chip; any chip hides it, §P3.4). |
| Q3 Pins in Edit | Pins are rows in the Outline (an inner band: reorder, Unpin, keyboard moves); a "Show all" fold above 50 (§P4.5). The inner pins band exists in BOTH layouts; only the SECTION band is absent in Library (§P4.5, §P5.7). Every pin reorder (band, keyboard, menu, Outline) is recorded in the ring (`DefaultReorderCommit` → `MovePinRecorded`, §P4.4). |
| Q4 Pin while Pinned hidden | Pinning shows Pinned again with the toast "Pinned is shown again · Undo"; the pin and the show are ONE `Batch` ring entry, so that Undo unpins and re-hides (§P4.3, §P4.4). |
| Q5 Last Collections item | Hiding the last item hides the section and keeps `hiddenItems`; restoring drops only the section's Hidden bit (§P3.2). |
| Q6 Undo outside Edit | Every undo raises "Undid: … · Redo" and every redo "Redid: … · Undo"; both are registered sidebar toasts, so outside Edit Ctrl+Z/Ctrl+Y act while one is open and a ring clear closes them (§P4.3, §P4.4); the edit bar exposes the whole ring. The keys are taken by the sidebar's own key handlers (focus in the pane, the head or the edit bar, design V.11), never by a frame-global chord (§P4.6). |
| Q7 Global actions in Edit | The Outline replaces the footer too (one Settings row); a layout switch from Settings/palette and "Reset everything" (pane menu, Settings item) are disabled with "Finish editing the sidebar first" — Settings shows it as the item subtitle and under the layout cards, the palette toasts it; Density stays live (§P4.4, §P4.7a). |
| Q8 Outline header click | The checkbox is the only hide path; header click focuses; Enter toggles Show; Space lifts; the `Reorderable.Item` wrapper is the focus target (§P4.5). |
| Q9 Unavailable pins | Three states: pending (last-known name + kind glyph), unavailable only after an authoritative miss, offline neutral (`SidebarPinStateRules`, §P3.4). A pending pin with no name anywhere shows the kind's noun in `TextTertiary` (`FallbackTitleKey`, §P3.12): never a blank row (D9). |
| Q10 Phase cut | Design Phases 3 and 4 are **one phase here (P3)**. |
| Q11 Library tab order | Matches the screen: Home, dropdown, chips, toolbar (search, sort, List, Grid, +, ⋯), list. One pure rule, `SidebarLibraryHeadRules.TabOrder` (§P5.1), which `LibraryToolbar` walks to BUILD its controls (§P5.5), pinned by `TabOrder_MatchesScreen` (§P5.10). |
| Q12 Hidden kinds | Hiding a kind (Filters) removes its chip, its dropdown entry and its items from the unfiltered list — with or without a search: `SidebarBinderPipeline.Shape` compacts for hidden kinds and for the Podcasts/Audiobooks split, not only while searching (§P3.6.2, §P5.2). |
| Q13 Collections in Library | No Collections section in Library; the page dropdown reaches the pages (P.2a). |
| Q14 Pin reorder toasts | None (the ring covers moves). |
| Q15 Naming | "Show section ▸" (lists Settings and Pinned too; every section id has its own `sidebar.section.title.*` key, Settings and Home included, pinned by a loc-key fact, §P4.2, §P4.9, §P4.10); a persistent "What changed" note in Settings › Sidebar after the migration (§P3.10, §P3.13). |
| Q16 Forced-compact Edit | Non-dismissing scrim at 0.2 behind the pinned overlay (§P4.5). |
| Q17 Bulk unpin | "Unpin all shortcuts" in the Pinned header menu (Classic), in the pane ⋯ menu and the Outline's Pinned block (Library, which has no Pinned header), as ONE batch entry with one toast (§P4.2, §P4.4, §P4.5). Library's pane ⋯ also carries "Hide pinned", disabled with "Unpin {names} first" while shortcuts lock it. |
| Q18 Density default | Default (two-line, 32 art). |

### 0.2 The four blocking feasibility items, resolved

1. **Cross-account pin write (Q1b).** §P3.8: `SidebarPinLatch.Matches` gates `ApplyServer` and `OnLocalPinChanged`; the
   swap runs from `AfterPublish`'s `s_seenScope` branch and from a UI `ScopeEpoch` effect (idempotent, same-key no-op);
   `PinWriteMarks.Rebind(Sidebar.AccountKey)` clears the in-flight marks on a real account change only; the old account file is serialized before the store is
   reloaded, so nothing of B ever lands in A's file. Facts in `SidebarAccountStoreTests` and `SidebarPinSyncTests`.
2. **Liked server pin (Q1a).** §P3.9: `SidebarPinRules.IsFixedRoute` (`home`, `liked`) is refused by the store
   (`Pin`, `Insert`, `LoadFrom`, `ApplyRemote`), by `PinSyncRules` both ways and by the menus (`PinRowRule`).
3. **Binder rewrite + recent/newReleases demand + projection tests.** §P3.6 gives the binder's new field list, the new
   `Rebuild`, `PublishInput`, `Read` and the demand model (`SidebarFeedDemand`): *Recently played* is resolved
   synchronously from the play log by `SidebarRecentsRules` (index → resident peek → logged title → skip); *New releases*
   reads the existing What's New feed (`Notify.Items`, filled by `Home.Feeds.Refresh`) and kicks
   `Notify.RefreshFeeds` only while the section is shown, at most every 30 minutes (`SidebarNewReleasesRules`).
   `ISidebarDataSource`, the table, the contribution host, the slices and every source class are deleted.
   `SidebarProjectionTests` keeps its projection/shape facts and loses exactly the facts listed in §P3.15.
4. **Persistence gap (Q10).** Merged: P3 introduces the new model, the v3 device file, the account files and the
   one-time migration in the same phase, so there is no interim "new model on the v2 store" state. `sidebar-layout.json`
   is read once by `SidebarMigrationV2` and never written again.

### 0.3 What the design said that the code corrected

| Design text | This plan | Why |
|---|---|---|
| P.3 / Phase 6 put `SidebarRecentsRules` in the Library phase | **P3** | the binder rewrite needs it: it IS the `recent` demand model |
| A.4 `sidebar.library.view` key | `view`, `sort`, `descending`, `showLiked` live on the `library` section state; only `sidebar.library.filter` is a key | C.5 records sort/view as undoable layout ops; a settings key is not in the ring |
| Library hidden kinds on a `collections` section | on the `library` section's `hiddenItems` (`albums`,`artists`,`podcasts`,`audiobooks`) | P.2a: Library has no Collections section |
| The account file has no first-seen stamps | `firstSeen` added to `sidebar.acct-*.json` | the playlist "recently added" proxy (`SidebarFirstSeen`) lived in the v2 file and is per account |
| `--fake` key `fake:` | `fake:wavee-listener` | `Platform.ResolveScope` |
| Head ↔ list arrow handoff "ItemsView owns its roving stop" | P0 adds `ItemsViewController.FocusItem` and `ListOptions.OnEdgeNavigate` | there is no public focus API today |
| Audiobooks chip | `SidebarLibraryEntry.IsAudiobook` stamped from `LibraryAudiobookFilter.IsAudiobookRow` (P3) | audiobooks are `Show` rows split by flags (`User.Page.Library.cs`) |
| New releases "fetch seam" `Sidebar.NewReleasesFetch` | deleted; reads `Notify.Items` | the seam was never installed anywhere; the What's New feed already exists |
| The Outline's chip resolver on `Drag.ReorderChip` | `Drag.ExtraChip` | `ReorderChip` belongs to the Home customizer; `ExtraChip` is the slot the deleted sidebar customizer freed |
| Library sort/view/filter as V3 int keys through P4 | a P3-P4 bridge (`V3*` mirror signals, never persisted) until P5 replaces the V3 chrome | keeps the V3 chrome compiling for two phases without re-plumbing 72 call sites twice |
| Design Phase 3 "rewrite the planner" + Phase 2 "compact arm" | P2 adds the compact arm to today's planner, P3 rewrites the planner with it | the rail must stay usable between phases |

---

## 1. Coexistence with `feat/smooth-reveal`

`feat/smooth-reveal` (worktree `C:\WAVEE\perf\baseline`) replaces the disclosure animation. This plan keeps the
disclosure call path **untouched and isolated**:

- `PaneView.StartDisclosure`, `DisclosureSettled`, `PendingExpandRange`, `OnExpandStarted`, `OnExpandSettled`,
  `BumpDisclosureEpochs`, `TrySectionBodyRange`, `TryFolderDescendantRange`, `DisclosureOpen`, the `_activeDisclosure*` /
  `_pendingExpand*` / `_queuedDisclosure` fields and the `Disclosure = new DisclosureOptions { … }` block of
  `PlanList()` are **not edited** by any WP. `ToggleSection` and `ActivateFolder` keep calling `StartDisclosure` with the
  same arguments.
- The `Chevron` class in `Sidebar.UI.Rows.cs` (`Chevron.Section` / `Chevron.Disclosure`) is **not edited**.
- `SidebarRowGeometry.TrySectionBodyRange` / `TryFolderDescendantRange` / `FolderHeaderIndexOf` keep their signatures
  and bodies (they move file in P3 with the rest of the geometry, unchanged).

Files both branches touch (checked against `C:\WAVEE\perf\baseline`, `git diff --name-only origin/main...HEAD` plus its
working tree, 2026-10-08). Expect textual conflicts; resolve mechanically by keeping their disclosure/reveal hunks and our
everything-else hunks:

| File | smooth-reveal hunks | this plan | mechanical resolution |
|---|---|---|---|
| `WaveeMusic\src\apps\Wavee\Shell\Sidebar.UI.cs` | PaneView fields (~477), `Render` (~612), `PlanList` options (~746), `PublishStage` (~891), the disclosure block (~1120-1281), `ToggleSection`/`ActivateFolder` (~2061, ~2166) | P1 `PaneMetrics`, `PlanList` options (adds `IsItemEnabled`/`ItemText`/`KeepAlive`/`OnEdgeNavigate` next to `Disclosure`), P2 `Render` (single layer), P3 `RebuildIndex`/`BuildStage`/`PlanDep`, P4 menus + edit swap, P5 `FocusListItem`, pill lanes | keep both; inside `PlanList` keep their `Disclosure = …` block verbatim and our added option lines beside it |
| `WaveeMusic\src\apps\Wavee\Shell\Sidebar.UI.Rows.cs` | `Chevron` (~627-693: `MotionTokenId.Reveal`, the `accent` flag) | P1 `EntityRow`, `SectionHeader`, `SelectionPill`; P5 `SelectionPill` lane — never `Chevron` | take theirs for the whole `Chevron` class, ours for everything else |
| `WaveeMusic\src\apps\Wavee\Shell\Sidebar.Disclosures.cs` (NEW on their branch) | the disclosure bookkeeping (`SidebarDisclosures`) | not touched; P6's guide and skill name it as the disclosure owner | none (no hunk of ours); if this plan lands first, P6's references to it are added by their rebase instead |
| `WaveeMusic\CHANGELOG.md` | a `## [0.3.5] - unreleased` heading with a `### Changed` "Smooth expand and collapse" bullet | P6-WP6 adds a `### Changed` bullet and a `### Removed` bullet under the current unreleased version | keep ONE `## [0.3.5] - unreleased` heading and ONE `### Changed` list holding both bullets (theirs first), then our `### Removed` |
| `WaveeMusic\.claude\skills\wavee-sidebar\architecture.md` | 5 lines (the disclosure paragraph) | P6 rewrite | P6 keeps their disclosure paragraph verbatim inside the rewritten file |
| `fluent-gpu\src\FluentGpu.Engine\Animation\MotionTok.cs` | deletes `DisclosureExpand/Collapse/Chevron` (enum, `Get` arms, properties); appends `Reveal` at the END of `MotionTokenId` (+ `RevealResponseSec`, its `Get` arm and property) | P0-WP2 appends `PaneOpen, PaneClose` at the END of `MotionTokenId` (+ two `Get` arms and two properties) | CERTAIN conflict at the enum tail: keep their deletions, then `MediaChromeReveal, MediaChromeConceal,` · their `Reveal,` group · our `PaneOpen, PaneClose,` group; keep all `Get` arms and properties of both |
| `fluent-gpu\src\FluentGpu.Engine\Foundation\Easing.cs` | deletes `FluentDisclosureCollapse`, `FluentDisclosureChevron` (enum + evaluator arms) | P0-WP2 appends `FluentPane` after `Hold` (enum + evaluator arm) | keep their deletions and our append (separate hunks; the enum is not persisted) |
| `fluent-gpu\src\FluentGpu.Controls\ItemsView.cs`, `ListOptions.cs` | FlowReveal (`SizeMode.FlowReveal`, reveal bands; ~443 lines in ItemsView) | P0 adds `FocusItem` / `OnEdgeNavigate` (additive; the arrow-key arm edit) | keep both; our arrow-arm hunk replaces only the `Left/Right/Up/Down` case body |
| `fluent-gpu\src\FluentGpu.VerticalSlice\Suites\ControlsSuite.cs` | 312 lines | not touched (P0 registers its check from `ControlsSuite.RowFocus.cs`) | none |

**No new code may reference `MotionTok.DisclosureExpand` / `DisclosureCollapse` / `DisclosureChevron`, their
`MotionTokenId` members, or `Easing.FluentDisclosureCollapse` / `FluentDisclosureChevron`**: smooth-reveal deletes them, so
a reference added here would break whichever branch lands second. Nothing in this plan does (the only `Disclosure*`
names it uses are `RowSpec.DisclosureChevron`, a row slot, and the untouched `PaneView` disclosure members).

Whichever branch lands second rebases; no WP here adds a WinUI-style disclosure tween.

---

## 2. Phase map

| Phase | Repos | What ships | Gates |
|---|---|---|---|
| **P0** engine parity | engine | NavigationView metrics, `Easing.FluentPane`, `MotionTok.PaneOpen/PaneClose`, TitleBar `PaneToggleEnabledSignal`/`PaneToggleToolTip`, glyphs `Lock`/`Library`, `ItemsViewController.FocusItem`, `ListOptions.OnEdgeNavigate` | engine Debug+Release build, `FluentGpu.Engine.Tests` D+R, VerticalSlice |
| **P1** geometry and skin | app | WinUI row ladder (36/40, 4,2 margin, icon centre 24, label 48, 31 indents), neutral ramp, pill r2, 40-px headers, 8-px separators, subtitles, truncation tooltips, typeahead, Classic Ctrl+F filter | app D+R build, Wavee.Tests D+R |
| **P2** modes, rail, footer, title bar | app | `SidebarPaneModeRules` (Expanded/Compact/Minimal), the one presentation effect, the 48 rail as the same list, the overlay pane, the footer (Settings + ⋯), title-bar toggle wiring, detents deleted | same |
| **P3** model, planner, stores, accounts, migration | app | catalogue + overlays, new planner, binder rewrite, `sidebar.json` v3 + `sidebar.acct-*.json`, per-account pins with the safe swap, Liked filter, the v2 migration, Custom/customizer/templates/palette/reducer/data sources deleted | same |
| **P4** Edit mode, menus, undo | app | Outline + edit bar, `SidebarMenuModel`, `SidebarUndoRing`, header/row/pane menus, Ctrl+Z/Y, palette command, wizard Layout step | same |
| **P5** Library (P.2a) | app | Home · page dropdown · chips · toolbar head, pins with a pin mark, Liked first, hidden kinds, Custom order = rootlist, grid without placeholders, Library rail | same |
| **P6** cleanup | app | `docs/guide/sidebar.md`, the `wavee-sidebar` skill, dead loc keys, dead settings keys, census trim, CHANGELOG | same |

Every phase leaves both repos building and the app usable: P1 and P2 run on today's model; P3 swaps the model in one
step; P4 and P5 add on top.

The app references the engine through `$(EngineRoot)` (`..\fluent-gpu\`), so P0 must be merged into the sibling engine
checkout before P1 builds.

---

## P0. Engine parity (fluent-gpu only, its own engine PR)

Nothing in the app depends on the NavigationView control edits (the sidebar does not adopt the control; it adopts its
metrics). The app does depend on `Easing.FluentPane`/`MotionTok.PaneOpen` (P2), the TitleBar members (P2), the glyphs
(P2, P4) and the ItemsView members (P5).

### P0.1 NavigationView metrics (`src/FluentGpu.Controls/NavigationView.cs`)

Edit the constants block (search `const float ItemMarginY`):

```csharp
    const float ItemHeight = 36f;         // NavigationViewItemOnLeftMinHeight (:217)
    const float ItemMarginX = 4f;
    const float ItemMarginY = 2f;         // NavigationViewItemButtonMargin 4,2 (:228, applied :451)
    const float ItemOuterHeight = ItemHeight + 2f * ItemMarginY;   // the row PITCH, 40
    const float HeaderHeight = 40f;       // NavigationViewItemInnerHeaderMargin row (:229)
    const float SeparatorRowHeight = 8f;  // 1px rule + the 0,3,0,4 margin (:247/:223)
```

(`ItemOuterHeight` was `36f`; it is now derived.)

`SelectedPos` — the compact header contributes nothing:

```csharp
            if (it.IsHeader)
            {
                y += labelsVisible ? HeaderHeight : 0f;
                continue;
            }
```

`HeaderItem` — compact arm:

```csharp
        if (!expandedLayout)
            return new BoxEl { Height = 0f };
```

`CompactPane` builds its rail rows without `HeaderItem`: both of its header arms (the `compactFlat` loop and the
`footerItems` loop, today `it.IsHeader ? new BoxEl { Height = 8 }`) become `it.IsHeader ? new BoxEl { Height = 0f }` —
WinUI collapses headers in the compact rail, so a header contributes nothing to the rail's pitch:

```csharp
        foreach (var (it, _) in compactFlat)
            children.Add(it.IsHeader ? new BoxEl { Height = 0f }
                       : it.IsSeparator ? SeparatorRow(expandedLayout: false)
                       : Item(it, 0, children.Count, compactSelected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false, captureRow(it.Key), Parts));
```

```csharp
        foreach (var it in footerItems)
            children.Add(it.IsHeader ? new BoxEl { Height = 0f }
                       : it.IsSeparator ? SeparatorRow(expandedLayout: false)
                       : Item(it, 0, children.Count, selected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false, captureRow(it.Key), Parts));
```

`SeparatorRow` — full width in both layouts, the rule at y = 3 (margin 0,3,0,4):

```csharp
    static Element SeparatorRow(bool expandedLayout, int depth = 0) => new BoxEl
    {
        Height = SeparatorRowHeight,
        Direction = 1,
        Justify = FlexJustify.Start,
        Padding = new Edges4(expandedLayout ? depth * IndentStep : 0f, 3f, 0f, 4f),
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
    };
```

Item root (`Corners = Radii.OverlayAll`): replace the line and its comment with

```csharp
            Corners = Radii.ControlAll,   // ControlCornerRadius (4): NavigationView.xaml:432 sets it and the presenter template-binds it (:462)
```

The compact InfoBadge overlay (the `iconCell` children): the badge sits in the icon column's top-right corner with
margin 0,2,2,0 (TR:586-595) instead of a pixel offset:

```csharp
            Children = it.InfoBadge is not null && !expandedLayout
                ?
                [
                    // Compact rail: the badge overlays the icon column's top-right corner, margin 0,2,2,0 (themeresources:586-595).
                    new BoxEl
                    {
                        ZStack = true, Width = IconColumnWidth - IndicatorW, Height = ItemHeight,
                        Children =
                        [
                            new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [iconVisual] },
                            new BoxEl
                            {
                                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
                                Margin = new Edges4(0f, 2f, 2f, 0f), HitTestVisible = false, Children = [it.InfoBadge],
                            },
                        ],
                    },
                ]
                : [iconVisual],
```

### P0.2 `Easing.FluentPane` and the pane tokens

`src/FluentGpu.Engine/Foundation/Easing.cs` — append after `Hold,` (the enum is not persisted; appending keeps every
existing value):

```csharp
    /// <summary><b>FluentPane</b> — WinUI SplitView pane open/close — cubic-bezier(0, 0.35, 0.15, 1)
    /// (SplitView_themeresources.xaml:10-12, 313-316). Not <see cref="FluentDecelerate"/> (0.1, 0.9, 0.2, 1): the pane
    /// leaves at a steeper slope and settles later.</summary>
    FluentPane,
```

and in the evaluator switch, after the `Easing.Hold` arm:

```csharp
        Easing.FluentPane => CubicBezier(t, 0.0f, 0.35f, 0.15f, 1.0f),
```

`src/FluentGpu.Engine/Animation/MotionTok.cs` — append `PaneOpen, PaneClose,` as a new comment group at the END of
`MotionTokenId`:

```csharp
    // Navigation pane (WinUI SplitView: open 200 ms, close 100 ms, both on the FluentPane spline).
    PaneOpen, PaneClose,
```

`Get` arms (before the `_` arm) and the two named properties (after `MediaChromeConceal`):

```csharp
        MotionTokenId.PaneOpen => MotionTokenDef.Eased(200f, Easing.FluentPane, ReducedMotionPolicy.KeepFade),
        MotionTokenId.PaneClose => MotionTokenDef.Eased(100f, Easing.FluentPane, ReducedMotionPolicy.KeepFade),
```

```csharp
    public static MotionTokenDef PaneOpen => Get(MotionTokenId.PaneOpen);
    public static MotionTokenDef PaneClose => Get(MotionTokenId.PaneClose);
```

### P0.3 TitleBar pane toggle (`src/FluentGpu.Controls/TitleBar.cs`)

`TitleBarOptions` gains (after `OnPaneToggle`):

```csharp
    /// <summary>Live enabled state of the pane toggle (see <see cref="TitleBar.PaneToggleEnabledSignal"/>).</summary>
    public IReadSignal<bool>? PaneToggleEnabledSignal { get; init; }
    /// <summary>The pane toggle's tooltip, read per render (see <see cref="TitleBar.PaneToggleToolTip"/>).</summary>
    public Func<string>? PaneToggleToolTip { get; init; }
```

`TitleBar` fields (after `OnPaneToggle`):

```csharp
    /// <summary>Live enabled state of the pane toggle. Null ⇒ enabled. Reading it subscribes the bar, so the toggle
    /// re-glyphs enabled↔disabled in place (the <see cref="BackEnabledSignal"/> pattern) — a shell that pins its pane
    /// open (an edit mode) binds this instead of hiding the button.</summary>
    public IReadSignal<bool>? PaneToggleEnabledSignal;
    /// <summary>The pane toggle's tooltip text, invoked per render (WinUI's PaneToggleButton names its action:
    /// "Collapse navigation" / "Expand navigation"). Null or "" ⇒ no tooltip. Its text is part of the memo key.</summary>
    public Func<string>? PaneToggleToolTip;
```

`TitleBar.Create(TitleBarOptions)` copies both (`PaneToggleEnabledSignal = options.PaneToggleEnabledSignal,
PaneToggleToolTip = options.PaneToggleToolTip,`).

In `Render`, right after the `backEnabled` line:

```csharp
        bool paneEnabled = PaneToggleEnabledSignal is { } pes ? pes.Value : true;   // subscribe: re-glyph the toggle live
        string paneTip = PaneToggleToolTip?.Invoke() ?? "";
```

The layout-effect dep key becomes
`DepKey.From(HashCode.Combine(HashCode.Combine(viewport.Width, viewport.Height, epoch, _availDip.Peek(), tabsVer, ShowBackButton, ShowPaneToggle, ShowCaptionButtons), contentVer, paneEnabled))`.

The memo key gains the two facts:

```csharp
        int key = unchecked(((((((epoch * 397 ^ tabsVer) * 397 ^ contentVer) * 397 ^ _availDip.Peek().GetHashCode()) * 397 ^ Tok.Epoch) * 397
                              ^ StringComparer.Ordinal.GetHashCode(paneTip)) * 397)
            ^ ((active ? 1 : 0) | (maximized ? 2 : 0) | (ShowBackButton ? 4 : 0) | (ShowPaneToggle ? 8 : 0) | (ShowCaptionButtons ? 16 : 0) | (backEnabled ? 32 : 0) | (ShowRailBaseline ? 64 : 0) | (TabsElasticLane ? 128 : 0) | (paneEnabled ? 256 : 0)));
```

The toggle:

```csharp
        if (ShowPaneToggle)
        {
            var pane = IconButton.Create(Icons.Menu, () => OnPaneToggle?.Invoke(), navStyle, isEnabled: paneEnabled)
                with { Margin = navMargin };
            var applied = Parts.Apply(PartPaneToggle, pane);
            Element paneEl = applied with
            {
                OnClick = pane.OnClick, Role = AutomationRole.Button, Children = pane.Children,
                OnRealized = TemplateParts.Chain<NodeHandle>(h => _pane = h, applied.OnRealized),
            };
            kids.Add(paneTip.Length > 0 ? ToolTip.Wrap(paneEl, paneTip) : paneEl);
        }
```

(`ToolTip.Wrap` keeps the inner box, so `_pane` still captures the button node the region report reads.)

### P0.4 Glyphs (`src/FluentGpu.Controls/glyphs.json`)

Add next to `"Pin"`:

```json
  "Lock": "E72E",
  "Library": "E8F1",
```

### P0.5 ItemsView focus handoff (`src/FluentGpu.Controls/ItemsView.cs`, `ListOptions.cs`)

`ItemsViewController` (after `CurrentItemIndex`):

```csharp
    internal Action<int>? FocusItemImpl;

    /// <summary>Make <paramref name="index"/> the current item, bring it into view and give it keyboard focus (the bound
    /// list's roving tab stop follows). The way a fixed head above a list hands keyboard focus down into it. A no-op for
    /// a disabled or out-of-range index, and before the list mounts.</summary>
    public void FocusItem(int index) => FocusItemImpl?.Invoke(index);
```

Wire it where `ctl.GetCurrent = current.Peek;` is assigned (same effect), and clear it next to `ctl.GetCurrent = null;`:

```csharp
            ctl.FocusItemImpl = i => MoveCurrent(i, false, false);
```

```csharp
                ctl.FocusItemImpl = null;
```

`ListOptions` (after `IsItemSelectable`):

```csharp
    /// <summary>Arrow navigation ran off an END of the list: −1 for Up/Home at the first enabled item, +1 for Down/End at
    /// the last. The key stays handled (nav keys never fall through to an outer scroller); the callback lets a fixed head
    /// above or below the list take focus back. Null ⇒ nothing happens at the ends (today's behaviour).</summary>
    public Action<int>? OnEdgeNavigate { get; init; }
```

`ItemsView` component field (after `IsItemSelectable`):

```csharp
    /// <summary>See <see cref="ListOptions.OnEdgeNavigate"/>.</summary>
    public Action<int>? OnEdgeNavigate;
```

Copy it in every options → component / options → options mapping that copies `ItemText = …` (three sites:
`OnEdgeNavigate = o.OnEdgeNavigate,`).

In the key handler, the arrow arm becomes:

```csharp
                case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                {
                    int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                    int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                    int next = NavigateIndex(from, dx, dy);
                    if (next >= 0 && next != from) MoveCurrent(next, ctrl, shift);
                    else if (dy != 0) OnEdgeNavigate?.Invoke(dy);   // ran off an end: a head above may take focus back
                    e.Handled = true;   // nav keys never fall through to an outer scroller (cpp:806-807)
                    return;
                }
```

and in the `Home or End` arm, after computing `t`: `if (t == from) OnEdgeNavigate?.Invoke(home ? -1 : 1);`.

### P0.6 VerticalSlice gates

`Suites/AnimSuite.cs` — a check next to `gate.anim.navigationSelectionWorm`:

```csharp
        // gate.anim.easing.fluentPane — the WinUI SplitView spline, and the two pane tokens built on it.
        {
            float e0 = Easings.Ease(Easing.FluentPane, 0f), e1 = Easings.Ease(Easing.FluentPane, 1f);
            float mid = Easings.Ease(Easing.FluentPane, 0.5f);           // cubic-bezier(0,.35,.15,1) at x=.5 ≈ 0.90
            float q = Easings.Ease(Easing.FluentPane, 0.25f), q3 = Easings.Ease(Easing.FluentPane, 0.75f);
            var open = MotionTok.PaneOpen; var close = MotionTok.PaneClose;
            Check("gate.anim.easing.fluentPane FluentPane is cubic-bezier(0,0.35,0.15,1): ends pinned, monotone, ~0.90 at the midpoint; PaneOpen 200 ms / PaneClose 100 ms on it",
                e0 == 0f && e1 == 1f && mid > 0.88f && mid < 0.92f && q < mid && mid < q3
                && open.DurationMs == 200f && open.Easing == Easing.FluentPane
                && close.DurationMs == 100f && close.Easing == Easing.FluentPane,
                $"e0={e0} e1={e1} q={q:0.000} mid={mid:0.000} q3={q3:0.000} open={open.DurationMs}/{open.Easing} close={close.DurationMs}/{close.Easing}");
        }
```

`Suites/TitleBarSuite.cs` — a probe and a check (after the rail-baseline check, inside `Run`):

```csharp
        // ── (g) the pane toggle's live enabled state: a disabled toggle re-renders in place, and re-enables ────────────
        {
            using var app2 = new HeadlessPlatformApp();
            var window2 = new HeadlessWindow(new WindowDesc("titlebar-pane", new Size2((int)BarW, 300), 1f));
            window2.Show();
            var paneProbe = new PaneToggleProbe();
            using var host2 = new AppHost(app2, window2, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, paneProbe);
            void Settle2(int n = 6) { for (int i = 0; i < n; i++) host2.RunFrame(); }
            Settle2();
            var toggle = FindPaneToggle(host2.Scene, host2.Scene.Root);
            bool enabledAtStart = !toggle.IsNull && (host2.Scene.Flags(toggle) & NodeFlags.Disabled) == 0;
            paneProbe.Enabled.Value = false;
            Settle2();
            var toggleOff = FindPaneToggle(host2.Scene, host2.Scene.Root);
            bool disabled = !toggleOff.IsNull && (host2.Scene.Flags(toggleOff) & NodeFlags.Disabled) != 0;
            paneProbe.Enabled.Value = true;
            Settle2();
            var toggleOn = FindPaneToggle(host2.Scene, host2.Scene.Root);
            bool reenabled = !toggleOn.IsNull && (host2.Scene.Flags(toggleOn) & NodeFlags.Disabled) == 0;
            Check("gate.titlebar.pane-toggle.enabled-signal PaneToggleEnabledSignal disables the pane toggle in place and re-enables it",
                enabledAtStart && disabled && reenabled,
                $"start={enabledAtStart} disabled={disabled} reenabled={reenabled}");
        }
```

```csharp
    /// <summary>The pane toggle: the first Button-role node inside the bar's left 96 DIP that is 36-52 wide.</summary>
    static NodeHandle FindPaneToggle(SceneStore scene, NodeHandle root)
    {
        NodeHandle found = NodeHandle.Null;
        void Walk(NodeHandle n)
        {
            if (n.IsNull || !found.IsNull) return;
            var r = scene.AbsoluteRect(n);
            if (scene.Interaction(n).Role == AutomationRole.Button && r.X < 96f && r.W >= 36f && r.W <= 52f) { found = n; return; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) Walk(c);
        }
        Walk(root);
        return found;
    }
```

```csharp
/// <summary>A bar with only the pane toggle, its enabled state bound to a signal.</summary>
sealed class PaneToggleProbe : Component
{
    public readonly Signal<bool> Enabled = new(true);

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Embed.Comp(() => new TitleBar
            {
                Title = "pane", ShowPaneToggle = true, PaneToggleEnabledSignal = Enabled,
                PaneToggleToolTip = () => "Collapse navigation", ShowCaptionButtons = true,
            }),
            new BoxEl { Grow = 1f },
        ],
    };
}
```

`Suites/NavSuite.cs` — no probe in the suite carries a header today, so the gate brings its own. At the END of
`NavigationViewChecks` (after the 54d block):

```csharp
        // gate.nav.metrics.parity — the 4,2 item margin (pitch 40), the 40-DIP header, and a 0-DIP header in compact.
        {
            // The two item rows' top edges, in a FORCED display mode (Left = Expanded, LeftCompact = the 48 rail). An item
            // row is the NavigationItem-role box; its AbsoluteRect excludes the 2-DIP margins, so b.Y − a.Y = 36 + 2 + header + 2.
            float GapAB(NavPaneDisplayMode mode)
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("nav-metrics", new Size2(1200, 700), 1f));
                window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                    new NavMetricsProbe(mode));
                for (int i = 0; i < 8; i++) host.RunFrame();   // past the first-mount reflow transitions
                var ys = new List<float>();
                void Visit(NodeHandle n)
                {
                    if (n.IsNull) return;
                    if (host.Scene.Interaction(n).Role == AutomationRole.NavigationItem)
                    {
                        float y = MathF.Round(host.Scene.AbsoluteRect(n).Y, 1);
                        if (!ys.Contains(y)) ys.Add(y);
                    }
                    for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Visit(c);
                }
                Visit(host.Scene.Root);
                ys.Sort();
                return ys.Count >= 2 ? ys[1] - ys[0] : -1f;   // a (first) and b (second): the probe has no footer items
            }

            float expandedGap = GapAB(NavPaneDisplayMode.Left);
            float compactGap = GapAB(NavPaneDisplayMode.LeftCompact);
            Check("gate.nav.metrics.parity expanded: b.Y − a.Y == 40 (item pitch) + 40 (header); compact: b.Y − a.Y == 40 (header 0)",
                Near(expandedGap, 80f, 0.5f) && Near(compactGap, 40f, 0.5f),
                $"expandedGap={expandedGap} compactGap={compactGap}");
        }
```

and, at the end of `NavSuite.cs` beside the file's other probe classes (`SemanticZoomItemsProbe`, `ChromeIdleProbe`):

```csharp
/// <summary>gate.nav.metrics.parity: two leaf items around a header, in a forced display mode, no footer.</summary>
sealed class NavMetricsProbe(NavPaneDisplayMode mode) : Component
{
    public override Element Render() => Embed.Comp(() => new NavigationView
    {
        Items = [new NavItem("a", "A", "Alpha"), new NavItem("h", "", "Group", IsHeader: true), new NavItem("b", "B", "Beta")],
        PaneDisplayMode = mode,
        Content = key => new BoxEl { Children = [new TextEl("PAGE:" + key)] },
    });
}
```

(`Near(a, b, tol)` is the suite's existing helper; `List<float>`/`MathF` need no new `using` — the file already uses
both.) Before P0.1 the expanded gap is 72 (36 item, no Y margin, a 36 header) and the compact gap 44 (the 8-px header box); after it, 80 and
40.

New `Suites/ControlsSuite.ItemsFocus.cs` (a partial of `ControlsSuite`, registered by ONE line appended at the end of
`BoundRowFocusChecks` in `ControlsSuite.RowFocus.cs`: `ItemsFocusApiChecks(strings);`). Its using header is
`ControlsSuite.RowFocus.cs`'s, copied verbatim (the `System`/`FluentGpu.*` usings, the two `using static
FluentGpu.VerticalSlice.Harness.*` lines); like that file it has no namespace declaration:

```csharp
// ── ItemsViewController.FocusItem + ListOptions.OnEdgeNavigate: a fixed head hands keyboard focus down into a bound
// list, and the list hands it back when the arrows run off an end. ────────────────────────────────────────────────
static partial class ControlsSuite
{
    sealed class ItemsFocusProbe : Component
    {
        public const int N = 20;
        public readonly ItemsViewController Controller = new();
        public readonly List<NodeHandle> Nodes = new();
        public int LastEdge;

        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = 200f,
            Children =
            [
                ItemsView.CreateBound(N, scope =>
                {
                    int slot = Nodes.Count;
                    Nodes.Add(NodeHandle.Null);
                    return SelectorVisualsBound.None(in scope, new BoxEl { Height = 40f, Grow = 1f })
                        with { OnRealized = h => Nodes[slot] = h };
                }, RepeatLayout.Stack(40f), new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None, Controller = Controller,
                    OnEdgeNavigate = dir => LastEdge = dir,
                }),
            ],
        };
    }

    static void ItemsFocusApiChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("items-focus", new Size2(240, 200), 1f));
        window.Show();
        var probe = new ItemsFocusProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        void Frames(int n) { for (int i = 0; i < n; i++) host.RunFrame(); }
        void Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); Frames(3); }
        Frames(4);

        probe.Controller.FocusItem(2);
        Frames(3);
        bool current2 = probe.Controller.CurrentItemIndex == 2 && !host.Input.Focused.IsNull;
        probe.Controller.FocusItem(0);
        Frames(3);
        Key(Keys.Up);
        bool upEdge = probe.LastEdge == -1 && probe.Controller.CurrentItemIndex == 0;
        probe.LastEdge = 0;
        Key(Keys.Down);
        bool noEdgeInside = probe.LastEdge == 0 && probe.Controller.CurrentItemIndex == 1;

        Check("gate.virt.itemsFocus FocusItem makes an item current and focuses its slot; Up at the first item reports OnEdgeNavigate(−1) and keeps the item; Down inside the list reports nothing",
            current2 && upEdge && noEdgeInside,
            $"current2={current2} upEdge={upEdge} noEdgeInside={noEdgeInside} current={probe.Controller.CurrentItemIndex} edge={probe.LastEdge}");
    }
}
```

(`using` list: copy the header of `ControlsSuite.RowFocus.cs`.)

### P0 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P0-WP1 | `NavigationView.cs` | §P0.1 (constants, `SelectedPos`, `HeaderItem`, BOTH `CompactPane` header arms → 0, `SeparatorRow`, corners, badge) | — |
| P0-WP2 | `Easing.cs`, `MotionTok.cs` | §P0.2 (`Easing.FluentPane` appended after `Hold`; `MotionTokenId.PaneOpen/PaneClose` appended at the END of the enum + `Get` arms + properties; never reference the Disclosure* tokens, §1) | — |
| P0-WP3 | `TitleBar.cs` | §P0.3 | — |
| P0-WP4 | `glyphs.json` | §P0.4 | — |
| P0-WP5 | `ItemsView.cs`, `ListOptions.cs` | §P0.5 (`ItemsViewController.FocusItem`, `ListOptions.OnEdgeNavigate`; the arrow arm hunk replaces only the Left/Right/Up/Down case body) | — |
| P0-WP6 | `AnimSuite.cs`, `TitleBarSuite.cs`, `NavSuite.cs`, `ControlsSuite.RowFocus.cs`, new `ControlsSuite.ItemsFocus.cs` | §P0.6 (incl. `gate.nav.metrics.parity` + `NavMetricsProbe` at the end of `NavSuite.cs`) | P0-WP1, P0-WP2, P0-WP3, P0-WP4, P0-WP5 |

---

## P1. Geometry and skin (no model change)

Today's documents (`SidebarCustomLayout`), modes and planner stay. Every row, header, separator and pill is redrawn to
the WinUI ladder; one coordinate space is fixed for the whole pane.

### P1.0 The coordinate space (read this first)

The list gets ONE horizontal inset of **4** (WinUI's item margin X, applied once): `PaneMetrics.PanePad =
(4, 3, 4, 0)`. Every slot therefore starts at pane x = 4, and every number below is **slot space** unless it says
"pane":

```
pane x:  0  4        24          48                                 W-44  W-18 W-4  W
slot x:     0        20          44                                 W-48  W-22 W-8
            |pill 3×16 at 31·depth
            |  icon column 40 (glyph 16 / art 32 or 24, centred at 20) | label (4 gap) … | trailing … pad 14 |
                                                                  |← chevron column 40 →|  (replaces the 14 pad)
```

Rows carry the vertical half of the 4,2 margin themselves (`Margin = (0, 2, 0, 2)`), so a 36-px row has a 40-px pitch
and a 40-px two-line row a 44-px pitch. Headers (40) and separators (8) have no margin. A separator bleeds to the full
pane width with a −4 horizontal margin. The drop caret, the pill and the indent all start at slot x `31·depth`.

### P1.1 `SidebarRowGeometry` (rewritten, `Shell/Sidebar.cs`)

Replace the whole class body above `// ── pure plan geometry ──` with the block below, add the shape enum above the
class, and keep the plan-geometry helpers (`ContentYOf`, `IndexOfRoute`, `DirectionOf`, `FolderHeaderIndexOf`,
`TryFolderDescendantRange`, `ShowsPinGlyph`, `GridFallbackColumns`, `TrySectionBodyRange`) byte-for-byte.

```csharp
/// <summary>Which of the three WinUI row shapes a section's rows take (design V.3). One shape per SECTION, never per
/// row: a band's reorder pitch and the virtualizing host's extent table both assume it.</summary>
public enum SidebarRowShape : byte
{
    /// <summary>Row A — a glyph row (Home, Collections, Settings, a route pin in a glyph section): 36 tall, 16-px glyph.</summary>
    Glyph = 0,
    /// <summary>Row C — an entity row at Default density: 40 tall, 32-px art, title + subtitle.</summary>
    EntityTwoLine = 1,
    /// <summary>Row B — an entity row at Compact density: 36 tall, 24-px art, title only.</summary>
    EntityOneLine = 2,
}

/// <summary>THE ONE ROW LADDER, drawn to WinUI NavigationView (NavigationView_themeresources.xaml, "TR"): 36-px rows in
/// a 4,2 margin (TR:217, TR:228), a 40-px icon column whose centre sits at pane x 24 (TR:612), the label at pane x 48
/// (TR:251), trailing content ending at pane W − 18 (TR:604), a 40-px chevron column at pane W − 44..W − 4 (TR:617), 31-px
/// folder indents (NavigationViewItemBase.h:63) and the 3×16 r2 pill at slot x 31·depth (TR:220-222). Numbers are SLOT
/// space (the list's one 4-px inset is <see cref="PaneEdge"/>). Engine-free so Wavee.Tests pins every one.</summary>
public static class SidebarRowGeometry
{
    // ── the pane ──
    /// <summary>The list's horizontal inset (4) — WinUI's item margin X, applied ONCE around the list
    /// (<c>PaneMetrics.PanePad</c>), never per row.</summary>
    public const float PaneEdge = 4f;
    /// <summary>The pane content grid's top margin (TR:233's −1,3 with the −1 dropped: no content border to tuck under).</summary>
    public const float PaneTopInset = 3f;
    /// <summary>The vertical half of the 4,2 item margin, carried by every row.</summary>
    public const float RowMarginY = 2f;
    /// <summary>The compact rail (TR:208 NavigationViewCompactPaneLength).</summary>
    public const float RailWidth = 48f;
    /// <summary>A compact-rail row: the 48 rail less the two 4-px insets.</summary>
    public const float TileWidth = 40f;

    // ── rows ──
    public const float RowHeight = 36f;
    public const float TwoLineRowHeight = 40f;
    public const float IconColumn = 40f;
    public const float GlyphSize = 16f;
    /// <summary>The ContentPresenter's 4-px left margin between the icon column and the label (TR:251).</summary>
    public const float LabelGap = 4f;
    /// <summary>The ContentGrid's 14-px right margin (TR:604): trailing content ends at pane W − 18.</summary>
    public const float TrailingPad = 14f;
    /// <summary>The chevron column (TR:617): its −14 margin cancels <see cref="TrailingPad"/>.</summary>
    public const float ChevronColumn = 40f;
    public const float IndentStep = 31f;
    public const int MaxIndentDepth = 3;
    /// <summary>The trailing cluster's gap (count · pin mark · equalizer).</summary>
    public const float TrailingGap = 6f;

    // ── chrome rows ──
    public const float HeaderHeight = 40f;
    /// <summary>The header title's x: pane 16 (TR:229's 16,0) less <see cref="PaneEdge"/>.</summary>
    public const float HeaderTextX = 12f;
    /// <summary>A header's inline button (chevron, ⋯, +): 24×24.</summary>
    public const float HeaderButton = 24f;
    /// <summary>A separator: the 1-px rule plus its 0,3,0,4 margin (TR:223, TR:247), full pane width.</summary>
    public const float SeparatorHeight = 8f;
    public const float SeparatorLineTop = 3f;
    /// <summary>A quiet one-line hint (an empty Playlists, a search with no match): 40 tall in the 4,2 margin.</summary>
    public const float EmptyHintHeight = 40f;
    /// <summary>The tree's closing drop gutter.</summary>
    public const float TreeEndHeight = 24f;

    // ── the pill ──
    public const float PillW = 3f, PillH = 16f, PillRadius = 2f;

    public static float HeightOf(SidebarRowShape shape)
        => shape == SidebarRowShape.EntityTwoLine ? TwoLineRowHeight : RowHeight;

    /// <summary>A row's slot extent: its height plus the 2 + 2 margin (40 / 44 / 40).</summary>
    public static float PitchOf(SidebarRowShape shape) => HeightOf(shape) + 2f * RowMarginY;

    /// <summary>The leading visual's edge: glyph 16 · art 32 (Default) · art 24 (Compact).</summary>
    public static float ArtOf(SidebarRowShape shape) => shape switch
    {
        SidebarRowShape.Glyph => GlyphSize,
        SidebarRowShape.EntityTwoLine => 32f,
        _ => 24f,
    };

    public static int ClampDepth(int depth) => depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;

    /// <summary>The content indent for a nesting depth: 31 per level, capped at 3 (93). The FILL stays full width; only
    /// pill, icon and label move (NavigationViewItem.cpp:894-902).</summary>
    public static float IndentFor(int depth) => IndentStep * ClampDepth(depth);

    /// <summary>The pill's x in slot space (pane 4 + 31·depth).</summary>
    public static float PillX(int depth) => IndentFor(depth);

    /// <summary>The pill's y inside a slot whose row is <paramref name="rowHeight"/> tall: centred on the row, below its
    /// 2-px top margin.</summary>
    public static float PillTop(float rowHeight) => RowMarginY + (rowHeight - PillH) * 0.5f;

    /// <summary>Where the drop caret for depth <paramref name="depth"/> starts (slot space): the same x as the pill, so
    /// "insert here at this depth" lines up with the row it describes. Read backwards by the drop resolver.</summary>
    public static float TreeContentX(int depth) => IndentFor(depth);

    /// <summary>Pane-space rulers (diagnostics, tests).</summary>
    public const float IconCentreX = PaneEdge + IconColumn * 0.5f;              // 24
    public const float LabelX = PaneEdge + IconColumn + LabelGap;               // 48
    public static float TrailingRight(float paneWidth) => paneWidth - PaneEdge - TrailingPad;   // W − 18
    public static float ChevronLeft(float paneWidth) => paneWidth - PaneEdge - ChevronColumn;   // W − 44

    /// <summary>A subtitle line is drawn only in a two-line row and only when there is text.</summary>
    public static bool SubtitleVisible(SidebarRowShape shape, string? subtitle)
        => shape == SidebarRowShape.EntityTwoLine && subtitle is { Length: > 0 };

    /// <summary>TRANSITIONAL (deleted in P3 with <c>SidebarDisplayOptions</c>): today's per-section display options →
    /// the shape. A glyph section (no artwork) is row A; Compact or no subtitles is row B; otherwise row C.</summary>
    public static SidebarRowShape ShapeFor(SidebarDensity density, bool subtitles, bool artwork)
        => !artwork ? SidebarRowShape.Glyph
         : density == SidebarDensity.Compact || !subtitles ? SidebarRowShape.EntityOneLine
         : SidebarRowShape.EntityTwoLine;

    // The EntityEmbed / PromptRow / chip-strip ladder survives only until P3 deletes Curated:
    public static float CardHeightFor(SidebarDensity density) => density switch
    {
        SidebarDensity.Compact => 56f,
        SidebarDensity.Comfortable => 88f,
        _ => 72f,
    };
    public static float PromptHeight(bool hasReason) => hasReason ? 56f : 48f;
    public const float ChipHeight = 24f;
    public const float ChipStripHeight = ChipHeight + 2f;
    public const float ChipStripGap = 4f;
    public const float PinDropZoneRestHeight = 56f;
```

Removed members and their replacements (every caller, every file):

| removed | use |
|---|---|
| `ClassicHeight` | `PitchOf(SidebarRowShape.EntityTwoLine)` |
| `HeightFor(density, subtitles)` / `HeightFor(opts)` | `HeightOf(ShapeFor(density, subtitles, artwork: true))` (`PitchOf` where the caller sized a slot) |
| `ArtFor(density)` | `ArtOf(ShapeFor(density, subtitles: true, artwork: true))` |
| `RowInsetLeft`, `RowInsetRight`, `ContentLane`, `ContentLaneEnd` | `0f`, `TrailingPad`, `HeaderTextX`, `TrailingPad` |
| `SelGutterWidth`, `LeadingGap`, `LeadingLaneWidth`, `ArtX`, `TreeGuideStep` | gone (no gutter, no guides); `TreeGuideStep` → `IndentStep` |
| `SectionGap`, `HeaderBodyGap` | gone (0: separators carry the rhythm) |
| `DividerHeight` | `SeparatorHeight` |
| `SubtitleVisible(density, s)` | `SubtitleVisible(shape, s)` |

`SidebarRowExtents.HeightOf` (same file) becomes:

```csharp
    /// <summary>The analytic extent of plan row <paramref name="index"/>: one SLOT (row + its 2 + 2 margin) per item kind,
    /// 40 per header, 8 per separator. NaN for a grid strip (estimate, then correct on measure).</summary>
    public static float HeightOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSectionSpec? section, bool editable)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if ((uint)index >= (uint)rows.Count) return 0f;
        var row = rows[index];
        if (section is null) return 0f;
        var shape = SidebarRowGeometry.ShapeFor(section.Opts.Density, section.Opts.Subtitles, section.Opts.Artwork);
        switch (row.Kind)
        {
            case SidebarRowKind.SectionHeader:
                return SidebarRowGeometry.HeaderHeight
                     + (CarriesChipStrip(section, editable) ? SidebarRowGeometry.ChipStripGap + SidebarRowGeometry.ChipStripHeight : 0f);
            case SidebarRowKind.HeaderLabel: return SidebarRowGeometry.HeaderHeight;
            case SidebarRowKind.Divider: return SidebarRowGeometry.SeparatorHeight;
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            case SidebarRowKind.FolderHeader:
            case SidebarRowKind.Skeleton:
                return SidebarRowGeometry.PitchOf(shape);
            case SidebarRowKind.Empty: return EmptyHeight(section);
            case SidebarRowKind.TreeEnd: return SidebarRowGeometry.TreeEndHeight;
            case SidebarRowKind.EntityCard: return SidebarRowGeometry.CardHeightFor(section.Opts.Density);
            case SidebarRowKind.PromptRow: return SidebarRowGeometry.PromptHeight(section.Kind != SidebarSectionKind.Concerts);
            case SidebarRowKind.SectionCard: return SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine);
            default: return float.NaN;
        }
    }
```

`BandTop` is deleted (its only callers are this method and the slot's `Banded`, both rewritten). `EmptyHeight`
returns `PinDropZoneRestHeight` for Pinned, `0` for HideBody, `PitchOf(shape)` for ActionCard and
`EmptyHintHeight + 2f * RowMarginY` otherwise.

`SidebarDropCue.LineWidth` (same file): the caret runs from `TreeContentX(depth)` to the row's right edge —
`float w = contentWidth - SidebarRowGeometry.TreeContentX(depth);`. In `RootlistSlotResolver` (the x → depth
"ladder" lines that read `TreeContentX(0)` and `TreeGuideStep`), replace `SidebarRowGeometry.TreeGuideStep` with
`SidebarRowGeometry.IndentStep`; nothing else in the resolver changes.

**Episode-bearing playlists.** `SidebarLibraryEntry` gains

```csharp
    /// <summary>A playlist holding at least one episode — its subtitle counts "items", not "songs" (design P.1). Stamped by
    /// the projection from <c>Playlist.EpisodeCount</c>; false for every other kind.</summary>
    public bool HasEpisodes { get; init; }
```

and `SidebarProjection`'s rootlist walk (where it builds a `SidebarEntryKind.Playlist` entry from `new Playlist(slot)`)
adds `HasEpisodes = p.EpisodeCount > 0,` to the initializer (the same object initializer that sets `CountKnown`).

### P1.2 Pure rules (new file `Shell/Sidebar.Rules.cs`)

Header comment in the house style (`// ── Shell/Sidebar.Rules.cs ──…`, "Role: CORE", what lives here, "engine-free so
Wavee.Tests pins it"). Contents:

```csharp
using System.Collections.Generic;

namespace Wavee;

/// <summary>Where the one selection pill sits (design V.5).</summary>
public enum SidebarPillAnchor : byte { None = 0, Row = 1, AncestorFolder = 2, SectionHeader = 3 }

/// <summary>The pill's plan index and why it is there. <see cref="None"/> ⇒ no pill (the route is in a hidden section,
/// or nowhere in the sidebar).</summary>
public readonly record struct SidebarPillTarget(int PlanIndex, SidebarPillAnchor Anchor)
{
    public static readonly SidebarPillTarget None = new(-1, SidebarPillAnchor.None);
}

/// <summary>THE ONE-PILL RULE (design V.5): exactly one pill, on (1) the visible row for the route, else (2) the deepest
/// visible ancestor folder (NavigationView.cpp:5555-5586), else (3) the header — or the compact rail's section tile — of
/// the collapsed section holding it (a Wavee extension), else (4) nowhere. Navigation is never affected.</summary>
public static class SidebarPillRules
{
    /// <param name="routeAt">The pane's row → route projection (null/"" = not a navigation target).</param>
    /// <param name="ancestorFolderIds">The route's containing folders, DEEPEST first (<see cref="AncestorFolders"/>).</param>
    /// <param name="owningSectionId">The section that would show the route if it were expanded, or null.</param>
    public static SidebarPillTarget Resolve(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
        System.Func<int, string?> routeAt, string? route, IReadOnlyList<string> ancestorFolderIds, string? owningSectionId)
    {
        if (string.IsNullOrEmpty(route)) return SidebarPillTarget.None;
        int row = SidebarRowGeometry.IndexOfRoute(rows.Count, routeAt, route);
        if (row >= 0) return new SidebarPillTarget(row, SidebarPillAnchor.Row);
        for (int a = 0; a < ancestorFolderIds.Count; a++)
        {
            int folder = SidebarRowGeometry.FolderHeaderIndexOf(rows, entries, ancestorFolderIds[a]);
            if (folder >= 0) return new SidebarPillTarget(folder, SidebarPillAnchor.AncestorFolder);
        }
        if (owningSectionId is { Length: > 0 } section)
            for (int i = 0; i < rows.Count; i++)
                if (IsSectionAnchor(rows[i].Kind) && string.Equals(rows[i].SectionId, section, System.StringComparison.Ordinal))
                    return new SidebarPillTarget(i, SidebarPillAnchor.SectionHeader);
        return SidebarPillTarget.None;
    }

    /// <summary>A row that can stand for a whole collapsed section.</summary>
    public static bool IsSectionAnchor(SidebarRowKind kind) => kind == SidebarRowKind.SectionHeader;

    /// <summary>The folders containing <paramref name="route"/> in a depth-first flattened tree, deepest first, into a
    /// caller-owned list (cleared first). Walks backwards from the route's entry, taking each shallower folder once.</summary>
    public static void AncestorFolders(IReadOnlyList<SidebarLibraryEntry>? tree, string? route, List<string> into)
    {
        into.Clear();
        if (tree is null || string.IsNullOrEmpty(route)) return;
        int at = -1;
        for (int i = 0; i < tree.Count; i++)
            if (string.Equals(tree[i].Id, route, System.StringComparison.Ordinal)) { at = i; break; }
        if (at < 0) return;
        int depth = tree[at].Depth;
        for (int i = at - 1; i >= 0 && depth > 0; i--)
        {
            var e = tree[i];
            if (!e.IsFolder || e.Depth >= depth) continue;
            into.Add(e.FolderId.Length > 0 ? e.FolderId : e.Id);
            depth = e.Depth;
        }
    }
}

/// <summary>What a row's second line says, as data (formatting is the UI's: it owns the loc table).</summary>
public enum SidebarSubtitleKind : byte { None = 0, Songs = 1, Items = 2, Album = 3, Podcast = 4, Artist = 5, Text = 6 }

public readonly record struct SidebarSubtitle(SidebarSubtitleKind Kind, int Count, string Detail)
{
    public static readonly SidebarSubtitle None = new(SidebarSubtitleKind.None, 0, "");
}

/// <summary>The subtitle grammar (design P.1): a playlist with an episode counts "items", any other playlist "songs";
/// an album is "Album · first artist"; a show "Podcast · publisher"; an artist "Artist"; a folder "N items"; a track or
/// route its creator. An UNKNOWN count shows no subtitle — never "0 songs" (bug A1: gate on CountKnown).</summary>
public static class SidebarSubtitleRules
{
    public static SidebarSubtitle Of(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => !e.CountKnown ? SidebarSubtitle.None
            : new SidebarSubtitle(e.HasEpisodes ? SidebarSubtitleKind.Items : SidebarSubtitleKind.Songs, e.TrackCount, ""),
        SidebarEntryKind.Album => new SidebarSubtitle(SidebarSubtitleKind.Album, 0,
            e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator),
        SidebarEntryKind.Show => new SidebarSubtitle(SidebarSubtitleKind.Podcast, 0, e.Publisher),
        SidebarEntryKind.Artist => new SidebarSubtitle(SidebarSubtitleKind.Artist, 0, ""),
        SidebarEntryKind.Folder => e.CountKnown ? new SidebarSubtitle(SidebarSubtitleKind.Items, e.ChildCount, "") : SidebarSubtitle.None,
        SidebarEntryKind.Track or SidebarEntryKind.AppRoute => e.Creator.Length > 0
            ? new SidebarSubtitle(SidebarSubtitleKind.Text, 0, e.Creator) : SidebarSubtitle.None,
        _ => SidebarSubtitle.None,
    };
}

/// <summary>Which plan rows take keyboard focus and what typeahead reads from them (the ItemsView's own roving and
/// typeahead do the rest — <c>ListOptions.IsItemEnabled</c> / <c>ItemText</c>).</summary>
public static class SidebarTypeAheadRules
{
    /// <summary>Headers, glyph rows, entity rows and folders are focus stops; separators, hints, skeletons, drop bands
    /// and the tree's end gutter are not.</summary>
    public static bool IsFocusStop(SidebarRowKind kind) => kind is SidebarRowKind.SectionHeader or SidebarRowKind.IconRow
        or SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader or SidebarRowKind.Placeholder;

    /// <summary>The typeahead text of a row: its label for a focus stop, "" otherwise.</summary>
    public static string TextOf(SidebarRowKind kind, string label) => IsFocusStop(kind) ? label : "";
}

/// <summary>Does a one-line label overflow its column? An ESTIMATE (the engine exposes no "was trimmed" fact): Segoe UI
/// Variable at 14 px averages ~7 DIP per character. Used only to decide whether a row carries its label as a tooltip, so
/// a near miss costs a redundant tooltip, never a missing one for a clearly long title.</summary>
public static class SidebarLabelFit
{
    public const float AverageCharWidth = 7f;

    /// <summary>The label column of a row in a pane <paramref name="paneWidth"/> wide at <paramref name="depth"/>, with
    /// <paramref name="trailing"/> DIP of trailing content.</summary>
    public static float LabelWidth(float paneWidth, int depth, float trailing)
        => paneWidth - 2f * SidebarRowGeometry.PaneEdge - SidebarRowGeometry.IndentFor(depth) - SidebarRowGeometry.IconColumn
           - SidebarRowGeometry.LabelGap - SidebarRowGeometry.TrailingPad - trailing;

    public static bool Overflows(string? label, float labelWidth)
        => label is { Length: > 0 } && label.Length * AverageCharWidth > labelWidth;
}
```

### P1.3 Row primitives (`Shell/Sidebar.UI.Rows.cs`, never the `Chevron` class)

**`RowSpec`** — replace `Density`, `Gap`, `TreeNode`, `TreeDepth`, `TreeContinuationMask` with one field:

```csharp
        /// <summary>The section's row shape (height, art size, subtitle line). One per section.</summary>
        public SidebarRowShape Shape;
```

(constructor default `Shape = SidebarRowShape.EntityTwoLine;`). Add:

```csharp
        /// <summary>A compact-rail tile (P2): 40 wide, icon only, the label as tooltip.</summary>
        public bool Tile;
        /// <summary>The full label as a tooltip (a truncated title, or a rail tile).</summary>
        public bool LabelTooltip;
```

**The neutral ramp** (new, inside `EntityRow`):

```csharp
        /// <summary>WinUI's NavigationViewItem backplate ramp verbatim (TR:9-32 / 75-98): neutral fills, the pill is the only
        /// "you are here". Rest / hover / pressed / disabled, brush cross-fade 83 ms (ControlFaster).</summary>
        public static InteractionRecipe NavRow(bool selected) => new()
        {
            Fill = new StateBrush(
                selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                selected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                Tok.FillSubtleTransparent),
            BrushMs = 83f,
        };
```

**`EntityRow.Create`** — rewritten. Keep the `RowSpec` wiring members (`OnClick`, `OnActivate`, `Menu`, `Drag`,
`DropTarget`, `CheckLane`, `OnRename`, `OnMove`, `OnEscape`, `Overflow`, `Playing`, `Pinned`, `Trailing`,
`DisclosureChevron`) and `KeyHandler`, `OverflowButton`, `TrackArt`, `WithPlayTrackHint` as they are; delete
`SelGutter`, `StandardLeading`, `TreeLeading`, `TreeGuides`. The body:

```csharp
        public static BoxEl Create(in RowSpec spec)
        {
            bool selected = spec.Selected;
            bool enabled = spec.Enabled;
            bool hasSubtitle = !spec.Tile && SidebarRowGeometry.SubtitleVisible(spec.Shape, spec.Subtitle);
            float height = float.IsNaN(spec.Height) ? SidebarRowGeometry.HeightOf(spec.Shape) : spec.Height;
            float art = float.IsNaN(spec.ArtSize) ? SidebarRowGeometry.ArtOf(spec.Shape) : spec.ArtSize;
            var activate = spec.OnActivate;
            var checksVisible = spec.ChecksVisible;
            // Icon colour = label colour in every state (NVX:425-427): TextPrimary, a disabled row fades as a whole.
            var ink = Tok.TextPrimary;

            // ── the icon column: 40 wide, the glyph (16) or art centred at slot x 20 ──
            Element visual = spec.Leading
                ?? (spec.Glyph is { Length: > 0 } g ? Icon(g, SidebarRowGeometry.GlyphSize, ink) : new BoxEl { Width = art, Height = art });
            if (spec.Track && spec.Leading is not null) visual = TrackArt(visual, art, spec.Shape);
            Element iconColumn = new BoxEl
            {
                Width = SidebarRowGeometry.IconColumn, Height = height, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = spec.CheckLane is { } lane ? [lane, visual] : [visual],
            };
            if (spec.Tile)
                return Finish(in spec, new BoxEl
                {
                    Key = spec.Key, Animate = spec.Animate, OnRealized = spec.OnRealized,
                    Width = SidebarRowGeometry.TileWidth, Height = height, Shrink = 0f,
                    Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
                    Corners = Radii.ControlAll, AlignItems = FlexAlign.Center,
                    Children = [iconColumn],
                }, enabled, selected, activate, checksVisible);

            // ── the label column ──
            Element text = !hasSubtitle
                ? Body(spec.Label) with
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    Wrap = TextWrap.NoWrap, Color = ink,
                }
                : new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children =
                    [
                        Body(spec.Label) with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap, Color = ink, LineHeight = 18f },
                        Caption(spec.Subtitle!).Secondary() with { Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Wrap = TextWrap.NoWrap },
                    ],
                };

            // ── trailing: equalizer · pin mark · count/badge, ending at W − 18; the chevron column replaces the 14 pad ──
            bool overflow = spec.Overflow && enabled && spec.MenuOverlay is not null && spec.Menu is not null;
            int trailingCount = (spec.Playing ? 1 : 0) + (spec.Pinned ? 1 : 0) + (spec.Trailing is null ? 0 : 1);
            var kids = new List<Element>(5) { iconColumn, new BoxEl { Width = SidebarRowGeometry.LabelGap, Shrink = 0f }, text };
            if (trailingCount > 0)
            {
                var parts = new Element[trailingCount];
                int t = 0;
                if (spec.Playing) parts[t++] = Controls.Equalizer(spec.PlayingAnimated, Tok.AccentDefault, Controls.EqualizerH);
                if (spec.Pinned) parts[t++] = Icon(Icons.Pin, 12f, Tok.TextTertiary);
                if (spec.Trailing is { } trailingContent) parts[t] = trailingContent;
                kids.Add(new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = SidebarRowGeometry.TrailingGap,
                    Margin = new Edges4(SidebarRowGeometry.TrailingGap, 0f, 0f, 0f), Children = parts,
                });
            }
            bool chevron = spec.DisclosureChevron is not null;
            if (spec.DisclosureChevron is { } chev)
                kids.Add(new BoxEl
                {
                    Width = SidebarRowGeometry.ChevronColumn, Height = height, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [chev],
                });

            Element[] rowChildren = overflow
                ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Children = [.. kids] }, OverflowButton()]
                : [.. kids];

            var row = new BoxEl
            {
                Key = spec.Key,
                Animate = spec.Animate,
                OnRealized = spec.OnRealized,
                ZStack = overflow,
                Direction = 0, Height = height, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
                Padding = new Edges4(SidebarRowGeometry.IndentFor(spec.Depth), 0f, chevron ? 0f : SidebarRowGeometry.TrailingPad, 0f),
                Corners = Radii.ControlAll,
                Children = rowChildren,
            };
            return Finish(in spec, row, enabled, selected, activate, checksVisible);
        }

        /// <summary>The wiring every row shape shares: the neutral ramp (the multi-selection's quiet plate is the
        /// selected rest fill without a pill), disabled 0.3 (TR:501), the click / modifier-aware activation, the keys, the
        /// drag source, the drop target, the label tooltip and the context menu.</summary>
        static BoxEl Finish(in RowSpec spec, BoxEl row, bool enabled, bool selected, Action<KeyModifiers>? activate,
                            Func<bool>? checksVisible)
        {
            var own = SurfaceRules.Ownership(inSlot: false, hasClick: enabled && (activate is not null || spec.OnClick is not null));
            row = (row with
            {
                Opacity = enabled ? 1f : 0.3f,
                IsEnabled = enabled,
                Role = AutomationRole.NavigationItem,
                Cursor = SurfaceRules.Cursor(in own),
                OnClick = enabled && activate is null ? spec.OnClick : null,
                OnPointerReleased = enabled && activate is not null
                    ? args => activate!(args.ClickCount >= 2
                        ? KeyModifiers.None
                        : SelectorVisualsBound.MultiSelectMods(checksVisible?.Invoke() ?? false, args.Mods))
                    : null,
                Focusable = spec.Focusable || (enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null)),
                OnKeyDown = enabled && (spec.OnRename is not null || spec.OnMove is not null || activate is not null || spec.OnEscape is not null)
                    ? KeyHandler(spec.OnRename, spec.OnMove, activate, spec.OnEscape)
                    : null,
                Draggable = enabled && spec.Drag is { } payload ? Drag.Source(() => payload, clickPrimary: true) : null,
                DropTarget = spec.DropTarget,
            }).Interactive(NavRow(selected || spec.MultiSelected), isEnabled: enabled);
            if (spec.MenuOverlay is { } svc && spec.Menu is { } factory) row = row.WithContextMenu(svc, factory);
            return row;
        }
```

Notes for the coder: `List<Element>` → `[.. kids]` spreads to `Element[]`; `TrackArt` takes `SidebarRowShape` instead of
`SidebarDensity` (`shape == SidebarRowShape.EntityOneLine ? 10f : 14f` for the play glyph); the `DropActive` member and
its bound plate STAY, permanently: the folder flyout's rows set it (the rail's flyout in P1, `PaneFolderFlyout` from P2
on, §P2.5) — keep the two `Prop.Of(() => plateOn() …)` arms of the old code on `row` before `Interactive` when
`spec.DropActive` is not null. `HeightOf(in RowSpec)` becomes
`float.IsNaN(spec.Height) ? SidebarRowGeometry.HeightOf(spec.Shape) : spec.Height`.

The label tooltip: callers set `LabelTooltip = SidebarLabelFit.Overflows(label, SidebarLabelFit.LabelWidth(width, depth,
trailing))`; `Finish`'s caller (the slot) wraps with `ToolTip.Wrap(built, label, grow: 1f)` when `LabelTooltip` is true
(the slot owns the wrap so a tooltip is a sibling of the drop cues, not a child of the row).

**`SelectionPill`** — `PillW/PillH` come from `SidebarRowGeometry`; `Corners = CornerRadius4.All(SidebarRowGeometry.PillRadius)`.
`Margin = new Edges4(state.Indent, state.Top, 0f, 0f)` is unchanged (the slot passes the new numbers).

**`SectionHeader`** — replace `Header`, `Label`, `ExplicitDivider` with:

```csharp
    /// <summary>The 40-px section header (design V.6): the title at slot x 12 (pane 16), 14 SemiBold TextSecondary, brightening
    /// to TextPrimary on hover (no plate); then the "+" (always, Playlists / Your Library), the ⋯ (revealed on hover, permanent
    /// after a touch), and the chevron (24×24, always visible, ending at pane W − 4). A click anywhere collapses; the slot root
    /// is the roving focus stop (ToggleButton). <paramref name="onToggle"/> null ⇒ a heading with no chevron.</summary>
    internal static class SectionHeader
    {
        public const float Height = SidebarRowGeometry.HeaderHeight;

        public static Element Header(string title, bool open, Action<bool>? onToggle, Element? create, Element? more,
                                     Element? chevron)
        {
            var kids = new List<Element>(5)
            {
                new TextEl(title)
                {
                    Size = 14f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary,
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap,
                },
                new BoxEl { Grow = 1f },
            };
            if (create is not null) kids.Add(create);
            if (more is not null) kids.Add(more);
            if (onToggle is not null) kids.Add(chevron ?? Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 12f, Tok.TextSecondary));
            Action? click = null;
            if (onToggle is { } toggle) click = () => toggle(!open);
            return new BoxEl
            {
                Direction = 0, Height = SidebarRowGeometry.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 0f,
                Padding = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, 0f, 0f),
                Role = onToggle is null ? AutomationRole.None : AutomationRole.ToggleButton,
                Cursor = onToggle is null ? CursorId.Arrow : CursorId.Hand,
                OnClick = click,
                Children = [.. kids],
            };
        }

        /// <summary>A header's 24×24 inline button (⋯ / +): consumes its own click, hover plate FillSubtleSecondary.
        /// <paramref name="reveal"/> = hover-revealed (the ⋯ off touch).</summary>
        public static BoxEl InlineButton(string glyph, Action? onClick, bool reveal, string tooltipText)
            => new BoxEl
            {
                Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = onClick,
                Opacity = reveal ? 0f : 1f, HoverOpacity = 1f,
                BlocksDragArm = true,
                Children = [Icon(glyph, 12f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);

        /// <summary>The 8-px separator (TR:223, TR:247): a 1-px StrokeDividerDefault rule at y 3, bleeding over the list's
        /// 4-px inset so it spans the full pane width.</summary>
        public static Element Separator() => new BoxEl
        {
            Direction = 1, Height = SidebarRowGeometry.SeparatorHeight, Shrink = 0f,
            Margin = new Edges4(-SidebarRowGeometry.PaneEdge, 0f, -SidebarRowGeometry.PaneEdge, 0f),
            Padding = new Edges4(0f, SidebarRowGeometry.SeparatorLineTop, 0f, 0f),
            HitTestVisible = false,
            Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
        };
    }
```

(`BlocksDragArm` is `Element.BlocksDragArm`, `Element.cs:511`, the drag-arm barrier `DragController` checks.)
The two `PaneMetrics.RowInset` reads in this file are `SectionHeader.Header`'s `Padding = PaneMetrics.RowInset` (:924) and
`SectionHeader.ExplicitDivider`'s `Padding = PaneMetrics.RowInset` (:940); both go with the members this block replaces.
The `EditCard.Build` wrapper's `RowInsetLeft`/`RowInsetRight` padding (:1215) becomes
`new Edges4(0f, PlateInsetY, SidebarRowGeometry.TrailingPad, PlateInsetY)` (§P1.6 table). `Skeletons.Row` takes `SidebarRowShape shape` instead of
`(density, subtitle)` and pads `(SidebarRowGeometry.IconColumn * 0.5f - art * 0.5f, 0, 14, 0)`; `PinDropZone` keeps its
rest/active heights in P1 (P3 replaces it with the 36-px drop band).

### P1.4 The slot (`Shell/Sidebar.UI.Slot.cs`)

1. `PaneMetrics.ShapeOf(section)` (P1.5) replaces every `section.Opts.Density` / `PaneMetrics.RowHeight(section)` /
   `PaneMetrics.ArtSize(section)` pair: `Shape = shape, Height = SidebarRowGeometry.HeightOf(shape), ArtSize =
   SidebarRowGeometry.ArtOf(shape)`.
2. Depth: `Depth = row.Depth` for every entity/folder row. Delete `treeNode`, `treeDepth`, `baseDepth`, `TreeMaskOf`
   and the `TreeNode/TreeDepth/TreeContinuationMask` assignments (no guides).
3. Subtitles: `Subtitle = SidebarRowGeometry.SubtitleVisible(shape, "x") ? PaneText.SubtitleOf(in snapshot) : null`, where
   `PaneText.SubtitleOf` (Rows.cs) now formats `SidebarSubtitleRules.Of(in e)`:

```csharp
        public static string? SubtitleOf(in SidebarLibraryEntry e)
        {
            var s = SidebarSubtitleRules.Of(in e);
            return s.Kind switch
            {
                SidebarSubtitleKind.Songs => Strings.Sidebar.SongCount(s.Count),
                SidebarSubtitleKind.Items => Strings.Sidebar.V3.ItemCount(s.Count),
                SidebarSubtitleKind.Album => s.Detail.Length > 0 ? Loc.Get(Strings.Sidebar.V3.Kind.Album) + " · " + s.Detail : Loc.Get(Strings.Sidebar.V3.Kind.Album),
                SidebarSubtitleKind.Podcast => s.Detail.Length > 0 ? Loc.Get(Strings.Sidebar.V3.Kind.Show) + " · " + s.Detail : Loc.Get(Strings.Sidebar.V3.Kind.Show),
                SidebarSubtitleKind.Artist => Loc.Get(Strings.Sidebar.V3.Kind.Artist),
                SidebarSubtitleKind.Text => s.Detail,
                _ => null,
            };
        }
```

4. `Indicator(row, selected, depth, height, route)` builds the pill state as
   `new SidebarPillState(Route: route, Selected: selected, Indent: SidebarRowGeometry.PillX(depth), Top: SidebarRowGeometry.PillTop(height))`
   and the `DropPlate()` gets `Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY)` so
   the "into" plate covers the row, not its margin.
5. Folder rows and header rows get a pill too (rule 2 and 3 of §P1.2): `FolderRow` returns
   `Indicator(EntityRow.Create(in spec), _o.RowSelectsRoute(index, sel), row.Depth, height, _o.PillRouteOf(index, sel))`
   instead of `DropCueOverlay(...)`; the header returns `ZStack(header, Embed.Comp(() => new SelectionPill(owner, probe)))`
   with `_pillState = new SidebarPillState(_o.PillRouteOf(index, sel), _o.RowSelectsRoute(index, sel), 0f,
   (SidebarRowGeometry.HeaderHeight - SidebarRowGeometry.PillH) * 0.5f - SidebarRowGeometry.RowMarginY)`.
6. `HeaderRow` (rewritten; `Banded` deleted):

```csharp
        Element HeaderRow(SidebarSectionSpec section, in SidebarRow row, int index, IReadOnlyList<SidebarRow> rows)
        {
            string id = section.Id;
            var owner = _o;
            Element? create = null, more = null;
            if (section.Kind == SidebarSectionKind.PlaylistTree && _o.Config.HeaderCreate && _o.Config.OnCreatePlaylist is not null)
                create = Embed.Comp(() => new CreateButton(
                    owner.CreatePlaylist, menu: owner.CreateMenu, drop: owner.HeaderCreateDropSpec(),
                    dropActive: () => owner.HeaderCreateDropActive.Value, box: SidebarRowGeometry.HeaderButton, glyph: 12f))
                    with { Key = "tree-create" };
            if (_o.MenuOverlay is { } svc && _o.HeaderMenu(id) is { } menu)
                more = SectionHeader.InlineButton(Icons.More, null, reveal: !_o.TouchLast, Loc.Get(PaneLoc.SectionOptions))
                    .WithContextMenu(svc, menu) with { ClickRequestsContext = true };
            Action<bool>? toggle = null;
            Element? chevron = null;
            if (_o.Config.SetSectionCollapsed is not null && !SidebarIds.IsTopBar(id))
            {
                toggle = open => owner.ToggleSection(id, !open);
                chevron = new BoxEl
                {
                    Width = SidebarRowGeometry.HeaderButton, Height = SidebarRowGeometry.HeaderButton, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Chevron.Section(_headerOpen ??= HeaderOpenLive, identity: _sectionIdentity ??= SectionIdentity)],
                };
            }
            Element header = SectionHeader.Header(PaneText.TitleOf(section), !section.Collapsed, toggle, create, more, chevron);
            if (_o.FilterOpenFor(id)) header = new BoxEl { Direction = 1, Children = [header, _o.FilterBox()] };
            else if (!_o.Config.ReadOnly && section.Kind == SidebarSectionKind.EntityList && section.Opts.InlineControls && !section.Collapsed)
                header = new BoxEl { Direction = 1, Gap = 4f, Children = [header, InlineControls.Chips(_o, section)] };
            return header;
        }
```

   (`CreateButton` already takes `box:`/`glyph:` — `Sidebar.UI.Rows.cs:1055`.)
   `PaneLoc.SectionOptions = "sidebar.header.options"` (new loc key, "Section options"). The header's `⋯` menu is
   `PaneView.HeaderMenu(sectionId)` (§P1.5): Collapse/Expand in P1; P4 replaces its body with `SidebarMenuModel.Header`.
   The quick layout menu button on the first header is gone (`MenuHostSectionId` and `LayoutMenu.Button` callers deleted;
   the pane background's context menu still opens `LayoutMenu.Model` until P2's footer owns it).
7. `Divider` rows render `SectionHeader.Separator()`; `HeaderLabel` renders `SectionHeader.Header(title, true, null, null,
   null, null)`.
8. Every built row is wrapped `ToolTip.Wrap(built, label, grow: 1f)` when its `RowSpec.LabelTooltip` is true; the
   slot computes it with `SidebarLabelFit.Overflows(label, SidebarLabelFit.LabelWidth(Sidebar.Width.Peek(), row.Depth,
   trailingWidth))` where `trailingWidth` is 40 for a folder, 28 when a count is shown, else 0.

### P1.5 The pane (`Shell/Sidebar.UI.cs`)

`PaneMetrics` (replace the class):

```csharp
    /// <summary>The pane's one inset (the list's 4-px WinUI item margin + the 3-px content-grid top) and the shape of
    /// each section's rows. Every band above or below the list reproduces <see cref="PanePad"/>'s horizontal 4.</summary>
    internal static class PaneMetrics
    {
        public static readonly Edges4 PanePad = new(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f);
        public const float PaneInsetH = SidebarRowGeometry.PaneEdge * 2f;
        /// <summary>A band above the list whose content starts at the header text's x (pane 16).</summary>
        public static readonly Edges4 HeadBandInset = new(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.HeaderTextX, 0f,
                                                          SidebarRowGeometry.PaneEdge + SidebarRowGeometry.TrailingPad, 0f);
        public const float EmptyHintHeight = SidebarRowGeometry.EmptyHintHeight;
        public const float GridCellMax = 160f;

        /// <summary>A section's ONE row shape (P1: from its display options; P3: from the catalogue + density).</summary>
        public static SidebarRowShape ShapeOf(SidebarSectionSpec section)
            => SidebarRowGeometry.ShapeFor(section.Opts.Density, section.Opts.Subtitles, section.Opts.Artwork);
        public static float RowHeight(SidebarSectionSpec section) => SidebarRowGeometry.HeightOf(ShapeOf(section));
        public static float RowPitch(SidebarSectionSpec section) => SidebarRowGeometry.PitchOf(ShapeOf(section));
        public static float ArtSize(SidebarSectionSpec section) => SidebarRowGeometry.ArtOf(ShapeOf(section));
        public static float CardHeight(SidebarSectionSpec section) => SidebarRowGeometry.CardHeightFor(section.Opts.Density);
        public const float EditCardHeight = 44f;
    }
```

(`RowInset`, `BandInset`, `LeadInset`, `LeadBandInset`, `ContentLane`, `ContentLaneEnd`, `SectionGap`, `HeaderBodyGap`
are deleted; callers use `HeadBandInset` or the geometry constants. The reorder band extent in `RebuildIndex` becomes
`bandExtent = PaneMetrics.RowPitch(section)`.)

`PaneView` additions:

```csharp
        // ── the pill target (design V.5) ──
        SidebarPillTarget _pillTarget = SidebarPillTarget.None;
        readonly List<string> _ancestorScratch = new(4);
        InputHooks? _hooks;

        /// <summary>The last pointer was a finger: header ⋯ buttons stay visible (design V.6).</summary>
        internal bool TouchLast => _hooks?.LastPointerWasTouch?.Invoke() ?? false;

        /// <summary>Recompute the one pill target for <paramref name="route"/> over the PUBLISHED plan.</summary>
        void ResolvePillTarget(string route)
        {
            SidebarPillRules.AncestorFolders(Binder?.CurrentInput.PlaylistTree, route, _ancestorScratch);
            _pillTarget = SidebarPillRules.Resolve(Plan.Rows, Plan.Entries, i => RowRouteOf(i), route,
                _ancestorScratch, OwningSection(route));
        }

        /// <summary>The route a pill drawn on row <paramref name="index"/> registers under: the live route when the row is
        /// the pill's ancestor / header anchor, the row's own route otherwise.</summary>
        internal string? PillRouteOf(int index, string liveRoute)
            => index == _pillTarget.PlanIndex && _pillTarget.Anchor != SidebarPillAnchor.Row ? liveRoute : RowRouteOf(index);
```

`_hooks = UseContext(InputHooks.Current);` is added at the top of `Render` (hooks run in a stable order: add it after
`MenuOverlay = …`). `RowRouteOf(int index)` returns the route the row navigates to (the `SidebarRowResolve` route of the
row, extracted from `RowSelectsRoute`'s current body). `OwningSection(route)` (P1, old model): the first visible section
that would hold `route` — a Pinned section when `IsPinned(SidebarPinId.FromRoute(route))`, a section whose `ItemList`
has an item with `Key == route`, else the first visible `PlaylistTree` section when the route is a playlist/folder id
present in `Binder?.CurrentInput.PlaylistTree`; null otherwise.

`RowSelectsRoute(index, route)` becomes `index == _pillTarget.PlanIndex` after the existing `SidebarRowResolve` test
fails (`return SidebarRowResolve.SelectsRoute(...) || (index == _pillTarget.PlanIndex && _pillTarget.Anchor != SidebarPillAnchor.Row);`).
`RefreshSelection` calls `ResolvePillTarget(route)` before its sweep and, after the sweep, adds `_pillTarget.PlanIndex`
to `next` when it is ≥ 0 and absent. `PublishStage` calls `ResolvePillTarget(SelectedRoutePeek)` after `RebuildIndex`.

`PlanList()`'s `ListOptions` gains (the `Disclosure` block is NOT edited):

```csharp
                IsItemEnabled = IsRowFocusStop,
                ItemText = RowTypeAheadText,
                IsItemInvokedEnabled = true,
                OnInvoked = InvokeRow,
                KeepAlive = KeepFilterRow,
```

```csharp
        bool IsRowFocusStop(int index)
        {
            var rows = Plan.Rows;
            return (uint)index < (uint)rows.Count && SidebarTypeAheadRules.IsFocusStop(rows[index].Kind);
        }

        string RowTypeAheadText(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return "";
            var row = rows[index];
            return SidebarTypeAheadRules.TextOf(row.Kind, RowLabelOf(in row));
        }

        /// <summary>Enter (and a double tap) on the roving stop: a header toggles, a folder toggles, a row runs its click.</summary>
        void InvokeRow(int index)
        {
            var rows = Plan.Rows;
            if ((uint)index >= (uint)rows.Count) return;
            var row = rows[index];
            switch (row.Kind)
            {
                case SidebarRowKind.SectionHeader when SectionOf(row.SectionId) is { } s:
                    ToggleSection(s.Id, !s.Collapsed);
                    break;
                case SidebarRowKind.FolderHeader when (uint)row.EntryIndex < (uint)Plan.Entries.Count:
                    var folder = Plan.Entries[row.EntryIndex];
                    ActivateFolder(folder.FolderId, folder.Name, index);
                    break;
                default:
                    if (RowRouteOf(index) is { Length: > 0 } route) Navigate(route, null);
                    else if ((uint)row.EntryIndex < (uint)Plan.Entries.Count && Plan.Entries[row.EntryIndex].IsTrack)
                        Play(Plan.Entries[row.EntryIndex].Uri, asTrack: true);
                    break;
            }
        }

        /// <summary>The Classic filter box's header row stays realized while it is open (its editor keeps caret, focus and IME
        /// composition through a scroll).</summary>
        bool KeepFilterRow(int index)
        {
            var rows = Plan.Rows;
            return _filterSectionId is { } id && (uint)index < (uint)rows.Count
                   && rows[index].Kind == SidebarRowKind.SectionHeader && string.Equals(rows[index].SectionId, id, StringComparison.Ordinal);
        }
```

`RowLabelOf(in SidebarRow)` returns the header's `PaneText.TitleOf(section)`, an entry's `Name`, or a route's
`Shell.Dest(Shell.Parse(key)).Title`.

**The Classic Ctrl+F filter box.** `PaneView` owns `string? _filterSectionId` and reuses the existing pane-owned `_search`
signal. `FilterOpenFor(sectionId)` is `string.Equals(_filterSectionId, sectionId, Ordinal)`. The pane root gets
`OnKeyDown = OnPaneKey`:

```csharp
        void OnPaneKey(KeyEventArgs e)
        {
            if (e.Handled || e.KeyCode != Keys.F || e.Mods != KeyModifiers.Ctrl) return;
            string? tree = FirstVisibleSectionId(SidebarSectionKind.PlaylistTree);
            if (tree is null) return;
            _filterSectionId = tree;
            _filterFocus.Value = _filterFocus.Peek() + 1;   // the box's focus request
            RepublishNow();
            e.Handled = true;
        }

        /// <summary>The transient filter (design V.11): a 32-px TextBox under the Playlists header, focused on open; Esc, or
        /// leaving it empty on blur, closes it and clears the query.</summary>
        internal Element FilterBox() => Embed.Comp(() => new PaneFilterBox(this)) with { Key = "pane-filter" };

        internal void CloseFilter()
        {
            if (_filterSectionId is null) return;
            _filterSectionId = null;
            _search.Value = "";
            RepublishNow();
        }
```

`PaneFilterBox` (new nested component in `Sidebar.UI.cs`): a `TextBox.Create(owner._search, …)` 32 tall with
`Margin = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, SidebarRowGeometry.TrailingPad, 4f)`, placeholder
`Strings.Sidebar.V3.SearchPlaceholder`, a `UseEffect` that focuses its node when `owner._filterFocus` moves, an
`OnKeyDown` that calls `owner.CloseFilter()` on Escape, and a blur handler that closes when the text is empty. The
planner already honours `input.Search` for the PlaylistTree (it flattens).

`EmptyPane()`'s CTA (`Config.OnCustomize`) is unchanged in P1.

### P1.6 Mechanical constant fixes in the files P3 or P5 replace

Every site below reads a member P1.1 / P1.3 / P1.5 deletes (`RowSpec.Density`, `SidebarRowGeometry.ArtX` / `ArtFor` /
`HeightFor`, `PaneMetrics.ContentLane` / `ContentLaneEnd` / `LeadInset` / `LeadBandInset`). Two identities drive the
replacements: `ArtX(d)` ≡ `SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(d)` (the icon column now starts at
the indent, no gutter), and the old 12-px content lane (pane space) becomes the header text's x: pane
`SidebarRowGeometry.PaneEdge + SidebarRowGeometry.HeaderTextX` (16), slot `SidebarRowGeometry.HeaderTextX` (12).
Line numbers are today's; locate by the quoted text.

**`Shell/Sidebar.UI.LibraryV3.cs`** (P1-WP6):

| site | today | becomes |
|---|---|---|
| `Body()`, the `"v3-chrome-rule"` divider (:582) | `Margin = new Edges4(PaneMetrics.ContentLane, 4f, PaneMetrics.ContentLaneEnd, 4f)` | `Margin = new Edges4(PaneMetrics.HeadBandInset.Left, 4f, PaneMetrics.HeadBandInset.Right, 4f)` |
| `Breadcrumb()` (:607) | `Padding = new Edges4(PaneMetrics.ContentLane - 6f, 0f, PaneMetrics.ContentLaneEnd, 0f)` | `Padding = new Edges4(PaneMetrics.HeadBandInset.Left - 6f, 0f, PaneMetrics.HeadBandInset.Right, 0f)` |
| `V3NavBand.Render`, the `"v3-nav-rule"` divider (:762-763) | `Margin = new Edges4(PaneMetrics.ContentLane - SidebarRowGeometry.PaneEdge, 4f, PaneMetrics.ContentLaneEnd - SidebarRowGeometry.PaneEdge, 4f)` | `Margin = new Edges4(SidebarRowGeometry.HeaderTextX, 4f, SidebarRowGeometry.TrailingPad, 4f)` (the band already pads `PaneEdge`) |
| `V3ChipRail` destination words scroller (:838) | `Padding = new Edges4(SidebarRowGeometry.ArtX(0) - SidebarRowGeometry.PaneEdge, 0f, 0f, 0f)` | `Padding = new Edges4(SidebarRowGeometry.IndentFor(0), 0f, 0f, 0f)` (= `ArtX(0) − PaneEdge` under the identity; 0) |
| `RouteTile` (:930) | `Density = SidebarDensity.Cozy,` | `Shape = SidebarRowShape.Glyph,` |
| `ActionTile` (:951) | `Density = SidebarDensity.Cozy,` | `Shape = SidebarRowShape.Glyph,` |
| `ActionTile` leading (:954) | `PaneIcon.Leading(item.IconOverride, a.Icon, a.Enabled, SidebarRowGeometry.ArtFor(SidebarDensity.Cozy))` | `PaneIcon.Leading(item.IconOverride, a.Icon, a.Enabled, SidebarRowGeometry.ArtOf(SidebarRowShape.Glyph))` |
| `TrackTile` (:971) | `Density = SidebarDensity.Cozy,` | `Shape = SidebarRowShape.EntityTwoLine,` |
| `EntityTile` (:993) | `Density = SidebarDensity.Cozy,` | `Shape = SidebarRowShape.EntityTwoLine,` |
| `V3HeaderBand` (:1083), `V3ToolbarBand` (:1129), the sort panel band (:1380) | `Padding = PaneMetrics.LeadBandInset,` | `Padding = PaneMetrics.HeadBandInset,` |
| `V3SearchHost` open width (:1180) | `Sidebar.Width.Value, PaneMetrics.LeadInset + PaneMetrics.ContentLaneEnd` | `Sidebar.Width.Value, PaneMetrics.HeadBandInset.Horizontal` |
| every other `PaneMetrics.ContentLaneEnd` | | `PaneMetrics.HeadBandInset.Right` |

`PaneMetrics.PaneInsetH` and `SidebarRowGeometry.PaneEdge` (now 4) reads stay as they are. Fix the one comment that
names `ArtX(0)` (`V3NavBand.Render`, "lands their glyph on ArtX(0)") to "on the icon column".

**`Shell/Sidebar.UI.Rail.cs`** (P1-WP6), the folder flyout row (`RailFolderFlyout`'s `EntityRow.Create(new RowSpec { … })`, :515-539):

```csharp
                Shape = SidebarRowShape.EntityTwoLine,                                                  // was Density = SidebarDensity.Cozy
                Leading = Cover.ForEntry(in entry, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityTwoLine)),  // was ArtFor(Cozy)
```

`DropActive = drop is null ? null : () => owner.IsRailDropActive(cueKey),` (:538) stays: P1.3 keeps `RowSpec.DropActive`
and its bound plate, and P2 moves this row (with this line) into `PaneFolderFlyout` (§P2.5).

**`Shell/Sidebar.Customizer.UI.cs`** (P1-WP6), both `SidebarRowGeometry.HeightFor(section.Opts.Density,
section.Opts.Subtitles)` reads (:2347, :2387) → `PaneMetrics.RowHeight(section)`.

**`Shell/Sidebar.Resize.cs`** (P1-WP6): the two `SidebarRowGeometry.PaneEdge` reads (:41, :43) become a local
`const float RailEdge = 8f;` (the detents keep their 48/56/80 until P2 deletes them).

`Shell/Sidebar.Cards.cs`: `PaneMetrics.CardHeight(section)` (:142) is unchanged (P1.5 keeps it).

**Sites inside the WP files that own the rewrites** (listed so nothing is missed; each WP fixes its own file):

| file (WP) | site | becomes |
|---|---|---|
| `Sidebar.UI.Rows.cs` (P1-WP3) | `RowSpec` ctor (:50) `TreeDepth = 0; TreeContinuationMask = 0; Density = SidebarDensity.Cozy; Gap = float.NaN;` | `Shape = SidebarRowShape.EntityTwoLine;` |
| `Sidebar.UI.Rows.cs` (P1-WP3) | `SectionHeader.Header` `Padding = PaneMetrics.RowInset` (:924) and `SectionHeader.ExplicitDivider` `Padding = PaneMetrics.RowInset` (:940) | deleted with the two members (§P1.3 `SectionHeader`) |
| `Sidebar.UI.Rows.cs` (P1-WP3) | `EditCard.Build` wrapper (:1215) `Padding = new Edges4(SidebarRowGeometry.RowInsetLeft, PlateInsetY, SidebarRowGeometry.RowInsetRight, PlateInsetY)` | `Padding = new Edges4(0f, PlateInsetY, SidebarRowGeometry.TrailingPad, PlateInsetY)` |
| `Sidebar.UI.Rows.cs` (P1-WP3) | `Skeletons.Row` (:865-866) `HeightFor` / `ArtFor` | `SidebarRowShape shape` parameter (§P1.3) |
| `Sidebar.UI.Slot.cs` (P1-WP4) | `Banded` (:195, :201 `SectionGap` / `HeaderBodyGap`) | deleted with `Banded` (§P1.4.6) |
| `Sidebar.UI.Slot.cs` (P1-WP4) | `EmptyRow` hint (:758) `Padding = PaneMetrics.RowInset,` | `Padding = new Edges4(SidebarRowGeometry.HeaderTextX, 0f, SidebarRowGeometry.TrailingPad, 0f),` |
| `Sidebar.UI.Slot.cs` (P1-WP4) | the eight `Density = section.Opts.Density,` (:363, :444, :480, :545, :575, :607, :638, :750) | `Shape = PaneMetrics.ShapeOf(section),` |
| `Sidebar.UI.Slot.cs` (P1-WP4) | `Skeletons.Row(index, section.Opts.Density, section.Opts.Subtitles, …)` (:84) | `Skeletons.Row(index, PaneMetrics.ShapeOf(section), …)` |
| `Sidebar.UI.Slot.cs` (P1-WP4) | `Indicator` fallback (:946) `SidebarRowGeometry.ClassicHeight` | `SidebarRowGeometry.HeightOf(SidebarRowShape.EntityTwoLine)` |
| `Sidebar.UI.cs` (P1-WP5) | `SidebarRowGeometry.ClassicHeight` (:572 estimate, :1040 `bandExtent`, :1596, :1598, :1627, :1630, :1632) | `SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine)`, except `:1040` which is `PaneMetrics.RowPitch(section)` (§P1.5) |

### P1.7 Tests

New `SidebarRowGeometryTests.cs`:

```csharp
namespace Wavee.Tests;

public sealed class SidebarRowGeometryTests
{
    [Fact] public void Pitches_AreWinUi() 
    {
        Assert.Equal(36f, SidebarRowGeometry.HeightOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph));
        Assert.Equal(40f, SidebarRowGeometry.HeightOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(44f, SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(40f, SidebarRowGeometry.PitchOf(SidebarRowShape.EntityOneLine));
    }

    [Fact] public void Art_Is16_32_24()
    {
        Assert.Equal(16f, SidebarRowGeometry.ArtOf(SidebarRowShape.Glyph));
        Assert.Equal(32f, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityTwoLine));
        Assert.Equal(24f, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityOneLine));
    }

    [Fact] public void Rulers_IconCentre24_Label48_Trailing18_Chevron44()
    {
        Assert.Equal(24f, SidebarRowGeometry.IconCentreX);
        Assert.Equal(48f, SidebarRowGeometry.LabelX);
        Assert.Equal(302f, SidebarRowGeometry.TrailingRight(320f));
        Assert.Equal(276f, SidebarRowGeometry.ChevronLeft(320f));
    }

    [Theory]
    [InlineData(0, 0f)] [InlineData(1, 31f)] [InlineData(3, 93f)] [InlineData(7, 93f)] [InlineData(-1, 0f)]
    public void Indent_Is31PerLevel_CappedAt3(int depth, float expected) => Assert.Equal(expected, SidebarRowGeometry.IndentFor(depth));

    [Fact] public void Pill_StartsAtTheIndent_CentredBelowTheMargin()
    {
        Assert.Equal(31f, SidebarRowGeometry.PillX(1));
        Assert.Equal(12f, SidebarRowGeometry.PillTop(36f));     // 2 + (36 − 16) / 2
        Assert.Equal(14f, SidebarRowGeometry.PillTop(40f));
    }

    [Fact] public void Caret_StartsWhereThePillDoes() => Assert.Equal(SidebarRowGeometry.PillX(2), SidebarRowGeometry.TreeContentX(2));

    [Fact] public void Subtitle_OnlyInTheTwoLineShape()
    {
        Assert.True(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, "48 songs"));
        Assert.False(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityOneLine, "48 songs"));
        Assert.False(SidebarRowGeometry.SubtitleVisible(SidebarRowShape.EntityTwoLine, ""));
    }

    [Fact] public void Chrome_Header40_Separator8()
    {
        Assert.Equal(40f, SidebarRowGeometry.HeaderHeight);
        Assert.Equal(8f, SidebarRowGeometry.SeparatorHeight);
        Assert.Equal(48f, SidebarRowGeometry.RailWidth);
    }
}
```

New `SidebarPillRulesTests.cs` (helpers build `SidebarRow`/`SidebarLibraryEntry` with the same factory the planner tests
use; `Folder(id, depth)` = an entry with `Kind = Folder`, `FolderId = id`, `Depth = depth`; `Playlist(id, depth)`):

- `VisibleRow_TakesThePill` — rows [header P, entity "pl:a"], route "pl:a" → (1, Row).
- `CollapsedFolder_PillOnTheDeepestVisibleAncestor` — tree [folder f1 d0, folder f2 d1, playlist pl:x d2]; plan shows
  only the f1 and f2 folder rows; ancestors of pl:x = [f2, f1]; resolve → (index of f2, AncestorFolder).
- `CollapsedSection_PillOnItsHeader` — plan [header "s1"], owning section "s1", route absent → (0, SectionHeader).
- `HiddenSection_NoPill` — no row, no ancestor, owning null → `None`.
- `EmptyRoute_NoPill`.
- `AncestorFolders_DeepestFirst_SkipsSiblings` — tree [f1 d0, pl:y d1, f2 d1, pl:x d2] → [f2, f1].

New `SidebarSubtitleRulesTests.cs`:

- playlist `CountKnown` 48 songs → `(Songs, 48)`; `HasEpisodes` → `(Items, 48)`; `CountKnown = false` → `None`;
- album with `FirstArtistName = "Frank Ocean"` → `(Album, 0, "Frank Ocean")`; album with only `Creator` → its creator;
- show with publisher → `(Podcast, 0, publisher)`; artist → `(Artist, 0, "")`; folder `CountKnown` 5 → `(Items, 5)`;
  folder unknown → `None`; route with empty creator → `None`.

New `SidebarTypeAheadRulesTests.cs`: focus stops are exactly `SectionHeader, IconRow, EntityRow, FolderHeader,
Placeholder`; `TextOf(Divider, "x") == ""`; `TextOf(EntityRow, "Running") == "Running"`; and a `SidebarLabelFit`
fact: `Overflows("A very long playlist name that cannot fit", LabelWidth(240, 0, 0))` is true and
`Overflows("Jazz", LabelWidth(320, 0, 28))` is false.

Edited: `SidebarPlannerTests.cs` — the geometry facts that assert 44/40/28/16 numbers (search `ClassicHeight`,
`HeaderHeight`, `DividerHeight`, `SectionGap`, `HeightFor`) assert the new numbers via `PitchOf`/`HeaderHeight`/
`SeparatorHeight`; `BandTop` facts are deleted. `SidebarDropTests.cs` — caret/x→depth facts use `IndentStep = 31` and
`TreeContentX(d) = 31·d`; `LineWidth(w, d) == w − 31·d`. `SidebarCardsTests.cs:224` unchanged (`CardHeightFor` kept).

### P1 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P1-WP1 | `Shell\Sidebar.cs` | §P1.1 (geometry, extents, drop cue, resolver step, `HasEpisodes` + projection stamp) | — |
| P1-WP2 | new `Shell\Sidebar.Rules.cs` | §P1.2 | P1-WP1 |
| P1-WP3 | `Shell\Sidebar.UI.Rows.cs` | §P1.3 + `PaneText.SubtitleOf` from §P1.4.3 + the §P1.6 Rows.cs rows (`RowSpec` ctor, `EditCard.Build` padding; never `Chevron`) | P1-WP1, P1-WP2 |
| P1-WP4 | `Shell\Sidebar.UI.Slot.cs` | §P1.4 + the §P1.6 Slot.cs rows (`EmptyRow` padding, the eight `Shape =`, `Skeletons.Row`, `Indicator` fallback) | P1-WP1, P1-WP2, P1-WP3 |
| P1-WP5 | `Shell\Sidebar.UI.cs` | §P1.5 (`PaneMetrics`, pill target, ListOptions, filter box, `OnPaneKey` Ctrl+F, `HeaderMenu` = Collapse/Expand) + the §P1.6 `ClassicHeight` rows; never the disclosure members (§1) | P1-WP1, P1-WP2, P1-WP3 |
| P1-WP6 | `Shell\Sidebar.UI.LibraryV3.cs`, `Shell\Sidebar.Customizer.UI.cs`, `Shell\Sidebar.UI.Rail.cs`, `Shell\Sidebar.Resize.cs`, `assets\loc\en-US.json` | §P1.6 (the LibraryV3 table incl. the four tiles' `Shape =`, `ArtX`, `ContentLane`, `LeadInset`, `LeadBandInset`; the Rail flyout row; Customizer; Resize), loc key `sidebar.header.options` | P1-WP1 |
| P1-WP7 | new `SidebarRowGeometryTests.cs`, `SidebarPillRulesTests.cs`, `SidebarSubtitleRulesTests.cs`, `SidebarTypeAheadRulesTests.cs`; `SidebarPlannerTests.cs`, `SidebarDropTests.cs` | §P1.7 | P1-WP1, P1-WP2, P1-WP3 |

---

## P2. Modes, rail, footer, title bar

The window decides one of three **bands**; the band and the user's collapse decide the **mode**; the mode decides the
presented width; the right rail reads the presented width. The 48-px rail becomes the same list (the planner's compact
arm), the old `Sidebar.UI.Rail.cs` and the three detents go, and a footer carries Settings and the pane `⋯`.

### P2.1 The rules (`Shell/Sidebar.Resize.cs`, rewritten)

Delete `SidebarRailDetent`, `SidebarRegime`, `SidebarRailMetrics` and today's `SidebarResizeRules`. New file body
(header comment: "the sidebar's pane modes and seam rules — one pure rule class each; engine-free"):

```csharp
namespace Wavee;

/// <summary>What the pane presents (WinUI NavigationView's display modes): Expanded (inline, pushes content —
/// CompactInline), Compact (the 48-px rail, inline), Minimal (no rail; the toggle opens the overlay drawer).</summary>
public enum SidebarPaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>The window's width band, hysteretic. Wide: the user's collapse decides. Narrow (528 ≤ w &lt; 660, leave at 700):
/// a FORCED rail whose toggle opens the overlay pane. Tiny (w &lt; 528 = 48 + 480, leave at 568): no rail, the drawer.</summary>
public enum SidebarWindowBand : byte { Wide = 0, Narrow = 1, Tiny = 2 }

/// <summary>THE MODE RULES (design V.1). The pane reads the viewport ONLY; the right rail reads the pane's presented width
/// (never the reverse), so opening the rail or docking a video never moves the sidebar. A forced mode never writes the
/// user's collapse (WinUI's <c>m_wasForceClosed</c>, NavigationView.cpp:1491, 1658-1670): when the window grows back the
/// user's own state returns. Edit mode presents Expanded whatever <c>userCollapsed</c> says.</summary>
public static class SidebarPaneModeRules
{
    /// <summary>The content floor (<c>Shell.MinContentW</c>).</summary>
    public const float ContentFloorW = 480f;
    public const float NarrowEnterW = 660f;   // below: forced rail
    public const float WideEnterW = 700f;     // the Narrow → Wide edge (hysteresis 40)
    public const float TinyEnterW = 528f;     // below: no rail (48 + 480)
    public const float TinyLeaveW = 568f;     // the Tiny → Narrow edge (hysteresis 40)

    /// <summary>The next band from the viewport width and the current band. A non-positive or non-finite width (a
    /// minimized window) never moves it.</summary>
    public static SidebarWindowBand BandOf(float viewportW, SidebarWindowBand current)
    {
        if (!float.IsFinite(viewportW) || viewportW <= 0f) return current;
        return current switch
        {
            SidebarWindowBand.Wide => viewportW >= NarrowEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyEnterW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            SidebarWindowBand.Narrow => viewportW >= WideEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyEnterW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            _ => viewportW >= WideEnterW ? SidebarWindowBand.Wide
                : viewportW >= TinyLeaveW ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
        };
    }

    /// <summary>The first band of a launch (no history): asked from Wide, so only the entering thresholds apply (a fresh
    /// launch at 600 is Narrow, at 500 Tiny).</summary>
    public static SidebarWindowBand InitialBand(float viewportW) => BandOf(viewportW, SidebarWindowBand.Wide);

    /// <summary>① The mode. Narrow ⇒ Compact and Tiny ⇒ Minimal whatever the user or Edit mode wants (Edit then pins the
    /// OVERLAY open instead); Wide ⇒ the user's collapse, unless editing.</summary>
    public static SidebarPaneMode Resolve(SidebarWindowBand band, bool userCollapsed, bool editing) => band switch
    {
        SidebarWindowBand.Tiny => SidebarPaneMode.Minimal,
        SidebarWindowBand.Narrow => SidebarPaneMode.Compact,
        _ => editing || !userCollapsed ? SidebarPaneMode.Expanded : SidebarPaneMode.Compact,
    };

    /// <summary>② The inline column's width: Expanded clamps the preference to [180, 460] and to the content floor;
    /// Compact is the 48 rail; Minimal is nothing.</summary>
    public static float PresentedWidth(SidebarPaneMode mode, float preferredW, float viewportW) => mode switch
    {
        SidebarPaneMode.Expanded => Math.Clamp(
            float.IsFinite(viewportW) && viewportW > 0f ? MathF.Min(preferredW, viewportW - ContentFloorW) : preferredW,
            SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW),
        SidebarPaneMode.Compact => SidebarRowGeometry.RailWidth,
        _ => 0f,
    };

    /// <summary>The overlay pane's width (the drawer in Minimal, the pane opened over a forced rail in Narrow): the
    /// preference, leaving at least the 48-px rail's worth of page visible.</summary>
    public static float OverlayWidth(float preferredW, float viewportW)
        => MathF.Max(0f, MathF.Min(SidebarPaneBounds.Clamp(preferredW), viewportW - SidebarRowGeometry.RailWidth));

    /// <summary>Does a toggle / seam / double-click write <c>sidebar.pane.userCollapsed</c>? Only in the Wide band and
    /// never while editing.</summary>
    public static bool WritesUserCollapsed(SidebarWindowBand band, bool editing) => band == SidebarWindowBand.Wide && !editing;

    /// <summary>Is there an overlay pane to open (the toggle's job in Narrow and Tiny)?</summary>
    public static bool HasOverlay(SidebarWindowBand band) => band != SidebarWindowBand.Wide;

    /// <summary>Edit mode outside the Wide band pins the overlay open (no light dismiss, no leaf-invoke close).</summary>
    public static bool OverlayPinned(SidebarWindowBand band, bool editing) => editing && band != SidebarWindowBand.Wide;

    /// <summary>The seam exists only in the Wide band (a forced rail and the drawer have no width to drag).</summary>
    public static bool SeamVisible(SidebarWindowBand band) => band == SidebarWindowBand.Wide;

    /// <summary>A leaf navigation closes an OVERLAY pane (NavigationView.cpp:4840-4846), never the inline one, and never
    /// while editing.</summary>
    public static bool LeafInvokeClosesOverlay(bool editing) => !editing;

    /// <summary>③ The right rail fits inline beside the presented pane and the content floor.</summary>
    public static bool RailFits(float presentedW, float railW, float viewportW)
        => presentedW + railW + ContentFloorW <= viewportW;
}

/// <summary>THE SEAM RULES (design V.1 "Seam"): drag resizes 180-460; below 116 the pane collapses to the rail (writes
/// the user's collapse); from the rail, past 140 it expands; while editing it resizes but never flips (clamped at 180).
/// Double-click / Enter toggles; ←/→ step 8 (Shift 40). No detents, no flick.</summary>
public static class SidebarResizeRules
{
    public const float ExpandedMinW = SidebarPaneBounds.NavPaneMinW;             // 180
    public const float ExpandedMaxW = SidebarPaneBounds.NavPaneMaxW;             // 460
    public const float RegimePush = 64f;
    public const float RegimeHysteresis = SidebarPaneBounds.NavPaneHysteresisDip; // 24
    public const float MinFade = 0.35f;
    public const float NudgeW = 8f;
    public const float NudgeLargeW = 40f;
    public static float RailEnterW => ExpandedMinW - RegimePush;                  // 116
    public static float ExpandedEnterW => RailEnterW + RegimeHysteresis;          // 140

    /// <summary>The user's stored facts.</summary>
    public readonly record struct State(bool UserCollapsed, float ExpandedWidth);
    /// <summary>What the frame presents mid-drag.</summary>
    public readonly record struct Live(bool Collapsed, float PresentedWidth, float Fade);
    /// <summary>The release verdict; <see cref="TargetWidth"/> is what the column animates to.</summary>
    public readonly record struct Settle(bool UserCollapsed, float ExpandedWidth, float TargetWidth);

    public static Live Track(float raw, in State prev, bool editing)
    {
        if (!float.IsFinite(raw))
            return new(prev.UserCollapsed, prev.UserCollapsed ? SidebarRowGeometry.RailWidth : prev.ExpandedWidth, 1f);
        bool collapsed = !editing && (prev.UserCollapsed ? raw < ExpandedEnterW : raw < RailEnterW);
        if (collapsed) return new(true, SidebarRowGeometry.RailWidth, 1f);
        float presented = Math.Clamp(raw, ExpandedMinW, ExpandedMaxW);
        float fade = editing || raw >= ExpandedMinW ? 1f : SplitterFade(ExpandedMinW - raw, RegimePush);
        return new(false, presented, fade);
    }

    public static Settle Resolve(float raw, in State prev, bool editing)
    {
        var live = Track(raw, in prev, editing);
        return live.Collapsed
            ? new(true, prev.ExpandedWidth, SidebarRowGeometry.RailWidth)
            : new(false, live.PresentedWidth, live.PresentedWidth);
    }

    public static Settle Step(in State s, int direction, bool large, bool editing)
    {
        if (s.UserCollapsed)
        {
            if (direction <= 0 || editing) return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
            float back = Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW);
            return new(false, back, back);
        }
        float next = s.ExpandedWidth + direction * (large ? NudgeLargeW : NudgeW);
        if (!editing && next < ExpandedMinW && s.ExpandedWidth <= ExpandedMinW)
            return new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);
        float w = Math.Clamp(next, ExpandedMinW, ExpandedMaxW);
        return new(false, w, w);
    }

    public static Settle Toggle(in State s) => s.UserCollapsed
        ? new(false, s.ExpandedWidth, Math.Clamp(s.ExpandedWidth, ExpandedMinW, ExpandedMaxW))
        : new(true, s.ExpandedWidth, SidebarRowGeometry.RailWidth);

    /// <summary>SplitterMath.Fade restated (CORE must not reference FluentGpu.Controls): 1 at 0, MinFade at <paramref name="over"/>.</summary>
    static float SplitterFade(float into, float over)
    {
        if (over <= 0f) return 1f;
        float t = Math.Clamp(into / over, 0f, 1f);
        return 1f - t * (1f - MinFade);
    }
}
```

### P2.2 The service (`Shell/Sidebar.Host.cs`, the "pane state" block)

Replace `Regime`, `RailDetent`, `ResizeState`, `CommitSeam(float)`, `ToggleRegime`, `SetRegime`, `StepSeam`,
`SetRailDetent`, `Apply` with:

```csharp
    /// <summary>The user's collapse (the 48 rail). Written ONLY by a toggle / seam / double-click in the Wide band while
    /// not editing (<see cref="SidebarPaneModeRules.WritesUserCollapsed"/>).</summary>
    public static readonly Signal<bool> UserCollapsed = new(false);

    /// <summary>The window band — written ONLY by the shell's band effect.</summary>
    public static readonly Signal<SidebarWindowBand> Band = new(SidebarWindowBand.Wide);

    /// <summary>What the frame presents — written ONLY by the shell's presentation effect, beside <see cref="PresentedWidth"/>.</summary>
    public static readonly Signal<SidebarPaneMode> Mode = new(SidebarPaneMode.Expanded);

    /// <summary>The overlay pane (the drawer in Tiny, the pane over a forced rail in Narrow) is open. Session-only.</summary>
    public static readonly Signal<bool> OverlayOpen = new(false);

    /// <summary>Edit mode (P4). False until then; the title bar, the overlay guards and the seam already read it.</summary>
    public static readonly Signal<bool> Editing = new(false);

    public static SidebarResizeRules.State ResizeState() => new(UserCollapsed.Peek(), Width.Peek());

    /// <summary>Drag end: resolve the seam into a settle, write the facts, persist, park the seam on the target.</summary>
    public static void CommitSeam() => Apply(SidebarResizeRules.Resolve(Seam.Peek(), ResizeState(), Editing.Peek()));

    /// <summary>The title-bar toggle, the seam's double-click and Enter: in the Wide band it flips the user's collapse;
    /// in Narrow/Tiny it opens or closes the overlay pane and never writes the collapse. Disabled while editing.</summary>
    public static void TogglePane()
    {
        if (Editing.Peek()) return;
        if (SidebarPaneModeRules.HasOverlay(Band.Peek())) { OverlayOpen.Value = !OverlayOpen.Peek(); return; }
        Apply(SidebarResizeRules.Toggle(ResizeState()));
    }

    public static void SetUserCollapsed(bool collapsed) { if (UserCollapsed.Peek() != collapsed) TogglePane(); }

    /// <summary>Show the FULL pane from wherever it is — the rail's search tile (design P.2a: "its search tile expands the
    /// pane with the search box open"). Narrow / Tiny: open the overlay pane (a forced rail has <c>UserCollapsed</c> false,
    /// so <see cref="SetUserCollapsed"/> would do nothing there). Wide: clear the user's collapse. Never closes anything;
    /// a no-op while editing (Edit already presents the pane).</summary>
    public static void OpenPane()
    {
        if (Editing.Peek()) return;
        if (SidebarPaneModeRules.HasOverlay(Band.Peek())) { OverlayOpen.SetIfChanged(true); return; }
        if (UserCollapsed.Peek()) Apply(SidebarResizeRules.Toggle(ResizeState()));
    }

    /// <summary>Your Library's search box is open. SESSION-ONLY and SHARED by every Library mount — the docked pane (a rail
    /// in Narrow) and the overlay pane are separate <c>PaneView</c> mounts with separate sessions, so a per-session flag
    /// set by the rail's tile would never open the overlay's box. Cleared with <c>LibrarySearch</c> on a layout or account
    /// switch.</summary>
    public static readonly Signal<bool> LibrarySearchOpen = new(false);

    /// <summary>Keyboard ←/→ on the seam (<paramref name="large"/> = Shift).</summary>
    public static void StepSeam(int direction, bool large)
        => Apply(SidebarResizeRules.Step(ResizeState(), direction, large, Editing.Peek()));

    /// <summary>Set the expanded width outright (Home/End, Settings): clamped, expanded, persisted.</summary>
    public static void SetExpandedWidth(float width)
    {
        float w = Math.Clamp(float.IsFinite(width) ? width : Width.Peek(), SidebarResizeRules.ExpandedMinW, SidebarResizeRules.ExpandedMaxW);
        Apply(new SidebarResizeRules.Settle(false, w, w));
    }

    static void Apply(in SidebarResizeRules.Settle s)
    {
        Width.SetIfChanged(s.ExpandedWidth);
        if (SidebarPaneModeRules.WritesUserCollapsed(Band.Peek(), Editing.Peek())) UserCollapsed.SetIfChanged(s.UserCollapsed);
        Seam.SetIfChanged(s.TargetWidth);
        SidebarPaneState.Snapshot(Platform.Settings, Design.Peek(), new SidebarPaneSnapshot(s.ExpandedWidth, UserCollapsed.Peek()));
    }
```

`Boot`: `UserCollapsed.Value = pane.Collapsed; Seam.Value = pane.Collapsed ? SidebarRowGeometry.RailWidth : pane.Width;
PresentedWidth.Value = Seam.Peek();` (no detent). `SwitchDesign` snapshots/restores the bool the same way. `ResetWidth`
is unchanged.

`Shell/Sidebar.Modes.cs`: `SidebarPaneSnapshot(float Width, bool Collapsed)`; `SidebarPaneState.Snapshot` writes
`state.Collapsed`; `Restore` reads it; `RestoreDetent` is deleted. (The per-design keys stay until P3's migration moves
them.)

### P2.3 The shell (`Shell/Shell.UI.cs`, `Shell/Shell.cs`, `Shell/Shell.Chrome.cs`)

`Shell.cs`: delete `Ui.LastResort` and `Ui.DrawerOpen` (the sidebar owns `Band` and `OverlayOpen`);
`FrameRules.SidebarSeamWidth(bool seamVisible) => seamVisible ? SeamStripW : 0f;` `Shell.Chrome.cs`: delete
`Layout.DrawerWidth`, `Layout.DrawerMinW` and `Layout.DrawerViewportInset` (their one caller, `DrawerPane` at
`Shell.UI.cs:1583`, uses `SidebarPaneModeRules.OverlayWidth`; their one test is rewritten in §P2.10).

`Shell.UI.cs` frame component:

- Replace the `_lastResortSeeded` seed with a band seed:

```csharp
            // The window band's FIRST value is known only here; seed it before the column mounts so a small launch lays out as
            // a rail (or no pane) on frame one (no subscriber reads it yet).
            if (!_bandSeeded)
            {
                _bandSeeded = true;
                float w0 = vp.Peek().Width;
                var band0 = SidebarPaneModeRules.BandOf(w0, SidebarWindowBand.Wide);
                Sidebar.Band.SetIfChanged(band0);
                var mode0 = SidebarPaneModeRules.Resolve(band0, Sidebar.UserCollapsed.Peek(), Sidebar.Editing.Peek());
                Sidebar.Mode.SetIfChanged(mode0);
                Sidebar.PresentedWidth.SetIfChanged(SidebarPaneModeRules.PresentedWidth(mode0, Sidebar.Width.Peek(), w0));
            }
```

- Replace the LAST RESORT effect and THE ONE PRESENTATION EFFECT with:

```csharp
            // THE WINDOW BAND (design V.1): hysteretic, derived — the ONLY way the window width reaches the sidebar.
            UseSignalEffect(() =>
            {
                var current = Sidebar.Band.Peek();
                var next = SidebarPaneModeRules.BandOf(vp.Value.Width, current);
                if (next == current) return;
                Sidebar.Band.Value = next;
                if (!SidebarPaneModeRules.HasOverlay(next)) Sidebar.OverlayOpen.SetIfChanged(false);
            });
            // THE ONE PRESENTATION EFFECT: ① the mode, ② the presented width. A live drag presents Track(seam). It never
            // writes Sidebar.Width or UserCollapsed (those are the preference, persisted at commit).
            UseSignalEffect(() =>
            {
                float vpW = vp.Value.Width;
                var band = Sidebar.Band.Value;
                bool editing = Sidebar.Editing.Value;
                var state = new SidebarResizeRules.State(Sidebar.UserCollapsed.Value, Sidebar.Width.Value);
                if (_sidebarDragging.Value && SidebarPaneModeRules.SeamVisible(band))
                {
                    var live = SidebarResizeRules.Track(Sidebar.Seam.Value, in state, editing);
                    Sidebar.Mode.SetIfChanged(live.Collapsed ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded);
                    Sidebar.PresentedWidth.SetIfChanged(live.PresentedWidth);
                    _sidebarFade.SetIfChanged(live.Fade);
                    return;
                }
                _sidebarFade.SetIfChanged(1f);
                var mode = SidebarPaneModeRules.Resolve(band, state.UserCollapsed, editing);
                Sidebar.Mode.SetIfChanged(mode);
                Sidebar.PresentedWidth.SetIfChanged(SidebarPaneModeRules.PresentedWidth(mode, state.ExpandedWidth, vpW));
            });
```

  `SampleSeamVelocity`/`ReleaseSeamVelocity` and their fields are deleted; the splitter's commit is
  `() => Sidebar.CommitSeam()`. The `Ui.RailFits` effect is unchanged (it already reads `Sidebar.PresentedWidth`).

- `SidebarPaneAnim` — the pane spline (design V.1):

```csharp
    /// <summary>The pane opens in 200 ms and closes in 100 ms on WinUI's SplitView spline (MotionTok.PaneOpen/PaneClose).</summary>
    static readonly LayoutTransition SidebarPaneAnim = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(MotionTok.PaneOpen.DurationMs, Easing.FluentPane), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(MotionTok.PaneClose.DurationMs, Easing.FluentPane), SuppressDescendantTransitions: true);
```

- The seam strip's width: `Prop.Of(static () => FrameRules.SidebarSeamWidth(SidebarPaneModeRules.SeamVisible(Sidebar.Band.Value)))`.
- `BuildSeamParts`: double-click → `Sidebar.TogglePane()`; its context menu stays `Sidebar.LayoutMenu.Model` until P4.
- `OnSeamKey`: `Enter or Space` → `Sidebar.TogglePane()`; the announcement: `Announcer.Say(Sidebar.UserCollapsed.Peek()
  ? Loc.Get("sidebar.seam.announceCollapsed") : Loc.Format("sidebar.seam.announceWidth", ("width", (int)Sidebar.Width.Peek())));`
  (`RailDetentLabelKey` deleted; new loc key `sidebar.seam.announceCollapsed` = "Navigation collapsed").
- `ToggleSidebar` → `Sidebar.TogglePane()`.
- `SidebarPaneFrame()` returns the new snapshot (§P2.9).
- `s_chromeParts`, `BuildChromeParts` are deleted; `ChromeRow()`'s bar drops `Parts = s_chromeParts` and gains:

```csharp
            PaneToggleEnabledSignal = s_paneToggleEnabled,
            PaneToggleToolTip = PaneToggleTip,
```

```csharp
    /// <summary>The title-bar toggle is disabled while the sidebar is being edited (design V.1).</summary>
    static readonly Memo<bool> s_paneToggleEnabled = new(static () => !Sidebar.Editing.Value);

    static string PaneToggleTip()
        => Sidebar.Editing.Value ? Loc.Get("sidebar.pane.finishEditing")
         : Sidebar.Mode.Value == SidebarPaneMode.Expanded || Sidebar.OverlayOpen.Value ? Loc.Get("sidebar.pane.collapse")
         : Loc.Get("sidebar.pane.expand");
```

  (`Memo<T>` needs a `ReactiveRuntime`, so the static is a plain signal mirrored by the frame component:
  `static readonly Signal<bool> s_paneToggleEnabled = new(true);` replaces the `Memo` line above, and the frame's
  `Render` adds `UseSignalEffect(static () => s_paneToggleEnabled.SetIfChanged(!Sidebar.Editing.Value));` beside the
  band effect.)
- `TrailingIsland`: delete the `chrome-settings` button; `Shell.Chrome.cs` `FixedBudget`: `4f * Layout.ChromeNavButtonW`
  → `3f * Layout.ChromeNavButtonW`, and its doc comments "bell, friends, pin, settings" → "bell, friends, pin".

**The overlay pane** (`NarrowDrawer` → it stays the class, it now serves Narrow and Tiny):

```csharp
        public override Element Render()
        {
            bool overlay = SidebarPaneModeRules.HasOverlay(Sidebar.Band.Value);
            bool pinned = SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, Sidebar.Editing.Value);
            bool open = overlay && (Sidebar.OverlayOpen.Value || pinned);
            var hooks = UseContext(InputHooks.Current);
            _escapePreview ??= key =>
            {
                // While editing, Esc belongs to the pane (it exits Edit first); a second Esc then closes the overlay.
                if ((key == Keys.Escape || key == Keys.GamepadB) && Sidebar.OverlayOpen.Peek() && !Sidebar.Editing.Peek())
                {
                    Sidebar.OverlayOpen.Value = false;
                    return true;
                }
                return _savedPreview?.Invoke(key) ?? false;
            };
            // … the KeyPreview chaining effect is unchanged, keyed on `open` …
            // A leaf navigation closes the overlay (NavigationView.cpp:4840-4846) — never while editing.
            UseSignalEffect(static () =>
            {
                _ = Current.Value;
                if (SidebarPaneModeRules.LeafInvokeClosesOverlay(Sidebar.Editing.Peek())) Sidebar.OverlayOpen.SetIfChanged(false);
            });
            if (!NarrowDrawerMount.ShouldMount(overlay, Sidebar.OverlayOpen.Peek()))
                return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };
            return new BoxEl
            {
                Grow = 1f, ZStack = true, HitTestVisible = open,
                Children =
                [
                    Embed.Comp(static () => new DrawerScrim()),
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, Justify = FlexJustify.Start, HitTestPassThrough = true,
                        Children = [Embed.Comp(static () => new DrawerPane())],
                    },
                ],
            };
        }
```

`DrawerScrim`: `bool open = Sidebar.OverlayOpen.Value || pinned` (same `pinned` expression); the scrim's click closes
only when not pinned; P4 swaps the pinned scrim to a non-dismissing 0.2 fill. `DrawerPane`: width
`SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Value, vp.Value.Width)`, transition `ms = Design.Reduced ? 0f :
(open ? MotionTok.PaneOpen.DurationMs : MotionTok.PaneClose.DurationMs)`, easing `Easing.FluentPane`.
`NarrowDrawerMount.ShouldMount(bool hasOverlay, bool overlayOpen) => hasOverlay;` (parameter renamed; same rule).

### P2.4 The planner's compact arm (`Shell/Sidebar.cs`)

`SidebarRowKind` gains, APPENDED:

```csharp
    // A COLLAPSED section in the compact rail (design V.9): one 40×36 glyph tile whose click opens a flyout of the
    // section's rows. Key == section id, EntryIndex == -1.
    SectionTile   = 15,
```

`SidebarRowPlanner.Build` gains a mode:

```csharp
    public static SidebarRowPlan Build(SidebarCustomLayout layout, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);
        st.ExcludePinned = HasPinnedSection(layout);
        st.Compact = compact;
        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++) PlanSection(sections[i], 0, in input, ref st);
        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }
```

`PlanState` gains `public bool Compact;`. `PlanSection`'s header/collapse step becomes:

```csharp
        bool titled = (s.Title is not null || s.TitleLocKey is not null) && !SidebarIds.IsTopBar(s.Id);
        if (st.Compact)
        {
            // The rail has no headers (they are 0 tall in WinUI's compact pane): a collapsed section is ONE tile, an
            // expanded one plans its rows as tiles.
            if (titled && s.Collapsed) { Add(ref st, Chrome(SidebarRowKind.SectionTile, s, depth)); return; }
            PlanBody(s, depth, in input, ref st);
            return;
        }
        if (titled) Add(ref st, new SidebarRow(SidebarRowKind.SectionHeader, s.Id, depth, -1, 0, s.Id));
        if (s.Collapsed) return;
        PlanBody(s, depth, in input, ref st);
```

In compact the playlist tree emits top-level rows only (a folder is a tile whose flyout holds its children): in
`PlanSourcePlaylistTree`, when `st.Compact`, skip every entry with `e.Depth > 0` and never descend into a folder.
`Empty`, `Skeleton`, `TreeEnd` and `PromptRow` rows are not emitted in compact (`if (st.Compact) return;` at the top of
`EmitSkeletons`, and guard each `Add(… Empty/TreeEnd/PromptRow …)` with `!st.Compact`). `BuildRail`, `RailSection`,
`RailFrom`, `RailItems`, `RailTree`, `RailEntityList`, `AddTile`, `RailJumpBackInCap`, `Sidebar.PlanRail`,
`SidebarRailItemKind`, `SidebarRailItems` and `SidebarRailExtents` are deleted.

`SidebarRowExtents.HeightOf` gains `case SidebarRowKind.SectionTile: return SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph);`.

`SidebarPillRules.IsSectionAnchor` (`Sidebar.Rules.cs`) becomes
`kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionTile`.

### P2.5 The pane: one layer (`Shell/Sidebar.UI.cs`)

Deleted from `PaneView`: `_railBuffersA/B`, `RailVersion`, `RailRowCount`, `_railHost`, `_railEpochs`,
`_railSelSet/_railSelNext/_railSelFlip`, `RailListController`, `RailLayout`, `RailPlan`, `RailHost`, `HasRailHead`,
`HasRailFooter`, `RailContentTypeHead/Footer`, `RailContentTypeOf`, `RefreshRailSelection`, `EnsureRailSlots`,
`BumpRailEpoch`, `BumpAllRailEpochs`, `BumpChangedRailEpochs`, `RailExtentSeed`, `ReseedRailExtents`, `SubscribeRailEpoch`,
`RegisterRailNode`, `RailNode`, `_railNodes`, `OpenRailFolderFlyout`, `CloseRailFolderFlyout`, `RailTileKey`, and the
`UseSignalEffect` that re-seeded on `RailDetent`.

**Kept, unchanged and under their current names:** `PaneView._railDropUri` (`Sidebar.UI.cs:508`, and its reset in the
drag-end path at `:2000`) and `PaneView.IsRailDropActive(string)` (`:2355`). The folder flyout survives P2
(`PaneFolderFlyout`, below), and its drop cue is exactly these members: its folder row sets
`DropActive = () => owner.IsRailDropActive(cueKey)`, its folder drop is `RailFolderDropSpec` (`Sidebar.UI.Drop.cs:202-247`,
which writes `_railDropUri` at :230 and :241-243) and its playlist drop is `ResourceDropSpec(…, railCueUri: entry.Uri)`
(`Drop.cs:108`, `:132`, and the `Drag.RailTileTransparent` test at :171). P2 does NOT edit `Sidebar.UI.Drop.cs` and no P2
WP owns it. A rename to `_flyoutDropUri` / `IsFlyoutDropActive` is out of scope (it would pull `Drop.cs` and the flyout
into one WP for no behaviour change). `RowSpec.DropActive` and its bound plate (P1.3) also survive P2 for good: the
flyout's rows are their caller. After P2-WP9 deletes `SidebarCards.RailOf` (and its `railCueUri:` call at
`Cards.cs:281`), the flyout is the only `railCueUri` caller; that is fine.

`PaneConfig` loses
`RailHead`, `RailHeadTiles`, `RailFooter`, `RailLayoutMenu`. `PlanStage` loses `Rail` and gains `bool Compact`.
`PublishStage` drops every rail line; it sets `CompactPlan = stage.Compact;` (a new `internal bool CompactPlan` field,
read by the slot).

`Render` (the layer part; the hook sequence before it is unchanged):

```csharp
            // The plan is built for what the FRAME presents: the rail is the same list (design V.9), planned compact.
            bool compact = !InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact && !_dragPeek.Value;
            …
            var stage = UseMemo(() => BuildStage(sourceDoc, search, edit, compact), PlanDep(search, in edit, compact));
            …
            var layer = new BoxEl
            {
                Key = "pane-layer", Direction = 1, Grow = 1f, Shrink = 0f, ClipToBounds = true,
                // Measured at the OPEN width even while presented as the rail: text never reflows through 48 DIP.
                Width = _expandedWidth,
                // DRAG PEEK: a spring-load waypoint over the whole rail (never a destination).
                DropTarget = compact ? RailPeekDropSpec() : null,
                Children = PaneChildren(rows, compact),
            };
            var children = new List<Element>(2) { layer };
            if (!InDrawer) children.Add(_dragPeekWatcher ??= Embed.Comp(() => new DragPeekWatcher(this)) with { Key = "drag-peek" });
```

`PaneChildren(rows, compact)` = `[Config.Head?.Invoke(), body, _footer ??= Embed.Comp(() => new PaneFooter(this)) with
{ Key = "pane-footer" }]` (nulls skipped; `body` is `EmptyPane()` or the padded list as today). `PlanDep` folds
`compact` (`DepKey.Combine(…, DepKey.From(compact ? 1 : 0))`). `BuildStage(document, search, edit, compact)` calls
`Sidebar.Plan(document, in input, paneBuffers, compact)` (the named wrapper gains the parameter) and never builds a rail
plan. A compact↔expanded flip is a wholesale publish (`wholesale |= stage.Compact != CompactPlan`).

The slot (`Sidebar.UI.Slot.cs`) in compact (`_o.CompactPlan`): every entity/route/folder row is built with
`RowSpec.Tile = true` and `Depth = 0`, wrapped `ToolTip.Wrap(built, label)` (no grow), and its pill uses `PillX(0)`.
A folder tile's click is `_o.OpenFolderFlyout(row.SectionId, in entry, () => _tileNode)` instead of a toggle. The new
`SectionTile` kind renders:

```csharp
        /// <summary>A collapsed section in the rail: its kind glyph (design V.9) in a 40×36 tile; click opens the section's
        /// rows as a flyout. Takes the pill when the selected route lives inside (pill rule 3).</summary>
        Element SectionTileRow(SidebarSectionSpec section, in SidebarRow row, int index, string sel)
        {
            string id = section.Id;
            var owner = _o;
            var spec = new RowSpec
            {
                Key = row.Key, Label = PaneText.TitleOf(section), Shape = SidebarRowShape.Glyph, Tile = true,
                Glyph = PaneIcon.SectionGlyph(section.Kind), Selected = _o.RowSelectsRoute(index, sel),
                OnRealized = h => _tileNode = h,
                OnClick = () => owner.OpenSectionFlyout(id, () => _tileNode),
            };
            var built = ToolTip.Wrap(EntityRow.Create(in spec), spec.Label);
            return Indicator(built, spec.Selected, 0, SidebarRowGeometry.RowHeight, _o.PillRouteOf(index, sel));
        }
```

`PaneIcon.SectionGlyph(kind)` (Rows.cs): Pinned → `Icons.Pin`, CollectionShortcuts → `Icons.Library`, PlaylistTree →
`Icons.MusicNote`, JumpBackIn → `Icons.History`, NewReleases → `Icons.Album`, otherwise `Icons.List`. `_tileNode` is a
`NodeHandle` field on the slot (one per slot; a slot draws one tile).

**Flyouts** (new file `Shell/Sidebar.UI.Flyout.cs`, a named partial of `Sidebar.UI.cs`; **copy the using header of
`Sidebar.UI.cs`** — `System.Threading`, `System.Threading.Tasks`, the nine `FluentGpu.*` namespaces (`Animation`,
`Controls`, `Dsl`, `Foundation`, `Hooks`, `Input`, `Localization`, `Scene`, `Signals`) and `using static FluentGpu.Dsl.Ui;`
— then `namespace Wavee;`): move `RailFolderFlyout` here
renamed `PaneFolderFlyout` (body unchanged except the class name and `CloseRailFolderFlyout` → `CloseFlyout`; its rows
keep `owner.RailFolderDropSpec(…)`, `owner.ResourceDropSpec(…, railCueUri: entry.Uri, …)` and
`DropActive = () => owner.IsRailDropActive(cueKey)` verbatim — the kept members above), and add:

```csharp
    internal sealed partial class PaneView
    {
        OverlayHandle? _flyout;

        /// <summary>A compact folder tile's children (NavigationView's collapsed-parent flyout, NVX:468-496): right of the
        /// tile, padding 0,2, corner 8; a second click closes it; a selection inside closes it (CPP:1066-1068).</summary>
        internal void OpenFolderFlyout(string sectionId, in SidebarLibraryEntry folder, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay) || folder.FolderId.Length == 0) return;
            if (_flyout is { IsOpen: true } open) { open.Close(); return; }
            string folderId = folder.FolderId, name = folder.Name;
            _flyout = MenuOverlay.Open(anchor,
                () => Embed.Comp(() => new PaneFolderFlyout
                {
                    Owner = this, SectionId = sectionId, RootFolderId = folderId, RootFolderName = name, Close = CloseFlyout,
                }),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _flyout.ClosedAction = () => _flyout = null;
        }

        /// <summary>A collapsed section's rows (design V.9): the same rows the expanded pane plans for the section.</summary>
        internal void OpenSectionFlyout(string sectionId, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            if (_flyout is { IsOpen: true } open) { open.Close(); return; }
            _flyout = MenuOverlay.Open(anchor,
                () => Embed.Comp(() => new PaneSectionFlyout { Owner = this, SectionId = sectionId, Close = CloseFlyout }),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _flyout.ClosedAction = () => _flyout = null;
        }

        internal void CloseFlyout() => _flyout?.Close();
    }

    /// <summary>A collapsed section's rows in a 300-DIP flyout: planned by the ONE planner from a one-section document with
    /// the section expanded, rendered with the pane's own row primitive. Virtualized past 50 rows. Live: it re-plans on the
    /// binder's cells. A row's click navigates and closes.</summary>
    internal sealed class PaneSectionFlyout : Component
    {
        public required PaneView Owner;
        public required string SectionId;
        public required Action Close;
        const float PanelW = 300f, MaxListH = 420f;
        readonly SidebarPlanBuffers _buffers = new();

        public override Element Render()
        {
            _ = (Binder?.Entries ?? Entries).Version.Value + PinsVersion.Value + FolderVersion.Value;
            var plan = Owner.PlanSectionExpanded(SectionId, _buffers);
            int n = plan.Rows.Count;
            float h = MathF.Min(MaxListH, n * SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine));
            Element list = n > 50
                ? ItemsView.Create(n, i => FlyoutRow(plan, i), RepeatLayout.Stack(SidebarRowGeometry.PitchOf(SidebarRowShape.EntityTwoLine)),
                    new ListOptions { SelectionMode = ItemsSelectionMode.None, Grow = 0f }) with { Height = h }
                : new ScrollEl { Height = h, Children = [new BoxEl { Direction = 1, Children = BuildRows(plan) }] };
            return new BoxEl
            {
                Direction = 1, Width = PanelW, ClipToBounds = true, Corners = Radii.OverlayAll,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, 2f, SidebarRowGeometry.PaneEdge, 2f),
                Children = [list],
            };
        }

        Element[] BuildRows(SidebarRowPlan plan)
        {
            var rows = new Element[plan.Rows.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = FlyoutRow(plan, i);
            return rows;
        }

        Element FlyoutRow(SidebarRowPlan plan, int i)
        {
            var row = plan.Rows[i];
            if ((uint)row.EntryIndex >= (uint)plan.Entries.Count) return new BoxEl { Height = 0f };
            var e = plan.Entries[row.EntryIndex];
            var owner = Owner;
            Action close = Close;
            return EntityRow.Create(new RowSpec
            {
                Key = row.Key, Label = e.Name.Length > 0 ? e.Name : PaneText.ShortUri(e.Uri),
                Subtitle = PaneText.SubtitleOf(in e), Shape = SidebarRowShape.EntityTwoLine,
                Leading = Cover.ForEntry(in e, SidebarRowGeometry.ArtOf(SidebarRowShape.EntityTwoLine)),
                Selected = string.Equals(e.RouteKey, owner.SelectedRoutePeek, StringComparison.Ordinal),
                OnClick = e.RouteKey is { Length: > 0 } r ? () => { owner.Navigate(r, e.Name); close(); } : null,
            });
        }
    }
```

`PaneView.PlanSectionExpanded(sectionId, buffers)` (in `Sidebar.UI.cs`): finds the section in `Doc`, builds
`new SidebarCustomLayout(Doc.TemplateId, [section with { Collapsed = false }])` and returns
`Sidebar.Plan(thatDoc, in input, buffers)` (P3 rewrites it against the new model). Glyph routes inside the section (a
Collections section) render with `Glyph = Shell.Dest(Shell.Parse(key)).Glyph` and `Shape = Glyph`: in `FlyoutRow`, when
`row.EntryIndex < 0` and `row.Kind == SidebarRowKind.IconRow`, build the glyph spec from `row.Key` instead.

### P2.6 The footer (new file `Shell/Sidebar.UI.Footer.cs`, a named partial of `Sidebar.UI.cs`)

**Copy the using header of `Sidebar.UI.cs`** (the house-style comment block, then its usings verbatim — the nine
`FluentGpu.*` namespaces and `using static FluentGpu.Dsl.Ui;` — then `namespace Wavee;`). The body:

```csharp
public static partial class Sidebar
{
    /// <summary>THE PANE FOOTER (design V.2): outside the scroller, bottom-anchored, margin 0,0,0,4. Expanded: ONE 40-px
    /// slot — the Settings row (row A) whose chevron column holds the ⋯ pane button (24×24, tooltip "Sidebar options",
    /// always visible); with Settings hidden only the ⋯, right-aligned. Compact: a Settings tile over a ⋯ tile. The ⋯ is
    /// never hidden: it is the layout-independent entry to the pane menu.</summary>
    internal sealed class PaneFooter(PaneView owner) : Component
    {
        NodeHandle _more;

        public override Element Render()
        {
            bool compact = !owner.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact;
            bool showSettings = owner.Config.ShowsSettings?.Invoke() ?? true;
            bool selected = string.Equals(owner.SelectedRoute, "settings", StringComparison.Ordinal);
            var dest = Shell.Dest(new Shell.Route(Shell.RouteKind.Settings));
            Func<ContextMenuModel?> menu = owner.PaneMenu;
            var svc = owner.MenuOverlay;

            Element more = compact
                ? EntityRow.Create(new RowSpec
                {
                    Key = "footer-more", Label = Loc.Get("sidebar.pane.options"), Shape = SidebarRowShape.Glyph, Tile = true,
                    Glyph = Icons.More, OnRealized = h => _more = h, MenuOverlay = svc, Menu = menu,
                    OnClick = () => owner.OpenPaneMenu(() => _more),
                })
                : SectionHeader.InlineButton(Icons.More, () => owner.OpenPaneMenu(() => _more), reveal: false,
                    Loc.Get("sidebar.pane.options")) with { OnRealized = h => _more = h };
            more = ToolTip.Wrap(more, Loc.Get("sidebar.pane.options"));

            Element? settings = null;
            if (showSettings)
            {
                var spec = new RowSpec
                {
                    Key = "footer-settings", Label = dest.Title, Shape = SidebarRowShape.Glyph, Glyph = dest.Glyph,
                    Selected = selected, Tile = compact,
                    OnClick = () => owner.Navigate("settings", null),
                };
                settings = EntityRow.Create(in spec);
                if (compact) settings = ToolTip.Wrap(settings, dest.Title);
            }

            var pill = new BoxEl
            {
                Width = SidebarRowGeometry.PillW, Height = SidebarRowGeometry.PillH, HitTestVisible = false,
                Margin = new Edges4(0f, SidebarRowGeometry.PillTop(SidebarRowGeometry.RowHeight), 0f, 0f),
                Corners = CornerRadius4.All(SidebarRowGeometry.PillRadius), Fill = Tok.AccentDefault,
                Opacity = selected && showSettings ? 1f : 0f,
            };

            Element body;
            if (compact)
                body = new BoxEl
                {
                    Direction = 1,
                    Children = settings is null ? [more] : [ZStack(settings, pill), more],
                };
            else if (settings is null)
                body = new BoxEl
                {
                    Direction = 0, Height = SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph), AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.End, Padding = new Edges4(0f, 0f, (SidebarRowGeometry.ChevronColumn - SidebarRowGeometry.HeaderButton) * 0.5f, 0f),
                    Children = [more],
                };
            else
                body = ZStack(settings, pill, new BoxEl
                {
                    // The ⋯ sits centred in the Settings row's chevron column (pane W − 44..W − 4).
                    Width = SidebarRowGeometry.ChevronColumn, Height = SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
                    JustifySelf = FlexAlign.End, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [more],
                });

            return new BoxEl
            {
                Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 4f),
                // The pane background's own menu answers a right-click on the footer's dead space too.
                Children = [body],
            };
        }
    }
}
```

`PaneConfig` gains `public Func<bool>? ShowsSettings { get; init; }` (P3 binds it to the doc). `PaneView` gains:

```csharp
        /// <summary>The global pane menu (the footer ⋯, the seam's and the background's right-click). P2: today's layout
        /// menu minus the rail-size rows; P4: <c>SidebarMenuModel.Pane</c>.</summary>
        internal ContextMenuModel? PaneMenu() => LayoutMenu.Model();

        OverlayHandle? _paneMenu;

        internal void OpenPaneMenu(Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(MenuOverlay)) return;
            if (_paneMenu is { IsOpen: true } open) { open.Close(); return; }
            var items = PaneMenu()?.Rows ?? [];
            _paneMenu = MenuOverlay.Open(anchor, () => MenuFlyout.Create(items, () => _paneMenu?.Close()),
                FlyoutPlacement.TopEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _paneMenu.ClosedAction = () => _paneMenu = null;
        }
```

(`ContextMenuModel` is `(Primary, Rows, Header)`; the flyout takes its `Rows`.)

`LayoutMenu.Rows()` (`Sidebar.UI.Menus.cs`): delete the rail-size submenu; keep the three design radio items,
Customize, and "Reset width". The `LayoutMenuButton` class is deleted (no header carries it any more).
`PaneView.RailTileMenu` is deleted.

### P2.7 Library V3 in the rail (`Shell/Sidebar.UI.LibraryV3.cs`)

Delete `BuildRailFooter`, `BuildRailHead`, `RailHeadTileCount`, `RailHeadTiles`, `RailRouteTile`, `RailActionTile`,
`RailEntityTile`, and the `RailHead`/`RailHeadTiles`/`RailFooter`/`RailLayoutMenu` config members. `CollapsePane` →
`SetUserCollapsed(true)`; `ExpandPane` is deleted (its one caller, the rail's search tile, calls `Sidebar.OpenPane()`).
`V3Session.SearchOpen` stops being a per-session field and reads the shared signal, so the overlay pane's session sees a
search the rail's session opened (every `_s.SearchOpen.Value` / `SetIfChanged` call site keeps compiling):

```csharp
        public Signal<bool> SearchOpen => Sidebar.LibrarySearchOpen;   // shared by the docked and the overlay mounts
```

`V3Chrome.Render` opens with:

```csharp
            // The rail (design V.9): Home and a search tile that expands the pane with the search open, then a separator;
            // the list below is the planner's tiles.
            if (!_s.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact) return CompactHead();
```

```csharp
        Element CompactHead()
        {
            string route = Shell.NameOf(Shell.Current.Value);
            var s = _s;
            var home = Shell.Dest(Shell.Parse("home"));
            Element Tile(string key, string label, string glyph, bool selected, Action click)
                => ToolTip.Wrap(EntityRow.Create(new RowSpec
                {
                    Key = key, Label = label, Shape = SidebarRowShape.Glyph, Tile = true, Glyph = glyph,
                    Selected = selected, OnClick = click,
                }), label);
            return new BoxEl
            {
                Key = "v3-compact-head", Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f),
                Children =
                [
                    Tile("v3-home", home.Title, home.Glyph, string.Equals(route, "home", StringComparison.Ordinal), () => s.Navigate("home", null)),
                    Tile("v3-search", Loc.Get(Strings.Sidebar.V3.SearchTooltip), Icons.Search, false, () => { s.OpenSearch(); Sidebar.OpenPane(); }),
                    SectionHeader.Separator(),
                ],
            };
        }
```

(`_s` is the chrome's session field — use the field name `V3Chrome` already has.)

### P2.8 Deletions elsewhere

`Platform/Platform.cs`: delete `SidebarRailDetent`. `Platform/Surface.Rules.cs`: delete `Shape.RailTileOf`.
`Shell/Sidebar.Cards.cs`: delete the `Rail` surface arm and `SidebarCards.RailOf` (and `SidebarCardSurface.Rail`).
`Screens/Settings.UI.Appearance.cs`: delete the rail-size row (`railSize`, `RailDetentPicker`, `s_railDetent`).
`Shell/Sidebar.UI.Rail.cs`: delete the file. Loc (all three files): delete `sidebar.rail.size`, `.compact`, `.default`,
`.large`, `sidebar.seam.announceRail`, `settings.sidebar.railSize`, `.railSizeSub`; `sidebar.rail.folderFlyoutBack` is
kept (the folder flyout's back button). Add (en-US; nl/ko fall back):

```json
"pane": {
  "options": "Sidebar options",
  "collapse": "Collapse navigation",
  "expand": "Expand navigation",
  "finishEditing": "Finish editing the sidebar first"
},
```

under `sidebar`, plus `sidebar.seam.announceCollapsed` = "Navigation collapsed".

### P2.9 Diagnostics

`Shell/Sidebar.cs`:

```csharp
/// <summary>A settled pane observation (the presentation as decided, plus the column's laid-out width).</summary>
public readonly record struct SidebarPaneFrameSnapshot(
    SidebarDesign Design,
    SidebarPaneMode Mode,
    SidebarWindowBand Band,
    bool UserCollapsed,
    bool OverlayOpen,
    float PreferredExpandedWidth,
    float PresentedWidth,
    float RenderedPaneWidth);

[Flags]
public enum SidebarPaneInvariantFault : ushort
{
    None = 0,
    NonFiniteValue = 1 << 0,
    PreferredWidthOutOfRange = 1 << 1,
    RailWidthMismatch = 1 << 2,
    ExpandedWidthOutOfRange = 1 << 3,
    PresentedExceedsPreferred = 1 << 4,
    ExpandedWidthMismatch = 1 << 5,
    MinimalNotEmpty = 1 << 6,
    ModeBandMismatch = 1 << 7,
}

public static class SidebarPaneInvariant
{
    public const float Tolerance = 0.5f;

    public static SidebarPaneInvariantFault Inspect(in SidebarPaneFrameSnapshot s)
    {
        if (!float.IsFinite(s.PreferredExpandedWidth) || !float.IsFinite(s.PresentedWidth) || !float.IsFinite(s.RenderedPaneWidth))
            return SidebarPaneInvariantFault.NonFiniteValue;
        var fault = SidebarPaneInvariantFault.None;
        if (!InExpandedRange(s.PreferredExpandedWidth)) fault |= SidebarPaneInvariantFault.PreferredWidthOutOfRange;
        // A forced band can only present its forced mode.
        if ((s.Band == SidebarWindowBand.Narrow && s.Mode != SidebarPaneMode.Compact)
            || (s.Band == SidebarWindowBand.Tiny && s.Mode != SidebarPaneMode.Minimal))
            fault |= SidebarPaneInvariantFault.ModeBandMismatch;
        switch (s.Mode)
        {
            case SidebarPaneMode.Compact:
                if (!Near(s.RenderedPaneWidth, SidebarRowGeometry.RailWidth)) fault |= SidebarPaneInvariantFault.RailWidthMismatch;
                break;
            case SidebarPaneMode.Minimal:
                if (!Near(s.RenderedPaneWidth, 0f)) fault |= SidebarPaneInvariantFault.MinimalNotEmpty;
                break;
            default:
                if (!InExpandedRange(s.PresentedWidth)) fault |= SidebarPaneInvariantFault.ExpandedWidthOutOfRange;
                if (s.PresentedWidth > s.PreferredExpandedWidth + Tolerance) fault |= SidebarPaneInvariantFault.PresentedExceedsPreferred;
                if (!Near(s.RenderedPaneWidth, s.PresentedWidth)) fault |= SidebarPaneInvariantFault.ExpandedWidthMismatch;
                break;
        }
        return fault;
    }

    public static bool IsValid(in SidebarPaneFrameSnapshot state) => Inspect(state) == SidebarPaneInvariantFault.None;

    public static string FaultName(SidebarPaneInvariantFault fault)
    {
        if (fault == SidebarPaneInvariantFault.None) return "none";
        if (fault == SidebarPaneInvariantFault.NonFiniteValue) return "non_finite";
        return ((ushort)fault).ToString(CultureInfo.InvariantCulture);
    }

    static bool InExpandedRange(float width) =>
        width >= SidebarPaneBounds.NavPaneMinW - Tolerance && width <= SidebarPaneBounds.NavPaneMaxW + Tolerance;

    static bool Near(float actual, float expected) => MathF.Abs(actual - expected) <= Tolerance;
}
```

`Shell.UI.cs` `SidebarPaneFrame()`:
`new SidebarPaneFrameSnapshot(Sidebar.Design.Peek(), Sidebar.Mode.Peek(), Sidebar.Band.Peek(), Sidebar.UserCollapsed.Peek(),
Sidebar.OverlayOpen.Peek(), Sidebar.Width.Peek(), Sidebar.PresentedWidth.Peek(), rendered)`.
`Screens/Diagnostics.Host.cs:293`: the fields become `mode`, `band`, `userCollapsed`, `overlayOpen`, `presentedWidth`,
`preferredWidth`, `renderedWidth`, `fault`.

### P2.10 Tests

New `SidebarPaneModeRulesTests.cs`:

```csharp
namespace Wavee.Tests;

public sealed class SidebarPaneModeRulesTests
{
    [Theory]
    [InlineData(1200f, SidebarWindowBand.Wide)]
    [InlineData(659f, SidebarWindowBand.Narrow)]
    [InlineData(527f, SidebarWindowBand.Tiny)]
    public void Band_FromWide(float w, SidebarWindowBand expected)
        => Assert.Equal(expected, SidebarPaneModeRules.BandOf(w, SidebarWindowBand.Wide));

    [Fact] public void Band_NarrowLeavesOnlyAt700()
    {
        Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(690f, SidebarWindowBand.Narrow));
        Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(700f, SidebarWindowBand.Narrow));
    }

    [Fact] public void Band_TinyLeavesOnlyAt568()
    {
        Assert.Equal(SidebarWindowBand.Tiny, SidebarPaneModeRules.BandOf(560f, SidebarWindowBand.Tiny));
        Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(568f, SidebarWindowBand.Tiny));
        Assert.Equal(SidebarWindowBand.Wide, SidebarPaneModeRules.BandOf(720f, SidebarWindowBand.Tiny));
    }

    [Fact] public void Band_ZeroWidthNeverMoves()
        => Assert.Equal(SidebarWindowBand.Narrow, SidebarPaneModeRules.BandOf(0f, SidebarWindowBand.Narrow));

    [Fact] public void Mode_ForcedBandsIgnoreTheUser()
    {
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, false, false));
        Assert.Equal(SidebarPaneMode.Minimal, SidebarPaneModeRules.Resolve(SidebarWindowBand.Tiny, false, true));
    }

    [Fact] public void Mode_WideFollowsTheUser_EditingPresentsExpanded()
    {
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, false));
        Assert.Equal(SidebarPaneMode.Expanded, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, true, true));
    }

    [Fact] public void UserCollapsed_WrittenOnlyWideAndNotEditing()
    {
        Assert.True(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, false));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, true));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Narrow, false));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Tiny, false));
    }

    [Fact] public void Overlay_PinnedOnlyWhenEditingOutsideWide()
    {
        Assert.True(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Narrow, true));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Wide, true));
        Assert.False(SidebarPaneModeRules.OverlayPinned(SidebarWindowBand.Tiny, false));
        Assert.False(SidebarPaneModeRules.LeafInvokeClosesOverlay(editing: true));
    }

    [Fact] public void PresentedWidth_ClampsToTheContentFloor()
    {
        Assert.Equal(320f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 1400f));
        Assert.Equal(220f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 700f));
        Assert.Equal(180f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 320f, 640f));
        Assert.Equal(48f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Compact, 320f, 600f));
        Assert.Equal(0f, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Minimal, 320f, 500f));
    }

    [Fact] public void TheRailsSearchTile_OpensTheOverlay_InTheForcedBands()
    {
        // Sidebar.OpenPane's branch: Narrow and Tiny open the overlay (a forced rail never has UserCollapsed set, so
        // clearing it would do nothing); only Wide writes the user's collapse.
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Narrow));
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Tiny));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Wide));
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Narrow, userCollapsed: false, editing: false));
    }

    [Fact] public void OverlayWidth_LeavesTheRailsWorthOfPage()
        => Assert.Equal(452f, SidebarPaneModeRules.OverlayWidth(460f, 500f));

    [Fact] public void RailFit_ReadsThePresentedPane_NeverChangesTheMode()
    {
        // The right rail at 360 does not fit beside a 320 pane in a 1100 window: it floats, and the pane stays Expanded.
        Assert.False(SidebarPaneModeRules.RailFits(320f, 360f, 1100f));
        Assert.Equal(SidebarPaneMode.Expanded,
            SidebarPaneModeRules.Resolve(SidebarPaneModeRules.BandOf(1100f, SidebarWindowBand.Wide), false, false));
        Assert.True(SidebarPaneModeRules.RailFits(48f, 360f, 1100f));
    }
}
```

`SidebarResizeRulesTests.cs` — rewritten (every detent fact deleted): `Track` below 116 collapses and above it clamps to
180-460 with the fade; from collapsed, ≥ 140 expands; editing never collapses (`Track(100, …, editing: true)` →
expanded at 180, fade 1); `Resolve` keeps the expanded memory when collapsing; `Step` nudges 8/40, collapses at the floor
(not while editing) and expands from the rail; `Toggle` flips and keeps the width.

`SidebarPlannerTests.cs` — compact arm: a Classic document planned compact has no `SectionHeader` rows; a collapsed
titled section is exactly one `SectionTile` keyed by the section id; separators (Divider sections) are kept; a
playlist tree plans only depth-0 rows; `SidebarRowExtents.HeightOf` of a `SectionTile` is 40; and the
`SidebarPaneInvariantTests` region uses the new snapshot (Compact rendered 48 ⇒ valid, 56 ⇒ `RailWidthMismatch`;
Minimal rendered 0 ⇒ valid; Narrow band with Expanded ⇒ `ModeBandMismatch`). Delete every `BuildRail` fact and the rail
extents/items facts.

`ShellNarrowDrawerTests.cs`: `ShouldMount(hasOverlay: true, overlayOpen: false)` is true, `(false, true)` false; plus
two facts on the pure guards the drawer uses: `SidebarPaneModeRules.LeafInvokeClosesOverlay(true)` is false and
`OverlayPinned(Narrow, true)` is true (Esc deferral is the drawer's `!Sidebar.Editing.Peek()` guard — pinned by the
rule test, never by reading source).

`DiagnosticsCoreTests.cs:303-307`: use `SidebarPaneInvariantFault.RailWidthMismatch` and `.MinimalNotEmpty` in place
of `LayerOpacityMismatch` / `HitTestOwnerMismatch`. `SidebarDesignTests.cs`: delete the detent/regime facts (search
`SidebarRailDetent`, `SidebarRegime`, `RestoreDetent`) and make the pane-snapshot facts use the bool.
`SurfaceRulesTests.cs`: delete lines 82-83 and 126 (the `RailTileOf` facts). `SidebarCardsTests.cs`: delete the rail
surface facts (search `SidebarRailDetent`, `RailOf`).

`ShellFrameRulesTests.cs:31-32` (named argument renamed):

```csharp
        Assert.Equal(0f, Shell.FrameRules.SidebarSeamWidth(seamVisible: false));
        Assert.Equal(Shell.FrameRules.SeamStripW, Shell.FrameRules.SidebarSeamWidth(seamVisible: true));
```

`ShellNavTests.cs` (P2-WP10). `ShellResponsiveLayoutTests.The_drawer_never_outgrows_the_window` (:295-300) is replaced by:

```csharp
    [Fact]
    public void The_overlay_never_outgrows_the_window()
    {
        // The preference is clamped to the pane bounds, then capped so a rail's worth of page stays visible.
        Assert.Equal(SidebarPaneBounds.NavPaneMinW, SidebarPaneModeRules.OverlayWidth(100f, 1200f));
        Assert.Equal(320f - SidebarRowGeometry.RailWidth, SidebarPaneModeRules.OverlayWidth(400f, 320f));
        Assert.Equal(0f, SidebarPaneModeRules.OverlayWidth(320f, 40f));
    }
```

The settings button leaves the trailing island, so every budget-derived threshold in this file moves down by exactly one
`ChromeNavButtonW` (44): the fixed budget with the actions in the row is 590, not 634 (lead 60 + theme 44 + back 44 +
forward 44 + "+" 32 + avatar 32 + actions 3 × 44 + gutters 16 + drag strip 48 + captions 138). Raw thresholds
(`ChromeActionsEnterW` 1200, `ChromeNameEnterW` 1360) and every fact below 1200 do not move. Exact edits:

| line | today | becomes |
|---|---|---|
| :398 | `The_budget_charges_for_four_trailing_buttons_unconditionally` | `The_budget_charges_for_three_trailing_buttons_unconditionally` |
| :403 | `Assert.Equal(4f * Shell.Layout.ChromeNavButtonW, withActions - without, 3);` | `Assert.Equal(3f * Shell.Layout.ChromeNavButtonW, withActions - without, 3);` |
| :614-616 comment | "the trailing actions (1200, +176 DIP)" … "≈[1200, 1244) with three tabs, ≈[1200, 1354) with four" | "the trailing actions (1200, +132 DIP)" … "≈[1200, 1310) with four tabs" (drop the three-tab clause) |
| :637 | `[InlineData(330f, Shell.Layout.ChromeActionsEnterW, 1244f)]   // three tabs: 634 + 280 + 330` | `[InlineData(330f, Shell.Layout.ChromeActionsEnterW, 1200f)]   // three tabs: 590 + 280 + 330 (affordable at the raw threshold)` |
| :638 | `[InlineData(440f, Shell.Layout.ChromeActionsEnterW, 1354f)]   // four tabs:  634 + 280 + 440` | `[InlineData(440f, Shell.Layout.ChromeActionsEnterW, 1310f)]   // four tabs:  590 + 280 + 440` |
| :642 comment | "the four buttons stay FOLDED (bell and friends are profile-menu rows; pin is on the tab menu)" | "the three buttons stay FOLDED (bell and friends are profile-menu rows; pin is on the tab menu)" |
| :659 comment | "cannot seat 724 + 280 + 440 until 1444" | "cannot seat 680 + 280 + 440 until 1400" |
| :660 | `w < 1444f` | `w < 1400f` |
| :666 | `Shell.Chrome.Resolve(1444f, 440f)` | `Shell.Chrome.Resolve(1400f, 440f)` |
| :685 | `Resolve(1354f, 440f, narrow)` | `Resolve(1310f, 440f, narrow)` |
| :686 | `Resolve(1393f, 440f, narrow)` | `Resolve(1349f, 440f, narrow)` |
| :687 | `Resolve(1394f, 440f, narrow)` | `Resolve(1350f, 440f, narrow)` |
| :691 | `Resolve(1354f, 440f, promoted)` | `Resolve(1310f, 440f, promoted)` |
| :692 | `Resolve(1353f, 440f, promoted)` | `Resolve(1309f, 440f, promoted)` |

The flip-width theory at :713-727 (848 / 900 / 906 / 916) is unchanged: those widths are below 1200, where the actions
are folded and cost nothing. `:739` (`connecting.FixedBudgetFor() - live.FixedBudgetFor()`) is a difference of two
budgets at the same width and is unchanged. `ShellTabStripRulesTests.cs:32/:55` read `FixedBudgetFor()` and only get
looser (budget down 44); unchanged. `ShellMastheadsTests.cs` has no budget fact; untouched.

### P2 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P2-WP1 | `Shell\Sidebar.Resize.cs` | §P2.1 | — |
| P2-WP2 | `Shell\Sidebar.Host.cs`, `Shell\Sidebar.Modes.cs` | §P2.2 (incl. `OpenPane()` and `LibrarySearchOpen`) | P2-WP1 |
| P2-WP3 | `Shell\Sidebar.cs`, `Shell\Sidebar.Rules.cs` | §P2.4 (+ `IsSectionAnchor`), §P2.9 (snapshot + invariant) | P2-WP1 |
| P2-WP4 | `Shell\Sidebar.UI.cs`, `Shell\Sidebar.UI.Menus.cs` | §P2.5 (pane layer, `PlanSectionExpanded`, `PaneMenu`/`OpenPaneMenu`, `ShowsSettings` config; `_railDropUri` and `IsRailDropActive` are KEPT, the flyout's drop cue), §P2.6 LayoutMenu trim; never the disclosure members (§1) | P2-WP1, P2-WP2, P2-WP3 |
| P2-WP5 | `Shell\Sidebar.UI.Slot.cs`, `Shell\Sidebar.UI.Rows.cs` | §P2.5 compact rows, `SectionTileRow`, `PaneIcon.SectionGlyph` (never `Chevron`) | P2-WP3 |
| P2-WP6 | new `Shell\Sidebar.UI.Flyout.cs`, new `Shell\Sidebar.UI.Footer.cs`, delete `Shell\Sidebar.UI.Rail.cs` | §P2.5 flyouts, §P2.6 footer (both new files copy the using header of `Sidebar.UI.cs`) | P2-WP4 |
| P2-WP7 | `Shell\Sidebar.UI.LibraryV3.cs` | §P2.7 (`SearchOpen` → the shared signal, `ExpandPane` deleted, `CompactHead` with `OpenPane`) | P2-WP2 |
| P2-WP8 | `Shell\Shell.UI.cs`, `Shell\Shell.cs`, `Shell\Shell.Chrome.cs` | §P2.3 (incl. deleting `DrawerWidth`/`DrawerMinW`/`DrawerViewportInset`, the 3-button budget), §P2.9 frame snapshot | P2-WP1, P2-WP2 |
| P2-WP9 | `Platform\Platform.cs`, `Platform\Surface.Rules.cs`, `Shell\Sidebar.Cards.cs`, `Screens\Settings.UI.Appearance.cs`, `Screens\Diagnostics.Host.cs`, `assets\loc\en-US.json`, `assets\loc\nl.json`, `assets\loc\ko-KR.json` | §P2.8, §P2.9 log fields | P2-WP1, P2-WP2 |
| P2-WP10 | new `SidebarPaneModeRulesTests.cs`; `SidebarResizeRulesTests.cs`, `SidebarPlannerTests.cs`, `ShellNarrowDrawerTests.cs`, `DiagnosticsCoreTests.cs`, `SidebarDesignTests.cs`, `SurfaceRulesTests.cs`, `SidebarCardsTests.cs`, `ShellFrameRulesTests.cs`, `ShellNavTests.cs` | §P2.10 | P2-WP1, P2-WP2, P2-WP3, P2-WP8, P2-WP9 |

---

## P3. The layout model, planner, stores, accounts and migration (design Phases 3 + 4, one phase)

One step from today's three designs to two layouts over one model. After P3 the app runs on `sidebar.json` v3 and
`sidebar.acct-*.json`; `sidebar-layout.json` has been read once by the migration and is never written again; Custom,
the customizer page, templates, the palette, the 13-kind document, the reducer, the data-source registry and every
source class are gone. Library still wears today's V3 chrome (rewired to the new state) until P5.

New files: `Shell/Sidebar.Layout.cs` (§P3.1-P3.4), `Shell/Sidebar.Planner.cs` (§P3.5), `Shell/Sidebar.Feeds.cs`
(§P3.6.1), `Shell/Sidebar.Store.cs` (§P3.7), `Shell/Sidebar.Accounts.cs` (§P3.8), `Shell/Sidebar.Store.V2.cs` and
`Shell/Sidebar.Migration.cs` (§P3.10). Deleted: `Shell/Sidebar.Doc.cs`, `Shell/Sidebar.Customizer.UI.cs`.

### P3.1 The catalogue (`Shell/Sidebar.Layout.cs`, part 1)

Header comment: "the sidebar's information architecture as data — two layouts (Classic, Library), a closed eight-kind
catalogue, the per-layout overlay the user edits, the ops that edit it, and the resolved document the planner reads.
Role: CORE, engine-free. Spec: sidebar-rework-implementation.md §P3.1-§P3.4." Body:

```csharp
using System.Collections.Generic;

namespace Wavee;

/// <summary>The two sidebar layouts. PERSISTED as <c>sidebar.layout.id</c> (0 Classic · 1 Library) — append only.</summary>
public enum SidebarLayoutId : byte { Classic = 0, Library = 1 }

/// <summary>The closed section catalogue (design C.1). Not persisted by number: the files use <see cref="SidebarCatalogue.IdOf"/>.</summary>
public enum SidebarSectionKind : byte
{
    Home = 0, Pinned = 1, Collections = 2, Playlists = 3, Library = 4, Recent = 5, NewReleases = 6, Settings = 7,
}

/// <summary>Row density for entity rows (design P.4): Default = row C (two lines, art 32), Compact = row B (one line, art
/// 24). Glyph rows and headers are unaffected. PERSISTED as <c>sidebar.pane.density</c>.</summary>
public enum SidebarDensity : byte { Default = 0, Compact = 1 }

/// <summary>Your Library's list presentation.</summary>
public enum SidebarLibraryView : byte { List = 0, Grid = 1 }

/// <summary>Your Library's sort. <see cref="CustomOrder"/> IS the Spotify rootlist order and applies under the Playlists
/// filter only (elsewhere it presents Alphabetical, <see cref="SidebarSort.Effective"/>). Same values as the deleted V3
/// sort, so a stored int keeps its meaning.</summary>
public enum SidebarLibrarySort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, CustomOrder = 4 }

/// <summary>The active filter chip (none, or one kind). PERSISTED as <c>sidebar.library.filter</c>.</summary>
public enum SidebarLibraryFilter : byte { None = 0, Playlists = 1, Albums = 2, Artists = 3, Podcasts = 4, Audiobooks = 5 }

/// <summary>The library kinds a user can hide from Your Library (design Q12: no chip, no page in the dropdown, no items in
/// the unfiltered list).</summary>
[System.Flags]
public enum SidebarLibraryKinds : byte { None = 0, Albums = 1, Artists = 2, Podcasts = 4, Audiobooks = 8 }

/// <summary>THE CATALOGUE (design C.1): which sections each layout has, their stable ids, titles, locks and defaults.
/// Nothing outside this class decides what a section is.</summary>
public static class SidebarCatalogue
{
    public const string HomeRoute = "home";
    public const string SettingsRoute = "settings";
    public const string LikedRoute = "liked";

    /// <summary>Recent / New releases row limits.</summary>
    public static readonly int[] LimitChoices = [5, 10, 20];

    static readonly SidebarSectionKind[] s_classic =
    [
        SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Collections, SidebarSectionKind.Playlists,
        SidebarSectionKind.Recent, SidebarSectionKind.NewReleases, SidebarSectionKind.Settings,
    ];
    static readonly SidebarSectionKind[] s_library =
        [SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Library, SidebarSectionKind.Settings];

    static readonly string[] s_collectionItems = ["liked", "albums", "artists", "podcasts", "audiobooks"];
    static readonly string[] s_libraryKinds = ["albums", "artists", "podcasts", "audiobooks"];

    /// <summary>The layout's sections in catalogue (default) order.</summary>
    public static IReadOnlyList<SidebarSectionKind> KindsOf(SidebarLayoutId layout)
        => layout == SidebarLayoutId.Library ? s_library : s_classic;

    public static bool Has(SidebarLayoutId layout, SidebarSectionKind kind)
    {
        var kinds = KindsOf(layout);
        for (int i = 0; i < kinds.Count; i++) if (kinds[i] == kind) return true;
        return false;
    }

    /// <summary>The stable wire id. PERSISTED (both files) — never rename one.</summary>
    public static string IdOf(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Home => "home",
        SidebarSectionKind.Pinned => "pinned",
        SidebarSectionKind.Collections => "collections",
        SidebarSectionKind.Playlists => "playlists",
        SidebarSectionKind.Library => "library",
        SidebarSectionKind.Recent => "recent",
        SidebarSectionKind.NewReleases => "newReleases",
        _ => "settings",
    };

    public static bool TryKindOf(string? id, out SidebarSectionKind kind)
    {
        switch (id)
        {
            case "home": kind = SidebarSectionKind.Home; return true;
            case "pinned": kind = SidebarSectionKind.Pinned; return true;
            case "collections": kind = SidebarSectionKind.Collections; return true;
            case "playlists": kind = SidebarSectionKind.Playlists; return true;
            case "library": kind = SidebarSectionKind.Library; return true;
            case "recent": kind = SidebarSectionKind.Recent; return true;
            case "newReleases": kind = SidebarSectionKind.NewReleases; return true;
            case "settings": kind = SidebarSectionKind.Settings; return true;
            default: kind = SidebarSectionKind.Home; return false;
        }
    }

    /// <summary>The header title's loc key; null for Home and Settings (their titles are the route table's).</summary>
    public static string? TitleKeyOf(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.pinned",
        SidebarSectionKind.Collections => "sidebar.collections",
        SidebarSectionKind.Playlists => "sidebar.playlists",
        SidebarSectionKind.Library => "sidebar.yourLibrary",
        SidebarSectionKind.Recent => "sidebar.recentlyPlayed",
        SidebarSectionKind.NewReleases => "sidebar.newReleases",
        _ => null,
    };

    /// <summary>Can the user hide it? Home never (it is first and locked); Playlists and Your Library never (their "+" is
    /// how content starts — hiding them strands content); Settings yes (the profile menu and the palette keep it reachable).
    /// Pinned is hideable but LOCKED while it holds a route/module pin (<see cref="SidebarLayoutRules.Apply"/>).</summary>
    public static bool Hideable(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Recent
            or SidebarSectionKind.NewReleases or SidebarSectionKind.Settings;

    /// <summary>Movable sections (design Q2): Classic's middle sections only; Library is a fixed order.</summary>
    public static bool Movable(SidebarLayoutId layout, SidebarSectionKind kind)
        => layout == SidebarLayoutId.Classic
           && kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Playlists
               or SidebarSectionKind.Recent or SidebarSectionKind.NewReleases;

    /// <summary>Has a collapsible header (Home, Settings and Your Library have none).</summary>
    public static bool Collapsible(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Playlists
            or SidebarSectionKind.Recent or SidebarSectionKind.NewReleases;

    /// <summary>The section's hideable ITEMS in default order: Classic Collections = the five library pages (route keys);
    /// Library's <c>library</c> section = the four hideable kinds (its "Filters"). Empty for every other section.</summary>
    public static IReadOnlyList<string> ItemsOf(SidebarLayoutId layout, SidebarSectionKind kind)
        => kind == SidebarSectionKind.Collections && layout == SidebarLayoutId.Classic ? s_collectionItems
         : kind == SidebarSectionKind.Library && layout == SidebarLayoutId.Library ? s_libraryKinds
         : System.Array.Empty<string>();

    /// <summary>Can the items be reordered (Collections only; the library kinds have no order).</summary>
    public static bool ItemsMovable(SidebarSectionKind kind) => kind == SidebarSectionKind.Collections;

    public static int DefaultLimit(SidebarSectionKind kind)
        => kind == SidebarSectionKind.Recent ? 5 : kind == SidebarSectionKind.NewReleases ? 10 : 0;

    /// <summary>A section's default state in a layout. Recent and New releases ship hidden (Classic offers them; Library
    /// has no feeds). Your Library defaults: Recents, natural direction, List, Liked shown.</summary>
    public static SectionState DefaultState(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Recent or SidebarSectionKind.NewReleases => new SectionState(IdOf(kind), Hidden: true, Limit: DefaultLimit(kind)),
        SidebarSectionKind.Library => new SectionState(IdOf(kind), Sort: SidebarLibrarySort.Recents, Descending: false,
            View: SidebarLibraryView.List, ShowLiked: true),
        _ => new SectionState(IdOf(kind)),
    };

    public static LayoutOverlay DefaultOverlay(SidebarLayoutId layout)
    {
        var kinds = KindsOf(layout);
        var sections = new SectionState[kinds.Count];
        for (int i = 0; i < sections.Length; i++) sections[i] = DefaultState(kinds[i]);
        return new LayoutOverlay(layout, sections);
    }

    /// <summary>A library kind's flag, from its item id.</summary>
    public static SidebarLibraryKinds KindFlagOf(string item) => item switch
    {
        "albums" => SidebarLibraryKinds.Albums,
        "artists" => SidebarLibraryKinds.Artists,
        "podcasts" => SidebarLibraryKinds.Podcasts,
        "audiobooks" => SidebarLibraryKinds.Audiobooks,
        _ => SidebarLibraryKinds.None,
    };
}
```

### P3.2 State, ops and the rules (`Shell/Sidebar.Layout.cs`, part 2)

```csharp
/// <summary>One section's user state inside one layout (design A.3). Null members mean "the catalogue default", which
/// keeps <c>sidebar.json</c> small and makes a new default reach every user who never changed it.</summary>
public sealed record SectionState(
    string Id,
    bool Hidden = false,
    bool Collapsed = false,
    int? Limit = null,
    IReadOnlyList<string>? HiddenItems = null,
    IReadOnlyList<string>? ItemOrder = null,
    SidebarLibrarySort? Sort = null,
    bool? Descending = null,
    SidebarLibraryView? View = null,
    bool? ShowLiked = null)
{
    public IReadOnlyList<string> HiddenList => HiddenItems ?? System.Array.Empty<string>();

    // The two lists compare by CONTENT (ordinal, order-sensitive): a record's synthesized Equals compares references, so a
    // state read back from disk would never equal the one that wrote it and every load would look like an edit.
    public bool Equals(SectionState? other)
        => other is not null && string.Equals(Id, other.Id, System.StringComparison.Ordinal) && Hidden == other.Hidden
           && Collapsed == other.Collapsed && Limit == other.Limit && Sort == other.Sort && Descending == other.Descending
           && View == other.View && ShowLiked == other.ShowLiked
           && SidebarLayoutRules.SameList(HiddenItems, other.HiddenItems) && SidebarLayoutRules.SameList(ItemOrder, other.ItemOrder);

    public override int GetHashCode() => System.HashCode.Combine(Id, Hidden, Collapsed, Limit, HiddenItems?.Count ?? 0, ItemOrder?.Count ?? 0);
}

/// <summary>One layout's sections in DISPLAY order — always every catalogue section of the layout exactly once
/// (<see cref="SidebarLayoutRules.MergeWithCatalogue"/> guarantees it).</summary>
public sealed record LayoutOverlay(SidebarLayoutId Layout, IReadOnlyList<SectionState> Sections)
{
    public int IndexOf(string id)
    {
        for (int i = 0; i < Sections.Count; i++)
            if (string.Equals(Sections[i].Id, id, System.StringComparison.Ordinal)) return i;
        return -1;
    }

    public SectionState? Find(string id) => IndexOf(id) is int i and >= 0 ? Sections[i] : null;

    public bool Equals(LayoutOverlay? other)
    {
        if (other is null || other.Layout != Layout || other.Sections.Count != Sections.Count) return false;
        for (int i = 0; i < Sections.Count; i++) if (!Sections[i].Equals(other.Sections[i])) return false;
        return true;
    }

    public override int GetHashCode() => System.HashCode.Combine(Layout, Sections.Count);
}

/// <summary>Both layouts' overlays (design D1: one overlay per layout, both kept across a switch).</summary>
public sealed record SidebarLayoutState(LayoutOverlay Classic, LayoutOverlay Library)
{
    public static SidebarLayoutState Default { get; } =
        new(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic), SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library));

    public LayoutOverlay Of(SidebarLayoutId layout) => layout == SidebarLayoutId.Library ? Library : Classic;

    public SidebarLayoutState With(LayoutOverlay overlay)
        => overlay.Layout == SidebarLayoutId.Library ? this with { Library = overlay } : this with { Classic = overlay };
}

/// <summary>One edit (design A.3: every mutation is <c>Sidebar.Dispatch(op)</c>). In-memory only, never serialized.</summary>
public abstract record SidebarOp(SidebarLayoutId Layout);
public sealed record SetSectionShown(SidebarLayoutId Layout, string SectionId, bool Shown) : SidebarOp(Layout);
/// <summary><paramref name="ToSlot"/> is the index among the layout's MOVABLE sections (the Outline band's slots).</summary>
public sealed record MoveSection(SidebarLayoutId Layout, string SectionId, int ToSlot) : SidebarOp(Layout);
/// <summary>A live collapse toggle. Persisted, never recorded in the undo ring, never counted by IsModified.</summary>
public sealed record SetSectionCollapsed(SidebarLayoutId Layout, string SectionId, bool Collapsed) : SidebarOp(Layout);
public sealed record SetSectionLimit(SidebarLayoutId Layout, string SectionId, int Limit) : SidebarOp(Layout);
public sealed record SetItemShown(SidebarLayoutId Layout, string SectionId, string ItemId, bool Shown) : SidebarOp(Layout);
public sealed record MoveItem(SidebarLayoutId Layout, string SectionId, string ItemId, int ToIndex) : SidebarOp(Layout);
public sealed record SetLibrarySort(SidebarLibrarySort Sort, bool Descending) : SidebarOp(SidebarLayoutId.Library);
public sealed record SetLibraryView(SidebarLibraryView View) : SidebarOp(SidebarLayoutId.Library);
public sealed record SetShowLiked(bool Shown) : SidebarOp(SidebarLayoutId.Library);
/// <summary>"Reset this layout": every section back to its default, collapse bits kept.</summary>
public sealed record ResetLayout(SidebarLayoutId Layout) : SidebarOp(Layout);
/// <summary>Undo/redo and "Reset everything": replace a whole overlay (merged and capped first).</summary>
public sealed record ReplaceOverlay(LayoutOverlay Overlay) : SidebarOp(Overlay.Layout);

public enum SidebarOpReject : byte
{
    None = 0, NoChange = 1, UnknownSection = 2, Locked = 3, NotMovable = 4, OutOfRange = 5, BadLimit = 6,
    UnknownItem = 7, OverCap = 8,
}

public readonly record struct SidebarOpResult(SidebarLayoutState State, bool Changed, SidebarOpReject Reject)
{
    public static SidebarOpResult Refused(SidebarLayoutState s, SidebarOpReject why) => new(s, false, why);
}

/// <summary>THE LAYOUT RULES (design A.2): every op, the "last item hides the section" coupling (Q5), the merge with the
/// catalogue, the caps, and <see cref="IsModified"/>. A refused op changes nothing (the caller pushes no undo and writes
/// no file).</summary>
public static class SidebarLayoutRules
{
    /// <summary>Caps (design corner cases): an over-cap write is refused, never truncated.</summary>
    public const int MaxSections = 16, MaxHiddenItems = 64, MaxOverlayBytes = 8 * 1024;

    /// <param name="pinnedLocked">Pinned holds a route or module pin (it may not be hidden — "Unpin Search, Radio first").</param>
    public static SidebarOpResult Apply(SidebarLayoutState state, SidebarOp op, bool pinnedLocked)
    {
        var overlay = state.Of(op.Layout);
        LayoutOverlay? next = op switch
        {
            SetSectionShown o => Shown(overlay, o, pinnedLocked, out var r1) ?? Fail(r1),
            MoveSection o => Move(overlay, o, out var r2) ?? Fail(r2),
            SetSectionCollapsed o => Collapse(overlay, o, out var r3) ?? Fail(r3),
            SetSectionLimit o => Limit(overlay, o, out var r4) ?? Fail(r4),
            SetItemShown o => ItemShown(overlay, o, out var r5) ?? Fail(r5),
            MoveItem o => ItemMove(overlay, o, out var r6) ?? Fail(r6),
            SetLibrarySort o => Update(overlay, "library", s => s with { Sort = o.Sort, Descending = o.Descending }),
            SetLibraryView o => Update(overlay, "library", s => s with { View = o.View }),
            SetShowLiked o => Update(overlay, "library", s => s with { ShowLiked = o.Shown }),
            ResetLayout => Reset(overlay),
            ReplaceOverlay o => MergeWithCatalogue(o.Overlay, null),
            _ => null,
        };
        if (s_reject != SidebarOpReject.None)
        {
            var why = s_reject;
            s_reject = SidebarOpReject.None;
            return SidebarOpResult.Refused(state, why);
        }
        if (next is null || next.Equals(overlay)) return SidebarOpResult.Refused(state, SidebarOpReject.NoChange);
        return new SidebarOpResult(state.With(next), true, SidebarOpReject.None);
    }

    // Apply's arms report a refusal through this one slot (UI thread only; the class is otherwise stateless).
    [System.ThreadStatic] static SidebarOpReject s_reject;
    static LayoutOverlay? Fail(SidebarOpReject why) { s_reject = why; return null; }

    static LayoutOverlay? Shown(LayoutOverlay o, SetSectionShown op, bool pinnedLocked, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Hideable(kind)) { why = SidebarOpReject.Locked; return null; }
        if (kind == SidebarSectionKind.Pinned && !op.Shown && pinnedLocked) { why = SidebarOpReject.Locked; return null; }
        // Q5: showing a section only drops its Hidden bit; its hidden items stay as they were.
        return Replace(o, i, o.Sections[i] with { Hidden = !op.Shown });
    }

    static LayoutOverlay? Move(LayoutOverlay o, MoveSection op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int from = o.IndexOf(op.SectionId);
        if (from < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Movable(o.Layout, kind)) { why = SidebarOpReject.NotMovable; return null; }
        // The band's slots are the movable sections, in display order; the locked ones keep their absolute positions.
        var slots = new List<int>(o.Sections.Count);
        for (int k = 0; k < o.Sections.Count; k++)
            if (SidebarCatalogue.TryKindOf(o.Sections[k].Id, out var kk) && SidebarCatalogue.Movable(o.Layout, kk)) slots.Add(k);
        int fromSlot = slots.IndexOf(from);
        if ((uint)op.ToSlot >= (uint)slots.Count) { why = SidebarOpReject.OutOfRange; return null; }
        if (op.ToSlot == fromSlot) return o;
        var movable = new List<SectionState>(slots.Count);
        for (int k = 0; k < slots.Count; k++) movable.Add(o.Sections[slots[k]]);
        var moved = movable[fromSlot];
        movable.RemoveAt(fromSlot);
        movable.Insert(op.ToSlot, moved);
        var sections = new SectionState[o.Sections.Count];
        for (int k = 0; k < sections.Length; k++) sections[k] = o.Sections[k];
        for (int k = 0; k < slots.Count; k++) sections[slots[k]] = movable[k];
        return o with { Sections = sections };
    }

    static LayoutOverlay? Collapse(LayoutOverlay o, SetSectionCollapsed op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Collapsible(kind)) { why = SidebarOpReject.Locked; return null; }
        return Replace(o, i, o.Sections[i] with { Collapsed = op.Collapsed });
    }

    static LayoutOverlay? Limit(LayoutOverlay o, SetSectionLimit op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (SidebarCatalogue.DefaultLimit(kind) == 0 || System.Array.IndexOf(SidebarCatalogue.LimitChoices, op.Limit) < 0)
        { why = SidebarOpReject.BadLimit; return null; }
        return Replace(o, i, o.Sections[i] with { Limit = op.Limit });
    }

    static LayoutOverlay? ItemShown(LayoutOverlay o, SetItemShown op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        var items = SidebarCatalogue.ItemsOf(o.Layout, kind);
        if (!Contains(items, op.ItemId)) { why = SidebarOpReject.UnknownItem; return null; }
        var s = o.Sections[i];
        var hidden = new List<string>(s.HiddenList);
        if (op.Shown) hidden.Remove(op.ItemId);
        else
        {
            if (Contains(hidden, op.ItemId)) return o;
            // Q5: hiding the LAST visible item of Collections hides the section and keeps the previous item set, so
            // "Show section" restores what it showed. Library's kinds never hide their section (it is locked).
            if (kind == SidebarSectionKind.Collections && hidden.Count + 1 >= items.Count)
                return Replace(o, i, s with { Hidden = true });
            if (hidden.Count >= MaxHiddenItems) { why = SidebarOpReject.OverCap; return null; }
            hidden.Add(op.ItemId);
        }
        return Replace(o, i, s with { HiddenItems = hidden.Count == 0 ? null : hidden.ToArray() });
    }

    static LayoutOverlay? ItemMove(LayoutOverlay o, MoveItem op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.ItemsMovable(kind)) { why = SidebarOpReject.NotMovable; return null; }
        var order = new List<string>(EffectiveItemOrder(o.Layout, kind, o.Sections[i]));
        int from = order.IndexOf(op.ItemId);
        if (from < 0) { why = SidebarOpReject.UnknownItem; return null; }
        if ((uint)op.ToIndex >= (uint)order.Count) { why = SidebarOpReject.OutOfRange; return null; }
        order.RemoveAt(from);
        order.Insert(op.ToIndex, op.ItemId);
        bool isDefault = SameList(order, SidebarCatalogue.ItemsOf(o.Layout, kind));
        return Replace(o, i, o.Sections[i] with { ItemOrder = isDefault ? null : order.ToArray() });
    }

    static LayoutOverlay? Update(LayoutOverlay o, string id, System.Func<SectionState, SectionState> change)
    {
        int i = o.IndexOf(id);
        if (i < 0) return Fail(SidebarOpReject.UnknownSection);
        return Replace(o, i, change(o.Sections[i]));
    }

    static LayoutOverlay Reset(LayoutOverlay o)
    {
        var fresh = SidebarCatalogue.DefaultOverlay(o.Layout);
        var sections = new SectionState[fresh.Sections.Count];
        for (int k = 0; k < sections.Length; k++)
        {
            var old = o.Find(fresh.Sections[k].Id);
            sections[k] = old is null ? fresh.Sections[k] : fresh.Sections[k] with { Collapsed = old.Collapsed };
        }
        return fresh with { Sections = sections };
    }

    static LayoutOverlay Replace(LayoutOverlay o, int index, SectionState s)
    {
        var sections = new SectionState[o.Sections.Count];
        for (int k = 0; k < sections.Length; k++) sections[k] = k == index ? s : o.Sections[k];
        return o with { Sections = sections };
    }

    /// <summary>THE MERGE (design corner case "section added by an app update"): unknown ids are dropped (and reported
    /// into <paramref name="dropped"/> for the caller's log), duplicates keep the first, every missing catalogue section is
    /// inserted after the previous catalogue section that is present (default state), Home is forced first and — in
    /// Library — Your Library last before Settings; limits outside the choices fall back to the default; hidden items not
    /// in the catalogue are dropped; over-cap lists are cut at the cap (a merge reads a file, it never refuses a load).</summary>
    public static LayoutOverlay MergeWithCatalogue(LayoutOverlay overlay, List<string>? dropped)
    {
        var kinds = SidebarCatalogue.KindsOf(overlay.Layout);
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var list = new List<SectionState>(kinds.Count);
        for (int i = 0; i < overlay.Sections.Count && list.Count < MaxSections; i++)
        {
            var s = overlay.Sections[i];
            if (!SidebarCatalogue.TryKindOf(s.Id, out var kind) || !SidebarCatalogue.Has(overlay.Layout, kind))
            { dropped?.Add(s.Id ?? ""); continue; }
            if (!seen.Add(s.Id)) continue;
            list.Add(Sanitize(overlay.Layout, kind, s));
        }
        for (int k = 0; k < kinds.Count; k++)
        {
            string id = SidebarCatalogue.IdOf(kinds[k]);
            if (seen.Contains(id)) continue;
            int at = 0;
            for (int p = k - 1; p >= 0; p--)
            {
                int prev = IndexIn(list, SidebarCatalogue.IdOf(kinds[p]));
                if (prev >= 0) { at = prev + 1; break; }
            }
            list.Insert(at, SidebarCatalogue.DefaultState(kinds[k]));
            seen.Add(id);
        }
        Pin(list, "home", first: true);
        if (overlay.Layout == SidebarLayoutId.Library) { Pin(list, "library", first: false); }
        Pin(list, "settings", first: false);
        return new LayoutOverlay(overlay.Layout, list.ToArray());
    }

    static SectionState Sanitize(SidebarLayoutId layout, SidebarSectionKind kind, SectionState s)
    {
        int? limit = s.Limit is int l && System.Array.IndexOf(SidebarCatalogue.LimitChoices, l) >= 0 ? l
            : SidebarCatalogue.DefaultLimit(kind) is int d and > 0 ? d : null;
        var items = SidebarCatalogue.ItemsOf(layout, kind);
        string[]? hidden = Filter(s.HiddenItems, items, MaxHiddenItems);
        string[]? order = SidebarCatalogue.ItemsMovable(kind) ? Filter(s.ItemOrder, items, items.Count) : null;
        bool locked = !SidebarCatalogue.Hideable(kind);
        return s with
        {
            Hidden = !locked && s.Hidden,
            Collapsed = SidebarCatalogue.Collapsible(kind) && s.Collapsed,
            Limit = limit,
            HiddenItems = hidden,
            ItemOrder = order,
        };
    }

    static string[]? Filter(IReadOnlyList<string>? source, IReadOnlyList<string> allowed, int cap)
    {
        if (source is null || source.Count == 0) return null;
        var kept = new List<string>(source.Count);
        for (int i = 0; i < source.Count && kept.Count < cap; i++)
            if (Contains(allowed, source[i]) && !kept.Contains(source[i])) kept.Add(source[i]);
        return kept.Count == 0 ? null : kept.ToArray();
    }

    static void Pin(List<SectionState> list, string id, bool first)
    {
        int i = IndexIn(list, id);
        if (i < 0) return;
        var s = list[i];
        list.RemoveAt(i);
        if (first) list.Insert(0, s); else list.Add(s);
    }

    static int IndexIn(List<SectionState> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i].Id, id, System.StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>A section's items in its EFFECTIVE order (the stored order, completed by any catalogue item it lacks).</summary>
    public static IReadOnlyList<string> EffectiveItemOrder(SidebarLayoutId layout, SidebarSectionKind kind, SectionState s)
    {
        var items = SidebarCatalogue.ItemsOf(layout, kind);
        if (s.ItemOrder is not { Count: > 0 } order) return items;
        var list = new List<string>(items.Count);
        for (int i = 0; i < order.Count; i++) if (Contains(items, order[i]) && !list.Contains(order[i])) list.Add(order[i]);
        for (int i = 0; i < items.Count; i++) if (!list.Contains(items[i])) list.Add(items[i]);
        return list;
    }

    /// <summary>"Classic · modified" (design C.1): any section's shown / order / limit / sort / view / showLiked /
    /// hidden items / item order differs from the catalogue default. Collapse, density, width and the filter never count.</summary>
    public static bool IsModified(LayoutOverlay overlay)
    {
        var fresh = SidebarCatalogue.DefaultOverlay(overlay.Layout);
        if (overlay.Sections.Count != fresh.Sections.Count) return true;
        for (int i = 0; i < fresh.Sections.Count; i++)
        {
            var a = overlay.Sections[i];
            var b = fresh.Sections[i];
            if (!string.Equals(a.Id, b.Id, System.StringComparison.Ordinal)) return true;   // order
            if (!SidebarCatalogue.TryKindOf(a.Id, out var kind)) return true;
            if (a.Hidden != b.Hidden || (a.Limit ?? 0) != (b.Limit ?? 0)) return true;
            if ((a.Sort ?? b.Sort) != b.Sort || (a.Descending ?? b.Descending) != b.Descending
                || (a.View ?? b.View) != b.View || (a.ShowLiked ?? b.ShowLiked) != b.ShowLiked) return true;
            if (a.HiddenList.Count != 0) return true;
            if (!SameList(EffectiveItemOrder(overlay.Layout, kind, a), SidebarCatalogue.ItemsOf(overlay.Layout, kind))) return true;
        }
        return false;
    }

    public static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        int ac = a?.Count ?? 0, bc = b?.Count ?? 0;
        if (ac != bc) return false;
        for (int i = 0; i < ac; i++) if (!string.Equals(a![i], b![i], System.StringComparison.Ordinal)) return false;
        return true;
    }

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The resolved document for a layout (§P3.3).</summary>
    public static SidebarLayoutDoc Resolve(SidebarLayoutState state, SidebarLayoutId layout, SidebarDensity density)
    {
        var overlay = state.Of(layout);
        var sections = new SidebarSection[overlay.Sections.Count];
        var lib = SidebarLibraryOptions.Default;
        for (int i = 0; i < sections.Length; i++)
        {
            var s = overlay.Sections[i];
            SidebarCatalogue.TryKindOf(s.Id, out var kind);
            var order = EffectiveItemOrder(layout, kind, s);
            var visible = new List<string>(order.Count);
            for (int k = 0; k < order.Count; k++) if (!Contains(s.HiddenList, order[k])) visible.Add(order[k]);
            sections[i] = new SidebarSection(kind, s.Hidden, s.Collapsed, s.Limit ?? SidebarCatalogue.DefaultLimit(kind),
                SidebarSection.ShapeFor(kind, density), visible.ToArray());
            if (kind == SidebarSectionKind.Library)
            {
                var hiddenKinds = SidebarLibraryKinds.None;
                for (int k = 0; k < s.HiddenList.Count; k++) hiddenKinds |= SidebarCatalogue.KindFlagOf(s.HiddenList[k]);
                lib = new SidebarLibraryOptions(s.Sort ?? SidebarLibrarySort.Recents, s.Descending ?? false,
                    s.View ?? SidebarLibraryView.List, s.ShowLiked ?? true, hiddenKinds);
            }
        }
        return new SidebarLayoutDoc(layout, sections, density, lib);
    }
}
```

(The `Apply` refusal slot: if a coder prefers, replace the `s_reject` slot with each arm returning
`(LayoutOverlay? next, SidebarOpReject why)` tuples; the behaviour is what the tests pin.)

### P3.3 The resolved document (`Shell/Sidebar.Layout.cs`, part 3)

```csharp
/// <summary>A resolved section — what the planner and the slot read. <see cref="Items"/> are the VISIBLE items in order
/// (Collections: route keys; Your Library: the shown kinds). Immutable; rebuilt only when the overlay or the density
/// changes, so the pane's publish reference test holds.</summary>
public sealed record SidebarSection(
    SidebarSectionKind Kind,
    bool Hidden,
    bool Collapsed,
    int Limit,
    SidebarRowShape Shape,
    IReadOnlyList<string> Items)
{
    public string Id => SidebarCatalogue.IdOf(Kind);

    /// <summary>The one row shape per section (design V.3): glyph sections are row A; entity sections follow the density.</summary>
    public static SidebarRowShape ShapeFor(SidebarSectionKind kind, SidebarDensity density)
        => kind is SidebarSectionKind.Home or SidebarSectionKind.Collections or SidebarSectionKind.Settings
            ? SidebarRowShape.Glyph
            : density == SidebarDensity.Compact ? SidebarRowShape.EntityOneLine : SidebarRowShape.EntityTwoLine;
}

/// <summary>Your Library's resolved options (the <c>library</c> section's state).</summary>
public sealed record SidebarLibraryOptions(
    SidebarLibrarySort Sort, bool Descending, SidebarLibraryView View, bool ShowLiked, SidebarLibraryKinds HiddenKinds)
{
    public static readonly SidebarLibraryOptions Default =
        new(SidebarLibrarySort.Recents, false, SidebarLibraryView.List, true, SidebarLibraryKinds.None);
}

/// <summary>THE DOCUMENT the planner reads (replaces <c>SidebarCustomLayout</c>): the layout, its sections in display
/// order (hidden ones included — the Outline needs them), the density and Your Library's options.</summary>
public sealed record SidebarLayoutDoc(
    SidebarLayoutId Layout,
    IReadOnlyList<SidebarSection> Sections,
    SidebarDensity Density,
    SidebarLibraryOptions Library)
{
    public static readonly SidebarLayoutDoc Empty = new(SidebarLayoutId.Classic, System.Array.Empty<SidebarSection>(),
        SidebarDensity.Default, SidebarLibraryOptions.Default);

    public SidebarSection? Find(SidebarSectionKind kind)
    {
        for (int i = 0; i < Sections.Count; i++) if (Sections[i].Kind == kind) return Sections[i];
        return null;
    }

    public SidebarSection? Find(string? id)
        => SidebarCatalogue.TryKindOf(id, out var kind) ? Find(kind) : null;

    /// <summary>The footer's Settings row is shown.</summary>
    public bool ShowsSettings => Find(SidebarSectionKind.Settings) is { Hidden: false };
}
```

### P3.4 Visibility and pin rules (`Shell/Sidebar.Layout.cs`, part 4)

```csharp
/// <summary>What is drawn where (design A.2 SidebarVisibilityRules): the pin dedupe, Library's pin and Liked rules, the
/// Pinned lock and its reason.</summary>
public static class SidebarVisibilityRules
{
    /// <summary>A pinned entity/route is drawn ONLY in Pinned while Pinned is visible and either expanded or presented as a
    /// rail tile; collapsing Pinned in the expanded pane returns pinned items to their home sections. Library always
    /// dedupes while Pinned is shown (pins are the list's first rows).</summary>
    public static bool DedupesPins(SidebarLayoutDoc doc, bool compact)
    {
        var pinned = doc.Find(SidebarSectionKind.Pinned);
        if (pinned is null || pinned.Hidden) return false;
        return doc.Layout == SidebarLayoutId.Library || compact || !pinned.Collapsed;
    }

    /// <summary>Library: does the pin band show <paramref name="pin"/> under the current filter/search? An ENTITY pin
    /// follows the chip, the hidden kinds and the search; a non-entity pin (a route, a module) has no kind, so ANY chip hides
    /// it, and with no chip it follows the search like every other row (design P.2a "the text filters pins and the list",
    /// Q2 "search filters pins too"): a "Search" pin stays while the user types "sea" and goes for "xyz".</summary>
    public static bool ShowsPinInLibrary(in SidebarLibraryEntry pin, SidebarLibraryFilter filter, string? search,
                                         SidebarLibraryKinds hiddenKinds)
    {
        bool entity = pin.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder or SidebarEntryKind.Album
            or SidebarEntryKind.Artist or SidebarEntryKind.Show;
        bool searching = !string.IsNullOrEmpty(search);
        if (!entity)
            return filter == SidebarLibraryFilter.None
                   && (!searching || SidebarSearch.Matches(pin.Name.Length > 0 ? pin.Name : pin.Id, pin.Creator, search!));
        if (SidebarLibraryFilters.IsHidden(hiddenKinds, in pin)) return false;
        if (filter != SidebarLibraryFilter.None && !SidebarLibraryFilters.Matches(filter, in pin)) return false;
        return !searching || SidebarSearch.Matches(in pin, search!);
    }

    /// <summary>Library: Liked Songs is the list's fixed first row under no chip or the Playlists chip, unless hidden
    /// ("Show Liked Songs"), filtered out by the search, or the list is DRILLED into a folder (a folder level holds that
    /// folder's children only — today's V3 <c>LikedVisible</c> is <c>!Drilled</c> too).</summary>
    public static bool ShowsLiked(SidebarLibraryOptions options, SidebarLibraryFilter filter, string? search, string? likedTitle,
                                  bool drilled = false)
    {
        if (drilled || !options.ShowLiked) return false;
        if (filter is not (SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists)) return false;
        if (string.IsNullOrEmpty(search)) return true;
        return likedTitle is { Length: > 0 } t && t.Contains(search!, System.StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Pinned is LOCKED (cannot be hidden) while it holds a pin that would have nowhere else to show: a route or
    /// a module pin. Returns those pins' names for the reason line ("Unpin Search, Radio first"), or an empty list.</summary>
    public static void LockingPins(IReadOnlyList<SidebarPin> pins, List<string> names)
    {
        names.Clear();
        for (int i = 0; i < pins.Count; i++)
            if (pins[i].Kind == SidebarEntryKind.AppRoute) names.Add(pins[i].Name);
    }
}

/// <summary>The library filter vocabulary over projected entries (one place, so the chips, the pin band and the binder's
/// shaping agree). Podcasts and Audiobooks split the Show rows by <see cref="SidebarLibraryEntry.IsAudiobook"/>.</summary>
public static class SidebarLibraryFilters
{
    public static bool Matches(SidebarLibraryFilter filter, in SidebarLibraryEntry e) => filter switch
    {
        SidebarLibraryFilter.Playlists => e.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder,
        SidebarLibraryFilter.Albums => e.Kind == SidebarEntryKind.Album,
        SidebarLibraryFilter.Artists => e.Kind == SidebarEntryKind.Artist,
        SidebarLibraryFilter.Podcasts => e.Kind == SidebarEntryKind.Show && !e.IsAudiobook,
        SidebarLibraryFilter.Audiobooks => e.Kind == SidebarEntryKind.Show && e.IsAudiobook,
        _ => true,
    };

    public static bool IsHidden(SidebarLibraryKinds hidden, in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Album => (hidden & SidebarLibraryKinds.Albums) != 0,
        SidebarEntryKind.Artist => (hidden & SidebarLibraryKinds.Artists) != 0,
        SidebarEntryKind.Show => (hidden & (e.IsAudiobook ? SidebarLibraryKinds.Audiobooks : SidebarLibraryKinds.Podcasts)) != 0,
        _ => false,
    };

    /// <summary>A hidden kind has no chip (Q12).</summary>
    public static bool HasChip(SidebarLibraryFilter filter, SidebarLibraryKinds hidden) => filter switch
    {
        SidebarLibraryFilter.Albums => (hidden & SidebarLibraryKinds.Albums) == 0,
        SidebarLibraryFilter.Artists => (hidden & SidebarLibraryKinds.Artists) == 0,
        SidebarLibraryFilter.Podcasts => (hidden & SidebarLibraryKinds.Podcasts) == 0,
        SidebarLibraryFilter.Audiobooks => (hidden & SidebarLibraryKinds.Audiobooks) == 0,
        _ => true,
    };

    /// <summary>The filter a stored int means, falling back to None when its kind was hidden meanwhile (the active chip
    /// of a hidden kind falls back to everything).</summary>
    public static SidebarLibraryFilter Effective(int stored, SidebarLibraryKinds hidden)
    {
        var f = (uint)stored <= 5 ? (SidebarLibraryFilter)stored : SidebarLibraryFilter.None;
        return HasChip(f, hidden) ? f : SidebarLibraryFilter.None;
    }
}

/// <summary>A pin row's state (design Q9): pending (last-known name + kind glyph, no warning) until the projection
/// hydrates; unavailable only after an AUTHORITATIVE miss (a converged rootlist that no longer carries a folder); offline
/// neutral. Never auto-dropped, never a toast.</summary>
public enum SidebarPinState : byte { Resolved = 0, Pending = 1, Unavailable = 2, Offline = 3 }

public static class SidebarPinStateRules
{
    public static SidebarPinState Of(bool identityKnown, bool authoritativeMiss, bool online)
        => authoritativeMiss ? SidebarPinState.Unavailable
         : identityKnown ? SidebarPinState.Resolved
         : !online ? SidebarPinState.Offline
         : SidebarPinState.Pending;
}
```

`SidebarLibraryEntry` (in `Sidebar.cs`) gains

```csharp
    /// <summary>A saved show that is an AUDIOBOOK (Spotify files both under SavedShows; the flags decide —
    /// <c>LibraryAudiobookFilter.IsAudiobookRow</c>). Splits the Podcasts and Audiobooks chips.</summary>
    public bool IsAudiobook { get; init; }
```

and `SidebarProjection.Build`'s show loop adds `IsAudiobook = LibraryAudiobookFilter.IsAudiobookRow(sh.Flags),` to the
entry initializer.

### P3.5 The planner (new file `Shell/Sidebar.Planner.cs`; the old planner region of `Sidebar.cs` is deleted)

`SidebarRowKind` (rewritten in `Sidebar.cs`; values kept where the kind survives, so recycle pools stay distinct):

```csharp
/// <summary>The row vocabulary — also the ItemsView's <c>ContentType</c>, so each kind recycles in its own pool.</summary>
public enum SidebarRowKind : byte
{
    SectionHeader = 0,   // 40-px header (expanded only)
    Divider       = 2,   // the 8-px full-width separator between two rendered sections
    IconRow       = 3,   // a glyph row for an app route (Home, a Collections page); Key = the route key, EntryIndex = -1
    EntityRow     = 4,   // a projected entry (an entity, a route pin, the Library's Liked row); EntryIndex >= 0
    FolderHeader  = 5,   // a rootlist folder; disclosure in the trailing chevron column
    GridStrip     = 6,   // Library grid: [EntryIndex, ItemCount] cells
    Empty         = 8,   // a quiet one-line hint (an empty Playlists, a search with no match)
    Skeleton      = 9,   // the source is pending and nothing is known yet
    TreeEnd       = 14,  // the tree's closing drop gutter
    SectionTile   = 15,  // a collapsed section in the compact rail
    DropBand      = 16,  // empty Pinned while a pinnable drag is live: "Drop here to pin"
}
```

`SidebarProjectionInput` (rewritten in `Sidebar.cs`):

```csharp
/// <summary>Everything outside the document the plan depends on. Every list is nullable so a headless test can omit it.</summary>
public readonly record struct SidebarProjectionInput(
    // The library projection: Classic reads it for nothing; Library reads the mode-shaped list (filtered, searched,
    // sorted, the pin band removed, tree-regrouped when folders apply).
    IReadOnlyList<SidebarLibraryEntry>? Library = null,
    // The rootlist tree, depth-first flattened, folders as SidebarEntryKind.Folder entries — Classic's Playlists.
    IReadOnlyList<SidebarLibraryEntry>? PlaylistTree = null,
    // Every pin, resolved, in pin order (entities, folders, routes, modules).
    IReadOnlyList<SidebarLibraryEntry>? Pins = null,
    // Recently played contexts, resolved (never a nameless row), newest first.
    IReadOnlyList<SidebarLibraryEntry>? Played = null,
    // New releases from followed artists, newest first.
    IReadOnlyList<SidebarLibraryEntry>? NewReleases = null,
    IReadOnlySet<string>? PinnedIds = null,
    // Expanded rootlist folder ids. null = everything expanded (the headless default).
    IReadOnlySet<string>? ExpandedFolders = null,
    // Classic's Playlists filter / Library's search, normalized by the planner.
    string? Search = null,
    SidebarSourceState LibraryState = SidebarSourceState.Ready,
    SidebarSourceState TreeState = SidebarSourceState.Ready,
    // Library: the localized "Liked Songs" title (the Liked row's label and its search match).
    string? LikedTitle = null,
    // Library: Library is a depth-stamped tree (folders inline) rather than a flat sorted list.
    bool LibraryIsTree = false,
    int Revision = 0);
```

`SidebarRowPlan`, `SidebarRow`, `SidebarPlanBuffers` (drop the four `Tree*` scratch lists except `TreeAncestors`, unused
— delete it too) and `SidebarSourceState` are unchanged. The planner:

```csharp
// ── Shell/Sidebar.Planner.cs ───────────────────────────────────────────────────────────────────────────────────────
// (layout document × projection) → ONE flat row list, for the expanded pane and the compact rail alike
//
// Role: CORE
// Spec: sidebar-rework-implementation.md §P3.5 · design V.3, V.7, V.9, P.1, P.2a
//
// Pure: no engine type, no service, no clock. A row's Key is always an existing string (an entry's Id, a route key, a
// section id), so planning allocates no string; the row/entry lists are the caller's SidebarPlanBuffers and alias.

namespace Wavee;

/// <summary>What the plan is FOR, beyond the document and the projection.</summary>
/// <param name="Mode">Compact plans the rail: no headers, a collapsed section is one <see cref="SidebarRowKind.SectionTile"/>,
/// trees show their top level only (a folder is a flyout).</param>
/// <param name="PinDropArmed">A pinnable drag is live: an empty Pinned plans its drop band.</param>
/// <param name="Filter">Library's active chip.</param>
/// <param name="GridColumns">Library's grid column count (derived from the pane width by the caller).</param>
/// <param name="FoldersInline">Library: an expanded PINNED folder lists its children under it (the wide pane, not drilled).
/// False while the narrow/drawer pane drills into folders instead (its folder click drills, so nothing expands inline).
/// Classic ignores it.</param>
/// <param name="Drilled">Library: the list shows ONE folder level (the narrow pane or the drawer drilled in). That level is
/// the folder's children only: no pin rows, no "Drop here to pin" band, no Liked Songs row — today's V3 hides both while
/// drilled (<c>PinsBandVisible</c> / <c>LikedVisible</c> are <c>!Drilled</c>), and a pinned playlist inside the folder
/// would otherwise show twice (the shaped level does not skip pins while drilled). Classic ignores it.</param>
public readonly record struct SidebarPlanOptions(
    SidebarPaneMode Mode = SidebarPaneMode.Expanded,
    bool PinDropArmed = false,
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    int GridColumns = 2,
    bool FoldersInline = true,
    bool Drilled = false);

public static class SidebarRowPlanner
{
    /// <summary>The one finite guard on a projected section (a 10k library plans in full).</summary>
    public const int DynamicSectionRowCap = 20_000;
    const int SkeletonRows = 3;

    /// <summary>Deterministic: same inputs → identical plan.</summary>
    public static SidebarRowPlan Build(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
        SidebarPlanBuffers? buffers = null)
    {
        System.ArgumentNullException.ThrowIfNull(doc);
        var st = Begin(buffers);
        st.Compact = options.Mode == SidebarPaneMode.Compact;
        st.Dedupe = SidebarVisibilityRules.DedupesPins(doc, st.Compact);
        if (doc.Layout == SidebarLayoutId.Library) PlanLibrary(doc, in input, in options, ref st);
        else
        {
            var sections = doc.Sections;
            for (int i = 0; i < sections.Count; i++) PlanSection(sections[i], in input, in options, ref st);
        }
        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>One section's body, expanded, top level only for trees — a compact section tile's flyout.</summary>
    public static SidebarRowPlan BuildSection(SidebarLayoutDoc doc, string sectionId, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        var st = Begin(buffers);
        st.Compact = true;   // flyout rows: top level only, no hints, no gutters
        st.Dedupe = SidebarVisibilityRules.DedupesPins(doc, compact: true);
        if (doc.Find(sectionId) is { } s) PlanBody(s, in input, default, ref st);
        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    // ── Classic ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One Classic section: a separator before every rendered section but the first, then its header (expanded)
    /// or nothing (compact), then its body — or, collapsed, only the header (expanded) / one tile (compact). A section with
    /// no rows is not rendered at all (D9), except Playlists, whose "+" is how content starts.</summary>
    static void PlanSection(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        if (s.Hidden || s.Kind == SidebarSectionKind.Settings) return;          // Settings is the footer
        bool titled = s.Kind != SidebarSectionKind.Home;
        if (s.Collapsed && titled)
        {
            if (!HasRows(s, in input, in o, in st)) return;
            if (st.Rows.Count > 0) st.Rows.Add(Chrome(SidebarRowKind.Divider, s));
            st.Rows.Add(Chrome(st.Compact ? SidebarRowKind.SectionTile : SidebarRowKind.SectionHeader, s));
            return;
        }
        int rowMark = st.Rows.Count, entryMark = st.Entries.Count;
        if (rowMark > 0) st.Rows.Add(Chrome(SidebarRowKind.Divider, s));
        if (titled && !st.Compact) st.Rows.Add(Chrome(SidebarRowKind.SectionHeader, s));
        int bodyStart = st.Rows.Count;
        PlanBody(s, in input, in o, ref st);
        bool keepsEmpty = s.Kind == SidebarSectionKind.Playlists && !st.Compact;
        if (st.Rows.Count == bodyStart && !keepsEmpty) Truncate(ref st, rowMark, entryMark);
    }

    static void PlanBody(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Home:
                st.Rows.Add(new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, SidebarCatalogue.HomeRoute));
                break;
            case SidebarSectionKind.Pinned: PlanPins(s, in input, in o, ref st); break;
            case SidebarSectionKind.Collections: PlanCollections(s, in input, ref st); break;
            case SidebarSectionKind.Playlists: PlanPlaylists(s, in input, ref st); break;
            case SidebarSectionKind.Recent: PlanFeed(s, input.Played, ref st); break;
            case SidebarSectionKind.NewReleases: PlanFeed(s, input.NewReleases, ref st); break;
        }
    }

    /// <summary>Would the section plan any body row? Cheap: it counts, it never plans (a collapsed 10k Playlists costs a
    /// branch, not a walk).</summary>
    static bool HasRows(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, in PlanState st) => s.Kind switch
    {
        SidebarSectionKind.Pinned => input.Pins is { Count: > 0 },
        SidebarSectionKind.Collections => s.Items.Count > 0,
        SidebarSectionKind.Playlists => true,
        SidebarSectionKind.Recent => input.Played is { Count: > 0 },
        SidebarSectionKind.NewReleases => input.NewReleases is { Count: > 0 },
        _ => true,
    };

    static void PlanPins(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        int start = st.Entries.Count;
        var pins = input.Pins;
        if (pins is not null)
            for (int i = 0; i < pins.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var pin = pins[i];
                int at = st.Entries.Count;
                st.Entries.Add(pin);
                st.Rows.Add(new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, 0, at, 0, pin.Id));
                if (pin.IsFolder && !st.Compact && IsExpanded(in input, FolderId(in pin)))
                    AppendPinnedFolderChildren(s, in pin, start, in input, ref st);
            }
        // Empty Pinned is hidden by emptiness — except while a pinnable drag is live: then the dashed band appears here.
        if (st.Entries.Count == start && o.PinDropArmed && !st.Compact)
            st.Rows.Add(Chrome(SidebarRowKind.DropBand, s));
    }

    static void PlanCollections(SidebarSection s, in SidebarProjectionInput input, ref PlanState st)
    {
        var items = s.Items;
        for (int i = 0; i < items.Count; i++)
        {
            string key = items[i];
            // A pinned page is drawn in Pinned (and only there) while the dedupe applies.
            if (st.Dedupe && input.PinnedIds is { } pinned && SidebarPinId.FromRoute(key) is { } pinId && pinned.Contains(pinId))
                continue;
            st.Rows.Add(new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, key));
        }
    }

    static void PlanPlaylists(SidebarSection s, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (input.TreeState == SidebarSourceState.Pending && (tree is null || tree.Count == 0))
        {
            if (!st.Compact) EmitSkeletons(s, ref st);
            return;
        }
        if (tree is null || tree.Count == 0)
        {
            if (!st.Compact) st.Rows.Add(Chrome(SidebarRowKind.Empty, s));
            return;
        }
        string? search = Search(in input);
        int start = st.Rows.Count;
        if (search is not null) PlanFlatTree(s, tree, search, in input, ref st);
        else PlanTree(s, tree, in input, ref st);
        if (st.Rows.Count == start) { if (!st.Compact) st.Rows.Add(Chrome(SidebarRowKind.Empty, s)); return; }
        // The closing gutter: "top level, at the end" is a drop target (never in the rail, never under a search).
        if (!st.Compact && search is null) st.Rows.Add(Chrome(SidebarRowKind.TreeEnd, s));
    }

    /// <summary>The rootlist in its own order, folders honouring the shared expansion set; a pinned subtree is skipped
    /// while the dedupe applies; the rail keeps the top level only.</summary>
    static void PlanTree(SidebarSection s, IReadOnlyList<SidebarLibraryEntry> tree, in SidebarProjectionInput input, ref PlanState st)
    {
        int emitted = 0;
        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count && emitted < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (hidePinDepth >= 0)
            {
                if (e.Depth > hidePinDepth) continue;
                hidePinDepth = -1;
            }
            if (HiddenByPin(in input, in st, in e))
            {
                if (e.IsFolder) hidePinDepth = e.Depth;
                continue;
            }
            if (st.Compact && e.Depth > 0) continue;
            int at = st.Entries.Count;
            st.Entries.Add(e);
            byte d = (byte)System.Math.Min(e.Depth, byte.MaxValue);
            if (e.IsFolder)
            {
                st.Rows.Add(new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, e.Id));
                emitted++;
                if (st.Compact || !IsExpanded(in input, e.FolderId))
                {
                    int myDepth = e.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, at, 0, e.Id));
            emitted++;
        }
    }

    /// <summary>Classic's Ctrl+F filter flattens: matching playlists only, no folder chrome, rootlist order.</summary>
    static void PlanFlatTree(SidebarSection s, IReadOnlyList<SidebarLibraryEntry> tree, string search,
        in SidebarProjectionInput input, ref PlanState st)
    {
        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count && st.Rows.Count < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (hidePinDepth >= 0)
            {
                if (e.Depth > hidePinDepth) continue;
                hidePinDepth = -1;
            }
            if (HiddenByPin(in input, in st, in e))
            {
                if (e.IsFolder) hidePinDepth = e.Depth;
                continue;
            }
            if (e.IsFolder || !SidebarSearch.Matches(in e, search)) continue;
            int at = st.Entries.Count;
            st.Entries.Add(e);
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, at, 0, e.Id));
        }
    }

    static void PlanFeed(SidebarSection s, IReadOnlyList<SidebarLibraryEntry>? src, ref PlanState st)
    {
        if (src is null) return;
        int limit = s.Limit > 0 ? s.Limit : int.MaxValue;
        for (int i = 0; i < src.Count && i < limit; i++)
        {
            int at = st.Entries.Count;
            st.Entries.Add(src[i]);
            st.Rows.Add(new SidebarRow(src[i].IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, 0, at, 0, src[i].Id));
        }
    }

    // ── Library (design P.2a) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Your Library's scroller: the pins (no header, a pin mark on each row), then Liked Songs (no chip or the
    /// Playlists chip), then the mode-shaped list (List rows or Grid strips). The head (Home, dropdown, chips, toolbar) is
    /// fixed chrome above the list and is not planned.</summary>
    static void PlanLibrary(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        var lib = doc.Find(SidebarSectionKind.Library);
        if (lib is null) return;
        var pinned = doc.Find(SidebarSectionKind.Pinned);
        var options = doc.Library;
        string? search = Search(in input);

        int pinRows = 0;
        int pinStart = st.Entries.Count;
        // A drilled level is ONE folder's children: no pins, no drop band, no Liked row (o.Drilled; ShowsLiked reads it).
        if (pinned is { Hidden: false } && !o.Drilled && input.Pins is { } pins)
            for (int i = 0; i < pins.Count && pinRows < DynamicSectionRowCap; i++)
            {
                var pin = pins[i];
                if (!SidebarVisibilityRules.ShowsPinInLibrary(in pin, o.Filter, search, options.HiddenKinds)) continue;
                int at = st.Entries.Count;
                st.Entries.Add(pin);
                st.Rows.Add(new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, pinned.Id, 0, at, 0, pin.Id));
                pinRows++;
                // The wide pane opens a pinned folder IN PLACE, exactly as Classic's Pinned does (its subtree is deduped
                // out of the list below, so this is the only place its children can show). Not in the rail, not under a
                // search (a search flattens), not while the narrow pane drills (its folder click drills instead).
                if (pin.IsFolder && !st.Compact && o.FoldersInline && search is null && IsExpanded(in input, FolderId(in pin)))
                    AppendPinnedFolderChildren(pinned, in pin, pinStart, in input, ref st);
            }
        if (pinRows == 0 && pinned is { Hidden: false } && !o.Drilled && o.PinDropArmed && !st.Compact)
            st.Rows.Add(Chrome(SidebarRowKind.DropBand, pinned));

        bool liked = SidebarVisibilityRules.ShowsLiked(options, o.Filter, search, input.LikedTitle, o.Drilled);
        if (liked)
        {
            int at = st.Entries.Count;
            st.Entries.Add(SidebarLibraryEntry.ForRoute(SidebarCatalogue.LikedRoute, input.LikedTitle ?? ""));
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, lib.Id, 0, at, 0, SidebarCatalogue.LikedRoute));
        }

        var list = input.Library;
        if (input.LibraryState == SidebarSourceState.Pending && (list is null || list.Count == 0))
        {
            if (!st.Compact) EmitSkeletons(lib, ref st);
            return;
        }
        int start = st.Entries.Count;
        if (list is not null)
            for (int i = 0; i < list.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var e = list[i];
                if (st.Compact && e.Depth > 0) continue;   // the rail tiles the top level; a folder tile opens a flyout
                st.Entries.Add(e);
            }
        int count = st.Entries.Count - start;
        // No Empty row in Library: the head owns the empty state (V3Chrome's band in P3-P4; from P5 LibraryHead's, which
        // counts exactly the rows planned here through SidebarLibraryEmptyRules.ScrollerRows). Two owners drew two
        // contradicting messages ("No albums" over "your library is empty"; "empty" above Liked Songs).
        if (count == 0) return;
        if (options.View == SidebarLibraryView.Grid && !st.Compact)
        {
            int cols = System.Math.Clamp(o.GridColumns, 2, 4);
            for (int i = 0; i < count; i += cols)
                st.Rows.Add(new SidebarRow(SidebarRowKind.GridStrip, lib.Id, 0, start + i, System.Math.Min(cols, count - i), lib.Id));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var e = st.Entries[start + i];
            st.Rows.Add(new SidebarRow(e.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, lib.Id,
                (byte)System.Math.Min(e.Depth, byte.MaxValue), start + i, 0, e.Id));
        }
        // Custom order IS the rootlist: its end gutter takes "move to the end" drops.
        if (!st.Compact && input.LibraryIsTree && options.Sort == SidebarLibrarySort.CustomOrder
            && o.Filter is SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists && search is null)
            st.Rows.Add(Chrome(SidebarRowKind.TreeEnd, lib));
    }

    // ── shared plumbing ───────────────────────────────────────────────────────────────────────────────────────────────

    struct PlanState
    {
        public List<SidebarRow> Rows;
        public List<SidebarLibraryEntry> Entries;
        public bool Compact;
        public bool Dedupe;
    }

    static PlanState Begin(SidebarPlanBuffers? buffers)
    {
        var rows = buffers?.Rows ?? new List<SidebarRow>(64);
        var entries = buffers?.Entries ?? new List<SidebarLibraryEntry>(64);
        rows.Clear();
        entries.Clear();
        return new PlanState { Rows = rows, Entries = entries };
    }

    static void Truncate(ref PlanState st, int rowMark, int entryMark)
    {
        if (st.Rows.Count > rowMark) st.Rows.RemoveRange(rowMark, st.Rows.Count - rowMark);
        if (st.Entries.Count > entryMark) st.Entries.RemoveRange(entryMark, st.Entries.Count - entryMark);
    }

    static SidebarRow Chrome(SidebarRowKind kind, SidebarSection s) => new(kind, s.Id, 0, -1, 0, s.Id);

    static void EmitSkeletons(SidebarSection s, ref PlanState st)
    {
        for (int i = 0; i < SkeletonRows; i++) st.Rows.Add(Chrome(SidebarRowKind.Skeleton, s));
    }

    /// <summary>Expand one pinned folder against the canonical flattened rootlist (unchanged rule: descendants keep their
    /// depth RELATIVE to the pinned folder; nested disclosures obey the shared expansion set).</summary>
    static void AppendPinnedFolderChildren(SidebarSection s, in SidebarLibraryEntry pin, int sectionStart,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;
        string folderId = FolderId(in pin);
        int root = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var c = tree[i];
            if (!c.IsFolder) continue;
            if (string.Equals(c.Id, pin.Id, System.StringComparison.Ordinal)
                || string.Equals(FolderId(in c), folderId, System.StringComparison.Ordinal)) { root = i; break; }
        }
        if (root < 0) return;
        int rootDepth = tree[root].Depth;
        for (int i = root + 1; i < tree.Count && st.Entries.Count - sectionStart < DynamicSectionRowCap; i++)
        {
            var child = tree[i];
            if (child.Depth <= rootDepth) break;
            int at = st.Entries.Count;
            st.Entries.Add(child);
            byte rowDepth = (byte)System.Math.Min(System.Math.Max(1, child.Depth - rootDepth), byte.MaxValue);
            st.Rows.Add(new SidebarRow(child.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, rowDepth, at, 0, child.Id));
            if (child.IsFolder && !IsExpanded(in input, FolderId(in child)))
            {
                int collapsedDepth = child.Depth;
                while (i + 1 < tree.Count && tree[i + 1].Depth > collapsedDepth) i++;
            }
        }
    }

    static bool IsExpanded(in SidebarProjectionInput input, string folderId)
        => input.ExpandedFolders is null || input.ExpandedFolders.Contains(folderId);

    static bool HiddenByPin(in SidebarProjectionInput input, in PlanState st, in SidebarLibraryEntry e)
        => st.Dedupe && (e.IsPinned || (input.PinnedIds is { } p && p.Contains(e.Id)));

    static string FolderId(in SidebarLibraryEntry entry)
        => entry.FolderId.Length > 0 ? entry.FolderId : SidebarPinId.FolderIdOf(entry.Id);

    static string? Search(in SidebarProjectionInput input)
    {
        var q = SidebarSearch.Normalize(input.Search);
        return q.Length == 0 ? null : q;
    }
}
```

`Sidebar.Plan(...)` (the named wrapper at the top of `Sidebar.cs`) becomes
`public static SidebarRowPlan Plan(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
SidebarPlanBuffers buffers) => SidebarRowPlanner.Build(doc, in input, in options, buffers);` and `PlanEdit`/`PlanRail`/
`MountKey(SidebarDesign)` are deleted.

`SidebarRowExtents.HeightOf` (rewritten against the section):

```csharp
    public static float HeightOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSection? section)
    {
        if ((uint)index >= (uint)rows.Count || section is null) return 0f;
        return rows[index].Kind switch
        {
            SidebarRowKind.SectionHeader => SidebarRowGeometry.HeaderHeight,
            SidebarRowKind.Divider => SidebarRowGeometry.SeparatorHeight,
            SidebarRowKind.IconRow or SidebarRowKind.SectionTile => SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
            SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader or SidebarRowKind.Skeleton => SidebarRowGeometry.PitchOf(section.Shape),
            SidebarRowKind.Empty => SidebarRowGeometry.EmptyHintHeight + 2f * SidebarRowGeometry.RowMarginY,
            SidebarRowKind.DropBand => SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
            SidebarRowKind.TreeEnd => SidebarRowGeometry.TreeEndHeight,
            _ => float.NaN,   // GridStrip: estimate, correct on measure
        };
    }
```

A glyph row inside an entity section (a route pin in Pinned, the Library's Liked row) takes the SECTION's pitch: an
`EntityRow` is always `PitchOf(section.Shape)` whatever its leading visual (design V.3 "row A inside a row-C section keeps
44 pitch"). `SidebarRowGeometry.ShapeFor`, `CardHeightFor`, `PromptHeight`, `Chip*`, `PinDropZoneRestHeight` are deleted.

`SidebarRowResolve` (rewritten, `Sidebar.cs`):

```csharp
public static class SidebarRowResolve
{
    /// <summary>The route a plan row navigates to: a glyph row's key, an entity row's entry route; null for everything
    /// else (headers, separators, folders, tracks, hints, tiles).</summary>
    public static string? RouteOf(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries) => row.Kind switch
    {
        SidebarRowKind.IconRow => row.Key,
        SidebarRowKind.EntityRow when (uint)row.EntryIndex < (uint)entries.Count => entries[row.EntryIndex].RouteKey,
        _ => null,
    };

    public static bool EntrySelects(in SidebarLibraryEntry entry, string route)
        => route.Length > 0 && entry.RouteKey is { Length: > 0 } r && string.Equals(r, route, System.StringComparison.Ordinal);

    public static bool SelectsRoute(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries, string route)
    {
        if (route.Length == 0 || entries is null) return false;
        if (row.Kind == SidebarRowKind.GridStrip)
        {
            int start = row.EntryIndex, count = row.ItemCount;
            if (start < 0 || count <= 0 || start >= entries.Count) return false;
            if (start + count > entries.Count) count = entries.Count - start;
            for (int i = 0; i < count; i++) if (EntrySelects(entries[start + i], route)) return true;
            return false;
        }
        return RouteOf(in row, entries) is { Length: > 0 } r && string.Equals(r, route, System.StringComparison.Ordinal);
    }

    public static void Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries, string route, List<int> into)
    {
        System.ArgumentNullException.ThrowIfNull(into);
        if (rows is null || entries is null || route.Length == 0) return;
        for (int i = 0; i < rows.Count; i++) { var row = rows[i]; if (SelectsRoute(in row, entries, route)) into.Add(i); }
    }

    // Flipped(...) unchanged.
}
```

### P3.6 The binder rewrite and the feed demand model

#### P3.6.1 Feeds (new file `Shell/Sidebar.Feeds.cs`)

```csharp
// ── Shell/Sidebar.Feeds.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the two feed sections' data: Recently played (resolved synchronously from the play log) and New releases (the What's
// New feed), plus which feeds the active layout demands
//
// Role: CORE (the rules) + SHELL (SidebarResidentPeek, the one entity read)
// Spec: sidebar-rework-implementation.md §P3.6 · design P.3

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Which feed the active layout shows — the binder reads, kicks and folds a feed only while its section is
/// visible (design blocking item 3: "who fetches New releases, when, on what demand").</summary>
[System.Flags]
public enum SidebarFeedDemand : byte
{
    None = 0,
    /// <summary>Recently played: the play log (always folded) + the resident tables a peek reads.</summary>
    Recent = 1,
    /// <summary>New releases: the What's New feed (<c>Notify.Items</c>), refreshed at most every 30 minutes.</summary>
    NewReleases = 2,
}

public static class SidebarFeedDemands
{
    public static SidebarFeedDemand Of(SidebarLayoutDoc doc)
    {
        var d = SidebarFeedDemand.None;
        if (doc.Find(SidebarSectionKind.Recent) is { Hidden: false }) d |= SidebarFeedDemand.Recent;
        if (doc.Find(SidebarSectionKind.NewReleases) is { Hidden: false }) d |= SidebarFeedDemand.NewReleases;
        return d;
    }
}

/// <summary>A played context, newest first, with the title the play log recorded at play time (design P.3 step 1).</summary>
public readonly record struct SidebarPlayedContext(string Uri, SidebarEntryKind Kind, long PlayedAtMs, string? Title = null)
{
    public bool IsTrack => Kind == SidebarEntryKind.Track;
}

/// <summary>A resident row's identity WITHOUT a fetch: a played context was loaded to play, so its row is usually
/// resident (design P.3 step 2). An interface so the rule is tested without Entities.</summary>
public interface ISidebarEntityPeek
{
    bool TryPeek(string uri, out SidebarLibraryEntry entry);
}

/// <summary>RECENTLY PLAYED, the honest path (design P.3): each context resolves SYNCHRONOUSLY, in order — the library
/// projection, then a peek into the resident tables, then the play log's own title with the kind glyph — else it is
/// SKIPPED. Nothing arrives later, so there is no skeleton and rows never jump; a context nobody can name is invisible,
/// never a grey bone (D9).</summary>
public static class SidebarRecentsRules
{
    public static int Resolve(IReadOnlyList<SidebarPlayedContext>? contexts, SidebarSourceIndex index, ISidebarEntityPeek? peek,
                              int max, List<SidebarLibraryEntry> into)
    {
        into.Clear();
        if (contexts is null || max <= 0) return 0;
        for (int i = 0; i < contexts.Count && into.Count < max; i++)
        {
            var c = contexts[i];
            if (c.Uri.Length == 0 || c.IsTrack) continue;    // a bare track play is not a context
            string? id = EntityUri.IsLikedCollection(c.Uri) ? SidebarCatalogue.LikedRoute : SidebarPinId.FromUri(c.Uri);
            if (id is null || Contains(into, id)) continue;
            if (index.TryGet(id, out var known) && known.Name.Length > 0)
            {
                into.Add(known with { SortStamp = c.PlayedAtMs, SourceOrder = into.Count });
                continue;
            }
            if (peek is not null && peek.TryPeek(c.Uri, out var resident) && resident.Name.Length > 0)
            {
                into.Add(resident with { Id = id, Uri = c.Uri, SortStamp = c.PlayedAtMs, SourceOrder = into.Count });
                continue;
            }
            if (c.Title is { Length: > 0 } title)
                into.Add(new SidebarLibraryEntry(id, id == SidebarCatalogue.LikedRoute ? SidebarEntryKind.AppRoute : c.Kind,
                    c.Uri, title, "", StringId.Empty, null, ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs,
                    LastVisitedTicksUtc: 0, SourceOrder: into.Count, Depth: 0, Circular: c.Kind == SidebarEntryKind.Artist,
                    Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "", IdentityKnown = true });
        }
        return into.Count;
    }

    static bool Contains(List<SidebarLibraryEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i].Id, id, System.StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>NEW RELEASES from the What's New feed the notification centre already fetches (<c>Home.Feeds.Refresh</c> →
/// <c>Notify.Items</c>). The section never fetches on its own schedule: it asks the feed owner at most every
/// <see cref="RefreshEveryMs"/> while it is shown, and reads whatever the feed holds.</summary>
public static class SidebarNewReleasesRules
{
    public const long RefreshEveryMs = 30L * 60L * 1000L;

    /// <summary>Ask the feed owner now? Never while it is loading; at once when it has never answered; then every 30 min.</summary>
    public static bool ShouldRefresh(long lastAskedTicks, long nowTicks, Notify.FeedState state)
    {
        if (state == Notify.FeedState.Loading) return false;
        if (lastAskedTicks == 0 || state == Notify.FeedState.Idle) return true;
        return nowTicks - lastAskedTicks >= RefreshEveryMs;
    }

    /// <summary>The feed's release rows, newest first, as entries: a release the projection knows is its real entry
    /// (stamped with the release time); one it does not is built from the feed row's own title, image and creator; a row
    /// with neither a known entry nor a title is skipped.</summary>
    public static int Fill(IReadOnlyList<Notification>? feed, SidebarSourceIndex index, List<SidebarLibraryEntry> into, int max)
    {
        into.Clear();
        if (feed is null || max <= 0) return 0;
        for (int i = 0; i < feed.Count && into.Count < max; i++)
        {
            var n = feed[i];
            if (n.Category != NotifyCategory.NewRelease || !n.Subject.IsValid) continue;
            string uri = n.Subject.Text;
            if (SidebarPinId.FromUri(uri) is not { } id) continue;
            if (index.TryGet(id, out var known) && known.Name.Length > 0)
            {
                into.Add(known with { SortStamp = n.TimestampMs, SourceOrder = into.Count });
                continue;
            }
            if (n.Title.Length == 0) continue;
            var kind = n.ReleaseKind == NewReleaseKind.Episode ? SidebarEntryKind.Show : SidebarEntryKind.Album;
            into.Add(new SidebarLibraryEntry(id, kind, uri, n.Title, n.Creator ?? "",
                n.ImageUrl is { Length: > 0 } url ? Entities.Strings.Intern(url) : StringId.Empty, null,
                ChildCount: 0, AddedAtMs: 0, SortStamp: n.TimestampMs, LastVisitedTicksUtc: 0, SourceOrder: into.Count,
                Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
            { FolderId = "", FolderName = "", FirstArtistName = n.Creator ?? "", IdentityKnown = true });
        }
        return into.Count;
    }

    /// <summary>How many releases arrived since the user last looked (the section's InfoBadge).</summary>
    public static int NewSince(IReadOnlyList<SidebarLibraryEntry> releases, long seenMs)
    {
        int n = 0;
        for (int i = 0; i < releases.Count; i++) if (releases[i].SortStamp > seenMs) n++;
        return n;
    }
}

/// <summary>The production peek: the current scope's tables, read only when the row's identity already landed. Never an
/// Ensure, never a slot allocation (<c>TryGetSlot</c>).</summary>
public sealed class SidebarResidentPeek : ISidebarEntityPeek
{
    public static readonly SidebarResidentPeek Instance = new();

    public bool TryPeek(string uri, out SidebarLibraryEntry entry)
    {
        entry = default;
        var scope = Entities.Current;
        switch (EntityUri.KindOf(uri))
        {
            case EntityKind.Playlist when scope.Playlists.TryGetSlot(uri.AsSpan(), out int ps):
            {
                var p = new Playlist(ps);
                if (!p.Knows(PlaylistFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Playlist, uri, Entities.Strings.Resolve(p.TitleId), Entities.Strings.Resolve(p.Owner.NameId), p.ImageId, false);
                return true;
            }
            case EntityKind.Album when scope.Albums.TryGetSlot(uri.AsSpan(), out int als):
            {
                var a = new Album(als);
                if (!a.Knows(AlbumFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Album, uri, a.Title, "", a.ImageId, false);
                return true;
            }
            case EntityKind.Artist when scope.Artists.TryGetSlot(uri.AsSpan(), out int ars):
            {
                var ar = new Artist(ars);
                if (!ar.Knows(ArtistFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Artist, uri, ar.Name, "", ar.ImageId, true);
                return true;
            }
            case EntityKind.Show when scope.Shows.TryGetSlot(uri.AsSpan(), out int ss):
            {
                var s = new Show(ss);
                if (!s.Knows(ShowFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Show, uri, s.Title, "", s.ImageId, false);
                return true;
            }
            default:
                return false;
        }
    }

    static SidebarLibraryEntry Make(SidebarEntryKind kind, string uri, string name, string creator, StringId cover, bool circular)
        => new("", kind, uri, name, creator, cover, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: circular, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "", IdentityKnown = true };
}
```

`SidebarRecencyFold.PlayedContexts` (`Sidebar.Host.cs`) carries the logged title:
`into.Add(new SidebarPlayedContext(text, KindOf(uri, context), e.PlayedAtMs, context ? e.ContextTitle : null));`.
(`SidebarPlayedContext` moves from `Sidebar.cs` to this file.)

#### P3.6.2 The binder (`Shell/Sidebar.Host.cs`, `SidebarProjectionBinder`)

**Deleted members:** `ISidebarProjectionSnapshot` (the interface and the implementation), `_visited`, `_concerts`,
`_extEntries`, `_slices`, `_cache`, `_observedSourceStates`, `_staleSourceIds`, `_visitedShadow`, `_concertsShadow`,
`_extShadow`, `_host`, `_table`, `_demand`, `_demandLayoutVersion`, `_demandLayout`, `_sourceEpoch`, `FeedDemand`,
`UseHost`, `Sources`, `StateOf(string)`, `AvailabilityOf`, `ContributionCache`, `OnSourceChanged`, `NeedsPrompt`,
`FillFeed`, `ObserveExtensionSources`, `ObserveExtensionSection`, `ObserveSourceState`, `Demand`, `DemandOf`,
`PlaybackEpoch`, `SliceOf`. **New fields:**

```csharp
    readonly ISidebarEntityPeek _peek;
    long _newReleasesAskedTicks;
```

Constructor: `public SidebarProjectionBinder(ISidebarRecencyLogs? logs = null, ISidebarEntityPeek? peek = null)` with
`_peek = peek ?? SidebarResidentPeek.Instance;`. A new method resets the first-seen map after an account swap:

```csharp
    /// <summary>An account swap re-seeds the first-seen proxy from the new account's file (§P3.8).</summary>
    public void ResetFirstSeen() { _firstSeen = null; Invalidate(); }
```

**`Rebuild`:**

```csharp
    void Rebuild()
    {
        var u = User.Me;
        RefreshRecency();
        var recency = _recency;
        var lastPlayed = _logs.LastPlayed;
        var firstSeen = _firstSeen ??= LoadFirstSeen(Sidebar.FirstSeen);
        var doc = Sidebar.Doc;
        var demand = SidebarFeedDemands.Of(doc);

        var build = BuildTarget;
        _lastBuild = build;

        // 1, 2 — THE projection (every kind, folders and children) + the tree slice + the join index. Unchanged.
        var full = SidebarProjection.Build(build.All, in u, SidebarEntryKindMask.All, firstSeen, recency,
                                           includeFolderChildren: true, lastPlayed: lastPlayed);
        SidebarProjection.Build(build.Tree, in u, SidebarEntryKindMask.PlaylistTree, firstSeen, recency,
                                includeFolderChildren: true, lastPlayed: lastPlayed);
        build.Index.Rebuild(build.All);
        _treeState = StateOf(u.RootlistState);
        _libraryState = Worst(StateOf(u.State(LibraryEdgeKind.SavedAlbums)),
                        Worst(StateOf(u.State(LibraryEdgeKind.FollowedArtists)), StateOf(u.State(LibraryEdgeKind.SavedShows))));

        // 3 — the PUBLISHED list Your Library renders: chip → hidden kinds → qualifier → search → sort → pins first.
        // Classic reads none of it, so outside Library this pass is the plain everything-list (cheap, and it keeps the
        // Entries cell honest for the folder flyout).
        bool library = doc.Layout == SidebarLayoutId.Library;
        var options = doc.Library;
        var filter = library ? Sidebar.LibraryFilter.Peek() : SidebarLibraryFilter.None;
        string search = library ? SidebarSearch.Normalize(Sidebar.LibrarySearch.Peek()) : "";
        bool searching = search.Length > 0;
        bool qualifiers = SidebarProjection.QualifiersAvailable(full.FlavorMask);
        var buffer = Entries.Buffer;
        var libResult = SidebarProjection.Build(buffer, in u, SidebarEntryKinds.From(filter), firstSeen, recency,
                                                includeFolderChildren: searching,
                                                isFolderExpanded: searching ? null : _isFolderExpanded,
                                                lastPlayed: lastPlayed, ensureIdentity: true);
        ResolvePins(build.Index, u.RootlistState);
        bool pinnedShown = doc.Find(SidebarSectionKind.Pinned) is { Hidden: false };
        var query = new SidebarLibraryQuery(filter, library ? (SidebarV3Qualifier)Sidebar.V3Qualifier.Peek() : SidebarV3Qualifier.Any,
            library ? options.HiddenKinds : SidebarLibraryKinds.None, options.Sort, options.Descending, search, qualifiers);
        var shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, pinnedShown ? Sidebar.Pins.Items : null);

        // 4 — Recently played: synchronous (index → resident peek → logged title → skip), only while shown.
        _played.Clear();
        if ((demand & SidebarFeedDemand.Recent) != 0)
            SidebarRecentsRules.Resolve(_playedContexts, build.Index, _peek, RecencyCap, _played);

        // 5 — New releases: the What's New feed, kicked at most every 30 minutes, only while shown.
        _newReleases.Clear();
        if ((demand & SidebarFeedDemand.NewReleases) != 0)
        {
            long now = System.Environment.TickCount64;
            if (SidebarNewReleasesRules.ShouldRefresh(_newReleasesAskedTicks, now, Notify.ReleasesState.Peek()))
            {
                _newReleasesAskedTicks = now;
                Notify.RefreshFeeds?.Invoke();
            }
            SidebarNewReleasesRules.Fill(Notify.Items.Peek().Items, build.Index, _newReleases, RecencyCap);
        }

        bool anyPending = AnyContributingKindPending(filter, u);
        var (state, error) = PublishState(shape.Count, anyPending);
        _publishedStage = build;
        bool entriesChanged = Entries.Publish(state, error, anyPending, qualifiers, shape.PinCount, _publishedStage.All);
        bool inputChanged = PublishInput();
        int newStamps = full.NewFirstSeenStamps + libResult.NewFirstSeenStamps;
        if (newStamps > 0) CommitFirstSeen(firstSeen, build.All);
        if (SidebarRevisionGate.ShouldBump(entriesChanged, inputChanged)) _revision++;
        _input = new SidebarProjectionInput(
            Library: _publishedStage.All,
            PlaylistTree: _publishedStage.Tree,
            Pins: _pinRows,
            Played: _played,
            NewReleases: _newReleases,
            PinnedIds: _pinnedIds,
            ExpandedFolders: Sidebar.ExpandedFolders,
            Search: search,
            LibraryState: _libraryState,
            TreeState: _treeState,
            Revision: _revision);
    }
```

**`PublishInput`:**

```csharp
    bool PublishInput()
    {
        bool changed = _pinShadow.Publish(_pinRows, default);
        changed |= _playedShadow.Publish(_played, default);
        changed |= _newReleasesShadow.Publish(_newReleases, default);
        ulong h = SidebarLibraryFingerprint.Seed;
        h = SidebarLibraryFingerprint.Mix(h, (uint)_libraryState);
        h = SidebarLibraryFingerprint.Mix(h, (uint)_treeState);
        long meta = (long)h;
        if (meta != _inputMeta) { _inputMeta = meta; changed = true; }
        if (changed) _inputVersion.Value = _inputVersion.Peek() + 1;
        return changed;
    }
```

**`Read`** (the trigger fold) and **`FeedTables`**:

```csharp
    SidebarBinderTriggers Read()
    {
        var u = User.Me;
        var doc = Sidebar.Doc;
        return new SidebarBinderTriggers(
            LibraryEpoch: LibraryEpoch(in u),
            PinsVersion: Sidebar.Pins.Version.Peek(),
            HistoryVersion: _logs.HistoryVersion,
            PlayLogRevision: _logs.PlayLogVersion,
            LayoutVersion: Sidebar.LayoutVersion.Peek(),
            FolderVersion: Sidebar.FolderVersion.Peek(),
            OrderVersion: 0,
            CultureEpoch: 0,
            V3State: SidebarBinderTriggers.PackV3((int)doc.Layout, (int)Sidebar.LibraryFilter.Peek(), Sidebar.V3Qualifier.Peek(),
                                                  (int)doc.Library.Sort, doc.Library.Descending),
            SearchHash: SidebarSearch.Normalize(Sidebar.LibrarySearch.Peek()).GetHashCode(System.StringComparison.Ordinal),
            SourceEpoch: 0,
            PlaybackEpoch: 0L,
            LibraryRows: SidebarLibraryFingerprint.Of(in u, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinRefs)),
            FeedTables: FeedTables(SidebarFeedDemands.Of(doc)));
    }

    /// <summary>The tables a SHOWN feed reads: the What's New list and its state for New releases; the four entity tables
    /// the resident peek reads for Recently played. Nothing at all for a layout that shows no feed.</summary>
    static long FeedTables(SidebarFeedDemand demand)
    {
        ulong h = SidebarLibraryFingerprint.Seed;
        if ((demand & SidebarFeedDemand.NewReleases) != 0)
        {
            var feed = Notify.Items.Peek();
            h = SidebarLibraryFingerprint.Mix(h, (uint)feed.Items.Count);
            h = SidebarLibraryFingerprint.Mix(h, (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(feed.Items));
            h = SidebarLibraryFingerprint.Mix(h, (uint)Notify.ReleasesState.Peek());
        }
        if ((demand & SidebarFeedDemand.Recent) != 0)
        {
            var scope = Entities.Current;
            h = SidebarLibraryFingerprint.Mix(h, scope.Playlists.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Albums.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Artists.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Shows.Changed.Peek());
        }
        return (long)h;
    }
```

`PublishState(int count, bool anyPending)` drops its `filter` parameter; `AnyContributingKindPending(SidebarLibraryFilter
filter, in User u)` uses `SidebarEntryKinds.From(filter)`.

`SidebarBinderPipeline.Shape` and `Project` take `in SidebarLibraryQuery query` (renamed from `SidebarV3Query`):

```csharp
/// <summary>Your Library's shaping state: the chip, the qualifier (deleted in P5), the hidden kinds, the sort and the search.</summary>
public readonly record struct SidebarLibraryQuery(
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    SidebarV3Qualifier Qualifier = SidebarV3Qualifier.Any,
    SidebarLibraryKinds HiddenKinds = SidebarLibraryKinds.None,
    SidebarLibrarySort Sort = SidebarLibrarySort.Recents,
    bool Descending = false,
    string? Search = null,
    bool QualifiersAvailable = false);
```

`Shape` is rewritten whole (and `Project` loses its `customOrder` parameter the same way; its body is unchanged apart
from `Shape(into, scratch, in query, pins)`). The compaction loop no longer runs only while searching: the kind mask the
projection was built with (`SidebarEntryKinds.From`) maps Podcasts AND Audiobooks to `Show` and knows nothing of the
kinds hidden through Filters, so with no search the Podcasts chip would list audiobooks, the Audiobooks chip podcasts,
and a hidden kind would stay in the unfiltered list (Q12, P.2a). The gate widens to every state the mask cannot express:

```csharp
    /// <summary>The IN-PLACE half of <see cref="Project"/>, for a list that already holds the kinds the filter's MASK wants:
    /// compact by hidden kinds + the chip's exact match (the Podcasts/Audiobooks split the mask cannot express) + qualifier
    /// + search, then sort, then partition pins to the front.</summary>
    public static SidebarEntriesShape Shape(
        List<SidebarLibraryEntry> list,
        List<SidebarLibraryEntry> scratch,
        in SidebarLibraryQuery query,
        IReadOnlyList<SidebarPin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scratch);

        string search = SidebarSearch.Normalize(query.Search);
        bool searching = search.Length > 0;
        byte qualifier = query.QualifiersAvailable ? (byte)query.Qualifier : (byte)0;
        // Not only while searching: the kind mask cannot split Podcasts from Audiobooks (both are Show) and does not know the
        // kinds hidden through Filters, so the loop also runs for a hidden kind and for those two chips.
        bool narrow = searching || qualifier != 0 || query.HiddenKinds != SidebarLibraryKinds.None
                      || query.Filter is SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks;

        if (narrow)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var e = list[read];
                if (SidebarLibraryFilters.IsHidden(query.HiddenKinds, in e)) continue;
                if (query.Filter != SidebarLibraryFilter.None && !SidebarLibraryFilters.Matches(query.Filter, in e)) continue;
                if (qualifier != 0 && (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier(qualifier))) continue;
                if (searching)
                {
                    // Searching FLATTENS: matching leaves only, no folder chrome.
                    if (e.Kind == SidebarEntryKind.Folder) continue;
                    if (!SidebarSearch.Matches(in e, search)) continue;
                }
                list[write++] = e;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        // No custom-order overlay: Custom order is the rootlist's own source order.
        SidebarSort.Apply(list, SidebarSort.Effective(query.Sort, query.Filter), query.Descending, null);
        int band = SidebarProjection.PinsFirst(list, pins, scratch);
        return new SidebarEntriesShape(list.Count, band);
    }
```

`SidebarSort.For/Effective/Apply` take
`SidebarLibrarySort`/`SidebarLibraryFilter`; `SidebarSort.Custom` keeps its rank-map signature but every caller passes
null (pure `SourceOrder`). `SidebarEntryKinds.From(SidebarLibraryFilter)`: Playlists → `PlaylistTree`, Albums →
`Album`, Artists → `Artist`, Podcasts/Audiobooks → `Show`, None → `All`. `SidebarEntryKinds.From(SidebarEntityKinds)` is
deleted.

**The pump** (`Sidebar.UI.cs` `PumpBinder`): drop the queue / track-artists / artist-popular / feed-section / tracks /
episodes / concerts / playback / `LayoutVersion`-of-the-old-document / `V3OrderVersion` / `Design` / V3 reads; read
instead `Sidebar.LayoutVersion.Value`, `Sidebar.Layout.Value`, `Sidebar.LibraryFilter.Value`, `Sidebar.V3Qualifier.Value`,
`Sidebar.LibrarySearch.Value`, `Notify.Items.Value`, `Notify.ReleasesState.Value`. `EnsureBinder()` becomes
`Binder ??= new SidebarProjectionBinder();` (no table, no host, no attach).

**Deleted from `Sidebar.Host.cs`:** `SidebarLibrarySource`, `SidebarPlaylistTreeSource`, `SidebarVisitedSource`,
`SidebarPlayedSource`, `SidebarQueueSource`, `SidebarNowPlayingSource`, `ISidebarDataSourceLifecycle`,
`SidebarArtistTopTracksSource`, `SidebarNewReleaseRow`, `SidebarFeedFetch`, `SidebarNewReleasesSource`,
`SidebarConcertsFetch`, `SidebarConcertsSource`, `WaveeBuiltInDataSources`, `Sidebar.NewReleasesFetch`,
`Sidebar.ConcertsFetch`, `ISidebarEditHost`, `SidebarEditSession`, `SidebarLayoutLoad`, `SidebarLoadFault`,
`SidebarLayoutStore` (→ `SidebarFileStore`, §P3.7), and `SidebarSaveFault` (MOVED, not dropped: it is re-declared in
`Sidebar.Store.cs` without `ConfigTooLarge`, §P3.7). `SidebarPersistenceFault` and `SidebarWriteResult` STAY in
`Sidebar.Host.cs` (the service's `PersistenceHealth` and Settings › Sidebar read them); `SidebarPersistenceFault` loses
`ConfigTooLarge = 5` (its only producer was the per-section config budget) and keeps every other member and value. `Entities/Concert.Page.cs:49` loses its
`Sidebar.ConcertsFetch = …` assignment (the concerts page still fills `Edges.FeedSection` for itself). **Deleted from
`Sidebar.cs`:** the old planner region, `SidebarRailExtents`… (already gone in P2), `SidebarEditPlan`,
`SidebarEditState`, `SidebarSectionDropPayload`, `SidebarQueryPanelShape`, `SidebarNumberEdit`, `SidebarPaletteGroup`,
`SidebarPaletteAdd`, `SidebarPaletteEntry`, `SidebarPalette`, `SidebarDisplayValues`, `SidebarConfigJson`,
`SidebarSectionSlice`, `ISidebarSectionSlices`, `SidebarDataSourceTable`, `SidebarExtensionSlices`,
`SidebarContributionCache`, `SidebarEntriesMeta` (if unused after the binder edit), `SidebarFeedState`,
`SidebarSourceMap` (its `Played`/`Visited` are replaced; `FromTrack` had only queue callers), `SidebarSourceItemType`,
`SidebarSourceFilters`, `SidebarSourceSorts`, `SidebarSourcePaging`, `SidebarConfigFieldKind`, `SidebarConfigField`,
`SidebarConfigSchema`, `SidebarSourceConfig`, `SidebarSourceRequest`, `SidebarContributionAvailability`,
`ISidebarDataSource`, `SidebarDataSourceBase`, `ISidebarContributionHost`, `SidebarContributions`, `SidebarV3Query`.
`SidebarNavLayout` is NOT edited in P3 (`SidebarDropTests.cs`' `SidebarNavLayoutTests` call its three-argument
`Decide`, and no P3 WP owns that test file): `NavExtras` passes `removable: false`, so `Remove` is never set. P4.6 stops
calling it and P5.2 deletes it with its tests.
`SidebarSourceIndex` stays (the binder's join index).

### P3.7 The stores (new file `Shell/Sidebar.Store.cs`)

**`SidebarFileStore`** is today's `SidebarLayoutStore` (moved out of `Sidebar.Host.cs`) made schema-free: it stores
bytes the caller serialized on the UI thread. The new file's header (after the house-style comment block) is:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;
```

(`JsonPropertyName`, `JsonSerializable`, `JsonSourceGenerationOptions`, `JsonSerializerContext` and `JsonSerializer`
are used by the DTOs and the JSON context below; the app has no global usings beyond ImplicitUsings.) Transcribe the
class with these changes and nothing else:

- rename `SidebarLayoutStore` → `SidebarFileStore`; delete `CurrentVersion`, `MaxSectionConfigBytes`, `OversizedConfig`,
  `ForApp`, `DefaultPath`, `PathUnder`, `Load`, `TryRead`, `ReadOutcome`, `DiscardCorrupt`, `WritesBlocked`/`_writesBlocked`
  (the v3 files never block writes: a corrupt file is set aside and the defaults are written over it);
- `Commit(SidebarLayoutDocDto snapshot)` → `Commit(byte[] bytes)`; `_pendingSnapshot` is a `byte[]?`; `WriteNow(byte[] bytes)`
  writes those bytes (the `MaxDocumentBytes` check stays, with `MaxDocumentBytes = 256 * 1024`; the `SerializeToUtf8Bytes`
  line goes);
- keep `CommitDebounceMs = 300`, the timer, `FlushNow`, `WaitForWrites`, `WriteCompleted`, `LastWriteResult`, `SaveFault`,
  `SaveFaultDetail`, `SaveFaulted`, the atomic `File.Replace` + `.bak` rotation, `PublishWriteResult`, `FaultName`;
- `SidebarSaveFault` moves here (above the class), without the config budget:

```csharp
/// <summary>Why the last commit did not land (a write fault never latches: the next good commit clears it).</summary>
public enum SidebarSaveFault : byte
{
    None = 0,
    DocumentTooLarge = 2,   // the serialized file exceeds SidebarFileStore.MaxDocumentBytes (256 KiB)
    IoFailure = 3,          // directory / fsync / atomic-replace failure
}
```

- `Fault(...)` loses its `ConfigTooLarge` arm and `FaultName(...)` loses its `ConfigTooLarge` arm (the enum members are
  gone); every other arm is unchanged:

```csharp
    void Fault(SidebarSaveFault fault, string detail, int bytes = 0, long elapsedMs = 0)
    {
        _saveFault = fault;
        _saveFaultDetail = detail;
        var persistenceFault = fault switch
        {
            SidebarSaveFault.DocumentTooLarge => SidebarPersistenceFault.DocumentTooLarge,
            SidebarSaveFault.IoFailure => SidebarPersistenceFault.IoFailure,
            _ => SidebarPersistenceFault.None,
        };
        PublishWriteResult(new SidebarWriteResult(false, persistenceFault, bytes, elapsedMs, detail));
    }
```

- `Commit` drops the oversized-config refusal and the three `snapshot.Version/UpdatedAtMs/AppVersion` stamps (the
  serializers stamp their own DTOs); the rest of its body is unchanged:

```csharp
    public void Commit(byte[] bytes)
    {
        if (bytes is null) return;
        lock (_stateGate)
        {
            _pendingSnapshot = bytes;
            if (_pendingWrite is null || _pendingWrite.Task.IsCompleted)
                _pendingWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commitTimer ??= new Timer(static state => ((SidebarFileStore)state!).FlushDebounced(), this, Timeout.Infinite, Timeout.Infinite);
            _commitTimer.Change(CommitDebounceMs, Timeout.Infinite);
        }
    }
```

  `FlushDebounced` reads `byte[]? snapshot`; `WriteNow(byte[] bytes)` loses its `if (_writesBlocked) return;` line and
  writes `bytes` where it wrote the serialized DTO;
- add the read half:

```csharp
/// <summary>How reading one sidebar file ended. The caller parses; the store only moves bytes.</summary>
public enum SidebarFileReadOutcome : byte { Missing = 0, Ok = 1, Unreadable = 2 }

public readonly record struct SidebarFileRead(SidebarFileReadOutcome Outcome, byte[]? Bytes)
{
    public static readonly SidebarFileRead Missing = new(SidebarFileReadOutcome.Missing, null);
}
```

```csharp
    public string CorruptPath => _path + ".corrupt";

    /// <summary>The file's bytes (never throws). The caller parses and calls <see cref="MarkCorrupt"/> when it cannot.</summary>
    public SidebarFileRead Read() => ReadAt(_path);

    /// <summary>The rotated backup's bytes — tried when the primary does not parse.</summary>
    public SidebarFileRead ReadBak() => ReadAt(BakPath);

    static SidebarFileRead ReadAt(string path)
    {
        if (!File.Exists(path)) return SidebarFileRead.Missing;
        try { return new SidebarFileRead(SidebarFileReadOutcome.Ok, File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("sidebar", "sidebar.file.unreadable " + ex.GetType().Name);
            return new SidebarFileRead(SidebarFileReadOutcome.Unreadable, null);
        }
    }

    /// <summary>Set an unparseable file aside as <c>.corrupt</c> (the bytes are kept for inspection, never deleted) so the
    /// next commit writes a fresh one. The <c>.bak</c> is left alone.</summary>
    public void MarkCorrupt()
    {
        lock (_writeGate)
        {
            try { if (File.Exists(_path)) File.Move(_path, CorruptPath, overwrite: true); }
            catch (Exception ex) { Log.Warn("sidebar", "sidebar.file.set_aside_failed", ex); }
        }
    }
```

**The wire** (same file):

```csharp
// ── sidebar.json v3 (device) and sidebar.acct-<hash>.json v1 (per account) — design A.4 ────────────────────────────
// Source-generated JSON only (NativeAOT). Null members are omitted, so a default section is just its id.

public sealed class SidebarDeviceFileDto
{
    [JsonPropertyName("v")] public int Version { get; set; }
    public SidebarLayoutsDto? Layouts { get; set; }
}

public sealed class SidebarLayoutsDto
{
    public SidebarOverlayDto? Classic { get; set; }
    public SidebarOverlayDto? Library { get; set; }
}

public sealed class SidebarOverlayDto
{
    public SidebarSectionStateDto[]? Sections { get; set; }
}

public sealed class SidebarSectionStateDto
{
    public string? Id { get; set; }
    public bool? Hidden { get; set; }
    public bool? Collapsed { get; set; }
    public int? Limit { get; set; }
    public string[]? HiddenItems { get; set; }
    public string[]? ItemOrder { get; set; }
    public string? Sort { get; set; }          // "recents" | "recentlyAdded" | "alphabetical" | "creator" | "customOrder"
    public bool? Descending { get; set; }
    public string? View { get; set; }          // "list" | "grid"
    public bool? ShowLiked { get; set; }
}

public sealed class SidebarAccountFileDto
{
    [JsonPropertyName("v")] public int Version { get; set; }
    /// <summary>The account key in clear, for diagnostics and the hash-collision check (the file NAME is the hash).</summary>
    public string? Account { get; set; }
    public bool MigratedToServer { get; set; }
    /// <summary>Every pin id in the user's order. Syncable ids are order hints (membership is the converged server set).</summary>
    public string[]? Pins { get; set; }
    /// <summary>The records the server never has: route, module and wavee: playlist pins.</summary>
    public SidebarLocalPinDto[]? Local { get; set; }
    /// <summary>Last-known names of synced pins (design Q9: a pending pin paints its last name, never blank).</summary>
    public Dictionary<string, string>? Names { get; set; }
    public string[]? ExpandedFolders { get; set; }
    public long NewReleasesSeenMs { get; set; }
    public SidebarFirstSeenDto[]? FirstSeen { get; set; }
}

public sealed class SidebarLocalPinDto
{
    public string? Id { get; set; }
    public string? Kind { get; set; }          // SidebarPinWire.KindName
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public long AddedAtMs { get; set; }
}

/// <summary>A playlist's first-observation stamp (the "recently added" proxy, <see cref="SidebarFirstSeen"/>).</summary>
public readonly record struct SidebarFirstSeenDto(string Id, long Ms);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SidebarDeviceFileDto))]
[JsonSerializable(typeof(SidebarAccountFileDto))]
public sealed partial class SidebarStoreJsonCtx : JsonSerializerContext { }

/// <summary>The pin kind's wire string (shared by the account file and the v2 reader).</summary>
public static class SidebarPinWire
{
    public static string KindName(SidebarEntryKind k) => k switch
    {
        SidebarEntryKind.Playlist => "playlist",
        SidebarEntryKind.Album => "album",
        SidebarEntryKind.Artist => "artist",
        SidebarEntryKind.Show => "show",
        SidebarEntryKind.Folder => "playlistFolder",
        SidebarEntryKind.Track => "track",
        _ => "appRoute",
    };

    public static bool TryParseKind(string? s, out SidebarEntryKind kind)
    {
        switch (s)
        {
            case "playlist": kind = SidebarEntryKind.Playlist; return true;
            case "album": kind = SidebarEntryKind.Album; return true;
            case "artist": kind = SidebarEntryKind.Artist; return true;
            case "show": kind = SidebarEntryKind.Show; return true;
            case "playlistFolder": kind = SidebarEntryKind.Folder; return true;
            case "appRoute": kind = SidebarEntryKind.AppRoute; return true;
            case "track": kind = SidebarEntryKind.Track; return true;
            default: kind = default; return false;
        }
    }
}
```

**The device file mapping:**

```csharp
/// <summary><c>sidebar.json</c> v3 ⇄ <see cref="SidebarLayoutState"/>. Unknown section ids are dropped (and returned for
/// the caller's log); missing sections are merged from the catalogue; a section at its defaults writes only its id.</summary>
public static class SidebarStoreV3
{
    public const int Version = 3;
    public const string FileName = "sidebar.json";

    public static string PathUnder(string profileDir) => Path.Combine(profileDir, FileName);

    public static byte[] Serialize(SidebarLayoutState state)
        => JsonSerializer.SerializeToUtf8Bytes(ToDto(state), SidebarStoreJsonCtx.Default.SidebarDeviceFileDto);

    /// <summary>False for anything that is not a readable v3 document (the caller then tries the .bak, then defaults).</summary>
    public static bool TryParse(byte[] bytes, out SidebarLayoutState state, List<string>? dropped = null)
    {
        state = SidebarLayoutState.Default;
        SidebarDeviceFileDto? dto;
        try { dto = JsonSerializer.Deserialize(bytes, SidebarStoreJsonCtx.Default.SidebarDeviceFileDto); }
        catch (Exception) { return false; }
        if (dto is null || dto.Version != Version) return false;
        state = new SidebarLayoutState(
            SidebarLayoutRules.MergeWithCatalogue(FromDto(SidebarLayoutId.Classic, dto.Layouts?.Classic), dropped),
            SidebarLayoutRules.MergeWithCatalogue(FromDto(SidebarLayoutId.Library, dto.Layouts?.Library), dropped));
        return true;
    }

    public static SidebarDeviceFileDto ToDto(SidebarLayoutState state) => new()
    {
        Version = Version,
        Layouts = new SidebarLayoutsDto { Classic = ToDto(state.Classic), Library = ToDto(state.Library) },
    };

    static SidebarOverlayDto ToDto(LayoutOverlay overlay)
    {
        var sections = new SidebarSectionStateDto[overlay.Sections.Count];
        for (int i = 0; i < sections.Length; i++)
        {
            var s = overlay.Sections[i];
            sections[i] = new SidebarSectionStateDto
            {
                Id = s.Id,
                Hidden = s.Hidden ? true : null,
                Collapsed = s.Collapsed ? true : null,
                Limit = s.Limit,
                HiddenItems = s.HiddenItems is { Count: > 0 } h ? [.. h] : null,
                ItemOrder = s.ItemOrder is { Count: > 0 } o ? [.. o] : null,
                Sort = s.Sort is { } sort ? SortName(sort) : null,
                Descending = s.Descending,
                View = s.View is { } view ? (view == SidebarLibraryView.Grid ? "grid" : "list") : null,
                ShowLiked = s.ShowLiked,
            };
        }
        return new SidebarOverlayDto { Sections = sections };
    }

    static LayoutOverlay FromDto(SidebarLayoutId layout, SidebarOverlayDto? dto)
    {
        if (dto?.Sections is not { } list) return SidebarCatalogue.DefaultOverlay(layout);
        var sections = new List<SectionState>(list.Length);
        for (int i = 0; i < list.Length; i++)
        {
            var d = list[i];
            if (d?.Id is not { Length: > 0 } id) continue;
            sections.Add(new SectionState(id,
                Hidden: d.Hidden ?? false,
                Collapsed: d.Collapsed ?? false,
                Limit: d.Limit,
                HiddenItems: d.HiddenItems,
                ItemOrder: d.ItemOrder,
                Sort: TryParseSort(d.Sort, out var sort) ? sort : null,
                Descending: d.Descending,
                View: d.View switch { "grid" => SidebarLibraryView.Grid, "list" => SidebarLibraryView.List, _ => null },
                ShowLiked: d.ShowLiked));
        }
        return new LayoutOverlay(layout, sections.ToArray());
    }

    public static string SortName(SidebarLibrarySort s) => s switch
    {
        SidebarLibrarySort.RecentlyAdded => "recentlyAdded",
        SidebarLibrarySort.Alphabetical => "alphabetical",
        SidebarLibrarySort.Creator => "creator",
        SidebarLibrarySort.CustomOrder => "customOrder",
        _ => "recents",
    };

    public static bool TryParseSort(string? s, out SidebarLibrarySort sort)
    {
        switch (s)
        {
            case "recents": sort = SidebarLibrarySort.Recents; return true;
            case "recentlyAdded": sort = SidebarLibrarySort.RecentlyAdded; return true;
            case "alphabetical": sort = SidebarLibrarySort.Alphabetical; return true;
            case "creator": sort = SidebarLibrarySort.Creator; return true;
            case "customOrder": sort = SidebarLibrarySort.CustomOrder; return true;
            default: sort = SidebarLibrarySort.Recents; return false;
        }
    }
}
```

**The account file mapping:**

```csharp
/// <summary>One account's sidebar data (design C.6): its pins in order (synced membership + local records), the latch,
/// the expanded folders, the New-releases watermark and the first-seen stamps.</summary>
public sealed record SidebarAccountData(
    string Key,
    bool MigratedToServer,
    IReadOnlyList<SidebarPin> Pins,
    IReadOnlyList<string> ExpandedFolders,
    long NewReleasesSeenMs,
    IReadOnlyList<SidebarFirstSeenDto> FirstSeen);

public static class SidebarAccountStore
{
    public const int Version = 1;
    public const int MaxPins = 2000;
    public const string PendingFileName = "sidebar.acct-pending.json";

    public static SidebarAccountData Empty(string key) => new(key, false, [], [], 0L, []);

    /// <summary>The account's file name; the signed-out key is the "pending" file a migration parks old pins in.</summary>
    public static string FileNameOf(string accountKey)
        => SidebarAccountKey.IsSignedOut(accountKey) ? PendingFileName : "sidebar.acct-" + SidebarAccountKey.Hash(accountKey) + ".json";

    public static byte[] Serialize(SidebarAccountData data)
    {
        var order = new string[data.Pins.Count];
        var local = new List<SidebarLocalPinDto>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < order.Length; i++)
        {
            var p = data.Pins[i];
            order[i] = p.Id;
            if (PinSyncRules.IsSyncable(p.Id, ""))
            {
                if (p.Name.Length > 0) names[p.Id] = p.Name;
            }
            else local.Add(new SidebarLocalPinDto
            {
                Id = p.Id, Kind = SidebarPinWire.KindName(p.Kind), Uri = p.Uri.Length > 0 ? p.Uri : null,
                Name = p.Name.Length > 0 ? p.Name : null, AddedAtMs = p.AddedAtMs,
            });
        }
        var dto = new SidebarAccountFileDto
        {
            Version = Version,
            Account = data.Key.Length > 0 ? data.Key : null,
            MigratedToServer = data.MigratedToServer,
            Pins = order.Length > 0 ? order : null,
            Local = local.Count > 0 ? local.ToArray() : null,
            Names = names.Count > 0 ? names : null,
            ExpandedFolders = data.ExpandedFolders.Count > 0 ? [.. data.ExpandedFolders] : null,
            NewReleasesSeenMs = data.NewReleasesSeenMs,
            FirstSeen = data.FirstSeen.Count > 0 ? [.. data.FirstSeen] : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(dto, SidebarStoreJsonCtx.Default.SidebarAccountFileDto);
    }

    /// <summary>False for anything that is not a readable v1 account file. Fixed routes (Home, Liked) are never pins
    /// (Q1a); an id that names neither a syncable entity nor a local record is dropped; the list is capped.</summary>
    public static bool TryParse(byte[] bytes, string key, out SidebarAccountData data)
    {
        data = Empty(key);
        SidebarAccountFileDto? dto;
        try { dto = JsonSerializer.Deserialize(bytes, SidebarStoreJsonCtx.Default.SidebarAccountFileDto); }
        catch (Exception) { return false; }
        if (dto is null || dto.Version != Version) return false;
        var local = new Dictionary<string, SidebarLocalPinDto>(StringComparer.Ordinal);
        if (dto.Local is { } records)
            for (int i = 0; i < records.Length; i++)
                if (records[i]?.Id is { Length: > 0 } id) local[id] = records[i];
        var pins = new List<SidebarPin>(dto.Pins?.Length ?? 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (dto.Pins is { } ids)
            for (int i = 0; i < ids.Length && pins.Count < MaxPins; i++)
            {
                string? id = ids[i];
                if (string.IsNullOrEmpty(id) || SidebarPinRules.IsFixedRoute(id) || !seen.Add(id)) continue;
                if (local.TryGetValue(id, out var rec))
                {
                    if (!SidebarPinWire.TryParseKind(rec.Kind, out var kind)) continue;
                    pins.Add(new SidebarPin(id, kind, rec.Uri ?? "", rec.Name ?? "", rec.AddedAtMs));
                    continue;
                }
                if (!PinSyncRules.IsSyncable(id, "")) continue;
                string name = dto.Names is { } n && n.TryGetValue(id, out var cached) ? cached : "";
                pins.Add(new SidebarPin(id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), name, 0L));
            }
        data = new SidebarAccountData(key, dto.MigratedToServer, pins, dto.ExpandedFolders ?? [], dto.NewReleasesSeenMs,
            dto.FirstSeen ?? []);
        return true;
    }
}

/// <summary>Whose sidebar account file is live (design C.6): <c>provider:account</c> of the catalog scope; an empty
/// account is signed out. Market, locale, tier and explicit-filter switches change the scope but NOT this key.</summary>
public static class SidebarAccountKey
{
    public static string Of(in CatalogScope scope) => scope.Account.Length == 0 ? "" : scope.Provider + ":" + scope.Account;

    public static bool IsSignedOut(string key) => key.Length == 0;

    /// <summary>FNV-1a 64 over the key's UTF-16 code units, 16 lowercase hex digits — a stable, path-safe file name.</summary>
    public static string Hash(string key)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < key.Length; i++) { h ^= key[i]; h *= 1099511628211UL; }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>The per-account <c>migratedToServer</c> latch and THE ACCOUNT GUARD <c>LibraryPinSync</c> consults before
/// converging the server set onto the store and before writing a local change to the server (Q1b).</summary>
public sealed class SidebarPinLatch
{
    readonly Func<string> _loadedKey;
    readonly Func<string> _liveKey;
    readonly Func<bool> _get;
    readonly Action<bool> _set;

    public SidebarPinLatch(Func<string> loadedAccountKey, Func<string> liveAccountKey, Func<bool> get, Action<bool> set)
    {
        _loadedKey = loadedAccountKey;
        _liveKey = liveAccountKey;
        _get = get;
        _set = set;
    }

    public bool Get() => _get();
    public void Set(bool migrated) => _set(migrated);

    /// <summary>The pin store holds the LIVE account's pins (and someone is signed in). False during the window between a
    /// scope switch and the store's reload: nothing may cross between two accounts then.</summary>
    public bool Matches
    {
        get
        {
            string loaded = _loadedKey();
            return loaded.Length > 0 && string.Equals(loaded, _liveKey(), StringComparison.Ordinal);
        }
    }
}
```

`SidebarFirstSeenDto` is deleted from its old home (it moves here).

### P3.8 Accounts and the safe swap (new file `Shell/Sidebar.Accounts.cs`; `Spotify/Spotify.Encode.cs`; `Spotify/Spotify.Library.cs`)

`Shell/Sidebar.Accounts.cs` — a `Sidebar` partial (header: "the per-account half of the sidebar service: which account's
pins are loaded, the safe swap, the account file's commit"; the house-style comment block, then this using header):

```csharp
using System.Globalization;

namespace Wavee;

public static partial class Sidebar
{
    static SidebarAccountData s_account = SidebarAccountStore.Empty("");
    static SidebarFileStore? s_accountFile;
    static bool s_accountLoaded;
    static long s_newReleasesSeenMs;

    /// <summary>The account whose pins are loaded ("" = signed out: no pins, no "Pin to sidebar").</summary>
    public static string AccountKey => s_account.Key;

    /// <summary>The latch + guard the pin bridge is built with (Spotify.Library.Install).</summary>
    public static SidebarPinLatch PinLatch { get; } = new(
        static () => s_account.Key,
        static () => SidebarAccountKey.Of(Entities.Current.Key),
        static () => s_account.MigratedToServer,
        SetMigrated);

    /// <summary>Raised after an account swap, on the UI thread (P4: the undo ring clears and closes its toasts).</summary>
    internal static Action? AccountSwapped;

    /// <summary>THE ONE ACCOUNT EDGE. Idempotent and cheap: a scope change that keeps the account (market, locale, tier)
    /// returns false at the key compare. Called by <see cref="Boot"/>, by the pane's ScopeEpoch effect, and — synchronously,
    /// before any pin of the new scope is converged — by <c>Spotify.Library.AfterPublish</c>'s scope-change branch.
    /// Returns true only when it swapped the loaded account.</summary>
    public static bool EnsureAccount(in CatalogScope scope)
    {
        string key = SidebarAccountKey.Of(scope);
        if (s_accountLoaded && string.Equals(key, s_account.Key, StringComparison.Ordinal)) return false;
        SwapAccount(key);
        return true;
    }

    static void SwapAccount(string key)
    {
        // 1 — the outgoing account's file is written from ITS store, now, before the store is reloaded: nothing of the new
        //     account can reach it (the bridge's guard refuses while the keys differ).
        if (s_accountLoaded)
        {
            CommitAccount();
            s_accountFile?.FlushNow();
        }
        // 2 — Edit mode and the undo ring never span accounts; the Library search is per session and per account.
        Editing.SetIfChanged(false);
        LibrarySearch.SetIfChanged("");
        LibrarySearchOpen.SetIfChanged(false);
        AccountSwapped?.Invoke();
        // 3 — load the incoming account (adopting a migration's pending pins on the first sign-in).
        s_accountFile = SidebarAccountKey.IsSignedOut(key) ? null : new SidebarFileStore(Path.Combine(s_profileDir, SidebarAccountStore.FileNameOf(key)));
        s_account = LoadAccount(key, s_accountFile);
        s_accountLoaded = true;
        s_newReleasesSeenMs = s_account.NewReleasesSeenMs;
        Pins.LoadFrom(s_account.Pins);                         // silent: no OnLocalPinChanged, no write
        s_expandedFolders.Clear();
        for (int i = 0; i < s_account.ExpandedFolders.Count; i++) s_expandedFolders.Add(s_account.ExpandedFolders[i]);
        s_folderVersion.Value = s_folderVersion.Peek() + 1;
        s_firstSeen = s_account.FirstSeen.Count > 0 ? [.. s_account.FirstSeen] : null;
        Binder?.ResetFirstSeen();
        Log.Info("sidebar", "account.loaded key=" + (key.Length == 0 ? "signed-out" : SidebarAccountKey.Hash(key))
                            + " pins=" + Pins.Count.ToString(CultureInfo.InvariantCulture));
    }

    static SidebarAccountData LoadAccount(string key, SidebarFileStore? file)
    {
        if (file is null) return SidebarAccountStore.Empty("");
        // A migration that ran signed out parked the old global pins: the first account that signs in adopts them.
        string pending = Path.Combine(s_profileDir, SidebarAccountStore.PendingFileName);
        if (!File.Exists(file.FilePath) && File.Exists(pending))
        {
            try { File.Move(pending, file.FilePath); Log.Info("sidebar", "account.adopted_pending"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("sidebar", "account.adopt_failed", ex); }
        }
        var read = file.Read();
        if (read.Outcome == SidebarFileReadOutcome.Ok && SidebarAccountStore.TryParse(read.Bytes!, key, out var data)) return data;
        if (read.Outcome == SidebarFileReadOutcome.Missing) return SidebarAccountStore.Empty(key);
        // Corrupt (design corner case): set aside; the mirror's membership comes back through the next ApplyServer, the
        // latch starts false (the next converged walk pushes nothing and sweeps nothing until it latches).
        var bak = file.ReadBak();
        if (bak.Outcome == SidebarFileReadOutcome.Ok && SidebarAccountStore.TryParse(bak.Bytes!, key, out var recovered)) return recovered;
        file.MarkCorrupt();
        Log.Warn("sidebar", "account.file_corrupt key=" + SidebarAccountKey.Hash(key));
        return SidebarAccountStore.Empty(key);
    }

    /// <summary>Snapshot the account's pins, folders, watermark and first-seen stamps into its file (coalesced 300 ms,
    /// atomic, .bak). The pin store's OnChanged and every folder / first-seen / watermark write land here.</summary>
    static void CommitAccount()
    {
        s_commitPending = false;
        if (s_accountFile is null || !s_accountLoaded) return;
        var pins = new SidebarPin[Pins.Count];
        for (int i = 0; i < pins.Length; i++) pins[i] = Pins[i];
        var folders = new string[s_expandedFolders.Count];
        s_expandedFolders.CopyTo(folders);
        s_account = s_account with
        {
            Pins = pins,
            ExpandedFolders = folders,
            NewReleasesSeenMs = s_newReleasesSeenMs,
            FirstSeen = s_firstSeen ?? [],
        };
        s_accountFile.Commit(SidebarAccountStore.Serialize(s_account));
    }

    static void SetMigrated(bool migrated)
    {
        if (s_account.MigratedToServer == migrated) return;
        s_account = s_account with { MigratedToServer = migrated };
        CommitAccount();
    }

    /// <summary>The user looked at New releases (expanded it or opened a row): the badge counts from now.</summary>
    public static void MarkNewReleasesSeen()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now <= s_newReleasesSeenMs) return;
        s_newReleasesSeenMs = now;
        CommitAccount();
    }

    public static long NewReleasesSeenMs => s_newReleasesSeenMs;
}
```

`Spotify/Spotify.Encode.cs` `LibraryPinSync` — constructor and three members change:

```csharp
    readonly SidebarPinStore _pins;
    readonly SidebarPinLatch _latch;
    readonly Action<string, bool> _write;
    readonly Func<string, bool> _hasPending;
    readonly Func<string, string>? _routeTitle;
    readonly Action<SidebarPin, bool> _onLocal;

    /// <param name="latch">The loaded account's migration latch and the ACCOUNT GUARD (Q1b): while the store holds
    /// another account's pins, the server set is never converged onto it and no local change is written.</param>
    public LibraryPinSync(SidebarPinStore pins, SidebarPinLatch latch, Action<string, bool> write, Func<string, bool> hasPending,
                          Func<string, string>? routeTitle = null)
    {
        _pins = pins;
        _latch = latch;
        _write = write;
        _hasPending = hasPending;
        _routeTitle = routeTitle;
        _onLocal = OnLocalPinChanged;
        _pins.OnLocalPinChanged = _onLocal;
    }

    /// <summary>Has the loaded account's one-time local → server migration run? Re-read every time (no cached copy:
    /// an account swap changes it).</summary>
    public bool Migrated => _latch.Get();

    /// <summary>Converge the store onto the server set. Returns false (and changes nothing) when the store holds another
    /// account's pins — the swap has not run yet for this scope.</summary>
    public bool ApplyServer(IReadOnlyList<PinWire> server, bool converged)
    {
        if (!_latch.Matches) return false;
        // … the oldest-first ordering and the mapping loop are unchanged (TryPinId now refuses Liked/Home, §P3.9) …
        bool migrated = _latch.Get();
        bool removeMissing = converged && migrated;
        _pins.ApplyRemote(mapped, IsSweepable, removeMissing);
        if (!migrated && converged) Migrate(mapped);
        return true;
    }

    void OnLocalPinChanged(SidebarPin pin, bool pinned)
    {
        if (!_latch.Matches) return;                                   // never write account A's change to account B
        if (PinSyncRules.TryWireUri(pin.Id, "") is not { } uri) return;   // not syncable — a silent local pin
        _write(uri, pinned);
    }
```

`Migrate` ends with `_latch.Set(true);` (replacing `_migrated = true; _settings.Set(...)`). The class doc's
`Platform.Keys.PinsMigratedToServer` reference becomes "the loaded account's latch (`SidebarPinLatch`)".

`Spotify/Spotify.Library.cs`:

- `Install`: `s_pinSync = new LibraryPinSync(Sidebar.Pins, Sidebar.PinLatch, WritePin, IsPinWritePending, static id =>
  "");` (the Liked title arm is dead: `liked` is never a pin now).
- `AfterPublish`'s scope-change branch (`if (!ReferenceEquals(scope, s_seenScope)) { … }`), at its end:

```csharp
                // Q1b: the sidebar's pin store follows the ACCOUNT before any pin of this scope is converged onto it (the pane's
                // own ScopeEpoch effect may run later, in another runtime). The grace marks are forgotten ONLY when the
                // account really changed: a market / locale / tier switch keeps the account, and a mark dropped there would
                // let the next converged walk sweep a pin whose ylpin write is still in flight (IsSweepable).
                Sidebar.EnsureAccount(scope.Key);
                s_pinWrites.Rebind(Sidebar.AccountKey);
```

  (`Rebind` compares account KEYS instead of trusting `EnsureAccount`'s return value: when the pane's ScopeEpoch effect
  swapped first, `EnsureAccount` returns false here, yet the marks still belong to the old account.)

- The marks become a small testable class. Delete `PinWriteGraceMs`, the `Dictionary<string, long> s_pinWrites`,
  `NotePinWrite` and `IsPinWritePending` (`Spotify.Library.cs:621-629`) and write in their place:

```csharp
        // A pin whose write is in flight — or settled in the last minute, before any read could reflect it — is never
        // swept by the bridge: a pins read that raced the write would otherwise un-pin what the user just pinned.
        static readonly PinWriteMarks s_pinWrites = new();

        static void NotePinWrite(string uri, bool inFlight) => s_pinWrites.Note(uri, inFlight);

        static bool IsPinWritePending(string uri) => s_pinWrites.IsPending(uri);
```

  and in `Spotify/Spotify.Encode.cs`, directly above `LibraryPinSync` (same namespace, public so Wavee.Tests pins it):

```csharp
/// <summary>The pin writes the bridge must not sweep (<see cref="LibraryPinSync"/>'s <c>hasPending</c>): in flight, or
/// settled less than <see cref="GraceMs"/> ago. The marks belong to ONE account: <see cref="Rebind"/> forgets them only
/// when the account changes, never on a market / locale / tier switch of the same account (Q1b).</summary>
public sealed class PinWriteMarks
{
    public const long GraceMs = 60_000;
    readonly Dictionary<string, long> _at = new(StringComparer.Ordinal);
    readonly Func<long> _now;
    string _account = "";

    public PinWriteMarks(Func<long>? now = null) => _now = now ?? (static () => Environment.TickCount64);

    public int Count => _at.Count;

    public void Note(string uri, bool inFlight) => _at[uri] = inFlight ? long.MaxValue : _now();

    public bool IsPending(string uri)
        => _at.TryGetValue(uri, out long at) && (at == long.MaxValue || _now() - at < GraceMs);

    /// <summary>The live account is <paramref name="accountKey"/>. Another account's marks describe ITS pins, so they go;
    /// the same account keeps them. An old account's write that settles AFTER this call still lands here:
    /// <c>SettleCollection</c> notes the settle before its scope check (it must — on a same-account market switch the
    /// in-flight mark would otherwise never clear), so it leaves a <see cref="GraceMs"/> mark in the new account's set.
    /// That only delays a sweep of that uri by a minute, and is harmless.</summary>
    public void Rebind(string accountKey)
    {
        if (string.Equals(accountKey, _account, StringComparison.Ordinal)) return;
        _account = accountKey;
        _at.Clear();
    }
}
```

The pane's own effect (`Sidebar.UI.cs`, `PaneHost.Render`, after `UseSignalEffect(PumpBinder)`):

```csharp
            // The account edge (design C.6): idempotent — a market/locale/tier switch keeps the account and returns at once.
            UseSignalEffect(static () =>
            {
                _ = Entities.ScopeEpoch.Value;
                if (Entities.Current is { } scope) EnsureAccount(scope.Key);
            });
```

### P3.9 Liked and Home are never pins (Q1a) (`Shell/Sidebar.cs`, `Shell/Sidebar.Rules.cs`)

`Sidebar.Rules.cs` gains:

```csharp
/// <summary>The fixed-home routes (design D10 + Q1a): Home is always first and Liked Songs has a fixed home in each
/// layout, so neither is EVER a pin — not by the menu, not by a drop, not from the server (Spotify pins Liked Songs by
/// default; that server pin stays on the server, untouched, and never reaches the sidebar), not from a migrated file.</summary>
public static class SidebarPinRules
{
    public static bool IsFixedRoute(string? pinId) => pinId is "home" or "liked";

    /// <summary>The drag chip's caption key when a drag over Pinned (a pin row or the empty "Drop here to pin" band) can
    /// NOT be pinned — design corner case "Pin of an unpinnable thing (track, episode, local file, search result, Home)":
    /// the menu item is absent (<c>PinRowRule</c>) and a drop is refused with a reason. A search result is one of these
    /// kinds, so it needs no arm. Null when the payload pins. Order: a local file before the plain track (a local file
    /// IS a track payload), then the fixed routes.</summary>
    public static string? RefusalKeyOf(DragKind kind, string? id, string? uri)
    {
        if (kind == DragKind.Track && uri is { } u
            && (u.StartsWith("wavee:local:", System.StringComparison.Ordinal) || u.StartsWith("local:", System.StringComparison.Ordinal)))
            return "sidebar.pin.cantPin.local";
        if (kind == DragKind.Track) return "sidebar.pin.cantPin.track";
        if (kind == DragKind.Episode) return "sidebar.pin.cantPin.episode";
        if (kind == DragKind.Route && id == "home") return "sidebar.pin.cantPin.home";
        if (kind == DragKind.Route && id == "liked") return "sidebar.pin.cantPin.liked";
        return null;
    }
}
```

(`DragKind` is the app's `Platform/Drag.cs` enum, namespace `Wavee`: the file stays engine-free.)

**The drop refusal over Pinned** (`Sidebar.UI.Drop.cs` `ResourceDropSpec`, P3-WP13; both the pin rows and the empty
band's `DropBandRow` use this spec, `slot >= 0` being the pinned region). Today a track crossing a pinned row is
TRANSPARENT and nothing explains why it does not pin; from P3 it is REFUSED with the caption. Add one local and edit three
local functions:

```csharp
            // The pinned region refuses what can never be a pin, WITH the reason (design corner case). A pinned PLAYLIST
            // row keeps its own track answer: it deposits, or refuses with "can't edit this playlist" (WhyRefused's
            // existing arm), which names the real reason better than "Tracks can't be pinned".
            string? PinRefusal(DragPayload source)
                => slot >= 0 && !(playlistRow && source.CanCopyTracks)
                    ? SidebarPinRules.RefusalKeyOf(source.Kind, source.Id, source.Uri)
                    : null;
```

- `Compatible(source)`: after its `Filing` line, `if (PinRefusal(source) is not null) return false;`.
- `Transparent(source)`: first line `if (PinRefusal(source) is not null) return false;` (aimed here: refuse, never pass
  through).
- `WhyRefused(source)`: first line `if (PinRefusal(source) is { } pinKey) return Loc.Get(pinKey);`.
- `CaptionFor`/`CommitDrop` are unchanged (a refused drop never commits).

`DragKind.Route` with `Id == "home"`/`"liked"` is `CanPin` (kind-only), so `Drag.LivePinnable()` still opens the empty
band for it — and the band then refuses it with "Home is always first" / "Liked Songs always has its own row", which is
the design's wording. `PinWithToast`'s `IsFixedRoute` refusal (below) stays as the commit-side guard.

`Sidebar.cs`:

- `SidebarPinId.PinnableRoutes` = `["search", "albums", "artists", "podcasts", "audiobooks", "local", "history", "recents"]`.
- `SidebarPinId.FromRoute` (the recogniser `IsPinnableRoute`, `PaneView.PinWithToast` and the drop specs consult) gains,
  right after its `IsNullOrWhiteSpace` guard: `if (SidebarPinRules.IsFixedRoute(routeKey)) return null;`. (`Canonical`
  still maps `spotify:collection` → `liked` through `FromUri`; the store's refusal below is what stops it.)
- `PinSyncRules.TryWireUri`: delete the `liked` arm; add `if (SidebarPinRules.IsFixedRoute(pinId)) return null;` first.
- `PinSyncRules.TryPinId`: `var id = SidebarPinId.FromUri(wireUri); return id is null || SidebarPinRules.IsFixedRoute(id) ? null : id;`
  (folder arm unchanged).
- `PinSyncRules.LikedWireUri` is deleted. Its one other reader, `Spotify/Spotify.Encode.cs:94`
  (`public const string LikedPinUri = PinSyncRules.LikedWireUri;`, read by `PinWires`' `PinKind.Liked` arm at :714), keeps
  the constant as a literal (P3-WP15) — the wire still carries the server's Liked pin, the sidebar just never maps it:

```csharp
        /// <summary>The wire uri Liked Songs pins as on the server. <see cref="PinWires"/> still reports it (the server pin
        /// is real and stays untouched); <c>PinSyncRules.TryPinId</c> refuses it, so it never becomes a sidebar pin (Q1a).</summary>
        public const string LikedPinUri = "spotify:collection";
```
- `PinRowRule.Decide`: `if (!hasStore || string.IsNullOrEmpty(pinId) || SidebarPinRules.IsFixedRoute(pinId)) return PinRowKind.None;`
- `SidebarPinStore` (`Sidebar.Host.cs`): `Pin` and `Insert` return false for `SidebarPinRules.IsFixedRoute(stored.Id)`;
  `LoadFrom` and `ApplyRemote` skip such ids.

`Sidebar.InstallActionSeams` (`Sidebar.UI.Menus.cs`): `s.IsPinned` unchanged; `s.SetPinned` returns early when
`AccountKey.Length == 0` (signed out: nowhere to keep a pin).

### P3.10 The one-time migration (new files `Shell/Sidebar.Store.V2.cs`, `Shell/Sidebar.Migration.cs`)

`Shell/Sidebar.Store.V2.cs` (header: "READ-ONLY: the v2 `sidebar-layout.json` shapes, read once by
`SidebarMigrationV2` and never written. Delete one release after the rework ships."): move from `Sidebar.Doc.cs`
verbatim the DTOs `SidebarLayoutDocDto`, `SidebarPinDto`, `SidebarV3Dto`, `SidebarCuratedDto`, `SidebarSectionDto`,
`SidebarExtensionDto`, `SidebarActionDto`, `SidebarDisplayDto`, `SidebarItemDto`, `SidebarQueryDto`, and
`SidebarLayoutJsonCtx` (copy `Sidebar.Doc.cs`'s using header verbatim: `System.Diagnostics.CodeAnalysis`,
`System.Globalization`, `System.Text`, `System.Text.Json`, `System.Text.Json.Serialization`; an unused using is not a
build error here), plus:

```csharp
/// <summary>The v2 file, read once. Null when it is absent or unreadable (the .bak is tried first) — a migration then
/// simply has nothing to carry.</summary>
public static class SidebarStoreV2
{
    public const string FileName = "sidebar-layout.json";

    public static SidebarLayoutDocDto? TryRead(string profileDir)
    {
        string path = Path.Combine(profileDir, FileName);
        return TryReadAt(path) ?? TryReadAt(path + ".bak");
    }

    static SidebarLayoutDocDto? TryReadAt(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllBytes(path), SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);
            return doc is { Version: >= 1 and <= 2 } ? doc : null;
        }
        catch (Exception ex) { Log.Warn("sidebar", "migration.v2_unreadable " + ex.GetType().Name); return null; }
    }

    /// <summary>The pre-unification pin kind table (Route=0, Playlist=1, Album=2, Artist=3, Show=4, Folder=5) — frozen.</summary>
    public static bool TryLegacyPinKind(int legacy, out SidebarEntryKind kind)
    {
        kind = legacy switch
        {
            1 => SidebarEntryKind.Playlist, 2 => SidebarEntryKind.Album, 3 => SidebarEntryKind.Artist,
            4 => SidebarEntryKind.Show, 5 => SidebarEntryKind.Folder, _ => SidebarEntryKind.AppRoute,
        };
        return (uint)legacy <= 5;
    }
}
```

`Shell/Sidebar.Migration.cs` — the pure half and the IO half:

```csharp
// ── Shell/Sidebar.Migration.cs ─────────────────────────────────────────────────────────────────────────────────────
// the ONE migration from the three designs + sidebar-layout.json v2 to the two layouts + sidebar.json v3 + the account
// files, run once before the pane first mounts, latched by sidebar.bootstrap.version = 2
//
// Role: CORE (SidebarMigrationV2) + SHELL (SidebarMigrationHost)
// Spec: sidebar-rework-implementation.md §P3.10 · design corner case "Migration from today's data", Q1a, Q15
// Delete one release after the rework ships, with Sidebar.Store.V2.cs.

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Everything the migration reads, gathered by the host so the rule is pure.</summary>
public readonly record struct SidebarMigrationInput(
    int Design,                       // sidebar.design: 0 Classic · 1 LibraryV3 · 2 Curated
    SidebarLayoutDocDto? V2,          // sidebar-layout.json (null: absent / unreadable)
    bool ClassicPinnedOpen, bool ClassicLibraryOpen, bool ClassicPlaylistsOpen,
    float DesignWidth, bool DesignCollapsed,
    int V3Filter, int V3Sort, bool V3Desc, int V3View,
    bool PinsMigrated,                // the old global sidebar.pins.migratedToServer
    string AccountKey);               // the stored credential's account at migration time ("" ⇒ pending)

public sealed record SidebarMigrationResult(
    SidebarLayoutId Layout,
    SidebarLayoutState State,
    SidebarDensity Density,
    SidebarLibraryFilter Filter,
    float Width,
    bool UserCollapsed,
    SidebarAccountData Account,
    bool ShowToast,
    IReadOnlyList<string> Dropped);   // human-readable names of what did not carry over (Q15)

public static class SidebarMigrationV2
{
    public static SidebarMigrationResult Migrate(in SidebarMigrationInput input)
    {
        var dropped = new List<string>();
        var curated = input.Design == 2 ? input.V2?.Curated?.Sections : null;

        // ── the layout: ALWAYS from sidebar.design (the key's "unwritten" state cannot be probed) ──
        var layout = input.Design switch
        {
            1 => SidebarLayoutId.Library,
            2 => CuratedLooksLikeLibrary(curated) ? SidebarLayoutId.Library : SidebarLayoutId.Classic,
            _ => SidebarLayoutId.Classic,
        };

        // ── Classic: today's three collapse flags; from Curated, its feeds and hidden built-ins ──
        var classic = new List<SectionState>(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic).Sections);
        Set(classic, "pinned", s => s with { Collapsed = !input.ClassicPinnedOpen });
        Set(classic, "collections", s => s with { Collapsed = !input.ClassicLibraryOpen });
        Set(classic, "playlists", s => s with { Collapsed = !input.ClassicPlaylistsOpen });
        if (curated is not null) ApplyCurated(curated, classic, dropped);

        // ── Library: the V3 sort / direction / view; density from the compact list ──
        var library = new List<SectionState>(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library).Sections);
        var sort = (uint)input.V3Sort <= 4 ? (SidebarLibrarySort)input.V3Sort : SidebarLibrarySort.Recents;
        var view = input.V3View >= 2 ? SidebarLibraryView.Grid : SidebarLibraryView.List;
        Set(library, "library", s => s with { Sort = sort, Descending = input.V3Desc, View = view });
        var density = input.Design == 1 && input.V3View == 0 ? SidebarDensity.Compact : SidebarDensity.Default;
        var filter = input.V3Filter switch
        {
            1 => SidebarLibraryFilter.Playlists, 2 => SidebarLibraryFilter.Podcasts,
            3 => SidebarLibraryFilter.Albums, 4 => SidebarLibraryFilter.Artists, _ => SidebarLibraryFilter.None,
        };

        var state = new SidebarLayoutState(
            SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Classic, classic.ToArray()), null),
            SidebarLayoutRules.MergeWithCatalogue(new LayoutOverlay(SidebarLayoutId.Library, library.ToArray()), null));

        // ── pins: the v2 list (fixed routes dropped, Q1a), then the TopBar shortcuts as local route pins, prepended ──
        var pins = new List<SidebarPin>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool topBarDiffered = false;
        if (input.V2?.TopBar is { } topBar)
        {
            topBarDiffered = !(topBar.Length == 1 && topBar[0] is { Target: "route", Key: "home" });
            var shortcuts = new List<SidebarPin>(topBar.Length);
            for (int i = 0; i < topBar.Length; i++)
            {
                var item = topBar[i];
                if (item is null || item.Hidden == true || string.IsNullOrEmpty(item.Key)) continue;
                string? id = item.Target switch
                {
                    "route" => SidebarPinId.FromRoute(item.Key),
                    "entity" => SidebarPinId.FromUri(item.Key),
                    _ => null,                                   // tracks and actions have no pin form
                };
                if (id is null || SidebarPinRules.IsFixedRoute(id) || !ids.Add(id)) continue;
                var kind = SidebarPinId.KindOf(id);
                string name = item.Label ?? item.FallbackTitle ?? "";
                shortcuts.Add(new SidebarPin(id, kind, SidebarPinId.UriOf(id), name, 0L));
            }
            pins.AddRange(shortcuts);
        }
        if (input.V2?.Pins is { } v2Pins)
            for (int i = 0; i < v2Pins.Length && pins.Count < SidebarAccountStore.MaxPins; i++)
            {
                var d = v2Pins[i];
                if (d?.Id is not { Length: > 0 } id || SidebarPinRules.IsFixedRoute(id) || !ids.Add(id)) continue;
                SidebarEntryKind kind;
                if (!string.IsNullOrEmpty(d.EntityKind)) { if (!SidebarPinWire.TryParseKind(d.EntityKind, out kind)) continue; }
                else if (!SidebarStoreV2.TryLegacyPinKind(d.Kind, out kind)) continue;
                pins.Add(new SidebarPin(id, kind, d.Uri ?? "", d.Name ?? "", d.AddedAtMs));
            }

        var account = new SidebarAccountData(
            input.AccountKey,
            input.PinsMigrated,
            pins,
            input.V2?.V3?.ExpandedFolders is { } folders ? Clean(folders) : [],
            0L,
            input.V2?.V3?.FirstSeen ?? []);

        return new SidebarMigrationResult(layout, state, density, filter, SidebarPaneBounds.Clamp(input.DesignWidth),
            input.DesignCollapsed, account, input.Design == 2 || topBarDiffered, dropped);
    }

    /// <summary>A Curated document reads as Library when it shows a library LIST and no playlist TREE.</summary>
    public static bool CuratedLooksLikeLibrary(SidebarSectionDto[]? sections)
    {
        if (sections is null) return false;
        bool list = false, tree = false;
        void Walk(SidebarSectionDto[] level)
        {
            for (int i = 0; i < level.Length; i++)
            {
                var s = level[i];
                if (s is null || s.Hidden == true) continue;
                if (s.Kind == "entityList") list = true;
                if (s.Kind == "playlistTree") tree = true;
                if (s.Children is { } kids) Walk(kids);
            }
        }
        Walk(sections);
        return list && !tree;
    }

    static void ApplyCurated(SidebarSectionDto[] sections, List<SectionState> classic, List<string> dropped)
    {
        void Walk(SidebarSectionDto[] level)
        {
            for (int i = 0; i < level.Length; i++)
            {
                var s = level[i];
                if (s is null) continue;
                bool visible = s.Hidden != true;
                switch (s.Kind)
                {
                    case "jumpBackIn" when visible:
                        Set(classic, "recent", x => x with { Hidden = false, Limit = NearestLimit(s.Display?.MaxItems, 5) });
                        break;
                    case "newReleases" when visible:
                        Set(classic, "newReleases", x => x with { Hidden = false, Limit = NearestLimit(s.Display?.MaxItems, 10) });
                        break;
                    case "pinned":
                        Set(classic, "pinned", x => x with { Hidden = !visible, Collapsed = s.Collapsed == true });
                        break;
                    case "collectionShortcuts":
                        var hidden = new List<string>();
                        if (s.Items is { } items)
                            for (int k = 0; k < items.Length; k++)
                                if (items[k] is { Hidden: true, Target: "route" } it && it.Key is { } key
                                    && Array.IndexOf(SidebarCatalogue.ItemsOf(SidebarLayoutId.Classic, SidebarSectionKind.Collections) as string[] ?? [], key) >= 0)
                                    hidden.Add(key);
                        Set(classic, "collections", x => x with
                        {
                            Hidden = !visible, Collapsed = s.Collapsed == true,
                            HiddenItems = hidden.Count > 0 ? hidden.ToArray() : null,
                        });
                        break;
                    case "playlistTree":
                        Set(classic, "playlists", x => x with { Collapsed = s.Collapsed == true });   // it cannot hide
                        break;
                    case "customGroup":
                        if (visible) Note(dropped, "sidebar.migration.kind.group");
                        if (s.Children is { } kids) Walk(kids);
                        break;
                    default:
                        if (visible && DroppedName(s) is { } name) Note(dropped, name);
                        break;
                }
            }
        }
        Walk(sections);
    }

    /// <summary>The loc key naming a section that does not carry over (Q15: human-readable, never a wire kind).</summary>
    static string? DroppedName(SidebarSectionDto s) => s.Kind switch
    {
        "entityEmbed" => "sidebar.migration.kind.spotlight",
        "concerts" => "sidebar.migration.kind.concerts",
        "staticLinks" => "sidebar.migration.kind.links",
        "header" => "sidebar.migration.kind.heading",
        "entityList" => "sidebar.migration.kind.libraryList",
        "extension" => s.Extension?.ContributionId switch
        {
            "queue" => "sidebar.migration.kind.queue",
            "nowPlaying" => "sidebar.migration.kind.nowPlaying",
            "artist.topTracks" => "sidebar.migration.kind.topTracks",
            "concerts" => "sidebar.migration.kind.concerts",
            _ => "sidebar.migration.kind.extension",
        },
        _ => null,   // divider: a separator is structural now, nothing to report
    };

    static void Note(List<string> dropped, string key) { if (!dropped.Contains(key)) dropped.Add(key); }

    static int NearestLimit(int? maxItems, int fallback)
    {
        if (maxItems is not int m || m <= 0) return fallback;
        return m <= 7 ? 5 : m <= 15 ? 10 : 20;
    }

    static void Set(List<SectionState> list, string id, Func<SectionState, SectionState> change)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) { list[i] = change(list[i]); return; }
    }

    static string[] Clean(string?[] ids)
    {
        var list = new List<string>(ids.Length);
        for (int i = 0; i < ids.Length; i++) if (!string.IsNullOrEmpty(ids[i]) && !list.Contains(ids[i]!)) list.Add(ids[i]!);
        return list.ToArray();
    }
}

/// <summary>The migration's IO half, run by <see cref="Sidebar.Boot"/> before anything else loads. The legacy keys are READ
/// here and nowhere else, and never written again; <c>sidebar-layout.json</c> is left on disk untouched.</summary>
internal static class SidebarMigrationHost
{
    // The legacy keys exist only here (Platform.Keys no longer declares them).
    static readonly SettingKey<int> Design = new("sidebar.design", 0);
    static readonly SettingKey<bool> ClassicPinned = new("sidebar.classic.section.pinned", true);
    static readonly SettingKey<bool> ClassicLibrary = new("sidebar.classic.section.library", true);
    static readonly SettingKey<bool> ClassicPlaylists = new("sidebar.classic.section.playlists", true);
    static readonly SettingKey<int> V3Filter = new("sidebar.v3.filter", 0);
    static readonly SettingKey<int> V3Sort = new("sidebar.v3.sort", 0);
    static readonly SettingKey<bool> V3Desc = new("sidebar.v3.desc", false);
    static readonly SettingKey<int> V3View = new("sidebar.v3.view", 1);

    public static void RunIfNeeded(IAppSettings settings, string profileDir, in CatalogScope scope)
    {
        if (settings.Get(Platform.Keys.SidebarBootstrapVersion) >= 2) return;
        int design = settings.Get(Design);
        string slug = design switch { 1 => "v3", 2 => "curated", _ => "classic" };
        float defaultWidth = design switch { 1 => 340f, 2 => 320f, _ => 280f };
        var input = new SidebarMigrationInput(
            design,
            SidebarStoreV2.TryRead(profileDir),
            settings.Get(ClassicPinned), settings.Get(ClassicLibrary), settings.Get(ClassicPlaylists),
            settings.Get(new SettingKey<float>("sidebar." + slug + ".width", defaultWidth)),
            settings.Get(new SettingKey<bool>("sidebar." + slug + ".collapsed", false)),
            settings.Get(V3Filter), settings.Get(V3Sort), settings.Get(V3Desc), settings.Get(V3View),
            settings.Get(Platform.Keys.PinsMigratedToServer),
            SidebarAccountKey.Of(scope));
        var r = SidebarMigrationV2.Migrate(in input);

        // Written NOW and waited for (bounded): the pane mounts right after Boot and must read what this produced.
        Directory.CreateDirectory(profileDir);
        var device = new SidebarFileStore(SidebarStoreV3.PathUnder(profileDir));
        device.Commit(SidebarStoreV3.Serialize(r.State));
        device.FlushNow();
        device.WaitForWrites(2000);
        if (r.Account.Pins.Count > 0 || r.Account.ExpandedFolders.Count > 0 || r.Account.FirstSeen.Count > 0)
        {
            var acct = new SidebarFileStore(Path.Combine(profileDir, SidebarAccountStore.FileNameOf(r.Account.Key)));
            acct.Commit(SidebarAccountStore.Serialize(r.Account));
            acct.FlushNow();
            acct.WaitForWrites(2000);
        }

        settings.Set(Platform.Keys.SidebarLayoutId, (int)r.Layout);
        settings.Set(Platform.Keys.SidebarPaneWidth, r.Width);
        settings.Set(Platform.Keys.SidebarPaneUserCollapsed, r.UserCollapsed);
        settings.Set(Platform.Keys.SidebarPaneDensity, (int)r.Density);
        settings.Set(Platform.Keys.SidebarLibraryFilter, (int)r.Filter);
        settings.Set(Platform.Keys.SidebarMigrationDropped, string.Join(",", r.Dropped));
        settings.Set(Platform.Keys.SidebarBootstrapVersion, 2);
        if (r.ShowToast) Sidebar.PendingMigrationToast = r;
        Log.Info("sidebar", "migration.v2 design=" + design.ToString(CultureInfo.InvariantCulture)
            + " layout=" + r.Layout + " pins=" + r.Account.Pins.Count.ToString(CultureInfo.InvariantCulture)
            + " account=" + (r.Account.Key.Length == 0 ? "pending" : SidebarAccountKey.Hash(r.Account.Key))
            + " dropped=" + r.Dropped.Count.ToString(CultureInfo.InvariantCulture));
    }
}
```

(The `Array.IndexOf(... as string[])` in `ApplyCurated` is clumsy: write a small `static bool IsCollectionItem(string key)`
that loops `SidebarCatalogue.ItemsOf(Classic, Collections)`.)

**The migration toast** (`Sidebar.UI.cs`, `PaneHost.Render`, a one-shot `UseEffect(…, DepKey.Empty)`):

```csharp
            UseEffect(static () => { ShowPendingMigrationToast(); return null; }, DepKey.Empty);
```

```csharp
    /// <summary>Set by the migration when a Curated user or a customized TopBar was carried over; shown once, after the
    /// first pane mounts.</summary>
    internal static SidebarMigrationResult? PendingMigrationToast;

    static void ShowPendingMigrationToast()
    {
        if (PendingMigrationToast is not { } r) return;
        PendingMigrationToast = null;
        string layout = Loc.Get(r.Layout == SidebarLayoutId.Library ? "sidebar.layoutName.library" : "sidebar.layoutName.classic");
        string text = r.Dropped.Count == 0
            ? Loc.Format("sidebar.migration.toast", ("layout", layout))
            : Loc.Format("sidebar.migration.toastDropped", ("layout", layout), ("names", DroppedNames(r.Dropped)));
        Toast.Show(text, new ToastOptions { Severity = InfoBarSeverity.Informational, DurationMs = 8000f });   // P4 adds the "Edit sidebar" action
    }

    internal static string DroppedNames(IReadOnlyList<string> keys)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = Loc.Get(keys[i]);
        return string.Join(", ", parts);
    }
```


### P3.11 The service (`Shell/Sidebar.Host.cs`, the `Sidebar` partial at the top of the file)

Replace everything from `// ── boot-time wiring` through the end of the `Sidebar` partial that ends before
`ISidebarEditHost` with the block below, keeping the UI-thread seam (`ToUi`), the pane-state block of §P2.2 (with the
keys of this phase), the folder-expansion block, the pins block and `Entries`/`Binder` unchanged except where noted.

```csharp
    // ── boot ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static string s_profileDir = "";
    static string? s_profileDirOverride;
    static SidebarFileStore? s_deviceFile;
    static readonly Signal<SidebarWriteResult> s_persistenceHealth = new(SidebarWriteResult.Healthy);

    /// <summary>Point the service at another profile folder — the <c>Platform.UseSettings</c> precedent, for a test (a temp
    /// folder) or a host with its own profile. Call it BEFORE <see cref="Boot"/>: the migration's v2 read, the device file
    /// and every account file then live under <paramref name="dir"/>, and the real
    /// <c>%LOCALAPPDATA%\Wavee\WaveeMusic</c> is never read or written. Lazy by design: resolving the default reads
    /// <c>Platform.LocalFolder</c>, whose getter creates the profile directory.</summary>
    public static void UseProfileDir(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        s_profileDirOverride = dir;
    }

    /// <summary>Boot (App.cs, after Entities.Boot): the one-time v2 migration, then the settings, the device file and the
    /// live account. Its partners are <see cref="Activate"/> and <see cref="Shutdown"/>. A second Boot (tests) reloads
    /// everything, the account included.</summary>
    public static void Boot()
    {
        s_profileDir = s_profileDirOverride ?? Path.Combine(Platform.LocalFolder, "WaveeMusic");
        SidebarMigrationHost.RunIfNeeded(Platform.Settings, s_profileDir, Entities.Current.Key);

        var s = Platform.Settings;
        Layout.Value = s.Get(Platform.Keys.SidebarLayoutId) == 1 ? SidebarLayoutId.Library : SidebarLayoutId.Classic;
        Density.Value = s.Get(Platform.Keys.SidebarPaneDensity) == 1 ? SidebarDensity.Compact : SidebarDensity.Default;
        Width.Value = SidebarPaneBounds.Clamp(s.Get(Platform.Keys.SidebarPaneWidth));
        UserCollapsed.Value = s.Get(Platform.Keys.SidebarPaneUserCollapsed);
        Seam.Value = UserCollapsed.Peek() ? SidebarRowGeometry.RailWidth : Width.Peek();
        PresentedWidth.Value = Seam.Peek();

        s_deviceFile = new SidebarFileStore(SidebarStoreV3.PathUnder(s_profileDir)) { WriteCompleted = OnWriteCompleted };
        LoadDeviceFile();
        LibraryFilter.Value = SidebarLibraryFilters.Effective(s.Get(Platform.Keys.SidebarLibraryFilter), Doc.Library.HiddenKinds);
        V3Qualifier.Value = s.Get(Platform.Keys.V3Qualifier);   // the P3-P4 V3 chrome bridge (§P3.12); deleted in P5
        SyncV3Mirrors();

        s_accountLoaded = false;                                          // a re-Boot reloads the account even for the same key
        s_accountFile = null;
        EnsureAccount(Entities.Current.Key);
        Pins.OnChanged = CommitAccount;                                   // every pin mutation is a commit point
        Log.Info("sidebar", "boot layout=" + Layout.Peek() + " fault=" + LayoutFileFault);
    }

    static void LoadDeviceFile()
    {
        var file = s_deviceFile!;
        var dropped = new List<string>();
        var read = file.Read();
        if (read.Outcome == SidebarFileReadOutcome.Ok && SidebarStoreV3.TryParse(read.Bytes!, out var state, dropped)) s_state = state;
        else if (read.Outcome == SidebarFileReadOutcome.Missing) s_state = SidebarLayoutState.Default;
        else
        {
            var bak = file.ReadBak();
            if (bak.Outcome == SidebarFileReadOutcome.Ok && SidebarStoreV3.TryParse(bak.Bytes!, out var recovered, dropped))
                s_state = recovered;
            else
            {
                // Corrupt (design corner case): catalogue defaults, the bytes set aside, one InfoBar in Settings › Sidebar.
                // Pins are unaffected — they live in the account file.
                file.MarkCorrupt();
                s_state = SidebarLayoutState.Default;
                LayoutFileFault = true;
            }
        }
        for (int i = 0; i < dropped.Count; i++) Log.Info("sidebar", "layout.unknown_section id=" + dropped[i]);
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
    }

    /// <summary>The device file could not be read at boot (Settings › Sidebar shows "Your sidebar layout couldn't be read · Reset").</summary>
    public static bool LayoutFileFault { get; private set; }

    public static IReadSignal<SidebarWriteResult> PersistenceHealth => s_persistenceHealth;

    // ── the layout ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active layout (<c>sidebar.layout.id</c>). A switch remounts the mode (fresh hooks, fresh scroll).</summary>
    public static readonly Signal<SidebarLayoutId> Layout = new(SidebarLayoutId.Classic);

    /// <summary>Entity-row density (<c>sidebar.pane.density</c>), global.</summary>
    public static readonly Signal<SidebarDensity> Density = new(SidebarDensity.Default);

    /// <summary>Your Library's active chip (<c>sidebar.library.filter</c>).</summary>
    public static readonly Signal<SidebarLibraryFilter> LibraryFilter = new(SidebarLibraryFilter.None);

    /// <summary>Your Library's search text. SESSION-ONLY: never persisted; cleared on a layout or account switch.</summary>
    public static readonly Signal<string> LibrarySearch = new("");

    static SidebarLayoutState s_state = SidebarLayoutState.Default;
    static readonly Signal<int> s_layoutVersion = new(0);
    static SidebarLayoutDoc? s_doc;
    static int s_docVersion = -1;
    static SidebarLayoutId s_docLayout;
    static SidebarDensity s_docDensity;

    /// <summary>Both overlays (the undo ring and the Outline read them).</summary>
    public static SidebarLayoutState State => s_state;

    /// <summary>Bumped on every overlay, density or layout change — the plan's and the binder's edge.</summary>
    public static IReadSignal<int> LayoutVersion => s_layoutVersion;

    /// <summary>The active layout's resolved document. PEEKS: a render that must re-plan reads <see cref="LayoutVersion"/>.
    /// Cached on (version, layout, density), so the same instance comes back until something changed — the pane's
    /// publish reference test depends on it.</summary>
    public static SidebarLayoutDoc Doc
    {
        get
        {
            int v = s_layoutVersion.Peek();
            var layout = Layout.Peek();
            var density = Density.Peek();
            if (s_doc is null || v != s_docVersion || layout != s_docLayout || density != s_docDensity)
            {
                s_doc = SidebarLayoutRules.Resolve(s_state, layout, density);
                s_docVersion = v;
                s_docLayout = layout;
                s_docDensity = density;
            }
            return s_doc;
        }
    }

    /// <summary>THE ONE MUTATION PATH (design A.3): rules → signal → coalesced write. A refused op changes nothing and
    /// returns why. (P4 adds the undo push.)</summary>
    public static SidebarOpReject Dispatch(SidebarOp op)
    {
        var r = SidebarLayoutRules.Apply(s_state, op, PinnedLocked());
        if (!r.Changed)
        {
            if (r.Reject is not (SidebarOpReject.None or SidebarOpReject.NoChange))
                Log.Info("sidebar", "op.refused op=" + op.GetType().Name + " reason=" + r.Reject);
            return r.Reject;
        }
        s_state = r.State;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        if (op is SetLibrarySort or SetLibraryView) SyncV3Mirrors();
        CommitLayout();
        return SidebarOpReject.None;
    }

    /// <summary>Pinned holds a route or module pin and may not be hidden.</summary>
    public static bool PinnedLocked()
    {
        for (int i = 0; i < Pins.Count; i++) if (Pins[i].Kind == SidebarEntryKind.AppRoute) return true;
        return false;
    }

    /// <summary>Switch the layout (Settings, the pane menu, the palette). Each layout keeps its own overlay.</summary>
    public static void SwitchLayout(SidebarLayoutId next)
    {
        if (Layout.Peek() == next || Editing.Peek()) return;
        LibrarySearch.SetIfChanged("");
        LibrarySearchOpen.SetIfChanged(false);
        Layout.Value = next;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Platform.Settings.Set(Platform.Keys.SidebarLayoutId, (int)next);
        Log.Info("sidebar", "layout.changed to=" + next);
    }

    public static void SetDensity(SidebarDensity density)
    {
        if (Density.Peek() == density) return;
        Density.Value = density;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Platform.Settings.Set(Platform.Keys.SidebarPaneDensity, (int)density);
        SyncV3Mirrors();
    }

    public static void SetLibraryFilter(SidebarLibraryFilter filter)
    {
        LibraryFilter.SetIfChanged(filter);
        Platform.Settings.Set(Platform.Keys.SidebarLibraryFilter, (int)filter);
        SyncV3Mirrors();
    }

    /// <summary>"Reset everything" (design C.3): both layouts, density and width back to their defaults; pins untouched.</summary>
    public static void ResetEverything()
    {
        Dispatch(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic)));
        Dispatch(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library)));
        SetDensity(SidebarDensity.Default);
        SetExpandedWidth(SidebarPaneBounds.DefaultWidth);
    }

    static void CommitLayout() => s_deviceFile?.Commit(SidebarStoreV3.Serialize(s_state));
```

`SidebarPaneBounds` gains `public const float DefaultWidth = 320f;`. `ResetWidth()` → `SetExpandedWidth(SidebarPaneBounds.DefaultWidth)`.
The pane-state `Apply` of §P2.2 persists `Platform.Keys.SidebarPaneWidth` and `SidebarPaneUserCollapsed` instead of the
per-design snapshot (`SidebarPaneState` is deleted with `Sidebar.Modes.cs`'s design block).

The folder-expansion block keeps `IsFolderExpanded`, `ExpandedFolders`, `FolderVersion`, `SetFolderExpanded`,
`ToggleFolder`, `FlushPendingCommit` — `FlushPendingCommit` and the coalesced arm call `CommitAccount()` (folders live in
the account file now). The pins block keeps `Pins`, `PinsVersion`, `IsPinned`, `Pin`, `Unpin`, `InsertPin`, `MovePin`,
`TouchPin` (its dirty flag rides `CommitAccount`). `PublishFirstSeen(stamps)` sets `s_firstSeen` and calls
`CommitAccount()`; `FirstSeen` returns `s_firstSeen`.

`Flush()` → `if (s_commitPending || s_pinNamesDirty) CommitAccount();`. `Shutdown(timeout)`: detach `WriteCompleted`
on both stores, `Flush()`, `CommitLayout()`, `FlushNow()` and `WaitForWrites(timeout)` on both; returns both landed.

Deleted from the partial: `Store`, `UseStore`, `s_undo`, `s_layout`, `s_carry`, `s_loaded`, `Design`, `SwitchDesign`,
`FlushBagOf`, `SeedBagOf`, the Classic bag (`ClassicPinnedOpen/LibraryOpen/PlaylistsOpen`, `SetClassicSection`), the V3
custom order (`s_v3CustomOrder`, `V3CustomOrder`, `V3OrderVersion`, `CanReorderV3`, `V3RankOf`, `SetV3CustomOrder`),
`Edit`, `Layout` (the Curated document), `Dispatch(SidebarCommand)`, `ApplyCurated`, the TopBar API (`TopBar`, `TopBarFull`,
`AddTopBarShortcut`, `MoveTopBarShortcut`, `RemoveTopBarShortcut`, `TopBarIndexOf`), `PinKeySet`, `ApplyTemplateId`,
`CanUndo`/`CanRedo`/`UndoLabel`/`RedoLabel`/`Undo`/`Redo`, `Fault`/`FaultDetail`/`SaveFault`/`SaveFaultDetail`,
`DiscardCorruptDocument`, `LoadDocument`, `PinsFromDto`, `PinsToDto`, `SetV3StateFromDto`, `Commit` (→ `CommitAccount`/
`CommitLayout`), `BuildSnapshot`, `ExpandedFolderArray`.

**The P3-P4 V3 bridge** (kept in `Sidebar.Host.cs`, deleted in P5): the chrome of `Sidebar.UI.LibraryV3.cs` still reads
`V3Filter`, `V3Qualifier`, `V3Sort`, `V3Desc`, `V3View`, `V3GridSize`, `V3Search` and calls `SetV3Filter`, `SetV3Qualifier`,
`SetV3Sort`, `SetV3View`, `SetV3GridSize`. They stay as session signals MIRRORED from the new state (never persisted):

```csharp
    // ── P3-P4 BRIDGE: Library V3's chrome speaks its old int codes until P5 replaces it. The model is the truth; these
    //    mirror it (SyncV3Mirrors) and their setters write the model. Never persisted. Delete in P5. ──
    public static readonly Signal<int> V3Filter = new(0);
    public static readonly Signal<int> V3Qualifier = new(0);
    public static readonly Signal<int> V3Sort = new(0);
    public static readonly Signal<bool> V3Desc = new(false);
    public static readonly Signal<int> V3View = new(1);
    public static readonly Signal<int> V3GridSize = new(1);
    public static Signal<string> V3Search => LibrarySearch;

    static void SyncV3Mirrors()
    {
        var lib = Doc.Library;
        V3Filter.SetIfChanged(LibraryFilter.Peek() switch
        {
            SidebarLibraryFilter.Playlists => (int)SidebarV3Filter.Playlists,
            SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks => (int)SidebarV3Filter.Podcasts,
            SidebarLibraryFilter.Albums => (int)SidebarV3Filter.Albums,
            SidebarLibraryFilter.Artists => (int)SidebarV3Filter.Artists,
            _ => (int)SidebarV3Filter.All,
        });
        V3Sort.SetIfChanged((int)lib.Sort);
        V3Desc.SetIfChanged(lib.Descending);
        bool compact = Density.Peek() == SidebarDensity.Compact;
        V3View.SetIfChanged(lib.View == SidebarLibraryView.Grid
            ? (compact ? (int)SidebarV3View.CompactGrid : (int)SidebarV3View.Grid)
            : (compact ? (int)SidebarV3View.CompactList : (int)SidebarV3View.List));
    }

    public static void SetV3Filter(int v) => SetLibraryFilter(v switch
    {
        (int)SidebarV3Filter.Playlists => SidebarLibraryFilter.Playlists,
        (int)SidebarV3Filter.Podcasts => SidebarLibraryFilter.Podcasts,
        (int)SidebarV3Filter.Albums => SidebarLibraryFilter.Albums,
        (int)SidebarV3Filter.Artists => SidebarLibraryFilter.Artists,
        _ => SidebarLibraryFilter.None,
    });
    public static void SetV3Qualifier(int v) { V3Qualifier.SetIfChanged(v); Platform.Settings.Set(Platform.Keys.V3Qualifier, v); }
    public static void SetV3Sort(int sort, bool desc)
        => Dispatch(new SetLibrarySort((uint)sort <= 4 ? (SidebarLibrarySort)sort : SidebarLibrarySort.Recents, desc));
    public static void SetV3View(int view)
    {
        Dispatch(new SetLibraryView(view >= (int)SidebarV3View.CompactGrid ? SidebarLibraryView.Grid : SidebarLibraryView.List));
        SetDensity(view is (int)SidebarV3View.CompactList or (int)SidebarV3View.CompactGrid ? SidebarDensity.Compact : SidebarDensity.Default);
    }
    public static void SetV3GridSize(int size) => V3GridSize.SetIfChanged(size);
```

`Platform.Keys.V3Qualifier` stays declared until P5. `SidebarV3Filter`, `SidebarV3Qualifier`, `SidebarV3Sort`,
`SidebarV3View` stay in `Sidebar.Modes.cs` until P5. `SidebarSort.Effective` keeps a `SidebarV3Filter` overload only if a
chrome call site needs it (prefer converting at the call site).

### P3.12 The UI on the new model

**`PaneConfig`** (`Sidebar.UI.cs`), trimmed:

```csharp
    internal sealed record PaneConfig
    {
        public required SidebarLayoutId Layout { get; init; }
        public required string ScrollKeyPrefix { get; init; }
        /// <summary>The live document — invoked in the pane's render (its read of LayoutVersion subscribes the pane).</summary>
        public required Func<SidebarLayoutDoc> Document { get; init; }
        /// <summary>The mode's transform of the binder's planner input (Library: the shaped list + the tree regroup).</summary>
        public Func<SidebarProjectionInput, SidebarProjectionInput>? Input { get; init; }
        /// <summary>Mode-owned state folded into the plan key (Library: filter, columns, drill).</summary>
        public Func<int>? ModeEpoch { get; init; }
        /// <summary>The planner options the mode decides (Library: the chip and the grid columns).</summary>
        public Func<SidebarPlanOptions, SidebarPlanOptions>? Options { get; init; }
        public Func<Element?>? Head { get; init; }
        public Action<string, string>? ActivateFolder { get; init; }
        public Func<bool>? DisclosesFoldersInline { get; init; }
        public Func<bool>? TreeSortedNonCustom { get; init; }
        public Action? SortedListRefusalAction { get; init; }
        public Func<SidebarSectionKind, int, int, int>? ClampReorderSlot { get; init; }
        public Action<PaneReorder>? CommitReorder { get; init; }
        public Action? OnCreatePlaylist { get; init; }
        public Func<bool>? ShowsSettings { get; init; }
    }

    internal readonly record struct PaneReorder(SidebarSection Section, int FromSlot, int ToSlot, int SlotCount, Func<int, string> KeyAt);

    /// <summary>The shared commit: Pinned through the pin store (mapped by pin id — a band position can drift from it).</summary>
    internal static void DefaultReorderCommit(in PaneReorder r)
    {
        if (r.FromSlot == r.ToSlot || r.Section.Kind != SidebarSectionKind.Pinned) return;
        int pf = Pins.IndexOf(r.KeyAt(r.FromSlot)), pt = Pins.IndexOf(r.KeyAt(r.ToSlot));
        if (pf < 0 || pt < 0) MovePin(r.FromSlot, r.ToSlot);
        else MovePin(pf, pt);
    }
```

(`Design`, `SetSectionCollapsed`, `ReadOnly`, `HeaderCreate`, `SearchHead`, `Edit`, `ShowLayoutMenu`,
`IsReorderableSection`, `OnCustomize` are gone. `HeaderCreate` is implied: the Playlists header always carries its
"+". Collapse always dispatches `SetSectionCollapsed`. `ItemIndexAt` is deleted.)

**The host and the two modes** (`Sidebar.UI.cs` §2-3):

```csharp
    internal sealed class PaneHost : Component
    {
        readonly bool _inDrawer;
        public PaneHost(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            EnsureBinder();
            UseSignalEffect(PumpBinder);
            // (the ScopeEpoch → EnsureAccount effect of §P3.8 and the one-shot migration toast of §P3.10 go here)
            var layout = Layout.Value;   // the ONLY signal this body reads: a switch remounts the mode
            Element mode = layout == SidebarLayoutId.Library
                ? Embed.Comp(() => new LibraryV3Mode(_inDrawer))
                : Embed.Comp(() => new ClassicMode(_inDrawer));
            return new BoxEl
            {
                Grow = 1f, Direction = 1,
                Children = [mode with
                {
                    Key = layout == SidebarLayoutId.Library ? "sidebar.library" : "sidebar.classic",
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Transition = MotionTok.ControlFast,
                }],
            };
        }
    }

    /// <summary>Classic: the catalogue's sectioned list, one scroller (design P.1).</summary>
    internal sealed class ClassicMode : Component
    {
        readonly bool _inDrawer;
        public ClassicMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Classic,
                ScrollKeyPrefix = "sidebar.classic",
                Document = static () => { _ = LayoutVersion.Value; return Doc; },
                OnCreatePlaylist = PaneView.CreatePlaylistFlow,
                ShowsSettings = static () => Doc.ShowsSettings,
            }, DepKey.Empty);
            return Embed.Comp(() => new PaneView(config, _inDrawer));
        }
    }
```

`CuratedMode`, `OpenCustomizerRoute` and `s_binderEpoch`'s Curated comment are deleted.

**`PaneView`** (the edits; the disclosure block is untouched):

- `Doc` is a `SidebarLayoutDoc` (seed `SidebarLayoutDoc.Empty`); `_sections` is `Dictionary<string, SidebarSection>`;
  `SectionOf(id)` returns `SidebarSection?`; `RebuildIndex` fills it from `Doc.Sections`, drops the
  `MenuHostSectionId`/`RebuildSectionBand`/edit-card logic, and arms reorder bands only for `SidebarSectionKind.Pinned`
  (band extent `SidebarRowGeometry.PitchOf(section.Shape)`), skipping a Pinned band that shows folder descendants
  (unchanged `_pinnedSubtrees` rule).
- `Render`: `var sourceDoc = Config.Document();` (a `SidebarLayoutDoc`); the edit-session read is deleted; the plan
  options are built here:

```csharp
            var options = new SidebarPlanOptions(
                Mode: compact ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded,
                PinDropArmed: _pinDropArmed.Value);
            if (Config.Options is { } shape) options = shape(options);
```

  `Drag.LivePinnable()` is new in `Platform/Drag.cs`, beside `LiveRootlistDrag`:

```csharp
    /// <summary>Is a drag that could be PINNED live right now (an entity, a folder, a route — never a track)? An empty
    /// Pinned section plans its drop band only while this holds.</summary>
    public static bool LivePinnable()
    {
        var state = FluentGpu.Hooks.InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        return state.Active && Unwrap(state.Payload) is { CanPin: true };
    }
```

  The pane owns `readonly Signal<bool> _pinDropArmed = new(false);`; `DragPeekWatcher` (its `UseDragState` already
  re-renders on a drag edge) writes it from a layout effect: `owner._pinDropArmed.SetIfChanged(Drag.LivePinnable());`.
  `Render` builds the options with `PinDropArmed: _pinDropArmed.Value` (that read is the subscription) and `PlanDep`
  folds it, so the band appears and leaves with the drag.
- `BuildStage(SidebarLayoutDoc document, string search, in SidebarPlanOptions options)` →
  `Sidebar.Plan(document, in input, in options, buffers)`. `PlanStage` = `(SidebarLayoutDoc Document, SidebarRowPlan Pane,
  string EffectiveSearch, bool UsesA, int Epoch, bool Compact)`. `PlanDep(search, compact, options)` folds
  `LayoutVersion`, entries, input, pins, folders, binder epoch, revision, mode epoch, search, compact, `PinDropArmed`,
  `Filter`, `GridColumns`, `FoldersInline`, `Drilled` (drop `editFold` and its census cause).
- `Input(search)`: `NewReleasesState`/`RecentsState`/`ConcertsState` lines go (only `LibraryState`/`TreeState` are forced
  Pending before the first projection); `LikedTitle = Loc.Get("nav.likedSongs")` is set here.
- `ToggleSection(id, collapsed)` keeps its body (`StartDisclosure(...)` with the same arguments) and its commit becomes
  `() => Sidebar.Dispatch(new SetSectionCollapsed(Config.Layout, id, collapsed))`; a New releases section expanding calls
  `Sidebar.MarkNewReleasesSeen()`.
- `RowRouteOf(i)` = `SidebarRowResolve.RouteOf(rows[i], Plan.Entries)`; `RowSelectsRoute` and `RefreshSelection` use the
  section-free `SidebarRowResolve` (§P3.5). `OwningSection(route)` (P1) becomes: Pinned when `IsPinned(SidebarPinId.FromRoute(route))`
  and Pinned is visible; Collections when the route is one of its items; Playlists when the route is a playlist/folder in
  the tree; Your Library (its id) in the Library layout for any entity route; else null.
- `PlanSectionExpanded(sectionId, buffers)` → `SidebarRowPlanner.BuildSection(Doc, sectionId, in input, buffers)`.
- Deleted: `Dispatch(SidebarCommand)`, `ToggleEditExpanded`, `EditShowsBody`, `SetSectionHidden`, `MoveSectionBy`,
  `RemoveEditSection`, `CanAcceptPaletteDropBefore`, `PaletteRefusalKey`, `AddSectionFromPalette`, `DuplicateEditSection`,
  `DispatchCanvas`, `SayCanvasRejection`, `OpenCustomizer`, `SectionReorder`, `TryEditSectionBand`, `ConfigureSectionReorder`,
  `SectionAnnounce`, `SectionTitleAtCardSlot`, `CommitSectionMove`, `_sectionBand`, `_sectionReorder`, `PublishedEdit`,
  `HasEntityList`, the `SearchHead` mount and `EmptyPane()`'s customize CTA (the empty pane names the state only).
- `Registry`/`Acts` stay (row menus still resolve entity verbs through the action registry).

**`Sidebar.UI.LibraryV3.cs`** (P3-WP14). The V3 chrome keeps rendering through P4 on the bridge signals of §P3.11; every
member below that is built on a type P3 deletes (`SidebarItemSpec`, `SidebarItemTarget`, `SidebarEntityKind`,
`SidebarNavBandModel`, `SidebarShortcutsSection`, `LibraryV3Document`, `SidebarCustomLayout`, `SidebarSectionKind.PlaylistTree`,
`TopBar`, `V3OrderVersion`, `CanReorderV3`, `SetV3CustomOrder`, `ResolveActionRow`, `PaneIcon`, `RowGlyphs`) is replaced
by the code here. Nothing else in the file changes in P3.

`LibraryV3Mode.Render`: in the debounce effect delete `_ = V3OrderVersion.Value;` (the local custom order is gone); the
config becomes

```csharp
            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Library,
                ScrollKeyPrefix = "sidebar.library",
                Document = static () => { _ = LayoutVersion.Value; return Doc; },
                Input = s.ShapeInput,
                ModeEpoch = s.ReadModeEpoch,
                Options = s.ShapeOptions,
                Head = s.ChromeHead,
                TreeSortedNonCustom = s.TreeSortedNonCustom,
                SortedListRefusalAction = s.SwitchToCustomSortForReorder,
                ClampReorderSlot = s.ClampReorderSlot,
                CommitReorder = s.CommitReorder,
                ActivateFolder = s.ActivateFolder,
                DisclosesFoldersInline = s.DisclosesFoldersInline,
                ShowsSettings = static () => Doc.ShowsSettings,
                // Null: V3's "+" calls PaneView.CreatePlaylist() — the pane's ONE create flow (no second one here).
                OnCreatePlaylist = null,
            }, DepKey.Empty);
```

(the mount factory that sets `s.PaneRef` is unchanged).

`V3Session` — fields: delete `_pins`, `_docState`, `_docTopBar`, `_docLayout`. Members:

```csharp
        public LibraryV3DocState ReadState()
        {
            int columns = Columns is { } c ? c.Value : 2;
            int filter = LibraryV3Metrics.NormalizeFilter(V3Filter.Value);
            int qualifier = LibraryV3Metrics.NormalizeQualifier(V3Qualifier.Value);
            int sort = LibraryV3Metrics.NormalizeSort(V3Sort.Value);
            bool desc = V3Desc.Value;
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            bool searching = LibraryV3Metrics.HasQuery(V3Search.Value);
            var cell = Cell;
            _ = cell.Version.Value;          // PinCount + QualifiersAvailable move with the projection
            _ = DrillVersion.Value;          // a push/pop re-slices the view
            return new LibraryV3DocState(filter, qualifier, sort, desc, view, columns, searching,
                DrillActive ? CurrentFolderId : null,
                cell.PinCount > 0,
                LikedPinned: false,           // Liked is never a pin (Q1a)
                cell.QualifiersAvailable,
                DragInFlight.Value);
        }

        public int ComputeColumns()
        {
            int view = LibraryV3Metrics.NormalizeView(V3View.Value);
            if (!LibraryV3Metrics.IsGrid(view)) return LibraryV3Metrics.ClampColumns(0);
            // Against the pane's ONE inset — the grid strip derives its cell edge from exactly that width.
            float cross = Sidebar.Width.Value - PaneMetrics.PaneInsetH;
            return LibraryV3Metrics.ClampColumns(LibraryV3Metrics.Columns(view, cross));
        }
```

(`LikedPinned:` is a named argument in a positional list: C# allows it because it is in its own position.)
`BuildDocument` is deleted. `ShapeInput` and the new `ShapeOptions`:

```csharp
        /// <summary>The published projection (filtered, searched, sorted, matching pins FIRST) re-grouped into tree order or
        /// sliced to one folder level. While Pinned is shown the planner draws the pins from <c>input.Pins</c> (§P3.5), so the
        /// list skips the published pin head; with Pinned hidden the pins stay in their natural place.</summary>
        public SidebarProjectionInput ShapeInput(SidebarProjectionInput input)
        {
            var state = ReadState();
            var cell = Cell;
            var published = cell.Current;
            int pinCount = Math.Clamp(cell.PinCount, 0, published.Count);
            int skip = Doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } && !state.Drilled ? pinCount : 0;
            bool group = LibraryV3Metrics.FoldersApply(in state);
            int revision = Binder?.Revision ?? 0;
            long epoch = ViewEpoch(cell.Version.Peek(), skip, group, state.DrillFolderId);
            if (epoch != _viewEpoch)
            {
                _viewEpoch = epoch;
                View.Build(published, skip, input.PlaylistTree, revision, state.DrillFolderId, group);
            }
            // ExpandedFolders is KEPT: the shaped list is already depth-stamped (the planner never re-filters it), and
            // PlanLibrary reads the set for one thing only — an expanded PINNED folder's children (§P3.5).
            return input with { Library = View.Rows, LibraryIsTree = group };
        }

        public SidebarPlanOptions ShapeOptions(SidebarPlanOptions o)
            => o with
            {
                Filter = LibraryFilter.Value, GridColumns = Columns?.Value ?? 2,
                // A pinned folder opens in place only where the pane discloses folders inline (wide list, not drilled);
                // the narrow/drawer pane and the grid drill instead (ActivateFolder).
                FoldersInline = DisclosesFoldersInline() && !DrillActive,
                // A drilled level plans the folder's children only: no pins, no drop band, no Liked row (§P3.5).
                Drilled = DrillActive,
            };
```

(`PaneView.PlanDep` folds `options.FoldersInline` and `options.Drilled` — P3-WP11's bullet below.)

(Keep the existing `BUG E, step 3` comment above the `epoch` line.) The reorder block (from `IsSectionReorderable` through
`SwitchToCustomSortForReorder`) is replaced whole by:

```csharp
        // ── PaneConfig: reorder. Pins reorder through the pin store; the library reorders only as a ROOTLIST move under
        //    Playlists · Custom order (the pane's resource drop → WaveeResourceDrop.MoveRootlist), never a local overlay. ──

        bool CanReorderCustom()
            => LibraryFilter.Peek() == SidebarLibraryFilter.Playlists && Doc.Library.Sort == SidebarLibrarySort.CustomOrder
               && LibrarySearch.Peek().Length == 0 && Doc.Library.View == SidebarLibraryView.List && !DrillActive;

        /// <summary>D10 — the tree shows a SORTED view, so positional drops refuse with "clear sorting to reorder" while Into
        /// stays legal.</summary>
        public bool TreeSortedNonCustom() => !CanReorderCustom();

        /// <summary>No local overlay to clamp: a rootlist move is the server's.</summary>
        public int ClampReorderSlot(SidebarSectionKind kind, int from, int to) => to;

        public void CommitReorder(PaneReorder r)
        {
            DefaultReorderCommit(in r);
            Resync();
        }

        /// <summary>H3 (#85) — the refusal toast's action: Playlists + Custom order, the one state a positional reorder is legal.</summary>
        public void SwitchToCustomSortForReorder()
        {
            SetLibraryFilter(SidebarLibraryFilter.Playlists);
            Dispatch(new SetLibrarySort(SidebarLibrarySort.CustomOrder, false));
            Resync();
        }
```

`V3Session.LabelOf(SidebarItemSpec)` is deleted (its callers were the tiles below).

`V3NavBand` — the TopBar is gone (its shortcuts are pins now), so the band is the destination word rail and its rule. The
five destinations are a local array (`SidebarShortcutsSection.LibraryDestinations` is deleted with `Sidebar.Doc.cs`):

```csharp
        /// <summary>The library destinations the word rail names (P3-P4; P5 replaces this band with the page dropdown).</summary>
        static readonly string[] s_destinations = ["liked", "albums", "artists", "podcasts", "audiobooks"];

        public override Element Render()
        {
            string route = Shell.NameOf(Shell.Current.Value);
            return new BoxEl
            {
                Key = "v3-nav-band",
                Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 0f),
                Children =
                [
                    DestinationRail(route),
                    Divider() with
                    {
                        Key = "v3-nav-rule",
                        Margin = new Edges4(SidebarRowGeometry.HeaderTextX, 4f, SidebarRowGeometry.TrailingPad, 4f),
                    },
                ],
            };
        }
```

In `DestinationRail`, `var keys = SidebarShortcutsSection.LibraryDestinations;` → `var keys = s_destinations;`. The four
tile builders under `// ── the four tile shapes` (`RouteTile`, `ActionTile`, `TrackTile`, `EntityTile`) and that banner
are deleted: their only caller was the TopBar loop (the destinations are words, not tiles, so no `RouteTile` survives;
the §P1.6 `Shape =` fixes to those four methods are superseded here).

`Sidebar.Modes.cs` (P3-WP10) gains, on `LibraryV3Metrics`, the two helpers `LibraryV3Document` held:

```csharp
    /// <summary>Folders group (inline disclosure / a drill) only in an unsearched, undrilled list under All or Playlists.</summary>
    public static bool FoldersApply(in LibraryV3DocState state)
        => IsList(state.View) && !state.Searching && !state.Drilled
           && (state.Filter == (int)SidebarV3Filter.All || state.Filter == (int)SidebarV3Filter.Playlists);

    /// <summary>The grid's column count, 2-4.</summary>
    public static int ClampColumns(int columns) => columns < 2 ? 2 : columns > 4 ? 4 : columns;
```

(`IsList` is `LibraryV3Metrics.IsList`, already on the class.) `LibraryV3Document.LikedRouteKey` has no replacement: its one
reader (`ReadState`) passes `LikedPinned: false`.

**The slot** (`Sidebar.UI.Slot.cs`): every `SidebarSectionSpec` parameter becomes `SidebarSection`;
`PaneMetrics.ShapeOf(section)` is `section.Shape`; `PaneText.TitleOf(section)` is
`SidebarCatalogue.TitleKeyOf(section.Kind) is { } key ? Loc.Get(key) : ""`. The dispatch:

```csharp
            Element content = row.Kind switch
            {
                SidebarRowKind.SectionHeader => HeaderRow(section, in row, index, sel),
                SidebarRowKind.Divider => SectionHeader.Separator(),
                SidebarRowKind.IconRow => RouteRow(section, row.Key, sel, index),
                SidebarRowKind.EntityRow => (uint)row.EntryIndex < (uint)_o.Plan.Entries.Count
                    ? EntryRow(section, _o.Plan.Entries[row.EntryIndex], in row, sel, index) : Nothing,
                SidebarRowKind.FolderHeader => FolderRow(section, in row, index),
                SidebarRowKind.GridStrip => GridStripRow(section, in row, sel),
                SidebarRowKind.Empty => EmptyRow(section),
                SidebarRowKind.Skeleton => Skeletons.Row(index, section.Shape),
                SidebarRowKind.TreeEnd => TreeEndRow(in row, index),
                SidebarRowKind.SectionTile => SectionTileRow(section, in row, index, sel),
                SidebarRowKind.DropBand => DropBandRow(section, index),
                _ => Nothing,
            };
```

`ItemOrEntity`, `ActionRow`, `TrackItemRow`, `MissingRow`, `HeroCard`, `PromptCard`, `ExtensionReason`, `EditCard`
calls, `CardOpenLive` and every `SidebarItemSpec` use are deleted. `RouteRow(section, key, sel, index)` takes the route
key (label/glyph from `Shell.Dest(Shell.Parse(key))`, count `Counts.Live` for `ShortcutCount.KindOf(key)` when the section
is Collections). `EntryRow`: an `AppRoute` entry (a route pin, the Library's Liked row) uses the route's glyph — except
`liked`, which uses `Cover.ForEntry` (the heart tile) — and the section's shape; its label falls back to
`Shell.Dest(Shell.Parse(route)).Title` when `Name` is empty. A pin row's state (`SidebarPinStateRules.Of(entry.IdentityKnown
|| entry.Name.Length > 0, entry.Missing, Spotify.Current.IsOnline)`): Unavailable → title `TextTertiary`, subtitle
`Icons.Warning` 12 + `Loc.Get("sidebar.pin.unavailable")`, menu = Unpin only; Pending/Offline → the last-known name
(the pin's cached `Name`) and the kind glyph, no warning. A pin with NO name at all (synced from another device through
ylpin before the projection hydrates: no entry, no cached `Name`) is never a blank row (D9): its title is the kind's noun
in `TextTertiary`, until the entity names it:

```csharp
            // In EntryRow, where the label is chosen (pin rows only; a non-pin entity row keeps P1's identity gate):
            string label = entry.Name.Length > 0 ? entry.Name
                : entry.Kind == SidebarEntryKind.AppRoute ? Shell.Dest(Shell.Parse(entry.RouteKey ?? "")).Title
                : SidebarPinStateRules.FallbackTitleKey(entry.Kind) is { } noun ? Loc.Get(noun) : PaneText.ShortUri(entry.Uri);
            bool placeholderTitle = entry.Name.Length == 0 && entry.Kind != SidebarEntryKind.AppRoute;
            bool unavailable = pinState == SidebarPinState.Unavailable;   // pinState: the SidebarPinStateRules.Of(…) above
            // … RowSpec { Label = label, Ink = placeholderTitle || unavailable ? Tok.TextTertiary : null, … }
```

`RowSpec` (`Sidebar.UI.Rows.cs`, P3-WP12) gains the one override both states need (the Unavailable title above uses it
too):

```csharp
        /// <summary>The label's (and so the glyph's — NVX:425-427, one ink per row) colour when not TextPrimary: a pin that
        /// is unavailable, or still unnamed (design Q9, D9). Null = TextPrimary.</summary>
        public ColorF? Ink;
```

(constructor: `Ink = null;`), and `EntityRow.Create`'s `var ink = Tok.TextPrimary;` becomes
`var ink = spec.Ink ?? Tok.TextPrimary;`. `SidebarPinStateRules` (§P3.4, P3-WP1) gains

```csharp
    /// <summary>The localized noun a pin row shows while nothing has named it yet (design D9: nothing blank is ever
    /// rendered). Null for a kind that always carries its own title (a route pin's destination title).</summary>
    public static string? FallbackTitleKey(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => "nav.playlist",
        SidebarEntryKind.Folder => "sidebar.pin.folder",
        SidebarEntryKind.Album => "nav.album",
        SidebarEntryKind.Artist => "nav.artist",
        SidebarEntryKind.Show => "nav.show",          // "Podcast": IsAudiobook is not known before hydration either
        _ => null,
    };
```

(`nav.playlist`/`nav.album`/`nav.artist`/`nav.show` exist in en-US; P3-WP18 adds `sidebar.pin.folder` = "Folder" to the
`pin` object of §P3.13's loc.) `EmptyRow`: Playlists only (the planner plans no Empty row in Library — the head owns that state, §P3.5) →
`Strings.Sidebar.Empty.Playlists`, or `LibraryEmptyText()` (the query copy) while Classic's Ctrl+F filter is live; the hint
row is `EmptyHintHeight` tall inside the 2/2 margin. `DropBandRow`:

```csharp
        /// <summary>The empty Pinned band while a pinnable drag is live (design V.3): 36 tall in the 4,2 margin, a dashed
        /// 1-px AccentDefault ring, r4, "Drop here to pin"; the drop pins at position 0.</summary>
        Element DropBandRow(SidebarSection section, int index) => new BoxEl
        {
            Key = "pin-drop-band", Height = SidebarRowGeometry.RowHeight, Shrink = 0f,
            Margin = new Edges4(0f, SidebarRowGeometry.RowMarginY, 0f, SidebarRowGeometry.RowMarginY),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            BorderWidth = 1f, BorderColor = Tok.AccentDefault, BorderDashOn = 4f, BorderDashOff = 2f,
            DropTarget = _o.ResourceDropSpec(section.Id, 0, null, null, rootPlanIndex: index),
            Children = [Ui.Caption(Loc.Get("sidebar.pin.dropHere")) with { Color = Tok.AccentDefault }],
        };
```

(`PinDropZone` is deleted.)

**Rows** (`Sidebar.UI.Rows.cs`): delete `EditCard`, `InlineControls`, `SearchHead`, `PinDropZone`, `RowGlyphs` (its only
callers were item overrides), `PaneIcon.Leading`; `PaneText.TitleOf`/`ItemOf`/`Glyph(item, …)`/`EmptyText(kind)` take
the new types (`EmptyText` keys: Recent → `sidebar.section.empty`, NewReleases → `PaneLoc.NewReleasesEmpty`).
`Cover.ForEntry` handles `AppRoute` `liked` with the heart tile (it does today through `LikedArt`; keep).

**Menus** (`Sidebar.UI.Menus.cs`): the row-menu signatures become exactly

```csharp
        internal Func<ContextMenuModel?>? EntryMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry entry, string rowKey)
        internal Func<ContextMenuModel?>? FolderMenu(SidebarSection section, int planIndex, in SidebarLibraryEntry folder,
                                                      Action activate, bool expanded, string rowKey)
        internal Func<ContextMenuModel?>? RouteMenu(SidebarSection section, string routeKey, int planIndex)
        Actions.Menu.Extras NavExtras(SidebarSection section, int planIndex, string key)
        (SidebarTreeNavLayout Layout, string EntryId) TreeMoves(SidebarSection section, int planIndex)
```

(bodies as today with the item gone: `EntryMenu`/`FolderMenu`/`RouteMenu` call `NavExtras(section, planIndex, rowKey)`;
`RouteMenu` builds its entry from `routeKey`; `NavExtras` loses `removable`, the trailing Remove row and its
`SidebarItemCommands.Remove` dispatch, and calls `SidebarNavLayout.Decide(at, count, removable: false)`; `TreeMoves`'s gate becomes
`if (!(section.Kind == SidebarSectionKind.Playlists || (section.Kind == SidebarSectionKind.Library && Config.TreeSortedNonCustom?.Invoke() == false)) || TryBandOf(planIndex, out _)) return (default, "");`
— the Library list under Playlists · Custom order IS the rootlist). Delete `LayoutOnlyMenu`, `MissingItemMenu`,
`EditCardMenu`, `OpenSectionOptions`, `OptionsSubjectWatch`, `ResolveActionRow`, `GridCellMenu`'s item paths.
`LayoutMenu.Rows()` becomes (until P4's pane menu):

```csharp
        public static IReadOnlyList<MenuFlyoutItem> Rows()
        {
            var layout = Sidebar.Layout.Peek();
            bool editing = Sidebar.Editing.Peek();
            return new List<MenuFlyoutItem>(5)
            {
                MenuFlyoutItem.RadioItem(LayoutName(SidebarLayoutId.Classic), layout == SidebarLayoutId.Classic,
                    static () => SwitchLayout(SidebarLayoutId.Classic)) with { Enabled = !editing },
                MenuFlyoutItem.RadioItem(LayoutName(SidebarLayoutId.Library), layout == SidebarLayoutId.Library,
                    static () => SwitchLayout(SidebarLayoutId.Library)) with { Enabled = !editing },
                MenuFlyoutItem.Separator,
                new(Loc.Get("sidebar.menu.resetLayout"), default, SidebarLayoutRules.IsModified(Sidebar.State.Of(layout)),
                    static () => Sidebar.Dispatch(new ResetLayout(Sidebar.Layout.Peek()))),
                new(Loc.Get("sidebar.menu.resetWidth"), default,
                    MathF.Abs(Width.Peek() - SidebarPaneBounds.DefaultWidth) > 0.5f, ResetWidth),
            };
        }

```

and, OUTSIDE `LayoutMenu`, directly on the `Sidebar` partial (same file), the one layout-name helper. It lives on
`Sidebar`, not in `LayoutMenu`, because P4 deletes `LayoutMenu` while the menu mapper and the Settings card keep calling it:

```csharp
    /// <summary>"Classic" / "Classic · modified" (design C.1). The ONE layout name: the pane menu, the menu mapper
    /// (P4 <c>SidebarMenus</c>), the reset toast and Settings › Sidebar all read it.</summary>
    internal static string LayoutName(SidebarLayoutId layout)
    {
        string name = Loc.Get(layout == SidebarLayoutId.Library ? "sidebar.layoutName.library" : "sidebar.layoutName.classic");
        return SidebarLayoutRules.IsModified(State.Of(layout)) ? Loc.Format("sidebar.layoutName.modified", ("layout", name)) : name;
    }
```

(`MenuFlyoutItem` is a record struct; `Enabled` is its init member. Inside `LayoutMenu` the unqualified `LayoutName(…)`
binds to the enclosing `Sidebar.LayoutName`.)

**Cards** (`Sidebar.Cards.cs`): delete `SidebarCardSurface.Hero`, `SidebarCards.Hero`, `HeroOf`; `Platform/Surface.Rules.cs`:
delete `Shape.SidebarHero`. The grid `Tile` is retyped (the slot's `GridStripRow` now passes a `SidebarSection`):

```csharp
        public static Controls.CardData Tile(PaneView o, SidebarSection section, in SidebarLibraryEntry entry, float cell,
                                             string sel)
```

with one body edit: the subtitle argument `section.Opts.Subtitles ? SubtitleOf(PaneText.SubtitleOf(in e)) : null` becomes
`section.Shape == SidebarRowShape.EntityTwoLine ? SubtitleOf(PaneText.SubtitleOf(in e)) : null` (Compact density drops
the second line in the grid as in the list). `DropOf(o, SidebarCardSurface.Tile, section.Id, in e)` is unchanged.

**Retyped leftovers in `Sidebar.UI.cs` and `Sidebar.UI.Flyout.cs`** (P3-WP11; `SidebarSectionSpec` is deleted with
`Sidebar.Doc.cs`, so each of these must change in the same commit):

- `PaneMetrics`: delete `ShapeOf(SidebarSectionSpec)`, `RowHeight(SidebarSectionSpec)`, `RowPitch(SidebarSectionSpec)`,
  `ArtSize(SidebarSectionSpec)`, `CardHeight(SidebarSectionSpec)` and `EditCardHeight` (P1.5). Every caller already has a
  `SidebarSection`: `PaneMetrics.ShapeOf(s)` → `s.Shape`; `RowHeight(s)` → `SidebarRowGeometry.HeightOf(s.Shape)`;
  `RowPitch(s)` → `SidebarRowGeometry.PitchOf(s.Shape)` (the `RebuildIndex` band extent); `ArtSize(s)` →
  `SidebarRowGeometry.ArtOf(s.Shape)`; `CardHeight`'s only callers (the hero, the slot's `HeroCard`) are deleted here.
  `PanePad`, `PaneInsetH`, `HeadBandInset`, `EmptyHintHeight`, `GridCellMax` stay.
- `PaneReorder`'s `Section` is a `SidebarSection` (above); `_sectionOf` is `Func<string, SidebarSection?>?`.
- `Sidebar.UI.Flyout.cs` `PaneFolderFlyout`: `ListOf` reads `var section = Owner.SectionOf(SectionId);` (now
  `SidebarSection?`) and `Row` becomes

```csharp
        Element Row(IReadOnlyList<SidebarLibraryEntry>? tree, SidebarSection? section, SidebarLibraryEntry entry,
                    string sel, bool cursored)
        {
            …
            // The SAME menus the pane builds. A folder's Expand verb still means the PANE's disclosure, not this drill-in.
            Func<ContextMenuModel?>? menu = section is null ? null
                : folder ? owner.FolderMenu(section, -1, in entry, () => owner.ExpandFolderInPane(entry.FolderId),
                                            IsFolderExpanded(entry.FolderId), entry.Id)
                : owner.EntryMenu(section, -1, in entry, entry.Id);
            …   // the rest verbatim (drops, DropActive, Shape = EntityTwoLine)
        }
```

**Drop** (`Sidebar.UI.Drop.cs`): `PinWithToast` refuses `SidebarPinRules.IsFixedRoute(pinId)` and a signed-out account
(toast `sidebar.pin.signedOut`); `SidebarSectionSpec` → `SidebarSection`, `SidebarSectionKind.PlaylistTree` →
`Playlists`/`Library` (a rootlist drop target is a row of the Playlists section, or of Your Library under
Playlists · Custom order).

### P3.13 Settings, setup, shell, keys, loc

**Settings › Appearance › Sidebar** (`Screens/Settings.UI.Appearance.cs`) — `SidebarDesignCard` → `SidebarLayoutCard`:

```csharp
    /// <summary>Settings › Sidebar (design C.2 entry 3): the layout (two cards, "· modified"), density, Edit sidebar (P4),
    /// reset; a corrupt-file InfoBar and the migration's "What changed" note when they apply. Subscribes to the layout
    /// signals, so a change made on the sidebar itself shows here live.</summary>
    sealed class SidebarLayoutCard : Component
    {
        public override Element Render()
        {
            var layout = Sidebar.Layout.Value;
            _ = Sidebar.LayoutVersion.Value;
            var density = Sidebar.Density.Value;
            bool editing = Sidebar.Editing.Value;
            var items = new List<Element>(4)
            {
                Item(Loc.Get("settings.sidebar.density"), Loc.Get("settings.sidebar.densitySub"),
                    Embed.Comp(() => new SidebarDensityPicker()) with { Key = "sidebar.density:" + (int)density }, icon: Icons.ViewList),
                Item(Loc.Get("settings.sidebar.reset"), Loc.Get("settings.sidebar.resetSub"), null,
                    isClickEnabled: !editing && SidebarLayoutRules.IsModified(Sidebar.State.Of(layout)),
                    onClick: static () => Sidebar.Dispatch(new ResetLayout(Sidebar.Layout.Peek())), icon: Icons.Undo),
            };
            if (Sidebar.LayoutFileFault)
                items.Insert(0, Item(Loc.Get("settings.sidebar.fileFault"), "", null, icon: Icons.Warning));
            if (Platform.Settings.Get(Platform.Keys.SidebarMigrationDropped) is { Length: > 0 } dropped)
                items.Add(Item(Loc.Get("settings.sidebar.whatChanged"),
                    Loc.Format("settings.sidebar.whatChangedSub", ("names", Sidebar.DroppedNames(dropped.Split(',')))), null,
                    icon: Icons.Info));
            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get("settings.sidebar.layout"),
                Description = Loc.Get("settings.sidebar.layoutSub"),
                HeaderIcon = RowGlyph(Tab.Appearance, "sidebarDesign"),
                Content = ValueTag(Sidebar.LayoutName(layout)),
                ItemsHeader = ExpanderPanel(SidebarLayoutCards(layout, editing)),
                Items = [.. items],
            }) with { Key = "appearance.sidebar.layout" };
        }
    }

    /// <summary>Default · Compact (design P.4).</summary>
    sealed class SidebarDensityPicker : Component
    {
        static readonly Signal<int> s_index = new(0);
        public override Element Render()
        {
            int index = (int)Sidebar.Density.Peek();
            UseEffect(() => s_index.Value = index, DepKey.From(index));
            return ComboBox.Create([Loc.Get("sidebar.density.default"), Loc.Get("sidebar.density.compact")], s_index, width: 160f,
                onChange: static i => Sidebar.SetDensity(i == 1 ? SidebarDensity.Compact : SidebarDensity.Default));
        }
    }

    /// <summary>The two layout cards (Classic · Library), applied at once; disabled while the sidebar is being edited
    /// (design Q7). Public: the setup wizard's Layout step reuses them (P4).</summary>
    public static Element SidebarLayoutCards(SidebarLayoutId active, bool editing)
        => Controls.PickerStrip(2, (int)active,
            static (i, on) => SidebarLayoutCardFace((SidebarLayoutId)i, on),
            editing ? static _ => { } : static i => Sidebar.SwitchLayout((SidebarLayoutId)i));
```

`SidebarLayoutCardFace(layout, on)` is today's `SidebarDesignCardFace` with `SidebarPreview` drawing the Classic sketch for
Classic and the V3 sketch for Library (the Curated arm and the "Customize sidebar" item are deleted), its title
`Sidebar.LayoutName(layout)`, its subtitle `sidebar.layoutName.classicSub` / `.librarySub`. The section header's
subtitle reads "Saved on this PC" (`settings.sidebar.subtitle` value changes). `kids.Add(Embed.Comp(static () => new
SidebarLayoutCard()));` replaces the old card.

**Setup** (`Screens/Setup.cs:706`): delete `settings.Set(Platform.Keys.SidebarOnboardingSeen, true);`.

**Shell** (`Shell/Shell.cs`, `Shell/Shell.UI.cs`): delete `RouteKind.SidebarCustomize`, its route-table row
(`"sidebar-customize"`), its `PlaceByArg`/`363` mention and the `SetPage(RouteKind.SidebarCustomize, …)` line.

**Diagnostics**: `SidebarPaneFrameSnapshot.Design` becomes `SidebarLayoutId Layout` (the frame snapshot and the log field
`layout`).

**Platform keys** (`Platform/Platform.cs`): delete `SidebarDesign`, `SidebarOnboardingSeen`, `ClassicPinnedOpen`,
`ClassicLibraryOpen`, `ClassicPlaylistsOpen`, `V3Filter`, `V3Sort`, `V3Desc`, `V3View`, `V3GridSize`, `CuratedTemplateId`,
`CuratedRailLabels`, `SidebarWidth(slug, …)`, `SidebarCollapsed(slug)`; keep `SidebarWidthLegacy`, `SidebarWidthUserSetLegacy`,
`SidebarCollapsedLegacy`, `SidebarBootstrapVersion`, `PinsMigratedToServer` (read by the migration only; doc comment says
so) and `V3Qualifier` (until P5); add:

```csharp
        /// <summary>The active sidebar layout: 0 Classic · 1 Library (design D1). Written by the v2 migration once.</summary>
        public static readonly SettingKey<int> SidebarLayoutId = new("sidebar.layout.id", 0);
        /// <summary>The expanded pane width, one value for both layouts (180-460).</summary>
        public static readonly SettingKey<float> SidebarPaneWidth = new("sidebar.pane.width", 320f);
        /// <summary>The user's collapse to the 48 rail — written only in the Wide band, never by a forced mode.</summary>
        public static readonly SettingKey<bool> SidebarPaneUserCollapsed = new("sidebar.pane.userCollapsed", false);
        /// <summary>Entity-row density: 0 Default · 1 Compact.</summary>
        public static readonly SettingKey<int> SidebarPaneDensity = new("sidebar.pane.density", 0);
        /// <summary>Your Library's chip: 0 none · 1 Playlists · 2 Albums · 3 Artists · 4 Podcasts · 5 Audiobooks.</summary>
        public static readonly SettingKey<int> SidebarLibraryFilter = new("sidebar.library.filter", 0);
        /// <summary>The loc keys of what the v2 migration could not carry over, comma-separated (Settings' "What changed").</summary>
        public static readonly SettingKey<string> SidebarMigrationDropped = new("sidebar.migration.dropped", "");
```

The `SidebarBootstrapVersion` doc comment: "2 = the v3 layout migration ran". `TestAppSettingsShim` (if Wavee.Tests still
mirrors `SidebarKeys`) gets the same edits.

**Loc** (en-US; nl/ko fall back): add

```json
"collections": "Collections",
"recentlyPlayed": "Recently played",
"layoutName": {
  "classic": "Classic", "library": "Library", "modified": "{layout} · modified",
  "classicSub": "Sections you can show, hide and reorder.", "librarySub": "Spotify's one list with filters."
},
"density": { "default": "Default", "compact": "Compact" },
"menu": { "resetLayout": "Reset this layout" },
"pin": { "unavailable": "Unavailable", "dropHere": "Drop here to pin", "signedOut": "Sign in to pin to the sidebar",
         "folder": "Folder",
         "cantPin": { "track": "Tracks can't be pinned", "episode": "Episodes can't be pinned",
                      "local": "Local files can't be pinned", "home": "Home is always first",
                      "liked": "Liked Songs always has its own row" } },
"migration": {
  "toast": "Your sidebar became {layout}. Shortcuts are now pins.",
  "toastDropped": "Your sidebar became {layout}. Shortcuts are now pins. Not carried over: {names}.",
  "kind": { "spotlight": "Spotlight", "queue": "Queue", "nowPlaying": "Now playing", "topTracks": "Artist top tracks",
            "concerts": "Concerts", "links": "Links", "heading": "Headings", "group": "Groups",
            "libraryList": "Library list", "extension": "Extension sections" }
}
```

under `sidebar` (merge `menu` and `pin` into the existing objects), and under `settings.sidebar`: `layout` ("Layout"),
`layoutSub` ("Classic or Library. Applies immediately."), `density` ("Density"), `densitySub` ("Two-line or one-line
rows for playlists and albums"), `reset` ("Reset this layout"), `resetSub` ("Back to the default sections and order"),
`fileFault` ("Your sidebar layout couldn't be read, so the default is shown."), `whatChanged` ("What changed"),
`whatChangedSub` ("Not carried over from your custom sidebar: {names}."); `settings.sidebar.subtitle` → "Saved on this
PC". Delete every `sidebar.customizer.*`, `sidebar.template.*`, `sidebar.option.*`, `sidebar.palette.*`,
`sidebar.source.*`, `sidebar.topbar.*`, `sidebar.chooser.*` (check `sidebar.chooser.*` callers first: the chooser
strings are also used by the history page's `chooser.history` subtree — keep any key with a remaining caller),
`sidebar.design.*`, `sidebar.layout.*`, `settings.sidebar.customize`, `.customizeSub`, `.design`, `.designShort`,
`.designSub`; prune those names from `$unusedAllow`.

### P3.14 Deletion checklist

Files: `Shell/Sidebar.Doc.cs`, `Shell/Sidebar.Customizer.UI.cs`. Types (beyond §P3.6/§P3.11/§P3.12): everything in
`Sidebar.Doc.cs` except what moved (`MenuLabel` → `Sidebar.Rules.cs`; the v2 DTOs → `Sidebar.Store.V2.cs`;
`SidebarFirstSeenDto` → `Sidebar.Store.cs`; pin-kind wire → `SidebarPinWire`). From `Sidebar.Modes.cs`: `SidebarDesign`,
`ClassicSection`, `SidebarDesignInfo`, `SidebarPaneSnapshot`, `SidebarPaneState`, `SidebarDesignGating`,
`SidebarBuiltInDocuments`, `ClassicDocumentCache`, `SidebarNavBandTileKind`, `SidebarNavBandTile`, `SidebarNavBandModel`,
`LibraryV3Document` (its `FoldersApply` and `ClampColumns` move to `LibraryV3Metrics`, §P3.12), the "two built documents"
`Sidebar` partial. Kept in
`Sidebar.Modes.cs` until P5: `SidebarV3Filter`, `SidebarV3Qualifier`, `SidebarV3Sort`, `SidebarV3View`, `LibraryV3Metrics`,
`LibraryV3Labels`, `LibraryV3SearchRules`, `V3ChipKind`, `V3ChipSlot`, `LibraryV3ChipStrip`, `LibraryV3View`,
`LibraryV3Window`, `LibraryV3DocState`. `Drag.ExtraChip` is no longer assigned anywhere (the customizer was its only
writer) — leave the engine slot; delete nothing engine-side.

### P3.15 Tests

Every new test class is pure (no engine host, no disk except a temp folder for the file store).

**New `SidebarCatalogueTests.cs`**

- `Classic_HasSevenSections_InOrder` — `KindsOf(Classic)` = Home, Pinned, Collections, Playlists, Recent, NewReleases, Settings.
- `Library_HasFour_NoCollections_NoFeeds` — Home, Pinned, Library, Settings; `Has(Library, Collections)` false.
- `Ids_RoundTrip` — for every kind, `TryKindOf(IdOf(k))` gives `k`; `TryKindOf("spotlight")` is false.
- `Locks` — `Hideable(Home/Playlists/Library)` false; `Hideable(Settings)` true; `Movable(Library, Pinned)` false;
  `Movable(Classic, Playlists)` true; `Movable(Classic, Home)` false; `Collapsible(Library)` false.
- `Items` — Classic Collections = liked, albums, artists, podcasts, audiobooks; Library library = albums, artists,
  podcasts, audiobooks; everything else empty.
- `Defaults` — Recent hidden limit 5; NewReleases hidden limit 10; Library sort Recents, view List, liked shown.

**New `SidebarLayoutRulesTests.cs`** (helper `static SidebarLayoutState S() => SidebarLayoutState.Default;` and
`Apply(op, pinnedLocked: false)`):

- `Hide_Collections_SetsHidden_AndIsModified`.
- `Hide_Home_Playlists_Library_Refused_Locked` (three facts).
- `Hide_Pinned_RefusedWhileLocked` / `Hide_Pinned_AllowedWhenNotLocked`.
- `Show_Recent_DropsOnlyTheHiddenBit`.
- `Move_Classic_ReordersMovableSlots_LockedKeepTheirPlace` — move `newReleases` to slot 0: order becomes home,
  newReleases, pinned, collections, playlists, recent, settings.
- `Move_OutOfRange_Refused`; `Move_InLibrary_Refused_NotMovable`.
- `Collapse_NotRecordedAsModified` — collapse Pinned ⇒ `Changed`, `IsModified` false.
- `Collapse_Home_Refused`.
- `Limit_Accepts5_10_20_Only` (`SetSectionLimit(recent, 7)` ⇒ `BadLimit`).
- `HideItem_AddsToHiddenItems`; `HideLastItem_HidesSection_KeepsPreviousHiddenItems` (hide 4 of 5 items one by one,
  then the 5th: section `Hidden` true, `HiddenItems` holds 4); `ShowSection_AfterAutoHide_RestoresTheLastItem`.
- `HideItem_Unknown_Refused`.
- `MoveItem_StoresOrder_NullWhenBackToDefault`.
- `LibrarySort_View_Liked_Persist_AndCountAsModified`.
- `Reset_KeepsCollapse_DropsEverythingElse`.
- `Replace_MergesAndSanitizes` — a replacement with an unknown id and a bad limit comes back merged.
- `Merge_DropsUnknown_ReportsIt`, `Merge_InsertsMissingAfterPrevious`, `Merge_HomeFirst_LibraryLast_SettingsLast`,
  `Merge_DropsDuplicateIds`, `Merge_FiltersHiddenItemsToCatalogue`, `Merge_CapsHiddenItemsAt64`.
- `IsModified_DefaultFalse_OrderTrue_HiddenItemTrue_ItemOrderTrue`.
- `Resolve_ShapesFollowDensity` — Default: Pinned `EntityTwoLine`, Collections `Glyph`; Compact: Pinned `EntityOneLine`.
- `Resolve_LibraryHiddenKinds_Flags` — hide podcasts + audiobooks ⇒ `HiddenKinds = Podcasts | Audiobooks`.
- `Resolve_VisibleItems_InEffectiveOrder`.

**New `SidebarVisibilityRulesTests.cs`**

- `Dedupe_Matrix` — Classic: Pinned visible+expanded true; visible+collapsed false; visible+collapsed+compact true;
  hidden false. Library: visible true (collapsed irrelevant); hidden false.
- `LibraryPins_RoutePin_NoChip_FollowsTheSearch_AnyChipHides`:

```csharp
    [Fact]
    public void LibraryPins_RoutePin_NoChip_FollowsTheSearch_AnyChipHides()
    {
        var pin = SidebarLibraryEntry.ForRoute("search", "Search");
        var none = SidebarLibraryKinds.None;
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "", none));
        Assert.True(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "sea", none));   // P.2a / Q2
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.None, "xyz", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.Albums, "", none));
        Assert.False(SidebarVisibilityRules.ShowsPinInLibrary(in pin, SidebarLibraryFilter.Playlists, "sea", none));
    }
```
- `LibraryPins_EntityFollowsChip_HiddenKinds_Search` (an album pin is hidden under the Playlists chip and when Albums is
  hidden; matches a search on its name).
- `Liked_UnderNoneAndPlaylistsOnly_RespectsShowLiked_AndSearch`.
- `LockingPins_NamesRouteAndModulePins`.
- `Filters_Matches_SplitsPodcastsAndAudiobooks` (a show with `IsAudiobook` is Audiobooks, not Podcasts).
- `Filters_HasChip_FalseForHiddenKind`; `Filters_Effective_FallsBackToNone_WhenItsKindIsHidden`.
- `PinState_PendingOnlineUnknown_UnavailableOnAuthoritativeMiss_OfflineNeutral`.
- `PinState_FallbackTitle_KindNoun_NeverBlank`:

```csharp
    [Fact]
    public void PinState_FallbackTitle_KindNoun_NeverBlank()
    {
        // A pin synced from another device before the projection hydrates has no name anywhere: D9 forbids a blank row.
        foreach (var kind in new[] { SidebarEntryKind.Playlist, SidebarEntryKind.Folder, SidebarEntryKind.Album,
                                     SidebarEntryKind.Artist, SidebarEntryKind.Show })
            Assert.False(string.IsNullOrEmpty(SidebarPinStateRules.FallbackTitleKey(kind)));
        Assert.Equal("nav.album", SidebarPinStateRules.FallbackTitleKey(SidebarEntryKind.Album));
        Assert.Null(SidebarPinStateRules.FallbackTitleKey(SidebarEntryKind.AppRoute));   // a route pin has its page title
    }
```

**`SidebarPlannerTests.cs` — rewritten.** Delete every fact that builds a `SidebarCustomLayout`, a `SidebarSectionSpec`,
a `SidebarEditState`, a `LibraryV3Document` or calls `BuildEdit`. Keep the classes in the file that test types that
survive (`SidebarRowDiff`, `SidebarRowResolve.Flipped`, `SidebarStageHold`, `SidebarReorderClamp`, `SidebarPaneInvariant`
(P2 form), the `SidebarRowGeometry` plan helpers). Add a planner fixture:

```csharp
static class PlanFixture
{
    public static SidebarLibraryEntry Playlist(string id, int depth = 0, string folder = "")
        => new(id, SidebarEntryKind.Playlist, id.Replace("pl:", ""), "P " + id, "", default, null, 3, 0, 0, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = folder, FolderName = "", FirstArtistName = "", CountKnown = true };
    public static SidebarLibraryEntry Folder(string folderId, int depth = 0)
        => new("folder:" + folderId, SidebarEntryKind.Folder, "", "F " + folderId, "", default, null, 1, 0, 0, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = folderId, FolderName = "", FirstArtistName = "", CountKnown = true };
    public static SidebarLibraryEntry Album(string id) => new("album:" + id, SidebarEntryKind.Album, id, "A " + id, "", default, null,
        10, 0, 0, 0, 0, 0, false, SidebarPlaylistFlavor.None) { FolderId = "", FolderName = "", FirstArtistName = "" };
    public static SidebarLibraryEntry Route(string key) => SidebarLibraryEntry.ForRoute(key, key);
    public static SidebarLayoutDoc Doc(SidebarLayoutId layout, Func<SidebarLayoutState, SidebarLayoutState>? edit = null,
                                       SidebarDensity density = SidebarDensity.Default)
        => SidebarLayoutRules.Resolve((edit ?? (s => s))(SidebarLayoutState.Default), layout, density);
    public static SidebarLayoutState Op(SidebarLayoutState s, SidebarOp op) => SidebarLayoutRules.Apply(s, op, false).State;
    public static SidebarRowKind[] Kinds(in SidebarRowPlan p) { var k = new SidebarRowKind[p.Rows.Count]; for (int i = 0; i < k.Length; i++) k[i] = p.Rows[i].Kind; return k; }
}
```

Facts (each builds `SidebarRowPlanner.Build(doc, in input, in options)`):

1. `Classic_NoPins_HomeSepCollectionsSepPlaylists` — rows: IconRow(home), Divider, SectionHeader(collections), 5 ×
   IconRow, Divider, SectionHeader(playlists), …tree…, TreeEnd. No Pinned header (empty Pinned is not rendered).
2. `Classic_Pins_UnderTheirHeader_DedupedFromTheTree` — pin `pl:a`, tree [`pl:a`, `pl:b`] ⇒ Pinned shows `pl:a`, Playlists
   shows only `pl:b`.
3. `Classic_PinnedCollapsed_Expanded_NoDedupe` — collapsed Pinned ⇒ header only, and `pl:a` is back in Playlists.
4. `Classic_PinnedCollapsed_Compact_OneTile_Deduped`.
5. `Classic_CollapsedSection_HeaderOnly`; `Classic_CollapsedEmptySection_NotRendered`.
6. `Classic_EmptyRecent_NotRendered_NoDoubleSeparator` (Recent shown, no plays ⇒ no header, no separator).
7. `Classic_Recent_RespectsLimit` (12 plays, limit 5 ⇒ 5 rows).
8. `Classic_HiddenSection_NotRendered`.
9. `Classic_EmptyTree_HeaderAndHint`; `Classic_PendingTree_Skeletons`.
10. `Classic_CollapsedFolder_SkipsChildren`; `Classic_ExpandedFolder_ChildrenAtTheirDepth`.
11. `Classic_Collections_OrderAndHidden_AndPinnedRouteDeduped` (pin `albums` ⇒ the Albums row moves to Pinned).
12. `Classic_Compact_NoHeaders_TopLevelOnly_NoGutter`.
13. `Classic_DropArmed_EmptyPinned_HeaderAndDropBand`; `…_HiddenPinned_NoBand`; `…_Compact_NoBand`.
14. `Classic_Separators_NeverFirst_NeverDouble` (all optional sections shown, some empty).
15. `Classic_Search_FlattensPlaylists_NoGutter`.
16. `Classic_SettingsNeverPlanned`.
17. `Library_PinsFirst_NoHeader_ThenLiked_ThenList`.
18. `Library_AlbumsChip_HidesLikedAndRoutePins`.
19. `Library_ShowLikedOff_NoLikedRow`.
20. `Library_HiddenKind_PinGone`.
21. `Library_Grid_Strips` (7 entries, 3 columns ⇒ 3 strips of 3, 3, 1).
22. `Library_EmptyList_PlansNoEmptyRow` — an empty shaped list under the Albums chip plans no `SidebarRowKind.Empty` row
    (with or without Liked shown, with or without pins): the head owns the empty state (§P5.5).
22a. `Library_PinnedFolder_ExpandsInline` and `Library_PinnedFolder_NotInlineWhenDrilled`:

```csharp
    [Fact]
    public void Library_PinnedFolder_ExpandsInline()
    {
        // Pin folder f (tree: folder f → pl:a, pl:b). Expanded in the wide pane: its children follow it, one level in.
        var folder = PlanFixture.Folder("f") with { IsPinned = true };
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f"),
                           PlanFixture.Playlist("pl:b", depth: 1, folder: "f") };
        var input = new SidebarProjectionInput(Library: [], PlaylistTree: tree, Pins: [folder],
                                               ExpandedFolders: new HashSet<string> { "f" });
        var plan = SidebarRowPlanner.Build(PlanFixture.Doc(SidebarLayoutId.Library), in input, new SidebarPlanOptions());
        Assert.Equal(SidebarRowKind.FolderHeader, plan.Rows[0].Kind);
        Assert.Equal("pl:a", plan.Rows[1].Key);
        Assert.Equal(1, plan.Rows[1].Depth);
        Assert.Equal("pl:b", plan.Rows[2].Key);
    }

    [Fact]
    public void Library_PinnedFolder_NotInlineWhenDrilled()
    {
        var folder = PlanFixture.Folder("f") with { IsPinned = true };
        var tree = new[] { PlanFixture.Folder("f"), PlanFixture.Playlist("pl:a", depth: 1, folder: "f") };
        var input = new SidebarProjectionInput(Library: [], PlaylistTree: tree, Pins: [folder],
                                               ExpandedFolders: new HashSet<string> { "f" });
        var plan = SidebarRowPlanner.Build(PlanFixture.Doc(SidebarLayoutId.Library), in input,
                                           new SidebarPlanOptions(FoldersInline: false));
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:a");   // the narrow pane drills (ActivateFolder) instead
    }
```

(`SidebarRow.Depth` is a `byte`; `Assert.Equal(1, …)` compares as `int`. The children are appended INSIDE the pin loop,
so they sit at `Rows[1..]`, right after their folder; the Liked row — shown under no chip even with `LikedTitle` null —
comes after the whole pin block.)
22b. `Library_Drilled_NoPins_NoLiked` and `Library_Drilled_PinnedChild_Once`:

```csharp
    [Fact]
    public void Library_Drilled_NoPins_NoLiked()
    {
        // Drilled into folder f: the level is f's children only — no pin rows, no Liked row, no drop band even mid-drag.
        var pin = PlanFixture.Playlist("pl:p") with { IsPinned = true };
        var level = new[] { PlanFixture.Playlist("pl:a", depth: 0, folder: "f") };
        var input = new SidebarProjectionInput(Library: level, Pins: [pin], LikedTitle: "Liked Songs");
        var plan = SidebarRowPlanner.Build(PlanFixture.Doc(SidebarLayoutId.Library), in input,
                                           new SidebarPlanOptions(FoldersInline: false, Drilled: true, PinDropArmed: true));
        Assert.Single(plan.Rows);
        Assert.Equal("pl:a", plan.Rows[0].Key);
        Assert.DoesNotContain(plan.Rows, r => r.Key == "pl:p" || r.Key == SidebarCatalogue.LikedRoute
                                              || r.Kind == SidebarRowKind.DropBand);
    }

    [Fact]
    public void Library_Drilled_PinnedChild_Once()
    {
        // pl:a is pinned AND a child of f: drilled, the shaped level keeps it (no pin skip while drilled) and the pin
        // block is not planned, so it shows exactly once.
        var pinA = PlanFixture.Playlist("pl:a", depth: 0, folder: "f") with { IsPinned = true };
        var input = new SidebarProjectionInput(Library: [pinA], Pins: [pinA]);
        var plan = SidebarRowPlanner.Build(PlanFixture.Doc(SidebarLayoutId.Library), in input,
                                           new SidebarPlanOptions(FoldersInline: false, Drilled: true));
        Assert.Single(plan.Rows, r => r.Key == "pl:a");
    }
```

23. `Library_CustomOrderTree_EndsWithGutter`; `Library_RecentsSort_NoGutter`.
24. `Library_Compact_TopLevelOnly`.
25. `BuildSection_ReturnsTheSectionsRows_TopLevel`.
26. `LikedAppearsOncePerLayout` — Classic: exactly one IconRow `liked` (Collections), no EntityRow `liked`; Library:
    exactly one EntityRow `liked`, no IconRow `liked`.
27. `Extents_Header40_Separator8_Entity44_Glyph40_DropBand40` via `SidebarRowExtents.HeightOf`.

**New `SidebarStoreV3Tests.cs`**

- `Default_RoundTrips` (`TryParse(Serialize(Default))` equals `Default`).
- `DefaultSection_WritesItsIdOnly` (deserialize the bytes into `SidebarDeviceFileDto`; every `Hidden`/`Limit`… of a
  default Pinned is null).
- `EditedState_RoundTrips` (hidden collections item, moved section, library sort/view/liked, recent limit 20).
- `UnknownSection_Dropped_Reported`; `MissingSection_Merged`.
- `WrongVersion_NotParsed`; `Garbage_NotParsed`.

**New `SidebarAccountStoreTests.cs`**

- `Key_ProviderColonAccount`; `Key_EmptyAccountIsSignedOut`; `Key_MarketAndLocaleDoNotChangeIt` (two `CatalogScope`s that
  differ only in market/locale/tier give the same key).
- `Hash_Stable16Hex`; `FileName_SignedOutIsPending`.
- `RoundTrip_SyncedNamesLocalRecordsAndOrder` — pins [`pl:spotify:playlist:a`, `search`, `module:radio`,
  `album:spotify:album:b`] round-trip in order with names and kinds.
- `Parse_DropsLikedAndHome` (Q1a); `Parse_CapsAt2000`; `Parse_DropsUnknownLocalKind`; `WrongVersion_NotParsed`.
- `Latch_IsPerAccount` — two `SidebarAccountData` with different keys keep separate `MigratedToServer`.
- `PinLatch_Matches_OnlyWhenLoadedEqualsLive_AndSignedIn` (A/A true; A/B false; ""/"" false).
- `NoCrossAccountApplyServer` — a `SidebarPinStore` loaded with A's pins, a latch whose loaded key is "spotify:a" and live
  key "spotify:b", a `LibraryPinSync` with a recording `write`: `ApplyServer([b's pins], converged: true)` returns false,
  the store is unchanged and `write` was never called; then a local `Pin(...)` writes nothing.
- `SameKeyScopeSwitch_NoSwap` — the pure half: `SidebarAccountKey.Of` equal ⇒ `EnsureAccount` returns false at the key
  compare; assert on the key equality here (the swap itself is covered by `SidebarWiringTests`' boot facts, §P3.15).

**`SidebarPinSyncTests.cs`** — every `new LibraryPinSync(store, settings, …)` becomes `new LibraryPinSync(store,
TestLatch(loaded: "spotify:me", live: "spotify:me", migrated), …)` with

```csharp
    sealed class LatchBox { public bool Migrated; }
    static SidebarPinLatch TestLatch(string loaded, string live, LatchBox box)
        => new(() => loaded, () => live, () => box.Migrated, v => box.Migrated = v);
```

and the facts that read `Platform.Keys.PinsMigratedToServer` read `box.Migrated`. The `Rig` constructor becomes

```csharp
        public readonly LatchBox Latch = new();

        public Rig(bool migrated, Func<string, bool>? hasPending = null)
        {
            Latch.Migrated = migrated;
            Sync = new LibraryPinSync(Pins, TestLatch("spotify:me", "spotify:me", Latch),
                (uri, pinned) => Writes.Add((uri, pinned)), hasPending ?? (_ => false));
        }
```

(the `MemoryAppSettings Settings` field and the `routeTitle` lambda go). New facts:
`ServerLiked_NeverBecomesAPin` (`spotify:collection` in the server set ⇒ no `liked` pin; nothing written);
`LocalLiked_IsNeverWritten` (the store refuses it; no write); `Mismatch_NoConvergeNoWrite`; and the grace marks (Q1b):

```csharp
    [Fact]
    public void SameAccountScopeSwitch_KeepsPendingMarks()
    {
        long t = 1_000;
        var marks = new PinWriteMarks(() => t);
        marks.Rebind("spotify:me");
        marks.Note("spotify:playlist:a", inFlight: true);
        marks.Rebind("spotify:me");                       // a market / locale / tier switch: same account
        Assert.True(marks.IsPending("spotify:playlist:a"));
        marks.Rebind("spotify:other");                    // a real account change
        Assert.False(marks.IsPending("spotify:playlist:a"));
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void SettledMark_ExpiresAfterTheGrace()
    {
        long t = 0;
        var marks = new PinWriteMarks(() => t);
        marks.Note("spotify:playlist:a", inFlight: false);
        t = PinWriteMarks.GraceMs - 1;
        Assert.True(marks.IsPending("spotify:playlist:a"));
        t = PinWriteMarks.GraceMs;
        Assert.False(marks.IsPending("spotify:playlist:a"));
    }

    [Fact]
    public void SameAccountScopeSwitch_ConvergedWalk_DoesNotSweepAnInFlightPin()
    {
        var marks = new PinWriteMarks(() => 0);
        marks.Rebind("spotify:me");
        var rig = new Rig(migrated: true, hasPending: marks.IsPending);
        Assert.True(rig.Pins.Pin(Pin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a")));
        marks.Note("spotify:playlist:a", inFlight: true);  // the ylpin write the pin raised is in flight …
        marks.Rebind("spotify:me");                         // … when the market switches
        rig.Server(converged: true);                        // a converged read that raced the write
        Assert.True(rig.Pins.IsPinned("pl:spotify:playlist:a"));
    }
```

**New `SidebarMigrationV2Tests.cs`** (fixtures are DTO objects built in code):

- `Design0_Classic_FlagsBecomeCollapsed`.
- `Design1_Library_SortDescView_CompactListBecomesCompactDensity`.
- `Design1_FilterMapped` (V3 Podcasts=2 ⇒ `SidebarLibraryFilter.Podcasts`).
- `Design2_ListNoTree_IsLibrary`; `Design2_WithTree_IsClassic`.
- `Design2_JumpBackIn_BecomesRecent_LimitNearest` (MaxItems 12 ⇒ 10, 0 ⇒ 5).
- `Design2_NewReleases_Shown`; `Design2_HiddenPinned_Hidden`; `Design2_HiddenShortcutItems_BecomeHiddenItems`.
- `Design2_DroppedKinds_Named_InOrder_NoDuplicates` (spotlight, queue, two dividers ⇒ [spotlight, queue]).
- `TopBar_HomeOnly_NoToast`; `TopBar_Shortcuts_BecomeLocalPins_Prepended_HomeAndLikedDropped_Toast`.
- `V2Pins_LikedAndHomeDropped` (Q1a); `V2Pins_LegacyKindDecoded`.
- `FoldersAndFirstSeen_Carried`; `Width_Clamped`; `Collapsed_Carried`.
- `SignedOut_AccountKeyEmpty_IsPending`; `PinsMigrated_Carried`.
- `LayoutDerivedFromDesign_WithoutAFile` (design 1, `V2 = null` ⇒ Library, no pins, no toast).

**New `SidebarFeedsTests.cs`** (`SidebarRecentsRules`, `SidebarNewReleasesRules`, `SidebarFeedDemands`):

- `Recents_IndexHit_StampedWithPlayTime`; `Recents_PeekWhenIndexMisses`; `Recents_LoggedTitleFallback`;
  `Recents_UnnamedSkipped_NeverBlank`; `Recents_TracksSkipped`; `Recents_Deduped`; `Recents_Limit`;
  `Recents_LikedCollectionIsTheLikedRoute`.
- `NewReleases_ShouldRefresh_Matrix` (Loading ⇒ false; Idle ⇒ true; asked 10 min ago ⇒ false; 31 min ⇒ true).
- `NewReleases_Fill_SkipsNonReleases_IndexHit_TitleStub_NoTitleSkipped`; `NewReleases_NewSince`.
- `Demand_OnlyShownFeeds`.

(`ISidebarEntityPeek` test double: a dictionary-backed class in the test file.)

**`SidebarStoreTests.cs` → `SidebarFileStoreTests.cs`** (rename; the v2 facts are deleted with the v2 store): commit lands
after `WaitForWrites`; a second commit rotates a `.bak`; `MarkCorrupt` moves the file to `.corrupt`; `Read` on a missing
file is `Missing`; a commit over `MaxDocumentBytes` is refused whole and reports `DocumentTooLarge` (temp folder per test,
deleted in `Dispose`).

**`SidebarProjectionTests.cs`** — delete the facts that use `SidebarDataSourceTable`, `ISidebarContributionHost`,
`SidebarExtensionSlices`, `SidebarContributionCache`, `SidebarCustomLayout`, `SidebarSectionSpec`, `SidebarDesign`,
`ClassicDocumentCache`, `SidebarSources*`; convert `SidebarV3Filter`/`SidebarV3Sort`/`SidebarV3Query` in the shaping facts
to `SidebarLibraryFilter`/`SidebarLibrarySort`/`SidebarLibraryQuery` (V3 `All` ⇒ `None`; `Custom` ⇒ `CustomOrder`; the
`customOrder` overlay argument is gone — facts that pinned the local overlay are deleted). Add
`UnnamedPlayedEntry_IsSkipped` (through `SidebarRecentsRules`), `PinnedPlaylist_AbsentFromTheTree_UnderCustomOrder`
(Library plan with `LibraryIsTree`, a pin `pl:a` and the shaped list without it), and the two no-search shaping facts
(they hold in P5 unchanged: `HiddenKinds` is named and `Filter` is the first positional argument in both query shapes):

```csharp
    static SidebarLibraryEntry ShowEntry(string id, bool audiobook)
        => new("show:" + id, SidebarEntryKind.Show, "spotify:show:" + id, id, "", default, null, 0, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None) { FolderId = "", FolderName = "", FirstArtistName = "", IsAudiobook = audiobook };
    static SidebarLibraryEntry AlbumEntry(string id)
        => new("album:" + id, SidebarEntryKind.Album, "spotify:album:" + id, id, "", default, null, 10, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None) { FolderId = "", FolderName = "", FirstArtistName = "" };

    [Fact]
    public void Shape_PodcastsChip_ExcludesAudiobooks()
    {
        // The projection's mask for both chips is Show: with NO search, Shape alone splits them.
        var list = new List<SidebarLibraryEntry> { ShowEntry("pod", false), ShowEntry("book", true) };
        var podcasts = new SidebarLibraryQuery(SidebarLibraryFilter.Podcasts);
        var shape = SidebarBinderPipeline.Shape(list, new List<SidebarLibraryEntry>(), in podcasts);
        Assert.Equal(1, shape.Count);
        Assert.Equal("show:pod", list[0].Id);

        list = [ShowEntry("pod", false), ShowEntry("book", true)];
        var audiobooks = new SidebarLibraryQuery(SidebarLibraryFilter.Audiobooks);
        SidebarBinderPipeline.Shape(list, new List<SidebarLibraryEntry>(), in audiobooks);
        Assert.Equal("show:book", Assert.Single(list).Id);
    }

    [Fact]
    public void Shape_HiddenKind_RemovedFromUnfilteredList()
    {
        var list = new List<SidebarLibraryEntry> { AlbumEntry("a"), ShowEntry("pod", false), ShowEntry("book", true) };
        var query = new SidebarLibraryQuery(HiddenKinds: SidebarLibraryKinds.Albums | SidebarLibraryKinds.Audiobooks);
        var shape = SidebarBinderPipeline.Shape(list, new List<SidebarLibraryEntry>(), in query);
        Assert.Equal(1, shape.Count);
        Assert.Equal("show:pod", list[0].Id);
    }
```

**`SidebarWiringTests.cs`** — delete `SidebarCanvasRuleTests`; `SidebarRecencyFoldTests` asserts the context title is
carried; `SidebarBinderWiringTests` constructs `new SidebarProjectionBinder(logs, peek: null)` and drops any
source-table assertion. The file header's "and the service its store through `Sidebar.UseStore`" becomes "and the service
its profile folder through `Sidebar.UseProfileDir`"; its `SidebarCanvasRuleTests` line goes; the `SidebarBinderWiringTests`
line's "G-176 — Sidebar.Shutdown lands the last edit" becomes "G-176 — Sidebar.Shutdown lands the last pin in the account
file". `Shutdown_writes_the_last_edit_before_it_returns` (:493-518) is replaced by:

```csharp
    [Fact]
    public void Shutdown_writes_the_last_pin_to_the_account_file_before_it_returns()
    {
        // Signed in, so the account file exists (a signed-out scope keeps no pins on disk). The profile folder is the
        // fact's temp folder: the real %LOCALAPPDATA%\Wavee\WaveeMusic is never read or written.
        Entities.Boot(new CatalogScope("spotify", "wiring", "en-US", "US", 0, true));
        string dir = Path.Combine(_dir, "WaveeMusic");
        Sidebar.UseProfileDir(dir);
        Sidebar.Boot();
        try
        {
            Assert.Equal("spotify:wiring", Sidebar.AccountKey);
            Assert.True(Sidebar.Pin(new SidebarPin("pl:spotify:playlist:keep", SidebarEntryKind.Playlist,
                "spotify:playlist:keep", "Keep", AddedAtMs: 1)));

            // The pin armed the 300 ms coalesced commit; Shutdown fires it and waits for the pool write.
            Assert.True(Sidebar.Shutdown(10_000));

            string file = Path.Combine(dir, SidebarAccountStore.FileNameOf("spotify:wiring"));
            Assert.True(SidebarAccountStore.TryParse(File.ReadAllBytes(file), "spotify:wiring", out var data));
            Assert.Contains(data.Pins, p => p.Id == "pl:spotify:playlist:keep");
            Assert.True(File.Exists(SidebarStoreV3.PathUnder(dir)));        // the device file landed beside it
        }
        finally
        {
            // Signed out, an empty profile, a re-Boot: Boot reloads the account, so no later fact inherits this pin, and
            // nothing is committed to do it.
            TestScope.Fresh();
            Sidebar.UseProfileDir(Path.Combine(_dir, "reset"));
            Sidebar.Boot();
        }
    }

    [Fact]
    public void Boot_reads_and_writes_only_the_profile_folder_it_was_given()
    {
        TestScope.Fresh();
        string dir = Path.Combine(_dir, "WaveeMusic");
        Sidebar.UseProfileDir(dir);
        Sidebar.Boot();
        Assert.Equal(SidebarOpReject.None, Sidebar.Dispatch(new SetSectionShown(SidebarLayoutId.Classic, "recent", true)));
        Assert.True(Sidebar.Shutdown(10_000));
        Assert.True(File.Exists(SidebarStoreV3.PathUnder(dir)));
        Assert.Equal("", Sidebar.AccountKey);                               // CatalogScope.Fake() is signed out
        Assert.Empty(Directory.GetFiles(dir, "sidebar.acct-*.json"));       // … so no account file
    }
```

(`SetSectionShown` is §P3.2's op; Recent is hidden by default, so showing it is a change and commits the device file.)

**`BootOrderingTests.cs`** (P3-WP22). CHAIN 2 is gone with `Sidebar.ConcertsFetch` (§P3.6.1: the Concerts section is
deleted; `Artist.InstallPages` stays eager for its own pages, nothing in the sidebar reads it). Edits:

- the header comment: delete the `CHAIN 2` paragraph (keep the `CHAIN 1` / `CHAIN 3` names; nothing renumbers); in the
  `CHAIN 3` paragraph replace "the pin bridge needs the pin store Boot() loads" with "the pin bridge needs the pin store
  and the account Boot() loads"; in "GLOBAL STATE, KEPT LOCAL" drop `Sidebar.ConcertsFetch,` from the seam list and add
  "Sidebar's profile folder is pointed at the fact's temp folder (`Sidebar.UseProfileDir`), so the real profile is never
  touched".
- delete `SidebarLayoutStore FreshSidebarStore() => new(Path.Combine(_dir, "sidebar-layout.json"));`.
- delete `Artist_InstallPages_sets_the_sidebar_concerts_seam_the_pane_reads_at_its_first_mount` and its `CHAIN 2` banner.
- `Sidebar_boot_then_action_seams_then_library_install_wires_a_working_pin_bridge` becomes:

```csharp
    [Fact]
    public void Sidebar_boot_then_action_seams_then_library_install_wires_a_working_pin_bridge()
    {
        TestScope.Fresh();
        Sidebar.UseProfileDir(Path.Combine(_dir, "WaveeMusic"));

        Sidebar.Boot();                 // the layout, the device file and the live account's pins
        Sidebar.InstallActionSeams();   // Actions.Services.IsPinned / SetPinned
        Spotify.Library.Install();      // "after Sidebar.Boot, before the pane mounts" — the pin bridge + the seam

        // Spotify.Library.Install's own contract: it hands the sidebar its ONE library-write seam.
        Assert.Same(Spotify.Library.Writes, Sidebar.LibraryWrites);

        // InstallActionSeams's two seams only answer correctly because Sidebar.Boot already loaded a live (here,
        // empty) pin store for them to read — a fresh route starts unpinned...
        var search = new Shell.Route(Shell.RouteKind.Search);
        Assert.NotNull(Actions.Services.IsPinned);
        Assert.False(Actions.Services.IsPinned!(search));

        // ...and the SAME seam reflects a pin the moment the store it depends on carries one, proving the seam reads
        // Sidebar's LIVE state rather than a snapshot taken before Boot() ran.
        Assert.True(Sidebar.Pin(new SidebarPin("search", SidebarEntryKind.AppRoute, "", "Search", AddedAtMs: 1)));
        Assert.True(Actions.Services.IsPinned!(search));

        // Liked Songs has a fixed home and is never a pin (Q1a): the store refuses it whoever asks.
        Assert.False(Sidebar.Pin(new SidebarPin("liked", SidebarEntryKind.AppRoute, "", "Liked Songs", AddedAtMs: 1)));
        Assert.False(Actions.Services.IsPinned!(new Shell.Route(Shell.RouteKind.Liked)));

        // Land the device file now, while the temp folder still exists — Dispose deletes it right after this fact.
        Sidebar.Shutdown();
    }
```

**`SidebarPinTests.cs`** — `PinnableRoutes` facts drop `home`/`liked`; `PinRowRule.Decide(true,
"liked", false)` is `None`; and the unpinnable-drop refusal (§P3.9):

```csharp
    [Fact]
    public void RefusalKeyOf_Unpinnables_NamedReason_PinnablesNull()
    {
        Assert.Equal("sidebar.pin.cantPin.track", SidebarPinRules.RefusalKeyOf(DragKind.Track, "t1", "spotify:track:1"));
        Assert.Equal("sidebar.pin.cantPin.local", SidebarPinRules.RefusalKeyOf(DragKind.Track, "l1", "wavee:local:track:1"));
        Assert.Equal("sidebar.pin.cantPin.episode", SidebarPinRules.RefusalKeyOf(DragKind.Episode, "e1", "spotify:episode:1"));
        Assert.Equal("sidebar.pin.cantPin.home", SidebarPinRules.RefusalKeyOf(DragKind.Route, "home", ""));
        Assert.Equal("sidebar.pin.cantPin.liked", SidebarPinRules.RefusalKeyOf(DragKind.Route, "liked", ""));
        Assert.Null(SidebarPinRules.RefusalKeyOf(DragKind.Route, "search", ""));
        Assert.Null(SidebarPinRules.RefusalKeyOf(DragKind.Playlist, "pl:a", "spotify:playlist:a"));
        Assert.Null(SidebarPinRules.RefusalKeyOf(DragKind.Folder, "folder:f", "spotify:user:u:folder:f"));
        Assert.Null(SidebarPinRules.RefusalKeyOf(DragKind.Album, "album:a", "spotify:album:a"));
    }
```

(That the five `sidebar.pin.cantPin.*` keys exist in en-US is pinned in P4 by `SidebarMenuModelTests.PinRefusalKeys_Resolve`,
which reuses that class's `FindLocDir`/`Has` helpers, §P4.10.) **`SidebarCardsTests.cs`** — delete the hero facts. **`SidebarDesignTests.cs` →
`SidebarLayoutInfoTests.cs`** — keep only the `LibraryV3Metrics`/`LibraryV3Labels`/`LibraryV3SearchRules`/
`LibraryV3ChipStrip`/`LibraryV3View`/`LibraryV3Window` facts (the chrome helpers live until P5; P5 deletes this file).
**`PlatformWave6Tests.cs:40-77`, `SetupTests.cs:148`, `PlatformTests.cs:269`** — the `SidebarDesign` key facts become
`SidebarLayoutId` key facts (default 0 ⇒ Classic). **`ShellRoutesTests.cs`** — drop `SidebarCustomize`.
**`DiagnosticsCoreTests.cs`** — snapshot facts use `Layout`.

Deleted test files: `SidebarReducerTests.cs`, `SidebarCustomizerTests.cs`, `SidebarCustomizerBannersTests.cs`,
`SidebarCustomizerPicksTests.cs`, `SidebarMiniaturePlanTests.cs`, `SidebarPropertyRowsTests.cs`, `SidebarDocTests.cs`,
`SidebarConfigFieldRulesTests.cs`, `SidebarDataSourceTableOrderTests.cs`, `SidebarChoiceTreatmentTests.cs`,
`SidebarRejectTextTests.cs`, `SidebarSourcesTests.cs`, `SidebarActionBindingsTests.cs`,
`SidebarActionBindingInverseTests.cs` (both only tested the deleted `SidebarActionBinding` wire record).
`SidebarRevisionTests.cs` STAYS (`SidebarRevisionGate` survives).

### P3 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P3-WP1 | new `Shell\Sidebar.Layout.cs` | §P3.1, §P3.2, §P3.3, §P3.4 (all types in those sections, incl. `SidebarPinStateRules.FallbackTitleKey` from §P3.12 and `ShowsLiked(…, bool drilled = false)`) | — |
| P3-WP2 | new `Shell\Sidebar.Planner.cs` | §P3.5 planner + `SidebarPlanOptions` (with `FoldersInline` and `Drilled`; `PlanLibrary` skips the pin loop, the drop band and the Liked row while `Drilled`, appends an expanded pinned folder's children and plans NO Empty row) | P3-WP1, P3-WP3 |
| P3-WP3 | `Shell\Sidebar.cs` | §P3.5 (row kinds, input, extents, resolve, `Plan` wrapper), §P3.4 `IsAudiobook` + stamp, §P3.6.2 (`SidebarLibraryQuery`, the whole `Shape` method with the widened `narrow` gate, `Project` without `customOrder`, sort, kinds), §P3.9 (`FromRoute` refusal, `PinSyncRules`, `PinRowRule`; `LikedWireUri` deleted), §P3.6.2 + §P3.14 deletions, P1 geometry leftovers (`ShapeFor`, card heights), `SidebarPaneBounds.DefaultWidth`, frame snapshot `Layout`, `SidebarPlayedContext` removed | P3-WP1 |
| P3-WP4 | new `Shell\Sidebar.Feeds.cs` | §P3.6.1 (header includes `using FluentGpu.Foundation;`) | P3-WP1, P3-WP3 |
| P3-WP5 | new `Shell\Sidebar.Store.cs` | §P3.7 (using header `System.Text.Json` + `System.Text.Json.Serialization`; `SidebarSaveFault` moved here without `ConfigTooLarge`, the `Fault`/`FaultName`/`Commit` bodies) | P3-WP1 |
| P3-WP6 | new `Shell\Sidebar.Accounts.cs` | §P3.8 (the `Sidebar` partial with `using System.Globalization;`; `EnsureAccount` returns bool; the swap clears `LibrarySearchOpen`) | P3-WP1, P3-WP5 |
| P3-WP7 | new `Shell\Sidebar.Store.V2.cs`, new `Shell\Sidebar.Migration.cs`, delete `Shell\Sidebar.Doc.cs`, delete `Shell\Sidebar.Customizer.UI.cs` | §P3.10 (the two files: Store.V2 copies `Sidebar.Doc.cs`'s usings; Migration has `using System.Globalization; using FluentGpu.Localization;`), §P3.14 (the two deleted files) | P3-WP1, P3-WP5 |
| P3-WP8 | `Shell\Sidebar.Host.cs` | §P3.11 (service incl. `UseProfileDir` + the re-Boot account reset, bridge, `SwitchLayout` clears `LibrarySearchOpen`), §P3.6.2 (binder; deletes `SidebarSaveFault` here and `SidebarPersistenceFault.ConfigTooLarge`), §P3.9 (pin store refusal), the recency fold title, deletions | P3-WP1, P3-WP3, P3-WP4, P3-WP5, P3-WP6, P3-WP7 |
| P3-WP9 | `Shell\Sidebar.Rules.cs` | §P3.9 `SidebarPinRules` (`IsFixedRoute` and `RefusalKeyOf(DragKind, id, uri)`); `MenuLabel` moved in verbatim | — |
| P3-WP10 | `Shell\Sidebar.Modes.cs` | §P3.14 Modes deletions; §P3.12 `LibraryV3Metrics.FoldersApply` + `ClampColumns` | P3-WP1 |
| P3-WP11 | `Shell\Sidebar.UI.cs`, `Shell\Sidebar.UI.Flyout.cs`, `Shell\Sidebar.UI.Footer.cs` | §P3.12 (config, host, Classic mode, PaneView, pump, `PlanDep` folds `FoldersInline` and `Drilled`; "Retyped leftovers": `PaneMetrics`' five `SidebarSectionSpec` members + `EditCardHeight` deleted, `PaneFolderFlyout.Row(…, SidebarSection? section, …)` with the 4-arg `EntryMenu` / retyped `FolderMenu`), §P3.8 account effect, §P3.10 toast; never the disclosure members (§1) | P3-WP1, P3-WP2, P3-WP3, P3-WP8 |
| P3-WP12 | `Shell\Sidebar.UI.Slot.cs`, `Shell\Sidebar.UI.Rows.cs` | §P3.12 slot + rows (`RowSpec.Ink`; a nameless pin's kind-noun title in `TextTertiary`; `EmptyRow` Playlists only; `DropBandRow`; `PinDropZone` deleted) | P3-WP1, P3-WP3 |
| P3-WP13 | `Shell\Sidebar.UI.Menus.cs`, `Shell\Sidebar.UI.Drop.cs`, `Shell\Sidebar.Cards.cs`, `Platform\Surface.Rules.cs`, `Platform\Drag.cs` | §P3.12 menus (the five exact signatures, `NavExtras` without Remove, the `TreeMoves` gate, `LayoutMenu.Rows` and `Sidebar.LayoutName` on the `Sidebar` partial OUTSIDE `LayoutMenu`), drop, cards (Hero deleted; `SidebarCards.Tile(PaneView o, SidebarSection section, …)` with the shape-gated subtitle), `Drag.LivePinnable`, §P3.9 seams and the unpinnable-drop refusal in `ResourceDropSpec` (`PinRefusal` → `Compatible`/`Transparent`/`WhyRefused`) | P3-WP1, P3-WP3, P3-WP9 |
| P3-WP14 | `Shell\Sidebar.UI.LibraryV3.cs` | §P3.12 "`Sidebar.UI.LibraryV3.cs` (P3-WP14)" (config, `ReadState`, `ComputeColumns`, `ShapeInput` (keeps `ExpandedFolders`)/`ShapeOptions` (with `FoldersInline` and `Drilled = DrillActive`), the reorder block, `V3NavBand` with `s_destinations`, the four tiles + `LabelOf` deleted) | P3-WP1, P3-WP2, P3-WP8, P3-WP10 |
| P3-WP15 | `Spotify\Spotify.Encode.cs`, `Spotify\Spotify.Library.cs`, `Entities\Concert.Page.cs` | §P3.8 pin bridge (`LibraryPinSync` ctor/`ApplyServer`/`OnLocalPinChanged`, `PinWriteMarks` in Encode with the corrected `Rebind` comment, `s_pinWrites.Rebind(Sidebar.AccountKey)` in `AfterPublish`; `NotePinWrite` stays ABOVE `SettleCollection`'s scope check), §P3.9 `LikedPinUri` literal, `ConcertsFetch` removal | P3-WP3, P3-WP6 |
| P3-WP16 | `Shell\Shell.cs`, `Shell\Shell.UI.cs`, `Screens\Setup.cs`, `Screens\Diagnostics.Host.cs`, `Platform\Platform.cs` | §P3.13 shell/setup/diagnostics/keys | P3-WP1, P3-WP8 |
| P3-WP17 | `Screens\Settings.UI.Appearance.cs` | §P3.13 Settings card (`Sidebar.LayoutName(layout)` for the value tag and the card faces) | P3-WP1, P3-WP8, P3-WP13 |
| P3-WP18 | `assets\loc\en-US.json`, `assets\loc\nl.json`, `assets\loc\ko-KR.json` | §P3.13 loc (incl. `sidebar.pin.folder` and `sidebar.pin.cantPin.{track,episode,local,home,liked}`) | — |
| P3-WP19 | new `SidebarCatalogueTests.cs`, `SidebarLayoutRulesTests.cs`, `SidebarVisibilityRulesTests.cs` | §P3.15 (incl. `PinState_FallbackTitle_KindNoun_NeverBlank`; `Filters_Matches_SplitsPodcastsAndAudiobooks` reads `SidebarLibraryEntry.IsAudiobook` from P3-WP3) | P3-WP1, P3-WP3 |
| P3-WP20 | `SidebarPlannerTests.cs` | §P3.15 planner rewrite (incl. facts 22 `Library_EmptyList_PlansNoEmptyRow`, 22a `Library_PinnedFolder_ExpandsInline` / `_NotInlineWhenDrilled`, 22b `Library_Drilled_NoPins_NoLiked` / `Library_Drilled_PinnedChild_Once`) | P3-WP1, P3-WP2, P3-WP3 |
| P3-WP21 | new `SidebarStoreV3Tests.cs`, `SidebarAccountStoreTests.cs`, `SidebarMigrationV2Tests.cs`, `SidebarFeedsTests.cs`, `SidebarFileStoreTests.cs`; delete `SidebarStoreTests.cs` | §P3.15 (`SidebarStoreV3Tests` round-trips `SidebarLayoutState` from P3-WP1; `SidebarAccountStoreTests.NoCrossAccountApplyServer` builds `LibraryPinSync(store, SidebarPinLatch, …)` from P3-WP15) | P3-WP1, P3-WP4, P3-WP5, P3-WP6, P3-WP7, P3-WP15 |
| P3-WP22 | `SidebarProjectionTests.cs`, `SidebarWiringTests.cs`, `SidebarPinSyncTests.cs`, `SidebarPinTests.cs`, `SidebarCardsTests.cs`, `SidebarDesignTests.cs` (→ `SidebarLayoutInfoTests.cs`), `PlatformWave6Tests.cs`, `SetupTests.cs`, `PlatformTests.cs`, `ShellRoutesTests.cs`, `DiagnosticsCoreTests.cs`, `BootOrderingTests.cs`; delete the 14 test files listed in §P3.15 | §P3.15 (incl. `Shape_PodcastsChip_ExcludesAudiobooks` / `Shape_HiddenKind_RemovedFromUnfilteredList` as written, the rewritten `SidebarWiringTests` boot facts, `BootOrderingTests` chains 1+3, the `PinWriteMarks` facts, `SidebarPinTests.RefusalKeyOf_Unpinnables_NamedReason_PinnablesNull`) | P3-WP1, P3-WP3, P3-WP4, P3-WP6, P3-WP7, P3-WP8, P3-WP9, P3-WP10, P3-WP13, P3-WP15, P3-WP16 |

---

## P4. Edit mode, menus, undo

Customization lives on the sidebar: the Outline (Edit mode), one global pane menu (the footer ⋯), section-scoped header
menus, row-menu additions, and one undo ring for layout and pin edits.

### P4.1 `SidebarEditRules` (new file `Shell/Sidebar.Edit.cs`, pure)

```csharp
// ── Shell/Sidebar.Edit.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the pure half of Edit mode, the menus and the undo ring: what the Outline shows and how it moves, what every menu
// offers in every state, and the 50-entry inverse ring
//
// Role: CORE · Spec: sidebar-rework-implementation.md §P4.1-§P4.3 · design C.3-C.5, Q3, Q6-Q8, Q14-Q17

namespace Wavee;

/// <summary>One row of the Outline (design C.3), as data.</summary>
public enum SidebarOutlineRowKind : byte
{
    /// <summary>Home: locked, outside the band, first.</summary>
    Home = 0,
    /// <summary>A section block's header: Show checkbox (or a lock), title, summary, grip when movable.</summary>
    Section = 1,
    /// <summary>An item of an item section (Collections pages; Library's Filters kinds): checkbox, glyph, title, grip.</summary>
    Item = 2,
    /// <summary>A pin (Q3): art/glyph, title, Unpin, grip.</summary>
    Pin = 3,
    /// <summary>"Show all 312 pins" — the fold above <see cref="SidebarEditRules.PinFoldAt"/>.</summary>
    ShowAllPins = 4,
    /// <summary>A data section's one-line summary or an empty section's hint.</summary>
    Hint = 5,
    /// <summary>The footer's Settings row with its Show checkbox (the Outline replaces the footer, Q7).</summary>
    Settings = 6,
}

public readonly record struct SidebarOutlineRow(
    SidebarOutlineRowKind Kind,
    string SectionId,
    string ItemId,
    bool Shown,
    bool Movable,
    /// <summary>The checkbox is disabled (a locked section shows a lock; a locked Pinned shows the reason).</summary>
    bool Locked,
    int Count);

public static class SidebarEditRules
{
    /// <summary>Pins beyond this many fold behind "Show all" (Q3).</summary>
    public const int PinFoldAt = 50;
    public const float SectionHeaderHeight = 40f;
    public const float RowHeight = 40f;
    public const float HintHeight = 44f;

    /// <summary>The Outline's rows for <paramref name="layout"/> in display order: Home; then the BAND (Classic: every
    /// movable section, each with its body; Library: Pinned then Filters — a fixed order, design Q2); then Your Library
    /// (Library, locked); then Settings.</summary>
    public static void Outline(SidebarLayoutState state, SidebarLayoutId layout, IReadOnlyList<SidebarPin> pins,
                               bool pinnedLocked, bool showAllPins, int playlistCount, List<SidebarOutlineRow> into)
    {
        into.Clear();
        var overlay = state.Of(layout);
        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Home, "home", "", true, false, true, 0));
        for (int i = 0; i < overlay.Sections.Count; i++)
        {
            var s = overlay.Sections[i];
            if (!SidebarCatalogue.TryKindOf(s.Id, out var kind)) continue;
            if (kind is SidebarSectionKind.Home or SidebarSectionKind.Settings) continue;
            bool movable = SidebarCatalogue.Movable(layout, kind);
            bool locked = !SidebarCatalogue.Hideable(kind) || (kind == SidebarSectionKind.Pinned && pinnedLocked && !s.Hidden);
            int count = kind switch
            {
                SidebarSectionKind.Pinned => pins.Count,
                SidebarSectionKind.Playlists => playlistCount,
                _ => SidebarCatalogue.ItemsOf(layout, kind).Count - s.HiddenList.Count,
            };
            into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Section, s.Id, "", !s.Hidden, movable, locked, count));
            switch (kind)
            {
                case SidebarSectionKind.Pinned:
                    int shown = showAllPins ? pins.Count : Math.Min(pins.Count, PinFoldAt);
                    for (int p = 0; p < shown; p++)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Pin, s.Id, pins[p].Id, true, true, false, 0));
                    if (pins.Count > shown)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.ShowAllPins, s.Id, "", true, false, false, pins.Count));
                    if (pins.Count == 0)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Hint, s.Id, "", true, false, false, 0));
                    break;
                case SidebarSectionKind.Collections:
                case SidebarSectionKind.Library:
                    var items = SidebarLayoutRules.EffectiveItemOrder(layout, kind, s);
                    for (int k = 0; k < items.Count; k++)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Item, s.Id, items[k], !Contains(s.HiddenList, items[k]),
                            SidebarCatalogue.ItemsMovable(kind), false, 0));
                    break;
                default:
                    into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Hint, s.Id, "", !s.Hidden, false, false, count));
                    break;
            }
        }
        var settings = overlay.Find("settings");
        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Settings, "settings", "", settings is { Hidden: false }, false, false, 0));
    }

    /// <summary>The SECTION band's sections (Classic: the movable ones in display order; Library: none — its sections keep
    /// a fixed order). Only the section band: the Pinned block's inner pins band exists in BOTH layouts (Q3, P.2a).</summary>
    public static void Band(LayoutOverlay overlay, List<string> into)
    {
        into.Clear();
        for (int i = 0; i < overlay.Sections.Count; i++)
            if (SidebarCatalogue.TryKindOf(overlay.Sections[i].Id, out var kind) && SidebarCatalogue.Movable(overlay.Layout, kind))
                into.Add(overlay.Sections[i].Id);
    }

    /// <summary>A band item's extent: its header plus its body rows (the Reorderable's variable-extent slot pitch).</summary>
    public static float ExtentOf(SidebarLayoutId layout, SectionState s, int pinCount, bool showAllPins)
    {
        if (!SidebarCatalogue.TryKindOf(s.Id, out var kind)) return SectionHeaderHeight;
        float body = kind switch
        {
            SidebarSectionKind.Pinned => pinCount == 0 ? HintHeight
                : RowHeight * (showAllPins ? pinCount : Math.Min(pinCount, PinFoldAt)) + (pinCount > PinFoldAt && !showAllPins ? RowHeight : 0f),
            SidebarSectionKind.Collections or SidebarSectionKind.Library => RowHeight * SidebarCatalogue.ItemsOf(layout, kind).Count,
            _ => HintHeight,
        };
        return SectionHeaderHeight + body;
    }

    /// <summary>A section header's one-line summary in the Outline (Caption, <c>TextTertiary</c>), as a loc key formatted
    /// with <c>("count", row.Count)</c> and <c>("total", SidebarCatalogue.ItemsOf(layout, kind).Count)</c>; null = no
    /// summary. Collections states its coupling (Q5): hiding its last page hides the section itself, and showing the
    /// section back restores its pages — so its summary says so where the user is about to uncheck the last one.</summary>
    public static string? SummaryKeyOf(SidebarSectionKind kind, int count) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.edit.pinsSummary",
        SidebarSectionKind.Playlists => "sidebar.edit.playlistsSummary",
        SidebarSectionKind.Collections => count <= 1 ? "sidebar.edit.collectionsLastSummary" : "sidebar.edit.collectionsSummary",
        _ => null,
    };

    /// <summary>Alt+↑/↓ (one step) and Alt+Shift+↑/↓ (to the edge) over <paramref name="count"/> slots.</summary>
    public static int MoveTarget(int slot, int count, int direction, bool toEdge)
    {
        if (count <= 0) return -1;
        if (toEdge) return direction < 0 ? 0 : count - 1;
        return Math.Clamp(slot + direction, 0, count - 1);
    }

    /// <summary>The spoken sentence's loc key for a reorder milestone ("Grabbed {name}, position {pos} of {count}").</summary>
    public static string AnnounceKey(ReorderAnnounceKind kind) => kind switch
    {
        ReorderAnnounceKind.Grab => "sidebar.edit.announceGrabbed",
        ReorderAnnounceKind.Move => "sidebar.edit.announceMoved",
        ReorderAnnounceKind.Drop => "sidebar.edit.announceDropped",
        _ => "sidebar.edit.announceCancelled",
    };

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}
```

(`ReorderAnnounceKind` — `Grab`, `Move`, `Drop`, `Cancel` — is the engine's enum in `FluentGpu.Controls/Reorderable.cs`;
this file adds `using FluentGpu.Controls;` for it only. Wavee.Tests already references that assembly through Wavee.)

### P4.2 `SidebarMenuModel` (same file)

Menus as data; the UI maps rows to `MenuFlyoutItem`s and actions to service calls.

```csharp
public enum SidebarMenuAction : byte
{
    None = 0,
    SwitchLayout, ResetLayout, ShowSection, SetDensity, EditSidebar, ResetEverything,
    SetSort, SetView, ToggleLiked, ToggleKind, SetLimit, ShowItem, Collapse, Expand, MoveUp, MoveDown, HideSection,
    UnpinAllShortcuts, Unpin, MovePinUp, MovePinDown, HideItem, MoveRootlistUp, MoveRootlistDown,
}

/// <summary>A rootlist row's one-step moves (from <c>PaneView.RootlistStepOf</c>: <c>TreeMoves</c>' sibling run and the marker
/// legality). <see cref="Shown"/> is false for a non-rootlist row, for a row inside a ≥2 selection, and in Your Library
/// unless Playlists · Custom order is on screen (the rootlist order is then not what the user sees).</summary>
public readonly record struct SidebarRootlistStep(bool Shown, bool CanMoveUp, bool CanMoveDown);

/// <summary>One menu row: a verb (or a submenu when <see cref="Children"/> is set, or a separator), its label key and
/// argument, its state, and — when disabled — the reason key the row shows as its tooltip.</summary>
public sealed record SidebarMenuRow(
    SidebarMenuAction Action,
    string LabelKey,
    string Arg = "",
    bool Enabled = true,
    bool Checked = false,
    bool Radio = false,
    string? ReasonKey = null,
    IReadOnlyList<SidebarMenuRow>? Children = null,
    bool Separator = false)
{
    public static readonly SidebarMenuRow Divider = new(SidebarMenuAction.None, "", Separator: true);
}

public static class SidebarMenuModel
{
    const string FinishEditing = "sidebar.pane.finishEditing";

    /// <summary>The global pane menu (design C.4): Layout ▸ · Show section ▸ · [Library: Hide pinned · Unpin all shortcuts]
    /// · Density ▸ · Edit sidebar… · ─ · Reset everything…. While editing, the layout radios and Reset everything are disabled
    /// with the reason (Q7); Density stays. Library has no Pinned header (P.2a), so its two Pinned verbs live HERE: "Hide
    /// pinned" (disabled with "Unpin {names} first" while route/module pins lock it) and "Unpin all shortcuts" (Q17, while
    /// such pins exist). <paramref name="lockingPinNames"/> = <see cref="SidebarVisibilityRules.LockingPins"/>.</summary>
    public static IReadOnlyList<SidebarMenuRow> Pane(SidebarLayoutId layout, SidebarLayoutState state, SidebarDensity density, bool editing,
                                                     IReadOnlyList<string> lockingPinNames)
    {
        var hidden = new List<SidebarMenuRow>();
        var overlay = state.Of(layout);
        for (int i = 0; i < overlay.Sections.Count; i++)
            if (overlay.Sections[i].Hidden)
                hidden.Add(new SidebarMenuRow(SidebarMenuAction.ShowSection, "sidebar.section.title." + overlay.Sections[i].Id, overlay.Sections[i].Id));
        var pinnedRows = new List<SidebarMenuRow>(2);
        if (layout == SidebarLayoutId.Library)
        {
            bool locked = lockingPinNames.Count > 0;
            if (overlay.Find("pinned") is { Hidden: false })
                pinnedRows.Add(new SidebarMenuRow(SidebarMenuAction.HideSection, "sidebar.menu.hidePinned", "pinned",
                    Enabled: !locked, ReasonKey: locked ? "sidebar.menu.unpinFirst" : null));
            if (locked)
                pinnedRows.Add(new SidebarMenuRow(SidebarMenuAction.UnpinAllShortcuts, "sidebar.menu.unpinShortcuts"));
        }
        return
        [
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.layout", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SwitchLayout, "sidebar.layoutName.classic", "classic",
                    Enabled: !editing, Checked: layout == SidebarLayoutId.Classic, Radio: true, ReasonKey: editing ? FinishEditing : null),
                new SidebarMenuRow(SidebarMenuAction.SwitchLayout, "sidebar.layoutName.library", "library",
                    Enabled: !editing, Checked: layout == SidebarLayoutId.Library, Radio: true, ReasonKey: editing ? FinishEditing : null),
                SidebarMenuRow.Divider,
                new SidebarMenuRow(SidebarMenuAction.ResetLayout, "sidebar.menu.resetLayout",
                    Enabled: SidebarLayoutRules.IsModified(overlay)),
            ]),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.showSection",
                Enabled: hidden.Count > 0, ReasonKey: hidden.Count == 0 ? "sidebar.menu.nothingHidden" : null, Children: hidden),
            .. pinnedRows,
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.density", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SetDensity, "sidebar.density.default", "default", Checked: density == SidebarDensity.Default, Radio: true),
                new SidebarMenuRow(SidebarMenuAction.SetDensity, "sidebar.density.compact", "compact", Checked: density == SidebarDensity.Compact, Radio: true),
            ]),
            new SidebarMenuRow(SidebarMenuAction.EditSidebar, "sidebar.menu.edit", Enabled: !editing),
            SidebarMenuRow.Divider,
            new SidebarMenuRow(SidebarMenuAction.ResetEverything, "sidebar.menu.resetEverything",
                Enabled: !editing, ReasonKey: editing ? FinishEditing : null),
        ];
    }

    /// <summary>A section header's ⋯ (design C.4, section-scoped). <paramref name="lockingPinNames"/> non-empty ⇒ Pinned
    /// cannot be hidden ("Unpin {names} first") and offers "Unpin all shortcuts" (Q17).</summary>
    public static IReadOnlyList<SidebarMenuRow> Header(SidebarLayoutId layout, SidebarLayoutState state, string sectionId,
                                                       IReadOnlyList<string> lockingPinNames)
    {
        var rows = new List<SidebarMenuRow>(8);
        var overlay = state.Of(layout);
        var s = overlay.Find(sectionId);
        if (s is null || !SidebarCatalogue.TryKindOf(sectionId, out var kind)) return rows;
        if (kind is SidebarSectionKind.Recent or SidebarSectionKind.NewReleases)
        {
            int limit = s.Limit ?? SidebarCatalogue.DefaultLimit(kind);
            var choices = new List<SidebarMenuRow>(3);
            foreach (int n in SidebarCatalogue.LimitChoices)
                choices.Add(new SidebarMenuRow(SidebarMenuAction.SetLimit, "sidebar.menu.showCount", n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Checked: limit == n, Radio: true));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.show", Children: choices));
        }
        if (kind == SidebarSectionKind.Collections && s.HiddenList.Count > 0)
        {
            var hidden = new List<SidebarMenuRow>(s.HiddenList.Count);
            for (int i = 0; i < s.HiddenList.Count; i++)
                hidden.Add(new SidebarMenuRow(SidebarMenuAction.ShowItem, "sidebar.item." + s.HiddenList[i], s.HiddenList[i]));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.showHidden", Children: hidden));
        }
        if (kind == SidebarSectionKind.Pinned && lockingPinNames.Count > 0)
            rows.Add(new SidebarMenuRow(SidebarMenuAction.UnpinAllShortcuts, "sidebar.menu.unpinShortcuts"));
        if (SidebarCatalogue.Collapsible(kind))
            rows.Add(new SidebarMenuRow(s.Collapsed ? SidebarMenuAction.Expand : SidebarMenuAction.Collapse,
                s.Collapsed ? "sidebar.menu.expand" : "sidebar.menu.collapse"));
        if (SidebarCatalogue.Movable(layout, kind))
        {
            var band = new List<string>(8);
            SidebarEditRules.Band(overlay, band);
            int slot = band.IndexOf(sectionId);
            rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveUp, "sidebar.menu.moveUp", Enabled: slot > 0));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveDown, "sidebar.menu.moveDown", Enabled: slot >= 0 && slot < band.Count - 1));
        }
        if (SidebarCatalogue.Hideable(kind))
        {
            bool locked = kind == SidebarSectionKind.Pinned && lockingPinNames.Count > 0;
            rows.Add(SidebarMenuRow.Divider);
            rows.Add(new SidebarMenuRow(SidebarMenuAction.HideSection, "sidebar.menu.hideSection", Enabled: !locked,
                ReasonKey: locked ? "sidebar.menu.unpinFirst" : null));
        }
        return rows;
    }

    /// <summary>Your Library's ⋯ (P5's toolbar): Sort ▸ · View ▸ · Filters ▸ · Show Liked Songs.</summary>
    public static IReadOnlyList<SidebarMenuRow> LibraryOptions(SidebarLayoutState state, SidebarLibraryFilter filter)
    {
        var lib = state.Library.Find("library") ?? SidebarCatalogue.DefaultState(SidebarSectionKind.Library);
        var sort = lib.Sort ?? SidebarLibrarySort.Recents;
        var view = lib.View ?? SidebarLibraryView.List;
        bool customAvailable = filter == SidebarLibraryFilter.Playlists;
        var sorts = new List<SidebarMenuRow>(5);
        for (int i = 0; i <= (int)SidebarLibrarySort.CustomOrder; i++)
        {
            var sv = (SidebarLibrarySort)i;
            bool custom = sv == SidebarLibrarySort.CustomOrder;
            sorts.Add(new SidebarMenuRow(SidebarMenuAction.SetSort, "sidebar.sort." + SidebarStoreV3.SortName(sv), SidebarStoreV3.SortName(sv),
                Enabled: !custom || customAvailable, Checked: sort == sv, Radio: true,
                ReasonKey: custom && !customAvailable ? "sidebar.sort.customOnlyPlaylists" : null));
        }
        var kinds = new List<SidebarMenuRow>(4);
        var items = SidebarCatalogue.ItemsOf(SidebarLayoutId.Library, SidebarSectionKind.Library);
        for (int i = 0; i < items.Count; i++)
            kinds.Add(new SidebarMenuRow(SidebarMenuAction.ToggleKind, "sidebar.item." + items[i], items[i],
                Checked: !Contains(lib.HiddenList, items[i])));
        return
        [
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.sort", Children: sorts),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.view", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SetView, "sidebar.view.list", "list", Checked: view == SidebarLibraryView.List, Radio: true),
                new SidebarMenuRow(SidebarMenuAction.SetView, "sidebar.view.grid", "grid", Checked: view == SidebarLibraryView.Grid, Radio: true),
            ]),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.filters", Children: kinds),
            new SidebarMenuRow(SidebarMenuAction.ToggleLiked, "sidebar.menu.showLiked", Checked: lib.ShowLiked ?? true),
        ];
    }

    /// <summary>The sidebar-specific rows a row's context menu adds (the entity verbs stay the action registry's): a pin
    /// → Move up/down + Unpin; a Collections page → Move up/down + Hide from sidebar; the Library's Liked row and the
    /// footer's Settings → Hide from sidebar; a rootlist row (Classic Playlists always, Your Library under Playlists ·
    /// Custom order) → Move up/down through the rootlist (design C.4), each verb absent at its end of the sibling run, as
    /// every rootlist verb is (<c>SidebarTreeNavLayout</c>). An unavailable pin offers Unpin only.</summary>
    public static IReadOnlyList<SidebarMenuRow> Item(SidebarSectionKind section, string key, int index, int count, bool unavailable,
                                                     SidebarRootlistStep rootlist = default)
    {
        var rows = new List<SidebarMenuRow>(4);
        switch (section)
        {
            case SidebarSectionKind.Pinned:
                if (!unavailable)
                {
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MovePinUp, "sidebar.menu.moveUp", key, Enabled: index > 0));
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MovePinDown, "sidebar.menu.moveDown", key, Enabled: index < count - 1));
                }
                rows.Add(new SidebarMenuRow(SidebarMenuAction.Unpin, "sidebar.pin.unpin", key));
                break;
            case SidebarSectionKind.Collections:
                rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveUp, "sidebar.menu.moveUp", key, Enabled: index > 0));
                rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveDown, "sidebar.menu.moveDown", key, Enabled: index < count - 1));
                rows.Add(new SidebarMenuRow(SidebarMenuAction.HideItem, "sidebar.menu.hideFromSidebar", key));
                break;
            case SidebarSectionKind.Library when key == SidebarCatalogue.LikedRoute:
            case SidebarSectionKind.Settings:
                rows.Add(new SidebarMenuRow(SidebarMenuAction.HideItem, "sidebar.menu.hideFromSidebar", key));
                break;
            case SidebarSectionKind.Playlists:
            case SidebarSectionKind.Library:
                if (rootlist.Shown && rootlist.CanMoveUp)
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveRootlistUp, "sidebar.menu.moveUp", key));
                if (rootlist.Shown && rootlist.CanMoveDown)
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveRootlistDown, "sidebar.menu.moveDown", key));
                break;
        }
        return rows;
    }

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}
```

(Every `"sidebar.section.title.<id>"` / `"sidebar.item.<id>"` key is a loc key added in §P4.9 — for EVERY catalogue id,
`home` and `settings` included: "Show section ▸" lists a hidden Settings (Q15), so that row's `LabelKey` is
`sidebar.section.title.settings` and must resolve. There is no UI-side id mapping: `SidebarMenus.LabelOf` falls through to
`Loc.Get(r.LabelKey)`, and `SidebarMenus.SectionTitle(id)` is `Loc.Get("sidebar.section.title." + id)` for the toasts and
the Outline. `SidebarMenuModelTests.AssertEveryLabelKeyResolves` (§P4.10) pins it.)

### P4.3 `SidebarUndoRing` (same file)

```csharp
public enum SidebarUndoKind : byte { Layout = 0, Density = 1, Pin = 2, Batch = 3 }
public enum SidebarPinChange : byte { Pinned = 0, Unpinned = 1, Moved = 2 }

/// <summary>One undoable edit (design C.5): a layout overlay pre/post image, a density pair, a pin change with what
/// its inverse needs — or a <see cref="SidebarUndoKind.Batch"/> of those (<see cref="Parts"/>) when ONE user action made
/// several changes (pin while Pinned is hidden, Reset everything, Unpin all shortcuts): one action, one entry, one toast,
/// one Undo. <see cref="Label"/> is the localized "Unpin Running" the toast says.</summary>
public sealed record SidebarUndoEntry(
    int Id,
    SidebarUndoKind Kind,
    SidebarLayoutId Layout,
    string Label,
    LayoutOverlay? Before = null,
    LayoutOverlay? After = null,
    SidebarDensity DensityBefore = SidebarDensity.Default,
    SidebarDensity DensityAfter = SidebarDensity.Default,
    SidebarPinChange PinChange = SidebarPinChange.Pinned,
    SidebarPin? Pin = null,
    int PinFrom = -1,
    int PinTo = -1,
    IReadOnlyList<SidebarUndoEntry>? Parts = null)
{
    /// <summary>One action's several changes as ONE entry. The parts are in the order they were APPLIED.</summary>
    public static SidebarUndoEntry Batch(SidebarLayoutId layout, string label, IReadOnlyList<SidebarUndoEntry> parts)
        => new(0, SidebarUndoKind.Batch, layout, label, Parts: parts);

    /// <summary>The leaf entries to apply, in order: an undo walks the parts BACKWARDS (the last change is reverted first,
    /// so an unpin batch re-inserts lowest index first), a redo FORWARDS. A leaf is itself. Nested batches flatten.</summary>
    public static void Flatten(SidebarUndoEntry entry, bool undo, List<SidebarUndoEntry> into)
    {
        if (entry.Kind != SidebarUndoKind.Batch || entry.Parts is not { } parts) { into.Add(entry); return; }
        if (undo) for (int i = parts.Count - 1; i >= 0; i--) Flatten(parts[i], true, into);
        else for (int i = 0; i < parts.Count; i++) Flatten(parts[i], false, into);
    }
}

/// <summary>THE ONE RING (design C.5): 50 inverse entries, memory only, layout AND pin edits (collapse toggles are never
/// recorded). Survives Done; cleared on a layout switch and an account switch only. Pure: the service applies the inverse.</summary>
public sealed class SidebarUndoRing
{
    public const int Capacity = 50;
    readonly List<SidebarUndoEntry> _undo = new(Capacity), _redo = new(Capacity);
    int _nextId = 1;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int Count => _undo.Count;
    public SidebarUndoEntry? Peek => _undo.Count > 0 ? _undo[^1] : null;

    /// <summary>Record an edit (assigns its id). Clears redo; drops the oldest beyond <see cref="Capacity"/>.</summary>
    public SidebarUndoEntry Push(SidebarUndoEntry entry)
    {
        var stamped = entry with { Id = _nextId++ };
        _undo.Add(stamped);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        return stamped;
    }

    public bool TryUndo(out SidebarUndoEntry entry)
    {
        if (_undo.Count == 0) { entry = null!; return false; }
        entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        return true;
    }

    public bool TryRedo(out SidebarUndoEntry entry)
    {
        if (_redo.Count == 0) { entry = null!; return false; }
        entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        return true;
    }

    /// <summary>A toast's Undo acts only on ITS entry and only while it is the newest one; otherwise it is a no-op.</summary>
    public bool IsTop(int id) => _undo.Count > 0 && _undo[^1].Id == id;

    /// <summary>An "Undid … · Redo" toast's Redo acts only while ITS entry is the newest redo (a later undo or any new edit
    /// makes it stale).</summary>
    public bool IsRedoTop(int id) => _redo.Count > 0 && _redo[^1].Id == id;

    public void Clear() { _undo.Clear(); _redo.Clear(); }

    /// <summary>Q6: Ctrl+Z / Ctrl+Y act while editing, or — outside Edit — only while a sidebar toast is open; never while a
    /// text editor has focus (its own undo stack wins). Called only from the sidebar's own key handlers (focused routing:
    /// <c>PaneView.OnPaneKey</c>, <c>EditPane.OnPaneKey</c>, §P4.6), so design V.11's "focus in the pane, the head or the
    /// edit bar" is structural, not a parameter: there is no frame-global chord.</summary>
    public static bool KeyAllowed(bool editing, bool sidebarToastOpen, bool textEditorFocused)
        => !textEditorFocused && (editing || sidebarToastOpen);

    /// <summary>Is this op recorded? Collapse toggles are not (design C.5).</summary>
    public static bool Records(SidebarOp op) => op is not SetSectionCollapsed;
}
```

### P4.4 The service: recording, undo, Edit mode, toasts (`Shell/Sidebar.Host.cs`, `Shell/Sidebar.Accounts.cs`, `Platform/Drag.cs`)

`Sidebar.Host.cs` adds `using FluentGpu.Controls;` (`Toast`, `ToastHandle`, `ToastOptions`, `InfoBarSeverity`) and `using
FluentGpu.Localization;` (`Loc`) to its using header — today it has only `FluentGpu.Foundation` and `FluentGpu.Signals`.

`Platform/Drag.cs`, beside `LiveRootlistDrag` (the same `GetDragState` read):

```csharp
    /// <summary>Is ANY drag live right now? <c>Sidebar.EnterEdit</c> refuses while one is (§P4.4).</summary>
    public static bool IsLive()
        => (FluentGpu.Hooks.InputHooks.Current.Default.GetDragState?.Invoke() ?? default).Active;
```

`Sidebar.Host.cs`, beside `Dispatch`:

```csharp
    // ── the undo ring (design C.5) ───────────────────────────────────────────────────────────────────────────────────

    static readonly SidebarUndoRing s_ring = new();
    static readonly Signal<int> s_ringVersion = new(0);
    static readonly Dictionary<int, ToastHandle> s_toasts = new();

    /// <summary>The ring (the edit bar's Undo/Redo read <see cref="RingVersion"/> to re-render).</summary>
    public static SidebarUndoRing Ring => s_ring;
    public static IReadSignal<int> RingVersion => s_ringVersion;

    /// <summary>Is a sidebar toast open (Q6: outside Edit, Ctrl+Z needs one)?</summary>
    public static bool SidebarToastOpen
    {
        get
        {
            foreach (var h in s_toasts.Values) if (h.IsOpen) return true;
            return false;
        }
    }

    static void Record(SidebarUndoEntry entry, string? toastText)
    {
        var e = s_ring.Push(entry);
        s_ringVersion.Value = s_ringVersion.Peek() + 1;
        // Outside Edit every STRUCTURAL change toasts with Undo (hide, unpin, reset); moves never do (Q14).
        if (toastText is not null && !Editing.Peek()) ShowUndoToast(e.Id, toastText);
    }

    static void ShowUndoToast(int entryId, string text)
        => ShowRingToast(entryId, text, "sidebar.undo.undo", () => { if (s_ring.IsTop(entryId)) Undo(); }, 6000f);

    /// <summary>EVERY ring toast — an edit's "… · Undo", an undo's "Undid … · Redo", a redo's "Redid … · Undo" — is
    /// registered under its entry id, so <see cref="SidebarToastOpen"/> (Q6: Ctrl+Z/Y outside Edit need an open sidebar
    /// toast) sees it and <see cref="ClearRing"/> closes it. A newer toast for the same entry replaces the older one.</summary>
    static void ShowRingToast(int entryId, string text, string actionKey, Action action, float durationMs)
    {
        if (s_toasts.Remove(entryId, out var previous) && previous.IsOpen) previous.Close();
        PruneClosedToasts();
        s_toasts[entryId] = Toast.Show(text, new ToastOptions
        {
            ActionLabel = Loc.Get(actionKey), OnAction = action, DurationMs = durationMs,
        });
    }

    static readonly List<int> s_closedToasts = new(4);

    /// <summary>Forget the handles whose toast already closed: the map holds the open ones.</summary>
    static void PruneClosedToasts()
    {
        s_closedToasts.Clear();
        foreach (var (id, h) in s_toasts) if (!h.IsOpen) s_closedToasts.Add(id);
        for (int i = 0; i < s_closedToasts.Count; i++) s_toasts.Remove(s_closedToasts[i]);
    }

    static void ClearRing()
    {
        s_ring.Clear();
        foreach (var h in s_toasts.Values) if (h.IsOpen) h.Close();
        s_toasts.Clear();
        s_ringVersion.Value = s_ringVersion.Peek() + 1;
    }

    public static void Undo()
    {
        if (!s_ring.TryUndo(out var e)) return;
        ApplyInverse(e, undo: true);
        s_ringVersion.Value = s_ringVersion.Peek() + 1;
        // Q6: every undo says what it undid, with Redo — an invisible, synced undo is the surprise this ring must not cause.
        int id = e.Id;
        if (!Editing.Peek())
            ShowRingToast(id, Loc.Format("sidebar.undo.undid", ("what", e.Label)), "sidebar.undo.redo",
                () => { if (s_ring.IsRedoTop(id)) Redo(); }, 5000f);
    }

    public static void Redo()
    {
        if (!s_ring.TryRedo(out var e)) return;
        ApplyInverse(e, undo: false);
        s_ringVersion.Value = s_ringVersion.Peek() + 1;
        // Q6: a redo is announced too, with Undo — the edit is live again and synced.
        int id = e.Id;
        if (!Editing.Peek())
            ShowRingToast(id, Loc.Format("sidebar.undo.redid", ("what", e.Label)), "sidebar.undo.undo",
                () => { if (s_ring.IsTop(id)) Undo(); }, 5000f);
    }

    static readonly List<SidebarUndoEntry> s_leaves = new(8);

    /// <summary>Apply an entry backwards (undo) or forwards (redo) WITHOUT recording — a batch as its leaves, in
    /// <see cref="SidebarUndoEntry.Flatten"/>'s order. Pin inverses re-enter the store's user-intent path (Pin / InsertPin /
    /// Unpin), so <c>OnLocalPinChanged</c> syncs them to Spotify like any edit.</summary>
    static void ApplyInverse(SidebarUndoEntry e, bool undo)
    {
        s_leaves.Clear();
        SidebarUndoEntry.Flatten(e, undo, s_leaves);
        for (int i = 0; i < s_leaves.Count; i++) ApplyLeaf(s_leaves[i], undo);
        s_leaves.Clear();
    }

    static void ApplyLeaf(SidebarUndoEntry e, bool undo)
    {
        switch (e.Kind)
        {
            case SidebarUndoKind.Layout when (undo ? e.Before : e.After) is { } overlay:
                ApplyUnrecorded(new ReplaceOverlay(overlay));
                break;
            case SidebarUndoKind.Density:
                SetDensityUnrecorded(undo ? e.DensityBefore : e.DensityAfter);
                break;
            case SidebarUndoKind.Pin when e.Pin is { } pin:
                switch (e.PinChange)
                {
                    case SidebarPinChange.Pinned when undo: Pins.Unpin(pin.Id); break;
                    case SidebarPinChange.Pinned: Pins.Insert(pin, e.PinFrom); break;
                    case SidebarPinChange.Unpinned when undo: Pins.Insert(pin, e.PinFrom); break;
                    case SidebarPinChange.Unpinned: Pins.Unpin(pin.Id); break;
                    case SidebarPinChange.Moved: Pins.Move(undo ? e.PinTo : e.PinFrom, undo ? e.PinFrom : e.PinTo); break;
                }
                break;
        }
    }
```

`Dispatch(op)` becomes `Dispatch(SidebarOp op, string? toastText = null)`: after a successful apply, when
`SidebarUndoRing.Records(op)`, it records `new SidebarUndoEntry(0, SidebarUndoKind.Layout, op.Layout, LabelOf(op),
Before: beforeOverlay, After: s_state.Of(op.Layout))` with `toastText`. `ApplyUnrecorded(op)` is the old `Dispatch` body
without the record. `LabelOf(op)` composes the label from loc keys (`sidebar.undo.label.hide` "Hide {name}",
`.show`, `.move`, `.reset`, `.limit`, `.sort`, `.view`, `.liked`) with the section/item title. `SetDensity(d)` records a
`Density` entry (no toast); `SetDensityUnrecorded` is its old body. `SwitchLayout` calls `ClearRing()` (design C.5).
`Sidebar.Accounts.cs`'s `SwapAccount`: its `Editing.SetIfChanged(false);` line becomes `ExitEditCore(restoreFocus: false);`
(an account switch never moves focus, below) and `AccountSwapped?.Invoke();` becomes `ClearRing();` (delete the
`AccountSwapped` hook).

**One action, one entry, one toast.** Every user action records exactly ONE ring entry and raises at most ONE toast; an
action that changes several things records a `Batch` (§P4.3), so the toast's `IsTop(entryId)` check always names the
entry the toast announced.

Pin edits record through three helpers the UI calls instead of the store directly:

```csharp
    /// <summary>Pin with undo. Q4: pinning while Pinned is hidden shows Pinned again — ONE batch (the pin, then the show) under
    /// ONE toast "Pinned is shown again · Undo", whose Undo unpins AND re-hides.</summary>
    public static bool PinRecorded(SidebarPin pin, string name)
    {
        var layout = Layout.Peek();
        bool reshow = State.Of(layout).Find("pinned") is { Hidden: true };
        if (!Pins.Pin(pin)) return false;
        int at = Pins.IndexOf(pin.Id);
        string label = Loc.Format("sidebar.undo.label.pin", ("name", name));
        var pinned = new SidebarUndoEntry(0, SidebarUndoKind.Pin, layout, label,
            PinChange: SidebarPinChange.Pinned, Pin: Pins[at], PinFrom: at);
        if (!reshow)
        {
            Record(pinned, Loc.Format("sidebar.pin.pinnedNamed", ("name", name)));
            return true;
        }
        var before = State.Of(layout);
        ApplyUnrecorded(new SetSectionShown(layout, "pinned", true));
        var shown = new SidebarUndoEntry(0, SidebarUndoKind.Layout, layout, label, Before: before, After: State.Of(layout));
        Record(SidebarUndoEntry.Batch(layout, label, [pinned, shown]), Loc.Get("sidebar.pin.pinnedShownAgain"));
        return true;
    }

    /// <summary>"Unpin all shortcuts" (Q17): every route/module pin, as ONE batch under ONE toast "Shortcuts unpinned · Undo".
    /// Unpinned from the LAST index down, so each part's <c>PinFrom</c> is still right when the undo re-inserts them in
    /// reverse (lowest index first).</summary>
    public static void UnpinAllShortcutsRecorded()
    {
        var layout = Layout.Peek();
        var parts = new List<SidebarUndoEntry>(4);
        for (int i = Pins.Count - 1; i >= 0; i--)
        {
            var pin = Pins[i];
            if (pin.Kind != SidebarEntryKind.AppRoute) continue;
            Pins.Unpin(pin.Id);
            parts.Add(new SidebarUndoEntry(0, SidebarUndoKind.Pin, layout, pin.Name,
                PinChange: SidebarPinChange.Unpinned, Pin: pin, PinFrom: i));
        }
        if (parts.Count == 0) return;
        Record(SidebarUndoEntry.Batch(layout, Loc.Get("sidebar.undo.label.unpinShortcuts"), parts), Loc.Get("sidebar.toast.shortcutsUnpinned"));
    }

    /// <summary>"Reset everything" (design C.3): both layouts and the density back to their defaults (the width too, which
    /// is a preference, not a ring edit) — ONE batch under ONE toast "Sidebar reset · Undo". Pins untouched.</summary>
    public static void ResetEverythingRecorded()
    {
        var classicBefore = State.Of(SidebarLayoutId.Classic);
        var libraryBefore = State.Of(SidebarLayoutId.Library);
        var densityBefore = Density.Peek();
        ApplyUnrecorded(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic)));
        ApplyUnrecorded(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library)));
        SetDensityUnrecorded(SidebarDensity.Default);
        SetExpandedWidth(SidebarPaneBounds.DefaultWidth);
        string label = Loc.Get("sidebar.undo.label.resetEverything");
        Record(SidebarUndoEntry.Batch(Layout.Peek(), label,
        [
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Classic, label, Before: classicBefore, After: State.Of(SidebarLayoutId.Classic)),
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Library, label, Before: libraryBefore, After: State.Of(SidebarLayoutId.Library)),
            new SidebarUndoEntry(0, SidebarUndoKind.Density, Layout.Peek(), label, DensityBefore: densityBefore, DensityAfter: SidebarDensity.Default),
        ]), Loc.Get("sidebar.toast.resetEverything"));
    }

    public static bool UnpinRecorded(string pinId, string name)
    {
        int at = Pins.IndexOf(pinId);
        if (at < 0) return false;
        var pin = Pins[at];
        Pins.Unpin(pinId);
        Record(new SidebarUndoEntry(0, SidebarUndoKind.Pin, Layout.Peek(), Loc.Format("sidebar.undo.label.unpin", ("name", name)),
            PinChange: SidebarPinChange.Unpinned, Pin: pin, PinFrom: at), Loc.Format("sidebar.pin.unpinnedNamed", ("name", name)));
        return true;
    }

    /// <summary>Move a pin (a band drop, Alt+↑/↓, Move up/down). Recorded, never toasted (Q14).</summary>
    public static void MovePinRecorded(int from, int to)
    {
        if (from == to || (uint)from >= (uint)Pins.Count) return;
        var pin = Pins[from];
        Pins.Move(from, to);
        Record(new SidebarUndoEntry(0, SidebarUndoKind.Pin, Layout.Peek(), Loc.Format("sidebar.undo.label.move", ("name", pin.Name)),
            PinChange: SidebarPinChange.Moved, Pin: pin, PinFrom: from, PinTo: to), null);
    }
```

`PaneView.PinWithToast` / `UnpinWithToast` (`Sidebar.UI.Drop.cs`) become thin calls to `PinRecorded` / `UnpinRecorded`
(their own toasts are deleted — the ring's toasts replace them, design C.5; P4-WP5). Every pin reorder in the pane goes
through ONE commit, `DefaultReorderCommit` (`Sidebar.UI.cs`, P4-WP4): the Classic Pinned band's drop, its keyboard lift
and the Library session's `CommitReorder` (which calls it, then `Resync()`) all land here, so recording it covers them all
(design C.5, Q14: the ring covers moves; moves never toast):

```csharp
    /// <summary>The shared commit: Pinned through the pin store (mapped by pin id — a band position can drift from it),
    /// RECORDED in the undo ring (design C.5 / Q14; never toasted).</summary>
    internal static void DefaultReorderCommit(in PaneReorder r)
    {
        if (r.FromSlot == r.ToSlot || r.Section.Kind != SidebarSectionKind.Pinned) return;
        int pf = Pins.IndexOf(r.KeyAt(r.FromSlot)), pt = Pins.IndexOf(r.KeyAt(r.ToSlot));
        if (pf < 0 || pt < 0) MovePinRecorded(r.FromSlot, r.ToSlot);
        else MovePinRecorded(pf, pt);
    }
```

After P4 nothing in the app calls the unrecorded `Sidebar.MovePin` but the ring's own inverse (`Pins.Move` in
`ApplyLeaf`); `MovePin` stays as the store verb.

**Edit mode** (`Sidebar.Host.cs`):

```csharp
    /// <summary>Enter Edit mode (design C.3): the pane presents Expanded (the presentation effect reads
    /// <see cref="Editing"/>); <c>userCollapsed</c> is never written; the route does not change.
    /// <para>Design corner case "Entering Edit cancels a live drag": the palette's chord CAN run while the pointer holds a
    /// drag, and the app has no seam to cancel the engine's drag (<c>DragController.Cancel</c> is not exposed through
    /// <c>InputHooks</c>). So Edit is REFUSED while a drag is live — the toast "Finish dragging first", nothing changes —
    /// and the user drops (or presses Esc) and enters again. A drag can never straddle the Outline swap.</para></summary>
    public static void EnterEdit()
    {
        if (Editing.Peek()) return;
        if (Drag.IsLive())
        {
            Notify.Say(Loc.Get("sidebar.edit.finishDrag"), InfoBarSeverity.Informational, dedupeKey: "sidebar.edit.finish-drag");
            return;
        }
        Editing.Value = true;
    }

    /// <summary>Done / Esc: back to the sidebar, focus restored to the row that had it before Edit (§P4.5). The ring is
    /// KEPT (Q6: the edit bar's Undo after Done is the toast's).</summary>
    public static void ExitEdit() => ExitEditCore(restoreFocus: true);

    /// <param name="restoreFocus">False for an account switch: the user is not in the sidebar, so focus stays where it is
    /// and no "{name} is empty" toast is raised.</param>
    internal static void ExitEditCore(bool restoreFocus)
    {
        if (!Editing.Peek()) return;
        EditExitRestoresFocus = restoreFocus;
        Editing.Value = false;
        if (restoreFocus && s_enabledEmptySection is { } name)
            Toast.Show(Loc.Format("sidebar.edit.emptyOnDone", ("name", name)), new ToastOptions());
        s_enabledEmptySection = null;
    }

    /// <summary>How the LAST exit from Edit ended: the pane restores focus on the editing true → false edge only when this
    /// is true (Done / Esc), never for an account switch.</summary>
    internal static bool EditExitRestoresFocus { get; private set; }

    /// <summary>The name of a section the user showed in Edit while it had nothing to show (one toast on Done).</summary>
    internal static string? s_enabledEmptySection;
```

### P4.5 The Outline and the edit bar (new file `Shell/Sidebar.UI.Edit.cs`, a named partial of `Sidebar.UI.cs`)

**Copy the using header of `Sidebar.UI.cs`** into the new file (its usings verbatim — the nine `FluentGpu.*` namespaces
and `using static FluentGpu.Dsl.Ui;` — plus `using System.Globalization;`, then `namespace Wavee;`).

Component tree while `Sidebar.Editing` (the `PaneView` swaps its head + list + footer for this, keyed `editing`; the
disclosure and list state stay mounted underneath because the `PlanList` element is kept by reference — only the
visible child changes):

```
EditPane (Direction 1, Grow 1)
├─ EditBar            36 tall + margin 4,2,4,2 · FillLayerDefault · 1-px StrokeCardDefault · r4 · padding 8,0,4,0
│    [✎ 16 secondary] "Editing sidebar" (Caption 600 secondary) · spacer ·
│    [Undo 32×32 IconButton, disabled when !CanUndo] [Redo] [Reset ▾ subtle button] [Done accent button]
└─ ScrollEl (Grow 1, ScrollKey "sidebar.outline")
     └─ Column (Direction 1, Padding 4,3,4,8)  — keyed rows, never virtualized (≤ ~24 + pins)
          Home row          locked: Icons.Lock 12 tertiary in a 24 box · Icons.Home 16 · "Home"
          ── band: Reorderable("sidebar-section"), ExtentOf = extents, LiveProject = true,
          │        DragStyle = Stationary @ Drag.SourceDimOpacity, AnnounceText, RequireDropOnList = true
          │  SectionBlock(id)   = Reorderable.Item(slot, block, key: id) inside an outer BoxEl with OnKeyDown (Enter / Alt+arrows / Menu)
          │     header 40     [☑ CheckBox 24] [title 14/600] [summary Caption tertiary] spacer [⋯ 24] [GripperBar 16 tertiary]
          │     body          Pinned: pin rows (inner band "sidebar-item:pins") · Collections: item rows (inner band
          │                   "sidebar-item:collections") · Playlists/Recent/New releases: one Hint row (44)
          ── (Library: no SECTION band — Pinned and Filters keep a fixed order and their headers have no grip. The Pinned
          │   block's body is the SAME inner pins band "sidebar-item:pins" as Classic's: drag, Space lift, Alt+↑/↓, Unpin
          │   and the "Show all" fold, Q3 / P.2a)
          Your Library row  (Library layout) locked: lock · "Your Library" · Hint "Search, filters and everything you saved"
          Separator
          Settings row      [☑ Show] [Icons.Settings] "Settings" · Caption "Still in the profile menu" when hidden
          Footnote          Caption tertiary: "Drag sections by their header or grip. Changes apply at once." (Classic) /
                            "Library keeps a fixed order. Changes apply at once." (Library)
```

Metrics: section header 40; item/pin rows 40 (art 20 for pins, glyph 16 for items, checkbox column 32 at x 4); hint rows
44 (Caption at x 40); hidden sections and items render at opacity 0.5 with the checkbox cleared.

```csharp
public static partial class Sidebar
{
    /// <summary>EDIT MODE'S PANE (design C.3): the edit bar over the Outline. Modal for the sidebar only — the page behind
    /// stays fully interactive.</summary>
    internal sealed class EditPane(PaneView owner) : Component
    {
        readonly Signal<bool> _showAllPins = new(false);
        readonly List<SidebarOutlineRow> _rows = new(32);
        readonly List<string> _band = new(8);
        Reorderable? _sections, _pins, _items;

        public override Element Render()
        {
            var layout = Layout.Value;
            _ = LayoutVersion.Value;
            _ = PinsVersion.Value;
            _ = RingVersion.Value;
            bool showAll = _showAllPins.Value;
            var state = State;
            int playlists = Binder?.CurrentInput.PlaylistTree?.Count ?? 0;
            SidebarEditRules.Outline(state, layout, Pins.Items, PinnedLocked(), showAll, playlists, _rows);
            SidebarEditRules.Band(state.Of(layout), _band);
            UseEffect(() =>
            {
                // The section chip (the customizer's freed slot): "Playlists" + its kind glyph while a section travels.
                var previous = Drag.ExtraChip;
                Drag.ExtraChip = SectionChip;
                return () => { if (ReferenceEquals(Drag.ExtraChip, (Func<DragState, DragChipSpec?>)SectionChip)) Drag.ExtraChip = previous; };
            }, DepKey.Empty);

            return new BoxEl
            {
                Key = "edit-pane", Direction = 1, Grow = 1f,
                OnKeyDown = OnPaneKey,
                Children = [EditBar(), Outline(layout, state)],
            };
        }

        // … EditBar(), Outline(), SectionBlock(), PinRow(), ItemRow(), HintRow(), SettingsRow(), SectionChip() — below …
    }
}
```

`EditBar()`:

```csharp
        Element EditBar()
        {
            bool canUndo = Ring.CanUndo, canRedo = Ring.CanRedo;
            return new BoxEl
            {
                Key = "edit-bar", Direction = 0, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
                Margin = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 2f),
                Padding = new Edges4(8f, 0f, 4f, 0f), Corners = Radii.ControlAll,
                Fill = Tok.FillLayerDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    Icon(Icons.Edit, 14f, Tok.TextSecondary),
                    Ui.Caption(Loc.Get("sidebar.edit.title")) with { Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Grow = 1f },
                    ToolTip.Wrap(IconButton.Create(Icons.Undo, Undo, isEnabled: canUndo), Loc.Get("sidebar.undo.undo")),
                    ToolTip.Wrap(IconButton.Create(Icons.Redo, Redo, isEnabled: canRedo), Loc.Get("sidebar.undo.redo")),
                    Button.Create(Loc.Get("sidebar.edit.reset"), OpenResetMenu, ButtonAppearance.Subtle, ControlSize.Small),
                    Button.Accent(Loc.Get("sidebar.edit.done"), ExitEdit) with { Key = "edit-done" },
                ],
            };
        }
```

(`OpenResetMenu` opens a `MenuFlyout` anchored at the Reset button: "Reset this layout" (→ `Dispatch(new ResetLayout(…),
toastText: null)`; recorded) and "Reset everything…" (→ `SidebarMenus.ConfirmResetEverything(owner.MenuOverlay)`, §P4.6).
`ResetEverythingRecorded()` (§P4.4) records ONE batch entry — both layouts and the density — and toasts "Sidebar reset ·
Undo" outside Edit; that Undo reverts all three. P3.11's unrecorded `Sidebar.ResetEverything()` is deleted in P4 (its
only callers are this dialog and the Settings card's item, which both go through `ConfirmResetEverything`).)

`Outline(layout, state)` builds the column from `_rows` in order. The SECTION band (Classic only; the pins' inner band
below is built in both layouts):

```csharp
            _sections ??= new Reorderable("sidebar-section")
            {
                LiveProject = true,
                ShowInsertionLine = false,
                RequireDropOnList = true,
                DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = FluentGpu.Controls.Drag.SourceDimOpacity },
                AnnounceAssertive = true,
            };
            _sections.ItemCount = _band.Count;
            _sections.ExtentOf = i => SidebarEditRules.ExtentOf(layout, state.Of(layout).Find(_band[i])!, Pins.Count, _showAllPins.Peek());
            _sections.OnReorder = (from, to) => Dispatch(new MoveSection(Layout.Peek(), _band[from], to));
            _sections.AnnounceText = a => Loc.Format(SidebarEditRules.AnnounceKey(a.Kind),
                ("name", SectionTitle(_band[Math.Clamp(a.Index, 0, _band.Count - 1)])),
                ("position", (a.Slot + 1).ToString(CultureInfo.InvariantCulture)),
                ("count", a.Count.ToString(CultureInfo.InvariantCulture)));
```

(The same stationary lift `Entities/Queue.UI.cs:284` uses; the chip is the shell's DragPreviewLayer through
`Drag.ExtraChip`, below.)

Each band block:

```csharp
        Element SectionBlock(int slot, string sectionId, List<Element> body, SidebarOutlineRow header)
        {
            string id = sectionId;
            Element block = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [SectionHeaderRow(header), .. body] };
            Element item = _sections!.Item(slot, block, key: id, transition: LayoutTransition.Slide);
            // Keys that are not the Reorderable's (Space lift / arrows while lifted / Esc) bubble here (design Q8).
            return new BoxEl
            {
                Key = "block:" + id, Direction = 1,
                OnKeyDown = e => OnSectionKey(id, slot, e),
                Children = [item],
            };
        }

        void OnSectionKey(string id, int slot, KeyEventArgs e)
        {
            if (e.Handled) return;
            var layout = Layout.Peek();
            if (e.KeyCode == Keys.Enter && e.Mods == KeyModifiers.None)
            {
                var s = State.Of(layout).Find(id);
                if (s is not null && SidebarCatalogue.TryKindOf(id, out var kind) && SidebarCatalogue.Hideable(kind))
                    Dispatch(new SetSectionShown(layout, id, s.Hidden));
                e.Handled = true;
                return;
            }
            if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && (e.Mods & KeyModifiers.Alt) != 0)
            {
                int dir = e.KeyCode == Keys.Up ? -1 : 1;
                int to = SidebarEditRules.MoveTarget(slot, _band.Count, dir, toEdge: (e.Mods & KeyModifiers.Shift) != 0);
                if (to >= 0 && to != slot) Dispatch(new MoveSection(layout, id, to));
                e.Handled = true;
            }
        }
```

The header row of a block: a 40-px row with `Padding = (4, 0, 4, 0)`, children `[checkbox-or-lock (24), title (14 /
600, `TextPrimary`), summary (Caption `TextTertiary`, ellipsised; the text is `Loc.Format(SidebarEditRules.SummaryKeyOf(kind,
row.Count), ("count", row.Count), ("total", SidebarCatalogue.ItemsOf(layout, kind).Count))`, absent when the key is null —
Collections reads "4 of 5 pages · hiding all hides Collections", and with one page left "Last page · hiding it hides
Collections", Q5), spacer, ⋯ (24, `SectionHeader.InlineButton`, opens
`SidebarMenuModel.Header`), grip (`Icons.GripperBar` 16 `TextTertiary`, only when movable)]`. The checkbox takes a
`Signal<bool>` that freezes at mount (`CheckBox.Create(string label, Signal<bool>? isChecked, Action<bool>? onChange, …)`),
so the Outline owns one signal per row key and refreshes it every render:

```csharp
        readonly Dictionary<string, Signal<bool>> _checks = new(StringComparer.Ordinal);

        /// <summary>The checkbox for row <paramref name="key"/> ("section:pinned", "item:collections:albums", "settings"): its
        /// signal is reused across renders and set to the overlay's truth before the element is built.</summary>
        Element Check(string key, bool shown, bool enabled, string title, Action<bool> onChange)
        {
            if (!_checks.TryGetValue(key, out var sig)) _checks[key] = sig = new Signal<bool>(shown);
            sig.SetIfChanged(shown);
            return ToolTip.Wrap(
                CheckBox.Create("", sig, onChange, isEnabled: enabled) with { Key = "check:" + key, BlocksDragArm = true },
                Loc.Format("sidebar.edit.showInSidebar", ("name", title)));   // the label-less box's accessible name
        }
```

A section header's box is `Check("section:" + id, shown, !locked, title, v => Dispatch(new SetSectionShown(layout, id, v)))`;
a locked section shows `Icon(Icons.Lock, 12f, Tok.TextTertiary)` in the 24 box instead; a locked Pinned shows the box
disabled and its tooltip is "Unpin {names} first". Hidden sections render at `Opacity = 0.5f`.
When the user shows a section that has no rows (Recent with no plays, New releases with an empty feed), set
`s_enabledEmptySection` to its title (one toast on Done). The header text is NOT clickable (Q8): it only focuses the
wrapper.

Inner bands (`_pins` kind `"sidebar-item:pins"`, `_items` kind `"sidebar-item:collections"`) use the same pattern with
`ItemExtent = SidebarEditRules.RowHeight`; `_pins.OnReorder = (f, t) => MovePinRecorded(f, t)`; `_items.OnReorder =
`(f, t) => Dispatch(new MoveItem(layout, "collections", items[f], t))`. A pin row: `[art 20 (Cover.ForEntry of the pin's
entry, or the route glyph)] [title] spacer [Unpin 24 InlineButton Icons.UnPin → UnpinRecorded(id, name)] [grip]`. An item
row: `[Check("item:" + sectionId + ":" + item, shown, true, title, v => Dispatch(new SetItemShown(layout, sectionId, item, v)))]
[glyph 16 (Shell.Dest(item).Glyph, or the kind's glyph for Library filters)] [title] spacer [grip when movable]`. The
Library layout's Filters block is a plain column (no band) titled "Filters" with the four kind rows; its header shows
`Icons.Filter` instead of a checkbox.

**The pins band in both layouts** (Q3). The Library Pinned block is a plain column (it is not a `_sections.Item`), but
its body is built exactly as Classic's: `_pins` (`"sidebar-item:pins"`) with one `_pins.Item(i, PinRow(…), key: pin.Id)`
per shown pin row, then the `ShowAllPins` row while folded. Each pin row's wrapper handles Alt+↑/↓ (Alt+Shift: to the
edge), the same keys the section blocks use:

```csharp
        void OnPinKey(int index, KeyEventArgs e)
        {
            if (e.Handled || (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) || (e.Mods & KeyModifiers.Alt) == 0) return;
            int to = SidebarEditRules.MoveTarget(index, Pins.Count, e.KeyCode == Keys.Up ? -1 : 1,
                                                 toEdge: (e.Mods & KeyModifiers.Shift) != 0);
            if (to >= 0 && to != index) MovePinRecorded(index, to);
            e.Handled = true;
        }
```

**The Pinned block's shortcut verbs** (both layouts; in Library this block is the ONLY non-menu place for them, since the
Library pane has no Pinned header, P.2a). Its header row carries, before the ⋯, a subtle small button "Unpin all
shortcuts" while `LockingNames()` is non-empty:

```csharp
            if (kind == SidebarSectionKind.Pinned && LockingNames().Count > 0)
                kids.Add(Button.Create(Loc.Get("sidebar.menu.unpinShortcuts"), UnpinAllShortcutsRecorded,
                    ButtonAppearance.Subtle, ControlSize.Small) with { Key = "pinned-unpin-shortcuts", BlocksDragArm = true });
```

and its ⋯ opens `SidebarMenuModel.Header(layout, State, "pinned", LockingNames())` — which already holds "Unpin all
shortcuts" and the reasoned, disabled "Hide section" while locked — in the Library layout as well as in Classic.

The Settings row: `Check("settings", shown, true, title, v => Dispatch(new SetSectionShown(layout, "settings", v)))`;
hidden adds the caption "Still in the profile menu".

`SectionChip(DragState state)`:

```csharp
        static DragChipSpec? SectionChip(DragState state)
            => state.Payload is ReorderPayload { Owner.Kind: "sidebar-section" } p && p.Index >= 0
                ? new DragChipSpec(Title: SectionTitleAt(p.Index), Glyph: SectionGlyphAt(p.Index))
                : state.Payload is ReorderPayload { Owner.Kind: "sidebar-item:collections" or "sidebar-item:pins" } q
                    ? new DragChipSpec(Title: ItemTitleAt(q), Glyph: Icons.GripperBar)
                    : null;
```

(`SectionTitleAt`/`SectionGlyphAt` read the current band through a static mirror of `_band` — the resolver is static;
store the active `EditPane` instance in a static `s_activeEditPane` set in the mount effect.)

`OnPaneKey` (on the `EditPane` root): Esc (not handled by a lifted Reorderable or an open menu) → `ExitEdit()`; Ctrl+Z /
Ctrl+Y → `Undo()` / `Redo()` (the Outline has no text editor).

**Focus into the Outline** (design C.3). `EditPane` focuses its first band wrapper once, after its first layout: Classic
→ the wrapper (`"block:" + _band[0]`) of the first movable section; Library (no band) → the Pinned block's header row.
Both capture their node with `OnRealized` into `_firstStop`:

```csharp
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            UseLayoutEffect(() =>
            {
                // Once per Edit session (this component mounts on enter): keyboard and palette users land IN the Outline.
                post(() =>
                {
                    if (_firstStop.IsNull) return;
                    var target = hooks.FirstFocusableIn?.Invoke(_firstStop) ?? _firstStop;
                    hooks.FocusNode?.Invoke(target, true);
                });
            }, DepKey.Empty);
```

(`NodeHandle _firstStop;` is a field; the Classic band's slot-0 wrapper sets it in `SectionBlock` when `slot == 0`, the
Library Pinned block's header row sets it in its `OnRealized`.)

**`PaneView` wiring** (`Sidebar.UI.cs`): `Render` reads `bool editing = Sidebar.Editing.Value;` (before the layer is
built, after the plan memo — hooks stay in order); `PaneChildren(rows, compact)` returns
`[_editPane ??= Embed.Comp(() => new EditPane(this)) with { Key = "edit-pane" }]` when `editing && !compact`, and the
normal `[head, body, footer]` otherwise. The drawer mount does the same (the overlay is pinned open while editing,
§P2.3).

**Focus back out** (design C.3: "the row that had it before Edit, by key"). The list is unmounted while editing, so the
pane captures the keyboard-current row's KEY in `Render`, on the false → true edge, while the old list is still mounted,
and restores it after the true → false edge — never for an account-switch exit:

```csharp
        bool _wasEditing;
        string? _editReturnKey;
        bool _restoreAfterEdit;

        // In Render, right after `bool editing = Sidebar.Editing.Value;`:
            if (editing && !_wasEditing)
            {
                int current = _listController.CurrentItemIndex;
                var rows = Plan.Rows;
                _editReturnKey = (uint)current < (uint)rows.Count ? rows[current].Key : null;
            }
            else if (!editing && _wasEditing)
                _restoreAfterEdit = Sidebar.EditExitRestoresFocus;
            _wasEditing = editing;

        // A layout effect beside the others (after the commit that remounted the list):
            UseLayoutEffect(() =>
            {
                if (!_restoreAfterEdit) return;
                _restoreAfterEdit = false;
                string? key = _editReturnKey;
                _editReturnKey = null;
                _post(() =>
                {
                    int at = key is null ? -1 : IndexOfRowKey(key);
                    if (at >= 0) _listController.FocusItem(at);   // P0: current + into view + keyboard focus
                    else FocusFooterMore();                       // the row is gone (or focus was never in the list)
                });
            }, DepKey.From(editing ? 1 : 0));
```

```csharp
        /// <summary>The plan row whose key is <paramref name="key"/>, or −1.</summary>
        int IndexOfRowKey(string key)
        {
            var rows = Plan.Rows;
            for (int i = 0; i < rows.Count; i++) if (string.Equals(rows[i].Key, key, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>The footer's ⋯ (the Edit entry most pointer users came from). <c>PaneFooter</c> registers its node here.</summary>
        internal void FocusFooterMore()
        {
            if (!_footerMore.IsNull) _hooks?.FocusNode?.Invoke(_footerMore, true);   // _hooks: P1.5's InputHooks field
        }

        internal NodeHandle _footerMore;   // PaneFooter: `OnRealized = h => { _more = h; owner._footerMore = h; }`
```

(`post` is the pane's existing `_post` field — `_post = UsePost();` in `Render` today: write `_post(() => …)`.)

**The pinned-overlay scrim** (`Shell/Shell.UI.cs` `DrawerScrim`): while `pinned` (editing outside the Wide band), the
scrim fill is `ColorF.FromRgba(0, 0, 0, 0x33)` at opacity `0.2f / 0.2f`… i.e. a non-dismissing scrim at 0.2 (Q16):
`Opacity = pinned ? 1f : target`, `Fill = pinned ? new ColorF(0f, 0f, 0f, 0.2f) : ColorF.FromRgba(0, 0, 0, 0x33)`,
`HitTestVisible = open && !pinned`, `OnClick = pinned ? null : close` — the page behind stays usable (Q16: "signals the
page is still usable").

**The inline rail behind a pinned overlay is inert** (design C.3): `PaneView.Render` (the docked mount) sets
`HitTestVisible = !(Sidebar.Editing.Value && SidebarPaneModeRules.OverlayPinned(Sidebar.Band.Value, true))` on its layer.

### P4.6 Menus on the sidebar

`Sidebar.UI.Menus.cs`:

```csharp
    /// <summary>Data menu rows → engine menu items; actions → service calls. ONE mapper for every sidebar menu.</summary>
    internal static class SidebarMenus
    {
        /// <param name="pane">The pane a row menu belongs to — the rootlist moves commit through its resolver. Null for the
        /// pane-wide menus (none of their rows needs it).</param>
        public static IReadOnlyList<MenuFlyoutItem> Map(IReadOnlyList<SidebarMenuRow> rows, string? sectionId = null, PaneView? pane = null)
        {
            var items = new List<MenuFlyoutItem>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Separator) { items.Add(MenuFlyoutItem.Separator); continue; }
                string label = LabelOf(r);
                if (r.Children is { } kids) { items.Add(MenuFlyoutItem.SubMenu(label, Map(kids, sectionId, pane)) with { Enabled = r.Enabled }); continue; }
                var row = r;
                Action invoke = () => Run(row, sectionId, pane);
                var item = r.Radio ? MenuFlyoutItem.RadioItem(label, r.Checked, invoke)
                    : r.Action is SidebarMenuAction.ToggleLiked or SidebarMenuAction.ToggleKind ? MenuFlyoutItem.Toggle(label, r.Checked, invoke)
                    : new MenuFlyoutItem(label, default, r.Enabled, invoke);
                items.Add(item with { Enabled = r.Enabled });
            }
            return items;
        }

        static string LabelOf(SidebarMenuRow r) => r.Action switch
        {
            SidebarMenuAction.SwitchLayout => LayoutName(r.Arg == "library" ? SidebarLayoutId.Library : SidebarLayoutId.Classic),
            SidebarMenuAction.SetLimit => Loc.Format(r.LabelKey, ("count", r.Arg)),
            SidebarMenuAction.HideSection when r.ReasonKey is { } reason => Loc.Format(reason, ("names", string.Join(", ", LockingNames()))),
            _ => Loc.Get(r.LabelKey),
        };

        static void Run(SidebarMenuRow r, string? sectionId, PaneView? pane)
        {
            var layout = Sidebar.Layout.Peek();
            switch (r.Action)
            {
                case SidebarMenuAction.SwitchLayout: SwitchLayout(r.Arg == "library" ? SidebarLayoutId.Library : SidebarLayoutId.Classic); break;
                case SidebarMenuAction.ResetLayout: Dispatch(new ResetLayout(layout), Loc.Format("sidebar.toast.reset", ("name", LayoutName(layout)))); break;
                case SidebarMenuAction.ShowSection: Dispatch(new SetSectionShown(layout, r.Arg, true)); break;
                case SidebarMenuAction.SetDensity: SetDensity(r.Arg == "compact" ? SidebarDensity.Compact : SidebarDensity.Default); break;
                case SidebarMenuAction.EditSidebar: EnterEdit(); break;
                case SidebarMenuAction.ResetEverything when Overlay is { } overlay: ConfirmResetEverything(overlay); break;
                case SidebarMenuAction.SetSort:
                    SidebarStoreV3.TryParseSort(r.Arg, out var sort);
                    Dispatch(new SetLibrarySort(sort, Doc.Library.Descending));
                    break;
                case SidebarMenuAction.SetView: Dispatch(new SetLibraryView(r.Arg == "grid" ? SidebarLibraryView.Grid : SidebarLibraryView.List)); break;
                case SidebarMenuAction.ToggleLiked: Dispatch(new SetShowLiked(!Doc.Library.ShowLiked)); break;
                case SidebarMenuAction.ToggleKind:
                    bool shown = (Doc.Library.HiddenKinds & SidebarCatalogue.KindFlagOf(r.Arg)) != 0;
                    // C.5 / Q14: a hide toasts "{name} hidden · Undo"; showing a kind back is quiet (the chip reappears).
                    Dispatch(new SetItemShown(SidebarLayoutId.Library, "library", r.Arg, shown),
                             shown ? null : Loc.Format("sidebar.toast.hidden", ("name", Loc.Get("sidebar.item." + r.Arg))));
                    if (!shown) SetLibraryFilter(SidebarLibraryFilters.Effective((int)LibraryFilter.Peek(), Doc.Library.HiddenKinds));
                    break;
                case SidebarMenuAction.SetLimit when sectionId is not null:
                    Dispatch(new SetSectionLimit(layout, sectionId, int.Parse(r.Arg, CultureInfo.InvariantCulture)));
                    break;
                case SidebarMenuAction.ShowItem when sectionId is not null: Dispatch(new SetItemShown(layout, sectionId, r.Arg, true)); break;
                case SidebarMenuAction.Collapse when sectionId is not null: Dispatch(new SetSectionCollapsed(layout, sectionId, true)); break;
                case SidebarMenuAction.Expand when sectionId is not null: Dispatch(new SetSectionCollapsed(layout, sectionId, false)); break;
                case SidebarMenuAction.MoveUp when sectionId is not null: MoveBy(layout, sectionId, r.Arg, -1); break;
                case SidebarMenuAction.MoveDown when sectionId is not null: MoveBy(layout, sectionId, r.Arg, +1); break;
                case SidebarMenuAction.HideSection when (r.Arg.Length > 0 ? r.Arg : sectionId) is { } hideId:
                    // The pane menu's "Hide pinned" carries its section in Arg; a header's Hide uses the header's id.
                    Dispatch(new SetSectionShown(layout, hideId, false), Loc.Format("sidebar.toast.hidden", ("name", SectionTitle(hideId))));
                    break;
                case SidebarMenuAction.UnpinAllShortcuts: UnpinAllShortcutsRecorded(); break;
                case SidebarMenuAction.Unpin: UnpinRecorded(r.Arg, PinName(r.Arg)); break;
                case SidebarMenuAction.MovePinUp: MovePinRecorded(Pins.IndexOf(r.Arg), Pins.IndexOf(r.Arg) - 1); break;
                case SidebarMenuAction.MovePinDown: MovePinRecorded(Pins.IndexOf(r.Arg), Pins.IndexOf(r.Arg) + 1); break;
                case SidebarMenuAction.HideItem: HideItem(layout, sectionId, r.Arg); break;
                // One sibling step through WaveeResourceDrop.MoveRootlist: legality, undo anchors and the refusal toast are
                // the drop's own (PaneView.MoveSibling → CommitMove).
                case SidebarMenuAction.MoveRootlistUp when pane is not null: pane.MoveSibling(r.Arg, -1); break;
                case SidebarMenuAction.MoveRootlistDown when pane is not null: pane.MoveSibling(r.Arg, 1); break;
            }
        }
    }
```

and, in the same class:

```csharp
        /// <summary>The overlay host the last sidebar menu opened on (set by <c>PaneView.OpenPaneMenu</c>, the header ⋯ and the
        /// row menus right before they open) — the confirm dialog of a menu verb opens on the same host.</summary>
        internal static IOverlayService? Overlay;

        /// <summary>"Reset everything…" (design C.3): confirm, then ONE recorded batch (§P4.4).</summary>
        internal static void ConfirmResetEverything(IOverlayService overlay)
            => ContentDialog.Show(overlay, d =>
            {
                d.Title = Loc.Get("sidebar.edit.resetAllTitle");
                d.Message = Loc.Get("sidebar.edit.resetAllBody");
                d.PrimaryText = Loc.Get("sidebar.edit.resetAllConfirm");
                d.CloseText = Loc.Get(Strings.Auth.Cancel);
                d.PrimaryClick = ResetEverythingRecorded;
            });
```

(`PaneView.OpenPaneMenu`, `HeaderMenu`'s opener and the row-menu openers each set `SidebarMenus.Overlay = MenuOverlay;`
before opening; the Settings card's "Reset everything…" item calls `SidebarMenus.ConfirmResetEverything` with the
Settings page's own overlay host.)

with the helpers: `MoveBy(layout, sectionId, itemArg, dir)` — an item arg moves the item inside Collections
(`MoveItem` to its effective index ± 1), an empty arg moves the section one band slot (`MoveSection`); `HideItem` —
Collections page → `SetItemShown(…, false)` with toast "{name} hidden · Undo", EXCEPT for the section's last visible item:
that op hides the whole section (§P3.2), so the toast names the SECTION (design C.4 corner case):

```csharp
            case SidebarSectionKind.Collections:
                // Hiding the last visible page hides Collections itself: say so, not "Albums hidden".
                bool last = Doc.Find(SidebarSectionKind.Collections) is { Items.Count: 1 };
                string name = last ? SectionTitle("collections") : Loc.Get("sidebar.item." + item);
                Dispatch(new SetItemShown(layout, "collections", item, false), Loc.Format("sidebar.toast.hidden", ("name", name)));
                break;
```

(`SidebarSection.Items` is the visible items in effective order, §P3.3); the Library's Liked row → `SetShowLiked(false)`
with toast; Settings → `SetSectionShown(settings, false)` with toast "Settings hidden. It stays in the profile menu";
"Unpin all shortcuts" → `UnpinAllShortcutsRecorded()` (§P4.4: one batch entry, one toast "Shortcuts unpinned · Undo",
one Undo restores every pin); `PinName(id)` — the pin's `Name` or the route title; `LockingNames()` —
`internal static IReadOnlyList<string> LockingNames()` filling a static `List<string>` through
`SidebarVisibilityRules.LockingPins(Pins.Items, …)`; `SectionTitle(id)`. In `LabelOf`, the `HideSection` arm with a
reason also covers the pane menu's "Hide pinned" (same "Unpin {names} first" text).

Wiring:

- The header ⋯ (`Sidebar.UI.Slot.cs` `HeaderRow`): `PaneView.HeaderMenu(sectionId)` returns
  `new ContextMenuModel(SidebarMenus.Map(SidebarMenuModel.Header(Layout.Peek(), State, sectionId, LockingNames()), sectionId), header)`;
  a long-press (500 ms) on the header opens the same menu (`WithContextMenu` already covers right-click, Menu,
  Shift+F10 and the touch long-press).
- The pane menu: `PaneView.PaneMenu()` returns `SidebarMenus.Map(SidebarMenuModel.Pane(Layout.Peek(), State, Density.Peek(),
  Editing.Peek(), SidebarMenus.LockingNames()))`; the pane background's shield, the seam (`BuildSeamParts` in `Shell.UI.cs`), the footer ⋯ and the
  rail's empty space all open it. `LayoutMenu` (the class) is deleted; `Sidebar.LayoutName` (§P3.12, on the `Sidebar` partial, not inside `LayoutMenu`) stays, so `SidebarMenus.LabelOf`/`Run` (unqualified `LayoutName(…)`) and the Settings card (`Sidebar.LayoutName(layout)` since P3) keep compiling. Every other `LayoutMenu.` caller is gone by P4: `Shell.UI.cs`'s seam menu (P4-WP7) and `PaneView.PaneMenu` (P4-WP4) open the P4 pane menu, and Library V3's overflow (P4-WP6) maps `SidebarMenuModel.Pane(…)`.
- Row menus: `PaneView.EntryMenu`/`FolderMenu`/`RouteMenu` append `SidebarMenus.Map(SidebarMenuModel.Item(section.Kind, key,
  index, count, unavailable, RootlistStepOf(section, planIndex)), section.Id, this)` after a separator, where `index`/`count`
  are the pin's index in `Pins` (Pinned) or the item's index in the section's visible items (Collections). `MoveSibling`
  becomes `internal` (the menu mapper calls it) and `Sidebar.UI.Menus.cs` gains

```csharp
        /// <summary>The row menu's rootlist step (§P4.2): TreeMoves' sibling verbs — whose gate already limits them to Classic
        /// Playlists and to Your Library under Playlists · Custom order — unless the row sits inside a ≥2 selection (the
        /// positional verbs mean nothing for N rows; "Move N to folder…" covers it).</summary>
        internal SidebarRootlistStep RootlistStepOf(SidebarSection section, int planIndex)
        {
            var (tree, entryId) = TreeMoves(section, planIndex);
            if (entryId.Length == 0) return default;
            if (TreeSelection.Count >= 2 && TreeSelection.Contains(entryId)) return default;
            return new SidebarRootlistStep(true, tree.MoveUp, tree.MoveDown);
        }
```

  `NavExtras` keeps only what the model does not hold: "Move to folder…" / "Move N to folder…" and "Select". Its two
  `Actions.Menu.AddMoveRows` calls go (the pin/band moves are `MovePinUp/Down`, the rootlist moves `MoveRootlistUp/Down`
  — one Move up per menu, never two), and with them its `SidebarNavLayout.Decide` call (P5.2 deletes the type).
- The footer's Settings row gets `Menu = () => new ContextMenuModel(SidebarMenus.Map(SidebarMenuModel.Item(SidebarSectionKind.Settings, "settings", 0, 1, false)), null)`.

**Ctrl+Z / Ctrl+Y — focused routing inside the sidebar, never frame-global** (design V.11, C.5: the keys act only with
focus in the pane, the head or the edit bar). A frame chord would fire with focus on the PAGE too — a Ctrl+Z there within
6 s of a sidebar toast, or at any time during Edit, would silently undo and sync a sidebar edit. So there is NO chord in
`Shell.UI.cs`; the sidebar's own key handlers take the keys, and `KeyEventArgs` only reach them while focus is inside:

- `PaneView.OnPaneKey` (`Sidebar.UI.cs`, P4-WP4; the pane root's `OnKeyDown` from P1.5). The Library head, the list, the
  footer and the Classic filter box are all children of that root, so their unhandled keys bubble here. It opens with:

```csharp
        void OnPaneKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            // Ctrl+Z / Ctrl+Y with focus IN the sidebar (this handler only sees keys routed through the pane): the ring's
            // undo/redo, while editing or while a sidebar toast is open (Q6). A focused TextBox consumed its own Ctrl+Z
            // first; FocusedIsTextEditor covers its empty-stack fall-through (TextEditCore.cs:349-352).
            if (e.Mods == KeyModifiers.Ctrl && e.KeyCode is Keys.Z or Keys.Y)
            {
                if (SidebarUndoRing.KeyAllowed(Editing.Peek(), SidebarToastOpen, Shell.FocusedIsTextEditor()))
                {
                    if (e.KeyCode == Keys.Z) Undo(); else Redo();
                    e.Handled = true;
                }
                return;
            }
            // … P1.5's Ctrl+F arm, unchanged (P5 adds the OpenSearch line at its head) …
        }
```

- `EditPane.OnPaneKey` (`Sidebar.UI.Edit.cs`, P4-WP3, §P4.5) handles them for the Outline and the edit bar (it sets
  `e.Handled`, so the pane's arm above never doubles them).
- `Shell.UI.cs` (P4-WP7) only makes `FocusedIsTextEditor()` `internal static` for that call. It adds no chord.

`SidebarUndoRing.KeyAllowed` keeps its signature; its summary gains: "Called only from the sidebar's own key handlers
(focused routing), so the focus-in-the-sidebar term of design V.11 is structural, not a parameter."

**Esc precedence inside the pane** (design V.11): an open menu (the overlay host's own Esc) → a live drag (the drag
controller's) → the Classic filter box (`PaneFilterBox`'s handler) → Edit (`EditPane.OnPaneKey`) → the overlay pane
(`NarrowDrawer`'s preview, which defers while editing, §P2.3). Nothing new is needed beyond those handlers.

### P4.7 The palette command and the wizard's Layout step

**Palette** (`Shell/Shell.Palette.cs`): append `EditSidebar, SidebarClassic, SidebarLibrary` to `PaletteSettingsVerb`;
add rows `SetRow("sidebar.edit", Loc.Get("sidebar.menu.edit"), Icons.Edit, PaletteSettingsVerb.EditSidebar)`,
`SetRow("sidebar.classic", Loc.Format("sidebar.palette.useLayout", ("layout", Loc.Get("sidebar.layoutName.classic"))), Icons.List, PaletteSettingsVerb.SidebarClassic)`,
`SetRow("sidebar.library", …"library"…, Icons.Library, PaletteSettingsVerb.SidebarLibrary)`; `InvokePaletteEntry` arms:
`EditSidebar` → `Sidebar.EnterEdit()`; the two layout verbs → `Sidebar.SwitchLayout(…)` (refused while editing — the
arm shows `Toast.Show(Loc.Get("sidebar.pane.finishEditing"), …)` when `Sidebar.Editing.Peek()`).

**The setup wizard's Layout step** (`Screens/Setup.cs`, `Screens/Setup.UI.cs`), first runs only:

```csharp
    /// <summary>The wizard's pages in display order. Layout (the sidebar's two layouts) is offered on a FIRST RUN only.</summary>
    public enum WizardPage : byte { Terms = 0, SignIn = 1, Layout = 2, LocalPlayback = 3 }
```

`Gating`: `StepTotal = 3`; `NextPage(page, skipSignIn, skipLayout)` clamps at `LocalPlayback`, skips `SignIn` when
`skipSignIn` and `Layout` when `skipLayout`; `PrevPage` mirrors it; `ShowsLayout(WizardEntry entry) => entry ==
WizardEntry.FirstRun`; `StepNumber(page)` = `null` for Terms, else `((int)page, StepTotal)`; `Progress(page) = (int)page /
(float)StepTotal`; `ShowsBack(page)` = `page is WizardPage.Layout or WizardPage.LocalPlayback`. `Commands.Resolve` gives
Layout `new CommandRow(Strings.Setup.Next, null, ButtonKind.Accent, true, false, false, false)` (add `setup.next` = "Next":
the key does not exist today). `WizardRules.HeroAsset(Layout)` → `"connect"` (reuses a loaded hero; no new Lottie asset). Every caller of
`NextPage`/`PrevPage` passes `skipLayout: !Gating.ShowsLayout(session.Entry)`. `Setup.UI.cs` renders the page:

```csharp
    /// <summary>Pick a sidebar layout (design C.2 entry 5): the two Settings cards, applied at once.</summary>
    sealed class LayoutPage(WizardSession session) : Component
    {
        public override Element Render()
        {
            var layout = Sidebar.Layout.Value;
            var body = SetupText.Stack(
                Loc.Get("setup.layout.body"),
                Settings.SidebarLayoutCards(layout, editing: false));
            return WizardFrame(WizardPage.Layout, Loc.Get("setup.layout.title"), body);
        }
    }
```

(`WizardFrame`/`SetupText.Stack` exist; `Settings.SidebarLayoutCards` is public from §P3.13. The page key
`"setup:page:layout"` joins the page switch; the hero preload list gains `LoadHero(WizardPage.Layout)`.)

### P4.7a Settings › Sidebar while editing (Q7) (`Screens/Settings.UI.Appearance.cs`)

`SidebarLayoutCard.Render` (§P3.13) adds two items after "Reset this layout", and that item's subtitle also carries the
reason while editing. A disabled control always says why (Q7); the palette path already toasts the same reason (§P4.7):

```csharp
            string finish = Loc.Get("sidebar.pane.finishEditing");
            // (the existing "Reset this layout" item: its subtitle becomes `editing ? finish : Loc.Get("settings.sidebar.resetSub")`)
            items.Add(Item(Loc.Get("sidebar.menu.edit"), editing ? finish : Loc.Get("settings.sidebar.editSub"), null,
                isClickEnabled: !editing, onClick: static () => Sidebar.EnterEdit(), icon: Icons.Edit));
            items.Add(Item(Loc.Get("sidebar.menu.resetEverything"), editing ? finish : Loc.Get("settings.sidebar.resetEverythingSub"), null,
                isClickEnabled: !editing,
                // The Settings page's own overlay host (Settings.UI.cs `s_overlay`): the confirm opens over Settings.
                onClick: static () => { if (s_overlay is { } overlay) Sidebar.SidebarMenus.ConfirmResetEverything(overlay); },
                icon: Icons.Undo));
```

`SidebarLayoutCards` shows the same reason under the cards while editing (the cards ignore clicks then, §P3.13):

```csharp
    public static Element SidebarLayoutCards(SidebarLayoutId active, bool editing)
    {
        var strip = Controls.PickerStrip(2, (int)active,
            static (i, on) => SidebarLayoutCardFace((SidebarLayoutId)i, on),
            editing ? static _ => { } : static i => Sidebar.SwitchLayout((SidebarLayoutId)i));
        if (!editing) return strip;
        // Q7: never a silent dead control — the cards say why they do not switch.
        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children = [strip, Ui.Caption(Loc.Get("sidebar.pane.finishEditing")) with { Color = Tok.TextSecondary }],
        };
    }
```

### P4.8 Migration toast action

`ShowPendingMigrationToast()` (§P3.10) gains `ActionLabel = Loc.Get("sidebar.menu.edit"), OnAction = EnterEdit`.

### P4.9 Loc (en-US)

Under `sidebar`: `section.title.{home,pinned,collections,playlists,library,recent,newReleases,settings}` ("Home",
"Pinned", "Collections", "Playlists", "Your Library", "Recently played", "New releases", "Settings" — one per catalogue
id: the menus, the toasts and the Outline read them),
`item.{liked,albums,artists,podcasts,audiobooks}` ("Liked Songs", "Albums", …), `menu.{layout,showSection,nothingHidden,
density,edit,resetEverything,show,showCount,showHidden,unpinShortcuts,collapse,expand,moveUp,moveDown,hideSection,
unpinFirst,hideFromSidebar,sort,view,filters,showLiked}` ("Layout", "Show section", "Nothing is hidden", "Density",
"Edit sidebar…", "Reset everything…", "Show", "Show {count}", "Show hidden", "Unpin all shortcuts", "Collapse", "Expand",
"Move up", "Move down", "Hide section", "Unpin {names} first", "Hide from sidebar", "Sort", "View", "Filters",
"Show Liked Songs"), `sort.{recents,recentlyAdded,alphabetical,creator,customOrder,customOnlyPlaylists}`,
`view.{list,grid}`, `undo.{undo,redo,undid,redid}` ("Undo", "Redo", "Undid: {what}", "Redid: {what}"), `undo.label.{hide,show,move,reset,limit,
sort,view,liked,pin,unpin}`, `edit.{title,reset,done,resetAllTitle,resetAllBody,resetAllConfirm,emptyOnDone,announceGrabbed,
announceMoved,announceDropped,announceCancelled,footClassic,footLibrary,stillInProfile,pinsSummary,showAllPins,
playlistsSummary,recentHint,newReleasesHint,libraryHint,filters,collectionsSummary,collectionsLastSummary}` (the three
summaries: `pinsSummary` "{count} pinned", `playlistsSummary` "{count} playlists", `collectionsSummary` "{count} of {total}
pages · hiding all hides Collections", `collectionsLastSummary` "Last page · hiding it hides Collections"),
`toast.{reset,hidden}`,
`pin.{pinnedShownAgain,pinnedNamed,unpinnedNamed}`, `palette.useLayout`, `menu.hidePinned` ("Hide pinned"),
`toast.{shortcutsUnpinned,resetEverything}` ("Shortcuts unpinned", "Sidebar reset"), `undo.label.{unpinShortcuts,
resetEverything}` ("Unpin all shortcuts", "Reset everything"), `edit.showInSidebar` ("Show {name} in sidebar"),
`edit.finishDrag` ("Finish dragging first" — `EnterEdit`'s refusal while a drag is live, §P4.4); under
`settings.sidebar`: `editSub` ("Show, hide and reorder sections and pins in place"), `resetEverythingSub` ("Both layouts and
the density back to their defaults. Pins stay."); under `setup`: `layout.title` ("Choose your
sidebar"), `layout.body` ("Classic shows sections you can arrange. Library is one list with filters. You can switch any
time in Settings."), `next` ("Next" — new; `setup` has no `next` key today).

### P4.10 Tests

New `SidebarEditRulesTests.cs`: `Outline_Classic_HomeBandSettings` (row kinds in order for the default state); `Outline_
Library_FixedOrder_FiltersAsItems_NoBand` (`Band` empty); `Outline_PinsAsRows_FoldAbove50` (60 pins ⇒ 50 pin rows + one
ShowAllPins with count 60; `showAll` ⇒ 60); `Outline_PinnedLocked_ByRoutePins`; `Outline_HiddenSectionShownFalse`;
`Band_MovableOnly_InDisplayOrder`; `Extents_HeaderPlusBody` (Collections = 40 + 5 × 40; Playlists = 40 + 44);
`MoveTarget_StepAndEdge`; `AnnounceKeys`; `Summary_Collections_NamesTheCoupling` (`SummaryKeyOf(Collections, 5)` is
`sidebar.edit.collectionsSummary`, `SummaryKeyOf(Collections, 1)` is `sidebar.edit.collectionsLastSummary`, `SummaryKeyOf(Recent, 3)`
is null; Q5); and the Library pins band (Q3):

```csharp
    [Fact]
    public void Outline_Library_PinsInnerBand()
    {
        // Library has no SECTION band, but its pins stay reorderable in the Outline (drag, Space lift, Alt+↑/↓).
        var pins = new[] { new SidebarPin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a", "A", 0),
                           new SidebarPin("search", SidebarEntryKind.AppRoute, "", "Search", 0) };
        var rows = new List<SidebarOutlineRow>();
        SidebarEditRules.Outline(SidebarLayoutState.Default, SidebarLayoutId.Library, pins, pinnedLocked: true,
                                 showAllPins: false, playlistCount: 0, rows);
        var band = new List<string>();
        SidebarEditRules.Band(SidebarLayoutState.Default.Of(SidebarLayoutId.Library), band);
        Assert.Empty(band);                                                              // no section band
        var pinRows = rows.Where(r => r.Kind == SidebarOutlineRowKind.Pin).ToList();
        Assert.Equal(2, pinRows.Count);
        Assert.All(pinRows, r => Assert.True(r.Movable));                                // the inner pins band
        Assert.False(rows.Single(r => r.Kind == SidebarOutlineRowKind.Section && r.SectionId == "pinned").Movable);
    }
```

New `SidebarMenuModelTests.cs`: `Pane_Default_ResetDisabledUntilModified`; `Pane_Editing_LayoutAndResetEverythingDisabled
WithReason_DensityLive`; `Pane_ShowSection_ListsHiddenSettingsAndPinned`; `Header_Recent_ShowCountRadios`;
`Header_Pinned_LockedHideHasReason_AndUnpinAllShortcuts` (also for `SidebarLayoutId.Library`: the Outline's Pinned
block opens the same menu); `Header_Playlists_NoHide`; `Header_Movable_EdgesDisabled`;
`Header_Library_None` (Library has no header menu in Classic terms: the Library section's options are `LibraryOptions`);
`LibraryOptions_CustomOrderOnlyUnderPlaylists`; `LibraryOptions_FiltersReflectHiddenKinds`; `Item_Pin_MoveAndUnpin`;
`Item_UnavailablePin_UnpinOnly`; `Item_Collections_MoveAndHide`; `Item_LibraryLiked_Hide`; `Item_Settings_Hide`; the
rootlist moves, the loc-key guard, and the Library pane menu (every `Pane(...)` call in the file passes a
`lockingPinNames` list; `using System.IO; using System.Linq; using System.Text.Json;` at the top; `Pane_ShowSection_…`
is written out here, the other names above are described by their names):

```csharp
    [Fact]
    public void Pane_ShowSection_ListsHiddenSettingsAndPinned()
    {
        var state = SidebarLayoutState.Default;
        state = SidebarLayoutRules.Apply(state, new SetSectionShown(SidebarLayoutId.Classic, "settings", false), pinnedLocked: false).State;
        state = SidebarLayoutRules.Apply(state, new SetSectionShown(SidebarLayoutId.Classic, "pinned", false), pinnedLocked: false).State;
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Classic, state, SidebarDensity.Default, false, None);
        var show = rows.First(r => r.LabelKey == "sidebar.menu.showSection");
        Assert.Contains(show.Children!, c => c.Arg == "settings");   // Q15
        Assert.Contains(show.Children!, c => c.Arg == "pinned");
        AssertEveryLabelKeyResolves(rows);                           // no raw "sidebar.section.title.settings" on screen (D9)
    }

    [Fact]
    public void Item_RootlistRow_MovesOnlyWhileTheRootlistOrderIsShown()
    {
        var mid = new SidebarRootlistStep(Shown: true, CanMoveUp: true, CanMoveDown: true);
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Playlists, "pl:a", 3, 10, false, mid);
        Assert.Equal([SidebarMenuAction.MoveRootlistUp, SidebarMenuAction.MoveRootlistDown], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal("pl:a", r.Arg));
        // Your Library under a non-Custom sort: the caller passes Shown false ⇒ no positional verbs.
        Assert.Empty(SidebarMenuModel.Item(SidebarSectionKind.Library, "pl:a", 3, 10, false, default));
        // Library under Playlists · Custom order, the first sibling: Move down only (an end is absent, never disabled).
        var first = SidebarMenuModel.Item(SidebarSectionKind.Library, "pl:a", 0, 10, false, new SidebarRootlistStep(true, false, true));
        Assert.Equal(SidebarMenuAction.MoveRootlistDown, Assert.Single(first).Action);
        // Liked keeps its Hide row and never gets a rootlist move.
        var liked = SidebarMenuModel.Item(SidebarSectionKind.Library, SidebarCatalogue.LikedRoute, 0, 1, false, mid);
        Assert.Equal(SidebarMenuAction.HideItem, Assert.Single(liked).Action);
        AssertEveryLabelKeyResolves(rows);
    }

    /// <summary>Every LabelKey and ReasonKey a menu emits exists in the base catalog: a raw key on screen is the bug.
    /// Call it from the Header_*, LibraryOptions_* and Item_* facts too.</summary>
    static void AssertEveryLabelKeyResolves(IReadOnlyList<SidebarMenuRow> rows)
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        var root = doc.RootElement;
        void Walk(IReadOnlyList<SidebarMenuRow> list)
        {
            foreach (var r in list)
            {
                if (!r.Separator) Assert.True(Has(root, r.LabelKey), r.LabelKey + " is not in the base catalog");
                if (r.ReasonKey is { } reason) Assert.True(Has(root, reason), reason + " is not in the base catalog");
                if (r.Children is { } kids) Walk(kids);
            }
        }
        Walk(rows);
    }

    static bool Has(JsonElement root, string dotted)
    {
        var e = root;
        foreach (var part in dotted.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return false;
        return e.ValueKind == JsonValueKind.String;
    }

    /// <summary>ModulePageGateTests' helper, copied: walk up from the test binary to src/apps/Wavee/assets/loc.</summary>
    static string? FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }

    /// <summary>The drag chip's unpinnable-drop captions (§P3.9 <c>SidebarPinRules.RefusalKeyOf</c>) exist: a missing key
    /// would put the raw key on the chip.</summary>
    [Fact]
    public void PinRefusalKeys_Resolve()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        foreach (var kind in new[] { DragKind.Track, DragKind.Episode })
            Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(kind, "x", "spotify:x")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Track, "x", "wavee:local:track:1")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Route, "home", "")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Route, "liked", "")!));
    }
```

The Library pane menu:

```csharp
    static readonly string[] None = [];
    static readonly string[] Search = ["Search"];

    static SidebarMenuRow? Find(IReadOnlyList<SidebarMenuRow> rows, SidebarMenuAction action)
    {
        foreach (var r in rows) if (r.Action == action) return r;
        return null;
    }

    [Fact]
    public void Pane_Library_HidePinned_EnabledWithoutShortcutPins()
    {
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Library, SidebarLayoutState.Default, SidebarDensity.Default, false, None);
        var hide = Find(rows, SidebarMenuAction.HideSection);
        Assert.NotNull(hide);
        Assert.Equal("pinned", hide!.Arg);
        Assert.True(hide.Enabled);
        Assert.Null(Find(rows, SidebarMenuAction.UnpinAllShortcuts));
    }

    [Fact]
    public void Pane_Library_HidePinned_DisabledWithReason_AndUnpinAllShortcuts_WhileLocked()
    {
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Library, SidebarLayoutState.Default, SidebarDensity.Default, false, Search);
        var hide = Find(rows, SidebarMenuAction.HideSection)!;
        Assert.False(hide.Enabled);
        Assert.Equal("sidebar.menu.unpinFirst", hide.ReasonKey);
        Assert.NotNull(Find(rows, SidebarMenuAction.UnpinAllShortcuts));
    }

    [Fact]
    public void Pane_Library_PinnedHidden_NoHideRow_ShowSectionListsIt()
    {
        var state = SidebarLayoutRules.Apply(SidebarLayoutState.Default,
            new SetSectionShown(SidebarLayoutId.Library, "pinned", false), pinnedLocked: false).State;
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Library, state, SidebarDensity.Default, false, None);
        Assert.Null(Find(rows, SidebarMenuAction.HideSection));
        var show = rows.First(r => r.LabelKey == "sidebar.menu.showSection");
        Assert.Contains(show.Children!, c => c.Arg == "pinned");
    }

    [Fact]
    public void Pane_Classic_NoPinnedRows()
    {
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Classic, SidebarLayoutState.Default, SidebarDensity.Default, false, Search);
        Assert.Null(Find(rows, SidebarMenuAction.HideSection));
        Assert.Null(Find(rows, SidebarMenuAction.UnpinAllShortcuts));   // Classic keeps them on the Pinned header (Q17)
    }
```

New `SidebarUndoRingTests.cs`: `PushUndoRedo_RoundTrip`; `Cap50_DropsOldest`; `Push_ClearsRedo`; `IsTop_OnlyNewest`;
`Clear_EmptiesBoth`; `CollapseNotRecorded` (`Records(SetSectionCollapsed)` false, `Records(SetSectionShown)` true);
`KeyAllowed_Matrix` (editing ⇒ true; outside with toast ⇒ true; outside without ⇒ false; text editor ⇒ false);
`RingToasts_ActOnlyOnTheirEntry_UndidOffersRedo_RedidOffersUndo` (Q6; the toasts themselves are registered in
`s_toasts`, which is what `KeyAllowed`'s "toast open" reads):

```csharp
    [Fact]
    public void RingToasts_ActOnlyOnTheirEntry_UndidOffersRedo_RedidOffersUndo()
    {
        var ring = new SidebarUndoRing();
        var a = ring.Push(PinLeaf(SidebarPinChange.Unpinned, "a", 0));
        var b = ring.Push(PinLeaf(SidebarPinChange.Unpinned, "b", 1));
        Assert.True(ring.TryUndo(out _));                      // "Undid: b · Redo"
        Assert.True(ring.IsRedoTop(b.Id));
        Assert.False(ring.IsRedoTop(a.Id));
        Assert.True(ring.TryUndo(out _));                      // "Undid: a · Redo" — b's toast is now stale
        Assert.False(ring.IsRedoTop(b.Id));
        Assert.True(ring.TryRedo(out var redone));             // "Redid: a · Undo"
        Assert.Equal(a.Id, redone.Id);
        Assert.True(ring.IsTop(a.Id));
        ring.Push(PinLeaf(SidebarPinChange.Pinned, "c", 0));   // a new edit clears redo
        Assert.False(ring.IsRedoTop(b.Id));
    }
```
`PinEntries_CarryWhatTheInverseNeeds` (an Unpinned entry keeps the pin and its index); and the one-action-one-entry
facts (C.5, Q4, Q17):

```csharp
    static SidebarPin P(string id) => new(id, SidebarEntryKind.AppRoute, "", id, 0);
    static SidebarUndoEntry PinLeaf(SidebarPinChange change, string id, int at)
        => new(0, SidebarUndoKind.Pin, SidebarLayoutId.Library, id, PinChange: change, Pin: P(id), PinFrom: at);

    [Fact]
    public void PinWhilePinnedHidden_IsOneEntry_TheToastsUndoHitsIt()
    {
        var ring = new SidebarUndoRing();
        var pin = PinLeaf(SidebarPinChange.Pinned, "search", 0);
        var show = new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Library, "show",
            Before: SidebarLayoutState.Default.Of(SidebarLayoutId.Library), After: SidebarLayoutState.Default.Of(SidebarLayoutId.Library));
        var batch = ring.Push(SidebarUndoEntry.Batch(SidebarLayoutId.Library, "Pin Search", [pin, show]));
        Assert.Equal(1, ring.Count);
        Assert.True(ring.IsTop(batch.Id));                     // the "Pinned is shown again · Undo" toast's check
        var undo = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: true, undo);
        Assert.Equal([SidebarUndoKind.Layout, SidebarUndoKind.Pin], undo.Select(e => e.Kind));   // re-hide, then unpin
        var redo = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: false, redo);
        Assert.Equal([SidebarUndoKind.Pin, SidebarUndoKind.Layout], redo.Select(e => e.Kind));
    }

    [Fact]
    public void ResetEverything_IsOneEntry_UndoRevertsAllThree()
    {
        var ring = new SidebarUndoRing();
        var classic = SidebarLayoutState.Default.Of(SidebarLayoutId.Classic);
        var library = SidebarLayoutState.Default.Of(SidebarLayoutId.Library);
        var batch = ring.Push(SidebarUndoEntry.Batch(SidebarLayoutId.Classic, "Reset everything",
        [
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Classic, "", Before: classic, After: classic),
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Library, "", Before: library, After: library),
            new SidebarUndoEntry(0, SidebarUndoKind.Density, SidebarLayoutId.Classic, "", DensityBefore: SidebarDensity.Compact),
        ]));
        Assert.True(ring.TryUndo(out var undone));
        Assert.Same(batch, undone);
        var leaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(undone, undo: true, leaves);
        Assert.Equal(3, leaves.Count);
        Assert.Equal(SidebarUndoKind.Density, leaves[0].Kind);
        Assert.False(ring.CanUndo);
    }

    [Fact]
    public void UnpinAllShortcuts_IsOneEntry_UndoReinsertsEveryPinInPlace()
    {
        // Pins [Search, x, Radio]; the batch unpinned from the last index down: Radio@2, then Search@0.
        var batch = SidebarUndoEntry.Batch(SidebarLayoutId.Library, "Unpin all shortcuts",
            [PinLeaf(SidebarPinChange.Unpinned, "radio", 2), PinLeaf(SidebarPinChange.Unpinned, "search", 0)]);
        var store = new List<string> { "x" };                   // what is left after the batch ran
        var leaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: true, leaves);
        foreach (var leaf in leaves) store.Insert(leaf.PinFrom, leaf.Pin!.Id);   // the service's Pins.Insert
        Assert.Equal(["search", "x", "radio"], store);
    }
```

(`using System.Linq;` at the top of the file for `Select`/`First`.)

`SetupTests.cs`/`SetupWizardTests.cs`: the page ladder facts (`NextPage`, `PrevPage`, `StepNumber`, `Progress`,
`ShowsBack`) for the four-page wizard, `Layout` skipped for `Reauth`/`TermsRearm`.

### P4 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P4-WP1 | new `Shell\Sidebar.Edit.cs` | §P4.1 (`AnnounceKey` on `Grab/Move/Drop/Cancel`, `SummaryKeyOf` with the Collections coupling summary), §P4.2 (`Pane(…, lockingPinNames)` with Library's Hide pinned / Unpin all shortcuts; `SidebarRootlistStep` + `Item`'s rootlist arm + `MoveRootlistUp/Down`), §P4.3 (`Batch`, `Parts`, `Flatten`, `IsRedoTop`, `KeyAllowed` doc) | — |
| P4-WP2 | `Shell\Sidebar.Host.cs`, `Shell\Sidebar.Accounts.cs`, `Platform\Drag.cs` | §P4.4 (Host.cs gains `using FluentGpu.Controls;` + `using FluentGpu.Localization;`; ring, `ApplyInverse`/`ApplyLeaf`, `PinRecorded` batch, `UnpinAllShortcutsRecorded`, `ResetEverythingRecorded`, `EnterEdit` REFUSED while `Drag.IsLive()` with the `sidebar.edit.finishDrag` toast, `ExitEdit`/`ExitEditCore`/`EditExitRestoresFocus`, `Dispatch(op, toastText)` recording, `ClearRing` on switches; `SwapAccount` → `ExitEditCore(false)` + `ClearRing()`; P3's `ResetEverything` deleted; `Drag.IsLive()` in Drag.cs) | P4-WP1 |
| P4-WP3 | new `Shell\Sidebar.UI.Edit.cs` | §P4.5 (copy the using header of `Sidebar.UI.cs` + `System.Globalization`; EditPane, EditBar, Outline with `Check` signals, header summaries via `SummaryKeyOf`, the stationary drag style, the Pinned block's "Unpin all shortcuts", the pins inner band in BOTH layouts with `OnPinKey`, focus into the first stop, chip, keys incl. `EditPane.OnPaneKey` Ctrl+Z/Ctrl+Y) | P4-WP1, P4-WP2 |
| P4-WP4 | `Shell\Sidebar.UI.cs`, `Shell\Sidebar.UI.Footer.cs` | §P4.4 `DefaultReorderCommit` → `MovePinRecorded(pf, pt)` (the code block; the one pin-reorder commit for Classic and Library), §P4.5 PaneView wiring (edit swap, inert rail, focus capture/restore by key, `FocusFooterMore`, `_footerMore`), §P4.6 `HeaderMenu`/`PaneMenu` (passes `SidebarMenus.LockingNames()`, sets `SidebarMenus.Overlay`) and the Ctrl+Z/Ctrl+Y arm at the head of `PaneView.OnPaneKey` (focused routing), §P4.8 toast action; never the disclosure members (§1) | P4-WP1, P4-WP2, P4-WP3 |
| P4-WP5 | `Shell\Sidebar.UI.Menus.cs`, `Shell\Sidebar.UI.Slot.cs`, `Shell\Sidebar.UI.Drop.cs` | §P4.6 (`SidebarMenus` incl. `Overlay`, `ConfirmResetEverything`, `LockingNames`, `SectionTitle`, `HideSection` by `Arg`, `ToggleKind` with the `sidebar.toast.hidden` toast on hide, unqualified `LayoutName(…)`, `UnpinAllShortcutsRecorded`, `Map(…, pane)`/`Run` rootlist arms, `HideItem`'s last-Collections-item toast; DELETE the `LayoutMenu` class (keep `Sidebar.LayoutName`); row-menu additions incl. `FolderMenu`, `RootlistStepOf`, `MoveSibling` internal, `NavExtras` without move rows; header ⋯, pin toasts → ring) | P4-WP1, P4-WP2 |
| P4-WP6 | `Shell\Sidebar.UI.LibraryV3.cs` | V3 overflow's layout submenu → `SidebarMenus.Map(SidebarMenuModel.Pane(Layout.Peek(), State, Density.Peek(), Editing.Peek(), SidebarMenus.LockingNames()))` (no `LayoutMenu.Rows` left). `V3Session.CommitReorder` is NOT edited: it calls `DefaultReorderCommit`, which records from P4-WP4 on (§P4.4) | P4-WP1, P4-WP5 |
| P4-WP7 | `Shell\Shell.UI.cs`, `Shell\Shell.Palette.cs` | §P4.5 scrim, §P4.6 `FocusedIsTextEditor` internal static (NO undo chord: the keys are the sidebar's focused handlers) + seam menu → the P4 pane menu (no `LayoutMenu.Model`), §P4.7 palette | P4-WP2, P4-WP5 |
| P4-WP8 | `Screens\Setup.cs`, `Screens\Setup.UI.cs`, `Screens\Settings.UI.Appearance.cs` | §P4.7 wizard step; §P4.7a (Settings card: "Edit sidebar…" and "Reset everything…" items disabled while editing with the `sidebar.pane.finishEditing` subtitle, reset-layout subtitle, `SidebarLayoutCards` reason caption; the card keeps `Sidebar.LayoutName`) | P4-WP2, P4-WP5 |
| P4-WP9 | `assets\loc\en-US.json` | §P4.9 (incl. `menu.hidePinned`, `toast.shortcutsUnpinned`/`resetEverything`, `undo.label.unpinShortcuts`/`resetEverything`, `edit.showInSidebar`, `edit.finishDrag`, `edit.collectionsSummary`/`collectionsLastSummary` + the English of `pinsSummary`/`playlistsSummary`, `section.title.home`/`.settings`, `undo.redid`, `settings.sidebar.editSub`/`resetEverythingSub`) | — |
| P4-WP10 | new `SidebarEditRulesTests.cs`, `SidebarMenuModelTests.cs`, `SidebarUndoRingTests.cs`; `SetupTests.cs`, `SetupWizardTests.cs` | §P4.10 (incl. `Outline_Library_PinsInnerBand`, `Summary_Collections_NamesTheCoupling`, `Pane_ShowSection_ListsHiddenSettingsAndPinned` with `AssertEveryLabelKeyResolves`, `PinRefusalKeys_Resolve`, `Item_RootlistRow_MovesOnlyWhileTheRootlistOrderIsShown`, `RingToasts_…`, the Library pane-menu facts, the three one-action-one-entry facts; the `SetupTests`/`SetupWizardTests` page-ladder facts test P4-WP8's `Setup.cs`) | P4-WP1, P4-WP2, P4-WP8, P4-WP9 |

---

## P5. Library: the P.2a head

The owner picked option E (design P.2a): the "Your Library ▾" dropdown **navigates only**; filtering is the chips plus
one toolbar. This phase replaces the V3 chrome (`Sidebar.UI.LibraryV3.cs`, 1 782 lines) with a ~170-px head, removes
the P3-P4 bridge, and deletes every `LibraryV3*` / `SidebarV3*` type.

```
 x (pane): 0 4      24       48                                         W-44   W-18 W-4
    ┌──────────────────────────────────────────────────────────────────────────────┐ 0
    │▌ ⌂     Home                                                                   │ 3     Home row A (36 + 2·2)
    │──────────────────────────────────────────────────────────────────────────────│ 43    separator 8
    │▌  [Your Library · Albums ⌄]                                                   │ 51    dropdown row 40 (navigation)
    │   (Playlists) (Albums) (Artists) (Podcasts) (Audiobooks)                ›     │ 91    chips 32 in 40, r16, gap 8
    │   [⌕] Recents ⇅                                    [≡][▦]  [+] [⋯]           │ 131   toolbar 32 in 40
    │──────────────────────────────────────────────────────────────────────────────│ 171   rule 1 (StrokeDividerDefault)
    │  [art] Discover Weekly                                            📌          │ 172   pins: list top, pin mark, no header
    │   ⌕    Search                                                     📌          │
    │  [♡]   Liked Songs                                                            │       Liked first (no chip / Playlists)
    │  [art] Running                                                                │       the list in the current sort
    │          ⋮ (virtualized)                                                      │
    │──────────────────────────────────────────────────────────────────────────────│
    │   ⚙    Settings                                                     [⋯]       │       footer
    └──────────────────────────────────────────────────────────────────────────────┘
 search open:
    │   [⌕ Search in Your Library ………………………… ✕]                        [⇅]           │ 131   the same 40 slot
```

### P5.1 Pure rules (new file `Shell/Sidebar.Library.cs`)

```csharp
// ── Shell/Sidebar.Library.cs ───────────────────────────────────────────────────────────────────────────────────────
// Your Library's head (design P.2a) and list shaping: the page dropdown, the chips, the toolbar's search state, the
// grid's columns, the head↔list keyboard handoff — and the list shaper (ex LibraryV3View) and the pin window
//
// Role: CORE · Spec: sidebar-rework-implementation.md §P5.1 · design P.2, P.2a, V.11, Q11-Q13

namespace Wavee;

/// <summary>One library page in the "Your Library ▾" menu: its route key, the kind item it stands for, its title key,
/// its glyph and its count — null while unknown (the menu then shows no number, design D9), a real 0 once known. The menu
/// NAVIGATES; it never filters (design P.2a).</summary>
public readonly record struct SidebarLibraryPage(string Route, string Item, string TitleKey, string Glyph, int? Count);

/// <summary>The four kind counts the dropdown shows (from the binder's full projection, before any chip or search).
/// <see cref="Known"/> is false until the library projection is Ready: <c>default</c> is "unknown", never "0 albums".</summary>
public readonly record struct SidebarLibraryCounts(int Albums, int Artists, int Podcasts, int Audiobooks, bool Known)
{
    public int? Of(string item) => !Known ? null : item switch
    {
        "albums" => Albums,
        "artists" => Artists,
        "podcasts" => Podcasts,
        "audiobooks" => Audiobooks,
        _ => 0,
    };
}

public static class SidebarLibraryHeadRules
{
    public const float HomeRowHeight = 40f;       // row A 36 + 2·2 margin
    public const float SeparatorHeight = 8f;
    public const float DropdownRowHeight = 40f;
    public const float ChipRowHeight = 40f;
    public const float ChipHeight = 32f;
    public const float ChipGap = 8f;
    public const float ToolbarHeight = 40f;
    public const float ToolbarControl = 32f;
    public const float RuleHeight = 1f;
    /// <summary>The dropdown title's x in pane space (design P.2a: "at the header's x = 16") and its 8-px hit inset.</summary>
    public const float TitleX = 16f;
    public const float TitleHitInset = 8f;
    public const float TitleChevron = 10f;
    /// <summary>The chip row's and the toolbar's lead/trail inset in pane space (the chips' first edge sits under the title).</summary>
    public const float BandInsetX = 12f;
    /// <summary>The whole head above the list: 3 (top inset) + 40 + 8 + 40 + 40 + 40 + 1 = 172.</summary>
    public const float HeadHeight = SidebarRowGeometry.PaneTopInset + HomeRowHeight + SeparatorHeight + DropdownRowHeight
                                    + ChipRowHeight + ToolbarHeight + RuleHeight;

    /// <summary>The grid's columns (design P.2: 2–4 from the pane width, no size picker): as many 116-px cells with 8-px
    /// gaps as the lane fits, clamped to [2, 4]. The lane is the pane minus the list's 4+4 pad and the 8+8 strip inset.</summary>
    public const float GridMinCell = 116f;
    public const float GridGap = 8f;
    public static int GridColumns(float paneWidth)
    {
        float lane = paneWidth - 8f - 16f;
        if (!float.IsFinite(lane) || lane <= 0f) return 2;
        int n = (int)System.MathF.Floor((lane + GridGap) / (GridMinCell + GridGap));
        return System.Math.Clamp(n, 2, 4);
    }

    /// <summary>The kind items in their catalogue order ("albums", "artists", "podcasts", "audiobooks").</summary>
    static readonly string[] Items = ["albums", "artists", "podcasts", "audiobooks"];
    static readonly string[] TitleKeys = ["nav.albums", "nav.artists", "nav.podcasts", "nav.audiobooks"];

    /// <summary>The dropdown's pages: every kind not hidden through Filters (Q12), in catalogue order, with its count.
    /// Glyphs are the destinations' own (the caller passes <paramref name="glyphOf"/> = <c>Shell.Dest(route).Glyph</c>).</summary>
    public static void Pages(SidebarLibraryKinds hidden, in SidebarLibraryCounts counts, System.Func<string, string> glyphOf,
                             List<SidebarLibraryPage> into)
    {
        into.Clear();
        for (int i = 0; i < Items.Length; i++)
        {
            if ((hidden & SidebarCatalogue.KindFlagOf(Items[i])) != 0) continue;
            into.Add(new SidebarLibraryPage(Items[i], Items[i], TitleKeys[i], glyphOf(Items[i]), counts.Of(Items[i])));
        }
    }

    /// <summary>Is the title a menu button? Not when every kind is hidden through Filters (a legal state: the library
    /// section is locked, so hiding kinds never hides it): with no page to offer, the title is plain text — no chevron, no
    /// button role, no empty flyout.</summary>
    public static bool DropdownIsMenu(int pageCount) => pageCount > 0;

    /// <summary>The page the current route is, or null. A hidden kind's page is not a library page (its route still works).</summary>
    public static string? PageOf(string route, SidebarLibraryKinds hidden)
    {
        for (int i = 0; i < Items.Length; i++)
            if (string.Equals(route, Items[i], System.StringComparison.Ordinal))
                return (hidden & SidebarCatalogue.KindFlagOf(Items[i])) != 0 ? null : Items[i];
        return null;
    }

    /// <summary>The dropdown carries the pill (pill rule 3, the lowest visible ancestor) only while the route is one of
    /// its pages AND no list row already carries it (a route pin "albums" is an exact match, rule 1).</summary>
    public static bool DropdownCarriesPill(string route, SidebarLibraryKinds hidden, bool rowCarriesPill)
        => !rowCarriesPill && PageOf(route, hidden) is not null;

    /// <summary>The chips (Q12: a hidden kind has none), Playlists first.</summary>
    public static void Chips(SidebarLibraryKinds hidden, List<SidebarLibraryFilter> into)
    {
        into.Clear();
        for (var f = SidebarLibraryFilter.Playlists; f <= SidebarLibraryFilter.Audiobooks; f++)
            if (SidebarLibraryFilters.HasChip(f, hidden)) into.Add(f);
    }

    /// <summary>Click an active chip (or its ✕) to clear it; any other chip replaces it. One active at a time.</summary>
    public static SidebarLibraryFilter Toggle(SidebarLibraryFilter current, SidebarLibraryFilter chip)
        => current == chip ? SidebarLibraryFilter.None : chip;

    /// <summary>The chip's label key.</summary>
    public static string ChipKey(SidebarLibraryFilter f) => f switch
    {
        SidebarLibraryFilter.Playlists => "sidebar.chip.playlists",
        SidebarLibraryFilter.Albums => "nav.albums",
        SidebarLibraryFilter.Artists => "nav.artists",
        SidebarLibraryFilter.Podcasts => "nav.podcasts",
        SidebarLibraryFilter.Audiobooks => "nav.audiobooks",
        _ => "",
    };

    public enum SearchEscape : byte { Clear = 0, Close = 1 }

    /// <summary>Esc in the box: a query clears first; an empty box closes back to the toolbar (design P.2a).</summary>
    public static SearchEscape OnEscape(string text) => text.Length > 0 ? SearchEscape.Clear : SearchEscape.Close;

    /// <summary>An empty box that lost focus closes; a query stays on screen.</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The toolbar's sort label key (the button reads "Recents", "A–Z", …).</summary>
    public static string SortKey(SidebarLibrarySort sort) => "sidebar.sort." + SidebarStoreV3.SortName(sort);

    /// <summary>Is Custom order offered? Only under the Playlists chip (it IS the rootlist order, design P.2).</summary>
    public static bool CustomOrderOffered(SidebarLibraryFilter filter) => filter == SidebarLibraryFilter.Playlists;

    /// <summary>The sort that applies: Custom order outside the Playlists chip falls back to Recents (the stored sort is kept).</summary>
    public static SidebarLibrarySort Effective(SidebarLibrarySort stored, SidebarLibraryFilter filter)
        => stored == SidebarLibrarySort.CustomOrder && !CustomOrderOffered(filter) ? SidebarLibrarySort.Recents : stored;

    // ── the toolbar's shape at the pane width (design: the sort/view row keeps icons only below 240) ─────────────────

    /// <summary>IconButton's <c>ControlSize.Small</c> box (search, the icon-only sort, ⋯).</summary>
    public const float ToolbarIconButton = 28f;
    public const float ToolbarGap = 4f;
    /// <summary>The labelled sort button's chrome around its label: 8 + 16 glyph + 8 gap + 8.</summary>
    public const float SortButtonChrome = 40f;
    /// <summary>Below this pane width List/Grid leave the toolbar (they stay in ⋯ › View, which LibraryOptions always has).</summary>
    public const float FoldViewBelow = 240f;

    /// <summary>Full: [⌕][⇅ label] · [≡][▦][+][⋯]. IconSort: the sort button is icon-only (as while searching). Folded:
    /// icon-only sort AND no List/Grid toggles — the ⋯ (which holds Filters, View and Show Liked Songs) must never be the
    /// control that gets clipped.</summary>
    public enum ToolbarShape : byte { Full = 0, IconSort = 1, Folded = 2 }

    /// <summary>The toolbar's content lane: the pane minus the head's 4+4 pad and the toolbar's own lead inset (8).</summary>
    public static float ToolbarLane(float paneWidth) => paneWidth - 2f * SidebarRowGeometry.PaneEdge - (BandInsetX - SidebarRowGeometry.PaneEdge);

    /// <summary>The summed control widths plus gaps for a shape (the spacer is 0 at the minimum). The label is measured
    /// with the row label's estimate (<see cref="SidebarLabelFit.AverageCharWidth"/>).</summary>
    public static float ToolbarWidth(ToolbarShape shape, string sortLabel)
    {
        float sort = shape == ToolbarShape.Full ? SortButtonChrome + sortLabel.Length * SidebarLabelFit.AverageCharWidth : ToolbarIconButton;
        float view = shape == ToolbarShape.Folded ? 0f : 2f * ToolbarControl;
        int controls = shape == ToolbarShape.Folded ? 4 : 6;   // search, sort, [List, Grid,] +, ⋯
        return ToolbarIconButton + sort + view + ToolbarControl + ToolbarIconButton + (controls - 1) * ToolbarGap;
    }

    /// <summary>The widest shape that fits: Folded below <see cref="FoldViewBelow"/>; the label only when the whole row
    /// with it fits the lane ("Recently added" at the 320 default does not: icon-only there).</summary>
    public static ToolbarShape ShapeOf(float paneWidth, string sortLabel)
    {
        if (!float.IsFinite(paneWidth) || paneWidth < FoldViewBelow) return ToolbarShape.Folded;
        return ToolbarWidth(ToolbarShape.Full, sortLabel) <= ToolbarLane(paneWidth) ? ToolbarShape.Full : ToolbarShape.IconSort;
    }

    // ── the keyboard ring (design V.11, P.2a) ────────────────────────────────────────────────────────────────────────

    /// <summary>The head's ring part (Home ↔ dropdown). Down from the dropdown leaves the head for the list's first row;
    /// with an empty list it stays. Up from Home stays.</summary>
    public enum HeadStop : byte { Home = 0, Dropdown = 1, List = 2 }

    public static HeadStop Next(HeadStop from, int direction, int listCount) => (from, direction) switch
    {
        (HeadStop.Home, > 0) => HeadStop.Dropdown,
        (HeadStop.Dropdown, > 0) => listCount > 0 ? HeadStop.List : HeadStop.Dropdown,
        (HeadStop.Dropdown, < 0) => HeadStop.Home,
        (HeadStop.List, < 0) => HeadStop.Dropdown,
        _ => from,
    };

    // ── the Tab order (Q11: "match the screen; add the test") ────────────────────────────────────────────────────────

    /// <summary>One Tab stop of Your Library's head, in screen order. The chips strip is ONE stop (it roves inside).</summary>
    public enum HeadTabStop : byte { Home = 0, Dropdown = 1, Chips = 2, SearchBox = 3, Search = 4, Sort = 5, ListView = 6, GridView = 7, Create = 8, More = 9, List = 10 }

    /// <summary>The head's Tab order — exactly its left-to-right, top-to-bottom screen order (Q11): Home, the dropdown, the
    /// chips, the toolbar (search or the open box, sort, List, Grid, +, ⋯; the view toggles only when the shape keeps
    /// them; the open box replaces search and the right-hand group), then the list. <c>LibraryToolbar</c> BUILDS its
    /// controls by walking this list, so the order the fact pins is the order on screen.</summary>
    public static void TabOrder(ToolbarShape shape, bool searching, List<HeadTabStop> into)
    {
        into.Clear();
        into.Add(HeadTabStop.Home);
        into.Add(HeadTabStop.Dropdown);
        into.Add(HeadTabStop.Chips);
        if (searching)
        {
            into.Add(HeadTabStop.SearchBox);
            into.Add(HeadTabStop.Sort);
        }
        else
        {
            into.Add(HeadTabStop.Search);
            into.Add(HeadTabStop.Sort);
            if (shape != ToolbarShape.Folded)
            {
                into.Add(HeadTabStop.ListView);
                into.Add(HeadTabStop.GridView);
            }
            into.Add(HeadTabStop.Create);
            into.Add(HeadTabStop.More);
        }
        into.Add(HeadTabStop.List);
    }
}

/// <summary>Why Your Library's scroller is empty — or <see cref="None"/> when it has a row (a pin, Liked Songs, a list row) or
/// is still loading (skeletons).</summary>
public enum SidebarLibraryEmpty : byte { None = 0, Library = 1, Filter = 2, Search = 3, Failed = 4 }

/// <summary>Your Library's empty state has ONE owner, the head (§P5.5); the planner plans no Empty row in this layout
/// (§P3.5). Both read the same rows: <see cref="ScrollerRows"/> counts exactly what <c>SidebarRowPlanner.PlanLibrary</c>
/// emits, so "No albums" never sits above a list, and "Your library is empty" never sits above Liked Songs.</summary>
public static class SidebarLibraryEmptyRules
{
    /// <summary>The scroller's content rows: the pins the band keeps (<see cref="SidebarVisibilityRules.ShowsPinInLibrary"/>,
    /// only while Pinned is shown), the Liked row (<see cref="SidebarVisibilityRules.ShowsLiked"/>), the shaped list. While
    /// <paramref name="drilled"/> the level is the folder's children only — no pins, no Liked — exactly as PlanLibrary
    /// plans it under <c>SidebarPlanOptions.Drilled</c>, so an empty folder reads as empty.</summary>
    public static int ScrollerRows(IReadOnlyList<SidebarLibraryEntry>? pins, bool pinnedShown, SidebarLibraryOptions options,
                                   SidebarLibraryFilter filter, string? search, string? likedTitle, int listCount,
                                   bool drilled = false)
    {
        int rows = listCount;
        if (SidebarVisibilityRules.ShowsLiked(options, filter, search, likedTitle, drilled)) rows++;
        if (pinnedShown && !drilled && pins is not null)
            for (int i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (SidebarVisibilityRules.ShowsPinInLibrary(in pin, filter, search, options.HiddenKinds)) rows++;
            }
        return rows;
    }

    /// <summary>The empty state, in V3's priority order: a failure, then the search, then the chip, then the library itself.
    /// Pending (or any contributing kind pending) shows skeletons, never an empty state.</summary>
    public static SidebarLibraryEmpty Of(int scrollerRows, LoadState load, bool anyPending, SidebarLibraryFilter filter, bool searching)
    {
        if (scrollerRows > 0 || anyPending || load == LoadState.Pending) return SidebarLibraryEmpty.None;
        if (load == LoadState.Failed) return SidebarLibraryEmpty.Failed;
        if (searching) return SidebarLibraryEmpty.Search;
        return filter != SidebarLibraryFilter.None ? SidebarLibraryEmpty.Filter : SidebarLibraryEmpty.Library;
    }
}
```

(`LoadState` is the engine's `FluentGpu.Signals.LoadState` — the entry cell's `State`: add `using FluentGpu.Signals;` at the
top of `Sidebar.Library.cs`. The engine's foundation types are plain enums: the file stays a pure CORE file and
Wavee.Tests already references the engine through Wavee.)

**The list shaper and the pin window** move into the same file from `Sidebar.Modes.cs`, renamed, bodies verbatim
except for the noted edits:

- `LibraryV3View` → `public sealed class SidebarLibraryShaper`. `Build(published, skip, tree, revision, drillFolderId,
  group)` keeps its signature. (Its XML doc's "V3" wording becomes "Your Library".)
- `LibraryV3Window` → `public sealed class SidebarLibraryWindow`.
- From `LibraryV3Metrics` only these survive, into `public static class SidebarLibraryMetrics`: `DrillInWidth` — **240**
  (design P.2 "inline tree (≥ 240 px) or drill-in", corner case "Pane at 180 px … drill-in replaces the inline tree below
  240", the preview's `presentedPaneW() >= 240`; today's code value 320 is NOT kept, so a 240–319 px pane now shows the
  inline tree; the drawer still always drills) with the doc comment `/// <summary>Below this pane width Your Library
  drills into folders instead of disclosing them inline (design P.2: 240). The drawer always drills.</summary>`,
  `BreadcrumbHeight` (32), `HasQuery(string?)`, and `FoldersApply(in SidebarLibraryState state)` (the moved
  `LibraryV3Document.FoldersApply` body, retyped as below).

```csharp
/// <summary>The session's shaping state (replaces LibraryV3DocState): the chip, the stored sort, the view, searching,
/// the drill and the pin presence.</summary>
public readonly record struct SidebarLibraryState(
    SidebarLibraryFilter Filter,
    SidebarLibrarySort Sort,
    bool Descending,
    SidebarLibraryView View,
    int GridColumns,
    bool Searching,
    string? DrillFolderId,
    bool HasPins)
{
    public bool Drilled => DrillFolderId is { Length: > 0 };

    /// <summary>Folders group (inline tree) only for a playlist-capable chip, a list view, no search and no drill (a drilled
    /// level is one folder's direct children) — the old <c>LibraryV3Document.FoldersApply</c> body, retyped.</summary>
    public bool FoldersApply
        => !Searching && !Drilled && View == SidebarLibraryView.List
           && Filter is SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists;
}
```

### P5.2 The binder: counts, no qualifier (`Shell/Sidebar.Host.cs`, `Shell/Sidebar.cs`)

`Shell/Sidebar.cs`:

- `SidebarLibraryQuery` loses `Qualifier` and `QualifiersAvailable`:

```csharp
/// <summary>Your Library's shaping state: the chip, the hidden kinds, the sort and the search.</summary>
public readonly record struct SidebarLibraryQuery(
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    SidebarLibraryKinds HiddenKinds = SidebarLibraryKinds.None,
    SidebarLibrarySort Sort = SidebarLibrarySort.Recents,
    bool Descending = false,
    string? Search = null);
```

- `SidebarBinderPipeline.Shape`: delete `byte qualifier = …;`, the `qualifier != 0` term of the gate and the qualifier
  clause; everything else is P3's body. The whole method after P5 (transcribe it, do not merge by hand):

```csharp
    public static SidebarEntriesShape Shape(
        List<SidebarLibraryEntry> list,
        List<SidebarLibraryEntry> scratch,
        in SidebarLibraryQuery query,
        IReadOnlyList<SidebarPin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scratch);

        string search = SidebarSearch.Normalize(query.Search);
        bool searching = search.Length > 0;
        // Not only while searching: the kind mask cannot split Podcasts from Audiobooks (both are Show) and does not know the
        // kinds hidden through Filters, so the loop also runs for a hidden kind and for those two chips.
        bool narrow = searching || query.HiddenKinds != SidebarLibraryKinds.None
                      || query.Filter is SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks;

        if (narrow)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var e = list[read];
                if (SidebarLibraryFilters.IsHidden(query.HiddenKinds, in e)) continue;
                if (query.Filter != SidebarLibraryFilter.None && !SidebarLibraryFilters.Matches(query.Filter, in e)) continue;
                if (searching)
                {
                    // Searching FLATTENS: matching leaves only, no folder chrome.
                    if (e.Kind == SidebarEntryKind.Folder) continue;
                    if (!SidebarSearch.Matches(in e, search)) continue;
                }
                list[write++] = e;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        // No custom-order overlay: Custom order is the rootlist's own source order.
        SidebarSort.Apply(list, SidebarSort.Effective(query.Sort, query.Filter), query.Descending, null);
        int band = SidebarProjection.PinsFirst(list, pins, scratch);
        return new SidebarEntriesShape(list.Count, band);
    }
```

- `SidebarNavLayout` is deleted (P4.6 moved every positional Move row into `SidebarMenuModel.Item`, so nothing in the app
  calls it); P5-WP8 deletes its `SidebarNavLayoutTests` class from `SidebarDropTests.cs`.
- `SidebarLibraryEntry.MatchesQualifier(SidebarV3Qualifier)` (the overload) is deleted; `MatchesQualifier(byte)` stays
  (the Core query path reads it). `SidebarProjection.QualifiersAvailable` and `SidebarEntries.QualifiersAvailable` STAY:
  `Entries.Publish` still carries the flag; nothing in the UI reads it after P5.

`Shell/Sidebar.Host.cs` (`SidebarProjectionBinder`):

- A field and a property: `SidebarLibraryCounts _counts;` and

```csharp
    /// <summary>The kind counts over the FULL projection (before chip, search and hidden kinds), for the page dropdown.
    /// Moves through <see cref="InputVersion"/>.</summary>
    public SidebarLibraryCounts Counts => _counts;
```

- In `Rebuild`, right after `_libraryState = Worst(…);` (the counts read `build.All`, the projection step 1 filled):

```csharp
        int albums = 0, artists = 0, podcasts = 0, audiobooks = 0;
        var all = build.All;
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            switch (e.Kind)
            {
                case SidebarEntryKind.Album: albums++; break;
                case SidebarEntryKind.Artist: artists++; break;
                case SidebarEntryKind.Show when e.IsAudiobook: audiobooks++; break;
                case SidebarEntryKind.Show: podcasts++; break;
            }
        }
        var counts = new SidebarLibraryCounts(albums, artists, podcasts, audiobooks,
                                              Known: _libraryState == SidebarSourceState.Ready);
        bool countsMoved = counts != _counts;
        _counts = counts;
```

- the query line becomes

```csharp
        var query = new SidebarLibraryQuery(filter, library ? options.HiddenKinds : SidebarLibraryKinds.None,
            SidebarLibraryHeadRules.Effective(options.Sort, filter), options.Descending, search);
```

- `bool inputChanged = PublishInput();` becomes
  `bool inputChanged = PublishInput(countsMoved || !string.Equals(search, _input.Search ?? "", StringComparison.Ordinal));`
  (the line runs BEFORE `_input` is replaced, so `_input.Search` is the previous rebuild's query). `PublishInput` becomes
  `bool PublishInput(bool headMoved)` and its first line `bool changed = _pinShadow.Publish(_pinRows, default);` becomes
  `bool changed = headMoved | _pinShadow.Publish(_pinRows, default);` (non-short-circuit `|`: the shadow must publish
  either way). The `_inputVersion` bump below it then covers the counts AND the query the list was shaped with: the head's
  empty state reads that query (§P5.5), and a search whose result set did not change (no results → still no results)
  moves no entry cell.
- `Read()`: `V3State: SidebarBinderTriggers.PackV3((int)doc.Layout, (int)Sidebar.LibraryFilter.Peek(), 0, (int)doc.Library.Sort,
  doc.Library.Descending),` (the qualifier slot packs 0).

`Shell/Sidebar.UI.cs` `PumpBinder` (P5-WP5): delete the `Sidebar.V3Qualifier.Value` read.

### P5.3 The bridge goes (`Shell/Sidebar.Host.cs`, `Platform/Platform.cs`)

Delete `V3Filter`, `V3Qualifier`, `V3Sort`, `V3Desc`, `V3View`, `V3GridSize`, `V3Search`, `SyncV3Mirrors` and its four
call sites, `SetV3Filter`, `SetV3Qualifier`, `SetV3Sort`, `SetV3View`, `SetV3GridSize`, and the `V3Qualifier` seed line
in `Boot`. `Platform.Keys.V3Qualifier` is deleted (the migration reads the old v3 keys through its own private
`SettingKey`s, §P3.10; it never read the qualifier). One service setter is added for the toolbar:

```csharp
    /// <summary>The toolbar's sort button and the sort menu: a sort (and its direction) through the one op.</summary>
    public static void SetLibrarySort(SidebarLibrarySort sort, bool descending)
        => Dispatch(new SetLibrarySort(sort, descending));

    /// <summary>List ↔ Grid from the toolbar toggles.</summary>
    public static void SetLibraryView(SidebarLibraryView view) => Dispatch(new SetLibraryView(view));
```

### P5.4 The menus (`Shell/Sidebar.Edit.cs`)

Add `SidebarMenuAction.ToggleDescending` and `SidebarMenuAction.NavigatePage`, and two builders:

```csharp
    /// <summary>The toolbar's sort button: the five sorts (Custom order only under Playlists, Q-P.2) · ─ · Reverse order.</summary>
    public static IReadOnlyList<SidebarMenuRow> Sort(SidebarLayoutState state, SidebarLibraryFilter filter)
    {
        var lib = state.Library.Find("library") ?? SidebarCatalogue.DefaultState(SidebarSectionKind.Library);
        var stored = lib.Sort ?? SidebarLibrarySort.Recents;
        var sort = SidebarLibraryHeadRules.Effective(stored, filter);
        bool custom = SidebarLibraryHeadRules.CustomOrderOffered(filter);
        var rows = new List<SidebarMenuRow>(7);
        for (int i = 0; i <= (int)SidebarLibrarySort.CustomOrder; i++)
        {
            var sv = (SidebarLibrarySort)i;
            bool isCustom = sv == SidebarLibrarySort.CustomOrder;
            rows.Add(new SidebarMenuRow(SidebarMenuAction.SetSort, SidebarLibraryHeadRules.SortKey(sv), SidebarStoreV3.SortName(sv),
                Enabled: !isCustom || custom, Checked: sort == sv, Radio: true,
                ReasonKey: isCustom && !custom ? "sidebar.sort.customOnlyPlaylists" : null));
        }
        rows.Add(SidebarMenuRow.Divider);
        rows.Add(new SidebarMenuRow(SidebarMenuAction.ToggleDescending, "sidebar.sort.reverse",
            Enabled: sort != SidebarLibrarySort.CustomOrder, Checked: lib.Descending ?? false));
        return rows;
    }

    /// <summary>The "Your Library ▾" page menu: one row per page (glyph + count), the current page checked. Navigation only.</summary>
    public static IReadOnlyList<SidebarMenuRow> Pages(IReadOnlyList<SidebarLibraryPage> pages, string? currentPage)
    {
        var rows = new List<SidebarMenuRow>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
            rows.Add(new SidebarMenuRow(SidebarMenuAction.NavigatePage, pages[i].TitleKey, pages[i].Route,
                Checked: string.Equals(pages[i].Route, currentPage, System.StringComparison.Ordinal)));
        return rows;
    }
```

`LibraryOptions` (P4) changes its Sort child to `Sort(state, filter)`. `SidebarMenus.Run` (`Sidebar.UI.Menus.cs`) adds:
`ToggleDescending` → `SetLibrarySort(Doc.Library.Sort, !Doc.Library.Descending)`; `SetSort` keeps the stored direction.
A second mapper gives page rows their glyph and count:

```csharp
        /// <summary>The page menu: each row is a radio (the current page checked) wearing its page's glyph, with the count in the
        /// trailing accelerator column — none while the count is unknown (design D9), "0" for a genuine zero. Choosing a row
        /// NAVIGATES through <paramref name="navigate"/> (the session's <c>Navigate</c>); it never touches the filter.</summary>
        public static IReadOnlyList<MenuFlyoutItem> MapPages(IReadOnlyList<SidebarMenuRow> rows, IReadOnlyList<SidebarLibraryPage> pages,
                                                            Action<string> navigate)
        {
            var items = new List<MenuFlyoutItem>(rows.Count);
            for (int i = 0; i < rows.Count && i < pages.Count; i++)
            {
                var page = pages[i];
                string route = page.Route;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(rows[i].LabelKey), rows[i].Checked, () => navigate(route), page.Glyph) with
                {
                    AcceleratorText = page.Count is { } n ? FormatCache.Int(n) : null,
                });
            }
            return items;
        }
```

(`MenuFlyoutItem.AcceleratorText` is the engine's trailing right-aligned text column; `IconRef` converts from the glyph
string implicitly. `SidebarMenuAction.NavigatePage` stays in the model for the rows' identity; `Run` has no arm for it —
`MapPages` binds the navigation itself.)

### P5.5 The UI (new file `Shell/Sidebar.UI.Library.cs`; `Shell/Sidebar.UI.LibraryV3.cs` is deleted)

**The prop rule this file follows** (fluentgpu SKILL rule 2): a keyed child's factory runs ONCE; a parent re-render
reuses the instance and discards the new factory. So every component below takes ONLY the session `s` (a
reference-stable mount seed) and reads its changing state — `Sidebar.LibrarySearchOpen`, `LayoutVersion`/`Doc.Library`,
`LibraryFilter`, `Shell.Current`, `Binder.InputVersion`, `s.RowPill`, `s.DrillVersion` — from signals in its OWN
`Render`. Nothing changing is ever passed through a constructor.

```csharp
// ── Shell/Sidebar.UI.Library.cs ────────────────────────────────────────────────────────────────────────────────────
// Your Library (design P.2a): the mode's PaneConfig, the session (shaping, drill, reorder rules) and the fixed head —
// Home, the "Your Library ▾" page dropdown, the chips and the toolbar
//
// Role: UI · Spec: sidebar-rework-implementation.md §P5.5 · design P.2, P.2a, V.5, V.9, V.11

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>The Library layout's mount: one <see cref="PaneView"/> with the head above the list.</summary>
    internal sealed class LibraryMode : Component
    {
        /// <summary>0.2.9's binder-pump search debounce (<c>SidebarBinderPump.SearchDebounceMs</c>).</summary>
        const float SearchDebounceMs = 90f;

        readonly bool _inDrawer;   // mount configuration: a drawer and a docked pane are separate mounts

        public LibraryMode(bool inDrawer) => _inDrawer = inDrawer;

        public override Element Render()
        {
            bool inDrawer = _inDrawer;
            var s = UseMemo(() => new LibrarySession(inDrawer), DepKey.Empty);

            // MEMOS, not raw width reads: a seam drag writes Width every frame; the column count / folder mode flip rarely.
            s.Columns = UseComputed(static () => SidebarLibraryHeadRules.GridColumns(Sidebar.Width.Value));
            // The drawer always drills; a docked pane drills below 240 px (design P.2) and shows the inline tree from 240 up.
            bool narrow = UseComputed(() => inDrawer || Sidebar.Width.Value < SidebarLibraryMetrics.DrillInWidth).Value;
            // Inline disclosure and a drill stack answer the same question, so only one may be live. Signal writes ⇒ effect.
            UseLayoutEffect(() =>
            {
                s.NarrowFolders.SetIfChanged(narrow);
                if (!narrow) s.ResetDrill();
            }, DepKey.From(narrow));

            // The binder has no pump of its own: the debounced search text and the folder edits resync here.
            var search = UseDebouncedValue(LibrarySearch, SearchDebounceMs);
            UseEffect(() =>
            {
                _ = search.Value;
                _ = FolderVersion.Value;
                LibrarySession.Resync();
            });

            var config = UseMemo(() => new PaneConfig
            {
                Layout = SidebarLayoutId.Library,
                ScrollKeyPrefix = "sidebar.library",
                Document = static () => { _ = LayoutVersion.Value; return Doc; },
                Input = s.ShapeInput,
                ModeEpoch = s.ReadModeEpoch,
                Options = s.ShapeOptions,
                Head = s.Head,
                TreeSortedNonCustom = s.TreeSortedNonCustom,
                SortedListRefusalAction = s.SwitchToCustomSortForReorder,
                ClampReorderSlot = s.ClampReorderSlot,
                CommitReorder = s.CommitReorder,
                ActivateFolder = s.ActivateFolder,
                DisclosesFoldersInline = s.DisclosesFoldersInline,
                ShowsSettings = static () => Doc.ShowsSettings,
                OnEdgeNavigate = s.OnListEdge,
                PillOnRowChanged = s.NotePillOnRow,
                OpenSearch = s.OpenSearch,
                // Null: the toolbar's "+" calls PaneView.CreatePlaylist() — the pane's ONE create flow.
                OnCreatePlaylist = null,
            }, DepKey.Empty);

            // The factory runs once at mount (the LibraryV3Mode shape, kept): the session holds the pane so the head's "+"
            // reuses the pane's own HeaderCreateDropSpec / CreateMenu / CreatePlaylist and the ring can focus the list.
            return Embed.Comp(() =>
            {
                var pane = new PaneView(config, inDrawer);
                s.PaneRef = pane;
                return pane;
            });
        }
    }
}
```

**`PaneConfig` gains** three members (`Sidebar.UI.cs`, P5-WP5):

```csharp
        /// <summary>Arrow navigation ran off an END of the list (−1 above the first row, +1 below the last). Return true when
        /// the mode took focus (Library: Up from the first row lands on the page dropdown, design V.11).</summary>
        public Func<int, bool>? OnEdgeNavigate { get; init; }
        /// <summary>Whether a LIST row carries the pill now (the pane calls it on change only): the Library head's dropdown
        /// carries the pill only when no row does (pill rule 1 beats rule 3).</summary>
        public Action<bool>? PillOnRowChanged { get; init; }
        /// <summary>Ctrl+F with the pane focused, when set (Library: open the toolbar's search box); otherwise Classic's
        /// transient Playlists filter (P1.5).</summary>
        public Action? OpenSearch { get; init; }
```

and `PaneView` (P5-WP5):

```csharp
        Action<int>? _edgeNav;

        // PlanList()'s ListOptions — beside IsItemEnabled / ItemText (the Disclosure block is NOT edited). The engine's
        // ListOptions.OnEdgeNavigate is an Action<int>; the config's is a Func<int, bool> so a mode can say whether it took
        // focus. One cached adapter: the options record is rebuilt every render and must not allocate a closure each time.
                OnEdgeNavigate = Config.OnEdgeNavigate is { } edge ? (_edgeNav ??= d => edge(d)) : null,

        /// <summary>Give list row <paramref name="index"/> keyboard focus (current + into view) — the Library head's Down
        /// from the dropdown, and the Edit-mode exit's focus return (§P4.5).</summary>
        internal void FocusListItem(int index) => _listController.FocusItem(index);

        bool _pillOnRow;

        // At the END of ResolvePillTarget (P1.5):
            bool onRow = _pillTarget.PlanIndex >= 0;
            if (onRow != _pillOnRow)
            {
                _pillOnRow = onRow;
                Config.PillOnRowChanged?.Invoke(onRow);
            }
```

`OnPaneKey` (P1.5's Ctrl+F handler) opens with `if (Config.OpenSearch is { } open) { open(); e.Handled = true; return; }`
after its key test, before the Classic filter-box arm.

**`LibrarySession`** (renamed `V3Session`; members kept unless listed):

- Kept verbatim AS THEY STAND AFTER P4 (the P3 forms plus every P4 edit to `V3Session`; in particular `CommitReorder` is
  `DefaultReorderCommit(in r); Resync();`, and `DefaultReorderCommit` records through `MovePinRecorded` since P4-WP4, so a
  Library pin reorder stays in the undo ring): `Cell`, `Resync`, `InDrawer`, `PaneRef`, `Columns`, `NarrowFolders`, `DrillVersion`, `View`
  (type `SidebarLibraryShaper`), `_viewEpoch`/`ViewEpoch`, the drill stack (`DrillActive`, `CurrentFolderId`,
  `CurrentFolderName`, `ParentName`, `PushFolder`, `PopFolder`, `ResetDrill`, `ActivateFolder`), `Navigate`,
  `CreatePlaylist`, `CreateMenuFn`, `HeaderDropActiveFn`, `Retry`, `ClampReorderSlot`, `CommitReorder`,
  `TreeSortedNonCustom`, `SwitchToCustomSortForReorder`, `CanReorderCustom`.
- Deleted: `SearchOpen` (the head reads `Sidebar.LibrarySearchOpen` directly), `DragInFlight` and `V3DragWatch` (the
  new state has no pin band to hold open), `ComputeColumns`, `ComputeNarrow`, `CollapsePane`, `ClearAllFilters`,
  `AnyFilterActive`, `ChromeHead`, every `BuildRail*`.
- Changed / new:

```csharp
        /// <summary>A list row carries the pill now (fed by the pane's PillOnRowChanged). Session-scoped; the head reads it.</summary>
        public readonly Signal<bool> RowPill = new(false);

        public void NotePillOnRow(bool on) => RowPill.SetIfChanged(on);

        /// <summary>The fixed head above the list. KEYED and type-stable: the pane rebuilds this element every render and
        /// the instance behind it must survive.</summary>
        public Element Head() => Embed.Comp(() => new LibraryHead(this)) with { Key = "library-head" };

        // ── the head's focus targets (written by the head's OnRealized; the ring and the list's edge read them) ──
        internal NodeHandle HomeNode, DropdownNode, SearchButtonNode;
        internal InputHooks? Hooks;
        internal bool HasPages;
        /// <summary>Set by the search box's Esc-close: the toolbar's search button takes focus when it remounts.</summary>
        internal bool FocusSearchButtonOnMount;

        internal bool FocusHead(SidebarLibraryHeadRules.HeadStop stop)
        {
            var node = stop == SidebarLibraryHeadRules.HeadStop.Home || !HasPages ? HomeNode : DropdownNode;
            if (node.IsNull || Hooks?.FocusNode is not { } focus) return false;
            focus(node, true);
            return true;
        }

        /// <summary>The list's Up from its first row lands on the dropdown (Home when the title is not a menu).</summary>
        public bool OnListEdge(int direction) => direction < 0 && FocusHead(SidebarLibraryHeadRules.HeadStop.Dropdown);

        public void OpenSearch() => LibrarySearchOpen.SetIfChanged(true);

        /// <summary>Close AND clear: a closed box showing leftover text next time would read as a bug.</summary>
        public void CloseSearch()
        {
            LibrarySearchOpen.SetIfChanged(false);
            LibrarySearch.SetIfChanged("");
            Resync();
        }

        public SidebarLibraryState ReadState()
        {
            _ = LayoutVersion.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            _ = DrillVersion.Value;          // a push/pop re-slices the view
            return new SidebarLibraryState(filter, lib.Sort, lib.Descending, lib.View, Columns?.Value ?? 2,
                SidebarLibraryMetrics.HasQuery(LibrarySearch.Value), DrillActive ? CurrentFolderId : null,
                Pins.Count > 0);
        }

        public int ReadModeEpoch() => ReadState().GetHashCode();

        public bool DisclosesFoldersInline() => !NarrowFolders.Peek() && Doc.Library.View == SidebarLibraryView.List;

        public SidebarPlanOptions ShapeOptions(SidebarPlanOptions o)
            => o with
            {
                Filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, Doc.Library.HiddenKinds),
                GridColumns = Columns?.Value ?? 2,
                FoldersInline = DisclosesFoldersInline() && !DrillActive,   // §P3.5: a pinned folder opens in place
                Drilled = DrillActive,                                       // §P3.5: a folder level has no pins / Liked
            };
```

  `ShapeInput` is P3's body with `bool group = state.FoldersApply;` in place of `LibraryV3Metrics.FoldersApply(in state)`.
  `CurrentFolderName`/`ParentName` keep their `Strings.Sidebar.V3.Title` fallback (P6 deletes only UNREFERENCED keys).

**`LibraryHead`** — the component tree (pane x; the head pads 4, like the list):

```
LibraryHead (BoxEl Direction 1, Shrink 0, Padding (4, 3, 4, 0))                         172 tall (+32 breadcrumb)
├─ Home row       ZStack[EntityRow row A "lib-home", SelectionPill lane Head]                     40
├─ Separator      SectionHeader.Separator()                                                         8
├─ Dropdown row   ZStack[SelectionPill lane Head (top 12), title button 32 tall | plain title]   40
├─ LibraryChips   ScrollEl horizontal, 40 (chips 32, gap 8, r16)                                   40
├─ LibraryToolbar 40: closed [⌕][Sort ▾ "Recents"] · spacer · [≡][▦] · [+] [⋯] | open [search box][⇅]  40
├─ Breadcrumb     32, only while DrillActive
├─ Rule           1, StrokeDividerDefault, full pane width                                          1
└─ Error banner / empty band (below the rule, by the V3 rules)
```

Compact (the rail, `Mode == Compact && !InDrawer`): P2.7's `CompactHead` moved here (Home tile, search tile, separator)
— the rail has no dropdown, chips or toolbar. Its search tile is `() => { s.OpenSearch(); Sidebar.OpenPane(); }`:
`OpenPane` opens the OVERLAY in the forced Narrow/Tiny bands (a forced rail has `UserCollapsed` false, so clearing it would
do nothing) and expands the docked pane in Wide; the shared `LibrarySearchOpen` makes whichever mount shows the full head
render the search box, which focuses itself on mount.

```csharp
    /// <summary>Your Library's fixed head (design P.2a). Reads every changing input from signals in its own render.</summary>
    internal sealed class LibraryHead(LibrarySession s) : Component
    {
        readonly List<SidebarLibraryPage> _pages = new(4);
        OverlayHandle? _pagesMenu;

        public override Element Render()
        {
            // Every hook runs BEFORE the compact early return: the rail ⇄ pane switch must not change the hook order.
            var hooks = UseContext(InputHooks.Current);
            s.Hooks = hooks;
            int drillVersion = s.DrillVersion.Value;
            bool searching = SidebarLibraryMetrics.HasQuery(LibrarySearch.Value);
            // Self-correcting state 1: a drilled-into folder that vanished POPS rather than pointing at nothing.
            bool missing = s.View.DrillTargetMissing;
            UseLayoutEffect(() => { if (missing) s.PopFolder(); }, DepKey.From(missing ? 1 : 0, drillVersion));
            // Self-correcting state 2: a search FLATTENS the tree, so there is no folder to be inside of.
            UseLayoutEffect(() => { if (searching) s.ResetDrill(); }, DepKey.From(searching));

            if (!s.InDrawer && Sidebar.Mode.Value == SidebarPaneMode.Compact) return CompactHead();

            _ = LayoutVersion.Value;
            // The kind counts AND the query the list was shaped with publish through the binder's input edge (§P5.2).
            _ = Binder?.InputVersion.Value;
            // The PUBLISHED rows: the list count, the load state and the pending flag below are plain properties of the cell,
            // so the head subscribes to the cell's version here (V3Chrome did it through ReadState). Without this read a
            // search with no results never shows its band, and a cleared search keeps a stale one.
            _ = LibrarySession.Cell.Version.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            string route = Shell.NameOf(Shell.Current.Value);
            var counts = Binder?.Counts ?? default;
            SidebarLibraryHeadRules.Pages(lib.HiddenKinds, in counts, static r => Shell.Dest(Shell.Parse(r)).Glyph, _pages);
            s.HasPages = SidebarLibraryHeadRules.DropdownIsMenu(_pages.Count);
            string? page = SidebarLibraryHeadRules.PageOf(route, lib.HiddenKinds);

            var cell = LibrarySession.Cell;
            var load = cell.State;
            bool anyPending = cell.AnyContributingKindPending;
            // THE ONE OWNER of Your Library's empty state (the planner plans no Empty row in Library, §P3.5): the scroller's
            // row count exactly as PlanLibrary builds it — the pins ShowsPinInLibrary keeps, the Liked row, the list — over
            // the query the LIST was shaped with (the binder's, never the live box text, which runs ahead by the debounce).
            var input = Binder?.CurrentInput ?? default;
            string? shapedSearch = SidebarSearch.Normalize(input.Search) is { Length: > 0 } q ? q : null;
            bool pinnedShown = Doc.Find(SidebarSectionKind.Pinned) is { Hidden: false };
            int rows = SidebarLibraryEmptyRules.ScrollerRows(input.Pins, pinnedShown, lib, filter, shapedSearch,
                                                             Loc.Get("nav.likedSongs"), s.View.Count, s.DrillActive);
            var empty = SidebarLibraryEmptyRules.Of(rows, load, anyPending, filter, shapedSearch is not null);

            var kids = new List<Element>(9)
            {
                HomeRow(route),
                SectionHeader.Separator(),
                DropdownRow(page),
                Embed.Comp(() => new LibraryChips(s)) with { Key = "library-chips" },
                Embed.Comp(() => new LibraryToolbar(s)) with { Key = "library-toolbar" },
            };
            if (s.DrillActive) kids.Add(Breadcrumb());
            kids.Add(new BoxEl
            {
                Key = "library-rule", Height = SidebarLibraryHeadRules.RuleHeight, Shrink = 0f,
                Margin = new Edges4(-SidebarRowGeometry.PaneEdge, 0f, -SidebarRowGeometry.PaneEdge, 0f),
                Fill = Tok.StrokeDividerDefault,
            });
            // §3.2.10 (kept): loaded content is NEVER blanked. A failure WITH rows is a one-line banner; only a failure with
            // nothing to show takes the pane. A pending library shows the pane's skeletons, never an empty state.
            if (load == LoadState.Failed && rows > 0) kids.Add(ErrorBanner());
            else if (empty != SidebarLibraryEmpty.None) kids.Add(EmptyBand(empty, filter, shapedSearch ?? ""));

            return new BoxEl
            {
                Key = "library-head", Direction = 1, Shrink = 0f,
                Padding = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 0f),
                Children = [.. kids],
            };
        }

        Element HomeRow(string route)
        {
            var home = Shell.Dest(Shell.Parse("home"));
            var row = EntityRow.Create(new RowSpec
            {
                Key = "lib-home", Label = home.Title, Shape = SidebarRowShape.Glyph, Glyph = home.Glyph,
                Selected = string.Equals(route, "home", StringComparison.Ordinal),
                OnClick = () => s.Navigate("home", null), Focusable = true,
                OnRealized = h => s.HomeNode = h,
                MenuOverlay = s.PaneRef?.MenuOverlay,
                // The Home section exists in the Library catalogue; its row menu is the route's (P3's RouteMenu returns the
                // menu factory itself). SidebarMenuModel.Item(Home, …) adds nothing section-scoped.
                Menu = s.PaneRef is { } p && Doc.Find(SidebarSectionKind.Home) is { } homeSection ? p.RouteMenu(homeSection, "home", -1) : null,
            });
            return new BoxEl
            {
                Key = "lib-home-ring", ZStack = true, Shrink = 0f,
                OnKeyDown = e => OnRingKey(SidebarLibraryHeadRules.HeadStop.Home, e),
                Children = s.PaneRef is { } pane
                    ? [row, Embed.Comp(() => new SelectionPill(pane, () => HomePill(pane), SidebarPillLane.Head)) with { Key = "lib-home-pill" }]
                    : [row],
            };
        }

        static SidebarPillState HomePill(PaneView pane)
        {
            string live = pane.SelectedRoute;                                   // a signal read: the pill re-renders
            return new SidebarPillState("home", SidebarPillState.Lit("home", live), 0f,
                SidebarRowGeometry.PillTop(SidebarRowGeometry.RowHeight));
        }

        /// <summary>The page dropdown's pill: lit while the route is one of the pages AND no list row carries it.</summary>
        SidebarPillState DropdownPill(PaneView pane)
        {
            string live = pane.SelectedRoute;
            string? pg = SidebarLibraryHeadRules.PageOf(live, Doc.Library.HiddenKinds);
            bool lit = pg is not null && SidebarLibraryHeadRules.DropdownCarriesPill(live, Doc.Library.HiddenKinds, s.RowPill.Value);
            return new SidebarPillState(lit ? pg : null, lit, 0f,
                (SidebarLibraryHeadRules.DropdownRowHeight - SidebarRowGeometry.PillH) * 0.5f);
        }

        Element DropdownRow(string? page)
        {
            // The accessible name reads the whole "Your Library · Albums".
            string title = page is null
                ? Loc.Get("sidebar.library.title")
                : Loc.Format("sidebar.library.titleOnPage", ("page", Loc.Get(PageTitleKey(page))));
            // "Your Library" in 600 TextPrimary; on a page the "· Albums" suffix in 400 TextSecondary (the preview's head),
            // and only the suffix ellipsises in a narrow pane.
            var head = new TextEl(Loc.Get("sidebar.library.title"))
            {
                Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 0f,
            };
            Element[] words = page is null
                ? [head]
                : [head, new TextEl(Loc.Format("sidebar.library.pageSuffix", ("page", Loc.Get(PageTitleKey(page)))))
                  {
                      Size = 14f, Weight = 400, Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
                      Trim = TextTrim.CharacterEllipsis,
                  }];
            // No page to offer (every kind hidden through Filters): plain text — no chevron, no button role, no empty menu.
            Element title0 = !s.HasPages
                ? new BoxEl
                {
                    Key = "library-title", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                    Height = SidebarLibraryHeadRules.ToolbarControl,
                    Margin = new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge, 4f, 0f, 4f),
                    Children = [.. words],
                }
                : new BoxEl
                {
                    Key = "library-dropdown", Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
                    Height = SidebarLibraryHeadRules.ToolbarControl, Shrink = 1f, MinWidth = 0f,
                    Margin = new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge - SidebarLibraryHeadRules.TitleHitInset, 4f, 0f, 4f),
                    Padding = new Edges4(SidebarLibraryHeadRules.TitleHitInset, 0f, SidebarLibraryHeadRules.TitleHitInset, 0f),
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    AutomationName = title,
                    OnRealized = h => s.DropdownNode = h,
                    OnClick = () => OpenPages(page),
                    OnKeyDown = e => OnRingKey(SidebarLibraryHeadRules.HeadStop.Dropdown, e),
                    Children = [.. words, Icon(Icons.ChevronDown, SidebarLibraryHeadRules.TitleChevron, Tok.TextSecondary)],
                }.Interactive(new InteractionRecipe
                {
                    Fill = new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
                    BrushMs = 83f,
                });
            return new BoxEl
            {
                Key = "library-dropdown-row", ZStack = true, Height = SidebarLibraryHeadRules.DropdownRowHeight, Shrink = 0f,
                // The pill is ALWAYS mounted (lit or dark, like a row's): the pane's selection transaction needs the outgoing
                // node to animate it out when the route leaves the page (§P5.5 "Cross-container pill motion").
                Children = s.PaneRef is { } pane
                    ? [Embed.Comp(() => new SelectionPill(pane, () => DropdownPill(pane), SidebarPillLane.Head)) with { Key = "lib-dropdown-pill" }, title0]
                    : [title0],
            };
        }

        static string PageTitleKey(string page) => page switch
        {
            "albums" => "nav.albums",
            "artists" => "nav.artists",
            "podcasts" => "nav.podcasts",
            _ => "nav.audiobooks",
        };

        void OpenPages(string? page)
        {
            if (s.PaneRef?.MenuOverlay is not { } svc || _pages.Count == 0) return;
            if (_pagesMenu is { IsOpen: true } open) { open.Close(); return; }
            var items = SidebarMenus.MapPages(SidebarMenuModel.Pages(_pages, page), _pages, r => s.Navigate(r, null));
            _pagesMenu = svc.Open(
                () => s.DropdownNode,
                () => MenuFlyout.Create(items, () => _pagesMenu?.Close(), minWidth: 220f),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            _pagesMenu.ClosedAction = () => _pagesMenu = null;
        }

        /// <summary>The head's ring (design V.11): Up/Down between Home, the dropdown and the list's first row; Enter/Space on
        /// the dropdown opens the page menu.</summary>
        void OnRingKey(SidebarLibraryHeadRules.HeadStop stop, KeyEventArgs e)
        {
            if (e.Handled) return;
            if (stop == SidebarLibraryHeadRules.HeadStop.Dropdown && e.KeyCode is Keys.Enter or Keys.Space)
            {
                OpenPages(SidebarLibraryHeadRules.PageOf(Shell.NameOf(Shell.Current.Peek()), Doc.Library.HiddenKinds));
                e.Handled = true;
                return;
            }
            int dir = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (dir == 0) return;
            var from = stop == SidebarLibraryHeadRules.HeadStop.Dropdown && !s.HasPages ? SidebarLibraryHeadRules.HeadStop.Dropdown : stop;
            var next = SidebarLibraryHeadRules.Next(from, dir, s.PaneRef?.Plan.Rows.Count ?? 0);
            if (!s.HasPages && next == SidebarLibraryHeadRules.HeadStop.Dropdown && dir > 0)
                next = (s.PaneRef?.Plan.Rows.Count ?? 0) > 0 ? SidebarLibraryHeadRules.HeadStop.List : stop;   // skip the plain title
            if (next == SidebarLibraryHeadRules.HeadStop.List) s.PaneRef?.FocusListItem(0);
            else if (next != stop) s.FocusHead(next);
            e.Handled = true;
        }
```

`CompactHead()` is P2.7's body with `Tile("lib-search", Loc.Get("sidebar.library.search"), Icons.Search, false, () => {
s.OpenSearch(); Sidebar.OpenPane(); })` and keys `"lib-compact-head"`, `"lib-home"`. `Breadcrumb()` and `ErrorBanner()` are
V3Chrome's, moved, with these exact edits: the breadcrumb's padding is
`new Edges4(SidebarLibraryHeadRules.TitleX - SidebarRowGeometry.PaneEdge - 6f, 0f, SidebarRowGeometry.TrailingPad, 0f)` and
its height `SidebarLibraryMetrics.BreadcrumbHeight`; `ErrorBanner`'s margin is `new Edges4(0f, 4f, 0f, 4f)` (the head
already pads 4). `EmptyBand` is V3Chrome's, re-keyed on the one decision (`SidebarLibraryEmptyRules.Of`, §P5.1) so its
arms cannot disagree with the list:

```csharp
        /// <summary>The scroller has nothing to show (decided by <see cref="SidebarLibraryEmptyRules.Of"/> over the planned
        /// rows). The ONLY empty state Your Library draws: the planner plans no Empty row in this layout.</summary>
        Element EmptyBand(SidebarLibraryEmpty empty, SidebarLibraryFilter filter, string query)
        {
            Element body = empty switch
            {
                // The FULL error state (page scale), never the compact arm and never the banner.
                SidebarLibraryEmpty.Failed => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Page, onAction: s.Retry),
                SidebarLibraryEmpty.Search => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    // a pasted paragraph cannot blow out 240 DIP
                    title: Strings.Sidebar.V3.Empty.Search(query.Length > 24 ? string.Concat(query.AsSpan(0, 24), "…") : query),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.SearchSub),
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearSearch),
                    onAction: static () => { LibrarySearch.SetIfChanged(""); LibrarySession.Resync(); }),
                SidebarLibraryEmpty.Filter => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                    title: Strings.Sidebar.V3.Empty.Filter(Loc.Get(SidebarLibraryHeadRules.ChipKey(filter))),
                    subtitle: "",
                    actionLabel: Loc.Get(Strings.Sidebar.V3.ClearFilter),
                    onAction: static () => { SetLibraryFilter(SidebarLibraryFilter.None); LibrarySession.Resync(); }),
                _ => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                    title: Loc.Get(Strings.Sidebar.V3.Empty.Library),
                    subtitle: Loc.Get(Strings.Sidebar.V3.Empty.LibrarySub),
                    actionLabel: Loc.Get(Strings.Sidebar.CreatePlaylistTooltip),
                    onAction: s.CreatePlaylist),
            };
            // Shrink 0, no Grow: the state reads as the content it replaces, above the (now empty) scroll surface.
            return new BoxEl { Key = "library-empty", Direction = 1, Shrink = 0f, Children = [body] };
        }
```

(`Strings.Sidebar.V3.*` keep their keys: P6 deletes only UNREFERENCED loc keys. Keep V3Chrome's one-shot failure log as
a layout effect among the hooks BEFORE the compact early return — hook order must not depend on the rail — keyed on a
value computed there: `bool failedEmpty = LibrarySession.Cell.State == LoadState.Failed && s.View.Count == 0;`
`UseLayoutEffect(() => { if (failedEmpty) Log.Warn("sidebar", "library.failed rows=0", LibrarySession.Cell.Error); }, DepKey.From(failedEmpty));`
— a diagnostic line, so the list-only count is enough.)

**`LibraryChips`** — the chip rail (V3ChipRail without the fused/qualifier/clear machinery). Only `s` comes in:

```csharp
    internal sealed class LibraryChips(LibrarySession s) : Component
    {
        readonly List<SidebarLibraryFilter> _chips = new(5);

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var focused = UseSignal(-1);                                       // roved chip index (one tab stop)
            var nodes = UseMemo(static () => new NodeHandle[5], DepKey.Empty);
            var scroll = UseMemo(static () => new ScrollHandle(), DepKey.Empty);

            _ = LayoutVersion.Value;
            var hidden = Doc.Library.HiddenKinds;
            var active = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, hidden);
            SidebarLibraryHeadRules.Chips(hidden, _chips);                     // rebuilt here: a hidden kind loses its chip
            int roved = focused.Value is var f && f >= 0 && f < _chips.Count ? f : Math.Max(0, _chips.IndexOf(active));

            // HOME the rail on a chip change: the active chip leads it.
            UseLayoutEffect(() => scroll.ScrollTo(0.0), DepKey.From((int)active));

            var chips = new Element[_chips.Count];
            for (int i = 0; i < _chips.Count; i++) chips[i] = Chip(nodes, i, _chips[i], _chips[i] == active, i == roved);

            return ScrollView(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = SidebarLibraryHeadRules.ChipGap,
                Padding = new Edges4(SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f,
                                     SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f),
                OnKeyDown = e => Rove(e, hooks, nodes, focused, roved),
                Children = chips,
            }, horizontal: true) with
            {
                Grow = 0f, Height = SidebarLibraryHeadRules.ChipRowHeight, AutoEdgeFade = true, SuppressScrollBar = true,
                ScrollKey = "sidebar.library.chips", Handle = scroll,
            };
        }

        void Commit(SidebarLibraryFilter chip)
        {
            SetLibraryFilter(SidebarLibraryHeadRules.Toggle(LibraryFilter.Peek(), chip));
            s.ResetDrill();      // a chip change leaves any drilled folder
            LibrarySession.Resync();
        }

        /// <summary>32 tall, r16; inactive FillSubtleSecondary + TextPrimary; active AccentDefault + TextOnAccentPrimary with a
        /// trailing ✕ 10 (a click on the active chip clears it). Selection is colour only — the padding never changes.</summary>
        Element Chip(NodeHandle[] nodes, int index, SidebarLibraryFilter chip, bool on, bool focusable)
        {
            string label = Loc.Get(SidebarLibraryHeadRules.ChipKey(chip));
            var kids = on
                ? new Element[] { new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 },
                                  Icon(Icons.Cancel, 10f, Tok.TextOnAccentPrimary) }
                : new Element[] { new TextEl(label) { Size = 12f, Color = Tok.TextPrimary, MaxLines = 1 } };   // V.12 ramp: 12
            return new BoxEl
            {
                Key = "chip:" + (int)chip,
                Direction = 0, Height = SidebarLibraryHeadRules.ChipHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
                Padding = new Edges4(12f, 0f, 12f, 0f), Corners = Radii.PillAll,
                Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
                HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
                Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                OnClick = () => Commit(chip),
                OnRealized = h => nodes[index] = h,
                Children = kids,
            };
        }

        /// <summary>One tab stop, roving by index (←/→/Home/End); Space/Enter commits; selection does not follow focus.</summary>
        void Rove(KeyEventArgs e, InputHooks hooks, NodeHandle[] nodes, Signal<int> focused, int cur)
        {
            if (e.Handled || _chips.Count == 0) return;
            int n = _chips.Count, next;
            switch (e.KeyCode)
            {
                case Keys.Left: next = cur == 0 ? n - 1 : cur - 1; break;
                case Keys.Right: next = cur == n - 1 ? 0 : cur + 1; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = n - 1; break;
                case Keys.Space:
                case Keys.Enter:
                    Commit(_chips[cur]);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
            focused.Value = next;
            if (!nodes[next].IsNull) (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(nodes[next]);
            e.Handled = true;
        }
    }
```

**`LibraryToolbar`** — closed and open shapes in one keyed host. Only `s` comes in; `searching`, the sort and the view
are read here, so the search button turns the toolbar into the box, and the sort label and the view toggles follow the
model:

```csharp
    internal sealed class LibraryToolbar(LibrarySession s) : Component
    {
        OverlayHandle? _menu;
        readonly List<SidebarLibraryHeadRules.HeadTabStop> _stops = new(11);   // reused every render (no per-render list)

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            bool searching = LibrarySearchOpen.Value;
            _ = LayoutVersion.Value;
            var lib = Doc.Library;
            var filter = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, lib.HiddenKinds);
            var sort = SidebarLibraryHeadRules.Effective(lib.Sort, filter);
            var sortAnchor = UseRef<NodeHandle>(default);
            var moreAnchor = UseRef<NodeHandle>(default);
            // Anchors survive the controls' re-assertion of their roots only through a Parts modifier (memoised once).
            var sortParts = UseMemo(() => AnchorParts(Button.PartRoot, sortAnchor), DepKey.Empty);
            var sortIconParts = UseMemo(() => AnchorParts(IconButton.PartRoot, sortAnchor), DepKey.Empty);
            var moreParts = UseMemo(() => AnchorParts(IconButton.PartRoot, moreAnchor), DepKey.Empty);
            var searchParts = UseMemo(() =>
            {
                var m = new TemplateParts();
                m[IconButton.PartRoot] = b => b with
                {
                    OnRealized = h =>
                    {
                        s.SearchButtonNode = h;
                        if (!s.FocusSearchButtonOnMount) return;
                        s.FocusSearchButtonOnMount = false;
                        post(() => hooks.FocusNode?.Invoke(h, true));   // back to the magnifier after Esc closed the box
                    },
                };
                return m;
            }, DepKey.Empty);

            string sortLabel = Loc.Get(SidebarLibraryHeadRules.SortKey(sort));
            // The shape flips rarely; a seam drag writes Width every frame, so it is a MEMO (static: it reads only signals).
            var shape = UseComputed(static () =>
            {
                _ = LayoutVersion.Value;
                var l = Doc.Library;
                var f = SidebarLibraryFilters.Effective((int)LibraryFilter.Value, l.HiddenKinds);
                return SidebarLibraryHeadRules.ShapeOf(Sidebar.Width.Value,
                    Loc.Get(SidebarLibraryHeadRules.SortKey(SidebarLibraryHeadRules.Effective(l.Sort, f))));
            }).Value;
            // Icon-only while searching (the box takes the row) and whenever the labelled row would not fit (§P5.1 ShapeOf).
            Element sortButton = searching || shape != SidebarLibraryHeadRules.ToolbarShape.Full
                ? ToolTip.Wrap(IconButton.Create(Icons.Sort, () => OpenSort(sortAnchor.Value, filter), parts: sortIconParts,
                                                 size: ControlSize.Small) with { Key = "lib-sort-compact" }, sortLabel)
                : Button.Create(sortLabel, () => OpenSort(sortAnchor.Value, filter), ButtonAppearance.Subtle, ControlSize.Small,
                                glyph: Icons.Sort, parts: sortParts) with { Key = "lib-sort" };

            // The controls are built by walking the head's Tab order (§P5.1 TabOrder, Q11), so the Tab order IS the screen
            // order and SidebarLibraryHeadRulesTests.TabOrder_MatchesScreen pins both. Below 240 the view toggles leave the
            // order (⋯ › View keeps the choice): ⋯ is never the control that gets clipped.
            SidebarLibraryHeadRules.TabOrder(shape, searching, _stops);
            var kids = new List<Element>(8);
            for (int i = 0; i < _stops.Count; i++)
                switch (_stops[i])
                {
                    case SidebarLibraryHeadRules.HeadTabStop.SearchBox:
                        kids.Add(Embed.Comp(() => new LibrarySearchBox(s)) with { Key = "lib-search-box" });
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Search:
                        kids.Add(ToolTip.Wrap(IconButton.Create(Icons.Search, s.OpenSearch, parts: searchParts, size: ControlSize.Small)
                                                  with { Key = "lib-search" }, Loc.Get("sidebar.library.search")));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Sort:
                        kids.Add(sortButton);
                        if (!searching) kids.Add(new BoxEl { Key = "lib-spacer", Grow = 1f });   // not a stop
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.ListView:
                        kids.Add(ViewToggle(SidebarLibraryView.List, lib.View, Icons.ViewList, "sidebar.view.list"));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.GridView:
                        kids.Add(ViewToggle(SidebarLibraryView.Grid, lib.View, Icons.ViewGrid, "sidebar.view.grid"));
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.Create:
                        kids.Add(Embed.Comp(() => new CreateButton(s.CreatePlaylist, menu: s.CreateMenuFn,
                            drop: s.PaneRef?.HeaderCreateDropSpec(), dropActive: s.HeaderDropActiveFn,
                            box: SidebarLibraryHeadRules.ToolbarControl, glyph: 14f)) with { Key = "lib-create" });
                        break;
                    case SidebarLibraryHeadRules.HeadTabStop.More:
                        kids.Add(ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenOptions(moreAnchor.Value, filter), parts: moreParts,
                                                                size: ControlSize.Small) with { Key = "lib-more" },
                                              Loc.Get("sidebar.library.options")));
                        break;
                    // Home, Dropdown and Chips are LibraryHead's rows above, List is the pane's list below: not toolbar controls.
                }
            return new BoxEl
            {
                Key = "library-toolbar", Direction = 0, Height = SidebarLibraryHeadRules.ToolbarHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = SidebarLibraryHeadRules.ToolbarGap,
                Padding = new Edges4(SidebarLibraryHeadRules.BandInsetX - SidebarRowGeometry.PaneEdge, 4f, 0f, 4f),
                Children = [.. kids],
            };
        }

        static TemplateParts AnchorParts(string part, Ref<NodeHandle> anchor)
        {
            var m = new TemplateParts();
            m[part] = b => b with { OnRealized = h => anchor.Value = h };
            return m;
        }

        /// <summary>V3SortPanel.ViewToggles' checked-state cell, at toolbar size: 32×32 (IconButton has no checked state).
        /// NEUTRAL, never accent (design V.0/V.4 keep the accent for the pill, the InfoBadge, the insertion line and the drop
        /// ring; the preview's <c>.ib.on</c>): on = FillSubtleSecondary + TextPrimary, hover FillSubtleTertiary; off =
        /// transparent + TextSecondary, hover FillSubtleSecondary. An icon-only cell's accessible name is its tooltip.</summary>
        static Element ViewToggle(SidebarLibraryView v, SidebarLibraryView current, string glyph, string key)
        {
            bool on = current == v;
            var cell = new BoxEl
            {
                Key = "lib-view-" + (v == SidebarLibraryView.Grid ? "grid" : "list"),
                Width = SidebarLibraryHeadRules.ToolbarControl, Height = SidebarLibraryHeadRules.ToolbarControl, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = on ? Tok.FillSubtleSecondary : ColorF.Transparent,
                HoverFill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
                BrushTransitionMs = global::Wavee.Design.Motion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () => SetLibraryView(v),
                Children = [Icon(glyph, 16f, on ? Tok.TextPrimary : Tok.TextSecondary)],
            };
            return ToolTip.Wrap(cell, Loc.Get(key));
        }

        void OpenSort(NodeHandle anchor, SidebarLibraryFilter filter)
            => Open(anchor, SidebarMenus.Map(SidebarMenuModel.Sort(State, filter)));

        void OpenOptions(NodeHandle anchor, SidebarLibraryFilter filter)
            => Open(anchor, SidebarMenus.Map(SidebarMenuModel.LibraryOptions(State, filter)));

        /// <summary>V3HeaderBand.ToggleOverflow's open/close-handle pattern, moved.</summary>
        void Open(NodeHandle anchor, IReadOnlyList<MenuFlyoutItem> items)
        {
            if (s.PaneRef?.MenuOverlay is not { } svc || items.Count == 0) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            SidebarMenus.Overlay = svc;
            _menu = svc.Open(() => anchor, () => MenuFlyout.Create(items, () => _menu?.Close(), minWidth: 220f),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            _menu.ClosedAction = () => _menu = null;
        }
    }
```

(`Button.Create`'s `glyph:` is a LEADING icon — the engine has no trailing-icon button; the design's "Recents ⇅" reads
"⇅ Recents" here.)

**`LibrarySearchBox`** — `V3SearchHost`'s expanded arm only (the narrow morph, `HostMorph`, `LibraryV3SearchRules` and the
inline/narrow split are gone: the box exists only while `LibrarySearchOpen`):

```csharp
    internal sealed class LibrarySearchBox(LibrarySession s) : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            bool empty = LibrarySearch.Value.Length == 0;
            var parts = UseMemo(() =>
            {
                var pr = new TemplateParts();
                pr[EditableText.PartRoot] = b => b with
                {
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent,
                    // The box mounts only because the user opened it (the toolbar's ⌕, the rail's search tile, Ctrl+F): it
                    // always takes focus — after commit, through FirstFocusableIn (the node is not laid out in OnRealized).
                    OnRealized = h => post(() => hooks.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, true)),
                };
                pr[EditableText.PartLane] = b => b with { Padding = new Edges4(SidebarLibraryHeadRules.ToolbarControl, 0f, 4f, 0f) };
                return pr;
            }, DepKey.Empty);

            var kids = new List<Element>(3)
            {
                new BoxEl
                {
                    Key = "search:glyph", Width = SidebarLibraryHeadRules.ToolbarControl, Height = SidebarLibraryHeadRules.ToolbarControl,
                    Shrink = 0f, HitTestVisible = false, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
                    Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
                },
                Embed.Comp(() => new EditableText
                {
                    Text = LibrarySearch, Placeholder = Loc.Get("sidebar.library.searchPlaceholder"),
                    Width = float.NaN, Height = SidebarLibraryHeadRules.ToolbarControl, FontSize = 14f, Chromeless = true,
                    Parts = parts, ShowDeleteButton = true,   // the WinUI inline ✕ — clears, keeps focus
                    // BEFORE the editor's own revert-then-blur; true pre-empts it.
                    PreviewKeyDown = e =>
                    {
                        if (e.KeyCode != Keys.Escape) return false;
                        if (SidebarLibraryHeadRules.OnEscape(LibrarySearch.Peek()) == SidebarLibraryHeadRules.SearchEscape.Clear)
                        {
                            LibrarySearch.SetIfChanged("");
                            LibrarySession.Resync();
                            return true;
                        }
                        s.FocusSearchButtonOnMount = true;   // the toolbar's ⌕ takes focus when it remounts
                        s.CloseSearch();
                        return true;
                    },
                    OnFocusChanged = gained =>
                    {
                        if (!gained && SidebarLibraryHeadRules.ClosesOnBlur(LibrarySearch.Peek())) s.CloseSearch();
                    },
                }) with { Key = "search:field" },
            };
            // The editor hides its own ✕ while empty, so an empty box offers a trailing close.
            if (empty)
                kids.Add(IconButton.Create(Icons.Cancel, s.CloseSearch, size: ControlSize.Small) with { Key = "search:close" });

            return new BoxEl
            {
                Key = "lib-search", ZStack = false, Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Height = SidebarLibraryHeadRules.ToolbarControl, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleSecondary,
                Children = [.. kids],
            };
        }
    }
```

(The glyph box and the field's lane share the 32-px leading inset, so the text starts past the magnifier, as V3's did.
The `EditableText` factory is keyed and created once; `LibrarySearch` is a signal, so the field stays live.)

**Cross-container pill motion** (design V.5, CPP:2130-2152). The pill never SLIDES between the head, the list and the
footer: the outgoing pill scales Y 1 → 0 and the incoming 0 → 1 over 600 ms, each pivoting at the edge facing the other;
under reduced motion both snap. `NavigationSelectionMotion.StartVertical(…, sameDepth: false)` already is exactly that
choreography (its keyframes use `ReducedMotionPolicy.KeepFade`, which snaps transforms under reduced motion), so the work
is telling the pane's selection transaction which CONTAINER each pill is in:

`Shell/Sidebar.Rules.cs` (P5-WP5):

```csharp
/// <summary>Which container a selection pill lives in (design V.5): the list, the fixed head above it, the footer below.</summary>
public enum SidebarPillLane : byte { List = 0, Head = 1, Footer = 2 }

public static class SidebarPillMotionRules
{
    /// <summary>The pill SLIDES (WinUI's same-level worm) only within ONE container at one depth (same x). Across containers
    /// — head ↔ list ↔ footer — it never slides: it scales out and in place (the non-same-level animation).</summary>
    public static bool Slides(SidebarPillLane from, SidebarPillLane to, float dx)
        => from == to && System.MathF.Abs(dx) < 0.5f;
}
```

`Shell/Sidebar.UI.Rows.cs` `SelectionPill` (P5-WP5; never `Chevron`): a third constructor parameter
`SidebarPillLane lane = SidebarPillLane.List` stored in `readonly SidebarPillLane _lane;`, and its layout effect registers
with it: `_owner.RegisterSelectionPill(route, _self, _lane);`. Every existing caller (the slot's rows and headers) keeps
the default.

`Shell/Sidebar.UI.cs` (P5-WP5): beside `_selectionRouteByNode`, `readonly Dictionary<int, SidebarPillLane>
_selectionLaneByNode = new();`; `RegisterSelectionPill(string route, NodeHandle node, SidebarPillLane lane)` stores
`_selectionLaneByNode[index] = lane;` next to `_selectionRouteByNode[index] = route;`; and in `RunSelectionTransaction`
the same-lane test becomes

```csharp
            // Same container and depth ⇒ the continuous worm; anything else — a depth change, or the pill moving between the
            // head, the list and the footer (design V.5) — scales out and in place, pivoting at the facing edges.
            bool sameLane = SidebarPillMotionRules.Slides(LaneOf(outgoing), LaneOf(incoming), to.X - from.X);
```

```csharp
        SidebarPillLane LaneOf(NodeHandle node)
            => _selectionLaneByNode.TryGetValue((int)node.Raw.Index, out var lane) ? lane : SidebarPillLane.List;
```

`Shell/Sidebar.UI.Footer.cs` (P5-WP5): the footer's static pill `BoxEl` (P2.6) becomes a registered pill, so leaving
Settings for a list row (or arriving) scales instead of popping:

```csharp
            Element pill = Embed.Comp(() => new SelectionPill(owner, () => new SidebarPillState("settings",
                SidebarPillState.Lit("settings", owner.SelectedRoute), 0f, SidebarRowGeometry.PillTop(SidebarRowGeometry.RowHeight)),
                SidebarPillLane.Footer)) with { Key = "footer-pill" };
```

(used where P2.6 placed `pill`). The head's two pills are the `SidebarPillLane.Head` ones above; Classic has no head
pill. A list row's pill keeps lane `List`, so a move between two list rows still worms.

**Ctrl+F** with the pane focused: Library sets `OpenSearch = s.OpenSearch` (the `PaneConfig` member above); the box
focuses itself on mount.

### P5.6 The list

- **Pins**: first rows of the list, no header, each with the 12-px `Icons.Pin` mark in the trailing cluster (P1). ONLY
  the pin rows themselves carry it — the DEPTH-0 rows of the pinned section. The children of an expanded pinned folder
  are planned inside the same section (§P3.5 `AppendPinnedFolderChildren`, depth ≥ 1) and are NOT pins, so they get no
  mark (unless the child is itself pinned, which the existing `ShowsPinGlyph` rule already marks). One pure rule in
  `Sidebar.Rules.cs` (P5-WP5), appended to `SidebarPinRules`:

```csharp
    /// <summary>Does a row draw the 12-px pin mark? In Your Library the pins have no header, so each depth-0 row of the
    /// pinned section is marked; the children an expanded pinned folder lists under it (depth ≥ 1) are not pins and are
    /// not marked. Everywhere else the existing rule holds: a pinned entity that is not a track (#85).</summary>
    public static bool ShowsPinMark(SidebarLayoutId layout, SidebarSectionKind section, int rowDepth, bool isPinned, bool isTrack)
        => (layout == SidebarLayoutId.Library && section == SidebarSectionKind.Pinned && rowDepth == 0)
           || SidebarRowGeometry.ShowsPinGlyph(isPinned, isTrack);
```

  `Sidebar.UI.Slot.cs` `EntryRow`: `Pinned = SidebarPinRules.ShowsPinMark(Layout.Peek(), section.Kind, row.Depth,
  snapshot.IsPinned, track),` (replaces the `ShowsPinGlyph` line). `FolderRow`: add `Pinned =
  SidebarPinRules.ShowsPinMark(Layout.Peek(), section.Kind, row.Depth, false, false),` to its `RowSpec` — **a pinned
  FOLDER shows the mark LEFT of its chevron**: P1.3's row lays out the trailing cluster (equalizer · pin mark · count)
  first and the 40-px chevron column after it, so the mark sits just left of the chevron column, and the chevron keeps
  its column and its disclosure (the wide pane opens the folder in place, §P3.5). `MissingFolderRow` gets the same line,
  so an unavailable pinned folder still reads as a pin. (`isPinned: false` for a folder: a folder entry's `IsPinned` is
  not a rootlist fact, and the Classic Pinned header already says it is a pin.)
  Reorder inside the pinned block stays the pane's Pinned `Reorderable` (`MovePinRecorded`), exempt from the sort
  refusal.
- **Liked first** under no chip / Playlists (planner, P3); its row menu offers "Hide from sidebar" (P4).
- **Hidden kinds**: shaping drops them (P3); the chip and the dropdown page go (§P5.1).
- **Custom order = the rootlist**: `TreeSortedNonCustom()` (P3) gates the drag; a refused drag toasts with the action
  "Playlists · Custom order" (`SwitchToCustomSortForReorder`). The `RootlistSlotResolver` skips the Liked row and the
  pin rows when it maps a slot (`Sidebar.UI.cs`: `_pinnedSubtrees` keeps pinned subtrees out; add: a row whose key is
  `SidebarCatalogue.LikedRoute` or whose section is `pinned` is not a rootlist slot).
- **Grid without placeholders**: `GridStripRow` uses `SidebarLibraryHeadRules.GridColumns` (the session's `Columns`);
  `SidebarCards.Tile` for an entry without art draws the kind glyph (24, `TextSecondary`) on `Tok.FillSubtleSecondary`
  in the cover box — never the grey skeleton bone — and draws its title only when `entry.Title.Length > 0`. While the
  library is `Pending` with NOTHING published yet, the grid plans no placeholder strips (the list view keeps its three
  skeleton rows). The grid arm lives INSIDE the empty-list branch, never as a standalone `Pending` test:
  `LibraryState` is the WORST of three edges (albums, artists, shows), so a grid that already shows rows would blank
  whenever one edge is still pending, and `SidebarLibraryEmptyRules.Of` returns None while pending, so nothing would
  replace them (§3.2.10: loaded content is never blanked; D9). `PlanLibrary`'s pending branch becomes:

```csharp
        var list = input.Library;
        if (input.LibraryState == SidebarSourceState.Pending && (list is null || list.Count == 0))
        {
            // Nothing published yet: the list view shows its skeletons; the grid shows nothing (no grey placeholder
            // tiles). Loaded rows never take this branch, so a pending edge never blanks them.
            if (!st.Compact && options.View == SidebarLibraryView.List) EmitSkeletons(lib, ref st);
            return;
        }
```
- **The drill breadcrumb** (narrow/drawer, folders drill) and the **empty/error bands** are part of `LibraryHead` (§P5.5):
  the breadcrumb under the toolbar, 32 tall, only while `DrillActive` (the head is then 204 tall); the banner/empty band
  below the rule. The head is the ONE owner of the empty state: `PlanLibrary` plans no Empty row (§P3.5), and the head
  decides over the planned rows (`SidebarLibraryEmptyRules`, §P5.1), so "No albums · Clear filter" never sits above a
  list that says the library is empty, and no empty state shows above Liked Songs.
- **Pinned folders** open in place in the wide list (`SidebarPlanOptions.FoldersInline`, §P3.5) and drill in the
  narrow/drawer pane (`ActivateFolder`).

### P5.7 Edit mode for Library (`Shell/Sidebar.UI.Edit.cs`)

The Outline already lists Home · Pinned (pin rows) · the Library section's kinds · Settings (P4). For P.2a the Library
section block's header reads "Filters" with `Icons.Filter` and its four kind checkboxes, followed by a locked row
"Your Library" (lock · title · hint `sidebar.edit.libraryHint`). Only the SECTION band is absent in Library (its sections
keep a fixed order, no header grips). The Pinned block's inner pins band is present exactly as in Classic (§P4.5 "The
pins band in both layouts"): drag, Space lift, Alt+↑/↓, Unpin, and the "Show all" fold — hiding Pinned and ordering pins
live in Edit mode for Library (P.2a), so this band is how a Library user reorders pins without a pointer.

### P5.8 Deletions

- File `Shell/Sidebar.UI.LibraryV3.cs` (all of it: `LibraryV3Mode`, `V3Session`, `V3Chrome`, `V3DragWatch`,
  `V3NavBand`, `V3HeaderBand`, `V3ToolbarBand`, `V3SearchHost`, `V3ChipRail`, `V3SortTrigger`, `V3SortPanel`).
- File `Shell/Sidebar.Modes.cs` (all of it: `SidebarV3Filter`, `SidebarV3Qualifier`, `SidebarV3Sort`, `SidebarV3View`,
  `LibraryV3Metrics`, `LibraryV3Labels`, `LibraryV3SearchRules`, `V3ChipKind`, `V3ChipSlot`, `LibraryV3ChipStrip`,
  `LibraryV3DocState`; `LibraryV3View`/`LibraryV3Window` moved, §P5.1). Anything else still in the file after P3 moves
  to `Sidebar.Library.cs` if it has a reader, else goes.
- `Sidebar.UI.cs`: the `LibraryV3Mode` mount becomes `LibraryMode` (`PaneHost.Render`'s `Embed.Comp(() => new
  LibraryV3Mode(_inDrawer))` → `Embed.Comp(() => new LibraryMode(_inDrawer))`).
- `Sidebar.Host.cs`: the bridge (§P5.3).
- `Platform.cs`: `Keys.V3Qualifier`.
- `Settings.UI.Appearance.cs`: any `SidebarV3*` reference (the layout card's caption) → the new names.
- Tests: `SidebarLayoutInfoTests.cs` (the V3 chrome helper facts); V3 enum uses in `SidebarProjectionTests.cs` and
  `SidebarPlannerTests.cs` (the qualifier facts are deleted).

### P5.9 Loc (en-US)

Add under `sidebar`: `library.title` ("Your Library"), `library.titleOnPage` ("Your Library · {page}", the dropdown's
accessible name), `library.pageSuffix` ("· {page}", the visible suffix), `library.search`
("Search in Your Library"), `library.searchPlaceholder` ("Search in Your Library"), `library.options` ("More options"),
`chip.playlists` ("Playlists"), `sort.reverse` ("Reverse order"), `edit.libraryHint` (already in P4 — reuse). Every key
the head reads under `sidebar.library.*` is listed here; `nav.albums`/`nav.artists`/`nav.podcasts`/`nav.audiobooks` exist. The
`sidebar.v3.*` keys become unused; P6 deletes them.

### P5.10 Tests

New `SidebarLibraryHeadRulesTests.cs`:

- `HeadHeight_Is172`.
- The counts (design D9 / "count unknown"):

```csharp
    static readonly System.Func<string, string> Glyph = static r => "g:" + r;

    [Fact]
    public void Pages_CountUnknown_NoNumber_WhileTheLibraryLoads()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.None, default, Glyph, pages);   // default ⇒ Known false
        Assert.Equal(4, pages.Count);
        Assert.All(pages, p => Assert.Null(p.Count));
    }

    [Fact]
    public void Pages_GenuineZero_IsShown()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.None, new SidebarLibraryCounts(0, 3, 0, 1, Known: true), Glyph, pages);
        Assert.Equal(0, pages[0].Count);       // Albums 0, not "no count"
        Assert.Equal(3, pages[1].Count);
    }

    [Fact]
    public void Pages_AllFourHidden_Empty_TitleIsNotAMenu()
    {
        var pages = new List<SidebarLibraryPage>();
        var all = SidebarLibraryKinds.Albums | SidebarLibraryKinds.Artists | SidebarLibraryKinds.Podcasts | SidebarLibraryKinds.Audiobooks;
        SidebarLibraryHeadRules.Pages(all, new SidebarLibraryCounts(1, 1, 1, 1, Known: true), Glyph, pages);
        Assert.Empty(pages);
        Assert.False(SidebarLibraryHeadRules.DropdownIsMenu(pages.Count));
        Assert.True(SidebarLibraryHeadRules.DropdownIsMenu(1));
    }
```
- `GridColumns_Clamped` (240 ⇒ 2; 300 ⇒ 2; 420 ⇒ 3; 560 ⇒ 4; 900 ⇒ 4; NaN ⇒ 2).
- `Pages_AllFour_InOrder_WithCounts`; `Pages_HiddenKindAbsent`.
- `PageOf_LibraryPages_NullForOthers_NullForHidden`.
- `DropdownPill_OnlyOnAPage_AndNotWhenARowHasIt`.
- `Chips_PlaylistsFirst_HiddenKindsAbsent`.
- `Toggle_SameClears_OtherReplaces`.
- `Escape_ClearThenClose`; `Blur_ClosesOnlyEmpty`.
- `CustomOrder_OnlyUnderPlaylists_EffectiveFallsBackToRecents`.
- `Ring_HomeDropdownList` (Down from Home ⇒ Dropdown; Down from Dropdown with 0 rows ⇒ Dropdown; with rows ⇒ List; Up
  from List ⇒ Dropdown; Up from Home ⇒ Home).
- The Tab order matches the screen (Q11; `LibraryToolbar` builds its controls by walking this same list):

```csharp
    [Fact]
    public void TabOrder_MatchesScreen()
    {
        var order = new List<SidebarLibraryHeadRules.HeadTabStop>();
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Full, searching: false, order);
        Assert.Equal(new[]
        {
            SidebarLibraryHeadRules.HeadTabStop.Home, SidebarLibraryHeadRules.HeadTabStop.Dropdown,
            SidebarLibraryHeadRules.HeadTabStop.Chips, SidebarLibraryHeadRules.HeadTabStop.Search,
            SidebarLibraryHeadRules.HeadTabStop.Sort, SidebarLibraryHeadRules.HeadTabStop.ListView,
            SidebarLibraryHeadRules.HeadTabStop.GridView, SidebarLibraryHeadRules.HeadTabStop.Create,
            SidebarLibraryHeadRules.HeadTabStop.More, SidebarLibraryHeadRules.HeadTabStop.List,
        }, order);
        // Folded (< 240): the view toggles leave; ⋯ stays the last toolbar stop.
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Folded, false, order);
        Assert.DoesNotContain(SidebarLibraryHeadRules.HeadTabStop.ListView, order);
        Assert.Equal(SidebarLibraryHeadRules.HeadTabStop.More, order[^2]);
        // Searching: the box replaces the search button and the right-hand group; sort stays after it.
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Full, searching: true, order);
        Assert.Equal(new[]
        {
            SidebarLibraryHeadRules.HeadTabStop.Home, SidebarLibraryHeadRules.HeadTabStop.Dropdown,
            SidebarLibraryHeadRules.HeadTabStop.Chips, SidebarLibraryHeadRules.HeadTabStop.SearchBox,
            SidebarLibraryHeadRules.HeadTabStop.Sort, SidebarLibraryHeadRules.HeadTabStop.List,
        }, order);
    }
```
- The toolbar at narrow widths (the ⋯ must never be clipped):

```csharp
    [Fact]
    public void Toolbar_FitsAt180_IconsOnlyBelow240()
    {
        string[] labels = ["Recents", "Recently added", "Alphabetical", "Creator", "Custom order"];
        foreach (float w in new[] { 180f, 200f, 239f, 240f, 260f, 300f, 320f, 400f, 460f })
            foreach (string label in labels)
            {
                var shape = SidebarLibraryHeadRules.ShapeOf(w, label);
                Assert.True(SidebarLibraryHeadRules.ToolbarWidth(shape, label) <= SidebarLibraryHeadRules.ToolbarLane(w),
                            $"{shape} overflows at {w} with '{label}'");
            }
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(180f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(239f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.IconSort, SidebarLibraryHeadRules.ShapeOf(320f, "Recently added"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Full, SidebarLibraryHeadRules.ShapeOf(460f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(float.NaN, "Recents"));
        Assert.Equal(164f, SidebarLibraryHeadRules.ToolbarLane(180f));
    }
```

- The empty state's one owner (§P5.1, §P5.5):

```csharp
    static readonly SidebarLibraryOptions LikedShown = SidebarLibraryOptions.Default;                 // ShowLiked true
    static readonly SidebarLibraryOptions LikedHidden = SidebarLibraryOptions.Default with { ShowLiked = false };

    [Fact]
    public void Empty_AlbumsChipWithNoAlbums_IsFilter_NeverLibrary()
    {
        // The Albums chip hides Liked and route pins; no albums ⇒ the ONE message is "No albums · Clear filter".
        int rows = SidebarLibraryEmptyRules.ScrollerRows([SidebarLibraryEntry.ForRoute("search", "Search")], pinnedShown: true,
            LikedShown, SidebarLibraryFilter.Albums, search: null, likedTitle: "Liked Songs", listCount: 0);
        Assert.Equal(0, rows);
        Assert.Equal(SidebarLibraryEmpty.Filter,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, anyPending: false, SidebarLibraryFilter.Albums, searching: false));
    }

    [Fact]
    public void Empty_EmptyLibraryWithLiked_IsNone()
    {
        // An empty library still shows Liked Songs under no chip: no "Your library is empty" above it.
        int rows = SidebarLibraryEmptyRules.ScrollerRows(null, pinnedShown: true, LikedShown, SidebarLibraryFilter.None,
            null, "Liked Songs", 0);
        Assert.Equal(1, rows);
        Assert.Equal(SidebarLibraryEmpty.None,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, false, SidebarLibraryFilter.None, false));
        int none = SidebarLibraryEmptyRules.ScrollerRows(null, true, LikedHidden, SidebarLibraryFilter.None, null, "Liked Songs", 0);
        Assert.Equal(SidebarLibraryEmpty.Library,
            SidebarLibraryEmptyRules.Of(none, LoadState.Ready, false, SidebarLibraryFilter.None, false));
    }

    [Fact]
    public void Empty_Priority_PendingFailedSearchFilter()
    {
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(0, LoadState.Pending, false, SidebarLibraryFilter.None, true));
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(0, LoadState.Ready, true, SidebarLibraryFilter.None, true));
        Assert.Equal(SidebarLibraryEmpty.Failed, SidebarLibraryEmptyRules.Of(0, LoadState.Failed, false, SidebarLibraryFilter.Albums, true));
        Assert.Equal(SidebarLibraryEmpty.Search, SidebarLibraryEmptyRules.Of(0, LoadState.Ready, false, SidebarLibraryFilter.Albums, true));
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(3, LoadState.Failed, false, SidebarLibraryFilter.None, false));
    }

    [Fact]
    public void Empty_DrilledEmptyFolder_IsLibrary_PinsAndLikedNotCounted()
    {
        // Drilled into an empty folder: the pins and Liked Songs are not on this level (PlanLibrary under Drilled plans
        // neither), so the head says the level is empty instead of counting rows that are not drawn.
        var pins = new[] { SidebarLibraryEntry.ForRoute("search", "Search") };
        int rows = SidebarLibraryEmptyRules.ScrollerRows(pins, pinnedShown: true, LikedShown, SidebarLibraryFilter.None,
            search: null, likedTitle: "Liked Songs", listCount: 0, drilled: true);
        Assert.Equal(0, rows);
        Assert.Equal(SidebarLibraryEmpty.Library,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, false, SidebarLibraryFilter.None, false));
        // The same inputs, not drilled: the route pin and Liked Songs are rows.
        Assert.Equal(2, SidebarLibraryEmptyRules.ScrollerRows(pins, true, LikedShown, SidebarLibraryFilter.None, null,
            "Liked Songs", 0));
    }
```

(`SidebarLibraryOptions.Default` is §P3.3's; `using FluentGpu.Signals;` at the top of the test file for `LoadState`.)

New `SidebarLibraryStateTests.cs`: `FoldersApply_ListNoSearch_NoneOrPlaylists`; `FoldersApply_FalseInGrid`;
`FoldersApply_FalseUnderAlbums`; `FoldersApply_FalseWhileSearching`; `FoldersApply_FalseWhileDrilled`.

`SidebarPillRulesTests.cs` additions (the cross-container motion rule, §P5.5):

```csharp
    [Fact]
    public void Pill_SlidesOnlyWithinOneContainerAtOneDepth()
    {
        Assert.True(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.List, 0f));
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.List, 31f));     // a depth change scales
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.Head, SidebarPillLane.List, 0f));      // head ↔ list: no slide
        Assert.False(SidebarPillMotionRules.Slides(SidebarPillLane.List, SidebarPillLane.Footer, 0f));    // list ↔ footer: no slide
        Assert.True(SidebarPillMotionRules.Slides(SidebarPillLane.Head, SidebarPillLane.Head, 0f));       // Home ↔ dropdown worms
    }
```

`SidebarMenuModelTests.cs` additions: `Sort_CustomDisabledOutsidePlaylists_WithReason`; `Sort_EffectiveChecked`;
`Sort_ReverseDisabledUnderCustom`; `Pages_CurrentChecked_NavigateRows`.

`SidebarPlannerTests.cs` additions:

```csharp
    static SidebarLayoutDoc GridDoc() => PlanFixture.Doc(SidebarLayoutId.Library,
        s => PlanFixture.Op(s, new SetLibraryView(SidebarLibraryView.Grid)));

    [Fact]
    public void Library_GridPending_NoRows()
    {
        // Pending with an EMPTY published list: no placeholder strips, no skeletons (the list view would show three).
        var input = new SidebarProjectionInput(Library: [], LibraryState: SidebarSourceState.Pending);
        var plan = SidebarRowPlanner.Build(GridDoc(), in input, new SidebarPlanOptions(GridColumns: 3));
        Assert.DoesNotContain(plan.Rows, r => r.Kind is SidebarRowKind.GridStrip or SidebarRowKind.Skeleton);
    }

    [Fact]
    public void Library_GridPending_WithRows_KeepsStrips()
    {
        // One edge still pending (LibraryState is the worst of three) but rows already published: they stay (§3.2.10).
        var list = new[] { PlanFixture.Album("a"), PlanFixture.Album("b"), PlanFixture.Album("c"), PlanFixture.Album("d") };
        var input = new SidebarProjectionInput(Library: list, LibraryState: SidebarSourceState.Pending);
        var plan = SidebarRowPlanner.Build(GridDoc(), in input, new SidebarPlanOptions(GridColumns: 3));
        Assert.Equal(2, plan.Rows.Count(r => r.Kind == SidebarRowKind.GridStrip));   // 3 + 1
    }

    [Fact]
    public void Library_PinMark_DepthZeroPinsOnly()
    {
        Assert.True(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 0, false, false));
        // A pinned folder's expanded child (planned in the pinned section at depth 1) is not a pin.
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 1, false, false));
        // …unless it is itself pinned (the existing #85 rule).
        Assert.True(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Pinned, 1, true, false));
        // Classic's pins sit under their header and keep today's rule; a track never shows the mark.
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Classic, SidebarSectionKind.Pinned, 0, false, false));
        Assert.False(SidebarPinRules.ShowsPinMark(SidebarLayoutId.Library, SidebarSectionKind.Library, 0, true, true));
    }
```

(`SetLibraryView` is §P3.2's op record. `Count(...)` is LINQ, fine in a test.)

`SidebarProjectionTests.cs`: delete the qualifier facts; add `Counts_AreOverTheFullProjection` through
`SidebarProjectionBinder` in `SidebarWiringTests.SidebarBinderWiringTests` (the binder fixture — `FakeLogs`, the staging
helpers — lives there):
a library of 2 albums, 1 artist, 1 podcast, 1 audiobook with the Albums chip active still counts all four kinds
(`Known` true once the staged edges are Complete); and `Counts_UnknownBeforeTheLibraryLoads` — a fresh binder before any
library edge publishes has `Counts.Known == false`.

### P5 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P5-WP1 | new `Shell\Sidebar.Library.cs`, delete `Shell\Sidebar.Modes.cs` | §P5.1 (rules with `Known` counts, nullable page counts, `DropdownIsMenu`, the toolbar shape (`ToolbarShape`/`ToolbarLane`/`ToolbarWidth`/`ShapeOf`), `HeadTabStop` + `TabOrder` (Q11), `SidebarLibraryEmpty` + `SidebarLibraryEmptyRules` with `ScrollerRows(…, bool drilled = false)`; shaper and window moved; `SidebarLibraryState` with `!Drilled`; `SidebarLibraryMetrics` with `DrillInWidth = 240`), §P5.8 Modes deletion | — |
| P5-WP2 | `Shell\Sidebar.Host.cs`, `Shell\Sidebar.cs`, `Platform\Platform.cs` | §P5.2 (query without qualifier, the whole `Shape` method with the widened `narrow` gate, `SidebarNavLayout` deleted, counts with `Known`, `PublishInput(headMoved)` with the search term, `Read()` packs 0), §P5.3 (bridge, setters, key) | P5-WP1 |
| P5-WP3 | `Shell\Sidebar.Edit.cs`, `Shell\Sidebar.UI.Menus.cs` | §P5.4 | P5-WP1 |
| P5-WP4 | new `Shell\Sidebar.UI.Library.cs`, delete `Shell\Sidebar.UI.LibraryV3.cs` | §P5.5 (the full using header of `Sidebar.UI.cs` + `System.Globalization`; `LibraryMode` (narrow memo at `DrillInWidth` 240), `LibrarySession` (`ShapeOptions` sets `Drilled = DrillActive`), `LibraryHead` incl. the `Cell.Version` subscription, the one-owner empty state over `SidebarLibraryEmptyRules.ScrollerRows(…, s.DrillActive)`, the split dropdown title, pills/ring/breadcrumb/error, `LibraryChips` (12-px text), `LibraryToolbar` (`ShapeOf` memo, builds its controls by walking `TabOrder`, neutral view toggles), `LibrarySearchBox`, compact head; `LibrarySession` members as they stand after P4) | P5-WP1, P5-WP2, P5-WP3 |
| P5-WP5 | `Shell\Sidebar.UI.cs`, `Shell\Sidebar.UI.Slot.cs`, `Shell\Sidebar.Cards.cs`, `Shell\Sidebar.Planner.cs`, `Shell\Sidebar.UI.Rows.cs`, `Shell\Sidebar.UI.Footer.cs`, `Shell\Sidebar.Rules.cs` | §P5.5 `PaneConfig` members (`OnEdgeNavigate`, `PillOnRowChanged`, `OpenSearch`) + the edge adapter + `FocusListItem` + pill-on-row callback + Ctrl+F + the `LibraryMode` mount + `PumpBinder` qualifier read deleted; §P5.5 cross-container pill motion (`SidebarPillLane`/`SidebarPillMotionRules`, `SelectionPill` lane, registration + transaction, footer pill); §P5.6 (`SidebarPinRules.ShowsPinMark` — depth-0 pin rows only, a pinned folder shows the mark LEFT of its chevron via `FolderRow`/`MissingFolderRow`; slot resolver; grid tile; the pending grid arm INSIDE `PlanLibrary`'s empty-list branch) | P5-WP1, P5-WP2 |
| P5-WP6 | `Shell\Sidebar.UI.Edit.cs`, `Screens\Settings.UI.Appearance.cs` | §P5.7 (Filters header + locked Your Library row; the Pinned block keeps its inner pins band), §P5.8 Settings | P5-WP1, P5-WP3 |
| P5-WP7 | `assets\loc\en-US.json` | §P5.9 (incl. `library.pageSuffix`) | — |
| P5-WP8 | new `SidebarLibraryHeadRulesTests.cs`, `SidebarLibraryStateTests.cs`; `SidebarMenuModelTests.cs`, `SidebarPlannerTests.cs`, `SidebarProjectionTests.cs`, `SidebarWiringTests.cs`, `SidebarPillRulesTests.cs`, `SidebarDropTests.cs` (the `SidebarNavLayoutTests` class deleted); delete `SidebarLayoutInfoTests.cs` | §P5.10 (incl. `Toolbar_FitsAt180_IconsOnlyBelow240`, `TabOrder_MatchesScreen`, the four `Empty_*` facts incl. `Empty_DrilledEmptyFolder_IsLibrary_PinsAndLikedNotCounted`, the pill-motion fact (needs P5-WP5's `SidebarPillLane`/`SidebarPillMotionRules`), `Library_GridPending_NoRows` (empty list), `Library_GridPending_WithRows_KeepsStrips`, `Library_PinMark_DepthZeroPinsOnly`) | P5-WP1, P5-WP2, P5-WP3, P5-WP5 |

---

## P6. Cleanup: docs, skill, dead keys, CHANGELOG

No behaviour changes. Everything here is text, dead strings and dead settings.

### P6.1 `docs/guide/sidebar.md` (new) replaces `docs/guide/sidebar-extension-platform.md` (deleted)

Sections, in order (prose with the real type names; each section ≤ one screen):

1. **What the sidebar is** — two layouts (Classic, Library), one renderer (`PaneView` + `SidebarRowPlanner`), three
   pane modes (Expanded / Compact / Minimal) from the window band, one Edit mode, one undo ring.
2. **The model** — `SidebarCatalogue` (what each layout can hold and what is locked), `SidebarLayoutState` (two
   `LayoutOverlay`s), `SidebarOp` + `SidebarLayoutRules.Apply` (every edit), `SidebarLayoutDoc` (what the planner reads),
   density. A table of the seven section kinds × the two layouts × hideable / movable / collapsible.
3. **Rows and metrics** — the WinUI ladder (36/40, 4,2 margin, icon centre 24, label 48, indent 31 capped at 3, pill
   3×16 r2, header 40, separator 8, rail 48), `SidebarRowGeometry`, `SidebarRowShape`, the coordinate space note (§P1.0).
4. **Modes and the window band** — `SidebarPaneModeRules` (bands Wide / Narrow 528–660 leave 700 / Tiny < 528 leave
   568), when `userCollapsed` is written (Wide, not editing), the overlay pane, `FluentPane` 200/100 ms.
5. **Data** — the binder (`SidebarProjectionBinder`), `SidebarProjectionInput`, feeds (`SidebarFeedDemand`: Recent and
   New releases fetch only while shown), the recents resolution order (index → resident peek → logged title → skip).
6. **Pins** — per account, `SidebarPinStore`, `LibraryPinSync` + `SidebarPinLatch` (no cross-account apply: the loaded
   key must equal the live key), Liked and Home are fixed routes and never pins, the server pin set (ylpin) is written
   only for entity pins.
7. **Persistence** — `sidebar.json` v3 (device: layouts, density, width, collapsed) and `sidebar.acct-<fnv1a64>.json`
   (account: pins, folders, first-seen, the migration latch), `SidebarFileStore` (temp + rotate + `.bak` + `.corrupt`),
   the one-time v2 migration (`sidebar.bootstrap.version = 2`) and its toast. JSON examples of both files (from
   §P3.7).
8. **Customizing** — Edit mode (the Outline, the edit bar, keyboard: Space lift, arrows, Alt+↑/↓, Enter toggles
   shown; focus lands in the Outline on enter and returns to the row by key on Done/Esc), menus (pane ⋯ / header ⋯ /
   row menus, `SidebarMenuModel`), undo (`SidebarUndoRing`, 50, one entry and one toast per action — `Batch` entries —
   Ctrl+Z/Y rules: taken by the sidebar's own key handlers, focus in the pane/head/edit bar, never a frame chord).
8a. **Disclosure** — sections and folders open and close through `PaneView`'s disclosure path and the
   `SidebarDisclosures` bookkeeping in `Shell/Sidebar.Disclosures.cs` (owned by the smooth-reveal work, not by this
   rework); the guide links to it and describes it in one paragraph, never as a WinUI-style tween.
9. **Library** — the P.2a head (Home, page dropdown = navigation, chips = filtering, toolbar), Custom order = the
   rootlist, the sort refusal, pins at the top with a pin mark, hidden kinds.
10. **Where to change what** — a table: "add a Collections page" → `SidebarCatalogue.Items` + loc; "change a row
    metric" → `SidebarRowGeometry`; "add a menu item" → `SidebarMenuModel` + `SidebarMenus.Run`; "add a pane mode
    rule" → `SidebarPaneModeRules`; "add a persisted field" → the DTO + `SidebarStoreJsonCtx` + a round-trip test.
11. **Tests** — the pure test classes by area (names from §P1.7, §P2.10, §P3.15, §P4.10, §P5.10).

Delete `docs/guide/sidebar-extension-platform.md`. Old plans under `docs/plans/wavee/` that link to it stay as they
are (they are history).

### P6.2 `CLAUDE.md` (app repo) and the `wavee` skill

`WaveeMusic/CLAUDE.md` line 80–82: `wavee-sidebar` (the sidebar platform) → `wavee-sidebar` (the sidebar: layouts,
modes, Edit mode, pins); "Sidebar platform design: `docs/guide/sidebar-extension-platform.md`" → "Sidebar:
`docs/guide/sidebar.md`".

`.claude/skills/wavee/SKILL.md:98`: the `wavee-sidebar` bullet's description → "the left sidebar: Classic and Library
layouts, one renderer, pane modes, Edit mode, per-account pins".

### P6.3 The `wavee-sidebar` skill (rewrite all five files)

- `SKILL.md` — front matter `description`: "Use when changing anything in Wavee's left sidebar: the Classic and Library
  layouts (catalogue, overlays, ops), the one PaneView renderer and SidebarRowPlanner, row geometry, the pane modes and
  window bands, Edit mode and the undo ring, menus, per-account pins and Spotify pin sync, sidebar.json /
  sidebar.acct-*.json persistence, or the Library head. Read before adding a section kind or item, a menu entry, a
  persisted field, or touching pane metrics." Body: scope (`src/apps/Wavee/Shell/Sidebar*.cs`, the `Sidebar*Tests.cs`
  files), the 30-second map (an ASCII flow: `SidebarOp → SidebarLayoutRules.Apply → SidebarLayoutState → Resolve →
  SidebarLayoutDoc → SidebarRowPlanner.Build (+ SidebarProjectionInput from the binder) → SidebarRowPlan → PaneView`),
  the five rules that matter most (one renderer, no mode branches inside it; every edit is a `SidebarOp`; pure decision
  classes are engine-free and tested; Liked/Home are never pins; never write pins across accounts), and links to the
  other four files and `docs/guide/sidebar.md`.
- `architecture.md` — keeps smooth-reveal's disclosure paragraph verbatim (it names `Sidebar.Disclosures.cs`); the
  component tree (design A.1, updated to the shipped names), the pure classes (one paragraph each), the state signals on `Sidebar` (`Layout`, `State`, `Doc`, `Density`, `Mode`, `Band`, `UserCollapsed`,
  `OverlayOpen`, `Editing`, `LibraryFilter`, `LibrarySearch`, `PinsVersion`, `LayoutVersion`, `RingVersion`), the
  stores, the account swap, and the section **"Rootlist drag & drop"** (keep this heading: the
  `wavee-playlist-mutations` skill links to it) describing Classic Playlists and Library Custom order as the rootlist,
  `WaveeResourceDrop.MoveRootlist`, the slot resolver skipping pins and Liked, and the sort refusal.
- `pitfalls.md` — the traps, each as "symptom → cause → rule": a fresh `SidebarLayoutDoc` per render (re-skins the
  window; cache by overlay reference); writing `userCollapsed` outside the Wide band; planning a feed that is not shown
  (network for nothing); a pin write while the loaded account ≠ the live one; treating Liked as a pin; a drag under a
  non-Custom sort; a header that is clickable in the Outline (it must only focus); recording collapse toggles in the
  ring; zero-alloc: no LINQ/closures in `Render` of rows; the disclosure path is shared with `feat/smooth-reveal`
  (`Shell/Sidebar.Disclosures.cs` + `PaneView.StartDisclosure`; no WinUI-style tweens, never a `MotionTok.Disclosure*`
  token — they are deleted); a keyed child's constructor argument is frozen at mount (pass the session, read signals).
- `testing.md` — which pure class covers what (the test class list from §P6.1 item 11), how to build a planner fixture
  (`PlanFixture`), how to test the file store with a temp folder, and "no source-text tests".
- `where-to-change-what.md` — the §P6.1 item 10 table, expanded to every file under `Shell/Sidebar*.cs` with one line
  each (what lives there), including `Sidebar.Disclosures.cs` ("in-flight section/folder disclosures by key; reversal").

### P6.4 Dead loc keys (`assets/loc/en-US.json`, `nl.json`, `ko-KR.json`)

FLLOC005 (unused base key) is an Info diagnostic, so earlier phases build with dead keys; this phase removes them.
Run the build with `-warnaserror` off and collect FLLOC005 from the build log:

```powershell
dotnet build src/apps/Wavee/Wavee.csproj -v:n 2>&1 | Select-String FLLOC005
```

Delete every reported key under `sidebar.*` (expected: the `sidebar.v3.*` block, `sidebar.layout.*` radios of the
three-design menu, `sidebar.template.*`, `sidebar.customizer.*`, `sidebar.palette.*` (the customizer palette, not
P4's `sidebar.palette.useLayout`), `sidebar.section.kind.*`, `sidebar.display.*`, `sidebar.rail.*`, `sidebar.width.*`),
and the same keys from `nl.json` and `ko-KR.json`. Remove `sidebar.template.curated` and `sidebar.template.curatedSub`
from `$unusedAllow` (the keys are gone). Keys outside `sidebar.*` that FLLOC005 reports are not this plan's: leave them.

### P6.5 Dead settings keys (`Platform/Platform.cs`)

Already deleted in P3/P5: `SidebarDesign` reads, `V3Qualifier`. Delete now any remaining `Platform.Keys` sidebar
member with no reader (check each with a grep across `src/apps/Wavee`): expected none beyond comments. The migration
keeps its private `SettingKey`s (it reads the old values once; `sidebar.bootstrap.version` latches it). Do not delete
the old values from users' `settings.json` files: the migration latch already prevents a re-read and removing them
would make a downgrade lose the user's layout.

### P6.6 Census trim (`Shell/Sidebar.Census.cs`, `Shell/Sidebar.UI.cs`)

`SidebarReplanCause`: delete `Edit` (Edit mode swaps the pane since P4; if `editFold`/`_depEdit` still exist in
`PaneView.PlanDep`, delete them with it) and add `PinDrop = 1 << 10` noted when `options.PinDropArmed` moved.
`SidebarReplanCensus`: delete the `railBumps` counter and the `railChanged` parameter of `NotePublish` if P2's single
layer left no rail re-render edge (the call site in `PaneView.PublishStage` passes it today); `Describe` drops
`railBumps=` with it, and `SidebarReplanCensusTests` facts that assert the line's shape follow.

### P6.7 Comments that name deleted files

`Entities/Album.Pane.cs:454` and `Entities/Concert.UI.cs:18` cite `Sidebar.UI.LibraryV3.cs` → `Sidebar.UI.Library.cs`
(the chip visuals moved there, §P5.5). `Wavee.Tests/SidebarDropTests.cs:31` and `:1551` name `LibraryV3View` /
`Sidebar.Modes.cs` → `SidebarLibraryShaper` / `Sidebar.Library.cs`. `Wavee.Tests/SidebarRevisionTests.cs:6` names
`V3Session.ShapeInput` → `LibrarySession.ShapeInput`. (`Spotify.Decode.*`'s `LibraryV3` is Spotify's API name: keep.)

### P6.8 CHANGELOG

Under the topmost `## [X.Y.Z] - unreleased` heading of `WaveeMusic/CHANGELOG.md`; if the top heading is dated (it is
`## [0.3.4] - 2026-10-06` today), add `## [0.3.5] - unreleased` above it. If `feat/smooth-reveal` landed first, that
heading already exists with a `### Changed` list ("Smooth expand and collapse"): append the bullet below to THAT list
(theirs first) and add `### Removed` after it — one heading, one `### Changed`, one `### Removed`. Do not touch
`Wavee.Version.props` (the release script bumps it).

```markdown
### Changed

- **A new sidebar.** The sidebar now looks and behaves like a Windows navigation pane: rows, headers and the selection
  bar match Windows 11, the pane opens and closes on Windows 11's pane curve, and it switches on its own between the
  full pane, a 48-px icon rail and a pane that slides over the page as the window narrows. Pick one of two layouts in Settings › Appearance or from
  the sidebar's ⋯ menu: Classic (Home, Pinned, Collections, Playlists, and optional Recently played and New releases
  sections) or Library (one list with filters, like Spotify's Your Library). In Library, the "Your Library" button
  opens your Albums, Artists, Podcasts or Audiobooks page, and the chips and the toolbar under it filter, sort and
  search the list. "Edit sidebar…" lets you show, hide and reorder sections and pins in place, with Undo. Pins now
  belong to the signed-in account, so switching accounts shows that account's pins. Your current sidebar carries over.

### Removed

- **The Custom sidebar layout and its full-page customizer.** Its sections are mapped onto the closest Classic or
  Library setup the first time this version starts (a toast says which ones could not be kept); everything it offered
  is now in the sidebar's Edit mode.
```

### P6 work packages

| WP | Files | Implements | Depends on |
|---|---|---|---|
| P6-WP1 | new `docs\guide\sidebar.md`, delete `docs\guide\sidebar-extension-platform.md` | §P6.1 | — |
| P6-WP2 | `CLAUDE.md`, `.claude\skills\wavee\SKILL.md` | §P6.2 | P6-WP1 |
| P6-WP3 | `.claude\skills\wavee-sidebar\SKILL.md`, `architecture.md`, `pitfalls.md`, `testing.md`, `where-to-change-what.md` | §P6.3 (keeps smooth-reveal's disclosure paragraph; names `Sidebar.Disclosures.cs`) | P6-WP1 |
| P6-WP4 | `src\apps\Wavee\assets\loc\en-US.json`, `nl.json`, `ko-KR.json` | §P6.4 | — |
| P6-WP5 | `Platform\Platform.cs`, `Shell\Sidebar.Census.cs`, `Shell\Sidebar.UI.cs`, `Entities\Album.Pane.cs`, `Entities\Concert.UI.cs`, `SidebarReplanCensusTests.cs`, `SidebarDropTests.cs`, `SidebarRevisionTests.cs` | §P6.5, §P6.6, §P6.7 | — |
| P6-WP6 | `CHANGELOG.md` | §P6.8 (one `## [0.3.5] - unreleased` heading shared with smooth-reveal's bullet if present: a `### Changed` bullet for the new sidebar, a `### Removed` bullet for Custom) | — |

---

## Appendix A. End-state file map (`src/apps/Wavee/Shell/`)

| File | Holds |
|---|---|
| `Sidebar.cs` | entries, projection, shaping, sort, row kinds, `SidebarProjectionInput`, extents, resolve, `SidebarRowGeometry`, pin ids |
| `Sidebar.Rules.cs` | pill, subtitle, type-ahead, label fit, `SidebarPinRules`, `MenuLabel` |
| `Sidebar.Resize.cs` | `SidebarPaneMode`, `SidebarWindowBand`, `SidebarPaneModeRules`, `SidebarResizeRules` |
| `Sidebar.Layout.cs` | catalogue, overlays, ops, `SidebarLayoutRules`, `SidebarLayoutDoc`, visibility, filters, pin state |
| `Sidebar.Planner.cs` | `SidebarRowPlanner`, `SidebarPlanOptions` |
| `Sidebar.Feeds.cs` | feed demand, recents, new releases, the resident peek |
| `Sidebar.Store.cs` | `SidebarFileStore`, DTOs, `SidebarStoreV3`, `SidebarAccountStore`, `SidebarAccountKey`, `SidebarPinLatch` |
| `Sidebar.Store.V2.cs`, `Sidebar.Migration.cs` | the v2 reader and the one-time migration |
| `Sidebar.Accounts.cs` | the account swap |
| `Sidebar.Edit.cs` | Outline rules, `SidebarMenuModel`, `SidebarUndoRing` |
| `Sidebar.Library.cs` | the Library head rules, `SidebarLibraryShaper`, `SidebarLibraryWindow`, `SidebarLibraryState` |
| `Sidebar.Host.cs` | the `Sidebar` service, the binder, the pin store |
| `Sidebar.Cards.cs` | grid tiles |
| `Sidebar.Census.cs` | the re-plan census |
| `Sidebar.UI.cs` + `.Slot` `.Rows` `.Menus` `.Drop` `.Flyout` `.Footer` `.Edit` `.Library` | the pane and its parts |

Gone: `Sidebar.Doc.cs`, `Sidebar.Modes.cs`, `Sidebar.Customizer.UI.cs`, `Sidebar.UI.LibraryV3.cs`, `Sidebar.UI.Rail.cs`
(P2).
