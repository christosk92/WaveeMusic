// ── Shell/NavEditModel.cs ──────────────────────────────────────────────────────────────────────────────────────────
// Edit mode as model state and verbs: the Outline's rows, the layout ops a checkbox, a drag or a key makes, undo and redo,
// the "you showed an empty section" note, and the key dispatch table
//
// Role: CORE · Plan: technical-plan.md §5.1 row C11 (BP X28) · design C.3, C.5, Q3, Q6, Q8, V.11
//
// Every edit is a `SidebarOp` through `Sidebar.Dispatch` (the one layout mutation path, with the undo ring behind it); this
// is the layer that decides WHICH op a gesture means. The Classic edit pane (`Views/Nav/ClassicNavEdit.cs`) draws the Outline and
// forwards its checkboxes, its reorders and its keys here. A key arrives as a `NavEditKey` plus `NavEditMods`, never an
// engine event, so the whole table is unit-testable.
//
// THREADING: UI thread only, like the service it drives.

using FluentGpu.Signals;

namespace Wavee;

/// <summary>The keys Edit mode reacts to; everything else is <see cref="Other"/>.</summary>
public enum NavEditKey : byte { Other, Enter, Up, Down, Escape, Z, Y }

/// <summary>Held modifiers. <see cref="Other"/> is any modifier the table does not name (a key with it is never ours).</summary>
[Flags]
public enum NavEditMods : byte { None = 0, Alt = 1, Shift = 2, Ctrl = 4, Other = 8 }

/// <summary>Edit mode (design C.3), one per <see cref="NavModel"/>.</summary>
public sealed class NavEditModel
{
    readonly NavModel _model;

    internal NavEditModel(NavModel model) => _model = model;

    // ── the mode ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Edit mode is on.</summary>
    public IReadSignal<bool> Editing => Sidebar.Editing;

    /// <summary>Enter Edit (refused with a toast while a drag is live).</summary>
    public void Enter() => Sidebar.EnterEdit();

    /// <summary>Done / Esc: back to the sidebar, focus restored to the row that had it before Edit. The undo ring is KEPT.</summary>
    public void Exit() => Sidebar.ExitEdit();

    /// <summary>The layout state both overlays live in.</summary>
    public SidebarLayoutState State => Sidebar.State;

    // ── undo and redo ──────────────────────────────────────────────────────────────────────────────────────────────────

    public bool CanUndo => Sidebar.Ring.CanUndo;
    public bool CanRedo => Sidebar.Ring.CanRedo;

    /// <summary>Bumped on every ring change (the edit bar re-renders on it).</summary>
    public IReadSignal<int> RingVersion => Sidebar.RingVersion;

    public void Undo() => Sidebar.Undo();
    public void Redo() => Sidebar.Redo();

    /// <summary>"Unpin all shortcuts" (Q17): every route / module pin as ONE batch under ONE toast.</summary>
    public void UnpinAllShortcuts() => Sidebar.UnpinAllShortcutsRecorded();

    // ── the Outline ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the Outline for the active layout into <paramref name="rows"/> and the movable sections' slots into
    /// <paramref name="band"/>, and return the layout. SUBSCRIBES the caller to the layout, its version, the pin store and the
    /// undo ring.</summary>
    public SidebarLayoutId ReadOutline(bool showAllPins, List<SidebarOutlineRow> rows, List<string> band)
    {
        var layout = Sidebar.Layout.Value;
        _ = Sidebar.LayoutVersion.Value;
        _ = Sidebar.PinsVersion.Value;
        _ = Sidebar.RingVersion.Value;
        var state = Sidebar.State;
        int playlists = _model.Rootlist?.Count ?? 0;
        SidebarEditRules.Outline(state, layout, Sidebar.Pins.Items, Sidebar.PinnedLocked(), showAllPins, playlists, rows);
        SidebarEditRules.Band(state.Of(layout), band);
        return layout;
    }

    /// <summary>True when a section has no rows to show right now: the same sources the section renders from.</summary>
    public bool HasNothingToShow(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => Sidebar.Pins.Count == 0,
        SidebarSectionKind.Playlists => _model.Rootlist is not { Count: > 0 },
        SidebarSectionKind.Recent => _model.Feeds.Played is not { Count: > 0 },
        SidebarSectionKind.NewReleases => _model.Feeds.NewReleases is not { Count: > 0 },
        _ => false,
    };

    // ── "you showed an empty section" ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title of a section the user showed in Edit while it had nothing to show (one toast on Done), or null.</summary>
    public string? EnabledEmptySection { get; private set; }

    /// <summary>Read the note and clear it (the exit tail).</summary>
    internal string? TakeEnabledEmptySection()
    {
        string? name = EnabledEmptySection;
        EnabledEmptySection = null;
        return name;
    }

    // ── the ops a checkbox, a drag or a key makes ──────────────────────────────────────────────────────────────────────

    /// <summary>A section's Show checkbox. Showing an empty section is allowed; Done says so once (design C.3). Returns whether
    /// the op was accepted.</summary>
    public bool SetSectionShown(SidebarLayoutId layout, string id, SidebarSectionKind kind, string title, bool shown)
    {
        if (Sidebar.Dispatch(new SetSectionShown(layout, id, shown)) != SidebarOpReject.None) return false;
        if (shown && HasNothingToShow(kind)) EnabledEmptySection = title;
        else if (!shown && EnabledEmptySection == title) EnabledEmptySection = null;
        return true;
    }

    /// <summary>The footer Settings row's Show checkbox.</summary>
    public void SetSettingsShown(SidebarLayoutId layout, bool shown) => Sidebar.Dispatch(new SetSectionShown(layout, "settings", shown));

    /// <summary>An item's Show checkbox (or Enter on a Collections row). A hidden Library kind's chip is gone: the active filter
    /// steps off it, as the menu's Filters rows do. Returns whether the op was accepted.</summary>
    public bool SetItemShown(SidebarLayoutId layout, string sectionId, string itemId, SidebarSectionKind kind, bool shown)
    {
        if (Sidebar.Dispatch(new SetItemShown(layout, sectionId, itemId, shown)) != SidebarOpReject.None) return false;
        if (!shown && kind == SidebarSectionKind.Library)
            Sidebar.SetLibraryFilter(SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Peek(), Sidebar.Doc.Library.HiddenKinds));
        return true;
    }

    /// <summary>A section dragged (or moved by key) to band slot <paramref name="to"/>.</summary>
    public void MoveSection(string sectionId, int to) => Sidebar.Dispatch(new MoveSection(Sidebar.Layout.Peek(), sectionId, to));

    /// <summary>A Collections page dragged (or moved by key) to <paramref name="to"/>.</summary>
    public void MoveItem(string sectionId, string itemId, int to) => Sidebar.Dispatch(new MoveItem(Sidebar.Layout.Peek(), sectionId, itemId, to));

    // ── the key dispatch table (design Q8) ─────────────────────────────────────────────────────────────────────────────

    static bool IsStep(NavEditKey key, NavEditMods mods) => key is NavEditKey.Up or NavEditKey.Down && (mods & NavEditMods.Alt) != 0;

    /// <summary>The edit pane's own keys: Esc leaves Edit; Ctrl+Z / Ctrl+Y undo and redo. True = handled.</summary>
    public bool OnPaneKey(NavEditKey key, NavEditMods mods)
    {
        if (key == NavEditKey.Escape && mods == NavEditMods.None)
        {
            Exit();
            return true;
        }
        if (mods == NavEditMods.Ctrl && key is NavEditKey.Z or NavEditKey.Y)
        {
            if (key == NavEditKey.Z) Undo(); else Redo();
            return true;
        }
        return false;
    }

    /// <summary>Keys the lifted Reorderable does not take, on a section wrapper in band slot <paramref name="slot"/> of
    /// <paramref name="bandCount"/>: Enter toggles Show (when the section can be hidden); Alt+↑/↓ moves one step, Alt+Shift+↑/↓ to
    /// the edge. True = handled.</summary>
    public bool OnSectionKey(string id, int slot, int bandCount, NavEditKey key, NavEditMods mods)
    {
        var layout = Sidebar.Layout.Peek();
        if (key == NavEditKey.Enter && mods == NavEditMods.None)
        {
            var s = Sidebar.State.Of(layout).Find(id);
            if (s is not null && SidebarCatalogue.TryKindOf(id, out var kind) && SidebarCatalogue.Hideable(kind))
                Sidebar.Dispatch(new SetSectionShown(layout, id, s.Hidden));
            return true;
        }
        if (IsStep(key, mods))
        {
            int to = SidebarEditRules.MoveTarget(slot, bandCount, key == NavEditKey.Up ? -1 : 1, toEdge: (mods & NavEditMods.Shift) != 0);
            if (to >= 0 && to != slot) Sidebar.Dispatch(new MoveSection(layout, id, to));
            return true;
        }
        return false;
    }

    /// <summary>Alt+↑/↓ on a pin row's wrapper (Alt+Shift: to the edge). The same in both layouts (Q3). Enter stops here: a pin
    /// has no Show, and the key must not reach the section wrapper and toggle Pinned. True = handled.</summary>
    public bool OnPinKey(int index, NavEditKey key, NavEditMods mods)
    {
        if (key == NavEditKey.Enter) return true;
        if (!IsStep(key, mods)) return false;
        int to = SidebarEditRules.MoveTarget(index, Sidebar.Pins.Count, key == NavEditKey.Up ? -1 : 1, toEdge: (mods & NavEditMods.Shift) != 0);
        if (to >= 0 && to != index) _model.MovePin(index, to);
        return true;
    }

    /// <summary>Keys on a Collections item row's wrapper (<paramref name="index"/> of <paramref name="collections"/>): Enter toggles
    /// the item's Show (never the section's); Alt+↑/↓ moves one step, Alt+Shift+↑/↓ to the edge. True = handled.</summary>
    public bool OnItemKey(int index, IReadOnlyList<string> collections, string itemId, bool shown, NavEditKey key, NavEditMods mods)
    {
        if (key == NavEditKey.Enter && mods == NavEditMods.None)
        {
            Sidebar.Dispatch(new SetItemShown(Sidebar.Layout.Peek(), "collections", itemId, !shown));
            return true;
        }
        if (!IsStep(key, mods)) return false;
        int to = SidebarEditRules.MoveTarget(index, collections.Count, key == NavEditKey.Up ? -1 : 1, toEdge: (mods & NavEditMods.Shift) != 0);
        if (to >= 0 && to != index) Sidebar.Dispatch(new MoveItem(Sidebar.Layout.Peek(), "collections", collections[index], to));
        return true;
    }
}
