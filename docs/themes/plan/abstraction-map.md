# Wavee themes, step 3: abstracting the pages from their look (the blueprint)

Status: COMPLETE (architecture, contract catalogue, extraction and dissolution lists, engine work, four themes incl.
Zune Fluent = Zune structure × WinUI style, risks/questions), written 2026-10-11 from
`map-shell.md`, `map-pages.md`, `map-engine.md`, `research-external.md`, the WinUI canvas (39 boards on 2026-10-11, `winui-canvas.txt`
+ live read) and the Zune canvas (37 boards, `zune-canvas.txt` + live read), with the load-bearing claims re-verified against `main`
(`C:\wavee\fgpu\WaveeMusic`, `C:\wavee\fgpu\fluent-gpu`) and the PR 205 worktree (`C:\WAVEE\sidebar\WaveeMusic`).
Paths are relative to `WaveeMusic\src\apps\Wavee\` (app) or `fluent-gpu\src\` (engine, prefixed `E\`) unless absolute.
`[PR205]` marks a line number from the worktree. Nothing in the repos was edited, built or run.

Scope as fixed by the owner: Wavee Classic, WinUI, Zune and Zune Fluent (Zune structure drawn with the WinUI style,
added 2026-10-11) are complete, self-contained themes behind contracts;
core/component code never names a theme; a runtime loader for community themes must fit later without redesign; no
legacy code or switches survive; never a wrong-theme frame (startup, switch, navigation); never a layout jump.

---

## 1. The architecture

### 1.1 Decision in one paragraph

Architecture **D** from `research-external.md §7` (configuration → body at every level, compiled theme tables, typed
option records, per-surface fallback, snapshot cross-fade switching, versioned vocabulary), made concrete for this
engine as: **every surface is a core behaviour host that computes one immutable props record and mounts a view slot;
a theme is a compiled table from view keys to pure template functions plus a token definition plus a declared option
schema; the shell is just the outermost surface.** The engine gains one element (`ViewSlotEl`), one context
(`ViewRegistry`), a theme-scoped token definition that covers more than colours, a theme-options channel, a
main-window reveal gate and an atomic theme switch. Nothing in the app reads a theme id outside the catalogue and the
composition root.

### 1.2 Why not the alternatives

| Alternative | Why it was rejected for Wavee |
|---|---|
| **C** (style protocols for controls only; pages stay core with hooks) | The three looks differ *structurally*: Zune replaces the sidebar with a pivot band + row 2, puts the right panel inline, renders album pages as a panorama with no art in rows, hoists page heads into the frame; WinUI replaces the tab strip with TabView on Mica Alt, the sidebar with a NavigationView pane, the detail rail with a single content layer, puts sortable column headers on tables. Any design where pages stay core turns those into `if (nav == Zune)` reads, which is exactly what PR 205 started doing (11 reads in Entities/Home, `map-pages.md §7`) and what the owner forbids. |
| **B** (declarative documents for everything, first-party compiled from documents) | The document grammar becomes a second API the engine must freeze before any first-party theme can use a new state or slot (KDE #85). It costs an interpreter per surface and gives the first-party themes nothing they do not already get from compiled C#. It is the right *loader* later, not the right authoring model now. |
| **Token-only theming** (Flutter/Godot style: a bigger `TokenSet`, no view replacement) | Cannot express pivots vs pane vs tab strip, panorama vs rail, hoisted vs in-page heads. The product already has this and the owner calls it "messy and confusing". |
| **Three apps / three shells sharing Entities** | Triples the behaviour code (selection, drag, scrub, binder, wizard) that is the hard part, and still needs a theme switch mechanism. The behaviour hosts are the asset; only their Element output is replaced. |
| **Live-tree restyle** (walk the mounted tree and re-template, Avalonia/WinUI per-element `RequestedTheme`) | The documented flicker class (`research-external.md §4.1`); FluentGpu's `RethemeAll` cross-fades colours while geometry snaps (`map-engine.md §4.4` row 4), so a tree-changing theme would show new geometry under mid-fade colours. The switch must be a new root built off-screen in one publication. |

### 1.3 What a theme is (type and shape)

```csharp
namespace Wavee.Theming;                       // core: contracts and the catalogue, never a theme

A theme is the **composition of two layers**: a *structure* (what is where, and how it behaves when arranged) and a
*style* (how anything looks). The split is what makes the fourth built-in theme, Zune Fluent (Zune structure drawn
with the WinUI style), possible without copying Zune's views; §1.11 has the rules and their limits.

```csharp
public sealed record ThemeManifest(
    string Id,                                 // "wavee.classic" | "wavee.winui" | "wavee.zune" | "wavee.zunefluent"
    string DisplayNameKey,                     // loc key
    int ContractMajor, int ContractMinor,      // the vocabulary version it was compiled against (§1.9)
    string StructureId, string StyleId,        // "wavee.zune/structure" + "wavee.winui/style" for Zune Fluent
    string? InheritsFrom);                     // loader only (§1.4); null for built-ins

public interface IStructureLayer               // R/C/S contracts: page/shell layout views, list styles, behaviour data
{
    string Id { get; }
    ViewTable Views { get; }                   // ViewKey -> ViewTemplate, StyleKey -> list style record, built ONCE
    ThemeOptionSchema Options { get; }         // STRUCTURAL options (tabs on/off, pins in band, sidebar layout...)
    ThemePolicies Policies(in OptionValues o, in StyleMetrics m); // §1.10a; may read style METRICS (line heights...)
    ShellCapabilities Capabilities(in OptionValues o);            // §1.10b; structure-only
    RoleSet RequiredRoles { get; }             // the style roles its views use (checked against every style, §1.11)
}

public interface IStyleLayer                   // T: everything visual, addressed by ROLE
{
    string Id { get; }
    ThemeDefinition Tokens { get; }            // ENGINE (§5 E3): TokenSet light/dark(/hc), type ramp, spacing, radii,
                                               // elevation, motion table, fonts, material policy, icon overlay, ext
                                               // tokens, CONTROL STYLE TABLES (button/slider/toggle/list backplate...)
    WaveeStyleRoles Roles { get; }             // app role vocabulary (§1.11): type roles incl. Panorama/Hero/Pivot/
                                               // Numeral, ink/layer/shape/case roles, on-media ladder, stage arm,
                                               // wash alphas, motion recipes per relation
    ThemeOptionSchema Options { get; }         // STYLE options (accent, backdrop, density...)
    AccentPolicy Accent { get; }               // System | Fixed(ramp) | FromOption(OptionKey<ColorF>) | OptionSwitch(...)
    StyleMetrics Metrics(in OptionValues o);   // density-dependent extents the structure's lists/policies read
}

public sealed record ThemeComposition(ThemeManifest Manifest, IStructureLayer Structure, IStyleLayer Style,
                                      ViewOverrides? Views = null, RoleOverrides? Roles = null); // = ITheme
```

`Policies` and `Capabilities` (added while completing the blueprint, §1.10) belong to the structure. The style only
supplies the metrics they may read. Neither is a theme switch in disguise: core never learns which layer produced
them.

- `ViewTable` is an immutable map `ViewKey<TProps> → ViewTemplate<TProps>` built by the theme's static
  `Create()`; the engine's `ViewRegistry` (§5.1) wraps it with an `Epoch` and the fallback chain.
- `ViewTemplate<TProps>` is the engine delegate `Element (TProps props, in ViewContext ctx)`: a **pure function of
  the props record, the theme (tokens + options) and the host's hook context**. It may call hooks only through
  `ctx` (measured width, image, context reads). It reads looks **only through style roles** (`ctx.Style.Type.X`,
  `.Ink.X`, `.Layer.X`, `.Shape.X`, `.Case(...)`, engine control styles by role), never literal colours, sizes,
  weights or radii (§1.11 rule S1). It reads its own layer's options through `ctx.Option(key)` and composes nested
  contracts through `ctx.View(key, props)`, never by calling another layer's function. Templates are `static`
  methods; closures over mutable state are a DEBUG analyzer error.
- Layout of a built-in theme on disk: `Themes/<Name>/Structure/` (views, list styles, rules, structural options),
  `Themes/<Name>/Style/` (tokens, roles, control styles, style options), `Themes/<Name>/<Name>Theme.cs` (the
  composition). Zune Fluent is a single file `Themes/ZuneFluent/ZuneFluentTheme.cs` that composes
  `Zune.Structure` with `WinUi.Style`. All four are compiled into the one app assembly today (the app is one
  csproj under one `Wavee` namespace, `Wavee.csproj:140-146`). A csproj split needs no redesign, because the
  contracts are already the only thing a layer references (§7 Q1).

### 1.4 Registration and resolution

```csharp
public static class ThemeCatalog                 // the ONLY place that names a theme type, besides App.cs
{
    public static ITheme Resolve(string? id)     // unknown/null -> Classic (the reference theme, always complete)
    public static IReadOnlyList<ThemeManifest> Installed { get; }
}
// App.cs (composition root), before Shell.Run():
Themes.Install(ClassicTheme.Create(), WinUiTheme.Create(), ZuneTheme.Create(), ZuneFluentTheme.Create());
// ZuneFluentTheme.Create() => new ThemeComposition(manifest, ZuneStructure.Instance, WinUiStyle.Instance);
```

- Persisted key: `theme.id` (string, default `"wavee.classic"`), read synchronously in `Platform.Boot` like every
  other key (`Platform.cs:1134-1168`; `Settings.Get` is sync, `Platform.cs:968-983`).
- The active theme is a process-wide `Signal<ITheme> Themes.Current`, written only by `Themes.Switch(id)` (§1.7).
- Resolution of a view at render: `ViewSlotEl` → nearest `ViewRegistry` provider (the root provides the active
  theme's registry) → `theme.Views[key]`. **Built-in tables are complete and flat**: a Wavee.Tests test asserts every
  `ViewKey`/`StyleKey` in the catalogue is registered by each built-in theme (missing key = red test, not a runtime
  fallback). A built-in theme that does not redesign a surface registers a block from `Themes/Common/` explicitly
  (§6.5); it never inherits another built-in theme's view. `InheritsFrom` is kept for the later loader only: a
  package's omitted keys are copied from its declared base theme **once, at load**, into a flat table (no chain walk
  at render). There are **no default views in core**: a core default would be one theme's look in disguise, which is
  where switches regrow. (Amended: the earlier draft let built-ins fall back to Classic per key; that made Classic an
  implicit base and let Classic option reads leak into other themes, so it is replaced by explicit `Common` blocks.)
- Acceptance: `grep -rn "Wavee.Themes\." src/apps/Wavee --include=*.cs` returns only `App.cs` and
  `Theming/ThemeCatalog.cs`; a Wavee.Tests source-scan test enforces it (the project already runs source-shape tests,
  e.g. `BootOrderingTests.cs`).

### 1.5 Layering (what is core, what is theme)

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ 4  STYLE LAYER (§1.11)         ThemeDefinition (engine) · WaveeStyleRoles (app roles) · style options   │
│    per theme                   option values persisted per theme id · AccentPolicy                       │
├────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 3  STRUCTURE LAYER (§1.11)     ViewTable: ViewKey -> static template; structure rules (pixel ladders,     │
│    per theme                   breakpoints, list styles, policies, capabilities); looks ONLY via roles    │
├────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 2  SURFACE CONTRACTS           Theming/Contracts/*.cs: TProps records (immutable, value-equal, init-only),│
│    core, versioned             I…Verbs interfaces, state enums, ViewKey<TProps> constants, ContractVersion│
├────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 1  CORE STATE + BEHAVIOUR      Entities/Playback/Spotify/Store (unchanged) · surface HOSTS (components    │
│    core                        owning hooks, signals, selection, drag, scrub FSM, binders, wizard FSM,    │
│                                persistence) that build TProps and mount View(key, props) · Palette math  │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Rules that hold the layers apart:

1. **Core never builds presentation.** A host's `Render()` ends in `View(ViewKey.X, props)`; it contains no `BoxEl`,
   no `Tok.*`, no sizes. The only Elements core still constructs are the engine controls that *are* behaviour
   (ScrollView, ItemsView/VirtualList hosts, EditableText, MediaPlayerElement) and those are passed to templates as
   `Slot`s with their chrome delegated back to the theme (§5.5).
2. **Themes never read state.** A template receives everything through `TProps` (values, `Prop<T>` binds for hot
   channels, verb interfaces); it never touches `Entities.*`, `Playback.*`, `Shell.*`, `Platform.Settings`, `Prefs.*`.
   A DEBUG analyzer + a source-scan test enforce "no `Wavee.Entities`/`Wavee.Playback`/`Platform.Settings` symbol in
   `Themes/`".
3. **Contracts are data.** No `Element` members in a props record except the three documented opaque slots (§2.0);
   nested surfaces are nested props records, not pre-built trees.
4. **Options are the theme's.** Core never reads a theme option; a theme never reads another theme's option.
5. **User preferences stay core** (light/dark mode, zoom, reduced motion, marquee, daylist clock, tempo/plays columns,
   stage content prefs, lyrics/video/playback prefs, developer mode): they arrive in props as facts (§4).

### 1.6 Theme options: declared, persisted, read

```csharp
public abstract record ThemeOption(string Id, string TitleKey, string? DescriptionKey, OptionGroup Group);
public sealed record ToggleOption(string Id, …, bool Default) : ThemeOption;
public sealed record SelectOption<TEnum>(string Id, …, TEnum Default, IReadOnlyList<SelectChoice<TEnum>> Choices, PickerStyle Picker /* radio | cards | combo */) : ThemeOption where TEnum : struct, Enum;
public sealed record SliderOption(string Id, …, float Default, float Min, float Max, float Step, string? UnitKey) : ThemeOption;
public sealed record ColorOption(string Id, …, ColorF Default, IReadOnlyList<ColorF>? Swatches) : ThemeOption;
public sealed record HeadingOption(string Id, string TitleKey) : ThemeOption;

public readonly record struct OptionKey<T>(string Id, T Default);   // declared as static readonly fields in <Theme>Options.cs
public sealed record ThemeOptionSchema(IReadOnlyList<ThemeOption> Options);
```

- **Declared** by the theme as typed C# (`ClassicOptions.RowStyle = new OptionKey<RowStyle>("rowStyle", RowStyle.Modern)`
  plus the matching `SelectOption` in the schema). A later package loader reads the same shape from JSON.
- **Persisted** by core, keyed `theme.<themeId>.<layer>.<optionId>` (layer = structure | style, §1.11 S5) through `Platform.Settings` (sync, pre-window), typed by the
  schema (int for enums, bool, float, uint ARGB). Values survive theme switches (Rainmeter rule). Migration rules
  (Millennium): renamed option ⇒ default; removed choice ⇒ default; removed option ⇒ ignored, key left in place.
- **Read** by templates through `ctx.Option(ClassicOptions.RowStyle)`: the engine `ThemeOptions` context (§5.3) holds
  one `Signal<T>` cell per declared key; the read subscribes the *slot's* effect, so an option change re-renders only
  the slots that read it (Kodi `NeedLoad()` at slot granularity, for free from the reactive core).
- **Rendered** by the generated Settings › Appearance › "<Theme name> options" section from the schema (§2.6
  `Settings.ThemeOptions`). No theme ships settings UI. Pickers with previews (today's density/row-style/cover cards)
  become `PickerStyle.Cards` whose card faces are themselves a contract (`Settings.OptionPreview`) the theme renders.
- **Theme-scoped view state** (rail widths, pane widths, library view size, section collapse) is persisted the same
  way (`theme.<id>.state.<key>`) but is not in the schema: it is written by hosts through a `ThemeState` service with
  keys the theme declares (`StateKey<T>`), never from inside a template.

### 1.7 Switching without a wrong frame (end to end)

Startup (replaces the four duplicated theme-apply routines, `map-shell.md §0.2` item 1):

1. `Platform.Boot` reads `theme.id`, `theme.mode`, every `theme.<id>.*` value (sync).
2. `Themes.Install(...)`; `Themes.Current = ThemeCatalog.Resolve(id)`.
3. `Appearance.Seed()` (ONE owner, core): `kind = ThemeMode.Resolve(mode, FluentApp.SystemUsesLightTheme())`;
   `Tok.Use(theme.Tokens, kind)`; accent per `theme.Accent` (`SystemAccentRamp()` | fixed | option value);
   `ThemeOptions.Seed(theme.Options, store)`; fonts referenced by `theme.Tokens.Fonts` pre-created on a worker.
4. `AppOptions` built from the theme: `CustomFrame = true` and a **composited (premultiplied) swapchain always**
   (today's Mica path), because the alpha mode is fixed at creation (`E\FluentGpu.Windows\D3D12\D3D12Device.cs:1292`)
   and WinUI's own `Backdrop` option (Mica · Mica Alt · Acrylic · None) must switch live. An opaque theme (Zune's
   #161616 ground, WinUI `Backdrop = None`) paints its ground as the root fill and sets the DWM backdrop to none; the
   initial `DWMWA_SYSTEMBACKDROP_TYPE`, dark-mode attribute and `WindowBackground` come from
   `theme.Tokens.Materials.For(options, kind)` before the window is shown (§5 E6, E10).
5. `RootHost.Render` wraps `Shell.Frame()` in `Ctx.Provide(ViewRegistry.Current, registry)` +
   `Ctx.Provide(ThemeOptions.Current, options)`; every surface resolves its view from the right theme in the very first
   `MountRoot`.
6. `AppOptions.RevealOnFirstPresent = true` (§5.4): the HWND is shown only after the first present, so even the
   backdrop-only gap of today (`map-engine.md §4.4` row 1) is gone.

Live switch (`Themes.Switch(id)`, from Settings or the command palette):

1. `next = ThemeCatalog.Resolve(id)`; `AppHost.PrewarmTheme(next)` on a worker: fonts, icon geometry, the visible
   page's artwork pinned (`§5.4`), option cells seeded.
2. `AppHost.SwitchTheme(next.Tokens, kind, ThemeSwitchMode.CrossFade, ms)` with an app callback that, inside the same
   flush, writes `Themes.Current`, swaps the `ViewRegistry`/`ThemeOptions` provider values and remounts the content
   root under a new key. The engine marks the old root `ExitFrozen`, fades it as one opacity group, mounts the new
   root with an enter fade, applies DWM material at the first present of the new root. One publication contains the
   complete new theme; the fade is between two complete frames (View Transitions / Uniwind model).
3. State lives outside the tree, so nothing crosses: navigation (`Shell.Nav`), selection, playback, rail open/mode,
   scroll offsets (captured per `SlotKey` into `Nav.ScrollMemory` before the switch and restored by the content host
   on mount; the engine already captures departing scroll positions in `ChildReconcilePlan.Commit`), focus (the
   focused route's host re-requests focus on mount).
4. `KeepAlive` page slots are dropped with the old root (their parked trees are the old theme's); the new root
   re-creates only the current page; back/forward pages are rebuilt on demand (same cost as a route change).
5. Persist `theme.id` after the switch has presented, not before (a crash mid-switch restarts in the old theme).
6. (Added) Things that must NOT remount with the root: the video presenter (DComp video hole / `MediaPlayerElement`)
   is placed through a persistent slot that the engine re-parents into the new root (§5 E7), so playback never shows a
   black frame; OS fullscreen (stage, fullscreen video) is owned by core state, not by a view's mount effect (today
   `Stage.UI.cs:318-324` and `Video.UI.cs:1388-1393` enter/restore fullscreen in mount/unmount effects, which a root
   swap would turn into exit + re-enter — §3.1 row X12); transient popups (menus, flyouts, tooltips, teaching tips)
   are closed before the swap; modal flows whose state lives in a core model (setup wizard, feedback, crash prompt,
   sign-in door, release-notes plate) re-render open in the new theme.
7. (Added) Detached windows (video pop-out) switch in the same tick as the main window (§5 E5), not one tick later.

Option change (within a theme): no remount, no switch; the slots that read the option re-render in place; a slot that
changes geometry declares a `LayoutTransition` in its template if the theme wants animation (user-initiated in
Settings, so a relayout is expected; it must still be animated or height-reserved per the no-jump rule).

Light/dark flip (independent of theme): unchanged engine path (`Tok.Use(kind)` + `RethemeAll` cross-fade) because every
built-in theme's templates are geometry-stable across kinds (acceptance test: identical layout tree light vs dark).

### 1.8 Where the vocabulary lives

`Theming/Contracts/` (core): one file per surface family holding the `TProps` records, verb interfaces, state enums and
the `ViewKey` constants (`ViewKeys.Shell`, `ViewKeys.Pages`, `ViewKeys.Components`). `Theming/ContractVersion.cs`
holds `Major/Minor`. The catalogue in §2 is that folder's table of contents.

### 1.9 Versioning so a loader fits later

- `ContractVersion.Major.Minor`. **Additive** changes (new `ViewKey`, new optional props member with a default, new
  enum value appended, new option kind) bump Minor; **removals/renames/semantic changes** bump Major. Built-in themes
  compile against the current version; a Major bump is a compile error they must fix (Kodi's per-release orphaning is
  designed out by making additions the norm and removals rare and loud).
- Every `ViewKey<TProps>` is a string id with a `Since` version: `"wavee.player.bar@1"`. Props records are
  `sealed record` with `init` members and defaults; state enums are append-only; verbs are interfaces whose members are
  only ever added.
- A later package declares `contract: { major: 1, min: 3 }`; the loader gates `[Major == 1 && Minor >= min]`, fills
  omitted keys from `InheritsFrom`, and compiles documents against the same `TProps` through a source-generated
  `PropsAccessor<TProps>` (the `[Props]` generator already emits typed accessors, `E\FluentGpu.SourceGen\Engine\PropsGenerator.cs`).
  Because every theme-visible fact already passes through a `TProps`, the document grammar is a serialisation of these
  records: no redesign.
- The manifest/discovery/budget posture is reused from `Wavee.Sdk\ModuleManifest.cs` + `ModulePage.cs`
  ("unknown is skipped, over-budget is rejected"), which the app already treats as untrusted input.


### 1.10 Four mechanisms added while cataloguing (they close gaps the canvases exposed)

The full catalogue (§2) showed four things the first draft of §1 could not express without a switch creeping back.

**(a) Policies: geometry that behaviour depends on.** Some core behaviour needs numbers that only the theme knows:
- the pane mode ladder decides when the nav becomes an overlay drawer. Classic uses 700/660/528 (`Sidebar.Resize.cs:26-100`). WinUI uses expanded ≥1100, compact 660–1100, minimal <660 (canvas W-ThemeOptions). Zune has no pane; its panel is inline ≥820 and floats below (Z-ReleaseNotes, Z-Frame-Narrow);
- the player-bar overflow menu holds exactly what the bar dropped (`PlayerBarRules.Overflow`, `Shell.PlayerBar.cs:330-344`), and the tier ladder decides what drops (`Shell.cs:1195-1323`);
- the chrome allocator (`Shell.Chrome.cs:26-513`);
- sidebar drop hit-testing works from row extents (`RootlistSlotResolver`, `Sidebar.cs:1339-1346`);
- virtualized lists need analytic extents before they measure anything (the no-jump rule).

These become typed, versioned **policy records** that the theme computes from its own option values:

```csharp
public sealed record ThemePolicies(
    WindowBandPolicy Bands,          // band thresholds + hysteresis; replaces SidebarPaneModeRules constants
    NavPanePolicy NavPane,           // Expanded/Compact/Overlay/None per band, width bounds, resizable?, seam width
    PanelPolicy Panel,               // right panel: docked width bounds, inline-vs-floating threshold, full-height?
    PlayerBarPolicy PlayerBar,       // tier ladder (thresholds, hysteresis) + per-tier visible-slot set
    ChromePolicy Chrome,             // allocator budget table (today Shell.Chrome.Layout constants)
    StagePolicy Stage);              // aspect-class thresholds the stage chrome uses
```

Core state machines (pane mode, drawer, panel float, overflow composition, allocator) take the policy as a parameter. Their pure rules stay in core and stay unit-tested, now as functions of `(facts, policy)`. A policy is data, so this is not a switch: core never knows which theme produced it. Lists get their extents from the theme's style record (§1.10c), not from a policy.

**(b) Capabilities: facts that change what core offers.** WinUI's "Tabs in the title bar = Off" keeps one page per window. Zune has no tabs at all, no nav tree (playlists live under library › playlists), and no Friends panel tab (Z-Frame-Panel). So core must know, for example, not to offer "Open in new tab" or Ctrl+T, and not to open the Friends panel.

```csharp
public sealed record ShellCapabilities(bool Tabs, bool NavTree, bool FriendsPanel, bool ForwardButton,
                                       bool HistoryMenu, int MaxBandPins /*0 = pins live in the nav tree*/);
```

Core verbs and menu models read capabilities: the tab verbs, the `Track.Menu` "open in new tab" row, the `RailMode.Friends` toggle, and the history-menu right-click. The theme derives capabilities from its option values. They are recomputed on every option change, and a change runs one core reaction. For example, `Tabs` true→false collapses the workspace to its active tab. The other tabs stay persisted and come back when Tabs returns (§7 Q4).

**(c) Style keys: list-shaped contracts.** A theme cannot hand back a finished `Element` for a virtualized list. Recycling, selection, drag, keyboard and scroll anchoring are core behaviour (`TableHost`, `Track.Table.cs:402`). The engine's recyclability rules also forbid components in rows (`E\FluentGpu.Engine\Reconciler\Reconciler.cs:4185-4209`). So list-shaped surfaces use a **`StyleKey<TStyle>`**: the theme returns a record of row and header templates plus extents and column policy, and the core host builds the `ItemsView` from it. This is the "layout object + item factory" seam from `research-external.md §2.2`.

```csharp
public sealed record TrackListStyle(
    int Stamp,                                   // theme-computed hash of the options it read; part of every cache key
    Func<TableTier, TableFlags, ColumnSet> Columns,
    Func<RowKind, float> Extent,                 // analytic extents (no-jump); group headers included
    BoundRowTemplate<RowPresentation> Row,       // MUST be a recyclable plain/bound tree (E13 DEBUG check)
    ViewTemplate<TableHeaderProps> Header, ViewTemplate<GroupHeaderProps> GroupHeader,
    ViewTemplate<TableChromeProps> Chrome,       // command bar / list bar / filter card trigger
    ViewTemplate<ListStateProps> Placeholder);   // shimmer / failed / empty / no-match, sized like the rows
```

**(d) Host slots, the page-chrome channel and theme state.**
- *Host slots.* A theme view places a core behaviour host through a typed `HostSlot<TArgs>` carried in its props. Examples: the track table inside an album view; the lyrics host inside a panel; the video presenter; a visualizer face. `slot.Place(args)` returns the core host component, keyed. The theme decides where the host goes; core owns what it does. There are exactly three kinds of opaque member in a props record (§2.0): `HostSlot<T>`, `MediaSlot` (core content renderers: video hole, visualizer face, deck face, Liked cover treatment) and `Prop<T>` bindings for hot channels.
- *Page-chrome channel.* Some page heads are drawn by the frame, not the page. Zune puts the page's views, its scroll-spy tabs and its actions into the band's row 2, and draws breadcrumbs in the title bar. So every page host always publishes one core `PageChrome` record (title, crumbs, views, band pivots, actions, material claim, backdrop) through a core channel that generalises PR 205's `Shell.PageViews`/`PageBands`/`Mastheads`/`ShellMaterial` (`[PR205] Shell.cs:2344-2484`, `Design.cs:2299`). The active theme's frame view decides whether and where to draw it. The same theme's page view decides whether to draw its head in-page or leave it to the frame. Both decisions sit inside one theme, so `HoistedFor(navStyle)`, `PresentedNavStyle` and `BandLayout.InRow2` disappear (§4).
- *Theme state.* User-sized view state belongs to the theme because each theme sizes it differently: rail/panel widths, Classic's detail-rail widths, library column widths, Classic's sidebar width and Library list/grid. Core hosts persist it through `ThemeState.Set(StateKey<T>)` under `theme.<id>.state.<key>`, with keys and bounds declared by the theme. Templates never write settings directly.
- *Option writes from templates.* A template may write **its own** theme's declared options from a user gesture, through `ctx.Options.Set(key, value)`. Examples: Classic's table ⋯ › Row size, and the Liked cover picker. The option store persists the value and re-renders the slots that read it. Writing another theme's key is a DEBUG assert. Core code never writes theme options.

### 1.11 Structure × style: composing Zune Fluent (owner requirement, 2026-10-11)

**Requirement.** A fourth built-in theme combines Zune's *structure* with WinUI's *visual design*:
- Zune structure: pivots instead of a sidebar, panorama pages, the inline full-height right panel, big numerals.
- WinUI visual design: the Segoe UI Variable ramp, 4/8 px radii, Mica layers, WinUI buttons/sliders/list backplates, accent used sparingly.

It must not copy Zune's views and must not branch on theme identity. This is a pressure test: if any Zune view hard-codes a Zune look, Zune Fluent cannot exist.

**The two layers and who owns what**

| Concern | Layer | Notes |
|---|---|---|
| Which surfaces exist and where they sit (frame arrangement, band vs pane vs tabs, panel inline/floating, panorama scroll, page section order, three-pane library) | Structure | R/C views |
| List row composition (which cells, numerals vs art, group headers) and list extents formula | Structure | S records, but the extents are computed from style metrics (`StyleMetrics.Row(density)`, type line heights) |
| Behaviour-relevant geometry (bands, tiers, panel threshold, pins count) | Structure | policies (§1.10a), which may read `StyleMetrics` |
| Capabilities (tabs, nav tree, friends) | Structure | §1.10b |
| Every colour, layer material, elevation, stroke | Style | `Ink.*`, `Layer.*` roles; Mica vs opaque is a style decision |
| Type: family, size, line height, weight, tracking for each **role** | Style | the role vocabulary includes roles a structure needs even if the style's own structure never uses them: `Panorama`, `Hero`, `Pivot`, `SubPivot`, `Numeral`, `NumeralLarge`, `NowPlayingDisplay` |
| Text casing of theme-written labels | Style | `Roles.Case(LabelKind)`: Zune = Lower, WinUI = AsWritten. Never applied to user/content strings |
| Shapes: corner radii per shape role, focus ring, button silhouettes | Style | `Shape.Card`, `Shape.Cover`, `Shape.Control`, `Shape.Overlay`, `Shape.PrimaryAction` (Zune: full circle 36 with 2 px ring; WinUI: 32 r4 accent) |
| Control looks (buttons, toggles, sliders, chips, list backplates, menus, dialogs) | Style | engine control style tables (§5 E14), addressed by **control roles** (`PrimaryAction`, `SecondaryAction`, `QuietIcon`, `Choice`, `ListBackplate`) |
| Motion curves/durations; page-transition *recipe kind* | Style supplies durations, easings and reduced-motion policy. The structure picks the recipe kind per relation (panorama slide vs entrance fade) | a structure may name a choreography, never a duration |
| Spacing scale | Style | the structure references `Space.Gutter`, `Space.Section`, `Space.Item`; Zune style gutter 48, WinUI 36 |
| Iconography (glyph set) | Style | `Icons` overlay; the structure references icon **roles** (`Icon.Queue`, `Icon.Devices`) |
| Command presentation (word vs icon vs icon+label) | Style **role** `CommandPresentation(context)`, with a structural floor (§7 Q15) | Zune bar: words ("lyrics queue devices"); WinUI: icons |

**Rules that make composition safe (enforced)**
- **S1, no literal looks in structure.** Files under `Themes/*/Structure/**` may not contain colour literals (`ColorF`/hex/`FromRgba`), numeric `Size`/`Weight`/`LineHeight`/`Corners`/`BorderThickness`, font family strings, `ToLower`/`ToUpper` on display strings, or direct `Tok.*` colour reads. They read `ctx.Style.*` roles and engine controls by role.
  - Enforcement: a Wavee.Tests source scan plus a DEBUG analyzer. Numeric literals in structure are allowed only for layout geometry in `Structure/Rules/*` tables (panel 340, pins 6, column fractions), and those tables must express type-dependent heights through `StyleMetrics` (e.g. band height = pivot line + sub-pivot line + gaps, not 84).
- **S2, style layers are complete.** Every style implements the whole versioned role vocabulary (`WaveeStyleRoles` contract `Major.Minor`, like §1.9). A test composes every shipped structure with every shipped style and asserts that `structure.RequiredRoles ⊆ style.Roles`.
  - The WinUI style must therefore define `Panorama`, `Numeral` and `Pivot` (e.g. Panorama = Display 68/92 600, Numeral = Subtitle 20/28 400, Pivot = Title 28/36 600 for selected, 400 at rest), even though the WinUI structure never uses them.
- **S3, the cross-product renders.** A headless probe renders all nine structure/style pairs (3 structures × 3 styles; 4 ship as themes, the other 5 are test-only) and checks that no role is missing and no text is clipped by a fixed height.
- **S4, no identity reads.** A layer never asks which theme, structure or style it is composed with. A style cannot add views; a structure cannot add tokens.
- **S5, options are per layer and persisted per theme.** A composed theme exposes the union of its structure's and style's options.
  - Ids are layer-qualified (`structure.pins`, `style.accent`) and persisted as `theme.<themeId>.<layer>.<option>`. Zune and Zune Fluent therefore keep independent values.
  - A layer reads only its own options. A structure can respond to a style option only through `StyleMetrics` (e.g. density → row extents), never by reading the option.

**Limits of `basedOn` / derivation (stated, not hidden)**
1. Composition is at **layer granularity**, plus an explicit, counted list of per-key overrides (`ViewOverrides`, `RoleOverrides`). Zune Fluent ships with zero view overrides. Its role overrides are ≤ a handful (§6.4), each with a reason.
2. No partial mixing inside a layer: a theme cannot take "Zune's album page but WinUI's artist page" except through a named view override. Overrides count against a budget and are visible in the theme's table.
3. A view override must come from a structure built for the same style vocabulary version (S2). It does not inherit the overridden theme's options.
4. Policies stay structure-owned. A style changes geometry only through `StyleMetrics` (row height, line heights, control heights), so the structure's band/tier ladders stay correct when, for example, WinUI's 40-px rows replace Zune's 52-px rows.
5. A style cannot make a structure show or hide a surface (capability), and a structure cannot pick a material (Mica vs opaque).
6. The later loader composes the same way: a package may ship a style only, a structure only (rare), or both, each declaring the vocabulary version it targets.

**What this changes in Zune's views (the rule that prevents breakage).** The Zune canvases freely mix look and structure (lowercase words, 120/200 panorama, 0-radius tiles, round metro buttons, flat accent chips). The Zune structure must express each of these as a role:
- Panorama title: `Type.Panorama` in a non-wrapping, edge-bleeding `PanoramaTitle` layout. The *bleed* is structure; size and weight are style.
- Track numbers: `Type.Numeral`.
- Lowercase: `Case(Label)`.
- Tile corners: `Shape.Tile`.
- Play: `ctx.Control(PrimaryAction, ...)`.
- Chips: the `Choice` contract (§2.6.6).
- Seek line at the bar's top edge: structure (placement) drawn with `Ink.Accent` / `Shape.SeekTrack` (style).

Under Zune Fluent the same views give Segoe-Display 68 sentence-case panorama titles, 4-px tiles, WinUI accent buttons and Mica layers.

---

## 2. Contract catalogue

### 2.0 Contract grammar (applies to every entry)

- **Key**: `ViewKeys.X` (`ViewKey<TProps>`, id `"wavee.<family>.<name>@<since>"`) or `StyleKeys.X` (`StyleKey<TStyle>`).
- **Props**: `sealed record XProps` with `init` members and value equality. Allowed member types:
  - values: primitives, strings, `StringId`, `ColorF`, enums, nested props records, `ImmutableArray<T>` of props records;
  - `Prop<T>` for hot channels (position, volume, scroll, progress), which bind without re-rendering;
  - verb interfaces `IXVerbs`, implemented by the core host;
  - opaque kinds: `HostSlot<TArgs>`, `MediaSlot`, `ItemSource<T>` (a virtualized data source and its controller hooks).

  No `Element` members, no theme ids, no `Platform.Settings`.
- **Verbs**: intent interfaces. Each member is one user intent (`TogglePlay()`, `Seek(ms)`, `Navigate(Link)`). Their implementations are core, and the template only forwards input to them.
- **States**: an append-only enum or flag set, published in the props. A template never re-derives a state from raw data.
- **Level**, which says what a theme replaces:
  - **R** region/page: the whole surface's Element tree;
  - **C** component: one sub-contract inside a region;
  - **S** list style record (list-shaped, §1.10c);
  - **T** tokens/policy only (no view).

  **R, C and S belong to the STRUCTURE layer** (§1.11). Their templates read every look through STYLE roles.
  **T and the control looks belong to the STYLE layer.** So Zune Fluent takes all R/C/S entries from Zune's
  structure and all T entries from WinUI's style.
- **Origin**: **NEW** core abstraction, or **REUSE** an existing record (with the path:line it comes from). "Today" names the code that becomes the host or state.
- **Not repeated per entry**: policies, capabilities and the page-chrome channel (all §1.10). Every page host publishes `PageChrome` (§2.1.9).

### 2.1 Shell

**2.1.1 `ViewKeys.Shell.Frame` · `ShellFrameProps`** — R, NEW
- **Today**: `Shell.UI.cs:89` `Frame()`, `FrameRoot.Render` 223-510, `ContentRegion` 512-650; PR 205 page column `[PR205] Shell.UI.cs:642-671`.
- **State**:
  - `WindowFacts`: width, height, band (from `WindowBandPolicy`), active, zoom.
  - `ChromeMounted`: `FrameRules.ChromeMounted`, `Shell.cs:1906`.
  - `ImmersiveOpen`.
  - `NavPaneState`:
    - `Mode` Expanded/Compact/Overlay/None, from `NavPanePolicy`; today `SidebarPaneModeRules`, `Sidebar.Resize.cs:26-100`;
    - `PresentedWidth`, `DragPeek`, `UserCollapsed`, `Editing`.
  - `PanelState`: `Open`, `Mode`, `Width`, `Floats` (from `PanelPolicy`) and `DockedVideoHeight`. Today these live on `Shell.Ui` (`Shell.cs:1065-1189`).
  - `Auth`: `Shell.cs:1549-1578`.
  - `OfflineStrip`, `FileDragOver`.
  - `CoverDim`: `Setup.Covering`.
  - `Capabilities`.
- **Verbs**: `IFrameVerbs`
  - nav pane: `TogglePane`, `ResizePane(begin/update/commit/cancel)`, `OpenDrawer`, `CloseDrawer`;
  - panel: `ResizePanel(...)`, `ResizeDockedVideo(...)`;
  - `DropFiles`.
  - Shell keys and chords (`Shell.UI.cs:177-190, 794-818`, `FrameRules.Escape/F11`) stay core handlers, not view code.
- **Slots** (`HostSlot`): TitleBar, NavPane, Content, Panel, PlayerBar, Stage, PipLayer, FullscreenVideoLayer, Overlays, Palette, DragPreview, FpsOverlay, Material.
- **States**: chrome collapsed; pane Expanded/Compact/Overlay/None; panel docked/floating/closed; offline strip; cover dim; file drag.
- **Per theme**:
  - Classic: title row / [sidebar | card | rail] / bar.
  - WinUI: TabView + toolbar / NavigationView / content layer with docked pane / bar.
  - Zune: title bar / [band + card] / full-height inline-or-floating panel / bar.

**2.1.2 `ViewKeys.Shell.TitleBar` · `TitleBarProps`** — R, NEW (wraps engine `TitleBar`)
- **Today**: `ChromeRow` (`Shell.UI.cs:1038-1413`), the allocator `Chrome` (`Shell.Chrome.cs:201-513`), `TabWorkspace` (`Shell.Chrome.cs:515-889`); PR 205 wordmark and compact search (`[PR205] Shell.UI.cs:1686-2040`).
- **State**:
  - `ChromeLayout`: REUSE the `Chrome` record, computed with the theme's `ChromePolicy`.
  - `Tabs`: REUSE the `TabWorkspace` snapshot (label, glyph or art, pinned, closable, selected). Present only if `Capabilities.Tabs`.
  - `Nav`: CanBack, CanForward, history peek (≤8, `FrameRules.HistoryMenu`).
  - `AuthChip`: Profile/Connecting/Reconnect/SignIn (`Shell.UI.cs:1314-1325`).
  - `Unread`, `PinState` (`Shell.UI.cs:1329-1338`), `Crumbs` (PageChrome), `Presenting` (StoreShot).
- **Verbs**: `IChromeVerbs`
  - Back, Forward, HistoryMenu, Go;
  - tabs: NewTab, ActivateTab, CloseTab, CloseOthers, CloseToRight, CloseUnpinned, SetTabPinned, `TabDrop(payload, target)` (the drag payload and drop composition `Shell.UI.cs:1159-1268` → core);
  - TogglePin (it mutates the core pin store), OpenNotifications, ToggleFriends, SignIn, TogglePane.
- **Slots**: Search (2.1.3), ProfileChip (2.3.4).
- **States**: allocator stages; auth forms; tabs on/off; unread; presenting.
- **Engine**: WinUI needs a two-row title bar (tabs 40 + toolbar 48, captions on row 1) — §5 E11.

**2.1.3 `ViewKeys.Shell.Search` (+ `.SearchSuggestions`) · `OmnibarProps`** — C, REUSE model
- **Today**: model `Shell.cs:2108-2338`; store and fetch `Shell.Masthead.UI.cs:219-237, 477-516`; views 256-731; PR 205 pill and height reservation `[PR205] Shell.cs:2533-2568`.
- **State**: `Query` (`Prop<string>`), `QueryState` Idle/Pending/Results/Empty/Failed, rows (REUSE `Omnibar.Suggestions` + `OmnibarRowRules`, `Shell.Omnibar.Rows.cs:13-44`), highlight, ghost completion, `Form` Field/Icon (`ChromeLayout.SearchMode`), `Expanded`, `PendingBodyHeight`.
- **Verbs**: SetQuery, Submit, Choose (`Mh:506-516` → core), Play (`ContextPlayRules`), RowMenu, Retry, Focus, Escape.
- **States**: the 5 query states; field/icon; expanded; highlight.

**2.1.4 `ViewKeys.Shell.NavPane` · `NavPaneProps`** — R, NEW core model (the hardest one)
- **Today**: `Sidebar.UI.cs:46-2129`, `Sidebar.UI.Library.cs`, `.Footer`, `.Flyout`, `.Menus`, `.Edit`, `.Drop`; binder `Sidebar.Host.cs:1484-1845`; projection `Sidebar.cs:2715-3034`; PR 205 band `[PR205] Sidebar.UI.Zune.cs`.
- **State**: `NavModel` (NEW, layout-neutral; the binder's projection without any row planning):
  - `Destinations`: Home/Browse/Library kinds/Liked/Local/Recents, each with live counts (count logic `Sidebar.UI.Rows.cs:748-815` → core).
  - `Pins` (REUSE pin rows + `SidebarPinState`, `Sidebar.Layout.cs:752-772`), `Rootlist` tree (folders, playlists, expanded set), `LibraryItems` (filtered and sorted per `NavLayoutDoc`), `Feeds` (recents, releases + ages).
  - `Selected`, `Playing` (`Sidebar.UI.cs:1278-1283, 1337-1498` → core).
  - `Readiness`: Pending/Ready/Failed. The binder must start publishing Failed (the GAP at `Sidebar.Host.cs:1773`).
  - `Account`, `Editing`, `PersistenceHealth`.
  - `Layout`: REUSE `NavLayoutDoc` = `sidebar.json` (`Sidebar.Store.cs:372-468`) minus its presentation fields (§4 rows 7-8).
- **Verbs**: `INavVerbs`
  - Navigate, Play, ToggleSection, ToggleFolder, ActivateFolder, CreatePlaylist/Folder, Pin, Unpin, MovePin.
  - `Drop(payload, NavDropTarget)`. The decision is made by the pure rules `Sidebar.cs:1228-1832`. The theme turns a pointer position into a `NavDropTarget{Folder|Playlist|PinIndex|Section}` with its own geometry.
  - `Menu(target)`: `SidebarMenuModel` (`Sidebar.Edit.cs:194-395`) + `Run` (`Sidebar.UI.Menus.cs:151-245` → core).
  - Edit ops (`SidebarEditRules`), Undo, Redo, SetFilter, SetSort, Find.
- **States**: loading skeleton, empty, error banner/vacancy, offline, signed-out; row hover/pressed/selected/multi/focus/playing; edit mode; drag cues; width tiers.
- **Per theme**:
  - Classic: today's pane, with Classic/Library as an option. Its own `SidebarPlanner` moves from core (`Sidebar.Planner.cs`).
  - WinUI: NavigationView open 280 / compact 48.
  - Zune: pivot band, with pins ≤ `MaxBandPins` and row 2 from PageChrome. No tree.

**2.1.5 Content host** — core, no view
- **Today**: `ContentHost`, `Shell.UI.cs:885-1036`.
- **Stays core**: `Flow.KeepAlive(Current, SlotKey, PageBody, 3)`, scroll scopes, `Shown` landing, NotFound/Empty arms, offline lane.
- **From the theme**: only `AppTokens.Nav.RecipeFor(relation, reduced)`, which replaces `Design.Nav.RecipeFor(style, …)` (`Design.cs:1868`) and the page-motion style (§4 row 12). Each page root is `ViewSlot(Pages.X, props)` (§2.5).

**2.1.6 `ViewKeys.Shell.Panel` · `PanelProps`** — R, NEW (today "rail")
- **Today**: `Rail.UI.cs:42-136`, `BodyModeFor` (`Rail.cs:97`).
- **State**: `Mode` Lyrics/Queue/NowPlaying/Friends/Video (Friends filtered by `Capabilities.FriendsPanel`), `BodyMode`, `Open`, `Floats`, `DockedVideo` (height, pinned).
- **Verbs**: SetMode, Close (`Rail.UI.cs:314` → core), ResizeDockedVideo (`CommitCap`, `Rail.UI.cs:172-179` → core), ShowVideoAt (`Rail.UI.cs:217-222` → core), OpenStage.
- **Slots** (`HostSlot`): Lyrics (2.2.6), Queue (2.2.8), Friends (2.2.9), DockedVideo (2.2.7), NowPlayingHero (2.2.4).
- **States**: open/closed slide; docked/floating; nothing playing; stage-hosts-video fallback.

**2.1.7 `ViewKeys.Shell.NarrowDrawer`** — C
- **Today**: `Shell.UI.cs:1415-1563`.
- **Props**: `DrawerProps{Open, PinnedWhileEditing, Width}` plus the NavPane HostSlot.
- **Verbs**: Close.
- **Note**: a theme whose policy never yields Overlay (Zune) registers `Common.Nothing`.

**2.1.8 `ViewKeys.Shell.CommandPalette` · `PaletteProps`** — C, REUSE
- **Today**: `Shell.Palette.cs:38-255` (pure); card at `:375-539`.
- **Glyphs**: move from `CommandTable.Builtins` (`Pal:84-114`) to the theme's icon map, keyed by command id.
- **Verbs**: Filter, Move, Invoke (`Pal:282`). `ToggleTheme` (`Pal:267`) becomes `Appearance.ToggleMode`.
- **Theme commands**: "sidebar layout" and "player style" (`Pal:315-335`) become rows that the theme contributes through `ThemeOption.Command = true`. Core lists none.

**2.1.9 `PageChrome` channel** — core, NEW (no view)
- **Today**: `[PR205] Page.UI.cs:123-128`, `[PR205] Shell.cs:2344-2484`, `Shell.Mastheads` (`Shell.cs:2042-2100`), `ShellMaterial.Publish` (`Design.cs:2182-2213`; `[PR205] Design.cs:2299-2389`).
- **Record**:

  ```
  PageChrome(Route, Title, Crumbs[], Views{Labels,Selected,OnSelect,Trailing?}, Band{Title,Pivots,Active,OnPivot,Actions[],OnTitle},
             Material{Owner, Scheme|PaletteUrl, PayloadAccent}, Backdrop?{Url,PhotoHeight,HeroHeight,Floor,ScrollY,CollapseDistance,Decode})
  ```

- **Rule**: published by every page host at Shown, kept in an LRU by route key. Only frame-level views read it.

**2.1.10 `ViewKeys.Shell.Material` · `MaterialProps`** — R
- **Today**: `MaterialLayerView` (`Shell.Masthead.UI.cs:141-213`), the four cover leaves (`Design.cs:2270-2613`), PR 205 bleed (`[PR205] Shell.Masthead.UI.cs:147-400`, `Artist.Bleed.cs`).
- **State**: Material and Backdrop (from PageChrome), `Scheme` (from `Palette.Watch`, core data), `CardPose` (PR 205 `Shell.cs:1095-1135`, moved to core frame facts).
- **Per theme**:
  - Classic: tint, Home washes and tone plane, under its own wash options.
  - Zune: flat ground plus the artist bleed. No `ArtistBleed.Enabled`, no `Applies(zune)`.
  - WinUI: nothing (Mica).

**2.1.11 `ViewKeys.Shell.Masthead`** — C
- **Today**: `Shell.Masthead.UI.cs:41-132`, `TryMasthead` (`Shell.cs:944-957`), `Trail` (`Shell.cs:864`).
- **Per theme**: Classic draws today's band; WinUI a BreadcrumbBar inside its page head; Zune draws crumbs in the title bar and registers `Common.Nothing` here.

**2.1.12 Small frame contracts** — C
- `Shell.HistoryMenu` (≤8 + view all).
- `Shell.FileDrop` (`Shell.UI.cs:849-864`).
- `Shell.DragChip`: REUSE `Drag.Chip` (`Drag.cs:476-507`) and `Drag.InsertionCards` (`Drag.cs:554-613`); its art-hidden parameter is removed, and the theme style decides.
- `Shell.CoverScrim` (`Shell.UI.cs:870-883`).
- `Shell.Fps` (`Diagnostics.UI.cs:146-175`).

### 2.2 Player and now playing

**2.2.1 `ViewKeys.Player.Bar` · `PlayerBarProps`** — R, REUSE facts + NEW record
- **Today**: rules `Shell.PlayerBar.cs:34-621`; views `Shell.PlayerBar.UI.cs:165-2029`, `.Podcast.UI.cs`, `.Chapters.UI.cs`.
- **State**:
  - `Facts`: REUSE `PlayerBarFacts` (`Shell.PlayerBar.cs:90`).
  - `Face`:
    - title, artist links, album link, art url, morph key, context route;
    - `FaceArm` with the 450 ms hold (`PB:129`; the store `PBUI:711-757` → core);
    - `MediaKind` Music/Podcast/Video/Live, `Remote` device name, `Liked`/`Saved`, `TitleInk` role.
  - `Transport`: playing, shuffle, repeat (`NextRepeat`), owns-transport, speed.
  - `Seek`:
    - `SeekRailMode` Track/Dvr/Line;
    - `Prop<float> Fraction`, `Prop<string> Elapsed/Remaining/Total` (`TimeLabel`, `PB:591-621`);
    - live edge, behind-live, chapters (`PodcastChapterTimeline`), `TopEdgeSweep` (`PB:226`).
  - `Volume` (`Prop<float>`), `Muted`.
  - `Availability`: lyrics/transcript, video has/showing, queue, devices, expand, which panel mode is latched.
  - `Overflow`: `PlayerBarRules.Overflow(visible)` (core helper), where `visible` comes from the theme's `PlayerBarPolicy` for the current tier.
  - `ShowRemaining`: content pref.
- **Verbs**: `IPlayerVerbs`
  - TogglePlay, Previous, Next, SeekBy;
  - `KeyStep(dir, fine)`: the scrub ladder `PBPod:30-109` → core;
  - `Scrub.Begin/Update/Commit/Cancel`: the FSM and seek-generation hold `PBUI:1546-1825` → core;
  - SetVolume, ToggleMute, ToggleShuffle, CycleRepeat;
  - ToggleLike (`PBUI:876-886` → core), SetSpeed, GoLive, ToggleRemaining;
  - OpenPanel, OpenStage, ToggleVideo, SetVideoPlacement, OpenDevicePicker;
  - Navigate (`Shell.LinkFor`, `Shell.cs:1477`), DropPlayNext (`PBUI:897-933` → core), NowPlayingMenu, Retry.
- **Slots** (C): DevicePicker (2.2.2), ChapterTip, SpeedPanel, VolumePopup.
- **States**: the 5 player states; playing/paused/buffering; face hold; remote; music/podcast/video/live/DVR; tier (from policy); hidden under fullscreen.

**2.2.2 `ViewKeys.Player.DevicePicker` · `DevicePickerProps`** — C, REUSE
- **Today**: `DevicePickerRows` (`Shell.cs:1326-1446`), `Shell.DevicePicker.cs:37-80`.
- **State**: sections Local/Connect; rows with a kind-glyph *role*, active and needs-takeover flags; a quality echo row (the settings read at `PBUI:1430` → core fact); empty hint.
- **Verbs**: `RunDeviceIntent` (`PBUI:1473-1488` → core).
- **Note**: max height is policy.

**2.2.3 `ViewKeys.Player.OnMediaTransport` · `OnMediaProps`** — C
- **Today**: `OnMedia.UI.cs:42-301`.
- **State**: transport, seek binds, volume, chrome visibility (engine idle machine); menu data for placement, aspect, quality and speed (`Video.cs:883-926`, `OnMedia.UI.cs:74-158`).
- **Verbs**: transport, SetPlacement, SetAspect, SetQuality, SetSpeed.

**2.2.4 `ViewKeys.Player.NowPlayingHero` · `NowPlayingHeroProps`** — R
- **Today**: `Rail.UI.cs:182-768`.
- **State**:
  - face (as in 2.2.1);
  - `Deck` MediaSlot (preset + options are content prefs, §4 row 20); whether the deck is shown is a Classic option;
  - artist about, cities, merch facts;
  - `NextUp` ≤5 (the windowing at `Rail.UI.cs:701-719` → core);
  - `Scheme` (data).
- **Verbs**: Navigate, ToggleSave, ToggleArtVideo, Play(row).
- **States**: nothing playing; artist skeleton; video available.

**2.2.5 `ViewKeys.Stage.Root` · `StageProps`** — R, NEW `StageModel`
- **Today**: `Stage.UI.cs:62-1852` (`StageCtx` 87-142), `Stage.cs`, `Stage.Layouts.cs`, `Stage.Options.UI.cs`, `Stage.Hero.UI.cs`, `Stage.Artist.UI.cs`.
- **State** (`StageModel`):
  - `Mode` Lyrics/Visualizer/Queue/Artist.
  - `Face` (content pref) and `FaceSlot`: a MediaSlot over the `Visualizer.Face` and Verse renderers (`Visualizer.UI.cs:200-617`). The Clock lease and palette-fade orchestration also move to core (§3.1).
  - `Caption` clock (`Stage.cs:432-539`); `CaptionWipeRows` (`Stage.UI.cs:1208-1341`) moves to a core helper.
  - `HasTimedLyrics`, `ChromeIdle` (counted `MenuOpen`), `GalleryOpen`, `TipSeen`.
  - `Transport`, `Palette`/`Accent` (data), `Aspect` class (`StagePolicy`).
  - `Artist` facts (`ArtistRules`, `Stage.Artist.UI.cs:49-159` → core), `Queue` (2.2.8).
  - `LayoutState`: theme state. For Classic that is Card/LargeArt/Centered/Artist, spectrum and hero options.
- **Verbs**: SetMode, StepFace, SetFace, ToggleGallery (one rule; today duplicated at `Stage.UI.cs:205-220` and `Stage.Options.UI.cs:268-274`), Exit, OpenOptions, Activity, transport verbs, Navigate (which closes the stage first).
- **Sub-keys** (C): `Stage.TopBar`, `Stage.Transport`, `Stage.Options`, `Stage.Gallery`, `Stage.Caption`, `Stage.ArtistPane`, `Stage.Hero`, `Stage.QueuePane`.
  - Zune draws transport as a flush 112 bar with words. WinUI uses a floating card with chips. Classic keeps today's.
  - Zune's full-screen "up next" page is its `Stage.QueuePane` registration.

**2.2.6 `ViewKeys.Lyrics.View` · `LyricsProps` + `StyleKeys.LyricsLines`** — R + S, NEW `LyricsModel`
- **Today**: `Lyrics.UI.cs:75-2693` (`ViewCore`, `LineRow` 2106-2333), `Lyrics.cs` (Doc 61-127, Fx 140-166, Surface 1456-1521, Interlude 1599-1660), `AiLyrics.UI.Lyrics.cs`.
- **State**:
  - `State` Nothing/Loading/NoLyrics/Failed/Unsynced/Synced.
  - `Doc`: lines; `SyncKind` None/Unsynced/Line/Syllable; Generated/Override; provider credit.
  - `ActiveLine` (`Prop<int>`) and word-progress binds (wipe driven by core).
  - `SecondLine` Off/Translation/Romanization + availability (a session pref).
  - Podcast transcript, `VideoSuppressed`, `ResyncNeeded`, `Interlude`, `AiState`, `Surface` Panel/Stage/Peek.
- **Verbs**: SeekToLine, ToggleSecondLine, Resync, ToggleAiPreferred, OpenAiSettings, ExpandToStage.
- **Style**: `LyricsLinesStyle{Extent, Line (bound), Metrics, Interlude}`.
- **Why these fields**: the canvases need granularity, second-line kind, timing source, provider and the resync verb. WinUI draws a word wipe with romanization; Zune draws line-level with translation and resync.

**2.2.7 Video** — R per surface, REUSE `Video` model
- **Keys**: `ViewKeys.Video.Docked`, `.Pip`, `.PopOut`, `.Fullscreen`, `.JoinPoster`, `.PlacementMenu`.
- **Today**: `Video.cs` (placement FSM 51-156, `Joining.Decide` 1545-1560), `Video.UI.cs:98-1483`, `Video.Host*.cs`.
- **State**: REUSE `PlacementState` (`Video.cs:139-156`), `TransportOwner` (85-103), aspect, on-top, quality, the join verdict, PiP rect. PiP size bounds (`Video.cs:1374-1384`) become policy.
- **Slots**: `MediaSlot Presenter`, the persistent video hole (§5 E7).
- **Verbs**: SetPlacement, ToggleFullscreen, SetAspect, SetQuality, ToggleOnTop, MovePip/ResizePip (`PipGesture` → core), SwitchToAudio, TurnOffVideo.

**2.2.8 `ViewKeys.Queue.Panel` / `ViewKeys.Queue.StagePane` · `QueueProps`** — R ×2, NEW `QueueModel`
- **Today**: `Queue.UI.cs:409` `RailPanel` and `:957` `StagePane`, plus the second skin at `:613-925`; `Queue.Rules.cs`, `Queue.cs`.
- **State**:
  - `PlayingFrom`, `NowPlaying`;
  - `NextInQueue` (clearable) and `NextUp` (source name, "N more", numbering continues across both);
  - `Autoplay` + a preview of the next autoplay track;
  - `Shuffle`, `Repeat`;
  - rows: REUSE `RowText` (`Queue.UI.cs:102`), `Track.RowData`, `Episode.RowData`;
  - `Empty`.
- **Verbs**: Play, Remove, Clear, Move (`QueueMovePlan`), ToggleAutoplay, ShowMore, RowMenu. ToggleAutoplay is today a raw key write in views, at `Stage.UI.cs:1840` and `Queue.UI.cs:326, 948`.
- **Note**: Classic's RowStyle option selects between today's two skins inside Classic's view.

**2.2.9 `ViewKeys.Panel.Friends` · `FriendsProps`** — C, REUSE
- **Today**: `Rail.UI.cs:834-1008`; feed rules `Rail.cs:368-426`.
- **State**: Rows/Skeleton/Empty/Offline/Error.
- **Verbs**: Refresh, Navigate, Play.

**2.2.10 `ViewKeys.Player.DeckStyleFlyout`** — C, REUSE catalog
- **Today**: `Rail.Styles.UI.cs:48-285` over the catalog `Rail.cs:120-186`.
- **Note**: only Classic draws a picker. The preference writes (`RSt:101-194`) move to core content-pref verbs.

### 2.3 Overlays, menus, dialogs, toasts

**2.3.1 `ViewKeys.Menu.Context` · `MenuProps`** — R, REUSE model
- **Today**: `Actions.Menu`/`ContextMenuModel`, the track grammar (`Track.Menu.cs:11-24`), `Menus.Container` (`Menus.Container.cs:126-160`), `SidebarMenuModel`.
- **State**: header (title, subtitle, art); `PrimaryVerbs[]` (Zune draws them as a strip of round verbs, others as rows); row groups (label, glyph role, check/radio, enabled, submenu, destructive).
- **Verbs**: Invoke.
- **Engine**: the popup chrome and placement stay in the engine `MenuFlyout`; the presenter is theme-supplied (§5 E14).

**2.3.2 `ViewKeys.Overlay.Toast` · `ToastProps` + `ToastPolicy`** — C
- **Today**: `Notify.Say` (`Controls.cs:1390`), `Shell.Overlays.UI.cs:204-375`.
- **State**: text, action word, progress, severity, lifetime.
- **Verbs**: Act, Dismiss.
- **Policy**: placement and max count (Zune ≤3, bottom right above the bar).

**2.3.3 `ViewKeys.Overlay.Notifications` · `NotificationPanelProps`** — R, REUSE
- **Today**: `Shell.UI.cs:1565-2064`; `Notify.cs` (update plan 777-818).
- **State**: filter, items by `NotifyCategory`, pending-sync row, empty per filter.
- **Verbs**: MarkAllRead, Clear, SetFilter, RunUpdateVerb, Open, Undo. The verb bodies at `Shell.UI.cs:1832-1985` move to core.

**2.3.4 `ViewKeys.Overlay.ProfileChip` / `.ProfileMenu` · `IdentityProps`** — C
- **Today**: `Shell.Overlays.UI.cs:398-616`.
- **State**: identity or skeleton, tier, `Presenting`, `AppMode`.
- **Verbs**: Profile, Settings, PlayLink, PlayFile, Notifications, Friends, `ToggleAppMode`, LogOut (with confirm; `Spotify.Logout` at `Ov:635` stays core).

**2.3.5 Dialogs** — R each, engine `ContentDialog` + the theme's dialog control style
- `Dialog.Confirm`, `.Prompt`, `.Sheet` (`Controls.cs:1309-1359`).
- `Dialog.LogoutConfirm` (`Ov:616`).
- `Dialog.PlayLink` (`Ov:662-788`; busy / no owner / failed; the async router `Ov:678-706` → core).
- `Dialog.Signature` (`Ov:256`), `Dialog.SidebarReset` (`Sidebar.UI.Menus.cs:90-98`), `Dialog.FolderPicker`, `Dialog.Rename` (`:635-727`).
- Each is a props record + verbs. Width, radius and button order come from the theme.

**2.3.6 `ViewKeys.Overlay.RuntimeBanner`, `.TeachingTip`, `.OfflineStrip`** — C
- **Today**: `Ov:141-199`, `Ov:799-855`, `Controls.cs:1272`.
- **Note**: the gating and logging rules (`Ov:147-176, 204-249`) and the tip slot `s_tipHandle` (`Ov:792`) move to core.

### 2.4 Screens

**2.4.1 `ViewKeys.Pages.Settings` · `SettingsProps` + `SettingsDoc`** — R, NEW core catalogue
- **Today**: `Settings.UI*.cs`; catalog `Settings.cs:33-244`; writers inline in views (§3.1).
- **State**: `SettingsDoc` (NEW): tabs → sections → rows. Each row has:
  - `Kind`: Toggle/Combo/Slider/Button/StatusPill/Expander/Link/InfoBar/Custom;
  - `Value` (`Prop`) and `Writer` (verb);
  - `Enabled` + reason, `Visibility` (developer);
  - glyph *role* and caption.
- **Custom rows**: `Settings.ThemePicker` (cards + previews), `Settings.ThemeOptions` (generated from the active schema), `Settings.Equalizer`, `Settings.GpuPicker`, `Settings.VideoOverrides`, `Settings.StorageUsage`, `Settings.Receipts`, `Settings.AiLyricsCard`, `Settings.RuntimeCard`, `Settings.SavedReports`.
- **Verbs**: SetTab, `Invoke(row, value)`.
- **States**: loading census, failed, disabled-with-reason, developer-only, editing-locked.
- **Appearance tab** becomes Theme (picker + the active theme's options) · App mode · Zoom (canvas W-Settings). The remaining rows become content prefs (§4).

**2.4.2 Setup** — R per step; REUSE `Setup.cs`, NEW `WizardModel`
- **Keys**: `Setup.Wizard` (plate + footer), `Setup.Terms`, `Setup.SignIn`, `Setup.Theme` (today's Layout page; the pick is a live theme switch under the plate), `Setup.LocalPlayback`, `Setup.SignInDoor`, `Setup.RuntimeCard`.
- **Today**:
  - pure rules: `Setup.cs:224-811`;
  - `WizardSession` (`Setup.UI.cs:511-658`) → core `WizardModel`;
  - `RuntimeModel` (`Setup.UI.Runtime.cs:137-497`) → core.
- **State**: page, direction, primary/secondary/back, step index/count, sign-in facet (Idle/Busy/Done/Failed/Expired/Premium), pairing code and expiry (`Prop`), runtime phase.
- **Policy**: `Setup.Layout` geometry (`Setup.cs:41-104`) moves to the theme.

**2.4.3 Release notes** — R; REUSE `ReleaseNotes.Model.cs`
- **Keys**: `Pages.WhatsNew`, `ReleaseNotes.Viewer`, `ReleaseNotes.AfterUpdatePlate`.
- **State**:
  - since range, releases + tags, highlights;
  - Added/Changed/Fixed rows, each with issue state and contributor;
  - selection; loading/empty/offline.
- **Verbs**: SelectRelease, OnlyLatest, OpenOnGitHub, CopyLink, OpenHighlight.
- **Moves**:
  - `HighlightCardMetrics`/`HighlightViewerLayout` (`ReleaseNotes.cs:1456-1632`) → theme;
  - the last-seen write (`ReleaseNotes.UI.cs:137`) and the after-update gate (`:1201-1259`) → core.

**2.4.4 `ViewKeys.Dialog.Feedback` · `FeedbackProps`** — R; REUSE `Feedback.cs` form
- **State**: kind, fields, include logs, redacted preview, crash variant, composing/ready.
- **Verbs**: SetKind, Edit, OpenGitHub, Copy, SaveAs, Close.
- **Moves**: `ReportComposer` I/O (`Feedback.UI.cs:440-537`) and `OpenGithub`/`SaveReport` (`:370-416`) → core.
- **Diagnostics**: gain `theme id · option hash · policy facts` (the Zune canvas preview shows "theme zune · nav zune · panel inline").

**2.4.5 `ViewKeys.Dialog.Crash` · `CrashPromptProps`** — R
- **Today**: `Crash.UI.cs:128-375`.
- **State**: from-list/this-launch, hang/crash, bullets, snapshot size, always-send, not-configured.
- **Verbs**: Send, NotNow, ReadFull, Privacy, ReportOnGitHub.
- **Moves**: consent policy and enqueue (`Crash.UI.cs:62-91`) and the submit settings writes (`:260-264`) → core.

**2.4.6 Recovery** — outside the theme system
- It is the engine-free Win32 TaskDialog (`Recovery.Win32.cs`), shown before `Platform.Boot`.
- The Zune canvas draws a themed recovery dialog. That would need the engine and the theme before the crash-safe boot path exists (§7 Q9).

**2.4.7 `Pages.Logs`, `Pages.Diagnostics.{Playback,Connect,Capture}`, `Pages.History`, `Pages.NotFound`** — R
- **Today**:
  - `LogsPage.UI.cs` (rules `LogsPage.cs:21-61`; disk walk `:351-473` → core);
  - `Diagnostics.UI.cs:181-775`, `Diagnostics.Capture.UI.cs`;
  - `Shell.History.UI.cs:35-351` (rules `Shell.cs:1683-1843`; the direct DeveloperMode read at `Hist:65` → a props fact; `Store.Clear/Remove` at `Hist:133, 324` → verbs);
  - `Shell.UI.cs:1002-1019`.
- **Registration**: built-ins register `Common` blocks for Diagnostics and Logs. History and NotFound go to `Common` unless a canvas draws them.

### 2.5 Pages

Every route root is `ViewSlot(Pages.X, props)`, built by a core page host.
- **Host** (stays core): today's demand (`Entities.Ensure/EnsureEdge`), readiness gates, value stamps (`PageStamp`/`IdentityInputs`), and the PageChrome publication.
- **View** (the theme's): composes sub-contracts through `ctx.View` and HostSlots. Classic re-registers today's trees as its views.

**2.5.1 Home** — `Pages.Home` · `HomeProps` — R; REUSE `ScreenModel`
- **Today**: `Home/Model.cs:60` (`ScreenModel`), `Zone` (`:42`, 16 `ZoneKind`s at `:25`), `HomeCard` (`Entities/Home.cs:1395`), the facet machine (`Home/Facets.cs:25-302`), the planners (`ZonePlanner.cs:275`, `PodcastPlanner.cs:49`).
- **State**: screen; facets + `FacetState` (Loading/Refreshing/Failed); `Following`; `Daylist` (`DaylistForm` + clock pref); wash pick (`Home/Reveal.cs:53`, data); `Quickplay` (NEW projection of pins, history and new releases, for Zune's quickplay row).
- **Verbs**: SetFacet, ToggleFollowing, Retry, OpenSection, Play(card), CardMenu, Customize.
- **Sub-keys**:
  - `Home.FacetRow`, `Home.Daylist`, `Home.Quickplay`, and one `Home.Zone.<Kind>` per ZoneKind (Classic re-registers `Zones.UI.cs:57-70`);
  - `Pages.HomeSection` (`SectionScreen.UI.cs:37`);
  - `Pages.HomeCustomize` (`Customize.UI.cs:35`).
- **States**: derived shimmer, facet dim, refreshing, failed, empty facet, card now-playing relation.

**2.5.2 Browse** — `Pages.Browse` / `.BrowseCategory` / `.BrowseSection` — R; REUSE
- **Today**: `Browse.Page.cs:92, 211, 295`; `BrowsePageLayout.Mode` (`Browse.Rules.cs:275`); `BrowseTileModel` (`:408`); `BrowseLoadGate` (`:73`).
- **State**: taxonomy, tiles (category colour is data), charts, readiness, page mode.
- **Note**: the five tile densities (`Browse.UI.cs:48`) are Classic's views. Zune puts categories in row 2 through PageChrome views.

**2.5.3 Search** — `Pages.Search` · `SearchProps` — R; REUSE
- **Today**: `Search.Page.cs:81`; `HitItem/HitContext` (`Search.UI.cs:64-68`); `SearchHitRules` (`:804`); `Search.Rules.cs:274-521`.
- **State**: query, readiness (`Search.Page.cs:79`), facets + counts, top result, sections, per-facet empty sentence.
- **Verbs**: SetFacet, Play, Navigate, Follow/Save, Retry.
- **Moves**: `HitContext.HideTrackArt` → theme style (§4 row 10); the shimmer shape → theme.

**2.5.4 Detail family** — `Pages.Album` / `.Playlist` / `.Liked` / `.Show` / `.Episode` · `DetailPageProps` — R; REUSE `FrameSpec`, split
- **Today**: `FrameSpec` (`Detail.UI.cs:332`), `Identity` (`:62`), `FrameActions` (`:225`), `FrameSlots` (`:259`), `Detail.Config` (`Detail.cs:1405`), `FrameHost` (`Detail.UI.cs:493`), `Detail.UI.Hero.cs`.
- **The split**:
  - `Identity` stays.
  - `Config` splits into:
    - core `DetailFacts`: Kind, Selection, Content, Editable, HasTrailing, Recommendations, ShowVersions, PlaysOptIn, Heart;
    - theme layout: TwoColumn, RailWidth, Badges, ShowArtThumb, ShowAlbumColumn, and PR 205 SlimHead/HoverHeart/PlainRows (`[PR205] Detail.cs:1670-1699`).
  - The `FrameSlots` page-built Elements become typed sub-contracts:
    - Album: `Detail.Attribution` (face pile, `Album.UI.cs:51`), `Detail.Countdown` (`:290`), `Detail.ReleasePanel` (`:463`);
    - `Detail.Trailing` sections (`Album.Page.cs:625-1254`);
    - Playlist: `Playlist.CoverEditor` (`Playlist.UI.cs:185`), `Playlist.InlineEditor` (`:333`), `Playlist.Owner` (`:523`), `Playlist.Access` (`:787`), `Playlist.Daylist` (`:959`);
    - Liked: `Liked.Cover` (MediaSlot), `Liked.Facts` (`User.Facts.UI.cs:212`);
    - podcast slots (`Show.Page.cs:1126-1363`) and the Episode slots (`Episode.Page.cs:96-442`).
- **State**:
  - identity, facts, readiness, notice (`NoticeRules`, `Detail.cs:901`);
  - actions (PlaySplit verbs, Shuffle, Save/Follow, Share, More, Invite, Tune);
  - `Table` (`HostSlot<TableArgs>`).
  - `RailState`: Classic theme state; today written from the view at `Detail.UI.cs:419-489`.
  - The breakpoint ladder (`Detail.cs:73-144`) becomes a policy of themes that draw a rail.
- **States**: header pending, meta loading, notice strip, partial, held reveal (playlist /diff), optimistic edits, collapsed rail, vertical arm, sticky band.

**2.5.5 Library panes** — `Library.AlbumPane`, `Library.ShowPane`, `Library.ArtistReader`, `Library.SavedEpisodes` — C; REUSE
- **Today**:
  - `Album.Pane.cs:60` (`AlbumPaneState`, `User.cs:1242`);
  - `Show.Pane.cs:62`;
  - `Artist.Reader.cs` + `Artist.Reader.Shape.cs` (geometry constants `:63-205` → style; analytic block extents stay);
  - `User.SavedEpisodes.cs:19`.
- **Moves**: `RowDensity` read at `Album.Pane.cs:192` → table style.

**2.5.6 Artist** — `Pages.Artist` · `ArtistPageProps` — R; REUSE values
- **Today**: `Artist.Page.cs:82`; `ArtistHeroMetrics/HeroText/ArtistPageFacts/ArtistSections` (`Artist.UI.cs:51-224`); `ArtistReadiness` (`Artist.Rules.cs:41`), `ArtistPopularTracks` (`:374`), `ArtistTour` (`:467`), `ArtistCatalog` (`:521`).
- **State**: facts, sections + per-section readiness, following, spy anchors.
- **Verbs**: Play, Shuffle, Follow, Navigate, OpenGallery, SetPivot.
- **Sub-keys**:
  - `Artist.Hero`: tiers become theme policy;
  - `Artist.Chart` (S): cache keys go from `|art|noart × |classic|modern` (`Artist.UI.Chart.cs:20-21, 79-80, 412`) to a stamp;
  - `Artist.Lightbox`.
- **Per theme**:
  - Zune's Artist view publishes `PageChrome.Backdrop` (the bleed);
  - the 59fd96c1 entrance clock is deleted;
  - Classic keeps the production hero choreography unchanged.

**2.5.7 Discography** — `Pages.Discography` — R; REUSE `Artist.Discography.cs`
- **Today**: `DiscoFacet` (`:55`), eras (`:57-270`), `DiscoCellStamp/Rules` (`:392-423`), `DiscoGridHost` (`:823`), `DrawerPanel` (`:1174`).

**2.5.8 Library** — `Pages.Library` · `LibraryProps` — R; NEW record over REUSE pieces
- **Today**: `User.Page.Library.cs:196`; `NavShape` (`:151`); `LibraryNavMount` (`User.NavMount.cs:60`); `LibraryNavOrder/Letters/Hits` (`User.cs:912-1324`); collapse breakpoint (`User.cs:675`).
- **State**: kind, `ItemSource<LibraryNavItem>`, sort/desc/scope, `ViewPrefs{List|Grid, S|M|L}` (content pref), letters readiness, search hits, selection, `RightPane` (HostSlot), `PodcastsMode`.
- **Verbs**: SetSort, SetView, SetSize, Filter, Select, Open, Play, JumpTo.
- **Moves**: column widths `LibraryLeftW/MidW` → theme state; the collapse breakpoint → policy.
- **Per theme**: WinUI list + detail (380/1fr); Zune three panes (220 · grid · 380); Classic today's.

**2.5.9 Recents** — `Pages.Recents` — R; REUSE
- **Today**: `Recents.Page.cs:64, 112`; `RecentsView` (`Recents.cs:325`), `RecentsSnapshot` (`:239`), `RecentsLayout` (`:713`).
- **State**: kinds, day groups (label + count), rows (kind word, art, caption, time, playing, children), calendar heatmap, zoom, viewport accent (data).
- **Verbs**: SetKind, Expand, Play, Navigate, JumpToToday, Zoom.

**2.5.10 Profile** — `Pages.User` / `.ProfileList` — R; REUSE
- **Today**: `Profile.Page.cs:49` (`ProfileLoadRule`), `ProfileSections` (`Profile.Rules.cs:48`), `ProfileTone` (`:215`); lists `Profile.Lists.Page.cs:67` (`ProfileListLoadRule`, `ProfileListModel`, `ProfileLetterRows`).
- **States**: Loading/Ready/Unavailable(404 seal)/Failed; lists Empty/Hidden.

**2.5.11 Concerts** — `Pages.Concerts` / `.ArtistConcerts` / `.Concert` — R; REUSE
- **Today**: `Concert.Page.cs:250, 824, 1445`; `DetailView` (`:1381`); `TileData` (`Concert.UI.cs:73`); `ConcertLayout` (`Concert.Rules.cs:244`).
- **Moves**: the `DarkOr` fork (`Concert.UI.cs:134, 251`) disappears into theme tokens. The provider accent stays data.

**2.5.12 Module page** — `Pages.Module` · `ModulePageDoc` (Sdk) — R; REUSE
- **Today**: `Wavee.Sdk/ModulePage.cs`, drawn by `Platform/Modules.UI.cs:255-468`.
- **State**: the doc + readiness; the watch template's video is a `MediaSlot`.
- **Rule**: unknown section kinds are skipped, as now.

### 2.6 Shared components

**2.6.1 Track table** — `StyleKeys.TrackTable` (§1.10c) + core `TableHost` — S; REUSE
- **Core keeps**:
  - `TableSource` (`Track.Table.cs:54`), `TableProfile` (`:298`), `TableArgs` (`:349`);
  - `TableHost` (`:402`): selection, drag, playback, reorder, filter, sort keys, choreography;
  - `Snapshot` (`:559`): `Classic`, `ArtHidden` and `Marquee` are removed and `StyleStamp` is added;
  - `RowPresentation` (`:2185`): `Marquee` moves into the style;
  - `FilterModel` (`Track.Rules.cs:904`); the tier/relief ladder (`Detail.cs:79`, `Track.Rules.cs:274-359`).
- **The style supplies**:
  - the `ColumnSet` builder (the `Classic` field leaves `Track.Rules.cs:113`);
  - extents (`TableRules.RowHeightFor/ArtSizeFor`, `Track.Rules.cs:166-215`);
  - the row template (the `Skin` fork `Track.Table.cs:2061-2151`, cells in `Track.UI.Bound.cs`);
  - header (`Track.Table.Chrome.cs:576`), command/list bar (`:142`), filter card (`:915-1270`), selection bar (`:1271`), placeholder.
- **States**:
  - row: rest/hover/pressed/selected/multi/playing/paused/buffering (`RowCellRules.RestOf`, `Track.UI.Bound.cs:43`)/unavailable/not-yet-out/explicit/local/video/podcast/drop/insertion;
  - list: loading (12-hot-row gate)/failed/empty/no-match/held;
  - tiers 0-6; sticky header.
- **Cache keys**: `TracksFor` (`Track.UI.cs:115-150`) and the list key (`Track.Table.cs:1065-1066`) use `(StyleKey, Stamp)`.

**2.6.2 Eager row / episode row** — `StyleKeys.EagerTrackRow`, `StyleKeys.EpisodeRow` — S; REUSE
- **Today**: `Track.UI.cs:1007`; `Episode.UI.cs:69-655` (`ReaderRow`, `RowItem`, `RowPlan`).

**2.6.3 Media surface** — `StyleKeys.Surface` + core `Controls.Surface` host — S; REUSE
- **Today**: `CardData` (`Controls.Art.cs:324`), `SurfaceShape` (`Surface.Rules.cs:56`), `SurfaceHost` (`Surface.Host.cs:42`).
- **The style supplies**: the plate table (`Surface.Parts.cs:44-58`), parts (`:74-493`), FAB, menu placement. The shape presets (`Surface.Rules.cs:77-126`) become theme metrics: page views ask for a `ShapeRole` and the theme maps it to a `SurfaceShape`.
- **Core keeps**: hot/focus, drag, menu, play seam, `RelatesNow`.

**2.6.4 Page head** — `ViewKeys.Page.Head` · `PageHeadSpec` — C; REUSE (PR 205 `[PR205] Page.UI.cs:40`)
- **Moves**: `PageGeometry`/`PageHeadKind` extents (`[PR205] Page.Rules.cs:22-221`) → theme tokens.
- **Deleted**: `Hoisted` and `ViewsInBand` (§1.10d).

**2.6.5 Detail hero / rail / band** — C (Classic's internal composition); REUSE
- **Today**: `Detail.UI.cs:1182-1994`, `Detail.UI.Hero.cs:50-821`; rules `VerticalLayout` (`Detail.cs:144`), `BandLayout` (`:950`), `Skeleton` (`:1168`).
- **Note**: these are Classic's own building blocks. Other themes may reuse the rules through `Common`.

**2.6.6 Choice controls** — `ViewKeys.Control.Choice` · `ChoiceProps{Items, Selected, Counts?, OnSelect, Scroll}` — C
- **Today**: five vocabularies:
  - `Controls.Chip`/`ChipRail` (`Controls.Art.cs:567-601`);
  - `Words.Rail` (`Controls.Words.cs:171`);
  - `SegmentedPillStyle` (`Concert.UI.cs:38`);
  - `PivotTabs` (`Recents.Page.cs:848`);
  - the facet `SelectorBar` (`Design.FacetTitleStyle`, `Design.cs:1436`).
- **Change**: they collapse into one contract with a `ChoiceRole` (Filter, Views, Sort, Scope) and each theme draws its own.

**2.6.7 Vacancy** — `ViewKeys.State.Vacancy` · `VacancyProps{Voice, Scale, Action?}` — C; REUSE
- **Today**: `Controls.cs:1212-1268`.
- **Skeletons**: stay derived (`Skel.Region`).

**2.6.8 CTAs** — `ViewKeys.Control.PlaySplit`, `.Secondary`, `.IconAction`, `.Follow`, `.Save`, `.More` — C
- **Today**: `Controls.Cta.cs` (PR 205 `PlaySplitButton` `:612`), `Controls.cs:856-1038`.
- **State**: the `PlayState` relation (`Playback.CardSeam.cs:17-34`, `Controls.Cta.cs:474` → core fact), saved/following/pending.
- **Moves**: `ButtonRules.FollowStyle` math → theme.
- **Deleted**: the legacy pill set (`Cta:196-284`).

**2.6.9 Small content components** — C; REUSE records
- Face pile (`Controls.Art.cs:777-857`), stat tile (`:619`), countdown / flip digits (`:660-720`), rich text (`:868-1092`), section header (`Controls.cs:433-479`), jump strip / sticky letter (`User.UI.cs:275`), selection bar (`Controls.cs:1137-1191`), explicit badge, equalizer (`Controls.cs:660-839`).
- Each becomes a `ViewKeys.Control.*` over its existing record; Classic registers today's builder.

**2.6.10 Content renderers** — `MediaSlot`, not ViewKeys
- **Today**: Liked cover treatments (`User.Cover.cs:233-899`), visualizer faces (`Visualizer.*.UI.cs`, 19), deck faces (`Deck.Faces.cs`, 12), Verse (`Verse.UI.cs`).
- **Rule**: they are user-picked content, so they stay core and theme-independent. They paint with the theme's stage-arm ink tokens (`StageArm`, `Design.cs:498-619` → theme tokens).
- **Theme role**: the theme places and frames them, and may only *suggest* a default when the content pref is unset (Zune → Mosaic).

---

## 3. Logic extraction list

Rule: after the refactor, everything under `Themes/` is a pure function of props + roles + its own options, and
nothing under core builds a look. Each row names the code and where it goes. "Core svc" means a core service or host
(engine-free where possible, unit-tested like today's CORE rules).

### 3.1 Views that own logic → core

| # | Today (path:line) | What it is | Goes to |
|---|---|---|---|
| X1 | `Shell.PlayerBar.UI.cs:56-81` | app-wide verbs `TogglePlayPause/ToggleShuffle/CycleRepeat/TransportFacts/RetryPlayback` | `PlayerVerbs` (core svc), also used by stage/OnMedia |
| X2 | `Shell.PlayerBar.UI.cs:1546-1825` | scrub FSM, pixel dwell, seek-generation hold | `ScrubController` (core). The theme only maps pointer → fraction |
| X3 | `Shell.PlayerBar.Podcast.UI.cs:21-109` | keyboard scrub ladder (static mutable state) + `BarSeekBy` | `ScrubController.KeyStep` |
| X4 | `Shell.PlayerBar.UI.cs:711-757, 283-296` | `BarFaceHold` store written from layout effects | `NowPlayingFace` (core, `PlayerBarRules.FaceOf`) |
| X5 | `Shell.PlayerBar.UI.cs:270-274, 876-886` | podcast saved-read effect; library mutation `BarToggleLike` | `PlayerVerbs.ToggleLike` / an entity demand in the host |
| X6 | `Shell.PlayerBar.UI.cs:897-933` | async drag-drop resolve, queue insert, toasts | `QueueVerbs.DropPlayNext` |
| X7 | `Shell.PlayerBar.UI.cs:1377, 1429-1431, 1473-1488` | picker-opened side effect, quality read, device intent execution | `DeviceVerbs.Run` + the `DevicePickerProps.Quality` fact |
| X8 | `Shell.PlayerBar.Chapters.UI.cs:31-43` | chapter lease and normalisation | chapter fact in `PlayerBarProps.Seek` |
| X9 | `Rail.UI.cs:172-179, 217-222, 314, 493-497, 701-719, 838` | docked-height persist, `Video.State` mutation, rail close, `Entities.Ensure`, queue windowing, `Friends.SetActive` | `PanelHost` (core) + `ThemeState` for the height |
| X10 | `Rail.Styles.UI.cs:101, 134-159, 187-194` | per-render `Signal` mirror; preference writes | `DeckPrefs` verbs (content prefs) |
| X11 | `Stage.UI.cs:205-220` ≡ `Stage.Options.UI.cs:268-274`; `Stage.UI.cs:509-525, 1121-1150, 1208-1341, 1840` | ToggleGallery (duplicated), key routing, caption tick/edge logic, `CaptionWipeRows`, the autoplay raw-key write | `StageModel` / `CaptionClock` / `QueueVerbs.ToggleAutoplay` |
| X12 | `Stage.UI.cs:318-342, 545-564`; `Video.UI.cs:1388-1434` | OS fullscreen with restore, focus scope, idle machine, all done in mount effects | `ImmersiveService` (core, state-keyed, survives a root swap, §1.7 step 6) |
| X13 | `Visualizer.UI.cs:292-562` | Clock: lease wiring, palette fade, moments, scope pull, batch write | `VisualizerClock` (core). Faces stay content renderers |
| X14 | `Visualizer.Gallery.UI.cs:181-228, 400-404`; `Stage.Artist.UI.cs:49-159` | `GalleryNav`, `SubtitleOf`, `ArtistRules`: pure code in UI files | move to core rule files |
| X15 | `Lyrics.UI.cs:125-155, 2327-2332`, ViewCore effects `167-2011` | Ink seam colour mapping (→ style roles); sync/follow/interlude/resync state in the view | `LyricsModel` (core); inks → `Roles.Lyrics*` |
| X16 | `Video.UI.cs:265-281, 681-707, 997-1025` | aspect mapping, docked fit effect, PiP geometry and overlay placement switch | `Video` core rules (+ PiP bounds policy) |
| X17 | `Deck.UI.cs:444-483, 586-612` | `SpectrumFold` (logic in UI); wash colour mixing by theme | core fold; wash → style roles |
| X18 | `Shell.UI.cs:217-218, 295-319` ≡ `Settings.UI.Appearance.cs:247-299` | auto-zoom control loop + zoom persistence, done twice | `ZoomService` (core) |
| X19 | `Shell.UI.cs:770-776` | `CommitRailDrag` persists `ShellRailWidth` | `PanelHost` → `ThemeState` (panel width is per theme) |
| X20 | `Shell.UI.cs:699-708`, `Shell.Host.cs:674-690`, `Shell.Palette.cs:267-273`, `Settings.UI.Appearance.cs:195-213` | four copies of mode→kind + palette seed + accent + transition | ONE `Appearance` service (core): `Seed`, `SetMode`, `ToggleMode`, `OnSystemColorsChanged`, `SwitchTheme` |
| X21 | `Shell.UI.cs:1159-1268, 1832-1985` | tab drag payloads/drop targets, pin store mutation from chrome, notification verbs | `WorkspaceVerbs`, `PinVerbs`, `NotificationVerbs` |
| X22 | `Shell.Overlays.UI.cs:93-110, 147-176, 204-249, 330-331, 381, 635, 678-706, 792` | composition-root wiring, banner/toast gating, static update-toast state, `RunUpdateAction`, logout, the PlayLink router, the tip slot | `App.cs` composition root + `OverlayGates`, `UpdateToastModel`, `PlayLinkRouter`, `TipService` |
| X23 | `Shell.Masthead.UI.cs:219-237, 477-516, 620-635` | omnibar store, focus tickets, fetch with cancel, choose/play decisions | `OmnibarModel` (with `Shell.cs:2108-2338`) |
| X24 | `Shell.History.UI.cs:65, 133, 324` | non-reactive developer read; destructive `Store.Clear/Remove` | props fact + `HistoryVerbs` |
| X25 | `Sidebar.UI.cs:72-172, 246-253, 740-782, 952-1049, 1337-1498, 1746-1866, 1873-1878, 1922-2035, 2098-2129` | binder build/pump, account swap, reorder commit, filter/undo keys, plan/publish A/B pipeline + freeze, selection/play/pill sweeps, tree-selection mutation, the global `DragPeek` write, section toggle + `MarkNewReleasesSeen`, folder activation, navigate/play, create flows, payloads, `PlanDiff` | `NavModel` service (binder, projection, selection, play state, drop decisions, edit ops). Row planning + `PlanDiff` + freeze → Classic structure (`Classic/Structure/Rules/SidebarPlanner`) |
| X26 | `Sidebar.UI.cs:1434` | `OwningSection` branches on `doc.Layout == Library` inside the "one renderer" | deleted: the Classic planner owns its layouts |
| X27 | `Sidebar.UI.Library.cs:27-57, 95-338, 360-397, 720-725, 967-983` | `LibrarySession` drill stack, debounce, self-correcting effects, empty-state computation, chip commit, Esc logic | `NavModel.Library` (core) |
| X28 | `Sidebar.UI.Edit.cs:54-65, 215, 232, 379-392, 443-444, 532-582` | global `Drag.ExtraChip`, layout branches, `s_enabledEmptySection` write, binder reads, filter step, key dispatch | `NavEditModel` (core); the Outline view → Classic |
| X29 | `Sidebar.UI.Menus.cs:35-56, 151-252, 577-628, 744-759` | `Run` dispatch, ToggleKind/MoveBy/HideItem, legality, commit, rename, the "· modified" rule | `NavMenuModel` (core) |
| X30 | `Sidebar.UI.Drop.cs:59-334, 374-383, 496-506` | drop arm/commit decisions, refusal toasts, signed-out guard | `NavDropRules` (core). Pointer→target mapping → structure |
| X31 | `Sidebar.UI.Slot.cs:219-248, 309, 395, 437, 802-829`; `Sidebar.UI.Rows.cs:539-563, 748-815, 1046-1056`; `Sidebar.UI.Flyout.cs:137, 175-179` | online read, label fallbacks, `RootlistSection` rule, `TreeRowFacts`, `ShowsPinMark(layout)`, LikedArt table reads, `CountHost`, `ShortcutCount`, the age-badge clock, folder expansion persist, flyout nav state | `NavModel` facts (counts, pin state, labels, ages, liked mosaic urls) |
| X32 | `Setup.UI.cs:426-434, 454-504, 511-658, 889-898, 993` | wizard markers/open (1–2 posts), the whole `WizardSession` FSM, `LoadHero` disk I/O, crash-consent write | `WizardModel` (core). Opened in boot so frame 1 includes the plate (§5 E5) |
| X33 | `Setup.UI.Runtime.cs:137-497` | `RuntimeModel` (network tasks, settings writes) | core |
| X34 | `Settings.UI.cs:107-131, 291-301, 396-402, 425-528, 702-706` | statics captured from context, `Toggle` writer, language, tray writers, GPU enumerate/pick, toast-delivery OS query | `SettingsDoc` writers (core) |
| X35 | `Settings.UI.Appearance.cs:195-213, 229-234, 279-298, 355-378, 462-487, 551-625, 729-744, 773-821` | theme/zoom/page-motion/density/row-style writers, rail reset, sidebar layout/density, lyrics blur, stage toggles (raw writes bypassing `Prefs.Stage`) | `SettingsDoc` + theme option schemas (§4). Most rows become theme options |
| X36 | `Settings.UI.Playback.cs:219, 374-449, 560-569`; `Settings.UI.Video.cs:213-311`; `Settings.UI.Storage.cs:280-344`; `Settings.UI.Privacy.cs:135-168, 205, 326-391, 565-594`; `Settings.UI.About.cs:255-317` | EQ/crossfade persist + DSP push, `File.Exists` in render, override actions, budgets, crash mode, per-render file I/O, Notepad launch, test report, receipts sampling | `SettingsDoc` writers + `SettingsFacts` producers (core) |
| X37 | `ReleaseNotes.UI.cs:137, 1155, 1201-1259, 1332` | last-seen write, developer/deep-link read, after-update gate/latch/load, auto-show write | `ReleaseNotesModel` (core) |
| X38 | `Feedback.UI.cs:370-537`; `Crash.UI.cs:62-91, 153-161, 260-264`; `LogsPage.UI.cs:328, 351-473`; `Diagnostics.Scroll.cs:84-110` | report composer I/O, crash consent/enqueue/preview I/O, log disk walk/export, scroll settings persist | core models |
| X39 | `Detail.UI.cs:419-489, 669-684, 703` | `RailCell` persists rail prefs from the view; pref reads; the theme effect; Hero forcing the vertical layout | Classic structure (`ThemeState` writes go through the core host); pref reads → props |
| X40 | `Track.Table.Chrome.cs:359, 804-889`; `Track.Table.cs:903` | density/tempo/plays writes from the chrome | density → the theme's `ctx.Options.Set`; tempo/plays → `TableColumnsPref` verbs (core) |
| X41 | `Show.Page.cs:264-266, 414`; `Playlist.Page.cs:1005-1025`; `User.Page.Library.cs:20, 379-380` | view prefs persisted from page hosts | already hosts; keep them, but in typed pref services, not raw `Platform.Settings` |
| X42 | `Queue.UI.cs:326, 948`; whole file render subscriptions | autoplay raw read/write; two skins inline | `QueueModel`; skins → Classic structure |
| X43 | `Home/HomeScreen.cs` facet effects; `Daylist.UI.cs:400` | facet switch effects; daylist clock pref read | `HomeHost` (core). The clock pref arrives in props |
| X44 | `Artist.Page.cs:144-291`, `Detail.UI.cs:514-735`, `Profile.Page.cs:102-238` | `Signal<ThemeKind>` republish of `Tok.Theme` to re-key washes | deleted: tone/wash keys come from the style epoch (§5 E3) |
| X45 | `Controls.Cta.cs:474-485`; `Playback.CardSeam.cs:40-41` | `ShowsPause` relation decision; UI seam installed by playback | `PlayRelation` fact (core) in props |
| X46 | `Controls.Podcast.cs:102-139` | pure `LedgerPartsOf/LedgerGeometry` in a UI file | core rule file |
| X47 | `Shell.UI.cs:238-281, 1006` | band/mode seeding + the sidebar presentation effect; `NotFoundPage` logging | `FrameModel` with policies (core). Logging → host |
| X48 | `[PR205] Shell.UI.cs:300-311, 1472-1526` | `NavStylePresenter`, `SubRowPresenter`, seeded presented copies | deleted (§4) |

### 3.2 Core that holds presentation → style or structure layers

| # | Today (path:line) | What it is | Goes to |
|---|---|---|---|
| P1 | `Platform/Design.cs` (all 2,613 lines) | type ramp, colour ladders, on-media ladder, stage arm, wash alphas, nav motion recipes, cover leaves | split three ways. **Style roles**: §1 Size, §3 Colors, §4 OnMedia, §5 StageArm, §8 Type, §9-10 Motion/Nav, §15 Wash. **Classic structure**: §17 cover-leaf components, which become its material views. **Core colour science**: §6 `Palette.ToColor/Lift/Vivid/Hairline/PageTone/TextInk` with explicit kind/alpha params; §7 placeholder math; `AccentLadder.cs` |
| P2 | `Shell.cs:148-204` route table `Icons.*`; `Shell.cs:1236-1323` `PlayerBarLayout.ForTier` metrics | route glyphs; per-tier button boxes, art size, LeftW, gaps | icon roles per `RouteKind` (style); tier visible-sets → `PlayerBarPolicy` (structure), boxes/sizes → style metrics |
| P3 | `Shell.Chrome.cs:26-172` | allocator budget constants | `ChromePolicy` (structure, reading style control heights) |
| P4 | `Shell.PlayerBar.cs:21, 273-296, 354, 357, 369-378, 401-418, 423-431, 455-468, 585, 594-597` | `FluentGpu.Controls` import, slot widths, glyph choices, display strings, picker max height, podcast layout, `ThumbX`, time slot widths | `PlayerBarPolicy` + style metrics/icon roles. Loc keys stay core (content) |
| P5 | `Sidebar.cs:54-68, 340-617, 1339-1346, 1483-1514, 531-540`; `Sidebar.Library.cs:32-61, 147-178, 275-278`; `Sidebar.Edit.cs:50-52, 115-126`; `Sidebar.Rules.cs:78-84, 132-144, 182-200`; `Sidebar.Resize.cs:29-33, 109-113`; `Sidebar.Cards.cs:92-166`; `Sidebar.Layout.cs:594-597` | the whole row pixel ladder, head metrics, grid cells, drop-cue geometry, label-fit 7 px/char, band widths, card builder with tokens, density→shape | Classic structure (`Rules/SidebarGeometry`, `SidebarPlanner`), reading style metrics. Band widths → `NavPanePolicy` |
| P6 | `Stage.cs:78-102, 258-276, 351-357, 390-416`; `Stage.Layouts.cs:94, 188, 269-289, 367-380, 469-470` | DIP constants, fonts, tone alphas, per-look geometry, scrim, ring metrics | each structure's stage rules + style roles (fonts, alphas). `StagePolicy` keeps only aspect thresholds |
| P7 | `Visualizer.Palette.cs:37-201`; `Visualizer.Particles.cs:68-79`; `Visualizer.Covers.cs:322`; `Verse.cs:724-748` | arm colours, contrast floor, ghost alphas/scales | stage-arm role table (style). Faces read it |
| P8 | `Lyrics.cs:140-166, 1456-1521, 1599-1660` | Fx σ ladder, surface font sizes/alphas/shimmer widths, interlude sizes | `LyricsLinesStyle` metrics (structure, reading style type roles) |
| P9 | `Video.cs:1374-1384, 1425` | PiP sizes, gesture margins | `PanelPolicy`/PiP policy (structure) |
| P10 | `Rail.cs:130-131, 170-171, 344-361` | swatch ARGB + loc keys in the catalog; hero geometry | swatches stay (content catalog); hero geometry → structure |
| P11 | `Shell.Palette.cs:84-114` | glyphs + `Loc.Get` in the pure table | glyph → icon roles; loc keys stay |
| P12 | `Shell.Omnibar.Rows.cs:43` | `RowShape` (a `SurfaceShape`) in rules | `ShapeRole.OmniRow` (structure maps it) |
| P13 | `Setup.cs:41-104, 306-330, 405-427` | plate geometry, Lottie recolor math, runtime widths | structure (geometry) + style (recolor inputs = accent roles) |
| P14 | `ReleaseNotes.cs:1456-1632`; `ReleaseNotes.Host.cs:51-79` | card/viewer metrics; kind-pill loc mapping | structure; loc mapping stays |
| P15 | `Settings.cs:102-216, 633-659`; `Diagnostics.Tiles.cs:31-122` | glyph names in the catalog, hero tone | icon roles; tone → style |
| P16 | `Track.Rules.cs:113, 143-243, 274-413, 688-873, 1585-1588` | `ColumnSet.Classic`, row/header heights per skin, art sizes, relief ladder, command-bar fit | `ColumnSet` loses `Classic`; heights → `TrackListStyle.Extent` (structure via style metrics); the relief ladder stays core (behaviour over the widths the style declares) |
| P17 | `Detail.cs:73-144, 286-359, 569, 1145, 1405-1470` | breakpoints, hero arithmetic, sticky inset 36/32, band floor latch, `Config` layout knobs | Classic structure (rail/hero geometry); `Config` split (§2.5.4) |
| P18 | `Surface.Rules.cs:77-126`; `Surface.Parts.cs:44-58` | shape presets with pixel metrics; plate fills | structure `ShapeRole` map + style plate roles |
| P19 | `Controls.cs:504-541, 1015-1032`; `ButtonRules.cs:50-129` | theme-branching hero backdrop/veil, follow tint math | style control roles (`Follow`), Classic material views |
| P20 | `Artist.UI.cs:51-158, 993-1091, 1273` | hero tiers/heights + hard-coded gradients/scrims | Artist structure rules + style on-media roles |
| P21 | `Concert.UI.cs:134, 251`; `User.Cover.cs:331-379, 528, 772-773, 869-871` | `DarkOr` fork; treatment inks | style roles; treatment inks are content (on-media ladder) |
| P22 | `Home/Zones.UI.cs:88-115` | zone grid tables | structure rules |
| P23 | `[PR205] Platform/Page.Rules.cs:22-221` | gutters, head rhythm, extents | structure rules reading `Space.Gutter` + type line heights |
| P24 | `[PR205] Sidebar.Zune.cs:68-292`, `Artist.Bleed.cs:31-301`, `Design.cs:1429-1449` (`ZunePivot`, `ZuneSubPivot`, `Wordmark`) | band rhythm, bleed rules, Zune type roles | Zune structure (`Rules/Band`, `Rules/Bleed`); type → `Type.Pivot/SubPivot/Wordmark` roles in every style |
| P25 | `E\FluentGpu.Engine\Dsl\Spacing.cs:11-32`, `Radii.cs:10-28`, `Typography.cs:39-46`, `Elevation.cs:15-44`, `Animation\MotionTok.cs:170-200`, `Dsl\Theme.cs:33-34` | engine consts/statics | `ThemeDefinition` tables (§5 E3) |

---

## 4. Switch dissolution table

### How to read this table

Each switch gets one verdict:
- **(a)** a Classic option (Classic structure "CS" or Classic style "CY");
- **(b)** an option of another theme's layer (WinUI structure/style "WS/WY", Zune structure/style "ZS/ZY"; Zune Fluent = ZS + WY);
- **(c)** a theme-independent user preference (core, passed to views as a fact);
- **(d)** deleted.

**Zero legacy** means:
- every key below either moves to `theme.<id>.<layer>.<option>`, becomes a typed core pref, or is deleted;
- a one-time migration latch `theme.bootstrap.version = 1` (in `Platform.Boot`, before the window) copies old values to their new home and then deletes the old key;
- the latch is the only code that still names an old key, and it is removed one release later (§7 Q12).

### 4.1 The 29 master switches (`map-shell.md §0.1`)

| # | Switch (key) | Verdict | New home | Code that disappears |
|---|---|---|---|---|
| 1 | Light/dark/system `theme.mode` | (c) | `Appearance` svc. Every style ships light + dark. Entry points: Settings "App mode", profile/account menu row, palette | the four apply routines (X20); the title-bar sun/moon (PR 205 already deletes it); 27 `Tok.Theme ==` branches in app code (now style roles); the `Signal<ThemeKind>` republish ×3 (X44); `StageInk.IsDark`/`pal.Dark` arm reads (~113, become the role table) |
| 2 | Accent (OS only) | (b)/(a) per style | `AccentPolicy`: CY = System. WY = `style.useWindowsAccent` (default on; off = #0078d4 ramp, W-ThemeOptions). ZY = `style.accent` (5 swatches, default #ff3ca6; light = one step darker, Z-Tokens) | the three `Tok.SetAccent(SystemAccentRamp())` copies (`Shell.Host.cs:685`, `Shell.UI.cs:701`, `Settings.UI.Appearance.cs:207`) |
| 3 | Colour washes `appearance.colorWashes.enabled` (+ PR 205 `wash.surfaces/accent/nowPlaying`) | (a) CY | `style.surfaces` Off/Subtle/Rich, `style.accentFromArtwork`, `style.nowPlayingColors` | `Prefs.Appearance.ColorWashes/SurfaceWash/AccentFromArtwork/NowPlayingColors`; `ColorWashRules` (`[PR205] Prefs.cs:352-386`); the `Disabled`/`Apply` props of the cover leaves (`Design.cs:2337-2339, 2443, 2575`) because only Classic's material views remain and they read their own options; 18 page reads (`Detail.UI.cs:669`, `Artist.Page.cs:295`, `Profile.Page.cs:242`, `Recents.Page.cs:1162, 1205`, + PR 205 13+4+5) |
| 4 | Track row style `detail.rowstyle` | (a) CS `structure.rowStyle` Modern/Classic | Classic `TrackListStyle` builds one of two row templates; Classic queue/chart views pick a skin | `ColumnSet.Classic`, `Snapshot.Classic`; ~324 `classic` branch mentions in `Track.UI`, `Track.UI.Bound`, `Track.Rules`, `Track.Table(.Chrome)`, `Queue.UI`, `Artist.UI.Chart`, `Artist.Page`, `Detail.cs`; the list key `classic|modern` (`Track.Table.cs:1066`); chart keys; queue keys `:classic=` (`Queue.UI.cs:665, 708`) |
| 5 | Row density `detail.rowdensity` (4 steps) | (a)/(b) per style | CY `style.density` Compact/Default/Cozy/Comfortable. WY `style.density` Default/Compact (rows 40/48 → 32/40). ZY `style.density` compact/default/comfortable (44/52/60). Reaches lists only through `StyleMetrics.Row` | `TableRules.RowHeightFor/ArtSizeFor(density, classic)` → metrics; `TableDensityButton/Panel` (PR 205 already removes them; "Row size ▸" in ⋯ writes the style option via `ctx.Options.Set`); `Album.Pane.cs:192` read |
| 6 | Sidebar layout `sidebar.layout.id` (PR 205 `ShellNavStyle {Classic, Library, Zune}`) | (a) CS `structure.sidebar` Classic/Library; the Zune value → (d) | Zune nav *is* the Zune theme. Migration: stored 2 → `theme.id = wavee.zune`, and `sidebar.layout.lastPane` → Classic `structure.sidebar` | the `ShellNavStyle` enum, `ShellNavStyleRules`, `Sidebar.NavStyle`, `Shell.Ui.PresentedNavStyle` (+ `HoistSettleMs`), `NavStylePresenter`/`SubRowPresenter`, ~70 branch sites (21 in `Shell.UI.cs`), `PageHead.HoistedFor/ViewsInBandFor`, `BandLayout.InRow2`, `ArtistBleed.Applies(zune)`, `sidebar.layout.lastPane`, `SidebarLayoutCards(ShellNavStyle)` and the Settings/Setup layout cards (now theme cards), `Sidebar.SwitchLayout/SwitchNavStyle` (an option write), the palette verbs `SidebarZune` etc., `ShellNavStyleTests` (rewritten as option tests) |
| 7 | Sidebar density `sidebar.pane.density` (+ PR 205 `sidebar.classic.covers`) | (a) CS `structure.libraryDensity` (Library layout), `structure.showCovers` (Classic layout) | Classic planner | `SidebarSection.ShapeFor(layout, kind, density, covers)` (`Sidebar.Layout.cs:594-597`); `Sidebar.Density`/`ClassicCovers` signals; the Host undo for them |
| 8 | Library view list/grid (`sidebar.json` `view`) | (a) Classic theme **state** `state.libraryView` | Classic Library layout | the `view` field in the `sidebar.json` DTO (`Sidebar.Store.cs:299, 418, 441`) moves out of the core doc |
| 9 | Pane width/collapse/filter/sort/showLiked/hidden kinds | split | width + collapse → per-theme **state** (bounds from `NavPanePolicy`: Classic 180–460; WinUI fixed 280/48, no resize; Zune none). Filter chip, sort, desc, ShowLiked, hidden kinds → (c) the core `NavLayoutDoc` (what is listed, not how) | `SidebarPaneBounds` constants (`Sidebar.cs:54-68`) → policy |
| 10 | Hide track artwork `appearance.trackArtwork.hidden` | (a) CS `structure.hideTrackArt`; WS `structure.artworkInRows` (default on); Zune: none (rows never show art by design) | read inside each structure's list styles | 11 reads (`Track.Table.cs:612`, `Artist.Page.cs:297`, `Artist.UI.Chart.cs:177`, `Queue.UI.cs:469, 985`, `Recents.UI.cs:296, 316`, `Search.Page.cs:136`, `User.Page.Library.cs:1397`, `Rail.UI.cs:732`, `Stage.Artist.UI.cs:561`); `HitContext.HideTrackArt`; the `Drag.InsertionCards(showArtwork)` parameter |
| 11 | Marquee `appearance.marquee.enabled` | (a) CS `structure.marquee` | Classic row + bar templates. WinUI/Zune trim with tooltip | `Prefs.Appearance.Marquee`; `RowPresentation.Marquee`; the `PBUI:339, 378, 380` branches. Reduced motion still disables it (c) |
| 12 | Page motion `appearance.pageMotion.style` (dev-only, 5 styles) | (d) | each structure picks its recipe kind per relation; each style supplies durations. Classic = today's default "Classic" recipe, WinUI = "WinUi" recipe, Zune = panorama slide | the `PageMotionStyle` enum, `Design.Nav.RecipeFor(style, …)` switch (`Design.cs:1868-1897`), the Spatial/Fluent/None arms nobody uses, the developer Settings row (`Settings.UI.Appearance.cs:125, 229-234`), `Shell.PageMotionStyle()` (`Shell.cs:1007`), the key |
| 13 | Liked cover `appearance.likedCover.style` (9) | (c) content pref | the treatment catalogue is a content renderer (§2.6.10). Any structure that draws a Liked cover (Classic, WinUI) offers the picker; the value is shared | the key moves to the `LikedCoverPref` service; `User.Cover.cs:93, 141, 1118` read through props |
| 14 | Daylist clock `appearance.daylistClock` | (c) | `DaylistProps.Clock` | `Prefs.Appearance.DaylistClock` reads in views (`Daylist.UI.cs:400`, `Playlist.UI.cs:990`) |
| 15 | Track page layout + rail uniform + 8 rail widths | (a) CS `structure.pageLayout` Automatic/Hero, `structure.railUniform`, rail widths → Classic **state**; "clear remembered sizes" is a Classic option action | Classic detail structure | `Prefs.DetailHero`; `Detail.UI.cs:419-489, 669-672, 703`; `ResetRailSizes` (`Settings.UI.Appearance.cs:474-487`) |
| 16 | Tempo / plays columns | (c) `TableColumnsPref` | the structure's column policy decides whether it can show them (Zune shows "plays · time") | the epoch-gated raw reads (`Track.Table.cs:583-592`) |
| 17 | Zoom (`appearance.zoom*`) | (c) | `ZoomService` | the duplicated persistence (X18). Auto-zoom is computed pre-window (§5 E6) |
| 18 | Stage mode/layout/visualizer/spectrum/hero/calm/moments/sensitivity/sync/gallery/tip (14 keys) | split | **(c)** mode, visualizer face, sensitivity, sync offset, calm, moments, lyrics overlay, gallery open, tip seen. **(a) CS** layout Card/LargeArt/Centered/Artist, spectrum, heroLyrics/heroMotion/heroDim (Classic stage structure options, editable in its stage options popover and in Settings). WinUI/Zune stages have their own (smaller) layout sets in their structures | `Prefs.Stage` keeps only the (c) keys; `Stage.Layouts.cs` geometry → structures; the `Settings.UI.Appearance.cs:777` raw writes |
| 19 | NPV presentation cover/deck (dev-gated) | (a) CS `structure.heroPresentation` with `Visibility = Developer` in the schema | Classic panel hero | `Prefs.NpvPlayer.Presentation/ResolvedPresentation` (`Prefs.Player.cs:41-64`); the developer gate becomes generic schema visibility |
| 20 | Deck preset + per-preset options | (c) content pref (`DeckPrefs`) | content renderer catalogue | the `RSt` writes (X10); the two 12-arm dispatches stay inside the deck renderer (content, not theme) |
| 21 | Player bar remaining `playerbar.duration.remaining` | (c) | `PlayerBarProps.ShowRemaining` + `ToggleRemaining` verb | `PBUI:1940, 1968` reads |
| 22 | Lyrics blur / secondary line | blur (a) CS `structure.lyricsBlur` (Classic lines have DoF; WinUI fades by distance in its own template; Zune uses opacity ramps). Secondary line: (c) session | `LyricsLinesStyle` | `Prefs.Lyrics.BlurStrength` (`Lyrics.cs:1425`); the Settings slider moves into the Classic schema |
| 23 | AI lyrics prefs | (c) | unchanged | — |
| 24 | Video placement/aspect/ontop/pip/quality | (c) | unchanged | PiP size bounds → policy (P9) |
| 25 | Window width tiers | (b) structure policies | each structure's `WindowBandPolicy`/`NavPanePolicy`/`PlayerBarPolicy`/`PanelPolicy`; Classic keeps today's ladders exactly | the hard-coded ladders in `Sidebar.Resize.cs:26-100`, `Shell.cs:1195-1323`, `Shell.Chrome.cs:390-443` become functions of a policy |
| 26 | Reduced motion | (c) | motion tokens carry `ReducedMotionPolicy` | 73 `Design.Reduced` reads drop to the few that choose structure (e.g. Ken Burns off), via `ctx.Reduced` |
| 27 | Developer mode / FPS / StoreShot | (c) | props facts (`Presenting`, `Developer`) | direct reads in views (`Shell.History.UI.cs:65`, `ReleaseNotes.UI.cs:1155`, …) |
| 28 | Tray / notify / update | (c) | unchanged (OS; the tray follows the taskbar) | — |
| 29 | Language | (c) | unchanged | — |

### 4.2 Page-level and PR 205 switches not covered above

| Switch | Verdict | New home / what disappears |
|---|---|---|
| `Detail.Config.SlimHead/HoverHeart/PlainRows` (`[PR205] Detail.cs:1670-1699`) | structure decision | knobs leave `Config`; each structure's Liked view decides |
| `PageHeadKind` / `Hoisted` / `ViewsInBand` (`[PR205] Page.Rules.cs:153-221`) | (d) | PageChrome channel (§1.10d) |
| `ZuneSubRow` (`[PR205] Sidebar.Zune.cs:35-153`) | ZS rule | `SubRowOf(route)` becomes a pure Zune structure rule over PageChrome. No core enum |
| `sidebar.zune.pins` | (b) ZS `structure.bandPins` (default on) | also exposed by Zune Fluent |
| Artist bleed `ArtistBleed.Enabled` const | (d) | Zune's artist view always publishes the backdrop; the gate is deleted |
| Chrome ink binds `Shell.Ui.ChromeInk*` (always 0-mix) | (d) | deleted; Zune's frame computes ink from the backdrop if it ever needs to |
| Entrance clock (59fd96c1) | (d) | disposable, per its own commit message |
| Library per-kind `view/size/sort/desc/scope` | view/size (c) `LibraryViewPrefs`; sort/desc/scope (c); `leftw/midw` → per-theme state | `Platform.cs:355-372` raw keys become a typed service |
| `podcast.views` (show prefs) | (c) | typed `ShowViewPrefs` service (`Show.Rules.cs:700`) |
| `detail.sort.col/desc:<ctx>` | (c) | unchanged (`Track.Table.cs:890-921`) |
| `playback.autoplay` | (c) | `QueueVerbs.ToggleAutoplay` |
| `Design.Type.PivotLabel`, `FacetTitleStyle/FacetCompactStyle`, `PivotTabs` | (d) / style roles | Choice contract (§2.6.6) |
| Legacy CTA pill set (`Controls.Cta.cs:196-284`) | (d) | already marked DELETE |
| `Prefs.BumpAll` (0 callers) | (d) | the option store notifies per key |
| `TemplateParts` "theme is startup-resolved" cache | (d) as written | keyed on the style epoch (§5 E12) |

---

## 5. Engine work required (refines `map-engine.md §7`)

"Why not app-level" names what breaks if Wavee builds the feature itself. Every feature ships with a VerticalSlice
suite and Engine.Tests in Debug and Release.

### E1 — `ViewSlotEl`, `ViewRegistry`, `ViewContext`, `StyleKey` (the structure layer's carrier)

```csharp
public readonly record struct ViewKey<TProps>(string Id) where TProps : class;        // "wavee.player.bar@1"
public readonly record struct StyleKey<TStyle>(string Id) where TStyle : class;
public delegate Element ViewTemplate<TProps>(TProps props, in ViewContext ctx) where TProps : class;
public sealed class ViewRegistry {                        // immutable after Build(); Epoch identifies the table
    public static readonly Context<ViewRegistry> Current; public int Epoch { get; }
    public static ViewRegistry Build(ViewTableBuilder b); // flat: key -> template | style record
    public ViewTemplate<T> Get<T>(ViewKey<T> k); public TStyle Style<TStyle>(StyleKey<TStyle> k); }
public sealed record ViewSlotEl<TProps>(ViewKey<TProps> Key, TProps Props) : Element { ElementTypeId => 20 } // id 20 is free (max today 19, SpriteFieldEl)
public readonly ref struct ViewContext { RenderContext Hooks; ThemeDefinition Style; IOptionReader Options;
    T Role<T>(RoleKey<T> k); Element View<TP>(ViewKey<TP> k, TP p); TStyle StyleOf<TStyle>(StyleKey<TStyle> k); bool Reduced; }
```

- **Reconciler**: `MountViewSlot` is a boundary effect (like `MountShow`, `Reconciler.cs:882`). It reads the registry provider signal, calls the template, and runs `ReconcileSingleChild`.
  - Props equality-gates the call (generated `DiffProps` / record equality).
  - A registry epoch change with the same template identity diffs in place. A different template remounts the child under the slot, while state survives in the host.
- **Why not app-level**: a host `Component` that looks up a delegate would re-render the whole host on a registry change. It cannot carry an engine-owned swap transition (E2) or recycling checks (E13). And `UseContext` resolution would be an O(depth) walk on every host render instead of one cached signal per slot.
- **Acceptance**:
  1. A registry swap re-runs only the slot effects (counted).
  2. Equal props skip the template (counted, 0 allocations).
  3. 10k slot renders add < 1% reconcile time vs direct calls (VerticalSlice bench).
  4. A DEBUG analyzer flags templates that call hooks positionally or capture mutable state.

### E2 — Slot swap transitions (shared by option changes and the theme switch)

```csharp
public enum SwapTransition : byte { Snap, CrossFade, Choreographed }
ViewSlotEl { SwapTransition OnTemplateChange = Snap; float SwapMs = 0; }
// Root: AppHost.SwitchRoot(Func<Element> next, SwapTransition t, float ms)
```

- **Mechanism**: the old child becomes `ExitFrozen` (no render) in one opacity group (`PushLayer`, `DrawList.cs:13-14`) fading out. The new child mounts in the same flush and enters 0→1 over the same window. Both layers composite from offscreen RTs only for the duration.
- **Why not app-level**: two app-side keyed children work today, but the old tree keeps rendering (it is not frozen). Its bound channels snap. And exit orphans are capped at 64 per parent, so a whole page as an orphan is untested.
- **Acceptance**:
  1. Frame capture: no frame contains a mix of old and new geometry outside the cross-fade group.
  2. 0 hot-phase allocations during the fade.
  3. Two full-window RTs max.
  4. Reduced motion → Snap.

### E3 — `ThemeDefinition`: theme-scoped non-colour tokens (the style layer's engine half)

```csharp
public sealed record ThemeDefinition(string Id, TokenSet Light, TokenSet Dark, TokenSet? HighContrast,
    TypeRamp Type, SpacingScale Spacing, RadiiScale Radii, ElevationSet Elevation,
    FrozenDictionary<MotionTokenId, MotionTokenDef> Motion, FontSet Fonts, MaterialPolicy Materials,
    IconSet Icons, ExtTokenTable Ext, ControlStyleTable Controls);
public static void Tok.Use(ThemeDefinition d, ThemeKind k);   // ONE pointer swap + Epoch++ (and StyleEpoch++ if d changed)
```

- **Changes to engine statics**:
  - `Spacing`/`Radii` stop being `const` (`Spacing.cs:11`, `Radii.cs:10`) and become `static float X => Tok.Definition.Spacing.X`. App code keeps the same syntax; only const contexts (attribute args, default parameters, switch patterns) must change. A grep finds them.
  - `Ui.Caption…Display` (`Typography.cs:39-46`) read `Definition.Type`.
  - `Elevation` reads `Definition.Elevation`.
  - `MotionTok.Get` reads `Definition.Motion` first (the planned "per-theme FrozenDictionary", `MotionTok.cs:13-15`).
  - `Theme.BodyFont`/`IconFont` are replaced by `Definition.Fonts`, and the DirectWrite `DefaultFamily` const (`TextLayoutEngine.cs:69`) reads it.
  - `ThemedIconRegistry` gets a theme overlay.
  - `Materials.*` gets a policy input.
- **Ext tokens**: an interned `ExtTokenId → ColorF` per kind, read as a field.
- **Why not app-level**: engine controls (Button, Slider, TitleBar, NavigationView, MenuFlyout, ContentDialog) read engine statics. An app-only token table would leave every control in the WinUI look under Zune.
- **Acceptance**:
  1. `Tok.Use(def)` switches spacing, type, radii, motion and fonts for engine controls in one flush.
  2. A layout-affecting token change marks only geometry-changed nodes dirty.
  3. The gallery runs all three built-in styles.
  4. The WinUI ThemeDefinition reproduces today's engine defaults bit-for-bit (golden test).

### E4 — Option cells (`IOptionReader`/`OptionStore`)

```csharp
public sealed class OptionStore { public Signal<T> Cell<T>(OptionKey<T> k); public void Set<T>(OptionKey<T> k, T v); }
// ctx.Option(k) subscribes the SLOT effect; ctx.Options.Set(k, v) allowed only for keys of the slot's layer (DEBUG assert)
```

- **Why engine**: the read must subscribe the ViewSlot's effect, not the host. Persistence stays app (`theme.<id>.<layer>.<opt>`).
- **Acceptance**: an option flip re-runs only the slots that read it. The research acceptance (e), "an option change does not rebuild surfaces that do not read it", is counted.

### E5 — Atomic `SwitchTheme` orchestration

```csharp
public Task PrewarmTheme(ThemeDefinition next, PrewarmSet s);   // worker: fonts (E9), icon geometry, image pins for the visible route
public void SwitchTheme(ThemeDefinition next, ThemeKind kind, Func<Element> nextRoot, SwapTransition t, float ms);
```

- **One flush**:
  1. `Tok.Use(next, kind)`;
  2. app callback (registry + option providers);
  3. `SwitchRoot(nextRoot, t, ms)` (E2);
  4. `RethemeAll` is skipped for the frozen old root;
  5. the DWM backdrop, dark attribute and window background are applied at the first present of the new root;
  6. detached hosts (`TickDetachedHosts`, `FluentApp.cs:495`) switch in the **same tick**;
  7. the app gets `OnThemePresented` for persisting `theme.id`.
- **Light/dark only (same theme)**: `SwitchTheme(sameDef, newKind, …, Snap or CrossFade)`. It replaces today's `ThemeControl.Request` + 250 ms brush fade (`AppHost.cs:6293`), whose colour-only fade is kept as the `CrossFade` choice for a kind flip.
- **Why not app-level**: the app cannot order "DWM material at first present", cannot freeze the old root, and cannot tick detached windows in the same frame.
- **Acceptance**:
  1. A frame capture across 100 switches finds zero frames where any node shows tokens of the other theme outside the fade group.
  2. Video keeps presenting (E7).
  3. Switch latency from the click to the first new frame ≤ 2 frames after prewarm.
  4. Prewarm ≤ 300 ms off the UI thread.

### E6 — Main-window reveal gate + pre-show material

```csharp
AppOptions { bool RevealOnFirstPresent = true; Func<bool>? FirstFrameReady; WindowMaterial InitialMaterial; ColorF InitialBackground; }
FluentApp.SetWindowMaterial(WindowMaterial m)  // None | Mica | MicaAlt | Acrylic + dark flag; live
```

- **Mechanism**:
  - Create the window cloaked (`DWMWA_CLOAK`) or hidden, using the `StartHidden` path (`FluentApp.cs:803-807`).
  - Apply the material/dark attribute from `InitialMaterial` before the first present (today `:327`, from `Theme.Dark` only).
  - Mount and run frames, then uncloak or show at `HasPresentedContent` (`AppHost.cs:8071`), reusing the detached reveal gate (`AppHost.cs:2872-2883, 3244-3282`) with its timeout.
  - The swapchain is always composited (§1.7 step 4).
- **Why not app-level**: the HWND is shown inside `RunCore` before the first present (`FluentApp.cs:427` vs `:494`). The app has no hook between them.
- **Acceptance**:
  1. A cold start capture shows the first visible frame fully in the target theme, including an opaque Zune ground, with no bare-Mica frame.
  2. Time to first visible frame regresses ≤ 5 ms vs today.

### E7 — Persistent subtrees across a root swap (`Flow.Persist`)

```csharp
public static Element Flow.Persist(string key, Func<Element> build);  // the live node subtree is re-parented into the
                                                                      // new root at the slot with the same key; never remounted
```

- **Use**: the video presenter (DComp hole / `MediaPlayerElement`, `Video.UI.cs:505-603, 887-1188`). Optionally the visualizer clock host.
- **Why not app-level**: a remount releases and reacquires the video surface (a black frame / stall). Only the reconciler can move a mounted subtree between parents. The KeepAlive "detach/attach" path (`Reconciler.cs:1986-2017`) is the closest primitive, but it releases resources on park.
- **Acceptance**: a theme switch during 4K video playback shows no dropped presented video frames (`MediaPlayerElement` stats) and no black frame.

### E8 — Scroll memory restored at first layout

```csharp
ScrollView.Create(..., memoryKey: SlotKey)  // offset captured on unmount/switch; applied in the FIRST layout pass, clamped
                                            // once extents are known; virtual lists use analytic extents (E1 styles)
```

- **Why**: after a switch or a KeepAlive drop, the page must open at its old offset without a "top then jump" frame. Today departing scroll is captured only in `ChildReconcilePlan.Commit` for keyed children.
- **Acceptance**: switching theme on a scrolled album page lands at the same item, with no intermediate frame at offset 0.

### E9 — Font preload and strict families

```csharp
Fonts.Preload(FontSet s) -> Task  // IDWriteFontFace creation off-thread
// DEBUG: unknown family = error, not a silent Segoe fallback (TextLayoutEngine.cs:1101)
```

- **Why**: today a theme font stalls first layout (`TextLayoutEngine.cs:1081-1097`) and a wrong family falls back silently.
- **Acceptance**: a switch to a style with uncached fonts shows no layout stall beyond 1 ms on the UI thread.

### E10 — Live backdrop switching

- **What**: `FluentApp.SetWindowMaterial` (E6) generalises `SetWindowMaterialAlt` (`FluentApp.cs:49-54`) to None/Mica/MicaAlt/Acrylic. `Theme.WindowBackground` is driven by `MaterialPolicy`. The inactive fallback (`AppHost.cs:5001`) uses the style's ground.
- **Acceptance**: WinUI's Backdrop option cycles through all four values live with no frame showing the previous material.

### E11 — TitleBar for three shells

- **What**:
  - a two-row mode (tab strip row with caption buttons + toolbar row; drag regions across both);
  - caption-button styling from `ControlStyleTable`;
  - keyed children + `CaptionInk` (PR 205 engine commits `d28a471fa`, `55dadcbcc`; merge them);
  - wordmark/lead content slots.
- **Why**: WinUI's chrome is 40 + 48 (canvas Shell-Top). Hit-testing and the caption region belong to the engine control.
- **Acceptance**: all three title bars pass the existing `TitleBarSuite` plus a two-row case; nav-buttons and captions work after a theme switch.

### E12 — Latent bugs the switch would expose

1. `RethemeAll` runs parked replays without the per-frame budget when the park epoch is older than the style epoch (`Reconciler.cs:1304, 1994-1999, 2186-2194`).
2. `TemplateParts.TryApplyCached` keys on `(part, partsEpoch, StyleEpoch)` (`TemplateParts.cs:70-83`).
3. A DEBUG assert fires on `Tok.Use` during `MountRoot` (`AppHost.cs:5292-5296`, map-engine §4.4 #3).
4. `IconFont` is captured at construction (`Factories.cs:56`) and is now read at render.

### E13 — Recyclable template guard

- **What**: in DEBUG, `BoundRowTemplate` output is checked against the recyclability rules (`Reconciler.cs:4185-4209`). A row template that breaks them fails a test, rather than silently dropping row recycling.
- **Acceptance**: every shipped list style of all three structures passes the check.

### E14 — Control style tables replacing global `StyleHook`s; structural parts for menus and dialogs

```csharp
public sealed record ControlStyleTable(Func<ControlRole, ButtonAppearance, ControlSize, Button.Style> Button,
    Slider.Style Slider, ToggleSwitch.Style Toggle, CheckBox.Style Check, ListBackplateStyle ListItem,
    MenuPresenterStyle Menu /* header + primary-verb strip part */, DialogStyle Dialog, ChipStyle Chip, ...);
```

- **What**: all 24 `Style` records are read from `Tok.Definition.Controls`. The global `StyleHook`s (`Button.cs:204`, …) go. `MenuFlyout` and `ContentDialog` gain presenter parts, so a style can draw Zune's header + round-verb strip or a square 548 dialog.
- **Why**: process-global hooks cannot switch atomically, and are a theme switch in disguise.
- **Acceptance**: the gallery renders every control in all styles. `StyleHook` symbols are deleted.

### E15 — High contrast (deferred, §7 Q11)

- **What**: `ThemeKind.HighContrast` + `SPI_GETHIGHCONTRAST` + per-style HC `TokenSet`. Not required for the four themes now, but the `ThemeDefinition` shape reserves it.

### E16 — Structure × style composition support (Zune Fluent)

- **Engine shape**: the engine needs no knowledge of "structure" or "style". The style is `ThemeDefinition` (E3); the structure is `ViewRegistry` (E1). Composition is the app pairing one registry with one definition.
- **Two engine guarantees make it safe**:
  1. Controls take their whole look from `ControlStyleTable` by role, so a Zune structure view that asks for `ControlRole.PrimaryAction` gets a WinUI accent button under the WinUI style.
  2. `ViewContext.Role<T>(RoleKey<T>)` reads typed app roles from an `ExtRoleTable` that is part of `ThemeDefinition` and swapped in the same pointer swap. So structure views never read a static app table that could lag the definition.
- **Acceptance**:
  1. The S3 cross-product probe (§1.11) renders 9 pairs.
  2. Zune Fluent ↔ Zune switches are a Snap/CrossFade of tokens only. Structure is identical, so the registry is the same and no slot remounts. Only geometry from metrics changes (rows 52 → 40), and that change is animated by the list's layout transition (no jump).

### 5.1 Cross-cutting mechanics

#### Startup, end to end (replaces today's single `SeedTheme`)

1. `Platform.Boot` opens settings synchronously and runs the one-time `theme.bootstrap` migration (§4).
2. `Themes.Install` → resolve `theme.id` (unknown → Classic) → compose structure + style.
3. Read option values for both layers and compute `StyleMetrics`, `Policies` and `Capabilities`.
4. `Appearance.Seed`:
   - `Tok.Use(style.Tokens, kind)`;
   - accent per `AccentPolicy`;
   - `Fonts.Preload` (joined before the first layout, with a budget);
   - auto-zoom computed from the monitor pre-window, removing the today-500 ms re-zoom (`Shell.UI.cs:295-311`).
5. Build `AppOptions` with `InitialMaterial`/`InitialBackground` and `RevealOnFirstPresent`.
6. Core models are seeded synchronously:
   - nav restore;
   - `NavModel` from the store (today the binder plans skeletons until `PumpBinder`, `Sidebar.UI.cs:1056-1060`; it is now pre-built at boot, so frame 1 has rows or reserved skeleton heights);
   - `WizardModel.Open` decided (the plate is in frame 1, not posted 1–2 frames late, `Setup.UI.cs:460-472`);
   - the restored page's material claim is computed from the persisted palette store in its first render, so frame 1 has the right tint and not the neutral ground (`Shell.UI.cs:925-930`).
7. `MountRoot` with `ViewRegistry` + option providers.
8. First present → uncloak.

#### Live switch

- §1.7 plus E5/E7/E8. The ordering:
  1. prewarm;
  2. close transient popups;
  3. capture scroll memory;
  4. one flush (tokens + registry + root swap);
  5. first present → DWM material;
  6. persist `theme.id`.
- Theme switches drop parked KeepAlive pages; back/forward rebuild on demand.

#### Cache keys per theme

| Cache | Today keyed by | Becomes |
|---|---|---|
| `Track.TracksFor` (`Track.UI.cs:115-150`) | `ColumnSet` incl. `Classic` | `(ColumnSet, StyleStamp)`; `ColumnSet` loses `Classic` |
| Track list key (`Track.Table.cs:1065-1066`) | density + `classic|modern` | `(scroll, styleKey, Stamp, query, reset)` |
| Chart shelf/cell keys (`Artist.UI.Chart.cs:20-21, 79-80, 412`) | art × classic | `Stamp` |
| Queue card keys (`Queue.UI.cs:665, 708`) | `classic=` | `Stamp` |
| `NavMountCache` (`User.Page.Library.cs:1209`) | — | keyed by `Stamp` |
| `Design.WatchedPlaceholder` (`Design.cs:1134-1165`) | caches thunks reading `Tok.Theme` | keyed by `StyleEpoch` |
| Engine `TemplateParts` prototypes | — | `StyleEpoch` (E12) |
| Glyph/shaping caches | — | unchanged: they key on font + size, so new fonts miss naturally |

- **What a Stamp is**: an int each structure computes from the options and metrics its style record read. It changes on any relevant option or style change, and only then.

#### KeepAlive on switch

Parked pages (3 slots, `Shell.UI.cs:887-891`) hold old-theme trees and are dropped with the old root; only the current page is rebuilt. An **option** change within a theme does not drop KeepAlive pages: their slots re-run on un-park, unbudgeted per E12.1.

#### Token completeness

- **Style layer**: the style test asserts that every `TokenSet` field, every `TypeRamp` role, every app role in `WaveeStyleRoles` (versioned), every `ControlRole` style and every `IconRole` is defined for light and dark (S2).
- **Structure layer**: the structure test asserts registry completeness (§1.4) and `RequiredRoles ⊆ Roles` for every shipped pairing.

#### Perf budget

- **Per frame**: zero added cost. Slot resolution happens only in the reconcile phase when a slot is invalidated. Role and token reads are field reads off the current definition.
- **Per slot render**: ≤ 1 delegate call + 1 cached signal read. No dictionary probe at render, because the registry is resolved to a typed entry when the slot is created.
- **Row recycling**: preserved. Templates are plain or bound trees (E13); `RowPresentation` stays the equality-gated value (`Track.Table.cs:2190`); extents are analytic from `StyleMetrics`.
- **Theme switch**: ≤ one route-change-equivalent remount + 2 full-window RTs for `ms`, measured in VerticalSlice.
- **Option change**: re-renders only the reading slots.
- **Cold start**: ≤ +5 ms to the first visible frame.

---

## 6. Per-theme structure

Notation:
- **Own**: the layer registers its own view.
- **Common**: it registers a `Themes/Common/` block (§6.5).
- **n/a**: a capability or policy makes the surface unreachable, and `Common.Nothing` is registered so the table stays complete.

### 6.1 Wavee Classic = `Classic.Structure` + `Classic.Style`

**Goal**: pixel-identical to production. The refactor's acceptance is a golden-frame comparison of every route, panel mode and stage layout against `main`, light and dark.

**Structure: Own views for every key**, built by moving today's builders behind the contracts:
- the sidebar (with the `SidebarPlanner` and the Classic/Library layouts) and the title row with tabs;
- rail panel, player bar, stage (4 layouts), lyrics (DoF), queue (two skins), detail frame (rail + vertical arm);
- Home zones, Browse tiles, search, library, recents (`PivotTabs`), profile, concerts, settings, setup, release notes, dialogs, menus, toasts.

**Common**: diagnostics, logs.

**Policies**: today's ladders, unchanged:
- bands 700/660/528 + hysteresis;
- pane 180–460 resizable;
- rail 200–500;
- player tiers 440/760/900/1100/1240 −24;
- chrome allocator constants.

**Capabilities**: Tabs, NavTree, FriendsPanel, ForwardButton, HistoryMenu all true; MaxBandPins 0.

**Options**:

```csharp
// structure
sidebar: Select{Classic, Library}=Classic · showCovers: Toggle=false (Classic layout) · libraryDensity: Select{Default, Compact}
rowStyle: Select{Modern, Classic}=Modern (Cards picker with today's PickerModernRow/ClassicRow previews)
hideTrackArt: Toggle=false · marquee: Toggle=true · pageLayout: Select{Automatic, Hero} · railUniform: Toggle
lyricsBlur: Slider(-1 Auto..) · heroPresentation: Select{Cover, Player} (Visibility=Developer)
stageLayout: Select{Card, LargeArt, Centered, Artist} · stageSpectrum · stageHeroLyrics · stageHeroMotion · stageHeroDim
action: clearRememberedSizes
// style
density: Select{Compact, Default, Cozy, Comfortable}=Default · surfaces: Select{Off, Subtle, Rich}
accentFromArtwork: Toggle=true · nowPlayingColors: Toggle=true
```

**State**: pane width/collapse, library list/grid, rail widths per scope, detail rail widths, library column widths, docked video height.

**Style**: the engine's neutral palette (today `Tok.NeutralPalette`), system accent, today's `Design.*` values as the role table, and Mica (today's `MicaAlt=false`).

### 6.2 WinUI = `WinUi.Structure` + `WinUi.Style` (canvas: 39 boards)

**Structure: Own**:
- two-row TabView title bar with an AutoSuggestBox toolbar;
- NavigationView pane (open 280 / compact 48 / minimal);
- content layer with an 8-px top-left radius;
- docked 340 pane (Queue/Lyrics/Now playing/Friends);
- 72 player bar (centred 520 transport, podcast/video/remote variants);
- stage (visualizer + three-line caption + floating transport card) and full-screen lyrics (word wipe, romanization);
- video mini player + placement menu;
- every page board: Home (greeting, facet SelectorBar), Browse, Category (BreadcrumbBar), Search (facet SelectorBar), Album (SplitButton Play, sortable headers), Artist, Discography, Playlist owned/followed, Liked (wall cover + facts bento), Library (grid; list + detail 380/1fr), Recents (heatmap), Profile, Show, Episode, Concert;
- Settings (SettingsCards), Setup, ReleaseNotes, Feedback, Crash.

**Common**: diagnostics, logs, history, not-found, Home customize, the module page (§7 Q14).

**Policies**:
- bands: expanded ≥1100, compact 660–1100, minimal <660 (the "Navigation pane = Automatic" option);
- pane not resizable;
- panel docked 340 inside the content card;
- player tiers: not drawn by the canvas, so the policy is borrowed numerically from Classic until designed (§7 Q8).

**Capabilities**: Tabs = `structure.tabs`, NavTree, FriendsPanel, ForwardButton, HistoryMenu true.

**Options** (W-ThemeOptions):

```csharp
// structure
navigationPane: Select{Expanded, Compact, Automatic}=Automatic · tabs: Toggle=true · artworkInRows: Toggle=true
// style
backdrop: Select{Mica, MicaAlt, Acrylic, None}=Mica · density: Select{Default, Compact}=Default · useWindowsAccent: Toggle=true
```

"Not options" per the canvas: radii, type ramp, icon size, pane width 280, player height 72.

**Style**:
- Fluent 2 tokens (W-Tokens): layers #1c1c1c/#202020/#282828; card white 5%; acrylic flyouts r8; accent #60cdff / #005fb8;
- Segoe UI Variable ramp (Caption 12/16 … Display 68/92);
- radii 4 controls/rows, 8 overlays/cards/content layer;
- 4-px grid: page inset 36, section gap 24;
- rows 40 single / 48 two-line, hover white 6% on a 4-px inset, selected 3×16 pill;
- **plus the Zune-needed roles** (S2): Panorama, Pivot, SubPivot, Numeral, NowPlayingDisplay, `Case(Label) = AsWritten`.

### 6.3 Zune = `Zune.Structure` + `Zune.Style` (canvas: 37 boards)

**Structure: Own**:
- 48 title bar: wordmark, Back only, search pill with a ctrl+f hint, account, notification dot;
- 84/52 pivot band: row 1 pivots + ≤6 pins; row 2 sub-pivots, views, categories or the context row from PageChrome;
- inline full-height panel 340, floating below 820, with queue/lyrics/now playing pivots and no friends;
- 72 play bar with the seek line as its top edge (tiers full ≥1240, medium 760 → ⋯ overflow, narrow);
- stage: flush 112 transport, pivot words, faces default Mosaic; the "up next" full-screen queue page; full-screen line lyrics with translation + resync; full-bleed video; the floating mini player;
- pages:
  - Home: quickplay pins/history/new, daylist with dayparts, zones;
  - Browse: words in row 2, colour tiles; Category: panorama word on a colour field;
  - Search: facets with counts in row 2;
  - Artist: context row 2, panorama name, bleed; Album: panorama, numerals, no art;
  - Playlist owned/followed; Liked (hero 64, date groups); Show; Episode; Concert (big date numeral);
  - Library: three-pane albums, podcasts with day groups; Recents (day groups + heatmap); Profile;
- overlays: track menu with a round-verb strip, toasts ≤3, account menu with the light-theme row, device picker;
- Setup with an accent step column; Release notes (panorama title); Dialogs (feedback with inline preview, crash).

**Common**: diagnostics, logs, history, not-found, customize, module page.

**n/a**: NarrowDrawer, Masthead (crumbs live in the title bar), Panel.Friends (capability false).

**Policies**:
- bands: no nav pane;
- panel inline ≥820, else floating;
- band pins shown when the page column is ≥720 and `structure.bandPins`;
- player tiers 1240/760 (+ narrow).

**Capabilities**: Tabs false, NavTree false, FriendsPanel false, ForwardButton false, HistoryMenu false (§7 Q5), MaxBandPins 6.

**Options**:

```csharp
// structure
bandPins: Toggle=true
// style
accent: Select{#ff3ca6, #ff7a1a, #60cdff, #8fd14f, #b983ff}=#ff3ca6 (Swatches picker) · density: Select{Compact, Default, Comfortable}=Default (rows 44/52/60)
```

**Style** (Z-Tokens):
- grounds #161616/#111111/#1b1b1b/#242424, light #f5f5f5…;
- ink #fff/#a8a8a8/#7a7a7a;
- hover 5% white, selected 12% accent;
- radii 0 (covers 2, focus 4, round metro buttons full);
- type: Panorama 120/130·200, Hero 64/72·300, Title 40, Pivot 28/36 600·350, Heading 26/32·300, Group 22, Numeral 22 (28 on artist)·300, SubPivot 14/20, Body 15/20, Meta 13/18, Micro 11/14, NowPlayingDisplay 150;
- `Case(Label) = Lower`;
- gutter 48, section 32;
- material: opaque (DWM backdrop none);
- control roles: PrimaryAction = 36 round accent disc with a lowercase label beside it; chips = flat rectangles; switch = 40×20 rectangle; search = the one rounded field.

### 6.4 Zune Fluent = `Zune.Structure` + `WinUi.Style` (owner requirement)

**Views**: zero own views. The table is exactly `Zune.Structure.Views`. Built by composition, with no copy of any Zune view and no `if` on identity.

**What the user sees**:
- Zune's pivot band, pins, panorama pages, inline full-height panel, big numerals, flush play bar and up-next page;
- drawn in Segoe UI Variable with sentence-case labels, 4/8-px radii, Mica layers, WinUI accent buttons/sliders/toggles/list backplates and Fluent menus/dialogs;
- the accent is used sparingly because the WinUI style's control roles use it sparingly.

**Role overrides**: allowed but counted, each with a reason; at most a handful (§1.11 limit 1). Proposed starting set, all subject to §7 Q2:
1. `Type.Panorama` = Display 68/92 600 instead of Zune's 120·200. WinUI's own Panorama role already says so, so this is not an override but the style's value.
2. `Space.Gutter` = 36, the WinUI value.
3. `Layer.Panel` = the WinUI content layer #282828 with a 1-px stroke.

**Options**: the union.

```csharp
structure.bandPins (Zune) · style.backdrop · style.density · style.useWindowsAccent (WinUI)
```

- Persisted under `theme.wavee.zunefluent.*`, independent of Zune and WinUI.
- Zune's accent swatches are not offered: accent is a style option, and this theme uses the WinUI style.

**Policies and capabilities**: Zune's, fed WinUI metrics. Rows become 40/48, and the band height is recomputed from WinUI's Pivot/SubPivot line heights. Panel threshold 820 and pins ≥720 are unchanged.

**Proof obligations**:
- S1 analyzer clean on `Zune/Structure/**`;
- S2 role completeness of `WinUi.Style` against `Zune.Structure.RequiredRoles`;
- S3 probe;
- golden frames of the Zune canvases re-rendered under the WinUI style, reviewed by the owner (§7 Q2).

### 6.5 Shared defaults without hidden switches (`Themes/Common/`)

- `Common` is a **library of view blocks**, not a theme. It has no options, no policies and no capabilities, and registers nothing by itself.
- Its blocks read only style roles and props, so they look native in any style. Examples: diagnostics pages, logs page, history, not-found, Home customize, the module-page renderer, `Common.Nothing`.
- A structure opts in by registering a block under a key **explicitly**. The registry test counts `Common` registrations per structure; the count is reported, so drifting toward "everything is common" is visible.
- **Why this is not a switch in disguise**: a block cannot read any theme's options, cannot tell which structure registered it, and gets every look from the style. Its variability can arrive only through props, which the core host builds the same way for every theme.
- **Contrast with the earlier draft**, where WinUI/Zune inherited Classic views: a Classic view under WinUI would still read Classic options (rowStyle, hideTrackArt) and Classic geometry, i.e. Classic's switches leaking into other themes.

---

## 7. Risks and open questions for the owner

### 7.1 Risks (with the mitigation built into the plan)

| # | Risk | Mitigation |
|---|---|---|
| R1 | Size. The view side to move is ~18k sidebar lines, 2.6k `TableHost`, 2.7k lyrics, 1.9k stage, 2k player bar, plus every page. A big-bang refactor would stall. | Phase it: (1) engine E1-E4 + E12; (2) core models and verbs (§3.1) with Classic views calling them, no visual change; (3) the Classic registry, golden frames identical to main; (4) the Zune structure+style; (5) WinUI; (6) Zune Fluent, almost free if S1/S2 held; (7) delete the switches (§4). Each phase ships green. |
| R2 | Classic must stay pixel-identical. | Golden-frame suite per route/panel/stage layout, light and dark, captured from `main` before phase 2. |
| R3 | Row recycling or list perf regresses under templates. | E13 guard, analytic extents, `RowPresentation` equality gate unchanged, bench in VerticalSlice. |
| R4 | `Spacing`/`Radii` const → readonly is a breaking engine API change (const contexts). | One mechanical PR in the engine + app, listed by grep before E3. |
| R5 | Video across a root swap (E7) is new reconciler territory. | Land E7 with its own VerticalSlice suite before any theme switch UI ships. Until then, the theme picker disables "switch now" while video is presenting (it applies on video end). That is a temporary guard, not a switch in components. |
| R6 | PR 205 conflicts: it adds ~70 nav-style branch sites and 11 page reads that this plan deletes, plus useful pieces (PageHead, PlaySplit, Liked, wash split, engine commits). | Decision Q3. |
| R7 | S1 friction: structure authors will want "just 28 px here". | The analyzer points to the role to add. Adding a role is a Minor vocabulary bump and must be filled by every style (S2), which is the intended cost. |
| R8 | Contract churn makes later packages brittle. | §1.9 rules: additions only, Major bumps rare and loud; contract tests per key. |
| R9 | Zune Fluent may look wrong even with a clean composition: Zune's structure was designed around its own type scale. | Role overrides (≤ handful) and an owner review of golden frames (Q2). |

### 7.2 Open questions (real decisions only)

**Q1. Assembly split.**
- Keep one `Wavee.csproj` with folders plus an analyzer/test fence (cheap now), or split `Wavee.Core` / `Wavee.Themes.*` assemblies so the compiler forbids core → theme references.
- Recommendation: fence now, split later; the contracts make the split mechanical.

**Q2. Zune Fluent ambiguities** (design-pressure answers needed before phase 6). Which of these is *structure* (kept from Zune) and which is *style* (taken from WinUI)?
- (a) Lowercase labels. Proposed: style (`Case`), so Zune Fluent is sentence case.
- (b) Panorama titles. Proposed: the edge bleed is structure and the 120/200 size is style, so Zune Fluent gets 68/92 600 bleeding off the edge. Or is size part of the Zune identity the owner wants kept?
- (c) Big track numerals. Proposed: the numeral column is structure; size and weight are style (WinUI Numeral role ~20/28).
- (d) Round metro play buttons vs WinUI accent buttons. Proposed: style (control role).
- (e) Words vs icons in the play bar and panel. Proposed: style role `CommandPresentation` (Q15).
- (f) Opaque ground vs Mica. Proposed: style, so Zune Fluent is Mica.
- (g) Options exposed: proposed bandPins + backdrop + density + useWindowsAccent, and **not** Zune's accent swatches. Should Zune Fluent offer them?
- Does the owner want a short Zune Fluent canvas before phase 6, or is the composed result reviewed from golden frames?

**Q3. PR 205.**
- (A) Merge as-is, then dissolve its nav-style plumbing in phase 7. Simplest history; carries ~70 sites to delete later.
- (B) Before merge, split out its engine commits, PageHead/PlaySplit/Liked/filter/chip/wash-split work and the Zune visuals, and drop `ShellNavStyle`/`PresentedNavStyle`/`HoistedFor`/`InRow2`.
- Recommendation: (B) for the plumbing; its Zune band and bleed *views* become the starting Zune structure.

**Q4. WinUI "Tabs in the title bar = Off".** What happens to existing tabs?
- (a) Hide and keep them, restored when the option is turned back on (proposed).
- (b) Convert pinned tabs to sidebar pins, as the canvas note says (a data migration triggered by a presentation option).

**Q5. Zune capabilities.**
- No Forward button and no tabs are drawn. Is the back-history right-click menu (today's `NavHistoryButton`) also gone?
- No Friends panel tab is drawn: is the friends feed unavailable in Zune, or reachable elsewhere (e.g. a panel pivot)?

**Q6. Per-theme view state.**
- Pane, rail and library column widths and the panel's docked-video height are stored per theme (proposed), so switching back restores each theme's sizes.
- Alternative: share one value where both themes allow resizing.

**Q7. Content vs theme classification.**
- Proposed as **theme-independent user prefs**: visualizer face, deck preset + options, Liked cover treatment, daylist clock, tempo/plays columns, show remaining time.
- Proposed as **Classic-only options**: marquee, lyrics blur, hide track artwork, stage layouts, NPV deck-in-hero.
- Confirm. In particular: should the 19 visualizer faces and 12 deck presets be available in every theme? Proposed yes ("never disable requested features").

**Q8. Undesigned states.**
- The WinUI canvas draws no player-bar width tiers and no narrow window. Zune draws full/medium/narrow.
- Proposed: WinUI borrows Classic's tier thresholds and drop order until designed. Alternatively, design them first.

**Q9. Recovery dialog.**
- The Zune canvas draws a themed startup-recovery dialog, but recovery is deliberately engine-free and runs before settings exist (`Recovery.Win32.cs`).
- Proposed: keep it OS-native in all themes. A themed version needs the engine and the theme before the crash-safe path.

**Q10. Switch feel.**
- Theme switch = 250 ms cross-fade (proposed) or snap.
- App-mode (light/dark) flip: cross-fade as today, or snap.

**Q11. High contrast.** Out of scope for the four themes now (E15 reserves the shape). Confirm.

**Q12. Migration.**
- One-time latch `theme.bootstrap.version`, which maps:
  - `sidebar.layout.id = 2` (PR 205 Zune) → `theme.id = wavee.zune`;
  - every other user → Classic with their old values copied into Classic options.
- The latch is removed one release later. Confirm that one release is enough.

**Q13. Settings › Appearance.**
- Proposed: Theme (picker + generated options of the active theme) · App mode · Zoom. Every other current row moves into a theme schema or to its feature's tab (stage content prefs under Full screen, lyrics under Lyrics).
- The "Browse community themes" tile from W-Settings is hidden until the loader exists. Confirm both.

**Q14. Common blocks in WinUI/Zune.**
- Diagnostics, logs, history, not-found, Home customize and the module page use `Common` blocks (style-native, not redesigned).
- Acceptable, or should any get a WinUI/Zune design first (the module page is user-visible for Radio/Twitch/YouTube)?

**Q15. Word vs icon commands.**
- Zune's bar and panel use lowercase words ("lyrics", "queue", "devices") where WinUI uses icons.
- Is that a style choice (Zune Fluent shows icons; proposed) or Zune structure (Zune Fluent keeps words, in sentence case)?

**Q16. Stage layouts per structure.**
- Classic keeps its 4.
- The WinUI canvas shows Visualizer-first (Card-like) and Large-cover lyrics.
- Zune shows face-first with flush transport plus the up-next page.
- Confirm that WinUI and Zune get only what their canvases show, with Centered/Artist hero layouts Classic-only, rather than porting all four.
