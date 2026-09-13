// ── Shell/Sidebar.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the file I/O and debounce for sidebar-layout.json, the store/binder half, the four extension data-source hosts
//
// Role: SHELL
// Owner: J
// Wave: 4
// Budget: 2500 lines
// Spec: ch 26 §9.4 (which restates §2's old 800 as ~2,500 by name)
//
// The imperative half of the sidebar platform: the one app-wide service (`public static partial class Sidebar` —
// signals, the design, the width, the folders, the pins, the document and the ONE `Dispatch`), the
// `sidebar-layout.json` store with its named debounce and its atomic write, the projection binder that drives every
// rebuild, and the data sources a contributed section is filled from.
//
// FOUR CONTRACTS.
//
// 1. **`Dispatch` is the one mutation path.** Reduce → if `Changed`: push the pre-image to undo, clear redo, bump
//    `LayoutVersion`, autosave. A REJECTED command does none of those and returns its reason. The customizer never
//    talks to the renderer; it dispatches.
// 2. **The layout file is user data.** Atomic write-then-rename with an fsync and ONE rotated `.bak`; a corrupt file
//    is preserved, never rewritten; an over-budget snapshot is dropped WHOLE and classified, never truncated; a good
//    `.bak` is a full recovery with writes still enabled. The fault classification is what the customizer's banners
//    say, so it is a pure function and it is tested without a disk.
// 3. **A source is an edge read, not a fetching service.** Each `Fill` reads `User.Me`'s edges and entity handles.
//    The two genuinely incremental feeds (Concerts, 30-minute TTL; New releases) keep their own `EnsureFresh` kick —
//    that is the one exemption, and it is stated rather than smuggled.
// 4. **C1/C9.** Everything here runs on the UI thread inside one drain, or Posts to it. The UI thread never blocks on
//    a file: the commit is coalesced onto a timer and the write happens off-thread.
//
// No environment-variable switches (CLAUDE.md): 0.2.9's `WAVEE_SIDEBAR_DISCLOSURE_TRACE` and
// `WAVEE_SIDEBAR_BINDER_DIAG` gates are gone and their lines are always-on `Log` events.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── SIDEBAR SERVICE — design/pane/Classic/V3 state, pins, the Curated document + undo, persistence health ─────────────
//
// Everything `SidebarPreferences` was in 0.2.9, re-expressed as the process-lifetime static service `Sidebar`. UI
// thread only, unsynchronized — the same discipline as every other static service (`Playback`, `Entities`): there are
// no off-thread writers except `Store.Commit`'s pool-thread serialize, whose completion is coalesced and republished
// through `ToUi` (never touches a signal off-thread). `Boot()` seeds every signal from `Platform.Settings` and loads
// the Curated document; `Activate(post)` wires the UI-thread marshaller App.cs owns.
//
// `Dispatch` is the ONE mutation path for the Curated document (§C3.4): reduce → on `Changed`, push the PRE-IMAGE onto
// the undo ring, clear redo, replace the document, bump `LayoutVersion`, autosave — a rejected command changes
// NOTHING, and returns the reason for the customizer's inline message. Every other document mutator (top-bar
// shortcuts, template apply, undo/redo) is sugar over this one call, so they all share the one undo ring and the one
// rejection contract.

public static partial class Sidebar
{
    // ── the UI-thread seam ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller onto the UI thread — the `Playback.ToUi` / `Store.Post` contract: `App.cs` sets it to
    /// `AppHost.Post`, a test leaves it, and the default runs the action inline.</summary>
    public static Action<Action> ToUi { get; set; } = static a => a();

    // ── boot-time wiring ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sidebar-layout.json store. Reference-stable for the process lifetime.</summary>
    public static readonly SidebarLayoutStore Store = SidebarLayoutStore.ForApp();

    static readonly SidebarUndo s_undo = new();                         // decision 6: 50-step pre-image undo/redo
    static readonly object s_persistenceGate = new();
    static readonly Signal<SidebarWriteResult> s_persistenceHealth = new(SidebarWriteResult.Healthy);

    static SidebarCustomLayout s_layout = SidebarCustomLayout.Empty;
    static SidebarWireCarry s_carry = SidebarWireCarry.Empty;           // forward tolerance: unknown sections round-trip
    static SidebarFirstSeenDto[]? s_firstSeen;                          // the playlist added-at PROXY wire array (F.7.5)
    static float s_viewportWidth;                                       // last seen; written by the shell's tier effect
    static bool s_pinNamesDirty;                                        // a TouchPin refresh waiting to ride the next commit
    static bool s_loaded;                                               // Load() ran; before that no mutation may commit
    static bool s_commitPending;
    static SidebarWriteResult s_pendingPersistenceHealth;
    static bool s_hasPendingPersistenceHealth;

    /// <summary>Boot-time initialization, called once by `App.cs` after `Platform.Boot`/`Entities.Boot`: seeds every
    /// signal from settings, loads the Curated document, and wires the pin store's autosave.</summary>
    public static void Boot()
    {
        var design = SidebarDesignInfo.FromInt(Platform.Settings.Get(Platform.Keys.SidebarDesign));
        Design.Value = design;

        // The pane triple for the ACTIVE design, seeded before the first layout: a dragged width is a preference and
        // seeds verbatim, an undragged one takes the design's tier ladder. The viewport is unknown here (no bounds
        // callback yet), so this takes the pre-measure fallback and the shell's tier effect commits the real tier
        // before the first layout without a visible step.
        var pane = SidebarPaneState.Restore(Platform.Settings, design, viewportWidth: 0f);
        Width.Value = pane.Width;
        Collapsed.Value = pane.Collapsed;
        WidthUserSet = pane.WidthUserSet;

        ClassicPinnedOpen.Value = Platform.Settings.Get(Platform.Keys.ClassicPinnedOpen);
        ClassicLibraryOpen.Value = Platform.Settings.Get(Platform.Keys.ClassicLibraryOpen);
        ClassicPlaylistsOpen.Value = Platform.Settings.Get(Platform.Keys.ClassicPlaylistsOpen);

        V3Filter.Value = Platform.Settings.Get(Platform.Keys.V3Filter);
        V3Qualifier.Value = Platform.Settings.Get(Platform.Keys.V3Qualifier);
        V3Sort.Value = Platform.Settings.Get(Platform.Keys.V3Sort);
        V3Desc.Value = Platform.Settings.Get(Platform.Keys.V3Desc);
        V3View.Value = Platform.Settings.Get(Platform.Keys.V3View);
        V3GridSize.Value = Platform.Settings.Get(Platform.Keys.V3GridSize);
        V3Search.Value = "";                                            // SESSION-ONLY, never persisted

        Store.WriteCompleted = OnWriteCompleted;
        LoadDocument();
        Pins.OnChanged = Commit;                                        // every pin mutation is a commit point (#1)

        Log.Info("sidebar", "boot design=" + SidebarDesignInfo.Slug(design) + " fault=" + Fault);
    }

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

    /// <summary>Reactive, redaction-safe persistence health. Load faults seed it before mount; completed writes update
    /// it only through <see cref="Activate"/>'s dispatcher so every signal write stays UI-thread affine.</summary>
    public static IReadSignal<SidebarWriteResult> PersistenceHealth => s_persistenceHealth;

    // ── design ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active design. The ONE signal the sidebar host reads, so a switch remounts the mode component.</summary>
    public static readonly Signal<SidebarDesign> Design = new(SidebarDesign.Classic);

    /// <summary>The active design's width tiers (locked decision 14). <c>Peek</c>, not <c>Value</c>: the tier ladder
    /// effect already subscribes to <see cref="Design"/> explicitly and must not gain a second subscription here.</summary>
    public static (float Narrow, float Mid, float Wide) Tiers => SidebarDesignInfo.Tiers(Design.Peek());

    /// <summary>The shell's tier effect publishes the live viewport width here (a plain field, not a signal — only
    /// <see cref="SwitchDesign"/> and the reset paths read it, and neither is a render).</summary>
    public static void SetViewportWidth(float width)
    {
        if (width > 0f) s_viewportWidth = width;
    }

    /// <summary>Snapshot the outgoing design's live state into its bag + settings, then reseed every live signal from
    /// the incoming design's bag, then flip <see cref="Design"/>. Applies LIVE (no restart). No-op when unchanged.
    /// The selection is persisted LAST, so a crash mid-switch reopens on the OLD design with its state intact.</summary>
    public static void SwitchDesign(SidebarDesign next)
    {
        var cur = Design.Peek();
        if (next == cur) return;

        SidebarPaneState.Snapshot(Platform.Settings, cur, new SidebarPaneSnapshot(Width.Peek(), Collapsed.Peek(), WidthUserSet));
        FlushBagOf(cur);
        Flush();                       // issue any coalesced document write NOW, before the design flips

        var pane = SidebarPaneState.Restore(Platform.Settings, next, s_viewportWidth);
        SeedBagOf(next);
        WidthUserSet = pane.WidthUserSet;
        // A synchronous burst of signal writes on the UI thread coalesces into one frame's layout commit (a signal
        // write is deferred: it marks dependents stale and asks the host for a frame, drained once per frame) — the
        // pane animates one width/collapse step, not two.
        Width.SetIfChanged(pane.Width);
        Collapsed.SetIfChanged(pane.Collapsed);
        Design.Value = next;

        Platform.Settings.Set(Platform.Keys.SidebarDesign, (int)next);
        Log.Info("sidebar", "mode.changed from=" + SidebarDesignInfo.Slug(cur) + " to=" + SidebarDesignInfo.Slug(next));
    }

    /// <summary>Write the design's view-state signals back to its own keys. Classic: the three section flags · V3: the
    /// filter/qualifier/sort/desc/view/size sextet · Curated: the template id (the layout document is already
    /// autosaved per command). <c>V3Search</c> is session-only and is never written.</summary>
    static void FlushBagOf(SidebarDesign design)
    {
        switch (design)
        {
            case SidebarDesign.Classic:
                Platform.Settings.Set(Platform.Keys.ClassicPinnedOpen, ClassicPinnedOpen.Peek());
                Platform.Settings.Set(Platform.Keys.ClassicLibraryOpen, ClassicLibraryOpen.Peek());
                Platform.Settings.Set(Platform.Keys.ClassicPlaylistsOpen, ClassicPlaylistsOpen.Peek());
                break;
            case SidebarDesign.LibraryV3:
                Platform.Settings.Set(Platform.Keys.V3Filter, V3Filter.Peek());
                Platform.Settings.Set(Platform.Keys.V3Qualifier, V3Qualifier.Peek());
                Platform.Settings.Set(Platform.Keys.V3Sort, V3Sort.Peek());
                Platform.Settings.Set(Platform.Keys.V3Desc, V3Desc.Peek());
                Platform.Settings.Set(Platform.Keys.V3View, V3View.Peek());
                Platform.Settings.Set(Platform.Keys.V3GridSize, V3GridSize.Peek());
                break;
            case SidebarDesign.Curated:
                Platform.Settings.Set(Platform.Keys.CuratedTemplateId, s_layout.TemplateId);
                break;
        }
    }

    /// <summary>Reseed the incoming design's view-state signals from its keys. The V3 search box is CLEARED on every
    /// switch (session-only, never persisted, never restored).</summary>
    static void SeedBagOf(SidebarDesign design)
    {
        switch (design)
        {
            case SidebarDesign.Classic:
                ClassicPinnedOpen.SetIfChanged(Platform.Settings.Get(Platform.Keys.ClassicPinnedOpen));
                ClassicLibraryOpen.SetIfChanged(Platform.Settings.Get(Platform.Keys.ClassicLibraryOpen));
                ClassicPlaylistsOpen.SetIfChanged(Platform.Settings.Get(Platform.Keys.ClassicPlaylistsOpen));
                break;
            case SidebarDesign.LibraryV3:
                V3Filter.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3Filter));
                V3Qualifier.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3Qualifier));
                V3Sort.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3Sort));
                V3Desc.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3Desc));
                V3View.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3View));
                V3GridSize.SetIfChanged(Platform.Settings.Get(Platform.Keys.V3GridSize));
                break;
        }
        V3Search.SetIfChanged("");
    }

    // ── pane state (ACTIVE design) ──────────────────────────────────────────────────────────────────────────────────
    // The pane clamp is ONE pair — SidebarPaneBounds.NavPaneMinW/MaxW (180/460) — and every writer below clamps
    // through it, directly or via SidebarPaneState; the tier ladder stops applying once WidthUserSet latches.

    /// <summary>The pane's expanded width. The shell BINDS this signal — the docked pane and the narrow drawer share
    /// it. <see cref="SwitchDesign"/> writes a new VALUE, never a new signal, so every existing binding stays live.</summary>
    public static readonly Signal<float> Width = new(300f);

    /// <summary>The user's collapse PREFERENCE — never "presented compact" (that is <c>narrowShell ∨ Collapsed</c>,
    /// the shell's own derived signal).</summary>
    public static readonly Signal<bool> Collapsed = new(false);

    /// <summary>True once a committed seam drag pinned the ACTIVE design's width. While false that design's width
    /// follows its tier ladder; once true nothing but another drag may write it. Per design — pinning V3's width does
    /// not freeze Classic's ladder. A plain property, not a signal: only <see cref="SwitchDesign"/> and the reset
    /// paths read it, and neither is a render.</summary>
    public static bool WidthUserSet { get; private set; }

    /// <summary>DRAG PEEK — TRANSIENT, never persisted, never a write to <see cref="Collapsed"/>: a collapsed sidebar
    /// is PRESENTED expanded for the rest of one drag once the pointer dwells on the rail. Two readers must agree: the
    /// pane decides its own presentation, and the shell clips the column's width off the SAME signal.</summary>
    public static readonly Signal<bool> DragPeek = new(false);

    /// <summary>Drag-commit edge: clamp + persist the width AND latch <see cref="WidthUserSet"/>, for the active
    /// design only. The grip's own moved-gate still decides whether this is called at all — a zero-movement click on
    /// the seam is not a width preference.</summary>
    public static void CommitWidthDrag(float width)
    {
        var design = Design.Peek();
        float clamped = SidebarPaneState.CommitWidth(Platform.Settings, design, width);
        WidthUserSet = true;
        Width.SetIfChanged(clamped);
        Platform.Settings.Set(Platform.Keys.SidebarCollapsed(SidebarDesignInfo.Slug(design)), Collapsed.Peek());
    }

    /// <summary>Collapse toggle: persist <see cref="Collapsed"/> for the active design. NEVER touches
    /// <see cref="WidthUserSet"/> — collapsing the pane is not a width choice.</summary>
    public static void SetCollapsed(bool collapsed)
    {
        Collapsed.SetIfChanged(collapsed);
        Platform.Settings.Set(Platform.Keys.SidebarCollapsed(SidebarDesignInfo.Slug(Design.Peek())), collapsed);
    }

    /// <summary>The responsive tier ladder's ONLY writer. Clamped through the one pane pair; silently no-ops once the
    /// active design's width is pinned.</summary>
    public static void SetResponsiveWidth(float width)
    {
        if (WidthUserSet) return;
        var design = Design.Peek();
        float clamped = Math.Clamp(width, SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW);
        Width.SetIfChanged(clamped);
        Platform.Settings.Set(Platform.Keys.SidebarWidth(SidebarDesignInfo.Slug(design), Tiers.Narrow), clamped);
    }

    /// <summary>"Reset width": drop the active design's user-set latch and re-seed from its tier ladder, handing the
    /// width back to the responsive effect.</summary>
    public static void ResetWidth()
    {
        var pane = SidebarPaneState.ResetWidth(Platform.Settings, Design.Peek(), s_viewportWidth);
        WidthUserSet = false;
        Width.SetIfChanged(pane.Width);
    }

    // ── Classic bag ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly Signal<bool> ClassicPinnedOpen = new(true);
    public static readonly Signal<bool> ClassicLibraryOpen = new(true);
    public static readonly Signal<bool> ClassicPlaylistsOpen = new(true);

    /// <summary>Toggle one of Classic's three sections: writes the signal AND the setting, so the docked pane and the
    /// narrow drawer (two independent mounts) agree and the state survives a design round-trip.</summary>
    public static void SetClassicSection(ClassicSection section, bool open)
    {
        switch (section)
        {
            case ClassicSection.Pinned:
                ClassicPinnedOpen.SetIfChanged(open);
                Platform.Settings.Set(Platform.Keys.ClassicPinnedOpen, open);
                break;
            case ClassicSection.Library:
                ClassicLibraryOpen.SetIfChanged(open);
                Platform.Settings.Set(Platform.Keys.ClassicLibraryOpen, open);
                break;
            case ClassicSection.Playlists:
                ClassicPlaylistsOpen.SetIfChanged(open);
                Platform.Settings.Set(Platform.Keys.ClassicPlaylistsOpen, open);
                break;
        }
    }

    // ── Library V3 bag ──────────────────────────────────────────────────────────────────────────────────────────────
    // Ints, not the enums: the persisted form is an int and the chip/flyout rows bind straight to them. Cast at the
    // read site: (SidebarV3Filter)V3Filter.Value.

    public static readonly Signal<int> V3Filter = new(0);
    public static readonly Signal<int> V3Qualifier = new(0);
    public static readonly Signal<int> V3Sort = new(0);
    public static readonly Signal<bool> V3Desc = new(false);
    public static readonly Signal<int> V3View = new(1);
    public static readonly Signal<int> V3GridSize = new(1);

    /// <summary>The library-only search text. SESSION-ONLY: never persisted, never restored, cleared on every design
    /// switch — a relaunch reopening a stale, unfocused field is exactly what NOT persisting fixes.</summary>
    public static readonly Signal<string> V3Search = new("");

    public static void SetV3Filter(int v) { V3Filter.SetIfChanged(v); Platform.Settings.Set(Platform.Keys.V3Filter, v); }
    public static void SetV3Qualifier(int v) { V3Qualifier.SetIfChanged(v); Platform.Settings.Set(Platform.Keys.V3Qualifier, v); }
    public static void SetV3View(int view) { V3View.SetIfChanged(view); Platform.Settings.Set(Platform.Keys.V3View, view); }
    public static void SetV3GridSize(int size) { V3GridSize.SetIfChanged(size); Platform.Settings.Set(Platform.Keys.V3GridSize, size); }

    /// <summary>Sort + direction commit as a pair (one flyout interaction). <c>V3Desc</c> is ignored while the sort is
    /// <c>Custom</c> — the direction affordance is hidden there — but the stored value is preserved.</summary>
    public static void SetV3Sort(int sort, bool desc)
    {
        V3Sort.SetIfChanged(sort);
        Platform.Settings.Set(Platform.Keys.V3Sort, sort);
        if (sort == (int)SidebarV3Sort.Custom) return;
        V3Desc.SetIfChanged(desc);
        Platform.Settings.Set(Platform.Keys.V3Desc, desc);
    }

    // ── the local V3 custom order (a view overlay; explicit resource drops may also mutate the rootlist) ──────────────

    static readonly List<string> s_v3CustomOrder = new();
    static readonly Dictionary<string, int> s_v3OrderRank = new(StringComparer.Ordinal);
    static readonly Signal<int> s_v3OrderVersion = new(0);

    /// <summary>Entry/pin ids in the user's order (Playlists filter only). Entries absent from this list sort after it
    /// in projection order and stay there stably.</summary>
    public static IReadOnlyList<string> V3CustomOrder => s_v3CustomOrder;

    /// <summary>Bumped whenever <see cref="V3CustomOrder"/> changes — the render/sort dep.</summary>
    public static IReadSignal<int> V3OrderVersion => s_v3OrderVersion;

    /// <summary>True when a local reorder is meaningful at all: only the Playlists filter with the Custom sort.</summary>
    public static bool CanReorderV3 => V3Filter.Peek() == (int)SidebarV3Filter.Playlists
                                     && V3Sort.Peek() == (int)SidebarV3Sort.Custom;

    /// <summary>Rank of an id in the stored order, or <see cref="int.MaxValue"/> when unranked (an unknown id sorts
    /// last, stably, without a fabricated position).</summary>
    public static int V3RankOf(string? id)
        => id is not null && s_v3OrderRank.TryGetValue(id, out int r) ? r : int.MaxValue;

    /// <summary>Commit a user reorder (drag end / keyboard drop). Persists the document.</summary>
    public static void SetV3CustomOrder(IReadOnlyList<string>? orderedIds)
    {
        s_v3CustomOrder.Clear();
        s_v3OrderRank.Clear();
        if (orderedIds is not null)
            for (int i = 0; i < orderedIds.Count; i++)
            {
                string id = orderedIds[i];
                if (string.IsNullOrEmpty(id) || s_v3OrderRank.ContainsKey(id)) continue;
                s_v3OrderRank[id] = s_v3CustomOrder.Count;
                s_v3CustomOrder.Add(id);
            }
        s_v3OrderVersion.Value = s_v3OrderVersion.Peek() + 1;
        Commit();
    }

    // ── V3 folder expansion (an unbounded id set ⇒ the document, not settings) ──────────────────────────────────────

    static readonly HashSet<string> s_expandedFolders = new(StringComparer.Ordinal);
    static readonly Signal<int> s_folderVersion = new(0);

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
        // also snapshot + serialize the whole document. The renderer drains this on the next frame.
        s_commitPending = true;
    }

    public static void ToggleFolder(string? folderId) => SetFolderExpanded(folderId, !IsFolderExpanded(folderId));

    /// <summary>Issue a write a coalescing mutator deferred. Idempotent, and a no-op when nothing is pending.</summary>
    public static void FlushPendingCommit()
    {
        if (!s_commitPending) return;
        Commit();
    }

    // ── pins (SHARED across all three designs) ──────────────────────────────────────────────────────────────────────

    /// <summary>The one pin list, shared by every design (locked decision 4). Unlimited — no cap, no eviction.</summary>
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
    /// next commit — never commits alone. Called by the projection, never by rows.</summary>
    public static void TouchPin(string? pinId, string? name)
    {
        if (Pins.Touch(pinId, name)) s_pinNamesDirty = true;
    }

    // ── the entry projection cell (owned here; not this section — see SidebarEntries) ──────────────────────────────

    /// <summary>The unified projection the V3 and Curated modes read — one projection, shared by the docked pane and
    /// the drawer. Rebuilt by <see cref="Binder"/> through <c>SidebarProjection.Build</c> + <c>Entries.Publish</c>.</summary>
    public static readonly SidebarEntries Entries = new();

    /// <summary>The ONE driver of <see cref="Entries"/>, set right after both exist. Null in a headless/unit context,
    /// where <see cref="Entries"/> simply stays empty.</summary>
    public static SidebarProjectionBinder? Binder { get; internal set; }

    // ── the Curated layout + editor ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>PHASE 2 / Decision B — the live "customize" session, shared by the docked pane (the canvas) and the
    /// companion page: two siblings in the tree that reach the shared state through this static service instead of a
    /// context either owns. Session-only, never persisted — which section is expanded is editor ergonomics, not
    /// document state.</summary>
    public static readonly SidebarEditSession Edit = new();

    /// <summary>The current Curated document — an immutable snapshot, replaced wholesale. The live pane and the
    /// customizer both render from it, so there is exactly one document and one version signal.</summary>
    public static SidebarCustomLayout Layout => s_layout;

    public static IReadSignal<int> LayoutVersion => s_layoutVersion;
    static readonly Signal<int> s_layoutVersion = new(0);

    /// <summary>Apply one editor command — the single mutation entry point (§C3.4). Runs the pure reducer; on
    /// <c>Changed</c> it pushes the PRE-IMAGE onto the undo ring, clears redo, replaces <see cref="Layout"/>, bumps
    /// <see cref="LayoutVersion"/> and autosaves. A rejected command (unknown section id, out-of-range index, no-op)
    /// changes NOTHING: no undo push, no version bump, no commit. Returns why, for the customizer's inline message.</summary>
    public static SidebarRejectReason Dispatch(SidebarCommand command)
    {
        var before = s_layout;
        var result = SidebarLayoutReducer.Apply(before, command, PinKeySet());
        if (!result.Changed)
        {
            if (result.Reason is not SidebarRejectReason.None and not SidebarRejectReason.NoChange)
                Log.Warn("sidebar", "customizer.command.rejected command=" + command.LabelLocKey + " reason=" + result.Reason);
            return result.Reason;
        }

        s_undo.Push(before, command);                    // records the pre-image AND clears redo
        s_layout = result.Layout;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Commit();
        return SidebarRejectReason.None;
    }

    /// <summary>Foundation's F.2.2 name for <see cref="Dispatch"/>, kept as the documented alias.</summary>
    public static void ApplyCurated(SidebarCommand command) => Dispatch(command);

    // ── the shell TOP BAR band ──────────────────────────────────────────────────────────────────────────────────────
    // Sugar only: every mutation is an ordinary SidebarCommand through Dispatch, so the band gets the SAME undo ring,
    // rejection contract, LayoutVersion bump and autosave as every sidebar edit.

    /// <summary>What the shell's shortcut band renders: the authored list, or the built-in default (Home) when the
    /// user has never customized it. Never null, never empty-by-accident. Read together with
    /// <see cref="LayoutVersion"/> inside a render — that read is the subscription.</summary>
    public static IReadOnlyList<SidebarItemSpec> TopBar => s_layout.EffectiveTopBar;

    /// <summary>True when the band is at <see cref="SidebarLayoutReducer.MaxTopBarItems"/> — the customizer greys its
    /// "add" affordance off this rather than discovering the cap through a rejection.</summary>
    public static bool TopBarFull => TopBar.Count >= SidebarLayoutReducer.MaxTopBarItems;

    /// <summary>Append (default) or insert a shortcut. Returns the rejection reason, or <c>None</c> on success.</summary>
    public static SidebarRejectReason AddTopBarShortcut(SidebarItemSpec item, int index = -1)
        => Dispatch(new AddTopBarItem(item, index < 0 ? TopBar.Count : index));

    /// <summary>Reorder the band. <paramref name="toIndex"/> is interpreted after the removal (the Reorderable contract).</summary>
    public static SidebarRejectReason MoveTopBarShortcut(int fromIndex, int toIndex)
        => Dispatch(new MoveTopBarItem(fromIndex, toIndex));

    /// <summary>Remove a shortcut by item id. Undo re-inserts at its former index — callers snapshot the item + index
    /// BEFORE calling.</summary>
    public static SidebarRejectReason RemoveTopBarShortcut(string itemId) => Dispatch(new RemoveTopBarItem(itemId));

    /// <summary>Index of a tile in the effective band, or -1 — the "restore at its former position" input for the
    /// undo toast.</summary>
    public static int TopBarIndexOf(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return -1;
        var band = TopBar;
        for (int i = 0; i < band.Count; i++)
            if (string.Equals(band[i].Id, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Cached (pin id + pin uri) set for the reducer's lazy Pinned-override prune (§C1.6) — an override may
    /// be keyed either way. Rebuilt only when the pin store's version moves; Peek so Dispatch never subscribes.</summary>
    static IReadOnlySet<string> PinKeySet()
    {
        int v = Pins.Version.Peek();
        if (s_pinKeySet is null || s_pinKeySetVersion != v)
        {
            var set = s_pinKeySet ??= new HashSet<string>(StringComparer.Ordinal);
            set.Clear();
            var items = Pins.Items;
            for (int i = 0; i < items.Count; i++)
            {
                set.Add(items[i].Id);
                if (items[i].Uri is { Length: > 0 } uri) set.Add(uri);
            }
            s_pinKeySetVersion = v;
        }
        return s_pinKeySet;
    }
    static HashSet<string>? s_pinKeySet;
    static int s_pinKeySetVersion = -1;

    /// <summary>Replace the layout with a named template's sections — an ordinary single-step undoable command.</summary>
    public static SidebarRejectReason ApplyTemplateId(string templateId) => Dispatch(new ApplyTemplate(templateId));

    public static bool CanUndo => s_undo.CanUndo;
    public static bool CanRedo => s_undo.CanRedo;

    /// <summary>Loc KEY of the step Undo/Redo would take ("Undo: Add section"), for the tooltip + a11y announcement.</summary>
    public static string? UndoLabel => s_undo.UndoLabelLocKey;
    public static string? RedoLabel => s_undo.RedoLabelLocKey;

    /// <summary>An undo is itself autosaved — closing the app after one keeps the undone state (§C3.4 step 4).</summary>
    public static void Undo()
    {
        if (!s_undo.TryUndo(s_layout, out var restored, out _)) return;
        s_layout = restored;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Commit();
    }

    public static void Redo()
    {
        if (!s_undo.TryRedo(s_layout, out var restored, out _)) return;
        s_layout = restored;
        s_layoutVersion.Value = s_layoutVersion.Peek() + 1;
        Commit();
    }

    // ── document health ─────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Non-<c>None</c> ⇒ the built-in Curated default is loaded in memory, the unreadable file is untouched,
    /// and every write is suppressed. Surfaced ONLY in the customizer as a warning — never a startup toast, never a
    /// dialog: the user must not be interrupted at launch by a preferences problem.</summary>
    public static SidebarLoadFault Fault { get; private set; }

    /// <summary>The exception/validation detail behind <see cref="Fault"/>, for the customizer warning.</summary>
    public static string? FaultDetail { get; private set; }

    /// <summary>Why the LAST write refused the disk (LAYOUT V2's per-section / per-document budgets), or <c>None</c>.
    /// A pure PASSTHROUGH of the store's verdict — persistence classifies, this only surfaces. Unlike
    /// <see cref="Fault"/> it does not latch: the next in-budget commit clears it, because shrinking the oversized
    /// section IS the recovery.</summary>
    public static SidebarSaveFault SaveFault => Store.SaveFault;

    /// <summary>Which section / how many bytes, for that banner. Null when <see cref="SaveFault"/> is <c>None</c>.</summary>
    public static string? SaveFaultDetail => Store.SaveFaultDetail;

    /// <summary>The customizer's "Start fresh": rename the unreadable file aside, clear <see cref="Fault"/>, re-enable
    /// writes, and commit the in-memory document so the file exists again.</summary>
    public static void DiscardCorruptDocument()
    {
        if (Fault == SidebarLoadFault.None) return;
        Store.DiscardCorrupt();
        if (Store.WritesBlocked) return;      // move-aside failed: preserve the original bytes, keep writes suppressed
        Fault = SidebarLoadFault.None;
        FaultDetail = null;
        s_carry = SidebarWireCarry.Empty;      // nothing on disk to be forward-compatible WITH any more
        Commit();
    }

    static void LoadDocument()
    {
        var load = Store.Load();
        Fault = load.Fault;
        FaultDetail = load.Detail;
        s_loaded = true;
        if (load.Fault != SidebarLoadFault.None)
        {
            s_persistenceHealth.Value = new SidebarWriteResult(
                false,
                load.Fault switch
                {
                    SidebarLoadFault.Corrupt => SidebarPersistenceFault.Corrupt,
                    SidebarLoadFault.TooNew => SidebarPersistenceFault.TooNew,
                    SidebarLoadFault.Unreadable => SidebarPersistenceFault.Unreadable,
                    _ => SidebarPersistenceFault.None,
                },
                0, 0, load.Detail);
        }

        string storedTemplate = Platform.Settings.Get(Platform.Keys.CuratedTemplateId);
        if (load.Fault != SidebarLoadFault.None || load.Doc is null)
        {
            // A first run is NOT a fault (Doc null + Fault None). Both paths load the built-in default in memory; the
            // only difference is whether writes are suppressed, which the store itself enforces.
            s_layout = SidebarLayoutDefaults.LayoutOf(storedTemplate);
            return;
        }

        Pins.LoadFrom(PinsFromDto(load.Doc.Pins));
        SetV3StateFromDto(load.Doc.V3);
        // ReadCurated never throws and never DROPS a section whose kind this build does not know: it moves those
        // into the carry, which WriteCurated re-emits at their original index (locked decision 8: preserve, don't
        // destroy).
        var read = SidebarLayoutWire.ReadCurated(load.Doc.Curated);
        s_carry = read.Carry;
        s_carry.CaptureDoc(load.Doc);                                  // envelope-level unknown members ride the carry
        s_layout = load.Doc.Curated is null || read.Layout.Sections.Count == 0
            ? SidebarLayoutDefaults.LayoutOf(storedTemplate)
            : read.Layout;
        // The top-bar band lives on the ENVELOPE (one global list, like the pins), so it is folded in AFTER the
        // curated payload — including on the "curated payload was empty" path above.
        s_layout = s_layout with { TopBar = SidebarLayoutWire.ReadTopBar(load.Doc.TopBar, s_carry) };
    }

    // ── pins ⇄ wire. The pin RECORD is app-side; the enum⇄string/legacy-int TABLES live on SidebarLayoutWire ──────────
    //
    // Read prefers SidebarPinDto.EntityKind (the string form); a document written before the SidebarPinKind→
    // SidebarEntryKind unification has no EntityKind, so that pin falls back to the legacy int. Either arm that fails
    // to resolve DROPS the pin rather than guessing a kind — the pin's Kind is load-bearing for rendering/navigation.
    static List<SidebarPin>? PinsFromDto(SidebarPinDto[]? dto)
    {
        if (dto is null || dto.Length == 0) return null;
        var list = new List<SidebarPin>(dto.Length);
        for (int i = 0; i < dto.Length; i++)
        {
            var d = dto[i];
            if (d is null || string.IsNullOrEmpty(d.Id)) continue;   // an id-less row has no identity — not a pin
            SidebarEntryKind kind;
            if (!string.IsNullOrEmpty(d.EntityKind))
            {
                if (!SidebarLayoutWire.TryParsePinKind(d.EntityKind, out kind))
                {
                    Log.Warn("sidebar", "pin.unknown_kind pin_id=" + d.Id + " kind=" + d.EntityKind);
                    continue;
                }
            }
            else if (!SidebarLayoutWire.TryLegacyPinKind(d.Kind, out kind))
            {
                Log.Warn("sidebar", "pin.unknown_legacy_kind pin_id=" + d.Id + " kind=" + d.Kind);
                continue;
            }
            list.Add(new SidebarPin(d.Id!, kind, d.Uri ?? "", d.Name ?? "", d.AddedAtMs));
        }
        return list;
    }

    // Write BOTH the new string and the legacy int on every save (preserve-don't-destroy for a downgrade).
    static SidebarPinDto[]? PinsToDto(SidebarPinStore pins)
    {
        if (pins.Count == 0) return null;
        var arr = new SidebarPinDto[pins.Count];
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            arr[i] = new SidebarPinDto
            {
                Id = p.Id,
                Kind = SidebarLayoutWire.LegacyPinKindInt(p.Kind),
                EntityKind = SidebarLayoutWire.PinKindName(p.Kind),
                Uri = p.Uri, Name = p.Name, AddedAtMs = p.AddedAtMs,
            };
        }
        return arr;
    }

    /// <summary>The projection publishes new first-observation stamps here after a rebuild that produced any (commit
    /// point #9, at most one commit per rebuild). Passing null leaves the stored map untouched.</summary>
    public static void PublishFirstSeen(SidebarFirstSeenDto[]? stamps)
    {
        if (stamps is null) return;
        s_firstSeen = stamps;
        Commit();
    }

    /// <summary>The stored first-seen stamps, for the projection to seed its map from at build time.</summary>
    public static SidebarFirstSeenDto[]? FirstSeen => s_firstSeen;

    static void SetV3StateFromDto(SidebarV3Dto? v3)
    {
        s_v3CustomOrder.Clear();
        s_v3OrderRank.Clear();
        s_expandedFolders.Clear();
        s_firstSeen = null;
        if (v3 is null) return;
        s_firstSeen = v3.FirstSeen;
        if (v3.CustomOrder is { } order)
            for (int i = 0; i < order.Length; i++)
            {
                string? id = order[i];
                if (string.IsNullOrEmpty(id) || s_v3OrderRank.ContainsKey(id)) continue;
                s_v3OrderRank[id] = s_v3CustomOrder.Count;
                s_v3CustomOrder.Add(id);
            }
        if (v3.ExpandedFolders is { } folders)
            for (int i = 0; i < folders.Length; i++)
                if (!string.IsNullOrEmpty(folders[i])) s_expandedFolders.Add(folders[i]!);
    }

    // ── persistence ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Snapshot the whole document on the UI thread and hand it to the store, which serializes + writes on
    /// the pool and coalesces a burst into one file write (its monotonic commit sequence: a queued write aborts when
    /// a newer snapshot arrives). That store-side coalescing is why no burst timer is needed here.</summary>
    static void Commit()
    {
        if (!s_loaded || Fault != SidebarLoadFault.None) return;
        s_commitPending = false;   // any commit absorbs a coalesced one
        s_pinNamesDirty = false;
        Store.Commit(BuildSnapshot());
    }

    /// <summary>Force any pending write to be issued now (called by <see cref="SwitchDesign"/> and by the customizer
    /// on close). Never blocks on the pool write. Also the one path that flushes a name-cache refresh, which by
    /// contract never commits on its own.</summary>
    public static void Flush()
    {
        if (!s_pinNamesDirty && !s_commitPending) return;
        Commit();
    }

    /// <summary>The whole document, snapshotted on the UI thread. <c>UpdatedAtMs</c>/<c>AppVersion</c> are stamped by
    /// <see cref="SidebarLayoutStore.Commit"/>, not here.</summary>
    static SidebarLayoutDocDto BuildSnapshot()
    {
        var doc = new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Pins = PinsToDto(Pins),
            V3 = new SidebarV3Dto
            {
                CustomOrder = s_v3CustomOrder.Count > 0 ? s_v3CustomOrder.ToArray() : null,
                ExpandedFolders = ExpandedFolderArray(),
                FirstSeen = s_firstSeen,
            },
            Curated = SidebarLayoutWire.WriteCurated(s_layout, s_carry),
            TopBar = SidebarLayoutWire.WriteTopBar(s_layout.TopBar, s_carry),
        };
        s_carry.ReattachDoc(doc);      // an envelope member a NEWER build added is re-emitted, never dropped
        return doc;
    }

    static string[]? ExpandedFolderArray()
    {
        if (s_expandedFolders.Count == 0) return null;
        var arr = new string[s_expandedFolders.Count];
        int i = 0;
        foreach (string id in s_expandedFolders) arr[i++] = id;
        return arr;
    }
}

/// <summary>
/// What the customizer's generated control set needs from whoever is hosting it — the re-hosting seam that lets
/// `SidebarPropertyPanel` serve both the pane's per-section options popover and the full-page customizer without
/// being forked. The controls' bodies are untouched; only the type of the reference-stable holder changes.
///
/// <para>0.2.9's <c>Prefs</c>/<c>Acts</c>/<c>Registry</c>/<c>OverlaySvc</c> are dropped: <c>Prefs</c> is superseded by
/// the static <see cref="Sidebar"/> service itself (there is no longer an instance to hold); <c>Acts</c>
/// (<c>ActionServices</c>) and <c>Registry</c> (<c>WaveeExtensionRegistry</c>) are owner I's action platform, not yet
/// ported (§10 — never reach into an empty stub); <c>OverlaySvc</c> is an engine type CORE/the static service may not
/// reference. A host that needs any of them resolves its own, through whatever context it already has.</para></summary>
public interface ISidebarEditHost
{
    /// <summary>Bumped whenever the reducer's answer changes, INCLUDING "no". A rejected command does not bump
    /// <c>LayoutVersion</c>, so without this a controlled row would never re-render after a rejection and would keep
    /// showing the value the user picked while the document still held the old one.</summary>
    Signal<int> RejectEpoch { get; }

    /// <summary>The section the property surface is editing.</summary>
    Signal<string?> Selected { get; }

    /// <summary>The item inside it the item block is editing.</summary>
    Signal<string?> SelectedItem { get; }

    /// <summary>The ONE mutation path: <c>Sidebar.Dispatch</c> → reducer → undo pre-image → LayoutVersion → autosave,
    /// with the rejection surfaced through <see cref="RejectEpoch"/>.</summary>
    SidebarRejectReason Dispatch(SidebarCommand command);

    /// <summary>The same path with the shortcut band's rejection vocabulary. Command ROUTING is never re-decided at a
    /// call site — this only picks the message.</summary>
    SidebarRejectReason DispatchTopBar(SidebarCommand command);

    void Select(string? sectionId);
}

/// <summary>
/// The live "customize" session shared by the canvas and the companion page. Session-only and never persisted —
/// which section is expanded is editor ergonomics, not document state.
///
/// <para><b>Who writes what.</b> The COMPANION PAGE owns <see cref="ShowContents"/> and resets the ergonomics on the
/// way in and out. The CANVAS owns <see cref="Expanded"/> (tapping a card) and <see cref="OptionsSection"/> (a card's
/// "…" popover). Nobody reads the other's fields directly — both read <see cref="Read"/>.</para>
///
/// <para><b>What ARMS the canvas is not here.</b> There is deliberately no <c>Active</c> flag: the customizer page is
/// a KeepAlive destination, so navigating away PARKS rather than unmounts it, and a flag with no reliable clearer
/// would leave structural drag armed on the live sidebar for the rest of the session. Arming is DERIVED, by whoever
/// hosts the canvas, from the one fact that cannot go stale: whether the customize route is the active destination.</para>
/// </summary>
public sealed class SidebarEditSession : ISidebarEditHost
{
    /// <summary>The ONE section whose real rows are revealed under its card (null = every section is a card).</summary>
    public readonly Signal<string?> Expanded = new(null);

    /// <summary>The companion's "Show section contents" switch: reveal every visible section's body at once.</summary>
    public readonly Signal<bool> ShowContents = new(false);

    /// <summary>The section whose options popover is open — also the property surface's subject, which is why
    /// <see cref="ISidebarEditHost.Selected"/> IS this signal rather than a second one that could drift from it.</summary>
    public readonly Signal<string?> OptionsSection = new(null);

    readonly Signal<string?> _selectedItem = new(null);
    readonly Signal<int> _rejectEpoch = new(0);
    bool _rejected;

    /// <summary>The loc KEY of the last rejection's inline message ("sidebar.customizer.rejectSectionCap"), or null
    /// when the last command succeeded or none has run yet. The 0.3 honest improvement over 0.2.9's mute rejection:
    /// a control that snaps back now has a reason a surface can show, not just an epoch to react to.</summary>
    public string? LastReject { get; private set; }

    Signal<int> ISidebarEditHost.RejectEpoch => _rejectEpoch;
    Signal<string?> ISidebarEditHost.Selected => OptionsSection;
    Signal<string?> ISidebarEditHost.SelectedItem => _selectedItem;

    /// <summary>The session as a VALUE for the renderer. Reading it touches <see cref="Expanded"/> and
    /// <see cref="ShowContents"/> — exactly the subscription the pane wants. <see cref="OptionsSection"/> is read
    /// with <c>Peek</c>: opening a popover changes no planned row, and subscribing the pane to it would re-plan the
    /// whole canvas on every open.</summary>
    public SidebarEditState Read() => new(Expanded.Value, ShowContents.Value, OptionsSection.Peek());

    /// <summary>Reset the ergonomics — never the document. Called by the companion page on the way in and out.</summary>
    public void ResetErgonomics()
    {
        Expanded.SetIfChanged(null);
        OptionsSection.SetIfChanged(null);
        _selectedItem.SetIfChanged(null);
    }

    /// <summary>Expand this section, or collapse it when it is already the expanded one.</summary>
    public void ToggleExpanded(string sectionId)
    {
        if (sectionId.Length == 0) return;
        bool open = string.Equals(Expanded.Peek(), sectionId, StringComparison.Ordinal);
        Expanded.Value = open ? null : sectionId;
    }

    SidebarRejectReason ISidebarEditHost.Dispatch(SidebarCommand command) => Apply(command);
    SidebarRejectReason ISidebarEditHost.DispatchTopBar(SidebarCommand command) => Apply(command);

    void ISidebarEditHost.Select(string? sectionId)
    {
        OptionsSection.SetIfChanged(sectionId);
        _selectedItem.SetIfChanged(null);
    }

    /// <summary>Dispatch and publish the reducer's answer. The epoch moves on a rejection AND on the first success
    /// after one, so a control that snapped to the user's pick snaps back to the document exactly once.
    /// <see cref="LastReject"/> is updated the same way: set on a rejection, cleared on the next success.</summary>
    public SidebarRejectReason Apply(SidebarCommand command)
    {
        var reason = Sidebar.Dispatch(command);
        bool rejected = reason != SidebarRejectReason.None;
        if (rejected || _rejected) _rejectEpoch.Value = _rejectEpoch.Peek() + 1;
        _rejected = rejected;
        LastReject = rejected ? RejectLocKey(reason) : null;
        return reason;
    }

    /// <summary>The customizer's inline-message loc key for a rejection reason. Raw dotted keys (verified present in
    /// <c>assets/loc/en-US.json</c> under <c>sidebar.customizer.*</c>) rather than a generated <c>Strings</c> member —
    /// this class does not know whether the codegen's casing matches.</summary>
    static string? RejectLocKey(SidebarRejectReason reason) => reason switch
    {
        SidebarRejectReason.None or SidebarRejectReason.NoChange => "sidebar.customizer.rejectNoChange",
        SidebarRejectReason.SectionCapReached => "sidebar.customizer.rejectSectionCap",
        SidebarRejectReason.DuplicateItem => "sidebar.customizer.rejectDuplicateItem",
        SidebarRejectReason.InvalidIcon => "sidebar.customizer.rejectInvalidIcon",
        SidebarRejectReason.UnknownItem => "sidebar.customizer.rejectUnknownItem",
        SidebarRejectReason.UnknownSection => "sidebar.customizer.rejectUnknownSection",
        SidebarRejectReason.UnknownTemplate => "sidebar.customizer.rejectUnknownTemplate",
        SidebarRejectReason.KindDoesNotAcceptItems => "sidebar.customizer.rejectNoItems",
        SidebarRejectReason.KindNotDuplicable => "sidebar.customizer.rejectNotDuplicable",
        SidebarRejectReason.NestingTooDeep or SidebarRejectReason.KindNotNestable => "sidebar.customizer.rejectNesting",
        SidebarRejectReason.ConfigTooLarge => "sidebar.customizer.rejectConfigTooLarge",
        SidebarRejectReason.ExtensionRefMissing => "sidebar.customizer.rejectExtensionRefMissing",
        _ => null,
    };
}
// ── STORE: sidebar-layout.json persistence, pins, first-seen, navigation recency ──────────────────────────────────────
// The file I/O + a NAMED debounce for the one user-data document (locked decision 8: never deleted on a version bump),
// the ordered pin store (the offline display cache; the User.Me → Pins edge is the live membership, see SidebarPinStore's
// header), the bounded first-observation map (the playlist "added at" proxy) and the navigation-recency lookup that feeds
// the "recently opened" feed only. Every write is coalesced off the UI thread and is atomic; a corrupt file is preserved,
// never rewritten, and an over-budget snapshot is dropped whole and classified rather than truncated.

/// <summary>How a <see cref="SidebarLayoutStore.Load"/> ended. Anything other than <see cref="None"/> means the caller
/// must load the built-in Wavee Curated layout IN MEMORY, leave the file on disk untouched, and suppress every write for
/// the rest of the process. The persistence layer classifies the fault; nothing here decides what the UI shows.</summary>
public enum SidebarLoadFault : byte { None = 0, Corrupt = 1, TooNew = 2, Unreadable = 3 }

/// <summary>The outcome of a load. <c>Doc</c> is null on any fault (and on a first run, which is NOT a fault).</summary>
public readonly record struct SidebarLayoutLoad(SidebarLayoutDocDto? Doc, SidebarLoadFault Fault, string? Detail);

/// <summary>Why the LAST write attempt refused to touch the disk (a budget cap or an I/O failure). The document is never
/// truncated and never partially written: an over-cap snapshot is dropped whole and classified here, so the customizer can
/// tell the user which section to shrink. Recoverable by construction — the next in-budget commit clears it. Append only;
/// <see cref="SidebarLoadFault"/> stays the LOAD-side health enum.</summary>
public enum SidebarSaveFault : byte
{
    None = 0,
    ConfigTooLarge = 1,     // one section's extension config exceeds SidebarExtensionRef.MaxConfigBytes (64 KiB)
    DocumentTooLarge = 2,   // the serialized document exceeds SidebarLayoutStore.MaxDocumentBytes (2 MiB)
    IoFailure = 3,          // serialization / directory / fsync / atomic-replace failure
}

/// <summary>The unified, redaction-safe persistence fault vocabulary the customizer's banners read. Append only.</summary>
public enum SidebarPersistenceFault : byte
{
    None = 0,
    Corrupt = 1,
    TooNew = 2,
    Unreadable = 3,
    IoFailure = 4,
    ConfigTooLarge = 5,
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

/// <summary>Load/validate/fault-classify <c>sidebar-layout.json</c>, plus a coalesced, atomic write with one rotated
/// <c>.bak</c>. Corruption policy is preserve-don't-destroy: an unreadable document is NEVER rewritten, NEVER deleted and
/// NEVER silently replaced — the fault only surfaces in the customizer, never as a startup toast.
///
/// LAYOUT V2 adds two SIZE BUDGETS with the same stance: 64 KiB per section extension config and 2 MiB per document. An
/// over-budget snapshot is refused WHOLE — no temp file, no partial write, the document on disk and its <c>.bak</c>
/// untouched — and classified on <see cref="SaveFault"/>. It never latches: the next in-budget commit clears it, because
/// shrinking the offending section IS the recovery.
///
/// THREADING (C9 — the UI thread never blocks on a file): <see cref="Load"/>, <see cref="Commit"/> and
/// <see cref="DiscardCorrupt"/> are called on the UI thread. <see cref="Commit"/> only stamps the snapshot and (re)arms a
/// <see cref="CommitDebounceMs"/> <see cref="Timer"/> under a state-only lock that is never held during I/O; the timer
/// callback runs on the pool and does the actual write under a SEPARATE gate, so a burst of editor commands (a drag, a
/// resize, a property edit) produces ONE file write <see cref="CommitDebounceMs"/> after the LAST one lands. 0.2.9 coalesced
/// by a last-wins sequence number instead of a real delay; this named timer is the 0.3 house style
/// (<c>Spotify.Connect.cs</c>'s <c>PublishDebounceMs</c>/<c>Timer</c> pair).
public sealed class SidebarLayoutStore
{
    /// <summary>2 = LAYOUT V2 (extension refs, action bindings, query uri sets). v1 upgrades by IDENTITY — see
    /// <see cref="SidebarLayoutMigrations"/> — so an existing document loads unchanged and re-stamps on its next commit.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The whole-document budget. Checked against the SERIALIZED bytes, before any file is touched: an
    /// over-budget snapshot is dropped whole and classified <see cref="SidebarSaveFault.DocumentTooLarge"/>.</summary>
    public const int MaxDocumentBytes = 2 * 1024 * 1024;

    /// <summary>The per-section extension-config budget. Owned by <see cref="SidebarExtensionRef.MaxConfigBytes"/> — the
    /// reducer rejects an over-cap edit up front; this is the second line of defence against a hand-edited document.</summary>
    public const int MaxSectionConfigBytes = SidebarExtensionRef.MaxConfigBytes;

    /// <summary>The coalescing window (named per house style — see <c>Spotify.Connect.cs:199</c>'s
    /// <c>PublishDebounceMs</c>). 0.2.9 stated no delay (it coalesced by sequence number, not by time); 300 ms is short
    /// enough that a save never feels laggy and long enough to fold a whole drag or a burst of property-panel keystrokes
    /// into one write.</summary>
    public const int CommitDebounceMs = 300;

    readonly string _path;

    // State-only gate: guards the pending snapshot, its completion source and the timer handle. Never held during I/O,
    // so Commit() can never block the UI thread on a file (C9).
    readonly Lock _stateGate = new();
    // I/O gate: guards the actual write (and DiscardCorrupt's file moves) so at most one write touches the disk at a time.
    readonly Lock _writeGate = new();
    readonly Lock _resultGate = new();

    Timer? _commitTimer;
    SidebarLayoutDocDto? _pendingSnapshot;
    TaskCompletionSource<bool>? _pendingWrite;   // completes when the CURRENTLY-armed cycle's write lands

    volatile bool _writesBlocked;
    volatile SidebarSaveFault _saveFault;
    volatile string? _saveFaultDetail;

    SidebarWriteResult _lastWriteResult = SidebarWriteResult.Healthy;
    Action<SidebarWriteResult>? _writeCompleted;
    SidebarPersistenceFault _lastReportedWriteFault;

    public SidebarLayoutStore(string path) => _path = path;

    public static SidebarLayoutStore ForApp() => new(DefaultPath());

    /// <summary><c>%LOCALAPPDATA%\Wavee\sidebar-layout.json</c> — beside <c>store.json</c>. A static composition only;
    /// the first write creates the file (<see cref="Platform.LocalFolder"/> creates the directory on demand).</summary>
    public static string DefaultPath() => Path.Combine(Platform.LocalFolder, "sidebar-layout.json");

    public string FilePath => _path;
    public string BakPath => _path + ".bak";
    public string TmpPath => _path + ".tmp";
    public string CorruptPath => _path + ".corrupt";

    /// <summary>True while a fault suppresses every write. Cleared by <see cref="DiscardCorrupt"/>.</summary>
    public bool WritesBlocked => _writesBlocked;

    /// <summary>Why the last write attempt refused the disk, or <c>None</c>. Unlike <see cref="WritesBlocked"/> this does
    /// NOT latch: the next in-budget commit clears it. The config cap is checked synchronously inside <see cref="Commit"/>
    /// (observable the moment it returns); the document-size cap needs the serialized bytes and lands with the debounced
    /// write (observable after <see cref="WaitForWrites"/>).</summary>
    public SidebarSaveFault SaveFault => _saveFault;

    /// <summary>Which section / how many bytes, for the customizer's warning. Null when <see cref="SaveFault"/> is None.</summary>
    public string? SaveFaultDetail => _saveFaultDetail;

    public bool SaveFaulted => _saveFault != SidebarSaveFault.None;

    /// <summary>The most recent completed write attempt. Readable from any thread.</summary>
    public SidebarWriteResult LastWriteResult { get { lock (_resultGate) return _lastWriteResult; } }

    /// <summary>Completion edge for the UI-thread owner. Invoked on the writing (pool) thread — the caller must marshal
    /// before touching a signal.</summary>
    public Action<SidebarWriteResult>? WriteCompleted
    {
        get { lock (_resultGate) return _writeCompleted; }
        set { lock (_resultGate) _writeCompleted = value; }
    }

    /// <summary>Read + validate. NEVER throws. The fault rides on the RESULT; the caller decides what to load.</summary>
    public SidebarLayoutLoad Load()
    {
        // A first run is not a fault: the caller loads the built-in Curated default and the first commit creates the
        // file. An orphaned .bak with no primary is deliberately NOT recovered — without the primary there is no way to
        // tell a rotated backup from a leftover, and a first-run default is the safe answer.
        if (!File.Exists(_path)) return new SidebarLayoutLoad(null, SidebarLoadFault.None, null);

        switch (TryRead(_path, out var doc, out string? primaryDetail))
        {
            case ReadOutcome.Ok:
                return new SidebarLayoutLoad(SidebarLayoutMigrations.Upgrade(doc!), SidebarLoadFault.None, null);

            case ReadOutcome.TooNew:
                // Do NOT read further and do NOT touch the file: a newer build owns it.
                _writesBlocked = true;
                LogLoadFailed(SidebarPersistenceFault.TooNew, primaryDetail);
                return new SidebarLayoutLoad(null, SidebarLoadFault.TooNew, primaryDetail);

            case ReadOutcome.Unreadable:
                _writesBlocked = true;
                LogLoadFailed(SidebarPersistenceFault.Unreadable, primaryDetail);
                return new SidebarLayoutLoad(null, SidebarLoadFault.Unreadable, primaryDetail);
        }

        // Malformed / null / version <= 0 → try the rotated backup with the SAME validation.
        if (File.Exists(BakPath) && TryRead(BakPath, out var bak, out _) == ReadOutcome.Ok)
        {
            // A good backup is a full recovery: writes stay ENABLED and the next commit rewrites the primary.
            Log.Warn("sidebar", "sidebar.layout.recovered: the sidebar layout was recovered from its backup.");
            return new SidebarLayoutLoad(SidebarLayoutMigrations.Upgrade(bak!), SidebarLoadFault.None, "recovered from .bak");
        }

        _writesBlocked = true;
        LogLoadFailed(SidebarPersistenceFault.Corrupt, primaryDetail);
        return new SidebarLayoutLoad(null, SidebarLoadFault.Corrupt, primaryDetail);
    }

    enum ReadOutcome : byte { Ok, Malformed, TooNew, Unreadable }

    static ReadOutcome TryRead(string path, out SidebarLayoutDocDto? doc, out string? detail)
    {
        doc = null; detail = null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "The sidebar layout could not be read (" + ex.GetType().Name + ").";
            return ReadOutcome.Unreadable;
        }

        SidebarLayoutDocDto? parsed;
        try { parsed = JsonSerializer.Deserialize(bytes, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto); }
        catch (Exception ex)   // JsonException, and anything a hostile file can provoke out of the reader
        {
            detail = "The sidebar layout contains invalid data (" + ex.GetType().Name + ").";
            return ReadOutcome.Malformed;
        }

        if (parsed is null) { detail = "The sidebar layout contains no document."; return ReadOutcome.Malformed; }
        if (parsed.Version > CurrentVersion)
        {
            detail = $"Layout version {parsed.Version} is newer than supported version {CurrentVersion}.";
            return ReadOutcome.TooNew;
        }
        // A missing/zero/negative version is NOT accepted as v1: v1 is the first schema that ever shipped, so no real
        // file lacks it, and accepting `{}` as an empty layout would let the very next commit overwrite the user's
        // document with nothing. Treated as malformed → .bak → Curated default, file preserved.
        if (parsed.Version <= 0) { detail = $"The sidebar layout has an invalid version ({parsed.Version})."; return ReadOutcome.Malformed; }

        doc = parsed;
        return ReadOutcome.Ok;
    }

    static void LogLoadFailed(SidebarPersistenceFault fault, string? detail) =>
        Log.Warn("sidebar", $"sidebar.layout.load_failed fault={FaultName(fault)}: the saved file was preserved. {detail}");

    void PublishWriteResult(in SidebarWriteResult result)
    {
        Action<SidebarWriteResult>? completed;
        SidebarPersistenceFault previous;
        lock (_resultGate)
        {
            previous = _lastReportedWriteFault;
            _lastWriteResult = result;
            _lastReportedWriteFault = result.Success ? SidebarPersistenceFault.None : result.Fault;
            completed = _writeCompleted;
        }

        if (!result.Success)
            Log.Warn("sidebar", $"sidebar.layout.save_failed fault={FaultName(result.Fault)} bytes={result.Bytes} elapsedMs={result.ElapsedMs}");
        else if (previous != SidebarPersistenceFault.None)
            Log.Info("sidebar", $"sidebar.layout.save_recovered previousFault={FaultName(previous)} bytes={result.Bytes} elapsedMs={result.ElapsedMs}");

        try { completed?.Invoke(result); }
        catch (Exception ex) { Log.Warn("sidebar", "sidebar.layout.completion_failed", ex); }
    }

    static string FaultName(SidebarPersistenceFault fault) => fault switch
    {
        SidebarPersistenceFault.Corrupt => "corrupt",
        SidebarPersistenceFault.TooNew => "too_new",
        SidebarPersistenceFault.Unreadable => "unreadable",
        SidebarPersistenceFault.IoFailure => "io_failure",
        SidebarPersistenceFault.ConfigTooLarge => "config_too_large",
        SidebarPersistenceFault.DocumentTooLarge => "document_too_large",
        _ => "none",
    };

    /// <summary>Snapshot NOW (stamps + arms the debounce), write <see cref="CommitDebounceMs"/> later on the pool. A
    /// burst of calls inside the window replaces the pending snapshot each time, so only the LAST one is ever written.
    /// No-op while writes are blocked by a fault. Never blocks — only the fast state gate is taken here, never the write
    /// gate (C9).</summary>
    public void Commit(SidebarLayoutDocDto snapshot)
    {
        if (snapshot is null || _writesBlocked) return;

        // LAYOUT V2 cap #1, synchronously: a single section whose extension config is over budget. Cheap (it measures
        // only the config elements) and it must not reach the disk at all, so it is refused before anything is queued.
        if (OversizedConfig(snapshot) is { } tooBig)
        {
            Fault(SidebarSaveFault.ConfigTooLarge, tooBig, elapsedMs: 0);
            return;
        }

        snapshot.Version = CurrentVersion;
        snapshot.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshot.AppVersion ??= AppVersion();

        lock (_stateGate)
        {
            _pendingSnapshot = snapshot;
            if (_pendingWrite is null || _pendingWrite.Task.IsCompleted)
                _pendingWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commitTimer ??= new Timer(static state => ((SidebarLayoutStore)state!).FlushDebounced(), this, Timeout.Infinite, Timeout.Infinite);
            _commitTimer.Change(CommitDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Fire the pending debounced write NOW, skipping the rest of <see cref="CommitDebounceMs"/>. For app
    /// shutdown: a closing process must not lose the last edit to an armed-but-not-yet-fired debounce.</summary>
    public void FlushNow()
    {
        lock (_stateGate) _commitTimer?.Change(0, Timeout.Infinite);
    }

    // The debounce timer's callback — runs on the pool.
    void FlushDebounced()
    {
        SidebarLayoutDocDto? snapshot;
        lock (_stateGate) { snapshot = _pendingSnapshot; _pendingSnapshot = null; }

        if (snapshot is not null) WriteNow(snapshot);

        lock (_stateGate) _pendingWrite?.TrySetResult(true);
    }

    void WriteNow(SidebarLayoutDocDto snapshot)
    {
        long start = Environment.TickCount64;
        SidebarWriteResult completion;
        lock (_writeGate)
        {
            if (_writesBlocked) return;   // a fault landed while this write was waiting out the debounce
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);

                // LAYOUT V2 cap #2: the whole-document budget, measured on the real serialized payload and checked
                // BEFORE the temp file exists. Bailing here leaves the previous good document and its .bak untouched.
                if (bytes.Length > MaxDocumentBytes)
                {
                    string detail = $"Document is {bytes.Length} B, over the {MaxDocumentBytes} B budget.";
                    _saveFault = SidebarSaveFault.DocumentTooLarge;
                    _saveFaultDetail = detail;
                    completion = new SidebarWriteResult(
                        false, SidebarPersistenceFault.DocumentTooLarge, bytes.Length, Environment.TickCount64 - start, detail);
                    goto Complete;
                }

                using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);   // fsync — survive power loss, not just a process crash
                }

                if (File.Exists(_path))
                {
                    // ONE atomic call that installs the new file AND rotates the previous good one into .bak.
                    try { File.Replace(TmpPath, _path, BakPath, ignoreMetadataErrors: true); }
                    catch (Exception)
                    {
                        // Some filesystems (and some network shares) refuse Replace — fall back to copy-then-move.
                        try { File.Copy(_path, BakPath, overwrite: true); } catch (Exception) { }
                        File.Move(TmpPath, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(TmpPath, _path, overwrite: true);   // first write — no .bak is created
                }

                // A write that landed clears a previous budget fault: shrinking the offending section IS the recovery.
                _saveFault = SidebarSaveFault.None;
                _saveFaultDetail = null;
                completion = new SidebarWriteResult(true, SidebarPersistenceFault.None, bytes.Length, Environment.TickCount64 - start, null);
            }
            catch (Exception ex)
            {
                const string safe = "The sidebar layout could not be saved.";
                _saveFault = SidebarSaveFault.IoFailure;
                _saveFaultDetail = safe;
                completion = new SidebarWriteResult(false, SidebarPersistenceFault.IoFailure, 0, Environment.TickCount64 - start, safe);
                Log.Warn("sidebar", "sidebar.layout.write_exception", ex);
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
    Complete:
        PublishWriteResult(completion);
    }

    void Fault(SidebarSaveFault fault, string detail, int bytes = 0, long elapsedMs = 0)
    {
        _saveFault = fault;
        _saveFaultDetail = detail;
        var persistenceFault = fault switch
        {
            SidebarSaveFault.ConfigTooLarge => SidebarPersistenceFault.ConfigTooLarge,
            SidebarSaveFault.DocumentTooLarge => SidebarPersistenceFault.DocumentTooLarge,
            SidebarSaveFault.IoFailure => SidebarPersistenceFault.IoFailure,
            _ => SidebarPersistenceFault.None,
        };
        PublishWriteResult(new SidebarWriteResult(false, persistenceFault, bytes, elapsedMs, detail));
    }

    /// <summary>The first section (top level or child) whose extension config is over
    /// <see cref="MaxSectionConfigBytes"/>, as a human-readable detail string — or null when every section fits.
    /// Measures the raw config element only, so it costs nothing on a document with no contributed sections.</summary>
    static string? OversizedConfig(SidebarLayoutDocDto snapshot)
    {
        var sections = snapshot.Curated?.Sections;
        if (sections is null) return null;
        for (int i = 0; i < sections.Length; i++)
        {
            if (Check(sections[i]) is { } hit) return hit;
            var kids = sections[i]?.Children;
            if (kids is null) continue;
            for (int j = 0; j < kids.Length; j++)
                if (Check(kids[j]) is { } childHit) return childHit;
        }
        return null;

        static string? Check(SidebarSectionDto? s)
        {
            if (s?.Extension?.Config is not { } config) return null;
            int bytes = SidebarJson.ByteCount(config);
            return bytes > MaxSectionConfigBytes
                ? $"Section {s.Id ?? "?"} config is {bytes} B, over the {MaxSectionConfigBytes} B per-section budget."
                : null;
        }
    }

    /// <summary>Block until the newest ARMED write cycle has finished (or the timeout elapses). NOT for the UI thread's
    /// steady state — for tests and a deliberate drain point; the coalesced commit path is fire-and-forget by design.</summary>
    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_stateGate) t = _pendingWrite?.Task;
        if (t is null || t.IsCompleted) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }

    /// <summary>Fault recovery (the customizer's "Start fresh"): move the unreadable file to <c>*.corrupt</c> (replacing
    /// any previous one), delete the stale <c>.bak</c>, and unblock writes. The user's bytes are preserved, not deleted,
    /// so the document can still be inspected or hand-repaired.</summary>
    public void DiscardCorrupt()
    {
        SidebarWriteResult? completion = null;
        lock (_writeGate)
        {
            try
            {
                if (File.Exists(_path)) File.Move(_path, CorruptPath, overwrite: true);
                if (File.Exists(BakPath)) File.Delete(BakPath);
                if (File.Exists(TmpPath)) File.Delete(TmpPath);
                _writesBlocked = false;
                _saveFault = SidebarSaveFault.None;
                _saveFaultDetail = null;
            }
            catch (Exception ex)
            {
                const string safe = "The unreadable sidebar layout could not be set aside.";
                _saveFault = SidebarSaveFault.IoFailure;
                _saveFaultDetail = safe;
                completion = new SidebarWriteResult(false, SidebarPersistenceFault.IoFailure, 0, 0, safe);
                Log.Warn("sidebar", "sidebar.layout.discard_corrupt_failed", ex);
            }
        }
        if (completion is { } c) PublishWriteResult(c);
    }

    static string? s_appVersion;

    static string AppVersion()
    {
        if (s_appVersion is not null) return s_appVersion;
        try { s_appVersion = typeof(SidebarLayoutStore).Assembly.GetName().Version?.ToString() ?? ""; }
        catch (Exception) { s_appVersion = ""; }
        return s_appVersion;
    }
}

// ── PIN STORE: the one ordered, persisted pin list ──────────────────────────────────────────────────────────────────
// Shared by all three sidebar designs — unlimited, no cap, no eviction. Identity is the pin Id (Ordinal), the stable
// scheme in SidebarPinId which for every navigable kind IS the nav route key, so a pin survives a library refresh, a
// rename and an offline launch; Name/Uri are only a display cache refreshed through Touch.
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
                if (string.IsNullOrEmpty(p.Id) || _index.ContainsKey(p.Id)) continue;
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
            if (string.IsNullOrEmpty(p.Id)) continue;
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
// ── EXTENSION DATA SOURCES ──────────────────────────────────────────────────────────────────────────────────────────
// The nine first-party sidebar row producers, ported from 0.2.9's Features/Sidebar/Data/Sources/*.cs. Every Fill here
// is an EDGE READ over Entities (User.Me's relations, Playback's state, the Artist/Concert graph) — never a fetching
// service: 0.2.9's LibraryStore/HistoryStore/PlayLogStore/PlaybackBridge/IWhatsNewService/IConcertService/IMusicLibrary
// are all gone, and where 0.3 has no entity model yet (new releases) this file owns a small delegate seam instead.

// ── 1. the library projection's own sources (wavee.library, wavee.playlistTree, wavee.history.*) ──────────────────────
// These four read the binder's ALREADY-BUILT projection, not Entities directly — the projection itself (rootlist,
// saved albums/artists/shows, recency) is the binder's job, in another part of this file. THREADING: UI thread only,
// inside the rebuild path — no allocation beyond list growth, no LINQ, no closures, only SetHealthQuiet.

/// <summary>The live projection the four sources below read, owned by the binder. Built first, resolved second, so a
/// source reading this during a Fill always sees the current pass.</summary>
public interface ISidebarProjectionSnapshot
{
    /// <summary>Every kind, in source order (unsorted, unfiltered).</summary>
    IReadOnlyList<SidebarLibraryEntry> All { get; }
    /// <summary>The rootlist tree, depth-first flattened, folders carried as <see cref="SidebarEntryKind.Folder"/>.</summary>
    IReadOnlyList<SidebarLibraryEntry> Tree { get; }
    /// <summary>id/uri → entry, for the feed sources' join.</summary>
    SidebarSourceIndex Index { get; }
    SidebarSourceState LibraryState { get; }
    SidebarSourceState TreeState { get; }
    /// <summary>Navigation history, oldest first.</summary>
    IReadOnlyList<SidebarVisit> Visits { get; }
    /// <summary>Playback history, newest first, context-collapsed.</summary>
    IReadOnlyList<SidebarPlayedContext> Played { get; }
}

/// <summary><c>wavee.library</c> — the unified library projection as a contributed source: the one source that honours
/// the full filter/sort surface, so an extension section can express anything a built-in EntityList can.</summary>
public sealed class SidebarLibrarySource : SidebarDataSourceBase
{
    readonly List<string> _include = new();
    readonly List<string> _exclude = new();
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarLibrarySource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.Library)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Entity;

    public override SidebarSourceFilters SupportedFilters =>
        SidebarSourceFilters.Kinds | SidebarSourceFilters.Qualifier | SidebarSourceFilters.Search
        | SidebarSourceFilters.IncludeExcludeUris;

    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.All;
    public override SidebarSourcePaging Paging => SidebarSourcePaging.TopN;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("kinds", SidebarConfigFieldKind.Enum, "sidebar.source.library.kinds",
            DefaultJson: "\"all\"", EnumValues: ["all", "playlists", "albums", "artists", "shows"]),
        new SidebarConfigField("sort", SidebarConfigFieldKind.Enum, "sidebar.source.library.sort",
            DefaultJson: "\"recents\"", EnumValues: ["recents", "added", "alphabetical", "creator"]),
        new SidebarConfigField("descending", SidebarConfigFieldKind.Bool, "sidebar.source.library.descending",
            DefaultJson: "true"),
        new SidebarConfigField("qualifier", SidebarConfigFieldKind.Enum, "sidebar.source.library.qualifier",
            DefaultJson: "\"any\"", EnumValues: ["any", "byYou", "bySpotify", "mixed"]),
        new SidebarConfigField("includeUris", SidebarConfigFieldKind.UriList, "sidebar.source.library.includeUris"),
        new SidebarConfigField("excludeUris", SidebarConfigFieldKind.UriList, "sidebar.source.library.excludeUris"),
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.library.maxItems",
            Min: 0, Max: 500),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        var all = _snapshot.All;
        SetHealthQuiet(all.Count == 0 ? _snapshot.LibraryState : SidebarSourceState.Ready);

        var kinds = KindsOf(request.Config.Str("kinds"));
        byte qualifier = QualifierOf(request.Config.Str("qualifier"));
        string search = SidebarSearch.Normalize(request.Search);
        bool searching = search.Length > 0;

        _include.Clear();
        _exclude.Clear();
        request.Config.Strings("includeUris", _include);
        request.Config.Strings("excludeUris", _exclude);

        int max = Max(request, request.Config.Int("maxItems"));
        int start = into.Count;
        for (int i = 0; i < all.Count && into.Count - start < max; i++)
        {
            var e = all[i];
            if (!SidebarEntryKinds.Has(kinds, e.Kind)) continue;
            if (searching && e.Kind == SidebarEntryKind.Folder) continue;
            if (qualifier != 0 && (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier(qualifier))) continue;
            if (searching && !SidebarSearch.Matches(in e, search)) continue;
            if (_include.Count > 0 && !Contains(_include, in e)) continue;
            if (_exclude.Count > 0 && Contains(_exclude, in e)) continue;
            into.Add(e);
        }

        int count = into.Count - start;
        if (count > 1)
        {
            // Sort ONLY this source's slice: the pool is shared by every extension section in the document.
            var sorted = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(into).Slice(start, count);
            sorted.Sort(SidebarSort.For(SortOf(request.Config.Str("sort")), request.Config.Bool("descending", true)));
        }
        return count;
    }

    static bool Contains(List<string> uris, in SidebarLibraryEntry e)
    {
        for (int i = 0; i < uris.Count; i++)
            if (string.Equals(uris[i], e.Uri, StringComparison.Ordinal)
                || string.Equals(uris[i], e.Id, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Shared "how many" rule every first-party source uses: request wins, else the section's own config,
    /// else the fallback.</summary>
    internal static int Max(in SidebarSourceRequest request, int configured, int fallback = 500)
    {
        int m = request.MaxItems > 0 ? request.MaxItems : configured;
        return m > 0 ? m : fallback;
    }

    static SidebarEntryKindMask KindsOf(string? kinds) => kinds switch
    {
        "playlists" => SidebarEntryKindMask.PlaylistTree,
        "albums" => SidebarEntryKindMask.Album,
        "artists" => SidebarEntryKindMask.Artist,
        "shows" => SidebarEntryKindMask.Show,
        _ => SidebarEntryKindMask.All,
    };

    static byte QualifierOf(string? qualifier) => qualifier switch
    {
        "byYou" => (byte)SidebarPlaylistFlavor.ByYou,
        "bySpotify" => (byte)SidebarPlaylistFlavor.BySpotify,
        "mixed" => (byte)SidebarPlaylistFlavor.Mixed,
        _ => (byte)0,
    };

    static SidebarV3Sort SortOf(string? sort) => sort switch
    {
        "added" => SidebarV3Sort.RecentlyAdded,
        "alphabetical" => SidebarV3Sort.Alphabetical,
        "creator" => SidebarV3Sort.Creator,
        _ => SidebarV3Sort.Recents,
    };
}

/// <summary><c>wavee.playlistTree</c> — the folder-aware rootlist tree, depth-first flattened. Search flattens to
/// matching leaves (a folder is a container, not a result).</summary>
public sealed class SidebarPlaylistTreeSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarPlaylistTreeSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.PlaylistTree)
        => _snapshot = snapshot;

    public override SidebarSourceFilters SupportedFilters => SidebarSourceFilters.Search;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        var tree = _snapshot.Tree;
        SetHealthQuiet(tree.Count == 0 ? _snapshot.TreeState : SidebarSourceState.Ready);

        string search = SidebarSearch.Normalize(request.Search);
        bool searching = search.Length > 0;
        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5000);
        int n = 0;
        for (int i = 0; i < tree.Count && n < max; i++)
        {
            var e = tree[i];
            if (searching)
            {
                if (e.Kind == SidebarEntryKind.Folder || !SidebarSearch.Matches(in e, search)) continue;
            }
            into.Add(e);
            n++;
        }
        return n;
    }
}

/// <summary><c>wavee.history.visited</c> — recently OPENED. NOT "recently played": the label stays honest
/// (<see cref="SidebarRecency"/>'s semantics note).</summary>
public sealed class SidebarVisitedSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarVisitedSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.HistoryVisited)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Mixed;   // entities AND app routes
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.recents.maxItems",
            DefaultJson: "6", Min: 1, Max: 40),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);   // a local log is never pending: an empty log is an empty section
        return SidebarSourceMap.Visited(_snapshot.Visits,
            static v => v.RouteKey, static v => v.TicksUtc,
            _snapshot.Index, into, SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 6));
    }
}

/// <summary><c>wavee.history.played</c> — recently PLAYED, collapsed to distinct contexts.</summary>
public sealed class SidebarPlayedSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarPlayedSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.HistoryPlayed)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Mixed;   // containers AND bare tracks
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.recents.maxItems",
            DefaultJson: "6", Min: 1, Max: 40),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);
        return SidebarSourceMap.Played(_snapshot.Played, _snapshot.Index, into,
            SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 6));
    }
}

// ── 2. the playback-derived sources (wavee.queue, wavee.nowPlaying) ─────────────────────────────────────────────────
// PlaybackBridge is gone: both read Playback.Snap() / the static Queue class, which are plain edge reads (Edges.Queue,
// Playback.State.Current) — no subscription here. Freshness comes from the binder's own pump watching Playback's
// state, exactly as 0.2.9's comment describes ("one observer for the whole sidebar instead of one per source").

/// <summary><c>wavee.queue</c> — what plays next: the user queue and the context continuation
/// (<see cref="Queue.UpNext"/>), which already excludes history and the now-playing row by construction.</summary>
public sealed class SidebarQueueSource : SidebarDataSourceBase
{
    readonly List<Track> _scratch = new(32);

    public SidebarQueueSource() : base(SidebarContributions.Queue) { }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.queue.maxItems",
            DefaultJson: "5", Min: 1, Max: 50),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        // An empty queue is EMPTY, not pending: playback either has a queue or it does not.
        SetHealthQuiet(SidebarSourceState.Ready);
        _scratch.Clear();
        if (Queue.UpNext(out int start, out int length))
        {
            var packed = Queue.PackedRefs;
            for (int i = start; i < start + length; i++)
            {
                var r = Queue.Unpack(packed[i]);
                // GAP: a queued episode (EntityKind.Episode) is skipped — there is no track/episode unification
                // mapper in scope here (see the port report).
                if (r.IsNone || r.Kind != EntityKind.Track) continue;
                var track = new Track(r.Slot);
                if (track.IsValid) _scratch.Add(track);
            }
        }
        return SidebarSourceMap.Tracks(_scratch, into, SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5));
    }
}

/// <summary><c>wavee.nowPlaying</c> — the single current track (a one-row section). Empty while nothing plays, which
/// is the honest state.</summary>
public sealed class SidebarNowPlayingSource : SidebarDataSourceBase
{
    public SidebarNowPlayingSource() : base(SidebarContributions.NowPlaying) { }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;
    public override SidebarSourcePaging Paging => SidebarSourcePaging.None;

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);
        var current = Playback.Snap().Current;
        if (current.IsNone || current.Kind != EntityKind.Track) return 0;   // GAP: a now-playing episode is skipped
        var track = new Track(current.Slot);
        if (!track.IsValid) return 0;
        into.Add(SidebarSourceMap.FromTrack(track, 0));
        return 1;
    }
}

// ── 3. the fetching sources (wavee.artist.topTracks, wavee.newReleases, wavee.concerts) ────────────────────────────
// ArtistTopTracks is a real Entities edge read (Edges.ArtistPopular): EnsureFresh only KICKS the catalogue's own fetch
// ladder (Entities.Ensure) and Fill reads whatever has landed — no local cache, no async task, unlike 0.2.9. Concerts
// has a real Entities model too (Concert / Place / ConcertFeed / Edges.FeedSection) but 0.3 has no feed FETCHER yet, so
// EnsureFresh kicks an owned delegate seam. New releases has no Entities model at all yet, so it is seam-only.

/// <summary>A source whose freshness needs a UI-thread post (an async completion arriving off-thread). The binder
/// calls <see cref="Attach"/> from Start and <see cref="Detach"/> on teardown.</summary>
public interface ISidebarDataSourceLifecycle
{
    void Attach(Action<Action> post);
    void Detach();
}

/// <summary><c>wavee.artist.topTracks</c> — an artist's popular tracks, read straight off
/// <see cref="Artist.PopularSlots"/> (<c>Edges.ArtistPopular</c>). Config: <c>{ artistUri, maxItems }</c>.
///
/// <para>KEYED BY ARTIST, but unlike 0.2.9 there is no per-source cache: the artist row itself IS the cache (the
/// catalogue's own fetch ladder and eviction), so a section configured for a different artist just reads a different
/// slot.</para></summary>
public sealed class SidebarArtistTopTracksSource : SidebarDataSourceBase
{
    public SidebarArtistTopTracksSource() : base(SidebarContributions.ArtistTopTracks) { }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "sidebar.source.artistTopTracks.artist",
            Required: true),
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.artistTopTracks.maxItems",
            DefaultJson: "5", Min: 1, Max: 50),
    ]);

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (!TryArtist(request, out var artist)) return;
        Entities.Ensure(artist, ArtistFields.Identity | ArtistFields.Chart, FetchPriority.Visible);
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        if (!TryArtist(request, out var artist))
        {
            // An unconfigured section is not broken — it is waiting for the customizer to pick an artist.
            SetHealthQuiet(SidebarSourceState.Ready, "sidebar.source.artistTopTracks.unset");
            return 0;
        }

        var edgeState = Entities.Current.Edges.ArtistPopular.State(artist.Slot);
        SetHealthQuiet(edgeState == EdgeState.Complete ? SidebarSourceState.Ready : SidebarSourceState.Pending);

        var slots = artist.PopularSlots;
        if (slots.Length == 0) return 0;

        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5);
        int n = slots.Length < max ? slots.Length : max;
        var top = slots.Slice(0, n);
        Entities.Ensure(Entities.Current.Tracks, top, (uint)TrackFields.Row, FetchPriority.Visible);

        int start = into.Count;
        for (int i = 0; i < top.Length; i++)
        {
            var track = new Track(top[i]);
            if (track.IsValid) into.Add(SidebarSourceMap.FromTrack(track, into.Count - start));
        }
        return into.Count - start;
    }

    static bool TryArtist(in SidebarSourceRequest request, out Artist artist)
    {
        string? uri = request.Config.Str("artistUri");
        if (string.IsNullOrEmpty(uri) || EntityUri.KindOf(uri) != EntityKind.Artist) { artist = default; return false; }
        artist = Entities.Artist(EntityUri.Parse(uri));
        return true;
    }
}

/// <summary>One followed-artist release, as this source's fetch seam reports it back. Stands in for 0.2.9's
/// <c>NewReleaseNotification</c>/<c>IWhatsNewService</c>, neither of which has a 0.3 successor yet.</summary>
public readonly record struct SidebarNewReleaseRow(string Uri, long ReleasedAtMs);

/// <summary>The fetch seam a feed-backed first-party source owns until a real 0.3 feed model exists. Kicked from
/// <c>EnsureFresh</c>; the shell wires it to whatever eventually answers.</summary>
public delegate void SidebarFeedFetch(in SidebarSourceRequest request);

/// <summary><c>wavee.newReleases</c> — new releases from followed artists. A missing fetch seam is an EMPTY section,
/// not an error.</summary>
public sealed class SidebarNewReleasesSource : SidebarDataSourceBase, ISidebarDataSourceLifecycle
{
    readonly SidebarFeedFetch? _fetch;
    readonly ISidebarProjectionSnapshot _snapshot;
    readonly List<SidebarNewReleaseRow> _rows = new();
    Action<Action>? _post;
    SidebarSourceState _state;

    public SidebarNewReleasesSource(SidebarFeedFetch? fetch, ISidebarProjectionSnapshot snapshot)
        : base(SidebarContributions.NewReleases)
    {
        _fetch = fetch;
        _snapshot = snapshot;
        _state = fetch is null ? SidebarSourceState.Ready : SidebarSourceState.Pending;
        SetHealthQuiet(_state);
    }

    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.newReleases.maxItems",
            DefaultJson: "4", Min: 1, Max: 20),
    ]);

    public void Attach(Action<Action> post) => _post = post;
    public void Detach() => _post = null;

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (_fetch is null) return;
        try { _fetch(request); } catch (Exception) { /* a feed refresh is never fatal */ }
    }

    /// <summary>The seam's report call — any thread. <paramref name="rows"/> newest first, or null on failure.</summary>
    public void Report(SidebarSourceState state, IReadOnlyList<SidebarNewReleaseRow>? rows)
    {
        void Apply()
        {
            _rows.Clear();
            if (rows is not null) for (int i = 0; i < rows.Count; i++) _rows.Add(rows[i]);
            _state = state;
            SetHealth(state);
        }
        var post = _post;
        if (post is null) Apply(); else post(Apply);
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(_state);
        if (_fetch is null) return 0;

        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 4);
        int start = into.Count;
        for (int i = 0; i < _rows.Count && into.Count - start < max; i++)
        {
            var r = _rows[i];
            if (r.Uri.Length == 0) continue;
            // An unresolved release (not yet in the library projection) is DROPPED rather than stubbed: constructing a
            // bare SidebarLibraryEntry needs its full 0.3 constructor, which is owned by another file (see GAPS).
            if (_snapshot.Index.TryGet(r.Uri, out var known))
                into.Add(known with { SortStamp = r.ReleasedAtMs, SourceOrder = into.Count - start });
        }
        return into.Count - start;
    }
}

/// <summary>The fetch seam <see cref="SidebarConcertsSource"/> owns until a real feed fetcher exists (Concert.Page.cs's
/// host, a later wave). Told which feed subject to fill so it can call whatever populates
/// <c>Edges.FeedSection</c>.</summary>
public delegate void SidebarConcertsFetch(int placeSlot, int radiusKm, int feedSlot);

/// <summary>
/// <c>wavee.concerts</c> — upcoming events near the user, read off the REAL Entities concert model
/// (<c>Scope.SavedPlace</c>, <c>Scope.ConcertFeeds</c>, <c>Edges.FeedSection</c>) rather than a cached DTO list.
///
/// <para>NO LOCATION IS AN ACTIONABLE STATE, not an empty one: <see cref="Scope.SavedPlace"/> == 0 draws one
/// "Set your location" prompt row — also the logged-out path, since nothing ever saves a place then.</para></summary>
public sealed class SidebarConcertsSource : SidebarDataSourceBase, ISidebarDataSourceLifecycle
{
    /// <summary>Refresh window: concert feeds move on the scale of days.</summary>
    public const int RefreshMinutes = 30;
    /// <summary>Hard bound on how many rows one Fill ever sorts/emits — a sidebar section is a top-N surface.</summary>
    public const int SnapshotCap = 20;

    readonly SidebarConcertsFetch? _fetch;
    readonly List<int> _sorted = new(SnapshotCap);   // scratch: concert slots, reused across fills
    int _cachedPlace = -1, _cachedRadius = -1, _cachedFeedSlot;
    int _lastRequestedFeedSlot = -1;
    long _lastRequestedAtTicks;
    Action<Action>? _post;

    public SidebarConcertsSource(SidebarConcertsFetch? fetch) : base(SidebarContributions.Concerts)
    {
        _fetch = fetch;
        SetHealthQuiet(fetch is null ? SidebarSourceState.Ready : SidebarSourceState.Pending);
    }

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Event;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.concerts.maxItems",
            DefaultJson: "3", Min: 1, Max: 20),
        new SidebarConfigField("radiusKm", SidebarConfigFieldKind.Int, "sidebar.source.concerts.radiusKm",
            DefaultJson: "100", Min: 1, Max: 500),
    ]);

    public void Attach(Action<Action> post) => _post = post;
    public void Detach() => _post = null;

    /// <summary>The seam's report call, for whoever populates <c>Edges.FeedSection</c> asynchronously and wants the
    /// binder to rebuild once it has. Any thread.</summary>
    public void NotifyFeedChanged()
    {
        var post = _post;
        if (post is null) Raise(); else post(Raise);
    }

    public override void EnsureFresh(in SidebarSourceRequest request)
    {
        if (_fetch is null) return;
        int place = Entities.Current.SavedPlace;
        if (place <= 0) return;   // no location: nothing to ask for yet

        int radius = request.Config.Int("radiusKm", 100);
        int feedSlot = ResolveFeedSlot(place, radius);

        long now = Environment.TickCount64;
        if (feedSlot == _lastRequestedFeedSlot && _lastRequestedAtTicks != 0
            && now - _lastRequestedAtTicks < RefreshMinutes * 60_000L) return;

        _lastRequestedFeedSlot = feedSlot;
        _lastRequestedAtTicks = now;
        try { _fetch(place, radius, feedSlot); } catch (Exception) { }
    }

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        int place = Entities.Current.SavedPlace;
        if (place <= 0)
        {
            SetHealthQuiet(SidebarSourceState.Ready, "sidebar.concerts.setLocation", needsPrompt: true);
            return 0;
        }

        int radius = request.Config.Int("radiusKm", 100);
        int feedSlot = ResolveFeedSlot(place, radius);
        var edges = Entities.Current.Edges;
        var state = edges.FeedSection.State(feedSlot);
        SetHealthQuiet(state == EdgeState.Complete ? SidebarSourceState.Ready : SidebarSourceState.Pending);

        var slots = edges.FeedSection.Targets(feedSlot);
        if (slots.Length == 0) return 0;

        // Sections arrive Nearby ▸ Recommended ▸ All events, already provider-ordered; re-sort soonest-first, which is
        // what a sidebar strip wants regardless of section.
        _sorted.Clear();
        for (int i = 0; i < slots.Length && _sorted.Count < SnapshotCap; i++) _sorted.Add(slots[i]);
        _sorted.Sort(static (a, b) => new Concert(a).Date.CompareTo(new Concert(b).Date));

        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 3);
        int n = _sorted.Count < max ? _sorted.Count : max;
        int start = into.Count;
        for (int i = 0; i < n; i++)
        {
            var c = new Concert(_sorted[i]);
            if (!c.IsValid) continue;
            // GAP: ForRoute has no Creator/SortStamp slots, so the venue and the event instant are not stamped on the
            // row — soonest-first is preserved instead via SourceOrder (this source declares SourceOrder-only sorts).
            into.Add(SidebarLibraryEntry.ForRoute(c.Uri.Text, Entities.Strings.Resolve(c.TitleId), into.Count - start));
        }
        return into.Count - start;
    }

    int ResolveFeedSlot(int place, int radiusKm)
    {
        if (place == _cachedPlace && radiusKm == _cachedRadius) return _cachedFeedSlot;
        var row = Entities.Current.Places.Row[place];
        string key = Entities.Strings.Resolve(row.Id) + "|" + radiusKm.ToString(CultureInfo.InvariantCulture);
        _cachedPlace = place;
        _cachedRadius = radiusKm;
        _cachedFeedSlot = Entities.Current.ConcertFeeds.Slot(Entities.Strings.Intern(key));
        return _cachedFeedSlot;
    }
}

// ── 4. the first-party registration table ───────────────────────────────────────────────────────────────────────────
// 0.2.9 registered every source into BOTH a local table and the platform's WaveeExtensionRegistry, so the customizer's
// palette and the binder's resolution could never disagree. WaveeExtensionRegistry / IWaveeExtensionRegistrar do not
// exist in 0.3 — the table below is the only registry there is for now (see DROPPED in the port report).

public static class WaveeBuiltInDataSources
{
    /// <summary>Construct + register the nine first-party sources into one table the binder resolves through.</summary>
    /// <param name="snapshot">The binder's live projection.</param>
    /// <param name="newReleasesFetch">The new-releases fetch seam; null leaves the section permanently empty.</param>
    /// <param name="concertsFetch">The concerts fetch seam; null leaves the section empty (still prompting for a
    /// location once one is set).</param>
    public static SidebarDataSourceTable RegisterAll(
        ISidebarProjectionSnapshot snapshot,
        SidebarFeedFetch? newReleasesFetch = null,
        SidebarConcertsFetch? concertsFetch = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var table = new SidebarDataSourceTable();

        table.Add(new SidebarLibrarySource(snapshot));
        table.Add(new SidebarVisitedSource(snapshot));
        table.Add(new SidebarPlayedSource(snapshot));
        table.Add(new SidebarPlaylistTreeSource(snapshot));
        table.Add(new SidebarArtistTopTracksSource());
        table.Add(new SidebarNewReleasesSource(newReleasesFetch, snapshot));
        table.Add(new SidebarConcertsSource(concertsFetch));
        table.Add(new SidebarQueueSource());
        table.Add(new SidebarNowPlayingSource());

        return table;
    }

    /// <summary>The M1 contribution host: first-party only in 0.3 (no sandboxed/third-party registry exists yet), so
    /// resolution is a straight pass-through to the table.</summary>
    public sealed class ContributionHost : ISidebarContributionHost
    {
        readonly SidebarDataSourceTable _table;

        public ContributionHost(SidebarDataSourceTable table) => _table = table;

        public SidebarDataSourceTable Table => _table;

        public ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability)
            => _table.Resolve(sourceId, out availability);
    }

    /// <summary>Attach the UI-thread marshaller to every source that owns async work, and wire <paramref name="onChanged"/>
    /// to each source's Changed. Called once by the binder's Start; the returned action detaches everything.</summary>
    public static Action Attach(SidebarDataSourceTable table, Action<Action> post, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onChanged);

        var attached = new List<ISidebarDataSource>();
        foreach (var source in table.All)
        {
            (source as ISidebarDataSourceLifecycle)?.Attach(post);
            source.Changed += onChanged;
            attached.Add(source);
        }

        return () =>
        {
            for (int i = 0; i < attached.Count; i++)
            {
                attached[i].Changed -= onChanged;
                (attached[i] as ISidebarDataSourceLifecycle)?.Detach();
            }
            attached.Clear();
        };
    }

#if DEBUG || FLUENTGPU_DIAG
    /// <summary>Diagnostic-only labelled variant: each source gets its own handler so a wake capture can identify which
    /// one raised Changed.</summary>
    public static Action Attach(SidebarDataSourceTable table, Action<Action> post, Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onChanged);

        var attached = new List<(ISidebarDataSource Source, Action Handler)>();
        foreach (var source in table.All)
        {
            (source as ISidebarDataSourceLifecycle)?.Attach(post);
            string sourceId = source.Id;
            Action handler = () => onChanged(sourceId);
            source.Changed += handler;
            attached.Add((source, handler));
        }

        return () =>
        {
            for (int i = 0; i < attached.Count; i++)
            {
                var entry = attached[i];
                entry.Source.Changed -= entry.Handler;
                (entry.Source as ISidebarDataSourceLifecycle)?.Detach();
            }
            attached.Clear();
        };
    }
#endif
}
// ── PROJECTION BINDER ──────────────────────────────────────────────────────────────────────────────────────────────
// The ONE rebuild driver: folds every trigger (rootlist/library edges, pins, layout, V3 view state, search, contributed
// sources), builds the unified library projection over `User.Me`'s edges, shapes it into the published entry list and
// the Curated planner's `SidebarProjectionInput`, and holds a mid-drag publish behind `SidebarStageHold` so a rootlist
// reorder's rows never re-key under the pointer. `SidebarEntries` (below) is the published-entries cell it drives.

/// <summary>
/// THE ENTRY-PROJECTION DRIVER. Owns one unified projection over the account's library edges + the pin store, rebuilt
/// whenever any of them moves; the published V3/Classic entry list (<see cref="Entries"/>); the first-seen commit;
/// the contribution slices; and the <see cref="CurrentInput"/> a Curated pane hands to <c>SidebarRowPlanner.Build</c>.
///
/// <para><b>Impure by design.</b> Every DECISION lives in the engine-free pipeline (<c>SidebarBinderPipeline</c>,
/// <c>SidebarProjection</c>, <c>SidebarSort</c>) so the tests drive the real rules; this class is the
/// subscription/edge/publish shell around them.</para>
///
/// <para><b>THREADING: UI thread only</b>, unsynchronized, and never blocking (C1/C9) — a rebuild reads edge tables and
/// static preferences, never a socket or a file. There is no mounted pump: the 0.2.9 <c>MountPoint()</c> component
/// (the only way a plain service could subscribe an engine <c>Signal</c>) is gone with the reactive layer it needed.
/// <see cref="Sync"/> is a plain method the shell/pane calls after every drain and after every preference edit; it is
/// idempotent and cheap to call unconditionally (one trigger-struct compare).</para>
/// </summary>
public sealed class SidebarProjectionBinder : ISidebarProjectionSnapshot
{
    /// <summary>How many navigation/playback rows a recency feed would keep, once one is wired. See GAPS in the port
    /// report: neither feed has a 0.3 data source yet, so this is currently unused headroom, not dead code.</summary>
    public const int RecencyCap = 40;

    // ── the mid-drag freeze's two slots (the pane's `UsesA` precedent, relocated here — see the port report) ─────────
    // `Library`/`PlaylistTree`/`ByUri` are the only `SidebarProjectionInput` fields a rootlist drag can re-key, so only
    // they are double-buffered. `_publishedStage` is what `CurrentInput` and `Entries` currently show; a rebuild always
    // computes into the OTHER slot, so the published one is never mutated out from under a live drag.
    sealed class BuildSlot
    {
        public readonly List<SidebarLibraryEntry> All = new(256);
        public readonly List<SidebarLibraryEntry> Tree = new(128);
        public readonly SidebarSourceIndex Index = new();
    }

    readonly BuildSlot _slotA = new(), _slotB = new();
    BuildSlot _publishedStage;
    BuildSlot _lastBuild;
    readonly SidebarStageHold<BuildSlot> _stageHold = new();
    bool _dragSessionLive;
    bool _publishThroughFreeze;

    BuildSlot BuildTarget => ReferenceEquals(_publishedStage, _slotA) ? _slotB : _slotA;

    // Rebuild buffers — allocated once, reused forever (P8). Everything here publishes live every rebuild: the freeze
    // above covers only the tree/library re-keying defect, not these (see the port report's scope note).
    readonly List<SidebarLibraryEntry> _pinRows = new(16);
    readonly List<SidebarLibraryEntry> _visited = new(16);
    readonly List<SidebarLibraryEntry> _played = new(16);
    readonly List<SidebarLibraryEntry> _newReleases = new(8);
    readonly List<SidebarLibraryEntry> _concerts = new(8);
    readonly List<SidebarLibraryEntry> _extEntries = new(64);
    readonly List<SidebarLibraryEntry> _scratch = new(256);   // PinsFirst' partition buffer
    readonly List<string> _liveIds = new(256);
    readonly HashSet<string> _pinnedIds = new(StringComparer.Ordinal);
    readonly SidebarExtensionSlices _slices = new();
    readonly SidebarContributionCache _cache = new();
    readonly Dictionary<string, SidebarSourceState> _observedSourceStates = new(StringComparer.Ordinal);
    readonly HashSet<string> _staleSourceIds = new(StringComparer.Ordinal);
    readonly Func<string, bool> _isFolderExpanded;

    SidebarFirstSeen? _firstSeen;
    ISidebarContributionHost? _host;
    SidebarDataSourceTable? _table;
    SidebarProjectionInput _input;
    SidebarBinderTriggers _lastTriggers;
    SidebarSourceState _libraryState = SidebarSourceState.Pending;
    SidebarSourceState _treeState = SidebarSourceState.Pending;
    int _revision;
    int _sourceEpoch;
    bool _started;
    bool _rebuilding;
    bool _dirty = true;

    /// <summary>The published entry cell (F.7.5's <c>SidebarEntries</c>) — the ONE thing a V3/Classic pane binds to.
    /// Owned here because <see cref="Rebuild"/> is the one place a pass becomes visible.</summary>
    public readonly SidebarEntries Entries = new();

    public SidebarProjectionBinder()
    {
        _publishedStage = _slotA;
        _lastBuild = _slotA;
        _isFolderExpanded = Sidebar.IsFolderExpanded;
    }

    // ─────────────────────────────────── wiring ───────────────────────────────────

    /// <summary>The contribution host every Extension section (and the two built-in feed kinds) resolves through.
    /// <paramref name="sources"/> is the first-party table behind <paramref name="host"/>, for <see cref="StateOf"/>;
    /// omit it when the host IS the table.</summary>
    public void UseHost(ISidebarContributionHost? host, SidebarDataSourceTable? sources = null)
    {
        _host = host;
        _table = sources ?? host as SidebarDataSourceTable;
        Invalidate();
    }

    /// <summary>Idempotent start: do the first rebuild. There is no marshaller to hand out any more (no async pin
    /// hydration, no pump) — a contributed source that needs one gets it from wherever it is attached (out of scope
    /// here; see ASSUMED in the port report).</summary>
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

    // ─────────────────────────────────── the mid-drag freeze ───────────────────────────────────

    /// <summary>A rootlist filing session began: park every rebuild's tree/library snapshot instead of publishing it.</summary>
    public void BeginDragSession() => _dragSessionLive = true;

    /// <summary>One-shot: let the NEXT rebuild through the freeze even though the session is still live. The gesture's
    /// OWN commit (the reorder it just made) is not a foreign re-projection — holding it would snap the dropped row
    /// back to its pre-drag position until the session ends.</summary>
    public void AllowNextPublishThroughFreeze() => _publishThroughFreeze = true;

    /// <summary>The session ended (drop, cancel or Escape alike): flush whatever was parked and publish it.</summary>
    public void EndDragSession()
    {
        _dragSessionLive = false;
        if (!_stageHold.TryFlush(out var stage) || stage is null) return;
        _publishedStage = stage;
        Invalidate();
        Sync();
    }

    // ─────────────────────────────────── reads ───────────────────────────────────

    /// <summary>The planner input for the CURRENT projection. Its lists ALIAS the binder's buffers, so it is valid
    /// until the next rebuild that actually publishes (a held rebuild does not change it). Key a memo on
    /// <see cref="Revision"/>.</summary>
    public SidebarProjectionInput CurrentInput => _input;

    /// <summary>Bumped once per rebuild (held or not) — the planner's <c>DepKey</c> lane. A held rebuild still bumps
    /// this: everything BUT the frozen tree/library/ByUri triplet is live, so a re-plan of the other sections must
    /// still happen.</summary>
    public int Revision => _revision;

    public SidebarSourceState StateOf(string sourceId) => _table?.StateOf(sourceId) ?? SidebarSourceState.Error;
    public SidebarContributionAvailability AvailabilityOf(string sectionId) => _slices.AvailabilityOf(sectionId);
    public SidebarContributionCache ContributionCache => _cache;

    // ISidebarProjectionSnapshot — what the first-party sources read. Always the FRESH pass, never the frozen one: a
    // source is not what a rootlist drag is dragging, so it should see the same data a non-frozen rebuild would.
    IReadOnlyList<SidebarLibraryEntry> ISidebarProjectionSnapshot.All => _lastBuild.All;
    IReadOnlyList<SidebarLibraryEntry> ISidebarProjectionSnapshot.Tree => _lastBuild.Tree;
    SidebarSourceIndex ISidebarProjectionSnapshot.Index => _lastBuild.Index;
    SidebarSourceState ISidebarProjectionSnapshot.LibraryState => _libraryState;
    SidebarSourceState ISidebarProjectionSnapshot.TreeState => _treeState;
    // GAPS: neither a navigation log nor a play log has a 0.3 source yet (see the port report) — both feeds report
    // empty rather than inventing a shape nothing fills.
    IReadOnlyList<SidebarVisit> ISidebarProjectionSnapshot.Visits => Array.Empty<SidebarVisit>();
    IReadOnlyList<SidebarPlayedContext> ISidebarProjectionSnapshot.Played => Array.Empty<SidebarPlayedContext>();

    // ─────────────────────────────────── the rebuild gate ───────────────────────────────────

    /// <summary>Force the next <see cref="Sync"/> to rebuild even if no trigger moved (a new host, a flushed freeze).</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>Rebuild iff a trigger moved (or <see cref="Invalidate"/> was called). Returns whether it rebuilt. Cheap
    /// enough to call unconditionally — the gate is one struct compare over peeked edge versions.</summary>
    public bool Sync()
    {
        if (_rebuilding) { _dirty = true; return false; }
        var triggers = Read();
        if (!_dirty && triggers == _lastTriggers) return false;
        _lastTriggers = triggers;
        _dirty = false;

        _rebuilding = true;
        try { Rebuild(); }
        finally { _rebuilding = false; }

        // A source that fired Changed mid-rebuild only marked us dirty; settle now (never recurse into Rebuild).
        if (_dirty)
        {
            _dirty = false;
            _lastTriggers = Read();
            _rebuilding = true;
            try { Rebuild(); }
            finally { _rebuilding = false; }
        }
        return true;
    }

    /// <summary>A registered source said its rows or health moved. Public: the source-attachment code (owned
    /// elsewhere — see ASSUMED) calls this from a source's <c>Changed</c> event.</summary>
    public void OnSourceChanged()
    {
        _dirty = true;
        _sourceEpoch++;
        if (!_rebuilding) Sync();
    }

    // ─────────────────────────────────── the rebuild ───────────────────────────────────

    void Rebuild()
    {
        var u = User.Me;
        var recency = SidebarRecency.Empty;   // GAP: no navigation-visit source wired yet (see the port report)
        var firstSeen = _firstSeen ??= LoadFirstSeen(Sidebar.FirstSeen);

        var build = BuildTarget;
        _lastBuild = build;

        // 1 — THE projection, fully flattened (folders and all their children): the planner's `Library` slice, the
        //     pin resolver's join index. Folder COLLAPSE is a V3-list concern (step 3).
        var full = SidebarProjection.Build(build.All, in u, SidebarEntryKindMask.All, firstSeen, recency,
                                           includeFolderChildren: true);
        // 2 — the tree slice the Curated planner's PlaylistTree section walks: depth-stamped, folders carried as rows.
        SidebarProjection.Build(build.Tree, in u, SidebarEntryKindMask.PlaylistTree, firstSeen, recency,
                                includeFolderChildren: true);
        build.Index.Rebuild(build.All);

        _treeState = StateOf(u.RootlistState);
        _libraryState = Worst(StateOf(u.State(LibraryEdgeKind.SavedAlbums)),
                        Worst(StateOf(u.State(LibraryEdgeKind.FollowedArtists)), StateOf(u.State(LibraryEdgeKind.SavedShows))));

        // 3 — the PUBLISHED entry list (V3 / Classic read it): filter → sort → pins-first.
        // PEEK, never subscribe: the binder is not a computation, and a subscription here would make every rebuild
        // re-enter itself on the next preference write.
        bool v3 = Sidebar.Design.Peek() == SidebarDesign.LibraryV3;
        var filter = v3 ? (SidebarV3Filter)Sidebar.V3Filter.Peek() : SidebarV3Filter.All;
        var qualifier = v3 ? (SidebarV3Qualifier)Sidebar.V3Qualifier.Peek() : SidebarV3Qualifier.Any;
        var sort = (SidebarV3Sort)Sidebar.V3Sort.Peek();
        bool desc = Sidebar.V3Desc.Peek();
        string search = v3 ? SidebarSearch.Normalize(Sidebar.V3Search.Peek()) : "";
        bool searching = search.Length > 0;
        bool qualifiers = SidebarProjection.QualifiersAvailable(full.FlavorMask);

        var buffer = Entries.Buffer;
        var v3Result = SidebarProjection.Build(buffer, in u, SidebarEntryKinds.From(filter), firstSeen, recency,
                                               includeFolderChildren: searching,
                                               isFolderExpanded: searching ? null : _isFolderExpanded);

        ResolvePins(build.Index);
        var query = new SidebarV3Query(filter, qualifier, sort, desc, search, qualifiers);
        var shape = SidebarBinderPipeline.Shape(buffer, _scratch, in query, Sidebar.Pins.Items,
                                                Sidebar.CanReorderV3 ? Sidebar.V3CustomOrder : null);

        // 4 — the recency + playback feeds. GAP: neither has a 0.3 source (see the port report); both stay empty
        //     rather than a fabricated shape.
        _visited.Clear();
        _played.Clear();

        // 5 — the two built-in feed kinds, served by the same registered sources as their Extension-section form.
        FillFeed(SidebarContributions.NewReleases, _newReleases, 8);
        FillFeed(SidebarContributions.Concerts, _concerts, 8);

        // 6 — contributions, resolved AFTER the projection so a source reading the snapshot sees this pass.
        SidebarBinderPipeline.ResolveExtensions(Sidebar.Layout, _host, _extEntries, _slices, _cache, search);
        ObserveExtensionSources(Sidebar.Layout);

        bool anyPending = AnyContributingKindPending(filter, u);
        var (state, error) = PublishState(filter, shape.Count, anyPending);

        // 7 — THE FREEZE: hold this pass's tree/library/ByUri triplet during a live drag; otherwise advance it and
        //     drop any stale hold (the one-shot latch's own publish must not be overwritten by a later flush).
        bool sessionLive = _dragSessionLive && !_publishThroughFreeze;
        _publishThroughFreeze = false;
        if (_stageHold.TryHold(sessionLive, build)) { /* parked — _publishedStage keeps showing the old pass */ }
        else { _publishedStage = build; _stageHold.Discard(); }

        // 8 — publish the entries cell. ONE version bump per rebuild, never per entry, and none at all when the
        //     rebuild landed on byte-identical content (SidebarEntriesShadow's exact compare).
        Entries.Publish(state, error, anyPending, qualifiers, shape.PinCount, _publishedStage.All);

        // 9 — commit point: persist the first-seen document only when this pass observed something new.
        int newStamps = full.NewFirstSeenStamps + v3Result.NewFirstSeenStamps;
        if (newStamps > 0) CommitFirstSeen(firstSeen, build.All);

        // 10 — the planner input. Revision is the caller's composite epoch, echoed into every plan.
        _revision++;
        _input = new SidebarProjectionInput(
            Library: _publishedStage.All,
            PlaylistTree: _publishedStage.Tree,
            Pins: _pinRows,
            Visited: _visited,
            Played: _played,
            NewReleases: _newReleases,
            Concerts: _concerts,
            ByUri: _publishedStage.Index.AsLookup(),
            PinnedIds: _pinnedIds,
            ExpandedFolders: Sidebar.ExpandedFolders,
            Search: search,
            LibraryState: _libraryState,
            TreeState: _treeState,
            RecentsState: SidebarSourceState.Ready,
            NewReleasesState: StateOf(SidebarContributions.NewReleases),
            ConcertsState: StateOf(SidebarContributions.Concerts),
            ConcertsLocationUnset: NeedsPrompt(SidebarContributions.Concerts),
            Revision: _revision,
            ExtensionEntries: _extEntries,
            ExtensionSlices: _slices);
    }

    // Pins resolve against the fresh projection; an UNRESOLVED pin still renders from its own display cache (offline-
    // first) instead of disappearing. The async hydration side-cache is GONE (DATA GAPS answer (a)): an unresolved
    // pin's target is `Entities.Ensure`d at Visible priority instead, and the row picks up real art/count the moment
    // the entity becomes Known — no per-pin in-flight bookkeeping, no permanent "tried once, never again" set.
    void ResolvePins(SidebarSourceIndex index)
    {
        _pinRows.Clear();
        _pinnedIds.Clear();
        var pins = Sidebar.Pins.Items;
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.Id.Length == 0) continue;
            _pinnedIds.Add(pin.Id);
            if (index.TryGet(pin.Id, out var entry))
            {
                _pinRows.Add(entry with { IsPinned = true, SourceOrder = i });
                Sidebar.Pins.Touch(pin.Id, entry.Name);
                continue;
            }

            _pinRows.Add(SidebarBinderPipeline.ResolveUnlistedPin(pin, i, ResolveLivePin(pin)));
        }
    }

    // The entity a pin the library projection does not know (an editorial/Spotify-owned playlist, or any other row
    // never saved to the user's own library/rootlist). Returns the entity's CURRENTLY known fields (null if not yet
    // known, having just kicked its fetch) — never an async callback, never a cache keyed by pin id.
    static SidebarLibraryEntry? ResolveLivePin(SidebarPin pin)
    {
        if (pin.Uri.Length == 0 || !EntityId.TryParse(pin.Uri, out var id)) return null;
        switch (pin.Kind)
        {
            case SidebarEntryKind.Playlist:
            {
                var p = new Playlist(Entities.Current.Playlists.Slot(id));
                if (!p.Knows(PlaylistFields.Identity))
                { Entities.Ensure(p, PlaylistFields.Identity, FetchPriority.Visible); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Playlist, "", Entities.Strings.Resolve(p.TitleId),
                    Entities.Strings.Resolve(p.Owner.NameId), p.ImageId, null, ChildCount: p.TrackCount, AddedAtMs: 0,
                    SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false,
                    Flavor: SidebarPlaylistFlavor.None);
            }
            case SidebarEntryKind.Album:
            {
                var a = new Album(Entities.Current.Albums.Slot(id));
                if (!a.Knows(AlbumFields.Identity))
                { Entities.Ensure(a, AlbumFields.Identity, FetchPriority.Visible); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Album, "", a.Title, "", a.ImageId, null,
                    ChildCount: a.TrackCount, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
                    Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None);
            }
            case SidebarEntryKind.Artist:
            {
                var ar = new Artist(Entities.Current.Artists.Slot(id));
                if (!ar.Knows(ArtistFields.Identity))
                { Entities.Ensure(ar, ArtistFields.Identity, FetchPriority.Visible); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Artist, "", ar.Name, "", ar.ImageId, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0,
                    Circular: true, Flavor: SidebarPlaylistFlavor.None);
            }
            case SidebarEntryKind.Show:
            {
                var s = new Show(Entities.Current.Shows.Slot(id));
                if (!s.Knows(ShowFields.Identity))
                { Entities.Ensure(s, ShowFields.Identity, FetchPriority.Visible); return null; }
                return new SidebarLibraryEntry("", SidebarEntryKind.Show, "", s.Title, "", s.ImageId, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0,
                    Circular: false, Flavor: SidebarPlaylistFlavor.None);
            }
            default:
                return null;   // Folder/AppRoute/Track — no catalog entity backs any of these
        }
    }

    bool NeedsPrompt(string sourceId)
    {
        if (_host is null) return false;
        var source = _host.Resolve(sourceId, out _);
        return source?.NeedsPrompt ?? false;
    }

    /// <summary>Refresh a built-in feed's slice from its registered source. Never throws: one bad feed may not take the
    /// sidebar down.</summary>
    void FillFeed(string sourceId, List<SidebarLibraryEntry> into, int max)
    {
        into.Clear();
        var source = _host?.Resolve(sourceId, out _);
        if (source is null) return;
        var request = new SidebarSourceRequest(SidebarSourceConfig.Empty, max);
        try
        {
            source.EnsureFresh(request);
            source.Fill(into, request);
        }
        catch (Exception ex)
        {
            into.Clear();
            ObserveSourceState(sourceId, SidebarSourceState.Error, staleReplay: false, error: ex);
            return;
        }
        ObserveSourceState(sourceId, source.State, staleReplay: false);
    }

    void ObserveExtensionSources(SidebarCustomLayout layout)
    {
        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            ObserveExtensionSection(sections[i]);
            var children = sections[i].ChildList;
            for (int j = 0; j < children.Count; j++) ObserveExtensionSection(children[j]);
        }
    }

    void ObserveExtensionSection(SidebarSectionSpec section)
    {
        if (section.Kind != SidebarSectionKind.Extension || section.Extension is not { } xref) return;
        string sourceId = SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId);
        if (sourceId.Length == 0 || !_slices.TryGet(section.Id, out var slice)) return;
        bool stale = slice.Availability == SidebarContributionAvailability.Cached;
        ObserveSourceState(sourceId, stale ? SidebarSourceState.Error : slice.State, stale);
    }

    // Edge-triggered: a failed source is noisy only once, recovery is explicit, and a last-good replay is observable
    // without logging row contents, search text or config.
    void ObserveSourceState(string sourceId, SidebarSourceState next, bool staleReplay, Exception? error = null)
    {
        bool hadPrevious = _observedSourceStates.TryGetValue(sourceId, out var previous);
        if (!hadPrevious || previous != next)
        {
            _observedSourceStates[sourceId] = next;
            if (next == SidebarSourceState.Error)
                Log.Warn("sidebar", "A sidebar data source failed: " + sourceId, error);
            else if (hadPrevious && previous == SidebarSourceState.Error)
                Log.Info("sidebar", "A sidebar data source recovered: " + sourceId);
        }

        if (staleReplay)
        {
            if (_staleSourceIds.Add(sourceId))
                Log.Warn("sidebar", "A last-good sidebar source snapshot was replayed: " + sourceId);
        }
        else _staleSourceIds.Remove(sourceId);
    }

    // The skeleton gate is per CONTRIBUTING kind (a pending Shows load must not skeleton the Playlists filter).
    static bool AnyContributingKindPending(SidebarV3Filter filter, in User u)
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
    static (FluentGpu.Signals.LoadState State, Exception? Error) PublishState(SidebarV3Filter filter, int count, bool anyPending)
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

    // Peek-only: there is no computation to subscribe any more, so every trigger is read the same way whether Sync()
    // was called from a drain, from an explicit Invalidate(), or from a preference setter.
    SidebarBinderTriggers Read() => new(
        LibraryEpoch: LibraryEpoch(),
        PinsVersion: Sidebar.Pins.Version.Peek(),
        HistoryVersion: 0,     // GAP: no navigation-visit source wired yet
        PlayLogRevision: 0,    // GAP: no play-log source wired yet
        LayoutVersion: Sidebar.LayoutVersion.Peek(),
        FolderVersion: Sidebar.FolderVersion.Peek(),
        OrderVersion: Sidebar.V3OrderVersion.Peek(),
        CultureEpoch: 0,       // GAP: locale-triggered resort is an engine concern, out of scope for this SHELL file
        V3State: SidebarBinderTriggers.PackV3((int)Sidebar.Design.Peek(), Sidebar.V3Filter.Peek(),
                                              Sidebar.V3Qualifier.Peek(), Sidebar.V3Sort.Peek(),
                                              Sidebar.V3Desc.Peek()),
        SearchHash: SidebarSearch.Normalize(Sidebar.V3Search.Peek()).GetHashCode(StringComparison.Ordinal),
        SourceEpoch: _sourceEpoch,
        PlaybackEpoch: 0);     // GAP: no playback bridge wired yet

    // A REFERENCE-identity-free fold over the account's edges: every relation already carries a monotonic Version, so
    // there is no need for 0.2.9's "hash the published list instance" trick.
    static int LibraryEpoch()
    {
        var u = User.Me;
        unchecked
        {
            int h = 17;
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
    /// enough to bump <see cref="Version"/>.</summary>
    public void Publish(FluentGpu.Signals.LoadState state, Exception? error, bool anyContributingKindPending,
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
        if (!published && !full) return;
        _version.Value = _version.Peek() + 1;
    }
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

    /// <summary>Rename a FOLDER (group id, current name) — the rename dialog is the seam's. A playlist renames through
    /// its registered <c>ActionId.RenamePlaylist</c> verb, never here.</summary>
    public Action<string, string>? RenameFolder;

    /// <summary>Delete a folder (group id, name, direct child count) — the seam confirms first and refuses without an
    /// overlay to confirm in.</summary>
    public Action<string, string, int>? DeleteFolder;

    /// <summary>Resolve an entity's tracks for a deposit (a playlist, an album, a show, a single track), after the drop.</summary>
    public Func<string, CancellationToken, Task<Track[]>>? ResolveTracks;
}
