// ── Shell/HistoryVerbs.cs — the history page's destructive verbs ───────────────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X24). The page used to call `History.Store.Clear()` / `Remove(...)`
// itself from its click handlers. The page asks (and, for Clear all, confirms first); the store writes happen here.
// History has no undo: a row delete does not ask, Clear all does.

namespace Wavee;

/// <summary>The navigation log's verbs.</summary>
public static class HistoryVerbs
{
    /// <summary>Delete the whole log (and its file). Destructive and unrecoverable — the caller has asked first.</summary>
    public static void ClearAll() => Shell.History.Store.Clear();

    /// <summary>Delete one entry.</summary>
    public static void Remove(Shell.HistoryEntry entry) => Shell.History.Store.Remove(entry);
}
