// ── Shell/WorkspaceVerbs.cs — the tab strip's verbs: switch, close, pin, drag out, drop in ─────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X21). `Shell.UI.cs` built the strip's items AND owned what a tab drag
// carries and what a drop on a tab does. The strip is still the view's; the behaviour is `IChromeVerbs`, so a theme that draws
// its tabs differently (or not as a strip at all) calls the same workspace operations and the same drag contract.
//
// THE DRAG CONTRACT. A tab dragged out is its destination as a pin payload (tracks resolve lazily, after a compatible
// drop). EVERY tab is a spring-load waypoint (a dwell activates it); a tab standing for an EDITABLE playlist is also a
// deposit destination (the cross-tab append). A non-destination stays silent rather than flashing a refusal as a drag
// crosses the strip.

using System.Threading.Tasks;

namespace Wavee;

/// <summary>A tab that accepts a track deposit: the tab, and the editable playlist it stands for.</summary>
/// <param name="TabId">The tab's stable id (a spring-load dwell activates it).</param>
/// <param name="PlaylistUri">The playlist's uri.</param>
/// <param name="Name">Its display name (the drop caption and the deposit toast).</param>
public readonly record struct TabDropSite(int TabId, string PlaylistUri, string Name);

/// <summary>The chrome's workspace verbs (tabs).</summary>
public interface IChromeVerbs
{
    void ActivateTab(int index);
    void ActivateTabById(int id);
    void OpenTab(in Shell.Route route);
    void CloseTab(int index);
    void CloseTabById(int id);
    void SetTabPinned(int id, bool pinned);
    void CloseOtherTabs(int keepId);
    void CloseTabsToRight(int tabId);
    void CloseAllUnpinnedTabs();

    /// <summary>What a tab dragged out carries: its destination as a pin payload.</summary>
    DragPayload TabPayload(string pinId, string title);

    /// <summary>The deposit site a tab offers, or null when it is only a spring-load waypoint (not an editable playlist).</summary>
    TabDropSite? DropSiteFor(in Shell.Route route, int tabId, string name);

    /// <summary>May <paramref name="payload"/> be deposited into the playlist behind <paramref name="target"/>?</summary>
    bool TabAccepts(DragPayload payload, in TabDropSite target);

    /// <summary>Deposit the dropped payload's tracks into the tab's playlist.</summary>
    void TabDrop(DragPayload payload, in TabDropSite target);
}

/// <summary>The shell's implementation of <see cref="IChromeVerbs"/>: the workspace operations are the host's
/// (<c>Shell.Host.cs</c>), the drag contract is here.</summary>
public sealed class WorkspaceVerbs : IChromeVerbs
{
    /// <summary>The process-wide verbs the chrome calls.</summary>
    public static IChromeVerbs Shared { get; } = new WorkspaceVerbs();

    public void ActivateTab(int index) => Shell.ActivateTab(index);
    public void ActivateTabById(int id) => Shell.ActivateTabById(id);
    /// <summary>The strip's "+" (a new tab on <paramref name="route"/>). Refused under a structure without tabs (P5 ZK).</summary>
    public void OpenTab(in Shell.Route route)
    {
        if (!ShellCaps.Now.Tabs) return;
        Shell.OpenTab(route);
    }
    public void CloseTab(int index) => Shell.CloseTab(index);
    public void CloseTabById(int id) => Shell.CloseTabById(id);
    public void SetTabPinned(int id, bool pinned) => Shell.SetTabPinned(id, pinned);
    public void CloseOtherTabs(int keepId) => Shell.CloseOtherTabs(keepId);
    public void CloseTabsToRight(int tabId) => Shell.CloseTabsToRight(tabId);
    public void CloseAllUnpinnedTabs() => Shell.CloseAllUnpinnedTabs();

    public DragPayload TabPayload(string pinId, string title)
    {
        string uri = SidebarPinId.UriOf(pinId);
        var kind = uri.Length > 0 ? Drag.KindOfUri(uri) : DragKind.Route;
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (uri.Length > 0 && (kind is DragKind.Playlist or DragKind.Album or DragKind.Show)
            && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, pinId, uri, title, TrackResolver: resolver);
    }

    public TabDropSite? DropSiteFor(in Shell.Route route, int tabId, string name)
    {
        if (route.Kind == Shell.RouteKind.Playlist && route.Subject.IsValid
            && Entities.Current.Playlists.TryGetSlot(route.Subject.Text.AsSpan(), out int slot) && new Playlist(slot).Editable)
            return new TabDropSite(tabId, route.Subject.Text, name);
        return null;
    }

    public bool TabAccepts(DragPayload payload, in TabDropSite target)
        => Drag.TabAcceptsDeposit(target.PlaylistUri, targetEditable: true, payload.CanCopyTracks, payload.SourcePlaylistUri, payload.Uri);

    public void TabDrop(DragPayload payload, in TabDropSite target)
        => Sidebar.LibraryWrites?.DepositTracks?.Invoke(target.PlaylistUri, target.Name, payload);
}
