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

_Not built yet._

## Z2b: the band and the material

_Not built yet._

## Z3: the lists

_Not built yet._

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
