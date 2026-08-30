using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The three upcoming sections of the queue panel, in display order.</summary>
enum QueueSection : byte { Queue, NextUp, Autoplay }

/// <summary>What one slot of the upcoming list IS. A <see cref="Row"/> is a queue entry the user can lift; a
/// <see cref="Header"/> and a <see cref="More"/> row are non-items that nevertheless occupy a slot, because the
/// reorder geometry counts every child of the wrapped column (see <see cref="QueueSlots"/>).</summary>
enum QueueSlotKind : byte { Row, Header, More }

/// <summary>One slot of the upcoming list as the reorder sees it. <see cref="Pos"/> is the SECTION-relative row index
/// for a row (the index <c>PlaybackSession.MoveItem</c> addresses), −1 for a header / more row; <see cref="Entry"/>
/// is the row's queue entry (null for the non-items).</summary>
readonly record struct QueueSlot(QueueSlotKind Kind, QueueSection Section, QueueEntry? Entry, int Pos)
{
    public static QueueSlot Header(QueueSection section) => new(QueueSlotKind.Header, section, null, -1);
    public static QueueSlot More(QueueSection section) => new(QueueSlotKind.More, section, null, -1);
    public static QueueSlot Row(QueueSection section, QueueEntry entry, int pos) => new(QueueSlotKind.Row, section, entry, pos);
    public bool IsRow => Kind == QueueSlotKind.Row;
}

/// <summary>
/// The upcoming list FLATTENED into reorder slots — engine-free, so <c>Wavee.Tests</c> pins the shape the panel
/// renders and the shape <see cref="QueueMovePlan"/> decides over are one and the same list.
///
/// <para>ONE <c>Reorderable</c> now spans the whole upcoming list ("Next in queue" → "Next up" → "Autoplay"), and its
/// slot math assumes the wrapped column is nothing but stacked slots at the extents it was told. The section headers
/// and the per-section "Show more" rows sit INSIDE that column, so they are modelled as slots of their own kind rather
/// than hidden from the geometry: a header that the reorder did not know about would shift every slot boundary below
/// it by its height, and the drop would land one row off. They are never items the user can lift — the panel renders
/// them as plain keyed children (no drag source) and <see cref="QueueMovePlan.For"/> answers <c>NoOp</c> for them.</para>
///
/// <para>Only the REALIZED rows are slots (the sections paginate visually): the rows the user can see are the rows
/// they can aim between, and a hidden row cannot be displaced by a projection it is not part of.</para>
/// </summary>
static class QueueSlots
{
    /// <summary>Rows realized for a section under the visual pagination: whole pages of <paramref name="pageSize"/>,
    /// never more than the section holds.</summary>
    public static int Realized(int count, int pages, int pageSize)
        => Math.Min(count, Math.Max(1, pages) * Math.Max(1, pageSize));

    /// <summary>Flatten the three sections. A section with no rows contributes nothing — not even its header, which is
    /// the panel's own rule (a header over an empty section is a lie). <paramref name="headers"/> is false for the
    /// stage pane, whose sections run into each other with no captions. Autoplay rows are listed only while the toggle
    /// is on, exactly as they are rendered.</summary>
    public static List<QueueSlot> Build(IReadOnlyList<QueueEntry> queue, int shownQueue,
                                        IReadOnlyList<QueueEntry> nextUp, int shownNextUp,
                                        IReadOnlyList<QueueEntry> autoplay, int shownAutoplay,
                                        bool autoplayOn, bool headers)
    {
        var slots = new List<QueueSlot>(shownQueue + shownNextUp + (autoplayOn ? shownAutoplay : 0) + 6);
        Append(slots, QueueSection.Queue, queue, shownQueue, headers);
        Append(slots, QueueSection.NextUp, nextUp, shownNextUp, headers);
        if (autoplayOn) Append(slots, QueueSection.Autoplay, autoplay, shownAutoplay, headers);
        return slots;
    }

    static void Append(List<QueueSlot> slots, QueueSection section, IReadOnlyList<QueueEntry> rows, int shown, bool header)
    {
        if (rows.Count == 0) return;
        int n = Math.Clamp(shown, 0, rows.Count);
        if (header) slots.Add(QueueSlot.Header(section));
        for (int i = 0; i < n; i++) slots.Add(QueueSlot.Row(section, rows[i], i));
        if (rows.Count > n) slots.Add(QueueSlot.More(section));
    }
}

/// <summary>The verdict of one flat reorder commit.</summary>
enum QueueMoveKind : byte
{
    /// <summary>Nothing to do: the same slot, a non-row lifted (a header reached by the keyboard lift), or an index the
    /// slot list does not have (the list re-rendered under the gesture).</summary>
    NoOp,
    /// <summary>A section-local move: <see cref="QueueMove.Entry"/> goes from <see cref="QueueMove.FromPos"/> to
    /// <see cref="QueueMove.ToPos"/> inside <see cref="QueueMove.Section"/> (both section-relative).</summary>
    Move,
    /// <summary>The drop landed in ANOTHER section. The queue model keeps every row inside its own provider section
    /// (<c>PlaybackSession.MoveItem</c>), so the panel refuses it with one caption instead of guessing.</summary>
    Refused,
}

/// <summary>What the panel does with a flat <c>(from, to)</c> — see <see cref="QueueMovePlan.For"/>.</summary>
readonly record struct QueueMove(QueueMoveKind Kind, QueueSection Section, QueueEntry? Entry, int FromPos, int ToPos)
{
    public static readonly QueueMove NoOp = new(QueueMoveKind.NoOp, QueueSection.Queue, null, -1, -1);
}

/// <summary>
/// The PURE decision behind the queue panel's whole-list reorder: a flat display <c>(from, to)</c> from the one
/// <c>Reorderable</c> → either a section-local move the model can perform, a refusal, or nothing.
///
/// <para>The commit convention is the engine's own (<c>ReorderList.Complete</c> → <c>OnReorder(from, to)</c>): remove
/// the item at <paramref name="from"/>, then insert it at <paramref name="to"/> in the list that is one shorter. In
/// that post-removal list the lifted row's own section spans <c>[start, end − 1)</c>, and inserting at <c>end − 1</c>
/// puts the row LAST in its section — the same boundary the next section starts at. The boundary is claimed for the
/// row's own section on purpose: it is the one slot the user can aim at from either side, and "last in Next up" is a
/// move the model has, while "first in Autoplay" is not. So the legal window is <c>start ≤ to ≤ end − 1</c>, with the
/// section-relative position <c>to − start</c> — which is exactly the <c>[0, Count − 1]</c> that
/// <c>PlaybackSession.MoveItem</c> clamps to, so the optimistic <see cref="QueueOrder.Move"/> and the authoritative op
/// keep agreeing.</para>
///
/// <para>A single-row section has a legal window of one slot — its own — so every other drop from it is a refusal,
/// never a silent no-op: the finding this exists for was the lone queued track whose drag had no legal target and
/// ended as a playlist add.</para>
/// </summary>
static class QueueMovePlan
{
    public static QueueMove For(IReadOnlyList<QueueSlot> slots, int from, int to)
    {
        if ((uint)from >= (uint)slots.Count || (uint)to >= (uint)slots.Count) return QueueMove.NoOp;
        var lifted = slots[from];
        if (!lifted.IsRow || lifted.Entry is not { } entry) return QueueMove.NoOp;
        if (to == from) return QueueMove.NoOp;

        // The lifted row's section is a CONTIGUOUS run of row slots (the header precedes it, the more-row follows it),
        // so its flat extent is the first and one-past-the-last row of that section.
        int start = from, end = from + 1;
        while (start > 0 && IsRowOf(slots[start - 1], lifted.Section)) start--;
        while (end < slots.Count && IsRowOf(slots[end], lifted.Section)) end++;

        if (to < start || to > end - 1)
            return new QueueMove(QueueMoveKind.Refused, lifted.Section, entry, lifted.Pos, -1);
        int toPos = to - start;
        return toPos == lifted.Pos
            ? QueueMove.NoOp
            : new QueueMove(QueueMoveKind.Move, lifted.Section, entry, lifted.Pos, toPos);
    }

    /// <summary>Where a FOREIGN deposit at insertion slot <paramref name="slot"/> (0..<c>slots.Count</c>) lands in the
    /// USER QUEUE: the number of queue rows above that boundary. A drop anywhere below the user queue — on "Next up",
    /// on the autoplay tail, on the empty space under the list — appends to the queue, because that is the only place
    /// the model can put a track the user wants to hear next-ish: after everything they already queued, before the
    /// continuation. Slot 0 is the play-next insert.</summary>
    public static int InsertIndex(IReadOnlyList<QueueSlot> slots, int slot)
    {
        int n = 0;
        int limit = Math.Min(slot, slots.Count);
        for (int i = 0; i < limit; i++)
            if (IsRowOf(slots[i], QueueSection.Queue)) n++;
        return n;
    }

    static bool IsRowOf(in QueueSlot slot, QueueSection section) => slot.IsRow && slot.Section == section;
}
