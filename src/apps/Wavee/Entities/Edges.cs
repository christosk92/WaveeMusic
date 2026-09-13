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

// ── 2. the CSR table ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One relation: parent slot → an ordered run of child slots, each with an unmanaged
/// <typeparamref name="TEdge"/> payload. See the file header for the layout, the span rule and the pending bits.
///
/// <para><b>Parents are plain <c>int</c>s and need no table.</b> Usually a parent is an entity slot (album → tracks),
/// but a synthetic subject works exactly as well: the queue's parent is the playback session (1), a home section's
/// parent is a slot <c>Home.cs</c> mints, the friends feed's parent is one synthetic subject (ch 21 G6). The per-parent
/// columns grow on demand, so any small int is a legal parent.</para></summary>
public sealed class EdgeTable<TEdge> : Publishable where TEdge : unmanaged
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
    public int Count(int parent) => (uint)parent >= (uint)_parents ? 0 : _length[parent];

    /// <summary>Unknown / partial / complete. A page renders a skeleton for Unknown and an empty state only for
    /// Complete — the distinction 0.2.9 had to infer from a null list.</summary>
    public EdgeState State(int parent) => (uint)parent >= (uint)_parents ? EdgeState.Unknown : (EdgeState)_state[parent];

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
        int needed = Math.Max(Math.Max(end, length), total);

        DropFromIndex(parent);
        int start = Fit(parent, needed, keep: length);

        // A page that starts past the current end leaves a hole; zeroed targets read as slot 0 = "none" (P3), which is
        // exactly what an un-arrived row is, and the next page overwrites it.
        if (offset > length) { _targets.Clear(start + length, offset - length); _payload.Clear(start + length, offset - length); }

        targets.CopyTo(_targets.Span.Slice(start + offset, n));
        if (payload.Length != 0) payload.CopyTo(_payload.Span.Slice(start + offset, n));
        else _payload.Clear(start + offset, n);
        _pending.Clear(start + offset, n);

        if (end > length) _length[parent] = end;

        // A TOTAL THE SERVER DID NOT STATE CANNOT TERMINATE THE LIST (D7). `total` 0 means "nobody said how many there
        // are", and folding it into the present count — which is what `Math.Max(total, _length)` alone does — turns
        // every first page into a Complete list and stops the page asking for the rest. Only a STATED total, reached,
        // settles a page; a stated one also SURVIVES a later page that omits it.
        int stated = Math.Max(total, _total[parent]);
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

// ── 3. the payloads ──────────────────────────────────────────────────────────────────────────────────────────────────
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
/// (G6) — there is no <c>IsLiked</c> column anywhere (P3).</summary>
public readonly record struct LibraryEdge(int AddedAt, byte Flags);

/// <summary>A rootlist row: the sidebar's flat, ordered, foldered list of the user's playlists.</summary>
public readonly record struct RootlistEdge(ushort Position, byte Depth, byte Kind, StringId FolderName, int AddedAt);

/// <summary>A queue row. <paramref name="ItemId"/> is Spotify's own 64-bit queue identity, which is what makes a row
/// stable across a reorder; <paramref name="Bucket"/> splits now-playing / user queue / next-up / history.</summary>
public readonly record struct QueueEdge(ulong ItemId, byte Provider, byte Bucket);

/// <summary>An artist's release: album / single / compilation / appears-on, the discography facet's own axis.</summary>
public readonly record struct DiscographyEdge(byte Kind);

/// <summary>One friend's current or last activity (§9.6 Q5, ch 21 G6). Five real handles where 0.2.9 carried five uri
/// strings, so the panel's rows route through the ordinary factories and the string soup goes away.</summary>
public readonly record struct FriendEdge(int UserSlot, long TimestampMs, int TrackSlot, int AlbumSlot, int ArtistSlot, int ContextSlot);

// ── 4. merch: the one side table ─────────────────────────────────────────────────────────────────────────────────────

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

// ── 5. the relations ─────────────────────────────────────────────────────────────────────────────────────────────────

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
}
