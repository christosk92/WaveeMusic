// ── Entities/Browse.Bands.cs — Browse's bands as a ROUTE argument and as page-chrome row 2 (P5 ZC1) ──────────────────────────────
//
// Role: CORE (pure rules and verbs; no picture)
// Owner: ZC1 (themes plan 6.3; owner Q26 / Q30)
//
// A band-style structure (Zune) draws Browse's destinations as row 2 of its band: top · for you · genres · charts · concerts. They are
// sibling ROUTES, not page state (Q26), so the row is answered from the route alone (`ChromeViews`, wired into
// `Shell.PageChromeChannel.RouteViews`) and stands on the navigation's first frame, and selecting a view navigates:
//
//   band id   destination                                                       Browse group(s) it stands for
//   top       the directory (`browse`, no argument: today's whole view)          Top, Mood & activity, More (they stay here)
//   foryou    `browse` with the argument `foryou`: the directory filtered         ForYou
//   genres    `browse` with the argument `genres`: the directory filtered         Genres
//   charts    the Charts category page (`browse:<ChartPages.Charts>`)             Charts
//   concerts  the Concerts hub (`RouteKind.Concerts`, the "browse" destination    (a client feature of Top)
//             of the band stays lit: `Shell.BandRules`)
//
// ROUTE SYNTAX. The Browse route carries the band in its `Arg`: key `browse`, arg `foryou` or `genres` (deep link
// `wavee://open?route=browse&arg=genres`; the plan's `browse?band=genres` is this pair). The arg is a place discriminator, not a display
// name (`PlaceByArg`: a band switch is a new place for history and the tab) and not a keep-alive discriminator (the mounted directory is
// reused: the host re-reads its props). `Canonical` is the one normaliser (`Shell.Parse` runs it): top, charts, concerts and anything
// unknown fold to NO arg, so a stray or hostile arg is the plain directory and a route round-trips through `NameOf` / `ArgOf` / `Parse`.
//
// CLASSIC. Classic draws no row 2 and nothing of its UI produces the arg, so its Browse is unchanged; its band order stays
// `BrowseTaxonomy.BandOrder` (Top, Charts, For you, Genres, Mood & activity, More: pinned by `BrowseTaxonomyTests`). The row-2 ORDER here is a
// separate, band-style order.

using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>Browse's bands: the ids, their routes and the lit rule (route level, pure but for the interned route args), the filter the
/// directory host applies for a band argument, and the row-2 views.</summary>
public static class BrowseBands
{
    /// <summary>The band ids (stable; never the localised label).</summary>
    public const string Top = "top", ForYou = "foryou", Genres = "genres", Charts = "charts", Concerts = "concerts";

    static readonly string[] s_all = [Top, ForYou, Genres, Charts, Concerts];
    static readonly string[] s_filtered = [ForYou, Genres];

    /// <summary>The bands in row 2's order.</summary>
    public static IReadOnlyList<string> All => s_all;

    /// <summary>The ids that are an ARGUMENT of the <c>browse</c> route (the filtered directory). The other three have a route of their own.</summary>
    public static IReadOnlyList<string> Filtered => s_filtered;

    /// <summary>The route argument for <paramref name="arg"/>: <c>"foryou"</c> or <c>"genres"</c> (case-insensitive, trimmed), else "" (the whole
    /// directory). The one normaliser of the Browse route's arg: <c>Shell.Parse</c>, the directory page and the lit rule all call it.</summary>
    public static string Canonical(ReadOnlySpan<char> arg)
    {
        arg = arg.Trim();
        if (arg.Equals(ForYou, StringComparison.OrdinalIgnoreCase)) return ForYou;
        if (arg.Equals(Genres, StringComparison.OrdinalIgnoreCase)) return Genres;
        return "";
    }

    /// <summary>The taxonomy group a filtered band shows, or null for a band that does not filter the directory.</summary>
    public static BrowseGroup? GroupOf(string band) => band switch
    {
        ForYou => BrowseGroup.ForYou,
        Genres => BrowseGroup.Genres,
        _ => null,
    };

    /// <summary>The directory's groups for <paramref name="band"/> (a <see cref="Canonical"/> arg): all of them for "" (the SAME instance, so the
    /// host's memo keeps its reference), only the band's group for a filtered band (empty when the directory has none of it). Order kept.</summary>
    public static IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> Filter(
        IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)> groups, string band)
    {
        if (GroupOf(band) is not { } group) return groups;
        var kept = new List<(BrowseGroup, IReadOnlyList<BrowseCategory>)>(1);
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Group == group) kept.Add(groups[i]);
        return kept;
    }

    // ── routes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The destination of <paramref name="band"/> (UI thread: the route interns its arg and title); <see cref="Shell.Route.None"/> for an unknown id.</summary>
    public static Shell.Route RouteOf(string band) => band switch
    {
        Top => new Shell.Route(Shell.RouteKind.Browse),
        ForYou or Genres => new Shell.Route(Shell.RouteKind.Browse, default, Entities.Strings.Intern(band.AsSpan())),
        Charts => BrowseTiles.PageRoute(ChartPages.Charts, Loc.Get(Strings.Browse.Charts)),
        Concerts => new Shell.Route(Shell.RouteKind.Concerts),
        _ => Shell.Route.None,
    };

    /// <summary>The kinds whose row 2 is Browse's bands: the directory and the Concerts hub. A drill-in below them keeps no row 2: its head is the trail
    /// (a category page, Z-Browse-Category: the trail over the colour field; a section; an artist's concerts; one concert).</summary>
    public static bool IsChromeKind(Shell.RouteKind kind)
        => kind is Shell.RouteKind.Browse or Shell.RouteKind.Concerts;

    /// <summary>Does <paramref name="route"/> draw the bands as row 2: a chrome kind, or the Charts page (the "charts" band's own destination is a category page, and the bands
    /// are how a reader leaves it for another band).</summary>
    public static bool HasBands(in Shell.Route route)
        => IsChromeKind(route.Kind) || (route.Kind == Shell.RouteKind.BrowseCategory && route.Subject == RouteOf(Charts).Subject);

    /// <summary>The band a route lights, or null when it lights none. The directory lights its argument (top when it has none); the Concerts hub
    /// lights concerts; a category page lights the band of its taxonomy group (the Charts pages: charts; a genre: genres; a For-you page: for you;
    /// Top, Mood &amp; activity, More and anything unmapped: top).</summary>
    public static string? BandOf(in Shell.Route route)
    {
        switch (route.Kind)
        {
            case Shell.RouteKind.Browse:
                return Canonical(Entities.Strings.Resolve(route.Arg)) is { Length: > 0 } band ? band : Top;
            case Shell.RouteKind.Concerts:
                return Concerts;
            case Shell.RouteKind.BrowseCategory:
                return CategoryBand(route.Subject.Text);
            default:
                return null;
        }
    }

    /// <summary>The band a category page (either uri spelling) lights.</summary>
    static string CategoryBand(string pageUri)
    {
        Span<char> buffer = stackalloc char[EntityUri.StackChars];
        string uri = Browse.PageUriOf(pageUri.AsSpan(), buffer).ToString();
        return BrowseTaxonomy.GroupOf(uri) switch
        {
            BrowseGroup.Charts => Charts,
            BrowseGroup.Genres => Genres,
            BrowseGroup.ForYou => ForYou,
            _ => Top,
        };
    }

    // ── row 2 ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One cached go-to verb per band (an equal row compares its verbs by reference); the route is built when the verb runs.</summary>
    static readonly Action[] s_go = Array.ConvertAll(s_all, static band => (Action)(() => Shell.GoTo(RouteOf(band))));

    static string LabelOf(string band) => band switch
    {
        Top => Loc.Get(Strings.Browse.Top),
        ForYou => Loc.Get(Strings.Browse.ForYou),
        Genres => Loc.Get(Strings.Browse.Genres),
        Charts => Loc.Get(Strings.Browse.Charts),
        _ => Loc.Get(Strings.Concerts.Title),
    };

    /// <summary>Row 2 of a Browse-family route: the five bands in order with <paramref name="current"/>'s band lit (none lit when the route lights none);
    /// empty for a route that is not <see cref="IsChromeKind"/>. The ids are the band ids.</summary>
    public static IReadOnlyList<PageView> ChromeViews(in Shell.Route current)
    {
        if (!HasBands(current)) return [];
        string? lit = BandOf(current);
        var views = new PageView[All.Count];
        for (int i = 0; i < views.Length; i++)
            views[i] = new PageView(All[i], LabelOf(All[i]), string.Equals(All[i], lit, StringComparison.Ordinal), s_go[i]);
        return views;
    }
}
