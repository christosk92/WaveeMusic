// Engine-free by construction (System + FluentGpu.Controls.Route + WorkspaceTabsPersistence, no Element/Component/
// Signal) so TabWorkspaceTests can source-include it into Wavee.Tests, exactly like WorkspaceTabsPersistence.cs beside it.
using System;
using System.Collections.Generic;
using FluentGpu.Controls;

namespace Wavee;

/// <summary>One open browser-style tab: a stable identity, the route it currently shows, and whether it survives
/// the session (pinned tabs are the only cross-session subset — <see cref="WorkspaceTabsPersistence"/>).</summary>
internal sealed record WorkspaceTab(int Id, Route Route, bool Pinned);

/// <summary>What the shell must do with its route after a workspace op. <see cref="Restore"/> is a tab SWITCH: show
/// the tab's page as it was — no history push, no origin write. <see cref="Push"/> is a real navigation (a new tab
/// opening on a fresh route). <see cref="None"/> leaves the route alone.</summary>
internal enum TabNavIntent { None, Restore, Push }

internal readonly record struct TabNavResult(TabNavIntent Intent, Route? Route, int ActiveIndex);

/// <summary>The shell's tab list + which tab is active, as a pure model. <see cref="ActiveId"/> is the ONE source of
/// truth for "which tab is showing": the strip's own <c>SelectedIndex</c> cell is a projection the shell re-asserts
/// after every op, so the engine control pre-writing it before it raises <c>OnSelectionChanged</c> (its documented
/// controlled-prop idiom) can never make an activation look like a no-op. That pre-write is exactly what made the
/// old shell early-return on "selection unchanged" and leave the content on the previous tab's page.
/// <para>Ids are minted once and never reused, so drag/reorder/pin moves keep every tab's identity (the KeepAlive
/// slot key and the strip's item key are both built from it).</para></summary>
internal sealed class TabWorkspace
{
    const string HomeRoute = "home";

    readonly List<WorkspaceTab> _tabs = new();
    int _nextId = 1;

    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;
    public int Count => _tabs.Count;
    public int ActiveId { get; private set; }
    public int ActiveIndex => IndexOf(ActiveId);
    public WorkspaceTab Active => _tabs[ActiveIndex];

    /// <summary>The pinned tab that was selected most recently — the one a cold start reopens on. Survives switching
    /// to an unpinned tab (that is the whole point: the session-only tab is gone next launch, this one is not).</summary>
    public int LastSelectedPinnedId { get; private set; } = -1;

    /// <summary>Bumped whenever the persisted subset changes (pinned membership/order, a pinned tab's route, or
    /// <see cref="LastSelectedPinnedId"/>). The shell compares it against the revision it last saved, so every op
    /// that touches pins persists them and no op that does not pays for a settings write.</summary>
    public int PinnedRevision { get; private set; }

    public TabWorkspace() => SeedHome();

    public int IndexOf(int id)
    {
        for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Id == id) return i;
        return -1;
    }

    /// <summary>Open a new tab on <paramref name="route"/> and make it active. An unpinned tab appends at the end;
    /// a pinned one joins the end of the pinned block (pins are always a prefix). Always a <see cref="TabNavIntent.Push"/>:
    /// the route is a new place, so it belongs on the back stack.</summary>
    public TabNavResult Open(Route route, bool pinned = false)
    {
        var tab = new WorkspaceTab(_nextId++, route, pinned);
        int at = pinned ? PinnedBoundary() : _tabs.Count;
        _tabs.Insert(at, tab);
        ActiveId = tab.Id;
        if (pinned) { LastSelectedPinnedId = tab.Id; PinnedRevision++; }
        return new(TabNavIntent.Push, route, at);
    }

    /// <summary>Make the tab at <paramref name="index"/> active. The index is whatever the caller has in hand (the
    /// strip's click index, a spring-load's id lookup) — never trusted as "already selected": only
    /// <see cref="ActiveId"/> decides that. Out of range → <see cref="TabNavIntent.None"/>.</summary>
    public TabNavResult Activate(int index)
    {
        if ((uint)index >= (uint)_tabs.Count) return None();
        var tab = _tabs[index];
        if (tab.Id == ActiveId) return None();
        ActiveId = tab.Id;
        if (tab.Pinned && LastSelectedPinnedId != tab.Id) { LastSelectedPinnedId = tab.Id; PinnedRevision++; }
        return new(TabNavIntent.Restore, tab.Route, index);
    }

    public TabNavResult ActivateById(int id) => Activate(IndexOf(id));

    /// <summary>Re-select a tab WITHOUT a route intent — the cold-start session restore, which already knows the
    /// route it is about to put on the tab. Does not touch <see cref="LastSelectedPinnedId"/>: restore must not rewrite
    /// pins. False when the id is gone (a session-only tab from last launch), leaving the active tab as it is.</summary>
    public bool TrySelect(int id)
    {
        if (IndexOf(id) < 0) return false;
        ActiveId = id;
        return true;
    }

    /// <summary>Close the tab at <paramref name="index"/>. The last tab is never closed. Closing the ACTIVE tab
    /// selects its right neighbour (else the left one) and restores that tab's route; closing a BACKGROUND tab
    /// changes nothing the user is looking at, so it is <see cref="TabNavIntent.None"/>.</summary>
    public TabNavResult Close(int index)
    {
        if (_tabs.Count <= 1 || (uint)index >= (uint)_tabs.Count) return None();
        var closing = _tabs[index];
        bool wasActive = closing.Id == ActiveId;
        _tabs.RemoveAt(index);
        if (closing.Pinned) PinnedRevision++;
        if (!wasActive)
        {
            ReconcileLastPinned();
            return None();
        }
        int next = Math.Min(index, _tabs.Count - 1);
        ActiveId = _tabs[next].Id;
        ReconcileLastPinned();
        return new(TabNavIntent.Restore, _tabs[next].Route, next);
    }

    /// <summary>Close every tab <paramref name="remove"/> selects (the "close others / to the right / all unpinned"
    /// menu). An emptied workspace reseeds a Home tab. The active tab, when it survives, stays where it is
    /// (<see cref="TabNavIntent.None"/>); when it went, the tab now at its old index (clamped) takes over.</summary>
    public TabNavResult CloseWhere(Func<WorkspaceTab, bool> remove)
    {
        int oldIndex = ActiveIndex;
        bool activeGone = false;
        for (int i = _tabs.Count - 1; i >= 0; i--)
        {
            var tab = _tabs[i];
            if (!remove(tab)) continue;
            _tabs.RemoveAt(i);
            if (tab.Pinned) PinnedRevision++;
            if (tab.Id == ActiveId) activeGone = true;
        }
        if (_tabs.Count == 0)
        {
            SeedHome();
            ReconcileLastPinned();
            return new(TabNavIntent.Restore, _tabs[0].Route, 0);
        }
        if (!activeGone)
        {
            ReconcileLastPinned();
            return None();
        }
        int next = Math.Clamp(oldIndex, 0, _tabs.Count - 1);
        ActiveId = _tabs[next].Id;
        ReconcileLastPinned();
        return new(TabNavIntent.Restore, _tabs[next].Route, next);
    }

    /// <summary>Pin/unpin a tab: it moves to the pinned boundary (pins are always the leading block) and keeps its
    /// identity, so the active tab stays active even when its index changes. Never a route intent.</summary>
    public TabNavResult SetPinned(int id, bool pinned)
    {
        int index = IndexOf(id);
        if (index < 0 || _tabs[index].Pinned == pinned) return None();
        var tab = _tabs[index] with { Pinned = pinned };
        _tabs.RemoveAt(index);
        _tabs.Insert(PinnedBoundary(), tab);
        if (pinned && id == ActiveId) LastSelectedPinnedId = id;
        else if (!pinned && LastSelectedPinnedId == id) LastSelectedPinnedId = FirstPinnedId();
        PinnedRevision++;
        return None();
    }

    /// <summary>Every navigation lands on the ACTIVE tab (browser semantics: the tab follows the page). Returns
    /// whether that tab is pinned, i.e. whether the persisted subset just changed.</summary>
    public bool SetActiveRoute(Route route)
    {
        int index = ActiveIndex;
        var tab = _tabs[index];
        if (tab.Route != route) _tabs[index] = tab with { Route = route };
        if (tab.Pinned) PinnedRevision++;
        return tab.Pinned;
    }

    /// <summary>Cold start: replace the workspace with the persisted pins (routes run through
    /// <paramref name="normalize"/>, so a legacy route in the settings file never reaches the router) and select the
    /// one that was last selected. No pins → a single Home tab. Returns the active tab's route.</summary>
    public Route RestorePinned(WorkspaceTabsSnapshot snapshot, Func<string, string?, Route> normalize)
    {
        _tabs.Clear();
        for (int i = 0; i < snapshot.Tabs.Length; i++)
        {
            var saved = snapshot.Tabs[i];
            _tabs.Add(new WorkspaceTab(_nextId++, normalize(saved.Route, saved.Arg), Pinned: true));
        }
        if (_tabs.Count == 0)
        {
            SeedHome();
            LastSelectedPinnedId = -1;
            return _tabs[0].Route;
        }
        int selected = Math.Clamp(snapshot.LastSelected, 0, _tabs.Count - 1);
        ActiveId = _tabs[selected].Id;
        LastSelectedPinnedId = ActiveId;
        return _tabs[selected].Route;
    }

    /// <summary>The persisted subset: pinned tabs in order + which of them is <see cref="LastSelectedPinnedId"/>
    /// (first pin when none is). Round-trips through <see cref="WorkspaceTabsPersistence"/>.</summary>
    public WorkspaceTabsSnapshot PinnedSnapshot()
    {
        var pins = new List<PersistedWorkspaceTab>();
        int selected = -1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            if (!tab.Pinned) continue;
            if (tab.Id == LastSelectedPinnedId) selected = pins.Count;
            pins.Add(new PersistedWorkspaceTab(tab.Route.Name, tab.Route.Arg));
        }
        if (pins.Count > 0 && selected < 0) selected = 0;
        return new(pins.ToArray(), selected);
    }

    TabNavResult None() => new(TabNavIntent.None, null, ActiveIndex);

    void SeedHome()
    {
        var home = new WorkspaceTab(_nextId++, new Route(HomeRoute), Pinned: false);
        _tabs.Add(home);
        ActiveId = home.Id;
    }

    int PinnedBoundary()
    {
        int boundary = 0;
        while (boundary < _tabs.Count && _tabs[boundary].Pinned) boundary++;
        return boundary;
    }

    int FirstPinnedId()
    {
        for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Pinned) return _tabs[i].Id;
        return -1;
    }

    /// <summary>After a removal or a selection: the active tab, when pinned, is the last-selected pin; otherwise the
    /// remembered one must still exist, else the first pin (or none) stands in.</summary>
    void ReconcileLastPinned()
    {
        int before = LastSelectedPinnedId;
        var active = Active;
        if (active.Pinned) LastSelectedPinnedId = active.Id;
        else if (IndexOf(LastSelectedPinnedId) < 0) LastSelectedPinnedId = FirstPinnedId();
        if (LastSelectedPinnedId != before) PinnedRevision++;
    }
}
