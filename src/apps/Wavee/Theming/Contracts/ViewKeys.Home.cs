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


    /// <summary>A cover shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneCoverShelf = new("Home.Zone.CoverShelf");
    /// <summary>A wide-tile shelf's body. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneWideTiles = new("Home.Zone.WideTiles");
    /// <summary>The release list's body ("From artists you follow"). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneReleaseList = new("Home.Zone.ReleaseList");
    /// <summary>The Spine Shelf ("Because you like"): one open card and its preview songs, the rest as spines. Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneSpineShelf = new("Home.Zone.SpineShelf");
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
    /// <summary>Colour-plate mixes as flat plates with the mix name (home-gap P6). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneMoodPlates = new("Home.Zone.MoodPlates");
    /// <summary>Audiobooks with author, rating, length and plan, every card the same caption lines (home-gap P8). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneBookShelf = new("Home.Zone.BookShelf");
    /// <summary>A personal set as one lead card beside a small grid (Home rhythm A). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneLeadGrid = new("Home.Zone.LeadGrid");
    /// <summary>Small tiles with the cover beside the text, two to five columns (Home rhythm A). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneCompactGrid = new("Home.Zone.CompactGrid");
    /// <summary>A row of round covers with the names centred (Home rhythm A). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneArtistCircles = new("Home.Zone.ArtistCircles");
    /// <summary>One capped row of two-line cards, "See all N" instead of paging (Home rhythm A). Props: <see cref="HomeZoneProps"/>.</summary>
    public static readonly ViewKey<HomeZoneProps> HomeZoneCappedShelf = new("Home.Zone.CappedShelf");

    /// <summary>The "See all" / charts drill page: the "N items" line over the bound card grid in a skeleton region. Mounted by `SectionScreen`. Props: <see cref="HomeSectionProps"/>.</summary>
    public static readonly ViewKey<HomeSectionProps> HomeSection = new("Pages.HomeSection");

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
            case ZoneKind.SpineShelf: key = ViewKeys.HomeZoneSpineShelf; return true;
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
            case ZoneKind.MoodPlates: key = ViewKeys.HomeZoneMoodPlates; return true;
            case ZoneKind.BookShelf: key = ViewKeys.HomeZoneBookShelf; return true;
            case ZoneKind.LeadGrid: key = ViewKeys.HomeZoneLeadGrid; return true;
            case ZoneKind.CompactGrid: key = ViewKeys.HomeZoneCompactGrid; return true;
            case ZoneKind.ArtistCircles: key = ViewKeys.HomeZoneArtistCircles; return true;
            case ZoneKind.CappedShelf: key = ViewKeys.HomeZoneCappedShelf; return true;
            default: key = default; return false;
        }
    }
}
