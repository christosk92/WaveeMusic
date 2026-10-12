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
- Keys live only in `Contracts\ViewKeys.cs` and `StyleKeys.cs` (WVTH006). T1 declares none; each extraction PR adds its
  family.

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
| **A colour or an alpha that differs by kind is a role**; a view never writes `Tok.Theme == ThemeKind.Light ? a : b` | The branch is the style's decision. The 27 branches in `Design.cs`, `Controls.cs`, `Concert.UI.cs`, `Deck.UI.cs`, `Rail.UI.cs`, `Shell.Masthead.UI.cs` and `Detail.UI.cs` are role reads now |
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
paint budget (not a look), the shell material channel (`ShellMaterial`, `TintOwnership`) and the four cover-leaf components, which now read their
alphas and recipes from `R.Wash` and move to Classic's material views in T4. The shape roles (`ShapeRole`) and the icon table (`IconTable`) are not part
of T2b: their consumers (the omnibar row, the surface shapes, the route glyphs) are the T3/T4/T6/T8 PRs.

## 2a. Policies and metrics (T3)

A structure decides the numbers the core state machines run on; the machines themselves stay core. `ThemePolicies`
(`Theming\Policies\`) is the bundle a structure returns from `IStructureLayer.Policies`:

| Record | What the structure decides | Core machine that takes it |
|---|---|---|
| `NavPanePolicy` | the Wide / Narrow / Tiny window edges and their holds, what Narrow and Tiny force (`NavPaneMode`: Expanded, Compact rail, Overlay drawer, None), the expanded width bounds and default, the content floor, the seam collapse rule, whether the pane is resizable | `SidebarPaneModeRules`, `SidebarResizeRules`, `FrameModel`, `SidebarPaneBounds.Clamp` |
| `PanelPolicy` | the panel's min and max width, the content it must leave to stay inline, full height, the mini player's `Video.PipBounds` | `Shell.ClampRailWidth`, `Shell.CanFitRail`, `PanelHost.FitWidthTo`, `Video.PipGesture` |
| `PlayerBarPolicy` | six pressure levels (start width, `PlayerBarSlots`, spacing, art, hit targets), the narrowing hold and the bar height | `Shell.PlayerBar`, `Shell.PlayerBarLayout.ForTier` (so `PlayerBarRules.Overflow` and `RightWidth` follow) |
| `ChromePolicy` | the title-bar row's `ChromeBudget`: where the actions, name and forward button may appear, what each affordance costs, the search and tab widths | `Shell.Chrome` (the allocator) |
| `StagePolicy` | the stage's `AspectThresholds` (Compact, Portrait, Ultrawide edges and holds) | `Stage.Layout.ClassOf` / `Resolve` |

`ShellCapabilities` says what the shell offers (Classic: `Unrestricted`) and `StyleMetrics` is the style's row heights and
gutters (Classic publishes its own from `Design`). The tier policy (`TierPolicy`, `WidthTier`) is T3b's and joins this bundle.

- **Classic's numbers are `Themes\Classic\Structure\Rules\ClassicPolicies.cs`** and equal what the shell shipped with. The
  core keeps the same set as `ThemePolicies.Default` so it has a policy before a composition is installed and when a structure
  publishes one it cannot run on; `ClassicPoliciesTests` pins the two equal, so a tuning PR changes both on purpose.
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

The analyzers recognise types by namespace and name (they run inside the compiler and never reference the app), so a rule
is tested with stand-in declarations (`Wavee.Analyzers.Tests\Fixtures.cs`). WVTH013 (window tiers) arrives with T3c.

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
