// ── Home/Board/HomeUndo.cs: the Home undo ring (design 1.2.3, spec 8.4.5) ──────────────────────────────────────────
//
// 50 deep, like SidebarUndoRing. An entry is two document references. Grouping: a push with the same tag as the last
// one within CoalesceMs keeps the older Before (keyboard steps of one item, tag "step:<id>"); a batch (an open-tile
// visit, a drag-in) is pushed once by the store. Any push clears redo.

namespace Wavee;

public sealed record HomeUndoEntry(HomeDoc Before, HomeDoc After, string LabelKey, string At);   // At: a group id, or "page"

/// <summary>What the chrome shows: whether undo/redo can run, the label of the step undo would take back, where that
/// change was (a group id, or "page"), and a version that moves on every change of the ring.</summary>
public readonly record struct HomeUndoState(bool CanUndo, bool CanRedo, string LabelKey, string At, int Version)
{
    public static readonly HomeUndoState None = new(false, false, "", "page", 0);
}

public sealed class HomeUndoRing
{
    public const int Capacity = 50;
    public const int CoalesceMs = 2000;

    readonly HomeUndoEntry?[] _undo = new HomeUndoEntry?[Capacity];
    int _undoStart, _undoCount;
    readonly List<HomeUndoEntry> _redo = new();
    string? _lastTag;
    long _lastMs;

    public bool CanUndo => _undoCount > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undoCount;
    public int RedoCount => _redo.Count;

    /// <summary>The entry undo would take back, or null.</summary>
    public HomeUndoEntry? Top => _undoCount > 0 ? _undo[(_undoStart + _undoCount - 1) % Capacity] : null;

    /// <summary>The entry redo would bring back, or null.</summary>
    public HomeUndoEntry? RedoTop => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>Same tag within CoalesceMs: keep the older Before, take the newer After.</summary>
    public void Push(HomeUndoEntry entry, string? tag, long nowMs)
    {
        _redo.Clear();
        if (tag is not null && _undoCount > 0 && string.Equals(tag, _lastTag, StringComparison.Ordinal) && nowMs - _lastMs < CoalesceMs)
        {
            int top = (_undoStart + _undoCount - 1) % Capacity;
            var older = _undo[top]!;
            _undo[top] = entry with { Before = older.Before };
        }
        else if (_undoCount == Capacity)
        {
            _undo[_undoStart] = entry;                       // the oldest falls off
            _undoStart = (_undoStart + 1) % Capacity;
        }
        else
        {
            _undo[(_undoStart + _undoCount) % Capacity] = entry;
            _undoCount++;
        }
        _lastTag = tag;
        _lastMs = nowMs;
    }

    public bool TryUndo(out HomeUndoEntry entry)
    {
        _lastTag = null;
        if (_undoCount == 0) { entry = null!; return false; }
        int top = (_undoStart + _undoCount - 1) % Capacity;
        entry = _undo[top]!;
        _undo[top] = null;
        _undoCount--;
        _redo.Add(entry);
        return true;
    }

    public bool TryRedo(out HomeUndoEntry entry)
    {
        _lastTag = null;
        if (_redo.Count == 0) { entry = null!; return false; }
        entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        if (_undoCount == Capacity) { _undo[_undoStart] = entry; _undoStart = (_undoStart + 1) % Capacity; }
        else { _undo[(_undoStart + _undoCount) % Capacity] = entry; _undoCount++; }
        return true;
    }

    public void Clear()
    {
        Array.Clear(_undo);
        _undoStart = _undoCount = 0;
        _redo.Clear();
        _lastTag = null;
    }
}
