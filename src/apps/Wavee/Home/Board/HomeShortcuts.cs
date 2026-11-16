// ── Home/Board/HomeShortcuts.cs: the Pinned group as the concept's "Your shortcuts" grid (owner 2026-11-16) ───────────
//
// The Pinned group IS the shortcuts grid: the user's pins first, then Spotify's shortcut fill, and the Recently played widget, while it
// stands in that group, closes the grid as its history tile ("Listening history · N plays this week") and is the grid's other face: the
// group's head switches Shortcuts / Recently played and every shortcut cell cross-fades in place to the played item at its index (the grid
// keeps its size). Moved to another group, the widget is its own Recently played chapter again. Every theme reads these rules; what they
// look like is the theme's. Pure.

namespace Wavee;

public static class HomeShortcuts
{
    /// <summary>The face index of the shortcuts (the user's pins and the fill) and of Recently played (<see cref="HomeBoardView.ShortcutsFace"/>).</summary>
    public const int ShortcutsFace = 0, RecentsFace = 1;

    /// <summary>The Recently played widget standing in the Pinned group: the grid's history tile (a row tile, whatever size the document says).</summary>
    public static bool IsHistoryCell(BoardEntry e)
        => e.Kind == BoardEntryKind.Item && e.GroupId == HomeDocs.Pinned && e.Raw is WidgetSource { Widget: "recents" };

    /// <summary>A cell of the shortcuts grid: a pin (the user's) or a fill cell (Spotify's) in the Pinned group.</summary>
    public static bool IsShortcutCell(BoardEntry e)
        => e.GroupId == HomeDocs.Pinned && e.Kind is BoardEntryKind.Item or BoardEntryKind.FillCell && e.Raw is PinSource && !e.Hidden;

    /// <summary>The document's Pinned group holds the Recently played widget, so the history tile closes the grid in the fill's last place: the fill
    /// gives up one cell (the concept's seven shortcuts and the history tile, two full rows of four).</summary>
    public static bool ClosedByHistory(HomeGroup g)
    {
        if (g.Id != HomeDocs.Pinned) return false;
        foreach (var it in g.Items) if (it.Source is WidgetSource { Widget: "recents" }) return true;
        return false;
    }

    /// <summary>Whether the board's Pinned group carries the history tile, so its head offers the Shortcuts / Recently played switch.</summary>
    public static bool HasSwitch(BoardModel m)
    {
        var e = m.Entries;
        for (int i = 0; i < e.Length; i++) if (IsHistoryCell(e[i])) return true;
        return false;
    }
}
