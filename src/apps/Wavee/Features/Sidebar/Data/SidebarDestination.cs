using System;

namespace Wavee;

/// <summary>Canonical routable destination shared by tabs, page chrome and the sidebar. A destination carries both the
/// exact navigation identity and the offline display cache used by a pin; it deliberately does not depend on a visual
/// node or list index.</summary>
public readonly record struct SidebarDestination(
    string RouteKey,
    string? Arg,
    string PinId,
    SidebarEntryKind Kind,
    string Uri,
    string Name)
{
    /// <summary>Build a pinnable destination, or null for internal/non-routable surfaces. Search is canonicalized to the
    /// generic Search destination: a query may label a tab, but it never creates one pin per query.</summary>
    public static SidebarDestination? FromRoute(string? routeKey, string? arg, string? displayName)
    {
        string? id = SidebarPinId.FromRoute(routeKey);
        if (id is null || routeKey is null) return null;
        string? navArg = string.Equals(routeKey, "search", StringComparison.Ordinal) ? null : arg;
        string uri = SidebarPinId.UriOf(id);
        if (uri.Length == 0 && routeKey.StartsWith("browse:", StringComparison.Ordinal))
            uri = routeKey.Substring("browse:".Length);
        return new SidebarDestination(routeKey, navArg, id, SidebarPinId.KindOf(id), uri, displayName ?? "");
    }
}
