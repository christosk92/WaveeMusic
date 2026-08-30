# Wavee sidebar — testing and verification

**You do not run builds or tests.** Christos builds and runs everything himself; agents must not claim
"build-verified". Report the checklist below instead.

---

## The commands to hand back

```powershell
# 1 — build (both configurations; Release surfaces what Debug structurally cannot)
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release

# 2 — the sidebar-and-neighbours filtered sweep (~840 test CASES; ~600 methods, some [Theory])
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj `
  --filter "FullyQualifiedName~Sidebar|FullyQualifiedName~Rootlist|FullyQualifiedName~PlayLog|FullyQualifiedName~ShellNav|FullyQualifiedName~WaveeExtension|FullyQualifiedName~LibraryV3"

# 3 — engine gates: ONLY if src/FluentGpu.* changed (a sidebar-only change owes nothing here)
dotnet build src/FluentGpu.slnx           # Debug
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice   # expect "ALL CHECKS PASSED"
```

The filter above is the convention this build used; it is **not** recorded anywhere in the repo (no
`FullyQualifiedName~` string exists in `docs/`, `ops/` or `.claude/`), so paste it rather than hunting for it. The
only test invocation checked in is the whole-suite form in the `wavee` skill and
`docs/plans/wavee/wavee-sidebar-extension-platform.md:322-323`
(`dotnet build Wavee.slnx -p:WaveeSkipPrivateSources=true` + `dotnet run --project src/apps/Wavee.Tests
-p:WaveeSkipPrivateSources=true --no-build`).

### Two caveats that waste an afternoon

1. **A full `dotnet test` run hangs.** Always use `--filter` plus a hard timeout.
2. **A running app locks the apphost copy** — the build fails with a file-lock error until Wavee is closed.
3. There were 8 pre-existing unrelated failures on main before this work; do not attribute them to a sidebar change
   without checking a clean tree first.

---

## The test inventory (26 files, 27 classes, ~600 methods)

All under `src/apps/Wavee.Tests/`.

### Document / reducer / templates (Wavee.Core)

| File | Class | ~Methods | Covers |
|---|---|---|---|
| `SidebarLayoutReducerTests.cs` | `SidebarLayoutReducerTests` | 115 | One test per command-table row + the reducer invariants: never mutates its input, a rejection is a no-op, depth-1 nesting, `EntityEmbed` single item, query-legality repair, lazy Pinned-override prune. Also the three duplicate/seed defects: `StaticLinks` seeds the `Links` preset (not `Shortcuts`), a store-backed kind — or a group holding one — is `KindNotDuplicable`, and a clone with an authored `TitleLocKey` keeps the key instead of freezing a literal. |
| `SidebarTemplateTests.cs` | `SidebarTemplateTests` | 17 | The five seed layouts, pinned **row for row**, plus the per-kind fact tables (`DefaultDisplay(StaticLinks)` is `Links`). |
| `SidebarShortcutsSectionTests.cs` | `SidebarShortcutsSectionTests` | 16 | **Phase 1 / Decision A.** `SidebarShortcutsSection.From`/`Renders`/`Prepend`/`ContainsRoute`; the materialisation into Classic's and Library V3's documents (incl. V3 dropping its own `v3.liked` row exactly when the band carries a `liked` Route item); and `SidebarItemCommands`' sentinel routing — a move inside Shortcuts emits `MoveTopBarItem`, a raw `MoveItem` there is an `UnknownSection` rejection. |

### Persistence

| File | Class | ~Methods | Covers |
|---|---|---|---|
| `SidebarLayoutJsonTests.cs` | `SidebarLayoutJsonTests` | 26 | The WIRE contract: model→JSON→model round trip, the exact camelCase kind strings, unknown kinds/members round-tripping untouched. |
| `SidebarLayoutStoreTests.cs` | `SidebarLayoutStoreTests` | 26 | Atomic tmp→`File.Replace`, one rotated `.bak`, fault classification (None/Corrupt/TooNew/Unreadable), `.bak` recovery, preserve-don't-destroy. |
| `SidebarLayoutV2MigrationTests.cs` | `SidebarLayoutV2MigrationTests` | 11 | The version ladder through the real store; v1→v2 identity (sections, pins, V3 overlay, unknown members preserved). |
| `SidebarBootstrapTests.cs` | `SidebarBootstrapTests` | 19 | Fresh-install truth table per witness, legacy v0→v1 pane-key migration, "an existing install never sees the chooser", idempotence via `sidebar.bootstrap.version`. |
| `PlayLogStoreTests.cs` | `PlayLogStoreTests` | 23 | The 200-entry ring cap, the context-first dedupe read API the sidebar consumes, context classification, `play-log.json` round trip, corrupt-file fallback. |

### Data pipeline

| File | Class | ~Methods | Covers |
|---|---|---|---|
| `SidebarProjectionTests.cs` | `SidebarProjectionTests` | 31 | Per-kind field derivation, flavor mask + chip visibility, `SortStamp`/first-seen fallback, folder recursion, diacritics-insensitive search, recency, pins-first partition. |
| `SidebarSortTests.cs` | `SidebarSortTests` | 15 | The five comparators: totality, never-visited block under Recents, empty creators last, Custom stable append + ignores `desc`. |
| `SidebarProjectionBinderTests.cs` | `SidebarProjectionBinderTests` (+ nested `StubSource : SidebarDataSourceBase`) | 44 | The binder's **pure** half: the rebuild trigger fold, the Entries driver, M1 contribution resolution. Copy `StubSource` for a new source's tests. |
| `SidebarDataSourceTests.cs` | `SidebarDataSourceTests` (+ nested `StubSource`) | 25 | Opaque-config readers, the contribution-id scheme, registry/host resolution (missing/disabled/live), service-health translation, domain→entry mappers. |
| `SidebarRowPlannerTests.cs` | `SidebarRowPlannerTests` | 33 | The pane render contract: the row **sequence** per section kind, degraded states, a 10 000-entry realization. |
| `SidebarRailPlannerTests.cs` | `SidebarRailPlannerTests` | 17 | `ShowInRail` → tiles, the caps, heading collapse; the rail must not disagree with the expanded pane. |
| `RootlistTreeTests.cs` | **`RootlistTreeBuilderTests`** (class name ≠ file name) | 16 | Flat rootlist marker stream → the recursive `PlaylistNode` tree; nested shape; malformed markers. |
| `RootlistFollowTests.cs` | `RootlistFollowTests` | 12 | Follow/unfollow writes over a recording transport (the backend half of adding/removing a sidebar playlist). |

### Modes / documents / renderer invariants

| File | Class | ~Methods | Covers |
|---|---|---|---|
| `SidebarBuiltInDocumentTests.cs` | `SidebarBuiltInDocumentTests` | 7 | Classic as a **locked** built-in document: its IA and the Cozy+Subtitles density intent behind the 44-DIP rows. |
| `LibraryV3DocumentTests.cs` | `LibraryV3DocumentTests` + `LibraryV3ViewTests` | 23 + 15 | V3 as a synthesized ephemeral document (view state → sections + query + display) and the content **order** (tree re-grouping, drill slice, materialized custom order). |
| `SidebarPaneInvariantTests.cs` | `SidebarPaneInvariantTests` | 13 | `SidebarPaneFrameSnapshot` (a settled rendered pane width is exactly 56 or a valid expanded width) (the former source-scan drift guards were removed on 2026-08-22 — tests never read source; the rules below are documented here and covered by behaviour where a pure seam exists): the context menu hangs off a childless shield and not the pane root; every fixed chrome band expresses its inset through the one named content lane rather than the retired literal; and both `Reorderable` wrap sites in `SidebarPaneSlot` fill their slot. A source scan skips (not fails) on a binary-only run. |
| `SidebarNavBandTests.cs` | `SidebarNavBandTests` | 19 | `SidebarNavBandModel` — the shortcut band's pure SHAPING rules (item target → tile shape, tile → the route key a selection mark reads, document order, the truncation bound). Named for the MODEL: the `SidebarNavBand` component it was written against is gone, and the model has no production caller left. Its materialisation into a section lives in `SidebarShortcutsSectionTests`. |
| `SidebarModeStateTests.cs` | `SidebarModeStateTests` | 15 | Per-mode remembered state + per-design width tiers: `SidebarPaneState` snapshot/restore/latch behind `SwitchDesign`, `SidebarDesignInfo`, `ShellResponsiveLayout`, over `MemoryAppSettings`. |
| `SidebarPinStoreTests.cs` | `SidebarPinStoreTests` | 19 | The shared pin store + `SidebarPinId` mapping (rides `VirtualCollectionSignalShim` for `Signal<int>`). |
| `SidebarDesignGatingTests.cs` | `SidebarDesignGatingTests` | 19 | The one-time chooser gate + closing marker, the three preview cards' values, the "Customize sidebar" affordance rule. |
| `ShellNavDestTests.cs` | `ShellNavDestTests` | 10 | `ShellNav.Dest` route→(title, glyph); the `show:` regression. Carries its own inline `FluentGpu.Controls` `Icons`/`Route` shim. |

### Extension platform / customizer

| File | Class | ~Methods | Covers |
|---|---|---|---|
| `WaveeExtensionRegistryTests.cs` | `WaveeExtensionRegistryTests` | 40 | `WaveeRegistryTable<T>` namespaced keys + first-wins duplicates, action targeting, `PinRowRule`. |
| `SidebarCustomizerLayoutTests.cs` | `SidebarCustomizerLayoutTests` | 25 | The companion page's pure model: the searchable palette + filter, the **Destinations** group (the pinnable-routes ∪ three-extras set, its `dest:` ids, one seeded `AddSection` each, `AppendsToSelection`, `CanDrag`'s click-only rows), the display projection, the opaque extension-config rewriter. The tier-ladder, command-fit and outline cases were **deleted with their types** — do not re-add them. |
| `SidebarEditPlanTests.cs` | `SidebarEditPlanTests` | 27 | **Phase 2 / Decision B.** `SidebarEditPlan`'s pure rules (`ShowsBody`, `HasBody`, `SectionsReorderable`, `Fold`, `CardCount`, `IsPinnedCard`, `SectionIdAt`) and the two band-slot → command translations. `ToMoveSection`/`ToAddSection` are driven over a row array built from the **render** document (Shortcuts head at plan index 0) while the command is asserted against the **persisted** one, so the off-by-one those two index spaces invite fails here. |

Adjacent, one relevant test: `StoreLibrarySourceTests.GetPlaylists_OverlaysResolvedOwnerName_ForSidebarAndHomeSummaries`.

**Where a new test goes** is decided by the tests project's source-include list, not by preference — see
[pitfalls.md](pitfalls.md#tests). If the logic you want to assert lives in a non-included file, move the *decision*
into the pure half rather than adding an include.

---

## Screenshot probes

Both flags are read in `src/apps/Wavee/Features/Diagnostics/WaveeNavProbe.cs` (`:63-64`) and gate the hook wiring
in `WaveeShell.cs:186`. Artifacts land in `ProbeArtifacts.Dir` = an `artifacts\` folder **beside the log file that
run produced** (fallback `%TEMP%\wavee\artifacts`). Both need an **authenticated session**: they wait up to
`WAVEE_PROBE_AUTH_FRAMES` frames (default 7200, clamped 240…36000) for `WaveeShell.ProbeNav`, send a real
`WM_ACTIVATE` so the focused Mica/chrome arm is captured, and set the client size to 1500×950.

| Flag | Output |
|---|---|
| `WAVEE_SIDEBAR_MODE_SHOT=1` | 6 PNGs — `sidebar_{classic,v3,curated}_{expanded,rail}.png`. Log prefix `[sidebar-mode-shot]`. Leaves the pane expanded. |
| `WAVEE_SIDEBAR_V3_SHOT=1` | 8 PNGs — `sidebar_v3_view_{compactlist,list,compactgrid,grid}.png` (at filter All) then `sidebar_v3_filter_{playlists,podcasts,albums,artists}.png` (at view List). Log prefix `[sidebar-v3-shot]`. Restores filter All + view List. |
| `WAVEE_SIDEBAR_VISUAL_SHOT=1` (sibling) | stage-selectable via `WAVEE_SIDEBAR_VISUAL_STAGE` ∈ `curated\|customizer\|canvas\|expanded`. |

The probe hooks on `WaveeShell` (verbatim, `:152-167`) — note the `ProbeSidebar` prefix on all of them:

```csharp
internal static Action<int>?  ProbeSidebarMode;      // = _sidebar.SwitchDesign(SidebarDesignInfo.FromInt(mode))
internal static Action<int>?  ProbeSidebarDesign;    // the same delegate under a second name
internal static Action<int>?  ProbeSidebarV3View;    // = _sidebar.SetV3View(view)
internal static Action<int>?  ProbeSidebarV3Filter;  // = _sidebar.SetV3Filter(filter)
internal static Action<bool>? ProbeSidebarCompact;
internal static Action<bool>? ProbeSidebarDrawer;
internal static Func<SidebarPaneFrameSnapshot>? ProbeSidebarPaneFrame;
```

Every hook goes through the real production seam (`SwitchDesign`/`SetV3View`/`SetV3Filter`), never a poked signal —
keep it that way, or a probe stops proving anything.

**These are feature-diagnostic flags, not feature switches.** New user-facing toggles go in `WaveeSettings` +
a SettingsPage row, never a new env var.

---

## When engine gates apply

`src/FluentGpu.*` is **out of scope** for sidebar work. A sidebar-only change owes **no** `FluentGpu.slnx` build and
**no** VerticalSlice run. If the sidebar genuinely needs an engine change, hand it off rather than editing — and
then the full regime applies (`FluentGpu.slnx` Debug **and** Release + `ALL CHECKS PASSED`), because the two
configurations compile different diagnostic arms.

For reference, from the last engine-touching wave of this work: the full VerticalSlice run was **894/894 ALL CHECKS
PASSED** in Debug. A *Release run* shows a handful of structural diag-arm failures
(ReuseGuard/BindContract/BackwardsWriteGuard/ScrollTrace tripwires + refresh-swr) — per `CLAUDE.md`, **Release is a
BUILD gate only**; do not chase those.

---

## The eyeball checklist to hand the user

Metric identity across all three modes side by side (left edges, row heights, header typography) · badge quietness
(no accent count pills anywhere) · the pinned drop zone at rest (56) and during a compatible drag (72) · empty
dynamic sections showing the quiet per-kind hint rather than vanishing · collapse/expand motion including the
chevron rotation · pins at 0 / 1 / 13+, a circular artist pin, a route-glyph pin · drag reorder and drop-from-a-
playlist-row · V3 chips / search / drill against the shared renderer · the customizer at ≥1320 / 1000 / 820 /
narrow, the saved-locally dot, and **zero `[key]` text anywhere**.
