// ── Themes/Zune/Structure/ZuneStructure.Discovery.cs — the discovery family's registrar (themes plan 6.3; P5 Z5) ──────────────────────────
//
// Z5: Home, Browse and Search. Zune's registrations win over Classic's stand-in key by key.
//
//   Home      Pages.Home (`Views\Home\ZuneHomePage.cs`: the pinned board on All, the zones and the paired rows elsewhere), the board (`Views\Home\Board`),
//             the fourteen `Home.Zone.<Kind>` bodies and Home.Charts (`ZuneHomeZones`), Home.Zone.RecentGrid (the history group), Home.Daylist and
//             Home.DaylistClock (`ZuneHomeDaylist`), Pages.HomeSection (`ZuneHomeSection`). Home.FacetRow is Nothing: the facet words are the
//             band's row 2 (published by the core host, `HomeHost`).
//   Browse    Pages.Browse and Browse.Charts (`Views\Browse\ZuneBrowseDirectory.cs`), Pages.BrowseCategory (`ZuneBrowseCategory`), Pages.BrowseSection
//             (the Home drill page's template under its own id).
//   Search    Pages.Search (`Views\Search\ZuneSearchPage.cs`): the facet body alone; the facet strip is the band's row 2 (published by
//             `Search.PageHost`, each facet with its count).

using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterDiscovery(ViewRegistryBuilder views)
    {
        // Home.
        views.Add(ViewKeys.HomePage, ZuneHomePage.Template, "zune.home.page");
        views.Add(ViewKeys.HomeZoneRecentGrid, ZuneHomeHistory.HistoryTemplate, "zune.home.zone.recent-grid");
        // The pinned Home board (contract 1.117, spec 8.2): the board with its title, all items and the undo word; the app bar is the board's tools
        // (the page lays it over its foot); the tiles; the tile's back.
        views.Add(ViewKeys.HomeBoard, ZuneHomeBoard.Template, "zune.home.board");
        views.Add(ViewKeys.HomeBoardTools, ZuneHomeAppBar.Template, "zune.home.app-bar");
        views.Add(ViewKeys.HomeTilePin, ZuneHomeTiles.Pin, "zune.home.tile.pin");
        views.Add(ViewKeys.HomeTileNowPlaying, ZuneHomeTiles.NowPlaying, "zune.home.tile.now-playing");
        views.Add(ViewKeys.HomeTileUpNext, ZuneHomeTiles.UpNext, "zune.home.tile.up-next");
        views.Add(ViewKeys.HomeTileRecents, ZuneHomeTiles.Recents, "zune.home.tile.recents");
        views.Add(ViewKeys.HomeTileReleases, ZuneHomeTiles.Releases, "zune.home.tile.releases");
        views.Add(ViewKeys.HomeTileDaylist, ZuneHomeTiles.Daylist, "zune.home.tile.daylist");
        views.Add(ViewKeys.HomeTileSection, ZuneHomeTiles.Section, "zune.home.tile.section");
        views.Add(ViewKeys.HomeTileOptions, ZuneHomeTileBack.Template, "zune.home.tile.back");
        views.Add(ViewKeys.HomeTileVacant, ZuneHomeTiles.Vacant, "zune.home.tile.vacant");
        views.Add(ViewKeys.HomeDaylist, ZuneHomeDaylist.Template, "zune.home.daylist");
        views.Add(ViewKeys.HomeDaylistClock, ZuneHomeDaylist.ClockTemplate, "zune.home.daylist-clock");
        views.Add(ViewKeys.HomeCharts, ZuneHomeZones.ChartsTemplate, "zune.home.charts");
        views.Add(ViewKeys.HomeZoneCoverShelf, ZuneHomeZones.BodyTemplate, "zune.home.zone.cover-shelf");
        views.Add(ViewKeys.HomeZoneWideTiles, ZuneHomeZones.BodyTemplate, "zune.home.zone.wide-tiles");
        views.Add(ViewKeys.HomeZoneReleaseList, ZuneHomeZones.BodyTemplate, "zune.home.zone.release-list");
        views.Add(ViewKeys.HomeZoneSpineShelf, ZuneHomeZones.BodyTemplate, "zune.home.zone.spine-shelf");
        views.Add(ViewKeys.HomeZoneMixedCovers, ZuneHomeZones.BodyTemplate, "zune.home.zone.mixed-covers");
        views.Add(ViewKeys.HomeZoneRadioShelf, ZuneHomeZones.BodyTemplate, "zune.home.zone.radio-shelf");
        views.Add(ViewKeys.HomeZoneBrowseTiles, ZuneHomeZones.BodyTemplate, "zune.home.zone.browse-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeLead, ZuneHomeZones.BodyTemplate, "zune.home.zone.episode-lead");
        views.Add(ViewKeys.HomeZoneContinueEpisodes, ZuneHomeZones.BodyTemplate, "zune.home.zone.continue-episodes");
        views.Add(ViewKeys.HomeZoneShowGrid, ZuneHomeZones.BodyTemplate, "zune.home.zone.show-grid");
        views.Add(ViewKeys.HomeZoneVideoTiles, ZuneHomeZones.BodyTemplate, "zune.home.zone.video-tiles");
        views.Add(ViewKeys.HomeZoneEpisodeRows, ZuneHomeZones.BodyTemplate, "zune.home.zone.episode-rows");
        views.Add(ViewKeys.HomeZonePodcastGroups, ZuneHomeZones.BodyTemplate, "zune.home.zone.podcast-groups");
        views.Add(ViewKeys.HomeZoneEmptyFacet, ZuneHomeZones.BodyTemplate, "zune.home.zone.empty-facet");
        views.Add(ViewKeys.HomeZoneMoodPlates, ZuneHomeZones.BodyTemplate, "zune.home.zone.mood-plates");
        views.Add(ViewKeys.HomeZoneBookShelf, ZuneHomeZones.BodyTemplate, "zune.home.zone.book-shelf");
        views.Add(ViewKeys.HomeZoneLeadGrid, ZuneHomeZones.BodyTemplate, "zune.home.zone.lead-grid");
        views.Add(ViewKeys.HomeZoneCompactGrid, ZuneHomeZones.BodyTemplate, "zune.home.zone.compact-grid");
        views.Add(ViewKeys.HomeZoneArtistCircles, ZuneHomeZones.BodyTemplate, "zune.home.zone.artist-circles");
        views.Add(ViewKeys.HomeZoneCappedShelf, ZuneHomeZones.BodyTemplate, "zune.home.zone.capped-shelf");
        views.Add(ViewKeys.HomeZoneReleaseSpotlight, ZuneHomeZones.BodyTemplate, "zune.home.zone.release-spotlight");
        views.Add(ViewKeys.HomeSection, ZuneHomeSection.Template, "zune.home.section");
        CommonNothing.Register(views, ViewKeys.HomeFacetRow);

        // Browse.
        views.Add(ViewKeys.BrowseDirectory, ZuneBrowseDirectory.Template, "zune.browse.directory");
        views.Add(ViewKeys.BrowseCharts, ZuneBrowseDirectory.ChartsTemplate, "zune.browse.charts");
        views.Add(ViewKeys.BrowseCategory, ZuneBrowseCategory.Template, "zune.browse.category");
        views.Add(ViewKeys.BrowseSection, ZuneHomeSection.Template, "zune.browse.section");

        // Search.
        views.Add(ViewKeys.SearchPage, ZuneSearchPage.Template, "zune.search.page");
    }
}
