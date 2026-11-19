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

namespace Wavee.HomeUi;

/// <summary>The navigation and playback verbs of Home's views.</summary>
public static class HomeVerbs
{
    /// <summary>The "Listening history" drill (the Recents header's tools link, the history tile).</summary>
    public static void OpenRecents() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));

    /// <summary>The empty facet's action: Browse.</summary>
    public static void OpenBrowse() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));

    /// <summary>"Customize Home" (the facets' link, the palette command): Home's All facet with the theme's own way to change it open: WinUI's
    /// add band, Classic's Edit Home, Zune's customize (<see cref="IHomeBoardInspect.Customize"/>). On Home already, at once; elsewhere Home
    /// opens and the board enters it as it mounts. Nothing while <see cref="CanCustomize"/> is false.</summary>
    public static void OpenCustomize()
    {
        if (!CanCustomize) return;
        var cur = Shell.Current.Peek();
        if (cur.Kind == Shell.RouteKind.Home && Entities.Strings.Resolve(cur.Arg).Length == 0 && HomeBoardInspect.Current is { } board)
        {
            board.Customize();
            return;
        }
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        // A board kept alive behind another page is still registered: it enters at once. A first visit enters as the board mounts.
        if (HomeBoardInspect.Current is { } kept) kept.Customize();
        else HomeBoardInspect.CustomizeOnMount = true;
    }

    /// <summary>The board's daylist item (its id), or null when Home holds none: then "Also in: Home › Daylist tile" is plain text (no dead door). A
    /// subscribing read of the Home document, so a Settings row that asks re-reads when the daylist tile comes or goes.</summary>
    public static string? DaylistItemId() => DaylistIn(HomeBoardStore.Instance.Doc.Value);

    static string? DaylistIn(HomeDoc doc)
    {
        foreach (var group in doc.Groups)
            foreach (var item in group.Items)
                if (item.Source is WidgetSource { Widget: DaylistWidget }) return item.Id;
        return null;
    }

    /// <summary>The daylist widget's name in the Home document (<see cref="WidgetSource.Widget"/>).</summary>
    const string DaylistWidget = "daylist";

    /// <summary>"Also in: Home › Daylist tile" (settings design 5.1, 5.3): Home's All facet with the daylist tile's options open, where they draw the
    /// daylist's Clock and "Tell me when it refreshes". On Home already, at once; elsewhere Home opens and the board opens them (a board kept alive
    /// behind the page at once, else as it mounts). Nothing when Home holds no daylist tile.</summary>
    public static void OpenDaylistOptions()
    {
        if (DaylistIn(HomeBoardStore.Instance.Doc.Peek()) is not { } id) return;
        var cur = Shell.Current.Peek();
        if (!(cur.Kind == Shell.RouteKind.Home && Entities.Strings.Resolve(cur.Arg).Length == 0)) Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        if (HomeBoardInspect.Current is { } board) board.OpenOptions(id);
        else HomeBoardInspect.OptionsOnMount = id;
    }

    /// <summary>Whether this build offers "Customize Home" at all: the theme's way into changing Home is its edit mode or its add surface, and
    /// both can be switched off (<see cref="HomeCustomize"/>, owner 2026-11-18). The facets' link and the palette command show only while true.</summary>
    public static bool CanCustomize => HomeCustomize.EditMode || HomeCustomize.AddSurface;

    /// <summary>What the inspector answers when "Customize Home" is switched off.</summary>
    public const string CustomizeOff = "home customization is switched off for this release (HomeCustomize.EditMode and HomeCustomize.AddSurface)";

    /// <summary>What the inspector answers when the edit mode is switched off (<see cref="HomeCustomize.EditMode"/>).</summary>
    public const string EditOff = "home's edit mode is switched off for this release (HomeCustomize.EditMode)";

    /// <summary>What the inspector answers when the add surface is switched off (<see cref="HomeCustomize.AddSurface"/>).</summary>
    public const string AddOff = "home's add surface is switched off for this release (HomeCustomize.AddSurface)";

    /// <summary>A daylist tag opens Search for it.</summary>
    public static void OpenTag(string tag)
        => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(tag)));

    /// <summary>A daylist tag opens its own mix (<paramref name="link"/>, the tag's <c>nicheMixLinks</c> playlist), else Search for it.</summary>
    public static void OpenTag(string tag, string? link)
    {
        if (link is { Length: > 0 })
        {
            var route = Shell.For(EntityUri.Parse(link.AsSpan()), tag);
            if (route.Kind != Shell.RouteKind.NotFound) { Shell.GoTo(route, HomeCardNav.HomeOrigin); return; }
        }
        OpenTag(tag);
    }

    /// <summary>An entity a section hangs off (a header's "The Pog State", a cluster's "Henry Moodie") opens as itself.</summary>
    public static void OpenEntity(string uri, string name)
    {
        var route = Shell.For(EntityUri.Parse(uri.AsSpan()), name);
        if (route.Kind != Shell.RouteKind.NotFound) Shell.GoTo(route, HomeCardNav.HomeOrigin);
    }

    /// <summary>A zone header's "See all" words: "See all 36" when the section says how many it holds, else "See all".</summary>
    public static string SeeAllLabel(Zone zone)
        => zone.TotalCount > 1 ? Strings.Home.SeeAllN(zone.TotalCount) : Loc.Get(Strings.Home.SeeAll);

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
        // A followed release's section holds its one album, which the Release Spotlight already shows whole: nothing more to see.
        if (zone.SectionSlot < 0 || zone.Kind == ZoneKind.ReleaseSpotlight) return null;
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
