using System;
using System.Collections.Generic;

namespace Wavee;

// THE PLAYLIST TREE'S MULTI-SELECTION — pure, engine-free, keyed by ROW ID.
//
// WHY NOT `SelectionModel`. The engine's `FluentGpu.Controls.SelectionModel` already implements exactly the WinUI
// selector trio (SingleSelector / MultipleSelector / ExtendedSelector, cited line-for-line in that file), and this
// class deliberately does NOT wrap it: `SelectionModel` addresses items by INDEX, and the sidebar's tree re-flows
// under the user constantly — a folder collapses, a projection lands, a search filters, a customizer edit re-plans —
// so an index selected one frame names a different playlist the next. A rootlist selection has to survive that, and
// the only thing that does is the row's own id. What IS ported, rule for rule, is `SelectionModel.OnInteractedAction`'s
// EXTENDED arm (ExtendedSelector.cpp:18-53): Shift replaces the selection with the anchor range, Ctrl toggles, and a
// plain interaction clears-and-selects only when the item was not already selected. The anchor moves on every single-
// item operation, exactly as `Select`/`Deselect` move `SelectionModel.AnchorIndex`.
//
// THE VISIBLE ORDER IS AN ARGUMENT, never state. Ranges and payload order are questions about the rows the user can
// actually see right now, and this object must not hold a snapshot of a list the pane republishes several times a
// second. The caller passes the current order (the pane's `TreeVisibleOrder`, rebuilt with every plan) into the two
// operations that need it; everything else is a set membership test.
//
// ENGINE-FREE (System only) so `Wavee.Tests` source-includes it and drives the semantics directly
// (`SidebarTreeSelectionTests`) rather than through a mounted pane. Every mutator returns "did this CHANGE anything",
// which is what lets the pane bump the row epochs of exactly the rows that flipped instead of re-skinning the window.
sealed class SidebarTreeSelection
{
    readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    readonly List<string> _scratch = new();
    string? _anchor;
    bool _checkMode;

    /// <summary>How many rows are selected.</summary>
    public int Count => _ids.Count;

    /// <summary>The selected ids as a SET — the shape <c>RootlistSelection.Normalize</c> takes. Live, not a copy:
    /// every caller reads it inside the gesture that asked for it.</summary>
    public IReadOnlySet<string> Ids => _ids;

    /// <summary>The Shift-range anchor (WinUI <c>SelectionModel.AnchorIndex</c>, by key). Null = none.</summary>
    public string? Anchor => _anchor;

    /// <summary>Is the user in explicit CHECK MODE (entered from the row menu's "Select")? Distinct from
    /// <see cref="CheckLaneVisible"/>: check mode survives the selection emptying, which is what lets a user turn the
    /// lane on and then pick their first row.</summary>
    public bool CheckMode => _checkMode;

    /// <summary>Is the checkbox lane on screen? Explicit check mode, OR two or more rows selected — the track-list
    /// rule: one selected row is still "the row I clicked", two is a set and needs a visible handle.</summary>
    public bool CheckLaneVisible => _checkMode || _ids.Count >= 2;

    public bool Contains(string? id) => id is { Length: > 0 } && _ids.Contains(id);

    /// <summary>WinUI's EXTENDED interaction, by key (<c>ExtendedSelector.cpp:18-53</c>). Shift replaces the selection
    /// with the range from the anchor; Ctrl toggles; a plain interaction clears and selects, and is a NO-OP on a row
    /// that is already selected (which is what makes "click one of my five selected rows and drag" possible at all).
    /// <para>A Shift with no resolvable anchor degrades to selecting just this row rather than doing nothing — the
    /// anchor is lost exactly when the previously anchored row left the tree, and refusing there would read as a dead
    /// modifier.</para></summary>
    public bool Interact(string id, bool ctrl, bool shift, IReadOnlyList<string>? visibleOrder)
    {
        if (id is not { Length: > 0 }) return false;
        if (shift) return SelectRangeTo(id, visibleOrder);
        if (ctrl) return Toggle(id);
        if (_ids.Count == 1 && _ids.Contains(id)) { _anchor = id; return false; }
        return Replace(id);
    }

    /// <summary>Ctrl-click: add or remove this one row. The anchor follows it either way, as <c>SelectionModel.Select</c>
    /// and <c>Deselect</c> both move <c>AnchorIndex</c>.</summary>
    public bool Toggle(string id)
    {
        if (id is not { Length: > 0 }) return false;
        _anchor = id;
        return _ids.Contains(id) ? _ids.Remove(id) : _ids.Add(id);
    }

    /// <summary>Shift-click: REPLACE the selection with the inclusive range between the anchor and <paramref name="id"/>
    /// over the currently visible tree order. The anchor does not move (that is what makes a second Shift-click
    /// re-range from the same origin instead of walking).</summary>
    public bool SelectRangeTo(string id, IReadOnlyList<string>? visibleOrder)
    {
        if (id is not { Length: > 0 }) return false;
        int to = IndexOf(visibleOrder, id);
        int from = _anchor is { Length: > 0 } a ? IndexOf(visibleOrder, a) : -1;
        if (to < 0 || from < 0) return Replace(id);          // the anchor left the tree — select just this row

        if (from > to) (from, to) = (to, from);
        _scratch.Clear();
        for (int i = from; i <= to; i++) _scratch.Add(visibleOrder![i]);
        bool changed = _scratch.Count != _ids.Count;
        if (!changed)
            for (int i = 0; i < _scratch.Count && !changed; i++) changed = !_ids.Contains(_scratch[i]);
        if (!changed) return false;
        _ids.Clear();
        for (int i = 0; i < _scratch.Count; i++) _ids.Add(_scratch[i]);
        return true;   // _anchor deliberately unchanged
    }

    /// <summary>Empty the selection AND leave check mode — the Escape gesture, and the plain-click reset.</summary>
    public bool Clear()
    {
        bool changed = _ids.Count > 0 || _checkMode || _anchor is not null;
        _ids.Clear();
        _anchor = null;
        _checkMode = false;
        return changed;
    }

    /// <summary>Enter or leave explicit check mode. Leaving does NOT clear the selection: the lane can also be showing
    /// because two rows are selected, and the caller decides whether "done" means "deselect".</summary>
    public bool SetCheckMode(bool on)
    {
        if (_checkMode == on) return false;
        _checkMode = on;
        return true;
    }

    /// <summary>Drop every id the visible tree no longer holds — a folder collapsed, a search filtered, a projection
    /// landed. Run it with every plan: a selection naming rows nobody can see would drag items the user cannot point
    /// at. The anchor is pruned with them.</summary>
    public bool Prune(IReadOnlyList<string>? visibleOrder)
    {
        if (_ids.Count == 0 && _anchor is null) return false;
        bool changed = false;
        if (visibleOrder is null || visibleOrder.Count == 0)
        {
            // No tree at all (a pending projection, a section that planned nothing): keep the selection rather than
            // silently emptying it on a transient frame — Prune answers "is this row gone", not "is the plan warm".
            return false;
        }
        _scratch.Clear();
        foreach (string id in _ids)
            if (IndexOf(visibleOrder, id) < 0) _scratch.Add(id);
        for (int i = 0; i < _scratch.Count; i++) changed |= _ids.Remove(_scratch[i]);
        if (_anchor is { Length: > 0 } a && IndexOf(visibleOrder, a) < 0) { _anchor = null; changed = true; }
        return changed;
    }

    /// <summary>The selection in TREE ORDER — the shape a payload, a batch move and a picker all want. Ids the visible
    /// order does not hold are dropped, for the same reason <see cref="Prune"/> drops them.</summary>
    public IReadOnlyList<string> Ordered(IReadOnlyList<string>? visibleOrder)
    {
        if (_ids.Count == 0 || visibleOrder is null || visibleOrder.Count == 0) return Array.Empty<string>();
        var ordered = new List<string>(_ids.Count);
        for (int i = 0; i < visibleOrder.Count; i++)
            if (_ids.Contains(visibleOrder[i])) ordered.Add(visibleOrder[i]);
        return ordered;
    }

    static int IndexOf(IReadOnlyList<string>? order, string id)
    {
        if (order is null) return -1;
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    bool Replace(string id)
    {
        bool changed = _ids.Count != 1 || !_ids.Contains(id);
        _ids.Clear();
        _ids.Add(id);
        _anchor = id;
        return changed;
    }
}
