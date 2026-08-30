namespace Wavee;

/// <summary>
/// Which navbar-customization verbs a sidebar row's context menu offers — the queue-row extras, for the left pane.
/// Engine-free so <c>Wavee.Tests</c> drives the real rule.
///
/// <para>Drag is one of several ways to reorder, never the only one (P6). Explicit Move up / Move down stay available
/// when the in-place reorder band is disarmed (an expanded folder in Pinned, a single remaining item). Remove is the
/// authored-list verb (a StaticLinks / CustomGroup / Shortcuts item the user placed); a Pinned row's remove is Unpin,
/// which already lives in the pin-state slot of the entity menu and is therefore not duplicated here.</para>
/// </summary>
readonly record struct SidebarNavLayout(bool MoveUp, bool MoveDown, bool Remove)
{
    public bool IsEmpty => !MoveUp && !MoveDown && !Remove;

    /// <summary>
    /// <paramref name="orderIndex"/> is this row's slot in the list that actually moves (a reorder band, the pin store
    /// when that band is disarmed, or -1 when the row has no order of its own — a projected library leaf).
    /// <paramref name="removable"/> is true only for a hand-placed item the document will actually drop.
    /// </summary>
    public static SidebarNavLayout Decide(int orderIndex, int orderCount, bool removable)
    {
        bool ordered = orderIndex >= 0 && orderCount > 1;
        return new(
            MoveUp: ordered && orderIndex > 0,
            MoveDown: ordered && orderIndex < orderCount - 1,
            Remove: removable);
    }
}
