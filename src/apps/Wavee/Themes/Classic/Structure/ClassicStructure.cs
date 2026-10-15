// ── Themes/Classic/Structure/ClassicStructure.cs — the Classic structure (themes plan 6.1) ─────────────────────────────
//
// Registers the views T-lane PRs have moved here, one family at a time (`Register`): T4a is the frame and the content states
// (`Views\Frame\ClassicFrame.cs`, `ClassicContentState.cs`), T4b the title bar and the search (`Views\TitleBar\ClassicTitleBar.cs`,
// `Views\Search\ClassicSearch.cs`), T4c the frame's small views (`Views\Masthead`, `Material`, `Drawer`, `Palette`, `FrameBits`),
// T5a the navigation pane's rows (`Views\Nav\ClassicNavPane.cs`, `ClassicNavRows.cs`, `ClassicNavRow.cs`), T5b its heads
// (`ClassicNavHead.cs`, `ClassicNavFilter.cs`, `ClassicNavLibraryHead.cs`), T6a the player bar (`Views\Player\ClassicPlayerBar.cs`),
// T6b the right panel and the player's flyouts (`Views\Panel`, `Views\Player`), T7a1 the fullscreen stage's frame and T7a2 its layout layers (`Views\Stage`),
// T7b the lyrics surface (`Views\Lyrics\ClassicLyrics.cs`) and its lines style (`Lists\ClassicLyricsLines.cs`, the `StyleKeys.LyricsLines` style),
// T8a the track table's rows (`Lists\ClassicTrackTable.cs`: the Modern and Classic bound-row templates, registered as the
// `StyleKeys.TrackTable` style; T8b its chrome, `ClassicTrackChrome.cs`), T8c the other list surfaces (`Lists\ClassicEagerRow.cs`,
// `ClassicEpisodeRow.cs`, `ClassicSurface.cs`, `ClassicArtistChart.cs`, `ClassicTrackDrawer.cs`, with the shared cells in `ClassicRowCells.cs`:
// the `StyleKeys.EagerTrackRow`, `EpisodeRow`, `Surface`, `ArtistChart` and `TrackDrawer` styles), T10a1 Home (`Views\Home`: the page, facet row, daylist and clock, zone bodies, the drill page and the
// customizer), T9a the shared detail frame (`Views\Detail`: the scaffold, the rail and its strip, the show header, the hero, the insights sheet), T9b its album and playlist sub-contracts (`Views\Detail\ClassicAlbum*.cs`, `ClassicPlaylist*.cs`, `ClassicDetailSeam.cs`), T10a2 Browse and Search (`Views\Pages`: the directory and its Charts band, a category page, the section drill key, the Search results page), T7d the queue (`Views\Queue`: the rail panel and the stage's Up next column), T7c the video surfaces (`Views\Video`: the docked card, the in-window overlay and its chrome, the pop-out, the fullscreen exit chrome, the poster and join notice, the placement flyout, the panel's video body), T10b1 the artist page (`Views\Pages\ClassicArtist*.cs`: the page, the hero, the sections, the banners, the pick card), T10b2 the rest of the artist family (the gallery lightbox `ClassicArtistLightbox.cs`, the discography page `ClassicDiscographyPage.cs`, the discography style `Lists\ClassicDiscography.cs` and the chart's frame), T10b3 the concert surfaces (`Views\Pages\ClassicConcert*.cs`: the hub with its filter bar, tail and location picker, an artist's tour dashboard, the event page) and the Common module page (`Themes\Common\CommonModulePage.cs`), T10c1 the library (`Views\Pages\ClassicLibrary*.cs`: the master-detail page and its album, show, artist and Your Episodes panes), T11a the overlays and the dialog bodies (`Views\Overlays`: the account chip and menu, the notification panel, the runtime banner, the update toast's card, the offline strip, the context menu's tile; `Views\Dialogs`: the log-out card, the play-a-link card, the signature sheet, the destination picker, the credits sheet), T11b the Settings page (`Views\Screens\ClassicSettings*.cs`: the page, its seven tabs, the theme picker, the options and the sidebar layout cards), T11c the screens (`Views\Screens`: the setup wizard, the release notes, the feedback and crash dialogs' bodies) and the Common blocks it registers (`Themes\Common`); every other surface is still core code. It
// decides the shell's policies (`Rules\ClassicPolicies.cs`: nav pane, panel and mini player, player bar, chrome row, stage)
// and offers every capability (T3). Its window tiers (T3b) are `Rules\Tiers.cs`.
//
// T1b: the options Classic will own (plan 6.1) that are still TODAY's switches are bound to their legacy keys through `SettingsDoc`
// rows. `LegacyRows` lists them in their FINAL grouping so Settings › Appearance shows Classic's section once, in its
// final shape; each M-PR deletes its rows from the list and declares the option in `Options` (the generated row takes the
// same place in the same group). M2 declared `rowStyle`, M4 `hideTrackArt`, M6 `Surfaces` (the colour washes; CT9 added Rich and the `AccentFromArtwork` / `NowPlayingColors` toggles), M7a `Sidebar` and `LibraryDensity` and M8 `PageLayout`, `RailUniform` and
// `ClearRememberedSizes`; M3's `density` is the style's.

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The levels of Classic's Surfaces option. The values are what is stored (appended members never re-mean a stored value).</summary>
public enum SurfaceWash
{
    /// <summary>No colour washes: the neutral ground.</summary>
    Off = 0,
    /// <summary>Today's washes.</summary>
    Subtle = 1,
    /// <summary>Stronger washes (the style's `R.Wash.Rich` strengths); the tone plane stays Subtle in light beside a docked pane (F2).</summary>
    Rich = 2,
}

/// <summary>The structure of Wavee Classic.</summary>
public sealed class ClassicStructure : IStructureLayer
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "Classic.Structure";

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        // T4a: the window frame and the content region's states (offline lane, empty body, not-found, page).
        views.Add(ViewKeys.ShellFrame, ClassicFrame.Template, "classic.shell.frame");
        views.Add(ViewKeys.ShellContentState, ClassicContentState.Template, "classic.shell.content-state");
        // T4b: the merged title bar row, the search (the field or the magnifier and its flyout) and the suggestions popup body.
        views.Add(ViewKeys.ShellTitleBar, ClassicTitleBar.Template, "classic.shell.title-bar");
        views.Add(ViewKeys.ShellSearch, ClassicSearch.Template, "classic.shell.search");
        views.Add(ViewKeys.ShellSearchSuggestions, ClassicSearch.SuggestionsTemplate, "classic.shell.search-suggestions");
        // T4c: the surfaces around the frame. The masthead and the page head, the material and its four cover leaves, the narrow drawer, the
        // command palette, and the small ones (history menu, file-drop cue, drag chip layer, setup wizard's cover scrim, frame-rate overlay).
        views.Add(ViewKeys.ShellMasthead, ClassicMasthead.Template, "classic.shell.masthead");
        views.Add(ViewKeys.PageHead, ClassicPageHead.Template, "classic.page.head");
        views.Add(ViewKeys.ShellMaterialLayer, ClassicMaterial.Template, "classic.shell.material");
        views.Add(ViewKeys.ShellCoverTone, ClassicMaterial.CoverTone, "classic.shell.material.page-tone");
        views.Add(ViewKeys.ShellCoverBlend, ClassicMaterial.CoverBlend, "classic.shell.material.artist-blend");
        views.Add(ViewKeys.ShellCoverVeil, ClassicMaterial.CoverVeil, "classic.shell.material.artist-veil");
        views.Add(ViewKeys.ShellCoverTint, ClassicMaterial.CoverTint, "classic.shell.material.tint-binder");
        views.Add(ViewKeys.ShellNarrowDrawer, ClassicDrawer.Template, "classic.shell.narrow-drawer");
        views.Add(ViewKeys.ShellCommandPalette, ClassicPalette.Template, "classic.shell.command-palette");
        views.Add(ViewKeys.ShellHistoryMenu, ClassicFrameBits.HistoryMenu, "classic.shell.history-menu");
        views.Add(ViewKeys.ShellFileDrop, ClassicFrameBits.FileDrop, "classic.shell.file-drop");
        views.Add(ViewKeys.ShellDragChip, ClassicFrameBits.DragChip, "classic.shell.drag-chip");
        views.Add(ViewKeys.ShellCoverScrim, ClassicFrameBits.CoverScrim, "classic.shell.cover-scrim");
        // T5a: the navigation pane (the rows: planner, diff, extents, freeze and the row slot; the rest of the pane is T5b-f's).
        views.Add(ViewKeys.ShellNavPane, ClassicNavPane.Template, "classic.shell.nav-pane");
        // T5b: the heads. Your Library's head and the empty body; the section header, the "+" and the filter box ride the row slot.
        views.Add(ViewKeys.ShellNavLibraryHead, ClassicNavLibraryHead.Template, "classic.shell.nav-library-head");
        views.Add(ViewKeys.ShellNavEmpty, ClassicNavEmpty.Template, "classic.shell.nav-empty");
        // T5c: the compact rail's flyout panels (a collapsed section's rows, a folder tile's drill-in); the pane opens and dismisses them.
        views.Add(ViewKeys.ShellNavSectionFlyout, ClassicNavFlyouts.SectionTemplate, "classic.shell.nav-section-flyout");
        views.Add(ViewKeys.ShellNavFolderFlyout, ClassicNavFlyouts.FolderTemplate, "classic.shell.nav-folder-flyout");
        // T5d: the Edit outline (the edit bar, the Show boxes, the reorder bands); the pane decides when it edits, the edit model keeps the ops.
        views.Add(ViewKeys.ShellNavEdit, ClassicNavEdit.Template, "classic.shell.nav-edit");
        // T6a: the player bar (the podcast transport rides with it).
        views.Add(ViewKeys.PlayerBar, ClassicPlayerBar.Template, "classic.player.bar");
        // Leftovers P3: the transport pieces the bar, the stage's transport card and the on-media strip share (`Views\Player\ClassicTransport.cs`).
        views.Add(ViewKeys.SeekRail, ClassicTransport.SeekRail, "classic.player.seek-rail");
        views.Add(ViewKeys.SeekChapters, ClassicTransport.SeekChapters, "classic.player.seek-chapters");
        views.Add(ViewKeys.ChapterTip, ClassicTransport.ChapterTip, "classic.player.chapter-tip");
        views.Add(ViewKeys.SeekReadout, ClassicTransport.SeekReadout, "classic.player.seek-readout");
        views.Add(ViewKeys.TimeLabel, ClassicTransport.TimeLabel, "classic.player.time-label");
        // T8a: the track table's rows (the Modern and Classic bound grids and skins; the header, chrome and placeholder are T8b's).
        views.AddStyle(StyleKeys.TrackTable, ClassicTrackTable.Style);
        // T8c: the other list surfaces: the eager track row (grid, title run, hover container), the podcast episode row (content, seed,
        // failure), the media surface (shell, bodies, labels), the artist's Top-tracks chart and the track drawer (frame, body, format split).
        views.AddStyle(StyleKeys.EagerTrackRow, ClassicEagerRow.Style);
        views.AddStyle(StyleKeys.EpisodeRow, ClassicEpisodeRow.Style);
        views.AddStyle(StyleKeys.Surface, ClassicSurface.Style);
        views.AddStyle(StyleKeys.ArtistChart, ClassicArtistChart.Style);
        views.AddStyle(StyleKeys.TrackDrawer, ClassicTrackDrawer.Style);
        // T6b: the right panel (frame, header, arms), its friends body, the now-playing hero, the deck's style flyout, the device
        // picker's menu and the on-media transport.
        views.Add(ViewKeys.ShellPanel, ClassicPanel.Template, "classic.shell.panel");
        views.Add(ViewKeys.PanelFriends, ClassicFriends.Template, "classic.panel.friends");
        views.Add(ViewKeys.NowPlayingHero, ClassicNowPlayingHero.Template, "classic.player.now-playing-hero");
        views.Add(ViewKeys.DeckStyleFlyout, ClassicDeckFlyout.Template, "classic.player.deck-style-flyout");
        views.Add(ViewKeys.DevicePicker, ClassicDevicePicker.Template, "classic.player.device-picker");
        views.Add(ViewKeys.OnMediaTransport, ClassicOnMedia.Template, "classic.player.on-media-transport");
        // P3: the panel's lyrics header (with the AI sparkle and the developer inspector), the "Next up" rows and the lyrics peek; their facts,
        // verbs, the rows' data and the peek's clock stay core (`RailWiring`, `AiLyrics.HeaderFacts`, `Rail.NextUpRows`, `Lyrics.PeekReel`).
        views.Add(ViewKeys.PanelLyricsHeader, ClassicLyricsHeader.Template, "classic.panel.lyrics-header");
        views.Add(ViewKeys.PanelNextUp, ClassicNextUp.Template, "classic.panel.next-up");
        views.Add(ViewKeys.LyricsPeek, ClassicLyricsPeek.Template, "classic.player.lyrics-peek");
        // T7a1: the fullscreen stage's frame: the root (layer stack, chrome placement, hairline), the top bar, the transport card, the
        // options popover, the visualizer gallery and the lyric caption. The layouts, hero and artist pane (T7a2) have keys (the lyrics pane
        // is T7b's and the queue pane T7d's, below), and so do the video surfaces (T7c, after the queue's).
        views.Add(ViewKeys.StageRoot, ClassicStageRoot.Template, "classic.stage.root");
        views.Add(ViewKeys.StageTopBar, ClassicStageTopBar.Template, "classic.stage.top-bar");
        views.Add(ViewKeys.StageTransport, ClassicStageTransport.Template, "classic.stage.transport");
        views.Add(ViewKeys.StageOptions, ClassicStageOptions.Template, "classic.stage.options");
        views.Add(ViewKeys.StageGallery, ClassicStageGallery.Template, "classic.stage.gallery");
        views.Add(ViewKeys.StageCaption, ClassicStageCaption.Template, "classic.stage.caption");
        views.Add(ViewKeys.StageBackdrop, ClassicStageBackdrop.Template, "classic.stage.backdrop");
        views.Add(ViewKeys.StageHero, ClassicStageHero.Template, "classic.stage.hero");
        views.Add(ViewKeys.StageArtistName, ClassicStageArtistName.Template, "classic.stage.artist-name");
        views.Add(ViewKeys.StageFace, ClassicStageFace.Template, "classic.stage.face");
        views.Add(ViewKeys.StageFaceScrim, ClassicStageFaceScrim.Template, "classic.stage.face-scrim");
        views.Add(ViewKeys.StageSpectrum, ClassicStageSpectrum.Template, "classic.stage.spectrum");
        views.Add(ViewKeys.StageNowPlayingCard, ClassicStageNowPlayingCard.Template, "classic.stage.now-playing-card");
        views.Add(ViewKeys.StageIdentity, ClassicStageIdentity.Template, "classic.stage.identity");
        views.Add(ViewKeys.StageArtistPane, ClassicStageArtistPane.Template, "classic.stage.artist-pane");
        // P3: the stage's pane region (its rect, move, clip and the frame each pane enters in); the KeepAlive and which pane is shown stay core (`Stage.PaneHost`).
        views.Add(ViewKeys.StagePane, ClassicStagePane.Template, "classic.stage.pane");
        // T7b: the lyrics reading surface (the layer stack, the overlays, the stage's reading column) and its lines style (the state line, the
        // loading bars, the unsynced block, the synced list and its rows, the podcast peek). The document, the clock, the follow, the cascade,
        // the depth-of-field ramp and the word wipe stay core (`Lyrics.ViewCore`).
        views.Add(ViewKeys.LyricsView, ClassicLyrics.Template, "classic.lyrics.view");
        views.AddStyle(StyleKeys.LyricsLines, ClassicLyricsLines.Style);
        // T7d: the queue. The rail panel (the pills, "Playing from", the now-playing card, the sections' headers, rows and "Show more", the
        // nothing-playing vacancy) and the stage's Up next column (the title, the Autoplay switch, the glass rows and the Next row). The model,
        // the verbs, the page counters, the realized slots and the reorder lane (drag, drop, keyboard) stay core (`Queue.RailPanelHost`,
        // `Queue.StagePaneHost`).
        views.Add(ViewKeys.QueuePanel, ClassicQueue.PanelTemplate, "classic.queue.panel");
        views.Add(ViewKeys.QueueStagePane, ClassicQueue.StagePaneTemplate, "classic.queue.stage-pane");
        // T7c: the video surfaces' pictures: the docked card (and the watch page's stage frame), the in-window overlay and its chrome, the pop-out's
        // content, the fullscreen exit chrome, the poster with the join notice and the placement flyout. The media pipeline stays core.
        views.Add(ViewKeys.VideoDocked, ClassicVideoDocked.Template, "classic.video.docked");
        views.Add(ViewKeys.VideoPip, ClassicVideoPip.Template, "classic.video.pip");
        views.Add(ViewKeys.VideoPopOut, ClassicVideoPopOut.Template, "classic.video.pop-out");
        views.Add(ViewKeys.VideoFullscreen, ClassicVideoFullscreen.Template, "classic.video.fullscreen");
        views.Add(ViewKeys.VideoJoinPoster, ClassicVideoPoster.Template, "classic.video.join-poster");
        views.Add(ViewKeys.VideoPlacementMenu, ClassicVideoPlacementMenu.Template, "classic.video.placement-menu");
        // T10a1: Home. The page (the scroll column, the skeleton region, the zone column and every chapter), the facet row, the daylist card
        // and its clock, the zone bodies (`Home.Zone.<Kind>`: the Recents grid and the Charts block have core hosts, the other fourteen kinds are
        // pure functions of their zone and share one template), the "See all" drill page and the customizer. The facet machine, the model, the
        // load verdict, the wash claim and every timer stay core.
        views.Add(ViewKeys.HomePage, ClassicHomePage.Template, "classic.home.page");
        views.Add(ViewKeys.HomeFacetRow, ClassicHomeFacetRow.Template, "classic.home.facet-row");
        views.Add(ViewKeys.HomeDaylist, ClassicHomeDaylist.Template, "classic.home.daylist");
        views.Add(ViewKeys.HomeDaylistClock, ClassicHomeDaylistClock.Template, "classic.home.daylist-clock");
        views.Add(ViewKeys.HomeZoneRecentGrid, ClassicHomeRecents.Template, "classic.home.zone.recent-grid");
        views.Add(ViewKeys.HomeCharts, ClassicHomeCharts.Template, "classic.home.charts");
        views.Add(ViewKeys.HomeZoneCoverShelf, ClassicHomeZones.BodyTemplate, "classic.home.zone.cover-shelf");
        views.Add(ViewKeys.HomeZoneWideTiles, ClassicHomeZones.BodyTemplate, "classic.home.zone.wide-tiles");
        views.Add(ViewKeys.HomeZoneReleaseList, ClassicHomeZones.BodyTemplate, "classic.home.zone.release-list");
        views.Add(ViewKeys.HomeZoneClusterCards, ClassicHomeZones.BodyTemplate, "classic.home.zone.cluster-cards");
        views.Add(ViewKeys.HomeZoneMixedCovers, ClassicHomeZones.BodyTemplate, "classic.home.zone.mixed-covers");
        views.Add(ViewKeys.HomeZoneRadioShelf, ClassicHomeZones.BodyTemplate, "classic.home.zone.radio-shelf");
        views.Add(ViewKeys.HomeZoneBrowseTiles, ClassicHomeZones.BodyTemplate, "classic.home.zone.browse-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeLead, ClassicHomeZones.BodyTemplate, "classic.home.zone.episode-lead");
        views.Add(ViewKeys.HomeZoneContinueEpisodes, ClassicHomeZones.BodyTemplate, "classic.home.zone.continue-episodes");
        views.Add(ViewKeys.HomeZoneShowGrid, ClassicHomeZones.BodyTemplate, "classic.home.zone.show-grid");
        views.Add(ViewKeys.HomeZoneVideoTiles, ClassicHomeZones.BodyTemplate, "classic.home.zone.video-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeRows, ClassicHomeZones.BodyTemplate, "classic.home.zone.episode-rows");
        views.Add(ViewKeys.HomeZonePodcastGroups, ClassicHomeZones.BodyTemplate, "classic.home.zone.podcast-groups");
        views.Add(ViewKeys.HomeZoneEmptyFacet, ClassicHomeZones.BodyTemplate, "classic.home.zone.empty-facet");
        views.Add(ViewKeys.HomeSection, ClassicHomeSection.Template, "classic.home.section");
        // T9a: the shared detail frame. The scaffold (notice, content column, rail | grip | content row, the vertical page), the rail and its collapsed
        // strip, the vertical show header, the vertical arm's hero (and its loading band, a style the table reads) and the insights sheet with its two
        // entry points. The mode ladder, the rail cells and prefs, the accent, the tint and tone claims, the table, the drop target and the insights
        // toggle stay core (`Detail.FrameHost`); the per-kind pages (T9b, T9c) hand the frame their slots.
        views.Add(ViewKeys.DetailFrame, ClassicDetailFrame.Template, "classic.detail.frame");
        views.Add(ViewKeys.DetailRail, ClassicDetailFrame.RailTemplate, "classic.detail.rail");
        views.Add(ViewKeys.DetailCompactRail, ClassicDetailFrame.CompactRailTemplate, "classic.detail.compact-rail");
        views.Add(ViewKeys.DetailShowHeader, ClassicDetailFrame.ShowHeaderTemplate, "classic.detail.show-header");
        views.Add(ViewKeys.DetailHero, ClassicDetailFrame.HeroTemplate, "classic.detail.hero");
        views.AddStyle(StyleKeys.DetailHero, ClassicDetailFrame.HeroStyle);
        views.Add(ViewKeys.DetailInsights, ClassicInsights.SheetTemplate, "classic.detail.insights");
        views.Add(ViewKeys.DetailInsightsToggle, ClassicInsights.ToggleTemplate, "classic.detail.insights-toggle");
        // T9c: the Liked, show and episode pages (and the reader and discussion) over that frame. Each family registers its own keys.
        ClassicLiked.Register(views);
        ClassicShow.Register(views);
        ClassicEpisode.Register(views);
        // T10a2: Browse and Search (`Views\Pages`). The directory (the clipped masthead column and the six bands), its Charts band (a core host over the chart
        // deck), a category page (shelves, tile blocks, the flattened grid, the empty and error arms), the section drill page (the Home drill template under
        // its own key) and the Search results page (the query echo, the facet row, the sliding facet body). The load verdicts, the layout and ranking rules, the
        // readiness of each facet, the demands and the hit adapters stay core.
        views.Add(ViewKeys.BrowseDirectory, ClassicBrowseDirectory.Template, "classic.browse.directory");
        views.Add(ViewKeys.BrowseCharts, ClassicBrowseDirectory.ChartsTemplate, "classic.browse.charts");
        views.Add(ViewKeys.BrowseCategory, ClassicBrowseCategory.Template, "classic.browse.category");
        views.Add(ViewKeys.BrowseSection, ClassicHomeSection.Template, "classic.browse.section");
        views.Add(ViewKeys.SearchPage, ClassicSearchPage.Template, "classic.search.page");
        // T10b1: the artist page (`Views\Pages\ClassicArtist*.cs`): the scroll column and its skeleton region, the hero and its collapse into the context band, the
        // magazine (the Top-tracks band, the shelves, the biography band, the upcoming / latest-release / tour banners) and the artist pick card. The demands, the
        // readiness, the section plan, the latched hero metrics, the accent, the scroll spy and the Play / Shuffle / Radio decisions stay core (`Artist.PageHost`).
        views.Add(ViewKeys.ArtistPage, ClassicArtistPage.Template, "classic.artist.page");
        // T10b3: the concert surfaces (`Views\Pages\ClassicConcert*.cs`): the Concerts hub (header card, pinned filter bar with its when / where flyouts and count ticker, the feed's
        // shelves, promos and virtualized grid) with the feed's append tail and the location picker's flyout, an artist's tour dashboard (split hero, tour-dates card, month board,
        // shy month pill) and the event page (split hero with the facts, tickets, lineup, related shelf). The data and its demands, the filter tuple and its feed subject, the
        // append gate, the schedule shaping and the detail facts stay core (`Concert.HubHost`, `ScheduleHost`, `DetailHost`, `TailHost`, `PlacePickerHost`). The module page is a
        // Common block (`Themes\Common\CommonModulePage.cs`), registered with the others by `CommonBlocks.Register`.
        views.Add(ViewKeys.ConcertsHub, ClassicConcertHub.Template, "classic.concerts.hub");
        views.Add(ViewKeys.ConcertTail, ClassicConcertHub.TailTemplate, "classic.concerts.tail");
        views.Add(ViewKeys.ConcertPlacePicker, ClassicConcertPicker.Template, "classic.concerts.place-picker");
        views.Add(ViewKeys.ArtistConcerts, ClassicConcertSchedule.Template, "classic.concerts.artist-schedule");
        views.Add(ViewKeys.ConcertDetail, ClassicConcertDetail.Template, "classic.concerts.detail");
        // T10b2: the rest of the artist family. The gallery lightbox (the pager, the pan/zoom mode, the auto-hiding chrome, the filmstrip; the overlay, the
        // open index, the Escape veto's wiring and the export stay core in `Artist.GalleryOpen`), the standalone discography page (the crumbs, the title, the
        // facet selector, the scroll column; the route, the demand and the scroll plumbing stay core in `Artist.DiscoPageHost`) and the discography style (the
        // facet section, the cards, the album drawer's frame, panel, slots and rows; the demands, the verdicts, the memos and the selection stay core).
        views.Add(ViewKeys.ArtistLightbox, ClassicArtistLightbox.Template, "classic.artist.lightbox");
        views.Add(ViewKeys.Discography, ClassicDiscographyPage.Template, "classic.discography.page");
        views.AddStyle(StyleKeys.Discography, ClassicDiscography.Style);
        // T10c1: the library page (the navigator, the grips, the detail pane, the search columns, the collapsed drill-in) and its four panes (album, show, artists' reader, Your Episodes).
        // The library data, the shape memo, the selection and its persistence, the search matcher, the collapse state and every pane model and demand stay core.
        ClassicLibrary.Register(views);
        // T11b: the Settings page (the masthead, the seven-tab pivot, the keyed scroll lane and every tab's sections and rows, drawn from `SettingsDoc`) and the three Settings
        // surfaces the setup wizard shares with it: the theme picker, the active theme's options and the sidebar layout cards. The document, the verbs, the facts, the page
        // epoch, the Storage census, the deep link and the legacy-row binding stay core.
        ClassicSettings.Register(views);
        // T10c2: Recents, the profile page and the Following / Followers list page (`Views\Pages`). The shapes, demands, accents, scroll spies, load rules and every verb stay core.
        views.Add(ViewKeys.RecentsPage, ClassicRecents.Template, "classic.recents.page");
        ClassicProfile.Register(views);
        ClassicProfileList.Register(views);
        // T9b: the album and playlist sub-contracts of the detail frame. The notice strip and the vertical arm's trailing body (the frame's two leaves only an album or a
        // playlist fills); the album's billed-artist line and flyout, countdown, release facts, trailing band (region, sections, stack) and unresolved line; the playlist's
        // cover face and editable cover, inline editor, owner block and collaborator flyout, access panel, Tune panel, daylist strip and recommendations. The models, the
        // demands, the page and trailing gates, the optimistic switch rule, the edit verbs and the permission decisions stay core.
        views.Add(ViewKeys.DetailNotice, ClassicDetailSeam.NoticeTemplate, "classic.detail.notice");
        views.Add(ViewKeys.DetailVerticalTrailing, ClassicDetailSeam.VerticalTrailingTemplate, "classic.detail.vertical-trailing");
        views.Add(ViewKeys.AlbumArtistLine, ClassicAlbumArtist.LineTemplate, "classic.album.artist-line");
        views.Add(ViewKeys.AlbumArtistFlyout, ClassicAlbumArtist.FlyoutTemplate, "classic.album.artist-flyout");
        views.Add(ViewKeys.AlbumCountdown, ClassicAlbumRelease.CountdownTemplate, "classic.album.countdown");
        views.Add(ViewKeys.AlbumReleasePanel, ClassicAlbumRelease.ReleasePanelTemplate, "classic.album.release-panel");
        views.Add(ViewKeys.AlbumUnresolved, ClassicAlbumRelease.UnresolvedTemplate, "classic.album.unresolved");
        views.Add(ViewKeys.AlbumTrailing, ClassicAlbumTrailing.TrailingTemplate, "classic.album.trailing");
        views.Add(ViewKeys.AlbumSections, ClassicAlbumTrailing.SectionsTemplate, "classic.album.sections");
        views.Add(ViewKeys.AlbumTrailStack, ClassicAlbumTrailing.StackTemplate, "classic.album.trail-stack");
        views.Add(ViewKeys.PlaylistCoverArt, ClassicPlaylist.CoverArtTemplate, "classic.playlist.cover-art");
        views.Add(ViewKeys.PlaylistCoverEditor, ClassicPlaylist.CoverEditorTemplate, "classic.playlist.cover-editor");
        views.Add(ViewKeys.PlaylistEditor, ClassicPlaylist.EditorTemplate, "classic.playlist.editor");
        views.Add(ViewKeys.PlaylistOwner, ClassicPlaylist.OwnerTemplate, "classic.playlist.owner");
        views.Add(ViewKeys.PlaylistMembersFlyout, ClassicPlaylist.MembersFlyoutTemplate, "classic.playlist.members-flyout");
        views.Add(ViewKeys.PlaylistAccess, ClassicPlaylist.AccessTemplate, "classic.playlist.access");
        views.Add(ViewKeys.PlaylistTune, ClassicPlaylist.TuneTemplate, "classic.playlist.tune");
        views.Add(ViewKeys.PlaylistDaylist, ClassicPlaylist.DaylistTemplate, "classic.playlist.daylist");
        views.Add(ViewKeys.PlaylistRecs, ClassicPlaylist.RecsTemplate, "classic.playlist.recs");
        views.Add(ViewKeys.PlaylistRecRow, ClassicPlaylist.RecRowTemplate, "classic.playlist.rec-row");
        // T11a: the overlays (the account chip and its menu, the notification panel, the runtime banner, the update toast's download card, the offline
        // strip, the context menu's identity tile) and the dialog bodies the app builds (the log-out card, the play-a-link card, the signature sheet, the
        // destination picker, the credits sheet). Which overlay is open, its dismiss rules and focus trap, the toast queue, the dialog shells and every
        // verb and decision stay core; the confirm, prompt and reset dialogs are engine ContentDialog configurations with no picture of the app's own.
        views.Add(ViewKeys.OverlayProfileChip, ClassicProfileChip.Template, "classic.overlay.profile-chip");
        views.Add(ViewKeys.OverlayProfileMenu, ClassicProfileMenu.Template, "classic.overlay.profile-menu");
        views.Add(ViewKeys.OverlayNotifications, ClassicNotifications.Template, "classic.overlay.notifications");
        views.Add(ViewKeys.OverlayRuntimeBanner, ClassicRuntimeBanner.Template, "classic.overlay.runtime-banner");
        views.Add(ViewKeys.OverlayToast, ClassicToast.Template, "classic.overlay.toast");
        views.Add(ViewKeys.OverlayOfflineStrip, ClassicOfflineStrip.Template, "classic.overlay.offline-strip");
        views.Add(ViewKeys.OverlayOfflineVacancy, ClassicOfflineVacancy.Template, "classic.overlay.offline-vacancy");   // tuned Classic X1
        views.Add(ViewKeys.MenuContext, ClassicContextMenuTile.Template, "classic.menu.context-tile");
        views.Add(ViewKeys.DialogLogoutConfirm, ClassicLogoutConfirm.Template, "classic.dialog.logout-confirm");
        views.Add(ViewKeys.DialogPlayLink, ClassicPlayLink.Template, "classic.dialog.play-link");
        views.Add(ViewKeys.DialogSignature, ClassicSignature.Template, "classic.dialog.signature");
        views.Add(ViewKeys.DialogFolderPicker, ClassicDestinationPicker.Template, "classic.dialog.folder-picker");
        views.Add(ViewKeys.DialogSheet, ClassicCreditsSheet.Template, "classic.dialog.sheet");

        // T11c: the Common blocks every theme shares (history, the Home customizer, the frame-rate overlay, diagnostics, logs: today's views
        // converted, one copy), then Classic's own screens: the setup wizard, the release notes, the feedback and crash dialogs' bodies.
        CommonBlocks.Register(views);
        ClassicSetup.Register(views);
        ClassicReleaseNotes.Register(views);
        views.Add(ViewKeys.DialogFeedback, ClassicFeedback.Template, "classic.dialog.feedback");
        views.Add(ViewKeys.DialogCrash, ClassicCrash.Template, "classic.dialog.crash");
    }

    /// <summary>Hide track artwork (M4): the track cells of every list drop their cover and give the space to the titles; page heroes, media cards, sidebar covers and the
    /// player's identity artwork stay. It replaces `appearance.trackArtwork.hidden` (`ThemeBootstrap` step "HideTrackArtwork"). The core reads it as
    /// `ThemePolicies.Lists` (`ClassicStructure.Policies`); the Classic views that draw the hidden look read it through <see cref="ClassicOptions.HideTrackArt"/>.</summary>
    public static ToggleOption HideTrackArt { get; } = new(
        Id: "hideTrackArt",
        TitleKey: Strings.Settings.Appearance.HideTrackArtwork,
        DescriptionKey: Strings.Settings.Appearance.HideTrackArtworkSub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists,
        Default: false);

    /// <summary>The layer-qualified id Settings and the palette know the option by (<c>structure.hideTrackArt</c>).</summary>
    public static string HideTrackArtQualifiedId => "structure." + HideTrackArt.Id;

    /// <summary>Row style (M2): Modern, the artwork-forward stacked rows, or Classic, the three-lane column table over hairlines (the 0.2.9 look). It replaces
    /// `detail.rowstyle` (`ThemeBootstrap` step "TrackRowStyle"; the same 0/1 numbers). It is a STRUCTURE option because it picks which of the structure's two row
    /// templates draws every track list (`TrackListStyle.Modern` / `.Classic`), the queue rows and the artist chart; the core reads it as
    /// `ThemePolicies.Lists.RowStyle` for the lane sets, the header height and the list key (live: the list re-keys exactly as it did), the Classic views through
    /// <see cref="ClassicOptions.RowStyle"/>. Drawn as two miniature rows (Cards).</summary>
    public static SelectOption<TrackRowStyle> RowStyle { get; } = new(
        Id: "rowStyle",
        TitleKey: Strings.Settings.Appearance.TrackListStyle,
        DescriptionKey: Strings.Settings.Appearance.TrackListStyleSub,
        Group: Strings.Settings.ThemeOptions.Group.TrackLists,
        Default: TrackRowStyle.Modern,
        Choices:
        [
            new SelectChoice<TrackRowStyle>(TrackRowStyle.Modern, Strings.Settings.Appearance.TrackListModern),
            new SelectChoice<TrackRowStyle>(TrackRowStyle.Classic, Strings.Settings.Appearance.TrackListClassic),
        ],
        Picker: PickerStyle.Cards);

    /// <summary>The layer-qualified id Settings and the palette know the option by (<c>structure.rowStyle</c>).</summary>
    public static string RowStyleQualifiedId => "structure." + RowStyle.Id;

    /// <summary>Surfaces: how much the artwork tints the shell and the pages (the colour washes: chrome tint, page ground, hero
    /// wash, photo veils). Off paints the neutral ground and a hue-less veil; Subtle is today's wash; Rich (CT9) takes the style's stronger
    /// pair for each wash. Stored as the enum's int (`theme.classic.structure.surfaces`); the option replaced the `appearance.colorWashes.enabled`
    /// switch (<c>ThemeBootstrap</c>'s "ColorWashes" step copies it). Drawn as three preview cards (`ClassicSettings.SurfacesCard`). Live: the
    /// wash fades exactly as the switch did. It is a STRUCTURE option because the structure's material views and cover leaves read it
    /// (WVTH005: an option is read only by its own layer; the style publishes both strengths and the structure picks one).</summary>
    public static SelectOption<SurfaceWash> Surfaces { get; } = new(
        Id: "surfaces", TitleKey: Strings.Settings.ThemeOptions.Surfaces, DescriptionKey: Strings.Settings.ThemeOptions.SurfacesSub,
        Group: Strings.Settings.ThemeOptions.Group.Surfaces, Default: SurfaceWash.Subtle,
        Choices:
        [
            new SelectChoice<SurfaceWash>(SurfaceWash.Off, Strings.Settings.ThemeOptions.SurfacesOff),
            new SelectChoice<SurfaceWash>(SurfaceWash.Subtle, Strings.Settings.ThemeOptions.SurfacesSubtle),
            new SelectChoice<SurfaceWash>(SurfaceWash.Rich, Strings.Settings.ThemeOptions.SurfacesRich),
        ],
        Picker: PickerStyle.Cards);

    /// <summary>The layer-qualified id of <see cref="Surfaces"/> (the Settings view draws its preview cards).</summary>
    public const string SurfacesQualifiedId = "structure.surfaces";

    /// <summary>Accent from artwork (CT9, U2): the detail pages' Play, Follow, hearts, accent rule and links take the cover's colour (floored at
    /// 4.5 : 1, D13); off, the system accent everywhere. Stored as `theme.classic.structure.accentFromArtwork`; new, so no legacy key. The core
    /// accent ladder follows it through <see cref="ArtworkColorPolicy.AccentFromArtwork"/> (see <see cref="Policies"/>); the structure's own
    /// accent reads (the search's top result) read the option.</summary>
    public static ToggleOption AccentFromArtwork { get; } = new(
        Id: "accentFromArtwork", TitleKey: Strings.Settings.ThemeOptions.AccentFromArtwork, DescriptionKey: Strings.Settings.ThemeOptions.AccentFromArtworkSub,
        Group: Strings.Settings.ThemeOptions.Group.Surfaces, Default: true);

    /// <summary>Now playing colours (CT9, U3): the fullscreen stage, its lyrics, the deck faces, the queue's now-playing chip and the now-playing
    /// cover placeholders take the playing cover's colour; off, a neutral stage and the system accent. Stored as
    /// `theme.classic.structure.nowPlayingColors`; new, so no legacy key. The core stage and deck follow it through
    /// <see cref="ArtworkColorPolicy.NowPlayingColors"/>; the structure's queue and panel read the option.</summary>
    public static ToggleOption NowPlayingColors { get; } = new(
        Id: "nowPlayingColors", TitleKey: Strings.Settings.ThemeOptions.NowPlayingColors, DescriptionKey: Strings.Settings.ThemeOptions.NowPlayingColorsSub,
        Group: Strings.Settings.ThemeOptions.Group.Surfaces, Default: true);

    /// <summary>The layer-qualified ids of the two artwork-colour toggles (the Settings view gives them their glyphs).</summary>
    public const string AccentFromArtworkQualifiedId = "structure.accentFromArtwork";

    /// <inheritdoc cref="AccentFromArtworkQualifiedId"/>
    public const string NowPlayingColorsQualifiedId = "structure.nowPlayingColors";

    /// <summary>Colour washes are drawn (anything but Off): what the material views and cover leaves read.</summary>
    public static bool WashesOn(SurfaceWash surfaces) => surfaces != SurfaceWash.Off;

    /// <summary>The washes take the style's Rich strengths (Home's washes, the chrome tint).</summary>
    public static bool RichWashes(SurfaceWash surfaces) => surfaces == SurfaceWash.Rich;

    /// <summary>The detail page's tone plane takes the Rich strength (tuned item F2): Rich, except in the LIGHT theme while the navigation pane is
    /// docked beside the content card, where the plane stays at Subtle so the card stays one step lighter than the tinted chrome next to it.</summary>
    public static bool RichTonePlane(SurfaceWash surfaces, bool light, bool paneDocked) => surfaces == SurfaceWash.Rich && !(light && paneDocked);

    /// <summary>Sidebar: which of the two layouts the navigation pane shows (Classic's sections you arrange, Library's one list with filters).
    /// Stored as the layout's int (`theme.classic.structure.sidebar`: Classic 0, Library 1); the option replaced `sidebar.layout.id` (`ThemeBootstrap`'s
    /// "SidebarLayout" step copies it). It is a STRUCTURE option: the layout is the navigation pane's shape, and another theme's sidebar is
    /// another structure. Drawn as the two preview cards (the Settings view of this structure draws them; the generic cards are text only), and offered
    /// to the command palette. The core sidebar follows it through <see cref="NavPanePolicy.Layout"/> (see <see cref="Policies"/>): it owns the layout
    /// document, the undo ring and the menus, the structure owns the choice.</summary>
    public static SelectOption<SidebarLayoutId> Sidebar { get; } = new SelectOption<SidebarLayoutId>(
        Id: NavPaneOptionIds.Layout, TitleKey: Strings.Settings.Sidebar.Layout, DescriptionKey: Strings.Settings.Sidebar.LayoutSub,
        Group: Strings.Settings.ThemeOptions.Group.Sidebar, Default: SidebarLayoutId.Classic,
        Choices:
        [
            new SelectChoice<SidebarLayoutId>(SidebarLayoutId.Classic, Strings.Sidebar.LayoutName.Classic),
            new SelectChoice<SidebarLayoutId>(SidebarLayoutId.Library, Strings.Sidebar.LayoutName.Library),
        ],
        Picker: PickerStyle.Cards) with { Command = true };

    /// <summary>Density: two-line (art 32) or one-line (art 24) entity rows, in both layouts. Stored as the int (`theme.classic.structure.libraryDensity`:
    /// Default 0, Compact 1); the option replaced `sidebar.pane.density` (the "SidebarDensity" step). Always shown: the density shapes the playlist and album
    /// rows of the Classic layout too, so hiding it while that layout is active would take a working setting away (the plan's "only with the Library layout"
    /// is not taken for that reason).</summary>
    public static SelectOption<SidebarDensity> LibraryDensity { get; } = new(
        Id: NavPaneOptionIds.Density, TitleKey: Strings.Settings.Sidebar.Density, DescriptionKey: Strings.Settings.Sidebar.DensitySub,
        Group: Strings.Settings.ThemeOptions.Group.Sidebar, Default: SidebarDensity.Default,
        Choices:
        [
            new SelectChoice<SidebarDensity>(SidebarDensity.Default, Strings.Sidebar.Density.Default),
            new SelectChoice<SidebarDensity>(SidebarDensity.Compact, Strings.Sidebar.Density.Compact),
        ],
        Picker: PickerStyle.Combo);

    /// <summary>Show covers (tuned item S0): the Classic layout's playlist, album and pin rows carry their cover (today's 40-px two-line or 36-px
    /// one-line rows, by <see cref="LibraryDensity"/>); off, they are 36-px text rows with a folder glyph on folders (board `C-Sidebar`, covers
    /// false). Stored as `theme.classic.structure.showCovers`; new, so no legacy key. Shown only with the Classic layout: the Library layout always
    /// shows covers. Live: the core sidebar follows it through <see cref="NavPanePolicy.ShowCovers"/> and re-plans the pane.</summary>
    public static ToggleOption ShowCovers { get; } = new ToggleOption(
        Id: "showCovers", TitleKey: Strings.Settings.Sidebar.ShowCovers, DescriptionKey: Strings.Settings.Sidebar.ShowCoversSub,
        Group: Strings.Settings.ThemeOptions.Group.Sidebar, Default: true)
        with { VisibleWhen = new OptionCondition(NavPaneOptionIds.Layout, SidebarLayoutId.Classic) };

    /// <summary>The layer-qualified id of <see cref="ShowCovers"/> (the Settings view gives it the picture glyph).</summary>
    public const string ShowCoversQualifiedId = "structure.showCovers";

    /// <summary>The layer-qualified ids of the two sidebar options (what <c>GeneratedOptionRow.QualifiedId</c> answers): the Settings view draws the layout
    /// with its preview cards and the density with its narrow combo.</summary>
    public const string SidebarOptionId = "structure." + NavPaneOptionIds.Layout;

    /// <inheritdoc cref="SidebarOptionId"/>
    public const string LibraryDensityOptionId = "structure." + NavPaneOptionIds.Density;

    /// <summary>Page layout (M8): how a TRACK page (album, playlist, Liked) is composed. Automatic keeps the metadata rail beside the list on wide windows and
    /// the artwork hero on narrow ones; Hero uses the hero composition at every width (no rail). Stored as the enum's int (`theme.classic.structure.pageLayout`:
    /// Automatic 0, Hero 1); the option replaced `detail.page.layout` (`ThemeBootstrap`'s "DetailPageLayout" step). Drawn as the two wireframe cards of the row it
    /// replaced (`ClassicSettings.Theme.cs`). The core frame follows it through <see cref="DetailPolicy.PageLayout"/> (see <see cref="Policies"/>).</summary>
    public static SelectOption<DetailPageLayout> PageLayout { get; } = new(
        Id: "pageLayout", TitleKey: Strings.Settings.Appearance.PageLayout, DescriptionKey: Strings.Settings.Appearance.PageLayoutSub,
        Group: Strings.Settings.ThemeOptions.Group.TrackPages, Default: DetailPageLayout.Automatic,
        Choices:
        [
            new SelectChoice<DetailPageLayout>(DetailPageLayout.Automatic, Strings.Settings.Choice.Automatic),
            new SelectChoice<DetailPageLayout>(DetailPageLayout.Hero, Strings.Settings.Choice.Hero),
        ],
        Picker: PickerStyle.Cards);

    /// <summary>"Keep left-rail same size" (M8): every kind's rail rests at one shared remembered size and resizing it once applies everywhere. It replaced
    /// `detail.rail.uniform` (the "DetailRailUniform" step). Shown only with the Automatic page layout: the Hero composes no rail to size. The core frame follows it
    /// through <see cref="DetailPolicy.RailUniform"/>.</summary>
    public static ToggleOption RailUniform { get; } = new ToggleOption(
        Id: "railUniform", TitleKey: Strings.Settings.Appearance.RailUniform, DescriptionKey: Strings.Settings.Appearance.RailUniformSub,
        Group: Strings.Settings.ThemeOptions.Group.TrackPages, Default: false)
        with { VisibleWhen = new OptionCondition(PageLayoutId, DetailPageLayout.Automatic) };

    /// <summary>"Clear all remembered sizes" (M8): the verb forgets every kind's remembered rail width and collapse (the shared uniform size is not touched), after a
    /// confirmation. It is composed AWAY, not disabled, while the page layout is Hero or the rails share one size (there is no per-kind rail to forget), and greys
    /// while nothing is customised. The behaviour is the core's (`DetailRailPrefs`, registered as the <see cref="DetailVerbIds.ClearRememberedSizes"/> theme verb); the remembered
    /// sizes are this theme's state (`rail.&lt;scope&gt;.width` / `.collapsed`).</summary>
    public static ActionOption ClearRememberedSizes { get; } = new ActionOption(
        Id: "clearRememberedSizes", TitleKey: Strings.Settings.Appearance.RailReset, Group: Strings.Settings.ThemeOptions.Group.TrackPages,
        VerbId: DetailVerbIds.ClearRememberedSizes, Caption: Strings.Settings.Appearance.RailResetSub)
        with
        {
            Command = false,   // a destructive action asks first, and the palette has no dialog
            Confirm = new SettingsConfirm(Strings.Settings.Appearance.RailResetConfirmTitle, Strings.Settings.Appearance.RailResetConfirmBody,
                Strings.Settings.Appearance.RailResetAction),
            VisibleWhen = new OptionCondition(PageLayoutId, DetailPageLayout.Automatic) { And = new OptionCondition(RailUniform.Id, false) },
        };

    private const string PageLayoutId = "pageLayout";

    /// <summary>The layer-qualified ids of the two rail options (the Settings view gives them the icons of the rows they replaced).</summary>
    public const string RailUniformQualifiedId = "structure.railUniform";

    /// <inheritdoc cref="RailUniformQualifiedId"/>
    public const string ClearRememberedSizesQualifiedId = "structure.clearRememberedSizes";

    /// <summary>The layer-qualified ids of the page options (what <c>GeneratedOptionRow.QualifiedId</c> answers): the Settings view draws the page layout with its
    /// wireframe cards.</summary>
    public const string PageLayoutQualifiedId = "structure." + PageLayoutId;

    private static readonly ThemeOptionSchema s_options = ThemeOptionSchema.Of(Sidebar, LibraryDensity, ShowCovers, RowStyle, HideTrackArt, Surfaces,
        AccentFromArtwork, NowPlayingColors, PageLayout, RailUniform, ClearRememberedSizes);

    /// <inheritdoc />
    public ThemeOptionSchema Options => s_options;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics)
        => ClassicPolicies.For(structure.Get(Sidebar.Key), structure.Get(LibraryDensity.Key), structure.Get(HideTrackArt.Key),
            structure.Get(PageLayout.Key), structure.Get(RailUniform.Key), structure.Get(RowStyle.Key), structure.Get(ShowCovers.Key),
            structure.Get(AccentFromArtwork.Key), structure.Get(NowPlayingColors.Key));

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>The legacy rows are the sidebar's three ACTIONS: they are not switches, so no M-PR migrates them. (Page motion, M1,
    /// was deleted: it has no row. Washes, M6, became the structure's `surfaces` option; row style, M2, the structure's `rowStyle`; row density, M3, the style's `density` option, hide art, M4,
    /// the structure's `hideTrackArt`; the sidebar layout and density, M7a, the structure's `sidebar` and `libraryDensity` (and the new `showCovers`, CT3a); the page layout, the rails' uniformity and
    /// their reset, M8, the structure's `pageLayout`, `railUniform` and `clearRememberedSizes`.)</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } =
    [
        new("sidebarReset", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarCustomize", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarResetEverything", Strings.Settings.ThemeOptions.Group.Sidebar),
    ];
}
