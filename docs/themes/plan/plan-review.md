# Review of `technical-plan.md` (step 4 theming plan)

Reviewer: plan verifier, 2026-10-11. Read-only, against:
- the blueprint `abstraction-map.md` (BP);
- `map-shell.md` (MS), `map-pages.md` (MP), `map-engine.md` (ME), `research-external.md`;
- the owner memory files;
- the code: WaveeMusic `main` @21abd98e, fluent-gpu `main` @580b04464, PR 205 worktree `C:\WAVEE\sidebar\WaveeMusic` (@59fd96c1, engine @fadcbe89e), salvage worktree `C:\WAVEE\merge\WaveeMusic` (@40bb9b8a).

Three Opus helpers checked the engine claims, the app claims and the coverage. I re-checked every finding marked blocker against the code myself. Nothing was edited, built or run.

Notation:
- **TP n** = line in `technical-plan.md`.
- App paths are relative to `WaveeMusic\src\apps\Wavee\`.
- `E\` = `fluent-gpu\src\`.

**Totals: 7 blockers, 24 majors, 41 minors.**

The architecture direction is sound: structure × style, view slots, policies and capabilities, analyzer fences instead of source scans, a golden guard first, and a separate Classic tuning phase. The plan fails as written in four places:
1. **The live switch.** It does not produce two complete, frozen frames.
2. **Switch migration.** It cannot be sequenced under the plan's own analyzers.
3. **The settings bootstrap.** It loses user preferences when lanes merge out of order.
4. **The golden guard.** It is built on a headless host and a capture path that do not exist or cannot see what they must prove.

---

## BLOCKERS

### B1. The old root is not frozen during the cross-fade: `RethemeAll` re-themes it at once
**Plan.**
- One flush runs `Tok.Use` → `SwitchRoot` → "`RethemeAll` is skipped for the frozen old root" (TP842-846).
- The E2 acceptance reads "every frame is either all-old, all-new, or old+new inside exactly two layer groups" (TP795-797).

**Code.**
- `ExitFrozen` freezes component render effects only. `E\FluentGpu.Engine\Reconciler\Reconciler.cs:2144-2147` says: "Scope is component render-effects only — node-level property binds still fire".
- `RethemeAll` (`Reconciler.cs:681-696`) schedules every `_comps` entry **and every `_nodeBindings` list across the whole scene**: bound colours, `Flow.For` rows, Show and skeleton boundaries.
- `Tok` is process-global (`Dsl\Tokens.cs:249-256`). `Paint` polls `Tok.Epoch` and calls `RethemeAll` (`AppHost.cs:6292`).
- Result: the moment `Tok.Use(next)` runs, the outgoing root's bound brushes and boundaries repaint in the **new** theme while they fade out. Every switch frame is a hybrid, which is exactly the "wrong-theme frame" the owner forbids.
- The colour half is also inverted from what the plan assumes. Re-rendered element `ColorF`s cross-fade through `ThemeTransitionOr` (`Reconciler.cs:5170, 5984`). **Bound channels and token brushes snap** (`:679-680`, `:2730-2743`). Shadows, gradients, icon masks and `ListRowEl` cells snap too.

**Fix (engine).** Make the outgoing frame immutable for real.
- **Preferred: snapshot.** Render the old root once into a layer target (the research §4.3 "snapshot old" pattern) and fade that texture. Unmount the old tree in the same flush.
- **Or: scope the retheme.** Add a subtree-scoped `RethemeAll(root)` plus a bind-freeze flag honoured by `_nodeBindings` scheduling under `ExitFrozen`.

**Acceptance.**
- In the headless `root-swap` suite, the old group's draw lists are byte-identical across all frames of the fade: same colours, radii and glyphs as the last pre-switch frame.
- The new group's draw lists never contain old-definition values.

### B2. The new root's first frame is not complete, so the fade reveals an unfinished frame that then jumps
**Plan.**
- "cross-fades two complete frames" (TP38-40).
- The new root mounts in the same flush (TP790-792). Back, forward and current pages are re-created (TP914).
- §4.2 has no step that settles the new tree before it is shown.

**Code and maps.** Everything below happens on a remount and is unaddressed:
- **`Responsive.Of` fallback widths.** First composed frame at a fallback width, then a relayout:
  - `Artist.UI.cs:228-230` `PreMeasureWidth = 900`;
  - `Concert.Page.cs:866/914/1474/1507`;
  - `Search.UI.cs:533/622/714`;
  - `Browse.UI.cs:335-337`;
  - `Home\Zones.UI.cs:446`;
  - `Daylist.UI.cs:201/580`;
  - `Track.Table.Chrome.cs:140`.
- **`UseMeasuredWidth` / `UseMeasuredBounds` tiers.** These decide on the frame after the first layout (ME §4.4 #9, `Component.cs:116-121`):
  - `Show.Page.cs:956`;
  - `User.Facts.UI.cs:829`;
  - `Recents.Page.cs:1060`;
  - `Controls.TrimTip.cs:108`.
- **Reveal latches held in component fields.** These re-run their hold on remount:
  - `Artist.Page.cs:148` (`_bodyReady/_heroGateOpened/_measured`);
  - `Artist.Rules.cs:207-210`;
  - `Profile.Rules.cs:21-22`.
- **`UseResource` results held in hooks.** These refetch and shimmer:
  - `Episode.Discussion.cs` (×4);
  - `Episode.Reader.cs` (×2);
  - `Show.Page.cs`;
  - `User.SavedEpisodes.cs`.
- **Images.** A new template's `DecodePx`, corners or blur re-request textures (ME §4.4 #7). Prewarm pins URLs only (TP830, TP912).
- **Entrance choreography.** Production page entrances run on mount, e.g. the artist hero copy first and the photo afterwards (memory: production motion is deliberate). Inside a theme fade they either play (the frame is not "complete") or must be suppressed. The plan says neither.

**Fix.** Add an engine item "settled off-screen mount" to E2/E5.
- **Settle loop.** Mount the new root hidden. Run layout and measurement passes until the tree is quiescent:
  - every `Responsive.Of` / `UseMeasured*` decision is stable;
  - no scheduled effects remain;
  - the pinned images are ready or have timed out to their final placeholder size.
- **Seeding.** Seed `Responsive` / measured widths from per-slot memory (the same mechanism as scroll memory) so the first pass already uses the real width.
- **Entrances.** Mount with an `EntranceSuppressed` scope so the page appears in its settled pose. This is not a change to navigation motion; it applies only to the theme-switch mount, which needs an owner confirmation line.
- **Fade clock.** Start the fade clock at the first present of the settled tree, not at the click.
- **Acceptance.** A `switch-settle` suite for Artist, Search, Concert, Browse, Home and Show:
  - the first published frame of the new group equals the frame captured 500 ms later (minus running image cross-fades);
  - there is no geometry change of any node inside the new group during the fade.

### B3. The same-structure switch and style-option "case 4" are the live restyle the blueprint rejected
**Plan.**
- Zune ↔ Zune Fluent uses `NextRoot = null`: no remount, a `Tok.Use` cross-fade, and "list extents animate" (TP115, TP918-920, TP1416).
- Style options that feed tokens (accent, backdrop, casing, panorama size, numerals) use `SwitchTheme(def', …, NextRoot: null, CrossFade)`, and "type-size changes animate as geometry" (TP1603-1606).

**Blueprint and maps.**
- BP §1.2 row "Live-tree restyle" (BP39): "FluentGpu's `RethemeAll` cross-fades colours while geometry snaps… a tree-changing theme would show new geometry under mid-fade colours. The switch must be a new root built off-screen".
- ME §4.4 #4 (ME405) says the same.
- Zune ↔ ZF changes the following, all of which snap mid-fade:
  - radii (0 → 4/8);
  - control tables (flat rectangles → ToggleButton r4, round discs → AccentButton);
  - the type ramp (120/200 → 68/600);
  - the band (84 → 88) and the bar (72 → 84);
  - the material (opaque → Mica).
- Combined with B1, bound brushes snap as well.
- No engine item makes "type-size changes animate as geometry" or "list extents animate" true:
  - there is no animated virtual-list extent mechanism;
  - bound rows do not re-template on `Tok.Use` (M2).

**Fix.**
- Route every switch whose `ThemeDefinition` differs in any non-colour field (type, spacing, radii, controls, materials, fonts, roles that set geometry) through the snapshot path of B1. With an in-place registry, B1's snapshot design makes this cheap: snapshot, apply, settle, fade.
- Keep the colour-only cross-fade for accent and colour options, and only once B1's bound-channel snap is fixed.
- Light/dark flips have the same hybrid property today, because bound channels snap. State that explicitly and either accept it as production behaviour (owner line, Q10) or move flips onto the snapshot path too.

### B4. Switch migrations cannot be sequenced as written, and features become unreachable on releasable main
**Plan.**
- "Each switch is migrated atomically in the PR that registers its surface (old reads deleted)" (TP143-145).
- WVTH005: core never reads or writes a theme option (TP314). WVTH007: `theme.*` writes only in `Theming\Options` (TP316). WVTH002: no `Prefs` or `Platform.Settings` under `Themes\` (TP311).
- The generated Settings options section, the palette option commands and `Setup.Theme` arrive only in T12 (TP274, TP1609-1611).

**Code (readers on main and the PR that moves each file).**

| Switch | Migrating PR | Readers in other PRs' files |
|---|---|---|
| `detail.rowstyle` | T8 | `Queue.UI.cs:468,665,708` (T7, which needs `structure.rowStyle` per TP1148 while T7 ∥ T8); `Artist.Page.cs:296`, `Artist.UI.Chart.cs:176`, `Artist.UI.cs:171-191` (T10b); `Detail.cs:569` (T9); `Settings.UI.Appearance.cs:364-374` (T11/T12) |
| `appearance.trackArtwork.hidden` | T8 | `Rail.UI.cs:732` (T6); `Queue.UI.cs:469,985`, `Stage.Artist.UI.cs:561` (T7); `Search.Page.cs:136`, `Search.UI.cs:64,386,407` (T10a); `Artist.Page.cs:297`, `Artist.UI.Chart.cs:177` (T10b); `Recents.UI.cs:296,316`, `User.Page.Library.cs:1397` (T10c); `Settings.UI.Appearance.cs:334-337` |
| `appearance.marquee.enabled` | T6 | `Track.Table.cs:562,612,2185-2222`, `Track.UI.Bound.cs:414-416`, `Track.UI.cs:339` (T8) |
| washes | T9 | `Artist.Page.cs:295,356,364,517` (T10b); `Profile.Page.cs:242,257,349`, `Recents.Page.cs:1162-1205` (T10c) |
| `sidebar.layout.id` | T5 | deletes `SidebarLayoutCard` and the Setup Layout-page cards (TP1507) before T12 provides theme cards; palette sidebar rows go "→ option commands" (TP1507), which exist only from T12 (TP1608) |
| lyrics blur, stage layout, density | T7 / T8 | their Settings rows are deleted or re-pointed (TP1506, TP1519, TP1523) long before T11 moves the Settings page and T12 generates the option rows |

**Consequence.** Between the migrating PR and the later PRs, each remaining reader or writer has three options, all disallowed:
- read the theme option from core (WVTH005 error);
- keep reading the legacy key (the switch "lives in two places", TP143, and WVTH010);
- lose the feature: row style, density, sidebar layout, lyrics blur and stage layout become unchangeable in Settings. Main ships patches every 1-4 days, so users would get this. It violates "never disable an owner-requested feature" and "no half-migrated main".

**Fix.** Reorder.
1. Land the T12 *machinery* early, right after T1/T3: generated "‹Theme› options" section, palette option commands, `Setup.Theme`. Its first rebaseline goes in that PR.
2. Move surfaces into `Themes\Classic` **without** migrating switches. Allow `Prefs.*` reads in `Themes\Classic` under a shrink-only WVTH002 Classic baseline that is explicitly listed.
3. Run a late "M-series", one PR per switch, after the last reader surface of that switch is a template. Each PR adds the option to the schema (it then appears in the generated section in the same PR), the bootstrap step and the WVTH010 entry, and deletes all old reads at once.
4. Add a planning table "switch → every reader file → owning PR" (the table above, completed) as the gate for each M-PR.

### B5. The settings bootstrap loses user prefs when lanes merge out of order, and the store cannot do what §7.1 needs
**Plan.**
- Steps are numbered, and "a store already at version N skips steps ≤ N" (TP1489-1496).
- Steps 3, 4 and 5 belong to T6, T7 and T8 (TP1511-1523), which "can run in parallel after T4" (TP1690).
- §7.1: "writes the new key(s) only if the old key exists, deletes the old key".

**Failure 1: out-of-order merge.** If T8 merges and a patch release ships before T6, the latch reaches 5 and step 3 (marquee, NPV presentation, rail width, docked-video height) is skipped forever for every user. That is silent preference loss in a shipped release.

**Failure 2: no store API.** `IAppSettings` has only `Get`/`Set` (`Platform\Platform.cs:46-50`). The code itself says "There is no key-exists probe" (`Platform.cs:79-80`). The three backings (Registry, ProfileFile, Memory overlay; `Platform.Host.cs:73-76`) have no `Has` or `Remove`.

**Failure 3: deleting keys is risky.**
- It contradicts the codebase's own key rule: "Kept and unread… do not renumber, repurpose or delete one" (`Platform.cs:373-376`).
- It breaks downgrade.
- Dev builds of different branches share HKCU (`RegistryAppSettings.Open(Publisher, Product)`, `Platform.Host.cs:75`). A lane build the owner runs for review migrates and deletes keys his `main` build still reads.

**Fix.**
- Use **named steps with a per-step done marker**, e.g. `theme.bootstrap.done.<stepName> = true`, or a bitset. Never use a monotonic number while lanes run in parallel.
- Add `IAppSettings.TryGet` / `Has` / `Remove` with tests for all three backings. Make that a named PR in T1, not hidden inside it.
- Copy, do not delete: old keys stay as inert data (BP §1.6: "removed option ⇒ ignored, key left in place"). Optionally add one sweep step in F4 that deletes them.
- Test: apply steps in every merge order and assert that the end state is independent of the order.

### B6. The golden guard (P0) rests on a headless UI host that does not exist and a capture path that cannot prove its claims
**Plan.**
- Structural tier = "app in `--headless` mode on the engine headless backends" with route, panel, overlay and playback scripting (TP384, TP389-421).
- Pixel tier = real window, `--fg warp`, back-buffer capture. It "catches… material ground", is "deterministic", and is used as acceptance for E6, E7 and E10 (TP385, TP481, TP768-770, TP779-780, TP824-825).
- G1 is sized L.

**Code.**
- **App `--headless` is a windowless playback and script host.** It never mounts the shell: "a second SHELL composition root… and no `Shell.Run()`" (`Screens\Diagnostics.Probe.cs:14-18`).
  - Its parser rejects unknown flags, `--fake` included (`:139-157`).
  - By default it resumes the stored credential on the real profile.
  - No whole-shell headless UI host exists. The only precedent is a single component on an in-process headless `AppHost` (`Wavee.Tests\TrackRowRecycleAllocationTests.cs:79-81`).
- **`HeadlessGpuDevice` keeps per-kind lists, not one paint-ordered list:** `LastClear :51`, `LastRects :56`, `LastGlyphs :59`, `LastImages :65`, `LastStrokes :67`, `LastLayers :86`, plus shadows, gradients, paths, icon masks and videos. A "paint-order" dump (TP384) has to decode the draw-list bytes itself.
- **Back-buffer capture** reads only the primary swapchain (`D3D12Device.cs:4886-4901`). It cannot see:
  - the DWM backdrop (Mica, acrylic);
  - the cloak state;
  - DComp video holes (erased to zero);
  - pop-out windows.

  So the following are unmeasurable as specified:
  - E6 "first visible frame incl. opaque Zune ground";
  - E10 "no frame with the previous material";
  - E7 "no black frame";
  - G1 #3 "Mica-ground tint".
- **The window path is wall-clock paced.** `FixedFrameTimeSource` is headless-only (`AppHost.cs:4891`). Screenshot pacing is an 8 ms real wait (`FluentApp.cs:657-658`).
- **The fake data is not fully deterministic:**
  - the Home facet switch uses `System.Threading.Timer` + `Environment.TickCount64` (`Entities\Entities.Fake.Home.cs:708-717`);
  - `PlaylistCover.cs:151` uses `Task.Delay(FakeLatencyMs)`.

**Fix.** Split G1 into four PRs:
- **G1a (engine):** draw-list byte decoder → canonical paint-order dump.
- **G1b (app):** a new in-process headless **UI** shell host. This is new work, sized L, with its own plan. It mounts `RootHost` on a headless `AppHost` with `FixedFrameTimeSource`, fake data and a temp profile.
- **G1c:** remove wall-clock sources from fake data, behind an injected clock.
- **G1d (Windows-only evidence harness):** captures what DWM composes, via `Windows.Graphics.Capture` of the HWND or a DWM thumbnail. It also logs a timeline of `DwmSetWindowAttribute` / cloak calls against present indices. Use this as the acceptance tool for E6, E7 and E10, not the back buffer.

Move the determinism acceptance (TP476-478) to G1b/G1c, and add a 3-run determinism check that includes the Home facet trace.

### B7. Host state (and KeepAlive pages) are discarded on every cross-structure switch; "re-render open in the new theme" is unproven
**Plan.**
- "all async state lives in core models (P2), which survive the root swap" (TP1639).
- "KeepAlive pages of the old root dropped" (TP914).
- Focus restore (BP231) is missing from §4.2.

**Code.** There are about 250 component `readonly Signal<T>` fields of host state. All of them are lost when the root remounts. Examples:

| State | Location |
|---|---|
| table query, filters, multi-select | `Track.Table.cs:438-441` |
| open row drawer | `Track.Table.cs:473` |
| table search expanded/focused | `Track.Table.Chrome.cs:45-46` |
| sidebar search text and filter section | `Sidebar.UI.cs:305,308,311` |
| sidebar edit "show all pins" | `Sidebar.UI.Edit.cs:33` |
| playlist inline edit mode and save status | `Playlist.UI.cs:335-336` |
| queue "show more" | `Queue.UI.cs:416,964` |
| album stack expanded | `Album.UI.cs:370` |
| discography expanded album | `Artist.Discography.cs:826` |
| **comment draft text** | `Episode.Discussion.cs:169` |
| in-progress drags | `Shell.UI.cs:208-209` |
| lyrics manual-scroll follow mode | `Lyrics.UI.cs:265` |
| Home facet index | `HomeScreen.cs:79` |
| lightbox index | `Artist.UI.cs:1042` |

None of the P2 C-rows moves these. A switch from Settings is user-initiated, so losing a half-typed comment or an open drawer is an edge case the owner asked to have handled. After a switch, Back rebuilds the page with a fresh entrance and shimmer instead of today's KeepAlive return.

**Fix.**
1. Inventory every host-held state that must survive a switch (the table above, completed by grep of `Signal<` fields in hosts). For each item, either move it into a core model (P2) or make the `HostSlot` placement a persistent subtree keyed by slot (E7 generalised). With the second option, hosts placed by a frame template are re-parented, not remounted, and keep their signals.
2. Restore focus (BP231): the focused route's host re-requests focus on mount.
3. Specify Back/Forward after a switch: mount settled per B2, no shimmer when the data is in `Entities` tables, and scroll restored. Add a golden trace "switch then Back".
4. Acceptance: a `SwitchPreservesStateTests` suite per surface (query, drawer, draft, expanded flags, scroll, focus).

---

## MAJORS

### M1. E1's `ViewSlotEl` as a "MountShow-like boundary" loses hooks, park and freeze, and pairs wrongly
TP674-679 vs code:
- A Show-style effect (`MountShow`, `Reconciler.cs:1481`) has no hook scope.
- It has no park or `ExitFrozen` deferral (`RunComponent :1304` guards only `CompEntry`) and no un-park budget. Templates would re-run inside parked pages and fading roots.
- Pairing is by `ElementTypeId` alone (`ChildReconcilePlan.cs:96-122`, `ReconcileSingleChild :1170`), so `ViewSlotEl<A>` and `ViewSlotEl<B>` at one position pair.
- `DiffPropsGenerator` skips generic records (`DiffPropsGenerator.cs:53-55`).
- New boundary ids must be added to the hard-coded transparent-boundary lists (`Reconciler.cs:1742`, `Hooks\SkeletonRegion.cs:220-225`); the plan's engine-rule checklist (TP504-508) omits this.

**Fix.** Build the slot on the existing props-gated component machinery: `Embed.Comp<T,TProps>` with record `PropsSig` (`Hooks\ComponentEl.cs:53`, `Reconciler.cs:1271-1275`), i.e. a `ViewSlotHost<TProps>` component. This gets hooks, park/freeze, retheme and context for free, with no new type id. If id 20 is kept, use a non-generic abstract base (the `ForElBase`/`ForEl<T>` precedent, `Hooks\ControlFlow.cs:29-31`), a key and type check in `Update`, and the boundary-list edits.

The E1 test must also cover:
- a parked page does not run templates;
- `ExitFrozen` holds;
- two different TProps at one position remount.

### M2. E13 and the list story target the wrong recycling model; bound rows never see `Tok.Use` or option changes
**Wrong API.**
- `RowCell<T>` does not exist; `RowCell` is a non-generic paint cell (`Dsl\ListRowEl.cs:12`).
- `ItemsView.CreateBound` takes `Func<RowScope, Element>` / `Func<BoundItemScope<T>, Element>` (`Controls\ItemsView.cs:704, 757`).
- Bound slots run the template once per slot (`Reconciler.cs:3527`) and never call `IsRecyclable`. That check serves only the keyed `Virtual.List` path (`:4135`), where any ComponentEl, ShowEl, provider or **new boundary** is non-recyclable (`:4227-4228`). A nested `ctx.View(...)` in a keyed row therefore kills recycling, and FGTH003 (TP688) does not ban it.
- Probe citation: `Probes.cs:979` is a misplaced comment. The real probes are `VirtualProbe :1020`, `BoundVirtualProbe :1130` and `SlotPoolFillOnlyProbe :1174`.

**Rows go stale.** Bound rows freeze non-bound values across `Tok.Use`. No rebuild-on-template-change path exists for `RowBind` (`Reconciler.cs:3399-3527`). So E3's spacing, radii and fonts, density changes ("visible rows re-template from the recycle pool", TP1600-1602) and same-structure switches never reach existing rows. They show the old geometry until they are scrolled out.

**Fix.** Redefine E13 around `CreateBound`:
- a slot-pool invalidation keyed by `TrackListStyle.Stamp` / `Tok.Epoch` that rebuilds slot templates in one flush;
- `FGTH003` extended to `ctx.View`, `ViewSlotEl` and any boundary in keyed rows;
- the probes re-pointed;
- acceptance: density change on a 2,000-row list → every visible row has the new extent in the same publication, recycle hit rate ≥ baseline, 0 hot allocations.

The "animated extents" need an engine item (virtual-list extent transition with anchor) or the claim must go.

### M3. The analyzer rules forbid code the plan itself places in those locations
| Rule | Plan text | Violated by |
|---|---|---|
| WVTH001 | `Wavee.Themes.*` referenced only from App.cs and ThemeCatalog | `ZuneFluentTheme` → `ZuneStructure`, `WinUiStyle`, `Background`, `NowPlayingLayout` (TP1376-1391); every structure → `Themes\Common` (TP1418-1424); `Screens\Golden.Variants.cs` → `ClassicStructureOptions` (TP429) |
| WVTH004 | no `Themes.Current`, `ThemeCatalog` or id literals outside Composition | manifests in `Themes\<X>\*Theme.cs` (TP1377); the Settings ThemePicker selected state + `ThemeCatalog.Installed` (TP1567); palette "Theme:" rows (TP1609); `Setup.Theme`; `Appearance.SetMode(sameDef…)` (TP1569); feedback diagnostics (BP717) |
| WVTH005 | core never reads or writes a theme option | the generated Settings options rows and "Reset options" (TP1568), `Golden.Variants` |
| WVTH007 | `theme.*` writes only in `Theming\Options` | `ThemeBootstrap` (in `Theming\`), `Appearance.SetMode` (`theme.mode`), the `OnPresented` persist of `theme.id` |
| WVTH002 | no baseline, narrow allow-list | moved Classic builders use `Platform\Controls*` (Artwork, Chip, Vacancy, Cta), Surface hosts and Track cells (TP1138-1140) |
| WVTH008 / F5 | empty by P8 | the content host's `OfflineLane()` `BoxEl` (`Shell.UI.cs:1023`), the empty-body and NotFound arms have no ViewKey (MS21), so the baseline can never empty |
| G2 baseline | "a test fails if the file lists a site that no longer exists" (TP232) | finding stale entries means scanning production source, which is a source-text test, banned by `CLAUDE.md:70-72` and `CONTRIBUTING.md:117-118` |

**Fix.**
- Give each rule its precise allow-list:
  - composition files may reference other layers and Common;
  - `Theming\Composition\ThemePickerFacts` is the one sanctioned API for "installed + current";
  - bootstrap and Appearance writes are allowed.
- Add a `Settings.ThemeOptions` generator in `Theming\Options` (so option reads and writes stay in the allowed folder).
- Give WVTH002 an explicit, shrink-only Classic baseline.
- Add ViewKeys for the content-host states (`Shell.ContentState`: offline lane, empty body, not-found).
- Implement stale-baseline detection as an analyzer **diagnostic** (compilation-end over AdditionalFiles), not a test.

### M4. Style roles and opt-in options used as structural switches in disguise; `?? FluentDefault` fallbacks
**Roles that switch structure.**
- `R.SeekPresentation` (EdgeLine | SliderRow) changes the bar height 72 → 84 (TP1335). BP limit 4: a style changes geometry only via `StyleMetrics` (BP370).
- `R.PivotOverflow` (MoreMenu | EdgeFadeScroll) chooses whether a surface exists (TP1337, BP limit 5).
- `R.Panorama.Overflow` makes the bleed a style value (TP1340), but BP375 says "the bleed is structure".
- `R.Panorama.CompactBelow = 1000` puts a window breakpoint in the style (TP1341).
- Each of these enums takes exactly one value per shipped style. Together with the "SV" rows they are identity switches under a role name.

**Options that exist only for one composition.**
- The WinUI style declares `navigationCasing`, `panoramaSize`, `bigNumerals`, `customAccent`, `ZuneMagenta` and `Comfortable` only for Zune Fluent (TP1228-1231).
- The Zune structure declares `collapseBand` (Hidden) and `panelMode` (OptIn) only for Zune Fluent (TP1283-1284).
- `OptionOverrides` (TP1031-1035) weakens BP S5 ("a composed theme exposes the union", BP362) without flagging the deviation.

**Fallbacks.**
- `Tok.Definition.Controls ?? ControlStyleTable.FluentDefault` (TP588, TP870) is a core default look, which BP127-130 forbids.
- Release behaviour for a missing view key (TP653: "throws in DEBUG") is unspecified.
- The `IconSet.Empty` overlay fallback is likewise unspecified.

**Fix.**
- Turn seek-row height, band rows and the panorama breakpoint into `StyleMetrics` members (`SeekRow`, `PanoramaCompactBelow`). Policies then read numbers, not enums.
- Make bleed/fit and overflow-menu-vs-scroll **Zune structure options** (`structure.panoramaFit`, `structure.pivotOverflow`) that Zune Fluent sets through counted parameters.
- Either accept `OptionOverrides` with an owner line (Q2 addendum), or let compositions declare their *own* options layer instead of opt-ins hidden in WinUI.
- Make `Controls` `required` (non-nullable), so S2 completeness catches omissions.
- Release behaviour for a missing key: `RegistryCompletenessTests` makes it impossible for built-ins; at runtime, render `Common.Nothing` + log `FGTH-MISSING` once.

### M5. The Zune Fluent composition does not compile, and the single override has no pipeline
- `RoleOverrides(r => r with { Metrics = r.Metrics with { RowWithArtExtra = 12 } })` (TP1381): `WaveeStyleRoles` (TP1071-1081) has no `Metrics`.
- Metrics are computed by `t.Style.Metrics(o.Style)` (TP99), which the composition never touches.
- Row 16 uses `Metrics.RowWithArt`, but the struct declares `RowWithArtExtra` (TP1102 vs TP1348).
- `RoleOverrides` would also have to rebuild `ThemeDefinition.AppRoles`. The WinUI style's memoized definition is shared with the WinUI theme, so ZF needs its own definition instance (distinct `Id`/epoch). Not specified.
- Other undeclared members: `R.SeekPresentation`, `R.PivotOverflow`, `R.Case(...)` called as a method, `ControlRole.Follow` (TP287, TP991 vs the enum at TP856), `ShapeRole.OmniRow`, `IconRole`, `OptionValues`, `RoleSet`, `CorePref`, `ThemeFacts`.
- `NavPanePolicy(NavPaneMode ModeFor(WindowBand b), …)` is not valid record syntax (TP1096).
- `Defaults: new() {…}` targets an `IReadOnlyDictionary` (TP1388), which is a compile error.

**Fix.** `ThemeComposition` gets `MetricOverrides Func<StyleMetrics,StyleMetrics>` and wrappers `Definition(o)` / `Metrics(o)` that apply role and metric overrides and produce a composition-scoped `ThemeDefinition` (Id = manifest id). Typecheck every code block in the plan.

### M6. X44 deletion keyed on `StyleEpoch` misses light/dark flips
TP262 and TP962 delete the three `Signal<ThemeKind>` republishes "using `Tok.StyleEpoch`". But `StyleEpoch` changes only when the `Definition` reference changes (TP595-596), and `Appearance.SetMode` passes the same definition (TP921). The republishes exist precisely to re-key washes on a **kind** change (`Artist.Page.cs:144,193,238`; `Detail.UI.cs:514`; `Profile.Page.cs:102`). After the change, washes stay in the old kind after a light/dark flip.

**Fix.** Key on `Tok.Epoch` (kind + definition), or on `(StyleEpoch, Tok.Theme)`. Add a test: flip the kind on Artist/Album/Profile → the wash keys change in the same publication.

### M7. Turning `Spacing`/`Radii` consts into definition reads freezes values at type init, and IconFont is frozen elsewhere
- **App.** A1 converts `const … = Spacing.X` to `static readonly` (TP247, TP611). A `static readonly` captures the definition active when the type is **first touched**. A user who starts in WinUI and switches to Classic gets Classic statics initialised from WinUI spacing. Counts: 35 lines in 19 files (not 36/20), plus 5 defaults on 4 lines.
- **Engine.** The composites `Spacing.PagePad*/CardPad` and `Radii.*All` (`Spacing.cs:30-32`, `Radii.cs`) are `static readonly` and are used as `Corners = Radii.ControlAll` throughout Controls. `SplitButton.cs:45 public const float CornerRadius = Radii.Control` is a public const.
- **IconFont.** The sites the plan lists are mostly wrong:
  - `Factories.cs:56` and `IconRef.cs:54` read `Theme.IconFont` at render;
  - `IconRef.cs:17` is a doc comment;
  - the real freezes are `AppHost.cs:4884` (interned in the constructor) and `IconButton.cs:38` (Style default init).

  There are 81 `Theme.IconFont/BodyFont` reads, and `TextEl.FontFamily` is a plain `string?` (`Element.cs:838, 914`), so "a bound thunk" needs a new bindable channel.

**Fix.**
- All definition-derived values become properties.
- Moved Classic constants become literal Classic rule values (Classic's own metrics).
- Add analyzer FGTH004: no `static readonly` or `const` initialised from `Spacing`, `Radii`, `Tok`, `Theme` or `R`.
- E12 lists the real frozen sites and adds a bindable font-family channel or a retheme re-render.

### M8. E6 reveal gate conflicts with the engine's cloak and park logic, and covers only cold start
**Engine conflicts.**
- Cloaked or hidden HWNDs are "covered" and their presents are stood down (`D3D12Device.cs:3530, 3747-3752`), except the first `revealFrame`.
- A cloak parks the host after a debounce (`AppHost.cs:5625`).
- `DetachedRevealGate` and its helpers are `internal` to Engine, and FluentGpu.Windows is not an InternalsVisibleTo target (`Engine.csproj:12-16`).
- The swapchain is composited only when `o.Mica` (`FluentApp.cs:335`). Mica = false gives an opaque swapchain (`D3D12Device.cs:1292`, ALPHA_MODE_IGNORE), so "composited for every theme" (TP765) needs that decoupled.
- `ApplyWindowMaterial` has no `None`: mica = false selects TRANSIENTWINDOW, i.e. acrylic (`Pal\Win32Theme.cs:146`).

**Fonts at startup.** The 50 ms join with silent fallback (TP740) and a 750 ms uncloak timeout (TP763) can show a first visible frame in a fallback font.

**Paths not covered.**
- `StartHidden` / tray show "keeps today's path" (TP764).
- Restore after an OS theme change while hidden or minimised is not addressed.

**Fix.**
- Reuse the pop-out's hidden-window + `revealFrame` + `_revealPending` path (`AppHost.cs:3203-3229, 3259-3268`) instead of DWM cloak.
- Decouple `composited` from `Mica` and add `WindowMaterial.None`.
- Move the gate into AppHost or make it public.
- Gate on fonts with no fallback render (hold until ready, log on timeout).
- Apply the gate to every show-from-hidden path, including tray restore and wake.

### M9. Detached pop-out windows are separate `AppHost`s that the switch does not reach
- Each pop-out has its own reconciler, scene and root (`AppHost.cs:3138-3231`). `Ctx.Provide` does not cross into it (`Video.Host.cs:39-43`), so `SwitchRoot` and provider swaps never reach the `Video.PopOut` template.
- It gets no `ApplyWindowMaterial` and no immersive dark attribute. `OnApplyThemeMaterial` is wired only for the main window (`FluentApp.cs:396`).
- "Detached windows switch in the same tick" (TP844) therefore re-themes colours only, with the old template and the old DWM attributes.

**Fix.**
- E5 takes a list of detached hosts. Each receives its own root swap (or snapshot fade) with the new registry, options and facts providers, plus DWM dark/material.
- Acceptance: a pop-out open across a switch shows the new template in the same frame index, with a DWM attribute timeline per window (B6 G1d).
- Engine popup HWNDs set their dark flag at creation only (`Win32PopupWindow.cs:90`). They are fine because they are closed first (step 2), but state that.

### M10. E7 and E8 details
- **E7.** Context is resolved per read by walking scene parents (`Reconciler.cs:845-851`). A re-parented `Persist` subtree keeps its old provider subscriptions until it re-renders, so "effects not re-run" leaves the video chrome on the old registry and options. **Fix:** force one render of the persisted root on adoption. The E7 acceptance must also cover "new root has no `Persist("video.presenter")`", for example when the panel is closed in the new theme. Without a defined fallback, the video presenter is unmounted and re-created; define it (keep it parked off-tree for N seconds, or move playback to the docked default).
- **E8.** `ScrollEl.ScrollKey` / `VirtualListEl.ScrollKey` plus `ScrollPositionMemory` (an LRU of 256) already exist (`Scroll\Runtime\ScrollPositionMemory.cs`, `AppHost.Scroll.cs:198-214`). The plan's new static `ScrollMemory` reuses the name of a deleted reconciler type. **Fix:** extend the existing memory with item anchors and `CaptureAll`. Keys must be semantic (route instance + scope role such as "tracks" or "page"), not slot paths, because Classic and Zune pages nest scroll scopes differently.

### M11. Golden and harness runs share the owner's live state
`--golden` "implies `--fake`, skips the gate" (TP389-391). A `--fake --screenshot` run today shares a lot with the owner's instance:

| What | Where |
|---|---|
| reads and writes `session.json`, `history.json`, `play-log.json` (restore, then flush on exit) | `Shell.Host.cs:231-234, 300-303` |
| reads `home-layout.json` | `Home\LayoutFile.cs:282` |
| writes the real image cache | `Shell.Host.cs:278` |
| takes the shared `logs\crash\pending` marker and rewrites `bootfailures`, which can push the owner's next launch into Recovery | `Crash.Host.cs:624-643` |
| owns the jump list and toast activator whenever `ProfileRoot` is "" | `InstanceIdRules.cs:26-28`, `Notify.Host.cs:58` |

Golden output is also planned under `%LOCALAPPDATA%\Wavee\golden` (TP454). CLAUDE.md:60-63 warns about stray `%LOCALAPPDATA%\Wavee` folders.

**Fix.**
- `--golden` (and G1b's headless shell) **requires** `--profile <fresh temp dir>` and refuses otherwise.
- Output goes to the scratch or worktree `out\` folder.
- `Invoke-Golden.ps1` checks the baseline out with `git worktree` into a dedicated golden worktree, never an "idle" lane worktree (TP455-456).

### M12. PR sizes are unreviewable, and "moved, not rewritten" is false under the plan's own rules
- **"Moved, not rewritten" (TP1139) cannot hold.** WVTH002 (no state reads), FGTH001 (static templates) and FGTH002 (hooks only via `ctx.Hooks`) force every moved builder to be rewritten. Every state read becomes a props member, every positional hook becomes `ctx.Hooks`, and every component with fields is split into a host plus a template.
- **Size.**
  - Sidebar is about 18,000 lines (T5).
  - Stage, lyrics, video and queue are about 17,400+ lines (T7).
  - Track is about 10,900 (T8).
  - Artist is about 10,000 (T10b).

  Each is a single "L" PR (> 2,000 lines, unbounded).
- **The WVTH003 Classic baseline has no PR that empties it** (TP299, TP312). F2 (sized M) must convert thousands of literals in moved Classic views to roles and rules with zero golden diffs. That is hidden L-sized work.

**Fix.**
- Split per sub-surface (target ≤ 3k changed lines). The sidebar alone becomes six PRs: rows, heads, flyouts, edit, drop, library.
- Use a two-commit pattern per PR: a pure `git mv` move commit, then a rewrite commit, so review sees the real diff.
- Add a P3c "Classic S1 conversion" track, one PR per family after T13 with goldens identical. Otherwise get an owner decision that the Classic structure keeps an S1 baseline permanently (but that conflicts with "no legacy").

### M13. The props model defeats its own equality gate and adds per-render allocation
- WVTH002 forbids `Wavee.Entities` in `Themes\`, so every template sees contract copies of entity data. Hosts build nested props records plus `ImmutableArray`s on each render.
- Record `Equals` compares collections and delegates **by reference**. Fresh arrays, verb lambdas or lists on each host render make "equal props skip the template" (TP123-124, TP690-692) never fire.
- In bound slots, props are frozen at slot creation unless they are signal-bound.

**Fix.**
- Rules: verbs are stable objects (the host itself, or a cached instance); nested arrays are memoized per data version (entity stamps, the existing `RowStamp` pattern); lists cross as `ItemSource<T>`.
- Acceptance per surface: a census counter of "template runs per playback tick / per scroll frame / per unrelated signal" = 0, as a Wavee.Tests fact plus `RenderCensus` budgets.

### M14. Coverage gaps: surfaces with no key or no PR
**Missing from the key list (TP1121-1135):**
- the Local files route `RouteKind.Local` (`Shell.cs:54`; also missing from the BP);
- the track drawer (`Track.Drawer.cs`: `DrawerHost`, `FormatSplitHost`, `CreditsHost`);
- the Detail Insights sheet and fact cards (MP53, MP71);
- Episode discussion (`Discussion`, `CommentCard`, `ReactionFlyout`, MP69);
- content-host offline lane, empty body and page transition (MS21).

**Assigned to no theme PR:** the whole **Components** family (18 keys: `Page.Head`, `Control.Choice`, `State.Vacancy`, PlaySplit…, `Equalizer`) appears in no T, W or Z row. Also:
- Zune omits `Shell.FileDrop`, `DragChip`, `CoverScrim`, `Fps`, `Player.ChapterTip`, `Overlay.RuntimeBanner/TeachingTip/ProfileChip`, `Artist.Lightbox`, the detail sub-contracts and the Setup/Settings sub-keys;
- `Shell.HistoryMenu` should be marked n/a under Q5;
- Classic never registers `Home.Quickplay`.

**OS surfaces** are never declared out of scope (owner listed tray, taskbar and jump list):
- tray icon and native menu follow the **taskbar** theme by design (`Tray.cs:32-34`, `Tray.Host.cs:442-452`);
- thumbnail toolbar, overlay and jump list use single-variant `.ico` files (`Playback.Os.cs:456-492`, `JumpListWorker`);
- toasts and SMTC are OS-drawn.

  None can show a wrong theme frame, but the plan should say so and test the one theme-coupled piece: the tray icon after a taskbar theme change.

**Canvas states with no data.** Z-State-Error and ZF-Error draw downloads / "downloaded only" / "can't play offline". Wavee has no download state (no `IsDownloaded`/`DownloadState`). Z9 and ZF2 review against those boards.

**Fix.**
- Add the missing keys (Minor contract bump before T-PRs).
- Add a column "registered in PR" to §6.0 for every key, filled for all four themes.
- Add a §4.3 "OS surfaces" table.
- Get an owner decision to drop the download states from the Zune boards.

### M15. Golden matrix state coverage is too thin for "every edge case"
The `States` set is 14 scenarios (TP443). Missing:
- player bar NoTrack, Loading, Reconnecting, Error, podcast, video, live/DVR, remote (MS22);
- nav pane skeleton, empty, error, offline, signed-out, edit mode, drag cues, compact rail (MS20);
- auth chip forms;
- unavailable, explicit, local, video rows and multi-select;
- notice ladder, partial, deleted, create-failed (MP60, MP63);
- Profile 404 and hidden lists;
- Home refreshing, facet failed, empty facet;
- Queue empty;
- lyrics shimmer, failed, AI;
- video Poster, Working, Failed;
- release-notes loading, empty, offline, after-update plate;
- Logs reading, none, failed;
- Settings census loading, disabled-with-reason;
- Setup FirstRun, Reauth, TermsRearm;
- Library ShowPane, ArtistReader, SavedEpisodes, playlists view;
- Diagnostics Connect and Capture (only one of three pages);
- motion traces for **option changes** (density, rowStyle, sidebar layout: the no-jump rule of §8.2 has no trace);
- "switch then Back";
- "option changed while page parked".

**Fix.** Derive the `States` set mechanically from the published state enums (one scenario per enum value per surface, generated from `PropsFixtures`). Add the traces listed.

### M16. The PR 205 split is incomplete and has cherry-pick hazards
**Unassigned commits.** 23 commits are in no group, because "the Z list" (TP184) is never enumerated and zune-seed is the whole head:
- nav-style UI `68d566f4 feee05ff 8305c851`;
- Zune title bar, row 2 and panel `bb1f1cf8 21edc8dd 7fa6c56e`;
- bleed and ink `0983fe46 9d3942f1 0fc90409 f3cdec15`;
- gate fixes `2a9b73cd e7653367 f6813b15 44457e76 da3a0402 dfc67573`;
- round 4b `f5172154 57627d99 a6aa8d70 b66e7e26 82d1d1d2`;
- merges.

Several are not Zune-only:
- `f5172154` "one production veil" touches `Controls.cs`, `Design.cs`, `Palette.Host.cs` (verified);
- `da3a0402` is the card pose tracker;
- `48dcd230` and `eb837017` say "every layout / all nav styles".

**Dependencies ignored.**
- `8539d825` (R205-5) edits `Sidebar.UI.Zune.cs`, which only zune-seed commits create.
- `011f7deb` and `8539d825` build on unassigned `bb1f1cf8`.
- R205-3's D-series sits on `48dcd230`'s 674-line Detail/Track.Table rewrite (zune-seed).

**Duplicates with salvage R205-1** (matched by subject; TP183 notes it, but R205-2/3/4 still list them):
- R205-3: `5a3154fe`, `7379bc1c`, `a6ba93fa`, `7db10864`;
- R205-5: `b1828c37`, `b7076739`, `d617337d`, `b495b40c`, `37557e55`, `c9c43183`;
- R205-2: `e285b89e`;
- R205-4: `d5fbf9ca`.

**Owner items.**
- **`d1af8eb2`.** Memory says P01 is "NOT reverted until the Fable report", and the revert is still an open owner item. The plan reverts it by default with no Q.
- **Liked "Insights closed by default" (`48dcd230`).** It is in zune-seed only, so Classic loses an approved design.
- **Classic breadcrumb B.** No commit implements it, yet TP194 claims it lands via R205.
- **`e3b91c9f` (O1 card bleed).** Unclassified.
- **Breadcrumb row-2 words.** Still open in memory but missing from §10.

**Fix.**
- Produce a complete commit → group table (95 rows, each with one of R205-1..5, zune-seed or dropped, plus a reason).
- Assign the non-Zune 4b commits.
- Re-derive R205-3 on top of the parts of `48dcd230` it needs.
- Drop the duplicates explicitly.
- Add Q24 (d1af8eb2) and Q25 (row-2 words).

### M17. The plan violates or omits repo rules
- **CHANGELOG.** Every user-facing PR adds a bullet under `## [x.y.z] - unreleased`, and the release script refuses a version without it (`CONTRIBUTING.md:87-92`). The plan mentions it only in F1. User-facing PRs in P0 (R205-1..5), the removed rows (page motion), T12, P3b and the theme PRs need bullets.
- **Issue references.** `Fixes #n` in the commit body plus ` (#n)` on the CHANGELOG bullet are enforced by the release gate (`CLAUDE.md:75-76`, `releasing-wavee.md:186-190`). They are absent for the P3b category A/B fixes and the R205-1 salvage fixes.
- **Plans, builds and file lanes.** `CLAUDE.md:77-79`: plans live in `docs/plans/wavee/*-implementation.md` with real code; implementation is parallel subagents **on disjoint files**; **only the orchestrator builds/tests/launches**. §9.4 has the per-PR loop run gates after the Sonnet implementer without assigning them to the orchestrator, and several lanes overlap on files (M20).
- **Issue comments.** The release `notify` phase comments on closed issues (`releasing-wavee.md:170`). That conflicts with the owner memory "never comment on GitHub issues" and is not addressed for F3.
- **Stale docs and skills.** None of these is scheduled for an update: `docs/guide/sidebar.md:12,41-46`, `.claude/skills/wavee-sidebar/SKILL.md:3,17`, `docs/guide/fullscreen-visualizers.md:27,114`, `docs/guide/scrolling.md`.
- **"No legacy paths" (`CLAUDE.md:74`).** The plan keeps several transitional mechanisms: the WVTH008 baseline until P8, the `Golden.Variants` legacy rows, and the bootstrap until R+1. Each needs an explicit expiry PR (some have one) and owner acknowledgement in §10.

**Fix.** Add a "repo gates per PR" box to §2.2: CHANGELOG, issue ref, the plan file path, orchestrator-run gates, and docs touched.

### M18. Defaults that disable owner-requested features
- Zune `FriendsPanel = false` and `HistoryMenu = false` by default (TP1257-1260): the friends feed and the history right-click become unavailable.
- Deck presets are "core in every theme" (Q7), but the deck display is Classic-only (`heroPresentation`), so the presets are unusable outside Classic.
- Marquee becomes Classic-only (TP1171, #11), although BP §1.5 rule 5 lists marquee as a core user preference. The BP contradicts itself; the plan took one side silently.
- Zune Fluent hides `nowPlaying` and `background` (TP1391).

**Fix.**
- Default to *reachable*: a Zune "friends" panel pivot and the history menu on Back (until the owner says otherwise), plus a deck face in the WinUI and Zune now-playing panes or stage.
- Raise the marquee contradiction as a Q.

### M19. A theme that throws at mount bricks the app
The theme picker lives inside the theme. A Zune or WinUI template exception at startup or at a switch crashes into Recovery, which is OS-native and engine-free (Q9). The plan gives Recovery no "reset to Wavee Classic" path, and no automatic fallback after a crash before the first frame with a non-Classic `theme.id`.

**Fix.**
- `Crash.Host.BeginGuiRun` already knows "previous run crashed before first frame" (`Crash.Host.cs:624-643`). Add a rule: when the previous unclean run had a non-Classic `theme.id`, boot Classic and toast.
- Add a Recovery TaskDialog button "Reset appearance".
- A switch that throws during the flush reverts to the previous root (the snapshot is still there, B1).

### M20. Dependency graph and lanes are inconsistent
Edges missing from the graph (TP1644-1674):
- EP13's app pair needs T1 (`Themes.Switch`) and a Classic structure (`ClassicAlt`, TP286). P4's entry says "P1".
- EP14's app pair writes `Themes\Classic\Style\ClassicControls.cs` (TP287), so it needs T1/T2.
- T4 needs C8 (FrameModel, OmnibarModel, OverlayService).
- T8 needs EP6 (E13 types and `RecycleGuardTests`).
- The critical path's "T5 → T8" (TP1676-1677) is not an edge.

File conflicts the lanes ignore (TP967, TP1685-1690):
- EP2's app pair edits `Shell.Host.cs:672-690`, as does C1.
- T6, T7 and T8 share `Track.*`, `Queue.UI` and `Settings.UI.Appearance` (B4).
- P3b and theme lanes share `Theming\Contracts` (handled by R13) and also the S1 baseline file.

**Fix.** Regenerate the graph from a per-PR "reads/writes files" table. Make the CLAUDE.md "disjoint files" rule a lane-assignment check.

### M21. Fonts on live switch can render fallback frames
- Prewarm has a 300 ms budget and then "continue after it" (TP912). E9 joins for 50 ms (TP740). A slow file font therefore renders the Segoe fallback, which is a wrong-theme frame.
- There is a **second** font-resolution path with its own cache and silent fallback: `GlyphRenderer.cs:460-539` (fallback `:487, :514`).
- The `#Family` suffix is ignored and face index 0 is always used (`TextLayoutEngine.cs:1083-1095`).

**Fix.**
- The switch flush waits for prewarm completion (fonts are hard, images are soft) and shows pressed or busy feedback on the picker after 100 ms.
- E9 covers both resolution paths and honours `#Family`.
- Acceptance: no glyph run in a family outside the definition's `FontSet` in any published frame of the switch suite.

### M22. Accent and theme facts are outside the atomic flush
- `IStyleLayer.Accent(o)` (TP1023) is not part of `ThemeDefinition`, and §4.2 step 6 never says the accent ramp is applied inside the flush.
- `ThemeFacts.Current` is a `Context` (TP1063), but core models outside the tree (FrameModel, PanelHost, NavModel services) cannot read a Context.

**Fix.**
- Put the resolved accent ramp in `ThemeDefinition` (computed by `Definition(o)`). OS accent changes rebuild the definition.
- Expose `Themes.Facts` as a core `ReadSignal<ThemeFacts>`, written in `InFlush`, with the Context mirroring it for templates.

### M23. Same-structure switch and option-change paths have no engine item for animated geometry
"list extents change … and animate through the list's layout transition" (TP919) and "type-size changes animate as geometry" (TP1605) have no engine item. Virtual lists have no extent transition with anchor preservation, and bound rows do not rebuild (M2).

**Fix.** Either add engine item E17 (virtual-list extent transition + anchor) with a `density-change` suite, or route these changes through the snapshot path (B3), which makes them atomic fades instead.

### M24. Perf and verification cadence conflicts with owner memory and adds per-PR cost
- Per-PR gates:
  - pixel goldens on every view-touching PR;
  - frame benches on a copied live profile for every P2, P3 and P4 PR;
  - a 50-switch Windows run.
- The owner memory "no unrequested verification" explicitly pushes back on routine re-measurement. The owner's stated per-PR bar is Debug + Release builds, tests, and VerticalSlice for engine PRs.
- The planned `table-scroll` bench duplicates the existing `playlist-scroll` (`Diagnostics.FrameBench.cs:42-46`).

**Fix.**
- Per PR: structural goldens inside Wavee.Tests (already a test) plus alloc facts.
- Pixel goldens and live-profile perf: phase exits plus PRs labelled `perf-sensitive` (EP3, EP6, T8, T9, Z3, W3, EP13).
- Reuse `playlist-scroll`.

---

## MINORS (fix in passing)

Factual errors:
1. `CaptureBgra` is at `D3D12Device.cs:4898`, not :4895 (TP373).
2. `Probes.cs:979` is a misplaced comment; use `VirtualProbe :1020`, `BoundVirtualProbe :1130`, `SlotPoolFillOnlyProbe :1174` (TP351, TP514, TP730).
3. No `LayerTargetPool` type exists; use `LayerTargetCensus` (TP797).
4. `Component` is not `partial` (`Hooks\Component.cs:12`), which TP671 needs.
5. `ThemeControl.Request` is `Context<Action<float>?>` (`Hooks\ThemeControl.cs:19`).
6. The E12 `TemplateParts` fix is a no-op: `TryApplyCached` has zero callers (`ScrollSuite.cs:5302-5310`). Delete it instead of re-keying it.
7. `AppHost.cs:5292-5296` baselines the epoch after the mount **on purpose** (comment :5293-5294). Rephrase E12 #3 as a DEBUG assert, not a re-order.
8. "323 `Platform.Keys.*` sites" (TP939): `Platform.Keys.*` has 523 references; `Settings.Get/Set` calls number 330.
9. "30 existing alloc tests" (TP937): 68 files and 134 `GetAllocatedBytesForCurrentThread` sites.
10. The release cadence citation is wrong (TP1557): the dates come from `CHANGELOG.md:47,163,171,196,288`, not `releasing-wavee.md:38-70`.
11. The `theme.mode` citation `Platform.Settings.cs:98-101` is the fake overlay. The key is at `Platform.cs:111`, with the OS fallback in `Shell.Host.cs:676-682`.
12. `Clock.FixedSeedEpoch` is at `Platform.cs:927-935`. `assets\covers` holds 17 files.
13. `detail.rail.*` has 11 keys (`Platform.cs:169-181`), not 8. `uniform.width` and `uniform.collapsed` are unmapped (TP1516).
14. Washes have 5 reads on main, not "18+" (TP1504); 18 is the R205-4 count.
15. `PageMotionStyle` deletion list (TP1513) is incomplete. Missing:
    - `Shell.UI.cs:920`;
    - `Shell.cs:1013,1017`;
    - `Prefs.cs:119-122`;
    - `Design.cs:1739-1786`;
    - `Settings.cs:122`;
    - tests `DesignTests.cs:842-958` and `PrefsTests.cs:114-121`.
16. `MarqueeEnabled` is used as a generic bool fixture in `PlatformTests.cs:234-241` and `PlatformWave6Tests.cs:64-72`; those tests need a substitute key when #11 is migrated.
17. The engine API names in the examples are wrong: `Spacing.Md` does not exist (the real names are `Spacing.M`, `XXS`..`XXXL`).
18. "Exactly one StyleHook": true, but the VerticalSlice gate `gate.ctl.button.stylehook` (`AnimSuite.cs:2847-2857`) must be deleted with it. "`grep StyleHook` = 0" is a source grep, not an acceptance test; use "symbol deleted, solution compiles".
19. "Segoe UI" also appears at `Theme.cs:33` and in the TextLayoutEngine self-tests (`:1117-1152`).
20. Engine PR #53's "7 commits" includes the merge `e67e93ee2`; 6 are real.
21. Board counts: the local snapshots hold WinUI 58 and Zune 56 boards (TP4 says 59/58).
22. fluent-gpu ships the public `FluentGpu` NuGet (v0.1.x). EP2 and EP14 are breaking API changes; note a version bump.

Internal inconsistencies:

23. Totals (TP1696) are wrong:
    - P0 has 8-10 PRs (G1 is a pair, plus `chore/golden-baseline-1`), not 7;
    - ZF/finalise has 5, not 4;
    - the app pairs A1 and A4.x are uncounted.
24. "Dialogs (8)" (TP273) disagrees with BP §2.3.5 (9, plus Feedback and Crash).
25. Classic row branch counts disagree: ~64/226 (TP1505) vs BP ~324 vs MS ~400. State the counting method.
26. Zune ↔ ZF rows: "52 → 40" (TP919) vs "rows stay 52" (TP1416).
27. W1 density "Default/Compact" (TP1199) vs §6.2 Compact/Standard/Comfortable* (TP1223).
28. TP730 cites a non-existent "§5.6".
29. Classic keeps its own History and NotFound (TP1141, TP1152), yet T11 also writes `CommonHistory`/`CommonNotFound` from the same code (TP1421). That is two copies of one view.
30. Q17 drops `8002d28f` "with 59fd96c1", yet zune-seed = "head minus 59fd96c1" still contains it (TP184 vs TP187).
31. The P2 exit lists X48 as moved to P3 (TP155), but it is n/a under B (TP963).
32. `Golden.Applier` uses `Themes.Switch(id)` "Snap" (TP408), but `Switch` has no transition parameter. Before EP13 it can only be a stub.

Coverage nits:

33. C9 drops `Sidebar.UI.cs:1922-2035` from BP X25 (the `DragPeek` write, section toggle, `MarkNewReleasesSeen`).
34. The "wash colour mixing → style roles" half of X17 has no PR.
35. P21 `Concert.UI DarkOr` is assigned to T9, but Concerts are moved in T10b. P24 `Artist.Bleed` is assigned to Z2, but it is the Z6 artist view.
36. The BP §4.2 items have no per-item PR: `Design.Type.PivotLabel`/`FacetTitleStyle`/`PivotTabs`, and the legacy CTA pills (`Controls.Cta.cs:196-284`). Also missing from §7.2: `podcast.views`, `detail.sort.col/desc`, `playback.autoplay`, `sidebar.library.filter`.
37. The `sidebar.layout.id = 2` step (T5) writes `theme.id = wavee.zune` before Zune exists, and Resolve's handling of Developer-availability ids is unspecified. Under B, value 2 does not exist on main (`Platform.cs:82-83`), so the step serves only the owner's PR 205 profile. Make it explicit or drop it.

Design nits:

38. `Case` lowercasing has no specified mechanism. It needs a culture-invariant engine text transform at shaping (no per-render `ToLower` allocations, no Turkish-i issue), and must not apply to content.
39. A Zune Color option drag rebuilds the definition and calls `SwitchTheme` per pointer move (TP1588, TP1604). Throttle to one apply per frame with a snap transition while dragging.
40. The E3 and E14 acceptance weakens the BP's "the gallery runs/renders all styles" (BP1131, BP1252) to FluentDefault plus a test table. Restore it once W1/Z1 exist.
41. The WinUI NavigationView expander (W2) should carry the owner's "expanders: smooth, not WinUI" memory as an acceptance note.

---

## What the plan gets right (keep)
- Analyzer fences instead of source scans (R10).
- Golden guard first, structural tier, and owner-approved rebaselines only.
- Classic pixel-identity through P3, with the tuning isolated in P3b and its rules 1-6.
- Policies and capabilities as data.
- E12 latent-bug list (with the corrections in M7 and minors 6-7).
- `OnPresented` persistence of `theme.id`.
- Startup seeding of `NavModel`, `WizardModel` and the palette claim before frame 0.
- Explicit DEFAULT/alternative for every owner question.
- PlayPlay is never touched. No `git stash`. `gh` writes are asked first.
