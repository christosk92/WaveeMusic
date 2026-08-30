using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// The single owner of all sidebar state: the active design, per-design pane state, per-design view state, the shared
/// pin store, the entry projection cell, and the Curated layout document with its command/undo stack. ONE
/// reference-stable instance created in <c>Services</c> (so it survives the login-gate shell swap and keeps the
/// customizer's undo stack across a logout) and provided at the app root via <see cref="Slot"/> — context, not ctor
/// args (the component-props-freeze contract).
///
/// THREADING: UI thread only. Every field, signal and method on this class is UI-thread affine and unsynchronized — the
/// same discipline as LibraryStore's detail caches. There are no off-thread producers. The ONLY background work is the
/// document write inside <c>SidebarLayoutStore.Commit</c>, which serializes a UI-thread snapshot on a pool thread (the
/// <c>HistoryStore.SaveToDisk</c> precedent). Its completion only replaces a guarded pending-result slot; the
/// <see cref="Activate"/> dispatcher publishes that result to the reactive UI on the UI thread.
///
/// WHAT IS *NOT* HERE, deliberately: the pure per-design pane rules (<c>SidebarPaneState</c>) and the pin list
/// (<c>SidebarPinStore</c>) live in their own engine-free files so the unit tests drive the real logic; this class is the
/// signal/persistence shell around them.
/// </summary>
public sealed class SidebarPreferences
{
    /// <summary>The app-root context channel. The instance is reference-stable for the process lifetime, so the provide
    /// never churns its consumers (the <c>ActionServices</c> precedent).</summary>
    public static readonly Context<SidebarPreferences?> Slot = new(null);

    /// <summary>DRAG PEEK — TRANSIENT, never persisted, never a write to <see cref="Collapsed"/>: a collapsed sidebar is
    /// PRESENTED expanded for the rest of one drag once the pointer dwells on the rail (<c>SidebarPane.DragPeekMs</c>).
    /// <para>It lives on the service because the peek has TWO readers that must agree. The pane decides its own
    /// presentation (<c>SidebarPane.Render</c>'s <c>compact</c>), but the SHELL owns the sidebar COLUMN's width and clips
    /// it (<c>WaveeShell</c>: <c>Width = presentedCompact ? CompactRailW : _sidebarWidth</c> on a <c>ClipToBounds</c>
    /// box). A peek only the pane could see therefore laid the expanded rows out at the full pane width and then
    /// scissored them to the 56-DIP rail — the reported "dim rail of cover tiles with tree connector lines and no
    /// labels" is those rows' first 56 DIP: selection gutter + tree guides + art, every label cut off. Worse, flipping
    /// the pane out of compact clears the rail layer's <c>HitTestVisible</c>, so the rail tiles that WERE valid targets
    /// lost their drop-spotlight cut-outs at the same moment (the engine's hit-reachability gate). One signal, both
    /// readers, and the shell's <c>SidebarPaneAnim</c> animates the widening for free.</para></summary>
    public Signal<bool> DragPeek { get; } = new(false);

    readonly IAppSettings _settings;
    readonly SidebarLayoutStore _store;
    readonly HashSet<string> _expandedFolders = new(StringComparer.Ordinal);
    readonly List<string> _v3CustomOrder = new();
    readonly Dictionary<string, int> _v3OrderRank = new(StringComparer.Ordinal);
    readonly SidebarUndo _undo = new();                            // decision 6: 50-step pre-image undo/redo (Wavee.Core)
    readonly object _persistenceGate = new();
    readonly Signal<SidebarWriteResult> _persistenceHealth = new(SidebarWriteResult.Healthy);

    SidebarCustomLayout _layout;
    SidebarWireCarry _carry = SidebarWireCarry.Empty;               // forward tolerance: unknown sections round-trip
    // The playlist added-at PROXY (F.7.5): id → the ms the projection first ever observed it. Held here as the raw wire
    // array and re-emitted verbatim on every commit so it is never silently lost. It is deliberately NOT interpreted yet —
    // the bounded (cap 2000, pruned on save) SidebarFirstSeen map that stamps and reads it is the projection's, and
    // PublishFirstSeen below is the seam it writes through.
    SidebarFirstSeenDto[]? _firstSeen;
    float _viewportWidth;                                          // last seen; written by the shell's tier effect
    bool _pinNamesDirty;                                           // a TouchPin refresh waiting to ride the next commit
    bool _loaded;                                                  // Load() ran; before that no mutation may commit
    Action<Action>? _post;
    SidebarWriteResult _pendingPersistenceHealth;
    bool _hasPendingPersistenceHealth;

    public SidebarPreferences(IAppSettings settings, SidebarLayoutStore store)
    {
        _settings = settings;
        _store = store;
        _store.WriteCompleted = OnWriteCompleted;

        var design = SidebarDesignInfo.FromInt(settings.Get(WaveeSettings.SidebarDesign));
        Design = new Signal<SidebarDesign>(design);

        // Seed the pane triple for the ACTIVE design before the first layout, exactly as WaveeShell used to seed itself
        // from the single global key: a dragged width is a preference and seeds verbatim, an undragged one takes the
        // design's tier ladder. The viewport is unknown here (no bounds callback yet), so this takes the pre-measure
        // fallback and the shell's tier effect commits the real tier before the first layout without a visible step.
        var pane = SidebarPaneState.Restore(settings, design, viewportWidth: 0f);
        Width = new Signal<float>(pane.Width);
        Collapsed = new Signal<bool>(pane.Collapsed);
        WidthUserSet = pane.WidthUserSet;

        ClassicPinnedOpen = new Signal<bool>(settings.Get(SidebarKeys.ClassicPinnedOpen));
        ClassicLibraryOpen = new Signal<bool>(settings.Get(SidebarKeys.ClassicLibraryOpen));
        ClassicPlaylistsOpen = new Signal<bool>(settings.Get(SidebarKeys.ClassicPlaylistsOpen));

        V3Filter = new Signal<int>(settings.Get(SidebarKeys.V3Filter));
        V3Qualifier = new Signal<int>(settings.Get(SidebarKeys.V3Qualifier));
        V3Sort = new Signal<int>(settings.Get(SidebarKeys.V3Sort));
        V3Desc = new Signal<bool>(settings.Get(SidebarKeys.V3Desc));
        V3View = new Signal<int>(settings.Get(SidebarKeys.V3View));
        V3GridSize = new Signal<int>(settings.Get(SidebarKeys.V3GridSize));
        V3SearchOpen = new Signal<bool>(settings.Get(SidebarKeys.V3SearchOpen));
        V3Search = new Signal<string>("");                          // SESSION-ONLY, never persisted, cleared on switch

        Pins = new SidebarPinStore();
        Entries = new SidebarEntries();
        _layout = SidebarCustomLayout.Empty;
        LoadDocument();
        Pins.OnChanged = Commit;                                    // every pin mutation is a commit point (#1)
    }

    /// <summary>Attach the app's UI-thread dispatcher. Async file completion never touches a signal directly on the pool:
    /// it is coalesced here, then published through <paramref name="post"/>. Idempotent; a later dispatcher replaces the
    /// earlier one after a shell remount.</summary>
    public void Activate(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        bool hasPending;
        lock (_persistenceGate)
        {
            _post = post;
            hasPending = _hasPendingPersistenceHealth;
        }
        if (hasPending) post(PublishPendingPersistenceHealth);
    }

    /// <summary>Reactive, redaction-safe persistence health. Load faults seed it before mount; completed writes update it
    /// only through <see cref="Activate"/> so every signal write remains UI-thread affine.</summary>
    public IReadSignal<SidebarWriteResult> PersistenceHealth => _persistenceHealth;

    // ───────────────────────────────────── design ─────────────────────────────────────

    /// <summary>The active design. The ONE signal <c>SidebarHost</c> reads, so a switch remounts the mode component.</summary>
    public Signal<SidebarDesign> Design { get; }

    /// <summary>The active design's width tiers (locked decision 14). <c>Peek</c>, not <c>Value</c>: the tier ladder
    /// effect already subscribes to <see cref="Design"/> explicitly and must not gain a second subscription here.</summary>
    public (float Narrow, float Mid, float Wide) Tiers => SidebarDesignInfo.Tiers(Design.Peek());

    /// <summary>The shell's tier effect publishes the live viewport width here (a plain field, not a signal — only
    /// <see cref="SwitchDesign"/> and the reset paths read it, and neither is a render).</summary>
    public void SetViewportWidth(float width)
    {
        if (width > 0f) _viewportWidth = width;
    }

    /// <summary>Snapshot the outgoing design's live state into its bag + settings, then reseed every live signal from the
    /// incoming design's bag, then flip <see cref="Design"/>. Applies LIVE (no restart). No-op when unchanged.
    ///
    /// The selection is persisted LAST, so a crash mid-switch reopens on the OLD design with its state intact.</summary>
    public void SwitchDesign(SidebarDesign next)
    {
        var cur = Design.Peek();
        if (next == cur) return;

        // 1 — SNAPSHOT the outgoing design.
        SidebarPaneState.Snapshot(_settings, cur, new SidebarPaneSnapshot(Width.Peek(), Collapsed.Peek(), WidthUserSet));
        FlushBagOf(cur);
        Flush();                       // issue any coalesced document write NOW, before the design flips

        // 2 — RESTORE the incoming design.
        var pane = SidebarPaneState.Restore(_settings, next, _viewportWidth);
        SeedBagOf(next);
        WidthUserSet = pane.WidthUserSet;
        // The three writes below land in ONE frame's effect flush: a signal write is DEFERRED (it marks dependents stale
        // and asks the host for a frame; the host drains once per frame), so a synchronous burst on the UI thread already
        // coalesces into a single layout commit — the pane animates one width/collapse step, not two. (The spec called for
        // Runtime.Batch here; the ReactiveRuntime is not reachable from a plain service, and Batch only throttles the
        // frame REQUEST, not the flush, so it would change nothing observable.)
        Width.SetIfChanged(pane.Width);
        Collapsed.SetIfChanged(pane.Collapsed);
        Design.Value = next;

        // 3 — PERSIST the selection last.
        _settings.Set(WaveeSettings.SidebarDesign, (int)next);
        WaveeLog.Instance.Info("sidebar", "sidebar.mode.changed", "Sidebar design changed.",
            WaveeLogField.Of("from", SidebarDesignInfo.Slug(cur)),
            WaveeLogField.Of("to", SidebarDesignInfo.Slug(next)));
    }

    /// <summary>Write the design's view-state signals back to its own keys. Classic: the three section flags · V3: the
    /// filter/qualifier/sort/desc/view/size/searchOpen septet · Curated: the template id (the layout document itself is
    /// already autosaved per command). <c>V3Search</c> is session-only and is never written.</summary>
    void FlushBagOf(SidebarDesign design)
    {
        switch (design)
        {
            case SidebarDesign.Classic:
                _settings.Set(SidebarKeys.ClassicPinnedOpen, ClassicPinnedOpen.Peek());
                _settings.Set(SidebarKeys.ClassicLibraryOpen, ClassicLibraryOpen.Peek());
                _settings.Set(SidebarKeys.ClassicPlaylistsOpen, ClassicPlaylistsOpen.Peek());
                break;
            case SidebarDesign.LibraryV3:
                _settings.Set(SidebarKeys.V3Filter, V3Filter.Peek());
                _settings.Set(SidebarKeys.V3Qualifier, V3Qualifier.Peek());
                _settings.Set(SidebarKeys.V3Sort, V3Sort.Peek());
                _settings.Set(SidebarKeys.V3Desc, V3Desc.Peek());
                _settings.Set(SidebarKeys.V3View, V3View.Peek());
                _settings.Set(SidebarKeys.V3GridSize, V3GridSize.Peek());
                _settings.Set(SidebarKeys.V3SearchOpen, V3SearchOpen.Peek());
                break;
            case SidebarDesign.Curated:
                _settings.Set(SidebarKeys.CuratedTemplateId, _layout.TemplateId);
                break;
        }
    }

    /// <summary>Reseed the incoming design's view-state signals from its keys. The V3 search box is CLEARED on every
    /// switch (session-only state, never persisted, never restored).</summary>
    void SeedBagOf(SidebarDesign design)
    {
        switch (design)
        {
            case SidebarDesign.Classic:
                ClassicPinnedOpen.SetIfChanged(_settings.Get(SidebarKeys.ClassicPinnedOpen));
                ClassicLibraryOpen.SetIfChanged(_settings.Get(SidebarKeys.ClassicLibraryOpen));
                ClassicPlaylistsOpen.SetIfChanged(_settings.Get(SidebarKeys.ClassicPlaylistsOpen));
                break;
            case SidebarDesign.LibraryV3:
                V3Filter.SetIfChanged(_settings.Get(SidebarKeys.V3Filter));
                V3Qualifier.SetIfChanged(_settings.Get(SidebarKeys.V3Qualifier));
                V3Sort.SetIfChanged(_settings.Get(SidebarKeys.V3Sort));
                V3Desc.SetIfChanged(_settings.Get(SidebarKeys.V3Desc));
                V3View.SetIfChanged(_settings.Get(SidebarKeys.V3View));
                V3GridSize.SetIfChanged(_settings.Get(SidebarKeys.V3GridSize));
                V3SearchOpen.SetIfChanged(_settings.Get(SidebarKeys.V3SearchOpen));
                break;
        }
        V3Search.SetIfChanged("");
    }

    // ─────────────────────── pane state (ACTIVE design) ───────────────────────

    /// <summary>The pane's expanded width. The shell BINDS this signal (it no longer owns one) — the docked pane and the
    /// narrow drawer share it, exactly as they shared <c>_sidebarWidth</c> before. <see cref="SwitchDesign"/> writes a new
    /// VALUE, never a new signal, so every existing binding stays live across a switch.</summary>
    public Signal<float> Width { get; }

    /// <summary>The user's collapse PREFERENCE — never "presented compact" (which is <c>narrowShell ∨ Collapsed</c> and
    /// stays the shell's own derived signal).</summary>
    public Signal<bool> Collapsed { get; }

    /// <summary>True once a committed seam drag pinned the ACTIVE design's width. While false that design's width follows
    /// its tier ladder; once true nothing but another drag may write it. Per design — pinning V3's width does not freeze
    /// Classic's ladder.</summary>
    public bool WidthUserSet { get; private set; }

    /// <summary>Drag-commit edge: clamp + persist the width AND latch <see cref="WidthUserSet"/>, for the active design
    /// only. The grip's <c>_moved</c> gate still decides whether this is called at all — a zero-movement click on the seam
    /// is not a width preference.</summary>
    public void CommitWidthDrag(float width)
    {
        var design = Design.Peek();
        float clamped = SidebarPaneState.CommitWidth(_settings, design, width);
        WidthUserSet = true;
        Width.SetIfChanged(clamped);
        _settings.Set(SidebarKeys.Collapsed(design), Collapsed.Peek());
    }

    /// <summary>Collapse toggle: persist <see cref="Collapsed"/> for the active design. NEVER touches
    /// <see cref="WidthUserSet"/> — collapsing the pane is not a width choice, and pinning on it would freeze every user
    /// at whatever tier they happened to collapse from.</summary>
    public void SetCollapsed(bool collapsed)
    {
        Collapsed.SetIfChanged(collapsed);
        _settings.Set(SidebarKeys.Collapsed(Design.Peek()), collapsed);
    }

    /// <summary>The responsive tier ladder's ONLY writer. Silently no-ops once the active design's width is pinned.</summary>
    public void SetResponsiveWidth(float width)
    {
        if (WidthUserSet) return;
        Width.SetIfChanged(width);
        _settings.Set(SidebarKeys.Width(Design.Peek()), width);
    }

    /// <summary>"Reset width": drop the active design's user-set latch and re-seed from its tier ladder, handing the width
    /// back to the responsive effect.</summary>
    public void ResetWidth()
    {
        var pane = SidebarPaneState.ResetWidth(_settings, Design.Peek(), _viewportWidth);
        WidthUserSet = false;
        Width.SetIfChanged(pane.Width);
    }

    // ─────────────────────── Classic bag ───────────────────────

    public Signal<bool> ClassicPinnedOpen { get; }
    public Signal<bool> ClassicLibraryOpen { get; }
    public Signal<bool> ClassicPlaylistsOpen { get; }

    /// <summary>Toggle one of Classic's three sections: writes the signal AND the setting, so the docked pane and the
    /// narrow drawer (two independent mounts) agree and the state survives a design round-trip.</summary>
    public void SetClassicSection(ClassicSection section, bool open)
    {
        switch (section)
        {
            case ClassicSection.Pinned:
                ClassicPinnedOpen.SetIfChanged(open);
                _settings.Set(SidebarKeys.ClassicPinnedOpen, open);
                break;
            case ClassicSection.Library:
                ClassicLibraryOpen.SetIfChanged(open);
                _settings.Set(SidebarKeys.ClassicLibraryOpen, open);
                break;
            case ClassicSection.Playlists:
                ClassicPlaylistsOpen.SetIfChanged(open);
                _settings.Set(SidebarKeys.ClassicPlaylistsOpen, open);
                break;
        }
    }

    // ─────────────────────── Library V3 bag ───────────────────────
    // Ints, not the enums, because the persisted form is an int (AppDataSettings has no enum arm) and because the chip /
    // flyout rows bind straight to them. Cast at the read site: (SidebarV3Filter)prefs.V3Filter.Value.

    public Signal<int> V3Filter { get; }
    public Signal<int> V3Qualifier { get; }
    public Signal<int> V3Sort { get; }
    public Signal<bool> V3Desc { get; }
    public Signal<int> V3View { get; }
    public Signal<int> V3GridSize { get; }
    public Signal<bool> V3SearchOpen { get; }

    /// <summary>The library-only search text. SESSION-ONLY: never persisted, never restored, cleared on every design
    /// switch (the <c>LibraryStateKeys</c> precedent — filter text starts empty each launch).</summary>
    public Signal<string> V3Search { get; }

    public void SetV3Filter(int v) { V3Filter.SetIfChanged(v); _settings.Set(SidebarKeys.V3Filter, v); }
    public void SetV3Qualifier(int v) { V3Qualifier.SetIfChanged(v); _settings.Set(SidebarKeys.V3Qualifier, v); }
    public void SetV3View(int view) { V3View.SetIfChanged(view); _settings.Set(SidebarKeys.V3View, view); }
    public void SetV3GridSize(int size) { V3GridSize.SetIfChanged(size); _settings.Set(SidebarKeys.V3GridSize, size); }

    /// <summary>Sort + direction commit as a pair (one flyout interaction). <c>V3Desc</c> is ignored while the sort is
    /// <c>Custom</c> — the direction affordance is hidden there — but the stored value is preserved so returning to
    /// another sort restores it.</summary>
    public void SetV3Sort(int sort, bool desc)
    {
        V3Sort.SetIfChanged(sort);
        _settings.Set(SidebarKeys.V3Sort, sort);
        if (sort == (int)SidebarV3Sort.Custom) return;
        V3Desc.SetIfChanged(desc);
        _settings.Set(SidebarKeys.V3Desc, desc);
    }

    public void SetV3SearchOpen(bool open)
    {
        V3SearchOpen.SetIfChanged(open);
        _settings.Set(SidebarKeys.V3SearchOpen, open);
        if (!open) V3Search.SetIfChanged("");
    }

    // ── the local V3 custom order (a view overlay; explicit resource drops may also mutate the rootlist) ──

    /// <summary>Entry/pin ids in the user's order (Playlists filter only). Entries absent from this list sort after it in
    /// projection order and stay there stably.</summary>
    public IReadOnlyList<string> V3CustomOrder => _v3CustomOrder;

    /// <summary>Bumped whenever <see cref="V3CustomOrder"/> changes — the render/sort dep.</summary>
    public IReadSignal<int> V3OrderVersion => _v3OrderVersion;
    readonly Signal<int> _v3OrderVersion = new(0);

    /// <summary>True when a local reorder is meaningful at all: only the Playlists filter with the Custom sort selected.</summary>
    public bool CanReorderV3 => V3Filter.Peek() == (int)SidebarV3Filter.Playlists
                             && V3Sort.Peek() == (int)SidebarV3Sort.Custom;

    /// <summary>Rank of an id in the stored order, or <see cref="int.MaxValue"/> when unranked (so an unknown id sorts
    /// last, stably, without a fabricated position).</summary>
    public int V3RankOf(string? id)
        => id is not null && _v3OrderRank.TryGetValue(id, out int r) ? r : int.MaxValue;

    /// <summary>Commit a user reorder (drag end / keyboard drop). Persists the document.</summary>
    public void SetV3CustomOrder(IReadOnlyList<string>? orderedIds)
    {
        _v3CustomOrder.Clear();
        _v3OrderRank.Clear();
        if (orderedIds is not null)
            for (int i = 0; i < orderedIds.Count; i++)
            {
                string id = orderedIds[i];
                if (string.IsNullOrEmpty(id) || _v3OrderRank.ContainsKey(id)) continue;
                _v3OrderRank[id] = _v3CustomOrder.Count;
                _v3CustomOrder.Add(id);
            }
        _v3OrderVersion.Value = _v3OrderVersion.Peek() + 1;
        Commit();
    }

    // ── V3 folder expansion (an unbounded id set ⇒ the document, not settings) ──

    public bool IsFolderExpanded(string? folderId) => folderId is not null && _expandedFolders.Contains(folderId);

    /// <summary>The expanded folder id SET, for <c>SidebarProjectionInput.ExpandedFolders</c> (the row planner needs the
    /// set itself, not the predicate). A live read-only view of the owned set — never a copy, so a rebuild allocates
    /// nothing for it.</summary>
    public IReadOnlySet<string> ExpandedFolders => _expandedFolders;

    public IReadSignal<int> FolderVersion => _folderVersion;
    readonly Signal<int> _folderVersion = new(0);

    public void SetFolderExpanded(string? folderId, bool expanded)
    {
        if (string.IsNullOrEmpty(folderId)) return;
        bool changed = expanded ? _expandedFolders.Add(folderId) : _expandedFolders.Remove(folderId);
        if (!changed) return;
        _folderVersion.Value = _folderVersion.Peek() + 1;
        // COALESCED, not synchronous. A folder toggle used to snapshot + serialize the whole layout document inside the
        // click — on the very frame the expansion has to plan, publish, realize and arm on. The renderer drains this on
        // the next frame (SidebarPane.SchedulePrefsCommit); the store already coalesces the file write itself.
        _commitPending = true;
    }

    bool _commitPending;

    /// <summary>Issue a write a coalescing mutator deferred. Idempotent, and a no-op when nothing is pending.</summary>
    public void FlushPendingCommit()
    {
        if (!_commitPending) return;
        Commit();
    }

    public void ToggleFolder(string? folderId) => SetFolderExpanded(folderId, !IsFolderExpanded(folderId));

    // ─────────────────────── pins (SHARED across all three designs) ───────────────────────

    /// <summary>The one pin list, shared by every design (locked decision 4). Indexable/enumerable directly; mutate
    /// through it or through the flat helpers below (which exist so an <c>AppAction</c> never has to reach two levels
    /// deep). Unlimited — there is no cap and no eviction.</summary>
    public SidebarPinStore Pins { get; }

    public IReadSignal<int> PinsVersion => Pins.Version;
    public bool IsPinned(string? pinId) => Pins.IsPinned(pinId);

    /// <summary>Append a pin. False when already pinned (idempotent — the menu shows Unpin in that state).</summary>
    public bool Pin(SidebarPin pin) => Pins.Pin(pin);

    /// <summary>Remove by id. Returns the removed index (for the undo toast) or -1 when absent.</summary>
    public int Unpin(string? pinId) => Pins.Unpin(pinId);

    /// <summary>Undo path for <see cref="Unpin"/>: reinsert at its former index (clamped).</summary>
    public void InsertPin(SidebarPin pin, int index) => Pins.Insert(pin, index);

    public void MovePin(int fromIndex, int toIndex) => Pins.Move(fromIndex, toIndex);

    /// <summary>Refresh a pin's cached display name from live library data (a renamed playlist). No-op when unchanged;
    /// coalesced into the next commit — it never commits alone. Called by the projection, never by rows.</summary>
    public void TouchPin(string? pinId, string? name)
    {
        if (Pins.Touch(pinId, name)) _pinNamesDirty = true;
    }

    // ─────────────────────── the entry projection cell ───────────────────────

    /// <summary>The unified <c>SidebarLibraryEntry</c> projection the V3 and Curated modes read. The cell is owned here
    /// (one projection, shared by the docked pane and the drawer — two mounts, one list) and REBUILT by
    /// <see cref="Binder"/> through <c>SidebarProjection.Build(cell.Buffer, …)</c> + <c>cell.Publish(…)</c>.</summary>
    public SidebarEntries Entries { get; }

    /// <summary>The ONE driver of <see cref="Entries"/> — <c>SidebarProjectionBinder</c>, set by <c>Services</c> right
    /// after both exist. Exposed here so a mode surface that already has the preferences in context can reach the planner
    /// input (<c>prefs.Binder?.CurrentInput</c>) without also resolving <c>Services</c>. Null in a headless/unit context,
    /// where <see cref="Entries"/> simply stays empty.</summary>
    public SidebarProjectionBinder? Binder { get; internal set; }

    // ─────────────────────── the Curated layout + editor ───────────────────────

    /// <summary>PHASE 2 / Decision B — the live "customize" session, shared by the docked pane (the canvas) and the
    /// companion page. It lives HERE because those two are siblings in the tree: neither can read a context the other
    /// provides, and the customizer must never talk to the renderer (iron rule 6). This service is already the one
    /// owner of sidebar state and is provided at the app root, so it is the shared-state seam both surfaces reach
    /// through <see cref="Slot"/>. Session-only and never persisted — which section is expanded is editor ergonomics,
    /// not document state.</summary>
    public SidebarEditSession Edit { get; } = new();

    /// <summary>The current Curated document — an immutable snapshot, replaced wholesale. The live pane and the
    /// customizer both render from it, so there is exactly one document and one version signal.</summary>
    public SidebarCustomLayout Layout => _layout;

    public IReadSignal<int> LayoutVersion => _layoutVersion;
    readonly Signal<int> _layoutVersion = new(0);

    /// <summary>Apply one editor command — the single mutation entry point (§C3.4). Runs the pure reducer; on
    /// <c>Changed</c> it pushes the PRE-IMAGE onto the undo ring, clears redo, replaces <see cref="Layout"/>, bumps
    /// <see cref="LayoutVersion"/> and autosaves. A rejected command (unknown section id, out-of-range index, no-op)
    /// changes nothing: no undo push, no version bump, no commit. Returns why, for the customizer's inline message.</summary>
    public SidebarRejectReason Dispatch(SidebarCommand command)
    {
        var before = _layout;
        var result = SidebarLayoutReducer.Apply(before, command, PinKeySet());
        if (!result.Changed)
        {
            if (result.Reason is not SidebarRejectReason.None and not SidebarRejectReason.NoChange)
                WaveeLog.Instance.Warn("sidebar", "sidebar.customizer.command.rejected",
                    "A sidebar customization command was rejected.",
                    WaveeLogField.Of("command", command.LabelLocKey),
                    WaveeLogField.Of("reason", result.Reason.ToString()));
            return result.Reason;
        }

        _undo.Push(before, command);                    // records the pre-image AND clears redo
        _layout = result.Layout;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        Commit();
        return SidebarRejectReason.None;
    }

    /// <summary>Foundation's F.2.2 name for <see cref="Dispatch"/>, kept as the documented alias so both spec sections'
    /// call sites compile against one implementation.</summary>
    public void ApplyCurated(SidebarCommand command) => Dispatch(command);

    // ───────────────────── the shell TOP BAR band ─────────────────────
    // Sugar only: every mutation is an ordinary SidebarCommand through Dispatch, so the band gets the SAME undo ring, the
    // same rejection contract, the same LayoutVersion bump and the same autosave as every sidebar edit. Nothing structural
    // is added here — the list itself lives on the document (SidebarCustomLayout.TopBar).

    /// <summary>What the shell's shortcut band renders: the authored list, or the built-in default (Home) when the user has
    /// never customized it. Never null, never empty-by-accident — an EMPTY list means the user emptied it on purpose.
    /// <para>Read it inside a render together with <see cref="LayoutVersion"/> (that read is the subscription).</para></summary>
    public IReadOnlyList<SidebarItemSpec> TopBar => _layout.EffectiveTopBar;

    /// <summary>True when the band is at <see cref="SidebarLayoutReducer.MaxTopBarItems"/> — the customizer greys its "add"
    /// affordance off this rather than discovering the cap through a rejection.</summary>
    public bool TopBarFull => TopBar.Count >= SidebarLayoutReducer.MaxTopBarItems;

    // The three verbs are named …Shortcut, not …Item: a method sharing a command record's name (AddTopBarItem) would read
    // as a collision at every `new AddTopBarItem(…)` inside this class — the same reason ApplyTemplateId is not
    // "ApplyTemplate".

    /// <summary>Append (default) or insert a shortcut. Returns the rejection reason, or <c>None</c> on success —
    /// <see cref="SidebarRejectReason.SectionCapReached"/> is the cap, <see cref="SidebarRejectReason.DuplicateItem"/> the
    /// same (target, key) already in the band.</summary>
    public SidebarRejectReason AddTopBarShortcut(SidebarItemSpec item, int index = -1)
        => Dispatch(new AddTopBarItem(item, index < 0 ? TopBar.Count : index));

    /// <summary>Reorder the band. <paramref name="toIndex"/> is interpreted after the removal (the Reorderable contract).</summary>
    public SidebarRejectReason MoveTopBarShortcut(int fromIndex, int toIndex)
        => Dispatch(new MoveTopBarItem(fromIndex, toIndex));

    /// <summary>Remove a shortcut by item id. Undo (the toast) re-inserts at <paramref name="itemId"/>'s former index —
    /// callers snapshot the item + index BEFORE calling, exactly like <c>PinActions.Unpin</c>.</summary>
    public SidebarRejectReason RemoveTopBarShortcut(string itemId) => Dispatch(new RemoveTopBarItem(itemId));

    /// <summary>Index of a tile in the effective band, or -1 — the "restore at its former position" input for the undo toast.</summary>
    public int TopBarIndexOf(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return -1;
        var band = TopBar;
        for (int i = 0; i < band.Count; i++)
            if (string.Equals(band[i].Id, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Cached (pin id + pin uri) set for the reducer's lazy Pinned-override prune (§C1.6) — an override may be
    /// keyed either way. Rebuilt only when the pin store's version moves; Peek so Dispatch never subscribes.</summary>
    IReadOnlySet<string> PinKeySet()
    {
        int v = Pins.Version.Peek();
        if (_pinKeySet is null || _pinKeySetVersion != v)
        {
            var set = _pinKeySet ??= new HashSet<string>(StringComparer.Ordinal);
            set.Clear();
            var items = Pins.Items;
            for (int i = 0; i < items.Count; i++)
            {
                set.Add(items[i].Id);
                if (items[i].Uri is { Length: > 0 } uri) set.Add(uri);
            }
            _pinKeySetVersion = v;
        }
        return _pinKeySet;
    }
    HashSet<string>? _pinKeySet;
    int _pinKeySetVersion = -1;

    /// <summary>Replace the layout with a named template's sections — an ordinary single-step undoable command, which is
    /// why the confirmation dialog can honestly say "You can undo this."</summary>
    public SidebarRejectReason ApplyTemplateId(string templateId) => Dispatch(new ApplyTemplate(templateId));

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    /// <summary>Loc KEY of the step Undo/Redo would take ("Undo: Add section"), for the tooltip + a11y announcement.</summary>
    public string? UndoLabel => _undo.UndoLabelLocKey;
    public string? RedoLabel => _undo.RedoLabelLocKey;

    /// <summary>An undo is itself autosaved — closing the app after one keeps the undone state (§C3.4 step 4).</summary>
    public void Undo()
    {
        if (!_undo.TryUndo(_layout, out var restored, out _)) return;
        _layout = restored;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        Commit();
    }

    public void Redo()
    {
        if (!_undo.TryRedo(_layout, out var restored, out _)) return;
        _layout = restored;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        Commit();
    }

    // ─────────────────────── document health ───────────────────────

    void OnWriteCompleted(SidebarWriteResult result)
    {
        Action<Action>? post;
        lock (_persistenceGate)
        {
            _pendingPersistenceHealth = result;
            _hasPendingPersistenceHealth = true;
            post = _post;
        }
        post?.Invoke(PublishPendingPersistenceHealth);
    }

    void PublishPendingPersistenceHealth()
    {
        SidebarWriteResult next;
        lock (_persistenceGate)
        {
            if (!_hasPendingPersistenceHealth) return;
            next = _pendingPersistenceHealth;
            _hasPendingPersistenceHealth = false;
        }
        _persistenceHealth.SetIfChanged(next);
    }

    /// <summary>Non-<c>None</c> ⇒ the built-in Curated default is loaded in memory, the unreadable file is untouched, and
    /// every write is suppressed. Surfaced ONLY in the customizer as an InfoBar warning — never a startup toast, never a
    /// dialog: the user must not be interrupted at launch by a preferences problem.</summary>
    public SidebarLoadFault Fault { get; private set; }

    /// <summary>The exception/validation detail behind <see cref="Fault"/>, for the customizer warning.</summary>
    public string? FaultDetail { get; private set; }

    /// <summary>Why the LAST write refused the disk (LAYOUT V2's 64 KiB-per-section / 2 MiB-per-document budgets), or
    /// <c>None</c>. A pure PASSTHROUGH of the store's verdict — persistence classifies, this class only surfaces (the
    /// <see cref="Fault"/> precedent). Unlike <see cref="Fault"/> it does not latch: the next in-budget commit clears it,
    /// because shrinking the oversized section IS the recovery. Read by the customizer's save-fault banner, the only
    /// surface that can tell the user their edits are not reaching disk (<c>Commit</c> otherwise no-ops silently).</summary>
    public SidebarSaveFault SaveFault => _store.SaveFault;

    /// <summary>Which section / how many bytes, for that banner. Null when <see cref="SaveFault"/> is <c>None</c>.</summary>
    public string? SaveFaultDetail => _store.SaveFaultDetail;

    /// <summary>The customizer's "Start fresh": rename the unreadable file aside, clear <see cref="Fault"/>, re-enable
    /// writes, and commit the in-memory document so the file exists again.</summary>
    public void DiscardCorruptDocument()
    {
        if (Fault == SidebarLoadFault.None) return;
        _store.DiscardCorrupt();
        if (_store.WritesBlocked) return;    // move-aside failed: preserve the original bytes and keep writes suppressed
        Fault = SidebarLoadFault.None;
        FaultDetail = null;
        _carry = SidebarWireCarry.Empty;      // nothing on disk to be forward-compatible WITH any more
        Commit();
    }

    void LoadDocument()
    {
        var load = _store.Load();
        Fault = load.Fault;
        FaultDetail = load.Detail;
        _loaded = true;
        if (load.Fault != SidebarLoadFault.None)
        {
            _persistenceHealth.Value = new SidebarWriteResult(
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

        string storedTemplate = _settings.Get(SidebarKeys.CuratedTemplateId);
        if (load.Fault != SidebarLoadFault.None || load.Doc is null)
        {
            // A first run is NOT a fault (Doc null + Fault None). Both paths load the built-in default in memory; the only
            // difference is whether writes are suppressed, which the store itself enforces. On a fault the pin list is
            // empty for the session (pins live in the same document) — the honest consequence, and the customizer says so.
            _layout = SidebarLayoutDefaults.LayoutOf(storedTemplate);
            return;
        }

        Pins.LoadFrom(PinsFromDto(load.Doc.Pins));
        SetV3StateFromDto(load.Doc.V3);
        // ReadCurated never throws and never DROPS a section whose kind this build does not know: it moves those into the
        // carry, which WriteCurated re-emits at their original index. Holding the carry is what makes a
        // newer-build document opened by an older build non-destructive (locked decision 8's preserve-don't-destroy stance).
        var read = SidebarLayoutWire.ReadCurated(load.Doc.Curated);
        _carry = read.Carry;
        _carry.CaptureDoc(load.Doc);                                // envelope-level unknown members ride the same carry
        _layout = load.Doc.Curated is null || read.Layout.Sections.Count == 0
            ? SidebarLayoutDefaults.LayoutOf(storedTemplate)
            : read.Layout;
        // The top-bar band lives on the ENVELOPE (one global list, like the pins), so it is folded in AFTER the curated
        // payload — including on the "curated payload was empty, use the default sections" path above, where a customized
        // band must still survive.
        _layout = _layout with { TopBar = SidebarLayoutWire.ReadTopBar(load.Doc.TopBar, _carry) };
    }

    // ── pins ⇄ wire. The pin RECORD is app-side and this store owns the mapping, but the enum⇄string/legacy-int TABLES
    // it calls into live on SidebarLayoutWire (Persistence/SidebarLayoutDoc.cs) — the one translation layer, and the one
    // place Wavee.Tests (which does not include this file) can reach to pin the exhaustiveness contract.
    //
    // Read prefers SidebarPinDto.EntityKind (the string form); a document written before the SidebarPinKind→
    // SidebarEntryKind unification (2026-08-19) has no EntityKind, so that pin falls back to the legacy int. Either
    // arm that fails to resolve DROPS the pin rather than guessing a kind — the pin's Kind is load-bearing for
    // rendering/navigation, and there is no richer "carry" bucket for a single scalar the way an unknown SECTION kind
    // gets one, so silently rendering a wrong kind is a worse failure than an honestly logged drop.
    static List<SidebarPin>? PinsFromDto(SidebarPinDto[]? dto)
    {
        if (dto is null || dto.Length == 0) return null;
        var list = new List<SidebarPin>(dto.Length);
        for (int i = 0; i < dto.Length; i++)
        {
            var d = dto[i];
            if (d is null || string.IsNullOrEmpty(d.Id)) continue;   // an id-less row has no identity — it cannot be a pin
            SidebarEntryKind kind;
            if (!string.IsNullOrEmpty(d.EntityKind))
            {
                if (!SidebarLayoutWire.TryParsePinKind(d.EntityKind, out kind))
                {
                    WaveeLog.Instance.Warn("sidebar", "sidebar.pin.unknown_kind",
                        "Dropped a pinned row with an unrecognized kind string.",
                        WaveeLogField.Of("pin_id", d.Id!), WaveeLogField.Of("kind", d.EntityKind));
                    continue;
                }
            }
            else if (!SidebarLayoutWire.TryLegacyPinKind(d.Kind, out kind))
            {
                WaveeLog.Instance.Warn("sidebar", "sidebar.pin.unknown_legacy_kind",
                    "Dropped a pinned row with an unrecognized legacy kind int.",
                    WaveeLogField.Of("pin_id", d.Id!), WaveeLogField.Of("kind", d.Kind));
                continue;
            }
            list.Add(new SidebarPin(d.Id!, kind, d.Uri ?? "", d.Name ?? "", d.AddedAtMs));
        }
        return list;
    }

    // Write BOTH the new string and the legacy int on every save (preserve-don't-destroy for a downgrade — the same
    // stance ReadCurated/WriteCurated take for a section this build doesn't recognize).
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

    /// <summary>The projection publishes new first-observation stamps here after a rebuild that produced any
    /// (<c>SidebarProjectionResult.NewFirstSeenStamps &gt; 0</c> — commit point #9, at most one commit per rebuild).
    /// Passing null leaves the stored map untouched, so a rebuild that observed nothing new costs nothing.</summary>
    public void PublishFirstSeen(SidebarFirstSeenDto[]? stamps)
    {
        if (stamps is null) return;
        _firstSeen = stamps;
        Commit();
    }

    /// <summary>The stored first-seen stamps, for the projection to seed its map from at build time.</summary>
    public SidebarFirstSeenDto[]? FirstSeen => _firstSeen;

    void SetV3StateFromDto(SidebarV3Dto? v3)
    {
        _v3CustomOrder.Clear();
        _v3OrderRank.Clear();
        _expandedFolders.Clear();
        _firstSeen = null;
        if (v3 is null) return;
        _firstSeen = v3.FirstSeen;
        if (v3.CustomOrder is { } order)
            for (int i = 0; i < order.Length; i++)
            {
                string? id = order[i];
                if (string.IsNullOrEmpty(id) || _v3OrderRank.ContainsKey(id)) continue;
                _v3OrderRank[id] = _v3CustomOrder.Count;
                _v3CustomOrder.Add(id);
            }
        if (v3.ExpandedFolders is { } folders)
            for (int i = 0; i < folders.Length; i++)
                if (!string.IsNullOrEmpty(folders[i])) _expandedFolders.Add(folders[i]!);
    }

    // ─────────────────────── persistence ───────────────────────

    /// <summary>Snapshot the whole document on the UI thread and hand it to the store, which serializes + writes on the
    /// pool and coalesces a burst into one file write (its monotonic commit sequence: a queued write aborts when a newer
    /// snapshot arrives). That store-side coalescing is why no burst timer is needed here.</summary>
    void Commit()
    {
        if (!_loaded || Fault != SidebarLoadFault.None) return;
        _commitPending = false;   // any commit absorbs a coalesced one
        _pinNamesDirty = false;
        _store.Commit(BuildSnapshot());
    }

    /// <summary>Force any pending write to be issued now (called by <see cref="SwitchDesign"/> and by the customizer on
    /// close). Never blocks on the pool write. Also the one path that flushes a name-cache refresh, which by contract
    /// never commits on its own.</summary>
    public void Flush()
    {
        if (!_pinNamesDirty && !_commitPending) return;
        Commit();
    }

    /// <summary>The whole document, snapshotted on the UI thread. <c>UpdatedAtMs</c>/<c>AppVersion</c> are stamped by
    /// <c>SidebarLayoutStore.Commit</c>, not here.</summary>
    SidebarLayoutDocDto BuildSnapshot()
    {
        var doc = new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Pins = PinsToDto(Pins),
            V3 = new SidebarV3Dto
            {
                CustomOrder = _v3CustomOrder.Count > 0 ? _v3CustomOrder.ToArray() : null,
                ExpandedFolders = ExpandedFolderArray(),
                FirstSeen = _firstSeen,
            },
            Curated = SidebarLayoutWire.WriteCurated(_layout, _carry),
            TopBar = SidebarLayoutWire.WriteTopBar(_layout.TopBar, _carry),
        };
        _carry.ReattachDoc(doc);      // an envelope member a NEWER build added is re-emitted, never dropped
        return doc;
    }

    string[]? ExpandedFolderArray()
    {
        if (_expandedFolders.Count == 0) return null;
        var arr = new string[_expandedFolders.Count];
        int i = 0;
        foreach (string id in _expandedFolders) arr[i++] = id;
        return arr;
    }
}

/// <summary>
/// The entry-projection CELL: the reusable buffer + the derived flags every V3/Curated surface reads. Owned by
/// <see cref="SidebarPreferences"/> so the docked pane and the narrow drawer observe ONE projection (one mode, one
/// state); rebuilt by the mode side, which owns the library/history inputs.
///
/// Allocation discipline (F.7.5): the LIST IS REUSED across rebuilds. A producer clears + refills <see cref="Buffer"/>
/// through <c>SidebarProjection.Build</c> and then calls <see cref="Publish"/> exactly once, which bumps
/// <see cref="Version"/> — so consumers re-render on a version change and never on a per-frame read.
/// </summary>
public sealed class SidebarEntries
{
    readonly List<SidebarLibraryEntry> _entries = new();
    readonly Signal<int> _version = new(0);

    /// <summary>The caller-owned rebuild buffer (F.7.5's <c>into</c>). Fill it, then <see cref="Publish"/>.</summary>
    public List<SidebarLibraryEntry> Buffer => _entries;

    /// <summary>The published entry list, pins-first: the pin band (pin-store order, length <see cref="PinCount"/>),
    /// then Liked Songs when unpinned, then the sorted remainder.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Current => _entries;

    public IReadSignal<int> Version => _version;

    public FluentGpu.Signals.LoadState State { get; private set; } = FluentGpu.Signals.LoadState.Pending;
    public Exception? Error { get; private set; }

    /// <summary>True while any kind the current filter contributes is still loading — the skeleton gate. Deliberately
    /// per-CONTRIBUTING-kind: a pending Shows load must not skeleton the Playlists filter.</summary>
    public bool AnyContributingKindPending { get; private set; }

    /// <summary>Whether the data supports the qualifier chips at all (By you / By Spotify / Mixed). False ⇒ the
    /// qualifier rail is not rendered, rather than rendered empty (locked decision 10).</summary>
    public bool QualifiersAvailable { get; private set; }

    /// <summary>Length of the leading pin band in <see cref="Current"/>.</summary>
    public int PinCount { get; private set; }

    /// <summary>The exact shadow snapshot of the last published rebuild — the CONTENT GATE (see
    /// <see cref="SidebarEntriesShadow"/>). Owned here because <see cref="Publish"/> is the one place a rebuild becomes
    /// visible.</summary>
    readonly SidebarEntriesShadow _shadow = new();

    /// <summary>The SECOND content gate, over the binder's FULL flattened projection (its <c>_all</c>) rather than the
    /// published rows above. The planner (<c>SidebarPane.PlanDep</c>) reads the full projection, but a collapsed
    /// folder's children are excluded from the published buffer by design (V3-list concern) — so a hydration that
    /// changes only a playlist inside a collapsed folder would never flip the published shadow, and the pane would
    /// keep drawing a stale row for it. This shadow catches exactly that case.</summary>
    readonly SidebarEntriesShadow _fullShadow = new();

    /// <summary>Publish a completed rebuild: at most one version bump per rebuild, never per entry — and NO bump at all
    /// when the rebuild landed on byte-identical content.
    ///
    /// <para>The binder pump re-projects on every queue/track/play-log/history move, and almost all of those rebuilds
    /// reproduce the same rows; bumping unconditionally re-planned every sidebar pane (2 storms per track boundary,
    /// 3 per navigation) for nothing. The compare is EXACT — meta + count + elementwise — never a fingerprint: a hash
    /// collision would freeze the sidebar on stale content.</para>
    ///
    /// <para>The version now means "the binder's projection changed" — either the published rows changed, OR the
    /// binder's full flattened projection changed (<paramref name="fullProjection"/>). The two can disagree: a
    /// hydration that only touches a playlist inside a collapsed folder never appears in the published rows (the
    /// V3 list omits a collapsed folder's children) but does appear in the full projection the planner reads, so
    /// without the second gate the pane would keep drawing that row's stale raw-URI/"0 songs" content. Both shadows
    /// are evaluated unconditionally — each must capture its own snapshot regardless of the other's result.</para>
    ///
    /// <para>The meta fields are set either way, so a consumer that peeks them without the version still reads the
    /// truth; <see cref="Current"/> is the live buffer the producer just refilled, so it is correct across a skipped
    /// bump by construction (identical content is identical content).</para></summary>
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
