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

/// <summary>What the Home Pin row/button of a destination offers: pin or unpin, the pin id, the title.</summary>
public readonly record struct HomePinTarget(bool Pinned, string PinId, string Title);

/// <summary>Pin and unpin to the pinned Home from anywhere (design 1.6): the action seams the container menus, the palette, the
/// title bar and the now-playing menu run through. One command each; the confirmation is the structure's
/// (<see cref="Theming.HomePolicy.Confirm"/>): a toast with Undo, or nothing (Zune: on Home the board shows the tile).</summary>
public static class HomePinVerbs
{
    /// <summary>Fill <see cref="ActionServices.IsHomePinned"/> / <see cref="ActionServices.SetHomePinned"/> (App.cs, once).</summary>
    public static void InstallActionSeams()
    {
        var s = Actions.Services;
        s.IsHomePinned = static route => HomePinId.Canonical(Shell.NameOf(route)) is { } id && HomeBoardStore.Instance.IsPinned(id);
        s.SetHomePinned = static (route, pinned) =>
        {
            if (HomePinId.Canonical(Shell.NameOf(route)) is not { } id) return;
            if (pinned) Pin(id, Shell.Dest(route).Title, "");
            else Unpin(id);
        };
    }

    /// <summary>A destination Home can hold: an album, artist, playlist, show, Liked Songs or a pinnable page.</summary>
    public static bool CanPin(in Shell.Route route) => HomePinId.Canonical(Shell.NameOf(route)) is not null && !HomeBoardStore.Instance.ReadOnly;

    /// <summary>The Home Pin/Unpin offer for <paramref name="route"/>, or null when Home cannot hold it. Reads the pins' version
    /// (tracked: a chrome button re-renders when the pin set moves).</summary>
    public static HomePinTarget? TargetFor(in Shell.Route route)
    {
        if (HomePinId.Canonical(Shell.NameOf(route)) is not { } id || HomeBoardStore.Instance.ReadOnly) return null;
        _ = HomeBoardStore.Instance.PinsVersion.Value;
        string title = route.Kind == Shell.RouteKind.Search ? Loc.Get(Strings.Nav.Search) : Shell.Dest(route).Title;
        return new HomePinTarget(HomeBoardStore.Instance.IsPinned(id), id, title);
    }

    /// <summary>Pin to Home (into <paramref name="groupId"/>, default Pinned): the board's verb when a board is mounted (its
    /// confirmation and reveal), else the store's command and the structure's confirmation.</summary>
    public static void Pin(string pinId, string title, string cover, string? groupId = null)
    {
        if (HomeBoardInspect.Current is { } board) { board.Pin(pinId, title, cover, groupId); return; }
        var applied = HomeBoardStore.Instance.Dispatch(new HomeCommand.Pin(pinId, title, cover, groupId));
        if (!applied.Changed) return;
        string name = title.Length > 0 ? title : Sidebar.Model.PinName(pinId);
        string group = applied.GroupId ?? HomeDocs.Pinned;
        Confirm(group == HomeDocs.Pinned
            ? Strings.Home.Board.Pin.Pinned(name)
            : Strings.Home.Board.Pin.PinnedIn(name, GroupTitle(group)));
    }

    public static void Unpin(string pinId)
    {
        if (HomeBoardInspect.Current is { } board) { board.Unpin(pinId); return; }
        var doc = HomeBoardStore.Instance.Doc.Peek();
        var (g, i) = HomeCommands.FindPin(doc, pinId);
        string name = g >= 0 && doc.Groups[g].Items[i].Source is PinSource p && p.NameHint.Length > 0 ? p.NameHint : Sidebar.Model.PinName(pinId);
        if (!HomeBoardStore.Instance.Dispatch(new HomeCommand.Unpin(pinId)).Changed) return;
        Confirm(Strings.Home.Board.Pin.Unpinned(name));
    }

    public static void Toggle(string pinId, string title, string cover, string? groupId = null)
    {
        if (HomeBoardStore.Instance.IsPinned(pinId)) Unpin(pinId);
        else Pin(pinId, title, cover, groupId);
    }

    /// <summary>The group's title as the board shows it.</summary>
    public static string GroupTitle(string groupId)
    {
        var doc = HomeBoardStore.Instance.Doc.Peek();
        int g = doc.GroupIndex(groupId);
        if (g < 0) return groupId;
        var group = doc.Groups[g];
        return group.Title ?? (group.TitleKey is { Length: > 0 } k ? Loc.Get(k) : groupId);
    }

    /// <summary>The structure's confirmation: a toast with Undo (one per change: the revision keys it), or nothing.</summary>
    static void Confirm(string text)
    {
        // Zune shows no toast: the sentence is still spoken (the toast speaks its own).
        if (ShellPolicy.Now.Home.Confirm == Theming.HomeConfirm.None) { FluentGpu.Input.Announcer.Say(text); return; }
        var store = HomeBoardStore.Instance;
        Notify.Say(text, FluentGpu.Controls.InfoBarSeverity.Informational, Loc.Get(Strings.Home.Board.Pin.Undo), static () => HomeBoardStore.Instance.Undo(),
            dedupeKey: "home.undo." + store.Doc.Peek().Rev.ToString(System.Globalization.CultureInfo.InvariantCulture), durationMs: 7000f);
    }
}
