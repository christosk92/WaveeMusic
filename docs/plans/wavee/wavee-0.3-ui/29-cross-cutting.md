# Cross-cutting (focus & keyboard, light theme, page-transition pairs, network state, toasts, the action platform, empty/skeleton/error/offline, morph, localisation, zoom, dialogs, drag, first run, `--fake`, gates, the index) — 0.3 visual fidelity contract

> **0.2.9 sources** (all paths relative to `src/apps/Wavee/` unless marked; after Wave 0 the same relative paths under
> `src/apps/_old/Wavee/`):
> **Focus & keyboard visuals** — engine `FluentGpu.Engine/Render/SceneRecorder.cs:2593-2605, :3367-3396`
> (`EmitFocusRing`) · `FluentGpu.Engine/Dsl/Tokens.cs:123-124, :456` · `FluentGpu.Engine/Dsl/PaletteBuilder.cs:254-255`
> (light) `:342-343` (dark) · `FluentGpu.Engine/Hosting/AppHost.cs:3790, :2487` ·
> `FluentGpu.Engine/Input/InputDispatcher.cs:962-971, :1576, :3677, :3763-3781` ·
> `FluentGpu.Controls/ItemsView.cs:393, :844, :1085-1094, :1574-1580, :1722` ·
> `FluentGpu.Controls/OverlayHost.cs:281, :470-490, :1009` · app: `Design/WaveePicker.cs:236-244` ·
> `Components/TrackRow.cs:547-548, :631` · `Features/Detail/DetailTracks.cs:1019, :1543, :3568-3569, :2178-2184` ·
> `Features/Player/PlayerStyleFlyout.cs:101, :182` · `Components/WaveeEqualizerCurve.cs:144` ·
> `Features/Video/VideoFullscreenSurface.cs:128-147, :170-180` · `Features/Player/ImmersiveLyricsSurface.cs:157, :204` ·
> `Features/Shell/MergedChromeRow.cs:259-263` · `Features/Sidebar/Modes/LibraryV3/LibraryV3Chips.cs:345`.
> **Light theme, the derived surfaces** — `Design/CoverPaletteLeaves.cs:111-116, :120-139, :143-144, :155, :182-193, :242-246, :259-261` ·
> `Features/Shell/ShellWashGeometry.cs:24-37` · `Features/Shell/ShellMaterialLayer.cs:41-47, :72-77, :89` ·
> `Design/StageArm.cs:26-33, :44-64` · `Design/StageInk.cs` (82) · `Features/Player/LyricsInk.cs` ·
> `Design/Surfaces.cs:58-61, :85-89, :115-154, :169-181` · `Features/Detail/LikedCoverLeaves.cs:93-110` ·
> `Features/Player/Deck/Faces/*.cs` (nine files, zero theme reads) · `Features/Video/InWindowVideoPip.cs:243, :265-272` ·
> `Features/Video/VideoFullscreenSurface.cs:297, :316` · `Design/SearchHighlight.cs:44-52` ·
> engine `FluentGpu.Engine/Dsl/Tokens.cs:376-386` (`ScrimBottom` / `ScrimTop`).
> **The page-transition pair matrix** — `Features/Shell/PageNavMotion.cs` (122) · `Features/Shell/ContentHost.cs:96-110,
> :122-155, :184-186, :188-309` · `Features/Shell/ShellMastheadBand.cs` (131) · `App/ShellMasthead.cs` (53) ·
> `App/ShellMaterial.cs` (59) · `Features/Shell/ShellMaterialLayer.cs` (133) · `SpotifyLive/ShellTintOwnership.cs`.
> **Network state as a composed visual** — `App/NetworkPolicy.cs` (189) · `App/MeteredStatusLine.cs` ·
> `App/PlaybackBridge.cs:63-75, :259-298` (`ShellAuthState` + `ProjectAuthState`) ·
> `Features/Shell/MergedChromeRow.cs:201-219` (the chip) · `Features/Shell/PlayerBar.cs:28, :167-174, :620-630, :727-740` ·
> `Components/OfflineBanner.cs` (29) · `Features/Shell/NotificationPanel.cs:599`.
> **Toasts & confirmations** — engine `FluentGpu.Controls/Toast.cs:28-47, :79-98, :153-193, :240` ·
> `FluentGpu.Controls/SeverityVisuals.cs` (27) · `FluentGpu.Controls/ToastCoalescing.cs` ·
> 107 `Toast.Show` call sites across 38 app files (§2 W7, §6.4) · `App/ToastEscalator.cs` (255) ·
> `App/AppUpdateToasts.cs` · `Features/Shell/SettingsShared.cs:29-41`.
> **The action & extension platform** — `Actions/WaveeActionDescriptor.cs` (227) ·
> `Actions/Extensibility/{BuiltInExtensionTable.cs 303, WaveeActionTargeting.cs 251, WaveeRegistryTable.cs 173,
> WaveeExtensionRegistry.cs 148, IWaveeExtension.cs 39, PinRowRule.cs 32}` = **946 lines** ·
> `Actions/Menus.cs` (1 197 lines / 81 KB) · `Actions/ActionIcons.cs` (87) · `Actions/AppAction.cs` (109) ·
> `Actions/ActionRules.cs` (79) · `Features/Sidebar/Curated/SidebarItemPickers.cs:294-348, :411-445, :525-552`.
> **Empty / skeleton / error / offline** — `Components/EmptyState.cs` (73) · `Components/ErrorState.cs` (44) ·
> `Components/OfflineBanner.cs` (29) · `Features/Detail/DetailSkeleton.cs` · `Features/Detail/DetailNoticeBar.cs` ·
> `Features/Detail/PlaylistPageNoticeRules.cs:9-26` · engine
> `FluentGpu.Engine/Foundation/Signals/Loadable.cs:5, :16-58` · `SceneRecorder.cs:3402` (`LoadingBarSuppressors`).
> **Shared-element morph** — `Components/MorphKeys.cs` (17) · `Components/MediaCard.cs:1111, :1132, :1143` ·
> `Features/Detail/DetailConfig.cs:86` · `Features/Detail/DetailRail.cs:102-104, :159, :320, :421` ·
> `Features/Detail/DetailShell.cs:235-257` · `Features/Recents/RecentsPage.cs:1698-1707` ·
> engine `FluentGpu.Engine/Hosting/AppHost.cs:1790-1792`, `Reconciler/Reconciler.cs:4147, :4594, :4635-4643` ·
> `Features/Diagnostics/WaveeNavProbe.cs:747, :1635, :2575, :2682, :2758-2783`.
> **Localisation & long strings** — `App/AppLocale.cs` (45) · `Features/Shell/SettingsPage.General.cs:29-87` ·
> `Design/WaveeType.cs:33-55` · `Features/Detail/ContextBandLayout.cs:67-84` · `Features/Detail/DetailTracks.cs:2509,
> :3743` · the four fixed picker widths (`SettingsPage.Appearance.cs:344, :402`, `SettingsPage.General.cs:85, :227`) ·
> `Features/Shell/PlayerBar.cs:92-93, :266-291` · `assets/loc/{en-US,ko-KR,nl}.json`.
> **Zoom & large-display scaling** — `App/ZoomAutoPolicy.cs` (114) · `Features/Shell/WaveeShell.cs:581-632` ·
> `Features/Shell/SettingsPage.Appearance.cs:41-49, :73-91, :187-199, :344` · `WaveeApp.cs:231-244` ·
> `docs/plans/wavee/large-display-scaling.md` §3.2.
> **Gates & instrumentation** — `Features/Diagnostics/WaveeNavProbe.cs` (2 915; the route tables at `:35-49`) ·
> `Features/Shell/ShellRoutes.cs` (79) · `Features/Concerts/ConcertRoutes.cs:9-42` · engine `ReuseGuard` ·
> `FluentGpu.Engine/Hosting/AppHost.cs:594-610` (`FG_FPS_LOG` / `FG_ALLOC_DIAG` segment counters) ·
> `ops/tools/perf-tour.ps1` · `ops/tools/nav-measure.ps1`.
> **Dialogs** — engine `FluentGpu.Controls/ContentDialog.cs:110-117, :186, :256-291, :361-369` ·
> `Features/Shell/SettingsShared.cs:29-41` · ten further call sites (§2 W18).
> **Drag language** — `Features/DragDrop/WaveeResourceDrag.cs` (596) · `WaveeDragRules.cs` (210) ·
> `WaveeDragChipModel.cs` (52) · `PlaylistInsertionPreview.cs` (80) · `Features/Shell/WaveeShell.cs:414-421,
> :1341-1342, :1351-1359, :1418-1432` · engine `FluentGpu.Controls/DragChip.cs:68-220`.
> **First run** — `WaveeApp.cs:315-418` · `App/SetupGating.cs` (201) · `Features/Shell/WaveeShell.cs:2262-2289`.
> **`--fake`** — `App/Services.cs:584-634` + the twelve `Null*Service` seats · `Wavee.Core/Fakes/FakeData.cs` (633) ·
> `Wavee.Core/Fakes/FakeSource.cs` (79) · `Wavee.Core/Sources/FakePodcastSource.cs` (21) ·
> `Wavee.Core/Spotify/SpotifyExportSource.cs` · `assets/spotify/*.json` · `assets/covers/cover00..15.jpg`.
>
> **0.3 target:** `Platform/Design.cs` (the focus insets, the light arms, the cadence block, the dialog ladder, the
> zoom ladder) · `Platform/Controls.cs` (the dialog helpers, the one vacancy grammar, the toast helper) ·
> `Platform/Platform.cs` (network policy, zoom policy, locale) · `Platform/Drag.cs` (settled, owner L) ·
> **settled (A7): `Platform/Actions.cs` (CORE: descriptor, targeting matrix, the one action table, the thirteen
> first-party descriptors) + `Platform/Actions.UI.cs` (the menu vocabulary, icons, the row, the picker, the reason
> caption) — two files, owner I, not four and not owner L** · `Shell/Shell.{cs,UI.cs,Host.cs}` (transitions,
> material, masthead, the network chrome, the file-drop cue, the first-run composite) ·
> a proposed **`Entities/Entities.Fake.cs`** (`SeedFake`).
> **Waves:** 4 owner **L** (design, dialogs, drag, locale, zoom) · 4 owner **I** (transitions, material,
> network chrome, first run, and — settled by A7 — `Platform/Actions.cs` + `Actions.UI.cs`) · 5 owner **Q** (the fake seed) · 6 (the gates).
> **OS surfaces are NOT in this chapter.** They are `14-os-surfaces.md`'s whole subject — see §9.9.1 and the
> retraction at §11 audit 24.

**Path convention.** App paths are relative to `src/apps/Wavee/`; engine paths to `..\fluent-gpu\src\`. Every number
below is `file:line`; computed values give the formula **and** a worked result. `UNVERIFIED` marks anything the code
does not settle.

**What this chapter is.** Thirty sibling chapters each own one surface. Twelve things are owned by none of them
because they cross all of them: what keyboard focus *looks like* and where it goes; what the surfaces that **compute**
their colour do in light theme; what happens on screen when one page replaces another, composing three independent
channels; what the window shows when the network is gone, metered, or coming back; what every toast says; what a
bound action looks like when it cannot run; how "loading", "empty", "failed" and "offline" stay four visibly distinct
states everywhere; the dormant connected-animation pair; what a long localised string does to a fixed-width control;
the zoom ladder; the modal vocabulary; the drag vocabulary; and the gates that make a parity claim testable rather
than felt. Where this chapter and a page chapter disagree about a **surface**, the page chapter wins. Where they
disagree about one of the twelve above, this one wins.

---

## 0. The non-negotiables

Checkable statements. Each is what a user (or a reviewer) notices first if it is lost.

1. **The focus ring is keyboard-only, and that is a flag, not a heuristic.** `SceneRecorder` emits the ring only when
   `NodeFlags.FocusVisual` is set (`SceneRecorder.cs:2596`); `SetFocus(node, visual:)` takes the boolean and the
   pointer path passes **false** (`InputDispatcher.cs:968`, `:1576`), Tab passes **true** (`:3677`), and roving
   arrow-key movement passes true through `MoveFocusVisual` (`AppHost.cs:2487`). A rebuild that draws a ring on
   click puts a white halo on every button the user touches.
2. **The ring is two strokes, 2 px + 1 px, drawn OUTSIDE the node's own clip.** Primary `Tok.FocusThickness = 2f`
   (`Tokens.cs:456`), secondary a flat 1 px immediately inside it, both centre-line SDF strokes with concentric
   corner growth (`SceneRecorder.cs:3372-3396`); it is emitted **after** the node's clip pops, because the WinUI ring
   lives outside the bounds and a `ClipToBounds` parent would otherwise eat it (`:2593-2601`).
3. **Wavee has exactly two sanctioned departures from the engine's −3 focus margin, and both are INSETS.**
   `+2` for a control that draws its own 1 px border (the ring would cross it — `WaveePicker.cs:235-237`), and `+1`
   for a row inside a virtualized list (the ring must not bleed into the neighbour's pixels — `TrackRow.cs:548`).
   A third value, `−2`, exists at two lines in `PlayerStyleFlyout.cs:101, :182` with no stated reason; 0.3 normalises
   it to `+2` (it is a bordered card) — §9.9.4.
4. **A virtualized list has ONE tab stop, and arrows move inside it.** `ItemsView` is `TabNavigation="Once"`
   (`ItemsView.cs:393`): slot roots are built `Focusable = false` and the engine moves the single roving stop to the
   keyboard-current slot **in place, without re-rendering** (`:1094`, `:1574-1580`). The app obeys it explicitly —
   `TrackRow.cs:547` and `DetailTracks.cs:3569` both set `Focusable = false` with the comment naming the roving
   effect. A rebuild that makes every row focusable turns one Tab into four hundred.
5. **An overlay restores focus to what opened it, and only if it still owns focus.** `OverlayHost` saves focus on
   open and restores at close **start** (so the restore can land outside the dying subtree), but only when focus
   still lives inside that overlay or is dead (`OverlayHost.cs:470-490`). A popup that never took focus must not
   yank it from elsewhere.
6. **Page navigation does NOT restore or move focus, and that is current behaviour, not a decision.**
   A repo-wide grep finds **fifteen** `FocusNode` / `RestoreFocus` / `PushFocusScope` / `PopFocusScope` call sites
   in the app, across seven files, and **not one is on a route change or a KeepAlive reactivation** (§6.3). Fullscreen video and the immersive lyrics surface each
   save-and-restore around their own mount (`VideoFullscreenSurface.cs:132`, `:146`); nothing else does. 0.3 must
   either adopt the rule in §6.3 or record the gap; it may not leave it undocumented.
7. **The surfaces that COMPUTE colour each carry a light arm, and every one is a different formula.**
   **Thirteen**, not the eight an earlier draft counted — a repo-wide grep for `Tok.Theme ==` / `ThemeKind.Light` /
   `ThemeKind.Dark` returns **50 branches across 15 files**, and five of those files were not in the list (§2 W5B).
   The eight this chapter always had: the cover tint published to the shell (`Lift(TextBase) @ 0.05` light vs `TintedDark(scheme) @ 0.14` dark,
   `CoverPaletteLeaves.cs:242-246`), the page tone plane's alpha (`0.30` light / `0.20` dark, `:139`), the artist
   blend wash (`Lift(Accent)` vs `BackgroundDark`, at `0.20/0.06` vs `0.30/0.08`, `:182-192`), Home's three radial
   washes (hero `0.055` / shelf `0.05` light, `0.10` / `0.085` dark, `ShellWashGeometry.cs:33-37`), the artwork
   placeholder (`#F2F2F2` vs the dark tint, `Surfaces.cs:58-61`), the cover-grading half a surface reads
   (`SchemeFor` follows the theme, `ChromeSchemeFor` is deliberately **opposite**, `:120-154`), the stage's whole ink
   ladder (`StageArm.For(theme)`, `StageArm.cs:36-64`) and lyrics ink through it (`LyricsInk.cs:33-40`).
   The **five this chapter had missed**, each with its own per-theme ratio or its own answer to "which half of the
   grading": the concert plates and their two accent pulls (`ConcertUi.cs:164`, `:233`, `:260-263`, `:354-360`,
   `:913`, `:961`), the accent card fills every media card uses (`MediaCard.cs:48-56`), the now-playing hero wash
   (`NowPlayingPanel.cs:203-209`), the nav-preview accent seed (`NavPreview.cs:64`) and the search hero
   (`SearchHero.cs:39-41`) — drawn at §2 W5B.
   A fourteenth — the nine deck faces — has **no light arm at all, by design** (§4.9).
8. **A page swap composes THREE channels, and they have three different clocks.** The content card runs the
   fade-through pair (exit 120 ms accelerate from 0 ms; enter `Expressive.Fast` decelerate from **90 ms**,
   `PageNavMotion.cs:49-59`); the masthead band fades its own opacity over the **same 120 ms**, keyed off the family
   predicate, never collapsing to height 0 (`ShellMastheadBand.cs:23-26`, `:66-70`); the shell material cross-fades
   over `WaveeMotion.Standard` **250 ms** under an ownership handover that never clears
   (`ShellMaterialLayer.cs:96-98`, `ShellMaterial.cs:39-58`). Three clocks is the design: the card is fast because
   two full-bleed pages must not co-exist at readable opacity; the chrome is slow because a hue change should feel
   like light moving, not a cut.
9. **A swap touching a module page slides instead of cross-fading, and both sides are classified.** A composited
   video is a DestOut hole; an ancestor opacity washes it out and an opacity group erases it. So
   `ContentHost.PageTransition` tests **both** the outgoing and incoming route and takes the translate-only pair
   (`ContentHost.cs:150-153`, `PageNavMotion.cs:96-121`). Neutral has no video-safe form and is an honest cut.
10. **The shell material is a HAND-OVER, never a clear.** A page claims the slot on first publish and on every
    KeepAlive reactivation; a refresh lands only while that page still owns it; a page that parks writes nothing
    (`ShellMaterial.cs:39-58`). The "no colour" reading is `WaveeColors.ShellGround @ A 0.03`, **never**
    `ColorF.Transparent` — premultiplied transparent is black and drags every cross-fade toward black
    (`ShellMaterialLayer.cs:80-89`).
11. **Offline, metered and reconnecting are THREE layers that must be legible together.** The chrome chip
    (`ShellAuthState.Offline` → an **accent** `Reconnect` button, `MergedChromeRow.cs:216-219`), the player bar
    (`PlayerState.Reconnecting` → a secondary-coloured title plus an indeterminate top edge, `PlayerBar.cs:171`,
    `:627`, `:736`) and the page's own error state underneath both. 0.2.9 never specifies their composition; §2 W6
    does.
12. **A metered link changes two visible things and nothing else.** The streaming quality cap
    (`min(userQuality, meteredCap)`, `NetworkPolicy.cs:95-105`) and the protected-video height cap
    (`EffectiveVideoMaxHeight`, `:119-125`); prefetch is deferred silently (`ShouldDeferPrefetch`, `:50`). Unknown
    cost is **unmetered-conservative** — a failed probe never throttles playback (`:14`).
13. **A toast's severity is not a Wavee decision.** `ToastOptions.Severity` is `InfoBarSeverity`, and the glyph,
    the icon plate, the inverse foreground and the tinted card ground all come from one shared table so toast and
    InfoBar cannot drift (`SeverityVisuals.cs:19-26`). Default duration **5 000 ms**; `0` is sticky
    (`Toast.cs:33`); `MaxVisible = 3` with a FIFO overflow queue (`:82`).
14. **A repeated toast REFRESHES; it never stacks.** The effective dedupe key is `DedupeKey ?? message`, and a match
    restarts the countdown and adopts the newer action (`Toast.cs:38-46`, `:169-193`). That is what stops the
    accidental double-`Show`, and it is why seventeen of the 107 sites can share
    `Strings.Detail.AddedToPlaylist(name)` without producing a stack.
15. **A bound action that cannot run is VISIBLE and EXPLAINED, never hidden.** `Resolve` folds every reason into one
    call so the row's disabled state and `Execute`'s refusal cannot disagree (`WaveeActionDescriptor.cs:96-116`);
    seven reasons each carry a loc key (`WaveeActionTargeting.cs:140-146`); the picker renders the reason as a
    warning-glyph caption rather than removing the row (`SidebarItemPickers.cs:529-552`).
16. **A confirmation-required action REFUSES to run when there is no overlay.** `Resolve` returns
    `HostUnavailable` before anything else can happen (`WaveeActionDescriptor.cs:109-110`), and `Execute` re-checks
    (`:134`). "No sidebar binding can bypass the confirm" is enforced by construction, not by convention.
17. **"A request is in flight", "the answer was nothing", "the request failed" and "there is no network" are four
    states, and 0.2.9 has vocabulary for three.** `LoadState` is `{ Pending, Ready, Failed }` (`Loadable.cs:5`);
    `EmptyState` and `ErrorState` share one grammar (`ErrorState.cs:8-13`); `OfflineBanner` exists (30 lines) and
    **has no call site under `Features/`**. §7.2 is the per-surface table that makes the fourth state real.
18. **A skeleton suppresses the scroll rail; an empty state does not.** `LoadingBarSuppressors > 0` skips the
    scrollbar emit entirely (`SceneRecorder.cs:3402`). A shimmering page with a live scroll thumb is the tell that a
    surface crossed into `Ready` with no content.
19. **A surface crosses from skeleton to empty exactly once per account, and never back.** The skeleton is for "a
    request is in flight"; the empty state is for "the answer was nothing". §2 W20 draws the one crossing; §7.2
    generalises it per surface.
20. **The connected-animation key convention is ONE function and it is currently dormant.** `MorphKeys.For` mints
    `"album:"+id` / `"pl:"+id` — the same string the card navigates with, so source and destination agree with zero
    extra plumbing (`MorphKeys.cs:8-16`). Five sites plumb it; the destination is **deliberately null**
    (`DetailShell.cs:257`), documented as investigated and left. The nav probe asserts on it
    (`WaveeNavProbe.cs:2575`, `:2758-2783`). It is invisible plumbing a re-author will delete as dead code — §2 W10
    is the record that stops that.
21. **`.ToUpper()` on a localised string is forbidden at the eyebrow rung, and that refusal is the reason the
    tracking is 30.** `WaveeType.Eyebrow` takes the string's own casing; 30/1000 em is "the value that survives
    SENTENCE case", replacing nine ad-hoc tracking values tuned for caps (`WaveeType.cs:33-55`). The one
    sanctioned caps transform left in the app is the Classic track table's column headers
    (`DetailTracks.cs:2509`, `:3743`).
22. **The context band ESTIMATES text width and never measures it.** `AvgCharW = 7.6` DIP/char, deliberately above
    the real ~6.9 average for mixed-case Latin so a localised label reserves enough room rather than clipping
    (`ContextBandLayout.cs:70-80`). Nothing in the band drops at a breakpoint.
23. **Right-to-left is unsupported, and that is a decision.** No `FlowDirection` / `RightToLeft` / `BiDi` layout code
    anywhere under `src/apps/Wavee`; the engine's shaper resolves BiDi glyph runs but nothing mirrors layout.
    Overflow is per element: `TextTrim.CharacterEllipsis` or a `Marquee`, never a reflow.
24. **Two of the four shipped locales are shown and DISABLED on purpose.** `nl` and `ko-KR` are visible in the picker
    with a parallel `Enabled` mask so the row advertises what is coming, and `SetLanguage` refuses them twice — at
    the combo and again in the handler (`SettingsPage.General.cs:29-43`, `:70-76`).
25. **Auto zoom takes BOTH axes and snaps DOWN.** `min(baseW/1600, baseH/900)`, clamped to `[1.0 | 0.75, 2.0]`,
    snapped down to `{0.75, 1, 1.25, 1.5, 1.75, 2}` (`ZoomAutoPolicy.cs:71-89`). 1.20 → 100 %, 1.55 → 150 %. The
    input is the **base** DIP extent (`viewportDip × zoom`), never the live one (`:30-38`).
26. **A manual zoom move demotes the policy to Manual; the policy never clobbers a user's pick**
    (`WaveeShell.cs:616-628`, `ZoomAutoPolicy.cs:106-113`).
27. **Zoom crosses layout tiers in ONE direction.** `viewportDip = clientPx / (osDpi × zoom)`, so zooming **in**
    shrinks the DIP viewport and a page can only demote; zooming out can promote. Chapter 30 W23 states it; this
    chapter owns the ladder table that makes it checkable (§2 W12).
28. **One dialog family, one width ladder: 320 / 460 / 480 / 548.** Every modal is the engine `ContentDialog` card
    (min 320 × 184, max 548 × 756, padding 24, `Radii.OverlayAll`, `Elevation.Dialog`, `ContentDialog.cs:110-117`,
    `:361-369`); 480 is also what a three-button dialog picks on its own (`:283`). Two raw-overlay exceptions exist
    **because the card clamps** (the setup plate 762 × 490, the after-update plate 720). Nothing else invents a width.
29. **A destructive confirm defaults to Close, not Primary** (`SettingsShared.cs:38`) — the accent button is the one
    the focus trap focuses first (`ContentDialog.cs:256-260`), so a reflexive Enter must cancel.
30. **The drag chip is the ONLY moving visual and the only caption surface**, and a drag kind the resolver does not
    recognise draws nothing at all (`WaveeResourceDrag.cs:307-318`). **A refusal always has a sentence**
    (`WaveeDragRules.cs:126-142`).
31. **`--fake` must render LOADED states, not empty ones**, and the gate must say so. Twelve `Null*Service` seats
    plus `UnsupportedPlaybackPlayer` mean "`--fake` opens every route" passes on blank pages (§2 W22).
32. **The nav probe is blind to eighteen of thirty destinations.** Its two route tables drive twelve
    (`WaveeNavProbe.cs:35-49`); `ShellRoutes` knows sixteen exact names plus ten prefixes plus the three concert
    routes (`ShellRoutes.cs:26-60`, `ConcertRoutes.cs:9-11`). Any gate resting on the probe inherits that
    blindness — §9.7.
33. **Windows contrast themes are not supported, and that is a decision, not an oversight** (§9.8).

---

## 1. Anatomy

### 1.1 The twelve concerns and their 0.2.9 homes

```
CONCERN                      0.2.9 home                                        reaches the user through
── the keyboard's own visuals ────────────────────────────────────────────────────────────────────────────────────
focus ring tokens            engine Tokens.cs:123-124, :456             (3)    every focusable node in the app
focus ring geometry          engine SceneRecorder.cs:3367-3396         (30)    ditto
focus-visible vs pointer     engine InputDispatcher.cs:3763-3781       (19)    whether a click leaves a halo
FocusVisualMargin overrides  36 app call sites, FOUR distinct values           chips, cards, rows, sliders
                             (+2 ×22 · +1 ×11 · −2 ×2 · −3 ×1; a 37th grep hit
                              is the comment at Design/WaveeCta.cs:12)
roving tab index             engine ItemsView.cs:393, :1574-1580               track tables, card grids
focus restoration            engine OverlayHost.cs:470-490 + 14 app sites      dialogs, flyouts, drawers, fullscreen
                             — and NOTHING on a page nav (§6.3)
── colour that is COMPUTED, not read ─────────────────────────────────────────────────────────────────────────────
cover tint + page tone       Design/CoverPaletteLeaves.cs              265     every detail page's ground + the chrome
Home's three radial washes   Features/Shell/ShellWashGeometry.cs        55     the whole window on Home
the stage's ink ladder       Design/StageArm.cs + StageInk.cs          225     the immersive stage, lyrics on media
lyrics ink (the MODE seam)   Features/Player/LyricsInk.cs              ~90     rail lyrics vs stage lyrics
the nine deck faces          Features/Player/Deck/Faces/*.cs        ~4 800     the now-playing hero — NO light arm
video scrims                 engine Tokens.cs:376-386 + 4 app sites            PiP, docked, fullscreen transports
Liked generated covers       Features/Detail/LikedCoverLeaves.cs       299     Liked's nine cover styles — dark-authored
search highlight             Design/SearchHighlight.cs                  81     library rows + Charts grid titles
── the composition of a page swap ────────────────────────────────────────────────────────────────────────────────
the content-card recipe      Features/Shell/PageNavMotion.cs           122     the page that slides / fades
the masthead band            Features/Shell/ShellMastheadBand.cs       131     "Browse › Title" above the card
the shell material           App/ShellMaterial.cs + ShellMaterialLayer 192     the window's hue
── the network, as a picture ─────────────────────────────────────────────────────────────────────────────────────
the cost policy              App/NetworkPolicy.cs                      189     quality cap, prefetch, the Settings line
the auth fold                App/PlaybackBridge.cs:259-298             ~40     the chrome chip
the player bar's arm         Features/Shell/PlayerBar.cs:171, :627             title colour + the indeterminate edge
the page's own error         Components/ErrorState.cs                   45     the body underneath both
── what the app SAYS ─────────────────────────────────────────────────────────────────────────────────────────────
in-app toasts                107 Toast.Show sites / 38 files                   the bottom-right strip
the severity table           engine SeverityVisuals.cs                  27     glyph + plate + ground
confirmations                Features/Shell/SettingsShared.cs:29-41     13     every destructive verb
── the action platform ───────────────────────────────────────────────────────────────────────────────────────────
the descriptor               Actions/WaveeActionDescriptor.cs          227     a bound row's label, icon, state
the registry + targeting     Actions/Extensibility/*                   946     what a binding may aim at, and why not
the menu vocabulary          Actions/Menus.cs                        1 197     every context menu in the app
── the four readiness states ─────────────────────────────────────────────────────────────────────────────────────
Loadable / LoadState         engine Loadable.cs                         58     Pending | Ready | Failed — THREE
EmptyState / ErrorState      Components/{EmptyState,ErrorState}.cs     120     one grammar, two voices
OfflineBanner                Components/OfflineBanner.cs                29     the FOURTH state — zero call sites
the detail notice pipeline   Features/Detail/PlaylistPageNoticeRules   ~90     a page that must not un-render
── the dormant morph ─────────────────────────────────────────────────────────────────────────────────────────────
the key convention           Components/MorphKeys.cs                    17     nothing today; the pair is half-wired
── long strings ──────────────────────────────────────────────────────────────────────────────────────────────────
locale bootstrap             App/AppLocale.cs                           44     the whole UI + Spotify metadata
the estimator                Features/Detail/ContextBandLayout.cs:70-80        the context band's cluster widths
the caps refusal             Design/WaveeType.cs:33-55                         every eyebrow in the app
── zoom ──────────────────────────────────────────────────────────────────────────────────────────────────────────
the policy                   App/ZoomAutoPolicy.cs                     114     the whole window's DIP extent
the picker + live label      SettingsPage.Appearance.cs:73-91, :187-199        "Auto (150%)"
── the gates ─────────────────────────────────────────────────────────────────────────────────────────────────────
the nav probe                Features/Diagnostics/WaveeNavProbe.cs   2 915     12 of 30 destinations
ReuseGuard                   engine, per control                               the props-freeze tripwire
FG_FPS_LOG / FG_ALLOC_DIAG   engine AppHost.cs:594-610                         per-segment bytes + ticks
the perf tour                ops/tools/perf-tour.ps1                           scripted scroll under measurement
the --fake seed              Wavee.Core/Fakes/* + assets/spotify/*    ~735     every screenshot and every checklist
── still owned here, unchanged ───────────────────────────────────────────────────────────────────────────────────
dialog vocabulary            (none — 11 call sites, 4 widths)                  every modal
drag visual language         Features/DragDrop/*                       938     chip, line, gap, spring-load, refusals
first-run composite          WaveeApp.cs:315-418 + SetupGating.cs     ~280     the frame a new account meets
ambient cadence              App/AmbientPowerPolicy.cs                 140     every `loop: true` row in the app
```

**What moved out of this chapter since its first draft.** The four OS surfaces (SMTC, taskbar button, thumbnail
toolbar, jump list, Windows toasts) are `14-os-surfaces.md`'s subject — that chapter draws 28 wireframes of them and
budgets `Playback/Playback.Os.cs` itself. Window state, snapping and the live DPI hop are `18-shell-frame.md`
W23/W24. Both claims were correct when this chapter first made them and are no longer; §11 audits 24 and 25 are the
retractions, and §12.1's "14 is still unused" sentence is struck.

### 1.2 The same tree in 0.3 terms

| 0.2.9 node | 0.3 home | Shape | Inputs |
|---|---|---|---|
| the focus ring's tokens and geometry | **engine** — do not re-declare | — | `Tok.FocusOuter` / `FocusInner` / `FocusThickness` |
| the two sanctioned `FocusVisualMargin` insets | `Platform/Design.cs` — **two named constants**, `FocusInsetBordered = 2f` and `FocusInsetRow = 1f` | consts | — |
| `MorphKeys` | `Platform/Design.cs` (the convention) + `Entities/*.UI.cs` (the two plumbing ends) | one static | `DetailKind` + id |
| `PageNavMotion` | `Shell/Shell.cs` **CORE** — it is already pure enough to be source-included by tests, and is today | static recipes + `SlotKey` | `NavTransitionKind` |
| the video-safe classification (`ContentHost.PageTransition`) | `Shell/Shell.Host.cs` **SHELL** — must stay out of CORE because it knows module routes (`ContentHost.cs:145-149`) | one method | both slots' routes |
| `ShellMastheadStore` + `ShellMastheadBand` | `Shell/Shell.cs` (the LRU) + `Shell/Shell.UI.cs` (the band) | bounded LRU + one `Component` | route + the publication |
| `ShellMaterial` + `ShellMaterialLayer` + `ShellWashGeometry` | `Shell/Shell.cs` (publish rule + ownership) + `Shell/Shell.UI.cs` (the layer) + `Platform/Design.cs` (the geometry constants) | static + one `Component` | a `Signal<ShellMaterialState>` at the root |
| `NetworkPolicy` | `Platform/Platform.cs` **SHELL** (the NLM subscription + poll) and **CORE** (`EffectiveQuality`, `EffectiveVideoMaxHeight` — pure) | statics + two signals | `IAppSettings`, `NetworkStatus` |
| the three network layers' composition | `Shell/Shell.UI.cs` — **one `NetworkChrome` section**, today spread over three files | pure projection → three elements | `Playback.AuthState`, `Playback.RecoveryKind`, `Platform.Network.Cost` |
| `Toast.Show` (107 sites) | the call shape is unchanged, but every site routes through `Platform/Controls.cs`'s `Notify.Say(key, severity, …)` so §6.4's inventory is **generated, not grepped** | one static | a loc key, a severity, an optional action |
| `WaveeActionDescriptor` + `WaveeActionTargeting` + `WaveeRegistryTable` + `WaveeExtensionRegistry` | settled (A7) **`Platform/Actions.cs`** **CORE** (descriptor shape, targeting matrix, the seven reasons, the one action table, the thirteen first-party descriptors, the extension registry), owner **I** | init-only class + pure statics | an `ActionServices` bag, a binding |
| `PinRowRule` | settled (A7) **`Sidebar.cs`** — goes the other way from the rest of the action platform, owner **J** | pure static | the sidebar's own drop/pin rules |
| `BuiltInExtensionTable` | settled (A7) folded into **`Platform/Actions.cs`** CORE (the thirteen first-party descriptors) — no separate `Actions.Table.cs`, hand-written today, **source-generated in M4 against this exact call shape** (`BuiltInExtensionTable.cs:11-14`) | one `RegisterAll` | the registrar |
| `Menus.cs` (1 197 lines) | settled (A7) folded into **`Platform/Actions.UI.cs`** — the menu vocabulary, landed before owner J's "Move to folder…" picker; the *verbs* stay in `Entities/*` | static builders | `ActionContext` |
| `EmptyState` / `ErrorState` / `OfflineBanner` | `Platform/Controls.cs` — **ONE `Vacancy` grammar with four voices** (§7.2), not three types | static builders | title, caption, one quiet action |
| `Loadable<T>` / `LoadState` | **engine** — and 0.3 adds no fourth enum member: offline is a *page-level* verdict, not a per-field one (§7.2) | — | — |
| `AppLocaleBootstrap` | `Platform/Platform.cs` **SHELL** (`Platform.Boot()` step 1) | static | `IAppSettings` |
| `ContextBandLayout.AvgCharW` + the estimator | `Entities/Detail.cs` **CORE** (ch 03), with the constant restated in `Platform/Design.cs` §type | pure | a label length |
| `ZoomAutoPolicy` | `Platform/Platform.cs` **CORE** (BCL-only, source-included by tests) | pure statics | base DIP extent + mode |
| the auto-zoom effect | `Shell/Shell.Host.cs` | one `UseSignalEffect` | `Viewport.Size`, `Viewport.Zoom`, `Platform.Settings` |
| the `ContentDialog` call sites | `Platform/Controls.cs` — **three named helpers** (`Dialog.Confirm`, `Dialog.Name`, `Dialog.Panel`) | static builders | title/body/verb + a callback |
| `WaveeResourceDrag*` rules | proposed `Platform/Drag.cs` **CORE** + `Platform/Controls.cs` (`Drag.Chip`, the gap preview) | records + pure tables | handles, not models |
| `FakeData` + `FakeSource` + `FakePodcastSource` + `SpotifyExportSource` | proposed **`Entities/Entities.Fake.cs`** — `Entities.SeedFake()` | one static seeding function writing **staging columns** | none (deterministic) |
| `WaveeNavProbe`'s route tables | `Diagnostics/Diagnostics.Probe.cs` — **driven off `ShellRoutes`**, never a second list (§9.7) | one table | — |

**Props freeze at mount — where the 0.3 tree must use a Signal / Func / Key:**

* `ShellMaterialLayer` takes **two read-signals** in its constructor and reads `state.Value` in `Render` — that is
  the point: a material change re-renders only this component, never the shell (`ShellMaterialLayer.cs:26-40`). A
  0.3 re-author who passes a `ColorF` freezes the window's hue at mount.
* Each wash layer is **keyed on its artwork** (`Key = key + ":" + ArtworkKey`, `:112-114`), and that key IS the
  cross-fade mechanism: `GradientSpec` is not a Prop, so a wash can only change by remounting, and remounting is
  what runs Exit + Enter over the same pixels.
* The flat tint is the opposite: **always mounted**, carrying `BrushTransitionMs` — so the node stays live across a
  material change and the brush has a previous colour to fade from (`:96-98`).
* `ShellMastheadBand` holds its last-live trail in **plain fields**, not signals (`:29-33`): an unknown family keeps
  the last masthead and fades opacity rather than collapsing height. Fields are correct here precisely because they
  must survive a render that resolves nothing.
* `CoverShellTintBinder` claims the material slot from `UseActivation(onActivated:)` — **always a claim**, whether or
  not it has published before, because the whole point is to retake the slot from whatever deactivated in between
  (`CoverPaletteLeaves.cs:259-261`).
* `WaveeActionDescriptor` is **constructed at startup and then immutable**, with delegates that run on the UI thread
  only (`WaveeActionDescriptor.cs:22-27`). It is not a component and has no props gate; the gate is on the row that
  renders it, which must read `Resolve(...)` **reactively** (`peek: false`, the default) or the row's disabled state
  goes stale (`:101-103`).
* The file-drop cue's opacity is `Prop.Of(() => _fileDropOver.Value ? 1f : 0f)` (`WaveeShell.cs:1422`) — a **bound**
  prop. Reading it in `Render` re-renders the whole shell on every drag enter/leave.
* `ContentDialog` bodies that must change while open use `d.Footer` (a reactive footer) rather than re-opening
  (`PlaybackRuntimeSetupCard.cs:940`); `SidebarActionPickerFooter` exists for exactly this reason —
  `IsPrimaryButtonEnabled` is read once at card-build time (`SidebarItemPickers.cs:556-562`).

---

## 2. Wireframes

Scale is declared per frame. Detail frames use **1 char ≈ 8 DIP**; whole-window frames **1 char ≈ 16 DIP**;
timelines declare ms/char. Nothing here is drawn at Windows' own metrics — the OS surfaces left this chapter (§1.1).

**Twenty-eight frames: W1–W26, plus W5B** (the five derived-colour surfaces the first pass missed) **and W27** (the
states this chapter checked and does *not* own, with the grep that settles each — so silence about a state can never
be mistaken for nobody having looked).

### W1 — the focus ring, its two strokes, and the three margins in the app (1 char ≈ 2 DIP, magnified)

```
 THE ENGINE'S DEFAULT, FocusVisualMargin = -3 (Button.cs:102) — the ring lives OUTSIDE the control
        ┌───────────────────────────────────────────┐ ← focus rect = bounds expanded by -margin = +3 per side
        │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │ ← PRIMARY   2 DIP  Tok.FocusOuter   SceneRecorder.cs:3386
        │ ▓ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ▓ │ ← SECONDARY 1 DIP  Tok.FocusInner   :3390-3396
        │ ▓ ░ ┌───────────────────────────────┐ ░ ▓ │
        │ ▓ ░ │        the control            │ ░ ▓ │   corner radius grows by min(adjacent expansions)
        │ ▓ ░ └───────────────────────────────┘ ░ ▓ │   so the arc stays concentric              :3381-3384
        │ ▓ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ▓ │
        │ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓ │   with the default margin the pair lands exactly on
        └───────────────────────────────────────────┘   the control edge: edge → 1 inner → 2 outer  :3367-3371

 WAVEE OVERRIDE A — FocusVisualMargin = +2, an INSET (the ring is drawn INSIDE the bounds)
        ┌───────────────────────────────────────────┐ ← the control's OWN 1-DIP border
        │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │ ← the ring sits 2 DIP in, clear of that border
        │  ▓ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ▓  │
        └───────────────────────────────────────────┘
   WHY: "The focus ring insets 2 DIP (WinUI's -7,-3 would draw it through the card's own border)"
        WaveePicker.cs:235-237 — the glyph-less radio, where the CARD is the control.
   WHERE (the COMPLETE census, 22 sites — re-grepped 2026-09-12): WaveePicker.cs:244 · ContentFilterChips.cs:83 ·
        BrowseTiles.cs:38, :73, :101, :172 · BrowsePage.cs:702 · ArtistPage.Shelves.cs:34, :116 ·
        ConcertUi.cs:94, :142, :172, :469, :751 · ConcertHubPage.cs:316 · MonthBoard.cs:156, :202 ·
        RecentsPage.cs:572 · LibraryV3Chips.cs:253, :290 · LikedFactsPanel.cs:1078, :1122
   THE SHAPE THEY SHARE: a bordered or filled pill / tile with Corners >= Radii.Control and a visible stroke.

 WAVEE OVERRIDE B — FocusVisualMargin = +1, a tighter INSET, for a row inside a virtualized list
   row n-1  │ 11  ♥ [art] Undo                      Björk        3:41 │
   row n    ╔═══════════════════════════════════════════════════════╗  ← 1 DIP in: the ring cannot bleed
            ║ 12  ♥ [art] Weird Fishes            Radiohead      4:21 ║    into row n-1's or n+1's pixels,
            ╚═══════════════════════════════════════════════════════╝    which at RowDensity 40 is 0 DIP of gap
   row n+1  │ 13  ♥ [art] Genesis                   Grimes       4:12 │
   WHERE (the COMPLETE census, 11 app sites + 2 engine): TrackRow.cs:548 (the row container), :631 (the
        expand chevron) · DetailTracks.cs:3568 (the table's slot root) · FacePiles.cs:103, :132 ·
        LikedFactsPanel.cs:696, :724, :1157, :1185, :1459, :1747 · engine Charts/DensityPlot.cs:176 and
        Charts/SparkBars.cs:88 (the same shape, already adopted in the engine)
   NOTE two of the eleven are CONDITIONAL — FacePiles.cs:132 and LikedFactsPanel.cs:696 both read
        `live ? new Edges4(1f,…) : default`, i.e. a non-interactive row falls back to the engine's own −3
        rather than to the inset. That is correct (a row that cannot be focused never draws a ring) but it
        means "FocusInsetRow" in 0.3 must stay a VALUE a call site may decline, not a style baked into the row.
   WHY, stated once: a -3 ring on a 40-DIP row with no gutter paints 3 DIP of the neighbouring row. At
        RowDensity 64 the error is invisible; at 40 it reads as the WRONG row being focused.

 THE THIRD VALUE, and it is a defect — FocusVisualMargin = -2
        PlayerStyleFlyout.cs:101, :182 — ThumbCard: a 6-DIP-cornered padded card inside a flyout.
        No comment, no precedent. It is Override A's shape with Override A's problem, so 0.3 normalises
        it to +2. (CalendarView's engine -2 is a different control with a different template and is NOT
        the precedent.)                                                              → §9.9.4, parity 6

 THE ENGINE DEFAULT, RESTATED — FocusVisualMargin = -3
        WaveeEqualizerCurve.cs:144, Role = Slider. It restates the engine default explicitly because the
        node is a hand-built BoxEl, not a Button. Leave it.
```

### W2 — the ring's colour, both themes (the two tokens are inverted, not tinted)

```
                       LIGHT                                  DARK
  FocusOuter    #000000 @ E4 (89 %)  black              #FFFFFF @ FF (100 %) white
  FocusInner    #FFFFFF @ B3 (70 %)  white              #000000 @ B3 (70 %)  black
                PaletteBuilder.cs:254-255                PaletteBuilder.cs:342-343
  FocusThickness   2f, BOTH themes                       Tokens.cs:456 — a const, not a palette entry

  ┌─ on a LIGHT page ──────────────┐        ┌─ on a DARK page ───────────────┐
  │ ███████████████████████████████│ black  │ ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ white
  │ █ ░░░░░░░░░░░░░░░░░░░░░░░░░░░ █│ white  │ ▒ ███████████████████████████ ▒│ black
  │ █ ░ ┌───── control ─────┐   ░ █│        │ ▒ █ ┌──── control ─────┐   █ ▒│
  └────────────────────────────────┘        └────────────────────────────────┘

  The pair is a DUAL ring for one reason: whichever theme the control's own fill happens to be, one of the
  two strokes contrasts against it. That is why the light arm is not "the dark arm at a lower alpha".

  ON MEDIA — a control drawn over artwork or a video frame keeps the THEME's ring. There is no media arm
  for focus and there must not be one: the ring is a system affordance, not part of the surface's own
  material. Verified: no app site overrides FocusOuter / FocusInner anywhere.
```

### W3 — focus-visible vs pointer focus, and the six restoration paths

```
 ── THE FLAG ──────────────────────────────────────────────────────────────────────────────────────────
   SetFocus(node, visual: false)   pointer press      InputDispatcher.cs:968, :1576   → NO ring
   SetFocus(node, visual: true)    Tab / Shift+Tab    :3677                           → ring
   MoveFocusVisual(node)           roving arrows      AppHost.cs:2487                 → ring
   SetFocus(Null)                  unhandled Escape   :3577, :3781                    → ring clears, PaintDirty
   NodeFlags.FocusVisual is what the recorder tests (:2596). The same flag distinguishes keyboard focus
   from pointer focus for SELECT-ALL in a text field (:3767) — one flag, two consumers.

 ── THE SIX RESTORATION PATHS, as they behave in 0.2.9 ─────────────────────────────────────────────────

   1. DIALOG CLOSE            ✔ engine. OverlayHost saves focus on open and restores at close START, so
                              the restore can land OUTSIDE the dying subtree (OverlayHost.cs:472-477);
                              and only when focus still lives inside this overlay or is dead (:482-490).
                              Initial focus on open is the ACCENT button (ContentDialog.cs:256-260).

   2. FLYOUT / MENU CLOSE     ✔ engine, same path — every popup is an OverlayHost entry.

   3. DRAWER CLOSE (the       ✔ engine, same path (the drawer is a popup entry — ch 18 W6).
      narrow shell's sidebar)

   4. FULLSCREEN VIDEO EXIT   ✔ app, hand-written. priorFocus captured in UseLayoutEffect; PushFocusScope;
                              on teardown PopFocusScope then RestoreFocus(back), with the comment "a stale
                              handle is harmless … but never restore to the surface we are tearing down"
                              (VideoFullscreenSurface.cs:128-147). The immersive lyrics surface has the
                              same shape minus the restore (ImmersiveLyricsSurface.cs:157, :204).
                              BOTH also carry a NEVER-LEAVE-FOCUS-NULL guard: OnFocusChanged(got:false)
                              re-focuses the root when the dispatcher's focus went null — "once mounted,
                              an automatic reappearance still owns keyboard Escape" (:170-180).

   5. PAGE NAVIGATION         ✖ NOTHING. No FocusNode, no RestoreFocus, no focus scope on the KeepAlive
                              boundary. Focus stays wherever it was — usually the sidebar row or the
                              omnibar the user activated, which is usually right and is never STATED.

   6. TAB SWITCH              ✖ NOTHING (it is a route change through the same boundary).
      KEEPALIVE REACTIVATION  ✖ NOTHING. A parked page's focused node is not restored on return. The
                              engine's roving stop IS preserved, because it is a node flag on a
                              live-but-parked subtree. UNVERIFIED whether that flag survives
                              ReleaseInactiveResources — the code does not say.

 ── THE 0.3 RULE (adopt it, or record the gap deliberately — silence is the one wrong answer) ──────────
   • A page swap MOVES focus only when the user's gesture had no focusable origin: a deep link, a jump
     from a toast action, a command-palette pick. Then focus the content card's first focusable.
   • Back / Forward restore the destination slot's LAST focused node when that node is still live;
     otherwise the content card. The slot key already exists (PageNavMotion.SlotKey) — the store is one
     dictionary keyed on it, capped at KeepAliveOptions.MaxEntries = 3 like the slots themselves.
   • A sidebar click, a card click and a track double-click leave focus alone. The user is looking at
     what they clicked; a forced move re-parents the ring somewhere they were not looking.
   • Nothing may call SetFocus with visual: true from a POINTER path. FIVE sites pass true today (not the
     three an earlier draft counted) and all five are keyboard or caret paths: MergedChromeRow.cs:263 and
     LibraryV3Search.cs:101 (opening a search field, where the caret IS the affordance),
     LibraryV3Search.cs:154 ("hand focus back to the magnifier" when the field collapses), and
     DetailTracks.cs:2183 + :2300 (restoring the ring to the table's search button, the same shape twice —
     the compact band and the expanded one).
   • The FOUR that pass visual: false are the ones a pointer or an auto-mount reaches: DetailTracks.cs:4207
     and PlaylistInlineEdit.cs:272 (an inline editor focused OnRealized — a caret with no ring, correct),
     and ImmersiveLyricsSurface.cs:157/:204/:291 + VideoFullscreenSurface.cs:137/:178/:216 (a surface taking
     keyboard Escape without painting a ring on its own root).
   • FULL CENSUS: 15 FocusNode / PushFocusScope / PopFocusScope / RestoreFocus sites across 7 app files.
     Not one is on a route change or a KeepAlive activation (§0.6).
```

### W4 — the roving tab index, drawn (a virtualized track table, 1 char ≈ 8 DIP)

```
  TAB lands ONCE on the list. Inside it, arrows move the CURRENT item and the ring goes with them.

  ┌ before Tab ────────────────────────────────┐   ┌ after Tab ─────────────────────────────────┐
  │  [ Play ]  [ ♥ ]  [ ⋯ ]     ← chrome row   │   │  [ Play ]  [ ♥ ]  [ ⋯ ]                    │
  │  ────────────────────────────────────────  │   │  ────────────────────────────────────────  │
  │  1  Weird Fishes      Radiohead     4:21   │   │ ╔══════════════════════════════════════╗   │
  │  2  Reckoner          Radiohead     4:50   │   │ ║1  Weird Fishes    Radiohead    4:21  ║   │ ← the ONE tab stop
  │  3  House of Cards    Radiohead     5:28   │   │ ╚══════════════════════════════════════╝   │   lands on the
  │  …                                          │   │  2  Reckoner        Radiohead    4:50      │   keyboard-CURRENT
  └────────────────────────────────────────────┘   └────────────────────────────────────────────┘   item, or item 0

  ↓ (arrow, not Tab)                                the stop MOVES IN PLACE — no re-render, no remount
  │  1  Weird Fishes      Radiohead     4:21   │    ItemsView.cs:1574-1580: "move the single roving tab
  │ ╔══════════════════════════════════════╗   │    stop to the keyboard-current slot IN PLACE"
  │ ║2  Reckoner        Radiohead    4:50  ║   │
  │ ╚══════════════════════════════════════╝   │    TAB again LEAVES the list entirely (TabNavigation
                                                    "Once", ItemsView.xaml:7 → ItemsView.cs:393)

  HOW THE APP PARTICIPATES — four lines, and all four are load-bearing
    TrackRow.cs:547         Focusable = false     the row container is NOT its own tab stop
    DetailTracks.cs:3569    Focusable = false     "the ItemsView roving effect owns the single tab stop"
    DetailTracks.cs:1019    IsItemEnabled = i => rowItems.TryPeek(i, out _)
                                                  "only track rows are roving-focus / selection targets" —
                                                  a header, a disc divider or a drawer slot is skipped by
                                                  the arrows, not merely un-clickable
    DetailTracks.cs:1543    IsItemEnabled = i => _rowItems!.TryPeek(i, out _, VerticalTrackStart)
                                                  THE SECOND TABLE, which an earlier draft missed. The
                                                  vertical/magazine layout carries its OWN ItemsView with its
                                                  own predicate and its own row-start offset. Two tables, one
                                                  rule — 0.3 must port BOTH, or the arrows walk headers on
                                                  one of the two skins and nothing else changes to show it.

  THE ROW'S OWN CONTROLS still Tab normally ONCE the row is current: the expand chevron
  (TrackRow.cs:625-631, Focusable = true, +1 margin) is inside the row, so it is reached by Tab from the
  row and returns to the row on Shift+Tab. A drawer opened by that chevron is a CHILD of the slot, not a
  list item of its own, "so selection, reorder, roving focus and [the rest] …" (DetailTracks.cs:3165).

  A CARD GRID is the same mechanism with 2-D arrows: the engine's FocusDirection walk is
  primary-axis-distance-dominant with the cross axis breaking ties (InputDispatcher.cs:3681), so Right at
  the end of a row lands on the next row's first card rather than nothing.

  WHAT BREAKS IT — three named regressions:
    · making the slot root Focusable = true      → 400 tab stops in a 400-track playlist
    · a Focusable child OUTSIDE the roving row    → Tab order interleaves rows and their chrome
    · rebuilding the item on arrow move           → the move is an in-place flag write; a re-render here
                                                    is one full list reconcile PER ARROW KEY
```

### W5 — light theme, the eight surfaces that COMPUTE their colour (1 char ≈ 16 DIP, schematic)

Every other surface in the app reads a token and is correct in both themes by construction. These eight solve a
colour instead, and each one solves it differently. This is the frame a light-theme parity run walks.

```
 ┌ 1. THE SHELL TINT — published by a detail/artist page, painted under all chrome ─────────────────────┐
 │  dark   WaveePalette.TintedDark(artScheme)          @ A 0.14      CoverPaletteLeaves.cs:244-246       │
 │  light  WaveePalette.Lift(ToColor(artScheme.TextBase)) @ A 0.05                                      │
 │  "no colour"  WaveeColors.ShellGround @ A 0.03 — NEVER ColorF.Transparent  ShellMaterialLayer.cs:89   │
 │  The two arms do not share a formula: dark tints a DARK derivative of the scheme, light LIFTS the     │
 │  scheme's own text colour. Alpha 0.14 vs 0.05 is not "light is weaker" — it is a different source.    │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 2. THE PAGE TONE PLANE — the detail page's ground, a page-root sibling of the scrolling page ─────────┐
 │  colour  WaveePalette.PageTone(scheme, theme)  — the SAME call in both arms     :143-144             │
 │  alpha   dark 0.20   light 0.30                                                      :139             │
 │  The comment at :126-138 records the whole ratchet (1.0 → 0.72 → 0.45/0.90 → 0.20/0.30) and states    │
 │  the consequence out loud: "in light theme the record's hue is now close to imperceptible".           │
 │  → the LIGHT-THEME READABILITY FLOOR is not on this plane at all. Light identity is carried by the    │
 │    chrome accent (the Play capsule, the accent rule, row chrome). A light-theme parity run that       │
 │    reports "the album page has no colour" is reporting the DESIGN, not a defect — parity 17.          │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 3. THE ARTIST BLEND WASH — under the artist hero photograph ─────────────────────────────────────────┐
 │  dark   WaveePalette.BackgroundDark(pagePal ?? Neutral)   stops @ 0.30 → 0.08 → 0    :182-193         │
 │  light  WaveePalette.Lift(Accent(pagePal)) ?? Tok.AccentDefault  stops @ 0.20 → 0.06 → 0              │
 │  Again two different SOURCES, not one source at two alphas. The light arm falls back to the app       │
 │  accent when the cover is ungraded; the dark arm falls back to the neutral scheme.                    │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 4. THE RADIAL WASHES — the whole window. FOUR route families publish one, not Home alone ────────────┐
 │  WHO PUBLISHES (ContentHost.PublishesShellMaterial, :184-186 — keep in step with PageFor):            │
 │    home             THREE legs (Hero + Weekly + Mix)   HomePage.cs:229-237                            │
 │    home-section: /  ONE leg (Hero only; Weekly = Mix = null)  HomeSectionPage.cs:177-182              │
 │      browse-section:  "the app's two drill-in surfaces must sit on one ground" (:165-167)             │
 │    recents          ONE leg (Hero only — the most recent hydrated cover)  RecentsPage.cs:308-325      │
 │    album/pl/liked/local/show/artist  a flat TINT, never a wash (CoverPaletteLeaves.cs:252)            │
 │  A one-leg wash is NOT a weaker three-leg wash: it is the SAME Hero placement with two layers absent,  │
 │  so the window is lit from the top-left corner only. Anyone testing "the wash" on Home alone is        │
 │  testing one of the two shapes that ship.                                                             │
 │            origin α      geometry (window fractions)                    ShellWashGeometry.cs:24-37    │
 │   Hero     .055 / .10    c(0.06, 0.00) r(0.74, 0.92) fade 0.62                                        │
 │   Weekly   .050 / .085   c(0.92, 0.10) r(0.58, 0.78) fade 0.64                                        │
 │   Mix      .050 / .085   c(0.58, 1.00) r(0.90, 0.70) fade 0.66  ← clipped at the dock line            │
 │                          (Margin bottom = PlayerDock.Reserve)          ShellMaterialLayer.cs:72-77    │
 │   "Dark carries roughly twice the light strength: the same colour reads far weaker over the dark      │
 │    ground, and the light ground has less headroom before a wash turns into a smudge."  :30-32         │
 │   The transparent stop carries the wash's OWN RGB (straight-alpha interpolation) in BOTH arms —       │
 │   ColorF.Transparent would drag the ramp's hue to black across the falloff.        :121-126           │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 5. THE ARTWORK PLACEHOLDER — every un-decoded cover in the app ──────────────────────────────────────┐
 │  light  #F2F2F2  ArtworkPlaceholderLight            Surfaces.cs:58                                    │
 │  dark   ArtworkPlaceholderDark                      :57 (the tinted near-black)                       │
 │  A GRADED cover mixes toward its own tint: TryGetTint(url, light, …) — the light half of the grading  │
 │  in light, the dark half in dark. "Light theme only accepts a light grading; a dark-only entry keeps  │
 │  the neutral" (:76-89). The STAGE overrides the polarity deliberately: StageInk.ArtStandIn passes     │
 │  light: !IsDark so the stand-in follows the STAGE, not the page (StageInk.cs:76).                     │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 6. WHICH HALF OF THE GRADING A SURFACE READS — the one genuinely counter-intuitive rule ─────────────┐
 │  SchemeFor(url)        follows the theme       light theme ⇒ the LIGHT grading    Surfaces.cs:142     │
 │  ChromeSchemeFor(url)  is deliberately OPPOSITE                                   :145-154            │
 │      "a light page wants the DARK grading's chroma for its one solid CTA, and a dark page wants the   │
 │       light grading's softness so the plate does not glow"                                            │
 │  Every cover is graded TWICE by the provider (:115-118). A 0.3 re-author who "simplifies" the two     │
 │  calls into one makes every Play capsule in light theme washed out and every one in dark theme loud.  │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 7. THE STAGE'S WHOLE INK LADDER — StageArm.For(theme), the one theme branch on the surface ──────────┐
 │  Veil / Floor   dark Tok.MediaStage    light WaveePalette.PageToneNeutralLight     StageArm.cs:44-51  │
 │  Ink            dark WaveeOnMedia.Ink  light Tok.MediaStage  (an ON-MEDIA token, deliberately NOT     │
 │                 Tok.TextPrimary — the stage's ink must stay OPAQUE)                :53-64             │
 │  the ALPHAS are shared; only the GROUND is mirrored                                :26-28             │
 │  the scrim alphas need NO light arm at all: "mixing toward black at a partial alpha destroys far more │
 │  perceptual luminance than mixing toward white, so the light arm's alpha'd ink clears a HIGHER        │
 │  contrast ratio than the dark arm we already ship"                                 :30-33             │
 │  The dark arm delegates to WaveeOnMedia VERBATIM and a test pins it: "dark theme is byte-identical    │
 │  to what shipped" is an executable claim (:19-21).                                                    │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 8. LYRICS INK — a MODE, not a captured colour ───────────────────────────────────────────────────────┐
 │  LyricsInk.Theme (the rail)   Primary = Tok.TextPrimary       Secondary/Tertiary = the theme rungs    │
 │  LyricsInk.Media (the stage)  Primary = StageInk.Ink          … = StageInk's ladder  LyricsInk.cs:33-40│
 │  The resync chip's plate, stroke and ring follow the same seam (:47-58). "A theme plate under         │
 │  theme-invariant white ink is the same invisibility bug the ink seam exists to remove."               │
 │  It is a MODE because a ColorF frozen into a constructor would not survive a live theme flip —        │
 │  component props freeze at mount (:17-22).                                                            │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### W5B — the five derived-colour surfaces W5 missed (the completing half of the light-theme contract)

W5 was written from the eight surfaces one sibling chapter or another had already named. A repo-wide grep for
`Tok.Theme ==` / `ThemeKind.Light` / `ThemeKind.Dark` (2026-09-12) returns **50 branches across 15 files**. Subtract
the plumbing (`WaveeTheme.cs`, `WaveeTokens.cs`, `WaveePalette.cs`, `Program.cs`, `WaveeApp.cs`, `WaveeShell.cs`) and
the eight already drawn, and **five surfaces are left that compute a colour and were in no chapter's light arm.**

```
 ┌ 9. THE CONCERT PLATES — six branches, TWO different per-theme accent pulls ───────────────────────────┐
 │  the plate      dark ColorF(0x1C,0x1C,0x1E) | 0x1B,0x1B,0x1D | 0x14,0x14,0x16      ConcertUi.cs:164,  │
 │                 light Tok.FillCardSecondary                                        :233, :261, :355,  │
 │                                                                                    :914, :962         │
 │  the pull       Lerp(plate, accent, dark 0.30 / light 0.18)   the split hero        :262-263           │
 │                 Lerp(plate, accent, dark 0.50 / light 0.18)   the 192-DIP band      :356-360           │
 │  and the file states the rule this chapter exists to generalise, in its own words:                     │
 │    "It was 0.5 in BOTH, which is the usual light-arm mistake: half a saturated wire hue mixed into a    │
 │     near-black plate is a dark tint, and half of the same hue mixed into #F6F6F6 is a full-strength     │
 │     pastel band 192 DIP tall behind the title. Light pulls a third as far."               :356-359     │
 │  ⇒ THE CROSS-CUTTING RULE: a Lerp toward a plate needs its OWN light ratio, because the two plates are  │
 │    at opposite ends of the luminance range. One ratio in both arms is the default bug, not a shortcut.  │
 │    Note the hand-authored near-blacks are NOT tokens — three distinct ones, unnamed. 0.3 names them.    │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 10. THE ACCENT CARD FILLS — every rectangular media card in the app ─────────────────────────────────┐
 │  rest   Lerp(Tok.FillCardDefault,      accent, dark 0.12 / light 0.08)          MediaCard.cs:48-51    │
 │  hover  Lerp(Tok.FillControlSecondary, accent, dark 0.18 / light 0.12)                     :53-56    │
 │  The same two-ratio shape as the concert plate, at a quarter the strength, on the single most repeated │
 │  surface in the window. A collapsed ratio here is visible on every shelf at once.                      │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 11. THE NOW-PLAYING HERO WASH — the rail's own derived ground ───────────────────────────────────────┐
 │  Lerp(Tok.FillCardSecondary, Lift(Accent(SchemeFor(url))) ?? Tok.AccentDefault,                       │
 │       dark 0.18 / light 0.10)                                              NowPlayingPanel.cs:203-209 │
 │  Note the composition: Surfaces.SchemeFor (theme-following, W5 box 6) THEN WaveePalette.Lift THEN a    │
 │  per-theme lerp. Three derivations stacked — the longest chain in the app, and ch 21 owns the surface. │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 12. + 13. THE OTHER TWO ANSWERS TO "WHICH HALF OF THE GRADING" ──────────────────────────────────────┐
 │  NavPreview.SeedAccentArgb   TryGetScheme(url, Tok.Theme == ThemeKind.Light) → Accent    NavPreview:64 │
 │  SearchHero                  TryGetScheme(url, Tok.Theme == ThemeKind.Light) → ChromeAccent           │
 │                                                                                      SearchHero:39-41 │
 │  Both follow the THEME (SchemeFor's polarity), then one takes Accent and the other ChromeAccent — so   │
 │  the app has FIVE distinct answers to "which half", not the three W5 box 6 and §4.5 name:              │
 │     SchemeFor (theme) · ChromeSchemeFor (opposite) · StageInk.ArtStandIn (the stage's polarity) ·      │
 │     NavPreview (theme → Accent) · SearchHero (theme → ChromeAccent)                                    │
 │  The last two are not new POLARITIES — they are new ROLE picks off the same half — but a port that     │
 │  folds "which half" into one helper must keep the role argument or these two change colour silently.   │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘

 WHAT THIS MEANS FOR §4's FLOOR. None of the five carries ink of its own, so none takes a readability floor —
 but four of the five sit UNDER ink that is a token read (a card title, a concert title, a now-playing title),
 which is exactly the §4.4 check: the token must still clear its ratio over the surface's WORST pixel, and in
 light theme the worst pixel is now a pastel, not a near-black. Parity 114-117.
```

### W6 — the three surfaces that have NO light arm, and what each one does instead

```
 ┌ A. THE NINE DECK FACES — authored dark, and correct ─────────────────────────────────────────────────┐
 │  CanvasDeck · CassetteDeck · CdDeck · IpodDeck · RecordDeck · ReelDeck · VuDeck · WinampDeck ·        │
 │  WmpDeck. Grep for Tok.Theme / ThemeKind / StageInk across all nine: ZERO hits.                       │
 │  They are PHYSICAL MATERIALS — a cassette shell, a VU meter's glass, a CD's iridescence, an LCD.      │
 │  A cassette is not lighter in daylight. Authoring a "light cassette" is authoring a second product.   │
 │  WHAT MAKES IT SAFE: the faces live inside the now-playing hero, which the stage's own veil already   │
 │  owns. In light theme the hero is a dark object on a light page — the same relationship a photograph  │
 │  has to a page. That is the contract, and it is why this is not the bug it looks like.                │
 │  THE ONE THING THAT MUST FOLLOW THE THEME is the frame AROUND the face (the card's stroke, its        │
 │  shadow, the hero slot's corners) — ch 21 and ch 23 own those, and they are token reads.              │
 │  PARITY: item 25 checks that the faces are byte-identical in both themes, deliberately.               │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ B. LIKED'S GENERATED COVERS — dark-authored near-blacks, cover-graded ───────────────────────────────┐
 │  MarqueeBase #17171C · StackBase #1B1B20, each Lerp'd 40 % / 45 % toward the cover's base tint        │
 │  (LikedCoverLeaves.cs:93-107). "Ungraded ⇒ the plate alone. That IS the neutral fallback: a dark      │
 │  plate, immediately, that gains its chroma the moment the plane answers — never a white flash."       │
 │  These are ARTWORK, not chrome. A generated Liked cover sits in a cover slot beside real album art;   │
 │  real album art does not lighten in light theme either. No light arm, and none is wanted.             │
 │  THE ONE RISK a light-theme run must check: the cover's own INK (the "Liked Songs" wordmark over it)  │
 │  is on-media, not theme — if it ever became Tok.TextPrimary it would vanish. ch 07 owns that.         │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ C. VIDEO SCRIMS — theme-INVARIANT by token, and that is the correct kind of invariance ──────────────┐
 │  Tok.ScrimBottom / Tok.ScrimTop are `static readonly GradientSpec` on the token class, not palette    │
 │  entries (Tokens.cs:376-386) — one definition, no per-theme arm, by construction.                     │
 │  Used at: InWindowVideoPip.cs:272 (the hover-revealed top strip) and VideoFullscreenSurface.cs:316.   │
 │  The ink over them is ON-MEDIA, explicitly: "this sits on a dark scrim over video in BOTH themes, so  │
 │  a light-theme [text rung would be invisible]" (InWindowVideoPip.cs:243).                             │
 │  RULE: a scrim over VIDEO is invariant; a scrim over the app's own ground is not. The stage is the    │
 │  single surface that is both, which is why it — and only it — gets StageArm.                          │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘

 AND THE NINTH DERIVED SURFACE, which IS a token read and therefore needs no arm:
   SEARCH HIGHLIGHT — Tok.AccentSelectedTextBackground behind the matched run, Tok.TextOnAccentSelectedText
   on it, padding 3/1, Radii.Control (SearchHighlight.cs:44-52). Both tokens are palette entries with real
   light and dark arms, and AccentSelectedTextBackground is #0078D4 in BOTH (PaletteBuilder.cs:253, :341) —
   so the pill is the same blue on both grounds and the ink on it is the inverse text rung. Nothing to add.
   It is in this chapter's list only because the completeness critic put it there; the finding is that it
   does NOT belong with the other eight.
```

### W7 — the page-swap TIMELINE: three channels, three clocks (1 char ≈ 10 ms)

```
  t(ms)  0    50   100  150  200  250  300
         ├────┼────┼────┼────┼────┼────┤
 CHANNEL 1 — the CONTENT CARD (PageNavMotion, ContentHost's KeepAlive boundary)
   exit   ████████████░                              120 ms, Easing.EaseOut, Opacity 1 → 0, Dx 0
          └ ~94 % gone at t=90 by construction        PageNavMotion.cs:52-59, :47-48
   enter            ░░████████████████               starts t=90 (DelayMs), Expressive.Fast,
                    ↑ DelayMs 90                      Easing.SmoothOut, Dx = ±Expressive.DistBase,
                                                      Opacity 0 → 1                       :56-58
   WHY the overlap is nearly zero: "an accelerate curve holds the old page near 1 until the end, which
   is exactly the superimposed-text frame" (:33-36). Exit.Active stays TRUE the whole time — with a
   stripped Exit the outgoing page detaches in the same frame and the card flashes EMPTY (:29-32).

 CHANNEL 2 — the MASTHEAD BAND (one node, mounted ONCE as an overlay on the same boundary)
   family→family     (no fade at all — the TITLE snaps with the route; the band never re-mounts)
   family→other      ████████████░                   Opacity 1 → 0 over FadeThroughExitMs = 120 ms,
                                                     Easing.FluentAccelerate    ShellMastheadBand.cs:23-24
   other→family      ████████████░                   Opacity 0 → 1 over the same 120 ms,
                                                     Easing.SmoothOut                          :25-26
   Both keep ReducedMotionPolicy.KeepFade — an opacity fade survives reduced motion.
   It NEVER collapses to Height 0: "unknown families keep the last trail and fade opacity" (:20),
   which is why crossing out of the family does not change KeepAlive height mid-swap.
   THE ONE EXCEPTION, and it is the COLD-START arm: until a family has been resolved ONCE, _heldTitle is
   null and Render returns `new BoxEl { MinWidth = 0f, Opacity = 0f, HitTestVisible = false }` — a
   genuinely zero-size node, not a faded masthead (:56-57). A launch that never reaches Browse, a section
   drill or Concerts therefore has NO band at all, and the FIRST crossing into a family is the one
   transition that does change the column's height. After that the trail is sticky for the process.
   W24's t=0 frame is that arm; parity 118 is the check.
   THE FIVE FAMILIES are ShellMastheadRegistry's, not this file's: browse home · browse: · browse-section: ·
   home-section: · Concerts (:17-18). Concerts is a family and W8 draws no concert pair — that is a gap in
   W8's seven walks, not in the mechanism.

 CHANNEL 3 — the SHELL MATERIAL (the layer under ALL chrome: title bar, toolbar, sidebar, dock)
   tint→tint   ░░░░████████████████████████████████  BrushTransitionMs = WaveeMotion.Standard = 250 ms
               ↑ starts when the NEW page CLAIMS      ShellMaterialLayer.cs:96-98
                 (its first UseEffect after mount)
   wash→tint   the wash layers EXIT and the tint layer (always mounted) fades to the new colour
   wash→wash   each layer is KEYED on its artwork ⇒ a re-grade EXITS the old node and ENTERS the new one
               over the same pixels (Enter/Exit = WashFade, Opacity 0, Active)          :34, :129-130
   → the chrome is STILL MOVING for ~100 ms after the content card has finished. That is the design.
```

### W8 — the pair matrix: the seven walks users actually take

Read each row as: what the **card** does · what the **band** does · what the **material** does. Every cell is derived
from `ContentHost.PageFor` (`:188-309`), the family predicate (`ShellMastheadRegistry`) and who publishes a material.

```
 ┌ 1. HOME → ALBUM  (a Home card click; Forward) ───────────────────────────────────────────────────────┐
 │ card      fade-through FORWARD: exit 120 accel, enter +DistBase from 90 ms                            │
 │ band      Home is NOT a family and album is NOT a family ⇒ NO band at either end. Nothing animates.    │
 │ material  WASH → TINT. Home owns three radial layers; the album's CoverShellTintBinder CLAIMS on its   │
 │           first publish. If the album's cover is not graded yet the claim is NOT "definite", so        │
 │           ShellTintOwnership returns WriteHeldColor: Home's wash is HELD, owner changes, and the       │
 │           chrome does not dip to neutral and back (ShellMaterial.cs:39-58). When the grading lands,    │
 │           the refresh is a WriteKnownColor and the three wash layers EXIT while the tint fades in.     │
 │ THE FRAME TO CHECK  t≈150: the card already shows the album's hero while the chrome still carries      │
 │           HOME's wash. That is correct and it is the single most surprising frame in the app.          │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 2. BROWSE → PLAYLIST  (a browse-category tile; Forward) ─────────────────────────────────────────────┐
 │ card      fade-through FORWARD                                                                        │
 │ band      family → NOT family ⇒ the band fades OUT over 120 ms while holding "Browse › Pop" (it does   │
 │           not blank first). Body content beneath slides normally — the band is an overlay, not a       │
 │           sibling, so it never consumed the card's height (ContentHost.cs:111-118, :133-137).          │
 │ material  browse: does NOT claim the material; browse-section: DOES (ch 18 audit 17). So from a        │
 │           CATEGORY page the slot's owner is still whatever claimed last, and the playlist's claim is   │
 │           an ordinary hand-over. From the DIRECTORY it is the same.                                    │
 │ THE FRAME TO CHECK  t≈60: the band is at ~50 % opacity over a card that is ~half faded. Two things     │
 │           dissolving at once, at the same rate, is why both use FadeThroughExitMs.                     │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 3. ALBUM → ARTIST  (the hero's artist link; Forward) ────────────────────────────────────────────────┐
 │ card      fade-through FORWARD. Both pages are full-bleed detail frames with a hero at the top, so     │
 │           this is the pair the fade-through exists for: a symmetric slide would show two heroes.       │
 │ band      neither is a family. Nothing.                                                               │
 │ material  TINT → TINT, and BOTH are art-derived. The artist page publishes from its OWN cover, so the  │
 │           250 ms ramp is colour→colour, never colour→neutral→colour. If the artist's grading has not   │
 │           landed, WriteHeldColor keeps the ALBUM's tint — the chrome stays warm through the swap.      │
 │ ALSO      the artist page adds its own CoverArtistBlendWash inside the card (a page-local gradient,    │
 │           not the shell material) — two art-derived colours on screen at once, from two schemes.       │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 4. DISCO → ARTIST  (the discography's back affordance; usually BACK) ────────────────────────────────┐
 │ card      fade-through BACK: enter from −DistBase. The ONLY difference from Forward is the sign.       │
 │ band      neither is a family. Nothing.                                                               │
 │ material  the artist page is a KeepAlive slot that was parked, so its binder's UseActivation fires     │
 │           onActivated and CLAIMS — "always a claim, regardless of claimedOnce" (CoverPaletteLeaves     │
 │           .cs:259-261). The disco page's exit tail cannot paint over it, because a refresh only lands  │
 │           while its page still owns the slot.                                                         │
 │ THE BUG THIS PREVENTS  without the reactivation claim, a Back to a parked page leaves the chrome       │
 │           carrying the page you just LEFT, for the rest of the session.                               │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 5. SETTINGS → HOME  (the sidebar; a fresh Forward, or Back) ─────────────────────────────────────────┐
 │ card      fade-through in whichever direction the shell wrote before the route (Peek, not a read —     │
 │           a motion-only write must never re-run the boundary thunk, ContentHost.cs:93-95).             │
 │ band      neither is a family. Nothing.                                                               │
 │ material  Settings publishes NO material and does not claim — but the BOUNDARY does: ContentHost's     │
 │           own effect claims NEUTRAL for every route no page publishes for (`ContentHost.cs:51-55`,      │
 │           `:184-186`), definite: true, so the chrome EASES to NeutralGround rather than keeping         │
 │           Settings' predecessor's colour. (An earlier draft said "whatever was showing is STILL          │
 │           showing" — that is the pre-boundary behaviour and is wrong for 0.2.9.) Home then claims with  │
 │           its three washes: NEUTRAL → WASH, so the tint layer is already neutral while three keyed wash │
 │           layers ENTER. This is the only pair where the material gains layers rather than changing a    │
 │           colour — and it is TWO 250 ms ramps back to back, not one.                                    │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 5b. HOME → RECENTS, or HOME → a "see all" section (the one-leg wash pair) ───────────────────────────┐
 │ card      fade-through FORWARD                                                                        │
 │ band      home-section: / browse-section: ARE families ⇒ the band fades IN over 120 ms; recents is NOT │
 │           a family ⇒ nothing. Two destinations that look alike, two different band behaviours.          │
 │ material  WASH(3) → WASH(1). Hero keeps its layer key only if the artwork matches (it will not), so     │
 │           Hero remounts and Weekly + Mix EXIT with nothing entering behind them. The window goes from   │
 │           lit at three corners to lit at one, over the engine's EnterExit opacity — NOT the 250 ms      │
 │           brush ramp, which only ever runs on the flat tint layer.                                      │
 │ THE FRAME TO CHECK  mid-swap the top-left wash is cross-fading while the other two are simply leaving.  │
 │           Three layers on three independent tracks is the shape; parity 119.                            │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 6. MODULE → ANYTHING  (or anything → module) ────────────────────────────────────────────────────────┐
 │ card      NOT the fade-through. PageSlideSafe*: TranslateX only, symmetric (enter +DistBase, exit      │
 │           −DistBase), same dynamics both halves, Exit.Active still TRUE   PageNavMotion.cs:105-121     │
 │           Neutral has NO video-safe form ⇒ a hard CUT (RecipeForVideoSafe returns null, :99)           │
 │ band      as the families dictate; the band is opacity-only and is NOT video-safe — but it is a        │
 │           SIBLING overlay, not an ancestor of the video, so its opacity never multiplies the hole.     │
 │ material  as normal; the material layer is BELOW the content pane, never an ancestor of the video.     │
 │ THE HONEST DEGRADATION, stated in the source: "a module-page swap SLIDES instead of cross-fading. Two  │
 │           full-bleed pages therefore share the card at full opacity for the length of the travel,      │
 │           which is the double-exposure fade-through was introduced to shrink — and it is still the     │
 │           better trade, because the alternative is a video that disappears mid-navigation." (:85-89)   │
 │ BOTH SIDES are classified because the outgoing root is attached and DRAWING for its whole exit (:79-82)│
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ┌ 7. BACK TO A PARKED PAGE  (any pair, via Back / the tab strip) ──────────────────────────────────────┐
 │ card      fade-through BACK. The slot is REACTIVATED, not rebuilt — MaxEntries 3 keeps the live page   │
 │           plus a two-deep back stack (ContentHost.cs:100-108).                                         │
 │ layout    SuppressLayoutTransitionsOnActivation: true (:108) — the returning page RE-LAYS-OUT with no  │
 │           transition. A parked page that was resized or re-zoomed while away must not animate its way  │
 │           back to the right shape.                                                                    │
 │ band      the store is route-keyed and bounded, so the returning route's masthead is still there and   │
 │           resolves live immediately (ShellMasthead.cs:14-47) — the band does not fade out and back in. │
 │ material  the reactivation CLAIM (case 4 above).                                                      │
 │ scroll    the slot's scroll offset is preserved by the boundary; nothing in this chapter touches it.   │
 │ THE THING THAT IS NOT PRESERVED  keyboard focus (W3, path 6).                                          │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘

  A TAB'S FIRST PAGE (a fresh tab opened straight onto a destination) asks too: the old token is then
  KeepAliveOptions.FirstActivation rather than a PageSlot, so it takes the ordinary recipe for its
  direction (ContentHost.cs:146-149). It is not a special case and must not become one.

  AN UNKNOWN ROUTE lands on the not-found page (ch 18 W21) through the same boundary with the same
  recipe. Nothing about "we cannot render this" changes the motion.
```

### W9 — the network state as ONE composed picture @ 1600×900 (1 char ≈ 16 DIP)

Three layers, three owners, and 0.2.9 never draws them together. This is what "offline" looks like.

```
 ── A. LIVE (the reference frame) ────────────────────────────────────────────────────────────────────
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋   [ 🔎 Search... ]              ◉ Christos  🔔 👥 📌 ⚙│  ⌄  ─ □ ✕   │
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤
│  sidebar     │  the page                                                                            │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤ 72
│  ◀◀  ▶  ▶▶   Weird Fishes · Radiohead    ━━━━●────────   🔊 ━━━━●──   📺 ♥ ⋯                       │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘

 ── B. RECONNECTING (playback's recovery; the session is still authenticated) ────────────────────────
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋   [ 🔎 Search... ]              ◉ Christos  🔔 👥 📌 ⚙│  ⌄  ─ □ ✕   │ ← chip UNCHANGED
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤   (AuthState = Live)
│  sidebar     │  the page is unchanged — it still shows what it had                                  │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤
│▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│ ← the INDETERMINATE
│  ◀◀  ▶  ▶▶   Reconnecting…                ━━━━●────────   🔊 ━━━━●──   📺 ♥ ⋯                       │   TOP EDGE, sweeping
└────────────────────────────────────────────────────────────────────────────────────────────────────┘   PlayerBar.cs:627
   PlayerState.Reconnecting ⇐ b.RecoveryKind.Value == PlaybackRecoveryKind.Network   PlayerBar.cs:171
   the title is Strings.Player.Reconnecting in Tok.TextSecondary                            :736
   canTransport STAYS TRUE (active || buffering || reconnecting, :174) — the buttons do not go dead
   the scrub bar keeps its position; it does not reset

 ── C. OFFLINE (the silent resume failed; a credential is still on disk) ─────────────────────────────
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋   [ 🔎 Search... ]           [ Reconnect ] 🔔 👥 📌 ⚙│  ⌄  ─ □ ✕   │ ← the profile chip is
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤   REPLACED by an ACCENT
│  sidebar     │   ┌──────────────────────────────────────────────────────────────────┐              │   button: Button.Accent
│  (its rows   │   │  ⓘ  You're offline — showing what's already here.      [ Retry ] │              │   MergedChromeRow.cs:216
│   are cached │   └──────────────────────────────────────────────────────────────────┘              │
│   and stay)  │   the page KEEPS its cached content beneath the banner                               │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤
│  ◀◀  ▶  ▶▶   Nothing playing              ──────────────  🔊 ━━━━●──   ♥ ⋯                         │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
   ShellAuthState.Offline ⇐ a resume did not complete but a stored credential exists  PlaybackBridge.cs:71-73
   "the verb is 'try again', not 'sign in' — the account is not in question"          MergedChromeRow.cs:213-215
   the BANNER is Components/OfflineBanner.cs — Tok.SystemFillCautionBackground, an InfoBar circle glyph
   in Tok.SystemFillCaution, TrackMeta text, a Button.Standard Retry, Radii.Control    OfflineBanner.cs:12-28

 ── D. METERED (connected, but the OS says the link costs money) ─────────────────────────────────────
   NOTHING in the chrome. NOTHING in the player bar. Two invisible effects and ONE settings line:
     · streaming quality      min(userQuality, meteredCap)          NetworkPolicy.cs:95-105
     · protected-video height EffectiveVideoMaxHeight (0 = unlimited)              :119-125
     · prefetch / CDN warm    deferred silently (ShouldDeferPrefetch)              :50
     · Settings ▸ Playback    the status line distinguishes "unrestricted" from "the probe failed"
                              (App/MeteredStatusLine.cs; the Cost signal carries kind + limit/roaming bits)
   THE 0.3 DECISION: keep it invisible. A persistent "metered" chip would be a permanent nag for a
   condition the user chose. The ONE visible consequence is that covers stop appearing ahead of the
   scroll — which reads as slowness, not as policy, and is why the Settings line exists.

   THE PORT TRAP §8 DOES NOT STATE. EffectiveQuality(int, int) and EffectiveVideoMaxHeight LOOK pure and
   are listed as CORE — but both read the file's mutable static `_cost` (NetworkPolicy.cs:104, :122), so
   neither is testable without installing the policy. The 0.3 CORE split must take the cost as a PARAMETER
   (`EffectiveQuality(user, cap, in NetworkCost)`); leaving the static read in place means the "add a test,
   it is four lines" row in §8 cannot be honoured. Same for ShouldDeferPrefetch (:50) and IsMetered (:39).
   Unlimited is spelled int.MaxValue on the way OUT and 0 on the way IN: a stored cap of 0 means unlimited,
   and the function returns int.MaxValue for it (:122-124). Two spellings of one idea, in one function.
```

### W10 — the composition rules, as a decision table

```
 AuthState   Recovery   Cost      chrome chip      player bar            page body
 ─────────────────────────────────────────────────────────────────────────────────────────────────────
 Live        None       Un/Unk    profile menu     normal                normal
 Live        None       Metered   profile menu     normal                normal (covers arrive later)
 Live        Network    any       profile menu     Reconnecting +        UNCHANGED — keep what you had
                                                   indeterminate edge
 Connecting  any        any       "Connecting…"    as the transport is   normal / skeleton
                                  Caption.Secondary
                                  MergedChromeRow.cs:208-212
 Offline     any        any       [ Reconnect ]    "Nothing playing" or  the OfflineBanner ABOVE the
                                  accent button    the last title        page's own content, cached
 SignInRequired  —      —         [ Sign in ]      resting               (unreachable from the shell on
                                  MergedChromeRow.cs:216-219             the real backend — the wizard is
                                                                         the whole window there; only the
                                                                         fake/demo backend reaches it :215)

 THE THREE RULES THAT MAKE IT LEGIBLE
  1. AT MOST ONE OF THE THREE LAYERS SHOUTS. Offline puts an accent button in the chrome ⇒ the page's
     banner is Caution-tinted and its action is Button.Standard, never Accent. Reconnecting puts a
     sweeping bar on the player ⇒ the chrome does not change at all. Two accents for one condition reads
     as two conditions.
  2. NOTHING UN-RENDERS. An offline page keeps its cached content and gains a banner — the same rule
     PlaylistPageNoticeRules already states for a vanished playlist: "a notice never un-renders the page
     … blanking it to a skeleton or an error state loses their place and tells them nothing"
     (PlaylistPageNoticeRules.cs:32-34). Offline is the same shape of fact.
  0. THE GRAMMAR HAS TWO SCALES, AND BOTH VOICES CARRY BOTH. EmptyState.Build / ErrorState.Build are the
     PAGE scale (WaveeType.PageHero, 28/36/600); EmptyState.Compact / ErrorState.Compact are the SECTION
     scale (Ui.Subtitle, 20/28/600) for anything narrower than ~340 DIP — the queue rail, the friends rail,
     the now-playing panel, a Browse band, a Search facet body (EmptyState.cs:34-45, ErrorState.cs:17-43).
     "28/36 wraps to three ragged lines at 240 DIP, which is not big type, it is a paragraph" (:40-42).
     Everything below the headline is identical in both, so a rail's vacancy and a page's read as the same
     sentence at two volumes. The 0.3 `Vacancy` grammar of §1.2 is therefore FOUR voices × TWO scales, not
     four builders — and the offline voice needs both, because the queue rail goes offline too.

  3. "THE ANSWER WAS NOTHING" AND "THE QUESTION NEVER ARRIVED" ARE DRAWN DIFFERENTLY. An empty library
     is EmptyState (a display-face headline, a caption, one quiet action). A library we could not reach
     is the page's LAST GOOD CONTENT plus the banner — and if there is no last good content, ErrorState
     with the offline copy, NOT the empty copy. §7.2 is the per-surface table.

 WHAT 0.2.9 DOES NOT DO, and 0.3 must (each is a parity item, expected to FAIL against 0.2.9):
  · OfflineBanner has ZERO call sites under Features/. Nothing ever shows it.            parity 46
  · No page distinguishes a network failure from a missing entity: ErrorState.Build(ex) prints the same
    "Something went wrong" for both (ErrorState.cs:22-26).                                parity 47
  · The chrome chip and the player bar never consult each other, so an Offline chip can sit above a bar
    that is confidently showing a track (the bar reads the PLAYER, the chip reads AUTH).  parity 53
```

### W11 — the toast card and the severity ramp (1 char ≈ 8 DIP)

```
  ┌──────────────────────────────────────────────────────┐ ← the strip docks BottomRight, 24 DIP from the
  │ ⊕ │ Added to Dalkom Cafe               [Go to] [✕] │   screen edge + Toast.EdgeInset (the player-bar
  └──────────────────────────────────────────────────────┘   height, so toasts float above the dock)
    ↑    ↑                                  ↑        ↑                            Toast.cs:84-88, :53-64
    │    │                                  │        └ Closable (default true)              :36
    │    │                                  └ ActionLabel + OnAction                        :35
    │    └ the message — the FIRST argument of Toast.Show                                    :93
    └ SeverityVisual.Glyph on SeverityVisual.IconBackground, in IconForeground
      the card's ground is SeverityVisual.Background                       SeverityVisuals.cs:19-26

  SEVERITY          glyph            icon plate               card ground
  Informational     StatusInfo       Tok.SystemFillAttention  Tok.SystemFillAttentionBackground  (the OS accent)
  Success           StatusSuccess    SystemFillSuccess        SystemFillSuccessBackground
  Warning           StatusWarning    SystemFillCaution        SystemFillCautionBackground
  Error             StatusError      SystemFillCritical       SystemFillCriticalBackground
  IconForeground is ALWAYS Tok.TextInverse. One table, shared with InfoBar, "so their severity mapping
  CANNOT drift" (SeverityVisuals.cs:5-9) — ch 19 owns the strip's geometry and stacking; this chapter
  owns what goes in it.

  DURATION   default 5 000 ms (Toast.cs:33) · 0 = sticky, user-dismissed only
  STACKING   MaxVisible = 3, the rest wait in a FIFO overflow queue                        :82
  DEDUPE     effective key = DedupeKey ?? message. A match REFRESHES the existing card: the countdown
             restarts "so the user gets the full read time from now" and the newer action is adopted
             (:38-46, :169-193). A second Show never stacks a second card.
  A CUSTOM BODY (CustomContent) replaces the severity/title/message card entirely inside the same
  shadowed, tinted frame (:37-39) — used only by the update toast's progress bar.
```

### W12 — a bound action's four visual states (the extension platform's row)

```
  ┌ ENABLED ────────────────────────────────────────────────┐  Fill transparent · HoverFill FillSubtleSecondary
  │ ♪  Play                                                 │  PressedFill FillSubtleTertiary · 48 DIP tall
  │    This playlist                                        │  label 13/600 TextPrimary · sub 11 TextTertiary
  └─────────────────────────────────────────────────────────┘  SidebarItemPickers.cs:411-445
  ┌ SELECTED ───────────────────────────────────────────────┐  WaveeColors.SelectedRest / Hover / Pressed —
  │ ♪  Play                                          ✓      │  the app's standard 4-state selection ramp, set
  │    This playlist                                        │  EXPLICITLY (".Interactive(...) would overwrite
  └─────────────────────────────────────────────────────────┘  all three fills and erase the selected state")
     the selection MARK is added; the action's ICON is never replaced by a radio bullet — doing that
     "hid the one thing identifying the row (round-2 defect 6c)"                          :445-448
  ┌ CHECKED (a toggle descriptor: IsChecked != null) ───────┐  Icon(isChecked: true) picks the FILLED variant
  │ ♥  Save to Liked Songs                          [on]    │  WaveeActionDescriptor.cs:55-57, :94
  └─────────────────────────────────────────────────────────┘
  ┌ VISIBLE BUT DISABLED — the platform's rule ─────────────┐
  │ ♪  Play                                                 │
  │    This playlist                                        │
  │ ⚠ Nothing is playing right now                          │ ← the REASON, 11 DIP TextTertiary, wrapped to
  └─────────────────────────────────────────────────────────┘   2 lines, behind a 12-DIP StatusWarning glyph
     "an unavailable target is still a LEGAL binding, so this explains rather than blocks"  :525-552

  THE SEVEN REASONS, each with a loc key (WaveeActionTargeting.cs:39-58, :140-146)
    ModeNotSupported  sidebar.action.unavailable.mode         a doc from a newer build, or a narrowed extension
    MissingTargetKey  sidebar.action.unavailable.noTarget     a FixedEntity/FixedTrack binding with no key
    NoNowPlaying      sidebar.action.unavailable.noNowPlaying a NowPlaying binding while nothing plays
    NoActiveRoute     sidebar.action.unavailable.noRoute      an ActiveRoute binding with no resolvable page
    ActionMissing     sidebar.action.unavailable.missing      the extension was removed or disabled
    HostUnavailable   sidebar.action.unavailable.host         a needed service is absent — INCLUDING the
                                                              deliberate refusal to run a confirm-required
                                                              action with no overlay to confirm in
    NotApplicable     sidebar.action.unavailable.notNow       the descriptor's own IsEnabled said no
  The keys are LITERALS, not generated Strings.* members, on purpose: "a missing key renders loudly as
  '[key]' by design, which is exactly the signal we want if the [localization] wave is skipped" (:137-139).

  THE CONFIRMATION GATE, drawn
    Execute → Resolve(peek: true) → if (!RequiresConfirmation) Run                WaveeActionDescriptor.cs:126-131
            → else if (Overlay is null) return HostUnavailable  ← unreachable; Resolve already refused (:109)
            → else SettingsShared.Confirm(overlay, Loc(ConfirmTitleLocKey ?? LabelLocKey), …)      :139-143
    Copy falls back down a chain: title → LabelLocKey; body → title; primary → LabelLocKey  (:69-73)
    A DESTRUCTIVE descriptor (Destructive = true) is carried FOR PRESENTATION — "a future red-text row" —
    and is explicitly NOT the safety gate; RequiresConfirmation is (:59-61). Do not conflate them.

  BuiltInExtensionTable's DELIBERATE EXCLUSIONS (BuiltInExtensionTable.cs:16-27) — thirteen keys are
  registered and these are refused, each with its reason:
    AddToPlaylist · AddToDefaultPlaylist · RemoveFromThisPlaylist · RemoveFromQueue · SelectAll
        need a live selection, a playlist HOST with resolved row ids, or a queue row identity — none of
        which survives a restart, "so a stored binding could not honestly re-target them"
    ViewCredits           needs a resolved Track with a primary-artist uri (the fetch keys off both)
    Rename · TogglePlaylistPublic · InviteCollaborators · DeletePlaylist
        owner-only playlist MANAGEMENT. "A one-click sidebar shortcut is the wrong affordance for them
        (delete especially); they stay context-menu-only. The descriptor's confirmation gate exists for
        the day one of them is bound anyway."
    the Video ▸ verbs     open file pickers over a local-curation service that may not exist
  And the rule that makes the whole platform safe: "Every descriptor delegates to the SAME code path the
  context menu takes … never a second implementation — a binding and a right-click must never be able to
  disagree" (:28-30).
```

### W13 — the four readiness states, drawn side by side (1 char ≈ 16 DIP, a page body)

```
 1. IN FLIGHT — SKELETON                       2. THE ANSWER WAS NOTHING — EMPTY
 ┌──────────────────────────────────────┐      ┌──────────────────────────────────────┐
 │ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │      │                                      │
 │ ░░░░░░░░░  ░░░░░░░░  ░░░░░░░░        │      │        Nothing here yet              │ ← WaveeType.PageHero
 │ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │      │        28 / 36 / 600, display face   │
 │ ░░░░░░░░░  ░░░░░░░░  ░░░░░░░░        │      │                                      │
 │                                      │      │   Save an album and it shows up here │ ← TrackMeta caption
 │  NO SCROLL RAIL — LoadingBarSuppressors│      │                                      │
 │  > 0 skips the emit  SceneRecorder:3402│      │        [  Browse  ]                  │ ← Button.STANDARD,
 └──────────────────────────────────────┘      └──────────────────────────────────────┘   never Accent
   the shimmer is DERIVED from the loaded            EmptyState.cs:11-22 — three parts, in this order,
   silhouette, at the SAME sizes, from the           and nothing else. NO GLYPH (the parameter was
   SAME pure resolver (DetailSkeleton.cs:17-20)      removed, so a stale call site is a compile error)

 3. THE REQUEST FAILED — ERROR                 4. THERE IS NO NETWORK — OFFLINE  ← 0.3 ADDS THIS
 ┌──────────────────────────────────────┐      ┌──────────────────────────────────────┐
 │                                      │      │ ┌──────────────────────────────────┐ │
 │      Something went wrong            │      │ │ ⓘ You're offline — showing what's│ │ ← the BANNER sits ABOVE
 │                                      │      │ │   already here.        [ Retry ] │ │   whatever the page had
 │   We couldn't load this right now.   │      │ └──────────────────────────────────┘ │
 │                                      │      │  1  Weird Fishes   Radiohead   4:21  │ ← CACHED CONTENT STAYS
 │      [  Retry  ]                     │      │  2  Reckoner       Radiohead   4:50  │
 └──────────────────────────────────────┘      └──────────────────────────────────────┘
   ErrorState IS EmptyState with a reason:           and when there is NO cached content, the offline
   same grammar, same rungs, same quiet action       arm of state 3 — the ErrorState grammar with the
   (ErrorState.cs:8-14). The 32-DIP critical         OFFLINE copy. Never the EMPTY copy: "we could not
   glyph and the accent Retry are GONE — "a red      ask" must never be drawn as "there is nothing".
   pictogram over 'Something went wrong' states
   the same thing twice"                             The technical detail goes to the LOG, never the
                                                     user (ErrorState.cs:19-20, and it logs on EVERY
                                                     build — a Warning "ui" line per shown error).

 AND THE AXIS W13 LEFT OUT — every one of states 2, 3 and 4 has TWO SCALES, not one:
   PAGE     Build    — WaveeType.PageHero 28/36/600 · for a page body, a shelf region, a dialog
   SECTION  Compact  — Ui.Subtitle 20/28/600 · for anything narrower than ~340 DIP: the queue rail, the
                       friends rail, the now-playing panel, a Browse band, a Search facet body
   EmptyState.cs:34-45 · ErrorState.cs:17-43 (Build) / :33-43 (Compact). The reason is stated at :40-42:
   "28/36 wraps to three ragged lines at 240 DIP, which is not big type, it is a paragraph."
   Everything below the headline is byte-identical between the two, which is the point.
   ⇒ §1.2's ONE `Vacancy` grammar is FOUR VOICES × TWO SCALES = eight call shapes, from one builder with
     two parameters — NOT four builders. The OFFLINE voice needs the Compact scale too: the queue rail and
     the friends rail go offline exactly as a page does, and today neither can say so.
   ⇒ The SKELETON (state 1) has no scale axis — it is derived per surface from that surface's own loaded
     silhouette (DetailSkeleton.cs:17-20), so there is nothing to pick.
```

### W14 — the shared-element morph pair, end to end (the dormant contract)

```
 THE KEY CONVENTION — one function, and the key IS the route key
   MorphKeys.For(DetailKind.Album,    id)  →  "album:" + id       MorphKeys.cs:8-16
   MorphKeys.For(DetailKind.Playlist, id)  →  "pl:"    + id
   MorphKeys.For(anything else,       id)  →  null   ← liked has no uri; artist covers (circular) deferred
   "already unique and present on both the card and the detail page, so source and dest agree with ZERO
    extra plumbing. One place for the convention so the two sides never drift."          :3-6

 THE FIVE LIVE PLUMBING SITES
   1  MediaCard.Props.MorphKey                       Components/MediaCard.cs:1111
   2  MediaCard → ImageEl { MorphId = p.MorphKey }    :1143   ← the SOURCE half actually tags a node
   3  LikedSongsArtwork.For(..., p.MorphKey)          :1132   (the Liked cover's own tagged element)
   4  DetailConfig.MorphKey { get; init; }            Features/Detail/DetailConfig.cs:86
   5  DetailRail / DetailVerticalHero read m.MorphKey DetailRail.cs:102-104, :159, :320, :421 ·
                                                      DetailVerticalHero.cs:102 (and both LOG it —
                                                      WaveeLogField.Of("morphKey", …), :142, :393)
   6  RecentsPage mints one through the ONE convention RecentsPage.cs:1707

 THE DELIBERATELY-NULL DESTINATION
   DetailShell.cs:235-257 — "MorphKey stays NULL, and it is not vestigial — investigated 2026-08-12,
   left deliberately", with the finding recorded in full at :235-256 and cross-referenced from
   RecentsPage.cs:1698-1702: "Nothing flies today, and the missing half is NOT DetailShell's
   `MorphKey = null` — it is the forward [half] … This stays the source half of the pair, minted through
   the ONE shared convention (MorphKeys.For) so the two sides cannot drift while it is dormant."

 WHAT THE ENGINE DOES WITH A TAGGED NODE
   AppHost.FirstMorphKey / CollectMorphKeys expose the live tag set   AppHost.cs:1790-1792
   Reconciler tracks _morphKeyByNode, removes a stale key when a node's key changes or the node dies
                                                                      Reconciler.cs:4147, :4594, :4635-4643

 WHAT THE NAV PROBE ASSERTS ON IT — this is why it is not dead code
   WaveeNavProbe.cs:747     collect the live keys after a Home mount
   :1635                    collect them again for the live-lyrics route build
   :2575                    "a REAL home-card key → the morph actually FLIES" — the probe picks a live
                            key rather than a synthetic one, so the assertion exercises the real pair
   :2682, :2758-2783        collect before and after a home↔detail bounce and compare
   A re-author who deletes MorphKeys because "nothing uses it" breaks the probe, not the UI — which is
   exactly the kind of breakage that gets the probe deleted next.

 THE 0.3 CONTRACT
   · Keep the convention as ONE function in Platform/Design.cs. It is 17 lines and it is the only thing
     standing between two independently-invented key formats.
   · Keep the source half tagging (MediaCard, RecentsPage). Tagging costs a string on a node that
     already has one.
   · The destination half stays null until someone does the forward work, and the comment explaining
     WHY moves with it verbatim. A null with a reason is documentation; a null without one is rot.
   · The probe's assertions move to Diagnostics.Probe.cs unchanged.
```

### W15 — long strings: the four fixed picker widths and the estimator (1 char ≈ 8 DIP)

```
 THE FOUR FIXED WIDTHS — a long translation ELLIPSIZES inside them; the row never widens
   Zoom            160   SettingsPage.Appearance.cs:344     ┌ Settings ▸ Appearance ▸ Zoom ─────────────┐
   NPV style       180   SettingsPage.Appearance.cs:402     │ Zoom                  [ Auto (150%)    ▾ ]│ 160
   Language        260   SettingsPage.General.cs:85         │ Scale the whole app,                      │
   GPU adapter     300   SettingsPage.General.cs:227        │ like Ctrl + / − in a browser               │
 (plus, outside Settings: History sort 160 · Logs session   └───────────────────────────────────────────┘
  320 / category 180 / two levels 132 · Notifications hour
  120 · EQ preset 200 · Playback quality ×4 at 280 ·
  Storage budgets ×2 at 120)

 ┌ the failure mode these widths PREVENT ───────────────────────────────────────────────────────────────┐
 │ "A picker in an expander HEADER starves the header text track to zero and paints over it — the        │
 │  documented Sidebar-design bug; every picker lives in the expander BODY."  SettingsPage.Appearance:16-21│
 │ So the rule is not "pickers are 160 wide"; it is "a picker never competes with a text track for        │
 │  width". The fixed width is how that is enforced.                                                     │
 └──────────────────────────────────────────────────────────────────────────────────────────────────────┘

 THE CONTEXT BAND'S ESTIMATOR — the one place in the app that predicts text width
   EstimateLabelWidth(len, padX) = max(0, len) × AvgCharW + 2 × padX      ContextBandLayout.cs:78-80
   AvgCharW = 7.6 DIP at 14 px / weight 600, Segoe UI Variable Text                     :76
   "Deliberately on the generous side of the real average (~6.9 for mixed-case Latin), so command
    clusters reserve enough room instead of clipping a localized label. Non-Latin scripts run wider per
    glyph but shorter per word, and the two errors cancel in the band's favour."         :72-75
   ActionsWidth(spans) = Σ widths + (n−1) × ActionGap                                    :86-93
   TitleCap = 280 — "past it the surplus goes to the pivot, which is the affordance that actually does
   something with more room"                                                             :67-69
   NOTHING in the band drops at a breakpoint: the title never drops, the actions never drop, the pivot
   lane scrolls behind an alpha edge fade.

   ┌ EN  "Add to playlist"  15 ch × 7.6 + 20 = 134 DIP ────────────────────────────────────────────────┐
   │ [ Album title …            ]   Add to playlist  ·  Sort  ·  Filter        [ 🔎 ]                   │
   └──────────────────────────────────────────────────────────────────────────────────────────────────┘
   ┌ DE  "Zur Wiedergabeliste hinzufügen"  30 ch × 7.6 + 20 = 248 DIP ────────────────────────────────┐
   │ [ Album title… ]   Zur Wiedergabeliste hinzufügen · Sortieren · Filtern   [ 🔎 ]                  │
   └──────────────────────────────────────────────────────────────────────────────────────────────────┘
     the TITLE gives way (it is capped at 280 and shrinks first); the actions keep their room because
     the estimate reserved it. That is the whole mechanism.

 MARQUEE vs CharacterEllipsis — which overflow treatment a surface takes
   MARQUEE, and only on HOVER: the player bar's now-playing title (PingPong, Speed 18 dip/s, CycleMs
   10 000, EndPause 2 500, edge fade 24 clamped to 30 % of the viewport)   PlayerBar.cs:92-93, :277-291
   MARQUEE on the NOW-PLAYING ROW ONLY inside a track table: "Marquee only for the now-playing row;
   every other row is a cheap plain ellipsis title"                        DetailTracks.cs:2779-2780
   CharacterEllipsis EVERYWHERE ELSE. When the Marquee preference is off, the fade goes too — the edge
   fade lives on MarqueeHost, not on the text (ch 30 W21).
   RULE: a Marquee is for a string the user is CURRENTLY listening to. It is not an overflow strategy.

 THE CAPS REFUSAL — one rung, and the reason it is also a tracking decision
   WaveeType.Eyebrow takes the string's OWN casing; no call site may caps-transform it. "It mangles
   Turkish dotted i, expands German ß, and shouts a user's own display name back at them."  WaveeType.cs:43-47
   EyebrowTracking = 30/1000 em, and that number IS the consequence: "30 is the value that survives
   SENTENCE case — the old 60-120 rungs were compensating for ALL-CAPS."                   :33-38
   Before convergence the app carried NINE tracking values on this one role across 58 call sites.
   THE ONE SANCTIONED CAPS TRANSFORM: the CLASSIC track table's column headers, .ToUpper(CurrentUICulture)
   — culture-aware, and only in the Classic skin (DetailTracks.cs:2509, :3743). ConcertUi.cs:329 records
   removing another one: "the old .ToUpper(culture) was a caps transform over a CULTURE-FORMATTED date".
   Deck faces use ToUpperInvariant on titles — the COMPLETE census is TEN calls across FOUR faces, not the
   three an earlier draft listed: CassetteDeck.cs:319, :320 · CdDeck.cs:295 · VuDeck.cs:285, :296, :297 ·
   WinampDeck.cs:348. That is a PHYSICAL-MATERIAL choice (an LCD has one case), not a type rung, and it is
   allowed there. Note it is ToUpper*Invariant*, not culture-aware — correct for a simulated LCD, and the
   exact opposite of the Classic header's ToUpper(CurrentUICulture). Two caps transforms, two cultures,
   both deliberate; a port that unifies them breaks one.
   THE FULL `.ToUpper` CENSUS under src/apps/Wavee is therefore TWELVE calls: the two Classic-header sites,
   the ten deck-face sites, and ZERO others (ConcertUi.cs:329 records removing the last one).

 THE LOCALE PICKER — shown, and two of four DISABLED
   ["system", "en-US", "nl", "ko-KR"] with Enabled = [true, true, false, false]  SettingsPage.General.cs:34-43
   "Keep them visible so the row advertises what's coming; flip to true per locale as each table lands."
   SetLanguage refuses a disabled index a SECOND time in the handler, "belt-and-suspenders: the ComboBox
   already rejects a disabled pick" (:73).  The row's subtitle is Strings.Settings.Language.RestartSub —
   the change takes effect on RESTART, because AppLocale is captured once per process (AppLocale.cs:8).
   The same value drives the Spotify metadata language (2-letter primary subtag, else "en", :35-44):
   "UI and Spotify metadata move together on restart."

 RTL: NOT SUPPORTED, and it is a decision (§0.23). Every wireframe in all 31 chapters is drawn with
 English strings and nothing mirrors. A 0.3 owner who finds a FlowDirection knob in the engine must
 raise it rather than adopt it — a half-mirrored app is worse than an unmirrored one.
```

### W16 — the zoom ladder, worked (the table `large-display-scaling.md` §3.2 implies)

```
  Suggest(baseW, baseH, mode):  ratio = min(baseW / 1600, baseH / 900)        ZoomAutoPolicy.cs:71-77
                                lo    = mode == Dense ? 0.75 : 1.00                            :53, :75
                                return SnapPlateauDown(clamp(ratio, lo, 2.0))  Ceiling 2.0     :49, :79-89
  Plateaus (the plateau-clean subset of the engine's 12-rung ZoomLadder): 0.75 1.00 1.25 1.50 1.75 2.00
  "Microsoft's 4-epx rule lands on whole pixels only at the 100/125/150/175/200 plateaus; the ladder's
   0.67/0.8/0.9/1.1 rungs put a 4-DIP metric on a fractional pixel and stay available to a MANUAL pick."  :55-58

   base extent (DIP at zoom 1)   ratio             Auto (floor 1.00)   Dense (floor 0.75)
   ─────────────────────────────────────────────────────────────────────────────────────────────────
   1180 ×  760   (launch default) min(0.74, 0.84)  1.00                0.75
   1366 ×  768                    min(0.85, 0.85)  1.00                0.75
   1600 ×  900   (THE DESIGN BOX) min(1.00, 1.00)  1.00                1.00
   1920 × 1080                    min(1.20, 1.20)  1.00  ← 1.20 snaps DOWN, never up to 1.25
   2100 × 1200                    min(1.31, 1.33)  1.25
   2560 × 1440                    min(1.60, 1.60)  1.50  ← 1.55 → 1.50, never 1.75
   3440 × 1440   (ultrawide)      min(2.15, 1.60)  1.50  ← WIDTH alone would say 2.00; both axes bind
   3840 × 2160   (4K)             min(2.40, 2.40)  2.00  ← the Ceiling
   The Dense column is blank from row 4 down because Auto and Dense are IDENTICAL for any ratio ≥ 1: the
   two modes differ only in the FLOOR (1.00 vs 0.75), and the clamp's lower bound never binds above 1.
   Dense is a small-display mode with a large-display name. Rows 1-3 are the only ones where it does
   anything at all — which is the whole finding, and it is worth stating in Settings' own copy.

  WHAT THE MANUAL LADDER IS, which the plateau set is NOT
    ZoomLadder.Steps = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5]  ZoomLadder.cs:17-18
    TWELVE rungs, 50 %–250 % — Chromium's factors. Ctrl+± / Ctrl+wheel / the palette walk ALL twelve, and
    the Settings picker's Manual arm lists all twelve (SettingsPage.Appearance.cs:203-206). AUTO and DENSE
    may only land on the six-plateau subset 75 %–200 %. So "the app's zoom range" is 50 %–250 % by hand and
    75 %–200 % by policy — §9.8's "67 %–200 %" was wrong at both ends and is corrected there.
    Hard bounds beyond the ladder: ZoomLadder.Min 0.25 / Max 5 (:21, :24) — a persisted or programmatic
    value clamps there, which is what a corrupt settings store lands on.

  WHO WINS, when the user and the policy disagree
    A manual step (Ctrl ±, Ctrl+wheel, Ctrl 0, the palette) lands on the shell's static ZoomStep, which
    carries NO settings reference and CANNOT flip the mode itself. The effect detects it instead: live
    zoom ≠ the last value WE applied ⇒ write ZoomMode = Manual and return  (WaveeShell.cs:616-628).
    That one comparison IS the "the policy never clobbers a user's pick" guarantee.
    The upgrade path is the same rule, once: MigrateMode pins an install that already stored a non-1.0
    zoom to Manual — "a user who already chose 125 % must not wake up to 150 %" (ZoomAutoPolicy.cs:99-113).

  WHICH DIRECTION CROSSES A LAYOUT TIER — the statement this chapter owns
    viewportDip = clientPx / (osDpi × zoom). Zooming IN shrinks the DIP viewport, so a page can only
    DEMOTE: sidebar Wide → Mid, detail mode 0 → 1 → 2 → 3, track-table tier 0 → 1, the detail hero's
    winH >= 900 TitleLarge rung → Title, the player bar's ShowTimesRemaining tier. Zooming OUT can
    promote. Auto takes min() over BOTH axes precisely so an auto pick cannot demote the height rung to
    buy width (ZoomAutoPolicy.cs:25-28).
    ⇒ A 200 % zoom on a 3840×2160 panel presents a 1920×1080 DIP viewport — the SAME tier set a 1080p
      monitor at 100 % gets. That is the point of the policy, and it is the sentence to check it by.

  THE DEBOUNCE  ZoomAutoDebounceMs = 500, matching the engine's own resize settle rather than inventing a
  figure (WaveeShell.cs:589-591). A zoom change is a full relayout PLUS a glyph re-raster.
  THE NO-OP GUARD  |suggested − live| <= 0.004 returns without writing (WaveeShell.cs:615, and the
  manual-actor detect at :624; also ZoomAutoPolicy.cs:110 and WaveeApp.cs:242's debounced persist).
  CORRECTION: the literal appears SIX times under src/apps/Wavee but only FOUR of them are about zoom.
  WaveeApp.cs:231 is the SavedVolume persist epsilon and LyricsView.cs:593 is InterludeDotWriteEps
  ("≈1/255: below this an alpha write cannot change a pixel"). Folding all six into one "zoom tolerance"
  constant — which §3's earlier row implied — would couple the volume slider and a lyric alpha to the zoom
  ladder. 0.3 names TWO constants: a zoom-equality epsilon and a settings-persist epsilon. → §3.

  NOTE ON OWNERSHIP  18 §2 W23/W24 own window state, snapping and the DPI hop, including the two-stage
  settle. This chapter owns only the LADDER and the "who wins" rule, because they are a preference
  dimension rather than a window event — and chapter 30 W23 draws the picker. If all three disagree,
  this table is the arithmetic and 18 is the sequence.
```

### W17 — the drag chip, all states (1 char ≈ 8 DIP)

```
 SINGLE ITEM                                        MULTI-SELECT (Count > 1)
 ┌───────────────────────────────┐                      ┌───────────────────────────────┐  ← StackOffset*2 = 8
 │ ┌────┐  Weird Fishes          │                    ┌─┤                               │  ← StackOffset   = 4
 │ │ART │  Radiohead             │                    │ │ ┌────┐  Weird Fishes      ⑶  │  ← count badge
 │ └────┘                        │                    │ │ │ART │  Radiohead            │
 └───────────────────────────────┘                    │ │ └────┘                        │
   ↑40  ↑ title / subtitle                            └─┤                               │
                                                        └───────────────────────────────┘
   ArtSize      40 DIP, Radii.ControlAll, DecodePx = 80 (2×)    DragChip.cs:70, :124-125
   MaxWidth     280                                             :68
   MinWidth/H   96 / 56  (ArtSize + 16)                         :210
   Corners      Radii.OverlayAll     Shadow  Elevation.Flyout   :161-162
   stack cards  Opacity 0.85, offsets 8 and 4                   :174-175, :214
   PICKUP       Rotation 4° + scale 1.02, eased back to flat over 150 ms   :73, :75, :80
   ENTER        Sx/Sy 0.92, Opacity 0 → 1                       :200

 CAPTION — the chip is the ONLY caption surface. Three resting verbs, one refusal, one accept:
   from the QUEUE panel        → Strings.Drag.ReorderHint        WaveeResourceDrag.cs:343
   a ROOTLIST playlist/folder  → Strings.Drag.OrganizeHint       :345
   anything with tracks        → Strings.Drag.DragOntoPlaylist   :346
   nothing depositable         → (no resting caption)            :346
   a live target ACCEPTS       → that target's caption supersedes the resting verb
   a live target REFUSES       → the refusal reason (W20) supersedes both, beside a not-allowed glyph

 ART FALLBACK LADDER (WaveeDragChipModel.For, :31-42 · ArtOf, :47-49)
   a TRACK snapshot → first track's Title + first artist's Name, art = payload.ArtUrl ?? first track's Image
   anything else    → the resource's own Name, NO subtitle, art = payload.ArtUrl
   an Image with no Url → its first mosaic tile (a cover-less playlist carries tiles and no url)
   no art at all    → the KIND GLYPH tile: MusicNote / Album / Contact / RadioTower / Folder / Home   :366-375
   Liked Songs      → its OWN cover, a pre-built element made once at type init (the chip resolver runs
                      inside the 0-alloc frame region while a drag is live)                          :352-362
```

### W18 — the insertion line and its gap preview (1 char ≈ 8 DIP, a playlist track list)

```
   row n-1   │ 12  ♥  [art]  Undo                         Björk          3:41 │
             ╞════════════════════════════════════════════════════════════════╡ ← the framework's insertion LINE
   ╭─────────────────────────────────────────────────────────────────────────╮   (position, size and lifecycle are
   │  [art]  Weird Fishes                                                    │    the framework's: ItemsView's
   │         Radiohead                                                       │    InsertionOptions.GapPreview)
   ├─────────────────────────────────────────────────────────────────────────┤
   │  [art]  Genesis                                                         │  PlaylistInsertionPreview.Row :35-79
   │         Grimes                                                          │    height  = the list's row height
   ├─────────────────────────────────────────────────────────────────────────┤    margin  = TrackRow.RowInset l/r
   │  [art]  Strobe                                              ( +47 )     │    padding = PadX − RowInset
   │         deadmau5                                                        │    corners = Radii.ControlAll
   ╰─────────────────────────────────────────────────────────────────────────╯    fill    = Tok.FillSolidSecondary
   row n     │ 13  ♥  [art]  Genesis                       Grimes        4:12 │    border  = 1 · Tok.AccentDefault
                                                                               shadow  = Elevation.Card
   ≤ Cap cards (= SortableMath.DefaultPreviewCap, the FRAMEWORK's cap, :18 — a local literal
     would drift the cards off the gap the view already sized)
   the LAST card carries the "+N" pill: Radii.PillAll, Tok.AccentSubtle, 12/600 Tok.AccentTextPrimary  :60-66
   HideTrackArtwork drops the art column from the preview too (showArtwork, :20, :50)
   HitTestVisible = false on every node  :32, :76
```

### W19 — the spring-load waypoint (a container that opens under a held drag)

```
 t = 0 ms      pointer enters a collapsed sidebar folder / an inactive tab
               ┌──────────────────────────────┐
               │ ▸ Cafe & chill               │   the drop plate lights; the chip keeps travelling
               └──────────────────────────────┘

 t = 500 ms    WaveeResourceDrag.SpringLoadMs = 500   (WaveeResourceDrag.cs:263)
               ┌──────────────────────────────┐        "long enough that merely TRAVELLING ACROSS a
               │ ▾ Cafe & chill               │         folder never opens it" — the macOS spring-loaded
               │    ▸ Late night              │         folder / WinUI hold-to-open convention.
               │    ♪ 우울해                   │
               └──────────────────────────────┘        A TAB springs the same way and ACTIVATES its page
                                                       (ch 18 §6, parity 43): the deposit then targets the
 the gesture is NOT committed by the spring —          page the user can now see.
 the drag is still live and can leave again.
```

Two surfaces use it and neither owns the constant: the sidebar tree (ch 25 W16) and the tab strip (ch 18 §6). It is
one number in `Platform/Drag.cs` in 0.3.

### W20 — the five playlist refusals, as captions (1 char ≈ 8 DIP)

```
  Evaluate(editable, loading, payloadHasTracks, sameList, naturalOrder, filtered, rowsKeyed)
  in THIS order — the order is the design (WaveeDragRules.cs:126-137):

  ┌─ chip ──────────────────────────┐  refusal          when                              remedy the user has
  │ ┌────┐ Weird Fishes        ⊘    │  ─────────────────────────────────────────────────────────────────────
  │ │ART │ Radiohead                │  NotEditable      an editorial / daylist / someone   none — this is a fact
  │ └────┘ <caption>                │                   else's playlist
  └─────────────────────────────────┘  Loading          the destination's track list is    wait a moment
    ⊘ = the framework's not-allowed                     still Pending (a shimmer)
        glyph; the caption is ours    NoTracks          an artist / a route / a show —     none — locked decision
                                                        nothing resolvable (:210-217)      (ch 01 §8)
                                      Sorted            a SAME-LIST reorder under a        clear the sort
                                                        non-natural sort
                                      Filtered          a SAME-LIST reorder under a        clear the filter
                                                        search/filter (display is a subset)
                                      Syncing           our own add is still in flight;    try again in a moment
                                                        rows carry no item_id yet

  A FOREIGN COPY is legal under any sort or filter (:132) — it appends/inserts by display position
  without having to name existing membership rows. Only the same-list MOVE has the last three arms.

  Sorted and Filtered are reported BEFORE Syncing on purpose (:123-125): they are states the user can act
  on and fix, while "still syncing" is a wait — naming the wait first would hide the two with a remedy.
```

Two more refusals live outside that table and must keep their own shapes:

```
  TAB STRIP      TabDropRules.AcceptsDeposit (:168-174) — a tab can only APPEND, so a row dragged out of
                 playlist P onto P's OWN tab is refused OUTRIGHT rather than silently no-op'd: the tab never
                 lights up for a gesture that has nothing to do.
  COLLAPSED RAIL SidebarRailDropRules.TileTransparent (:208-209) — a 56-DIP strip of covers is a CORRIDOR as
                 much as a set of destinations. A rootlist payload with no tracks (a FOLDER being re-filed) is
                 merely passing through: the tile sits the gesture out ENTIRELY (transparent, discovery walks
                 past it to an accepting ancestor) instead of accusing it with "Nothing to add".
```

### W21 — the OS-file drop cue and its scrim scope @ 1600×900 (1 char ≈ 16 DIP)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48
│☰  ◀ ▶ │Home│Daily Mix 3 │ ＋      [ 🔎 Search... ]           ◉ Christos  🔔 👥 📌 ⚙│  ⌄  ─ □ ✕   │ ← FULLY LIT
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│  sidebar     │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│  FULLY LIT   │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒┌──────────────────────────────┐▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│   Drop a file to play it     │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒└──────────────────────────────┘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│              │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤ 72
│  ◀◀  ▶  ▶▶     Weird Fishes · Radiohead      ━━━━●────────      🔊 ━━━━●──   📺 ♥ ⋯               │ ← FULLY LIT
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ▒ = the ENGINE's spotlight scrim, clipped to the content region's absolute rect, which the shell
      publishes on realize and on every re-arrange:  sc.SpotlightScrimClip = AbsoluteRect(contentRegion)
      (WaveeShell.cs:414-421, wired at :1341-1342)

  the PILL:  Padding 18/10, Corners Radii.Control, Fill Tok.FillSolidBase, Border 1 · Tok.AccentDefault,
             Shadow Elevation.Dialog, text 14 Tok.TextPrimary, Strings.LocalFile.DropHint  (:1425-1430)
  the LAYER: Grow=1, HitTestPassThrough, centred both axes,
             Opacity = Prop.Of(() => _fileDropOver.Value ? 1 : 0)  ← BOUND: compositor-only  (:1418-1432)
  the TARGET: DropTargetSpec([DropKinds.Files]) on the shell ROOT, so the whole window accepts;
              OnDrop → LocalFileActions.PlayDropped(paths)  (:1351-1359)
```

The scrim is the *engine's*, but its **scope** is ours, and that is the whole visual decision: dimming the chrome
would make the window look disabled during a gesture the chrome is not part of.

### W22 — the dialog width ladder (cards drawn to scale in width only)

```
 320  ├────────────────────────────────────┤   ContentDialog MinW (ContentDialog.cs:110) — the DEFAULT when
      │ Rename playlist                    │   DialogWidth is unset and there are ≤ 2 buttons (:283)
      │ ┌────────────────────────────────┐ │   · ContainerActions.RenameDialog (a 320 EditableText inside) :180-184
      │ │ Dalkom Cafe                    │ │   · TrackActions "View credits" (PrimaryText="" hides primary) :161-168
      │ └────────────────────────────────┘ │   · Menus.OpenPicker "Add to playlist"                        :518-524
      │              [ Cancel ] [ Rename ] │   · SettingsShared.Confirm — every destructive confirm        :32-40
      └────────────────────────────────────┘

 460  ├──────────────────────────────────────────────────────┤   PlaybackRuntimeSetupCard (the phased card,
                                                                 reactive body + reactive Footer)          :38

 480  ├────────────────────────────────────────────────────────────┤   two sources:
                                                                      · the implicit THREE-BUTTON width     :283
                                                                        (the Storage move-cache pair, which
                                                                         carry Primary/Secondary/Close)  :629-654
                                                                      · SidebarPickers.DialogW, BodyW = 432 :58-61
                                                                        (item pickers + the customizer confirm)

 548  ├────────────────────────────────────────────────────────────────────────┤   ContentDialog MaxW (:110)
                                                                                  · ReportDialog (ContentWidth 500) :35
                                                                                  · LyricsInspectorDialog           :59
                                                                                  · PlaybackRuntime diagnostics     :779

 ── the two NON-ContentDialog plates, and why ────────────────────────────────────────────────────────────────
 720  ├────────────────────────────────────────────────────────────────────────────────────────────┤
        AfterUpdateDialog — a raw overlay with PopupChrome.Modal: "the plate is 720 DIP wide and
        ContentDialog hard-clamps its card" (AfterUpdateDialog.cs:24-25)
 762 × 490 ├─────────────────────────────────────────────────────────────────────────────────────────────┤
        SetupDialog / SetupPlate — a raw overlay reproducing ContentDialog's OWN chrome tokens at a
        different size, because the card clamps to 548 × 756 and Rise's reference plate is 762 × 490
        (SetupDialog.cs:11-16). It keeps ContentDialog's Min (320 × 184) and a 32-DIP viewport margin
        (SetupLayout.cs:25) and re-implements Enter → primary / Escape → close itself (:99-100).

 SHARED CARD METRICS (every width above)          ContentDialog.cs
   padding                 24 all round                :111     title size     20 SemiBold        :114
   content gap             12                          :112     content size   14                 :115
   button gap               8                          :113     button min/H   130 / 32           :116-117
   corners  Radii.OverlayAll · border 1 SurfaceStrokeColorDefault · Shadow Elevation.Dialog        :365-369
   a body taller than the card SCROLLS inside it at MaxH − 200 = 556, with EdgeCues.None           :308
   a SINGLE-button dialog keeps the unused left star column and pushes the button right             :288-291
```

### W23 — the destructive-confirm shape (1 char ≈ 8 DIP)

```
 ┌────────────────────────────────────────────┐  320 DIP
 │                                            │  SettingsShared.Confirm(overlay, title, body, verb, onConfirm)
 │  Clear all history?                        │  ← d.Title      (a question)
 │                                            │
 │  This deletes the log file. It cannot be   │  ← d.Message
 │  undone.                                   │
 │                                            │
 │                    ┌────────┐ ┌──────────┐ │
 │                    │ Clear  │ │  Cancel  │ │  ← ORDER IS primary, secondary, close (ContentDialog.cs:277-280)
 │                    └────────┘ └━━━━━━━━━━┘ │     so the DESTRUCTIVE verb sits LEFT and Cancel RIGHT
 └────────────────────────────────────────────┘     ━━ = the ACCENT ring: DefaultBtn.Close (SettingsShared.cs:38)
                                                    → Enter cancels; the focus trap focuses Cancel first
                                                      (TabIndex 1 goes to the accent button, :256-260)
 CALLERS (every destructive confirm in the app goes through this ONE helper)
   Settings ▸ Storage — cache wipe                          SettingsPage.Storage.cs
   History page — "Clear all"                               HistoryPage.cs:568-575
   Sidebar customizer — reset to preset                     SidebarCustomizerPage.cs:711-717 (480, DefaultBtn.Primary —
                                                            a RESTORE, not a destruction; it is allowed to default)
   ANY confirm-required WaveeActionDescriptor                WaveeActionDescriptor.cs:139-143 (W12)
 NO-OVERLAY FALLBACK: `if (overlay is null) { onConfirm(); return; }` (:31) — a confirm with nowhere to
 render must not silently swallow the action in a headless path. NOTE the deliberate asymmetry with the
 action platform: a DESCRIPTOR refuses outright in the same situation (§0.16), because a sidebar binding
 firing unconfirmed is a user-facing hazard while a headless internal call is not.
```

### W24 — first run: the composite frame a brand-new account meets (1 char ≈ 16 DIP)

```
 ── t = −1 : THE WIZARD OWNS THE WHOLE WINDOW ───────────────────────────────────────────────────────
   needsSignIn ⇒ the ONLY leaf is SetupPreAuthRoot (WaveeApp.cs:403-405). There is no shell behind it —
   no chrome row, no sidebar, no player bar. SetupSession(FirstRun) walks Terms → SignIn → LocalPlayback
   (SetupPage, SetupGating.cs:14). An install that COMPLETED setup and then signed out enters at
   SetupEntryPoint.Reauth, straight to the SignIn page — re-walking terms would be nonsense (:365-368).

 ── t = 0 : THE WIZARD CLOSES. THE SHELL MOUNTS. NOTHING HAS ARRIVED YET. ───────────────────────────
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐ 48
│☰  ◀ ▶ │Home│ ＋            [ 🔎 Search songs, artists, albums... ]        ◉ <name>  🔔 👥 📌 ⚙│⌄ ─□✕│
├──────────────┬─────────────────────────────────────────────────────────────────────────────────────┤
│ ▸ Home       │  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ← Home's SKELETON, derived from the HomeSeed      │
│ ▸ Search     │  ░░░░░  ░░░░░  ░░░░░  ░░░░░           silhouette: hero, weekly pair, quick grid,     │
│ ▸ Your Library│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░     recents, mix band, chips, radio, queue, books, │
│              │  ░░░░░  ░░░░░  ░░░░░  ░░░░░           featured, podcasts, topic, section, discover   │
│  ── Playlists│                                        (FakeData.cs:587-610 — real shape, blank text)│
│   (EMPTY —   │                                                                                      │
│    no rows,  │  every OTHER surface at t=0:                                                         │
│    no        │    right rail        CLOSED (session chrome restored from an empty session.json)     │
│    skeleton) │    tabs              exactly ONE tab, "Home", unpinned                               │
│              │    back / forward    both DISABLED (an empty history stack)                          │
│              │    masthead band     ABSENT (Home is not a masthead family)                          │
│              │    shell material    NEUTRAL — Home has published no wash yet (ContentHost.cs:51-55) │
│              │    notification bell  no badge                                                       │
│              │    focus             wherever the wizard left it — NOTHING moves it (W3 path 5)      │
├──────────────┴─────────────────────────────────────────────────────────────────────────────────────┤ 72
│  ◀◀  ▶  ▶▶     Nothing playing                     ──────────────      🔊 ━━━━●──   ♥ ⋯           │ ← MOUNTED and
└────────────────────────────────────────────────────────────────────────────────────────────────────┘   resting, not
  OS SURFACES AT t=0: idle in all four (ch 14 W5, W9, W12, W16 draw them).                               absent

 ── t = first catalog answer (the silent resume completes; rootlist + home land) ────────────────────
   sidebar   the playlist rows APPEAR. This is the ONE surface that goes straight from empty to
             populated with no skeleton in between; ch 25 W4 owns the PINNED band's own empty state.
   home      ONE staggered reveal, 40 ms apart, 8-DIP rise, blur→sharp (ch 10 parity 2-3). NOT a
             partial paint: skeleton → hold → one reveal.
   material  Home publishes its three washes; the chrome eases from neutral over 250 ms (W7 channel 3)
   bell      may gain a badge — and the FIRST feed rebuild raises NO toast, because the watermark is
             zero and a zero watermark only records where the feed was (ToastEscalator.cs:25-26, :64)

 ── t = steady (a genuinely EMPTY account: no playlists, no likes, no history, no recents) ──────────
   sidebar        its own empty state (ch 25 W4)          library pages    ch 15 W9/W10
   home           the server's own empty/greeting feed     recents          ch 16
   liked          ch 07 W19                                search           ch 13 W11
   history page   empty (nothing navigated yet)            jump list        ch 14 W16
   RULE: at no point does a surface show a SKELETON for data that will never arrive. The skeleton is
   for "a request is in flight"; the empty state is for "the answer was nothing". A new account crosses
   from one to the other exactly once per surface, and never back.  → §7.2 generalises it.

 ── the wizard re-entered POST-AUTH (Appearance / Sidebar pages, or a terms re-arm) ─────────────────
   The shell is up and the wizard sits over it. The shell paints its OWN scrim, because only the shell
   knows whether the current page wants an ordinary dim (SetupCover.Dim) or the lifted live-preview look
   (SetupCover.Live — where the wizard's promise is "this window IS the preview").
   SetupCoverScrim: Tok.FillSmoke, cross-faded over WaveeMotion.Standard, 0 ms under reduced motion,
   HitTestVisible=false, always mounted, reading the static SetupSession.Covering signal itself.
   (WaveeShell.cs:2272-2289 · the engine's popup scrim paints only for the BARE pre-auth mount)
```

### W25 — the ambient cadence, plugged / battery / unfocused (1 char ≈ 1 s)

```
 launch ──┬─ AmbientPowerPolicy.Attach(host)                        AmbientPowerPolicy.cs:75-81
          │    s_plugged = s_pending = ReadPlugged()  ← applied IMMEDIATELY, no debounce at launch
          │    Apply(): DefaultLoopHz = 30 | 24 ;  InactiveFrameIntervalMs = 33  (set once)  :118-123
          │
 the Watcher component (WaveeShell.cs:859) owns a UseInterval(PollPower, 2000)     :132-139

  t (s)   0    2    4    6    8   10   12   14   16   18   20
  power   ███████████████████│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   ← charger unplugged at ~t = 9
  reads   ▲    ▲    ▲    ▲    ▲    ▲    ▲    ▲    ▲    ▲    ▲     ← PollMs = 2000  :66
                                  └ first DC read: s_pending flips, the window re-arms  :88-92
                                       └ second DC read, 2 s later: HELD → APPLY        :94-97
  DefaultLoopHz  30 30 30 30 30 30 │ 24 24 24 24 24
                                    └ ~2-4 s after the real transition, by design

  A BLIP shorter than one poll interval is usually never even sampled (:55-59); one that IS sampled
  re-arms the window and is then overwritten by the next read, so the cadence never flips for it.

 WHAT READS THE KNOB (every cadence-less `loop: true` slab row)
   the buffering spinner · the skeleton shimmer · the now-playing equalizer · the seek playhead ·
   the karaoke lyrics wipe · CanvasDeck's four drift channels (CanvasDeck.cs:131-134) ·
   the concert ground + two arcs (19 s / 9 s / 7 s, ConcertUi.cs:1010-1012, :1035-1037) ·
   the browse tile wobble (11 / 13 / 17 s, :1013-1015, :1041-1043) ·
   the Liked cover wall drift (92 s, LikedCoverTreatments.cs:438, :742) and its three marquee bands
   (60 / 74 / 66 s, :553, :747-749)
 WHAT DOES NOT
   an explicit Cadence.Display row — springs, live drags, LoginView's 1 100 ms marquee (LoginView.cs:166)
   an explicit Cadence.At(hz) row
   every UseInterval source — the deck tick, DeckClock, the analyser: self-paced timers, not slab rows
   (AmbientPowerPolicy.cs:37-40). DeckClock.TickMs REFERENCES PluggedLoopHz rather than copying it (:46-48).

 UNFOCUSED WINDOW: the engine floors the gap between ANIMATION-ONLY frames at InactiveFrameIntervalMs = 33
 (~30 fps ceiling) whenever the window is not foreground. This is the ENGINE's throttle now; the policy
 no longer tracks focus and the Watcher no longer reads InputHooks.WindowChromeEpoch (:125-131).
 MINIMIZED/PARKED: UseInterval auto-pauses via UseIsActive, so the poll itself stops (:58-59).
 ENERGY SAVER counts as "not plugged" — the OS is explicitly asking for less work (:109).
 A DESKTOP (no battery) or an unknown ACLineStatus resolves PLUGGED, or every desktop would run
 permanently half-capped; a FAILED read also resolves plugged (:100-115).
```

### W26 — what `--fake` actually renders (the Wave 5 gate's real shape)

```
 ROUTE / SURFACE          --fake state       source                                          gate value
 ──────────────────────────────────────────────────────────────────────────────────────────────────────
 home                     LOADED  ████       assets/spotify/home.json via SpotifyExportSource  HIGH
   └ Charts row           FAILED  ▒▒▒▒       NullBrowseService + an AUTHENTICATED fake session HIGH (fail-loud arm)
 album:…                  LOADED  ████       FakeData.Album(i), 4 kinds via AlbumShape         HIGH
 pl:…                     LOADED  ████       FakeData.Playlist(i) + export playlists.json      HIGH
 liked                    LOADED  ████       export PseudoPlaylist / FakeData.LikedSongs       HIGH
   └ content-filter chips EMPTY   ░░░░       NullContentFilterService  (Services.cs:324)       LOW
 local                    LOADED  ████       LocalSource + FakeData.LocalTracks()              HIGH
 artist:…                 LOADED  ████       FakeData.Artist(i) + Discography() (hundreds)     HIGH
 disco:…                  LOADED  ████       the same synthetic discography                    HIGH
 show:…                   LOADED  ████       FakePodcastSource, 8 shows wavee:show:0..7        HIGH
 prerelease:…             ABSENT  ────       no prerelease in FakeData (ch 30 DG3)             ZERO
 albums/artists/podcasts  LOADED  ████       FakeSource 13 albums / 12 artists; stats 13/12/161/7 HIGH
 search                   PARTIAL ▓▓░░       SpotifyExportSource.SearchAsync :142-155 —
                                             tracks + albums + artists + playlists ONLY. NO shows,
                                             NO episodes, NO genres, NO top-hit, NO chips.      PARTIAL
 browse / browse:…        EMPTY   ░░░░       NullBrowseService  (:224)                          ZERO
 browse-section: /
   home-section:          EMPTY   ░░░░       NullHomeSectionService  (:236)                     ZERO
 recents                  EMPTY   ░░░░       NullRecentsService → RecentsSnapshot.Empty (:232)  ZERO
 history                  LOADED  ████       the shell's own log — navigate 10-12 routes first  HIGH
 concerts / concert:… /
   artist-concerts:…      EMPTY   ░░░░       NullConcertService  (:360)                         ZERO
 whatsnew                 LOADED  ████       the bundled release-notes document                 HIGH
 settings                 LOADED  ████       the real settings store                            HIGH
 sidebar-customize        LOADED  ████       the live preference document + FakeData miniatures HIGH
 home-customize           LOADED  ████       the live home-layout document                      HIGH
 the two diagnostics    LOADED  ████       real, developer-mode gated (api-console DELETED,   HIGH
   pages                                       plan §9.6 Q7, 2026-09-12 — was three)
 module:…                 ABSENT  ────       Services.Modules is NULL on the fake backend       N/A
 (unknown route)          LOADED  ████       the not-found page                                 HIGH
 ── SURFACES, not routes ─────────────────────────────────────────────────────────────────────────────
 player bar               RESTING ░░░░       UnsupportedPlaybackPlayer → every play intent is a
                                             toast; the bar rests at "Nothing playing"           ZERO
 right rail / NPV / deck  RESTING ░░░░       nothing plays ⇒ no art, no clock, no motion          ZERO
 queue panel              EMPTY   ░░░░       FakeData.DefaultQueue() exists (:569-576) but has
                                             NO caller in --fake                                 ZERO
 lyrics (rail + immersive) ABSENT ────       NoLyricsProvider (:624). FakeData.Lyrics() builds a
                                             40-line WORD-SYNCED document (:543-567) that NOTHING
                                             CALLS.                                              ZERO
 video (all four surfaces) ABSENT ────       no modules, no VideoOverrides                       ZERO
 friends panel            EMPTY   ░░░░       NullFriendActivityService  (:325)                   ZERO
 notifications centre     EMPTY   ░░░░       NullSpotifyNotificationsService  (:358)             ZERO
 device picker            EMPTY   ░░░░       NoConnectDevices  (:590)                            ZERO
 track credits            EMPTY   ░░░░       NullTrackCreditsService  (:323)                     ZERO
 top-tracks / popcount /
   pre-save release rows  EMPTY   ░░░░       NullUserTopService / NullPlaylistPopcountService /
                                             NullPreReleaseService  (:320-322)                   ZERO
 OFFLINE / METERED state  ABSENT  ────       the fake backend never reports Offline and NetworkPolicy
                                             never sees a metered link                           ZERO
 OS surfaces (all four)   IDLE    ░░░░       nothing plays (ch 14 owns their frames)             ZERO

 ████ = renders its loaded state    ▓▓░░ = partial    ▒▒▒▒ = a real failure state    ░░░░ = empty state
 ──── = the surface does not mount at all

 SCORE: of the 30 renderable destinations in §12.2, 18 render LOADED, 1 partial, 1 fail-loud, 6 empty,
 2 absent — and of the ~15 non-route SURFACES above, TWO (the queue's and the lyrics' fixtures) are
 seeded but unreachable. "--fake opens every route" is satisfied by all 30. That is the whole problem,
 and §9.7 restates the gate so it is not.
```

### W27 — the states this chapter CHECKED and does not own, and what the check found

Silence about a state is indistinguishable from having missed it. These are the states a cross-cutting audit asks
for that this chapter does **not** draw, each with the grep that settles it, so a reader can tell "owned elsewhere"
from "nobody looked".

```
 STATE                        WHAT THE GREP FOUND under src/apps/Wavee                    OWNER / VERDICT
 ─────────────────────────────────────────────────────────────────────────────────────────────────────────
 maximized / restored /       ZERO hits for WindowState / RestoreBounds / maximiz* as a   ch 18 W23.
 snapped                      window read. Nothing in the app branches on window state:   The app sees a
                              every size-dependent decision is a Viewport.Size tier.      SIZE, never a STATE.

 a monitor move / a LIVE      ZERO hits for "Dpi" of any casing in the whole app tree.    ch 18 W24.
 DPI change                   The OS DPI scale reaches the app ONLY as the product        This chapter owns
                              osDpi × zoom inside Viewport.Size (ZoomAutoPolicy.cs:30-38  only the 500 ms
                              is the one place that even names it, and it names it to     re-resolve that
                              say it must NOT be read directly).                          follows (W16).
                              ⇒ a DPI hop reaches THIS chapter as exactly one event: a
                                debounced baseDip change → ZoomAutoPolicy.Suggest.

 Windows CONTRAST themes      ZERO. `ThemeKind` has two members and the engine's own       NOT SUPPORTED,
                              comment reserves the third (Tokens.cs:5). The only two       and it is a
                              `highContrast` strings in the app are Spotify's WIRE KEY     DECISION — §9.8.
                              for cover grading (CoverColorFiller.cs:76-81,
                              WaveePalette.cs:140) and have nothing to do with the OS
                              setting. Checked 2026-09-12, not assumed.

 LIGHT theme                  SUPPORTED, and it is §4 + W5 + W5B. 50 theme branches        THIS chapter.
                              across 15 files; thirteen surfaces compute a colour.

 first run / empty account    SUPPORTED and drawn — W24 (the composite at t=−1, t=0,       THIS chapter.
 at t=0                       t=first answer, t=steady).

 OFFLINE / metered /          SUPPORTED in the policy, ABSENT in the composition —         THIS chapter,
 reconnecting                 W9/W10, and OfflineBanner has zero call sites (§9.9.5).      W9-W10.

 an UNFOCUSED window          SUPPORTED and drawn — W25. The throttle is the ENGINE's      THIS chapter,
                              (InactiveFrameIntervalMs = 33, set once and never            W25 + §5.4.
                              re-read); the policy no longer tracks focus at all
                              (AmbientPowerPolicy.cs:24-29, :125-131).

 MINIMIZED / parked           UseInterval auto-pauses via UseIsActive, so the power poll   W25.
                              itself stops (AmbientPowerPolicy.cs:58-59). Nothing else
                              in this chapter runs while parked.

 RTL / bidi layout            ZERO FlowDirection / RightToLeft / BiDi layout code. The     NOT SUPPORTED,
                              engine's shaper resolves BiDi glyph RUNS; nothing mirrors    a DECISION —
                              LAYOUT.                                                      §0.23, parity 94.

 TextScaleFactor (OS text     ZERO. `large-display-scaling.md` §3.5 PROPOSES it on type    UNVERIFIED
 scaling)                     only; nothing in 0.2.9 reads it. A user with OS text         whether 0.3
                              scaling at 200 % gets Wavee's own metrics unchanged.         adopts it — §9.8.
```

**The rule this frame encodes.** Two of the eleven rows are *decisions* (contrast themes, RTL), two are *owned by
chapter 18* (window state, the DPI hop), one is *unverified* (TextScaleFactor), and six are this chapter's. A 0.3
owner who finds no wireframe for maximized windows here should find this table instead of nothing — and a 0.3 owner
who sees `ThemeKind.HighContrast` compile should stop, per §9.8.

---

## 3. Tokens

This chapter introduces **no new colour tokens**. It owns constants, and every one of them either has a name today or
needs one in 0.3.

| Constant | Value | 0.2.9 site | 0.3 home |
|---|---|---|---|
| `Tok.FocusOuter` / `FocusInner` | light `#000000E4` / `#FFFFFFB3`; dark `#FFFFFF` / `#000000B3` | engine `PaletteBuilder.cs:254-255`, `:342-343` | **engine — never restate** |
| `Tok.FocusThickness` | `2f` (both themes) | engine `Tokens.cs:456` | engine |
| the secondary ring's width | `1f` (a literal inside `EmitFocusRing`) | engine `SceneRecorder.cs:3364-3368` | engine |
| **`FocusInsetBordered`** | `2f` | **22** app sites, unnamed (the complete census, §2 W1) | `Platform/Design.cs` — **a name, once** |
| **`FocusInsetRow`** | `1f` | **11** app + 2 engine sites, unnamed; two of the eleven are conditional (`live ? inset : default`) | `Platform/Design.cs` |
| the stray `-2` | `Edges4.All(-2f)` | `PlayerStyleFlyout.cs:101`, `:182` | **deleted** — becomes `FocusInsetBordered` |
| `PageNavMotion.FadeThroughExitMs` | `120f` | `PageNavMotion.cs:49` | `Shell/Shell.cs` — and the **masthead reads it**, never a copy (`ShellMastheadBand.cs:23-26`) |
| `FadeThroughEnterDelayMs` | `90f` | `:50` | same |
| `Expressive.Fast` / `Expressive.DistBase` | engine tokens | engine | engine |
| `WaveeMotion.Standard` | `250f` | `Design/WaveeMotion.cs` | `Platform/Design.cs` §5 |
| `KeepAliveOptions.MaxEntries` | `3` | `ContentHost.cs:106` | `Shell/Shell.Host.cs` |
| `ShellMaterialLayer.NeutralGround` | `WaveeColors.ShellGround @ A 0.03` | `ShellMaterialLayer.cs:89` | `Platform/Design.cs` — `CoverShellTintBinder` reads the SAME value (`CoverPaletteLeaves.cs:80-88` note) |
| the three wash placements | `Hero c(.06,.00) r(.74,.92) f.62` · `Weekly c(.92,.10) r(.58,.78) f.64` · `Mix c(.58,1.00) r(.90,.70) f.66` | `ShellWashGeometry.cs:24-30` | `Platform/Design.cs` |
| wash alphas | hero `.055 / .10`; shelf `.05 / .085` (light / dark) | `:33-37` | same |
| the wash clip inset | `PlayerDock.Reserve` (72) on the bottom | `ShellMaterialLayer.cs:76` | `Platform/Design.cs` |
| page-tone plane alpha | `0.30` light / `0.20` dark | `CoverPaletteLeaves.cs:139` | `Platform/Design.cs` |
| shell tint alpha | `0.05` light / `0.14` dark | `:242-246` | same |
| artist blend wash stops | `.20/.06/0` light · `.30/.08/0` dark | `:182-193` | same |
| `ArtworkPlaceholderLight` | `#F2F2F2` | `Surfaces.cs:58` | `Platform/Design.cs` |
| `Toast` defaults | `DurationMs 5000` · `MaxVisible 3` · `Placement BottomRight` · `EdgeInset` = the dock height | engine `Toast.cs:33`, `:82-88` | engine; the `EdgeInset` assignment is `Shell/Shell.UI.cs` |
| the one non-default toast duration | `8000f` | `ReportDialog.cs:519` | `Platform/Controls.cs` — see §6.4 |
| the one settings toast duration | `6000f` | `SettingsPage.Notifications.cs:119` | same |
| the sticky arm | `DurationMs = plan.Sticky ? 0f : 5000f` | `NotificationCenterBridge.cs:236` | `Platform/Platform.cs` |
| `ToastEscalator.MaxPerRebuild` | `3` | `ToastEscalator.cs:31` | `Platform/Platform.cs` |
| `ProgressStepPercent` | `5` | `:107` | same |
| the seven action-reason loc keys | `sidebar.action.unavailable.*` | `WaveeActionTargeting.cs:140-146` | `Platform/Actions.cs` — **literals on purpose** (`:137-139`) |
| the 29 action icon keys | `play`, `play-next`, `queue`, `like`, `save`, `heart`, `add`, `album`, `artist`, `link`, `remove`, `delete`, `open`, `rename`, `people`, `globe`, `credits`, `share`, `copy-uri`, `open-web`, `radio`, `video`, `replace`, `locate`, `reveal-folder`, `pin`, `unpin`, `folder` | `ActionIcons.cs:13-41` | `Platform/Actions.cs` — a semantic key, **never a raw glyph** (`WaveeActionDescriptor.cs:37-39`) |
| the thirteen first-party action keys | `wavee.play`, `.playNext`, `.addToQueue`, `.toggleLike`, `.save`, `.open`, `.goToAlbum`, `.goToArtist`, `.copyLink`, `.songRadio`, `.artistRadio`, `.pinToSidebar`, `.unpinFromSidebar` | `BuiltInExtensionTable.cs:37-49` | `Platform/Actions.Table.cs` — **persisted inside bindings; never rename one** |
| the bound-row metrics | height `48` · gap `Spacing.S` · padding `2,0,S,0` · label 13/600 · sub 11 `TextTertiary` · reason 11 `TextTertiary` + a 12-DIP `StatusWarning` | `SidebarItemPickers.cs:430-434`, `:541-548` | `Platform/Actions.UI.cs` |
| `AvgCharW` | `7.6f` | `ContextBandLayout.cs:76` | `Entities/Detail.cs` CORE + restated in `Platform/Design.cs` §type |
| `ContextBandLayout.TitleCap` | `280f` | `:70` | same |
| `EyebrowTracking` | `30f` (per 1000 em) | `WaveeType.cs:38` | `Platform/Design.cs` §type |
| the four settings picker widths | `160 / 180 / 260 / 300` | `SettingsPage.Appearance.cs:344`, `:402`; `General.cs:85`, `:227` | `Platform/Controls.cs` — **a named ladder**, not four literals |
| the locale set + mask | `["system","en-US","nl","ko-KR"]`, `[true,true,false,false]` | `SettingsPage.General.cs:34-43` | `Platform/Platform.cs` |
| `ZoomAutoPolicy.DesignW / DesignH` | `1600f / 900f` | `ZoomAutoPolicy.cs:42`, `:45` | `Platform/Platform.cs` CORE |
| `Ceiling / DenseFloor` | `2f / 0.75f` | `:49`, `:53` | same |
| `Plateaus` | `{.75, 1, 1.25, 1.5, 1.75, 2}` | `:59` | same |
| `ZoomLadder.Steps` (the MANUAL ladder) | `{.5, .67, .75, .8, .9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5}` — twelve rungs, 50 %–250 % | engine `ZoomLadder.cs:17-18` | **engine — never restate.** `Plateaus` is its plateau-clean subset and the policy may only pick from that |
| `ZoomLadder.Min / Max` | `0.25f / 5f` — the hard bounds a persisted value clamps to | engine `ZoomLadder.cs:21`, `:24` | engine |
| the live re-theme duration | `250f` ms, passed to `ThemeControl.Request` | `WaveeShell.cs:2155` (the manual toggle) · `WaveeApp.cs:59` (the OS follow, **guarded on `Tok.Epoch` actually advancing**, `:56-59`) | `Platform/Design.cs` §5 — it is `WaveeMotion.Standard` spelled as a literal twice |
| zoom equality tolerance | `0.004f` | **four zoom sites**: `ZoomAutoPolicy.cs:110` · `WaveeShell.cs:615`, `:624` · `WaveeApp.cs:242` | **one** named constant. The literal also appears at `WaveeApp.cs:231` (the **SavedVolume** persist epsilon) and `LyricsView.cs:593` (`InterludeDotWriteEps`, ≈1/255) — those are NOT zoom and must not be folded in |
| `ZoomAutoDebounceMs` | `500f` | `WaveeShell.cs:591` | `Platform/Design.cs` — it IS the **resize settle**, shared |
| `MigrationTargetVersion` | `1` | `ZoomAutoPolicy.cs:95` | same |
| `NetworkPolicy.RefreshMs` | `60_000` | `NetworkPolicy.cs:19` | `Platform/Platform.cs` |
| `QualityMin / QualityMax` | `0 / 2` | `:20-21` | same |
| dialog `MinW / MaxW / MinH / MaxH` | `320 / 548 / 184 / 756` | engine `ContentDialog.cs:110` | engine — do not re-declare |
| dialog three-button width | `480f` | `:283` | engine |
| the app's dialog widths | `320 · 460 · 480 · 548` | §2 W22 | `Platform/Controls.cs` — **a named ladder**, not five literals |
| `SidebarPickers.DialogW / BodyW` | `480f / 432f` | `SidebarItemPickers.cs:58-61` | `Platform/Controls.cs` (it IS the 480 rung) |
| `ReportDialog.DialogWidth / ContentWidth` | `548f / 500f` | `ReportDialog.cs:35-36` | same |
| setup plate | `762 × 490`; min `320 × 184`; viewport margin `32` | `SetupLayout.cs:23-25` | `Screens/Setup.cs` (ch 28) |
| after-update plate | `720f` | `AfterUpdateDialog.cs:24-25` | `Screens/ReleaseNotes.UI.cs` (ch 28) |
| `SpringLoadMs` | `500f` | `WaveeResourceDrag.cs:263` | `Platform/Drag.cs` |
| `DragChip.MaxWidth / ArtSize / TiltDeg / PickupScale / PickupFlashMs / StackOffset` | `280 / 40 / 4° / 1.02 / 150 / 4` | engine `DragChip.cs:68-85` | engine — do not re-declare |
| insertion preview cap | `SortableMath.DefaultPreviewCap` | `PlaylistInsertionPreview.cs:18` | **read the framework's**, never a local 3 |
| file-drop pill | padding `18,10`; `Radii.Control`; `FillSolidBase`; border 1 `AccentDefault`; `Elevation.Dialog`; text 14 `TextPrimary` | `WaveeShell.cs:1425-1430` | `Shell/Shell.UI.cs` |
| insertion-preview card | `FillSolidSecondary`; border 1 `AccentDefault`; `Elevation.Card`; `Radii.ControlAll`; `+N` pill `AccentSubtle` / `AccentTextPrimary` 12/600 | `PlaylistInsertionPreview.cs:60-76` | `Entities/Track.UI.cs` |
| setup cover scrim | `Tok.FillSmoke` | `WaveeShell.cs:2285` | `Shell/Shell.UI.cs` |
| `PluggedLoopHz` / `BatteryLoopHz` / `InactiveFrameIntervalMs` / `DebounceSeconds` / `PollMs` | `30f` / `24f` / `33` / `2.0` / `2000f` | `AmbientPowerPolicy.cs:49`, `:51`, `:54`, `:45`, `:66` | `Platform/Design.cs` §cadence (the four) + `Platform/Platform.cs` (the poll) |

**Asset inventory this chapter still owns** (the taskbar/app icons moved to ch 14):

```
assets/covers/cover00.jpg … cover15.jpg                      CoverCount = 16, wrapped by index   FakeData.cs:11, :24-32
assets/spotify/home.json · playlists.json ·
                artist-maroon5.json · icedamericano.json      the --fake export                   Services.cs:595
assets/loc/en-US.json · nl.json · ko-KR.json                  the locale tables; two are DISABLED SettingsPage.General.cs:41
```

---

## 4. Colour & material

Sections 4.1–4.10 are the **light-theme contract**, surface by surface, for everything that computes a colour instead
of reading a token. It is the "light arm" row the completeness critic asked each chapter to add — collected here
instead, because the eight surfaces share one problem and eight separate paragraphs would state it eight ways.
4.11–4.15 are the rest of this chapter's material, and **4.16 is the correction**: the first pass walked the eight
surfaces a sibling chapter had already named rather than the grep, and missed five.

### 4.1 Why light theme needs its own contract at all

Every token read is correct in both themes by construction: `Tok.TextPrimary` is black-ish in light and white-ish in
dark, and a surface that reads it inherits that for free. The failure mode is **arithmetic**. The moment a surface
computes `someColour with { A = 0.3 }`, or lerps toward a near-black plate, or picks a gradient stop, the theme stops
being a lookup and becomes a *second design that nobody drew*. Chapter 03 §1138 already flags the honest consequence
for one of them — "whether the light-theme hue is 'imperceptible' remains the author's, unverified". This section is
where that becomes checkable.

**The readability floor, stated once.** On every surface below the requirement is the same and it is not a contrast
ratio on the *derived* colour — it is: **the ink that sits on this surface must clear its own ratio against the
surface's WORST case.** For a wash at α, the worst case is the ground plus the wash at full α. For a scrim over
artwork, the worst case is a white frame. Where the derived colour is decorative (a wash, a tint) and carries no ink
of its own, there is **no floor** — only an upper bound, which is "it must not become a smudge" (`ShellWashGeometry.cs:30-32`).

### 4.2 The shell tint — light is a different SOURCE, not a weaker alpha

```
  dark   WaveePalette.TintedDark(artScheme)                  @ A 0.14
  light  WaveePalette.Lift(WaveePalette.ToColor(scheme.TextBase)) @ A 0.05     CoverPaletteLeaves.cs:242-246
```
Dark tints a *dark derivative of the scheme*; light **lifts the scheme's own text colour**. The alphas differ by
2.8× because the sources differ, not because light is quieter. The layer composites over live Mica, so the real
ground is the user's wallpaper — which is the reason both arms are whispers.
**Floor:** none (no ink sits on the shell tint; every chrome control brings its own ground).
**Upper bound:** at `A 0.05` over light Mica the tint must remain *perceptible as a hue* on a plain wallpaper. This
is the one claim in this section that a parity run can falsify — parity 16.

### 4.3 The page tone plane — and the light-theme consequence the code states out loud

```
  colour   WaveePalette.PageTone(scheme, theme)   — the same call in both arms   CoverPaletteLeaves.cs:143-144
  alpha    dark 0.20   light 0.30                                                 :139
```
The comment at `:126-138` is the design record and must survive the port verbatim. The ratchet was
1.0 → 0.72 → 0.45/0.90 → 0.20/0.30, every step a user report, and the direction never reversed. The light arm gave
up its old `0.90` because "a near-white whisper tone (L 0.94, S ≤ 0.16) over near-white Mica has almost no hue to
protect in the first place".

> **The stated consequence: in light theme the record's hue is close to imperceptible, and a busy wallpaper shows
> through.** Light identity is carried by the **chrome accent** — the Play capsule, the accent rule, row chrome —
> not by the page ground.

A light-theme parity run that reports "the album page has no colour" is reporting the design. What it must check
instead is that the **accent** carries the record's identity in light (parity 17), and that raising the pair — never
the clamp — is the documented dial if the page should take more colour again.

### 4.4 Home's three radial washes

```
             origin α (light / dark)    geometry (window fractions)        ShellWashGeometry.cs:24-37
   Hero      0.055 / 0.10               c(0.06, 0.00) r(0.74, 0.92) f 0.62
   Weekly    0.050 / 0.085              c(0.92, 0.10) r(0.58, 0.78) f 0.64
   Mix       0.050 / 0.085              c(0.58, 1.00) r(0.90, 0.70) f 0.66   ← clipped at the dock line
```
"Dark carries roughly twice the light strength: the same colour reads far weaker over the dark ground, and the light
ground has less headroom before a wash turns into a smudge" (`:30-32`). The geometry is **identical** in both themes;
only α moves. Both arms carry the wash's own RGB on the transparent stop — `ColorF.Transparent` is premultiplied
black and would drag the hue toward black across the whole falloff (`ShellMaterialLayer.cs:121-126`).
**Floor:** none on the wash. But the *chrome* sits on top of it — the sidebar's row ink, the toolbar's glyphs — and
those are token reads over a ground that just gained 5.5 % of a saturated hue. The check is that the sidebar's
secondary text still clears its ratio over the worst wash pixel (parity 19).

### 4.5 The artwork placeholder and the two gradings

```
  light  ArtworkPlaceholderLight  #F2F2F2        Surfaces.cs:58
  dark   ArtworkPlaceholderDark                   :57
  graded covers mix toward TryGetTint(url, light, …) — the LIGHT half in light, the DARK half in dark  :85-89
  "Light theme only accepts a light grading; a dark-only entry keeps the neutral"                       :76
```
And the rule that is genuinely counter-intuitive, because it is deliberately inverted:

```
  SchemeFor(url)        follows the theme        light theme ⇒ the LIGHT grading     Surfaces.cs:142
  ChromeSchemeFor(url)  is deliberately OPPOSITE                                      :145-154
```
> "a light page wants the DARK grading's chroma for its one solid CTA, and a dark page wants the light grading's
> softness so the plate does not glow"

A 0.3 re-author who collapses the two calls into one makes every Play capsule washed out in light and loud in dark.
**Parity 21 is exactly this check**, and it is the single easiest light-theme regression to introduce.

The **stage** overrides the polarity a third way: `StageInk.ArtStandIn` passes `light: !IsDark`, so the stand-in
follows the *stage's* polarity rather than the page's (`StageInk.cs:76`). Three different answers to "which half",
all correct, all for stated reasons.

### 4.6 The stage's ink ladder — the one surface with a whole second arm

`StageArm.For(theme)` is the single theme branch on the immersive stage, and the file's own doc is the contract
(`StageArm.cs:6-34`). Four claims a port must keep:

1. **The ground is mirrored; the alphas are shared.** "Every light rung reads its opacity off its dark twin rather
   than restating it, so the two arms cannot drift into two different ladders" (`:26-28`). What differs is only what
   the alpha is applied to.
2. **Light ink is `Tok.MediaStage`, not `Tok.TextPrimary`** — an on-media token, "because the stage's ink must stay
   opaque (the theme text rung is black @ 0.894, which is a second, quieter ladder)" (`:55-57`).
3. **The scrim alphas need no light arm at all.** "The sRGB transfer curve does the work: mixing toward black at a
   partial alpha destroys far more perceptual luminance than mixing toward white, so the light arm's alpha'd ink
   clears a HIGHER contrast ratio than the dark arm we already ship" (`:30-33`). `StageLayout` stays one set of
   numbers. **This is the one place in the app where the light arm is provably safer than the dark one** — so the
   light-theme parity item for the stage is a *readability* check, not a contrast measurement (parity 22).
4. **The dark arm delegates to `WaveeOnMedia` verbatim, and a test pins it** (`:19-21`) — "dark theme is
   byte-identical to what shipped" is an executable claim. Keep the test with the code.

`WaveeOnMedia` itself must **not** become theme-aware: it is the ladder for everything that paints on actual
artwork — cover thumbnails, row FABs, the bar — and on a cover thumbnail white-on-scrim is right in both themes
(`:22-25`).

### 4.7 Lyrics ink — a MODE, and why that is load-bearing in both arms

```
  LyricsInk.Theme (rail)   Primary Tok.TextPrimary      Secondary/Tertiary the theme rungs   LyricsInk.cs:33-40
  LyricsInk.Media (stage)  Primary StageInk.Ink         …  StageInk's ladder
  the resync chip: Plate / PlateHover / PlatePressed / PlateStroke / RingFill / RingTrack all follow the seam  :47-58
```
The struct holds a **bool** and resolves the token at the point of consumption, so a live theme flip re-reads
correctly on either surface; "a `ColorF` frozen into a component's constructor would not, because component props
freeze at mount" (`:17-22`). The file also records the failure chain the seam removed: the view always painted
`Tok.TextPrimary`, "which is precisely why the stage's base scrim had to flip white in light theme to keep the lyrics
readable, which is what forced every region of stage chrome to bring its own boxed dark veil, which is what made the
surface a two-world collage" (`:9-14`). **That paragraph is the light-theme design record for chapter 22** and must
move with the code.
**Floor:** the stage's blur σ and backdrop (ch 22, ch 30 W25) change what the ink sits on. The light arm's backdrop
is the light veil, so the σ that reads as "soft" in dark reads as "washed" in light — UNVERIFIED whether ch 22's
values were tuned in both themes. Parity 23 asks for it.

### 4.8 The artist blend wash

```
  dark   BackgroundDark(pagePal ?? Neutral)                 stops 0.30 → 0.08 → 0   CoverPaletteLeaves.cs:182-193
  light  Lift(Accent(pagePal)) ?? Tok.AccentDefault         stops 0.20 → 0.06 → 0
```
Two sources again, and two fallbacks: light falls back to the **app accent**, dark to the **neutral scheme**. On an
ungraded artist the light page therefore shows the product's blue and the dark page shows grey. That asymmetry is
intentional — light has no dark ground to hide an un-graded hero against — but it is **UNVERIFIED** whether it was
chosen or inherited. Parity 29 records the frame either way.

### 4.9 The three surfaces with no light arm, and the rule that makes that correct

| surface | why no arm | what the port must not do |
|---|---|---|
| the nine **deck faces** (`Features/Player/Deck/Faces/*.cs`, zero `ThemeKind` hits) | they are **physical materials** — a cassette shell, a VU meter's glass, a CD's iridescence, an LCD. A cassette is not lighter in daylight; a "light cassette" is a second product | do not add a theme branch. Do check that the **frame around** the face — the hero card's stroke, shadow and corners — is a token read (ch 21, ch 23) |
| **Liked's generated covers** (`LikedCoverLeaves.cs:93-107`, near-blacks `#17171C` / `#1B1B20` lerped 40 % / 45 % toward the cover tint) | they are **artwork**. Real album art does not lighten in light theme either | do not lighten the plate. Do check the on-cover wordmark is on-media ink — if it ever became `Tok.TextPrimary` it vanishes (ch 07) |
| **video scrims** (`Tok.ScrimTop` / `ScrimBottom`, `static readonly GradientSpec` on the token class — `Tokens.cs:376-386`) | they sit over **video**, and the ink over them is on-media, explicitly: "this sits on a dark scrim over video in BOTH themes, so a light-theme [rung would be invisible]" (`InWindowVideoPip.cs:243`) | do not give them palette arms. The invariance is structural — they are not palette entries |

**The rule behind all three: a scrim over MEDIA is theme-invariant; a scrim over the app's own ground is not.** The
immersive stage is the single surface that is both — it paints a scrim over artwork that *is* the app's ground — and
that is precisely why it, and only it, gets `StageArm`.

### 4.10 Search highlight — the one that does NOT belong in this list

`Tok.AccentSelectedTextBackground` behind the matched run, `Tok.TextOnAccentSelectedText` on it, padding 3/1,
`Radii.Control` (`SearchHighlight.cs:44-52`). Both are palette entries with real arms, and
`AccentSelectedTextBackground` is `#0078D4` in **both** themes (`PaletteBuilder.cs:253`, `:341`) — so the pill is the
same blue on both grounds and the ink on it is the inverse text rung. Nothing to add, nothing to verify beyond the
token read. It appears here only because the completeness critic listed it; **the finding is that it is not a
derived-colour surface.**

### 4.11 The material layer's own rules (they are colour decisions, not plumbing)

1. **"No colour" is a real colour.** `NeutralGround = WaveeColors.ShellGround with { A = 0.03f }` — never
   `ColorF.Transparent`. The recorded finding: "cross-fading INTO or OUT OF `ColorF.Transparent` (implicitly
   premultiplied BLACK, RGB all zero) drags the interpolated colour toward black for the whole length of the ramp —
   which is exactly what read as the shell tint going 'neutral AND DARKER' for several frames at almost every
   navigation" (`ShellMaterialLayer.cs:80-88`). `CoverShellTintBinder` uses the same value for its "definitely no
   colour" writes, so the two can never disagree about what neutral looks like.
2. **The washes stop at the dock line.** The Mix wash is bottom-anchored with its ellipse centre at window
   `y = 1.00`, so its peak alpha landed exactly across the dock band — "which is what read as 'the dock has a pastel
   gradient'. It never had a gradient; it had the shell's" (`:59-71`). The fix is a host box inset by
   `PlayerDock.Reserve`, chosen as a **margin** rather than a re-anchored ellipse because the placement constants are
   node-relative ratios and subtracting a fixed 72 DIP from the height would make them viewport-dependent.
3. **The flat tint stays full-bleed** — "a uniform low-alpha scrim with no peak to land anywhere, so the dock
   carrying it is the page's colour reaching the whole window, which is the intent" (`:72-74`).
4. **The base layer is live Mica, full stop.** The shell root paints nothing; `WaveeColors.ShellGround` is the
   no-Mica fallback and the flatten base for opaque floating surfaces, not something painted here (`:8-10`).

### 4.12 The dialog card is the one deliberately opaque surface

`SolidBackgroundFillColorBase` + a 1 px `SurfaceStrokeColorDefault` border over a `#4D000000` smoke (engine
`ContentDialog.cs:17-19`). It is the one place in Wavee that is deliberately opaque over the Mica stack — the shell's
whole material argument (ch 18 §0.2) stops at a modal. Do not make dialogs translucent to "match". The setup plate
reproduces the same chrome tokens at a different size (`SetupDialog.cs:13-16`; `:132` uses `Tok.FillLayerAlt` for
`ContentDialogTopOverlay`), so a 0.3 change to a dialog token must change both files — two files, one look.

### 4.13 The toast card's colour is entirely the severity table

Four grounds, four plates, one inverse foreground (`SeverityVisuals.cs:19-26`). Wavee contributes **nothing** to a
toast's colour except the severity byte. That is the design: it is why toast and InfoBar cannot drift, and it is why
§6.4's inventory can be a table of severities rather than a table of colours.

### 4.14 The insertion preview is the third accent role

The one place `Tok.AccentDefault` is used as a 1-px border on a content card (`PlaylistInsertionPreview.cs:75`). It
is how "these rows are not in the list yet" is said. That spend is the third accent role declared in
`00-design-system.md` W3; it must not be re-spent on a hover state nearby.

### 4.15 The drop scrim is the engine's, clipped by us

Wavee never picks its colour; it publishes a rect (`WaveeShell.cs:414-421`). The scrim's own colour belongs to
`00-design-system.md`. The **scope** is the visual decision (§2 W21).

---

### 4.16 The five derived-colour surfaces §4.1–4.10 missed, and the one rule they share

§4's first pass walked the eight surfaces some chapter had already named. The grep that should have opened it —
`Tok.Theme ==` / `ThemeKind.Light` / `ThemeKind.Dark` across `src/apps/Wavee`, **50 hits in 15 files** — turns up
five more. §2 W5B draws them; this is what they mean for the contract.

| surface | the arithmetic | the floor |
|---|---|---|
| **the concert plates** (`ConcertUi.cs:164`, `:233`, `:261`, `:355`, `:914`, `:962`) | three hand-authored near-blacks (`#1C1C1E` / `#1B1B1D` / `#141416`) vs `Tok.FillCardSecondary`, then `Lerp(plate, accent, …)` at **two different per-theme ratio pairs**: `0.30/0.18` (`:262-263`) and `0.50/0.18` (`:356-360`) | the concert TITLE sits on the 192-DIP band, so the band's light arm is the one that carries a floor. The file already lost this argument once and wrote it down (`:356-359`) |
| **the accent card fills** (`MediaCard.cs:48-56`) | `Lerp(FillCardDefault, accent, 0.12/0.08)` rest, `Lerp(FillControlSecondary, accent, 0.18/0.12)` hover | the card's title and subtitle are token reads over it — the most-repeated ink/ground pair in the window |
| **the now-playing hero wash** (`NowPlayingPanel.cs:203-209`) | `Lerp(FillCardSecondary, Lift(Accent(SchemeFor(url))) ?? AccentDefault, 0.18/0.10)` — three derivations stacked, the longest chain in the app | ch 21's ink sits on it |
| **the nav-preview accent seed** (`NavPreview.cs:64`) | `TryGetScheme(url, theme == Light)` → `WaveePalette.Accent` → packed ARGB | none (it seeds a preview's own gradient) |
| **the search hero** (`SearchHero.cs:39-41`) | `TryGetScheme(url, theme == Light)` → `WaveePalette.ChromeAccent` | ch 13's hero ink |

**The rule all five share, and it is the one §4.1 should have opened with.** *A `Lerp` toward a plate is a light-arm
problem even when the plate is a token.* `Tok.FillCardDefault` and `Tok.FillCardSecondary` already flip with the
theme, so the **colour** is handled — but the **ratio** is not, because the two plates sit at opposite ends of the
luminance range and the same fraction of a saturated hue reads as a tint on one and a pastel on the other. Every one
of the five carries two ratios for exactly that reason. A port that keeps one is the failure `ConcertUi.cs:356-359`
already documents, at five more sites.

**And a correction to §4.5's "three different answers to which half".** There are **five** — `SchemeFor` (follows the
theme), `ChromeSchemeFor` (deliberately opposite), `StageInk.ArtStandIn` (follows the *stage*), plus `NavPreview`
and `SearchHero`, which follow the theme like `SchemeFor` but then take *different roles* off it (`Accent` vs
`ChromeAccent`). The last two are not new polarities; they are the reason a 0.3 helper that folds "which half" into
one call must keep the **role** as an argument, or these two change colour with no diff to show for it.

## 5. Motion

### 5.1 The page-swap matrix, as motion rows

| pair | card channel | band channel | material channel | reduced motion |
|---|---|---|---|---|
| Home → album | fade-through FWD: exit 120 ms `EaseOut`; enter `Expressive.Fast` `SmoothOut` from 90 ms, Dx `+DistBase` | none (neither is a family) | wash → tint, 250 ms brush, held while ungraded | the engine's reduced arm on both halves; the brush fade is a colour ramp and is **kept** |
| browse → playlist | fade-through FWD | family → none: opacity 1 → 0 over **120 ms** `FluentAccelerate`, `KeepFade` | ordinary hand-over | band fade kept (`ReducedMotionPolicy.KeepFade`) |
| album → artist | fade-through FWD | none | tint → tint, 250 ms, colour → colour | as above |
| disco → artist (Back) | fade-through BACK: enter Dx `−DistBase` | none | **reactivation claim** (`UseActivation`) then 250 ms | as above |
| settings → Home | fade-through in the written direction | none | tint → wash: the tint fades to `NeutralGround` while three keyed wash layers **Enter** (`WashFade`, opacity only) | `WashFade` is **null** under reduced motion (`ShellMaterialLayer.cs:34`) — the wash swaps instantly |
| module ↔ anything | `PageSlideSafe*`: Position only, symmetric ±`DistBase`, `Expressive.Fast` `SmoothOut`, no delay either half | as the families dictate | ordinary | the engine's reduced arm; a position-only recipe under reduced motion becomes a cut |
| module ↔ anything, **Neutral** | **null** — an honest CUT (`PageNavMotion.cs:99`) | as above | ordinary | already a cut |
| any → any, Neutral | `MotionRecipes.PageFade` (opacity only) | as above | ordinary | kept |
| back to a parked page | fade-through BACK, **plus** `SuppressLayoutTransitionsOnActivation: true` — the returning page re-lays-out with **no** transition (`ContentHost.cs:108`) | store is route-keyed: the masthead resolves live immediately, no fade | reactivation claim | as above |
| a tab's first page | the ordinary recipe for its direction (`FirstActivation`, not a `PageSlot`) | as the families dictate | first claim | as above |

**The one rule that explains the table.** The card is fast (120 / 90) because two full-bleed pages must never share
the viewport at readable opacity. The band matches the card's *exit* (120) because it is dissolving alongside it. The
material is slow (250) because it is the window's light, and light does not cut. Three clocks, three reasons.

### 5.2 The material's own motion

| what | recipe | duration / easing | source |
|---|---|---|---|
| tint → tint (or → neutral) | a **brush** transition on an always-mounted node | `WaveeMotion.Standard` 250 ms | `ShellMaterialLayer.cs:96-98` |
| wash layer arriving / leaving | a **mount** cross-fade: the layer is keyed on its artwork, so a re-grade Exits the old node and Enters the new one over the same pixels | the engine's `EnterExit` opacity | `:34`, `:108-110`, `:129-130` |
| a theme flip on the same artwork | **no motion at all** — the node keeps its key and simply re-records its stops | — | `:108-110` |
| the page tone plane | a bound `Fill` with `BrushTransitionMs` — a grading arrival cross-fades instead of snapping | `WaveeMotion.Standard` | `CoverPaletteLeaves.cs:110-116` |
| washes OFF (the preference) | the plane has **no Fill at all**, so its 250 ms ramp does **not** run; the shell tint eases to `NeutralGround` normally | 250 ms on the shell only | ch 30 §4.1, §5.2 |

### 5.3 Focus motion — there is none, and that is the row

| what | recipe |
|---|---|
| the focus ring appearing | **instant**. The recorder emits it on the frame the flag is set; there is no fade, no grow, no travel. |
| the ring moving between nodes | **instant**, at both ends. `SetFocus` marks the old node `PaintDirty` so its ring disappears in the same frame the new one appears (`InputDispatcher.cs:3776-3781`). |
| the roving stop moving | **instant**, and specifically *not a re-render*: the stop moves in place (`ItemsView.cs:1574-1580`). |
| a focus-driven scroll | the list's own bring-into-view; the engine finds the nearest vertical scrollable ancestor (`InputDispatcher.cs:506`). Not this chapter's. |

A travelling focus ring is a WinUI-Reveal-era idea and the app does not have one. Adding it would put a moving
visual on the keyboard path — the same objection §0.30 makes about the drag chip.

### 5.4 The ambient cadence block (the named block `00-design-system.md` §5 is missing)

| what | plugged | on battery | unfocused window | reduced motion | source |
|---|---|---|---|---|---|
| a cadence-less `loop: true` slab row | **30 Hz** | **24 Hz** | the row still runs; the *host* floors animation-only frames at **33 ms** | the row's own still-array / `ReducedSnap` arm decides — the cadence is unchanged | `AmbientPowerPolicy.cs:49`, `:51`, `:54` |
| the power poll | 1 read / 2 000 ms | same | same (auto-paused while parked) | — | `:66`, `:58-59` |
| a cadence change | applied only after the new reading has held **2 000 ms** | same | same | — | `:45`, `:95` |
| launch | applied **immediately**, no debounce | same | — | — | `:78-80` |
| `Cadence.Display` rows (springs, live drags, `LoginView`'s 1 100 ms marquee) | display rate | display rate | host-throttled | per-row | `LoginView.cs:166` |
| `Cadence.At(hz)` rows | the declared hz | the declared hz | host-throttled | per-row | engine |
| `UseInterval` sources (deck tick, `DeckClock`, analyser) | **not governed** — self-paced | same | paused when parked | per-row | `AmbientPowerPolicy.cs:37-40`, `:126-131` |

**Every chapter whose §5 carries a `loop: true` row must state which cadence it declares.** The ones that exist
today, so the audit can be done mechanically:

| chapter | loop | period | declared cadence |
|---|---|---|---|
| 00 design system | skeleton shimmer; cover placeholder | — | **default** (30 / 24) |
| 02 cards & controls | browse tile wobble ×3 | 11 000 / 13 000 / 17 000 ms | **default** — `ConcertUi.cs:1013-1015`, `:1041-1043` |
| 07 liked songs | cover wall drift | 92 000 ms | **default** — `LikedCoverTreatments.cs:438`, `:742` |
| 07 liked songs | three marquee bands | 60 000 / 74 000 / 66 000 ms | **default** — `:553`, `:747-749` |
| 17 concerts | ground drift + two arc sweeps | 19 000 / 9 000 / 7 000 ms (coprime on purpose) | **default** — `ConcertUi.cs:1010-1012`, `:1035-1037` |
| 20 player bar | buffering spinner; seek playhead; **the Reconnecting indeterminate top edge** | — | **default** — `PlayerBar.cs:627` |
| 21 rail / NPV | the now-playing equalizer | — | **default** |
| 22 lyrics | the karaoke wipe | — | **default** |
| 23 deck faces | `CanvasDeck`'s four drift channels | per-face | **default** — `CanvasDeck.cs:131-134` |
| 23 deck faces | `DeckClock`'s tick | `TickMs`, which **references** `PluggedLoopHz` | **self-paced `UseInterval`** — not governed |
| 28 setup | `LoginView`'s hero marquee | 1 100 ms | **explicit `Cadence.Display`** — `LoginView.cs:166` |
| 29 (this) | the toast strip's countdown | 5 000 ms per card | **a timer, not a loop** — `HostTimerQueue`, `Toast.cs:240` |

A chapter that adds a loop and does not name its cadence is adding an ungoverned one. That is the check.

### 5.5 Live preference flips — what animates when a toggle moves

Chapter 30 now carries its own §5 (§5.1 the five epoch bumps, §5.2 per preference, §5.3 the remounts, §5.4 reduced
motion), so this table is no longer the only home for it — see §11 audit 26, which retracts the earlier claim that
30 had no §5. It is kept here in the **cross-cutting** form: what a flip does to the surfaces *this* chapter owns.

| preference | the shell material | the masthead | a page swap in flight | focus |
|---|---|---|---|---|
| **Theme** | re-themed in place over 250 ms; the wash layers keep their keys and simply re-record their stops (no remount) | unaffected (token reads) | a swap in flight continues; the theme epoch bumps and every mounted render re-runs | unaffected — the ring's tokens flip with the theme |
| **Theme, the OS-follow arm** | the same 250 ms — but **only when `Tok.Epoch` actually advanced**. Windows broadcasts `ImmersiveColorSet` without an effective palette change, and requesting a transition for it "still forces RethemeAll, re-rendering the entire mounted app for identical colors" (`WaveeApp.cs:56-59`). The guard is the cross-cutting rule: **a re-theme is a whole-app re-render, so it must be epoch-gated, never event-gated.** | — | — | — |
| **Theme, the mode** | `0 System · 1 Light · 2 Dark` (`WaveeTheme.ApplyThemeMode`, `:19-33`). System re-reads the OS theme **and the accent ramp** — so a System install can change accent without any theme change at all, which no §5 row in any chapter covers | — | — | — |
| **Colour washes off** | eases to `NeutralGround` over 250 ms via a **definite** publish, never a cut to transparent | unaffected | unaffected | unaffected |
| **Zoom** | the layer's bound `Width`/`Height` props re-evaluate off the viewport; **no re-render**, and no animation | re-lays out; no transition | the swap's Dx is `Expressive.DistBase` in DIP, so a zoom mid-swap changes the travel distance — UNVERIFIED whether that is visible in practice | unaffected |
| **Reduced motion** | `WashFade` becomes **null** (`ShellMaterialLayer.cs:34`) — washes swap instantly; the tint's brush ramp is **kept** | the band keeps its fade (`KeepFade`) | the card's recipes take the engine's reduced arms | unaffected |
| **Language** | — | the trail re-resolves its labels on the next render | — | — (a restart is required anyway) |

### 5.6 Everything else this chapter owns

| what | recipe | duration / easing | source |
|---|---|---|---|
| dialog **open** | scale 1.05 → 1.0 **and** opacity 0 → 1 | 250 ms scale on `(0,0,0,1)`; 83 ms opacity, linear | engine `ContentDialog.cs:21-24` |
| dialog **close** | scale 1.0 → 1.05 + opacity 1 → 0 | 167 ms scale; 83 ms opacity | `:21-24` |
| the dialog smoke | fades with the overlay's `PopupChrome.Modal` | engine-owned | `:186` |
| a **toast** entering / leaving the strip | the strip's own recipe (ch 19 owns it) | — | engine `Toast.cs` |
| a **toast refresh** (a dedupe hit) | **no motion** — the card stays put and its countdown restarts | — | `Toast.cs:169-193` |
| setup **cover scrim** (post-auth wizard pages) | `Tok.FillSmoke` opacity cross-fade, driven by `SetupSession.Covering` | `WaveeMotion.Standard`; **0 ms under reduced motion** | `WaveeShell.cs:2278-2281` |
| drag chip **pickup flash** | Rotation 4° + scale 1.02 → flat | 150 ms | `DragChip.cs:73-80` |
| drag chip **enter** | Sx/Sy 0.92 → 1, opacity 0 → 1 | engine `EnterExit` | `DragChip.cs:200` |
| drag **spring-load** | a dwell, not an animation | 500 ms hold | `WaveeResourceDrag.cs:263` |
| the **file-drop cue** | bound opacity 0 ↔ 1 | the box's own opacity transition; **compositor-only** | `WaveeShell.cs:1422` |
| **insertion gap** open/close | the framework's `InsertionOptions.GapPreview` | engine | `PlaylistInsertionPreview.cs:9-11` |
| a **sidebar seam drag** | every layout transition in the window is **snapped** for the drag's duration | `Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, …)` | `WaveeShell.cs:134-135`, armed at `:540` |
| an **Auto-zoom re-resolve** | **500 ms of nothing**, then a snap | `ZoomAutoDebounceMs` | `WaveeShell.cs:589-591` |
| a **cadence flip** | no transition; loops simply advance at a new rate from the next frame | a 2 s debounce in front of it | `AmbientPowerPolicy.cs:45` |
| the **Reconnecting** top edge | `ProgressBar.Indeterminate(L.TopEdgeWidth)` — a sweeping band at the bar's top edge, "a browser-style global-activity cue, kept SEPARATE" from the seek bar | the engine's indeterminate cadence | `PlayerBar.cs:21`, `:627` |
| a **network state change** | **no transition on the chrome chip** — the profile chip is replaced outright by the accent button | — | `MergedChromeRow.cs:201-219` |

**Reduced motion.** Four rows here have an explicit reduced arm and all four are already written: `SetupCoverScrim`
drops to 0 ms (`WaveeShell.cs:2278`); `WashFade` becomes null (`ShellMaterialLayer.cs:34`); the masthead band keeps
its fade deliberately (`ReducedMotionPolicy.KeepFade`, `ShellMastheadBand.cs:23-26`); and the layout-transition
suppression during a seam drag is *stronger* than reduced motion (it snaps everything, deliberately).

### 5.7 Reduced motion is a VALUE, never a branch — the rule no chapter's §5 states

This is cross-cutting and it has no owner, so it lands here. A repo-wide grep finds **~40 `Motion.ReducedMotion`
reads across 25 files**, and every one of them is a value read during `Render`, not a conditional around a hook. The
reason is written down twice:

> "REDUCED MOTION IS A VALUE, NEVER A BRANCH (the engine's animation canon, and the rule a previous stagger attempt
> broke: gating an entrance HOOK on `Motion.ReducedMotion` changes the hook COUNT between renders and **crashes the
> reconciler the moment the flag flips mid-session — a resize grip flips it**)." (`WaveeMotion.cs:88-91`)

So: `DelayMs(index)` reads the flag and returns `0f` (`:122`); `ScaleTier.Hover` / `.Press` return `1f`
(`:179`, `:182`); `SetupCoverScrim` computes `ms = ReducedMotion ? 0f : Standard` (`WaveeShell.cs:2278`);
`WashFade` is a `static EnterExit?` **property**, read as a value, with the comment "Read as a VALUE, never a hook
branch" (`ShellMaterialLayer.cs:32-34`). Not one of them is an `if` around a `Use*` call.

**And the app-side suppression nobody else documents.** The interaction scale cues are the ONE animated channel the
engine does not police:

> "InteractionAnim.HoverT/PressT are seeded by `SeedEased()`, which has no policy parameter, and `SceneRecorder.cs`
> composites `1 + (HoverScale-1)*HoverT` unconditionally. So a reduced-motion user WOULD still get every scale cue,
> fully eased. … the correct minimal fix is app-side and lives HERE: the tier accessors return `1f` under reduced
> motion, which makes the recorder's `MathF.Abs(isc - 1f) > 0.0008f` test fail and skips the transform entirely."
> (`WaveeMotion.cs:17-29`)

Three consequences for 0.3, and each is a thing a re-author gets wrong by default:

1. **`Platform/Design.cs` must keep the tier accessors as accessors**, not as consts. A `const float Hover = 1.02f`
   is a silent reduced-motion regression on every hover in the app.
2. **The suppression is NOT double.** "Nothing else nulls these" (`:28`). A port that also nulls the recipe gets a
   dead hover for everyone.
3. **Delete the app-side reads only when the engine grows a policy on the interaction channels** — the file says so
   itself (`:28-29`). That is a note to carry, not a cleanup to perform.

**The exception that proves it.** `PlaylistInsertionPreview`, `DragChip`'s pickup flash and the drag chip's travel
have **no reduced arm at all** — a drag is a direct-manipulation gesture where the moving thing is the thing the
user is moving. That is correct and it is stated nowhere; it is stated here now, so a reduced-motion audit does not
file it as a miss (parity 120).

---

## 6. Interaction

### 6.1 Keyboard

| chord | what | where |
|---|---|---|
| `Tab` / `Shift+Tab` | moves focus **with** the ring (`visual: true`) | engine `InputDispatcher.cs:3677` |
| `Tab` into a list | lands on the **one** roving stop — the keyboard-current item, or item 0 | `ItemsView.cs:393`, `:1580` |
| `↑ ↓ ← →` inside a list / grid | moves the roving stop **in place**, with the ring; 2-D walks are primary-axis-distance dominant, cross-axis breaks ties | `ItemsView.cs:1574-1580`, `InputDispatcher.cs:3681` |
| `Escape` (unhandled) | clears focus **and** the ring, and runs routed LostFocus cleanup | `InputDispatcher.cs:3577`, `:3781` |
| `Enter` in a dialog | invokes `DefaultButton` **from anywhere inside the card** | engine `ContentDialog.cs:264-272` |
| `Escape` in a dialog | closes with `ContentDialogResult.None` | `:25-27` |
| `Tab` / `Shift+Tab` in a dialog | **cycles** inside the focus trap; initial focus is the accent button | `:26-28`, `:256-260` |
| `Escape` during a drag | the engine cancels the session; the chip exits | engine |
| `Escape` in fullscreen video / immersive lyrics | exits — and the surface's own `OnFocusChanged` guard means the key is always reachable because focus is never left null | `VideoFullscreenSurface.cs:170-180` |
| `Ctrl +` / `Ctrl −` / `Ctrl 0` / `Ctrl+wheel` | zoom — and each one flips the mode out of Auto via the disagreement check | `WaveeShell.cs:616-628` |
| `Ctrl+F` | omnibar caret (`Ctrl+K` opens the command palette) | `WaveeShell.cs:94`, `:823-825` |
| `F11` | full-screen video | `WaveeShell.cs:943` |

**A bound action has no chord of its own.** It is invoked from a sidebar row, the command palette
(`WaveeCommands.cs:177`, `:243`) or a customizer preview, and every path goes through `Execute` — so the
confirmation gate cannot be routed around by a keyboard path either.

### 6.2 Pointer

| gesture | what |
|---|---|
| click anything focusable | focus moves **without** the ring (`visual: false`, `InputDispatcher.cs:968`) |
| click a row in a virtualized list | "a press on a non-current container can't take pointer focus at the dispatch edge" — the roving stop follows on the click's own path (`ItemsView.cs:1433`) |
| click a toast's action button | runs `OnAction`; the card closes |
| click a toast's ✕ | closes that card only; the overflow queue advances |
| hover a now-playing title | the Marquee runs (PingPong, 18 dip/s, 10 s cycle, 2.5 s end pause) — **hover only** |
| drag a file over the window | the spotlight scrim + the pill (§2 W21); dropping plays it |
| press-and-drag a detail hero cover | lifts the whole **entity** (`WaveeDetailDrag.Hero`, `WaveeResourceDrag.cs:386-393`). It coexists with the editable-cover **file** drop target underneath: opposite directions, two specs, two nodes (`:378-383`) |
| hold a drag over a container | spring-load at 500 ms (§2 W19) |
| drop on a refusing target | the target is transparent; discovery walks to an accepting ancestor. The chip says why (§2 W20) |
| click the offline **Reconnect** chip | `Bridge.SignIn` — the ONE verb that starts a real resume. **Never** `Session.ConnectAsync` on the fake inner session: doing that "signed the user in as 'Wavee Listener' over a real Spotify resume that was already in flight" (`MergedChromeRow.cs:195-200`) |

### 6.3 Focus and accessibility

The ring's rules are §0.1–5 and §2 W1–W4; the restoration matrix is §2 W3. Three things belong here rather than
there:

1. **What announces.** `Announcer.SayThrottled` at chokepoints, always behind `Announcer.IsAvailable` so no line is
   composed when nothing is listening: a rootlist drop (`WaveeResourceDrag.cs:464`), a library edit
   (`LibraryBridge.cs:290-292`), a folder create (`FolderActions.cs:275-277`), the command palette's result count
   (`WaveePalette.cs:106-112`). `Announcer.Say(…, assertive: true)` is reserved for **failures** and one hard stop:
   a playlist create failure (`PlaylistCreateFlow.cs:97`), a playlist edit error (`LibraryBridge.cs:419`), and
   `Strings.Drag.StillSyncing` (`DetailTracks.cs:1351`). A track boundary announces at the app root
   (`WaveeApp.cs:643-649`).
2. **A toast is not an announcement.** Of the 107 `Toast.Show` sites, **zero** call `Announcer` at the same site.
   Where both happen it is because the *action* announced (the drop, the edit) and then separately toasted. §6.4's
   last column records it per row, and the 0.3 rule is in §6.5.
3. **The drag chip is not focusable and publishes no automation.** The announcement is the drop's toast, spoken
   through one chokepoint.

**Windows contrast themes: not supported** (§9.8). **RTL: not supported** (§0.23).

### 6.4 The toast and confirmation inventory — one row per `Toast.Show` site

107 sites across 38 files, read on 2026-09-12. `S/W/E/I` = Success / Warning / Error / Informational.
`dur` blank = the 5 000 ms default. `act` = an action button. `ann` = the site also announces (none do — see §6.3).
This is the artefact that makes Wave 4's "`Actions/*` → one table" port falsifiable: a ported table has 107 rows or
a written reason why not.

```
FILE:LINE                              copy key / source                          S  dur   act
── Actions/ContainerActions.cs ────────────────────────────────────────────────────────────────────
:98    ex.Message (a raw exception)                                               E   —    —
:102   Strings.Menu.ArtistUnavailable                                             W   —    —
:220   Strings.Menu.LinkCopied                                                    S   —    —
:314   ex.Message                                                                 E   —    —
:320   Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:323   Strings.Drag.NothingToAdd                                                  I   —    —
:346   Strings.Drag.NothingToAdd                                                  I   —    —
:351   Strings.Detail.AddedToPlaylist(targetName)                                 S   —    Go to playlist (:355)
── Actions/Extensibility/BuiltInExtensionTable.cs ─────────────────────────────────────────────────
:210   Strings.Menu.LinkCopied                                                    S   —    —
── Actions/FolderActions.cs ───────────────────────────────────────────────────────────────────────
:116   Strings.Sidebar.FolderCreatedWith(name, moves.Count)                       S   —    Rename (:120)
── Actions/LocalFileActions.cs ────────────────────────────────────────────────────────────────────
:48    ex.Message                                                                 E   —    —
:66    Strings.LocalFile.Rejected                                                 E   —    —
:73    Strings.LocalFile.NotReady                                                 I   —    —
:93    Strings.LocalFile.Rejected                                                 E   —    —
── Actions/Menus.cs ───────────────────────────────────────────────────────────────────────────────
:359   Strings.Detail.AddedToPlaylist(name)                                       S   —    Undo (:363, activity id)
:373   Strings.Detail.Edit.RemovedFromPlaylist(count)                             S   —    Undo (:377)
:430   Strings.Menu.MovedToPlaylist(name)                                         S   —    Go to playlist (:433)
:493   Strings.Detail.AddedToPlaylist(name)                                       S   —    Go to playlist (:496)
── Actions/PinActions.cs ──────────────────────────────────────────────────────────────────────────
:110   Sidebar.PinnedToastKey / Pin.Pinned + name                                 S   —    Unpin (:114)
:128   Sidebar.UnpinnedToastKey / Pin.Unpinned + name                             S   —    Undo — InsertPin(removed, at) (:132)
── Actions/PlaylistCreateFlow.cs ──────────────────────────────────────────────────────────────────
:91    Strings.Detail.Edit.CreateFailed                                           E   —    Retry — re-runs with a NEW id (:95)
── Actions/RadioLaunch.cs ─────────────────────────────────────────────────────────────────────────
:27    ex.Message                                                                 E   —    —
:31    Strings.Menu.RadioUnavailable                                              W   —    —
:37    Strings.Menu.RadioStarted                                                  S   —    Go to the radio playlist (:41)
── Actions/TrackActions.cs ────────────────────────────────────────────────────────────────────────
:41    Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:54    Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:96    Strings.Menu.LinkCopied                                                    S   —    —
:185   Strings.Menu.UriCopied                                                     S   —    —
── Actions/VideoActions.cs ────────────────────────────────────────────────────────────────────────
:60    Strings.VideoOverride.Removed                                              S?  —    Restore (:64)
:99    ex.Message                                                                 E   —    —
:151   VideoOverride.NotMp4 | (the other rejection key)                           W   —    —
:165   ex.Message                                                                 E   —    —
:174   VideoOverride.Replaced | .Attached                                         S   —    Restore previous (:179)
:194   ex.Message                                                                 E   —    —
:195   Strings.VideoOverride.Restored                                             I   —    —
── App/NotificationCenterBridge.cs ────────────────────────────────────────────────────────────────
:147   Strings.Notifications.UndoFailed                                           W   —    —
:232   plan.Body (the notification plan's own sentence)                           *   0 if plan.Sticky else 5000 (:236)   plan action (:239)
── App/PlaybackBridge.cs ──────────────────────────────────────────────────────────────────────────
:952   msg (a playback/device message)                                            *   —    Open the device picker (:956)
:1043  (a device-handoff message)                                                 *   —    Open the device picker (:1049)
:1071  Strings.Player.RemoteCommandFailed                                         E   —    —
:1089  message (a playback error)                                                 *   —    Retry, when a retry token exists (:1098)
:1228  Strings.VideoOverride.MissingToast                                         *   —    Open the override manager (:1232)
:1241  Strings.VideoOverride.UnplayableToast                                      *   —    Open the override manager (:1245)
── App/AppUpdateToasts.cs ─────────────────────────────────────────────────────────────────────────
(the plan type: Body + Sticky + action; the STATE machine is ch 14 W26-W28 / ch 28)
── Features/Detail/ArtistGalleryLightbox.cs ───────────────────────────────────────────────────────
:386   "Image exported"                              ← ENGLISH LITERAL             S   —    —
:394   "Image export failed: " + ex.Message          ← ENGLISH LITERAL             E   —    —
── Features/Detail/DetailShell.cs ─────────────────────────────────────────────────────────────────
:342   Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:347   Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:353   Strings.Detail.AddedToPlaylist(plName)                                     S   —    (see the site)
── Features/Detail/DetailTracks.cs ────────────────────────────────────────────────────────────────
:3081  Strings.Detail.AddedToPlaylist(_model.Title)                               S   —    —
:3789  Strings.Detail.Tuning.Applied                                              *   —    —
:3792  Strings.Detail.Tuning.ApplyFailed                                          *   —    —
── Features/Detail/PlaylistEditErrors.cs ──────────────────────────────────────────────────────────
:24    PlaylistEditErrorKinds.KeyFor(kind, verb)   ← ONE key table, six kinds      *   —    —
── Features/Detail/PlaylistInlineEdit.cs ──────────────────────────────────────────────────────────
:146   Strings.Detail.Edit.PickCover                                              W   —    —
── Features/Detail/PlaylistPicker.cs ──────────────────────────────────────────────────────────────
:82    Strings.Detail.AddedToPlaylist(name)                                       S   —    —
:109   Strings.Detail.AddedToPlaylist(name)                                       S   —    (see the site)
── Features/Diagnostics/PlaybackRuntimeDiagnosticsPage.cs ─────────────────────────────────────────
:309   "Diagnostics copied"                          ← ENGLISH LITERAL (dev page)  S   —    —
── Features/DragDrop/WaveeResourceDrag.cs ─────────────────────────────────────────────────────────
:433   Strings.Drag.CantMoveHere                                                  I   —    —
:472   `where` — the drop's own sentence; the ONE chokepoint that also announces  *   —    (per options)
:582   Strings.Detail.AddedToPlaylist(targetName)                                 S   —    (see the site)
── Features/Feedback/ReportChrome.cs ──────────────────────────────────────────────────────────────
:55    Strings.Common.CrashLastRun                                                *   —    (see the site)
── Features/Feedback/ReportDialog.cs ──────────────────────────────────────────────────────────────
:207   Strings.Report.Preparing                                                   I   —    —
:212   Strings.Report.TitleRequired                                               W   —    —
:471   Strings.Report.Copied                                                      S   —    —
:487   Strings.Report.Saved(path)                                                 S   —    —
:491   ex.Message                                                                 E   —    —
:519   Strings.Report.CopiedPaste(channel.PasteBox)                               S  8000  —   ← the ONE 8 s toast
── Features/Home/HomePage.cs ──────────────────────────────────────────────────────────────────────
:69    Strings.Home.PlayFailed                                                    E   —    —
:1071  Strings.Home.FacetFailed                                                   E   —    —
── Features/Library/LibraryPage.cs ────────────────────────────────────────────────────────────────
:1083  Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:1089  Strings.Detail.AddedToQueue(SongCount(n))                                  S   —    —
:1095  Strings.Detail.AddedToPlaylist(plName)                                     S   —    (see the site)
── Features/Player/LyricsInspectorDialog.cs  (developer surface) ──────────────────────────────────
:138   "Lyrics report copied"                        ← ENGLISH LITERAL             S   —    —
:180   "Lyrics evidence bundle saved"                ← ENGLISH LITERAL             S   —    —
:386   $"{p.SourceId} payload copied"                ← ENGLISH LITERAL             S   —    —
:463   "Parsed lyrics copied"                        ← ENGLISH LITERAL             S   —    —
── Features/Player/QueuePanel.cs ──────────────────────────────────────────────────────────────────
:282   Strings.Drag.CantMoveAcrossSections                                        *   —    —
:330   (a queue message, posted through acts.Post)                                *   —    (see the site)
── Features/ReleaseNotes/ReleaseNotesPage.cs ──────────────────────────────────────────────────────
:86    Strings.WhatsNew.LinkCopied                                                S   —    —
── Features/Shell/PlaybackRuntimeSetupCard.cs ─────────────────────────────────────────────────────
:467   Strings.Playback.Runtime.Ready                                             S   —    —
── Features/Shell/PlayerBar.cs ────────────────────────────────────────────────────────────────────
:678   (a transport/device message, posted through acts.Post)                     *   —    (see the site)
── Features/Shell/PlayLinkDialog.cs ───────────────────────────────────────────────────────────────
:60    Strings.Play.NoOwner                                                       I   —    —
:73    Strings.Play.NoOwner                                                       I   —    —
:84    PlayLinkActions.ErrorText(ex, Strings.Play.Failed)                         *   —    —
:191   PlayLinkActions.ErrorText(ex, Strings.Play.Failed)                         *   —    —
── Features/Shell/SettingsPage.About.cs ───────────────────────────────────────────────────────────
:157   Strings.Settings.About.DiagnosticsCopied                                   S   —    —
:176   Strings.Settings.About.NoticesMissing                                      I   —    —
:182   Strings.Settings.About.NoticesMissing                                      W   —    —   ← same key, two severities
── Features/Shell/SettingsPage.Notifications.cs ───────────────────────────────────────────────────
:119   message (the simulator's own body)                                         *  6000  —   ← the ONE 6 s toast
── Features/Shell/SettingsPage.Playback.cs ────────────────────────────────────────────────────────
:296   Strings.Playback.Runtime.ViewSignatureFailed                               W   —    —
── Features/Shell/SettingsPage.Storage.cs ─────────────────────────────────────────────────────────
:121   Strings.Settings.Storage.MetadataCleared                                   S   —    —
:272   (a deleted-count sentence, two arms)                                       *   —    —
:413   Strings.Settings.Storage.DetailsReleased                                   S   —    —
:667   Strings.Settings.Storage.CacheLocationChanged                              S   —    —
:672   Strings.Settings.Storage.CacheLocationFailed                               E   —    —
:695   Strings.Settings.Storage.AudioCacheCleared                                 S   —    —
:710   Strings.Settings.Storage.LicenseKeysCleared                                S   —    —
── Features/Shell/SettingsPage.VideoOverrides.cs ──────────────────────────────────────────────────
:240   Strings.VideoOverride.Removed                                              *   —    (see the site)
:247   ex.Message                                                                 E   —    —
:267   ex.Message                                                                 E   —    —
:275   VideoOverride.NotMp4 | (the other rejection key)                           *   —    —
:284   ex.Message                                                                 E   —    —
:287   Strings.VideoOverride.Replaced                                             S   —    —
:298   Strings.VideoOverride.ClearedAll                                           S   —    —
── Features/Sidebar/Pane/SidebarPane.cs ───────────────────────────────────────────────────────────
:2416  `sentence` — a drop result, with options from the caller                   *   —    (per options)
:2821  Strings.Detail.AddedToPlaylist(name)                                       S   —    (see the site)
── SpotifyLive/LiveSessionHost.cs ─────────────────────────────────────────────────────────────────
:637   ShowSetupToast — the playback-runtime setup prompt                         *   —    (see the site)
```

**What the inventory shows, and what 0.3 must do with it:**

| finding | count | the 0.3 rule |
|---|---|---|
| sites that show a **raw `ex.Message`** to the user | **12** (`ContainerActions` ×2, `LocalFileActions`, `RadioLaunch`, `VideoActions` ×3, `ReportDialog`, `SettingsPage.VideoOverrides` ×3, `ArtistGalleryLightbox`) | a raw exception is the *log's* voice, not the user's — `ErrorState` already makes that argument (`ErrorState.cs:19-20`). Replace each with a loc key **plus** the exception to the log. Parity 63. |
| sites with an **English literal** | **7** (`ArtistGalleryLightbox` ×2, `PlaybackRuntimeDiagnosticsPage`, `LyricsInspectorDialog` ×4) | five of the seven are developer surfaces and may stay; `ArtistGalleryLightbox`'s two are user-facing and must not. Parity 62. |
| the **same message key** reached from more than one site | `Strings.Detail.AddedToPlaylist` from **9** sites; `AddedToQueue` from **7**; `LinkCopied` from **3**; `NothingToAdd`, `Rejected`, `NoOwner`, `Play.Failed`, `VideoOverride.Removed` from 2 each | correct as-is — the dedupe key is the message, so two sites firing the same sentence REFRESH one card. Do not add a `DedupeKey` to separate them. |
| the **same key at two severities** | `Settings.About.NoticesMissing` at I (`:176`) and W (`:182`) | deliberate: "no notices file" vs "the file failed to open". Keep, and give the second its own key in 0.3. |
| **non-default durations** | exactly **2** — `8000f` (`ReportDialog.cs:519`) and `6000f` (`SettingsPage.Notifications.cs:119`), plus the sticky arm `0f` (`NotificationCenterBridge.cs:236`) | three named rungs in `Platform/Controls.cs`: `Notify.Normal 5000`, `Notify.Long 8000`, `Notify.Sticky 0`. The 6 000 becomes `Normal`. |
| sites with an **action button** | **13** | every one is either an **Undo** (Menus ×2, PinActions ×2), a **navigation** (Go to playlist ×3, Go to radio), a **Retry** (PlaylistCreateFlow, PlaybackBridge), a **Restore** (VideoActions ×2) or an **Open** (device picker ×2, override manager ×2). No sixth kind. Keep the taxonomy. |
| sites that also **announce** | **0** | see §6.5 |

### 6.5 The four confirmation sites, and the 0.3 announce rule

**Confirmations** are the other half of the inventory, and there are only four shapes:

| shape | where | default button | copy |
|---|---|---|---|
| destructive confirm | `SettingsShared.Confirm` — Storage cache wipe, History "Clear all" | `DefaultBtn.Close` (Enter cancels) | title is a **question**; body says what is lost and that it cannot be undone |
| restorative confirm | `SidebarCustomizerPage.cs:711-717` (reset to preset), 480 wide | `DefaultBtn.Primary` — a restore, not a destruction | the two **must not look alike** (parity 65) |
| a bound action's confirm | `WaveeActionDescriptor.Execute` → `SettingsShared.Confirm` | inherits Close | copy falls back title → `LabelLocKey`, body → title, primary → `LabelLocKey` (`:69-73`) |
| a headless fallback | `SettingsShared.cs:31` — `if (overlay is null) { onConfirm(); return; }` | — | **the descriptor path does the opposite** and refuses (§0.16); the asymmetry is deliberate (§2 W23) |

**The 0.3 announce rule.** Today no toast announces, and that is a gap, not a decision: a screen-reader user who
adds ten tracks to a playlist gets silence. The rule:

* A toast whose severity is **Error or Warning** announces `assertive: true`.
* A toast with an **action button** announces polite, because the action is time-boxed and the user must know it
  exists before the 5 000 ms elapses.
* A **Success** toast with no action does **not** announce — the action that produced it already announced at its
  own chokepoint (a library edit, a rootlist drop, a folder create), and announcing twice is worse than once.
* An **Informational** toast does not announce.
* The announce happens **inside the one `Notify.Say` helper**, never at the 107 call sites — which is the whole
  reason the helper exists.

### 6.6 Tooltips and localisation keys this chapter owns

| surface | string | localised? |
|---|---|---|
| the file-drop pill | `Strings.LocalFile.DropHint` | **yes** (`WaveeShell.cs:1429`) |
| dialog default primary | `Strings.Dialog.Ok`, re-resolved at render so it follows a culture change | **yes** (engine `ContentDialog.cs:37-39`) |
| dialog cancel / close | `Strings.Auth.Cancel` / `Strings.Auth.Close` | **yes** |
| drag captions | `Strings.Drag.ReorderHint` · `.OrganizeHint` · `.DragOntoPlaylist` · `.CantMoveHere` · `.CantMoveAcrossSections` · `.MovedTo` · `.MovedToLibrary` · `.MovedManyTo` · `.NothingToAdd` · `.StillSyncing` | **yes** (`WaveeResourceDrag.cs:343-346`, `:433`, `:457-461`) |
| the offline chip | `Strings.Shell.Reconnect` / `Strings.Shell.SignIn` / `Strings.Shell.Connecting` | **yes** (`MergedChromeRow.cs:210`, `:217`) |
| the player bar's states | `Strings.Player.NothingPlaying` / `.Loading` / `.Reconnecting` / `.CannotPlay` | **yes** (`PlayerBar.cs:734-739`) |
| the offline banner | `Strings.Common.Offline` + `Strings.Common.Retry` | **yes** (`OfflineBanner.cs:17`, `:20`) |
| the empty/error grammar | `Strings.Common.ErrorTitle` / `.ErrorSubtitle` / `.Retry` | **yes** (`ErrorState.cs:23-25`) |
| the seven action-unavailable reasons | `sidebar.action.unavailable.*` — **literal keys**, so a missing one renders loudly as `[key]` | **yes, by design** (`WaveeActionTargeting.cs:137-146`) |
| the zoom picker's Auto label | `Strings.Settings.Appearance.ZoomAuto(autoPercent)` — rebuilt per render because it carries the LIVE percentage | **yes** (`SettingsPage.Appearance.cs:84-91`) |
| the language row | `Strings.Settings.Language.{Title,Subtitle,Label,RestartSub,System,EnglishUs,Dutch,Korean}` | **yes** (`SettingsPage.General.cs:36-40`, `:81-86`) |
| **seven toast sites** | English literals (§6.4) — five are developer surfaces, two (`ArtistGalleryLightbox.cs:386, :394`) are not | **NO** — parity 62 |

---

## 7. Data & readiness in 0.3 terms

| this chapter needs | 0.2.9 source | 0.3 |
|---|---|---|
| the active route, and the direction of the next swap | `Signal<Route>` + a `NavTransitionKind` signal written **before** the route in the same flush and read by **Peek** (`ContentHost.cs:93-95`) | `Shell.Route` + `Shell.NavMotion`, same discipline |
| the shell material | a root `Signal<ShellMaterialState>` provided through `ShellMaterial.Slot` | `Shell.Material`, a Context slot |
| the masthead publication | `ShellMastheadStore` — a bounded LRU keyed on `name + arg`, with ONE `Version` signal the band reads (`ShellMasthead.cs:14-47`) | `Shell.Masthead` |
| a cover's two gradings | `SpotifyLive.CoverColorPlane.Current.TryGetScheme(url, lightTheme)` — **both halves**, read in opposite directions by `SchemeFor` / `ChromeSchemeFor` | an `Entities` palette edge (ch 00 proposes `Entities/Palette.cs`) — **two columns, not one** |
| auth state | `PlaybackBridge.AuthState` — a **signal** refolded on every event that could move it. A plain `HasStoredCredential()` re-check would be non-reactive and a mid-flight rejection would never swap the leaf (`WaveeApp.cs:333-339`) | `Playback.AuthState` |
| playback recovery kind | `PlaybackBridge.RecoveryKind` (`PlaybackRecoveryKind.Network` ⇒ Reconnecting) | `Playback.RecoveryKind` |
| network cost | `NetworkPolicy.Cost` (kind + limit/roaming bits) and its bool projection `Metered` | `Platform.Network.Cost` / `.Metered` |
| the metered caps | `WaveeSettings.MeteredQualityCap`, `.VideoMeteredMaxHeight`, seeded at `Install` | `Platform.Settings` |
| a bound action's target | `WaveeActionTargets.Resolve(binding, AcceptedTargets, in host)` over a snapshotted `WaveeActionHostState` (now-playing uri, context uri, active route key) | `Platform/Actions.cs` CORE — **the host state stays a readonly struct** so the pure resolver never touches a signal (`WaveeActionTargeting.cs:60-64`) |
| the extension registry | `WaveeExtensionRegistry` via `UseContext(WaveeExtensionRegistry.Slot) ?? Acts?.Extensions` (`SidebarPane.cs:468`) | `Platform.Actions.Registry`, same fallback |
| the locale | `AppLocaleBootstrap.Initialize(settings, folder)` at `Program.cs:90`, captured **once per process** | `Platform.Boot()` step 1 |
| the window's base DIP extent | `Viewport.Size × Viewport.Zoom` — reading `Viewport.Zoom` here is the engine's **sanctioned display-only use**: a policy input, never a coordinate conversion (`ZoomAutoPolicy.cs:30-38`) | unchanged |
| setup completion / pending | `WaveeSettings.SetupCompleted` / `SetupPending` via `SetupGating` | `Screens/Setup.cs` CORE |
| AC power / energy saver | `PowerSession.ReadPower()` (`AmbientPowerPolicy.cs:108`) | unchanged (engine) |

### 7.1 The four-state readiness table, per surface

The generalisation of §2 W24's rule. `SK` = skeleton · `EM` = empty state · `ER` = error state · `OF` = offline
(banner over cached content; the offline-copy error state when there is none). A cell marked **✖** is a state that
surface cannot currently express.

| surface (chapter) | in flight | nothing | failed | no network | today's gap |
|---|---|---|---|---|---|
| Home (10, 11) | SK — derived from `HomeSeed`, same shape and counts | EM — the server's greeting feed | ER + Retry | ✖ **OF** | a network failure renders as ER, indistinguishable from a server error |
| a Home module (11) | SK per module | the module is **omitted**, never an empty shelf | ER per module (the Charts row is the fail-loud arm) | ✖ | — |
| album / playlist / liked (05, 06, 07) | SK — `DetailSkeleton` reserves the hero band at the real sizes (`DetailSkeleton.cs:17-20`) | EM in the track region; the hero still renders | ER | ✖ **OF** | a playlist that is merely unreachable looks deleted |
| the detail **notice** strip (03) | — | — | — | — | `DetailNotice` already carries Deleted / AccessRevoked / CreateFailed / MinifiedAlbum and **never un-renders the page** (`PlaylistPageNoticeRules.cs:32-34`). **This is the right shape for OF** — add an `Offline` member rather than a new component |
| artist + disco (08) | SK | EM per facet | ER | ✖ | — |
| search (13) | SK per facet | EM — "No results for …" | ER | ✖ **OF** | the most visible gap: a search with no network reads as "no results" |
| browse (13) | SK | EM | ER | ✖ | — |
| library pages (15) | SK | EM — the grammar's canonical case | ER | ✖ **OF** | a library IS cached; offline must show it, not an error |
| recents / history (16) | SK / none | EM | ER | n/a — both are local | history is local and therefore correct offline by construction |
| concerts (17) | SK | EM | ER | ✖ | — |
| the sidebar (25) | **none, deliberately** — it goes empty → populated with no skeleton (§2 W24) | EM per band (ch 25 W4) | ✖ | ✖ | the sidebar has **no** error state at all; offline it simply keeps the last rootlist, which is right |
| the queue (21) | none | EM (`EmptyState.Compact`) | ✖ | ✖ | — |
| lyrics (22) | SK | EM — "No lyrics for this track" | ER | ✖ | — |
| the player bar (20) | Loading | NoTrack | Error (title in Critical) | **Reconnecting** ✔ | **the one surface that already has all four**, because `PlayerState` is a five-member enum (`PlayerBar.cs:28`) |
| settings / diagnostics (27) | — | — | — | n/a | local |

**The 0.3 rule, in one sentence:** every page-level surface takes the same four-arm switch, the fourth arm is
`Platform.Network` + the page's own cache, and the arm that draws it is the **notice strip over kept content**, not a
replacement of the page. That is why §1.2 folds `EmptyState` / `ErrorState` / `OfflineBanner` into **one** `Vacancy`
grammar with four voices rather than three types: the difference between them is copy and one glyph, and three types
is how they drifted.

**And the axis the table above does not have a column for: SCALE.** Every voice ships at two — `Build` (PageHero
28/36/600) for a page body and `Compact` (Subtitle 20/28/600) for anything under ~340 DIP (`EmptyState.cs:34-45`,
`ErrorState.cs:17-43`). Four rows of the table are rails, not pages — the queue (21), the friends panel, the
now-playing panel, and a Search facet body — and they take the Compact scale of the same voice, not a different
component. The offline voice needs both for the same reason: a queue rail with no network is exactly as offline as
the page behind it, and today neither can say so. So the 0.3 signature is `Vacancy(voice, scale, …)` — two
parameters, one builder, eight shapes — and **never** eight builders, which is the shape that drifted the first time.

**And the crossing rule, generalised from §2 W24:** a surface crosses `SK → EM` exactly once per account and never
back. Mechanically: once a surface has observed `LoadState.Ready` with zero rows for a given account, it must not
return to `Pending` for the same query — a refresh re-fetches **behind** the empty state, not in front of it. The
engine already gives the tell: `LoadingBarSuppressors > 0` hides the scroll rail (`SceneRecorder.cs:3402`), so a
surface that shimmers with a live scroll thumb has violated it.

### 7.2 What `SeedFake` must contain, per surface

The inventory the Wave 5 gate needs. Each row is what must exist in `Entities.SeedFake()` for that surface to render
its **loaded** state. ✔ = exists today *and reaches the surface*; ✖ = must be added.

| surface (chapter) | required fixture | today |
|---|---|---|
| Home (10, 11) | a 31-section document with a daylist hero, weekly pair, quick grid, recents, mix band, chip cards, radio dial, queue list, rated shelf, featured, podcast shelf, topic + section-entry folds | ✔ `assets/spotify/home.json` |
| Home skeleton (10) | a blank-content feed of the **same shape and counts** as the loaded one — the shimmer is *derived* from it | ✔ `FakeData.HomeSeed` `:587-610` |
| Home hero (11) | a **daylist with a live `ExpiresAtMs`** so the countdown ticks and `PulseRowHeight 28` is occupied | ✖ ch 30 DG3 |
| Home washes (this ch, §4.4) | at least **three** artwork-bearing modules so all three wash layers mount — a two-module seed silently tests only two thirds of the material layer | ✔ (the export has more), but assert it |
| Album (05) | all four `AlbumKind`s — the `AlbumShape` cycle gives Single(1) / EP(5) / Album(12) / Single(2) / Compilation(18) / Album(10) per index | ✔ `FakeData.cs:78-86` |
| Album (05) | a **prerelease** with a countdown | ✖ ch 30 DG3 |
| Track row / table (01, 04) | tempo in three humps, a Camelot slot + its wire colour, one descriptor tag, a year spread over 17 values, explicit every 6th, and **no** video association | ✔ `:50-60` |
| Track table (04) | a collaborative playlist (→ the Added-by lane) and a dated one | ✔ (ch 04 names `pl0` / `pl1`) |
| Playlist (06) | 7 named playlists incl. a 4-track one, a 50-track one and two non-Latin titles | ✔ `PlaylistSeed` `:481-490` |
| Liked (07) | ≥ 160 tracks with a real `AddedAt` spread and 16 distinct covers | ✔ the export's `LikedCount` + `LikedSongs` `:339` |
| Liked chips (07) | content-filter tags | ✖ `NullContentFilterService` |
| Artist + discography (08) | a discography of **hundreds** per facet, so the grid genuinely virtualizes | ✔ `Discography` `:114-130` |
| Show / episode (09) | 8 shows, 8–12 episodes each, exactly one in-progress at ⅓, and `TotalEpisodes == PagedThrough` so the load-more pill is correctly absent | ✔ `FakePodcastSource` |
| Search (13) | top hits across **five** kinds plus chips and genres | ✖ **partial** — tracks / albums / artists / playlists only (`SpotifyExportSource.cs:142-155`) |
| Browse (13) | a directory with all five bands populated **and one category page in each of the four `BrowsePageLayout` modes** | ✖ `NullBrowseService` |
| Home section / browse section (12) | a pageable section with a cursor | ✖ `NullHomeSectionService` |
| Library (15) | 13 albums, 12 artists, 7 podcasts, 161 liked; a nested playlist tree (a folder inside a folder) | ✔ `LibraryStats` `:477`, `PlaylistTree` `:501-517` |
| Recents (16) | a grouped recents snapshot | ✖ `NullRecentsService` |
| History (16) | nothing — it self-seeds from navigation | ✔ |
| Concerts (17) | a hub feed, an artist schedule and a detail | ✖ `NullConcertService` |
| Player bar / rail / deck (20, 21, 23) | **something playing.** Every one of these surfaces is dark under `UnsupportedPlaybackPlayer` | ✖ the largest single gap |
| Queue (21) | 1 now-playing + 3 user-queue + 8 next-up, the last four autoplay | ✔ `DefaultQueue()` `:569-576` — **built and never called** |
| Lyrics (22) | a 40-line **word-synced** document that overflows the ~11-line viewport | ✔ `FakeData.Lyrics` `:543-567` — **built and never called** (`NoLyricsProvider`, `Services.cs:624`) |
| Video (24) | a module with a watch page | ✖ `Services.Modules` is null on the fake backend |
| Sidebar (25) | a folder-capable tree with real recursion (a folder inside a folder) | ✔ `PlaylistTree` (`cafe` → `latenight`) |
| Sidebar miniature (26) | **index-addressable** `Playlist(1, 2, 5, 7, 8, 10, 12, 14)` + `index + 6`, `Artist(3)`, `LibraryStats()` — **2 and 7 are the grid-strip cells** | ✔ today via `FakeData.Playlist(n)`; must stay deterministic and index-addressable in `SeedFake` |
| Settings / diagnostics (27) | the real store | ✔ |
| Setup / What's new / Feedback (28) | `WAVEE_FAKE_CHALLENGE` seeds a canned pairing challenge for deterministic login shots | ✔ `WaveeApp.cs:276-283` |
| Appearance variants (30) | every preference exercisable without the Settings UI | ✖ |
| **the light-theme pass** (this ch §4) | a launch flag or a settings pre-seed that starts `--fake` in **light**, so the eight derived surfaces can be screenshotted without ten clicks | ✖ — and it is what makes the §4 parity items runnable |
| **the network states** (this ch §2 W9) | a way to force `ShellAuthState.Offline`, `PlaybackRecoveryKind.Network` and `NetworkCost.Fixed` on the fake backend | ✖ — three signals; today none is reachable in `--fake` |
| **the action platform** (this ch §2 W12) | at least one binding per `WaveeActionTargetModes` arm, plus one that resolves **unavailable**, so the reason caption renders | ✖ |
| **the focus surfaces** (this ch §2 W1-W4) | nothing extra — every focus state is reachable by pressing Tab. The seed's job is only to ensure a list has ≥ 2 rows so the roving stop can move | ✔ |
| OS surfaces (ch 14) | **something playing**, ≥ 6 distinct recent contexts, a notification feed with ≥ 4 unread rows | ✖ — ch 14 owns the row; repeated here because it shares the "something playing" gap |

**The determinism rule** (chapter 26 discovered it; it is general). `SeedFake` must be **index-addressable and
deterministic**: `SeedFake.Playlist(7)` is the same playlist on every launch and in every process. The sidebar's
template-confirmation miniature indexes fixed slots (`SidebarMiniature.cs:128`, `:134-137`, `:156-162`, `:272`,
`:292`, `:320`), and a random seed makes that dialog **flicker between opens**. `FakeData` already obeys the rule —
every generator is a pure function of an `int`, `Wrap()` makes it total for *any* int including the negatives a uri
hash can produce (`FakeData.cs:12`, `:29`, `:56-58`), and covers wrap modulo 16. **Port the discipline, not just the
data**: no `Random`, no `DateTime.Now`, no dictionary iteration order.

### DATA GAPS

1. **No notification table.** `ToastEscalator` reads `WaveeNotification` rows that 0.2.9's `Wavee.Core` produced for
   free. The 0.3 tree has no notification entity (ch 19 §7 raises the same gap, ≈ 400 lines). Ch 14's toast bridge
   cannot be ported before it exists, and this chapter's `Notify.Say` helper has no severity source without it.
2. **`Image.LargestUrl` is not wired to the OS surfaces** — ch 14's finding, repeated here only because the same
   column feeds the drag chip's art (`WaveeDragChipModel.ArtOf`, `:47-49`), which takes `payload.ArtUrl` and never
   asks for the largest.
3. **`ZoomAutoPolicy.MigrateMode` has no call site in plan §3.5's `Main`** (ch 00 §9.5 found the same). It must run
   at settings load, before anything reads `appearance.zoom.mode`.
4. **No `Platform.Boot()` locale step in the plan.** `AppLocaleBootstrap.Initialize` runs at `Program.cs:90`, before
   the window; plan §3.5 does not name it. Every `Loc.Get` before it returns the `en-US` fallback.
5. **No `Platform.Boot()` font step.** `Glyphs.cs` must be set before `FluentAppHarness.Run` or every Fluent-Icons
   glyph is tofu on Windows 10 — including the action-icon table's 29 keys (ch 00 §9.5).
6. **`SetupSession.MarkerEpoch`** is a process-static signal the app root subscribes to, so a completion burned
   inside the dialog re-evaluates the gate immediately (`WaveeApp.cs:352`). §2's tree has no home for it. **RESOLVED,
   plan §9.6 Q8, 2026-09-12: `Screens/Setup.cs`, 950 → 1,150.**
7. **Nothing owns the `--fake` seed's SHAPE.** Plan §5 gives owner Q one clause — *"+ the fake data seed"* — and
   names no file, no size and no contents. §7.2 above is that contents; `Entities/Entities.Fake.cs` is the file.
   **And it is needed in Wave 4, not Wave 5** — see §9.6(b).
8. **`NetworkPolicy` has no fake arm.** `NetworkStatus.Subscribe` / `SubscribeCost` are real NLM calls; on the fake
   backend the cost is whatever the machine reports, so the metered arm is untestable offline and the offline arm is
   unreachable. Three seams in `Entities.SeedFake()` close it (§7.2).

---

## 8. Pure rules to port verbatim

Every one of these is already engine-free, already tested (or trivially testable), and must move **unchanged** —
input types may change from records to handles, decisions may not.

| rule | 0.2.9 | what it decides | test today | 0.3 home |
|---|---|---|---|---|
| `PageNavMotion.SlotKey` | `PageNavMotion.cs:28-29` | the identity of a keep-alive page slot: tab + route name + arg, ``-joined. **Direction is deliberately NOT part of it** — folding it in "made a motion-only write on the already-active key look like an activation change, which re-seeded the entrance and re-faded the whole page with no content change at all" (`:13-17`) | source-included by `Wavee.Tests` | `Shell/Shell.cs` CORE |
| `PageNavMotion.RecipeFor` / `RecipeForVideoSafe` | `:36-121` | the whole direction → recipe map, both families, including the null for video-safe Neutral | same | same |
| `ShellTintOwnership.Resolve` | `SpotifyLive/ShellTintOwnership.cs` | claim vs refresh vs hold; the three outcomes `WriteKnownColor` / `WriteNeutral` / `WriteHeldColor` (`ShellMaterial.cs:52-57`) | — | `Shell/Shell.cs` CORE |
| `ShellWashGeometry.Resolve` | `ShellWashGeometry.cs:40-53` | a window-relative ellipse → its clipped box + the ellipse re-expressed in that box; the anchor choice ("a box whose leading edge left the window edge hangs off the TRAILING one … leading wins because Start is also the fill-the-slot arm of the ZStack arranger") | **none today** — add one | `Platform/Design.cs` CORE |
| `NetworkPolicy.EffectiveQuality(int, int)` / `EffectiveVideoMaxHeight` | `NetworkPolicy.cs:95-125` | `min(user, cap)` when metered, both clamped 0..2; unknown cost is unmetered-conservative; a video cap of 0 means unlimited | **none today** — add one; it is four lines and it gates streaming cost | `Platform/Platform.cs` CORE |
| `ZoomAutoPolicy.Suggest` / `SnapPlateauDown` / `MigrateMode` | `ZoomAutoPolicy.cs:71-113` | the whole auto-zoom ladder; the migration's one-shot version stamp | `ZoomAutoPolicyTests` drives the real decision, incl. `Suggest_IsIdempotent…` and the `DesignW == PageMaxW` cross-check | `Platform/Platform.cs` CORE |
| `AppLocaleBootstrap.SpotifyLanguage` | `AppLocale.cs:35-44` | culture → the 2-letter primary subtag, else `"en"`; the `-`/`_` split and the ASCII-letter guard | **none today** — add one | `Platform/Platform.cs` CORE |
| `ContextBandLayout.EstimateLabelWidth` / `ActionsWidth` | `ContextBandLayout.cs:78-93` | the band's whole width budget from `AvgCharW` | ch 03's | `Entities/Detail.cs` CORE |
| `WaveeActionTargets.Resolve` + `LocKeyOf` | `WaveeActionTargeting.cs:135-160` | the target-mode matrix and the seven reasons — "one function, so every surface … agrees on what a binding means and on why it is disabled" (`:133-134`) | `Wavee.Tests` | `Platform/Actions.cs` CORE |
| `WaveeActionDescriptor.Resolve` (the fold) | `WaveeActionDescriptor.cs:103-116` | mode + target + overlay + the descriptor's own veto, in one call, "so the row's disabled state and `Execute`'s refusal can never disagree" | same | same (it needs `ActionServices`, so it is the CORE/SHELL seam) |
| `PinRowRule` | `Actions/Extensibility/PinRowRule.cs` (32) | whether a pin row may be shown for a target | same | same |
| `PlaylistPageNoticeRules.Next` | `Features/Detail/PlaylistPageNoticeRules.cs` | the notice for the next model, with terminal notices **sticky**; "a notice never un-renders the page" | `PlaylistPageNoticeRulesTests` | `Entities/Detail.cs` CORE — **and this is where the Offline arm goes** (§7.1) |
| `PlaylistDropRefusalRules.Evaluate` / `Accepts` | `WaveeDragRules.cs:126-142` | **both** the accept test and the refusal caption, from one table, in one order | `WaveeDragRulesTests` | `Platform/Drag.cs` CORE |
| `TabDropRules` · `QueueDragRules` · `SidebarRailDropRules` · `WaveeDragKindMap` | `:159-209`, `:19-80` | the tab's append-only rule, the queue's reorder-never-copy rule, the collapsed rail's corridor rule, four surface enums → one drag kind (`prerelease:` before `album:`) | same | same |
| `WaveeDragChipModel.For` / `ArtOf` | `WaveeDragChipModel.cs:31-49` | which line wins for a track vs an entity drag; where art comes from; what the badge counts | same | `Platform/Drag.cs` CORE |
| `WaveeResourceDragPayload.CanCopyTracks` / `TryPin` / `RootlistCount` | `WaveeResourceDrag.cs:53-93` | capability, pinnability (routed through `SidebarPinId.IsPinnable` at the boundary), and the ONE count the badge and the toast share | — | `Platform/Drag.cs` |
| `WaveeResourceDrop.RootRefs` / `IsSource` | `:488-520` | the refs a payload moves AS; "is this row one of the items I am carrying", asked BOTH ways (entry id and uri) | — | `Platform/Drag.cs` |
| `SetupGating.*` | `SetupGating.cs` | the first-run gate and its two markers. `MarkCompleted`'s two writes are **independent** on purpose: a re-armed, already-completed install would otherwise leave `SetupPending` set forever and re-open the wizard every launch | `SetupGatingTests` | `Screens/Setup.cs` CORE |
| `AmbientPowerPolicy.ReadPlugged` (the verdict, not the P/Invoke) | `AmbientPowerPolicy.cs:104-116` | energy saver ⇒ battery; no battery / unknown / a failed read ⇒ plugged | **none today** — add one | `Platform/Platform.cs` CORE |
| `ToastCoalescing` (the dedupe key rule) | engine `ToastCoalescing.cs` | `DedupeKey ?? message`; a match refreshes rather than stacks | engine | engine |

**Five of the twenty have no test at all today** (`ShellWashGeometry.Resolve`, `EffectiveQuality`,
`SpotifyLanguage`, `ReadPlugged`, and the drag payload helpers) and each guards a failure that is quiet: a wash
anchored off the window edge, a metered user billed for 320 kbps, a Korean UI asking Spotify for `ko-KR` instead of
`ko`, a desktop permanently at 24 Hz, and a chip that counts wrong. Add them in the wave that ports them.

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **The focus ring's flag is not a heuristic.** "Draw the ring when the last input was a key" is a different, worse
   design: it gets a pointer-then-key sequence wrong and it cannot express `MoveFocusVisual`. The flag rides on the
   node (`NodeFlags.FocusVisual`), so a re-parent, a scroll and a re-render all keep it.
2. **The two focus insets are not decoration.** +2 exists because a −3 ring crosses a 1-DIP border; +1 exists because
   a −3 ring on a 40-DIP row paints the neighbour. Both are geometry facts, not taste. Collapsing them to one value
   breaks one of the two.
3. **`ChromeSchemeFor` is deliberately the OPPOSITE half of the grading.** The single easiest light-theme regression
   in the app is to "simplify" it into `SchemeFor` (§4.5).
4. **The stage's scrim alphas have no light arm ON PURPOSE.** The sRGB argument at `StageArm.cs:30-33` is correct and
   is *why* `StageLayout` stays one set of numbers. A well-meaning "add a light arm for symmetry" makes the light
   stage worse, not better.
5. **The page tone plane's light arm is `0.30` and the record's hue is close to imperceptible there.** That is
   written down at `CoverPaletteLeaves.cs:126-138` as the endpoint of a four-step ratchet driven by user reports.
   Do not undo it because a light screenshot "looks colourless" — raise the **pair**, never the clamp, and only on
   a new report.
6. **The three page-swap clocks are three numbers for three reasons** (120 / 90 / 250). Merging them into one
   "transition duration" produces either a chrome that cuts or a card that double-exposes.
7. **`Exit.Active` must stay TRUE.** A stripped Exit detaches the outgoing page in the same frame and the card
   flashes EMPTY (`PageNavMotion.cs:31-34`). This is the most likely "cleanup" a re-author makes.
8. **The video-safe pair classifies BOTH sides.** Navigating away from a watch page is exactly as exposed as
   navigating to one, because the outgoing root is attached and drawing for its whole exit (`:79-82`).
9. **The material is a hand-over and never a clear**, and its neutral is a real colour. Both facts exist because the
   obvious implementation (clear on unmount, fade to transparent) produced two separate visible bugs.
10. **A wash layer's Key is its artwork.** It is not a cache key; it is the cross-fade.
11. **A toast refresh is not a new toast.** The dedupe key is the message by default, and seventeen call sites depend
    on that (§6.4). Adding per-site `DedupeKey`s "for clarity" turns nine "Added to X" sites into nine cards.
12. **A bound action's disabled state and its refusal come from ONE call.** Splitting `Resolve` into a cheap "can I
    show this" and an expensive "can I run this" is how a row that looks enabled starts doing nothing.
13. **`BuiltInExtensionTable`'s exclusions are a safety list, not an oversight.** Each of the twelve has a one-line
    reason at `BuiltInExtensionTable.cs:16-27`, and the strongest is: a stored binding must be able to honestly
    re-target itself after a restart. Adding `DeletePlaylist` "because the confirm gate exists" is exactly the move
    the comment pre-refuses.
14. **The empty / error grammar has NO glyph, and the `glyph` parameter was removed so a stale call site is a compile
    error** (`EmptyState.cs:24-29`). Six surfaces had grown their own variants around that glyph.
15. **The refusal table's ORDER is the design** (`WaveeDragRules.cs:114-125`). Reordering it to "most likely first"
    hides the two refusals that have a remedy behind the one that is just a wait.
16. **`DefaultBtn.Close` on a destructive confirm.** The accent button is the one the focus trap focuses and the one
    Enter invokes; putting it on "Delete" turns a reflexive Enter into data loss.
17. **Both zoom axes bind.** Width alone is the failure mode of every naive "match my display" auto-zoom: on a
    3440 × 1440 ultrawide it demotes structural decisions the app already made on height (`ZoomAutoPolicy.cs:25-28`).
18. **The eyebrow's tracking is 30 BECAUSE the role gave up caps.** Restoring `.ToUpper()` without restoring the
    60–120 tracking gives a tight, shouty label; restoring both re-breaks Turkish and German.
19. **`--fake` is not a demo mode that happens to be useful; it is the only offline verification surface the project
    has.** Every parity checklist in all 31 chapters names the same kept 0.2.9 build with `--fake`. A seed that
    leaves surfaces empty invalidates those checklists, not just the gate.
20. **The first-run composite is one frame, not twenty empty states.** The per-surface empty states already exist and
    are owned. What has no owner — and what a new user actually sees — is the *composition*.

### 9.2 Traps

1. **A render that reads `GetDragState()`** re-renders per pointer move. The two helpers are event-path reads
   (`WaveeResourceDrag.cs:278-287`).
2. **Reading `_fileDropOver.Value` in the shell's render body** instead of binding it re-renders the whole shell on
   every drag enter/leave (`WaveeShell.cs:1422`).
3. **Reading the route in the transition thunk.** `_motion` is read by **Peek** so a motion-only write cannot re-run
   the keep-alive boundary and re-activate the page that is already active (`ContentHost.cs:93-95`).
4. **Putting the masthead in flow.** It is an overlay on the boundary with `HitTestPassThrough`; as a sibling it
   consumes KeepAlive height and changes it when the family changes (`ContentHost.cs:111-118`, `:133-137`).
5. **Cross-fading to `ColorF.Transparent`.** Premultiplied black; the ramp goes dark. Two independent sites document
   it (`ShellMaterialLayer.cs:80-88`, `:121-126`).
6. **Subtracting a fixed DIP from the wash host's height** instead of insetting the host. The placement constants are
   node-relative ratios; a fixed subtraction makes them viewport-dependent and they go stale on the next resize
   (`ShellMaterialLayer.cs:66-71`).
7. **Reading the descriptor's `Resolve` with `peek: true` in a render.** `peek` false is the reactive read a
   rendering row wants; true is for an invoke (`WaveeActionDescriptor.cs:101-103`). Getting it backwards gives a row
   that never updates *and* an invoke that re-subscribes.
8. **`PrimaryText = null` does not hide the primary button** — the dialog reads `PrimaryText != ""` and falls back to
   the localized "OK", which is how a stray OK once shipped next to Cancel (`SidebarItemPickers.cs:40-44`). `""` hides.
9. **A frozen `ContentDialog` footer.** A dialog whose buttons must change while open needs `d.Footer`
   (`PlaybackRuntimeSetupCard.cs:940`); a picker whose own state drives the commands carries Cancel only and owns its
   buttons in the body (`SidebarItemPickers.cs:17-19`, `:556-562`).
10. **`.Interactive(...)` on a selection-aware row** overwrites all three fills and erases the selected state
    (`SidebarItemPickers.cs:435-437`).
11. **Replacing a selected row's icon with a radio bullet** hides the one thing identifying the row (round-2 defect
    6c, `:445-448`).
12. **Feeding the LIVE viewport into `Suggest`** closes a control loop that converges on the design box regardless of
    what the display is (`ZoomAutoPolicy.cs:30-38`). Always `baseDip = viewportDip × zoom`.
13. **An early return before reading a signal drops it from a `UseSignalEffect`'s tracked set.** Both the auto-zoom
    effect and the nav-pane width effect read every signal first and unconditionally, and both say why
    (`WaveeShell.cs:598-604`, `:515-518`).
14. **`Session.ConnectAsync` from the offline chip.** It signs the user in as the FAKE inner session over a real
    resume already in flight (`MergedChromeRow.cs:195-200`). `Bridge.SignIn` is the one verb.
15. **A local literal for the insertion-preview cap** drifts the cards off a gap the *view* sized from
    `SortableMath.DefaultPreviewCap` (`PlaylistInsertionPreview.cs:15-18`).
16. **Calling `Announcer.Say` without `IsAvailable`** composes a string for nobody, on the UI thread, per event.
17. **An action icon as a raw glyph.** `IconKey` resolves through one table so the checked/unchecked variants stay
    paired (`ActionIcons.Resolve`, `WaveeActionDescriptor.cs:37-39`, `:94`).
18. **Renaming an action key.** Keys are persisted inside `SidebarActionBinding`; a rename orphans every stored
    binding silently (`BuiltInExtensionTable.cs:35`).

### 9.3 `RouteKind`, rewritten against the app

Plan §4.11 declares eighteen kinds. The app has **thirty renderable destinations** and four of the eighteen name
nothing. Derived from `ShellRoutes.s_exact` (`ShellRoutes.cs:26-44`), `s_prefixes` (`:48-60`),
`ConcertRoutes.TryParse` (`ConcertRoutes.cs:21-42`) and `ContentHost.PageFor` (`ContentHost.cs:188-309`):

```csharp
public enum RouteKind : byte
{
    // ── landing + discovery ────────────────────────────────────────────────────────────────
    Home,                 // "home"                          ch 10
    HomeCustomize,        // "home-customize"                ch 12
    HomeSection,          // "home-section:<uri>"            ch 12
    BrowseSection,        // "browse-section:<uri>"          ch 12
    Search,               // "search"                        ch 13
    BrowseDirectory,      // "browse"        (EXACT)         ch 13
    BrowseCategory,       // "browse:<uri>"  (PREFIX)        ch 13
    // ── entity detail ──────────────────────────────────────────────────────────────────────
    Album,                // "album:<uri>"                   ch 05
    Prerelease,           // "prerelease:<uri>"              ch 05   (same page class; its own slot + arm)
    Playlist,             // "pl:<uri>"                      ch 06
    Artist,               // "artist:<uri>"                  ch 08
    Discography,          // "disco:<kindInt>:<uri>"         ch 08
    Show,                 // "show:<uri>"                    ch 09
    Module,               // "module:<wavee:module:…>"       ch 09  (+ 24 for a watch page)
    // ── the user's own ─────────────────────────────────────────────────────────────────────
    Liked,                // "liked"                         ch 07
    Local,                // "local"                         ch 15 / ch 03
    LibraryAlbums,        // "albums"                        ch 15
    LibraryArtists,       // "artists"                       ch 15
    LibraryPodcasts,      // "podcasts"                      ch 15
    Recents,              // "recents"                       ch 16
    History,              // "history"                       ch 16
    // ── concerts ───────────────────────────────────────────────────────────────────────────
    Concerts,             // "concerts"                      ch 17
    ArtistConcerts,       // "artist-concerts:<id>"          ch 17
    Concert,              // "concert:<id>"                  ch 17
    // ── screens ────────────────────────────────────────────────────────────────────────────
    Settings,             // "settings"                      ch 27
    SidebarCustomize,     // "sidebar-customize"             ch 26
    WhatsNew,             // "whatsnew"                      ch 28
    // ApiConsole DELETED, plan §9.6 Q7, 2026-09-12 — the console and its four ApiDebug* helpers are cut outright
    PlaybackDiagnostics,  // "playback-diagnostics"          ch 27
    ConnectDiagnostics,   // "connect-diagnostics"           ch 27
    // ── the terminal arm ───────────────────────────────────────────────────────────────────
    NotFound,             // anything PageFor does not claim  ch 18 W21
}
```

**Deleted, with what each one actually is:**

| plan kind | reality |
|---|---|
| `Episode` | **no episode page exists.** An episode opens its show (`show:`) or a module page. `Episode.UI.cs` is a *row*, not a destination (ch 09). |
| `Queue` | **a rail panel and a stage pane, not a route** (ch 21 W3–W5, W12). No route key, no keep-alive slot, no history entry. |
| `User` | **no profile page and no route in 0.2.9** — see §9.9.6, which strikes it. |
| `Setup` | **a cover over the shell, or the whole window — never a route.** Not in the back/forward stack; cannot be deep-linked. |
| `Library` (one kind) | **three destinations** — `albums` / `artists` / `podcasts` — each with its own persisted width, sort, view mode and selection (`ContentHost.cs:262-264`, ch 15). |
| `Browse` (one kind) | **two destinations** — the directory (exact) and a category (prefix) — with different pages *and* different shell-material behaviour (`ContentHost.cs:184-186`, ch 18 audit 17). |
| `Diagnostics` (one kind) | **three pages**, one developer-gated at the route→page boundary so every entry path obeys one switch (`ContentHost.cs:229`). |
| `ReleaseNotes` | keep it, but name it `WhatsNew` — the route key is `"whatsnew"` and it is **keyed by its `Arg`**, so two versions are two keep-alive slots (`ContentHost.cs:236`). |

**Rules for the enum, so it cannot rot again.** (a) One kind per renderable destination, and `ShellRoutes.IsKnown`
becomes `RouteKind != NotFound` — the two lists cannot drift because there is only one. (b) **Each chapter's §9 names
its own kind.** (c) A `Route` carries `Kind + Subject + Arg + Tab`; the *prefix string* stops existing outside the
parser. (d) `connect-diagnostics` is in `PageFor` but **missing from `ShellRoutes.s_exact`** in 0.2.9 — a real hole
(ch 18 audit 18 found the same gap on the naming side). The enum closes it by construction. (e) **The enum is also
what makes §9.7's gate mechanical**: the nav probe walks `RouteKind` values, not a hand-kept list.

### 9.4 The §2 budget reconciliation — the table the plan needs, per folder

Nobody had summed the chapters' own estimates against plan §2. Here it is. Every "chapters' sum" cell is the sum of
the honest §9 line-budget sections the chapters wrote for themselves (00 §9.4, 01, 02 §9, 03 §9, 05, 06, 08, 09 §9.5,
10 §9, 11 §9.6, 12 §9, 13 §9, 14 §9, 15 §9, 16 §9.4, 17 §9, 18 §9, 19 §9.4, 20 §9, 21 §9.5, 22 §9, 23 §9, 24 §9,
25 §9, 26 §9.4, 27 §9.4, 28 §9.5, 29 §9.10, 30 §9.4), de-duplicated by hand where two chapters budget the same file.

| plan §2 folder | files | §2 target | chapters' sum | over by | the decision |
|---|--:|--:|--:|--:|---|
| `Entities/` | 40 | **23 000** | **≈ 45 000** | +96 % | **raise to 45 000.** Detail frame 3 500 + 700 (ch 03) · album 2 800 (05) · playlist 4 600 (06) · artist + disco 5 550 (08) · User/library/liked/profile 3 650 (07, 15) · show + episode 1 800 (09) · the Home family 7 500 (10, 11, 12) · search + browse 3 950 (13) · recents 3 080 (16) · concerts 3 850 (17) · the track row 4 350 (01) and the track table 5 100 (04). **Nothing here is fat**: §2's 23 000 was written before chapters 03, 04 and 16 existed as surfaces at all. |
| `Shell/` | 13 | **21 600** | **≈ 38 500** | +78 % | **raise to 38 500.** Lyrics 10 000 against ch 22's own count of 5 100 in §2 · decks 5 500 vs 2 400 · **video 3 200 vs 0** · sidebar 10 300 (at risk — chs 25+26 together estimate 23 000–25 000, de-duplicated) · player bar 2 300 · **overlays (ch 19) unallocated** · **the stage (ch 21) unallocated** · this chapter's transitions + material + network chrome + first run ≈ 700. |
| `Platform/` | 7 | **8 600** | **≈ 14 000** | +63 % | **raise to 14 000.** `Design.cs` 2 600–3 000 + `Controls.cs` 4 500–6 000 by ch 00's own count · this chapter's share **1 850** (§9.10) · ch 30's ≈ 4 120. |
| `Screens/` | 13 | **13 700** | **≈ 15 000** | +9 % | **accept.** ch 27 → 6 800, ch 28 → 8 100. This is the only folder inside §5's 30 % partial-file tolerance. |
| `Playback/` | 5 | **6 800** | **≈ 7 100** | +4 % | **accept, after §9.9.1 reconciles `Playback.Os.cs`** — ch 14's honest estimate is 830 for that file against §2's 1 100, and this chapter no longer budgets it at all. |
| **total (UI-bearing)** | 78 | **73 700** | **≈ 119 600** | **+62 %** | |
| **plan §2 whole tree** | ~86 | **~83–88 k** | **≈ 125–135 k** | **+50 %** | |

**Why this cannot be absorbed.** Plan §5 says "a file that passes its budget by 30 % gets a named partial". At +62 %
that mechanism produces a partial for *nearly every file*, which is not a budget — it is a rename. And the DoD's file
cap (§9.5) is what would then silently delete the partials.

**What to do, in the plan, before Wave 0 creates the skeletons:**

1. Add this table to `wavee-0.3-implementation.md` §2 as a **reconciliation block**, with the "decision" column
   filled in by the human who owns the plan — raise, or name the cut. Silence defaults to a cut, and the cut lands on
   whoever ports last.
2. Do it **before Wave 0**, because the file skeleton is where the named partials must appear. A partial invented in
   Wave 5 is a new file against a frozen cap.
3. For each **raise**, restate the folder's file list with the partials included (§9.5's list is the input).
4. For each **cut**, name it in the chapter's §9 so the chapter's parity checklist can drop the corresponding items
   rather than failing them. Chapter 25 §9 already lists, **in order**, what a hurried sidebar port sheds first —
   that is the model for a named cut.

**The three numbers most likely to be wrong, stated so a reviewer checks them first:** the sidebar's 23–25 k (two
chapters describing one platform; the de-duplication is by hand), lyrics' 10 000 against a §2 line of 5 100, and
`Controls.cs`'s 4 500–6 000 (chapters 00, 01, 02, 27 and this one all add to the same file).

### 9.5 The file count — ~40 proposals against a cap of 100

The chapters collectively propose about **forty** files that §2 does not have. §12.4 already warns that "a row whose
0.3 file column is bold has no home in plan §2 and will be dropped by default", and **DoD item 7** — *"the file count
under `src/apps/Wavee` is under 100"* — is the mechanism that would enforce that drop **silently**.

```
ch 00  Entities/Palette.cs · Entities/Palette.Host.cs · Platform/Prefs.cs ·
       Controls.Cta.cs · Controls.Art.cs · Controls.Picker.cs                                   6
ch 01  Entities/Track.Drawer.cs · Platform/Drag.cs                                              2
ch 03  Entities/Detail.cs · Entities/Detail.UI.cs                                               2
ch 04  Entities/Track.Table.cs                                                                  1
ch 07  Entities/User.Liked.cs · User.Facts.cs · User.Cover.cs · User.Facts.UI.cs                4
ch 08  Entities/Artist.Discography.cs                                                           1
ch 11  Entities/Home.Cards.UI.cs · Home.Artists.UI.cs                                           2
ch 12  Entities/Home.Host.cs · Screens/HomeCustomize.UI.cs · Entities/Browse.Page.cs            3
ch 15  Entities/User.Page.Library.cs · User.Page.Liked.cs · User.Page.Profile.cs (→ §9.9.6)     3
ch 16  Entities/Recents.cs · Recents.UI.cs · Recents.Page.cs                                    3
ch 17  Entities/Concert.Feed.cs                                                                 1
ch 20  Shell/Shell.PlayerBar.UI.cs                                                              1
ch 21  Shell/Rail.Styles.UI.cs · Shell/Rail.cs · Entities/Queue.cs · Shell/Stage.UI.cs ·
       Shell/Stage.cs                                                                           5
ch 22  Shell/Lyrics.* — four partials named in ch 22's §9                                       4
ch 23  Shell/Deck.Faces.cs                                                                      1
ch 24  Shell/Video.cs · Video.UI.cs · Video.Host.cs                                             3
ch 26  Shell/Sidebar.Doc.cs                                                                     1
ch 28  Screens/Feedback.cs                                                                      1
ch 29  Entities/Entities.Fake.cs · Platform/Actions.cs · Actions.UI.cs · Actions.Menus.cs ·
       Actions.Table.cs                                                                         5
                                                                                        total  49
```

86 + 49 = **135**. (The completeness critic counted 33 bold plus "at least seven more" = 40; the delta is this
chapter's four `Platform/Actions.*` proposals, which the first draft folded into `Platform/Controls.cs` and which
§1.2 now names, plus chapter 22's four Lyrics partials.)

**The call, and it must be explicit:**

* **Raise DoD item 7 to ~140**, together with §9.4's reconciliation. A file cap is a proxy for "the tree stayed
  comprehensible"; 135 files across six folders with the naming discipline §2 already uses is comprehensible. 86 was
  a guess made before three surfaces (the detail frame, recents, video) were known to need files at all.
* **Or** collapse each proposal into a named partial of an existing §2 file and say so per row — in which case the
  count is unchanged and §9.4's line budget must absorb the whole overrun inside fewer files, which makes several
  single files 8 000+ lines.
* **Do not leave it implicit.** The default outcome of silence is that Wave 5 drops the bold rows, and the bold rows
  are exactly the surfaces nobody has budgeted: the shared detail frame, recents + history, and every video surface.

### 9.6 Cross-wave dependencies, and the two ordering impossibilities

§5's owner tables have `Owner | Files | Ports from` and **no `depends on` column**. Add one. These are the edges it
would carry:

| file / fixture | scheduled | consumed by | what breaks |
|---|---|---|---|
| **`Platform/Drag.cs`** (ch 01's proposal; this chapter is its visual spec) | Wave 5 owner M / Wave 4 owner L | Wave 4 owner **I** needs `TabDropRules` for tab spring-load + deposit (ch 18 §6); Wave 4 owner **J** needs `SidebarRailDropRules` and the five sidebar drop cues (ch 25 W16) | two Wave-4 owners either block or write their own copy of the rules — and a second copy of a refusal table is how a refusal ends up uncaptioned |
| **`Entities/Queue.{cs,UI.cs}`** | Wave 5 owner Q | Wave 4 owner **K** makes it the body of the rail's queue panel (ch 21 W3–W5) **and** of the stage's queue pane (W12) | the Wave-4 gate passes with a rail that has no queue; Wave 5 inherits two skins to reconcile |
| **`Platform/Platform.cs`** (the settings store + typed keys) | Wave 6 owner S | **every Wave-4 and Wave-5 page** reads preferences; `App.Main` calls `Platform.Boot()` first (ch 30 §7.3.1) | every page invents its own read |
| **`Platform/Actions.cs`** (this chapter's proposal) | unscheduled | Wave 4 owner **J** (the sidebar's bound rows and the action picker), Wave 5 owners M/N/O/P (every context menu) | the descriptor is re-invented per surface, and the confirmation gate stops being one gate |
| **the notification table** (ch 19 §7 gap 1) | not scheduled at all | ch 14's toast bridge; this chapter's `Notify.Say` severity source | the toast bridge cannot be ported |
| **`Entities/Entities.Fake.cs`** | Wave 5 owner Q | **the Wave 4 gate itself** — see (b) | the Wave-4 sidebar has no playlists to render |

**(a) The Wave 5 gate cannot pass at the end of Wave 5, as written.** The gate is "`dotnet run -- --fake` opens every
route from the nav probe list", but `settings`, `whatsnew` and `playback-diagnostics` are **Wave 6**
owner R/S, and every `module:` route is **Wave 6** owner T (no `api-console` any more — plan §9.6 Q7, 2026-09-12,
deleted, never on the probe list at all). Four of the thirty destinations do not exist yet when the
gate runs.
→ **Either** scope the Wave 5 gate to the routes Waves 4–5 actually own (and say which), **or** move the Screens and
Modules work forward. Wave 6's own gate is the live/perf/release pass and should not be the first time a route
renders at all.

**(b) The Wave 4 gate cannot pass either.** It is "`--fake` shows the shell frame with an empty content host and a
working sidebar" — but the fake seed (`Entities.SeedFake()`, which replaces `Wavee.Core/Fakes`) is **Wave 5** owner
Q. A Wave-4 sidebar has no rootlist, no playlists, no pins and no folders; "a working sidebar" is unfalsifiable.
→ **Move `Entities.Fake.cs` — at minimum the sidebar/rootlist and playback slices — into Wave 0's skeleton or
Wave 4.** It is the cheapest fix in this section: the seed is pure data, it has no dependencies of its own, and every
later wave's gate reads it.

**(c) A third edge nobody has flagged:** the **light-theme seed** and the **network-state seams** (§7.2) are also
Wave 5 owner Q, and §4's and §2 W9's parity items are Wave 4 owner I's surfaces. Same fix: they are three signals and
one settings pre-seed, and they belong with the Wave-4 slice of the seed.

### 9.7 The gate and instrumentation contract — what proves parity without an eye

A parity checklist is 100 sentences a human reads. This section is the subset a machine can assert, and it is what
makes "nothing beautiful was lost" a claim a wave can **fail**.

#### 9.7.1 The nav probe's route table is blind to eighteen of thirty destinations

`Features/Diagnostics/WaveeNavProbe.cs:35-49` drives two hand-kept lists:

```
HeavyRoutes  home · liked · pl:pl0 … pl:pl5                                 (8 entries, 3 distinct kinds)
CheapRoutes  albums · artists · podcasts · local · browse                   (5 entries)
plus, in the shot paths only: search · album:<uri> · artist:<uri> · sidebar-customize
```

Twelve destinations. `ShellRoutes` knows **sixteen exact names + ten prefixes** (`ShellRoutes.cs:26-60`) plus the
three concert routes `ConcertRoutes` parses (`:9-11`). The probe **never** visits:

```
show:  ·  prerelease:  ·  disco:  ·  module:  ·  browse:<uri>  ·  home-section:  ·  browse-section:  ·
recents  ·  history  ·  settings  ·  api-console (DELETED in 0.3, plan §9.6 Q7, 2026-09-12)  ·  playback-diagnostics  ·  connect-diagnostics  ·
whatsnew  ·  home-customize  ·  concerts  ·  artist-concerts:  ·  concert:
```

Eighteen — **including every surface chapters 09, 12, 16, 17, 27 and 28 own.** The earlier draft of this chapter
caught that the Wave 5 gate is satisfied by blank pages; it did not catch that the instrument the gate names is
blind to more than half the routes.

**The fix, and it is small:**

1. **Drive the probe off `ShellRoutes`.** `ShellRoutes.IsKnown` is already engine-free and already source-included
   into `Wavee.Tests` (`ShellRoutes.cs:13-16`) — which means the route set is *already* available as data to
   something that is not the shell. Replace the two literal arrays with a walk over the `RouteKind` enum of §9.3,
   with one representative argument per prefix family drawn from the seed (`SeedFake.Album(0)`, `SeedFake.Show(0)`,
   and so on). The two lists then cannot drift, because there is one list.
2. **Keep the Heavy/Cheap split as a WEIGHT, not a membership.** The split exists so the timing harness knows which
   routes should be expensive; it is a property of a route, not a second route table.
3. **Restate the gate** — Wave 5's, and DoD item 3 — as:

   > **`dotnet run -- --fake` opens every route in `ShellRoutes` and each one renders its LOADED state from the
   > seed**, with §7.2's per-surface checklist as the pass condition.

   "Opens" is satisfied by a blank page; "renders its loaded state from the seed" is not. The per-surface checklist
   is what turns it into a list of things that either are or are not on screen.
4. **Add the same wording to DoD item 3.**

#### 9.7.2 The four instruments that already exist, and what each one can prove

| instrument | what it asserts | this chapter's use |
|---|---|---|
| **`WaveeNavProbe`** (`:35-49` the routes; `:747`, `:1635`, `:2575`, `:2682`, `:2758-2783` the morph assertions; `:964-1108` the scroll/nav phases) | every listed route mounts; frame time per route against a 120 Hz budget (`BudgetMs = 1000.0/120.0`, `:29`); the morph key set before and after a bounce | **the morph pair (§2 W14) has no other test.** Extend the route table (9.7.1) and the probe covers §2 W8's seven pairs too — each pair is two `ProbeNav` calls |
| **`ReuseGuard`** (engine, per control; `ChecksReuse` + `ScalarChanged`) | a component whose props froze at mount was handed different props on a re-render — the props-freeze contract, mechanically | **every component this chapter names** must declare its frozen scalars: `ShellMaterialLayer` (none — it takes signals), `ShellMastheadBand` (none — it takes a signal), `CoverShellTintBinder` (all of them — it is a props record). A 0.3 owner who adds a `ColorF` prop to the material layer gets a tripwire, not a silent freeze |
| **`FG_FPS_LOG` / `FG_ALLOC_DIAG`** (`AppHost.cs:594-610`) | per-frame UI-thread **bytes and ticks per segment** — `pump · dispatch · flip · flush · layout · anim · images · record · submit · effects · dyntext · publish` — plus `GC.CollectionCount` deltas since the previous painted frame, so a hitch is attributable to a phase without a profiler | **the page-swap matrix is the allocation test.** A swap that allocates in `record` is a wash layer being rebuilt; one that allocates in `flush` is the boundary re-rendering. Both are the specific regressions §0.8–10 exist to prevent |
| **`ops/tools/perf-tour.ps1`** | scripted scroll under measurement, with focus and cursor guards so a stolen foreground invalidates the run (`:139-148`) | the cadence table (§5.4): a loop that lost its `Cadence` declaration shows up as frames where nothing should be animating |
| **`ops/tools/nav-measure.ps1`** + the always-on `nav.frames` / `frame.stall` / `hydration.*` lines | the navigation timings the 2026-09-07 baseline was taken against | the pair matrix's three channels each end at a different time; `nav.frames` is where a channel that never ends shows up |

#### 9.7.3 What is NOT instrumented, and what each gap costs

| unproven claim | why no instrument catches it | the cheapest closure |
|---|---|---|
| the **focus ring** is keyboard-only | nothing screenshots a focus state | a probe phase that Tabs N times and asserts `NodeFlags.FocusVisual` is set on exactly one node, then clicks and asserts it is set on none |
| the **two focus insets** are the only two values | 37 literal call sites | a **source-free** test is impossible (§CLAUDE.md forbids source-text tests); instead make them named constants (§3) and let the compiler be the instrument |
| the **light arms** are correct | no light-theme run exists | the `--fake` light seed (§7.2) plus eight screenshots. It is a manual gate and that is acceptable — but it must be **listed**, or it is not run |
| **offline / metered** compose correctly | unreachable in `--fake` | the three seed seams (§7.2) turn it into a probe phase |
| the **107 toasts** say the right thing | `Toast.Show` takes a string | routing every site through `Notify.Say(key, …)` makes the inventory a **compile-time enum**, and the test becomes "every member has a loc key in every table" |
| a **bound action's** disabled reason | the picker renders it, nothing asserts it | `WaveeActionTargets.Resolve` is already pure and already tested; add one case per reason |
| the **crossing rule** (skeleton → empty, once) | nothing tracks a surface's state history | the `LoadingBarSuppressors` tell (§0.18) is assertable from the probe: a frame with a shimmer *and* a scroll thumb is a violation |

#### 9.7.4 The `--fake` seed IS an instrument

§7.2 is not documentation; it is the assertion list for a Wave 5 gate that can fail. Each ✖ row is a surface that
**cannot** be checked offline today, which means its parity items are either untested or tested against a live
account — and a parity run against a live account is not reproducible, which is the whole reason `--fake` exists.

---

### 9.8 The accessibility position, stated once

**Windows contrast themes are not supported in 0.2.9 and are out of scope for 0.3.**

* Verified by grep: no `HighContrast`, no contrast-theme read, no per-theme token override anywhere in
  `src/apps/Wavee`. The only two `highContrast` occurrences are **Spotify's wire key** for cover-colour grading
  (`SpotifyLive/CoverColorFiller.cs:76-81`, `Design/WaveePalette.cs:140`) and have nothing to do with the OS setting.
* The engine cannot express it either: `public enum ThemeKind : byte { Light = 0, Dark = 1 }` with the comment
  *"HighContrast reserved for the full FluentGpu.Theme subsystem"* (`FluentGpu.Engine/Dsl/Tokens.cs:5`). Two controls
  already carry a documented "the E8 HighContrast pass forces this on" note
  (`FluentGpu.Controls/HyperlinkButton.cs:15-16`, `:51-54`) — i.e. the engine has a *plan*, not an implementation.
* **Consequence to watch for:** if the engine's theme subsystem lands a third `ThemeKind` mid-rebuild, Wavee will
  start receiving a palette it has never been designed against, with no chapter to check the result. A 0.3 owner who
  sees `ThemeKind.HighContrast` compile must stop and raise it, not guess at token overrides. **And note the
  specific hazard this chapter adds:** the eight derived-colour surfaces of §4 would each compute a *third* answer
  from a palette nobody authored.

**Right-to-left and long strings: also unsupported, also a decision** (§0.23, §2 W15, ch 30 §2.4).

**What IS supported, so the position is not read as "no accessibility":**

| capability | where |
|---|---|
| a keyboard-only focus ring with a dual-stroke contrast guarantee | §2 W1–W2 |
| one tab stop per virtualized list, with 2-D arrow navigation inside it | §2 W4 |
| a real focus trap + Tab cycling + focus restore in every modal, flyout and drawer | `ContentDialog.cs:186`, `OverlayHost.cs:470-490` |
| keyboard operation of the whole shell (13 accelerators + the palette) | ch 18 §6 |
| screen-reader announcements at chokepoints (track boundary, rootlist move, library edit, folder create, palette results) | §6.3 |
| automation names and roles on interactive nodes | ch 00 §6.7, per-chapter §6 |
| OS reduced-motion, honoured app-wide and with four explicit arms in this chapter alone | §5.6 |
| app zoom **50 %–250 % by hand** (the engine's twelve-rung `ZoomLadder`, `ZoomLadder.cs:17-18`) and **75 %–200 % by policy** (the six-plateau Auto/Dense subset). An earlier draft said "67 %–200 %", which was wrong at both ends | §2 W16 |
| OS light/dark following, animated in place | `WaveeApp.cs:46-63` |
| `TextScaleFactor` on type only | `large-display-scaling.md` §3.5 — **proposed, not shipped**; UNVERIFIED whether 0.3 adopts it |

**The gap this chapter is honest about:** page navigation moves no focus (§2 W3 path 5–6). For a keyboard user that
means a route change leaves the ring on the sidebar row they activated — which is *usually* right and is never
stated, so it is not a decision. §2 W3's 0.3 rule is the smallest version that makes it one.

### 9.9 Where the plan — or a sibling chapter, or an earlier draft of this one — is wrong

#### 9.9.1 OS surfaces: chapter 14 owns them. This chapter's earlier claim is RETRACTED.

`14-os-surfaces.md` exists (106 KB, 2026-09-12) and owns SMTC, the taskbar button, the thumbnail toolbar, the jump
list and Windows toasts — 28 wireframes of exactly the surfaces this chapter's first draft drew as W1–W6, and §12.3
routed to 29. Two chapters, one surface, two line budgets, two wireframe sets.

**Resolution, applied in this revision:**

* **14 is the home.** Those surfaces are its whole subject; this chapter's were a section of a chapter about
  something else.
* **29 has deleted those six wireframes** and the `Playback.Os.cs` row from its line budget. §12.3 keeps only the pointer.
* **§12.1's "14 is still unused" sentence is struck** and its row now names the chapter.
* **The two budgets reconcile to ONE number for `Playback/Playback.Os.cs`: 830** — chapter 14 §9's own count
  (`14-os-surfaces.md:230`, `:1104`), against §2's 1 100. Chapter 14 additionally shows that the 1 100 is 46 % short
  of the *whole* OS surface once `Notify.cs` (390) and `Notify.Host.cs` (700) are counted (`:1036`, `:1103-1104`) —
  which is a `Platform/` line, not a `Playback/` one, and lands in §9.4's `Platform/` row.
* **Two things 14 must inherit from this chapter's earlier §9.7**, because they were findings about the plan rather
  than about the surfaces: (a) **Wave 3 owner H has no design contract** — `Playback.Os.cs` sits in a wave whose gate
  is "the login smoke plays 10 s of a track through the real pump", and nothing in that gate looks at a taskbar
  button; chapter 14 §9 makes the same finding independently (`:1047`), so it is confirmed from two sides.
  (b) **`AmbientPowerPolicy` is not playback** — it never touches Windows, it drives `AppHost.Animation`, and it is
  mounted by the *shell* (`WaveeShell.cs:859`); it belongs in `Platform/Platform.cs` with its four constants in
  `Platform/Design.cs` §5. **This chapter keeps the ambient cadence** (§2 W25, §5.4) for that reason.

#### 9.9.2 Window state and the DPI hop: chapter 18 owns them. RETRACTED.

The earlier draft asserted that "18's only window-state row is 'resize suppresses layout transitions'" and offered
its own W15 "to be lifted into 18 verbatim". That is stale. Chapter 18's audit item 26 (dated 2026-09-12, flagged
`critic-fix: missing`) added **W23** (restored / maximized / snapped), **W24** (the per-monitor DPI hop) and five
motion rows covering maximize/restore/snap, the DPI hop, the auto-zoom re-resolve with its 500 ms trailing debounce,
the equal-DPI cadence re-probe at `WM_EXITSIZEMOVE`, and the caption glyph swap — from the same `file:line`
citations this chapter was using.

**Resolution, applied:** the earlier draft's W15 — its window-state and DPI halves — is **deleted**. What is kept here is only what 18 does
not have: **the worked `ZoomAutoPolicy` ladder table** (seven base extents → Auto/Dense rungs) and **the "who wins"
manual-step rule** — now §2 W16, cross-referenced from 18 W24 and ch 30 W23. Arguably both belong in 30 (it owns the
preference dimension and draws the picker at W23); they are kept here because the ladder is arithmetic that 18's
sequence and 30's picker both cite, and one copy is better than two. If the plan prefers, move §2 W16 wholesale into
30 §3.4 and leave a pointer.

**Re-checked at the same time:** is anything else in the old §9.7 overtaken?

| old claim | status |
|---|---|
| 9.7.1 Wave 3 owner H has no design contract | **still true**, and now confirmed by ch 14 §9 independently → §9.9.1 |
| 9.7.2 `AmbientPowerPolicy` is not playback | **still true** → §9.9.1 |
| 9.7.3 chapter 30 breaks the twelve-section contract | **RETRACTED** → §9.9.3 |
| 9.7.4 `00-design-system.md` §5 has no ambient-cadence block | **still true**; §5.4 is it |
| 9.7.5 18 has no window-state rows | **RETRACTED** → this section |
| 9.7.6 no chapter owns the first-run composite | **still true.** A grep for "first run" in `18-shell-frame.md` returns **zero** hits. §2 W24 remains this chapter's, written to be lifted into 18 |
| 9.7.7 the Wave 5 gate is unfalsifiable | **still true, and understated** → §9.7.1 |
| 9.7.8 DoD §8 has no appearance-parity or OS-surface item | **still true** |

#### 9.9.3 Chapter 30 has been renumbered and audited. RETRACTED.

The earlier draft recorded that 30 broke the twelve-section contract — "no `## 2. Wireframes`, no §3 Tokens, no §4
Colour & material, no §5 Motion, no §11 Audit log" — and hosted 30's missing motion table as its own §5.2. **That is
no longer true.** `30-appearance-preferences.md` now runs `0 · 1 · 2 Wireframes (W1–W29 + §2.0–2.4) · 3 Tokens ·
4 Colour & material · 5 Motion (§5.1 the five epoch bumps, §5.2 per preference, §5.3 the remounts, §5.4 reduced
motion) · 6 · 7 · 8 · 9 · 10 · 11 Audit log`. It also now carries:

* the **light/dark** frame the earlier draft said existed only in 19 §4.4 — 30 W22 (theme, every token that moves)
  and W20 (colour washes on/off, with the derived-colour formulas and both alphas);
* the **zoom ladder and picker** — 30 W23, including the "does a zoomed window cross layout tiers? Yes — downward,
  and only downward" statement;
* the **localisation** section — 30 §2.4, 27 lines, carrying `AvgCharW 7.6`, the `ToUpper` refusal, the four fixed
  picker widths, the Marquee fallback and the shown-but-disabled locale picker.

**What this chapter therefore does with items 9, 10, 18 and 21 of the completeness review:**

| item | disposition |
|---|---|
| 9 — "localisation needs a real chapter rather than 30 §3.8's 28 lines" | 30 §2.4 is the *preference* view (what the picker does, which strings are disabled). **§2 W15 here is the cross-cutting view**: the estimator's arithmetic worked against a long German string, the Marquee-vs-ellipsis rule, and the caps refusal as a *type* decision. Both are wanted; neither is redundant. |
| 10 — "zoom split across four places" | **one home is §2 W16** for the ladder + the who-wins rule; 18 W23/W24 owns the window sequence; 30 W23 owns the picker. Three views, one arithmetic, cross-referenced. `large-display-scaling.md` §3.2 is inherited by W16 explicitly. |
| 18 — "light theme is one 20-row palette and nowhere else" | **superseded in part** (30 W20 now carries the derived-colour formulas) and **answered in full by §4**, which is the "one chapter that walks the derived-colour surfaces in light" the item asked for as the alternative. |
| 21 — "30 breaks the section contract and has no §5" | **RETRACTED.** What remains is one live sub-item: 30's §5 owner entry is a **range, "4–6"**, so no single person owns the preference dimension. 30 §7.3 argues the settings store must land in Wave 4, which makes **owner L** the natural single owner. That is a plan edit, not a chapter edit. |

#### 9.9.4 The third focus margin is a defect, not a variant

`PlayerStyleFlyout.cs:101` and `:182` set `FocusVisualMargin = Edges4.All(-2f)` on `ThumbCard` — a 6-DIP-cornered,
padded card inside a flyout, i.e. exactly Override A's shape. There is no comment and no precedent in the app.
**0.3 normalises it to `FocusInsetBordered = 2f`.** (The engine's `CalendarView` −2 is a different control with a
different template and is not the precedent.) Parity 6.

#### 9.9.5 `OfflineBanner` has no call sites

`Components/OfflineBanner.cs` is 30 lines, complete, and **nothing under `Features/` calls it**. The component that
would make §2 W9's third state real already exists and was never wired. Either wire it (§7.1) or delete it — a
finished component nobody calls is the single most likely thing a re-author drops as dead code, and this one is the
fourth readiness state.

#### 9.9.6 The profile page is a phantom — strike it

Plan §2 annotates `User.cs User.UI.cs User.Page.cs` as *"(library edges; library, liked, **profile** pages)"*, and
chapter 15 §9 proposes a `User.Page.Profile.cs` partial. But:

* there is **no profile route** in `ShellRoutes` (`ShellRoutes.cs:26-60`);
* there is **no arm** in `ContentHost.PageFor` (`:188-309`);
* there is **no row** in §12.2 and **no wireframe** in any chapter;
* plan §1's scope table says "New features" are **out**.

So it is either new work inside a release that forbids new work, or the budget line and the proposed partial are
phantom. **Strike it from §2's annotation and from 15 §9's partial list** — or add it to §1's In column as an
explicit exception, with a chapter and wireframes. Silence is the one outcome that guarantees a Wave-5 owner invents
it. (§9.3 deletes the `User` route kind for the same reason.)

#### 9.9.7 Four smaller plan gaps this chapter found

1. **Plan §3.5's `Main` has no locale step.** `AppLocaleBootstrap.Initialize` runs at `Program.cs:90`, before the
   window exists; every `Loc.Get` before it returns the `en-US` fallback (DATA GAP 4).
2. **Plan §5 had no owner for `Platform/Actions.*` when this chapter was first written.** The action platform is
   ~946 lines of `Extensibility/` plus an 81 KB menu vocabulary, and §5's owner tables named neither. It was the one
   surface whose §5 owner was genuinely ambiguous between Wave 4 owner **I** (it is shell-adjacent — the palette,
   the sidebar rows) and Wave 4 owner **J** (the sidebar owns every bound row today). This chapter's first draft
   resolved it to owner L, on the same argument that puts `Platform/Drag.cs` with L. **Settled instead (A7): owner
   I**, landed before owner J's "Move to folder…" picker — ch 01 already assumes I builds the table, and getting it
   wrong costs a second descriptor type. Two files, not four: `Platform/Actions.cs` CORE (descriptor, targeting,
   the one action table, the thirteen first-party descriptors, the extension registry) + `Platform/Actions.UI.cs`
   (the menu vocabulary, icons, the row, the picker, the reason caption). `PinRowRule` goes the other way, into
   `Sidebar.cs` (owner J).
3. **DoD item 3 and the Wave 5 gate both name "the nav probe list"** — a hand-kept array, not a contract (§9.7.1).
4. **No DoD item covers light theme.** Add: *the §4 light-arm checks of `29-cross-cutting.md` pass against the kept
   0.2.9 Release build*, alongside the appearance-parity item §9.9.2 notes is still missing.

#### 9.9.8 Six things this chapter itself had wrong, found by the 2026-09-12 adversarial pass

Every earlier §9.9 entry retracts a claim about the *plan* or a *sibling chapter*. These six are about this chapter.

1. **"Home's three radial washes … on Home only" was wrong.** `ContentHost.PublishesShellMaterial` (`:184-186`)
   admits **four** route families, and three of them publish a wash: `home` (three legs, `HomePage.cs:229-237`),
   `home-section:` / `browse-section:` (**one** leg, `HomeSectionPage.cs:177-182`) and `recents` (**one** leg,
   `RecentsPage.cs:308-325`). A one-leg wash is not a weaker three-leg wash — it is the same Hero placement with
   two layers absent, so the window is lit from one corner. §2 W5 box 4 and W8 pair 5b now carry it.
2. **W8 pair 5 described pre-boundary behaviour.** "Settings publishes NO material and does not claim ⇒ whatever
   was showing when Settings opened is STILL showing" is false in 0.2.9: `ContentHost` itself claims **definite
   neutral** for every route no page publishes for (`ContentHost.cs:51-55`), so Settings eases the chrome to
   `NeutralGround` and Settings → Home is **two** 250 ms ramps, not one. Corrected in place.
3. **The light-theme surface list was built from sibling chapters, not from a grep.** 50 theme branches across 15
   files; five surfaces computed a colour and were in nobody's light arm (§2 W5B, §4.16). Two of them carry the
   *same* two-ratio pattern the concert file already documents as "the usual light-arm mistake".
4. **`EffectiveQuality` / `EffectiveVideoMaxHeight` are listed as pure CORE and are not.** Both read the file's
   mutable static `_cost` (`NetworkPolicy.cs:104`, `:122`), so §8's "add a test, it is four lines" cannot be
   honoured without changing the signature to take the cost as a parameter. Same for `ShouldDeferPrefetch`.
5. **The zoom-tolerance row counted a volume epsilon as a zoom site.** `WaveeApp.cs:231` is `SavedVolume`;
   `LyricsView.cs:593` is an alpha epsilon. Four zoom sites, not five — and unifying all six couples three
   unrelated subsystems to one constant.
6. **Two censuses were off and one citation family was stale.** `FocusVisualMargin` is 36 call sites at **four**
   values (not 37 at three; the 37th hit is a comment), `+2` is 22 sites (not 20), `+1` is 11 app sites (not 12),
   and the focus-restore census is 15 (not 14). `DetailTracks` has a **second** `IsItemEnabled` at `:1543` — the
   vertical/magazine table's own roving predicate — which no chapter had. And the whole `SceneRecorder` /
   `AppHost` / `DetailTracks` citation block had drifted 20–35 lines; every number in §0, §2, §3, §5 and §8 was
   re-read from `file:line` and corrected (§11 audit 38).

### 9.10 Line budget (this chapter)

| | lines |
|---|---|
| 0.2.9, the code this chapter owns outright | **≈ 4 150** — the transition/material/masthead trio 373 · the action platform 946 + `Menus.cs` 1 197 + `ActionIcons`/`AppAction`/`ActionRules` 275 · network policy ~150 + the three composition sites ~60 · drag 938 minus the ~110 that is ch 01's payload plumbing · the vacancy grammar 150 · `MorphKeys` 17 · `AppLocale` 44 · `ZoomAutoPolicy` 114 + the 52-line effect · the ambient policy 140 · the file-drop cue and scrim 40 · the first-run gate ~100. (The focus ring and the toast card are the *engine's*; this chapter specifies their use, not their code.) |
| plan §2 target | **nothing at all** for any of it. §2 allots this chapter no file; the earlier draft's 1 100 was `Playback.Os.cs`, which is now chapter 14's |
| honest estimate | **≈ 1 850 of NEW app lines**, plus ~2 100 that ports into files other chapters already budget: <br>· **settled (A7), owner I, not this chapter's L**: `Platform/Actions.cs` **950** (descriptor + targeting matrix + the seven reasons + the one action table + the thirteen first-party descriptors + the extension registry — folding what this chapter first split into `Actions.Table.cs`) <br>· `Platform/Actions.UI.cs` **1,200** (the menu vocabulary — down from 1 197 raw because the verbs move to `Entities/*` — icons, the row, the picker, the reason caption; folds what this chapter first split into `Actions.Menus.cs`) <br>· `Platform/Controls.cs` share **250** (the three dialog helpers + the one vacancy grammar + `Notify.Say`) <br>· `Platform/Design.cs` share **200** (the focus insets, the wash geometry, the cadence block, the ladders) <br>· `Platform/Platform.cs` share **400** (network policy 160, zoom policy 120, locale 60, ambient policy 60) <br>· `Platform/Drag.cs` **700** — ch 01 budgets the same file; this chapter adds no lines, only its spec <br>· `Shell/Shell.cs` + `Shell.UI.cs` + `Shell.Host.cs` share **450** (the recipes, the material publish + layer, the masthead band, the network chrome, the file-drop cue, the first-run composite) <br>· **`Entities/Entities.Fake.cs` 850** — the seed §7.2 demands: larger than today's 733 because it must also seed a playing track, a lyric document, a queue, a browse directory, a recents snapshot, a notification feed, three network signals and a light-theme pre-seed |
| **this chapter's share of §9.4's `Platform/` row** | **1 850** — now split as owner **I**'s two `Actions.*` files (2,150 together, settled by A7) plus this chapter's own three `Platform/*` shares (850), excluding `Drag.cs` which ch 01 carries and owner L keeps |
| documentation cost | **3 787 lines** of this chapter (28 wireframes, 123 parity items, 48 audit entries) |

### 9.11 Files missing from the §2 tree (this chapter's five)

1. **`Platform/Actions.cs` + `Platform/Actions.UI.cs`** — the action and extension platform, settled as two files by
   A7 (this chapter's first draft proposed four: `Actions.cs`, `Actions.UI.cs`, `Actions.Menus.cs`, `Actions.Table.cs`,
   under owner L — both the split and the owner are superseded). §2 had no file for ~2 500 lines of shipped code, and
   §5 had no owner (§9.9.7(2)); now owner **I**, Wave 4, landed before owner J's picker. `PinRowRule` is not here —
   it goes to `Sidebar.cs` (owner J).
2. **`Entities/Entities.Fake.cs`** — `Entities.SeedFake()`. Plan §5 assigns it to owner Q in a parenthetical and
   names no file. It is the Wave 5 gate's entire substance, **and it is needed in Wave 4** (§9.6(b)).
3. **`Platform/Drag.cs`** — chapter 01 proposes it; this chapter is its visual spec. Needed in **Wave 4** (§9.6).
4. **A home for the notification table** — ch 19 §7 gap 1; ch 14's toast bridge's input and this chapter's severity
   source.
5. ~~**A home for `SetupSession` / `SetupSession.MarkerEpoch`** — a process-static signal the app root subscribes to.~~
   **RESOLVED, plan §9.6 Q8, 2026-09-12: `Screens/Setup.cs` (CORE), which grows 950 → 1,150 to carry it.** The
   orchestrator's call, under the plan's own rule that the plan decides where code lives — no chapter named a home.

---

## 10. Parity checklist

Side-by-side against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`.
Items marked **[live]** need a signed-in session on both builds — run them one at a time, and never disturb the
user's own instance. "static" = a screenshot; "recording" = a frame capture, diffed; "probe" = a `WaveeNavProbe`
phase. Items marked **[0.2.9 FAILS]** are known defects this chapter names: they are expected to fail against the
kept build and are the bar 0.3 must clear.

**Focus and keyboard visuals**

1. **Press Tab from a cold window, static** — exactly one node carries a ring: a 2-DIP outer stroke and a 1-DIP inner
   one, concentric with the control's corners.
2. **Click the same control instead, static** — **no ring at all**. Focus moved; the visual did not.
3. **Tab, then click elsewhere, then Shift+Tab, recording** — the ring disappears on the click and reappears on the
   key, with no fade at either edge.
4. **In light and dark, static pair of the same focused chip** — light is a black outer / white inner ring; dark is
   white outer / black inner. Not the same ring at two alphas.
5. **Tab to a filter chip and to a track row, static pair** — the chip's ring sits **inside** its 1-DIP border
   (2 DIP in); the row's sits 1 DIP in and does not touch the rows above or below.
6. **Open the player-style flyout and Tab to a thumbnail card, static** — **[0.2.9 FAILS]** the ring is drawn 2 DIP
   *outside* the card and crosses its neighbour's padding. 0.3 insets it like every other bordered card (§9.9.4).
7. **Tab into a 400-track playlist, then press Tab again** — the second Tab leaves the list entirely. It does not
   walk to track 2.
8. **Tab into the list, then press ↓ five times, recording** — the ring walks down five rows; the page does **not**
   re-render (check the render census, or `FG_ALLOC_DIAG`'s `flush` segment stays flat).
9. **Tab into a card grid and press → at the end of a row** — focus lands on the next row's first card, not nowhere.
10. **With a track row current, press Tab** — focus reaches the row's expand chevron; Shift+Tab returns to the row.
11. **Open any dialog, press Tab repeatedly** — focus cycles inside the card and never reaches the window behind it;
    the initial focus is the accent button.
12. **Open a dialog from a button, close it with Escape, static** — focus is back on the button that opened it.
13. **Open a flyout from the player bar, close it by clicking the page** — focus does **not** jump; the popup never
    took it.
14. **Enter full-screen video from the rail, press Escape** — focus returns to the control that invoked fullscreen.
15. **[live] Navigate Home → an album with the keyboard, static** — record where the ring is afterwards. In 0.2.9 it
    stays on the sidebar row. Whatever 0.3 chooses (§2 W3), it must be the same on every route.

**Light theme, the derived surfaces**

16. **Switch to light and open an album whose cover is strongly coloured, static** — the shell chrome (title bar,
    toolbar, sidebar, dock) carries a perceptible tint of the record. Then measure it against a plain wallpaper.
17. **Same page, same light theme** — the **page ground** is close to neutral, and the record's identity is carried
    by the **Play capsule and the row chrome** instead. This is the design (§4.3), not a regression.
18. **Compare the same album in dark** — the page ground takes visibly more of the record's hue. The two themes are
    not the same design at two brightnesses.
19. **Light theme, Home, static** — three radial washes are visible and none of them is a smudge; the sidebar's
    secondary text still reads over the strongest wash pixel.
20. **Light theme, Home, resize the window to half width, static** — the washes re-scale with the window and none of
    them crosses the player dock line.
21. **Light theme, a detail page's Play capsule, static, beside the dark one** — the capsule's colour comes from the
    **opposite** grading half in each theme (§4.5). If they look like the same colour at two brightnesses, the two
    calls have been collapsed.
22. **Light theme, open the immersive stage, static** — the stage is **light**: a light veil, near-black ink, the
    same alphas as dark. It is not a black slab under light chrome.
23. **Light theme, immersive lyrics, static** — the active line, the inactive lines and the resync chip all read.
    Compare the blur σ against dark (§4.7) — UNVERIFIED whether it was tuned in both.
24. **Light theme, the rail's lyrics panel, static** — it uses the **theme** rungs, not the stage's. The two surfaces
    render the same document two ways and both are correct.
25. **Light theme, a now-playing deck face, static** — it is **identical** to dark. The frame around it (card stroke,
    shadow, corners) follows the theme; the face does not (§4.9).
26. **Light theme, a generated Liked cover, static** — identical to dark, and its wordmark still reads.
27. **Light theme, a docked video with the transport showing, static** — the scrim is the same gradient as in dark
    and the transport ink is on-media white.
28. **Light theme, library search with a match, static** — the highlight pill is the same blue as in dark and its
    text is the inverse rung.
29. **Light theme, an artist page with an ungraded hero, static** — the blend wash falls back to the **app accent**;
    in dark it falls back to grey (§4.8). Record the frame; decide whether it was chosen.
30. **Flip the theme while an album page is open, recording** — the whole window re-themes in place over ~250 ms; no
    remount, no flash, and the wash/tint layers keep their identity.

**The page-transition pairs**

31. **Home → an album card, recording at 240 fps** — the outgoing page is ~94 % faded by the time the incoming page
    starts moving (t ≈ 90 ms); the two never share the card at readable opacity.
32. **The same recording, frame t ≈ 150 ms** — the card already shows the album while the chrome still carries
    **Home's wash**. That frame is correct (§2 W8 pair 1).
33. **Browse → a playlist, recording** — the masthead band fades out over 120 ms **holding its last text**; it never
    blanks first and never collapses to zero height (the body below does not jump).
34. **Album → artist, recording the chrome only** — the shell tint goes colour → colour over ~250 ms. It never dips
    through neutral, and it never goes dark.
35. **Open an artist whose cover has not been graded yet (cold cache), recording the chrome** — the previous page's
    tint is **held**; the chrome does not blink to neutral and back.
36. **Disco → artist via Back, recording the chrome** — the artist page reclaims the material on reactivation. Then
    wait 10 s: the chrome must still be the artist's, not the discography's.
37. **Settings → Home, recording** — the flat tint fades toward neutral while three wash layers enter. This is the
    only pair where the material gains layers.
38. **[live] Any page → a module watch page with video playing, recording** — the swap **slides**; the video stays
    visible for the whole travel and does not wash out or vanish.
39. **[live] The same, but Neutral (a tab activation onto a module page)** — an honest cut. No slide, no fade.
40. **Back to a page parked two navigations ago, recording** — it reactivates without a layout transition (nothing
    animates into place), and its masthead is immediately correct.
41. **Open a fresh tab straight onto an album** — the ordinary directional recipe plays; it is not a special case.
42. **Turn reduced motion on and repeat 31, 33 and 37** — the card takes its reduced arm, the **masthead still
    fades** (deliberate), and the washes swap instantly.

**Network state**

43. **[live] Pull the network cable while a track is playing, recording the player bar** — the title becomes
    "Reconnecting…" in secondary, an indeterminate band sweeps the bar's top edge, the transport buttons stay live,
    and the scrub position does not reset.
44. **[live] Same moment, the chrome row** — the profile chip is **unchanged**. Reconnecting is a playback state, not
    an auth state.
45. **[live] Kill the network, then relaunch so the silent resume fails, static** — the profile chip is replaced by
    an **accent** `Reconnect` button, and the sidebar still shows its cached playlists.
46. **Same frame, the page body** — **[0.2.9 FAILS]** there is no offline banner anywhere: `OfflineBanner` has zero
    call sites (§9.9.5). 0.3 shows it above the kept content.
47. **Same, on a page with no cached content** — **[0.2.9 FAILS]** 0.2.9 shows "Something went wrong"; 0.3 shows the
    offline copy. "We could not ask" must not be drawn as "the answer was nothing".
48. **[live] Search for something with no network** — **[0.2.9 FAILS]** the result is an empty state reading "no
    results". It must say the request did not reach the server.
49. **Set the connection to metered (Windows ▸ Network ▸ Metered connection), then Settings ▸ Playback** — the
    status line distinguishes "metered, capped at X" from "the probe failed".
50. **[live] On a metered link, play a track and check the resolved quality in Diagnostics** — it is
    `min(user, cap)`, not the user's setting.
51. **On a metered link, scroll a long grid, recording** — covers arrive **later** than on an unmetered link
    (prefetch is deferred). Nothing on screen says "metered", and that is deliberate (§2 W9 D).
52. **Disable the network adapter entirely so the cost probe fails** — playback quality is **not** capped
    (unmetered-conservative).
53. **The chrome chip and the player bar together, offline with a stale track loaded** — **[0.2.9 FAILS]** the chip
    says offline while the bar confidently shows a track. 0.3's composition table (§2 W10) forbids the pair.

**Toasts and confirmations**

54. **Add a track to a playlist, static** — one card, Success severity: the success glyph on its plate, the tinted
    ground, the message, a `Go to playlist` action and a ✕.
55. **Add three tracks to three different playlists quickly, static** — three cards, stacked, and a fourth waits.
56. **Add the same track to the same playlist twice, recording** — the **same card refreshes**; its countdown
    restarts. There is no second card.
57. **Trigger an error toast (drop an unsupported file), static** — the Error ramp: critical plate, critical-tinted
    ground, inverse glyph.
58. **Report a problem ▸ copy for paste** — the toast lives **8 seconds**, not 5.
59. **Settings ▸ Notifications ▸ send a test event** — that toast lives 6 seconds. (0.3 folds it to 5 — §6.4.)
60. **Trigger a sticky notification toast** — it does not auto-dismiss; only ✕ or its action closes it.
61. **Undo an add from its toast** — the action runs and the card closes.
62. **[0.2.9 FAILS] Export an image from the artist gallery with the app language set to something other than
    English** — the toast reads "Image exported" in English (`ArtistGalleryLightbox.cs:386`).
63. **[0.2.9 FAILS] Force a video-attach failure** — the toast body is a raw `ex.Message`. Twelve sites do this
    (§6.4); 0.3 sends the exception to the log and a sentence to the user.
64. **Clear all history, static** — the accent ring is on **Cancel**, Cancel sits on the **right**, and pressing
    Enter **cancels**.
65. **The sidebar customizer's "reset to preset" confirm, static** — here the accent ring **is** on the primary. The
    two must not look alike.
66. **Open, in turn: rename a playlist · view credits · add to playlist · clear history · Settings ▸ Storage move
    cache · a sidebar item picker · report a problem · the lyrics inspector, static each** — the widths measure
    320 / 320 / 320 / 320 / 480 / 480 / 548 / 548. **Nothing measures a fifth width.**
67. **Any dialog, recording the open** — the card scales 1.05 → 1.0 over 250 ms while its opacity ramps over 83 ms;
    the close mirrors it at 167 ms.
68. **"Add to playlist", static** — exactly one button (Cancel). No stray "OK".
69. **The setup wizard and the after-update dialog, static** — 762 × 490 and 720 wide, both with the **same** corner
    radius, padding, border and shadow as a `ContentDialog`.
70. **Resize the window to 400 × 400 with a dialog open** — the card stays ≥ 320 wide and inside the viewport.

**The action platform**

71. **Open the sidebar customizer ▸ add an action row ▸ the action picker, static** — rows are 48 DIP with a 13/600
    label and an 11 DIP target summary; the selected row keeps its **action icon** and gains a check mark.
72. **Pick "Play" with target mode = Now playing while nothing is playing, static** — the row stays visible and a
    warning-glyph caption explains why it will not run. It is not hidden and it is not silently enabled.
73. **Bind a toggle action (Save to Liked Songs) to a track that is already saved** — the row renders **checked** and
    its icon is the filled variant.
74. **Scan the action picker's full list** — two consecutive rows never read the same label with the same icon
    (round-2 defect 6b; `SaveToLiked` vs `SaveToLibrary`).
75. **Confirm that `DeletePlaylist`, `Rename`, `AddToPlaylist`, `RemoveFromQueue`, `SelectAll`, `ViewCredits` and the
    Video verbs are ABSENT from the picker** — twelve deliberate exclusions (§2 W12).
76. **Invoke a confirm-required bound action** — the confirm dialog appears with the descriptor's copy (falling back
    to its label), and cancelling runs nothing.
77. **Invoke the same action with no overlay host present (a headless path)** — it refuses and reports
    `HostUnavailable`. It does **not** run unconfirmed.
78. **Rename a first-party action's loc key in a local build and reopen the picker** — the row renders `[key]`
    loudly, not blank (`WaveeActionTargeting.cs:137-139`).

**Empty, skeleton, error, offline**

79. **Open a cold album (clear the cache first), recording** — the skeleton reserves the hero band at its real size,
    and when content lands the hero **fades into a slot that is already its size**; the list does not shove down.
80. **During that skeleton, look for a scroll rail** — there is none. A shimmer with a live scroll thumb is the
    violation (§0.18).
81. **A brand-new account's library, static** — a display-face headline, one caption, one **Standard** button. No
    glyph, no accent button.
82. **A surface's error state, static** — the same grammar as the empty state: no red glyph, no accent Retry.
83. **Open a surface that is empty, navigate away and back** — it does **not** shimmer again (the crossing rule).
84. **[0.2.9 FAILS] Any surface with no network** — see items 46–48.

**Shared-element morph**

85. **probe: run `WAVEE_NAV_PROBE` and read the morph-key lines** — Home publishes at least one live key, the key
    format is `album:<uri>` / `pl:<uri>`, and the detail side publishes **none** (the deliberate null).
86. **Drag/navigate Home → an album, recording** — nothing flies. That is correct today and must stay a *documented*
    null, not a silently deleted convention.
87. **Grep the 0.3 tree for `MorphKeys`** — it exists, has ≥ 5 call sites, and its `null` destination still carries
    the explanatory comment.

**Localisation and long strings**

88. **Settings ▸ General ▸ Language, static** — four entries; Nederlands and 한국어 are visible and **greyed**.
89. **Pick a greyed entry** — nothing happens; the stored culture does not change.
90. **Set the app language to a long-string locale and open Settings ▸ Appearance and ▸ General** — every picker
    ellipsizes inside its fixed width (160 / 180 / 260 / 300) and none of them paints over its row's label.
91. **Same locale, an album page's context band, static** — the title gives way first; the command cluster keeps its
    room and does not clip.
92. **Same locale, every eyebrow in the app (Home module kinds, card labels, the release rung)** — all sentence
    case. No ALL-CAPS anywhere except the **Classic** track table's column headers.
93. **Same locale, the player bar's now-playing title, hover** — the Marquee runs. Turn the Marquee preference off:
    it ellipsizes and the edge fade goes with it.
94. **Set the OS to an RTL locale and launch** — the layout is **unmirrored**. This is the documented, expected
    result (§0.23), and the item exists so an accidental change is caught.

**Zoom**

95. **@1920×1080 with mode Auto, check Settings ▸ Appearance ▸ Zoom** — it reads **100 %**, not 125 % (snap down).
96. **@3440×1440 with mode Auto** — **150 %**, not 200 % (both axes bind).
97. **@3840×2160 with mode Dense** — 200 % (the ceiling); at 1180×760 Dense is 75 % and Auto is 100 %.
98. **With mode Auto, press `Ctrl +` once** — the zoom steps AND the mode flips to Manual; the policy never moves it
    again.
99. **Upgrade-path check: set `appearance.zoom` to 1.25 in the store with no `zoom.mode.bootstrap.version`,
    relaunch** — the mode comes up **Manual** and the zoom stays 125 %.
100. **Zoom to 200 % on a 1600-DIP-wide window, static** — the sidebar, the detail page and the track table have all
     **demoted** exactly one or more tiers. Zoom out to 67 %: they promote. Nothing crosses a tier in the other
     direction.

**Ambient cadence, drag, first run, `--fake`**

101. **Launch on mains, open a page with a perpetual loop (Concerts, or Liked with the cover wall), recording** —
     ~30 fps. Unplug and hold: ~24 fps, 2–4 s later. Unplug and re-plug within 1 s: no change at all.
102. **Click another app so Wavee loses focus, recording Wavee's window** — perpetual loops keep advancing, spaced
     ≥ 33 ms.
103. **Drag a track row, recording the pickup** — the chip tilts ~4° and scales to 1.02, then eases flat within
     150 ms; it enters from 0.92 scale and opacity 0.
104. **Select 3 tracks and drag, static** — one chip with a count badge ⑶ and two stacked cards at 0.85 opacity,
     offset 4 and 8 DIP. Not three chips.
105. **Drag a track over an editorial playlist** — the not-allowed glyph plus a caption naming "not editable". Repeat
     under a sort, under a filter, and mid-sync: three different captions, in that priority order.
106. **Drag a track into the middle of a playlist, static** — an insertion line plus up to `DefaultPreviewCap` cards
     with a 1-px accent border; a 50-track drag shows "+47" on the last.
107. **Hold a drag over a collapsed sidebar folder for 500 ms** — it springs open; sweep across in 200 ms and it does
     not.
108. **Drag a file from Explorer over the window, static** — the page dims under the spotlight scrim while the title
     bar and player bar stay fully lit; the pill is centred over the **content region**.
109. **Wipe `%LOCALAPPDATA%\Wavee` and launch** — the wizard is the **whole window**; then complete it and check the
     composite frame of §2 W24 item by item.
110. **Launch `--fake` and open every route in `ShellRoutes`** — each renders its **loaded** state. **[0.2.9 FAILS]**
     for search (partial), browse, both section families, recents, concerts, prerelease, the player bar, the rail,
     the queue, lyrics, video and every network state. That is the baseline this item exists to move.
111. **`--fake`, relaunch twice and diff a screenshot of Home, an album and the sidebar** — pixel-identical.
112. **`--fake`, open the sidebar customizer and preview a template twice** — the confirmation miniature shows the
     **same** covers both times.
113. **`--fake`, force light theme from the seed** — **[0.2.9 FAILS]** there is no way to; items 16–30 are therefore
     manual today. §7.2 adds the pre-seed.

**The five derived-colour surfaces §4 first missed, and the four states W27 checks**

114. **Light theme, a concert hub card and a concert detail's 192-DIP title band, static beside dark** — the accent
     pull is visibly *weaker* in light (0.18 vs 0.30 on the card, 0.18 vs 0.50 on the band). If the two themes
     show the same saturation the two ratios have been collapsed into one, which is the failure
     `ConcertUi.cs:356-359` already documents. The concert TITLE must still read over the light band.
115. **Light theme, a Home shelf of accent-tinted media cards, static, then hover one** — rest and hover are
     `0.08` / `0.12` toward the accent, not the dark arm's `0.12` / `0.18`. Check the card title and subtitle
     still clear their ratio over the *worst* (most saturated) card on the shelf.
116. **Light theme, the now-playing panel with a strongly coloured cover, static** — the hero wash is `0.10` toward
     `Lift(Accent(SchemeFor))`, not `0.18`. Three derivations stacked; a single wrong link reads as "the rail lost
     its colour" or "the rail is shouting", and both look like a different bug.
117. **Light theme, the search hero and a nav preview for the same entity, static pair** — both follow the theme's
     grading half, but one takes `Accent` and the other `ChromeAccent`. They are *allowed* to differ; what they may
     not do is both become the `ChromeSchemeFor` half, which is what a "one helper for which-half" port produces.
118. **Launch cold onto Home and look for the masthead band, static** — there is none at all (a zero-size node,
     not a faded one). Then go to Browse: the band appears and the column's height changes once. Then go back to
     Home: the band fades to opacity 0 but the height does **not** change again. First crossing is the only one
     that moves layout.
119. **Home → Recents, recording the chrome** — the window goes from lit at three corners to lit at ONE: the Hero
     layer cross-fades (its artwork key changed) while Weekly and Mix simply exit with nothing behind them. Then
     Recents → Home: three layers enter. Neither direction runs the 250 ms brush ramp — that is the flat tint's,
     and the tint is not what is moving here.
120. **Turn reduced motion on, then drag a track row** — the chip still tilts, still scales and still travels, and
     the insertion gap still opens. That is **correct and deliberate**: a direct-manipulation gesture's moving
     visual is the thing the user is moving. The item exists so a reduced-motion audit does not file it as a miss.
121. **Turn reduced motion on and hover any media card** — the card does **not** lift or scale. If it does, the
     tier accessors have been turned into consts (`WaveeMotion.cs:179`, `:182`), which is the one reduced-motion
     regression the engine cannot catch for us (`:17-29`).
122. **Set the OS to a Windows contrast theme and launch** — the app renders in its ordinary light or dark palette,
     unchanged. This is the documented, expected result (§9.8, W27), and the item exists so an accidental
     `ThemeKind.HighContrast` arriving from the engine is caught as a *change* rather than shipped as a guess.
123. **Set OS text scaling to 200 % and launch** — Wavee's own type metrics are unchanged. `TextScaleFactor` is
     proposed in `large-display-scaling.md` §3.5 and is not implemented; UNVERIFIED whether 0.3 adopts it.

---

## 11. Audit log

Adversarial re-read against the 0.2.9 sources on **2026-09-12**, after a completeness review re-scoped this chapter
from "OS surfaces, ambient cadence, window state, dialogs, drag, first run, `--fake`, the index" to the twelve
concerns of §1.1. Every constant in §3, every line reference in §1, §2, §4, §5 and §8, the 107 `Toast.Show` sites,
the 37 `FocusVisualMargin` sites, the engine's focus-ring geometry and palette entries, the `Loadable` state set, the
nav probe's two route tables, `ShellRoutes` + `ConcertRoutes`, the `ZoomAutoPolicy` ladder (recomputed for all eight
worked rows) and the nine deck faces' theme greps were read back from `file:line`. **This chapter was written without
launching either build** (the task forbade it), so §10 is a procedure derived from code, not an executed run — see
item 12.

**SECOND ADVERSARIAL PASS, 2026-09-12 (items 38–48).** This chapter was the one of the thirty-two that had never had
an independent audit pass, and the pass found that its own §11 was not a substitute for one. The sources were re-read
from scratch rather than re-checked against the chapter's claims: every engine citation against `..\fluent-gpu`
(the whole block had drifted 20–35 lines), every `file:line` in §0, §2, §3, §5 and §8, six stated line counts, three
app-wide censuses re-grepped (`FocusVisualMargin`, the focus-restoration sites, `.ToUpper*`), and — the finding that
produced the most new material — the **theme-branch grep the light-theme section should have opened with**, which
turns up fifty branches across fifteen files where §4 had walked eight surfaces borrowed from sibling chapters.
Two wireframes are new (**W5B**, **W27**), one is new inside W8 (**pair 5b**), one section is new (**§5.7**), one
is new inside §4 (**§4.16**), §9.9.8 collects the six places this chapter was wrong about itself, and §10 grew from
113 to **123** items. Nothing correct was deleted; every retraction names what replaced it.

| # | kind | section | note |
|---|---|---|---|
| 1 | missing → added | §2 W1-W4, §0.1-6 | **Focus had no owner.** The ring's tokens, geometry, keyboard-only flag, the two sanctioned insets, the roving tab index and the six restoration paths were scattered across 15 chapters and five stray "focus returns to X" sentences. Collected, with the engine's `EmitFocusRing` arithmetic and both palettes. |
| 2 | missing → added | §2 W3 path 5-6 | **Page navigation moves no focus at all.** Fourteen `FocusNode`/`RestoreFocus` sites in the app, none on a route change or a KeepAlive reactivation. It is not a decision anywhere; §2 W3's 0.3 rule is the smallest thing that makes it one. |
| 3 | missing → added | §9.9.4, parity 6 | **A third `FocusVisualMargin` value exists** — `-2` at `PlayerStyleFlyout.cs:101, :182`, with no comment and no precedent, on a control whose shape is exactly the `+2` case. Filed as a defect, not a variant. |
| 4 | missing → added | §4 (whole section) | **Light theme had one 20-row palette and one per-chapter section (19 §4.4).** The eight surfaces that COMPUTE colour were unspecified in light. Each is now stated with its formula, both arms, and its readability floor or explicit absence of one. |
| 5 | overclaim → checked | §4.10 | **Search highlight is NOT a derived-colour surface.** The completeness review listed it with the other eight; both its tokens are palette entries and `AccentSelectedTextBackground` is `#0078D4` in both themes (`PaletteBuilder.cs:253, :341`). Recorded as a non-finding so it is not re-investigated. |
| 6 | missing → added | §4.9 | **Three surfaces have no light arm and all three are correct**: the nine deck faces (zero `ThemeKind` hits across ~4 800 lines), Liked's generated covers, and the video scrims (`static readonly GradientSpec` on the token class — structurally invariant). The rule extracted: a scrim over MEDIA is theme-invariant; a scrim over the app's own ground is not; the stage is the one surface that is both. |
| 7 | missing → added | §2 W7-W8 | **The page-transition pair matrix.** Three channels were each specified separately (18 §5 the recipes, 18 §7 the material route table, 10 parity 34 the single documented pair). Composed here as a timeline plus the seven walks users take, with the surprising frame named in each. |
| 8 | missing → added | §2 W9-W10, §0.11-12 | **Network state had no visual owner.** `App/NetworkPolicy.cs` was named by one chapter as a Settings status line. Composed here across the chrome chip, the player bar and the page body, with the two visible metered effects and a decision table. |
| 9 | missing → added | §9.9.5, parity 46 | **`Components/OfflineBanner.cs` has ZERO call sites under `Features/`.** The component that makes the fourth readiness state real exists, is complete, and is never shown. |
| 10 | missing → added | §6.4 | **The toast inventory.** 107 `Toast.Show` sites across 38 files, one row each: copy key, severity, duration, action. Derived findings: 12 sites show a raw `ex.Message`; 7 carry English literals (5 developer, 2 not); exactly 2 non-default durations exist; 13 have actions and they fall into 5 kinds; `AddedToPlaylist` is reached from 9 sites and correctly relies on message-dedupe. |
| 11 | missing → added | §6.3, §6.5 | **Zero of the 107 toasts announce.** The announce chokepoints are elsewhere (`Announcer.SayThrottled` at four sites, `assertive` at three). The 0.3 rule puts announcement inside the one `Notify.Say` helper, keyed on severity and on whether an action is present. |
| 12 | missing → added | §2 W12, §0.15-16 | **`Actions/Extensibility/*` (946 lines, not the ~1 200 the review estimated) was unread by every chapter**, along with `Menus.cs` (1 197 lines / 81 KB). Added: the descriptor's visual fields, the four bound-row states, the seven disabled reasons with their literal loc keys, the confirmation gate's exact path, and `BuiltInExtensionTable`'s twelve deliberate exclusions with each one's stated reason. |
| 13 | missing → added | §7.1 | **The four-state readiness table.** `LoadState` has three members (`Pending, Ready, Failed`); offline is the fourth and has no representation. The per-surface table shows the player bar is the ONLY surface with all four, because `PlayerState` is a five-member enum. The fix routes through `DetailNotice` (which already refuses to un-render a page) rather than a new component. |
| 14 | missing → added | §2 W14 | **The shared-element morph pair, end to end.** Small, dormant, and asserted on by the nav probe at five sites — i.e. deleting it as dead code breaks the probe, not the UI, which is how a probe gets deleted next. |
| 15 | missing → added | §2 W15 | **Long strings.** The estimator's arithmetic worked against a 30-character German string, the four fixed picker widths with the expander-header bug they prevent, the Marquee-vs-ellipsis rule, and the caps refusal explained as a *tracking* decision (30/1000 em is the value that survives sentence case). |
| 16 | missing → added | §2 W16 | **The zoom ladder, worked for eight base extents**, plus the "who wins" rule and the one-direction tier-crossing statement. `large-display-scaling.md` §3.2 is inherited explicitly — it had no chapter. |
| 17 | missing → added | §9.7 | **The gate and instrumentation contract.** What each of the five existing instruments can prove, and the seven claims nothing catches, each with its cheapest closure. |
| 18 | **wrong → corrected** | §9.7.1 | **The nav probe drives TWELVE destinations, not thirty.** `HeavyRoutes` (8 entries, 3 kinds) + `CheapRoutes` (5) + four shot-path routes. It never visits `show:`, `prerelease:`, `disco:`, `module:`, `browse:<uri>`, `home-section:`, `browse-section:`, recents, history, settings, api-console, playback-diagnostics, connect-diagnostics, whatsnew, home-customize, concerts, `artist-concerts:` or `concert:` — **eighteen**, including every surface chapters 09, 12, 16, 17, 27 and 28 own. The previous draft caught that the gate passes on blank pages; it missed that the instrument is blind to more than half the routes. |
| 19 | missing → added | §9.4 | **Nobody had summed the chapters against plan §2.** Done, per folder: `Entities/` +96 %, `Shell/` +78 %, `Platform/` +63 %, `Screens/` +9 %, `Playback/` +4 %; whole tree ≈ +50 %. §5's "30 % → a named partial" cannot absorb it. The table names a decision column and says it must be filled before Wave 0, because the skeleton is where partials have to appear. |
| 20 | missing → added | §9.5 | **~49 proposed files against a cap of 100.** 86 + 49 = 135. DoD item 7 is the mechanism that would silently drop the bold rows, and the bold rows are the three surfaces with no plan file at all (the detail frame, recents + history, video). |
| 21 | missing → added | §9.6 | **Two wave-ordering impossibilities.** (a) The Wave 5 gate names routes that are Wave 6 owner R/S/T. (b) The Wave 4 gate requires "a working sidebar" from a seed that is Wave 5 owner Q. Fix: a `depends on` column, and `Entities.Fake.cs` (at least the sidebar/rootlist + playback + network slices) moves to Wave 0 or Wave 4. |
| 22 | missing → added | §9.9.6 | **The profile page is a phantom.** §2's annotation and 15 §9's `User.Page.Profile.cs` name a page with no route, no `PageFor` arm, no index row and no wireframe, inside a release whose scope table says "New features" are out. Strike it or admit it. |
| 23 | missing → added | §9.9.7(2) | **`Platform/Actions.*` has no owner in plan §5.** It is the one surface whose owner is genuinely ambiguous between Wave 4 **I** and **J**; resolved to **L** on the same argument that puts `Platform/Drag.cs` there — it is a pure `Platform/` file and I and J are both consumers. |
| 24 | **RETRACTION** | §9.9.1, §12.1, §12.3 | **OS surfaces belong to chapter 14, not here.** `14-os-surfaces.md` (106 KB, written 2026-09-12 08:36) owns SMTC, the taskbar button, the thumbnail toolbar, the jump list and Windows toasts across 28 wireframes. This chapter's W1–W6 drew the same surfaces and §9.8 budgeted the same `Playback.Os.cs` 1 100. **W1–W6 are deleted**, the `Playback.Os.cs` row is deleted from the line budget, §12.3 keeps only the pointer, **§12.1's "14 is still unused" sentence is struck**, and the two budgets reconcile to **one number: 830** (ch 14 §9's own count). Two findings from the old §9.7 are handed to 14: Wave 3 owner H has no design contract (which 14 §9 makes independently), and `AmbientPowerPolicy` is not playback and stays here. |
| 25 | **RETRACTION** | §9.9.2, §2 W16 | **Window state and the DPI hop belong to chapter 18.** The claim that "18's only window-state row is 'resize suppresses layout transitions'" was true when written and is now stale: 18's audit item 26 added W23, W24 and five motion rows from the same citations. 29 W15's window-state and DPI halves are **deleted**; what is kept is the worked zoom ladder and the "who wins" rule, now §2 W16. The rest of the old §9.7 was re-checked item by item (§9.9.2's table); **§9.7.6 is still true — a grep for "first run" in `18-shell-frame.md` returns zero hits.** |
| 26 | **RETRACTION** | §9.9.3 | **Chapter 30 has been renumbered and audited.** It now runs the full twelve sections including `## 2. Wireframes` (W1–W29 plus §2.0–2.4), §3 Tokens, §4 Colour & material, §5 Motion (four subsections) and §11 Audit log. The earlier draft's §5.2 ("the live preference flips table, hosted here until 30 is renumbered") is therefore no longer a host — §5.5 keeps only the cross-cutting form. 30 also now carries the light/dark frame (W22, W20), the zoom picker (W23) and the localisation section (§2.4), which supersedes parts of completeness items 9, 10 and 18. **One sub-item survives: 30's §5 owner entry is still a range, "4–6".** Resolved to owner **L** on 30 §7.3's own argument. |
| 27 | unverified | §10 | The parity checklist has **not been executed**. Every item is derived from a `file:line` fact in this chapter, and the **[live]** items additionally need a signed-in session on both builds. The items marked **[0.2.9 FAILS]** (6, 46, 47, 48, 53, 62, 63, 84, 110, 113) are written as known defects that 0.3 must not reproduce — they are expected to fail against the kept build. The checklist now runs to **123** items (114–123 added by audit 38–44). |
| 28 | unverified | §4.7, §4.8 | Two light-theme claims the code does not settle: whether chapter 22's lyric blur σ and backdrop values were tuned in **both** themes, and whether the artist blend wash's asymmetric fallback (app accent in light, neutral scheme in dark) was chosen or inherited. Parity 23 and 29 record the frames either way. |
| 29 | unverified | §2 W3 path 6 | Whether a KeepAlive-parked subtree's `NodeFlags.Focused` / `FocusVisual` survive `ReleaseInactiveResources`. The engine's park path releases image pins; nothing in the code says what happens to focus flags. It matters only if §2 W3's 0.3 rule adopts "restore the slot's last focused node". |
| 30 | unverified | §9.4 | The line-budget sum takes each chapter's own honest estimate at face value and de-duplicates two known overlaps by hand (the `Controls.cs` shares of chs 00/01/02/27/29, and chs 25+26's sidebar total). The **0.2.9 line counts** those rest on were each re-counted by their own chapter; the 0.3 projections are projections. The plan-side figures are read directly from `wavee-0.3-implementation.md:38-81`. |
| 31 | verified, no change | §3 | The `Actions/Extensibility/` total is **946** lines, not the ~1 200 the completeness review estimated (`BuiltInExtensionTable 303 · WaveeActionTargeting 251 · WaveeRegistryTable 173 · WaveeExtensionRegistry 148 · IWaveeExtension 39 · PinRowRule 32`). `Menus.cs` is **1 197 lines / 81 KB** — the size is in line length, not line count. Both corrected in the sources block and §9.10. |
| 32 | verified, no change | §2 W22 | The implicit **480** for a three-button dialog (`ContentDialog.cs:283`) is reached by exactly two call sites, both in `SettingsPage.Storage.cs` — and `SidebarPickers.DialogW` independently equals 480. The ladder is four rungs, not five; confirmed rather than assumed. |
| 33 | verified, no change | §0.13, §4.13 | Wavee contributes **nothing** to a toast's colour except the severity byte: glyph, icon plate, inverse foreground and tinted ground all come from `SeverityVisuals.For`, shared with InfoBar "so their severity mapping CANNOT drift". This is why §6.4 can be a table of severities rather than of colours. |
| 34 | wrong → corrected | §7.2 | Chapter 26's DATA GAP row lists the miniature's playlist indices as 1, 3, 5, 8, 10, 12, 14; its own audit item 11 corrects that to include **2 and 7** (the grid-strip cells, `SidebarMiniature.cs:128`). This chapter carries the corrected list. |
| 35 | wrong → corrected | §2 W26 | `--fake` search is **not** empty, as a reading of `FakeSource.SearchAsync` would suggest: `SpotifyExportSource` owns Search and returns tracks/albums/artists/playlists from `FakeData.Search(q)` plus name-matched exported playlists and artists (`SpotifyExportSource.cs:142-155`). PARTIAL, not ZERO. |
| 36 | missing → added | §7 DATA GAP 8 | **`NetworkPolicy` has no fake arm.** `NetworkStatus.Subscribe`/`SubscribeCost` are real NLM calls, so on the fake backend the cost is whatever the machine reports — the metered arm is untestable offline and `ShellAuthState.Offline` is unreachable. Three signals in `SeedFake` close it, and without them §2 W9's parity items (43–53) cannot run offline at all. |
| 37 | wrong → corrected | §2 W5, W6, W10 · §4.2-4.8 · §6.4-6.6 | **2026-09-12: sixteen `parity N` cross-references in the body named the wrong items.** They were written against an earlier ordering and were not renumbered when §10 landed at **113** items, so a reader following §4.5's "Parity 14 is exactly this check" arrived at the fullscreen-video focus item. Re-pointed, subject by subject, against the checklist as it now stands: **11 → 16** (the light shell tint, §4.2) · **12 → 17** (the light page ground and where identity is carried; both the §2 W5 and the §4.3 citation) · **13 → 19** (Home's washes and the sidebar ink over the worst wash pixel, §4.4) · **14 → 21** (`ChromeSchemeFor` is the opposite grading half, §4.5) · **15 → 22** (the stage's light arm as a readability check, §4.6) · **16 → 23** (the lyric blur σ in light, §4.7) · **item 17 → item 25** (the deck faces byte-identical in both themes, §2 W6 A) · **18 → 29** (the artist blend wash's ungraded fallback, §4.8) · **22 → 46** (`OfflineBanner` has no call site) · **23 → 47** (one error copy for a network failure and a missing entity) · **24 → 53** (the chip and the bar disagreeing) · **26 → 63** (the twelve raw `ex.Message` sites, §6.4) · **27 → 62** (the English-literal toasts; both the §6.4 and the §6.6 citation) · **45 → 65** (the restorative confirm must not look like the destructive one, §6.5). **parity 6** (§0.3, §2 W1, §9.9.4) already matched and is unchanged, and the two citations of *other* chapters' checklists — `ch 18 §6, parity 43` in §2 W19 and `ch 10 parity 2-3` in §2 W24 — are theirs and were left alone. No checklist item, wireframe or prose claim was edited; only the numbers that name them, and every ASCII-box line kept its exact width. |
| 38 | **wrong → corrected** | §0, §2 W1-W4, §3, §5.3, §7.1, §9.7.3 | **The engine citation block had drifted 20-35 lines and every number in it was stale.** Re-read from `file:line` on 2026-09-12 against `..\fluent-gpu`: the focus-ring emit block is `SceneRecorder.cs:2593-2605` (the `NodeFlags.FocusVisual` test at `:2596`), not `:2570-2580`/`:2573`; `EmitFocusRing` is `:3367-3396` (primary stroke `:3386-3389`, the 1 px secondary `:3390-3396`, the concentric corner growth `:3381-3384`), not `:3340-3370`; the `LoadingBarSuppressors` rail skip is `:3402`, not `:3374`; `MoveFocusVisual` is `AppHost.cs:2487`, not `:2481`; unhandled Escape is `InputDispatcher.cs:3577`; `LoadState` is `Loadable.cs:5`; `Toast.Show` is `Toast.cs:93`. Verified UNCHANGED and left alone: `Tok.FocusThickness = 2f` at `Tokens.cs:456`, both palette arms at `PaletteBuilder.cs:254-255`/`:342-343`, the 2-D focus walk tie-break at `InputDispatcher.cs:3680-3681`, `SetFocus` doc + `PaintDirty` at `:3763-3781`, `ItemsView.cs:393`/`:1574-1580`, `OverlayHost.cs:470-490`, every `ContentDialog` metric at `:110-117`/`:283`/`:361-369`, every `DragChip` constant at `:68-85`, `SeverityVisuals` (27 lines, `For` at `:20-26`) and `Tok.ScrimBottom`/`ScrimTop` at `Tokens.cs:376`/`:385`. |
| 39 | **wrong → corrected** | §2 W1, W3, W4, §3 | **Three app censuses were wrong and one file's citations were 30+ lines stale.** `FocusVisualMargin`: **36** call sites at **FOUR** distinct values (`+2` ×22 · `+1` ×11 · `−2` ×2 · `−3` ×1), not "37 sites, THREE values" — the 37th grep hit is the comment at `Design/WaveeCta.cs:12`. W1's `+2` list was labelled 20 and already contained 22 entries; its `+1` list was labelled 12 and contained 11 app sites plus 2 engine ones. **Two of the eleven `+1` sites are conditional** (`live ? inset : default` — `FacePiles.cs:132`, `LikedFactsPanel.cs:696`), so `FocusInsetRow` must stay a value a call site may decline. The focus-restoration census is **15** sites across 7 files, not fourteen, and **five** pass `visual: true` (not three): `DetailTracks.cs:2183` **and `:2300`**, `MergedChromeRow.cs:263`, `LibraryV3Search.cs:101` **and `:154`**. `DetailTracks` itself: the slot root is `:3568-3569` (not `:3534-3535`), `IsItemEnabled` is `:1019` (not `:985`), and the Classic caps transforms are `:2509`/`:3743` (not `:2475`/`:3709`). |
| 40 | missing → added | §2 W4, §2 W15 | **Two complete-census misses.** (a) `DetailTracks` has a **second** `IsItemEnabled`, at `:1543` — the vertical/magazine table's own roving predicate with its own `VerticalTrackStart` offset. Two tables, one rule; 0.3 must port both or the arrows walk headers on one of the two skins with nothing else on screen to show it. No chapter had it. (b) The deck faces' caps transform is **ten** `ToUpperInvariant` calls across **four** faces (`CassetteDeck.cs:319`, `:320`, **`CdDeck.cs:295`**, `VuDeck.cs:285`, `:296`, `:297`, `WinampDeck.cs:348`), not the three an earlier draft listed — and it is Invariant where the Classic header is `CurrentUICulture`, which is the opposite choice for the opposite reason. Twelve `.ToUpper*` calls in the app, total. |
| 41 | **wrong → corrected** | §2 W5 box 4, W8 pair 5, **new W8 pair 5b** | **"Home's three radial washes … on Home only" is false, and W8's Settings pair described pre-boundary behaviour.** `ContentHost.PublishesShellMaterial` (`:184-186`) admits four families; **three** publish a wash — `home` (three legs, `HomePage.cs:229-237`), `home-section:`/`browse-section:` (**one** leg, `HomeSectionPage.cs:177-182`) and `recents` (**one** leg, `RecentsPage.cs:308-325`). And Settings does **not** leave the previous page's material up: the boundary claims **definite neutral** for every route no page publishes for (`ContentHost.cs:51-55`), so Settings → Home is two 250 ms ramps, not one. A new W8 pair 5b draws the three-leg → one-leg swap, the only pair where layers leave with nothing entering behind them. |
| 42 | missing → added | §0.7, **§2 W5B**, §4.16, parity 114-117 | **The light-theme surface list was assembled from sibling chapters instead of from a grep, and missed five surfaces.** `Tok.Theme ==` / `ThemeKind.Light` / `ThemeKind.Dark` returns **50 branches across 15 files**. Five compute a colour and were in nobody's light arm: the concert plates and their **two** per-theme accent pulls (`ConcertUi.cs:262-263` `0.30/0.18`, `:356-360` `0.50/0.18`), the accent card fills every media card uses (`MediaCard.cs:48-56`, `0.12/0.08` and `0.18/0.12`), the now-playing hero wash (`NowPlayingPanel.cs:203-209`, `0.18/0.10`), the nav-preview accent seed (`NavPreview.cs:64`) and the search hero (`SearchHero.cs:39-41`). The rule extracted: **a `Lerp` toward a plate is a light-arm problem even when the plate is a token** — the colour flips, the RATIO does not, and `ConcertUi.cs:356-359` already calls one ratio in both arms "the usual light-arm mistake". Also: there are **five** answers to "which half of the grading", not the three §4.5 names. |
| 43 | missing → added | §2 **W27** (new), §9.8, parity 122-123 | **The states this chapter does not own were SILENT, which reads as "nobody looked".** W27 is the frame that fixes it: eleven states, each with the grep that settles it — **zero** `Dpi` reads anywhere under `src/apps/Wavee` (the OS scale reaches the app only inside `Viewport.Size`, and `ZoomAutoPolicy.cs:30-38` is the one place that names it, to say it must not be read directly); **zero** window-state reads (every size decision is a `Viewport.Size` tier); **zero** contrast-theme support, checked not assumed, the only two `highContrast` strings being Spotify's wire key; **zero** RTL layout code; **zero** `TextScaleFactor` reads. Two rows are decisions, two are chapter 18's, one is unverified, six are this chapter's. |
| 44 | missing → added | §2 W7, W13, §5.5, **§5.7** (new), §7.1, §2 W10 rule 0, parity 118-121 | **Four cross-cutting mechanisms had no row anywhere.** (a) **The masthead band's cold-start arm**: until a family resolves once, `_heldTitle` is null and Render returns a genuinely zero-size node (`ShellMastheadBand.cs:56-57`) — so the first crossing INTO a family is the one transition that moves the column's height, which contradicts a flat reading of "it never collapses to Height 0". (b) **The vacancy grammar has two SCALES** — `Build` (PageHero) and `Compact` (Subtitle, under ~340 DIP) — for *both* voices (`EmptyState.cs:34-45`, `ErrorState.cs:17-43`), so §1.2's one `Vacancy` is four voices × two scales from one builder, and the offline voice needs Compact because the queue and friends rails go offline too. (c) **Reduced motion is a VALUE, never a branch** (`WaveeMotion.cs:88-91`: gating a HOOK on it "changes the hook COUNT between renders and crashes the reconciler the moment the flag flips mid-session — a resize grip flips it"), plus the app-side interaction-tier suppression the engine cannot do (`:17-29`, `:179`, `:182`) and the deliberate absence of a reduced arm on the drag chip. (d) **The live re-theme is epoch-gated**, not event-gated: `WaveeApp.cs:56-59` arms the 250 ms cross-fade only when a guarded `Tok` mutator actually advanced `Tok.Epoch`, because Windows broadcasts `ImmersiveColorSet` for identical colours and a re-theme is a whole-app re-render. |
| 45 | **wrong → corrected** | §2 W9, W16, §3, §8, §9.8 | **Four numeric claims.** (a) `EffectiveQuality` is `NetworkPolicy.cs:95-105` and `EffectiveVideoMaxHeight` is `:119-125`, and **neither is pure**: both read the file's mutable static `_cost` (`:104`, `:122`), so §8's "add a test, it is four lines" needs the signature to take the cost as a parameter first. (b) The `0.004f` tolerance is **four** zoom sites, not five — `WaveeApp.cs:231` is the `SavedVolume` persist epsilon and `LyricsView.cs:593` is an alpha epsilon; folding all six into one constant couples three subsystems. (c) The app's zoom range is **50 %–250 % by hand** (`ZoomLadder.Steps`, twelve rungs, `ZoomLadder.cs:17-18`) and **75 %–200 % by policy**; §9.8's "67 %–200 %" was wrong at both ends. (d) W16's Dense column is blank below 1600×900 because Auto and Dense are **identical** for any ratio ≥ 1 — the modes differ only in the floor, so Dense is a small-display mode with a large-display name. |
| 46 | **wrong → corrected** | the sources block, §1.1, §3 | **Six stated line counts were wrong.** `WaveeActionDescriptor.cs` is **227**, not 338. `NetworkPolicy.cs` is **189**, not ~150. `LikedCoverLeaves.cs` is **299**, not ~140. `ShellMasthead.cs` is **53**, not 59; `ShellMastheadBand.cs` is **131**, not 129; `ShellRoutes.cs` is **79**, not 77. Confirmed correct and left alone: `Actions/Extensibility/*` = **946**, `Menus.cs` = **1 197**, `WaveeNavProbe.cs` = **2 915**, the drag quartet = **938**, `ShellMaterial.cs` + `ShellMaterialLayer.cs` = **192**, `StageArm.cs` + `StageInk.cs` = **225**, `PageNavMotion.cs` = **122**, `MorphKeys.cs` = **17**, `ZoomAutoPolicy.cs` = **114**, `AmbientPowerPolicy.cs` = **140**, `SetupGating.cs` = **201**, `ToastEscalator.cs` = **255**, `ShellRoutes` = 16 exact + 10 prefixes, the nav probe = 8 heavy + 5 cheap. |
| 47 | verified, no change | §2 W16, W25, §5.4 | **The zoom ladder and the ambient cadence were re-derived and both hold.** All eight `Suggest` rows recomputed from `ZoomAutoPolicy.cs:71-89`: 1180×760 → 1.00/0.75 · 1366×768 → 1.00/0.75 · 1600×900 → 1.00 · 1920×1080 → 1.00 (1.20 snaps down) · 2100×1200 → 1.25 · 2560×1440 → 1.50 · 3440×1440 → 1.50 (width alone says 2.00) · 3840×2160 → 2.00. Every `AmbientPowerPolicy` citation in W25 and §5.4 was re-read line by line and **not one was wrong** — `:45` `:49` `:51` `:54` `:66` `:75-81` `:88-92` `:94-97` `:104-116` `:118-123` `:125-131` `:132-139` all land exactly. So did every `WaveeShell` zoom-effect and file-drop citation (`:591` `:598-604` `:615` `:624` `:414-421` `:1341-1342` `:1351-1359` `:1418-1432`) and every `SettingsShared.Confirm` and picker-width citation (`:29`, `:31`, `:38`; `160/180/260/300` at `Appearance.cs:344`, `:402`, `General.cs:85`, `:227`). |
| 48 | **added** | §9.9.8 | A section this chapter did not have: **where THIS chapter is wrong**. Every earlier §9.9 entry retracts a claim about the plan or a sibling chapter; six findings above are about this chapter's own text, and they are collected there so the next reader can tell a corrected claim from one that was always right. |
| 49 | **consistency 2026-09-12** | header, §1.2, §9.9.7(2), §9.10, §9.11, §12.1, §12.4 | This chapter's own resolution of the unowned action platform (§9.9.7(2): "resolved to owner L", four files `Platform/Actions.{cs,UI.cs,Menus.cs,Table.cs}`) is superseded by cross-chapter arbitration **A7**: two files, `Platform/Actions.cs` (CORE) + `Platform/Actions.UI.cs`, owner **I**, landed before owner J's picker; `PinRowRule` goes to `Sidebar.cs` instead. Corrected: the header's "0.3 target"/"Waves" lines, §1.2's three `Actions.*` rows, §9.9.7 item 2, §9.10's line-budget breakdown, §9.11 item 1, §12.1's chapter-29 row and §12.4's "three rows have no owner" bullet. §11's earlier numbered findings (dated before A7) were left as the historical record. |

---

## 12. The chapter index

Nothing in `docs/` outside this folder references these chapters, and the implementation plan cites none of them.
This table is what makes a dropped surface visible before Wave 5, and it is the artefact the fold into
`wavee-0.3-implementation.md` actually needs.

**Chapter 14 is in use.** It was an unused number when this chapter was first drafted; `14-os-surfaces.md` now owns
the SMTC card, the taskbar button, the thumbnail toolbar, the jump list and Windows toasts. The earlier sentence
"14 is still unused — nothing anywhere cross-references it" is **struck** (§11 audit 24). Every number 00–31 is now
claimed.

### 12.1 Surface → chapter → 0.3 file → wave → owner

| ch | surface | 0.3 file(s) | in plan §2? | wave · owner |
|---|---|---|---|---|
| 00 | design system: tokens, type, colour, cover palette, materials, motion, CTAs, zoom | `Platform/Design.cs`, `Platform/Controls.cs` | yes | 4 · **L** |
| 01 | the track row (+ the drag payload layer) | `Entities/Track.UI.cs`, `Platform/Controls.cs`, **`Platform/Drag.cs`**, **`Entities/Track.Drawer.cs`** | partly | 5 · **M** / 4 · **L** |
| 02 | cards, shelves and shared small controls | `Platform/Controls.cs` + per-entity `Card`/`ListRow` adapters | partly | 4 · **L** + 5 · M/N/O/P |
| 03 | the shared detail frame (hero, rail, band, skeleton, notices) | **`Entities/Detail.cs`**, **`Entities/Detail.UI.cs`** | **NO** | proposed 4.5 · M |
| 04 | the detail track table (chrome, command bar, filters, reorder) | `Entities/Track.UI.cs`, **`Entities/Track.Table.cs`** | partly | 5 · **M** + **O** |
| 05 | album / single / compilation / prerelease | `Entities/Album.{cs,UI.cs,Page.cs}` | yes | 5 · **M** |
| 06 | playlist | `Entities/Playlist.{UI.cs,Page.cs}` | yes | 5 · **O** |
| 07 | Liked Songs (cover treatments, facts bento) | `Entities/User.{UI,Page}.cs` + **`User.Liked.cs`, `User.Facts.cs`, `User.Cover.cs`, `User.Facts.UI.cs`** | partly | 5 · **O** |
| 08 | artist page + discography | `Entities/Artist.{cs,UI.cs,Page.cs}` + **`Artist.Discography.cs`** | partly | 5 · **N** |
| 09 | show, episode, module page | `Entities/Show.*`, `Episode.*`, `Platform/Modules.{cs,UI.cs}` | yes | 5 · **M** + 6 · **T** |
| 10 | Home landing (frame, wash, facets, reveal) | `Entities/Home.{cs,UI.cs,Page.cs}` | yes | 5 · **P** |
| 11 | Home cards and module renderers | `Entities/Home.UI.cs` + **`Home.Cards.UI.cs`, `Home.Artists.UI.cs`** | partly | 5 · **P** |
| 12 | Home "see all" section page + the Home customizer | `Entities/Home.Page.cs` + **`Home.Host.cs`**, **`Screens/HomeCustomize.UI.cs`** | partly | 5 · **P** |
| 13 | search + browse directory/category | `Entities/Search.*`, `Browse.*` + **`Browse.Page.cs`** | partly | 5 · **P** |
| **14** | **OS surfaces: the SMTC media card, the taskbar button, the thumbnail toolbar, the jump list, Windows toasts** | `Playback/Playback.Os.cs` (**830**, §9.9.1) + **`Platform/Notify.cs`**, **`Notify.Host.cs`** | partly | 3 · **H** — **a wave with no design contract** (§9.9.1) |
| 15 | library master-detail (albums / artists / podcasts) | `Entities/User.{UI,Page}.cs` + **`User.Page.Library.cs`** | partly | 5 · **O** |
| 16 | recents + history | **`Entities/Recents.{cs,UI.cs,Page.cs}`** + `Shell/Shell.{cs,Host.cs,UI.cs}` | **NO** | 4 · I (history) · **recents unassigned** |
| 17 | concerts hub / schedule / detail | `Entities/Concert.{cs,UI.cs,Page.cs}` + **`Concert.Feed.cs`** | partly | 5 · **N** |
| 18 | shell frame: window (incl. state, snapping, the DPI hop), chrome row, omnibar, tabs, trail, masthead, material, page transitions | `Shell/Shell.{cs,UI.cs,Host.cs}` | yes | 4 · **I** |
| 19 | shell overlays: the toast strip, dialogs' host, command palette, notification centre, tips, banners | `Shell/Shell.UI.cs`, `Shell.Palette.cs` | yes (no lines allocated) | 4 · **I** |
| 20 | the player bar | `Shell/Shell.UI.cs` + `Shell.cs` + **`Shell.PlayerBar.UI.cs`** | partly | 4 · **I** |
| 21 | right rail, now-playing view, queue panel, immersive stage | `Shell/Rail.UI.cs`, `Entities/Queue.UI.cs` + **`Rail.Styles.UI.cs`, `Rail.cs`, `Queue.cs`, `Stage.UI.cs`, `Stage.cs`** | partly | 4 · **K** + 5 · **Q** |
| 22 | lyrics (rail, immersive, pipeline) | `Shell/Lyrics.{cs,UI.cs,Host.cs}` + **four named partials** | yes | 4 · **K** |
| 23 | the nine deck faces and their machines | `Shell/Deck.{cs,UI.cs}` + **`Deck.Faces.cs`** | partly | 4 · **K** |
| 24 | video: docked, in-window PiP, pop-out, fullscreen, placement | **`Shell/Video.{cs,UI.cs,Host.cs}`** | **NO** | **unassigned** (K or I) |
| 25 | the sidebar pane: three designs, rows, modes, rail | `Shell/Sidebar.{cs,UI.cs,Host.cs}` | yes | 4 · **J** |
| 26 | the sidebar customizer + the projection pipeline | `Shell/Sidebar.*` + **`Sidebar.Doc.cs`** | partly | 4 · **J** |
| 27 | settings pages + diagnostics screens | `Screens/Settings.*`, `Screens/Diagnostics.*`, `Platform/Controls.cs` | yes | 6 · **R** + **S** |
| 28 | setup wizard, What's New, feedback/report | `Screens/Setup.*`, `ReleaseNotes.*`, `Feedback.UI.cs` + **`Feedback.cs`** | partly | 6 · **R** |
| **29** | **cross-cutting: focus & keyboard visuals · the light arm of every derived colour · the page-transition pair matrix · network state · the toast + confirmation inventory · the action & extension platform · empty/skeleton/error/offline · shared-element morph · localisation & long strings · the zoom ladder · dialogs · drag · first run · `--fake` · the gates · this index** | `Platform/Design.cs`, `Platform/Controls.cs`, `Platform/Platform.cs`, settled (A7) **`Platform/Actions.cs` + `Platform/Actions.UI.cs`** (two files, not four — this chapter's first draft split `Actions.Menus.cs`/`Actions.Table.cs`, since folded in), **`Platform/Drag.cs`**, `Shell/Shell.{cs,UI.cs,Host.cs}`, **`Entities/Entities.Fake.cs`** | partly | 4 · **L** (design, controls, platform, drag) + 4 · **I** (transitions, material, network chrome, first run, and — A7 — `Actions.cs`/`Actions.UI.cs`) + 5 · **Q** + 6 (the gates) |
| 30 | appearance & layout preferences (the variant dimension of every surface above) | `Platform/Platform.cs`, `Platform/Design.cs` + every `*.UI.cs` | yes | **4–6 — no single owner; resolve to L** (§9.9.3) |
| 31 | the fake-data seed as a surface | **`Entities/Entities.Fake.cs`** | **NO** | 5 · **Q** — **needed in Wave 4** (§9.6(b)) |

### 12.2 Route → chapter (every renderable destination)

| route key | `RouteKind` (§9.3) | chapter | in the nav probe? (§9.7.1) |
|---|---|---|---|
| `home` | `Home` | 10 | ✔ |
| `home-customize` | `HomeCustomize` | 12 | ✖ |
| `home-section:<uri>` | `HomeSection` | 12 | ✖ |
| `browse-section:<uri>` | `BrowseSection` | 12 | ✖ |
| `search` | `Search` | 13 | ✔ (shot path only) |
| `browse` | `BrowseDirectory` | 13 | ✔ |
| `browse:<uri>` | `BrowseCategory` | 13 | ✖ |
| `album:<uri>` | `Album` | 05 (frame: 03, table: 04) | ✔ (shot path only) |
| `prerelease:<uri>` | `Prerelease` | 05 | ✖ |
| `pl:<uri>` | `Playlist` | 06 (frame: 03, table: 04) | ✔ |
| `liked` | `Liked` | 07 | ✔ |
| `local` | `Local` | 15 / 03 | ✔ |
| `artist:<uri>` | `Artist` | 08 | ✔ (shot path only) |
| `disco:<k>:<uri>` | `Discography` | 08 | ✖ |
| `show:<uri>` | `Show` | 09 | ✖ |
| `module:<uri>` | `Module` | 09 (+ 24 for a watch page) | ✖ |
| `albums` | `LibraryAlbums` | 15 | ✔ |
| `artists` | `LibraryArtists` | 15 | ✔ |
| `podcasts` | `LibraryPodcasts` | 15 | ✔ |
| `recents` | `Recents` | 16 | ✖ |
| `history` | `History` | 16 | ✖ |
| `concerts` | `Concerts` | 17 | ✖ |
| `artist-concerts:<id>` | `ArtistConcerts` | 17 | ✖ |
| `concert:<id>` | `Concert` | 17 | ✖ |
| `settings` | `Settings` | 27 | ✖ |
| ~~`api-console`~~ | ~~`ApiConsole`~~ | 27 | **DELETED, plan §9.6 Q7, 2026-09-12 — struck, not a 0.3 route at all** |
| `playback-diagnostics` | `PlaybackDiagnostics` | 27 | ✖ |
| `connect-diagnostics` | `ConnectDiagnostics` | 27 (**not in `ShellRoutes`** — §9.3(d)) | ✖ |
| `sidebar-customize` | `SidebarCustomize` | 26 | ✔ (shot path only) |
| `whatsnew` | `WhatsNew` | 28 | ✖ |
| *(anything else)* | `NotFound` | 18 W21 | ✖ |

**12 of 30 are probed. Eighteen are not** (0.2.9's own count, `api-console` among the 18) — **in 0.3, one row above
is struck (Q7, 2026-09-12): 12 of 29 are probed, seventeen are not** — and the Wave 5 gate and DoD item 3 both rest
on that list (§9.7.1).

### 12.3 Non-route surfaces → chapter

| surface | chapter |
|---|---|
| the chrome row, omnibar, tabs, drill trail, masthead band, shell material, page transitions, **window state / snapping / the DPI hop** | 18 (the pair **matrix** and the material's ownership rule: **29** §2 W7-W8) |
| the player bar | 20 (its **Reconnecting** arm's composition with the chrome: **29** §2 W9-W10) |
| the right rail · now-playing view · queue panel · immersive stage | 21 |
| the sidebar pane (+ its collapsed rail and narrow drawer) | 25 (drawer: 18) |
| the deck faces | 23 (their theme-blindness as a *decision*: **29** §4.9) |
| lyrics (rail peek, rail panel, immersive) | 22 (the ink MODE seam in light: **29** §4.7) |
| the four video surfaces + the placement menu | 24 (their scrims' theme-invariance: **29** §4.9) |
| the toast **strip** — geometry, stacking, the severity ramp | 19 |
| what the strip **says** — 107 sites, copy key, severity, duration, action | **29** §6.4 |
| teaching tips · the command palette · the notification centre · runtime banners | 19 |
| every `ContentDialog` (the width ladder, the confirm shape) | **29** §2 W22-W23 (callers: 19, 22, 26, 27, 28) |
| the drag chip · insertion line · gap preview · spring-load · refusals · file-drop cue | **29** §2 W17-W21 (call sites: 01, 02, 06, 12, 18, 25, 26) |
| the keyboard focus ring, the roving tab index, focus restoration | **29** §2 W1-W4 |
| the light arm of every surface that COMPUTES a colour (**thirteen** of them, §2 W5 + W5B) | **29** §4 + §4.16 (the token palette itself: 00; the preference view: 30) |
| offline · metered · reconnecting, as one composed picture | **29** §2 W9-W10 |
| the action & extension platform (descriptor, registry, targeting, menus) | **29** §2 W12 |
| empty · skeleton · error · offline as one four-state system | **29** §7.1 |
| the shared-element morph pair | **29** §2 W14 |
| localisation, long strings, the disabled locales, the RTL position | **29** §2 W15 (the preference view: 30 §2.4) |
| the zoom **ladder** and the who-wins rule | **29** §2 W16 (the window sequence: 18 W23-W24; the picker: 30 W23) |
| the setup wizard (pre-auth leaf and post-auth cover) | 28 (the shell's scrim: **29** §2 W24) |
| the after-update dialog · the highlight viewer · the report dialog | 28 |
| **the SMTC media card · the taskbar button · the thumbnail toolbar · the jump list · Windows toasts** | **14** |
| the ambient cadence, the power policy, the inactive-window throttle | **29** §2 W25, §5.4 (tokens land in 00 §5) |
| reduced motion as a VALUE, the hook-count rule, and the app-side interaction-tier suppression | **29** §5.7 (per-surface arms: each chapter's own §5) |
| window state · the DPI hop · Windows contrast themes · RTL · `TextScaleFactor` — **the states this chapter checked and does NOT own** | **29** §2 W27 (the frame + the greps); owners: 18 W23/W24, and §9.8 for the two decisions |
| the first-run composite frame | **29** §2 W24 (belongs in 18 — §9.9.2, still true) |
| the `--fake` fixture inventory | **29** §7.2 + **31** |
| the gates: the nav probe, ReuseGuard, the allocation counters, the perf tour | **29** §9.7 |
| the accessibility / high-contrast / RTL position | **29** §9.8 (states 00 §0 and 30 §2.4) |

### 12.4 How to use this index

* **Before Wave 5 closes**, walk §12.2 and §12.3 and check that every row has a file that exists and an owner who
  built it. A row whose 0.3 file column is bold (**proposed**) has no home in plan §2 and will be dropped by
  default — §9.5 is the decision that stops that happening silently.
* **Chapters 03, 16, 24 and 31 have no plan file at all** — a shared detail frame, recents + history, every video
  surface, and the fake seed. Those four are the most likely surfaces to vanish silently.
* **Three rows had no owner when this chapter was first written**: recents (ch 16), video (ch 24) and the action
  platform (**29** §9.9.7(2), this chapter's first draft resolved it to L). All three are now settled in the plan:
  recents is owner P/Wave 5 with history split to owner I/Wave 4 (§9.5 index row 16), video is owner K/Wave 4
  (A13), and the action platform is owner **I**/Wave 4 as two files, `Platform/Actions.cs` + `Actions.UI.cs`, not L
  (A7). **Chapter 30's owner is a range**, which is the same problem spelled differently (§9.9.3).
* **Chapter 14's OS half sits in Wave 3**, a wave with no design contract, no wireframes and no parity checklist —
  a finding both 14 §9 and this chapter's §9.9.1 make independently.
* **§12.2's last column is a to-do list**: eighteen ✖ rows are eighteen routes no automated check has ever opened.

**answers 2026-09-12: Q8 (plan §9.6) resolves this chapter's own §7 item 6 and §9.11 item 5 — `SetupSession` /
`SetupSession.MarkerEpoch` has a home.** The orchestrator's call, under the plan's own rule that the plan decides
where code lives: `Screens/Setup.cs` (CORE), which grows 950 → 1,150. Both items are marked resolved rather than a
missing file. **Q7 also touches this chapter in five places**, all struck rather than silently edited: the
§9.6(a)-adjacent 0.2.9 `--fake` readiness table's `api-console` row (folded into "the two diagnostics pages"), the
§9.3 `RouteKind` enum sketch's `ApiConsole` member, the Wave-5-gate note "`settings`, `whatsnew`, `api-console` and
`playback-diagnostics` are Wave 6" (now four destinations, not five), and the §9.7.2 route-probe table's
`api-console` row and its "12 of 30 probed" tally (now 12 of 29, `api-console` never a 0.3 route to probe).
