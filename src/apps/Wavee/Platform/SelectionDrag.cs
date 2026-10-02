// ── Platform/SelectionDrag.cs ──────────────────────────────────────────────────────────────────────────────────────
// The ONE rule for "which rows does a drag carry": the whole selection when the pressed row is part of it, otherwise
// just that row (Explorer semantics). Pure over the engine's SelectionModel; the callers map indices to their own rows
// (Top tracks → the popular slots, the album drawer → the album's track targets).
//
// Role: CORE (pure)
// Owner: interaction-consistency WP3
// Spec: docs/plans/wavee/interaction-consistency-implementation.md §7 (WP3)

using FluentGpu.Controls;

namespace Wavee;

public static class SelectionDrag
{
    /// <summary>Fill <paramref name="into"/> (cleared first) with the indices a drag started on <paramref name="pressed"/>
    /// carries: every selected index, ascending, when <paramref name="pressed"/> is selected; otherwise only
    /// <paramref name="pressed"/>. Runs once, at drag promotion.</summary>
    public static void Indices(SelectionModel sel, int pressed, List<int> into)
    {
        into.Clear();
        if (sel.IsSelected(pressed)) sel.GetSelectedIndices(into);
        else into.Add(pressed);
    }
}
