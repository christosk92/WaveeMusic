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

/// <summary>Whether a pinned entity is still there (design 1.5, the pin tile's unavailable face).</summary>
public static class HomePinState
{
    /// <summary>The identity group of every pinnable kind (an album's, a playlist's, an artist's, a show's first field group).</summary>
    const uint Identity = 1u;

    /// <summary>The pinned row is GONE: its identity was asked and nothing is coming. <see cref="GroupState.Absent"/> (the request settled
    /// without it: no route carries it, nobody owns the uri), or <see cref="GroupState.Failed"/> while the session is not offline (a 404, an
    /// answer that omitted the row until its miss retries ran out: the fetch layer's own words for a removed or region-locked entity). Offline
    /// the failure is the network's, and the tile keeps its last face. A tracked read: the tile re-renders when the answer moves.</summary>
    public static bool IsGone(EntityRef r)
    {
        if (r.IsNone || r.Kind == EntityKind.Collection) return false;
        _ = Entities.ScopeEpoch.Value;
        if (SectionTable.CardTable(r.Kind) is not { } table) return false;
        var state = table.Observe(r.Slot, Identity);
        if (state == GroupState.Absent) return true;
        return state == GroupState.Failed && Spotify.Status.Value is not (>= Spotify.SessionPhase.Offline and <= Spotify.SessionPhase.Minting);
    }

    /// <summary>The pinned row of a pin id (none for a route pin, Liked Songs or an unparsable id).</summary>
    public static EntityRef RefOf(string pinId)
    {
        string uri = HomePinId.UriOf(pinId);
        if (uri.Length == 0 || EntityUri.IsLikedCollection(uri)) return default;
        var id = EntityUri.Parse(uri);
        return id.IsValid ? Entities.Ref(id.Id) : default;
    }

    /// <summary>The identity group's state of a pinned row, lower case ("" for no row): the inspector's status reads it.</summary>
    public static string StateText(string pinId)
    {
        var r = RefOf(pinId);
        if (r.IsNone || SectionTable.CardTable(r.Kind) is not { } table) return "";
        return table.StateOf(r.Slot, Identity).ToString().ToLowerInvariant();
    }

    /// <summary>Ask for the pinned row's identity (from an effect, never a render): a pin nobody else shows is otherwise never asked,
    /// and only an asked row can be known or gone.</summary>
    public static void Ensure(EntityRef r)
    {
        if (r.IsNone || r.Kind == EntityKind.Collection || SectionTable.CardTable(r.Kind) is not { } table) return;
        if (table.StateOf(r.Slot, Identity) is GroupState.Known or GroupState.Inflight) return;
        Entities.Ensure(table, [r.Slot], Identity);
    }
}
