using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>What a playlist insertion's chip claims the drop will do (see <see cref="PlaylistReorderRules.VerbFor"/>).
/// <see cref="AddContainer"/> is the honest "some tracks, count not yet known" case.</summary>
enum PlaylistDropVerb : byte { None = 0, MoveRows = 1, AddTracks = 2, AddContainer = 3 }

/// <summary>When a same-playlist drag may reorder membership. Engine-free by design so the rule is pinned by
/// PlaylistReorderRulesTests against production code rather than a copy of it.</summary>
static class PlaylistReorderRules
{
    /// <summary>A same-list move addresses ORIGINAL membership rows through the DISPLAYED order, so it is only
    /// unambiguous while the displayed order IS the membership order. Sorting was already gated; a text query or an
    /// advanced filter is the same hazard — the display→original map then skips rows, so "insert before the row at
    /// display slot N" no longer names the membership position the user aimed at.
    /// <paramref name="naturalOrder"/> = the list is sorted by its own index, ascending.</summary>
    public static bool AllowsSameListMove(bool naturalOrder, string query, in TrackFilterState filters)
        => naturalOrder && query.Length == 0 && filters.IsDefault;

    /// <summary>What a playlist insertion's drop caption should CLAIM — the one part of the sentence that is a semantic
    /// promise rather than a formatting detail. A same-playlist drop MOVES rows out of their slots; a foreign drop with
    /// a track snapshot ADDS a known number; a container still behind a cold resolver adds an unknown number, which must
    /// be said without a count rather than with a fabricated one.</summary>
    public static PlaylistDropVerb VerbFor(bool sameList, int sourceRowCount, int trackCount)
        => sameList
            ? (sourceRowCount > 0 ? PlaylistDropVerb.MoveRows : PlaylistDropVerb.None)
            : trackCount > 0 ? PlaylistDropVerb.AddTracks : PlaylistDropVerb.AddContainer;

    /// <summary>May the Alt+Up / Alt+Down keyboard block move run? Same ambiguity as the drag (a display order that is
    /// not the membership order cannot name a membership position), plus the write gate the drag gets from its own
    /// mount site. This is the a11y/Pragmatic answer to "how do I reorder without a mouse": an outcome-equivalent
    /// COMMAND, deliberately not a simulated keyboard drag.</summary>
    public static bool AllowsBlockMove(bool canEditItems, bool naturalOrder, string query, in TrackFilterState filters)
        => canEditItems && AllowsSameListMove(naturalOrder, query, filters);

    /// <summary>The PRE-move insertion index for shifting the selected rows by <paramref name="delta"/> (±1), or −1 when
    /// the move is illegal. "Pre-move" is the backend's own convention — insert before the row currently at this index —
    /// which is why moving DOWN targets <c>max + 2</c>: the row being jumped is still counted at that moment
    /// (MoveRowsConventionTests pins it; pre-correcting for the removed rows moves the block twice).
    /// <para>A NON-CONTIGUOUS selection is refused rather than guessed at: "one row up" has no single meaning for a
    /// gapped set — collapsing the gaps is a different edit, and nobody asked for it.</para></summary>
    public static int BlockMoveTarget(ReadOnlySpan<int> originalIndices, int itemCount, int delta)
    {
        if (originalIndices.Length == 0 || itemCount <= 0 || (delta != -1 && delta != 1)) return -1;
        int min = originalIndices[0], max = originalIndices[0];
        for (int i = 1; i < originalIndices.Length; i++)
        {
            if (originalIndices[i] < min) min = originalIndices[i];
            if (originalIndices[i] > max) max = originalIndices[i];
        }
        if (min < 0 || max >= itemCount) return -1;
        if (max - min + 1 != originalIndices.Length) return -1;   // gapped (or duplicated) selection
        if (delta < 0) return min > 0 ? min - 1 : -1;
        return max + 1 < itemCount ? max + 2 : -1;
    }

    // ── the KEYED-reorder gate (P2) ───────────────────────────────────────────────────────────────────────────────
    // The wire reorder is ONE item-keyed MOV: every moved row is named by its `item_id`, and the landing position is
    // named by ONE anchor row's `item_id` (add_first / add_last for the two ends). There is deliberately NO positional
    // fallback — sending indices while our own add is still in flight is exactly how rows land in the wrong place —
    // so a row whose id has not arrived yet cannot be moved, and neither can a drop whose anchor row has no id.
    // These two predicates answer that BEFORE the gesture commits, so the refusal is a caption rather than a rolled
    // back move. They mirror PlaylistMutationSource.BuildKeyedMove's own derivation (which walks the same anchor back
    // over the rows being moved); this is the page-side half of the same rule.

    /// <summary>Every moved row carries the membership <c>item_id</c> the keyed MOV names it by.</summary>
    public static bool RowsAreKeyed(IReadOnlyList<PlaylistRowRef> rows)
    {
        if (rows.Count == 0) return false;
        for (int i = 0; i < rows.Count; i++)
            if (string.IsNullOrEmpty(rows[i].ItemId)) return false;
        return true;
    }

    /// <summary>The ORIGINAL membership index a DISPLAY slot names ("insert before the row currently here"). Slot 0 is
    /// the head of the displayed order and the end is the end of MEMBERSHIP, which is why the two edges do not read
    /// through the view map at all.</summary>
    public static int OriginalInsertionIndex(ReadOnlySpan<int> view, int trackCount, int displaySlot)
    {
        if (displaySlot <= 0) return view.Length > 0 ? view[0] : 0;
        if (displaySlot >= view.Length) return trackCount;
        return view[displaySlot];
    }

    /// <summary>Will the row that becomes the MOVE'S ANCHOR carry an <c>item_id</c>? The two ends (<c>add_first</c> /
    /// <c>add_last</c>) need no anchor at all and are therefore always keyed; otherwise the anchor is the predecessor
    /// at <paramref name="at"/>−1, walking back over rows that are themselves being moved (that is what lets a GAPPED
    /// selection land as one contiguous run) — nothing left above means <c>add_first</c>.</summary>
    /// <param name="tracks">Membership in ORIGINAL order; <c>Track.ContextUid</c> is the row's <c>item_id</c>.</param>
    /// <param name="moved">The rows being moved (ORIGINAL indices).</param>
    /// <param name="at">The PRE-move insertion index into <paramref name="tracks"/>.</param>
    public static bool AnchorRowIsKeyedAt(IReadOnlyList<Track> tracks, IReadOnlyList<PlaylistRowRef> moved, int at)
    {
        if (at <= 0 || at >= tracks.Count) return true;      // add_first / add_last — no anchor row to name
        int j = at - 1;
        while (j >= 0 && IsMoved(moved, j)) j--;
        if (j < 0) return true;                              // everything above is moving → add_first
        return !string.IsNullOrEmpty(tracks[j].ContextUid);

        static bool IsMoved(IReadOnlyList<PlaylistRowRef> rows, int original)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i].Index == original) return true;
            return false;
        }
    }

    /// <summary>The same question asked in DISPLAY coordinates (what a drop hands us).</summary>
    public static bool AnchorRowIsKeyed(ReadOnlySpan<int> view, IReadOnlyList<Track> tracks,
                                        IReadOnlyList<PlaylistRowRef> moved, int displaySlot)
        => AnchorRowIsKeyedAt(tracks, moved, OriginalInsertionIndex(view, tracks.Count, displaySlot));

    /// <summary>Invert the display→original view map for ONE dragged row: the drag payload carries ORIGINAL membership
    /// indices, while the framework's virtual-removal math counts DISPLAY positions. A same-list move is only legal in
    /// natural order (see <see cref="AllowsSameListMove"/>), so the map is normally the identity and the O(1) probe
    /// answers; the scan is the defensive fallback for a view that is not. −1 = the row is not displayed.</summary>
    public static int DisplayRowOf(int originalIndex, ReadOnlySpan<int> view)
    {
        if ((uint)originalIndex < (uint)view.Length && view[originalIndex] == originalIndex) return originalIndex;
        for (int d = 0; d < view.Length; d++)
            if (view[d] == originalIndex) return d;
        return -1;
    }
}
