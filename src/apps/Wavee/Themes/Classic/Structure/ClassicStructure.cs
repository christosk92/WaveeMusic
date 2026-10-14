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
// customizer), T9a the shared detail frame (`Views\Detail`: the scaffold, the rail and its strip, the show header, the hero, the insights sheet), T9b its album and playlist sub-contracts (`Views\Detail\ClassicAlbum*.cs`, `ClassicPlaylist*.cs`, `ClassicDetailSeam.cs`), T10a2 Browse and Search (`Views\Pages`: the directory and its Charts band, a category page, the section drill key, the Search results page), T7d the queue (`Views\Queue`: the rail panel and the stage's Up next column), T10b1 the artist page (`Views\Pages\ClassicArtist*.cs`: the page, the hero, the sections, the banners, the pick card), T10c1 the library (`Views\Pages\ClassicLibrary*.cs`: the master-detail page and its album, show, artist and Your Episodes panes); every other surface is still core code. It
// decides the shell's policies (`Rules\ClassicPolicies.cs`: nav pane, panel and mini player, player bar, chrome row, stage)
// and offers every capability (T3). Its window tiers (T3b) are `Rules\Tiers.cs`.
//
// T1b: the options Classic will own (plan 6.1) are still TODAY's switches, bound to their legacy keys through `SettingsDoc`
// rows. `LegacyRows` lists them in their FINAL grouping so Settings › Appearance shows Classic's section once, in its
// final shape; each M-PR deletes its rows from the list and declares the option in `Options` (the generated row takes the
// same place in the same group).

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

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
        views.Add(ViewKeys.ShellFps, ClassicFrameBits.Fps, "classic.shell.fps");
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
        // T7a1: the fullscreen stage's frame: the root (layer stack, chrome placement, hairline), the top bar, the transport card, the
        // options popover, the visualizer gallery and the lyric caption. The layouts, hero and artist pane (T7a2) have keys; video
        // (T7c) arrives as a builder on the root until it does (the lyrics pane is T7b's and the queue pane T7d's, below).
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
        views.Add(ViewKeys.HomeCustomize, ClassicHomeCustomize.Template, "classic.home.customize");
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
        // T10c1: the library page (the navigator, the grips, the detail pane, the search columns, the collapsed drill-in) and its four panes (album, show, artists' reader, Your Episodes).
        // The library data, the shape memo, the selection and its persistence, the search matcher, the collapse state and every pane model and demand stay core.
        ClassicLibrary.Register(views);
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
    }

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ClassicPolicies.Bundle;

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>Each entry leaves with the M-PR that migrates its switch: sidebar M7a, row style M2, row density M3, hide art M4,
    /// washes M6, page layout and rails M8, page motion M1 (deleted).</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } =
    [
        new("sidebarDesign", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarDensity", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarReset", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarCustomize", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarResetEverything", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("trackListStyle", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("rowDensity", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("hideTrackArtwork", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("pageLayout", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railUniform", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railReset", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("colorWashes", Strings.Settings.ThemeOptions.Group.Surfaces),
        new("pageMotion", Strings.Settings.ThemeOptions.Group.Surfaces),
    ];
}
