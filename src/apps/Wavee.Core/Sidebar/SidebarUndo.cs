namespace Wavee.Core.Sidebar;

// The customizer's 50-step undo/redo. PRE-IMAGE SNAPSHOTS, not inverse commands: SidebarCustomLayout and everything
// under it are immutable records, so an edit rebuilds only the spine (O(depth)) and structurally shares the rest —
// keeping 50 whole documents costs 50 spines, not 50 copies. That is why ApplyTemplate and ResetLayout are ordinary
// single-step undoable commands with no special machinery, and why the confirmation dialogs can honestly say
// "You can undo this."
//
// Pure and engine-free on purpose: SidebarPreferences owns the instance and pairs every accepted command with a Push,
// but this type has no idea a signal, a file or a UI thread exists.

/// <summary>One undo step: the document as it was BEFORE the command, plus the command's label key.</summary>
public readonly record struct SidebarUndoEntry(SidebarCustomLayout Before, string LabelLocKey);

public sealed class SidebarUndo
{
    /// <summary>Undo depth. The oldest entry is evicted silently once full (§C3.1).</summary>
    public const int Capacity = 50;

    readonly SidebarUndoEntry[] _undo = new SidebarUndoEntry[Capacity];
    int _undoHead;                                     // next write slot
    int _undoCount;

    readonly SidebarUndoEntry[] _redo = new SidebarUndoEntry[Capacity];
    int _redoHead;
    int _redoCount;

    public int UndoDepth => _undoCount;
    public int RedoDepth => _redoCount;
    public bool CanUndo => _undoCount > 0;
    public bool CanRedo => _redoCount > 0;

    /// <summary>The label of the step Undo would take ("Add section"), for the tooltip and the a11y announcement.</summary>
    public string? UndoLabelLocKey => _undoCount > 0 ? Peek(_undo, _undoHead).LabelLocKey : null;
    public string? RedoLabelLocKey => _redoCount > 0 ? Peek(_redo, _redoHead).LabelLocKey : null;

    /// <summary>Records an ACCEPTED command's pre-image and clears redo. A rejected command (Changed == false) must
    /// never reach here — that is the whole point of SidebarCommandResult.Changed.</summary>
    public void Push(SidebarCustomLayout before, string? labelLocKey)
    {
        ArgumentNullException.ThrowIfNull(before);
        PushInto(_undo, ref _undoHead, ref _undoCount, new SidebarUndoEntry(before, labelLocKey ?? string.Empty));
        _redoHead = 0;
        _redoCount = 0;
    }

    public void Push(SidebarCustomLayout before, SidebarCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Push(before, command.LabelLocKey);
    }

    /// <summary>Steps back: <paramref name="restored"/> is the pre-image, and <paramref name="current"/> becomes the
    /// redo step. False (and no state change) when there is nothing to undo — the 51st undo is a clean no-op.</summary>
    public bool TryUndo(SidebarCustomLayout current, out SidebarCustomLayout restored, out string labelLocKey)
        => Step(current, _undo, ref _undoHead, ref _undoCount, _redo, ref _redoHead, ref _redoCount,
            out restored, out labelLocKey);

    public bool TryRedo(SidebarCustomLayout current, out SidebarCustomLayout restored, out string labelLocKey)
        => Step(current, _redo, ref _redoHead, ref _redoCount, _undo, ref _undoHead, ref _undoCount,
            out restored, out labelLocKey);

    /// <summary>Drops both stacks — used when the document is replaced from outside the command stream (a reload after
    /// a corrupt file, an account switch), where a pre-image from the old document would be nonsense.</summary>
    public void Clear()
    {
        Array.Clear(_undo);
        Array.Clear(_redo);
        _undoHead = _undoCount = _redoHead = _redoCount = 0;
    }

    static bool Step(SidebarCustomLayout current,
        SidebarUndoEntry[] from, ref int fromHead, ref int fromCount,
        SidebarUndoEntry[] to, ref int toHead, ref int toCount,
        out SidebarCustomLayout restored, out string labelLocKey)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (fromCount == 0)
        {
            restored = current;
            labelLocKey = string.Empty;
            return false;
        }

        fromHead = (fromHead - 1 + Capacity) % Capacity;
        var entry = from[fromHead];
        from[fromHead] = default;
        fromCount--;

        PushInto(to, ref toHead, ref toCount, new SidebarUndoEntry(current, entry.LabelLocKey));
        restored = entry.Before;
        labelLocKey = entry.LabelLocKey;
        return true;
    }

    static void PushInto(SidebarUndoEntry[] ring, ref int head, ref int count, SidebarUndoEntry entry)
    {
        ring[head] = entry;
        head = (head + 1) % Capacity;
        if (count < Capacity) count++;   // full ⇒ the oldest entry is overwritten, silently
    }

    static SidebarUndoEntry Peek(SidebarUndoEntry[] ring, int head) => ring[(head - 1 + Capacity) % Capacity];
}
