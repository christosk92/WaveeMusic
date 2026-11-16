// ── Home/Board/HomePinId.cs: one pin identity for Home, shared with the sidebar's (design 1.6) ─────────────────────
//
// HomePinId.Canonical = SidebarPinId.Canonical plus Liked Songs, which the sidebar refuses as a fixed route. Home-only
// kinds (track, episode, profile, a browse section) are Phase 2 and are refused here, in one place.

using Wavee.Theming.Contracts;

namespace Wavee;

public static class HomePinId
{
    /// <summary>A pin id, an entity uri or a route key → the canonical pin id; null = not pinnable to Home.</summary>
    public static string? Canonical(string? idOrUri)
    {
        if (string.IsNullOrEmpty(idOrUri)) return null;
        if (idOrUri == "liked" || EntityUri.IsLikedCollection(idOrUri)) return "liked";
        var id = SidebarPinId.Canonical(idOrUri);
        return id is null || SidebarPinId.KindOf(id) == SidebarEntryKind.Folder ? null : id;
    }

    /// <summary>The route key a pin opens (empty when it opens nothing).</summary>
    public static string RouteOf(string pinId) => SidebarPinId.RouteOf(pinId) ?? "";

    /// <summary>The entity uri behind a pin (empty for a route pin).</summary>
    public static string UriOf(string pinId) => SidebarPinId.UriOf(pinId);
}

public static class HomePins
{
    /// <summary>Home's pins as band pins (Zune's band strip reads Home's Pinned group). Built by the nav model, which
    /// owns the band's open, play and menu verbs.</summary>
    public static IReadOnlyList<BandPin> BandPinsOf(IReadOnlyList<PinSource> pins) => Sidebar.Model.BandPinsFromHome(pins);
}
