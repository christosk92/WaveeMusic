// ── Entities/Queue.cs — CORE (owner B, wave 1; plan §2, §4.3, §4.7, ch 21 §7) ────────────────────────────────────────
//
// THE QUEUE IS AN EDGE, and its parent is the playback SESSION — a synthetic subject, not an entity (`Edges.cs`:
// "parent = the playback session subject (slot 1)"). That one decision removes the whole 0.2.9 queue model: no
// `QueueEntry` record per row, no `IReadOnlyList<QueueEntry>` republished on every state push, no re-derivation of the
// bucket split per render. A queue row is a cross-kind row POINTER plus a <see cref="QueueEdge"/> payload — the
// server's own 64-bit item id, its provider and its bucket — in one CSR run the reorder path can splice.
//
//   Edges.Queue[Session] :  [ history … ] [ now-playing ] [ user queue … ] [ next up … ]
//                            ▲ rank 0       ▲ rank 1        ▲ rank 2         ▲ rank 3
//                            already played  the row the     "Next in queue"  "Next from <context>"
//                                            deck is on      (user-inserted)  (context continuation)
//
// THE LIST IS STORED IN READING ORDER, buckets contiguous and ascending by <see cref="Queue.Rank"/>. Two things fall
// out of that and both are load-bearing: "what plays next" is a FORWARD SCAN from the cursor (no bucket arithmetic, no
// three lists to merge), and a bucket's rows are a SLICE — <see cref="Queue.Range"/> is two integers, so the rail's
// "Next in queue" section is a span and not a filtered copy. The invariant is asserted on every whole-list write
// rather than trusted (see <see cref="Queue.Replace(ReadOnlySpan{int},ReadOnlySpan{QueueEdge})"/>).
//
// ┌─ A QUEUE ROW IS CROSS-KIND, AND THE TARGET CARRIES THE KIND (defect 5 of the identity investigation, ────────────┐
// │  docs/plans/wavee/wavee-0.3-entity-identity-memory.md §3.1 requirement 4)                                        │
// │                                                                                                                  │
// │  A CSR target is an `int`, and an `int` is only an identity WITH a table. Every same-kind relation gets that     │
// │  table from the relation itself (an `AlbumTracks` target is a track); the queue does not. It mixes TRACKS and    │
// │  EPISODES — and 0.2.9 "solved" that by making an episode ride as a Podcast-flagged TRACK row (`Track.cs:89-93`), │
// │  which is why the doc lists the queue among the four places cross-kind identity was unresolved. Worse: track     │
// │  slot 5 and episode slot 5 are the SAME `int`, so `Contains`/`IndexOf`/`Insert`/`Settle` — all keyed on the      │
// │  target — would confuse two different rows for one.                                                             │
// │                                                                                                                  │
// │  So the queue's target is not a slot: it is an `EntityRef` (kind, slot) PACKED into the int, the kind (0-127)   │
// │  above 24 bits of slot (<see cref="Queue.Pack"/> / <see cref="Queue.Unpack"/>). The whole `EdgeTable` machinery  │
// │  then keys on the FULL identity for free: no second column to keep in step, no payload change, no ambiguity, and │
// │  0 stays "none" (kind Unknown, slot 0) exactly as P3 requires. 24 bits is 16.7 M rows in one table — a track     │
// │  table that large is 1.4 GB of columns, so the cap is not a limit, and `Pack` refuses rather than aliases.       │
// │                                                                                                                  │
// │  WHY NOT A `Kind` BYTE ON `QueueEdge`: that is the shape this file would prefer, and it is the one-word change   │
// │  `Edges.cs` should make. `Edges.cs` is another owner's file in this wave — REPORTED, and packing is the          │
// │  work-around that needs nothing from anyone. If the payload ever grows the byte, `Pack`/`Unpack` collapse to     │
// │  `row.Slot` / `new EntityRef(edge.Kind, target)` and NOTHING else in this file moves.                            │
// └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
//
// THE CURSOR IS A VALUE, NOT A POINTER (§4.7). `Playback.State` carries a <see cref="QueueCursor"/>, `Step` advances it
// synchronously, and ten `Next` clicks in one drain are ten cursor moves and ONE Load effect (C3/C4). It is a
// (bucket, index) pair rather than a row pointer because the same recording can legitimately sit in the queue twice and
// "the row I am on" must survive that; it is re-validated against the list, never dereferenced blindly.
//
// EVERY RULE HERE IS A PURE FUNCTION OVER A `ReadOnlySpan<QueueEdge>`, with a session-bound one-liner over it. That is
// not test scaffolding — it is what lets the reducer reason about a queue it has been handed (a cluster delta being
// folded, a reorder being previewed) without that queue having to be the live one first.
//
// NO TEXT IS OWNED HERE. `QueueEdge` carries no `StringId` and this file declares no table, so the ref-counting
// discipline (defect 1, doc §4.4) has nothing to bind to — the rows' titles belong to the Track / Episode tables, which
// release them in their own `ReleaseText`.
//
// What is NOT here, deliberately: `QueueSlots`, `QueueMovePlan` and `QueueOrder` — ch 21 §8's three ported rule sets,
// owner Q's, Wave 5, in this same file. Wave 1 owns the shape they will read.
//
// Rules: single writer, UI thread (C1); no allocation on any path here (P8); no LINQ, no closures (P9).

using System.Buffers;

namespace Wavee;

// ── 1. the small enums the payload encodes ───────────────────────────────────────────────────────────────────────────

/// <summary>Which section of the queue a row belongs to. Ported member-for-member from 0.2.9's <c>QueueBucket</c> so a
/// side-by-side reviewer sees the same four names; the STORAGE order is <see cref="Queue.Rank"/>'s, which is a
/// different question and is why the two are separate.</summary>
public enum QueueBucket : byte
{
    /// <summary>The row the deck is on. Exactly one, when the session has anything at all.</summary>
    NowPlaying = 0,
    /// <summary>User-inserted rows — "Next in queue". Consumed before the context continues.</summary>
    UserQueue = 1,
    /// <summary>The context's own continuation — "Next from &lt;playlist&gt;".</summary>
    NextUp = 2,
    /// <summary>Already played. Kept so Previous has somewhere to go, and never advanced INTO.</summary>
    History = 3,
}

/// <summary>Where a queue row came from on the wire — the Connect protocol's own "context" / "queue" / "autoplay"
/// tokens. Distinct from <see cref="QueueBucket"/>: an autoplay-station row is <c>NextUp</c> by position and
/// <c>Autoplay</c> by provenance, and the rail renders the provenance. 0.2.9 carried both a <c>Provider</c> enum and a
/// redundant <c>IsAutoplay</c> bool; here that bool is <c>Provider == Autoplay</c> and cannot disagree with itself.</summary>
public enum QueueProvider : byte { Context = 0, Queue = 1, Autoplay = 2 }

/// <summary>WHERE IN THE QUEUE the session is (plan §4.7: "cursor = (bucket, index) into Edges.Queue"). A value, so
/// `Playback.State` stays copyable and `Step` stays pure.
///
/// <para><c>default</c> is the head of the list — a sensible fresh session — and <see cref="None"/> is the honest
/// "nothing is playing". They are different states and the reducer must not confuse them, which is why
/// <see cref="IsNone"/> exists rather than a magic index-0 test.</para></summary>
public readonly record struct QueueCursor(QueueBucket Bucket, int Index)
{
    /// <summary>No position at all: an idle session, or a queue emptied under us.</summary>
    public static QueueCursor None => new(QueueBucket.NowPlaying, -1);

    public bool IsNone => Index < 0;
}

// ── 2. the queue ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The playback queue: the pure rules, the session-bound reads, the cursor walk and the optimistic writes. A
/// static class and not a handle because there is exactly ONE queue — the session's — and inventing a handle over a
/// constant slot would only invite a second one.</summary>
public static partial class Queue
{
    /// <summary>The synthetic parent every queue edge hangs off. Slot 0 is "none" in every table and arena
    /// (<c>Entities.cs</c>), so the session is 1.</summary>
    public const int Session = 1;

    static EdgeTable<QueueEdge> Q => Entities.Current.Edges.Queue;

    // ── the cross-kind target (see the file header's box) ───────────────────────────────────────────────────────────

    /// <summary>Bits of the packed target that hold the slot; the remaining 7 hold the <see cref="EntityKind"/>.</summary>
    const int SlotBits = 24;
    const int SlotMask = (1 << SlotBits) - 1;
    /// <summary>Kinds must stay below this, or the shift would reach the target's SIGN bit and a packed row would read
    /// back as "none". <see cref="EntityKind"/> has ten members; the guard in <see cref="Pack"/> is what keeps the
    /// eleventh from being a silent one.</summary>
    const int MaxKind = 127;

    /// <summary>The largest slot a queue target can address. 16,777,215 rows in one table is 1.4 GB of columns, so
    /// this is a guard against a corrupt caller, not a capacity the app can reach.</summary>
    public const int MaxSlot = SlotMask;

    /// <summary>A cross-kind row pointer as ONE int, which is what a CSR target is. Answers 0 — "none", the value a
    /// cleared arena hole already reads as (P3) — for a kindless ref, slot 0, a slot past <see cref="MaxSlot"/> or a
    /// kind past <see cref="MaxKind"/>: refusing is the point, because aliasing two entities onto one queue row is the
    /// defect this exists to prevent.</summary>
    public static int Pack(EntityRef row)
        => row.IsNone || (uint)row.Slot > SlotMask || (uint)row.Kind > MaxKind
            ? 0
            : ((int)row.Kind << SlotBits) | row.Slot;

    /// <summary>The inverse of <see cref="Pack"/>: a packed target back to (kind, slot). A non-positive target — 0 is
    /// a cleared arena hole — is <see cref="EntityRef.IsNone"/>.</summary>
    public static EntityRef Unpack(int target)
        => target <= 0 ? default : new EntityRef((EntityKind)(target >> SlotBits), target & SlotMask);

    /// <summary>Pack a whole batch into a caller-owned buffer — what a cluster decode hands
    /// <see cref="Replace(ReadOnlySpan{int},ReadOnlySpan{QueueEdge})"/>. Returns how many were written (the shorter of
    /// the two spans); allocates nothing.</summary>
    public static int Pack(ReadOnlySpan<EntityRef> rows, Span<int> dst)
    {
        int n = rows.Length < dst.Length ? rows.Length : dst.Length;
        for (int i = 0; i < n; i++) dst[i] = Pack(rows[i]);
        return n;
    }

    // ── the pure rules ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The STORAGE rank of a bucket — the order rows are laid out in, which is reading order and not the
    /// enum's declaration order. History first, so a forward walk from the cursor never re-enters it.</summary>
    public static int Rank(QueueBucket bucket) => bucket switch
    {
        QueueBucket.History => 0,
        QueueBucket.NowPlaying => 1,
        QueueBucket.UserQueue => 2,
        _ => 3,
    };

    /// <summary>Are these rows laid out in ascending <see cref="Rank"/> order, buckets contiguous? The invariant every
    /// other rule here depends on. Public because the decoder's own tests assert it on their output, which is far
    /// cheaper than debugging a queue that plays the wrong song later.</summary>
    public static bool IsOrdered(ReadOnlySpan<QueueEdge> rows)
    {
        int rank = -1;
        for (int i = 0; i < rows.Length; i++)
        {
            int r = Rank((QueueBucket)rows[i].Bucket);
            if (r < rank) return false;
            rank = r;
        }
        return true;
    }

    /// <summary>The contiguous run one bucket occupies. False — with a zero-length range — when the bucket has no
    /// rows, which is exactly the answer the rail wants: a section with no rows contributes NOTHING, header included
    /// (ch 21 §8's <c>QueueSlots</c> rule, which Wave 5 builds on this).</summary>
    public static bool Range(ReadOnlySpan<QueueEdge> rows, QueueBucket bucket, out int start, out int length)
    {
        start = 0;
        length = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Bucket != (byte)bucket) { if (length > 0) break; continue; }
            if (length == 0) start = i;
            length++;
        }
        return length > 0;
    }

    /// <summary>The rail's "Next up" run: the user queue and the context continuation together, adjacent by
    /// construction (ranks 2 and 3) and therefore ONE slice rather than a filtered copy (ch 21 §7).</summary>
    public static bool UpNext(ReadOnlySpan<QueueEdge> rows, out int start, out int length)
    {
        bool user = Range(rows, QueueBucket.UserQueue, out int userStart, out int userLength);
        bool next = Range(rows, QueueBucket.NextUp, out int nextStart, out int nextLength);
        if (!user && !next) { start = 0; length = 0; return false; }
        start = user ? userStart : nextStart;
        length = userLength + nextLength;
        return true;
    }

    /// <summary>The first index after <paramref name="index"/> that is not history, or -1. Pass -1 to start at the
    /// head. THE forward walk: <see cref="TryAdvance"/> and <see cref="TryPeek"/> are both this plus an unpack.</summary>
    public static int NextIndex(ReadOnlySpan<QueueEdge> rows, int index)
    {
        for (int i = index < 0 ? 0 : index + 1; i < rows.Length; i++)
            if (rows[i].Bucket != (byte)QueueBucket.History) return i;
        return -1;
    }

    /// <summary>The index before <paramref name="index"/>, or -1. History is NOT skipped here — walking back into it is
    /// the whole point of Previous. Pass a negative index to mean "from the end".</summary>
    public static int PrevIndex(ReadOnlySpan<QueueEdge> rows, int index)
    {
        int from = index < 0 ? rows.Length : index;
        return from - 1 < 0 ? -1 : from - 1;
    }

    /// <summary>Where a server-minted queue item sits, or -1. THE stable identity across a reorder — 0.2.9's
    /// <c>QueueItemId</c>, never index-derived and never reused. A linear scan on purpose: the queue is tens of rows,
    /// and an index over it would have to be invalidated by every splice.</summary>
    public static int IndexOfItem(ReadOnlySpan<QueueEdge> rows, ulong itemId)
    {
        if (itemId == 0) return -1;
        for (int i = 0; i < rows.Length; i++) if (rows[i].ItemId == itemId) return i;
        return -1;
    }

    /// <summary>Where a new "Add to queue" row belongs: after the last user-queued row, else straight after the row
    /// that is playing, else at the end. Appending — rather than prepending — is what makes queueing three tracks play
    /// them in the order they were clicked.</summary>
    public static int EnqueueIndex(ReadOnlySpan<QueueEdge> rows)
    {
        if (Range(rows, QueueBucket.UserQueue, out int start, out int length)) return start + length;
        if (Range(rows, QueueBucket.NowPlaying, out int nowStart, out int nowLength)) return nowStart + nowLength;
        return rows.Length;
    }

    // ── session-bound reads ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The queue's rows as PACKED cross-kind pointers, in reading order — the raw CSR targets. Read them
    /// through <see cref="RefAt(int)"/> unless you are re-writing the list; a packed target is not a slot and must
    /// never be indexed into a table. Read the span rule in <c>Edges.cs</c>'s header before you keep it.</summary>
    public static ReadOnlySpan<int> PackedRefs => Q.Targets(Session);
    /// <summary>The per-row payload (item id, provider, bucket), parallel to <see cref="PackedRefs"/>.</summary>
    public static ReadOnlySpan<QueueEdge> Rows => Q.Payload(Session);
    /// <summary>The optimistic state of each row (C6): an unconfirmed insert spins, a pending remove greys.</summary>
    public static ReadOnlySpan<byte> Pending => Q.Pending(Session);

    public static int Count => Q.Count(Session);
    /// <summary>Unknown until a cluster or a local play has said otherwise. The rail renders a skeleton for Unknown and
    /// the real empty state only for Complete — the distinction 0.2.9 had to infer from a null list (ch 21 §7).</summary>
    public static EdgeState State => Q.State(Session);
    /// <summary>Bumps on every structural change; the number the rail's bound list compares.</summary>
    public static uint Version => Q.Version(Session);

    /// <summary>The row at a flat index, as (kind, slot). <see cref="EntityRef.IsNone"/> for an index that is not a
    /// row — which is the honest answer for a cursor that survived a rewrite.</summary>
    public static EntityRef RefAt(int index)
    {
        var packed = PackedRefs;
        return (uint)index >= (uint)packed.Length ? default : Unpack(packed[index]);
    }

    /// <inheritdoc cref="Range(ReadOnlySpan{QueueEdge},QueueBucket,out int,out int)"/>
    public static bool Range(QueueBucket bucket, out int start, out int length) => Range(Rows, bucket, out start, out length);
    /// <inheritdoc cref="UpNext(ReadOnlySpan{QueueEdge},out int,out int)"/>
    public static bool UpNext(out int start, out int length) => UpNext(Rows, out start, out length);
    /// <inheritdoc cref="IndexOfItem(ReadOnlySpan{QueueEdge},ulong)"/>
    public static int IndexOfItem(ulong itemId) => IndexOfItem(Rows, itemId);

    /// <summary>The cursor for a flat index, its bucket filled in from the row. <see cref="QueueCursor.None"/> when the
    /// index is not a row.</summary>
    public static QueueCursor CursorOf(int index)
    {
        var rows = Rows;
        return (uint)index >= (uint)rows.Length ? QueueCursor.None : new QueueCursor((QueueBucket)rows[index].Bucket, index);
    }

    /// <summary>The row the cursor is on, as (kind, slot). Re-validated against the list every time: a cursor that
    /// survived a rewrite may point past the end, and <see cref="EntityRef.IsNone"/> is the honest answer for that.</summary>
    public static EntityRef RefAt(in QueueCursor cursor) => RefAt(cursor.Index);

    /// <summary>The row's slot alone, or <see cref="Table.None"/> — for a caller that already knows the kind (the deck
    /// binding a track row). Prefer <see cref="RefAt(in QueueCursor)"/>: a queue mixes kinds.</summary>
    public static int SlotAt(in QueueCursor cursor) => RefAt(cursor.Index).Slot;

    // ── the cursor walk (§4.7) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What plays after the cursor, WITHOUT moving it — the reducer's <c>PrepareNext</c> (§4.7).</summary>
    public static bool TryPeek(in QueueCursor cursor, out EntityRef row)
    {
        int next = NextIndex(Rows, cursor.Index);
        row = next < 0 ? default : RefAt(next);
        return next >= 0;
    }

    /// <summary>Move the cursor to the next playable row. False at the end of the queue, with the cursor left where it
    /// was — the reducer turns that into <c>Phase.Ended</c>, never into a silent wrap (§4.7).</summary>
    public static bool TryAdvance(ref QueueCursor cursor, out EntityRef row)
    {
        int next = NextIndex(Rows, cursor.Index);
        if (next < 0) { row = default; return false; }
        cursor = CursorOf(next);
        row = RefAt(next);
        return true;
    }

    /// <summary>Move the cursor BACK one row — the Previous transport, which walks into history because that is what
    /// history is for.</summary>
    public static bool TryRetreat(ref QueueCursor cursor, out EntityRef row)
    {
        int prev = PrevIndex(Rows, cursor.Index);
        if (prev < 0) { row = default; return false; }
        cursor = CursorOf(prev);
        row = RefAt(prev);
        return true;
    }

    /// <summary>The row the cursor is on; an invalid cursor answers <see cref="EntityRef.IsNone"/> — a real value with
    /// nothing pointed at (P3), never a null.</summary>
    public static EntityRef Current(in QueueCursor cursor) => RefAt(cursor.Index);

    // ── writes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Land the whole queue from a provider answer (a cluster push, a local context load) in one copy, from
    /// targets that are ALREADY packed (<see cref="Pack(ReadOnlySpan{EntityRef},Span{int})"/>). The bucket-order
    /// invariant is CHECKED here rather than assumed: a decoder that hands rows over in wire order would otherwise
    /// break <see cref="Range"/> and the cursor walk in a way that only surfaces as the wrong song.</summary>
    public static void Replace(ReadOnlySpan<int> packedRefs, ReadOnlySpan<QueueEdge> rows)
    {
        System.Diagnostics.Debug.Assert(IsOrdered(rows),
            "queue rows must arrive in reading order: history, now-playing, user queue, next up");
        Q.ReplaceRun(Session, packedRefs, rows);
    }

    /// <summary>The same, from cross-kind row pointers. Packs through a POOLED buffer rather than a new array: a
    /// cluster push is tens of rows and arrives on a wire answer, so this allocates nothing after warm-up (P8).</summary>
    public static void Replace(ReadOnlySpan<EntityRef> refs, ReadOnlySpan<QueueEdge> rows)
    {
        int n = refs.Length;
        if (n == 0) { Replace(default(ReadOnlySpan<int>), rows); return; }
        int[] rented = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Pack(refs, rented);
            Replace(rented.AsSpan(0, n), rows);
        }
        finally { ArrayPool<int>.Shared.Return(rented); }
    }

    /// <summary>"Add to queue": splice a row in at <see cref="EnqueueIndex"/>, marked <see cref="EdgePending.Add"/> so
    /// it renders the instant it is clicked while the shell sends it (C6). A ref the queue already holds is updated in
    /// place — a double-click must not add a second row — and because the target carries the KIND, "already holds it"
    /// now means the same ENTITY and not merely the same slot number in some other table.</summary>
    public static void Enqueue(EntityRef row, ulong itemId = 0)
    {
        int packed = Pack(row);
        if (packed == 0) return;
        Q.Insert(Session, packed, new QueueEdge(itemId, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue),
            EnqueueIndex(Rows), EdgePending.Add);
    }

    /// <summary>Take a row out optimistically: it STAYS, greyed, until <see cref="Settle"/> (C6). Addressed by flat
    /// index because that is what a drag or a row menu has, and because the same entity may be queued twice.</summary>
    public static bool MarkRemoveAt(int index)
    {
        var packed = PackedRefs;
        return (uint)index < (uint)packed.Length && Q.MarkRemove(Session, packed[index]);
    }

    /// <summary>The server answered about an optimistic queue write (C6).</summary>
    public static bool Settle(EntityRef row, bool ok)
    {
        int packed = Pack(row);
        return packed != 0 && Q.Settle(Session, packed, ok);
    }

    /// <summary>Forget the queue: back to <see cref="EdgeState.Unknown"/>, which renders as a skeleton and not as "the
    /// queue is empty". What a device transfer does before the new owner's cluster lands (C7).</summary>
    public static void Clear() => Q.Clear(Session);
}
