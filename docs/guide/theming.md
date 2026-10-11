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
| WVTH008 | a `[CoreHost]` type builds no element and reads no `Tok.*`/`Design.*` | shrinks per surface PR |
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
