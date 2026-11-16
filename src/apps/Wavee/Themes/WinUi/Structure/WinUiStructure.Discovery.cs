// ── Themes/WinUi/Structure/WinUiStructure.Discovery.cs — the discovery family's registrar (themes plan 6.2; P6 W5) ─────────────────────
//
// W5: Home, Browse and Search. WinUI's registrations win over Classic's stand-in key by key.
//
//   Home      Pages.Home (`Views\Discovery\Home\WinUiHomePage.cs`: the greeting, the pinned facet band, the zones), Home.FacetRow (`WinUiHomeFacetRow`: the
//             SelectorBar with the 3-px pill and Following), Home.Daylist and Home.DaylistClock (`WinUiHomeDaylist`, `WinUiHomeDaylistClock`: the card with its
//             cover, copy and clock column), the fourteen `Home.Zone.<Kind>` bodies and Home.Zone.RecentGrid (Jump back in), Home.Charts, Pages.HomeSection
//             (the drill grid, undesigned: WinUI's parts under the masthead). The Home quickplay strip stays Nothing (`RegisterOwn`): WinUI's pins are the nav
//             pane's Pinned section.
//   Browse    Pages.Browse and Browse.Charts, Pages.BrowseCategory, Pages.BrowseSection (the Home drill page's template under its own id).
//   Search    Pages.Search (the facet bar, the results, the top result that stacks below 900, no results).

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterDiscovery(ViewRegistryBuilder views)
    {
        // Home.
        views.Add(ViewKeys.HomePage, WinUiHomePage.Template, "winui.home.page");
        views.Add(ViewKeys.HomeFacetRow, WinUiHomeFacetRow.Template, "winui.home.facet-row");
        views.Add(ViewKeys.HomeDaylist, WinUiHomeDaylist.Template, "winui.home.daylist");
        views.Add(ViewKeys.HomeDaylistClock, WinUiHomeDaylistClock.Template, "winui.home.daylist-clock");
        views.Add(ViewKeys.HomeZoneRecentGrid, WinUiHomeRecents.Template, "winui.home.zone.recent-grid");
        views.Add(ViewKeys.HomeCharts, WinUiHomeCharts.Template, "winui.home.charts");
        // The pinned Home board (contract 1.117): the grid, the head-row tools, the tiles, the options presenter.
        views.Add(ViewKeys.HomeBoard, WinUiHomeBoard.Template, "winui.home.board");
        views.Add(ViewKeys.HomeBoardTools, WinUiHomeBoardTools.Template, "winui.home.board-tools");
        views.Add(ViewKeys.HomeTilePin, WinUiHomeTiles.Pin, "winui.home.tile.pin");
        views.Add(ViewKeys.HomeTileNowPlaying, WinUiHomeTiles.NowPlaying, "winui.home.tile.now-playing");
        views.Add(ViewKeys.HomeTileUpNext, WinUiHomeTiles.UpNext, "winui.home.tile.up-next");
        views.Add(ViewKeys.HomeTileRecents, WinUiHomeTiles.Recents, "winui.home.tile.recents");
        views.Add(ViewKeys.HomeTileReleases, WinUiHomeTiles.Releases, "winui.home.tile.releases");
        views.Add(ViewKeys.HomeTileDaylist, WinUiHomeTiles.Daylist, "winui.home.tile.daylist");
        views.Add(ViewKeys.HomeTileSection, WinUiHomeTiles.Section, "winui.home.tile.section");
        views.Add(ViewKeys.HomeTileOptions, WinUiHomeTileOptions.Template, "winui.home.tile.options");
        views.Add(ViewKeys.HomeTileVacant, WinUiHomeTiles.Vacant, "winui.home.tile.vacant");
        views.Add(ViewKeys.HomeZoneCoverShelf, WinUiHomeZones.BodyTemplate, "winui.home.zone.cover-shelf");
        views.Add(ViewKeys.HomeZoneWideTiles, WinUiHomeZones.BodyTemplate, "winui.home.zone.wide-tiles");
        views.Add(ViewKeys.HomeZoneReleaseList, WinUiHomeZones.BodyTemplate, "winui.home.zone.release-list");
        views.Add(ViewKeys.HomeZoneClusterCards, WinUiHomeZones.BodyTemplate, "winui.home.zone.cluster-cards");
        views.Add(ViewKeys.HomeZoneMixedCovers, WinUiHomeZones.BodyTemplate, "winui.home.zone.mixed-covers");
        views.Add(ViewKeys.HomeZoneRadioShelf, WinUiHomeZones.BodyTemplate, "winui.home.zone.radio-shelf");
        views.Add(ViewKeys.HomeZoneBrowseTiles, WinUiHomeZones.BodyTemplate, "winui.home.zone.browse-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeLead, WinUiHomeZones.BodyTemplate, "winui.home.zone.episode-lead");
        views.Add(ViewKeys.HomeZoneContinueEpisodes, WinUiHomeZones.BodyTemplate, "winui.home.zone.continue-episodes");
        views.Add(ViewKeys.HomeZoneShowGrid, WinUiHomeZones.BodyTemplate, "winui.home.zone.show-grid");
        views.Add(ViewKeys.HomeZoneVideoTiles, WinUiHomeZones.BodyTemplate, "winui.home.zone.video-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeRows, WinUiHomeZones.BodyTemplate, "winui.home.zone.episode-rows");
        views.Add(ViewKeys.HomeZonePodcastGroups, WinUiHomeZones.BodyTemplate, "winui.home.zone.podcast-groups");
        views.Add(ViewKeys.HomeZoneEmptyFacet, WinUiHomeZones.BodyTemplate, "winui.home.zone.empty-facet");
        views.Add(ViewKeys.HomeZoneMoodPlates, WinUiHomeZones.BodyTemplate, "winui.home.zone.mood-plates");
        views.Add(ViewKeys.HomeZoneBookShelf, WinUiHomeZones.BodyTemplate, "winui.home.zone.book-shelf");
        views.Add(ViewKeys.HomeSection, WinUiHomeSection.Template, "winui.home.section");

        // Browse.
        views.Add(ViewKeys.BrowseDirectory, WinUiBrowseDirectory.Template, "winui.browse.directory");
        views.Add(ViewKeys.BrowseCharts, WinUiBrowseDirectory.ChartsTemplate, "winui.browse.charts");
        views.Add(ViewKeys.BrowseCategory, WinUiBrowseCategory.Template, "winui.browse.category");
        views.Add(ViewKeys.BrowseSection, WinUiHomeSection.Template, "winui.browse.section");

        // Search.
        views.Add(ViewKeys.SearchPage, WinUiSearchPage.Template, "winui.search.page");
    }
}
