// ── Home/HomeVerbs.cs — the verbs a Home view runs (T10a1) ─────────────────────────────────────────────────────────────────
//
// Role: CORE
//
// What a Home VIEW (Classic's, under Themes\Classic\Structure\Views\Home) does that reaches core state a theme may not name (WVTH002:
// `Shell`, `Playback`, `Entities`): open the listening history, a zone's "See all", Browse, the customizer, a tag's search, a chart tile, and
// start the daylist shuffled. Each was a static helper in the view file (`Zones.GoRecents`, `Zones.SeeAll`, `CustomizeScreen.GoToCustomize`,
// the daylist's `OpenTag`, `ChartTileRoute`); they are the same code here, so a view takes them as method groups and the theme never
// names the shell. Stateless: a handler, never a decision a view makes.

using FluentGpu.Localization;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>The navigation and playback verbs of Home's views.</summary>
public static class HomeVerbs
{
    /// <summary>The "Listening history" drill (the Recents header's tools link, the history tile).</summary>
    public static void OpenRecents() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));

    /// <summary>The empty facet's action: Browse.</summary>
    public static void OpenBrowse() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));

    /// <summary>The "Customize Home" link's navigation (and the palette command's one line).</summary>
    public static void OpenCustomize() => Shell.GoTo(new Shell.Route(Shell.RouteKind.HomeCustomize));

    /// <summary>A daylist tag opens Search for it.</summary>
    public static void OpenTag(string tag)
        => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(tag)));

    /// <summary>Shuffle STARTS the daylist shuffled; it never toggles (<c>HomeCardNav.Play</c> pauses a daylist that already plays).</summary>
    public static void ShuffleContext(string uri)
    {
        Playback.SetShuffle(true);
        Playback.PlayContext(uri);
    }

    /// <summary>A zone's own "See all", from its backing section slot; null when the zone carries none (an aggregate
    /// zone with no single backing section) — the header's tools then carry no "See all" link.</summary>
    public static Action? SeeAll(Zone zone)
    {
        if (zone.SectionSlot < 0) return null;
        int slot = zone.SectionSlot;
        return () =>
        {
            // The section's own drill route (title arg + Home origin), the one composer every "See all" shares.
            var route = HomeCardNav.SectionRoute(HomeSectionView.Of(new Section(slot)), browse: false);
            if (route.IsNone) return;
            Shell.GoTo(route, HomeCardNav.HomeOrigin);
        };
    }

    /// <summary>A chart tile opens its target.</summary>
    public static void OpenChart(ChartTileTarget target) => Shell.GoTo(ChartTileRoute(target));

    /// <summary>A picked item opens as itself (<see cref="Shell.For"/>: the playlist page, its title the arg); the
    /// fallback — and a uri no page renders — is the Charts category page with its title (<c>BrowseTiles.PageRoute</c>,
    /// the Browse directory's own route for it). UI thread (both parse/intern).</summary>
    static Shell.Route ChartTileRoute(ChartTileTarget target)
    {
        if (!target.IsFallback)
        {
            var route = Shell.For(EntityUri.Parse(target.Uri.AsSpan()), target.Title);
            if (route.Kind != Shell.RouteKind.NotFound) return route;
        }
        return global::Wavee.BrowseTiles.PageRoute(ChartPages.Charts, Loc.Get(Strings.Home.Charts));
    }
}
