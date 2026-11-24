# Custom light/dark colour palettes (#184): implementation plan for 0.4 "Drift"

> **Design.** Design board with the palette editor screens and the spec diagrams:
> https://claude.ai/artifact/Nz5F8Tuq3b61kWGBTiQLoP (private until shared). Palette hex values on the board are
> illustrative.

Base code: `release/0.3.13` (the app at `5e1711d62`, `fluent-gpu` at `edbda277c`).
Work branches from the 0.4 release branch once it exists. All paths are relative to `WaveeMusic\src\apps\Wavee\`
unless they start with `fluent-gpu\`.

## Cross-plan notes (0.4)

- **#184 is independent of the other three plans.** It touches theming and Settings only: no playback, no library, no
  module code. It owns no shared groundwork and needs none.
- **Shared real estate: the Settings page.** This plan adds one group to the Appearance category and touches
  `Screens\SettingsSpec.Appearance.cs`, `Screens\SettingsSpec.cs` (one new row kind), the three theme Settings views
  (`Themes\Classic\Structure\Views\Screens\ClassicSettings.Theme.cs` + `ClassicSettings.Cards.cs`,
  `Themes\WinUi\Structure\Views\Screens\WinUiSettings.Theme.cs` + `WinUiSettings.Rows.cs` + `WinUiSettings.Body.Appearance.cs`,
  `Themes\Zune\Structure\Views\Screens\ZuneSettings.Theme.cs` + `ZuneSettings.Composites.cs`) and the three setup theme
  pages (`ClassicSetup.Pages.cs`, `WinUiSetup.Pages.cs`, `ZuneSetup.Pages.cs`). #240 adds local-folder rows (Storage or
  a new category) and #154 / #48 add Playback / Sound rows: different `SettingsSpec.*.cs` files, but all four plans add
  cases to the same per-theme row switches (`ClassicSettings.Cards.cs`, `WinUiSettings.Rows.cs`,
  `ZuneSettings.Composites.cs`). Expect small merge conflicts there; land #184's row-kind change first or rebase.
- **Groundwork owned elsewhere:** G1 (local library: folders setting, scan, index) is owned by **#240**. G2 (playback
  timeline tap, auxiliary audio output, module observer hook) is owned by **#154**. The mix transition planner is
  owned by **#48**. None is used here.
- **Suggested 0.4 build order:** #240 and #184 in parallel first (no shared code), then #154 phase 1 (the timeline
  tap), then #48, then the rest of #154.

---

## 1. What was asked, and what exists today

### The request

Issue #184: pick one colour palette for light mode and another for dark mode (the example is Catppuccin Latte by day,
Catppuccin Mocha by night), and with the app mode on System, switch between them when Windows switches.

The owner's poll (discussion #196, 4 votes): 1 "built-in palettes only", 1 "built-in + my own accent", 2 "fully
custom colours", 0 "import a theme file", 0 "happy with the default". Comments ask for: an OLED-style near-black
palette with saturated blue accents, an accent that is independent of Windows, artwork tint probably off with a custom
palette (or restricted to the palette's hues), a secondary accent, and (one commenter) JSON import "for longevity".

### What exists (verified)

**The engine already models a palette as a light + dark pair.** A `ThemeDefinition` carries `Light` and `Dark`
`TokenSet`s, `LightShell` and `DarkShell` `ShellPalette`s, an `Accent` ramp and an app role table
(`fluent-gpu\src\FluentGpu.Engine\Theming\ThemeDefinition.cs`, the record at line 16). The engine ships three seeded
presets plus an accent-tinted one: `Tok.WarmPalette`, `Tok.SlatePalette`, `Tok.NeutralPalette`,
`Tok.AccentTintedPalette`, `Tok.Presets` (`fluent-gpu\src\FluentGpu.Engine\Dsl\Tokens.cs:244-250`), each built by
`PaletteBuilder.Build(PaletteSeed)` from a hue, a chroma and four light luminance anchors
(`fluent-gpu\src\FluentGpu.Engine\Dsl\PaletteBuilder.cs:11-20, 34-62`). This is what the owner means by "palettes
already exist as light + dark pairs". They are generated tints of one neutral ramp, not exact colours.

**Wavee always uses the Neutral preset and has no picker.** `Design.Definition` is `Tok.NeutralPalette` with Wavee's
fonts (`Platform\Design.cs:157`), and a tombstone records that the old picker was removed: "The palette PICKER
(Settings + the profile menu) and its swatch preview are gone, Wavee always renders the neutral palette"
(`Platform\Design.cs:167-169`).

**One install path for colours.** `Design.SeedPalette(kind)` is `Tok.Use(ThemeHost.ActiveDefinition ?? Definition, kind)`
(`Platform\Design.cs:153`). `Tok.Use(definition, kind)` swaps the whole table with one pointer write and bumps
`Tok.Epoch`, which re-renders every mounted component (`Tokens.cs:276, 318-333`). A change that differs only in
colours takes the live 250 ms cross-fade; anything else is a theme transaction (`ThemeDefinition.DiffersOnlyInColours`,
`ThemeDefinition.cs:82-86`).

**Light / dark / System has one owner.** `Theming\Appearance.cs`: `ThemeMode { System, Light, Dark }` (line 42,
stored as `theme.mode`, `Platform\Platform.cs:116`), `SetMode` (line 119), `OnSystemColorsChanged` (line 137: the
accent follows always, the kind only while the mode is System), and `Switch` (line 150), which takes the theme
transaction when the active definition's window material differs between the two kinds
(`LiveAppearancePorts.KindChangeIsStructural`, line 239). That last branch matters below.

**The OS accent is an input of the style, not an override.** `ThemeHost.OsAccent` (`Theming\Composition\ThemeHost.cs:71`)
is handed to `IStyleLayer.Definition(OptionValues style, AccentRamp osAccent)` (`Theming\Composition\ThemeManifest.cs:96`).
Each style decides:

| Theme | Accent today | Where |
|---|---|---|
| Classic | always the Windows accent, no option | `Themes\Classic\Style\ClassicStyle.cs:49-61` |
| WinUI | `style.useWindowsAccent` (default on), else the theme's #0078D4 ramp | `Themes\WinUi\Style\WinUiStyle.cs:77-79, 96-113`, `WinUiAccent.cs:19-31` |
| Zune | `style.accent`: five swatches, pink default, each a light/dark pair floored to 4.5:1; `style.useWindowsAccent` (default off) | `Themes\Zune\Style\ZuneStyle.cs:69-80, 108-126`, `ZuneAccent.cs` |

**Where each theme's ground colours come from.**

| Theme | Grounds | Where |
|---|---|---|
| Classic | the Neutral preset's token sets, read live through `ColorRole.By` recipes; 12 literal colours in the role table | `ClassicStyle.cs:30-36`, `Themes\Classic\Style\ClassicRoles.cs` (literals at 194-197, 203, 208-211, 232-234, 294) |
| WinUI | `WinUiTokens`: Mica base #202020 / #F3F3F3, base-alt, content layer, tertiary text; window material Mica | `Themes\WinUi\Style\WinUiRoles.cs:28-72`, `WinUiStyle.cs:128-155` |
| Zune | `ZuneTokens`: a flat opaque ladder (ground #161616, well, panel, raised, dialog, placeholder, three inks) per kind; no window material | `Themes\Zune\Style\ZuneRoles.cs:35-108`, `ZuneStyle.cs:137-209` |

The role tables hold many of these as frozen pairs (`ColorRole.Pair(light: ZuneTokens.LightGround, dark: ZuneTokens.DarkGround)`,
`ZuneRoles.cs:280`), so swapping the token sets alone would not recolour WinUI or Zune.

**Theme options already include a colour option and both of its controls.** `ColorOption` (packed ARGB, optional
swatches; `Theming\Options\ThemeOptions.cs:255`), drawn as a swatch strip or the engine's free picker in Classic
(`ClassicSettings.Theme.cs:601-626, 670-682`) and Zune (`ZuneSettings.Theme.cs:289-317`). WinUI's option control has no
colour branch (`WinUiSettings.Theme.cs:343-359` returns null for it): no WinUI option is a colour today.

**A colour picker exists in the engine.** `FluentGpu.Controls.ColorPicker.Create(Signal<ColorF> color, bool alphaEnabled, …, onChange)`
with a spectrum, a hue rail and editable Hex / R / G / B fields (`fluent-gpu\src\FluentGpu.Controls\ColorPicker.cs:19`,
hex field at line 262).

**Settings are spec-driven.** A setting is a `SettingsDial` made in `SettingsSpec` with a `SettingsRowKind`
(`Toggle, Choice, Segmented, Slider, Picker, Action, Fact`; `Screens\SettingsSpec.cs:38`); each theme's Settings view
draws rows by kind and special-cases a few ids (`"appearance.theme"` in `ClassicSettings.Cards.cs:26`,
`WinUiSettings.Rows.cs:242, 256`, `ZuneSettings.Composites.cs:27`). The App mode dial is `appearance.appMode`
(`Screens\SettingsSpec.Appearance.cs:51`). The setup wizard's theme page reuses these rows
(`ClassicSetup.Pages.cs:39`, `WinUiSetup.Pages.cs:37`, `ZuneSetup.Pages.cs:40`).

**Artwork tint.** Classic has the Surfaces option (Off / Subtle / Rich) and two toggles, "Accent from artwork" and
"Now playing colours" (`Themes\Classic\Structure\ClassicStructure.cs:362-390`); the last two publish an
`ArtworkColorPolicy` core reads (`Theming\Policies\ArtworkColorPolicy.cs:15`). WinUI's shell material view paints
nothing (`Themes\WinUi\Structure\Views\Material\WinUiMaterial.cs:27`); Zune has its own material view
(`Themes\Zune\Structure\Views\Material\ZuneMaterial.cs:43`). WinUI and Zune publish `ArtworkColorPolicy.Default`
(`WinUiPolicies.cs:90`, `ZunePolicies.cs:68`).

**Contrast tooling.** `ColorContrast.Ratio / MeetsAaText / EnsureContrast / PickContrast / Flatten / Over`
(`fluent-gpu\src\FluentGpu.Engine\Foundation\ColorContrast.cs:13-86`); app solves in `Platform\ColorScience.cs`
(`AccentFloor`, `Hairline`, `TextInk`); Zune's lightness walk `ZuneAccent.Floored` (`ZuneAccent.cs:71-78`). The
inspector's `contrast.text` lint judges every text run against the rendered ground
(`docs\guide\agent-lint.md` "contrast.text", line 305).

**Inspector.** `setting id=<dial> value=<v>` calls a dial's own writer (`Screens\Diagnostics.Inspect.Setting.cs`,
`docs\guide\agent-inspect.md:803`); `mount … theme= kind=` picks the theme and light/dark (`agent-inspect.md:201`).
So a palette that is a Settings dial is mountable for verification with no new verb.

### What is missing

1. Exact-colour palettes (Catppuccin is not a tint of the neutral ramp).
2. A light pick and a dark pick that are independent of each other.
3. An accent that comes from the palette or from the user instead of from Windows.
4. Every theme's role table reading its grounds from the palette.
5. A picker, in all three themes and in setup.
6. A contrast guarantee for colours the user types in.

---

## 2. Product decisions for the owner

### D1. Scope for 0.4 (the poll's question)

| Option | Cost | Serves |
|---|---|---|
| A. Built-in palettes only | M | 1 of 4 votes |
| B. Built-in + own accent | M+ | 2 of 4 (A's voter is covered) |
| **C. Built-in + own accent + one custom palette per mode, set from six seed colours** | L | all 4 votes |
| D. Every token editable (about 90 colours per mode) | XL | the same 2 "fully custom" voters, at a far worse first experience |
| E. Theme file import (Spicetify `color.ini`, JSON) | M on top of C | 0 votes, 1 comment |

**Recommendation: C.** "Fully custom" won the poll, but nobody who voted for it asked for ninety colour wells; the
comments describe a ground, a text colour and accents. Six seeds per mode (background, surface, text, muted text,
accent, plus an optional second surface) with everything else derived gives a custom look in a minute and keeps
contrast provable. D and E go to "After 0.4"; the custom palette's stored form is already a plain text line, so E is a
file dialog and a parser away.

### D2. Palette scope: one choice for the whole app, or per theme?

**Recommendation: one app-wide pair** (`theme.palette.light`, `theme.palette.dark`), applied to whichever of Classic,
WinUI or Zune is active. That is what the issue describes ("Catppuccin Latte for light, Mocha for dark"), and it
survives a theme switch. Per-theme palettes can be added later by qualifying the key; nothing in the design blocks it.

### D3. Where the accent comes from

Three sources, one dial (`appearance.paletteAccent`):

| Value | Meaning |
|---|---|
| Theme (default while no palette is picked) | exactly today: Classic follows Windows, WinUI and Zune follow their own options |
| Palette (default once a palette is picked) | the palette's accent; for families with named accents (Catppuccin's 14) a swatch row picks which |
| My colour | one colour from the picker, floored for contrast per mode |

**Recommendation: as tabled.** It answers the issue's question 2 (pick the accent or keep Windows: both) and the
poll comment (accent independent of Windows). While the source is Palette or My colour, the theme's own accent
options (Zune's swatches, the two "use the Windows accent" toggles) are shown disabled with the reason "Set by your
palette", never hidden (state stays visible).

### D4. Artwork tint with a palette

| Option | |
|---|---|
| a. Leave each theme's own options alone | a Mocha ground washed green by an album cover is not Mocha |
| **b. One toggle, "Artwork colours", default off while a palette is active for the current mode** | the exact palette by default, one click to get the washes back |
| c. Restrict artwork hues to the palette's ("hue scale") | attractive, needs design and tuning time |

**Recommendation: b for 0.4, c after.** The toggle governs the shell tint, the page tone and wash planes and the
"accent from artwork" page accent. It does not touch the fullscreen stage, the visualizers, the deck or anything
painted on artwork (`R.OnMedia.*`): those are media surfaces, and a commenter in #196 praised exactly that blending.

### D5. Window material under a palette

Classic and WinUI sit on Mica, which takes its colour from the desktop wallpaper. An exact palette cannot be exact on
Mica. **Recommendation: a mode with a palette uses an opaque window (`WindowMaterial.None`)**; a mode on "Theme
default" keeps the theme's material. Consequence: a user with default light and Mocha dark has different materials per
mode, so the System light/dark flip takes the existing theme transaction (a 250 ms dissolve) instead of the live
cross-fade. Both paths exist and are jump-free today (`Appearance.cs:150-158`). Zune is unaffected (already opaque).

### D6. Built-in palettes for 0.4

Proposed list (each flavour is one mode's palette; licences **to verify** before shipping, and each needs a line in
`THIRD-PARTY-NOTICES.txt`):

| Family | Light | Dark | Named accents | Licence (to verify) |
|---|---|---|---|---|
| Catppuccin | Latte | Frappé, Macchiato, Mocha | 14 (Rosewater … Lavender), default Mauve | MIT |
| Nord | Snow Storm | Polar Night | Frost 4 + Aurora 5 | MIT |
| Gruvbox | Light (medium) | Dark (medium) | 7 | MIT/X11 |
| Dracula | (none; Alucard is newer and its licence status is unverified) | Dracula | 7 | MIT |
| Wavee | Paper (warm white) | Ink (OLED: ground #000000, panels about #0B0B10, saturated blue accent) | 1 | own |

Ink is the poll's most concrete request (near-black with saturated blue). Colour values are facts and not
copyrightable in most readings, but the names are the projects' and the palettes are published under the licences
above: ship attribution regardless. The exact hex tables are not reproduced in this plan; they must be copied from
each project's published palette file at implementation time and cross-checked by rendering.

### D7. Naming

The word "palette" already means the album-art colour table in code (`Wavee.Palette`, `Design.Palette`) and "command
palette" in the UI. **Recommendation:** user-facing string "Colours" (group title) with "Light colours" / "Dark
colours" rows; code namespace `Wavee.Theming.Colours` with types prefixed `Colour` (`ColourScheme…` is taken by
neither). This plan uses `ColourSet` for the user's choice to keep clear of both existing meanings.

---

## 3. Architecture

### 3.1 The shape in one diagram

```
 Settings (3 themes) / setup / command palette / inspector `setting`
        │  SettingsVerbs.SetLightColours / SetDarkColours / SetColourAccent / SetCustom…
        ▼
 ColourChoice (core, UI thread)            persisted: theme.palette.* (Platform.Settings)
        │  ColourCatalog.Find(id) → ColourSpec (built-in table or the user's custom line)
        │  ColourSolver.Resolve(spec, accent source) → ColourLadder (per mode, contrast-floored)
        ▼
 ThemeHost.Colours : ColourPair            an INPUT of the style, exactly like ThemeHost.OsAccent
        │  IStyleLayer.Definition(OptionValues style, in StyleInputs inputs)   ← signature change
        ▼
 ClassicStyle / WinUiStyle / ZuneStyle     each maps the ladder onto ITS token sets, shell palette,
        │                                  role table and material; memoized per (options, inputs)
        ▼
 Tok.Use(definition, kind)                 one pointer write, Tok.Epoch++  → live cross-fade (colour-only)
                                           or the theme transaction (material differs per mode)
```

Nothing is added per frame. A palette change is one definition build (off the hot path, on the UI thread, a few
hundred colour computations, memoized) and one `Tok.Use`. Steady frames read `Tok.*` properties exactly as today.

### 3.2 Data model (new folder `Theming\Colours\`)

```csharp
namespace Wavee.Theming.Colours;

/// <summary>What a palette author gives for ONE mode. Six seeds are required; the rest are optional and derived when absent.</summary>
public readonly record struct ColourSeeds(
    ColorF Ground,          // the page (Catppuccin base, Nord nord0 / nord6)
    ColorF Surface,         // raised plates: cards, menus, flyouts (surface0)
    ColorF Ink,             // primary text
    ColorF InkMuted,        // secondary text (subtext0)
    ColorF Accent,          // the default accent of this palette
    ColorF? Well = null,    // the lower plate: player bar, pane (mantle); derived: Ground stepped away from Surface
    ColorF? Deep = null,    // the deepest band: tab rail, title bar (crust); derived: Well stepped once more
    ColorF? InkFaint = null,// tertiary text (overlay1); derived: InkMuted mixed toward Ground, floored to 4.5:1
    ColorF? Critical = null, ColorF? Caution = null, ColorF? Success = null);   // derived: the theme's own severity colours

/// <summary>One named accent of a palette family (Catppuccin "Mauve").</summary>
public readonly record struct NamedAccent(string Id, string Name, ColorF Color);

/// <summary>One palette for one mode. Built-ins are static data; the two custom ones are parsed from settings.</summary>
public sealed record ColourSpec(
    string Id,                       // "catppuccin.mocha", "wavee.ink", "custom.dark"
    string Family,                   // "Catppuccin": the picker groups by it
    string Name,                     // "Mocha": a proper noun, not localized; null NameKey
    ThemeKind Kind,                  // which mode it is offered for
    ColourSeeds Seeds,
    ImmutableArray<NamedAccent> Accents,   // empty: the palette has the one accent
    bool BuiltIn);

public enum AccentSource : byte { Theme = 0, Palette = 1, Own = 2 }

/// <summary>The resolved ladder a style maps onto its tokens: every value final, contrast already guaranteed.</summary>
public sealed record ColourLadder(
    string SpecId, ThemeKind Kind,
    ColorF Deep, ColorF Well, ColorF Ground, ColorF Panel, ColorF Raised, ColorF Dialog, ColorF Placeholder,
    ColorF Ink, ColorF InkSecondary, ColorF InkTertiary, ColorF InkDisabled,
    ColorF WashInk,                  // the colour state washes are made of (Ink at alpha: hover, pressed, hairline)
    ColorF AccentFill,               // accent as a fill: >= 3:1 on Ground (a non-text UI component)
    ColorF AccentInk,                // accent as text: >= 4.5:1 on Ground, Well, Raised and its own 12 % wash
    ColorF OnAccent,                 // text on AccentFill: >= 4.5:1
    ColorF Critical, ColorF Caution, ColorF Success);

/// <summary>What the styles are handed. A null side is "theme default": the style returns today's colours for that mode.</summary>
public readonly record struct ColourPair(ColourLadder? Light, ColourLadder? Dark, AccentSource Accent)
{
    public static ColourPair None => default;
    public ColourLadder? For(ThemeKind kind) => kind == ThemeKind.Light ? Light : Dark;
}

/// <summary>The inputs a style resolves its definition from: what the OS said and what the user picked.</summary>
public readonly record struct StyleInputs(AccentRamp OsAccent, ColourPair Colours);
```

`ColourLadder` is a record so equal picks compare equal and the styles' memo dictionaries key on it (the same reason
`ZuneStyle.DefinitionKey` exists, `ZuneStyle.cs:101`).

### 3.3 The solver (pure, no token reads)

`Theming\Colours\ColourSolver.cs`. Everything a user can type passes through here, which is where the contrast
guarantee lives:

```csharp
public static class ColourSolver
{
    public const float TextFloor = 4.5f, FaintFloor = 4.5f, FillFloor = 3.0f;

    /// <summary>The ladder for a spec in its mode. <paramref name="accent"/> is the palette's default, a named accent
    /// or the user's own colour; it is floored here, so no caller can hand a style an unreadable accent.</summary>
    public static ColourLadder Resolve(ColourSpec spec, ColorF accent);

    /// <summary>What Resolve changed, for the editor's note ("Text was darkened to stay readable"). Empty for a clean spec.</summary>
    public static ColourAdjustments Explain(ColourSpec spec, ColorF accent);

    // Lightness walk in HSL, hue and saturation kept: the rule ZuneAccent.Floored already uses (ZuneAccent.cs:71-78).
    static ColorF Floor(ColorF ink, ReadOnlySpan<ColorF> grounds, float min, bool towardDark);
}
```

Rules, in order:

1. **Mode check.** A spec offered for Dark must have `RelativeLuminance(Ground) < 0.18`, for Light `> 0.60`. The custom
   editor refuses a seed that breaks it and says why (a "dark" palette with a white ground would put the app's dark
   tokens, the title-bar caption colour and the on-media ladder on the wrong polarity).
2. **Derived grounds.** `Well` / `Deep` missing: step `Ground` away from `Surface` by the same lightness distance as the
   theme's default ladder (Zune dark: #161616 → #111111). `Panel` = a third of the way from `Ground` to `Surface`;
   `Dialog` = two thirds; `Raised` = `Surface`; `Placeholder` = `Surface` stepped once more.
3. **Inks.** `Ink` is floored to 7:1 on `Ground` and 4.5:1 on `Raised`. `InkSecondary` to 4.5:1 on `Ground`, `Well`,
   `Raised`. `InkTertiary` to 4.5:1 on the same three and kept at least 0.04 lightness from `InkSecondary` (so the two
   voices stay distinct, the property `ZuneTokens.DarkInkTertiary`'s comment records, `ZuneRoles.cs:53-55`).
4. **Accent.** `AccentFill` floored to 3:1 on `Ground`; `AccentInk` to 4.5:1 on `Ground`, `Well`, `Raised` and on its own
   selection wash (`Flatten(AccentFill @ 12 % dark / 6 % light, Ground)`, Zune's rule, `ZuneRoles.cs:86-91`);
   `OnAccent` = `ColorContrast.PickContrast(AccentFill)`.
5. **Severity.** Missing ones are the style's own; given ones are floored like `AccentInk`.

A built-in palette that the floors move is a finding at implementation time: record which colour moved and by how
much in the plan's as-built notes (Catppuccin Latte's yellow and several Nord frost colours will not reach 4.5:1 as
text on their own light ground; they stay exact as fills and are darkened only where they draw words).

### 3.4 The catalog and the choice

```csharp
public static class ColourCatalog
{
    public static ImmutableArray<ColourSpec> BuiltIn { get; }            // static data, Theming\Colours\ColourCatalog.Data.cs
    public static IReadOnlyList<ColourSpec> For(ThemeKind kind);         // "Theme default" is index 0 in the picker, not a spec
    public static ColourSpec? Find(string id);                            // unknown or stale id → null → theme default
}

/// <summary>The user's stored choice and the one verb set that changes it. UI thread.</summary>
public static class ColourChoice
{
    public static ColourPair Current { get; }                            // resolved, memoized
    public static IReadSignal<int> Epoch { get; }                        // Settings rows and previews read it

    public static void Seed(IAppSettings settings);                      // before the window: no startup flash
    public static void SetSpec(ThemeKind kind, string? specId);          // null = theme default
    public static void SetAccentSource(AccentSource source);
    public static void SetNamedAccent(ThemeKind kind, string accentId);
    public static void SetOwnAccent(ColorF colour);
    public static void SetCustomSeed(ThemeKind kind, CustomSeed seed, ColorF colour);   // the editor
    public static void ResetCustom(ThemeKind kind);
}
```

Every setter: validate, persist, rebuild `Current`, assign `ThemeHost.Colours`, then apply through one private
`Apply()`:

```csharp
static void Apply()
{
    ThemeDefinition next = ThemeHost.ActiveDefinition!;                  // resolves with the new ThemeHost.Colours
    if (ReferenceEquals(next, Tok.Definition)) return;                   // an unchanged pick is not a change
    bool live = next.DiffersOnlyInColours(Tok.Definition);
    if (live) { Tok.Use(next, Tok.Theme); Appearance.RequestTransition(); }   // the 250 ms cross-fade
    else _ = ThemeHost.ApplyColoursInTransaction(next);                  // the material changed: freeze, apply, dissolve
}
```

`Appearance.RequestTransition()` is a new one-line public face over the model's `_transition`
(`Appearance.cs:91, 100`). `ThemeHost.ApplyColoursInTransaction` reuses `Transact(new ThemeTransactionRequest(next, Tok.Theme, …))`
(`ThemeHost.cs:451`) in place, with no remount (the pattern `PublishInTransaction` uses, `ThemeHost.cs:320-346`).

**The colour editor's drag is the one hot path.** Dragging the spectrum must not rebuild a definition per pointer
move, persist per move, or run a transaction per move. The editor holds its own `Signal<ColorF>` (as
`OptionColorPicker` does, `ClassicSettings.Theme.cs:672`), previews live in a small mock-up that reads that signal
through bound props, and commits on release. The engine has no release callback on `ColorPicker` or `Slider` today
(theming.md records the same gap: "a Transaction-mode slider or colour picker must apply on release … the engine
`Slider` has no release callback", `docs\guide\theming.md` §3 "Apply and switch"). **Engine work: `ColorPicker.Create`
gains `Action<ColorF>? onCommit`** (fired on pointer-up of the spectrum, hue rail and on a committed text field).
Until it lands, an explicit **Apply** button in the editor is the commit; the plan ships the button either way, since
it also gives a clean place for the contrast note.

### 3.5 Persistence

Flat keys through `Platform.Settings` (`IAppSettings`, `Platform\Platform.cs:46`), declared in `Platform.Keys`
beside `ThemeMode` (`Platform\Platform.cs:116`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| `theme.palette.light` | string | `""` | spec id for light mode; empty = theme default |
| `theme.palette.dark` | string | `""` | spec id for dark mode |
| `theme.palette.accent` | int | 0 | `AccentSource` |
| `theme.palette.accentName.light` / `.dark` | string | `""` | named accent id; empty = the palette's default |
| `theme.palette.accentOwn` | int | 0 | packed ARGB (the encoding `ColorOption` already uses, `ThemeOptions.cs:255`) |
| `theme.palette.custom.light` / `.dark` | string | `""` | `v1;ground=#1e1e2e;surface=#313244;ink=#cdd6f4;muted=#a6adc8;accent=#cba6f7;surface2=#45475a` |
| `theme.palette.artwork` | bool | false | D4: artwork colours while a palette is active |

The custom line is parsed with `ColorF.TryParseHex` and string splitting: no JSON, so nothing new for the NativeAOT
trimmer. A line that does not parse reads as "no custom palette" and is left in place (the option store's rule,
theming.md §3 "Storage"). Value rules match the theme options': a stored id the catalog no longer has reads as theme
default and is not rewritten.

The seed runs before the window, next to the mode seed (`Shell\Shell.Host.cs:279`): `ColourChoice.Seed(Platform.Settings)`
must run **before** `Appearance.Seed(...)`, because `Appearance.Seed` installs the definition.

### 3.6 The style contract change

`IStyleLayer.Definition(OptionValues style, AccentRamp osAccent)` becomes
`Definition(OptionValues style, in StyleInputs inputs)` (`Theming\Composition\ThemeManifest.cs:96`). Replace, do not
overload (no legacy path). Callers to update: `ThemeComposition.Definition` (`ThemeComposition.cs:211-238`, its memo
gains the `ColourPair`), `ThemeHost.ActiveDefinition` and the four other `theme.Definition(…, OsAccent)` call sites
(`ThemeHost.cs:118, 245, 328, 431`), and the three `DefaultDefinition` statics (`WinUiStyle.cs:117`, `ZuneStyle.cs:129`).
This is a theme contract change: declare it as the `wavee-contributing` skill requires.

A shared helper does the part every theme has in common, the engine `TokenSet`:

```csharp
public static class ColourTokens
{
    /// <summary>The style's token set for one mode with the ladder's colours in every colour-carrying member. Fills and
    /// strokes are washes of the ladder's ink over its grounds at the SAME alphas the base set uses, so each theme keeps
    /// its own state ladder; solid surfaces, text, the acrylic tint and fallback, focus, the scroll thumb, the window
    /// background and the severity colours are replaced.</summary>
    public static TokenSet Apply(TokenSet baseSet, ColourLadder l);

    /// <summary>The engine shell palette from a ladder: opaque bands (toolbar / sidebar / player on Well or Ground as the
    /// style asks), content on Ground, rows as ink washes.</summary>
    public static ShellPalette Shell(ColourLadder l, ShellPalette baseShell, bool chromeOnWell);

    /// <summary>The accent ramp that makes Tok.AccentDefault and Tok.AccentTextPrimary the ladder's exact colours in both
    /// modes: Zune's trick (ZuneAccent.RampOf, ZuneAccent.cs:103-106), since the engine reads Light2/Light3 in dark and
    /// Dark1/Dark2 in light (Tokens.cs:531).</summary>
    public static AccentRamp Ramp(ColourLadder? light, ColourLadder? dark, AccentRamp themeRamp);
}
```

"Re-wash at the base set's alpha" is the rule that keeps this small: for a member whose base value is translucent
white-on-dark or black-on-light (most `Fill*` and `Stroke*` members), the result is `l.WashInk with { A = base.A }`;
for an opaque member the result is the ladder rung the table below names. About 60 of `TokenSet`'s members are
handled by the alpha rule; about 25 are named explicitly.

| TokenSet member(s) | Ladder rung |
|---|---|
| `WindowBackground`, `FillSolidBase` | Ground |
| `FillSolidBaseAlt`, `LayerOnMicaBaseAlt` | Well |
| `FillSolidSecondary`, `FillLayerDefault` | Panel |
| `FillSolidTertiary`, `FillCardDefault` (flattened), `AcrylicFlyout.Fallback`, `AcrylicBase` | Raised |
| `FillLayerAlt` | Dialog |
| `TextPrimary` / `Secondary` / `Tertiary` / `Disabled`, `TextControl*` | Ink / InkSecondary / InkTertiary / InkDisabled |
| `TextOnAccentPrimary` / `Secondary` / `SelectedText` | OnAccent (secondary at 70 %) |
| `FocusOuter` / `FocusInner` | Ink / Ground |
| `SystemFillCritical` / `Caution` / `Success` (+ `…Background` at the base alpha) | Critical / Caution / Success |
| `CaptionCloseHover` / `Pressed` | unchanged (the Windows caption red) |
| `HeroGradientTop` / `Bottom` | Panel / Ground |

Per theme:

**Zune** (`ZuneStyle.cs`, `ZuneRoles.cs`). The natural fit: its ladder is already flat and opaque with the same
rungs. `ZuneTokens`' static colours become a `ZuneGrounds` record (`Ground, Well, Panel, Raised, Dialog, Placeholder,
Ink, InkSecondary, InkTertiary`) with two static defaults holding today's values; `ZuneGrounds.From(ColourLadder)` maps
1:1. `ZuneStyle.DarkTokens(t)` / `LightTokens(t)` / `ShellOf(...)` (`ZuneStyle.cs:165-209`) take a `ZuneGrounds`.
`ZuneRoles.For(pair, followsWindows, bigNumerals)` (`ZuneRoles.cs:137`) gains the two grounds, and its private
`Ground` / `Well` / `InkWash` pairs (`ZuneRoles.cs:280-287`) read them. Accent: with source Palette or Own, the
`ZuneAccentPair` is built from the ladders (`Dark = dark.AccentFill`, `Light = light.AccentFill`, `LightInk = light.AccentInk`)
instead of from `style.accent`. Zune's alpha constants (`HoverA`, `SelectedA`, …) stay: they are Zune's look.

**WinUI** (`WinUiStyle.cs`, `WinUiRoles.cs`). `WinUiTokens`' six ground statics and two tertiary-text statics
(`WinUiRoles.cs:31-49`) become a `WinUiGrounds` record per mode (`Base = Well`, `BaseAlt = Deep`, `Content = Ground`,
tertiary text = `InkTertiary`). `BaseDefinition()` (`WinUiStyle.cs:128-155`) takes the two grounds; the layer role
pairs that hold literals (`WinUiRoles.cs:213-236`) read them. Material: `Materials = new MaterialPolicy { Light = lightLadder is null ? chosen : WindowMaterial.None, Dark = … }`
(D5), so the Backdrop option keeps working for a default mode and is shown disabled with a reason for a palette mode.
Memo key gains the `ColourPair` (`WinUiStyle.cs:87`). WinUI also needs its first colour control in the options view
(`WinUiSettings.Theme.cs:343`): the palette rows are core rows, not theme options, so this is the new row kind of §3.7
and not a `ColorOption` branch.

**Classic** (`ClassicStyle.cs`, `ClassicRoles.cs`). Most of Classic's roles are `ColorRole.By` recipes over live
tokens (theming.md §2 "Colour roles"), so `ColourTokens.Apply` carries most of the change. To do: the twelve literal
colours in `ClassicRoles.cs` (the dark event plates at 194-195, the art placeholder at 197, the acrylic spec at 294)
become ladder reads through a `ClassicGrounds` argument to the table builder; `ColorScience.ShellGround` and
`ContentSurface` (`Platform\ColorScience.cs:60-70`) derive the chrome ground from `FillSolidBase` with a fixed
light drop and dark lift, which is right for neutral greys and must be checked on a saturated ground (if the 6 % lift
lands off the palette's `Raised`, the style passes the ladder's rungs instead of the derived ones). Classic's
definition memo (`ClassicStyle.cs:44-61`, one entry for the last OS ramp) becomes a small dictionary keyed on
`StyleInputs`. Material as WinUI. Classic's own window material source was **not traced** (its `MaterialPolicy` is
`FluentDefault`, "no opinion", `ThemeTables.cs:189-200`); find where the window material is applied for Classic
before writing the override.

### 3.7 Settings model: one new row kind, five dials

A palette pick is "one value from a list, each drawn as a small coloured tile". `SettingsRowKind.Picker` already
means "a strip of picture cards", and every theme draws it by id with custom art. Rather than five more id special
cases per theme, add one kind and a little data:

```csharp
// Screens\SettingsSpec.cs
public enum SettingsRowKind : byte { …, Swatches /* one value from a strip of colour tiles */, Colour /* a free colour */ }

/// <summary>One tile of a Swatches row: up to four colours drawn as a 2x2 or a split square, and its name.</summary>
public readonly record struct SwatchTile(string Name, string? GroupName, ColorF A, ColorF B, ColorF C, ColorF D);
```

`SettingsDial` gains `Func<IReadOnlyList<SwatchTile>>? Tiles` (live: the custom tile changes as the user edits).
New group in `Screens\SettingsSpec.Appearance.cs`, between `mode` and `sizeAndText` (so App mode sits directly above
the two palettes it switches between):

| Dial id | Kind | Writer |
|---|---|---|
| `appearance.colours.light` | Swatches | `SettingsVerbs.SetLightColours(int index)` |
| `appearance.colours.dark` | Swatches | `SettingsVerbs.SetDarkColours(int index)` |
| `appearance.colours.accent` | Segmented (Theme · Palette · My colour) | `SettingsVerbs.SetColourAccent(int)` |
| `appearance.colours.accentPick` | Swatches (named accents) or Colour (own); visible by source | `SetNamedAccent` / `SetOwnAccent` |
| `appearance.colours.artwork` | Toggle; visible while a palette is active for the current mode | `SetColourArtwork(bool)` |
| `appearance.colours.custom.light` / `.dark` | Action ("Edit…"); visible while that mode's pick is Custom | opens the editor |

Index 0 of each palette row is "Theme default"; the last is "Custom". Because they are ordinary dials, they appear in
Settings search and the command palette's "find a setting" for free, and `setting id=appearance.colours.dark value=3`
drives them from the inspector.

`SettingsHome.cs` (the landing "your setup" glance, `Screens\SettingsHome.cs:33-47`) shows the theme and mode today;
extend its theme value to "WinUI · Mocha" when a palette is active for the current mode.

### 3.8 The picker UI, per theme

The three themes draw the same model with their own controls. Heights are fixed per row, and the rows that appear and
disappear (`accentPick`, `artwork`, the custom "Edit…") use each theme's existing disclosure (`SizeMode.FlowReveal`),
never a snap.

**WinUI** (cards, `WinUiSettings.Body.Appearance.cs`; the App mode card is the model, lines 24-27):

```
 Colours
 ┌───────────────────────────────────────────────────────────────────────────────┐
 │ ☀  Light colours                                             Catppuccin Latte ⌄│
 ├───────────────────────────────────────────────────────────────────────────────┤
 │  ┌──┬──┐  ┌──┬──┐  ┌──┬──┐  ┌──┬──┐  ┌──┬──┐  ┌──┬──┐                          │
 │  │▒▒│░░│  │▓▓│░░│  │▒▒│▓▓│  │░░│▒▒│  │░░│▓▓│  │ +│  │                          │
 │  ├──┼──┤  ├──┼──┤  ├──┼──┤  ├──┼──┤  ├──┼──┤  ├──┼──┤                          │
 │  │██│▒▒│  │██│▒▒│  │██│░░│  │██│▓▓│  │██│▒▒│  │  │  │                          │
 │  └──┴──┘  └──┴──┘  └──┴──┘  └──┴──┘  └──┴──┘  └──┴──┘                          │
 │  Default  [Latte]  Snow     Gruvbox  Paper    Custom                           │
 └───────────────────────────────────────────────────────────────────────────────┘
 ┌───────────────────────────────────────────────────────────────────────────────┐
 │ ☾  Dark colours                                              Catppuccin Mocha ⌄│
 ├───────────────────────────────────────────────────────────────────────────────┤
 │  Default  Frappé  Macchiato  [Mocha]  Polar Night  Gruvbox  Dracula  Ink  Custom│
 └───────────────────────────────────────────────────────────────────────────────┘
 ┌───────────────────────────────────────────────────────────────────────────────┐
 │ ◉  Accent colour                         [ Theme │ Palette │ My colour ]       │
 ├───────────────────────────────────────────────────────────────────────────────┤
 │  ● ● ● ● ● ● ● ◉ ● ● ● ● ● ●      Mauve                                        │
 └───────────────────────────────────────────────────────────────────────────────┘
 ┌───────────────────────────────────────────────────────────────────────────────┐
 │ 🖼  Artwork colours                                                    Off ◯── │
 │    Let album art tint the window on top of your colours                        │
 └───────────────────────────────────────────────────────────────────────────────┘
```

Each palette card is a settings expander (the theme-options expander's shape, `WinUiSettings.Theme.cs:400-455`); the
tile grid wraps to as many lines as the width needs (Compact: three to a line, each tile at least 40 DIP tall, the
Compact hit floor). The mode that is active now carries a "Now" tag in its header, so it is clear which row the user
is looking at the result of.

**Classic** (`ClassicSettings.Theme.cs`): the same two rows as `Controls.PickerStrip` strips (the control the colour
swatches and the Surfaces cards already use, lines 565-626), tiles drawn by a new `ColourTile` beside `ColorSwatch`
(line 619). Accent source is the existing `Selector` (line 591's control).

**Zune** (`ZuneSettings.Theme.cs`): lowercase word rows and square tiles, the shape of its accent swatch row (lines
289-317): `light colours` / `dark colours` as two tile rows with the picked one ringed in the ink, `accent` as its
existing five-square row replaced by the palette's named accents when the source is Palette. Zune's live preview aside
(`Aside`, line 321) already redraws from live roles, so it previews a pick with no extra work. Titles stay Zune's
display size; a long family name wraps, it never shrinks.

**The custom editor** (all themes; a dialog body, registered as one view key per theme like the other dialog bodies,
theming.md "The overlays and the dialog bodies"):

```
 ┌ Custom dark colours ─────────────────────────────────────────────────────────┐
 │                                                                              │
 │  Background   ■ #1E1E2E      ┌─────────────── preview ───────────────────┐  │
 │  Surface      ■ #313244      │ ▌ Title bar                               │  │
 │  Text         ■ #CDD6F4      │ ┌────────┐  Heading                       │  │
 │  Muted text   ■ #A6ADC8      │ │  nav   │  Secondary line · tertiary     │  │
 │  Accent       ■ #CBA6F7      │ │  item  │  ┌──────┐ ┌──────┐  [ Play ]   │  │
 │  Second surface ■ (auto)     │ └────────┘  │ card │ │ card │   link      │  │
 │                              │ ▬▬▬▬▬▬▬●────────  player bar              │  │
 │  ┌──────────────────────┐    └───────────────────────────────────────────┘  │
 │  │   colour picker      │                                                    │
 │  │   (spectrum, hue,    │    ⓘ Muted text was darkened a little so small     │
 │  │    Hex R G B)        │      print stays readable.                         │
 │  └──────────────────────┘                                                    │
 │                                                                              │
 │  Start from: [ Mocha ⌄ ]                       [ Reset ]  [ Cancel ] [ Apply ]│
 └──────────────────────────────────────────────────────────────────────────────┘
```

- The six wells are a radio group; the picker edits the selected one (one `ColorPicker`, one `Signal<ColorF>`).
- The preview is a fixed-size miniature drawn from the **solved** ladder (`ColourSolver.Resolve` on the editor's
  draft), through bound `Fill` / `Color` props: a drag repaints, it does not relayout or rebuild a definition.
- The note line has a reserved height and shows `ColourSolver.Explain` (what the floors changed). The well shows the
  user's own colour; the preview shows the result.
- "Start from" copies a built-in's seeds into the draft: the fast path to "Mocha, but with a blacker ground".
- Apply commits (persist, resolve, install); Cancel discards the draft. Nothing outside the preview changes before
  Apply, so there is no per-drag retheme of the whole window.
- Narrow window: the preview moves under the wells and the dialog scrolls; the picker keeps its fixed size.

**Setup** (`Screens\Setup.UI.Theme.cs` and the three `*Setup.Pages.cs`): the theme page already shows the App mode row
under the theme cards. Add the two palette rows under it, collapsed to their header ("Light colours: Default ⌄"), so
the wizard page does not grow for a user who does not care. No custom editor in setup.

**Theme picker previews.** The theme cards show a photograph of each theme's Home per mode
(`assets\themes\{classic,winui,zune}-{dark,light}.jpg`, CHANGELOG "The theme picker shows each theme as it looks").
They cannot show a palette. Leave them as the default look; the palette tiles are their own preview.

### 3.9 Following Windows

No new mechanism. `Appearance.OnSystemColorsChanged` flips the kind while the mode is System (`Appearance.cs:137-145`)
and `Switch(kind)` installs `ThemeHost.ActiveDefinition` for that kind. Since the definition now holds the light
palette in its `Light` side and the dark palette in its `Dark` side, the flip lands on the other palette. With the
same material on both sides it is the live cross-fade; with different materials it is the transaction (D5).

One addition: while the accent source is Palette or Own, `ApplySystemAccent` still records the OS ramp in
`ThemeHost.OsAccent` (so switching the source back to Theme is immediate), but the styles ignore it, so an OS accent
change no longer re-installs an identical definition (the memo returns the same instance and `Tok.Use` adopts the
reference).

### 3.10 Artwork colours (D4)

`ArtworkColorPolicy` gains a third member and core applies the palette's veto in one place:

```csharp
public sealed record ArtworkColorPolicy(bool AccentFromArtwork, bool NowPlayingColors, bool Washes = true);
```

`ThemeHost.PublishFacts` (`ThemeHost.cs:409-425`), after the structure's policies are validated: when a palette is
active for the current kind and `theme.palette.artwork` is off, publish
`Artwork with { AccentFromArtwork = false, Washes = false }`. `NowPlayingColors` is left to the theme (D4: media
surfaces keep their colours). Readers: the page accent already reads `ShellPolicy.Tracked.Artwork`
(`Entities\Detail.UI.cs:1291`); the wash leaves (Classic's `ClassicCoverLeaves`, which read the Surfaces option
themselves, `ClassicCoverLeaves.cs:226-232`, and Zune's `ZuneMaterial`) additionally read `Artwork.Washes`. Facts must
be republished on a kind flip (the veto depends on the current mode's pick): `ColourChoice` subscribes nothing; the
kind flip already runs through `Design.SeedPalette`, so add the republish to `LiveAppearancePorts.ApplyKind` /
`ApplyKindInTransaction` (`Appearance.cs:237, 245`).

Classic's Surfaces cards and "Accent from artwork" toggle are shown disabled with "Off while your colours are on
(Appearance › Colours › Artwork colours)" while the veto holds.

### 3.11 Threads

Everything is UI thread, like every signal and the token set (`ThemeHost.cs` header: "UI thread only"). The solver is
pure and cheap (bounded 200-step lightness walks, the bound `ZuneAccent.Floored` already uses), and runs only on a
pick or an Apply. No background work, no I/O beyond the settings store's own write.

### 3.12 Files

New:

| File | Content |
|---|---|
| `Theming\Colours\ColourSpec.cs` | `ColourSeeds`, `NamedAccent`, `ColourSpec`, `ColourLadder`, `ColourPair`, `StyleInputs`, `AccentSource` |
| `Theming\Colours\ColourSolver.cs` | `Resolve`, `Explain`, the floors |
| `Theming\Colours\ColourCatalog.cs`, `ColourCatalog.Data.cs` | the built-in table |
| `Theming\Colours\ColourChoice.cs` | stored choice, verbs, `Apply` |
| `Theming\Colours\ColourTokens.cs` | `Apply(TokenSet, ladder)`, `Shell`, `Ramp` |
| `Theming\Colours\CustomColours.cs` | the `v1;…` line: parse, format |
| `Theming\Contracts\ColourEditorProps.cs` + `ViewKeys` entry | the editor dialog body's contract |
| `Themes\{Classic,WinUi,Zune}\Structure\Views\Screens\*ColourEditor.cs` | three editor bodies |
| `changes\unreleased\184-custom-colours.md` | the change file (`issues: [184]`) |

Changed: `Theming\Composition\ThemeManifest.cs` (contract), `ThemeComposition.cs`, `ThemeHost.cs`, `Theming\Appearance.cs`,
`Theming\Policies\ArtworkColorPolicy.cs`, `Platform\Platform.cs` (keys), `Platform\Design.cs` (delete the picker
tombstone: it is no longer true), `Shell\Shell.Host.cs` (seed order), the three `*Style.cs` and `*Roles.cs`,
`Screens\SettingsSpec.cs`, `SettingsSpec.Appearance.cs`, `SettingsHome.cs`, `Screens\Setup.UI.Theme.cs`, the nine theme
Settings / Setup view files listed in the cross-plan notes, `assets\loc\*.json`, `docs\guide\theming.md` (a "Colours"
section; the tombstone's doc twin), `THIRD-PARTY-NOTICES.txt`.

Engine (`fluent-gpu`): `FluentGpu.Controls\ColorPicker.cs` (`onCommit`). Nothing else is required; see §4 for the two
engine items that may surface.

---

## 4. Phases (all ship in 0.4)

Each phase leaves the app shippable. Sizes: S about a day, M two to four, L a week.

| # | Phase | Size | Engine |
|---|---|---|---|
| 1 | **Model and plumbing, no UI.** `Theming\Colours\*` (spec, solver, catalog with Wavee Ink + Paper and Catppuccin's four flavours, choice, tokens). The `IStyleLayer.Definition` contract change with all three styles accepting `StyleInputs` and ignoring `Colours` (bit-identical output: the memo returns today's instances). Keys, seed order. Developer-only dial `appearance.colours.dark` as a plain Choice so the inspector can drive it. | M | none |
| 2 | **Zune takes the ladder.** `ZuneGrounds`, `ZuneRoles.For(…, grounds)`, tokens, shell, accent from the ladder. Zune is first because its ladder is flat and opaque: it proves the solver and the mapping with no Mica question. | M | none |
| 3 | **WinUI takes the ladder.** `WinUiGrounds`, the layer roles, the material per mode (D5), the transaction path for a mixed pair. | M | possibly: see below |
| 4 | **Classic takes the ladder.** `ColourTokens.Apply` over the Neutral sets, the twelve literals, the chrome ground check, material. Classic is "verified, touch only for registrations unless asked": this phase is asked for by the feature, and its files are listed in the report. | M | none |
| 5 | **The picker in Settings, three themes.** The `Swatches` row kind, the five dials, tiles, the accent row, the disabled-with-reason states on each theme's own accent options, Settings Home's glance, loc strings, the Developer gate removed. Remaining built-ins (Nord, Gruvbox, Dracula) and notices. | L | none |
| 6 | **Artwork colours (D4).** `ArtworkColorPolicy.Washes`, the veto in `PublishFacts`, the toggle, the disabled Classic rows. | S | none |
| 7 | **The custom editor.** Dialog body per theme, the draft model, the preview, `Explain`, Apply / Cancel / Reset / Start from. | L | `ColorPicker onCommit` (S); optional with the Apply button |
| 8 | **Setup and polish.** The two collapsed rows on the setup theme page; the docs section; the change file; a full gallery pass. | S | none |

Phases 2, 3, 4 are disjoint files (one theme each) and can run as parallel lanes after phase 1. Phase 5 can start
against phase 2 alone. Total: about three to four weeks of lane time, less in parallel.

**Engine items that may surface** (fix in fluent-gpu, per the standing rule, without asking):

- **`ColorPicker.onCommit`** (phase 7): planned above.
- **Hard-coded engine colours.** A few engine tokens are fixed across palettes by design ("fixed cross-palette values",
  `Tokens.cs:513-515`), and controls may hold literals (caption glyphs, the title bar). Phase 2's renders will show any
  control that ignores the token set; each one is an engine fix to read a token.
- **Window material change on a kind flip under a transaction.** The path exists (`Appearance.cs:150-158`,
  theming.md "Apply and switch": "The DWM backdrop change … is ordered with the dissolve by the engine") but today no
  shipped theme has different materials per kind (WinUI and Zune set both to one value, `WinUiStyle.cs:107`,
  `ZuneStyle.cs:150`). Phase 3 is its first real use: budget a day for whatever it finds.
- **Mica and the dark-mode title bar.** With `WindowMaterial.None` the DWM frame colour and caption colour must follow
  the palette's ground. Not traced in this research: check `FluentApp.SetWindowMaterial` and the immersive dark-mode
  flag on a light palette in Windows dark mode.

### After 0.4 (not built, not half-built)

- **Import / export a palette file** (the `v1;…` line as a `.waveecolours` file; a Spicetify `color.ini` reader).
  0 poll votes, 1 comment. The stored form is import-ready.
- **More than one custom palette per mode**, named, with duplicate and delete.
- **Per-token overrides** ("set every colour myself" taken literally): an Advanced list over `ColourLadder`'s rungs.
- **Hue-restricted artwork tint** (D4 option c: washes clamped to the palette's hue range).
- **Secondary / tertiary accents** (requested in #196; the role model has one accent voice per theme today, three
  budgeted roles in `R.Ink`, so this is a role-table design task, not a palette task).
- **Flat gradient grounds** (requested in #196).
- **Per-theme palette pairs** (D2).
- **A high-contrast palette** (`ThemeDefinition.HighContrast` is reserved and unread, `ThemeDefinition.cs:24-26`).
- **Scheduled switching** (sunset/sunrise without Windows' own switch).

---

## 5. Risks, unknowns and verification

### Risks

| Risk | Why | Mitigation |
|---|---|---|
| **Literal colours that never read a token.** | About 64 / 76 / 66 colour literals sit in the Classic / WinUI / Zune theme folders (a grep count of `FromRgba`), plus about 55 in core (`Entities`, `Shell`, `Screens`, `Platform`). Many are legitimate (on-media scrims, data colours, the Windows caption red), some are grounds that will show as grey patches on a coloured palette. | This is the biggest risk and it is discovered by rendering, not by reading. Phase 2-4 each end with a full gallery in Mocha and Latte; every patch is fixed at its source (a role read), never by a per-palette exception. WVTH003 already bans new look literals in theme structures. |
| **Contrast on user colours.** | A user can type any six colours. | The solver's floors (§3.3) make it impossible to install an unreadable ink; the mode check refuses a wrong-polarity ground. The inspector's `contrast.text` rule is the proof. |
| **Built-in palettes do not pass 4.5:1 as shipped.** | Catppuccin's and Nord's lighter accents are below 4.5:1 as text on their light grounds. | Fills stay exact; text use is floored. Say so in the picker's caption ("Text colours are adjusted where needed to stay readable") so a purist is not surprised. |
| **The artwork system assumes neutral grounds.** | Page tones, placeholders and the "chrome scheme" grading (`Design.cs` §7) are tuned for grey grounds. | D4 turns them off by default under a palette. With the toggle on, results are the user's choice; note it in the caption. |
| **Mica off under a palette** | Some users pick WinUI for Mica. | D5 is per mode and the Backdrop row says why it is disabled. Owner decision. |
| **Theme contract churn.** | `IStyleLayer.Definition` changes for all three themes at once. | Phase 1 changes the signature with identical output and ships alone: any regression is then a diff of zero colours. |
| **Memo growth.** | WinUI and Zune memoize definitions in an unbounded dictionary keyed by inputs (`WinUiStyle.cs:85`, `ZuneStyle.cs:99`); the custom editor could add an entry per Apply. | Apply is a deliberate click, not a drag; cap the dictionaries at a small LRU (8) in phase 1. |
| **Licences and names.** | Catppuccin, Nord, Gruvbox, Dracula are third-party projects. | Verify each licence file and add notices (D6). Dracula's light variant is left out until its terms are read. |
| **Poll size.** | Four votes. | Scope C covers every vote at a bounded cost; the expensive, speculative parts are in "After 0.4". |

### Unknowns (not verified in this research)

- Where Classic's window material is applied (its `MaterialPolicy` has no opinion).
- Whether any engine control paints a literal colour instead of a token (found by rendering).
- How the DWM caption and frame colours behave with `WindowMaterial.None` and a ground that disagrees with the
  Windows mode (a light palette forced in Windows dark mode).
- Whether `ColorScience.ShellGround` / `ContentSurface`'s fixed drop and lift read correctly on saturated grounds.
- The exact hex tables of the built-ins: to be copied from each project's published source, not from memory.
- Pop-out windows (the mini player, `Shell\MiniPlayer.cs:473` tracks its own material signal): they share `Tok`, so
  colours follow, but their material handling under D5 was not read.

### Verification (no tests: a clean Debug build, the headless inspector, screenshots looked at)

Build: `dotnet build src/apps/Wavee/Wavee.csproj -c Debug --artifacts-path <your artifacts folder>` (warnings are
errors); launch only a copy (fgpu skill §6). One fresh `inspect.py` process per configuration.

| Phase | Check |
|---|---|
| 1 | Byte-identical renders before and after for `home`, an album, Settings in all three themes, light and dark (same commands on a base-commit build, compare the PNGs by eye and by file hash). `setting id=appearance.colours.dark` lists the dial. |
| 2-4 | Per theme, per palette (at least Mocha, Latte, Ink, Paper) at the theme's Compact / Standard / Wide board sizes: `mount w=… h=… theme=<t> kind=dark route=home` → `setting id=appearance.colours.dark value=<i>` → `frames 90` → `lint min=warn` → `render`. Routes: home, album, playlist, artist, search, library, settings, the queue and right panel, the fullscreen stage, a menu and a dialog (`state` / `dialog` verbs). `--gate error` must exit 0 with `contrast.text` in the rule set; read every warn. Look at each PNG for grey patches (a literal ground), unreadable ink, lost row hover, invisible hairlines and focus rings. Many at once: `tools/themes/gallery.py --theme <t>` with the palette set first. |
| 2-4 | **The switch itself:** `trace=90` around `setting id=appearance.appMode value=1` then `value=2` with a mixed pair (default light, Mocha dark) and with a same-material pair: no key `movedAtOnce`, nothing rebuilt on the live path; on the transaction path the root must not remount (page scroll and an open disclosure survive). A `film` of the dissolve to look at the cross-fade. |
| 3 | Real-window check on the secondary monitor (`--window-monitor secondary --window-rect …`, never focused): Mica on for the default mode and off for the palette mode, the title bar and caption buttons readable in both, across a Windows light/dark flip with the app mode on System. This is the one thing the headless backend cannot show. |
| 5 | Settings › Appearance in three themes at Compact / Standard / Wide, light and dark: `lint min=error` clean; tiles at least 40 DIP at Compact; `crossing` at each tier edge the Appearance page spans, both ways (the tile grid re-wraps: nothing may move at once or rebuild); a `trace` over toggling the accent source (the `accentPick` row reveals: animated, no jump). `settings q=mocha` finds the row. |
| 6 | Album and artist pages in Classic with a palette: washes off by default, on after `setting id=appearance.colours.artwork value=true`; the fullscreen stage coloured by the cover in both states. |
| 7 | The editor in three themes: `perf do=…` over a scripted spectrum drag (`drag key=<spectrum> …`) shows zero hot-phase allocation and no relayout outside the preview; a draft with white-on-white text shows the note and a readable preview; Apply installs; Cancel leaves the window untouched (render before and after equal). |
| 8 | The setup wizard's theme page in three themes (`state` / setup route), both rows collapsed and expanded; `trace` over the expand. |

Done for the feature: every row above green, the screenshots looked at beside the default-palette renders, and the
change file written. No Release build until the 0.4 release itself.
