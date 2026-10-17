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
- **Collapse** (`collapseBand`, Compact, Small and Standard): row 2 folds while the page last scrolled down (`BandProps.Scroll`) and comes
  back on the way up or at the top. Huge and Huge 4K never collapse.
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

_Not built yet._

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

_Not built yet._

## Z6b2: the show, the episode and the concerts

_Not built yet._

## Z7: the collection

_Not built yet._

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
