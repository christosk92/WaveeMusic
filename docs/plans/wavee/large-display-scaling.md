# Wavee on large displays — a scaling design

Status: **design / recommendation**. No code changed by this document.
Companion prototype: `docs/plans/wavee/large-display-scaling-mica.html`.

---

## 0. TL;DR

The app-level zoom the complaint asks for **already exists, already works, and already covers every
surface** — `ZoomLadder` + `FluentApp.SetZoom` + `Win32Platform._scale = _rawDpiScale × _zoom`. Wavee is
also already per-monitor-DPI-v2 correct. Nothing is broken in the scaling machinery.

What is broken is the **default**: the zoom is a single manual number, monitor-blind, seeded at 100 %, and
it survives a monitor hop on purpose. The user's two displays differ by **2.07× in DIP width** (1664 vs
3440), so no single persisted zoom can be right on both — and the one the app ships (100 %) is right only
on the laptop.

**Recommendation: make the existing zoom auto-derive from the display, and fix three surfaces that a
uniform multiplier provably cannot fix.** Do *not* build a per-surface size-tier ladder. Concretely:

| Layer | What | Blast radius |
|---|---|---|
| **A** | New pure class `ZoomAutoPolicy` + an `Auto` head on the zoom ladder; seeded at window create, re-evaluated on monitor hop / resize-settle | ~3 call sites, 1 new setting, 1 new test file. **Zero existing constants change.** |
| **B** | Lyrics rung ladder (the `_large` boolean → 3 rungs + a measure cap) | 1 file, 1 new pure helper, 1 test file |
| **C0** | **Blocker for A** — route image decode budgets through `Viewport.Scale`, or the auto-zoom ships soft cover art | every `UseImage` call site with a literal budget |
| **C** | A literal that is wrong past 2400 DIP (`TopEdgeWidth`), and a tier gate that Auto lands just under on 1440p (`NavPaneWideEnterW`) | 2 constants, 2 test files |
| **D** | Honour `UISettings.TextScaleFactor` as a **type-only** multiplier in the engine | engine-side; a separate piece of work |

---

## 1. The facts, read off the code

### 1.1 DPI is already correct — this is not a DPI bug

`..\fluent-gpu\src\FluentGpu.Windows\Pal\Win32Platform.cs`

```
:16   private static partial int SetProcessDpiAwarenessContext(nint value);
:35   SetProcessDpiAwarenessContext(unchecked((nint)(-4)));   // PER_MONITOR_AWARE_V2

:595  // The EFFECTIVE scale (px per engine DIP) = _rawDpiScale × _zoom — every engine DIP↔px conversion
:600  private float _rawDpiScale = 1f;   // the OS per-monitor DPI scale alone (dpi/96)
:601  private float _zoom = 1f;          // the browser-style app zoom; survives monitor hops
:739  _rawDpiScale = dpi == 0 ? 1f : dpi / 96f;
:741  _scale = _rawDpiScale * _zoom;
:799  /// <summary>The EFFECTIVE scale (px per engine DIP): OS per-monitor DPI scale × app Zoom.</summary>
:1781 _rawDpiScale = ((uint)wParam & 0xFFFF) / 96f;   // WM_DPICHANGED
:1782 _scale = _rawDpiScale * _zoom;                  // app zoom survives the monitor hop
```

`ZoomLadder`'s own header states the contract explicitly:

> The effective window scale is `OS DPI scale × zoom` (`IPlatformWindow.Scale`); **everything downstream
> (layout DIP viewport, glyph raster, damage, popups, IME, input DIP conversion) consumes that one
> product.**

So there is exactly **one** choke point, it already exists, and it already reaches layout, raster **and**
input hit-testing. This is the "one global scale factor" answer, and it is already shipped.

Three structural facts that constrain everything below:

- **Layout is scale-independent.** Nothing in `..\fluent-gpu\src\FluentGpu.Engine\Layout\` reads DPI,
  `Viewport.Scale`, `FrameInfo.Scale` or `IPlatformWindow.Scale`; the `Seams\Text\` interfaces take no scale
  parameter at all. Scale enters in exactly two places: the DIP viewport divide
  (`AppHost.ClientSizeDip()` = `ClientSizePx / Scale`, `AppHost.cs:4818`) and the raster
  (`D3D12Device._frameScale` → the logical viewport `lw = _w / _frameScale`, `D3D12Device.cs:1440–1473`;
  `GlyphRenderer.cs:485` — `advance` in DIP, `physEm = size * dpiScale`).
- **There is deliberately no root visual transform.** No root `Matrix3x2.CreateScale`, no `SetRootScale`, no
  `Window.Content` wrapper. Adding one would **double-apply** against `lw/lh` and desync hit-testing. The
  sanctioned way to inject an app-level multiplier is to fold it into `Scale` — which `SetZoom` already does.
- **The raw-vs-effective split is already correct.** `Win32Platform` deliberately uses `_rawDpiScale` for OS
  chrome (frame outsets `:781`, `WM_GETMINMAXINFO` min-track `:1732`, the initial window size `:745`) and
  `_scale` for everything engine-DIP-facing (`ScreenPtToDip :2366`, `WheelPt :2374`, NC hit-test `:1009`,
  IME `:721`, OLE drop `:758`, touchpad calibration `:1364`). Zoom therefore already cannot resize the OS
  frame, and already lands pointer input in the right DIP. **Nothing in this split needs work.**

### 1.2 The measured hardware — and why this is a *proportion* problem

| Display | Physical px | OS scale | DIPs at zoom 1 | PPI |
|---|---|---|---|---|
| `\\.\DISPLAY2` (laptop, primary) | 2496 × 1664 | **150 %** | **1664 × 1109** | ~207 |
| `\\.\DISPLAY1` (34" ultrawide) | 3440 × 1440 | **100 %** | **3440 × 1440** | ~109 |

Wavee runs on DISPLAY1 with a client area of **3440 × 1392 px = 3440 × 1392 DIP**.

A DIP on the ultrawide is **physically larger** than on the laptop — ≈ 0.233 mm vs ≈ 0.184 mm — so nothing
is literally smaller in millimetres. Windows' scaling model is doing exactly what it promises: *physical
size constancy* ([effective pixels and scale
factor](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design)).

What changed is **proportion**. The window has 2.07× the DIP width it has on the laptop, and every constant
in the app is a fixed DIP literal, so the 72-DIP player bar and the 14-DIP row text now occupy **half the
relative area they were designed for**. The player bar is 5.2 % of window width on the laptop and 2.5 % on
the ultrawide. That is an **information-density / proportion** problem, not a DPI-correctness problem — and
it is precisely the axis Windows' effective-pixel model deliberately does *not* address, because DPI
scaling holds physical size constant by design.

### 1.3 The app states its own design width, twice

Two independent numbers in the codebase both say "this app was designed for a ~1600 × 900 DIP window":

- `src/apps/Wavee/Design/WaveeTokens.cs:75` — `public const float PageMaxW = 1600;`
  > *"The widest a page's content column grows before it stops tracking the window. DetailShell and
  > ArtistPage cap their two-column row here; Home caps each feed row at it, so the three pages line up at
  > the same measure on an ultra-wide display."*
  Pinned by `DesignTokenConvergenceTests.PageMeasure_IsOneNumber` (`Assert.Equal(1600f, …)`).
- `src/apps/Wavee/Features/Detail/DetailShell.cs:590` — `float titleSize = winH >= 900f ? 40f : 28f;`
  the app's own "tall window" breakpoint.

**This is the whole design.** At 3440 × 1392 DIP the content column is capped at 1600 and centred, so the
app is spending ~1840 DIP of its width on gutters — the literal, code-level cause of "a small app stretched
wide". At the *right* zoom, `PageMaxW` stops being a cap and becomes a fit.

### 1.4 The zoom is complete — picker, chords, wheel, palette, persistence

| Piece | Where |
|---|---|
| Ladder (Chromium steps 0.5 … 2.5, `Snap`/`In`/`Out`/`Percent`) | `..\fluent-gpu\src\FluentGpu.Engine\Foundation\ZoomLadder.cs` |
| Live setter + `ZoomChanged` event | `..\fluent-gpu\src\FluentGpu.Windows\Hosting\FluentApp.cs:56–84` |
| Window seam (`SetZoom` → re-derive `_scale` → relayout + re-raster) | `Win32Platform.cs:805–815` |
| Startup seed, before the first frame | `src/apps/Wavee/Program.cs:524` — `Zoom = ZoomLadder.Snap(settings.Get(WaveeSettings.ZoomLevel))` |
| Setting key | `src/apps/Wavee/Platform/AppSettings.cs:106` — `appearance.zoom`, default `1f` |
| Settings picker (Appearance › Theme › Zoom) | `src/apps/Wavee/Features/Shell/SettingsPage.Appearance.cs:57–62, 94–117, 160–161` |
| Ctrl+= / Ctrl+− / Ctrl+0, Ctrl+wheel, command palette | `src/apps/Wavee/Features/Shell/WaveeShell.cs:2102–2106` (`ZoomStep`) |
| Ctrl+wheel hook seam | `..\fluent-gpu\src\FluentGpu.Engine\Input\InputDispatcher.cs:717–720, 1109–1115` |
| Debounced persist (2 s timer) | `src/apps/Wavee/WaveeApp.cs:239–243` |
| Diagnostics receipt | `src/apps/Wavee/Features/Shell/SettingsPage.About.cs:294, 320` |

**So why is the user still complaining?** Four reasons, all structural:

1. **It defaults to 100 %.** A user who never opens Appearance › Theme never gets it. Discoverability of a
   Ctrl+± zoom in a music player is near zero — nobody reaches for browser chords in Spotify. (Spotify
   itself ships exactly this and gets exactly this complaint: users on ultrawides still file
   [zoom/scaling requests](https://community.spotify.com/t5/Live-Ideas/Desktop-Zoom-Scaling-setting/idi-p/5638600).)
2. **It is one number for two very different displays**, and it *deliberately* survives a monitor hop
   (`Win32Platform.cs:1782`). The correct zoom is 100 % on the laptop and ~150 % on the ultrawide. A single
   persisted value is wrong on one display by construction. This is the single most important finding.
3. **It has no display-derived suggestion.** Nothing in the app ever looks at the window's DIP extent and
   says "you have 2.07× the room this app was designed for."
4. **It is not reactive.** No surface in the app subscribes to `FluentApp.ZoomChanged` — grep finds exactly
   one hit, a comment in `SettingsPage.About.cs:318` saying *"deliberately no ZoomChanged"* (it polls on a
   5 s tick instead). `SettingsPage.Appearance.cs:97` computes `zoomIndex` from the **live** `FluentApp.Zoom`
   — the right source — but only at build time, and hands the combo a **freshly constructed**
   `new Signal<int>(zoomIndex)` per build (line 161). So the row shows whatever the zoom was the last time
   the tab rebuilt.
   *Answering the coordinator's aside directly:* the stored value and the applied scale **do** come from one
   source — `FluentApp.Zoom` is the live truth, and `WaveeApp`'s timer copies it into `appearance.zoom`.
   The stale "100 %" is not a two-source divergence; it is a missing subscription plus a per-build signal.
   **And the engine already publishes the subscription for exactly this purpose:** `Viewport.Zoom`
   (`..\fluent-gpu\src\FluentGpu.Engine\Hooks\Context.cs:31`), a signal-backed ambient context pushed from
   `AppHost.EnsureSize` (`AppHost.cs:410, 4787`), whose doc says to read it *"only to DISPLAY the level (a
   settings row, a diagnostics receipt), never to convert coordinates."* The fix is therefore one line —
   `UseContext(Viewport.Zoom)` in place of the `FluentApp.Zoom` read — not a hand-rolled `ZoomChanged`
   subscription. The About tab's 5-second poll can go the same way.

### 1.5 What zoom costs: it trades width for size

`Win32Platform.SetZoom`'s own doc: *"The physical window is untouched: the DIP viewport shrinks/grows."*
So on the 3440-px ultrawide:

| Zoom | DIP viewport | `PageMaxW` fit | NavPane tier (Classic) | PlayerBar tier | Detail hero rung |
|---|---|---|---|---|---|
| 100 % | 3440 × 1392 | 1600 of 3440 (**47 %**) | Wide 320 | Full | TitleLarge 40 |
| 125 % | 2752 × 1114 | 1600 of 2752 (58 %) | Wide 320 | Full | TitleLarge 40 |
| **150 %** | **2293 × 928** | **1600 of 2293 (70 %)** | **Wide 320** | **Full** | **TitleLarge 40** |
| 175 % | 1966 × 795 | 1600 of 1966 (81 %) | Wide 320 | Full | Title 28 ← **hero demotes** |
| 200 % | 1720 × 696 | 1600 of 1720 (93 %) | Mid 280 ← **pane demotes** | Full | Title 28 |

This table is the whole argument for a **height-aware** cap. Push zoom too far and the app starts *losing*
structure it had — the tall-hero rung at 900 DIP and the nav-pane wide tier at 1800 DIP both fall off.
150 % is the last rung on this window that keeps every structural promise **and** brings the content
column to 70 % of the viewport. It is not a coincidence: it is what "the design box is 1600 × 900" means.

---

## 2. The research

### 2.1 Two different problems, routinely conflated

Every mature desktop app that solved this shipped **two independent knobs**:

- **DPI / device scale** — physical size constancy. Chromium calls it *device scale factor*; it is display
  density and affects `devicePixelRatio` and `screenX/Y`
  ([Blink coordinate spaces](https://www.chromium.org/developers/design-documents/blink-coordinate-spaces/)).
- **App zoom / information density** — a deliberate choice to show things *bigger* (or *smaller*, to show
  *more*). Chromium's `pageZoomFactor`; it affects `clientX/Y` and `devicePixelRatio` but **not**
  `screenX/Y`.

VS Code splits it as `window.zoomLevel` (whole UI, Electron CSS zoom, 20 % per step) vs `editor.fontSize`
(the text pane only) — *"if you want everything to be larger for better overall visibility, use
`window.zoomLevel`… For large monitors, `window.zoomLevel` would be more suitable"*
([1](https://tms-outsource.com/blog/posts/how-to-zoom-in-vscode/),
[2](https://devgex.com/en/article/00006697)).

**Wavee already has the first knob (OS DPI, correct) and the second knob (`ZoomLadder`, correct).** The
research does not tell us to build anything new. It tells us what the *defaults and affordances* around the
second knob should look like.

### 2.2 What the shipped apps actually do

| App | Mechanism | Steps | Notes |
|---|---|---|---|
| **Slack** | Preferences › **Accessibility › Zoom** | **80 / 100 / 125 / 150 %** | *"Everything in the Slack interface scales: text, icons, buttons, and the sidebar."* Explicitly frames **80 %** as the 27"-and-larger *density* case ([help](https://slack.com/help/articles/236067467-Adjust-your-zoom-level-in-Slack), [walkthrough](https://frontdeskchat.com/books/slack/set-your-preference/adjust-zoom-level-slack/)) |
| **Discord** | Ctrl+± (10 % steps), Ctrl+0 reset; Settings › Appearance sliders | continuous-ish | **Two separate controls**: *Zoom Level* resizes all interface elements; *Chat Font Scaling* changes only chat text ([1](https://filmora.wondershare.com/zoom-video/zoom-in-and-out-on-discord.html), [2](https://techwiser.com/zoom-in-zoom-out-discord/)) |
| **VS Code** | `window.zoomLevel` + `editor.fontSize` | 20 % / step | the canonical split |
| **JetBrains** | **View › Appearance › Zoom IDE** (a discrete % popup with **live hover preview**), plus a separate *Presentation Mode Zoom* (default 175 %) and `-Dide.ui.scale=` ([Rider](https://www.jetbrains.com/help/rider/IDE_zoom_level.html), [HiDPI](https://intellij-support.jetbrains.com/hc/en-us/articles/360007994999-HiDPI-configuration), [presentation](https://www.josephguadagno.net/2026/02/16/jetbrains-rider-settings-for-presentations)) | discrete | the *hover-to-preview* affordance is worth stealing |
| **Figma** | View › Interface Scale (Larger/Smaller/Reset) | discrete | known flaw: **scales the canvas too**, so a UI-only request is still open ([forum](https://forum.figma.com/suggest-a-feature-11/ui-zoom-not-canvas-zoom-18976), [help](https://help.figma.com/hc/en-us/articles/360049549913-Adjust-the-scale-of-the-Figma-UI)) |
| **Photoshop** | Preferences › Interface › UI Scaling (Auto / 100 / 200 %), **restart required** | 2 steps | UI scale is deliberately decoupled from the image so 100 % zoom stays 1 image px : 1 screen px ([1](https://community.adobe.com/questions-712/ui-scaling-still-not-independent-1175585), [2](https://community.adobe.com/questions-9/icons-and-menus-too-small-on-4k-monitors-1261408)) |
| **Teams** | Ctrl+± | partial | notoriously inconsistent across panes ([Q&A](https://learn.microsoft.com/en-us/answers/questions/4397675/teams-ui-zoom-adjust-with-mouse-or-ctrl-0-not-work)) |
| **Spotify** | Ctrl+± / ⋯ › View › Zoom | continuous-ish | **breaks its own chrome when zoomed** (top-bar buttons overlap the caption cluster), and ultrawide users are still asking for a real setting ([bug](https://community.spotify.com/t5/Desktop-Windows/Zooming-not-scaling-properly-with-the-new-desktop-UI/td-p/5962599), [idea](https://community.spotify.com/t5/Live-Ideas/Desktop-Zoom-Scaling-setting/idi-p/5638600), [4K](https://community.spotify.com/t5/Desktop-Windows/Scaling-issue-on-high-resolution-display/td-p/1735092)) |

**Four lessons.**

1. **Everyone converged on a small discrete ladder, not a slider.** Slack's four rungs is the cleanest.
   Wavee's `ZoomLadder` already is one, and it already documents *why* discrete (glyph-atlas raster keys
   quantize at ×100).
2. **Nobody auto-derives it from the display.** Photoshop's `Auto` is the sole example, and it just follows
   Windows DPI — i.e. it solves the DPI problem, not the proportion problem. **An auto-zoom derived from the
   window's DIP extent against the app's own design box is a genuine differentiator, not a catch-up
   feature.**
3. **The demand is bidirectional.** Slack ships **80 %** and frames it as the large-monitor case: *"if
   you're working on a 27-inch or larger monitor where the default sizing feels spacious and you'd rather
   see more content at once."* Forum threads bear this out — the tension is explicitly *"max real estate
   space… but NOT having to squint"*
   ([AnandTech](https://forums.anandtech.com/threads/folks-with-1440p-and-beyond-how-do-you-cope-with-small-text.2418629/post-37124164),
   [Tom's](https://forums.tomsguide.com/threads/4k-resolution-laptop-but-apps-are-small.407719/post-1750886)).
   So the auto policy must be **allowed to suggest below 100 %** for a genuinely dense-preferring user — but
   only as an *option*, never as the default.
4. **Zoom that breaks the app's own chrome is worse than no zoom.** Spotify's zoomed top bar collides with
   the caption buttons. Wavee is structurally safe here — `MergedChromeLayout` allocates the merged chrome
   row by *space accounting* against a measured fixed budget, so a smaller DIP viewport simply folds stages
   in the documented order rather than overlapping. But it must be **verified at every ladder rung**, not
   assumed (see §6).

### 2.3 Fluid type, and where it stops

Utopia's model is the honest one for "grow with the window": pick **two viewport poles**, a base size and a
modular ratio at each, interpolate with `clamp()`, and **lock at both ends** so growth terminates
([calculator](https://utopia.fyi/type/calculator/),
[Smashing](https://www.smashingmagazine.com/2021/04/designing-developing-fluid-type-space-scales/),
[CSS-Tricks](https://css-tricks.com/consistent-fluidly-scaling-type-and-spacing/)). The essential part is
the **cap**: *"capped off with a media query to prevent the scaling going on forever"*
([Trys Mudford](https://www.trysmudford.com/blog/fluid-feeling/)).

Container queries are the other half of the modern answer — style by the **container's** width, not the
viewport's ([freeCodeCamp](https://www.freecodecamp.org/news/container-queries-responsive-design-beyond-the-viewport/)).
**Wavee already does this and does it better than CSS**: `DetailLayoutBreakpoints`, `DetailTrackTableRules`'
relief ladder, `MergedChromeLayout`'s space accounting and `PlayerBarResponsiveLayout` all key off the
*measured extent of the surface itself*, with hysteresis. The engine has no need of container queries; it
has real measurement.

**So the reflow half of responsive design is done. The scale half is what is missing — and it is a
multiplier, not a breakpoint.** This is the single strongest argument against a per-surface tier ladder: we
would be adding a second, coarser, hand-tuned copy of a mechanism that already exists and is already
better.

### 2.4 Where growth must stop

- **Measure.** Bringhurst's 45–75 characters, ~66 as the ideal; Dyson & Haselgrove find ~55 CPL best for
  both normal and fast reading; novices ~45, experts up to ~80
  ([Google Fonts](https://fonts.google.com/knowledge/using_type/understanding_measure_line_length),
  [Baymard](https://baymard.com/blog/line-length-readability),
  [webtypography 2.1.2](http://webtypography.net/2.1.2),
  [UXPin](https://www.uxpin.com/studio/blog/optimal-line-length-for-readability/)).
  Prose that grows with the window past ~75ch gets *harder* to read, not easier — the return sweep starts
  missing lines.
- **Hit targets.** WCAG 2.2 **2.5.8 Target Size (Minimum)** is 24 × 24 CSS px at AA; **2.5.5** is 44 × 44 at
  AAA ([Silktide](https://silktide.com/accessibility-guide/the-wcag-standard/2-5/input-modalities/2-5-8-target-size-minimum/),
  [w3c/wcag#1831](https://github.com/w3c/wcag/issues/1831)). Wavee's floors already clear AA at zoom 1:
  `PlayerBarLayout.MinButtonBox = 32`, `MinPrimaryBox = 36`, `PrimaryBoxRoomy = 40`, `TrackLane.Heart = 28`,
  and the type doc explains they are **flat across the pressure ladder** on purpose. Under a multiplier they
  only grow. **No hit-target work is needed** — which is itself an argument for the multiplier: a
  hand-authored per-surface tier table is exactly how three of six player-bar tiers once shipped
  sub-minimum targets (that regression is documented in `PlayerBarResponsiveLayout.cs:45–59`).
- **The 4-epx grid.** Microsoft: *"sizes, margins, and positions… should always be in multiples of 4 epx"*,
  because 4 lands on whole pixels at the 100/125/150/175/200/225/250/300/350/400 % plateaus — *"Text does
  not have this requirement."* Wavee's ladders are already 4-multiples. **A zoom ladder rung that is not a
  multiple of 0.25 will put some 4-DIP metric on a fractional pixel.** `ZoomLadder` contains 0.67, 0.8, 0.9,
  1.1 and 1.75 — fine for a manual pick, but the **auto policy should prefer the plateau-clean rungs**
  (1.0, 1.25, 1.5, 1.75, 2.0).

### 2.5 Microsoft's guidance, and the accessibility gap

- Breakpoints are **Small ≤ 640 / Medium 641–1007 / Large ≥ 1008** epx, and there is **no class above
  1008** — Microsoft's own model simply has nothing to say about a 3440-DIP window. Its only advice for the
  top end is *"add density or multi-column views only when width allows"*
  ([breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design),
  [responsive layouts](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml)).
  That is a real gap in the guidance, and it is exactly the gap this document fills.
- `NavigationView.OpenPaneLength` **defaults to 320 DIP**, and `ExpandedModeThresholdWidth` to 1008
  ([docs](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)). Wavee's
  Classic wide tier is exactly 320 (`ShellResponsiveLayout.NavPaneWideW`), so the pane is *at stock* once
  the ladder reaches its top rung — which it does at 1800 DIP.
- **`UISettings.TextScaleFactor`** ranges **1.0 → 2.25** and is set by *Settings › Accessibility › Text
  size*. Microsoft is blunt about the failure mode: *"DirectWrite, GDI, and XAML SwapChainPanels do not
  natively support text scaling"* — Wavee is a from-scratch GPU renderer, so it is squarely in that
  category — and *"If your Windows application includes custom controls, custom text surfaces, hard-coded
  control heights, older frameworks… you likely have to make some updates"*
  ([text scaling](https://learn.microsoft.com/en-us/windows/apps/develop/input/text-scaling),
  [accessible text](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements),
  [API](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.textscalefactor)).
  **Wavee ignores it today** — nothing in the engine or the app reads `TextScaleFactor`. That is a genuine
  accessibility gap, and it is *part of this complaint's answer*: a user whose system text size is at 125 %
  currently sees no change in Wavee at all. WCAG **1.4.4 Resize Text** (AA) wants 200 % without loss of
  content or function ([W3C](https://www.w3.org/TR/UNDERSTANDING-WCAG20/visual-audio-contrast-scale.html)).
- Two MS rules that constrain the design directly: *"Don't specify absolute sizes for your controls"* and
  **"Don't scale font-based icons or symbols"** (`IsTextScaleFactorEnabled = false`). The second is the
  reason `TextScaleFactor` must **not** be folded into the effective window scale — see §4.

---

## 3. The recommendation

> **One global scale (the existing zoom), auto-derived from the display, with a small number of
> surface-local fixes where a uniform multiplier is provably insufficient.**

### 3.1 Why not automatic per-surface size tiers

Rejected, for five reasons:

1. **It duplicates a working mechanism.** The multiplier already exists at the one correct choke point and
   already reaches layout, raster *and* input DIP conversion. A tier ladder would reach only whatever
   surfaces we remembered to wire.
2. **The blast radius is the whole app.** 799 `const float` declarations in `src/apps/Wavee`, and ~27 pure
   layout test files that pin literals (`ShellResponsiveLayoutTests`, `PlayerBarResponsiveLayoutTests`,
   `DetailVerticalLayoutTests`, `ArtistHeroLayoutTests`, `SidebarRowGeometryTests`,
   `DesignTokenConvergenceTests`, `MergedChromeLayoutTests`, `BrowseMastheadMetricsTests`, …). Every one of
   those would have to become "the literal, times a tier factor". The zoom approach changes **zero** of
   them, because zoom is applied *below* the DIP layer they all live in.
3. **It re-opens settled regressions.** `PlayerBarResponsiveLayout`'s type doc records that a per-tier
   metric ramp shipped **three of six tiers with sub-WCAG hit targets** and was deliberately flattened. A
   size-tier ladder is that mistake, re-authored upward.
4. **It becomes a matrix.** Density (Compact/Cozy/Comfortable, `DetailTrackTableRules.RowHeightFor`) ×
   size-tier × the existing width tier is a 3 × N × M table nobody can hold in their head.
   With zoom it is not a matrix at all — it is one product:
   `physical px = DIP(density, width-tier) × osDpiScale × zoom`.
   Density stays a *content* decision (how many rows do I want per screen), zoom stays a *size* decision.
   Orthogonal by construction.
5. **The wrong thing grows.** A width-driven ladder that pushes track-row type to 20 DIP makes a 34" panel
   show *fewer* rows than a laptop — the opposite of what a big screen is for.

### 3.2 Layer A — `ZoomAutoPolicy`: derive the zoom from the display

A new engine-free pure class in the app (the `SetupGating` / `ShutdownUpdatePolicy` pattern).

**The design box is the app's own two numbers**, not a fresh guess:

```csharp
/// The window extent Wavee's DIP constants were authored against. NOT a new number:
///   DesignW = WaveeSize.PageMaxW              (1600 — the page measure cap; every page centres at it)
///   DesignH = DetailShell's tall-hero gate    ( 900 — TitleLarge above it, Title below)
public const float DesignW = WaveeSize.PageMaxW;   // 1600
public const float DesignH = 900f;
```

```csharp
/// The zoom that makes THIS window's DIP viewport match the design box.
///
/// Both axes bind. Width alone would pick 2.15 on a 3440-DIP ultrawide, whose 1720x696 DIP viewport
/// DEMOTES two structural decisions the app already made — the nav-pane wide tier (arms at 1800) and the
/// detail hero's TitleLarge rung (arms at 900 tall). Taking min() over the two ratios makes the policy
/// incapable of buying size by giving up structure, which is the failure mode of every naive app zoom.
///
/// baseW/baseH are DIPs at zoom 1 — i.e. clientPx / osDpiScale, NOT the live (already-zoomed) viewport.
/// Feeding the live viewport back in would be a control loop that converges on the design box regardless
/// of the display, which is exactly wrong.
public static float Suggest(float baseW, float baseH, ZoomAutoMode mode)
{
    if (baseW <= 0f || baseH <= 0f) return ZoomLadder.Default;
    float ratio = MathF.Min(baseW / DesignW, baseH / DesignH);
    float lo = mode == ZoomAutoMode.Dense ? DenseFloor : 1f;   // Dense may go below 100% (Slack's 80%)
    return SnapPlateau(Math.Clamp(ratio, lo, Ceiling));        // 2.0
}

/// The plateau-clean subset of ZoomLadder.Steps. Microsoft's 4-epx rule lands on whole pixels only at the
/// 100/125/150/175/200% plateaus; 0.67/0.8/0.9/1.1 put a 4-DIP metric on a fractional pixel. Those rungs
/// stay available to a MANUAL pick (muscle memory, Ctrl+wheel) — the AUTO policy just never chooses one.
static readonly float[] Plateaus = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];
```

Worked, against the real hardware:

| Window | baseW × baseH | W ratio | H ratio | min | → | DIP viewport | Verdict |
|---|---|---|---|---|---|---|---|
| Laptop maximized, 150 % | 1664 × 1109 | 1.04 | 1.23 | **1.04** | **100 %** | 1664 × 1109 | unchanged — correct today |
| **Ultrawide maximized, 100 %** | **3440 × 1392** | **2.15** | **1.55** | **1.55** | **150 %** | **2293 × 928** | **every structural promise kept** |
| 1920 × 1080, 100 % | 1920 × 1080 | 1.20 | 1.20 | 1.20 | 100 % | 1920 × 1080 | ~unchanged (1.20 → 1.25 is the coin-flip; snap-down) |
| 2560 × 1440, 100 % | 2560 × 1440 | 1.60 | 1.60 | 1.60 | 150 % | 1707 × 960 | pane demotes to Mid at 1707 — **see §3.4** |
| 3840 × 2160, 150 % | 2560 × 1440 | 1.60 | 1.60 | 1.60 | 150 % | 1707 × 960 | identical to the row above — **DPI-independent, as it must be** |
| 3840 × 2160, 100 % | 3840 × 2160 | 2.40 | 2.40 | 2.40 | 200 % | 1920 × 1080 | ceiling binds; good |
| 1366 × 768, 100 % | 1366 × 768 | 0.85 | 0.85 | 0.85 | 100 % (Dense: 75 %) | 1366 × 768 | never shrinks unless asked |
| 3440 × 700 (a wide sliver) | 3440 × 700 | 2.15 | 0.78 | 0.78 | 100 % | 3440 × 700 | height guard works |

Note the fifth and fourth rows: two very different panels that present the same DIP box get the same
answer. That is the property that makes this **not** a DPI hack.

**Where it plugs in — three call sites, no existing constant touched:**

1. **`Program.cs:524`** — today `Zoom = ZoomLadder.Snap(settings.Get(WaveeSettings.ZoomLevel))`.
   Becomes: if `appearance.zoom.mode == Auto`, pass `AppOptions.Zoom = ZoomAutoPolicy.Suggest(...)`
   computed from the primary monitor's work area / the restored window rect (`GetDpiForMonitor` +
   `MonitorFromRect`), so the very first frame is already at the right zoom. The existing
   *"seeded BEFORE the first frame (the ThemeMode discipline: no startup jump)"* comment is the contract to
   preserve.
2. **`Win32Platform` WM_DPICHANGED (`:1775`) and the resize-settle path** — the engine already re-derives
   `_scale` there. Add a seam: `IPlatformWindow.BaseDipSize` (client px ÷ `_rawDpiScale`) plus an
   `Action<Size2>? OnBaseExtentChanged` the host relays. The app subscribes and, **only in Auto mode**,
   calls `FluentApp.SetZoom(ZoomAutoPolicy.Suggest(...))`. Must be **debounced to resize-settle**, not
   per-`WM_SIZE` — a zoom change is a full relayout + glyph re-raster.
   Line 1782's *"app zoom survives the monitor hop"* stays literally true for the Manual mode and becomes
   *"is re-derived for the new monitor"* in Auto — which is the actual fix for the two-display problem.
3. **`SettingsPage.Appearance.cs`** — the Zoom picker gains **Auto** as its head item, labelled with the
   resolved value (`Auto (150 %)`), the JetBrains "Zoom IDE" affordance. Manual picks and Ctrl+± set
   `mode = Manual` as a side effect (a deliberate act overrides the policy — the same
   `SidebarPreferences.WidthUserSet` idiom the nav-pane ladder already uses:
   *"a user who drags the seam pins their own width… and the ladder stops applying"*).
   Ctrl+0 resets to **Auto**, not to 100 %.

**Settings shape** (`AppSettings.cs`): keep `appearance.zoom` (float) as the applied value so nothing
downstream changes, and add `appearance.zoom.mode` (int: `0 = Auto`, `1 = Manual`, `2 = Dense`) defaulting
to **`Auto` for a fresh install** and **`Manual` on upgrade when a non-1.0 value is already stored** (a user
who already picked 125 % must not have it silently overridden). If `Auto` is judged too aggressive as a
default, ship the one-shot alternative: an `AppUpdateToasts`-style suggestion — *"This display has room for
a larger Wavee. Use 150 %?"* — once per new display identity.

### 3.3 Layer B — lyrics: the one surface a uniform multiplier does not fix

`src/apps/Wavee/Features/Player/LyricsView.cs`

```
:1275  float RowFontSize   => _large ? 36f : 26f;
:1276  float RowLineHeight => _large ? 46f : 33f;   // ~1.27x
:1279  float RowSidePad    => _large ? 64f : 22f;
:1353  float fontSz        => _large ? 28f : 19f;   // the secondary/translation line
```

`_large` is a **boolean** — stage vs rail (`:282, :303`) — not a ladder. Two problems that zoom cannot
touch, because both are about the *shape of the surface*, not its size:

1. **The immersive stage has no measure cap.** At a 2293-DIP viewport the stage line box is
   `2293 − 2×64 = 2165` DIP at 36-DIP type ≈ **120 characters** — far past Bringhurst's 75 and past even the
   80 CPL expert ceiling. Zooming makes the type bigger *and the line proportionally just as long*, because
   both are DIPs. **The stage lyric needs a measure cap, and the cap must be expressed in em, not DIP**, so
   it survives zoom and `TextScaleFactor`:
   `measure = min(available, RowFontSize × MeasureEm)` with `MeasureEm ≈ 22` (≈ 44–50 characters at Segoe's
   average advance — the low, "focal / karaoke" end of the range, right for a display line you read one at a
   time, not a paragraph).
2. **The rail rung is one size for a 200–500 DIP rail** (`ShellResponsiveLayout.RailMinW/RailMaxW`). 26 DIP
   at a 200-DIP rail wraps every line; 26 DIP at a 500-DIP rail is timid.

Proposed: one new pure class, `LyricsTypeRungs`, and delete the boolean's arithmetic:

```csharp
/// Three rungs on the engine ramp, chosen by the pane's MEASURED extent (the container-query idiom this
/// codebase already uses everywhere) — never by the window's, and never by the zoom, which has already
/// been applied to `extent` by the time layout sees it.
///   rail  <  320 DIP → 22 / 30      (a narrow rail; fit beats presence)
///   rail  >= 320 DIP → 26 / 33      (today's rail rung — unchanged, so the common case is a no-op)
///   stage, extent < 1100 → 36 / 46  (today's stage rung — unchanged)
///   stage, extent >= 1100 → 44 / 56 (the rung the ramp already publishes above TitleLarge)
/// Then, unconditionally: measure = min(extent - 2*sidePad, size * MeasureEm).
```

Note what this is *not*: it is not a fluid interpolation. Two rungs per surface, on the engine's own ramp,
with hysteresis — the `DetailShell` `winH >= 900f` idiom (*"TWO RUNGS OF THE RAMP, not a fluid
interpolation. The old Clamp(…) produced a different off-ramp size at every window height… so the page hero
was never the same typographic step twice"*). That comment is the house rule; follow it.

### 3.4 Layer C — what the recommendation itself breaks, and the literals that are already wrong

**C0 — image decode budgets do not track the effective scale. This is a blocker for Layer A, not a nicety.**

`UseImage(src, decodePx)` (`..\fluent-gpu\src\FluentGpu.Engine\Hooks\RenderContext.cs:946`,
`Component.cs:147`) takes a **caller-chosen** decode budget, and Wavee passes DIP-ish constants
(`Features/Library/Surfaces.cs:403`, `Features/Home/HomePage.cs:1622`,
`Features/Detail/ArtistPage.Hero.cs:327`). Nothing multiplies them by `Viewport.Scale`.

That is already a latent softness bug at high DPI — a 150 % laptop decodes every cover at 1/1.5 the
resolution it displays. **Layer A makes it worse and makes it mine:** recommending 150 % zoom on the
ultrawide takes the effective scale to 1.5, and on the laptop at 150 % OS DPI plus a future 125 % zoom it
reaches 1.875. Art would visibly soften the moment the auto-zoom lands — and the user would correctly read
that as "the new scaling feature made my album art blurry."

Fix: route every decode budget through the ambient scale —
`UseImage(src, (int)MathF.Ceiling(dipEdge * UseContext(Viewport.Scale)))` — clamped to a sane ceiling so a
250 % zoom on a 4K panel does not ask for a 3000-px decode of a 300-px thumbnail. `Equalizer.cs:63` is the
existing precedent in this codebase for consuming `Viewport.Scale` to size a device-pixel resource. This is
app-side work across the art call sites, plus a decision about where the ceiling sits; it is small but it is
**not optional**, and it should ship in the same change as Layer A or immediately before it.

**C1–C3 — literals that are already wrong, independent of everything above:**

1. **`PlayerBarLayout.TopEdgeWidth: 2400f`** (`PlayerBarResponsiveLayout.cs:157`), consumed as
   `ProgressBar.Indeterminate(L.TopEdgeWidth)` (`PlayerBar.cs:591`). A hard 2400-DIP nominal for the
   indeterminate sweep's track. On the ultrawide at zoom 100 % the window is **3440 DIP** — the sweep stops
   1040 DIP short of the bar. This is a genuine visual bug on a wide window, independent of everything else
   in this document. Fix: pass the bar's measured width (it already has it — `PlayerBarLayout.Resolve` takes
   `width`), or floor it at `max(2400, width)`.
2. **`ShellResponsiveLayout.NavPaneWideEnterW = 1800`** has no rung above it. At the recommended zoom the
   2560 × 1440 case lands on a **1707**-DIP viewport, which is *below* 1800 — so a 27" 1440p panel at Auto
   gets the **Mid** pane (280) where at 100 % zoom it got **Wide** (320). Two clean options:
   (a) lower the gate to **1700** — inside the existing 24-DIP hysteresis band's spirit, one constant, one
   test line; or (b) make the ladder ratio-based: the tiers are 240/280/320 at 1400/1800, i.e. the pane is
   ~17 % of the window at every rung, so express it as
   `clamp(round4(viewportW × 0.17), NavPaneMinW, tiers.Wide)`. **(a) is the recommendation** — (b) replaces
   a documented three-rung ladder with arithmetic, and `SidebarDesignInfo.Tiers` owns three triples that
   locked decision 14 deliberately made different per design.
3. **No fourth pane tier.** Once Auto lands, a 3840 × 2160 @ 100 % display sits at a 1920-DIP viewport,
   above 1800, so Wide (320) applies and the pane is 16.7 % — correct. **Nothing to do.** Listed only so a
   reviewer does not add one: the ladder's own doc already says these are *defaults*, and a user who drags
   the seam pins their own width up to `NavPaneMaxW = 460`.

Explicitly **not** in scope, and why:

- **The player bar's 72-DIP height and flat 32/16 · 40/20 transport metrics.** Under zoom 150 % that is a
  108-px bar with 48-px secondaries and 60-px primaries on a 1392-px-tall window — 7.8 % of height, roomy,
  and comfortably past WCAG 2.5.5's 44. The flatness is load-bearing (§2.4). **Leave it alone.**
- **Track-row type.** 14/20 BodyStrong at 150 % is 21 physical px. Growing the *DIP* size to 18 or 20 would
  invalidate every floor in `TrackLane` — `TitleFloor = 120` is documented as *"~14 characters of the row's
  BodyStrong 14/20"*, `ArtistFloor`/`AlbumFloor = 90` as *"~11 characters of Caption 14/20"*, and `Date = 88`
  is sized to its **header** at 11 px uppercase. Change the type and the relief ladder's `MinWidthFor`
  arithmetic silently mis-predicts every squeeze. **The table should gain lanes and whitespace as it grows,
  and it already does** — `NominalReliefFor` returns 0 the moment the identity floors clear, so a wide
  table re-admits Plays → BPM·key → Added by → Date added on the way up, in the documented order. That is
  the correct way for a table to use a big screen.
- **`WaveeSize.PageMaxW = 1600`.** Keep it, and keep the test that pins it. It is the design box; raising it
  would be an admission that we are hand-tuning for one monitor. At the recommended zoom it becomes a
  70–93 % fit instead of a 47 % one, which is what it was always for.

### 3.5 Layer D — `TextScaleFactor`: yes, but on **type only**

Honour it, and **do not** fold it into `IPlatformWindow.Scale`. Two reasons, both from Microsoft's own text:

- *"Don't scale font-based icons or symbols"* — `Icons.*` glyphs in Wavee are a font. Folding text scale
  into the window scale would grow every glyph, every art tile, every hit target and every row height by up
  to 2.25×, which is DPI's job, not text scaling's.
- Text scaling exists precisely so a user can enlarge **text alone** *"instead of… relying on DPI scaling
  (which resizes everything)"*.

The seam does not exist yet, and the file to touch is small: `Typography.cs:39–46` hardcodes all eight ramp
rungs as literal floats, and there is no font-scale multiplier anywhere in the engine. One control already
records the intended posture for the opt-out case —
`..\fluent-gpu\src\FluentGpu.Controls\PersonPicture.cs:98`: *"`IsTextScaleFactorEnabled=False` ⇒ fixed font
size."* Note also that `UISettings` is *already* instantiated in the engine, for accent colour
(`Win32Theme.Accent.cs:19–48`, a hand-vtable `IUISettings3.GetColorValue`); `IUISettings2.TextScaleFactor`
is simply never queried. So the PAL read is an addition to an existing call, not a new dependency.

Shape (engine work, in `..\fluent-gpu`, verified there):

```
Typography.TextScale  — a float read once from UISettings.TextScaleFactor at startup and refreshed on the
                        TextScaleFactorChanged event; 1.0 when the API is unavailable.
                        Applied at TextEl resolve time to Size and LineHeight ONLY.
                        Icon/glyph runs opt out (the IsTextScaleFactorEnabled=false equivalent).
```

Then the app-side consequence, which is the real work: **fixed row heights must floor against the scaled
type.** `DetailTrackTableRules.RowHeightFor` returns 36/40/44/48 (Classic) and 40/48/56/64 (Modern) and
`ArtSizeFor` documents *"the row keeps ≥ 8 DIP of breathing room above and below (row − art ≥ 16)"*. At a
1.5× text scale a 40-DIP Compact row holding 21-DIP type has 19 DIP of leading left — it clips. So:

```csharp
RowHeightFor(density, classic) => MathF.Max(table[density], Typography.ScaledLineHeight(Body) + 2 * MinRowAir);
```

and the same treatment for `SidebarRowGeometry` / `SidebarRowMetrics.HeightFor`, `LibraryV3Metrics`
(`NavRowHeight 40`, `HeaderHeight 44`, `ToolbarHeight 36`, `ChipRailHeight 40`, `BreadcrumbHeight 32`),
`WaveeSize.ControlH 32` / `NavItemH 44`, and `DetailTrackTableRules.HeaderHeightFor`. **Cap the honoured
factor at 1.5** for a first pass and say so in the release notes — MS's range goes to 2.25, but a
fixed-height music table at 2.25× needs a genuine reflow pass, and shipping a clipped one is worse than
shipping a capped one.

**Interaction with zoom:** they multiply, and they must be allowed to.
`effective type px = DIP × osDpiScale × zoom × textScale`. A user at 150 % OS DPI, Auto zoom 150 % and text
scale 125 % gets 14 × 1.5 × 1.5 × 1.25 = 39 px row text. That is a legitimate, requested outcome — and
`ZoomAutoPolicy` should **not** compensate for it. Text scale is the user's stated preference about text;
the auto zoom is the app's inference about proportion. Confusing the two is how apps end up with a zoom
that fights the accessibility setting.

**This layer is also the WCAG 1.4.4 story.** It is currently unmet, and it is worth its own issue.

---

## 4. Where growth stops — the summary table

| Thing | Grows with zoom? | Cap | Why |
|---|---|---|---|
| Track-row type / height / art | yes (as DIPs) | **DIP values unchanged** | the table should gain lanes, not type; `TrackLane` floors are measured against 14/20 |
| Track table lanes | n/a — reflow | relief ladder, already correct | `NominalReliefFor` re-admits cheapest-fact-last on the way up |
| Page content column | yes | **`PageMaxW = 1600` DIP** | already the design measure; keep the pinning test |
| Lyrics (stage) | yes | **`size × ~22 em` measure cap**, new | 2165 DIP ≈ 120 CPL today; Bringhurst 45–75 |
| Lyrics (rail) | yes | 3 rungs by rail extent, hysteretic | one size cannot serve a 200–500 DIP rail |
| Descriptions | yes | `DetailVerticalLayout.TitleWMax` / `CopyAvailFor` + `DescriptionMaxLines(3/4)` already cap | already correct |
| Player-bar height + transport | yes | **flat metrics, unchanged** | flatness is a documented WCAG fix |
| Hit targets | yes | floors 28/32/36/40 DIP, unchanged | clears 2.5.8 (24) at zoom 1; only grows |
| Nav pane | yes | `NavPaneMaxW = 460` DIP; tiers 240/280/320 | stock `OpenPaneLength` is 320 |
| Cover art **decode** | **no — bug (C0)** | must scale by `Viewport.Scale`, then cap | a DIP-sized decode at effective scale 1.5 is 1/1.5 resolution |
| Right rail | yes | `RailMaxW = 500` DIP | unchanged |
| Zoom itself | — | **`[1.0, 2.0]` auto; `[0.75, 2.0]` in Dense; `ZoomLadder.Min/Max 0.25–5` manual** | above 2.0 on any real panel the DIP viewport drops below the design box |

---

## 5. Blast radius, honestly

### The recommended path (Layers A + B + C)

| Touched | Files |
|---|---|
| **New pure classes** | `ZoomAutoPolicy` (+ `ZoomAutoMode`), `LyricsTypeRungs` |
| **New tests** | `ZoomAutoPolicyTests`, `LyricsTypeRungsTests` |
| **App edits** | `Program.cs` (1 line), `AppSettings.cs` (1 key), `SettingsPage.Appearance.cs` (Auto item + `UseContext(Viewport.Zoom)`), `WaveeShell.cs` (`ZoomStep` sets Manual; Ctrl+0 → Auto), `LyricsView.cs` (4 property bodies + a measure cap), `PlayerBarResponsiveLayout.cs` (`TopEdgeWidth`), `ShellResponsiveLayout.cs` (`NavPaneWideEnterW` 1800 → 1700) |
| **App edits, C0** | every art call site that passes a literal decode budget — `Surfaces.cs`, `HomePage.cs`, `ArtistPage.Hero.cs`, and the rest of the `UseImage` callers — routed through `Viewport.Scale` |
| **Engine edits** | one read-only seam: `IPlatformWindow.RawDpiScale` (or `BaseDipSize` = `ClientSizePx / _rawDpiScale`) + a settle-debounced `OnBaseExtentChanged` relay. `_rawDpiScale` already exists as a field and is already exposed to one consumer (`Win32InputPane.RawDpiScale`, `Interop\Win32InputPane.cs:182`) — this follows that precedent rather than inventing a seam. Made and verified in `..\fluent-gpu` (Debug + Release + VerticalSlice). |
| **Existing test edits** | `ShellResponsiveLayoutTests` (the 1800 line), `PlayerBarResponsiveLayoutTests` (the `TopEdgeWidth` assertion) |
| **Existing constants changed** | **two** |

### The rejected path (per-surface size tiers), for contrast

- **799** `const float` declarations in `src/apps/Wavee` would each need a "does this tier?" decision.
- **~27** pure layout test files pin literals and would all need a tier axis:
  `ShellResponsiveLayoutTests`, `PlayerBarResponsiveLayoutTests`, `DetailLayoutBreakpointTests`,
  `DetailVerticalLayoutTests`, `ArtistHeroLayoutTests`, `ContextBandLayoutTests`,
  `DetailTrackCommandBarLayoutTests`, `DetailSkeletonGeometryTests`, `BrowseMastheadMetricsTests`,
  `BrowsePageLayoutTests`, `HomeLayoutTests`, `HomeHeroLayoutTests`, `HomeArtistRowLayoutTests`,
  `LibraryLayoutBreakpointTests`, `MergedChromeLayoutTests`, `SidebarRowGeometryTests`,
  `SidebarNavLayoutTests`, `SidebarCustomizerLayoutTests`, `StageLayoutTests`, `SetupLayoutTests`,
  `ConcertLayoutTests`, `PlaylistInsertionGeometryTests`, `ShellWashGeometryTests`,
  `DesignTokenConvergenceTests`, …
- **`MergedChromeLayout.FixedBudget`** is the worst case: nine hand-measured constants
  (`ChromeBarLeadW 60`, `ChromeNavButtonW 44`, `ChromeAddSlotW 32`, `ChromeTabOverflowW 36`,
  `ChromeProfileChipW 32`, `ChromeProfileNameW 90`, `ChromeMinDragStripW 48`, `ChromeCaptionClusterW 138`,
  `ChromeThemeToggleW 44`) each documented as *"the row's REAL laid-out widths, read off the code that draws
  them"*. Three of them are **native caption geometry** (`ChromeMinDragStripW` is pinned by `TitleBar` under
  merged mode; `ChromeCaptionClusterW` is 3 × `CaptionButton.Width`) and therefore **cannot** be scaled by
  an app-level tier — only by the window scale, which zoom already is. Issue #88's comment records what
  happens when this budget drifts by 44 DIP: *"the search field appeared to fit 44 DIP sooner than the row
  could actually give it."* A tier factor over this budget would reintroduce exactly that class of bug, per
  tier.

That last point is decisive on its own: **a per-surface tier system is structurally unable to scale the
native caption cluster, and the merged chrome row's whole allocator is measured against it.** Only the
window scale can move both together — and it already does.

---

## 6. Verification

**Pure unit tests** (`Wavee.Tests`, no engine mount, no source-text reads — house rule):

- `ZoomAutoPolicyTests` — fixtures for both of the user's real displays plus 1366×768, 1600×900, 1920×1080,
  2560×1440, 3840×2160 at 100/125/150 %, a 3440×700 sliver, and the degenerate 0×0 / NaN inputs.
  Three properties worth asserting as *properties*, not point values:
  1. **DPI independence** — `Suggest(w/s, h/s, …)` is equal for any `(w, h, s)` presenting the same base
     box (the 3840×2160@150 % vs 2560×1440@100 % pair).
  2. **Monotonicity** — a larger base box never suggests a smaller zoom.
  3. **Structure preservation** — for every fixture, the resulting DIP viewport
     (`base / suggested`) satisfies `H ≥ 900` (the tall-hero gate) **and**
     `W ≥ NavPaneWideEnterW` whenever the unzoomed width already did. This is the assertion that encodes
     *why* `min()` is there, and it is the one that would have caught a width-only policy.
  4. Every returned value is a member of `Plateaus` and of `ZoomLadder.Steps`.
- `LyricsTypeRungsTests` — rung selection with hysteresis across the rail band and the stage band; and the
  measure cap: assert the computed line box never exceeds `size × MeasureEm`, at every rung.
- **The engine already has the zoom gate.** `..\fluent-gpu\src\FluentGpu.VerticalSlice\Suites\NavSuite.cs`
  gate **54d** — *"app zoom re-lays-out the DIP viewport and survives a DPI hop (Scale = DPI × Zoom)"* —
  asserts the root rect shrinks on `SetZoom(1.25f)`, that a raw-DPI change to 1.5 **keeps** the zoom, and the
  ladder invariants (`In(1f) == 1.1f`, `Snap(1.24f) == 1.25f`, `Clamp(NaN) == 1f`); gate **54c**
  (`NavSuite.cs:880`) covers the mid-session `WM_DPICHANGED` hop. **Do not re-test the mechanism** — extend
  54d only if the `RawDpiScale`/`BaseDipSize` seam is added.
- **Regression guard for the whole idea:** a test that walks **every** `ZoomLadder.Steps` rung against a
  set of physical window sizes and asserts, purely, that `MergedChromeLayout.Resolve` still admits the
  search field at its `ChromeSearchIconW` minimum and evicts tabs in the documented order — i.e. that no
  zoom rung produces the Spotify caption-collision failure. This is cheap (the allocator is already pure)
  and it is the highest-value test in the set.

**Manual / visual** (per `docs/guide` and the memory note about launching for visual checks — the second
`Wavee.exe` hands off to the running instance):

1. On DISPLAY1, `Settings › Appearance › Theme › Zoom` reads **Auto (150 %)**, and the About tab's zoom
   receipt (`SettingsPage.About.cs:294`) agrees.
2. Drag the window from DISPLAY1 (100 %) to DISPLAY2 (150 %) and back. In **Auto** the zoom re-derives per
   monitor; in **Manual** it survives the hop unchanged (`Win32Platform.cs:1782`'s contract).
3. Ctrl+± sets Manual and the picker updates **live** (the `Viewport.Zoom` fix) — this is also the repro for
   the stale-100 % bug.
4. At each of 100/125/150/175/200 % on the ultrawide: the merged chrome row never overlaps the caption
   cluster; the player bar keeps its Full tier; the track table's lane set changes only in the relief
   ladder's documented order; the stage lyric's line box stays inside its measure cap.
5. **C0:** at Auto (150 %) on the ultrawide, compare a playlist grid and the player-bar art against the same
   view at 100 % zoom. Any softening means a decode budget is still a raw DIP literal. Do this check
   *before* signing off Layer A.
6. `Settings › Accessibility › Text size` at 100/125/150 % (Layer D): no clipped rows anywhere in the track
   table, the sidebar, or Library V3's header/toolbar/chip rail.

**What a reviewer should look at, in order:**

1. `ZoomAutoPolicy.Suggest` — is the design box derived from `WaveeSize.PageMaxW` and the `DetailShell`
   gate, or is it a fresh magic number? (It must be the former; a fresh number is the whole failure mode.)
2. Does `Suggest` take **base** DIPs (client px ÷ OS DPI scale) and never the live, already-zoomed viewport?
   A feedback loop here is silent and converges wrong.
3. Is the auto re-evaluation **debounced to resize-settle**? A per-`WM_SIZE` `SetZoom` is a full relayout +
   glyph re-raster per pixel of a drag.
4. Does a manual pick / Ctrl+± flip the mode to Manual? (The `SidebarPreferences.WidthUserSet` precedent.)
5. Do the two changed constants each carry a comment naming this document and the issue number, and does
   each have a test line moved with it?
6. **Is C0 in the same change?** Every `UseImage` budget multiplied by `Viewport.Scale` and capped. A Layer A
   that lands without C0 is a regression the user will see immediately.
7. Are the lyrics rungs **rungs of the engine ramp with hysteresis**, not a `Clamp` interpolation? (The
   `DetailShell.cs:585` comment is the house rule.)
8. Did anyone add a **root scale transform**? Reject it — it double-applies against `D3D12Device`'s
   `lw/lh` logical viewport and desyncs hit-testing. The only sanctioned multiplier is `IPlatformWindow.Scale`.
9. Layer D only: is `TextScaleFactor` applied to `Size`/`LineHeight` **only**, with glyph runs opted out,
   and does every fixed row/control height floor against the scaled line box?

**Gates:** `dotnet build Wavee.slnx` Debug **and** Release (`TreatWarningsAsErrors`), `Wavee.Tests` green,
and for the engine seam the engine's own gates in `..\fluent-gpu` (Debug + Release + VerticalSlice).

---

## 7. Sources

**Windows / Microsoft**
- [Screen sizes and breakpoints for responsive design](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design) — effective pixels, the 640/1007/1008 classes, the 4-epx rule and the 100–400 % plateaus
- [Responsive layouts with XAML](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml)
- [Text scaling](https://learn.microsoft.com/en-us/windows/apps/develop/input/text-scaling) — 100–225 %, "DirectWrite, GDI, and XAML SwapChainPanels do not natively support text scaling", "Don't scale font-based icons or symbols"
- [Accessible text requirements](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements)
- [UISettings.TextScaleFactor](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.textscalefactor) · [TextScaleFactorChanged](https://github.com/MicrosoftDocs/winrt-api/blob/docs/windows.ui.viewmanagement/uisettings_textscalefactorchanged.md)
- [NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview) · [OpenPaneLength (default 320)](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.navigationview.openpanelength) · [ExpandedModeThresholdWidth (default 1008)](https://github.com/MicrosoftDocs/winui-api/blob/docs//microsoft.ui.xaml.controls/navigationview_expandedmodethresholdwidth.md)
- [Resize a WinUI window, part 0: what is DPI](https://ben.stolovitz.com/posts/resize_winui_window_part_0_dpi/)

**App zoom, as shipped**
- Slack — [Adjust your zoom level](https://slack.com/help/articles/236067467-Adjust-your-zoom-level-in-Slack) (80/100/125/150 %) · [Accessibility in Slack](https://slack.com/help/articles/4455747966739-Accessibility-in-Slack) · [the 80 % large-monitor case](https://frontdeskchat.com/books/slack/set-your-preference/adjust-zoom-level-slack/)
- Discord — [zoom level vs chat font scaling](https://filmora.wondershare.com/zoom-video/zoom-in-and-out-on-discord.html) · [Ctrl+± in 10 % steps](https://techwiser.com/zoom-in-zoom-out-discord/)
- VS Code — [`window.zoomLevel` vs `editor.fontSize`](https://tms-outsource.com/blog/posts/how-to-zoom-in-vscode/) · [scope of each](https://devgex.com/en/article/00006697)
- JetBrains — [IDE zoom level](https://www.jetbrains.com/help/rider/IDE_zoom_level.html) · [HiDPI / `ide.ui.scale`](https://intellij-support.jetbrains.com/hc/en-us/articles/360007994999-HiDPI-configuration) · [presentation-mode zoom 175 %](https://www.josephguadagno.net/2026/02/16/jetbrains-rider-settings-for-presentations)
- Figma — [Adjust the scale of the Figma UI](https://help.figma.com/hc/en-us/articles/360049549913-Adjust-the-scale-of-the-Figma-UI) · [UI zoom, not canvas zoom](https://forum.figma.com/suggest-a-feature-11/ui-zoom-not-canvas-zoom-18976)
- Photoshop — [UI scaling still not independent](https://community.adobe.com/questions-712/ui-scaling-still-not-independent-1175585) · [icons and menus too small on 4K](https://community.adobe.com/questions-9/icons-and-menus-too-small-on-4k-monitors-1261408)
- Teams — [Ctrl+zoom inconsistency](https://learn.microsoft.com/en-us/answers/questions/4397675/teams-ui-zoom-adjust-with-mouse-or-ctrl-0-not-work)
- Chromium — [Blink coordinate spaces: device scale factor vs browser zoom](https://www.chromium.org/developers/design-documents/blink-coordinate-spaces/) · [`pageZoomFactor` vs device scale factor](https://lists.w3.org/Archives/Public/public-css-archive/2019Jan/0388.html)

**The complaint in the wild**
- Spotify — [Desktop zoom/scaling setting (idea)](https://community.spotify.com/t5/Live-Ideas/Desktop-Zoom-Scaling-setting/idi-p/5638600) · [Zooming not scaling properly with the new desktop UI](https://community.spotify.com/t5/Desktop-Windows/Zooming-not-scaling-properly-with-the-new-desktop-UI/td-p/5962599) · [Scaling issue on high-resolution display](https://community.spotify.com/t5/Desktop-Windows/Scaling-issue-on-high-resolution-display/td-p/1735092) · [Playlist section too small](https://community.spotify.com/t5/Desktop-Windows/Playlist-section-is-too-small-to-view/td-p/5092779)
- [AnandTech — folks with 1440p and beyond, how do you cope with small text](https://forums.anandtech.com/threads/folks-with-1440p-and-beyond-how-do-you-cope-with-small-text.2418629/post-37124164) · [Text way too small in 1440p](https://forums.anandtech.com/threads/text-way-too-small-in-1440p-4k-must-be-like-reading-fine-print.2363817/post-35951070) · [Tom's — 4K laptop but apps are small](https://forums.tomsguide.com/threads/4k-resolution-laptop-but-apps-are-small.407719/post-1750886)

**Type, measure, targets**
- Utopia — [fluid type scale calculator](https://utopia.fyi/type/calculator/) · [CSS-only fluid modular scales](https://utopia.fyi/blog/css-modular-scales/) · [Meet Utopia (Smashing)](https://www.smashingmagazine.com/2021/04/designing-developing-fluid-type-space-scales/) · [Consistent, fluidly scaling type and spacing (CSS-Tricks)](https://css-tricks.com/consistent-fluidly-scaling-type-and-spacing/) · ["that fluid feeling" — the cap](https://www.trysmudford.com/blog/fluid-feeling/)
- Measure — [Google Fonts: understanding measure / line length](https://fonts.google.com/knowledge/using_type/understanding_measure_line_length) · [Baymard: readability and line length](https://baymard.com/blog/line-length-readability) · [Bringhurst applied to the web, 2.1.2](http://webtypography.net/2.1.2) · [UXPin: the 50–75 character rule](https://www.uxpin.com/studio/blog/optimal-line-length-for-readability/)
- Container queries — [freeCodeCamp: responsive design beyond the viewport](https://www.freecodecamp.org/news/container-queries-responsive-design-beyond-the-viewport/)
- WCAG — [1.4.4 Resize Text (W3C understanding)](https://www.w3.org/TR/UNDERSTANDING-WCAG20/visual-audio-contrast-scale.html) · [1.4.4 explained](https://silktide.com/accessibility-guide/the-wcag-standard/1-4/distinguishable/1-4-4-resize-text/) · [2.5.8 Target Size (Minimum), 24×24](https://silktide.com/accessibility-guide/the-wcag-standard/2-5/input-modalities/2-5-8-target-size-minimum/) · [w3c/wcag#1831 — is 24×24 reasonable](https://github.com/w3c/wcag/issues/1831)
