// ── Shell/NavModel.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the navigation pane's core model: what the sidebar knows (destinations, pins, the rootlist, the library list, the feeds,
// the selected route, what plays, how far the sources have answered, the account, edit mode, persistence health, the
// layout document) and what it can do (INavVerbs), with no row planning and no pixels
//
// Role: CORE
// Plan: docs/themes/plan/technical-plan.md §5.1 row C9 (BP X25, core half) · abstraction-map.md §2.1.4
//
// THE SPLIT. `NavModel` is the layout-neutral half of the old sidebar renderer: the binder's projection, the selection and
// play sweeps' inputs, the section / folder / pin / create verbs. The Classic pane (`Sidebar.UI*.cs`) still plans rows,
// diffs plans and freezes a drag from it (those move to the Classic structure in T5); it READS this model and CALLS these
// verbs, and owns no decision about which route is selected, what a pin toast says or what a section toggle writes.
//
// OWNERSHIP. The `Sidebar` service keeps the stored state it always owned (the layout file, the pin store, the folder set,
// edit mode, the persistence health: other modules read those through `Sidebar.*`); the model is the ONE read surface the
// views use and the ONE place the verbs live. Its projection is the `SidebarProjectionBinder`'s, published as the binder
// publishes it (the lists alias the binder's buffers, valid until the next rebuild; key a memo on `Revision`).
//
// BUILT AT BOOT, STARTED BY THE PUMP. `Sidebar.Boot` constructs the model; nothing reads the network or starts the binder
// there. Until the first rebuild (`PumpBinder`, `Start`) `Readiness` is Pending and `PlannerInput` is honest skeleton.
//
// THREADING: UI thread only, unsynchronized, like every static service here. No environment-variable switches.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

// ── readiness ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How far one source (the rootlist, or the saved albums / artists / shows) has answered. <see cref="Failed"/> is the
/// state the binder could not say before: an unanswered list whose last ask failed (<see cref="EdgeState.Failed"/>).</summary>
public enum NavReadiness : byte
{
    /// <summary>Nobody has answered yet. A view draws a skeleton, never an empty library.</summary>
    Pending = 0,
    /// <summary>The list has answered (partially or completely). An empty answer is a real, renderable "you have none".</summary>
    Ready = 1,
    /// <summary>Nobody has answered AND the last ask failed.</summary>
    Failed = 2,
}

/// <summary>The two sources' readiness. <c>default</c> is Pending, both: "not asked yet" is the honest default.</summary>
public readonly record struct NavReadinessFacts(NavReadiness Tree, NavReadiness Library)
{
    public static NavReadinessFacts Pending => new(NavReadiness.Pending, NavReadiness.Pending);

    /// <summary>The worse of the two (Failed over Pending over Ready).</summary>
    public NavReadiness Overall => NavReadinessRules.Worst(Tree, Library);

    public bool AnyFailed => Tree == NavReadiness.Failed || Library == NavReadiness.Failed;
}

/// <summary>The pure readiness rules: edge state in, readiness out, and the planner's two-state projection of it.</summary>
public static class NavReadinessRules
{
    /// <summary>The readiness a surface renders for an edge (<see cref="EdgeTableBase.Readiness"/>'s answer): Unknown is Pending,
    /// Failed is Failed, a partial or complete list is Ready.</summary>
    public static NavReadiness Of(EdgeState readiness) => readiness switch
    {
        EdgeState.Failed => NavReadiness.Failed,
        EdgeState.Unknown => NavReadiness.Pending,
        _ => NavReadiness.Ready,
    };

    /// <summary>The worse of two: Failed beats Pending beats Ready.</summary>
    public static NavReadiness Worst(NavReadiness a, NavReadiness b)
        => a == NavReadiness.Failed || b == NavReadiness.Failed ? NavReadiness.Failed
         : a == NavReadiness.Pending || b == NavReadiness.Pending ? NavReadiness.Pending
         : NavReadiness.Ready;

    /// <summary>What the row planner reads: Ready, or Pending. A failed source draws as Pending (the skeleton) until a view
    /// draws the failure, so publishing Failed changes no pixel of today's panes.</summary>
    public static SidebarSourceState ToPlanner(NavReadiness readiness)
        => readiness == NavReadiness.Ready ? SidebarSourceState.Ready : SidebarSourceState.Pending;
}

// ── the small value shapes ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One fixed destination the pane offers (Home, Liked Songs, the library pages, Settings) with its live count:
/// null while unknown or when the destination has no count (design D9: never "0" for "not loaded").</summary>
public readonly record struct NavDestination(string RouteKey, int? Count);

/// <summary>The fixed destinations in catalogue order, from the kind counts. PURE.</summary>
public static class NavDestinationRules
{
    public static NavDestination[] Build(in SidebarLibraryCounts counts)
    {
        var items = SidebarCatalogue.ItemsOf(SidebarLayoutId.Classic, SidebarSectionKind.Collections);
        var all = new NavDestination[items.Count + 2];
        all[0] = new NavDestination(SidebarCatalogue.HomeRoute, null);
        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i];
            all[i + 1] = new NavDestination(item, SidebarCatalogue.KindFlagOf(item) != SidebarLibraryKinds.None ? counts.Of(item) : null);
        }
        all[^1] = new NavDestination(SidebarCatalogue.SettingsRoute, null);
        return all;
    }
}

/// <summary>The two feeds the Classic layout can show: Recently played and New releases, resolved (never a nameless row).</summary>
public readonly record struct NavFeeds(IReadOnlyList<SidebarLibraryEntry>? Played, IReadOnlyList<SidebarLibraryEntry>? NewReleases);

/// <summary>The account whose pins are loaded. <see cref="SignedOut"/> means no account file and nowhere to keep a pin.</summary>
public readonly record struct NavAccount(string Key)
{
    public bool SignedOut => SidebarAccountKey.IsSignedOut(Key);
}

/// <summary>Is something playing, as the pane's play sweep reads it: <see cref="Active"/> = a context is loaded.</summary>
public readonly record struct NavPlaying(bool Active, bool Playing);

// ── the verbs ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What a navigation view can ask of the model. Implemented once by <see cref="NavModel"/> (a view holds the one
/// instance, never a fresh lambda per render). Every verb is a decision the view used to own.</summary>
public interface INavVerbs
{
    /// <summary>Go to a route key (<c>"home"</c>, <c>"pl:spotify:playlist:…"</c>), with the display argument the destination wants.</summary>
    void Navigate(string routeKey, string? arg);

    /// <summary>Play an entity as a CONTEXT (the verb a card's play button and a track row's click share).</summary>
    void Play(string uri);

    /// <summary>Write a section's collapsed bit in <paramref name="layout"/>'s overlay (the target state, not a flip). A New
    /// releases section that opens is seen (its badge clears). The disclosure motion is the view's.</summary>
    void ToggleSection(SidebarLayoutId layout, string sectionId, bool collapsed);

    /// <summary>Flip a folder's inline expansion (persisted in the account file, coalesced).</summary>
    void ToggleFolder(string? folderId);

    /// <summary>A folder row's activation. The model's rule is the inline one: it toggles the folder. A mode that drills
    /// instead (Your Library's narrow pane) decides that itself until its session moves into the model.</summary>
    void ActivateFolder(string folderId, string name);

    /// <summary>The ONE create-playlist flow (numbered name, optimistic row), at the top level or inside a folder. An absent
    /// library write seam is never a half-made playlist and never a silent click: it says why.</summary>
    void CreatePlaylist(string? folderId, bool navigate);

    /// <summary>The plain "New folder" verb (the seam asks for a name), at the top level or inside a folder.</summary>
    void CreateFolder(string? parentFolderId);

    /// <summary>Pin through the undo ring (appended, or at <paramref name="slot"/>). Signed out says so and pins nothing.</summary>
    bool Pin(string pinId, SidebarEntryKind kind, string? uri, string? name, int slot);

    /// <summary>Unpin through the undo ring; undo restores it at its former index.</summary>
    bool Unpin(string pinId, string? nameHint);

    /// <summary>Move a pin (recorded, never toasted).</summary>
    void MovePin(int from, int to);
}

// ── the model ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The navigation pane's core model. See the file header. Split: this file (the stored facts and the verbs),
/// <c>NavModel.Binder.cs</c> (the projection and its pump), <c>NavModel.Selection.cs</c> (the selected route, the play
/// state).</summary>
public sealed partial class NavModel : INavVerbs
{
    readonly SidebarProjectionBinder _binder;

    NavDestination[]? _destinations;
    SidebarLibraryCounts _destinationCounts;

    /// <param name="binder">The projection driver; null = a new one over the shell's own logs. A test hands its own so it never
    /// races the process-wide stores.</param>
    public NavModel(SidebarProjectionBinder? binder = null) => _binder = binder ?? new SidebarProjectionBinder();

    // ── destinations ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Home, the library pages (Liked Songs, Albums, Artists, Podcasts, Audiobooks) and Settings, with the kind counts
    /// the binder computed over the FULL projection. Rebuilt only when the counts moved, so a read allocates nothing.</summary>
    public IReadOnlyList<NavDestination> Destinations
    {
        get
        {
            var counts = _binder.Counts;
            if (_destinations is null || counts != _destinationCounts)
            {
                _destinations = NavDestinationRules.Build(in counts);
                _destinationCounts = counts;
            }
            return _destinations;
        }
    }

    // ── layout ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active layout (<c>sidebar.layout.id</c>). A switch remounts the mode.</summary>
    public IReadSignal<SidebarLayoutId> LayoutId => Sidebar.Layout;

    /// <summary>The active layout's resolved document (<c>sidebar.json</c> resolved against the catalogue). PEEKS: a render
    /// that must re-plan calls <see cref="ReadLayout"/>. Cached by the service on (version, layout, density), so the same
    /// instance comes back until something changed.</summary>
    public SidebarLayoutDoc Layout => Sidebar.Doc;

    /// <summary>Bumped on every overlay, density or layout change.</summary>
    public IReadSignal<int> LayoutVersion => Sidebar.LayoutVersion;

    /// <summary>The document, SUBSCRIBING the caller to <see cref="LayoutVersion"/> (the pane's render).</summary>
    public SidebarLayoutDoc ReadLayout()
    {
        _ = Sidebar.LayoutVersion.Value;
        return Sidebar.Doc;
    }

    // ── edit mode, drag peek, account, persistence ──────────────────────────────────────────────────────────────────────

    /// <summary>Edit mode is on. The writers are <c>Sidebar.EnterEdit</c> / <c>ExitEdit</c> (the edit model, C11).</summary>
    public IReadSignal<bool> Editing => Sidebar.Editing;

    /// <summary>A drag is live near a collapsed pane: the column presents expanded for the rest of the drag. TRANSIENT, never
    /// persisted, never a write to the user's collapse. The pane and the shell clip the column off the SAME signal.</summary>
    public IReadSignal<bool> DragPeek => Sidebar.DragPeek;

    /// <summary>THE drag-peek write: the shell clips the column off it, so the pane mirrors its own flag here.</summary>
    public void SetDragPeek(bool on)
    {
        if (Sidebar.DragPeek.Peek() != on) Sidebar.DragPeek.Value = on;
    }

    /// <summary>The account whose pins are loaded ("" = signed out).</summary>
    public NavAccount Account => new(Sidebar.AccountKey);

    /// <summary>The account edge (the pane's scope effect): idempotent and cheap, a market, locale or tier switch keeps the
    /// account and returns at once. Reads <c>Entities.ScopeEpoch</c> first, so the caller re-runs on every scope switch.</summary>
    public void SyncAccount()
    {
        _ = Entities.ScopeEpoch.Value;
        if (Entities.Current is { } scope) Sidebar.EnsureAccount(scope.Key);
    }

    /// <summary>Redaction-safe verdict of the last device / account file write.</summary>
    public IReadSignal<SidebarWriteResult> PersistenceHealth => Sidebar.PersistenceHealth;

    // ── pins and folders (the stores stay on the service; the model is the read surface) ───────────────────────────────

    /// <summary>The live account's pin store: the ordered, persisted AUTHORITY and the offline display cache.</summary>
    public SidebarPinStore Pins => Sidebar.Pins;

    /// <summary>Bumped on every accepted pin mutation.</summary>
    public IReadSignal<int> PinsVersion => Sidebar.PinsVersion;

    public bool IsPinned(string? pinId) => Sidebar.IsPinned(pinId);

    /// <summary>The expanded folder id set (a live read-only view, never a copy).</summary>
    public IReadOnlySet<string> ExpandedFolders => Sidebar.ExpandedFolders;

    public bool IsFolderExpanded(string? folderId) => Sidebar.IsFolderExpanded(folderId);

    /// <summary>Bumped on every expansion change.</summary>
    public IReadSignal<int> FolderVersion => Sidebar.FolderVersion;

    /// <summary>Set a folder's expansion outright (Your Library's drill expands what it enters).</summary>
    public void SetFolderExpanded(string? folderId, bool expanded) => Sidebar.SetFolderExpanded(folderId, expanded);

    /// <summary>A pin's name for its toast: the stored name, else the route's title.</summary>
    public string PinName(string id)
    {
        var pins = Sidebar.Pins;
        int at = pins.IndexOf(id);
        if (at >= 0 && pins[at].Name.Length > 0) return pins[at].Name;
        return SidebarPinId.RouteOf(id) is { } route ? Shell.Dest(Shell.Parse(route)).Title : "";
    }

    // ── verbs ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    public void Navigate(string routeKey, string? arg)
    {
        if (routeKey.Length == 0) return;
        Shell.GoTo(Shell.Parse(routeKey, arg));
    }

    public void Play(string uri)
    {
        if (uri.Length == 0) return;
        var target = EntityUri.Parse(uri);
        if (!target.IsValid) return;
        if (Actions.Services?.Play is { } play) play(target);
        else Shell.OnPlayContext?.Invoke(target);
    }

    public void ToggleSection(SidebarLayoutId layout, string sectionId, bool collapsed)
    {
        Sidebar.Dispatch(new SetSectionCollapsed(layout, sectionId, collapsed));
        if (!collapsed && sectionId == SidebarCatalogue.IdOf(SidebarSectionKind.NewReleases)) Sidebar.MarkNewReleasesSeen();
    }

    public void ToggleFolder(string? folderId) => Sidebar.ToggleFolder(folderId);

    public void ActivateFolder(string folderId, string name)
    {
        _ = name;
        ToggleFolder(folderId);
    }

    public void CreatePlaylist(string? folderId = null, bool navigate = true)
    {
        if (Sidebar.LibraryWrites?.CreatePlaylist is { } create) create(folderId, navigate);
        else RefuseWrite(folderId is null ? "create playlist" : "new playlist in folder");
    }

    public void CreateFolder(string? parentFolderId = null)
        => Sidebar.LibraryWrites?.NewFolderWith?.Invoke(parentFolderId, Array.Empty<RootlistItemRef>());

    public bool Pin(string pinId, SidebarEntryKind kind, string? uri, string? name, int slot = -1)
    {
        if (string.IsNullOrEmpty(pinId) || SidebarPinRules.IsFixedRoute(pinId)) return false;
        if (SidebarAccountKey.IsSignedOut(Sidebar.AccountKey))
        {
            Notify.Say(Loc.Get("sidebar.pin.signedOut"), InfoBarSeverity.Informational);
            return false;
        }
        var pin = new SidebarPin(pinId, kind, uri ?? "", name ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Sidebar.PinRecorded(pin, name is { Length: > 0 } n ? n : PinName(pinId), slot);
    }

    public bool Unpin(string pinId, string? nameHint = null)
    {
        if (string.IsNullOrEmpty(pinId)) return false;
        return Sidebar.UnpinRecorded(pinId, nameHint is { Length: > 0 } n ? n : PinName(pinId));
    }

    public void MovePin(int from, int to) => Sidebar.MovePinRecorded(from, to);

    /// <summary>The "never a silent no-op" arm of every library write a view issues (a "+" click, a create drop, a folder
    /// verb): a log line for us, the refusal's sentence for the user. Static: a click verb has no instance to hand.</summary>
    public static void RefuseWrite(string verb)
    {
        Log.Warn("sidebar", "library write refused: no seam for " + verb);
        string? sentence = SidebarDropRefusalText.LocKey(SidebarDropRefusal.WritesUnavailable) is { Length: > 0 } key ? Loc.Get(key) : null;
        if (sentence is { Length: > 0 })
            Notify.Say(sentence, InfoBarSeverity.Informational, dedupeKey: "sidebar.writes-unavailable");
    }
}
