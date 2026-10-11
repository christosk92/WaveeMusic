// ── Shell/NavModel.Binder.cs ───────────────────────────────────────────────────────────────────────────────────────
// the model's projection: the binder's published lists as typed facts (rootlist, library list, pins, feeds, counts), the
// source readiness (Failed included), the planner input, and the pump that keeps the binder current
//
// Role: CORE · Plan: technical-plan.md §5.1 row C9
//
// The binder (`SidebarProjectionBinder`, Sidebar.Host.cs) stays the one rebuild driver. This partial is the model's side of
// it: the typed reads the views used to take straight off `Sidebar.Binder`, and the subscription list the pane's effect
// used to own (`PumpBinder`). Every list ALIASES the binder's buffers, valid until the next rebuild; key a memo on
// <see cref="Revision"/> and subscribe through <see cref="InputVersion"/> / <see cref="LibraryItems"/>'s version.

using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public sealed partial class NavModel
{
    readonly Signal<int> _epoch = new(0);
    readonly List<RootlistEntry> _markers = new(256);
    int _markerRevision = -1;

    // ── the projection ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The planner input exactly as the binder published it (empty and Ready before the first rebuild). The row
    /// planner is the view's: it plans from <see cref="PlannerInput"/>.</summary>
    public SidebarProjectionInput Projection => _binder.CurrentInput;

    /// <summary>The rootlist tree, depth-first flattened, folders as <see cref="SidebarEntryKind.Folder"/> entries (null before
    /// the first rebuild). The FULL tree: structure is decided here, never on a plan.</summary>
    public IReadOnlyList<SidebarLibraryEntry>? Rootlist => _binder.CurrentInput.PlaylistTree;

    /// <summary>The published library list cell: chip → hidden kinds → search → sort → pins first, plus its version and load
    /// state. One cell, shared by the docked pane and the drawer.</summary>
    public SidebarEntries LibraryItems => _binder.Entries;

    /// <summary>The pin rows, resolved in pin order (an unresolved pin renders from its display cache, never disappears).</summary>
    public IReadOnlyList<SidebarLibraryEntry>? PinRows => _binder.CurrentInput.Pins;

    /// <summary>Recently played and New releases, resolved; empty while their section is hidden.</summary>
    public NavFeeds Feeds => new(_binder.CurrentInput.Played, _binder.CurrentInput.NewReleases);

    /// <summary>The kind counts over the FULL projection (before chip, search and hidden kinds). <c>Known</c> is false until
    /// the library has answered.</summary>
    public SidebarLibraryCounts Counts => _binder.Counts;

    /// <summary>Bumped once per rebuild that published anything: the planner's <c>DepKey</c> lane. A plain value.</summary>
    public int Revision => _binder.Revision;

    /// <summary>Bumped once per rebuild in which a planner input OTHER than the library list changed (the pin band, the feeds,
    /// the source states).</summary>
    public IReadSignal<int> InputVersion => _binder.InputVersion;

    /// <summary>Bumped once, when the binder first builds from the pump. A pane that rendered before the first projection
    /// folds it into its plan key, so it re-plans the moment one exists.</summary>
    public IReadSignal<int> BinderEpoch => _epoch;

    /// <summary>How far the rootlist and the saved library have answered, a failed ask included. Pending until the first
    /// rebuild. The planner keeps reading its two-state projection of this (<see cref="NavReadinessRules.ToPlanner"/>).</summary>
    public IReadSignal<NavReadinessFacts> Readiness => _binder.Readiness;

    /// <summary>"Not known to be loaded" never presents as "is": a filing against an empty tree lands nowhere.</summary>
    public bool RootlistLoaded => _binder.CurrentInput is { TreeState: SidebarSourceState.Ready, PlaylistTree.Count: > 0 };

    /// <summary>THE marker stream every legality question is asked against, derived from the published tree
    /// (<see cref="RootlistMarkerStream"/>) and cached per projection revision. Null while the tree is empty.</summary>
    public IReadOnlyList<RootlistEntry>? RootlistMarkers
    {
        get
        {
            var tree = _binder.CurrentInput.PlaylistTree;
            if (tree is null || tree.Count == 0) return null;
            if (_markerRevision != _binder.Revision)
            {
                RootlistMarkerStream.Build(tree, _markers);
                _markerRevision = _binder.Revision;
            }
            return _markers;
        }
    }

    /// <summary>The input a pane PLANS from: the binder's, with Liked Songs' title, and — until the binder has built once (the
    /// pump has not started it) — every dynamic source honestly PENDING: skeletons, never an empty library.</summary>
    public SidebarProjectionInput PlannerInput()
    {
        var input = _binder.CurrentInput;
        if (_binder.Revision == 0)
            input = input with { LibraryState = SidebarSourceState.Pending, TreeState = SidebarSourceState.Pending };
        return input with { LikedTitle = Loc.Get("nav.likedSongs") };
    }

    // ── the binder's drivers ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Idempotent start: do the first rebuild.</summary>
    public void Start() => _binder.Start();

    /// <summary>Rebuild iff a trigger moved. Returns whether it rebuilt.</summary>
    public bool Sync() => _binder.Sync();

    /// <summary>Force the next <see cref="Sync"/> to rebuild even if no trigger moved (a retry).</summary>
    public void Invalidate() => _binder.Invalidate();

    /// <summary>An account swap re-seeds the first-seen proxy from the new account's file.</summary>
    public void ResetFirstSeen() => _binder.ResetFirstSeen();

    /// <summary>The binder's subscription, as ONE signal effect body: every table and edge the projection reads, the two shell
    /// logs, the layout and library signals, and every preference that reshapes the pass. The binder itself only Peeks (it is
    /// not a computation), so this is what makes a hydrated playlist title, a rootlist push, a navigation or a filter change
    /// reach a pane. It SYNCS, never invalidates: the binder's gate folds the row versions of exactly the rows the sidebar
    /// shows, so a wake from a table change elsewhere costs one fold, not a three-pass rebuild. It reads
    /// <c>Entities.ScopeEpoch</c> FIRST, because the welcome effect switches scope on every login and this effect must
    /// re-point at the new scope's signals, not keep holding the retired set's.</summary>
    public void Pump()
    {
        var binder = _binder;
        _ = Entities.ScopeEpoch.Value;   // FIRST: a Switch re-points every table signal read below
        if (Entities.Current is { } scope)
        {
            var edges = scope.Edges;
            _ = edges.Rootlist.Changed.Value;
            _ = edges.SavedAlbums.Changed.Value;
            _ = edges.FollowedArtists.Changed.Value;
            _ = edges.SavedShows.Changed.Value;
            _ = edges.AlbumArtists.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Shows.Changed.Value;
        }
        // The recency feeds and the Recents sort, the new-releases feed.
        _ = Shell.History.Store.Version.Value;
        _ = Shell.PlayLog.Version.Value;
        _ = Notify.Items.Value;
        _ = Notify.ReleasesState.Value;
        _ = Sidebar.PinsVersion.Value;
        _ = Sidebar.LayoutVersion.Value;
        _ = Sidebar.Layout.Value;
        _ = Sidebar.FolderVersion.Value;
        _ = Sidebar.LibraryFilter.Value;
        _ = Sidebar.LibrarySearch.Value;

        bool first = binder.Revision == 0;
        if (first) binder.Start();
        else binder.Sync();
        if (first) _epoch.Value = _epoch.Peek() + 1;
    }
}
