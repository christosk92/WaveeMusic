# Design system (tokens, type ramp, colour, cover palette, materials, motion curves, CTAs, zoom) — 0.3 visual fidelity contract

> **0.2.9 sources** (all under `src/apps/Wavee/`, 4 276 lines total):
> `Design/WaveeTokens.cs` 297 · `Design/WaveeType.cs` 283 · `Design/Surfaces.cs` 505 · `Design/WaveePalette.cs` 333 ·
> `Design/CoverPaletteLeaves.cs` 266 · `Design/WaveeMotion.cs` 191 · `Design/WaveeOnMedia.cs` 137 ·
> `Design/StageArm.cs` 143 · `Design/StageInk.cs` 82 · `Design/WaveeCta.cs` 243 · `Design/WaveePicker.cs` 287 ·
> `Design/WaveeTheme.cs` 36 · `Design/WaveeAccentCtx.cs` 31 · `Design/Glyphs.cs` 37 · `Design/AppearancePrefs.cs` 30 ·
> `Design/SearchHighlight.cs` 81 · `Features/Shell/ShellMaterialLayer.cs` 133 · `Features/Shell/ShellWashGeometry.cs` 55 ·
> `Features/Shell/PageNavMotion.cs` 123 · `App/ShellMaterial.cs` 59 · `App/ZoomAutoPolicy.cs` 114 ·
> `Components/MorphKeys.cs` 17 · `Features/Detail/DetailRevealRamp.cs` 27 · `Features/Player/FrameTime.cs` 27 ·
> consumer surfaces read for call-site truth: `Features/Shell/SettingsPage.Appearance.cs` 651,
> `Features/Shell/ContentHost.cs` (the video-safe classification, `:147-152`), `Features/Shell/ShellMastheadBand.cs`
> (`:23-27`), `Features/Shell/WaveeShell.cs:150` (`ContentPaneCorners`), `Features/Detail/ContextBandLayout.cs:27,61`,
> `Features/Player/StageLayout.cs:217` (`ScrimBaseA`), `Features/Detail/DetailTrackTableRules.cs`,
> `Components/TrackRow.cs:118-124`, `SpotifyLive/CoverColorPlane.cs` 556, `Program.cs` (theme/font/zoom seed),
> `assets/loc/en-US.json` (`settings.appearance.*`, `settings.choice.*`), `assets/fonts/`;
> read for §5.1 (the dormant morph seam): `Features/Detail/DetailShell.cs:235-257`, `Features/Recents/RecentsPage.cs:1693-1707`,
> `Components/MediaCard.cs:172-178, 959-981, 1111-1143`, `Components/LikedSongsArtwork.cs`, `Features/Detail/DetailRail.cs:99-104`,
> `Features/Diagnostics/WaveeNavProbe.cs` (the seven `CollectMorphKeys`/`FirstMorphKey` reads);
> read for §6.8 / W18 (the focus contract): `Components/TrackRow.cs:547-548`, `Features/Detail/DetailTracks.cs:2143-2150, 3534-3535, 4170-4174`,
> `Features/Video/VideoFullscreenSurface.cs:129-147`, `Features/Player/ImmersiveLyricsSurface.cs:157, 199-204`,
> `Features/Sidebar/Modes/LibraryV3/{LibraryV3Search.cs, LibraryV3Chips.cs}`, `Features/Detail/PlaylistInlineEdit.cs:265-274`,
> and the engine's `Render/SceneRecorder.cs:3340-3369` / `Foundation/NodeFlags.cs:51` / `Controls/OverlayHost.cs:468-490`.
> `App/ShellUi.cs` was in the original source list and is **not** a design-system file — it is rail/overlay chrome
> state and belongs to `18-shell-frame.md` / `21-right-rail-npv-queue-stage.md`. Nothing in this chapter reads it.
> **0.3 target**: `Platform/Design.cs` (tokens, type, colour, palette, materials, motion), `Platform/Controls.cs`
> (`WaveeCta`, `WaveePicker`, `Surfaces`, `SearchHighlight`) — **Wave 4, owner L**.
> After Wave 0 every 0.2.9 path below lives at `src/apps/_old/Wavee/<same relative path>`.

Every other chapter in `docs/plans/wavee/wavee-0.3-ui/` cites this one. This chapter owns the numbers; the others own
how their surface spends them. Where a value here disagrees with a page chapter, this one wins.

---

## 0. The non-negotiables

1. **Every text node resolves a size AND a line height AND a weight, from one of eight ramp rungs.** The rungs are
   12/16 · 14/20 · 14/20-600 · 18/24 · 20/28-600 · 28/36-600 · 40/52-600 · 68/92-600 (`..\fluent-gpu` `Dsl/Typography.cs:39-46`).
   Note the ONE default that is not `TextPrimary`: `Ui.Caption` ships bound to `Tok.TextSecondary`
   (`Dsl/Typography.cs:39`), so `Eyebrow` and every "Caption at 600" rung are SECONDARY unless the call site sets
   `Color`, and `TrackMeta`'s `.Secondary()` is a restatement rather than a change.
   Two sanctioned off-ramps exist and are named here so a third is a regression: `PivotLabel` (19/25, §3) and
   `WaveePicker.Label`, a raw `TextEl` whose line height is `size + 4` (`WaveePicker.cs:86-94`) — at its default
   `size = 12` that lands exactly on the 12/16 rung; any other `labelSize` leaves the ramp.
   A bare `with { Size = 13f }` keeps the previous rung's line box and destroys the vertical rhythm — pinned by
   `DesignTokenConvergenceTests.EveryAlias_ResolvesSizeLineHeightAndWeight` (`Wavee.Tests/DesignTokenConvergenceTests.cs:74`).
2. **Weight is 400 or 600 — with exactly six sanctioned divergences, all named.** Three display-face 700s
   (`ArtistDisplay` 84/96, `ArtistTitle` 48/60, `ArtistCompactTitle` 32/40 — `WaveeType.cs:155/167/179`) and three
   SemiLight 350s (`PivotLabel` 19/25, `NpvLyric` 20/28, `StatHero` 28/36 — `WaveeType.cs:219/230/254`). A seventh is a
   regression.
3. **The accent has exactly three jobs and they never mix.** *Action* = a solid accent plate under on-accent ink, at
   most one per screenful. *Selection* = a short accent bar/pill against a control's edge, reserved geometry.
   *Decor* = accent as ink or wash, never a plate, never selection-shaped. Accent is **never** structure (border,
   divider, chevron, disclosure) — those take `Tok.StrokeDividerDefault` / `Tok.TextSecondary`
   (`WaveeTokens.cs:10-51`; `VoiceUnificationTests.AccentRoles_AreNamed:108`).
4. **The detail page's ground is hue-from-the-record, lightness-and-saturation-from-the-page, and it is a whisper over
   live Mica.** L is FORCED to 0.15 dark / 0.94 light; S is CAPPED at 0.30 dark / 0.16 light; the plane paints at
   α 0.20 dark / 0.30 light (`WaveePalette.cs:192/204`, `CoverPaletteLeaves.cs:139`). Two albums by the same artist
   must read as the *same page* in two different colours. No blurred cover band, ever
   (`CoverPaletteLeaves.cs:60-73` is the tombstone; `DetailPageToneTests` asserts the absence).
5. **Every chrome band is an unpainted omission over live Mica.** The shell root paints nothing; title row, sidebar
   and player dock are paint-site omissions; the content region is `FileArea` (`#FFFFFF80` light / `#3A3A3A4C` dark)
   with a 1 px `Tok.StrokeCardDefault` on its LEFT+TOP edges and ONE rounded corner
   (`ContentPaneCorners = (8,0,0,0)`, `WaveeShell.cs:150`) and **no shadow** (`WaveeTokens.cs:86-106`).
6. **Interaction scale is three tiers and nothing else**: Subtle 1.02/0.98, Standard 1.04/0.96, Emphatic 1.07/0.92
   (`WaveeMotion.cs:39/43/48`). Every accessor returns exactly `1f` under reduced motion, because the engine's
   hover/press channel carries no reduced-motion policy of its own (`WaveeMotion.cs:17-29`, `ScaleTier.Hover:179`).
7. **Interaction durations are the three WinUI rungs — 83 / 167 / 250 ms — and nothing between them**
   (`WaveeMotion.cs:55/57/59`; `MotionSystemTests.DurationLadder_IsTheWinUiLadder:143`). Structural page/pane/flyout
   transitions are asymmetric and authored per surface; they are NOT on this ladder.
8. **Page swaps are a fade-through, not a cross-fade**: exit fades in place over 120 ms on `Easing.EaseOut`; enter
   starts at 90 ms, when the outgoing page is ~94 % gone, sliding ±8 DIP on `Easing.SmoothOut` over 250 ms
   (`PageNavMotion.cs:49-68`). Two full-bleed pages must never be legible at the same time.
9. **Every art slot is a solid, cover-tinted tile before its bitmap lands — never a hole, never a grey wall.** The
   tile is the neutral placeholder (`#2A2A2A` dark / `#F2F2F2` light) lerped 0.55 toward the cover's own graded
   colour, resolved through an image-keyed plane that *enqueues on a miss*, so rendering the art IS the request
   (`Surfaces.cs:57-111`).
10. **A cover ≥ 80 DIP breathes while it loads (1.0↔0.5 opacity over 1 s) and stops the instant it settles;** below
    80 DIP it is a cheap static tile so a 50 k-row list pays nothing per item (`Surfaces.cs:203-230, 453-505`).
11. **The accent ornament under a section header is a 20 × 2 rule at +2 DIP, never a capsule beside the title** —
    a capsule is selection geometry (`Surfaces.cs:328-356`).
12. **A labeled media primary is a 36-DIP `Radii.Full` capsule with 18/6/18/7 padding and a Bold label; a naked icon
    button is a 32 × 32 `Radii.Control` square; a circle is only ever a FAB on media.** Three rows in the geometry
    table, and a new icon affordance must be one of them (`WaveeCta.cs:29-43`).
13. **Colour on media is theme-INVARIANT** (white ink on a black scrim in both themes) — the single exception is the
    immersive stage, which owns its own polarity through `StageArm` (`WaveeOnMedia.cs:24-30`, `StageArm.cs:6-35`).
14. **Motion reads `FrameTime.NowQpc` (the frame's predicted present time), never `Environment.TickCount64`**
    (`FrameTime.cs:19-27`). TickCount64 steps in ~15.6 ms quanta and makes anything driven off it stutter at 120 Hz.
15. **App zoom is one global multiplier folded into the window scale** (`OS DPI × zoom`), auto-derived from the
    display against a 1600 × 900 design box, snapped DOWN to a plateau rung. There is no per-surface size tier
    (`ZoomAutoPolicy.cs`, `large-display-scaling.md §3.1`).
16. **No app node ever paints a focus ring.** The engine draws WinUI's dual visual (2-DIP `Tok.FocusOuter` outer +
    1-DIP `Tok.FocusInner` inner, concentric with the control's own corner) and only when focus arrived from the
    KEYBOARD — `NodeFlags.FocusVisual` is set by Tab/arrows and never by a pointer press
    (`..luent-gpu` `Render/SceneRecorder.cs:3340-3369`, `Foundation/NodeFlags.cs:51`). A surface owns exactly one
    knob, `FocusVisualMargin`, and overrides the control template's value only for the five reasons in §6.8.

---

## 1. Anatomy

### 1.1 The 0.2.9 token layer, as a dependency tree

```
ENGINE (..\fluent-gpu, read-only)
├─ Tok.*                          Dsl/Tokens.cs            the WinUI-faithful colour token set (live theme reads)
│   ├─ Tok.Palette                = WaveeTheme.ResolvePalette() = Tok.NeutralPalette   WaveeTheme.cs:15
│   ├─ Tok.Theme                  ThemeKind.Light | Dark; bumps Tok.Epoch → Reconciler.RethemeAll()
│   └─ Tok.SetAccent(ramp)        System-mode only: the OS accent ramp             WaveeTheme.cs:31-32
├─ Spacing.XXS..XXXL, PageWide    Dsl/Spacing.cs:11-18,22  2·4·8·12·16·20·24·32 + PageWide 36 (Gutter 24 == XXL)
├─ Radii.None/Control/Card/Overlay/Pill/Full  Dsl/Radii.cs:10-15   0 · 4 · 8 · 8 · 16 · 999(clamped to half-box)
├─ Ui.Caption..Ui.Display         Dsl/Typography.cs:39-46  the 8-rung type ramp (Caption defaults SECONDARY)
├─ Expressive.*                   Dsl/Expressive.cs:14-38  Stagger 40, Quick 150, Fast 250, Slow 400, DistBase 8, BlurSmall 2
├─ Motion.ControlFaster/Fast/Normal   Dsl/Motion.cs:12-14  83 / 167 / 250
├─ Motion.ReducedMotion           Dsl/Motion.cs:24         the process-wide flag, read as a VALUE
├─ Easing.SmoothOut / EaseOut     Foundation/Easing.cs:411,396   cubic-bezier(.22,1,.36,1) / 1-(1-t)²
├─ Elevation.Card/CardHover/Tooltip/Flyout/DockTop/Dialog  Dsl/Elevation.cs:18-43   see §3 for all six
├─ ZoomLadder.Steps/Min/Max/Default/Snap/In/Out/Percent   Foundation/ZoomLadder.cs:17-27
│    Steps = 0.5 · 0.67 · 0.75 · 0.8 · 0.9 · 1 · 1.1 · 1.25 · 1.5 · 1.75 · 2 · 2.5  (TWELVE rungs; 1f at index 5)
├─ Tok.FocusOuter / FocusInner   Dsl/Tokens.cs:455-456   the dual ring's two bands; FocusThickness 2 is a const
│   └─ Element.FocusVisualMargin  Dsl/Element.cs:296      the ONE app-side focus knob (negative = ring OUTSIDE)
└─ ColorContrast.Over/Flatten/Ratio/PickContrast/MeetsAaText/RelativeLuminance

WAVEE — GEOMETRY
├─ WaveeSize                      WaveeTokens.cs:54-76     control/nav/row/dock heights, thumb ladder, PageMaxW
├─ PlayerDock                     WaveeTokens.cs:79-84     BarH 72, Reserve 72
└─ (per-surface *Layout classes live in their own chapters; they read WaveeSize)

WAVEE — COLOUR
├─ WaveeAccent {Action, Selection, Decor}     WaveeTokens.cs:39-51   the accent budget
├─ WaveeColors                    WaveeTokens.cs:107-297
│   ├─ ShellGround / ContentSurface / ContentLayer      the opaque + layer ladder (:163-210)
│   ├─ Toolbar / Sidebar / PlayerBar / FileArea / Content / ContentAlt   the translucent MUX rungs (:127-213)
│   ├─ RowZebra / RowHover / RowPressed / *Zebra        the list-row ladder (:230-242)
│   ├─ SelectedRest / SelectedHover / SelectedPressed   the NAV selection ladder (:272-274)
│   └─ ChromeHover / ChromePressed / Badge / PremiumText / FloatingChrome / FloatingPane
├─ WaveeOnMedia                   WaveeOnMedia.cs         the ONE on-media ladder (scrim, glass, ink plate, ink, light button)
├─ StageArm (readonly record struct) StageArm.cs         the immersive stage's POLARITY; pure over ThemeKind
│   └─ StageInk                   StageInk.cs             the live-theme facade over StageArm
└─ WaveePalette                   WaveePalette.cs         cover ARGB → ColorF; ToColor/Lift/Vivid/Hairline/
                                                          HairlineHover/TextInk/ChromeAccent/ChromeFromPayload/
                                                          Accent/BackgroundDark/TintedDark/PageTone/PageToneNeutral*/
                                                          DataDotInk/ToHsl/FromHsl/Neutral/BarSurface

WAVEE — TYPE
└─ WaveeType                      WaveeType.cs            18 aliases + EyebrowTracking 30

WAVEE — SURFACES / RECIPES
├─ Surfaces                       Surfaces.cs:53-446   (file 506 lines incl. CoverShimmer)
│   ├─ ImageDecodeScale.For       :21-44   DIP edge × Viewport.Scale → ceil to 8 px grid, clamp [8, 2048]
│   │                                      (Ceiling :25, BucketPx :31; edge ≤ 0 / non-finite ⇒ 0; scale ≤ 0 ⇒ 1×)
│   ├─ PlaceholderFor / WatchedPlaceholder / TintedPlaceholder   :70-111
│   ├─ SchemeFor / ChromeSchemeFor                       :141-155  page half vs chrome half (OPPOSITE theme)
│   ├─ Shimmer / Artwork / ArtworkFill / Mosaic          :207-324
│   ├─ AccentRule / AccentHeader / SectionHeader         :339-400
│   ├─ SectionBand / HomeHeroBackdrop / ArtistHeroVeil   :173-192, :416-445
│   └─ CoverShimmer : Component                          :453-505  breathe while loading, settle on ready
├─ CoverPaletteLeaves             CoverPaletteLeaves.cs:18-48  four leaf mounters (Watch lives HERE, never in a page Render)
│   ├─ CoverPageTonePlane         :74-169   the detail page's ground; α 0.20/0.30; BrushTransitionMs 250
│   ├─ CoverArtistBlendWash       :171-196
│   ├─ CoverKeyedVeil             :199-217
│   └─ CoverShellTintBinder       :223-264  publishes ShellMaterialState; claims on mount + reactivation
├─ SearchHighlight.Row            SearchHighlight.cs:18-70  the accent match pill — TWO arms (single-line / wrapping)
│   └─ LineBoxFor                 :75-80   14→20, 12→16, else ceil(size × 1.43)
└─ WaveeIcons / WaveeFonts        Glyphs.cs              wavee-icons.otf (3 PUA glyphs) + bundled SegoeFluentIcons.ttf
    ├─ WaveeIcons.PlayNext U+E900 · PlayAfter U+E901 · Lyrics U+E902      Glyphs.cs:12-14
    ├─ WaveeIcons.Font  <exe>/assets/fonts/wavee-icons.otf#WaveeIcons     Glyphs.cs:17-18
    └─ WaveeFonts.Icons <exe>/assets/fonts/SegoeFluentIcons.ttf#Segoe Fluent Icons  + IconsPath (startup log)

WAVEE — MOTION
├─ WaveeMotion                    WaveeMotion.cs:30-76    3 scale tiers + 3 duration rungs + 2 stagger rungs
├─ ScaleTier (readonly struct)    WaveeMotion.cs:163-190  Hover/Press/HoverIf/PressIf, reduced-motion-safe
├─ WaveeEntrance                  WaveeMotion.cs:102-127  the list/shelf entrance; cap 8, rise 8 DIP, blur 2
├─ HoverMotionGate (struct)       WaveeMotion.cs:141-159  arm only after the pointer demonstrably moved (0.5 DIP)
├─ PageNavMotion                  PageNavMotion.cs        SlotKey + 5 page-swap recipes
│   ├─ NavTransitionKind          :12      Forward | Back | Neutral (a byte enum)
│   ├─ PageSlot(TabId, Route)     :18      identity ONLY — direction is deliberately NOT in the key
│   └─ SlotKey                    :28-29   tabId + U+001F + route.Name + U+001F + (route.Arg ?? "")
├─ MorphKeys.For                  MorphKeys.cs:9-16       the shared-element key convention (DORMANT — see §5)
├─ DetailRevealRamp               DetailRevealRamp.cs     Chunk 12 / Cap 60 / Done + Revealed(i, reveal) :26
└─ FrameTime                      FrameTime.cs:19-27      NowQpc / NowMs — the ONE app-side motion clock

WAVEE — MATERIAL CHANNEL (shell-owned, page-published)
├─ ShellMaterialState(Owner, Tint?, Wash?)     ShellMaterial.cs:20
├─ ShellMaterial.Slot / Publish               ShellMaterial.cs:37-58 (hand-over, never a clear)
├─ WashLayer(Color, ArtworkKey) / HomeWash(Hero, Weekly, Mix)   ShellMaterial.cs:10-14
├─ ShellWashGeometry.Hero/Weekly/Mix + Resolve ShellWashGeometry.cs:20-55
└─ ShellMaterialLayer : Component              ShellMaterialLayer.cs:21-133  paints Tint (flat) XOR Wash (3 radials)

WAVEE — PREFERENCES / ZOOM
├─ WaveeTheme.ResolvePalette / ApplyThemeMode  WaveeTheme.cs:15-35   mode 0 System · 1 Light · 2 Dark
├─ AppearancePrefs.Epoch / Bump                AppearancePrefs.cs:9-29  the cross-surface appearance edge
│   ├─ TrackArtworkHidden(settings)            :14-18   Epoch is the edge, the store is the truth
│   └─ LikedCover(settings)                    :24-29   clamped through LikedCoverRules.FromSetting, so an
│                                                       out-of-range persisted int reads as Stock, never as nothing
├─ PageAccent(ColorF Ink, ColorF Fill, string Key)          WaveeAccentCtx.cs:12   Key = PROVENANCE, not colour —
│                                                       two sources landing on one colour are still two accents
├─ WaveeAccentCtx.Slot (Context<IReadSignal<PageAccent>?>)   WaveeAccentCtx.cs:30   one provider: RecentsPage
└─ ZoomAutoMode / ZoomAutoPolicy.Suggest / MigrateMode   ZoomAutoPolicy.cs:10, 71-113
    ZoomAutoMode.Auto 0 · Manual 1 · Dense 2 — **Dense is NOT exposed in Settings today**; the ComboBox only ever
    writes Auto (index 0) or Manual (any ladder pick). Dense's 0.75 floor is a built, unreachable arm.
```

### 1.2 The same tree in 0.3 terms

Everything in §1.1 is **static, pure and engine-only** — it reads `Tok.*` and `Motion.ReducedMotion`, never an entity.
That is why it ports as `static partial class` sections rather than as components. Only **four nodes are Components**
(they subscribe to the cover-colour plane or the material signal), and only those four have a props/Key story.

| 0.2.9 node | 0.3 file | form | inputs | how change reaches it |
|---|---|---|---|---|
| `WaveeSize`, `PlayerDock` | `Platform/Design.cs` (CORE) | `static class Design.Size` | none (consts) | n/a — compile-time |
| `WaveeType` (18 aliases) | `Platform/Design.cs` (CORE) | `static class Design.Type`, `TextEl` factories | `string` | value returned per call; a theme flip re-fires through the engine's bound `Ui.*TextBrush` |
| `WaveeAccent`, `WaveeColors` | `Platform/Design.cs` (CORE) | `static class Design.Colors`, **properties not fields** | none | every member is a `get =>` over `Tok.*` so `Tok.Epoch` re-fires it; a `static readonly ColorF` here is a bug |
| `WaveeOnMedia` | `Platform/Design.cs` | `static class Design.OnMedia` | none | properties over `Tok.OnMedia*`; the four literal `ColorF`s (ScrimHover/Pressed/CoverScrim/Stroke/Spotlight*/BackdropDim) stay `static readonly` — they are theme-invariant by contract |
| `StageArm` / `StageInk` | `Platform/Design.cs` | `readonly record struct Design.StageArm(bool Dark)` + `static Design.StageInk` facade | `ThemeKind` (pure entry `For(theme)`) | `StageInk.Live => Arm(Tok.Theme)` re-reads per access |
| `WaveePalette` | `Platform/Design.cs` (CORE) | `static class Design.Palette` | `uint` ARGB / `ColorF` / `ThemeKind` | pure functions; no globals except the two `Tok.Theme`-reading convenience overloads |
| `Surfaces` (recipes) | `Platform/Controls.cs` | `static class Surfaces` | `Image?`, url `string?`, DIP floats, `float scale` | returns `Element`; the tinted fills are `Prop.Of(...)` **binds** so a landed grading repaints the tile without a re-render |
| `CoverShimmer` | `Platform/Controls.cs` | `sealed class CoverShimmer : Component` | ctor args `(url, decodeW, decodeH, w, h, corners)` — **frozen at mount** | **`Key` remount**: `"shim:" + url + ":" + dw + "x" + dh` (`Surfaces.cs:228`). A rebind to a new cover or a new decode bucket MUST change the Key or the tile keeps reading its first decode handle |
| `CoverPageTonePlane` | `Platform/Design.cs` | `sealed class : Component` | `Props(Url, FallbackUrl, Disabled, HeroBand, PageHeight, HeroOnly)` via `Embed.Comp(props, factory)` + `UseProps<Props>()` | **re-pushed props** (live), plus the caller's own `Key` (`CoverPaletteLeaves.cs:30-31`) |
| `CoverArtistBlendWash`, `CoverKeyedVeil` | `Platform/Design.cs` | `sealed class : Component` | `Props(...)` re-pushed + caller `Key` | re-pushed props |
| `CoverShellTintBinder` | `Platform/Design.cs` | `sealed class : Component` | `Props(Url, FallbackUrl, Ready, Disabled, Apply, Owner, Slot)` | re-pushed props; writes through `Signal<ShellMaterialState>`; `UseActivation(onActivated: claim)` for KeepAlive |
| `ShellMaterialLayer` | `Shell/Shell.UI.cs` (cites this chapter) | `sealed class : Component` | ctor `(IReadSignal<ShellMaterialState>, IReadSignal<Size2>)` | **signal subscribe** on material (navigation rate); viewport read through **bound** `Prop.Of` size props so a resize never re-renders it |
| `ShellWashGeometry`, `ShellMaterial` | `Platform/Design.cs` (CORE) | `static class` + `readonly record struct`s | pure | n/a |
| `WaveeMotion`, `ScaleTier`, `WaveeEntrance`, `HoverMotionGate` | `Platform/Design.cs` (CORE) | `static class` / `readonly struct` | `int index`, `Point2` | tiers are **properties** that read `Motion.ReducedMotion` at access; never cache a tier value in a field |
| `PageNavMotion`, `MorphKeys`, `DetailRevealRamp`, `FrameTime` | `Platform/Design.cs` (CORE) | `static class` | pure | n/a |
| `WaveeCta` | `Platform/Controls.cs` | `static class Cta` | `label`, `ColorF accent`, `Action onClick`, `glyph`, `ink`, `minHeight` | returns `BoxEl`; colour arrives as an argument every render |
| `WaveePicker` | `Platform/Controls.cs` | `static class Picker` | `count`, `selected`, `Func<int,bool,Element>`, `Action<int>` | `Strip` hands `RadioButtons.Create` a **fresh `Signal<int>(selected)` per render** (`WaveePicker.cs:279`) — a throwaway re-seeded from the caller's truth, never a mirror kept in step by a write-during-render |
| `SearchHighlight` | `Platform/Controls.cs` | `static class` | pure | n/a |
| `WaveeTheme`, `AppearancePrefs`, `WaveeAccentCtx` | `Platform/Platform.cs` | `static class` + `Signal<int>` + `Context<T>` | `IAppSettings` | `AppearancePrefs.Epoch` is the **update edge**; the settings store is the **truth** — a consumer reads `_ = Epoch.Value;` then re-reads the store (`AppearancePrefs.cs:17-18`) |
| `ZoomAutoPolicy` | `Platform/Platform.cs` (CORE, BCL-only) | `static class` | `float baseW, baseH, ZoomAutoMode` | pure. **Must stay BCL-only** so it source-includes into `Wavee.Tests` |
| `WaveeIcons` / `WaveeFonts` | `Platform/Platform.cs` | `static class` | none | assigned to `Theme.IconFont` before `FluentAppHarness.Run` (`Program.cs:412`) |

**Props freeze at mount — the three traps this layer sets:**
1. `CoverShimmer`'s constructor args freeze. Its Key carries url *and* decode bucket for exactly this reason.
2. `Surfaces.PlaceholderFor(url)` evaluated inline into a record field freezes the colour at the render that mounted
   the slot; a grading landing a moment later never repaints it. Use `Surfaces.WatchedPlaceholder(url)` — a
   `Prop.Of` that reads `CoverColorPlane.Watch(url).Value` so the **paint** path re-evaluates
   (`Surfaces.cs:94-111`). This is why every sidebar/pin thumb (< 80 DIP) and every `ArtworkFill` grid cell used to
   stay grey forever.
3. `StageInk.*` must be read at the point of consumption. A `ColorF` captured into a component ctor does not follow a
   live theme flip (`StageArm.cs:14-18`).

---

## 2. Wireframes

Scale is **8 DIP per character** unless the title says otherwise.

### W1 — Type ramp specimen @ any width (8 DIP/char)

```
 alias                    face                       size/line/weight   tracking   colour
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ ArtistDisplay       ██████████████████████████     84/96/700  Display  -28/1000  TextPrimary  │  ← MinSize 68
│                                                                                              │
│ ArtistTitle         ████████████████               48/60/700  Display  -20/1000               │  ← MinSize 40
│ SurfaceDisplay      ████████████                   40/52/400  Display  -12/1000               │
│ ArtistCompactTitle  █████████                      32/40/700  Display  -12/1000               │  ← MinSize 28
│ DetailHero          ████████                       28/36/600  Display  -20/1000               │  ← shell swaps 40/52 when winH>=900
│ PageHero            ████████                       28/36/600  UI       0                      │
│ PickQuote/FoldTitle ████████                       28/36/400  Display  -12/1000               │
│ StatHero  "1,203,455  plays"                       28/36/350  Display  -6/1000   + caption run│
│ RailHeader / ModuleHeader / NowPlayingTitle         20/28/600  UI / Display(-6)               │
│ NpvLyric            █████                          20/28/350  Display  -6/1000                │
│ PivotLabel          ████                           19/25/350  Display  -6/1000                │  ← OFF-ramp by design
│ (Ui.BodyLarge)      ████                           18/24/400  UI                              │
│ TrackTitle / CardTitle  ███                        14/20/600  UI       0         TextPrimary  │
│ (Ui.Body)               ███                        14/20/400  UI                              │
│ WaveeCta.TextAction     ███                        14/20/600  UI       0         see §4       │
│ Eyebrow                 ██                         12/16/600  UI      +30/1000   CALL SITE ¹  │
│ TrackMeta               ██                         12/16/400  UI       0         TextSecondary│
└──────────────────────────────────────────────────────────────────────────────────────────────┘
 ¹ the DEFAULT, when the call site sets nothing, is Tok.TextSecondary — Ui.Caption is the one ramp rung bound to
   the secondary brush (Dsl/Typography.cs:39). "Call site owns colour" means it may OVERRIDE, not that it must.
 Every Artist* alias also carries a MinSize (68 / 40 / 28): the engine shrinks the glyph run toward it before it
   wraps or ellipsises, so a long artist name steps DOWN inside one rung rather than breaking to a second line.
 Ellipsis policy, where the alias owns it (everything else is the call site's): ModuleHeader(title, meta),
   RailHeader(title, meta) and StatHero(value, unit) all ship Wrap=NoWrap + Trim=CharacterEllipsis + MaxLines=1 +
   MinWidth=0 + Shrink=1 (WaveeType.cs:93-97, 119-123, 276-280) — a baseline-paired span must never wrap, because
   the small run would land alone on line 2 with no heading to sit on.
Baseline-paired spans (ONE paragraph, because FlexAlign has no Baseline member):
  ModuleHeader(title, meta) │ "Radio␣␣20 stations"   20/28/600 Display + 12/16/400 Tok.TextTertiary
  RailHeader(title, meta)   │ same, UI face
  StatHero(value, unit)     │ "2B␣␣monthly"          28/36/350 Display + 12/16/400 Tok.TextSecondary
  The gap IS two literal spaces — a run break cannot carry margin (WaveeType.cs:81-83, 261-263).
```

### W2 — Spacing / radius / size scales @ any width (8 DIP/char)

```
Spacing (engine, 4-grid; XXS the sole 2-step)
  XXS  XS   S     M       L       XL       XXL        XXXL          PageWide
   ▌   ▐▌   ▐──▌  ▐────▌  ▐─────▌ ▐───────▌▐─────────▌▐────────────▌▐──────────────▌
   2    4    8     12      16      20       24         32            36

Radii          0        4          8           16          999→half-box
             ┌──┐    ╭──╮      ╭────╮       ╭──────╮      ╭────────╮
             │  │    │  │      │    │       │      │      (        )
             └──┘    ╰──╯      ╰────╯       ╰──────╯      ╰────────╯
             None    Control   Card/Overlay  Pill          Full

WaveeSize ladder (DIP)
  NavCompactW 56 │ ControlH 32 │ NavItemH 44 │ TrackRowH 56 │ PlayerBarH/PlayerDock.BarH 72
  NavPaneW 240   │ RailPlaylist 240 │ RailAlbum 280 │ RailCard 180 │ PageMaxW 1600
  Art: ArtThumb 40 · ArtPlayerBar 48 · ArtNowPlaying 64
  Thumb ladder (the ONLY in-content cover sizes):  32 ─ 40 ─ 48 ─ 56 ─ 64      (8-step, on the 4-grid)
  Section rhythm:  SectionGap 32  ·  SectionGapWide 40
  CTA:  IconButtonSize 32 (= ControlH)  ·  PillHeight 36
```

### W3 — The accent budget, three roles @ 1200 (16 DIP/char)

```
┌────────────────────────────────────────────────────────────────────────┐ 1200
│  ROLE 1 — AccentAction        at most ONE per screenful                │
│    ╭──────────────╮  ╭──────────────╮                                  │
│    │ ▶  Play      │  │   Shuffle    │  ← the 2nd is Button.Standard    │
│    ╰──────────────╯  ╰──────────────╯     (NOT a second accent plate)  │
│     solid accent fill     stock ramp                                   │
│     h36 r=Full pad 18/6/18/7                                           │
│                                                                        │
│  ROLE 2 — AccentSelection     reserved GEOMETRY: a short bar/pill      │
│    ▌ Home            ← 3-DIP sidebar pill + WaveeColors.SelectedRest    │
│    ▁▁▁▁▁▁ (SelectorBar pill)                                           │
│                                                                        │
│  ROLE 3 — AccentDecor         accent as INK or WASH, never a plate      │
│    Editorial                 ← Eyebrow, Color = accent (call site)     │
│    Because you played                                                  │
│    ▬▬▬                       ← Surfaces.AccentRule, 20 × 2, +2 gap     │
│                                                                        │
│  FORBIDDEN                                                             │
│    ▌ Because you played      ← a 3×22 r1.5 capsule beside a header:    │
│                                 that IS selection geometry (rule 1)    │
│    ┌ ─ ─ ─ ─ ─ ┐  ›          ← accent borders / accent chevrons:       │
│                                 accent is never STRUCTURE (rule 2)     │
└────────────────────────────────────────────────────────────────────────┘
```

### W4 — CTA geometry table, all states @ 8 DIP/char

```
ROW 1 — the labeled media pill (WaveeCta.Play / .Accent / .Pill)
        h36, Radii.Full → r18, Padding 18/6/18/7, Bold(700) 14px label, glyph Icons.Play
  rest        hover (scale 1.04)   pressed (0.96)      disabled            focused
 ╭─────────────╮  ╭──────────────╮   ╭────────────╮    ╭─────────────╮   ╭┄┄┄┄┄┄┄┄┄┄┄┄┄╮
 │ ▶  Play     │  │  ▶  Play     │   │▶ Play      │    │ ▶  Play     │   ┆╭───────────╮┆
 ╰─────────────╯  ╰──────────────╯   ╰────────────╯    ╰─────────────╯   ┆│ ▶  Play   │┆
  fill=accent      fill=accent@0.90   fill=accent@0.80  Tok.AccentDisabled┆╰───────────╯┆
  ink=PickContrast ink unchanged      ink@0x80|0xB3     ink=TextOnAccent-  ╰┄┄┄┄┄┄┄┄┄┄┄┄┄╯
  border=AccentControlElevationBorder border=transparent      Disabled     FocusVisualMargin -3
  (3px band, StrokeControlOnAccentSecondary@0.33 → …OnAccentDefault@1, AnchorEnd)
  brush ramp on every state flip: 83 ms

ROW 2 — the icon-only arm of the pill (WaveeCta.Icon), size 36, Radii.Full → circle
        ONLY inside a CTA cluster beside labeled 36 capsules. IconHoverScale/IconPressScale = 1
        (the CAPSULE scales, not the glyph — running both would compound to 1.12).
    ╭────╮      ╭─────╮        ╭───╮
    │ ⤨  │      │  ⤨  │        │ ⤨ │
    ╰────╯      ╰─────╯        ╰───╯
    36×36        1.04           0.96        + the button ramp's hairline (BorderBrush/Width from Button.DefaultStyle)

ROW 3 — the standard icon button, 32 × 32, Radii.Control (4). Stock IconButton.
    ┌────┐  toolbars, panels, rows, flyouts, dialogs. If unsure, it is this one.
    │ ⋯  │
    └────┘

ROW 4 — a circle, ANY diameter: a FAB, and ONLY over artwork or video.
        A circle on a flat panel is off-table.

THE TEXT ACTION — fenced to the sticky ContextBand (WaveeCta.TextAction)
  h = ContextBandLayout.Height 56 − 2×Spacing.M 12 = 32 ; PadX = ContextBandLayout.ActionPadX 10
  Corners = Radii.ControlAll (invisible at rest — nothing is filled); Gap Spacing.S 8
  ┌ ─ ─ ─ ─ ─ ─ ─ ┐
    ♡  Save         ink is the WHOLE state model:
  └ ─ ─ ─ ─ ─ ─ ─ ┘   neutral: TextSecondary → TextPrimary → TextSecondary
                       primary / toggledOn: AccentTextPrimary → AccentTextSecondary → AccentTextTertiary
                       NO fill, NO border, NO HoverScale (a growing word shoves its neighbours in a 56-DIP bar)
```

### W5 — WaveePicker: card shells and miniatures @ 8 DIP/char

```
Tile 116×84, inset 8 (7 when selected), gap 4          CoverMini 92×auto, inset 8, gap 5
┌──────────────┐ unselected            ┏━━━━━━━━━━━━━━┓ selected      ┌───────────┐
│ ▓▓ ▬▬▬▬▬ ─ ─ │ fill FillCardDefault  ┃ ▓▓ ▬▬▬▬▬ ─ ─ ┃ fill Accent-  │  ▓▓▓▓▓▓▓  │ 76-DIP
│ ▓▓ ▬▬▬▬▬ ─ ─ │ border 1 Stroke-      ┃ ▓▓ ▬▬▬▬▬ ─ ─ ┃ Subtle        │  ▓▓▓▓▓▓▓  │ square
│ ▓▓ ▬▬▬▬▬ ─ ─ │        ControlDefault ┃ ▓▓ ▬▬▬▬▬ ─ ─ ┃ border 2      └───────────┘
└──────────────┘ r8                    ┗━━━━━━━━━━━━━━┛ AccentDefault    Stock
    Default                                  Cozy                     (label BELOW the card)
  Label 12/16/400 TextSecondary          Label 12/16/600 TextPrimary
  hover: FillCardSecondary, scale 1.02   hover: WaveeColors.SelectedHover, scale 1.02
  press: FillSubtleSecondary, 0.98       press: Tok.AccentSubtle, 0.98
  THE BORDER GROWS INWARD (padding 8→7) so the wireframe never shifts by a pixel.
  Card also carries Shrink 0 and ClipToBounds TRUE (WaveePicker.cs:63-82): a miniature that outgrows its card is
  CUT, never painted over its neighbour — the failure that produced the overlapping Sidebar header.
  Titled(card, label, on) stacks the two: Direction 1, Gap Spacing.S 8, AlignItems Center (:225-231).
  No disabled arm and no focus arm of its own — the RadioButtons item root owns focus/role/click (§6.7).

The four miniatures (all tinted from Ink.For(on) — WaveePicker.cs:32-34)
      selected    Block = Tok.AccentDefault        Faint = AccentDefault @ 0.45
      unselected  Block = AccentDefault @ 0.58     Faint = AccentDefault @ 0.22
      The SELECTED card tints its WHOLE wireframe, so the choice reads from across the page, not just at the border.
  DensityRows(density)       rowHeight = TrackRow.RowHeightFor(d) × 0.25 ; art = ArtSizeFor(d) × 0.25
      Compact  10.0 h / 8.0 art      Default 12.0 / 8.0     Cozy 14.0 / 10.0    Comfortable 16.0 / 12.0
      row: Direction 0, Gap XS 4, PadX XS 4 → [art r4 Block] [bar grow1 h2 Block] [24×2 Faint] [16×2 Faint]
      column: Gap XXS 2, Justify Center, three rows
      IT ALWAYS DRAWS THE **MODERN** LADDER. TrackRow.RowHeightFor(d)/ArtSizeFor(d) are bare forwards to the
      classic:false arm (TrackRow.cs:118,124), so the density preview shows 40/48/56/64 → 10/12/14/16 even when the
      user's Track list style is Classic, whose real ladder is 36/40/44/48 with art 32/32/32/40
      (DetailTrackTableRules.cs:45-47, 54-56). Known, and pinned as the Modern formula by
      TrackRowStyleRulesTests.Preview_MirrorsTrackRow:105-120.

  ModernRow(h 20, art 16)          ClassicRow(h 20)
   ┌────────────────────┐           ┌────────────────────┐
   │ ▓▓ ▬▬▬▬▬▬▬▬▬▬▬▬▬▬  │ r4 plate  │ ▬▬▬▬▬▬ ─────  ───── │ 3 aligned lanes
   │ ▓▓ ▬▬▬▬▬▬▬▬        │ ink.Faint │ ──────────────────  │ 1-DIP hairline
   └────────────────────┘           └────────────────────┘

  PageLayoutCards — Automatic: [art 20 / 30×6 / 22×4 / 20×8 pill]  ‖ 4 × full-width 4-DIP row bars
                    Hero:      [24-DIP full-width block] + [48×6 / 28×4 / two 24×8 pills] over 3 row bars

The strip: RadioButtons.Create, ONE tab stop, roving Up/Down/Left/Right, selection follows focus,
           Ctrl+arrow moves without applying, Space selects. Grid Wrap = true, Gap Spacing.M 12.
           COLUMN-MAJOR: maxColumns defaults to count ⇒ one item per column ⇒ a single horizontal strip,
           CLAMPED Math.Max(1, maxColumns ?? count) so 0 / a negative can never reach the grid (:284).
           A caller may pass a smaller maxColumns for a picker whose cards do not fit on a line — the Liked
           cover flyout's 3-wide grid is the one such caller today.
           Glyph-less radio (s_bare, :238-245): ShowGlyph = false, MinWidth = MinHeight = ContentGap = 0,
           FocusVisualMargin All(2) — WinUI's −7,−3 would draw the ring through the card's own border (W18).
           `parts` OVERRIDE (:271-286): a picker that reads as a vertical LIST rather than a wrapped strip —
           the setup wizard's WideRow sidebar-design chooser — passes its own TemplateParts instead of s_strip.

WIDTH: the strip's cards are FIXED-width (Shrink 0 on both Card and PartColumn), so a narrowing window drops
       COLUMNS; the cards never shrink and never overflow. That is the whole reason PartGrid takes Wrap = true —
       WinUI's ColumnMajorUniformToLargestGridLayout has no wrap state of its own (:247-254).
```

### W6 — The shell material stack, DARK, a detail page @ 1600 × 900 (16 DIP/char)

```
 window  ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
 y=0     │ ░░░ live DWM Mica (base layer — the shell root paints NOTHING) ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │
         │┌──────────────────────────────────────────────────────────────────────────────────────────────────┐│
         ││ MATERIAL  ShellMaterialLayer → Tint: ONE flat full-bleed rect, Fill = published tint or           ││
         ││           NeutralGround (ShellGround @ α 0.03), BrushTransitionMs 250                             ││
         ││   detail tint = WaveePalette.TintedDark(scheme) with A = 0.14        (CoverPaletteLeaves.cs:245)  ││
         │└──────────────────────────────────────────────────────────────────────────────────────────────────┘│
 y=48    │ ═══ merged title row (UNPAINTED: material + Mica show through) ══════════════════════════════ ─ □ × │
         │┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈╭─────────────────────────────────────────────────────────────────────────────────│
         │  sidebar 240      │ ▏CONTENT REGION                                                                 │
         │  (UNPAINTED)      │ ▏ Fill = WaveeColors.FileArea  #3A3A3A4C                                        │
         │                   │ ▏ + 1px Tok.StrokeCardDefault #00000019 on LEFT + TOP edges ONLY                │
         │                   │ ▏ + ContentPaneCorners (8, 0, 0, 0) — ONE rounded corner, facing the pane       │
         │                   │ ▏ + NO shadow                                                                   │
         │                   │ ▏                                                                               │
         │                   │ ▏ PAGE TONE PLANE (a page-root SIBLING of the scrolling page, not a child)      │
         │                   │ ▏   Fill = PageTone(scheme, Dark) with A = 0.20   ⇒ ~80 % of Mica survives      │
         │                   │ ▏   ClipToBounds + ContentPaneCorners, HitTestVisible = false                   │
         │                   │ ▏   BrushTransitionMs 250 ⇒ a grading ARRIVAL cross-fades, never snaps          │
         │                   │ ▏                                                                               │
 y=828   │┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┴─────────────────────────────────────────────────────────────────────────────────│
         │ ▁▁▁ player dock 72 (UNPAINTED — whatever the material layer paints under it IS the dock) ▁▁▁▁▁▁▁▁▁▁ │
 y=900   └────────────────────────────────────────────────────────────────────────────────────────────────────┘
  Opaque stand-ins (floating panes, the login view, the flatten base for every contrast gate):
     ShellGround  dark #202020  ·  ContentSurface dark #282828 (one step, lift 0.0364 toward white)
     ContentLayer dark = white @ α 9/255 — the LAYER equivalent: Over(ContentLayer, ShellGround) == ContentSurface
```

### W7 — Same stack, LIGHT @ 1600 × 900 (16 DIP/char)

```
         ┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
         │ ▒▒▒ live DWM Mica (light) ▒▒▒                                                                      │
         │ MATERIAL tint = Lift(ToColor(scheme.TextBase)) with A = 0.05     ← a WHISPER (CoverPaletteLeaves:244)│
         │ ═══ merged title row (unpainted) ═════════════════════════════════════════════════ ─ □ × │          │
         │  sidebar 240      │ ▏ CONTENT  FileArea #FFFFFF80 + stroke #0000000F on L+T, corner (8,0,0,0)       │
         │                   │ ▏ PAGE TONE  PageTone(scheme, Light) with A = 0.30                              │
         │                   │ ▏   L forced 0.94, S capped 0.16 → e.g. a green sleeve lands near #EFF4EF        │
         │                   │ ▏   HONEST CONSEQUENCE (documented, not a bug): in light the record's hue is    │
         │                   │ ▏   close to imperceptible and a busy wallpaper shows through. Light identity   │
         │                   │ ▏   is carried by the CHROME accent (Play capsule, accent rule, row chrome).    │
         │                   │ ▏                                     (CoverPaletteLeaves.cs:131-138)           │
         │ ▁▁▁ player dock 72 (unpainted) ▁▁▁                                                                  │
         └────────────────────────────────────────────────────────────────────────────────────────────────────┘
  Opaque stand-ins: ShellGround light #EDEDED (= MicaRef.LightDefault; #F3F3F3 dropped by 6/243 ≈ 2.469 %)
                    ContentSurface light #F9F9F9 (Tok.FillSolidTertiary)
                    ContentLayer light = white @ α 170/255 (the 2/3 lift that composites to #F9F9F9 over #EDEDED)
```

### W8 — Home's three-wash placement map @ 1600 × 900 (16 DIP/char)

```
         x=0                                                                                             x=1600
 y=0     ┌──────────────────────────────────────────┬───────────────────────────────────────────────────┐
         │ HERO   box 0.5188w × 0.5704h = 830 × 513 │                          WEEKLY  box 0.4512 ×     │
         │ anchored LEFT+TOP (AnchorRight=false,    │                          0.5992 = 722 × 539       │
         │          AnchorBottom=false)             │                          anchored RIGHT+TOP       │
         │ ellipse centre (0.1157, 0.000) node-rel  │                          centre (0.8227, 0.1669)  │
         │ radius (1.4264, 1.6129) node-rel         │                          radius (1.2855, 1.3018)  │
         │ stops: 0 → α HeroAlpha; 0.62 → α 0       │                          stops 0 → ShelfAlpha;    │
 y=513   ├──────────────────────────────────────────┤                          0.64 → 0                 │
         │                                          └───────────────────────────────────────────────────┤
 y=539   │                                                                                              │
         │                                                                                              │
 y=412   │  ┌────────────────────────────────────────────────────────────────────────────────────────┐  │
         │  │ MIX  box 1.0w × 0.462h = 1600 × 416, anchored BOTTOM of the WASH HOST                    │ │
         │  │ centre (0.58, 1.00) node-rel, radius (0.90, 1.5152), stops 0 → ShelfAlpha; 0.66 → 0      │ │
 y=828   │  └────────────────────────── CLIPPED HERE (WashHost Margin.Bottom = PlayerDock.Reserve 72) ┘ │
         │ ▁▁▁ player dock 72: the Mix wash's PEAK used to land across this band — that is what read   │
 y=900   └▁▁▁ as "the dock has a pastel gradient". It never had one; it had the shell's. ▁▁▁▁▁▁▁▁▁▁▁▁▁▁┘
  Alphas at the wash origin:   HeroAlphaLight 0.055  ShelfAlphaLight 0.05
                               HeroAlphaDark  0.10   ShelfAlphaDark  0.085   (dark ≈ 2× light)
  The flat TINT stays full-bleed (uniform, no peak to land anywhere) — only the three RADIALS are clipped.
  Each wash layer is keyed "shell.wash.<slot>:<artworkKey>" so a new grading EXITS the old layer and
  ENTERS the new one over the same pixels — gradients carry no brush-fade channel, so a wash cross-fades
  by MOUNT.  Enter/Exit = EnterExit(Opacity: 0, Active: true), or NULL under reduced motion.
```

### W9 — The on-media ladder, over a cover @ 8 DIP/char

```
 ┌────────────────────────────────┐  SCRIM PLATES (a small dark surface on artwork)
 │  ▓▓▓▓▓ album artwork ▓▓▓▓▓ ⓘ⋯ │    rest    Tok.MediaScrim      black α 140/255 (0.55)
 │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │    hover   #000000BE          α 190
 │  ▓▓▓▓▓▓  ╭───╮  ▓▓▓▓▓▓▓▓▓▓▓▓▓  │    press   #000000DC          α 220
 │  ▓▓▓▓▓▓  │ ▶ │  ▓▓▓▓▓▓▓▓▓▓▓▓▓  │    every plate ringed by Stroke #FFFFFF3A (white α 58)
 │  ▓▓▓▓▓▓  ╰───╯  ▓▓▓▓▓▓▓▓▓▓▓▓▓  │
 │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓  │  COVER VEIL (a whole square dimmed so a centred FAB reads)
 │  Kind chip   Artist · 2024     │    CoverScrim #0000006E  α 110  — LIGHTER than the small plate, on purpose
 └────────────────────────────────┘
   INK (theme-INVARIANT: white in light mode too)      GLASS (no resting plate: the stage's ramp)
     Ink           Tok.OnMediaPrimary   white 1.00       rest      transparent
     InkSecondary  Tok.OnMediaSecondary white 0.80       hover     OnMediaPrimary @ 0.10
     InkTertiary   Tok.OnMediaTertiary  white 0.60       pressed   OnMediaPrimary @ 0.16
   INK PLATE (the one control that must be findable — the stage's way out)
     rest 0.14 · hover 0.22 · pressed 0.28   — deliberately ABOVE GlassHover: a rest state needs its own edge
   LIGHT ON-MEDIA BUTTON (the editorial card's persistent FAB)
     rest OnMediaPrimary · hover Darken(·, 0.08) ≈ #EBEBEB · pressed Darken(·, 0.157) ≈ #D7D7D7
     glyph Tok.MediaStage #0A0A0A
   BACKDROP DIM over a baked-blur derivative: rgb(8,8,10) @ 0.45
   HOVER SPOTLIGHT on an editorial cover: inner #FFFFFF2E (46) → mid #FFFFFF14 (20) → transparent
```

### W10 — The row-state ladders (list vs nav) @ 8 DIP/char

```
LIST / BROWSE — neutral subtle-fill ladder (a track row, a library hit, a suggestion)
  ┌──────────────────────────────────────────────┐  even  (no plate)
  │██████████████████████████████████████████████│  odd   RowZebra   light #00000008 (3.1%)  dark FillSubtleTertiary #FFFFFF0A
  │██████████████████████████████████████████████│  hover RowHover   light #0000000D (5.1%)  dark #FFFFFF0F
  │██████████████████████████████████████████████│  press RowPressed light #00000012 (7.1%)  dark #FFFFFF0A
  └──────────────────────────────────────────────┘
  On a STRIPED row the state is composed, never stacked: RowHoverZebra = ColorContrast.Over(RowHover, RowZebra)
  (a row paints ONE Fill). THE INVARIANT: zebra < hover, and pressed > rest — a stripe must be quieter than the
  state that lands on it, or the row has no hover.
  PRESS DIRECTION INVERTS BY THEME, and both arms are correct. LIGHT: pressed 0x12 (7.1%) is ABOVE hover 0x0D
  (5.1%) — black ink deepening. DARK: pressed 0x0A is BELOW hover 0x0F — the white subtle-fill ladder's own
  rest→hover(up)→pressed(down-but-above-rest) shape. Do NOT "fix" one to match the other; they are WinUI's two
  arms (PaletteBuilder.cs:117-119 light, :422-427 dark).
  LIGHT zebra is the SHELL's own black-alpha rung (ShellPalette.RowZebra 0x08); DARK zebra is overridden app-side
  to Tok.FillSubtleTertiary 0x0A because the shell's dark zebra is LITERALLY the hover fill (both 0x0F)
  (WaveeTokens.cs:216-230). That override is why the dark RowHoverZebra Wavee computes is not the engine's own
  ShellPalette.RowHoverZebra 0x14 — the app recomposes Over(hover, the overridden zebra).
  Zebra is for lists LONG ENOUGH TO LOSE YOUR PLACE IN only. A 6-row queue or a friends rail gets plain rows.

NAV — the selection ladder, and it only ever goes UP (WaveeTokens.cs:244-274)
  ▌ Home         rest    Tok.AccentSubtle              light #005FB824 (14 %)  dark #60CDFF29 (16 %)
  ▌ Home         hover   Over(FillSubtleSecondary, AccentSubtle)     strictly stronger than rest
  ▌ Home         press   Over(FillSubtleTertiary,  AccentSubtle)     above rest, below hover
  └ the reserved 3-DIP selection pill (geometry) — the accent plate NEVER appears without it.
  The bug this replaced was an INVERSION: hovering the selected row swapped DOWN to a quieter plate,
  so pointing at the row you are on looked like a deselection.
```

### W11 — Page-transition timeline (ContentHost) @ 8 ms/char

```
 t=0        90ms                                    250ms     340ms
 ├──────────┼───────────────────────────────────────┼─────────┤
 OUT  ████████▓▓▒▒░░                                           exit: Opacity 1→0, Dx 0 (in place)
      └ Tween(120 ms, Easing.EaseOut) ┘                        ~94 % gone at 90 ms
      Exit.Active = TRUE — the reconciler ZStacks the outgoing page, hit-test invisible, and parks it
      once its tracks settle. A stripped Exit detaches it in the same frame and the card FLASHES EMPTY.
 IN             ░░▒▒▓▓████████████████████████████████         enter: Opacity 0→1 and Dx ±8 → 0
                └ delay 90 ms ┘└ Tween(250 ms, Easing.SmoothOut cubic-bezier(.22,1,.36,1)) ┘
   Forward  Enter Dx = +Expressive.DistBase = +8      Back  Enter Dx = −8
   Neutral  MotionRecipes.PageFade — opacity only, Tween(250, SmoothOut), no delay

 VIDEO-SAFE PAIR (a module watch page on EITHER side — a composited video is a DestOut hole an
 ancestor opacity washes out and an opacity GROUP erases entirely):
 OUT  ◄────────────────────────────────────► Dx 0 → ∓8, Tween(250, SmoothOut), no delay
 IN   ◄────────────────────────────────────► Dx ±8 → 0, Tween(250, SmoothOut), no delay
   Position ONLY. The honest degradation: two full-bleed pages share the card at FULL opacity for the
   travel. Neutral has no video-safe form at all — RecipeForVideoSafe returns NULL, an honest CUT.
```

### W12 — Entrance stagger and the detail reveal ramp @ 8 DIP/char

```
WaveeEntrance — the ONE list/shelf entrance. Tween(Expressive.Slow 400 ms, Easing.SmoothOut),
                Channels = Opacity ONLY (never a FLIP), Enter = Dy +8, Opacity 0, Blur σ 2.
 item 0  ├────────────────────────────────────┤     delay   0 ms
 item 1     ├────────────────────────────────────┤          40
 item 2        ├────────────────────────────────────┤       80
 …
 item 8                ├────────────────────────────────────┤   320  ← THE CAP
 item 9+               ├────────────────────────────────────┤   320  (everything lands together)
 reduced motion: every delay 0; the engine's ReducedSnap parks the rise and the blur at their end
 state and still cross-fades opacity (a fade aids orientation; it is not motion).
 MAY be used only where items mount ONCE, or on the engine's BOUND recycler path. On a RenderItem
 virtual list it reads as flicker — those take Skel.Region(reveal: SkelReveal.StaggerRows) instead.
 The masthead's own rung is SEPARATE: MastheadStaggerMs 45, assigned to Element.Stagger on a
 two-child container (uncapped index×ms is safe for exactly two authored lines).

DetailRevealRamp — the cold shimmer→content swap, per frame
 frame 0  ██░░░░░░░░░░░░░░░░  reveal 12 real rows, the rest stay shimmer
 frame 1  ████░░░░░░░░░░░░░░  24
 frame 2  ██████░░░░░░░░░░░░  36
 frame 3  ████████░░░░░░░░░░  48
 frame 4  ██████████████████  Next() ≥ min(visible, Cap 60) ⇒ Done (int.MaxValue): every row real, forever
 Measured: the un-ramped swap mounted the whole visible band in one ~80 ms UI frame (694 spans
 re-recorded, record = 72.4 ms, gen2 GC). Chunk 12 ≈ one 20 ms record slice of that.
```

### W13 — Zoom: the auto policy and the ladder

```
 Suggest(baseW, baseH, mode) = SnapPlateauDown(Clamp(min(baseW/1600, baseH/900), lo, 2.0))
   lo = 1.0 (Auto) | 0.75 (Dense) ; Plateaus = [0.75, 1, 1.25, 1.5, 1.75, 2] ; snap DOWN, never nearest.
   baseDip = clientPx / osDpiScale ALONE (zoom 1) — recovered as viewportDip × Viewport.Zoom.
   Feeding the LIVE viewport back in closes a control loop that converges on the design box regardless
   of the display, which is exactly wrong.

 │ window (base DIP)      │ W ratio │ H ratio │ min  │ → zoom │ resulting DIP viewport │
 │ 1664 × 1109 (laptop)   │  1.04   │  1.23   │ 1.04 │  100 % │ 1664 × 1109            │
 │ 3440 × 1392 (34" UW)   │  2.15   │  1.55   │ 1.55 │  150 % │ 2293 ×  928            │
 │ 1920 × 1080            │  1.20   │  1.20   │ 1.20 │  100 % │ 1920 × 1080            │
 │ 2560 × 1440            │  1.60   │  1.60   │ 1.60 │  150 % │ 1707 ×  960            │
 │ 3840 × 2160 @150 % DPI │  1.60   │  1.60   │ 1.60 │  150 % │ 1707 ×  960  ← same box, same answer │
 │ 3840 × 2160 @100 % DPI │  2.40   │  2.40   │ 2.40 │  200 % │ 1920 × 1080  (ceiling binds)         │
 │ 3440 ×  700 (sliver)   │  2.15   │  0.78   │ 0.78 │  100 % │ 3440 ×  700  (height guard)          │
 BOTH AXES BIND: width alone would pick 2.15 on the ultrawide, whose 1720 × 696 viewport DEMOTES the
 nav-pane wide tier (arms at 1800) and the detail hero's TitleLarge rung (arms at 900 tall).

 Settings ▸ Theme ▸ Zoom — a ComboBox (13 items would be 13 SelectorBar segments), width 160
 ┌──────────────────────────────┐
 │ Auto (150 %)              ▾  │  ← index 0, label Strings.Settings.Appearance.ZoomAuto("Auto ({percent}%)")
 │ 50 % 67 % 75 % 80 % 90 %     │     every other index = 1 + ZoomLadder.Steps index
 │ 100 % 110 % 125 % 150 % …    │     picking one sets mode = Manual (a deliberate act overrides the policy)
 └──────────────────────────────┘
```

### W14 — Cover placeholder / shimmer states @ 8 DIP/char

```
 edge ≥ 80 DIP                                  edge < 80 DIP (rows, sidebar, chips)
 ┌──────────────┐ loading, grading MISS         ┌────┐ ALWAYS a cheap static tile:
 │░░░░░░░░░░░░░░│ neutral #2A2A2A / #F2F2F2     │░░░░│ no component, no image-epoch subscription,
 │░░ breathing ░│ opacity 1.0↔0.5 over 1000 ms  └────┘ no breathe. Fill = WatchedPlaceholder(url)
 │░░░░░░░░░░░░░░│ keyframes [(0,1),(0.5,0.5),(1,1)]    — an OPAQUE tile (A forced to 1): a small
 └──────────────┘ loop, Cadence from the host         thumb sits over an UNPAINTED chrome band, so a
 ┌──────────────┐ loading, grading HIT                see-through placeholder lets the desktop read
 │▒▒▒▒▒▒▒▒▒▒▒▒▒▒│ Lerp(neutral, coverColor, 0.55)     through and the cover becomes a washed smear.
 │▒▒ breathing ▒│ — the difference between a list of
 └──────────────┘   grey squares and one that paints its covers at once
 ┌──────────────┐ SETTLED (Ready, or Failed non-Canceled)
 │▓▓▓▓▓▓▓▓▓▓▓▓▓▓│ the looping track is replaced IN PLACE by a finite flat track (opacity → 1, loop:false);
 │▓▓ real art ▓▓│ the tile latches `settled` and STOPS calling UseImage, so a loaded cover never
 └──────────────┘ re-renders on an unrelated image's status change. The loop count drops and the frame
                  loop can quiesce (the engine's "no forever-loop" rule).
 WEAK GPU (GpuProfile.IsWeak): the placeholder is held FLAT even while loading — a per-frame opacity loop
 keeps the render loop hot so every decoded cover uploads at max cadence (the Adreno DEVICE_HUNG amplifier).
 MOSAIC (a cover-less playlist): 2×2 of the first four tiles, each cell decoded at width/2, url-keyed so a
 changed tile re-decodes and the rest stay. 1–3 tiles ⇒ show the first as a single cover (Surfaces.cs:241-244).
 NO IMAGE AT ALL (url null / empty): Artwork renders an empty BoxEl over the STATIC neutral tile — the slot is a
 solid placeholder, never a hole, and never a shimmer (a url-less slot takes the cheap arm regardless of size,
 Surfaces.cs:211).
 ArtworkFill NEVER MOSAICS: a fluid grid cell has no known width, so any MosaicTiles collapse to tiles[0] and the
 cell shows the FIRST cover as a single square (Surfaces.cs:296). Default decodePx 256, aspect 1, Cover fit.
 MORPH PARTICIPANT (morphKey non-null): NO shimmer sibling is mounted at all — the tagged ImageEl owns its own
 placeholder, resolved through the FROZEN PlaceholderFor(url), not the watched bind (Surfaces.cs:272, 285). A
 grading that lands after mount therefore does NOT repaint a morph slot. Dormant today (§5), but it is the one
 art path with no live tint.
 DECODE BRANCHES (Surfaces.cs:258-280): scale == 1 and decodePx == 0 → decode at the laid-out DIP size, slot
 aspect preserved. decodePx > 0 → a SQUARE decode at that literal, cover-fit (the Hero hand-off path: the Home
 card and the detail cover both pass 256 so they resolve to one cached texture, no fresh decode). scale ≠ 1 →
 ImageDecodeScale.For(...) buckets/clamps it; an unscaled decodePx caller keeps its EXACT literal, never rounded.
 `saturation` (default 1) and `preferLargest` (default false) ride through to the ImageEl untouched.
```

### W15 — Reduced motion, everywhere at once

```
 WaveeMotion.ScaleSubtle/Standard/Emphatic  .Hover / .Press  →  exactly 1f
     ⇒ SceneRecorder's |isc − 1| > 0.0008 test FAILS and the transform is skipped entirely.
 WaveeEntrance.DelayMs(i)                   →  0   (the recipe SHAPE is unchanged — see the trap in §9)
 ShellMaterialLayer.WashFade                →  null (no Enter/Exit on a wash layer)
 Element.Stagger call sites                 →  Motion.ReducedMotion ? 0f : MastheadStaggerMs  (a VALUE)
 Engine-side: SeedMotion / KeyframesMotion consult ReducedSnap and park at the end value; the
 declarative While*/Transition surface honours its MotionTokenDef.Reduced policy. The ONE channel the
 engine does NOT suppress is hover/press interaction scale (AnimScheduler.SeedEased carries no policy),
 which is exactly why the suppression is a property of the app's authored VALUE.
 NOT gated (deliberately): the CoverShimmer breathe — it is a loading indicator, not a flourish, and it stops on
 settle anyway; and every brush/ink cross-fade, because a fade is not motion.
```

### W16 — SearchHighlight, both arms @ 8 DIP/char

```
 The chapter listed ONE arm. There are two, and the wrapping one is what the Charts grid uses.

 maxLines = 1 (the default — a library row)
 ┌──────────────────────────────────────────────────────────────┐
 │ Radio  ╔══════╗  head                    ▏← trailing SPACER  │  run row: Direction 0, AlignItems Center
 │        ║ head ║                          ▏  (Grow 1) pushes  │  Grow 1, Basis 0, ClipToBounds TRUE,
 │        ╚══════╝                          ▏  the runs LEFT    │  Wrap = false, MaxHeight = NaN
 └──────────────────────────────────────────────────────────────┘  each run: NoWrap, MaxLines 1, ellipsis;
   the pill:  Fill Tok.AccentSelectedTextBackground, ink Tok.TextOnAccentSelectedText,                the run AFTER the pill takes Grow 1
              Padding 3/1/3/1, Corners Radii.Control 4, Shrink 0,                                     (SearchHighlight.cs:41, 58-59)
              inner TextEl ALWAYS MaxLines 1 + NoWrap — a match never wraps inside its own pill.

 maxLines > 1 (a Charts grid title)
 ┌──────────────────────────────┐
 │ The Neth ╔══════╗ ds Anthem  │  run row: Wrap = TRUE, Grow 0, Basis NaN, ClipToBounds FALSE,
 │          ║ erlan ║           │  MaxHeight = lines × LineBoxFor(size)  ← the ONLY thing bounding the wrap,
 │ continued on line two        │  because a run ROW has no MaxLines of its own the way a TextEl does.
 └──────────────────────────────┘  Runs: Wrap, MaxLines = lines. Grow is FORCED OFF on every run (a grown run
                                   would eat line 1 and push the pill down alone) and NO trailing spacer is
                                   added (flex would hand it the rest of line 1 and break the tail early).
                                   Breaks happen BETWEEN runs — SpanTextEl has no per-span background fill,
                                   so a real paragraph would have to trade the pill for accent-coloured text.

 NO MATCH / OUT-OF-RANGE (matchLen ≤ 0, matchStart < 0, or start+len > text.Length)
 ┌──────────────────────────────────────────────────────────────┐
 │ Radiohead                                                    │  ONE plain TextEl at the caller's size/weight/
 └──────────────────────────────────────────────────────────────┘  colour, wrap+MaxLines per the same rule,
                                                                   CharacterEllipsis. No box, no pill, no spacer.
   LineBoxFor: 14 → 20 · 12 → 16 · else ceil(size × 1.43)   (SearchHighlight.cs:75-80)
```

### W17 — Settings ▸ Theme: the tab this chapter's knobs live on @ ~760 (8 DIP/char)

```
 Section header  "Theme"  +  "Theme, zoom and shell effects"      settings.appearance.title / .subtitle
 ┌──────────────────────────────────────────────────────────────────────────────────────┐
 │ ◨ Theme              System follows the Windows setting live   [System│Light│Dark]   │ SelectorBar
 │ ◨ Zoom               Scale the whole app, like Ctrl + / Ctrl − [ Auto (150 %)   ▾ ]  │ ComboBox w160
 │ ◨ Marquee text       Scroll overflowing titles …               (   ●)               │ ToggleSwitch
 │ ◨ Color washes       Tint the shell and page surfaces …        (   ●)               │ ToggleSwitch
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │ ▾ Row density        Choose how much music fits …      「Cozy」                      │ EXPANDER, collapsed
 │     ┌──────┐┌──────┐┌──────┐┌──────┐        ← ItemsHeader = the Strip of 4 Tiles     │ (SettingsValueTag
 │     └──────┘└──────┘└──────┘└──────┘                                                 │  reports the answer)
 │     ◨ Always hide track artwork   Remove cover thumbnails …    [x]                   │ ← an ITEM of THIS
 ├──────────────────────────────────────────────────────────────────────────────────────┤    expander's body,
 │ ▾ Track list style   Artwork-forward rows or a classic table   「Modern」            │    not a top-level row
 │     ┌──────┐┌──────┐   Modern / Classic wireframes                                   │
 ├──────────────────────────────────────────────────────────────────────────────────────┤
 │ ▾ Track page layout  Side rail automatically, or Hero …        「Automatic」         │
 │     ┌──────┐┌──────┐   Automatic / Hero wireframes                                   │
 │     ◨ Keep left-rail same size      …                          (   ●)                │ ← Automatic only
 │     ◨ Clear all remembered sizes    …                          [ Clear ]             │ ← Button.Standard,
 ├──────────────────────────────────────────────────────────────────────────────────────┤    enabled only when
 │ ▾ Design (sidebar)   Choose the left-hand navigation …         「Wavee Curated」     │    a rail pref moved;
 ├──────────────────────────────────────────────────────────────────────────────────────┤    confirm dialog
 │ ◨ Lyrics second line …                             [Off│Translation│Romanization]    │
 │ ◨ Animated lyrics backdrop …                                   (   ●)                │
 │ ◨ Lyrics blur        0 turns them off …            ├──●────────┤ 42 %   Auto         │ Slider 0-100 len 180
 ├──────────────────────────────────────────────────────────────────────────────────────┤   + "Auto" hyperlink,
 │ ◨ Hero (Now playing) Show the cover or a player …  [Cover│<style short label>]       │   shown ONLY while pinned
 │ ◨ Player style       Which of the twelve players …  [ Balcony            ▾ ]         │ ComboBox w180
 └──────────────────────────────────────────────────────────────────────────────────────┘
  ◨ = every Appearance row carries a per-row glyph, SettingsGlyphs.Row(SettingsTab.Appearance, <key>).
  Both structural rules (§6.5) are visible here: a picker is always an expander BODY, never a header; and every
  expander ships COLLAPSED with its current answer in a SettingsValueTag.
```

### W18 — The keyboard focus ring, and the five margin arms @ 8 DIP/char

```
The ENGINE draws it; no app node paints a ring. WinUI's DUAL visual: a 2-DIP PRIMARY (outer) stroke hugging the
inside of the focus rect's edge, and a 1-DIP SECONDARY (inner) stroke immediately inside that. Both are centre-line
SDF strokes, so each insets by half its thickness, and the corner radii GROW with the expansion (by the smaller
adjacent expansion) so the pair stays concentric with the control's own corner — SceneRecorder.cs:3340-3369.
It paints ONLY when focus arrived from the keyboard: NodeFlags.FocusVisual (1u << 22) is set by Tab/arrows and
"pointer focus does NOT set it" (NodeFlags.cs:51). InputHooks.FocusNode(handle, visual) is the app-side seam —
visual:true = a keyboard-style landing WITH the ring, visual:false = a pointer-style landing without one.

  FocusVisualMargin −3  (the stock Button/IconButton default, Button.cs:102)   →  ring 3 DIP OUTSIDE the bounds
   ╭┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╮  ← outer band, 2 DIP, Tok.FocusOuter
   ┆ ╭────────────────╮┆ ← inner band, 1 DIP, Tok.FocusInner
   ┆ │  ▶   Play      │┆ ← the control edge (r18 capsule; the ring's radius is 18 + 3 = 21, concentric)
   ┆ ╰────────────────╯┆
   ╰┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╯    with the default margin the pair lands exactly on the edge: edge → 1 inner → 2 outer

  FocusVisualMargin All(2)  (WaveePicker's glyph-less radio, WaveePicker.cs:244)  →  ring 2 DIP INSIDE the bounds
   ┏━━━━━━━━━━━━━━━━━━┓  ← the CARD's own border (1 unselected / 2 selected, growing inward)
   ┃╭┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╮┃  ← the ring, clear of it
   ┃┆ ▓▓ ▬▬▬▬▬  ─  ─ ┆┃     WinUI's radio margin (−7,−3) would draw the ring THROUGH that border
   ┃╰┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╯┃
   ┗━━━━━━━━━━━━━━━━━━┛

 THE DEFAULT IS NOT "nothing". Element.FocusVisualMargin is Edges4? and NULL means the WinUI template default,
 −3 all around (Dsl/Element.cs:296) — so a bare BoxEl that declares no margin still draws its ring 3 DIP OUTSIDE
 itself, into the neighbouring card. THAT is why a card/tile declares +2; it is an override, not a fill-in.

 THE FIVE ARMS IN 0.2.9 (36 explicit call sites; a sixth arm is a regression unless it is named here)
  margin            n   where                                                              why
  ── (unset)       —   every stock control (Button/IconButton/HyperlinkButton/Repeat/      null ⇒ the template value;
                        ToggleButton −3; CheckBox/RadioButton/ToggleSwitch −7,−3), plus     correct for a control,
                        bare boxes that never declared one — e.g. MediaCard's cover FAB     WRONG-BY-DEFAULT for a
                        (:1231), Focusable with no margin ⇒ a −3 ring over the artwork      bare box (see above)
  (2,2,2,2)        21   bare clickable cards / tiles / chips: BrowseTiles:38,73,101,172,    +2 keeps the ring inside
                        BrowsePage:702, ArtistPage.Shelves:34,116, ConcertUi:94,142,172,    the card's own rounded
                        469,751, ConcertHubPage:316, MonthBoard:156,202,                    corner instead of 3 DIP
                        ContentFilterChips:83, LikedFactsPanel:1078,1122,                   out into its neighbour
                        RecentsPage:572, LibraryV3Chips:253,290
  (1,1,1,1)/All(1) 11   ROW scale: TrackRow:548,631, DetailTracks:3534, FacePiles:103,132,  at 40-64 DIP a 2-DIP
                        LikedFactsPanel:696,724,1157,1185,1459,1747                         inset eats the zebra
                                                                                            stripe's edge; 1 sits
                                                                                            just inside the row's
                                                                                            1-px card stroke
  All(2)            1   WaveePicker s_bare (:244)                                           see the sketch above
  All(−2)           2   PlayerStyleFlyout:101,182                                           flyout rows run edge-to-
                                                                                            edge in a padded pane;
                                                                                            −2 borrows the pane's own
                                                                                            padding instead of being
                                                                                            clipped by it
  All(−3)           1   WaveeEqualizerCurve:144                                             Role = Slider on a
                                                                                            bordered card: it wears
                                                                                            the STOCK ring, outside
  A `live ? new Edges4(1f,…) : default` arm (FacePiles:132, LikedFactsPanel:696) is the SAME rule read backwards: a
  row that is not live carries no margin because it carries no Focusable either.
```

---

## 3. Tokens

| element | size (DIP) | padding / gap | radius | type style | colour / brush token | material / elevation | source |
|---|---|---|---|---|---|---|---|
| **Spacing scale** | 2 · 4 · 8 · 12 · 16 · 20 · 24 · 32 | — | — | — | — | — | engine `Dsl/Spacing.cs:11-18` |
| Page gutter (wide) | 36 | `PagePadWide` = (36, 24, 36, 36) | — | — | — | — | `Dsl/Spacing.cs:22, 30` |
| Page gutter (narrow) | 16 | `Edges4.All(16)` | — | — | — | — | `Dsl/Spacing.cs:23, 31` |
| **Radius ramp** | — | — | 0 · 4 · 8 · 8 · 16 · Full | — | — | — | `Dsl/Radii.cs:10-15` (`Overlay` is a second name for 8) |
| Content pane corner | — | — | (8, 0, 0, 0) | — | — | — | `WaveeShell.cs:150` |
| **Control height** | 32 | — | 4 | — | — | — | `WaveeTokens.cs:56` |
| Nav item height | 44 | — | — | — | — | — | `WaveeTokens.cs:56` |
| Track row height | `WaveeSize.TrackRowH` 56; the DENSITY ladder is Modern 40/48/56/64 (default 48) and Classic 36/40/44/48 (default 40) | — | — | — | — | — | `WaveeTokens.cs:56`; `DetailTrackTableRules.cs:45-47` |
| Track row art edge | Modern 32/32/40/48 · Classic 32/32/32/40 — always on the thumb ladder, always ≥ 8 DIP of room above and below | — | — | — | — | — | `DetailTrackTableRules.cs:54-56` |
| Player dock | 72 (`Reserve` 72, `Margin` 0) | — | — | — | unpainted | `Elevation.DockTop` available, not used by the dock | `WaveeTokens.cs:56, 81-83` |
| Nav pane / compact | 240 / 56 | — | — | — | unpainted | — | `WaveeTokens.cs:57` |
| Detail rail | album 280 · playlist 240 | — | — | — | — | — | `WaveeTokens.cs:60` |
| Rail card | 180 | — | — | — | — | — | `WaveeTokens.cs:57` |
| Art sizes | thumb 40 · bar 48 · NPV 64 | — | — | — | — | — | `WaveeTokens.cs:58` |
| Thumbnail ladder | 32 · 40 · 48 · 56 · 64 | — | — | — | — | — | `WaveeTokens.cs:65` |
| Section rhythm | gap 32 · wide 40 | — | — | — | — | — | `WaveeTokens.cs:70` |
| Page measure cap | 1600 | — | — | — | — | — | `WaveeTokens.cs:75` |
| **Type: TrackTitle / CardTitle** | 14/20 | — | — | `Ui.BodyStrong` 600 | `Tok.TextPrimary` (bound) | — | `WaveeType.cs:23, 28` |
| Type: TrackMeta | 12/16 | — | — | `Ui.Caption` 400 | `Tok.TextSecondary` (bound) | — | `WaveeType.cs:31` |
| Type: Eyebrow | 12/16 | tracking +30/1000 em | — | `Ui.Caption` 600 | **call site owns colour** | — | `WaveeType.cs:38, 54` |
| Type: RailHeader | 20/28 | — | — | `Ui.Subtitle` 600, UI face | `Tok.TextPrimary` | — | `WaveeType.cs:57` |
| Type: ModuleHeader | 20/28 | tracking −6/1000 | — | `Ui.Subtitle` 600, Display face | `Tok.TextPrimary`; meta run `Tok.TextTertiary` | — | `WaveeType.cs:63-99` |
| Type: PageHero | 28/36 | — | — | `Ui.Title` 600 | `Tok.TextPrimary` | — | `WaveeType.cs:128` |
| Type: DetailHero | 28/36 (→ 40/52 when `winH ≥ 900`) | tracking −20/1000 | — | `Ui.Title` 600, Display face | `Tok.TextPrimary` | — | `WaveeType.cs:134-138`; `DetailShell.cs:590` |
| Type: SurfaceDisplay | 40/52 | tracking −12/1000 | — | `Ui.TitleLarge` **400**, Display face | `Tok.TextPrimary` | — | `WaveeType.cs:146-151` |
| Type: ArtistDisplay | 84/96 (MinSize 68) | tracking −28/1000 | — | **700**, Display face | `Tok.TextPrimary` | — | `WaveeType.cs:155-163` |
| Type: ArtistTitle | 48/60 (MinSize 40) | tracking −20/1000 | — | **700**, Display face | — | — | `WaveeType.cs:167-175` |
| Type: ArtistCompactTitle | 32/40 (MinSize 28) | tracking −12/1000 | — | **700**, Display face | — | — | `WaveeType.cs:179-187` |
| Type: NowPlayingTitle | 20/28 | — | — | `Ui.Subtitle` 600 | — | — | `WaveeType.cs:190` |
| Type: PickQuote / FoldTitle | 28/36 | tracking **−12**/1000 | — | **400**, Display face | `Tok.TextPrimary` | — | `WaveeType.cs:198-212` |
| Type: PivotLabel | 19/25 (**off-ramp**) | tracking −6/1000 | — | **350**, Display face | — | — | `WaveeType.cs:219-226` |
| Type: NpvLyric | 20/28 | tracking −6/1000 | — | **350**, Display face | — | — | `WaveeType.cs:230-235` |
| Type: StatHero | 28/36 | tracking −6/1000; unit run 12/16 | — | **350**, Display face | value `Tok.TextPrimary`, unit `Tok.TextSecondary` | — | `WaveeType.cs:254-282` |
| **CTA: media pill** | h 36 | 18/6/18/7 | `Radii.Full` → 18 | 14 px **Bold** (Style exposes only `Bold`=700) | `WaveeCta.Palette(fill)` — see §4 | border `Tok.AccentControlElevationBorder` (3 px band, AnchorEnd); brush ramp 83 ms | `WaveeCta.cs:65, 88-107` |
| CTA: icon arm of the pill | 36 × 36 | — | `Radii.Full` → circle | glyph | Button appearance ramp (not the icon-button's subtle one) | `IconHoverScale = IconPressScale = 1` | `WaveeCta.cs:121-152` |
| CTA: standard icon button | 32 × 32 | — | `Radii.Control` 4 | glyph | stock `IconButton.DefaultStyle` | — | `WaveeCta.cs:29-43, 61` |
| CTA: text action | h 32 (= 56 − 2×12) | PadX 10, Gap 8 | `Radii.ControlAll` (invisible at rest) | 14/20/600 | neutral `TextSecondary→TextPrimary→TextSecondary`; accent `AccentTextPrimary→Secondary→Tertiary` | none — no fill, no border, no scale | `WaveeCta.cs:159-218`; `ContextBandLayout.cs:27, 61` |
| **Section rule** | 20 × 2 | top margin 2 (+ header column gap 2 = 4 total) | 0 | — | call-site accent | `HitTestVisible = false`, `AlignSelf.Start` | `Surfaces.cs:339-356` |
| Accent header | — | column gap 2 | — | Eyebrow + RailHeader + rule | eyebrow & rule take the accent | — | `Surfaces.cs:362-369` |
| Section header | — | row gap 12, `AlignItems.Center` | — | `ModuleHeader` (Grow 1, **no Basis** — `Basis 0` collapses it inside a `PagedShelf`) | — | — | `Surfaces.cs:385-400` |
| Section band | — | padding 16 all, gap 12 | `Radii.Card` 8 | — | gradient: `Lerp(FillCardDefault, accent, dark .10 / light .06)` holding card α → card at 0.45 → card at 1 | **no border** (a box of boxes is one stroke too many) | `Surfaces.cs:416-433` |
| **Picker: Tile** | 116 × 84 | inset 8 (7 selected), gap 4 | 8 | label 12/16 | see W5 | — | `WaveePicker.cs:44, 63-94` |
| Picker: PaneCompact / Pane | 200 / 224 × auto | inset 10, gap 7 | 8 | — | — | — | `WaveePicker.cs:46, 48` |
| Picker: CoverMini | 92 × auto (76 miniature) | inset 8, gap 5 | 8 | — | — | — | `WaveePicker.cs:52` |
| Picker: WideRow | 480 × 100 | inset 8, gap 0 | 8 | — | — | — | `WaveePicker.cs:56` |
| Picker: strip | — | grid gap 12, `Wrap = true`, columns hold width | — | — | — | focus ring inset 2 | `WaveePicker.cs:238-254` |
| **Search match pill** | — | 3/1/3/1 | `Radii.Control` 4 | caller's size/weight, `Shrink 0`, inner run always `MaxLines 1` + `NoWrap` | `Tok.AccentSelectedTextBackground` / ink `Tok.TextOnAccentSelectedText` | — | `SearchHighlight.cs:42-54` |
| Search row, single-line arm (`maxLines = 1`) | — | — | — | row `Grow 1`, `Basis 0`, `ClipToBounds`, trailing `Grow 1` spacer | — | — | `SearchHighlight.cs:59, 66` |
| Search row, wrapping arm (`maxLines > 1`) | `MaxHeight = lines × LineBoxFor(size)` | — | — | row `Wrap`, `Grow 0`, `Basis NaN`, **no** spacer, runs' `Grow` forced 0 | — | — | `SearchHighlight.cs:34-38, 66-67` |
| Search row, NO match | — | — | — | one plain ellipsised `TextEl` — no box, no pill | caller's `baseColor` | — | `SearchHighlight.cs:23-29` |
| **Picker ink** (miniature tint) | — | — | — | — | selected `AccentDefault` / `@0.45`; unselected `@0.58` / `@0.22` | — | `WaveePicker.cs:32-34` |
| **Shimmer threshold** | 80 (min edge) | — | caller's | — | `#2A2A2A` dark / `#F2F2F2` light, tinted 0.55 | opacity breathe 1.0↔0.5 / 1000 ms | `Surfaces.cs:57-66, 203, 455` |
| Decode bucket / ceiling | 8 px grid / 2048 px | — | — | — | — | — | `Surfaces.cs:25, 31` |
| **Elevation: card** | — | — | — | — | dark blur 8, y +2, `#00000033`; light blur 4, y +2, `#0000001A` | `Elevation.Card` | engine `Dsl/Elevation.cs:18-21` |
| Elevation: card **hover** | — | — | — | — | dark blur 16, y +4, `#00000040`; light blur 12, y +4, `#00000024` | `Elevation.CardHover` | `Dsl/Elevation.cs:22-24` |
| Elevation: tooltip | — | — | — | — | dark blur 16, y +4, `#00000040`; light blur 8, y +4, `#00000024` | `Elevation.Tooltip` | `Dsl/Elevation.cs:26-28` |
| Elevation: flyout | — | — | — | — | blur 16, y +8, `#00000042` dark / `#00000024` light | `Elevation.Flyout` | `Dsl/Elevation.cs:33-35` |
| Elevation: dock top | — | — | — | — | dark blur 12, y **−2**, `#00000028`; light blur 6, y **−1**, `#00000014` | `Elevation.DockTop` — an UPWARD shadow, and **Wavee's dock does not use it** | `Dsl/Elevation.cs:37-39` |
| Elevation: dialog | — | — | — | — | blur 64/48, y +16/+12, `#00000066` / `#00000030` | `Elevation.Dialog` | `Dsl/Elevation.cs:41-43` |
| Content region | — | — | (8,0,0,0) | — | `WaveeColors.FileArea` | 1 px `Tok.StrokeCardDefault` on LEFT+TOP only, **no shadow** | `WaveeTokens.cs:95-102`; `WaveeShell.cs:150, 1140` |

### 3.1 Resolved colour tokens, neutral palette (the only palette Wavee ships)

`WaveeTheme.ResolvePalette() => Tok.NeutralPalette` (`WaveeTheme.cs:15`) — the palette picker is deleted; this method
survives as the ONE place a future preset would be named.

| token | light | dark | source |
|---|---|---|---|
| `Tok.TextPrimary` | `#000000E4` | `#FFFFFF` | engine `PaletteBuilder.cs:437, 323` |
| `Tok.TextSecondary` | `#0000009E` | `#FFFFFFC5` | `:438, 324` |
| `Tok.TextTertiary` | solved to AA against the lightest host (the near-white card) | `#FFFFFF87` | `:398-400, 325` |
| `Tok.TextDisabled` | `#0000005C` | — | `:440` |
| `Tok.AccentDefault` | `#005FB8` | (OS ramp shade, or the dark token) | `Tokens.cs:438` |
| `Tok.AccentTextPrimary` (= `WaveeAccent.Decor`) | `#004275` | `#A6D8FF` | `:248, 336` |
| `Tok.AccentTextSecondary` | `#002642` | `#A6D8FF` | `:249, 337` |
| `Tok.AccentTextTertiary` | `#005FB8` | `#76B9ED` | `:250, 338` |
| `Tok.AccentSubtle` (= `SelectedRest`) | `#005FB824` (14 %) | `#60CDFF29` (16 %) | `:252, 340` |
| `Tok.AccentSelectedTextBackground` | `#0078D4` | — | `:255` |
| `Tok.FillSubtleSecondary` (= `ChromeHover`) | `#00000009` | `#FFFFFF0F` | `:214, 302` |
| `Tok.FillSubtleTertiary` (= `ChromePressed`, dark `RowZebra`) | `#00000006` | `#FFFFFF0A` | `:215, 303` |
| `Tok.FillCardDefault` | `#FFFFFFB3` | `#FFFFFF0D` | `:416, 304` |
| `Tok.FillCardSecondary` | `#F6F6F680` | `#FFFFFF08` | `:417, 305` |
| `Tok.FillLayerDefault` | `#FFFFFF80` | `#3A3A3A4C` | `:417, 306` |
| `Tok.FillSolidBase` | `#F3F3F3` | `#202020` (`Tinted(0.125, ·, 0)`) | `:420, 290` |
| `Tok.FillSolidTertiary` | `#F9F9F9` | — | `:423` |
| `Tok.StrokeCardDefault` | `#0000000F` | `#00000019` | `:427, 316` |
| `Tok.StrokeControlDefault` | `#00000017` | — | `:425` |
| `Tok.StrokeDividerDefault` | `#0000000F` | `#FFFFFF15` | `:428, 317` |
| `Tok.FocusOuter` (ring PRIMARY, 2 DIP) | `#000000E4` | `#FFFFFF` | `PaletteBuilder.cs:254, 342` (and `:453` in `BuildWinUILight`) |
| `Tok.FocusInner` (ring SECONDARY, 1 DIP) | `#FFFFFFB3` | `#000000B3` | `PaletteBuilder.cs:255, 343` (and `:454`) |
| `Tok.FocusThickness` | 2 — a `const`, not a palette read; the inner band is a fixed 1 DIP in the recorder | same | engine `Dsl/Tokens.cs:455-456`; `SceneRecorder.cs:3358-3368` |
| `Tok.OnMediaPrimary / Secondary / Tertiary` | white 1.00 / 0.80 / 0.60 (**theme-invariant**) | same | `Tokens.cs:361-365` |
| `Tok.MediaScrim` | black @ 0.55 (140) | same | `Tokens.cs:367` |
| `Tok.MediaStage` | `#0A0A0A` | same | `Tokens.cs:370` |
| `Tok.SystemFillSuccess` | `#0C6B0C` | `#6CCB5F` | `:474, 363` |
| **Shell (Files/neutral) plate** = `Toolbar` = `Sidebar` = `PlayerBar` | `#FFFFFFB3` | `#3A3A3A73` | `PaletteBuilder.cs:124-126, 150-152` |
| Shell `FileArea` = `Content` | `#FFFFFF80` | `#3A3A3A4C` | same |
| Shell `ContentAlt` | `#F6F6F680` | `#FFFFFF08` | same |
| Shell `RowZebra` | `#00000008` (3.1 %) | `#FFFFFF0F` → **overridden** by `WaveeColors.RowZebra` to `FillSubtleTertiary` `#FFFFFF0A` | `PaletteBuilder.cs:114`; `WaveeTokens.cs:230` |
| Shell `RowHover` | `#0000000D` (5.1 %) | `#FFFFFF0F` | `PaletteBuilder.cs:115, 158` |
| Shell `RowPressed` | `#00000012` (7.1 %) | `#FFFFFF0A` | `PaletteBuilder.cs:116, 160` |
| `WaveeColors.ShellGround` | `#EDEDED` (= `MicaRef.LightDefault`) | `#202020` | `WaveeTokens.cs:163-170` |
| `WaveeColors.ContentSurface` (= `FloatingPane`) | `#F9F9F9` | `#282828` | `WaveeTokens.cs:181-188` |
| `WaveeColors.ContentLayer` | white @ α 170 (2/3) | white @ α 9 (0.0364) | `WaveeTokens.cs:195-210` |
| `WaveeColors.PremiumText` | `Tok.SystemFillSuccess` `#0C6B0C` | `#1DB954` | `WaveeTokens.cs:120` |
| `ShellMaterialLayer.NeutralGround` | `#EDEDED` @ α 0.03 | `#202020` @ α 0.03 | `ShellMaterialLayer.cs:89` |
| `WaveePalette.PageToneNeutral*` | `#F5F5F5` | `#151515` | `WaveePalette.cs:215-216` |
| `WaveePalette.BarSurface` | `#1A1B1E` (single value, both themes) | same | `WaveePalette.cs:332` |
| `Surfaces.ArtworkPlaceholder*` | `#F2F2F2` | `#2A2A2A` | `Surfaces.cs:57-58` |
| `WaveeColors.Badge` | `Tok.AccentDefault` | same | `WaveeTokens.cs:278` |
| `WaveeColors.FloatingChrome` (= `ShellGround`) | `#EDEDED` | `#202020` | `WaveeTokens.cs:133` |
| `Tok.AccentDisabled` / `Tok.TextOnAccentDisabled` | the CTA's disabled plate + ink — system tokens on purpose, so a disabled control never advertises the page's accent | same | `WaveeCta.cs:231-232` |
| `Tok.AccentControlElevationBorder` | the pill's 3-px rest/hover border band (`…OnAccentSecondary` @0.33 → `…OnAccentDefault` @1, `AnchorEnd`) | same | `WaveeCta.cs:228` |
| `Tok.TextOnAccentSelectedText` | the search pill's ink | same | `SearchHighlight.cs:50` |
| `Tok.StrokeControlDefault` (picker card border, unselected) | `#00000017` | — | `PaletteBuilder.cs:425`; `WaveePicker.cs:77` |

**The ladder arithmetic** (so a future preset lands on the same steps rather than on hand-mixed greys):
- `DarkGroundL = 0.125`; `DarkContentLift = (40/255 − 0.125) / (1 − 0.125) = 0.03641` → `#202020` → `#282828`
  (`WaveeTokens.cs:145-146`).
- `LightGroundL = 243/255`; `LightGroundDrop = (243 − 237)/243 = 0.024691` → `#F3F3F3` → `#EDEDED`
  (`WaveeTokens.cs:155-156`).
- `LightContentLayerA = (249 − 237)/(255 − 237) = 2/3` → white @ 170. The identity
  `Over(ContentLayer, ShellGround) == ContentSurface` is pinned by `ShellMergedRungTests:177`.
- Both are expressed as MIX FRACTIONS, not literal greys, so a tinted preset canvas takes an equivalent
  perceptual step instead of being flattened back to neutral. At chroma 0 they land on exactly 40/255 and 237/255.

---

## 4. Colour & material

### 4.1 The cover-colour plane (the input to everything below)

`SpotifyLive/CoverColorPlane.cs` — **image-keyed**, not entity-keyed: the colours are a property of the cover, not of
the row that shows it. One durable table, two feeds, one five-role `Scheme`:

```
Scheme(uint BackgroundBase, BackgroundTintedBase, TextBase, TextSubdued, TextBrightAccent)   :47-50
  feed 1  extension kind 179 VISUAL_IDENTITY_TRAIT — free, rides batches the app already makes, DARK ONLY
          (its three schemes are elevation levels base/darker/darkest, not light-vs-dark)
  feed 2  getDynamicColorsByUris — the universal filler, keyed spotify:image:<id>, returns dark AND light
  TTL     hit 180 days · miss (negative) 7 days                                              :55-56
  batching  BatchCap 50 uris · PumpDebounceMs 120 (coalesce a grid realize into ONE batch) · MaxQueue 512
  persistence  <LocalCache>/cover-colors.json, flush debounce 1500 ms, Prewarm() off-thread at startup
```

| function | file:line | input → output |
|---|---|---|
| `TryGetTint(url, lightTheme, out argb)` | `:194-222` | url → `BackgroundBase` of the requested half. **Light theme only accepts a LIGHT grading**: a dark-only (kind-179) entry returns false and stays queued, so a dark slab never lands on a pale page. A miss ENQUEUES ⇒ rendering the art IS the request. |
| `TryGetScheme(url, lightTheme)` | `:227-244` | url → the full 5-role scheme, or null. Same enqueue-on-miss. |
| `Watch(url)` | `:105-118` | url → an `IReadSignal<int>` that changes only when THIS image is graded — for page chrome. |
| `Epoch` | `:98` | bumps once per landed BATCH — subscribe from ART TILES only. |
| `HasFreshDark(url)` | `:255` | a pure probe that never enqueues (the kind-179 projector's planning question). |

**Two halves, opposite directions** (`Surfaces.cs:113-155`):

- `Surfaces.SchemeFor(url)` — the **PAGE** half, follows the active theme. Everything that paints a SURFACE the
  page's own ink then sits on: page tone, hero/blend washes, shell material tint, section spines.
- `Surfaces.ChromeSchemeFor(url)` — the **CHROME** half, takes the OPPOSITE theme's grading on purpose.
  Everything that paints a SOLID PLATE carrying on-accent ink: the Play capsule, Verified/Following pills, the
  stage's filled transport. Rationale from the payloads: the light grading is the softer, lower-chroma treatment
  (median HSV S ≈ 0.45) while the dark grading carries the stronger chroma (median S ≈ 0.73). A light page wants the
  dark grading's chroma for its one CTA; a dark page wants the light grading's softness so the plate does not glow.
  Both fall back to the other half when only one exists — `ChromeSchemeFor` literally re-asks for the SAME-theme half
  when the opposite one is missing (`Surfaces.cs:153-154`), so a dark-only kind-179 entry still colours the CTA.
  **The rule in one line:** if the app's own ink lands ON it, grade it FOR the theme; if it carries on-accent ink and
  has to be seen, grade it AGAINST the theme.

**The three "no colour yet" states, and what each paints** — a MISS is not an error, and none of them is a skeleton:

| state | art tile | page tone / wash | shell material |
|---|---|---|---|
| No grading at all (miss ⇒ enqueued) | the NEUTRAL tile (`#2A2A2A`/`#F2F2F2`), opaque | `CoverPageTonePlane` returns a bare `BoxEl { Grow = 1, HitTestVisible = false }` — no fill, no corners, no clip (`CoverPaletteLeaves.cs:92-94`) | `WriteHeldColor` on a claim: the PREVIOUS page's colour is kept, so the chrome never dips |
| Graded, but wrong half (a dark-only entry on a light page) | neutral tile — `TryGetTint` REFUSES a dark grading for a light theme and stays queued | same as above | same as above |
| Greyscale art (`HSV S < 0.12`) | tinted toward a grey — visually the neutral tile | `PageToneNeutralDark #151515` / `PageToneNeutralLight #F5F5F5`, **not** an invented hue | a neutral-ish tint; the Play capsule falls to `Tok.AccentDefault` system blue |

### 4.2 Cover ARGB → renderer colour (`WaveePalette.cs`)

| derivation | file:line | formula | typical result |
|---|---|---|---|
| `ToColor(argb)` | `:16-20` | byte unpack | — |
| `Accent(scheme)` | `:151-164` | the most saturated of `[BackgroundTintedBase, BackgroundBase, TextSubdued, TextBrightAccent]`; strict `>` so ties keep the earlier (higher-preference) role | **Why not `textBrightAccent`:** over 9 316 cached gradings it is pure `#FFFFFF` in every dark half and pure `#000000` in every light half — 100 %. Reading it as "the accent" made `NeutralS` fire on EVERY cover, so every Play CTA rendered system blue. |
| `Lift(c, targetMax = 210)` | `:25-33` | scale RGB uniformly so `max(R,G,B) = 210/255`; **only ever lifts**. Pure black → neutral grey at the target | Spotify's `colorDark` is often near-black and collapses to nothing as a tint |
| `Vivid(c, minS = 0.55, targetMax = 210)` | `:48-53` | HSV: `S ← max(S, 0.55)`, `V ← max(V, 210/255)` at constant hue. `S ≤ NeutralS 0.08` returns unchanged | a washed pastel CTA stops reading as a grey plate |
| `ChromeAccent(scheme)` | `:126-131` | `Lift(Accent(s))`; if its S ≤ 0.08 → `Tok.AccentDefault`, else `Vivid(lifted)` | **THE** page chrome accent. Greyscale art keeps the system blue rather than shipping a grey Play button |
| `ChromeFromPayload(argb)` | `:89-90` | `argb == 0 ? Tok.AccentDefault : Lift(ToColor(argb))` | a Pathfinder `extractedColors.colorDark` before the plane has graded the art |
| `Hairline(seed)` | `:58-60, 93-117` | cap `S` at 0.50, then **22-iteration bisection on V** to contrast 3.25 : 1 against `Flatten(FillCardDefault, FloatingPane)`; dark keeps the lighter solution, light the darker | a quiet identity rule on a card |
| `HairlineHover(seed)` | `:63-65` | same, 3.55 : 1 against `Flatten(FillCardSecondary, FloatingPane)` | below the 4.5 text threshold on purpose |
| `TextInk(seed)` | `:74-84` | the same hue solve at `TextContrast 4.5`; if it still fails `MeetsAaText`, fall back to `ColorContrast.PickContrast(background)` | some mid-blues top out ~4.4 : 1 on a dark card at the saturation cap |
| `DataDotInk(argb, theme)` | `:311-319` | **dark = passthrough** (the wire colours already ARE the dark answer). Light: HSL, `L ← 0.30` when hue ∈ [40°, 200°] (yellow→cyan, intrinsically light) else `L ← 0.40`, at a common `S = 0.65`. `S ≤ 0.08` ⇒ neutral at the same L, never an invented hue. Alpha preserved | the Camelot key wheel: harmonically adjacent keys stay adjacent hues |
| `PageTone(scheme, theme)` | `:229-240` | `dominant = Accent(s)`; if `HSV S < 0.12` → the neutral tone. Else HSL: dark `FromHsl(h, min(S, 0.30), 0.15)`; light `FromHsl(h, min(S, 0.16), 0.94)` | **HSL, not HSV** — the contract is stated in LIGHTNESS, and a saturated hue at `V = 0.15` and a grey at `V = 0.15` have very different perceived brightness (`:242-244`) |
| `BackgroundDark(scheme)` / `TintedDark(scheme)` | `:166-167` | the two RAW role reads — `ToColor(BackgroundBase)` / `ToColor(BackgroundTintedBase)`, no lift, no clamp | the artist blend wash's dark arm and the shell tint's dark arm are the only callers; everything else goes through `Accent`/`PageTone`/`ChromeAccent` |
| `Neutral` scheme | `:325-327` | `BackgroundBase #1C1C1C · TintedBase #2A2A2A · TextBase #FFFFFF · TextSubdued #B3B3B3 · TextBrightAccent #FFFFFF` | every role greyscale ON PURPOSE — a fabricated blue made the fallback the one "cover" with a hue |
| `PageToneChromaFloor` / `NeutralS` / `TextContrast` / `HairlineSaturationCeiling` | `:189, 37, 70, 12` | 0.12 · 0.08 · 4.5 · 0.50 | the four thresholds every derivation above branches on. `Vivid`'s `V ← max(V, 210/255)` is a NO-OP after `Lift` (same 210 target) — only S moves |

**Why the page-tone clamp is the point** (`WaveePalette.cs:169-186`): Apple Music's unclamped version of this is its
loudest complaint — a saturated cover makes a page genuinely hard to read and a dark cover makes one indistinguishable
from every other dark cover. Forcing L is also what makes the standard `Tok` ink tokens correct on the plane in both
themes: there is no on-media ladder on these pages, because polarity is guaranteed by construction rather than
measured per cover. The FIRST light clamp (L 0.89 / S ≤ 0.42) was the dark arm mirrored, and mirroring was the
mistake: a green cover landed on ≈`#D7EFD7` and REPLACED the page ground with a coloured plane.

### 4.3 Where each derivation is applied, and how it transitions

| surface | input → function | applied at | transition |
|---|---|---|---|
| Art placeholder tile | `TryGetTint` → `Lerp(neutral, tint, 0.55)` | `Surfaces.PlaceholderFor` / `WatchedPlaceholder` (`:78-111`) | a **bound `Fill`** marks PaintDirty on exactly that tile — never a component re-render, never the global Epoch fan-out |
| Detail page ground | `SchemeFor` → `PageTone` → `with { A = 0.20 / 0.30 }` | `CoverPageTonePlane` bound `Fill` (`CoverPaletteLeaves.cs:112-115`) | `BrushTransitionMs = WaveeMotion.Standard` **250 ms** — a grading arrival cross-fades |
| Detail hero-only band | same tone | `HeroOnlyVeil` gradient (`:150-168`): stops `0 → α`, `start → α`, `end → 0`, `1 → 0`, where `start = Clamp(BackdropBandFor(heroBand)/pageH, 0.12, 0.80)`, `end = min(1, start + 0.22)` | mounts/unmounts with the plane |
| Shell material tint (detail/artist) | light `Lift(ToColor(TextBase)) @ α 0.05`; dark `TintedDark(scheme) @ α 0.14` | `CoverShellTintBinder` → `ShellMaterial.Publish` → `ShellMaterialLayer.Tint` | `BrushTransitionMs 250` on a **static** fill in a re-rendered component (a BOUND fill is excluded from the implicit brush transition per-channel) |
| Shell material wash (Home) | three `WashLayer(Color, ArtworkKey)` | `ShellMaterialLayer.Wash` radial gradients | gradients carry **no brush-fade channel** → cross-fade by MOUNT: each layer is keyed on its artwork, so a re-grading exits the old node and enters the new one over the same pixels |
| "No colour" reading | `WaveeColors.ShellGround with { A = 0.03 }` | `ShellMaterialLayer.NeutralGround` (`:89`) | **never `ColorF.Transparent`** — transparent is premultiplied BLACK, and cross-fading into or out of it drags the interpolated colour toward black for the whole ramp. That is what read as "the shell tint goes neutral AND DARKER" at almost every navigation. |
| Artist blend wash | light `Lift(Accent(pagePal))` else `Tok.AccentDefault`; dark `BackgroundDark(pagePal ?? Neutral)` | `CoverArtistBlendWash` (`:183-194`), height `ArtistHeroLayout.BlendBackdropHeightFor(heroWidth)`; stops `0 → α (light .20 / dark .30)`, `BlendBoundaryFor(w) → α (.06 / .08)`, `1 → 0` | remounts on the cover key |
| Artist hero veil over photography | `ChromeAccent` for the accent, `Lift(Accent(pagePal))` for the wash | `Surfaces.ArtistHeroVeil(accent, axis)` (`:173-192`) | — |
| Section band / Home hero backdrop | any accent | `Lerp(FillCardDefault, accent, dark .10 / light .06)` holding the card's own α → card at 0.45 → card at 1 | — |
| Cover-art placeholder on the stage | `PlaceholderFor(url, light: !StageInk.IsDark)` | `StageInk.ArtStandIn` (`:79`) | the cover TINT survives; only the neutral it blends toward follows the stage |

**`ArtistHeroVeil`, both axes** (`Surfaces.cs:173-192`) — four stops each, the recorder limit, both releasing to α 0
at the hero seam. `layer = Tok.FillLayerDefault`; `pull = light 0.16 / dark 0.24`; `veil = Lerp(layer, accent, pull)`.

| axis | stops | why |
|---|---|---|
| Horizontal (the copy plate) | `0 → 0.96`, `0.30 → 0.92`, `0.62 → 0.35`, `1 → 0` | theme-invariant, near-opaque: the copy column sits on a real surface and the photography lives in the right half. A softened 0.42 pass made the plate a whisper in light themes; restored by explicit user ruling ("like before, it was really beautiful") |
| Vertical (copy at a photo's bottom seam) | `0 → 0`, `0.45 → 0.35`, `0.82 → light 0.42 / dark 0.78`, `1 → 0` | a 0.96 band flattened the image into a painted plate |

### 4.4 The immersive stage's polarity (`StageArm` / `StageInk`)

The **one** surface whose "media" is a full-bleed blurred backdrop it also owns the scrim of, and therefore the one
surface that flips with the theme. The alphas are SHARED between the arms (every light rung reads its opacity off its
dark twin rather than restating it); only the GROUND is mirrored.

| rung | dark | light |
|---|---|---|
| `Veil` / `Floor` | `Tok.MediaStage` `#0A0A0A` | `WaveePalette.PageToneNeutralLight` `#F5F5F5` (achromatic on purpose, so the cover's colour comes through the veil instead of fighting a tint underneath it) |
| `Ink` / `Secondary` / `Tertiary` | `WaveeOnMedia.Ink*` (white 1.00/0.80/0.60) | `Tok.MediaStage` at the same three alphas — an ON-MEDIA token, deliberately NOT `Tok.TextPrimary` (which is black @ 0.894, a second quieter ladder) |
| `Glass*` | `OnMediaPrimary` @ 0 / 0.10 / 0.16 | `Ink` @ the same alphas |
| `GlassPlate*` | `OnMediaPrimary` @ 0.14 / 0.22 / 0.28 | `Ink` @ the same |
| `Scrim*` | `WaveeOnMedia.Scrim*` verbatim | `Veil` at `WaveeOnMedia.Scrim*.A` |
| `Stroke` | `#FFFFFF3A` | `Ink` @ α 58/255 — it inverts WITH the ink; a white hairline on a light plate is an absent one |
| `ButtonFill` / hover / pressed | `WaveeOnMedia.LightButton*` (white → `#EBEBEB` → `#D7D7D7`) | `Ink` → `Lighten(Ink, 0.08)` → `Lighten(Ink, 0.157)` — **one ramp, two directions** |
| `ButtonInk` | `Tok.MediaStage` | `Veil` — the ground it stands on, so the two can never collide |
| `AccentFrom(chrome)` | `chrome` (= `ChromeAccent`) | `WaveePalette.TextInk(chrome, Light, AccentGround)` where `AccentGround = Over(Veil @ StageLayout.ScrimBaseA 0.46, Tok.OnMediaPrimary)` — the BRIGHTEST ground the light stage can produce (a plateau veil over a white cover) |
| `SkeletonBar` | `Ink` @ 0.12 | `Ink` @ 0.12 |

The scrim ALPHAS need no light arm: the sRGB transfer curve does the work — mixing toward black at a partial alpha
destroys far more perceptual luminance than mixing toward white, so the light arm's alpha'd ink clears a HIGHER
contrast ratio than the dark arm already ships (`StageArm.cs:31-34`).

### 4.5 The material channel's hand-over protocol

`ShellMaterial.Publish(slot, owner, isClaim, definite, tint, wash)` (`ShellMaterial.cs:47-58`) routes every write
through `SpotifyLive.ShellTintOwnership.Resolve` (`CoverColorPlane.cs:543-555`):

```
  !IsClaim && !amOwner              → NoWrite          a superseded/stray publish never lands
  Definite &&  HasColor             → WriteKnownColor
  Definite && !HasColor             → WriteNeutral     the page DECIDED it carries no colour
 !Definite &&  HasColor             → WriteKnownColor
 !Definite && !HasColor &&  IsClaim → WriteHeldColor   the hand-over: take the slot, KEEP the current colour
 !Definite && !HasColor && !IsClaim → NoWrite          nothing new to say
```

The consequence that matters visually: **the chrome never dips to neutral and back between two coloured pages.** A
page claims on its first publish and on a KeepAlive reactivation (`UseActivation(onActivated:)`); a page that parks
or unmounts writes nothing.

Three mechanics the table does not show, all load-bearing:

- **`hasColor` counts a WASH leg too** — `tint.HasValue || (Hero|Weekly|Mix).HasValue` (`ShellMaterial.cs:51`), so
  Home's three-radial form participates in the same protocol as a detail page's flat tint.
- **The publish is an effect, keyed on the whole answer**: `DepKey.From(HashCode.Combine(Url, known.HasValue, known,
  Tok.Theme, Ready, Disabled, Apply))` (`CoverPaletteLeaves.cs:258`). `Tok.Theme` is in the key on purpose — a live
  theme flip re-derives the tint (light `Lift(TextBase)@0.05` vs dark `TintedDark@0.14`) and re-publishes it.
- **The binder's own node is a 0 × 0, `HitTestVisible = false` box** (`:263`). It renders nothing; it exists to own a
  subscription and an effect at leaf scope.
- **The tint layer is ALWAYS mounted**, keyed `"shell.material.tint"`, even at the neutral ground
  (`ShellMaterialLayer.cs:93-99`) — a brush transition needs a previous colour on a LIVE node to fade from.
- **Every wash stop carries the wash's own RGB at α 0**, never `ColorF.Transparent` (`ShellMaterialLayer.cs:118-124`):
  stop interpolation is straight-alpha, and premultiplied black would drag the whole falloff toward black.
- **`ShellWashGeometry.Resolve`'s anchor rule**: `AnchorRight = x0 > 0`, `AnchorBottom = y0 > 0`. A full-span axis
  touches both window edges and LEADING wins, because `Start` is also the fill-the-slot arm of the ZStack arranger
  (`ShellWashGeometry.cs:48-53`).

---

## 5. Motion

| trigger | target | property | from → to | duration | easing | delay / stagger | reduced motion | file:line |
|---|---|---|---|---|---|---|---|---|
| Hover on a chip / small toggle / settings row / inline picker | the node | `HoverScale` | 1 → **1.02** | engine `SeedEased` | engine interaction curve | — | **1f** (transform skipped: recorder culls \|s−1\| ≤ 0.0008) | `WaveeMotion.cs:39, 179` |
| Press on the same | the node | `PressScale` | 1 → **0.98** | — | — | — | 1f | `WaveeMotion.cs:39, 182` |
| Hover on a button / CTA / pill / secondary circle | the node | `HoverScale` | 1 → **1.04** | — | — | — | 1f | `WaveeMotion.cs:43` |
| Press on the same | the node | `PressScale` | 1 → **0.96** | — | — | — | 1f | `WaveeMotion.cs:43` |
| Hover / press a **picker card** | the card | `HoverScale` / `PressScale` | 1 → 1.02 / 0.98 (`ScaleSubtle`) | — | — | — | 1f | `WaveePicker.cs:78-79` |
| Hover / press a **text action** | the word | `Color` ONLY | rest → hover → pressed ink | engine `HoverT` ease on `TextEl.HoverColor` | engine interaction curve | — | unaffected (an ink interpolation, not motion). **No `HoverScale` at all** | `WaveeCta.cs:193-203` |
| Hover on a media FAB / transport / on-artwork circle / "…" corner | the node | `HoverScale` | 1 → **1.07** | — | — | — | 1f | `WaveeMotion.cs:48` |
| Press on the same | the node | `PressScale` | 1 → **0.92** | — | — | — | 1f | `WaveeMotion.cs:48` |
| Hover/press on a **near-full-width row** | the row | `PressedFill` ONLY | — | 83 ms brush | — | — | unaffected | `WaveeMotion.cs:36-38` — even a 2 % swing moves each edge several DIP in opposite directions on a ~1000 px row and visibly blurs the title mid-scale |
| Hover on a disabled affordance | — | — | 1 → 1 | — | — | — | — | `ScaleTier.HoverIf(false)` `:186` |
| Any brush/fill/ink state flip | `Fill` / `Color` / `BorderBrush` | colour | old → new | **83 ms** (`Faster`) | engine brush ramp | — | unaffected (a fade is not motion) | `WaveeMotion.cs:55` |
| Hover reveal, small state change | opacity / size | — | — | **167 ms** (`Fast`) | — | — | — | `WaveeMotion.cs:57` |
| Recolor / icon swap / **material cross-fade** | `Fill` | colour | old → new | **250 ms** (`Standard`) | engine brush ramp | — | — | `WaveeMotion.cs:59` |
| Shell material TINT change (navigation) | `shell.material.tint` box | `Fill` | previous tint → new (or `NeutralGround`) | **250 ms** | engine brush ramp | — | unaffected | `ShellMaterialLayer.cs:93-99` |
| Shell material WASH change (a new grading) | one keyed wash layer | `Opacity` | Enter 0 → 1 / Exit 1 → 0 | engine default | engine default | — | **`Enter`/`Exit` = null** ⇒ hard swap | `ShellMaterialLayer.cs:34, 129-130` |
| Detail page tone: a grading LANDS | `CoverPageTonePlane` | bound `Fill` | transparent-or-old-tone → new tone | **250 ms** | engine brush ramp | — | unaffected | `CoverPaletteLeaves.cs:115` |
| List/shelf item mounts (eager stacks, bound recycler only) | the item wrapper | `Opacity` + `Dy` + `BlurSigma` | 0 → 1, +8 → 0 DIP, σ 2 → 0 | **400 ms** (`Expressive.Slow`) | `Easing.SmoothOut` cubic-bezier(.22, 1, .36, 1) | `min(index, 8) × 40 ms`, i.e. 0…**320 ms** | delay 0; `ReducedSnap` parks rise + blur at the end value and keeps the opacity cross-fade | `WaveeMotion.cs:110-126` |
| Drill-in masthead mounts | the 2-child masthead container | `Element.Stagger` | — | — | — | **45 ms** between the display line and the metadata line | `Motion.ReducedMotion ? 0f : 45f` at the call site (a plain float, no motion token) | `WaveeMotion.cs:75` |
| Page swap FORWARD | outgoing page root | `Opacity` | 1 → 0 | **120 ms** | `Easing.EaseOut` (1−(1−t)²) | 0 ms; `Exit.Active = true` | engine `ReducedSnap` | `PageNavMotion.cs:52-59` |
| " | incoming page root | `Opacity` + `Dx` | 0 → 1, **+8** → 0 | **250 ms** (`Expressive.Fast`) | `Easing.SmoothOut` | **90 ms** | " | same |
| Page swap BACK | incoming page root | `Dx` | **−8** → 0 | 250 ms | `SmoothOut` | 90 ms | " | `PageNavMotion.cs:61-68` |
| Page swap NEUTRAL | both roots | `Opacity` | cross-fade | 250 ms | `SmoothOut` | 0 | " | `MotionRecipes.PageFade` |
| Page swap, either side hosts VIDEO | both roots | `Dx` ONLY | ±8 → 0 / 0 → ∓8 | 250 ms both halves | `SmoothOut` both halves | 0 both halves | " | `PageNavMotion.cs:105-121` |
| Page swap, NEUTRAL + video | — | — | — | — | — | — | **null — an honest CUT** | `PageNavMotion.cs:99` |
| Masthead band family change | the band | `Opacity` | — | **120 ms** (`FadeThroughExitMs`) | `FluentAccelerate` out / `SmoothOut` in | — | `ReducedMotionPolicy.KeepFade` | `ShellMastheadBand.cs:24-26` |
| Cover art still loading (edge ≥ 80 DIP, non-weak GPU) | the shimmer tile | `AnimChannel.Opacity` | keyframes **(0, 1) (0.5, 0.5) (1, 1)** | **1000 ms**, looping | linear between keys | — | not gated (it is a loading indicator); the loop stops on settle | `Surfaces.cs:455, 488` |
| Cover art settles (Ready / Failed non-Canceled) | the same tile | `AnimChannel.Opacity` | flat (0, 1) (1, 1) | **1 ms**, `loop: false` | — | — | — | `Surfaces.cs:456, 488` |
| Cover art loading on a **weak GPU** | the tile | — | held FLAT | — | — | — | the frame loop must idle between decodes so uploads coalesce | `Surfaces.cs:487` |
| Cover art tile, the loading→settled EDGE | the same tile | `UseKeyframes` dep | `DepKey.From(shimmer)` | — | — | — | — | `Surfaces.cs:488` — the dep is the BOOL, so the looping track is replaced IN PLACE on the edge; the loop-track count drops and the frame loop can quiesce |
| A grading LANDS under an art tile | that tile only | bound `Fill` | neutral → `Lerp(neutral, cover, 0.55)` | **instant** (a paint, not a track) | — | — | — | `Surfaces.cs:107-111, 495` — `Prop.Of` reading `Watch(url).Value` marks PaintDirty on exactly one tile. The ONLY art-colour arrival that is not a 250 ms cross-fade, deliberately: a tile has no previous colour worth easing from |
| Detail track list: cold shimmer→real | rows | swap | 12 rows per frame, cap 60 | ~1 frame per chunk | — | — | unaffected (this is a work ramp, not an animation) | `DetailRevealRamp.cs:11-23` |
| Shared-element (Hero) cover fly | the tagged `ImageEl` | `MorphId` | source rect → dest rect | engine `ConnectedAnimation` | — | — | — | `MorphKeys.cs:9-16` — **DORMANT**: `DetailShell.cs:257` sets `MorphKey = null`. See §5.1 |
| Pointer enters a card after back-navigation | the card | nothing | — | — | — | — | — | `HoverMotionGate` (`WaveeMotion.cs:141-159`): the engine re-fires `OnPointerMoveWithin` at the SAME point when new content lands under a resting cursor. The gate records the FIRST sample as a baseline and arms only when a later sample moves > 0.5 DIP on either axis. Once armed it stays armed for the component's lifetime. |
| Any per-frame animation clock | — | — | — | — | — | — | — | `FrameTime.NowQpc` = `FrameClock.PresentQpc` (the predicted vblank the frame being produced will land on), falling back to a live QPC read only before the first frame. **Never `Environment.TickCount64`** — it advances in ~15.6 ms quanta, so at 120 Hz its per-frame delta alternates 0 / 15.6 ms. `FrameTime.cs:19-27` |

**No exceptions to the frame-time rule exist in this layer.** Two per-frame writers that compare wall-clock stamps
against each other must both read `FrameTime`, or the comparison silently spans two epochs.

### 5.1 Shared-element (Hero) morph — DORMANT, and the plumbing that must survive the port

Nothing flies in 0.2.9. The *convention* and the whole paint-side seam are nonetheless live, threaded through five
files and asserted by the nav probe in seven places, so a Wave-4/5 owner who "cleans up" an unused `MorphId`
parameter deletes the convention AND silences the probe in the same commit. This block is the owner.

**The key convention — one function, two halves.** `MorphKeys.For(DetailKind, id)` returns `"album:" + id` /
`"pl:" + id`, and `null` for everything else (liked has no uri; artist covers are circular and were deferred)
(`MorphKeys.cs:7-17`). The key IS the route key the card navigates with, so it is already unique and already present
on both sides with zero extra plumbing — that is the entire reason the convention lives in one file.

| half | call site | what it does |
|---|---|---|
| **source** (minted) | `RecentsPage.cs:1707` — `morphKey: Morphable(displayIndex) ? MorphKeys.For(DetailKindOf(kind), uri) : null` | tags the card cover. `Morphable` is a per-row flag on the page's `Shape` (`RecentsPage.cs:137, 149, 2073-2075`) true only for the **FIRST occurrence of each uri**: uris repeat down a recents list (~1 388 times on a real account), the engine's tagged registry is last-writer-wins, and `SetTaggedVisible`/`SetTaggedOpacity` hide EVERY node carrying the flying key — so a second tagged row would blank itself mid-fly (`RecentsPage.cs:1693-1697`, restated at `RecentsView.cs:527` and `MediaCard.cs:980`) |
| **destination** (deliberately null) | `DetailShell.cs:257` — `var m = raw with { MorphKey = null };` | the rail cover is the consumer (`DetailRail.cs:102, 104, 159, 320, 421` all read `m.MorphKey`). It is nulled, and the note at `DetailShell.cs:235-256` is the reason — below |

**Why the destination is null (`DetailShell.cs:235-256`, investigated 2026-08-12, three findings):**

1. It was introduced by `3b80bbcf8`, the **same** commit that removed `UseContext(SharedTransition.Begin)` from this
   shell, dropped `this.UseSoftReveal(...)`, and cut the `morph` argument off `DetailNav.OpenAlbum/OpenPlaylist`.
   The stated reason is the class contract: **ContentHost exclusively owns route-level entrance motion**, and a cover
   fly must not compete with it.
2. Setting it back would change **nothing**, because the forward CAPTURE is gone repo-wide. `ConnectedAnimation`
   fills its pending-snapshot slot only from `Begin()`; `CaptureOnLeave` is an explicit no-op ("reverse-fly capture is
   Phase 6"); `SharedTransition.Begin`/`BeginConfigured` have ZERO callers outside `AppHost`'s own ambient
   registration — even `WaveeShell.ProbeCardNav` ignores its `doMorph` argument. No capture => no pending snapshot =>
   no flight. Publishing a key here would only make the pairing LOOK wired.
3. The reason from (1) is still live: `ContentHost`'s `PageTransition` is `MotionRecipes.PageSlideForward/Back`
   (`Dx = Expressive.DistBase` 8) and `SceneStore.AbsoluteRect` INCLUDES the ancestor's `LocalTransform.Dx`, so the
   destination rect moves every frame of the slide. That drives `ConnectedAnimation.Settle`'s per-frame
   `RetargetFlight`, and the overlay would land on a page still fading up from 0 (`DestReady` gates on image decode,
   not on page opacity).

Re-arming therefore means **restoring the whole capture seam** — a `morph` action back on `DetailNav.OpenAlbum` /
`OpenPlaylist`, threaded from `HomeCardNav` and `RecentsPage`, plus `UseContext(SharedTransition.Begin)` in
`DetailShell` — **and** deciding how the fly composes with the page slide. It is not flipping one null.

**The paint path the parameter rides (all of it ports, dormant):**

```
RecentsPage.cs:1707  morphKey
      |
      +--> MediaCard.ProtoRow(..., morphKey)               :959-981
      |       \__ ArtworkOrLiked(..., morphKey)            :172-178
      +--> MediaCard.ShelfCard.Props.MorphKey              :1111  ->  :1132, :1143 (MorphId = p.MorphKey)
      |
      +--> Surfaces.Artwork(..., morphKey)                 :233-285
      |       . MorphId = morphKey on the ImageEl                          :279-280
      |       . placeholder = morphKey is null ? Transparent : PlaceholderFor(url)   :272
      |       . Children   = morphKey is null ? [Shimmer, img] : [img]     :285
      |         => a morph-tagged slot has NO shimmer sibling and a FROZEN placeholder
      |            (the section 9.2 trap: re-arming Hero must give this branch WatchedPlaceholder)
      +--> LikedSongsArtwork.Cover/Dynamic/Fitted/Fill(..., morphKey)      :21-83
              \__ LikedCoverArt._morphKey -> MorphId on the mosaic        :52-64, :141
                  LikedCoverTreatments: the composed cover occupies the exact slot the stock PNG's
                  MorphId held, so a card->rail fly would still pair                 :96-98

DetailRail.cs:99-104, 159, 320, 421   the destination consumer — reads m.MorphKey (null today)
DetailVerticalHero.cs:122-130         passes morphKey: null ON PURPOSE — this hero never participates
```

**Not a Hero key; do not fold the two conventions together.** `WaveeShell.ContentRowMorphId = "shell.content-row"`
(`WaveeShell.cs:146-148`, applied at `:991-994`, referenced by `RelativeTo` at `:1135`) is a **stable-frame anchor**
for the content card's FLIP as the sidebar resizes. It rides the same `MorphId` column and has nothing to do with
connected animation.

**The port instruction.** The `MorphId` / `morphKey` parameters and the `DetailConfig.MorphKey` property
(`DetailConfig.cs:86`) survive the port **even while dormant**, defaulting to `null` so every non-morph call site
stays byte-identical (`MediaCard.cs:978`). Dropping them is not a simplification; it is the deletion of the only
agreement the two halves have.

**Measured, not assumed.** `WaveeNavProbe` reads the engine's tagged registry in seven places —
`host.CollectMorphKeys(keys)` at `:747, 1635, 2682, 2762, 2783` and `host.FirstMorphKey` at `:2575, 2758` — and
`[conn-stress]` **aborts** with `"no home-card morph keys — aborting"` when the list comes back empty (`:747-748`).
`:2575`'s comment is the assertion in words: *"a REAL home-card key -> the morph actually FLIES"*. Keep that
behaviour in the port's gate (section 10, items 74-75) so the dormancy is a measured state rather than a hope.

---

## 6. Interaction

### 6.1 The media pill (`WaveeCta.Play` / `.Accent` / `.Pill`)

- **It is a styled stock engine `Button`, not a hand-rolled box.** It therefore inherits verbatim: the keyboard focus
  ring (`FocusVisualMargin −3` — §6.8 / W18 own the ring itself), `AutomationRole.Button`, Space/Enter mechanics, the `ButtonPalette` colour seam, and
  the 83 ms WinUI brush ramp on every state flip (`WaveeCta.cs:10-13`).
- Exactly **five** substitutions: `CornerRadius = Radii.Full`, `MinHeight = 36`, `Padding = 18/6/18/7` (the 1 px
  bottom bias is the optical baseline nudge), `Bold = true`, and `HoverScale`/`PressScale`/`Cursor.Hand`. WinUI's
  Button has no scale cue and keeps the arrow cursor; that divergence is CONFINED to this skin (`WaveeCta.cs:15-22`).
- **`MinHeight` is a FLOOR.** A caller declaring `Height` below 36 still measures 36 unless it passes
  `minHeight:` (`WaveeCta.cs:85-87`).
- **Style and palette are not independent on `Button.Create`** — a supplied style WINS and the palette argument is
  dropped. The palette must ride INSIDE the style: `Button.DefaultStyle(appearance, palette: p) with { … }`
  (`WaveeCta.cs:91-95`).
- **`WaveeCta.Palette(fill, ink, border)` is the colour seam, and it has THREE parameters** (`:225-235`). `ink`
  overrides the WCAG-picked on-fill ink; `border` overrides the `Tok.AccentControlElevationBorder` gradient (a
  photo-local white ramp passes its own). It also sets `Sizing: BackgroundSizing.OuterBorderEdge` — the
  AccentButtonStyle setter, so the fill runs under the 1 px border rather than inside it.
- **The disabled legs stay on SYSTEM tokens** (`Tok.AccentDisabled` plate, `Tok.TextOnAccentDisabled` ink): a
  disabled control must not advertise the page's accent.
- **The pressed label's alpha is keyed off the ink's LUMINANCE, not off `Tok.Theme`** — `0x80` where the on-accent
  ink is dark, `0xB3` where it is light (`WaveeCta.cs:241-242`). An artwork accent can invert the ink against the
  theme, and a caller may pass an explicit ink that is not the palette's near-black constant, so neither a theme
  read nor token equality would be correct here.
- Default label: `Loc.Get(Strings.Detail.Play)` = **"Play"** (`WaveeCta.cs:75`). Artist page passes
  `Strings.Artist.Play`; Home passes `Strings.Home.Play`; podcasts pass `Strings.Podcast.Resume`.
- Default glyph: `Icons.Play` (`WaveeCta.cs:81`).
- Utility surfaces (settings, dialogs, empty-state actions) **must** keep stock Fluent rectangles by calling
  `Button.*` directly. This pill is for media primaries only.

### 6.2 The icon arm and the standard icon button

- `WaveeCta.Icon(glyph, onClick, appearance, palette, size = 36, requestsContext)` — square, so `Radii.Full`
  resolves to a circle; the cluster reads as "three capsules, one of which happens to be round".
- It wears the **button** appearance ramp, not the icon button's subtle one: beside a filled standard capsule a
  transparent-until-hover square disappears (`WaveeCta.cs:113-116`).
- The inner glyph's AnimatedIcon scale is switched OFF (`IconHoverScale = IconPressScale = 1`) — running both would
  compound to a 1.12 hover.
- `requestsContext: true` (the overflow "…") sets `ClickRequestsContext = true` and **drops `OnClick`** — the two are
  mutually exclusive in the reconciler. The button re-enters the engine's context funnel to find the surface's
  ATTACHED menu instead of carrying a handler.

### 6.3 The text action (context bands only)

- Rest `Tok.TextSecondary` → hover `Tok.TextPrimary` → pressed `Tok.TextSecondary`, eased by the engine's own
  `HoverT` through `TextEl.HoverColor`. `primary: true` (the ONE per band) and `toggledOn: true` (a latched toggle,
  e.g. Following) both take the stock hyperlink ramp `AccentTextPrimary → AccentTextSecondary → AccentTextTertiary`.
  So accent ink in a context band always means either "the primary verb" or "this is on", and never decoration.
- **The hover boundary is the ACTION's own box, never the row.** `HoverColor` interpolates against the nearest
  interactive ancestor's `HoverT`, so a handler one level up would light every action in the cluster at once — the
  hover-container trap that produced the "all the shelf cards popped" class of bug (`WaveeCta.cs:174-177`).
- **It takes an optional leading glyph** (`WaveeCta.cs:192-197`): a `TextEl` at `Size 14` in `Theme.IconFont`,
  wearing the SAME rest/hover/pressed ink triple as the word, separated by `Gap = Spacing.S` 8. The box is
  `Direction 0`, `Shrink 0`, `AlignItems.Center`, `Justify.Center`.
- **There is no disabled arm.** `onClick` is nullable and a null handler leaves a fully live-looking word — a band
  action that can be unavailable must not be rendered, or must be rendered by something else.
- Still a button: `AutomationRole.Button`, `Focusable` with the engine's keyboard ring, hand cursor, and SENTENCE
  case (the label passes through verbatim). The label itself is `MaxLines 1` + `NoWrap` + `CharacterEllipsis`.
- **Not a general low-emphasis button.** A quiet action anywhere else is
  `Button.Create(…, ButtonAppearance.Subtle)`; a navigational word is the stock `HyperlinkButton`.

### 6.4 The picker strip

| input | behaviour |
|---|---|
| Tab | ONE tab stop per picker, landing on the current value |
| ↑ / ↓ | ±1 in DATA order (down a column) |
| ← / → | column to column at the same row |
| Ctrl + arrow | move focus **without** applying |
| Space | select |
| click | select |
| hover | card fill lifts (`FillCardSecondary` unselected / `WaveeColors.SelectedHover` selected), scale 1.02 |
| press | `FillSubtleSecondary` / `Tok.AccentSubtle`, scale 0.98 |
| focus | ring inset 2 DIP (WinUI's −7,−3 would draw through the card's own border) — one of the five arms in W18; §6.8 owns the rule |

`onChange` fires on click **and** on a keyboard rove (selection follows focus — the WinUI `RadioButtons` contract), so
it must be safe to call repeatedly. The layout is **column-major** (`ColumnMajorUniformToLargestGridLayout`);
`maxColumns` defaults to `count`, i.e. one item per column ⇒ a single horizontal strip.
The selected label goes **semibold + primary** while the rest stay regular + secondary, so the selection survives a
colour-blind read of the accent border (`WaveePicker.cs:84-94`).

### 6.5 Appearance preferences — what the user can change (Settings ▸ Theme / Lists / Sidebar / Lyrics / Now playing)

| row | control | loc key | writes | live edge |
|---|---|---|---|---|
| *(the tab's own header)* | `SettingsSectionHeader` | `settings.appearance.title` "Theme" / `.subtitle` "Theme, zoom and shell effects" | — | — |
| Theme | `SelectorBar` System / Light / Dark | `settings.appearance.theme` / `.themeSub`; choices `settings.choice.{system,light,dark}` | `WaveeSettings.ThemeMode` (0 System · 1 Light · 2 Dark) via `WaveeTheme.ApplyThemeMode` | `Tok.Use(…)` → `Tok.Epoch` → `Reconciler.RethemeAll()`; System also re-reads the OS accent ramp (`SystemAccentRamp()` first, `SystemAccent()` as the fallback — `WaveeTheme.cs:31-32`). `requestTheme?.Invoke(250f)` |
| Zoom | `ComboBox`, width 160, `Auto (N %)` head + 12 ladder rungs | `settings.appearance.zoom` / `.zoomSub` / `.zoomAuto` | `WaveeSettings.ZoomMode` + `ZoomLevel`; `FluentApp.SetZoom` immediately | live, no restart. Index 0 = Auto applies this render's `autoSuggested` |
| Marquee text | `ToggleSwitch` | `settings.appearance.marquee` / `.marqueeSub` | `WaveeSettings.MarqueeEnabled` | `AppearancePrefs.Bump()` |
| Color washes | `ToggleSwitch` | `settings.appearance.colorWashes` / `.colorWashesSub` ("Tint the shell and page surfaces from artwork") | `WaveeSettings.ColorWashesEnabled` | `AppearancePrefs.Bump()` → every tone plane / wash / tint binder takes `disabled = true` and paints nothing |
| Row density | `SettingsExpander`, collapsed, header reports the answer; body = `WaveePicker.Strip` of 4 `Tile` cards | `settings.appearance.rowDensity` / `.rowDensitySub`; choices `settings.choice.{compact,default,cozy,comfortable}` | `WaveeSettings.RowDensity` | `AppearancePrefs.Bump()` |
| Always hide track artwork — **an ITEM inside the Row-density expander's body, not a top-level row** (`SettingsPage.Appearance.cs:421-423`) | `CheckBox` (MinWidth/MinHeight `Spacing.XXXL` 32) | `settings.appearance.hideTrackArtwork` / `.hideTrackArtworkSub` | `WaveeSettings.HideTrackArtwork` | `AppearancePrefs.Bump()` |
| Track list style | expander + 2 `Tile` cards (Modern / Classic wireframes) | `settings.appearance.trackListStyle`; `.trackListModern` / `.trackListClassic` | `WaveeSettings.TrackRowStyle` | `AppearancePrefs.Bump()` |
| Track page layout | expander + 2 `Tile` cards (Automatic / Hero) | `settings.appearance.pageLayout`; `settings.choice.automatic` / `.hero` | `WaveeSettings.DetailPageLayout` | `DetailHeroPrefs.Bump()` |
| Keep left-rail same size | `ToggleSwitch` (sub-row, Automatic only) | `settings.appearance.railUniform` | `WaveeSettings.DetailRailUniform` | `DetailHeroPrefs.Bump()` |
| Clear all remembered sizes | `Button.Standard`, enabled only when a rail pref moved; confirm dialog | `settings.appearance.railReset*` | four per-scope width/collapsed pairs | `DetailHeroPrefs.Bump()` |
| Sidebar design | expander + `SidebarDesignPicker.Row(compact: true)` | `settings.sidebar.designShort` / `.designSub` | via `SidebarPreferences` | the card subscribes `prefs.Design` directly |
| Lyrics second line | `SelectorBar` Off / Translation / Romanization | `settings.appearance.lyricsSecondary` / `.lyricsSecondarySub`; choices `settings.choice.{off,translation,romanization}` | `WaveeSettings.LyricsSecondaryLine` | `LyricsPrefs.Bump()` |
| Animated lyrics backdrop | `ToggleSwitch` | `settings.appearance.lyricsBackdrop` | `WaveeSettings.LyricsAnimatedBackdrop` | `AppearancePrefs.Bump()` |
| Lyrics blur | `Slider` 0–100, step 1, ticks 25, thumb tooltip "N %", length 180, + an "Auto" `HyperlinkButton` shown only while pinned | `settings.appearance.lyricsBlur` / `.lyricsBlurSub` / `.lyricsBlurAuto` | `WaveeSettings.LyricsBlurStrength` (−1 = Auto) | `LyricsPrefs.Bump()` |
| Hero (Now playing) | `SelectorBar` Cover / \<style short label\> | `settings.appearance.npvPresentation` | `NpvPlayerPrefs.SetPresentation` | `NpvPlayerPrefs.Epoch` |
| Player style | `ComboBox`, width 180, 12 presets in catalog order | `settings.appearance.npvStyle` | `NpvPlayerPrefs.SetStyle` | `NpvPlayerPrefs.Epoch` |

Every row also carries a per-row glyph, `SettingsGlyphs.Row(SettingsTab.Appearance, <key>)` — see W17.
`ZoomAutoMode.Dense` (floor 0.75) is BUILT but **unreachable from this tab**: the ComboBox writes Auto at index 0
and Manual at every other index, and nothing else writes the mode. If 0.3 wants a "Dense" choice it has to add a
control; the policy already supports it (`ZoomAutoPolicy.cs:10, 75`).

Two structural rules the Appearance tab encodes (`SettingsPage.Appearance.cs:14-30`):
1. **A picker goes in an expander BODY, never an expander HEADER.** The header content slot lands in a
   `SettingsCard`'s right-hand Auto grid track, which starves the header-text track toward zero; a zero-width text run
   neither wraps nor clips, so the header paints straight over the content.
2. **Collapsed by default.** Each header carries the current answer via `SettingsValueTag`.

**Gone on purpose** and not to be reinstated: the palette picker (Settings + profile menu), Mica Alt, and
"Limit page color to the hero". `WaveeColors`'s `PresetSwatch` tombstone (`WaveeTokens.cs:280-283`) records the first;
`WaveeTheme`'s class doc (`:7-10`) records why.

### 6.6 Zoom chords (the design system's keyboard surface)

`WaveeShell.ZoomStep(dir)` is the one verb; eight `KeyAccelerator` statics ride zero-size, hit-test-free boxes:
Ctrl+= · Ctrl+Shift+= · Ctrl+Numpad+ (in), Ctrl+− · Ctrl+Shift+− · Ctrl+Numpad− (out), Ctrl+0 · Ctrl+Numpad0 (reset).
`InputHooks.ZoomWheel` handles Ctrl+wheel (a single slot, not an event; the cleanup restores only if still ours).
Palette verbs `settings.zoomIn` / `zoomOut` / `zoomReset` (`Strings.Settings.Appearance.ZoomIn/ZoomOut/ZoomReset`,
glyphs `Icons.Add` / `Icons.Remove` / `Icons.Undo` — the bundled Fluent set has no Zoom glyph).
Zoom is deliberately DISCRETE: the glyph-atlas raster keys quantize the effective scale at ×100, so a free-form
slider would alias distinct zooms onto one raster bucket or churn the atlas per drag-pixel
(`app-zoom-implementation.md`).

### 6.7 Accessibility names / automation

- Media pill, icon arm, standard icon button, picker card: `AutomationRole.Button` / radio come from the stock
  engine controls — do NOT re-declare a role on the wrapper (`WaveePicker.Card` deliberately carries no
  `Role`/`Focusable`/`OnClick`: inside `Strip` the `RadioButtons` item root owns all three, and a second radio role
  would announce the card twice — `WaveePicker.cs:59-62`).
- `WaveeCta.TextAction` declares `Role = AutomationRole.Button`, `Focusable = true` itself, because it is a bare Box.
- `Surfaces.AccentRule`, every wash, every tone plane and the tint binder's 0 × 0 box all carry
  `HitTestVisible = false`.
- **No caps transform on any localized string, anywhere.** `.ToUpper()` mangles Turkish dotted i, expands German ß,
  and shouts a user's own display name back at them. The `Eyebrow` alias takes the string's OWN casing and no call
  site may caps-transform it (`WaveeType.cs:43-47`).

### 6.8 Focus — the ring, the tab stop, and restoration

This chapter owns the focus VISUAL and the four rules below, because it owns the token layer. Every page
chapter's §6 "Focus & accessibility" paragraph cites this section and states only what its own surface ADDS (a modal
trap, an automation name, a Narrator announcement). Nothing else may restate the ring's numbers.

**1. The ring is the engine's, and it is keyboard-only.** No app node paints a focus visual. The recorder emits
WinUI's dual stroke — a 2-DIP PRIMARY band (`Tok.FocusOuter`) hugging the inside of the focus rect's edge and a 1-DIP
SECONDARY band (`Tok.FocusInner`) immediately inside it, both centre-line SDF strokes with radii grown by the smaller
adjacent expansion so the pair stays concentric with the control's corner (`..\fluent-gpu`
`Render/SceneRecorder.cs:3340-3369`; thickness `Dsl/Tokens.cs:455-456`; colours §3.1). It paints only while
`NodeFlags.FocusVisual` is set, which the dispatcher sets for Tab/arrow moves and explicitly NOT for pointer focus
(`Foundation/NodeFlags.cs:51`, `Input/InputDispatcher.cs:3677, 3763`). The app-side seam is
`InputHooks.FocusNode(handle, visual)`: **`visual: true` = a keyboard landing WITH a ring** (a rove, a restore the
user drove from the keyboard), **`visual: false` = a pointer-style landing with no ring** (opening an editor, claiming
a surface on mount). Pick the wrong one and either a mouse user gets a ring they did not ask for, or a keyboard user
loses the caret's location. In 0.2.9 the split is: `true` for the search magnifier restore (`DetailTracks.cs:2149,
2266`), the omnibar (`MergedChromeRow.cs:263`), the narrow sidebar search and its close (`LibraryV3Search.cs:101,
154`); `false` for the inline-edit field (`PlaylistInlineEdit.cs:272` — caret at end, no select-all), the track
table's search field (`DetailTracks.cs:4173`), fullscreen video (`VideoFullscreenSurface.cs:137, 178`) and immersive
lyrics (`ImmersiveLyricsSurface.cs:157, 204, 291`).

**2. `FocusVisualMargin` is the one knob, and it has a DEFAULT that is wrong for bare boxes.** `Element.FocusVisualMargin`
is `Edges4?`, and **null means the WinUI template default of −3 all around** (`Dsl/Element.cs:296`) — not "no ring".
So a **stock control** correctly declares nothing and inherits its template value (Button/IconButton/HyperlinkButton/
RepeatButton/ToggleButton −3, `Button.cs:102`; CheckBox/RadioButton/ToggleSwitch −7,−3), while a **bare `BoxEl` with
`Focusable = true` that declares nothing silently gets a ring 3 DIP outside itself**, over its neighbour or, on
`MediaCard`'s cover FAB (`MediaCard.cs:1231`), over the artwork. Hence the two working arms: `(2,2,2,2)` on a
card/tile/chip, `(1,1,1,1)` at row scale. Past those, an override exists only where the ring would collide with a
border the node does not own — `WaveePicker`'s glyph-less radio `All(2)` (`WaveePicker.cs:236-245`),
`PlayerStyleFlyout`'s `All(−2)` (`:101, 182`) — or where a bare box is deliberately wearing the STOCK look
(`WaveeEqualizerCurve`'s `All(−3)`, `:144`, a `Role = Slider` on a bordered card). All 36 explicit call sites are
censused in **W18**; **a sixth arm is a regression** unless it is added there with its reason.

**3. Roving tab index: a virtualized list is ONE tab stop; a standalone tile is its own.** These are two different
shapes and mixing them breaks both.

| shape | tab stops | item declares | who owns the arrows | file:line |
|---|---|---|---|---|
| Virtualized `ItemsView` list / grid (detail track table, library lists, sidebar pane, recents flat list, logs) | **one**, on the list | `Focusable = false` + `FocusVisualMargin` `All(1)` — the ring draws on the ROVED container, not on the tab stop | the `ItemsView` roving effect (arrows/Home/End/PageUp-Down/Space/Enter/Ctrl+A/typeahead) | `DetailTracks.cs:3534-3535` ("the ItemsView roving effect owns the single tab stop"), `TrackRow.cs:547-548`, engine `ItemsViewPresets.cs:17, 292` |
| `RadioButtons` strip (every `WaveePicker`) | **one**, landing on the current value; selection follows focus | nothing — the item root owns role, focus and click; the `Card` deliberately declares none (§6.7) | `RadioButtons`, via `MoveFocusVisual ?? RestoreFocus` | `WaveePicker.cs:59-62, 244`; engine `RadioButtons.cs:118` |
| Non-virtualized tile / chip / card (Browse tiles, artist shelf cards, concert cards, month board, filter chips, LibraryV3 chips) | **one per tile** | `Focusable = true` + `FocusVisualMargin (2,2,2,2)` | Tab order alone — there is no items host to rove | `BrowseTiles.cs:38, 73, 101, 172`, `ArtistPage.Shelves.cs:34, 116`, `ConcertUi.cs:94, 142, 172, 469, 751`, `MonthBoard.cs:156, 202`, `ContentFilterChips.cs:83` |
| `MediaCard` shelf/proto card | **zero on the card body** — only its cover FAB is focusable, and it declares no margin, so it inherits −3 and rings OVER the artwork | — | — | `MediaCard.cs:1231` (the only `Focusable` in the file). Recorded as 0.2.9 behaviour, not endorsed: a home shelf is reachable by pointer only |
| Hand-rolled chip strip (Library V3) | **one**, with its own key handler | a key on a node map; the strip moves the visual itself | the strip: `MoveFocusVisual ?? RestoreFocus` | `LibraryV3Chips.cs:335-347` |

**The failure this rule prevents:** a `Focusable = true` item inside an `ItemsView` gives the list N tab stops instead
of one and the arrow keys stop reaching the roving effect — a 50-row track table then costs 50 Tabs to cross.

**4. Focus restoration, every moment that moves it.** "Automatic" means a platform seam already does it and a surface
must not re-implement it.

| moment | who restores | where focus lands | file:line |
|---|---|---|---|
| Dialog / flyout / context menu / teaching tip CLOSES | **automatic** — `OverlayHost`, per level | the node saved when the overlay opened — but **only** if focus still lives inside the dying overlay's subtree (or is dead/null) AND the saved node is still live. A popup that never took focus must not yank it from elsewhere (WinUI `CPopup::Close`) | engine `OverlayHost.cs:468-490` |
| The same close, focus TRAP | **automatic** | the trap lifts at close START (the WinUI `DialogShowing` -> `DialogHidden` Tab-cycle teardown), so the restore above can land OUTSIDE the dying overlay | `OverlayHost.cs:470-477` |
| Drawer / fullscreen video OPENS | the surface | `PushFocusScope(root)` (Tab can no longer walk out into the now-adjacent shell chrome) then `FirstFocusableIn(videoArea)`, falling back to the root — and only when `UserInitiated` | `VideoFullscreenSurface.cs:129-138` |
| ...and CLOSES | the same surface, in the layout effect's teardown | `PopFocusScope(root)` then `RestoreFocus(priorFocus)`. A stale handle is harmless (the dispatcher drops a dead node); never restore to the surface being torn down | `VideoFullscreenSurface.cs:141-147` |
| Immersive lyrics mounts / is clicked | the surface | its own root, `visual: false`. A later re-entry claims focus **only when `GetFocus` is null** — anything the user has already focused (the player's play button, the caption field) wins | `ImmersiveLyricsSurface.cs:157, 199-204, 291` |
| An inline editor opens (playlist title/description, track-table search, narrow sidebar search) | the field's `TemplateParts` at `OnRealized`, deferred one pump | the EDITABLE node via `FirstFocusableIn` (never chrome that cannot type), `visual: false` so the caret lands at the end with no select-all | `PlaylistInlineEdit.cs:265-274`, `DetailTracks.cs:4170-4174`, `LibraryV3Search.cs:98-101`, `MergedChromeRow.cs:258-263` |
| ...and collapses | the owning page, latched over the remount | back to the affordance that opened it — the magnifier button — with `visual: true`, because the user got there from the keyboard | `DetailTracks.cs:2143-2150, 2266`; `LibraryV3Search.cs:154` |
| A keyboard rove inside a strip | the strip | `MoveFocusVisual ?? RestoreFocus` — `MoveFocusVisual` keeps the ring, the fallback does not | `LibraryV3Chips.cs:345`; engine `RadioButtons.cs:118`, `SelectorBar.cs:86`, `Segmented.cs:138`, `PipsPager.cs:139`, `BreadcrumbBar.cs:73` |
| **Page navigation (a route swap in `ContentHost`)** | **NOBODY — 0.2.9 has no answer.** `ContentHost.cs` contains no focus code at all; when the outgoing page's focused node dies the dispatcher drops focus to null and the next Tab restarts from the top of the shell | **an open decision for 0.3**, not a behaviour to copy blindly. The 0.2.9 behaviour is the baseline to measure first (§10 item 73) | `ContentHost.cs` (no `Focus*` occurrence) |
| **KeepAlive reactivation (a parked tab/page comes back)** | **NOBODY** — no `UseActivation` arm anywhere in the app touches focus; the page's nodes re-realize with no focus claim | same open decision; note that the material binder DOES re-claim on reactivation (`CoverPaletteLeaves.cs:223-264`), so the precedent for an activation-time claim exists | — |

Both blanks are deliberate to record, not to invent: writing a focus landing into `ContentHost` changes keyboard
behaviour app-wide, which is a product decision for the 0.3 owner and must be made once, here, rather than five times
in five page chapters.

---

## 7. Data & readiness in 0.3 terms

This chapter's surface reads almost nothing from the entity model — it is tokens and recipes. The two exceptions are
the **cover-colour plane** (an image-keyed side table) and the **appearance settings store**.

| visual element | 0.2.9 data source | 0.3 read | readiness predicate |
|---|---|---|---|
| Every type alias, spacing/radius/size value | compile-time consts | compile-time consts in `Platform/Design.cs` | always ready |
| Every `Tok.*` colour | engine `Tok`, live theme | unchanged (engine) | always ready |
| Art placeholder tint | `CoverColorPlane.TryGetTint(url, lightTheme)` | **unchanged** — an image-keyed side table, NOT an entity column. Key = the `spotify:image:<id>` identity of `Track.Image` / `Album.Image` (a `StringId` column) | a MISS is a first-class state: paint the neutral tile and enqueue. Never a skeleton, never a hole |
| Page tone / hero wash / artist blend / shell tint | `CoverColorPlane.TryGetScheme(url, lightTheme)` via `Surfaces.SchemeFor` / `ChromeSchemeFor` | unchanged | null scheme ⇒ paint **nothing** (the page keeps its neutral surface). The plane's arrival then cross-fades in over 250 ms |
| Whether a cover exists at all | `Image?.Url` / `Image?.MosaicTiles` on the 0.2.9 record | `handle.Knows(XFields.Image)` + `handle.ImageId` (`StringId`); mosaic tiles come from the playlist's first four `PlaylistTracks` edge targets' album images | **`Knows(Image)` must be true before the slot decides between `Artwork` and `Mosaic`** — a slot that flips from mosaic to single cover mid-reveal is the "sections popping in" regression |
| Row density / track style / page layout / washes on-off / zoom / theme | `IAppSettings` + `AppearancePrefs.Epoch` | unchanged — `Platform.Settings` + one `Signal<int>` epoch per concern | the store is the truth; the epoch is only the update EDGE (`_ = Epoch.Value; return settings.Get(key);`) |
| The published shell material | `Signal<ShellMaterialState>` at the shell root, claimed by the active page | unchanged — `Shell.Material` signal + `ShellTintOwnership.Resolve` | a claim whose colour is not graded yet writes `WriteHeldColor`: the chrome never dips to neutral between two coloured pages |
| Camelot key dot | `Track.CamelotColor` (uint ARGB) | `TrackTable.CamelotColor` column, `TrackFields.Audio` group | `Knows(TrackFields.Audio)`; else no dot at all (not a grey dot) |
| Zoom's `baseDip` | `Viewport.Size × Viewport.Zoom` (engine contexts) | unchanged | always ready; a degenerate read returns `NeutralZoom 1f` |

### DATA GAPS

Everything this surface shows that the plan's §4 data model does not hold:

| what | 0.2.9 source | proposal for 0.3 |
|---|---|---|
| **Cover palette (5 roles × light/dark per image)** — the input to *every* art-derived colour in the app | `SpotifyLive/CoverColorPlane.cs`: extension kind **179 VISUAL_IDENTITY_TRAIT** (dark only) + the `getDynamicColorsByUris` pathfinder query; persisted to `<LocalCache>/cover-colors.json` with a 180-day hit TTL / 7-day miss TTL | **Not an entity column** — keep it image-keyed. Port as `Entities/Palette.cs`: a `Dictionary<StringId, PaletteEntry>` where the key is the interned `spotify:image:<id>`, `PaletteEntry` is 10 `uint`s + flags + `int Ts` (unmanaged ⇒ it can be a `Column<PaletteEntry>` over a small side table). Persist in the SQLite store as `CREATE TABLE palette(image TEXT PRIMARY KEY, dark BLOB, light BLOB, flags INT, ts INT) WITHOUT ROWID` rather than a second JSON file. `Spotify.Decode.VisualIdentity(k179)` already appears in plan §4.6 — it must write HERE, and `Spotify.Api` needs a `GetDynamicColorsByUris(uris)` function. **The plan does not mention the palette at all.** |
| The demand-driven enqueue (`TryGetTint` enqueues on a miss) | `CoverColorPlane.EnqueueLocked` + a 120 ms debounced pump, 50 uris/batch | This is a *fetch* concern and belongs in `Entities/Fetch.cs` as a fifth request shape — but it is triggered from the PAINT path, not from a page's `Ensure`. Keep the 120 ms debounce: a grid realize produces dozens of misses in one frame and must coalesce into ONE batch. Do NOT route it through the page-demands-its-whole-model rule; an image-keyed colour is not part of a page's model. |
| `BestFitIsLight` (which half the server thinks suits the cover) | `CoverColorPlane.Entry.BestFitIsLight`, persisted as `"bfl"` | keep as a flag bit on `PaletteEntry`. Currently read by nothing in this layer, but it is on the wire and cheap. |
| **Artist visual identity / gallery images** | the artist page's full-bleed photography comes from `ArtistOverview`'s `visuals` (header image + gallery) | plan §4.6 lists `ArtistV4` and `ArtistOverview` but §4.2's field table is Track-only. `ArtistTable` needs `Column<StringId> HeaderImage, GalleryFirst` and an `EdgeTable<NoEdge> ArtistGallery` — see `08-artist-and-discography.md` |
| **Release facts** (label, ℗ line, copyright) | `Spotify.Decode.Publishing(k183)` | plan §4.2 has `TrackFields.Publishing` as a bit but no columns; `AlbumTable` needs `Column<StringId> Label, Copyright, PLine` |
| **Chart deltas** | `PlaylistTrackEdge(… ChartStatus, ChartPos, ChartPrev)` | **already in the plan** (§4.3) — good |
| `PageAccent` (a page's live, viewport-following ambient accent) | `WaveeAccentCtx.Slot`, published by `RecentsPage` only | port as a `Context<IReadSignal<PageAccent>?>` verbatim. No model change. |
| The zoom design box's second number | `DesignH = 900` is `DetailShell`'s tall-hero gate, with **no `WaveeSize` twin** | `ZoomAutoPolicy.cs:22-23` says so explicitly: this literal IS its source of truth. In 0.3, promote it: `Design.Size.DesignH = 900` and have `DetailShell` read it, so `ZoomAutoPolicyTests` can cross-check BOTH constants instead of only `PageMaxW`. |

---

## 8. Pure rules to port verbatim

These encode the look as decisions, not as pixels. **Port them; never re-derive them.**

| class | 0.2.9 file | decides | tests | 0.3 destination |
|---|---|---|---|---|
| `WaveeType` | `Design/WaveeType.cs` | every size/line-height/weight/tracking triple in the app; the six sanctioned weight divergences | `Wavee.Tests/DesignTokenConvergenceTests.cs:36-117` (9 on-ramp aliases × 3 gates + 3 display-face pins) | `Platform/Design.cs` CORE — `Design.Type` |
| `WaveeSize` / `PlayerDock` | `Design/WaveeTokens.cs:54-84` | the fixed dimension ladder, the thumbnail rungs, the section rhythm, `PageMaxW` | `DesignTokenConvergenceTests.cs:206-230` | `Platform/Design.cs` CORE — `Design.Size` |
| `WaveeAccent` | `Design/WaveeTokens.cs:39-51` | the three accent roles and the two hard rules | `VoiceUnificationTests.cs:108` | `Platform/Design.cs` CORE |
| `WaveeColors` | `Design/WaveeTokens.cs:107-297` | the opaque/layer ladder arithmetic, the row ladder, the selection ladder | `ShellMergedRungTests.cs` (11 facts, incl. the `Over(ContentLayer, ShellGround) == ContentSurface` identity at `:177`), `VoiceUnificationTests.cs:61-108`, `LightModeOverhaulTests.cs:118-204` | `Platform/Design.cs` CORE — **properties, never fields** |
| `WaveeOnMedia` | `Design/WaveeOnMedia.cs` | the ONE on-media ladder; which rungs the engine owns | `DesignTokenConvergenceTests.cs:119-199` (6 facts) | `Platform/Design.cs` CORE |
| `StageArm` | `Design/StageArm.cs` | the immersive stage's polarity, as a **pure function of `ThemeKind`** | `StageLayoutTests.cs` (585 lines) | `Platform/Design.cs` CORE — keep the pure `For(theme)` entry point so value tests can drive both arms without mutating global theme state |
| `WaveePalette` | `Design/WaveePalette.cs` | `Lift` · `Vivid` · `Hairline` (22-iteration bisection) · `TextInk` · `ChromeAccent` · `Accent`'s role preference · `PageTone`'s clamp · `DataDotInk`'s hue-band rungs · `ToHsl`/`FromHsl` | `DetailPageToneTests.cs` (8 facts), `LightModeOverhaulTests.cs:46-105` (6 facts on `DataDotInk`), `CoverColorPlaneTests.cs` (487 lines) | `Platform/Design.cs` CORE — `Design.Palette`. Every function must keep its **pure overload** (theme + background passed in) so tests drive both themes without touching `Tok.Theme` |
| `WaveeMotion` + `ScaleTier` | `Design/WaveeMotion.cs:30-76, 163-190` | three scale tiers, three duration rungs, two stagger rungs, the reduced-motion collapse | `MotionSystemTests.cs` (7 gates) | `Platform/Design.cs` CORE |
| `WaveeEntrance` | `Design/WaveeMotion.cs:102-127` | the capped stagger ladder and the entrance terminal | `EntranceStaggerTests.cs` (6 facts) | `Platform/Design.cs` CORE |
| `HoverMotionGate` | `Design/WaveeMotion.cs:141-159` | "has real pointer input happened since mount" | `HoverMotionGateTests.cs` (6 facts) | `Platform/Design.cs` CORE |
| `PageNavMotion` | `Features/Shell/PageNavMotion.cs` | the page-slot key and all five swap recipes | **`ContentHostPageTransitionTests.cs` — 17 facts, 272 lines.** Pins: direction is not in the slot key (`:48`), every search query owns a slot (`:76`), every direction has an ACTIVE Enter *and* Exit (`:106`), the fade-through is exit-leads-enter-follows (`:139`), no recipe blurs the page root (`:155`), the masthead fade shares the page exit window (`:167`), **no video-safe recipe touches Opacity** (`:178`), the video-safe pair is a symmetric translate with an active Exit (`:193`), Neutral has no video-safe form (`:221`), the exit is ~94 % gone before enter starts (`:226`), the exit uses `EaseOut` not `FluentAccelerate` (`:244`), and the slides use `DistBase` not `DistLarge` (`:256`) | `Platform/Design.cs` CORE. The route→video-safe CLASSIFICATION stays in the shell (`ContentHost.cs:147-152`) — the motion file source-includes into `Wavee.Tests`, which cannot compile module pages |
| `ShellWashGeometry` | `Features/Shell/ShellWashGeometry.cs` | the three window ellipses → clipped boxes; the per-theme alphas | `ShellWashGeometryTests.cs` (6 facts, incl. a node-relative → window round-trip at `:39`) | `Platform/Design.cs` CORE |
| `ShellTintOwnership` | `SpotifyLive/CoverColorPlane.cs:516-556` | the material hand-over protocol | *(covered by the shell tint tests)* | `Platform/Design.cs` CORE |
| `DetailRevealRamp` | `Features/Detail/DetailRevealRamp.cs` | Chunk 12 / Cap 60 / Done | `DetailRevealRampTests.cs` (4 facts) | `Platform/Design.cs` CORE |
| `ZoomAutoPolicy` | `App/ZoomAutoPolicy.cs` | the display→zoom derivation, the plateau set, the one-shot mode migration | `ZoomAutoPolicyTests.cs` (14 facts, incl. DPI-independence, monotonicity, idempotence, and a cross-check that every plateau is a real `ZoomLadder` rung) | `Platform/Platform.cs` CORE — **must stay BCL-only** so it source-includes cleanly |
| `ImageDecodeScale` | `Design/Surfaces.cs:21-44` | DIP edge × scale → bucketed, clamped device-pixel decode edge | *(no dedicated test — see §9)* | `Platform/Design.cs` CORE |
| `DetailTrackTableRules.PreviewScale` (0.25) + `ArtSizeFor` | `Features/Detail/DetailTrackTableRules.cs:31, 53-56` | the picker miniature's compression factor and the real row's art edge — so "preview mirrors the real row" is a tested decision, not a private constant | `TrackRowStyleRulesTests.cs:105-120` | `04-detail-track-table.md` owns these; this chapter's `Picker.DensityRows` READS them |
| `MorphKeys.For` | `Components/MorphKeys.cs` | `"album:" + id` / `"pl:" + id` / null | *(none)* | `Platform/Design.cs` CORE |
| `FrameTime` | `Features/Player/FrameTime.cs` | the one motion clock | *(none — but every clock consumer's tests depend on it)* | `Platform/Design.cs` CORE |
| `SearchHighlight.LineBoxFor` | `Design/SearchHighlight.cs:75-80` | 14 → 20, 12 → 16, else `ceil(size × 1.43)` | *(none)* | `Platform/Controls.cs` |
| `WaveeCta.Palette` + `OnFillSecondaryAlpha` | `Design/WaveeCta.cs:225-242` | the accent CTA's whole colour ramp: fill / @0.90 / @0.80 / `AccentDisabled`; ink / ink / ink@(0x80 if the ink is dark else 0xB3) / `TextOnAccentDisabled`; border rest+hover / transparent pressed+disabled; `OuterBorderEdge` sizing. The pressed-ink alpha is a LUMINANCE decision, not a theme one | *(none — pin it in 0.3: it is four literals and one branch, and it is every media CTA in the app)* | `Platform/Controls.cs` |
| `WaveePicker.Ink.For` + `Shell` | `Design/WaveePicker.cs:30-56` | the five card footprints and the selected/unselected Block/Faint accent pair | *(none)* | `Platform/Controls.cs` |
| `ShellWashGeometry.Resolve` | already listed above | — also the ANCHOR rule (`x0 > 0` ⇒ right, `y0 > 0` ⇒ bottom; a full-span axis takes leading) | `ShellWashGeometryTests.cs` | — |
| `Surfaces.ArtistHeroVeil` stops | `Design/Surfaces.cs:173-192` | the two four-stop veils and the light/dark `pull` (0.16 / 0.24) — four stops is the RECORDER limit, so this is a hard ceiling, not a preference | *(none)* | `Platform/Controls.cs` |
| `Surfaces.SectionBand` / `HomeHeroBackdrop` | `Design/Surfaces.cs:416-445` | the one banded-section gradient: `Lerp(FillCardDefault, accent, dark .10 / light .06)` holding the card's α → card at 0.45 → card at 1, and **no border** | *(none)* | `Platform/Controls.cs` |

---

## 9. Re-author notes

### 9.1 What must not be simplified

1. **Every `WaveeColors` / `WaveeOnMedia` / `StageInk` member is a `get =>` property, not a `static readonly` field.**
   `Tok.*` are live theme reads; a field captures the palette at type-init and never re-themes. The literal
   on-media values (`ScrimHover`, `ScrimPressed`, `CoverScrim`, `Stroke`, `BackdropDim`, `Spotlight*`) are the only
   sanctioned `static readonly`s here, because they are theme-invariant by contract.
2. **The two grading halves stay two functions.** Collapsing `SchemeFor` and `ChromeSchemeFor` into one is invisible
   in dark and glaring in light.
3. **The 22-iteration bisection in `Hairline` stays 22 iterations.** It is the contrast solve; a cheaper approximation
   changes every identity hairline and every accent-ink digit in the app.
4. **The clamp constants are not tuning knobs.** `PageToneDarkL 0.15 / SMax 0.30`, `PageToneLightL 0.94 / SMax 0.16`,
   `PlaneAlphaDark 0.20 / Light 0.30`. If a page should take more colour, raise the **alpha pair**, never the clamp
   (`CoverPaletteLeaves.cs:136-138`).
5. **`NeutralGround` is `ShellGround @ 0.03`, not `Transparent`.** See §4.3 — this is a correctness fix disguised as
   a constant.
6. **The `WaveeEntrance` recipe never changes SHAPE under reduced motion.** It returns the same
   `LayoutTransition` with `DelayMs = 0`. Gating an entrance HOOK on `Motion.ReducedMotion` changes the hook COUNT
   between renders and crashes the reconciler the moment the flag flips mid-session — a resize grip flips it
   (`WaveeMotion.cs:86-92`).
7. **`Exit.Active = true` on every page recipe.** A stripped Exit detaches the outgoing page in the same frame and
   the content card flashes EMPTY.
8. **The wash host's 72-DIP bottom inset is a MARGIN, not a re-anchored ellipse.**
   `ShellWashPlacement.Center`/`Radius` are node-relative CONSTANTS precisely because the box is a fraction of the
   window; subtracting a fixed 72 from the box HEIGHT makes those two ratios viewport-dependent and they go stale on
   the next resize (`ShellMaterialLayer.cs:65-69`).
9. **`Surfaces.SectionHeader`'s title carries `Grow = 1f` and NO `Basis`.** `Basis = 0f` collapsed every header
   inside a `PagedShelf` to a single ellipsised letter (`Surfaces.cs:374-381`).
10. **`Surfaces.AccentRule` carries `AlignSelf = FlexAlign.Start` explicitly.** In the header's COLUMN the cross axis
    is horizontal and a stretched rule would run the full section width instead of being a 20-DIP mark.

### 9.2 Traps

| trap | symptom | fix |
|---|---|---|
| `Surfaces.PlaceholderFor(url)` inlined into a record field | every sidebar/pin thumb (< 80 DIP) and every `ArtworkFill` grid cell stays grey forever despite the plane grading its cover | use `WatchedPlaceholder(url)` — a `Prop.Of` that reads `Watch(url).Value`, so the PAINT path re-evaluates (`Surfaces.cs:94-111`) |
| `CoverShimmer` without a decode-bucket Key | a virtualized card that rebinds to a new cover, or the SAME cover at a new decode target, keeps calling `UseImage` against its FIRST handle; the breathe/settle never tracks the size the real `Image` asked for | `Key = "shim:" + url + ":" + dw + "x" + dh` (`Surfaces.cs:228`) |
| Reading `CoverColorPlane.Watch` / `Epoch` at PAGE scope | a graded batch re-renders the whole Artist/Detail/Home tree, including every shelf in it | the subscription lives in a LEAF (`CoverPaletteLeaves`), and the paint-only tint lives in a bound `Fill` |
| A `ColorF` frozen into a component ctor | a live theme flip leaves the component on the old palette | resolve `Tok.*` / `StageInk.*` at the point of CONSUMPTION (`StageArm.cs:14-18`) |
| Two stacked plates for a hovered striped row | a row painting two `Fill`s | compose: `ColorContrast.Over(hover, zebra)` — `Over` is associative, so the merged rung composites pixel-identically |
| `Skeletonized(true)` on the shimmer component | inside a `Skel.Region`'s derived skeleton the opaque component maps to the deriver's default bar — a stray stripe inside the cover | `.Skeletonized(false)` (`Surfaces.cs:229`), letting the paired `Image`'s derived placeholder BE the cover square |
| A `PressScale` on a near-full-width row | the row visibly shrinks and springs back, blurring the title mid-scale | `PressedFill` alone (`WaveeMotion.cs:36-38`) |
| Compounding the capsule scale with the glyph scale on `WaveeCta.Icon` | a 1.12 hover | `IconHoverScale = IconPressScale = 1` |
| A `ReuseGuard` trip on any Design component | a parent re-render handed a new factory to a reused instance | these four Components take `Props` records through `Embed.Comp(props, factory)` + `UseProps<T>()`; `CoverShimmer` is the exception and pays with a Key |
| A trailing `Grow = 1` spacer left on a WRAPPING `SearchHighlight.Row` | flex hands the spacer the rest of line 1 and every following run breaks early | the spacer is added only when `!wrap` (`SearchHighlight.cs:59`); the wrap arm also drops `ClipToBounds`/`Basis 0` and bounds itself with `MaxHeight` instead |
| `ClipToBounds` left OFF a `WaveePicker.Card` | a miniature that outgrows its footprint paints over the neighbouring card — the overlapping-Sidebar-header failure | `ClipToBounds = true` on the shell (`WaveePicker.cs:71`) |
| A picker `Label` at a non-12 `labelSize` | the raw `size + 4` line box leaves the eight-rung ramp silently | keep 12 (the default) unless the divergence is named here, the way `PivotLabel` is |
| `Tok.Theme` left out of the shell-tint effect's `DepKey` | a live theme flip keeps the OLD arm's tint (dark `TintedDark@0.14` on a light shell) until the next navigation | it is in the key (`CoverPaletteLeaves.cs:258`) — keep it there |
| A morph-tagged `Artwork` slot expecting a live tint | the placeholder is the FROZEN `PlaceholderFor(url)`, so a grading that lands after mount never repaints it | only relevant while `MorphKey` is dormant; if 0.3 re-arms Hero, give the morph branch a `WatchedPlaceholder` too (`Surfaces.cs:272`) |

**Zero-allocation scroll frames vs per-row richness — how 0.2.9 reconciled them** (the tension the 0.3 gate measures):

- The scroll path never allocates because every per-row decision is either a **compile-time const** (spacing, radii,
  type metrics) or a **span/struct read**. `WaveePalette.Accent` uses a `Span<uint>` collection expression —
  stack-allocated, so the hottest palette function stays allocation-free on the render path (`WaveePalette.cs:153`).
- Richness lives in **binds**, not re-renders. A landed grading marks PaintDirty on exactly one tile via a
  `Prop.Of` closure allocated ONCE at template time.
- `Surfaces.Shimmer` has an explicit **cheap arm**: below 80 DIP it returns a bare `BoxEl` — no component, no hook
  cells, no image-epoch subscription — so a 50 k-row virtualized list pays nothing per item.
- `CoverShimmer` latches `settled` and **stops calling `UseImage`**, so a loaded cover unsubscribes from the global
  image epoch; without that, one image's status change re-renders every loaded cover in the grid.
- `ColorContrast.Over` composes the row-state ladder at token-resolve time, so a striped hovered row is still ONE
  `Fill` write.

### 9.3 Where plan §2 / §4.12 / §4.13 are wrong or too thin for this surface

1. **§2 gives `Platform/Design.cs` + `Platform/Controls.cs` no line budget of their own.** The `Platform/` folder is
   budgeted at "7 files, ~8 600 lines" across `Platform.cs`, `Platform.Host.cs`, `Modules.cs`, `Modules.UI.cs`,
   `Modules.Host.cs`, `Design.cs`, `Controls.cs`. The 0.2.9 `Design/` folder alone is **2 980 lines**, and
   `Components/` (the source of the rest of `Controls.cs`) is far larger. See §9.4.
2. **§4.12's `Design.*` references are placeholders that do not match anything here.** It names `Design.RowPad`,
   `Design.PlaceholderCover`, `Design.RowTitle`, `Design.RowSub`, `Design.Accent`, `Design.Fg`. The real names are
   `WaveeType.TrackTitle` / `TrackMeta`, `Surfaces.WatchedPlaceholder`, `Tok.TextPrimary`, `Tok.AccentTextPrimary`.
   More seriously: `Design.PlaceholderCover` implies **one** placeholder image. There is no such thing — the
   placeholder is a per-cover TINTED tile resolved through the palette plane, and shipping a single grey asset would
   lose the single most visible thing this layer does (see non-negotiable 9).
3. **§4.12 hardcodes `Height = 56` and `Size = 40` on the row.** Both are density-dependent
   (`RowHeightFor` 40/48/56/64, `ArtSizeFor` 32/32/40/48) and the Classic style has its own ladder. Owner M must read
   `04-detail-track-table.md`, not §4.12.
4. **§4.13's Album wireframe shows a `[cover 232]`.** 232 is on no ladder in this chapter. The real detail-rail
   widths are `RailAlbum 280` / `RailPlaylist 240` and the cover fills the rail's content width.
5. **Neither §4.12 nor §4.13 mentions the shell material at all.** Every detail page must mount a
   `CoverShellTintBinder` (or its 0.3 equivalent) or the window chrome stays neutral while the page is coloured —
   which is the single loudest visual regression available here.
6. **§5's Wave 4 table gives owner L "Design.cs, Controls.cs / Design/*, Components/* minus entity rows/cards" and no
   gate of its own.** The wave gate is `--fake` showing the shell frame. That does not exercise the palette plane,
   the page tone, the washes, the CTA ramp or the pickers. **Add a gate**: `DesignTokenConvergenceTests`,
   `MotionSystemTests`, `ShellMergedRungTests`, `VoiceUnificationTests`, `LightModeOverhaulTests`,
   `DetailPageToneTests`, `ShellWashGeometryTests`, `EntranceStaggerTests`, `HoverMotionGateTests`,
   `ZoomAutoPolicyTests`, `StageLayoutTests`, `CoverColorPlaneTests`, **`ContentHostPageTransitionTests`**,
   `DetailRevealRampTests`, `TrackRowStyleRulesTests` — ~3 400 lines of pure tests that port nearly
   1:1 and are the only executable statement of this chapter. Measured counts, for the port's own gate:
   DesignTokenConvergence 15 · MotionSystem 7 · EntranceStagger 6 · ShellMergedRung 11 · VoiceUnification 6 ·
   LightModeOverhaul 11 · DetailPageTone 8 · ZoomAutoPolicy 14 · ShellWashGeometry 6 · HoverMotionGate 6 ·
   DetailRevealRamp 4 · TrackRowStyleRules 22 · StageLayout 29 · CoverColorPlane 22 · ContentHostPageTransition 17
   = **184 facts**.
7. **§6's migration table does not list the design tests.** They are "pure-decision tests of files that survive as
   core sections" and belong in the **Port** row, not in any Delete row.
8. **§7's risk table has no row for "the rebuilt app is uglier than 0.2.9".** That is the owner's stated fear and the
   reason these chapters exist. Proposed mitigation row: *side-by-side parity capture against the kept 0.2.9 Release
   build (`C:\WAVEE\wavee-0.2.9\…\publish\Wavee.exe`) in `--fake` mode, per chapter's §10 checklist, before Wave 6
   deletes `_old`.*

### 9.4 Line budget

| | lines |
|---|---|
| 0.2.9, this chapter's sources (the 24 files in the header) | **4 276** |
| of which `Design/` alone | 2 980 |
| Plan §2 target: `Platform/Design.cs` + `Platform/Controls.cs` | unstated individually; the whole `Platform/` folder is ~8 600 across 7 files, so **≈2 400 implied** for the pair |
| **Honest estimate** | **`Design.cs` 2 600–3 000** (tokens, type, colour, palette, stage arm, motion, wash geometry, material, the four cover-leaf components, page-nav motion, reveal ramp, frame time, morph keys, image decode scale) + **`Controls.cs` 4 500–6 000** (`Surfaces`, `WaveeCta`, `WaveePicker`, `SearchHighlight` ≈ 1 100 from this chapter, plus every non-entity component from `Components/` — equalizer, save button, rail, selection bar, drag chip, flip countdown, nav preview, …). **`Controls.cs` will pass its budget by far more than 30 %; it needs named partials on day one** — fixed by the 2026-09-12 arbitration as exactly four: **`Controls.cs`, `Controls.Cta.cs`, `Controls.Art.cs`, `Controls.Picker.cs`**. |

**`Controls.cs`'s 4 500–6 000 is the ENVELOPE for the whole file, partials included** (arbitration 2026-09-12).
Three chapters sized this file and gave three answers: this one 4 500–6 000, `02-cards-and-controls.md` 3 200–3 800
for its own material with 01's primitives excluded (and ~4 500–5 500 in its §9 note), and `01-track-row.md` a 900
share "of a ~1 200 file budget" — which is smaller than the shares that have to fit inside it. The decision: this
chapter's envelope stands, and the other two chapters' figures are **shares of it, not additions to it** —
02's 3 200–3 800 (cards, shelves, states, countdowns, face piles, rich text) + 01's 900 (equalizer, save/follow
buttons, selection bar, row swipe, explicit badge, more button, expand chevron, search highlight) + this chapter's
≈ 1 100 (`Surfaces`, `WaveeCta`, `WaveePicker`, `SearchHighlight`) land **inside** 4 500–6 000. Owner L names the
four partials on day one and files each share into one of them; nobody adds a fifth name.

Comment/code ratio is the reason: roughly half of `Design/` is the *reasoning* — three deleted wash recipes, the
alpha ratchet, the accent-role rules, why `textBrightAccent` is not the accent. **That reasoning is the asset.**
Strip it and the next wave re-derives the same four wrong answers. Budget for it.

### 9.5 Files or pages missing from the §2 tree

- **No home for the cover-colour plane.** `CoverColorPlane.cs` (556 lines) is neither an entity, a Spotify wire
  decoder nor a design token. Proposed: `Entities/Palette.cs` (CORE: the table, TTL, key identity) +
  `Entities/Palette.Host.cs` (SHELL: the debounced pump and the filler), with `Spotify.Api.GetDynamicColorsByUris`
  and `Spotify.Decode.VisualIdentity` feeding it.
- **No home for `AppearancePrefs` / `LyricsPrefs` / `DetailHeroPrefs` / `NpvPlayerPrefs`** — four cross-surface
  epoch signals. Proposed: one `Platform/Prefs.cs` section, or a `Platform.Prefs` partial of `Platform.cs`.
- **`WaveeAccentCtx`** (the page-scoped ambient accent context) has no listed home. It belongs in `Design.cs`.
- **`Glyphs.cs`** (the custom `wavee-icons.otf` PUA glyphs + the bundled `SegoeFluentIcons.ttf` path) has no listed
  home. It must be set BEFORE `FluentAppHarness.Run` — `App.cs`'s `Platform.Boot()` is the right slot, but §3.5's
  `Main` does not mention fonts, and the failure mode (tofu for every Fluent-Icons-era glyph on Windows 10) is
  silent.
- **`ZoomAutoPolicy`'s migration hook.** §3.5's `Main` does not call `ZoomAutoPolicy.MigrateMode(settings)`. It must
  run at settings load, before anything reads `appearance.zoom.mode` (`ZoomAutoPolicy.cs:97-100`). The migration is
  one-shot and version-stamped: it pins `ZoomMode = Manual` iff `|stored ZoomLevel − 1| > 0.004`, then writes
  `ZoomModeBootstrapVersion = MigrationTargetVersion 1` (`:106-113`). Both the tolerance and the version key must
  port, or a settings store that was never written and one written at exactly the default become indistinguishable
  and a user who already chose 125 % wakes up at 150 %.

### 9.6 Two drifts to record (code wins)

1. **`WaveeMotion.cs:105-106` says the capped entrance is bounded at "360 ms".** The arithmetic is
   `StaggerCap 8 × StaggerMs 40 = 320 ms`, and `EntranceStaggerTests.cs:52` pins **320**. Port the code; fix the
   comment.
2. **`large-display-scaling.md §3.2` writes `public const float DesignW = WaveeSize.PageMaxW;`** — a direct
   reference. The shipped code keeps it as a **literal** `1600f` because the file must stay BCL-only to source-include
   into `Wavee.Tests`, with `ZoomAutoPolicyTests:116` cross-checking the two (`ZoomAutoPolicy.cs:17-23`). The code
   is right; the doc is the earlier sketch. Same doc also proposes a `LyricsTypeRungs` class and a `MeasureEm` cap
   (§3.3) that **were never built** — treat as design intent for `22-lyrics.md`, not as shipped behaviour.

---

## 10. Parity checklist

Verify each item **side by side** against the kept 0.2.9 Release build:
`C:\WAVEE\wavee-0.2.9\src\apps\Wavee\bin\Release\net10.0\win-arm64\publish\Wavee.exe --fake`
(offline FakeData; no login, no network). Drive routes with `wavee://open?route=<name>&arg=<value>`
(`.claude/skills/wavee/deep-linking.md`). "Static capture" = a screenshot at rest; "hover capture" = a screenshot with
the pointer parked on the named target; "frame recording" = a screen recording frame-diffed against the 0.2.9 one.

**Type**

1. Open Settings ▸ Theme at **1600** wide. Static capture. Every section header, row header and description matches
   0.2.9 in size, weight and line spacing — no row is one pixel taller.
2. Open an album (`route=album:<uri>`) at **1600 × 950** (tall, so the hero is on the TitleLarge rung). The album
   title renders at **40/52** in Segoe UI Variable **Display** with visibly tight tracking. Resize to **1600 × 860**:
   it steps to **28/36**. Two rungs, never an intermediate size.
3. Open an artist page at **1600**. The artist name is the **84/96/700** Display cut; at **900** wide it steps to
   48/60/700; below the compact threshold, 32/40/700. Capture all three.
4. Home at 1600: a module header ("Made for you") is 20/28/600 in the **Display** face; a shelf header on the same
   page is the same metrics in the **UI** face. The two must be distinguishable in a side-by-side crop.
5. A module header with a fact ("Radio · 20 stations" shape) sits the 12 px run on the 20 px run's **baseline**, not
   bottom-aligned. Crop at 400 % and compare baselines.
6. Any eyebrow ("Editorial", "Daily Mix", the Home greeting) is **sentence case** with visible +30/1000 tracking —
   never ALL CAPS.
7. Liked Songs facts panel: a stat numeral renders at 28/36 weight **350** (thin) with its unit on the same baseline.

**Colour & material**

8. Dark theme, album page, a cover with strong hue. The page ground is the record's **hue** at a forced dark
   lightness — hold two albums by the same artist side by side: same page, two colours. The wallpaper must be
   faintly visible through it (α 0.20).
9. Light theme, same album. The ground is near-white with a barely-there hue; the Play capsule carries the record's
   colour at full chroma. If the light page looks strongly tinted, the clamp is wrong.
10. Greyscale artwork (a black-and-white sleeve) in dark: the page ground is `#151515`, not an invented tint, and
    the Play capsule is **system blue**, not grey.
11. Navigate album → playlist → artist with a frame recording. The window chrome's tint **cross-fades** over ~250 ms
    and **never dips through neutral or through a darker frame** on the way.
12. Navigate from a coloured page to Settings (no colour of its own): the chrome eases to the neutral ground; it does
    not snap.
13. Home at 1600 × 900, dark. Three radial washes are visible: top-left (hero), top-right (weekly), bottom-centre
    (mix). The **player dock carries no gradient peak** — the bottom wash is cut at the dock line.
14. Same in light: the washes are roughly half the strength (0.055 / 0.05 vs 0.10 / 0.085).
15. Scroll Home until a module's artwork re-grades (or switch accounts in `--fake`). The changed wash **cross-fades**;
    it does not pop.
16. A track list with zebra striping: an odd row is faintly plated; hovering it is **strictly louder** than the
    stripe; pressing is above rest and below hover. Hover capture on both an even and an odd row.
17. The sidebar's selected row: at rest an **accent** plate plus a 3-DIP pill. Hovering it makes it **louder**, never
    quieter. Hover capture.
18. Any media card's "…" corner and any cover FAB share **one** scrim plate value — they must not read as two
    different darknesses. Hover capture on both.
19. Light theme, a media card: the ink over artwork is still **white** and the scrim still **black**. On-media colour
    does not flip with the theme.
20. Immersive lyrics in **light** theme: the surface is a light veil with near-black ink and a **dark** play disc.
    In dark: a near-black veil, white ink, a white disc. Capture both.
21. The Camelot key dot in a track list, light theme: a yellow/cyan key is visibly darkened (L 0.30) and a red/blue
    key less so (L 0.40). In dark the dots are the raw wire colours. Crop both.

**Surfaces & art**

22. Cold-start `--fake`, open a grid-heavy page (Library ▸ Albums). Frame recording of the first 3 s: covers ≥ 80 DIP
    **breathe** (1.0↔0.5 over 1 s) while loading and stop the instant each lands. No cover breathes after it is up.
23. The same page: a tile that has a graded colour shows a **tinted** placeholder (cover colour at 0.55), not a flat
    grey square.
24. A sidebar pin row (< 80 DIP art): a static, **opaque** tile — it must never breathe and never let the desktop
    read through.
25. A cover-less playlist: a **2 × 2 mosaic** of the first four track covers, each quadrant sharp (decoded at
    width/2). With 1–3 distinct covers it shows the first as a single cover, not a broken mosaic.
26. Set zoom to **150 %** and re-open the same grid. Cover art is **not soft** — the decode budget followed the
    scale. Compare a 400 % crop of one cover at 100 % and 150 %.
27. An artist section header: a **20 × 2** accent rule sits **4 DIP** under the header text (2 gap + 2 column gap) —
    not a vertical capsule beside the title. Crop at 400 %.
28. The content region's edge: a 1 px stroke on the **left and top only**, one rounded corner (top-left, 8), and
    **no shadow**. Crop the corner at 800 %.

**CTAs & controls**

29. Album, playlist, artist, Home hero, Liked: the primary Play is the **same capsule** everywhere — 36 tall, fully
    rounded, bold label, the same 18/6/18/7 waist. Static capture of each, overlaid.
30. Hover it: it scales to **1.04** and the fill drops to accent @ 0.90. Press: **0.96** and @ 0.80. Hover capture +
    frame recording.
31. Tab to it: the focus ring draws **outside** the capsule (`FocusVisualMargin −3`) and Space/Enter both activate.
32. A CTA cluster (artist hero): the round Shuffle/Radio arms are **36** circles beside the 36 capsule, with the same
    hairline and the same hover scale; the glyph inside does **not** scale independently.
33. Any toolbar/panel/row icon button is **32 × 32** with a **4** radius — never a 26/28/30 circle or pill.
34. The sticky context band (scroll an album past the hero): its actions are **plateless bold words**. Hovering one
    brightens only that word — not the whole cluster. Hover capture.
35. The context band's primary verb is in **accent ink**; a latched toggle (Following) is too; everything else is
    neutral. Nothing in the band has a plate, a border or a scale cue.
36. Settings ▸ Lists ▸ Row density: four cards, each 116 × 84, r8. Selecting one thickens its border 1 → 2 **inward**
    — the wireframe inside must not shift by a pixel. Capture selected vs unselected at 400 %.
37. The four density miniatures show visibly different row heights and art sizes (10/8, 12/8, 14/10, 16/12 DIP) that
    mirror the real rows at 0.25 ×.
38. Keyboard-drive the density strip: one Tab stop lands on the current value; ←/→ moves and applies; Ctrl+← moves
    without applying; Space applies. The selected label is **semibold + primary**, the rest regular + secondary.
39. Narrow Settings to ~520 wide: the picker strip **wraps** to fewer columns; the cards do not shrink or overflow.

**Motion**

40. Navigate forward (Home → album) with a frame recording. The outgoing page fades **in place** over 120 ms; the
    incoming page starts at 90 ms, sliding **+8** DIP. Step frame by frame: the two pages are never both legible.
41. Navigate Back: identical, with the incoming page sliding **−8**.
42. Open a module watch page hosting video, then navigate away. The swap **slides** (no fade) and the video hole
    never washes out or vanishes mid-transition.
43. Open Browse (`route=browse`): the category bands cascade in at **40 ms** per item, capped — item 9 and item 30
    land at the same moment. Frame recording.
44. Turn on Windows' "Show animations in Windows" = Off (reduced motion), relaunch, and repeat items 30, 40 and 43:
    no scale cue at all, no stagger, no wash mount fade — but page opacity still cross-fades.
45. Navigate Home → album → Back so the pointer is resting over a grid card as it mounts. The card **must not**
    scale up on its own; it arms only after the pointer actually moves. Frame recording.
46. Open a large album (50+ tracks) cold. Rows swap from shimmer to real in visible **chunks**, not all at once, and
    finish within ~5 frames. Frame recording.
47. Any brush state flip (hovering a button, switching theme) settles in **83 ms**, not instantly and not over a
    quarter second. Frame recording with a frame counter.

**Zoom & theme**

48. Settings ▸ Theme ▸ Zoom shows `Auto (N %)` as the **first** item with the live resolved percentage. Resize the
    window and re-open the tab: N updates.
49. On a 1920 × 1080 display at 100 % DPI, Auto resolves to **100 %**; on a 2560 × 1440, **150 %**. Verify the
    picker label and the rendered scale.
50. Ctrl+= / Ctrl+− / Ctrl+0 and Ctrl+wheel all step the same discrete ladder and switch the row to Manual. Ctrl+0
    returns to the ladder default.
51. Switch theme System → Light → Dark with a frame recording. Every mounted surface re-themes in place; nothing
    remounts, nothing flashes the old palette, and the immersive stage flips polarity with it.
52. Turn OFF Settings ▸ Theme ▸ Color washes. Every page tone, hero wash, artist blend and shell tint disappears at
    once on the **already-open** page — no restart, no navigation. Turn it back on: they return.

**States the first pass of this checklist missed**

53. **No cover at all.** Open a playlist with no artwork and fewer than four track covers. Every art slot is a
    **solid** neutral tile — never a hole, never a see-through square with the desktop behind it, never a shimmer.
54. **Mosaic vs fill.** The same cover-less playlist: its detail rail / sidebar row shows the **2 × 2 mosaic**, but
    the SAME playlist inside a fluid grid (Library ▸ Playlists) shows only its **first tile as one square** — a
    fill cell has no known width, so it cannot mosaic. Capture both; they are supposed to differ.
55. **Wrong-half grading.** In LIGHT theme, open a page whose cover has only a dark (kind-179) grading. The art tile
    stays the neutral light tile — a dark slab must never land on a pale page — and the page tone paints nothing
    until the light half arrives, then cross-fades in.
56. **Search highlight, both arms.** Type a partial query in the library (single-line rows) and open Charts with the
    same query (wrapping grid titles). The pill is identical in both; the library row ellipsises on one line, the
    Charts title **wraps between runs** with the pill intact and never clipped. Crop both at 400 %.
57. **No match.** Type a query that matches nothing in a highlighted list: titles render as plain ellipsised text —
    no empty pill, no 6-DIP gap where one used to be.
58. **Text-action glyph.** The sticky context band's "Save": the heart glyph and the word brighten **together** on
    hover, at the same ink, 8 DIP apart. Hover capture.
59. **Row press direction.** Press-and-hold a track row in LIGHT: the plate goes **darker than hover**. Repeat in
    DARK: it goes **lighter than rest but quieter than hover**. Both are correct; capture both so a future pass does
    not "fix" one to match the other.
60. **Picker card clip.** Settings ▸ Lists ▸ Track list style at a narrow width: the Modern/Classic miniatures are
    **cut at the card edge**, never painted over the neighbouring card. Crop the seam at 800 %.
61. **Picker strip wrap.** Narrow Settings until the density strip drops to two columns: the cards keep their exact
    116 × 84 footprint; only the COLUMN COUNT changes. (Item 39 says "wraps"; this pins *what* wraps.)
62. **Zoom migration.** On a profile that already stored `appearance.zoom = 1.25`, launch a build that introduces the
    policy: the Zoom row reads **125 %**, not `Auto (150 %)`. On a profile at exactly 1.0, it reads `Auto (N %)`.
63. **Theme flip re-publishes the shell tint.** On a coloured detail page, flip Light ↔ Dark without navigating: the
    window chrome swaps from the light `Lift(TextBase) @ 0.05` whisper to the dark `TintedDark @ 0.14` (or back) in
    the same gesture as the rest of the retheme. It must not stay on the old arm until the next navigation.
64. **Icon glyphs.** "Play next" / "Add to queue" in any track context menu, and the lyrics bubble in the player bar,
    render as real marks (`U+E900` / `U+E901` / `U+E902` from the bundled `wavee-icons.otf`), not tofu. Repeat on a
    Windows 10 box for the bundled `SegoeFluentIcons.ttf` path.
65. **Caption default colour.** An eyebrow whose call site sets no `Color` ("Video", "Release") is **secondary**, not
    primary — `Ui.Caption` is the one ramp rung bound to the secondary brush. Crop against an adjacent 12/16/400
    `TrackMeta`: the two must be the same ink.

**Focus (§6.8, W18), and the dormant Hero morph (§5.1)**

66. **One tab stop per virtualized list.** Open an album and Tab from the page top. The track table takes **ONE** Tab;
    ↑/↓ then rove inside it and the next Tab leaves the table entirely. Count the Tabs on a 50-row album: crossing the
    table must cost one, not fifty.
67. **Keyboard-only ring.** Click a track row with the mouse: **no ring**. Tab to the same row: the ring appears.
    Repeat on the Play capsule and on a home shelf card. A ring after a pointer press is a regression
    (`NodeFlags.FocusVisual`).
68. **The ring geometry, both arms.** Tab to the Play capsule and crop at 800 %: a 2-DIP outer band with a 1-DIP inner
    band, drawn **OUTSIDE** the capsule (margin −3) and concentric with its 18-DIP corner. Tab to a Settings ▸ Lists
    density card: the same pair drawn **INSIDE** (margin 2), clear of the card's own border. The two crops are supposed
    to differ.
69. **The ring re-themes in place.** Park keyboard focus on a button and flip Light ↔ Dark without clicking. Light =
    near-black outer (`#000000E4`) over a white inner band; dark inverts (white outer, `#000000B3` inner). It must
    re-theme with everything else, not on the next focus move.
70. **Editor focus is pointer-style; the restore is keyboard-style.** Open the track-table search field: the caret
    lands at the **END** with nothing selected and **no ring**. Press Esc: focus returns to the magnifier button
    **with** a ring. Repeat for the narrow-sidebar search and for playlist inline edit.
71. **Overlay restore, both directions.** Open a flyout from a focused button and close it: focus returns to that
    button. Now open a flyout, click somewhere else in the page so focus is outside it, then close it: focus must
    **NOT** be yanked back to the opener.
72. **Fullscreen video traps and returns.** Enter fullscreen from a focused transport button; Tab repeatedly — focus
    must never walk out of the presentation. Exit: focus is back on the transport button.
73. **Record where page-navigation focus lands.** 0.2.9 has no answer (§6.8, last two rows). Home → album → Back, and a
    KeepAlive-parked tab reactivated: after each, press Tab once and note where the ring appears. Capture the 0.2.9
    behaviour **before** 0.3 decides, so the decision is visible rather than a drift.
74. **Morph dormancy is measured, not assumed.** Run `WaveeNavProbe`'s card-click capture. The log must print
    `[wavee-nav-probe] fly key = album:…` or `pl:…` — a REAL key, never `(none found)` — and `[conn-stress]` must not
    abort with `no home-card morph keys`. **Nothing flies on screen, and that is correct today**; the assertion is that
    the SOURCE half is still minted through `MorphKeys.For`.
75. **One tag per uri.** In a recents list containing a repeated uri (a playlist opened on three different days), only
    the **first** row for that uri carries the tag — `CollectMorphKeys` returns DISTINCT keys, and its count matches the
    number of distinct morphable uris on screen, not the row count. Two live nodes under one `MorphId` is a
    duplicate-key bug that would blank a row mid-fly.

---

## 11. Audit log

Adversarial re-read of every 0.2.9 source against the chapter, 2026-09-12. `wrong` = the chapter stated something the
code contradicts. `missing` = a state / element / number the code has and the chapter did not. `unverified` = a claim
that could not be traced to the code it cites. `overclaim` = a source listed or a scope claimed that the chapter does
not actually cover.

| # | kind | section | correction |
|---|---|---|---|
| 1 | wrong | W1, §3 type table | `PickQuote` / `FoldTitle` tracking is **−12**/1000, not −6 (`WaveeType.cs:202, 212`). Both places fixed. |
| 2 | wrong | W10 | cited `WaveeColors.cs:244-274`. There is no such file — the selection ladder is `WaveeTokens.cs:244-274`. |
| 3 | wrong | §0 #6, §5 rows 3-4 | `ScaleStandard` is `WaveeMotion.cs:43`, not `:45`. |
| 4 | wrong | §8 `PageNavMotion` row | "no dedicated test file in 0.2.9" is false. **`ContentHostPageTransitionTests.cs` exists: 17 facts, 272 lines**, and it pins the exit/enter overlap, the curve pairing, `DistBase`, and all four video-safe properties. Row rewritten; §9.3 #6's gate list and its fact count updated (184 facts, ~3 400 lines). |
| 5 | wrong | §6.5 | "Always hide track artwork" is not a top-level Appearance row — it is an ITEM inside the **Row-density expander's body** (`SettingsPage.Appearance.cs:421-423`). |
| 6 | wrong | §1.1, §3 | engine line refs drifted: type ramp `Dsl/Typography.cs:39-46` (not 38-46); spacing scale `:11-18` (not 13-20); `PageWide` `:22` / `PagePadWide` `:30` (not 24/33); `PageNarrow` `:23` / `PagePadNarrow` `:31` (not 25/34); `Dsl/Radii.cs:10-15` (not 9-14); `Surfaces.cs:25` for `Ceiling` (not :24); `DetailTrackTableRules.cs:45-47` (not 44-46); `WaveePicker.cs:280` for the throwaway signal (not :279). |
| 7 | wrong | header | line counts: `Surfaces.cs` 506, `WaveeMotion.cs` 191, `PageNavMotion.cs` 123, `CoverPaletteLeaves.cs` 266. |
| 8 | missing | §0 #1, W1 | `Ui.Caption` is the ONE ramp rung bound to `Tok.TextSecondary` (`Dsl/Typography.cs:39`), so `Eyebrow`'s default is secondary. Also named the second off-ramp, `WaveePicker.Label`'s `size + 4` line box. |
| 9 | missing | W1 | the three `MinSize` shrink floors and the alias-owned ellipsis policy (NoWrap + CharacterEllipsis + MaxLines 1 + Shrink 1 on all three baseline-paired spans). |
| 10 | missing | W5, §3 | `WaveePicker.Ink.For` alphas (selected `AccentDefault` / `@0.45`; unselected `@0.58` / `@0.22`); `Card`'s `Shrink 0` + `ClipToBounds`; `Titled`'s gap 8 / centre; `Strip`'s `Math.Max(1, …)` clamp, `parts` override and `s_bare`'s zeroed MinWidth/MinHeight/ContentGap; what actually wraps (columns, not cards). |
| 11 | missing | W5 | `DensityRows` ALWAYS draws the **Modern** ladder — `TrackRow.RowHeightFor`/`ArtSizeFor` are bare forwards to `classic: false` — so the preview shows 10/12/14/16 even when the user's track style is Classic (real ladder 36/40/44/48, art 32/32/32/40). |
| 12 | missing | W10 | the press rung **inverts by theme**: light pressed 7.1 % is ABOVE hover 5.1 %; dark pressed `0x0A` is BELOW hover `0x0F`. Both are WinUI arms. Also why dark zebra is overridden app-side and what that does to `RowHoverZebra`. |
| 13 | missing | W14 | four art states the chapter had no answer for: **no image at all** (static neutral tile, never a shimmer); **`ArtworkFill` never mosaics** (collapses to `tiles[0]`); the **morph branch** (no shimmer sibling, frozen placeholder); and the three decode branches (`scale == 1`, `decodePx > 0` square cover-fit, `scale ≠ 1` bucketed) plus `saturation` / `preferLargest`. |
| 14 | missing | §2 | added **W16 — SearchHighlight, both arms**. The chapter documented one arm; the code has a single-line arm, a WRAPPING arm (`maxLines > 1`, used by Charts: `Wrap`, `Basis NaN`, no spacer, runs' `Grow` forced 0, `MaxHeight = lines × LineBoxFor`) and a **no-match** arm (a plain ellipsised `TextEl`). |
| 15 | missing | §2 | added **W17 — Settings ▸ Theme**, the user-facing surface of this chapter. §6.5 was a table with no picture, so the expander/body structure, the two nested sub-items and the per-row glyphs were invisible. |
| 16 | missing | §3 | `Elevation.CardHover`, `Elevation.Tooltip` and `Elevation.DockTop` (an UPWARD shadow, dark blur 12 y−2 `#00000028`) — three of the engine's six bands were absent; the chapter listed only Card/Flyout/Dialog. |
| 17 | missing | §3.1 | `WaveeColors.Badge`, `FloatingChrome`, `Tok.AccentDisabled` / `TextOnAccentDisabled`, `Tok.AccentControlElevationBorder`, `Tok.TextOnAccentSelectedText`, `Tok.StrokeControlDefault`. |
| 18 | missing | §4.1 | added the **three "no colour yet" states** table (no grading / wrong half / greyscale) and the fact that `ChromeSchemeFor` falls back to the SAME-theme half. A MISS is a first-class state and the chapter only said so in prose. |
| 19 | missing | §4.2 | `BackgroundDark` / `TintedDark` (`:166-167`), and the four thresholds (`PageToneChromaFloor` 0.12, `NeutralS` 0.08, `TextContrast` 4.5, `HairlineSaturationCeiling` 0.50). |
| 20 | missing | §4.5 | `hasColor` counts wash legs; the publish `DepKey` carries `Tok.Theme` (so a live theme flip re-derives the tint arm); the binder's 0×0 node; the always-mounted keyed tint layer; straight-alpha wash stops; `Resolve`'s anchor rule. |
| 21 | missing | §5 | four motion rows: picker-card `ScaleSubtle`; the text action's ink-only `HoverT` interpolation with **no** `HoverScale`; `CoverShimmer`'s `DepKey.From(shimmer)` loading→settled edge; and the art-tile grading arrival being an **instant paint**, the one art-colour arrival that is deliberately not a 250 ms cross-fade. |
| 22 | missing | §6.1 | `WaveeCta.Palette`'s THIRD parameter (`border`), `BackgroundSizing.OuterBorderEdge`, the system-token disabled legs, and `OnFillSecondaryAlpha` being a **luminance** decision (0x80 dark ink / 0xB3 light) rather than a theme read. |
| 23 | missing | §6.3 | the text action's optional leading glyph (`Size 14`, `Theme.IconFont`, same ink triple, `Gap` 8) and the fact that it has **no disabled arm**. |
| 24 | missing | §6.5 | the tab's own header keys, `settings.choice.{system,light,dark}` and `{off,translation,romanization}`, the per-row glyphs, and that `ZoomAutoMode.Dense` is built but **unreachable** from Settings. |
| 25 | missing | §1.1 | glyph CODES (`PlayNext U+E900`, `PlayAfter U+E901`, `Lyrics U+E902`) and both font paths; `PageAccent(Ink, Fill, Key)`'s shape and why `Key` is provenance; `AppearancePrefs.LikedCover`; `NavTransitionKind` / `PageSlot` / the `U+001F` separator in `SlotKey`; `DetailRevealRamp.Revealed`; `ZoomLadder.Steps`' twelve rungs. |
| 26 | missing | §8 | five pure rules that were not listed: `WaveeCta.Palette` + `OnFillSecondaryAlpha`, `WaveePicker.Ink.For` + the five `Shell` footprints, `ArtistHeroVeil`'s stops (four is the RECORDER limit), `SectionBand`/`HomeHeroBackdrop`'s gradient, and `ShellWashGeometry`'s anchor rule. Four of the five have **no test in 0.2.9** — flagged for the port. |
| 27 | missing | §9.2 | six traps: the wrap-arm spacer, the picker card's clip, a non-12 `labelSize`, `Tok.Theme` in the tint `DepKey`, and the morph branch's frozen placeholder. |
| 28 | missing | §9.5 | `MigrateMode`'s 0.004 tolerance and `ZoomModeBootstrapVersion` stamp — without both, a never-written store and one written at the default are indistinguishable. |
| 29 | missing | §10 | items 53-65: no-cover, mosaic-vs-fill, wrong-half grading, both search-highlight arms, no-match, the text-action glyph, the light/dark press inversion, picker clip, what wraps in the strip, zoom migration, theme-flip tint re-publish, the PUA glyphs, and the Caption default colour. |
| 30 | overclaim | header | `App/ShellUi.cs` was listed as a source. It is rail/overlay chrome state (`RailOpen`, `RailMode`, docked-video height, `ImmersiveLyrics`) and nothing in this chapter reads it. Re-pointed at `18-shell-frame.md` / `21-right-rail-npv-queue-stage.md` and replaced with the consumer files the chapter actually cites (`ContentHost.cs`, `ShellMastheadBand.cs`, `WaveeShell.cs:150`, `ContextBandLayout.cs:27,61`, `StageLayout.cs:217`, `TrackRow.cs:118-124`, `assets/loc`, `assets/fonts`). |
| 31 | missing | §0 #16, W18, §3.1, §6.8, §10 | **critic-fix:** there was no app-wide focus-visual specification. The chapter carried isolated values (`FocusVisualMargin −3` quoted in §6.1, `All(2)` in W5/§6.4) and no owner for the ring itself, the override rule, the roving tab stop or restoration. Added: non-negotiable 16; the `Tok.FocusOuter` / `FocusInner` / `FocusThickness` rows in §3.1 (`PaletteBuilder.cs:254-255, 342-343, 453-454`; `Dsl/Tokens.cs:455-456`); **W18**, the dual-ring geometry (`SceneRecorder.cs:3340-3369`), the fact that a null `FocusVisualMargin` means −3 and not "no ring" (`Dsl/Element.cs:296`), and all **five** margin arms censused across the 36 explicit app call sites — `(2,2,2,2)` ×21 on bare cards/tiles, `(1,1,1,1)`/`All(1)` ×11 at row scale, `All(2)` on `WaveePicker` s_bare (`:244`), `All(−2)` on `PlayerStyleFlyout` (`:101, 182`), `All(−3)` on `WaveeEqualizerCurve` (`:144`); and **§6.8**, which adds the keyboard-only rule (`NodeFlags.cs:51`, `InputDispatcher.cs:3677, 3763`), the `FocusNode(h, visual)` true/false split with every 0.2.9 call site, the four-shape roving-tab-stop table (`DetailTracks.cs:3534-3535` "the ItemsView roving effect owns the single tab stop", `TrackRow.cs:547-548`, `MediaCard.cs:1231`, `LibraryV3Chips.cs:335-347`), and a restoration table covering overlay close (`OverlayHost.cs:468-490`), fullscreen video (`VideoFullscreenSurface.cs:129-147`), immersive lyrics, inline editors and strip roves. **Two blanks recorded as blanks:** `ContentHost.cs` contains no focus code at all, so neither page navigation nor KeepAlive reactivation restores focus in 0.2.9 — an open decision for 0.3, with §10 item 73 measuring the baseline first. Page chapters' §6 paragraphs now cite §6.8 instead of restating fragments. |
| 32 | missing | §5.1, §10 | **critic-fix:** shared-element morph had no end-to-end owner even though the plumbing is live. 00 §5 carried one DORMANT row and §1.1 one line. Added **§5.1**: the key convention (`MorphKeys.cs:7-17`), the two halves — source minted at `RecentsPage.cs:1707` under the first-occurrence-only `Morphable` flag (`:137, 149, 1693-1697, 2073-2075`), destination nulled at `DetailShell.cs:257` — the three-part reason at `DetailShell.cs:235-256` (the capture seam went with `3b80bbcf8`; `SharedTransition.Begin` has zero callers; `AbsoluteRect` includes the page slide's `Dx` so `RetargetFlight` would chase a fading page), the full paint path (`Surfaces.cs:272, 279-280, 285` — the morph branch drops the shimmer sibling and freezes the placeholder; `MediaCard.cs:172-178, 959-981, 1111, 1132, 1143`; `LikedSongsArtwork.cs:21-83`; `LikedCoverArt.cs:141`; `LikedCoverTreatments.cs:96-98`; `DetailRail.cs:99-104, 159, 320, 421`; `DetailVerticalHero.cs:122-130`), the explicit instruction that `MorphId` / `morphKey` / `DetailConfig.MorphKey` survive the port while dormant, and the separation from `WaveeShell.ContentRowMorphId` (a FLIP anchor, `:146-148, 991-994, 1135`), which is NOT a Hero key. §10 items 74-75 pin the probe's behaviour (`WaveeNavProbe.cs:747-748, 1635, 2575, 2682, 2758, 2762, 2783`) so the dormancy is measured. |

**Verified correct, spot-checked and left alone** (recorded so the next pass does not re-derive them): every number in
**W8** — all three wash placements recomputed from `ShellWashGeometry.Resolve` and they match to four decimals
(Hero 0.5188 × 0.5704, centre 0.1157/0.000, radius 1.4264/1.6129; Weekly 0.4512 × 0.5992, centre 0.8227/0.1669,
radius 1.2855/1.3018; Mix 1.0 × 0.462, centre 0.58/1.00, radius 0.90/1.5152), and the four alphas. Every row of
**W13** re-derived through `Suggest`, including the ultrawide's 1.55 and the sliver's height guard. The **W4** CTA
ramp against `WaveeCta.Palette`. The **W11** timeline against `PageNavMotion.cs:49-68` and `:105-121`. The
**§3.1** light/dark colour table against `PaletteBuilder.cs`. The **§4.4** stage arms against `StageArm.cs`
(including `StageLayout.ScrimBaseA` = 0.46). `ContextBandLayout.Height` 56 / `ActionPadX` 10, and
`WaveeShell.ContentPaneCorners = (8,0,0,0)` at `:150`. The two drifts recorded in **§9.6** are both real: the
"360 ms" comment against the arithmetic 320, and `large-display-scaling.md §3.2`'s `DesignW = WaveeSize.PageMaxW`
against the shipped BCL-only literal.

**arbitration (2026-09-12):** `Platform/Controls.cs`'s size and shape are settled here, because three chapters gave
three answers and one of them could not hold its own contents: this chapter's **4 500–6 000**,
`02-cards-and-controls.md`'s **3 200–3 800** for its material alone, and `01-track-row.md`'s 900 share "of a ~1 200
file budget" — 900 + 3 200 does not fit in 1 200. The decision: **this chapter's 4 500–6 000 is the envelope for the
whole file**, 02's and 01's figures are shares *inside* it rather than additions to it, and the named partials are
fixed at four on day one — `Controls.cs`, `Controls.Cta.cs`, `Controls.Art.cs`, `Controls.Picker.cs` (this chapter's
own e.g.-list, now binding; 02's alternative `Controls.Cards.cs` / `Controls.States.cs` naming is dropped). §9.4's
honest-estimate row and a new paragraph under it record it; §9.3 item 1 already points there. The 0.2.9 counts the
envelope rests on were re-checked this pass: `Design/` is **2 980** lines and `Components/` is **7 600**.

**token-reconcile (2026-09-12):** §12 rebuilt against the same sources. Eleven `Tok.*` tokens that chapters name but the first build of §12.1 missed are now indexed — `CaptionCloseHover`, `ControlElevationBorder`, `FillControlAltSecondary`, `FillControlStrong`, `FillLayerAlt`, `FillSolidSecondary`, `MediaLetterbox`, `ScrimBottom`, `ScrimTop`, `SystemFillAttention`, `SystemFillAttentionBackground`. A seventh magic-number row was added to §12.2 (the multi-select check lane's 333 ms / 28 DIP, which the engine already names as `SelectorVisualsBound.MultiSelectAnimMs` / `CheckboxContentOffset` and the app re-authors raw at `TrackRow.cs:569`), and the collision list grew to four: **333 ms is two different motions** — `MotionTok.DisclosureExpand` on `FluentPopOpen` and the check lane on `FluentDecelerate`. Every value and line ref already in §12 was re-verified against `Design/*.cs`, `Dsl/Spacing.cs`, `Dsl/Radii.cs`, `Dsl/Expressive.cs`, `Dsl/Elevation.cs`, `Animation/MotionTok.cs` and `Foundation/Easing.cs` and none changed.

---

## 12. Token index

Every token, type style, spacing / radius / size constant, colour, material, easing curve and duration the 29
chapters of this contract cite, in one alphabetical table, with the value the code actually resolves and the chapters
that read it. Built 2026-09-12 by indexing all 29 chapters against `src/apps/Wavee/Design/*.cs` and the engine files
they cite (`Dsl/Spacing.cs`, `Dsl/Radii.cs`, `Dsl/Elevation.cs`, `Dsl/Expressive.cs`, `Dsl/PaletteBuilder.cs`,
`Animation/MotionTok.cs`, `Foundation/Easing.cs`). **Reconciled 2026-09-12** against the same sources: eleven `Tok.*`
tokens that chapters name but the first build of this index missed are now carried below, and a seventh magic-number
row was added to §12.2.

**How to read it.** `defined at` is the DECLARATION, not a call site. `used by chapters` is a literal occurrence of the
token's NAME across the 29 files, so a chapter that paraphrases a token instead of naming it does not appear — and
that absence is itself the signal §12.2 is built from. **§3.1 stays the authority on the colour ladder's derivation
and on its own `PaletteBuilder` line refs**; the colour rows below repeat §3.1's refs verbatim, and the Tok tokens
§3.1 does not cover cite the builder method they were read from (`BuildWinUILight()` 387-480 / `BuildDark()` 286-370).

**Four naming collisions to keep straight**, all real and all load-bearing:

- `WaveeMotion.Fast` is **167 ms** (WinUI `ControlFastAnimationDuration`); `MotionTok.ControlFast` is **150 ms**.
  Two rungs, near-identical names, different files. `Faster` 83 and `Standard` / `ControlNormal` 250 DO agree across
  the two layers — only the middle rung diverges.
- `PillHeight` is **36** in `WaveeCta` (`WaveeCta.cs:65`) and **28** in `LikedLens` (`LikedFactsPanel.cs:1355`, a
  private const of the liked-songs lens row). Always qualify it.
- `Radii.Overlay` is a second NAME for `Radii.Card`'s 8, not a second value; `Radii.Full` is **999**, clamped to half
  the box at record time — it is never 16 (that is `Radii.Pill`).
- **333 ms is two different motions.** `MotionTok.DisclosureExpand` is 333 ms on `FluentPopOpen`; the multi-select
  check lane is 333 ms on `FluentDecelerate` (engine `SelectorVisualsBound.MultiSelectAnimMs`). Matching the bare
  number to the token gets the wrong curve — see §12.2's row.

### 12.1 The index

| name | light / dark value | kind | defined at | used by chapters |
|---|---|---|---|---|
| `Easing.EaseInOut` | `t<.5 ? 2t² : 1−2(1−t)²` | easing | engine `Foundation/Easing.cs:397` | 01, 02, 23, 24 |
| `Easing.EaseOut` | `1−(1−t)²` | easing | `Easing.cs:396` | 00, 12 |
| `Easing.FluentAccelerate` | cubic-bezier(0.9, 0.1, 1.0, 0.2) | easing | `Easing.cs:407` | 12, 13, 17, 28 |
| `Easing.FluentDecelerate` | cubic-bezier(0.1, 0.9, 0.2, 1.0) | easing | `Easing.cs:406` | 01, 02, 12, 13, 15, 24, 28 |
| `Easing.FluentDisclosureChevron` | cubic-bezier(0.167, 0.167, 0, 1) | easing | `Easing.cs:410` | 01, 16 |
| `Easing.FluentDisclosureCollapse` | cubic-bezier(1, 1, 0, 1) | easing | `Easing.cs:409` | 16 |
| `Easing.FluentPopOpen` | cubic-bezier(0, 0, 0, 1) | easing | `Easing.cs:408` | 01, 03, 16 |
| `Easing.FluentStandard` | cubic-bezier(0.8, 0, 0.2, 1.0) | easing | `Easing.cs:405` | 02, 13, 21, 24, 28 |
| `Easing.Hold` | step-end — snaps only at `t = 1` | easing | `Easing.cs:415` | 24 |
| `Easing.Linear` | identity | easing | `Easing.cs:417` | 07, 21, 28 |
| `Easing.SmoothOut` | cubic-bezier(0.22, 1.0, 0.36, 1.0) | easing | `Easing.cs:411` | 20 ch — all but 03, 05, 11, 18, 19, 20, 25, 27, 30 |
| `Elevation.Card` | blur 4 / y +2 / `#0000001A` · blur 8 / y +2 / `#00000033` | shadow | engine `Dsl/Elevation.cs:18-21` | 21 ch — all but 15, 16, 18, 19, 20, 23, 26, 28 |
| `Elevation.CardHover` | blur 12 / y +4 / `#00000024` · blur 16 / y +4 / `#00000040` | shadow | `Elevation.cs:22-24` | 00 |
| `Elevation.Dialog` | blur 48 / y +12 / `#00000030` · blur 64 / y +16 / `#00000066` | shadow | `Elevation.cs:41-43` | 00, 18, 19, 21, 25, 28 |
| `Elevation.DockTop` | blur 6 / y **−1** / `#00000014` · blur 12 / y **−2** / `#00000028` | shadow (upward) | `Elevation.cs:37-39` | 00, 20 — **Wavee's dock does not use it** |
| `Elevation.Flyout` | blur 16 / y +8 / `#00000024` · blur 16 / y +8 / `#00000042` | shadow | `Elevation.cs:33-35` | 00, 01, 12, 17, 18, 19, 21, 24, 26, 27 |
| `Elevation.None` | `default` — no shadow | shadow | `Elevation.cs:17` | — |
| `Elevation.Tooltip` | blur 8 / y +4 / `#00000024` · blur 16 / y +4 / `#00000040` | shadow | `Elevation.cs:26-28` | 00, 27 |
| `Expressive.BlurSmall` | 2 (σ px) | blur | engine `Dsl/Expressive.cs:36` | 00, 01 |
| `Expressive.DistBase` | 8 DIP | distance | `Expressive.cs:25` | 00, 08 |
| `Expressive.Fast` | 250 ms | duration | `Expressive.cs:17` | 21 ch — all but 16, 19, 21, 23, 24, 25, 27, 30 |
| `Expressive.Slow` | 400 ms | duration | `Expressive.cs:19` | 00, 02, 10, 13, 15, 16, 17, 21 |
| `Expressive.Stagger` | 40 ms | duration | `Expressive.cs:14` | 11, 12, 15 |
| `Expressive.VerySlow` | 500 ms | duration | `Expressive.cs:20` | 09, 12, 15 |
| `ImageDecodeScale.BucketPx` | 8 device px — round UP to this grid | decode grid | `Design/Surfaces.cs:31` | 00, 02, 20 |
| `ImageDecodeScale.Ceiling` | 2048 device px | decode clamp | `Surfaces.cs:25` | 00, 02, 20 |
| `MotionTok.ContentResize` | spring, response 0.40 / damping 0.90 | motion token | engine `Animation/MotionTok.cs:177` | 16, 25 |
| `MotionTok.ControlFast` | **150 ms**, `FluentStandard`, `KeepFade` | motion token | `MotionTok.cs:165` | 02, 06, 10, 11, 12, 13, 18, 21, 22, 25, 28 |
| `MotionTok.ControlFaster` | 83 ms, `FluentStandard`, `KeepFade` | motion token | `MotionTok.cs:164` | 02, 12, 13, 25, 28 |
| `MotionTok.ControlNormal` | 250 ms, `FluentStandard`, `KeepFade` | motion token | `MotionTok.cs:166` | 02, 04, 06, 07, 08, 10, 11, 12, 13, 21, 22, 28 |
| `MotionTok.DisclosureChevron` | 167 ms, `FluentDisclosureChevron` | motion token | `MotionTok.cs:182` | 01, 16 |
| `MotionTok.DisclosureCollapse` | 167 ms, `FluentDisclosureCollapse` | motion token | `MotionTok.cs:181` | 16 |
| `MotionTok.DisclosureExpand` | 333 ms, `FluentPopOpen` | motion token | `MotionTok.cs:180` | 01, 03, 16 |
| `MotionTok.EmphasizedEnter` | 500 ms, `FluentDecelerate`, `KeepFade` | motion token | `MotionTok.cs:170` | 08 |
| `MotionTok.ItemPlacement` | spring, response 0.40 / damping 0.85 | motion token | `MotionTok.cs:178` | 25 |
| `MotionTok.StandardEnter` | 300 ms, `FluentDecelerate`, `KeepFade` | motion token | `MotionTok.cs:168` | 02, 10, 16, 19, 28 |
| `MotionTok.StandardExit` | 200 ms, `FluentAccelerate`, `KeepFade` | motion token | `MotionTok.cs:169` | 19 |
| `MotionTok.StandardSpring` | spring, response 0.35 / damping 0.85 | motion token | `MotionTok.cs:173` | 28 |
| `PlayerDock.BarH` | 72 | size | `WaveeTokens.cs:81` | 00 |
| `PlayerDock.Margin` | 0 | spacing | `WaveeTokens.cs:82` | 00 |
| `PlayerDock.Reserve` | 72 (`= BarH`) | size | `WaveeTokens.cs:83` | 00, 08, 09, 10, 13, 15, 17, 18, 30 |
| `Radii.Card` / `.CardAll` | 8 | radius | engine `Dsl/Radii.cs:12, 18` | 23 ch — all but 04, 11, 18, 19, 20, 22 |
| `Radii.Circle(d)` | `d / 2` | radius | `Radii.cs:23` | 02, 13, 19, 21, 25, 27, 28, 30 |
| `Radii.Control` / `.ControlAll` | 4 | radius | `Radii.cs:11, 17` | 26 ch — all but 08, 11, 23 |
| `Radii.Full` / `.FullAll` | **999** — clamped to half the box at record time (a 36-tall pill draws 18) | radius | `Radii.cs:15, 21` | 19 ch — all but 04, 17, 18, 19, 20, 21, 22, 23, 25, 30 |
| `Radii.None` | 0 | radius | `Radii.cs:10` | 00, 01, 21, 30 |
| `Radii.Overlay` / `.OverlayAll` | 8 — a second NAME for Card's 8, not a second value | radius | `Radii.cs:13, 19` | 01, 13, 19, 24, 28 |
| `Radii.Pill` / `.PillAll` | 16 | radius | `Radii.cs:14, 20` | 01, 06, 21, 25, 28 |
| `Spacing.Gutter` | 24 (`= XXL`) | spacing | engine `Dsl/Spacing.cs:19` | — |
| `Spacing.L` | 16 | spacing | `Spacing.cs:15` | 02, 03, 10, 12, 13, 16, 17, 18, 25, 28, 30 |
| `Spacing.M` | 12 | spacing | `Spacing.cs:14` | 00, 01, 02, 03, 07, 08, 10, 12, 13, 16, 18, 19, 24, 27, 28 |
| `Spacing.PageNarrow` / `PagePadNarrow` | 16 / `Edges4.All(16)` | spacing | `Spacing.cs:23, 31` | 00 |
| `Spacing.PageWide` / `PagePadWide` | 36 / `(36, 24, 36, 36)` — off-grid by WinUI design | spacing | `Spacing.cs:22, 30` | 00, 10, 12, 13, 16, 17, 18 |
| `Spacing.S` | 8 | spacing | `Spacing.cs:13` | 20 ch — all but 04, 05, 10, 11, 15, 20, 22, 25, 26 |
| `Spacing.XL` | 20 | spacing | `Spacing.cs:16` | 01, 05, 08, 09, 10, 12, 16 |
| `Spacing.XS` | 4 | spacing | `Spacing.cs:12` | 01, 02, 04, 07, 08, 10, 12, 17, 20, 22, 24, 25, 27, 28 |
| `Spacing.XXL` | 24 | spacing | `Spacing.cs:17` | 01, 04, 08, 09, 10, 12, 13, 16, 22 |
| `Spacing.XXS` | 2 — the sole 2 px step off the 4-grid | spacing | `Spacing.cs:11` | 00, 01, 02, 05, 12, 13, 19, 20, 22, 24, 25 |
| `Spacing.XXXL` | 32 | spacing | `Spacing.cs:18` | 00, 09, 11, 12, 13, 17, 18, 19, 27 |
| `Surfaces.AccentRuleGap` | 2 — the rule's TOP margin; stacks on the header column's own 2 ⇒ 4 total | spacing | `Design/Surfaces.cs:356` | 00, 03, 05, 06, 08, 30 |
| `Surfaces.AccentRuleHeight` | 2 | size | `Surfaces.cs:356` | 00, 03, 05, 06, 08, 30 |
| `Surfaces.AccentRuleWidth` | 20 | size | `Surfaces.cs:356` | 00, 03, 05, 06, 08, 30 |
| `Surfaces.ArtworkPlaceholderDark` | — / `#2A2A2A` | colour | `Surfaces.cs:57` | 00, 02, 08, 09, 20 |
| `Surfaces.ArtworkPlaceholderLight` | `#F2F2F2` / — | colour | `Surfaces.cs:58` | 00, 02, 08, 09, 20 |
| `Surfaces.ShimmerMinEdge` | 80 DIP — below it the breathe is imperceptible; use a static tile | threshold | `Surfaces.cs:203` | 00, 02, 09, 20 |
| `Surfaces.TintStrength` | 0.55 — `Lerp(neutral, cover, 0.55)` | ratio | `Surfaces.cs:66` | 00, 02, 09 |
| `Tok.AccentControlElevationBorder` | the pill's 3-px rest/hover border band (`OnAccentSecondary` @0.33 → `OnAccentDefault` @1, `AnchorEnd`) | brush ramp | `WaveeCta.cs:228` | 00 |
| `Tok.AccentDefault` | `#005FB8` / OS ramp shade | colour | engine `Dsl/Tokens.cs:438` | 28 ch — all but 24 |
| `Tok.AccentDisabled` / `Tok.TextOnAccentDisabled` | the CTA's disabled plate + ink — SYSTEM tokens on purpose | colour | `WaveeCta.cs:231-232` | 00, 05, 16 |
| `Tok.AccentSecondary` | `#005FB8E6` / dark arm | colour | `BuildWinUILight():445` | 19 |
| `Tok.AccentSelectedTextBackground` | `#0078D4` / — | colour | `PaletteBuilder.cs:255` | 00, 01, 02, 12, 13, 15 |
| `Tok.AccentSubtle` (= `WaveeColors.SelectedRest`) | `#005FB824` (14 %) / `#60CDFF29` (16 %) | colour | `PaletteBuilder.cs:252, 340` | 00, 01, 02, 04, 06, 07, 15, 16, 17, 18, 24, 25, 26, 27, 28 |
| `Tok.AccentTextPrimary` (= `WaveeAccent.Decor`) | `#004275` / `#A6D8FF` | colour | `PaletteBuilder.cs:248, 336` | 22 ch — all but 07, 10, 23, 24, 25, 27, 30 |
| `Tok.AccentTextSecondary` | `#002642` / `#A6D8FF` | colour | `PaletteBuilder.cs:249, 337` | 00 |
| `Tok.AccentTextTertiary` | `#005FB8` / `#76B9ED` | colour | `PaletteBuilder.cs:250, 338` | 00 |
| `Tok.AcrylicFlyout` | `AcrylicSpec.FlyoutLight` / `AcrylicSpec.InAppDefault` | material | `BuildWinUILight():458` / `BuildDark():347` | 01, 15, 16, 17, 18, 19 |
| `Tok.CaptionCloseHover` | `#C42B1C` — both themes | colour | `BuildWinUILight():442` / `BuildDark():331` | 18 |
| `Tok.ControlElevationBorder` | the STANDARD control's 3-px border band (`StrokeControlSecondary` @0.33 → `StrokeControlDefault` @1, `AxisLengthPx` 3, `AnchorEnd` in LIGHT) — the non-accent twin of `Tok.AccentControlElevationBorder` | brush ramp | engine `Dsl/Tokens.cs:306` | 17, 19, 20 |
| `Tok.Epoch` | the re-fire signal a theme / preset switch bumps | signal | engine `Dsl/Tokens.cs` | 00, 18, 20, 21, 22, 25, 30 |
| `Tok.FillCardDefault` | `#FFFFFFB3` / `#FFFFFF0D` | colour | `PaletteBuilder.cs:416, 304` | 00, 02, 05, 07, 08, 09, 10, 11, 12, 13, 15, 17, 21, 26, 27, 28 |
| `Tok.FillCardSecondary` | `#F6F6F680` / `#FFFFFF08` | colour | `PaletteBuilder.cs:417, 305` | 00, 01, 03, 05, 08, 09, 10, 11, 13, 16, 17, 21, 23, 26, 27, 28 |
| `Tok.FillControlAltSecondary` | `#00000006` / `#00000019` — the DARK arm is a BLACK wash, not a white one | colour | `BuildWinUILight():461` / `BuildDark():350` | 12, 21 |
| `Tok.FillControlDefault` | computed `card` / `#FFFFFF0F` | colour | `BuildWinUILight():404` / `BuildDark():293` | 02, 07, 11, 13, 17, 19, 20 |
| `Tok.FillControlSecondary` | computed `controlHover` / `#FFFFFF15` | colour | `BuildWinUILight():405` / `BuildDark():294` | 01, 09, 11, 13, 16 |
| `Tok.FillControlSolid` | `#FFFFFF` / `#454545` | colour | `BuildWinUILight():410` / `BuildDark():299` | 11, 15, 17, 20 |
| `Tok.FillControlStrong` | `#00000072` / `#FFFFFF8B` — the same pair as `Tok.StrokeControlStrongDefault`, used as a FILL | colour | `BuildWinUILight():408` / `BuildDark():297` | 15, 20 |
| `Tok.FillLayerAlt` | `#FFFFFF` (OPAQUE) / `#FFFFFF0D` — the light arm is fully opaque white, unlike `FillLayerDefault`'s `#FFFFFF80` | colour | `BuildWinUILight():418` / `BuildDark():307` | 19, 28, 29 |
| `Tok.FillLayerDefault` | `#FFFFFF80` / `#3A3A3A4C` | colour | `PaletteBuilder.cs:417, 306` | 00, 03, 05, 06, 07, 08, 09, 10, 11, 15, 16, 17, 30 |
| `Tok.FillSmoke` | `#0000004D` — both themes | colour | `BuildWinUILight():424` / `BuildDark():313` | 18, 19, 28 |
| `Tok.FillSolidBase` | `#F3F3F3` / `#202020` (`Tinted(0.125, ·, 0)`) | colour | `PaletteBuilder.cs:420, 290` | 00, 02, 05, 06, 07, 18, 19, 28 |
| `Tok.FillSolidSecondary` | `#EEEEEE` / `ColorRamp.Darken(canvas, 0.08)` — literal in light, COMPUTED in dark | colour | `BuildWinUILight():422` / `BuildDark():311` | 29 |
| `Tok.FillSolidTertiary` | `#F9F9F9` / — | colour | `PaletteBuilder.cs:423` | 00, 01, 19, 28 |
| `Tok.FillSubtleSecondary` (= `WaveeColors.ChromeHover`) | `#00000009` / `#FFFFFF0F` | colour | `PaletteBuilder.cs:214, 302` | 22 ch — all but 04, 06, 07, 22, 23, 27, 30 |
| `Tok.FillSubtleTertiary` (= `ChromePressed`, dark `RowZebra`) | `#00000006` / `#FFFFFF0A` | colour | `PaletteBuilder.cs:215, 303` | 00, 01, 02, 04, 05, 09, 12, 13, 15, 16, 20, 25, 26 |
| `Tok.FocusOuter` | `#000000E4` / `#FFFFFF` | colour | `BuildWinUILight():453` / `BuildDark():342` | 27 |
| `Tok.MediaLetterbox` | `#000000` pure black — **theme-invariant**; the letterbox / pillarbox bars, one rung below `MediaStage`'s `#0A0A0A` | colour | `Dsl/Tokens.cs:372` | 09, 21, 24 |
| `Tok.MediaScrim` | black @ 0.55 (140) — **theme-invariant** | colour | `Dsl/Tokens.cs:367` | 00, 02, 28 |
| `Tok.MediaStage` | `#0A0A0A` — **theme-invariant** | colour | `Dsl/Tokens.cs:370` | 00, 02, 21, 22, 24 |
| `Tok.NeutralPalette` | the only palette Wavee ships (`WaveeTheme.ResolvePalette()`) | palette | `WaveeTheme.cs:15` | 00, 15, 24, 30 |
| `Tok.OnMediaPrimary / Secondary / Tertiary` | white 1.00 / 0.80 / 0.60 — **theme-invariant** | colour | `Dsl/Tokens.cs:361-365` | 00, 02, 21, 24, 28 |
| `Tok.ScrimBottom` | linear 90°: transparent → transparent @0.36 → `#0000004C` @0.66 → `#000000E0` @1 | gradient | `Dsl/Tokens.cs:376-382` | 24 |
| `Tok.ScrimTop` | linear 90°: `#00000099` → `#00000033` @0.35 → transparent — the gentler mirror of `ScrimBottom` | gradient | `Dsl/Tokens.cs:385-390` | 24 |
| `Tok.StrokeCardDefault` | `#0000000F` / `#00000019` | colour | `PaletteBuilder.cs:427, 316` | 25 ch — all but 06, 08, 22, 23 |
| `Tok.StrokeControlDefault` | `#00000017` / — | colour | `PaletteBuilder.cs:425` | 00, 06, 10, 11, 13, 20, 27 |
| `Tok.StrokeControlStrongDefault` | `#00000072` / `#FFFFFF8B` | colour | `BuildWinUILight():465` / `BuildDark():354` | 27 |
| `Tok.StrokeDividerDefault` | `#0000000F` / `#FFFFFF15` | colour | `PaletteBuilder.cs:428, 317` | 23 ch — all but 02, 06, 07, 22, 23, 24 |
| `Tok.StrokeFlyoutDefault` | `#00000017` / `#00000033` | colour | `BuildWinUILight():430` / `BuildDark():319` | 19 |
| `Tok.StrokeSurfaceDefault` | `#75757566` — both themes | colour | `BuildWinUILight():429` / `BuildDark():318` | 17, 19, 24, 28 |
| `Tok.SystemFillAttention` | `= Tok.AccentDefault` — hand-written, follows the live OS accent; **there is no `TokenSet` field for it** | colour | `Dsl/Tokens.cs:467` | 19 |
| `Tok.SystemFillAttentionBackground` | `#F6F6F680` / `#FFFFFF08` | colour | `BuildWinUILight():478` / `BuildDark():367` | 19 |
| `Tok.SystemFillCaution` | `#9D5D00` / `#FCE100` | colour | `BuildWinUILight():473` / `BuildDark():362` | 02, 12, 19, 28 |
| `Tok.SystemFillCautionBackground` | `#FFF4CE` / `#433519` | colour | `BuildWinUILight():476` / `BuildDark():365` | 02, 12, 19, 28 |
| `Tok.SystemFillCritical` | `#C42B1C` / `#FF99A4` | colour | `BuildWinUILight():472` / `BuildDark():361` | 01, 04, 17, 19, 20, 26, 27, 28 |
| `Tok.SystemFillCriticalBackground` | `#FDE7E9` / `#442726` | colour | `BuildWinUILight():475` / `BuildDark():364` | 19, 28 |
| `Tok.SystemFillSuccess` | `#0C6B0C` / `#6CCB5F` | colour | `PaletteBuilder.cs:474, 363` | 00, 01, 04, 11, 13, 16, 19, 26, 28 |
| `Tok.SystemFillSuccessBackground` | `#DFF6DD` / `#393D1B` | colour | `BuildWinUILight():477` / `BuildDark():366` | 01, 11, 28 |
| `Tok.TextDisabled` | `#0000005C` / — | colour | `PaletteBuilder.cs:440` | 00, 01, 16, 18, 20, 26 |
| `Tok.TextInverse` | `#FFFFFF` / `#000000E4` | colour | `BuildWinUILight():480` / `BuildDark():369` | 19 |
| `Tok.TextOnAccentPrimary` | `#FFFFFF` / `#000000` | colour | `BuildWinUILight():438` / `BuildDark():327` | 02, 09, 10, 11, 12, 15, 21, 24, 25, 28 |
| `Tok.TextOnAccentSelectedText` | the search pill's ink | colour | `SearchHighlight.cs:50` | 00, 01, 02, 12, 13, 15 |
| `Tok.TextPrimary` | `#000000E4` / `#FFFFFF` | colour | `PaletteBuilder.cs:437, 323` | 25 ch — all but 01, 04, 23, 30 |
| `Tok.TextSecondary` | `#0000009E` / `#FFFFFFC5` | colour | `PaletteBuilder.cs:438, 324` | 26 ch — all but 01, 07, 23 |
| `Tok.TextTertiary` | solved to AA against the lightest host / `#FFFFFF87` | colour | `PaletteBuilder.cs:398-400, 325` | 23 ch — all but 08, 17, 22, 23, 24, 30 |
| `Tok.Theme` | `ThemeKind.Light` / `.Dark` — in the tint publish `DepKey` on purpose | enum | engine `Dsl/Tokens.cs` | 00, 01, 04, 06, 09, 13, 21, 24, 27 |
| `WaveeAccent.Action` | `Tok.AccentDefault` — role 1, the SOLID plate behind on-accent ink | accent role | `WaveeTokens.cs:43` | 00 |
| `WaveeAccent.Decor` | `Tok.AccentTextPrimary` — role 3, accent as CONTENT colour | accent role | `WaveeTokens.cs:50` | 00, 08, 09, 11, 12, 17, 20, 26 |
| `WaveeAccent.Selection` | `Tok.AccentDefault` — role 2, "you are here"; the bar/pill geometry is reserved to it | accent role | `WaveeTokens.cs:46` | 00 |
| `WaveeColors.Badge` | `Tok.AccentDefault` — both themes | colour | `WaveeTokens.cs:278` | 00 |
| `WaveeColors.ChromeHover` | `Tok.FillSubtleSecondary` | colour | `WaveeTokens.cs:276` | 00 |
| `WaveeColors.ChromePressed` | `Tok.FillSubtleTertiary` | colour | `WaveeTokens.cs:277` | 00 |
| `WaveeColors.Content` / `.ContentAlt` | `#FFFFFF80` / `#3A3A3A4C` · `#F6F6F680` / `#FFFFFF08` | colour | `WaveeTokens.cs:212-213` | 00 |
| `WaveeColors.ContentLayer` | white @ α 170 (2/3) / white @ α 9 (0.0364) | colour (layer) | `WaveeTokens.cs:195-210` | 00 |
| `WaveeColors.ContentSurface` (= `FloatingPane`) | `#F9F9F9` / `#282828` | colour | `WaveeTokens.cs:181-188` | 00, 21 |
| `WaveeColors.FileArea` | `#FFFFFF80` / `#3A3A3A4C` — stock `LayerFillColorDefault`, TRANSLUCENT | colour | `WaveeTokens.cs:140` | 00, 18, 21 |
| `WaveeColors.FloatingChrome` (= `ShellGround`) | `#EDEDED` / `#202020` | colour | `WaveeTokens.cs:133` | 00, 18, 21 |
| `WaveeColors.PremiumText` | `Tok.SystemFillSuccess` `#0C6B0C` / `#1DB954` | colour | `WaveeTokens.cs:120` | 00, 02, 13 |
| `WaveeColors.RowHover` | `#0000000D` (5.1 %) / `#FFFFFF0F` | colour | `WaveeTokens.cs:232`; `PaletteBuilder.cs:115, 158` | 00, 01, 05, 08, 19, 21 |
| `WaveeColors.RowHoverZebra` | `Over(RowHover, RowZebra)` — composed, never two stacked plates | colour | `WaveeTokens.cs:239` | 00 |
| `WaveeColors.RowPressed` | `#00000012` (7.1 %) / `#FFFFFF0A` | colour | `WaveeTokens.cs:233`; `PaletteBuilder.cs:116, 160` | 00, 19, 21 |
| `WaveeColors.RowPressedZebra` | `Over(RowPressed, RowZebra)` | colour | `WaveeTokens.cs:242` | 00 |
| `WaveeColors.RowZebra` | `#00000008` (3.1 %) / `Tok.FillSubtleTertiary` `#FFFFFF0A` (app override) | colour | `WaveeTokens.cs:230` | 00, 01, 04, 05, 30 |
| `WaveeColors.SelectedHover` | `Over(FillSubtleSecondary, AccentSubtle)` | colour | `WaveeTokens.cs:273` | 00, 07, 27 |
| `WaveeColors.SelectedPressed` | `Over(FillSubtleTertiary, AccentSubtle)` | colour | `WaveeTokens.cs:274` | 00 |
| `WaveeColors.SelectedRest` | `Tok.AccentSubtle` | colour | `WaveeTokens.cs:272` | 00, 25, 26 |
| `WaveeColors.ShellGround` | `#EDEDED` (= `MicaRef.LightDefault`) / `#202020` | colour | `WaveeTokens.cs:163-170` | 00, 03, 10, 18, 30 |
| `WaveeColors.Toolbar` / `.Sidebar` / `.PlayerBar` | `#FFFFFFB3` / `#3A3A3A73` — published, and **unpainted** by the merged shell | colour | `WaveeTokens.cs:127-128, 139` | 00 |
| `WaveeCta.IconButtonSize` | 32 (`= WaveeSize.ControlH` by construction) | size | `WaveeCta.cs:61` | 00, 02, 06, 07, 21, 22, 30 |
| `WaveeCta.PillHeight` | **36** — one step above the 32-DIP control ladder | size | `WaveeCta.cs:65` | 00, 01, 03, 05, 06, 09, 11 |
| `WaveeCta.TextActionLineHeight` | 20 | size | `WaveeCta.cs:161` | 00 |
| `WaveeCta.TextActionSize` | 14 | size | `WaveeCta.cs:159` | 00 |
| `WaveeEntrance.DelayMs(i)` | `min(i, 8) × 40` ms; **0** under reduced motion | duration | `WaveeMotion.cs:121-122` | 00, 02, 07, 16 |
| `WaveeEntrance.RiseDip` | 8 (`= Expressive.DistBase`) | distance | `WaveeMotion.cs:110` | 00 |
| `WaveeEntrance.Row(i)` | the Rise spec + `DelayMs(i)` — Tween `Expressive.Slow` 400 / `Easing.SmoothOut`, `Channels = Opacity`, `Blur = BlurSmall` | recipe | `WaveeMotion.cs:115-126` | 02, 13, 21 |
| `WaveeEntrance.StaggerCap` | 8 ⇒ the whole cascade is bounded at **8 × 40 = 320 ms** (the source comment's "360 ms" is the drift §9.6 records) | index cap | `WaveeMotion.cs:106` | 00, 16, 21 |
| `WaveeMotion.Fast` | **167 ms** — WinUI `ControlFastAnimationDuration` | duration | `WaveeMotion.cs:57` | 20 ch — all but 00, 05, 07, 10, 11, 19, 23, 26, 30 |
| `WaveeMotion.Faster` | **83 ms** — WinUI `ControlFasterAnimationDuration`, the brush cross-fade | duration | `WaveeMotion.cs:55` | 02, 03, 06, 09, 15, 16, 18, 20, 21, 22, 24, 27, 28 |
| `WaveeMotion.MastheadStaggerMs` | 45 ms — display line → metadata line; uncapped because it offsets exactly TWO lines | duration | `WaveeMotion.cs:75` | 00, 01, 03, 04, 05, 09, 16 |
| `WaveeMotion.ScaleEmphatic` | hover 1.07 / press 0.92 — media FABs, transport, on-artwork circles, the "…" corner | scale tier | `WaveeMotion.cs:48` | 18 ch — all but 00, 07, 12, 16, 17, 19, 23, 25, 26, 27, 30 |
| `WaveeMotion.ScaleStandard` | hover 1.04 / press 0.96 — buttons, CTAs, pills, secondary circles | scale tier | `WaveeMotion.cs:43` | 00, 01, 02, 05, 06, 08, 11, 13, 15, 28 |
| `WaveeMotion.ScaleSubtle` | hover 1.02 / press 0.98 — chips, small toggles, settings rows, inline pickers. **Never a near-full-width row** | scale tier | `WaveeMotion.cs:39` | 00, 01, 02, 04, 05, 06, 07, 13, 20, 21, 22, 27 |
| `WaveeMotion.StaggerMs` | 40 ms per item | duration | `WaveeMotion.cs:64` | 00, 01, 03, 04, 05, 09, 16, 21, 28 |
| `WaveeMotion.Standard` | **250 ms** — recolor, icon swap, material cross-fade | duration | `WaveeMotion.cs:59` | 00, 03, 05, 10, 16, 18, 28, 30 |
| `WaveePicker.CoverMini` | 92 × auto (76 miniature), inset 8, gap 5, r 8 | card shell | `WaveePicker.cs:52` | 00, 30 |
| `WaveePicker.Ink.For` | selected `AccentDefault` / @0.45 · unselected @0.58 / @0.22 | colour | `WaveePicker.cs:32-34` | 00, 30 |
| `WaveePicker.Pane` / `.PaneCompact` | 224 / 200 × auto, inset 10, gap 7, r 8 | card shell | `WaveePicker.cs:46, 48` | 00, 30 |
| `WaveePicker.Strip` | grid gap 12, `Wrap = true`, focus ring inset 2 | layout | `WaveePicker.cs:238-254` | 00, 30 |
| `WaveePicker.Tile` | 116 × 84, inset 8 (7 selected), gap 4, r 8, label 12 / 16 | card shell | `WaveePicker.cs:44, 63-94` | 00, 30 |
| `WaveePicker.WideRow` | 480 × 100, inset 8, gap 0, r 8 | card shell | `WaveePicker.cs:56` | 00, 30 |
| `WaveeSize.ArtNowPlaying` | 64 | size | `WaveeTokens.cs:58` | 00 |
| `WaveeSize.ArtPlayerBar` | 48 | size | `WaveeTokens.cs:58` | 00 |
| `WaveeSize.ArtThumb` | 40 | size | `WaveeTokens.cs:58` | 00, 03, 05, 06, 09, 30 |
| `WaveeSize.ControlH` | 32 | size | `WaveeTokens.cs:56` | 00, 01, 02, 17, 19, 24 |
| `WaveeSize.NavCompactW` | 56 | size | `WaveeTokens.cs:57` | 00 |
| `WaveeSize.NavItemH` | 44 | size | `WaveeTokens.cs:56` | 00, 21, 22 |
| `WaveeSize.NavPaneW` | 240 (= WinUI `OpenPaneLength`) | size | `WaveeTokens.cs:57` | 00 |
| `WaveeSize.PageMaxW` | 1600 | size | `WaveeTokens.cs:75` | 00, 08, 10, 27, 30 |
| `WaveeSize.PlayerBarH` | 72 | size | `WaveeTokens.cs:56` | 00, 19, 20, 21, 22 |
| `WaveeSize.RailAlbum` | 280 | size | `WaveeTokens.cs:60` | 00, 05, 30 |
| `WaveeSize.RailCard` | 180 | size | `WaveeTokens.cs:57` | 00 |
| `WaveeSize.RailPlaylist` | 240 | size | `WaveeTokens.cs:60` | 00, 06, 07, 30 |
| `WaveeSize.SectionGap` / `.SectionGapWide` | 32 / 40 | spacing | `WaveeTokens.cs:70` | 00, 10 |
| `WaveeSize.Thumb32 … Thumb64` | 32 · 40 · 48 · 56 · 64 — the ONLY sizes an in-content cover / avatar may take | size ladder | `WaveeTokens.cs:65` | 00, 01, 02 |
| `WaveeSize.TrackRowH` | 56 — the density ladder overrides it (Modern 40/48/56/64, Classic 36/40/44/48) | size | `WaveeTokens.cs:56`; `DetailTrackTableRules.cs:45-47` | 00, 21 |
| `WaveeType.ArtistCompactTitle` | 32 / 40 / **700**, Display face, tracking −12, MinSize 28 | type style | `WaveeType.cs:179-187` | 00, 08, 10, 11 |
| `WaveeType.ArtistDisplay` | 84 / 96 / **700**, Display face, tracking −28, MinSize 68 | type style | `WaveeType.cs:155-163` | 00, 08 |
| `WaveeType.ArtistTitle` | 48 / 60 / **700**, Display face, tracking −20, MinSize 40 | type style | `WaveeType.cs:167-175` | 00, 08, 10, 11 |
| `WaveeType.CardTitle` | 14 / 20 / 600 (`Ui.BodyStrong`) — the same rung as `TrackTitle`, named for the surface | type style | `WaveeType.cs:28` | 00, 02, 09, 13 |
| `WaveeType.DetailHero` | 28 / 36 / 600, Display face, tracking −20; the shell swaps to 40 / 52 when `winH ≥ 900` | type style | `WaveeType.cs:134-138`; `DetailShell.cs:590` | 00, 03, 05, 06, 07, 09, 27, 30 |
| `WaveeType.Eyebrow` | 12 / 16 / 600 + `EyebrowTracking`; **sentence case, never `.ToUpper()`**; the call site owns colour | type style | `WaveeType.cs:54` | 23 ch — all but 20, 22, 23, 24, 25, 27 |
| `WaveeType.EyebrowTracking` | 30 / 1000 em — the ONE tracking every eyebrow carries | tracking | `WaveeType.cs:38` | 00, 04 |
| `WaveeType.FoldTitle` | `= PickQuote` — 28 / 36 / **400**, Display face, tracking −12 | type style | `WaveeType.cs:212` | 00, 10, 11 |
| `WaveeType.ModuleHeader(s)` | 20 / 28 / 600, Display face, tracking −6 | type style | `WaveeType.cs:63-67` | 00, 10, 11, 13, 16 |
| `WaveeType.ModuleHeader(title, meta)` | same + a baseline-shared 12 / 16 `Tok.TextTertiary` run after two spaces | type style | `WaveeType.cs:74-99` | 00, 10, 11 |
| `WaveeType.NowPlayingTitle` | 20 / 28 / 600 (`Ui.Subtitle`) | type style | `WaveeType.cs:190` | 00, 09, 20, 21 |
| `WaveeType.NpvLyric` | 20 / 28 / **350** SemiLight, Display face, tracking −6 | type style | `WaveeType.cs:230-235` | 00, 21, 22 |
| `WaveeType.PageHero` | 28 / 36 / 600 (`Ui.Title`) | type style | `WaveeType.cs:128` | 00, 02, 03, 07, 08, 09, 10, 11, 12, 13, 15, 16, 17, 18, 27, 28, 30 |
| `WaveeType.PickQuote` | 28 / 36 / **400**, Display face, tracking −12 | type style | `WaveeType.cs:198-203` | 00, 02, 08, 10 |
| `WaveeType.PivotLabel` | 19 / 25 / **350** SemiLight, Display face, tracking −6 — **off the engine ramp** | type style | `WaveeType.cs:219-226` | 00, 16 |
| `WaveeType.RailHeader(s)` | 20 / 28 / 600 (`Ui.Subtitle`), UI face | type style | `WaveeType.cs:57` | 00, 03, 05, 08, 09, 13, 21, 22 |
| `WaveeType.RailHeader(title, meta)` | same + a baseline-shared 12 / 16 `Tok.TextTertiary` run | type style | `WaveeType.cs:103-125` | 00, 05 |
| `WaveeType.StatHero(value, unit)` | 28 / 36 / **350**, Display face, tracking −6; unit run 12 / 16 `Tok.TextSecondary` on the same baseline | type style | `WaveeType.cs:254-282` | 00, 01, 04 |
| `WaveeType.SurfaceDisplay` | 40 / 52 / **400**, Display face, tracking −12 (`Ui.TitleLarge`) | type style | `WaveeType.cs:146-151` | 00, 08, 12, 13, 16, 18 |
| `WaveeType.TrackMeta` | 12 / 16 / 400 (`Ui.Caption`), bound `Tok.TextSecondary` | type style | `WaveeType.cs:31` | 00, 02, 03, 06, 08, 09, 10, 12, 13, 16, 17, 21 |
| `WaveeType.TrackTitle` | 14 / 20 / 600 (`Ui.BodyStrong`) | type style | `WaveeType.cs:23` | 00, 02, 03, 05, 06, 08, 09, 12, 13, 16, 17 |

**Used by a chapter, defined nowhere** — the only two names in the whole contract that resolve to no declaration:

| name | asked for by | proposed value | status |
|---|---|---|---|
| `WaveeType.EpisodeCardTitle` | `09-show-episode-module.md:1202` | 14 / 20 / **700** | **0.3 proposal, unbuilt.** 0.2.9 authors the pixels as a raw `TextEl { Size = … }` in `EpisodeList.cs`, against `WaveeType.cs:6-19`'s "never author a raw size" rule AND its 400/600 weight policy. A 700 alias would be a FOURTH sanctioned weight divergence, so it needs the same explicit blessing `ArtistDisplay` / `PivotLabel` carry — or the pixels change. |
| `WaveeType.EpisodeBannerTitle` | `09-show-episode-module.md:1202` | 15 / 20 / **700** | Same, plus **15 px is off the engine type ramp entirely** (12 · 14 · 18 · 20 · 28 · 40 · 68). Snap it to 14, or record it as a second off-ramp rung beside `PivotLabel`'s 19 / 25 — do not let it in unlabelled. |

Every other token any chapter names resolves to a declaration in the table above.

### 12.2 Magic numbers to name in 0.3

Values that are repeated verbatim across two or more 0.2.9 files with no token behind them. Each row lists **every**
usage site found, so the 0.3 port can convert them in one pass instead of discovering the fifth copy after the fourth
has already drifted. Ordered by how far the copies have already diverged.

| the value(s) | what it actually is | every 0.2.9 usage site | proposed 0.3 name |
|---|---|---|---|
| **280 × 360 outer · 264 × 344 inner · pad 8 · row gap 2 · row h 44** | the "…and N more" attribution flyout — a fixed-width list of people hung off a face pile | `ArtistFacePile.cs:169` (row h 44), `:182` (264 / gap 2), `:185` (280 × 360), `:187` (264 × **344**) · `CollaboratorFacePile.cs:142, 154, 157, 159` (identical) · `LikedFactsPanel.cs:763, 784, 787, 792` — identical **except the inner `MaxHeight` is 320, not 344**, with nothing explaining the 24 DIP · `ConcertDateFlyout.cs:299` (264 / gap 2, the outer authored separately) | one `WaveeSize.FacePileFlyout` record (`W 280 · H 360 · ListW 264 · ListH 344 · RowH` → `WaveeSize.NavItemH`). Four copies of a nine-number shape is exactly how the 320 got in. |
| **12 · 16 · 24 · 36 · clamp(h × 0.28, 120, 180)** | the edge-fade band width — how far a scroll viewport feathers its clipped edge | `HomeModules.cs:506` `HomeModuleLayout.ShelfEdgeFade = 24f`, read at `:177, :334, :356, :372, :413` and `BrowsePage.cs:673` · `ArtistPopular.cs:179` a bare `edgeFade: 16f` · `Rail.cs:49` takes the **engine default 36** · `ConcertUi.cs:385` a bare `EdgeFadeSpec(Bottom, …)` · `ArtistPage.Hero.cs:107` the photo's `clamp(h × 0.28, 120, 180)` · plus the per-feature feeds `BrowseMastheadMetrics.*` (`BrowseDirectoryPage.cs:46`, `ArtistSchedulePage.cs:106`, `ConcertDetailPage.cs:83`, `ConcertHubPage.cs:119, 140`), `ContextBand*` (`ArtistPage.cs:321`) and `DetailVertical*` (`DetailTracks.cs:1819`, `ArtistPage.AlbumExpand.cs:742`) | a `WaveeFade` ladder — `Chip 16 · Shelf 24 · Rail 36` — with the hero photograph's clamp left as its own thing. `02-cards-and-controls.md:676-677` already records two of these bands colliding inside ONE chapter, which is the tell. |
| **`#ED6B73` = `new ColorF(0.93f, 0.42f, 0.45f, 1f)`** | the transport's error ink — a hand-mixed red | `PlayerBar.cs:89` (the declaration), read at `:270-282` and `:732-739`. Documented as an "app-local literal" by `20-player-bar.md:613` | delete it. `Tok.SystemFillCritical` (`#C42B1C` / `#FF99A4`) already exists and is what eight other chapters' error surfaces take. If the dock genuinely needs a softer red over Mica, it is `WaveeColors.TransportError` with a written reason — not an unnamed `ColorF` triple. |
| **148 / 188 / 200** | the horizontal shelf's card-width band | `HomeModules.cs:504-505` `ShelfCardMin = 148f` / `ShelfCardMax = 188f`, read by every Home shelf and re-read by `SearchPage.cs:1080-1089` · the artist discography shelf authors its own `maxCardW 200` (`08-artist-and-discography.md:589`) | `WaveeSize.ShelfCardMin / ShelfCardMax` in the token layer, so a fourth shelf cannot invent a fifth width. The 200 is either a deliberate exception that needs saying, or a copy that drifted. |
| **44** (a full-width tappable list row) | the flyout / face-pile row height | `ArtistFacePile.cs:169` · `CollaboratorFacePile.cs:142` · `LikedFactsPanel.cs:763` — three bare `Height = 44f` | `WaveeSize.NavItemH` is **already 44** and already means exactly this. Point the three at it; no new token needed. |
| **333 ms + 28 DIP** (the multi-select check lane) | the check-lane slide-in: a 333 ms `FluentDecelerate` tween and a 28-DIP content offset. The engine ALREADY names both — `SelectorVisualsBound.MultiSelectAnimMs = 333f` and `CheckboxContentOffset = 28f` (engine `FluentGpu.Controls/SelectorVisualsBound.cs:59, 61`) | the engine reads its own constants at `SelectorVisualsBound.cs:88, 122`; the APP re-authors the 333 raw at `TrackRow.cs:569` (`TransitionDynamics.Tween(333f, Easing.FluentDecelerate)`) · and the chapters transcribe both numbers rather than either name: `01-track-row.md:1019, 1020, 1032` · `04-detail-track-table.md:652, 653` | point `TrackRow.cs:569` at `SelectorVisualsBound.MultiSelectAnimMs`, and have the two chapters cite the two engine constants. **333 here is NOT `MotionTok.DisclosureExpand`** — that is also 333 ms but pairs `FluentPopOpen`, so a reader who matches the bare number to the token gets the wrong curve. This is the one collision in the contract where a raw value and a token share a number and differ in behaviour. |
| **28 · 52 · 90 · 120** | the track table's fixed lane widths and star-lane floors | named locally at `DetailTrackTableRules.cs:235` (`Num 28`), `:241` (`Heart 28`), `:253` (`Plays 52`), `:257` (`Duration 52`), `:259` (`Video 28`), `:274` (`TitleFloor 120`), `:276` (`ArtistFloor 90`), `:278` (`AlbumFloor 90`) — but because they are NOT in the token layer, the chapters re-author them as raw numbers: `01-track-row.md:398, 411, 808, 899, 904-905` · `04-detail-track-table.md:43, 269` · `30-appearance-preferences.md:850` | promote (or re-export) them through `WaveeSize`, so a wireframe can cite a token instead of transcribing four numbers. They are the only geometry in the app that a chapter cannot name. |

**Not on this list, deliberately.** `0.55` (`Surfaces.TintStrength`), `80` (`ShimmerMinEdge`), `2048` / `8`
(`ImageDecodeScale`), `20 × 2 + 2` (the `AccentRule` trio), `0.46` (`StageLayout.ScrimBaseA`) and `3.25 / 3.55 / 4.5 /
0.50 / 0.12 / 0.08` (the `WaveePalette` thresholds) all LOOK like magic numbers in a chapter's §3 table and are not:
each has exactly one named declaration with a written reason, and every chapter that cites one cites that name. They
are the shape the six rows above should end up in.
