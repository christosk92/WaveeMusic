// ── Entities/Queue.Rules.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Wave 5 queue rules: the section split, QueueSlots, QueueMovePlan, QueueOrder (move · follow · skip) and the
// session writes a verb, a drop and a controller land through
//
// Role: CORE
// Owner: Q
// Wave: 5
// Budget: 550 lines — a NAMED PARTIAL of Queue.cs, whose Wave 1 model already passed §2's 250 on its own
// Spec: ch 21 §8 (QueueSlots / QueueMovePlan / QueueOrder, ported), §6.4 (the verbs), §7 (forward-looking only);
//       gap register G-074 Q half (the cursor-aware, duplicate-safe enqueue). G-070's context load and G-080's shuffle
//       order and autoplay append are ONE path in the playback host (B3, Playback.Host.Context.cs), not restated here
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DIVIDER. A queue surface shows what comes AFTER the row the deck is on, and nothing before it (ch 21 §0 #5).
// Locally that row is the reducer's cursor; for a remote device's mirrored list, or a session nothing has played yet,
// it is the NowPlaying row (`Divider`). Sections are a row's PROVENANCE, not its position: a user-queued row is
// "Next in queue", an autoplay row is "Autoplay", everything else is "Next up" (`SectionOf`).
//
// FOLLOW. The storage invariant is that buckets describe the cursor — history before it, the deck ON it, the user's
// rows next, then the continuation (Queue.cs's header). The reducer only MOVES the cursor, so something has to put the
// buckets back: `Follow` does, in one pass, never moving the cursor's own index (a retreat that leaves a continuation
// row in front of queued rows slides the queued rows forward — only rows AFTER the cursor ever move). Every write in
// this file follows first, so an edit always lands against the list the listener is actually hearing. The playback
// host should follow after each drain too, so the surfaces that read buckets directly (the NPV "Next up", the sidebar
// feed) stay exact between edits.
//
// EDITS ARE WHOLE-RUN REWRITES over a pooled copy. `EdgeTable.Insert` updates an EXISTING target in place — right for a
// like, wrong for a queue, where the same recording may legitimately sit twice — so a queue splice copies the run into
// rented buffers, edits them with the pure rules below and lands them in one `ReplaceRun`. Tens to hundreds of rows at
// human rate; nothing allocates after the pool warms (P8). A local write is authoritative, so no pending bit is set.

using System.Buffers;

namespace Wavee;

// ── 1. the vocabulary ch 21 §8 ports ───────────────────────────────────────────────────────────────────────────────

/// <summary>The three upcoming sections, in display order.</summary>
public enum QueueSection : byte { Queue, NextUp, Autoplay }

/// <summary>What one slot of the upcoming list IS. Headers and "Show more" rows are not items but still occupy a slot,
/// because the reorder geometry counts every child of the wrapped column.</summary>
public enum QueueSlotKind : byte { Row, Header, More }

/// <summary>One slot as the reorder sees it. <see cref="Pos"/> is the SECTION-relative row index for a row, −1
/// otherwise.</summary>
public readonly record struct QueueSlot(QueueSlotKind Kind, QueueSection Section, int Pos)
{
    public static QueueSlot Header(QueueSection section) => new(QueueSlotKind.Header, section, -1);
    public static QueueSlot More(QueueSection section) => new(QueueSlotKind.More, section, -1);
    public static QueueSlot Row(QueueSection section, int pos) => new(QueueSlotKind.Row, section, pos);
    public bool IsRow => Kind == QueueSlotKind.Row;
}

/// <summary>The verdict of one flat reorder commit.</summary>
public enum QueueMoveKind : byte { NoOp, Move, Refused }

/// <summary>What a flat <c>(from, to)</c> means: a section-local move (both positions section-relative), a refusal
/// (naming what was lifted), or nothing.</summary>
public readonly record struct QueueMove(QueueMoveKind Kind, QueueSection Section, int FromPos, int ToPos)
{
    public static QueueMove NoOp => new(QueueMoveKind.NoOp, QueueSection.Queue, -1, -1);
}

/// <summary>The upcoming list FLATTENED into reorder slots — the shape the panel renders and the shape
/// <see cref="QueueMovePlan"/> decides over are one list.</summary>
public static class QueueSlots
{
    /// <summary>Rows realized under the visual pagination: whole pages, never more than the section holds, and a page
    /// count of 0 still shows page one.</summary>
    public static int Realized(int count, int pages, int pageSize)
        => Math.Min(count, Math.Max(1, pages) * Math.Max(1, pageSize));

    /// <summary>The most slots <see cref="Build"/> can write: every row, three headers, three more-rows.</summary>
    public static int Capacity(int queue, int nextUp, int autoplay) => queue + nextUp + autoplay + 6;

    /// <summary>Flatten the three sections into <paramref name="dst"/>. A section with no rows contributes NOTHING,
    /// header included; autoplay rows are listed only while the toggle is on; <paramref name="headers"/> is false for the
    /// stage's continuous list. Returns the slots written.</summary>
    public static int Build(int queue, int shownQueue, int nextUp, int shownNextUp, int autoplay, int shownAutoplay,
                            bool autoplayOn, bool headers, Span<QueueSlot> dst)
    {
        int n = 0;
        Append(dst, ref n, QueueSection.Queue, queue, shownQueue, headers);
        Append(dst, ref n, QueueSection.NextUp, nextUp, shownNextUp, headers);
        if (autoplayOn) Append(dst, ref n, QueueSection.Autoplay, autoplay, shownAutoplay, headers);
        return n;
    }

    static void Append(Span<QueueSlot> dst, ref int n, QueueSection section, int count, int shown, bool header)
    {
        if (count <= 0) return;
        int rows = Math.Clamp(shown, 0, count);
        if (header && n < dst.Length) dst[n++] = QueueSlot.Header(section);
        for (int i = 0; i < rows && n < dst.Length; i++) dst[n++] = QueueSlot.Row(section, i);
        if (count > rows && n < dst.Length) dst[n++] = QueueSlot.More(section);
    }
}

/// <summary>The PURE decision behind the whole-list reorder: a flat display <c>(from, to)</c> (the engine's remove-then-
/// insert convention) → a section-local move, a refusal, or nothing. The legal window is <c>start ≤ to ≤ end − 1</c>
/// of the lifted row's own run — the shared boundary is claimed for the row's own section — so a single-row section
/// has exactly one legal slot and every other drop is a TOLD refusal, never a silent landing.</summary>
public static class QueueMovePlan
{
    public static QueueMove For(ReadOnlySpan<QueueSlot> slots, int from, int to)
    {
        if ((uint)from >= (uint)slots.Length || (uint)to >= (uint)slots.Length) return QueueMove.NoOp;
        var lifted = slots[from];
        if (!lifted.IsRow || to == from) return QueueMove.NoOp;

        int start = from, end = from + 1;
        while (start > 0 && IsRowOf(slots[start - 1], lifted.Section)) start--;
        while (end < slots.Length && IsRowOf(slots[end], lifted.Section)) end++;

        if (to < start || to > end - 1) return new QueueMove(QueueMoveKind.Refused, lifted.Section, lifted.Pos, -1);
        int toPos = to - start;
        return toPos == lifted.Pos ? QueueMove.NoOp : new QueueMove(QueueMoveKind.Move, lifted.Section, lifted.Pos, toPos);
    }

    /// <summary>Where a FOREIGN deposit at insertion slot <paramref name="slot"/> lands in the USER QUEUE: the number of
    /// queue rows above that boundary. Anywhere below the user queue appends to it; slot 0 is play-next.</summary>
    public static int InsertIndex(ReadOnlySpan<QueueSlot> slots, int slot)
    {
        int n = 0, limit = Math.Min(slot, slots.Length);
        for (int i = 0; i < limit; i++) if (IsRowOf(slots[i], QueueSection.Queue)) n++;
        return n;
    }

    static bool IsRowOf(in QueueSlot slot, QueueSection section) => slot.IsRow && slot.Section == section;
}

// ── 2. the order rules, pure over spans ────────────────────────────────────────────────────────────────────────────

/// <summary>Every rearrangement of a queue run as pure data over (packed targets, payload) spans — the move, the cursor
/// follow and the skip. Nothing here reads the live queue.</summary>
public static class QueueOrder
{
    /// <summary>Move section row <paramref name="from"/> to <paramref name="to"/> over the section's flat
    /// <paramref name="positions"/> (ascending): remove, then insert at the post-removal index — never a swap, which only
    /// coincides for ±1. Rows outside the section never shift. False for a no-op.</summary>
    public static bool Move(Span<int> targets, Span<QueueEdge> rows, ReadOnlySpan<int> positions, int from, int to)
    {
        int n = positions.Length;
        if (n == 0 || (uint)from >= (uint)n) return false;
        int at = Math.Clamp(to, 0, n - 1);
        if (at == from) return false;
        int movedTarget = targets[positions[from]];
        QueueEdge movedRow = rows[positions[from]];
        if (at > from)
            for (int k = from; k < at; k++) { targets[positions[k]] = targets[positions[k + 1]]; rows[positions[k]] = rows[positions[k + 1]]; }
        else
            for (int k = from; k > at; k--) { targets[positions[k]] = targets[positions[k - 1]]; rows[positions[k]] = rows[positions[k - 1]]; }
        targets[positions[at]] = movedTarget;
        rows[positions[at]] = movedRow;
        return true;
    }

    /// <summary>Put the buckets back around <paramref name="cursor"/>: history before it, NowPlaying on it, then the user's
    /// rows (provider Queue) and then everything else, each in its own order. The cursor's index never moves. True when
    /// anything changed; an out-of-range cursor changes nothing.</summary>
    public static bool Follow(Span<int> targets, Span<QueueEdge> rows, int cursor)
    {
        if ((uint)cursor >= (uint)rows.Length) return false;
        bool changed = false;
        for (int i = 0; i < cursor; i++) changed |= SetBucket(ref rows[i], QueueBucket.History);
        changed |= SetBucket(ref rows[cursor], QueueBucket.NowPlaying);

        int firstOther = -1;
        for (int i = cursor + 1; i < rows.Length; i++)
        {
            if (!IsUser(rows[i]))
            {
                if (firstOther < 0) firstOther = i;
                changed |= SetBucket(ref rows[i], QueueBucket.NextUp);
                continue;
            }
            changed |= SetBucket(ref rows[i], QueueBucket.UserQueue);
            if (firstOther < 0) continue;
            // A queued row behind a continuation row (a retreat put the old deck row back): slide it in front, stably.
            RotateRight(targets, rows, firstOther, i - firstOther + 1);
            firstOther++;
            changed = true;
        }
        return changed;
    }

    /// <summary>A click on upcoming row <paramref name="target"/> of a FOLLOWED list: rearrange in place and answer the
    /// index the deck lands on (−1 when the row is not upcoming). A queued row consumes its queued predecessors. A
    /// continuation or autoplay row does NOT consume the user's queue (0.2.9 <c>SkipToUpcomingIndex</c>): the queued run
    /// moves behind the target, and the continuation rows skipped over become history.</summary>
    public static int Skip(Span<int> targets, Span<QueueEdge> rows, int target)
    {
        if ((uint)target >= (uint)rows.Length) return -1;
        byte bucket = rows[target].Bucket;
        if (bucket != (byte)QueueBucket.UserQueue && bucket != (byte)QueueBucket.NextUp) return -1;
        int at = target;
        if (bucket == (byte)QueueBucket.NextUp && Queue.Range(rows, QueueBucket.UserQueue, out int user, out int users)
            && user < target)
        {
            RotateLeft(targets, rows, user, target - user + 1, users);
            at = target - users;
        }
        Follow(targets, rows, at);
        return at;
    }

    // THE SHUFFLE ORDER IS DELIBERATELY NOT HERE. It landed with the playback host in the same round
    // (`Playback.ShuffleOrder` in Playback.Transitions.cs, applied by Playback.Host.Context.cs's ReorderQueue, which also
    // keeps the context's saved order), and a second copy in this file would be two rules for one gesture. Rehoming it
    // here later is a move, not a rewrite.

    static bool IsUser(in QueueEdge row) => row.Provider == (byte)QueueProvider.Queue || row.Bucket == (byte)QueueBucket.UserQueue;

    static bool SetBucket(ref QueueEdge row, QueueBucket bucket)
    {
        if (row.Bucket == (byte)bucket) return false;
        row = row with { Bucket = (byte)bucket };
        return true;
    }

    /// <summary>The last element of [start, start + length) moves to <paramref name="start"/>; the rest shift up one.</summary>
    static void RotateRight(Span<int> targets, Span<QueueEdge> rows, int start, int length)
    {
        int last = start + length - 1;
        int t = targets[last];
        QueueEdge r = rows[last];
        targets.Slice(start, length - 1).CopyTo(targets.Slice(start + 1));
        rows.Slice(start, length - 1).CopyTo(rows.Slice(start + 1));
        targets[start] = t;
        rows[start] = r;
    }

    /// <summary>Rotate [start, start + length) left by <paramref name="by"/> (three reversals, in place).</summary>
    static void RotateLeft(Span<int> targets, Span<QueueEdge> rows, int start, int length, int by)
    {
        if (length <= 1 || by % length == 0) return;
        by %= length;
        Span<int> t = targets.Slice(start, length);
        Span<QueueEdge> r = rows.Slice(start, length);
        t[..by].Reverse(); r[..by].Reverse();
        t[by..].Reverse(); r[by..].Reverse();
        t.Reverse(); r.Reverse();
    }
}

// NO CONTEXT BUILD HERE EITHER. Loading a context — resolving it, laying out history · now playing · the transfer's
// queue · next up, shuffling it, starting it — is ONE path in the playback host (`Playback.PlayContext` →
// `Playback.Host.Context.cs` StartContext), shared by a local play and Connect's play/transfer. This file owns the
// rows that path writes, not a second way to write them.

// ── 3. the section reads and the session writes ────────────────────────────────────────────────────────────────────

public static partial class Queue
{
    /// <summary>The row the upcoming list starts after: <paramref name="cursorIndex"/> when it names a row (a local
    /// session), else the NowPlaying row (a mirrored remote list, a session nothing has played yet), else −1.</summary>
    public static int Divider(ReadOnlySpan<QueueEdge> rows, int cursorIndex)
        => (uint)cursorIndex < (uint)rows.Length ? cursorIndex
         : Range(rows, QueueBucket.NowPlaying, out int start, out _) ? start : -1;

    /// <summary>A row's section, by provenance: queued → Queue, autoplay → Autoplay, anything else → NextUp.</summary>
    public static QueueSection SectionOf(in QueueEdge row)
        => row.Provider == (byte)QueueProvider.Queue || row.Bucket == (byte)QueueBucket.UserQueue ? QueueSection.Queue
         : row.Provider == (byte)QueueProvider.Autoplay ? QueueSection.Autoplay
         : QueueSection.NextUp;

    /// <summary>Is flat row <paramref name="index"/> upcoming against <paramref name="divider"/>?</summary>
    public static bool IsUpcoming(ReadOnlySpan<QueueEdge> rows, int divider, int index)
        => (uint)index < (uint)rows.Length
           && (divider >= 0 ? index > divider
               : rows[index].Bucket is (byte)QueueBucket.UserQueue or (byte)QueueBucket.NextUp);

    /// <summary>The upcoming rows as flat indices into <paramref name="dst"/>, section by section in list order: [0, user)
    /// the queue, then the continuation, then autoplay. Returns the indices written (capped by the span).</summary>
    public static int Split(ReadOnlySpan<QueueEdge> rows, int divider, Span<int> dst, out int user, out int next, out int autoplay)
    {
        int n = 0;
        user = Collect(rows, divider, QueueSection.Queue, dst, ref n);
        next = Collect(rows, divider, QueueSection.NextUp, dst, ref n);
        autoplay = Collect(rows, divider, QueueSection.Autoplay, dst, ref n);
        return n;
    }

    static int Collect(ReadOnlySpan<QueueEdge> rows, int divider, QueueSection section, Span<int> dst, ref int n)
    {
        int count = 0;
        for (int i = divider < 0 ? 0 : divider + 1; i < rows.Length && n < dst.Length; i++)
        {
            if (!IsUpcoming(rows, divider, i) || SectionOf(rows[i]) != section) continue;
            dst[n++] = i;
            count++;
        }
        return count;
    }

    /// <summary>How many upcoming rows a section holds, and where flat row <paramref name="index"/> sits in its own
    /// section (−1 when it is not upcoming) — the menu's Move up / Move down legality.</summary>
    public static int PositionInSection(ReadOnlySpan<QueueEdge> rows, int divider, int index, out int sectionCount)
    {
        sectionCount = 0;
        if (!IsUpcoming(rows, divider, index)) return -1;
        var section = SectionOf(rows[index]);
        int pos = -1;
        for (int i = divider < 0 ? 0 : divider + 1; i < rows.Length; i++)
        {
            if (!IsUpcoming(rows, divider, i) || SectionOf(rows[i]) != section) continue;
            if (i == index) pos = sectionCount;
            sectionCount++;
        }
        return pos;
    }

    /// <summary>Where a repeat-context WRAP lands: the first row the context itself provided (a consumed queue row or an
    /// autoplay row must not replay), else the first row that is not history.</summary>
    public static int WrapIndex(ReadOnlySpan<QueueEdge> rows)
    {
        for (int i = 0; i < rows.Length; i++) if (rows[i].Provider == (byte)QueueProvider.Context) return i;
        return NextIndex(rows, -1);
    }

    // ── the item-id mint ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Locally minted ids carry the top bit, so they read as a server's 16-hex-character uid when announced and
    /// never collide with the small ids a fixture or a test writes.</summary>
    const ulong LocalItemBit = 1UL << 63;
    static ulong s_mint;

    /// <summary>Reserve <paramref name="count"/> consecutive item ids and answer the first. Never 0 (the "no id" sentinel).</summary>
    public static ulong MintItemIds(int count)
    {
        ulong first = LocalItemBit | (s_mint + 1);
        s_mint += (ulong)Math.Max(1, count);
        return first;
    }

    // ── the scratch run ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A pooled copy of the live run with room for <c>extra</c> more rows. Returned with <see cref="Return"/>.</summary>
    struct Run
    {
        public int[] Targets;
        public QueueEdge[] Rows;
        public int Count;
        public readonly Span<int> T => Targets.AsSpan(0, Count);
        public readonly Span<QueueEdge> R => Rows.AsSpan(0, Count);
    }

    static Run Copy(int extra)
    {
        var packed = PackedRefs;
        var rows = Rows;
        var run = new Run
        {
            Targets = ArrayPool<int>.Shared.Rent(Math.Max(1, packed.Length + extra)),
            Rows = ArrayPool<QueueEdge>.Shared.Rent(Math.Max(1, rows.Length + extra)),
            Count = rows.Length,
        };
        packed.CopyTo(run.Targets);
        rows.CopyTo(run.Rows);
        return run;
    }

    static void Return(in Run run)
    {
        ArrayPool<int>.Shared.Return(run.Targets);
        ArrayPool<QueueEdge>.Shared.Return(run.Rows);
    }

    /// <summary>Land an edited run in one structural write.</summary>
    static void Land(in Run run) => Q.ReplaceRun(Session, run.T, run.R);

    /// <summary>Open a gap of <paramref name="n"/> rows at <paramref name="at"/>.</summary>
    static void Open(ref Run run, int at, int n)
    {
        int tail = run.Count - at;
        if (tail > 0)
        {
            Array.Copy(run.Targets, at, run.Targets, at + n, tail);
            Array.Copy(run.Rows, at, run.Rows, at + n, tail);
        }
        run.Count += n;
    }

    /// <summary>The first row a user row may precede: the first UserQueue/NextUp row, or the end.</summary>
    static int FirstUpcoming(ReadOnlySpan<QueueEdge> rows)
    {
        for (int i = 0; i < rows.Length; i++) if (Rank((QueueBucket)rows[i].Bucket) >= 2) return i;
        return rows.Length;
    }

    static int UserRun(ReadOnlySpan<QueueEdge> rows, int from)
    {
        int n = 0;
        while (from + n < rows.Length && rows[from + n].Bucket == (byte)QueueBucket.UserQueue) n++;
        return n;
    }

    // ── the session writes (UI thread, C1). Each follows `cursor` first. ───────────────────────────────────────────

    /// <summary>Re-bucket the live queue around the reducer's cursor. Idempotent: an already-followed list is not
    /// rewritten and bumps no version.</summary>
    public static bool Follow(in QueueCursor cursor)
    {
        if (cursor.IsNone || (uint)cursor.Index >= (uint)Count) return false;
        var run = Copy(0);
        try
        {
            if (!QueueOrder.Follow(run.T, run.R, cursor.Index)) return false;
            Land(in run);
            return true;
        }
        finally { Return(in run); }
    }

    /// <summary>Insert rows into the USER QUEUE at queue-relative <paramref name="userIndex"/> (clamped; 0 is play-next,
    /// <c>int.MaxValue</c> appends), in the order given. Returns the rows inserted.</summary>
    public static int InsertUser(ReadOnlySpan<EntityRef> refs, in QueueCursor cursor, int userIndex)
    {
        int valid = 0;
        for (int i = 0; i < refs.Length; i++) if (Pack(refs[i]) != 0) valid++;
        if (valid == 0) return 0;
        var run = Copy(valid);
        try
        {
            QueueOrder.Follow(run.T, run.R, cursor.Index);
            int first = FirstUpcoming(run.R);
            int at = first + Math.Clamp(userIndex, 0, UserRun(run.R, first));
            Open(ref run, at, valid);
            ulong id = MintItemIds(valid);
            int w = at;
            for (int i = 0; i < refs.Length; i++)
            {
                int packed = Pack(refs[i]);
                if (packed == 0) continue;
                run.Targets[w] = packed;
                run.Rows[w] = new QueueEdge(id++, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue);
                w++;
            }
            Land(in run);
            return valid;
        }
        finally { Return(in run); }
    }

    /// <summary>"Play next": the head of the user queue.</summary>
    public static int PlayNext(ReadOnlySpan<EntityRef> refs, in QueueCursor cursor) => InsertUser(refs, cursor, 0);

    /// <summary>"Add to queue": after everything already queued.</summary>
    public static int AddToQueue(ReadOnlySpan<EntityRef> refs, in QueueCursor cursor) => InsertUser(refs, cursor, int.MaxValue);

    /// <summary>Take an UPCOMING row out (✕, swipe, the menu). History and the deck's own row are refused: removing one
    /// would shift the reducer's cursor under it.</summary>
    public static bool RemoveUpcoming(int index, in QueueCursor cursor)
    {
        var rows = Rows;
        if (!IsUpcoming(rows, Divider(rows, cursor.Index), index)) return false;
        Q.RemoveAt(Session, index);
        return true;
    }

    /// <summary>Remove by the stable item id.</summary>
    public static bool RemoveItem(ulong itemId, in QueueCursor cursor) => RemoveUpcoming(IndexOfItem(itemId), cursor);

    /// <summary>"Clear": drop every row still waiting in the user queue. Returns how many went.</summary>
    public static int ClearUserQueue(in QueueCursor cursor)
    {
        var run = Copy(0);
        try
        {
            QueueOrder.Follow(run.T, run.R, cursor.Index);
            int first = FirstUpcoming(run.R), n = UserRun(run.R, first);
            if (n == 0) return 0;
            Array.Copy(run.Targets, first + n, run.Targets, first, run.Count - first - n);
            Array.Copy(run.Rows, first + n, run.Rows, first, run.Count - first - n);
            run.Count -= n;
            Land(in run);
            return n;
        }
        finally { Return(in run); }
    }

    /// <summary>The ONE move path behind the drag reorder and the menu's ±1 verbs: section row <paramref name="from"/> to
    /// <paramref name="to"/> (section-relative, remove-then-insert).</summary>
    public static bool MoveInSection(QueueSection section, int from, int to, in QueueCursor cursor)
    {
        var run = Copy(0);
        int[] positions = ArrayPool<int>.Shared.Rent(Math.Max(1, run.Count));
        try
        {
            QueueOrder.Follow(run.T, run.R, cursor.Index);
            int divider = Divider(run.R, cursor.Index), n = 0;
            for (int i = divider < 0 ? 0 : divider + 1; i < run.Count; i++)
                if (IsUpcoming(run.R, divider, i) && SectionOf(run.Rows[i]) == section) positions[n++] = i;
            if (!QueueOrder.Move(run.T, run.R, positions.AsSpan(0, n), from, to)) return false;
            Land(in run);
            return true;
        }
        finally { ArrayPool<int>.Shared.Return(positions); Return(in run); }
    }

    /// <summary>A click on an upcoming row: rearrange (<see cref="QueueOrder.Skip"/>) and answer the cursor the caller
    /// posts with <c>Playback.PlayNow</c>. False when the row is not upcoming.</summary>
    public static bool SkipTo(int index, in QueueCursor current, out QueueCursor cursor)
    {
        cursor = QueueCursor.None;
        var run = Copy(0);
        try
        {
            QueueOrder.Follow(run.T, run.R, current.Index);
            if (!IsUpcoming(run.R, Divider(run.R, current.Index), index)) return false;
            int at = QueueOrder.Skip(run.T, run.R, index);
            if (at < 0) return false;
            Land(in run);
            cursor = CursorOf(at);
            return true;
        }
        finally { Return(in run); }
    }

}
