// Engine-free by construction (System + FluentGpu.Localization + Browse/Concert/Home routes, no Element/Component)
// so DrillTrailTests can source-include it into Wavee.Tests, exactly like ShellNav.cs above it.
using System;
using System.Collections.Generic;
using FluentGpu.Localization;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;

namespace Wavee;

/// One crumb of a drill trail. RouteName == null marks the CURRENT page — the last crumb, never clickable.
readonly record struct DrillCrumb(string Label, string? RouteName, string? RouteArg = null);

/// <summary>The breadcrumb as a pure function of the route plus optional <see cref="NavOrigin"/> captured at Go
/// time. Without origin this is the IA answer (same route ⇒ same trail). With origin, one extra parent crumb is
/// composed: same-family origin inserts between root and current; foreign-family origin replaces the root.
/// One level deep by design.</summary>
static class DrillTrail
{
    public static IReadOnlyList<DrillCrumb> Of(string routeName, string? routeArg, string? liveTitle,
        NavOrigin? origin = null)
    {
        string? label = !string.IsNullOrWhiteSpace(liveTitle) ? liveTitle.Trim()
                       : !string.IsNullOrWhiteSpace(routeArg) ? routeArg.Trim()
                       : null;

        if (ConcertRoutes.TryParse(routeName, out var concert))
            return ConcertTrail(routeName, concert, label, origin);

        if (label is null) return [];
        return Compose(IaArms(routeName, label), origin, label, routeName);
    }

    static IReadOnlyList<DrillCrumb> IaArms(string routeName, string label)
    {
        if (HomeSectionRoutes.Is(routeName))
            return [new(Loc.Get(Strings.Nav.Home), "home"), new(label, null)];

        // A Home-minted section drills to Home (its trail arm above), but a BROWSE section/category drills to
        // Browse — even when the tile that opened it lived on the Home page (e.g. a Home Charts Fold). Home was
        // a shortcut into Browse's IA there, never an ancestor of it, so the parent crumb must say Browse.
        if (BrowseSectionRoutes.Is(routeName) || BrowseRoutes.Is(routeName))
            return [new(Loc.Get(Strings.Browse.HomeTitle), BrowseRoutes.Home), new(label, null)];

        return [];
    }

    static IReadOnlyList<DrillCrumb> ConcertTrail(string routeName, ConcertRoute concert, string? label,
        NavOrigin? origin)
    {
        var browse = new DrillCrumb(Loc.Get(Strings.Browse.HomeTitle), BrowseRoutes.Home);
        string concerts = Loc.Get(Strings.Concerts.Title);
        if (concert.Kind == ConcertRouteKind.Hub)
            return Compose([browse, new(concerts, null)], origin, concerts, routeName);

        string current = label ?? concerts;
        return Compose(
            [browse, new(concerts, ConcertRoutes.Hub), new(current, null)],
            origin, current, routeName);
    }

    /// <summary>no origin → IA; same-family origin → root + origin + current; foreign-family origin → origin replaces root.</summary>
    internal static IReadOnlyList<DrillCrumb> Compose(IReadOnlyList<DrillCrumb> ia, NavOrigin? origin,
        string currentLabel, string currentRoute)
    {
        if (origin is not { } o || string.IsNullOrWhiteSpace(o.Label)) return ia;
        var originCrumb = new DrillCrumb(o.Label.Trim(), o.RouteName, o.RouteArg);
        var current = new DrillCrumb(currentLabel, null);
        if (SameFamily(o.RouteName, currentRoute))
        {
            if (ia.Count >= 2) return [ia[0], originCrumb, current];
            return [originCrumb, current];
        }
        return [originCrumb, current];
    }

    internal static bool SameFamily(string a, string b)
        => (BrowseFamily(a) && BrowseFamily(b))
        || (HomeSectionRoutes.Is(a) && HomeSectionRoutes.Is(b));

    static bool BrowseFamily(string n)
        => BrowseRoutes.IsHome(n) || BrowseRoutes.Is(n) || BrowseSectionRoutes.Is(n) || ConcertRoutes.Is(n);
}
