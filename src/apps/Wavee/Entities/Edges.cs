// ── Entities/Edges.cs — CORE (owner B, wave 1, budget 980; plan §2, §4.3, §9.6 Q4/Q5) ────────────────────────────────
//
// EVERY RELATIONSHIP IN THE APP, as CSR (compressed sparse row) tables: a parent slot indexes a contiguous run of child
// slots, with one unmanaged payload per edge. Album → tracks, track → artists, artist → releases, user → liked (the
// library IS edges, G6), the queue, the rootlist, the friends feed, a home section's cards. One shape, one set of
// invariants, one place where paging, membership and optimistic writes are implemented.
//
// WHY CSR AND NOT `List<T>` PER PARENT (design record §5.11 cost 2): a `List<int>` per parent is one GC object per
// parent plus one per growth; 4,000 playlists is 4,000+ live objects that a gen2 has to walk. CSR is FOUR arrays for
// the whole relation whatever the parent count — the GC sees four objects, and a parent's children are contiguous, so
// walking them is a linear read the prefetcher likes (P1, P2, P5).
//
//   Start[p] ─┐                         Targets:  [ .. | c0 c1 c2 c3 | .. ]      Length[p] = 4
//             └──────────────────────▶            ▲                 ▲            Capacity[p] ≥ 4 (slack for Insert)
//                                                 └── Start[p] ─────┘
//   Payload runs parallel to Targets, one TEdge per edge. Pending runs parallel too (see below).
//
// ┌─ THE SPAN RULE (plan §7's risk row) ───────────────────────────────────────────────────────────────────────────┐
// │ A CALLER NEVER HOLDS A `Targets(p)` / `Payload(p)` / `Pending(p)` SPAN ACROSS A UI DRAIN. They are slices into  │
// │ the shared arena, and `Replace`, `ReplacePage`, `Insert` and `Compact` all rewrite or MOVE a parent's range —   │
// │ a span captured before a drain can point at another parent's edges after it. Read it, use it inside the frame, │
// │ drop it. What survives a drain is (parent, index) or the parent's `Version(p)`, which is exactly what a bound   │
// │ list compares.                                                                                                  │
// └─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
//
// PENDING BITS LIVE ON THE TABLE, NOT IN THE PAYLOAD (a deliberate change from §4.3's sketch, which put "1 = pending
// add, 2 = pending remove" inside `PlaylistTrackEdge.Flags` and `LibraryEdge.Flags`). Optimism is a property of the
// EDGE, not of the wire shape carried on it: liking a track, following an artist, saving an album, adding to a queue
// and reordering a playlist are the same C6 dance, and a generic `EdgeTable<TEdge>` cannot reach into an arbitrary
// payload to flip a flag without an interface constraint every payload — including the engine's own `StringId` — would
// have to implement. So the table owns a parallel `byte` per edge (`EdgePending`) and `Insert`/`MarkRemove`/`Settle`
// are payload-agnostic. The payload `Flags` fields stay for WIRE flags.
//
// EDGE-OWNED TEXT IS REF-COUNTED BY ITS OWNER, and this table cannot do it for you. A `StringId` in a payload
// (`TrackTags`, `RootlistEdge.FolderName`, `PlaylistTrackEdge.ItemId`, every `Merch` field) is an interned string the
// engine reclaims only when its last reference is released, and one that was never AddRef'd is PERMANENT
// (the engine's `StringTable.cs:26`, in the sibling fluent-gpu checkout; the leak the identity investigation found,
// docs/plans/wavee/wavee-0.3-entity-identity-memory.md §4.4). `EdgeTable<TEdge>` is generic over an unmanaged payload
// it cannot reach into — the same reason the pending bits sit beside the payload rather than in it — so the rule is on
// the WRITER: build the payload with `Entities.RetainText(ref edge.FolderName, id)` and give it back with
// `Entities.ReleaseText(ref edge.FolderName)` when the row is replaced or the scope is retired. A `Table` column has
// `SetText`/`ClearText` for exactly this; an edge payload has the two primitives and its owner's discipline.
//
// MERCH LIVES HERE, not in Entities.cs (§9.6 Q4, ch 05 D8): it is not an entity — no uri, no fetch ladder, nothing
// links to it — it is a side table that exactly one edge points into, so it belongs beside that edge. See MerchTable.
//
// Rules: single writer, UI thread (C1); a structural write bumps the parent's `Version` and marks the table dirty
// exactly once per drain (D8, C3); no LINQ, no closures, no allocation on the read paths (P8, P9).

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. edge state and pending bits ───────────────────────────────────────────────────────────────────────────────────

/// <summary>How complete a parent's child list is (D7's answer: "partial" IS first-class, because a 5,000-track
/// playlist arrives as pages and the page has to render the first one honestly).</summary>
public enum EdgeState : byte
{
    /// <summary>Nobody has answered for this parent. Renders as a skeleton, never as "empty".</summary>
    Unknown = 0,
    /// <summary>Some pages have arrived; <see cref="EdgeTable{TEdge}.Total"/> says how many there will be.</summary>
    Partial = 1,
    /// <summary>Every child is here. An empty complete list is a real, renderable "this playlist has no tracks".</summary>
    Complete = 2,
    /// <summary>Nobody has answered AND the last ask failed (G-050): the skeleton must become a Retry vacancy, not stay a
    /// skeleton forever. <b>Never stored and never persisted</b> — <see cref="EdgeTable{TEdge}.State"/> cannot answer
    /// it; only <see cref="EdgeTableBase.Readiness"/> does, and only while the list is still Unknown. A list that has
    /// rows keeps rendering them after a failed refresh (the failure stays readable through
    /// <see cref="EdgeTableBase.FailureOf"/>).</summary>
    Failed = 3,
}

/// <summary>Optimistic-write state for ONE edge (C6). The UI shows the flip immediately, the shell PUTs, and
/// <see cref="EdgeTable{TEdge}.Settle"/> either clears the bit or reverts the edge. Never replayed automatically.</summary>
[Flags]
public enum EdgePending : byte
{
    None = 0,
    /// <summary>Added locally, not yet confirmed. A rejection removes the edge.</summary>
    Add = 1,
    /// <summary>Removed locally, not yet confirmed — the row is still present (greyed / undoable) so the list does not
    /// jump before the server agrees. A rejection restores it.</summary>
    Remove = 2,
}

// ── 2. the fetch marks: what the edge door remembers per parent (G-042, G-050) ───────────────────────────────────────

/// <summary>The payload-agnostic half of a relation: the two per-parent marks the edge door plans with, and the
/// readiness a surface renders. A base class and not an interface for the same reason <see cref="Publishable"/> is one
/// (P9): the planner holds relations of eleven payload types through one reference, with no boxing and no generic
/// dispatch (<c>Fetch.Edges.cs</c>).
///
/// <para><b>ASKED</b> is "the highest page offset anybody asked for, plus one" (0 = never). Pages are asked in order —
/// the first, then <c>Count</c> — so one int is the whole memory, and "has page <c>o</c> been asked" is
/// <c>asked &gt; o</c>. It is the edge twin of <see cref="Table.Asked"/>, and it is what stops a sidebar that rebuilds
/// every frame from re-reading the rootlist every frame.</para>
///
/// <para><b>FAILURE</b> is the status of the last ask that did not land (0 = none). A terminal failure un-asks the page
/// (so the next mount really retries) AND records the status (so the mount that is showing right now can say so):
/// 0.2.9's Loadable carried both halves, and the V3 error banner, the Retry vacancy and parity item 73 read them.</para>
///
/// <para>Single writer, UI thread (C1). A mark is not a structural write: it bumps no version — but a failure DOES mark
/// the relation dirty, because <see cref="Readiness"/> changed and a bound surface has to re-read it.</para></summary>
public abstract class EdgeTableBase : Publishable
{
    Column<int> _asked;
    Column<int> _failure;          // status + FailureBias; 0 = none (a zeroed column reads as "never failed")
    int _marked;

    /// <summary>The stored failure is <c>status + 2</c>, so the three non-HTTP codes below survive a zeroed column.</summary>
    const int FailureBias = 2;

    /// <summary>The provider had no route for this relation and answered with nothing.</summary>
    public const int NoRoute = -1;
    /// <summary>The request never reached a server (DNS, socket, timeout) — HTTP status 0.</summary>
    public const int Transport = 0;

    /// <inheritdoc cref="EdgeTable{TEdge}.State"/>
    public abstract EdgeState State(int parent);

    /// <inheritdoc cref="EdgeTable{TEdge}.Count"/>
    public abstract int Count(int parent);

    /// <summary>THE state a surface renders (G-050): <see cref="State"/>, except that an Unknown list whose last ask
    /// failed reads <see cref="EdgeState.Failed"/>. Everything that is not a surface — the store, the planner — reads
    /// <see cref="State"/>, which never answers Failed.</summary>
    public EdgeState Readiness(int parent)
    {
        var state = State(parent);
        return state == EdgeState.Unknown && IsFailed(parent) ? EdgeState.Failed : state;
    }

    /// <summary>Did the last ask for this parent fail without landing?</summary>
    public bool IsFailed(int parent) => (uint)parent < (uint)_marked && _failure[parent] != 0;

    /// <summary>The status of that failure: an HTTP status, <see cref="Transport"/> (0) or <see cref="NoRoute"/>.
    /// Meaningful only when <see cref="IsFailed"/>; reads <see cref="Transport"/> otherwise.</summary>
    public int FailureOf(int parent) => IsFailed(parent) ? _failure[parent] - FailureBias : Transport;

    /// <summary>Has the page at <paramref name="offset"/> (or a later one) been asked for this parent, this scope?</summary>
    public bool WasAsked(int parent, int offset) => (uint)parent < (uint)_marked && _asked[parent] > Math.Max(0, offset);

    /// <summary>The planner asked for the page at <paramref name="offset"/>. Clears a recorded failure: a new ask is a new
    /// attempt, and the surface goes back to "loading".</summary>
    public void MarkAsked(int parent, int offset)
    {
        if (parent < 0) return;
        EnsureMarks(parent);
        int page = Math.Max(0, offset) + 1;
        if (_asked[parent] < page) _asked[parent] = page;
        if (_failure[parent] != 0) { _failure[parent] = 0; MarkDirty(); }
    }

    /// <summary>The ask for the page at <paramref name="offset"/> did not land: un-ask it (the next mount retries) and
    /// record why (the surface showing now can say so).</summary>
    public void MarkFailed(int parent, int offset, int status)
    {
        if (parent < 0) return;
        EnsureMarks(parent);
        int page = Math.Max(0, offset);
        if (_asked[parent] > page) _asked[parent] = page;
        _failure[parent] = status + FailureBias;
        MarkDirty();
    }

    /// <summary>An answer came back for this parent WITHOUT its list — the route had nothing, or nobody owns the
    /// parent. Recorded as <see cref="NoRoute"/> and the ask is KEPT: the planner does not ask again this scope (the
    /// row twin is a group that stays asked), and the surface renders a vacancy rather than a skeleton forever.</summary>
    public void MarkUnanswered(int parent, int offset)
    {
        if (parent < 0) return;
        EnsureMarks(parent);
        int page = Math.Max(0, offset) + 1;
        if (_asked[parent] < page) _asked[parent] = page;
        _failure[parent] = NoRoute + FailureBias;
        MarkDirty();
    }

    /// <summary>An answer landed for this parent: whatever failed before did not stay failed.</summary>
    public void MarkAnswered(int parent)
    {
        if ((uint)parent >= (uint)_marked || _failure[parent] == 0) return;
        _failure[parent] = 0;
        MarkDirty();
    }

    /// <summary>Forget every ask for this parent — a refresh (a dealer push, a pull-to-refresh, the login sync). The
    /// list itself is untouched: stale rows keep rendering until the new answer replaces them.</summary>
    public void ForgetAsked(int parent)
    {
        if ((uint)parent >= (uint)_marked) return;
        _asked[parent] = 0;
    }

    void EnsureMarks(int parent)
    {
        if (parent < _marked) return;
        int capacity = Math.Max(parent + 1, Math.Max(16, _marked * 2));
        _asked.EnsureCapacity(capacity);
        _failure.EnsureCapacity(capacity);
        _marked = parent + 1;
    }
}

// ── 3. the CSR table ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One relation: parent slot → an ordered run of child slots, each with an unmanaged
/// <typeparamref name="TEdge"/> payload. See the file header for the layout, the span rule and the pending bits.
///
/// <para><b>Parents are plain <c>int</c>s and need no table.</b> Usually a parent is an entity slot (album → tracks),
/// but a synthetic subject works exactly as well: the queue's parent is the playback session (1), a home section's
/// parent is a slot <c>Home.cs</c> mints, the friends feed's parent is one synthetic subject (ch 21 G6). The per-parent
/// columns grow on demand, so any small int is a legal parent.</para></summary>
public sealed class EdgeTable<TEdge> : EdgeTableBase where TEdge : unmanaged
{
    // per parent
    // `_total` is what the SERVER said the total is; 0 = it has not said (see ReplacePage). The honest extent a
    // scrollbar reads — never below the present count — is derived in Total(), not stored: one column, not two.
    Column<int> _start, _length, _capacity, _total;
    Column<uint> _version;
    Column<byte> _state;
    int _parents;                      // parents with per-parent columns initialised

    // the arena, parallel: one entry per edge
    Column<int> _targets;
    Column<TEdge> _payload;
    Column<byte> _pending;
    int _tail;                         // arena high-water mark
    int _dead;                         // arena entries abandoned by a regrow, reclaimable by Compact()

    // Lazily built membership index, keyed (parent, target). Built on the first Contains() over a list long enough to
    // be worth it, then kept COHERENT by every mutation (each one knows exactly which pairs it changed), so a like /
    // unlike storm never triggers a rebuild. Tables nobody asks membership of never pay for it at all.
    HashSet<long>? _index;

    /// <summary>A list shorter than this is scanned linearly instead of indexed: `IndexOf` over a handful of ints is a
    /// single vectorized compare in the BCL, and a hash set for a three-element list is pure overhead.</summary>
    const int IndexFloor = 32;

    /// <summary>Parents with storage allocated. Not "parents with edges" — the store's walk skips empty ones.</summary>
    public int ParentCount => _parents;
    /// <summary>Arena entries in use, live and abandoned — the diagnostics page's fragmentation read.</summary>
    public int ArenaUsed => _tail;
    /// <summary>Arena entries abandoned by regrows, reclaimable by <see cref="Compact"/>.</summary>
    public int ArenaDead => _dead;
    /// <summary>True once the membership index exists (diagnostics; it is an allocation worth being able to see).</summary>
    public bool Indexed => _index is not null;

    // ── reads ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The parent's children, in order. Read the span rule in the file header before you keep it.</summary>
    public ReadOnlySpan<int> Targets(int parent)
        => (uint)parent >= (uint)_parents ? default : _targets.Span.Slice(_start[parent], _length[parent]);

    /// <summary>The payload of each child, parallel to <see cref="Targets"/>. Same span rule.</summary>
    public ReadOnlySpan<TEdge> Payload(int parent)
        => (uint)parent >= (uint)_parents ? default : _payload.Span.Slice(_start[parent], _length[parent]);

    /// <summary>The optimistic state of each child, parallel to <see cref="Targets"/> (C6) — a row reads it to grey
    /// itself or spin. Same span rule.</summary>
    public ReadOnlySpan<byte> Pending(int parent)
        => (uint)parent >= (uint)_parents ? default : _pending.Span.Slice(_start[parent], _length[parent]);

    /// <summary>How many children are present right now (not how many there will be — that is <see cref="Total"/>).</summary>
    public override int Count(int parent) => (uint)parent >= (uint)_parents ? 0 : _length[parent];

    /// <summary>Unknown / partial / complete. A page renders a skeleton for Unknown and an empty state only for
    /// Complete — the distinction 0.2.9 had to infer from a null list. Never <see cref="EdgeState.Failed"/>: that is
    /// <see cref="EdgeTableBase.Readiness"/>'s answer, and this one is what the store persists.</summary>
    public override EdgeState State(int parent) => (uint)parent >= (uint)_parents ? EdgeState.Unknown : (EdgeState)_state[parent];

    /// <summary>The server-reported total while <see cref="EdgeState.Partial"/> (the scrollbar's real extent), else the
    /// present count.</summary>
    public int Total(int parent) => (uint)parent >= (uint)_parents ? 0 : Math.Max(_total[parent], _length[parent]);

    /// <summary>Bumps on every structural write to THIS parent's list. A bound list compares it across frames; the
    /// table's <see cref="Publishable.Changed"/> signal is what wakes it (D8).</summary>
    public uint Version(int parent) => (uint)parent >= (uint)_parents ? 0 : _version[parent];

    /// <summary>Is <paramref name="target"/> one of this parent's children? THE membership question — "is this track
    /// liked", "is this album saved", "is this playlist pinned" — answered without a bool column anywhere (P3).
    /// O(1) once the index exists, a vectorized linear scan below <see cref="IndexFloor"/>.</summary>
    public bool Contains(int parent, int target)
    {
        if (_index is not null) return _index.Contains(Key(parent, target));
        if ((uint)parent >= (uint)_parents) return false;
        int length = _length[parent];
        if (length == 0) return false;
        if (length < IndexFloor) return _targets.Span.Slice(_start[parent], length).IndexOf(target) >= 0;
        BuildIndex();
        return _index!.Contains(Key(parent, target));
    }

    /// <summary>Where <paramref name="target"/> sits in the parent's list, or -1. Position, unlike membership, is
    /// inherently a scan — the index answers "whether", not "where".</summary>
    public int IndexOf(int parent, int target)
        => (uint)parent >= (uint)_parents ? -1 : _targets.Span.Slice(_start[parent], _length[parent]).IndexOf(target);

    /// <summary>The optimistic state of one edge, or <see cref="EdgePending.None"/> when it is not in the list.</summary>
    public EdgePending PendingOf(int parent, int target)
    {
        int at = IndexOf(parent, target);
        return at < 0 ? EdgePending.None : (EdgePending)_pending[_start[parent] + at];
    }

    // ── whole-list writes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rewrite the parent's list. This is how nearly every relation arrives — a page of a revision, an album's
    /// disc rows, a search answer — so it is the primary write and the others are special cases of it.
    /// <para><paramref name="payload"/> may be empty for a <c>NoEdge</c>-style relation; otherwise it must be the same
    /// length as <paramref name="targets"/>.</para></summary>
    public void Replace(int parent, ReadOnlySpan<int> targets, ReadOnlySpan<TEdge> payload, EdgeState state, int total)
    {
        if (payload.Length != 0 && payload.Length != targets.Length)
            throw new ArgumentException("payload must be empty or parallel to targets", nameof(payload));

        EnsureParent(parent);
        DropFromIndex(parent);

        int n = targets.Length;
        int start = Fit(parent, n);
        targets.CopyTo(_targets.Span.Slice(start, n));
        if (payload.Length != 0) payload.CopyTo(_payload.Span.Slice(start, n));
        else _payload.Clear(start, n);
        _pending.Clear(start, n);

        _length[parent] = n;
        _state[parent] = (byte)state;
        _total[parent] = total;                                    // a whole rewrite re-states the server's total
        AddToIndex(parent);
        Touched(parent);
    }

    /// <summary>Rewrite the parent's list with a COMPLETE run — the seed's and the bulk decoder's call (ch 31 GAP 3):
    /// one capacity probe, one copy, no per-row Add.</summary>
    public void ReplaceRun(int parent, ReadOnlySpan<int> targets, ReadOnlySpan<TEdge> payload)
        => Replace(parent, targets, payload, EdgeState.Complete, targets.Length);

    /// <summary>Land one PAGE of a paged relation at <paramref name="offset"/>, growing the list to
    /// <c>offset + targets.Length</c>. The list stays <see cref="EdgeState.Partial"/> until as many children are
    /// present as <paramref name="total"/> says there are; then it flips to <see cref="EdgeState.Complete"/> without
    /// anyone having to say so.
    /// <para>Capacity is reserved for the whole <paramref name="total"/> on the first page, so a 5,000-track playlist
    /// arriving in 100-row pages copies its arena once, not fifty times.</para></summary>
    public void ReplacePage(int parent, int offset, ReadOnlySpan<int> targets, ReadOnlySpan<TEdge> payload, int total)
    {
        if (payload.Length != 0 && payload.Length != targets.Length)
            throw new ArgumentException("payload must be empty or parallel to targets", nameof(payload));

        EnsureParent(parent);
        int n = targets.Length;
        int end = offset + n;
        int length = _length[parent];

        // A TERMINAL page — one that reaches a STATED total — is authoritative about the list's real extent (D7): a
        // shorter terminal answer than what is already stored means the rows past `end` are stale duplicates of a
        // LONGER PREVIOUS answer for the same parent, not a later page that has not landed yet. `total == 0` ("nobody
        // said") must never shrink — that would delete a legitimately landed later page on a multi-page list — so this
        // guarded, terminal case is the only one allowed to drop anything.
        bool terminal = total > 0 && end >= total;
        bool shrinks = terminal && length > end;

        // Re-derive the reserved extent from what the list will actually hold once this page lands, so a shrink does
        // not over-reserve for rows that are about to be dropped (harmless either way, just wasteful).
        int extent = shrinks ? end : Math.Max(end, length);
        int needed = Math.Max(extent, total);

        DropFromIndex(parent);
        int start = Fit(parent, needed, keep: length);

        // A page that starts past the current end leaves a hole; zeroed targets read as slot 0 = "none" (P3), which is
        // exactly what an un-arrived row is, and the next page overwrites it.
        if (offset > length) { _targets.Clear(start + length, offset - length); _payload.Clear(start + length, offset - length); }

        targets.CopyTo(_targets.Span.Slice(start + offset, n));
        if (payload.Length != 0) payload.CopyTo(_payload.Span.Slice(start + offset, n));
        else _payload.Clear(start + offset, n);
        _pending.Clear(start + offset, n);

        if (shrinks)
        {
            // Zero the truncated tail so a stale slot reads as "none" rather than a duplicate row from the longer
            // previous answer (P3), and shrink the length to match. A generic table cannot release a payload's own
            // interned strings (file header) — the rootlist arm (Edges.Staging.cs `ReleaseRootlistRows`) releases the
            // truncated rows' text BEFORE this call lands, under the same terminal guard, so nothing here leaks.
            _targets.Clear(start + end, length - end);
            _payload.Clear(start + end, length - end);
            _pending.Clear(start + end, length - end);
            _length[parent] = end;
        }
        else if (end > length) _length[parent] = end;

        // A TOTAL THE SERVER DID NOT STATE CANNOT TERMINATE THE LIST (D7). `total` 0 means "nobody said how many there
        // are", and folding it into the present count — which is what `Math.Max(total, _length)` alone does — turns
        // every first page into a Complete list and stops the page asking for the rest. Only a STATED total, reached,
        // settles a page; a stated one also SURVIVES a later page that omits it — UNLESS this page is itself terminal:
        // a terminal page's own stated total is authoritative, and letting it stay `Math.Max`ed against a stale, larger
        // `_total` would keep the state below reading Partial forever (the shrunk length can never reach it) and the
        // page would re-ask in a loop that never settles. So the terminal case is the one place that breaks the
        // `Math.Max` and lets `_total` take the stated value outright.
        int stated = terminal ? total : Math.Max(total, _total[parent]);
        _total[parent] = stated;
        _state[parent] = (byte)(stated > 0 && _length[parent] >= stated ? EdgeState.Complete : EdgeState.Partial);
        AddToIndex(parent);
        Touched(parent);
    }

    /// <summary>Forget the parent's children (keeping the arena capacity) and go back to
    /// <see cref="EdgeState.Unknown"/> — a scope-local invalidation, not a delete of anything.</summary>
    public void Clear(int parent)
    {
        if ((uint)parent >= (uint)_parents) return;
        DropFromIndex(parent);
        _length[parent] = 0;
        _total[parent] = 0;
        _state[parent] = (byte)EdgeState.Unknown;
        Touched(parent);
    }

    // ── single-edge writes (C6, ch 07 §9 item 3) ────────────────────────────────────────────────────────────────────

    /// <summary>Splice one child in at <paramref name="at"/> (default: append; <c>at: 0</c> prepends, which is what
    /// liking a track does — the Liked cover is newest-first). Marks it <paramref name="pending"/> so the UI can show
    /// the flip immediately while the shell PUTs (C6); <see cref="Settle"/> resolves it.
    /// <para>Already present? The payload and pending bits are updated in place and the position is left alone — a
    /// double-click on the heart must not add a second edge.</para></summary>
    public void Insert(int parent, int target, in TEdge payload, int at = -1, EdgePending pending = EdgePending.None)
    {
        EnsureParent(parent);
        int length = _length[parent];

        int existing = IndexOf(parent, target);
        if (existing >= 0)
        {
            int slot = _start[parent] + existing;
            _payload[slot] = payload;
            _pending[slot] = (byte)pending;
            Touched(parent);
            return;
        }

        if ((uint)at > (uint)length) at = length;
        int start = Fit(parent, length + 1, keep: length);

        int moved = length - at;
        if (moved > 0)
        {
            // Span.CopyTo is memmove: an overlapping shift-right is well defined.
            _targets.Span.Slice(start + at, moved).CopyTo(_targets.Span.Slice(start + at + 1, moved));
            _payload.Span.Slice(start + at, moved).CopyTo(_payload.Span.Slice(start + at + 1, moved));
            _pending.Span.Slice(start + at, moved).CopyTo(_pending.Span.Slice(start + at + 1, moved));
        }
        _targets[start + at] = target;
        _payload[start + at] = payload;
        _pending[start + at] = (byte)pending;
        _length[parent] = length + 1;
        if (_total[parent] > 0 && _total[parent] < _length[parent]) _total[parent] = _length[parent];   // a stated total grows with the list; an unstated one stays unstated
        if (_state[parent] == (byte)EdgeState.Unknown) _state[parent] = (byte)EdgeState.Complete;   // a local list we now know
        _index?.Add(Key(parent, target));
        Touched(parent);
    }

    /// <summary>Take one child out. Returns false when it was not there.</summary>
    public bool Remove(int parent, int target)
    {
        int at = IndexOf(parent, target);
        if (at < 0) return false;
        RemoveAt(parent, at);
        return true;
    }

    /// <summary>Take the child at <paramref name="at"/> out (the reorder / drag path already knows the index).</summary>
    public void RemoveAt(int parent, int at)
    {
        if ((uint)parent >= (uint)_parents) return;
        int length = _length[parent];
        if ((uint)at >= (uint)length) return;
        int start = _start[parent];
        _index?.Remove(Key(parent, _targets[start + at]));
        int moved = length - at - 1;
        if (moved > 0)
        {
            _targets.Span.Slice(start + at + 1, moved).CopyTo(_targets.Span.Slice(start + at, moved));
            _payload.Span.Slice(start + at + 1, moved).CopyTo(_payload.Span.Slice(start + at, moved));
            _pending.Span.Slice(start + at + 1, moved).CopyTo(_pending.Span.Slice(start + at, moved));
        }
        _length[parent] = length - 1;
        if (_total[parent] > 0) _total[parent]--;
        Touched(parent);
    }

    /// <summary>Mark a child as removed-but-not-yet-confirmed (C6): the row stays, greyed, until
    /// <see cref="Settle"/> either drops it or brings it back. Returns false when it was not there.</summary>
    public bool MarkRemove(int parent, int target)
    {
        int at = IndexOf(parent, target);
        if (at < 0) return false;
        _pending[_start[parent] + at] = (byte)EdgePending.Remove;
        Touched(parent);
        return true;
    }

    /// <summary>The server answered about an optimistic edge (C6). Four cases, one call:
    /// <list type="bullet">
    /// <item>pending Add + ok → clear the bit, the edge stands.</item>
    /// <item>pending Add + rejected → remove the edge; the like never happened.</item>
    /// <item>pending Remove + ok → remove the edge for real.</item>
    /// <item>pending Remove + rejected → clear the bit; the row comes back.</item>
    /// </list>
    /// An edge with no pending bit is left exactly as it is (a late duplicate confirmation is not an event).</summary>
    public bool Settle(int parent, int target, bool ok)
    {
        int at = IndexOf(parent, target);
        if (at < 0) return false;
        var pending = (EdgePending)_pending[_start[parent] + at];
        if (pending == EdgePending.None) return false;

        bool remove = (pending & EdgePending.Add) != 0 ? !ok : ok;
        if (remove) RemoveAt(parent, at);
        else { _pending[_start[parent] + at] = (byte)EdgePending.None; Touched(parent); }
        return true;
    }

    // ── arena ───────────────────────────────────────────────────────────────────────────────────────────────────────

    void EnsureParent(int parent)
    {
        if (parent < _parents) return;
        int capacity = Math.Max(parent + 1, Math.Max(16, _parents * 2));
        _start.EnsureCapacity(capacity);
        _length.EnsureCapacity(capacity);
        _capacity.EnsureCapacity(capacity);
        _total.EnsureCapacity(capacity);
        _version.EnsureCapacity(capacity);
        _state.EnsureCapacity(capacity);
        _parents = parent + 1;
    }

    /// <summary>Make sure the parent's range holds <paramref name="need"/> edges, moving it to the arena tail (with
    /// ×2 slack) when it does not fit and copying the first <paramref name="keep"/> entries across. Returns the range's
    /// start, which the caller must re-read — this is the move the span rule exists for.</summary>
    int Fit(int parent, int need, int keep = 0)
    {
        int start = _start[parent], capacity = _capacity[parent];
        if (need <= capacity) return start;

        int grown = Math.Max(need, Math.Max(4, capacity * 2));
        EnsureArena(_tail + grown);
        int moved = Math.Min(keep, capacity);
        if (moved > 0)
        {
            _targets.Span.Slice(start, moved).CopyTo(_targets.Span.Slice(_tail, moved));
            _payload.Span.Slice(start, moved).CopyTo(_payload.Span.Slice(_tail, moved));
            _pending.Span.Slice(start, moved).CopyTo(_pending.Span.Slice(_tail, moved));
        }
        _dead += capacity;
        _start[parent] = _tail;
        _capacity[parent] = grown;
        _tail += grown;
        return _start[parent];
    }

    void EnsureArena(int n)
    {
        _targets.EnsureCapacity(n);
        _payload.EnsureCapacity(n);
        _pending.EnsureCapacity(n);
    }

    /// <summary>Squeeze the arena back down to its live edges, in parent order — the fragmentation left by regrows.
    /// Called from the store's GC tick (R2), never from a frame: it rebuilds the three arena arrays, which is the one
    /// place a column is allowed to shrink (P5's exception, named in plan §4.3). Returns the entries reclaimed.</summary>
    public int Compact()
    {
        if (_dead == 0) return 0;

        int live = 0;
        for (int p = 0; p < _parents; p++) live += _length[p];

        var targets = new Column<int>(Math.Max(live, 4));
        var payload = new Column<TEdge>(Math.Max(live, 4));
        var pending = new Column<byte>(Math.Max(live, 4));

        int tail = 0;
        for (int p = 0; p < _parents; p++)
        {
            int length = _length[p];
            if (length == 0) { _start[p] = tail; _capacity[p] = 0; continue; }
            _targets.Span.Slice(_start[p], length).CopyTo(targets.Span.Slice(tail, length));
            _payload.Span.Slice(_start[p], length).CopyTo(payload.Span.Slice(tail, length));
            _pending.Span.Slice(_start[p], length).CopyTo(pending.Span.Slice(tail, length));
            _start[p] = tail;
            _capacity[p] = length;
            tail += length;
        }

        int reclaimed = _tail - tail;
        _targets = targets;
        _payload = payload;
        _pending = pending;
        _tail = tail;
        _dead = 0;
        return reclaimed;
    }

    // ── index + bookkeeping ─────────────────────────────────────────────────────────────────────────────────────────

    static long Key(int parent, int target) => ((long)parent << 32) | (uint)target;

    void BuildIndex()
    {
        var index = new HashSet<long>();
        for (int p = 0; p < _parents; p++)
        {
            int start = _start[p], length = _length[p];
            for (int i = 0; i < length; i++) index.Add(Key(p, _targets[start + i]));
        }
        _index = index;
    }

    void DropFromIndex(int parent)
    {
        if (_index is null || (uint)parent >= (uint)_parents) return;
        int start = _start[parent], length = _length[parent];
        for (int i = 0; i < length; i++) _index.Remove(Key(parent, _targets[start + i]));
    }

    void AddToIndex(int parent)
    {
        if (_index is null) return;
        int start = _start[parent], length = _length[parent];
        for (int i = 0; i < length; i++) _index.Add(Key(parent, _targets[start + i]));
    }

    void Touched(int parent)
    {
        _version[parent]++;
        MarkDirty();
    }
}

// ── 4. the payloads ───────────────────────────────────────────────────────────────────────────────────────────────────
// One struct per relation that carries data ON the edge rather than on either end (D10). All unmanaged, all small: a
// payload column is `n` edges × sizeof(TEdge) and nothing else.

/// <summary>No payload — the relation is the fact (track → artists, album → featured-on).</summary>
public readonly record struct NoEdge;

/// <summary>WHICH TABLE the target slot indexes — the payload of a CROSS-KIND relation (defect 5,
/// docs/plans/wavee/wavee-0.3-entity-identity-memory.md §3.1 requirement 4).
///
/// <para>A CSR target is an <c>int</c>, and an <c>int</c> is only an identity WITH a table. Every same-kind relation
/// here gets that table from the relation itself (an <c>AlbumTracks</c> target is a track), but the search "All" facet
/// is a list of tracks, albums, artists, playlists, shows and profiles in one ranked order — and it was an
/// <c>EdgeTable&lt;NoEdge&gt;</c> over "entity slots" (<c>Search.cs:7</c>) with nothing at all recording which table
/// each slot belonged to. The kind now rides on the row's <see cref="EntityId"/>, so the writer copies one byte off it
/// and the reader pairs it with the target: <c>new EntityRef(payload[i].Kind, targets[i])</c>.</para>
///
/// <para>One byte per edge, and not a whole <see cref="EntityId"/>: the target already names the row, and a 24-byte
/// payload on a 1,000-hit result page would cost more than the identity change saves.</para></summary>
public readonly record struct KindEdge(EntityKind Kind)
{
    /// <summary>The payload for a hit whose identity we hold.</summary>
    public static KindEdge Of(EntityId id) => new(id.Kind);

    /// <summary>Pair this payload with its target slot — the cross-kind pointer a mixed row binds to.</summary>
    public EntityRef Ref(int target) => new(Kind, target);
}

/// <summary>Where a track sits in an album: disc and track number belong to the PAIR, not to the track (the same
/// recording is track 3 here and track 11 on the deluxe edition).</summary>
public readonly record struct AlbumTrackEdge(byte Disc, ushort Number);

/// <summary>ONE RUNG of a track's format ladder — extension kind 5 (<c>AUDIO_FILES</c>) on the derived
/// <c>spotify:audio:</c> entity (FLAC plan §5.2, ch 01 DATA GAP 1 and 14). The relation is <b>payload-only</b>: the
/// rung is not a row anywhere, so <see cref="Edges.TrackFormats"/> carries no targets and the run's ORDER is the
/// wire's own (which is how the drawer can print the ladder the account was actually offered).
///
/// <para><paramref name="FormatId"/> is the wire enum verbatim (<c>metadata.AudioFile.Format</c>: 0-2 Ogg Vorbis
/// 96/160/320, 3-6 MP3, 8-9 AAC, <b>16 = FLAC</b>, 18-20 xHE-AAC, <b>22 = FLAC 24-bit</b>) and it is kept raw rather
/// than folded into an app enum on purpose: a rung this build cannot decode must still RENDER, disabled, or the ladder
/// silently shrinks (0.2.9's rule, plan §5.3). <paramref name="Kbps"/> is <c>average_bitrate / 1000</c> — 0 means the
/// wire did not say, and sorts last.</para>
///
/// <para>Three bytes per rung, and never a file id: the id is the wire's key to a STREAM and it is fetched at open
/// time (<c>Spotify.Audio</c>), so caching it per track per session would be 20 bytes an edge for a value the opener
/// re-reads anyway.</para></summary>
public readonly record struct FormatEdge(byte FormatId, ushort Kbps);

/// <summary>A playlist membership. Everything here was a field on 0.2.9's Track record and did not belong there: two
/// playlists containing the same track disagree about all of it (D10).
/// <para><paramref name="Flags"/> is for WIRE flags only; optimistic state is the table's
/// <see cref="EdgePending"/> column (file header). The chart triple is written by the seed today and by the live chart
/// decode when it lands (ch 31 GAP 12).</para></summary>
public readonly record struct PlaylistTrackEdge(
    StringId ItemId, int AddedAt, int AddedBy, byte ChartStatus, ushort ChartPos, ushort ChartPrev, byte Flags);

/// <summary>A library membership: liked track, saved album, followed artist, saved show, pin. The library IS this edge
/// (G6) — there is no <c>IsLiked</c> column anywhere (P3). <c>AddedAt</c> is UNIX seconds.</summary>
public readonly record struct LibraryEdge(int AddedAt, byte Flags);

/// <summary>A rootlist row: the sidebar's flat, ordered, foldered list of the user's playlists — the wire's own marker
/// stream (<see cref="RootlistKind"/>), one edge per item AND one per <c>start-group</c>/<c>end-group</c> marker.
///
/// <para><paramref name="FolderId"/> is THE FOLDER'S IDENTITY (D10, G-047): the bare group id off
/// <c>spotify:start-group:&lt;hex&gt;:&lt;name&gt;</c> / <c>spotify:end-group:&lt;hex&gt;</c> — 16 lowercase hex characters
/// as the client mints it (0.2.9 <c>SpotifyIds.NewGroupId</c>), kept verbatim because the server accepts whatever a
/// client once wrote. Set on BOTH markers of a folder and on nothing else: an item's folder is the innermost open
/// <see cref="RootlistKind.FolderStart"/> before it, exactly as the stream says. It is the id every surface keys a folder
/// on (the sidebar's <c>folder:&lt;hex&gt;</c> row and pin ids, persisted expanded state, the rootlist writes), and unlike
/// <paramref name="Position"/> it does not move when the folder does. <paramref name="FolderName"/> is the decoded name
/// (<c>+</c> is a space, then percent-unescaped) and is set on the START marker only.</para>
///
/// <para>Both strings are OWNED by the edge (the file header's rule): <c>Entities.CommitRootlist</c> AddRefs them and
/// releases the list it replaces, and <see cref="Edges.ReleaseText"/> gives the whole relation back with its scope.</para></summary>
public readonly record struct RootlistEdge(ushort Position, byte Depth, byte Kind, StringId FolderName, int AddedAt,
                                           StringId FolderId = default);

/// <summary>A queue row. <paramref name="ItemId"/> is Spotify's own 64-bit queue identity, which is what makes a row
/// stable across a reorder; <paramref name="Bucket"/> splits now-playing / user queue / next-up / history.</summary>
public readonly record struct QueueEdge(ulong ItemId, byte Provider, byte Bucket);

/// <summary>An artist's release: album / single / compilation / appears-on, the discography facet's own axis.</summary>
public readonly record struct DiscographyEdge(byte Kind);

/// <summary>One friend's current or last activity (§9.6 Q5, ch 21 G6). Five real handles where 0.2.9 carried five uri
/// strings, so the panel's rows route through the ordinary factories and the string soup goes away.</summary>
public readonly record struct FriendEdge(int UserSlot, long TimestampMs, int TrackSlot, int AlbumSlot, int ArtistSlot, int ContextSlot);

// ── 5. merch: the one side table ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>One merch listing (§9.6 Q4, ch 05 D8). Four interned strings: name, the PRICE AS THE WIRE GIVES IT
/// ("$25", "£19.00" — a formatted, localized, currency-bearing string, which is why it is text and not the
/// <c>uint PriceMinorUnits</c> plan §4.3 sketched; parsing it would lose the currency and gain nothing, and the row
/// renders it verbatim), the image and the shop url.</summary>
public struct Merch
{
    public StringId Name, Price, ImageId, ShopUrl;
}

/// <summary>The rows <see cref="Edges.AlbumMerch"/> and any later merch edge point into. NOT a <see cref="Table"/>:
/// merch has no uri, no known-bits, no authority and no fetch ladder of its own — it arrives whole with its album and
/// dies with it. A plain slab with a free-list-less bump allocator is the honest model (§9.6 Q4: "merch is not an
/// entity"). Slot 0 is "none", as everywhere else.</summary>
public sealed class MerchTable
{
    public Column<Merch> Row;
    public int Count = 1;

    /// <summary>One listing. Returns its slot — the value an <c>AlbumMerch</c> edge targets.</summary>
    public int Alloc()
    {
        int slot = Count++;
        Row.EnsureCapacity(Count);
        Row[slot] = default;
        return slot;
    }

    /// <summary><paramref name="n"/> contiguous listings, for an album's whole merch answer in one go (P4).</summary>
    public int AllocRun(int n)
    {
        if (n <= 0) return 0;
        int first = Count;
        Count += n;
        Row.EnsureCapacity(Count);
        Row.Clear(first, n);
        return first;
    }
}

// ── 6. the relations ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every relation in one place, one per scope (they die with their <see cref="Scope"/>, D9).
///
/// <para><b>Partial by design.</b> The chapters name roughly forty more relations that Waves 4 and 5 will need
/// (<c>ArtistTopCities</c>, <c>TrackCredits</c>, <c>PlaylistTuning</c>, <c>RecentsMembers</c>, …). Each is one field and
/// one payload struct, and its OWNER adds it from their own file — <c>public sealed partial class Edges { public
/// readonly EdgeTable&lt;CreditEdge&gt; TrackCredits = new(); }</c> — rather than queueing behind an edit to this one.
/// Nothing here enumerates the fields, so a new table needs no registration: publication is driven by the dirty list
/// each table enqueues itself on (see <see cref="Publishable"/>).</para>
///
/// <para>What is below is the set plan §4.3 fixes, plus the two the arbitrations added (<c>AlbumMerch</c> §9.6 Q4,
/// <c>Friends</c> §9.6 Q5) and the two ch 05 D8 names alongside merch (<c>AlbumFeaturedOn</c>, <c>AlbumSimilar</c>).</para></summary>
public sealed partial class Edges
{
    // identity relations
    public readonly EdgeTable<NoEdge> TrackArtists = new(), AlbumArtists = new(), ArtistRelated = new(), ShowEpisodes = new();
    /// <summary>Payload IS the tag id; targets are unused (the "the row is the payload" pattern, also used by credits
    /// and top-cities in later waves).</summary>
    public readonly EdgeTable<StringId> TrackTags = new();
    /// <summary>The per-track format ladder (extension kind 5, FLAC plan §5.2). Parent = the TRACK's slot — the answer
    /// is keyed by the derived <c>spotify:audio:</c> entity on the wire, but the ladder is a fact about the track, and
    /// the audio uuid is not a row in any table. Payload-only, like <see cref="TrackTags"/>: targets are unused.</summary>
    public readonly EdgeTable<FormatEdge> TrackFormats = new();
    public readonly EdgeTable<AlbumTrackEdge> AlbumTracks = new();
    public readonly EdgeTable<DiscographyEdge> ArtistReleases = new(), ArtistAppearsOn = new();
    public readonly EdgeTable<NoEdge> ArtistPopular = new();

    // the album trailing band (ch 05 D8): two ordinary relations and one side table
    public readonly EdgeTable<NoEdge> AlbumFeaturedOn = new(), AlbumSimilar = new();
    /// <summary>§9.6 Q4: the small side table merch rows live in.</summary>
    public readonly MerchTable Merch = new();
    /// <summary>§9.6 Q4: parent = album slot, target = a <see cref="MerchTable"/> row.</summary>
    public readonly EdgeTable<NoEdge> AlbumMerch = new();

    public readonly EdgeTable<PlaylistTrackEdge> PlaylistTracks = new();

    // the LIBRARY (G6): parent = the user slot, and membership is the edge, not a column
    public readonly EdgeTable<LibraryEdge> Liked = new(), SavedAlbums = new(), FollowedArtists = new(), SavedShows = new(), Pins = new();
    public readonly EdgeTable<RootlistEdge> Rootlist = new();

    /// <summary>Parent = the playback session subject (slot 1).</summary>
    public readonly EdgeTable<QueueEdge> Queue = new();
    /// <summary>§9.6 Q5 CONFIRMED: parent = one synthetic feed subject (ch 21 G6). The panel is Wave 4's.</summary>
    public readonly EdgeTable<FriendEdge> Friends = new();

    /// <summary>Synthetic subjects: <c>Home.cs</c> owns the parent slots. A home section's targets are all
    /// <c>SectionTable</c> rows, so the relation itself names the table and the payload stays empty.</summary>
    public readonly EdgeTable<NoEdge> HomeSection = new();

    /// <summary>Synthetic subjects: <c>Search.cs</c> owns the parent slots, one per (query, facet). CROSS-KIND, so the
    /// payload carries the table each target belongs to (<see cref="KindEdge"/>, defect 5): the "All" facet mixes six
    /// kinds in one ranked order and a bare slot cannot say which is which. A single-kind facet writes the same
    /// payload with the same value in every entry — one byte per hit, and no reader has to know which facet it is
    /// reading.</summary>
    public readonly EdgeTable<KindEdge> SearchResult = new();

    // the TRAIT relations (G-044): four extension kinds the track drawer and the album page read, each "the row is the
    // payload" or a plain list, and each landed by §8 below
    /// <summary>Extension kind 186: the credits block, in the server's own grouped order. Parent = track slot; the
    /// target is the credited ARTIST's slot, or <see cref="Table.None"/> for an unlinked contributor (a session player
    /// with no artist page). Owned text: see <see cref="CreditEdge"/>.</summary>
    public readonly EdgeTable<CreditEdge> TrackCredits = new();
    /// <summary>Extension kinds 98 / 99: the recording's other RENDITIONS — the audio counterpart of a music video and
    /// the video counterpart of a song. Parent = track slot, targets = track slots, the payload says which way.</summary>
    public readonly EdgeTable<VersionEdge> TrackVersions = new();
    /// <summary>Extension kind 237 reduced to <see cref="Spotify.Decode.WaveformColumns"/> magnitudes (0-255, the loudest
    /// column = 255). Parent = track slot; payload-only, targets unused — ~38 KB of wire per track becomes 220 bytes,
    /// once, at decode (0.2.9 <c>SpotifyTrackExpansionService.MapWaveform</c>).</summary>
    public readonly EdgeTable<byte> TrackWaveform = new();
    /// <summary>Extension kind 151: "playlists featuring this album", at most twelve (0.2.9's Take(12)). Parent = album
    /// slot, targets = playlist slots.</summary>
    public readonly EdgeTable<NoEdge> AlbumRecommendations = new();

    // The rootlist's revision, per parent — `{counter},{hex}` as the playlist routes want it back (Spotify.Api's
    // `FormatRevision`), ref-counted like every owned string. One string for the whole list, so a column keyed by parent
    // is the honest shape (the same call Recents.cs made for its snapshot revision).
    Column<StringId> _rootlistRevision;
    int _rootlistRevisionCount;

    /// <summary>The revision the parent's rootlist was answered at, or <see cref="StringId.Empty"/> — the base revision
    /// a rootlist write and a <c>/diff</c> read send.</summary>
    public StringId RootlistRevision(int parent)
        => (uint)parent >= (uint)_rootlistRevisionCount ? StringId.Empty : _rootlistRevision[parent];

    /// <summary>Record the revision an answer carried (AddRef the new, release the old).</summary>
    public void SetRootlistRevision(int parent, StringId revision)
    {
        if (parent < 0) return;
        if (parent >= _rootlistRevisionCount)
        {
            _rootlistRevision.EnsureCapacity(parent + 1);
            _rootlistRevision.Clear(_rootlistRevisionCount, parent + 1 - _rootlistRevisionCount);
            _rootlistRevisionCount = parent + 1;
        }
        Entities.RetainText(ref _rootlistRevision[parent], revision);
    }

    /// <summary>Give back the strings one parent's rootlist owns — the folder names and group ids — before the list is
    /// replaced (<c>Entities.CommitRootlist</c> AddRefs the new list FIRST, so a folder that kept its name keeps its id).</summary>
    internal void ReleaseRootlistText(int parent)
    {
        var rows = Rootlist.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].FolderName);
            Entities.Strings.Release(rows[i].FolderId);
        }
    }

    /// <summary>Give back the strings one track's credits own.</summary>
    internal void ReleaseCreditText(int parent)
    {
        var rows = TrackCredits.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].Name);
            Entities.Strings.Release(rows[i].Role);
            Entities.Strings.Release(rows[i].Group);
        }
    }

    /// <summary>THE SCOPE'S EDGE-OWNED TEXT, handed back when the scope is retired (G-052; <c>Scope.ReleaseText</c>
    /// calls it after the tables). Every relation whose payload holds a string it AddRef'd is walked here, and so is
    /// every per-parent revision column — the recents snapshot's (declared in Recents.cs, a part of this class) and the
    /// rootlist's. A relation that interns without AddRef (a permanent string) is not listed, because releasing what
    /// nobody retained is the one mistake worse than the leak.</summary>
    public void ReleaseText()
    {
        for (int p = 0; p < Rootlist.ParentCount; p++) ReleaseRootlistText(p);
        for (int p = 0; p < TrackCredits.ParentCount; p++) ReleaseCreditText(p);
        ReleaseSearchText();
        ReleaseArtistPayloadText();
        for (int p = 0; p < PlaylistTuning.ParentCount; p++) ReleaseTuningText(p);
        for (int p = 0; p < _rootlistRevisionCount; p++) Entities.ReleaseText(ref _rootlistRevision[p]);
        _rootlistRevisionCount = 0;
        for (int p = 0; p < _recentsRevisionCount; p++) Entities.ReleaseText(ref _recentsRevision[p]);
        _recentsRevisionCount = 0;
    }
}

/// <summary>One credit row (extension kind 186, <c>credits_v2_trait.proto</c>): the person, the role, the server's
/// group heading. Three OWNED strings — AddRef'd at commit, released when the track's credits are replaced and when
/// the scope retires (<see cref="Edges.ReleaseText"/>).</summary>
public readonly record struct CreditEdge(StringId Name, StringId Role, StringId Group);

/// <summary>Which way a <see cref="Edges.TrackVersions"/> edge points.</summary>
public enum TrackVersionKind : byte
{
    /// <summary>Kind 98: the AUDIO counterpart of a video rendition.</summary>
    Audio = 0,
    /// <summary>Kind 99: the VIDEO counterpart of an audio recording.</summary>
    Video = 1,
}

/// <summary>The payload of a <see cref="Edges.TrackVersions"/> edge.</summary>
public readonly record struct VersionEdge(TrackVersionKind Kind);

// ── 7. the rootlist marker stream (G-046, G-047) ─────────────────────────────────────────────────────────────────────
//
// THE ROOTLIST HAS ITS OWN STAGED SHAPE, because its payload carries two strings the generic staged edge has one slot
// for: a folder's NAME and its GROUP ID. The wire is a flat stream —
//
//     spotify:playlist:A
//     spotify:start-group:edb339e10aebcf38:Workout      FolderStart  depth 0  name "Workout"  id "edb339e10aebcf38"
//     spotify:playlist:B                                Item         depth 1
//     spotify:end-group:edb339e10aebcf38                FolderEnd    depth 0                   id "edb339e10aebcf38"
//
// — and it lands verbatim: one edge per item AND per marker, markers targeting slot 0, in wire order, with the wire
// index as the position. The sidebar's tree is BUILT from this stream (Sidebar.cs `WalkRootlist`); storing the stream
// is what makes a reorder a single index move.

/// <summary>One staged rootlist row (item or marker). Text is a <see cref="TextRef"/>: the decoder cannot intern (C1).</summary>
public struct StagedRootlistRow
{
    /// <summary>The playlist, for an <see cref="RootlistKind.Item"/>; empty for a marker.</summary>
    public StagedId Target;
    /// <summary>The decoded folder name (start marker only).</summary>
    public TextRef Name;
    /// <summary>The bare group id (both markers).</summary>
    public TextRef FolderId;
    public int AddedAt;
    public ushort Position;
    public byte Depth;
    public RootlistKind Kind;
}

/// <summary>One answered rootlist: the account it hangs off, its revision, and the slice of
/// <see cref="Staging.RootlistRows"/> it produced.</summary>
public struct StagedRootlist
{
    public StagedId Parent;
    public TextRef Revision;
    public int Start, Length;
}

public sealed partial class Staging
{
    StagedList<StagedRootlistRow>? _rootlistRows;
    StagedList<StagedRootlist>? _rootlists;

    /// <inheritdoc cref="StagedRootlistRow"/>
    public StagedList<StagedRootlistRow> RootlistRows => _rootlistRows ??= Register(new StagedList<StagedRootlistRow>());
    /// <inheritdoc cref="StagedRootlist"/>
    public StagedList<StagedRootlist> Rootlists => _rootlists ??= Register(new StagedList<StagedRootlist>());

    internal StagedList<StagedRootlistRow>? StagedRootlistRows => _rootlistRows;
    internal StagedList<StagedRootlist>? StagedRootlists => _rootlists;
}

// ── 8. the trait relations (G-044) ───────────────────────────────────────────────────────────────────────────────────

/// <summary>Which trait relation a <see cref="StagedTraitRun"/> rewrites — the four §6 names, and the tables they land
/// in are fixed by it (parent and child), exactly as <see cref="Relation"/> fixes them for the generic runs.</summary>
public enum TraitRelation : byte { TrackCredits, TrackVersions, TrackWaveform, AlbumRecommendations }

/// <summary>One staged trait member: a credit row, a version, a recommended playlist. A union read by the run's
/// relation: credits read the three texts and <see cref="Target"/> (the artist); versions read <see cref="Target"/> and
/// <see cref="B0"/> (the <see cref="TrackVersionKind"/>); recommendations read <see cref="Target"/>.</summary>
public struct StagedTrait
{
    public StagedId Target;
    public TextRef Name, Role, Group;
    public byte B0;
}

/// <summary>One parent's rewritten trait relation. A waveform carries no members: its 220 magnitudes ride
/// <see cref="Bytes"/> in the staging arena.</summary>
public struct StagedTraitRun
{
    public StagedId Parent;
    public TraitRelation Relation;
    public int Start, Length;
    public TextRef Bytes;
}

public sealed partial class Staging
{
    StagedList<StagedTrait>? _traits;
    StagedList<StagedTraitRun>? _traitRuns;

    /// <inheritdoc cref="StagedTrait"/>
    public StagedList<StagedTrait> Traits => _traits ??= Register(new StagedList<StagedTrait>());
    /// <inheritdoc cref="StagedTraitRun"/>
    public StagedList<StagedTraitRun> TraitRuns => _traitRuns ??= Register(new StagedList<StagedTraitRun>());

    internal StagedList<StagedTrait>? StagedTraits => _traits;
    internal StagedList<StagedTraitRun>? StagedTraitRuns => _traitRuns;
}

public static partial class Entities
{
    // UI thread only (C1); grown to the widest run seen and never shrunk (P8).
    static int[] s_traitTargets = new int[64];
    static RootlistEdge[] s_rootlistPayload = new RootlistEdge[64];
    static CreditEdge[] s_creditPayload = new CreditEdge[64];
    static VersionEdge[] s_versionPayload = new VersionEdge[64];
    static NoEdge[] s_traitNone = new NoEdge[64];

    /// <summary>Land every staged rootlist: resolve the account, resolve each ITEM to a playlist slot (a marker targets
    /// <see cref="Table.None"/>), AddRef the folder strings, release the list being replaced, and <c>Replace</c> —
    /// Complete, empty included (an account with no playlists has an empty rootlist, and that is an answer).</summary>
    static partial void CommitRootlist(Staging s)
    {
        var lists = s.StagedRootlists;
        if (lists is null || lists.Count == 0) return;
        var rows = s.StagedRootlistRows is { } r ? r.Span : default;
        var edges = Current.Edges;

        foreach (ref readonly var list in lists.Span)
        {
            if (list.Start < 0 || list.Length < 0 || list.Start + list.Length > rows.Length) continue;
            int parent = s.Slot(Current.Users, in list.Parent);
            if (parent == Table.None) continue;
            GrowTraits(list.Length);

            var page = rows.Slice(list.Start, list.Length);
            for (int i = 0; i < page.Length; i++)
            {
                ref readonly var row = ref page[i];
                s_traitTargets[i] = row.Kind == RootlistKind.Item ? s.Slot(Current.Playlists, in row.Target) : Table.None;
                s_rootlistPayload[i] = new RootlistEdge(row.Position, row.Depth, (byte)row.Kind,
                                                        Retained(s.Intern(row.Name)), row.AddedAt,
                                                        Retained(s.Intern(row.FolderId)));
            }
            edges.ReleaseRootlistText(parent);             // AFTER the AddRefs above: an unchanged folder keeps its id
            edges.Rootlist.Replace(parent, s_traitTargets.AsSpan(0, page.Length),
                                   s_rootlistPayload.AsSpan(0, page.Length), EdgeState.Complete, page.Length);
            if (!list.Revision.IsEmpty) edges.SetRootlistRevision(parent, s.Intern(list.Revision));
        }
    }

    /// <summary>Land every staged trait run (credits, versions, waveform, recommendations). Each is a whole-list
    /// <c>Replace</c>, Complete, EMPTY INCLUDED: "this track has no credits" is the answer that stops the drawer asking
    /// (finding 27's rule, as for descriptors).</summary>
    static partial void CommitTraits(Staging s)
    {
        var runs = s.StagedTraitRuns;
        if (runs is null || runs.Count == 0) return;
        var members = s.StagedTraits is { } m ? m.Span : default;
        var edges = Current.Edges;

        foreach (ref readonly var run in runs.Span)
        {
            if (run.Start < 0 || run.Length < 0 || run.Start + run.Length > members.Length) continue;
            var page = members.Slice(run.Start, run.Length);

            switch (run.Relation)
            {
                case TraitRelation.TrackCredits:
                    {
                        int parent = s.Slot(Current.Tracks, in run.Parent);
                        if (parent == Table.None) break;
                        GrowTraits(page.Length);
                        for (int i = 0; i < page.Length; i++)
                        {
                            ref readonly var row = ref page[i];
                            s_traitTargets[i] = s.Slot(Current.Artists, in row.Target);   // None for an unlinked name
                            s_creditPayload[i] = new CreditEdge(Retained(s.Intern(row.Name)), Retained(s.Intern(row.Role)),
                                                                Retained(s.Intern(row.Group)));
                        }
                        edges.ReleaseCreditText(parent);
                        edges.TrackCredits.Replace(parent, s_traitTargets.AsSpan(0, page.Length),
                                                   s_creditPayload.AsSpan(0, page.Length), EdgeState.Complete, page.Length);
                        break;
                    }
                case TraitRelation.TrackVersions:
                    {
                        int parent = s.Slot(Current.Tracks, in run.Parent);
                        if (parent == Table.None) break;
                        GrowTraits(page.Length);
                        int n = 0;
                        for (int i = 0; i < page.Length; i++)
                        {
                            int target = s.Slot(Current.Tracks, in page[i].Target);
                            if (target == Table.None || target == parent) continue;   // a rendition of itself is not a version
                            s_traitTargets[n] = target;
                            s_versionPayload[n++] = new VersionEdge((TrackVersionKind)page[i].B0);
                        }
                        edges.TrackVersions.Replace(parent, s_traitTargets.AsSpan(0, n), s_versionPayload.AsSpan(0, n),
                                                    EdgeState.Complete, n);
                        break;
                    }
                case TraitRelation.TrackWaveform:
                    {
                        int parent = s.Slot(Current.Tracks, in run.Parent);
                        if (parent == Table.None) break;
                        var magnitudes = s.Utf8(run.Bytes);
                        GrowTraits(magnitudes.Length);
                        s_traitTargets.AsSpan(0, magnitudes.Length).Clear();         // payload-only: targets unused
                        edges.TrackWaveform.Replace(parent, s_traitTargets.AsSpan(0, magnitudes.Length), magnitudes,
                                                    EdgeState.Complete, magnitudes.Length);
                        break;
                    }
                case TraitRelation.AlbumRecommendations:
                    {
                        int parent = s.Slot(Current.Albums, in run.Parent);
                        if (parent == Table.None) break;
                        GrowTraits(page.Length);
                        int n = 0;
                        for (int i = 0; i < page.Length; i++)
                        {
                            int target = s.Slot(Current.Playlists, in page[i].Target);
                            if (target != Table.None) s_traitTargets[n++] = target;
                        }
                        edges.AlbumRecommendations.Replace(parent, s_traitTargets.AsSpan(0, n), s_traitNone.AsSpan(0, n),
                                                           EdgeState.Complete, n);
                        break;
                    }
            }
        }
    }

    /// <summary>Intern-then-own: AddRef a freshly interned id so the edge that stores it OWNS it (the file header's rule).</summary>
    static StringId Retained(StringId id)
    {
        Strings.AddRef(id);
        return id;
    }

    static void GrowTraits(int n)
    {
        if (n <= s_traitTargets.Length) return;
        int size = s_traitTargets.Length;
        while (size < n) size *= 2;
        s_traitTargets = new int[size];
        s_rootlistPayload = new RootlistEdge[size];
        s_creditPayload = new CreditEdge[size];
        s_versionPayload = new VersionEdge[size];
        s_traitNone = new NoEdge[size];
    }
}
