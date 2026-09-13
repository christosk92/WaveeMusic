using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The queue panel's OPTIMISTIC reorder, as pure data — engine-free by design so <c>Wavee.Tests</c> pins it against the
/// production code (the panel itself is elements and signals, which a headless test cannot compile).
///
/// <para>The authoritative op (<c>PlaybackSession.MoveItem</c> → <c>IPlaybackPlayer.MoveQueueItemAsync</c>) is a
/// REMOVE + INSERT at a section-relative index: the row leaves its slot and every row between the two ends shifts by
/// one. The panel used to mirror it with a two-element SWAP, which coincides only for an adjacent (±1) move — exactly
/// the context-menu verbs it was written for. A drag across several slots rendered a swap for one frame and then
/// snapped to the server's real order, which reads as the drag having landed somewhere else.</para>
///
/// <para>Sections are addressed by the panel's OWN rendered list (the entries it shows for that bucket), not by a
/// bucket predicate over the flat queue: the panel hides the currently-playing entry, so a predicate would number the
/// rows differently from what the user is dragging.</para>
/// </summary>
static class QueueOrder
{
    /// <summary>Session-stable identity: the queue item id when both rows carry one (it survives reorder/remove), else
    /// the derived entry id.</summary>
    public static bool Same(QueueEntry a, QueueEntry b)
        => a.ItemId.IsNone || b.ItemId.IsNone
            ? string.Equals(a.EntryId, b.EntryId, StringComparison.Ordinal)
            : a.ItemId == b.ItemId;

    /// <summary>The flat-queue positions of <paramref name="section"/>'s rows, in display order. Empty when any row is
    /// missing from the snapshot (it moved under us — the authoritative push is the only honest answer then).</summary>
    public static IReadOnlyList<int> Positions(IReadOnlyList<QueueEntry> queue, IReadOnlyList<QueueEntry> section)
    {
        var positions = new List<int>(section.Count);
        int from = 0;
        for (int i = 0; i < section.Count; i++)
        {
            int at = -1;
            for (int j = from; j < queue.Count; j++)
                if (Same(queue[j], section[i])) { at = j; break; }
            if (at < 0) return Array.Empty<int>();
            positions.Add(at);
            from = at + 1;   // a section is a SUBSEQUENCE of the queue, so positions only ever advance
        }
        return positions;
    }

    /// <summary>Move section row <paramref name="from"/> to section slot <paramref name="to"/> — remove, then insert at
    /// the post-removal index, the same convention <c>PlaybackSession.MoveItem</c> and <c>ReorderList.Move</c> use.
    /// The section's rows are rewritten into their own flat positions, so rows of other buckets never shift. Returns
    /// the input untouched when the move is a no-op or the snapshot no longer contains the section.</summary>
    public static IReadOnlyList<QueueEntry> Move(IReadOnlyList<QueueEntry> queue, IReadOnlyList<QueueEntry> section,
                                                 int from, int to)
    {
        if (section.Count == 0 || (uint)from >= (uint)section.Count) return queue;
        int at = Math.Clamp(to, 0, section.Count - 1);
        if (at == from) return queue;
        var positions = Positions(queue, section);
        if (positions.Count != section.Count) return queue;

        var order = new List<QueueEntry>(section.Count);
        for (int i = 0; i < positions.Count; i++) order.Add(queue[positions[i]]);
        var moved = order[from];
        order.RemoveAt(from);
        order.Insert(at, moved);

        var next = new List<QueueEntry>(queue);
        for (int i = 0; i < positions.Count; i++) next[positions[i]] = order[i];
        return next;
    }

    /// <summary>Drop a row from the flat snapshot by identity (the panel's ✕ / swipe-remove).</summary>
    public static IReadOnlyList<QueueEntry> Remove(IReadOnlyList<QueueEntry> queue, QueueEntry entry)
    {
        var next = new List<QueueEntry>(queue.Count);
        for (int i = 0; i < queue.Count; i++)
            if (!Same(queue[i], entry)) next.Add(queue[i]);
        return next;
    }
}
