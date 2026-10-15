# Review of the themes technical plan, revision 4 (round 3)

Reviewer: Opus 5.5, independent. Date 2026-10-11. Inputs: `technical-plan.md` rev 4 (2,424 lines), `owner-decisions-2026-10-11.md`
(incl. "Revision 4 follow-ups"), the size-tier requirement (`canvas-v2\SIZES-BRIEF.md` + partial boards), `plan-review.md`,
`plan-review-r2.md`. Code checked on `origin/main` WaveeMusic `b665627e`, fluent-gpu `9aff04403` (read-only: `git show`/`git grep`).

Status: complete. TPn = line n of `technical-plan.md` rev 4.

Severity:
- **Blocker**: must change before owner approval.
- **Major**: must change in rev 5; the plan stays coherent without it.
- **Minor**: fix when convenient.
- **Rev 5 must change**: applying the owner's follow-up answers. These are not defects of rev 4 and are not counted
  below.

## Summary

| Severity | Count |
|---|---|
| Blocker | 2 |
| Major | 8 |
| Minor | 14 |
| Rev 5 must change (owner follow-ups) | 5 |

**Blockers.**
- **R3-B1.** The replacement tests have no host. LP1, `ViewHarness`, `TierLayoutTests`, `SwitchResetsStateTests`,
  `RootScopeTests` and S3 all need to mount the shell headlessly. Revision 4 deleted the PR that built that host (G1b)
  together with the goldens, and the app has no `InternalsVisibleTo` and a private `RootHost`.
- **R3-B2.** E23 cannot make tier crossings animate. Resized frames skip FLIP capture and cancel structural tracks
  (`AppHost.cs:6180-6197`), and composited drags paint nothing until mouse-up. Maximize and Snap snap too. Lifting the
  `WindowResize` bit changes nothing.

**Verdict:** needs another revision (rev 5). The blockers are local and each has a concrete fix. Everything else is
sound or a straightforward correction. The decisions are applied faithfully, with only small leftovers.

## Blockers

### R3-B1. The tests that replace the goldens need a headless shell host, and revision 4 deleted it (G1b) without a substitute
**Plan.** LP1 `LiveOptionBehaviourTests` is written "on `main` before any view moves" (TP202, TP215, P0). `ViewHarness`
arrives later, inside T1 (TP234, TP345-350), and is meant to mount "a view key with fixture props, a theme composition,
options and a window size", including "the frame and every page fixture" (`TierLayoutTests` TP527, `TierResizeTests`
TP532, S3 "9 pairs on the frame + 6 page fixtures" TP353, `RootScopeTests` "unmounting and remounting the root" TP1395,
`SwitchResetsStateTests` TP1397, the §5.7 census TP1406). Revision 3 had a whole L-sized PR for this, G1b
`feat/headless-shell-host`, with four named prerequisites (`BootCore` out of `App.Main`, injected argv, a frame-tick
seam for `FluentApp.FrameCompleted`, a mountable `RootHost` with its side services gated; rev 3 TP177). Revision 4
deleted G1b with the goldens (TP279) but kept every test that stood on it.

**Code (re-checked).**
- The assembly has no `InternalsVisibleTo` (stated at `Diagnostics\Capture.Recent.cs:39`, `Entities\Artist.Discography.cs:1010`;
  `Wavee.csproj` has none). The root is a private nested `sealed class RootHost : Component` (`Shell\Shell.Host.cs:1041`),
  created only by `Shell.Host.Run` (`:250`). The sidebar's `PaneHost` is `internal` (`Shell\Sidebar.UI.cs:62`) and its
  remount key is inline in render (`:95`).
- `Stage.View()` is public (`Stage.UI.cs:62`), but its surface needs shell contexts (`InputHooks.Current`, `Overlay.Service`,
  `SharedTransition.Begin`, `Viewport.Size`; `Stage.UI.cs:230-234`); the FLIP and pane spring are inline literals
  (`:625-626, 657, 747, 758`), not pure rule classes.
- The only headless mount in `Wavee.Tests` is one table (`TrackRowRecycleAllocationTests.cs:73-81`: `Entities.Boot(Fake)`,
  `SeedFake`, a serialized collection). No test mounts a page, the sidebar or the frame.
- The repo's remedy for untestable views is "extract the decision into an engine-free pure class" (`WaveeMusic\CLAUDE.md:70-72`,
  `CONTRIBUTING.md:117-118`), which is a code move, and LP1 is supposed to land before any code moves.

**Consequence.** As planned, LP1 can only assert "the rule that decides it" for the stage, sidebar and page-layout
options, and those rules do not exist as classes on main. So the R2-B4 guard (Live options keep production motion) is not
actually built in P0, and the tier and switch suites have no host to run on until something rebuilds G1b.

**Fix.**
1. Restore a P0 PR `test/headless-shell-host` (app, L, own plan): G1b's four prerequisites without anything golden
   (no decoder, no clocks rule, no baselines). Make it a dependency of LP1, T1 (`ViewHarness` becomes a thin layer on it),
   C17, A4.2 and T3b.
2. LP1 asserts mechanisms on mounted nodes through the engine's existing public inspection: `host.Animation.TryGetTransition(node)`
   and `HasTracks(node)` (`E\…\AnimScheduler.Parity.cs:241, 293`, reached via `AppHost.Animation`, `AppHost.cs:3123`), plus
   the node keys (`stage:cover`, `sidebar.classic|library`, `list:…`). Each assertion names a node, not a source line.
3. Where a mechanism stays inline (`Stage.UI.cs:625-626`), LP1 asserts it on the mounted stage. Do not extract a "rule"
   only to make it testable before the moves.
4. Fix the citations LP1 uses: FLIP `Stage.UI.cs:657` (not 658); the sidebar remount key is `Sidebar.UI.cs:95` (`Sidebar.Host.cs:157`
   is the signal); `Detail.UI.cs:703` is the Hero → vertical rule, and the re-key is the `vh:` segment of `Track.Table.cs:1065`;
   `ArtistReadiness.BodyReady` (`Artist.Rules.cs:41, 207-211`), not `Artist.Rules.BodyReady`.

### R3-B2. E23 rests on a wrong reading of the resize path: lifting the `WindowResize` bit cannot make a tier crossing animate
**Plan.** §3.1 (TP413-416): "the engine turns off all layout transitions while the OS modal size-move loop runs
(`AppHost.cs:6074`) … Crossings animate only on maximize, Snap or restore." §3.3.3 (TP497-502) and E23 (TP517, TP1042-1044):
"a commit that carries a tier change is exempt from the `WindowResize` suppression", so crossings FLIP or cross-fade "also
during a drag". R11 (TP2077) and `TierResizeTests` (TP532) are built on this.

**Code (re-checked).**
- `AppHost.cs:6074` only sets the suppression bit from `_window.InModalLoop`. The real gate comes later and is keyed on
  `resized`, not on the modal loop. FLIP capture is skipped on any frame whose window size changed (`AppHost.cs:6180`,
  `… && !resized`), and `_anim.CancelStructuralAll(_scene.BoundsAnimatedNodes)` runs on that frame (`:6184-6197`). The
  comment says the gate exists because stale pre-resize rects produce "a stale presented translation" and an FPS collapse,
  and that it deliberately covers "maximize / restore / snap / programmatic resizes". So **maximize, Snap and restore snap
  too**, and TP416 is false.
- Wavee's window is composited (Mica, `FluentApp.cs:304, 768`), and a composited edge drag paints nothing until
  mouse-up:
  - `DeferModalResize` (`AppHost.cs:8289-8290`);
  - the `EnsureSize` early-out, "pending until WM_EXITSIZEMOVE" (`:8317`);
  - `WM_TIMER`, "Composited edge-resize: full defer (zero paints)" (`Win32Platform.cs:2047-2050`; only live video is
    exempt).

  There is no relayout mid-drag, so there is nothing to animate "during a drag". The final size lands in one frame,
  and that frame is a `resized` frame, so it snaps.
- There is no "commit" object to tag. Projections are captured per frame into `_projectBefore` before the reconcile
  (`:6181-6183`).

**Consequence.** EP23 as specified changes nothing visible. The owner's binding rule for tier crossings ("changes
must never jump") stays unmet, and the plan's own gate (`TierResizeTests`) would fail on every resize, maximize and Snap.

**Fix.** Redesign E23 around the existing snap-on-resize rule instead of against it:
1. On a `resized` frame, keep today's behaviour: lay out the **old** tier's layout at the new size (continuous tracking,
   snapped). Regions the old tier cannot fit are clipped, never squeezed (§3.3.3).
2. Change the tier signal on the **next** frame (one frame of hysteresis on resize). The tier swap is then an ordinary
   state-driven reconcile on a non-resized frame: capture runs, moved nodes FLIP, and swapped subtrees cross-fade with
   today's machinery. This is the path that already animates today when `Responsive.Of` rebuilds on a later frame.
3. Keep the composited zero-paint drag (a deliberate perf choice). A drag then produces exactly one animated crossing,
   at mouse-up. Maximize, Snap and restore get the same two-frame sequence. Say this in §3.3 and correct §3.1.
4. §3.3.1 ("same-frame tier") stays true for first frames and navigation. It does not hold for live resizes; say so.
5. Write `TierResizeTests` against the headless window:
   - Script `InModalLoop`, `SizedInModalLoop`, `ClientSizePx` and `ModalLoopTick` on `HeadlessWindow`
     (`E\FluentGpu.Engine\Headless\Pal\HeadlessPlatform.cs:66-94`; precedent: `Engine.Tests\ModalTickPolicyTests.cs:114-182`),
     with `Composited = true`.
   - Assert that the resized frame snaps, and that on the following frame every node whose rect changed at the crossing
     has `HasTracks(node)`.

   The new projection-report hook is then unnecessary: `AnimEngine.HasTracks` and `TryGetTransition` already exist
   (`AnimScheduler.Parity.cs:241, 293`).
6. Lanes: EP23 now edits the `AppHost.cs` frame step, which is Engine B's file. Serialize it with EP10 and EP14
   explicitly.

## Majors

### R3-M1. M14's "a stored −1 reads as 100" is false in the code: Auto users would get blur **off**
TP313, TP1973, TP2259; the owner's follow-up made this behaviour binding.

**Code.**
- `Prefs.BlurStrength()` returns `v < 0 ? -1 : Min(v, 100)` (`Platform\Prefs.cs:217-222`).
- `BlurPolicy.Resolve` is `setting == Auto ? AutoDefault : Math.Clamp(setting, 0, 100)` (`Shell\Lyrics.cs:180-189`).
- If `Auto` and `AutoDefault` are deleted and only the key default changes, a stored −1 resolves to `Clamp(−1, 0, 100) = 0`.
  The existing test already pins that result for other negatives (`LyricsRulesTests.cs:85`, `[InlineData(-9, 0)]`).
- Every profile where the user ever pressed "Auto" holds an explicit −1 (`ResetLyricsBlur` writes it,
  `Settings.UI.Appearance.cs:739-744`).

So M14 as written silently switches off a feature the owner asked to keep. The "§7.1 value rule" does not apply here:
it covers legacy keys that the bootstrap copies, and this is a core pref with no bootstrap step. M14 also misses two
`AutoDefault` readers: `Settings.UI.Appearance.cs:47` (the slider seed) and `Lyrics.UI.cs:315`.

**Fix.** Either option works:
- M14 maps `v < 0 → 100` in `Prefs.BlurStrength`. This is one arm, kept until F4 like the bootstrap and listed in Q31.
- Or add a named step `lyricsBlurAuto` that rewrites −1 to 100. This is an in-place rewrite of the same key, so it bends
  the copy-only rule and must be justified.

Either way, change the `-9 → 0` test case and cover the two missed readers.

### R3-M2. The zoom rules (NQ1) are sequenced before the ladders they need, and they make zoom theme-dependent
TP476-480, TP2250, C1 (TP228, TP1103).

**Sequencing.**
- C1 is in P2. `TierPolicy` and every structure's ladder arrive in T3b (P3), and the tier PRs redesign them.
- So when C1 lands, "clamp Auto zoom so the zoomed viewport stays in the unzoomed tier" has nothing to clamp against.
- Once the ladders exist, the result depends on the active structure's thresholds. On the boards: WinUI 1920; Zune 1920
  and 3200; Panorama 1920 and 2880.
- A theme switch can therefore change the Auto zoom. That means a rescale inside a transaction, which §4/E5 never plans.

**Code facts** (`Platform\Platform.cs:391-460`).
- Plateaus are {0.75, 1, 1.25, 1.5, 1.75, 2}, with a ceiling of 2.
- 2560 × 1440 → 1.5 (1707 wide); 3840 × 2160 → 2.0 (1920 wide).
- Every board puts the Huge threshold at 1920. A 4K window at 2.0 is therefore already "Huge", the clamp does nothing,
  and TP477-478 ("lays out near the 2560 × 1440 Huge board") is false.
- At zoom 1.5, a 4K window lands exactly on 2560.
- The 500 ms debounce (`Shell.UI.cs:295-311`, `Shell.cs:2003`) makes a monitor change cross tiers twice: once at the old
  zoom, then again at the new one.

**Fix.**
- Move the zoom rule out of C1 into T3b, or into a C1b after T3b.
- Clamp against a **core** reference ladder (Compact < 640, Small < 1008, Standard < 1920, Huge, Huge 4K ≥ 2880). Every
  structure must align its four contract tiers with it, so zoom never depends on the theme.
- In §3.2, state the resulting zoom per reference monitor (1080p, 1440p and 4K, at 100 % and 150 %).
- Apply the new zoom and the new tier in the same debounced step.

### R3-M3. The boards define a fifth band (Huge 4K) and Panorama-specific thresholds the model cannot express
The contract has four width tiers (TP438). The boards define more:
- **Zune** (`Z-Sizes-Breakpoints`): `huge 4k` from 3200, with its own rules (gutter 64, pill 400, play bar 88, pivots 40).
- **Panorama** (`ZF-Breakpoints`): `Huge 4K` from 2880; "second pane docks" at 2400; Small/Standard at 1000, not 1008.
- **WinUI** (`W-Tiers`): 641/1008/1920, with 24 px narrowing hysteresis.

TP467-469 says Panorama's deviations are "counted composition effects". But `CompositionEffect` (TP1196-1200) can only
change roles, metrics, accent and structure parameters, not ladders. §6.4.1 lists none of these rows.

**Fix.**
- Either add `Huge4K` to `WidthTier` in contract v1.0 (cheaper now than a Minor bump later), or define it as a named
  sub-ladder that every structure declares.
- Derive Panorama's 1000 from `MinPageColumn + 340`. It is already the panel rule (§6.4.1 row 7).
- Add the 2400 and 2880 rows to §6.4.1, as metrics-driven PF rows or as counted parameters.

### R3-M4. WVTH013 is not implementable as written
TP341 bans "literal `Width`/`MaxWidth`/`MinWidth` on a page-level region". An analyzer cannot know what a "page-level
region" is, and §3.1 counts about 255 `Width = N` literals, most of them control sizes.

**Fix.** Specify the rule as syntax:
- (a) No comparison of `Viewport.Size`, `ctx.Hooks.UseMeasured*`, `Responsive.Of` arguments or `TierFacts.*Width`
  against a literal, or against a constant outside `Rules\Tiers*`.
- (b) No `Responsive.Of` or `UseMeasuredWidth` breakpoint lambdas outside `Rules\Tiers*`.
- (c) Page-level widths come from the layout record. Review enforces this, not the analyzer.

Add analyzer tests for each case.

### R3-M5. Tier PRs are under-sized, and their boards are far from complete
**Sizing.**
- TC6, WT5 and ZT5 (TP555-557) each put 12 or more surfaces × three tiers into one L PR of at most 3k lines: Settings,
  Setup, release notes, feedback, crash, dialogs, overlays, toasts, stage, lyrics, video, queue and the Common blocks.
- TC1 does the same for frame, title bar, nav, panel, player and content state.
- That does not fit in 3k lines.

**Board state today** (`canvas-v2`).
- Classic: Compact and Small for Home, Browse, BrowseCategory, Search and Album; only `C-Shell-Huge` for Huge; no
  breakpoint board.
- Zune: Huge only for Album, Home, Library-Albums, ReleaseNotes, Settings, Setup, Show and Frame.
- Panorama: no Huge board at all.
- WinUI: the most complete.

**Fix.**
- Split each theme's tier work by family, at the same grain as its page PRs: about 8-10 PRs per theme, each still at
  most 3k lines.
- List the board inventory per BD set, with what is missing.
- Make "BD-Z complete" and "BD-W complete" explicit milestones on the critical path (§9.2): ZT and W4a sit on it.

### R3-M6. The dependency graph over-constrains the critical path
- Z and W wait for **every** M-PR (`M & EP14 & EP15 & EP23 --> Z/W`, TP2130-2131). Their real needs are narrower:
  - Z1/W1 (style) need only T2 and EP15.
  - Z2/W2 need M7a (the nav-pane policy) and T4.
  - Z3/W3 need M2/M3 (row stamp, metrics).

  Gate each PR on what it needs.
- `T8 --> T9 --> T10` (TP2121) puts Home, Browse, Search, Library and Recents behind the detail frame. Only T10b (the
  artist chart) and T10c1 (the library panes) need T8/T9.
- With those edges, the critical path is about 60 serial PRs. Each one runs plan → plan check → implement →
  orchestrator gate → up to 3 review rounds, and there are two design gates on top. Without a duration estimate per
  phase, the "critical path" is not a schedule.

**Fix.** Per-PR edges in §9.2, a duration estimate per phase, and start conditions for P3t, P5 and P6 that do not wait
on unrelated M-PRs.

### R3-M7. The 340 DIP minimum height does not fit the quarter-snap it was chosen for
TP483-484, TP2251. The owner accepted the default on this premise.
- `MinHeight` is a **client** minimum. `ClientToWindowPx` adds the custom frame's bottom border
  (`Win32Platform.cs:1118-1135`, about 8 DIP at 150 %), so the window floor is about 348 DIP.
- A quarter of a 1080p work area at 150 % with a 48 px taskbar is (1080 − 48) / 2 / 1.5 ≈ 344 DIP.
- So 340 blocks the very snap it was meant to allow.

**Fix.** Rev 5 tells the owner and proposes 330, or derives the client floor from the work-area quarter minus the
frame. Title bar 48 and player bar 72 stay; the tier boards' Short-height rules must then cover about 210 DIP of
content.

### R3-M8. R2-B2's frame guarantee has no measurement method after the golden decoder went
EP14 acceptance (TP979-981) promises "every presented frame is S, N or the dissolve formula; no brush or slab fade
inside N". Revision 3 checked that with the G1a paint-ordered decoder. Revision 4 deletes the decoder without saying
how the suite now decides what counts as N.

**Fix.** State the method:
- (a) Headless: at thaw start, `AnimEngine.HasTracks(node) == false` for every node, and no brush transition is pending
  (snap scope).
- (b) Windows tests: WARP `CaptureBgra` readback of the presented frames, compared with the captured S and the settled
  N. This is an engine mechanism test against in-memory references, not a stored baseline, so it is not a
  pixel-identity harness.


## Minors

1. **TP373, TP2192.** `Shell.Host.cs:317-331` is `ParseArgs`: `--frames`, `--screenshot`, `--width`, `--height`
   (each ≥ 300; default 1180 × 760). `--fake` and `--profile` are parsed elsewhere. Cite their parser, and note the
   300 floor if Compact screenshots at 300 are wanted.
2. **TP285 and the M7a row.** §2.3 says it was regenerated at `21abd98e`, but the refs are at `b665627e`. Fix the
   header. In M7a, `Sidebar.UI.cs:323, 1645, 2109` read `Sidebar.Width`, which belongs to M7b. The only layout read in
   that file is `:860`.
3. **ZC2 (TP1564).**
   - `ChooseFor` is at `Spotify\Spotify.Audio.cs:1133` (called at `:1056, 1092`), not `:596-610`.
   - "On this PC" is already the Settings › Storage header (`Settings.UI.Storage.cs:124`,
     `Strings.Settings.Storage.OnThisPc`). Use a distinct loc key.
4. **TP1514, TP2254.** "Full screen never blurs" is not literal. The active-line halo follows the user's setting on
   stage (`Lyrics.UI.cs:417`). Only the depth blur is forced off, for perf: `Lyrics.cs:191`'s comment calls it "the
   stage's largest per-frame cost".
5. **TP351, TP2074 vs TP199, TP347.** "Contract-surface approval" implies a checked-in approved file, but the plan
   also says "no baseline". Call it a reflection-over-assembly approval file and say the repo rules allow it. Nothing
   in the rules bans it; only source-text tests are banned (`WaveeMusic\CLAUDE.md:70-72`; engine `CLAUDE.md:83-88`).
6. **TP347.** `WindowDesc`'s field is `Size2 SizePx`, in pixels (`Pal.cs:474-476`). It equals DIP only at scale 1;
   `ViewHarness` must multiply.
7. **Leftovers from removed work.**
   - TP369 cites `docs/guide/fullscreen-visualizers.md` "(T7, M9)", but M9 is deleted.
   - TP2232 maps Q29 to "C16 (core `Cached` row fact)", but it is ZC2 `OnThisPc`.
   - TP2223 keeps Q20's "Spotlight" default, which NQ3 supersedes.
8. **TP1790, D7.** #207 was not only "salvage fixes": it shipped two approved #205 features, the Filter flyout and
   the stock chips (`89a5fd4b`, `ee68bbd5`, `c8dbabf4`, `2484d27f`; `7a869109`, `de693982`). D7 "one chip style
   everywhere" is true only for filter chips. Browse, podcast, search and playlist chip-likes keep their own boxes
   (`Browse.UI.cs:66`, `Controls.Podcast.cs:67`, `Search.UI.cs:518`, `Playlist.UI.cs:278`). Move D7 to "partly" and
   assign the rest to a CT PR.
9. **TP238 vs TP554.** Who moves Classic's ladders into `Rules\Tiers*.cs`? T3b says T3b, §3.6 says "the T4-T11
   moves". Pick one: T3b can only move the ladders that live in core files.
10. **CT1a F3 (TP1829, TP1904) vs TC1.** CT1a builds a gutter ladder (36/32/16, 24 hysteresis), then TC1 redesigns
    the Classic tier layout from BD-C. Make F3 part of TC1, or make TC1 depend on CT1a and reuse its ladder.
11. **ZC1 (TP1563).** Main's Browse band order is Top, Charts, For you, Genres, Mood, More (`Browse.Page.cs:86, 211`).
    Q30's row 2 is a Zune/Panorama `PageChrome` order. ZC1 must not reorder Classic's page. Say so.
12. **PR count (TP276-281).** 170 is correctly counted. About 8-10 S PRs could merge without breaking the cap:
    - EP4 into EP3;
    - EP7b into EP7;
    - EP21 with EP20;
    - M12 with M13;
    - M2/M3/M4 as one PR (they ship as a batch anyway, if ≤ 3k);
    - M7a/M7b likewise;
    - C0 with LP1's harness PR.

    Each PR carries a fixed cost of plan, check and up to 3 review rounds, so merging pays. The tier split in R3-M5
    adds back about 15, so the honest total is about 175-180.
13. **`TierLayoutTests` (TP527-531).**
    - Add the brief's Compact rule: hit targets ≥ 40 at Compact.
    - Use the Compact board size 400 × 860 (not 760).
    - Add threshold-edge sizes (639/640, 1007/1008, 1919/1920, ±24 hysteresis).
14. **TP1575-1583, TP1809.** The plan treats the Zune "binding owner decisions" from the #205 rounds and the
    "approved #205 Classic designs" as decided. The decisions file says the latter is "assumed … not confirmed by the
    owner". Add one §10.2 line asking for confirmation, and state that the tuned canvas wins over those decisions where
    they conflict.

## Rev 5 must change (the owner's follow-up answers; not rev 4 defects)

- **NQ3: Bloom in every theme.** Delete `StagePolicy.DefaultFace` (TP1332) and `structure.defaultFace` (TP1602,
  TP1725). Panorama's counted structure parameters go from 5 to 4 (TP1745-1746). Rewrite TP2252. Record that Q20's
  Spotlight default is superseded. Main already defaults to Bloom (`Shell\Visualizer.cs:85`; key default 9,
  `Platform.Settings.cs:46`), and Mosaic/Type/Spotlight already ship in the catalogue of every theme
  (`Visualizer.cs:47-48, 88-96`). So Q21 and NQ3 need no face code at all, and the C0 `Has` probe is not needed for
  the face.
- **NQ5: WinUI full-screen lyrics may blur.** Change the M14 row (TP313, "`ResolveFor(onStage) => 0` stays"), W4c
  (TP1514), §7.2 row 22 (TP1973) and TP2254.
  - The on-stage rule becomes a declared style/structure value, e.g. `LyricsLinesStyle.StageDepthBlur: bool`, with
    WinUI true and every other style false. Never an identity check.
  - `BlurPolicy.ResolveFor` takes that flag.
  - Add a stage-lyrics perf check on the owner's 120 Hz device to W4c: the code calls this the stage's largest
    per-frame cost (`Lyrics.cs:191`).
- **Q37 confirmed.** The plan's reading is right, but its mechanism is wrong (R3-M1). Rev 5 must implement "stored −1
  reads as 100" explicitly.
- **NQ1 accepted.** Apply it per R3-M2: a core reference ladder, sequenced after T3b. Tell the owner that a 4K window
  at 100 % then lays out at 2560 (zoom 1.5) only if the core Huge-4K threshold sits above 1920. Otherwise Auto stays
  at 2.0.
- **NQ2, NQ4, NQ6 accepted as defaults.** No plan change, except that NQ2's 340 premise is false (R3-M7). NQ4 and NQ6
  conflict with no owner rule. NQ6(c), "a second toggle reverses mid-way as production does", is consistent with
  "production motion is deliberate".

## Task 1: decisions and the size-tier requirement

- **Applied correctly:** Q1-Q38, including Q3 (no split, no `themes/zune-seed`, EP16 built from main), Q23 (no golden
  PRs, no WVTH012, no rebaseline gates), Q26 (§5.6 rewritten, C17 is only `RootScope`) and Q12 (F4 at the next
  minor).
  - Leftovers: minor 7 and minor 14.
  - Scroll after a switch: keys are tab-scoped (`Shell.cs:989-999`, `Shell.UI.cs:986`), so a switch restores exactly
    what a re-visit in the same tab restores. That matches Q26's "like a fresh navigation". No finding.
- **Size tiers:**
  - The model is right: a core fact, structure ladders, views read `ctx.Tier()`, and the boards gate the tier PRs.
  - Compact is reachable today: `MinWidth = 300`, `Shell.Host.cs:258`. The brief's "minimum must drop" case does not
    arise.
  - Gaps: the fifth band and Panorama thresholds (R3-M3), the zoom interplay (R3-M2), no-jump crossings (R3-B2), PR
    sizing and board inventory (R3-M5), and test sizes (minor 13).
  - The brief's rule "never a tiny layout floating in a huge window" holds: TP470-473 requires designed Huge records.

## Task 2: load-bearing claims against origin/main

| Claim | Verdict | Evidence |
|---|---|---|
| EP22 facts (`Responsive.Of` fallback, `UseMeasuredWidth` one frame late, `Viewport.Size` same-frame, tile caps) | holds | `Responsive.cs:28-103`; `Browse.Modules.cs:31` (1100); `RenderContext.Measure.cs:145`; `Context.cs:20-24`; `AppHost.cs:492`; `TileBudget.cs:7-24`; `SliceTable.cs:52-53, 237` |
| EP23 / `AppHost.cs:6074` "all layout transitions off in the modal loop; crossings animate on maximize/Snap" | **false** (R3-B2) | `AppHost.cs:6180-6197`, `:8289-8317`; `Win32Platform.cs:2047-2050` |
| Minimum window size | holds | `Shell.Host.cs:258` (rationale `:252-256`); no window MinHeight; `FluentApp.cs:764, 766, 307`; `Pal.cs:474-482`; `Win32Platform.cs:1901-1924` honours height; DPI-scaled only, no zoom (`:610-621`) |
| 340 fits a 1080p/150 % quarter-snap | **false** (R3-M7) | `Win32Platform.cs:1118-1135` |
| Auto-zoom numbers (1.5 → 1707, 2.0 → 1920) | holds; NQ1 consequence wrong (R3-M2) | `Platform.cs:391-460` |
| `ViewHarness` "as `TrackRowRecycleAllocationTests.cs:76-81` does" | the pattern is real and the types are public, but it mounts one table, not views or the frame (R3-B1) | `Headless\Pal\HeadlessPlatform.cs:6, 45`; `HeadlessGpuDevice.cs:14`; `HeadlessFontSystem.cs:34`; `Shell.Host.cs:1041` private `RootHost` |
| Replacement tests vs repo rules | allowed: none reads source; analyzers are not tests | `WaveeMusic\CLAUDE.md:70-72`, `AGENTS.md:15`, `CONTRIBUTING.md:117-118`, engine `CLAUDE.md:83-88` |
| Q37: no separate on/off key; Auto = 100 today | holds | only `LyricsBlurStrength` (`Platform.cs:158`); `Lyrics.cs:180, 189` |
| Q37: "a stored −1 reads as 100" after M14 | **false** (R3-M1) | `Prefs.cs:217-222`; `LyricsRulesTests.cs:85` |
| #207 shipped the Filter flyout and chips | holds (chips: filter chips only, minor 8) | `Track.Table.Chrome.cs:1126, 1179`; `Track.Rules.cs:709, 850`; `Controls.Art.cs:568-570` |
| Other #207 rows (S1, S2, S6, S7, R3, D9-D12, D14, X3, F6 partly) | holds | commit contents match |
| §2.3 refs (Platform keys, Prefs, `Sidebar.Host.cs:78-82`, stage keys, `Detail.UI.cs:669, 703`) | holds at `b665627e` (header stale, minor 2) | |
| Mosaic/Type/Spotlight already ship (Q21) | holds | `Visualizer.cs:47-48, 88-96, 120-130` |
| Browse data for Q30 bands | holds | `Entities\Browse.cs:62`; `Browse.Rules.cs:29`; `Shell.cs:185-187` |
| No download or whole-file-cached state (Q29/NQ4) | holds | `ChunkDiskCache.cs:377-775` chunk-level only |

## Task 3: round-2 blockers after the removals

- **R2-B1:** moot under Q26. Agreed.
- **R2-B2 (settle):** the mechanism is closed (E17a/b/c unchanged and still gating EP14). The evidence for "every
  frame is S, N or the formula" lost its decoder (R3-M8).
- **R2-B3 (Mica dissolve):** closed. `DwmOrderTests` over a recording seam plus the WARP readback of the formula is an
  adequate replacement for G1d.
- **R2-B4 (options stay live):** the design is closed, but the guard is not built. LP1 cannot run on main as specified
  (R3-B1).
- **Motion the owner cares about:**
  - Covered: entrance clocks (`EntranceFirstFrameTests`), the colour fade (`LiveFadeCompletenessTests`) and the hero
    order (`ArtistReadinessTests.cs:101`).
  - Not covered: tier-crossing motion, because E23 cannot deliver it (R3-B2).
  - Unguarded until R3-B1 is fixed: stage layout, sidebar and list re-key motion.

## Task 4: plan quality

- **Sequencing.** Engine PRs merge before their app pairs (TP617, TP2180). EP22/EP23 are in P1. EP20/EP21 are engine
  PRs that land before CT2 adopts them.
- **Graph.** It is over-constrained (R3-M6). C1's zoom rule is too early (R3-M2).
- **Size cap.** It holds for the catalogue except the tier PRs (R3-M5).
- **Critical path.** Unrealistic without estimates. About 60 serial PRs, plus the BD-Z and BD-W design gates, which
  are incomplete today (R3-M5, R3-M6).
- **170 PRs.** Counted correctly (P0 3 + P1 12 + P2 17 + P3 39 + P3m 11 + P3c 5 + P3b 17 + P3t 6 + P4 13 + P5 21 +
  P6 19 + P7 3 + P8 4). It is justified in order of magnitude. Consolidation is possible (minor 12).

## Task 5: the new questions against the owner rules

- **NQ1:** no rule conflict. The risks are a wrong-theme-adjacent rescale on a switch and a double crossing (R3-M2).
  The fix is a core ladder.
- **NQ2:** no conflict; the premise is wrong (R3-M7).
- **NQ3 (owner: Bloom everywhere):** removes a structure parameter and reduces risk. No conflict.
- **NQ4:** no conflict. The wording avoids promising offline playback (R25).
- **NQ5 (owner: WinUI may blur):** conflicts with "no `if (theme == X)`" only if it is implemented as an identity
  check. It must be a declared style value (see "Rev 5 must change"). Add a perf check.
- **NQ6:** no conflict. (a) keeps today's covers, (b) does not remove Insights (it stays reachable from the list bar),
  (c) keeps production motion, (d) keeps today's behaviour.
- **Q37 follow-up:** consistent with "never switch off a requested feature", but only if R3-M1 is fixed.
