# Theming

> **Scope.** How Wavee's themes are built: the layers, the composition, the options a theme exposes, the settings that
> store them, the migration of retired switches, the analyzers that fence it all, and how a test mounts one view. The plan
> (`docs/themes/plan/technical-plan.md`) is the design; this is the working guide for the code that exists. T1
> (`feat/theming-core`) ships the machinery with Wavee Classic as the only theme and **no user-visible change**.
>
> **Zune** (P5, Developer-only) has its own guide, one section per lane: [theming-zune.md](theming-zune.md).

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
- **Contract version 1.11** (`Contracts\ContractVersion.cs`, which keeps the history): the published view keys and style keys, their props records
  and the role vocabulary. A theme records the version it was built against in its manifest; `ThemeHost.Install` refuses a
  composition whose major differs or whose minor is newer than the core's. 1.1 (CT4) appended `LyricsState.Offline`, the
  optional `LyricsLinesStyle.Offline` picture and `PlayerBarSlots.VideoSplit`; 1.6 (CT6b) added `CoverVeilProps.Hold`; 1.7 (CT9) `ThemePolicies.Artwork` and `WashRoles.Rich`; 1.8 (CT8) `Overlay.OfflineVacancy`; 1.9 (CT6a) `DetailFrameProps.Head`, `LikedChipBarProps.Inline` and `TrackRowSkin.Quiet`; 1.10 (X2 follow-up) `LibraryAlbumPaneProps.Columns`; 1.11 (P3t TC9) `ThemePolicies.Overlays` and `Overlay.ContextSheet`; 1.12 (TC1) `ChromeRowState.TabCount` / `.TabMaxWidth`, the optional `ChromePolicy.Compact` / `.Huge` budgets with `ChromeBudget.TabsAsCount`, and `ctx.TierReader()`; 1.13 (TC3) `StagePolicy.Aspect` as three `TierLadder`s and the per-tier `StageTierMetrics`; 1.14 (TC5) `ThemePolicies.DetailTiers`, the `DetailLadders` ids, `Track.ColumnSet.Compact`, `Track.RowMetrics.ShowDuration`, `DetailInsightsProps.Docked` and `ContextSheetProps.Content`; 1.15 (TC2) the panel's placement per window tier (`PanelPlacement`, `PanelView.Placement`, `PanelPolicy.Tiers`, the frame's second docked pane), `PlayerBarPolicy.Windows`, the mini bar's `PlayerBarProps.ProgressLine` / `OpenNowPlaying` and `SeekRailProps.Hairline`, `ChromeBudget.TrailingInDrawer`, `ChromeRowState.TabsHidden` and the drawer's account row (`NarrowDrawerProps.Account`, `ProfileChipProps.Drawer`); `PanelPolicy.InlineMin` is removed; 1.16 (TC7) `LibraryPageProps.SetPageWidth` / `.Kinds` replace `IsCollapsed` / `SetCollapsed`, and the `LibraryLadders.Layout` sub-ladder; 1.17 (TC8) `DialogFit.FormWidth` / `.CommandHeight` / `.FormCardWidthFor` / `FormBodyWidth` and `OverlayFit.ShowForm`; 1.18 (TC6) `ThemePolicies.ArtistTiers` (`ArtistTierPolicy`), the `ArtistLadders` ids, `ArtistHeroMetrics.PhotoHeight`, `Controls.FollowToggle.Size` and the sized `Controls.PlayButton` / `PrimaryButton`; the core's artist and profile hero ladders and heights (`ArtistHeroLayout.TierFor` / `For`, `ProfileHeroLayout.For`), `ArtistSections.TopBandWide`, `ReaderShape.Narrow` / `Medium` and `ProfileGridFit`'s thresholds are removed; 1.19 (compact control sizes) `DialogFit.CommandSize` / `.CommandMinWidth` / `.UniformCommands` replace `DialogFit.CommandHeight` (the engine's own command buttons at a band's size, no hand-built row), `OverlayLayout.MenuSize` and the optional `ToastDock.Lane` (the engine's `ToastLaneStyle`); 1.20 (the Compact overlays fix) the optional `PanelProps.Transport` (`PanelTransportProps`, `PanelTransportFacts`: the transport the now-playing sheet carries, since it covers the player bar); 1.21 (the Huge detail and library layouts) the optional `DetailFrameProps.Aside` (`DetailAsideProps`), the core slot `Detail.FrameSlots.Aside` and context `Detail.AsideDocked`, the `Album.Aside` key (`AlbumAsideProps`), `DetailTierLayout.AsideDocked` / `.InsightsRestOpen`, `DetailTierPolicy.HugeWide` / `.HugeWideRung`, `AboutLayout.Aside`, `NavPanePolicy.HugeWidth`, `ThemePolicies.LibraryTiers` (`LibraryTierPolicy`) and `ThemeState.IsCustomized` / `IsStored`; 1.22 (the remaining artist and stage items) `ArtistTierPolicy.ChartRows`, `DiscographyMetrics.Drawer` (`Album.DrawerShape`) with `DrawerVerdict.Pitch`, the tier's columns and rows in `ArtistChartSkeletonArgs.For`, the stage's `StageTierMetrics.GalleryToFoot` / `.UpNextW` / `.ArtBarsH` / `.IdentityCard` / `.GalleryOnDemand`, `StagePolicy.HugeWide` / `.HugeWideRung`, `StageLayers.UpNext`, `QueueStagePaneProps.Aside` and `PanelProps.VideoTitle`; 1.23 (the facts a view is drawn in) `DetailHeroStyle.Skeleton` and `DiscographyMetricsTemplate` take the drawn-in `TierFacts`, `ITrackCommandKit.In(in TierFacts)` and `TierContext.DrawnIn`; 1.24 (the remaining board items) the now-playing sheet's `PanelTransportProps.Source` / `.Device` / `.ToggleDevicePicker`, the unavailable-songs bar (`TrackChromeTemplates.UnavailableBar` / `.UnavailableBarExtent`, `TrackUnavailableBarArgs`), `DetailTableTier.ListIndex` with `Track.ColumnSet.IndexHidden`, `PlaylistRecsProps.Lead`, `VerticalItemRole.Recommendations`, `FacetRowProps.ToggleFriends`, `SearchBodyModel.ArtistRail`, `SearchPageProps.SelectArtists`, `LibraryTierPolicy`'s Small widths and `CardCellMinFor`, and `LibraryShowPaneProps.Primary` taking a `ControlSize`; 1.25 (the last Classic gaps) the optional `PanelTransportProps.DevicePickerRequest` and the library grid's per-tier gaps (`LibraryGridGaps`, `LibraryTierPolicy.*GridGaps` / `GridGapsFor`); 1.26 (P5 round A) the Zune role vocabulary (twelve type roles, `ShapeRoles` / `R.Shape`, four `StyleMetrics` members), page chrome row 2 (`PageChrome.Views` / `.Actions` / `.Backdrop`, `PageChromeSink`) and the band (`NavPaneProps.Band`, `BandProps`); 1.27 (P5 round B) `Track.RowState.OnThisPc`; 1.28 (P5 round D) `Home.Quickplay` (`HomeQuickplayProps`).
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
Corners = R.Shape.All(ShapeRole.Cover)                     // the corner radius of a kind of surface (ShapeRoles, contract 1.26)
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

**The pivot-and-panorama vocabulary and the shape roles (contract 1.26, P5).** A Zune-shaped structure reads type and radii Classic never
needed. They are members of the same records, so every style defines them (S2) and Classic gives each one the value its surfaces use today:

| Role | What it is | Classic's value (what it draws today) |
|---|---|---|
| `R.Type.Panorama` / `PanoramaCompact` | the page's name set larger than the column / the same where the page is narrower than `StyleMetrics.PanoramaCompactBelow` | `SurfaceDisplay` (40/52/400 display) / `PageHero` (28/36/600) |
| `R.Type.Hero` | a record page's hero title | `DetailHero` |
| `R.Type.Pivot` / `SubPivot` | a first-row pivot / a second-row one | `PivotLabel` (19/25/350 display) / Body 14/20 (the stock `SelectorBar` word) |
| `R.Type.Numeral` / `NumeralLarge` | a row's number / a number that leads a feature row | Caption 12/16, both (`ClassicRowCells.NumberCell`) |
| `R.Type.NowPlayingDisplay` | the full-screen now-playing title set as type | Title Large 40/52/600 display (the stage hero's title) |
| `R.Type.Wordmark` | the app's name where the frame writes it | Caption 12/16/600 (the stage top bar's "Wavee") |
| `R.Type.LyricsLine` | a line on the full-size lyrics surface | 36/46/700 (`Lyrics.Surface.Timed(large: true)`) |
| `R.Type.Heading` / `QuickplayHead` | a section heading / the head of a Home quickplay group | `RailHeader` / `ModuleHeader` |
| `R.Shape` (`ShapeRoles`, `ShapeRole`) | one uniform radius per kind of surface: `Card`, `Cover`, `Control`, `Overlay`, `Tile`, `PinArt`, `Avatar`, `CurrentMark`, `ContentSeam`, `OmniRow`, `SeekTrack`; read as `R.Shape.Cover`, `R.Shape.For(role)` or `R.Shape.All(role)` (a `CornerRadius4`). A radius at or above half the box is a full round | 8, 6, 4, 8, 8, 6, 999, 2, 8, 4, 2 (the engine ramp's card, control, overlay and full rungs; covers and nav art 6; the nav pill 2; the slider track 2) |

Two helpers belong to the table, for a composition that applies an option to the style's roles (plan 6.4.2):
`TypeRoles.WithPanorama(PanoramaSize)` puts `Panorama` on the Display (68/92) or Title Large (40/52) rung and keeps the style's face and
tracking; `TypeRoles.WithBigNumerals(bool)` is the table itself when on and turns both numeral roles into the Body rung when off. Both start
from the table they are given, so "on" is never a restore of an earlier "off". The style metrics gain `DockedPane` / `DockedPane4K` (each
docked side pane's width from the second-pane rung and from Huge 4K; how many dock is the structure's), `PaneItem` (a navigation pane item's
height) and `CardGap` (the gap between cards); a density option moves `RowSingle`, `PaneItem` and `CardGap` together. Classic publishes
380 / 420 (its Huge panes' floor), 36 (`SidebarGeometry.RowHeight`) and 12 (`R.Space.GridGap`). No Classic view reads the new roles or
metrics yet, so nothing Classic draws changes. A **shape is not a colour**: a table that differs in `Shape` is a transaction
(`DiffersOnlyInColours` is false). `ClassicRolesTests` pins Classic's values against the surfaces that draw them and walks the role records by
reflection (every style defines every role, every text role has its `R.Type` call).

**What stayed in `Design`.** The behaviour (`Entrance`, `Reveal`, `Nav`, `Cadence`, `Reduced`; the frame clock left for `Platform\FrameTime.cs`,
a clock and not a token, so a `[CoreHost]` may read it), the Fluent duration ladder
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
| Wash | `WashRoles` | `R.Wash` | `NeutralGround`, the strengths (`HeroAlpha`, `ShelfAlpha`, `HeroVeilPull`, `HeroVeilPeak`, `CoverMix`, `ConcertMediaMix`, `PageTonePlaneAlpha`, `BlendWashTop`, `BlendWashBoundary`, `ShellTintAlpha`) and the recipes (`PageToneFromHue`, `ShellTintFromScheme`, `BlendWashLadder`) |
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
| **A colour or an alpha that differs by kind is a role**; a view never writes `Tok.Theme == ThemeKind.Light ? a : b` | The branch is the style's decision. The 27 branches in `Design.cs`, `Controls.cs`, `Concert.UI.cs` (now `ClassicConcertCells.cs`), `Deck.UI.cs`, `Rail.UI.cs`, `ClassicMaterial.cs` (was `Shell.Masthead.UI.cs`) and `Detail.UI.cs` are role reads now |
| **The polarity as an ARGUMENT is `R.Kind` / `R.IsLight`** | Selecting the graded half of a cover (`Design.SchemeFor`, `ChromeSchemeFor`, `PlaceholderFor`), the `light` flag of the pure follow-button maths (`ButtonRules.FollowStyle`) and the theme picker's preview image need the kind, not a colour. Apart from the stage faces' own `dark` flag (`StageInk.IsDark`; T7 re-points the faces) and the install paths (`Appearance`, `ThemeHost`, `Tok.Use`), these are the only places code asks for the kind |
| **Re-derive by `Tok.Epoch`, never by the light/dark flag; never KEY a node on either** (themes plan X44, tuning F10) | `Tok.Epoch` moves for a kind flip AND a definition swap: the shell tint binder keys its publish effect on it, and the three pages that republished a `Signal<ThemeKind>` (Detail, Artist, Profile) re-derive their accent from an effect keyed on it. A wash or tone plane is keyed on its surface (`Palette.ToneKey(route)`), not the theme: the live cross-fade re-runs its colour in place and fades it with every other colour (EP20); a theme key would remount it and snap it |
| **A colour-only table is not a transaction** | `WaveeStyleRoles.DiffersOnlyInColours` compares the type, shape, space, motion and case roles; ink, layer, on-media, stage arm, wash and lyrics may differ. A swap that moves only those bumps `Tok.Epoch` and leaves `StyleEpoch`, so the engine takes the live cross-fade path |

**The maths is `Platform\ColorScience.cs`.** Lift, vivid, the hairline and text-ink contrast solves (the 22-iteration bisection stays 22), the page-tone
clamp, the data-hue ink, the shell ground and content step, the placeholder blend and the accent ladder take the theme **kind**, the card surface and
the fallback colour as arguments and read no token. A recipe in a style calls it; `Design.Palette` is the thin face that binds it to the active kind
and accent (`TextInk`, `ChromeAccent(scheme)`, `ChromeFromPayload`).

**Classic's colour table** is the `Layer()`, `Ink()`, `OnMedia()`, `StageArm()`, `Wash()` and `Lyrics()` recipes in `ClassicRoles.cs`: each is the body
of the `Design` expression it replaced with the kind passed in. They are pinned against the formulas written out in the test
(`Wavee.Tests\Theming\ClassicColourRolesTests.cs`: both kinds for every role whose formula reads no live token, the process's own kind for the rest) and
against a flip in the shell process, where the kind may change (`Wavee.ShellTests\ColourRolesTests.cs`, which also covers the colour-only swap and a
mounted view repainting).

**What stayed in `Design` of colour, and why.** `SchemeFor` / `ChromeSchemeFor` / `PlaceholderFor` / `WatchedPlaceholder` (they read a cover's grading
for the active polarity from the palette TABLE: data selection, not a look), `Design.Palette` (above), `Design.Wash`'s window-relative GEOMETRY and
paint budget (not a look), the shell material channel (`ShellMaterial`, `TintOwnership`, now `Platform\ShellMaterial.cs`), while the four cover-leaf components read their
alphas and recipes from `R.Wash` and are Classic's material views since T4c. The shape roles (`ShapeRoles`) joined in contract 1.26 (see Style roles
above); the icon table (`IconTable`) is not part of the vocabulary yet: its consumers are the route glyphs.

## 2a. Policies and metrics (T3)

A structure decides the numbers the core state machines run on; the machines themselves stay core. `ThemePolicies`
(`Theming\Policies\`) is the bundle a structure returns from `IStructureLayer.Policies`:

| Record | What the structure decides | Core machine that takes it |
|---|---|---|
| `NavPanePolicy` | what the Narrow and Tiny bands force (`NavPaneMode`: Expanded, Compact rail, Overlay drawer, None; `ModeFor(WidthTier)`), the expanded width bounds and default, the content floor, the seam collapse rule, the collapsed rail's width (`RailWidth`), whether the pane is resizable. The bands themselves are the window tiers (section 7): Compact is Tiny, Small is Narrow, Standard and up are Wide | `SidebarPaneModeRules`, `SidebarResizeRules`, `FrameModel`, `SidebarPaneBounds.Clamp` |
| `PanelPolicy` | the panel's min and max width, its placement per window tier (`Tiers`: `PanelTierRule` docked / floating / sheet, how many dock, the docked floor; TC2), full height (on `PanelView.FullHeight` since T6b), the mini player's `Video.PipBounds` | `Shell.ClampRailWidth`, `PanelHost.FitWidthTo` / `FitWidthToFloor`, the frame's placement effect, `WindowTiers.PanelsReserved`, `Video.PipGesture` |
| `PlayerBarPolicy` | six pressure levels (start width, `PlayerBarSlots`, spacing, art, hit targets, and what the podcast bar carries at the level: `PodcastSlots`, `PodcastLeftW`), the narrowing hold and the bar height | `Shell.PlayerBar`, `Shell.PlayerBarLayout.ForTier`, `Shell.PodcastBarLayout` (so `PlayerBarRules.Overflow` and `RightWidth` follow); `R.Space.DockReserve` answers the bar's height |
| `ChromePolicy` | the title-bar row's `ChromeBudget`: where the actions, name and forward button may appear, what each affordance costs, the search and tab widths | `Shell.Chrome` (the allocator) |
| `StagePolicy` | the stage's `AspectThresholds` (three `TierLadder`s: width, height, ratio, each edge with its hold) and the per-window-tier `StageTierMetrics` (`Metrics`, `Compact`, `Small`, `Huge`: the transport card's height and gutter, the lyrics type range; contract 1.13), with the regions a tier adds (`GalleryToFoot`, `UpNextW`, `ArtBarsH`, `IdentityCard`, `GalleryOnDemand`) and the Huge band's wide twin (`HugeWide` read from `HugeWideRung`; contract 1.22) | `Stage.Layout.ClassOf` / `Resolve` (the stage surface passes `MetricsFor(tiers)`) |

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
| 3. Core host | the existing component | keeps every hook, effect, state machine and verb; builds the props once; mounts `Slots.View(ViewKeys.X, props)`. Mark it `[CoreHost]` (WVTH008: it then builds no element and reads no `Tok`/`Design`/`R`; the rule has no baseline, so the presentation moves in the same PR) |
| 4. Template | `Themes\Classic\Structure\Views\<Family>\ClassicX.cs` | `internal static class ClassicX { public static Element Template(XProps props, in ViewContext ctx) }`: the element tree the core built before, with the same keys, ids, transitions and bound channels. It may not reference `Shell`, `Entities`, `Playback`, `Spotify`, `Store`, `Prefs` (WVTH002): what it needs arrives on the props or as a verb |
| 5. Numbers | `Themes\Classic\Structure\Rules\` | a literal size, radius, gap or opacity (not 0 or 1) is a rule or a role, not a template literal (WVTH003). The shell-level constants that came with the code (`ClassicFrameRules`) live here |
| 6. Register | `ClassicStructure.Register` | `views.Add(ViewKeys.ShellFrame, ClassicFrame.Template, "classic.shell.frame");` The template id is the slot's identity: a different id under the same key remounts the slot, the same id with a new registry only re-runs it |

**Every surface with a picture is a key; builders carry what is not a picture.** A view places another surface by mounting its key with
the props the parent carries (`ShellFrameProps.PlayerBar` carries its props and the frame mounts `ctx.View(ViewKeys.PlayerBar, props.PlayerBar)`).
A `Func<Element>` member is a core host that mounts its own keys (the content host, the narrow drawer, the overlay layer, the video layers,
the panel's lyrics and queue bodies, the profile chip, the stage's pane and caption hosts) or a behaviour control the engine wires (the
seams' splitters, the zero-size machinery). Since T13 every published key has a Classic or Common picture (`RegistryCompletenessTests`); what
core still draws itself is listed under "What stays core" below.

**What stays core, and why.** Models, state machines, signals and verbs (the sidebar band and presentation, the tiers, auto zoom,
the rail and docked-video coupling, the chrome allocator, the material mirror, the route reducer, the auth fold, the page
transition's recipe) decide and publish; a theme only draws. Behaviour the engine wires as a control (the splitter, the
shell chords, the file-drop target, the realized-node captures the probes and the scrim clip read) is built by the host and
**placed** by the template. Since the leftovers lane P3 no picture is drawn by a core component: the seek rail, time labels and credits
line (`Player.SeekRail`, `Player.TimeLabel`, ..., T6a), the panel's lyrics header, the "Next up" rows and the lyrics peek (`Panel.LyricsHeader`,
`Panel.NextUp`, `Player.LyricsPeek`, T6b), the stage's pane region (`Stage.Pane`, T7a1), the sidebar pane's layer (`NavRowKit.Frame`, T5a) are
Classic's, and their hosts, `PaneView` and the album and playlist pages' two clock hosts (the frame clock is `Platform\FrameTime.cs`, not a
`Design` token) are `[CoreHost]`. One deliberate exception is left: `Video.HoleBoxes` (`Shell\Video.UI.cs`, T7c below) builds the video
hole's ancestor chain (transparent layout boxes, the fullscreen focus root, empty placeholders) in core, because the hole rule binds every
theme and keeping that chain out of a template means no theme can put an effect between the picture and the swapchain. Zero-size
machinery (the key ladder's tick, the drag-peek watcher) returns a bare 0×0 box from a behaviour component and is not a picture.

### The frame and the content states (T4a)

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.Frame` | `ShellFrameProps` | `Shell.FrameRoot` (`[CoreHost]`, `Shell\Shell.UI.cs`) | `ClassicFrame`: the column (chords, title row, content region, player dock) under the material, the stage, the video and overlay layers, the content region (sidebar column, content card and its stroke, rail gap and reservation, both seams, the rail overlay, the narrow drawer) |
| `Shell.ContentState` | `ContentStateProps` (`Kind` is a `ContentStateKind`) | `Shell.ContentHost` (`PageBody`, `NotFoundPage`, `OfflineLane`) | `ClassicContentState`: the page and empty-body boxes and the offline lane (its NotFound arm is the Common block `CommonNotFound`, T11c) |

`Shell.ContentState` is also a **pure core decision**: the route's body (`ContentStateKind.Page`, `EmptyBody`, `NotFound`) and
the offline-lane flag. `Shell.FrameRules.ContentBodyFor(route, hasPage, developerMode)` and `ShowsOfflineStrip(auth)` decide it (a known route
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
| `Shell.TitleBar` | `ShellTitleBarProps` (`ShellFrameProps.TitleBar` replaced the frame's `ChromeRow` builder) | `Shell.TitleBarProps()` (`Shell\Shell.TitleBar.cs`) over the allocator (`Shell.Chrome`), the tab workspace's items (`BuildTabItems`), the auth fold, the history stacks, the pin | `ClassicTitleBar`: the engine `TitleBar` in merged mode (tabs island with the history buttons and the text-first strip, the centre column, the trailing identity island with the bell, friends and pin, the caption-leading magnifier in the icon form; no theme toggle, light and dark are Settings › App mode and the profile menu) |
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
| `Page.Head` | `PageHeadProps` (a `PageChrome`) | placed by the masthead; a page that draws its own head mounts it | `ClassicPageHead`: breadcrumb B (Classic tuning F5): one 40/52 title under a stable key and a 12/16/600 trail of only its ancestors on a 28-DIP slot (none at depth ≤ 2); what it holds is the route's `PageHeadForm` (Title, Drill, Hero), so the head keeps one height per route (`Rules\ClassicPageHeadRules.cs`) |
| `Shell.Material` | `MaterialProps` (the published `ShellMaterialState` as a read-only signal) | the frame; the channel is `Platform\ShellMaterial.cs` | `ClassicMaterial`: the flat tint with its brush fade, Home's three radial washes |
| `Shell.Material.PageTone`, `.ArtistBlend`, `.ArtistVeil`, `.TintBinder` | `CoverToneProps`, `CoverBlendProps`, `CoverVeilProps`, `CoverTintProps` | the mounts in `Entities\Palette.Host.cs` (pages never write `Slots.View`) | `ClassicCoverLeaves`: the four cover leaves, moved from `Design.cs` |
| `Shell.NarrowDrawer` | `NarrowDrawerProps` (`Mounted`, `Open`, and the stable `NarrowDrawerParts`) | `Shell.NarrowDrawer` (`[CoreHost]`, `Shell\Shell.UI.cs`): the Escape key-preview chain, the leaf-navigation close, the mount decision | `ClassicDrawer`: the scrim, the pane, their open/close motion |
| `Shell.CommandPalette` | `PaletteProps` (the popup control, a session factory, close) | `Shell.CommandPalette()` + `Shell.PaletteSession` (`Shell\Shell.Palette.cs`); the table, the filter and the empty-query order stay `CommandTable` | `ClassicPalette`: the lane, the card, the keyboard navigation |
| `Shell.HistoryMenu` | `HistoryMenuProps` (the rows, close) | `NavHistoryButton` decides the rows (`FrameRules.HistoryMenu`) | `ClassicFrameBits.HistoryMenu` |
| `Shell.FileDrop`, `Shell.DragChip`, `Shell.CoverScrim`, `Shell.Fps` | `FileDropProps`, `DragChipProps`, `CoverScrimProps`, `FpsProps` | the frame root (the drop target, `Drag.Preview`, `Setup.Covering`, `Diagnostics.FpsOverlayOn`) | `ClassicFrameBits` (`Shell.Fps` is the Common block `CommonFps`, T11c) |

**`PageChrome`** (`Theming\Contracts\PageChrome.cs`) is the value the core hands the frame for a page's head: today the title, the trail
(`PageCrumb`: a label and a navigate verb, none on the current page), the "Show all" tool's state and the head's form (`PageHeadForm`, `Shell.HeadFormOf`: by the route's kind), built from the masthead publications
(`Shell.Mastheads`, the nav origins, `Shell.TryMasthead`). Since contract 1.26 it also carries **row 2** (`Views`, `Actions`, `Backdrop`, empty by
default): see "Page chrome row 2, the band and the shell capabilities (P5 ZK)" below.

**Props that cannot carry a writable signal.** The tint binder publishes into the shell material, and a props record may not hold a `Signal<T>`
(WVTH006), so it carries a `MaterialPort` (`Platform\ShellMaterial.cs`): one cached object per slot, compared by reference, whose `Publish` is
`ShellMaterial.Publish`. The palette's popup is the engine control wired to the open flag by the host, so the props carry a
`Func<Element, Func<Element>, Element>` and the template places what comes back. The decisions stay core and pinned by their tests: the hand-over
rule (`TintOwnership`: a claim, an owner-only refresh, never a clear, so a navigation never flashes), the veil's pre-settlement guard
(`VeilSettlement`), the drawer's mount decision (`NarrowDrawerMount`, the band alone), the palette's scoring and the FPS rule (developer mode only).

**Tests.** `Wavee.ShellTests\FrameBitsMountedTests.cs`: Classic is the registered template of every key above, each view stands in the real
shell or alone under `ViewHarness` with fixture props (the masthead row, the drawer's two states, the palette's rows and keyboard, the file-drop
cue, the scrim, the HUD only in developer mode), and the cover leaves still publish the shell tint through the port.

### Page chrome row 2, the band and the shell capabilities (P5 ZK)

A band-style structure (Zune) draws the frame differently from Classic: no nav tree, no tabs and no forward button, a two-row band over
the page column (row 1: the destinations and the pins; row 2: the page's own words), and a head for every page. ZK gives the core the
seams for it; Classic reads none of them and is unchanged.

**Row 2 on `PageChrome`** (contract 1.26). `Views` (`PageView`: id, label, current, `Go`, an optional count), `Actions` (`PageAction`: id,
label, verb, `PageActionKind` Play / Shuffle / Word, enabled) and `Backdrop` (`PageBackdrop`: the `Bleed` image, or the `Mix` covers) are
additive init members with empty defaults. They come from two places, merged by `Shell.PageChromeChannel.ChromeOf(route)`:

| Source | Who publishes | How |
|---|---|---|
| route level | a family whose row 2 is sibling routes; today the Library kinds (liked, albums, artists, podcasts, audiobooks: `User.LibraryChromeViews`) | `PageChromeChannel.RouteViews(route)`: answered from the route alone, so the views stand on the navigation's first frame |
| in page | a page's core host or its theme template | the `PageChromeSink` the content host provides around every page (`ctx.Hooks.UseContext(PageChromeSink.Context)`): `Publish(PageChromeRow)` from an effect, `Publish(null)` from its cleanup. No `Shell` named |

The page's views win over the route's; its actions and backdrop are its own. The title and trail are the masthead's for its family and
the destination's title elsewhere (one crumb, the page itself). `PageChromeChannel.Current` is the chrome of the page on screen, a signal
the content host republishes on every navigation and publication.

**The band** (`NavPaneProps.Band`, `BandProps`, contract 1.26): one static record on both pane mounts. `Destinations` (home, browse, library,
recents; `BandDestination`), `Current` (the lit index, `Shell.BandRules.IndexOf`: Home and its sections, Browse and its family with the
concerts, the library kinds, Recents; -1 elsewhere), `Pins` (`NavModel.BandPins(max)`: the pins in the user's order, folders left out, at most
`ShellCapabilities.MaxBandPins`, each with `Go`, `Play` and a tracked `Playing`), `Chrome` (the current page's chrome), `Scroll` (`PageScroll`:
the current page's offset and the direction it last travelled past `PageScrollRules.DeadZone`; a page names its main scroller with
`PageChromeSink.ReportScroll(handle.Offset)`) and `Hub` (the hub list a narrow band's lead word opens: row 1, profile, settings). Whether the
pins show at a tier, how the band collapses and how its words overflow are the structure's. Read `Scroll` in a bound prop, never a render body.

**The capabilities** (`ShellCapabilities`, read through `ShellCaps.Now` / `.Tracked`):

| Member off | What the core does |
|---|---|
| `Tabs` | `Chrome.Resolve(..., tabs: false)`: no strip, no count button, no "+" slot, none priced; Ctrl+T and the "+" verb refuse; "open in a new tab" (History's own tab) navigates in place; the workspace **collapses** to the active tab (`TabWorkspace.Collapse`, `Shell.FollowTabsCapability`, an effect of the frame, so it lands in the transaction that changed the capability), the others parked with their ids and pins; the persisted pins are the whole workspace; a structure with tabs **expands** it back (owner Q4); palette rows with ids `tabs.*` are hidden |
| `ForwardButton` | `ShellCaps.Budget`: the budget never admits Forward, so the row neither draws nor prices it (Alt+Right still goes forward) |
| `NavTree` | the pane is never presented (`FrameModel.Gate`: Minimal, width 0, no seam), `NavPaneProps.Mount` answers an empty box, the drawer does not mount, `Sidebar.TogglePane` / `OpenPane` / `EnterEdit` refuse, the title bar's toggle is disabled, and the palette hides "Edit sidebar" and "New folder" |
| `MaxBandPins` | the band's pins are capped |

`HistoryMenu` and `FriendsPanel` are kept by every structure so far. A template may read the capabilities itself
(`ctx.Hooks.UseContext(ThemeFactsContext.Signal).Value.Capabilities`), for instance to leave its own pane toggle out.

**Tests.** Pure: `Wavee.Tests\PageChromeRowTests.cs` (the Library row per route, the merge, the band's lit index, the scroll rule),
`ShellCapabilityRulesTests.cs` (the budget without Forward, the row without tabs, the workspace's collapse and expansion, the palette filter).
Mounted: `Wavee.ShellTests\ShellCapabilitiesMountedTests.cs` under the test-only `ClassicBand` theme (Classic's views, Tabs, Forward and NavTree
off, six band pins): no tab island and no Forward, no pane mounted, the workspace collapsed on the switch and restored on the switch back, the
band's pins capped at six, and the current chrome carrying the Library row.

### The navigation pane's rows (T5a)

The sidebar is one pane over six sub-surfaces (rows, heads, flyouts and menus, the Edit outline, drop cues, the library grid). T5a
gives the pane its key and moves the **rows**; T5b moves the **heads** (below); T5c moves the rail's flyouts (below); T5d moves the Edit outline (below); T5e moves the drop cues (below); T5f moves the library grid.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.NavPane` | `NavPaneProps` (`InDrawer`, `Mount`) | `Sidebar.PaneHost` -> `ClassicMode` / `LibraryMode` -> `PaneView` (`Shell\Sidebar.UI*.cs`), mounted by the frame (`ShellFrameProps.NavPane`, replacing the `SidebarPane` builder) and by the narrow drawer (`Sidebar.DrawerPane()`) | `ClassicNavPane` (`Views\Nav\`): hands the core builder Classic's `ClassicNavRows` |

- **The pane is a core host.** `PaneView` keeps every hook, effect and state machine: the plan -> publish -> per-row epoch pipeline,
  the selection travel and the pill target, disclosures, reorder bands, the tree multi-selection, the drag peek and when the freeze
  holds or flushes. Since P3 it is a `[CoreHost]` (as are `PaneHost` and `ClassicMode`): the boxes it is built in are the kit's
  (`NavRowKit.Frame` over a `NavPaneFrameArgs`: the layer at the open width, the root, the context-menu shell and its childless
  `sidebar:context-shield`; `ListBox`: the `plan-pad` inset; `ModeHost`: the layout host's box and the mode switch's fade), while the
  pane decides what they hold and every flag on them (inert behind a pinned overlay, the drag peek's waypoint, the key handler, the menu,
  no shell without an overlay). The drag watcher is zero-size machinery beside the pane (`Sidebar.DragPeekWatcher`), placed by the frame.
  `LibraryMode` stays unmarked: it draws nothing, but its drill-in width rule (`SidebarLibraryMetrics.DrillInWidth` against the
  viewport) is not a tier and WVTH013 would report it. The filter box and the empty hint left with the heads (T5b, below).
- **The structure supplies the list as a `NavRowKit`** (`Theming\Contracts\NavRowKit.cs`; Classic's is `ClassicNavRows`): the planner
  and its owning-section answer, the publish diff (`RowChanged`), the analytic extent that seeds the virtualizing host, the mid-drag
  parking bay (`INavStageHold<T>`: no row jumps while a list is frozen during a drag, the newest stage flushes once at session end)
  and the row **slot**. The template calls `props.Mount(kit)` and the core builds the pane around it, so core never names a structure.
- **Classic's files.** `Rules\SidebarPlanner.cs` (`SidebarRowPlanner`, `SidebarPlanDiff`, `SidebarRowExtents`,
  `SidebarStageHold<T>`), `Rules\SidebarGeometry.cs` (the row ladder: heights, the icon column, indent, pill, the grid and cue
  numbers), `Views\Nav\ClassicNavRow.cs` (the bound slot: header, route, entity and folder rows, rail tiles, grid strips, the empty
  hint, the tree's end gutter, the drop band, the selection pill and the drop cues), `ClassicNavRows.cs` (the kit),
  `ClassicNavFrame.cs` (the pane's boxes, P3), `ClassicNavPane.cs` (the template).
- **`INavRowHost`** (`Theming\Contracts\NavRowHost.cs`, implemented explicitly by `PaneView` in `Shell\Sidebar.UI.Host.cs`) is what a
  slot may ask of the pane: the published plan, what the pane resolved once for every row (selected route, playing rows, the pill
  target, the reorder band), the verbs, and the pieces core decides for the structure's surfaces (menus' rows, drop specs).
  A slot never reaches into `Shell`.
- **X26 is gone.** `OwningSection` (which section would hold a route: the pill's header or folder anchor) was a layout branch inside
  the "one renderer"; it is `SidebarRowPlanner.OwningSection`, next to the Library plan, and `NavModel` no longer answers it.
- **No core copy of the ladder.** The row ladder is Classic's alone. T5a kept a pinned mirror (`SidebarRowGeometry`, with
  `SidebarGeometryParityTests`) for the sub-surfaces that were still core; T5f, the last sidebar lane, deleted both (see "The navigation
  pane's Library grid (T5f)" below). What core keeps is no pixels: the plan queries (`SidebarPlanQueries`: `TrySectionBodyRange`,
  `FolderHeaderIndexOf`, ...). The band widths are `NavPanePolicy`'s (section 2a).

**Tests.** The planner, the diff, the extents, the freeze and the ladder are pure (`SidebarPlannerTests`, `SidebarWiringTests`,
`NavDropRulesTests`, `SidebarRowGeometryTests`, `NavModelTests` for the owning section); the mounted facts are
`Wavee.ShellTests\NavPaneMountedTests.cs` (the registered template is Classic's, the real shell draws the pane through the key and
its Home row stands at the ladder's row height, the kit plans with Classic's planner, and the pane's boxes are the kit's: the menu
shell over a childless shield only with an overlay, the inset, the mode's fade, and in the real shell the list in the layer beside the one shield).

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
- **The mirror shrank.** The header height, the separator's two numbers and the empty-hint height had no core reader left and were
  deleted. The `Sidebar.SectionHeader.InlineButton` copy is gone (T5d: the Edit outline uses `ClassicNavHead.InlineButton`).
  `PaneMetrics.HeadBandInset`, which nothing read, is gone; the rest of the mirror went with T5f.
- **Numbers** the heads carried as literals are `Rules\NavHeadLayout.cs` (`ClassicNavHeadLayout`); the Library head's heights, insets and
  toolbar widths are `Rules\NavLibraryHeadLayout.cs` (`ClassicLibraryHeadLayout`, T5f: they moved out of core's `SidebarLibraryHeadRules`,
  which keeps the decisions the pure tests pin).

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
  and F2 rename are the engine control's and the row slot's, unchanged.
- **The panels are the structure's.** The two props records carry the pane as `INavRowHost` (internal, so the member is internal;
  public record, internal member, as `NavLibraryHeadProps`). `INavRowHost` gained `PlanSectionExpanded`, `RailFolderDropSpec`,
  `IsRailDropActive` and `ExpandFolderInPane`; the panels read the rest (`Model`, `Navigate`, `SelectedRoutePeek`, `EntryMenu`,
  `FolderMenu`, `PayloadOf`, `ResourceDropSpec`, `MenuOverlay`) from it. Numbers are `Rules\NavFlyoutLayout.cs`.
- **Nothing was left to delete.** The ladder members the flyouts read (`PitchOf`, `ArtOf`, `PaneEdge`) had other core readers (the row
  primitives, the drop rules), so the mirror went with T5f.

**Tests.** `Wavee.ShellTests\NavPaneMountedTests.cs` pins that Classic registers both keys; the drill-in stack stays
`SidebarFolderFlyoutNavTests` (`NavDropRulesTests.cs`).

### The navigation pane's Edit outline (T5d)

T5d gives the structure the **picture** of edit mode; the edit model, its ops, validation, persistence and verbs stay core (C11).

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Shell.NavEdit` | `NavEditProps` (the pane as `INavEditHost`) | `PaneView.PaneChildren` mounts it while `editing && !compact` (the rail never draws the Outline), mount-stable (`_editPane`) | `ClassicNavEdit.Template` -> `ClassicEditPane` (`Views\Nav\ClassicNavEdit.cs`): the edit bar (title, undo, redo, Reset, Done), the Outline's section band, pins band and Collections band (three `Reorderable`s), the Show checkboxes and locks, the hint rows, the Settings row, the section drag chip, the spoken reorder sentences and the engine-key -> `NavEditKey` mapping |

- **Core keeps the decisions.** `NavEditModel` (`Model.Edit`: the Outline read, `SetSectionShown` / `SetItemShown` (and the "empty section" note Done
  shows), `MoveSection` / `MoveItem`, undo / redo, `OnPaneKey` / `OnSectionKey` / `OnPinKey` / `OnItemKey`), `SidebarEditRules` (extents, heights,
  summary and announce keys), the layout store and the Reset and header menus' rows (`NavMenuModel`, `SidebarMenus`).
- **`INavEditHost`** (`Theming\Contracts\NavEdit.cs`, implemented explicitly by `PaneView` in `Shell\Sidebar.UI.Host.cs`) is the whole surface the
  view reads: the edit model, the overlay, the pin store and its verbs (move, unpin, name, resolved cover), the Reset and header menus, route
  titles and glyphs, section and item names and the Settings title. The view never names `Shell`.
- **Nothing changes in behavior.** The component keeps its hook order (the drag-chip effect, the first-stop layout effect that lands focus
  in the Outline once per session), its keys (`edit-pane`, `edit-bar`, `block:`, `head:`, `pin:`, `item:`, `check:`), its Reorderables and
  the focus order. Entering and leaving edit mode is still the pane swapping its head and list for this one element. Numbers are
  `Rules\NavEditLayout.cs` (`ClassicNavEditLayout`).
- **Deleted.** `Shell\Sidebar.UI.Edit.cs` (`EditPane`) and the `Sidebar.SectionHeader.InlineButton` copy in `Sidebar.UI.Rows.cs`: the Edit rows
  use `ClassicNavHead.InlineButton`.
- Rename-in-place is not part of the Outline: a row's F2 rename and the create flow are the row slots' and the pane's (T5a / T5b), unchanged.

**Tests.** `Wavee.ShellTests\NavPaneMountedTests.cs` pins that Classic registers the key; the edit model's behavior is `NavEditModelTests`,
the rules `SidebarEditRulesTests`.

### The navigation pane's drop cues (T5e)

T5e gives the structure the **pictures** of a drag over the pane and the rail; the drop **facts** stay core (C12). There is no new view key: the
cues live inside the row slot and the flyout rows that T5a and T5c already moved, plus one small contract on the kit.

| Surface | Core keeps | Classic draws |
|---|---|---|
| Tree rows (`ClassicNavRow`) | the published slot (`_dropSlot`) and the resolver, `NavDropRules`, which kinds draw a line or a plate (`SidebarDropCue.DrawsLine` / `DrawsPlate`), the refusal sentences, the spring-load and auto-expand timers, the commit | the insertion caret (stroke, dot, width by depth, Y by kind; `SidebarGeometry.DropLine*`), the "into" plate, the Pinned band's dashed ring and "Drop here to pin", the tree's end caret |
| Rail folder flyout rows (`ClassicNavFlyouts`) | `_railDropUri`, `RailFolderDropSpec`, the accept / refuse rules | the whole-row plate: `Sidebar.RowDropCue` (armed, fill, border, width) on the row spec, painted by the shared row primitive |
| Drag presentation | when a collapsed pane peeks open (`DragPeekMs`, `SetDragPeek`), freeze and flush, `SpotlightFor` | `NavRowKit.DropLook` (`NavDropLook`, `Theming\Contracts\NavDropLook.cs`): the drag-dim policy of every pane drop target (the rows, the header and folder "+" plates, the rail tiles), the lifted source's look and the engine insertion line of the reorderable bands |

- **Nothing changes in behavior.** Classic's `DropLook` carries exactly the values core hard-coded (Spotlight, a Stationary lift at opacity 0, no engine
  line); the caret and plate numbers did not move, only the readers (`SidebarDropCue.LineThickness` / `LineCorner` / `DotSize` / `LineWidth` /
  `LineY` became `SidebarGeometry.DropLineThickness` / `DropLineCorner` / `DropDotSize` / `DropLineWidth` / `DropLineY`; the flyout plate's 1-DIP
  border is `SidebarGeometry.DropPlateBorderWidth`).
- **The depth origin is shared.** The resolver's depth pick (core) and the caret's start (Classic) both read `TreeContentX`: the pane hands
  the resolver the kit's `NavRowKit.TreeLadder` (T5f), so there is no core copy.

**Tests.** The cue invariant is `NavDropRulesTests` (`SidebarDropCueTests`: line iff ordering, plate iff Into); the caret geometry is
`SidebarRowGeometryTests`; the mounted pane still draws through Classic's kit (`NavPaneMountedTests`).

### The navigation pane's Library grid (T5f)

T5f is the last sidebar lane. It gives the structure the Library layout's grid tile and ends the T5a mirror: **core reads the row ladder
through the kit and the policy, and `SidebarRowGeometry` and `SidebarGeometryParityTests` are deleted.** Library data, the layout
choice, persistence and the demands stay core (`NavLibrary`, the layout store, the binder).

| Surface | Core keeps | Classic draws |
|---|---|---|
| The grid tile (one cell of a `GridStrip`) | `SidebarCardRules` (selected, activation, play, drop, drag, the title gate, pending, circular: pure facts, `SidebarCardsTests`), the verbs and specs through `INavRowHost` (`Navigate`, `Play`, `GridCellMenu`, `ResourceDropSpec`, `PayloadOf`, `DestOf`) | `Views\Nav\ClassicNavGridCell.cs`: the `CardData` the media surface renders (the cover box a no-art entry gets, its kind glyph, the subtitle's look, the cover's size in the card's plate, `SidebarGeometry.TileCover`) at `Shape.SidebarTile` |
| The strip, its columns and cells | the presented pane width; `INavRowHost.GridColumns` (the planned count) | `ClassicNavRow.GridStripRow` (unchanged), `SidebarGeometry.GridColumns` / `GridFallbackColumns` / `GridCellMax` |
| The head's rows, chips and toolbar | the session, the pages, chips, sort rules, the Tab order and ring (`SidebarLibraryHeadRules`, incl. the `ToolbarShape` vocabulary) | their heights, insets and the toolbar widths that choose the shape: `Rules\NavLibraryHeadLayout.cs` (`ClassicLibraryHeadLayout`: `ShapeOf`, `ToolbarWidth`, `ToolbarLane`) |

- **The mirror is gone; the structure answers.** Each remaining core reader asks for what it needs:
  - the drop resolver's depth pick takes the kit's `NavTreeLadder` (`NavRowKit.TreeLadder`: origin and indent step; `RootlistSlotResolver.Resolve`
    gained a trailing `in NavTreeLadder`);
  - the pane's list inset, a row's pitch (the virtualizing host's estimate, the reorder bands, the live-extent fallback) and the selection
    indicator's height are `NavRowKit.ListInset`, `PitchOf(SidebarRowShape)` and `PillHeight`; the Library grid's column count is
    `NavRowKit.GridColumns(paneWidth)` (the mode asks it and hands the planner the count);
  - the rail's width is `NavPanePolicy.RailWidth` (Classic 48, `ClassicPolicies.NavPane`; core's baseline is `SidebarPaneBounds.RailWidth`),
    read by the resize and mode rules, the seam's floor, the boot seed and `SidebarPaneInvariant.Inspect`.
- **The row primitives moved with the ladder they are drawn on.** `Sidebar.EntityRow` / `RowSpec` / `RowDropCue` are
  `ClassicEntityRow` / `ClassicRowSpec` / `ClassicRowDropCue` (`Views\Nav\ClassicNavEntityRow.cs`), `Sidebar.SelectionPill` and
  `Sidebar.Skeletons` are `ClassicSelectionPill` and `ClassicSkeletons` (`ClassicNavRowParts.cs`); their literals are `SidebarGeometry`
  members. What stays in `Shell\Sidebar.UI.Rows.cs` is shared with the pages: `Sidebar.Cover` (Recents, Home), `Sidebar.Chevron` (the track
  table, the episode discussion, the logs page), the pane text, loc and glyph tables.
- **What `SidebarRowGeometry` became.** The plan queries (no pixels) are `SidebarPlanQueries` in `Shell\Sidebar.cs`; `SidebarLabelFit.LabelWidth`
  is `SidebarGeometry.LabelWidth`; every other member was Classic's already.
- **Nothing changes in behavior.** The tile's data, its closures, its menu, drag and drop spec and the strip's column math are the code that
  was in `SidebarCards`, moved; the layout switch, the rail form, selection and the playing indicator, drag reorder, the chips, the empty
  and signed-out states, keyboard navigation and every accessible name are untouched.

**Tests.** `SidebarGeometryKitTests` (`SidebarRowGeometryTests.cs`) pins the kit's answers, the rail width, the grid columns and the label
width; `SidebarCardsTests` the card facts; `NavDropRulesTests` drives the resolver with Classic's ladder (`DropLadder`);
`SidebarLibraryHeadRulesTests` the head's rules and Classic's toolbar shape; `NavPaneMountedTests` that the list stands inside the kit's inset.

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

**What stayed core, and why.** The "⋯" rows (`OverflowItem`: labels and what a row does, built from live reads at open time) and the
key map (`PlayerKey`) are decisions; the device picker's menu is `Player.DevicePicker` since T6b, below. The credits line under the title
is the template's own picture (`ClassicPlayerBar.Credits.cs`: the links, the ", " runs, the "+N" chip and where its menu opens) over
two core reads on the props: `Credits` (a `PlayerCredits`: the face's show or credited artists as `PlayerCreditLink`s, an inert name
carrying no verb, the chip's key and its menu rows built at open time) and `CreditsStamp` (the row versions the line's memo watches).

**The transport pieces the bar shares (leftovers P3).** The seek rail and the time labels are placed by the bar, the stage's transport
card and the on-media strip, each over its own core host: `PlayerBarProps.SeekBar` / `TimeText` (and the stage's and the strip's
builders) return `Shell.BarSeekRail` / `Shell.BarTimeText` (`[CoreHost]`, `Shell\Shell.PlayerBar.UI.cs`), because the per-surface
arguments (the chrome feed, the stage's scrub edge, the strip's reveal, the ink) are core objects. The hosts keep the scrub controller
and every hook (the report effect, the pixel-due stepper, the unmount abort), open and close the readout and the chapter tip, own the
chapter lease and the markers' arithmetic (`BarChapterTimeline`, `[CoreHost]`), format the digits and decide the live slot; they mount:

| Key | Props | Classic (`Views\Player\ClassicTransport.cs`, numbers in `Rules\PlayerTransportLayout.cs`) |
|---|---|---|
| `Player.SeekRail` | `SeekRailProps` (`Enabled`, `Dvr`, `Line`, `ChapterRail`, the chapter host; `Host` is an `ISeekRailHost`: the fill and thumb binds, the handlers, the thumb's node report, the playing flag, the keyboard ladder's zero-size clock) | the 32-DIP hit box (Slider role) over the track, the fill, the DVR live edge, the chapter layer, the thumb ring and its disc; the breathing live line when there is nothing to rewind |
| `Player.SeekChapters` | `SeekChaptersProps` (the markers, the rail width, the tip's fraction and anchor report) | the ticks and the tip's invisible anchor |
| `Player.ChapterTip`, `Player.SeekReadout` | `ChapterTipProps`, `SeekReadoutProps` (bound text) | the two bubbles, mounted inside the host's overlays |
| `Player.TimeLabel` | `TimeLabelProps` (`Remaining`, `Ink`, `IsLive`, `LiveSlot`, the bound label, the verbs) | the 44-DIP digits slot (a floor while live) and the 104-DIP live slot (LIVE, or GO LIVE behind the edge) |

The slot widths (`TimeLabel.SlotW` / `LiveSlotW`) only the picture read, so they moved with it (`ClassicTransportLayout.TimeSlotWidth` /
`LiveSlotWidth`); `SeekRail.ThumbX` stays a core rule the host's thumb bind runs for the ring diameter the view asks for.

### The right panel and the Player family's other views (T6b)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Shell.Panel` | `PanelProps` (`ShellFrameProps.Panel` replaced the `RailBody` builder) | `RailWiring.Panel` (`Shell\Rail.Wiring.cs`, `[CoreHost]`) over `Shell.PanelHost`, `Rail.VideoCoupling`, `Video` | `ClassicPanel` (`Views\Panel\`): the frame component (surface, edge, the 300 ms slide), the header and its glyph buttons, the arm of each mode, the docked video cap and its splitter, the Art \| Video switch |
| `Panel.Friends` | `FriendsProps` (`IFriendsFeed`: the rows and state with their tables subscribed) | `RailWiring` (the feed, the clock, the retry seam, the profile verb) | `ClassicFriends`: the rows, the skeleton, offline, error (Retry only when the host can retry) and empty faces |
| `Player.NowPlayingHero` | `NowPlayingHeroProps` | `RailWiring` (the playing track and cover, the deck, the entity subscriptions, the demand) | `ClassicNowPlayingHero`: the pinned tile (header strip, cover or the hosted deck, the art menu) and the now-playing body under it (credits, About, cities, merch, Next up) |
| `Player.DeckStyleFlyout` | `DeckStyleFlyoutProps` (`IDeckStyleVerbs`) | `Shell.DeckPrefs` behind `RailWiring`'s verbs object | `ClassicDeckFlyout` and `Rules\DeckThumbnails.cs`: the preset shelves, the option rows, the swatches, the twelve mini-arts |
| `Player.DevicePicker` | `DevicePickerProps` (`DevicePickerView`: `DevicePickerEntry` rows and a stamp; built per open) | `Shell.DevicePicker.Entries`, `RailWiring.ReadDevicePicker`; the overlay stays the bar's `ToggleDevicePicker` | `ClassicDevicePicker`: the menu over the entries, scrolled to the window |
| `Player.OnMediaTransport` | `OnMediaTransportProps` (`OnMediaFacts`; a surface adds its idle signal and chrome feed) | `OnMedia.Transport` mounts it (`RailWiring.OnMediaProps`); `OnMedia.MoreMenu` and `FadeChrome` stay | `ClassicOnMedia`: previous / play / next, the seek rail's and the times' places (their core hosts mount `Player.SeekRail` / `Player.TimeLabel`, above), mute, the volume popup, "⋯" |

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

**Bodies the core hosts, and the small views (P3).** The lyrics body (the core builder `Lyrics.View()`, which mounts `Lyrics.View` since T7b), the docked video card (a core host that
mounts `Video.Docked` since T7c) and the lyrics peek (`NowPlayingHeroProps.LyricsPeek`: the core hosts `Lyrics.PeekHost` / `PeekReel`, `[CoreHost]`, keep the
document, the 100 ms clock and the active / peek pair and mount `Player.LyricsPeek` with the decided face) arrive as `Func<Element>` members on the props. Three
pictures got keys in the leftovers lane (P3; props in `Theming\Contracts\PanelBitsProps.cs`, Classic in `Views\Panel\`): `Panel.LyricsHeader`
(`PanelProps.LyricsHeader`; `ClassicLyricsHeader`: the title, the globe toggle, the AI sparkle and its breath, expand, the developer inspector, close, on the panel
header's own row and glyph buttons; the facts and verbs are `RailWiring`'s, the sparkle's verdict and click `AiLyrics.HeaderFacts` / `HeaderClick`, the inspector's gate
`Diagnostics.StoreShot.DeveloperSurfaces`), `Panel.NextUp` (`NowPlayingHeroProps.NextUp`; `ClassicNextUp`: the media-surface rows at the 52-DIP floor; the window, the
row data, each row's verbs and key are `Rail.NextUpRows`) and `Player.LyricsPeek` (`ClassicLyricsPeek`: the accent spine, the two clipped rows and their flip, the video
note). Each read is a `Func` the template calls inside its render, so the subscriptions land on the view's slot as they landed on the core component; the sparkle
and the inspector are components of their own, so only they subscribe the AI status and the developer setting. `Rail.HeaderButton` and the core header, row and
reel pictures are gone. The video body is the template's since T7c
(`ClassicVideoPanelBody`; `PanelProps.SubscribeVideoBody` carries its subscriptions). The queue body (`PanelProps.QueueBody`) is the core host's builder
(`Queue.RailPanelHost` inside its slot pin), which mounts the `Queue.Panel` key since T7d.

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

- **Two templates, one per row style.** The core picks `Modern` or `Classic` by the table's `ColumnSet.Classic`, which the snapshot reads from
  `ThemePolicies.Lists.RowStyle`: the row style is Classic's own `structure.rowStyle` option since M2 (below). Classic's two pairs share one
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
  `GoToArtistAt`, ...) were `internal` on `Track` until T8c moved that row: the cells are `ClassicRowCells` now (see "The list surfaces (T8c)"), and `MoreCell`, `ExpandCell` and the three lane wrappers stay on `Track` because Recents passes them in.
- **Not yet the engine's typed channel.** EP6 (`ItemsView.CreateBound` over `BoundListProps`, a stamp-driven rebuild of every slot, the
  FGTH003 ban on components in a row template) wants the whole slot as a plain tree. The table's slot is two components today
  (the kind switch and the open drawer; the reveal gate, the presentation memo and the like latch), and folding them would add binds
  and `Flow` boundaries to every row. T8a therefore moves the picture and leaves the slot as it is: the per-row cost on a scroll
  frame is unchanged (no extra component, no extra closure per bind). `TrackListStyle.Stamp` is carried for the PR that folds the
  slot; until then a row-style change (the policy) re-keys the list (`list:<scroll>:d<N>:classic|modern`), as before.
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
| Command bar | `ITrackCommandKit`: the lane (`Surface`), a measured slot (`Command`), `Bar`, `PlayNext`, `Labeled`, `IconButton`, `Separator`, `Chevron`, `SortCommand` (contract 1.22: the Sort command, a glyph at Classic Small), `Trail`, `SortCaret`, `FilterCommand`, `FilterFunnel`, `MoreSlot`, and (M3) `DensityLabel`, `DensityPanel`, `DensityItems` | which commands exist and fit (`CommandBarLayout`), the bar's hold while the filter card is open, every overlay and its focus, the "…" menu's items, the search field | the 32-DIP buttons, the split button's parts, the count badges, the bar's two motions |
| Selection bar | the same `Surface` (mode `selection`) around `Controls.SelectionBar` | the live selection, the lane's commands, exit and select-all | the lane and the browse / selection swap motion |
| Filter card | `TrackFilterCardTemplate(TrackFilterCardArgs)`: title, status (the filtered counts, already worded), `Reset`, `TrackFilterFacet` rows (signal, options, commit, `On`) and `TrackFilterToggle` rows | `TableFilterFlyout`: which facets the list can earn, the signals, what each commit writes, the sync from external filter writes | the 360-wide card, the 44-DIP facet rows with the accent-tinted combo, the switches, the scroll fallback |
| Placeholder | `TrackPlaceholderTemplate(in ColumnSet, tracks, rowH, padX)` | `TableHost.PlaceholderRow`: the count, height and padding are the live row's, so landing data never jumps | a `GridEl` of empty boxes |
| Unavailable-songs bar (contract 1.22, optional) | `TrackChromeTemplates.UnavailableBar` (`TrackUnavailableBarTemplate(TrackUnavailableBarArgs)`: `Shown`, the short and the explained sentence, "Hide them", the `Hide` verb, the horizontal inset) and `UnavailableBarExtent` (the band it takes while shown, a tracked tier read) | `TableHost.TableUnavailableBar`, a slot in the chrome stack of a detail page's track list (under the list bar, or first in the hero arm's pinned chrome): the tally of the rows `FilterFlags.PlayableOnly` would hide, by why (`Track.UnavailableTally`, `FilterModel.HiddenByPlayable`, the filter's own rule), on while `FilterModel.OffersHide`; `Hide` turns the flag on (the filter card's "Hide songs I can’t play" switch, reset with the other filters on a context change); the hero arm's clip inset adds the extent while it is on | boards C-Unavailable / C-Unavailable-Compact (`Rules\ClassicUnavailableBarLayout.cs`, `ClassicDetailTier.Unavailable`): the card plate with the accent info glyph, the sentence and the button; Compact 58 tall with the 40 button and the short sentence, Small the short sentence, Standard and Huge 50 tall with the reasons; the slot reveals its height between the band and 0 (`Design.Reveal.Resize`), so the rows never jump |

- **Templates run inside the core component's render.** The header, the filter card and every button are drawn while the core
  component renders, so the signals they read subscribe that component exactly as before; no key, motion, hit-test or tab order changes.
- **Still core.** The search disclosure's field (`TableSearchHost`, a focus and caret machine; it calls the kit for its glyph button),
  the row-size button's overlay (the panel, the word and the submenu's rows are the kit's since M3, below), `Controls.SelectionBar` and the shared selection lane (shared
  controls) and the chrome stack's own arrangement (`Chrome(...)`, whose sticky clip the hero system measures). `Track.TableHeaderShim` and `Track.ShimmerRow` (the host-free
  statics the library panes and the artist reader called with no registry) moved with those callers in T10c1: the header shim is the album pane's own picture
  (`ClassicLibraryAlbumPane`), and the placeholder row both panes draw is `ClassicTrackChrome.Placeholder`.
- **A host without a registry** draws the placeholder and every chrome piece as an empty box (logged once with the rows).

**Tests.** The chrome's registration is `Wavee.Tests\Theming\TrackListStyleTests.cs`; the mounted facts in both row styles (the keyed header,
the browse-mode command lane, the "…" slot) are `Wavee.ShellTests\TrackChromeMountedTests.cs`.

### The list surfaces (T8c)

Five more list-shaped surfaces are styles, not views, for the same reason the table is: each has a core host that keeps hooks, state and
decisions (a recycled slot, a shelf, a bound list) and calls the structure's templates while it renders, so subscriptions, keys, motion and
effect timing are what they were. They share one lookup, `ListStyleLookup.Of(registry, key)` (the ambient `ViewRegistry`; a miss is logged
once per key and the host draws nothing), and one rule: a template reads facts and verbs from its args and never names `Playback`,
`Entities`, `Store`, `Shell` or `Prefs` (WVTH002).

| Key (id) | Contract | Core half | Classic |
|---|---|---|---|
| `StyleKeys.EagerTrackRow` (`Track.EagerRow`) | `EagerRowStyle`: `Grid` (the lanes of the one-shot row), `Title` (the plain run with its full-text tooltip), `Skin(EagerRowSkin)` (the hover container) | `Track.EagerRow`'s host (the stamp memo that re-renders a row only when ITS track moved, the like edge, the hover signal, the drag payload, click, menu), `TableHost`'s counted shimmer (it draws the same grid over an empty track; since T10b2 Classic's discography style draws its drawer rows with Classic's own grid) | `ClassicEagerRow` (skin, title) and `ClassicRowCells` (`Grid` and every cell: the # state machine, heart, trailing film / "..." lane, metadata line, album link, Added-by, tempo, the Classic title line and explicit word-mark) |
| `StyleKeys.EpisodeRow` (`Episode.Row`) | `EpisodeRowStyle`: `Content(item, ctx, narrow)`, `Seed(narrow)`, `Failure(episode, narrow)` | `Episode.ReaderRow` / `SeedReaderRow` (the reveal gate over `ReaderLoadState`: pending shows the seed, failed the failure row with `RetryRow`), the row's decisions (`PlayedOf`, `InProgressOf`, `LeftOf`, `IsNew`, `ShownTitle`, the completion rule `ReaderPct`), `RowContext` (tone, disc verb, open, menu, selection) | `ClassicEpisodeRow`: art, title line, blurb, meta line, the rule, the hover cluster and tone disc, the now-playing bar, the check lane |
| `StyleKeys.Surface` (`Media.Surface`) | `SurfaceStyle`: `Shell`, `Selected`, `StackBody`, `RowBody`, `Labels`, `RowText` | `Controls.SurfaceHost` (the hot fold, what is mounted when, click / focus / tab-stop ownership, drag, drop, menu, title tooltip, trampolines), the seed face, the gutter, the drag barrier, `InnerOf`, `RowTitleStyle` (`SurfaceParts`: host-free, and the seed IS the live extent in bones) | `ClassicSurface`: the plate table (`SurfacePlate`), the shell, the selected skin, both bodies, the "..." overlay and lane, the labels |
| `StyleKeys.ArtistChart` (`Artist.Chart`) | `ArtistChartStyle`: `Metrics`, `Frame(ArtistChartFrameArgs)`, `Shelf`, `Bar`, `Header`, `Row(ArtistChartRowArgs)`, `Cell(ArtistChartCellArgs)`, `Skeleton`, `Failed`, `Pager` | `Artist.ChartHost` (readiness gate and failure latch, asks, items and row keys, selection model, drag payload, menu, play-from-here), the per-row memo, the tier decision (`ArtistPopularLayout.Decide`), the skeleton's counts (`ArtistSections.ChartSkeletonShape`, through `ArtistChartSkeletonArgs.For`), the selection bar's count and lane, the pager's controlled pips | `ClassicArtistChart`: the prototype row (rank cell, art, title, sub line, heart, duration), the credit line, the selection pill, the pager chevrons, the skeleton, the failed panel; since T10b2 the frame (the column, the readiness region and the floating bar), the shelf's pass-through holder and the selection bar |
| `StyleKeys.TrackDrawer` (`Track.Drawer`) | `TrackDrawerStyle`: `Frame(TrackDrawerFrameArgs)`, `Body(TrackDrawerBodyArgs)`, `FormatSplit(TrackDrawerFormatArgs)` | `TableHost.DrawerBox` (the keyed mount, the reveal wiring `OnRealized`, the zebra parity, the indent), `DrawerHost` (demand, relations, the facts, the version entries and their order, the reserved video row, the peaks, the "arriving" latch), `FormatSplitHost` (the radio ladder and the override), the View-credits sheet | `ClassicTrackDrawer`: the clip box with `Design.Reveal.Drawer` (the open and close motion), the facts strip, the connector rail and version rows, the split button's halves |

- **Rank and plays at every width (chart).** The tier (`ArtistPopularLayout.Tier`: art edge, whether the duration shows, whether the sub line
  stacks) is the core's decision per cell width, with hysteresis, and rides as a PROP, never a key; the template draws the # cell, the compact
  count in the sub line (the exact count is its tooltip) and, in a stacking tier with a credit AND a count, the exact count on a third line.
- **Skeletons match real heights.** The chart's skeleton is the SAME row template over a representative shape at the real column count; the
  episode seed is the real row's geometry in figure-space bars; the surface's seed face is core and pinned to the live extent
  (`SurfaceGeometry`), so a hydrating card never re-measures its list.
- **Hooks stay in the host.** Each host resolves its style with one unconditional `UseContext(ViewRegistry.Context)` and calls the templates from
  its own render; the templates are plain functions.
- **What stayed core, and why.** `Track.ArtistLinks` / `BuildLinkSpans` / `LinkClick` (the surface's data adapter `Track.RowData` builds the
  subtitle with no registry), `Track.MoreCell`, `ExpandCell` and the lane wrappers (Recents builds its actions cell with them), `Track.Spinner`,
  the View-credits sheet (a
  dialog: T11), and `Controls.Vacancy` and the other shared controls (several pages mount them with no registry). The eager row's title is
  always the plain run (the marquee branch of the old `TitleCell` was unreachable for it; the table's now-playing marquee is
  `ClassicTrackTable`'s).
- **A host without a registry** logs `no style is registered for <id>` once and draws nothing (the surface's seed face still draws: it is core).

**Tests.** Registration, ids and the lookup are `Wavee.Tests\Theming\ListSurfaceStylesTests.cs`; the mounted facts (the eager row's lanes and
hover container, a live surface row and its seed at one height, the cold episode row at the row's geometry, and each host drawing nothing
without a registry) are `Wavee.ShellTests\ListSurfacesMountedTests.cs`.

### The fullscreen stage's frame (T7a1)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Stage.Root` | `StageRootProps` (`Mount`), drawn from a `StageRootView` | `Stage.SurfaceCore` (`Shell\Stage.UI.cs`), mounted through `ShellFrameProps.StageRoot` (replacing the `StageView` builder) | `ClassicStageRoot`: the root box, the layer stack, the chrome's placement and its Enter/Exit, the smoke, the hairline |
| `Stage.TopBar` | `StageTopBarProps` | the stage context's verbs (`SetMode`, `OpenOptions`, `Exit`, `OverControls`) | `ClassicStageTopBar`: brand, the mode `SelectorBar`, the options glyph (the TeachingTip's anchor), exit |
| `Stage.Transport` | `StageTransportProps` (`Facts`, `Verbs`, the bar's seek and time hosts, which mount `Player.SeekRail` / `Player.TimeLabel`) | `Stage.TransportFacts` and friends (`Shell\Stage.Frame.cs`) | `ClassicStageTransport`: the card, the seek row, the clusters, the mute button, the volume slider, the device button |
| `Stage.Options` | `StageOptionsProps` (`Verbs` is an `IStageSettingsVerbs`) | `Stage.OptionsContent` (the opener re-provides the stage context in the overlay layer), `SurfaceCore._openOptions` | `ClassicStageOptions`: the tabs, the visualizer grid, the layout rows, the hero options |
| `Stage.Gallery` | `StageGalleryProps` | the root mounts it under the chrome's Show; `Visualizer.Gallery.UI.cs` keeps the words | `ClassicStageGallery` (+ `ClassicStageGalleryTiles`): the pane, the live/poster tiles, the settings rows |
| `Stage.Caption` | `StageCaptionProps` (what one render of the host decided) | `Stage.CaptionHost` (`Shell\Stage.Caption.UI.cs`): the playhead clock, the edge wake, the render-thread wipe, the dots' driver | `ClassicStageCaption`: the veil, the keyed previous/active/next slots, the bloom and the dots |
| `Stage.Pane` (P3) | `StagePaneProps` (`Stage`, `Panes`) | `Stage.PaneHost` (`[CoreHost]`, `Shell\Stage.UI.cs`): which pane is shown, the KeepAlive (`pane:<kind>`) and the Artist pane's props cache | `ClassicStagePane`: the region at the allocator's `PaneRect` (clip, the position spring that slides it with the cover, shown while the layout has a pane and one is chosen) and the frame each pane's body enters in (rise 40 DIP and fade in over 500 ms FluentDecelerate, fade out in place) |

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
writes the `viz.pick` line) or as builders (the core hosts of the bar's own seek rail and time labels, which mount `Player.SeekRail` / `Player.TimeLabel` (T6a, above), the device picker's rows).

**Layers other lanes own arrive as builders.** `StageRootView.Layers` carries `Func<Element>` over the core components of the pane host (the lyrics pane inside
it mounts `Lyrics.View` since T7b, below, and the queue pane mounts `Queue.StagePane` since T7d, below), the caption host, plus the zero-size machinery (lyric facts, hero facts, the visualizer clock) the template must still
place. The backdrop, hero photo, face, scrim, spectrum, now-playing card, identity and artist name were builders here until T7a2 gave them keys (below). The root
keys and orders them exactly as the stack always was. The pane host builds no picture since P3: it mounts `Stage.Pane` and hands the template its KeepAlive as
`StagePaneProps.Panes(frame, empty)`, so the template wraps each pane's body in its own frame while the core keeps the pane's identity and state; the props are
built once per stage context, and the template reads the layout, the look and the shown pane itself (it re-renders on them, as the host did).

**Numbers.** `Rules\StageGeometry.cs` holds the frame's look (the chrome's Enter/Exit offsets, the top bar's selector and brand, the transport card's paddings,
glyph and disc sizes, the volume slider's alphas, the pane region's move spring and its panes' entrance: `PaneMoveResponse` / `PaneMoveDamping`, `PaneEnterDy`,
`PaneFadeMs`); `StageGeometry.Options.cs`, `.Gallery.cs` and `.Caption.cs` hold their views'. The allocator's
authored extents (`Stage.Layout.TopBarH`, `TransportDesktopH`, `Pad`, the gallery column, the caption block) stay in `Stage.Layout`: the core budgets
the face-safe rect, the caption and the pane from them, so the views read them by name. The aspect thresholds are `StagePolicy`'s (see the
stage family's tiers below).

**Polarity.** The stage faces' own dark flag (`StageInk.IsDark`; T7a2 re-points the faces) is read by the core palette derivation only
(`Stage.StageCtx`'s seed palette and `SurfaceCore.PublishAccent`: polarity as an argument, not a colour); the moved views take rungs from the stage-arm
table (`StageInk.Ink`, `GlassHover`, `Card`, `SelectedTileWash`, ...) and never ask which arm is live.

**Tests.** `Wavee.Tests\StageFrameContractTests.cs` pins the keys' ids, Classic's registration and the props' cached equality;
`StageSettingsVerbsTests.cs` pins what the option surfaces write (each verb persists and bumps the stage epoch once, a picked face survives other writes);
`Wavee.ShellTests\StageFrameMountedTests.cs` opens the real stage under Classic and checks the registered templates, the root's stack and the chrome's
nodes, then mounts the top bar, the transport card, the options popover and the gallery alone under `ViewHarness` with fixture props, and the pane region
(`Stage.Pane`) with a fixture KeepAlive: the region at the allocator's pane rect, the frame's entrance run out, the empty arm.

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
| `Stage.FaceScrim` | `StageFaceScrimProps` (`Alpha`) | `LayoutRules.BackdropScrim` (per face) | `ClassicStageFaceScrim`: the veil over a backdrop face |
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
| `StyleKeys.LyricsLines` | `LyricsLinesStyle` (`Message`, `Shimmer`, `Skeleton`, `Unsynced`, `Lines`, `Peek`; optional `Offline` since 1.1) | the host calls it where a document is on screen | `ClassicLyricsLines` (`Lists\`): the state line, the loading bars, the unsynced block, the measured virtual list and its rows (`ClassicLyricsRow`), the podcast peek and its gate |

**The model and the driver stay core.** `LyricsModel` (state, document, the upgrade gate, the clock, the follow and resync machine, the interlude
reserve, the second line, the AI state, the verbs), `Lyrics.BlurPolicy` / `Lyrics.Prefs.BlurStrength(onStage)` and the frame driver in `ViewCore`
(the media clock, the active-line latch and the staggered cascade, the depth-of-field ramp, the word wipe and its render-thread keyframes, the halo
fades, the motion demand, the ticker and stepper, the node handles they write) are untouched; so is the stage's `PaneHost` (the
KeepAlive, the keyed pane) which still mounts `Lyrics.StagePane`, inside the frame of the `Stage.Pane` view (T7a1's table, P3). The core host builds a `LyricsViewProps` each render and mounts
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

**The header and the peek.** The panel's lyrics header (the globe, the AI sparkle, expand, the inspector) and the now-playing panel's two-row peek
are the panel family's keys since the leftovers lane (`Panel.LyricsHeader`, `Player.LyricsPeek`; see "The right panel and the Player family's other
views (T6b)" above). The second line, the AI verdict and the peek's document and clock stay core.

**Numbers.** `Rules\ClassicLyricsRules.cs`: the column's cap and gutter, the state line, the note, the pill, the peek's meta line, the dots' entrance,
the stage's accent pill and the secondary weight. The metrics (`Lyrics.Surface`: row type and rhythm, the focal band, the halo sigmas, the loading
bars) are the core's and the views read them by name.

**Tests.** `Wavee.Tests\LyricsViewContractTests.cs` pins the keys' ids, Classic's registration and the style's members;
`Wavee.ShellTests\LyricsViewMountedTests.cs` opens the real rail on lyrics (nothing playing: the centred line; a fixture track: the keyed rows and
the layer overlays) and the real stage's column, and `LiveOptionBehaviourTests.Lyrics_blur_strength_reaches_the_rail_lines_and_never_the_stage`
keeps pinning the blur.

### The queue (T7d)

Two keys, both drawn by `Themes\Classic\Structure\Views\Queue`: `Queue.Panel` (`QueuePanelProps`, the rail's queue body: the shuffle, repeat and autoplay pills,
"Playing from", the now-playing card, the three sections' headers, rows and "Show more", the nothing-playing vacancy) and `Queue.StagePane`
(`QueueStagePaneProps`, the fullscreen stage's Up next column: the title, the Autoplay switch, the glass rows and the larger Next row, the empty sentence).
`ClassicQueue.cs` holds the two templates; `ClassicQueuePanel` and `ClassicQueueStagePane` are components because the picture has state of its own (the
swipe group, the scroll handle and the chip accent with its hold on the rail; the switch's signal on the stage). The numbers are `Rules\ClassicQueueRules.cs`
(the art edges, the row's extent under each skin, the header and "Show more" slots, the autoplay dims, the stage's row, Next row, grip and duration slots,
which the view scales by `Stage.Layout.QueueScale`).

**The model, the lane and the verbs stay core.** `Queue.RailPanelHost` and `Queue.StagePaneHost` (`Entities\Queue.UI.cs`, one `QueueSurface` base) keep every
subscription (the queue edge, the two row tables, the deck's current row and phase and owner, the settings epoch), the section split
(`Shell.QueueModel.Project` into one `QueueProps` per render), the page counters (`Shell.QueuePages`: three on the rail, one unified on the stage), the realized
slots (`QueueSlots`), the reorder lane (`Reorderable`: the lift, the keyboard reorder, the foreign-drop rule and its captions, `QueueMovePlan`), the row's drag
payload and context menu, the demand for row text and the context's name, and `Shell.QueueVerbs` (play, remove, clear, move, autoplay, show more, the entry menu,
a foreign drop). They mount the key with a props record; `QueueProps`, `QueueRowProps`, `QueueCounts`, `QueuePlayingFrom`, `QueueAutoplay` and `IQueueVerbs` are
published in `Theming\Contracts\QueueProps.cs` (they were nested in `Shell`, which a theme may not name).

**What a theme reaches.** `QueueProps` (`Rows` listed section by section, the autoplay rows only while the toggle is on; `TryRow(section, position)` finds a
row), `IQueueVerbs` (bound to the surface's own page counters), the player's verbs (the pills and the card's play button), and `IQueueLane`: the realized slots
(`SlotCount`, `SlotAt`, `ItemAt` under the live drag projection), `Lane` / `List` (the wrapper a row needs and the lane around the body), `OwnDrag` and `Attach`
(a viewed queue's own drag; the entry's context menu), `Remaining` / `PageSize` ("Show more"), `TextOf` (what a row paints: the entity tables are not a theme's), `Clock`,
`RowKey` and `SetMetrics`. **A template must call `SetMetrics(rowExtent, extentOf)` before it builds the lane**: the reorder maths needs every slot's height
EXACT, and the heights are the skin's (a skin or a scale change moves them). The settings the core read arrive as facts (`RepeatOn`,
`RepeatTrack`; the row style and "hide track art" are Classic's own options since M2 and M4: the panel and the stage pane read `ClassicOptions.ClassicRows()` and `ClassicOptions.HideTrackArt()`), the two navigations as `Action?` (`OpenPlayingFrom`, `OpenNowPlaying`: null means display-only). The stage's accent, queue scale and
width travel in the stage's ambient context (`Stage.StageContext`), which the view component reads.

**Motion and identity are the old surface's.** Row keys (`i<itemId>` or `e<index>`, plus the skin and art suffix), the lane keys (`lane:upcoming`,
`lane:empty`, `stagelane:upcoming`, `stagelane:empty`), the header, more and "Playing from" keys, the Enter / Exit / Slide of every slot, the now-playing card's
track-keyed remount, the hover-revealed grip and ✕ on the stage, the swipe actions and every accessibility role and name are unchanged. The `queue.panel.rows`
diagnostic line and its capture stay in the rail host.

**Tests.** `Wavee.Tests\QueueContractTests.cs` pins the ids, Classic's registration and the section tags; `QueueModelTests.cs` the projection (and `TryRow`), the
paging and the verbs; `QueueRowRulesTests.cs` the core's decisions (the ✕, the row's own drag, no "..." button) and `ClassicQueueRulesTests.cs` the structure's
(extents, dim, adapter, card click). `Wavee.ShellTests\QueueMountedTests.cs` mounts the real rail on the queue and the real stage's column.

### The video surfaces (T7c)

Six keys, all drawn by `Themes\Classic\Structure\Views\Video`: `Video.Docked` (`VideoDockedProps`: the docked card, or with `PageFrame` the watch page's in-page stage frame), `Video.Pip`
(`VideoPipProps`: the in-window overlay's box and the chrome over the picture), `Video.PopOut` (`VideoPopOutProps`), `Video.Fullscreen` (`VideoFullscreenProps`), `Video.JoinPoster`
(`VideoJoinPosterProps`: the poster and the join notice) and `Video.PlacementMenu` (`VideoPlacementMenuProps`: the player bar's chevron flyout). The panel's video body is
`ClassicVideoPanelBody` (not a key: the panel mounts it through `PanelProps`). The numbers are `Rules\ClassicVideoRules.cs`.

**The media pipeline and the rules stay core.** `Shell\Video.UI.cs` keeps the hosts: `DockedSurface` (the mount decision, the fit and the log lines, `ReportLive`, the hollow reservation's
node and last rect, which the stay-mounted presenter FOLLOWS through the engine's `FollowRect`), `PipSurface` (the ONE presenter the docked card, the mini player and fullscreen are geometry
modes of: the three bound geometry thunks, the remembered rect, the make-before-break mount, and the gestures as `IVideoPipGestures` over `PipGesture` and the panel policy's `PipBounds`),
`PlayerStage` / `MainVideoArea` (the engine `MediaPlayerElement`, its keys and handles, the idle machine, the chrome feed), `JoinWatch` (the one fold of the host's switch events into
`Video.JoinNow`), `PopOutContent` (the viewport, the caption regions at `Video.PopOutBand.HeightDip`, the move-loop hold, the band's fade) and `FullscreenSurface` (OS fullscreen, the focus scope,
Escape / F at the root). `Video.cs` and `PipGesture.cs` keep the placement, hosting, join and PiP rules. Each host mounts its key with a props record.

**Every host is a `[CoreHost]`; the hole's boxes stay core on purpose (P3).** The nine hosts of `Video.UI.cs` (`JoinWatch`, `PlayerStage`, `DockedSurface`, `MainVideoArea`, `PipSurface`,
`PopOutRoot`, `PopOutContent`, `FullscreenHost`, `FullscreenSurface`) build no element: the few boxes they place come from ONE core helper, `Video.HoleBoxes` — the shell's video layer
(`PipLayer`), the presenter's pass-through column around the `Video.Pip` box, the video area's stack (stage over poster), the stage box that holds the `MediaPlayerElement` and the on-media strip,
the fullscreen layer's column and the fullscreen mode's focus-scope root (the Escape / F keys and the focus rule are wired on it), plus the empty box of a surface whose mount decision is false and
the join fold's zero-size node. They are not presentation: no colour but transparent (an erasable plate), no size but 0 and Grow, no motion. They stay core because they are the picture's
ancestors (or the fullscreen mode's behaviour), and the hole rule above binds every theme: keeping that chain core means no theme can put a transition, an opacity or an effect between the
picture and the window. What a theme draws around and over the hole is a key placed inside them.

**The hole rule binds every theme.** The picture is a hole erased from the swapchain: no layout transition, opacity, offscreen effect or scale Enter/Exit on ANY ancestor of it. Chrome is a
SIBLING (a hover- or idle-revealed strip). `VideoPipProps.Visible`, `Width`, `Height`, `Transform` and `FollowRect` are the engine's geometry for the hole: a template puts them on the box it
returns and nothing between that box and `Area()`. The poster's notice is the one place an opacity channel is legal (a poster is mounted only while no player is bound). Hover chrome costs
no signal: `Opacity 0 / HoverOpacity 1` under a container that earns hover with a no-op `OnPointerExit`; the on-media transport (`Player.OnMediaTransport`) and the pop-out's band ride the
surface's idle machine instead (the pop-out's tree has no pointer handler above the stage). The pop-out's band height is `Video.PopOutBand.HeightDip` (the OS caption region and the band
must agree on it).

**The pop-out is its own window.** A detached window builds its own `AppHost`, so `PopOutRoot` wraps its content in `ThemeRoot.Wrap` (the view registry, the option cells, the facts): without
it none of the keys that draw in that window (the pop-out's, the poster, the on-media strip) would resolve. A theme switch re-renders that root through the same epoch as the main window's.

**What reads the six keys.** `Video.PlacementMenu(includeFullscreen)` (core) still builds the rows (the radio checks, the reasons, the always-on-top toggle): the bar's overflow cascade and
the on-media "more" menu nest the same rows with the aspect, quality and speed cascades (`OnMedia.MoreMenu`, core: the fit and aspect picker is T6b's on-media strip, whose rows are decided from live reads); the
chevron's flyout is the key's presenter.

**Tests.** `Wavee.Tests\VideoContractTests.cs` pins the ids, Classic's registration and the band's height; `VideoRulesTests`, `VideoSurfaceViewRulesTests`, `VideoJoinPhaseTests`,
`PlacementMenuTests` and `RailSurfaceTests` pin the rules that stayed. `Wavee.ShellTests\VideoMountedTests.cs` mounts each view alone (the poster's states, the cap's height and reservation, the
watch frame, the overlay's buttons and zones per placement, the pop-out, the exit chrome, the flyout).

### Home (T10a1)

Home is the structure's picture over core hosts. The page, the facet row, the daylist card and its clock, the zone bodies, the "See all" drill
page are registered Classic views (the customizer is a Common block); the facet machine, the screen model, the load verdict, the shell-tint claim, the daylist's
timers and the layout document stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Home` | `HomePageProps` (the published facet, the scroll handle and key, the facet row's props, the switch signal, the pending / failed / content / seed Funcs, the wash element) | `HomeScreen` (`[CoreHost]`, `Home\HomeScreen.cs`) over `HomeHost`: the effects, `Compute`, `Verdict`, `Seed`, `BuildWash` | `ClassicHomePage`: the wash over the `ScrollView` column (facet row slot, the sticky-clipped content in a `SkelRegionEl`), the keyed facet content over the dim host |
| `Home.FacetRow` | `FacetRowProps` (words and switch as read-only signals, `Index` as a `Func<Signal<int>>`, the select / dismiss / following-memory verbs) | `HomeHost.RowProps` (one record per tab) | `ClassicHomeFacetRow`: the `SelectorBar` in the facet-title style, the Following toggle, the busy bar and the failure bar, pinned as a transparent band |
| `Home.Daylist`, `Home.DaylistClock` | `HomeDaylistProps` (zone, eyebrow, `Pauses`, the keyed clock element), `HomeDaylistClockProps` (a face, the daypart, the lines, the live countdown / elapsed / segment signals) | `DaylistCard`, `DaylistClock`, `DaylistCountingClock` (`Home\Daylist.Host.cs`): the subscribing reads, the window phase, the rollover ladder, the clock mode, the 1-Hz tick | `ClassicHomeDaylist`: art, veil, copy, action row, tag line; `ClassicHomeDaylistClock`: the ring, the two lines, the five-segment timeline |
| `Home.Zone.<Kind>` ×14 (`HomeZoneKeys.TryKeyOf`) | `HomeZoneProps` (zone, overlay, the shelf pager's controller) | none: a pure function of the zone | `ClassicHomeZones.BodyTemplate` under every kind: shelves, release list, cluster cards, browse tiles, episode rows, the empty facet |
| `Home.Zone.RecentGrid`, `Home.Charts` | `HomeRecentsProps` (cells, week, `Playing`, `RemoteDeviceName`), `HomeChartsProps` (the pick) | `RecentGrid`, `ChartsBlockView` (`Home\Zones.Hosts.cs`): the Recents subscription, the Featured Charts demand | `ClassicHomeRecents`, `ClassicHomeCharts` |
| `Pages.HomeSection` | `HomeSectionProps` (total, scroll key, the pending / failed / empty Funcs, the card source, the adapter) | `SectionScreen` (`Home\SectionScreen.Host.cs`): the demand, the table sync, the masthead publication | `ClassicHomeSection`: the "N items" line, the skeleton region, the bound `ItemsView` over `GridFit` |
| `Pages.HomeCustomize` | `HomeCustomizeProps` (the `LayoutDoc`, the `Reorderable`, the toggle / move / reset verbs) | `CustomizeScreen` (`Home\Customize.Host.cs`) over the one `HomeLayout` | `CommonCustomize` (a Common block since T11c, `Themes\Common\CommonCustomize.cs`): the header with Reset, the reorderable rows |

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

### Browse and Search (T10a2)

The Browse directory, its Charts band, a category page, the section drill page and the Search results page are registered Classic views (`Themes\Classic\Structure\Views\Pages`); the load verdicts, the layout and ranking rules, the readiness of
each facet, the demands and retries, the masthead publication and the hit adapters stay core. The omnibar (T4b, `Views\Search`) and the search query model are untouched.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Browse` | `BrowseDirectoryProps` (the scroll key, the `BrowseLoad` verdict, the memoized `BrowseTaxonomy.Grouped` bands, Retry) | `Browse.DirectoryPage` (`[CoreHost]`, `Entities\Browse.Page.cs`): the demand, the verdict, the band list rebuilt only when a tile row changed | `ClassicBrowseDirectory`: the masthead spacer over the `StickyClip(84)` column with the `WhileStuck` feather, the skeleton region (`SkelReveal.None`: the bands own the entrance), the six bands in taxonomy order (the 40 ms cascade from index 1), the loading directory over `BrowseDirectorySeeds` |
| `Browse.Charts` | `BrowseChartsProps` (the `HomeLoad` state, the deck, the tile / header / Retry verbs) | `BrowseChartsBand` (`[CoreHost]`, public: the directory view mounts it as a component) | `ClassicBrowseDirectory.ChartsTemplate`: the Fold deck, its shimmer over the seed deck, the compact empty and the compact error |
| `Pages.BrowseCategory` | `BrowseCategoryProps` (the verdict, the title, the layout `Mode`, one `BrowseBand` per answered band, the memoized concat list, the verbs) | `Browse.CategoryPage` (`[CoreHost]`): readiness, `BrowsePageLayout`, the card demands, the masthead title | `ClassicBrowseCategory`: shelves, tile blocks, the flattened grid, the empty and error arms (the Explore link on every arm), the non-virtual shimmer |
| `Pages.BrowseSection` | `HomeSectionProps` | `SectionScreen` (mounts `Pages.HomeSection` for a Home section) | `ClassicHomeSection`, registered under its own template id so a theme may dress the Browse drill apart |
| `Pages.Search` | `SearchPageProps` (the echo, the tabs, the slide, the verbs, `Pending` / `Failed`, and `Body`: a `Func<SearchBodyModel>`) | `Search.PageHost` (`[CoreHost]`, `Entities\Search.Page.cs`): the query row, the facet strip, the readiness of each facet, the demand and the recheck deadline, the fallback interleave, the rail's items, the column hysteresis | `ClassicSearchPage` (the echo, the facet row and its eleven-pill skeleton, the keyed sliding body, the three loading shapes) and `ClassicSearchBody` (the top result, Best matches, the rail, the Songs grid, the card-grid facets, the lists, the genre links, the related queries, the empty and error vacancies) |

**The shared cells.** The five Browse densities (Word, Name, Link, Bar, Peek), the link grid, the star grid, the category block and the Explore link are `ClassicBrowseCells`; the
Search page's genre links are the same Link cell over the same grid. The numbers are `Rules\ClassicBrowseRules.cs` and `Rules\ClassicSearchRules.cs`; the three column ladders that
`BrowseLayout` held (Genres' fixed 720 / 380 bands, Mood's and More's floor-fits) are `Rules\Tiers.Browse.cs` (`ClassicBrowseTiers`) and no Classic view compares a width with a number (WVTH013).
`BrowseMastheadMetrics` stays core: the Concert page, the module page and the drill page read the same reserve.

**What a Search view may call.** The body is a MODEL the host builds each time the skeleton region asks for its content (`SearchBodyModel`: the hits as `Search.HitItem`s over the live
handles, the fallback rows, the rail, the genre tiles, the related queries), so a hydrating hit row still re-reads in the region's own binding. A hit is drawn with the core adapters
(`Search.HitData`, `OpenHit`, `PlayHit`, `MenuOf`, `DragOf`, `TrailingOf`, `TitleOf`, `CoverOf`, `SubtitleOf`, `CardDataOf`, `OpenRelated`) over the one media surface;
`SearchHitRules` (shape, art, eyebrow, trailing, drag) stays core because the omnibar flyout reads it. Whether track art is hidden is Classic's own option (`ClassicOptions.HideTrackArt()`, M4): the body reads it where a hit row is built and hands it to `Search.HitData`.

**Tests.** The ids, Classic's registration and the grid-facet rule are `Wavee.Tests\BrowseSearchContractTests.cs`; the column bands and the star grid are `BrowseLayoutTests`; the load gate, the facet rules and the hit decisions
stay `BrowseLoadGateTests`, `SearchFacetRulesTests` and `SearchHitDataTests`. The mounted facts are `Wavee.ShellTests\BrowseSearchMountedTests.cs`: Classic is the registered template of every key, a tile block keys
each tile by its destination's uri, the Search facet row is the skeleton until the first body has answered, and then carries one underline and a body keyed by the facet.

### The artist page (T10b1)

The artist page is the structure's picture over a core host. The scroll column, the page's skeleton region, the hero and its collapse into the context band, the section anchors, the Top-tracks band, the shelves, the biography band, the banners and the artist pick card are one registered Classic view
(`Themes\Classic\Structure\Views\Pages\ClassicArtist*.cs`); the demands, the readiness, the section plan, the hero metrics' hysteresis, the accent, the scroll spy and the Play / Shuffle / Radio decisions stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Artist` | `ArtistPageProps` (the route and scope keys, the artist handle, the pending / failed verdicts and Retry, the measured width and the ONE resolved `ArtistHeroMetrics`, the accent as a value, a live read, an untracked read and a signal, the palette and hero urls, the washes flag, the tint claim leaf, the band's engaged edge, the pivot tabs and the spy's answer, the section slots with their anchor callbacks, the fans pool, the verbs; the chart's skeleton reads the row-style and hide-art options itself) | `Artist.PageHost` (`[CoreHost]`, `Entities\Artist.Page.cs`): the demand effects, `DemandRows`, `Stamp`, readiness and the failure verdict, the hero-photo latch and its decode warm-up, the watched accent, `PlanSections` (the plan and the pivot's membership), the anchor registry and the spy | `ClassicArtistPage`: the keyed column and scroller over the `SkelRegionEl` (Reveal.Soft, Group null), the hero, the sentinel, the clipped magazine and wash, the band's artist arm and the derived shimmer; `ClassicArtistHero`, `ClassicArtistSections`, `ClassicArtistBanners`, `ClassicArtistPick` |

**There is no hero key.** The hero's pinned, collapsing root has to stay a direct child of the page column (a slot would put a component boundary between the sticky node and its scroll content) and the derived skeleton cannot run a view slot, so the page view builds the hero for the live page and for the shimmer with the same builder
(`ClassicArtistHero.Build`; the skeleton passes the placeholder facts and null verbs). Since TC6 the hero's sub-tiers, heights, arms, photo bands and copy measures are Classic's tier records (`Rules\Tiers.Artist.cs`, below); the core keeps the collapse arithmetic (`ArtistHeroLayout`) and latches the metrics through the structure's policy, so the host still decodes the photo at the size they name; the Classic numbers the
pictures own (the pick card's pieces, the magazine's dock reserve, the biography band and city bars, the banners) are `Rules\ClassicArtistRules.cs`.

**What the view may call.** The WVTH002 fence applies. What a card opens, plays or puts in its menu, an interned string and an external link are the static `ArtistVerbs` (`Entities\Artist.Verbs.cs`: `Text`, `OpenRoute`, `OpenEntity`, `OpenPlaylist`, `OpenConcert`, `OpenSchedule`, `PlayOrToggle`, `Play`, `PickMenu`, `OpenLink`); the handles' own commands (`Track.GoToAlbum`, `Menus.Container`, `Drag.Source`) and the shared builders
(`Controls.*`, `ClassicDetailBand.Band` / `Pivot`, `ClassicArtistSections.SectionBlock`, the chart and the discography facets as core hosts) are called directly. The chart is `StyleKeys.ArtistChart` and the facets `Artist.FacetSection` (drawn by `StyleKeys.Discography` since T10b2); the Top band mounts the chart as a component, and the page's skeleton draws the chart's skeleton at `ArtistChartSkeletonArgs.For(0, ..)`. The gallery lightbox opens through the host (`OpenGallery`) and is `Artist.Lightbox` since T10b2.

**Motion is the page's, unchanged.** The hero photo's zoom-in and fade on its own ready edge, the parallax and stretch, the collapse into the band (the Leading collapse with no `ClipToBounds`), the band's reveal over the last 44 DIP, the magazine's `WhileStuck` feather, the pick photo's grow-open, the soft reveal over the shimmer and the reduced-motion arms
are the same elements with the same keys. The view keeps the page's latches (the Top band's wide arm, the biography band's mode, the resolved styles) in one hook cell of its slot; the host keeps the tier latch.

**Washes.** The shell-tint claim is the host's (`Palette.ShellTint`, handed over as `Tint`); the blend wash is drawn by the view through `Palette.ArtistBlendWash` and whether it draws at all is Classic's Surfaces option (M6), read by the leaf itself: the host passes no flag.

**Tests.** `Wavee.Tests\ArtistPageContractTests.cs` pins the id, Classic's registration and the value types; the plan, the pivot rules, the failure verdict and the hero tiers stay `ArtistPageRulesTests` and `ArtistHeroLayoutTests`. `Wavee.ShellTests\ArtistPageMountedTests.cs` mounts the page under Classic: the keyed column, scroller and body child while pending
and while failed, no magazine without a body, and the size observer hearing the width.

### The rest of the artist family (T10b2)

T10b2 moves what T10b1 left in core: the Top-tracks chart host's remaining picture, the discography (the artist page's inline facets and the standalone
`disco:` page), the gallery lightbox and the chart's skeleton entry point. (The plan's "reader" is the library's artist reader, which T10c1 already moved:
`Library.ArtistReader`.)

| Key | Props / style | Core half | Classic |
|---|---|---|---|
| `Artist.Lightbox` | `ArtistLightboxProps` (the photos, the selected index as a read signal and its `Select`, the export's `Saving` flag and `Export(index, post)`, `Close`, and `Attach(consumeEscape, closed)`; one record per open) | `Artist.GalleryOpen` (`Entities\Artist.UI.cs` §4.2): the modal overlay (focus-trapped, no light dismiss), the index signal clamped on open, the close, the Escape veto (the overlay asks the view's `consumeEscape`: a zoomed photo unzooms and VETOES the close), the closed hook, and `GalleryExport` (the Save As picker, the download or copy, the notice) | `ClassicArtistLightbox`: the FlipView pager, the pan/zoom surface that REPLACES the pager and the filmstrip (anim-channel writes, the spring home, the frame-clock unzoom settle), the auto-hiding top chrome over its scrim and the filmstrip; numbers in `Rules\ClassicArtistLightboxRules.cs` |
| `Pages.Discography` | `DiscographyPageProps` (parsed, the crumbs' name and title, the facet, the crumb and selector verbs, the grid as `Func<float, Element>`, the scroll key and handle) | `Artist.DiscoPageHost` (`[CoreHost]`): the route parse, the artist handle and its demand, the scroll handle and its 24-DIP floor provided to the grid (`LazyScroll.Slot`, around the slot), the navigation | `ClassicDiscographyPage`: the vacancy for an unparsed route, the crumbs, the title, the facet selector (a fresh signal per render: the route is the truth), the padded scroll column, the grid at the page's 28-DIP reveal inset |
| `StyleKeys.Discography` (`Artist.Discography`) | `DiscographyStyle`: `Metrics`, `Facet(DiscoFacetArgs)`, `FacetFailed`, `Card(DiscoCardArgs)`, `Opened`, `Seed`, `Cell`, `DrawerFrame`, `DrawerPanel`, `DrawerBar`, `DrawerSlot`, `ShowAll`, `DrawerTrack` | `FacetHost` (demand, presence and failure, the era plan and its year-fold memo, the visible range), `DiscoGridHost` (the count memo, the expanded ALBUM SLOT, the drawer's ONE verdict, the tracklist demand and Retry, the LazyGrid mount), `DiscoCell` (the row stamp, the cached card data, the toggle / play / drag / menu), `DrawerPanel` (the selection model, the geometry-keyed list mount, invoke / menu / drag, play-from-here), `DrawerSlot` (the interaction funnel), `DrawerRowHost` | `ClassicDiscography` (`Lists\`): the spined, pinned facet header (an expander with content resize off) and the pinned label that follows the visible era, the grid's sticky clip and its `WhileStuck` feather, the card data and its disclosure chevron, the drawer's revealed slot and caret, the panel, its head, the shimmer and the empty note, the selection bar, the track slot's skin and pill, "Show all", the track cell; numbers in `Rules\ClassicDiscographyRules.cs` |

**The hosts resolve the style once.** `FacetHost` and `DiscoGridHost` each read the registry with one unconditional `UseContext(ViewRegistry.Context)`; the grid
hands its style to the cells (`DiscoCellProps.Style`, never compared) and to the drawer (`DrawerProps.Style`), and the drawer's slots and rows read it off
their panel, so no cell adds a hook. The card data the structure builds is still cached by the cell per (slot, version, height), so a seed hydrating is a
props re-push and an expanded cell re-pushes one reference.

**What stayed core, and why.** `Artist.SectionBlock` and `Detail.Band` / `BandTitle` / `Pivot` (the profile page still builds from them until T10c2), the
facet's selection and demand state, the gallery's open and index state, the chart's skeleton counts (`ArtistSections.ChartSkeletonShape`), and the drawer's
verdict and geometry (`Album.DrawerVerdict`: the structure reads its row pitch and top gap). `Artist.ChartSkeleton` and `Artist.DiscoGap` are gone: the
page's skeleton calls the chart style's `Skeleton` with `ArtistChartSkeletonArgs.For`, and the gap is `ClassicDiscographyRules.Gap`.

**Tests.** `Wavee.Tests\ArtistMoreContractTests.cs` pins the two ids, Classic's registration and the reference-only props equality;
`Theming\ListSurfaceStylesTests` the discography style; `ArtistPageRulesTests` the skeleton's counts; `DiscoCellPropsTests` the cell's gate (the style is
not data). `Wavee.ShellTests\ArtistMoreMountedTests.cs` mounts the lightbox (the Escape veto wired on mount, the selected photo keyed in the pager, a core
selection reaching it) and the discography page (no grid for an unparsed route, the core's grid at the 28-DIP inset).

### The concert surfaces and the module page (T10b3)

The Concerts hub, an artist's tour dashboard and the event page are registered Classic views (`Themes\Classic\Structure\Views\Pages\ClassicConcert*.cs`); the module page is a Common block that Classic registers. The data and its demands (the feed subject per filter tuple, the count preview, the place's concepts, the saved place, the artist's schedule edge, the concert row), the filter tuple and its decisions (`ConcertHub`), the near-tail watch and the append gate, the readiness verdicts, the schedule shaping (`ConcertScheduleShaping`), the detail facts (`ConcertOffers`, `ConcertDetailInfo`, the provider's clock), the location picker's search, locate and save, and ticket links stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Concerts` | `ConcertHubProps` (the scroll scope and handle, the pending / failed Funcs, the settled body as a plan: `ConcertHubBody` of `ConcertFeedBlock`s, Retry, `OpenPicker`, the filter bar's `ConcertFilterBarProps`, the grid's count memo and `GridSlotAt`, `Tail`) | `Concert.HubHost` (`[CoreHost]`, `Entities\Concert.Hub.cs`): the filter tuple as signals, `FeedSlot`, `Sync` (the demand, the concepts, the count), `Append`, the near-tail watch, the readiness, the feed's plan; it provides `LazyScroll.Slot` around the slot | `ClassicConcertHub`: the two-clip sandwich (header card, pinned filter bar, feed region), the shelves, promo shelves, the lazy grid and the pending silhouette; `ClassicConcertFilterBar` (the pinned card, the where / when pills and genre tokens, the ticker) and `ClassicConcertFlyouts` (the when drill-down and the where radii) |
| `Concert.Tail` | `ConcertTailProps(ConcertTailState)` | `Concert.TailHost` (`[CoreHost]`): the three-attempt gate, the 300-ms arm, near / loading | `ClassicConcertHub.TailTemplate`: nothing, the 16-DIP spacer or four shimmering tiles |
| `Concert.PlacePicker` | `ConcertPlacePickerProps` (the query's writable cell as a `Func<Signal<string>>`, the results, loading and error signals, `UseMyLocation`, `Select`) | `Concert.PlacePicker` (the controller and its anchored open, `Entities\Concert.Picker.cs`) mounting `Concert.PlacePickerHost` (`[CoreHost]`): the 220-ms debounced search, the consent flow, the save | `ClassicConcertPicker`: the search field, "Use my location", the error line, the keyed result rows or the one-row message |
| `Pages.ArtistConcerts` | `ConcertScheduleProps` (the route key and scroll scope, pending / failed, `Empty`, `Model` and `Seed` as Funcs over `ConcertScheduleModel`, `SelectMonth`, `OpenPicker`, `OpenSpotlight`, `CaptureAnchor`) | `Concert.ScheduleHost` (`[CoreHost]`, `Entities\Concert.Schedule.cs`): the demand and the two location reads, the readiness, the selected month, the model's shaping | `ClassicConcertSchedule`: the split hero with the next show, the tour-dates card with its month tabs, `ClassicConcertMonthBoard`, `ClassicConcertShyMonthPill` and its linger clock |
| `Pages.Concert` | `ConcertDetailProps` (the route key and scroll scope, `Pending`, `Unavailable`, `Detail` as a Func over `ConcertDetailView`) | `Concert.DetailHost` (`[CoreHost]`, `Entities\Concert.Detail.cs`): the whole-row demand, the settled verdict, `DetailViewOf` | `ClassicConcertDetail`: the split hero with the facts, the tickets rail (or inline), the lineup card, the related shelf |
| `Pages.Module` | `ModulePageProps` | `Modules.PageHost` (`[CoreHost]`, `Platform\Modules.Page.cs`) | `CommonModulePage` (`Themes\Common`), registered by Classic under `common.module.page` |

**The artist page's "Upcoming concerts" card** is `ClassicConcertCells.Stub`, called by `ClassicArtistSections`. The cells every concert surface shares (the event tile, the browse-all lead cell, the date block, the split hero, the location button, the pills and tokens, the fused pill with its `SegmentedPillStyle`, the under-band cut, the sub-page scroll frame and the skeleton region) are `ClassicConcertCells`. The tile's facts are the core value `Concert.TileData` (`TileOf`, `SeedTile`), so a skeleton derives its shimmer from the same builder; the interned strings, the opens and the playlist play are the static `ConcertVerbs` (`Entities\Concert.Verbs.cs`). `BrowseMastheadMetrics` stays core: the hub, the schedule and the event page read the same reserve as Browse.

**View state lives in the view.** The filter bar keeps the genre strip's expansion and the two flyouts' overlay handles; the month board's expansion sets, the viewport handle and the shy pill's crossing test are one hook cell (`ctx.Hooks.UseRef`) shared by the board and the pill, and the pill reads the viewport's scroll handle itself (the old page published an 8-DIP floor of it in a signal only the pill read). The 760 / 720 and 920 / 860 hysteresis latches are one hook cell each (the skeleton's seed build shares it, as the old page's fields did). The count ticker's tween and the linger clock mount only while they run. What the host decides (the filter tuple, the subject, the append gate, the selected month, the readiness) reaches the view as values, signals and verbs.

**Tiers and numbers.** `ConcertLayout` is gone from core: the schedule's and the event page's wide ladders, the split hero's metrics and the grid's column floor are `Rules\Tiers.Concert.cs` (`ClassicConcertTiers`), and no view compares a width with a number (WVTH013); the page views answer the ladder from `Responsive.Of`'s width. The literals are `Rules\ClassicConcertRules.cs`. The two `DarkOr` branches of the old file (P21) are `R.Layer.EventPlate` and `R.Layer.EventHeroPlate` (the tile, the card and the split hero's plates; the hero's tint pull is `R.Wash.ConcertMediaMix`).

**Motion and identity are the old page's.** The count ticker's 380 ms cubic-out tween on the frame clock, the where / when pills' shared `when-pill` key and its 260 ms width reflow, the "This weekend" chip's exit into the pill and the segment's dock in from it, the flyouts' forward / back slide, the hero's seam gradient and photo reveal, the month board's keyed remount on its composed signature, the shy pill's 200 / 260 ms presence and 1 s linger, the soft skeleton reveal and every key (`concert:<slot>`, `concert-stub:`, `hub-shelf:`, `hub-promos:`, `hub-grid`, `hub-grid-lazy:`, `concert-append:`, `dashboard-month:`, `shy-month:`, `offer:`, `act:`, `lineup:`) are the same elements. The near-you set, the "Set location" action, the "tickets not available online" line (a validated external link only, never a dead button), the inert billing-only act and the omitted empty lineup / related sections are unchanged.

**What a concert view may call.** The WVTH002 fence applies (no `Shell`, `Entities`, `Playback`, `Spotify`, `Store`, `Prefs`). The handles (`Concert`, `Playlist`, `Artist`, `Track.GoToArtist`), the pure rules (`ConcertHub`, `ConcertScheduleShaping`, `ConcertCopy`, `ConcertPlaces`, `ConcertTime`) and `Actions.Services.OpenExternal` are called directly. The overlay options of the flyouts are `Concert.FlyoutOptions` (focus trap, light dismiss).

**The module page.** The `module:` page, the one page a playback module describes, is a Common block (`Themes\Common\CommonModulePage.cs`, numbers in `Themes\Common\Rules\CommonModuleRules.cs`; namespace `Wavee.Themes.Common`). It is written once and registered by every structure, Classic included, under the template id `common.module.page`; the route parse, the load, the stage-playable claim and the verbs stay core (`Modules.PageHost`, `Platform\Modules.Page.cs`, with `Modules.PlayOrToggle`, `OpenUrl`, `Navigate`, `InvokeFor` and `PlayingModuleUri` as the view's verbs and reads). The view draws the masthead reserve column, the stage slot (an EMPTY keyed box for a non-watch document, and always a sibling ABOVE the scroller: the stage hosts the app's one composited video, so no ancestor may carry an opacity, a transition or an edge fade), the scroller over the `Skel.Region` (the derived loading shimmer, the failed body with Retry and "Open in browser", the unparsed-route vacancy), the entity reading (hero, actions, text / facts / playables / cards / links sections under the shared item budget, unknown kinds skipped) and the watch reading (title, live badge and meta, channel row, chips with the inert Playing capsule, description card, 16:9 shelf). Its motion (the sections' fade-up and shove, the soft reveal) and keys are the old tree's.

**Tests.** The ids, Classic's registration (and the Common module block's), the props identities and the skeleton's silhouette are `Wavee.Tests\ConcertContractTests.cs` and `ModulePageContractTests.cs`; the shaping, the hub's decisions, the offers and the ladders stay `ConcertSchedulePageTests`, `ConcertHubModelTests`, `ConcertDetailModelTests` and `ConcertLayoutTests` (the ladders now over `ClassicConcertTiers`). The mounted facts are `Wavee.ShellTests\ConcertMountedTests.cs` (Classic is the registered template of every key; the filter bar is the keyed pinned card at rest and fuses its pill from the host's signal; a feed body mounts the host's tail under the block's key; a pending schedule or event page reads no model; a ready one keys its board, offers and acts; the picker's rows) and `ModulePageMountedTests.cs`.

### The library and its panes (T10c1)

The library page (the albums / artists / podcasts / audiobooks master-detail) and its four panes are registered Classic views (`Themes\Classic\Structure\Views\Pages\ClassicLibrary*.cs`). The data (the relations, the rows, the demands),
the navigator's shape memo (filter, sort, the letter groups, the a-z gate, the remount identity `LibraryNavMount`), the selection and its persistence (`LibraryViewPrefs`), the search matcher and its selection commit,
the collapse state and every pane's model, readiness and verbs stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Library` | `LibraryPageProps` (the title and kind, the arm facts: collapsed, saved-episodes mode, full search, shimmer, depth and crumbs; the persisted signals `LeftW`, `MidW`, `Sort`, `Desc`, `View`, `Size`, `Filter`, `PodcastsMode` and their commit verbs; the width seam `SetPageWidth` and the kind chips `Kinds` (the layout ladder's source and the one-column head's chips); the pane and Your Episodes slots; `LibraryNavigatorProps`: the load state, the bound source, the mount and the letters' signals; `LibrarySearchProps`: the hit lists as lazy Funcs and the selection) | `User.LibraryPage` (`[CoreHost]`, `Entities\User.Page.Library.cs`): the shape memo, the selection and its re-assert effects, the letters, the demand, the search state, the persistence, the layout tier (`UseTier` over the structure's `library.layout` ladder); `ResolveMount()` is the one place the mount identity, the selection pre-write and the cached layout and options meet | `ClassicLibraryPage` (`.cs`, `.Toolbar.cs`, `.Nav.cs`, `.Search.cs`): the root that reports its width, the wide arm (navigator column, `lib:grip`, the pane column), Your Episodes, the three search columns, the collapsed drill (the kind chips over the root rung, a crumb bar over a leaf) keyed by depth, the two arms cross-faded; the toolbar with the word rail and view toggle (Compact: kind chips, a sort button and 40 view cells); the bound list, rows, cards and letter headers; the hit rows |
| `Library.AlbumPane` | `LibraryAlbumPaneProps` (the readiness state, the keyed child's key, the header facts, the verbs, the notice, the region's cached `Loadable`, the shimmer count and row height, `Table` and `AlsoBy`) | `Album.Pane` (`[CoreHost]`, `Entities\Album.Pane.cs`): the demand (identity, rows, credits, partial pages), `AlbumPaneReadiness`, the keys, the log, the retry | `ClassicLibraryAlbumPane`: the header and its stated-height skeleton, the command row, the keyed swap with its crossfade, the counted shimmer under the header shim, the failed strip and the "Also by" strip |
| `Library.ShowPane` | `LibraryShowPaneProps` (the header facts, the primary as a Func over the accent, the heart's presence bind, the menu, the reader element) | `Show.Pane` (`[CoreHost]`, `Entities\Show.Pane.cs`): the selection signal, the memo pair, the demand, the reader's per-show key | `ClassicLibraryShowPane`: the two arm keys (`showpane:body` / `showpane:skel`), the swap, the header, the command row and the reader slot |
| `Library.SavedEpisodes` | `LibrarySavedEpisodesProps` (pending, failed, the content and failed arms as Funcs) | `SavedEpisodesReader` (`[CoreHost]`, `Entities\User.SavedEpisodes.cs`): the read of the listen-later playlist, the edge demand, the table | `ClassicLibrarySavedEpisodes`: the region with the cold seed of the structure's episode rows and the fade-only reveal |
| `Library.ArtistReader` | `LibraryArtistReaderProps` (the host seam, the mount's `ListKey`, counts, `RepeatLayout` and `ListOptions`, the page's scope and sort signals, the spine's controller and count, the bounds and mount callbacks; never equal to another instance) | `Artist.Reader` (`[CoreHost]`, `Entities\Artist.Reader.cs`): props channel, shape memo and block order, catalogue gate, demand, `BlockCell` readiness, extent corrections, scroll spy and spine follow, mount key, width arms, verbs; it implements `IArtistReaderHost`, and each block cell `IArtistReaderBlock` | `ClassicLibraryArtistReader` (`Views\Pages`): the lane, the band in both arms, the name column, the pinned sub-rail, the cover spine and dots, the block faces and their counted skeletons, the tail |

**Every branch root is keyed, and the cache is the template's.** The old page kept the picker, the two grips, the sticky letter and the A-Z strip as component fields, and the slot templates and the navigator's thunks as readonly
delegates, because a key on a branch root is what turns a branch swap into the remount a bound `Width` needs and a fresh delegate would re-arm the skeleton region. A template has no fields, so the same objects live in one
cache per mounted template (`ctx.Hooks.UseRef`), built lazily: keys, binds and mounts are the old tree's. The host rebuilds ONE props record per render (it compares by reference) and caches the navigator's record, whose
delegates are the host's stable fields.

**The collapse ladder is the structure's, and a named ladder of its `TierPolicy`.** The old 640 / 664 collapse (`LibraryLayoutBreakpoints`, then `ClassicLibraryTiers.Collapsed`) is `ClassicLibraryTiers.Layout`
(`Rules\Tiers.Library.cs`; TC7): the split from 600 of the page's own width, held 24 either way. The view reports its measured width (`SetPageWidth`); the core host resolves the structure's `library.layout` ladder
(`Wavee.Theming.LibraryLadders`, a structure that names none is always split) through `UseTier` over it, seeded from the same-frame content estimate, so a page mounted at Compact is one column in its FIRST frame and a
crossing after it commits on the first non-resized frame, where the two arms cross-fade (`s_arm`) instead of snapping. The answer is the host's `Collapsed`, which the selection, the crumbs and the persisted pick read; no
Classic view compares a width with a number (WVTH013). The numbers within a layout are the record, `ClassicLibraryTiers.Of(facts, single)` (see "The collection at every tier (TC7)").

**Column widths (M13).** `library.<kind>.leftw/midw` are Classic's theme *state* (`theme.classic.state.library.<kind>.leftw|midw`, owner Q6: per theme), one `StateKey` per kind and column declared by the core host that
writes them (`LibraryViewPrefs.LeftWKey/MidWKey`; default 340, 280 for artists, and 440; clamped on every read and write to the grips' range, which core spells as `LibraryViewPrefs.LeftMin` / `LeftMax` / `MidMin` / `MidMax`
and a test pins equal to `ClassicLibraryRules`). `LibraryViewPrefs` (`Read`, `SetLeftW`, `SetMidW`) keeps the door; the view only receives the two signal instances and the commit verbs. The `LibraryColumnWidths`
`ThemeBootstrap` step copies the eight stored widths once.

**What a Library view may call.** The WVTH002 fence applies. The navigator's data adapters stay core and are called by the rows (`LibraryRows`, `User.NavSubtitleText`, `User.ArtOf`, `User.DragOf`, `User.HitDrag`,
`ClassicLetterKit.StickyLetter` / `JumpStrip` / `LetterPlate` / `LetterText` (`Views\Pages\ClassicLetterKit.cs`, shared with the profile lists), `ClassicDetailKit.MoreButton` / `CommandCircle` / `ArtistLine`); a hit row is the shared media surface over `Search.MenuOf` and `Track.RowData`; the pane's verbs (play, shuffle, open,
retry, the ⋯ menu) arrive as props. The numbers are `Rules\ClassicLibraryRules.cs`, `ClassicLibraryPaneRules.cs` (the pane header's stated height and type budget, the swap, the shimmer, the "Also by" strip),
`ClassicLibrarySearchRules.cs` and `ClassicLibraryReaderRules.cs`. The pane header, its loading twin, the command row and the swap are shared by the album and the show pane (`ClassicLibraryPaneKit`).

**Skeletons.** The navigator's eight shimmer rows cross-dissolve into the rows (`SkelReveal.StaggerRows`); the album pane's header skeleton states the real header's height, so the crossfade is a dissolve and never a reflow; its
counted shimmer is the table's own lanes and row pitch under the header shim; Your Episodes seeds with the structure's episode rows; the reader's blocks keep their analytic extents.

**Tests.** The ids, Classic's registration, the by-reference props and the pane arithmetic are `Wavee.Tests\LibraryContractTests.cs`; the ladder and the records `LibraryTiersTests`; the search row shapes `LibrarySearchRowTests`;
the pane header's budget `AlbumPageRulesTests`; the "Also by" strip `AlbumSurfaceRulesTests`; the reader's chrome `ArtistReaderChromeTests`. The mounted facts (Classic is the registered template of every key, the wide arm's keyed
columns, the page's width reported to the host, the kind chips over the root rung and the crumb bar over a leaf of the depth-keyed drill, the header skeleton at the stated height, the show pane's arm keys) are `Wavee.ShellTests\LibraryMountedTests.cs`.

### The Recents page (T10c2)

The Recents page is the structure's picture over a core host. The masthead, the pivot strip, the skeleton region, the semantic surface (the grouped list with its pinned day band, the group card, the single-play row, the drawer, the annotated rail) and the calendar overview are registered Classic views (`Themes\Classic\Structure\Views\Pages\ClassicRecents*.cs`); the demand, the shape and its atomic publication, the midnight rollover, the refresh after a play, the accordion's state, the sticky band's arithmetic, the page accent and its shell wash, the playlist bylines' owner demand and every verb stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Recents` | `RecentsPageProps` (the host seam `IRecentsHost`, the summary line, the shape epoch, the pending / failed Funcs, Retry, the Overview verb, the page's key handler, the page accent signal, and the two zero-size core leaves: the accent binder and the owner demand) | `Recents.PageView` (`[CoreHost]`, `Entities\Recents.Page.cs`): the shape (`RecentsShape`: snapshot, pivot cut, sections, calendar, stateful measured layout), `BuildShape`, `Adopt`, the rollover, the accordion, `UpdateSticky`, the rail labels, the verbs; `Entities\Recents.Facts.cs`: `FactsOf`, `CoverOf`, `SubscribeRow`, the accent fallback and the heat's fill | `ClassicRecents` (the page: Ctx.Provide of the accent, the masthead, the pivot strip, the region with the FLIP, the derived shimmer source, the empty vacancy), `ClassicRecentsList` (semantic surface, bound list, row slot, pinned day band, rail, day header), `ClassicRecentsRows` (group card, Saved stack, single-play arm, drawer, children, episode rows), `ClassicRecentsCalendar` (the overview, the band, month cards, cells, the legend) |

**The slots are the view's components, and they read the core through `IRecentsHost`.** The bound list's row slot, the pinned band, the rail, the calendar and its month cards were components that read the page; they are now Classic components that read the host (one stable object per page: signals, controllers, bound `Prop`s, pre-created delegates and the verbs), so a read inside a slot's render subscribes THAT slot, never the page. The host renders only what it always did (it builds one props record per render, which never compares equal to another); everything that touches `Entities`, `Playback` or `Prefs` (the row facts, the row's table subscription, the artwork policy, a drawer member's play) is a host member, because a view may not name them (WVTH002).

**Motion is the page's, unchanged.** The masthead's two lines 45 ms apart, the underline's scaleX enter, the body FLIP (a value under reduced motion), the drawer's spring reveal, the zoom's 1.08 / 0.94 crossfade, the brush glide of every accent consumer (bound `Prop`s over the page's thunks), the pinned band's quantized push (a bound transform: a scroll frame never re-renders it) are the same elements with the same keys. The skeleton source is the real day headers and the row's real geometry over the pending seed rows, so the shimmer matches the real heights.

**Numbers.** The card's art, the Saved stack's tiles, the child lanes and the single-play lanes are `Rules\ClassicRecentsRules.cs`; the grouped layout's own geometry (the day header and row heights, the calendar's cell and card sizes, the sticky push quantum) stays in the core `RecentsLayout` because the layout, the rail and the calendar grid read it. There is no width threshold in the page: the calendar's columns come from the engine's GridFit, and the tier's numbers are `ClassicRecentsTiers.Of(facts)` (see "The collection at every tier (TC7)").

**Tests.** The id, Classic's registration, the unknown facts and a shape over the pending seed are `Wavee.Tests\RecentsContractTests.cs`; the grouping, the time buckets and the layout arithmetic are the existing `RecentsView` / `RecentsLayout` tests. `Wavee.ShellTests\RecentsMountedTests.cs` mounts the view over a fixture host: Classic is the registered template, a pending page keeps the pivot strip and the core leaves and mounts no surface, an answered empty relation shows the vacancy, an answered relation mounts the keyed semantic surface with its list and rail.

### The profile page (T10c2)

The profile page (your own profile and another person's, route `user`) is the structure's picture over a core host. The scroll column and its page-level skeleton region, the round-avatar hero and its collapse into the context band, the sentinel, the blend wash, the clipped magazine, the band (title, pivot, Follow as a word), one region per section and the cards are one registered Classic view (`Themes\Classic\Structure\Views\Pages\ClassicProfile*.cs`); the demands, the load faces, the reveal latch, the section plan and each section's body verdict and cards, the hero metrics' hysteresis, the accent, the scroll spy, the tint claim and every verb stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.User` | `ProfilePageProps` (route / scope / scroll keys, the uri, name and own flag, `Pending` / `Failed` / `Retry` / `Unavailable` / `BodyReady`, the measured width and the ONE resolved `ProfileHeroMetrics`, the accent as a value, a live read, an untracked read and the page-accent signal, the palette url and payload accent, the washes flag, the tint claim leaf, `ProfileHeroText` + `ProfileHeroActs`, the band's engaged edge, the lit-tab signal, the scroll handle / viewport capture / scroll offset, `ScrollToTop`, and the section slots) | `Profile.PageHost` (`[CoreHost]`, `Entities\Profile.Page.cs`): `ProfileAsk` demands, `Stamp`, `ProfileLoadRule.Header`, `ProfileReveal`, `PlanSections` (the plan, the pivot hash, each section's `ProfileSectionBody` verdict, its `ProfileCard[]`, "See all", Retry, tab click, anchor), `ResolveSpy`, `PublishAccent` | `ClassicProfilePage` (the keyed column and scroller over the `SkelRegionEl`, Reveal.Soft, Group null; the magazine, the band, the shimmer source), `ClassicProfileHero`, `ClassicProfileSections`; numbers in `Rules\ClassicProfileRules.cs` |

**`ProfileSectionSlot`** is one planned section: `Section`, `Body` (Seed / Cards / Empty / Error), `Cards`, `SeeAll`, `Retry`, `Click` (the pivot tab) and `Anchor` (`OnRealized`, the spy's registry). The slots are also the pivot's tabs (`PivotLabel` is the view's word), so the plan and the pivot cannot disagree.

**Per-section regions keep their delegates.** Every section is its OWN `SkelRegionEl` (FadeOnly, Group null: a late list shimmers in place and never holds the hero). The old page latched each section's verdict and element in host fields read by cached region delegates; a fresh delegate would re-arm a skeleton region, so the view keeps the same latch in ONE hook cell of its slot (`ClassicProfileLatch`) with the delegates built once.

**There is no hero key**, for the artist page's reason: the pinned, collapsing hero root has to stay a direct child of the page column and the derived skeleton cannot run a view slot, so the page view builds the hero for the live page and for the shimmer with `ClassicProfileHero.Build` (the shimmer passes the placeholder facts and null verbs). The hero's sub-tier is the artist hero's (the structure's hero ladder); what each sub-tier lays out per window tier is Classic's tier records (`Rules\Tiers.Profile.cs`, TC6); the stat plan and the collapse arithmetic (`ProfileStats`, `ProfileHeroLayout`) stay core in `Entities\Profile.Rules.cs`.

**Own versus another person.** Your own page has the "Open account" button (`ProfileVerbs.OpenAccount`) and the top-artists section and no Follow; another person's has the Follow toggle in the hero and the Follow word in the band. The decision is `ProfilePageProps.Own` (the host's `_own`); the lists a person shows (`ProfileSections.ShowsPeople`) are the plan's.

**What the view may call.** The WVTH002 fence applies. What a card opens is `ProfileVerbs.Open` (a playlist, an artist or a person), a playlist's title `ProfileVerbs.TitleOf`, what a card plays `DetailVerbs.PlayContext`, its menu and drag the shared `Search.MenuOf` / `Search.DragOf`; the hero's share, ⋯ menu and list links arrive as `ProfileHeroActs`. The Follow toggles are the shared `Controls.FollowToggle` / `FollowTextAction`, and `Profile.PageHost` installs their `Controls.Library` seam. The shared band, pivot, ⋯ and command circle are `ClassicDetailBand` / `ClassicDetailKit`, the section shell `ClassicArtistSections.SectionBlock`.

**Washes.** The shell-tint claim is the host's (`Palette.ShellTint`, handed over as `Tint`); the blend wash is drawn by the view through `Palette.ArtistBlendWash` with the washes flag. Unlike the artist page the wash is NOT cut at the band (a profile hero has no photo, so a cut wash left a bare strip of shell colour above the name).

**Skeletons.** The shimmer is the SAME hero over a placeholder (no band, the action row a `Skeletonized(false)` spacer), the divider and two seed shelves at the magazine's real width (own: Top artists + Public playlists; other: Public playlists + Recently played artists), each at the shelf's real fit.

**Tests.** The id, Classic's registration and the value types are `Wavee.Tests\ProfilePageContractTests.cs`; the plan, the stats and the tiers stay `ProfilePageRulesTests`. `Wavee.ShellTests\ProfilePageMountedTests.cs` mounts the page under Classic: the keyed column, scroller and body child while pending, the 404 and failed faces without a magazine, and a ready own page with its keyed anchor, section and no Follow.

### The profile list pages (T10c2)

The Following / Followers "See all" page (`people:<facet>:<user uri>`) is a registered Classic view (`Themes\Classic\Structure\Views\Pages\ClassicProfileList.cs` and `.Cards.cs`). The demand and its retries, the pooled entry model (the filing order, the All / Artists / People chip and the find box), the load rule that picks the face, the grid plan, the flat letter projection with its pinned extents, the sticky-letter watch and the list's mount cache stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.ProfileList` | `ProfileListProps` (the parsed flag; the head `ProfileListHead`: name, title, facet, the two selector words, the open-profile and pick-facet verbs; the region's pending / failed Funcs and Retry; the `ProfileListShape` signal: load face, facet, counts, grid plan, row count, mount key, epoch; the facet and body width as untracked reads; the size observer; the find box and chip verbs; `Mount(key)` → `ProfileListMount` (layout + options); the bound rows; the sticky letter, push, present mask and jump; the pinned extent, visible count and visible entry reads) | `ProfileLists.PageHost` (`[CoreHost]`, `Entities\Profile.Lists.Page.cs`): the demand, `Sync` (entries, filter, letter rows, the shape), the letters watch, `EnsureMount` | `ClassicProfileList`: the head (breadcrumb, page title, selector bar), the measured body over ONE soft skeleton region, the keyed faces (`people:ready` / `empty` / `hidden` / `unavailable` / `nomatch`), the tools, the keyed list with `ClassicLetterKit`'s sticky letter and strip, the row slot (letter header or a pinned-height row of cards), the cells with their Follow lines and the derived shimmer |

**Every face is a keyed child of one stable root**, and the region's delegates (content, shimmer, failure, the row template, the chip clicks) plus the sticky letter and the strip live in one cache per mounted template (`ctx.Hooks.UseRef`), so keys, binds and mounts are the old tree's and a fresh delegate never re-arms the skeleton region.

**What the view may call.** The WVTH002 fence applies. A card opens, plays and menus an artist through `HomeCardNav`; a person card opens through `ProfileListVerbs.OpenPerson`, and the "N followers" lines are `ProfileListVerbs.ArtistSubtitle` / `PersonSubtitle` (`Entities\Profile.Lists.Verbs.cs`). The grid fit's arithmetic (`ProfileGridFit`: columns, the card row, the action line) stays core; where the strip and the side-by-side tools appear (the list ladder) and the narrowest card per tier are the structure's (TC6); the shimmer's bones and row count are `Rules\ClassicProfileListRules.cs`.

**Tests.** The id, Classic's registration, the by-reference props and the shape's value equality are `Wavee.Tests\ProfileListContractTests.cs`; the load rule, the model, the grid and the letter rows stay `ProfileListRulesTests` / `ProfileListRouteTests`. `Wavee.ShellTests\ProfileListMountedTests.cs` mounts the page under Classic: each settled face is one keyed child, the tools stay mounted for a find that matched nothing on both facets, and a pending list shows only the shimmer.

### The detail frame (T9a)

The shared detail frame is the structure's picture over a core host. Every album, playlist, Liked, show and episode page hands `Detail.Frame` one
`FrameSpec` (identity, config, verbs and slots); `Detail.FrameHost` decides and the registered views draw. T9b (album, playlist) and T9c (Liked, show,
episode) own the per-kind slot builders and their sub-contract keys; the frame places whatever the slots return, so they plug in without moving it.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Detail.Frame` | `DetailFrameProps` (the arm, the page accent, the measure callback, the tint / tone / notice leaves, the keyed content column and its floor, the drop target, then per arm: the show header, the sheet, or `DetailTwoColumnProps`) | `Detail.FrameHost` (`Entities\Detail.UI.cs`): the mode ladder and both anti-flicker fail-safes, the Hero page-layout override, the per-scope rail cells, the accent, the data gate, the table mount | `ClassicDetailFrame.Template`: the vertical page (notice over the content, always inside the insights overlay for a track page) or the centred rail \| content row with the grip in its seam layer |
| `Detail.Rail` | `DetailRailProps` (spec, trampolined verbs, the rail width, the title cap and description clamp the window rung picked, the accent thunk, play-all, the pending flag) | the frame host | `ClassicDetailFrame.RailTemplate`: the layer fill and scroller around one skeleton boundary; the column and its loading twin walk the same row model |
| `Detail.CompactRail` | `DetailCompactRailProps` | the frame host | `CompactRailTemplate`: the 96-DIP strip (cover, two-line title, expand chevron) |
| `Detail.ShowHeader` | `DetailShowHeaderProps` | the frame host (a non-track page's vertical arm) | `ShowHeaderTemplate`: cover 140, the info column, the CTA cluster |
| `Detail.Hero` | `DetailHeroProps` (the `VerticalSpec` and the table's `HeroParts`, compared by data) | `Detail.Hero` (`Detail.UI.Hero.cs`) is a plain box the TABLE pins and collapses, around the slot | `HeroTemplate`: the expanded hero and the pinned 56-DIP context band, scroll-bound |
| `StyleKeys.DetailHero` | `DetailHeroStyle` (`Skeleton`) | the table reads it through the registry, like `TrackListStyle` | `ClassicDetailFrame.HeroStyle`: the loading band is a PURE builder because the skeleton deriver cannot run a view slot |
| `Detail.Insights` | `DetailInsightsProps` (the page, the toggle, the latched facts builder, the page width, the route key, the close edge) | the frame host: the toggle, the facts latch, the focus return | `ClassicInsights.SheetTemplate`: the engine `SplitView` (Overlay x Right) with the scrim, the cast, the frosted pane |
| `Detail.InsightsToggle` | `DetailInsightsToggleProps` (the toggle and its `Form`) | the hero's toolbar row, and `Detail.InsightsBandAction` for the table's pinned band | `ToggleTemplate`: the glyph button, or the band's plateless word (its own component) |

**What stays core, and why.** The decisions the frame, the table and the skeleton must agree on row for row are core, pure and unit-tested: the
breakpoint ladder (`Breakpoints`), the rail's width bands (`RailPolicy`) and the persistence of its remembered sizes (`DetailRailPrefs`, theme state since M8),
the hero arithmetic (`VerticalLayout`, `BandLayout`, `Skeleton.RailPlanFor`, `RailLayout`, `Detail.FlagsOf`, `Detail.HeroBandHeight`), the notice rules,
`InsightsSheet` (which arm hosts the facts, the sheet's width), `Identity` / `Config` / `FrameSlots`, the shell-tint and page-tone claims, and the
accent. No view compares a width with a number (WVTH013): the views are handed the mode, the rail width, the rung and the strip width. The numbers
the pictures own (the cover bucket and floors, the show header's cover and measure, the sheet's grip, toggle, shadow and spline) are
`Themes\Classic\Structure\Rules\DetailLayout.cs` (`ClassicDetailLayout`). `Config.SlimHead` / `HoverHeart` / `PlainRows` (PR 205) are not on this
branch; they leave `Config` for Liked's view in T9c.

**The records compare by reference.** The host builds a props record on every render, and `FrameSlots` equality is PRESENCE only, so a data-equal
spec can carry newer page builders; a view that skipped on an "equal" record would keep the old ones. The frame's records therefore never compare equal
to another instance (the view re-runs exactly when the host re-rendered, which is when the tree used to be rebuilt). `DetailHeroProps` is the one
that compares by data and by the reference of the table's pieces: the table owns that gate.

**What the pages keep calling.** The page-facing builders that used to stay in `Detail` as the seam are all Classic's since T10c2: the band (`Band` / `BandTitle` / `BandByline` / `BandHairline` / `Pivot`) is `ClassicDetailBand` (`Views\Detail\ClassicDetailBand.cs`); `Satellite`, `Fab`, `MoreButton`, `ArtistLine`, `CommandCircle` (was `Album.CommandCircle`) and the `SaveTargetOf` / `ShareActionFor` targets are `ClassicDetailKit` (`ClassicDetailKit.Helpers.cs`); the A-Z overlay, plate and strip (was `User.StickyLetter` / `LetterPlate` / `JumpStrip` / `LetterText`) are `ClassicLetterKit`; the artist section shell (was `Artist.SectionBlock`) is `ClassicArtistSections.SectionBlock`. `Detail.BandLayout` (the band's numbers and the scroll-spy arithmetic) stays core: the artist and profile hosts read it. The library album pane's props carry the billed `Artists` (the view builds the line). The ones the album and the playlist, and the frame's own views, used moved in T9b (below): the keyed rail rows, the eyebrow run and
the billed-artist line box are the shared Classic kit (`ClassicDetailKit.cs`), and `NoticeBar` is a mount of the `Detail.Notice` view. T9c moved the
rail satellites (`ClassicDetailKit.Satellites.cs`) and dropped `Detail.PlayButton`. A view may not name `Shell`, `Playback`, `Entities` or `Store`
(WVTH002): what a detail view must do that touches them is `DetailVerbs` (`NavigateKey`, `OpenOwner`, `PlayContext`, `ChartCaptionText`).

**Motion and keys are the host's old tree.** The content column keeps its key across a rail collapse so the table reconciles in place; the grip lives in
the seam layer in both arms (a drag collapses and re-opens mid-gesture); the track page's vertical arm is always wrapped in the sheet's overlay so the
facts arriving never remount the list; the sheet's slide (350 ms open, 120 ms close, reduced motion snaps), its focus trap and return, the hero's
parallax and band reveal, and the rail's row FLIP and fade-up are unchanged.

**At every tier (P3t, TC5).** The frame's three width and height ladders are no longer core numbers: the page MODE ladder (the vertical hero arm
below 560, held 20 both ways, then 660 and 820 re-admitted 24 past the edge), the track table's TIER ladder (300 … 860, re-admitted 24 past each
edge) and the HEIGHT rungs (760 and 900, held 40 on growing) are named sub-ladders of the structure's `TierPolicy` (`TierPolicy.Ladders`, ids
`Wavee.Theming.DetailLadders.Mode` / `.Table` / `.Height`; Classic: `Themes\Classic\Structure\Rules\Tiers.Detail.cs`, `ClassicDetailTiers`). The
frame resolves the mode and the height rung, and the table its tier, through the engine's `UseTier` over the measured page / list width (seeded
from the content region the frame resolved the window tiers on, so the first frame is already in its final arm): a crossing found after mount is
PENDING until the first frame that is not resized and then animates, the arms cross-fade under their own keys (`detail:arm:vertical` /
`two-column` / `head`), and the vertical exit takes the holds of the edges it crosses (the old core ladder jumped to the width's nominal mode and
snapped). A structure that declares no detail ladders gets the shell as it shipped (the widest arm, tier 0, the tall rung). What each WINDOW tier
lays out is the structure's `ThemePolicies.DetailTiers` (`DetailTierPolicy`: per band a `DetailTierLayout` with the table's form, `DetailTableTier`,
the docked insights flag, the rail's resting width where the user never dragged it, the hero action row's extra height, and the rail title's cap and
the description clamp per height rung), read by the core frame and table; Classic's views read the richer `ClassicDetailTier` records (the target
edge, the sort-led Compact bar, the selection tint, the page row's max width). Classic's records: Compact the vertical arm with the C-TrackList
compact row (`ColumnSet.Compact`: no column header but its hairline, the heart trailing, no chevron lane, a 40 "…" shown at rest, the Modern
duration in the subline, 56 / 44 rows with 40 art), 40 x 40 hero actions and list-bar commands with the sort word leading the bar, the selection
bar tinted with the accent, the filter card in the context sheet (`ContextSheets.OpenContent`); Small the 224 rail arm with no Album or Plays lane and
the date only on a quiet list; Standard unchanged; Huge (and Huge 4K) the rail resting at 320, the cozy rows (56 / 44, 40 art), the page row up to
2400 and the insights pane docked as a 360 column beside the list (`DetailInsightsProps.Docked`) on a page with no rail (Liked Songs), resting OPEN
there until the user closes it (`DetailTierLayout.InsightsRestOpen`, `InsightsToggle.Rest`: a sheet over the list still never opens by itself). A
two-column page docks its **aside** at Huge (contract 1.21, `DetailTierLayout.AsideDocked`): the frame host sets `DetailFrameProps.Aside` from the
page's `FrameSlots.Aside` body (or its facts) and provides `Detail.AsideDocked` around the frame, so what the aside holds leaves its other places: the
album's About this release leaves the rail, its artist card, Fans also like and "More by" leave the band under the rows (`Album.SectionsHost`'s aside
arm draws them as the `Album.Aside` view), a playlist's recommended songs leave the list's tail and its insights show in the aside instead of a sheet
or a dock. Classic's frame keeps the aside slot in the row at every tier (width 0 below Huge, `ClassicDetailFrame.AsideSlot`), so a crossing eases
its width and the list yields to it without a re-parent; the list keeps its 8 inset before a hairline (24 over it, 16 each side), then the column:
C-Album-Huge 520 (the 270 artist band, 96 fans four to a row), C-Playlist-Huge 440, C-PlaylistFollowed-Huge 400 (the insights alone, following the
toggle). **Huge 4K** is a rung (`ClassicDetailTiers.HugeWideRung`, window width from 3200, held 40 narrowing) to `ClassicDetailTiers.Huge4K`
(C-Album-Huge4K: the 360 rail with its 336 art, the 640 aside, the 360 band and 120 fans); the core reads it as `DetailTierPolicy.HugeWide`. The
2400 page cap stays at 4K (the plan's rule). The bar's fit slack (`ToolbarPaneInset`) is now
named from the lane it describes (twice the surface's horizontal pad). Tests: `Wavee.Tests\Theming\ClassicDetailTiersTests.cs` (the ladders, their
holds, the board sizes' modes, the records, the rail's Huge rest, the Compact row form's lanes) and `Wavee.ShellTests\TierLayoutTests.Detail.cs`
(Album at every size including 3840, Playlist, Liked, Show and Episode at the page sizes and edges: the record in use, the arm, the header, the dock,
the Compact targets).

**The remaining Compact and Small board items (contract 1.22).** Compact (C-Playlist-Compact, C-Liked-Compact, C-Album-Compact): a LIST's Modern row has no
"#" lane (`DetailTableTier.ListIndex` false; the core sets `ColumnSet.IndexHidden` for a Modern row of any kind but an album, and the width tracks,
the cell count, the relief floor, the drawer's art centre, the rows and the header all drop it; an album's track numbers and the Classic row keep it),
and the list bar's Filter is the funnel alone with its corner badge (`ClassicDetailTier.FilterGlyph`, read by the kit's `FilterCommand` whatever the
bar's fit). The hero arm now carries the playlist's Recommended songs after its rows like the two-column list (`VerticalItemRole.Recommendations`,
`VerticalLayout.ItemCount(visible, recommendations)`: an EMPTY list shows the section alone), and an empty playlist's section is led, where the record
says so (`ClassicDetailTier.Lead`, a `ClassicEmptyLead`: at Compact the 20/28 message over a 40 field, C-Empty-Compact; at Small, Standard and Huge the 28/36 message, 24 over and 8 under, over a 32 field with the blocks 14 apart, C-Empty; the box reserves the record's `Height`, 132 / 148, and eases a crossing), by "Nothing here yet", its line and a field that looks for songs to add
(`PlaylistRecsProps.Lead`, `PlaylistRecsLead.Find`: the core opens Search for the query); the section's refresh and "+" are 40 targets there and its
rows drop the duration. Small (C-Album-Small, C-Playlist-Small, C-Liked-Small): the Sort is its glyph and chevron with the order in the tooltip
(`ITrackCommandKit.SortCommand`, a default member drawing the labelled one; `ClassicDetailTier.SortGlyph`), and the rail takes the tier's
`ClassicDetailRail` (padding 16 / 12, rows 10 apart, the 224 rail keeping its 200 cover, and the Play row breaking after Play: a keyed zero-height break
the width of the column follows the split in `ClassicDetailKit.ActionRow`, loaded and shimmering alike, so the actions shove into their own line on a
crossing). Standard and Huge keep the tuned rail (`ClassicDetailRail.Standard`: `ClassicTiers.RailPadding`, the row model's 14).

**Tests.** The ids, the registration (every key and the hero style, distinct template ids), the equality promises and the shared toggle are
`Wavee.Tests\DetailContractTests.cs`; the arithmetic stays `DetailRailPolicyTests`, `DetailSkeletonGeometryTests`, `DetailInsightsSheetTests`. The mounted
facts are `Wavee.ShellTests\DetailFrameMountedTests.cs`: Classic is the registered template of every key, the real shell draws the two-column arm at a wide
window and the vertical arm under the overlay at a narrow one, the rail's cover fills the column less the side pads with the rows in the row model's
order, the collapsed strip is 96 DIP, and the insights toggle's button mounts through its own key.

### The Liked Songs page (T9c)

The Liked page is the shared detail frame (T9a) plus three pictures of its own, each over a core host: the generated cover, the "Cover style" picker with
its flyout, and the chip rail above the list. `User.LikedPage` keeps what decides (the scope epoch, the demand rules, the identity, the chip memo, the
table profiles, the verbs), and `User.LikedCover`'s hosts keep the snapshot gate (`LikedSnapshot`: the effective style, the newest-first distinct tiles and
their content key, the count stated only once the liked edge is Complete), the demand and the persisted pick. The views are handed values, never the edge.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Liked.Cover` | `LikedCoverProps` (size, radius, morph id, effective style, tiles, tile key, count; equal by scalars and the tile KEY, never the array) | `User.LikedCoverArt` (`Entities\User.Cover.cs`): the snapshot memo and the demand; `LikedCover` / `LikedArtwork` / `LikedChipArt` / `LikedToneAnchorUrl` stay the contract surface | `ClassicLikedCover.CoverTemplate`: the site ladder (`LikedCoverRules.Site`), the nine treatments (`ClassicLikedTreatments`, one 304-DIP authoring canvas scaled to the size), the palette leaves (`ClassicLikedLeaves`: the only nodes that read `Palette.Watch`), the stock PNG and the Wall / Marquee ambient loops (the view owns their hooks: one unconditional order on every path) |
| `Liked.CoverPicker` | `LikedCoverPickerProps` (size, radius, morph id, the core cover element, the flyout builder) | `User.LikedCover(picker: true)` builds the props | `ClassicLikedCover.PickerTemplate`: the cover under an on-media "Cover style" pill whose reveal is a bound opacity (hover, focus or an open flyout); Down / F4 open the flyout |
| `Liked.CoverStyleFlyout` | `LikedCoverStyleFlyoutProps` (tiles, tile key, count, the persisted slot, `Apply`) | `User.LikedCoverStyleFlyout`: the snapshot (read even while the style is Stock), the demand, the persisted pick (idempotent: the strip fires on every rove) | `ClassicLikedCover.FlyoutTemplate`: the eyebrow, the three-wide radio grid of LIVE miniatures (an unfed style shows the stock cover at .45 and stays selectable), the note |
| `Liked.ChipBar` | `LikedChipBarProps` (titles, the evidence prefix, the selected tag, the scroll key, `SelectAll`, `Select`) | `User.LikedChipBar`: the table's live filter (`Track.TableLiveSlot`), the selected tag, the two verbs | `ClassicLikedChipBar.Template`: "All" first, exclusive selection, re-tapping the active chip clears it, an un-evidenced chip disabled as one tail; no titles is a zero-hit placeholder |

**Inks over artwork are the on-media ladder (P21).** The name chip's word and glyph, the lens rim and sheen, the Tone heart and its count are
`R.OnMedia.Ink`, the scrims `R.OnMedia.ScrimRest`: theme-invariant by design (a Rainbow cover is every luminance at once). The plates and washes
(`#131318`, the wall's vignette, Marquee's and Stack's ground, the badge disc and its near-black glyph) are artwork, not ink, so they are imagery
constants in `Rules\ClassicLikedRules.cs`.

**What stays core, and why.** `LikedCoverRules` (styles, tile rule, site ladder, picker order, hue order, minimum tiles), the snapshot and its gate,
`User.LikedToneAnchorUrl` (the page tone follows the newest tile), the chip set (`LikedFactsRules.ChipSet`), the lens header and the facts panel
(`User.LensHeader`, `User.FactsPanel`: the playlist page mounts the same two, so they are T9b's seam), the empty state (`Controls.Vacancy`, shared) and
the table's filter. `Detail.Config.SlimHead` / `HoverHeart` / `PlainRows` (PR 205) are not on this branch, so there was nothing to move.

**Tests.** The ids, the registration and the equality are `Wavee.Tests\LikedContractTests.cs`; the pure rules stay `LikedCoverRulesTests`,
`LikedPageRulesTests`, `LikedFactsRulesTests`. The mounted facts are `Wavee.ShellTests\LikedMountedTests.cs`: Classic is the registered template of every
key, a treatment mounts its canvas keyed by its style (and its leaves), an unfed Wall below the floor is not a treatment, the stock cover has no canvas,
the picker mounts the cover under its pill, and the chip rail stands "All" first in title order.

### The podcast show page (T9c)

The show page is the shared detail frame (T9a) with a rail of podcast slots on the left and the episode reader on the right. Its pictures are ONE style, not views:
the reader is a bound list with a persistent sticky prefix, a reveal region per head and per row, and skeletons that derive their shimmer from a PURE builder (the
skeleton deriver cannot run a view slot), so the core hosts keep every hook and call pure templates while they render, exactly as the list surfaces do (T8c).

| Key (id) | Contract | Core half | Classic |
|---|---|---|---|
| `StyleKeys.ShowReader` (`Show.Reader`) | `ShowReaderStyle`: the rail slots `Primary`, `Ledger`, `Rating`, `Badges`, `Topics`, `Publisher`, `Satellites`; the reader's `Toolbar`, `HeadShell`, `Head`, `EpisodesHeader`, `Group`, `StickyMonth`, `YearStrip`, `YearReserve`, `Foot`, `Seed` | `Show.PageHost` (the frame spec, the demand, the persisted view, the slot sets by presence), the rail slot components (`PrimarySlot`, `LedgerSlot`, `RatingSlot` with the rate flyout's handle, `BadgesSlot`, `TopicsSlot`: tone memo, facts, verbs), `Show.ReaderHost` (the model and its one snapshot, the toolbar stage and its measures, the selection, the date index and sticky month, the analytic extents and `CheckExtent`, the reveal gates), `RailBody`, `VisitHead`, `EpisodesHeader`, `ReaderFoot` (stamps and args), `Show.Pane` (the selection signal, the memo pair, the demand and the reader's key; its picture is the `Library.ShowPane` view, T10c1) | `ClassicShow` (`Views\Show\ClassicShowReader.cs`, `ClassicShowHead.cs`, `ClassicShowRail.cs`) over `Rules\ClassicShowRules.cs` |

- **What stayed core, and why.** The decisions the list, the skeleton and the rows must agree on row for row are core, pure and unit-tested: `ShowReaderRules` (the head, the primary, the
  results mode, the item list with its marks, the head columns, the gutters, the strip's column, the rail / header / month extents and every seed), `ShowToolbarLayout`, `ShowDateIndex`,
  `ShowCadence`, `ListenNext`, the follow write (`Controls.Library`), the trailer, `Show.MonthWordOf` (culture text shared by the month header and the sticky month) and `Show.MetaOf` (the meta
  line's words). The sticky root of the toolbar is a RAW element in the host: a sticky root returned from a component never pins.
- **Skeletons match real heights.** The cold seed (`Seed`: the real filter words, the "episodes" head, eight seed rows) and the head's shimmer (`HeadShell`) are the same builders as the live
  pictures; the extents they reserve are the core's (`ShowReaderRules.SeedExtent`), and `CheckExtent` holds the arranged month and header heights to them.
- **Episodes in the head.** The head's doors, hero and up-next cards sit behind `Episode.Reveal` (card once the identity is there, a shimmer while it comes, "unavailable - retry" on a failed ask), and
  the new-since rows are the episode row style (`StyleKeys.EpisodeRow`) over a fixed scope; both are the core's gate over the structure's pictures.
- **A host without a registry** logs `no style is registered for Show.Reader` once and draws nothing.

**Tests.** The registration and id are `Wavee.Tests\ShowContractTests.cs`; the arithmetic stays `ShowReaderTests`, `ShowReaderShapeTests`, `PodcastShowToolbarTests`. The mounted facts (one-row toolbar,
the reserved year strip, the pinned month, the keyed satellites) are `Wavee.ShellTests\ShowMountedTests.cs`.

Also in the T9a section: "What the pages keep calling" no longer lists `PlayButton` (dropped: a one-line forward to `Controls.PlayButton`) or `RailSatellite*` (moved to
`Views\Detail\ClassicDetailKit.cs`, the shared kit T9b also appends to).

### The episode page, reader and discussion (T9c)

The episode page hands the T9a frame its slots; T9c makes the slots' pictures, the reader and the discussion the structure's. The core hosts keep every
decision and every cell: the page host and its demand effects, the saved / played / resume decisions (`Episode.Invoke`, `StartAt`, `MarkEpisode`,
`Spotify.Podcasts.*`), the scroll spy (anchors, viewport, active section), the chapter lease and the active-chapter decision, the transcript view, the
discussion's signals, resources, composer and reaction verbs, the reaction session and the overlay the flyout opens in, and the pure rules
(`PodcastReaderRules`, `DiscussionLayout`, `PodcastReactionRules.Pill`). A host builds ONE props record per render it makes and mounts the key through
`ViewSlots.View`; a template draws and never names `Spotify`, `Playback`, `Entities` or `Shell` (a comment crosses as `DiscussionComment`).

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Episode.Primary` | `EpisodePrimaryProps` (the quiet line of a blocked episode, or label / playing / play) | `Episode.EpisodePrimary` (Resume vs Play vs Pause, blocked, `Invoke`) | `ClassicEpisode.Primary`: the Play button on the APP accent (never the cover tone) or the quiet line |
| `Episode.Attribution` | `EpisodeAttributionProps` (the escaped anchor, the link verb) | `Episode.EpisodeAttribution` | the inline rich-text link row |
| `Episode.Actions`, `Episode.Save` | `EpisodeActionsProps`, `EpisodeSaveProps` | `EpisodeActions` (mark played, copy link, the menu), `SaveEpisode` (the saved-state machine) | the rail satellites (`ClassicDetailKit.RailSatellite*`) |
| `Episode.RailAbout` | `EpisodeRailAboutProps` | `EpisodeRailAbout` (nothing until the description answered) | the label, the divider and the 4-line clamp |
| `Episode.Reader` | `EpisodeReaderProps` (clicks, the active signal, anchors, the four section bodies, the scroll key and handle) | `Episode.EpisodeReader` (spy, anchors, `GoToSection`) | `ClassicEpisodeReader.ReaderTemplate`: the pivot band over the scroll of stacked sections |
| `Episode.About`, `Episode.Chapters` | `EpisodeAboutProps` (a `Loadable<string>` and its loading text), `EpisodeChaptersProps` | `AboutProps`, `ChapterReader` (lease, retry, empty, active index) | the description region with its seek list; the chapter rows |
| `Episode.Discussion`, `.CommentCard`, `.CommentSeed`, `.LoadMore` | `DiscussionThreadProps`, `CommentCardProps`, `CommentSeedProps`, `LoadMoreProps` | `PodcastReaderUI.Discussion` / `CommentPage` / `CommentCard` / `MoreComments` | `ClassicDiscussion`: the avatar / content grid ("one left edge"), the reaction pill, the replies disclosure and rail, the composer, the quiet lines |
| `Episode.ReactionFlyout`, `.ReactionRoster`, `.ReactionPeople` | `ReactionFlyoutProps`, `ReactionRosterProps`, `ReactionPeopleProps` | `ReactionFlyout`, `ReactionPage`, `MoreReactions` | the palette, the filter chips over the scrolling people |
| `Episode.Rating` | `ShowRatingProps` | `PodcastReaderUI.RatingEditor` (embedded by the show page's rating slot) | title, stars, error |

**Loading and failure stay the host's.** The seed is the live card's frame (`Episode.CommentSeed`), the thread, the roster and the chapters sit inside the
hosts' `Skel.Region`, `Failed` + retry and `Quiet` stay the shared core helpers (the lyrics transcript and the saved-episodes page build the same lines).
**Numbers** are `Themes\Classic\Structure\Rules\ClassicEpisodeRules.cs`.

**Tests.** The ids and Classic's registration are `Wavee.Tests\EpisodeContractTests.cs`; the pure rules stay `DiscussionLayoutTests` and
`PodcastReactionTests`. The mounted facts (registered templates, the thread's order, the reply rail only when handed a thread, the reader's anchors and
section order, the chapter rows) are `Wavee.ShellTests\EpisodeMountedTests.cs`.

### The album and playlist sub-contracts (T9b)

The album page and the playlist page hand the frame slots (the attribution, the prerelease card, the release facts, the trailing band; the cover, the inline
title and description editors, the owner block, the daylist strip, the recommendations section) and open their own panels (the every-artist flyout, the
collaborator flyout, the invite and access panel, the Tune panel). Each of those pictures has a key (`Theming\Contracts\ViewKeys.DetailAlbumPlaylist.cs`, props in
`DetailAlbumProps.cs` and `DetailPlaylistProps.cs`); the page's hosts keep every gate, state and verb and mount the key with the plain data the picture shows.

| Key | Core host | Classic template |
|---|---|---|
| `Detail.Notice`, `Detail.VerticalTrailing` | `Detail.NoticeBar` (the frame host and the library album pane), `FrameHost.VerticalTrailing` | `ClassicDetailSeam`: the Informational strip (zero-height while there is nothing to say), the prerelease card / release facts / trailing band in the rail's keyed rows |
| `Album.ArtistLine`, `Album.ArtistFlyout` | `Album.FacePileHost` (`[CoreHost]`): the artist / track / edge gate, the portrait demand, the width mode's hysteresis, the open handle | `ClassicAlbumArtist`: the shared kit's line (Wide pile, Medium lead face and "+N", Narrow names; the chevron follows a list-opening click), the 44-DIP flyout rows |
| `Album.Countdown`, `Album.ReleasePanel`, `Album.Unresolved` | `Album.CountdownHost` (`[CoreHost]`; the clock: one wall-clock sample, then the frame clock's delta), `ReleasePanelHost` (`[CoreHost]`: the stamp, the facts), the page | `ClassicAlbumRelease`: the ring card or its bare body, About this release (facts, ℗ / © notes, Other versions), the unresolvable line |
| `Album.Trailing`, `Album.Sections`, `Album.TrailStack` | `TrailingHost` (demand, the pending gate with its one swap, the deadline), `SectionsHost` (which sections are present, the video counterparts' demand), `StackHost` (the expand cell) — all `[CoreHost]` | `ClassicAlbumTrailing`: the one skeleton region and its section skeletons, the video hero / shelf, About, Fans also like, the capped stacks and their rows |
| `Playlist.CoverArt`, `Playlist.CoverEditor` | `Playlist.CoverArt` (which tiles: the server mosaic, the art, four member covers), `CoverEditor` (`[CoreHost]`: the decode bucket, pick / drop / busy verbs) | `ClassicPlaylist` cover: the saturation-1.18 face, the hover / file-drag / saving scrim |
| `Playlist.Editor` | `InlineEditor` (`[CoreHost]`): the edit mode, the draft, the status 0 / 1 / 2 and its 1.8 s hold, `Begin` / `Commit`, "a create opens in edit mode" | `ClassicPlaylist` editor: the read arm and its pencil, the edit arm (focus without a cue, caret at the end) over Save / Cancel, the status row |
| `Playlist.Owner`, `Playlist.MembersFlyout` | `OwnerBlock` (`[CoreHost]`): the gate, the collaborator set, `IsOwner` / `CanAdministratePermissions` / `Live`, the two popups' anchors | `ClassicPlaylist` owner: the pile or the owner run in its reserved row, the invite pill (round under 260 DIP), the member rows |
| `Playlist.Access`, `Playlist.Tune` | `AccessPanel` (`[CoreHost]`: `OptimisticSwitch`, the two cells, the verbs), `OpenTune` | `ClassicPlaylist` panels: the copy-invite call to action and the two switch rows, the Tune choices and reset row |
| `Playlist.Daylist` | `DaylistHost` (`[CoreHost]`): the wall-clock anchor re-anchored on `DaylistCountdown`'s cadence, the interval and expiry timer, the clock-mode decisions | `ClassicPlaylist` daylist: the flip cells, the Rolling ring, the next-update caption |
| `Playlist.Recs`, `Playlist.RecRow` | `RecsSection` (`[CoreHost]`): the edge's demand and refill, the adding state | `ClassicPlaylist` recs: the header row, one slot per row on the shared media surface |

**The shared Classic detail kit** (`Themes\Classic\Structure\Views\Detail\ClassicDetailKit.cs`) holds the pieces the frame's views, the album and the other lanes
share: the keyed rail rows (`Row`: a position-only FLIP, `LateRow`: it also fades up on arrival, `ReleaseRow`), the eyebrow run and the billed-artist line
box. The lanes only ADD to it. `Detail.RailArtistLine` (the width ladder with its hysteresis, the reserved heights, the "+N" arithmetic) is the core's rule.

**Records and keys.** A record that carries closures a host rebuilds per render, or a list the host mutates in place, compares by reference (the host is
the gate: a stamp that did not move never re-renders it); `PlaylistRecRowProps` is plain data over the section's two stable handlers, so an untouched row
re-renders nothing. Writable cells travel as `Func<Signal<T>>` (the draft, the two switches). The hover cells of the pencil and of the cover's scrim, and the
file-drag read, are the pictures' own (`ctx.Hooks`). Keys keep the old tree (`pl-read`, `pl-edit`, `pl-field:<size>`, `pl-status-row`, `release-panel`,
`trail:<signature>`, `video:<slot>`, `rec:<slot>` ...). The popups' measures (`Playlist.AccessPanelWidth`, `TunePanelWidth`) belong to the hosts that open
the popup and the dialog, and travel on the props.

**The two clock hosts are `[CoreHost]`.** `Album.CountdownHost` and `Playlist.DaylistHost` read the frame clock, which was `Design.FrameTime` until the
leftovers lane P3 and kept them off WVTH008 (the rule fences every `Design.*` read). The clock is a platform fact, not a token: it is `FrameTime`
(`Platform\FrameTime.cs`) now, the same clock every model and per-frame driver reads, and both hosts are guarded like the rest.

**Tests.** The ids, the registration, the equality promises and the numbers the pictures own against the reserves the core's skeleton states
(`ClassicAlbumLayout` against `Album.PageRules.Skel*`, the reserved owner row, the invite pill's arm) are `Wavee.Tests\AlbumPlaylistContractTests.cs`; the rules stay
`AlbumPageRulesTests`, `AlbumSurfaceRulesTests`, `PlaylistHeaderRulesTests`, `PlaylistTuneMenuModelTests`, `Daylist*Tests`. The mounted facts are
`Wavee.ShellTests\AlbumPlaylistMountedTests.cs`: the billed-artist line's three modes and its chevron, the notice strip, About this release (facts, outer padding,
nothing without a fact), the keyed rows of a stack, the one trailing region, the video hero and the list sections, the vertical arm's trailing rows, the editor's
read / edit / status states and the keyed field, the copy-invite call to action, the flip cells while counting, and a smoke mount of the views with no keyed output.

### The overlays and the dialog bodies (T11a)

| Key | Props | Core half | Classic template |
|---|---|---|---|
| `Overlay.ProfileChip` | `ProfileChipProps` (identity, the named form, presentation mode, the name cap, `Open`, `Realized`) | `Shell.ProfileChipCore` (`[CoreHost]`, `Shell\Shell.Overlays.UI.cs`): the overlay handle (toggle, light dismiss, focus trap), the anchor capture, the chrome ladder's flags | `ClassicProfileChip`: the avatar (a skeleton until the identity is known) and, in the named form, the name beside it |
| `Overlay.ProfileMenu` | `ProfileMenuProps` (identity, tier facts, the decided `MenuFlyoutItem` rows, `Close`) | `ProfileMenuContent`: which rows exist (`Actions.ProfileRules.Rows`), their labels, "close first, then act", the Play cascade | `ClassicProfileMenu`: the header (avatar, name, tier line in the premium ink), the divider, the menu |
| `Overlay.Notifications` | `NotificationsProps` (the rows that pass the filter, `Now`, the expanded id, the empty line, `Clear`, `CanUndo`, the progress and pending-sync signals, `INotificationsHost`) | `Shell.NotificationPanelView` (`[CoreHost]`, `Shell\Shell.UI.cs`) over the feed, the filter, the 30 s tick and `NotificationsHost` (filter, read marks, undo, the activity target, the release menu); the popup is `Shell.OpenNotificationPanel`, the verbs `INotificationVerbs` | `ClassicNotifications`: header, filter pills, pending-sync line, the update / social / release / activity rows with their enter, exit and slide choreography |
| `Overlay.RuntimeBanner` | `RuntimeBannerProps` (`Show`, the issue's text, `Dismiss`, `SetUp`) | `Shell.RuntimeBannerChrome` (`[CoreHost]`): the gate (`OverlayGates.ShowsRuntimeBanner`), the setup-request watcher, the one-card-at-a-time rule; the lane's top pad stays core | `ClassicRuntimeBanner`: the width cap, the opaque base and elevation under the warning InfoBar |
| `Overlay.Toast` | `ToastCardProps` (title, a bound `FloatSignal`) | `UpdateToastWatcher` and `UpdateToastModel` (what a toast is and when it is planned); `Notify.Say` is the one door | `ClassicToast`: the update toast's download card |
| `Overlay.OfflineStrip` | `OfflineStripProps` (message, `OnRetry`) | `Shell.OfflineLane` hands the content state's lane its builder; `FrameRules.ShowsOfflineStrip` decides; the lane stands in the flow above the page (1.8) | `ClassicOfflineStrip` |
| `Overlay.OfflineVacancy` | `OfflineVacancyProps` (the page's name, `OnRetry`, `GoToLibrary`) | `Controls.Vacancy`: a page-scale Error vacancy mounts it while `FrameRules.VoiceFor` says Offline (1.8) | `ClassicOfflineVacancy` |
| `Menu.Context` | `ContextMenuTileProps` (art url, circular) | `Actions.Menu.Header` | `ClassicContextMenuTile`: the 38 DIP tile (a rounded square, or a circle for an artist) |
| `Dialog.LogoutConfirm` | `LogoutConfirmProps` (`Cancel`, `Confirm`) | `Shell.ConfirmLogout`: the modal popup, what the buttons do | `ClassicLogoutConfirm` on `ClassicModalCard` |
| `Dialog.PlayLink` | `PlayLinkProps` (placeholder, the text cell, the status, `CanPlay`, the browser escape, the verbs) | `Shell.PlayLinkCardCore` (`[CoreHost]`): the field's text, the lookup, the status, the failed input, the failure toast | `ClassicPlayLink` on `ClassicModalCard` |
| `Dialog.Signature` | `SignatureSheetProps` (summary, trusted, resolved label and value rows) | `Shell.OpenSignatureDialog`: the engine ContentDialog (548, Close the one command) and every string | `ClassicSignature` |
| `Dialog.FolderPicker` | `DestinationPickerProps` (guidance, placeholder, empty line, the filtered rows, the query cell, `Commit`) | `Actions.DestinationPicker` (`[CoreHost]`): the live filter and the match rule (`PickRules`, a pinned row is never filtered away); `Actions.OpenPicker` opens the dialog | `ClassicDestinationPicker`: guidance, the field, the rows (plated glyph, cover or glyph, label over subtitle, indent per level) or the empty line |
| `Dialog.Sheet` | `CreditsSheetProps` (the three states, the ordered entries with their group headings, `OpenArtist`) | `Track.OpenCredits` (the sheet: `Controls.Sheet`) and `Track.CreditsHost` (`[CoreHost]`: the demand, the grouping and the order) | `ClassicCreditsSheet`: bars while unanswered, grouped contributors in a bounded scroll, the empty line |

**The shells stay the engine's.** The toast queue (`ToastHost`: the cap and the stacking, enter and exit motion, the auto-dismiss clock, hover-to-pause, the
action buttons, the dedupe key), the context menu and the menu presenter (`ContextMenu`, `MenuFlyout`) and the dialog shell (`ContentDialog`: the card, the command
row and its button order, the default button, Enter and Escape, the focus trap and its return, the open and close motion) are engine controls; the core configures
them and the structure draws only what the app puts in them. `Dialog.LogoutConfirm` and `Dialog.PlayLink` are the two bare modal cards (`PopupChrome.Modal`, no light
dismiss). Their presenter parts (the card, the verb strip, the button order) wait for the engine's control style table (E14).

**What has no key, and why.** Teaching tips: the app-level subsystem (`TipService`, `Shell.Tips`) was deleted as dead code in an earlier cleanup, so there is
nothing to move (the stage's one first-visit tip is an engine `TeachingTip` configured with two strings, in the stage host). The destructive confirm, the prompt (rename a
playlist or a folder) and "reset everything" are `ContentDialog` configurations (`Controls.Confirm`, `Prompt`, `Sheet`; `SidebarMenus.ConfirmResetEverything`) with no
element tree of the app's own: the destructive verb on the left, Cancel on the right as the default, the width ladder (320 / 480 / 548) and busy states are the shell's and
the opener's. `Dialog.Feedback` and `Dialog.Crash` are T11c's, the setup and release-notes dialogs T11b's and T11c's.

**Tests.** The ids, the registration and the header builder are `Wavee.Tests\OverlayContractTests.cs`; the decisions stay `OverlayGatesTests`, `NotifyPanelRulesTests`,
`PlayLinkRulesTests`, `ActionMenuRulesTests` and `TrackDrawerRulesTests`. The mounted facts are `Wavee.ShellTests\OverlaysMountedTests.cs`: the chip is one button and
reports its node, one menu row per decided row, a card per notification under its key and the expanded wrapper, the banner's buttons only while shown, the offline
strip's retry, the two cards' buttons, the picker's keyed rows and the credits and signature sheets.

### The stage, lyrics, video and queue at every tier (P3t, TC3)

The fullscreen stage fills the window, so it reads the WINDOW tier; its own layout classes stay the allocator's, but their edges are three engine
`TierLadder`s the structure declares (`StagePolicy.Aspect`, contract 1.13; Classic's in `Rules\Tiers.Stage.cs`, `ClassicStageTiers`): **width** (Compact
under 600, no hold), **height** (a landscape stage at or below 460 tall is Compact, held to 484) and **ratio** (Portrait at or below 0.80, held to 0.85;
Ultrawide from 2.00, held while above 1.95). The lower bands are inclusive, so `AspectThresholds.AtOrBelow` / `.From` set each edge one float above the
board value and the holds land exactly on the leave values. `Stage.Layout.ClassOf` composes them: a tall window is Portrait at every width (the
400 x 860 boards draw a column with its pane), a narrow one Compact, a short landscape one Compact, the ratio splits the rest; each ladder resolves
against the band the previous class sat in.

Beside the ladders the policy carries **per-window-tier metrics** (`StageTierMetrics`, `StagePolicy.MetricsFor(tiers)`), which the core surface hands
`Stage.Layout.Resolve`: the wide classes' transport card (Compact 104, Small 96, Standard and Huge 112), its gutter (16 on a Compact or Small
window, 24 above) and the lyrics pane's type range (Compact 26, Small 30-36, Standard 36-64, Huge 36-48: C-Lyrics-Compact, -Small, -Huge,
C-Stage-Huge4K). The allocator decides two fits from them: `TransportStacked` (the card cannot seat its three clusters: it stacks to heart · previous
· play · next · "…", C-Stage-Compact) and, on the Compact class, `TransportUnder` (the strip beside the art cannot hold even that row: the card
parks under the art at full width). `CaptionFits(mode)` drops the Lyrics-mode caption on a row-identity stage too short to clear the title
(330 tall keeps the art, the title and the controls). Portrait's pane starts under its identity row (the art or a two-line title column).

**The regions a tier adds (contract 1.22).** `StageTierMetrics` also says what a tier draws that the others do not, and the allocator answers it:

| Tier (board) | Metric | What the allocator answers | Classic draws |
|---|---|---|---|
| Compact (C-Stage-Compact, C-Lyrics-Compact) | `IdentityCard` | `IdentityCardOn(mode)` (Visualizer mode, and every mode on a Portrait stage): the cover is 56 at the card's 8 padding, the titles 16/22 over 13/18 beside it, no chips; `NowPlayingCardRect(mode)` is the card across the stage at the gutter under the top bar (16, 56, W-32, 72); Portrait's pane starts 8 under it (136); the face chip sits 12 under it (`FaceChipShown`, `FaceChipY`, 40 tall) and the face's safe rect starts under the chip | `ClassicStageNowPlayingCard` at the card rect (it follows a crossing on the cover's spring), the identity at the card's geometry, `ClassicStageFaceChip` (the face's name, the door to the gallery) under the chrome's Show |
| Compact | `GalleryOnDemand` | `GalleryOpenFor(stored, asked)`: the sheet shows only once it was asked for during the stage's life (`StageCtx.GalleryAsked`: the chip, G, the options footer), never from the stored flag alone; the verbs decide on the gallery as shown (`StageModel.StateOf`), so the first press opens it | nothing of its own: the root's gallery Show reads `GalleryShown` |
| Huge (C-Stage-Huge) | `GalleryToFoot` | the docked gallery runs to the foot (`GalleryH`, `GalleryBottomInset`: 24) and the transport card ends a gutter short of the face's inset while it is open (`TransportRight(true)`: 516 on 2560) | `ClassicStageTransportPlace` places the card (its own component: a gallery toggle re-renders the placement only) and slides its width on the face's 550 ms re-centre tween; the gallery's foot follows a crossing on the same tween |
| Huge 4K (C-Stage-Huge4K), from `StagePolicy.HugeWideRung` (Classic: the detail family's 3200 mark) | `UpNextW` (520), `ArtBarsH` (140), read through `StagePolicy.HugeWide` / `IsHugeWide` | Lyrics mode: `UpNextShown` / `UpNextRect` (right 24, top 64, down to a gutter above the card), the lyrics pane narrowed beside it (`LyricsPaneW`, `PaneRect`); `ArtBarsRect` under the cover's two-line title budget, the cover's width; the clock raises its level lease to the spectrum while they show (`Visualizer.Demand.WithArtBars`, through the narrowed `StageCtx.ArtBars`) | the Up next column (the core's `StageLayers.UpNext` builder: a second stage-queue host with `QueueStagePaneProps.Aside`, the 1x rows across its own frame) in an acrylic frame sliding in from the right; the bars in the spectrum layer (`artbars:` key); the pane region's width follows on its clip-revealed spring |

The transport card's padding and gaps are the view record's (`ClassicStageLayout.TransportPadding`, `ClusterGap`, `RightGap`): the tuned 20 / 10 card with
the theme's S and 6 below Huge, C-Stage-Huge's 24 / 14 card with 12 between the centred buttons and 8 in the right cluster at Huge. The video body's title row
carries the heart and the playing track's "…" at Huge (`ClassicPanelContentLayout.VideoTitleActions`, C-Video-Huge; the core hands `PanelProps.VideoTitle`:
`PanelVideoTitleFacts` and the now-playing menu).

The views read `ClassicStageTiers.StageOf(ctx.Tier())` (`ClassicStageLayout`: the top bar's 40 targets at Compact, the play disc's ceiling, Small's
48 disc and 88 volume slider) and the panel views read `ClassicStageTiers.PanelOf(...)` (`ClassicPanelContentLayout`): the queue, lyrics and
video views are fluid at every panel width the frame gives them (200-500), and on a Compact window every target is at least 40 x 40 (the queue's
hearts, remove buttons, toggles, "Clear", whose header grows with the lane's slot maths, and "Playing from"; the lyrics' Resync pill; the video
chrome's 40 buttons on a 40 strip, shown at rest, with full screen on the mini player, C-Video-Compact). Tests: `Wavee.Tests\Theming\StageTiersTests`
(the ladders, their holds, the boards' sizes in the boards' classes, the metrics, the 330-tall geometry, the records, the Huge gallery and card, the
4K column and bars and their lease, the Compact card, chip and gallery-on-request) and `Wavee.ShellTests\TierLayoutTests.Stage.cs` (the real stage at every
plan 3.5 size and edge, 3840 x 2160 included, with the Compact card and the 4K column and bars; the Compact visualizer's card, chip and absent sheet; the
queue panel; the mini player).

### Overlays, dialogs, toasts and states at every tier (P3t, TC9)

The engine owns the shells (the dialog card, the toast lane, the menu presenter) and the core opens them, so a structure decides their size and place per window band
through **one policy**: `ThemePolicies.Overlays` (`Theming\Policies\OverlayPolicy.cs`, contract 1.11), an `OverlayLayout` for Compact, for the bands between and for Huge
(`For(WidthTier)`; a ladder that moves an edge moves no layout). Classic builds it from `Rules\Tiers.Overlays.cs` (`ClassicOverlayTiers`: `Compact`, `Standard`, `Huge`, each a
`ClassicOverlayTier` that carries the core `OverlayLayout` and the numbers only Classic's own views read). A view reads `ClassicOverlayTiers.For(ctx.Tier().Window)` and passes
the live viewport to the record's arithmetic (`CardWidth`, `PanelWidth`, `PanelHeight`, `PanelBodyHeight`); it compares no width (WVTH013).

| Surface | Compact (the boards) | Small, Standard, Huge | Core reads / view reads |
|---|---|---|---|
| ContentDialog card (`OverlayFit.Dialog`, applied at every opener) | the window less 16 either side, 16 / 20 text padding, 8 / 16 command padding, never taller than the window less 16 | the opener's rung (320 / 480 / 548), never wider than the window less 16 and never over 548 (nothing stretches at 2560 or 3840), never taller than the window less 24 | `OverlayLayout.Dialog` (`DialogFit`) |
| Bare cards (log-out, play-a-link) | fill the window less 16, 16 / 20 padding, two 40-tall buttons sharing the row | 380 / 420 wide, 24 padding, right-aligned stock buttons | `ClassicModalCard` |
| Dialog bodies (picker, credits, signature) | the card's width less its padding; a body taller than the card scrolls inside it (`DialogFit.BodyMaxHeight`) | the tuned panel width | `ClassicDestinationPicker`, `ClassicCreditsSheet`, `ClassicSignature` |
| Context menu | a bottom sheet (`Overlay.ContextSheet`): grabber, identity header, a four-column quick-action strip of 64-tall cells, 44-tall rows, a sub-menu page in place, scrim from the 48 DIP chrome row down | the engine's anchored menu | `ContextSheets` decides (`OverlayLayout.ContextSheet`); `ClassicContextSheet` draws |
| ContentDialog commands | the engine's buttons at `ControlSize.Large` (40), two equal columns from a 96 floor (`StacksCommands` stacks three below a 336 card) | the engine's 32-tall, 130-wide WinUI buttons | `DialogFit.CommandSize` / `CommandMinWidth` / `UniformCommands` → `ContentDialog.CommandSize` / `CommandMinWidth` / `UniformCommandWidths` |
| Anchored menus (sort, filter, "...") | 40-tall rows | the engine's 36 | `OverlayLayout.MenuSize`, passed as `MenuFlyout.Create(…, size: OverlayFit.MenuSize)` at every opener |
| Toast lane | bottom centre, 8 above the 56 mini bar, the window less 8 either side, a 40-tall action (`ToastDock.Lane`) | bottom right, 24 above the bar, 300-380 cards (as shipped) | `OverlayLayout.Toast`, docked by the overlay lane (`OverlayFit.Toasts`); the lane geometry is the engine's `Toast.Lane` context, provided above the overlay host (`OverlayFit.ToastLane`) |
| Vacancy (empty, error, no match) | the 20 / 28 headline at page scale too, 16 around, a 40-tall action | the page hero, 24 around | `OverlayLayout.Vacancy`, read by `Controls.Vacancy` |
| Offline strip / vacancy | 40 tall edge to edge with a 40-tall Retry; the vacancy is the quiet rung over a stacked column of 40-tall actions | the 36-tall r4 strip; the page-hero vacancy with its action row | `ClassicOfflineStrip`, `ClassicOfflineVacancy` |
| Standalone selection bar | at least 48 tall, 8 from the sides, 4 padding | as shipped | `OverlayLayout.Selection`, read by `Controls.SelectionBar` |
| Panels (notifications, account menu, runtime banner) | never wider than the window less 8 | less 16; the nominal 380 / 304 / 560 | `ClassicOverlayTier.PanelWidth`, `PanelHeight` |

**Short windows.** A window under 680 tall (the page is 210 at 330) clamps everything that is taller than it: a dialog card to the window less its margin (its body scrolls), a
panel to the window less the 48 DIP chrome row and its margin (its feed scrolls), a sheet to the window less the chrome row (its rows scroll), and the offline vacancy gives its
glyph and its 120 lift up so the words and the actions fit (`VacancyShowsGlyph`, `VacancyLiftAt`).

**Context sheets.** `ContextSheets.Attach` composes onto an element exactly as the engine's `ContextMenu.Attach` does, and decides **when the request arrives**: where the active
policy's `ContextSheet` is true for the band on screen it opens `Overlay.ContextSheet` over the whole window (`PopupChrome.Raw`, `FlyoutPlacement.Full`, light dismiss), anywhere else it
delegates to the engine's attach unchanged. The model is the same the stock menu would have shown, so no verb, row or order changes. `ContextSheetExtensions.WithContextMenu`
shadows the engine's extension of the same shape for every file in `namespace Wavee.*` (an extension declared in an enclosing namespace wins over one a `using` imports), so
`row.WithContextMenu(overlay, factory)` needs no edit; direct `ContextMenu.Attach(...)` calls say `ContextSheets.Attach(...)`. Menus that are not context menus (the sort and filter
dropdowns, the sidebar's menus, a flyout card) are anchored popups at every band.

**The engine's size knobs.** The stock controls take the Compact target through their own idiom (`ControlSize.Large` on the dialog's commands, the ComboBox, the menu rows, the
breadcrumb and the toast's action; `NumberBoxOptions.Style.FieldHeight`; `ToastLaneStyle` for the lane; `TitleBarSpacing` for the title bar's spacers), so no structure builds a copy
of a stock control to make it 40 tall. The dialog's `Parts` still carry the width, the heights and the paddings (`OverlayFit.Dialog`).

**One sheet drag (the Compact overlays fix).** Every Classic bottom sheet, the context sheet here and the Compact panel sheet (TC2), takes the same drag, `ClassicSheetDrag`
(`Views\Overlays\ClassicSheetDrag.cs`, numbers and decisions in `ClassicSheetRules`: `Rubber`, `Dismisses`, `ScrimShare`): the head (the grabber and the row under it) shows the
vertical-resize cursor, the sheet follows the pointer 1:1 with the scrim's dim fading by the share still on screen, it rubber-bands above its rest (up to the sheet's reach), and a
release past 160 DIP or 30 % of its height, or a downward fling of 900 DIP/s, slides it out and closes it; short of that it springs back carrying the release speed. A tap moves nothing,
a press mid-slide catches it, and mouse, pen and touch drive the same `OnDrag` capture. The context sheet's drag moves the card's carrier (the card's entry and exit compose with it); the
card and the scrim are no tab stops and keep the wheel off the page (`BlocksBackgroundScroll`).

**Tests.** `Wavee.Tests\ClassicOverlayTiersTests.cs` pins the records, the band mapping and the arithmetic. `Wavee.ShellTests\ClassicOverlayTierTests.cs` mounts each surface at 400 x 860,
300 x 330, 1440 x 960 and 2560 x 1440: inside the window, Compact targets at least 40, the card's width per band, a sheet's rows and strip, a sub-menu page, and an engine dialog fitted by
`OverlayFit.Dialog` keeping its command row in the window.

### The Settings page (T11b)

Settings is the structure's picture over the core's document (`Themes\Classic\Structure\Views\Screens\ClassicSettings*.cs`). `SettingsDoc` (tabs, sections, rows: kind, title, value, writer, enablement and its reason, visibility, glyph name,
confirmation), every verb (`row.Writer`: persistence, live apply, DSP pushes, OS registrations, file I/O off the render path), the facts the rows ask (`SettingsFacts`), the page epoch (`Settings.Bump`), the Storage census, the pivot's
root-lifetime cell with the deep link (`Settings.Open`, `TabFromSlug`), the page context the verbs read (`SettingsContext`) and the legacy-row binding (`IStructureLayer.LegacyRows`, left for the M-PRs) stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.Settings` | `SettingsPageProps` (the visible tab, the `Revision` that carries the page epoch and the two foreign epochs the Appearance tab reads, the pivot's cell as a `Func<Signal<int>>`, and the Storage census as `SettingsStorageProps` while that tab is visible) | `Settings.PageHost` (`[CoreHost]`, `Screens\Settings.Page.cs`): the context bind, the census trigger and the notices warm read | `ClassicSettings` (`.cs` the template, the pivot, the row grammar and the glyphs; `.General`, `.Appearance`, `.Playback`, `.VideoOverrides`, `.Storage`, `.Privacy`, `.About`, `.AiLyrics`): the masthead, the seven-tab pivot over a per-tab keyed scroll lane, every tab's sections and rows |
| `Settings.ThemePicker` | `SettingsThemePickerProps` (none) | the Appearance tab, the setup wizard's Theme step | the fixed-size card per `ThemePickerEntry` |
| `Settings.ThemeOptions` | `SettingsThemeOptionsProps` (`Wizard`) | the Appearance tab (the expander), the wizard (the plate rows, placed only when `SettingsThemeOptions.HasWizardOptions`) | the groups, the generated rows by kind (Classic draws its `structure.sidebar` row as the two layout cards with their miniatures, which refuse while the sidebar is edited), the legacy rows by document id |

**What a Settings view may call.** The WVTH002 fence applies. A row's value is `row.Current` (the live store read through the document), not a settings key; what no row carries is a typed fact on `SettingsFacts` (`EqualizerGainsRaw`,
`CrossfadeSeconds`, `AudioBudgetBytes`, `CrashInstallId`, `UpdateLastCheckedMs`, `SidebarMigrationDropped`, `AiLyricsEpoch`, `VideoOverrideTitle` / `VideoOverrideArtistLine`); the About receipts are one `Settings.SampleReceipts()` per tick.
The active theme's options arrive grouped by `SettingsThemeOptions.Current(wizard)` (legacy rows first, then the generated ones, a group per label; developer rows absent; the sidebar's two notes ride the sidebar rows), so the core
decides what is shown and the view only draws it. The numbers are `Rules\ClassicSettingsRules.cs`; the page's tiers are `Rules\Tiers.Settings.cs` (see "Screens at every tier", below) and nothing is compared with a window width (WVTH013).

**Controls that freeze their inputs stay what they were.** A ComboBox, Slider or NumberBox binds one signal for its life, so each is seeded from the document when the page mounts (the template's mount effect) and re-synced by the child
component that owns it; the video-override flyout and its anchor are root-lifetime state torn down when the Playback tab is left or the route is parked. The tab body swaps with the page cross-fade (Classic tuning T2, Q38: the lane `settings:tab:<slug>` carries the neutral page swap) and the scroller is keyed, per tab
(`settings:scroll:<slug>`), so each tab keeps its own offset. The keys the mounted tests read (`settings:theme:picker`, `settings:theme:card:<id>`, `settings:theme:options`, `opt:group:<title>`, `legacy:<row>`, `appearance.zoom:<percent>`,
`appearance.ai-lyrics`) are unchanged. A row deep link with scroll-into-view is not production behaviour today and is not part of this move.

**Tests.** The ids, Classic's registration, the props' equality, `row.Current` and the option grouping are `Wavee.Tests\SettingsPageContractTests.cs` (the document itself stays `SettingsDocTests`); the mounted facts (a tab opens in
its own keyed lane, the deep link, and the three shared surfaces mounted through their keys) are `Wavee.ShellTests\SettingsMountedTests.cs`, next to `ThemeOptionsMountedTests` (the Appearance tab and the wizard step).

### The setup screens (T11c)

The first-run wizard, the sign-in door and the playback runtime card are the structure's picture over core models. The plate with its footer and Back button, the four pages (Terms, Sign in, the Theme step, Local playback), the sign-in door's dialog (body and footer) and the runtime card's dialog (body and command row) are registered Classic views (`Themes\Classic\Structure\Views\Screens\ClassicSetup*.cs`, one `ClassicSetup.Register(views)`); the boot decision, the open funnel and the overlay handle, `Setup.WizardModel` (page, direction, the footer row, the verbs), `Setup.RuntimeModel` and its provisioning flows, the sign-in flows, the Lottie hero loader, the crash-consent write and the QR encoder stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Setup.Wizard` | `SetupWizardProps` (the `WizardModel`, and the four pages' props) | `Setup.WizardPlate` (`[CoreHost]`, `Screens\Setup.UI.cs`): the overlay's content; keeps the one non-picture effect (the runtime banner stands down on the Local playback page) | `ClassicSetup.WizardTemplate` (`ClassicSetup.cs`): the plate (762 x 490, r8, stroke, dialog elevation), Enter / Backspace chords, the keep-alive page stack (3 entries, nav recipe by direction, no replay on activate), the footer (progress column, primary left, secondary right), Back over the content's corner, the page frame (icon column with the recoloured Lottie hero from the Standard tier, title, scrolling body) |
| `Setup.Terms` | `SetupTermsProps` (session, privacy verb, `SetupConsent`) | the plate's props | `ClassicSetup.TermsTemplate`; the opt-in crash-reporting card |
| `Setup.SignIn` | `SetupSignInPageProps` (session, `ISetupSignInHost`) | `Setup.SignInHost` (`Screens\Setup.SignInHost.cs`) | `ClassicSetup.SignInPageTemplate`: the browser / pairing cards, the Busy ladder, "Is this you?", the UIA announcements |
| `Setup.Theme` | `SetupThemeProps` (the settings' picker and options builders) | the plate's props (`Setup.ThemeProps`, `Screens\Setup.UI.Theme.cs`); the wizard's second page, `WizardPage.Theme` | `ClassicSetup.ThemeTemplate`: the Layout page's frame |
| `Setup.LocalPlayback` | `SetupLocalPlaybackProps` (session, signature-sheet verb, reveal-folder verb) | the plate's props | `ClassicSetup.LocalPlaybackTemplate`: the runtime model's phases on the plate |
| `Setup.SignInDoor` | `SetupSignInDoorProps` (`SetupDialogPart` Body / Footer, `ISetupSignInHost`) | `Setup.SignInDoorView` (the zero-size watcher) and `OpenSignIn` (the ContentDialog) | `ClassicSetup.SignInDoorTemplate` |
| `Setup.RuntimeCard` | `SetupRuntimeCardProps` (part, signature-sheet verb, "has a diagnostics page") | `Setup.OpenRuntimeCard` (`Screens\Setup.UI.Runtime.cs`) | `ClassicSetup.RuntimeCardTemplate` (`ClassicSetup.Runtime.cs`): the phase bodies, the fact boxes, the version picker, the local-source rows, the wrapping command row |

**The sign-in flows reach a view as facts.** A theme may not name `Spotify`, `Shell`, `Entities` or `Prefs` (WVTH002), so `ISetupSignInHost` projects the sign-in state, the session phase and the fault into `SetupSignInFacts` (facet, handed, minting, busy message, failure sentence, needs-a-code), `SetupPairingFacts` (the pairing card reads the sign-in state only, so a phase change never re-renders it), `SetupLoginStepFacts` and `SetupAccountFacts`, and carries the verbs (start the browser or the pairing flow, cancel, retry, not me, close the door, the sign-up and Premium links). Every fact method reads the same signals the old components read, in the same order, inside the view's render: a render subscribes exactly as before. The few shell and settings builders the pages place (the theme picker and options, the sidebar layout cards) and the signature sheet arrive as `Func<Element>` / `Action` members.

**Numbers.** The plate's geometry is `Rules\Tiers.Setup.cs` (`ClassicSetupTiers`, one record per window tier: the plate or sheet, the icon column, the footer; the hand-rolled 770 breakpoint is gone) and `Rules\SetupGeometry.cs` (what stays tier-invariant); the Lottie recolour is `Rules\SetupRecolor.cs` (what `Setup.Recolor` held); the footer, door and runtime card numbers are `Rules\ClassicSetupRules.cs`. The core keeps `Setup.Cover` (the shell's dim) and `Setup.WizardHeroes.AssetFor` (the hero loader's asset names).

**Motion is the wizard's, unchanged.** The page swap recipe (`Design.Nav.RecipeFor` of the session's direction), the body's 6 / -4 slide, the Busy ladder's staggered rungs and scale-popped marks, the reflow-eased ladder bar, the 1 Hz pairing countdown on its own component (the page and its hero never re-render) are the same elements with the same keys.

**Tests.** `Wavee.Tests\SetupWizardTests.cs` (the plate geometry and the recolour now over `SetupGeometry` and `SetupRecolor`; the footer table, the gating and the folds are unchanged); `Wavee.ShellTests\SetupMountedTests.cs`: Classic is the registered template of the seven keys, the plate shows the session's page with Back only where a page has one, the icon column is the window's breakpoint, the consent card shows on a first run and a terms re-arm and not on a re-auth, the door body keeps one tree for the cards and one each for Busy and Done, the wizard's Sign in page keys its body by facet, and the runtime card's command row asks the host for the diagnostics page. `ThemeOptionsMountedTests` still mounts `Setup.ThemeStep()`.

### The release notes (T11c)

The What's-new page, the highlight viewer and the after-update plate are the structure's picture over core hosts. The page (hero, highlight strip and cards, changelog sections / rows / issue chips / avatars, the release rail, the since banner, the loading and empty states), the viewer (veil, plate, band with its chrome, pager, text) and the plate (welcome, highlight row, footer) are registered Classic views (`Themes\Classic\Structure\Views\Screens\ClassicReleaseNotes*.cs`, numbers in `Rules\ClassicReleaseNotesRules.cs`); the load, last-seen, the issue-state republish, the after-update gate / latch / load, the overlays and every verb stay core.

| Key | Props | Core host | Classic template |
|---|---|---|---|
| `Pages.WhatsNew` | `WhatsNewPageProps` (the state `Loading` / `Empty` / `Loaded`, the `ReleaseNotesView`, "only the latest", the running version, and the verbs: show only the latest, copy link, open highlight `i`, open URL, the Store listing, open a release) | `ReleaseNotes.PageHost` (`[CoreHost]`, `Screens\ReleaseNotes.UI.cs`): the signals (view, loaded, onlyLatest), the load off the UI thread with its cancellation, `lastSeen` read BEFORE `MarkSeen`, the budgeted issue-state republish | `ClassicReleaseNotes` (`PageTemplate`: frame, hero, since banner, divider, rail, states), `.Cards` (strip, `CardView`, poster band, kind pill), `.Changelog` (`SectionView` with its local "Show all N", rows, chips, avatars, span bridge) |
| `ReleaseNotes.Viewer` | `HighlightViewerProps` (items, first index, `Close`, `TryItFor`, `OpenUrl`, `OpenStoreListing`) | `ReleaseNotes.OpenViewer` (anchor-less Modal overlay, LightDismiss, focus trap, `ScrimVisual = false`; builds the props once, "Try it" = navigate, then the host plate's close, then the viewer's) | `ClassicReleaseNotes.ViewerTemplate` (the index, the pager's mirror and the next slide's direction are the template's hook cells) + `HighlightViewerMotion` |
| `ReleaseNotes.AfterUpdatePlate` | `AfterUpdatePlateProps` (the "from → to" range, the version sentence, the tagline, the cards, open card, "Don't show again", "Full release notes", close, the Store listing) | `ReleaseNotes.AfterUpdateChrome` (zero size: gates, once-per-launch latch, load FIRST, then `OpenPlate` opens the Modal overlay at its final height) | `ClassicReleaseNotes.PlateTemplate` (the checkbox is the template's own cell) |

**Pure rules stay core.** The after-update gate, the range, `HighlightVisibility`, `HighlightMax`, the rail markers, the chip states and `HighlightPaging` (the viewer's clamped, no-wrap stepping and the direction a slide travels) are `Screens\ReleaseNotes.cs` / `.Host.cs`; `Wavee.ReleaseTool` compiles `ReleaseNotes.cs` by name and none of them names an engine type. `ReleaseNotes.Pill` stays a core door because Settings › About and the setup wizard's terms page draw it too (Classic's hero calls it as well).

**Numbers.** The highlight card's geometry and the plate's height budget (was `ReleaseNotes.HighlightCardMetrics`) and the viewer plate's geometry (was the geometry half of `HighlightViewerLayout`) are `ClassicReleaseNotesRules.Card` and `.Viewer`; the plate renders from them and `ReleaseNotesTests` holds the 620 DIP cap against them. The viewer's fixed colours, shadows, pager and the page's sizes are the rest of that file.

**Motion is the surfaces', unchanged.** The viewer's directional enter (±24 DIP + fade, 250 ms) over a direction-free 120 ms exit, the pips' Reflow width tween, the circle chrome's dim-in-place at an end, the sizer ZStack that keeps the plate's box final from frame one, the card's edge fade and press spring are the same elements with the same keys.

**Tests.** The arithmetic and stepping are `Wavee.Tests\ReleaseNotesTests.cs` (`ClassicReleaseNotesRules.Card` / `.Viewer`, `HighlightPaging`, `HighlightViewerMotion`); `Wavee.ShellTests\ReleaseNotesMountedTests.cs` mounts the three keys under Classic: the registered templates, a loading / empty page without a rail, a loaded page with its rail rows and highlight cards, the viewer's stable chrome and one pip per highlight, the plate's cards.

### The diagnostics pages and the log viewer (T11c, Common blocks)

The three diagnostics pages and the log viewer are **Common** blocks (`Themes\Common\CommonDiagnostics.cs`, `CommonDiagnostics.Widgets.cs`, `CommonLogs.cs`, numbers in `Themes\Common\Rules\CommonDiagnosticsRules.cs` / `CommonLogsRules.cs`): one copy of each view for every theme, written from today's code and converted to roles (WVTH003 applies to `Common` like a structure). `CommonBlocks.Register` adds them and Classic's `Register` calls it, so a theme that wants its own simply registers its own template under the same key.

| Key | Props | Core host | Common template |
|---|---|---|---|
| `Pages.Diagnostics.Playback` | `DiagnosticsPageProps` (glyph, title, scroll key, `Build`, optional poll gate) | `Diagnostics.RuntimePage` (`Screens\Diagnostics.Pages.cs`): the document of the runtime report, status, modules, updates, the fullscreen card, the health card, the podcast wire check, the buttons | `CommonDiagnostics.Template` (registered as `common.diagnostics.playback`) |
| `Pages.Diagnostics.Connect` | `DiagnosticsPageProps` | `Diagnostics.ConnectPage`: owner, last cluster, last put, last echo, local host | the same template (`common.diagnostics.connect`) |
| `Pages.Diagnostics.Capture` | `DiagnosticsPageProps` (poll gate = `RealtimeCaptureHost.Enabled`, 750 ms) | `Diagnostics.CapturePage`: status, anomalies, recent roots with their trees, the footer, and the scroll, tiles and evidence nodes | the same template (`common.diagnostics.capture`) |
| `Pages.Logs` | `LogsPageProps` (the `ILogsHost` model) | `LogsPage.Host` (`[CoreHost]`, `Screens\LogsPage.Host.cs`) over the `LogsPage.Sessions` model | `CommonLogs` (`common.logs.page`): the viewer component (header with crumbs and session picker, toolbar, column header, measured list, row anatomy, footer, the "…" menu) |

**A diagnostics page is a document.** The core builds `DiagNode`s (`DiagCard`, `DiagRow`, `DiagStatus`, `DiagNamed` with presence chips, `DiagButtons`, `DiagLink`, `DiagRoot` with its tree, ...: plain records over strings, enums and delegates). What a fact is and how it reads (a blank value is the dash, the invariant number formats, the stamps, the tone of a status) is the core's; the view draws the nodes. `Build` runs inside the view's render, so every `.Value` read in it (`Setup.Runtime.Status`, `Notify.Update`, `Stage.Diagnostics.Version`, the owner and phase signals, `Connect.Version`, the capture toggle) subscribes the view exactly as the old page component's reads did. The view owns the refresh counter (the Refresh button), the capture page's expanded root and the poll; a status tone is a `DiagTone` mapped to the system fills in the view (P15: icon roles and hero tones).

**The stateful cards are components the page embeds for a node**: the playback health card (a 1 Hz read of a core delegate), the podcast wire check (text boxes, the Run state, the result table; the walk and the prefill are `IDiagWireHost`), the scroll probe card (`IDiagScrollHost` over `ScrollProbePrefs`), the tiles card (a 750 ms poll and the pass-timing switch's signal over `IDiagTilesHost`) and the evidence card (the pixel boxes, the pick-with-click observer and the poll over `IDiagEvidenceHost`). Their hosts are one core object each, so an unchanged page render re-pushes an equal props record and the card skips.

**The log viewer keeps its picture and its view-only state, the core keeps the session model.** `LogsPage.Sessions` owns the folder walk (never latches, a thrown walk is a caption), the load of a past session, the picker's labels and caption, where a selection lands after a re-list, the export through the Save As picker, Verbose, Clear view and the verbs; it publishes the picker's selection, the refresh bump and the session list's revision as signals. The view owns the filter text, the level strip, the category, the order, the grouping, the wrap, the page cap, the expanded row, the list's controller and the menu, and runs the mount walk, the activation walk and the 750 ms live tail itself (so the effect timing is the old component's).

**What stayed core:** every report and decision (`Diagnostics.cs`: the runtime report text, `LogView`, the frame watch, the lyrics inspector's rules), the capture store and its pure scanners, `TileDiagRules`, `ScrollDiagRules`, `ScrollProbePrefs`, `EvidenceReport`, `LogStore`, `LogsPage.Rules`, the install and the probes. The lyrics inspector dialog stays in `Screens\Diagnostics.UI.cs` (a dialog, not a page).

**Tests.** The ids, Classic's registration of the four keys under the Common template ids and the records' value equality (the widgets' skip) are `Wavee.Tests\CommonDiagnosticsContractTests.cs`; the pure rules are the existing `LogsPageRulesTests`, `TileDiagRulesTests`, `ScrollDiagRulesTests` and `DiagnosticsCoreTests`. `Wavee.ShellTests\CommonDiagnosticsMountedTests.cs` mounts the views under Classic: a page builds its document in the render and the three cards stand under `scroll-card`, `tiles-card` and `evidence-card`, the page poll re-renders only while its gate holds, and the viewer stands its frame, shows the empty and loading states and mounts the keyed list for a session with rows.

### The Common blocks (T11c)

`Themes\Common\` (namespace `Wavee.Themes.Common`) holds the utility views every theme shares: written once from today's code, converted to roles (WVTH003 applies to Common like a structure, so its numbers are `Themes\Common\Rules\Common*Rules.cs`), and registered by **one call** from a structure's `Register`: `CommonBlocks.Register(views)` (WVTH001 lets a structure reference its own theme and Common; Common references only Common). Classic calls it, so there is one copy of each view; a theme that wants its own registers its own template under the same key instead of calling the block.

| Block | Key | Core half | Common template |
|---|---|---|---|
| history | `Pages.History` (`HistoryPageProps`: the log's version signal and `Project(search, filterIndex, sortIndex)`; `HistoryPageModel` / `HistorySection` / `HistoryRowFacts` come back) | `Shell.HistoryPage` and `ProjectHistory` (`Shell\Shell.History.UI.cs`) over `Shell.History` (kinds, filter, search, the most-visited fold, date buckets, timestamps, unique count), the store and `HistoryVerbs` | `CommonHistory`: header, search, chips, sort, the date cards or the one most-visited card, the vacancy. The search text, the filter chip and the sort are the view's own signals |
| Home customizer | `Pages.HomeCustomize` (unchanged key and props; T10a1 drew it in Classic) | `CustomizeScreen` (`Home\Customize.Host.cs`) | `CommonCustomize` |
| frame-rate overlay | `Shell.Fps` (unchanged key and props; T4c drew it in Classic) | the frame root: `Diagnostics.FpsOverlayOn` | `CommonFps` |
| not-found | none: the content state's NotFound arm | `Shell.ContentHost` decides | `CommonNotFound.Block(key, glyph, goHome)`, called by a theme's `Shell.ContentState` template (the page box and the offline lane stay the theme's) |
| diagnostics, logs | see the diagnostics section | | `CommonDiagnostics`, `CommonLogs` |
| nothing (P5 ZK) | any key a structure has no picture for: `CommonNothing.Register(views, key)`, template id `common.nothing.<key id>` | the key's own host still runs | `CommonNothing`: a zero box, not hit-testable. Not a way to stop a core host that needs a picture (the pane): that is a capability |

**History keeps today's behaviour.** The page re-renders on the log's version and on its three signals, exactly as the component did; a route this build has no page for stays in the log at 0.6 opacity and inert while its delete stays live; Clear all asks first (Cancel is the default button), a row delete does not; Most recent trims the last row's divider of every card, Most visited keeps every divider. The projection is built per render by the core (at most 500 rows) and carries ready-to-draw facts (title, glyph, kind label, argument, timestamp, visit count, reachability and the two verbs), so the view names no `Shell`.

### The Common blocks' tiers (TC10)

No theme draws a board for the Common blocks, so under owner Q18 each is built from the host theme's parts (the same tokens, the same controls) and tiered ONCE for every theme. A Common block therefore
decides by what is true of every ladder: it reads the **content** tier (`ctx.Tier().Content`, or `CommonTierComponent.ReadTier()` in a component: the region the page is laid out in, whatever edges the
host theme drew) and the height tier, never an edge and never a number of pixels. The per-tier layout records and their few numbers are `Themes\Common\Rules\Tiers.cs` (`CommonDiagnosticsLayout`,
`CommonLogsLayout`, `CommonHistoryLayout`, `CommonCustomizeLayout`, `CommonModuleLayout`, `CommonNotFoundLayout`; `CommonTiers` holds what they share); each is a value built where it is used (the spacing
roles are read live), asserted on the record by `Wavee.Tests\CommonTiersTests.cs`. The one rule everywhere:

| Tier | What a Common block does |
|---|---|
| Compact | One column. A label and its value stack; a wide table becomes stacked rows (never a page-level scroll and never a scrolled region: a result row is two or three lines); a row of controls wraps; the page gutter is the narrow one (16); every hit target is at least 40 x 40 (a Large button, a 40-DIP icon button, a link, field, segmented strip or check lifted to 40, and the stock ComboBox at its Large 40-DIP field with touch rows, filling a column and hugging its selection in a row, `CommonTouch.Picker`; a bare icon head is a 40-DIP icon button over a menu of 40-DIP rows); the log viewer's crumbs are the stock Large ones |
| Small | the Standard arrangement narrowed: the gutter steps down to 32; a head that cannot fit one row (the log viewer's title with its session picker) takes two |
| Standard | today's layout, unchanged |
| Huge, Huge 4K | the Standard arrangement under a max content width, the fill rule: one column that stops growing at the width (the Classic page column, `R.Space.PageMaxW`; the diagnostics' own 1000) and keeps the page's left edge, as the Settings and release-notes Huge boards do; lines of text never run across 3840 DIP |
| Short height | a pinned head that would eat the page scrolls with the page instead (history, the Home customizer; at Compact too); the log viewer drops its crumbs and caption and scrolls its toolbar as one line (Open folder moves into the "..." menu) |

Per block. **Diagnostics**: Compact stacks `Row`, a pass, anomaly, root and tree row and the wire result and evidence hit tables into lines, wraps the control rows (pixel query, export), fits the text boxes to the card (240) and
lifts every button, link, field and check; the column is 1000 on every tier. **Logs**: Compact draws a row as two lines (time, level, count over category and message) with no column header (the sort is a
"..." menu item), wraps the toolbar with 40-DIP targets and pickers, and puts the picker cluster under the title (Small too); the footer wraps. **History**: Compact stacks the head (title, then stats and Clear all, then
the full-width search, the filter chips as one scrolling line with 40-DIP chips and the sort picker), moves a row's time and visit count into its meta line and makes the delete a 40-DIP target. **Customizer**:
Compact stacks Reset under the title, gives a row 56 DIP with 40-DIP Up and Down, and scrolls the head with the list (the list alone does not fit a Compact window). **Module page**: the gutter and a
max-width column for the stage and the scroller, 40-DIP buttons, chips, links and the prose toggle; the hero, cards, fact tiles and chips already wrap. **Not found**: the line wraps inside a narrow side room
and Go home is a 40-DIP target (the block keeps its call shape and its three children). **Frame-rate overlay**: no tier layout: a content-sized, hit-test pass-through pill that is never a target. The gutter reads
`CommonTiers.WideGutter` / `Gutter` (36 / 32 / 16) until TC1's page gutter ladder owns those three numbers for the structure's own pages.

### The collection at every tier (TC7)

The library, Recents and Local files are tiered by the **content** tier (`ctx.Tier().Content`) and, for the library, by its own layout ladder. Boards: `C-Library`, `C-LibraryList` and `C-Recents` with their `-Compact`,
`-Small` and `-Huge` twins; Local files has no board in any theme (owner Q18) and is the playlist page (`Playlist.LocalFiles`), so it holds the detail family's tier layout.

| | Compact (400) | Small (800) | Standard | Huge (2560, and 3840) |
|---|---|---|---|---|
| Library columns | ONE column, drill-in (the navigator, then the pane or reader; a crumb bar over a leaf) | split: navigator rests at 240 (grip 240-560), pane floor 300 | split (tuned), pane floor 360 | split, navigator rests at 560 (artists 400) and is tinted, pane floor 480 |
| Library grid | the core's 116-164 cells, gaps 12 between cells / 16 between rows | 96-164: the full grid starts at 96 (two cells in the 240 navigator), gaps 16 / 12 | 116-164, gaps 8 / 8 | 116-164, gaps 8 / 16 |
| Library head | kind chips (40 pills, the page on screen filled with the accent), title and count, a 40 sort button (menu of the kind's words) with 40 view cells, a 40 filter; rows 12 apart, 16 sides, 12 over it | title, a 32 sort button (`ClassicLibraryLayout.SortButton`; the artists' list and Podcasts keep their words) with 28 view cells, 32 filter; rows 10 apart, 12 sides, 16 over | tuned: title, word rail and 28 view cells, 32 filter; rows 8 apart, 12 sides, 16 over | 16 sides, 24 over it |
| Library pane | the pane's insets are the frame's 16 (`ClassicTiers.PaneInset`, was 20); command row 40 (the album and the show pane) | 16; the header is C-Library-Small's 96 cover in a 120 band, 16 from the words, 10 over the command row (146 stated; the drill-in's leaf keeps the tuned 156) | 16 | 16; the album pane's "Also by" is a 300 aside 32 right of the rows |
| Recents masthead | 12 over it, Overview 40, pivot tabs 40 tall (8 pad, gap 4) | 16 over it, Overview 24 | 24 over it, Overview 24 | Overview 32, a toggle for the docked aside |
| Recents Overview | the semantic zoom's calendar | as Compact | as Compact | docked: a 660 aside (two plated month cards) 48 right of the list, the list capped at 1100 |
| Recents rows | stacked: the kind and played-at stamp under the sub line, a 40 chevron, no hover "...", child rows 44, no day rail | the tuned row with a 44 time lane and a 28 chevron button (12 glyph), the 44 rail FLUSH against the list and 4 from the card's edge (the body keeps no right pad; masthead rows 2 apart, 12 from the summary to the pivots) | the tuned row (time sizes to its text, the shared chevron cell), rail 44 12 from the list | as Standard |
| Recents calendar gap | 16 | 24 | 36 (the old `PageWide`) | 16 |

**The persisted column widths are never written by a tier.** `library.<kind>.leftw` / `.midw` are the host's: the grip's release writes them (`CommitLeft`), nothing else does. The one-column arm leaves them alone, and a split
whose pane would fall under its floor squeezes the navigator by flex (`Shrink 1`, down to the grip's 240), it does not rewrite it. The column counts of a grid are the engine's floor-fit over the navigator's width (116-164
cells): 2 at the 340 default and at Compact, 4 at Huge's 560. **The navigator's resting width per tier** (contract 1.21): while the user never dragged the grip the host rests the navigator at the structure's width for the content tier (`ThemePolicies.LibraryTiers`, Classic `ClassicLibraryTiers.Policy`: 560 at Huge, the artists' reader list 400; the key's 340 / 280 below), re-rests it on a crossing (the column eases: layout transitions are held off only while the grip is dragged) and never stores it; "never dragged" is `ThemeState.IsCustomized` (a value is stored and it is not the key's default), so an older build's saved default needs no migration, and the first drag makes the width the user's. The Huge pane header is C-Library-Huge's 160 cover in a 200 band, 24 from the words (`ClassicLibraryLayout.PaneCover` / `PaneHeaderHeight` / `PaneHeaderGap`). The sort rail is a **fit** rail (`Controls.Words.Rail(fit: true)`): the leading words that fit whole and a trailing "more ▾" with a radio menu of the rest (the selected word takes that slot when it is folded), never a feathered clipped word.

**The grid's gaps per tier** (contract addition after 1.22, `LibraryTierPolicy.GridGapsFor(content)` / `LibraryGridGaps(Column, Row)`, Classic `ClassicLibraryLayout.GridGaps`): the boards' `gap: row column` (C-Library 8 8, -Compact 16 12, -Small 12 16, -Huge 16 8). The host mints them into `LibraryNavMount.Gap` / `RowGap` beside the cell floor, never into the key; the mount cache swaps the `GridFit` value (`.WithRowGap`) and the engine re-fits the mounted grid's floor AND both gaps live (`GridVirtualLayout.SetFit`, the anchor row kept). The shimmer draws the record's gaps.

**The head's sort button and word rail share one slot** (`lib:picker`, `MinHeight = ControlH`): the sort button (Compact, Small) and the word rail (Standard, Huge) are different trees, so a crossing between them swaps under `ClassicTierMotion.SwapFade` in that slot (the entering form fades in, the leaving one fades out beside it) and the head's rows glide (`ClassicTierMotion.Shove`) when the row gap changes (10 at Small, 8 at Standard). Artists and Podcasts keep one form at every tier and do not swap. Held by `TierLayoutTests.LibraryPicker.cs`.

**Small and the grid's cell floor** (contract 1.22): the policy also rests the navigator at Small (`LibraryTierPolicy.SmallNavWidth` / `SmallReaderNavWidth`, Classic 240, C-Library-Small / C-LibraryList-Small) and moves the full grid's size-S
cell floor there (`SmallCardCellMin`, Classic 96: the 116 floor would fit one column in 240). The host reads it through `CardCellMinFor(content)` into `LibraryNavMount.For(..., fullBase)`: the floor moves `CellMin`, never the mount key,
and the page's mount cache swaps only the grid's `GridFit` value, so a crossing re-fits the mounted grid in place (scroll and selection stay). The navigator's shimmer lays its cards at the record's `CardCellMin`, the same floor.

**The Huge navigator and album pane** (C-Library-Huge, C-LibraryList-Huge): the navigator column is tinted over the card (`ClassicLibraryLayout.NavTinted`, `ClassicLibraryRules.NavTint`, a static fill eased across a crossing) and the album
pane's "Also by … in your library" leaves the rows' trailing band for a 300 aside 32 right of them (`AlsoByAside` / `AlsoByAsideGap`): a 14/20 heading over two columns of 140 covers (the shared surface at a 156 card), scrolling on its own.
The aside column is in the pane's row at every tier (width 0 below Huge) so a crossing eases it and the rows' slot is never re-parented; the view provides a Classic context the rows' trailing band reads (a small host of its own,
`AlsoByTrailing`), so the strip is never shown twice. **The library show pane's command row is 40 at Compact** like the album pane's: `LibraryShowPaneProps.Primary` takes the control size the view asks for (contract 1.22, `ShowPrimaryArgs.Size`),
and the heart and the "..." take the 40 box.

**Recents' docked Overview** (C-Recents-Huge, `ClassicRecentsLayout.ListMax` / `OverviewAside` / `OverviewGap`): the page is a row of the column (masthead, pivots, list; capped at 1100 plus the gutters) and an aside 48 right of it with
its own "Overview" head, the heat key and two columns of plated month cards (`ClassicRecentsOverview`: the calendar's month card in a card plate, its own list and scroll, never the zoom's controller). The masthead's Overview
button becomes the aside's toggle (pressed while it shows, per mounted page); a day in it brings the list to that day (`IRecentsHost.JumpIn` only zooms in when the zoom is out). The aside is in the row at every tier, width 0
below Huge, so a crossing eases it; the semantic zoom's calendar stays the zoomed-out view (a day header still zooms out to it).

**Tests.** `Wavee.Tests\LibraryTiersTests.cs` pins the ladder (the 600 edge, the 24 holds both ways, the policy's named ladder, a structure with none is always split), the records and the 16 inset;
`Wavee.ShellTests\TierLayoutTests.Collection.cs` mounts the albums library (a list at every size including 3840, and a grid), the artists library, Recents and Local files at the plan 3.5 sizes and every threshold edge, with a
width stored before each library fact that a tier must leave alone; it also checks the tinted navigator, the album pane's aside slot (0 below Huge, never past the card), the Small grid's floor and Recents' list cap and
aside per tier.

### The artist and people pages at every tier (P3t, TC6)

The artist page (and its hero, the reader, the discography, the lightbox), the three concert pages and the profile pages read Classic's tier records; the sub-ladders that
were hand-rolled in core are the structure's, written with the engine's `TierLadder` (`Themes\Classic\Structure\Rules\Tiers.Artist.cs`, `Tiers.Profile.cs`, `Tiers.Concert.cs`):

| Ladder | Measured on | Edges (holds) | Who reads it |
|---|---|---|---|
| `ArtistLadders.Hero` (`ClassicArtistTiers.Hero`) | the artist / profile page's own width | Narrow · Compact 360 · Medium 600 · Wide 880 (24 either way) | `Artist.PageHost`, `Profile.PageHost` through `UseTier` (seeded from the content region) |
| `ArtistLadders.Reader` (`ClassicArtistTiers.Reader`) | the library artist reader's width | narrow · 640 · medium · 748 (the wide band's lane) · wide (24 on widening) | `Artist.Reader` through `UseTier` |
| `ArtistLadders.ProfileList` (`ClassicProfileTiers.List`) | the profile list body | narrow · 640 · wide (24 on widening) | `ProfileLists.PageHost` through `UseTier` |
| `ClassicArtistTiers.TopBand` | the Top-tracks band | stacked · 760 · beside (24 on narrowing) | the view, inside its `Responsive.Of` |
| `ClassicConcertTiers.Schedule` / `.Detail` | the schedule / event page | 760 (40 on narrowing) / 920 (60 on narrowing) | the views, inside their `Responsive.Of` (seeded from `ClassicRegion.EstimateWidth`) |

**What the core asks** is `ThemePolicies.ArtistTiers` (`ArtistTierPolicy`, `Theming\Policies\ArtistTierPolicy.cs`): the hero's `ArtistHeroMetrics` and the profile hero's
`ProfileHeroMetrics` for a sub-tier in the tiers on screen, the Top-tracks chart's widest column count and its rows per column (`ChartRows`, contract 1.22; the chart's
skeleton reads the same pair, so it keeps the chart's columns per tier) and the profile list's narrowest card. The album drawer's geometry (its track columns, row pitch
and head) rides the discography style's metrics (`DiscographyMetrics.Drawer`, an `Album.DrawerShape`, 1.22): the core's one verdict sizes it, and an open drawer re-flows
on a crossing. Classic answers with `ClassicArtistTierPolicy` and `ClassicArtistLayout.Drawer` from its records; the metrics carry the one page gutter
(`ClassicTiers.PageOf`), so the views no longer override it. A structure that declares nothing gets `ArtistTierPolicy.Default` (one Wide hero, two chart columns of five)
and the drawer before tiers (two columns from a five-column grid, 32 rows).

**One alignment source.** Where the page is wider than its measure (1600; Huge's 2400) the magazine is a capped, centred column. The hero's copy, the band, the Huge
section row and the magazine all inset by `ClassicArtistLayout.ColumnInset(width, gutter)` (the gutter plus half of what the measure leaves), so at 4K the hero's copy
stands on the same edge as Top tracks (`ClassicArtistRules.HeroCopyWidth` / `HeroVeilHold` take that inset).

| Window tier | Artist hero (`ClassicArtistHeroForm`) | Artist page (`ClassicArtistLayout`) | Profile hero / page | Concert event (`ClassicConcertLayout`) |
|---|---|---|---|---|
| Compact (`C-*-Compact`) | the overlay arm: the photograph 300 with the copy on it over a vertical veil (its meta wraps 4 apart, runs 16 apart), the actions a 40-target row under it (two rows under 360) | one chart column, a 164 shelf strip, each discography facet a 164 strip of album cards (152 on 12, no drawer); the discography page's two-column grid (128 floor), its one-column drawer of 40 rows and 48 head, 44 tabs; 40 targets and 40 pivot words | centred: the "Profile" eyebrow, 128 avatar, 96 x 56 stats, 40 actions; a 164 strip; a two-column list grid (128); 40 pivot words | a 220 stacked hero, the tickets inline, a 40 Buy; the hub's filter pills 40 |
| Small (`C-*-Small`) | Medium: the copy at the bottom-left, 560 wide, one bio line (288 tall in the Short 800 x 600) | two chart columns, a two-column drawer | a 200 row (the eyebrow 4 over the name) with the actions at its right end | split at 240, the tickets inline |
| Standard | the tuned design (Wide 440 / Medium 384 centred, the Compact and Narrow photograph fields) | the tuned design (the drawer's columns follow the grid) | the tuned design (no eyebrow) | the tuned arms, the 320 rail from 920 |
| Huge (`C-*-Huge`) | the copy 40 up at the bottom-left, 1120 wide, ending in a More button (the artist's menu); the height 0.24 of the hero's width between 560 and 640 | the 56 section row under the hero (the name, the pivot, Play · Follow), three chart columns of five at 3 : 1 beside the pick under its own "Artist pick" accent header (`ClassicArtistLayout.PickHeading`), ten-card shelves, a four-column drawer, the magazine to 2400 | the eyebrow, a 320 row, 240 avatar, the actions at the right end, the magazine to 2400 | split at 400 with 40 of air, a 360 rail; the lineup a 640 column beside a four-column grid of date cards (the related concerts: the app has no tour-dates feed) |
| Huge 4K (`C-Artist-Huge4K`, past the 3200 rung `ClassicDetailTiers.HugeWideRung`) | Huge's | Huge's with the chart four columns of four at 4 : 1 and the artist's next concert under the pick | Huge's | Huge's |
| Short (330-679 tall) | a hero caps at 288 (60 % of the 480 page) with its copy at the bottom; under 480 tall the 126 strip (the name, Play · Follow) | | a 96-avatar row; under 480 tall the 64-avatar strip | |

A hero crossing commits on the first frame that is not resized: the hero's arm is keyed (`artist-hero:<arm>`) and cross-fades, the pinned root's height tweens
(`ClassicArtistTiers.HeroMotion`, the detail hero's reflow recipe) and the magazine under it glides (`FollowMotion`). The Huge section row (`artist-section-row`) is
always mounted: its height tweens between 55 and 0 and its row fades, and the magazine glides under it. A discography facet's arm is keyed by what it is
(`disco-arm:grid` / `disco-arm:strip`, never the tier) and cross-fades, and every section's holder glides (`FollowMotion`) when a section above it changes height. The
4K concert card under the pick fades in and out while the pick's column tweens; the pick keeps its node. A shelf, the discography grid and the chart keep ONE key at
every tier: their fit is a live prop (PagedShelf's band, `LazyGridFit`), so a crossing re-fits the same nodes (their page, the chart's selection, the open album
drawer and the page's scroll survive); only the chart's rows are a frozen shelf configuration, so the Huge 4K crossing remounts the chart shelf under its
`chart-rows` holder, the selection model surviving it. The lightbox (no board) takes 40 targets at Compact and its filmstrip scrolls sideways where it outruns the window.

**Tests.** `Wavee.Tests\ClassicPeopleTiersTests.cs` (the records per tier against the boards, the Short caps, the ladders), `ArtistHeroLayoutTests` /
`ProfileHeroLayoutTests` (the tuned Standard numbers and the collapse), `ArtistReaderHeadLayoutTests` (the reader ladder), `ProfileListRulesTests`, the concert ladder
tests; `Wavee.ShellTests\TierLayoutTests.Artist.cs` mounts the artist page (3840 too: the hero's copy on the magazine's column, the section row, the More button, the
Compact strip), the discography (the drawer's geometry), the concert hub (the 40 pills) and event page, the profile and the profile list at the plan 3.5 sizes and edges.

### Discovery at every tier (TC4)

Home, the Home "See all" page, Browse (the directory, its Charts band and a category page) and Search are tiered by the **content** tier (`ctx.Tier().Content`, the region the page is in, which
is also what the page gutter follows) and the height tier. Each page reads one record: `ClassicHomeTiers.Of(facts)` (`Rules\Tiers.Home.cs`: `ClassicHomeLayout` with the nested
`ClassicDaylistLayout`), `ClassicBrowseTiers.Of(facts)` (`Rules\Tiers.Browse.cs`) and `ClassicSearchTiers.Of(facts)` (`Rules\Tiers.Search.cs`). A column count is a `ClassicGridBand(MinCol, MaxCols)`
(a floor-fit INSIDE the tier at the region's own measured width, or a fixed count when `MinCol` is 0) and a paged shelf is a `ClassicShelfBand` (card band, column cap, fixed strip width, pager);
no view compares a width with a number and the old 720 / 380 genre bands, the Mood and More floor-fits and the fixed 900 and 1100 first-frame guesses are gone. A width-driven box (`Responsive.Of`)
is seeded from `ClassicRegion.EstimateWidth(facts, viewport width)`: the same-frame viewport less the chrome in front of the page (the facts' content width when they are current), so the first
frame is in its final tier. A paged shelf, the drill grid and the Fold deck keep ONE key at every tier: the card band, the column cap, the pager and the card height are live props
of the mounted shelf (the engine re-fits `FillRowVirtualLayout` in place), and the drill grid's cell floor is a live layout prop (`GridVirtualLayout.SetMinCellWidth`, applied
in the layout pass's anchored-reseed seam so the row at the top stays where the user sees it). The daylist and the Recently played tiles are one band at every tier (a row at
Huge, a column elsewhere), so the Huge crossing moves the two chapters instead of reparenting them, and every zone FLIPs into its new box (`ClassicTierMotion.Reflow`).

| Page | Compact (400) | Small (800) | Standard | Huge (2560, and 3840) |
|---|---|---|---|---|
| Home page | column fills, top 12, zone gap 24, 20/28 facet words | fills, top 16, gap 32 | cap 1600, top 24, gap 40 (tuned) | cap 2400, head gap 28 |
| Daylist | stacked: 140 art, copy over its foot, 40-tall actions, clock = ring, countdown and the daypart word over 4-high bars | 216 banner, 364 copy, clock one line | the tuned card (320 floor, half-width copy) | 400 hero beside the tiles (1.35 : 1), 760 copy at 68/92, clock a 440 plate |
| Recently played | 2 columns of 56 tiles with 40 art (3 + history) | 3 columns (5 + history) | fit 200, up to 4 | 2 columns x 4 rows beside the hero (7 + history) |
| Cover / wide shelf | fixed 164 cell strip (148 art), no pager | fit 148-188, paged | fit 148-188, paged | 10 columns (wide: 5), paged |
| Release, cluster, tile, episode grids | 1 column | tuned | tuned | 3 / 4 / 6 / 3 columns |
| "See all" grid | cells from 124 | 156 | 176 | 216 |
| Failure InfoBar | 72 tall, Retry 40 | stock | stock | stock |
| Facet row's end | the 40 x 40 Friend activity button (`FacetRowProps.ToggleFriends`, the shell's friends panel) | Following only on a word with a sub-chip (tuned) | tuned | Following on every word: on "All" it drives the first word's sub-chip (`FacetPivot.FollowingWord`) |
| Chapter headers | titles alone (no subtitles) | title · subtitle | title · subtitle | title · subtitle |
| Browse genres / mood / more | 2 / 2 / 2 | 2 / 5 / 3 | 3 / fit 132 / 4 | 6 / 12 / 8 |
| Browse charts | stacked, 136 tall, 92 covers | stacked, 140 tall, 100 covers | paged two up, 176 | paged four up |
| Browse chips | Top 40-tall pills; For you one 40-tall scrolling line | stock | stock | stock |
| Category shelf | 164 cell strip, 40-tall title, no pager | 176 cell strip, 4 gap | fit 148-188 | 10 columns, "See all" |
| Search | rows flush, 44 art in 60, a 44-tall scrolling tab strip, 1 song column, 2 genre columns | scrolling 36 tab strip, 2 song columns | wrapping tabs, tuned | top result 700 x 300 beside Best matches, 6 genre columns beside Related, 10-column rails and grids |
| Search top result and rails | the tuned card | the tuned card | the tuned card | the AVATAR form (`ClassicSearchHeroForm.Avatar`): a "Top result" header over the card, 128 art top left (round for a person), the 40/52 title, the kind line, the actions bottom right, inset 28; an "Artists" rail of round cards (`SearchBodyModel.ArtistRail`, its header `SearchPageProps.SelectArtists`) between the playlist rail and Genres, keyed without the tier and revealed or collapsed with the drawer reveal on a crossing |

**Short** (330-679 tall; `C-Shell-Short`): the page top is 16 and the hero never sits beside the tiles. Under 480 tall (the `page.hero.full` height rung, `ClassicTiers.HeroFullRung`: where 60 % of the page,
the window less 120, no longer holds the 216 banner) the daylist is a strip capped at 60 % of the 210-DIP page (126 tall) carrying only its eyebrow and title with no clock; from 480 it keeps its form (the
Small board is drawn at 800 x 600, a Short window), Huge's 400 hero stepping down to the banner. **Huge 4K** reads Huge's records: Classic draws no 4K band and the plan caps the grids at 2400, so the 12-card shelves and full-bleed page of `C-Home-Huge4K` are not built.

### The feedback and crash dialogs' bodies (T11c)

The dialog **chrome** (the stock `ContentDialog`: title, buttons, the vetoable primary click) stays core; the **body** is the key's picture. `Dialog.Feedback` (`FeedbackDialogProps`: the opening kind and `IFeedbackDialog`) is Classic's `ClassicFeedback` and `Dialog.Crash` (`CrashDialogProps`) is `ClassicCrash` (`Themes\Classic\Structure\Views\Screens`, numbers in `Rules\ClassicFeedbackRules.cs` and `ClassicCrashRules.cs`). T11a owns the other nine dialog keys.

| Key | Core half | Classic template |
|---|---|---|
| `Dialog.Feedback` | `Feedback.OpenDialog` (the chrome and the veto) and `Screens\Feedback.Form.cs`: `FormModel` (one per fixed kind: the off-thread compose, the live preview, the crash form's three header states, the submit gate with its toasts, the GitHub door, Copy, Save As) behind `IFeedbackForm`; `DialogSession` behind `IFeedbackDialog` | `ClassicFeedback`: the subtitle, the kind `Segmented` (never in crash mode) and the card, a nested component keyed by the kind (a switch remounts the form, so every signal is seeded once). The field signals are the view's; the card installs its submit on the dialog while mounted and drops it on unmount |
| `Dialog.Crash` | `Crash.OpenPrompt` (the chrome, `PromptProps`: the header message, the lead, the dump label, the compose factory, `CrashConsent.Submit`); the launch decision, the upload queue and the outcome toasts | `ClassicCrash`: the header bar, the lead, the "what is sent" box with the scrubbed preview, the dump and always-send boxes, the privacy and GitHub links. Send is vetoed with a "preparing" toast until the pool-thread preview has been posted back |

**Tests.** `Wavee.Tests\CommonTiersTests.cs` pins the layout records per tier; `Wavee.ShellTests\CommonBlocksTierTests.cs` mounts every block at 300x640, 400x860, 300x330 (and the 210 and 126 page heights a 330-tall window leaves Classic and Zune), 1440x960, 2560x1440 and 3840x2160 (no page-level horizontal overflow, the record in use, Compact targets >= 40, the column under its max width, nothing laid out below the page). `Wavee.Tests\CommonBlocksContractTests.cs` pins the ids, Classic's template ids and the empty history model; `Wavee.ShellTests\CommonBlocksMountedTests.cs` mounts the history page over a fixture projection (it projects under its own states and again when the version moves; an empty projection mounts the vacancy), the feedback card over a fixture form (one form, one compose, the submit held while mounted) and the crash card over fixture props (compose prepared, Send waits for the preview).

### Screens at every tier (P3t, TC8)

Settings, the setup wizard, the release notes and the report / crash dialogs are tiered by records, never by a width comparison (WVTH013). Compact follows the `-Compact` boards, Small the `-Small`, Huge the `-Huge`; Standard is the tuned design, unchanged.

**Settings** (`Rules\Tiers.Settings.cs`, `ClassicSettingsTiers.Of(ctx.Tier())`, one `ClassicSettingsLayout` per CONTENT tier). Compact: the tab strip is one 44-tall lane that scrolls sideways and bleeds to the card's edges with a faded end, every setting's control sits under its name at full width (choices are the stock ComboBox at its Large 40-DIP field, filling the row, with touch rows; a NumberBox's field is 40 through its options' style, `ClassicSettings.Touch.cs`; a switch keeps label | state | switch in a 48 x 40 cell), buttons, links, sliders, selectors and radios are 40, and the theme picker is a two-column grid of 72-tall thumbnails (`GridCardWidth` is the arithmetic over the live viewport). Small: the tabs scroll and fade, the picker is one scrolling strip of 190-wide cards. Standard: the 1000 left-aligned column and the 208 x 172 wrapping cards. Huge: a tab's sections split over two columns of up to 1000, 32 apart, left-aligned (`SplitAt` picks the boundary that balances them; a tab with one section stays one column), the theme cards the board's five-up row that fits the column (`ClassicThemePickerForm.Fit`: equal fluid columns, each at most 184). **One tree at every tier.** The split is made at every tier: the two halves are always the keyed columns `settings:column:0` and `:1` in the pair box `settings:columns`, a row at Huge and a column elsewhere (4 apart, the second half's opening header keeping its space above, so it reads as one column), so a crossing into or out of Huge keeps every section, row, expander and the page scroll, and the columns glide (`ClassicTierMotion.Reflow`). The theme picker's radio group always rides one lane (`settings:theme:lane`) that scrolls sideways at Small and is a content-sized, never-scrolling box elsewhere; the form is the lane's orientation and the row's and cards' sizing, and the cards glide on a crossing. The tab bodies are static functions: the template stores the record in `s_layout` before it builds, and every stateful card derives from `SettingsComponent` (subscribes to the tier facts and refreshes `s_layout` first), so a control is never built from a stale tier. The control helpers (`StdButton`, `LinkButton`, `Combo`, `NumberField`, `Toggle`, `Selector`, `SliderControl`, `Radio`) are the only way a tab makes a control. The head and the section headers follow the boards too: the page title stands behind a 24 gear glyph (gap 12) at Compact, Small and Huge (`TitleGlyph`; the tuned Standard title stands alone), and a section header's caption hangs under its title at Compact (20 above, 6 under) and Standard (the tuned 32 / 8) but rides the title's line at Small (the boards' baseline row: 8 after the title, one line with an ellipsis, 24 / 8) and Huge (28 / 4) (`SectionInline`, `SectionTop`, `SectionBottom`). The header's spacing is its own padding, so a crossing reveals the new height (`Design.Reveal.Resize`, the cards below ride it) while the caption (`settings:section-caption:<title>`) slides between its slots; the glyph (`settings:title-glyph`) fades and the title slides beside it. The standalone "Classic options" page of the `C-ThemeOptions` boards does not exist in the app: the options are the Appearance tab's expander, which takes the same tier rules (stacked rows, 40 chips and swatches).

**Setup** (`Rules\Tiers.Setup.cs`, `ClassicSetupTiers`, per window tier): Compact is a full-bleed sheet under the 48 title row with a stacked footer (step label over a full-width bar, then a two-column grid of 40-DIP buttons), Small the 762 x 490 plate with no icon column and the two sign-in cards side by side, Standard the tuned plate with its 192 icon column, Huge a 960 x 600 plate with a 240 column and a 120 QR. The old 770 breakpoint and its helpers are deleted.

**Release notes** (`Rules\Tiers.ReleaseNotes.cs`): `ClassicReleaseNotesLayout` per content tier (rail 208 / 280 at Standard and Huge, a 40 picker at Compact and a 32 one at Small, the highlight form, the 1600 left-aligned cap and three changelog columns at Huge) and `ClassicReleaseOverlayLayout` per window tier for the viewer and the after-update plate. The rail and the picker are keyed fading wrappers, so a crossing cross-fades them.

**Report and crash dialogs** (`Rules\Tiers.Dialogs.cs`, `ClassicScreenDialogTiers`): form dialogs open through `OverlayFit.ShowForm`. A structure sets `DialogFit.FormWidth` (the card width for those two dialogs beyond the 548 every other dialog keeps; contract 1.17); their command buttons are the engine's own at the band's `DialogFit.CommandSize` like every dialog's (contract 1.19). Compact fills the window less 16 and folds the preview behind a 40 row; Small and Standard keep the 548 card; Huge is a two-pane 1040 card with the live report beside the form. A body reads its record and the width its card left it once, at open.

**Tests.** `Wavee.Tests\ClassicSettingsTiersTests.cs`, `ClassicSetupTiersTests.cs`, `ClassicReleaseNotesTiersTests.cs`, `ClassicScreenDialogTiersTests.cs` pin the records; `Wavee.ShellTests\TierLayoutTests.Settings.cs`, `.Setup.cs`, `.ReleaseNotes.cs`, `.Dialogs.cs` mount each screen at the plan 3.5 sizes and edges.

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

### Apply and switch (the theme transaction, A4.2)

`ThemeHost.Switch(id)` runs on the engine's **theme transaction** (`AppHost.RunThemeTransaction`, engine EP14 / E5): the new
composition's fonts prewarm with the UI live (the picked picker card shows a busy ring once that takes 100 ms), the window and its
pop-outs freeze, and in ONE apply flush under the frozen frame `theme.id` is persisted, the composition activated (new option
store, facts, registry), its definition installed and `ThemeHost.RootEpoch` bumped, so the root remounts; the new frame settles
(6 passes / 250 ms) and the thaw dissolves to it (250 ms, a premultiplied cross-dissolve over the live DWM backdrop, on D3D12
as on the headless backend). The DWM backdrop change, when the material or the dark flag differs, is ordered with the
dissolve by the engine. `ThemeHost.Apply(OptionChange)` validates the change (an invalid value, an unknown option or a parameter
throws `ArgumentException`); a **Live** option is published and the definition re-applied live (the cross-fade path, no freeze);
a **Transaction** option is published inside a transaction **in place**: no remount, so every slot whose template did not change
keeps its state and the transient hosting the option's control stays open. `ResetOptions` resets the Live options live and the
Transaction ones in one transaction.

- **Nothing is carried** (owner Q26): the remounted pages mount like a fresh navigation (find text, drawers, disclosures reset;
  route, panel, stage and every core model stay; scroll lands where `ScrollPositionMemory` puts a fresh navigation).
  `SwitchResetsStateTests` pins it.
- **Failure:** a template of the new theme that throws (or an `Apply` that throws) is rolled back under the same freeze: the previous
  composition is re-activated and the root swapped back, the frame thaws with a cut, and a toast says the theme could not be
  applied (`Strings.Settings.ThemePicker.SwitchFailed`).
- **Navigation during a transaction** (`Shell.GoTo`) waits for its thaw start (`ThemeHost.HoldNavigation`; the latest wins).
- **Appearance:** light/dark stays live. Only when the active definition changes more than colours with the kind (a different
  window material per kind) does `Appearance` take the transaction (`ThemeHost.SwitchKind`), with no live cross-fade.
- **The runner:** the frame root binds its window's `InputHooks.RunThemeTransaction` (`ThemeHost.BindTransactions`); with none
  bound (the boot before the window, unit tests) a change applies at once.
- **The video presenter** is `Flow.Persist(Video.PresenterPersistKey)` (`"video.presenter"`): the new root adopts the playing
  picture's subtree (its surface and binding) instead of re-creating it.
- **Not built yet:** a Transaction-mode slider or colour picker must apply on release (plan 8.2); no such option exists and the
  engine `Slider` has no release callback, so it lands with the first one.
- Tests: `ThemeTransactionMountedTests` (the test-only `ClassicAlt` theme, `Wavee.ShellTests/ClassicAlt.cs`, switches live and
  back; a Transaction option applies in place; navigation waits; 100 switches over Home, Artist, Search, Concert, Browse, Show and
  the stage with lyrics playing end with 0 settle timeouts), `SwitchResetsStateTests`, `LiveOptionBehaviourTests` (a Live option
  never freezes), `AppearanceTests`.

`ThemeRoot.Wrap` (called by both `RootHost` and the headless root through `Shell.RootFrame`) provides
`ViewRegistry.Context`, `OptionStore.Context` and `ThemeFactsContext.Signal` around the frame, and renders the frame in an
alternating component type from the first switch on, which is how the root remounts.

> **Why `ThemeHost`, not the plan's `Themes`.** Plan 5.3 calls the class `Themes`, but the themes live in `Wavee.Themes.<Name>`
> (plan 1.2), and inside `namespace Wavee.*` the simple name `Themes` binds to that namespace. Read every `Themes.Current`,
> `Themes.Facts`, `Themes.Switch` or `Themes.Apply` in the plan as `ThemeHost.…` (`using Wavee.Theming;`).

### The options UI (T1b)

Settings › Appearance is `Theme` (the picker) · `‹Theme› options` · `Display` (app mode, zoom, marquee), then the rows that have
no other home yet (lyrics, full screen, now playing, Home). Nothing in it names a theme.

- **Picker.** Classic's `Settings.ThemePicker` view (`ClassicSettings.Theme.cs`) draws one fixed-size card per `ThemePickerEntry` (`PickerState.Entries(developerMode)`:
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
  the tab never changes layout again. Classic lists 3 (M1 deleted one; M2, M3, M4, M6, M7a and M8 moved the track list style, the row density, the hide-art switch, the washes, the sidebar layout and density and the page layout with its two rail rows into options; the sidebar's three actions stay rows). Inside a group the generated rows come first, so the layout cards and the density sit above the sidebar's actions, as the rows they replaced did.
- **Palette.** An option with `Command = true` contributes rows (`ThemeOptionsSection.Commands`): a toggle on/off, a select one per
  choice, an action one. `Shell.CommandTable.BuildIndex` appends them after the builtins (`PaletteKind.ThemeOption`).
- **Setup.Theme.** The wizard's second page (`WizardPage.Theme`, M7a: it replaced the Layout page and its two sidebar cards) mounts the `Setup.Theme` view (`ClassicSetup.ThemeTemplate`, on the Layout page's frame) with the picker and
  the options (`Setup.ThemeProps`): the wizard plate draws the generated option rows (the layout cards, the density, the surfaces cards and its two artwork toggles), no legacy rows and no actions.

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

A switch that is *deleted* rather than moved (M1, page motion) needs no step: its stored value has no replacement, so nothing
copies it. The key is listed in `WVTH010.legacy.txt` (no code may name it again) and the stale value stays in the settings
file as inert data, never read. `Design.Nav.RecipeFor` is the one page-swap recipe (the 0.2.x fade-through, the plain fade
for Neutral), asked for by `Shell.RecipeFor`; a theme that wants its own page motion adds a structure verb in its own PR.

### M6: the colour washes became Classic's first option

`appearance.colorWashes.enabled` (a bool, default on) is now `ClassicStructure.Surfaces`, a `SelectOption<SurfaceWash>` {Off 0, Subtle 1, Rich 2}
(default Subtle; CT9 added Rich and draws it as three preview cards, `ClassicSettings.SurfacesCard`), stored as the int under
`theme.classic.structure.surfaces`. It is a *structure* option, not the plan's `style.surfaces`: the structure's material views read it and
WVTH005 lets a layer read only its own options (the style's roles do not change with it). The "ColorWashes" step in
`ThemeBootstrap.Steps` copies the stored bool (`false` becomes Off, `true` becomes Subtle) when the new key is absent; the legacy key stays as
inert data and is listed in `WVTH010.legacy.txt` with `Prefs.Appearance.ColorWashes` and `Platform.Keys.ColorWashesEnabled`. The Settings row is the generated
"Surfaces" row (the `colorWashes` legacy row, its catalog entry and its `SettingsDoc` writer are gone).

Who reads it: **the theme decides whether a wash is drawn, the core publishes data.** Classic's four cover leaves read the option themselves
(`UseContext(OptionStore.Context)` in `ClassicCoverLeaves.cs`: the tone plane and the artist blend paint nothing when Off, the tint binder
publishes a definite "no colour"), and `Shell.Material` (`ClassicMaterial.Template`, `ctx.Options`) paints the neutral ground whatever the
pages published. The hosts (`Detail`, `Artist`, `Profile`, `Home`, `Recents`) pass no flag: `Palette.PageTonePlane/ArtistBlendWash/ShellTint`
and the `Cover*Props` records lost their `disabled` parameter, and `ArtistProps.Washes` / `ProfilePageProps.Washes` are gone. The Recents
binder publishes its wash and page accent whatever the option says (the accent follows the artwork even with the washes off, owner NQ6).

**CT9: Rich and the two artwork toggles.** Rich takes the style's stronger pairs, `WashRoles.Rich` (a `WashStrength`: hero, shelf, tone plane, tint;
the reads take the level, `R.Wash.HeroAlphaAt(rich)` and siblings, `ShellTintFromScheme(scheme, rich)`); a style without the pair paints Rich at its one
strength. The tone plane stays at Subtle in the light theme while the navigation pane is docked (`ClassicStructure.RichTonePlane`, tuned item F2): the
frame provides the core's `SidebarPaneDocked` predicate as `ClassicMaterial.PaneDockedSlot`, read inside the plane's bound fill. Off also turns the
photo veil (`ClassicCoverVeil`) to the ladder's neutral rung. `accentFromArtwork` and `nowPlayingColors` are structure toggles in the same group; the core
follows them through `ThemePolicies.Artwork` (`ArtworkColorPolicy`, contract 1.7): `Detail.AccentFor` (every page accent, so Play, Follow, hearts, the
accent rule and links) and `Recents`' accent answer the system accent when the first is off; `StageInk.Accent` / `ArtStandIn`, the stage's visualizer
palette and the deck faces drop the cover's colour when the second is off. Classic's own reads (`ClassicOptions.AccentFromArtwork()` /
`NowPlayingColors()`) gate the search's top-result Play, the queue's now-playing chip and the panel's cover placeholder (`ClassicHeroWash`).

### M7a: the sidebar layout and density became Classic's options

`sidebar.layout.id` (0 Classic, 1 Library) and `sidebar.pane.density` (0 Default, 1 Compact) are now `ClassicStructure.Sidebar` (a `SelectOption<SidebarLayoutId>`,
Cards, `Command = true`, id `sidebar`) and `ClassicStructure.LibraryDensity` (a `SelectOption<SidebarDensity>`, Combo, id `libraryDensity`), stored as the enum ints under
`theme.classic.structure.sidebar` / `.libraryDensity`. They are *structure* options (another theme's sidebar is another structure) that use the core enums, so the core can
name a value. The "SidebarLayout" and "SidebarDensity" steps in `ThemeBootstrap.Steps` copy a stored value of 0 or 1 when the new key is absent, so a Library or Compact user keeps
it; any other value (2 exists only in profiles that ran the discarded PR 205 branch) is not copied and the default applies. The legacy keys, `Platform.Keys.SidebarLayoutId` and
`SidebarPaneDensity` are listed in `WVTH010.legacy.txt`. The v2 sidebar migration (`SidebarMigrationHost`, retiring with its own release) now hands its layout and density to the option
through `ThemeHost.Request` instead of writing the old keys.

How the core follows a choice it may not read (WVTH005): the structure publishes it as **`NavPanePolicy.Layout` / `Density`** (`ClassicPolicies.For(layout, density, hideTrackArt)`, one bundle per combination together with M4's `Lists.TrackArtwork`; one per
pair, the default pair being `Bundle` itself). `Sidebar.Boot` reads `ShellPolicy.Now.NavPane`; the frame root's effect calls **`Sidebar.FollowDesign(ShellPolicy.Tracked.NavPane)`**, which
brings the sidebar's live `Layout` / `Density` signals (the layout document, the undo ring and the menus read those) after an option that moved without a verb of the sidebar: the Settings
cards, the palette's option commands, a theme switch. A layout change that arrives that way while the sidebar is being edited ends the edit first (the verb refuses instead). The verbs
`Sidebar.SwitchLayout` / `SetDensity` (the pane's menu, the cards, the undo ring) keep their guards and ring records and store the choice with `ThemeHost.Request(NavPaneOptionIds.Layout /
Density, value)` (a theme without the option ignores it and the choice lasts for the session). Classic's Settings view draws `structure.sidebar` as the two preview cards (the generic Cards
row is text only) and `structure.libraryDensity` as the narrow combo it always was, and the options kept the icons of the rows they replaced. The density stays visible with either layout
(the plan said "only with Library", but the density shapes the Classic layout's entity rows too). The wizard's second page is the Theme page; the Layout page, `Settings.SidebarLayoutCards`
and the palette's "Use ... layout" rows are gone.

### M8: the page layout and the detail rails

`detail.page.layout` (0 Automatic, 1 Hero), `detail.rail.uniform` and the ten remembered rail sizes (`detail.rail.<album|playlist|liked|show|uniform>.width|collapsed`) left the core
settings. The choice of shape is Classic's *structure* options `ClassicStructure.PageLayout` (a Cards `SelectOption<DetailPageLayout>`, drawn with the two wireframe cards the old row had),
`RailUniform` (a toggle, shown only with the Automatic layout: the Hero composes no rail to size) and `ClearRememberedSizes` (an `ActionOption` with a confirmation, shown only with
Automatic and per-kind rails, greyed while nothing is customised), all in the group "Track pages". The **remembered sizes** are Classic's theme *state* (`theme.classic.state.rail.<scope>.width|collapsed`,
one `StateKey` per scope declared by the core host that writes them, `DetailRailPrefs.WidthKey/CollapsedKey`, clamped to the grip's range on every read and write; owner Q6: sizes are per theme).
Three `ThemeBootstrap` steps copy the stored values once (`DetailPageLayout`, 0 or 1; `DetailRailUniform`; `DetailRailSizes`, all ten), so an existing user keeps their layout and their rails.

How the core follows without reading an option (WVTH005): the structure publishes **`ThemePolicies.Detail`** (`DetailPolicy(PageLayout, RailUniform)`, `ClassicPolicies.For(..., pageLayout, railUniform)`, one bundle per
combination). `Detail.FrameHost` reads `ShellPolicy.Tracked.Detail` (Hero forces the vertical arm for a track page; uniform folds every kind into `RailScope.Uniform`) and `DetailRailPrefs.Epoch` (bumped once per
grip release, re-open or reset, so every mounted and parked frame re-syncs). The reset's behaviour is the core's (`DetailRailPrefs.ResetAll`, registered at boot as the `clearRememberedSizes` theme verb with an enable gate,
`ThemeVerbs.Register(id, run, enabled)`; `ActionOption.Confirm` makes the page ask first, so the action has no palette row). What stays core: the width ARITHMETIC (`RailPolicy`: the bounds, the page-aware cap, the
resting width, what a drag commits) because the skeleton and the host agree on it row for row; the plan's "RailPolicy becomes a Classic rule" would need every width through a published policy and moves nothing a second
theme could not simply reuse. `Prefs.DetailHero`, the twelve `Platform.Keys.Detail*` keys, the three legacy Settings rows, `SettingsVerbs.SetPageLayout/SetRailUniform/ResetRailSizes` and the three `ISettingsFacts` members are gone.

### M12: the right panel's sizes

`shell.rail.width` and `shell.rail.docked-video.height` left the core settings. They are Classic's theme *state* (`theme.classic.state.panelWidth` and `.dockedVideoHeight`; the keys are
`Shell.PanelSizeKeys`, declared in `ShellPanelHost.cs` because the host writes them; width clamped to the shell's baseline bounds, docked height 0..560 with 0 meaning "follow the 16:9 floor"). `Shell.PanelSizes` is the one
door (`LoadWidth`, `LoadDockedVideoHeight`, `SaveWidth`, `SaveDockedVideoHeight`) over `IPanelSizeStore`, now backed by `ThemeState`; the active `PanelPolicy` still narrows the width on load. Two `ThemeBootstrap` steps copy the
stored values once (`PanelWidth`, `PanelDockedVideoHeight`). The sizes are read at boot: a live theme switch keeps the committed signals (`FitWidthTo` only fits the width to the new policy, persisting nothing), and the next
launch loads the new active theme's own state.

### M7b: the sidebar pane's width and collapse and the Library view became theme state

`sidebar.pane.width`, `sidebar.pane.userCollapsed` and the Library's list/grid choice (`sidebar.json`, `layouts.library.sections[id=library].view`) are not options (the user does not choose
them in Settings, they are what the pane remembers), so they are Classic's theme *state* (`theme.classic.state.paneWidth` / `.paneCollapsed` / `.libraryView`): `StateKey`s declared by the core
host that writes them (`Sidebar.PaneWidthKey` 180-460, `PaneCollapsedKey`, `LibraryViewKey` 0-1), clamped on every read and write, per theme (owner Q6). The host stays the only writer: `Sidebar.Apply`
(a seam drag, the toggle, a keyboard step, "Set width") stores the width and the collapse; `Sidebar.SetLibraryView` (the toolbar toggles, the ⋯ menu, the narrow-pane fallback to the list) stores the
view. The live signals the views bind (`Sidebar.Width`, `UserCollapsed`, `LibraryView`) mirror the state, and `Doc.Library.View` is `SidebarLayoutRules.Resolve`'s new `view` input: the view is **no longer
a member of the section overlay** (`SectionState.View`, the `SetLibraryView` op and the DTO's `view` are gone). What the old overlay gave for free is kept by the host: the view is its own ring entry
(`SidebarUndoKind.View`), "Reset layout" of Library (`Sidebar.ResetLayoutRecorded`) and "Reset everything" put it back in the same entry, and `SidebarLayoutRules.IsModified(overlay, view)` still
shows "Library - modified" for a grid. Because the state is per theme, the frame root's effect calls **`Sidebar.FollowThemeState()`** when `ThemeState.Scope` (new: the active theme's id as a signal)
changes, so another theme brings its own pane and the next switch back finds Classic's. Three `ThemeBootstrap` steps copy the old values once (`SidebarPaneWidth`, `SidebarPaneCollapsed`,
`SidebarLibraryView`; the last reads the device file with a plain JSON read, `ThemeBootstrap.CopyLibraryView`, and never touches it; a demo never reads the real profile), so an existing user keeps
their width, their collapse and their view. The two settings keys and `Platform.Keys.SidebarPane*` are listed in `WVTH010.legacy.txt`; the v2 sidebar migration writes the three state keys instead.
The `Sidebar.Width` reads in the sidebar's views stay: they read the host's signal, not a setting.

### Worked example: row density and hide track art (M3, M4)

Two Classic switches became options in one PR. Row density (`detail.rowdensity`) is the **style's** `density` option (`Themes\Classic\Style\ClassicDensity.cs`:
a Cards select over `RowDensity` Compact / Default / Cozy / Comfortable, stored as the legacy number so a stored value carries over unchanged). Hide track art
(`appearance.trackArtwork.hidden`) is the **structure's** `hideTrackArt` toggle (`ClassicStructure.HideTrackArt`). Both live in Settings › Appearance
through `ThemeOptionsSection` (the group "Track lists"), have no `SettingsDoc` row any more, and left `LegacyRows`; the Classic Settings view draws the density's
cards as the miniature rows it always drew (`ClassicSettings.OptionChoiceCard`) and gives both rows their glyphs (`OptionGlyph`).

| | Density | Hide track art |
|---|---|---|
| What core reads | `StyleMetrics.TrackRows` (`TrackRowMetrics`: both skins' row height and art edge, the episode row, the rung, the episode row's narrow arm), published by `ClassicStyle.Metrics`; `Track.RowMetrics.Rows()` is the tracked read (the shell's Default rung when nothing is published) | `ThemePolicies.Lists.TrackArtwork` (`ListPolicy`), published by `ClassicStructure.Policies`; core models read `ShellPolicy.Tracked.Lists` (the track table's identity lane, the artist chart, the next-up rows, the stage's artist pane) |
| What a Classic view reads | the table's chrome calls the style's accessors (`ClassicDensity.Current`, `Request`): a structure may not read or request a style key itself (WVTH005, WVTH009) | `ClassicOptions.HideTrackArt()`, a tracked read of the option cell: the queue panel and stage pane, the Recents rows, the library search, the search hits, the artist page's chart skeleton |
| Where it is written | the Settings picker, the table's "Row size" slider (`ClassicTrackDensity`, through `ThemeHost.Options.RequestForwarder`) and its overflow submenu: all land in `ThemeHost.Apply` | the Settings toggle |
| Stored value | the legacy rung (0-3) is copied to `theme.classic.style.density` by the step `TrackRowDensity`; a value outside 0-3 is not copied (the default rung applies) | the legacy bool is copied to `theme.classic.structure.hideTrackArt` by the step `HideTrackArtwork` |

The row-size button keeps its overlay in the core (`TableDensityButton`: open, close, the word it holds while the panel is open) and asks the command kit for the
word, the panel and the radio rows. What the lists draw is unchanged, and so is the live re-key: a density change moves the list key's `d<N>` segment, a hide-art change drops the
artwork from the rows without re-keying the list (`LiveOptionBehaviourTests`). The facts are in `ClassicDensityAndArtTests`, including every stored value and the
existing-choice-wins rule. The old keys are listed in `WVTH010.legacy.txt`; `Prefs.Appearance.RowDensity` / `TrackArtworkHidden` and `Platform.Keys.RowDensity` / `HideTrackArtwork` are gone.

### Worked example: row style (M2)

The Modern / Classic skin of the track rows (`detail.rowstyle`, Settings "Track list style") is the **structure's** `rowStyle` option (`ClassicStructure.RowStyle`, a
Cards select over `TrackRowStyle` Modern / Classic, stored as the legacy number; group "Track lists", first in the card). It is a structure option because it picks which
of the structure's two row templates draws (`TrackListStyle.Modern` / `.Classic`, the queue's hairline rows, the artist chart's rows), and the style layer cannot read a
structure option (WVTH005), which is why `ClassicDensity.Rows` keeps publishing both skins' ladders and the core picks by the policy. Its `SettingsDoc` row, verb, catalog entry,
`Platform.Keys.TrackRowStyle`, `Prefs.Appearance.TrackRowStyle` and the props that only carried it (`ArtistPageProps.RowsClassic`, `QueuePanelProps.HairlineRows`) are deleted.

| | Row style |
|---|---|
| What core reads | `ThemePolicies.Lists.RowStyle` (`ListPolicy`, `ClassicRows`), published by `ClassicStructure.Policies` (`ClassicPolicies.For`: one instance per artwork x skin pair); the track table's snapshot (`ColumnSet.Classic`: the lane sets, header height, row height, the list key `:classic|:modern`) and the artist chart's metrics and cache keys read `ShellPolicy.Tracked.Lists` |
| What a Classic view reads | `ClassicOptions.RowStyle()` / `ClassicOptions.ClassicRows()`, tracked reads of the option cell: the queue panel (hairline rows), the artist page's chart skeleton |
| Where it is written | the generated Settings row (the two miniature rows, `ClassicSettings.TrackListStyleCard`), lands in `ThemeHost.Apply`; "Reset options" forgets it |
| Stored value | the legacy 0 / 1 is copied to `theme.classic.structure.rowStyle` by the step `TrackRowStyle`; a value outside 0-1 is not copied (Modern applies), a user who never changed it stays Modern |

**Left in the core, on purpose.** `ColumnSet.Classic` and the `classic` arguments of `Track.TableRules` (`IdentityColumns`, `TrailingColumns`, `HeaderHeightFor`, the relief ladder)
and of `ArtistChartStyle.Metrics` / `Skeleton` are the core's lane decisions over a skin the structure names; the plan's longer-term move of the lane table into the Classic rules
(a second structure with its own lanes) waits for the structure that needs it. The facts are in `ClassicRowStyleTests`; the re-key and both skins mounted are
`LiveOptionBehaviourTests`, `TrackRowsMountedTests` and `TrackChromeMountedTests`.

## 5. The analyzers (`Wavee.Analyzers`, WVTH rules)

All rules are errors and run in the normal Wavee build. Baselines (`src\apps\Wavee\analyzer-baseline\WVTHnnn.txt`, one
`path/suffix.cs[ :: detail]` per line) are **shrink-only**: an entry that matches nothing is WVTH900; a baseline file for a
rule that allows none is WVTH901. Rules that need a list read `src\apps\Wavee\analyzer-input\*.txt`.

| Id | Rule | Baseline |
|---|---|---|
| WVTH001 | `Wavee.Themes.*` referenced only along the allowed edges (App.cs, ThemeCatalog.cs, composition files, structure to Common) | none |
| WVTH002 | `Themes\**` may not reference `Entities`, `Playback`, `Spotify`, `Store`, `Shell`, `Prefs`, `Platform.Settings` | `Themes/Classic/Structure/LegacyPrefs.cs` only, shrink-only |
| WVTH003 | no literal looks (a number for a size/radius/gap/opacity, a colour built from components) in `Themes\*\Structure\**` and `Themes\Common\**` outside `Rules\**` | none (P3c: every Classic and Common view is clean; a file is WVTH901) |
| WVTH004 | no identity reads (`ThemeHost.Current`, `ThemeCatalog`, `ThemeComposition.Manifest`, `ThemeManifest` ids, layer ids, `ThemeDefinition.Id`) outside Composition, Options, ThemeBootstrap, App.cs, manifests | none |
| WVTH005 | an option is read or published only by its own layer (and Options/Composition) | none |
| WVTH006 | keys created only under `Theming\Contracts`; dotted unique ids; props are sealed records with read-only members | none |
| WVTH007 | `theme.*` settings minted only in `Theming\Options`, `ThemeBootstrap.cs`, `ThemeHost.cs` (`theme.mode` stays core) | none |
| WVTH008 | a `[CoreHost]` type builds no element and reads no `Tok.*`/`Design.*`/`R.*` (nor the live `StageInk.*`); every colour-role facade is a member of `R`, so it is fenced with the rest | none (T13 deleted it; a file is WVTH901) |
| WVTH009 | `ctx.OptionRequests.Set` only with own-layer keys | none |
| WVTH010 | retired keys and deleted members (listed in `analyzer-input\WVTH010.legacy.txt`) only in `ThemeBootstrap.cs` | empty after F4 |
| WVTH011 | each hidden structure option has a line with two or more values in `analyzer-input\WVTH011.matrix.txt` | none |
| WVTH013 | the tier fence: in `Themes\**` and `[CoreHost]` types, no comparison of a measured width (`Viewport.Size`, `UseMeasuredWidth`/`UseMeasuredBounds`, the width parameter of a `Responsive.Of` build lambda, a `TierFacts` width/height member) with a number or a constant declared outside `Rules\Tiers*.cs`, and no `Responsive.Of`/`UseMeasured*` call that feeds one (section 7) | none (a hard gate: a baseline file is WVTH901; TC10 deleted Classic's) |

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
| `ctx.Tier()`, `ctx.TierReader()`, `TierContext.DrawnIn` | `Theming\TierContext.cs` | how a template reads `TierFacts` (never a width); `TierReader` for a bound channel of a template that renders once (the frame); `DrawnIn(UseContext(ThemeFactsContext.Signal))` for a component |
| `TierRegion`, `ITierLayoutRecord`, `TierClip` | `Theming\Policies\TierRegions.cs` | the clip-region API of a tier layout record |
| Classic's ladders and frame records | `Themes\Classic\Structure\Rules\Tiers.cs` | `ClassicTiers`: the board's ladders (TC1), `ClassicFrameLayout` per window tier, `ClassicPageLayout` (the gutter) per content tier |

**An edge** is the engine's `TierEdge(At, NarrowHold, WidenHold)`: crossed upward at `At + WidenHold`, downward below
`At - NarrowHold`; a fresh resolve (a launch, a navigation, a first mount) uses `At`. An absent edge (`TierEdge.None`) leaves the
band above it unreachable, so a structure that draws no Huge 4K band lets its Huge band cover every wider window. Classic holds 40 on
widening at 528 and 1008 (to 568 and 1048) and on narrowing at 1920 (back below 1880); WinUI, Zune and Panorama will hold on narrowing.

**The sidebar's bands are the window tiers.** `SidebarPaneModeRules.BandOf(WidthTier)` maps Compact to Tiny, Small to Narrow and the rest to Wide; `NavPanePolicy.ModeFor(WidthTier)` is what the band forces. The pane has no edges of its own, so the frame seeds its band from the window tier of the first frame and derives `Sidebar.Band` from the tier signal. **The player bar's levels are a sub-ladder, not a tier ladder:** they are `PlayerBarPolicy`'s (section 2a), which carries each level's slots and spacing beside its start width and the narrowing hold, and `PlayerBarPolicy.Windows` clamps the level to the range the committed window tier may use (TC2). **The panel docks by tier:** `PanelPolicy.For(tier)` says whether the panel docks, floats or is a sheet and how many panels dock; the frame publishes it from the committed tier (`Shell.Ui.RailFits`, `PanelPlacement`, `SecondPaneAllowed`, `PanelFloor`), so docking never depends on a fit test.

**No jumps (plan 3.3).** A first mount resolves in its own frame. A crossing found after mount is a *pending* value in the host and
is committed on the first frame that is **not resized**, right before FLIP capture: the resized frame lays the OLD tier out at the new
size and snaps, and the new tier crosses on the next frame as an ordinary animated update (moved nodes FLIP, swapped subtrees
cross-fade). A composited edge drag therefore produces one crossing, at mouse-up. A size that is not positive and finite (a minimised
window reports 0) resolves to "keep the previous tier", so a restore returns to the tier the window left.

**A crossing keeps the parts it keeps.** A part that survives a crossing keeps its node, so its scroll offset, selection and focus
survive with it: never key a node on a tier, a form or a layout record. Where a control froze a prop at mount the prop is live
(PagedShelf's band, pager, card height and the rows of a multi-row strip; a bound ItemsView's responsive cell floor; `LazyGridFit`; NumberBox's style; ComboBox's size),
and where two forms differ only in arrangement they share one container whose shape is the tier's (a grid's track count, a box's
direction) so the children move instead of being reparented. Only a genuinely different tree swaps, keyed per family, under the shared
cross-fade. Classic's crossing recipes are `Rules\Tiers.Motion.cs` (`ClassicTierMotion.Reflow`, `.Shove`, `.SwapFade`, and `.Resize` for a part whose height alone changes, such as the artist chart's rows past the 4K rung: a flow reveal that the page below rides). Where the nav
pane changes (528, 1008) the content card is the one projection root of the commit and the page reflows inside its glide.
A swap reserves its region: the box around the two forms reflows its height (`ClassicTierMotion.Reflow`) and its siblings below glide, as Browse's
Charts band does between its paged deck (Standard, Huge) and its stack (Compact, Small): the forms are keyed `ClassicBrowseTiers.ChartsDeckKey` /
`.ChartsStackKey` and cross-fade, and every Browse band is a keyed reflowing box with its entrance on an inner box (Home's chapter shape). A card a
shelf builds is re-rendered only when its ITEM changes, so a card whose form follows the tier is a component that reads the facts it is drawn in:
Search's Best matches rows are `BestMatchRowHost`s (the row form of `ClassicSearchTiers.Of`, the shelf's live row height
`ClassicSearchLayout.BestMatchRowH`) and its rows per page (`ClassicSearchLayout.BestMatchRowsFor`) are live props of the one mounted shelf
(PagedShelf takes its row count live between multi-row counts and keeps the page holding its first item).
`Wavee.ShellTests\TierLayoutTests.Crossing.cs` mounts each page, scrolls and focuses it, crosses 528 and 1008 (and 1920 for Home) both
ways and asserts the same nodes, the kept offset and focus, and tracks on the crossing frame.

**Reading tiers.** Read the facts a view is DRAWN IN, never the process's: a template reads `ctx.Tier()` and its structure's layout record
for that tier; a component (a core host, a theme's stateful card) reads `TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal))` (`.Value` to
follow a crossing, `.Peek()` for a seed); a style builder the core calls outside any view (the track command kit, the hero skeleton, the
discography metrics) is handed the calling component's tiers (contract 1.23). A composition mounted at another size than the app window (the view
harness, a theme preview) draws in its own facts, so a read of the process's `ThemeHost.Facts` there is a bug. Only code that means the app
WINDOW itself reads `ThemeHost.Facts`: the overlay layer (`OverlayFit`, the context sheet's band, the toast lane), the title bar's field budget,
the frame's own tier publisher (`WindowTiers`), and the seeds that run before a component has a context (a constructor, the first `ApplyProps`). The fact is republished when a tier, a rung or `Resizing` changes, not once per resized frame, so the three size members
are the sizes *at that snapshot*; a reader that needs the live width reads `Viewport.Size`.

**Content width** is the window less the sidebar column the *logical* window tier presents (the pending crossing included, so the
content tier crosses together with the window tier) and the panels that tier docks (`WindowTiers.PanelsReserved`, `EstimateContentWidth`).

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
region from a control size). The rule has **no baseline**: Classic's grandfathered files were emptied family by family by the Classic tier PRs and TC10 deleted
the mechanism for it, so an `analyzer-baseline\WVTH013.txt` is WVTH901. `Themes\Common\**` is held to the rule like a structure; a Common block's breakpoint
constants, if it ever needs any, live in `Themes\Common\Rules\Tiers*.cs` (today it needs none: see "The Common blocks' tiers" below).

**Keyed measurement (A4.0, engine E17c).** A measured region in a view is KEYED by its role: `Responsive.Of("artist:top-band", w => ...)`,
`Responsive.Of("browse:genres", state, (st, w) => ...)`, `UseMeasuredWidth("show:body", 4f)`, `UseMeasuredBounds("recents:rail")`. The
content host provides the page slot as the scope (`Shell.MeasureScopeOf(route)` = the keep-alive slot key + `/`, as
`MeasureMemory.Scope` around `PageBody`), so the full key names one region of one page instance; when that page remounts (a theme
switch's new root, a revisit after a keep-alive eviction) the region builds its first frame at its remembered width, and the switch's
settle (`AppHost.Settle`) is quiet in its first pass. The role must be unique among the page's measured regions (a repeated block
folds its identity in: `CategoryBlock("browse-cats:" + uri, ...)`); a keyed `Responsive.Of` also uses it as the element `Key`. An
unkeyed hook still works and costs the settle one corrective pass. The WVTH013 fence reads the keyed overloads exactly like the
unkeyed ones. Pinned by `KeyedMeasureTests` (`Wavee.ShellTests`).

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

**Classic (TC1, board `C-Breakpoints`).** `ClassicTiers` (`Rules\Tiers.cs`) holds every number:

| Ladder | Edges | Holds |
|---|---|---|
| window | Compact < 528 · Small 528-1007 · Standard 1008-1919 · Huge >= 1920 (no Huge 4K band: Huge covers 3840) | 40 on widening at 528 and 1008, 40 on narrowing at 1920; a fresh resolve uses the edge itself, so 528-567 and 1008-1047 depend on the direction (owner NQ12) |
| content | the page gutter's: Compact < 600 (16) · Small 600 (32) · Standard 880 (36) · Huge 1600 (36) | 24 either way at 600 and 880, 40 on narrowing at 1600 |
| height | Short below 680, Regular from 720; `ShortFloor` 210 (330 less the 48 title row and the 72 player bar) | 40 on growing |

What each window tier lays out: **Compact** the drawer (no rail), the content card square and flush to the window's left edge with its
top hairline only, the title bar's tabs folded into one tab-count button whose menu switches tabs (`ChromePolicy.Compact`,
`TabsAsCount`); **Small** the forced 48 rail over the whole band, its ☰ opening the pane over the page; **Standard** the resizable
pane the user may collapse; **Huge** the same pane, resting at 320 while the user never resized it (contract 1.21: `NavPanePolicy.HugeWidth`,
`Sidebar.FollowTier`; "resized" is the `paneWidthSet` state, or a stored width none of the old defaults 280 / 320 / 340 explains), a title bar search
of up to 480 and tabs of up to 240 (`ChromePolicy.Huge`).
Forward hides at 520 and below at every tier. A view reads `ClassicTiers.FrameOf(ctx.Tier())` (the card) and
`ClassicTiers.PageOf(ctx.Tier())` (the gutter): **every Classic page insets its content by `PageOf(...).Gutter`**, never by
`Spacing.PageWide`; the tier-invariant rhythm is `ClassicTiers.PaneInset` (16), `HeadMetaGap` (4), `HeadTop` (24) and `RailPadding`
(24 / 8 / 24 / 16). The window's minimum height is `TierPolicy.WindowMinHeight` (330, `Shell.Host`; F8, #197).

**The player bar and the panels per tier (TC2; boards C-PlayerBar, C-PlayerBars, C-Shell-*, C-NowPlaying-Compact, C-Queue-Small,
C-Shell-Huge, the Huge 4K boards).** `Rules\Tiers.Player.cs` (`ClassicPlayerTiers`) holds the six levels (the sub-ladder 0 / 440 / 760 /
900 / 1100 / 1240, 24 narrowing hold, moved from `ClassicPolicies`) and one `ClassicPlayerBarTier` per window tier: **Compact** the 56 mini
bar (a 2-DIP progress hairline, the identity as one door to the now-playing sheet, the heart and the play disc at 40 x 40); **Small** the
Medium level (capped there; 528-759 keeps the Compact level, where the Medium row does not fit); **Standard** the production ladder
(Comfortable / Wide / Full); **Huge** the three-zone bar (360 identity zone with a 56 cover, the transport with shuffle and repeat centred
in a column of at most 1040, a 360 right zone with a 128 volume rail). A Short window keeps its tier's bar (72). `Rules\Tiers.Panel.cs`
(`ClassicPanelTiers`) places the panel: **Compact** a bottom sheet from 56 down over the page and the mini bar (grab strip, r8 top
corners, a 0.4 scrim that closes it; its now-playing arm adds Lyrics and Queue doors), **Small** floating 340 wide 8 in from the right,
the title bar and the bar, **Standard** one docked panel, **Huge** two (D3: the queue opens in a second pane right of the primary, both at
least 380, 420 at 3840). The frame's panel layer spans the content region and the dock; its hosts carry a layout transition, so a crossing
that docks, floats, sheets or adds the second pane animates on the crossing frame. At Compact the title bar carries no identity chip
(`ChromeBudget.TrailingInDrawer`): the drawer leads with the account row; the tab-count button drops whole (`TabsHidden`) where the row
cannot seat it, never clipped. The Compact row narrows the engine title bar's spacers (`TitleBar.RowSpacing`, `ClassicChromeLayout.CompactSpacing`: no 14 lead pad, a
16 drag strip instead of 48, budgeted by `ClassicPolicies.Chrome.Compact` with no centre-island gutter floors), so the count seats from 288 (every Classic window) and Back
joins it from 332; below that Back gives way first (four 44-DIP buttons and the 138 caption cluster are 316, more than the 300 floor).

**The Compact overlays are modal (the Compact overlays fix).** The drawer and the panel's bottom sheet live in the frame, so the page they cover is
a SIBLING under them, not an ancestor, and the engine's wheel routing (the containing-scroller scan) found it: their scrims and surfaces say
`BlocksBackgroundScroll`, so a wheel or a touchpad pan over them scrolls their own list or nothing, never the page. While open, focus moves into
them with Tab trapped there (`ClassicModalScope`: a dispatcher focus scope, focus restored on close), so PageDown and the arrows go to them too;
Escape closes the sheet (the arm's own key handler) and the drawer (the core host's key preview). A reversal mid-slide starts where the surface is
drawn (`ClassicOverlayMotion.From`), never from the far end. The drawer's pane is a column child that GROWS to the window's height: it sits in a
component anchor, which is a default column whatever row it is in, so a row's stretch never reached it and it measured its content (the account
row over a nav list whose scroller has no minimum height, so 0). The sheet's head is the shared drag (`ClassicSheetDrag`, see TC9's section): it
moves the primary panel HOST (its clip moves with it, so the sheet can rise the 8 DIP to the title bar's edge), while the arm under it keeps the
open/close slide, the pose handed over at the press and at a dismiss. The NOW-PLAYING sheet is its own column (C-NowPlaying-Compact,
`ClassicNowPlayingSheet`): the head (the grab strip over the sheet's own 48-tall header, `ClassicSheetHeader`: the close chevron 40, "Playing from album"
over the source's name, centred, a link to its page that closes the sheet first, and the "⋯" 40 that opens the now-playing track's menu, the bar's
right-click seam, as the context sheet; the developer-only style gear beside it), then in one scroll the art at `ClassicPanelTiers.SheetArtSide` (at most
320, the sheet's width less 16 a side, and whatever the fixed column under it leaves the window, floor 96), the title and the artists beside the
heart, the bar's seek rail and times, the transport (shuffle 40, previous 48, the 56 play disc, next 48, repeat 40; `PanelProps.Transport`, 1.20),
the Lyrics, Devices and Queue doors (three equal columns; Devices is named for the device playback is on and opens the bar's own picker over the
door, 14 under the transport row and 8 apart (`ClassicPanelTiers.SheetDoorTop` / `SheetDoorGap`; the board's `margin-top: 14px`); the source, the device and the
toggle are `PanelTransportProps.Source` / `.Device` / `.ToggleDevicePicker`, 1.22), then the lyrics peek and
the docked body's sections. A request for the device picker (a toast's "Choose device", the tray's
Devices: `PanelTransportProps.DevicePickerRequest`, the signal the bar's Devices button opens on) is answered by the sheet: the frame shows the now-playing
body if the sheet shows another or none, then asks the Devices door (`ClassicSheetPickerAsk`, once per ask) to open its picker, at once when that body is
already up and in the same step when it was not (the picker follows the door as the sheet slides up); at every other placement the bar's button answers as before. The other panel kinds as a sheet are the head over their own
scrolling body.

Still not moved out of core, because none is a ladder this primitive can state without changing behaviour: the chrome-row allocator
(it now takes the tier's budget), the stage aspect classes, the detail page-mode ladder and the artist hero's tiers
(the Classic artist and profile pages override the hero metrics' gutter with the page gutter).

**Testing the tiers.** `TierPolicyTests` pin the ladders and records; `Wavee.ShellTests\TierLayoutTests` mounts the real frame at every
plan 3.5 size and threshold edge (527/528, 567 from below, 1007/1008, 1047 from below, 1879/1880 from above, 1919/1920) and asserts the
tier in the first frame, the records in use, the four top-level regions inside the window without overlap, Compact targets of at least
40 and the ShortFloor at 330 tall. A lane adds its pages in its own partial file of `TierLayoutTests` (a `TierPage` fixture and a
theory over `PageSizes` that calls `AssertPage(page, size, more)`), never by editing the core file.
