// ── Shell/NavModel.Selection.cs ────────────────────────────────────────────────────────────────────────────────────
// the model's two live facts about the app around the pane: which route is selected, and what is playing
//
// Role: CORE · Plan: technical-plan.md §5.1 row C9
//
// A pane draws a selection pill on the row whose route is selected, and a playing mark on the rows whose entity relates to
// the playing context. Both are decided here from the shell's current route and the playback seam; WHICH ROW of a plan that
// is (an index, a pill target) is the view's, because it is a fact about a plan.

namespace Wavee;

public sealed partial class NavModel
{
    Shell.Route _routeCache = Shell.Route.None;
    string _routeKeyCache = "";

    // ── selected ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The live selected route key. SUBSCRIBES the caller to the shell's current route.</summary>
    public string Selected => RouteKeyOf(Shell.Current.Value);

    /// <summary>The selected route key WITHOUT subscribing (rows re-render on their own epoch).</summary>
    public string SelectedPeek => RouteKeyOf(Shell.Current.Peek());

    string RouteKeyOf(in Shell.Route route)
    {
        if (!route.Equals(_routeCache))
        {
            _routeCache = route;
            _routeKeyCache = Shell.NameOf(route);
        }
        return _routeKeyCache;
    }

    /// <summary>The first visible top-level section of <paramref name="doc"/> that would hold <paramref name="route"/> if it were
    /// expanded: Pinned for a pinned route, Collections for one of its pages, Playlists for a playlist or folder in the tree,
    /// and Your Library for an entity route under the Library layout. Null otherwise.</summary>
    public string? OwningSection(SidebarLayoutDoc doc, string route)
    {
        if (route.Length == 0) return null;
        var input = _binder.CurrentInput;
        if (doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } pinned && Sidebar.IsPinned(SidebarPinId.FromRoute(route))) return pinned.Id;
        if (doc.Find(SidebarSectionKind.Collections) is { Hidden: false } collections && ItemsHold(collections.Items, route))
            return collections.Id;
        if (doc.Find(SidebarSectionKind.Playlists) is { Hidden: false } playlists
            && input.PlaylistTree is { } tree && EntriesHold(tree, route)) return playlists.Id;
        if (doc.Layout == SidebarLayoutId.Library && doc.Find(SidebarSectionKind.Library) is { Hidden: false } library
            && input.Library is { } list && EntriesHold(list, route)) return library.Id;
        return null;
    }

    static bool ItemsHold(IReadOnlyList<string> items, string route)
    {
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i], route, StringComparison.Ordinal)) return true;
        return false;
    }

    static bool EntriesHold(IReadOnlyList<SidebarLibraryEntry> entries, string route)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.Equals(entry.Id, route, StringComparison.Ordinal) || SidebarRowResolve.EntrySelects(in entry, route)) return true;
        }
        return false;
    }

    // ── playing ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is a context loaded, and is it playing? SUBSCRIBES the caller: the coarse active-context gate first (an idle
    /// app never joins the identity fan-out), the play state only while one is active.</summary>
    public NavPlaying ReadPlaying()
    {
        var seam = Controls.NowPlaying;
        bool active = seam is not null && seam.HasActiveContext.Value;
        bool playing = active && seam!.IsPlaying.Value;
        return new NavPlaying(active, playing);
    }

    /// <summary>Does the playing context relate to <paramref name="uri"/> (the entity itself, its container, a track inside it)?
    /// False while nothing is loaded.</summary>
    public bool PlayingRelatesTo(string uri) => Controls.NowPlaying is { } seam && seam.RelatesTo(uri);
}
