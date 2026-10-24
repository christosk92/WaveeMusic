// ── Shell/PinVerbs.cs — pin and unpin a destination from the chrome ─────────────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X21). The tab menu's Pin row and the chrome's Pin button both used to
// decide "is this destination pinnable, and is it pinned" and write the sidebar's pin store from `Shell.UI.cs`. The decision
// and the two writes are here; the views build their row / button from the answer.

using FluentGpu.Localization;

namespace Wavee;

/// <summary>What the Pin row/button of a destination offers.</summary>
/// <param name="Kind">Pin or Unpin (never <see cref="PinRowKind.None"/>: a destination with no row has no target).</param>
/// <param name="PinId">The store's id for the destination.</param>
/// <param name="Title">The pin's display title.</param>
public readonly record struct PinTarget(PinRowKind Kind, string PinId, string Title);

/// <summary>The chrome's pin verbs.</summary>
public static class PinVerbs
{
    /// <summary>The Pin/Unpin offer for <paramref name="route"/>, or null when it is not pinnable (no signed-in store, a route
    /// with no pin id — <see cref="PinRowRule"/>).</summary>
    public static PinTarget? TargetFor(in Shell.Route route)
    {
        string? pinId = Shell.FrameRules.PinIdFor(route);
        var kind = PinRowRule.Decide(hasStore: Sidebar.AccountKey.Length > 0, pinId, Sidebar.IsPinned(pinId));
        if (kind == PinRowKind.None || pinId is not { } id) return null;
        string title = route.Kind == Shell.RouteKind.Search ? Loc.Get(Strings.Nav.Search) : Shell.Dest(route).Title;
        return new PinTarget(kind, id, title);
    }

    /// <summary>Pin a destination, recording the action in the journal.</summary>
    public static void Pin(string pinId, string title)
    {
        var pin = new SidebarPin(pinId, SidebarPinId.KindOf(pinId), SidebarPinId.UriOf(pinId), title,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Sidebar.PinRecorded(pin, title.Length > 0 ? title : Sidebar.SidebarMenus.PinName(pinId));
    }

    /// <summary>Unpin a destination, recording the action in the journal.</summary>
    public static void Unpin(string pinId) => Sidebar.UnpinRecorded(pinId, Sidebar.SidebarMenus.PinName(pinId));
}
