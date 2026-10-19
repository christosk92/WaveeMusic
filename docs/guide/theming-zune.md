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
- **The panel's surface** (owner decision 2026-10-20, design "B, one surface"; `Views\Panel\ZunePanelWash.cs`, numbers in
  `Rules\ZunePanelWashRules.cs`): no plate of its own. The page's wash (the material's artwork wash, `ZunePageWash`) runs on under the panel
  and a scrim dims it (dark: near-black at 0.42; light: a faint dark veil), so page and panel read as one sheet with a slightly darker panel
  zone and a 1-DIP divider down its left edge. A hued cover adds its glow at the head over the scrim (cross-fading on a track change); a
  hueless cover, or nothing playing, shows the page's colour through the scrim. DOCKED the surface is a full-height column
  (`ZunePanelSurface`, mounted by the frame under the title bar and the body, as wide as the docked reservations): it runs from the window's
  top edge, so the title bar's bell, captions and the end of the search pill sit on it and no horizontal seam runs under the title bar; the
  layout does not change (the reservations are unpainted). FLOATING (and the sheet, and a docked panel sliding out) the panel is a card: it
  paints the same picture itself, opaque, picking the page wash up below its own top edge. The panel's inks (`ZunePanelInk`) are judged on
  the surface's colour where the head lies.

Tests: `Wavee.ShellTests\ZuneFrameMountedTests.cs`.

## Z2b: the band and the material

Registered by `ZuneStructure.Band.cs`; numbers in `Rules\ZuneBandRules.cs` (record `ZuneBandLayout` per window tier, board Z-Part-Band).

- **The band** (`Shell.NavPane`, `Views\Band\ZuneBand.cs`) draws from `NavPaneProps.Band` (contract 1.26) and reads the page chrome
  through `ZuneFrameContext.PageChrome`. Row 1: home · browse · library · recents as lowercase pivot words (the `Pivot` role; the lit one
  at `FacetTitleStyle.SelectedWeight` in the primary ink, the rest tertiary), starting on the frame's gutter, then "pinned" and up to six
  pins (art; a click opens, a hover-revealed play disc plays, a 4-dip accent dot marks the playing pin). Pins show at Standard and Huge
  only (P5 decision 4) and follow the `bandPins` option. Row 2: the page's views (Library's kinds, Browse's bands, in-page tabs), the
  current one at the selected weight; a detail page (no lit pivot) leads with its title and a hairline and underlines the current view in
  the accent; then the page's actions (play disc, shuffle ring, word pills). No views and no actions: no row 2.
- **Heights**: row 1 44 + row 2 32 + 8 = 84 at Standard (Compact 92, Huge 4K 112); row 1 + 8 = 52 without row 2 or collapsed (Huge 4K
  72). The band's root eases its own height over 250 ms (a reflow on the height axis, `ZuneBandRules.HeightMotion`); the rows keep their
  height and are clipped, so nothing jumps.
- **Row 2 on scroll** (`collapseBand`; owner decision 2026-10-20): PINNED at Small, Standard, Huge and Huge 4K: a scroll never moves it.
  At Compact it is scroll-TIED: the band gives back clamp(offset, 0, row 2) of its height (`ZuneBandRules.TiedFold`, from
  `BandProps.Scroll`'s offset) and row 2 slides up under row 1's foot and fades by the same share, so it scrolls away with the page and
  returns as the page reaches the top, continuously; no direction is read and nothing is timed, so it cannot snap or oscillate (the old
  fold on "last scrolled down" snapped the page at the end of its fade and fed its own height change back into the direction detector).
  While tied and off the top the band slot follows the band exactly (no ease); the last step back to the top eases. A Short window rests
  with row 2 folded, static. A page with no row 2 still eases the band 84 <-> 52 on navigation.
- **Overflow** (`pivotOverflow`): EdgeFadeScroll scrolls the pivot run under an edge fade (Compact also rotates the lit pivot to the front,
  the words sliding); MoreMenu, at Compact and Small, keeps the lit pivot and folds the rest (and the hub's profile and settings) into a
  "more" menu. Row 2 scrolls under a fade at Compact and Small.
- **Keys**: each row is a tab list with one tab stop (the lit word); Left, Right, Home and End move the focus; Enter opens.
- **The page scroll is reported by the content host** (`Shell\Shell.PageScrollFollower.cs`): `PageBody` wraps each page in a follower
  that finds the page's outermost vertical scroller (preferring one keyed with the page's scroll scope) and reports its offset through the
  page-chrome channel; a page that reports through `PageChromeSink.ReportScroll` itself still wins (the last report is followed).
- **The material** (`Shell.Material`, `Views\Material\ZuneMaterial.cs`): the flat ground (the style's shell ground), then by the
  `background` option: artwork wash (the published cover tint as a band over the title bar and the band), mix (the page's
  `Backdrop.Mix` covers, faint, behind the top of the window), none (the ground alone). Under artwork wash and mix the page's
  `Backdrop.Bleed` (an artist's header) lies faint behind the band and the head. Live.
- **Cover leaves**: PageTone **Own** (artwork wash: the page tone as a band fading out within the hero; else nothing), ArtistBlend **Own**
  (artwork wash: the style's blend ladder; else nothing), ArtistVeil **Own** (neutral, never tinted), TintBinder **Own** (publishes the cover
  tint under artwork wash and a definite "no colour" otherwise).
- **Nothing**: `Shell.NavLibraryHead`, `Shell.NavEmpty`, `Shell.NavSectionFlyout`, `Shell.NavFolderFlyout`, `Shell.NavEdit` (the core pane
  never mounts under Zune: `NavTree` is false).

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

`ZuneStructure.Player.cs` registers `Player.Bar`, `.SeekRail`, `.SeekChapters`, `.ChapterTip`, `.SeekReadout`, `.TimeLabel`, `.DevicePicker`,
`.OnMediaTransport`, `.NowPlayingHero`, `.DeckStyleFlyout`, `.LyricsPeek`, `Panel.Friends`, `Panel.LyricsHeader`, `Panel.NextUp` and
`Queue.Panel` as `zune.player.*` / `zune.panel.*` / `zune.queue.panel` (views in `Structure\Views\Player\*`, `Views\Panel\*` but the panel's
frame, `Views\Queue\ZuneQueuePanel.cs`). Every number is in `Rules\PlayerBarLayout.cs`; the levels and the per-tier records in
`Rules\Tiers.Player.cs`; the policies in `Rules\ZunePolicies.Player.cs`.

**The play bar** (Z-PlayBar, Z-Part-PlayBar, Z-PlayBar-Small / -Huge / -Compact, Z-Light-PlayBar): flat and square on the player-bar layer, a
2-px seek line along its top edge (full width; its 12 hit box reaches 5 either side, the 12 round accent thumb on hover) over the transport
row: the identity column (art · "playing on …" in the accent · title · artists · heart), the transport centred (previous · the round accent
disc · next, or −10 · disc · +10 · the speed word for an episode; the times under it) and the tools as words and glyphs (shuffle · repeat ·
mute · the volume line · lyrics · video ▾ · queue · devices · full screen · "…"). A level that drops a tool puts it in "…" (the core's
overflow). The `seekTimes` parameter at AtSliderEnds makes the seek row 16 tall with the times at its two ends.

| level | from | window tier | identity column | art | disc | tools |
|---|---|---|---|---|---|---|
| mini | 0 | Compact | (the door grows) | 44 | 40 | the disc and next (+10) |
| narrow | 640 | Small | 200 | 48 | 40 | mute · devices · "…" |
| medium | 760 | Small, Standard | 260 + heart | 48 | 40 | mute · lyrics · devices · "…" |
| full | 1240 | Standard | 320 + heart | 48 | 40 | all, both times |
| wide | 1920 | Huge | 420, 32 apart, 48 in | 48 | 40 | all |
| big | 3200 | Huge 4K | 520, 64 in, 44 targets, title 17 | 64 | 48 | all, words 15 |

The core walks the six levels with a 24 hold on narrowing and clamps them to the window tier (`PlayerBarPolicy.Windows`). The bar keeps the
frame's reserved 72 at every tier (88 at Huge 4K): the mini bar (64) sits at the foot of the same slot, so a 640 crossing moves nothing above it.
One element tree for every level and both forms: the art, the meta, the disc and each tool keep their keys and FLIP on a crossing; a tool
lights or dims by opacity; a line or a time that comes or goes fades. No key names a level or a form.

**The transport pieces** are shared with the stage's plate (Z4b) and the strip over a video: the seek rail (also the DVR edge, the breathing
live line, the mini bar's inert hairline), the chapter ticks hanging from the line, the tip and the readout on square raised plates, the time
labels (12/16, slot 40; the live slot's words lowercase).

**The panel's bodies** (Z-Part-Panel, Queue; the frame and the pivot head are Z2a's):

- `Panel.LyricsHeader`: no title and no close (the pivot head has them); the meta row under the pivots: "translate" (the second line),
  the AI sparkle, "full screen", the developer inspector. 28 tall, 40 at Compact.
- `Queue.Panel`: the mode words (shuffle · repeat · autoplay), "playing from …", the 72 now-playing row in the accent, the lane's headers at the
  foot of their 32 ("next in queue" · "clear", "next up from …", "autoplay"), the rows (the shared surface at the 36 art with the ✕), "show next".
  The lane's slot extents are exact (`ZuneQueueRules.RowExtent`, `Header`, `MoreExtent`).
- `Panel.Friends`: the 40 round avatar, the name, the track in the accent while live, the context, the time or the accent equalizer.
- `Player.NowPlayingHero` (no board): the cover (or, in developer mode, the deck) at the column's width, the title, the artists and album as
  links, the heart, the lyrics peek; `Player.DeckStyleFlyout` lists the presets and options as words.
- `Panel.NextUp`: the shared surface at the 36 art and 52 floor. `Player.LyricsPeek`: two 28 lines on a 2-px square accent spine.
- `Player.DevicePicker` (Z-PlayBar-Huge's "play on"): 340 wide, 52 device rows with the accent dot on the one in use.

**The panel's policy**: 340 wide and never resized (Min = Max), the full content height; a sheet at Compact, floating at Small, inline from
Standard, the queue docked beside it from Huge; `panelMode` AlwaysOverlay floats it at every desktop tier, `maxDockedPanes` caps the docked
count (the core docks at most 2 until ZT2).

Loc: `theme.zune.player.{devices,playingOn,playOn,playingFrom,nextUpFrom,translate}`. Tests: `Wavee.ShellTests\ZunePlayerMountedTests.cs`.

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

`ZuneStructure.StageMore.cs` registers eleven keys, each winning over Classic's stand-in: `Stage.Hero`, `.ArtistName`, `.ArtistPane`
(`Structure\Views\Stage\ZuneHero.cs`, `ZuneArtistName.cs`, `ZuneArtistPane.cs`), `Queue.StagePane` (`ZuneUpNext.cs`), `Lyrics.View`
(`Views\Lyrics\ZuneLyrics.cs`) and `Video.Docked`, `.Pip`, `.PopOut`, `.Fullscreen`, `.JoinPoster`, `.PlacementMenu` (`Views\Video\ZuneVideo*.cs`). The numbers
are `Rules\ZuneStageMoreRules.cs` (`ZuneStageHeroRules`, `ZuneStageArtistRules`, `ZuneStageUpNextRules`, `ZuneStageLyricsRules`, `ZuneStageVideoRules` with its per-window-tier
`ZuneStageVideoLayout`). The core keeps every decision, hook and state machine (the hero's facts, the pane's facts, the lane, the lyrics clock and follow, the media
pipeline); Zune changes the picture.

**Hero and name** (Z-NowPlaying-Artist): the stage's choreography is kept whole (the Ken Burns on a posed box, the cross-fade over the old artist, the blurred
cover until the overview answers, the next header decoded hidden in the last ten seconds). The scrims are Zune's: the stage veil at 0.35 at the top, clear by a
quarter, 0.3 at 60 %, the "Image dimming" foot; with the lyrics over the image, the left veil. The name is the display face at 300, -0.02 em, the ink at 0.92, a
quarter of the stage's height (72 to 300), two lines at most, bottom-anchored above the identity row; no eyebrow.

**Artist pane**: "about the artist" as the board has it (20/28 at 600, the bio 14/21 in the secondary ink, the word actions "follow" and "go to artist" under
it); the name, credits, Popular, Where people listen and Fans also like are built from Zune's parts (no plates, lowercase headings through the heading case, flat
rows, the 2-px accent line for the cities, square covers).

**Up next** (Z-Queue-Page): the "autoplay" word as the toggle; per section a heading in the display face (26/32 at 300) with a quiet word beside it ("clear" on
the user's queue, "from <context> · n more" on the continuation, the count on autoplay); 60-tall flat rows (the position in the display face, the 44 square cover,
17/22 title over 13/18 artists, the duration, a 40 remove target); the glass lights a row on hover. Every metric scales by the stage's queue scale and is rounded
as rendered (the lane's drag maths needs exact slot heights).

**Lyrics view** (Z-Lyrics): the same tree for the panel and the stage's reading column (left-anchored, capped at 1000 times the type scale). The lines are Z3's
(`ZuneLyricsLines`: the sung line in the accent). The view adds the "resync" pill (a flat plate with the countdown ring and the word in the accent, a target of
the window tier's height), the accent breathing dots, the video note, and the AI disclosure under the layers.

**Video**: the docked card is the hollow reservation (the hole rule: no transition, opacity or scale on an ancestor of the picture) and the watch page's 16:9
frame; the docked overlay's hover strip is words ("mini player", "full screen") and a close glyph; the mini player is square with the flyout shadow and no ring,
a 32 strip (the drag surface and the close glyph) and the eight resize zones; the fullscreen bar is the 48 bar of Z-Video ("video · full screen", the title, "exit
full screen"); the pop-out keeps its OS caption band; the poster dims the cover to 0.4 and shows the ring or the fault in lowercase label words; the placement menu is
`MenuFlyout.Create` over the core's decided rows (the plate is Zune's menu style). Compact shows the chrome at rest with 40 targets.

**Loc:** `theme.zune.stage.{aboutArtist,upNextFrom,upNextMore,videoFullScreen,videoMiniPlayerWord}`. **Tests:** `Wavee.ShellTests\ZuneVideoLyricsMountedTests.cs`.

**Gaps (no verb or prop reaches the view):** the lyrics "translate" word (the secondary-line choice has no verb on `ILyricsHost`; the lines draw the secondary line
when the user's choice says so); the Up next "shuffle" and "repeat" words (the transport has them; `QueueStagePaneProps` carries only the queue verbs); the Artist
pane's "more like this"; the fullscreen bar's "switch to audio"; the mini player's title and "back to wavee" and its bottom control bar (the on-media transport is
its own key); the docked strip's "turn off video" is a glyph, not a word, to fit the 296 card.

## Z5: discovery

`Structure\ZuneStructure.Discovery.cs` registers Home, Browse and Search; each wins over Classic's stand-in key by key. The pictures are
`Structure\Views\{Home,Browse,Search}\Zune*.cs` (the shared words and parts in `Views\Home\ZuneDiscoveryKit.cs`), every number
`Rules\Zune{Home,Browse,Search}Rules.cs` (one record per CONTENT tier: `ZuneHomeTiers`, `ZuneBrowseTiers`, `ZuneSearchTiers`; ZT4 tunes the
non-Standard rows). No discovery page draws a title or a facet row: the band is the head, and the page's views are its row 2.

| Key | Zune's picture (boards) |
|---|---|
| `Pages.Home` | Z-Home: the column on the frame's gutter, 8 under the band, capped at the style's page width, zones 28 apart. On All the quickplay strip leads; two neighbours that pair (release list + clusters, new + continue episodes) share a row 1 : 1 where the tier has room (`ZuneHomePlan.Rows`, pure). The facet swap is the production keyed fade over the dim host; the busy bar and a failed switch's bar lie over the content's top (they never move it); "customize home" closes All. |
| `Home.Quickplay` (new, contract 1.28) | pins (132 art, the band's own pins, the name in the accent while it plays) · history (`Home.Zone.RecentGrid`, through its core host) · new (the release list's first covers, its "see all"), 32 apart, wrapping. Mounted by the core host `QuickplayHost` (`Home\Quickplay.Host.cs`); Classic registers Nothing. |
| `Home.Zone.RecentGrid` | The history group: the tier's 100 tiles, the playing one in the accent with "playing" as its line, "listening history" to Recents. |
| `Home.Zone.<Kind>` (14) | A body draws its own heading row (26/32 light, subtitle, "see all"): shelves of 154 covers (`PagedShelf`, chevrons in the heading), the wide tiles the tier's columns of 16:9, the release list's 64 rows, the clusters' 28-title columns of 48 rows, the Browse tiles as flat 88 plates 2 : 1 beside the charts, the shared episode rows, the empty facet's vacancy. |
| `Home.Charts` | A numbered list: 40 rows, the 22 light rank, the chart's caption. |
| `Home.Daylist`, `Home.DaylistClock` | A 176 strip: the art 320, the copy (eyebrow, the 40/48 name, meta, the accent play disc and the outlined shuffle / save / "…" rings with their words), the clock column 260 (the bound count, five 40 bars with the current daypart 20 tall in the accent, the dayparts line). Stacked (art over copy over clock) at Compact and Small: the same three nodes. |
| `Pages.HomeSection`, `Pages.BrowseSection` | Undrawn: the "N items" line over the bound grid (154 cells, 16 apart) on the gutter. |
| `Pages.Browse`, `Browse.Charts` | Z-Browse: top (120 plates; undrawn, the board's row 2 held them), charts (the chart categories as two 120 plates) beside For you (a three-column list with the category's colour square), genres (a six-column list), mood & activity (six-across 96 plates), more (a list). The groups arrive filtered to the band's view (ZC1). `Browse.Charts` is the deck as a numbered list (registered; Zune's directory draws its chart categories as plates instead of mounting the deck). |
| `Pages.BrowseCategory` | Z-Browse-Category: the accent banner (200) with the name as a panorama and its kind, then shelves of 154 covers and six-across tile plates; the flattened modes keep the banner over a grid that owns its scroll. |
| `Pages.Search` | Z-Search: the facet body alone. All: top result (heading + kind, the tier's art beside the 40/48 name, its line, the matched words in the accent, the play ring and the hit's own control) beside best matches (two columns of 56 rows), the playlist shelf, genres and related searches as inline words. The other facets: the songs in columns, the Albums / Playlists virtual grid, the hit rows. |

**Nothing:** `Home.FacetRow`. **Common:** `Pages.HomeCustomize`.

**Row 2.** Home: the core host (`HomeHost.SyncFacetIndex`) publishes the facet words as `PageView`s ("all" first, the target's word lit, Go the
facet row's select path with its Following memory), a "following" / "all" action where the lit word has a Following sub-chip, and the shown
facet's first six covers as the page's mix backdrop; "all" alone before the words land, so the band keeps its two rows. It publishes under the
route on screen (Home's facet changes the route's argument inside one mounted page, and the channel keys a publication by it). Search: the core
host (`Search.PageHost`) publishes the facet strip with each facet's count (none for All and for 0); the lone All while the strip is skeletal;
a vacant query withdraws it. Browse's row 2 is route level (ZC1).

**Tests.** `Wavee.Tests\Theming\ZuneDiscoveryRulesTests.cs` (the plan, the records against the boards, every tier fitting its narrowest
window, Huge 4K = Huge, the core row 2); `Wavee.ShellTests\ZuneDiscoveryMountedTests.cs` (mounted at 1440 x 960: the registry, Home's page and
quickplay with pins, row 2's facets lit and switching, Browse filtering on a band switch, Search's row 2 with counts).

## Z6a1: the detail frame and the album

Boards Z-Album (Standard), Z-Album-Small, Z-Album-Compact, Z-Album-Huge, Z-Album-Huge4K, Album-Page / Album (studies), Z-Light-Album.
Registrar `ZuneStructure.Detail.cs`; views `Structure\Views\Detail\*` and `Structure\Views\Album\*`; numbers `Rules\ZuneDetailRules.cs`;
ladders `Rules\Tiers.Detail.cs` (`LaddersDetail`).

- **Composition.** A detail page is a panorama: the title across the top (`R.Type.Panorama` / `PanoramaCompact`, bleeding off the edge or
  fitting it per `structure.panoramaFit`), then up to three columns: the identity column (cover, artist, meta line, ring actions, the
  release's courtesy lines), the list (a "songs" heading over Z3's table), the aside ("more by", the artist, fans also like). The art wash is
  the material's: the frame places the core's tint claim and tone plane.
- **Records by content tier** (`ZuneDetailTiers.Of`): Compact = the vertical arm (the hero); Small 160 identity, no aside; Standard 240 / 260;
  Huge 320 / 400 ("more by" as a grid, the artist band); Huge 4K 400 / 440, 64 apart. A short window folds the panorama band (eased) and the
  identity column leads with the title. Pages cap at 2400 between the gutters.
- **Ladders** the core resolves by id: `detail.mode` (vertical arm below 640; 820 and 1008 split the core's rail modes), `detail.table`
  (300 … 860, re-admitted 24 past the edge), `detail.height` (700 / 900). Every edge holds 24 on narrowing.
- **Aside.** The core docks it by the window tier (`ZuneListRules.DetailTiers`: `AsideDocked` from Standard); the frame lays it out by the
  content tier and, where a docked panel leaves the page Small, folds it and provides `Detail.AsideDocked = false`, so its sections return
  under the rows. The release facts stay in the identity column (Z-Album's ℗ line), never in the aside.
- **Detail.Rail / CompactRail: own.** No grip (the column is the record's width). A collapse stored by another theme shows Zune's strip (cover
  and a 40 expand target).
- **Hero** (Compact): panorama, cover (0.42 of the column, 96 to 144) beside the artist and the meta, the 40 ring row (words from a 340 inner
  measure), the description, the list bar. Its height is one rule (`ZuneDetailRules.HeroHeight`) the skeleton reserves too.
- **Row 2.** `Album.Page.cs` publishes the views "songs" (current) and "more by <artist>" (the artist's page) and the actions play,
  shuffle and save / saved (the word follows the saved set) through the `PageChromeSink`.
- **Words.** Rings and headings the theme writes are lowercased by the case roles; a heading that carries a name ("More by Daft Punk")
  and every title or artist keep their casing.

## Z6a2: the artist page

Boards `Z-Artist` (Standard, the target) and `Z-Artist-Compact` / `-Small` / `-Huge` / `-Huge4K` for the other tiers' records (ZT6 tunes them). Files: `Themes\Zune\Structure\ZuneStructure.Artist.cs` (the registrar), `Views\Artist\ZuneArtistPage.cs`, `ZuneArtistHero.cs`, `ZuneArtistSections.cs`, `ZuneArtistBanners.cs`, `ZuneArtistLightbox.cs`, `ZuneDiscographyPage.cs`, `ZuneDiscography.cs`, `Rules\ZuneArtistRules.cs` (numbers, node keys, `ZuneArtistTiers`, the tier policy) and `Rules\ZunePolicies.Artist.cs`.

| Key | Zune's template |
|---|---|
| `Pages.Artist` | `zune.artist.page` |
| `Artist.Lightbox` | `zune.artist.lightbox` |
| `Pages.Discography` | `zune.discography.page` |
| `StyleKeys.Discography` | `ZuneDiscography.Style` |

**The hero is a panorama title, not a photograph.** 340 tall at Standard over the cover's blend wash (`Palette.ArtistBlendWash`, Zune's `ArtistBlend` leaf): the "verified artist" eyebrow, the name in the panorama voice (120/130 at the light 200; the 64 compact voice below 640; 150 at Huge), standing 8 left of the gutter so its glyph edge lines up with the eyebrow, and the caption ("#3 in the world" in the accent, monthly listeners, followers). `panoramaFit` decides how the name meets the window edge, as a live prop of one text node: Bleed lets it run off the hero's edge, Fit trims it with an ellipsis. A name keeps its own casing; the eyebrow and the caption are label words lowercased by the case role.

**The artist's photograph is the material's.** The core host (`Artist.PageHost`) publishes the page's chrome through the `PageChromeSink`: the pivot (one `PageView` per destination, the spy's answer current), play, shuffle and follow as the page's actions, and the hero image (the latched art identity) as `Backdrop.Bleed`, which Z2b's material paints behind the band under the artwork-wash and mix backgrounds. The host rebuilds the row only when the pivot, the lit tab, the follow state, the photograph or the name changed. Classic reads none of it.

**Actions follow the board's tier.** Standard: the band's row 2 carries them. Compact: a 40 row under the hero. Small: the hero's bottom right. Huge: a row of rings with their lowercase words under the caption. The follow pill is a 2-dip outline in the primary ink, the accent while following.

**The body.** Sections are 28 apart on the frame's gutter, capped at the page measure (1600; Huge 2400) on the page's left edge. Popular is the chart (Z3's style; the policy sets its columns and rows per tier) beside a rail (300; 360 at Huge; under the chart where there is no room) holding the upcoming release, the latest release (a 96 art row) and the artist pick (a 64 art row), all Zune surface rows. Upcoming and Latest release ride the rail whenever Popular stands (their own section holders are not drawn); without Popular, Latest release is a section. Albums, singles and compilations are a heading row ("albums", "N releases", "discography" at its end, opening the standalone page) over the core's grid. Appears on, music videos, playlists, concerts, merch, the gallery and fans also like are shelves of 154 covers 16 apart. The tour is a flat row. "about" is the biography (links in the accent), the external links, the cities as 4-dip bars and the profile facts as light numerals, beside the text at Standard and up.

**Production motion is kept.** One visual swap (overview, measured width and the chart settled) as a soft reveal over the derived shimmer, the chart's inner region in no group; the host's measure, readiness and entrance gates are untouched. The photograph's own fade is the material's.

**The discography (undesigned; built from Zune's parts, Q18).** The style draws the grid's cells with Zune's surface (square, a wash on hover, a disclosure cue on the meta line, the selected skin when open), the drawer as a flat floating-pane panel with a filled caret, a play ring, the cover, the title and an open ring in its head, Zune's own eager row for the tracks, the selection as a 12 % accent wash and "show all N tracks" as an accent word. The standalone page is the artist crumb, a 40/48 light title and the release-type words as a sub-pivot row (the current one in 600 under a 2-dip accent line), over the same grid at a 24 reveal inset. The lightbox keeps Classic's mechanics (pager, pan/zoom, auto-hiding chrome, filmstrip) in Zune's parts: an outlined export pill and a close ring.

**Tiers.** `ZuneArtistTiers` has a record per content tier (hero height, the name's voice, where the actions stand, the chart's columns, the rail, the drawer's columns, pitch and head). Huge 4K is Huge's (decision 2). Every size is sane from 300 up; ZT6 tunes Compact, Small, Huge and Huge 4K against their boards.

**Tests.** `Wavee.ShellTests\ZuneArtistMountedTests.cs`: Zune registers the family; the fake artist `ar0` renders Zune's page at the record's hero height with the popular band and the facets; the bleed and the row 2 are published; the discography page mounts its column and words.

## Z6b1: the playlist and Liked Songs

Boards Z-Playlist-Owned-Followed (+ -Small, -Compact, -Huge), Z-Playlist-Followed (+ -Small, -Huge), Playlist-Page (study), Z-Liked (+ -Small, -Compact, -Huge),
Liked (study), Z-State-Empty-Playlist (+ -Compact). Registrar `ZuneStructure.PlaylistLiked.cs`; views `Structure\Views\Playlist\*` and `Structure\Views\Liked\*`;
numbers and tier records `Rules\ZunePlaylistRules.cs` (`ZunePlaylistRules`, `ZunePlaylistTiers`).

- **The frame is Z6a1's.** A playlist and Liked Songs are detail pages: the panorama, the identity column, the vertical hero, the "songs" heading and the aside are
  `Views\Detail\*`. This family draws what the pages hand the frame as slots and the keys it mounts: `Playlist.*` (ten) and `Liked.*` (four).
- **Cover.** `Playlist.CoverArt` is a 2 x 2 mosaic or the art at the Cover shape (no saturation boost). `Playlist.CoverEditor` wears a strip along its foot ("change
  cover"; "drop image to change cover" while a file is dragged anywhere or over it; the status while a write is out), 28 high at Compact and 36 elsewhere, always
  there (a touch window has no hover); an empty playlist's cover is the dashed "choose a cover" tile (Z-State-Empty-Playlist). The whole cover is the file-drop
  target and the click-to-pick.
- **Editor.** The title and the description are the editor: the read arm is the text with a hairline under it that rises on hover (and the pencil), the edit arm is
  the engine's field over two words, "save" in the accent and "cancel". The status ("saving" / "saved") stands below the box so the measure never moves.
- **Owner.** The avatar and the name in the artist voice (18/24, a link) or the collaborator pile, and the invite ring (the detail kit's outline disc with a plus,
  36 / 40) at the row's end. The row reserves the tallest arm's height, so flipping "collaborative" moves neither the ring (the access panel is anchored to it) nor
  anything below. The collaborator flyout is the album's every-artist flyout.
- **Access and Tune** are flat panels (no board; Q18): words, rules, a flat accent call to action, the engine's switch; the current tuning is marked by a 2-dip
  accent bar and "current", not a disabled look.
- **Daylist.** HH:MM:SS in the numeral voices (22/28 in the rail, 28/36 in the hero), every digit in a fixed cell; the ring stands in past the window, at the same height.
- **Recommended songs.** The heading (26/32, lowercased), a "refresh" word (a spinner while asked), rows with a 40 cover and an outlined 28 "add" pill (40 hit edge at
  Compact). An EMPTY playlist leads the section with the board's sentence ("let's find something for this playlist": 40/48 display; 26/32 at Compact), a line and the
  field that finds songs to add (560 square at the wide tiers, the full-width pill at Compact); the lead reserves its height and eases a change of it.
- **Liked Songs.** The wide arm has no cover (the frame's head arm: the panorama and the meta over the list); the cover is drawn where a page hands one over (the
  vertical hero, the Home and library tiles) and is FLAT: the bundled stock cover, a flat mosaic of the newest likes or the accent plate with the heart. The engine's
  other six treatments are Classic's ambient loops and are not drawn: a persisted look Zune does not draw reads as the mosaic, and the picker (a "cover style" strip
  on hover or focus, a three-row flyout) writes the engine's persisted index. The chip rail is Zune's flat chips (a wash at rest, the accent fill on), 32 tall, 40 at
  Compact; "all" takes the label case role, the tags are never re-cased. Day groups are the table's (Z3).
- **Row 2.** `Playlist.Page.cs` publishes the view "songs" (current) and the actions play, shuffle (disabled while the list is known to be empty) and, on a playlist
  that is not the viewer's own, save / saved, through the `PageChromeSink`. `User.Page.Liked.cs` publishes play and shuffle and no views (the library route's row stands).
- **Tiers.** `ZunePlaylistTiers` has a record per content tier (the hit edge, the cover strip, the lead and its field, the duration lane, the detail record the rings
  read). Standard is the target; ZT5 tunes the rest. Every size is sane from 300 up.
- **Tests.** `Wavee.ShellTests\ZunePlaylistLikedMountedTests.cs`: the registry draws all fourteen keys; pl1 draws Zune's frame over Zune's table and publishes its row 2;
  a Compact window draws the vertical arm; Liked Songs draws the head arm and publishes play and shuffle; the cover looks and the tier records are pinned. The fake
  dataset has no empty playlist, so the lead is pinned through its rules.

## Z6b2: the show, the episode and the concerts

Boards `Z-Show`, `Z-Episode`, `Z-Concert` (Standard, the target) and their `-Compact` / `-Small` / `-Huge` twins. Files: `Themes\Zune\Structure\ZuneStructure.ShowEpisodeConcert.cs` (the registrar), `Views\Show\ZuneShowReader.cs`, `ZuneShowHead.cs`, `ZuneShowRail.cs`; `Views\Episode\ZuneEpisode.cs`, `ZuneEpisodeReader.cs`, `ZuneEpisodeDiscussion.cs`; `Views\Concert\ZuneConcertCells.cs`, `ZuneConcertDetail.cs`, `ZuneConcertHub.cs`, `ZuneConcertFilterBar.cs`, `ZuneConcertFlyouts.cs`, `ZuneConcertPicker.cs`, `ZuneConcertSchedule.cs`; numbers and records `Rules\ZuneShowRules.cs` (`ZuneShowRules`, `ZuneEpisodeRules`, `ZuneConcertRules`, `ZuneConcertTiers` / `ZuneConcertLayout`). Zune's registrations win over Classic's stand-in key by key.

| Key | Zune's template |
|---|---|
| `StyleKeys.ShowReader` | `ZuneShow.Style` (rail primary, ledger, rating, badges, topics, publisher, satellites; the toolbar, the visit head, the headers, the foot, the seed) |
| `Episode.Primary` `.Attribution` `.Actions` `.Save` `.RailAbout` `.Rating` | `zune.episode.*` |
| `Episode.Reader` `.About` `.Chapters` | `zune.episode.reader` / `.about` / `.chapters` |
| `Episode.Discussion` `.CommentCard` `.CommentSeed` `.LoadMore` `.ReactionFlyout` `.ReactionRoster` `.ReactionPeople` | `zune.episode.*` |
| `Pages.Concert` | `zune.concert.detail` |
| `Pages.Concerts`, `Concert.Tail` | `zune.concerts.hub`, `zune.concerts.tail` |
| `Pages.ArtistConcerts`, `Concert.PlacePicker` | `zune.concerts.artist-schedule`, `zune.concerts.place-picker` |

**The show** stands in Z6a1's detail frame. The rail's slots: the publisher link (15), the badge words ("exclusive · E · video", the accent on exclusive), the rating line, the played LEDGER (a 3-dip bar: played in the primary ink, in progress in the accent, the rest the track, its parts easing their widths; "84 played · 3 in progress · 41 to go · 5 new"), the RING row (the accent play ring with its words, "resume" over "23 min left", or "follow" with a ghost play; save, trailer, share and "..." after it) and the topic words. The rings' size is the tier's (36, 40 at Compact and 4K) and a slot template has no view context, so each ring is a small component that reads the facts it is drawn in. The reader is the core word rail under the accent underline (the stage rule's measure is the core's), the visit head's cards are flat (a 72 cover, the accent label, the title, "6 min left · #808", a 2-dip track and a 40 ring), the "episodes 812" heading is the 26/32 heading voice on the core's stated 30 line, a month the 22 light group voice on the stated 24 line. The core's arithmetic (48 rail, 30 + 12 header, 14 + 24 + 4 month, 30 strip) is untouched.

**The episode** is the frame's two columns (the cover, the show link, the ring row: resume over its minutes, save, the played mark, share, "...") and Zune's reader: about · chapters · transcript · comments STACKED in one scroll under 26/32 light headings at a 760 measure, the description at 15/22. Chapters are the BIG-NUMERAL rows (a 22 light numeral in a 44 column, the title at 15, the start time at 12; the playing chapter in the accent on the selected wash). The reader draws no pivot: the four words are the band's row 2.

**Row 2** (all through the `PageChromeSink`, Album's pattern): the episode (`Episode.Page.cs`, `EpisodeReader`) publishes about · chapters · transcript · comments, the spy's section current, a click scrolls it; the show (`Show.Page.cs`, `PageHost`) publishes "episodes" and the action follow / following (the word follows the saved set); the concert (a zero-size component beside the page's scroll) publishes details · lineup · related concerts, each only when the page holds it, with its own spy. Gated on what each row is built from.

**The concert page** (the designed one): the caption ("concert · on sale"), the title in the hero voice (64/72; 40/48 at Small; 150/160 at Huge; a name, never re-cased), the date as a big light NUMERAL (64, 150 at Huge) over "november · saturday", the facts as sentences in the provider's clock ("20:00 · doors open 19:00", venue · place, "ages 18+"), the TICKETS SLAB (the raised plate: one row per offer, provider, price and a round accent ring that buys, or the quiet "tickets not available online"; the footnote says the prices are the providers'), the lineup (round faces, rows with a chevron below Standard) and the related concerts as date cards (a 4-across grid on the plate at Standard, rows below). Compact stacks the slab and bleeds it to the gutters; Small seats a 260 slab beside the facts; Standard 320; Huge 440.

**Undesigned (owner Q18), built from Zune's parts.** The hub: a head (the accent "live music" eyebrow, the 40/48 light title, a quiet line), the filter row PINNED under the band on the ground (no card; round outlined pills, the stock genre toggles, the fused accent pill keeping the segment grammar, the events ticker), the feed (shelves of flat event tiles, playlist promos, the virtualized "all events" grid at a stated tile extent, the tail). The tour dashboard: "on tour", the hero-voice name, the stats, the next-show row (a light numeral, the accent "next show · in 3 days"), "view event" beside the round location control, the spotlight's photo; "tour dates" over the month tabs and ONE month board (two balanced columns at Standard and up, "show all N" over six tiles, a run expanding in place) and the shy month pill. The place picker and the two filter flyouts: square rows on the wash.

**Words and casing.** Everything the theme writes (headings, rings, captions, quiet lines, a month, a weekday) is lowered by the case roles; a title, a venue, a place, an artist and a genre keep their casing.

**Decided here.** `Concert.Tail` and `Concert.PlacePicker` are Zune's own (not Nothing). The stand-in's disabled new-episode bell has no ring in Zune. `ConcertDetailView` gained `Date` (the show's date and time as a value, the contract addition).

**Board versus contract / plan.** The boards lay chapters and transcript side by side; the reader's contract is a stack of anchored sections read by one spy (two sections would share a top), so they stack. The boards put the episode's title (40/48) above the about; the frame's panorama (and row 2) already carries it. The boards draw no panorama on a show or an episode (the title in the rail); Z6a1's frame draws one on every detail page. The board's "about" tab on a show has no target: the rail already carries the description.

**Gaps with no verb in the props.** The concert page's ring row (share and play the lineup are wired; "interested" is a LOCAL mark since contract 1.46 (`ConcertDetailView.Interested` / `.ToggleInterested`, the band's word too: the concert API has no such state); add to calendar has no verb) and the Huge board's "in 34 days" and capacity facts: the page carries neither a uri nor a verb for them, so nothing is drawn. The show's "..." menu sits in the rail's ring row (the frame's), not in row 2 (a `PageAction` is a word). The show toolbar's glyph toggles stay 30 wide (the core's stage rule sums them), so below 40 at Compact: a core follow-up.

**Tiers.** `ZuneConcertTiers` has a record per content tier; the show and episode read Z6a1's detail records. Every page is correct from 300 to 3840; ZT5 tunes the finer tiers.

**Tests.** `Wavee.ShellTests\ZuneShowEpisodeConcertMountedTests.cs`: the registry (Zune's own wins over the stand-in), the concert records, the show (toolbar, rail ring, row 2), the episode (stacked sections, four words in row 2, the 40 ring at Compact), the event page and the hub, the 400 and 3840 fits.

## Z7: the collection

Library, Recents and the profile (`ZuneStructure.Collection.cs`; boards `Z-Library-{Albums,Artists,Playlists,Podcasts,Audiobooks}`, `Z-Recents`, `Z-Profile`, each with its Compact / Small / Huge twin).

**Keys.** Own: `Pages.Library`, `Library.AlbumPane`, `Library.ShowPane`, `Library.ArtistReader`, `Library.SavedEpisodes` (`Views\Library\`), `Pages.Recents` (`Views\Recents\`), `Pages.User` and `Pages.ProfileList` (`Views\Profile\`; the list has no board, owner Q18). Common: Module. Local files renders through the playlist page.

**The playlists route (core).** `RouteKind.LibraryPlaylists` (`playlists`) is a new exact route. Its page is `User.LibraryPlaylists` (`Entities\User.Page.Playlists.cs`): the rootlist tree the sidebar plans from (`Sidebar.Model.Rootlist`: playlists and folders, folder expansion persisted), the sidebar's covers and subtitles, the create-playlist and new-folder verbs. It is core-drawn with the style's roles (no view key, so Classic needed no change), pumps the nav model itself (the pane that normally does is not mounted under Zune), and is the second view of the library's row 2 (`User.LibraryChromeViews`: liked, playlists, albums, artists, podcasts, audiobooks). Under Classic nothing links to it, but navigating there draws the list. It is registered through its own lazy group (`Shell.InstallLazyGroupFor`).

**The library page.** The kinds are the band's row 2, so the page draws no title. Under the band, on the gutter, 8 down: a head of words (sort: the kind's words, a re-tap flips the direction; view: grid and list and a "..." menu of the four views and the S/M/L steps; the find field; the count), 16 over the body. Albums, Podcasts, Audiobooks: the navigator (a grid of covers or a list) takes the width, the pane is 380 beside it, 40 apart. Artists: a 220 list, then the reader (the band, the pinned sub-rail of scope and sort words, a block per release, the cover spine). A typed filter swaps both for the search columns. Compact folds the columns into the breadcrumb drill-in (the library layout ladder `library.layout`: split from 640 of the page's own width, held 24 either way). There are no grips: the columns have the tier record's widths (`ZuneLibraryTiers`). A crossing between the arms keeps the user's place through the host's `ApplyNavPlace` hand-over; the arms cross-fade.

**What the core fixes.** The navigator's row pitch (a 56 plate in a 60 pitch, 40 in 44), the letters' arithmetic and the reader's block extents (`Artist.ReaderShape`) stay the host's, so the artists' list rows stand 44 apart (the board's 34), and the reader keeps its geometry (re-voiced: rings for the verbs, words for the rails).

**Recents.** The band's row 2 carries the pivots (all, music, podcasts, artists) and the "overview" word (published by `Recents.PageView`); the page is the day-grouped list with the accordion and the 300 overview column beside it from Standard. **Profile.** The page's sections and the people list's facets are the band's row 2 (published by the profile hosts); the page is the hero, then shelves and ranked top-artist rows.

**Loc.** `theme.zune.collection.{library,recents}`. **Tests.** `ZuneCollectionMountedTests*` (mounted at 1440 x 960), `LibraryPlaylistsRouteTests`, `ZuneProfileRulesTests`, the updated `PageChromeRowTests` and route-count facts.

## Z8a: the overlays and the dialogs

The account chip and menu, the context menu's header and round-verb strip, the Compact context sheet, the notification panel, the toast card, the runtime banner, the offline strip and vacancy, and the log-out, play-a-link, signature, destination-picker and credits dialogs. All fourteen keys are Zune's own (`zune.overlay.*`, `zune.menu.context-tile`, `zune.dialog.*`). The state stays core: which overlay is open and where it anchors, the dismiss rules, the focus traps, the toast queue (the engine's `ToastHost`: at most three, the stacking, the clock, the action word), the dialog shells (`ContentDialog`) and every verb. Boards: `Z-Overlays`, `Z-Overlays-Small`, `Z-Overlays-Compact`, `Z-Dialogs` and its tiers; no board draws the log-out card, the play-a-link card, the signature sheet, the destination picker, the credits sheet or the Compact context sheet, so they are built from the same parts (owner Q18).

**The grammar.** Flat plates with words on them. A menu or panel is the raised plate (`FillSolidTertiary`, #242424), a dialog the dialog plate (`FillLayerAlt`, #1f1f1f), both square with the flyout / dialog shadow and no ring. A heading is the 26/300 display voice, a command a lowercase word (`ZuneText.Command`, `ZuneKit.Cmd`), the primary command the accent disc with its word beside it (`ZuneKit.Go`). The one round thing on a plate is a person (an avatar) and a verb.

**The context menu.** The menu is the engine's `ContextMenu`; `ZuneControls.TrackMenu` hands it two presenter parts drawn in `Views\Overlays\ZuneContextMenu.cs`: the header (a 40 tile, the title 15/20 over the subtitle 13/18, no divider) and the strip of round verbs (the first, a plain command, is the accent disc; the others are 2-DIP rings; a toggle that is on wears the accent ring and its filled icon; 32 across, 40 for a touch menu; 18 apart). The tile is `Menu.Context` (a 2-corner square, a circle for an artist). At Compact the policy says menus are sheets (`OverlayLayout.ContextSheet`): `ZuneContextSheet` draws the same header and verbs (40 across) over 48-DIP word rows on a card on the window's lower edge, under a 72 % scrim that starts below the title row. There is no grabber and no drag: a sheet closes by its scrim, Escape or a row, and a row closes it first, then acts.

**The tiers.** `Rules\ZuneOverlayRules.cs` holds the numbers and ONE record per window tier (`ZuneOverlayTiers.For(window)`), read by the views and by the core hosts through the policy (`ZunePolicies.Overlays` -> `ThemePolicies.Overlays`). Compact: the sheet, the toast across the window less 16 above the 64 mini bar (a 40-DIP action word), cards that fill the window less 16 with 16 / 20 padding, 40-DIP fields and commands, the quiet vacancy, the offline strip 40 tall with the board's short notice. Small and Standard: the toast bottom right 12 above the bar, 360 wide (the policy's lane: the shell provides it over the control table's), the 548 dialog plate, the strip 36 tall. Huge and Huge 4K: the same, with the report and crash dialogs' two-pane 1040 card (`DialogFit.FormWidth`; Z8b's `ZuneDialogTiers` reads it). A panel is never wider than the window less the tier's margin (16, 8 at Compact) nor taller than the window less the title row; the feed scrolls inside it.

**Rules kept.** No layout jumps: the unread dot and the playing status are reserved columns, the play-a-link status line keeps its 18, the banner is a zero-footprint box when gated off. Words are cased by `R.Case` (a label, a navigation word, a heading, a command); names, titles and the engine menu's rows are never re-cased.

**Conflicts with the engine (reported).** The engine's anchored menu rows, the account menu rows, a toast's action word and an info bar's severity tint come from stock controls: their labels cannot be lowercased from the table, and the informational toast tint is the engine's translucent accent wash over the slab (the board's slab is flat). Loc: `theme.zune.overlays.offlineNotice` and `offlineNoticeShort` (the strip's board sentences).

## Z8b: the screens

Settings, the theme picker and its options, the setup wizard, the release notes, and the report and crash dialogs. All fifteen keys are Zune's own
(`Structure\ZuneStructure.Screens.cs`); Zune's registrations win over Classic's stand-in key by key.

**Files.** `Structure\Views\Screens\ZuneSettings*.cs` (the page and its seven tabs), `ZuneSetup*.cs`, `ZuneReleaseNotes*.cs`, `ZuneFeedback.cs`,
`ZuneCrash.cs`; numbers and tier records in `Structure\Rules\ZuneSettingsRules.cs` (settings) and `Rules\ZuneSetupRules.cs` (setup, release notes
and the two dialogs); loc `theme.zune.screens`; tests `Wavee.ShellTests\ZuneScreensMountedTests.cs`.

**Keys.** `Pages.Settings`, `Settings.ThemePicker`, `Settings.ThemeOptions` (page and wizard variants), `Setup.Wizard / Terms / SignIn / Theme /
LocalPlayback / SignInDoor / RuntimeCard`, `Pages.WhatsNew`, `ReleaseNotes.Viewer`, `ReleaseNotes.AfterUpdatePlate`, `Dialog.Feedback`,
`Dialog.Crash`. Template ids: `zune.screens.settings[.theme-picker|.theme-options]`, `zune.setup.*`, `zune.release-notes.*`, `zune.dialog.*`.

**Settings.** A panorama title ("settings"), a lowercase section list (the seven core tabs: the page state and every deep link name a tab), and the
open tab's column. The column is the document drawn: every `SettingsDoc` section of the tab is a group (a 26/300 heading over label | control rows), so a
new document row shows up with its words and its writer; the controls that need labels or ranges (combos, chips, sliders) are built per row in
`ZuneSettings.<Tab>.cs`. Controls are the engine's, drawn by `ZuneControls`; the stateful ones (`ZuneKit.ChoiceCell`, `SliderCell`) keep one signal for
their life and re-read the document on `Settings.PageEpoch`. Appearance mounts the picker and the options through the registry (`Slots.View`), and
beside the column (or under it, by flex wrap) a live preview built from the roles. Node identity: the list, lane, groups and rows are the same nodes in
every tier; a tier changes their direction, wrap and widths only.

**Theme options.** `ZuneSettings.Theme.cs` draws the core's `SettingsOptionsView` by row kind: toggles as switches, short selects as chips (a combo
past four or when the structure asks for one), the accent as a row of square swatches, sliders, confirmed actions, "reset" beside the heading. The
wizard variant has no reset and always stacks.

**Setup.** A flat plate on the dialog ground with an accent column (the step as a 150 numeral at 35 % ink-on-accent, the page's name, the four steps);
the footer is the step label over a 2 DIP line, back and the secondary as words, the primary as the round accent button with its word. Compact is a sheet
with a 120 accent strip and a stacked footer. The core model (`Setup.WizardModel`, `Commands.ResolveFor`) decides everything.

**Release notes.** A panorama title; the main column (since line, hero, highlight panels, the changelog groups) beside the release rail at Standard and up,
a picker under the title below it. The viewer and the after-update plate are built from Zune's parts (no board draws them, owner Q18).

**Dialogs.** The report and crash bodies keep the core chrome; the body width is the active structure's overlay fit (`DialogFit.FormBodyWidth`), frozen
at open. Compact folds the preview behind a 40 row, Huge is a two-pane card.

**Numbers.** `ZuneSettingsTiers.For/Of` (content tier), `ZuneSetupTiers`, `ZuneReleaseNotesTiers` (page: content tier; overlays: window tier),
`ZuneDialogTiers`. The sizes the lane covers are Standard; the others are sane variations ZT8 tunes against the -Compact, -Small and -Huge boards.

**Conflicts with the boards (the plan and the owner win).** The board lists twelve sections; the core page is a seven-tab cell, so account, devices and
keyboard have no document rows and are not built, and audio, lyrics, feedback & help are groups of Playback, Appearance, About and Privacy. No downloads
(Q29 / NQ4): the core Storage cache settings stay. The board's "add a theme" tile and community-themes line are not built (nothing installs a theme
from a folder). Compact and Small draw the section list as a wrapping row above the column, not a drill-in. The Standard preview column wraps under the
options when the window is too narrow for both.

## Z9: the states, "on this PC", the light pass and the completeness test

(Round H, first half: the light pass and the review capture list are not in this entry.)

- **"On this PC" (Z-Liked, `Track.RowState.OnThisPc`).** A 14 ring (2 stroke) with an 8 arrow leads the row's subline, in the subline's ink (the accent on
  the playing row), tooltip "On this PC" (`ZuneRowCells.OnThisPcMark`, numbers in `ZuneListRules.OnPc*`). It is a Visible flip, not a mount, so a
  row without it pays no subline gap. An indicator only (NQ4).
- **Offline dimming.** `Track.OfflineGone` (the session is down, the audio is not on this PC, the row is not a local file) joins `Track.Withheld` in
  `Track.Dimmed`: the WHOLE row steps to 45 % (Z-State-Rows: "unavailable · 45% ink"; it used to be the title column only) and the time lane says
  "Not on PC" with the long reason in its tooltip. No play gate reads it. Board difference: the board's lane word is "not downloaded"; Wavee has no downloads (Q29).
- **Probes.** `--fake` marks every third pool track "on this PC" (`Playback.OnThisPc.UseFake`, `Entities.FakeOnThisPc`);
  `--inspect state kind=offline-rows` keeps the page and reads the session as down (`kind=offline` still swaps the page for the vacancy).
- **Focused row.** The focused row hands the time lane to its heart and "..." (the rest time and heart used to draw under them).
- **Loading.** An artist's name already known (a search row, a link) stands in its real display type over the skeleton (Z-State-Loading), the
  same element and place as the loaded hero's.
- **Completeness.** `ZuneRegistryCompletenessTests`: every view key and style key the contract declares resolves under Zune to `zune.*` / `common.*`
  with an EMPTY stand-in list (`DeliberateStandIns`): Classic's stand-in is no longer reached by any key. Availability Developer is asserted.
- **Not done / conflicts.** The board's selection bar is a bottom-docked bar of ring commands ("3 selected · play · play next · add to playlist · like ·
  remove · select all · done"); Zune keeps the core table's in-flow command bar in selection mode (a bottom dock is a core change). The focus ring on
  a row is clipped by the list's viewport on the left and right (the board draws a 2 dip ring inside the row).

## ZT1: frame tiers

The frame, the title bar, the search and the band at every window tier, from Z-Frame-Compact / -Small / -Huge / -Huge4K / -Narrow,
Z-Shell-Short, Z-Part-TitleBar, Z-Part-Band and Z-Sizes-Breakpoints. The records are `Rules\Tiers.cs` (`ZuneFrameLayout`),
`Rules\Tiers.Frame.cs` (`ZuneFrameTiers`) and `Rules\Tiers.Band.cs` (`ZuneBandTiers`).

| tier | title bar | search | trail | band (row 2 / collapsed) | band type | row 2 on scroll |
|---|---|---|---|---|---|---|
| Compact 300-639 | 48, 40 targets | magnifier | no | 92 / 52, pivots rotate (lit first), row 2 40 | role | scroll-tied |
| Small 640-1007 | 48 | magnifier | yes | 84 / 52, rows under a fade, no pins | role | pinned |
| Standard 1008-1919 | 48 | pill 220 | yes | 84 / 52, pins | role | pinned |
| Huge 1920-3199 | 48 | pill 320 | yes | 84 / 52, pins | role | pinned |
| Huge 4K from 3200 | 56 | pill 400 x 40 | yes, 15 | 112 / 72, pins 40 | pivots 40/48, row 2 15/20 | pinned |

- **One band height source.** `ZuneFrameLayout.BandH` / `BandCollapsedH` are read from the band's record, and the floating panel hangs
  from the band slot's measured foot (84, 52 collapsed or without row 2, easing between), not from a constant.
- **Short (below 700 tall).** The band rests collapsed to row 1 at every width, Huge and Huge 4K included, static (a scroll neither folds
  nor returns it). ShortFloor is
  126 (330 less 48, 84 and 72).
- **Crossings.** They commit on the first frame that is not resized and move: the title bar's height eases (48 <-> 56), the band's height,
  lead inset ease (a reflow the rows ride), the pivots slide, the pill's width eases (220 / 320 / 400), the trail, the bell, the pins and the actions fade in and out, and the
  magnifier and the pill cross-fade. The pivot run, row 2's run and the identity's host are one element type at every tier, so a crossing
  keeps their nodes.
- **The core's allocator.** `ZunePolicies.FramePolicy` sets `ZuneFrameTiers.Chrome`: the icon budget at Compact and Small, the field budget
  from Standard up (`ChromePolicy.Small` is new). The allocator's search form flips on the crossing frame, so a focused search keeps its focus
  across the pill / magnifier swap.
- **Clip region.** From Standard up the frame declares the docked panel (`frame.panel`, 340): a resized frame clips it, never squeezes it.

## ZT2: player and panel tiers

- **The bar per tier** (`Rules\Tiers.Player.cs`, unchanged levels from Z4a): the mini 64 at the foot of the reserved 72 at Compact; narrow
  (640) and medium (760) at Small; medium and full (1240) at Standard; wide (1920) at Huge in the 72; big (3200) at Huge 4K in an 88 slot
  with the 48 disc. The slot's and the bar's heights ease through real layout on a crossing (`SlotMotion`, `BarMotion`: size channels), so
  72 <-> 88 at 3200 and 64 <-> 72 at 640 never snap.
- **The bar's margins and rhythm** (the owner's review): the 2-px seek line lies on the top edge and takes no room; the transport cluster
  (40 disc row, 4, 16 times; 48, 6, 16 at Huge 4K) is centred in the whole bar, 6 / 6 of air in the 72 and 9 / 9 in the 88, the 48 cover
  12 / 12. The row's left inset is the frame's gutter (16 / 24 / 48 / 48 / 64), its right inset puts the last tool's glyph on the gutter
  (`ZunePlayerTiers.RowInsets`: 12 at Small, 36 at Standard and Huge, 52 at Huge 4K; the mini bar keeps 16 / 8). The insets are boxes that
  ease on a crossing (`InsetMotion`). Board conflicts, the owner's call: the board's 2 under the disc, and its 24 / 12 insets at Standard and up.
- **The panel per tier** (`Rules\Tiers.Panel.cs`, `ZunePanelTiers`; Z-Part-Panel, Z-Frame-Huge, Z-Frame-Huge4K): a full-width sheet at
  Compact, a 340 overlay at Small, inline 340 at Standard, docked 720 at Huge (queue | lyrics · friends) and 1080 at Huge 4K
  (queue | lyrics | friends), each docked column the board's 360 (the rule's floor). The panes are LINKED (core contract 1.29): they open and close with the
  panel, the primary steps aside to lyrics when it would show the queue or friends, its pivot head leaves the docked bodies out, and a docked
  pane draws no close. `maxDockedPanes` caps the columns (2: queue | lyrics · friends at Huge 4K too; 1: the 340 panel everywhere);
  `panelMode` AlwaysOverlay floats it at every desktop tier.
- **The frame** (`ZuneFrame`) places one host per docked pane, keyed by pane index (`zune-panel-host-2`, `zune-panel-host-3`), each flush
  right with a spacer after it as wide as what docks right of it. A crossing moves every host by size channels only: its width
  (`HostMotion`), its spacer (`ShiftMotion`, a reflow) and the floating panel's top spacer (`TopMotion`, a reflow).
- **Gaps.** The wide bar's "playing from" line has no source fact in `PlayerBarProps` (the sheet's `PanelTransportProps.Source` is the
  panel's), and the board's friends tool at wide and 4K has no `PlayerBarSlots` member: both need a core seam.

## ZT3: stage tiers

The stage's per-window-tier numbers live in `Rules\Tiers.Stage.cs`: the allocator's metrics (`ZuneStageTiers.Policy`) and the views'
record (`ZuneStageLayout`, with the Card, Column, Centered and Stack records and the voice each draws in). Views read
`ZuneStageTiers.StageOf(ctx.Tier())`; nothing compares a width.

| Tier | Head | Plate | Arrangements |
|---|---|---|---|
| Compact | 48 bar (exit chevron, "now playing", options) over a 48 pivot row (28/36, the lit pivot rotated to the front, edge-fade scroll) | 152 stack: seek 36, cluster 68 spread across (56 disc), heart · device · "…" 44 | Visualizer + Card layout: the Stack (cover at 0.76 of the width, 40/48 title, 64 disc, face and layout words at the foot); every target at least 40 |
| Small | 48 bar, pivots lead (20/28), look words, "…" and exit glyphs | 96: seek 30, heart · cluster (48 disc) · device · "…" | Large art: the 360 column (no volume row; the aside sits under the block); Centered at its Small record |
| Standard | Z4b's 48 bar | Z4b's 112 | Z4b's Card and Column; Centered with the 76 gradient foot |
| Huge | 56 bar, 64 gutter, pivots 22/28 | 120: seek 40, row 80, 44 targets, 64 disc, 160 volume | the 860 card (360 cover, 64/72 title, 72 disc) under the 150/160 display title; the 440 Up next column beside the lyrics |
| Huge 4K | 72 bar, 96 gutter, pivots 28/36, words 17 | 280 (from the stage's own `stage.huge4k` rung): seek 72, row 160, 56 targets, 96 disc, 220 volume | the 1120 card (480 cover), the larger column and Centered records; the 560 Up next column |

Every rect the allocator hangs under its 48 bar (the identity card, a Portrait or Compact identity, the pane, the 460 card, the face
chip, the docked gallery) moves down by the tier's `Drop` (the head's height above 48). An arrangement that does not fit its tier tries
Standard's, then the plate. Crossings re-key nothing: the stage root, the transport's layer and the cover keep their nodes; the plates
cross-fade, the cover and the pane spring.

## ZT4: discovery tiers

Home, Browse and Search draw one record per CONTENT tier (`Rules\Zune{Home,Browse,Search}Rules.cs`: `ZuneHomeTiers`, `ZuneBrowseTiers`,
`ZuneSearchTiers`). Every property of a record defaults to Standard's board, so a tier states only its differences (`Std(tier) with { ... }`);
Huge 4K is Huge's (pages cap at 2400, decision 2). Views read `ZuneHomeTiers.Of(ctx.Tier())` and the like; nothing compares a width. Grids and
shelves are live fits (a `GridEl` with `MinColWidth` and the record's `MaxColumns`; a `PagedShelf` in fit mode with the record's `maxColumns`),
one keyed node at every tier. Where a tier draws a different tree for one part (Home's history tiles / rows, Browse's For you list / plates) the two
arms are keyed by the PART, in an unkeyed box, and cross-fade (`ZuneMotion.SwapFade`).

| | Compact | Small | Standard | Huge (= Huge 4K) |
|---|---|---|---|---|
| Home zone gap | 22 | 18 | 28 | 36 |
| Quickplay | one column; 4 x 80 pins; history as 56 rows (44 art); no new group | pins beside history, 3 x 88 tiles, the name alone; no new group | 4 x 132 pins, 3 x 100 history, 3 x 100 new | 6 x 180 pins, 4 x 140 history, 3 x 140 new, groups 48 apart |
| Daylist | 112 row: 112 art, 22/28 over 2 lines, one 40 play ring at the end, no clock | 112 row: 140 art, 26/32, play + shuffle with words, no clock | 176 strip: 320 art, 40/48, 4 rings, 260 clock | 220 strip: 440 art, 64/72, 3 rings, 320 clock of 52 bars |
| Home shelves | cover shelf 2 across, wide tiles 2, Browse tiles 2 | cover 5, wide 4, tiles 4; releases beside "because you like" (32 apart) | cover 154 fixed, wide 4, tiles 4, pair 48 apart | cover 8, wide 6, tiles 8 (120 tall, 22 name), 3 cluster columns |
| Browse | charts above For you; 104 plates; For you 56 rows (44 tile); moods 2 x 88; banner 140 | charts 280 beside For you (2-col dot list); genres 4, moods 5 x 80; banner 120 | charts 420 beside a 3-col list; genres 6, moods 6 x 96; banner 200 | charts 640 (216 plates, 40/48 name) beside 5 plates; genres 8 x 40, moods 8 x 140; banner 260 |
| Search | name over a 120 art row; best matches 1 column of 56 rows; 40 rings | 280 top-result column (name under the art), 1 column of 52 rows | 440 column, 180 art, name in the copy, 2 columns | 480 column, 240 art over the 64/72 name, 3 columns of 60 rows |

**The one-row daylist.** `Z-Shell-Short` ("330 tall: daylist to one row") needs a height the Short tier (below 700) is too coarse for: the Small board is
800 x 600, Short by the ladder, and keeps its 112 row. So Tiers.Home.cs declares a window-height RUNG, `ZuneHomeRules.DaylistRung`
(`home.daylist.full`), crossed from 480 and held 24 on shrinking (a lane assumption between the two boards); not crossed, `ZuneHomeTiers.Of`
answers the tier's record in its one-row form (56 high, 56 art, the eyebrow and the title on one line, the 36 play ring at the end).

**Crossings.** A tier crossing runs reflows in the frame (the band's inset, the title bar's pill) beside the page, and the engine's reflow-shove
rule snaps a POSITION slide committed beside a live reflow (ZT1's lesson). So a chapter of a discovery page (a zone, or a pair of zones, a
Browse band, Search's top row) animates its HEIGHT with a size channel, `ZuneHomeRules.ChapterResize` (the engine's flow reveal, the cut of
`Design.Reveal.Resize`): the layout lands once and everything below rides the presented extent. Only the OUTERMOST box of a chapter carries it (two
nested flow reveals would count one height change twice): the daylist strip, the Browse tiles | charts row and a Browse band's charts | For you pair ride
their chapter. The daylist keeps its art, copy, clock and title nodes through every form; only the end play ring, the meta line and the rings past
the tier's count come and go.

**Compact targets.** A link word (`K.Link(..., target)`), a cluster's title and a word of Search's runs are at least 40 x 40 at Compact; the
rows are 56 (40 for a genre row), the rings 40.

**Home row 2 seed.** `Shell.PageChromeChannel.RouteViews` answers Home with the lone "all" view (lit when the route names no facet), so the band
reserves row 2 on the first frame instead of easing 52 to 84 when `HomeHost` publishes the real words (which replace it under the same id).

**Tests.** `Wavee.Tests\Theming\ZuneDiscoveryRulesTests.cs` (the records per tier, every tier fitting its narrowest window, the one-row rung and its
hysteresis); `Wavee.ShellTests\ZuneTierLayoutTests.Discovery.cs` (each page at every harness size: the record's page, no overflow, what the record
decides is what is mounted, Compact targets; each page across 639 / 640, 1007 / 1008, 1919 / 1920, 3199 / 3200 both ways and the heights: the same
nodes, the daylist gliding).

## ZT5: detail tiers

Album, playlist, Liked Songs, show and episode at every width tier and on a short window, against `Z-Album*`, `Z-Playlist-*`, `Z-Liked*`, `Z-Show*`, `Z-Episode*` and
`Z-Sizes-Breakpoints` (there is no `Z-Part-TrackTable` board). The frame, the panorama and the page column are Z6a1's and T2/T3's; ZT5 finishes the ladders' tier
records, the table, the insights and the show and episode pages, and hardens every page from 300 to 3840 wide and at 330 tall.

**One body.** `ZuneListRules.DetailTiers` is the single `DetailPolicyFor` body (`ZunePolicies.Lists.cs`); the three ladders (`detail.mode`, `detail.table`,
`detail.height`) are declared by `LaddersDetail` (`Rules\Tiers.Detail.cs`, numbers in `ZuneDetailRules`).

| | Compact | Small | Standard | Huge | Huge 4K |
|---|---|---|---|---|---|
| Arm (page mode ladder) | vertical (hero or show header) | two-column | two-column | two-column | two-column |
| Track table | Compact row: art or number, title over "artist · time", heart, 40 "..." (no header, no list "#") | # · art · title · time · "..."; the Album lane where the table ladder admits it ("album from 900") | + album, added, plays | the same | the same (the wide twin) |
| Identity column (rest) | - | 160 | 240 | 320 | 400 |
| Aside | - | - (its sections return under the rows) | 260, docks by the window tier | 400 | 440 |
| Insights | sheet over the list (never wider than the page) | sheet (320 to 400) | the aside's section, closed until asked (320 wide alone) | the aside's section, rests OPEN (420) | the same (440) |

The Huge 4K row is `ZuneListRules.Huge4K`, the `HugeWide` twin the core reads past the third pane's rung (`pane.third`, a window of 3200). Row heights follow the
user's density at every tier (P5 decision 3); the boards' lane widths are the core's `Track.TracksFor` (within a few DIP of the boards).

**Panorama step by kind** (`ZunePlaylistTiers.Panorama`): an album's is the record's (64/72, 120/130 at Standard, 150/160); a playlist's, a show's and an
episode's are 64/72 up to Standard and 150/160 at Huge; Liked Songs' 40/48, 64/72, 120/130. An episode at Standard sets its title at 40/48 over two lines
(Z-Episode wraps it): `ZuneDetailLayout.PanoramaLines`, and the band hugs the title (its reserved height a floor).

**Show and episode.** They stand in the same frame as the album (the page column, the panorama, the identity column, the aside slot). The vertical arm's
fixed show header now opens with the panorama (Z-Show-Compact), the 120 cover beside the meta, the ledger, then the ring row. One home per verb: the band's
row 2 carries "follow" from Small up, so the rail's follow ring and heart leave (the show's own primary, resume or the ghost play, stays); Compact keeps them.
The rating line is a 40 target at Compact.

**The short window (under 700 tall, 330 at the least).** The two-column arm folds the panorama band (flow-revealed: the columns ride it) and the identity
column leads with the title; Liked Songs' head states the title over the meta; a show's or an episode's fixed vertical header folds to the name over the ring
row (about 96 tall), so a 126-tall page keeps room for its list.

**Crossings.** The identity column, the aside and the docked insights ease their WIDTH with a reflow (`ZuneDetailRules.ColumnReflow`: the list beside them
yields smoothly; the earlier clip-window reveal laid the neighbours out at the final size at once); the panorama band eases its height with a flow reveal
(`PanoramaFold`). Nothing is keyed on a tier.

**The drawer.** The waveform strip (191 wide, fixed) leaves the version row where the content tier is Compact and fades in from Small (`WaveHost`).

**Compact targets.** Rings 40, the hero's artist line 40 tall (`ZuneDetailRules.HitHeight`), the "show all" link of a stack 40, the rating line 40, the insights
toggle and close 40.

**Row 2 (Z6b1 item 3, not done).** The playlist's "recommended" and "collaborators" views and its "invite" action need a core seam; see the lane report.

**Tests.** `Wavee.Tests\Theming\ZuneDetailTiersTests.cs` (the table form, the aside, the insights' rest and widths, the wide twin, the panorama step by kind, the
short fold, the Compact minimum); `Wavee.ShellTests\ZuneTierLayoutTests.Detail.cs` (each page at every harness size: the arm, the band, the columns and the
page column's x; the album across 1008, 1920, 3200 and the 700 height, both ways: the same nodes, the columns gliding).

## ZT6: artist and people tiers

Boards: `Z-Artist` and its `-Compact` / `-Small` / `-Huge` / `-Huge4K`, `Z-Profile` and its `-Compact` / `-Small` / `-Huge`, `Z-Concert` and its `-Compact` / `-Small` / `-Huge`. No board (owner Q18, built from Zune's parts): the discography page, the lightbox, the people list, the concert hub, the tour dashboard and the place picker. Files: `Rules\ZuneArtistRules.cs` (`ZuneArtistTiers`, `ZuneArtistTierPolicy`), `Rules\ZuneProfileRules.cs` (`ZuneProfileTiers`), `Rules\ZuneShowRules.cs` (`ZuneConcertTiers`, the popups' fit), `Rules\ZunePolicies.Artist.cs`, the views under `Views\Artist`, `Views\Profile` and `Views\Concert`.

**Records.** Each family has a record per content tier and every one reads `ZuneTiers.Frame(tier).Gutter`. `Of(facts)` returns the content tier's record, folded on a Short window (330-699 tall: the band folds to 52 and the bar holds 72, so about 160 DIP are left to the page); a view mounted with no frame reads Standard's. A record carries what the tier lays out: the artist's hero height, name voice, where the actions stand, the chart's columns, cap and rail, the shelf cards and the grid cell; the profile's avatar, name voice, ranked columns, cards and list cell; the concert's title, numeral, slab and rows. A bare link word is a `LinkTarget` (40 at Compact, 0 where the word is its own target).

**Short rung.** The artist's hero gives its height back (a 132 floor that still hugs its copy, the 64 compact name, the body 8 under it, sections at most 20 apart). The profile's avatar is at most 88 with the 40/48 title voice and 20-apart sections. The concerts' title, numeral and facts take the Small voice at most (40/48, 48, 15/22), the gaps and the schedule's photo shrink (20, 140). The columns, slabs, rows and targets stay the tier's own.

**Huge 4K artist (the conflict).** The board draws the page 2632 wide with a 520 hero, a 440 rail and about as a third body column; decision 2 caps pages at 2400. Kept from the board: the 520 hero (the 150 name, a 56 eyebrow, a 24 foot), the 440 rail 64 from the chart, the 44 section gap and 32 body top, the 88 pick art, 200 cells with a 20 gap (eleven across 2400, the board's count), 220 shelf cards (as Home's). Given up to the cap: the 2632 column (the chart is the column less the rail, two columns of about 920 against the board's 1000, so it carries no cap of its own; Huge keeps its audited 1200), and the third body column ("about" stays the pivot's own "details" section, because the pivot's anchors are the core's plan). The latest release stays a 160 row in the rail (the board stacks a 240 square). Huge 4K profile and concerts have no board: they are Huge's with the 220 cards and the 480 slab.

**Pivot clearance (core seam).** Classic's pivot click parks a section 56 under the viewport's top (its sticky band is inside the viewport) and its scroll spy starts there. Zune's band is the frame's, outside the viewport. `ArtistTierPolicy.BandClearance(facts)` (default `Detail.BandLayout.Height`, so Classic and the shell before tiers are unchanged) is read by both `Artist.PageHost` and `Profile.PageHost` for the click's margin and the spy's band edge; Zune answers `ZuneArtistRules.SectionAir` (16).

**Own profile (core fix).** `--fake`'s account row is the bare id "wavee-listener", which no provider owns, so its `EntityUri` is not valid; the profile host gated the row on validity and answered "isn't available" forever. `Profile.PageHost` now binds a subject whose uri is valid, or whose row is yours (`Entities.Current.MeSlot`). This changes Classic's own profile in `--fake` (and for any account whose row is a bare id) from "isn't available" to its page.

**Popups.** The concert flyouts and the place picker are 320 / 264 / 360 DIP at their natural width and the window less 12 each side below that (`ZuneConcertRules.FlyoutWidthIn`); the calendar's days share the flyout (38 at most) and are 40 tall at Compact, the month's back target 40.

**Compact.** The hero's rings are 40 targets (the kit ring's box is the clickable node), the discography and "see all" link words and the external links are 40 boxes, the discography's three words wrap to a second row at 300, the profile's action row wraps to a second row of 40 targets beside a 96 avatar, and the people list's chip words wrap. The people list's narrowest card is the profile record's (`ArtistTierPolicy.ProfileListMinCell`): 120 at Compact (two across 300), 176, 200 at Huge, 220 at 4K.

**Lightbox.** The overlay covers the window, so its thumb and top row follow the window tier (`ZuneArtistTiers.OfWindow`): thumb 48 / 56 / 56 / 72 / 88, top row 64 / 72 / 72 / 88 / 104.

**Tests.** `Wavee.Tests\Theming\ZuneArtistRulesTests.cs` (the records, the 4K reconciliation, the Short folds, the clearance, the popups' fit); `Wavee.ShellTests\ZuneTierLayoutTests.Artist.cs` (the artist, the discography, the concert hub and event pages, the profile and the people list at every harness size, their parts across every edge both ways, the own profile reaching Ready, and a pivot click parking at the clearance).

## ZT7: collection tiers

The library and the module page tuned per tier against `Z-Library-*` (+ `-Compact`, `-Small`, `-Huge`) and the Short height rung. Recents already carried a record per content tier
(`ZuneRecentsTiers`) and was not changed. Numbers live in the records (`ZuneLibraryTiers`, `ZuneModuleTiers`); no key changed.

- **Huge and Huge 4K fill the 2400 column.** The board draws four columns (artists, the artist, albums, songs); the host mounts a navigator and one right column, so Huge keeps those two
  and shares the width: the pane rests at the board's songs column (640) and grows 1 share to the navigator's 2, up to 1200. A grid navigator is uncapped (its cells fill the column), a
  list stops at 960 (`NavMaxW`; the grid's is `GridNavMaxW`). The artists' list is the board's 280 and the reader takes the rest. At 2560 the pair covers 2432 of 2464 (grid) or 2208 (list);
  it used to cover 1648.
- **Short rung (330-699 tall, 126 of page).** `ZuneLibraryTiers.Of(facts, single)` reads the height tier. The page's top is 4 and the head-to-body gap 8 (`PageTop`, `BodyGap`). The
  album and show panes fold the header and the verbs into ONE row (`PaneBrief`: the title, which still opens the page, then play, shuffle, the heart and "..."), because the full header
  is as tall as the page. Compact's head is ONE 40 row (`OneRow`): the sort button, the find field and the "..." view menu; the count and the grid and list words go (the menu carries
  every view). Podcasts keeps two rows (its mode words fill the first). Both forms keep the same child slots, so the rows and the table keep their nodes and scroll across the crossing.
- **Module page.** A Common block (`CommonModulePage`), drawn in the style's roles (Zune's type scale). `ZuneModuleTiers` places it on Zune's page column: the frame's gutter (16 / 24 / 48 / 48
  / 64), a centred 2400 column that starts at the band's x, a 40 target at Compact and no masthead reserve. `CommonModulePage.Page(props, layout, topReserve)` takes the layout; Classic's
  `Template` is unchanged. The registry refuses a second template for a key, so the structure registers it through `CommonBlocks.Register(views, ZuneModulePage.Template, "zune.module.page")`.
- **Local files** is the playlist page (Z6b1 / ZT5 own `Views\Detail\*`); it has no collection view of its own and no board.

## ZT8: screens, overlays, dialogs and states tiers

Settings, setup, release notes, the overlays and the two form dialogs, tuned per tier against their -Compact, -Small and -Huge boards (`Z-Settings-*`, `Z-Theme-Options-*`, `Z-Setup-*`,
`Z-ReleaseNotes-*`, `Z-Overlays-*`, `Z-Dialogs-*`). Numbers only moved in the records (`ZuneSettingsTiers`, `ZuneSetupTiers`, `ZuneReleaseNotesTiers`, `ZuneDialogTiers`,
`ZuneOverlayTiers`); no key, no node identity and no verb changed.

**Settings** (content tier). Compact: the 64/72 title, a wrapping row of 40-tall section words, rows stacked, the short choices the board's wrapping 40-tall chips (buttons in a
wrapping row, the picked one on the accent: the segmented control is one row and clips), the picker 2-up (88 tall, name and line), 40 swatches. Small: the 180 list of 30-tall 18/24
words, the 520 column, rows `140 | rest` that wrap the control under the label when the column is too narrow (a 640 window leaves 380), the picker 4-up (68 tall, the name alone,
12 apart), 28 swatches. Huge and Huge 4K: the 150/160 panorama, a 260 list of 52-tall 24/32 words 40 from the 760 column, rows `220 | rest`, the picker 4-up (120 tall), 36 swatches.
The section list's box (`ColumnCollapse`) and the column resize on a size channel; the words keep their nodes.

**Setup** (window tier). Small: 720 x 480, a 160 accent column (20 in, the numeral at 12 / 4), a 72 QR, a 180 progress line, 24 / 22 / 24 / 18 around the page. Compact, Standard and
Huge were already the boards'; Huge 4K is Huge's (the plate never stretches).

**Release notes** (content tier). The rail is on at Small too (160, 32 from the body, three panels 12 apart: Z-ReleaseNotes-Small draws no picker); Huge's rail is 160 (the board's) 32
from a body capped at the two 760 columns and their gap (1552). Compact keeps the 40 picker. The body resizes on a size channel when the rail comes or goes.

**Overlays and dialogs** (window tier). `OverlayPolicy.Small` (a contract addition, optional) splits Small from Standard for the core hosts, so the report and crash dialogs are the
752 two-pane card at Small (Z-Dialogs-Small: the form, a rule, the live report in a 300 pane; a card the window leaves too narrow for the pane keeps the report under the form) and the 548
plate at Standard AND Huge (Z-Dialogs-Huge draws nothing wider: the 1040 card Z8b copied from Classic is gone). Huge 4K has its own overlay record: the chrome row a panel and a sheet keep
clear is the frame's 56, not Huge's 48 (ZT1).

**States.** The Compact offline strip (40, the short notice, a 40 retry word) and vacancy (the quiet title, stacked 40 targets) were already the Compact board's. The page states
(loading skeletons, empty libraries and playlists, the offline artist) belong to the pages' own lanes (Z9, ZT5-ZT7).

**Conflicts with the boards (the plan and the owner win).** Settings at Compact is a drill-in on the boards (a master list of 60-tall rows with a hint and a chevron, a section page with
a back link and the section's name as its 64/72 title); the core page state is one always-open tab (`TabCell`), so Compact keeps the 40-tall row above the open section. Dialogs at
Compact are a full-bleed sheet from the title bar to the window's foot on the board; the core fit (`OverlayFit.Fit`) centres a card inside `EdgeY`, so the card fills the window less
16. The offline strip is amber and 40 tall on Z-Overlays-Compact and grey and 56 on Z-State-Error-Compact; Z-Overlays-Compact is followed. The theme-options boards say 110 / 240
for the label column where the Appearance boards say 140 / 220; the page's own value is used for both.
