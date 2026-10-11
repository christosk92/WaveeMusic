# External research: structure-changing themes over a shared core

Read on 2026-10-10 from the cited sources. Where a statement is my inference rather than a sourced fact it is marked
*(inference)*. Longer per-lane notes with more verbatim quotes: `ext\kodi-winamp-foobar.md`, `ext\media-os-skins.md`,
`ext\style-contracts-a.md` (raw downloads under `ext\dl\` and `scratchpad\dl\`).

Scope as updated by the coordinator: built-in themes (Classic, WinUI, Zune) must be fully self-contained — own pages,
player, navigation, options — behind contracts that keep core/component code theme-agnostic, such that a runtime loader
for community themes can be added later without redesign. Depth therefore goes to: the core↔theme contract (§1),
theme-owned pages/views (§2), theme-defined options (§3), zero-flicker switching (§4). Per-system reference is §5;
the runtime-loading comparison is compact (§6); candidate architectures are §7.

Target recap: Wavee on FluentGpu — NativeAOT .NET 10, D3D12 (HLSL compiled at runtime via `D3DCompile` to DXBC sm5.1
with a content-hashed cache, `fluent-gpu/src/FluentGpu.Windows/D3D12/ShaderCompiler.cs:14-16`), a retained declarative
`Element` tree built by source-generated C# components, 60–120 fps.

---

## 1. The core↔theme contract: shapes that survive, shapes that die

Across ~30 systems the contract between a theme-agnostic core and a theme that owns presentation takes five shapes.
They are ordered from "theme gets the most structural freedom" to "theme gets paint only".

### 1.1 Configuration → body (SwiftUI style protocols)

A style is a type with one method, `makeBody(configuration:)`; the control keeps behaviour and hands the style a
*configuration* value containing type-erased content slots, state, intent and (optionally) the action
(`ext\style-contracts-a.md §1`, Apple doc JSON endpoints):

```swift
struct ButtonStyleConfiguration          { let label: Label; let isPressed: Bool; let role: ButtonRole? }
struct PrimitiveButtonStyleConfiguration { let label: Label; let role: ButtonRole?; func trigger() }   // theme owns the gesture
struct ToggleStyleConfiguration          { let label: Label; @Binding var isOn: Bool; let isMixed: Bool }
struct DisclosureGroupStyleConfiguration { let label: Label; let content: Content; @Binding var isExpanded: Bool }
struct NavigationSplitViewStyle / GroupBoxStyle / ProgressViewStyle / LabelStyle / MenuStyle / GaugeStyle … // open
ListStyle / PickerStyle / TextFieldStyle / TabViewStyle                                                    // closed
```

"The system calls this method for each Button instance in a view hierarchy where this style is the current button
style." Styles are environment values: `.buttonStyle(_:)` "Sets the style for buttons within this view", overridable
nearer the leaf, reset with `.automatic`. `ToggleStyleConfiguration.isOn` is a `Binding` so the theme can "Write this
value when the user takes an action that's meant to change the state of the toggle". Apple keeps the behaviour-heavy
containers closed (`ListStyle`, `TextFieldStyle`, `PickerStyle`, `TabViewStyle` expose no `makeBody`) and opens
"slots + simple state" containers — including structural ones (`NavigationSplitViewStyle`, `GroupBoxStyle`,
`DisclosureGroupStyle`), so a theme can re-arrange a container's parts (`VStack { icon; title }` vs `HStack`).
Composition semantics are undocumented and stack-based (styles push/pop; built-ins do not cascade to nested views of the
same type; `Button(configuration)` lets a style wrap the next one) — Moving Parts, `ext\style-contracts-a.md §1.4`.
The library-author recipe is protocol + configuration struct + `EnvironmentKey` + `AnyView` erasure (§1.5 there).

What the theme never gets: the model. What it always gets: slots (already-built content), state booleans/bindings,
intent (`role`), and for `Primitive*` the action. This is the richest *typed* contract in the survey and maps 1:1 to
C#: `record XConfiguration(...)` + `interface IXStyle { Element Build(XConfiguration c); }` + a scoped provider.

### 1.2 Named parts + declared states + per-type factory table (WPF/WinUI, Avalonia, Qt Quick Controls 2)

XAML: "When you change the visual tree of a control, you must replace the entire ControlTemplate. There's no way to
replace only part of the visual tree"; the published contract is `[TemplatePart(Name, Type)]` ("usually required in the
template and have a specific predefined name") + `[TemplateVisualState(Name, GroupName)]`, both documentation only —
nothing enforces them; WinUI's `NavigationView` is ~40 string part constants (`c_togglePaneButtonName = L"TogglePaneButton"sv`
…) null-checked in `OnApplyTemplate` and 37 `VisualState`s in groups `DisplayModeGroup`, `TogglePaneGroup`, `PaneStateGroup`
… (`microsoft-ui-xaml/controls/dev/NavigationView/NavigationView.cpp:33-102, :392`, `NavigationView.xaml`). Layout
seams are first-class objects: `ItemsPanelTemplate`, `ItemsRepeater.Layout` (`StackLayout`/`UniformGridLayout`/custom
`VirtualizingLayout`; "You can think of ItemsRepeater conceptually as a data-driven panel"), `DataTemplateSelector :
IElementFactory` (participates in recycling). Lightweight styling is a *suffix-structured token vocabulary*
(`ButtonBackground`, `ButtonBackgroundPointerOver`, `ButtonForegroundPressed`, `ButtonBorderBrushDisabled`) overridable
per scope without re-templating (`ext\style-contracts-a.md §2`).

Avalonia 11: `ControlTheme : StyleBase` "Defines a switchable theme for a control", matched by
`TargetType.IsAssignableFrom(target.StyleKey)` (source: `ControlTheme.TryAttach`), looked up via `TryFindResource(StyleKey)`;
`StyleKeyOverride` lets a subclass keep its parent's theme; `BasedOn` inherits; the control publishes
`[TemplatePart("PART_Popup", typeof(Popup), IsRequired = true)]` and `[PseudoClasses(":pressed", ":flyout-open")]`, sets
`PseudoClasses.Set(":pressed", IsPressed)` itself, and the theme reacts through nested selectors
(`^:pointerover /template/ ContentPresenter#PART_ContentPresenter`). Fluent and Simple are two complete per-control
theme tables with *different token vocabularies* (`ButtonBackgroundPointerOver` vs `ThemeControlMidBrush`) over the same
controls (`ext\style-contracts-a.md §3`).

Qt Quick Controls 2: "a style is essentially a set of QML files within a single directory"; each `Button.qml` must have
the C++ behaviour type `T.Button` (from `QtQuick.Templates`) as root and supply the `contentItem`/`background`
delegates; the C++ side "never draws" and exposes `pressed/down/checked/hovered/visualFocus/mirrored/display/icon/
palette`; a `qmldir` + `import QtQuick.Controls.Basic auto` gives a fallback chain so a style can ship three controls
and inherit the rest (`ext\style-contracts-a.md §4`, `qtdeclarative/src/quickcontrols/basic/Button.qml`).

Common denominator: **theme = per-control-type factory table with a fallback chain**, resolved by "look up the current
scope's factory for this type". That lookup is how all three avoid `if (theme == X)`: the control never knows a theme
exists. Weakness shared by all three: the part/state contract is stringly typed and unenforced (`PART_*`, `:pressed`,
`"TogglePaneButton"`), which is why WinUI needs ~40 null checks and Avalonia's `IsRequired` is advisory.

### 1.3 Providers + one-way bindings + verbs (WMP, AIMP, MusicBee Theater Mode)

Windows Media Player (`ext\media-os-skins.md §3`) is the best-documented example: the skin sees three global objects
(`player`, `theme`, `view`); element attributes bind one-way to core state with `wmpprop:` ("the first value will be
automatically updated to reflect the second value each time the second value changes", e.g.
`<SLIDER value="wmpprop:player.Controls.currentPosition"/>`) and to *capabilities* with
`enabled="wmpenabled:player.controls.play"`; **predefined elements** wire verbs with zero script (`PLAYELEMENT`
"A BUTTONELEMENT with a built in onclick event handler that calls player.controls.play", `SEEKSLIDER`, `VOLUMESLIDER`,
`SHUFFLEBUTTON`, `TRACKNAMETEXT`, `CURRENTPOSITIONTEXT`, `ITEMSPLAYLIST`, `WMPVIDEO`…); engine animation primitives
(`moveSizeTo`, `alphaBlendTo`, `slideTo`) replace scripted loops; views are per media category (`VIEW category="Music|Video|CD…"`)
each with its own scope and `onload`. The cliff: "If you do not supply a video element and the content contains video,
Windows Media Player will return to full mode and your skin will not be displayed."

AIMP (`ext\media-os-skins.md §2`): skin controls (`TASEPlaylist`, `TASESeekBar`, `TASEPageSelector` "bound to a Tab Control
access point", `TASELayoutDockSite`…) bind to core **data providers** (`prvQueue`, `prvPlaybackQueue`, `prvEqualizer`,
`prvPlaylistFileInfo`…) through named "access points"; "Scripts and binding expressions are compiled before export";
the skin manifest declares "which skin engine version a skin was developed for".

MusicBee Theater Mode (`ext\media-os-skins.md §1`): element `type` is the core vocabulary (`AlbumCover`, `Field id="Title"`,
`NowPlayingList`, `ProgressBar`, `Lyrics`…), `onClick` names core **verbs** (`PlayPause`, `ShuffleToggle`,
`OpenAutoHidePanel:#PanelQueue`) and `visibleTrigger` names core **boolean states** (`Playing`, `ShuffleOn`, `RepeatOff`);
layout is anchor-to-named-sibling (`xAnchor="#Cover.Left"`, `yAnchor="#Cover.Bottom"`, `widthDock="Panel.Height.66"`).
The skin never calls code.

Common denominator: **the core publishes observable state, capabilities and verbs under stable names; the theme binds
and arranges.** This is the right shape for Wavee's *player* and *now-playing* surfaces: `Playback.Position`,
`Playback.CanSkipNext`, verb `Playback.TogglePlay`, etc., are the contract, not the player bar's internals.

### 1.4 Window ids + control ids + info labels + skin settings (Kodi)

Kodi is the one system in this survey where a theme defines *every* window and remains (mostly) robust, so its contract
is worth stating precisely (`ext\kodi-winamp-foobar.md §1`, sources `xbmc/xbmc` master + Kodi wiki via MediaWiki API).

**Integer ids, not types.** `xbmc/guilib/WindowIDs.h`: `WINDOW_HOME 10000`, `WINDOW_VIDEO_NAV 10025`,
`WINDOW_SKIN_SETTINGS 10035`, `WINDOW_MUSIC_NAV 10502`, dialogs 10100+, "11100 to 11199 reserved for Skins".
`CGUIWindowManager::CreateWindows` instantiates a fixed set of C++ window classes, each naming its XML file
(`CGUIWindowHome(): CGUIWindow(WINDOW_HOME, "Home.xml")`); `SkinSettings.xml` is `new CGUIWindow(WINDOW_SKIN_SETTINGS,
"SkinSettings.xml")` — a plain window with no C++ behind it. Per window the core binds only a handful of control ids
(`CGUIMediaWindow`: `CONTROL_BTNVIEWASICONS 2`, `CONTROL_BTNSORTBY 3`, `CONTROL_BTNSORTASC 4`, `CONTROL_BTN_FILTER 19`,
`CONTROL_LABELFILES 12`; `GUIMediaWindow.cpp:77-82`); the list containers are **whatever the skin lists** in
`<views>504,50,51,52,53,54,55,500,501,502</views>` — `CGUIMediaWindow::Load` adds every listed control that
`IsContainer()` to `m_viewControl`, and `CGUIViewControl::SetCurrentView` picks the one whose `viewtype` matches the
saved view mode, shows exactly one, and binds the `CFileItemList` with `GUI_MSG_LABEL_BIND`. Everything else is
messages (`GUI_MSG_CLICKED` with the control id) or built-in functions (`ActivateWindow`, `SetFocus`, `Skin.*`), so a
button with `<onclick>ActivateWindow(videoplaylist)</onclick>` needs no id at all. "Every `if (theme == X)` in Kodi is
really `GetControl(id) != nullptr` or a message that nobody answers."

**Includes, expressions, variables.** `CGUIIncludes` resolves, in order, `<default type="button">` (appended to every
control of that type), `<constant>`, `<expression name>` (flattened at load with circular-reference detection,
substituted for `$EXP[]`), `<include name="X"><param name="left" default="120"/><definition>…</definition></include>`
with `$PARAM[]`, parameter forwarding and `<nested/>`, and `<variable><value condition="Player.Paused">…</value><value>…
</value></variable>` (first match wins; Estuary has 177) — all into the window's XML tree before `CGUIControlFactory`
creates controls (`GUIIncludes.cpp:303-320`).

**Conditions as a shared engine.** Every `<visible>`, `<enable>`, `<animation condition>`, `<onclick condition>` string
goes through `CGUIInfoManager::Register(expression, context)`: deduplicated by (context, string), shunting-yard parsed
into an AND/OR tree with NOTs pushed to leaves, leaves are integer ids from `TranslateSingleString`
(`Skin.HasSetting(x)` → `SKIN_BOOL`, `Skin.String(x)` → `SKIN_STRING`, `GUIInfoManager.cpp:11551-11584`); `Evaluate`
moves the short-circuiting child to the front "so we evaluate faster next time"; each `InfoBool` caches per frame via a
refresh counter reset at the end of `CApplication::Render`, so a condition used by 30 controls costs one evaluation per
frame (`InfoBool.h:36-46`, `Application.cpp:957-962`).

**Structure follows settings without a reload.** `CGUIWindow::Prepare()` copies the parsed root and runs
`skin->ResolveIncludes(copy, &m_xmlIncludeConditions)`; `NeedLoad()` = `!m_windowLoaded ||
infoMgr.ConditionsChangedValues(m_xmlIncludeConditions)`, so when `Skin.HasSetting(touchmode)` flips, the next window
init rebuilds that window's control tree (`GUIWindow.cpp:71-174, 762`). Estuary uses this for structure:
`<include content="WidgetListPoster" condition="Library.HasContent(movies) + !Skin.HasSetting(home_no_movies_inprogress_widget)">`
(`Home.xml:54-61`), `<include condition="!Skin.HasSetting(hide_mediaflags)">MediaFlags</include>` (`MyVideoNav.xml:94`).

**Fallback and gating.** The only hard requirement is `Home.xml` (`LoadSkin` fails → `ReloadSkin` resets
`lookandfeel.skin` to Estuary + toast). There is **no per-window fallback**: a missing window XML logs "Unable to load
window XML" and the window activates empty (`GUIWindow.cpp:137`). Skins import `<import addon="xbmc.gui"
version="5.18.0"/>`; the installed `xbmc.gui` carries `<backwards-compatibility abi="5.17.0"/>` and `CAddonMgr::IsCompatible`
accepts anything in [abi, version] — bumps per release (Jarvis 5.10 → Krypton 5.12 → Leia 5.14 → Matrix 5.15/abi 5.14 →
Nexus 5.16/abi 5.15) orphan every third-party skin until its author updates (`addons/xbmc.gui/addon.xml`,
`AddonManager.cpp:1107-1127`). Custom windows (`custom*.xml`, id + 10000) may use "only controls that do not require
specific source code to operate".

**Lessons.** Copy: ids/roles not types; skin-declared views (core needs "a container"); one expression engine shared by
core and skin with settings as leaves, parsed once, deduplicated, memoised per frame; includes with params/defaults/
nested and per-type defaults; re-resolve a subtree only when a recorded condition changed. Avoid: no per-window
fallback (Wavee must resolve page → base theme page → engine default); API bumps that orphan themes; per-frame
evaluation of every control's condition; the skin-helper-service era (XML has no logic, so skins depend on Python
service add-ons to compute properties — give themes a sanctioned typed binding layer instead).

### 1.5 Headless state vocabulary + parts + contexts (React Aria, Radix, Headless UI, Zag)

React Aria's split is three layers: `react-stately` state hooks, `react-aria` behaviour hooks that "return DOM props"
(handlers, ARIA, ids), and your rendering — "You then render the elements yourself and spread those props onto them,
which gives you full control over the DOM structure"; "every React Aria Component is built on these hooks"
(https://react-aria.adobe.com/hooks). The styling contract (https://react-aria.adobe.com/styling): "React Aria does not
include any styles by default"; `className`/`style`/`children` may be functions of state
(`className={({isSelected}) => …}`); states are also data attributes that "work consistently across mouse, touch, and
keyboard" — interaction `data-hovered, data-pressed, data-focused, data-focus-visible, data-focus-within, data-active`;
selection/state `data-selected, data-disabled, data-invalid, data-required, data-readonly, data-indeterminate`;
animation `data-entering, data-exiting`; non-boolean `data-orientation, data-placement, data-selection-mode,
data-sort-direction`; repeated parts addressed with `slot` (`<Button slot="increment">`); some CSS variables
(`--trigger-width`). Composition (https://react-aria.adobe.com/customization): every component exports a context
(`ButtonContext`…), `slots` give per-slot props (`DEFAULT_SLOT` for unslotted children), `Provider values={[[Ctx, v]…]}`
stacks several, `useContextProps` merges local props/ref with context so a custom part "work[s] inside any React Aria
component" — you can "replace one part of a component with your own custom implementation without rebuilding the whole
pattern from scratch."

Radix: "Radix Primitives are unstyled" (even functional styles are the consumer's), state via `data-state="open"`;
`asChild` "will not render a default DOM element, instead cloning the part's child" — the consumer supplies the element,
the primitive supplies behaviour, nestable without limit (https://www.radix-ui.com/primitives/docs/guides/composition,
…/styling). Headless UI: data attributes per part (`MenuButton`: `data-open, data-focus, data-hover, data-active,
data-autofocus`), render props, `as={Fragment}` to render children directly, and layout-affecting behaviour kept in
the core and parameterised (`anchor="bottom start"`, `--anchor-gap`) (https://headlessui.com/react/menu). Zag: "framework
agnostic toolkit … powered by state machines", "The machine APIs are completely unstyled" (https://zagjs.com/overview/introduction).

Common denominator: **a published state vocabulary per component + named parts + a way for the theme to supply the
element for a part.** Compose implements the same split as *library layering*: foundation is "design system agnostic
building blocks … like `Row` and `Column`, `LazyColumn`, recognition of particular gestures"; material is "a theming
system, styled components, ripple indications"; "each layer builds on public APIs of the lower layers … enable you to
replace any layer should you need to"; `BespokeButton` is built with "No Material components used"
(https://developer.android.com/develop/ui/compose/layering).

### 1.6 Capability probing and hints (KDE Plasma)

A global theme is "a bundle of pointers to other theme packages" (`contents/defaults` INI: colour scheme, icons, plasma
theme, cursors, decoration) plus an optional `contents/layouts/<shell>-layout.js`; the host *probes* what the package
provides — `contents.setFlag(DesktopLayout, !pkg.filePath("layouts").isEmpty()); … setFlag(Colors, configProvides(conf,
"kdeglobals/General", "ColorScheme") …)` (`libklookandfeel/klookandfeelmanager.cpp:67-105`) — and the KCM deliberately
excludes layout from the default apply set (`resetContents &= ~LayoutSettings`, `kcm.cpp:533-541`; CLI needs
`--resetLayout`). Plasma SVG themes declare behavioural preferences by *presence of named elements* that the core queries:
`frame->stretchBorders = q->hasElement("hint-stretch-borders")`, `hint-top-margin`, `hint-compose-over-border`
(`ksvg/src/ksvg/framesvg.cpp`); "All SVG images are optional. If a theme is missing an SVG file, it will fall back to the
default Breeze theme." KDE's own verdict on bundling live QML with defaults: "Having a frozen API for core plasma parts
has been problematic … It's a lot of pain points for something really not well documented, and therefore unused"
(plasma-desktop issue #85) (`ext\media-os-skins.md §4`).

Lesson: capability flags derived from the package (not self-declared), user-selectable apply subsets, `hint-*` style
optional declarations with core fallbacks; and *do not* freeze an imperative scripting API over core internals.

### 1.7 What dies: contracts made of host-internal names

- Steam VGUI skins bound to **C++ control names** per dialog (`place { control="AvatarImage" x=6 y=8 … }`,
  `friendpanel.layout`); when Steam moved to CEF (beta 2023-04-27, all users 2023-06-15) "Metro is now dead" — 100 %
  of skins died in one update; the successors (Millennium/SFP) inject CSS/JS by regex on window title/URL/body class and
  break whenever generated class names change (`ext\media-os-skins.md §5`).
- Spicetify: the 15-key `color.ini` → `--spice-*` variables survive updates; `user.css` against Spotify's obfuscated
  classes needs a vendor-maintained `css-map` updated per build ("css-map: Add classes for Spotify 1.2.86 (#3757)") and
  "After a Spotify update, you'll need to re-apply Spicetify" (https://spicetify.app/docs/advanced-usage/installation,
  https://raw.githubusercontent.com/spicetify/cli/main/Themes/SpicetifyDefault/color.ini).
- BetterDiscord themes (`.theme.css` + `@name/@author/@version/@description` meta) "can break when Discord updates its
  interface" (mirror of docs: https://www.mintlify.com/betterdiscord/betterdiscord/quickstart).
- Firefox `userChrome.css` "can override nearly every built-in style rule" but targets selectors that "are not well
  documented", applies only "at your next startup", and recipes were redone for Firefox 89
  (https://www.userchrome.org/what-is-userchrome-css.html); the static WebExtension `theme` key (≈40 colour/image/property
  keys) is robust precisely because it cannot touch structure
  (https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/theme).

### 1.8 Contract rules distilled for Wavee

1. **Typed configuration per surface and per control** (§1.1): slots (already-built `Element`s), state (booleans/enums
   from a published vocabulary, §1.5), intent, verbs/actions, and bindings the theme may write (`IsOn`-style). Generate it:
   WinUI's 40 strings + 37 states show what hand-maintained contracts become; a source generator turning
   part/state names into enums makes a missing part a compile error, not a null check (`ext\style-contracts-a.md §5.10`).
2. **Open vs closed** (§1.1): keep scrolling, text editing, virtualization, playback transport *behaviour* closed; open
   their *composition* (parts + state + layout object). SwiftUI opens `NavigationSplitViewStyle` and `GroupBoxStyle`
   while closing `ListStyle` — the same line Wavee should draw: a theme arranges the shell/pages/player from parts, it
   does not reimplement list virtualization.
3. **Per-type factory table + fallback chain** (§1.2): resolution is "current scope → factory for this type/surface",
   with a base theme supplying defaults so a theme may be partial. `StyleKeyOverride`/`BasedOn` semantics are worth
   copying verbatim. Fallback must be **per surface**: Kodi falls back only per skin (a missing window is blank),
   foobar2000 refuses a whole layout when one panel is missing (§1.4, §5.2); Wavee resolves surface → base theme → engine
   default.
4. **Observable state, capabilities and verbs under stable names** (§1.3) for the player/now-playing: `wmpprop:`-style
   one-way binding and `wmpenabled:`-style capability binding, predefined verb elements so most themes need no logic.
5. **Nothing from internals.** No theme reads an `Element` the core built, no theme matches by component type name or
   key string (§1.7). The vocabulary is the API; version it (AIMP engine version, Kodi `xbmc.gui`, VS Code `engines.vscode`).
6. **Capabilities are derived, not declared** (§1.6): the loader (later) infers what a package touches from its content;
   the settings UI lets the user apply subsets (colours vs layout).

---

## 2. Theme-owned pages, player and navigation (structure, not paint)

### 2.1 Surfaces as the unit a theme owns

- Kodi: one XML per window (`Home.xml`, `MyVideoNav.xml`, `MyMusicNav.xml`, `DialogSettings.xml`, `SkinSettings.xml`…),
  window header `<onload>`, `<defaultcontrol always="">`, `<menucontrol>`, `<views>`, `<animation>`, `<zorder>`, then
  `<controls>`; the skin decides how many list views exist and how each looks (`<include>View_50_List</include>` …
  `View_504_MediaList`); `Startup.xml` picks the first window (`ReplaceWindow($INFO[System.StartupWindow])`); custom
  windows/dialogs (`custom*.xml`) add surfaces the core never heard of; Home is `KEEP_IN_MEMORY`, most windows
  `LOAD_EVERY_TIME` (parsed DOM kept, controls rebuilt per activation) (`ext\kodi-winamp-foobar.md §1.1-1.2`).
- Winamp Modern (Wasabi): a **container** is a top-level window owning several **layouts** (one visible: `normal` vs
  `shade`, `action="SWITCH" param="shade"`); layouts are built from `<groupdef>` templates with `<sendparams>` overrides
  and `relatx/relaty` anchoring; the core's components (playlist `pl`, library `ml`, `video`, `vis`, config) are
  embedded by GUID into theme-owned windows (`<container component="guid:{…}">`); the only mandatory id is `main`
  (`WACUP/Winamp-Modern-Skin xml/player.xml`, `ext\kodi-winamp-foobar.md §2.2`). Classic skins are fixed-coordinate
  bitmaps: unbreakable, no structure.
- foobar2000: the UI is a service (`user_interface`: `init/shutdown/activate/hide/get_guid/show_now_playing`, nine
  methods) selected by GUID; inside it, panels are `ui_element`s with a **subclass role GUID** (`playlist_renderers`,
  `media_library_viewers`, `containers`, `playback_information`, `playback_visualisation`…) and an opaque config blob;
  the layout is a nested config tree edited live and exported as `.fth`/`.fcl` ("For sharing" vs "For this computer";
  missing panels are listed and the layout applied only if complete) (`ext\kodi-winamp-foobar.md §3`).
- WMP: `THEME > VIEW(category) > SUBVIEW/elements`; "Each THEME must have at least one VIEW"; "You may want to have more
  than one view … a large view for working with playlists, a medium view for watching visualizations, and a tiny view
  that fits in a corner"; `SUBVIEW` "can be used for parts of your skin that you want to move around, hide, or show …
  a sliding tray"; each view has its own variable scope and `onload` (`ext\media-os-skins.md §3`).
- MusicBee Theater Mode: one XML per surface (`TheaterMode.Embeded` for the Now Playing tab / Compact view,
  `TheaterMode.List` for full screen); the file is the whole now-playing layout; `<autoHide allowPinning>` is the one
  structural container (`ext\media-os-skins.md §1`).
- SwiftUI: container styles (`NavigationSplitViewStyle`, `GroupBoxStyle`, `DisclosureGroupStyle`, `MenuStyle`) are
  open — the theme lays out the container's parts; `ListStyle`/`TabViewStyle` are closed (§1.1).
- Compose: a design system is a library over foundation; pages are composables the design system owns; `MaterialTheme`
  is "just a function" providing locals (https://developer.android.com/develop/ui/compose/designsystems/custom).
- Avalonia: whole `ControlTemplate` per control type; `ItemsControl.ItemsPanel` ("To arrange items horizontally instead
  of vertically, replace the default ItemsPanel"); `IDataTemplate.Match(object)/Build(object)` selects the item view
  by data (`ext\style-contracts-a.md §3.3`).
- WinUI: `ItemsRepeater.Layout` as a swappable layout object; `DataTemplateSelector : IElementFactory` with recycling;
  "A GridView that shows 20 items on screen using this data template creates at least 200 elements (20*10=200)" — the
  per-item element count is the cost a theme controls (`ext\style-contracts-a.md §2.3`).
- Qt Quick Controls 2: a style directory supplies the whole delegate tree per control; `Material` adds
  `layer.effect: RoundedElevationEffect` + `Ripple` per button — structure and effects are the style's
  (`ext\style-contracts-a.md §4.1`).
- KDE: `contents/layouts/org.kde.plasma.desktop-layout.js` constructs the panel (`panel.addWidget("org.kde.plasma.kickoff")
  …`) against a frozen scripting API; application is destructive ("get rid of old config" → `unload()` → `load`)
  (`ext\media-os-skins.md §4c`).

### 2.2 Layout seams that a theme can replace without owning behaviour

| Seam | WinUI | Avalonia | SwiftUI | Qt QC2 | WMP/MusicBee |
|---|---|---|---|---|---|
| Item arrangement | `ItemsRepeater.Layout`, `ItemsPanelTemplate` | `ItemsPanel` | `ListStyle` (closed, fixed set) | per-control delegate | absolute/anchored |
| Item view by data | `DataTemplateSelector`/`IElementFactory` | `IDataTemplate.Match/Build` | `ForEach` in theme's body | `delegate:` | `ITEMSPLAYLIST` child ids |
| Container parts | `[TemplatePart]` + VSM | `PART_*` + pseudo-classes | configuration slots | `contentItem/background/indicator/popup` | `type`/`id` + anchors |
| Effects | `layer`/composition brushes | `Transitions` in theme | modifiers | `layer.effect`, `Ripple` | `hueShift`, `alphaBlendTo` |

Lesson: give lists/grids a **layout object** (stack, grid, wrap, panorama) and an **item factory** the theme supplies,
with recycling owned by the core; give composites (shell, nav, page header, player bar) **typed part slots**. That is
enough for Classic vs WinUI vs Zune to differ in structure while the core owns virtualization, input, focus and UIA.

### 2.3 Data-only themes stop at paint and metrics (what not to expect from tokens)

Flutter `ThemeData`/`ThemeExtension<T>` (fields + `copyWith` + `lerp`), Iced `Catalog` (`(theme, Status) -> Style{background,
text_color, border, shadow, snap}`, resolved per draw; `Status ∈ {Active, Hovered, Pressed, Disabled}`,
https://raw.githubusercontent.com/iced-rs/iced/master/widget/src/button.rs), egui `Style{text_styles, spacing, interaction,
visuals, animation_time…}` (https://docs.rs/egui/latest/egui/style/struct.Style.html), Dear ImGui `ImGuiStyle` "Runtime
data for styling/colors" + `PushStyleColor/PushStyleVar`, Unreal Slate style sets (`ToolBarButton.Normal/.Pressed/.Hovered`
brushes; Common UI "style data assets that separate styling information from UI elements"), Godot `Theme` items
(Color/Constant/Font/FontSize/Icon/StyleBox, type variations, lookup "local overrides → control theme → parents →
project theme → default"; containers "use theme constants for defining separation between their children",
https://docs.godotengine.org/en/stable/tutorials/ui/gui_skinning.html). All change paint and metrics; none change
structure — layout is code in every one of them. Two ideas transfer: Godot's **type variations** (a named variant of a
control type a theme styles separately — Wavee's row/card variants) and Flutter's **interpolable theme** (§4.5).

### 2.4 Wavee's own precedent runs the other way (modules)

`ModulePageDoc` is "never code and never markup: … a small declarative document (a hero, some actions, a list of typed
sections) that the app draws with its own controls" (`WaveeMusic/src/apps/Wavee.Sdk/ModulePage.cs:8-11`); `Template ∈
{entity, custom, watch}` with fallback, `PageSection.Kind ∈ {text, facts, playables, cards, links}` where "a kind the
app does not know is skipped", `[JsonExtensionData] Extra` preserved, hard budgets rejected on both sides
(`MaxSections=40, MaxItems=500, MaxSectionBytes=64K, MaxDocBytes=2M`, `:250-312`); the host draws it in
`Wavee/Platform/Modules.UI.cs:255-468`. Modules are out-of-process JSON-RPC over stdio in a kill-on-close job
(`Modules.Host.cs:10, :72`) with a manifest (`SchemaVersion`, `ProtocolVersion`, `Publisher`, `Entry` "never escapes",
`Capabilities`; `Wavee.Sdk/ModuleManifest.cs`), treated as "untrusted input" (`:214`). A theme is the inverse — app-owned
content, theme-owned presentation — so it cannot be out-of-process on the frame path; but the manifest/versioning/
discovery/"unknown is skipped, over-budget is rejected" posture is exactly what a later theme loader reuses, and the
module page renderer is itself a *surface a theme should own* (Classic/WinUI/Zune should draw a module page differently).

---

## 3. Theme-defined options

### 3.1 Obsidian Style Settings — options declared by the theme, UI generated by the host

A CSS file declares `/* @settings … */` with YAML `name`, `id`, `settings:`; types: `heading` (`level`, `collapsed`),
`info-text`, `class-toggle` (adds the setting id as a `body` class; `addCommand: true`), `class-select`
(`options`, `allowEmpty`), `variable-text` (`quotes`), `variable-number` (`format` unit), `variable-number-slider`
(`min/max/step`), `variable-select`, `variable-color` (`format: hex|rgb|hsl|…-split`, `opacity`, `alt-format`),
`variable-themed-color` (`default-light`/`default-dark`, scoped to `body.theme-light/.theme-dark`), `color-gradient`
(`from/to/step`); outputs are CSS custom properties named after the setting id (`--accent: #007AFF;`) or body classes;
translated titles via `title.<lang>` (https://raw.githubusercontent.com/mgmeyers/obsidian-style-settings/master/README.md).

```css
/* @settings
name: Your Section Name Here
id: a-unique-id
settings:
    - { id: my-title, title: My Settings, type: heading, level: 3 }
    - { id: accent, title: Accent Color, type: variable-color, format: hsl-split, default: '#007AFF' }
    - { id: text, title: UI font, description: Font used for the user interface, type: variable-text, default: -apple-system, … }
*/
```

### 3.2 Millennium (Steam) `Conditions` + `RootColors` — options with migration semantics and scoping

Each setting: `description`, `tab`, `default`, `values{ <value>: { TargetCss: { affects: [regex…], src }, TargetJs: {…} } }`;
"If the values are only `yes` and `no`, it renders a checkbox. Otherwise it renders a dropdown"; evolution rules:
"Renaming a setting resets all users to its default. Removing a dropdown option reverts users who had it selected to the
default. Removing an entire setting is safe." `RootColors` is one `:root {}` of `--vars` with `/* @name … @description … */`
comments, user-editable under Settings → Themes (https://docs.steambrew.app/themes/intermediate/user, …/colors;
`ext\media-os-skins.md §5`). `affects` scopes an option to particular windows/pages.

### 3.3 Kodi skin settings

Storage: two maps `m_bools`/`m_strings` keyed by sequential int; `TranslateBool(name)`/`TranslateString(name)` **lazily
insert unknown names** (`Skin.cpp:756-774`), so a skin never declares settings — the first `Skin.HasSetting(foo)` creates
`foo`; unknown bool reads `false`, string `""`; any set triggers a 500 ms debounced save to the skin add-on's own
`settings.xml` (`<setting id="touchmode" type="bool">true</setting>`); `Skin.ResetSettings` clears and resets the
info cache; legacy `guisettings.xml` values are migrated on first load. Built-ins: `Skin.SetBool`, `Skin.ToggleSetting`,
`Skin.SetString(name[,value])` (keyboard when no value), `Skin.SetNumeric`, `Skin.SetPath`, `Skin.SetImage`,
`Skin.SetFile`, `Skin.SetAddon`, `Skin.SetColor`, `Skin.SelectBool(header, label|setting, …)` (select dialog setting one
bool, clearing the others), `Skin.Reset`, `Skin.Theme(+-1)` (`SkinBuiltins.cpp:727-748`).

The settings *page* is skin XML (`SkinSettings.xml`, a plain `CGUIWindow`):

```xml
<onload condition="String.IsEmpty(Skin.String(background_overlay))">Skin.SetString(background_overlay,1)</onload>
<control type="radiobutton" id="702">
    <label>$LOCALIZE[20189]</label>
    <onclick>Skin.ToggleSetting(autoscroll)</onclick>
    <selected>Skin.HasSetting(autoscroll)</selected>
</control>
<control type="button" id="706">
    <onclick>Skin.SelectBool(31024, 38018|circle_userrating, 563|circle_rating, 16018|circle_none)</onclick>
</control>
```

Enumerations are emulated with chained `<onclick condition="String.IsEqual(Skin.String(background_overlay),3)">
Skin.SetString(background_overlay,4)</onclick>`; pickers call Python (`RunScript(script.image.resource.select,…)`).
Settings drive structure through include conditions (§1.4). Colour themes are overlays: `media/<Theme>.xbt` holds only
changed textures, `colors/<Theme>.xml` only changed colours, and selecting a theme sets colours + fontset of the same
name (`ApplicationSkinHandling.cpp:574-597`). The core's own "Interface → Skin" page (fonts, colours, themes, startup
window, zoom) is filled from the skin's files by `CSkinInfo::SettingOptions*Filler` (`ext\kodi-winamp-foobar.md §1.4-1.5`).

Lessons: settings as *expression leaves* that can switch structure, visibility, animation and `onclick` routing uniformly
is the right idea; lazy undeclared settings are the wrong implementation (typos silently create settings; no type,
default, label or options; hence the `<onload>` seeding hack and chained-onclick enums). Winamp Modern lets skins
register their own config pages into the host's Options menu via MAKI `Config`/`ConfigAttribute` objects (opaque
bytecode — avoid); foobar2000 panels each own an opaque config blob and `show_config_popup` (host never interprets —
avoid as the option store, keep the shareable/private export split).

### 3.4 Framework-level option channels

- SwiftUI: styles and options are **environment values** — set on a container, inherited by the subtree, overridable
  nearer the leaf, reset with `.automatic`; custom `EnvironmentKey`s carry theme-specific options; `@Environment(\.isEnabled)`
  etc. ride the same bus (`ext\style-contracts-a.md §1.3`).
- Compose: `staticCompositionLocalOf` + a `Theme` composable + accessor object (`ExtendedTheme.colors.caution`); three
  strategies — extend Material, replace a subsystem, or "Implement a fully custom design system"
  (https://developer.android.com/develop/ui/compose/designsystems/custom).
- Qt: style-specific **attached properties** (`Material.accent`, `Material.elevation`) with declared propagation
  ("The value is propagated to children" vs elevation/roundedScale "do not propagate"); `qtquickcontrols2.conf`
  (`Theme`, `Variant`, `Accent`…); warning: "These style-specific APIs are only available when their style is actually
  selected" — app code that uses them is `if (theme == Material)` in disguise (`ext\style-contracts-a.md §4.3, §4.7`).
- Avalonia: `RequestedThemeVariant` on `Application`/`TopLevel`/`ThemeVariantScope` (subtree override), `ThemeDictionaries`
  only via `DynamicResource`; `FluentTheme.DensityStyle="Compact"`; palette `Accent` "supports bindings and can be changed
  at runtime", other palette properties read once at startup (`ext\style-contracts-a.md §3.3`).
- WinUI: `ThemeDictionaries` (`Default/Light/Dark/HighContrast`) + `{ThemeResource}` ("Evaluation … occurs when the app
  loads and subsequently each time the theme changes at runtime"), lightweight styling per scope (`<Page.Resources>` →
  `ButtonBackground`…) (`ext\style-contracts-a.md §2.4`).
- VS Code: no theme options, but the registry gives every colour **defaults per base theme and derivation transforms**
  (`registerColor(id, {light, dark, hcDark, hcLight}, description, needsTransparency?, deprecationMessage?)`;
  `ColorTransform ∈ {Darken, Lighten, Transparent, Opaque, OneOf, LessProminent, IfDefinedThenElse, Mix}`), and the JSON
  schema is generated from the registry (`vscode://schemas/workbench-colors`)
  (https://raw.githubusercontent.com/microsoft/vscode/main/src/vs/platform/theme/common/colorUtils.ts). Zed's schema makes
  every style key optional and refined onto defaults (https://zed.dev/schema/themes/v0.2.0.json).
- Windows Terminal: tiny layered theme objects (`window/tabRow/tab`, only sections present are parsed) with **symbolic
  values resolved at use time** (`"accent"`, `"terminalBackground"` = "the background color of the active terminal pane")
  and a `{ "dark": …, "light": … }` pair that follows the OS (https://learn.microsoft.com/en-us/windows/terminal/customize-settings/themes,
  `TerminalSettingsModel/Theme.cpp`).
- Rainmeter: user values live in separate `Settings.inc`/"Variables files" that survive package upgrades ("existing
  variable values are used instead of the defaults in the package"); `!SetVariable` (memory) vs `!WriteKeyValue` +
  `!Refresh` (file; "Reads the skin file and recreates the skin") (`ext\media-os-skins.md §6`).
- WMP: only `theme.loadPreference/savePreference` (registry); skins built their own option UIs (`ext\media-os-skins.md §3`).
- Home Assistant: YAML CSS-variable maps with `modes: light/dark`; selection "saved to your user profile"; "the behavior
  of other theme variables can change between releases" (https://www.home-assistant.io/integrations/frontend/).

### 3.5 Option model distilled for Wavee

- **Options are expression leaves** (Kodi): the same value a theme reads for a colour can gate a slot, a variant, a
  visibility or an animation; the theme's factories branch on *their own* options, core never does.
- **Declared by the theme, rendered by the host** (Obsidian, Millennium; Kodi's lazy strings are the counter-example):
  a typed option record per option —
  `Toggle`, `Select(options)`, `Slider(min,max,step,unit)`, `Text`, `Color(format, opacity)`, `ThemedColor(light,dark)`,
  `Heading` — with `id`, title/description (localizable), `default`, optional `scope` (surface/page, Millennium `affects`),
  optional `command` exposure. First-party themes declare them as C# records; a later package loader reads the same
  shape from JSON. The settings page is generated; no theme ships settings UI.
- **Values flow as scoped environment** (SwiftUI/Compose/Avalonia): the theme's factories read their own option values
  from the provider; core never reads theme options (Qt's warning). Options may also feed the token table (Obsidian
  `variable-*`) and *structural choices* (Obsidian `class-select` ⇒ in Wavee: select among the theme's own variants,
  e.g. Classic "row style classic/modern", "sidebar v3/classic").
- **Persistence outside the theme, keyed by theme id + option id**, kept across upgrades (Rainmeter), with Millennium's
  migration rules (rename ⇒ reset; removed option value ⇒ default; removed option ⇒ ignore).
- **Symbolic values** (`accent`, `artworkDominant`, `systemAccent`) resolved at evaluation time (Windows Terminal) so
  options stay correct when live state changes.
- **Capability subsets** (KDE): "use this theme's colours but keep my layout" is a user choice the host offers, not a
  theme feature.

---

## 4. Zero-flicker: startup, switching, navigation

### 4.1 Why frameworks flicker (documented causes)

- WinUI: `Application.RequestedTheme` "can only be set when the app is started … Attempting to set RequestedTheme while
  the app is running throws … NotSupportedException"; per-element `RequestedTheme` works by re-walking the tree:
  "It re-evaluates the {ThemeResource} markup extension expression … but doesn't yet propagate this reference down to
  the children of your control; this happens later, such as during the next measure pass" and shared brushes are
  mutated while walking — the documented cause of partial/visible intermediate frames (microsoft-ui-xaml #6938, #8249,
  #5788; the proposal to lift the restriction, #4474, is open)
  (https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.requestedtheme,
  `ext\style-contracts-a.md §2.5`).
- Avalonia: variant switch is supported (`RequestedThemeVariant`, `DynamicResource` only), but replacing a whole theme
  pack rebuilds every template on the live tree and re-parents live content: "At some point it crashes, when same child
  control was re-attached to another template parent … I wouldn't suggest replacing FluentTheme in runtime for
  production apps"; ControlCatalog's answer is "we reopen whole main window" (AvaloniaUI/Avalonia #15798, open)
  (`ext\style-contracts-a.md §3.4`).
- Qt Quick Controls 2: "It is not possible to change the style after the QML types have been registered" — the style is
  the QML type resolved at import; runtime style selection also disables QML→C++ binding compilation
  (`qquickstyle.cpp:441-452`, `ext\style-contracts-a.md §4.4`).
- Android: "`setDefaultNightMode()` automatically recreates any started activities"; a theme change "triggers a `uiMode`
  configuration change. This means activities are automatically recreated" (the black frame); `android:configChanges="uiMode"`
  + `onConfigurationChanged()` handles it in place, "for example while a video is playing"; API 31
  `UiModeManager#setApplicationNightMode` "lets the system match the theme during the splash screen"; launch screens
  must use `?android:attr/colorBackground`, never hard-coded white
  (https://developer.android.com/develop/ui/views/theming/darktheme).
- Kodi: `ReloadSkin` tears the whole GUI down under the graphics lock with animations disabled and re-opens the saved
  window, so a switch is a hard cut plus a replayed `WindowOpen` animation; GUI state (scroll, dialog stack, history) is
  lost while playback state survives because it lives outside the GUI; font/colour/texture-theme changes trigger the same
  full reload (§5.1). Winamp Modern reloads everything; foobar2000 needs a restart to switch UI modules — none of the
  three can switch without a visible rebuild (`ext\kodi-winamp-foobar.md §4.5`).
- KDE: layout apply is "get rid of old config" → `unload()` → reload (destructive), hidden by the compositor:
  `org.kde.KWin /BlendChanges start(800)` — "deliberately blocking so that we ensure Kwin has processed the animation start
  event before we potentially trigger client side changes" (`klookandfeelmanager.cpp:392-440`); Rainmeter `!Refresh`
  "recreates the skin"; WMP view switches tear down and re-`onload` (`ext\media-os-skins.md`).

### 4.2 Resolve before first paint

- Web: `next-themes` injects an inline script before children that reads `localStorage[storageKey]` (fallback
  `defaultTheme`), resolves `'system'` with `matchMedia('(prefers-color-scheme: dark)')`, sets a class/attribute on
  `document.documentElement` and `el.style.colorScheme = theme` — all inside `try/catch` and before hydration
  (https://raw.githubusercontent.com/pacocoursey/next-themes/main/next-themes/src/script.ts, `…/src/index.tsx`).
- VS Code: the theme service constructor restores `ColorThemeData.fromStorageData(storageService)` and applies it
  synchronously — "In order to avoid paint flashing for tokens, because themes are loaded asynchronously", "This needs
  to happen as quickly as possible for a flicker-free startup experience"; the stored theme is discarded if it no longer
  matches `workbench.colorTheme`; the real initialise waits for `whenInstalledExtensionsRegistered()`; a missing theme
  falls back to built-in light/dark after settings sync
  (https://raw.githubusercontent.com/microsoft/vscode/main/src/vs/workbench/services/themes/browser/workbenchThemeService.ts).
- Android: the system splash "uses the `windowBackground` of your theme if `windowBackground` is a single color"
  (`windowSplashScreenBackground` explicit); hold the first frame with a `ViewTreeObserver.OnPreDrawListener` returning
  `false` until `viewModel.isReady` ("suspend the app to draw its first frame")
  (https://developer.android.com/develop/ui/views/launch/splash-screen).
- WinUI sample: `LocalSettings.Values["themeSetting"]` is read in the `App()` constructor, before any window
  (https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.requestedtheme).
- Windows non-client: `DWMWA_USE_IMMERSIVE_DARK_MODE` (=20) — "For compatibility reasons, all windows default to light mode
  regardless of the system setting"; `DWMWA_CAPTION_COLOR`, `DWMWA_TEXT_COLOR`, `DWMWA_BORDER_COLOR` (`DWMWA_COLOR_NONE`
  suppresses the border), `DWMWA_SYSTEMBACKDROP_TYPE` (22621+); `DWMWA_CLOAK` "Cloaks the window such that it is not
  visible to the user. The window is still composed by DWM"
  (https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute). A Compose-Desktop/Skiko
  developer's fix for the dark-mode white flash on start/resize: clear the surface to the right colour and synchronise
  the DWM caption/border colours (https://slack-chats.kotlinlang.org/t/33147106/…); Chromium/Edge's dark-mode white flash
  was a window-creation-path fix (windowslatest.com, 2025-01-08).
- Startup-flicker root cause in XAML toolkits is *deferred* resource resolution (`{ThemeResource}` second pass "during
  the next measure pass", `DynamicResource` subscriptions firing after first layout): resolve the whole theme (tokens +
  factories) before the first frame; no lazy lookups on the render path (`ext\style-contracts-a.md §5.7`).

### 4.3 Switching without a wrong frame: snapshot old, build new off-screen, reveal

- View Transitions API: `document.startViewTransition(cb)` — "the API captures static image **snapshots** of elements that
  have a non-`none` `view-transition-name`"; the callback changes the DOM while "the page is frozen"; "The API captures
  'live' snapshots … from the new view"; default "old snapshots animate from `opacity` 1 to 0, and the new snapshots
  animate from `opacity` 0 to 1"; pseudo-tree `::view-transition > ::view-transition-group(root) >
  ::view-transition-image-pair(root) > ::view-transition-old(root) | ::view-transition-new(root)`; skipped if hidden or
  on duplicate names; the theme-toggle idiom animates `clipPath: circle(…)` on `::view-transition-new(root)`
  (https://developer.mozilla.org/en-US/docs/Web/API/View_Transition_API/Using,
  https://developer.chrome.com/docs/web-platform/view-transitions/same-document).
- iOS/Android production pattern (Uniwind): `UIView.snapshotView(afterScreenUpdates:)` overlay → set
  `window.overrideUserInterfaceStyle` underneath → animate the overlay away with `CAShapeLayer` masks → remove; Android
  uses a `PixelCopy` bitmap ("preserves hardware-rendered effects like filter: blur()") + `setDefaultNightMode()`;
  updates are coordinated "atomically" so the UI changes in one frame; ordering bug on 120 Hz ("the snapshot was cleaned
  up before the theme flip", fixed 1.8.0); OS-initiated switches cannot be animated this way
  (https://docs.uniwind.dev/pro/theme-transitions.md).
- KDE: compositor crossfade over the destructive restyle (`BlendChanges start(800)`, §4.1).
- React: pure render computes, commit "will apply the minimal necessary operations" and "After rendering is done and
  React updated the DOM, the browser will repaint the screen" — never a half-applied tree
  (https://react.dev/learn/render-and-commit).
- `next-themes` `disableTransitionOnChange`: inject `*,*::before,*::after{transition:none!important}`, force
  `getComputedStyle(document.body)`, remove after 1 ms — the whole page restyles in one frame instead of each element
  animating its own colour.

### 4.4 Keep state outside the tree

Android `configChanges="uiMode"` exists because recreate loses state; Avalonia #15798 crashes because live content was
re-parented between templates; Rainmeter "When a skin refreshes, all values are reset"; KDE deletes all applet config
groups; Kodi loses scroll positions, dialog stacks and window history on `ReloadSkin` but keeps playback because the
player lives outside the GUI. The rule from all five: the new tree is built from a model that owns scroll offsets,
focus, selection, playback and navigation stack; nothing is moved from the old tree to the new one
(`ext\style-contracts-a.md §5.6`). Two finer-grained precedents are worth copying for option changes *within* a theme:
Kodi re-resolves only windows whose recorded include conditions changed (`m_xmlIncludeConditions`/`NeedLoad()`), and
Columns UI *transfers* a live panel window to a new host instead of destroying it (`create_or_transfer_window`) — in
Wavee terms: an option flip rebuilds the affected subtree from its configuration, keyed state restored by identity,
not the whole surface.

### 4.5 Interpolate instead of cut where the theme is data

Flutter: `MaterialApp` wraps the tree in `AnimatedTheme`, which "automatically transitions the colors, etc, over a given
duration" (`kThemeAnimationDuration` = 200 ms); `ThemeExtension<T>.lerp` "will ensure smooth transitions of properties
when switching themes" (https://api.flutter.dev/flutter/material/AnimatedTheme-class.html,
https://api.flutter.dev/flutter/material/ThemeExtension-class.html). Immediate-mode UIs (egui `Context::set_style_of`,
ImGui `PushStyleColor`) are trivially atomic because the whole UI is re-emitted per frame. For Wavee: token-only changes
(accent, light/dark within one theme) can lerp; structural theme switches use §4.3.

### 4.6 Procedure for Wavee (what the sources add up to)

1. Theme id + the theme's base/caption/border colours are read from settings before `CreateWindow`; DWM attributes and the
   clear colour are set before the window is shown; tokens and factories are fully resolved (no lazy lookups) before the
   first layout.
2. The window is not shown until the first frame of the right theme is presented (gate on readiness as Android does;
   `DWMWA_CLOAK` → present → uncloak is the Win32 equivalent *(inference: the cloak/present/uncloak ordering is my
   proposal; DWM documents only cloaking)*).
3. Switch: keep the last presented frame as a texture; build the new theme's root from the shared model off-screen;
   render it to an off-screen target; cross-fade/wipe between the two textures; suppress per-element transitions during
   the swap; dispose the old tree after the reveal. Navigation under a theme = the theme's page factory, so page
   transitions are the theme's choreography, not a relayout.
4. Nothing crosses from the old tree to the new one; scroll/focus/selection/playback/navigation live in models.

---

## 5. Per-system reference (mechanism · contract · options · switching · lessons)

### 5.1 Kodi (from `ext\kodi-winamp-foobar.md §1`)

- **Mechanism.** Skin = add-on (`<extension point="xbmc.gui.skin">`, `<res width height aspect default folder>` ×8,
  `@effectslowdown`, `@debugging`); load order `UnloadSkin → skin->Start (pick <res>) → MigrateSettings → require Home.xml
  → ColorManager (system colors.xml → colors/defaults.xml → colors/<theme>.xml) → LoadIncludes → LoadFonts (Font.xml
  fontsets) → skin strings 31000-31999 → Timers → LoadCustomWindows → WindowManager.Initialize (LOAD_ON_GUI_INIT windows)
  → re-activate previous window + SETFOCUS` (`ApplicationSkinHandling.cpp:66-237`). Per-window XML parsed lazily on
  first activation.
- **Contract.** §1.4 above: window ids, few control ids per window, `<views>`, messages/built-ins, InfoLabels/booleans,
  includes/expressions/variables; `xbmc.gui` version gate with abi floor; no signing; "install from zip".
- **Options.** §3.3 above.
- **Switching.** `ReloadSkin` snapshots (active window, focused control), pauses video, tears everything down under
  the graphics lock (`DeInitialize`: `DisableAnimations()`, `Close(true)`, `FreeResources(true)`; info bools, textures,
  fonts, colours cleared), rebuilds, re-activates + `SETFOCUS`; player/playlist/library state untouched, *GUI* state
  (scroll, dialog stack, history) lost; visible as a hard cut followed by the window's replayed `WindowOpen` animation;
  font/colour/texture-theme changes also do a full reload; deferred while a modal render loop is nested
  (`ApplicationSkinHandling.cpp:434-513`, `GUIWindowManager.cpp:1587-1629`).
- **Performance.** `Includes.xml` parsed once per skin load (Estuary: 34 named includes, 24 include files, 177 variables);
  conditions parsed once to ids, deduplicated, per-frame memoised; the render loop still processes every control every
  frame (dirty regions save only the draw); single texture instance per path, `.xbt` bundles loaded synchronously;
  wiki advice: fewer controls, conditions on groups, no ids on unreferenced controls; `Skin.ToggleDebug` and
  `CGUIControlProfiler` for per-control timing.
- **Lessons.** Copy: id/role contract; skin-declared views; shared expression engine with settings as leaves; includes
  with params + per-type defaults; overlay colour/texture themes; version floor + ceiling gate; `OnSkinUnloading/
  OnSkinLoaded/OnSkinLoadFailed` announcements; sanity check (Home.xml) + automatic fallback with a toast. Avoid: full
  teardown on switch; undeclared settings; no per-window fallback; API bumps that orphan skins; per-frame evaluation
  of every control; helper-service dependence.

### 5.2 Winamp and foobar2000 (from `ext\kodi-winamp-foobar.md §2-3`)

- **Winamp classic (.wsz)**: fixed-coordinate bitmaps (`main.bmp`, `cbuttons.bmp` sprites at fixed offsets, `pledit.txt`
  colours); pure resource substitution; instant, flicker-free swap; works with every player version; no layout, no
  options. **Winamp Modern (.wal)**: `<WasabiXML>` with `<container>` → `<layout>` (one visible) → `<group>` from
  `<groupdef>` templates, `<elements>` mapping ids to sprite rects with `gammagroup` colour themes; core components
  embedded by GUID; behaviour in MAKI bytecode (`.m` → `.maki`, magic `FG`, GUID-dispatched classes from `std.mi`,
  `findObject("id")`); skins register config pages via `Config` attribs; load = parse all includes → build every
  container → load bitmaps/fonts → run `System.onScriptLoaded` → first paint (full rebuild). Lessons: copy
  containers/layouts (one window, several named appearances), group templates with overrides, core components embedded
  into theme-owned windows, theme-owned config pages registered into the host menu; avoid Turing-complete skin scripts
  bound to ids by string (opaque, un-upgradable, VM-quirk-dependent), all-or-nothing load.
- **foobar2000**: core has no UI; `user_interface` service (nine methods) chosen by GUID, `init` once/`shutdown` once
  (switch = restart); DUI `ui_element` (guid, subclass-role guid, `instantiate(parent,cfg,callback)`,
  `get_default_configuration`, `enumerate_children`, `import`, `notify(GUID)` for colour/visibility) with opaque
  `ui_element_config` blobs nested in container configs; Columns UI `uie::window` (type flags panel/toolbar/playlist/
  splitter, `create_or_transfer_window` — a window can be *transferred* to a new host without destruction,
  `get_config/set_config` + `import/export_config`, `show_config_popup`) hosted by `uie::window_host`; `.fcl` export
  (GUID header, `fcl_stream_version = 2`, "For sharing" vs "For this computer", missing panels reported, layout applied
  only if complete, "requires a newer version of Columns UI" guard). Lessons: tiny versioned host interface; role
  (subclass) GUIDs so the host finds "the playlist renderer" by role; shareable vs machine-local config; import that
  lists what is missing; transfer-not-destroy for live re-layout. Avoid: restart-to-switch; two incompatible panel
  ecosystems; unversioned opaque blobs as the option store.

### 5.3 MusicBee, AIMP, Windows Media Player, KDE Plasma, Steam, Rainmeter (from `ext\media-os-skins.md`)

| Engine | Format | Layout by skin? | Contract | Skin-defined options | Switch | Scripting |
|---|---|---|---|---|---|---|
| MusicBee skin (.xml/.xmlc) | `<element id="Panel.ScrollBarThumb.Default" bg= bg2= fg= bdr=>` + base64 images | No (layout is user "Arrange Panels": 21 elements × 9 locations) | fixed element-id vocabulary; unknown ignored, missing → default | none | in-app | none |
| MusicBee Theater Mode | `<element type= xAnchor= widthDock= onClick= visibleTrigger=>` | Yes, one surface | `type`/`id`, verbs, state triggers | none (edit XML) | pick per surface | none; wrong folder "crash hard" |
| AIMP (.acs5) | compiled container: per-resource compression, lazy texture unpack + eviction, flat-colour elision, precompiled scripts/bindings (1.8 GB → 270 MB peak) | Yes | `TASE*` controls ↔ `prv*` providers via access points; engine-version tag | script-opened font/colour dialogs | Skins dialog; first load repacks | compiled scripts |
| WMP (.wms/.wmz) | `THEME > VIEW(category) > SUBVIEW`; absolute positions; colour-keyed hit maps | Yes | `player/theme/view`; `wmpprop:`/`wmpenabled:`; predefined verb elements; `moveSizeTo`/`alphaBlendTo` | `theme.savePreference` only | per view; reload; "no video element → leave skin" | JScript |
| KDE Global Theme | `contents/defaults` INI of pointers + `layouts/*.js` | Yes (opt-in) | capability probing (`packageContents()`), `hint-*` probing, Breeze fallback | none; user picks apply subsets | KWin `BlendChanges(800)` crossfade; destructive layout reload | QJSEngine |
| Steam VGUI (†2023) | `.styles` (CSS-like) + per-dialog `.layout` (`place { control="A,B" x= align=right end-right=X }`) | Yes | host C++ control names | none | restart | none |
| Millennium/SFP | `skin.json` `Patches[{MatchRegexString, TargetCss, TargetJs}]` | Yes (CSS/JS over React DOM) | regex on title/URL/body class; generated class names | `Conditions` + `RootColors` (§3.2) | live | JS |
| Rainmeter | INI; `@Include`, `[Variables]`, measures/meters | n/a (skin is the widget) | measure/meter vocabulary | `.inc` variables files kept on upgrade; `!WriteKeyValue` + `!Refresh` | `!Refresh` rebuilds | bangs, Lua |

Lessons kept: verbs + state triggers (MusicBee), providers/access points + compiled bindings + lazy/evicting assets +
cap on distinct artwork sizes (AIMP "drop to 1x for skins with more sizes"), `wmpprop:`/`wmpenabled:` + predefined verb
elements + engine animation primitives + per-context views (WMP), capability probing + apply subsets + `hint-*` + theme
writes *defaults* under user overrides (KDE), `Conditions`/`RootColors` option schema (Millennium), settings persisted
outside the package (Rainmeter). Avoid: host-internal names (Steam), two incompatible formats with crash cliffs
(MusicBee), destructive apply (KDE/Rainmeter), mandatory script runtime (WMP), undocumented binary format (AIMP).

### 5.4 SwiftUI, WPF/WinUI 3, Avalonia 11, Qt Quick Controls 2 (from `ext\style-contracts-a.md`)

| Dimension | SwiftUI | WPF / WinUI 3 | Avalonia 11 | Qt Quick Controls 2 |
|---|---|---|---|---|
| Unit of contract | `XStyle` protocol + `XStyleConfiguration` → `makeBody` | `ControlTemplate` + `[TemplatePart]`/`[TemplateVisualState]` + VSM | `ControlTheme` (TargetType, `^` pseudo-class styles) + `[TemplatePart]`/`[PseudoClasses]` | style dir of `Control.qml`; root `T.Control` (C++) + `contentItem/background` |
| Who owns interaction | system (`ButtonStyle`) or style (`PrimitiveButtonStyle.trigger()`) | control (`GoToState`) | control (`PseudoClasses.Set`) | C++ template |
| Theme → control data | `configuration.label/isPressed/isOn…` | `{TemplateBinding}`, parts, states | `{TemplateBinding}`, `/template/ #PART_x`, pseudo-classes | `control.down/checked/hovered/visualFocus/palette`, attached props |
| Options | `EnvironmentValues` | `ThemeDictionaries` + `{ThemeResource}`; lightweight styling keys | `ThemeVariant`/`ThemeVariantScope`, `DynamicResource` | attached props, `qtquickcontrols2.conf` |
| Structure swap | container styles (`GroupBox`, `NavigationSplitView`, `DisclosureGroup`) | `ItemsPanelTemplate`, `ItemsRepeater.Layout`, `DataTemplateSelector`, whole template | `ItemsPanel`, `IDataTemplate`, whole template | whole delegate tree |
| Runtime switch | env change, per subtree | app-level throws; per-element re-walk (flicker class) | variant OK; whole pack "wouldn't suggest … production" (#15798) | impossible after type registration |
| AOT | native | WinUI compiled XAML; WPF not NativeAOT | compiled XAML OK; `AvaloniaRuntimeXamlLoader.Load` is `[RequiresDynamicCode]` | qmlcachegen only with compile-time style |

### 5.5 Flutter, Compose, Iced, egui/ImGui, Slate/Common UI, Godot

See §2.3 and §4.5; mechanisms, contracts and limits are paint/metrics-only except Compose, whose foundation/material
layering is the structural model (§1.5). Flutter `Adaptation<T>` ("Currently, only Switch.adaptive supports this
class") shows platform adaptation as a per-component theme hook, not a theme-level switch
(https://api.flutter.dev/flutter/material/Adaptation-class.html).

### 5.6 Community ecosystems (robustness summary)

| Ecosystem | Contract surface | Structure | Breaks on vendor update | Theme-defined options |
|---|---|---|---|---|
| VS Code | ~800 registered colour ids + generated schema, per-base-theme defaults/transforms | no | no (ids deprecated with messages) | no |
| Zed | JSON schema, all style keys optional | no | no | no |
| Firefox static theme | ~40 keys | no | no | no |
| Home Assistant | CSS variables | no | "can change between releases" | no |
| Windows Terminal | layered theme objects, symbolic colours | no | no | no |
| Obsidian | 400+ CSS vars + raw CSS | partially | sometimes | yes (Style Settings) |
| Spicetify | 15 colours + raw CSS vs obfuscated DOM | yes | every release | via extensions |
| BetterDiscord / userChrome / Millennium | raw CSS/JS vs internals | yes | every release | Millennium: yes |
| Kodi | window/control ids + infolabels + skin settings, `xbmc.gui` versioned | yes | on GUI API major bump | yes |

Robustness ∝ how much of the contract is published and versioned by the vendor. Kodi is the only ecosystem that is both
structural and (mostly) robust.

---

## 6. Loading third-party themes later (compact)

Not needed now; recorded so the contract designed in §1–§3 does not preclude it. NativeAOT facts: "No dynamic loading,
for example, `Assembly.LoadFile`. No runtime code generation, for example, `System.Reflection.Emit`";
`System.Linq.Expressions` interpreted only (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

| Option | Mechanism | AOT status | Isolation / trust | Unload | Per-frame | Verdict |
|---|---|---|---|---|---|---|
| Declarative package (tokens, variant picks, option schema, surface documents, assets) interpreted by compiled engine code | same shape as `ModulePageDoc` turned around; compile documents to retained `Element` trees once; bound expressions non-Turing-complete | native | data only; schema-validated, hash-pinned | trivial | zero beyond bound expressions | **the loader to add later**; requires only that §1's vocabulary is published |
| Effect shaders (HLSL text → `D3DCompile` → cached DXBC behind a fixed uniform/root-signature interface) | engine's existing path (`ShaderCompiler.cs`); MilkDrop precedent (`shader_body` per preset, recompiled "every time the preset loads", SM gating); Flutter precedent (build-time `impellerc`, `setFloat` by index) | native | GPU-sandboxed; DoS risk (TDR, frame budget) → per-shader budget + kill-switch *(inference)*; DXBC needs no `dxil.dll` signing (DXIL validator hash "was never intended as a security feature") | trivial | shader cost only | acceptable leaf extension; the only "code" a package may ship |
| WASM (wasmtime-dotnet / Extism) at event time, emitting documents | P/Invoke over Wasmtime C API; `FromCallback` = delegate marshalling + `GCHandle` + `ArrayPool`; typed path `wasmtime_func_call_unchecked` | not official: PR #348 "Support for native AOT & static linking" open (maintainer 2026-10-10: "I'd still love to merge this work"); one user report "seems to work fine (so far)" | memory isolation, `FuelLimit`/epochs, `allowed_hosts/paths`, `max_pages`, SHA-256 `hash`; precompiled `.cwasm` must be produced locally ("Passing unknown or untrusted bytes will lead to arbitrary code execution") | drop instance | wrong (≈25 ns host call + marshalling per node); event-time only | later, only if logic is ever needed |
| Lua (NLua/KeraLua, Luau) | native Lua behind P/Invoke; Luau sandbox (no `io/package`, read-only globals, bytecode rejected, interrupt handler, allocator limits) | native libs fine; MoonSharp needs hardwired descriptors | shared address space; weaker than WASM | per state | event-time only | inferior to WASM for the same purpose |
| NativeAOT shared library (`NativeLib=Shared`, `[UnmanagedCallersOnly]` exports, `NativeLibrary.Load`) | exports static, primitives/value types only, "cannot use regular C# exception handling"; "Each library has its own GC"; "we cannot share objects between them"; ~15.8 MB per library | native | none (native code, full trust); needs signing + trust prompt | none ("cannot be closed with dlclose/FreeLibrary") | native call cost; tree must cross a C ABI (vtables/handles, Shard's design) | first-party only, and worse than compiling in |
| Host CoreCLR via hostfxr | `hostfxr_initialize_for_runtime_config` + `hdt_load_assembly_and_get_function_pointer`; HostFxrSharp claims a Native-AOT-compatible wrapper | "Only one runtime can be loaded inside a single process"; hosting APIs "only support framework-dependent deployments" → shared .NET install or ship ~70 MB | second runtime, two GCs, no shared objects | `AssemblyLoadContext.Unload` on the CoreCLR side | C ABI boundary again | not recommended |

Versioning precedents: Kodi `xbmc.gui`, VS Code `engines.vscode` ("Cannot be `*`"), Obsidian `minAppVersion`, Zed schema
URL version, Wavee `SchemaVersion` + `ProtocolVersion`. Shape of a future package: `wavee-theme.json` (id, version,
`contractVersion`, publisher, capabilities, option schema) + documents + assets, hash-pinned, validated on install,
"unknown is skipped, over-budget is rejected".

---

## 7. Candidate architectures for Wavee

Criteria: **Z** zero-flicker (startup, switch, navigation); **P** per-frame perf under NativeAOT; **S** self-contained
built-in themes (own pages/player/nav/options) with no `if (theme == X)` in core; **O** theme-defined options; **L** a
runtime loader can be added later without redesign.

### A. Configuration → body at every level (SwiftUI/React Aria model, compiled in)

Every control and every composite surface (shell, nav, page templates, header, rows/cards, player bar, queue, module
page) is split into a *behaviour* (state machine, input, focus, UIA, virtualization, playback bindings) that produces a
typed `…Configuration` (slots + state vocabulary + verbs + writable bindings), and a *style protocol* `IXStyle { Element
Build(XConfiguration) }` resolved through a scoped provider. A theme = one assembly implementing the full factory table
(with a base theme as fallback), plus typed option records.
- Z: strong — theme resolved before the window; factories are plain C# so the first tree is built in the right theme;
  switch = off-screen build + snapshot cross-fade (§4.6); page navigation is the theme's own factory.
- P: best — no interpreter, no marshalling, no string lookups on the render path; the existing source-gen applies.
- S: strong — core references only protocols/configurations; the theme owns every `Build`. Risk: a configuration that
  leaks an engine-built subtree or lets the theme match by type name recreates §1.7 internally.
- O: strong — typed records, generated settings page, values as provider inputs to the theme's own factories.
- L: medium — a later loader needs a second implementation of every `IXStyle` that interprets documents; feasible
  because the configuration *is* the vocabulary, but the document grammar does not exist yet.

### B. Declarative surface documents for everything (Kodi model, first-party compiled from documents)

Themes are packages of surface documents + expressions + assets + option declarations; a document compiler turns them
into retained `Element` trees bound to published names; first-party themes are documents pre-compiled by the source
generator; community packages are parsed at install.
- Z: good if documents are compiled at install/first load and instantiated before first paint; same switch procedure.
- P: medium — instantiation is an interpreter walk per surface; per-frame cost is the bound-expression set (must be
  tiny, non-Turing-complete, pre-parsed to ids as Kodi does); hot lists must be engine-instantiated templates.
- S: strong, but the document grammar becomes a second API surface the engine must keep stable; every new state/part
  must be published before a first-party theme can use it, which slows first-party work (KDE issue #85 is the warning).
- O: strong (declared in the manifest).
- L: strongest — the loader is the same compiler.

### C. Behaviour/style split for controls only; pages stay in core with theme hooks

Only controls get style protocols; pages/player/nav remain core code that consults the theme for tokens, variants and a
few slot overrides.
- Z/P: as A. S: **fails the requirement** — pages are not theme-owned, so structural differences between Classic, WinUI
  and Zune end up as variant switches in core (the current "messy" state, formalised). O: partial. L: weak.
Listed for completeness; not viable.

### D. Hybrid (recommended): A as the contract, with the vocabulary designed so B's loader is additive

1. Engine (`FluentGpu.Controls`): behaviour + style protocol per control (§1.1/§1.2), published state vocabulary (seeded
   from React Aria's list, §1.5), typed parts (generated enums), layout objects + item factories for lists (§2.2),
   `StyleKeyOverride`/`BasedOn`-style fallback, scoped provider for styles and options.
2. App surfaces: each surface (shell, nav, home, album, artist, library, liked, playlist, queue, search, settings, player
   bar, full-screen, module page) is a behaviour exposing a `…Configuration` with slots, state, verbs and bindings
   (WMP's `wmpprop:`/`wmpenabled:` discipline for playback; MusicBee's verb/trigger naming); the theme assembly supplies
   `Build` for every surface. Theme-defined options are typed records (§3.5) read only by the theme's factories.
3. Startup/switch per §4.6; state in models, nothing crosses trees. Resolution is per surface with a fallback chain
   (theme → base theme → engine default), so a theme may omit a surface without a blank page (Kodi's gap, §1.4). An
   option flip rebuilds only the subtrees whose configuration depends on it (Kodi `NeedLoad()` idea at subtree
   granularity, §4.4), with the same keyed-state restore as a full switch.
4. Contract versioning from day one: the configuration/state/verb vocabulary carries a `contractVersion`; built-in themes
   are compiled against it, and a later package format declares the version it targets with a floor + ceiling gate
   (`xbmc.gui` `backwards-compatibility abi`, §1.4). Cosmetic additions (new tokens, new optional parts) must not bump it;
   only removals/renames do — the cost Kodi pays per release is the thing to design out.
5. Loader later: a package supplies tokens, option values/declarations, variant picks and surface documents; a document
   compiler maps names onto the *same* configurations and style protocols. Because A already forces every theme-visible
   fact through a configuration, B's grammar is a serialization of A's types — no redesign, and the engine's
   `ModulePageDoc` posture (manifest, versions, budgets, "unknown is skipped, over-budget is rejected") is reused.
- Z: strong. P: best for built-in themes; bounded for packages. S: strong — Classic, WinUI and Zune are three complete
  factory tables over one behaviour layer, each with its own option records, none referenced by core. O: one model for
  both tiers. L: additive.

Acceptance checks implied by the evidence: (a) `grep` for theme identifiers in core/engine returns only the registry
that lists installed themes; (b) deleting one theme assembly leaves the others compiling and running; (c) a surface
omitted by a theme renders the base theme's surface, not nothing; (d) the first presented frame after a cold start and
the first frame after a switch are already in the target theme (frame-capture test); (e) a theme option change does not
rebuild surfaces whose configuration does not read that option.

What to avoid, from the evidence: CSS-style "override anything" over the live tree (Spicetify/BetterDiscord/userChrome:
breaks every release); OS-style recreate-to-switch (Android, WinUI app-level theme: flicker or no runtime switch);
live-tree template replacement (Avalonia #15798); style identity bound to type resolution (Qt: no runtime switch);
theme-specific APIs consumed by app code (Qt attached props: `if (theme == Material)` in disguise); imperative theme
scripting over core internals (KDE #85); native/managed code loading for community themes (own GC, no unload, no
isolation); paint-only contracts where structure is required (Flutter/Iced/egui/ImGui/Godot/Slate).
