# Theming: Zune

> **Scope.** The Zune theme (P5, Developer-only): what each lane built and how to extend it. The core is `theming.md`;
> the plan is `docs\themes\plan\technical-plan.md` (6.3, 9). Each lane writes only
> under its own heading below.

## Z0: the skeleton

The composition, the structure's skeleton, the tiers and the policies every family lane builds on.

**Files.** `Themes\Zune\ZuneTheme.cs` (the composition: id `zune`, `Zune.Structure` x `Zune.Style`, `ThemeAvailability.Developer`),
`Structure\ZuneStructure.cs` (options, registrars, capabilities), `Structure\ZuneOptions.cs` (the option enums, `ZuneParameters`, the
tracked reads), `Structure\Rules\Tiers.cs` (`ZuneTiers`), `Rules\ZunePolicies.cs`, `Rules\ZuneMotion.cs`. Installed in `App.cs` next to
Classic.

**Developer only.** With Developer mode off, `ThemeCatalog.Resolve("zune")` answers Classic; the stored `theme.id` is kept, so Zune comes
back when Developer mode does.

**Registering a family.** Each lane implements its own registrar in `Structure\ZuneStructure.<Family>.cs`:

```csharp
public sealed partial class ZuneStructure
{
    static partial void RegisterFrame(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ShellFrame, ZuneFrame.Template, "zune.shell.frame");
    }
}
```

The registrars are `RegisterFrame` (Z2a), `RegisterBand` (Z2b), `RegisterLists` (Z3), `RegisterPlayer` (Z4a), `RegisterStage` (Z4b),
`RegisterStageMore` (Z4c), `RegisterDiscovery` (Z5), `RegisterDetail` (Z6a1), `RegisterArtist` (Z6a2), `RegisterPlaylistLiked` (Z6b1),
`RegisterShowEpisodeConcert` (Z6b2), `RegisterCollection` (Z7), `RegisterOverlays` (Z8a) and `RegisterScreens` (Z8b).
`ZuneStructure.RegisterOwn` runs them all, then `CommonBlocks.Register` and Nothing (`CommonNothing`) for `Shell.NarrowDrawer` and
`Shell.Masthead`. Template ids start with `zune.`.

**The stand-in (until Z9).** A key nobody registers renders nothing, and a missing style key throws, so a half-built Zune would be a blank
window. `ZuneTheme.Create()` therefore gives the structure Classic's registration as a stand-in, and the structure registers it IN PLACE of
its own: picking Zune in Developer mode shows Classic's views under Zune's style, policies, tiers and capabilities. The engine cannot
merge two registrations key by key yet (`ViewRegistryBuilder.Add` throws on a duplicate; a built registry cannot be read back). Once it
offers `ViewRegistryBuilder.AddMissing(ViewRegistry)`, `ZuneStructure.Register` becomes "Zune's own views, then Classic's for every key
Zune lacks", and a family's views show in the app as soon as its lane lands. `new ZuneStructure()` (no stand-in) is Zune's own registry:
what the Zune registry tests read. Z9 removes the stand-in.

**Options** (stored as `theme.zune.structure.<id>`): `background` {ArtworkWash, Mix, None} (Live), `panoramaFit` {Bleed, Fit}
(Transaction), `bandPins` (Live). **Structure parameters** (Hidden: never shown or stored, a composition sets them; each has a WVTH011
matrix line): `collapseBand` (true), `pivotOverflow` {EdgeFadeScroll, MoreMenu}, `seekTimes` {InFace, AtSliderEnds}, `panelMode`
{InlineWhenRoom, AlwaysOverlay} (the enum is `PanelPlacement`, apart from the contract's `PanelMode`) and `maxDockedPanes` (1-3, default 3).
A view reads them through `ZuneOptions` (tracked); a policy hook gets them as `ZuneParameters`.

**Capabilities** (`ZunePolicies.Capabilities`): Tabs, NavTree and ForwardButton off, MaxBandPins 6, HistoryMenu and FriendsPanel on.

**Tiers** (`ZuneTiers`, board `Z-Sizes-Breakpoints`): window and content edges 640, 1008, 1920 and 3200, each committing at the line on
widening and held 24 on narrowing (Compact 300-639, Small 640-1007, Standard 1008-1919, Huge 1920-3199, Huge 4K from 3200; narrowing
commits below 616 / 984 / 1896 / 3176). Height: Short below 700, held 24. ShortFloor 126 (330 less the 48 title bar, the 84 band and the
72 bar). Rungs `pane.second` (1920) and `pane.third` (3200) on the window axis. A family adds its own ladders and rungs in
`Rules\Tiers.<Family>.cs` through the partial hooks `Ladders<Family>` / `Rungs<Family>` (Frame, Band, Lists, Player, Stage, Discovery,
Detail, Artist, Collection, Overlays, Screens); they run inside `ZuneTiers.Policy`'s initializer, so keep their numbers in their own class.

| window tier | gutter | title bar | band (collapsed) | play bar |
|---|---|---|---|---|
| Compact | 16 | 48 | 92 (52) | 64 (mini) |
| Small | 24 | 48 | 84 (52) | 72 |
| Standard | 48 | 48 | 84 (52) | 72 |
| Huge | 48 | 48 | 84 (52) | 72 |
| Huge 4K | 64 | 56 | 84 (52) | 88 |

`ZuneTiers.FrameOf(tiers)` is the record; the page reserves 72 for the bar at every tier (`ZuneTiers.BarReserveH`).

**Policies** (`ZunePolicies.For(ZuneParameters)`, one bundle per value set): no nav pane at any tier (NarrowMode and TinyMode `None`,
not resizable; the Wide band's pane never mounts because NavTree is off), `ZuneTiers.Policy`, and every other policy at the core's
Standard value (Classic's numbers) passed through a family hook: `BandPolicy`, `FramePolicy`, `PanelPolicyFor`, `PlayerPolicy`,
`StagePolicyFor`, `ListsPolicy`, `DetailPolicyFor`, `ArtistPolicy`, `CollectionPolicy`, `ArtworkPolicy`, `OverlaysPolicy`. A lane
implements its hook in `Rules\ZunePolicies.<Family>.cs`.

**Motion** (`ZuneMotion`): `PivotSlide` (250), `TitleScale` (200, the larger line height reserved), `ColumnFade` (120) then
`ColumnCollapse` (200), `GridCrossFade` (160) and `SwapFade`.

**Loc.** `theme.zune.name`, `.description` and `.options.*` are Z0's; each lane writes only inside its own object:
`theme.zune.{frame,band,lists,player,stage,discovery,detail,artist,collection,overlays,screens,states}`. nl and ko-KR fall back to English.

**Tests.** `Wavee.Tests\Theming\ZuneCatalogTests.cs` and `ZuneTierPolicyTests.cs`; `Wavee.ShellTests\ZuneHost.cs` (mount the app under
Zune) and `ZuneTierLayoutTests.cs` (the harness: Zune's size table and edges, the frame's assertions). A lane adds its page fixtures in
`ZuneTierLayoutTests.<Family>.cs`:

```csharp
public sealed partial class ZuneTierLayoutTests
{
    static readonly TierPage Album = new("album", new Shell.Route(Shell.RouteKind.Album, ...), "detail:");
    [Theory, MemberData(nameof(PageSizes))]
    public void The_album_page_holds_at_every_tier(string size) => AssertPage(Album, size, (shell, tiers) => { /* its record */ });
}
```

## Z1: the Zune style

_Not built yet._

## ZC1: the Browse band route (core)

_Not built yet._

## ZC2: "on this PC" (core)

_Not built yet._

## Z2a: the frame

Boards: Z-Frame-Home, Z-Frame-Panel, Z-Frame-Narrow, Z-Part-TitleBar, Z-Part-Panel. Registrar: `Structure\ZuneStructure.Frame.cs`
(`zune.shell.frame`, `.title-bar`, `.search`, `.search-suggestions`, `.command-palette`, `.history-menu`, `.file-drop`, `.drag-chip`,
`.cover-scrim`, `.content-state`, `zune.page.head`, `zune.shell.panel`). Every number is in `Rules\ZuneFrameRules.cs`.

- **The frame** (`Views\Frame\ZuneFrame.cs`): title bar / body / play bar in one window-tall column; the body is the page row (the page
  column `shell.content.card` = the band slot over the page, then the docked panel's reservation and the second pane's) with the panel layer
  over it. The band spans the page column only, so a docked panel runs the body's full height, title bar to play bar; the page column and
  the panel host FLIP together (300 ms, smooth-out) when it docks or leaves. Floating: 340 wide, flush right, from under the band
  (`ZuneFrameLayout.BandH`). No seam and no gap. The band slot hugs whatever `Shell.NavPane` draws (Z2b): nothing below depends on its
  height, so an empty band (the stand-in) is safe. The frame provides the page chrome signal as `ZuneFrameContext.PageChrome` (the title
  bar's trail reads it; the band may too).
- **The title bar** (`Views\TitleBar\ZuneTitleBar.cs`, record `ZuneTitleBarLayout` per window tier): `[wordmark · Back · trail] … [search ·
  account · bell] [captions]`, 48 tall (56 at Huge 4K), starting on the gutter. Search is the pill from Standard up (220 / 320 / 400, growing
  by 120 while focused), the magnifier below; the trail from Small up; the bell (with the accent dot) on the pill tiers. Every button is
  40 x 40. Back's history menu opens on right-click / touch-hold when `Capabilities.HistoryMenu`. The pane toggle follows
  `Capabilities.NavTree` (false under Zune); no tabs, no Forward, no pin, no friends toggle. A tier crossing and a navigation re-render the
  bar (they are folded into its content and tabs versions), so the non-client regions are re-pushed.
- **Search** (`Views\Search\ZuneSearch.cs`): the pill sizes itself from the record, not from the allocator's field width; the popup body is
  exactly the box's width and hangs from its left edge; square 36-tall rows, the accent wash on the selected row.
- **Palette, frame bits, content states**: Zune's parts (no boards). `Page.Head` collapses to nothing when hoisted (a hero head, or a page
  with row 2) and is one title (40/48/300) otherwise: the ancestors are the title bar's trail.
- **The panel frame** (`Views\Panel\ZunePanelFrame.cs`): the pivot head (queue · lyrics · now playing · friends, 26/32/300 lowercase, the
  current word in the primary ink) and ✕, over the body of the mode on show; the bodies are the player lane's keys and core hosts.

Tests: `Wavee.ShellTests\ZuneFrameMountedTests.cs`.

## Z2b: the band and the material

_Not built yet._

## Z3: the lists

`Structure\ZuneStructure.Lists.cs` registers seven list STYLES (the core hosts build the lists from them, `ListStyleLookup`); each wins over
Classic's stand-in key by key. The pictures are `Structure\Lists\Zune*.cs`, every number `Rules\ZuneListRules.cs`.

| Style key | Zune's picture (boards) |
|---|---|
| `TrackTable` | One row skin in both slots (Z-State-Rows, Z-Album, Z-Liked): the "#" in the Numeral role (22·300), the accent bars on the playing row (key `zune.row.now`), title / subline / every lane in the accent on the playing row, no plate; rest bare (no zebra, border, corner or pill), hover the 5 % ink wash, selected the 12 % accent wash, a drop target the 2-DIP accent line (key `zune.row.drop`). The chrome: lowercased 12/16 header words (key `zune.list.header`), flat square commands, the round accent play disc and ringed shuffle as the page bar's lead, a square filter card, the unavailable bar on the panel fill. |
| `EagerTrackRow` | The same cells over a bare hover container. |
| `EpisodeRow` | Square cover (Cover 2), the accent disc, bar, rule, "new" and "left" (Zune's one accent, not the page tone); no hairlines. |
| `Surface` | Tiles and plates radius 0, covers 2, people round; washes, never strokes; selected = 2-DIP accent border over the accent wash. |
| `LyricsLines` | Line level only (no word wipe), the active line in the accent, the core's opacity ramp, the translation at 0.625 in 300. |
| `ArtistChart` | 60-DIP touching rows, the rank in NumeralLarge (28·300), the 48 cover, the heading in the Heading role, square pager chevrons. |
| `TrackDrawer` | Flat and square under its row, Zune's type, the self row on the wash. |

Policies (`Rules\ZunePolicies.Lists.cs`): `ListPolicy` is fixed (Modern skin, artwork on: Zune has no row-style or hide-artwork option); the
detail table's records per window tier use the density's rows everywhere ("Standard rows": Compact form at Compact, no Album / Plays lane at
Small, every lane from Standard). The row height is the style's density (44 / 52 / 60), published as `StyleMetrics.TrackRows`; 4K keeps the
user's density (P5 decision 3).

Density: the "..." menu's "Row size" lists Zune's three densities and checks the PUBLISHED rung (`ThemeHost.Metrics`), never the style's
option cell, so nothing under Zune reads `style.density` as Classic's `RowDensity`. The resync pill is the lyrics view's (Z4c), not the lines'.
`Track.RowState.OnThisPc` is not drawn (Z9).

## Z4a: the player

_Not built yet._

## Z4b: the stage frame, Card and LargeArt

`ZuneStructure.Stage.cs` registers `Stage.Root`, `.TopBar`, `.Transport`, `.Options`, `.Gallery`, `.Caption`, `.Backdrop`, `.Face`,
`.FaceScrim`, `.Spectrum`, `.NowPlayingCard`, `.Identity` and `.Pane` as `zune.stage.*` (views in `Structure\Views\Stage\ZuneStage*.cs`).
The faces are core (owner Q21; Bloom is the default face, NQ3): Zune draws the frame around them. Z4c's keys (`Stage.Hero`, `.ArtistName`,
`.ArtistPane`, `Queue.StagePane`, `Lyrics.View`, `Video.*`) keep their slots in Zune's root.

**Policy** (`Rules\ZunePolicies.Stage.cs`, `ZuneStageTiers.Policy`): the core class edges; the full-bleed 112 plate at gutter 0 on Small,
Standard, Huge and Huge 4K (the allocator clears the plate exactly); Compact keeps the core's 104 stacked card at 16, the identity card and the
gallery on request. Lyrics type 36-64 (Small 30-36, Huge 48).

**Record** (`ZuneStageTiers.Stage(tier)`, `ZuneStageLayout`): the frame's gutter, the top bar's targets (40 at Compact), its right padding, the
pivots' size / line / gap (14/20/16, 20/28/20, 20/28/24, 22/28/28, 28/36/36), the label and look words, the glyph box, the disc (56 at
Standard, 52 Small, 72 Huge, capped by its row) and the volume line (128, 88 under Standard).

**Frame** (Z-NowPlaying, Z-Lyrics, Z-Queue-Page): the 48 top bar (label · lowercase pivots, 350 / 600 from `FacetTitleStyle` · word actions:
`layout · …` and `face · …` in Visualizer mode where `LookWordsFit`, `options` (the TeachingTip anchor), `exit full screen`; glyphs on the
icon-only classes), the flat transport plate (the stage veil at 0.85 over a face, opaque under a pane; seek row 36; disc in the accent;
heart · mute · volume line · device word · "…"; stacked when the clusters do not fit), the 2-px resting progress line.

**Arrangements** (`Rules\StageGeometry.Arrangement.cs`, `ZuneStageGeometry.Arrange`, pure and shared by every stage view):
`Plate` (the allocator's geometry), `Card` (Visualizer + Card on a wide stage, Z-NowPlaying-Card: the 600 x 376 well card at the bottom-left
gutter with the 220 cover, the bottom-aligned title column and the controls inside; mute · volume · device at the bottom-right; the caption
rises above the card; the face's safe rect stays above it) and `Column` (Visualizer + Large art on a wide stage, Z-NowPlaying-LargeArt: the
cover from 96 to the foot's gutter, the column 56 right of it with "playing from …", the lyrics pane, and the 368 foot block of titles and
controls; the spectrum under the cover or ringing it). An arrangement that does not fit is `Plate`. A switch keeps the stage root and the
transport's slot: the plates cross-fade (`ZuneMotion.SwapFade`) and the one cover node springs to its rect.

**Undrawn surfaces** (built from Zune's parts, Q18): the options popover, the gallery, the caption, the face chip. **Loc:**
`theme.zune.stage.{options,layoutWord,faceWord}`. **Tests:** `Wavee.Tests\Theming\ZuneStageGeometryTests.cs`,
`Wavee.ShellTests\ZuneStageMountedTests.cs`.

## Z4c: the rest of the stage

_Not built yet._

## Z5: discovery

_Not built yet._

## Z6a1: the detail frame and the album

_Not built yet._

## Z6a2: the artist page

_Not built yet._

## Z6b1: the playlist and Liked Songs

_Not built yet._

## Z6b2: the show, the episode and the concerts

_Not built yet._

## Z7: the collection

_Not built yet._

## Z8a: the overlays and the dialogs

_Not built yet._

## Z8b: the screens

_Not built yet._

## Z9: the states, "on this PC", the light pass and the completeness test

_Not built yet._

## ZT1: frame tiers

_Not built yet._

## ZT2: player and panel tiers

_Not built yet._

## ZT3: stage tiers

_Not built yet._

## ZT4: discovery tiers

_Not built yet._

## ZT5: detail tiers

_Not built yet._

## ZT6: artist and people tiers

_Not built yet._

## ZT7: collection tiers

_Not built yet._

## ZT8: screens, overlays, dialogs and states tiers

_Not built yet._
