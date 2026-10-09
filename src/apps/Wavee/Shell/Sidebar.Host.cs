// ── Shell/Sidebar.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the one app-wide sidebar service (layout, pane, pins, folders, the per-account file), the pin store, the first-seen
// stamps, the navigation recency, and the projection binder that drives every rebuild
//
// Role: SHELL
// Owner: J
// Wave: 4
// Budget: 2500 lines
// Spec: ch 26 §9.4 (which restates §2's old 800 as ~2,500 by name)
//
// The imperative half of the sidebar platform: `public static partial class Sidebar` (signals, the pane, the layout's
// ONE mutation path `Dispatch`, the pins, the folders, the boot and the exit tail), the pin store, and the projection
// binder. The file stores live in `Sidebar.Store.cs` (bytes, atomic writes) and `Sidebar.Accounts.cs` (which account's
// file is loaded); the pure feed rules live in `Sidebar.Feeds.cs`.
//
// THREE CONTRACTS.
//
// 1. **`Dispatch` is the one layout mutation path.** Reduce → if `Changed`: bump `LayoutVersion`, commit. A REJECTED op
//    changes nothing and returns its reason. Every other mutator is sugar over it or over a named signal.
// 2. **The files are user data.** Atomic write-then-rename with an fsync and ONE rotated `.bak`; a corrupt file is set
//    aside, never rewritten in place. Pins and folders live in the ACCOUNT file, so a swap of account never mixes them.
// 3. **C1/C9.** Everything here runs on the UI thread inside one drain, or Posts to it. The UI thread never blocks on
//    a file: the commit is coalesced onto a timer and the write happens off-thread.
//
// No environment-variable switches (CLAUDE.md): every diagnostic is an always-on `Log` event.

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── SIDEBAR SERVICE — the layout, the pane, the pins, the folders, the persistence health ───────────────────────────────
//
// UI thread only, unsynchronized — the same discipline as every other static service (`Playback`, `Entities`): the only
// off-thread work is the file store's pool write, whose completion is coalesced and republished through `ToUi`.

public static partial class Sidebar
{
    // ── the UI-thread seam ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller onto the UI thread — the `Playback.ToUi` / `Store.Post` contract: `App.cs` sets it to
    /// `AppHost.Post`, a test leaves it, and the default runs the action inline.</summary>
    public static Action<Action> ToUi { get; set; } = static a => a();

    // ── boot ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static string s_profileDir = "";
    static string? s_profileDirOverride;
    static SidebarFileStore? s_deviceFile;
    static readonly Signal<SidebarWriteResult> s_persistenceHealth = new(SidebarWriteResult.Healthy);
    static readonly object s_persistenceGate = new();
    static SidebarWriteResult s_pendingPersistenceHealth;
    static bool s_hasPendingPersistenceHealth;

    /// <summary>Point the service at another profile folder — the <c>Platform.UseSettings</c> precedent, for a test (a temp
    /// folder) or a host with its own profile. Call it BEFORE <see cref="Boot"/>: the migration's v2 read, the device file
    /// and every account file then live under <paramref name="dir"/>, and the real
    /// <c>%LOCALAPPDATA%\Wavee\WaveeMusic</c> is never read or written. Lazy by design: resolving the default reads
    /// <c>Platform.LocalFolder</c>, whose getter creates the profile directory.</summary>
    public static void UseProfileDir(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        s_profileDirOverride = dir;
    }

    /// <summary>Boot (App.cs, after Entities.Boot): the one-time v2 migration, then the settings, the device file and the
    /// live account. Its partners are <see cref="Activate"/> and <see cref="Shutdown"/>. A second Boot (tests) reloads
    /// everything, the account included.</summary>
    public static void Boot()
    {
        s_profileDir = s_profileDirOverride ?? Path.Combine(Platform.LocalFolder, "WaveeMusic");
        SidebarMigrationHost.RunIfNeeded(Platform.Settings, s_profileDir, Entities.Current.Key);

        var s = Platform.Settings;
        Layout.Value = s.Get(Platform.Keys.SidebarLayoutId) == 1 ? SidebarLayoutId.Library : SidebarLayoutId.Classic;
        Density.Value = s.Get(Platform.Keys.SidebarPaneDensity) == 1 ? SidebarDensity.Compact : SidebarDensity.Default;
        Width.Value = SidebarPaneBounds.Clamp(s.Get(Platform.Keys.SidebarPaneWidth));
        UserCollapsed.Value = s.Get(Platform.Keys.SidebarPaneUserCollapsed);
        Seam.Value = UserCollapsed.Peek() ? SidebarRowGeometry.RailWidth : Width.Peek();
        PresentedWidth.Value = Seam.Peek();

        s_deviceFile = new SidebarFileStore(SidebarStoreV3.PathUnder(s_profileDir)) { WriteCompleted = OnWriteCompleted };
        LoadDeviceFile();
        LibraryFilter.Value = SidebarLibraryFilters.Effective(s.Get(Platform.Keys.SidebarLibraryFilter), Doc.Library.HiddenKinds);
        V3Qualifier.Value = s.Get(Platform.Keys.V3Qualifier);   // the P3-P4 V3 chrome bridge (§P3.12); deleted in P5
        SyncV3Mirrors();

        s_accountLoaded = false;                                          // a re-Boot reloads the account even for the same key
        s_accountFile = null;
        EnsureAccount(Entities.Current.Key);
        Pins.OnChanged = CommitAccount;                                   // every pin mutation is a commit point
        Log.Info("sidebar", "boot layout=" + Layout.Peek() + " fault=" + LayoutFileFault);
    }

    static void LoadDeviceFile()
    {
        LayoutFileFault = false;
        var file = s_deviceFile!;
        var dropped = new List<string>();
        var read = file.Read();
        if (read.Outcome == SidebarFileReadOutcome.Ok && SidebarStoreV3.TryParse(read.Bytes!, out var state, dropped)) s_state = state;
        else if (read.Outcome == SidebarFileReadOutcome.Missing) s_state = SidebarLayoutState.Default;
        else
        {
            var bak = file.ReadBak();
            if (bak.Outcome == SidebarFileReadOutcome.Ok && SidebarStoreV3.TryParse(bak.Bytes!, out var recovered, dropped))
                s_state = recovered;
            else
            {
                // Corrupt (design corner case): catalogue defaults, the bytes set aside, one InfoBar in Settings › Sidebar.
                // Pins are unaffected — they live in the account file.
                file.MarkCorrupt();
                s_state = SidebarLayoutState.Default;
                LayoutFileFault = true;
            }
        }
        for (int i = 0; i < dropped.Count; i++) Log.Info("sidebar", "layout.unknown_section id=" + dropped[i]);
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
    }

    /// <summary>The device file could not be read at boot (Settings › Sidebar shows "Your sidebar layout couldn't be read · Reset").</summary>
    public static bool LayoutFileFault { get; private set; }

    /// <summary>Attach the app's UI-thread dispatcher. Idempotent; a later dispatcher replaces the earlier one after a
    /// shell remount. Async file completion never touches a signal directly on the pool: `OnWriteCompleted` coalesces
    /// the verdict and this publishes it through <see cref="ToUi"/>.</summary>
    public static void Activate(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        bool hasPending;
        lock (s_persistenceGate)
        {
            ToUi = post;
            hasPending = s_hasPendingPersistenceHealth;
        }
        if (hasPending) post(PublishPendingPersistenceHealth);
    }

    /// <summary>Reactive, redaction-safe persistence health. Completed writes update it only through <see cref="Activate"/>'s
    /// dispatcher so every signal write stays UI-thread affine.</summary>
    public static IReadSignal<SidebarWriteResult> PersistenceHealth => s_persistenceHealth;

    // ── the layout ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active layout (<c>sidebar.layout.id</c>). A switch remounts the mode (fresh hooks, fresh scroll).</summary>
    public static readonly Signal<SidebarLayoutId> Layout = new(SidebarLayoutId.Classic);

    /// <summary>Entity-row density (<c>sidebar.pane.density</c>), global.</summary>
    public static readonly Signal<SidebarDensity> Density = new(SidebarDensity.Default);

    /// <summary>Your Library's active chip (<c>sidebar.library.filter</c>).</summary>
    public static readonly Signal<SidebarLibraryFilter> LibraryFilter = new(SidebarLibraryFilter.None);

    /// <summary>Your Library's search text. SESSION-ONLY: never persisted; cleared on a layout or account switch.</summary>
    public static readonly Signal<string> LibrarySearch = new("");

    static SidebarLayoutState s_state = SidebarLayoutState.Default;
    static readonly Signal<int> s_layoutVersion = new(0);
    static SidebarLayoutDoc? s_doc;
    static int s_docVersion = -1;
    static SidebarLayoutId s_docLayout;
    static SidebarDensity s_docDensity;

    /// <summary>Both overlays (the undo ring and the Outline read them).</summary>
    public static SidebarLayoutState State => s_state;

    /// <summary>Bumped on every overlay, density or layout change — the plan's and the binder's edge.</summary>
    public static IReadSignal<int> LayoutVersion => s_layoutVersion;

    /// <summary>The active layout's resolved document. PEEKS: a render that must re-plan reads <see cref="LayoutVersion"/>.
    /// Cached on (version, layout, density), so the same instance comes back until something changed — the pane's
    /// publish reference test depends on it.</summary>
    public static SidebarLayoutDoc Doc
    {
        get
        {
            int v = s_layoutVersion.Peek();
            var layout = Layout.Peek();
            var density = Density.Peek();
            if (s_doc is null || v != s_docVersion || layout != s_docLayout || density != s_docDensity)
            {
                s_doc = SidebarLayoutRules.Resolve(s_state, layout, density);
                s_docVersion = v;
                s_docLayout = layout;
                s_docDensity = density;
            }
            return s_doc;
        }
    }

    /// <summary>THE ONE MUTATION PATH (design A.3): rules → signal → coalesced write. A refused op changes nothing and
    /// returns why. (P4 adds the undo push.)</summary>
    public static SidebarOpReject Dispatch(SidebarOp op)
    {
        var r = SidebarLayoutRules.Apply(s_state, op, PinnedLocked());
        if (!r.Changed)
        {
            if (r.Reject is not (SidebarOpReject.None or SidebarOpReject.NoChange))
                Log.Info("sidebar", "op.refused op=" + op.GetType().Name + " reason=" + r.Reject);
            return r.Reject;
        }
        s_state = r.State;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        if (op is SetLibrarySort or SetLibraryView) SyncV3Mirrors();
        CommitLayout();
        return SidebarOpReject.None;
    }

    /// <summary>Pinned holds a route or module pin and may not be hidden.</summary>
    public static bool PinnedLocked()
    {
        for (int i = 0; i < Pins.Count; i++) if (Pins[i].Kind == SidebarEntryKind.AppRoute) return true;
        return false;
    }

    /// <summary>Switch the layout (Settings, the pane menu, the palette). Each layout keeps its own overlay.</summary>
    public static void SwitchLayout(SidebarLayoutId next)
    {
        if (Layout.Peek() == next || Editing.Peek()) return;
        LibrarySearch.SetIfChanged("");
        LibrarySearchOpen.SetIfChanged(false);
        Layout.Value = next;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Platform.Settings.Set(Platform.Keys.SidebarLayoutId, (int)next);
        Log.Info("sidebar", "layout.changed to=" + next);
    }

    public static void SetDensity(SidebarDensity density)
    {
        if (Density.Peek() == density) return;
        Density.Value = density;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Platform.Settings.Set(Platform.Keys.SidebarPaneDensity, (int)density);
        SyncV3Mirrors();
    }

    public static void SetLibraryFilter(SidebarLibraryFilter filter)
    {
        LibraryFilter.SetIfChanged(filter);
        Platform.Settings.Set(Platform.Keys.SidebarLibraryFilter, (int)filter);
        SyncV3Mirrors();
    }

    /// <summary>"Reset everything" (design C.3): both layouts, density and width back to their defaults; pins untouched.</summary>
    public static void ResetEverything()
    {
        Dispatch(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic)));
        Dispatch(new ReplaceOverlay(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library)));
        SetDensity(SidebarDensity.Default);
        SetExpandedWidth(SidebarPaneBounds.DefaultWidth);
    }

    static void CommitLayout() => s_deviceFile?.Commit(SidebarStoreV3.Serialize(s_state));

    // ── pane state (the ONE pane, both layouts) ─────────────────────────────────────────────────────────────────────────
    // Two stored facts, each with one owner: Width (the EXPANDED width, one value for both layouts) and UserCollapsed
    // (persisted as `sidebar.pane.userCollapsed`). Band, Mode and OverlayOpen are the window's and the shell's; Seam and
    // PresentedWidth are derived/transient. The pure rules live in SidebarResizeRules and SidebarPaneModeRules; every
    // writer below goes through Apply.

    /// <summary>The pane EXPANDED width, the user preference. The shell BINDS this signal. Never written by the window.</summary>
    public static readonly Signal<float> Width = new(SidebarPaneBounds.DefaultWidth);

    /// <summary>The user's collapse (the 48 rail). Written ONLY by a toggle / seam / double-click in the Wide band while
    /// not editing (<see cref="SidebarPaneModeRules.WritesUserCollapsed"/>).</summary>
    public static readonly Signal<bool> UserCollapsed = new(false);

    /// <summary>The window band — written ONLY by the shell's band effect.</summary>
    public static readonly Signal<SidebarWindowBand> Band = new(SidebarWindowBand.Wide);

    /// <summary>What the frame presents — written ONLY by the shell's presentation effect, beside <see cref="PresentedWidth"/>.</summary>
    public static readonly Signal<SidebarPaneMode> Mode = new(SidebarPaneMode.Expanded);

    /// <summary>The overlay pane (the drawer in Tiny, the pane over a forced rail in Narrow) is open. Session-only.</summary>
    public static readonly Signal<bool> OverlayOpen = new(false);

    /// <summary>Edit mode (P4). False until then; the title bar, the overlay guards and the seam already read it.</summary>
    public static readonly Signal<bool> Editing = new(false);

    /// <summary>The splitter raw cell: 1:1 with the pointer during a drag, equal to <see cref="PresentedWidth"/> at rest.</summary>
    public static readonly Signal<float> Seam = new(SidebarPaneBounds.DefaultWidth);

    /// <summary>What the column lays out at. Written ONLY by the shell presentation effect
    /// (<c>SidebarPaneModeRules.PresentedWidth</c> / <c>SidebarResizeRules.Track</c>).</summary>
    public static readonly Signal<float> PresentedWidth = new(SidebarPaneBounds.DefaultWidth);

    /// <summary>DRAG PEEK: TRANSIENT, never persisted, never a write to <see cref="UserCollapsed"/>. A rail sidebar is
    /// PRESENTED expanded for the rest of one drag once the pointer dwells on the rail. Two readers must agree: the
    /// pane decides its own presentation, and the shell clips the column width off the SAME signal.</summary>
    public static readonly Signal<bool> DragPeek = new(false);

    /// <summary>The two stored facts as the pure rules input.</summary>
    public static SidebarResizeRules.State ResizeState() => new(UserCollapsed.Peek(), Width.Peek());

    /// <summary>Drag end: resolve the seam into a settle, write the facts, persist, park the seam on the target.</summary>
    public static void CommitSeam() => Apply(SidebarResizeRules.Resolve(Seam.Peek(), ResizeState(), Editing.Peek()));

    /// <summary>The title-bar toggle, the seam's double-click and Enter: in the Wide band it flips the user's collapse;
    /// in Narrow/Tiny it opens or closes the overlay pane and never writes the collapse. Disabled while editing.</summary>
    public static void TogglePane()
    {
        if (Editing.Peek()) return;
        if (SidebarPaneModeRules.HasOverlay(Band.Peek())) { OverlayOpen.Value = !OverlayOpen.Peek(); return; }
        Apply(SidebarResizeRules.Toggle(ResizeState()));
    }

    public static void SetUserCollapsed(bool collapsed) { if (UserCollapsed.Peek() != collapsed) TogglePane(); }

    /// <summary>Show the FULL pane from wherever it is — the rail's search tile (design P.2a: "its search tile expands the
    /// pane with the search box open"). Narrow / Tiny: open the overlay pane (a forced rail has <c>UserCollapsed</c> false,
    /// so <see cref="SetUserCollapsed"/> would do nothing there). Wide: clear the user's collapse. Never closes anything;
    /// a no-op while editing (Edit already presents the pane).</summary>
    public static void OpenPane()
    {
        if (Editing.Peek()) return;
        if (SidebarPaneModeRules.HasOverlay(Band.Peek())) { OverlayOpen.SetIfChanged(true); return; }
        if (UserCollapsed.Peek()) Apply(SidebarResizeRules.Toggle(ResizeState()));
    }

    /// <summary>Your Library's search box is open. SESSION-ONLY and SHARED by every Library mount — the docked pane (a rail
    /// in Narrow) and the overlay pane are separate <c>PaneView</c> mounts with separate sessions, so a per-session flag
    /// set by the rail's tile would never open the overlay's box. Cleared with <c>LibrarySearch</c> on a layout or account
    /// switch.</summary>
    public static readonly Signal<bool> LibrarySearchOpen = new(false);

    /// <summary>Keyboard ←/→ on the seam (<paramref name="large"/> = Shift).</summary>
    public static void StepSeam(int direction, bool large)
        => Apply(SidebarResizeRules.Step(ResizeState(), direction, large, Editing.Peek()));

    /// <summary>Set the expanded width outright (Home/End, Settings): clamped, expanded, persisted.</summary>
    public static void SetExpandedWidth(float width)
    {
        float w = Math.Clamp(float.IsFinite(width) ? width : Width.Peek(), SidebarResizeRules.ExpandedMinW, SidebarResizeRules.ExpandedMaxW);
        Apply(new SidebarResizeRules.Settle(false, w, w));
    }

    /// <summary>"Reset width": the default width, expanded.</summary>
    public static void ResetWidth() => SetExpandedWidth(SidebarPaneBounds.DefaultWidth);

    static void Apply(in SidebarResizeRules.Settle s)
    {
        Width.SetIfChanged(s.ExpandedWidth);
        if (SidebarPaneModeRules.WritesUserCollapsed(Band.Peek(), Editing.Peek())) UserCollapsed.SetIfChanged(s.UserCollapsed);
        Seam.SetIfChanged(s.TargetWidth);
        Platform.Settings.Set(Platform.Keys.SidebarPaneWidth, s.ExpandedWidth);
        Platform.Settings.Set(Platform.Keys.SidebarPaneUserCollapsed, UserCollapsed.Peek());
    }

    // ── P3-P4 BRIDGE: Library V3's chrome speaks its old int codes until P5 replaces it. The model is the truth; these
    //    mirror it (SyncV3Mirrors) and their setters write the model. Never persisted. Delete in P5. ──────────────────────

    public static readonly Signal<int> V3Filter = new(0);
    public static readonly Signal<int> V3Qualifier = new(0);
    public static readonly Signal<int> V3Sort = new(0);
    public static readonly Signal<bool> V3Desc = new(false);
    public static readonly Signal<int> V3View = new(1);
    public static readonly Signal<int> V3GridSize = new(1);
    public static Signal<string> V3Search => LibrarySearch;

    static void SyncV3Mirrors()
    {
        var lib = Doc.Library;
        V3Filter.SetIfChanged(LibraryFilter.Peek() switch
        {
            SidebarLibraryFilter.Playlists => (int)SidebarV3Filter.Playlists,
            SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks => (int)SidebarV3Filter.Podcasts,
            SidebarLibraryFilter.Albums => (int)SidebarV3Filter.Albums,
            SidebarLibraryFilter.Artists => (int)SidebarV3Filter.Artists,
            _ => (int)SidebarV3Filter.All,
        });
        V3Sort.SetIfChanged((int)lib.Sort);
        V3Desc.SetIfChanged(lib.Descending);
        bool compact = Density.Peek() == SidebarDensity.Compact;
        V3View.SetIfChanged(lib.View == SidebarLibraryView.Grid
            ? (compact ? (int)SidebarV3View.CompactGrid : (int)SidebarV3View.Grid)
            : (compact ? (int)SidebarV3View.CompactList : (int)SidebarV3View.List));
    }

    public static void SetV3Filter(int v) => SetLibraryFilter(v switch
    {
        (int)SidebarV3Filter.Playlists => SidebarLibraryFilter.Playlists,
        (int)SidebarV3Filter.Podcasts => SidebarLibraryFilter.Podcasts,
        (int)SidebarV3Filter.Albums => SidebarLibraryFilter.Albums,
        (int)SidebarV3Filter.Artists => SidebarLibraryFilter.Artists,
        _ => SidebarLibraryFilter.None,
    });
    public static void SetV3Qualifier(int v) { V3Qualifier.SetIfChanged(v); Platform.Settings.Set(Platform.Keys.V3Qualifier, v); }
    public static void SetV3Sort(int sort, bool desc)
        => Dispatch(new SetLibrarySort((uint)sort <= 4 ? (SidebarLibrarySort)sort : SidebarLibrarySort.Recents, desc));
    public static void SetV3View(int view)
    {
        Dispatch(new SetLibraryView(view >= (int)SidebarV3View.CompactGrid ? SidebarLibraryView.Grid : SidebarLibraryView.List));
        SetDensity(view is (int)SidebarV3View.CompactList or (int)SidebarV3View.CompactGrid ? SidebarDensity.Compact : SidebarDensity.Default);
    }
    public static void SetV3GridSize(int size) => V3GridSize.SetIfChanged(size);

    // ── folder expansion (an unbounded id set ⇒ the account file, not settings) ─────────────────────────────────────────

    static readonly HashSet<string> s_expandedFolders = new(StringComparer.Ordinal);
    static readonly Signal<int> s_folderVersion = new(0);
    static bool s_commitPending;                                        // a coalesced account write waiting for Flush
    static bool s_pinNamesDirty;                                        // a TouchPin refresh waiting to ride the next commit
    static SidebarFirstSeenDto[]? s_firstSeen;                          // the playlist added-at PROXY wire array (F.7.5)

    public static bool IsFolderExpanded(string? folderId) => folderId is not null && s_expandedFolders.Contains(folderId);

    /// <summary>The expanded folder id SET, for the row planner (which needs the set itself, not the predicate). A
    /// live read-only view — never a copy, so a rebuild allocates nothing for it.</summary>
    public static IReadOnlySet<string> ExpandedFolders => s_expandedFolders;

    public static IReadSignal<int> FolderVersion => s_folderVersion;

    public static void SetFolderExpanded(string? folderId, bool expanded)
    {
        if (string.IsNullOrEmpty(folderId)) return;
        bool changed = expanded ? s_expandedFolders.Add(folderId) : s_expandedFolders.Remove(folderId);
        if (!changed) return;
        s_folderVersion.Value = s_folderVersion.Peek() + 1;
        // COALESCED, not synchronous — the very frame the expansion has to plan, publish, realize and arm on must not
        // also snapshot + serialize the account file. The renderer drains this on the next frame.
        s_commitPending = true;
    }

    public static void ToggleFolder(string? folderId) => SetFolderExpanded(folderId, !IsFolderExpanded(folderId));

    /// <summary>Issue a write a coalescing mutator deferred. Idempotent, and a no-op when nothing is pending.</summary>
    public static void FlushPendingCommit()
    {
        if (!s_commitPending) return;
        CommitAccount();
    }

    /// <summary>The projection publishes new first-observation stamps here after a rebuild that produced any (commit
    /// point, at most one commit per rebuild). Passing null leaves the stored map untouched.</summary>
    public static void PublishFirstSeen(SidebarFirstSeenDto[]? stamps)
    {
        if (stamps is null) return;
        s_firstSeen = stamps;
        CommitAccount();
    }

    /// <summary>The stored first-seen stamps, for the projection to seed its map from at build time.</summary>
    public static SidebarFirstSeenDto[]? FirstSeen => s_firstSeen;

    // ── pins (per ACCOUNT: the list loaded for the live account, see Sidebar.Accounts.cs) ───────────────────────────────

    /// <summary>The one pin list of the live account. Unlimited — no cap, no eviction.</summary>
    public static readonly SidebarPinStore Pins = new();

    public static IReadSignal<int> PinsVersion => Pins.Version;
    public static bool IsPinned(string? pinId) => Pins.IsPinned(pinId);

    /// <summary>Append a pin. False when already pinned (idempotent — the menu shows Unpin in that state).</summary>
    public static bool Pin(SidebarPin pin) => Pins.Pin(pin);

    /// <summary>Remove by id. Returns the removed index (for the undo toast) or -1 when absent.</summary>
    public static int Unpin(string? pinId) => Pins.Unpin(pinId);

    /// <summary>Undo path for <see cref="Unpin"/>: reinsert at its former index (clamped).</summary>
    public static void InsertPin(SidebarPin pin, int index) => Pins.Insert(pin, index);

    public static void MovePin(int fromIndex, int toIndex) => Pins.Move(fromIndex, toIndex);

    /// <summary>Refresh a pin's cached display name from live library data. No-op when unchanged; coalesced into the
    /// next commit — never commits alone. Called by <c>ResolvePins</c> on both the index-hit row and a freshly
    /// hydrated unlisted pin, never by rows directly — going through this (rather than <c>Pins.Touch</c> straight)
    /// is what makes a touched name actually persist.</summary>
    public static void TouchPin(string? pinId, string? name)
    {
        if (Pins.Touch(pinId, name)) s_pinNamesDirty = true;
    }

    // ── the entry projection cell (owned here; not this section — see SidebarEntries) ───────────────────────────────────

    /// <summary>The unified projection Your Library reads — one projection, shared by the docked pane and the drawer.
    /// Rebuilt by <see cref="Binder"/> through <c>SidebarProjection.Build</c> + <c>Entries.Publish</c>.</summary>
    public static readonly SidebarEntries Entries = new();

    /// <summary>The ONE driver of <see cref="Entries"/>, set right after both exist. Null in a headless/unit context,
    /// where <see cref="Entries"/> simply stays empty.</summary>
    public static SidebarProjectionBinder? Binder { get; internal set; }

    // ── persistence health ──────────────────────────────────────────────────────────────────────────────────────────────

    static void OnWriteCompleted(SidebarWriteResult result)
    {
        lock (s_persistenceGate)
        {
            s_pendingPersistenceHealth = result;
            s_hasPendingPersistenceHealth = true;
        }
        ToUi(PublishPendingPersistenceHealth);
    }

    static void PublishPendingPersistenceHealth()
    {
        SidebarWriteResult next;
        lock (s_persistenceGate)
        {
            if (!s_hasPendingPersistenceHealth) return;
            next = s_pendingPersistenceHealth;
            s_hasPendingPersistenceHealth = false;
        }
        s_persistenceHealth.SetIfChanged(next);
    }

    // ── the exit tail ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Flush, sync-write and wait (bounded) for every store: the device file and the live account file. THE EXIT
    /// TAIL (G-176): the stores coalesce every commit behind a timer, so without this the last pin, drag or folder
    /// expansion dies with the process. The completion edge is detached first: the UI loop is gone, and a health publish
    /// posted into it would be a signal write with no thread to run on. Returns false when a write did not land inside
    /// <paramref name="timeoutMs"/> (logged). Call once, after <c>Shell.Run</c>'s loop ends and before
    /// <c>Platform.Shutdown</c> (the log must still be alive).</summary>
    public static bool Shutdown(int timeoutMs = 2000)
    {
        var device = s_deviceFile;
        var account = s_accountFile;
        if (device is not null) device.WriteCompleted = null;
        if (account is not null) account.WriteCompleted = null;
        Flush();
        CommitLayout();
        device?.FlushNow();
        account?.FlushNow();
        bool deviceLanded = device?.WaitForWrites(timeoutMs) ?? true;
        bool accountLanded = account?.WaitForWrites(timeoutMs) ?? true;
        if (!deviceLanded || !accountLanded) Log.Warn("sidebar", "sidebar.shutdown_flush_timeout ms=" + timeoutMs);
        return deviceLanded && accountLanded;
    }

    /// <summary>Issue any coalesced account write NOW (the shutdown tail). Never blocks on the pool.
    /// Also the one path that flushes a name-cache refresh, which by contract never commits on its own.</summary>
    public static void Flush()
    {
        if (!s_pinNamesDirty && !s_commitPending) return;
        s_pinNamesDirty = false;
        CommitAccount();
    }
}

/// <summary>The unified, redaction-safe persistence fault vocabulary Settings › Sidebar and PersistenceHealth read. Append only.</summary>
public enum SidebarPersistenceFault : byte
{
    None = 0,
    Corrupt = 1,
    TooNew = 2,
    Unreadable = 3,
    IoFailure = 4,
    DocumentTooLarge = 6,
}

/// <summary>One completed write verdict. <c>SafeDetail</c> is suitable for normal UI: it never carries a local path, a
/// user title, an entity uri, search text, extension configuration, or an exception message.</summary>
public readonly record struct SidebarWriteResult(
    bool Success,
    SidebarPersistenceFault Fault,
    int Bytes,
    long ElapsedMs,
    string? SafeDetail)
{
    public static SidebarWriteResult Healthy => new(true, SidebarPersistenceFault.None, 0, 0, null);
}


// ── PIN STORE: the one ordered, persisted pin list ──────────────────────────────────────────────────────────────────
// The live ACCOUNT's pins (loaded by Sidebar.Accounts.cs), shared by both layouts — unlimited, no cap, no eviction.
// Identity is the pin Id (Ordinal), the stable scheme in SidebarPinId which for every navigable kind IS the nav route
// key, so a pin survives a library refresh, a rename and an offline launch; Name/Uri are only a display cache refreshed
// through Touch.
//
// In 0.3, pins are ALSO an edge (User.Me → LibraryEdgeKind.Pins). This store stays the ordered, persisted AUTHORITY and
// the OFFLINE DISPLAY CACHE — a pin must paint before any fetch — while the edge is the live server membership; whatever
// syncs the two (owned elsewhere) reads the edge and calls ApplyRemote/Pin/Unpin here, never the other way round.
//
// Implements IReadOnlyList<T> so a caller can index/enumerate directly; GetEnumerator hands back a STRUCT enumerator so
// the pinned-section render path allocates nothing per frame. THREADING: UI thread only, unsynchronized.

public sealed class SidebarPinStore : IReadOnlyList<SidebarPin>
{
    readonly List<SidebarPin> _items = new();
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);   // id → position in _items
    readonly FluentGpu.Signals.Signal<int> _version = new(0);

    /// <summary>Raised after every accepted mutation, so the owner can persist.</summary>
    public Action? OnChanged;

    /// <summary>Raised for a USER-INTENT membership change only (<see cref="Pin"/>/<see cref="Insert"/>/<see cref="Unpin"/>).
    /// Never by <see cref="ApplyRemote"/>, <see cref="LoadFrom"/>, <see cref="Move"/> or <see cref="Touch"/> — that
    /// asymmetry is what keeps a server-originated change from echoing back as a write. The bool is the target state:
    /// true = pinned (Pin/Insert), false = unpinned (Unpin).</summary>
    public Action<SidebarPin, bool>? OnLocalPinChanged;

    /// <summary>Bumped on every accepted mutation — the render dep for every Pinned section and rail band.</summary>
    public FluentGpu.Signals.IReadSignal<int> Version => _version;

    /// <summary>The ordered pin list. This IS the render order of every Pinned section, and the leading band of the
    /// entry projection (pins sort before everything else in every sort mode).</summary>
    public IReadOnlyList<SidebarPin> Items => this;

    public int Count => _items.Count;
    public SidebarPin this[int i] => _items[i];

    public bool IsPinned(string? pinId) => IndexOf(pinId) >= 0;

    /// <summary>Position of a pin, or -1. Ordinal identity, plus the raw-uri alias a card drop used to persist
    /// (<c>spotify:playlist:…</c> vs <c>pl:spotify:playlist:…</c>) so a menu looking up the canonical id still finds it.</summary>
    public int IndexOf(string? pinId)
    {
        if (string.IsNullOrEmpty(pinId)) return -1;
        if (_index.TryGetValue(pinId, out int i)) return i;
        string? canon = SidebarPinId.Canonical(pinId);
        if (canon is not null && _index.TryGetValue(canon, out i)) return i;
        string alias = SidebarPinId.LegacyUriAlias(canon ?? pinId);
        return alias.Length > 0 && _index.TryGetValue(alias, out i) ? i : -1;
    }

    /// <summary>Append a pin. Returns false when already pinned (idempotent — the menu shows Unpin in that state) and
    /// keeps the original position, so a double invoke can never reorder the list. UNLIMITED.</summary>
    public bool Pin(SidebarPin pin)
    {
        var stored = Canonicalize(pin);
        if (SidebarPinRules.IsFixedRoute(stored.Id)) return false;
        if (string.IsNullOrEmpty(stored.Id) || IndexOf(stored.Id) >= 0) return false;
        _index[stored.Id] = _items.Count;
        _items.Add(stored);
        Bump();
        OnLocalPinChanged?.Invoke(stored, true);
        return true;
    }

    /// <summary>Insert at a position (the undo path for <see cref="Unpin"/>: restore at the FORMER index). The index is
    /// CLAMPED to <c>[0, Count]</c> rather than throwing. Returns false when already pinned.</summary>
    public bool Insert(SidebarPin pin, int index)
    {
        var stored = Canonicalize(pin);
        if (SidebarPinRules.IsFixedRoute(stored.Id)) return false;
        if (string.IsNullOrEmpty(stored.Id) || IndexOf(stored.Id) >= 0) return false;
        int at = index < 0 ? 0 : index > _items.Count ? _items.Count : index;
        _items.Insert(at, stored);
        Reindex(at);
        Bump();
        OnLocalPinChanged?.Invoke(stored, true);
        return true;
    }

    /// <summary>Remove by id. Returns the index it occupied (for an undo toast) or -1 when absent.</summary>
    public int Unpin(string? pinId)
    {
        int at = IndexOf(pinId);
        if (at < 0) return -1;
        var removed = _items[at];
        _items.RemoveAt(at);
        _index.Remove(removed.Id);
        Reindex(at);
        Bump();
        OnLocalPinChanged?.Invoke(removed, false);
        return at;
    }

    /// <summary>Reorder within the list (a drag/keyboard drop). Both indices are clamped; <c>to == Count</c> means "move
    /// to the end". A no-op move neither bumps the version nor persists.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        int n = _items.Count;
        if (n < 2 || (uint)fromIndex >= (uint)n) return;
        int to = toIndex < 0 ? 0 : toIndex >= n ? n - 1 : toIndex;
        if (to == fromIndex) return;
        var moved = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(to, moved);
        Reindex(Math.Min(fromIndex, to));
        Bump();
    }

    /// <summary>Refresh a pin's cached display name (a renamed playlist) from live library data. Returns true when it
    /// actually changed. Deliberately does NOT bump the version or raise <see cref="OnChanged"/>: a cache refresh must
    /// never commit on its own and must never invalidate a render mid-projection.</summary>
    public bool Touch(string? pinId, string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        int at = IndexOf(pinId);
        if (at < 0) return false;
        var cur = _items[at];
        if (string.Equals(cur.Name, name, StringComparison.Ordinal)) return false;
        _items[at] = cur with { Name = name };
        return true;
    }

    /// <summary>Replace the whole list from the loaded document (startup only). Skips null/empty and duplicate ids so a
    /// hand-edited file can never produce two rows with one identity. Silent — no <see cref="OnChanged"/>.</summary>
    public void LoadFrom(IReadOnlyList<SidebarPin>? pins)
    {
        _items.Clear();
        _index.Clear();
        if (pins is not null)
            for (int i = 0; i < pins.Count; i++)
            {
                var p = Canonicalize(pins[i]);
                if (string.IsNullOrEmpty(p.Id) || SidebarPinRules.IsFixedRoute(p.Id) || _index.ContainsKey(p.Id)) continue;
                _index[p.Id] = _items.Count;
                _items.Add(p);
            }
        _version.Value = _version.Peek() + 1;
    }

    /// <summary>Converge the SYNCABLE pins onto the server's membership (the Pins edge) without touching order,
    /// local-only pins, or the local-change event. <paramref name="serverPins"/> is the server set already mapped to pin
    /// ids (+ display cache); <paramref name="isSyncable"/> gates which local pins are eligible for removal at all. Adds
    /// are APPENDED in the given order (caller sorts oldest-first); removals only happen when
    /// <paramref name="removeMissing"/> is true — gated by the caller on "the server set has converged once". Returns
    /// true when anything changed; commits (<see cref="OnChanged"/>) at most once, and never raises
    /// <see cref="OnLocalPinChanged"/> — this is a server-originated change.</summary>
    public bool ApplyRemote(IReadOnlyList<SidebarPin> serverPins, Func<string, bool> isSyncable, bool removeMissing)
    {
        bool changed = false;
        var keep = new HashSet<string>(serverPins.Count, StringComparer.Ordinal);
        for (int i = 0; i < serverPins.Count; i++)
        {
            var p = Canonicalize(serverPins[i]);
            if (string.IsNullOrEmpty(p.Id) || SidebarPinRules.IsFixedRoute(p.Id)) continue;
            keep.Add(p.Id);
            if (IndexOf(p.Id) >= 0) continue;
            _index[p.Id] = _items.Count;
            _items.Add(p);
            changed = true;
        }
        if (removeMissing)
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var id = _items[i].Id;
                if (!isSyncable(id) || keep.Contains(id)) continue;
                _items.RemoveAt(i);
                _index.Remove(id);
                Reindex(i);
                changed = true;
            }
        if (changed) Bump();          // version + OnChanged (persist) — deliberately NOT OnLocalPinChanged
        return changed;
    }

    static SidebarPin Canonicalize(SidebarPin pin)
    {
        string? id = SidebarPinId.Canonical(pin.Id);
        if (id is null || string.Equals(id, pin.Id, StringComparison.Ordinal)) return pin;
        string uri = pin.Uri.Length > 0 ? pin.Uri : SidebarPinId.UriOf(id);
        return pin with { Id = id, Uri = uri };
    }

    void Reindex(int from)
    {
        for (int i = from; i < _items.Count; i++) _index[_items[i].Id] = i;
    }

    void Bump()
    {
        _version.Value = _version.Peek() + 1;
        OnChanged?.Invoke();
    }

    public Enumerator GetEnumerator() => new(_items);
    IEnumerator<SidebarPin> IEnumerable<SidebarPin>.GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Allocation-free <c>foreach</c> over the pins (the pinned section renders per pin, per render).</summary>
    public struct Enumerator
    {
        readonly List<SidebarPin> _list;
        int _i;
        internal Enumerator(List<SidebarPin> list) { _list = list; _i = -1; }
        public SidebarPin Current => _list[_i];
        public bool MoveNext() => ++_i < _list.Count;
    }
}


// ── FIRST-SEEN: the bounded first-observation stamp map ─────────────────────────────────────────────────────────────
// The honest playlist "date added" proxy. Playlists have no add timestamp anywhere — the rootlist is an ordered marker
// stream, not a timestamped SavedItem set — so the projection records the first time it ever observes a playlist id and
// sorts by that. On the very first run every playlist gets the SAME stamp and ties break by SourceOrder ascending
// (Spotify's own newest-first rootlist order); from then on every newly added playlist gets a genuinely correct relative
// position. Honest-but-approximate: a surface's label must not promise more than that — this is a LOCAL stamp and must
// never masquerade as a server field.

public sealed class SidebarFirstSeen
{
    /// <summary>Id cap. Beyond it the OLDEST stamp is evicted to admit a new id, so the map can never grow unbounded
    /// even if pruning never runs.</summary>
    public const int Cap = 2000;

    /// <summary>A shared, FROZEN instance: it never records and never mutates, so it is safe as a default argument for a
    /// projection that must not persist anything (a preview, a test that does not care).</summary>
    public static readonly SidebarFirstSeen Frozen = new(frozen: true);

    readonly Dictionary<string, long> _first = new(StringComparer.Ordinal);
    readonly Func<long> _clock;
    readonly bool _frozen;

    /// <summary>Ids stamped since the last <see cref="ResetNewCount"/> — a non-zero value is what triggers a document
    /// commit.</summary>
    public int NewStamps { get; private set; }

    public SidebarFirstSeen(Func<long>? nowUnixMs = null)
    {
        _clock = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _frozen = false;
    }

    SidebarFirstSeen(bool frozen)
    {
        _clock = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _frozen = frozen;
    }

    /// <summary>Rehydrate from the persisted document (invalid rows skipped; the newest stamp wins on a duplicate id).</summary>
    public void Load(IReadOnlyList<KeyValuePair<string, long>> stamps)
    {
        if (_frozen) return;
        for (int i = 0; i < stamps.Count; i++)
        {
            var kv = stamps[i];
            if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
            if (!_first.TryGetValue(kv.Key, out long cur) || kv.Value < cur) _first[kv.Key] = kv.Value;
        }
        NewStamps = 0;
    }

    public int Count => _first.Count;

    /// <summary>The stamp for an id, WITHOUT recording one (0 = never observed).</summary>
    public long Peek(string id) => _first.TryGetValue(id, out long ms) ? ms : 0L;

    /// <summary>The stamp for an id, recording "now" the first time the id is ever seen — the call the projection makes
    /// per playlist row. <see cref="NewStamps"/> counts the fresh records so the caller knows to persist.</summary>
    public long Stamp(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0L;
        if (_first.TryGetValue(id, out long ms)) return ms;
        long now = _clock();
        if (_frozen) return now;                      // behave as "just seen" without mutating the shared instance
        if (_first.Count >= Cap) EvictOldest();
        _first[id] = now;
        NewStamps++;
        return now;
    }

    public void ResetNewCount() => NewStamps = 0;

    /// <summary>Drop stamps for ids no longer in the library (called on save). Returns the number removed.
    /// <paramref name="live"/> is the id set the projection just produced.</summary>
    public int PruneTo(IReadOnlyCollection<string> live)
    {
        if (_frozen || _first.Count == 0) return 0;
        List<string>? dead = null;
        foreach (var id in _first.Keys)
        {
            bool found = false;
            foreach (var l in live) if (string.Equals(l, id, StringComparison.Ordinal)) { found = true; break; }
            if (!found) (dead ??= new List<string>()).Add(id);
        }
        if (dead is null) return 0;
        for (int i = 0; i < dead.Count; i++) _first.Remove(dead[i]);
        return dead.Count;
    }

    /// <summary>Snapshot for persistence (append-into; the caller owns the list). Order is unspecified — the document
    /// is a map, not a sequence.</summary>
    public void CopyTo(List<KeyValuePair<string, long>> into)
    {
        foreach (var kv in _first) into.Add(kv);
    }

    // O(n) and only ever at the cap — a 2000-entry scan, on the UI thread, at most once per newly observed id.
    void EvictOldest()
    {
        string? oldestId = null;
        long oldest = long.MaxValue;
        foreach (var kv in _first)
            if (kv.Value < oldest) { oldest = kv.Value; oldestId = kv.Key; }
        if (oldestId is not null) _first.Remove(oldestId);
    }
}

// ── RECENCY: navigation recency for the "recently opened" feed ONLY ─────────────────────────────────────────────────
// Route-key → last-visited ticks, built from the navigation history log. This feeds ONLY the "recently opened" feed (a
// JumpBackIn-style shelf of places you navigated to). It is explicitly NOT what the sidebar's "Recents" SORT reads — that
// reads the play log's recency, so clicking a row to open it does not reorder the list; only playing something does. A
// surface must not relabel this feed "Recently played", and must not sort a list by these ticks expecting "recently
// played" either — the two recency facts are deliberately separate inputs with separate consumers.

/// <summary>One navigation observation: the route key that was opened and when (UTC ticks). The route key is the entry/
/// pin id, so the recency join is an identity lookup — no prefix stripping, no per-row parsing.</summary>
public readonly record struct SidebarVisit(string RouteKey, long TicksUtc);

public sealed class SidebarRecency
{
    /// <summary>The shared "nothing was ever visited" instance (the seed a surface renders before history loads).</summary>
    public static readonly SidebarRecency Empty = new(new Dictionary<string, long>(0, StringComparer.Ordinal));

    readonly Dictionary<string, long> _last;

    SidebarRecency(Dictionary<string, long> last) => _last = last;

    public int Count => _last.Count;

    /// <summary>Last-visited UTC ticks for an entry/pin id; 0 = never visited.</summary>
    public long LastVisitedTicks(string? id) => id is not null && _last.TryGetValue(id, out long t) ? t : 0L;

    /// <summary>Build from an oldest-first visit log. Walks BACKWARDS so the FIRST hit per key wins (the newest visit) —
    /// O(n) with no comparisons and no per-key max().</summary>
    public static SidebarRecency Build(IReadOnlyList<SidebarVisit> visitsOldestFirst)
    {
        if (visitsOldestFirst.Count == 0) return Empty;
        var map = new Dictionary<string, long>(visitsOldestFirst.Count, StringComparer.Ordinal);
        for (int i = visitsOldestFirst.Count - 1; i >= 0; i--)
        {
            var v = visitsOldestFirst[i];
            if (v.RouteKey is { Length: > 0 }) map.TryAdd(v.RouteKey, v.TicksUtc);
        }
        return new SidebarRecency(map);
    }

    /// <summary>Build off an oldest-first log of any row type, via STATIC accessor lambdas (cached by the compiler, so a
    /// rebuild allocates nothing but the dictionary) — keeps the real history-entry type out of this layer.</summary>
    public static SidebarRecency Build<T>(IReadOnlyList<T> entriesOldestFirst, Func<T, string> keyOf, Func<T, long> ticksUtcOf)
    {
        if (entriesOldestFirst.Count == 0) return Empty;
        var map = new Dictionary<string, long>(entriesOldestFirst.Count, StringComparer.Ordinal);
        for (int i = entriesOldestFirst.Count - 1; i >= 0; i--)
        {
            var key = keyOf(entriesOldestFirst[i]);
            if (key is { Length: > 0 }) map.TryAdd(key, ticksUtcOf(entriesOldestFirst[i]));
        }
        return new SidebarRecency(map);
    }
}

/// <summary>The two shell logs the binder's recency lanes read (G-172): the navigation log (<c>history.json</c>,
/// "recently OPENED") and the play log (<c>play-log.json</c> + its recency sidecar, "recently PLAYED"). An interface
/// only so a test hands the binder its own logs instead of racing the process-wide shell stores.</summary>
public interface ISidebarRecencyLogs
{
    /// <summary>Bumps on every navigation-log mutation.</summary>
    int HistoryVersion { get; }
    /// <summary>The navigation log, OLDEST first.</summary>
    IReadOnlyList<Shell.HistoryEntry> History { get; }
    /// <summary>Bumps on every play-log append or recency merge.</summary>
    int PlayLogVersion { get; }
    /// <summary>The play log, NEWEST last.</summary>
    IReadOnlyList<Shell.PlayEntry> Plays { get; }
    /// <summary>uri → last-played unix ms — what the Recents SORT reads (<c>SidebarLibraryEntry.LastPlayedMs</c>).</summary>
    IReadOnlyDictionary<string, long> LastPlayed { get; }
}

/// <summary>The production logs: <c>Shell.History.Store</c> and <c>Shell.PlayLog</c>, peeked (the pane's pump is what
/// subscribes to their versions).</summary>
public sealed class ShellRecencyLogs : ISidebarRecencyLogs
{
    public static readonly ShellRecencyLogs Instance = new();

    ShellRecencyLogs() { }

    public int HistoryVersion => Shell.History.Store.Version.Peek();
    public IReadOnlyList<Shell.HistoryEntry> History => Shell.History.Store.Entries;
    public int PlayLogVersion => Shell.PlayLog.Version.Peek();
    public IReadOnlyList<Shell.PlayEntry> Plays => Shell.PlayLog.Entries;
    public IReadOnlyDictionary<string, long> LastPlayed => Shell.PlayLog.Recency;
}

/// <summary>The pure folds from the shell's log rows to the sidebar's engine-free recency shapes. Run only when a log's
/// version moved, never per rebuild.</summary>
public static class SidebarRecencyFold
{
    /// <summary>The navigation log as <see cref="SidebarVisit"/>s, oldest first, keyed by the route KEY — which is the
    /// entry/pin id, so the recency join stays an identity lookup. A NotFound route addresses nothing and is skipped.</summary>
    public static void Visits(IReadOnlyList<Shell.HistoryEntry> oldestFirst, List<SidebarVisit> into)
    {
        into.Clear();
        for (int i = 0; i < oldestFirst.Count; i++)
        {
            var e = oldestFirst[i];
            if (e.Route.Kind == Shell.RouteKind.NotFound) continue;
            into.Add(new SidebarVisit(Shell.NameOf(e.Route), e.VisitedAt.ToUniversalTime().Ticks));
        }
    }

    /// <summary>The play log collapsed to its newest distinct CONTEXTS (0.2.9 <c>PlayLogStore.RecentContexts</c>):
    /// walked newest first, the context a play started from — else the bare track — once per uri, at most
    /// <paramref name="max"/>. <paramref name="seen"/> is the caller's reusable dedupe set.</summary>
    public static int PlayedContexts(IReadOnlyList<Shell.PlayEntry> newestLast, int max,
                                     List<SidebarPlayedContext> into, HashSet<string> seen)
    {
        into.Clear();
        seen.Clear();
        for (int i = newestLast.Count - 1; i >= 0 && into.Count < max; i--)
        {
            var e = newestLast[i];
            bool context = e.Context.IsValid;
            var uri = context ? e.Context : e.Track;
            if (!uri.IsValid) continue;
            string text = uri.Text;
            if (text.Length == 0 || !seen.Add(text)) continue;
            into.Add(new SidebarPlayedContext(text, KindOf(uri, context), e.PlayedAtMs, context ? e.ContextTitle : null));
        }
        return into.Count;
    }

    /// <summary>A played uri's row kind: Liked Songs is the "liked" ROUTE (whatever spelling the play carried), a
    /// catalogue container is its own kind, and a bare track or episode play stays a playable TRACK row.</summary>
    public static SidebarEntryKind KindOf(EntityUri uri, bool isContext)
    {
        if (EntityUri.IsLikedCollection(uri.Text)) return SidebarEntryKind.AppRoute;
        return uri.Kind switch
        {
            EntityKind.Album => SidebarEntryKind.Album,
            EntityKind.Playlist => SidebarEntryKind.Playlist,
            EntityKind.Artist => SidebarEntryKind.Artist,
            EntityKind.Show => SidebarEntryKind.Show,
            EntityKind.Track or EntityKind.Episode => SidebarEntryKind.Track,
            _ => isContext ? SidebarEntryKind.AppRoute : SidebarEntryKind.Track,
        };
    }
}
// ── PROJECTION BINDER ──────────────────────────────────────────────────────────────────────────────────────────────────
// The ONE rebuild driver: folds every trigger (the account's library and rootlist edges and the ROWS they point at, the
// pins, the folders, the layout, the library's chip, sort, search and qualifier, the two shell logs), builds the library
// projection from `User.Me`'s edges, and shapes it into the published entry list and the planner's input.
//
// THREADING: UI thread only, unsynchronized, and never blocking (C1/C9) — a rebuild reads in-memory tables and the two
// in-memory shell logs, never a socket or a file.

/// <summary>The projection binder: the one place a rebuild becomes visible to the planner.</summary>
public sealed class SidebarProjectionBinder
{
    /// <summary>How many navigation/playback rows each recency feed keeps.</summary>
    public const int RecencyCap = 40;

    // ── the A/B build slots ──────────────────────────────────────────────────────────────────────────────────────────
    // `Library`/`PlaylistTree`/`ByUri` alias these lists, so a rebuild computes into the slot the published input does
    // NOT point at: the input a pane planned from one rebuild ago is never cleared out from under its plan.
    sealed class BuildSlot
    {
        public readonly List<SidebarLibraryEntry> All = new(256);
        public readonly List<SidebarLibraryEntry> Tree = new(128);
        public readonly SidebarSourceIndex Index = new();
    }

    readonly BuildSlot _slotA = new(), _slotB = new();
    BuildSlot _publishedStage;
    BuildSlot _lastBuild;

    BuildSlot BuildTarget => ReferenceEquals(_publishedStage, _slotA) ? _slotB : _slotA;

    // Rebuild buffers — allocated once, reused forever (P8).
    readonly List<SidebarLibraryEntry> _pinRows = new(16);
    readonly List<SidebarLibraryEntry> _played = new(16);
    readonly List<SidebarLibraryEntry> _newReleases = new(8);
    readonly List<SidebarLibraryEntry> _scratch = new(256);   // PinsFirst' partition buffer
    readonly List<string> _liveIds = new(256);
    readonly HashSet<string> _pinnedIds = new(StringComparer.Ordinal);
    readonly Func<string, bool> _isFolderExpanded;

    // The two shell logs (G-172), recomputed only when their version moved.
    readonly ISidebarRecencyLogs _logs;
    readonly ISidebarEntityPeek _peek;
    readonly List<SidebarVisit> _visits = new(64);
    readonly List<SidebarPlayedContext> _playedContexts = new(RecencyCap);
    readonly HashSet<string> _playedSeen = new(StringComparer.Ordinal);
    SidebarRecency _recency = SidebarRecency.Empty;
    int _historySeen = int.MinValue, _playLogSeen = int.MinValue;
    long _newReleasesAskedTicks;   // when the What's New feed was last asked to refresh (0 = never)

    // The gate's extra rows: the entity each UNLISTED pin resolved through on the last rebuild (a pinned editorial
    // playlist hydrating must re-render its pin row).
    readonly List<EntityRef> _pinRefs = new(8);
    // E3/Bug A1, pin-band half: `ResolvePins` collects the playlist SLOTS a LISTED pin still needs into these
    // instead of asking per pin, and flushes at most one span-form call of each after its loop — the same pattern
    // `SidebarProjection.Build`'s own `s_ensureIdentitySlots`/`s_ensureTracksSlots` use for the rootlist walk.
    readonly List<int> _pinEnsureIdentitySlots = new(8);
    readonly List<int> _pinEnsureTracksSlots = new(8);
    // The unlisted cover-less pins' leading member tracks (`SidebarProjection.CollectMosaicTrackSlots`), one ask.
    readonly List<int> _pinEnsureMosaicTrackSlots = new(16);
    // D4 (F2): the same idiom for an UNLISTED pin's non-playlist identity — `ResolveLivePin` used to fire one
    // `Entities.Ensure` per pin per kind; collected here instead and flushed once per kind after the loop.
    readonly List<int> _pinEnsureAlbumSlots = new(4);
    readonly List<int> _pinEnsureArtistSlots = new(4);
    readonly List<int> _pinEnsureShowSlots = new(4);

    // The planner inputs that are NOT the published entries, published as one edge (see InputVersion).
    readonly SidebarEntriesShadow _pinShadow = new(), _playedShadow = new(), _newReleasesShadow = new();
    readonly Signal<int> _inputVersion = new(0);
    long _inputMeta = long.MinValue;

    SidebarFirstSeen? _firstSeen;
    SidebarProjectionInput _input;
    SidebarBinderTriggers _lastTriggers;
    SidebarSourceState _libraryState = SidebarSourceState.Pending;
    SidebarSourceState _treeState = SidebarSourceState.Pending;
    int _revision;
    bool _started;
    bool _rebuilding;
    bool _dirty = true;

    /// <summary>The published entry cell (F.7.5's <c>SidebarEntries</c>) — the ONE thing the Library pane binds to.
    /// Owned here because <see cref="Rebuild"/> is the one place a pass becomes visible.</summary>
    public readonly SidebarEntries Entries = new();

    /// <param name="logs">The navigation + play logs the recency feeds read; null = the shell's own
    /// (<see cref="ShellRecencyLogs"/>). A test passes its own so it never races the process-wide stores.</param>
    /// <param name="peek">The resident entity read the recently-played rule uses; null = <see cref="SidebarResidentPeek"/>.</param>
    public SidebarProjectionBinder(ISidebarRecencyLogs? logs = null, ISidebarEntityPeek? peek = null)
    {
        _publishedStage = _slotA;
        _lastBuild = _slotA;
        _isFolderExpanded = Sidebar.IsFolderExpanded;
        _logs = logs ?? ShellRecencyLogs.Instance;
        _peek = peek ?? SidebarResidentPeek.Instance;
    }

    /// <summary>An account swap re-seeds the first-seen proxy from the new account's file (§P3.8).</summary>
    public void ResetFirstSeen() { _firstSeen = null; Invalidate(); }

    /// <summary>Idempotent start: do the first rebuild.</summary>
    public void Start()
    {
        if (_started) { Invalidate(); Sync(); return; }
        _started = true;
        Log.Info("sidebar", "Projection binder started.");
        Invalidate();
        Sync();
    }

    public void Stop()
    {
        _started = false;
        Log.Info("sidebar", "Projection binder stopped.");
    }

    // ─────────────────────────────────── reads ───────────────────────────────────

    /// <summary>The planner input for the CURRENT projection. Its lists ALIAS the binder's buffers, so it is valid
    /// until the next rebuild. Key a memo on <see cref="Revision"/>; SUBSCRIBE through <see cref="Entries"/>'s version
    /// and <see cref="InputVersion"/>.</summary>
    public SidebarProjectionInput CurrentInput => _input;

    /// <summary>Bumped once per rebuild — the planner's <c>DepKey</c> lane. A plain value, never a subscription.</summary>
    public int Revision => _revision;

    /// <summary>Bumped once per rebuild in which a planner input OTHER than the published entries changed — the pin
    /// band, Recently played, New releases, or the library/tree source state. <see cref="Entries"/>' version only moves
    /// with the library projection, so without this edge a feed section would re-plan only when something unrelated
    /// did (G-172/G-173). Shadow-gated exactly like the entries cell: a rebuild that reproduced identical feeds does
    /// not bump it.</summary>
    public IReadSignal<int> InputVersion => _inputVersion;

    // ─────────────────────────────────── the rebuild gate ───────────────────────────────────

    /// <summary>Force the next <see cref="Sync"/> to rebuild even if no trigger moved (a new host, a retry).</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>Rebuild iff a trigger moved (or <see cref="Invalidate"/> was called). Returns whether it rebuilt. Cheap
    /// enough to call on every pump wake — the gate is one fold over edge and row versions.</summary>
    public bool Sync()
    {
        if (_rebuilding) { _dirty = true; return false; }
        var triggers = Read();
        if (!_dirty && triggers == _lastTriggers) return false;
        _dirty = false;

        _rebuilding = true;
        try { Rebuild(); }
        finally { _rebuilding = false; }

        // A source that fired Changed mid-rebuild only marked us dirty; settle now (never recurse into Rebuild).
        if (_dirty)
        {
            _dirty = false;
            _rebuilding = true;
            try { Rebuild(); }
            finally { _rebuilding = false; }
        }
        // Snapshot the fold AFTER the pass: the rebuild itself refreshes the unlisted-pin rows and the feed demand the
        // fold reads, and a snapshot taken before it would make the very next wake rebuild again for nothing.
        _lastTriggers = Read();
        return true;
    }

    // ─────────────────────────────────── the rebuild ───────────────────────────────────

    void Rebuild()
    {
        var u = User.Me;
        RefreshRecency();
        var recency = _recency;
        var lastPlayed = _logs.LastPlayed;
        var firstSeen = _firstSeen ??= LoadFirstSeen(Sidebar.FirstSeen);
        var doc = Sidebar.Doc;
        var demand = SidebarFeedDemands.Of(doc);

        var build = BuildTarget;
        _lastBuild = build;

        // 1, 2 — THE projection (every kind, folders and children) + the tree slice + the join index.
        var full = SidebarProjection.Build(build.All, in u, SidebarEntryKindMask.All, firstSeen, recency,
                                           includeFolderChildren: true, lastPlayed: lastPlayed);
        SidebarProjection.Build(build.Tree, in u, SidebarEntryKindMask.PlaylistTree, firstSeen, recency,
                                includeFolderChildren: true, lastPlayed: lastPlayed);
        build.Index.Rebuild(build.All);
        _treeState = StateOf(u.RootlistState);
        _libraryState = Worst(StateOf(u.State(LibraryEdgeKind.SavedAlbums)),
                        Worst(StateOf(u.State(LibraryEdgeKind.FollowedArtists)), StateOf(u.State(LibraryEdgeKind.SavedShows))));

        // 3 — the PUBLISHED list Your Library renders: chip → hidden kinds → qualifier → search → sort → pins first.
        // Classic reads none of it, so outside Library this pass is the plain everything-list (cheap, and it keeps the
        // Entries cell honest for the folder flyout).
        bool library = doc.Layout == SidebarLayoutId.Library;
        var options = doc.Library;
        var filter = library ? Sidebar.LibraryFilter.Peek() : SidebarLibraryFilter.None;
        string search = library ? SidebarSearch.Normalize(Sidebar.LibrarySearch.Peek()) : "";
        bool searching = search.Length > 0;
        bool qualifiers = SidebarProjection.QualifiersAvailable(full.FlavorMask);
        var buffer = Entries.Buffer;
        var libResult = SidebarProjection.Build(buffer, in u, SidebarEntryKinds.From(filter), firstSeen, recency,
                                                includeFolderChildren: searching,
                                                isFolderExpanded: searching ? null : _isFolderExpanded,
                                                lastPlayed: lastPlayed, ensureIdentity: true);
        ResolvePins(build.Index, u.RootlistState);
        bool pinnedShown = doc.Find(SidebarSectionKind.Pinned) is { Hidden: false };
        var query = new SidebarLibraryQuery(filter, library ? (SidebarV3Qualifier)Sidebar.V3Qualifier.Peek() : SidebarV3Qualifier.Any,
            library ? options.HiddenKinds : SidebarLibraryKinds.None, options.Sort, options.Descending, search, qualifiers);
        var shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, pinnedShown ? Sidebar.Pins.Items : null);

        // 4 — Recently played: synchronous (index → resident peek → logged title → skip), only while shown.
        _played.Clear();
        if ((demand & SidebarFeedDemand.Recent) != 0)
            SidebarRecentsRules.Resolve(_playedContexts, build.Index, _peek, RecencyCap, _played);

        // 5 — New releases: the What's New feed, kicked at most every 30 minutes, only while shown.
        _newReleases.Clear();
        if ((demand & SidebarFeedDemand.NewReleases) != 0)
        {
            long now = System.Environment.TickCount64;
            if (SidebarNewReleasesRules.ShouldRefresh(_newReleasesAskedTicks, now, Notify.ReleasesState.Peek()))
            {
                _newReleasesAskedTicks = now;
                Notify.RefreshFeeds?.Invoke();
            }
            SidebarNewReleasesRules.Fill(Notify.Items.Peek().Items, build.Index, _newReleases, RecencyCap);
        }

        bool anyPending = AnyContributingKindPending(filter, u);
        var (state, error) = PublishState(shape.Count, anyPending);
        _publishedStage = build;
        bool entriesChanged = Entries.Publish(state, error, anyPending, qualifiers, shape.PinCount, _publishedStage.All);
        bool inputChanged = PublishInput();
        int newStamps = full.NewFirstSeenStamps + libResult.NewFirstSeenStamps;
        if (newStamps > 0) CommitFirstSeen(firstSeen, build.All);
        if (SidebarRevisionGate.ShouldBump(entriesChanged, inputChanged)) _revision++;
        _input = new SidebarProjectionInput(
            Library: _publishedStage.All,
            PlaylistTree: _publishedStage.Tree,
            Pins: _pinRows,
            Played: _played,
            NewReleases: _newReleases,
            PinnedIds: _pinnedIds,
            ExpandedFolders: Sidebar.ExpandedFolders,
            Search: search,
            LibraryState: _libraryState,
            TreeState: _treeState,
            Revision: _revision);
    }

    /// <summary>Re-derive the visits, the navigation recency and the played contexts — each only when its log moved.</summary>
    void RefreshRecency()
    {
        int history = _logs.HistoryVersion;
        if (history != _historySeen)
        {
            _historySeen = history;
            SidebarRecencyFold.Visits(_logs.History, _visits);
            _recency = SidebarRecency.Build(_visits);
        }
        int plays = _logs.PlayLogVersion;
        if (plays != _playLogSeen)
        {
            _playLogSeen = plays;
            SidebarRecencyFold.PlayedContexts(_logs.Plays, RecencyCap, _playedContexts, _playedSeen);
        }
    }

    /// <summary>Bump <see cref="InputVersion"/> iff a non-entries planner input differs from the last published pass.
    /// Every shadow is evaluated (no short-circuit), so each one always holds the pass it last saw. Returns whether it
    /// bumped — BUG E2's <see cref="SidebarRevisionGate"/> reads this alongside <see cref="SidebarEntries.Publish"/>'s
    /// own return to decide whether <see cref="Revision"/> moves this rebuild.</summary>
    bool PublishInput()
    {
        bool changed = _pinShadow.Publish(_pinRows, default);
        changed |= _playedShadow.Publish(_played, default);
        changed |= _newReleasesShadow.Publish(_newReleases, default);
        ulong h = SidebarLibraryFingerprint.Seed;
        h = SidebarLibraryFingerprint.Mix(h, (uint)_libraryState);
        h = SidebarLibraryFingerprint.Mix(h, (uint)_treeState);
        long meta = (long)h;
        if (meta != _inputMeta) { _inputMeta = meta; changed = true; }
        if (changed) _inputVersion.Value = _inputVersion.Peek() + 1;
        return changed;
    }

    // Pins resolve against the fresh projection; an UNRESOLVED pin still renders from its own display cache (offline-
    // first) instead of disappearing. An unresolved pin's target is `Entities.Ensure`d at Visible priority, and its row
    // is remembered in `_pinRefs` so the gate's fingerprint re-renders the pin the moment that row hydrates.
    // `rootlistState` (trap 5) is what an unresolved FOLDER pin renders as while it is not (yet) found — Pending
    // before the rootlist has ever answered this session, Missing only once it has and still doesn't carry it.
    void ResolvePins(SidebarSourceIndex index, EdgeState rootlistState)
    {
        _pinRows.Clear();
        _pinnedIds.Clear();
        _pinRefs.Clear();
        _pinEnsureIdentitySlots.Clear();
        _pinEnsureTracksSlots.Clear();
        _pinEnsureMosaicTrackSlots.Clear();
        _pinEnsureAlbumSlots.Clear();
        _pinEnsureArtistSlots.Clear();
        _pinEnsureShowSlots.Clear();
        var pins = Sidebar.Pins.Items;
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.Id.Length == 0) continue;
            _pinnedIds.Add(pin.Id);
            if (index.TryGet(pin.Id, out var entry))
            {
                _pinRows.Add(entry with { IsPinned = true, SourceOrder = i });
                Sidebar.TouchPin(pin.Id, entry.Name);
                // Bug H: a pin is shown regardless of folder collapse, but the index it was found in came from the
                // FULL projection (`build.All`), which never ensures identity (see `SidebarProjection.Build`'s
                // `ensureIdentity` doc) — only the fold-state-gated buffer build does, and a collapsed-away pin
                // never reaches that walk. Collect it here instead, bounded by the pin count (never "the whole
                // rootlist"), the same way `ResolveLivePin` does for an UNLISTED pin below.
                CollectListedPinAsks(in entry);
                continue;
            }

            var hydrated = ResolveLivePin(pin, out var row);
            if (!row.IsNone) _pinRefs.Add(row);
            if (hydrated is { } h) Sidebar.TouchPin(pin.Id, h.Name);
            _pinRows.Add(SidebarBinderPipeline.ResolveUnlistedPin(pin, i, hydrated, rootlistState));
        }

        // E3: ONE span-form ask per group for the whole pin band, after the loop — never one
        // `Entities.Ensure`/`EnsureEdge` call per pin.
        if (_pinEnsureIdentitySlots.Count > 0)
            Entities.Ensure(Entities.Current.Playlists,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureIdentitySlots),
                (uint)PlaylistFields.Identity, FetchPriority.Visible);
        // D2: a cover-less pin's mosaic warm — a tile, not a page — never `Visible` any more (the count-driven ask
        // this list used to also carry, bug A1's `ShouldEnsureCount`, is deleted from `CollectListedPinAsks`).
        if (_pinEnsureTracksSlots.Count > 0)
            Entities.EnsureEdge(FetchEdge.PlaylistTracks,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureTracksSlots),
                priority: FetchPriority.Prefetch);
        if (_pinEnsureMosaicTrackSlots.Count > 0)
            Entities.Ensure(Entities.Current.Tracks,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureMosaicTrackSlots),
                (uint)SidebarProjection.MosaicTrackFields, FetchPriority.Prefetch);
        // D4 (F2): an UNLISTED Album/Artist/Show pin's identity — one span ask per kind, not one per pin.
        if (_pinEnsureAlbumSlots.Count > 0)
            Entities.Ensure(Entities.Current.Albums,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureAlbumSlots),
                (uint)AlbumFields.Identity, FetchPriority.Visible);
        if (_pinEnsureArtistSlots.Count > 0)
            Entities.Ensure(Entities.Current.Artists,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureArtistSlots),
                (uint)ArtistFields.Identity, FetchPriority.Visible);
        if (_pinEnsureShowSlots.Count > 0)
            Entities.Ensure(Entities.Current.Shows,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinEnsureShowSlots),
                (uint)ShowFields.Identity, FetchPriority.Visible);
    }

    /// <summary>Bug H companion to <see cref="ResolveLivePin"/>: a LISTED pin (already found in the projection
    /// index) whose identity or cover has not landed yet, or whose cover-less mosaic still needs its member tracks.
    /// Playlist-only — Album/Artist/Show listed pins are unaffected and read their fields directly off the edge,
    /// never gated on a Knows() check. D2 (issue #4): a count is never asked for on its own any more — the deleted
    /// bug A1 `ShouldEnsureCount` used to force this same full read just to learn a number;
    /// <see cref="SidebarLibraryEntry.CountKnown"/> is now a passive read of <see cref="PlaylistFields.TrackCount"/>
    /// (see `SidebarProjection.WalkRootlist`'s doc), never a reason to warm anything here. Collects into
    /// <see cref="_pinEnsureIdentitySlots"/>/<see cref="_pinEnsureTracksSlots"/> — <see cref="ResolvePins"/> issues
    /// the batched asks once, after its loop (the tracks ask at Prefetch).</summary>
    void CollectListedPinAsks(in SidebarLibraryEntry entry)
    {
        if (entry.Kind != SidebarEntryKind.Playlist) return;
        if (entry.IdentityKnown && !entry.Cover.IsEmpty) return;   // no cover-less mosaic, no count ask — nothing to warm
        if (entry.Uri.Length == 0 || !EntityId.TryParse(entry.Uri, out var id)) return;
        var p = new Playlist(Entities.Current.Playlists.Slot(id));
        if (!p.Knows(PlaylistFields.Identity)) _pinEnsureIdentitySlots.Add(p.Slot);
        bool needsMembership = p.ImageId.IsEmpty && p.MembershipState == EdgeState.Unknown;
        if (needsMembership) _pinEnsureTracksSlots.Add(p.Slot);
    }

    // The entity a pin the library projection does not know (an editorial/Spotify-owned playlist, or any other row
    // never saved to the user's own library/rootlist). Returns the entity's CURRENTLY known fields (null if not yet
    // known, having just kicked its fetch) and the row it read — never an async callback, never a cache keyed by pin id.
    SidebarLibraryEntry? ResolveLivePin(SidebarPin pin, out EntityRef row)
    {
        row = default;
        if (pin.Uri.Length == 0 || !EntityId.TryParse(pin.Uri, out var id)) return null;
        switch (pin.Kind)
        {
            case SidebarEntryKind.Playlist:
            {
                var p = new Playlist(Entities.Current.Playlists.Slot(id));
                row = new EntityRef(EntityKind.Playlist, p.Slot);
                if (!p.Knows(PlaylistFields.Identity))
                { _pinEnsureIdentitySlots.Add(p.Slot); return null; }
                // G-059: an unlisted pin (the library projection does not carry it) gets the same cover-less mosaic
                // fallback as a rootlist row — else a Pinned tile for it blanks out while its detail page mosaics fine.
                var mosaic = p.ImageId.IsEmpty ? SidebarProjection.PlaylistMosaicTiles(in p) : null;
                // The same count rule as the rootlist walk (`SidebarProjection.WalkRootlist`): the row fact first, then —
                // free — the resident membership's Total, so a pin and its rootlist row never disagree about a count.
                bool trackCountKnown = p.Knows(PlaylistFields.TrackCount);
                bool membershipResident = p.MembershipState != EdgeState.Unknown;
                SidebarProjection.PlaylistCount(trackCountKnown, p.TrackCount, membershipResident, p.MembershipTotal,
                    out bool countKnown, out int trackCount);
                // D2 (the cache plan §3.4): a count is a ROW fact and never a reason to read the list — only a
                // cover-less mosaic asks the membership edge, at Prefetch (`ResolvePins`' flush), same as a listed pin.
                bool needsMembership = mosaic is null && p.ImageId.IsEmpty && p.MembershipState == EdgeState.Unknown;
                if (needsMembership) _pinEnsureTracksSlots.Add(p.Slot);
                // A pin is always visible, so the rootlist walk's `ensureIdentity` gate is simply true here.
                if (SidebarProjection.ShouldWarmMosaicTracks(true, !p.ImageId.IsEmpty, p.MembershipState, mosaic?.Count ?? 0))
                    SidebarProjection.CollectMosaicTrackSlots(p.TrackSlots, _pinEnsureMosaicTrackSlots);
                return new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", Entities.Strings.Resolve(p.TitleId),
                    Entities.Strings.Resolve(p.Owner.NameId), p.ImageId, mosaic, ChildCount: trackCount, AddedAtMs: 0,
                    SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false,
                    Flavor: SidebarPlaylistFlavor.None)
                {
                    // Bug H: reached only past the `!Knows(Identity)` early-return above, so identity is
                    // unconditionally landed here — never gate this branch's subtitle on a bit that was never set.
                    IdentityKnown = true,
                    // Bug A1: NOT unconditionally true past that same gate — a route can land Identity while
                    // carrying no length at all (ListMetadataV2). Read the real bit.
                    CountKnown = countKnown,
                };
            }
            case SidebarEntryKind.Album:
            {
                var a = new Album(Entities.Current.Albums.Slot(id));
                row = new EntityRef(EntityKind.Album, a.Slot);
                if (!a.Knows(AlbumFields.Identity))
                { _pinEnsureAlbumSlots.Add(a.Slot); return null; }
                var artistSlots = a.ArtistSlots;
                return new SidebarLibraryEntry("", SidebarEntryKind.Album, "", a.Title, "", a.ImageId, null,
                    ChildCount: a.TrackCount, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
                    Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                {
                    // Same derivation SidebarProjection.Build's album loop uses (Sidebar.cs): the entity's first
                    // billed artist, not the joined Creator string — an unlisted album pin never had this overlay.
                    FirstArtistName = artistSlots.Length > 0 ? new Artist(artistSlots[0]).Name : "",
                    // Trap 5: reached only past the `!Knows(Identity)` early-return above, so Identity is
                    // unconditionally landed here — `ResolveUnlistedPin`'s merge also stamps this unconditionally
                    // once `hydrated` is non-null, but the per-kind row itself should not lie about its own state.
                    IdentityKnown = true,
                };
            }
            case SidebarEntryKind.Artist:
            {
                var ar = new Artist(Entities.Current.Artists.Slot(id));
                row = new EntityRef(EntityKind.Artist, ar.Slot);
                if (!ar.Knows(ArtistFields.Identity))
                { _pinEnsureArtistSlots.Add(ar.Slot); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Artist, "", ar.Name, "", ar.ImageId, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0,
                    Circular: true, Flavor: SidebarPlaylistFlavor.None)
                { IdentityKnown = true };
            }
            case SidebarEntryKind.Show:
            {
                var s = new Show(Entities.Current.Shows.Slot(id));
                row = new EntityRef(EntityKind.Show, s.Slot);
                if (!s.Knows(ShowFields.Identity))
                { _pinEnsureShowSlots.Add(s.Slot); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Show, "", s.Title, "", s.ImageId, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0,
                    Circular: false, Flavor: SidebarPlaylistFlavor.None)
                { IdentityKnown = true };
            }
            default:
                return null;   // Folder/AppRoute/Track — no catalog entity backs any of these
        }
    }

    // The skeleton gate is per CONTRIBUTING kind (a pending Shows load must not skeleton the Playlists filter).
    static bool AnyContributingKindPending(SidebarLibraryFilter filter, in User u)
    {
        var kinds = SidebarEntryKinds.From(filter);
        if ((kinds & SidebarEntryKindMask.PlaylistTree) != 0 && u.RootlistState == EdgeState.Unknown) return true;
        if ((kinds & SidebarEntryKindMask.Album) != 0 && u.State(LibraryEdgeKind.SavedAlbums) == EdgeState.Unknown) return true;
        if ((kinds & SidebarEntryKindMask.Artist) != 0 && u.State(LibraryEdgeKind.FollowedArtists) == EdgeState.Unknown) return true;
        if ((kinds & SidebarEntryKindMask.Show) != 0 && u.State(LibraryEdgeKind.SavedShows) == EdgeState.Unknown) return true;
        return false;
    }

    // GAP: `EdgeState` has no Failed member (Unknown/Partial/Complete only — ch 15 §7), so a fetch failure has no
    // per-relation surface yet; this never reports Failed, unlike 0.2.9's Loadable-backed version.
    static (FluentGpu.Signals.LoadState State, Exception? Error) PublishState(int count, bool anyPending)
        => count == 0 && anyPending ? (FluentGpu.Signals.LoadState.Pending, null) : (FluentGpu.Signals.LoadState.Ready, null);


    static SidebarFirstSeen LoadFirstSeen(SidebarFirstSeenDto[]? stored)
    {
        var seen = new SidebarFirstSeen();
        if (stored is null || stored.Length == 0) return seen;
        var pairs = new List<KeyValuePair<string, long>>(stored.Length);
        for (int i = 0; i < stored.Length; i++) pairs.Add(new KeyValuePair<string, long>(stored[i].Id, stored[i].Ms));
        seen.Load(pairs);
        return seen;
    }

    void CommitFirstSeen(SidebarFirstSeen seen, List<SidebarLibraryEntry> liveAll)
    {
        if (seen.Count > SidebarFirstSeen.Cap / 2)
        {
            _liveIds.Clear();
            SidebarProjection.CollectIds(liveAll, _liveIds);
            seen.PruneTo(_liveIds);
        }

        var pairs = new List<KeyValuePair<string, long>>(seen.Count);
        seen.CopyTo(pairs);
        var dtos = new SidebarFirstSeenDto[pairs.Count];
        for (int i = 0; i < pairs.Count; i++) dtos[i] = new SidebarFirstSeenDto(pairs[i].Key, pairs[i].Value);
        seen.ResetNewCount();
        Sidebar.PublishFirstSeen(dtos);
    }

    static SidebarSourceState StateOf(EdgeState s) => s == EdgeState.Unknown ? SidebarSourceState.Pending : SidebarSourceState.Ready;
    static SidebarSourceState Worst(SidebarSourceState a, SidebarSourceState b) => a > b ? a : b;

    // ─────────────────────────────────── triggers ───────────────────────────────────

    // Peek-only: there is no computation to subscribe (the pane's pump is the subscription), so every trigger is read
    // the same way whether Sync() was called from a pump wake, an explicit Invalidate() or a preference setter.
    SidebarBinderTriggers Read()
    {
        var u = User.Me;
        var doc = Sidebar.Doc;
        return new SidebarBinderTriggers(
            LibraryEpoch: LibraryEpoch(in u),
            PinsVersion: Sidebar.Pins.Version.Peek(),
            HistoryVersion: _logs.HistoryVersion,
            PlayLogRevision: _logs.PlayLogVersion,
            LayoutVersion: Sidebar.LayoutVersion.Peek(),
            FolderVersion: Sidebar.FolderVersion.Peek(),
            OrderVersion: 0,
            CultureEpoch: 0,       // a locale-triggered resort is the engine's concern, out of scope for this SHELL file
            V3State: SidebarBinderTriggers.PackV3((int)doc.Layout, (int)Sidebar.LibraryFilter.Peek(), Sidebar.V3Qualifier.Peek(),
                                                  (int)doc.Library.Sort, doc.Library.Descending),
            SearchHash: SidebarSearch.Normalize(Sidebar.LibrarySearch.Peek()).GetHashCode(StringComparison.Ordinal),
            SourceEpoch: 0,
            PlaybackEpoch: 0L,
            LibraryRows: SidebarLibraryFingerprint.Of(in u, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pinRefs)),
            FeedTables: FeedTables(SidebarFeedDemands.Of(doc)));
    }

    /// <summary>The tables a SHOWN feed reads: the What's New list and its state for New releases; the four entity tables
    /// the resident peek reads for Recently played. Nothing at all for a layout that shows no feed.</summary>
    static long FeedTables(SidebarFeedDemand demand)
    {
        ulong h = SidebarLibraryFingerprint.Seed;
        if ((demand & SidebarFeedDemand.NewReleases) != 0)
        {
            var feed = Notify.Items.Peek();
            h = SidebarLibraryFingerprint.Mix(h, (uint)feed.Items.Count);
            h = SidebarLibraryFingerprint.Mix(h, (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(feed.Items));
            h = SidebarLibraryFingerprint.Mix(h, (uint)Notify.ReleasesState.Peek());
        }
        if ((demand & SidebarFeedDemand.Recent) != 0)
        {
            var scope = Entities.Current;
            h = SidebarLibraryFingerprint.Mix(h, scope.Playlists.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Albums.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Artists.Changed.Peek());
            h = SidebarLibraryFingerprint.Mix(h, scope.Shows.Changed.Peek());
        }
        return (long)h;
    }

    // A REFERENCE-identity-free fold over the account's edges: every relation already carries a monotonic Version, so
    // there is no need for 0.2.9's "hash the published list instance" trick. The scope epoch and the account slot ride
    // along, so a scope switch (login, market, locale) is a rebuild even before its first edge lands.
    static int LibraryEpoch(in User u)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)Entities.Current.Epoch;
            h = h * 31 + u.Slot;
            h = h * 31 + (int)Entities.Current.Edges.Rootlist.Version(u.Slot);
            h = h * 31 + (int)u.EdgeVersion(LibraryEdgeKind.SavedAlbums);
            h = h * 31 + (int)u.EdgeVersion(LibraryEdgeKind.FollowedArtists);
            h = h * 31 + (int)u.EdgeVersion(LibraryEdgeKind.SavedShows);
            h = h * 31 + (int)u.RootlistState;
            h = h * 31 + (int)u.State(LibraryEdgeKind.SavedAlbums);
            h = h * 31 + (int)u.State(LibraryEdgeKind.FollowedArtists);
            h = h * 31 + (int)u.State(LibraryEdgeKind.SavedShows);
            return h;
        }
    }
}

// ── THE PUBLISHED ENTRIES CELL ────────────────────────────────────────────────────────────────────────────────────
// Wave 1's cell, driven by the binder above: the caller-owned rebuild buffer plus the shadow-gated publish that makes
// "nothing actually changed" cost zero re-plans.

public sealed class SidebarEntries
{
    readonly List<SidebarLibraryEntry> _entries = new();
    readonly FluentGpu.Signals.Signal<int> _version = new(0);

    /// <summary>The caller-owned rebuild buffer (P8's <c>into</c>). Fill it, then <see cref="Publish"/>.</summary>
    public List<SidebarLibraryEntry> Buffer => _entries;

    /// <summary>The published entry list, pins-first: the pin band (pin-store order, length <see cref="PinCount"/>),
    /// then the sorted remainder.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Current => _entries;

    public FluentGpu.Signals.IReadSignal<int> Version => _version;

    public FluentGpu.Signals.LoadState State { get; private set; } = FluentGpu.Signals.LoadState.Pending;
    public Exception? Error { get; private set; }

    /// <summary>True while any kind the current filter contributes is still loading — the skeleton gate, per
    /// CONTRIBUTING kind (a pending Shows load must not skeleton the Playlists filter).</summary>
    public bool AnyContributingKindPending { get; private set; }

    /// <summary>Whether the data supports the qualifier chips at all (By you / By Spotify / Mixed).</summary>
    public bool QualifiersAvailable { get; private set; }

    /// <summary>Length of the leading pin band in <see cref="Current"/>.</summary>
    public int PinCount { get; private set; }

    /// <summary>The exact shadow of the last published rebuild — the CONTENT GATE.</summary>
    readonly SidebarEntriesShadow _shadow = new();

    /// <summary>The SECOND content gate, over the binder's FULL flattened projection rather than the published rows:
    /// a collapsed folder's children are excluded from the published buffer by design, so a hydration that changes
    /// only a playlist inside a collapsed folder would never flip the published shadow while the planner (which reads
    /// the full projection) would still need to re-plan it.</summary>
    readonly SidebarEntriesShadow _fullShadow = new();

    /// <summary>Publish a completed rebuild: at most one version bump, never per entry, and none at all when the
    /// rebuild landed on byte-identical content. Both shadows are evaluated unconditionally — either one flipping is
    /// enough to bump <see cref="Version"/>. Returns whether it did — BUG E2's <see cref="SidebarRevisionGate"/>
    /// reads this alongside <see cref="SidebarProjectionBinder.PublishInput"/>'s own return.</summary>
    public bool Publish(FluentGpu.Signals.LoadState state, Exception? error, bool anyContributingKindPending,
                        bool qualifiersAvailable, int pinCount,
                        IReadOnlyList<SidebarLibraryEntry>? fullProjection = null)
    {
        State = state;
        Error = error;
        AnyContributingKindPending = anyContributingKindPending;
        QualifiersAvailable = qualifiersAvailable;
        PinCount = pinCount < 0 ? 0 : pinCount;
        var meta = new SidebarEntriesMeta((int)state, error, anyContributingKindPending, qualifiersAvailable, PinCount);
        bool published = _shadow.Publish(_entries, in meta);
        bool full = fullProjection is not null && _fullShadow.Publish(fullProjection, default);
        if (!published && !full) return false;
        _version.Value = _version.Peek() + 1;
        return true;
    }
}

/// <summary>BUG E2's pure revision-bump decision (`docs/plans/wavee/wavee-0.3-bug-handoff-2026-09-15.md` §7,
/// <see cref="SidebarProjectionBinder.Rebuild"/>): <c>Revision</c> moves iff the rebuild actually published
/// something new on at least one of the two gates — <see cref="SidebarEntries.Publish"/>'s byte-change shadow, or
/// <see cref="SidebarProjectionBinder.PublishInput"/>'s feed-moved shadow. A rebuild that reproduced identical
/// content on BOTH must not bump it: <c>Revision</c> feeds <c>PaneView.PlanDep</c> (Sidebar.UI.cs) and
/// <c>V3Session.ShapeInput</c>'s <c>ViewEpoch</c> (Sidebar.UI.LibraryV3.cs), and each folds it into a `DepKey`/hash
/// that forces a full re-plan (`Sidebar.Plan`) or a full re-group (`View.Build`) on any change — an
/// unconditional bump made every rebuild wake pay for both, twice, regardless of whether anything visible moved.
/// Engine-free and pure so it is unit-testable without a live binder (<c>SidebarRevisionTests</c>).</summary>
public static class SidebarRevisionGate
{
    public static bool ShouldBump(bool entriesChanged, bool inputChanged) => entriesChanged || inputChanged;
}

// ── THE LIBRARY WRITE SEAM (stage B, J1) ─────────────────────────────────────────────────────────────────────────────
// The pane DECIDES every organisation gesture (the slot, the legality, the undo anchors, the destination name) and the
// library mutation seam EXECUTES it. 0.2.9's `WaveeResourceDrop.MoveRootlist` / `DepositTracks` / `FolderActions.*` /
// `PlaylistCreateFlow` lived beside the renderer; Platform/Drag.cs §4 sends the rootlist half here and the deposit half to
// the playlist owner, and neither may re-decide what the pane decided. Set ONCE by the composition root (the owner of
// the Spotify rootlist/playlist writes); a null member makes the pane REFUSE with a sentence, never a silent no-op.

public static partial class Sidebar
{
    /// <summary>The library mutation seam the sidebar's drops, menus and "+" buttons call. Null until installed.</summary>
    public static SidebarLibraryWrites? LibraryWrites { get; set; }

}

/// <summary>What a drop on a "+" create affordance means (G-170).</summary>
public enum SidebarCreateDrop : byte
{
    /// <summary>None of this target's business — the drag is crossing on its way somewhere else. No cue at all.</summary>
    Transparent = 0,
    /// <summary>A new folder holding the dropped rootlist items.</summary>
    NewFolder = 1,
    /// <summary>A new playlist seeded from the dropped tracks.</summary>
    NewPlaylist = 2,
    /// <summary>The drop means something, but the library write it needs has no seam: refuse, with the sentence.</summary>
    Refused = 3,
}

/// <summary>THE "+" DROP RULE (G-170): one decision per create destination, so the cue, the refusal caption and the
/// commit all ask the same question — and a missing seam member REFUSES (a caption while hovering, a toast on a drop)
/// instead of arming a cue for a drop that does nothing. Allocation-free: it runs per frame while a drag is live.</summary>
public static class SidebarCreateDropRules
{
    /// <summary>A rootlist filing gesture: a playlist or folder dragged AS a rootlist item.</summary>
    public static bool IsFiling(DragPayload payload) => payload.RootlistItem && payload.Kind is DragKind.Playlist or DragKind.Folder;

    /// <summary>The PlaylistTree header "+": rootlist items ⇒ a new top-level folder holding them; a track set that is
    /// not itself a rootlist item ⇒ a new playlist from it; anything else crosses.</summary>
    public static SidebarCreateDrop Header(DragPayload payload, SidebarLibraryWrites? writes)
    {
        if (IsFiling(payload)) return writes?.NewFolderWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewFolder;
        if (payload.CanCopyTracks && !payload.RootlistItem)
            return writes?.CreatePlaylistWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewPlaylist;
        return SidebarCreateDrop.Transparent;
    }

    /// <summary>A folder row's "+": rootlist items ⇒ a new sub-folder inside it; a track set crosses (it is on its way
    /// to a playlist row).</summary>
    public static SidebarCreateDrop Folder(DragPayload payload, SidebarLibraryWrites? writes)
        => !IsFiling(payload) ? SidebarCreateDrop.Transparent
            : writes?.NewFolderWith is null ? SidebarCreateDrop.Refused : SidebarCreateDrop.NewFolder;

    /// <summary>A click verb that writes the library (a "+" click, "New playlist in this folder", "Rename folder"):
    /// the refusal to say when its seam member is absent, else <see cref="SidebarDropRefusal.None"/>.</summary>
    public static SidebarDropRefusal Verb(bool seamPresent)
        => seamPresent ? SidebarDropRefusal.None : SidebarDropRefusal.WritesUnavailable;
}

/// <summary>Delegates only (the ActionServices shape): each one is ONE mutation that awaits the server, then announces,
/// toasts and offers Undo itself. UI thread in, marshal back through <c>Sidebar.ToUi</c>.</summary>
public sealed class SidebarLibraryWrites
{
    /// <summary>Move rootlist items (tree order, one batch — whatever the selection size) to
    /// <c>placement</c> of <c>target</c>. <c>destinationName</c> is the toast's subject ("" = "Moved to Your
    /// Library"); <c>undo</c> is the pre-move inverse batch, or null when no anchor resolves (the toast then carries
    /// no Undo).</summary>
    public Action<IReadOnlyList<RootlistItemRef>, RootlistItemRef, RootlistDropPlacement, string, IReadOnlyList<RootlistMove>?>? MoveRootlist;

    /// <summary>Copy a payload's tracks into an editable playlist (uri, display name, payload).</summary>
    public Action<string, string, DragPayload>? DepositTracks;

    /// <summary>The ONE create-playlist flow: numbered name, optimistic row, optionally inside a folder (null = top
    /// level), optionally navigating to it.</summary>
    public Action<string?, bool>? CreatePlaylist;

    /// <summary>Create a playlist seeded from a dropped track set (create, then a silent deposit, then ONE toast).</summary>
    public Action<DragPayload>? CreatePlaylistWith;

    /// <summary>Create a folder (inside the given folder id, null = top level) holding the given items; an empty list is
    /// the plain "New folder" verb (which asks for a name).</summary>
    public Action<string?, IReadOnlyList<RootlistItemRef>>? NewFolderWith;

    /// <summary>Rename a FOLDER (group id, the NEW name). The pane asks for the name (its text prompt, 0.2.9
    /// <c>FolderActions.Rename</c>) and only calls this with a trimmed, non-blank name that differs from the current one
    /// (<see cref="SidebarFolderRename"/>); the seam awaits the rootlist write and announces. A playlist renames through
    /// its registered <c>ActionId.RenamePlaylist</c> verb, never here.</summary>
    public Action<string, string>? RenameFolder;

    /// <summary>Delete a folder (group id, name, direct child count) — the seam confirms first and refuses without an
    /// overlay to confirm in.</summary>
    public Action<string, string, int>? DeleteFolder;

    /// <summary>Resolve an entity's tracks for a deposit (a playlist, an album, a show, a single track), after the drop.</summary>
    public Func<string, CancellationToken, Task<Track[]>>? ResolveTracks;
}
