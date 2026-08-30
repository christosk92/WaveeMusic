using System;
using Wavee.Features.Concerts;

namespace Wavee.Features.Browse;

/// <summary>Route naming for Browse. <see cref="Home"/> is the directory (exact name — it must not collide with the
/// <see cref="Prefix"/> category pages). Category pages keep the prefix-plus-uri idiom (ConcertRoutes / DiscographyRoute).
/// </summary>
public static class BrowseRoutes
{
    /// <summary>The Browse directory. Exact name; <see cref="Is"/> is prefix-only and does not match this.</summary>
    public const string Home = "browse";

    public const string Prefix = "browse:";

    public static bool IsHome(string routeName)
        => string.Equals(routeName, Home, StringComparison.Ordinal);

    /// <summary>Route name for a category page uri ("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy").</summary>
    public static string Page(string pageUri) => Prefix + pageUri;

    public static bool Is(string routeName)
        => routeName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The page uri carried by a browse route, or "" when the route is not one.</summary>
    public static string UriOf(string routeName)
        => Is(routeName) ? routeName.Substring(Prefix.Length) : "";

    /// <summary>Map a BrowseClientFeature uri onto the client surface that owns it, or <c>null</c> when this client
    /// has no surface for it.
    ///
    /// <para>This used to fall back to the feature uri itself, on the theory that a new feature should open
    /// <em>something</em>. It could not: a client-feature uri (<c>spotify:concerts</c>) is not a route key, so the
    /// fallback navigated to a key no page renders — a tab on the not-found page, plus a permanent entry in the
    /// navigation log. Null is the honest answer, and it lets the caller decline to offer the destination at all.</para>
    /// </summary>
    public static string? FeatureRoute(string featureUri)
        => string.Equals(featureUri, "spotify:concerts", StringComparison.Ordinal)
            ? ConcertRoutes.Hub
            : null;
}
