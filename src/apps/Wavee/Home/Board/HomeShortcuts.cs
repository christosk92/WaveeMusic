// ── Home/Board/HomeShortcuts.cs: the Pinned group as the concept's "Your shortcuts" grid (owner 2026-11-16) ───────────
//
// The Pinned group IS the shortcuts grid: the user's pins first, then Spotify's shortcut fill, and the Recently played widget, while it
// stands in that group, closes the grid as its history tile ("Listening history · N plays this week") and is the grid's other face: the
// group's head switches Shortcuts / Recently played and every shortcut cell cross-fades in place to the played item at its index (the grid
// keeps its size). Moved to another group, the widget is its own Recently played chapter again. Every theme reads these rules; what they
// look like is the theme's. Pure.
//
// ONE GRID, EDIT INSIDE IT (owner 2026-11-16): the cells are drawn as ONE full-width board item, a compact paged grid (at most three rows a page),
// reordered and removed inside it. Every pin is a cell of it, at its one size: `Normalize` moves a pin placed among the widgets into the Pinned
// group (after the user's other pins, in document order) and resets a sized one, on load and after every command.

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

    /// <summary>The board item that stands for the whole grid (its id; its pack key is <c>home:tile:shortcuts</c>).</summary>
    public const string GridId = "shortcuts";

    /// <summary>A cell of the grid: a shortcut cell or the history tile.</summary>
    public static bool IsGridCell(BoardEntry e) => IsShortcutCell(e) || IsHistoryCell(e);

    /// <summary>The grid's cells in the board's order (the history tile where the document puts it: last, by default).</summary>
    public static List<BoardEntry> Cells(BoardModel m)
    {
        var e = m.Entries;
        var cells = new List<BoardEntry>();
        for (int i = 0; i < e.Length; i++) if (IsGridCell(e[i])) cells.Add(e[i]);
        return cells;
    }

    /// <summary>The grid's one board item, standing where its first cell stands: a full row of the Pinned group.</summary>
    public static BoardEntry GridEntry(BoardEntry first)
        => new(GridId, BoardEntryKind.Item, HomeDocs.Pinned, null, null, HomeSize.Full, SectionTemplate.None, first.Title, false, false, false, false, false, -1);

    /// <summary>The grid's item (not a document item: it is the cells' frame).</summary>
    public static bool IsGrid(BoardEntry e) => e.Id == GridId && e.Kind == BoardEntryKind.Item && e.Raw is null;

    /// <summary>Every pin in the Pinned group at its one size: a pin placed in another group moves in after the user's pins (before the fill and the
    /// history tile), in document order; a sized pin is reset. The same document (reference) when nothing moves.</summary>
    public static HomeDoc Normalize(HomeDoc doc)
    {
        int pg = doc.GroupIndex(HomeDocs.Pinned);
        if (pg < 0) return doc;
        bool changed = false;
        var moved = new List<HomeItem>();
        var groups = doc.Groups.ToBuilder();
        for (int g = 0; g < groups.Count; g++)
        {
            if (g == pg) continue;
            var items = groups[g].Items;
            bool any = false;
            foreach (var it in items) if (it.Source is PinSource) { any = true; break; }
            if (!any) continue;
            var kept = System.Collections.Immutable.ImmutableArray.CreateBuilder<HomeItem>(items.Length);
            foreach (var it in items)
            {
                if (it.Source is PinSource) moved.Add(it with { Size = HomeSize.Auto });
                else kept.Add(it);
            }
            groups[g] = groups[g] with { Items = kept.ToImmutable() };
            changed = true;
        }
        var pinned = groups[pg].Items;
        var list = new List<HomeItem>(pinned.Length + moved.Count);
        int at = -1;
        for (int i = 0; i < pinned.Length; i++)
        {
            var it = pinned[i];
            if (at < 0 && it.Source is FillSource or WidgetSource { Widget: "recents" }) at = list.Count;
            if (it.Source is PinSource && it.Size != HomeSize.Auto) { it = it with { Size = HomeSize.Auto }; changed = true; }
            list.Add(it);
        }
        if (!changed) return doc;
        list.InsertRange(at < 0 ? list.Count : at, moved);
        groups[pg] = groups[pg] with { Items = [.. list] };
        return doc with { Groups = groups.ToImmutable() };
    }

    /// <summary>Whether the board's Pinned group carries the history tile, so its head offers the Shortcuts / Recently played switch.</summary>
    public static bool HasSwitch(BoardModel m)
    {
        var e = m.Entries;
        for (int i = 0; i < e.Length; i++) if (IsHistoryCell(e[i])) return true;
        return false;
    }
}
