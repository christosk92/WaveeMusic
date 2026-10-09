# Wavee sidebar — testing and verification

**You do not run builds or tests.** Christos builds and runs everything himself; agents must not claim
"build-verified". Hand back the checklist below instead.

---

## The commands to hand back

```powershell
# 1 — build (both configurations; Release surfaces what Debug structurally cannot)
dotnet build Wavee.slnx
dotnet build Wavee.slnx -c Release

# 2 — the sidebar-and-neighbours filtered sweep
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj `
  --filter "FullyQualifiedName~Sidebar|FullyQualifiedName~Rootlist|FullyQualifiedName~PlayLog|FullyQualifiedName~ShellNav"

# 3 — the same sweep in Release (the optimising JIT shows bugs Debug does not)
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj -c Release `
  --filter "FullyQualifiedName~Sidebar|FullyQualifiedName~Rootlist|FullyQualifiedName~PlayLog|FullyQualifiedName~ShellNav"

# 4 — engine gates: ONLY if src/FluentGpu.* changed (a sidebar-only change owes nothing here)
dotnet build src/FluentGpu.slnx           # Debug
dotnet build src/FluentGpu.slnx -c Release
dotnet run --project src/FluentGpu.VerticalSlice   # expect "ALL CHECKS PASSED"

# 5 — compile check while iterating (not a substitute for 1)
dotnet build src/apps/Wavee/Wavee.csproj
```

The filter is not recorded anywhere else in the repo, so paste it rather than hunting for it. The whole-suite form is in
the `wavee` skill and `CLAUDE.md` (`dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`). Run the whole suite before a
release.

## Which test file covers what

Pure classes are tested directly. Each row names the class under test and its file.

| Area | Class under test | Test file (`src/apps/Wavee.Tests/`) |
|---|---|---|
| Catalogue: what each layout may hold, locked and movable | `SidebarCatalogue`, `SidebarCatalogue.ItemsOf` | `SidebarCatalogueTests.cs` |
| Ops and caps | `SidebarLayoutRules.Apply` | `SidebarLayoutRulesTests.cs` |
| Visibility of sections and items | `SidebarVisibilityRules` | `SidebarVisibilityRulesTests.cs` |
| Edit rules (Outline, move, hide) | `SidebarEditRules` | `SidebarEditRulesTests.cs` |
| Menu rows and verbs | `SidebarMenuModel` | `SidebarMenuModelTests.cs` |
| Undo ring (50, batch, memory only) | `SidebarUndoRing` | `SidebarUndoRingTests.cs` |
| Planner and row plan | `SidebarRowPlanner`, `PlanFixture` | `SidebarPlannerTests.cs` |
| Row geometry | `SidebarRowGeometry`, `SidebarRowExtents` | `SidebarRowGeometryTests.cs` |
| Pane modes and bands | `SidebarPaneModeRules` | `SidebarPaneModeRulesTests.cs` |
| Resize and settle | `SidebarResizeRules` | `SidebarResizeRulesTests.cs` |
| Library head and empty states | `SidebarLibraryHeadRules`, `SidebarLibraryEmptyRules` | `SidebarLibraryHeadRulesTests.cs` |
| Library shaping and state | `SidebarLibraryShaper`, `SidebarLibraryState` | `SidebarLibraryShaperTests.cs`, `SidebarLibraryStateTests.cs` |
| Type-ahead | `SidebarTypeAheadRules` | `SidebarTypeAheadRulesTests.cs` |
| Pills | `SidebarPillRules` | `SidebarPillRulesTests.cs` |
| Subtitles | `SidebarSubtitleRules` | `SidebarSubtitleRulesTests.cs` |
| Feeds: demand, recents, new releases | `SidebarFeedDemands`, `SidebarRecentsRules`, `SidebarNewReleasesRules` | `SidebarFeedsTests.cs` |
| Projection and binder inputs | `SidebarProjectionInput` | `SidebarProjectionTests.cs` |
| Drop and rootlist slot | `RootlistSlotResolver`, `RootlistSlotMapper` | `SidebarDropTests.cs` |
| Cards | `SidebarCardRules` | `SidebarCardsTests.cs` |
| Pins: rules and refusal | `SidebarPinRules`, `SidebarPinId`, `SidebarPinStore` | `SidebarPinTests.cs` |
| Pin sync with Spotify | `LibraryPinSync` | `SidebarPinSyncTests.cs` |
| Per-account store | `SidebarAccountStore`, `SidebarAccountKey` | `SidebarAccountStoreTests.cs` |
| Device document v3 round-trip | `SidebarStoreV3` (`sidebar.json`) | `SidebarStoreV3Tests.cs` |
| File store (temp, rotate, `.bak`, `.corrupt`) | `SidebarFileStore` | `SidebarFileStoreTests.cs` |
| v2 → v3 migration | `SidebarMigrationV2` | `SidebarMigrationV2Tests.cs` |
| Census of re-plan causes | `SidebarReplanCensus` | `SidebarReplanCensusTests.cs` |
| Revision and re-plan | `Sidebar` revision folding | `SidebarRevisionTests.cs` |
| Wiring and boot order | boot facts | `SidebarWiringTests.cs` |
| Cross-cutting shell wiring | `Shell` and `Sidebar` boot | `BootOrderingTests.cs`, `ShellRoutesTests.cs` |

If a name here no longer matches a file, search by the class name: the table is a map, not a contract.

## How to test the pieces

- **The planner.** Build a fixture with `PlanFixture` (in `SidebarPlannerTests.cs`): `PlanFixture.Playlist("pl:a")` makes a
  playlist, and `PlanFixture.Doc(layout, …)` makes a `SidebarLayoutDoc` for a layout. Pass the fixture to
  `SidebarRowPlanner.Build` with a `SidebarProjectionInput` and assert on the rows. Use `new SidebarProjectionInput(
  PlaylistTree: […])` for the smallest case.
- **The file store.** Use a temp folder created per test, write through `SidebarFileStore`, and assert on the files
  that appear: the document itself, the rotated `.bak`, and a `.corrupt` file when the input is unreadable. Delete the
  folder in `Dispose`. Do not touch the real profile folder.
- **Ops.** Call `SidebarLayoutRules.Apply` with a state and an op. Assert `Changed`, `Reject`, and the new state. A
  refused op must return the same state and `Changed == false`.
- **The undo ring.** Push entries and assert the capacity of 50, the batch behaviour, and that collapse toggles never
  appear.

## The "no source-text tests" rule

A test never reads, greps or parses production source. Extract the decision into an engine-free pure class and unit-test
that class. The sidebar follows the pattern set by `SetupGating`, `AppUpdateToasts`, `ShutdownUpdatePolicy` and
`ReleaseNotesRange`. A test that asserts a string is present in a `.cs` file is wrong, however convenient.

## What owes an engine gate run

Sidebar-only changes owe no engine gate run. The engine (`src/FluentGpu.*`) is off-limits from sidebar work: if the
sidebar needs an engine change, hand it off to the engine repo. Only then run step 4 above.

## Known limits of the tests

The renderer (`PaneView`, `PaneSlot`, the Library head) is engine-bound and has no unit tests. Its behaviour is checked
by the build and by hand in the running app (`dotnet run --project src/apps/Wavee -- --fake` for an offline demo). Keep
new decisions out of the renderer, so they can be tested.
