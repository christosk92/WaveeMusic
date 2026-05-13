---
guide: track-and-episode-ui
scope: Every WaveeMusic UI surface that renders a track or podcast episode as a row, cell, or card.
last_verified: 2026-05-13
verified_by: read+grep over src/Wavee.UI.WinUI now-playing video paths (build blocked by sandboxed NuGet signature lookup)
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee Track And Episode UI Inventory

This is a focused guide for agents changing places where Wavee shows tracks or
podcast episodes. Use it to answer "where is this row rendered?", "which DTO
backs this surface?", and "what control do I edit if I want the change to land
everywhere?" without re-grepping the repo.

## How To Use This Guide

1. Skim the **Quick-find table** below to locate your surface.
2. Open the listed file at the listed line for the host element.
3. Read the **Shared Track Controls** section before touching any reusable
   control — most behavior is shared and a local override is usually wrong.
4. If you add or remove a track/episode surface, update this file (and bump
   `last_verified` once you've re-checked the others).

Useful re-verification commands:

```
rg -n ":TrackDataGrid\b|:TrackListView\b|:TrackItem\b|:ShowEpisodeRow\b|:ShowUpNextCard\b|:ShowResumeBanner\b|:EpisodeCard\b|:SearchResultRowCard\b|:SearchResultHeroCard\b" src/Wavee.UI.WinUI -g "*.xaml"
rg -n "ITrackItem\b" src/Wavee.UI.WinUI -g "*.cs"
rg -n "QueueDisplayItem|LocalTrackRowViewModel|EpisodeChapterVm|HomeSectionItem" src/Wavee.UI.WinUI
rg -n "TrackBehavior|TrackStateBehavior" src/Wavee.UI.WinUI
```

Scope:
- Included: reusable track/episode row controls, page-level track and episode
  lists, queue rows, search result rows, omnibar suggestions, home episode
  cards, local-file track rows, podcast chapter rows, and now-playing surfaces
  that show the current item.
- Not included as primary track surfaces: album, playlist, artist, show,
  concert, genre, browse, settings, diagnostics, and comment lists unless a row
  can directly represent a track or episode.

## Quick-find Table

| Surface | Host file:line | DTO | Source binding |
| --- | --- | --- | --- |
| Playlist tracks | `Views/PlaylistPage.xaml:526` | `PlaylistTrackDto` (`LazyTrackItem`) via `ITrackItem` | `PlaylistViewModel.FilteredTracks` |
| Album tracks | `Views/AlbumPage.xaml:358` | `AlbumTrackDto` (`LazyTrackItem`) via `ITrackItem` | `AlbumViewModel.FilteredTracks` |
| Liked Songs | `Views/LikedSongsView.xaml:60` | `LikedSongDto` via `ITrackItem` | `LikedSongsViewModel.FilteredSongs` |
| Liked Songs (hidden legacy) | `Views/LikedSongsView.xaml:119` | `LikedSongDto` | hidden `TrackListView`, kept for old wiring |
| Artist top tracks | `Views/ArtistPage.xaml:650` (compact `TrackItem` in `ItemsRepeater`) | `LazyTrackItem` | `ArtistViewModel.PagedTopTracks` |
| Artist pinned item (can be a track) | `Views/ArtistPage.xaml:522` (`PinnedTopTracksCard`) | `ArtistPinnedItemResult` | `ArtistViewModel.PinnedItem` |
| Artist expanded album panel | `Controls/AlbumDetailPanel/AlbumDetailPanel.xaml:98` | `AlbumTrackDto` | `AlbumDetailPanel.Tracks` (set imperatively, e.g. `ArtistViewModel.ExpandedAlbumTracks`) |
| Library/Artists drill-in (inline preview) | `Views/ArtistsLibraryView.xaml:598` (`TrackItem`) | `AlbumTrackDto` | `ArtistAlbumItemViewModel.Tracks` |
| Library/Artists drill-in (wide panel) | `Views/ArtistsLibraryView.xaml:745` (`TrackListView`) | `AlbumTrackDto` | `ArtistsLibraryViewModel.SelectedAlbumTracks` |
| Library/Artists drill-in (narrow stage) | `Views/ArtistsLibraryView.xaml:1170` (`TrackListView`) | `AlbumTrackDto` | `ArtistsLibraryViewModel.SelectedAlbumTracks` |
| Library/Albums detail (wide) | `Views/AlbumsLibraryView.xaml:263` (`TrackListView`) | `AlbumTrackDto` | `AlbumsLibraryViewModel.SelectedAlbumTracks` |
| Library/Albums detail (narrow) | `Views/AlbumsLibraryView.xaml:477` (`TrackListView`) | `AlbumTrackDto` | `AlbumsLibraryViewModel.SelectedAlbumTracks` |
| Profile top tracks | `Views/ProfilePage.xaml:353` (`TrackListView`) | `TopTrackAdapter` | `ProfileViewModel.TopTrackItems` |
| Local files | `Views/LocalLibraryPage.xaml:65` (album group) → `:94` (track row) | `LocalAlbumGroupViewModel` + `LocalTrackRowViewModel` (**not** `ITrackItem`) | `LocalLibraryViewModel.Albums[*].Tracks` |
| Queue (all buckets) | `Controls/Queue/QueueControl.xaml:17` (shared `TrackTemplate`) — hosted by `Controls/RightPanel/QueueTabView.xaml:20` | `QueueDisplayItem` (**not** `ITrackItem`) | `PlaybackStateService.RawNextQueue` + current track |
| Search hero (top result) | `Views/SearchPage.xaml:137` (`SearchResultHeroCard`) | `SearchResultItem` | `SearchViewModel.TopResult` |
| Search results list | `Views/SearchPage.xaml:193` (`SearchResultRowCard`) | `SearchResultItem` | `SearchViewModel.VisibleResults` |
| Search section shelves (incl. tracks) | `Views/SearchPage.xaml:171` (`ContentCard`) | section entity DTO | section data from `SearchViewModel` |
| Omnibar suggestions | `Controls/Omnibar/SearchFlyoutPanel.xaml:31` (`EntityTemplate`) | `SearchSuggestionItem` (`SearchSuggestionType.Track`) | `ShellViewModel.SearchSuggestions` |
| Your Episodes (grouped header grid) | `Views/YourEpisodesView.xaml:189` | `LibraryEpisodeDto` via `ITrackItem` | grouped `YourEpisodesViewModel.Episodes` |
| Your Episodes (wide grid) | `Views/YourEpisodesView.xaml:658` | `LibraryEpisodeDto` via `ITrackItem` | `YourEpisodesViewModel.VisibleEpisodes` |
| Your Episodes (narrow stage) | `Views/YourEpisodesView.xaml:847` | `LibraryEpisodeDto` via `ITrackItem` | `YourEpisodesViewModel.VisibleEpisodes` |
| Show: resume banner | `Views/ShowPage.xaml:479` (`ShowResumeBanner`) | `ShowEpisodeDto` | `ShowViewModel.ResumeEpisode` |
| Show: listen-next grid | `Views/ShowPage.xaml:513` (`ShowUpNextCard`) | `ShowEpisodeDto` | `ShowViewModel.UpNextEpisodes` |
| Show: archive list | `Views/ShowPage.xaml:562` (`ShowEpisodeRow`) | `ShowEpisodeDto` | `ShowViewModel.FilteredEpisodes` |
| Episode detail: more from show | `Views/EpisodePage.xaml:528` (`ShowEpisodeRow`) | `ShowEpisodeDto` | `EpisodePageViewModel.MoreFromShow` |
| Episode detail: chapters | `Views/EpisodePage.xaml:433` (`ItemsRepeater`, chapter buttons) | `EpisodeChapterVm` | `EpisodePageViewModel.Chapters` |
| Home/Recents (episode cards) | `Views/HomePage.xaml:105` (template) + `Controls/RecentlyPlayedSection.xaml:57` | `HomeSectionItem` with `ContentType == Episode` | `HomeViewModel` / `RecentlyPlayedService` |
| Home (recent liked songs hero card) | `Views/HomePage.xaml:93` (`LikedSongsRecentCard`) + `Controls/RecentlyPlayedSection.xaml:44` | DPs on the card (not `ITrackItem`) | `HomeViewModel` / `RecentlyPlayedService` |

## Core Contracts

`src/Wavee.UI.WinUI/Data/Contracts/ITrackItem.cs`
- Canonical row contract for Spotify tracks **and** saved episodes rendered by
  `TrackItem`, `TrackListView`, and `TrackDataGrid`.
- Important fields: `Id`, `Uri`, `Title`, `ArtistName`, `AlbumName`, image URLs,
  `Duration`, `OriginalIndex`, `IsLoaded`, `HasVideo`, `IsLiked`, `IsLocal`,
  `AddedAtFormatted`, `PlayCountFormatted`, `PlaybackProgress`, `Artists`.
- Episode support is intentional: `LibraryEpisodeDto` implements `ITrackItem`.

Current `ITrackItem` implementers (run `rg -n "ITrackItem\b" src/Wavee.UI.WinUI -g "*.cs"` to re-confirm):

- `Data/DTOs/AlbumTrackDto.cs`
- `Data/DTOs/LikedSongDto.cs`
- `Data/DTOs/PlaylistTrackDto.cs`
- `Data/DTOs/LibraryEpisodeDto.cs`
- `Data/DTOs/SearchTrackAdapter.cs`
- `Data/DTOs/TopTrackAdapter.cs`
- `Data/DTOs/NowPlayingTrackAdapter.cs`
- `ViewModels/LazyItemVm.cs` (`LazyTrackItem`, placeholder for virtualized rows)

DTOs that intentionally bypass `ITrackItem`:

- `Data/DTOs/ShowEpisodeDto.cs` — show page rows (archive/up-next/more-from-show).
- `Data/DTOs/PodcastEpisodeDetailDto.cs` — episode detail metadata, recommendations, comments, chapters.
- `ViewModels/EpisodeChapterVm` (in `ViewModels/EpisodePageViewModel.cs`) — chapter rows.
- `Controls/Queue/QueueControl.xaml.cs` `QueueDisplayItem` — queue rows (tracks and episodes).
- `ViewModels/LocalAlbumGroupViewModel` + `LocalTrackRowViewModel` — local files.

## Shared Track Controls

`src/Wavee.UI.WinUI/Controls/Track/TrackItem.xaml(.cs)`
- Canonical reusable track cell.
- `Mode="Compact"` (~56 px card-like row) — used by artist top tracks and
  some search-like surfaces.
- `Mode="Row"` — the table/list row used by `TrackListView`, `TrackDataGrid`,
  profile top tracks, library album details, and artist album previews.
- Owns hover play, pending playback beam, now-playing equalizer, buffering
  ring, heart button, context menu, artist/album navigation, explicit / video /
  local badges, progress display, date added, play count, added-by, and row
  selection.
- Wires `TrackBehavior` (input) and `TrackStateBehavior` (state visuals).
- Performance contract: full row rebinds should be reserved for `Track`
  replacement / `LazyTrackItem.Data` changes. Simple source updates such as
  liked state, video availability, playback progress, duration, title, and
  album/artist text are expected to update only their affected visuals. Artist
  link children are signature-guarded; don't remove that guard unless replacing
  it with an equivalent reuse strategy.

`src/Wavee.UI.WinUI/Controls/TrackDataGrid/TrackDataGrid.xaml(.cs)`
- Modern sortable / filterable / list-grid host. Rows are always `TrackItem`
  in row mode (templated at `TrackDataGrid.xaml:345` and `:385` for the
  card-row variant).
- Adds toolbar, filter box, sort/view/details controls, density, optional card
  rows, grouping, sticky-ish header sync, and selection commands.
- Page keys and column sets live in
  `Controls/TrackDataGrid/TrackDataGridDefaults.cs`:
  - `playlist`: index, like, art, title, album, added-by, date added, duration.
  - `album`: index, like, title, play count, duration.
  - `liked`: index, like, art, title, album, date added, duration.
  - `podcasts`: index, like, art, title, duration, plus inline progress.
- Column header chrome lives in
  `Controls/TrackDataGrid/TrackDataGridColumnHeader.xaml(.cs)`.
- `TrackDataGrid` snapshots and reprojects whenever its source collection
  raises `CollectionChanged`. Producers should batch large source replacements
  with `ObservableCollectionExtensions.ReplaceWith` instead of adding/removing
  rows one at a time.
- `LazyTrackItem` (`ViewModels/LazyItemVm.cs`) deliberately suppresses delegated
  property fan-out during `Populate`. Consumers that need to react to a shimmer
  row becoming real should listen for `Data` and/or `IsLoaded`, not every
  delegated `ITrackItem` property.

`src/Wavee.UI.WinUI/Controls/TrackList/TrackListView.xaml(.cs)`
- Older reusable list host. Rows are `TrackItem` in row mode (templated at
  `TrackListView.xaml:169`).
- Still used for embedded/detail panels and profile top tracks.
- Owns sticky column-header overlay, selection command bar, custom columns,
  drag payloads (`DragDrop/TrackDragPayload.cs`), compact mode, and a fallback
  `TrackClicked` when no view model is supplied.
- Do not assume `TrackListView` is dead: it is hidden in `LikedSongsView`
  (line 119) but visible in multiple library/detail surfaces (see table).

`src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackBehavior.cs`
- Attached behavior for **input**: tap / double-tap to play (configurable),
  right-click / hold for context menu, first-time play action dialog.
- Set via `TrackBehavior.Track="{x:Bind}"` on a row root element.
- Consumers also include `SearchResultRowCard` and `SearchResultHeroCard`.

`src/Wavee.UI.WinUI/Controls/Track/Behaviors/TrackStateBehavior.cs`
- Attached behavior for **playback-state visuals**: single source of truth
  for `IsPlaying` / `IsPaused` / `IsHovered` across every track display.
- Subscribes to `IPlaybackStateService` and maintains a weak registry of
  elements via `TrackStateBehavior.TrackId`.
- Used by `TrackItem`, `SearchResultRowCard`, and `SearchResultHeroCard`.

## Track List Surfaces

`src/Wavee.UI.WinUI/Views/PlaylistPage.xaml:526`
- Surface: playlist track table.
- Host: `TrackDataGrid`, `PageKey="playlist"`, `UseItemsViewRows="True"`.
- Source: `PlaylistViewModel.FilteredTracks` (`PlaylistTrackDto` and
  `LazyTrackItem` placeholders, both via `ITrackItem`).
- Notes: added-by column is controlled by `ShouldShowAddedByColumn`;
  filter bar (`:536`) includes "music videos only".
- Load behavior: `PlaylistViewModel.Activate` clears stale rows and drives
  `TrackDataGrid.IsLoading` so the grid renders lightweight skeleton rows.
  `LoadTracksAsync` applies the real `FilteredTracks` snapshot with one
  collection reset. Keep that batched shape; adding rows one at a time causes
  `TrackDataGrid` to re-snapshot/reproject once per add.
- Added-by display names / avatars are cached in `PlaylistViewModel` and pulled
  by `PlaylistPage`'s `TrackGrid.AddedByFormatter`; avoid mutating every
  `PlaylistTrackDto` just to refresh resolved user labels.
- Fallback playlist mosaics should use the track snapshot already fetched by
  `LoadTracksAsync` when no `spotify:mosaic:` hint is available.

`src/Wavee.UI.WinUI/Views/AlbumPage.xaml:358`
- Surface: album track table.
- Host: `TrackDataGrid`, `PageKey="album"`, toolbar hidden.
- Source: `AlbumViewModel.FilteredTracks` (`LazyTrackItem` over `AlbumTrackDto`).
- Notes: `ForceShowArtistColumn` is true for multi-artist albums; footer
  (`:366`) hosts related album / merch shelves but those are not track rows.
- Load behavior: `AlbumViewModel.Initialize` clears old rows and drives
  `TrackDataGrid.IsLoading` from `IsLoadingTracks`, so the album grid uses the
  shared lightweight skeleton instead of `LazyTrackItem` placeholder rows.

`src/Wavee.UI.WinUI/Views/LikedSongsView.xaml:60`
- Surface: liked songs table.
- Host: visible `TrackDataGrid`, `PageKey="liked"`.
- Source: `LikedSongsViewModel.FilteredSongs` (`LikedSongDto`).
- Notes: toolbar has Play/Shuffle/stats and video-only filter. A collapsed
  legacy `TrackListView` (`:119`) remains for old formatter wiring; it is not
  visible UI but is still wired.

`src/Wavee.UI.WinUI/Views/ArtistPage.xaml:650`
- Surface: artist top tracks carousel/grid.
- Host: `ItemsRepeater` plus `TrackItem` (compact mode).
- Source: `ArtistViewModel.PagedTopTracks` (`LazyTrackItem`).
- Notes: selection and paging are in `ArtistPage.xaml.cs`.

`src/Wavee.UI.WinUI/Views/ArtistPage.xaml:522`
- Surface: pinned artist item card to the left of top tracks.
- Host: custom `Border`/`Grid` named `PinnedTopTracksCard`.
- Source: `ArtistViewModel.PinnedItem` (`ArtistPinnedItemResult`).
- Notes: not a list row, but **can represent a track** —
  `ArtistPage.xaml.cs:1903 PinnedItem_Click` plays the item in artist context
  when `PinnedItem.Type == "TRACK"`.

`src/Wavee.UI.WinUI/Controls/AlbumDetailPanel/AlbumDetailPanel.xaml:98`
- Surface: expanded album track list inside artist/release detail overlays.
- Host: `TrackListView` named `TrackListControl`.
- Source: set imperatively from `AlbumDetailPanel.xaml.cs` (and from
  `ArtistPage.xaml.cs` via `Tracks = ViewModel.ExpandedAlbumTracks`).
- Notes: compact, no headers, no art/artist/album/date columns, max height 380.

`src/Wavee.UI.WinUI/Views/ArtistsLibraryView.xaml`
- Surface: saved artists drill-in with album tracks. Three hosts:
  - Inline album preview at `:598` — `ItemsRepeater` plus `TrackItem` row mode.
  - Wide tracks panel at `:745` — `TrackListView`.
  - Narrow tracks stage at `:1170` — `TrackListView`.
- Sources:
  - Inline preview: `ArtistAlbumItemViewModel.Tracks`.
  - Panel/stage: `ArtistsLibraryViewModel.SelectedAlbumTracks`.
- DTO: `AlbumTrackDto`.
- Notes: inline previews hide art/artist/album/date and use compact row density.

`src/Wavee.UI.WinUI/Views/AlbumsLibraryView.xaml`
- Surface: saved albums detail track lists. Two hosts:
  - Wide `LibraryGridView.DetailContent` at `:263` — `TrackListView`.
  - Narrow details stage at `:477` — `TrackListView`.
- Source: `AlbumsLibraryViewModel.SelectedAlbumTracks` (`AlbumTrackDto`).
- Notes: compact, headers hidden, art/artist/album/date hidden.

`src/Wavee.UI.WinUI/Views/ProfilePage.xaml:353`
- Surface: "Top tracks this month".
- Host: `TrackListView`.
- Source: `ProfileViewModel.TopTrackItems` (`TopTrackAdapter`).
- Notes: headers hidden, art and album shown, artist/date hidden.

`src/Wavee.UI.WinUI/Views/LocalLibraryPage.xaml:65` (album group) → `:94` (track row)
- Surface: local-files album groups with nested local track rows.
- Host: outer `ItemsRepeater` over albums and inner `ItemsControl` over tracks.
- Source: `LocalLibraryViewModel.Albums[*].Tracks`.
- DTOs: `LocalAlbumGroupViewModel` and `LocalTrackRowViewModel`.
- Notes: this surface deliberately does **not** use `ITrackItem`, `TrackItem`,
  `TrackDataGrid`, or `TrackListView`. Click handlers live in
  `LocalLibraryPage.xaml.cs`.

`src/Wavee.UI.WinUI/Controls/Queue/QueueControl.xaml:17`
- Surface: right-panel queue tab.
- Host: `QueueControl` defines a single shared `TrackTemplate` keyed at line 17
  and consumed by every bucket `ItemsRepeater` in the same file (now playing,
  user queue, next up, queued later, autoplay). `QueueTabView.xaml:20` is a
  thin animator that hosts `QueueControl`.
- DTO: `QueueDisplayItem` (in `QueueControl.xaml.cs`) — not `ITrackItem`.
- Source: `PlaybackStateService.RawNextQueue` + current playback metadata.
- Notes: queue items can represent tracks or episodes depending on the
  playback queue metadata.

## Search Track Surfaces

`src/Wavee.UI.WinUI/Views/SearchPage.xaml`
- Surface: full search page result list and top result.
- Hosts:
  - `:137` `SearchResultHeroCard` for `ViewModel.TopResult`.
  - `:193` `SearchResultRowCard` inside `ItemsRepeater` for `ViewModel.VisibleResults`.
  - `:171` `ContentCard` inside section shelves for mixed section entities,
    including section-provided video-track entities.
- Data: `Wavee.Core.Http.Pathfinder.SearchResultItem`.
- Notes: row/hero cards own track now-playing/buffering visuals through
  `TrackStateBehavior` and play input through `TrackBehavior`. Dedicated
  songs data also exists as `SearchViewModel.Tracks` and `AdaptedTracks`, but
  the visible list currently renders `VisibleResults`.

`src/Wavee.UI.WinUI/Controls/Search/SearchResultRowCard.xaml(.cs)`
`src/Wavee.UI.WinUI/Controls/Search/SearchResultHeroCard.xaml(.cs)`
- Mixed-entity cards that are track-aware (not exclusively tracks).
- Use `Controls/Search/SearchSubtitleBuilder.cs` to assemble subtitles.

`src/Wavee.UI.WinUI/Controls/Omnibar/SearchFlyoutPanel.xaml:31`
- Surface: omnibar search suggestions.
- Host: `ListView` with `EntityTemplate` selected by `SearchSuggestionTemplateSelector`.
- Source: `ShellViewModel.SearchSuggestions`.
- DTO: `SearchSuggestionItem`.
- Notes: supports `SearchSuggestionType.Track`. There is no separate episode
  suggestion type today. Track action button is "Add to queue".

## Episode List Surfaces

`src/Wavee.UI.WinUI/Views/YourEpisodesView.xaml`
- Surface: saved/recent podcast episode library.
- Hosts: three `TrackDataGrid` instances with `PageKey="podcasts"`:
  - `:189` grouped header template.
  - `:658` wide visible episode grid.
  - `:847` narrow episode stage.
- DTO: `LibraryEpisodeDto` through `ITrackItem`.
- Source: `YourEpisodesViewModel.VisibleEpisodes` and grouped `Episodes`.
- Notes: grouped mode uses `PodcastEpisodeHeaderTemplate` plus group selector
  delegates from the view model.

`src/Wavee.UI.WinUI/Views/ShowPage.xaml`
- Surface: show detail episodes. Three hosts:
  - `:479` `ShowResumeBanner` for `ViewModel.ResumeEpisode`.
  - `:513` `ShowUpNextCard` (in listen-next grid) for `ViewModel.UpNextEpisodes`.
  - `:562` `ShowEpisodeRow` (in archive list) for `ViewModel.FilteredEpisodes`.
- DTO: `ShowEpisodeDto`.
- Notes: recommended shows below this are show cards, not episode rows.

`src/Wavee.UI.WinUI/Views/EpisodePage.xaml:528`
- Surface: "More from this show" list on episode detail.
- Host: `ItemsRepeater` plus `ShowEpisodeRow`.
- Source: `EpisodePageViewModel.MoreFromShow` (`ShowEpisodeDto`).

`src/Wavee.UI.WinUI/Views/EpisodePage.xaml:433`
- Surface: episode chapter rail on episode detail.
- Host: `ItemsRepeater` with `EpisodeChapterVm` template; rows are `Button`s
  hooked to `ChapterButton_Click` (`EpisodePage.xaml.cs:322`) that seek the
  player to chapter start.
- Inline rail control: `Controls/RightPanel/PodcastChapterTimelineRail.xaml`
  (also used in `RightPanelView` chapter view).
- Notes: chapters are episode sub-items, not full episode rows; visuals live
  next to the rail control rather than in the row controls.

`src/Wavee.UI.WinUI/Views/HomePage.xaml:105` and
`src/Wavee.UI.WinUI/Controls/RecentlyPlayedSection.xaml:57`
- Surface: home/recent shelves that may contain podcast episodes.
- Host: `EpisodeCard` selected by `HomeItemTemplateSelector` when
  `HomeSectionItem.ContentType == Episode`.
- Source: `HomeViewModel` and `RecentlyPlayedService`.
- DTO: `HomeSectionItem`.
- Notes: home has no `HomeContentType.Track`; tracks usually appear as
  playlists, albums, videos, or search entities rather than home track cards.

`src/Wavee.UI.WinUI/Controls/Cards/EpisodeCard.xaml(.cs)`
- Surface: square episode shelf card (cover, title, publisher, video chip,
  play chip, progress, played-state).
- Data: `HomeSectionItem`.

`src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowEpisodeRow.xaml`
- Surface: full-width episode row (cover, episode number, title,
  explicit/video/status chips, description, hover actions, metadata,
  progress underline).
- Data: `ShowEpisodeDto`.

`src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowUpNextCard.xaml`
- Surface: compact up-next episode card used by `ShowPage` listen-next grid.
- Data: `ShowEpisodeDto`.

`src/Wavee.UI.WinUI/Controls/ShowEpisode/ShowResumeBanner.xaml`
- Surface: large palette-tinted continue-listening episode banner used by `ShowPage`.
- Data: `ShowEpisodeDto`.

`src/Wavee.UI.WinUI/Controls/ShowEpisode/PodcastEpisodeRecommendationCard.xaml`
- Surface: reusable podcast episode recommendation row.
- Data: `PodcastEpisodeRecommendationDto`.
- Current status: no XAML call site exists at the `last_verified` date
  (`rg -n "PodcastEpisodeRecommendationCard"` returns only the control's own
  files). Keep it in mind before deleting — it may be wired imperatively or
  added back to a view soon. Re-check before any cleanup.

## Adjacent Card And Shelf Surfaces

These are not row controls and do not host lists of tracks/episodes, but they
visually represent track/episode identity and tend to need coordinated changes
when row controls or state visuals move.

`src/Wavee.UI.WinUI/Controls/Cards/ContentCard.xaml(.cs)`
- Generic content card (square or circular). Used by section shelves on
  HomePage, SearchPage, BrowsePage, AlbumPage, ShowPage, ProfilePage,
  AlbumsLibraryView, ConcertPage. Can render a track entity when the section
  data provides one.

`src/Wavee.UI.WinUI/Controls/Cards/LikedSongsRecentCard.xaml(.cs)`
- Hero card on home showing recent additions to Liked Songs (`HomePage.xaml:93`,
  `RecentlyPlayedSection.xaml:44`). Backed by dependency properties on the
  card, not `ITrackItem`.

`src/Wavee.UI.WinUI/Controls/RightPanel/PodcastChapterTimelineRail.xaml(.cs)`
- Chapter rail visual used by `EpisodePage` chapter list and the podcast
  chapter view in `RightPanelView`.

## Now-Playing And Detail Surfaces

These are not lists, but they show current track / episode identity and often
need to change alongside row/cell behavior.

`src/Wavee.UI.WinUI/Controls/PlayerBar/PlayerBar.xaml`
- Current item metadata in wide and narrow player bars.
- Source: `PlayerBarViewModel`.
- Shows title, artists, artwork, current item episode controls, video/audio
  toggles, chapter progress segments, like/menu controls.

`src/Wavee.UI.WinUI/Controls/SidebarPlayer/SidebarPlayerWidget.xaml`
`src/Wavee.UI.WinUI/Controls/SidebarPlayer/ExpandedNowPlayingLayout.xaml`
- Current item metadata in sidebar player and expanded now-playing layout.
- Source: `PlayerBarViewModel`.
- Shows title, artists, artwork, current item episode controls, video/audio
  toggles, and queue/menu affordances.

`src/Wavee.UI.WinUI/Controls/MiniVideoPlayer/MiniVideoPlayer.xaml`
- Current video title in the detachable mini video chrome.
- Source: `MiniVideoPlayerViewModel.Title`, derived from current track title.

`src/Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.xaml`
- Track details tab: selected `ITrackItem` from
  `ShellViewModel.SelectedTrackForDetails`.
- Details tab: current artist/album/podcast metadata, podcast chapter list,
  credits, concerts, and related videos.
- Notes: the `TrackDataGrid` details button opens the temporary
  track-details tab.

`src/Wavee.UI.WinUI/Windows/PlayerFloatingWindow.xaml.cs`
- Floating player window shell around the sidebar/expanded player views.
- Source: `PlayerBarViewModel`.

## Change Guidance

When changing track rows:
- Start in `TrackItem` if the behavior/visual should apply to playlists,
  albums, liked songs, profile top tracks, artist top tracks, and library
  album panels.
- Update `TrackDataGrid` if the change is about toolbar, filtering, sorting,
  grouping, density, column visibility, column widths, or selected-row
  details. Touch `TrackDataGridDefaults.cs` for column sets.
- Update `TrackListView` if the change affects embedded library/detail
  lists, sticky headers, drag payloads, or old selection command bar
  behavior.
- Update `TrackBehavior` for input semantics (click/tap/context menu/first-time
  play dialog), and `TrackStateBehavior` for now-playing/hover/playing visuals.
- Check custom surfaces separately — local files, queue, search cards,
  omnibar suggestions, and show episode controls do not all flow through
  `TrackItem`.
- For load/performance work, preserve these shared invariants: batch collection
  source changes, coalesce lazy-row population, keep `TrackItem` property
  updates incremental, and avoid rebuilding per-artist link controls when the
  artist signature did not change.

When changing episode rows:
- Saved/recent episodes in `YourEpisodesView` go through `LibraryEpisodeDto`
  plus `TrackDataGrid`/`TrackItem`.
- Show page and episode detail rows go through `ShowEpisodeDto` plus the
  dedicated `ShowEpisode*` controls.
- Home/recent shelves go through `HomeSectionItem` plus `EpisodeCard`.
- Chapters are not full episode rows; touch
  `Views/EpisodePage.xaml`, `EpisodePageViewModel.Chapters`, and
  `PodcastChapterTimelineRail` together.

When changing now-playing state:
- Row/search list visuals: `TrackStateBehavior`, `TrackItem`,
  `SearchResultRowCard`, `SearchResultHeroCard`.
- Current-item displays outside lists: `PlayerBarViewModel`, `PlayerBar`,
  `SidebarPlayerWidget`, `ExpandedNowPlayingLayout`, `MiniVideoPlayer`,
  `RightPanelView`.

## Keeping This Guide Current

If you add, remove, or rename a track/episode surface:
1. Update the relevant section above (and the **Quick-find table**).
2. Re-run the re-verification commands at the top to confirm nothing else
   moved.
3. Update `last_verified` in the frontmatter.
4. If a surface stops being a track/episode renderer entirely, remove its
   row from the table — do not leave stale entries.
