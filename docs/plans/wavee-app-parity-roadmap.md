# fluent-gpu → WaveeMusic app-parity roadmap — the next best things to build

> **What this is.** A dependency-ordered, value-ranked plan of the next engine + app work to make the in-repo
> fluent-gpu rebuild (`src/apps/Wavee/`) match the shipping **WaveeMusic** WinUI 3 app (`C:\WAVEE\WaveeMusic`).
> This is *app-level* parity (the composed surfaces WaveeMusic ships) — distinct from the *control-level* parity in
> [`winui-parity-sweep.md`](./winui-parity-sweep.md) and the *spec-level* gap in [`../design/BUILD-ROADMAP.md`](../design/BUILD-ROADMAP.md).
>
> **How it was produced (and its confidence).** A 30-agent investigation workflow: 8 inventory agents (both apps +
> the engine + existing plans), 10 per-feature-area gap analyses, an adversarial verification pass per area, a synthesis
> lead, and a completeness critic. Caveats: two inventory sections degraded (the *engine-plans* agent returned a junk
> payload; the *requirements-skeleton* agent failed schema retries), so the plan-state lean is on the gap agents reading
> `BUILD-ROADMAP.md` / `docs/plans/*` directly. Every **spine claim** below (the file:line assertions the priorities
> rest on) was **independently re-verified by hand** before publishing. One engine agent flagged that `BUILD-ROADMAP.md`
> is *materially stale / conservative* for several subsystems (animation, image GPU upload, scroll) — i.e. the engine is
> **further along** than that doc claims; trust the code over the roadmap doc.

---

## 1. Executive summary

The engine is well past a "skeleton," and so is the rebuild. **`src/apps/Wavee/` is already a working browser-shaped shell**:
4-row chrome (custom `TitleBar` + `TabStrip`, `ShellToolbar` with back/forward/omnibar, resizable/collapsible sidebar,
docked `PlayerBar`), a **per-tab keep-alive navigation host** (`ContentHost.cs:23` → `Flow.KeepAlive`, `MaxEntries: 8`),
detail pages with a **landed collapsing hero + shy pill** (`ArtistPage.Hero.cs`), Home/Library/Search pages,
connected-animation back/forward morphs, and animated live re-theme. Under it sits a genuinely strong engine: list
virtualization (uniform + Fenwick variable + grouped-layout math + grid), scroll-as-transform with snap/inertia/collapse,
the analytical-spring `AnimValue` slab, a real WIC-decode → pooled-D3D12 image-residency pipeline, a live D3D12 acrylic
compositor, the full text-edit stack, gesture arena + drag controller, and ~90 portable controls.

So the question is **not** "build the basics" — it's "close the specific holes that still block real WaveeMusic surfaces."
They cluster into **four honest gaps**:

1. **No dynamic color.** Album-art palette extraction is unbuilt — yet the app is *already wired to consume it*
   (`DetailShell.cs` waits on a `Palette`; `WaveePalette`, `PlaybackBridge.TrackPalette` exist). Producer missing,
   consumer ready. This is the app's signature mechanic.
2. **No real DataGrid composition.** The virtualizer, grouped-layout math (`GroupedListVirtualLayout.StickyHeaderIndexAt`),
   and sticky-pin flags (`NodeFlags.StickyPinned`) all exist, but **no control fuses them** into a multi-column,
   group-headered, footer-bearing track list — so today's Album/Playlist pages go **eager (de-virtualized)** when footer
   shelves are present (`DetailTracks.TrailingBody`).
3. **Player & action surfaces are half-wired.** Queue panel, device picker, now-playing expand, and SMTC are TODO stubs;
   `ContentDialog` can't host arbitrary content (hardcoded `Title`+`Message`), and `MenuFlyout` submenus are eager
   (can't load data on open). Multi-line text has no vertical scroll/caret-follow, which silently blocks composers + rename.
4. **Two big missing seams.** There is **no video surface** and **no app-authored custom-draw/shader seam**, which between
   them block VideoPlayerPage, MiniVideoPlayer, Theatre, the synced-lyrics canvas, the mesh-gradient hero, audio
   visualizers, and the editorial baked backdrop.

**Strategy:** lead with the cheap foundational unblocks (palette; dialog/menu content slots; multiline caret-follow; SMTC),
then make the detail-page track list real (DataGrid: footer band + grouping + columns + multi-select), then finish the
player/action surfaces (mostly app-composition over landed primitives), and only then take on the two XL seams
(app-shader → video) plus hardening (UIA, multi-window). Schedule risk lives almost entirely in Wave 4 — front-load 1–3.

---

## 2. What already works — the foundation (do **not** rebuild)

**Shell / navigation (app-side, landed).**
- Per-tab keep-alive nav host via `Flow.KeepAlive` (`src/apps/Wavee/Features/Shell/ContentHost.cs`; primitive
  `src/FluentGpu.Engine/Hooks/ControlFlow.cs`; reconciler `Reconciler.cs:723+`). Back/forward = visibility flip; scroll
  position + bindings survive eviction.
- Browser `TabStrip` (add/close/select/reorder + per-tab isolated stack, `WaveeShell.cs`); custom `TitleBar` with
  WM_NCHITTEST drag regions + snap-layout (`src/FluentGpu.Windows/Pal/Win32Platform.cs`).
- Resizable + collapsible sidebar (persisted width, 1:1-drag snap, `src/apps/Wavee/Features/Sidebar/`).
- Collapsing/shy hero + compact pill + parallax (`ArtistPage.Hero.cs`); `ScrollBind`/`ScrollState` slab
  (`src/FluentGpu.Engine/Animation/ScrollBind.cs`).
- Connected/shared-element morph (`Animation/ConnectedAnimation.cs`); animated live re-theme + Mica (`FluentApp.cs`).

**Engine primitives (landed, verified).**
- **Virtualization:** `VirtualListEl` + `StackVirtualLayout`/`MeasuredStackVirtualLayout` (Fenwick `ExtentTable`) +
  `GroupedListVirtualLayout` (`StickyHeaderIndexAt`/`IsHeader`) + grid layouts (`Scene/VirtualLayout.cs`); 10k rows →
  window, 0-alloc in-window scroll. `SelectionModel` (range-based, alloc-free), `Reorderable` (cross-list FLIP + edge
  auto-scroll), `PagedShelf`, `Skel.Region` skeletons.
- **Image residency:** `ImageCache` (dedup, ref-count pin/LRU, `SetPixelSink` exposing decoded **premultiplied BGRA8** at
  `ImageCache.cs:144`/`:341`) → `ImageTextureStore`/`WicImageCodec` real decode + GPU upload.
- **Animation slab:** `AnimValue` (Translate/Scale/Rotation/Opacity/Blur/Clip/Hover/Press/BrushFade, analytical spring,
  `StrokeTrim`); `Element.Enter/Exit/Stagger` with arbitrary `Easing.CubicBezier`.
- **Render:** `DrawList` gradients (linear/radial ≤4 stops + alpha), shadows, SDF strokes, rounded clip,
  `PushLayer` (Opacity/Blur/EdgeFade/**live D3D12 Acrylic**). Text: numeric `Weight`, `CharSpacing`, `LineBounds=Tight`,
  `MinSize` auto-fit; `SpanText`/`RichTextBlock` inline runs (bold/color/underline/hyperlink).
- **OS pillars:** `SystemMediaControls` (full SMTC, `src/FluentGpu.WindowsApi/Media/`), clipboard/IME seams, AOT-clean.

**Already at/near parity, explicitly NOT gaps** (don't "fix"): flex-wrap (`FlexLayout.MeasureWrap/ArrangeWrap` exist),
hero typography (numeric `Weight`/`CharSpacing`/`LineBounds=Tight`/`MinSize` — only `TrimSideBearings`/optical-margin
remain), custom cubic-bezier easing, the keep-alive nav host. Outbound OLE cross-process drag was a **deliberate design
rejection** — do not reintroduce it. The resizable `GridSplitter` is an enhancement, not a blocker (the app substitutes
adaptive breakpoints and ships).

---

## 3. Next best things — start here (ranked top 12)

Integrates the synthesis shortlist with the completeness critic's promotions (multiline caret-follow, drop-position).

| # | Item | Effort | Sev | Why it's first |
|---|------|--------|-----|----------------|
| 1 | **Dynamic palette/accent extraction from album art** | M | high | Cheapest high-leverage win — the pixel sink (`ImageCache.SetPixelSink`) **and** the `Palette` consumer (`WaveePalette`/`DetailShell`) both exist; lights up hero/detail/cards/right-panel/lyrics/shell-tint at once. |
| 2 | **ContentDialog arbitrary-content slot** (+ cancelable Opening/Closing, content-sizing) | M | blocker | Surgical change to a hardcoded `Title`+`Message` body (`ContentDialog.cs:188`); unblocks *every* dialog (create/refresh playlist, add-to-playlist, image reframe, crash recovery, feedback). |
| 3 | **Multi-line text vertical scroll + caret-follow** | S–M | high | `EditableText.cs:655/:958` give multi-line **no** vertical scroll/caret-follow; silently blocks AI composer, comments composer, and inline rename. Unstated dependency of #9. |
| 4 | **SMTC app-side wiring** | S | medium | Engine pillar is complete; wire `PlayerBar`↔`SystemMediaControls` for media keys + OS now-playing + lock-screen art. Baseline music-client credibility. |
| 5 | **In-list non-virtualized footer/leading band over the virtualizer** | L | high | Restores true virtualization to Album/Playlist pages (today eager via `DetailTracks.TrailingBody`). |
| 6 | **Sticky group headers wired into a list control** | M | high | Multi-disc albums + grouped Liked Songs (10k rows); `GroupedListVirtualLayout` math + pin flags exist, unconsumed. |
| 7 | **Async/lazy submenus + turnkey `ContextFlyout` helper** | M | high | Every track/album/artist context menu's "Add to playlist" loads data on open; `OnContextRequested` already raised. |
| 8 | **Shared multi-column model + out-of-list header sync (DataGrid backbone)** | L | high | The column-width broker every `TrackDataGrid`/library page needs. |
| 9 | **Queue panel (RightPanel) — sectioned virtualized list + drag-reorder** | L | high | Player's most visible stub (`PlayerBar.cs:329/359`); `Reorderable` + `Virtual.List` + `PlaybackBridge.Queue` present. |
| 10 | **Drop-position negotiation (insert-between vs drop-into)** | M | high | Add insert-index to `DragSession`/`DropTargetSpec` so sidebar folders + playlist drops distinguish reorder from drop-into. Foundational to #9. |
| 11 | **Streaming AI typewriter reveal + AI-card shell** | M | medium | AI About-this-album/artist/bio across Album/Artist/Show; `SpanText` substrate ready (depends on #3). |
| 12 | **Custom-draw / app-shader seam — start the design now** | XL | high | Long-pole dependency for lyrics canvas, mesh hero, audio visualizers, editorial backdrop (Wave 4). Design early. |

---

## 4. The roadmap (5 dependency-ordered waves)

### Wave 1 — Reactive color + core interaction unblocks
*Max app-surface parity per unit effort; no new dependencies.*

- **1.1 Dynamic palette/accent extraction** *(M, high)* — `PaletteExtractor` (median-cut/MMCQ) tapping `ImageCache.SetPixelSink`
  on the LQIP/bucket decode, + an async cached **scroll-batched** color service feeding reactive `Palette` signals.
  Consumers already wired (`DetailShell`, `WaveePalette`, `PlaybackBridge.TrackPalette`). Touch: `Scene/ImageCache.cs`,
  new `PaletteExtractor`; design `media-pipeline.md §4.6`, `BUILD-ROADMAP` step 35. CPU readback from the existing
  premultiplied-BGRA sink (no GPU-readback stall); per-row version latch so fast scroll never paints a stale tint.
- **1.2 ContentDialog content slot** *(M, blocker)* — host an arbitrary `Element` body (today `BuildCard` hardcodes
  `Title`+`Message`, `ContentDialog.cs:178/188`); content-driven sizing (today fixed `cardW`); cancelable `Opening`/`Closing`.
  Touch: `Controls/ContentDialog.cs`.
- **1.3 Multi-line text vertical scroll + caret-follow** *(S–M, high)* — add a vertical scroll offset + caret-follow for
  `AcceptsReturn` editors (`EditableText.cs:655/:958` currently force `scrollX=0` and early-return wheel). Unblocks every
  composer + inline rename. Touch: `Controls/EditableText.cs`, the scroll-as-transform path.
- **1.4 Async/lazy submenu population + `ContextFlyout` helper** *(M, high)* — on-open async submenu loader w/ loading
  placeholder (`MenuFlyout.cs:30/46` static today); one-property helper wiring `Element.OnContextRequested` → `MenuFlyout`
  at cursor. Touch: `Controls/MenuFlyout.cs`, `CommandBarFlyout.cs`, `OverlayHost.cs`.
- **1.5 SMTC app-side wiring** *(S, medium)* — `src/apps/Wavee/Features/Shell/PlayerBar.cs` ↔ `WindowsApi/Media/SystemMediaControls.cs`.
- **1.6 UIA provider-tree — begin the design** *(design only this wave)* — `Uia/Placeholder.cs` is an empty stub;
  `AutomationRole` is already tagged on the scene column. Start the provider-tree design now (it is the longest non-media
  pole, landing in Wave 5) — and note it is **UIA-client testable, not needs-pixels**.

### Wave 2 — The detail-page track list becomes real (the DataGrid)
*Fuse the existing virtualizer + grouped-layout + sticky-pin flags into a real grid. Depends on 1.x.*

- **2.1 In-list footer/leading band over `VirtualListEl`** *(L, high)* — measured header/footer subtrees sharing the scroll
  viewport, so footer shelves scroll *with* rows without de-virtualizing. Removes the eager `DetailTracks.TrailingBody`
  fallback. Touch: `Reconciler/VirtualListEl.cs`, `Scene/VirtualLayout.cs`. *(BUILD-ROADMAP step 29.)*
- **2.2 Sticky group headers in a list control** *(M, high)* — consume `GroupedListVirtualLayout.StickyHeaderIndexAt` +
  `NodeFlags.StickyPinned`/`ApplyPinAndFlagPass` (`ScrollBindEval.cs`); `ItemsView` has no grouping wiring today.
  Deps: 2.1. Touch: `Controls/ItemsView.cs`, `Virtual.cs`.
- **2.3 Shared multi-column model + out-of-list header sync** *(L, high)* — column-width broker pushed to every realized
  row + an out-of-list header `Grid` in lockstep; index-stable hidden-column slots; per-page column sets. `GridEl` has true
  Pixel/Star/Auto tracks; rows are arbitrary Elements. Deps: 2.1. Touch: new control in `Controls/`.
- **2.4 In-list drag-reorder gesture wiring + multi-selection drag-out** *(M, high)* — make the playlist-edit reorder
  gesture real over `Reorderable` (treated as "done" but needs gesture wiring + golden coverage); #9 (queue) depends on it.
- **2.5 Multi-select mode + floating action bar + Ctrl+A** *(M, high)* — select-mode toggle, `ItemChromeState.ShowCheckbox`
  (exists), floating multi-track action bar; heavily used across tracklists.
- **2.6 Resizable/sortable column headers + persisted width** *(M, medium)* — drag-resizable header dividers (`GridSplitter`
  over `Input/DragController`) writing clamped/persisted width; click-to-sort; "Sort by" `RadioMenuFlyoutItem` flyout (exists).
  Enhancement (app ships with adaptive breakpoints).

### Wave 3 — Player & action surfaces become usable
*Almost entirely app-composition over landed primitives.*

- **3.1 Queue panel (RightPanel)** *(L, high)* — sectioned list (Now-playing / Next up / Queued / Autoplay), drag-reorder,
  remove, per-row context menu, play-from-here. Over `Reorderable` + `Virtual.List`; `PlaybackBridge.Queue` exists. Deps: 2.4, 1.4.
- **3.2 Omnibar grouped/templated async-suggestion flyout** *(L, high)* — grouped sections + per-row templates +
  loading/error/empty + bold-match (today `AutoSuggestBox.Suggestions` is flat `string[]`). Touch: `Controls/AutoSuggestBox.cs`
  or a new templated search control over `OverlayHost`/`Repeater`.
- **3.3 Streaming AI typewriter reveal + AI-card shell** *(M, medium)* — word-by-word timed reveal over wrapping inline runs
  + `RevealCompleted` signal; thinking-placeholder. Over `SpanText`/`RichTextBlock` + `AnimValue`/`FrameClock`. Deps: 1.3.
- **3.4 Inline click-to-edit-in-place (rename)** *(M, medium)* — playlist/title rename swapping a `TextEl` for an inline
  `EditableText`. Deps: 1.3.
- **3.5 Audio-output / device picker** *(M, medium)* — wire `PlaybackBridge.Devices`/`DeviceControl` (volume already done).
- **3.6 Shell-scoped transient toast / activity-feed host** *(M, medium)* — `AddToPlaylistBar`-style toasts + infobars that
  survive navigation; recurring, currently absent.
- **3.7 Shimmer diagonal-sweep brush (animatable gradient-offset channel)** *(M, medium)* — looping masked-gradient sweep
  (skeletons currently "breathe", `SkeletonRegion.cs`); also the building block for the AI/pending **border-beam**.
- **3.8 Relocatable Compact/Expanded player (`PlayerLocation`) + scrubber chapter segments + hover timeline-preview**
  *(M–L, medium)* — the Expanded player is a major surface; name now even if partly deferred.

### Wave 4 — The two big media seams (XL; gated / needs-pixels)
*Sequence the app-shader seam first — lyrics/hero/backdrop depend on it.*

- **4.1 Per-frame app-authored custom-draw / pixel-shader surface seam** *(XL, high)* — app-supplied HLSL material + per-frame
  draw callback into a rounded-clipped sub-region, fed by app colors + time/audio uniforms. `DrawList` is fixed-POD today.
  Must not break zero-alloc phases 6–13 or leak TerraFX into Controls — design the material/uniform API portably first.
  Deps: 1.1. Touch: `Render/DrawList.cs` + RHI seam + Windows D3D12 leaf.
- **4.2 Video surface seam (hole-punch / external present)** *(XL, blocker for the video archetype)* — `IVideoPresenter` PAL
  seam mounting external D3D/media frames into the DComp tree with premultiplied-0 hole-punch + first-frame-ready gate
  (`MediaPlayerElement.cs` is chrome-only). Largest single unknown — interacts with the render-thread `ComPtr`/device-lost
  seam; descope to "audio-first parity" if time-boxed. Touch: `Seams/Pal/`, `FluentGpu.Windows` DComp; `media-pipeline.md §8`.
- **4.3 Synced lyrics engine (karaoke per-line on the playback clock)** *(L, high)* — `LyricsLayoutEngine` + `IPlaybackClock`
  seam + per-line stroke/fill cache + active-line re-record (`media-pipeline.md §9`). Per-glyph color + edge-fade mask exist.
  Deps: 4.1 + 1.1. **Note:** the `AnimValue` slab + `Marquee`/`RichTextBlock` do **not** provide word-timed canvas text — net-new.
- **4.4 Editorial 6-layer baked backdrop + mesh-gradient hero** *(L, medium)* — offscreen effect-graph bake (blur + exposure +
  radial/linear accent + procedural-noise dither + vignette) with an LRU surface cache. Deps: 4.1 + 1.1.
- **4.5 Theatre / Fullscreen presentation** *(M, medium — split out of the video seam)* — F11 + presentation-mode machine +
  a borderless-fullscreen PAL `WindowState` (today none). Lyrics/now-playing fullscreen are **non-video**, so this does
  **not** depend on 4.2.
- **4.6 3-layer art→poster→video crossfade + MiniVideoPlayer PiP** *(L, high)* — Deps: 4.2 + 4.5.

### Wave 5 — Hardening & multi-window (ship-blockers, deferrable past first visual parity)

- **5.1 UIA provider tree + live-region announce** *(XL, high — UIA-client testable, **not** needs-pixels)* —
  `Uia/Placeholder.cs` empty; `AutomationRole` already tagged. Design begins in Wave 1 (1.6). Also unblocks reorder a11y.
- **5.2 Multi-window / secondary top-levels + tear-off content migration** *(XL, high)* — `FluentApp` is single-window;
  `PopupWindowSlot` (`AppHost.cs`) proves a second swapchain; `BUILD-ROADMAP #12` plans the device/swapchain split.
  Unblocks MigrationErrorWindow + right-panel/player tear-off.
- **5.3 Localization migration** *(M, named cost)* — WaveeMusic ships **1823 `.resw` strings + ko-KR + 116 `x:Uid`**;
  the engine has a loc-keys generator but the migration itself is real work. Name it now.
- **5.4 App-wide content zoom (`ZoomContentControl`)** *(M, low)* — Ctrl +/-/0 over `LocalTransform` + DPI scaling.

**Explicitly deferred (named, not omitted):** WebView2 hosting, OS Share, the on-device-AI pillar. Out of scope for first
parity; revisit after Waves 1–4.

---

## 5. Risks, unknowns & needs-pixels

- **Needs-pixels / GPU-only (can't be proven headlessly):** real WIC decode → GPU upload under a 10k fling, acrylic
  composite pixels, video hole-punch, any app-shader output, lyrics 60 fps loop, DPI pixel-snap. Gate behind `--screenshot`
  + manual confirmation — **not** the headless suite. (UIA is **not** in this list — it's UIA-client testable.)
- **Palette quality is a tuning risk, not a build risk:** MMCQ on a downsampled bucket must avoid muddy averages and stay
  recycle-safe (per-row version latch); batch like the app's ~50-item/80 ms coalescing.
- **The video seam (4.2) is the largest single unknown** — external-frame DComp hosting + premultiplied-0 hole-punch +
  render-thread `ComPtr` ownership + device-lost rendezvous. It gates an entire page archetype; descope to audio-first if needed.
- **The custom-draw seam (4.1) crosses the RHI boundary** — must preserve zero-alloc phases 6–13 and the macOS-ready seam
  discipline (no TerraFX into Controls). Design the portable material/uniform API before coding.
- **Sequencing:** Waves 2–3 are mostly composition over existing primitives (low technical risk, high payoff). Front-load
  Waves 1–3 to make the existing pages fully functional before committing to the XL Wave-4 seams.
