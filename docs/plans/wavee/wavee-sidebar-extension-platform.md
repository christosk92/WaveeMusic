# Wavee Sidebar Visual Remediation and Custom Navigation Completion

## Summary

Wavee's three-mode sidebar foundation is substantially implemented, but the built-in Curated surface and its
customizer are not visually complete or ready to ship. The implementation transcript shows the model, persistence,
projection, pins, modes, virtualization, extension contracts, and engine motion work landing before the customizer
wave was interrupted twice by session limits. The customizer was compile-fixed afterward, but no final visual-probe
and screenshot-remediation loop happened.

The immediate release gate is therefore a visual and reliability completion pass, not a rewrite of the navigation
model. External extension-host work stays paused until the built-in sidebar and customizer pass the acceptance matrix
in this document.

The selected north star is **Rich Wavee showcase**: premium artwork, selective depth, live previews, and expressive
local transitions. Ordinary navigation stays quiet enough that featured media remains meaningful.

Research anchors:

- [Microsoft CommandBar guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar)
  establishes command priority and overflow as the response to width pressure.
- [Microsoft NavigationView guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)
  establishes stable Expanded, Compact, and Minimal/Drawer states.
- [Fluent 2 motion](https://fluent2.microsoft.design/motion) reserves stronger choreography for meaningful local
  hierarchy changes; top-level navigation should remain quick and calm.
- [Spotify's desktop library redesign](https://newsroom.spotify.com/2023-06-20/spotify-desktop-experience-redesign-your-library-now-playing-views-customize/)
  validates library-only filtering, compact navigation, resizing, pins, and drag/drop.
- [NN/g customization research](https://media.nngroup.com/media/reports/free/Customization_Features_Done_Correctly.pdf)
  supports strong templates/defaults, previews, undo, and discoverable add controls.
- [WCAG 2.2 dragging guidance](https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html) requires
  single-pointer alternatives to drag, so Move up/down/to-section remains available in menus.

## Current defects

### Curated sidebar

- Ordinary sections are separated by repeated full-width rules, producing a settings-form rhythm.
- Empty Pinned is a permanent 72-DIP dashed billboard.
- Empty dynamic sections consume large vertical bands.
- Bright accent count pills overpower labels and artwork.
- Headers, rows, artwork, dividers, and empty states use conflicting density systems.
- At the 460-DIP maximum width, content stretches without gaining useful structure.
- Lower content feels clipped against the fixed player slot.

### Customizer

- The docked sidebar can settle as an approximately 24-DIP clipped sliver. At rest it must be exactly 56 DIP or
  an expanded width in the 240-460 range.
- Done clips off the right edge because the command row has no pressure/overflow model.
- Divider sections appear as ordinary grippable content rows.
- Selected rows use a blunt gray slab without a Fluent accent indicator.
- The inspector is a dense stack of individually bordered settings cards.
- Rename appears disabled; grippers and overflow controls dominate at rest.
- The last inspector controls appear cut at the player boundary.
- Large screens lack a persistent live preview; compact screens do not reduce commands gracefully.

## Custom-navigation capability

The typed model already covers the requested scenarios:

| Desired navigation | Model |
|---|---|
| Recently played, maximum N | `JumpBackIn`, `Recents=Played`, `MaxItems=N` |
| Recently visited | `JumpBackIn`, `Recents=Visited` |
| Only artists | `EntityList` with `Kinds=Artists` |
| Only selected artists | `EntityList.IncludeUris` |
| One artist/album/playlist/show | `EntityEmbed` or a fixed Custom Group item |
| An artist's top tracks | `wavee.artist.topTracks` contribution with artist configuration |
| A track shortcut that plays immediately | `SidebarItemTarget.Track` |
| Play/pause/shuffle/pin/navigate/another operation | `SidebarItemTarget.Action` |
| Mixed routes, entities, tracks, and actions | `CustomGroup` |
| New application data | `ISidebarDataSource` contribution |
| New trusted native UI | Source-compiled extension registration |

"Anything" means any registered navigation, action, entity, track, or bounded data source. It deliberately does not
mean arbitrary untrusted C# or raw FluentGpu elements deserialized from `sidebar-layout.json`. Untrusted contributions
remain declarative and host-rendered; trusted source-compiled extensions may provide native FluentGpu components.

## Visual and interaction specification

### Curated sidebar

- Standard sections use whitespace, not automatic rules. A divider appears only when explicitly authored.
- Explicit dividers render as a semantic spacer or quiet hairline, never a normal content row.
- The Wavee Curated template explicitly authors quiet dividers between Pinned, Recently Played, Library shortcuts,
  and Playlists; these are intentional composition, remain user-removable, and are preserved during migration.
- Empty Pinned is a compact 40-DIP hint at rest. It expands to the dashed drop surface only for a compatible active
  drag or keyboard pin-placement operation.
- Empty dynamic sections hide their body by default. Actionable states render one compact action row.
- Recently Played defaults to a two-column artwork presentation, maximum four items, at normal/wide pane tiers and
  a single-column artwork row at the narrow tier.
- Collection shortcuts use compact icon tiles. Playlists and large collections remain virtualized lists.
- Only spotlight, now-playing, and artwork-grid content receives card depth. Ordinary navigation stays chromeless.
- Headers remain secondary 12/600 text with trailing actions revealed on hover/focus.
- Row extents are centralized at 40/48/56 DIP for Compact/Cozy/Comfortable.
- Selection uses a 3-DIP accent indicator plus subtle fill.
- Counts use tertiary text or neutral badges. Accent is reserved for selection, playback, new state, and primary action.
- The 56-DIP rail keeps 40-DIP tiles, semantic dividers, tooltips, and mode-specific ordering.
- Spacing, radii, color, elevation, and motion come from `Spacing.*`, `Radii.*`, `Tok.*`, `Interaction.*`, and
  `MotionTok.*`.

Add an optional persisted empty-state policy:

```csharp
public enum SidebarEmptyBehavior : byte
{
    Default = 0,
    HideBody = 1,
    CompactHint = 2,
    ActionCard = 3,
}
```

Per-kind defaults:

- Pinned: `CompactHint`
- Recently Played, Entity List, New Releases: `HideBody`
- Concerts without location: `ActionCard`
- Missing/failed extension: `ActionCard`

### Customizer

**This section previously specified a four-tier region ladder (Canvas / Full / Compact / Narrow), an outline column
beside a docked inspector and a preview column, and a command-fit resolver. None of that ships, and the numbers it
printed never matched the build either — the tier thresholds shipped as 1320/1000/820, not 1480/1180, and the outline's
rows shipped at 52/44 DIP, not 44/36. The ladder is now gone entirely rather than re-numbered.** What replaced it:

**The docked sidebar IS the canvas and the preview.** "Customize" is a MODE over the live pane, not a page that redraws
it. The pane gains edit affordances through ONE new `SidebarPaneConfig` member — `Func<SidebarEditState?>? Edit`, a
delegate like every other config member — and plans through a second planner entry point,
`SidebarRowPlanner.BuildEdit`, which emits one uniform `SectionCard` row per top-level section and plans the ONE
expanded section's real body underneath it with the same per-kind planners the live pane uses. There is no second
renderer, no nested scroller and no abstraction of the sidebar to drift from it. The pure rules live in
`Features/Sidebar/Data/SidebarEditPlan.cs` (source-included by the tests): `ShowsBody`, `SectionsReorderable`, `Fold`,
`CardCount`, `IsPinnedCard`, `ToMoveSection`, `ToAddSection`.

Canvas rules:

- Every section is a uniform-height header card — grip · kind glyph · title · count · eye · "…" — and ONE section
  expands at a time to reveal its real rows. Two reasons: a 60-row expanded sidebar makes section dragging a
  scroll-fight, and uniform card pitch means `Reorderable` needs no variable-extent insertion geometry. A "Show section
  contents" switch on the companion page turns the collapse off for item-level work, and deliberately DISARMS section
  drag (the card run is then no longer contiguous).
- Section reorder is a `Reorderable(SidebarEditPlan.SectionDragKind)` over those cards, committing `MoveSection`. Item
  reorder inside an expanded section commits through `SidebarItemCommands`, so a move inside the materialised Shortcuts
  section emits `MoveTopBarItem` rather than `MoveItem`.
- Hidden sections keep their card, dimmed with an eye-off badge, and never reveal a body — their rows are not in the
  live sidebar, and drawing them would be the editor lying about the artifact it edits.
- Per-section options are a POPOVER anchored to that card's "…", reusing the generated control set from
  `SidebarPropertyPanel` (`CzRow.*`, `CzToggleRow`, `CzSelectorRow`, `CzNumberRow`, `CzQueryBlock`, `CzConfigRow`) —
  only its host changed, from a docked column to a popover.
- Structural drag is armed only in edit mode. Outside it, pin/unpin and pin reordering stay live as they always were.
- Pointer, touch, keyboard lift/drop and the explicit Move up / Move down menu items dispatch the same reducer command,
  so drag is one of several ways and never the only one.
- Grab / move / drop / cancel are announced through the engine's live-region seam (`Reorderable.AnnounceText` over
  `FluentGpu.Input.Announcer`), keys `sidebar.customizer.reorder{Grabbed,Moved,Dropped,Cancelled}`.

Companion page rules (`sidebar-customize`, one scrolling column at EVERY width — no tier, no region, nothing to demote):

- Header: Back · title · Undo · Redo · Reset · Done. `Done` returns through `HistoryStore.BackCtx`, not a hard
  `Go("home")`.
- Presets: the three designs as a segmented control (making the force-switch to Custom visible and reversible), then
  the five `SidebarTemplates` as cards.
- The palette is PERSISTENT — no popover, no accordion — grouped as `SidebarPalette.Groups`, with **Destinations
  first**: real app pages generated from `SidebarPinId.PinnableRoutes` ∪ `{settings, api-console, concerts}` and
  labelled through `ShellNav.Dest`, so typing "home" answers with **Home** instead of an empty "Links" section. Each is
  one `AddSection(StaticLinks, Item: route)` — one undo step, a working row. Chips are `Drag.Source`s onto the canvas;
  clicking still adds, so drag is never the only way.
- Hidden sections are listed as a recovery surface, so nothing is ever unreachable.
- Advanced: reset to default plus a link to `sidebar-layout.json` as the documented power-user escape hatch.

### Motion and reactivity

- Design switch: keyed, opacity-only `MotionTok.ControlFast`.
- Section expansion: `MotionTok.ContentResize`.
- Reorder: `MotionTok.ItemPlacement`.
- Section-card selection and drop activation: `MotionTok.ControlFaster`.
- Drawer/flyouts use the existing overlay transition.
- Reduced motion is handled by the motion-token policy, not component branches.
- Drag, hover, width, scroll, and opacity use bindings/compositor transforms.
- Lists stay virtualized; pending states use `Skel.Region` over the real row factory.
- No signal writes occur during render and no constructor value is treated as live state.

## Layout invariants

Add a diagnostic snapshot:

```csharp
public readonly record struct SidebarPaneSnapshot(
    SidebarDesign Design,
    bool UserCollapsed,
    bool PresentedCompact,
    float PreferredExpandedWidth,
    float RenderedPaneWidth,
    float ExpandedOpacity,
    float RailOpacity);
```

At a settled frame:

- Compact requires rendered width `56 +/- 0.5`.
- Expanded requires rendered width in `[240,460]`.
- Exactly one content layer is hit-testable.
- Content starts at the pane's trailing edge.
- Drawer state never leaks into the docked pane.
- Design switch batches width, collapse, and design writes before remount.
- Screenshot capture waits for a valid terminal state.

Diagnose the owner of any invalid width. Never mask it with an arbitrary `MinWidth`.

## Error handling and observability

Add a reactive persistence result:

```csharp
public enum SidebarPersistenceFault : byte
{
    None,
    Corrupt,
    TooNew,
    Unreadable,
    IoFailure,
    ConfigTooLarge,
    DocumentTooLarge,
}

public readonly record struct SidebarWriteResult(
    bool Success,
    SidebarPersistenceFault Fault,
    int Bytes,
    long ElapsedMs,
    string? SafeDetail);
```

- `SidebarPreferences.Activate(Action<Action> post)` installs the UI-thread marshal.
- Store completions publish through `SidebarPreferences.PersistenceHealth`.
- I/O errors receive the same persistent customizer warning as size faults.
- A successful save clears the fault.
- Corrupt files remain preserved; Start fresh moves them aside first.
- Play-log corruption preserves a `.corrupt` copy and starts empty in memory.
- Play-log write failures retain current in-memory recents.
- Source errors replay last-good rows with a stale badge; without cache they render a compact retry/manage row.
- Raw `ex.Message` toasts are replaced by localized copy; exceptions go to the structured log.

Stable `sidebar` events:

- `sidebar.mode.changed`
- `sidebar.pane.invariant_failed`
- `sidebar.customizer.command.rejected`
- `sidebar.layout.recovered`
- `sidebar.layout.load_failed`
- `sidebar.layout.save_failed`
- `sidebar.layout.save_recovered`
- `sidebar.source.failed`
- `sidebar.source.recovered`
- `sidebar.source.cache_replayed`
- `sidebar.action.failed`
- `sidebar.projection.slow`

Allowed fields: design, tier, command kind, section kind, contribution ID, safe failure category, elapsed milliseconds,
row count, byte count, cache status, rendered pane width. Never log search text, titles, URIs, secrets, opaque
configuration, or full paths. Repeated source failures dedupe by `(sourceId, category, healthEpoch)`. No per-frame or
per-row events. Projection logs a warning only above 8 ms.

## Implementation workflow

1. Coordinator writes this plan, records the baseline, and owns all shared contracts, integration, builds, and visual
   review.
2. Correctness lane implements command fit, pane invariants, empty behavior, persistence health, logging, and pure tests.
3. Curated lane refines shared/live primitives, row hierarchy, empty states, dividers, badges, rail, and motion.
4. Customizer lane implements the live-pane edit canvas (section cards, section reorder, the options popover) and the
   one-column companion page (presets, the persistent palette incl. Destinations, hidden sections), and focus.
5. Coordinator integrates, runs the centralized verification, captures every visual state, and iterates before docs or
   extension-host work resumes.

Delegated lanes do not run builds or claim visual completion. The coordinator inspects original-resolution artifacts.

## Verification and acceptance

Pure coverage:

- `SidebarCustomizerLayoutTests`: the palette table + filter, the Destinations set/order/drag rules, the display
  projection, the opaque extension-config rewriter.
- `SidebarEditPlanTests`: which sections reveal a body, when section drag is armed, the plan `DepKey` fold, card counts,
  and the two band-slot → command translations (`ToMoveSection` / `ToAddSection`) against the PERSISTED indexes.
- `SidebarShortcutsSectionTests`: the materialised Shortcuts section in all three documents, and the sentinel-id
  routing that makes a move inside it emit `MoveTopBarItem`.
- `SidebarPaneInvariantTests`: expanded/rail/drawer/mode switch/resize; a settled 24-DIP pane is invalid.
- `SidebarEmptyBehaviorTests`: defaults, persistence, reducer, planner.
- `SidebarPersistenceHealthTests`: I/O failure, recovery, post-to-UI, budgets, corruption, redaction.
- `SidebarSourceHealthTests`: cache replay, isolated failure, deduped logging, recovery.
- Existing reducer, template, projection, rail, pin, and migration tests remain green.

Visual matrix:

- 1395x1107 and 577x987 at 100%, 125%, 150%, and 200%.
- Curated widths 240, 280, 320, 360, and 460.
- Expanded, rail, drawer, and settled design-switch states.
- The customize mode at a narrow and a wide window: the docked pane is the canvas at BOTH (there is no width below
  which the preview disappears, which was the whole point of removing the tier ladder).
- English, Dutch, Korean, and long synthetic strings.
- Light, dark, high contrast, and reduced motion.
- Empty, pending, ready, stale, failed, corrupt-layout, and save-failed states.
- Pointer and keyboard reorder.

Required outcomes:

- No command, row, pane, or options-popover control clips.
- Done is always fully visible.
- Settled pane width is exactly 56 or within 240-460.
- The companion page reaches its final control; nothing paints under the player.
- Automatic dividers are gone.
- Empty Pinned stays one compact row until activation.
- A Divider section's card names it rather than drawing a hairline with no label.
- Selection retains an accent indicator at rest/hover/press.
- No raw localization key or exception message reaches UI.
- A 10,000-entry library realizes no more than 60 rows.
- `FrameStats.HotPhaseAllocBytes == 0` and `FrameStats.RootRelayoutEscapes == 0` on steady frames.

Centralized commands:

```powershell
dotnet build Wavee.slnx -p:WaveeSkipPrivateSources=true
dotnet run --project src/apps/Wavee.Tests -p:WaveeSkipPrivateSources=true --no-build
```

Do not rebuild the engine or run VerticalSlice unless this remediation changes Engine or Controls. If unavoidable:

```powershell
dotnet build src/FluentGpu.slnx
dotnet run --project src/FluentGpu.VerticalSlice
```

and require `ALL CHECKS PASSED`.

## Boundaries

- Preserve the dirty working tree.
- Sidebar/layout/extension state remains device-local.
- Root-list writes, Spotify folder CRUD, and track-to-playlist drops remain separate.
- Untrusted code never executes inside Wavee.
- Built-in visual completion blocks further extension-host work.
- The user performs the final interactive application run after automated and screenshot verification.
