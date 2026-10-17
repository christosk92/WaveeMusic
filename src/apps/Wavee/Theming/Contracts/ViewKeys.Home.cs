// ── Theming/Contracts/ViewKeys.Home.cs — the Home family of published view keys (themes plan 6.0, T10a1) ────────────────────
//
// T10a1 publishes Home: the page (`Pages.Home`: the scroll column, the facet row slot, the skeleton region, the zone column and
// each zone's chapter), the facet row, the daylist card and its clock, the zone BODIES (`Home.Zone.<Kind>`), the Charts block, the
// "See all" drill page and the customizer.
//
// WHAT A ZONE KEY CARRIES. A chapter is the structure's: the pinned header, the clipped body, the pager in the header's tools and the
// entrance row belong to the page view. A zone's BODY is the view a theme swaps per kind: a cover shelf, the release list, the
// cluster cards, the tiles, the episode rows, the empty facet. The two kinds that need core state keep a core host that mounts
// their key (the Recents grid: the plays and the week; the Charts block: its demand and the pick), and the daylist is its own key.
// `HomeZoneKeys.TryKeyOf` is the one map from a `ZoneKind` to its body key.

using FluentGpu.Theming;
using Wavee.HomeUi;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Home page: the shell wash, the scroll column with the facet row and the skeleton region over the zone column.
    /// Mounted by `HomeScreen` (the host: the facet machine, the model, the verdict, the wash claim). Props: <see cref="HomePageProps"/>.</summary>
    public static readonly ViewKey<HomePageProps> HomePage = new("Pages.Home");

    /// <summary>The facet row: the facet words as the page title, the Following toggle, the busy bar and the failure bar. Props: <see cref="FacetRowProps"/>.</summary>
    public static readonly ViewKey<FacetRowProps> HomeFacetRow = new("Home.FacetRow");

    /// <summary>The daylist hero card (art, copy, actions, the clock slot). Mounted by `DaylistCard`. Props: <see cref="HomeDaylistProps"/>.</summary>
    public static readonly ViewKey<HomeDaylistProps> HomeDaylist = new("Home.Daylist");

    /// <summary>One face of the daylist clock: the countdown, the update-time face, "updating" or "running late". Mounted by the clock hosts. Props: <see cref="HomeDaylistClockProps"/>.</summary>
    public static readonly ViewKey<HomeDaylistClockProps> HomeDaylistClock = new("Home.DaylistClock");

    /// <summary>The Recently-played grid body (the played tiles and the history tile). Mounted by `RecentGrid`. Props: <see cref="HomeRecentsProps"/>.</summary>
    public static readonly ViewKey<HomeRecentsProps> HomeZoneRecentGrid = new("Home.Zone.RecentGrid");

    /// <summary>The Browse tiles' Charts block (Charts, Top 50, Viral 50). Mounted by `ChartsBlockView`. Props: <see cref="HomeChartsProps"/>.</summary>
    public static readonly ViewKey<HomeChartsProps> HomeCharts = new("Home.Charts");

    /// <summary>The quickplay strip at the head of Home (contract 1.28, P5 Z5): the user's pins, the listening history and what is new from
    /// the artists they follow, side by side. Mounted by `QuickplayHost` where a structure places it (Zune's Home on the All facet); a
    /// structure with no quickplay registers Nothing for it (Classic). Props: <see cref="HomeQuickplayProps"/>.</summary>
    public static readonly ViewKey<HomeQuickplayProps> HomeQuickplay = new("Home.Quickplay");

    /// <summary>A cover shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneCoverShelf = new("Home.Zone.CoverShelf");
    /// <summary>A wide-tile shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneWideTiles = new("Home.Zone.WideTiles");
    /// <summary>The release list's body ("From artists you follow"). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneReleaseList = new("Home.Zone.ReleaseList");
    /// <summary>The cluster cards' body ("Because you like"). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneClusterCards = new("Home.Zone.ClusterCards");
    /// <summary>A mixed-covers shelf's body ("Jump back in"). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneMixedCovers = new("Home.Zone.MixedCovers");
    /// <summary>A radio shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneRadioShelf = new("Home.Zone.RadioShelf");
    /// <summary>The Browse tiles' body (the category tiles, then the Charts block). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneBrowseTiles = new("Home.Zone.BrowseTiles");
    /// <summary>The Podcasts facet's lead episode rows. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneEpisodeLead = new("Home.Zone.EpisodeLead");
    /// <summary>The Podcasts facet's "continue listening" rows. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneContinueEpisodes = new("Home.Zone.ContinueEpisodes");
    /// <summary>The Podcasts facet's show grid shelf. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneShowGrid = new("Home.Zone.ShowGrid");
    /// <summary>A video-tile shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneVideoTiles = new("Home.Zone.VideoTiles");
    /// <summary>An episode-rows zone's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneEpisodeRows = new("Home.Zone.EpisodeRows");
    /// <summary>The Podcasts facet's grouped show cards. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZonePodcastGroups = new("Home.Zone.PodcastGroups");
    /// <summary>The empty facet's vacancy. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneEmptyFacet = new("Home.Zone.EmptyFacet");

    /// <summary>The "See all" / charts drill page: the "N items" line over the bound card grid in a skeleton region. Mounted by `SectionScreen`. Props: <see cref="HomeSectionProps"/>.</summary>
    public static readonly ViewKey<HomeSectionProps> HomeSection = new("Pages.HomeSection");

    /// <summary>The Home customizer: the header with Reset and the reorderable zone rows. Mounted by `CustomizeScreen`. Props: <see cref="HomeCustomizeProps"/>.</summary>
    public static readonly ViewKey<HomeCustomizeProps> HomeCustomize = new("Pages.HomeCustomize");
}

/// <summary>The map from a <see cref="ZoneKind"/> to the key of its body view. The daylist and the Recents grid are not in it: they have
/// their own props (<see cref="ViewKeys.HomeDaylist"/>, <see cref="ViewKeys.HomeZoneRecentGrid"/>).</summary>
public static class HomeZoneKeys
{
    /// <summary>The body key of <paramref name="kind"/>; false for the daylist and the Recents grid.</summary>
    public static bool TryKeyOf(ZoneKind kind, out FluentGpu.Theming.ViewKey<HomeZoneProps> key)
    {
        switch (kind)
        {
            case ZoneKind.CoverShelf: key = ViewKeys.HomeZoneCoverShelf; return true;
            case ZoneKind.WideTiles: key = ViewKeys.HomeZoneWideTiles; return true;
            case ZoneKind.ReleaseList: key = ViewKeys.HomeZoneReleaseList; return true;
            case ZoneKind.ClusterCards: key = ViewKeys.HomeZoneClusterCards; return true;
            case ZoneKind.MixedCovers: key = ViewKeys.HomeZoneMixedCovers; return true;
            case ZoneKind.RadioShelf: key = ViewKeys.HomeZoneRadioShelf; return true;
            case ZoneKind.BrowseTiles: key = ViewKeys.HomeZoneBrowseTiles; return true;
            case ZoneKind.EpisodeLead: key = ViewKeys.HomeZoneEpisodeLead; return true;
            case ZoneKind.ContinueEpisodes: key = ViewKeys.HomeZoneContinueEpisodes; return true;
            case ZoneKind.ShowGrid: key = ViewKeys.HomeZoneShowGrid; return true;
            case ZoneKind.VideoTiles: key = ViewKeys.HomeZoneVideoTiles; return true;
            case ZoneKind.EpisodeRows: key = ViewKeys.HomeZoneEpisodeRows; return true;
            case ZoneKind.PodcastGroups: key = ViewKeys.HomeZonePodcastGroups; return true;
            case ZoneKind.EmptyFacet: key = ViewKeys.HomeZoneEmptyFacet; return true;
            default: key = default; return false;
        }
    }
}
