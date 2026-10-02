// ── Entities/Episode.Selection.cs ──────────────────────────────────────────────────────────────────────────────────
// bulk selection over the show reader's rows (owner report 5): the model and what a row binds
//
// Role: UI (with pure statics)
// Owner: A1 (S-reader)
// Spec: report 5 — "no selection at all, and when there was one it said 0 selected"
//
// ── ONE SELECTION MODEL, ONE INDEX SPACE ─────────────────────────────────────────────────────────────────────────────
//
// The reader is a bound `ItemsView` whose item space is the SNAPSHOT's: index 0 is the sticky rail, 1 the visit head,
// 2 the "episodes N" header, then month Groups interleaved with Rows, then the Foot. The engine's `SelectionModel`
// indexes that same flat space (the persistent prefix included — `Track.Table` does the same through its `TrackStart`),
// so a row's own index IS its selection index and no mapping table exists to drift.
//
// A GROUP HEADER IS NOT SELECTABLE and is never counted: only `ItemKind.Row` carries the check lane / the toggle
// handlers (`Episode.ReaderRowContent`'s selection arm), `SelectAll` walks the rows alone, and `SelectedCount` counts
// the rows among the model's selected indices rather than asking the model how many indices it holds. That is why
// "Select all" on a show with twelve months does not report twelve phantom selections.
//
// THE BAR APPEARS ONLY ABOVE ZERO. `Controls.SelectionBar(count, …, minCount: 1)` renders nothing at 0 — the reader
// passes the LIVE row count, never 0-with-minCount-0 (the table's arm, which keeps its own bar mounted), so the "0
// selected" bar the owner saw cannot come back.
//
// ── THE COMMAND LANE IS THE SHARED ONE ──────────────────────────────────────────────────────────────────────────────
//
// The bar's commands are `Track.SelectionLane` (Track.Selection.cs) over `SelectionLaneArgs.ForEpisodes(SelectedCount,
// Selected, Exit, SelectAll)` — the same lane the detail table mounts; this class owns only the selection STATE.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Bulk selection over a reader whose items interleave rows with headers. Built ONCE per reader host; the
/// toolbar's select toggle arms it, Escape and the bar's ✕ clear it.</summary>
public sealed class EpisodeSelection
{
    /// <summary>The engine's model, handed to the list through <c>ListOptions.Selection</c> so the view's own
    /// keyboard/pointer semantics (Ctrl, Shift, the anchor) drive it.</summary>
    public SelectionModel Model { get; } = new() { Mode = ItemsSelectionMode.Extended };

    /// <summary>Is multi-select ARMED — the check lane's presence, and the click-vs-toggle decision a row makes.</summary>
    public Signal<bool> Selecting { get; } = new(false);

    /// <summary>What a row binds (<see cref="Episode.RowContext.Selection"/>).</summary>
    public Episode.RowSelectionContext Rows { get; }

    /// <summary>Clear the selection AND disarm — the bar's ✕ and a row's Escape.</summary>
    public Action Exit { get; }

    /// <summary>Select every REAL row and nothing else (never a month header).</summary>
    public Action SelectAll { get; }

    readonly Func<int, Episode> _episodeAt;
    readonly Func<int> _itemCount;
    readonly Func<int, bool> _isRow;

    /// <param name="episodeAt">the episode at a flat item index; <c>default</c> when that item is not a row.</param>
    /// <param name="itemCount">how many items the reader's list holds right now.</param>
    /// <param name="isRow">is the item at this flat index a selectable row (never a month header).</param>
    public EpisodeSelection(Func<int, Episode> episodeAt, Func<int> itemCount, Func<int, bool> isRow)
    {
        _episodeAt = episodeAt;
        _itemCount = itemCount;
        _isRow = isRow;
        Exit = () => { Model.DeselectAll(); Selecting.Value = false; };
        SelectAll = SelectEveryRow;
        Rows = new Episode.RowSelectionContext { Selecting = Selecting, Clear = Exit };
    }

    /// <summary>Arm or disarm. Disarming always drops the selection with it: a check lane that vanishes while rows stay
    /// selected leaves a bar nobody can see the cause of.</summary>
    public void Arm(bool on)
    {
        if (!on) { Model.DeselectAll(); Selecting.Value = false; return; }
        Selecting.Value = true;
    }

    /// <summary>How many EPISODES are selected — the bar's count. Reads <see cref="SelectionModel.Version"/>, so a
    /// caller's memo re-fires on every real change.</summary>
    public int SelectedCount
    {
        get
        {
            _ = Model.Version.Value;
            int n = 0;
            for (int r = 0; r < Model.RangeCount; r++)
            {
                var (s, e) = Model.GetRange(r);
                for (int i = s; i <= e; i++) if (_isRow(i)) n++;
            }
            return n;
        }
    }

    /// <summary>The selected episodes, in item order.</summary>
    public List<Episode> Selected()
    {
        var list = new List<Episode>();
        for (int r = 0; r < Model.RangeCount; r++)
        {
            var (s, e) = Model.GetRange(r);
            for (int i = s; i <= e; i++)
            {
                var episode = _episodeAt(i);
                if (episode.IsValid) list.Add(episode);
            }
        }
        return list;
    }

    /// <summary>Push the reader's current item count into the model — the model refuses an index past it. The hosting
    /// <c>ItemsView</c> does this on every render; a caller driving the model directly calls it first.</summary>
    public void Sync()
    {
        int n = _itemCount();
        if (Model.ItemCount != n) Model.ItemCount = n;
    }

    void SelectEveryRow()
    {
        Sync();
        int n = _itemCount();
        if (n <= 0) return;
        Selecting.Value = true;
        Model.DeselectAll();
        // One index at a time, never SelectAll(): a month header sits in the same index space and must stay unselected.
        for (int i = 0; i < n; i++) if (_isRow(i)) Model.Select(i);
    }
}
