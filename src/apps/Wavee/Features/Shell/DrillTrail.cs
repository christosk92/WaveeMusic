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
/// time. Without origin this is the IA answer (same route ⇒ same trail). With origin, ONE extra parent crumb is
/// composed — the journey's answer, never more than one level: an origin that IS the IA root adds nothing; a
/// same-family origin inserts between root and current (Browse › Netflix › New on Netflix); a foreign-family origin
/// on a Browse-family page is PREPENDED to the whole IA trail (Home › Browse › Weekly Song Charts — Home is where
/// the user came from, Browse is where the page lives, and both are true) — except a search LOOKUP, which jumped
/// straight to the page and would only invent a Browse visit (search "pop" › Pop); a foreign-family origin anywhere
/// else replaces the root.</summary>
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

        // A Home-minted section drills to Home (its trail arm above), but a BROWSE section/category lives under
        // Browse in the IA — even when the tile that opened it sat on the Home page (a Home Charts Fold). The IA
        // answer therefore says Browse; the JOURNEY answer (Home › Browse › X) is Compose's job, once the opener
        // hands over its NavOrigin. Without one, the IA is all there is.
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

    /// <summary>no origin → IA; origin == IA root → IA (Browse-home opening a Browse section, Home opening a Home
    /// section: nothing to add); same-family origin → root + origin + current; foreign-family origin on a
    /// Browse-family route → origin + the whole IA, unless the origin is a search lookup; otherwise → origin replaces
    /// the root.</summary>
    internal static IReadOnlyList<DrillCrumb> Compose(IReadOnlyList<DrillCrumb> ia, NavOrigin? origin,
        string currentLabel, string currentRoute)
    {
        if (origin is not { } o || string.IsNullOrWhiteSpace(o.Label)) return ia;
        if (ia.Count > 0 && IsRoot(ia[0], o)) return ia;
        var originCrumb = new DrillCrumb(o.Label.Trim(), o.RouteName, o.RouteArg);
        var current = new DrillCrumb(currentLabel, null);
        if (SameFamily(o.RouteName, currentRoute))
        {
            if (ia.Count >= 2) return [ia[0], originCrumb, current];
            return [originCrumb, current];
        }
        if (BrowseFamily(currentRoute) && ia.Count > 0 && !LookupOrigin(o))
        {
            // The origin is an ANCESTOR of the IA here, not a replacement for it: the user came from Home, the page
            // lives under Browse, and the Browse crumb stays clickable so the IA parent is one tap away too.
            var arms = new DrillCrumb[ia.Count + 1];
            arms[0] = originCrumb;
            for (int i = 0; i < ia.Count; i++) arms[i + 1] = ia[i];
            return arms;
        }
        return [originCrumb, current];
    }

    /// <summary>A search result is a LOOKUP, not a place: the query jumped straight to the page, so its crumb stands
    /// alone ("pop" › Pop) — a Browse crumb between them would claim a visit that never happened. Home, by contrast,
    /// is a place the page was reached FROM, and the IA parent belongs between them.</summary>
    static bool LookupOrigin(NavOrigin o) => string.Equals(o.RouteName, "search", StringComparison.Ordinal);

    /// <summary>The origin is the trail's own root (same route identity) — the journey and the IA agree.</summary>
    static bool IsRoot(DrillCrumb root, NavOrigin origin)
        => root.RouteName is { } name
        && string.Equals(name, origin.RouteName, StringComparison.Ordinal)
        && string.Equals(root.RouteArg ?? "", origin.RouteArg ?? "", StringComparison.Ordinal);

    internal static bool SameFamily(string a, string b)
        => (BrowseFamily(a) && BrowseFamily(b))
        || (HomeSectionRoutes.Is(a) && HomeSectionRoutes.Is(b));

    static bool BrowseFamily(string n)
        => BrowseRoutes.IsHome(n) || BrowseRoutes.Is(n) || BrowseSectionRoutes.Is(n) || ConcertRoutes.Is(n);
}
