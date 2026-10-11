# FluentGpu engine map — as it bears on a full theming system

Scope: `C:\wavee\fgpu\fluent-gpu\src\*` on `main` (HEAD `580b04464`), plus `docs/design` + `docs/guide`. All paths below
are relative to `C:\wavee\fgpu\fluent-gpu\src\` unless prefixed. Line numbers are from the files as read on 2026-10-10.
Read-only mapping; nothing was built or run.

---

## 0. Inventory (what exists today, one line each)

| Area | Mechanism (as built) | Where | Verdict for themes |
|---|---|---|---|
| UI declaration | Immutable `record Element` tree (`BoxEl`, `TextEl`, `ImageEl`, …) built by C# code; `Component.Render()` returns one `Element` | `FluentGpu.Engine/Dsl/Element.cs:7`, `Hooks/Component.cs:12` | Code-only; no markup, no runtime tree format |
| Node identity | `Element.Key` (string) + `ElementTypeId` (ushort, unique per record, compile-checked WGPU0003) | `Dsl/Element.cs:9,41`; `FluentGpu.SourceGen/Engine/ElementGenerator.cs:36-43` | Stable keys per subtree are the swap/reuse contract |
| Reconciliation | Keyed diff per child list (`ChildReconcilePlan`), type-tested `Mount`/`Update`, generated `DiffProps` fast path, bound→bound rewire | `Reconciler/Reconciler.cs:855-895, 931-1105`; `Reconciler/ChildReconcilePlan.cs:38-162` | In-place update when type+key match; type change ⇒ remount |
| State | Signals-first (`Signal<T>`, `FloatSignal`, `Memo<T>`), component render = one `Effect`; hooks keyed by call site | `Foundation/Signals/*.cs`; `Hooks/RenderContext.cs:20-24,502-544` | Per-consumer invalidation; no global dirty flag |
| Context (CompositionLocal) | `Context<T>` + `Ctx.Provide` ⇒ per-provider `Signal<object?>`; `UseContext` subscribes the render-effect; host `SetAmbient` for root-level channels | `Hooks/Context.cs:10-14,544-553`; `Reconciler.cs:845-851,1350-1358,1069-1082`; `Hooks/RenderContext.cs:795-802` | Exactly the CompositionLocal shape a theme registry needs |
| Props | `Embed.Comp(props, factory)` ⇒ `PropsSig`/`IPropsHost`; `[Props]`/`[Prop]` source generator; propless factory fields freeze at mount | `Hooks/ComponentEl.cs:11-69`; `Hooks/PropsAttributes.cs`; `SourceGen/Engine/PropsGenerator.cs:13-35` | Theme views must take data via props/context/signals, never factory closures |
| Colour tokens | `TokenSet` (≈90 `required ColorF` + `AcrylicSpec`), `ThemePalette{Id,Light,Dark,LightShell,DarkShell}`, `Tok.Use(palette, kind)` = pointer swap + `Epoch++` | `Dsl/Tokens.cs:77-222,230-320`; `Dsl/PaletteBuilder.cs:10-44` | Fully replaceable at runtime; closed set (no extension tokens) |
| Accent | `AccentRamp` (7 OS shades) override, theme-aware getters | `Dsl/Tokens.cs:23-36,295-317,473-489`; `FluentGpu.Windows/Pal/Win32Theme.Accent.cs:25-67` | Works for any theme as-is |
| Non-colour tokens | `Spacing`/`Radii` = `const`; `Elevation` statics keyed on `Tok.Theme`; `Motion` consts; `MotionTok.Get` = hard switch ("per-theme table TODO"); `Theme.BodyFont/IconFont` mutable statics; `Ui.Caption..Display` hard-coded sizes | `Dsl/Spacing.cs:11-32`; `Dsl/Radii.cs:10-28`; `Dsl/Elevation.cs:15-44`; `Dsl/Motion.cs:10-24`; `Animation/MotionTok.cs:13-15,170-200`; `Dsl/Theme.cs:33-34`; `Dsl/Typography.cs:39-46` | NOT theme-scoped; consts cannot change at all |
| Light/dark/HC | `ThemeKind { Light, Dark }` only; HighContrast "reserved" | `Dsl/Tokens.cs:5`; `FluentGpu.Controls/ThemedIcon.cs:17-18` | HC missing |
| Theme switch propagation | Host watches `Tok.Epoch` each `Paint`; `Reconciler.RethemeAll()` schedules EVERY component render-effect + EVERY node binding; cross-fade armed via `SetThemeTransition(250ms)`; OS material re-applied | `Hosting/AppHost.cs:1690-1709,6288-6311,6333`; `Reconciler/Reconciler.cs:655-696` | Whole-tree re-render in place (no remount); one reconcile-phase cost; colours cross-fade, geometry snaps |
| Window background | `Tok.WindowBackgroundEpoch` separate counter; clear colour read live at submit; Mica active/inactive flips it | `Dsl/Tokens.cs:258-265,320,508`; `AppHost.cs:3118,4986-5007,6302-6311,6826-6831` | Theme must author `TokenSet.WindowBackground` (inactive fallback) |
| Materials (Mica/acrylic) | DWM `DWMWA_SYSTEMBACKDROP_TYPE` + premultiplied composition swapchain; in-app acrylic `PushLayer` gated by `Materials.AcrylicEnabled` | `Win32Theme.cs:142-166`; `D3D12Device.cs:1292,1305`; `Dsl/Materials.cs:23-44`; `Render/SceneRecorder.cs:1727-1732` | Per-window attribute; no programmatic tint (doc §8 of window-backdrop-mica.md) |
| Per-control styling | Per-control `Style` records (24 controls) + `StyleHook` globals + appearance×size axes + `TemplateParts` (`::part`-style `with` modifiers) + `PartDelta` for recycled rows | `Controls/Button.cs:69-112,204-239,285-356`; `Dsl/TemplateParts.cs:34-101`; `Controls/SelectorVisuals.cs:20-38`; grep list §2.6 | Restyle-only; NO replaceable visual tree (the designed `ControlTemplate`/`ControlTheme` of controls.md §3 was never built) |
| Routing / pages | `RouteRegistry` (key → `Func<Route,Element>`), `[Route]` generator, `PageHost` over `Flow.KeepAlive` | `Controls/RouteRegistry.cs:23-72`; `SourceGen/Routing/RouteTableGenerator.cs`; `Controls/Navigation.cs:81-137` | A view-factory registry pattern already exists for pages |
| Layout | Flexbox over SoA columns (Measure/Arrange), `GridEl`, scoped relayout firewalled at boundaries | `Layout/FlexLayout.cs:10-16`; `Layout/LayoutInvalidator.cs:13-164`; `Dsl/Modifiers.cs:25` | Layout is per-node data; a theme view can change geometry freely |
| Motion | `Element.{Transition,WhileHover,WhilePressed,WhileFocus,Enter,Exit,Stagger,Layout}`, `BoxEl.Animate` (`LayoutTransition`), FLIP, `MorphId` connected animation, `NavTransition` on route roots | `Dsl/Element.cs:57-99`; `Foundation/LayoutTransition.cs`; `Animation/ConnectedAnimation.cs:8-24`; `Controls/Navigation.cs:124-136` | Declarative, data-only; theme views can carry their own choreography |
| Virtualization | `VirtualListEl` + `IVirtualLayout` family; `ListRowEl` cell rows; recyclability rules | `Reconciler/VirtualListEl.cs`; `Controls/Virtual.cs`; `Reconciler.cs:4185-4209` | Theme row templates must stay "plain" to recycle |
| Frame pipeline | 13-phase loop; UI thread reconcile/layout/capture → `SceneFramePublisher` (3-slot mailbox, last-writer-wins) → render thread records/presents | `AppHost.cs` (8.5k lines); `Hosting/Threading/SceneFramePublisher.cs:10-23` | A flush is atomic; a publication is atomic |
| First frame | `FluentApp.RunCore`: window created → accent+DWM material → device → `AppHost` ctor mounts root → `window.Show()` → loop `RunFrame()` | `FluentGpu.Windows/Hosting/FluentApp.cs:301-494` | Main window is SHOWN before the first present; `StartHidden` exists; detached windows have a reveal-after-first-present gate (main window does not) |
| Offscreen targets | Opacity/acrylic `PushLayer` groups, `RepaintBoundary` slices (retained tiles), `SurfacePool`/`LayerTargetPool`, `FeedbackSpec` (previous-frame retention), exit orphans (≤64), connected-animation overlays (≤8) | `Render/DrawList.cs:13-14`; `Dsl/Element.cs:207-221`; `Render/LayerTargetPool.cs`; `Foundation/Effects.cs:100-109`; `Scene/SceneStore.cs:47-76` | Whole-tree cross-fade is expressible with Exit/Enter on two roots; no "snapshot previous frame as texture" API |
| Declarative/data formats | Lottie JSON → `LottiePlan` → `BoxEl`/`PathEl` at mount; loc JSON via `Utf8JsonReader`; SVG path strings → `IconGeometryTable`; `path.ttf#Family` fonts; Wavee `home-layout.json` via STJ source-gen | `Lottie/*.cs`; `Controls/LottieView.cs:164-231`; `Localization/JsonResourceReader.cs:28-32`; `Controls/ThemedIcon.cs:64`; `DirectWrite/TextLayoutEngine.cs:1081-1097`; `WaveeMusic/.../Home/LayoutFile.cs:198-203` | All the AOT-safe ingredients of a theme-package interpreter exist; no view-tree format exists |
| Plugins/scripting | None (no `AssemblyLoadContext`/`Assembly.Load` in shipping src; only `NativeLibrary.TryLoad` for PlayReady) | grep §5.4 | Community themes must be data, not code |
| Perf law | 0 managed alloc in phases 6–13 (`FrameStats.HotPhaseAllocBytes`), reconcile-phase alloc bounded; DEBUG tripwires (`ReuseGuard`, `BindContract`, `RenderBudget`) | `AppHost.cs:37`; `fluent-gpu/CLAUDE.md` | A factory indirection at render time is reconcile-phase: allowed, cheap |

---

## 1. Authoring model

### 1.1 Elements (the "virtual DOM")

- `public abstract record Element` — `Dsl/Element.cs:7`. Base channels: `Key` (`:9`), `MorphId` (`:15`), `ScrollEffects`
  (`:22`), `ScrollScope` (`:27`), `Visible` as `Prop<bool>` (`:38`), `ElementTypeId` (`:41`), skeleton metadata
  (`:52-55`), the declarative motion vocabulary `Transition`/`WhileHover`/`WhilePressed`/`WhileFocus`/`Enter`/`Exit`/
  `Stagger`/`Layout` (`:63-82`), `WheelTarget` (`:87`), `RelativeTo` (`:99`). Rare channels live in a copy-on-write
  `ElementCold` block (`:43-45`).
- Concrete records and ids: `BoxEl`=1 (`:110`), `TextEl`=2 (`:759`), `ComponentEl`=3 (`Hooks/ComponentEl.cs:11-13`),
  `ContextProviderEl`=4 (`Hooks/Context.cs:544-547`), `ScrollEl`=5 (`:1044`), `VirtualListEl`=6
  (`Reconciler/VirtualListEl.cs:21-23`), `ShowEl`=7 (`Hooks/ControlFlow.cs:17-19`), `ImageEl`=8 (`:956`), `GridEl`=9
  (`:581`), `ForElBase`=10 (`ControlFlow.cs:29-31`), `PolylineStrokeEl`=11 (`:628`), `SpanTextEl`=12 (`:883`),
  `SkelRegionEl`=13 (`Hooks/SkeletonRegion.cs:51-62`), `KeepAliveEl`=14 (`ControlFlow.cs:141-143`), `IconLayerEl`=15
  (`:1020`), `PathEl`=16 (`:685`), `ListRowEl`=17 (`Dsl/ListRowEl.cs:84-86`), `SeriesEl`=18 (`Dsl/SeriesEl.cs:33-35`),
  `SpriteFieldEl`=19 (`Dsl/SpriteFieldEl.cs:31-33`). Adding one = pick a free id + handle in `Mount`/`Update`/`ChildrenOf`
  (`AGENTS.md` "Where to change what"); duplicates are a compile error WGPU0003 (`SourceGen/Engine/ElementGenerator.cs:36-43`).
- `BoxEl` is the universal container/surface: `Direction/Gap/Padding/Margin` (`:130-133`), `Fill`/`HoverFill`/
  `PressedFill`/`BorderColor` as `Prop<ColorF>` (`:135-142`), `Corners` as `Prop<CornerRadius4>` (`:153`), rich paint
  (`Shadow` `:161`, `Gradient` `:163`, `BorderBrush` `:193`, hover/pressed gradients `:196-199`, `Acrylic` `:200`),
  `RepaintBoundary`/`RasterScale`/`CompositePose` (`:207-221`), `VideoHole` (`:229`), `EdgeFade` (`:236`), the whole
  input surface (`OnClick` `:238` … `DropTarget` `:338`), `Children` (`:476`), `ZStack` (`:480`), `ClipToBounds` (`:481`),
  `IsolateLayout` (`:533`), `Animate` (`LayoutTransition?`, `:538`), `Width`/`Height` as `Prop<float>` (`:546-547`),
  flex (`:548-573`). Only `BoxEl` and `GridEl` have positional children (`Reconciler.cs:898-903`).
- `TextEl`: `Text` is `Prop<string>` (`:759`), `Size/Bold/Weight` (`:771-778`), `Color` `Prop<ColorF>` (`:797`) +
  `HoverColor/PressedColor/DisabledColor/FocusedColor` (`:802-805`), `BrushTransitionMs` (`:817`), `FontFamily`
  (`:838`, `"path.ttf#Family"` accepted), wrap/trim/`MaxLines` (`:841-845`).
- `ImageEl`: `Source` `Prop<string>` (`:962`), `Placeholder` `Prop<ColorF>` (`:982`), `BlurHash` (`:985`),
  `RevealTransition` (`:989`), `BakedBlur`, `Mask`, `Saturation`, `ColorOverlay` (`:992-1004`).
- `IconLayerEl`: `PathId` + `Tint` `Prop<ColorF>` (`:1025-1029`) — the ThemedIcon leaf.
- Factories: `Ui.VStack/HStack/ZStack/Card/Layer/Divider/Pill/…` (`Dsl/Factories.cs:9-200`); type ramp
  `Ui.Caption…Display` (`Dsl/Typography.cs:39-46`); modifiers are `with`-copies (`Dsl/Modifiers.cs:9-57`).
- `Prop<T>` (`Foundation/Signals/Prop.cs:32-90`): one field that is a static value OR a `Func<T>` thunk OR an
  `IReadSignal<T>`; the reconciler wires a bound channel into a mount-time effect that writes exactly one scene column
  ("compositor-only" for Transform/Opacity/Fill; scoped relayout for Width/Height/Text). Value equality without boxing
  (`:74-82`) is what makes the generated `DiffProps` cheap.

### 1.2 Components, hooks, memoization

- `Component` (`Hooks/Component.cs:12-16`): `Render()` returns an `Element`; hooks (`UseState`, `UseSignal`,
  `UseFloatSignal`, `UseComputed`, `UseEffect`/`UseLayoutEffect` (auto-tracked or `DepKey`-gated), `UseMemo(DepKey)`,
  `UseRef`, `UseContext`, `UseRequiredContext`, `UseContextSignal`, `UseProps`, `UseResource`, `UseLoadable`,
  `UseImage`, `UseMeasuredWidth/Bounds`, timers, animation hooks, `UseGesture`, …) — `:20-195`. `RenderWithHooks`
  (`:201-216`) is `BeginRender(); Render(); EndRender()`.
- Hook cells are keyed by `(hash(CallerFilePath), CallerLineNumber, per-render ordinal)` — `RenderContext.cs:20-24`,
  `BeginRender` `:502`, `LookupCell`/`RegisterCell` `:519-544`. So a hook inside a loop/branch keeps its cell; cells
  persist for the component's life and all cleanups run at unmount (`:432-435`).
- A component render is ONE reactive `Effect` owned by a per-component `ReactiveScope` (`Reconciler.cs:1283-1294`);
  `RunComponent` (`:1297-1348`) re-runs `RenderWithHooks` and `ReconcileSingleChild`s the result against the previous
  rendered element. `LayoutDirty` is marked only when the reconcile mutated layout shape (`:1338-1346`); a paint-only
  re-render never relayouts.
- Rule set (from `AGENTS.md` "The mental model"): three update paths — binding (compositor-only, no render),
  granular re-render (owning component subtree + scoped relayout), reactive control flow (`Flow.For`/`Flow.Show`
  keyed diff of one boundary). A `Render()` that reads no signal is inferred run-once.
- Reactive core: `Computation` with three-state CLEAN/CHECK/DIRTY push-pull cut-off (`ReactiveCore.cs:91-105,
  215-237`) — a memo that recomputes to an equal value never re-runs its subscribers. `ReactiveRuntime.Flush` runs to
  quiescence, structural queue first (`:387-452`). `Signal<T>` default comparer coalesces equal writes
  (`Signal.cs:57-68`); `Signal.AlwaysNotify` for retrigger semantics (`:13-19`).
- `DepKey` = 16-byte blittable scalar key, strings hashed, `FromRef` identity (`Hooks/DepKey.cs:21-94`).
- Memoization today = `UseMemo(deps)`, `Memo<T>`/`UseComputed`, `ContextReadSignal`, and the generated `DiffProps`
  equality (`Reconciler.cs:1084-1105`): an unchanged element record skips the column rewrite entirely.

### 1.3 Props vs frozen fields (the contract a theme view must honour)

- `Embed.Comp(() => new T { Field = v })` runs the factory ONCE; the field freezes at mount (`ComponentEl.cs:27-35`,
  `fluent-gpu/CLAUDE.md` "Plain propless factory fields freeze at mount"). Changing data flows via
  `Embed.Comp(props, factory)` → `PropsSig`/`IPropsHost.ApplyProps` (reconciler reuse seam `Reconciler.cs:953-981`),
  a signal/`Prop<T>` bind, context, or a `Key` remount. `[Props]`/`[Prop]` generate per-field signals + stable delegate
  forwarders (`PropsAttributes.cs:5-34`; `PropsGenerator.cs:13-35`). `ReuseGuard` is the DEBUG tripwire.
- `[MountOnceContent]` marks deliberately frozen `Element` slots (`PropsAttributes.cs:36-48`).

### 1.4 Reconciliation, keys, identity

- `Mount` (`Reconciler.cs:855-895`): type-tests `ComponentEl`/`ContextProviderEl`/`ScrollEl`/`VirtualListEl`/`ShowEl`/
  `ForElBase`/`KeepAliveEl`/`SkelRegionEl` (`:878-885`), else `WriteColumns` + `BindNode` + recurse children.
- `Update` (`:931-1105`): same-type `ComponentEl` ⇒ reuse the live component (props delivered live `:953-981`), type
  change ⇒ `ReplaceComponent` (`:992`). `ContextProviderEl` update writes the provider signal "iff changed" (`:1069-1082`).
  Everything else: `RecordChanged` (generated `AnyChanged`) gates `WriteColumns` + `RewireBinds` (`:1096-1105`). A bound
  channel can be re-wired bound→bound only; a static↔bound flip on a reused node is a DEBUG `BindContract` violation
  (`:1090-1094`).
- Keyed child diff (`ChildReconcilePlan.cs:70-130`): keyed children match by `Key` AND `ElementTypeId` (dictionary when
  >32 old children, linear otherwise); unkeyed children pair ordinally when the unkeyed count is unchanged, else
  positionally (`:103-123`, `UnkeyedPairing.Ordinal :188-202`). `Commit` order: updates, departing scroll capture,
  mounts, removes, `PublishOrder` (`:136-162`). Root type change ⇒ full remount (`:747-759`).
- `RenderRootDiff`: a root whose `ElementTypeId` changed is removed and remounted (`:749-758`).

### 1.5 Context / environment propagation (the CompositionLocal mechanism)

- `Context<T>` is a channel with a default (`Hooks/Context.cs:10-14`); `Ctx.Provide(ctx, value, child)` yields a
  `ContextProviderEl` (`:550-553`).
- `MountProvider` stores `(Channel, new Signal<object?>(Value))` keyed by node index (`Reconciler.cs:1350-1358`).
  `ResolveContext(anchor, channel)` walks parents to the nearest provider, then falls back to the host's ambient table
  (`:845-851`; `SetAmbient` `:649`). `UseContext` reads `sig.Value` ⇒ subscribes the render-effect, with a parked-page
  cache (`RenderContext.cs:790-802`); `UseRequiredContext` throws without a provider (`:811-820`); `UseContextSignal`
  returns a `ContextReadSignal<T>` for binds (`:572-609, 848-855`).
- Host ambients registered in the `AppHost` ctor: `Viewport.Size/Scale/Zoom`, `FrameClock.Tick/PaceableTick`,
  `HostDispatch.Post`, `SharedTransition.*`, `Activation.IsActive`, `ThemeControl.Request`, `VideoCompositor.Current`,
  `HostTimers.Current`, `InputHooks.Current` (`AppHost.cs:5232-5247`).
- `ContextStack` (`Hooks/Context.cs:560-576`) is a `[ThreadStatic]` shadow stack that the current resolve path does not
  use (resolution is node-walk based); treat as legacy.
- Cost: resolution is an O(depth) parent walk per `UseContext` call per render (not per frame); a provider value change
  re-renders exactly the subscribed consumers (no subtree walk) because the provider is a signal.

### 1.6 Invalidation scoping

- Signals ⇒ only the computations that read them (`ReactiveCore.cs:6-15`). Render ⇒ owning component subtree; layout ⇒
  `LayoutInvalidator.RunDirty` walks each dirty node UP to the nearest boundary (fixed `Width`+`Height`, no flex, clipped,
  or `IsolateLayout`/hard scroll viewport) and re-solves only that subtree; escapes to root are counted and
  size-stable-probed first (`Layout/LayoutInvalidator.cs:62-164`). `.Boundary()` modifier (`Dsl/Modifiers.cs:25`).
- Paint: `NodeFlags` dirty bits + record-dirty stamps per publication (`Scene/SceneStore.cs:108-121`), retained tiles
  and damage rects on the render side (`Render/Tiles/*`), skip-submit hash elides byte-identical frames
  (`AppHost.cs:6836-6838`).

### 1.7 Routing / page factories (the existing "view registry")

- `RouteDef(Key, Func<Route,Element> Factory)` + metadata (`Controls/RouteRegistry.cs:23-33`); `RouteRegistry.Add/
  Resolve/Fallback/BuildNavTree/BuildSearchIndex` (`:43-162`); `[Route]` attribute → generated
  `Routes.RegisterAll(registry)` with zero reflection (`:164-182`; `SourceGen/Routing/RouteTableGenerator.cs:13-28,
  152-185`). `PageHost.Create(nav, routes)` resolves the top route through the registry inside a `Flow.KeepAlive`
  keyed by `name|arg` (`Controls/Navigation.cs:94-121`) and applies `NavTransition` to the page root (`:124-136`).
  This is the closest existing precedent for "stable key → view factory resolved at render time".

---

## 2. Theming today

### 2.1 Colour tokens

- `TokenSet` (`Dsl/Tokens.cs:77-222`): a sealed record of ≈90 `required ColorF` properties (fill/stroke/text/accent/
  focus/scroll/acrylic/hero/control-alt/strong-stroke/input-active/text-control/severity/caption/window) + one
  `AcrylicSpec AcrylicFlyout` (`:173`). Fully constructible from data (`new TokenSet { … }` with every required field) —
  `PaletteBuilder.BuildWinUILight` (`PaletteBuilder.cs:387-483`) is the model.
- `ThemePalette(Id, Light, Dark, LightShell, DarkShell)` (`PaletteBuilder.cs:31`); `ShellPalette` = Wavee app-shell bands
  (`:26-28`); `PaletteSeed` → `PaletteBuilder.Build` derives both (`:10-19,44`). Presets `warm/slate/neutral/accent`
  (`Tokens.cs:232-238`; `PaletteById :283-290`).
- Active set: `public static TokenSet T` (`:247`, a plain mutable static), `Tok.Theme` (`:248`), `Tok.Use(palette,
  kind)` = pointer swap + `Epoch++` (`:272-281`). `Tok.Epoch` is a process-global int, deliberately NOT a signal
  (`:250-256`; multi-window/headless rationale).
- Generated forwards: `TokAccessorGenerator` emits `public static X Foo => T.Foo;` for every `TokenSet` property that
  `Tok` does not hand-write (`SourceGen/Engine/TokAccessorGenerator.cs:11-26`). Hand-written getters with logic:
  memoized elevation gradients per `Epoch` (`Tokens.cs:333-387`), `OnAccent` WCAG ink memoized per `Epoch` (`:431-451`),
  theme-keyed `IconBase/IconAlt` (`:459-465`), accent getters over the OS ramp (`:473-489`), `HeroGradientTop` (`:496`),
  `SystemFillAttention` (`:503`), override-aware `WindowBackground` (`:508`).
- Theme-INVARIANT statics: on-media ink/scrims (`:396-425`), `FocusThickness` const (`:492`).
- `AccentRamp` (7 OS shades, `Derive` approximation `:23-36`), `AccentSet.From(base)` for page/cover accents (`:50-71`);
  `Tok.SetAccent(in ramp)` rebuilds the accent-tinted palette and bumps `Epoch` (`:303-317`).
- `Theme` facade (`Dsl/Theme.cs:11-35`): `Theme.Dark` get/set ⇒ `Tok.Use(kind)`, `Accent`, `WindowBackground`, plus the
  two font statics `BodyFont`/`IconFont` (`:33-34`). `BodyFont` has no reader in the engine (grep: only `Theme.cs:33`);
  the real default family is the `const DefaultFamily = "Segoe UI"` in `DirectWrite/TextLayoutEngine.cs:69` and
  `D3D12/GlyphRenderer.cs:180`. `IconFont` is read by `Ui.Icon` (`Factories.cs:56`), `IconRef` (`Controls/IconRef.cs:17`),
  the recorder scrollbar (`AppHost.cs:4884`) and `SkeletonDeriver` (`:194`) — at ELEMENT CONSTRUCTION time, so changing
  it later does nothing until re-render.

### 2.2 Light/dark/high-contrast/accent, OS integration

- `ThemeKind { Light=0, Dark=1 }` — HighContrast is a comment (`Tokens.cs:5`); `ThemedIcon` defers "HighContrast auto"
  until a `ThemeKind` exists (`Controls/ThemedIcon.cs:17-18`); `HyperlinkButton` references an "E8 HighContrast pass"
  that only switches an underline (`Controls/HyperlinkButton.cs:15-16,51-54`). There is no HC palette, no
  `SystemParametersInfo(SPI_GETHIGHCONTRAST)` read, no `ISystemColors` seam (theming.md §4 marks T1 as "ADD").
- OS reads: `Win32Theme.Accent()` (registry/DWM, `Win32Theme.cs:62-75`), `SystemUsesLightTheme()`/
  `TaskbarUsesLightTheme()` (`:80-87`), `TransparencyEffectsEnabled()` (`:95-102`), `AccentLight2()` (`:121-130`),
  `ReadAccentRamp()` via `IUISettings3` hand-vtable (`Win32Theme.Accent.cs:25-67`). App facades on `FluentApp`
  (`FluentApp.cs:129-192`).
- Live follow: `WM_SETTINGCHANGE("ImmersiveColorSet")` → `Win32App.RaiseSystemColorsChanged()` (`Win32Platform.cs:
  1987-1992, 215-229`) → host `_pendingSystemColors` delivered at the top of `Paint` BEFORE the flush so the handler's
  `Tok.Use`/`SetAccent` lands in the same frame (`AppHost.cs:6016-6023`) → `FluentApp.SystemColorsChanged` relay
  (`FluentApp.cs:121,397-400`). `Materials.AdvancedEffectsEnabled`/`EffectsAreFast`/`EnergySaver` are host-written
  statics read at the ONE acrylic emission point (`Dsl/Materials.cs:23-44`; `SceneRecorder.cs:1727-1732`).
- DWM material: `ApplyWindowMaterial(hwnd, dark, mica, customFrame, micaAlt)` sets `DWMWA_USE_IMMERSIVE_DARK_MODE` +
  `DWMWA_SYSTEMBACKDROP_TYPE` (Mica Base/Alt, or transient acrylic) and the frame extension (`Win32Theme.cs:142-166`);
  re-applied on every theme change through `host.OnApplyThemeMaterial` (`FluentApp.cs:392-396`; `AppHost.cs:6298`) and
  on `FluentApp.SetWindowMaterialAlt` (`FluentApp.cs:49-54`). Popup HWNDs use `ApplyPopupMaterial` + host-backdrop
  accent policy (`:184-203`).

### 2.3 How controls read tokens, and how a token change propagates

- Controls read `Tok.*` fresh at render (`Button.ButtonPalette.For` reads Tok per call, `Controls/Button.cs:121-168`;
  `Ui.Card` `Factories.cs:162-167`). Text colours go through six shared static `Prop<ColorF>` thunks
  (`Dsl/Typography.cs:18-23`) whose identity marks them recyclable (`IsThemeTextBrush :30-37`; `Reconciler.cs:4185-4196`).
  ThemedIcon layers bind `Tint = Prop.Of(() => ResolveRole(...))` so a re-theme re-fires the tint with NO mask re-raster
  (`Controls/ThemedIcon.cs:28-32,67-72`).
- Propagation = `Tok.Epoch` polling, not signals: `AppHost.Paint` compares `Tok.Epoch` to `_lastThemeEpoch`
  (`AppHost.cs:6292`) → `OnApplyThemeMaterial` (instant DWM flip) → `_reconciler.SetThemeTransition(250ms)` →
  `_reconciler.RethemeAll()` (`:6295-6301`), disarmed after the flush (`:6333`). `RequestThemeTransition(ms)`
  (`:1709`) is reachable from app code through `UseContext(ThemeControl.Request)` (`Hooks/ThemeControl.cs:14-20`;
  ambient `AppHost.cs:5243`).
- `RethemeAll` (`Reconciler.cs:681-696`) schedules the root effect, EVERY mounted component's render-effect
  (`_comps.Values`) and EVERY node binding/control-flow boundary effect (`_nodeBindings`). Each re-render diffs in place
  (keyed hook cells, node identity and state preserved); `WriteColumns` seeds a `BrushAnim` for every fill/border/text
  colour diff at the armed duration (`:655-665`); bound channels snap (`:679-680`). Parked (KeepAlive) components defer
  the render (`RunComponent :1304`) and replay it under a per-frame budget on un-park (`:1994-1999, 2186-2194`).
- Cost: O(all mounted components + all bindings) re-runs in ONE reactive flush (reconcile phase, allocating), followed
  by a full-window repaint; layout re-solves only where the diff changed shape. The epoch baseline is taken AFTER the
  root mount (`AppHost.cs:5292-5296`) so startup injection never triggers a retheme on frame 1.
- `WindowBackgroundEpoch` is the separate counter for the clear colour (Mica active ⇒ `Transparent`, inactive ⇒
  `Tok.T.WindowBackground`, `AppHost.cs:4989-5007`); it only defeats the skip-submit hash (`:6307-6311`) and forces a
  full repaint (`:6826-6827`).

### 2.4 Non-colour tokens (the part that is NOT themeable today)

| Token family | Shape | Themeable at runtime? |
|---|---|---|
| Spacing (`Spacing.cs:11-32`), Radii (`Radii.cs:10-28`) | `public const float` + `static readonly` edges | No — consts are inlined at compile time |
| Typography (`Typography.cs:39-46`) | hard-coded `Size/LineHeight/Weight` in `Ui.Caption…Display` | No |
| Fonts (`Theme.cs:33-34`; `TextLayoutEngine.cs:69`) | mutable statics (IconFont read at construction; BodyFont unused) | Partially (IconFont, only on re-render) |
| Elevation (`Elevation.cs:15-44`) | statics selected by `Tok.Theme == Dark` | Light/dark only, fixed values |
| Motion (`Dsl/Motion.cs:10-24`, `Expressive.cs`, `MotionTok.cs:170-200`) | consts + one `switch` table; "per-theme FrozenDictionary … TODO" (`MotionTok.cs:13-15`; `AnimScheduler.Structural.cs:152`) | No |
| Materials/acrylic recipes (`Foundation/Effects.cs:249`; `TokenSet.AcrylicFlyout`) | `AcrylicSpec` statics + one TokenSet slot | One slot only |
| Icons (`Controls/ThemedIconRegistry.cs:8-25`, `Icons.Glyphs.g.cs`) | runtime registry (replaceable per name) + generated glyph consts | Yes for ThemedIcon names; glyph consts no |
| Control geometry (`Controls/ControlSize.cs:35-47`), control `Style` records | per-control statics / `StyleHook` | Per control, via `StyleHook`/`Style` overrides — no central table |

### 2.5 Per-control style objects, variants, template mechanism

- Every styled control carries a `public sealed record Style` (24 found: AppBarButton, Button, ColorPicker, CheckBox,
  HyperlinkButton, IconButton, CaptionButton, Charts/*, Marquee, NumberBox, RadioButton, RepeatButton, Segmented,
  SettingsCard, SettingsExpander, Slider, Splitter, ToggleButton, ToggleSwitch — grep `public sealed record Style`).
  Button's is the reference: 24 members, defaults from WinUI (`Controls/Button.cs:69-112`); `DefaultStyle(appearance,
  size, palette?)` composes `ButtonPalette` (colour axis) × `ControlMetrics` (size axis) (`:213-239`); `StyleHook` is a
  global `Func<ButtonAppearance, ControlSize, Style?>` consulted first (`:204`). `ButtonAppearance` {Standard, Accent,
  Subtle, Outline} (`:28-38`); `ControlSize` {Small, Medium, Large} with `ControlMetrics.For` (`ControlSize.cs:13-47`);
  `StateBrush(Rest, Hover, Pressed, Disabled)` ramps (`Controls/ControlMotion.cs:26-32`).
- `TemplateParts` (`Dsl/TemplateParts.cs:34-101`): CSS `::part`-style modifiers — a control names parts (`Button.PartRoot/
  PartGlyph/PartLabel`, `Button.cs:59-65`) and routes each built element through `parts.Apply(part, el)` then re-asserts
  mechanics-critical props (`:285-356`). Modifiers are type-preserving `with`-copies; `Children` reshaping is discouraged.
  The guide forbids new styling knobs in favour of parts (`docs/guide/control-fidelity.md §6`).
- Virtualized item chrome: `ItemsView.ContainerFactory`/`SelectorVisual` presets select the SKIN; `PartDelta` carries
  per-item value variation shape-stably (`Controls/SelectorVisuals.cs:20-56`).
- NavigationView exposes `NavigationViewOptions.Parts` + part consts (`Controls/NavigationView.cs:73,120-130`).
- Behaviour vs visuals in `FluentGpu.Controls`: visuals and behaviour are fused inside each static factory / `Component`
  (Button builds the `BoxEl` with `OnClick`, `Focusable`, `Role` and the fills together, `Button.cs:321-356`). The
  "lookless" split (`ControlTemplate<TProps>` + `ControlTheme` + `TemplateRegistry` via `Context<T>`, `ControlShell`)
  is DESIGNED in `docs/design/subsystems/controls.md §3.1-3.4` (lines 189-352) but is not in the code (no
  `ControlTemplate`, `ControlTheme`, `PartTokenId`, `TemplateRegistry` symbols exist in `src/`).
- Caveat found: `TemplateParts.TryApplyCached` caches a list-uniform prototype keyed on `(part, partsEpoch)` only, with
  the comment "theme is startup-resolved" (`TemplateParts.cs:70-83`). That contradicts the live `RethemeAll` model: a
  cached modifier that read `Tok.*` keeps the old theme after a live switch.

### 2.6 Where theme state lives (summary)

Process-global statics: `Tok.T/_palette/Theme/_accentRamp/_windowBg/Epoch/WindowBackgroundEpoch`, `Theme.BodyFont/
IconFont`, `Motion.ReducedMotion`, `Materials.*`, `ImageTransition.Default` (`Foundation/ImageTransition.cs:12`),
`ThemedIconRegistry`, `IconGeometryTable.Shared`. No per-window and no per-subtree theme scope exists.

---

## 3. Layout + motion

### 3.1 Layout

- `FlexLayout` = flexbox over SoA columns: Measure (bottom-up) + Arrange (top-down), direction 0 row / 1 column
  (`Layout/FlexLayout.cs:10-16`); within-pass measure memo (`:60-70`); `GridEl` tracks (`Element.cs:581-594`); ZStack
  (`:480`); `AspectRatio` (`:573`); wrap (`:563`). Text measured through the same DirectWrite engine that renders
  (`DirectWrite/DirectWriteFontSystem.cs:6-16`).
- Scoped relayout keyed by boundaries (`LayoutInvalidator.cs:62-94`): a theme view that changes a page's inner geometry
  stays inside the page boundary if the page root is a boundary (Wavee gives pages/cards/rows boundaries per the
  craft laws).
- Layout caches: per-node `LayoutInput`/`Bounds` columns (`SceneStore.cs:100-102`), text `TextMeasureCache` column
  (`Scene/SceneRecordingSnapshot.cs:25`), `FlexLayout` persistent first-measure record for `TryResolveSizeStable`
  (`FlexLayout.cs:68-70`). Keys are node handles, not theme/epoch — a theme switch invalidates only nodes whose
  columns changed.

### 3.2 Motion

- Declarative: `Element.Transition` (implicit on-change tween for any channel incl. Fill, `Element.cs:63`), `WhileHover/
  Pressed/Focus` targets (`:67-69`), `Enter`/`Exit` terminals (`:72-75`), `Stagger` (`:79`), `Layout` (auto-FLIP,
  `:82`), `BoxEl.Animate` (`LayoutTransition` with channels × dynamics × `SizeMode` {Reveal, ScaleCorrect, Relayout,
  Reflow, FlowReveal} × anchor, `Foundation/LayoutTransition.cs:11-134`), `HoverScale/PressScale` on leaves.
- Tokens: `MotionTok.Get(MotionTokenId)` (`Animation/MotionTok.cs:170-200`) with `ReducedMotionPolicy` as a value
  (`:18-21`); `MotionTokenDef.EffectiveDurationMs` resolves reduced motion without hook branches (`:79-90`).
  `Motion.ReducedMotion` is host-set from `SPI_GETCLIENTAREAANIMATION` (`Dsl/Motion.cs:24`; `Win32Platform.cs:1997-1999`).
  `Motion.SetLayoutTransitionsSuppressed(source)` snaps FLIP during window/app resize and scroll (`Motion.cs:26-52`).
- Engine: `AnimEngine`/`AnimScheduler` POD `AnimValue` slab keyed `(node, channel)` (`fluent-gpu/CLAUDE.md` "Animation
  (REWORKED)"); exit orphans retained up to 64 and drawn in their former visual parent (`Scene/SceneStore.cs:47-61`);
  `ConnectedAnimation` for `MorphId` shared elements, overlays ≤8, forward capture via `UseContext(SharedTransition.Begin)`
  (`Animation/ConnectedAnimation.cs:8-24,55-80`; `Hooks/SharedTransition.cs:11-24`).
- Page transitions: `NavTransition {Default, None, Entrance}` on route roots (`Controls/RouteRegistry.cs:14`;
  `Navigation.cs:124-136` seeds `Enter` + `Transition`); `KeepAliveOptions.TransitionFor(old, new)` owns every
  activation edge incl. same-key token changes (`ControlFlow.cs:113-135`; `Reconciler.cs:1797-1819`);
  `SuppressLayoutTransitionsOnActivation` snaps structural tracks on a cached page's return (`:1998-1999`). Exiting
  KeepAlive pages are `ExitFrozen` (no render while fading out, `CompEntry` `:42`, `RunComponent :1300-1304`).
- Control choreography: `NavigationSelectionMotion` (`Animation/NavigationSelectionMotion.cs`), `ControlMotion.IconPop`
  (`Controls/ControlMotion.cs:52-72`), expressive recipes (`Hooks/MotionRecipes.cs`).

### 3.3 Scroll / virtualization

- `VirtualListEl` + `IVirtualLayout`/`IMeasuredVirtualLayout` family (`Controls/Virtual.cs:12-120`; `ListOptions.cs
  RepeatLayout :21-96`): Stack, Grid, GridAuto/GridFit, LinedFlow, SpanGrid, HorizontalGrid, VariableList, Extents,
  GroupedList; signals-first bound rows `ListBound/GridBound` (`Virtual.cs:109-119`).
- Recyclability (`Reconciler.cs:4185-4209`): a row subtree recycles only if it is a PLAIN tree — no `Component`, no
  control flow, no provider, no bound `Text`, bound `Color` only via the shared theme brushes, no `OnRealized`; bound
  `IconLayerEl.Tint` is NOT recyclable (`:4205-4206`). A theme-supplied row template that embeds a component or binds
  colours ad hoc falls off the recycle path.
- `ListRowEl` (≤8 cells, `Dsl/ListRowEl.cs`) is the fast row shape (captured as a side column,
  `SceneRecordingSnapshot.cs:31-34`).

---

## 4. Render pipeline + first frame

### 4.1 Window creation and startup order (`FluentApp.RunCore`, `FluentGpu.Windows/Hosting/FluentApp.cs`)

1. `EngineSwitches.Apply(args)` (`:261`); `--fg opaque` A/B removes Mica (`:266-270`).
2. `app.CreateWindow(new WindowDesc(title, size, 1f, o.Mica, CustomFrame, MinClientSizeDip, Zoom))` (`:301-308`).
   `Win32Window` ctor: registers the class with NO `hbrBackground` (`Win32Platform.cs:733-746`),
   `CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP when composited, …)` (`:757-760`); `WM_ERASEBKGND` returns 1 (`:1900`).
3. Accent injection (`Tok.SetAccent(in ramp)` / `Theme.Accent`) (`:324-326`), `Win32Theme.ApplyWindowMaterial(hwnd,
   Theme.Dark, mica, customFrame, micaAlt)` (`:327`), `Theme.WindowBackground = Transparent` when Mica (`:328`).
   `Theme.Dark` here is whatever `Tok.Theme` already is — Wavee calls `SeedTheme()` (persisted mode or OS mode,
   `WaveeMusic/src/apps/Wavee/Shell/Shell.Host.cs:672-690`) at `:230`, BEFORE `FluentAppHarness.Run` at `:249`, so the
   DWM caption/backdrop and the first tree agree. (`Design.Install` only installs the palette seam, `Shell.UI.cs:61`.)
4. `DirectWriteFontSystem`, `D3D12Device(composited: o.Mica)`, `EnsureDeviceCreated` (`:333-343`), image pipeline (`:352-364`).
5. `new AppHost(...)` (`:366`): ctor registers ambients, wires `window.PaintRequested = () => Paint(0, keepAlive: true)`
   (`AppHost.cs:5253`), spawns the render thread (`:5272`), `MountRoot(_root)` (`:5292`) — the first `Render()` of the
   whole tree runs here, synchronously, before any present — then baselines `_lastThemeEpoch`/`_lastWindowBgEpoch`
   (`:5295-5296`).
6. `window.Show()` (`:427`) = `ShowWindow(SW_SHOW)` + `UpdateWindow` (`Win32Platform.cs:911-915`). `UpdateWindow`
   delivers `WM_PAINT` synchronously → `PaintRequested` → `Paint(0, keepAlive: true)` (`:1984-1986`), so the first
   publish can happen inside `Show()`; otherwise the loop's first `host.RunFrame()` (`FluentApp.cs:494`) does it.
7. Swapchain: `DXGI_ALPHA_MODE_PREMULTIPLIED` + `CreateSwapChainForComposition` for a composited window
   (`D3D12Device.cs:1292,1305`), `DCompositionCreateDevice` + `Commit` (`:1389,1420`); the DComp bind is deferred to the
   render thread's first present (`AppHost.cs:1585-1588`). `HasPresentedContent` latches the first real present
   (`:8071-8072`); `D3D12Device.FirstPresentQpc` (`:393`).
8. `StartHidden` (`AppOptions.StartHidden`, `FluentApp.cs:803-807`): window created, tree mounted, nothing shown until
   `FluentApp.SetWindowVisible(true)` (`:167-172`). The DETACHED child-window path has a reveal gate that shows the HWND
   only after its first present (or a timeout) (`AppHost.cs:2872-2883,3244-3282,7276,7460`; `Hooks/Context.cs:124-141`).
   No such gate exists for the MAIN window.

### 4.2 The frame (what a publication is)

- `Paint`: deliver platform events (`:6006-6023`) → theme-epoch check + `RethemeAll` (`:6288-6311`) → `FlushToQuiescence`
  (`:6319`, every scheduled render/effect/binding runs until no work remains) → virtual re-realize (`:6322`) → scoped
  layout → animation tick → capture scene snapshot (`Scene/SceneRecordingSnapshot.cs:7-12`) → `SceneFramePublisher.
  Publish` (`Hosting/Threading/SceneFramePublisher.cs:117-120`; 3-slot mailbox, last-writer-wins, skipped publications
  carry damage forward `:10-23`) → render thread records `SceneRecorder.Record` + `Present`.
- Consequence for theming: EVERYTHING written in one flush (a theme swap, a tree swap, a route change) lands in ONE
  publication. There is no intermediate frame unless the work is deliberately deferred (parked replays, image loads,
  `UseEffect` passive timing = frame N+1, `UseMeasuredWidth` = next frame).
- Clear colour: `AppHost.Clear => _childWindowBackground ?? Theme.WindowBackground` read live at submit (`:3118`);
  a change forces a full repaint (`:6826-6831`).

### 4.3 Offscreen rendering / snapshots / cross-fades (what exists)

- `PushLayer{Opacity}` renders a subtree into an offscreen RT and composites at group alpha (`Render/DrawList.cs:13-14`);
  acrylic layers blur the canvas behind (`SceneRecorder.cs:2316`); `RepaintBoundary` slices with retained tiles,
  `RasterScale` ≤1 surfaces, `CompositePose` image layers (`Element.cs:207-221`); surface pool with linear buckets and
  idle trim (`Render/LayerTargetPool.cs:25-114`); `FeedbackSpec` retains the previous frame of a boundary for trails
  (`Foundation/Effects.cs:100-109`); `BakedBlurCompositor` for image derivatives.
- Exit orphans: removed nodes keep drawing inside their former parent until their exit settles (bounded 64,
  `SceneStore.cs:47-61`); `ExitFrozen` pages (`Reconciler.cs:42`). So "old tree fades out while new tree fades in" is
  expressible today with `Exit`/`Enter` on two page roots (two full-window opacity groups during the fade).
- `IGpuDevice.TryCaptureBackBuffer` is a CPU readback that stalls the GPU (`Seams/Rhi/FrameCapture.cs:5-15`;
  `AppHost.Evidence.cs:205-218`; `--screenshot`), not a snapshot-to-texture.
- Two trees built offscreen and swapped atomically: partially. `Flow.KeepAlive` parks subtrees that stay mounted but
  detached (`_scene.Detach`, `SetSubtreeParked(parked:true)`, resources released, `Reconciler.cs:2004-2017`); they are
  not laid out or painted while parked, and their renders are deferred (`:1304`). Activation re-attaches in ONE
  structural effect (`:1986-2001`) — atomic as a publication — but the un-parked page's deferred renders are DRIPPED
  across frames (`BeginUnparkReplayWindow`/`UnparkReplaysPerFrame`, `:2186-2194`) and its images were unpinned.

### 4.4 Every way a "wrong theme for one frame" can happen today

| # | Path | Evidence | Status |
|---|---|---|---|
| 1 | Window shown before first present: between `ShowWindow` and the first `Present`, the HWND area is bare DWM Mica (no redirection bitmap, no client content). With a non-Mica/opaque theme the DWM paints the frame colour until the first present. | `FluentApp.cs:427` vs `:494`; `Win32Platform.cs:757-760,911-915`; `D3D12Device.cs:1292` | Not a theme-colour flash, but a backdrop-only frame; worse for an opaque-canvas theme (Zune) over Mica |
| 2 | DWM material applied from `Theme.Dark` at `FluentApp.cs:327` — any app that flips the theme AFTER `Run` started (e.g. inside the first `Render()`) gets a dark caption/backdrop for ≥1 frame until `OnApplyThemeMaterial` runs on the next `Paint` (`AppHost.cs:6298`). | `FluentApp.cs:327`; `AppHost.cs:5295,6292-6298` | Wavee avoids it (SeedTheme before Run) — a contract, not a guarantee |
| 3 | Theme set DURING `MountRoot` (a component calls `Tok.Use` in its first `Render`): components rendered before the call read the old tokens and are never rethemed, because `_lastThemeEpoch` is baselined after the mount. | `AppHost.cs:5292-5296` | Latent engine trap |
| 4 | Live switch: `RethemeAll` re-renders in one flush, but colour diffs CROSS-FADE for 250 ms (`SetThemeTransition`) while geometry/structure snap; bound colour channels snap (`:679-680`); the DWM backdrop/caption snaps (`:6298`). A theme that also changes layout therefore shows new geometry with mid-fade colours. | `Reconciler.cs:655-696`; `AppHost.cs:6299-6300` | By design for colour-only switches; wrong for visual-tree themes |
| 5 | Parked KeepAlive pages: `RethemeAll` schedules their effects, `RunComponent` defers (`:1304`), and on activation the replays are budgeted per frame (`:1997-1999,2186-2194`) ⇒ a cached page can come back with some components still in the old theme for a few frames (needs an empirical check, the code path permits it). | `Reconciler.cs:1304,1994-1999,2186` | Hazard |
| 6 | `TemplateParts.TryApplyCached` prototypes keyed without `Tok.Epoch` ⇒ list-uniform chrome keeps old colours after a live switch. | `TemplateParts.cs:70-83` | Bug class |
| 7 | Async image landings: placeholder/BlurHash → full-res reveal is a 220 ms cross-fade (`ImageTransition.Default`), +1-frame latency contract; a theme switch that changes `DecodePx`/corners/`BakedBlur` re-requests textures ⇒ placeholders for frames. KeepAlive park UNPINS images (`ReleaseInactiveResources`), so a pre-built hidden theme tree loses its art. | `ImageCache.cs:544-592,917,928-931`; `ImageTransition.cs:12`; `Reconciler.cs:2008-2012` | Content, not theme colour — still a visible "not ready" frame |
| 8 | Fonts: a new `"path.ttf#Family"` is loaded synchronously on first layout (`TextLayoutEngine.cs:1081-1097`); an unknown family silently falls back to Segoe UI (`:1101`); `Theme.IconFont` is captured at element construction. A theme that ships fonts pays a first-layout stall and, if the family name is wrong, renders the fallback with no error. | `TextLayoutEngine.cs:1005,1081-1110` | Silent fallback |
| 9 | Deferred subtrees: `Skel.Region` shimmer→real swaps, `UseResource` seeds, `Responsive`/`UseMeasuredWidth` (tier decided on the frame AFTER first layout, `Component.cs:116-121`) — a theme view that keys structure on measured width renders its default tier for one frame. | `Hooks/RenderContext.Measure.cs`; `Controls/Responsive.cs` | Layout jump unless heights are reserved |
| 10 | `WindowBackground` inactive fallback is `Tok.T.WindowBackground` (`AppHost.cs:5001`) — a theme whose canvas is not the TokenSet's `WindowBackground` shows the wrong inactive plate; a non-Mica theme must set `AppOptions.Mica=false` at `Run` (the swapchain alpha mode is chosen at window creation, `FluentApp.cs:335`; `D3D12Device.cs:1292`) — it cannot be switched per theme at runtime without recreating the window. | `FluentApp.cs:301-308,335`; `AppHost.cs:4993` | Structural |
| 11 | Multi-window: `Tok` is process-global; a detached video window shares the theme and gets its own `RethemeAll` on its next tick (`TickDetachedHosts`, `FluentApp.cs:495`) — one tick later than the main window. | `Tokens.cs:250-256`; `AppHost.cs:3116-3120` | One-frame skew across windows |

---

## 5. Extensibility under NativeAOT

### 5.1 Build posture

- `IsAotCompatible=true` on Engine/Controls/Windows (`FluentGpu.Engine.csproj:3`, `FluentGpu.Controls.csproj:3`,
  `FluentGpu.Windows.csproj:3`); `PublishAot=true` on the gallery and VerticalSlice (`FluentGpu.WindowsApp.csproj:15`,
  `FluentGpu.VerticalSlice.csproj:5`); `TreatWarningsAsErrors` solution-wide, trim/AOT analyzers in Release
  (`src/Directory.Build.props`). Design intent: no reflection, no `typeof`-keyed dictionaries, generated `switch`
  dispatch, `TrimMode full` (`docs/design/subsystems/dsl-aot.md:666-672,683-718,727-748`).
- Source generators (one analyzer assembly): `ElementGenerator` (marker attributes + WGPU0003), `DiffPropsGenerator`,
  `PropsGenerator`, `TokAccessorGenerator`, `ThemeBlobGenerator` (dormant: emits a `TokenId` enum + `Resolve(id, theme)`
  table only when a class is `[ThemeTokens]`, `SourceGen/Engine/ThemeBlobGenerator.cs:10-24`), `GlyphTableGenerator`
  (glyphs.json → consts), `RouteTableGenerator`, `LocalizationKeysGenerator`, `ValidatorGenerator`, COM binding
  generator (`SourceGen/Placeholder.cs:3-18`).

### 5.2 Existing declarative / data-driven paths (precedents)

- Lottie: `LottieSource.FromFile/FromUtf8` → `LottieParser.Parse` (`Utf8JsonReader`) → `LottieCompiler.Compile` →
  `LottiePlan` (nodes, tracks, path geometry minted once) (`Lottie/LottieSource.cs:6-48`); `LottieViewComponent`
  instantiates `BoxEl`/`PathEl` records from the plan at mount and seeds `AnimEngine.Keyframes` tracks in a layout
  effect (`Controls/LottieView.cs:71-231`). This is a complete data→element-tree→animation interpreter with no
  reflection: the template for a theme view interpreter.
- Localization JSON: forward `Utf8JsonReader` walk, "NO reflection, NO JsonSerializer, NO DOM" (`Localization/
  JsonResourceReader.cs:28-32`), hot-reload watcher (`LocalizationWatcher.cs`), ICU-style message formatting.
- Wavee's `home-layout.json`: STJ source-generated context (`[JsonSerializable]`, `LayoutFile.cs:198-203`) with an
  atomic `.tmp → File.Replace` store (`:263-376`) and a live `Signal<LayoutDoc>` service (`:387-436`) — the AOT-safe
  JSON + persistence + reactive-document pattern a theme/options store can copy.
- Icons: SVG path strings registered at runtime (`ThemedIconRegistry.Register`, `Controls/ThemedIconRegistry.cs:15`;
  `IconGeometryTable.Shared.Register(pathData, viewBox, …)`, `ThemedIcon.cs:64`; `IconPathParser`/`IconRaster` in
  `Render/`). `IconRef` polymorphic value (glyph | themed name | font override) (`Controls/IconRef.cs:13-36`).
- Fonts from files: `"C:\…\font.ttf#Family"` via `CreateFontFileReference` (`TextLayoutEngine.cs:1081-1097`).
- Images: `ImageEl.Source` any URL/path through `DefaultImageFetcher` + `DiskImageCache` (`FluentApp.cs:352-364`).
- Colour parsing: `Ui.TryParseColorCode("#rrggbbaa")` (`Factories.cs:103-137`); `ColorRamp`/`ColorContrast` helpers
  (`Foundation/ColorRamp.cs`, `ColorContrast.cs`) for derivation at load time.
- Runtime switches: `EngineSwitches` command-line only, no env vars (`Hosting/EngineSwitches.cs:23-83`).

### 5.3 What does NOT exist

- No markup/serialized view format, no view interpreter, no expression language, no scripting, no WASM, no plugin
  loader (`AssemblyLoadContext`/`Assembly.Load` appear only in test/probe code — grep §0), no reflection-based
  factories. Community "code" themes are out: `PublishAot` + `TrimMode full` means an installed theme cannot bring IL.

### 5.4 What it would take to load a community theme package at native speed

Package = data only: `theme.json` (manifest: id, name, version, base kind support, options schema), `tokens.json`
(colour sets per kind, non-colour scales, motion table, fonts, icons), `views/*.json` (declarative element trees per
view contract), assets (fonts, SVG paths, Lottie, images). Loading = a forward `Utf8JsonReader` parse into POD plans
(the Lottie/loc pattern), compiled ONCE into: a `TokenSet` pair + non-colour tables (§7b), icon registrations, font
file references, and `ViewPlan`s (flat node arrays with typed channel ops + binding slots). Instantiation = a
`TemplateViewComponent` that builds `Element` records from the plan on each render (reconcile-phase allocations only;
`Prop.Of` thunks read a per-instance `BindingTable` so the paint phases stay zero-alloc). No code executes from the
package; the only "logic" is a bounded binding vocabulary (prop reference, token reference, select-by-state,
format string) resolved to indices at compile time. Perf is identical to hand-written C# views after compile (records
and columns are the same), plus one plan walk per render of that view.

---

## 6. Perf characteristics relevant to a theme layer

- Allocation law: 0 managed allocations in phases 6–13 (layout → present) measured by `FrameStats.HotPhaseAllocBytes`
  (`AppHost.cs:37`) and gated in VerticalSlice; reconcile (phase 3–5) may allocate but is bounded (`fluent-gpu/CLAUDE.md`
  "near-zero-allocation"). Bind thunks and hot effects must not allocate (`AGENTS.md` rule 9). `GC.LatencyMode`
  sustained-low-latency at init (`dsl-aot.md:723-725`).
- Frame budget: display-rate pacing with adaptive GPU governor (`AppOptions.AdaptiveGpuPacing`, `FluentApp.cs:773-777`),
  warm-cadence hold 1000 ms (`:778-781`), `RenderBudget` tripwire per component render (`Component.cs:197-216`),
  `RenderCensus` names slow renders (`AppOptions.RenderCensus`, Wavee keeps it on, `Shell.Host.cs:264-267`).
- Damage: retained tile slices + per-tile damage + `Present1` dirty rects + skip-submit hash of the inline stream
  (`AppHost.cs:6825-6838`; `Render/Tiles/*`; `Seams/Rhi/RepaintDamage.cs`). A theme switch is a full repaint once.
- Retained caches: scene columns (`SceneStore`), `SpanTable` clean-span reuse (content-epoch + geometry hash), glyph
  atlas (`Seams/Text/GlyphAtlasStore.cs:51-60`) + DirectWrite shaping caches keyed by `(text, family, weight, size…)`,
  `FormatCache` for bound row strings (`Foundation/FormatCache.cs:6-14`), `StringTable` interning, image cache with
  pins/LRU/disk tier (`Scene/ImageCache.cs`), `IconGeometryTable`/`PathRealizationCache`, `TemplateParts` prototype cache,
  `Tok` per-epoch memos. New fonts/sizes = new shaping + atlas rows (one-time, render-thread uploads).
- Indirection cost of a "view factory lookup" per control/page: it runs in a component `Render()` (reconcile phase,
  only when that component is invalidated) — one dictionary probe + one delegate call + the record allocations the view
  makes anyway. It never touches phases 6–13. Compare: `RouteRegistry.Resolve` already does exactly this per
  navigation (`RouteRegistry.cs:65`), `ButtonPalette.For` switches per render (`Button.cs:123`). The measurable risk is
  not the lookup but (a) context resolution walks if the registry is provided deep in the tree (O(depth) per
  `UseContext`, cache the result in the component), and (b) losing row recyclability if a theme row view is not a
  plain tree (`Reconciler.cs:4185-4209`).
- Theme switch cost today: one flush that re-runs N component renders + M bindings (N ≈ mounted components; Wavee's
  shell + a page ≈ hundreds), reconcile diffs with generated equality, BrushAnim seeding per colour diff, then a full
  repaint. No layout unless shape changed. For a tree-replacing theme the cost becomes a full unmount/mount of the
  page tree (same order as a route change) plus image re-pin (unless prefetched) and font/glyph warm-up.

---

## 7. Gaps and the engine-level mechanisms to close them

### (a) Theme-provided views behind stable contracts — missing: a view registry resolved at render time

Today the only replaceable-visual seams are `TemplateParts` (restyle, not restructure), per-control `Style` records +
`StyleHook`, `SelectorVisual`/`ContainerFactory` for item chrome, and `RouteRegistry` for whole pages. Nothing lets a
theme replace the visual tree of a Wavee page/component while the behaviour (hooks, signals, services) stays. The
controls.md §3 design (`ControlTemplate<TProps>` + `TemplateRegistry` via context) is the right shape and was never built.

Proposed engine mechanism (Engine `Hooks/` + `Dsl/`, no new opcode/column):

```csharp
// A stable, versioned contract between core logic and a theme view. TProps is an immutable record (value-equal).
public interface IViewContract<TProps> where TProps : class { static abstract string Key { get; } }  // e.g. "wavee.player.bar@1"

// A theme view: a PURE function props+context -> Element. Named delegate (AOT-friendly, storable in a Context column).
public delegate Element ViewTemplate<TProps>(TProps props, in ViewContext ctx) where TProps : class;

public readonly ref struct ViewContext
{
    public readonly RenderContext Hooks;   // the HOST component's hooks: UseContext/UseMeasuredWidth/UseImage allowed
    public readonly ThemeDefinition Theme; // §7b (tokens, scales, motion, fonts, options)
    public readonly ViewState State;       // Rest/Hover/Pressed/Focused/Selected/Disabled bits (optional)
}

// The registry: key -> template, immutable after build, versioned by an epoch for invalidation.
public sealed class ViewRegistry
{
    public static readonly Context<ViewRegistry> Current = new(ViewRegistry.Empty);
    public int Epoch { get; }
    public ViewRegistry With<TProps>(string key, ViewTemplate<TProps> template);   // returns a new registry
    public bool TryGet<TProps>(string key, out ViewTemplate<TProps> t);
}

// The ONE element the engine adds: resolves the view for `Key` from the nearest ViewRegistry provider (falls back to
// `Fallback`), mounts the result as its child, and re-resolves in place when the registry provider's value changes.
public sealed record ViewSlotEl<TProps>(string Key, TProps Props, ViewTemplate<TProps> Fallback) : Element
{ public override ushort ElementTypeId => 20; }

// Sugar on Component:
protected Element View<TProps>(string key, TProps props, ViewTemplate<TProps> fallback) => new ViewSlotEl<TProps>(key, props, fallback);
```

Reconciler support: `MountViewSlot` creates a provider-aware boundary effect (like `MountShow`, `Reconciler.cs:882`)
that reads `ViewRegistry.Current` through the context signal, calls the template, and `ReconcileSingleChild`s. A registry
change re-runs only slot effects (no parent re-render); same key + same template identity ⇒ in-place diff; a different
template ⇒ the child subtree remounts under the slot (state in the HOST component survives because hooks live in the
host, not in the view). `Props` equality-gated like `ComponentEl.Props` (`Reconciler.cs:964-973`). A DEBUG analyzer
(sibling of FGRP001) flags a `ViewTemplate` that calls hooks positionally or captures mutable state.

Rules to publish with it: templates must be plain trees in virtualized rows (recyclability, `Reconciler.cs:4191`),
must forward the contract's `Action`/`Signal` members into `OnClick`/binds (behaviour stays in core), and must honour
reserved heights (owner rule: no layout jumps) via contract-level `MinHeight` hints carried in `TProps`.

### (b) Theme-scoped tokens and per-theme options — missing: a theme object, non-colour tables, extension tokens, options

Today only `TokenSet` colours + `ShellPalette` swap with `Tok.Use`; spacing/radii/type ramp/elevation/motion/fonts are
consts or unrelated statics; there is no per-theme option store; no HighContrast kind.

```csharp
public sealed record ThemeDefinition(
    string Id, string DisplayName,
    TokenSet Light, TokenSet Dark, TokenSet? HighContrast,          // HC: ThemeKind.HighContrast = 2 (Tokens.cs:5)
    ShellPalette LightShell, ShellPalette DarkShell,
    TypeRamp Type,                 // Caption..Display: (Size, LineHeight, Weight, Family) — Typography.cs reads it
    SpacingScale Spacing,          // replaces the consts: static readonly floats re-pointed per theme (XXS..Gutter, PageWide, Card, Inner)
    RadiiScale Radii,              // Control/Card/Overlay/Pill/Full
    ElevationSet Elevation,        // Card/CardHover/Tooltip/Flyout/DockTop/Dialog per kind
    FrozenDictionary<MotionTokenId, MotionTokenDef> Motion,   // the per-theme table MotionTok.cs:13-15 already plans
    FontSet Fonts,                 // Body/Icon/Display families ("path#family" allowed), preloaded at theme load
    MaterialPolicy Materials,      // Mica/MicaAlt/None + opaque canvas + acrylic recipes (AcrylicSpec per surface class)
    IconSet Icons,                 // name -> IconDef overrides merged into ThemedIconRegistry under a theme scope
    ExtTokenTable Ext,             // theme-defined extra colours: string -> ColorF per kind, interned to ushort ids
    ThemeOptionSchema Options);    // per-theme options (see below)

public static partial class Tok
{
    public static ThemeDefinition Definition { get; }
    public static void Use(ThemeDefinition theme, ThemeKind kind);   // ONE pointer swap for every table + Epoch++
    public static ColorF Ext(ExtTokenId id) => Definition.Ext[Theme][id];    // field read, no dictionary at paint
    public static ExtTokenId ExtId(string name);                               // interned once at load (reconcile phase)
}
```

Engine changes implied: `Spacing`/`Radii` consts become `static readonly` reads off `Tok.Definition` (consts are
inlined today, `Spacing.cs:11`), `Ui.Caption…Display` read `Definition.Type`, `Elevation.*` read `Definition.Elevation`,
`MotionTok.Get` reads `Definition.Motion` first, `Theme.BodyFont/IconFont` become `Definition.Fonts` (and the DirectWrite
`DefaultFamily` const becomes the theme body family, `TextLayoutEngine.cs:69`), `ThemedIconRegistry` gains a theme
overlay, `Materials.*` gains a theme policy input. All are Epoch-covered because `RethemeAll` re-runs every render and
binding; the one addition is that LAYOUT-affecting tokens (spacing/type) must mark `LayoutDirty` — which the diff
already does when geometry columns change (`Reconciler.cs:1342-1346`).

Options: a theme declares `ThemeOptionSchema` (key, type, default, choices, label key) in its manifest; the engine
provides `ThemeOptions` as a `Context<ThemeOptions>` with per-key `Signal<object?>` cells; `UseThemeOption<T>(key)`
subscribes the reading component; the app persists the values per theme id (Wavee's `LayoutStore` pattern). Options
flow to theme views through `ViewContext.Theme.Options`, never through app code (`no if (theme == Zune)`).

### (c) Zero-flicker startup and atomic theme switch — missing: a main-window reveal gate and a tree-swap choreography

Startup: today the main HWND is shown before the first present (`FluentApp.cs:427` vs `:494`). Engine mechanism:

```csharp
public sealed record AppOptions { public bool RevealOnFirstPresent { get; init; } = true;   // default on
                                  public Func<bool>? FirstFrameReady { get; init; } }       // app gate: theme + settings + fonts loaded
```
Implementation = reuse the detached reveal gate for the primary window: create the window hidden (`StartHidden` path),
run frames, and `Show()` on the UI thread the first time `_swapchain.HasPresentedContent` is true (the code already
does this for children at `AppHost.cs:7460`; `TryRevealDetached :3263-3282`), with the existing timeout backstop.
Pair with: `ApplyWindowMaterial` must be called with the FINAL theme kind before the reveal (today `:327`), and
`Theme.WindowBackground`/`AppOptions.Mica` chosen from the theme's `MaterialPolicy` before window creation (alpha mode
is fixed at swapchain creation, `D3D12Device.cs:1292`). Fonts referenced by the theme are pre-created on a worker
before the reveal (`IDWriteFontFace` creation is the stall at `TextLayoutEngine.cs:1081-1097`).

Atomic switch (no wrong frame, no snap-then-fade mismatch):

```csharp
public enum ThemeSwitchMode : byte { Snap, CrossFade, Choreographed }
public void AppHost.SwitchTheme(ThemeDefinition next, ThemeKind kind, ThemeSwitchMode mode, float ms = 250f);
```
- `Snap`: `Tok.Use(next, kind)` + `RethemeAll` with `SetThemeTransition(NaN)` so colours and geometry change in the same
  publication (today colours always fade over 250 ms, `AppHost.cs:6293`).
- `CrossFade` (the no-flicker default for tree-replacing themes): the host (1) marks the current root `ExitFrozen` and
  captures it as a full-window Opacity group fading 1→0 (exit orphan of the ROOT — raise `MaxOrphans` is unnecessary,
  it is one node; `SceneStore.cs:61`), (2) swaps `Tok.Use` + `ViewRegistry` provider in the same flush, (3) mounts the
  new root with `Enter` opacity 0→1 under the same `ThemeTransition` window. Both layers are already expressible
  (`PushLayer{Opacity}`, `DrawList.cs:13-14`); cost = two full-window RTs for `ms`. The DWM caption/backdrop flip is
  applied at the frame the new root first presents (`OnApplyThemeMaterial`), matching WinUI (which also snaps it).
- `Choreographed`: same, but the app's `KeepAliveOptions.TransitionFor`-style hook decides per page root.
- Pre-warm API so the incoming tree has no placeholder frames: `AppHost.PrewarmTheme(next)` loads fonts (worker),
  registers icon geometry (`IconGeometryTable`), compiles view plans, and `PrefetchImage`s the visible page's art with
  pins held until the swap (today `KeepAlive` unpins on park, `Reconciler.cs:2008-2012`, so a parked pre-built tree
  loses them — the prewarm must pin explicitly).
- Fix the two latent engine issues on the way: `RethemeAll` must run parked replays un-budgeted on activation when the
  park epoch < the theme epoch (`:1997-1999`), and `TemplateParts.TryApplyCached` must key on `Tok.Epoch` (`:75-83`).

### (d) Loadable community themes — missing: a package format, a view-plan interpreter, a safe asset loader

```csharp
public sealed class ThemePackage           // Engine/Theming/ (new folder), TerraFX-free, AOT-safe
{
    public static ThemePackage Load(string directory, ThemeLoadOptions o);   // Utf8JsonReader walk, no reflection
    public ThemeDefinition Definition { get; }        // §7b tables, validated (missing token -> base theme value, logged)
    public ViewRegistry Views { get; }                // compiled ViewPlans wrapped as ViewTemplate<TProps> per contract
    public IReadOnlyList<ThemeDiagnostic> Diagnostics { get; }
}

// views/<contract-key>.json compiles to a flat plan: nodes (type id, static channels), bindings (prop path, token id,
// state-select, format), children ranges. Instantiation builds Element records from the plan (reconcile phase);
// bindings become Prop.Of over a per-slot BindingTable (no per-frame allocation).
internal sealed class ViewPlan { NodePlan[] Nodes; BindOp[] Binds; }
internal sealed class ViewPlanComponent : Component { /* mirrors LottieViewComponent: one OnRealized capture per tracked node, built once */ }
```
Contract props exposed to JSON need no reflection: each `IViewContract<TProps>` gets a source-generated
`PropsAccessor<TProps>` (string → typed getter table; the `[Props]` generator already emits typed field access,
`PropsGenerator.cs:13-35`), so `"bind": "track.title"` resolves at compile to a slot index. Allowed node types: the
existing records (`BoxEl`, `TextEl`, `ImageEl`, `IconLayerEl`, `PathEl`, `ScrollEl`, `GridEl`, `ListRowEl`,
`SpanTextEl`), the control factories by name (`Button.Create`, `ToggleSwitch`, `ItemsView` presets) with their
`Style`/`TemplateParts` expressed as data, `Flow.Show/For` over contract lists, `Skel.Region` derived shimmer, and the
motion vocabulary (`Enter/Exit/Transition/Layout` as JSON of `LayoutTransition`/`MotionTokenId`). Assets: fonts via
`"fonts/x.ttf#Family"`, icons as SVG path strings into the theme overlay, Lottie via `LottieSource.FromFile`, images
via `ImageEl` through the disk cache. Trust model: data only, size caps, no network in the manifest, optional signature;
a malformed package fails closed to the built-in theme with diagnostics (loc JSON's "loud, never silently empty" rule,
`JsonResourceReader.cs:44-45`). Hot-reload for theme authors = `LocalizationWatcher`-style file watcher that re-`Load`s
and calls `SwitchTheme(next, Snap)`.

Residual limits to state honestly: a JSON view cannot add new paint primitives (no new opcodes — by design), cannot
run code, and cannot change swapchain alpha mode at runtime (opaque vs Mica is a window-creation decision), so a
theme's `MaterialPolicy` is applied at next launch when it differs on that axis.
