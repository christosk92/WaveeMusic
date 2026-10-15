# Wavee pages & content components — map of `main` (2026-10-10)

Scope: `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee\Entities\*`, `Home\*`, plus the shared presentation layer those pages
stand on in `Platform\` (`Controls.*`, `Surface.*`, `Design.cs`, `Prefs.cs`), and the extension points in `Wavee.Core`,
`Wavee.Sdk`, `modules\`. Shell/Screens and the engine are other agents' maps; where a page touches them it is named.
Baseline is `main`; PR 205 (`C:\WAVEE\sidebar\WaveeMusic`, branch `feat/shell-cohesion`) is summarised in §7.
All paths are relative to `src/apps/Wavee/` unless absolute. Line numbers are `main`'s unless marked `[PR205]`.

Corpus: 130 files, 95,399 lines (Entities 92.6k, Home 6.8k). Every file carries a header `Role: CORE | UI | SHELL`,
and the split is real: CORE files are engine-free and unit-tested, UI files build `Element` trees, SHELL files own I/O.

---

## 0. The vocabulary every page shares (read this first)

| Thing | Where | What it is |
|---|---|---|
| Persisted settings | `Platform/Platform.cs:111-180,351-379` (`Platform.Keys.*`, `SettingKey<T>`) | the only store; not observable |
| Reactive pref reads | `Platform/Prefs.cs` (`Prefs.Appearance.*`, `Prefs.DetailHero.*`) | epoch `Signal<int>` + re-read; `Appearance.Set` persists then `Bump()`s (Prefs.cs:76-150) |
| Engine theme tokens | `Tok.*` (engine `FluentGpu.Dsl`) | 946 reads in Entities/Home: TextSecondary 169, TextTertiary 144, AccentTextPrimary 122, TextPrimary 100, AccentDefault 65, FillSubtleSecondary 57, StrokeCardDefault 41, FillCardDefault 37 … `Tok.Theme` (ThemeKind Light/Dark) read at 15 sites (18 lines incl. the three `Signal<ThemeKind>` inits) |
| App design layer | `Platform/Design.cs` (2,613 lines, 16 sections) | type ramp, colour ladders, on-media ladder, cover palette→colour, motion, nav motion, reveal ramp, image decode scale, page accent, wash geometry, shell material channel, cover-leaf components |
| Shared controls | `Platform/Controls*.cs` (Controls.cs 1,410, Controls.Art.cs 1,222, Controls.Cta.cs 512, Controls.Words.cs 460, Controls.Podcast.cs 414, Controls.Picker.cs 665, Controls.TrimTip.cs 150, Controls.ArtistAbout.cs 187, Controls.Pending.cs 37, Controls.Cover.cs 33) | seams, art surfaces, equalizer, save/follow, selection bar, vacancy grammar, chips, stat tiles, face piles, rich text, CTAs, word rails |
| THE media surface | `Platform/Surface.{Rules,Host,Parts,Bound}.cs` (1,048 lines) | `Controls.Surface(CardData, SurfaceShape, width)` — every card, tile and row outside the track table (23 consumer files) |
| Data model | `Entities/Entities.cs`, `Edges*.cs`, `Fetch*.cs`, `Store*.cs` | columnar tables, slots, `Knows()/Version/Changed`, CSR edges, planner, sqlite cache (§6) |
| Localisation | `assets/loc/en-US.json` → source-generated `static class Strings` (`Wavee.csproj:219-231`, engine `LocalizationKeysGenerator`) | 774 `Loc.Get(` calls in Entities/Home; `Loc.Bind` for live binds |
| Icons | engine `FluentGpu.Controls/Icons.cs` (generated Segoe Fluent glyph table) | `Icons.*` 300+ uses; `Theme.IconFont` used raw at 10 sites (Queue.UI.cs:837,861,937,1044,1180,1193; Track.UI.Bound.cs:168,552,810; Track.UI.cs:878) |

**Key fact for the refactor:** on `main`, Entities/Home contain **zero** reads of the nav style
(`grep NavStyle|PresentedNavStyle|Zune` → only a comment in User.UI.cs:85). The only "theme-like" branches in pages are the
appearance prefs (TrackRowStyle, RowDensity, HideTrackArtwork, Marquee, ColorWashes, LikedCover, DaylistClock,
Tempo/Plays columns, DetailPageLayout, rail widths) and `Tok.Theme` polarity reads. PR 205 adds 11 nav-style reads
into pages (§7).

---

## 1. Inventory

Legend for **States**: L loading/skeleton · P partial · E empty · X error+retry · N not-found/unavailable · U unavailable rows ·
♪ playing/paused row · H hover/pressed · S selected/multi-select · D drag/drop · V explicit/local/video/podcast variants ·
W width tiers / stacked · St sticky · Sc scroll-driven · M entrance/leave motion · T light/dark read.
**Switches**: RS = TrackRowStyle · RD = RowDensity · AH = HideTrackArtwork · MQ = Marquee · CW = ColorWashes · LC = LikedCover ·
DC = DaylistClock · TC/PC = Tempo/Plays column · PL = DetailPageLayout (Automatic/Hero) · RU/RW = rail uniform/width/collapsed ·
LIB = per-kind library keys · RM = Design.Reduced · TH = Tok.Theme.

| Page / component | Files (lines) | Key types | Shared components used | States | Switches |
|---|---|---|---|---|---|
| **Track table** | Track.Table.cs 2592, Track.Table.Chrome.cs 1420, Track.Table.EpisodeRow.cs 59 | `TableSource`(:54) `TableProfile`(:298) `TableArgs`(:349) `TableLive`(:392) `TableHost`(:402) `Snapshot`(:559) `Shape`(:567) `RowPresentation`(:2185) `TableSlot`(:2352) `TableRowContent`(:2419) `TableVerticalItem`(:2486) `TableRecItem`(:2532) | Track.UI/Bound rows, Detail.UI.Hero (vertical arm), Controls.SelectionBar, Episode.ReaderRow, SkelRegionEl, ItemsView.CreateBound | L P E X U ♪ H S D V W St Sc M | RS RD AH MQ TC PC RM |
| **Track row (eager grid + bound grid)** | Track.UI.cs 1430, Track.UI.Bound.cs 821, Track.Rules.cs 1669, Track.cs 888 | `ColumnSet`(Rules:113) `TableRules`(:141) `RowMetrics`(:365) `Format`(:425) `FilterModel`(:904) `Facts`(:1136) `RowState`(UI:69) `GridOptions`(:177) `EagerRow`(:1007) `RowData`(:1203) `RowCellRules`(Bound:30) `BoundRow`(:110) | Controls.Artwork, Controls.Equalizer, Controls.ExplicitBadge, Icons | ♪ H S U V (explicit/local/video/podcast/not-yet-out) | RS (75+73+31 Classic branches) AH MQ |
| Track drawer | Track.Drawer.cs 841 | `DrawerHost`(:89) `DrawerRules`(:676) `FormatSplitHost`(:503) `CreditsHost`(:585) | Waveform, Controls.*, Design.Palette.DataDotInk | L E V | RM TH |
| Track menus | Track.Menu.cs 689, Track.Menu.Video.cs 231 | `MenuOptions`(:47) `MenuSeams`(:58), 18+5 verbs | ContextMenuModel | — | — |
| Selection lane | Track.Selection.cs 280 | `SelectionLaneArgs`(:51) `SelectionBarRules`(:28) | Controls.SelectionBar | S | — |
| Waveform / JumpIndex | Waveform.cs 152, JumpIndex.cs 57 | `WaveformBands` `BeatGrid` `JumpIndex` | — (pure) | — | — |
| **Detail frame** | Detail.cs 1598, Detail.UI.cs 2020, Detail.UI.Hero.cs 821, Detail.Insights.cs 417, Detail.ScrollSpy.cs 40 | `Config`(Detail:1405) `Breakpoints`(:73) `VerticalLayout`(:144) `RailPolicy`(:633) `InsightsSheet`(:785) `NoticeRules`(:901) `BandLayout`(:950) `CoverLatch`(:1060) `Skeleton`(:1168) `RailLayout`(:1267) `Text`(:1485) `SortKeys`(:1593); `Identity`(UI:62) `FrameActions`(:225) `FrameSlots`(:259) `FrameSpec`(:332) `RailCell`(:400) `FrameHost`(:493); `VerticalSpec`(Hero:50) `HeroParts`(:77) `HeroHost`(:166) `PivotHost`(:697); `InsightsToggle`(Insights:60) | Track.Table, Palette.ShellTint/PageTonePlane, Controls.PlayButton/SaveButton/FollowToggle/MoreButton/Face/FacePile, SplitView (engine) | L (rail skeleton + hero skeleton band) P X N St Sc (hero collapse, band crossfade) M (FLIP rows, FadeUp) W (modes 0/1/2/3) T | CW PL RU RW TH RM |
| **Media surface** (cards/tiles/rows) | Surface.Rules.cs 277, Surface.Host.cs 194, Surface.Parts.cs 494, Surface.Bound.cs 83, Controls.Art.cs 1222 | `CardData`(Art:324) `SurfaceShape`(Rules:56) `Shape`(:77: Grid, Shelf, Video, Row, RowLarge, RowTile, RowOutline, EpisodeRow, SidebarTile) `PlateKind` `MenuPlacement` `PlayReveal` `SurfaceRules`(:137) `SurfacePlate`(Parts:41) `SurfaceParts`(:63) | Controls.Artwork/Shimmer, overlay FAB, equalizer pill | L (seed face) ♪ H S D | — (all dials are data/shape; no pref reads) |
| Controls (shared) | Controls.cs 1410 (+partials) | `LibrarySeam`(:73) `Artwork`(:207) `CoverFill`(:324) `Equalizer`(§3) `SaveButton`(:856) `FollowToggle`(:996) `SelectionBar`(§7) `Vacancy`(:1232, `VacancyVoice` :1212 Empty/Error/Offline/NoMatch) `Chip`(Art:567) `ChipRail`(:591) `Face`(:770) `Words.Rail`(Words:171) `PlayButton`/`IconAction`/`TextAction`(Cta) `PendingBar` `TrimTip` `Picker` `ArtistAbout` | engine Button/ToggleButton/SelectorBar… | L X E NoMatch H | TH (Design) |
| Palette / washes / covers | Palette.cs 713, Palette.Host.cs 311, PalettePersistence.cs 66, Store.Palette.cs 146, CoverToken.cs 91, PlaylistCover.cs 203, PlaylistCover.Rules.cs 165 | `Scheme`(Palette:67) `PaletteTable`(:110) `Palette.Watch/Ensure/ShellTint/PageTonePlane/ArtistHeroVeil/ArtistBlendWash` (Host:256+) `CoverToken` `PlaylistCoverRules` | Design.cs §7/§14-17 cover-leaf components | L (neutral fallback then upgrade) | CW TH |
| **Home** | Home/HomeScreen.cs 503, Facet.UI.cs 207, Facets.cs 326, Facet.Rules.cs 51, Zones.UI.cs 888, Zones.Rules.cs 93, ZonePlanner.cs 585, PodcastPlanner.cs 363, Model.cs 229, Daylist.UI.cs 642, DaylistSource.cs 63, Items.Rules.cs 151, RecentsCells.cs 153, Time.cs 314, EpisodeCaption.cs 75, ChartTiles.Rules.cs 73, Reveal.cs 62, LayoutFile.cs 436, Customize.UI.cs 204, SectionScreen.UI.cs 284, SectionScreen.Rules.cs 61; Entities/Home.cs 1833, Home.Host.cs 566, Home.Rules.cs 408, Daylist.cs 195 | `ScreenModel/Zone/ZoneKind/SectionInput`(Model:25-60) `SectionReader`(:72) `ZonePlanner`(:275) `PodcastPlanner`(:49) `FacetPivot/FacetSwitch/FacetCache`(Facets) `HomeScreen`(:64) `FacetRow`(Facet.UI:97) `Zones`(Zones.UI:86) `DaylistCard/DaylistClock`(Daylist.UI:96/364) `HomeLayout/LayoutDoc`(LayoutFile) `CustomizeScreen` `SectionScreen` | Controls.Surface (RowTile/Row/EpisodeRow/Shelf), PagedShelf, HomeCards.ShelfCell, HomeModules.ModuleHeader, Ui.Card/SectionHeader, SettingsCard, SelectorBar (Design.FacetTitleStyle), ProgressBar/Ring, InfoBar, Controls.Vacancy | L (derived shimmer) X (InfoBar + vacancy) E (EmptyFacet) ♪ (RelatesNow) H St (facet band + zone headers) M (Design.Entrance.Row stagger) | DC CW RM |
| Browse | Browse.Page.cs 696, Browse.UI.cs 447, Browse.Rules.cs 450, Browse.Cards.cs 237, Browse.Modules.cs 312, Browse.cs 393 | `DirectoryPage`(:92) `ChartsBand`(:211) `CategoryPage`(:295) `BrowsePageLayout.Mode`(Rules:275: Shelves/FlattenOne/FlattenTwoConcat/FlattenTwoStacked) `BrowseTiles`(UI:48: Word/Name/Link/Bar/Peek cells) `HomeModules.SectionGrid`(Modules:192) | Controls.Surface, PagedShelf, StickyClip, SkelRegionEl, Shell.Mastheads | L X E St (masthead) M (band cascade) | — |
| Search | Search.Page.cs 506, Search.UI.cs 852, Search.Rules.cs 521, Search.Subtitle.cs 18, Search.cs 386 | `PageHost`(:81) `Readiness`(:79) `HitItem/HitContext`(UI:64-68) `SearchHitRules`(:804) `ShimmerShape`(Rules:467) | Controls.Surface (Row(48)/RowLarge/Shelf/Grid), chips, MotionRecipes.PageSlide | L (3 shimmer shapes) X E (per-facet sentence) H M (facet slide) | AH |
| **Album** (+prerelease, pane) | Album.Page.cs 1545, Album.UI.cs 643, Album.Pane.cs 491, Album.cs 1226 | `PageHost`(Page:146) `ReleasePanelHost`(:463) `TrailingHost`(:625) `SectionsHost`(:887) `SurfaceMetrics`(:1254) `FacePileHost`(UI:51) `CountdownHost`(:290) `StackHost`(:368) `Pane`(Pane:60) `PageRules`(Album.cs:960) `DrawerVerdict`(:913) | Detail.Frame (Config.Album/Single/Compilation), Track.Table (profile w/ Drawer), Controls.FacePile, Controls.ArtistAbout, Surface | L (frame skeleton + ONE reserved trailing skeleton) P X N U (notice ladder) M (pane crossfade) | RD (Pane:192) |
| **Artist** (+discography, chart, reader) | Artist.Page.cs 1789, Artist.UI.cs 1358, Artist.UI.Chart.cs 914, Artist.Discography.cs 1744, Artist.Reader.cs 1786, Artist.Reader.Shape.cs 438, Artist.Reader.Policy.cs 44, Artist.Rules.cs 994, Artist.cs 885 | `PageHost`(Page:82) `ArtistHeroLayout/ArtistHeroMetrics/ArtistHeroTier`(UI:51-158) `ArtistPopularLayout`(:167) `ArtistSections`(:224) `HeroArt`(:610) `PickPhoto`(:888) `GalleryLightbox`(:986) `ChartHost/ChartRow/ChartPager`(Chart:88/576/879) `DiscoGridHost/DiscoCell/DrawerPanel/DiscoPageHost`(Disco:823/1032/1174/1648) `Reader/ReaderShape/ReaderMountPolicy` | Detail.Band/Pivot/BandLayout, PagedShelf, LazyGrid, Palette.ArtistHeroVeil/ArtistBlendWash, Controls.Surface, Track.NumberCell/Heart | L X ♪ H S W (Narrow/Compact/Medium/Wide; Stacked) St (band) Sc (parallax, spy) M (hero photo zoom/enter) T | RS AH CW RM TH |
| Concert (hub, schedule, detail) | Concert.Page.cs 1670, Concert.UI.cs 839, Concert.Rules.cs 999, Concert.cs 1621 | `HubPage`(:250) `FilterBar`(:691) `SchedulePage`(:824) `MonthBoard`(:1119) `DetailPage`(:1445) `ConcertLayout`(Rules:244) `SegmentedPillStyle`(UI:38) | Controls.Surface, Controls.Vacancy ×7, SkelRegionEl | L X E W (wide/narrow hero) St (shy pill) M (ticker, presence) T | TH (UI:134,251) |
| Playlist (+local, open) | Playlist.Page.cs 1204, Playlist.UI.cs 1330, Playlist.Open.cs 583, Playlist.cs 1381 | `PlaylistPage`(:270) `HeldRows`(:814) `PageRules`(:196) `CoverEditor`(UI:185) `InlineEditor`(:333) `OwnerBlock`(:523) `AccessPanel`(:787) `DaylistHost`(:959) `RecsSection`(:1090) `ListOpenPolicy/ListOpen`(Open) | Detail.Frame (Config.Playlist), Track.Table, Controls.Mosaic, Surface | L P X N (notice: deleted/create-failed…) E NoMatch (PlaylistRowsState) D (cover drop, deposit) M | DC (UI:990) |
| **Liked Songs** (+cover treatments, facts) | User.Page.Liked.cs 334, User.Cover.cs 1151, User.Liked.cs 404, User.Facts.UI.cs 1695, User.Facts.cs 918 | `LikedPage`(:56) `LikedChipBar`(:297) `LikedCoverArt/LikedCoverPicker/LikedCoverStyleFlyout`(Cover:904/974/1053) `LikedTreatments`(:233: lens/flat grids/wall/marquee/tone/stack) `LikedCoverRules`(Liked:46) `FactsPanelHost`(Facts.UI:212) `LensCell`(:47) `LikedFactsRules`(Facts:39) | Detail.Frame (Config.Liked), Track.Table (ContentFilterBar/LensHeader), Controls.Chip/ChipRail, Palette leaves | L X E (chips hidden when nothing) H | LC (Cover:93,141,1118) RM (Cover:694) |
| **Library** (albums/artists/podcasts/audiobooks) | User.Page.Library.cs 1571, User.UI.cs 804, User.NavMount.cs 141, User.SavedEpisodes.cs 94, User.cs 1890 (§8-§11) | `LibraryPage`(:196) `NavShape`(:151) `LibraryNavMount`(NavMount:60) `WordRail/ScopeRail/ReaderSortRail`(UI:101/124/157) `ViewToggleHost/ViewPanelHost`(:178/226) `LibraryNavOrder/LibraryLetters/LibraryHits`(User.cs:912/1119/1324) `SavedEpisodesReader` | Album.Pane, Artist.Reader, Show.Pane, Controls.Words.Rail, ItemsView.CreateBound, JumpIndex, Controls.Surface | L (ListItemRow shimmer, cross-dissolve) E NoMatch S W (wide ↔ collapsed 640/24; three-column search) St (letters) | LIB (view/size/sort/desc/scope/leftw/midw/selected) AH (:1397) |
| Recents | Recents.Page.cs 1222, Recents.UI.cs 535, Recents.cs 844 | `Shape`(Page:64) `PageView`(:112) `PivotTabs`(:848) `ListSurface/RowSlot/StickyDayHeader/RailView`(:931-1054) `CalendarSurface`(:1096) `AccentBinder`(:1155) `RecentsView/RecentsLayout`(Recents.cs:325/713) | Controls.Surface(Row(48)), Track.EagerRow, ItemsView.CreateBound, Palette.Watch | L X E ♪ H St (pinned day band) Sc (viewport accent) M (BodyFlip, masthead stagger) | AH (UI:296,316) CW (Page:1162,1205) RM |
| Profile (+lists) | Profile.Page.cs 711, Profile.UI.cs 482, Profile.Rules.cs 222, Profile.Route.cs 89, Profile.Lists.Page.cs 731, Profile.Lists.cs 366, User.Profile.cs 121, User.Profile.Rules.cs 125, User.Follow.cs 56 | `PageHost`(Page:49) `ProfileHeroLayout/ProfileHeroMetrics`(Rules:120-130) `ProfileSections`(:48) `ProfileTone`(:215) `ProfileLoadRule`(User.Profile.Rules:90) `ProfileLists.PageHost`(Lists.Page:67) `ProfileListModel/ProfileLetterRows`(Lists) | Detail.Band/Pivot, Controls.AccentHeader, Controls.Surface, Controls.Mosaic, Vacancy ×11 | L (derived shimmer) X N (404 seal → vacancy as content) E Hidden W (Stacked tiers) St Sc M T | CW (Page:242) RM TH |
| Show (+pane, podcasts) | Show.Page.cs 1644, Show.UI.cs 1060, Show.Rules.cs 758, Show.Pane.cs 236, Show.Podcasts.cs 29, Show.cs 595 | `PageHost`(:86) `ReaderModel/ReaderSnap`(:389/358) `ReaderHost`(:679) `PrimarySlot/LedgerSlot/RatingSlot/BadgesSlot`(:1126-1326) `ShowReaderRules`(:1369) `RailBody/VisitHead/EpisodesHeader/ReaderFoot`(UI:196/368/795/970) `ShowVisit/ListenNext/ShowLedger/ShowCadence/ShowReaderShape/ShowToolbarLayout/ShowViewPrefs`(Rules) `Pane`(Pane:62) | Detail.Frame (Config.Show), Episode.ReaderRow, Controls.Words.Rail, Controls.Podcast (chip/ledger/stars/door/find), ItemsView.CreateBound | L P X E (empty arm) ♪ S W (narrow arm) St (word rail) M (doors/hero lift) | `PodcastViews` key (:264,414) |
| Episode (+reader, discussion) | Episode.Page.cs 480, Episode.UI.cs 804, Episode.Reader.cs 211, Episode.Discussion.cs 661, Episode.Menu.cs 214, Episode.Selection.cs 136, Episode.Progress.cs 173, Episode.PlaybackProgress.cs 30, Episode.cs 749 | `EpisodePage`(:24) `EpisodePrimary/Attribution/Actions/RailAbout/Reader/ChapterReader`(:96-442) `ReaderRow/RowItem/RowMarks`(UI:69) `RowData`(:655) `Discussion/CommentCard/ReactionFlyout`(Discussion) `EpisodeSelection` `Episode.Rules`(Episode.cs:611) | Detail.Frame (Config.Episode) with Primary/Attribution/Satellites/About slots, Detail.Pivot + ScrollSpy, Controls.Surface(EpisodeRow), RichTextBlocks | L X ♪ (now-playing bar) V (played/in-progress/new/video/explicit) H S W (narrow row) M (cluster fade 120 ms) | RM (Page:343); consent key (Discussion:179) |
| Queue | Queue.UI.cs 1523, Queue.Rules.cs 671, Queue.cs 684 | `RailPanel`(:409) `StagePane`(:957) `QueueSurface`(:262) `RowText`(:102) `QueueRowRules`(:1474) `QueueSlots/QueueMovePlan/QueueOrder`(Rules) | Controls.Surface (Modern rows + now-playing card), Reorderable, own Classic hairline tree | E ("Nothing up next") ♪ H D (reorder, cross-section refused) V (seed/track/episode face) | RS (:468) AH (:469,985) `AutoplayEnabled` (:326,948) |
| User facts / insights | (see Liked) User.Facts.UI.cs, Detail.Insights.cs | `FactsPanelHost`, `TempoCardHost`, `ArtistsCardHost`, `BlendCardHost`, `LensHeaderHost`; `InsightsSheet`, `InsightsBandWord` | Controls.StatTile, SplitView overlay | L (250 ms quiet then one summary) E | RM (:337) |
| Module page (playback modules) | Platform/Modules.UI.cs 682, Modules.cs, Modules.Host.cs; Sdk `ModulePage.cs` | `ModulePageDoc/PageHero/PageAction/PageSection/PageItem` (Sdk) rendered by `Modules.UI` | Skel.Region, app controls | L X | — |
| Other routes | Diagnostics/Settings/WhatsNew/Logs (Screens, other agent) | — | — | — | — |

Route registrations (who mounts what): Album.Page.cs:116-122 (Album, Prerelease, Show, Episode), Artist.Page.cs:65-66
(Artist, Discography), Concert.Page.cs:49-51 (Concerts, ArtistConcerts, Concert), Home.Host.cs:44-51 (Home, HomeSection,
BrowseSection, HomeCustomize, Search, Browse, BrowseCategory, Recents), Playlist.Page.cs:58-64 (Playlist, Local, Liked,
LibraryAlbums/Artists/Podcasts/Audiobooks), Profile.Page.cs:35-36 (User, ProfileList), Platform/Modules.UI.cs:54 (Module).

---

## 2. Shared content components (detail)

### 2.1 The track table stack

**Files and split**
- `Track.cs` (CORE): columns/flags/field groups/handle/commit. `TrackFlags` :109-158 — Explicit, Local, Podcast (superseded),
  Unavailable (:128, only meaningful once `TrackFields.Availability` known), HasVideo, VideoOverride, LiveVideo, Lossless/24.
- `Track.Rules.cs` (CORE, verbatim 0.2.9 port): `Lane` widths :44; `ColumnSet` :113 (the width-track cache key; carries `Classic`,
  `Tier`, `HeartTrailing`); `SortColumn/SortSpec` :119-125; `TableRules` :141 — `IdentityColumns` :155, `RowHeightFor` :166
  (Classic 36/40/44/48 vs Modern 40/48/56/64), `HeaderHeightFor` :170 (32 vs 36), `RowsPending` :183/:193 (shimmer gate),
  `ArtSizeFor` :215, `TrailingColumns` :225, `ShowClassicInlineVideo` :239, `NextSort` :256, relief ladder :274-359;
  `RowMetrics` :365 (PadX/ColGap per tier, `ShowTempo`, `DrawerIndent`); `Format` :425 (cached number/duration/date formats);
  `CommandBarLayout` :688; filter model :755-1027; expanded-row `Facts` :1136; `MembershipDiff` :1328; `ReorderRules` :1428;
  `TargetResolver` :1516; episodes-as-rows `TableRowShape/DrawerExtentRule/SelectionVerbs` :1563-1669.
- `Track.UI.cs` (UI): `CellKey` :56; `RowState` :69 + `StateOf` :74 (subscribes Playback.CurrentId/IsPlaying/Buffering/Pending.Load
  + Liked edge); `TracksFor` :123 (per-(ColumnSet, art) `TrackSize[]` cache, never trimmed); `Grid` :165-384 (the one-shot row);
  cells :385-1006; `EagerRow` :1007 (`EagerTrackRowHost` :1043 — Recents, drawers, Home top tracks, rails); `RowData` :1203 (the track
  adapter for `Controls.Surface`); `ArtistOverflowHost` :1343; `TrackRowRules` :1393.
- `Track.UI.Bound.cs` (UI): `RowCellRules` :30 (`Rest` enum :32 Empty/Spinner/Volume/Equalizer/Star/Number; `ChartMark`;
  `DurationKind` Clock/Unavailable/ReleaseDate/Dash); `BoundRow` :110; `BoundGrid` :171 (built ONCE per slot; every value a `Prop`).
- `Track.Table.cs` (UI): `TableSource` :54 (abstract: Context/Count/Total/State/Version/At/AddedAt/AddedBy/ItemId/ChartStatus/
  TopTrack/HasDateAdded/HasAddedBy/HasVideo/KindAt/EpisodeAt/HasEpisodes/Subscribe/Retry/HostPlaylist) with
  `AlbumTableSource` :120, `PlaylistTableSource` :148, `LikedTableSource` :233; `TableView` :291; `TableProfile` :298 (the page's
  knob record: Config, DefaultSort, Editable, ContentFilterBar/Extent, LensHeader/Extent, PlayFrom, Deposit, MoveRows, RemoveRows,
  Recommendations, Tune, Drawer — equality by DATA + delegate PRESENCE :335-346); `TableArgs` :349 (Source, Profile, Accent,
  Vertical, ShowToolbar, Embedded, Trailing, PlayAllCell, HeroHeight, WidthSeed, ScrollKey, OnController); `TableLive` :392 +
  `TableLiveSlot` context :395; `Track.Table(args)` :398; `TableHost` :402 — memos `Snapshot` :559 (Context, Version, Count, State,
  Sort, Query, Filters, DataPublication, LikesPublication, **Classic, ArtHidden, Marquee, TempoColumn, PlaysColumn**, Config) and
  `Shape` :567 (ColumnSet, TrackSize[], Art, RowH, Density, EpisodeRowH); `ComputeSnapshot` :596 (pref reads :612); `ComputeShape`
  :637 (tier → `SetFor` :650 → relief :672; density :644); list arms :1178 (`VerticalList`/`RecsList`/`FlatList`/`Placeholder`
  :1048-1052); the list **Key** :1065-1066 = `list:<scroll>:[vh:]d<density>:classic|modern:q<hash>:r<reset>[:rec]` (tier and
  sort deliberately NOT in the key); playback :1555; selection/menu/block move :1647; drag insertion :1877; **row skin** `Skin`
  :2061-2151 (Classic vs Modern vs "plain" stacked-hero rows: margins/corners/zebra/hover/press/border/pill/divider all fork on
  `classic`); `DrawerBox` :2157; `RowPresentation` :2185 + `Present` :2190 (the per-row equality-gated value); choreography :2225;
  row components :2342-2592.
- `Track.Table.Chrome.cs` (UI): chrome stack :110; command bar :142 (Responsive over one cached builder; `InlineCommand`
  promotion/eviction into "…"); search :369-570; `TableHeader` :576 (Classic: empty # cell :590-591, `Design.Type.MicroMeta`
  labels :642/:653/:747 vs `Ui.Caption`); `TableDensityButton/Panel` :794/:817 (reads `Prefs.Appearance.RowDensity` :804,:807,:821;
  writes via `SetDensity` Table.cs:903); `TableMoreButton` :860 (Tempo/Plays toggles :881-889); filter card :915-1270
  (`FilterCaps` :919, `TableFilterFlyout` :1002, 368 wide); selection bar :1271; embedded lanes + header shim :1311-1420 (library
  panes shimmer under it).
- `Track.Table.EpisodeRow.cs`: `EpisodeRowContent` :32 — an episode member in a playlist IS `Episode.ReaderRow` unchanged.
- `Track.Drawer.cs`: `DrawerHost` :89 (demand on mount: Audio|Tags|Isrc|Files|Video + versions/waveform/credits edges), facts strip
  :200, versions & formats :349 (`FormatSplitHost` :503), credits sheet :585, `DrawerRules` :676 (`VideoRow` reserve :689,
  `CreditsView` :778, `FormatOverrides` :788). Stagger reads `Design.Reduced` :241; data-dot ink reads `Tok.Theme` :411.
- `Track.Menu.cs`/`Track.Menu.Video.cs`: menu grammar :11-24 (header · transport · collection · navigation · Share ▸ · extras ·
  surface extras · destructive); 18 verbs :416-689 + 5 video verbs; built at OPEN inside `ContextMenu.Attach`.
- `Track.Selection.cs`: the ONE selection lane (`SelectionLaneArgs` :51 with Func providers; `SelectionLaneKind` Tracks/Episodes/Mixed).

**Consumers of Track.Table:** Album.Page (profile with `Drawer` seam), Album.Pane (`Embedded`, `Trailing` = "Also by"), Playlist.Page
(`HeldRows` :814 as source during a /diff hold), User.Page.Liked (ContentFilterBar/LensHeader), User.SavedEpisodes (episode rows),
Detail.UI `TableFor` (the frame mounts it for every `Content == Tracks` page). `Track.Grid`/`EagerRow` consumers: Artist.UI.Chart
(chart rows), Recents.UI (single-play arm + drawer children), Track.Drawer, Artist.Discography drawer rows, Artist.Reader blocks.

**States** (path:line)
- loading/skeleton: `TableRules.RowsPending` Rules:183/:193 (Unknown ⇒ shimmer; Partial ⇒ until 12 hot rows settled; Complete/Failed
  never); `SkelRegionEl` Table:1054-1060 (`RowsShimmer`/`VerticalShimmer`, `SkelReveal.FadeOnly`, `SmoothResize:false`); per-row
  placeholder when `!RowHasData` Rules:211; `Placeholder(listState)` :1052 over `PlaylistRowsState` (Playlist.cs:189 Loading/Empty/
  NoMatch/Rows).
- error: `FailedPanel` :1059 (Retry → `TableSource.Retry` :95).
- unavailable/greyed: `TrackFlags.Unavailable` + `DurationKind.Unavailable` (Bound:34), `NotYetOut` Rules:632 (withheld # cell,
  release-date duration), `TrackRowRules.Affordances` UI:1401.
- playing/paused/buffering: `RowState` UI:69; rest-cell state machine `RowCellRules.RestOf` Bound:43 (Classic: Volume glyph, Modern:
  equalizer), hover pauses the equalizer (Table:2120).
- hover/pressed/selected/multi-select: `Skin` Table:2084-2097 (fills), check lane `SelectorVisualsBound.BoundCheckLane` :2127, pill
  :2129-2136 (Classic: pill hidden, fill = RowHover on selection), selection bar Chrome:1271.
- drag source / drop target: `Draggable = Drag.Source` :2093, video-drop cue :2070-2086, insertion preview §7 :1877.
- explicit/local/video/podcast: `ClassicExplicitBadge` UI:778 vs `Controls.ExplicitBadge`; Modern film lane vs Classic inline
  glyph (Rules:219-243); local = `TrackFlags.Local`.
- width tiers 0-6 (`Detail.Breakpoints.NominalTierFor` Detail.cs:79: ≥860→0, ≥720→1, ≥560→2, ≥440→3, ≥340→4, ≥300→5, else 6) with
  24-DIP hysteresis; relief ladder (Rules:274+) subtracts lanes the identity cannot pay for; tier is NOT in the list key.
- sticky header: `Sticky(56, engaged:)` via the hero arm (Hero.cs:15-19), `StickyClipInset` Detail.cs:569 (36 Modern / 32 Classic).
- entrance/leave: `s_mountEntrance` Table:2048 (280 ms opacity), `s_checkShift` :2051, `Design.RevealRamp` :487, membership
  choreography :2225-2340 (`Design.Reduced` :2321 short-circuits).

**Presentation switches inside the stack** (every site)
- `TrackRowStyle` → Table:612 (snapshot) → `ColumnSet.Classic` → branches: Track.UI.cs 75 mentions (:230-322 cells, :344, :400-406,
  :502-520, :601-621, :721-728, :748-767, :778-834, :868-902, :987-997, :1118-1170), Track.UI.Bound.cs 73 (:43-46, :74-75, :98,
  :130-131, :151-160, :177-198, :216-290, :340-343, :423-428, :511-538, :562, :588-727, :803), Track.Rules.cs 31 (:143-170,
  :214-243, :364, :385, :413, :1585-1588), Track.Table.cs 27 (:562, :645-670, :1020, :1066, :2067-2169), Track.Table.Chrome.cs 20
  (:586-653, :737-747, :1407). Also Artist.Page.cs:296, Artist.UI.Chart.cs:176 (+47 more), Queue.UI.cs:468 (+43 more).
- `RowDensity` → Table:644 (shape), Chrome:804/807/821 (+ direct `Platform.Settings.Get(RowDensity)` Chrome:359), Album.Pane.cs:192.
- `HideTrackArtwork` → Table:612; Artist.Page:297; Chart:177; Queue:469,985; Recents.UI:296,316; Search.Page:136; User.Page.Library:1397.
- `MarqueeEnabled` → Table:612 → `RowPresentation.Marquee` :2187 → `TitleCell(marquee)` UI:344.
- `TempoColumn`/`PlaysColumn` → Table:583-592 (epoch-gated direct reads), :662/:667; Chrome:346,881-889.
- `Detail.SortKeys` per-context persisted sort → Table:890-921.
- `Design.Reduced` → Table:2321, Drawer:241; `Tok.Theme` → Drawer:411, Bound:699, UI:760 (Camelot data-dot ink).

**Logic/presentation split verdict:** The table is the best-factored *complex* component: rules are pure and tested
(Track.Rules.cs), the host owns signals and publishes two value memos, rows are bound grids over `RowPresentation`. But the
**view is not replaceable**: `TableHost` owns layout AND behaviour (selection, drag, playback, choreography, reveal) in one 2.6k
partial; the skin fork (`Classic`) is threaded through 230+ branch sites rather than being a strategy; `ColumnSet` (a geometry
value) carries `Classic` as a field so the width cache forks by skin; the chrome (command bar/header/flyouts) is a nested partial of
the host. Contract-wise the inputs are already clean: `TableSource` + `TableProfile` + `Detail.Config` + `TableView` + `RowPresentation`
+ `Shape` are all data. Score **3/5** for "behaviour/data contract + replaceable view": the data side exists; the view side would
need the row/skin/chrome builders lifted behind an interface keyed off `Shape`/`RowPresentation`, with selection/drag/playback
staying in the host.

### 2.2 The detail frame (album · playlist · liked · show · episode)

**Rules (`Detail.cs`, CORE, engine-free except Loc/ItemsSelectionMode)**
- `Breakpoints` :73 — table tiers (above) and page MODES: `NominalModeFor` :116 (≥820→0, ≥660→1, ≥560→2, else 3 = `VerticalMode`);
  vertical enter/exit 540/580 (:108-109); `ModeFor` :124 with hysteresis; pre-measure seeds `InitialModeForViewport` :120.
- `VerticalLayout` :144 — stacked↔row flow breakpoint (424/400), `StackedArtMax` 280 :166, text column, hero band height arithmetic
  :286, `TitleTypePlan` :322 (one size per bucketed width + title, NaturalLineRatio 1.3301), `StickyClipInset` :569, slot map :584.
- `RailPolicy` :633 — per-scope persisted width/collapsed keys (`Platform.Keys.Detail{Album,Playlist,Liked,Show}RailWidth/Collapsed`,
  `DetailRailUniform`, `DetailUniformRailWidth/Collapsed` Platform.cs:169-181), `ScopeFor(RailScope, uniform)`, `ComposedWidth`.
- `InsightsSheet` :785 — which arm hosts the facts bento (`ShowsToggle`, `FactsSettled`).
- `HeaderMerge` :881, `NoticeRules` :901 (the DetailNotice ladder: Playlist.cs:172), `BandLayout` :950 (56-DIP context band + scroll
  spy :989), `CoverLatch` :1060, `Skeleton` :1168 (`RailPlan` :1212), `RailSlotSet/RailBadgeRow/RailLayout` :1254-1400 (the rail's row
  model: loaded rows and skeleton rows walk ONE model), **`Config`** :1405 (Kind, TwoColumn, RailWidth, Badges, ShowArtThumb,
  ShowAlbumColumn, Selection, HasTrailing, Heart, ShowPlays, ShowTrackArtist, Content, Recommendations, ShowTempo, ShowVersions,
  PlaysColumnOptIn, RailScope, RailResizable) with literals `Playlist` :1429, `Album` :1436, `Single` :1444, `Compilation` :1447,
  `Liked` :1449, `Show` :1457, `Episode` :1466, `For(kind, releaseKind)` :1470; `Text` :1485 (eyebrow/byline/meta); `SortKeys` :1593.

**Views (`Detail.UI.cs`)**
- `Identity` :62-202 — the value snapshot a page builds per render (Subject, Kind, Title, CoverUrl, PaletteUrl, CardAccent, Eyebrow,
  Meta/MetaLoading, HeaderPending, Owner*, Artists/Collaborators faces, DescriptionHtml, chart/daylist/upcoming facts, SaveTarget,
  ShareUrl, Notice, EditableMetadata) with value equality; `Identity.For(Album)` :135.
- `FrameActions` :225 (Shuffle, More, CoverClick, CoverDrag, DepositOnPage, GoLibrary — presence equality), `FrameSlots` :259
  (Cover, CompactCover, Title, Attribution, Description, Pulse, Chart, PreRelease, ReleasePanel, LikedFacts, Trailing, Episodes +
  podcast seams Badges, Rating, Ledger, Primary, Satellites, Topics, About — presence equality :316-326), `FrameSpec` :332
  (Identity, Config, Source, Profile, Actions, Slots, RouteKey).
- `RailCell` :400 (per-scope rail state; direct `Platform.Settings.Get/Set` :419-489), `FrameHost` :493 (`IPropsHost`; Render :661:
  pref reads :669-672, theme effect :684, mode ladder :690-706, **Hero page layout forces vertical for track pages** :703, insights
  arm :718-724, shell tint + page tone leaves :727-736 (tone key carries `:light|:dark` :735), notice :739, right column :744-754,
  vertical arm :760-789 (`Ctx.Provide(Design.AccentCtx.Slot, …)` :784), two-column row :791+ (rail · grip · right; collapsed rail →
  96-DIP compact strip)), rail family :1182-1868 (`Rail` :1210, `RailFrame` with "Liked = no layer fill" :1219-1221, `CompactRail`,
  `ShowHeader`, shared row bodies :1727), notice/pill/satellite/FAB/More :1869-1994 (`MoreHost` :1968), `ArtistLine` :1995.
- `Detail.UI.Hero.cs` — `VerticalSpec` :50 (Identity, Config, Actions, Slots, Accent thunk; `PlayAll`, `Insights` set by the frame),
  `HeroParts` :77 (Toolbar, BandActions, SelectionBar, CompactInteractive, SearchField, ColumnWidth, CompactLeft, HeroHeight…),
  `HeroFlags/FlagsOf` :92-110 (the presence set the hero, skeleton and reserved height all derive from — D49), `HeroBandHeight`
  :115, `HeroHost` :166 (stacked↔row reflow animates as one gesture :287; expanded presentation binds TransY/Opacity to the nearest
  scroller; the 56-DIP STUCK BAND :324 paints typography only, no fill; input handoff `CompactInteractive`), skeleton band :486,
  band helpers `Band/BandTitle/BandByline/BandHairline/Pivot` :632-821 (`PivotHost` :697 — the artist page reuses them).
- `Detail.Insights.cs` — `InsightsToggle` :60, frame seam :114, scrim :170, pane :214 (engine `SplitView` Overlay×Right, 350/120 ms),
  toolbar toggle :299, band toggle :352 (`InsightsBandWord` :390).
- `Detail.ScrollSpy.cs` — pure spy for the episode page.

**States:** header pending ⇒ whole rail/hero shimmers and cross-dissolves once (`Identity.HeaderPending` :79-82); meta shimmer bar
(`MetaLoading` :76-78); rail skeleton from `Skeleton.RailPlanFor` behind one boundary (:875); notice strip never an error page
(:738-740); collapsed rail (:807); modes 0/1/2 two-column vs 3 vertical/hero; Hero layout setting; the vertical hero collapse → band
crossfade (Hero.cs:324-386, `Design.Reduced` :386); rail rows FLIP on shove / fade up on late insert (UI:1184-1198); accent
transition (`AccentTransitionMs` :1899, reduced → 0); theme flip re-derives accent (:684) and re-keys the tone plane (:735).

**Switches:** `ColorWashes` :669 (tint/tone `disabled`), `DetailHero.PageLayout` :670/:703, `RailUniform` :671, rail epoch :672,
rail width/collapsed keys :419-489, `Tok.Theme` :587/:656/:684/:735, `Design.Reduced` :1899, Hero.cs:386,:788.

**Split verdict:** This is the cleanest "contract + view" seam in the app already: a page hands ONE `FrameSpec` VALUE (data equality,
delegates by presence) and the frame renders. Everything a theme would want to restyle (rail vs hero, band, skeleton, satellites)
lives in `Detail.UI*.cs`; everything a theme must keep (identity derivation, readiness, notice, breakpoints, rail persistence,
sort keys) is in `Detail.cs`. Coupling points: the frame owns the mode ladder AND the per-scope rail prefs (view state persisted
through `Platform.Settings` from inside the view :419-489); the vertical arm is co-owned with the table (the table mounts the
hero as item 0 — Hero.cs:15-19); `FrameSlots` builders return `Element`s built by the page, so a theme replacing the frame still
receives page-built fragments (face piles, countdown, chips). Score **4/5**.

### 2.3 The media surface, controls and the design layer

**Surface** (`Platform/Surface.*`): `CardData` Controls.Art.cs:324 (Uri, Title, Subtitle `Element?`, CoverUrl, OnClick, OnPlay,
Circular, Drag, ShowMenu, CoverOverride + init-only Height, Selected, SelectedAccent, Menu, CoverAspect, Meta, Caption, CaptionLines,
Eyebrow, MetaRow, Trailing, Below, Highlight, Drop, IsSeed; `Seed` :410; data-only equality :412-427). `SurfaceShape`
Surface.Rules.cs:56 (Layout Stack/Row, PlateKind CardPlate/ListRow/Tile/Outline, ArtEdge, TitleLines, CaptionLines, MetaLine, Fab,
MenuPlacement None/Corner/Trailing/TrailingLane, PlayReveal, MinHeight, Labels); presets `Shape.Grid/Shelf/Video/Row(edge)/RowLarge/
RowTile/RowOutline/EpisodeRow/SidebarTile` :77-126; refused dials :19-21 ("motion, colours, eager vs lazy chrome… are derived from
DATA presence and the SHAPE — a caller never flips an affordance off"). `SurfaceRules` :137 (ownership/cursor/chrome/menu/tooltip),
`SurfacePlate` Parts:41 (fills per PlateKind), `SurfaceParts` :63 (shell, bodies, labels, seed face :399). `Controls.Surface` Host:42 —
one host, two modes (free vs bound slot), hot = pointer|focus folded into one signal, lazy chrome (`CardChromeRules` Art:454),
`RelatesNow` :474 (equalizer pill). Skeleton = the same tree derived (`SkeletonProxy`/`SkeletonOverride` Host:45-51).
Usage: `Shape.Row(` 17, `Shape.Grid` 17, `Shape.Shelf(` 10, `RowTile` 9, `EpisodeRow` 4, `RowOutline` 3, `RowLarge` 3.
Adapters: `Track.RowData` UI:1203, `Episode.RowData` Episode.UI:655, `HomeCards.GridCardData`/`ShelfItem` Browse.Cards.cs:45,
`Search.HitData` Search.UI:260, Profile cards Profile.UI:379, Recents facts Recents.UI:73, Queue `RowText` Queue.UI:102.
**This is the one component that is already "data contract + one view" by construction** (score 5/5) — and the natural unit a
theme would re-skin (plate table, FAB, menu placement, label block).

**Controls** (`Platform/Controls*.cs`): seams `LibrarySeam` Controls.cs:73, `PlaybackSeam` Art:79; art `Shimmer` :149, `Artwork` :207
(decode via `Design.ImageDecodeScale`), `CoverFill` :324, `ArtUrl` :92; section ornaments :412; `SearchHighlight` :543; equalizer
:622 (`FrameTicker` :749); library affordances `SaveButton` :856, `PreSaveButton` :895, `FollowButton` :933, `FollowToggle` :996,
`FollowTextAction` :1038; row primitives :1059; swipe belt :1097; `SelectionBar` :1120; **vacancy grammar** `VacancyVoice`
:1212 {Empty, Error, Offline, NoMatch} × `VacancyScale` {Page, Compact}, `Vacancy(...)` :1232; dialogs :1290; `Notify.Say` :1367.
Controls.Art.cs: cover stack/`CoverShape` :88-158, now-playing overlay :159 (`OverlayProps` :196), `CoverShimmer` :495, chips
:551-602 (`Chip(label, selected, available, onClick)` :567, `ChipRail` :591, `ChipRailExtent` :558), stat tile :603, countdowns
:656, face piles :729 (`Face` :770), rich text :859, `RichTextTip` :1100, `RichTextBlocks` :1137. Controls.Cta.cs: geometry ladder
:69, 32/r4 grammar :92, labeled media pill :190 (marked DELETE), icon buttons :235, text action :366, accent CTA ramp :436, context
play button :467. Controls.Words.cs: `Words.Rail` :171 (Zune-style text pivot with sliding underline; `Word` :103; `Slide` :117;
pure decisions :106 — used by library sort/scope/reader-sort rails and the podcast reader filter/sort/tab rails), `Words.Links` :411.
Controls.Podcast.cs: badge chip :56, ledger bar :85, star row :214, door :256, find box :404. Controls.Picker.cs: preview-card
radio :36 (used by row-density, liked-cover, page-motion pickers). Controls.TrimTip.cs, Controls.ArtistAbout.cs (`AboutLayout`
Rail/Page :30), Controls.Pending.cs (`PendingBar`), Controls.Cover.cs (2×2 mosaic). `ButtonRules.cs` (pure colour/geometry math),
`AccentLadder.cs` (Graded → Payload → Held → Default :17-40).

**Design.cs** (sections, line): 1 Geometry `Size` :62 (PageMaxW, Thumb32/40/48, RailAlbum/RailPlaylist, FacePile*, DesignH…),
`Dock` :124, focus insets :131; 2 Accent budget :153 (`Accent` :183); 3 Colour `SeedPalette` :203 (`Tok.Use(Tok.NeutralPalette,
kind)` — Wavee always renders the engine's neutral palette; the palette picker is a tombstone :359-361), `Colors` :221 — shell
rungs Toolbar/Sidebar/PlayerBar/FileArea/Content (translucent, now paint-site omissions over Mica), `PremiumText` :237 (hard-coded
#1DB954 in dark), `ShellGround/ContentSurface/ContentLayer` :272-306, **list row ladder** `RowZebra/RowHover/RowPressed/
RowHoverZebra/RowPressedZebra` :318-331, **selection ladder** `SelectedRest/Hover/Pressed` :351-353 (accent plate for NAV rows only;
list rows keep the neutral ladder), `ChromeHover/Pressed/Badge` :355-357; 4 On-media ladder `OnMedia` :386 (scrim plates, ink,
glass, ink plate, backdrops, light button ramp); 5 Stage polarity `StageArm/StageInk` :498-673; 6 Cover palette → colour
`Palette` :681 (ToColor, Lift, Vivid, Accent, ChromeAccent, ChromeFromPayload, PageTone* :863, DataDotInk :980…); 7 grading halves
+ `ArtworkPlaceholder/WatchedPlaceholder/PlaceholderFor` :1029-1166; 8 **Type ramp** `Type` :1190 (TrackTitle, CardTitle, TrackMeta,
Eyebrow 12/16/600 +30 tracking :1227, MicroMeta 11/15 :1232, DenseMeta 13/18 :1235, DenseTitle :1238, SheetTitle 16/22 :1241,
RailHeader 20/28 :1244, ModuleHeader (display face, −6) :1250/:1261, PageHero 28/36 :1296, DetailHero (display, −20) :1302,
SurfaceDisplay 40/52/400 :1314, HeroTitle 40/52/600 :1325, ArtistDisplay 84/96/700 :1335, ArtistTitle 48/60/700 :1341,
ArtistCompactTitle 32/40/700 :1347, NowPlayingTitle :1353, PickQuote :1360, FoldTitle :1371, PivotLabel 19/25/350 :1377, NpvLyric
:1383, StatHero 28/36/350 :1401; `DisplayFace = "Segoe UI Variable Display"` :1194), `FacetTitleStyle` :1436 (SelectorBar style:
28/36 display, no pill) / `FacetCompactStyle` :1461; 9 Motion `Motion` :1482 (Fluent duration ladder :1500, Scale* ), `Reveal`
:1536 (Drawer, Card), `Entrance` :1574 (`Row(i)` stagger, `DelayMs`), `HoverMotionGate` :1641, `FrameTime` :1673, `Cadence` :1697;
`Reduced` (reduced-motion as a VALUE); 10 Page-nav motion :1710 — `PageMotionStyle` {Fluent, Spatial, WinUi, Classic, None} :1739
(DEFAULT Classic :1786; persisted `Platform.Keys.PageMotionStyle`, developer-only row), `Nav` :1757 (sequenced clock 90+210 ms
:1790-1796); 11 `RevealRamp` :1959; 12 `ImageDecodeScale` :1995; 13 `MorphKeys` :2049 (dormant); 14 `PageAccent/AccentCtx` :2070;
15 `Wash` :2091; 16 shell material channel `ShellWashPlacement/WashLayer/HomeWash/ShellMaterialState/ShellMaterial/TintOwnership`
:2157-2269; 17 the four cover-leaf components :2270+, `VeilSettlement` :2412.
**Everything in Design.cs is a property/function over `Tok`** (rule: "a `static readonly ColorF` here is a bug" :220) — it is the
app's token table, and the obvious place a theme contract would plug in; today it is one static class with no indirection.

### 2.4 Palette, washes, covers
- `Palette.cs` (CORE, process-wide `PaletteTable Images` :248, image-keyed, TTL'd): `Scheme` :67 (five roles), `Watch(url)` :415
  (a signal per image), `Ensure`, render-path reads :329, demand queue :429, writers :503, persistence snapshot :587.
- `Palette.Host.cs` (SHELL): debounced pump :41 (`Graded` :46 — dark+light schemes), pure `ParseDynamicColors` :171, mounts :256
  (`ShellTint`, `PageTonePlane`, `ArtistHeroVeil`, `ArtistBlendWash` — the only ways a page gets a wash; "no page ever holds a
  palette subscription at PAGE scope" :25-27). Hex parse :242-246.
- `Store.Palette.cs`: sqlite `palette` table (no scope), armed flush. `PalettePersistence.cs`: 20-byte scheme codec.
- `CoverToken.cs` (`spotify:image:` / `spotify:mosaic:` tokens), `PlaylistCover*.cs` (change/remove cover flow + WIC rules).
- `User.Cover.cs`: 9 liked-cover treatments on a 304 canvas (lens/flat grids/wall/marquee/tone/stack :387-686), 4 palette leaves
  (the ONLY nodes reading `Palette.Watch` :709-899), picker + flyout :969-1151. Hard-coded inks: `Black/White` :331, badge ink
  #1B1B20 :371, shadow :376, `PlateDark` #131318 :379, `VignetteInk` :528, `MarqueeBase/StackBase` :772-773, gradient stops :869-871
  ("ink on imagery is Design.OnMedia, never a theme token" :22).
- Consumers of washes: Detail.UI:727-736, Artist.Page (blend wash + shell tint; "colour" :139), Profile.Page:657, Recents.Page
  `AccentBinder` :1155, HomeScreen `BuildWash` :480 (`WashPick` Home/Reveal.cs:53 → `Palette.ShellTint`), Daylist card veil
  (Daylist.UI:13-14 `Palette.ArtistHeroVeil`).

### 2.5 Small shared vocabularies
- **Skeletons**: engine `Skel.Region`/`SkelRegionEl` deriving the shimmer from the real tree (`DeriveRenderedOutput`), with per-page
  `SkeletonProxy` builders (Zones.UI:29-31, Daylist.UI:42-44, Surface.Host:49). 44 files use it; the biggest: User.Page.Library 46,
  Detail.UI 44, Browse.Page 29, Track.Table 26, Detail.UI.Hero 25, Profile.Page 23, Playlist.Page 21, Album.Page 21.
- **Vacancies**: 50 `Controls.Vacancy(` sites — Error 25, Empty 21, NoMatch 4; **`VacancyVoice.Offline` is never used by a page**
  (offline degrades to cached rows; `Home.ChromeConcluded` Home.cs:38-40 treats an idle bridge as concluded). Heaviest: Profile.Lists.Page
  7, Concert.Page 7, Browse.Page 6, Search.UI 4, Profile.UI 4.
- **Not-found**: `Shell.RouteKind.NotFound` is the shell's; pages use a 404 seal → `Unavailable` face (Profile.Page:11-12,
  `ProfileLoad` User.Profile.Rules:17 Loading/Ready/Unavailable/Failed; `ProfileListLoad` Profile.Lists:25 adds Empty/Hidden).
- **Chips**: `Controls.Chip` (Art:567) — Liked content-filter chips (User.Page.Liked:267-334), Search facet chips (Search.UI:73),
  Profile list chips (All/Artists/People), Browse "Top" band pills (Browse.UI:46+ Word cells), podcast badge chips
  (Controls.Podcast:56); the Home facet row is a `SelectorBar` restyled (`Design.FacetTitleStyle`), the Recents pivots are
  `PivotTabs` (Recents.Page:848), the Concert when-pill is `FusedPill`/`SegmentedPillStyle` (Concert.UI:38). → five chip-like
  vocabularies on main (PR 205 commit 5a3154fe "one chip everywhere" unifies some).
- **Word rails** (`Controls.Words.Rail`): library navigator sort rail, reader scope/sort rails (User.UI:101-162), show reader
  filter/sort/date rails (Show.UI:196). This is the one explicitly "Zune-ish" primitive on main (User.UI.cs:85, Controls.Words.cs:2).
- **Jump index / letters**: `JumpIndex.cs` kernel; `LibraryLetters` User.cs:1119; sticky letter overlay + A–Z strip User.UI:275;
  profile lists reuse (Profile.Lists.Page:388); show reader date rail (Show.UI:867, `ShowDateIndex` Show.Rules:572).
- **Edges / staging** (`Edges.cs`, `Edges.Staging.cs`): CSR relation tables (`EdgeState` :58 Unknown/Partial/Complete/Failed,
  `EdgePending` :77 optimistic bits), payload records (AlbumTrackEdge, PlaylistTrackEdge, LibraryEdge, RootlistEdge, QueueEdge,
  DiscographyEdge, FriendEdge, CreditEdge, VersionEdge…), `MerchTable` :797. Not presentation; the readiness vocabulary every page
  paints from.

---

## 3. Per-page sections

Each entry: files/types · what it renders · states · split · switches · verdict. (Shared pieces are referenced, not restated.)

### 3.1 Home (`Home/*`, `Entities/Home*.cs`)
- **Model → view pipeline (pure):** `SectionReader.Read` (Model.cs:72, the ONLY table reader in `Wavee.HomeUi`) → `ZonePlanner.Plan`
  (ZonePlanner.cs:275; `SectionRoles` :48, `ReleaseDetect` :106, `ClusterFold` :145, `RadioMerge` :217, `PageDedupe` :246;
  `DaylistSource.Find` DaylistSource.cs:30) or `PodcastPlanner.Plan` (PodcastPlanner.cs:49, rules 1-12) → `ScreenModel(Facet, Zones,
  Greeting, Chips)` (Model.cs:60) with `Zone(Kind, Key, Title, Subtitle, Items, Lead, Clusters…)` :42 and `ZoneKind` :25 (16 kinds).
  Facet state machine `FacetPivot/FacetSwitch/FacetCache/FacetRoute` (Facets.cs:25-302), `FacetDimPlan` (Facet.Rules:29),
  `ScreenLoad/WashPick` (Reveal.cs:30/53), pure captions `Time.cs` (`DaypartRules`, `WeekSummary`, `WhenCaption`, `DaylistNext`),
  `EpisodeCaption.cs`, `RecentsCells.cs`, `Items.Rules.cs` (`DaylistForm`, `DaypartTimeline`, `DaylistCountdownLine`, `DaylistArt`,
  `HomeCardGridShape`), `ChartTilePick` (ChartTiles.Rules:43), `ReleaseListRules/ShelfLead` (Zones.Rules), layout document
  `LayoutFile.cs` (v2 json, reducer `LayoutCommands` :132, `HomeLayout` service :387). Data: `Entities/Home.cs` (HomeTable/SectionTable,
  `HomeCard` :1395 live handle, `HomeFeedReadiness/HomeRevealGate` :1086-1203, demand `Home.EnsureFeed/EnsureSection` :1206),
  `Home.Rules.cs` (`HomeWashSource` :41, `HomeCardText/HomeCardAccent` :81/:152, routes, `HomeCardNav` :369), `Home.Host.cs`
  (install :39-55, feeds :67+).
- **Views:** `HomeScreen` (HomeScreen.cs:64): ZStack[`Palette.ShellTint`, ScrollView(keyed per published facet) → centred column
  (MaxWidth PageMaxW, ScrollScope `Facet.PageScope`) → `FacetRow` sticky at `Facet.StuckInset` (transparent; content clipped at
  `Facet.StuckBottom` with `WhileStuck` feather :29-34) → `SkelRegionEl`(keyed facet column → dim host (opacity bind) → `Zones.Column`,
  shimmer `Zones.Column(Seed)`, failed `Controls.Vacancy(Error, retry)`)]. No greeting headline (:26). Facet history/deep link :36-40.
  `FacetRow` (Facet.UI:97): `SelectorBar` in `Design.FacetTitleStyle`/`FacetCompactStyle` (`FacetForm` edge) · Following `ToggleButton`
  · 3-px indeterminate `ProgressBar` (opacity bind) · `InfoBar` on Failed (below the band, pushes content). `Zones.Column` (Zones.UI:116)
  one chapter per zone: `BoxEl{Key "zone:"+key, ScrollScope, Animate = Design.Entrance.Row(i)}` → `ModuleHeader` sticky at
  `Facet.StuckBottom` (paints nothing) + body `StickyClip` (:33-43). Dispatch :57-70: Daylist → `Daylist.Card` (Daylist.UI: `Ui.Card`
  ≥320 tall, `Controls.CoverFill` art + `Palette.ArtistHeroVeil` horizontal veil + copy column; `DaylistClock` :364 Counting/Updating/Late
  faces; `DaylistCountingClock` 1-Hz tick by binds only; `SkeletonProxy` :42-44); RecentGrid → `RecentGrid` (:408) `GridEl` of
  `Surface(RowTile)` ×(2·cols−1) + history tile `RowOutline` with `SparkBars`; Cover/Mixed/Radio shelves + ShowGrid → `PagedShelf` of
  `HomeCards.ShelfCell` with 2-span lead (`ShelfChapter` :382 owns the controller; pager lives in the sticky header's tools :45-55);
  WideTiles/VideoTiles → 16:9 `ShelfCell`; ReleaseList → `Ui.SectionHeader` + 2-col `Surface(Row)`; ClusterCards/PodcastGroups →
  `Ui.Card(SectionHeader + ≤3 Surface(Row(40)))`; BrowseTiles → `SettingsCard` tiles (`Controls.TileCardStyle`) + `ChartsBlockView`
  :728; EpisodeLead/Rows/Continue → `Surface(Episode.RowData, EpisodeRow)`; EmptyFacet → `Controls.Vacancy(Empty → Browse)`.
  Section screen (`SectionScreen` SectionScreen.UI:37): `ItemsView.CreateBound` over `RepeatLayout.GridFit` of `BoundSurface(Shape.Grid)`;
  load rule `SectionScreenLoadRule` (Pending/Ready/Empty/Failed). Customize (`CustomizeScreen` Customize.UI:35): `ToggleSwitch` rows +
  `Reorderable` over `HomeLayout`.
- **States:** facet Loading dims .6/167 ms (Facet.Rules), Refreshing busy bar, Failed InfoBar+Retry, cold-load derived shimmer (same
  builders fed placeholders), EmptyFacet vacancy, card "Playing now"/equalizer via bound `Prop` over `Playback.Current`
  (RecentsCells.cs:14-18, `Controls.RelatesNow`), sticky facet band + zone headers, zone entrance stagger (40 ms behind the last),
  keyed Exit/Enter on facet swap (Facet.Rules:13-17), `Design.Reduced` not read directly here (engine handles).
- **Switches:** `DaylistClock` (Daylist.UI:400; Playlist.UI:990 for the strip), `ColorWashes` via `Palette.ShellTint` (HomeScreen:480),
  `HideTrackArtwork` not read (cards always show art). No TrackRowStyle, no density.
- **Verdict:** the best-factored page: pure planners produce records; views are "stock controls over the shared surface"
  (Zones.UI:8-14). Coupling: the view files still own geometry constants (Zones.UI:88-115 grid tables), the sticky idiom, the
  entrance choreography, and the Daylist card composes `Palette.ArtistHeroVeil` directly. Score **4.5/5** — a theme could swap
  `Zones.Column`/`Daylist.Card`/`FacetRow` wholesale against `ScreenModel`.

### 3.2 Browse (`Browse.*`)
- Directory (`DirectoryPage` Browse.Page:92): masthead band paints nothing (live Mica); the directory clips itself under it with
  `StickyClip(84)` + 24-DIP feather (:25-27); bands cascade (`Design.Entrance`, Browse.UI:28-33); `ChartsBand` :211 is its own component
  (two retries). Five cell densities `BrowseTiles` Browse.UI:48 (Word/Name/Link/Bar/Peek — "the category colour is a wash + a tick,
  never the plate" :19-21; every cell is a link, no menu/drag :23-26). Category page (`CategoryPage` :295): `BrowsePageLayout.Mode`
  (Rules:275) decides Shelves (page ScrollView of `PagedShelf`+`ShelfCell`) vs FlattenOne (`HomeModules.SectionGrid` virtual viewport
  :36-43) vs FlattenTwo*; masthead title published to `Shell.Mastheads` (:33-34). Readiness read off columns (`BrowseLoadGate` :73:
  Pending/Ready/Failed); explicit non-virtual shimmer (:42-43) revealed FadeOnly.
- Rules (`Browse.Rules.cs`): `BrowseTaxonomy`, `ChartPages/ChartSections`, `BrowseDirectorySeeds`, `BrowsePageLayout`,
  `BrowseMastheadMetrics` :341, `BrowseLayout` :361 (column functions), `BrowseTiles.ToModel` → `BrowseTileModel` :408.
- Switches: none (no pref reads). Colour: category `uint?` from the wire → `Design.Palette.ToColor`; `Tok.*` only.
- Verdict: rules pure, pages compose stock pieces; the five cell densities are hand-built element trees in Browse.UI (a theme would
  replace them). Score **4/5**.

### 3.3 Search (`Search.*`)
- `PageHost` Search.Page:81: query frozen per mount (keyed `search-page:<q>`), ONE `Entities.Ensure(all, SearchFields.All)` :20-24,
  readiness enum :79 (Pending/Ready/Concluded/Failed; `Failed` = un-asked after asking → error vacancy with Retry :28-31), facet body
  keyed `facet-body:<facet>` with `MotionRecipes.PageSlideForward/Back` only on a real chip change (:35-37), `smoothResize:false`.
- `Search.UI.cs`: facet row + 11-pill skeleton :73, three loading shapes (`ShimmerShape` Hero/CardGrid/Rows) :193, `HitData` :260,
  **`HitRow` = `Controls.Surface(Shape.Row(48)|RowLarge)`** :365 (title/art/subtitle via `HomeCard`, track hits via `Track.RowData`,
  flags `Search.FlagsOf`), top result :415, best matches/playlist rail/songs grid :525, albums/playlists grid :615 (`Surface(Shape.Grid)`),
  flat lists :649, genres/related :679, empty/error :776, `SearchHitRules` :804 (which kind follows/saves/drags, `Trailing`
  None/Follow/Save).
- Rules (`Search.Rules.cs` :274-521): `FacetsFrom/FacetCount`, fallback interleave (`FallbackRow` :348), `ColsFor` with hysteresis,
  playlist rail items, genre route, shimmer shape, per-facet empty sentence. `Search.Subtitle.cs` shared with the omnibar.
- Switches: `HideTrackArtwork` Search.Page:136 (→ `HitContext.HideTrackArt`). Verdict **4/5** (every factory is a static function
  over handles; the surface draws the row).

### 3.4 Album (+ prerelease, pane) (`Album.*`)
- Page (`PageHost` Album.Page:146): hands `Detail.Frame` a `FrameSpec` (Config.Album/Single/Compilation via `Config.For`) + four slots
  (:17-22): Attribution → `FacePileHost` (Album.UI:51, "+N" flyout), PreRelease → `CountdownHost` (:290, three states incl. Bare),
  ReleasePanel → `ReleasePanelHost` (:463, About this release + Other versions), Trailing → `TrailingHost` (:625) holding ONE reserved
  skeleton region (`PageRules.TrailingShape` Album.cs:1027) and seven fail-soft sections (`SectionsHost` :887: about the artist
  (`Controls.ArtistAbout`), featured on, more by (`StackHost` Album.UI:368 capped 5 + "Show all N"), merch, similar, music video
  (:1219, `AlbumVideo/VideoArm/WatchAction` Album.cs:1135-1150), fans). Table profile with the `Drawer` seam (Track.Drawer).
  Demand: GateAlbumPage pattern (:25-32). Identity stamps `IdentityInputs/PageStamp` :176-184 (render-cost fold). `SurfaceMetrics`
  :1254. Trailing skeleton :1508 (section skeletons with 160×18 header bars).
- Pane (`Album.Pane` Album.Pane:60): library master-detail right column, ONE anatomy at every width; readiness `AlbumPaneState`
  (User.cs:1242 Header/Rows/Failed/Ready); header never skeletons past identity (:31-33, fixed `PaneHeaderHeight`); counted shimmer
  (:27-30); keyed-by-slot crossfade `s_paneSwap` (:23-26); "Also by" strip handed as `TableArgs.Trailing` (:34-37). Row height reads
  `RowDensity` :192. `PaneHeader/PaneCommands` statics in Album.UI:467+ (shared with Artist.Reader and Show.Pane).
- Rules (`Album.cs` §Wave 5 :673+): `ReleaseFactsRules` :731, `Upcoming` :823, `DrawerVerdict` :913, `PageRules` :960.
- States: frame skeleton + ONE trailing skeleton that latches after `TrailingDeadlineMs`; Partial tracklist paging; notice ladder
  (`NoticeRules.ForAlbum` → unavailable/minified); failed strip with Retry in the pane; upcoming (countdown) vs released; compilation
  (per-track artist). Verdict **4/5** (page = FrameSpec + slot builders + demand; slot bodies are self-subscribing components).

### 3.5 Artist (+ discography, chart, reader) (`Artist.*`)
- Page (`PageHost` Artist.Page:82, `IPropsHost`): ONE `ArtistHeroMetrics` per render feeds hero, band gutter, magazine gutter, wash
  (:27); chrome accent is a WATCHED signal `_accent` (:28-30); context band = `Detail.Band/BandTitle/BandHairline/Pivot` +
  `Detail.BandLayout` (:32-39) with page-owned section anchors + spy; sections :462 (Top-tracks band :723, shelves :1135 — appears on,
  music videos, playlists, concerts, merch, gallery, fans also like — biography :1409, banners :1560-1770 upcoming/latest/tour);
  skeleton :1072 (derived; `Design.Reduced` :652/:662/:668); Retry error state; theme published to `_theme` from `Tok.Theme`
  (:144,:193,:238,:291) — the page keys washes/veil on polarity.
- Hero (`Artist.UI.cs`): tiers `Narrow/Compact/Medium/Wide` (:51; widths 360/600/880 + 24 hysteresis :69-72; heights 440/384,
  stacked photo 200/176 :74-90; `PhotoHeightFor` :94; parallax 0.15 :102; `CollapseDistance` :136); leaves :357+ `HeroText` :366,
  hero banner (media · veil · copy), `HeroArt` :610 (entrance opacity `Design.Reduced ? 0 : Motion.Standard` :658; photo zoom-in —
  deliberate production choreography), artist pick :686 (`PickPhoto` :888), gallery lightbox :986. Hard-coded: gradient
  `FromRgba(0,0,0,158→0)` :993-994, lightbox scrim `FromRgba(0,0,0,224)` :1054, transparent placeholder :1091, border :1273.
- Chart (`Artist.UI.Chart.cs`): `PagedShelf` of `ChartRow` components (5 rows/page, ≤2 columns, pip pager); keys include
  `|art|noart` × `|classic|modern` (:20-21, :79-80, :412); `ArtistPopularLayout.Tier` (UI:167, Classic tier :839); Classic rows
  48/hairline/square vs Modern 56/plate (:705-823); reads `TrackRowStyle`/`HideTrackArtwork` :176-177; failure gate `GateState`
  :333; `ChartSkeleton` :834 also forks on classic.
- Discography (`Artist.Discography.cs`): `DiscoFacet` :55, era bands :57-270 (pure), `DiscoRoute` :277, `DiscoCardText` :326,
  `DiscoCellStamp/Rules` :392-423 (pure), facet section `FacetHost` :583, `DiscoGridHost` :823 (LazyGrid over the facet edge's
  `Total`), `DiscoCell` :1032 (own component per cell), `DrawerPanel` :1174 (album drawer under the grid, `DrawerVerdict`), disco
  page `DiscoPageHost` :1648.
- Reader (`Artist.Reader.cs` UI + `Artist.Reader.Shape.cs` CORE + `Artist.Reader.Policy.cs`): the library's artists right column —
  one scroller of album blocks (art + tracks), sticky band + sub-rail (scope/sort word rails) as a persistent prefix, spine of dots
  (second virtualized list), `BlockCell` :1371; width arms NARROW <640 / MEDIUM (Shape.cs:63-80: cover 120→88, stacked album head
  `HeadStackedH` :174, stacked band `BandStackedH` :205); mount identity `ReaderMountPolicy.MountKey`; catalogue gate `CatalogueReady`.
- Rules (`Artist.Rules.cs`): `ArtistReadiness` :41 (chart gate :114), `ArtistText` :272, `ArtistPopularTracks` :374, `ArtistTour`
  :467 (`TourArm`), `ArtistCatalog` :521 (`LinkKind` glyph classifier :554), commit derivations :614.
- Switches: `ColorWashes` :295, `TrackRowStyle` :296, `HideTrackArtwork` :297 (Artist.Page), chart :176-177, `Design.Reduced` ×5,
  `Tok.Theme` ×4. Verdict **3.5/5**: hero/band/wash composition is in the page host (1,789 lines) with latched geometry fields; the
  chart and discography are tidy components; the reader is a bespoke list with its own shape engine. Replacing the artist "look"
  means replacing `Artist.UI.cs §4` + `Artist.UI.Chart.cs` + the band wiring in `Artist.Page.cs §2.2` against `ArtistHeroMetrics`,
  `HeroText`, `ArtistPageFacts`, `ArtistSections` — all of which are already values.

### 3.6 Concert (hub, artist schedule, detail) (`Concert.*`)
- `Concert.Page.cs`: hub (`HubPage` :250; feed body = shelves · promos · virtualized grid · append tail :504), `FilterBar` :691
  (when/where pills, tokens), `SchedulePage` :824 (`MonthBoard` :1119, `ShyMonthPill` :1293 + `LingerClock`), `DetailPage` :1445
  (`DetailView` :1381 record = a view-model), `CountTicker` :132, `AppendTail` :201; 7 vacancies. Props freeze; every child takes
  signals/keyed remounts (:18-21).
- `Concert.UI.cs` ("PRESENTATIONAL ONLY… pure functions of VALUES" :10-11): event tile :69 (`TileData` :73), date blocks/split hero
  :226 (`SplitHero` :251: `DarkOr` #1B1B1D vs `Tok.FillCardSecondary` :134 — explicit dark/light fork; accent lerp 0.30/0.18),
  filter pills/tokens :310 (`SegmentedPillStyle` :38 public), flyout chrome :434, when/where flyouts :474/:646, `PlacePicker` :670.
- `Concert.Rules.cs`: values (`ConcertShow/Place/Concept/When/HubFilters/FeedQuery`), `ConcertTime` (provider-local clock),
  `ConcertCopy` (ICU plurals), `ConcertLayout` :244 (hysteresis, `EditorialHeroMetrics`), `EditorialArtGeometry` :305 (Home's promo art),
  `ConcertHub`, `ConcertScheduleShaping`, `ConcertOffers`, `ConcertDetailInfo`, `ConcertFeedKey`.
- Accent is the PROVIDER's, never palette-graded (Concert.cs:20-22). Verdict **4/5** (UI file is value-in/element-out; the page host is
  the composer).

### 3.7 Playlist (+ local files, open rule) (`Playlist.*`)
- `PlaylistPage` Playlist.Page:270 over `Detail.Frame` (Config.Playlist) with slots (:19-24): Cover → `CoverEditor` (Playlist.UI:185:
  hover scrim `ScrimHover/ScrimDrop/OnScrim` hard-coded :101-103, JPEG drop, saving chip), Title/Description → `InlineEditor` :333,
  Attribution → `OwnerBlock` :523 (+ `AccessPanel` :787 invite & access), Pulse → `DaylistHost` :959 (flip countdown; reads
  `DaylistClock` :990), LikedFacts → `User.FactsPanel`. Table profile: Editable, Deposit/MoveRows/RemoveRows, Recommendations →
  `RecsSection` :1090 (`RecRowHost` :1203), Tune :1240, Drawer. Demand through THE OPEN RULE (`ListOpenPolicy/ListOpen`
  Playlist.Open.cs:38/261; `HeldRows` :814 holds the reveal on a /diff ≤ `BlockingBudgetMs`). Owner items + deposit submenu :901,
  playlist verbs :1028, local files :1129 (`Playlist.LocalFilesUri`, drop/pick/import).
- Data/rules (`Playlist.cs`): `PlaylistFacts` :314 (column-existence facts derived at commit: AddedBy/DateAdded/Video), `DetailNotice`
  :172 (a COLUMN), `PlaylistRowsState` :189, edit errors/deposit targets/tune menu :1136-1324, local uri codec :1325.
- States: notice strip (deleted/create failed/…), Loading/Empty/NoMatch/Rows, held reveal (Stale), optimistic cover/title/description
  (`OptimisticSwitch` Playlist.UI:53), edit-error toasts, collaborative pile vs owner row, daylist strip, chart caption.
  Verdict **4/5** (same shape as Album).

### 3.8 Liked Songs (+ cover treatments, facts/insights) (`User.Page.Liked.cs`, `User.Cover.cs`, `User.Liked.cs`, `User.Facts*.cs`)
- `LikedPage` User.Page.Liked:56: identity (never `HeaderPending`), dynamic cover slots (`User.LikedCover`/`LikedArtwork`/`LikedChipArt`
  contract User.Cover:49-117), facts slot (`FactsHas/FactsPanel`), chip rail via `TableProfile.ContentFilterBar` (`LikedChipBar`
  :297, extent 48 only when chips exist :19-21), lens header via `TableProfile.LensHeader` (36), `LensCell` bridge (User.Facts.UI:47)
  — "configured only through FrameSpec / TableProfile, never by editing Detail*/Track*" (:4).
- Cover: `LikedCoverStyle` (User.Liked:26, `StyleCount`), site ladder `LikedCoverSite` :40 (Treatment/MiniTone/MiniMosaic/Stock — the
  honesty ladder: below a style's floor → bundled PNG :16-17), `LikedCoverRules.Tiles` (newest-first liked prefix, dedupe); treatments
  on a 304 canvas (User.Cover:13-17), ambient loops (`still = Design.Reduced` :694), picker + flyout (writes `LikedCoverStyle` :1118).
- Facts: `LikedFactsRules` (User.Facts:39; week/years/tempo/artists/blend/rediscover/since-line; `FactsPlan` :874 which cards earn a
  card), `FactsPanelHost` (User.Facts.UI:212; summarises once after 250 ms quiet :23-26), card hosts :430-1438, pills :1439, lens
  header :1551. In the vertical arm the panel is the `InsightsSheet` (Detail.Insights).
- Switches: `LikedCover` ×3, `Design.Reduced`, chips/lens extents. Verdict **4/5** for the page, **2/5** for the cover treatments
  (bespoke hard-coded canvas art — a theme would own them wholesale).

### 3.9 Library (albums · artists · podcasts · audiobooks) (`User.Page.Library.cs`, `User.UI.cs`, `User.NavMount.cs`, `User.cs §8-11`)
- `LibraryPage` User.Page.Library:196 (keyed per kind): WIDE `lib:wide` = `NavPanel`[toolbar (PageHero + count · `User.WordRail` ·
  `User.ViewToggle` · filter box) · `ListBody`] │ `ColumnGrip(LeftW)` │ right (`Album.Pane` | `LibraryShowPane`/`Show.Pane` |
  `Artist.Reader`); COLLAPSED `lib:collapsed` (640/24 hysteresis `LibraryLayoutBreakpoints` User.cs:675) = `CrumbBar` + depth 0/1;
  SEARCH mode keeps three columns under `MidW` (:1340). Navigator = `ItemsView.CreateBound<LibraryNavItem>` over a memo
  (`ComputeShape` :507 → `NavShape` :151; mount identity `LibraryNavMount.For(view, size, alphabetical, lettersKey)` NavMount:60 →
  `LibraryNavTemplate` Row/RowCompact/Card/CardCompact + `LibraryNavLayoutKind` Extents/GridFit); shimmer = eight `Skeletons.ListItemRow`
  cross-dissolving (:39); sticky letter + A–Z strip (`User.StickyLetter/JumpStrip` User.UI:275); a–z gate waits for titles
  (`LibraryNavReadiness`). Persisted per kind: `LibraryLeftW/MidW/Sort/Desc/View/Size/Selected/AlbumKey/AlbumSort/AlbumDesc/
  AlbumView/AlbumSize/Scope` (Platform.cs:355-372; seeded in the constructor :20, committed :379-380). `PodcastsMode` :159 (Followed
  shows · Your Episodes → `SavedEpisodesReader` User.SavedEpisodes:19 = Track.Table with episode rows, `PlaysColumnOptIn=false` :29).
- Row shapes (`User.UI.cs §6` :396): bound navigator rows/cards + grid selection chrome; `LibraryNavItem` :51; search-row shapes :613;
  view codes 0 CompactList/1 List/2 CompactGrid/3 Grid (:78-83); `ViewToggleHost/ViewPanelHost` :178/:226 (S/M/L sizes).
- Pure core (`User.cs`): `LibraryNavOrder` :912 (ONE ordering rule), `LibraryNavSorter` :818, `LibraryLetters` :1119,
  `AlbumPaneReadiness` :1255, `LibraryHits` :1324 (cache-only search), per-artist library reads :1524.
- Switches: all `Library*` keys, `HideTrackArtwork` :1397 (compact show pane episodes), word rail (Zune-ish). Verdict **3/5**: the
  navigator contract is a value (`NavShape`, `LibraryNavMount`), but the page host is 1.5k lines of layout arms + keyed branch roots
  whose correctness depends on reconciler keying (:31-37, :53-70); the three right columns are three unrelated bespoke components.

### 3.10 Recents (`Recents.*`)
- `PageView` Recents.Page:112: hand-rolled `SurfaceDisplay` masthead (:29-31; publishes nothing), `PivotTabs` :848 (content-type
  chips), measured grouped list `ListSurface/RowSlot` :931/:966 (`ItemsView.CreateBound`, pinned extents), `StickyDayHeader` :1019,
  drawer accordion (:521; `Track.EagerRow` children / `Surface(Episode.RowData)`), annotated rail `RailView` :1054, semantic zoom +
  `CalendarSurface/CalendarBand/MonthCard` :1096-1149/Recents.UI:425, viewport-following accent + ONE wash leg (`AccentBinder`
  :1155 — reads `ColorWashes` :1162/:1205, `Palette.Watch`), `OwnerDemand` :1211. ONE immutable `Shape` swapped atomically
  (:17-22; two epochs). Row = `Controls.Surface(…, Shape.Row(48))` pinned to the 64 band (Recents.UI:11-16); nothing removed to
  make room (:18-20 spacer/quiet bars). Art hidden read Recents.UI:296,316.
- Rules (`Recents.cs`): `RecentsSnapshot` :239, `RecentsView` :325 (chips, hydration targets, drawer, calendar, formatting, owner
  names, viewport accent), `RecentsLayout` :713 (zoom anchors, recycling/heat/clock/mount demand).
- Switches: `ColorWashes` ×2, `HideTrackArtwork` ×2, `Design.Reduced` (:294 BodyFlip, :330 stagger; UI:67 accent ms).
  Verdict **3.5/5** (rules pure; the page view is a large bespoke composition with its own masthead).

### 3.11 Profile (+ Following/Followers lists) (`Profile.*`, `User.Profile*.cs`, `User.Follow.cs`)
- `Profile.PageHost` Profile.Page:49 (template = Artist.Page: band/spy/sentinel/clip copied :8): four faces (`ProfileLoadRule.Header`
  Loading/Ready/Unavailable/Failed :11-12), derived shimmer :683, tone (`ProfileTone` Profile.Rules:215 — palette url or payload
  ARGB :218-221), blend wash + shell tint (:657; `ColorWashes` :242), context band :469 (title · pivot · Follow word), one
  `SkelRegion` per section. Theme signal :102-238 like Artist.
- `Profile.UI.cs`: round-avatar hero (:34; `HeroText` :38, `HeroActs` :51; `ProfileHeroMetrics.Stacked` tiers Profile.Rules:124-166),
  sections :265 (headers = `Controls.AccentHeader`, shelves, seeds, vacancies ×4), cards :379 (playlist/artist/person/ranked artist =
  `Controls.Surface`; mosaic via `Controls.MosaicTiles`). Type aliases per tier (ArtistDisplay/ArtistTitle/ArtistCompactTitle; StatHero).
- Lists (`ProfileLists.PageHost` Profile.Lists.Page:67): head (breadcrumb · `PageHero(facet)` · `SelectorBar` that NAVIGATES) + ONE
  `SkelRegionEl` (Pending shimmer; Ready = chips + find over `ItemsView.CreateBound` of letter headers | rows of N cards + sticky letter
  + jump strip; Empty/Hidden/Unavailable = vacancy as content; Failed = vacancy + Retry) (:10-18). Pure half `Profile.Lists.cs`
  (`ProfileListLoadRule` :34, `ProfileListFilter/Model` :88/:140, `ProfileGridFit` :219 → `ProfileGridPlan`, `ProfileLetterRows` :261).
- Verdict **4/5** (the lists page is a textbook "rules decide, view paints" split :15).

### 3.12 Show (+ pane) (`Show.*`)
- `PageHost` Show.Page:86 → `Detail.Frame(Config.Show)` with the six podcast rail slots (`BadgesSlot/RatingSlot/LedgerSlot/PrimarySlot`
  :1126-1363, Attribution = publisher, Satellites ♥·trailer·🔔·share·⋯, blurb) and right = `ReaderHost` :679: `SkelRegion` →
  `ItemsView.CreateBound` over `ReaderSnap` items with `PersistentPrefixCount = 1` ([0] RAW sticky `RailBody` Show.UI:196 (filter
  words + counts · FindBox · sort words = `Controls.Words.Rail`), [1] `VisitHead` :368 (New: doors+about │ Returning: hero+up next+new
  since │ CaughtUp │ shimmer), [2] `EpisodesHeader` :795 (+ empty arm), month `Group` | `Episode.ReaderRow`, `ReaderFoot` :970 load
  more). ONE model `ReaderModel.Snap` (:30-37); measured toolbar `ShowToolbarLayout` (Show.Rules:500; `ToolbarStage`); date rail
  :914/Show.UI:867; selection `EpisodeSelection`. View prefs per show persisted as a blob `Platform.Keys.PodcastViews` (:264,:414;
  `ShowViewPrefs` Show.Rules:700).
- `Show.Pane` Show.Pane:62: library pane — header = `Album.PaneHeader` (one geometry for both kinds), body = the SAME reader
  (`Show.EpisodeReaderBody`), re-skins in place (selection is a tracked signal :24-43), `s_paneSwap` for skeleton→body.
- Rules (`Show.Rules.cs`): `ShowVisit` :34, `ListenNext` :64, `ShowLedger` :126, `ShowCadence` :192, `DateKeys` :256,
  `ShowReaderShape` :391 (`ItemKind` Rail/Head/Header/Group/Row/Foot/Similar), `ShowToolbarLayout`, `ShowDateIndex`, `AudiobookOrder`,
  `EpisodeNeighbours`, `ShowViewPrefs`; `ShowReaderRules` Show.Page:1369 (`Head` Pending/New/Returning/CaughtUp/Unavailable, `Primary`
  PlayLatest/PlayFirst/Follow/Resume, results mode, `MiniLeadKind`).
- Verdict **4/5**.

### 3.13 Episode (+ reader, discussion) (`Episode.*`)
- `EpisodePage` Episode.Page:24 → `Detail.Frame(Config.Episode)` with slots Episodes → `EpisodeReader` :221 (show notes rich text,
  chapters `ChapterReader` :442, pivot + `Detail.ScrollSpy`, transcript `TranscriptReader` Episode.Reader:13, recommendations/similar
  shelf :101/:115, rating editor :170, discussion `PodcastReaderUI.Discussion` Episode.Discussion:167 with `CommentCard/ReactionFlyout`,
  layout `DiscussionLayout` :550, consent setting read :179), Primary → `EpisodePrimary` :96, Attribution → `EpisodeAttribution` :120,
  Satellites → `EpisodeActions` :142, About → `EpisodeRailAbout` :194. Error faces :62-64 (`Vacancy(Error)`, `PodcastReaderUI.Failed`).
- Row (`Episode.ReaderRow` Episode.UI:311): states REST/HOVER/IN PROGRESS/PLAYED/NOW PLAYING/NEW/ROW=LINK/NARROW (:23-32); binds over
  `RowItem(Episode, Version, RowMarks, Number)` :69; `RowContext/RowSelectionContext` :77/:99; metrics :105; pure pieces :127
  (`ReaderPct`, "N min left", duration words, stripped title); `RowData` :655 (`RowFacts/RowClock/RowOptions/RowPlan`) feeds
  `Controls.Surface(Shape.EpisodeRow)` for Home/Recents.
- Menu (`Episode.Menu.cs`: grammar :13-19; MarkPlayed/MarkUnplayed/GoToShow verbs), progress (`EpisodeProgress` :29, three writers),
  `Episode.Rules` Episode.cs:611 (`Status`, `ResumeArm`, completion rule ≥.98 or ≤30 s).
- Verdict **4/5** (the row is a bound template with pure rules; the page is slot composition).

### 3.14 Queue (`Queue.*`)
- Not a route: `Queue.InstallUi` fills `Rail.QueueBody` (rail panel) and `Stage.QueuePaneBody` (stage pane) (Queue.UI:11-16).
  `RailPanel` :409 = pills · `PlayingFrom` · now-playing card · lane[headers · rows · Show more] · vacancy; `StagePane` :957 = rows
  under the stage skin (glass rows). Rows are NOT `Track.Row` (:21): Modern rows and the now-playing card are `Controls.Surface` over
  `Track.RowData`/`Episode.RowData` (:22-25, `QueueRowRules.ShowsMenuButton`); **the Classic skin is its own tree** (`ClassicRow` :613,
  `ClassicNowPlaying` :673, `ClassicIdentity` :902, `ClassicHairline` :925; `ClassicExtent` 44 :1477) — 44 classic branch sites.
  No virtualization (plain keyed children, Enter/Exit/Slide, pagination at 100 :29-32); ONE `Reorderable` spans the lane; `QueueMovePlan`
  decides drops. Divider rule :34-36; each render subscribes queue edge, row tables, deck state and the settings epoch.
- Switches: `TrackRowStyle` :468, `HideTrackArtwork` :469/:985, `AutoplayEnabled` :326/:948. Raw icon font ×6.
- Rules: `QueueSlots/QueueMovePlan/QueueOrder` (Queue.Rules), `Queue.cs` cursor/buckets/mirror decisions.
- Verdict **2.5/5**: two full skins hand-built inside one UI file, keyed `":classic=True|False"` (:665,:708).

### 3.15 Module page (playback modules) (`Platform/Modules.UI.cs`, Sdk `ModulePage.cs`)
A module answers `module/page` with a declarative `ModulePageDoc` (Version, Template entity|custom|watch, `PageHero`, `PageAction[]`,
`PageSection[]` of kinds text|facts|playables|cards|links with `PageItem`s; budget `ModulePageBudget`) and `Modules.UI` draws it with
the app's own vocabulary ("nothing switches on a module id" Modules.UI:10-13; three states via `Skel.Region` :16-18; watch template
hosts the composited video stage outside the scroller :20-24). **This is the one existing "data contract + app-owned view" page**,
and the only plugin surface in the product — but it is for *content*, not for presentation (§6.3).

---

## 4. Presentation switches — the complete table

| Setting (key, Platform.cs) | Reader | Sites in Entities/Home (path:line) | Count |
|---|---|---|---|
| `detail.rowstyle` TrackRowStyle (0 Modern/1 Classic) :116 | `Prefs.Appearance.TrackRowStyle()` Prefs:98 | Track.Table.cs:612; Artist.Page.cs:296; Artist.UI.Chart.cs:176; Queue.UI.cs:468 | 4 reads → ~324 `classic` branch mentions (Track.UI 75, Track.UI.Bound 73, Artist.UI.Chart 49, Queue.UI 44, Track.Rules 31, Track.Table 27, Track.Table.Chrome 20, Artist.Page 3, Detail.cs 2) |
| `detail.rowdensity` RowDensity (0-3) :115 | `Prefs.Appearance.RowDensity()` :88 | Track.Table.cs:644 (+SetDensity :903); Track.Table.Chrome.cs:804,807,821 (+ raw Get :359); Album.Pane.cs:192 | 6 |
| `appearance.trackArtwork.hidden` :119 | `TrackArtworkHidden()` :58 | Track.Table.cs:612; Artist.Page.cs:297; Artist.UI.Chart.cs:177; Queue.UI.cs:469,985; Recents.UI.cs:296,316; Search.Page.cs:136; User.Page.Library.cs:1397 | 9 |
| `appearance.marquee.enabled` :142 | `Marquee()` :64 | Track.Table.cs:612 (→ `RowPresentation.Marquee` :2187; TitleCell UI:344) | 1 |
| `appearance.colorWashes.enabled` :144 | `ColorWashes()` :73 | Detail.UI.cs:669; Artist.Page.cs:295; Profile.Page.cs:242; Recents.Page.cs:1162,1205 (→ every `Palette.ShellTint/PageTonePlane/ArtistHeroVeil/ArtistBlendWash` `disabled:` arm) | 5 |
| `appearance.likedCover.style` :123 | `LikedCover(count)` :110 | User.Cover.cs:93,141 (+ Set :1118) | 3 |
| `appearance.daylistClock` :139 | `DaylistClock()` :130 | Home/Daylist.UI.cs:400; Entities/Playlist.UI.cs:990 | 2 |
| `detail.tempoColumn` :130 / `detail.playsColumn` :133 | epoch-gated raw reads | Track.Table.cs:583-592,662,667; Track.Table.Chrome.cs:346,881-889 | 7 |
| `detail.page.layout` DetailPageLayout (0 Automatic/1 Hero) :135 | `Prefs.DetailHero.PageLayout()` :290 | Detail.UI.cs:670,703 | 2 |
| `detail.rail.uniform` + per-scope `detail.rail.*.width/collapsed` :169-181 | `RailUniform()` :300; raw Get/Set | Detail.UI.cs:671-672, 419-421, 471-489 | 8 |
| `detail.sort.col/desc:<ctx>` :351-352 | raw | Track.Table.cs:890-921 | 4 |
| `library.<kind>.*` :355-372 | raw (seeded in ctor) | User.Page.Library.cs:379-380 (+ ctor seeds), User.UI view codes | ~10 |
| `podcast.views` :194 | raw | Show.Page.cs:264,266,414 | 3 |
| `playback.autoplay` :231 | raw | Queue.UI.cs:326,948 | 2 |
| `playlist.deposit.recents` :187 | raw | Playlist.Page.cs:1005-1025 | 3 |
| `appearance.pageMotion.style` :127 | `PageMotionStyle(count)` :120 | (shell only — `Design.Nav`) | 0 in pages |
| `theme.mode` :111 / `Tok.Theme` | engine epoch | Artist.Page.cs:144,193,238,291; Detail.UI.cs:514,587,656,684,735; Profile.Page.cs:102,163,206,238; Concert.UI.cs:134,251; Track.Drawer.cs:411; Track.UI.Bound.cs:699; Track.UI.cs:760 | 18 |
| Reduced motion (`Design.Reduced`) | engine value | Artist.Page.cs:652,662,668; Artist.UI.cs:658,955; Detail.UI.cs:1899; Detail.UI.Hero.cs:386,788; Episode.Page.cs:343; Profile.Page.cs:166,518,528; Recents.Page.cs:294,330; Recents.UI.cs:67; Track.Drawer.cs:241; Track.Table.cs:2321; User.Cover.cs:694; User.Facts.UI.cs:337 | 19 |
| Width tiers / stacked (not a setting) | measured | Detail.Breakpoints (tiers 0-6, modes 0-3), ArtistHeroTier ×4, ProfileHeroMetrics.Stacked, Reader arms (<640), Library collapse (640/24), Chart tier, Search `ColsFor`, Browse `BrowseLayout`, `VerticalLayout.RowFlow` 424/400 | — |
| Nav style (Classic/Library/Zune) | — | **none on main**; PR205: 11 reads (§7) | 0 |

Structural note: the Classic/Modern fork is not one switch but a *value* (`ColumnSet.Classic`, `Snapshot.Classic`, chart/queue
`classic` bools) that the geometry caches, list keys and component keys are all forked on: `TracksFor` cache key (UI:115-150), list
key `:classic|:modern` (Table:1066), chart shelf keys ×8 (Chart:79-80), chart cell keys (:412), queue card keys (:665,:708), header
sort-label key (Chrome:597). A theme that owns the row look must therefore own those keys too.

---

## 5. Cross-cutting findings

**Caching keyed by style.** `Track.TracksFor` per (ColumnSet incl. Classic/Tier/lanes, art) never trimmed (Track.UI:115-150);
`TableHost.Shape` memo (Table:567) compares by value; the list Key folds density+skin+filter+reset (Table:1065); chart shelf/cell keys
fold art×classic (Chart:76-80,406-423); `NavMountCache` per `LibraryNavMount.Key` (User.Page.Library:1209); `RepeatLayout.Extents`
stateful per mount; `ShimmerKey` per decode size (Controls.cs:175); FormatCaches per call site (Track.Rules:446, Episode.UI:214,
User.UI:141).

**Virtualization.** `ItemsView.CreateBound` (table rows, library navigator, show reader, recents, profile lists, section screen);
`PagedShelf` (artist chart, home shelves, browse shelves, search rail); `LazyGrid` (discography); `VirtualLayout` (14 mentions,
reader spine/blocks); `RepeatLayout.GridFit` (section screen, profile lists); `Reorderable` (queue, customize). Queue is deliberately
non-virtual (Queue.UI:29). Per-file density: Artist.Discography 24, Concert.Page 20, Track.Table 14, Zones.UI 13, User.Page.Library 13,
Artist.Reader 13, Browse.Page 12.

**Image loading.** `Controls.ArtUrl(StringId)` (Controls.cs:92) → `Controls.Artwork` :207 / `CoverFill` :324 / `Shimmer` :149 over the
engine `ImageEl` (6 raw uses; `ImageState` reads in Artist.UI hero, Track.Drawer, Concert.UI); decode targets through
`Design.ImageDecodeScale` (Design.cs:1995; `ShelfDecodePx` 256 / `WideDecodePx` 512 Art:444-449); placeholders tinted from the
palette (`Design.WatchedPlaceholder/PlaceholderFor`); `keepWhileHidden` for the player bar; cover tokens (`CoverToken`, `Controls.Mosaic`).

**Colour sources.** 946 `Tok.*` reads (engine tokens, theme-flip safe); `Design.Colors.*`/`Design.Palette.*`/`Design.OnMedia.*`
(properties over Tok); cover-derived colour only through `Palette.Watch` leaves. Hard-coded colours in pages (all on-media or
deliberate): Artist.UI.cs:993-994,1054,1091,1273; Concert.UI.cs:134 (`DarkOr` #RRGGBB in dark theme), :251-254; Detail.Insights.cs:206-207
(scrim gradient); Playlist.UI.cs:101-103 (scrims, `OnScrim` white); Track.Drawer.cs:473 (play glyph #111111 on accent); User.Cover.cs
:331,371,376,379,528,772-773,869-871; Design.cs:238 (`PremiumText` #1DB954 dark); Profile.Rules.cs:218 (ARGB packing); fake-data
tone tables (Entities.Fake*.cs:89-94, Fake.Home.cs:66-67,826-827,873-904, Fake.Library.cs:53, Fake.Podcast.cs:48) — payload accents
that *are* data. Palette JSON decode Palette.Host.cs:216-246.

**Typography.** All through `Design.Type.*` (37 Eyebrow, 26 TrackMeta, 18 DenseMeta, 17 DenseTitle, 14 TrackTitle, 13 MicroMeta,
9 PageHero, 7 RailHeader, 7 ModuleHeader, 4 DetailHero, 3 SurfaceDisplay/StatHero/ArtistCompactTitle, 2 PickQuote/HeroTitle/
ArtistTitle/ArtistDisplay, 1 PivotLabel/FoldTitle/EyebrowTracking) plus `Ui.Caption` 37, `Ui.BodyStrong` 4, `Ui.Body` 4. Raw
`TextEl { Size = }` survives in the track row factual rungs (`RowFactualText` 12/16 vs 14/20 Classic, Track.UI:987-997, Bound:151-160),
the Classic header (`MicroMeta`), the episode row metrics (Episode.UI:25-32), Show.UI type :89, the artist hero `MinSize` floors.
Display face is a string constant (`Design.Type.DisplayFace` :1194).

**Icons.** `Icons.*` (engine Segoe Fluent glyph table): Play 21, ChevronRight 18, ChevronDown 15, Movie 12, More 11, Shuffle 10,
MapPin 10 … (≈330 uses); `Theme.IconFont` raw in 10 sites (Queue.UI ×6, Track.UI.Bound ×3, Track.UI ×1) — glyph-as-text spans.

**Localisation.** `Strings.*` source-generated from `assets/loc/en-US.json` (nl, ko-KR variants); 774 `Loc.Get(`; dominant groups
Detail 413, Podcast 161, Home 118, Concerts 91, Artist 65, Library 51, Menu 45, Person 44. ICU plurals via `ConcertCopy`. Known
literal English: Home/EpisodeCaption.cs:19-20 (`Duration`/`Remaining` "27 min", "… left"); no caps-transforms by policy (Design.cs:1216-1220,
Show.UI:24-25).

**Motion.** Every duration/easing from `Design.Motion/Reveal/Entrance/RevealRamp` or engine `MotionTok`/`Expressive`; reduced motion is
read as a VALUE (`Design.Reduced`) at 19 page sites rather than left to the engine (the engine also honours it for transitions:
Album.Pane:26). Production choreography that is deliberate: artist hero photo zoom/enter after copy (Artist.UI:610-685), masthead
stagger, zone entrance cascade, drawer reveal spring, pane crossfades.

**Theme polarity.** 15 `Tok.Theme` reads in pages; three pages (Artist, Detail frame, Profile) mirror it into a `_theme` signal to
re-derive accents and re-key tone planes (`:light|:dark`), i.e. a theme flip is already a first-class repaint path, but polarity is
the only "theme" dimension pages know.

**Where views own logic (the leaks).** `Detail.UI.RailCell` persists rail prefs from the view (:419-489); `Track.Table.Chrome` writes
density/tempo/plays prefs (:883,:889, Table:903); `Show.Page` persists view prefs (:264); `Playlist.Page` persists deposit recents
(:1005-1025); `HomeScreen` owns the facet switch effects; `Artist.Page`/`Profile.Page` latch geometry fields in the host (by design).
**Where logic builds visuals:** `Concert.Rules.EditorialArtGeometry` (pure geometry, fine); `Search.Rules.ShimmerShape`; `Detail.Skeleton`
bar geometry; `Artist.UI.cs §1-3` CORE sections inside a UI file; `Track.Rules.CommandBarLayout` fit math. None return `Element`s.

**Zune/Classic on main.** The only Zune-named presentation on main is the word rail (`Controls.Words`), used by the library and
podcast readers regardless of nav style. Nav-style variants (Classic/Library/Zune sidebar, `Deck`, `Rail.Styles.UI`, stage
options) live in `Shell/` (other agent).

---

## 6. Data layer, Core, Sdk, modules

**6.1 Entities (CORE).** No object per entity: `EntityId` (packed 24-byte, Entities.cs:166) → slot in a per-kind `Table` (:951) of
`Column<T>` slabs (:854); `Knows(fields)`/`Version`/`Known`/`Asked`/`Inflight`/`FetchedAt`/`Stale` marks (Fetch.cs:41-70);
`Publishable.Changed` signal per table fired once per drain (C3); `Scope` (table set per account/locale/market, :1538);
`Staging` (worker-filled, UI-drained, :1751); `Authority` ladder (:90) incl. `Seed`. Per-kind tables + handles: Track, Album
(+`AlbumKind/AlbumFlags`), Artist (+pick/prerelease sparse side tables), Playlist (+`PlaylistFacts`, notice column), Show, Episode,
User (library = edges), Home/Section, Browse, Search, Concert (+places/concepts/feed), Recents, Palette (process-wide). Relations:
`Edges.cs` CSR tables with `EdgeState` per parent and optimistic `EdgePending`; `FetchEdge` enum (Fetch.Routes.cs:63) names every
relation a page can `EnsureEdge` (Rootlist, Liked/SavedAlbums/FollowedArtists/SavedShows/Pins, Recents, Friends, PlaylistTracks,
AlbumTracks/Recommendations/Merch/MoreBy/Similar, ArtistPopular/Related/Releases/Albums/Singles/Compilations/Concerts, ShowEpisodes,
TrackCredits/Versions/Waveform/Beats, HomeSections/Previews, SearchResults, BrowseCategories/Sections, ProfileFollowers/Following).

**6.2 Read-side contract a view uses** (representative): `src.Subscribe()` + `src.At(i)` + `t.Knows(TrackFields.Face)`
(Track.Table.cs:618-633); `_ = Entities.Current.Tracks.Changed.Value` subscribes (Track.Table.cs:2193-2202); `Entities.Ensure(row,
Fields)` in an effect (Episode.Page.cs:53-56); `Entities.EnsureEdge(FetchEdge.X, parent)` (Recents.Page:12-15); readiness from
`EdgeState`/`IsFailed`/`Asked && Inflight==0` (Search.Page:28-31, Episode.Page:63); value stamps gating re-render
(`RowStamp/RowFold` Album.Page:61-80, `IdentityInputs/PageStamp` :176-184, `FaceStamp` Album.UI:88, `PageStamp` Playlist.Page:149,
`HeadStamp/HeaderStamp/FootStamp` Show.UI). Pages that read tables DIRECTLY in render: every `*.Page.cs`, `Detail.Identity.For`,
`Track.Table`, `Queue.UI`, `Recents`, `Search.UI`, `Browse.Page`; pages that go through a pure reader/planner: Home (`SectionReader`
→ planners), Show (`ReaderModel.Snap`), Recents (`RecentsSnapshot`), Library (`ComputeShape`), Concert (`ConcertShows.From` values),
Profile lists (`ProfileListModel`). View-model-like records: `ScreenModel/Zone/HomeCard`, `Detail.Identity/FrameSpec/VerticalSpec`,
`TableProfile/TableArgs/Snapshot/Shape/RowPresentation`, `CardData`, `Episode.RowItem/RowFacts/RowPlan`, `Queue.RowText`,
`Concert.DetailView/TileData`, `Search.HitItem`, `Recents.Facts`, `NavShape/LibraryNavMount`, `ReaderShapeKey/ReaderBlock`,
`ArtistHeroMetrics/HeroText/ArtistPageFacts`, `ProfileHeroMetrics/ProfileFacts/ProfileCard`.

**6.3 Fetch/Store.** `Fetch.cs` planner (`Plan/Continue/Refresh/Invalidate`, `Demand` buckets, four-in-flight, `CanSend` gate,
`Settled` signal :276), `Fetch.Edges.cs` (edge door, disk-first for lists), `Fetch.Routes.cs` (group → transport table), `Fetch.Miss.cs`
(retry budget), `Fetch.Baselines.cs` (/diff replay). `Store.cs` sqlite cache (`library.<fingerprint>.db`, write-behind, cold read,
intent journal, GC), `Store.Lists.cs` (playlist/rootlist membership), `Store.Files.cs` (reaper/health), `Store.Repair.cs`,
`Store.Palette.cs`. **Presentation leaks into the data layer:** only the palette (`Palette.cs` table + `Store.Palette`), the
provider's concert accent column, Home card payload accents (`SectionTable` card facts Home.cs:261), `Browse` category colour, and
`DetailNotice` as a column (Playlist.cs:172). No `Tok`/`Design` reads in Entities.cs/Fetch*/Store* (grep: none).
`FetchPriority.Visible` is a planner priority, not a viewport read.

**6.4 Fake data.** `Entities.Fake*.cs` seed through the same `Staging`+`Commit` at `Authority.Seed` (Entities.Fake.cs:14-19: "No
`if (fake)` branch belongs in Entities/, Shell/ or a page"). Presentation-ish data baked in: hero/card tone tables (Fake.cs:89-94
cover colours, user colours :94; Fake.Home.cs:66-67,826-827 page colours; Fake.Library.cs:53 daylist accent; Fake.Podcast.cs:48
serial tone) — all payload accents a live wire would also carry.

**6.5 Wavee.Core.** Empty on disk: only `bin/` and `obj/` leftovers, no `.csproj`, no sources. Not an extension point.

**6.6 Wavee.Sdk** (`src/apps/Wavee.Sdk`, "ZERO project references by design", csproj:3-6). A **playback-module** SDK: JSON-RPC 2.0
over stdio (`Protocol/*`), `WaveeModule` (Initialize/Match/Resolve/Warm/OpenStream/GetPage/GetDiagnostics/InvokeAction/Shutdown,
WaveeModule.cs:9-94), `IModuleHost` (DataDir, PublishMetadata/Expired/Status/Progress, Log, bearer tokens…), `ModuleManifest`
(wavee-module.json: id, version, protocol, entry, capabilities, url patterns, optional `Menu` with `LabelLocKey`), `ModuleStatus`,
`ModuleUri`, `ModulePage.cs` (the declarative page document above; budgets `ModulePageBudget`), `Playback.cs`, `Streams/*`
(chunk disk cache, ranged http). Discovered in `<app>\modules\<id>\` and `%LOCALAPPDATA%\Wavee\modules\<id>\<version>\`
(ModuleManifest.cs:7-8). **There is no theme/presentation contract anywhere in the Sdk**; the only app-rendered extension content is
`ModulePageDoc`, drawn by `Platform/Modules.UI.cs` with the app's own controls.

**6.7 modules/** — `Wavee.Module.Radio`, `.Twitch`, `.YouTube`: out-of-process playback modules (each `Program.cs` + `*Module.cs` +
`wavee-module.json`). No presentation code.

---

## 7. PR 205 (`feat/shell-cohesion`, worktree `C:\WAVEE\sidebar\WaveeMusic`) — page-level delta

`git diff --stat origin/main...HEAD -- src/apps/Wavee/Entities src/apps/Wavee/Home`: 49 files, +5,140/−1,889. Biggest:
Track.Table.Chrome.cs +1120, Detail.UI.Hero.cs +571, Detail.UI.cs +440, Artist.Page.cs +432, Track.Rules.cs +414, Detail.cs +384,
Track.Table.cs +345, Artist.Bleed.cs +344 (new), Artist.UI.cs +269, Facet.UI.cs +240, Recents.Page.cs +235, User.Page.Library.cs +200.

What it changes in pages (sources: the diff, commit log, worktree files):
1. **Nav style reaches pages.** `ShellNavStyle {Classic, Library, Zune}` (Shell/Sidebar.Layout.cs:21) and `Shell.Ui.PresentedNavStyle`
   (Shell/Shell.cs:1078, a lagged "presented" copy set in the quiet commit) are read in Entities/Home at 11 sites:
   Artist.Page.cs:143,473(`Sidebar.NavStyle` live),898; Detail.cs:1116 (`BandLayout.InRow2(presented) => presented == Zune`);
   Detail.UI.Hero.cs:132,229,663; Episode.Page.cs:298; Profile.Page.cs:95,574; Home/Facet.UI.cs & Browse via `PageHead.HoistedFor("home"|
   "browse")` (Platform/Page.UI.cs:91) which reads `PresentedNavStyle`. Plus `ArtistBleed.Applies(enabled, surfaces, headerUrl, zune,
   stacked)` (Entities/Artist.Bleed.cs:122-127) — "the full bleed is Zune-only" (owner decision, :523).
2. **New shared page-head module** `Platform/Page.Rules.cs` (`PageGeometry` gutters/head rhythm/hoisted strip/views bar/bottom reserve;
   `PageHeadKind`; `PageHeadRules`) and `Platform/Page.UI.cs` (`PageHeadSpec`, `PageHead.TitleRow/MetaSlot/Views/Publish/Reflow/
   FadeIn/FadeOut`). Used by 25 page files (Facet.UI 29, User.Page.Library 17, Detail.UI.Hero 15, Browse.Rules 11, Recents.Page 9,
   Browse.Page 9, HomeScreen 6…). Under Zune the Home facet words/Following, Browse "Top" categories and the artist/profile/episode/show
   context bands are PUBLISHED into the Zune band's row 2 and the in-page heads collapse to 0 (`Facet.BandHFor(hoisted)`,
   `BrowseMastheadMetrics.ShowsTopBand(hoisted)`, `BandLayout.StuckHeight(inRow2)`), animated by `PageHead.Reflow`.
3. **Liked Songs redesign** (commits a20702ed, 99d7f157, 23d6dd92, e8fb4a57): `Detail.Config` gains `SlimHead`, `HoverHeart`,
   `PlainRows` (Detail.cs:1670-1672; `Config.Liked` sets all three :1699); ONE layout (always the vertical arm, no rail:
   `VerticalLayout` :112), slim head = title (outside Zune) · meta · ONE bar (`SlimExpanded` Detail.UI.Hero.cs:433, `SlimHeadHeight(zune)`
   Detail.cs:630, `SlimHeroSkeleton` Hero:663); bar = `Controls.PlaySplitButton` (Controls.Cta.cs:612) · Shuffle · divider · neutral
   genre chips in-bar (`TableProfile.ContentFilterInBar`, `Controls.ChipRail(..., inBar: true)`) · sort · Insights; rows: no heart lane,
   hover/focus heart in the trailing lane (`TableRules.ForHoverHeart` Track.Rules.cs:389), plain rows (Track.Table.cs:2278), relative
   "Added" (`Format.RelativeAddedLabel` :649), quiet date groups (`LikedGroups` :694; group headers in the list, `GroupPlan` Table:853,
   analytic extents :946); `LikedBarLayout.Rung` ladder (Full → … → FilterInMore) for width pressure.
4. **Detail bars** (commits 6a50c8fc, 345443d8, 918de576): album/playlist identity block with one action row `[Play │ ⌄] · Shuffle ·
   ♥ · share · ⋯` (`FrameActions.HasPlaySplit` Detail.UI.cs:248 when AddToQueue+PlayNext+StartRadio are supplied; `ActionRowLinesFor`
   wrap arithmetic Detail.cs:337-359), the table's command bar keeps only list tools (sort, find, Insights, ⋯ with Select/Density/
   columns), and a "list bar" shows the list's facts on the left (`TableFacts` Chrome:958).
5. **Filter** (commits a6ba93fa, 7db10864, 7379bc1c): Filter is a primary command-bar action (`InlineCommand {None, Sort, Filter,
   FilterLabel}` Track.Rules.cs; `FilterCommandProps(Labelled)` Chrome:1174; `TableFilterButton(command:)` :1252), filter card reworked
   as combo rows; the in-list find stays open on the vertical arm (`TableSearchField(band:)` :693, `SearchProps.Funnel` :556).
6. **Chips** (5a3154fe, 23d6dd92): one chip everywhere — `Controls.Chip(label, selected, available, onClick, TemplateParts?)`
   (Controls.Art.cs:569), `Controls.LinkChip` :581 (Browse categories when hoisted, Browse.Rules:2629 in diff), `ChipRail(inBar)` :597;
   Liked chips look like Browse pills.
7. **Zune artist hero bleed** (`Entities/Artist.Bleed.cs`, commits 9d3942f1…a6aa8d70): the header photo runs from the window top
   behind the title bar and the Zune band; the page publishes it as DATA through `ShellMaterial`/`ShellBackdrop`; shared `PhotoFrame`
   so the card's photo/veil and the shell's are the same crop; one entrance clock; `FieldBase(theme, scheme, payloadAccent)` takes the
   page tone; scrim stops per theme (:62-71); `CopyOnMedia(bleed, Tok.Theme)` flips the hero copy light-on-dark only over the dark
   field; reveal waits for the hero bitmap ≤250 ms (d1af8eb2). Switch: `ArtistBleed.Enabled` const (:31) read once in Artist.Page.
8. **Appearance prefs renamed/added** (worktree Platform/Prefs.cs:82-100): `ColorWashes()` → `SurfaceWash()` returning `WashLevel
   {Off, Subtle, Rich}` (:352; legacy bool migrated by `Level(stored, legacyEnabled)` :371) — 10 page reads; new `AccentFromArtwork()`
   (4 page reads) and `NowPlayingColors()` (1). `TrackRowStyle` now also read in Detail.UI.cs:822 (`classicRows`).
9. Smaller: Episode row-2 placement (`_inRow2` Episode.Page:298), Profile floor latch (Profile.Page:95,574), Recents/Library/Search/
   Concert/Discography heads moved onto `PageHead`, album trailing skeleton mirrors loaded sections (b7076739), rail gap 24 (b1828c37),
   Play split reads as one control (f33984ae), suggestions flyout height reservation (16419a60, shell), search pill (8539d825, shell).

Implication for the refactor: PR 205 moves in the *opposite* direction from "no `if (theme == X)` in components" — it adds nav-style
reads to pages and a Zune-only bleed gate — but it also builds the two seams a theme system wants: `PageHead` (a page-head contract
whose geometry is a function of route kind + presented style) and `Detail.Config` knobs (`SlimHead/HoverHeart/PlainRows`) that
describe a *look* as data. The absorb path is to keep `PageHead`/`Config` knobs and re-home the `== Zune` decisions into the theme.

---

## 8. Verdict per page: "behaviour/data contract + replaceable view"

| Page / component | Score | Contract that already exists | What blocks a swappable view |
|---|---|---|---|
| Media surface | 5 | `CardData` + `SurfaceShape` | nothing; re-skin = `SurfacePlate` table + `SurfaceParts` |
| Home | 4.5 | `ScreenModel/Zone/HomeCard`, facet machine | zone bodies own geometry tables + sticky idiom; Daylist card composes the veil |
| Detail frame | 4 | `FrameSpec` (Identity/Config/Slots/Actions) | mode ladder + rail prefs inside the view; hero co-owned with the table; slot builders return page-built Elements |
| Album / Playlist / Show / Episode / Liked | 4 | FrameSpec + TableProfile + slot components | slot bodies are page-specific element trees (face pile, countdown, owner block, podcast slots) |
| Search / Browse / Profile lists / Concert | 4 | pure rules + static factories over handles | hand-built cell/hero trees in *.UI.cs |
| Episode row / Show reader | 4 | `RowItem/RowPlan`, `ReaderSnap` | row template is one bound tree |
| Track table | 3 | `TableSource/TableProfile/Config/Snapshot/Shape/RowPresentation` | 2.6k-line host fuses layout+behaviour; 230+ `classic` branches; chrome partial; style-forked caches/keys |
| Artist | 3.5 | `ArtistHeroMetrics/HeroText/ArtistPageFacts/ArtistSections` | page host composes hero/band/wash/shelves; chart keys fork on art×classic; reader is bespoke |
| Recents | 3.5 | `Shape`, `RecentsView` rules | bespoke masthead/list/calendar composition |
| Library | 3 | `NavShape/LibraryNavMount`, `LibraryNavOrder` | layout arms keyed in the host; three unrelated right columns |
| Queue | 2.5 | `QueueRowRules`, `RowText` | two whole skins hand-built in one file, keyed `classic=` |
| Liked cover treatments | 2 | `LikedCoverStyle/Site` ladder | nine hard-coded canvases + palette leaves |

The honest summary: **the data side is already a contract everywhere** (every page builds value records and gates re-render on
them; CORE files are engine-free and tested). **The view side is a set of concrete element builders with appearance *switches*
threaded through as booleans** (Classic/Modern, art hidden, density) and one global token table (`Design.cs` over `Tok`). A theme
layer would (a) own `Design.*`/`Tok` resolution, (b) own the row/skin/chrome builders behind `Shape`/`RowPresentation`/`ColumnSet`,
(c) own the frame's hero/rail/band builders behind `FrameSpec`, (d) own `Zones.UI`/`Daylist.Card`/`FacetRow` behind `ScreenModel`,
and (e) absorb PR 205's `PageHead` + `Config` knobs as the place where "head geometry is a function of route kind + style" already lives.
