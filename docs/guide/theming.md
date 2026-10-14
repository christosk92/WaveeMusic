# Theming

> **Scope.** How Wavee's themes are built: the layers, the composition, the options a theme exposes, the settings that
> store them, the migration of retired switches, the analyzers that fence it all, and how a test mounts one view. The plan
> (`docs/themes/plan/technical-plan.md`) is the design; this is the working guide for the code that exists. T1
> (`feat/theming-core`) ships the machinery with Wavee Classic as the only theme and **no user-visible change**.

---

## 1. The layers

```
                     App.cs   ThemeHost.Install(ClassicTheme.Create(), ...)
 Themes\<Name>       Structure  (which views exist, what the shell offers)  x  Style  (the ThemeDefinition)  =  a composition
 Theming\            core: Contracts · Composition · Options · Policies · Roles · ThemeBootstrap · ThemeRoot
 Shell\ Entities\ Playback\ ...   core state and behaviour: models, verbs, hosts
```

- **Core never names a theme.** It talks to `ThemeComposition`, `IStructureLayer`, `IStyleLayer` and the contract keys. A
  theme is a folder `Themes\<Name>\` with namespace `Wavee.Themes.<Name>`, referenced only by `App.cs`,
  `Theming\Composition\ThemeCatalog.cs` and along the layering edges WVTH001 enforces.
- **A structure** registers views (`IStructureLayer.Register`), declares its options, and returns the shell's policies and
  capabilities for its option values. **A style** returns the engine's `ThemeDefinition` and the style metrics for its option
  values. Neither knows the other; the **composition** pairs them and adds counted overrides.
- **Contract version 1.0** (`Contracts\ContractVersion.cs`): the published view keys and style keys, their props records
  and the role vocabulary. A theme records the version it was built against in its manifest; `ThemeHost.Install` refuses a
  composition whose major differs or whose minor is newer than the core's.
- Keys live only in `Contracts\ViewKeys*.cs` and `StyleKeys.cs` (WVTH006). T1 declares none; each extraction PR adds its
  family in its own partial file (`ViewKeys.Shell.cs`: T4a's `Shell.Frame` and `Shell.ContentState`). How a surface becomes a
  registered view is section 2b.

## 2. Writing a theme

```csharp
namespace Wavee.Themes.Zune;

public static class ZuneTheme                                   // Themes\Zune\ZuneTheme.cs: the composition file
{
    public static ThemeComposition Create() => new(
        new ThemeManifest("zune", "theme.zune.name", ContractVersion.Major, ContractVersion.Minor,
            ZuneStructure.LayerId, ZuneStyle.LayerId, InheritsFrom: null, ThemeAvailability.Public,
            new ThemePreview("themes/zune-light.png", "themes/zune-dark.png")),
        new ZuneStructure(), new ZuneStyle());
}
```

- Ids: the theme id is lowercase letters, digits and dashes (it is part of every setting key). The layer ids in the manifest
  equal the layers' `Id`.
- `ThemeHost.Install` validates every composition (`ThemeComposition.Validate`: manifest, contract, layer ids, unique option ids
  across the three layers, every reference a composition makes to an option) and builds each registry once, so a structure
  that throws in `Register` fails the boot and not a frame.
- **Resolution never fails for an id.** `ThemeCatalog.Resolve(id)` answers Classic for an unknown or empty id and for a
  Developer-only theme while Developer mode is off. The stored `theme.id` is not rewritten when it falls back.

### The composition

`ThemeComposition.Definition / Metrics / Policies / Capabilities` are the only entry points core uses. The order is fixed:
(1) the style's metrics and definition, (2) every composition option's effect in declaration order, (3) `FixedEffect` last
(so a fixed formula sees the final metrics), (4) structure parameters = declared defaults, then `DefaultOverrides`, then
option effects, then `FixedEffect`. A composition that changes nothing returns the style's own `ThemeDefinition` instance
(Classic installs exactly `Design.Definition`); one that changes roles or accent returns a copy whose `Id` is scoped to the
composition and its option stamps, memoized per (stamp, OS accent).

Every override a composition makes (`CompositionOptions`, `FixedEffect`, `HiddenOptions`, `DefaultOverrides`,
`ReplacedOptions`) is a named, counted member of the record, so a review reads them in one place.

### Style roles (`Theming\Roles`, T2a)

A view never asks "which theme": it asks the **roles** of the style it renders under. A style publishes one immutable
`WaveeStyleRoles` table on its definition (`ThemeDefinition.AppRoles`) and a view reads it through the `R` facade, at the
point it builds an element:

```csharp
Element title = R.Type.TrackTitle(name);                  // text: the role's rung + deviations (TypeRoles)
Gap = R.Space.SectionGap, MaxWidth = R.Space.PageMaxW      // page rhythm, shelf and flyout geometry (SpaceRoles)
HoverScale = R.Motion.ScaleSubtle.Hover                    // interaction scale tiers, stagger rungs (MotionRoles)
Case = R.Case.For(LabelKind.Navigation)                    // how the style cases the words it writes (CaseRoles)
```

| Rule | Why |
|---|---|
| **Read `R` where the element is built.** Never into a `const`, a `static readonly` field or a long-lived closure | `R` caches nothing: it is `Tok.Definition.AppRoles` on every call, so a definition swap (`Tok.Use`, a theme switch, a Live option) reaches the next render. A field keeps the first style's value for the process. A value that needs a compile-time number is not a role yet (see `Design.Size`'s header) |
| **A role is data, a table is a record.** Add a role as a member of its record and give every style a value | Equal tables compare equal, so republishing an unchanged table is not a change (`Tok.Use` adopts the instance, no epoch moves); a table that differs in a non-colour role is a non-colour change (`StyleEpoch`); one that differs only in colour roles is not (see below) |
| **A text role is a rung plus deviations** (`TextRole`: face, size, line height, weight, tracking, auto-fit floor, secondary ink) | Every role resolves a size, a line height and a weight, and a style that changes the engine's type ramp changes every role built on it. Colour is the colour roles' (T2b, below) |
| **No role selects structure** | How a row or a pivot is *arranged* is the structure's (options, parameters, policies); how big, which colour, which casing is the style's |

**Classic's table** is `Themes\Classic\Style\ClassicRoles.cs` and holds the numbers `Design.Type`, `Design.Size`, `Design.Dock` and
`Design.Motion` held before T2a (pinned by `Wavee.Tests\Theming\ClassicRolesTests.cs`). `ClassicStyle.Definition` is `Design.Definition`
carrying that table. The appearance seed installs the **active composition's** definition (`ThemeHost.ActiveDefinition`), so a
light/dark flip keeps the active theme's roles.

**The process baseline.** Before a definition with roles is installed (a few boot steps, and a unit-test process that never boots
the shell) `R` falls back to a registered baseline. `ClassicRoles` registers itself with a module initializer, so core never names
a theme and no read lands on an empty table. A test that needs other roles installs a definition carrying them in the shell
test process (`Wavee.ShellTests\StyleRolesTests.cs`), the one process where `Tok` may change.

**What stayed in `Design`.** The behaviour (`Entrance`, `Reveal`, `Nav`, `FrameTime`, `Cadence`, `Reduced`), the Fluent duration ladder
(`Design.Motion.Faster` .. `Slow`, WinUI constants that `MotionTok` mirrors and a few `static readonly` transitions bake in), the
structural geometry whose consumers need compile-time numbers (`Design.Size`: control, row, pane, rail, thumb and player-bar sizes) and the
focus insets (`Design.FocusInsetBordered` / `FocusInsetRow`, an `Edges4` of the shape family: they leave with the shape roles). From T2b
the colour ladders are gone from `Design.cs`; what it keeps of colour is listed at the end of the next section.

### Colour roles (`Theming\Roles`, T2b)

T2b adds the **colour** half of the vocabulary to `WaveeStyleRoles`, family by family, with the same call syntax:

```csharp
Fill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed          // surfaces and row ladders (LayerRoles; was Design.Colors)
Color = R.Ink.AccentDecor                                          // the accent budget, premium green, data-hue ink (InkRoles; was Design.Accent)
Fill = R.OnMedia.ScrimRest, BorderColor = R.OnMedia.Stroke         // everything painted ON artwork or video (OnMediaLadder; was Design.OnMedia)
Fill = StageInk.Plate  /  R.StageArm.For(kind).Plate               // the immersive stage's polarity (StageArmTable; was Design.StageArm / StageInk)
float a = R.Wash.HeroAlpha, mix = R.Wash.CoverMix                  // how strongly artwork colours a surface (WashRoles; was Design.Wash + 8 branches)
float s = R.Lyrics.BloomScale                                      // the lyrics ink's numbers and its one polarity decision (LyricsInkRoles)
```

| Family | Record (`Theming\Roles`) | Reads | Members |
|---|---|---|---|
| Layer | `LayerRoles` | `R.Layer` | the translucent MUX rungs, `ShellGround`, `ContentSurface`, `ContentLayer`, `FloatingChrome`, `FloatingPane`, the row ladder (`RowZebra`, `RowHover`, `RowPressed`, `RowHoverZebra`, `RowPressedZebra`), the selection ladder, `ChromeHover`, `ChromePressed`, `Badge`, `EventPlate`, `EventHeroPlate`, `ArtPlaceholder` |
| Ink | `InkRoles` | `R.Ink` | `AccentAction`, `AccentSelection`, `AccentDecor`, `PremiumText`, `DataDot(argb)` |
| On-media | `OnMediaLadder` | `R.OnMedia` | scrim plates, `Stroke`, the three inks, glass and glass-plate rungs, `BackdropDim`, the spotlight stops, the light-button ramp and its two drops |
| Stage arm | `StageArmTable` | `R.StageArm` (the table), `StageInk` (the live arm) | `IsDark`, `Veil`, `Floor`, ink, glass, scrim, `Stroke`, the play disc, `SkeletonBar`, the quiet plates, `Card`, `AccentFrom`, `SelectedTileWash`, `ScrimBaseA` |
| Wash | `WashRoles` | `R.Wash` | `NeutralGround`, the strengths (`HeroAlpha`, `ShelfAlpha`, `BandPull`, `HeroVeilPull`, `HeroVeilPeak`, `CoverMix`, `ConcertMediaMix`, `PageTonePlaneAlpha`, `BlendWashTop`, `BlendWashBoundary`, `ShellTintAlpha`) and the recipes (`PageToneFromHue`, `ShellTintFromScheme`, `BlendWashLadder`) |
| Lyrics | `LyricsInkRoles` | `R.Lyrics` | `ThemePlateAlpha`, `RingTrackThemeAlpha`, `RingTrackOnMediaAlpha`, `BloomOnMedia`, `BloomScale` |

**A colour role is a pair or a function of the theme kind** (`ColorRole.cs`). `ColorRole.Pair(light, dark)` / `Fixed(c)` hold values and
compare by value; `ColorRole.By(kind => …)` is a recipe over the engine's **live** tokens (`Tok.FillSubtleTertiary`, `Tok.Palette.LightShell`,
the accent ramp): the engine re-derives those on every accent or palette change, so a role that IS "the engine's hover rung" cannot be a frozen
colour. A recipe is built from a `static` lambda (equal tables share the delegate, so an unchanged table compares equal), receives the kind it is
asked for, and reads everything else as live as the call. The numbers a style gives per polarity (a wash alpha, a tint pull) are
`ModePair<float>(light, dark)`. The `R.*` facades (`LayerReads`, `InkReads`, `OnMediaReads`, `WashReads`, `LyricsReads`) evaluate a role for the
**active** kind (`ColorRole.Now`); `Roles.X.Resolve(kind)` is the pure per-kind evaluation the tests use.

| Rule | Why |
|---|---|
| **A colour or an alpha that differs by kind is a role**; a view never writes `Tok.Theme == ThemeKind.Light ? a : b` | The branch is the style's decision. The 27 branches in `Design.cs`, `Controls.cs`, `Concert.UI.cs`, `Deck.UI.cs`, `Rail.UI.cs`, `ClassicMaterial.cs` (was `Shell.Masthead.UI.cs`) and `Detail.UI.cs` are role reads now |
| **The polarity as an ARGUMENT is `R.Kind` / `R.IsLight`** | Selecting the graded half of a cover (`Design.SchemeFor`, `ChromeSchemeFor`, `PlaceholderFor`), the `light` flag of the pure follow-button maths (`ButtonRules.FollowStyle`) and the theme picker's preview image need the kind, not a colour. Apart from the stage faces' own `dark` flag (`StageInk.IsDark`; T7 re-points the faces) and the install paths (`Appearance`, `ThemeHost`, `Tok.Use`), these are the only places code asks for the kind |
| **Key a wash or tone plane on `Tok.Epoch`, never on the light/dark flag** (themes plan X44) | `Tok.Epoch` moves for a kind flip AND a definition swap, so the plane is rebuilt in the same publication as the theme; a key on the kind keeps the old plane across a swap that moves a wash role. `Palette.ToneKey(route, Tok.Epoch)` builds the tone plane's key, the shell tint binder keys its publish effect on the epoch, and the three pages that republished a `Signal<ThemeKind>` (Detail, Artist, Profile) re-derive their accent from an effect keyed on `Tok.Epoch` instead |
| **A colour-only table is not a transaction** | `WaveeStyleRoles.DiffersOnlyInColours` compares the type, space, motion and case roles; ink, layer, on-media, stage arm, wash and lyrics may differ. A swap that moves only those bumps `Tok.Epoch` and leaves `StyleEpoch`, so the engine takes the live cross-fade path |

**The maths is `Platform\ColorScience.cs`.** Lift, vivid, the hairline and text-ink contrast solves (the 22-iteration bisection stays 22), the page-tone
clamp, the data-hue ink, the shell ground and content step, the placeholder blend and the accent ladder take the theme **kind**, the card surface and
the fallback colour as arguments and read no token. A recipe in a style calls it; `Design.Palette` is the thin face that binds it to the active kind
and accent (`Hairline(seed)`, `HairlineHover`, `TextInk`, `ChromeAccent(scheme)`, `ChromeFromPayload`).

**Classic's colour table** is the `Layer()`, `Ink()`, `OnMedia()`, `StageArm()`, `Wash()` and `Lyrics()` recipes in `ClassicRoles.cs`: each is the body
of the `Design` expression it replaced with the kind passed in. They are pinned against the formulas written out in the test
(`Wavee.Tests\Theming\ClassicColourRolesTests.cs`: both kinds for every role whose formula reads no live token, the process's own kind for the rest) and
against a flip in the shell process, where the kind may change (`Wavee.ShellTests\ColourRolesTests.cs`, which also covers the colour-only swap and a
mounted view repainting).

**What stayed in `Design` of colour, and why.** `SchemeFor` / `ChromeSchemeFor` / `PlaceholderFor` / `WatchedPlaceholder` (they read a cover's grading
for the active polarity from the palette TABLE: data selection, not a look), `Design.Palette` (above), `Design.Wash`'s window-relative GEOMETRY and
paint budget (not a look), the shell material channel (`ShellMaterial`, `TintOwnership`, now `Platform\ShellMaterial.cs`), while the four cover-leaf components read their
alphas and recipes from `R.Wash` and are Classic's material views since T4c. The shape roles (`ShapeRole`) and the icon table (`IconTable`) are not part
of T2b: their consumers (the omnibar row, the surface shapes, the route glyphs) are the T3/T4/T6/T8 PRs.

## 2a. Policies and metrics (T3)

A structure decides the numbers the core state machines run on; the machines themselves stay core. `ThemePolicies`
(`Theming\Policies\`) is the bundle a structure returns from `IStructureLayer.Policies`:

| Record | What the structure decides | Core machine that takes it |
|---|---|---|
| `NavPanePolicy` | what the Narrow and Tiny bands force (`NavPaneMode`: Expanded, Compact rail, Overlay drawer, None; `ModeFor(WidthTier)`), the expanded width bounds and default, the content floor, the seam collapse rule, whether the pane is resizable. The bands themselves are the window tiers (section 7): Compact is Tiny, Small is Narrow, Standard and up are Wide | `SidebarPaneModeRules`, `SidebarResizeRules`, `FrameModel`, `SidebarPaneBounds.Clamp` |
| `PanelPolicy` | the panel's min and max width, the content it must leave to stay inline, full height (on `PanelView.FullHeight` since T6b), the mini player's `Video.PipBounds` | `Shell.ClampRailWidth`, `Shell.CanFitRail`, `PanelHost.FitWidthTo`, `Video.PipGesture` |
| `PlayerBarPolicy` | six pressure levels (start width, `PlayerBarSlots`, spacing, art, hit targets, and what the podcast bar carries at the level: `PodcastSlots`, `PodcastLeftW`), the narrowing hold and the bar height | `Shell.PlayerBar`, `Shell.PlayerBarLayout.ForTier`, `Shell.PodcastBarLayout` (so `PlayerBarRules.Overflow` and `RightWidth` follow); `R.Space.DockReserve` answers the bar's height |
| `ChromePolicy` | the title-bar row's `ChromeBudget`: where the actions, name and forward button may appear, what each affordance costs, the search and tab widths | `Shell.Chrome` (the allocator) |
| `StagePolicy` | the stage's `AspectThresholds` (Compact, Portrait, Ultrawide edges and holds) | `Stage.Layout.ClassOf` / `Resolve` |

`ShellCapabilities` says what the shell offers (Classic: `Unrestricted`) and `StyleMetrics` is the style's row heights and
gutters (Classic publishes its own from `Design`). `ThemePolicies.Tiers` carries the tier policy (`TierPolicy`, `WidthTier`; section 7).

- **Classic's numbers are `Themes\Classic\Structure\Rules\ClassicPolicies.cs`** and equal what the shell shipped with. The
  core keeps the same set as `ThemePolicies.Default` so it has a policy before a composition is installed and when a structure
  publishes one it cannot run on; `ClassicPoliciesTests` pins the two equal (the tiers aside: core never names a theme, so `Default` carries `TierPolicy.Standard` and Classic's bundle carries `ClassicTiers.Policy`), so a tuning PR changes both on purpose.
- **A rule takes its policy as a trailing argument.** `null` is the active policy (`ShellPolicy.Now`, a plain read of
  `ThemeHost.Policies`); an effect or a render that must re-run when the policy changes passes `ShellPolicy.Tracked.<Member>`
  (the frame, the player-bar owner, the chrome allocation and the stage layout do). `ThemeHost.Policies` is the policy half of
  `ThemeHost.Facts` on a signal of its own, so a tier change does not re-run a policy reader.
- **Validation.** Every record has `Validate()` (ordered edges, finite and positive widths, the six player-bar levels rising,
  hit targets at or above the 32 / 36 floors, a device route and an identity block in every level). `ThemeHost` validates what a
  structure publishes; a bundle that fails is logged and replaced by `ThemePolicies.Default`, so a theme's bug is a wrong look
  and not a collapsed window.
- **A policy change at runtime** (a theme switch, or a live option that moves a policy) republishes `ThemeHost.Policies`: the
  frame re-bands, re-presents the pane and re-allocates the title bar, the bar re-resolves its level, the open stage
  re-classifies, and the panel's committed width is brought inside the new bounds (the stored preference is left alone).
- **Equality is by value** (the player bar's level array compares element by element), so republishing an unchanged bundle is
  not a change.

## 2b. Views: moving a surface into a structure (T4a, the T-lane recipe)

T4a is the first PR that registers a view. The API was T1's (`IStructureLayer.Register(ViewRegistryBuilder)`, the engine's
`ViewKey<TProps>`, `Slots.View`, `ViewTemplate<TProps>`); T4a only uses it. Every later T-PR repeats the same six steps.

| Step | Where | What |
|---|---|---|
| 1. Key | `Theming\Contracts\ViewKeys.<Family>.cs` | `public static readonly ViewKey<XProps> ShellFrame = new("Shell.Frame");` A dotted, constant, unique id (WVTH006); the key is created nowhere else |
| 2. Props | `Theming\Contracts\XProps.cs` | a **sealed record class**, init-only members (WVTH006: no setter, no `Signal<T>` member: carry `IReadSignal<T>`, a `Func`, a value). Props freeze at mount: **live values reach the template through signals or Funcs it binds with `Prop.Of`**, never through a changing member. Delegates are cached statics or built once, so an equal record skips the template |
| 3. Core host | the existing component | keeps every hook, effect, state machine and verb; builds the props once; mounts `Slots.View(ViewKeys.X, props)`. Mark it `[CoreHost]` (WVTH008: it then builds no element and reads no `Tok`/`Design`/`R`; what it still does goes in `analyzer-baseline\WVTH008.txt` and shrinks) |
| 4. Template | `Themes\Classic\Structure\Views\<Family>\ClassicX.cs` | `internal static class ClassicX { public static Element Template(XProps props, in ViewContext ctx) }`: the element tree the core built before, with the same keys, ids, transitions and bound channels. It may not reference `Shell`, `Entities`, `Playback`, `Spotify`, `Store`, `Prefs` (WVTH002): what it needs arrives on the props or as a verb |
| 5. Numbers | `Themes\Classic\Structure\Rules\` | a literal size, radius, gap or opacity (not 0 or 1) is a rule or a role, not a template literal (WVTH003). The shell-level constants that came with the code (`ClassicFrameRules`) live here |
| 6. Register | `ClassicStructure.Register` | `views.Add(ViewKeys.ShellFrame, ClassicFrame.Template, "classic.shell.frame");` The template id is the slot's identity: a different id under the same key remounts the slot, the same id with a new registry only re-runs it |

**Surfaces that are not yet their own key arrive as builders.** A moved view often places surfaces another T-PR owns. Until that
PR gives them a key, the props carry a `Func<Element>` over the core builder that exists (`ShellFrameProps.StageView`, ...); the owning
PR replaces the member with a `ctx.View(...)` call. Nothing else in the frame changes. The title bar (T4b), the frame's small views (T4c),
the sidebar pane (T5a), the player bar (T6a) and the right panel (T6b) did: `ShellFrameProps.PlayerBar` carries its props and the frame mounts
`ctx.View(ViewKeys.PlayerBar, props.PlayerBar)`.

**What stays core, and why.** Models, state machines, signals and verbs (the sidebar band and presentation, the tiers, auto zoom,
the rail and docked-video coupling, the chrome allocator, the material mirror, the route reducer, the auth fold, the page
transition's recipe) decide and publish; a theme only draws. Behaviour the engine wires as a control (the splitter, the
shell chords, the file-drop target, the realized-node captures the probes and the scrim clip read) is built by the host and
**placed** by the template.

### The frame and the content states (T4a)

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.Frame` | `ShellFrameProps` | `Shell.FrameRoot` (`[CoreHost]`, `Shell\Shell.UI.cs`) | `ClassicFrame`: the column (chords, title row, content region, player dock) under the material, the stage, the video and overlay layers, the content region (sidebar column, content card and its stroke, rail gap and reservation, both seams, the rail overlay, the narrow drawer) |
| `Shell.ContentState` | `ContentStateProps` (`Kind` is a `ContentStateKind`) | `Shell.ContentHost` (`PageBody`, `NotFoundPage`, `OfflineLane`) | `ClassicContentState`: the page and empty-body boxes, the not-found page, the offline lane |

`Shell.ContentState` is also a **pure core value**: `Theming\Contracts\ContentState` = the route's body (`Page`, `EmptyBody`,
`NotFound`) and the offline-lane flag. `Shell.FrameRules.ContentFor(route, hasPage, developerMode, auth)` decides it (a known route
with a page is a page; a known route whose page is not registered is an empty body, never the not-found page; an unknown route, or a
developer-only route while developer mode is off, is the not-found page; the lane is up exactly while the session is `Offline`,
over any body) and `Shell.FrameRules.ContentKey` names the body's node key (`page:<route>`, `page-empty:<route>`,
`page-notfound:<route>|<arg>`). The decision is stateless, so a quick run of route changes decides each route alone. The keep-alive
boundary, its LRU, the page-swap transition recipe (`PageTransition`) and the scroll scope stay in `ContentHost`/`PageBody`:
the recipe is the motion's (M1 moves it to the structure), not a picture.

A theme that mounts the real shell must register `Shell.Frame` and `Shell.ContentState`: a registry without them draws nothing
(the slot logs `[view-slot] view key 'Shell.Frame' renders nothing`). A test theme that mounts the shell registers Classic's views
(`new ClassicStructure().Register(views)`), as `ThemeOptionsMountedTests` does.

**Tests.** The decision and its edges are pure (`Wavee.Tests\ContentStateTests.cs`); the mounted facts are
`Wavee.ShellTests\ContentStateMountedTests.cs` (the registered frame is Classic's, each state stands under its key in the real
shell, the lane comes and goes, and `ViewHarness` mounts `Shell.ContentState` alone under the Classic composition).

### The title bar and the search (T4b)

| Key | Props | Core side | Classic template |
|---|---|---|---|
| `Shell.TitleBar` | `ShellTitleBarProps` (`ShellFrameProps.TitleBar` replaced the frame's `ChromeRow` builder) | `Shell.TitleBarProps()` (`Shell\Shell.TitleBar.cs`) over the allocator (`Shell.Chrome`), the tab workspace's items (`BuildTabItems`), the auth fold, the history stacks, the pin | `ClassicTitleBar`: the engine `TitleBar` in merged mode (tabs island with the history buttons and the text-first strip, the centre column, the trailing identity island with the bell, friends and pin, the caption-leading theme toggle and magnifier) |
| `Shell.Search` | `ShellSearchProps` (`Form` is a `SearchForm`: the field or the magnifier) | `OmnibarModel` (the store, the keystroke and fetch edges, the focus tickets, the verbs), the field width rule | `ClassicSearch`: `SearchField`, `SearchFlyoutButtonView` and the rich omnibar over the model |
| `Shell.SearchSuggestions` | `SearchSuggestionsProps` (a `SearchSuggestionSource` the core shares, plus the popup's width, choose and close) | `Shell.OmnibarAnswer()`: the store's state as a `SearchAnswer` (state, the visible completions, the visible rich rows with the builder of their facts) | `ClassicSearch.SuggestionsPopup` and `OmniRow`: rows, the pending skeleton, the notices, the always-mounted progress slot |

**The allocator stays core.** `Shell.Chrome` is a state machine that takes the structure's `ChromePolicy`: it decides which of the
name, the actions (bell, friends, pin) and the forward button are in the row, how wide the search field and the tabs are, and holds
each promotion for 40 DIP so a resize drag commits one change. The view reads its decision as a `ChromeRowState` (`Shell.RowStateOf`:
the flags, the identity chip's `ChromeChipForm`, the `SearchForm`, the field width and the tabs island's width) and draws it; the
element tree is exactly what the budget priced (the pin keeps its 44 DIP as an invisible slot when the destination cannot be pinned).
`ClassicPolicies.Chrome` holds the widths the row is allocated by; the view's own numbers (the 40×44 island buttons, the chip box,
the popup's paddings and skeleton bars) are `Rules\ChromeLayout.cs` (`ClassicChromeLayout`).

**Where the engine's control stays the engine's.** The drag regions and the three caption buttons are the `TitleBar` control's: it
reports the interactive islands as Client regions, the buttons as HTMIN / HTMAX / HTCLOSE and the whole bar as the Caption catch-all.
The template only fills its slots, and the core's `ContentVersion` stamp (every term an island reads) makes an island that changes
size re-push the regions in the same frame. The same holds for the tab strip (overflow scroll, pinned cells, drag reorder, the drop
targets that spring-load a tab): the template configures the engine `TabStrip`, the items and verbs are the workspace's.

**Writable cells and the model travel as Funcs and a model object.** WVTH006 keeps a writable `Signal<T>` out of a props member, so the
strip's selection cell and the omnibar's text cell are `Func<Signal<T>>` (the control binds them once at mount). The omnibar's model is
carried as the object; the members a view may use are the ones that name no `Shell` type (`Version`, `Highlight`, `SearchFocused`,
`FlyoutOpen`, the edges, `QueryGeneration`, `QueryText`, `GhostFor`, the verbs). Anything typed with `Shell.Omnibar.*` (the answer's
items, an item's play, menu and drag) stays in the core and reaches the popup as a `SearchAnswer` and its `SearchRichRowFacts`.

**Root-lifetime state moved with its user.** `Shell.TabStrip` (the mounted strip instance) is declared in `ClassicTitleBar` and
`Omnibar.AnswerMemory` (what the mounted popup last measured) in `ClassicSearch`; a root remount starts both fresh, as before. The
strip's measured extent (`Shell.TabExtent`) feeds the allocator and stays in `Shell.UI.cs`.

**Tests.** The mapping from the allocation to the view's value, with the hysteresis intact, is `Wavee.Tests\ChromeRowStateTests.cs`;
the published ids, Classic's registration and the props equality are `TitleBarContractTests.cs`. The mounted facts are
`Wavee.ShellTests\TitleBarMountedTests.cs`: the real shell's title row is the registered template's, narrowing swaps the field for the
magnifier and widening brings it back, `ViewHarness` mounts the title bar with fixture props (each identity chip, the pin slot, the
search form) and the suggestions body alone (the progress slot, the pending height, rows).

### The frame's small views (T4c)

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.Masthead` | `MastheadProps` (a held `PageChrome`, `Live`, the tool verb) | `Shell.MastheadBand` (`[CoreHost]`, `Shell\Shell.Masthead.UI.cs`): which route's publication is live, the trail it holds while an unknown family fades | `ClassicMasthead`: the band row, its frame insets, the Show/Hide fade |
| `Page.Head` | `PageHeadProps` (a `PageChrome`) | placed by the masthead; a page that draws its own head mounts it | `ClassicPageHead`: the trail-as-title row (dim clickable crumbs, chevrons, the current title under a stable key) |
| `Shell.Material` | `MaterialProps` (the published `ShellMaterialState` as a read-only signal) | the frame; the channel is `Platform\ShellMaterial.cs` | `ClassicMaterial`: the flat tint with its brush fade, Home's three radial washes |
| `Shell.Material.PageTone`, `.ArtistBlend`, `.ArtistVeil`, `.TintBinder` | `CoverToneProps`, `CoverBlendProps`, `CoverVeilProps`, `CoverTintProps` | the mounts in `Entities\Palette.Host.cs` (pages never write `Slots.View`) | `ClassicCoverLeaves`: the four cover leaves, moved from `Design.cs` |
| `Shell.NarrowDrawer` | `NarrowDrawerProps` (`Mounted`, `Open`, and the stable `NarrowDrawerParts`) | `Shell.NarrowDrawer` (`[CoreHost]`, `Shell\Shell.UI.cs`): the Escape key-preview chain, the leaf-navigation close, the mount decision | `ClassicDrawer`: the scrim, the pane, their open/close motion |
| `Shell.CommandPalette` | `PaletteProps` (the popup control, a session factory, close) | `Shell.CommandPalette()` + `Shell.PaletteSession` (`Shell\Shell.Palette.cs`); the table, the filter and the empty-query order stay `CommandTable` | `ClassicPalette`: the lane, the card, the keyboard navigation |
| `Shell.HistoryMenu` | `HistoryMenuProps` (the rows, close) | `NavHistoryButton` decides the rows (`FrameRules.HistoryMenu`) | `ClassicFrameBits.HistoryMenu` |
| `Shell.FileDrop`, `Shell.DragChip`, `Shell.CoverScrim`, `Shell.Fps` | `FileDropProps`, `DragChipProps`, `CoverScrimProps`, `FpsProps` | the frame root (the drop target, `Drag.Preview`, `Setup.Covering`, `Diagnostics.FpsOverlayOn`) | `ClassicFrameBits` |

**`PageChrome`** (`Theming\Contracts\PageChrome.cs`) is the value the core hands the frame for a page's head: today the title, the trail
(`PageCrumb`: a label and a navigate verb, none on the current page) and the "Show all" tool's state, built from the masthead publications
(`Shell.Mastheads`, the nav origins, `Shell.TryMasthead`). Views, band pivots, actions, the material claim and the backdrop join it as additive members
with the lanes that have data for them.

**Props that cannot carry a writable signal.** The tint binder publishes into the shell material, and a props record may not hold a `Signal<T>`
(WVTH006), so it carries a `MaterialPort` (`Platform\ShellMaterial.cs`): one cached object per slot, compared by reference, whose `Publish` is
`ShellMaterial.Publish`. The palette's popup is the engine control wired to the open flag by the host, so the props carry a
`Func<Element, Func<Element>, Element>` and the template places what comes back. The decisions stay core and pinned by their tests: the hand-over
rule (`TintOwnership`: a claim, an owner-only refresh, never a clear, so a navigation never flashes), the veil's pre-settlement guard
(`VeilSettlement`), the drawer's mount decision (`NarrowDrawerMount`, the band alone), the palette's scoring and the FPS rule (developer mode only).

**Tests.** `Wavee.ShellTests\FrameBitsMountedTests.cs`: Classic is the registered template of every key above, each view stands in the real
shell or alone under `ViewHarness` with fixture props (the masthead row, the drawer's two states, the palette's rows and keyboard, the file-drop
cue, the scrim, the HUD only in developer mode), and the cover leaves still publish the shell tint through the port.

### The navigation pane's rows (T5a)

The sidebar is one pane over six sub-surfaces (rows, heads, flyouts and menus, the Edit outline, drop cues, the library grid). T5a
gives the pane its key and moves the **rows**; T5b moves the **heads** (below); T5c moves the rail's flyouts (below); T5d-f move the rest, one sub-surface each.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.NavPane` | `NavPaneProps` (`InDrawer`, `Mount`) | `Sidebar.PaneHost` -> `ClassicMode` / `LibraryMode` -> `PaneView` (`Shell\Sidebar.UI*.cs`), mounted by the frame (`ShellFrameProps.NavPane`, replacing the `SidebarPane` builder) and by the narrow drawer (`Sidebar.DrawerPane()`) | `ClassicNavPane` (`Views\Nav\`): hands the core builder Classic's `ClassicNavRows` |

- **The pane is a core host.** `PaneView` keeps every hook, effect and state machine: the plan -> publish -> per-row epoch pipeline,
  the selection travel and the pill target, disclosures, reorder bands, the tree multi-selection, the drag peek and when the freeze
  holds or flushes. It is not marked `[CoreHost]` yet: it still builds the pane layer (the layer, root and context-menu shield
  boxes and the padded list) and the Edit pane, which WVTH008 would list. The filter box and the empty hint left with the heads
  (T5b, below).
- **The structure supplies the list as a `NavRowKit`** (`Theming\Contracts\NavRowKit.cs`; Classic's is `ClassicNavRows`): the planner
  and its owning-section answer, the publish diff (`RowChanged`), the analytic extent that seeds the virtualizing host, the mid-drag
  parking bay (`INavStageHold<T>`: no row jumps while a list is frozen during a drag, the newest stage flushes once at session end)
  and the row **slot**. The template calls `props.Mount(kit)` and the core builds the pane around it, so core never names a structure.
- **Classic's files.** `Rules\SidebarPlanner.cs` (`SidebarRowPlanner`, `SidebarPlanDiff`, `SidebarRowDiff`, `SidebarRowExtents`,
  `SidebarStageHold<T>`), `Rules\SidebarGeometry.cs` (the row ladder: heights, the icon column, indent, pill, the grid and cue
  numbers), `Views\Nav\ClassicNavRow.cs` (the bound slot: header, route, entity and folder rows, rail tiles, grid strips, the empty
  hint, the tree's end gutter, the drop band, the selection pill and the drop cues), `ClassicNavRows.cs` (the kit),
  `ClassicNavPane.cs` (the template).
- **`INavRowHost`** (`Theming\Contracts\NavRowHost.cs`, implemented explicitly by `PaneView` in `Shell\Sidebar.UI.Host.cs`) is what a
  slot may ask of the pane: the published plan, what the pane resolved once for every row (selected route, playing rows, the pill
  target, the reorder band), the verbs, and the pieces of the surfaces T5c-f still own (menus, drop specs, flyouts, the grid cell).
  A slot never reaches into `Shell`.
- **X26 is gone.** `OwningSection` (which section would hold a route: the pill's header or folder anchor) was a layout branch inside
  the "one renderer"; it is `SidebarRowPlanner.OwningSection`, next to the Library plan, and `NavModel` no longer answers it.
- **A transitional mirror.** The row ladder is Classic's. Core keeps `SidebarRowGeometry`: the plan queries (`TrySectionBodyRange`,
  `FolderHeaderIndexOf`, ... no pixels) and a copy of exactly the ladder members the still-core readers use (the shared row
  primitives in `Sidebar.UI.Rows.cs`, the Library head, the flyouts, the Edit outline, the drop rules, the resize rules).
  `SidebarGeometryParityTests` pins every shared member equal; the last reader to move (T5f) deletes the copy. The band widths are
  `NavPanePolicy`'s (section 2a).

**Tests.** The planner, the diff, the extents, the freeze and the ladder are pure (`SidebarPlannerTests`, `SidebarWiringTests`,
`NavDropRulesTests`, `SidebarRowGeometryTests`, `NavModelTests` for the owning section); the mounted facts are
`Wavee.ShellTests\NavPaneMountedTests.cs` (the registered template is Classic's, the real shell draws the pane through the key and
its Home row stands at the ladder's row height, and the kit plans with Classic's planner).

### The navigation pane's heads (T5b)

T5b gives the structure the pane's **heads**: whatever stands above or between the list's rows as chrome.

| Surface | Contract | Core side | Classic |
|---|---|---|---|
| Section header, its "+", the separator, the quiet count | none: they are part of a row, so they ride the row slot | the disclosure channel (`INavRowHost.ToggleSection`, `DisclosureOpen`), the create flow, the header menu | `Views\Nav\ClassicNavHead.cs` (`Header`, `InlineButton`, `Separator`, `ClassicNavCreateButton`, `ClassicNavCounts`) |
| Playlists filter box | `INavRowHost.FilterOpenFor`, `FilterQuery`, `FilterTicket`, `CloseFilter` | which section hosts it, the query signal, the focus ticket, Ctrl+F and the close rule (`PaneView`) | `Views\Nav\ClassicNavFilter.cs` (`ClassicNavFilterBox`), placed by the header slot |
| The empty body | `Shell.NavEmpty` (`NavEmptyProps`: `Pane` or `Rail`) | when the published row count is zero, and the presented compact flag | `ClassicNavEmpty` (the sentence, or the rail's bare spacer) |
| Your Library's head | `Shell.NavLibraryHead` (`NavLibraryHeadProps`: the host) | `LibrarySession` implements `INavLibraryHeadHost` (`Theming\Contracts\NavLibraryHead.cs`) over the model's `NavLibrary` | `Views\Nav\ClassicNavLibraryHead.cs` (`ClassicLibraryHead`, `ClassicLibraryChips`, `ClassicLibraryToolbar`, `ClassicLibrarySearchBox`) |

- **The head is drawn by the structure; the session decides.** Home, the page dropdown, the chips, the toolbar (and its Tab order,
  `SidebarLibraryHeadRules.TabOrder`), the breadcrumb, the retry banner, the empty band and the compact rail's form (Home, the
  search tile, the separator) are `ClassicNavLibraryHead.cs`. The model's session (`NavLibrary`: drill, shaping, facts, every
  verb), the head<->list focus ring's targets (`HomeNode`, `DropdownNode`, `SearchButtonNode`; the list's Up from its first row
  and the pane's pill rule read them), the pill flag and the menus' rows stay core, and the head reaches them only through
  `INavLibraryHeadHost`. The host is internal, so `NavLibraryHeadProps` keeps it internal (public record, internal member),
  as `NavRowKit.Slot` does for the row host. Every component keeps its hook order: the head still runs every hook before the
  compact early return, so the rail <-> pane switch never reorders them.
- **The collapse chevron is core's** (`Sidebar.Chevron`, in `Sidebar.UI.Rows.cs`): its spring rotation is the disclosure band's
  own `Reveal`, and the episode discussion draws with it too. The section-collapse animation, its persistence and the
  choreography flags (`_choreographed`) are the pane's and did not move.
- **`SidebarRowGeometry` shrank.** The header height, the separator's two numbers and the empty-hint height had no core reader
  left and were deleted; `HeaderTextX` (the Library lane rules) and `HeaderButton` (the Edit outline's inline buttons) stay
  until T5d / T5f. `Sidebar.SectionHeader.InlineButton` stays in `Sidebar.UI.Rows.cs` as a copy for the Edit outline (T5d deletes
  it). `PaneMetrics.HeadBandInset`, which nothing read, is gone. `SidebarGeometryParityTests` still pins every member core keeps.
- **Numbers** the heads carried as literals are `Rules\NavHeadLayout.cs` (`ClassicNavHeadLayout`); the Library lane's are core's
  `SidebarLibraryHeadRules`, which the pure tests pin.

**Tests.** The mounted facts are in `Wavee.ShellTests\NavPaneMountedTests.cs` (Classic registers both keys, the real shell in the
Library layout draws the head through its key with its ring, dropdown and toolbar standing, and the empty body is the sentence
or the bare spacer); the Library head's rules stay `SidebarLibraryHeadRulesTests`, `NavLibraryTests`.

### The navigation pane's flyouts and menus (T5c)

T5c gives the structure the **pictures** of the pane's flyouts; the menu **rows** (which commands exist, enablement, invoke) stay core.

| Surface | Contract | Core side | Classic |
|---|---|---|---|
| Collapsed section's flyout | `Shell.NavSectionFlyout` (`NavSectionFlyoutProps`: host, section id, close) | `PaneView.OpenSectionFlyout`: one flyout at a time (a second click closes it), anchor, `RightEdgeAlignedTop`, focus trap, light dismiss; `PlanSectionExpanded` | `ClassicSectionFlyout` (`Views\Nav\ClassicNavFlyouts.cs`) |
| Folder tile's flyout | `Shell.NavFolderFlyout` (`NavFolderFlyoutProps`: host, section id, root folder id and name, close) | `PaneView.OpenFolderFlyout`, the drill-in stack (`SidebarFolderFlyoutNav`, pure), the drop specs, `ExpandFolderInPane` | `ClassicFolderFlyout` (same file): panel, header and back chevron, page slide, the keyboard cursor and its keys |

- **Menus.** A row menu, a header "..." menu, the "+" menus, and the Library head's sort, options and pages menus are
  `ContextMenuModel` / `MenuFlyoutItem` rows built at open time by `Shell\Sidebar.UI.Menus.cs` (`SidebarMenus`, `NavMenuModel`); the
  engine's `MenuFlyout` draws them, and the slots already open them (T5a rows, T5b heads). So there was no menu picture left in
  core to move: menu placement, light dismiss, focus return to the row, arrows / Enter / Escape / Shift+F10, submenus, disabled rows
  and F2 rename are the engine control's and the row slot's, unchanged. `OpenPaneMenu` (the footer "...", no caller) stays with the rows.
- **The panels are the structure's.** The two props records carry the pane as `INavRowHost` (internal, so the member is internal;
  public record, internal member, as `NavLibraryHeadProps`). `INavRowHost` gained `PlanSectionExpanded`, `RailFolderDropSpec`,
  `IsRailDropActive` and `ExpandFolderInPane`; the panels read the rest (`Model`, `Navigate`, `SelectedRoutePeek`, `EntryMenu`,
  `FolderMenu`, `PayloadOf`, `ResourceDropSpec`, `MenuOverlay`) from it. Numbers are `Rules\NavFlyoutLayout.cs`.
- **Nothing was left to delete.** `SidebarRowGeometry`'s ladder members the flyouts read (`PitchOf`, `ArtOf`, `PaneEdge`) still have
  other core readers (the row primitives, the Edit outline, the drop rules), so the mirror shrinks with T5d / T5f.

**Tests.** `Wavee.ShellTests\NavPaneMountedTests.cs` pins that Classic registers both keys; the drill-in stack stays
`SidebarFolderFlyoutNavTests` (`NavDropRulesTests.cs`).

### The player bar (T6a)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Player.Bar` | `PlayerBarProps` (`PlayerBarView`, `PlayerBarMetrics`, `IPlayerBarHost`; `RightSlot`, `OverflowCommand`, `PrimaryVerb`, `NowPlayingInk` moved to `Theming\Contracts\PlayerBarVocabulary.cs`) | `Shell.PlayerBarHost` (`[CoreHost]`, `Shell\Shell.PlayerBar.Host.cs`) | `ClassicPlayerBar` (the dock, the three clusters, the buttons, the reserved slots, the "⋯", the volume, devices and remote-device faces) and `ClassicPlayerBarPodcast` (the ±10 s steps, the speed button and panel); numbers in `Rules\PlayerBarLayout.cs` |

**The host runs on the template's hook context.** The bar is one render over a lot of state (the five-way fold, the face hold,
the level machine, the saved-episodes demand, two deadline timers), and its hold commits in layout effects *after* the render
that read it. Splitting that across a host component and a child would change when the hold's model is read relative to those
effects, so the contract does not split the component: the template's first line is `props.Host.Use(ctx.Hooks)`, which runs the
core's hooks on the slot's hook context in the order the old component ran them and answers a `PlayerBarView`; the template then
draws that view with its own hooks (the hover signal, the captured nodes, the like pop, the face cross-fade). A theme that skipped
the call would draw a bar with no bookkeeping. The `Host` is also the seam for a test: a fixture host returns a fixed view and the
template draws it (`PlayerBarMountedTests`).

**The levels stay policy and the machine stays core.** `PlayerBarPolicy` (Classic's values in `ClassicPolicies`) is the single
source of the six levels, the 24 DIP narrowing hold and the bar's height; `Shell.PlayerBar`/`PlayerBarLayout` resolve them. T6a adds
what the podcast bar carries at each level to the same record (`PlayerTier.PodcastSlots`, `PodcastLeftW`, `PlayerBarSlots.Title`),
replacing the level-ordinal comparisons `Shell.PodcastBarLayout` made. `R.Space.DockReserve` (what a page reserves under its last
row) answers the active policy's height; the style table's own 72 is the value it equals in Classic.

**What stayed core, and why.** The seek rail with its chapter decoration, the time labels and the credits line are shared with the
stage and the on-media strip (or have no key yet) and arrive as builders on the props until T7 gives them keys (the device picker's
menu is `Player.DevicePicker` since T6b, below); the "⋯" rows (`OverflowItem`: labels and what a row does, built from live reads at
open time) and the key map (`PlayerKey`) are decisions.

### The right panel and the Player family's other views (T6b)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Shell.Panel` | `PanelProps` (`ShellFrameProps.Panel` replaced the `RailBody` builder) | `RailWiring.Panel` (`Shell\Rail.Wiring.cs`, `[CoreHost]`) over `Shell.PanelHost`, `Rail.VideoCoupling`, `Video` | `ClassicPanel` (`Views\Panel\`): the frame component (surface, edge, the 300 ms slide), the header and its glyph buttons, the arm of each mode, the docked video cap and its splitter, the Art \| Video switch |
| `Panel.Friends` | `FriendsProps` (`IFriendsFeed`: the rows and state with their tables subscribed) | `RailWiring` (the feed, the clock, the retry seam, the profile verb) | `ClassicFriends`: the rows, the skeleton, offline, error (Retry only when the host can retry) and empty faces |
| `Player.NowPlayingHero` | `NowPlayingHeroProps` | `RailWiring` (the playing track and cover, the deck, the entity subscriptions, the demand) | `ClassicNowPlayingHero`: the pinned tile (header strip, cover or the hosted deck, the art menu) and the now-playing body under it (credits, About, cities, merch, Next up) |
| `Player.DeckStyleFlyout` | `DeckStyleFlyoutProps` (`IDeckStyleVerbs`) | `Shell.DeckPrefs` behind `RailWiring`'s verbs object | `ClassicDeckFlyout` and `Rules\DeckThumbnails.cs`: the preset shelves, the option rows, the swatches, the twelve mini-arts |
| `Player.DevicePicker` | `DevicePickerProps` (`DevicePickerView`: `DevicePickerEntry` rows and a stamp; built per open) | `Shell.DevicePicker.Entries`, `RailWiring.ReadDevicePicker`; the overlay stays the bar's `ToggleDevicePicker` | `ClassicDevicePicker`: the menu over the entries, scrolled to the window |
| `Player.OnMediaTransport` | `OnMediaTransportProps` (`OnMediaFacts`; a surface adds its idle signal and chrome feed) | `OnMedia.Transport` mounts it (`RailWiring.OnMediaProps`); `OnMedia.MoreMenu` and `FadeChrome` stay | `ClassicOnMedia`: previous / play / next, the seek rail, the times, mute, the volume popup, "⋯" |

**The state machine and the coupling stay core.** `Shell.PanelHost` (open, mode, committed width, floats, the docked video's height and pin),
the width and fit rules (`PanelPolicy`, `Shell.ClampRailWidth`, `Shell.CanFitRail`, `PanelHost.FitWidthTo`), the rail <-> docked-video
coupling (`Rail.VideoCoupling`) and the seam's splitter (the frame places it: `ShellFrameProps.RailSeam` stays a builder, like the
sidebar's) are unchanged. The panel view reads ONE decided `PanelView` per render (`PanelProps.Read`): open, committed width, floats,
the body after the page stage's substitution (`BodyModeFor`), whether the docked card is up, and what occupies the now-playing arm's
pinned slot (`Rail.VideoCoupling.PinnedFor`: the tile, the docked video, or the tile with the switch). `PanelPolicy.FullHeight` is on the
view for a theme whose panel spans the window; Classic's is `false` and nothing in Classic's panel reads it.

**The frame is a component of the theme.** `ClassicPanelFrame` is the old rail frame moved whole: the slide is a layout effect on its rendered
root with a seeded-once flag (no fly-in at startup, a rapid reversal continues from the presented position), the surface fill and the
body-height report are built once per instance, and the arms are the same elements in the same positions, so a mode switch reconciles exactly as
before. Opening and closing is ONE `TranslateX` track, never an opacity track; the docked surface paints nothing (the frame's reservation band is
the one coat) and the floating one paints its own ring and elevation.

**The deck is hosted, not built.** The hero places `props.Deck` under the keyed wrapper (`"cover"` or `"<slug>@<side>"`), so a style flip
re-mounts the deck exactly as it did and a mode switch re-mounts nothing the deck owns. Which face paints is the core's decision
(`Rail.PlayerPrefs.ResolvedPresentation`, developer-gated as today). Every write goes through `IDeckStyleVerbs` with the surface that made it.

**Builders other lanes still own.** The lyrics header (T7b, not yet a key; the body is `Lyrics.View` since T7b, mounted by the core builder `Lyrics.View()`), the video body (T7c), the queue body (T7d), the docked video card, the
lyrics peek and the "Next up" rows arrive as `Func<Element>` members on the props; their keys replace them.

**Tests.** The pinned slot, the mode vocabulary and the picker's entries are pure (`Wavee.Tests\PanelContractTests.cs`); the mounted facts are
`Wavee.ShellTests\PanelMountedTests.cs` (the registered templates are Classic's; the real shell draws the hero's cover at the panel, a mode switch
keeps it, a narrow window floats the panel; `ViewHarness` mounts the panel with fixture bodies, the friends faces, the picker and the strip).

### The track table's rows (T8a)

A virtualized list is not a view a theme can hand back as an `Element`: recycling, selection, drag, the keyboard and scroll anchoring
are core behaviour. So the table is a **style**, not a view: the structure registers a `TrackListStyle` with
`views.AddStyle(StyleKeys.TrackTable, ...)` and the core host builds its rows from it. T8a is the rows' half of that record; T8b adds
the header, the command and list bar, the filter card, the selection bar and the placeholder, T8c the eager and episode rows, the
surface, the artist chart and the drawer.

| Key | Contract | Core half | Classic |
|---|---|---|---|
| `StyleKeys.TrackTable` (`StyleKey<TrackListStyle>`, id `Track.Table`) | `TrackListStyle` (`Stamp`, `Modern`, `Classic`: each a `TrackRowTemplates` = the bound grid + the skin); `TrackRowSkin` is what the core wires for a skin | `Track.TableHost` and its slot components `TableSlot` and `TableRowContent` (`Entities\Track.Table.cs`), `Track.BoundRow` / `Track.RowPresentation` / `Track.RowCellRules` (`Entities\Track.UI.Bound.cs`) | `ClassicTrackTable` (`Themes\Classic\Structure\Lists\`): both pairs; numbers and the two motions in `Rules\ClassicTrackRowLayout.cs` |

- **Two templates, one per row style.** The core picks `Modern` or `Classic` by the table's `ColumnSet.Classic` (the `detail.rowstyle`
  setting, read into the snapshot); M2 deletes that flag and the pick becomes the structure's own option. Classic's two pairs share one
  body per cell and branch on a `classic` argument exactly where the source did, so each style draws the tree it always drew.
- **The grid.** `templates.Grid(row, in set, tracks, rowH, art)` runs once per slot per shape from `TableRowContent` (the code that
  called `Track.BoundGrid`): every per-item value is a bound `Prop` over `row.Presentation`, frequent flips are `Visible` binds, the
  rare heavy branches (spinner, equalizer, marquee, the withheld # cell) are `Flow.Show`. A recycle rebinds and builds no element.
  A template reads the slot only through `Track.BoundRow` and never names `Playback`, `Entities` or `Store` (WVTH002): the deck's
  playing flag is `row.Playing`, the clock is `Track.NowUnix`, the adder's name is `Track.AddedByLabel`.
- **The skin.** `TableHost.Skin` keeps the behaviour and builds a `TrackRowSkin`: the drag source and the .mp4 drop target, the tap /
  double-tap / Enter / Space / Alt+arrow handlers, the focus and hover edges (the hover edge pauses the equalizer), the selection
  check lane, and the live facts the fills read (`IsSelected`, `Cue`, `Odd`, `ChecksVisible`, `Accent`). The template draws the box
  (fills for rest / hover / pressed / selected / the drop cue, the zebra border, the corners, the pill, the divider, the mount
  entrance and the check lane's shift) and applies every wired member; the core attaches the context menu around what it returns.
  A template must keep the box `Focusable = false` (the list owns the one roving tab stop) with the Button role.
- **What stays core.** Which rows exist, the track / episode switch, the reveal gate and the shimmer row, the presentation memo and
  its equality gate, the like-edge latch, the drawer (`DrawerBox`, T8c), column sets and widths (`ColumnSet`, `TracksFor`, the relief
  ladder), row height and density, the selection model, the keyboard map, the menu and the pure decisions (`RowCellRules`, pinned by
  `TrackRowCellRulesTests`). The cell wrappers and navigation the eager row shares (`Track.RowCenterCell`, `MoreCell`,
  `GoToArtistAt`, ...) are `internal` on `Track` until T8c moves that row.
- **Not yet the engine's typed channel.** EP6 (`ItemsView.CreateBound` over `BoundListProps`, a stamp-driven rebuild of every slot, the
  FGTH003 ban on components in a row template) wants the whole slot as a plain tree. The table's slot is two components today
  (the kind switch and the open drawer; the reveal gate, the presentation memo and the like latch), and folding them would add binds
  and `Flow` boundaries to every row. T8a therefore moves the picture and leaves the slot as it is: the per-row cost on a scroll
  frame is unchanged (no extra component, no extra closure per bind). `TrackListStyle.Stamp` is carried for the PR that folds the
  slot; until then a row-style change re-keys the list (`list:<scroll>:d<N>:classic|modern`), as before.
- **A host without a registry.** A table mounted where no registry provides `StyleKeys.TrackTable` logs once
  (`track table: no TrackListStyle is registered`) and draws bare containers. A test that mounts a table outside the real shell
  provides Classic's registry (`new ClassicStructure().Register(builder)`, `Ctx.Provide(ViewRegistry.Context, ...)`), as
  `TrackRowRecycleAllocationTests` does.

**Tests.** The registration and the two template pairs are `Wavee.Tests\Theming\TrackListStyleTests.cs`; the recycle budget on the real
table (a scroll re-renders no row component and allocates at most 7 KB per recycled row) is `TrackRowRecycleAllocationTests`; the
mounted facts in both row styles (the skin's pill and divider, the number and title lanes, the row height from the style's ladder)
are `Wavee.ShellTests\TrackRowsMountedTests.cs`; the re-keying on the setting, the density and the hidden art stay
`LiveOptionBehaviourTests`.

### The track table's chrome (T8b)

The other half of `StyleKeys.TrackTable` (no new key): `TrackListStyle.Chrome` (`TrackChromeTemplates`, `Theming\Contracts\TrackChromeStyle.cs`) is the
column header, the command bar's pictures, the filter card and the placeholder row. Classic: `ClassicTrackChrome`
(`Themes\Classic\Structure\Lists\`), numbers in `Rules\ClassicTrackChromeLayout.cs`.

| Piece | Contract | Core half | Classic |
|---|---|---|---|
| Column header | `TrackHeaderTemplate(TrackHeaderArgs)`: the lanes, the shared tracks, the sort and its signal, the check lane, the hero arm, `OnSort(column, artistColumn)` | `TableHost.TableHeader` (reads shape, sort and checks, so it re-renders on exactly those), `SortBy` (`TableRules.NextSort`) | the keyed cells, the sort caret (a spring) and the title label's word swap as components over the sort signal, the hairline, the check-lane shift |
| Command bar | `ITrackCommandKit`: the lane (`Surface`), a measured slot (`Command`), `Bar`, `PlayNext`, `Labeled`, `IconButton`, `Separator`, `Chevron`, `Trail`, `SortCaret`, `FilterCommand`, `FilterFunnel`, `MoreSlot` | which commands exist and fit (`CommandBarLayout`), the bar's hold while the filter card is open, every overlay and its focus, the "…" menu's items, the search field | the 32-DIP buttons, the split button's parts, the count badges, the bar's two motions |
| Selection bar | the same `Surface` (mode `selection`) around `Controls.SelectionBar` | the live selection, the lane's commands, exit and select-all | the lane and the browse / selection swap motion |
| Filter card | `TrackFilterCardTemplate(TrackFilterCardArgs)`: title, status (the filtered counts, already worded), `Reset`, `TrackFilterFacet` rows (signal, options, commit, `On`) and `TrackFilterToggle` rows | `TableFilterFlyout`: which facets the list can earn, the signals, what each commit writes, the sync from external filter writes | the 360-wide card, the 44-DIP facet rows with the accent-tinted combo, the switches, the scroll fallback |
| Placeholder | `TrackPlaceholderTemplate(in ColumnSet, tracks, rowH, padX)` | `TableHost.PlaceholderRow`: the count, height and padding are the live row's, so landing data never jumps | a `GridEl` of empty boxes |

- **Templates run inside the core component's render.** The header, the filter card and every button are drawn while the core
  component renders, so the signals they read subscribe that component exactly as before; no key, motion, hit-test or tab order changes.
- **Still core.** The search disclosure's field (`TableSearchHost`, a focus and caret machine; it calls the kit for its glyph button),
  the row-size slider's panel (M3 moves it with the option write), `Controls.SelectionBar` and the shared selection lane (shared
  controls), `Track.TableHeaderShim` and `Track.ShimmerRow` (host-free statics the library panes and the artist reader call with no
  registry; T8c / T10b move them with their callers), and the chrome stack's own arrangement (`Chrome(...)`, whose sticky clip the
  hero system measures).
- **A host without a registry** draws the placeholder as `Track.ShimmerRow` and every chrome piece as an empty box (logged once with the rows).

**Tests.** The chrome's registration is `Wavee.Tests\Theming\TrackListStyleTests.cs`; the mounted facts in both row styles (the keyed header,
the browse-mode command lane, the "…" slot) are `Wavee.ShellTests\TrackChromeMountedTests.cs`.

### The fullscreen stage's frame (T7a1)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Stage.Root` | `StageRootProps` (`Mount`), drawn from a `StageRootView` | `Stage.SurfaceCore` (`Shell\Stage.UI.cs`), mounted through `ShellFrameProps.StageRoot` (replacing the `StageView` builder) | `ClassicStageRoot`: the root box, the layer stack, the chrome's placement and its Enter/Exit, the smoke, the hairline |
| `Stage.TopBar` | `StageTopBarProps` | the stage context's verbs (`SetMode`, `OpenOptions`, `Exit`, `OverControls`) | `ClassicStageTopBar`: brand, the mode `SelectorBar`, the options glyph (the TeachingTip's anchor), exit |
| `Stage.Transport` | `StageTransportProps` (`Facts`, `Verbs`, the bar's seek and time pieces) | `Stage.TransportFacts` and friends (`Shell\Stage.Frame.cs`) | `ClassicStageTransport`: the card, the seek row, the clusters, the mute button, the volume slider, the device button |
| `Stage.Options` | `StageOptionsProps` (`Verbs` is an `IStageSettingsVerbs`) | `Stage.OptionsContent` (the opener re-provides the stage context in the overlay layer), `SurfaceCore._openOptions` | `ClassicStageOptions`: the tabs, the visualizer grid, the layout rows, the hero options |
| `Stage.Gallery` | `StageGalleryProps` | the root mounts it under the chrome's Show; `Visualizer.Gallery.UI.cs` keeps the words | `ClassicStageGallery` (+ `ClassicStageGalleryTiles`): the pane, the live/poster tiles, the settings rows |
| `Stage.Caption` | `StageCaptionProps` (what one render of the host decided) | `Stage.CaptionHost` (`Shell\Stage.Caption.UI.cs`): the playhead clock, the edge wake, the render-thread wipe, the dots' driver | `ClassicStageCaption`: the veil, the keyed previous/active/next slots, the bloom and the dots |

**The surface stays a component; the structure draws its root.** The stage is one component with hooks that must run in a fixed order
whatever the theme (the window and the focus scope are `ImmersiveService`'s, the idle machine and the cursor, the ONE layout signal and its
aspect classes with their hysteresis (`StagePolicy.Aspect` -> `Stage.Layout.ClassOf/Resolve`), the preferences mirrored into `Stage.StageCtx`,
the accent, the key map: Escape / F11 close, `[` `]` step, `G` the gallery). So the frame mounts `Stage.Root` and the template is
`props.Mount(Draw)`: the core component calls `Draw` (a static method, so its delegate is cached) with a `StageRootView` each render and
mounts what comes back under the stage context. The shell body's immersive collapse (bound `Visible` false on the chrome row, the content region and
the dock) and the open/close choreography (`Stage.EnterTerminal` / `ExitTerminal`) are the frame's and did not move.

**The stage context travels as a core object.** `Stage.StageCtx` is the stage's ambient signal bag (mode, layout, accent, palette, the chrome flag, the
preference mirrors) plus its verbs as actions. It names no `Shell` type, so the props carry it; what a theme may not name (`Prefs`, `Playback`, `Shell`)
arrives as facts (`StageTransportFacts`, `StageDeviceFacts`, read inside the template's render so a signal read subscribes it), as verbs
(`IPlayerVerbs`, `IStageSettingsVerbs`: one `Prefs.Stage` setter each, the sync offset also pushes the audio graph's spectrum offset, a face pick also
writes the `viz.pick` line) or as builders (the bar's own seek rail and time labels, the device picker's rows).

**Layers other lanes own arrive as builders.** `StageRootView.Layers` carries `Func<Element>` over the core components of the pane host (the lyrics pane inside
it mounts `Lyrics.View` since T7b, below, and the queue pane is T7d's), the caption host, plus the zero-size machinery (lyric facts, hero facts, the visualizer clock) the template must still
place. The backdrop, hero photo, face, scrim, spectrum, now-playing card, identity and artist name were builders here until T7a2 gave them keys (below). The root
keys and orders them exactly as the stack always was.

**Numbers.** `Rules\StageGeometry.cs` holds the frame's look (the chrome's Enter/Exit offsets, the top bar's selector and brand, the transport card's paddings,
glyph and disc sizes, the volume slider's alphas); `StageGeometry.Options.cs`, `.Gallery.cs` and `.Caption.cs` hold their views'. The allocator's
authored extents (`Stage.Layout.TopBarH`, `TransportDesktopH`, `Pad`, the gallery column, the caption block) stay in `Stage.Layout`: the core budgets
the face-safe rect, the caption and the pane from them, so the views read them by name. The aspect thresholds are `StagePolicy`'s.

**Polarity.** The stage faces' own dark flag (`StageInk.IsDark`; T7a2 re-points the faces) is read by the core palette derivation only
(`Stage.StageCtx`'s seed palette and `SurfaceCore.PublishAccent`: polarity as an argument, not a colour); the moved views take rungs from the stage-arm
table (`StageInk.Ink`, `GlassHover`, `Card`, `SelectedTileWash`, ...) and never ask which arm is live.

**Tests.** `Wavee.Tests\StageFrameContractTests.cs` pins the keys' ids, Classic's registration and the props' cached equality;
`StageSettingsVerbsTests.cs` pins what the option surfaces write (each verb persists and bumps the stage epoch once, a picked face survives other writes);
`Wavee.ShellTests\StageFrameMountedTests.cs` opens the real stage under Classic and checks the registered templates, the root's stack and the chrome's
nodes, then mounts the top bar, the transport card, the options popover and the gallery alone under `ViewHarness` with fixture props.

### The fullscreen stage's layouts (T7a2)

The four layouts (Card, Large art, Centered, Artist) stay a **core choice** (owner Q16: every theme draws all four): which layout is drawn
(`Stage.Layout.ClassOf/Resolve`, `Stage.LayoutRules`, `Stage.Look`), the allocator's authored extents (the cover's rect, the titles' fonts and widths, the strip
and ring, the face-safe rect, the pane's rect: `Stage.Layout`) and `StagePolicy` are core. What a theme draws is each layer **for the look the core decided**, in
every aspect class (Desktop / Compact / Portrait / Ultrawide).

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Stage.Backdrop` | `StageBackdropProps` (`Shown`) | `Stage.BackdropProps` (`Shell\Stage.Layers.cs`) | `ClassicStageBackdrop` (+ `ClassicStageBackdropArt`): the baked cover (old kept under new), the base field, the scrim; one quarter-scale repaint boundary |
| `Stage.Hero` | `StageHeroProps` (`PrefetchDue`) | `Stage.HeroFacts` (the lookups) | `ClassicStageHero` -> `ClassicStageHeroLayer` / `ClassicStageHeroPhoto`: the Artist layout's photo (Ken Burns on the render thread, the cross-fade on a new artist, the hidden next-track decode) and its two scrims |
| `Stage.ArtistName` | `StageArtistNameProps` (`Name`) | `Stage.ArtistNameProps` | `ClassicStageArtistName`: the eyebrow and the huge name |
| `Stage.Face` | `StageLayerProps` | the face pick, `Visualizer.Face` | `ClassicStageFace`: the face on a keyed child (per kind and 8 DIP width bucket), inset while the gallery is open, told its safe rect |
| `Stage.FaceScrim` | `StageFaceScrimProps` (`Alpha`) | `LayoutRules.BackdropScrim` | `ClassicStageFaceScrim`: the veil over a backdrop face |
| `Stage.Spectrum` | `StageLayerProps` | `Stage.Layout.StripRect/RingBox` | `ClassicStageSpectrum`: the strip or ring in its own repaint boundary |
| `Stage.NowPlayingCard` | `StageNowPlayingCardProps` (`Shown`) | `LayoutRules.ShowsNowPlayingCard` | `ClassicStageNowPlayingCard`: the acrylic card behind the Card thumb |
| `Stage.Identity` | `StageIdentityProps` (`Title`, `Meta`, `Chips`, `NowPlayingMenu`, `OpenArtist`) | `Stage.IdentityProps`, `MetaFacts`, `ChipFacts` | `ClassicStageIdentity` (+ `ClassicStageMetaLink`, `ClassicStageChips`): the cover (`stage:cover`, the **bounds spring**) and the title column (`stage:titles`, the position spring) |
| `Stage.ArtistPane` | `StageArtistPaneProps` (the facts, the demands, the navigation verbs) | `Stage.ArtistPaneProps`, `Stage.ArtistFacts` (`Shell\Stage.Artist.cs`), `Stage.ArtistRules` | `ClassicStageArtistPane`: the header band, About, This song, Popular, Where people listen, Fans also like |

**The layers read the stage context's signals inside their own renders.** `StageLayerProps` and its siblings carry the `Stage.StageCtx`; a template reads
`Layout`, `Look`, `Mode`, `Kind`, `Accent`, `Palette`, ... itself, so a layer re-renders on exactly what it read (a track change re-renders the identity and the
backdrop, never the face). What a theme may not name arrives as a predicate or a fact the template calls inside its render, and a signal read there subscribes
that layer: `Shown` / `Alpha` / `PrefetchDue` / `Name` / `Title` are `Func`s, the identity's meta line and chips are facts read by their own leaf components
(the format / device / owner signals re-render the chips row only), the Artist pane's sections each call one fact (`Header`, `About`, `Song`, `Popular`,
`Cities`, `Fans`) that reads its own tables. The facts are the old components' render bodies, moved verbatim: the same reads in the same order. The props are
built once per stage context (the pane host caches the pane's), so an equal record re-renders nothing.

**Motion is the stage's, unchanged.** The cover's bounds spring (`SizeMode.ScaleCorrect`) and the titles' position spring are the layout switch's morph; the
face is remounted per kind and width bucket under a 0.55 s decelerate; the hero photo cross-fades over the old one and is keyed by the artist slot; reduced
motion rests the pan (`Design.Reduced`) and keeps the fades. A null or empty fact is "no section": the Artist pane shows "Nothing playing" without an artist, an
empty scroller while the facts land, and a section that has nothing takes no space.

**Numbers.** `Rules\StageGeometry.Layers.cs` holds the identity's chips and cover corners, the card's entrance and the artist name's eyebrow;
`StageGeometry.Artist.cs` the pane's base DIPs (times `Layout.QueueScale`, rounded by `ClassicStageArtistKit.Px`). The decisions (two columns from 1100 DIP, the
header's height, the reading measure, how many avatars fit) are `Stage.ArtistRules`, which the pane calls.

**Tests.** `Wavee.Tests\StageLayerContractTests.cs` pins the keys' ids, Classic's registration and the facts' value equality;
`Wavee.ShellTests\StageLayersMountedTests.cs` opens the real stage and mounts the identity (the cover at the allocator's size, the bounds spring on a layout
switch), the spectrum, the backdrop and the Artist pane (nothing playing, no facts yet, facts, the demand once per artist and track) under `ViewHarness`.
`LiveOptionBehaviourTests` keeps pinning the spring on the real stage.

### Lyrics (T7b)

| Key | Props / style | Core half | Classic template |
|---|---|---|---|
| `Lyrics.View` | `LyricsViewProps` (`Host` is an `ILyricsHost`; `Message`, `Body`, `Ticker`, `Debug`, `Footer`) | `Lyrics.ViewCore` (`Shell\Lyrics.UI.cs`, `Lyrics.Host.UI.cs`; the rail's body, the stage's pane and the podcast reader mount it) | `ClassicLyrics` (`Views\Lyrics\`): the layer stack, the breathing dots, the resync pill and its ring, the "no sync during video" note, the AI disclosure's place under the layers, the stage's capped left-anchored column |
| `StyleKeys.LyricsLines` | `LyricsLinesStyle` (`Message`, `Shimmer`, `Skeleton`, `Unsynced`, `Lines`, `Peek`) | the host calls it where a document is on screen | `ClassicLyricsLines` (`Lists\`): the state line, the loading bars, the unsynced block, the measured virtual list and its rows (`ClassicLyricsRow`), the podcast peek and its gate |

**The model and the driver stay core.** `LyricsModel` (state, document, the upgrade gate, the clock, the follow and resync machine, the interlude
reserve, the second line, the AI state, the verbs), `Lyrics.BlurPolicy` / `Lyrics.Prefs.BlurStrength(onStage)` and the frame driver in `ViewCore`
(the media clock, the active-line latch and the staggered cascade, the depth-of-field ramp, the word wipe and its render-thread keyframes, the halo
fades, the motion demand, the ticker and stepper, the node handles they write) are untouched; so is the stage's `PaneHost` (`PaneFrame`, the
KeepAlive, the keyed pane) which still mounts `Lyrics.StagePane`. The core host builds a `LyricsViewProps` each render and mounts
`Slots.View(ViewKeys.LyricsView, props)`; its `DocHost` (the store sync and the skeleton region) and the transcript host call the lines style for
a picture and decide which one: a timed document that is not suppressed by a video is `Lines`, any other `Unsynced`, a reader browsing an episode
that is not playing `Peek`.

**What a theme reaches.** The WVTH002 fence applies: the template and the rows read the core only through `ILyricsHost`, the mount config and facts
(`Large`, `OnStage`, `TypeScale`, `Ink`, `Accent`, `Band`, `Metrics`, the podcast flags), the live signals a row reads (`EmphasisOf(i)`, `GlowOf(i)`,
`SecondaryLine`, `HaloScale`, `Now`, `Follow`), the per-line reads the frame driver owns (`DofShownFor`, `SoftnessOfLine`), the node reports
(`ReportLineNode`, `ReportGlowNode`, `ReportDofNode`, `OnDotsRealized`: the driver writes those nodes by handle) and the verbs (`SeekToLine`,
`Resync`). A read inside a template's render subscribes THAT render, as it did in the old components: a row re-renders on its own emphasis shape,
the dots on their flag, the pill on the follow state. `host.EnsureLayout()` hands the list the one `MeasuredLayout` the follow and the dots also read.

**Blur.** The depth blur is the core's: `Lyrics.Prefs.BlurStrength(OnStage)` resolves the stored strength (the stage's is ALWAYS 0; the rail's follows
the setting) and the frame driver ramps every line's σ toward it; a row only carries the σ the host says (`DofShownFor`) and the active line's halo
scale (`HaloScale`, which follows the user's setting on the stage too). `LyricsLinesStyle` has no `StageDepthBlur` yet: that field and the 3-argument
`BlurPolicy.ResolveFor` belong to M14/W4c.

**Motion is the surface's, unchanged.** The row keeps its mount-once fields (the opacity spring retargeted by a signal effect, the scale springs keyed
by the active flip and reduced motion, the frozen seed opacity), the wipe/glow/σ/cascade handles are the same nodes in the same places, the
pill, dots and note keep their Enter/Exit. The ink mapping (`Lyrics.Ink`) stays core: the AI footer and the transcript reader share it, and it
becomes `Roles.Lyrics*` with the colour-role pass.

**Not moved yet.** The panel's lyrics header (`Rail.LyricsHeader`: the globe, the AI sparkle, expand, the inspector button; it shares
`Rail.HeaderButton`) and the now-playing panel's two-row peek (`Lyrics.NpvPeek`) are still core builders (`PanelProps.LyricsHeader`,
`NowPlayingHeroProps.LyricsPeek`).

**Numbers.** `Rules\ClassicLyricsRules.cs`: the column's cap and gutter, the state line, the note, the pill, the peek's meta line, the dots' entrance,
the stage's accent pill and the secondary weight. The metrics (`Lyrics.Surface`: row type and rhythm, the focal band, the halo sigmas, the loading
bars) are the core's and the views read them by name.

**Tests.** `Wavee.Tests\LyricsViewContractTests.cs` pins the keys' ids, Classic's registration and the style's members;
`Wavee.ShellTests\LyricsViewMountedTests.cs` opens the real rail on lyrics (nothing playing: the centred line; a fixture track: the keyed rows and
the layer overlays) and the real stage's column, and `LiveOptionBehaviourTests.Lyrics_blur_strength_reaches_the_rail_lines_and_never_the_stage`
keeps pinning the blur.

### Home (T10a1)

Home is the structure's picture over core hosts. The page, the facet row, the daylist card and its clock, the zone bodies, the "See all" drill
page and the customizer are registered Classic views; the facet machine, the screen model, the load verdict, the shell-tint claim, the daylist's
timers and the layout document stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Home` | `HomePageProps` (the published facet, the scroll handle and key, the facet row's props, the switch signal, the pending / failed / content / seed Funcs, the wash element) | `HomeScreen` (`[CoreHost]`, `Home\HomeScreen.cs`) over `HomeHost`: the effects, `Compute`, `Verdict`, `Seed`, `BuildWash` | `ClassicHomePage`: the wash over the `ScrollView` column (facet row slot, the sticky-clipped content in a `SkelRegionEl`), the keyed facet content over the dim host |
| `Home.FacetRow` | `FacetRowProps` (words and switch as read-only signals, `Index` as a `Func<Signal<int>>`, the select / dismiss / following-memory verbs) | `HomeHost.RowProps` (one record per tab) | `ClassicHomeFacetRow`: the `SelectorBar` in the facet-title style, the Following toggle, the busy bar and the failure bar, pinned as a transparent band |
| `Home.Daylist`, `Home.DaylistClock` | `HomeDaylistProps` (zone, eyebrow, `Pauses`, the keyed clock element), `HomeDaylistClockProps` (a face, the daypart, the lines, the live countdown / elapsed / segment signals) | `DaylistCard`, `DaylistClock`, `DaylistCountingClock` (`Home\Daylist.Host.cs`): the subscribing reads, the window phase, the rollover ladder, the clock mode, the 1-Hz tick | `ClassicHomeDaylist`: art, veil, copy, action row, tag line; `ClassicHomeDaylistClock`: the ring, the two lines, the five-segment timeline |
| `Home.Zone.<Kind>` ×14 (`HomeZoneKeys.TryKeyOf`) | `HomeZoneProps` (zone, overlay, the shelf pager's controller) | none: a pure function of the zone | `ClassicHomeZones.BodyTemplate` under every kind: shelves, release list, cluster cards, browse tiles, episode rows, the empty facet |
| `Home.Zone.RecentGrid`, `Home.Charts` | `HomeRecentsProps` (cells, week, `Playing`, `RemoteDeviceName`), `HomeChartsProps` (the pick) | `RecentGrid`, `ChartsBlockView` (`Home\Zones.Hosts.cs`): the Recents subscription, the Featured Charts demand | `ClassicHomeRecents`, `ClassicHomeCharts` |
| `Pages.HomeSection` | `HomeSectionProps` (total, scroll key, the pending / failed / empty Funcs, the card source, the adapter) | `SectionScreen` (`Home\SectionScreen.Host.cs`): the demand, the table sync, the masthead publication | `ClassicHomeSection`: the "N items" line, the skeleton region, the bound `ItemsView` over `GridFit` |
| `Pages.HomeCustomize` | `HomeCustomizeProps` (the `LayoutDoc`, the `Reorderable`, the toggle / move / reset verbs) | `CustomizeScreen` (`Home\Customize.Host.cs`) over the one `HomeLayout` | `ClassicHomeCustomize`: the header with Reset, the reorderable rows |

**The chapter is the page's, the body is the key.** A zone's header, its sticky pin, its clipped body, the shelf pager in the header's tools and the
entrance row are the Classic page's structure (`ClassicHomeZones.Column`, `Chapter`); what another theme swaps per kind is the BODY. A shelf's
chapter owns the pager controller (`ClassicHomeShelfChapter`) and hands it to the body slot, so the header and the shelf share it as before. Kinds
that are a pure function of their zone share one template under 14 keys; the daylist and the Recents grid read core state and keep a core host that
mounts their key.

**Skeletons.** A slot is a component, and the skeleton deriver cannot run a component's render, so the zone column attaches a `SkeletonProxy` to every
slot and core host it places (`ZoneSlot`, the daylist, the Recents grid, the Charts block): the same static builder the template runs, at neutral live
facts. The page's `SkelRegionEl` still derives the shimmer from the real zone column over the seed, so a cold load matches the settled heights.

**What a Home view may call.** The WVTH002 fence applies (no `Shell`, `Playback`, `Entities`, `Spotify`, `Store`, `Prefs`). Navigation and playback verbs
are the static `HomeVerbs` (`OpenRecents`, `OpenBrowse`, `OpenCustomize`, `OpenTag`, `ShuffleContext`, `SeeAll`, `OpenChart`); the shared card
helpers (`HomeCardNav`, `HomeCards`, `HomeModules`, `Controls.*`) are called directly, as every Classic view does. The pure rules stay core and are
called by the views (`FacetForm`, `FacetDimPlan`, `DaylistForm`, `DaypartTimeline`, `ShelfLead`, `RecentsPlan`, `EmptyFacetCopy`); there is no raw width
threshold in a Classic Home view (WVTH013), the existing ones are those rules and the engine's own auto-fill.

**Numbers** (the plan's P22) are `Themes\Classic\Structure\Rules\ClassicHomeRules.cs`: the pinned band's heights, the chapter's sticky geometry, the grid
minimum columns and caps per body, the Recents tile pieces, the lead span, the daylist card's floor, art focus and copy insets, the drill page's card
minimum and the customizer's row.

**The customizer's reorderable.** A drag asks for a re-render of the component that built the rows, so the VIEW (the slot's own component) gives the
host-owned `Reorderable` its scene, its render request and its row extent each render; the host only owns the behaviour object.

**Tests.** The ids, the zone-kind map, Classic's registration and the chapter arithmetic are `Wavee.Tests\HomeContractTests.cs`; the facet machine, the dim plan
and the layout document stay `HomeHostTests`, `FacetDimPlanTests` and `LayoutFileTests`. The mounted facts are `Wavee.ShellTests\HomeMountedTests.cs`: Classic is
the registered template of every key, the real shell draws the page (column, pinned band, zone chapters), the band is the rule's height and the failure bar
follows the phase, and the customizer's rows stand in document order at the row height.

## 3. Options

An option is data: a `ToggleOption`, `SelectOption<TEnum>`, `SliderOption`, `ColorOption`, `ActionOption` or
`PrefReferenceOption` in a `ThemeOptionSchema` of the structure, the style or the composition.

| | |
|---|---|
| **Where a view reads it** | `ctx.Options.Get(option.Key)` (a cell; the view re-renders when it changes) |
| **How a view asks for a change** | `ctx.OptionRequests.Set(option.Key, value)` → `ThemeHost.Apply` |
| **Live or transaction** | `ApplyMode.Live` writes the cell (and re-applies the definition live); `ApplyMode.Transaction` also remounts the root |
| **Structure parameter** | `Visibility = OptionVisibility.Hidden`: never shown, never persisted; a composition sets it through `CompositionEffect.StructureParameters`; WVTH011 needs the probe matrix to exercise two values |

### Storage

`ThemeOptionStore` keeps one flat setting per option: **`theme.<themeId>.<layer>.<optionId>`** (layer is `structure`,
`style` or `composition`) through `Platform.Settings`. Toggle = bool, select = int (the enum value, so appending a member
never re-means a stored value), slider = float, colour = int (packed ARGB). The value rules (BP 1.6): a stored value that
is the wrong type, outside the slider range, no longer a choice, or belongs to a renamed option reads as the default and is
**left in place**. Only user options are read and written; parameters and options a composition hides or replaces always
answer the composition's default.

`ThemeOptionStore.Load(theme)` reads all three layers; `ThemeOptionStore.ResetOptions(themeId)` forgets a theme's option
keys (its UI state and other themes stay). `ThemeState` keeps small UI state (a panel width, the last tab) under
`theme.<themeId>.state.<key>`, clamped to the key's range on read and write.

### Apply and switch (a cut, until A4.2)

`ThemeHost.Switch(id)` persists `theme.id`, activates the composition (new option store, facts, registry), applies its
definition when it differs from the installed one, and bumps `ThemeHost.RootEpoch`. `ThemeHost.Apply(OptionChange)` validates the
change (an invalid value, an unknown option or a parameter throws `ArgumentException`), publishes it, republishes the facts
and re-applies the definition; a Transaction option also bumps the epoch. There is **no freeze, snapshot or dissolve**: the
engine's theme transaction (A4.2) replaces exactly these two paths later. Nothing is carried across a switch (owner Q26).

`ThemeRoot.Wrap` (called by both `RootHost` and the headless root through `Shell.RootFrame`) provides
`ViewRegistry.Context`, `OptionStore.Context` and `ThemeFactsContext.Signal` around the frame, and renders the frame in an
alternating component type from the first switch on, which is how the root remounts.

> **Why `ThemeHost`, not the plan's `Themes`.** Plan 5.3 calls the class `Themes`, but the themes live in `Wavee.Themes.<Name>`
> (plan 1.2), and inside `namespace Wavee.*` the simple name `Themes` binds to that namespace. Read every `Themes.Current`,
> `Themes.Facts`, `Themes.Switch` or `Themes.Apply` in the plan as `ThemeHost.…` (`using Wavee.Theming;`).

### The options UI (T1b)

Settings › Appearance is `Theme` (the picker) · `‹Theme› options` · `Display` (app mode, zoom, marquee), then the rows that have
no other home yet (lyrics, full screen, now playing, Home). Nothing in it names a theme.

- **Picker.** `Screens\Settings.UI.Theme.cs` draws one fixed-size card per `ThemePickerEntry` (`PickerState.Entries(developerMode)`:
  install order, Developer themes only in Developer mode but always the active one). Name is one line, description two, the
  preview is a UI-native miniature with the manifest's preview image over it when the asset ships. A pick is
  `SettingsVerbs.PickTheme` → `ThemeHost.Switch`; the remounted page opens on Appearance again. The manifest's
  `DescriptionKey` is optional.
- **Generated section.** `Theming\Options\ThemeOptionsSection.Build/Current` turns `ExposedOptions` into `GeneratedOptionRow`s
  (a `SettingsRow` plus choices, range, swatches): toggle, select (Radio → segments up to 4 choices, else a combo; Combo; Cards
  and Swatches → picker; Toggle with exactly two choices → switch), slider, colour (swatches or a free picker), action (its
  `ThemeVerbs.Register`ed verb; greyed without one), reference to a core pref (the core row's own control). Every write is
  `ThemeHost.Apply`; a refused value is logged and changes nothing. `VisibleWhen` and Developer visibility are evaluated at build.
  "Reset options" is `ThemeHost.ResetOptions` (forgets the stored keys and puts the cells back; one remount if a transaction
  option changed).
- **Legacy rows.** A structure lists the options that are still switches in `IStructureLayer.LegacyRows` (a `SettingsDoc` row id and a
  group). The page draws them in the same expander and groups; an M-PR deletes its rows from the list and declares the option, so
  the tab never changes layout again. Classic lists 13.
- **Palette.** An option with `Command = true` contributes rows (`ThemeOptionsSection.Commands`): a toggle on/off, a select one per
  choice, an action one. `Shell.CommandTable.BuildIndex` appends them after the builtins (`PaletteKind.ThemeOption`).
- **Setup.Theme.** `Setup.ThemeStep()` (`Screens\Setup.UI.Theme.cs`) is the Layout page's twin (same frame) with the picker and the options;
  it replaces Layout in M7a and is mounted by nothing until then.

## 4. Migrating a retired switch (`ThemeBootstrap`)

Each M-PR that retires a presentation switch adds one named step to `ThemeBootstrap.Steps`:

```csharp
new ThemeBootstrapStep("SidebarDensity",
    ReadsLegacyKeys: ["sidebar.density"], WritesKeys: ["theme.classic.structure.density"],
    Run: s => ThemeBootstrap.Copy(s, LegacyDensity, DensityKey, v => (v is >= 0 and <= 2, v)))
```

Rules (plan 7.1): steps are **named** (never numbered), record `theme.bootstrap.done.<Name>` when they have run, **copy**
(write the new key only when it is absent and the legacy key is present; legacy keys stay as inert data), and declare their
read and write sets so `ThemeBootstrap.Disjointness` can prove the merge order is irrelevant. A step that throws is logged
and retried next launch. `Platform.Boot` runs the shipped steps right after `ZoomAutoPolicy.MigrateMode`. The tests
(`Wavee.Tests\Theming\ThemeBootstrapTests.cs`) run every permutation of the shipped steps over a legacy store: seed their
legacy keys there and a new step is covered without a new test. Add the retired names to
`src\apps\Wavee\analyzer-input\WVTH010.legacy.txt` in the same PR (section 5).

## 5. The analyzers (`Wavee.Analyzers`, WVTH rules)

All rules are errors and run in the normal Wavee build. Baselines (`src\apps\Wavee\analyzer-baseline\WVTHnnn.txt`, one
`path/suffix.cs[ :: detail]` per line) are **shrink-only**: an entry that matches nothing is WVTH900; a baseline file for a
rule that allows none is WVTH901. Rules that need a list read `src\apps\Wavee\analyzer-input\*.txt`.

| Id | Rule | Baseline |
|---|---|---|
| WVTH001 | `Wavee.Themes.*` referenced only along the allowed edges (App.cs, ThemeCatalog.cs, composition files, structure to Common) | none |
| WVTH002 | `Themes\**` may not reference `Entities`, `Playback`, `Spotify`, `Store`, `Shell`, `Prefs`, `Platform.Settings` | `Themes/Classic/Structure/LegacyPrefs.cs` only, shrink-only |
| WVTH003 | no literal looks (a number for a size/radius/gap/opacity, a colour built from components) in `Themes\*\Structure\**` and `Themes\Common\**` outside `Rules\**` | Classic, emptied by P3c |
| WVTH004 | no identity reads (`ThemeHost.Current`, `ThemeCatalog`, `ThemeComposition.Manifest`, `ThemeManifest` ids, layer ids, `ThemeDefinition.Id`) outside Composition, Options, ThemeBootstrap, App.cs, manifests | none |
| WVTH005 | an option is read or published only by its own layer (and Options/Composition) | none |
| WVTH006 | keys created only under `Theming\Contracts`; dotted unique ids; props are sealed records with read-only members | none |
| WVTH007 | `theme.*` settings minted only in `Theming\Options`, `ThemeBootstrap.cs`, `ThemeHost.cs` (`theme.mode` stays core) | none |
| WVTH008 | a `[CoreHost]` type builds no element and reads no `Tok.*`/`Design.*`/`R.*` (nor the live `StageInk.*`); every colour-role facade is a member of `R`, so it is fenced with the rest | shrinks per surface PR |
| WVTH009 | `ctx.OptionRequests.Set` only with own-layer keys | none |
| WVTH010 | retired keys and deleted members (listed in `analyzer-input\WVTH010.legacy.txt`) only in `ThemeBootstrap.cs` | empty after F4 |
| WVTH011 | each hidden structure option has a line with two or more values in `analyzer-input\WVTH011.matrix.txt` | none |
| WVTH013 | the tier fence: in `Themes\**` and `[CoreHost]` types, no comparison of a measured width (`Viewport.Size`, `UseMeasuredWidth`/`UseMeasuredBounds`, the width parameter of a `Responsive.Of` build lambda, a `TierFacts` width/height member) with a number or a constant declared outside `Rules\Tiers*.cs`, and no `Responsive.Of`/`UseMeasured*` call that feeds one (section 7) | Classic only, shrink-only, deleted by TC10 |

The analyzers recognise types by namespace and name (they run inside the compiler and never reference the app), so a rule
is tested with stand-in declarations (`Wavee.Analyzers.Tests\Fixtures.cs`).

## 6. Testing a view (`ViewHarness`)

`Wavee.ShellTests\ViewHarness.cs` mounts one view key on the headless shell host:

```csharp
ViewHarness.With(host, ViewKeys.PlayerBar, fixtureProps, harness =>
{
    var bar = harness.Node("player:bar");
    Assert.True(harness.AbsoluteBounds(bar).H <= 80f);
    harness.SetOption("structure.density", Density.Compact);   // the cell changes, the view re-renders
}, new ViewHarnessOptions { Composition = ZuneTheme.Create(), WidthDip = 640f, HeightDip = 360f });
```

Without a composition the view resolves through the installed theme (the shell's own providers). With one, the harness
builds that composition's registry, option store (over a private in-memory settings store, never the profile) and facts and
applies its definition to the process tokens until the mount is disposed. It keeps no baselines and captures no pixels; it
asserts structure and geometry rules. Every member runs on the UI thread (`ShellHost.Run`).

Core tests for the machinery itself are in `Wavee.Tests\Theming\` (catalog, options, bootstrap) against fake layers; they
live in the platform collection because `Themes` is process state.

## 7. Window tiers (`TierPolicy`, `WindowTiers`, T3b)

Tiers are a **core fact**; where a tier begins, how it holds across a resize and what each tier lays out are the **structure's**
(plan section 3). The shell measures the window, the structure's ladders decide, a view reads the answer.

| Piece | Where | What |
|---|---|---|
| `WidthTier` (Compact, Small, Standard, Huge, Huge4K), `HeightTier` (Short, Regular) | `Theming\Contracts\Tiers.cs` | the contract enums; a ladder's tier index is the enum value |
| `TierPolicy` | `Theming\Policies\TierPolicy.cs` | three engine `TierLadder`s (window width, content width, window height), `CompactFloor` (300), `ShortFloor`, `NamedRung`s (board marks that are not tiers) and `NamedLadder`s (finer ladders a core model may read by id; Classic declares none); `ThemePolicies.Tiers` |
| `TierFacts` | `Theming\Policies\TierFacts.cs` | what is published: `Window`, `Height`, `Content`, `ContentWidth`, the viewport size, `Resizing`, `Rung(id)` |
| `WindowTiers` | `Shell\WindowTiers.cs` | the core rules (`EstimateContentWidth`, `Resolve`) and the frame root's `Binding` over the engine's `UseTier` |
| `ctx.Tier()` | `Theming\TierContext.cs` | how a template reads `TierFacts` (never a width) |
| `TierRegion`, `ITierLayoutRecord`, `TierClip` | `Theming\Policies\TierRegions.cs` | the clip-region API of a tier layout record |
| Classic's ladders | `Themes\Classic\Structure\Rules\Tiers.cs` | the production numbers, moved out of core |

**An edge** is the engine's `TierEdge(At, NarrowHold, WidenHold)`: crossed upward at `At + WidenHold`, downward below
`At - NarrowHold`; a fresh resolve (a launch, a navigation, a first mount) uses `At`. An absent edge (`TierEdge.None`) leaves the
band above it unreachable, so a structure that draws no Huge 4K band lets its Huge band cover every wider window. Classic holds on
widening (528 to 568, 660 to 700); WinUI, Zune and Panorama will hold on narrowing.

**The sidebar's bands are the window tiers.** `SidebarPaneModeRules.BandOf(WidthTier)` maps Compact to Tiny, Small to Narrow and the rest to Wide; `NavPanePolicy.ModeFor(WidthTier)` is what the band forces. The pane has no edges of its own, so the frame seeds its band from the window tier of the first frame and derives `Sidebar.Band` from the tier signal. **The player bar's levels are not a ladder here:** they are `PlayerBarPolicy`'s (section 2a), which carries each level's slots and spacing beside its start width and the narrowing hold.

**No jumps (plan 3.3).** A first mount resolves in its own frame. A crossing found after mount is a *pending* value in the host and
is committed on the first frame that is **not resized**, right before FLIP capture: the resized frame lays the OLD tier out at the new
size and snaps, and the new tier crosses on the next frame as an ordinary animated update (moved nodes FLIP, swapped subtrees
cross-fade). A composited edge drag therefore produces one crossing, at mouse-up. A size that is not positive and finite (a minimised
window reports 0) resolves to "keep the previous tier", so a restore returns to the tier the window left.

**Reading tiers.** Core models read `ThemeHost.Facts.Value.Tiers`; a template reads `ctx.Tier()` and its structure's layout record
for that tier. The fact is republished when a tier, a rung or `Resizing` changes, not once per resized frame, so the three size members
are the sizes *at that snapshot*; a reader that needs the live width reads `Viewport.Size`.

**Content width** is the window less the sidebar column the *logical* window tier presents (the pending crossing included, so the
content tier crosses together with the window tier) and an inline rail (`WindowTiers.EstimateContentWidth`).

**Clip, never squeeze.** A tier layout record implements `ITierLayoutRecord`: it lists the regions it may clip with their minimum
sizes, builds each with `TierRegion.Host` and puts them in a `TierClip.Ancestor`. On the resized frame a region the old tier cannot fit
keeps its minimum and overflows into the clipping ancestor for that one refresh. Tests read the same declaration (`TierClip.ClippedAt`).

**Adding a ladder.** A structure builds its `TierPolicy` in `Rules\Tiers*.cs` (WVTH003 allows literals there) and sets it on its bundle (`ClassicPolicies.Bundle` is `new(...) { Tiers = ClassicTiers.Policy }`). Keep the instance in a static property or field (in a static constructor if the ladders spread over several files): the facts compare equal only while the policy is the
same object. A finer ladder a core model reads by id goes in `Ladders` as a `NamedLadder` (`TierPolicy.Ladder(id)`); nothing in core reads one today.

**The fence (WVTH013, T3c).** A view never compares a width with a number: it reads `ctx.Tier()` and its structure's layout record, and
the breakpoints live in the structure's `Rules\Tiers*.cs`. The rule is a syntax rule over `Themes\**` and over types marked
`[CoreHost]`; `Rules\**` of a structure, `Theming\Policies\**` and `Shell\WindowTiers.cs` (the measurement) are exempt. It reports, at the
comparison, a relational or equality comparison (`<`, `<=`, `>`, `>=`, `==`, `!=`, and the relational or constant patterns of `is` and
`switch`) with a **measurement** on one side and a non-zero numeric literal, or a constant declared outside `Rules\Tiers*.cs`, on the
other; and, at the call, a `Responsive.Of` whose build lambda holds such a comparison and a `UseMeasuredWidth`/`UseMeasuredBounds`
whose result feeds one. A measurement is `Viewport.Size` (read through `UseContext`/`UseContextSignal`), the result of
`UseMeasuredWidth`/`UseMeasuredBounds`, the width (last) parameter of a `Responsive.Of` lambda, or `TierFacts.ContentWidth`/`ViewportWidth`/
`ViewportHeight`; a member read, `+ - * /`, a `Math`/`MathF` call and a local assigned from one carry it. Comparing the tier
(`ctx.Tier().Content >= WidthTier.Huge`), a layout-record member, a control-size literal (`Width = 32`) or a value against zero is
clean. That page-level widths come from the layout record is a review rule, not an analyzer rule (an analyzer cannot tell a page-level
region from a control size). The baseline detail is the measurement kind (`Viewport.Size`, `UseMeasuredWidth`, `UseMeasuredBounds`,
`Responsive.Of`, `TierFacts.ContentWidth`...), the same for the comparison and its call, so one entry per file and kind covers a site;
Classic's file is `analyzer-baseline\WVTH013.txt` (no file today: no view has moved into `Themes\**` yet), emptied family by family by the
Classic tier PRs and deleted by TC10. Where a ladder cannot move without a behaviour change, the move PR lists the file there.

**Zoom and tiers (C1b).** Tiers are measured on the layout viewport, after app zoom, so Auto zoom is clamped against ONE core reference,
`ZoomTierReference.HugeFrom` = 1920 (`Platform\ZoomService.cs`), the Huge edge of every shipped ladder. It is not any structure's ladder, so
a theme switch never changes the zoom. For an unzoomed window width W: `W < 1920 ? Suggest : min(Suggest, plateauDown(W / 1920))`, with
`Suggest` the unchanged `ZoomAutoPolicy` curve. Both arms are non-decreasing in W, so widening a window never lowers the zoom, and a window of
1920 or more never lays out narrower than 1920 (1440p at 100 % and 4K at 150 % get 1.25 instead of 1.5; 4K at 100 % stays 2.0). `AutoZoom`,
`InitialZoom` (the first frame) and `AutoSuggestion` (the Zoom row's "Auto (N%)") all go through it. A **settled** size (maximize, restore,
Snap, a drag release, a DPI or monitor change) is classified by `ZoomService.AutoLoop.Arrive` and the frame steps in the same layout phase, so
the zoom write is one more resized frame and the tier crosses once; only a stream of sizes (another within 500 ms of the previous) waits for the
debounce, and nothing is rescaled inside the modal size-move loop. Manual zoom stops at `ZoomTierReference.CompactFloor` (300 layout DIP):
`ZoomService.ManualStep` refuses a chord, wheel notch or palette step that would go under it, `PickManual` and `InitialZoom` land on the largest
rung that fits. `ZoomTierTests` pin the rule.

**Testing.** The pure rules (`WindowTiers.Resolve`, a policy's ladders) are driven with numbers (`TierPolicyTests`); the live resize
(resized frame keeps the old tier, the next frame crosses with tracks, a drag crosses once) is `Wavee.ShellTests\TierResizeTests`;
`ViewHarness` resolves the tiers of a view mounted alone from its window size, with hysteresis across `Resize`.

**Classic today.** Classic's window ladder is the production sidebar band (Compact below 528, Small below 660, Standard above) and has
no Huge band; TC1 redesigns it from the boards (528 / 1008 / 1920). Not moved out of core, because none is a ladder this primitive can
state without changing behaviour: the chrome-row allocator, the stage aspect classes, the right-rail fit and the detail page-mode
ladder.
