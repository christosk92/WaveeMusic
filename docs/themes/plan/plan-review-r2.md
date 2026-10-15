# Round-2 review of `technical-plan.md` (revision 2, ~2,080 lines)

Plan verifier, 2026-10-11. Read-only, against:
- fluent-gpu `main` @580b04464 and WaveeMusic `main` @21abd98e;
- the PR 205 worktree (@59fd96c1), the salvage worktree (@40bb9b8a);
- `plan-review.md` (round 1).

Three Opus helpers checked three areas: the engine mechanisms, the app-side claims, and the PR 205 table plus the round-1 minors. I re-checked every blocker in the code myself. Nothing was edited, built or run.

Notation:
- TP n = line in the revised plan.
- App paths are relative to `WaveeMusic\src\apps\Wavee\`; `E\` = `fluent-gpu\src\`.

**Totals: 4 blockers, 9 majors, 22 minors.**

**Verdict: needs another round.** The revision fixes most of round 1 and the direction is right: a snapshot plus held presents, copy-only named migration steps, M-PRs after their readers, and a headless shell host. But:
- Three of the "fixed differently" mechanisms do not work as written in this engine:
  - the settle step does not settle;
  - the Mica dissolve pops twice;
  - session-lifetime HostState changes production behaviour.
- Turning *every* option change into a transaction breaks production interactions and motion.

---

## 1. Round-1 disposition, re-verified

| Round-1 item | Status | Note |
|---|---|---|
| B1 old root repaints | **closed in principle** | A snapshot cannot repaint, and the old root is unmounted in the apply flush. The new problems are R2-B3 and R2-M6. |
| B2 new root unsettled | **reopened** | The settle step does not reach a settled frame (R2-B2). |
| B3 live restyle | **closed** for theme switches | "Every option change is a transaction" is a new blocker (R2-B4). |
| B4 switch sequencing | **mostly closed** | Bridge + M-PRs + reader table work for Classic templates. Core readers and writers of future Classic options remain (R2-M1). |
| B5 bootstrap | **closed** | C0 store API, named per-step markers, copy-only steps, disjointness + permutation tests (TP1739-1764). |
| B6 golden foundation | **mostly closed** | G1a-G1e are real and the citations check out. The G1c "full site list" and RootHost headless gating are incomplete (R2-M8). |
| B7 host state | **replaced by a new blocker** | R2-B1. |
| M1 view slot | partly | Hook-cell leak and key collisions (R2-M4). |
| M2 recycling | partly | The stamp cannot reach a propless `CreateBound` (R2-M5). |
| M3 analyzer allow-lists | closed | – |
| M4 disguised switches | closed for roles | Option hiding is not specified (R2-M3). |
| M5 composition | mostly closed | Effect order is undefined (R2-M3). The code-validity claim is false (minors). |
| M6 X44 epoch | closed at TP1258 | Still contradicted at TP1073 (minor 1). |
| M7, M10, M11, M13-M15, M18, M19, M21-M24 | closed | – |
| M8 reveal gate | partly | R2-M7. |
| M9 pop-outs | closed in plan | The same-frame thaw is best effort (R2-M6). |
| M12 PR sizes | closed | Totals are still wrong (minor 2). |
| M16 PR 205 split | partly | The table is complete and the shas are right, but rewrites, order and replay are wrong (R2-M2). |
| M17 repo rules | partly | R2-M9. |
| M20 graph/lanes | partly | Minors 3-4. |
| Minors 1-41 | 35 fixed, 4 partly (5, 11, 12, 25), 1 not fixed (23 totals) | 41 is fixed in the plan text but its line citation is wrong (see R2 minor 15). |

---

## BLOCKERS

### R2-B1. Session-lifetime `HostState` changes production behaviour, and its key is not unique
**Plan.**
- `HostStateKey(Scope = SlotKey(route) | tab{N} | global, ContractKey = ViewKey id, Member)`.
- Session lifetime, evicted only when the SlotKey leaves every back/forward stack and the KeepAlive table, with an LRU of 64.
- "latches → HostState (a latch that already opened stays open)".
- It claims to "keep today's semantics … exactly as KeepAlive and scroll do" (TP1309-1342).

**Code.**
- **Today's reuse.** A page keeps its instance state only while it is one of the **3** parked KeepAlive slots (`Shell.UI.cs:948`, `Shell.cs:986`, LRU in `E\…\Hooks\ControlFlow.cs:123-139`). A route still in the back stack but evicted from KeepAlive gets a **fresh** component; only scroll survives. The global `s_nav` keeps up to 200 entries (`Shell.cs:516`), and closed tabs' routes never leave it, so the plan's eviction rule almost never fires.
- **Reveal latches would skip the entrance.** `Artist.Rules.BodyReady` returns true as soon as `alreadyRevealed` is set (`Artist.Rules.cs:207-211`; latch `Artist.Page.cs:148`, reset :263, set :363).
  - Case: visit artist A, open three or more other pages, then click A from a card.
  - A persisted latch composes the body on the first frame. `_measured` is false and `_heroUrl` is null, so there is no copy→photo entrance (production motion), the body is laid out at the wrong tier and reflows (a layout jump), and the flat hero placeholder flashes (`:303-305`).
  - `Profile.Page.cs:107` / `Profile.Rules.cs:21-22` behave the same way.
- **Per-mount resets would be overridden.** `Track.Table.LoadView` deliberately resets query, filters, multi-select, search box and drawer on every mount and context change (`Track.Table.cs:907-923`, :437, :964). With HostState, a fresh navigation to an album restores last week's find text and open drawer. If `LoadView` is kept instead, the theme switch does not preserve state and C17 fails.
- **Protocol flags would break their protocols.**
  - `Playlist.Page.cs:819-821` (`_opened`/`_preOpenSpent`, the ListOpen handshake; see the bug class at :805-815).
  - `Album.Page.cs:635-638` (per-mount deadline).
  - `Album.UI.cs:366-372` ("a re-bound section remounts with a fresh expand state").
  - `HomeScreen.cs:80`, :358-360 (`_shown` feeds `FacetCache.Decide`, which changes refetch and shimmer decisions).
- **The key is not unique.**
  - There is no instance discriminator: several `StackHost`s per album (`Album.UI.cs:370`), the discography grid (`Artist.Discography.cs:826`), Home zones of the same kind.
  - Several subjects live under one SlotKey: the Library route re-points one embedded table at the selected album (`Album.Pane.cs:249-256`), and `Artist.Reader` advances `_visit` (:431). So `table.query` would leak across albums.
  - `ContractKey` carries `@version`, so a contract bump silently drops state.
- **Stale statics the inventory misses:**
  - `Screens\Settings.UI.cs:79-80`;
  - `Settings.UI.Playback.cs:73-74`;
  - `Playlist.Page.cs:119`;
  - `Setup.UI.cs:67, 436` (the wizard that must "re-render open");
  - `Shell.Overlays.UI.cs:792`.

**Fix.** Make host state **transaction-scoped**, not session-scoped.
- At freeze, every mounted host snapshots its declared members into a per-transaction carry-over map. The remounting host restores them once, and the map is discarded after settle.
- Navigation semantics stay exactly as today.
- Key = `(SlotKey, ContractKey without @version, host instance key, data context such as the EntityUri, Member)`.
- Never carry reveal latches or protocol flags. The settle already runs behind the frozen frame, so a latch re-runs invisibly. List them as "re-derive" in the inventory.
- `SwitchPreservesStateTests` gains the inverse: `NavigationResetsStateTests` (fresh navigation = today's state, golden trace "visit A, 3 pages, A again").

### R2-B2. The settle step does not produce a settled frame, so thawed frames still contain old-theme pixels
**Plan.**
- E17 "quiescent = no scheduled effects; measured decisions stable; images; fonts", capped at 12 passes / 300 ms.
- E5: "every presented frame is the snapshot, the settled new frame, or their lerp" (TP884-910, TP961-963).

**Code.**
- **Colour transitions keep running.** The in-place apply re-renders nodes, and each re-rendered fill or border arms its *own* `BrushTransitionMs` fade from the previous colour (`E\…\Reconciler.cs:5164-5180, 5978-5990`; 67 `BrushTransitionMs` sites in Wavee). E5 deletes only the `ThemeTransitionOr` override, so the element fades remain. Declarative `Transition`/`Enter` slab channels and the stage accent clock (`Stage.UI.cs:20-23, 312-315`) also animate. The first thawed frames therefore show old→new colour fades: old-theme pixels inside "the settled new frame".
  - `PushSuppressBoundTransitions` (`Reconciler.cs:583-593`) covers bound channels only.
- **Quiescence never holds during playback.**
  - Per-frame tickers (`FrameTicker` on `FrameClock.Tick`, `Platform\Controls.cs:749-772`) drive Deck, the Stage caption, Verse, the visualizers, the lyrics `Stepper` (`Lyrics.UI.cs:2405`), `Concert.Page.cs:185/1360` and `Track.Table.cs:2582`.
  - Intervals fire every 100 ms (`Lyrics.UI.cs:2578/2596`, `Shell.PlayerBar.UI.cs:1618`, `Shell.UI.cs:1603`, `Rail.UI.cs:840`).
  - With lyrics or a visualizer visible, every transaction hits the 300 ms cap, logs `settle-timeout` and thaws an unsettled frame.
- **Seeding has no API.**
  - No multi-pass reconcile/layout loop exists (the only loops are capped realize passes, `AppHost.cs:6377-6384, 6398-6406`).
  - `Responsive.Of` (`Controls\Responsive.cs:28-107`) and `UseMeasuredWidth/Bounds` (`Hooks\RenderContext.Measure.cs:18-158`) take no key, so `MeasureMemory` seeding needs new parameters on both, plus the app call sites.
- **Cost.** The apply re-creates the 3 KeepAlive pages and the stage pane parked (TP1024) and mounts a whole new root. Heavy pages (Home with 16 zones, Artist) make the 300 ms cap likely even without tickers.

**Fix.**
- **E17a: a transaction snap scope.** Inside apply and settle, every transition snaps: element brush transitions, slab channels, Enter/Stagger, image reveals (`ImageCache.BeginReveal`, `Scene\ImageCache.cs:895-926`, scoped per host).
- **E17b: freeze the sources.** Freeze the frame clock and the timer drain during settle. Quiescence = layout and measure stability plus no scheduled *render* effects, with ticker and interval sources excluded by construction.
- **E17c: keyed APIs.** Add keyed `Responsive.Of(key, …)` / `UseMeasured*(key)`. Make the app adoption explicit PRs.
- **Budget.** KeepAlive re-creation is deferred to after thaw (parked pages are invisible) or done lazily on Back with the settle mount. Measure transaction latency on Home/Artist in the acceptance, and add "settle-timeout = 0" over 100 transactions as a gate.

### R2-B3. The Mica dissolve pops at both ends and cannot keep its promises about video holes
**Plan.**
- E10/E2: the old frame's ground is "flattened into the snapshot at freeze".
- The new frame paints an opaque fallback ground during the dissolve, then switches to a transparent ground and the DWM material at thaw end.
- The snapshot "keeps alpha-0 holes, so live video shows through".
- Thaw = `lerp(snapshot, new)` (TP851-856, TP865, TP876-879).

**Code.**
- **The swapchain never holds Mica pixels.** DWM composites the backdrop underneath (`Pal\Win32Theme.cs:142-147`; transparent clear at `FluentApp.cs:328`).
  - "Flattening" can only use a fallback colour, so thaw start pops Mica → flat, and thaw end pops flat → Mica.
  - Classic is a Mica theme, so **every Classic light/dark flip** now runs through this path (E5 routes `Appearance.SetMode` through the transaction) and pops twice, where today it is a 250 ms fade.
  - A light/dark change also flips the DWM Mica tint through `DWMWA_USE_IMMERSIVE_DARK_MODE`, which cannot be cross-faded.
- **DWM attributes are not synchronised with presents.** `OnApplyThemeMaterial` runs on the UI thread at the top of `Paint`, before the render-thread present (`AppHost.cs:6295-6300`). There is no `DwmFlush` or render-thread hook.
- **Holes.** Flattening makes the snapshot opaque, which contradicts "the snapshot keeps alpha-0 holes".
- **Lerp math.** The composite path does src-over only. That equals `lerp(S, N)` only when S is opaque, and not with holes or a transparent ground.

**Fix.**
- Do not flatten. Keep the snapshot premultiplied with its alpha.
- Add a composite op that does a true premultiplied cross-dissolve, `out = (1−t)·S + t·N`, over the live DWM backdrop.
- Change DWM attributes at **thaw start** from the render thread, ordered right before the first dissolve present: a new render-thread hook + `DwmFlush`. When both themes use the same material (Classic light↔dark: Mica → Mica), the only residual is DWM's own tint step. Raise that as an owner question (Q31: "accept a one-step Mica tint change at the start of the dissolve, or keep today's colour fade for kind flips").
- Acceptance via G1d (DWM capture): no flat-ground frame in a Mica → Mica transaction; at most one material step, at thaw start.

### R2-B4. Turning every option change into a transaction breaks production interactions and motion
**Plan.** "Every option change is a theme transaction (E5), never a live restyle", with step 2 `CloseTransients()` and "gestures in flight cancelled before freeze", and slider/colour drags coalesced to one transaction per 100 ms (TP1020-1021, TP1341, TP1848-1860).

**Code.**
- **Popups that host the controls get closed by step 2.**
  - Table ⋯ › Row size is a slider in a `RichPopup` (`Track.Table.Chrome.cs:808, 816-843`).
  - The stage options flyout holds the layout radios, spectrum, hero toggles and hero-dim slider (`Stage.Options.UI.cs:40, 318-340`).
  - The lyrics-blur slider has a thumb tooltip (`Settings.UI.Appearance.cs:58`).
  - So the first tick closes the control the user is operating, and if gestures are cancelled, the drag itself is cancelled.
- **Drags stall.** Presents are held during every 100 ms transaction, so the thumb stalls and then jumps.
- **Production motion is replaced.**
  - Stage layout morphs today: one `stage:cover` node with a Bounds FLIP spring (`Stage.UI.cs:20, 625-626, 658`), a pane spring (:747), a scrim brush fade (:601). It also changes from `[`/`]`/arrows (:517) and G (:205-215).
  - Washes fade today (`Shell.Masthead.UI.cs:144, 159, 209-210`).
  - Both become freeze-and-dissolve, which breaks the "production motion is deliberate" rule.
- **New motion is added where production snaps.** Density and row style re-key the list (`Track.Table.cs:1065-1066`), page layout swaps the table key (`Detail.UI.cs:703, 932`), and a sidebar layout change remounts (`Sidebar.Host.cs:157`). A 250 ms full-window dissolve plus a freeze per density step is a visible change.

**Fix.** Scope the transaction to what needs it.
- **Transaction:** theme switch, and style/composition options that change non-colour `ThemeDefinition` fields (type, radii, spacing, controls, materials, fonts). Light/dark per Q10/Q31.
- **Today's in-place path:** structure options and Classic options whose production behaviour is in place (stage layout, washes, density, row style, page layout, sidebar layout, lyrics blur). These are not live restyles of the *definition*: they are view options, and the slots that read them re-render as today, with today's motion.
  - The motion traces that pin this are baselined **in P0** (option change per kind on `main`), not "from P4" (TP522).
- **Never** close the transient that hosts the originating control, and never cancel the gesture that issues the change.
- Colour drags apply in place (colour-only definition changes do not trigger B1-class hybrids once the snap scope of R2-B2 exists) or on release.

---

## MAJORS

### R2-M1. Core readers and writers of future Classic options remain (B4 residual)
The bridge covers Classic templates, but some readers and writers stay **core** after P3. WVTH005 then forbids them at the M-PR:
- **M9 stage layout.** Writers are `Stage.UI.cs:205-215` (G / ToggleGallery) and `:517` (`[`/`]` stepping), which C4 moves into core `StageModel`/`IStageVerbs`. Core `Stage.Layouts.cs` `LayoutRules` ("Role: CORE", :6) reads `VizLayout`. Readers missing from the list: `Prefs.Stage.cs:43-52`, `Settings.UI.Appearance.cs:88, 797-798`. The plan's `:777` is wrong, and `StoreShot.cs` writes only `stage.mode`.
- **M10 lyrics blur.** The visualizer face `Verse.UI.cs:240` (a core content renderer) and `Lyrics.cs:1431`.
- **M6 washes, R205-4 readers.**
  - `Queue.UI.cs:451` (T7d);
  - `Stage.UI.cs:482` (T7a1);
  - `Deck.UI.cs:603` and `Rail.UI.cs:351` (T6b);
  - `HomeScreen.cs:548` and `Daylist.UI.cs:157` (T10a1);
  - `Design.cs:682, 2524, 2771` (T2b);
  - `Artist.UI.cs:507`.

  None is in M6's prerequisites (TP393).
- **M8.** Core `Detail.cs:750-775` (`RailPolicy.KeysFor` and the clear-all gate), C16's `DetailRailPrefs`, and `Prefs.cs:290-301`.
- **M3.** The core density ladders in `Track.Rules.cs:166, 215, 382-390, 1585-1588`, and `Track.Table.Chrome.cs:357-367`.
- **M7.** About 120 `SidebarLayoutId` references, not ~70. Missing writers: `Sidebar.Host.cs:268, 287, 572-573`, `Sidebar.Migration.cs:279-282`, `Settings.UI.Appearance.cs:655, 658`. Also `sidebar.classic.covers` is not on main.
- **Wrong line numbers:** M13 reads at `User.Page.Library.cs:353-354` and writes at `:379-380`; M12 is missing `Shell.UI.cs:125-127`; M1 is missing `Settings.UI.Appearance.cs:104`.
- **Accessors and tests not listed:**
  - `Prefs.cs:98-101`, `66-69`, `82-85`;
  - `PrefsTests`, `PlatformTests`, `ShellFrameRulesTests`, `SetupTests`, and 13 `Sidebar*Tests`.

**Fix.** Each M-PR classifies every core reader and writer in one of three ways:
- (a) moves it into the Classic structure as a rule;
- (b) turns it into a structure-declared verb, e.g. `IStageLayoutVerbs.Step/Gallery` registered by the structure and called by core through capabilities;
- (c) re-classifies the switch as a core pref.

Regenerate §2.3 from a fresh grep that includes tests, and make that grep part of each M-PR's plan file.

### R2-M2. PR 205 split: rewrites that break the build, a landing order that violates its own dependencies, a zune-seed that cannot be replayed
The table is complete (95 rows, all shas and positions correct). Twelve duplicate claims hold (6 are exact patch-id matches) and d1af8eb2/Q24 is correct. But:
1. **#2's rewrite breaks the build.** Salvage `a7ffe5ed` adds `disabled: !Prefs.Appearance.ColorWashes()` and `d5fbf9ca` deletes `ColorWashes()`, which gives CS0117. Rewrite the salvaged line to `SurfaceWash() == WashLevel.Off` instead of dropping the hunk.
2. **#51's rewrite brings back a reversed design.** It re-adds `Controls.ChipTone`/capsule `ChipStyle`, which #91 and salvage `7a869109` removed. Drop them in the rewrite.
3. **The synthetic "Insights closed by default" commit is too narrow.** #49, #50 and #51 depend on #28's net in `Detail.cs`, `Detail.UI.cs`, `Detail.UI.Hero.cs`, `Track.Table.cs` and `Track.Table.Chrome.cs` (`InsightsLive`, `VerticalLayout.BandFloor`), not only `Detail.Insights.cs`.
4. **#74 has zune-seed bases.** It sits on #30 (stroke moved into the bleed `Ring`), #31, #24, #41 and #11. Re-derive it on the pre-bleed stroke.
5. **The landing order violates the dependency column.** #76 (step 2) needs #49 (step 4); #25 (step 2) needs #14/#15 (step 3), or re-derive it on main; #29 (step 3) needs #28 (step 4); #34(b) (step 1) needs #29.
6. **The zune-seed replay won't apply.** "27-28 squashed with 44" comes before #30/#35/#41, which #44 depends on. #44 is listed twice. #45 is R205-5 but sits inside "39-46". The Zune halves of #18, #45, #51, #74 and #81 are missing. These are re-derivations, not replays.
7. **The tally is wrong** (TP316): R205-1 11, R205-5 12, zune-seed 31.
8. **Hand-applies the hazards line does not name:** zune-seed bases under "clean Classic" commits (#27, #28, #29, #45, #47, #49, #50/51, #64, #81).
9. **#15's kept tests assert `Hoisted`.** `ShellHoistSettleTests` must go too.
10. **#34's leftover hunks are unassigned.**
11. **The R205-3 summary is wrong** to claim filter flyout B and stock chips (TP188); those are R205-1.

**Fix.** Regenerate the table's "builds on" column from `git blame` (helper output in `scratchpad\deps.txt`, scripts `blamedeps.sh`/`blameshow.sh`). Derive the landing order mechanically from it, and mark every zune-seed entry as a re-derivation with its base.

### R2-M3. The composition cannot express Zune Fluent's option list, and effect order is undefined
- `ExposedOptions` = "structure ∪ style ∪ composition, minus Hidden" (TP1168). Visibility is declared by the owning layer (TP1190). So Zune Fluent would show `structure.panoramaFit`, WinUI `style.density` and `style.useWindowsAccent` beside its own density and accent (TP1649-1655).
- Nothing lets a composition hide or replace an option, or change a visible option's default. Rows 32/33 and Q20 need `background = None` and `nowPlaying = Spotlight` against declared defaults ArtworkWash/Mosaic.
- **Effect order.** `RowWithArt = m.RowSingle + 12` (FixedEffect) versus `DensityOpt` setting `RowSingle`: if FixedEffect runs first, rows with art stay 52 at Compact 32 and Comfortable 48.
- **Density.** The composition density changes only `RowSingle`. WinUI's other density metrics (pane item 36→32, card gap 16→12, TP1456) do not follow.
- The "7 options, 4 parameters, 1 override" tally (TP1657) leaves out 3 hides and 2 default changes.

**Fix.**
- Add counted `HiddenOptions` and `DefaultOverrides` to `ThemeComposition`.
- Define the order: style metrics → composition option effects in declaration order → FixedEffect.
- Make the composition density set a whole metrics preset.
- Update the counts and the `RegistryCompletenessTests` listing.

### R2-M4. E1 view slots: hook cells leak across template swaps, and `Key = key.Id` collides
- **Hook cells persist** by (file, line, ordinal) and are never swept (`Hooks\RenderContext.cs:502, 519-546`). A registry-epoch re-render with a different template leaves the old template's `UseInterval` timers, effects and measure hooks alive. **Fix:** the template id or registry epoch goes into the slot Key, so a template change remounts.
- **Keys.** `with { Key = key.Id }` overwrites caller keys and collides among siblings.
  - Above 32 siblings, reconcile maps each key to its first index (`Reconciler.cs:4359-4361, 4396-4398`; `ChildReconcilePlan.cs:81, 95-96`), so later duplicates remount every reconcile.
  - Repeated `Control.More`/`Home.Zone.Shelf` slots, and keyed lists of slots, lose identity.
  - **Fix:** composite key `key.Id + "|" + callerKey`, and require a caller key for repeated slots (an analyzer).
- **The props gate is record `Equals`** through `Signal<object?>`, not DiffProps (generic records are skipped, `DiffPropsGenerator.cs:53-56`), and `ViewSlotArgs` must be a record class (`ComponentEl.cs:53-55`). This is fine, but the "0 allocs on equal props" acceptance holds only under §5.7 rules. Make the census acceptance explicit for slots.

### R2-M5. E13: the stamp cannot reach a mounted list
`CreateBound` is a propless `Embed.Comp(() => new ItemsView { RowTemplate = … })` (`Controls\ItemsView.cs:704-712, 757+`), so a changed stamp or template never reaches the mounted `ItemsView`. The reconciler uses a new `RowBind` only for newly created slots (`Reconciler.cs:3319, 3527, 3576`).

**Fix.**
- Move `CreateBound` onto the props channel.
- Add an explicit rebuild over every slot plus the Spare, ContentType and KeepAlive pools: remove and remount each with the same index signal, under the R2-B2 snap scope, and reset measured extents.
- Size EP6 as M-L, not M.

### R2-M6. E2 freeze: missing pieces and leaks
- **There is no snapshot composite kind** (`Seams\Rhi\Composite.cs:38`: Tiles, Region, EraseVideoHole, Group, Backdrop, Direct, Image). Add one. During the dissolve, force full presents (`FullPresent` knockout, `_ppHistFull`), or the other two FLIP_SEQUENTIAL buffers keep stale lerped pixels.
- **Capture.** Copying the presented front buffer is not sanctioned. Popups and detached windows are FLIP_DISCARD, so their last frame is undefined. **Fix:** on the first held turn, re-composite the pre-freeze publication whole and copy before the held present. That works for both swap effects.
- **Video releases still run while held** (`ReleasesOnly`, `AppHost.cs:2364, 2688-2689`; `VideoPlacementApplier.cs:60-62, 374`). A video unmounted in the apply flush loses its DComp visual while the glass still shows its hole. Defer releases while frozen, or make E7 a hard prerequisite of E2.
- **Popups are not held.** They present from the same publication (`RecordPopups`, `AppHost.cs:2313`), so a popup opened during the freeze shows new content.
- **Input stays live** against the invisible new tree: hit-testing, cursor, IME caret. Swallow input during the freeze.
- **Resize or DPI change during the freeze** invalidates the snapshot. Define it: cut-thaw, as the restore hold does (`AppHost.cs:1077`).
- **The DWM border and caption colour** flip at the attribute call (R2-B3).
- **Detached hosts thaw in sequence** (`FluentApp.cs:494-495`), and non-blocking presents can be refused (`AppHost.cs:2456-2465`), so "same frame index" is best effort. State it.
- **`DecideHold`** can extend the hold by up to 500 ms after release (`AppHost.cs:2428-2445`), and the owed-present retry path does not pass the hold flag (`:2124`).

### R2-M7. E6 reveal gate: the exemption is one-shot, and "composited == Mica" is assumed in many places
- The first-present reveal exemption latches once (`D3D12Device.cs:3527`, `HasPresentedContent`). After that, presents to a hidden window stand down (`:3530, 3747-3752`). So tray restore and wake, which the plan claims (TP842-843), cannot reuse it. **Fix:** a re-armable per-target exemption, or the restore hold for those paths.
- The composited ⇔ Mica assumption also appears at:
  - `FluentApp.cs:305, 328, 335`;
  - `AppHost.cs:4993` (`_micaWindow` inferred from a transparent clear);
  - `D3D12Device.cs:568, 1297` (tearing only when not composited);
  - `Pal\Win32Platform.cs:713, 1948, 2047-2084` (modal-resize paint);
  - `Win32Theme.cs:146`.
- Video requires a composited swapchain (`D3D12Device.cs:345`). A composited window with backdrop None shows the desktop through any transparent pixel, so `WindowMaterial.None` needs an opaque clear.

### R2-M8. G1 foundation: "full site list" and headless gating are incomplete
- **G1c misses wall-clock reads reachable under `--fake`:**
  - `Entities\Home.Host.cs:401, 476, 502, 519, 548` (rollover timer);
  - `Recents.Page.cs:140, 268, 416, 451`;
  - `Store.cs:576` (`Epoch` = UtcNow at type init);
  - `Shell.UI.cs:1264, 1613`;
  - `Playlist.UI.cs:981, 998`;
  - `Artist.Page.cs:1585, 1692`;
  - `Concert.UI.cs:484, 520`;
  - `Shell.History.UI.cs:64`;
  - `Sidebar.UI.Rows.cs:1049`;
  - `Sidebar.Host.cs:1051-1057, 1510`;
  - `Platform.cs:673-683`;
  - `Video.UI.cs:796, 807`.
- **G1b must also gate** `Tray.Host.Watch`, `UseInterval(TickAmbientPower)`, the AiLyrics drivers and `Palette.Filler` in `RootHost` (`Shell.Host.cs:1052-1066`), not just `Notify.HostInstall`.
- `FixedFrameTimeSource` is at `AppHost.cs:4891`, and `:4829` is the general ctor.

**Fix.**
- Replace the list with a rule: G1c routes every `DateTime*`/`Environment.TickCount*`/`Stopwatch`/`Timer`/`Task.Delay` in `Entities`, `Home`, `Shell` and `Screens` through the injected clock.
- Enforce it with an analyzer, WVTH012: no wall-clock API outside `Platform\Clock*`.

### R2-M9. Repo rules still not met
- **No legacy paths.** `CLAUDE.md:74`, `AGENTS.md:16` and `CONTRIBUTING.md:121-122` forbid keeping legacy paths alongside new ones. §2.5 never cites the rule. The LegacyPrefs bridge, the WVTH baselines and the bootstrap until R+1 need an explicit owner exception in §10 (a new Q).
- **Every fix references its issue.** This is unconditional (`CLAUDE.md:75-76`, `CONTRIBUTING.md:82, 94`). The plan says "when an issue exists". Creating issues is a `gh` write that needs owner approval, so plan it.
- **`-SkipIssueComments` does not exist** (`ops\release\wavee-release.ps1:60-95`). `Invoke-Notify` (:1431-1454) always comments, and `-Resume` retries it. The planned `ops` PR needs the `Invoke-Pester -Path ops/release/tests` gate (`CONTRIBUTING.md:107-111`), which §2.5 omits.
- **F3 (release) must be exempt** from the per-PR gates. Root `CLAUDE.md`: a release gets one Release build, nothing else.
- **Engine test fallout.** E5's deletion of the paint-time `Tok.Epoch` poll breaks callers in 9 VerticalSlice suites and 2 Engine.Tests files (`KeepAliveTransientRerunTests.cs:59`, `SliceSuite.cs:332`, and the `Tok.Use`/`Theme.Dark=` suites). EP14's scope must include migrating them.
- Keep the BrushAnim *seeding*: it is WinUI's hover/state `BrushTransition`. Only the theme override goes.

---

## MINORS

1. TP1073 still says X44 keys use `Tok.StyleEpoch`, which contradicts TP1258 (`Tok.Epoch`).
2. Totals are still wrong (round-1 minor 23):
   - P0 lists 14 PRs, not 12;
   - P3 has 38, not 31;
   - W and Z have 13 each;
   - R1's "~110 PRs" vs the plan's own 130 (139 corrected) before P3b.
3. §4.0 (TP614, TP616) chains E1→E4→E13 and E17→E8→E7, but the engine table and graph have EP6 needing only EP3 and EP13 only EP10. The graph's C17→EP14 is missing from the table (TP637).
4. The critical-path text (TP1948-1951) contradicts the graph:
   - EP3 does not need EP2, and EP1 is omitted;
   - C9 starts from BASE, not after T4;
   - C2→…→C6 is longer than C9-C12;
   - EP14 gates all of Z and W, not only Z2/W2.
5. "Every code block re-checked for C# validity" (TP1114) is false:
   - body-less class members (CS0501);
   - qualified member declarations (`AppHost.Freeze()`, `ThemeControl.Transaction`);
   - `ctx.Options.Set` with no `Set` on `IOptionReader` (TP768);
   - no mapping from `ThemeOption` records to `OptionKey<T>`;
   - `TypeRoles.WithPanorama/WithBigNumerals` undeclared.
6. Round-1 minor 11 partly fixed: add `Platform.cs:111` and `Shell.Host.cs:676-682` for `theme.mode`.
7. Round-1 minor 12 partly fixed: `FixedSeedEpoch` is at `Platform.cs:927-935`.
8. Round-1 minor 25 partly fixed: state the counting method beyond M2.
9. WVTH010 (TP1809) lists `sidebar.layout.lastPane`, `ShellNavStyle` and `PresentedNavStyle`, which never reach main under B. List them only under A.
10. §2.3 M6 cites `Settings.UI.Appearance.cs:118`; the row is at `:119`.
11. A1 (TP694-696) says "5 optional-parameter defaults on 4 lines" but lists `Controls.cs:1138` + `Controls.Picker.cs:114, 126, 178, 199`; the code has 6 defaults on 5 lines (`Picker.cs:178` holds two). Fix the count.
12. `Tok.Epoch` readers `Stage.UI.cs:314` and `TitleBar.cs:283-285` must keep working after E5 removes the poll; list them in EP14.
13. `PlaylistCover.cs:199` is a Guid, not a clock (G1c list).
14. HostState inventory labels: `s_strip` (TabStrip) and `s_active` (EditPane) are instances, not NodeHandles.
15. WVTH011 ("every structure parameter rendered with ≥ 2 values under every style") multiplies the S3 probe: 4 Zune parameters × 2 values × 3 styles × the fixture set. Budget the probe's runtime and say which fixture subset it uses.
16. M-PRs migrate Settings rows one switch at a time, so the Appearance tab ships in up to 11 transitional layouts across patch releases, each needing an owner rebaseline. Batch the M-PRs before a release, or make T1b render the legacy rows in their final grouping.
17. Each transaction during visualizer or lyrics playback freezes in-tree GPU visuals for the settle. Video (DComp) keeps playing. State it as expected, and keep settles short (R2-B2).
18. E2's video-rect move at thaw start is not atomic with the present: placements commit after `Present` returns, best effort (`D3D12Device.cs:3563-3569, 3639-3642`). One frame of mismatch is possible. Document it, or wait on the fence as video-moving frames already do.
19. `Invoke-Golden.ps1`'s dedicated worktree path is under the root agent repo (`C:\wavee\fgpu\.claude\worktrees\golden-baseline`). Baselines need **both** WaveeMusic and fluent-gpu at the tag, so specify two worktrees (one per repo) and the `EngineRoot` wiring between them.
20. Q25 (entrances suppressed during a transaction) and Q10 (dissolve replaces the light/dark fade) are production-motion changes. Make their owner approval a gate on EP14/A4.2, not just a default.
21. The `ResourceCache` (C17) for `UseResource` results changes refetch and shimmer behaviour on *navigation* too (the same issue as R2-B1). Scope it to the transaction carry-over, or prove that no navigation behaviour changes.
22. Q26 (host-state key) and its NavEntryId alternative become moot under the R2-B1 fix (transaction-scoped carry-over). Rewrite Q26.

---

## Verdict

**Needs another round.**

Must-fix before execution:
- **R2-B1:** transaction-scoped host state, with no navigation behaviour change.
- **R2-B2:** a snap scope, frozen tickers, keyed measure APIs, and a settle-timeout gate.
- **R2-B3:** no flattening, a premultiplied dissolve, DWM changes at thaw start from the render thread, and owner question Q31.
- **R2-B4:** transactions only for theme and definition changes; today's in-place path, with P0-baselined motion traces, for view options.

Also regenerate the PR 205 dependencies and the §2.3 reader table mechanically, from blame and a grep that includes tests. The remaining majors are scoped engine and composition corrections, each with a concrete fix above.
