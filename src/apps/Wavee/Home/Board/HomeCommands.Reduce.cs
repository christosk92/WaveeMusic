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
            HomeCommand.SetSize s => SetSize(doc, shown, sources, s.EntryId, s.Size, cycle: false, ref applied),
            HomeCommand.CycleSize c => SetSize(doc, shown, sources, c.EntryId, HomeSize.Auto, cycle: true, ref applied),
            HomeCommand.SetOptions o => SetOptions(doc, shown, o.EntryId, o.Patch, ref applied),
            HomeCommand.Reset r => Reset(doc, r.Scope, ref applied),
            _ => doc,
        };
        if (ReferenceEquals(next, doc) || !applied.Changed) { applied = applied with { Changed = false }; return doc; }
        return next with { Rev = doc.Rev + 1, UpdatedAtMs = nowMs };
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
    /// the user's pin where it is shown. Returns the item id (the entry id), or null when the entry cannot be owned.</summary>
    /// <param name="through">A fill cell is owned IN PLACE: with it, every cell of its fill run shown ahead of it, and up to this shown index
    /// (a move's destination inside the run), become the user's pins in the order shown, so the order seen is the order saved (a pin can only
    /// stand ahead of the Fill item, never between its cells).</param>
    static HomeDoc Own(HomeDoc doc, BoardModel shown, string entryId, ref HomeApplied applied, out string? itemId, int through = -1)
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
                if (doc.ItemCount >= HomeDoc.MaxItems) return doc;
                int g = doc.GroupIndex(e.GroupId);
                if (g < 0) return doc;
                var items = doc.Groups[g].Items;
                int fill = -1;
                for (int i = 0; i < items.Length; i++) if (items[i].Source is FillSource) { fill = i; break; }
                int index = fill >= 0 ? fill : items.Length;
                // In place: the run's cells from its start through this one (or through the move's destination) become pins in the order shown,
                // before the Fill item; the cells after them stay the fill's.
                int start = at;
                while (start > 0 && shown.Entries[start - 1].Kind == BoardEntryKind.FillCell && shown.Entries[start - 1].GroupId == e.GroupId) start--;
                int last = Math.Max(at, through);
                int end = at;
                while (end + 1 < shown.Entries.Length && end < last && shown.Entries[end + 1].Kind == BoardEntryKind.FillCell
                       && shown.Entries[end + 1].GroupId == e.GroupId) end++;
                var add = ImmutableArray.CreateBuilder<HomeItem>();
                for (int k = start; k <= end && doc.ItemCount + add.Count < HomeDoc.MaxItems; k++)
                    if (shown.Entries[k].Raw is PinSource kp && doc.Find(shown.Entries[k].Id).Group < 0)
                        add.Add(new HomeItem(shown.Entries[k].Id, kp, HomeSize.Auto, s_noOptions));
                if (add.Count == 0) return doc;
                applied = applied with { Changed = true, ItemId = entryId, GroupId = e.GroupId };
                itemId = entryId;
                return WithItems(doc, g, items.InsertRange(index, add.ToImmutable()));
            }
            default:
                return doc;
        }
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

        var owned = Own(doc, shown, entryId, ref applied, out string? id, through: to);
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
            case BoardEntryKind.FillCell:
            {
                // A fill cell target is owned in place too (its run through it), so the item lands exactly beside it.
                owned = Own(owned, shown, targetId, ref applied, out string? tid, through: to);
                targetItem = tid ?? FillItemOf(owned, target.GroupId);
                break;
            }
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
        int at = shown.IndexOf(entryId);
        var owned = Own(doc, shown, entryId, ref applied, out string? id, through: at < 0 ? -1 : at + delta);
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
        while (j >= 0 && j < items.Length && ShowsNothing(items[j], shown)) j += dir;
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
