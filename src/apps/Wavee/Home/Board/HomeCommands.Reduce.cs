// ── Home/Board/HomeCommands.Reduce.cs: the reducer's body ───────────────────────────────────────────────────────────
//
// Rules (design 1.2.3; named so nobody "fixes" them back):
// - A pin lands after the user's pins and before the Fill item of its group. Pin of something already pinned is a no-op
//   that returns its item id.
// - Hide only adds to Hidden. Every other command on a feed section first TAKES CONTROL: every section of the current
//   answer becomes an explicit SpotifySection item in the order shown, the SpotifyFeed item stays last as the catch-all.
//   One command, one undo entry. Resize takes control too: placed alone, a section would leave its position.
// - A placed item keeps the entry's id ("feed:<key>", "fill:<uri>"), so taking control or keeping a shortcut never
//   remounts the tile.
// - Move, resize, options or Keep on a fill cell pins it (the concept's own()); Remove on one hides that shortcut.
// - SetSize refuses a size the source does not declare; CycleSize walks the declared list.
// - Reset(Page) restores groups, sizes and hidden sections and keeps the user's pins in Pinned; Reset(Everything) also
//   clears options and Seen.
// - Unknown sources are moved and removed like anything else and never rewritten.

using System.Collections.Immutable;

namespace Wavee;

public static partial class HomeCommands
{
    public const string FeedPrefix = "feed:", FillPrefix = "fill:", FoldPrefix = "fold:";

    /// <summary>The family-wide hide marker: a Hidden ref with an empty key hides every section of its family.</summary>
    public static SectionRef FamilyRef(string family) => new("", 0, family);

    /// <summary>A shortcut the user removed from the fill: a Hidden ref whose family is this marker and whose key is the
    /// shortcut's pin id.</summary>
    public const string FillFamily = "\u0001fill";

    static readonly ImmutableSortedDictionary<string, string> s_noOptions = ImmutableSortedDictionary<string, string>.Empty;

    static HomeDoc ApplyCore(HomeDoc doc, HomeCommand command, BoardModel shown, HomeSourceRegistry sources, long nowMs,
        out HomeApplied applied)
    {
        applied = new HomeApplied(false, null, null, LabelOf(command));
        HomeDoc next = command switch
        {
            HomeCommand.Pin p => Pin(doc, p.PinId, p.Name, p.Cover, p.GroupId, -1, HomeSize.Auto, ref applied),
            HomeCommand.Unpin u => Unpin(doc, u.PinId, ref applied),
            HomeCommand.Add a => Add(doc, a, ref applied),
            HomeCommand.Remove r => Remove(doc, shown, r.EntryId, ref applied),
            HomeCommand.Hide h => Hide(doc, h.WholeFamily ? FamilyRef(h.Ref.Family) : h.Ref, ref applied),
            HomeCommand.Show s => Show(doc, s.Ref, ref applied),
            HomeCommand.Keep k => Own(doc, shown, k.EntryId, ref applied, out _),
            HomeCommand.MoveOnto m => MoveOnto(doc, shown, m.EntryId, m.TargetEntryId, ref applied),
            HomeCommand.MoveBy m => MoveBy(doc, shown, m.EntryId, m.Delta, ref applied),
            HomeCommand.MoveToEdge m => MoveToEdge(doc, shown, m.EntryId, m.ToStart, ref applied),
            HomeCommand.MoveToGroup m => MoveToGroup(doc, shown, m.EntryId, m.GroupId, m.AtIndex, ref applied),
            HomeCommand.ReorderShortcut r => ReorderShortcut(doc, shown, r.EntryId, r.ToIndex, ref applied),
            HomeCommand.SetSize s => SetSize(doc, shown, sources, s.EntryId, s.Size, cycle: false, ref applied),
            HomeCommand.CycleSize c => SetSize(doc, shown, sources, c.EntryId, HomeSize.Auto, cycle: true, ref applied),
            HomeCommand.SetOptions o => SetOptions(doc, shown, o.EntryId, o.Patch, ref applied),
            HomeCommand.Reset r => Reset(doc, r.Scope, ref applied),
            _ => doc,
        };
        if (ReferenceEquals(next, doc) || !applied.Changed) { applied = applied with { Changed = false }; return doc; }
        return NormalizeFills(next) with { Rev = doc.Rev + 1, UpdatedAtMs = nowMs };
    }

    static string LabelOf(HomeCommand c) => c switch
    {
        HomeCommand.Pin => "home.board.undo.pin",
        HomeCommand.Unpin => "home.board.undo.unpin",
        HomeCommand.Add => "home.board.undo.add",
        HomeCommand.Remove => "home.board.undo.remove",
        HomeCommand.Hide => "home.board.undo.hide",
        HomeCommand.Show => "home.board.undo.show",
        HomeCommand.Keep => "home.board.undo.keep",
        HomeCommand.SetSize or HomeCommand.CycleSize => "home.board.undo.size",
        HomeCommand.SetOptions => "home.board.undo.options",
        HomeCommand.Reset => "home.board.undo.reset",
        _ => "home.board.undo.move",
    };

    // ══ pins and adds ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The item holding this pin id, or (-1, -1).</summary>
    public static (int Group, int Index) FindPin(HomeDoc doc, string pinId)
    {
        for (int g = 0; g < doc.Groups.Length; g++)
        {
            var items = doc.Groups[g].Items;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Source is PinSource p && string.Equals(p.PinId, pinId, StringComparison.Ordinal)) return (g, i);
        }
        return (-1, -1);
    }

    static HomeDoc Pin(HomeDoc doc, string pinId, string name, string cover, string? groupId, int at, HomeSize size, ref HomeApplied applied)
    {
        var (pg, pi) = FindPin(doc, pinId);
        if (pg >= 0) { applied = applied with { ItemId = doc.Groups[pg].Items[pi].Id, GroupId = doc.Groups[pg].Id }; return doc; }
        if (doc.ItemCount >= HomeDoc.MaxItems) return doc;
        int g = TargetGroup(doc, groupId ?? HomeDocs.Pinned);
        if (g < 0) return doc;
        var items = doc.Groups[g].Items;
        int index = at >= 0 ? Math.Min(at, items.Length) : PinSlot(items);
        var item = new HomeItem(HomeDocs.NewItemId(), new PinSource(pinId, name, cover), size, s_noOptions);
        applied = applied with { Changed = true, ItemId = item.Id, GroupId = doc.Groups[g].Id };
        return WithItems(doc, g, items.Insert(index, item));
    }

    /// <summary>After the user's pins, before the group's Fill item (else after the last pin, else at the end).</summary>
    static int PinSlot(ImmutableArray<HomeItem> items)
    {
        int lastPin = -1;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Source is FillSource) return i;
            if (items[i].Source is PinSource) lastPin = i;
        }
        return lastPin >= 0 ? lastPin + 1 : items.Length;
    }

    static int TargetGroup(HomeDoc doc, string groupId)
    {
        int g = doc.GroupIndex(groupId);
        if (g < 0) g = doc.GroupIndex(HomeDocs.Pinned);
        if (g < 0 && doc.Groups.Length > 0) g = 0;
        return g;
    }

    static HomeDoc Unpin(HomeDoc doc, string pinId, ref HomeApplied applied)
    {
        var (g, i) = FindPin(doc, pinId);
        if (g < 0) return doc;
        applied = applied with { Changed = true, ItemId = doc.Groups[g].Items[i].Id, GroupId = doc.Groups[g].Id };
        return WithItems(doc, g, doc.Groups[g].Items.RemoveAt(i));
    }

    static HomeDoc Add(HomeDoc doc, HomeCommand.Add a, ref HomeApplied applied)
    {
        switch (a.Source)
        {
            case PinSource p:
                return Pin(doc, p.PinId, p.NameHint, p.CoverHint, a.GroupId, a.AtIndex, a.Size, ref applied);
            case SpotifySectionSource s:
            {
                // Adding a section the user hid shows it again, placed where asked.
                var shownAgain = WithoutHidden(doc, s.Ref);
                var (pg, pi) = FindSection(shownAgain, s.Ref.Key);
                if (pg >= 0)
                {
                    applied = applied with { Changed = !ReferenceEquals(shownAgain, doc), ItemId = shownAgain.Groups[pg].Items[pi].Id, GroupId = shownAgain.Groups[pg].Id };
                    return shownAgain;
                }
                return Insert(shownAgain, new HomeItem(FeedPrefix + s.Ref.Key, s, a.Size, s_noOptions), a.GroupId, a.AtIndex, beforeFeed: true, ref applied);
            }
            case WidgetSource w:
            {
                var (wg, wi) = FindWidget(doc, w.Widget);
                if (wg >= 0) { applied = applied with { ItemId = doc.Groups[wg].Items[wi].Id, GroupId = doc.Groups[wg].Id }; return doc; }
                return Insert(doc, new HomeItem(HomeDocs.NewItemId(), w, a.Size, s_noOptions), a.GroupId, a.AtIndex, beforeFeed: true, ref applied);
            }
            case FillSource f:
            {
                for (int g = 0; g < doc.Groups.Length; g++)
                    foreach (var it in doc.Groups[g].Items)
                        if (it.Source is FillSource) { applied = applied with { ItemId = it.Id, GroupId = doc.Groups[g].Id }; return doc; }
                return Insert(doc, new HomeItem(HomeDocs.FillId, f, HomeSize.Auto, s_noOptions), a.GroupId, a.AtIndex, beforeFeed: false, ref applied);
            }
            default:
                return Insert(doc, new HomeItem(HomeDocs.NewItemId(), a.Source, a.Size, s_noOptions), a.GroupId, a.AtIndex, beforeFeed: true, ref applied);
        }
    }

    static HomeDoc Insert(HomeDoc doc, HomeItem item, string groupId, int at, bool beforeFeed, ref HomeApplied applied)
    {
        if (doc.ItemCount >= HomeDoc.MaxItems) return doc;
        int g = TargetGroup(doc, groupId);
        if (g < 0) return doc;
        var items = doc.Groups[g].Items;
        int index = at >= 0 ? Math.Min(at, items.Length) : items.Length;
        if (at < 0 && beforeFeed)
            for (int i = 0; i < items.Length; i++) if (items[i].Source is SpotifyFeedSource) { index = i; break; }
        applied = applied with { Changed = true, ItemId = item.Id, GroupId = doc.Groups[g].Id };
        return WithItems(doc, g, items.Insert(index, item));
    }

    static (int, int) FindWidget(HomeDoc doc, string widget)
    {
        for (int g = 0; g < doc.Groups.Length; g++)
        {
            var items = doc.Groups[g].Items;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Source is WidgetSource w && string.Equals(w.Widget, widget, StringComparison.Ordinal)) return (g, i);
        }
        return (-1, -1);
    }

    static (int, int) FindSection(HomeDoc doc, string key)
    {
        for (int g = 0; g < doc.Groups.Length; g++)
        {
            var items = doc.Groups[g].Items;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Source is SpotifySectionSource s && string.Equals(s.Ref.Key, key, StringComparison.Ordinal)) return (g, i);
        }
        return (-1, -1);
    }

    // ══ remove, hide, show ══════════════════════════════════════════════════════════════════════════════════════════

    static HomeDoc Remove(HomeDoc doc, BoardModel shown, string entryId, ref HomeApplied applied)
    {
        int at = shown.IndexOf(entryId);
        var e = at >= 0 ? shown.Entries[at] : null;
        if (e is not null)
        {
            switch (e.Kind)
            {
                case BoardEntryKind.FeedSection when e.Raw is SpotifySectionSource s:
                    applied = applied with { ItemId = e.Id, GroupId = e.GroupId };
                    return Hide(doc, s.Ref, ref applied);
                case BoardEntryKind.Fold:
                {
                    var d = doc;
                    foreach (var m in e.Members) d = Hide(d, m, ref applied);
                    applied = applied with { ItemId = e.Id, GroupId = e.GroupId };
                    return d;
                }
                case BoardEntryKind.FillCell when e.Raw is PinSource p:
                    applied = applied with { ItemId = e.Id, GroupId = e.GroupId };
                    return Hide(doc, new SectionRef(p.PinId, 0, FillFamily), ref applied);
                case BoardEntryKind.GroupHead or BoardEntryKind.Placeholder:
                    return doc;
            }
        }
        var (g, i) = doc.Find(entryId);
        if (g < 0) return doc;
        var item = doc.Groups[g].Items[i];
        if (item.Source is SpotifyFeedSource) return doc;              // the catch-all never leaves; its sections are hidden one by one
        var next = WithItems(doc, g, doc.Groups[g].Items.RemoveAt(i));
        if (item.Source is SpotifySectionSource sec && !IsHidden(next, sec.Ref)) next = next with { Hidden = next.Hidden.Add(sec.Ref) };
        applied = applied with { Changed = true, ItemId = item.Id, GroupId = doc.Groups[g].Id };
        return next;
    }

    /// <summary>True when the document hides this section by its key or by its family.</summary>
    public static bool IsHidden(HomeDoc doc, SectionRef r)
    {
        foreach (var h in doc.Hidden)
        {
            if (h.Key.Length > 0 && string.Equals(h.Key, r.Key, StringComparison.Ordinal)) return true;
            if (h.Key.Length == 0 && h.Family.Length > 0 && string.Equals(h.Family, r.Family, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static HomeDoc Hide(HomeDoc doc, SectionRef r, ref HomeApplied applied)
    {
        foreach (var h in doc.Hidden)
            if (string.Equals(h.Key, r.Key, StringComparison.Ordinal) && (r.Key.Length > 0 || string.Equals(h.Family, r.Family, StringComparison.Ordinal)))
                return doc;
        applied = applied with { Changed = true, GroupId = applied.GroupId ?? HomeDocs.Spotify };
        return doc with { Hidden = doc.Hidden.Add(r) };
    }

    static HomeDoc WithoutHidden(HomeDoc doc, SectionRef r)
    {
        var b = ImmutableArray.CreateBuilder<SectionRef>(doc.Hidden.Length);
        foreach (var h in doc.Hidden)
        {
            bool match = r.Key.Length > 0
                ? string.Equals(h.Key, r.Key, StringComparison.Ordinal)
                : h.Key.Length == 0 && string.Equals(h.Family, r.Family, StringComparison.Ordinal);
            if (!match) b.Add(h);
        }
        return b.Count == doc.Hidden.Length ? doc : doc with { Hidden = b.ToImmutable() };
    }

    static HomeDoc Show(HomeDoc doc, SectionRef r, ref HomeApplied applied)
    {
        var next = WithoutHidden(doc, r);
        if (!ReferenceEquals(next, doc)) applied = applied with { Changed = true, GroupId = HomeDocs.Spotify };
        return next;
    }

    // ══ taking control and owning ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Make the entry a document item: a feed section or fold takes control of the whole feed, a fill cell becomes
    /// the user's pin where it is shown. Returns the item id (the entry id), or null when the entry cannot be owned.
    /// <para>A fill cell is owned IN PLACE and ALONE: its Fill item splits around it (the cells shown ahead of it stay the fill's, as do
    /// the ones after it), so the order seen is the order saved, no other shortcut becomes the user's, and the shortcuts shown stay as
    /// many (the parts' sizes add up to the one before less the owned cell).</para></summary>
    static HomeDoc Own(HomeDoc doc, BoardModel shown, string entryId, ref HomeApplied applied, out string? itemId)
    {
        itemId = null;
        if (doc.Find(entryId).Group >= 0) { itemId = entryId; return doc; }
        int at = shown.IndexOf(entryId);
        if (at < 0) return doc;
        var e = shown.Entries[at];
        switch (e.Kind)
        {
            case BoardEntryKind.FeedSection:
            case BoardEntryKind.Fold:
            {
                var next = TakeControl(doc, shown);
                if (ReferenceEquals(next, doc)) return doc;
                applied = applied with { Changed = true, ItemId = entryId, GroupId = e.GroupId };
                // A fold is owned as its sections: the first member's item stands for it.
                itemId = e.Kind == BoardEntryKind.Fold && !e.Members.IsDefaultOrEmpty ? FeedPrefix + e.Members[0].Key : entryId;
                return next;
            }
            case BoardEntryKind.FillCell when e.Raw is PinSource p:
            {
                if (doc.ItemCount >= HomeDoc.MaxItems || e.Parent is not { } fillId) return doc;
                var next = IntoFill(doc, shown, fillId, CellsOfBefore(shown, fillId, at), new HomeItem(entryId, p, HomeSize.Auto, s_noOptions), consumesCell: true);
                if (ReferenceEquals(next, doc)) return doc;
                applied = applied with { Changed = true, ItemId = entryId, GroupId = e.GroupId };
                itemId = entryId;
                return next;
            }
            default:
                return doc;
        }
    }

    /// <summary>How many cells of the Fill item <paramref name="fillId"/> are shown ahead of entry index <paramref name="at"/>.</summary>
    static int CellsOfBefore(BoardModel shown, string fillId, int at)
    {
        int n = 0;
        for (int i = 0; i < at && i < shown.Entries.Length; i++)
            if (shown.Entries[i].Kind == BoardEntryKind.FillCell && string.Equals(shown.Entries[i].Parent, fillId, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>How many cells the Fill item <paramref name="fillId"/> shows now.</summary>
    static int CellsOf(BoardModel shown, string fillId) => CellsOfBefore(shown, fillId, shown.Entries.Length);

    /// <summary>Put <paramref name="item"/> inside the Fill item <paramref name="fillId"/>'s run, after <paramref name="before"/> of its cells:
    /// the fill splits into a part of <paramref name="before"/> cells (the fill's own id), the item, and the rest (a new id). A cell the item
    /// <paramref name="consumesCell"/> (an owned shortcut) leaves the rest one smaller, so the shortcuts shown stay as many.</summary>
    static HomeDoc IntoFill(HomeDoc doc, BoardModel shown, string fillId, int before, HomeItem item, bool consumesCell)
    {
        var (g, i) = doc.Find(fillId);
        if (g < 0 || doc.Groups[g].Items[i].Source is not FillSource fill) return doc;
        var items = doc.Groups[g].Items;
        int max = fill.Max, take = Math.Clamp(before, 0, max);
        int rest = Math.Max(0, max - take - (consumesCell ? 1 : 0));
        var parts = ImmutableArray.CreateBuilder<HomeItem>(3);
        var fillItem = items[i];
        if (take > 0) parts.Add(fillItem with { Source = fill with { Max = take } });
        parts.Add(item);
        if (rest > 0) parts.Add(take > 0 ? new HomeItem(HomeDocs.NewItemId(), fill with { Max = rest }, HomeSize.Auto, s_noOptions) : fillItem with { Source = fill with { Max = rest } });
        return WithItems(doc, g, items.RemoveAt(i).InsertRange(i, parts.ToImmutable()));
    }

    /// <summary>Neighbouring Fill items of one kind merge back into one (the first's id, the sum of their sizes); an empty one goes. Run after
    /// every command, so a pin moved out from between two parts leaves one fill, as before.</summary>
    /// <summary>The shortcuts grid in a new order (owner 2026-11-16, "one grid, edit inside it"): the cell moves to <paramref name="to"/> among the
    /// grid's cells as shown, and the grid's order becomes the user's: every cell before the fill's end is written in that order (a fill cell becomes
    /// the user's pin, the fill gives up its place, so the grid keeps its count), the fill (what Spotify adds next) stays after them.</summary>
    static HomeDoc ReorderShortcut(HomeDoc doc, BoardModel shown, string entryId, int to, ref HomeApplied applied)
    {
        int pg = doc.GroupIndex(HomeDocs.Pinned);
        if (pg < 0) return doc;
        var cells = HomeShortcuts.Cells(shown);
        int from = -1;
        for (int i = 0; i < cells.Count; i++) if (cells[i].Id == entryId) { from = i; break; }
        if (from < 0 || (uint)to >= (uint)cells.Count || from == to) return doc;
        var moving = cells[from];
        cells.RemoveAt(from);
        cells.Insert(to, moving);

        var items = doc.Groups[pg].Items;
        var byId = new Dictionary<string, HomeItem>(StringComparer.Ordinal);
        foreach (var it in items) byId[it.Id] = it;
        var ordered = ImmutableArray.CreateBuilder<HomeItem>(cells.Count);
        int materialized = 0;
        string? movedId = null;
        foreach (var c in cells)
        {
            HomeItem item;
            if (c.Kind == BoardEntryKind.FillCell && c.Raw is PinSource p)
            {
                item = new HomeItem(HomeDocs.NewItemId(), p, HomeSize.Auto, ImmutableSortedDictionary<string, string>.Empty);
                materialized++;
            }
            else if (byId.TryGetValue(c.Id, out var own)) item = own;
            else continue;
            if (c.Id == entryId) movedId = item.Id;
            ordered.Add(item);
        }
        var b = ImmutableArray.CreateBuilder<HomeItem>(items.Length + materialized);
        bool placed = false;
        foreach (var it in items)
        {
            bool cell = it.Source is PinSource or WidgetSource { Widget: "recents" };
            if (cell || it.Source is FillSource)
            {
                if (!placed) { b.AddRange(ordered); placed = true; }
                if (it.Source is FillSource f) b.Add(it with { Source = f with { Max = Math.Max(0, f.Max - materialized) } });
                continue;
            }
            b.Add(it);
        }
        if (!placed) b.AddRange(ordered);
        applied = applied with { Changed = true, ItemId = movedId, GroupId = HomeDocs.Pinned };
        return WithItems(doc, pg, b.ToImmutable());
    }

    static HomeDoc NormalizeFills(HomeDoc doc)
    {
        var result = doc;
        for (int g = 0; g < doc.Groups.Length; g++)
        {
            var items = doc.Groups[g].Items;
            bool any = false;
            foreach (var it in items) if (it.Source is FillSource) { any = true; break; }
            if (!any) continue;
            var b = ImmutableArray.CreateBuilder<HomeItem>(items.Length);
            foreach (var it in items)
            {
                if (it.Source is FillSource f)
                {
                    if (f.Max <= 0) continue;
                    if (b.Count > 0 && b[^1].Source is FillSource prev && prev.Of == f.Of)
                    {
                        b[^1] = b[^1] with { Source = prev with { Max = prev.Max + f.Max } };
                        continue;
                    }
                }
                b.Add(it);
            }
            if (b.Count != items.Length || !b.SequenceEqual(items)) result = WithItems(result, g, b.ToImmutable());
        }
        return result;
    }

    /// <summary>Every section of the current answer that the feed shows becomes an explicit SpotifySection item, in the
    /// order shown, placed where the feed item is; the feed item stays last as the catch-all.</summary>
    public static HomeDoc TakeControl(HomeDoc doc, BoardModel shown)
    {
        int fg = -1, fi = -1;
        for (int g = 0; g < doc.Groups.Length && fg < 0; g++)
            for (int i = 0; i < doc.Groups[g].Items.Length; i++)
                if (doc.Groups[g].Items[i].Source is SpotifyFeedSource) { fg = g; fi = i; break; }
        if (fg < 0) return doc;

        var placed = ImmutableArray.CreateBuilder<HomeItem>();
        foreach (var e in shown.Entries)
        {
            if (e.Kind == BoardEntryKind.FeedSection && !e.Hidden && e.Raw is SpotifySectionSource s)
                placed.Add(new HomeItem(e.Id, s with { TitleHint = s.TitleHint.Length > 0 ? s.TitleHint : e.Title }, HomeSize.Auto, s_noOptions));
            else if (e.Kind == BoardEntryKind.Fold && !e.Members.IsDefaultOrEmpty && e.Raw is not null && !IsPlaced(doc, e.Members[0].Key))
                foreach (var m in e.Members)
                    placed.Add(new HomeItem(FeedPrefix + m.Key, new SpotifySectionSource(m, ""), HomeSize.Auto, s_noOptions));
        }
        if (placed.Count == 0) return doc;
        var items = doc.Groups[fg].Items;
        return WithItems(doc, fg, items.InsertRange(fi, placed.ToImmutable()));
    }

    static bool IsPlaced(HomeDoc doc, string key) => FindSection(doc, key).Item1 >= 0;

    // ══ moves ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static HomeDoc MoveOnto(HomeDoc doc, BoardModel shown, string entryId, string targetId, ref HomeApplied applied)
    {
        if (string.Equals(entryId, targetId, StringComparison.Ordinal)) return doc;
        int from = shown.IndexOf(entryId), to = shown.IndexOf(targetId);
        if (from < 0 || to < 0) return doc;
        var target = shown.Entries[to];
        bool later = to > from;

        // Onto a fill cell: the item lands inside that fill's run, beside the cell (the fill splits around it; no shortcut becomes the
        // user's but a moved one).
        if (target.Kind == BoardEntryKind.FillCell && target.Parent is { } fillId)
        {
            var self = shown.Entries[from];
            bool sameFill = self.Kind == BoardEntryKind.FillCell && string.Equals(self.Parent, fillId, StringComparison.Ordinal);
            int k = CellsOfBefore(shown, fillId, to) - (sameFill && from < to ? 1 : 0) + (later ? 1 : 0);
            HomeDoc start = doc;
            HomeItem moving;
            if (sameFill && self.Raw is PinSource sp)
                moving = new HomeItem(entryId, sp, HomeSize.Auto, s_noOptions);
            else
            {
                start = Own(doc, shown, entryId, ref applied, out string? oid);
                if (oid is null) return doc;
                var (og, oi) = start.Find(oid);
                if (og < 0) return doc;
                moving = start.Groups[og].Items[oi];
                start = WithItems(start, og, start.Groups[og].Items.RemoveAt(oi));
            }
            var landed = IntoFill(start, shown, fillId, k, moving, consumesCell: sameFill);
            if (ReferenceEquals(landed, start)) return doc;
            applied = applied with { Changed = true, ItemId = moving.Id, GroupId = target.GroupId };
            return landed;
        }

        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        // A target in the feed is owned too (one take-control covers both); a target fill cell stands for the Fill item.
        string? targetItem = null;
        string? targetGroup = null;
        switch (target.Kind)
        {
            case BoardEntryKind.GroupHead: targetGroup = target.GroupId; break;
            case BoardEntryKind.FeedSection:
            case BoardEntryKind.Fold:
            {
                var t = Own(owned, shown, targetId, ref applied, out string? tid);
                owned = t; targetItem = tid;
                if (target.Kind == BoardEntryKind.Fold && later && !target.Members.IsDefaultOrEmpty) targetItem = FeedPrefix + target.Members[^1].Key;
                break;
            }
            case BoardEntryKind.FillCell: targetItem = FillItemOf(owned, target.GroupId); break;
            default: targetItem = targetId; break;
        }

        var (g, i) = owned.Find(id);
        if (g < 0) return doc;
        var item = owned.Groups[g].Items[i];
        var removed = WithItems(owned, g, owned.Groups[g].Items.RemoveAt(i));

        int tg, ti;
        if (targetGroup is not null) { tg = removed.GroupIndex(targetGroup); ti = 0; }
        else
        {
            if (targetItem is null) return doc;
            (tg, ti) = removed.Find(targetItem);
            if (tg < 0) return doc;
            if (later) ti++;
        }
        if (tg < 0) return doc;
        var result = WithItems(removed, tg, removed.Groups[tg].Items.Insert(Math.Min(ti, removed.Groups[tg].Items.Length), item));
        applied = applied with { Changed = true, ItemId = id, GroupId = result.Groups[tg].Id };
        return result;
    }

    static string? FillItemOf(HomeDoc doc, string groupId)
    {
        int g = doc.GroupIndex(groupId);
        if (g < 0) return null;
        foreach (var it in doc.Groups[g].Items) if (it.Source is FillSource) return it.Id;
        return null;
    }

    /// <summary>One step at a time through the flat order of group heads and items (the concept's moveStep): past the
    /// end of a group the item becomes the first of the next; before the start, the last of the previous. Items that
    /// show nothing (the feed's catch-all, a placed section the answer lacks) are stepped over.</summary>
    static HomeDoc MoveBy(HomeDoc doc, BoardModel shown, string entryId, int delta, ref HomeApplied applied)
    {
        if (delta == 0) return doc;
        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        var d = owned;
        bool moved = false;
        int steps = Math.Abs(delta), dir = Math.Sign(delta);
        for (int s = 0; s < steps; s++)
        {
            if (!Step(d, shown, id, dir, out var n)) break;
            d = n; moved = true;
        }
        if (!moved) { applied = applied with { ItemId = id }; return ReferenceEquals(owned, doc) ? doc : owned; }
        var (g, _) = d.Find(id);
        applied = applied with { Changed = true, ItemId = id, GroupId = d.Groups[g].Id };
        return d;
    }

    static bool Step(HomeDoc doc, BoardModel shown, string id, int dir, out HomeDoc next)
    {
        next = doc;
        var (g, i) = doc.Find(id);
        if (g < 0) return false;
        var items = doc.Groups[g].Items;
        var item = items[i];
        int j = i + dir;
        while (j >= 0 && j < items.Length && (ShowsNothing(items[j], shown) || items[j].Source is FillSource && CellsOf(shown, items[j].Id) == 0)) j += dir;
        // A Fill item next in the way is a run of cells, not one: one step moves one of its cells to the other side of the item (a fill is
        // dealt in order, so its first cell after the item becomes the last before it, and the other way).
        if (j >= 0 && j < items.Length && items[j].Source is FillSource fill)
        {
            var list = items;
            list = list.SetItem(j, list[j] with { Source = fill with { Max = fill.Max - 1 } });
            int other = i - dir;
            if (other >= 0 && other < list.Length && list[other].Source is FillSource near && near.Of == fill.Of)
                list = list.SetItem(other, list[other] with { Source = near with { Max = near.Max + 1 } });
            else
                list = list.Insert(dir > 0 ? i : i + 1, new HomeItem(HomeDocs.NewItemId(), fill with { Max = 1 }, HomeSize.Auto, s_noOptions));
            next = WithItems(doc, g, list);
            return true;
        }
        if (j >= 0 && j < items.Length)
        {
            var list = items.RemoveAt(i);
            next = WithItems(doc, g, list.Insert(dir > 0 ? j : j, item));
            return true;
        }
        // Across a group head: into the neighbouring group's near end.
        int ng = g + dir;
        if (ng < 0 || ng >= doc.Groups.Length) return false;
        var without = WithItems(doc, g, items.RemoveAt(i));
        var target = without.Groups[ng].Items;
        next = WithItems(without, ng, dir > 0 ? target.Insert(0, item) : target.Add(item));
        return true;
    }

    static bool ShowsNothing(HomeItem it, BoardModel shown)
    {
        if (it.Source is SpotifyFeedSource) return true;
        int at = shown.IndexOf(it.Id);
        if (it.Source is FillSource) return false;
        return at >= 0 ? shown.Entries[at].Hidden : it.Source is SpotifySectionSource;
    }

    static HomeDoc MoveToEdge(HomeDoc doc, BoardModel shown, string entryId, bool toStart, ref HomeApplied applied)
    {
        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        var (g, i) = owned.Find(id);
        if (g < 0) return doc;
        var items = owned.Groups[g].Items;
        int dest = toStart ? 0 : items.Length - 1;
        if (dest == i) { applied = applied with { ItemId = id, GroupId = owned.Groups[g].Id }; return ReferenceEquals(owned, doc) ? doc : owned; }
        var item = items[i];
        var result = WithItems(owned, g, items.RemoveAt(i).Insert(dest, item));
        applied = applied with { Changed = true, ItemId = id, GroupId = owned.Groups[g].Id };
        return result;
    }

    static HomeDoc MoveToGroup(HomeDoc doc, BoardModel shown, string entryId, string groupId, int at, ref HomeApplied applied)
    {
        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        var (g, i) = owned.Find(id);
        int tg = owned.GroupIndex(groupId);
        if (g < 0 || tg < 0) return doc;
        var item = owned.Groups[g].Items[i];
        var removed = WithItems(owned, g, owned.Groups[g].Items.RemoveAt(i));
        var target = removed.Groups[tg].Items;
        int index = at < 0 ? target.Length : Math.Min(at, target.Length);
        if (g == tg && index == i) return ReferenceEquals(owned, doc) ? doc : owned;
        applied = applied with { Changed = true, ItemId = id, GroupId = groupId };
        return WithItems(removed, tg, target.Insert(index, item));
    }

    // ══ size and options ════════════════════════════════════════════════════════════════════════════════════════════

    static HomeDoc SetSize(HomeDoc doc, BoardModel shown, HomeSourceRegistry sources, string entryId, HomeSize size, bool cycle, ref HomeApplied applied)
    {
        // Check the size against the entry's source BEFORE owning anything: a refused size takes no control.
        HomeSourceDef? def = null;
        HomeSize current = HomeSize.Auto;
        int at = shown.IndexOf(entryId);
        if (at >= 0) { def = shown.Entries[at].Source; current = shown.Entries[at].Size; }
        else if (doc.Find(entryId) is var (dg, di) && dg >= 0)
        {
            var it = doc.Groups[dg].Items[di];
            def = sources.Of(it.Source); current = it.Size;
        }
        if (def is null) return doc;
        if (cycle) size = def.Next(current);
        if (!def.Declares(size) || size == current) return doc;

        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        var (g, i) = owned.Find(id);
        if (g < 0) return doc;
        var item = owned.Groups[g].Items[i];
        applied = applied with { Changed = true, ItemId = id, GroupId = owned.Groups[g].Id };
        return WithItems(owned, g, owned.Groups[g].Items.SetItem(i, item with { Size = size }));
    }

    static HomeDoc SetOptions(HomeDoc doc, BoardModel shown, string entryId, ImmutableSortedDictionary<string, string> patch, ref HomeApplied applied)
    {
        if (patch.Count == 0) return doc;
        var owned = Own(doc, shown, entryId, ref applied, out string? id);
        if (id is null) return doc;
        var (g, i) = owned.Find(id);
        if (g < 0) return doc;
        var item = owned.Groups[g].Items[i];
        var opts = item.Options;
        foreach (var (k, v) in patch) opts = v.Length == 0 ? opts.Remove(k) : opts.SetItem(k, v);   // "" clears a key
        if (opts.Count == item.Options.Count && opts.SequenceEqual(item.Options)) return ReferenceEquals(owned, doc) ? doc : owned;
        applied = applied with { Changed = true, ItemId = id, GroupId = owned.Groups[g].Id };
        return WithItems(owned, g, owned.Groups[g].Items.SetItem(i, item with { Options = opts }));
    }

    // ══ reset ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static HomeDoc Reset(HomeDoc doc, HomeResetScope scope, ref HomeApplied applied)
    {
        var fresh = HomeDocs.Default() with { Seen = scope == HomeResetScope.Everything ? [] : doc.Seen, SeededFromSidebar = doc.SeededFromSidebar };
        // The user's pins stay (D10): in Pinned, in their order, at their natural size; a page reset keeps their options.
        int pg = fresh.GroupIndex(HomeDocs.Pinned);
        if (pg >= 0)
        {
            var pins = ImmutableArray.CreateBuilder<HomeItem>();
            foreach (var g in doc.Groups)
                foreach (var it in g.Items)
                    if (it.Source is PinSource)
                        pins.Add(it with { Size = HomeSize.Auto, Options = scope == HomeResetScope.Everything ? s_noOptions : it.Options });
            var items = fresh.Groups[pg].Items;
            fresh = WithItems(fresh, pg, items.InsertRange(PinSlot(items), pins.ToImmutable()));
        }
        if (scope == HomeResetScope.Page)
        {
            // A page reset is about layout: a widget's options survive on the default's widget of the same kind.
            for (int g = 0; g < fresh.Groups.Length; g++)
            {
                var items = fresh.Groups[g].Items;
                for (int i = 0; i < items.Length; i++)
                    if (items[i].Source is WidgetSource w && FindWidget(doc, w.Widget) is var (og, oi) && og >= 0)
                        items = items.SetItem(i, items[i] with { Options = doc.Groups[og].Items[oi].Options });
                fresh = WithItems(fresh, g, items);
            }
        }
        if (Same(doc, fresh)) return doc;
        applied = applied with { Changed = true, GroupId = null };
        return fresh;
    }

    /// <summary>Structural equality of what a reset restores (groups, items, hidden, seen).</summary>
    static bool Same(HomeDoc a, HomeDoc b)
        => HomeDocWire.WriteText(a with { Rev = 0, UpdatedAtMs = 0 }) == HomeDocWire.WriteText(b with { Rev = 0, UpdatedAtMs = 0 });

    // ══ helpers ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static HomeDoc WithItems(HomeDoc doc, int g, ImmutableArray<HomeItem> items)
        => doc with { Groups = doc.Groups.SetItem(g, doc.Groups[g] with { Items = items }) };
}
