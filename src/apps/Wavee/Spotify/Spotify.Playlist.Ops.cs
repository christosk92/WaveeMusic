// ── Spotify/Spotify.Playlist.Ops.cs ──────────────────────────────────────────────────────────────────────────────────
// the playlist4 op replayer, its decoder, the list-freshness rule and the dealer-push rule (wave D3)
//
// Role: CORE
// Owner: L2
// Wave: D3 (plan §3.3)
// Budget: 1350 lines
// Spec: docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §2, §3.3, §3.7–§3.10
//
// THE WIRE, AS FOUR CAPTURES SHOWED IT (C:\WAVEE\wavee-captures\playlist4-2026-09 — the official desktop client, no
// Wavee traffic; the scrubbed re-encodes the tests replay are Wavee.Tests/Fixtures/playlist-ops, README there):
//   · clients WRITE keyed — MOV{items, add_after_item | add_first}, REM{items, items_as_key}, ADD{items, add_last |
//     add_after_item} — and the service ECHOES and DIFFS positional — MOV{from, length, to} with no items,
//     REM{from, length} WITH the removed rows, ADD{from, items}. A server `/diff` and a dealer echo are one shape.
//   · The ops of one answer are SEQUENTIAL: every index is against the state the PRECEDING ops left (a 3-op REM is
//     echoed as from 2 · from 4 len 2 · from 10; a 3-row ADD as from 3 · from 4 · from 5).
//   · MOV.to_index is in PRE-REMOVAL coordinates — "insert before original index `to`", so the block lands at
//     `to > from ? to − length : to`. Proven twice: the reorders pair (MOV{0,1,2} then MOV{3,1,1} — the keyed requests
//     say where each row went) and 11→14's forward block MOV{0,3,5} in a 6-row list, where no other reading is in range.
//   · A diff answer carries NO `length` (0 of 24), so acceptance is arithmetic over what was applied (Tally/Reconciles).
//   · `changes_require_resync` (20) is set on every ADD's /changes answer: such an answer is never replayed.
//
// PROVEN vs RULE-DERIVED. Capture-proven: positional MOV (forward len 1, backward block, forward block), positional REM
// (len 1 and 2, sequential), positional ADD (single, sequential, the rootlist's 2-marker folder create), keyed MOV
// (add_after_item, add_first), keyed REM, keyed ADD (add_after_item), UPDATE_LIST_ATTRIBUTES (name + description).
// Rule-derived (implemented; any doubt refuses): UPDATE_ITEM_ATTRIBUTES, keyed MOV/ADD add_last, a rootlist index REM.
// REFUSED because no capture has shown them: add_before_item (ADD and MOV), `multiple_heads`, a diff beside `contents`,
// `up_to_date` beside ops, a 0-op diff that advances the revision.
//
// ANY MISFIT ⇒ FALSE, AND THE LIST IS UNTOUCHED ⇒ the caller reads the list in full. A replayer that guesses poisons a
// persisted list for ever (plan §7); a refusal costs one request, and its `Refusal` names the shape for the
// `list.replay … verdict=fullread:<why>` line (§3.9) — how the still-unobserved shapes get observed in the field.
//
// Rules: pure — no table, no signal, no clock, no interner. A replay allocates (a copy of the list, the decoded
// strings): a diff is rare and small, and nothing here runs per frame. The decoder reads with the list decoders' own
// parser (`Spotify.Decode.ProtoReader`) behind a STRICT frame check (`Framed`), because a replay must refuse a
// truncated frame where a full-read decode may keep what it got.

using System.Runtime.InteropServices;
using System.Text;

namespace Wavee;

/// <summary>THE PLAYLIST4 OP REPLAYER (plan §3.3). Applies a <c>/diff</c> answer's or a dealer push's ops to a list
/// the caller holds — each op against the state the PRECEDING ops produced — and refuses, leaving the list untouched,
/// on anything that does not fit. Generic over the caller's row type (<see cref="IRowAccess{TRow}"/>): the persisted
/// <c>ListRow</c>, the decoder's own <see cref="WireItem"/>, or a test's. PURE.</summary>
public static class PlaylistOps
{
    // ── 1. the vocabulary ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What an op does (playlist4 <c>Op.Kind</c> 2…6).</summary>
    public enum Kind : byte { Add, Rem, Mov, UpdateItemAttributes, UpdateListAttributes }

    /// <summary>Where an ADD or a keyed MOV lands. <see cref="Index"/> is positional (<see cref="Op.From"/>);
    /// <see cref="AfterItem"/> resolves <see cref="Op.Anchor"/> against the list at that point of the batch (for a keyed
    /// MOV, after the moved rows were lifted out — 0.2.x's rule). There is deliberately no "before item": no capture
    /// has shown <c>add_before_item</c>, so the decoder refuses it (<see cref="Refusal.Unobserved"/>).</summary>
    public enum Place : byte { Index, First, Last, AfterItem }

    /// <summary>The item attributes an UPDATE_ITEM_ATTRIBUTES op may set or unset in this build — the three a row can
    /// hold. Any other attribute in the op (seen_at, format_attributes, item_id, an unnamed field) is
    /// <see cref="Refusal.ItemAttribute"/>.</summary>
    [Flags]
    public enum ItemAttrs : byte
    {
        None = 0,
        /// <summary><c>added_by</c> (1).</summary>
        AddedBy = 1 << 0,
        /// <summary><c>timestamp</c> (2).</summary>
        Timestamp = 1 << 1,
        /// <summary><c>public</c> (10) — a rootlist entry's.</summary>
        Public = 1 << 2,
    }

    /// <summary>The list attributes an UPDATE_LIST_ATTRIBUTES op may change in this build — the two a capture proved
    /// (§3.10: a phone rename + a first description). Anything else in <c>new_attributes</c> is
    /// <see cref="Refusal.ListAttribute"/>; the full read that follows re-reads the header anyway.</summary>
    [Flags]
    public enum ListAttrs : byte { None = 0, Name = 1 << 0, Description = 1 << 1 }

    /// <summary>Why a batch was not replayed — the <c>&lt;why&gt;</c> of <c>verdict=fullread:&lt;why&gt;</c>.</summary>
    public enum Refusal : byte
    {
        None,
        /// <summary><c>changes_require_resync</c> (20): the server says this answer is not a trustworthy delta.</summary>
        Resync,
        /// <summary><c>multiple_heads</c> (9) — never observed; nothing to replay against.</summary>
        MultipleHeads,
        /// <summary>Fields that contradict each other: a diff beside <c>contents</c>, <c>up_to_date</c> beside ops.</summary>
        Contradictory,
        /// <summary>A frame that does not parse, a wire type that is not the field's, a revision under five bytes, or an
        /// op whose item span the caller built out of range.</summary>
        Malformed,
        /// <summary>An op (or its Add/Rem/Mov/Diff body) carries a field this build does not name.</summary>
        UnknownField,
        /// <summary><c>KIND_UNKNOWN</c>, an unnamed kind, or a kind whose body is missing.</summary>
        OpKind,
        /// <summary>A shape this build does not express: no placement, two placements, a keyed op with indices, an index
        /// REM whose removed rows are not all carried, a destination inside the moved block, an op that touches no row.</summary>
        OpShape,
        /// <summary>A shape no capture has shown (<c>add_before_item</c>; a 0-op diff whose revision advances).</summary>
        Unobserved,
        /// <summary>UPDATE_ITEM_ATTRIBUTES names an attribute outside <see cref="ItemAttrs"/>.</summary>
        ItemAttribute,
        /// <summary>UPDATE_LIST_ATTRIBUTES names an attribute outside <see cref="ListAttrs"/>.</summary>
        ListAttribute,
        /// <summary>An index outside the list as the preceding ops left it.</summary>
        IndexOutOfRange,
        /// <summary>An index REM's carried row is not the row at that index (by item_id when both have one, else uri),
        /// or an UPDATE_ITEM's old attributes are not the row's.</summary>
        IdentityMismatch,
        /// <summary>A keyed REM/MOV names a row that is not in the list.</summary>
        KeyAbsent,
        /// <summary>The row an ADD/MOV lands after is not in the list (or is one of the rows being moved).</summary>
        AnchorAbsent,
        /// <summary>An ADD carries an item_id the list already holds — the list is not the one the op was computed against.</summary>
        DuplicateItemId,
        /// <summary>The caller's row type cannot hold the attribute change (<see cref="IRowAccess{TRow}.TryPatch"/>).</summary>
        PatchRefused,
    }

    /// <summary>ONE op, as a value. Rows travel beside it in an item span (<see cref="ItemsStart"/>/<see cref="ItemsCount"/>).
    /// <list type="bullet">
    /// <item>ADD — the new rows; placed at <see cref="From"/> (<see cref="Place.Index"/>), at an end, or right after
    /// the <see cref="Anchor"/> row.</item>
    /// <item>REM — positional: <see cref="From"/>/<see cref="Length"/>, and the carried rows (exactly
    /// <see cref="Length"/>) are checked one for one against the rows removed. Keyed (<see cref="ItemsAsKey"/>): each
    /// carried row names one row to remove.</item>
    /// <item>MOV — positional: <see cref="From"/>/<see cref="Length"/>/<see cref="To"/>, <see cref="To"/> in
    /// PRE-REMOVAL coordinates. Keyed (<see cref="ItemsAsKey"/>): the carried rows are lifted out in op order and land at
    /// <see cref="Place"/>.</item>
    /// <item>UPDATE_ITEM_ATTRIBUTES — the row at <see cref="From"/>; item <see cref="ItemsStart"/> carries the new
    /// values of <see cref="Set"/>, and when <see cref="OldSet"/>/<see cref="OldUnset"/> say anything, item
    /// <see cref="ItemsStart"/>+1 carries the old values the row must still hold.</item>
    /// <item>UPDATE_LIST_ATTRIBUTES — <see cref="Change"/> indexes the list-attribute span.</item>
    /// </list></summary>
    /// <param name="Anchor">Index into the item span of the anchor row; -1 for none.</param>
    public readonly record struct Op(
        Kind Kind,
        int From = -1,
        int Length = 0,
        int To = -1,
        Place Place = Place.Index,
        int Anchor = -1,
        bool ItemsAsKey = false,
        int ItemsStart = 0,
        int ItemsCount = 0,
        ItemAttrs Set = ItemAttrs.None,
        ItemAttrs Unset = ItemAttrs.None,
        ItemAttrs OldSet = ItemAttrs.None,
        ItemAttrs OldUnset = ItemAttrs.None,
        int Change = -1)
    {
        /// <summary>A positional ADD of items [<paramref name="start"/>, +<paramref name="count"/>) at <paramref name="index"/>.</summary>
        public static Op AddAt(int index, int start, int count) => new(Kind.Add, From: index, ItemsStart: start, ItemsCount: count);
        /// <summary>A placed ADD (an end, or right after the anchor item).</summary>
        public static Op AddAt(Place place, int start, int count, int anchor = -1)
            => new(Kind.Add, Place: place, Anchor: anchor, ItemsStart: start, ItemsCount: count);
        /// <summary>A positional REM whose removed rows are items [<paramref name="start"/>, +<paramref name="length"/>).</summary>
        public static Op Remove(int from, int length, int start)
            => new(Kind.Rem, From: from, Length: length, ItemsStart: start, ItemsCount: length);
        /// <summary>A keyed REM: each of items [<paramref name="start"/>, +<paramref name="count"/>) names one row.</summary>
        public static Op RemoveKeyed(int start, int count) => new(Kind.Rem, ItemsAsKey: true, ItemsStart: start, ItemsCount: count);
        /// <summary>A positional MOV; <paramref name="to"/> is in PRE-REMOVAL coordinates.</summary>
        public static Op Move(int from, int length, int to) => new(Kind.Mov, From: from, Length: length, To: to);
        /// <summary>A keyed MOV of items [<paramref name="start"/>, +<paramref name="count"/>) to <paramref name="place"/>.</summary>
        public static Op MoveKeyed(int start, int count, Place place, int anchor = -1)
            => new(Kind.Mov, Place: place, Anchor: anchor, ItemsAsKey: true, ItemsStart: start, ItemsCount: count);
        /// <summary>UPDATE_ITEM_ATTRIBUTES at <paramref name="index"/>, new values in item <paramref name="values"/>.</summary>
        public static Op UpdateItem(int index, int values, ItemAttrs set, ItemAttrs unset = ItemAttrs.None)
            => new(Kind.UpdateItemAttributes, From: index, ItemsStart: values, ItemsCount: 1, Set: set, Unset: unset);
        /// <summary>UPDATE_LIST_ATTRIBUTES, the change at <paramref name="change"/> of the list-attribute span.</summary>
        public static Op UpdateList(int change) => new(Kind.UpdateListAttributes, Change: change);
    }

    /// <summary>One wire <c>Item</c> as the decoder read it: the uri, the <c>item_id</c> as lowercase hex (the same
    /// spelling <c>Spotify.Decode</c> stages — null when the wire gave none, as on every rootlist row), the adder's bare
    /// USERNAME (not a uri), the raw wire <c>timestamp</c> (milliseconds on both relations in every capture — convert
    /// it the way a full read does, <c>Spotify.Decode.Instant</c>), the rootlist <c>public</c> bit, and a chart row's
    /// triple (<c>format_attributes</c> status/current_pos/previous_pos, read exactly as the full-read decoder reads
    /// them). <see cref="Present"/> says which of the three patchable attributes the wire actually carried.
    /// An UPDATE_ITEM's values ride as a WireItem with an empty <see cref="Uri"/>.</summary>
    public readonly record struct WireItem(
        string Uri,
        string? ItemId = null,
        string? AddedBy = null,
        long Timestamp = 0,
        bool Public = false,
        ItemAttrs Present = ItemAttrs.None,
        byte ChartStatus = 0,
        ushort ChartPos = 0,
        ushort ChartPrev = 0);

    /// <summary>A header change a batch carried (UPDATE_LIST_ATTRIBUTES, merged in op order). A null field says nothing;
    /// a bit in <see cref="Unset"/> is the wire's <c>no_value</c> — the attribute was explicitly UNSET, which the header
    /// must record as absent, never as an empty string (§3.10).</summary>
    public readonly record struct ListAttributeChange(string? Name = null, string? Description = null, ListAttrs Unset = ListAttrs.None)
    {
        /// <summary>No header change at all.</summary>
        public bool IsEmpty => Name is null && Description is null && Unset == ListAttrs.None;
    }

    /// <summary>What a replay did. <see cref="Added"/>/<see cref="Removed"/> feed the stored-total check
    /// (<see cref="Reconciles(int,int)"/>): the wire gives a diff answer no <c>length</c> (§3.8).</summary>
    public readonly record struct Tally(int Added, int Removed, int Moved, int ItemUpdates, int ListUpdates)
    {
        /// <inheritdoc cref="PlaylistOps.Reconciles(int,int,int,int)"/>
        public bool Reconciles(int baselineCount, int replayedCount)
            => PlaylistOps.Reconciles(baselineCount, replayedCount, Added, Removed);
    }

    /// <summary>The op a replay stopped at, and why.</summary>
    public readonly record struct Misfit(int OpIndex, Kind Kind, Refusal Why);

    /// <summary>How the replayer reads and patches the caller's row type. Implement it on a STRUCT so the generic
    /// <see cref="TryApply{TRow,TAccess}"/> calls are constrained (no boxing, no interface dispatch).
    /// <para>IDENTITY is one rule everywhere — index REM checks, keyed REM/MOV lookups, anchors: by <c>item_id</c> when
    /// BOTH rows have one, else by uri (rootlist rows have no item_id). A persisted rootlist row whose marker was split
    /// into columns must answer <see cref="SameUri"/> as if it still carried its <c>spotify:start-group:…</c> uri.</para></summary>
    public interface IRowAccess<TRow>
    {
        /// <summary>Do the two rows name the same uri (ordinal)?</summary>
        bool SameUri(in TRow a, in TRow b);
        /// <summary>Does the row carry an <c>item_id</c>?</summary>
        bool HasItemId(in TRow row);
        /// <summary>Do the two rows carry the same <c>item_id</c>? Asked only when both have one.</summary>
        bool SameItemId(in TRow a, in TRow b);
        /// <summary>Does <paramref name="row"/> hold <paramref name="values"/>' attributes named in <paramref name="set"/>
        /// and none of those named in <paramref name="unset"/>? (An UPDATE_ITEM's <c>old_attributes</c> check.)</summary>
        bool Holds(in TRow row, in TRow values, ItemAttrs set, ItemAttrs unset);
        /// <summary>Copy <paramref name="values"/>' attributes named in <paramref name="set"/> into
        /// <paramref name="row"/> and clear those named in <paramref name="unset"/>. False when the row type cannot hold
        /// one of them — the replay then refuses (<see cref="Refusal.PatchRefused"/>).</summary>
        bool TryPatch(ref TRow row, in TRow values, ItemAttrs set, ItemAttrs unset);
    }

    /// <summary>The accessor for the decoder's own row, so a caller (or a test) can replay over <see cref="WireItem"/>s
    /// directly.</summary>
    public readonly struct WireItemAccess : IRowAccess<WireItem>
    {
        public bool SameUri(in WireItem a, in WireItem b) => string.Equals(a.Uri, b.Uri, StringComparison.Ordinal);
        public bool HasItemId(in WireItem row) => !string.IsNullOrEmpty(row.ItemId);
        public bool SameItemId(in WireItem a, in WireItem b) => string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);

        public bool Holds(in WireItem row, in WireItem values, ItemAttrs set, ItemAttrs unset)
        {
            if ((set & ~row.Present) != ItemAttrs.None || (unset & row.Present) != ItemAttrs.None) return false;
            if ((set & ItemAttrs.AddedBy) != 0 && !string.Equals(row.AddedBy, values.AddedBy, StringComparison.Ordinal)) return false;
            if ((set & ItemAttrs.Timestamp) != 0 && row.Timestamp != values.Timestamp) return false;
            if ((set & ItemAttrs.Public) != 0 && row.Public != values.Public) return false;
            return true;
        }

        public bool TryPatch(ref WireItem row, in WireItem values, ItemAttrs set, ItemAttrs unset)
        {
            string? addedBy = row.AddedBy;
            long timestamp = row.Timestamp;
            bool isPublic = row.Public;
            if ((set & ItemAttrs.AddedBy) != 0) addedBy = values.AddedBy;
            if ((set & ItemAttrs.Timestamp) != 0) timestamp = values.Timestamp;
            if ((set & ItemAttrs.Public) != 0) isPublic = values.Public;
            if ((unset & ItemAttrs.AddedBy) != 0) addedBy = null;
            if ((unset & ItemAttrs.Timestamp) != 0) timestamp = 0;
            if ((unset & ItemAttrs.Public) != 0) isPublic = false;
            row = row with { AddedBy = addedBy, Timestamp = timestamp, Public = isPublic, Present = (row.Present | set) & ~unset };
            return true;
        }
    }

    /// <summary>A decoded op batch: the ops, the rows they carry (op items, anchors and UPDATE_ITEM values, indexed by
    /// <see cref="Op.ItemsStart"/>/<see cref="Op.Anchor"/>), and the header changes (<see cref="Op.Change"/>).</summary>
    public sealed class Batch
    {
        public static readonly Batch Empty = new([], [], []);

        public Batch(Op[] ops, WireItem[] items, ListAttributeChange[] lists)
        {
            Ops = ops;
            Items = items;
            Lists = lists;
        }

        public Op[] Ops { get; }
        public WireItem[] Items { get; }
        public ListAttributeChange[] Lists { get; }

        /// <summary>The uris this batch ADDS, in op order, appended to <paramref name="into"/> — "after a diff, hydrate
        /// only the added uris" (§2). Meaningful once <see cref="TryApply(List{WireItem},Batch,out ListAttributeChange,out Tally,out Misfit)"/>
        /// (or the generic form) accepted the batch.</summary>
        public void AddedUris(List<string> into)
        {
            foreach (var op in Ops)
                if (op.Kind == Kind.Add)
                    for (int i = 0; i < op.ItemsCount; i++) into.Add(Items[op.ItemsStart + i].Uri);
        }
    }

    // ── 2. the replay ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Apply <paramref name="ops"/> to <paramref name="list"/>, each against the state the PRECEDING ops
    /// produced. Returns false — and leaves <paramref name="list"/> exactly as it was — on any misfit:
    /// an index out of range, an index REM whose carried rows are not the rows it names (by item_id when both have one,
    /// else uri), a keyed REM/MOV whose row is not there, an anchor that is not there, an ADD of an item_id the list
    /// already holds, an UPDATE_ITEM the row type cannot hold, an op shape this build does not express. The caller
    /// then reads the list in full. <paramref name="opItems"/> are the batch's rows mapped to the caller's row type
    /// (<see cref="Batch.Items"/>, index for index); <paramref name="lists"/> the batch's header changes.
    /// <para>On success <paramref name="attrs"/> is the merged header change (land it on the playlist row in the SAME
    /// commit as the rows) and <paramref name="tally"/> what was added and removed. The in-memory list reconciles by
    /// construction; <see cref="Reconciles(int,int,int,int)"/> is for the persisted total beside it.</para></summary>
    public static bool TryApply<TRow, TAccess>(List<TRow> list, ReadOnlySpan<Op> ops, ReadOnlySpan<TRow> opItems,
        ReadOnlySpan<ListAttributeChange> lists, TAccess access, out ListAttributeChange attrs, out Tally tally, out Misfit misfit)
        where TAccess : struct, IRowAccess<TRow>
    {
        ArgumentNullException.ThrowIfNull(list);
        attrs = default;
        tally = default;
        misfit = default;
        var work = new List<TRow>(list.Count + 8);
        work.AddRange(list);                                          // the copy IS the undo log: `list` moves only on success
        int added = 0, removed = 0, moved = 0, itemUpdates = 0, listUpdates = 0;
        ListAttributeChange change = default;

        for (int i = 0; i < ops.Length; i++)
        {
            Op op = ops[i];
            Refusal why = InRange(in op, opItems.Length, lists.Length);
            if (why == Refusal.None)
            {
                switch (op.Kind)
                {
                    case Kind.Add: why = ApplyAdd(work, in op, opItems, access, ref added); break;
                    case Kind.Rem: why = ApplyRem(work, in op, opItems, access, ref removed); break;
                    case Kind.Mov: why = op.ItemsAsKey ? MoveKeyed(work, in op, opItems, access, ref moved) : Move(work, in op, ref moved); break;
                    case Kind.UpdateItemAttributes: why = ApplyItem(work, in op, opItems, access, ref itemUpdates); break;
                    case Kind.UpdateListAttributes: Merge(ref change, in lists[op.Change]); listUpdates++; break;
                    default: why = Refusal.OpKind; break;
                }
            }
            if (why != Refusal.None)
            {
                misfit = new Misfit(i, op.Kind, why);
                return false;
            }
        }

        list.Clear();
        list.AddRange(work);
        attrs = change;
        tally = new Tally(added, removed, moved, itemUpdates, listUpdates);
        return true;
    }

    /// <summary><see cref="TryApply{TRow,TAccess}"/> over the decoder's own rows.</summary>
    public static bool TryApply(List<WireItem> list, Batch batch, out ListAttributeChange attrs, out Tally tally, out Misfit misfit)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return TryApply<WireItem, WireItemAccess>(list, batch.Ops, batch.Items, batch.Lists, default, out attrs, out tally, out misfit);
    }

    /// <summary>THE STORED-TOTAL CHECK 0.2.x never had. The wire gives a diff answer no <c>length</c> (§3.8), so the
    /// check is arithmetic over what was applied: the list must end at baseline + adds − removes. A replay reconciles
    /// its own copy by construction — the check is for the persisted <c>list_head.total</c> / playlist
    /// <c>TrackCount</c> the caller holds beside the rows.</summary>
    public static bool Reconciles(int baselineCount, int replayedCount, int added, int removed)
        => baselineCount >= 0 && added >= 0 && removed >= 0 && replayedCount == baselineCount + added - removed;

    static Refusal InRange(in Op op, int items, int lists)
    {
        if (op.ItemsStart < 0 || op.ItemsCount < 0 || op.ItemsStart > items - op.ItemsCount) return Refusal.Malformed;
        if (op.Anchor >= items || op.Anchor < -1) return Refusal.Malformed;
        if (op.Kind == Kind.UpdateListAttributes && (uint)op.Change >= (uint)lists) return Refusal.Malformed;
        return Refusal.None;
    }

    static bool Same<TRow, TAccess>(in TRow a, in TRow b, TAccess access) where TAccess : struct, IRowAccess<TRow>
        => access.HasItemId(in a) && access.HasItemId(in b) ? access.SameItemId(in a, in b) : access.SameUri(in a, in b);

    static int IndexOf<TRow, TAccess>(List<TRow> work, in TRow key, TAccess access) where TAccess : struct, IRowAccess<TRow>
    {
        Span<TRow> rows = CollectionsMarshal.AsSpan(work);
        for (int i = 0; i < rows.Length; i++)
            if (Same(in rows[i], in key, access)) return i;
        return -1;
    }

    static Refusal ApplyAdd<TRow, TAccess>(List<TRow> work, in Op op, ReadOnlySpan<TRow> opItems, TAccess access, ref int added)
        where TAccess : struct, IRowAccess<TRow>
    {
        if (op.ItemsCount == 0 || op.ItemsAsKey) return Refusal.OpShape;
        ReadOnlySpan<TRow> rows = opItems.Slice(op.ItemsStart, op.ItemsCount);
        int at;
        switch (op.Place)
        {
            case Place.First: at = 0; break;
            case Place.Last: at = work.Count; break;
            case Place.Index:
                if (op.From < 0 || op.From > work.Count) return Refusal.IndexOutOfRange;
                at = op.From;
                break;
            case Place.AfterItem:
                {
                    if (op.Anchor < 0) return Refusal.OpShape;
                    int anchor = IndexOf(work, in opItems[op.Anchor], access);
                    if (anchor < 0) return Refusal.AnchorAbsent;
                    at = anchor + 1;
                    break;
                }
            default: return Refusal.OpShape;
        }

        // An ADD never brings a row the list already holds: item_ids are unique per list, so a carried id that is
        // already here means this list is not the one the op was computed against (0.2.x skipped such an item — I6 —
        // which is right for its optimistic echo and wrong for a replay). Rows without ids (rootlist) are never compared.
        Span<TRow> held = CollectionsMarshal.AsSpan(work);
        for (int i = 0; i < rows.Length; i++)
        {
            if (!access.HasItemId(in rows[i])) continue;
            for (int j = 0; j < held.Length; j++)
                if (access.HasItemId(in held[j]) && access.SameItemId(in held[j], in rows[i])) return Refusal.DuplicateItemId;
            for (int j = 0; j < i; j++)
                if (access.HasItemId(in rows[j]) && access.SameItemId(in rows[j], in rows[i])) return Refusal.DuplicateItemId;
        }

        work.InsertRange(at, rows);
        added += rows.Length;
        return Refusal.None;
    }

    static Refusal ApplyRem<TRow, TAccess>(List<TRow> work, in Op op, ReadOnlySpan<TRow> opItems, TAccess access, ref int removed)
        where TAccess : struct, IRowAccess<TRow>
    {
        ReadOnlySpan<TRow> rows = opItems.Slice(op.ItemsStart, op.ItemsCount);
        if (op.ItemsAsKey)
        {
            // Keyed (the desktop REQUEST shape): each carried row names exactly one row. Absent is a misfit — 0.2.x let a
            // uri-only key pass silently because its optimistic edit had already removed the row; a replay has no such excuse.
            if (rows.IsEmpty) return Refusal.OpShape;
            for (int i = 0; i < rows.Length; i++)
            {
                int at = IndexOf(work, in rows[i], access);
                if (at < 0) return Refusal.KeyAbsent;
                work.RemoveAt(at);
            }
            removed += rows.Length;
            return Refusal.None;
        }

        // Positional (the echo and diff shape): the removed rows ride along, and EVERY one is checked. 0.2.x checked
        // only when the counts happened to agree and removed blind otherwise — a gap not copied: fewer carried rows than
        // `length` is unverifiable, so it is refused.
        if (op.Length < 1) return Refusal.OpShape;
        if (op.From < 0 || op.From > work.Count - op.Length) return Refusal.IndexOutOfRange;
        if (rows.Length != op.Length) return Refusal.OpShape;
        Span<TRow> held = CollectionsMarshal.AsSpan(work);
        for (int i = 0; i < rows.Length; i++)
            if (!Same(in held[op.From + i], in rows[i], access)) return Refusal.IdentityMismatch;
        work.RemoveRange(op.From, op.Length);
        removed += op.Length;
        return Refusal.None;
    }

    /// <summary>The positional MOV: <c>to</c> is "insert before ORIGINAL index <c>to</c>" (§3.8's proof), so the block
    /// lands at <c>to &gt; from ? to − length : to</c>. A <c>to</c> strictly inside the block names no place and is
    /// refused (0.2.x caught only the negative case and moved such a block to the head). Rotated in place — two
    /// reversals and a third — so a move allocates nothing.</summary>
    static Refusal Move<TRow>(List<TRow> work, in Op op, ref int moved)
    {
        int from = op.From, length = op.Length, to = op.To, count = work.Count;
        if (length < 1 || op.Anchor != -1 || op.ItemsCount != 0) return Refusal.OpShape;
        if (from < 0 || from > count - length || to < 0 || to > count) return Refusal.IndexOutOfRange;
        if (to > from && to < from + length) return Refusal.OpShape;
        int at = to > from ? to - length : to;
        Span<TRow> rows = CollectionsMarshal.AsSpan(work);
        if (at > from) RotateLeft(rows.Slice(from, at + length - from), length);
        else if (at < from) RotateLeft(rows.Slice(at, from + length - at), from - at);
        moved += length;
        return Refusal.None;
    }

    static void RotateLeft<T>(Span<T> span, int by)
    {
        span[..by].Reverse();
        span[by..].Reverse();
        span.Reverse();
    }

    /// <summary>The keyed MOV (the desktop REQUEST shape, and Wavee's own): every carried row is found (none twice),
    /// lifted out together, and the block lands — in the op's order — at the placement resolved against the list
    /// WITHOUT the moved rows (0.2.x's rule; the reorders captures agree: request 467's three rows landed right after its
    /// anchor, exactly where the three positional echoes put them).</summary>
    static Refusal MoveKeyed<TRow, TAccess>(List<TRow> work, in Op op, ReadOnlySpan<TRow> opItems, TAccess access, ref int moved)
        where TAccess : struct, IRowAccess<TRow>
    {
        ReadOnlySpan<TRow> rows = opItems.Slice(op.ItemsStart, op.ItemsCount);
        if (rows.IsEmpty || op.Place == Place.Index) return Refusal.OpShape;
        if (op.Place == Place.AfterItem && op.Anchor < 0) return Refusal.OpShape;

        Span<int> at = rows.Length <= 64 ? stackalloc int[rows.Length] : new int[rows.Length];
        Span<TRow> held = CollectionsMarshal.AsSpan(work);
        for (int k = 0; k < rows.Length; k++)
        {
            int found = -1;
            for (int i = 0; i < held.Length && found < 0; i++)
            {
                if (!Same(in held[i], in rows[k], access)) continue;
                bool claimed = false;
                for (int j = 0; j < k && !claimed; j++) claimed = at[j] == i;
                if (!claimed) found = i;
            }
            if (found < 0) return Refusal.KeyAbsent;
            at[k] = found;
        }

        var block = new TRow[rows.Length];
        for (int k = 0; k < rows.Length; k++) block[k] = held[at[k]];
        Span<int> descending = rows.Length <= 64 ? stackalloc int[rows.Length] : new int[rows.Length];
        at.CopyTo(descending);
        descending.Sort();
        for (int k = descending.Length - 1; k >= 0; k--) work.RemoveAt(descending[k]);

        int dest;
        switch (op.Place)
        {
            case Place.First: dest = 0; break;
            case Place.Last: dest = work.Count; break;
            default:
                {
                    int anchor = IndexOf(work, in opItems[op.Anchor], access);
                    if (anchor < 0) return Refusal.AnchorAbsent;             // absent, or one of the rows being moved
                    dest = anchor + 1;
                    break;
                }
        }
        work.InsertRange(dest, (ReadOnlySpan<TRow>)block);
        moved += rows.Length;
        return Refusal.None;
    }

    /// <summary>UPDATE_ITEM_ATTRIBUTES — RULE-DERIVED (no capture has shown one). The reading applied: the row at
    /// <c>index</c> (the list as the preceding ops left it) takes <c>new_attributes.values</c> for the attributes it
    /// names and loses those in <c>new_attributes.no_value</c>; when <c>old_attributes</c> is present the row must hold
    /// it first. The op names its row only by index, so the old-state check is the only identity it has.</summary>
    static Refusal ApplyItem<TRow, TAccess>(List<TRow> work, in Op op, ReadOnlySpan<TRow> opItems, TAccess access, ref int updates)
        where TAccess : struct, IRowAccess<TRow>
    {
        if (op.ItemsCount < 1) return Refusal.OpShape;
        if ((op.Set | op.Unset) == ItemAttrs.None || (op.Set & op.Unset) != ItemAttrs.None) return Refusal.OpShape;
        if ((uint)op.From >= (uint)work.Count) return Refusal.IndexOutOfRange;
        Span<TRow> held = CollectionsMarshal.AsSpan(work);
        ref TRow row = ref held[op.From];
        if ((op.OldSet | op.OldUnset) != ItemAttrs.None)
        {
            if (op.ItemsCount < 2) return Refusal.OpShape;
            if (!access.Holds(in row, in opItems[op.ItemsStart + 1], op.OldSet, op.OldUnset)) return Refusal.IdentityMismatch;
        }
        if (!access.TryPatch(ref row, in opItems[op.ItemsStart], op.Set, op.Unset)) return Refusal.PatchRefused;
        updates++;
        return Refusal.None;
    }

    /// <summary>Fold one header change into the batch's: a later op wins, a set clears an earlier unset and back.</summary>
    static void Merge(ref ListAttributeChange change, in ListAttributeChange next)
    {
        string? name = change.Name, description = change.Description;
        ListAttrs unset = change.Unset;
        if (next.Name is not null) { name = next.Name; unset &= ~ListAttrs.Name; }
        if ((next.Unset & ListAttrs.Name) != 0) { name = null; unset |= ListAttrs.Name; }
        if (next.Description is not null) { description = next.Description; unset &= ~ListAttrs.Description; }
        if ((next.Unset & ListAttrs.Description) != 0) { description = null; unset |= ListAttrs.Description; }
        change = new ListAttributeChange(name, description, unset);
    }

    // ── 3. the decoder ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a <c>/diff</c> answer says about the list the caller holds.</summary>
    public enum Answer : byte
    {
        /// <summary>The list stands: the empty body of a bare 304, <c>up_to_date</c>, a diff with no ops and
        /// <c>from == to</c>, or nothing actionable (the retired <c>DiffVerdict</c>'s reading).</summary>
        Unchanged,
        /// <summary>A diff to replay: <see cref="DiffAnswer.Batch"/> from <see cref="DiffAnswer.From"/> to
        /// <see cref="DiffAnswer.To"/>. The caller checks <c>From</c> is the revision it holds before applying.</summary>
        Replay,
        /// <summary>The answer carries <c>contents</c> (and no diff): decode it as a full read.</summary>
        Contents,
        /// <summary>Read the list in full; <see cref="DiffAnswer.Why"/> says why.</summary>
        FullRead,
    }

    /// <summary>A decoded <c>/diff</c> answer. <see cref="From"/>/<see cref="To"/> are in the wire spelling
    /// <c>{counter},{hex}</c> (<c>Spotify.Api.FormatRevision</c>) — the spelling the list head stores.</summary>
    public readonly record struct DiffAnswer(Answer Answer, Refusal Why, string? From, string? To, Batch Batch);

    /// <summary>A decoded dealer <c>PlaylistModificationInfo</c>. <see cref="Ok"/> false: the frame or its new revision
    /// did not parse (mark the list dirty; never store anything). <see cref="Why"/> not None: the ops are not
    /// replayable here (mark dirty). A head-only push decodes Ok with no ops and no <see cref="ParentRevision"/>.</summary>
    public readonly record struct PushAnswer(bool Ok, string? Uri, string? NewRevision, string? ParentRevision, Batch Batch, Refusal Why)
    {
        /// <summary>The <c>hasOps</c> <see cref="ListPush.Decide"/> takes: ops that parsed and that this build expresses.</summary>
        public bool HasReplayableOps => Ok && Why == Refusal.None && Batch.Ops.Length > 0;
    }

    /// <summary>A <c>/diff</c> answer (<c>SelectedListContent</c>, already un-zstd'd) → what to do with it. Field 20
    /// is read FIRST and wins outright (plan §2: 0.2.x never checked it on a /diff answer — bug A1's shape); then
    /// <c>multiple_heads</c>; then contradictions; then <c>up_to_date</c>; then the diff; then <c>contents</c>.</summary>
    public static DiffAnswer DecodeDiff(ReadOnlySpan<byte> selectedListContent)
    {
        if (selectedListContent.IsEmpty) return new(Answer.Unchanged, Refusal.None, null, null, Batch.Empty);   // the bare 304
        if (!Framed(selectedListContent)) return Full(Refusal.Malformed);
        bool resync = false, multipleHeads = false, upToDate = false, contents = false, sawDiff = false;
        int diffs = 0;
        ReadOnlySpan<byte> diff = default;
        var r = new Spotify.Decode.ProtoReader(selectedListContent);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 5 && wire == 2) { r.Skip(); contents = true; }
            else if (field == 6 && wire == 2) { diff = r.Bytes(); sawDiff = true; diffs++; }
            else if (field == 9 && wire == 0) multipleHeads |= r.Bool();
            else if (field == 10 && wire == 0) upToDate |= r.Bool();
            else if (field == 20 && wire == 0) resync |= r.Bool();
            else r.Skip();
        }
        if (resync) return Full(Refusal.Resync);
        if (multipleHeads) return Full(Refusal.MultipleHeads);
        if (diffs > 1 || (sawDiff && contents)) return Full(Refusal.Contradictory);
        if (!sawDiff)
        {
            if (upToDate) return new(Answer.Unchanged, Refusal.None, null, null, Batch.Empty);
            return contents ? new(Answer.Contents, Refusal.None, null, null, Batch.Empty)
                            : new(Answer.Unchanged, Refusal.None, null, null, Batch.Empty);
        }

        var b = new Builder();
        ReadOnlySpan<byte> fromBytes = default, toBytes = default;
        bool sawFrom = false, sawTo = false;
        if (!Framed(diff)) return Full(Refusal.Malformed);
        var d = new Spotify.Decode.ProtoReader(diff);
        while (d.Next())
        {
            int field = d.Field, wire = d.Wire;
            if (field == 1 && wire == 2) { fromBytes = d.Bytes(); sawFrom = true; }
            else if (field == 3 && wire == 2) { toBytes = d.Bytes(); sawTo = true; }
            else if (field == 2 && wire == 2)
            {
                Refusal why = b.ReadOp(d.Bytes());
                if (why != Refusal.None) return Full(why);
            }
            else return Full(Refusal.UnknownField);
        }
        string? from = sawFrom ? Revision(fromBytes) : null, to = sawTo ? Revision(toBytes) : null;
        if (from is null || to is null) return Full(Refusal.Malformed);
        if (b.OpCount == 0)
        {
            // The trivial diff (from == to, 0 ops) is "unchanged" in every capture. A 0-op diff that ADVANCES the
            // revision was never seen: adopting its `to` would store a revision nothing was checked against.
            return string.Equals(from, to, StringComparison.Ordinal)
                ? new(Answer.Unchanged, Refusal.None, from, to, Batch.Empty)
                : new(Answer.FullRead, Refusal.Unobserved, from, to, Batch.Empty);
        }
        if (upToDate) return new(Answer.FullRead, Refusal.Contradictory, from, to, Batch.Empty);
        return new(Answer.Replay, Refusal.None, from, to, b.Build());

        static DiffAnswer Full(Refusal why) => new(Answer.FullRead, why, null, null, Batch.Empty);
    }

    /// <summary>A dealer <c>PlaylistModificationInfo</c> body → its uri, revisions and ops. Fields 5/6/8/9 (commit
    /// instant, server host, two unnamed varints) are read past — documented in the proto, meaningless here.</summary>
    public static PushAnswer DecodePush(ReadOnlySpan<byte> modificationInfo)
    {
        if (modificationInfo.IsEmpty || !Framed(modificationInfo))
            return new(false, null, null, null, Batch.Empty, Refusal.Malformed);
        var b = new Builder();
        string? uri = null, newRevision = null, parentRevision = null;
        bool sawNew = false, sawParent = false;
        Refusal why = Refusal.None;
        var r = new Spotify.Decode.ProtoReader(modificationInfo);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 1 && wire == 2) uri = Encoding.UTF8.GetString(r.Bytes());
            else if (field == 2 && wire == 2) { newRevision = Revision(r.Bytes()); sawNew = true; }
            else if (field == 3 && wire == 2) { parentRevision = Revision(r.Bytes()); sawParent = true; }
            else if (field == 4 && wire == 2)
            {
                ReadOnlySpan<byte> op = r.Bytes();
                if (why == Refusal.None) why = b.ReadOp(op);
            }
            else r.Skip();
        }
        if (!sawNew || newRevision is null) return new(false, uri, null, null, Batch.Empty, Refusal.Malformed);
        if (sawParent && parentRevision is null && why == Refusal.None) why = Refusal.Malformed;
        return new(true, uri, newRevision, parentRevision, why == Refusal.None ? b.Build() : Batch.Empty, why);
    }

    /// <summary>A <c>ListChanges</c> request body → every delta's ops, flattened in order. For Wavee's OWN outgoing
    /// body (<c>Spotify.Encode.PlaylistChanges</c>) — the keyed forms, replayed as the optimistic apply — and for the
    /// captured requests the tests hold as ground truth. <paramref name="baseRevision"/> is field 1 in wire spelling.</summary>
    public static bool TryDecodeChanges(ReadOnlySpan<byte> listChanges, out Batch batch, out string? baseRevision, out Refusal why)
    {
        batch = Batch.Empty;
        baseRevision = null;
        why = Refusal.Malformed;
        if (listChanges.IsEmpty || !Framed(listChanges)) return false;
        var b = new Builder();
        var r = new Spotify.Decode.ProtoReader(listChanges);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 1 && wire == 2) baseRevision = Revision(r.Bytes());
            else if (field == 2 && wire == 2)
            {
                ReadOnlySpan<byte> delta = r.Bytes();
                if (!Framed(delta)) { why = Refusal.Malformed; return false; }
                var d = new Spotify.Decode.ProtoReader(delta);
                while (d.Next())
                {
                    if (d.Field == 2 && d.Wire == 2)
                    {
                        why = b.ReadOp(d.Bytes());
                        if (why != Refusal.None) return false;
                    }
                    else d.Skip();                                   // base_version, info
                }
            }
            else r.Skip();
        }
        why = Refusal.None;
        batch = b.Build();
        return true;
    }

    /// <summary>A full read's <c>contents</c> (<c>SelectedListContent</c> field 5) → its rows, with the window it
    /// answers (<paramref name="pos"/>, <paramref name="truncated"/>) and the answer's revision. The same item reading
    /// the ops use, so a caller can compare a replayed list with a read one structurally ("a changed revision can carry
    /// byte-identical contents", §2). False when the frame does not parse or carries no <c>contents</c>.</summary>
    public static bool TryDecodeContents(ReadOnlySpan<byte> selectedListContent, out WireItem[] items, out int pos,
        out bool truncated, out string? revision)
    {
        items = [];
        pos = 0;
        truncated = false;
        revision = null;
        if (selectedListContent.IsEmpty || !Framed(selectedListContent)) return false;
        var rows = new List<WireItem>();
        bool sawContents = false;
        var r = new Spotify.Decode.ProtoReader(selectedListContent);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 1 && wire == 2) revision = Revision(r.Bytes());
            else if (field == 5 && wire == 2)
            {
                ReadOnlySpan<byte> contents = r.Bytes();
                if (!Framed(contents)) return false;
                sawContents = true;
                var c = new Spotify.Decode.ProtoReader(contents);
                while (c.Next())
                {
                    int f = c.Field, w = c.Wire;
                    if (f == 1 && w == 0) pos = c.Int32();
                    else if (f == 2 && w == 0) truncated = c.Bool();
                    else if (f == 3 && w == 2)
                    {
                        if (ReadItem(c.Bytes(), requireUri: true, out var item, out _) != Refusal.None) return false;
                        rows.Add(item);
                    }
                    else c.Skip();
                }
            }
            else r.Skip();
        }
        if (!sawContents) return false;
        items = rows.ToArray();
        return true;
    }

    /// <summary>Accumulates one batch's ops, rows and header changes while a decode walks the ops.</summary>
    sealed class Builder
    {
        readonly List<Op> _ops = [];
        readonly List<WireItem> _items = [];
        readonly List<ListAttributeChange> _lists = [];

        public int OpCount => _ops.Count;

        public Batch Build() => _ops.Count == 0 ? Batch.Empty : new Batch(_ops.ToArray(), _items.ToArray(), _lists.ToArray());

        /// <summary>One <c>Op</c> message. Its kind must name exactly its one body; any other field refuses.</summary>
        public Refusal ReadOp(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            int kind = 0, bodyField = 0, bodies = 0;
            ReadOnlySpan<byte> body = default;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 0) kind = r.Int32();
                else if (field is >= 2 and <= 6 && wire == 2) { body = r.Bytes(); bodyField = field; bodies++; }
                else return Refusal.UnknownField;
            }
            if (bodies != 1 || kind != bodyField) return Refusal.OpKind;   // Op.Kind ADD=2 … UPDATE_LIST=6 is its body's field number
            return kind switch
            {
                2 => Add(body),
                3 => Rem(body),
                4 => Mov(body),
                5 => UpdateItem(body),
                6 => UpdateList(body),
                _ => Refusal.OpKind,
            };
        }

        Refusal Add(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            int from = 0, start = _items.Count;
            bool hasFrom = false, first = false, last = false;
            ReadOnlySpan<byte> after = default;
            int anchors = 0;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 0) { from = r.Int32(); hasFrom = true; }
                else if (field == 2 && wire == 2)
                {
                    Refusal why = ReadItem(r.Bytes(), requireUri: true, out var item, out _);
                    if (why != Refusal.None) return why;
                    _items.Add(item);
                }
                else if (field == 4 && wire == 0) last = r.Bool();
                else if (field == 5 && wire == 0) first = r.Bool();
                else if (field == 6 && wire == 2) return Refusal.Unobserved;                     // add_before_item
                else if (field == 7 && wire == 2) { after = r.Bytes(); anchors++; }              // add_after_item (reorders 511)
                else return Refusal.UnknownField;
            }
            int count = _items.Count - start;
            if (count == 0 || anchors > 1) return Refusal.OpShape;

            Place place;
            int anchor = -1;
            if (anchors == 1)
            {
                // An anchor is the whole placement: beside an index or an end flag nothing says which one wins.
                if (hasFrom || first || last) return Refusal.OpShape;
                Refusal why = ReadItem(after, requireUri: true, out var anchorItem, out _);
                if (why != Refusal.None) return why;
                anchor = _items.Count;
                _items.Add(anchorItem);
                place = Place.AfterItem;
            }
            else if (first) place = Place.First;                     // add_first > add_last > from_index (§2, kept)
            else if (last) place = Place.Last;
            else if (hasFrom) place = Place.Index;
            else return Refusal.OpShape;
            _ops.Add(new Op(Kind.Add, From: place == Place.Index ? from : -1, Place: place, Anchor: anchor, ItemsStart: start, ItemsCount: count));
            return Refusal.None;
        }

        Refusal Rem(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            int from = 0, length = 0, start = _items.Count;
            bool hasFrom = false, hasLength = false, keyed = false;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 0) { from = r.Int32(); hasFrom = true; }
                else if (field == 2 && wire == 0) { length = r.Int32(); hasLength = true; }
                else if (field == 3 && wire == 2)
                {
                    Refusal why = ReadItem(r.Bytes(), requireUri: true, out var item, out _);
                    if (why != Refusal.None) return why;
                    _items.Add(item);
                }
                else if (field == 7 && wire == 0) keyed = r.Bool();
                else return Refusal.UnknownField;
            }
            int count = _items.Count - start;
            if (keyed)
            {
                if (hasFrom || hasLength || count == 0) return Refusal.OpShape;
                _ops.Add(new Op(Kind.Rem, ItemsAsKey: true, ItemsStart: start, ItemsCount: count));
                return Refusal.None;
            }
            if (!hasFrom || !hasLength || length < 1 || count != length) return Refusal.OpShape;
            _ops.Add(new Op(Kind.Rem, From: from, Length: length, ItemsStart: start, ItemsCount: count));
            return Refusal.None;
        }

        Refusal Mov(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            int from = 0, length = 0, to = 0, start = _items.Count;
            bool hasFrom = false, hasLength = false, hasTo = false, first = false, last = false;
            ReadOnlySpan<byte> after = default;
            int anchors = 0;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 0) { from = r.Int32(); hasFrom = true; }
                else if (field == 2 && wire == 0) { length = r.Int32(); hasLength = true; }
                else if (field == 3 && wire == 0) { to = r.Int32(); hasTo = true; }
                else if (field == 4 && wire == 2)
                {
                    Refusal why = ReadItem(r.Bytes(), requireUri: true, out var item, out _);
                    if (why != Refusal.None) return why;
                    _items.Add(item);
                }
                else if (field == 5 && wire == 2) return Refusal.Unobserved;                     // add_before_item
                else if (field == 6 && wire == 2) { after = r.Bytes(); anchors++; }
                else if (field == 7 && wire == 0) first = r.Bool();
                else if (field == 8 && wire == 0) last = r.Bool();
                else return Refusal.UnknownField;
            }
            int count = _items.Count - start;
            int placements = anchors + (first ? 1 : 0) + (last ? 1 : 0);

            if (count == 0)
            {
                // Positional (every echo, every server diff): all three indices, nothing else.
                if (!hasFrom || !hasLength || !hasTo || placements != 0 || length < 1) return Refusal.OpShape;
                _ops.Add(new Op(Kind.Mov, From: from, Length: length, To: to));
                return Refusal.None;
            }

            // Keyed (the request shape): rows + exactly one placement, no indices.
            if (hasFrom || hasLength || hasTo || placements != 1) return Refusal.OpShape;
            int anchor = -1;
            Place place = first ? Place.First : last ? Place.Last : Place.AfterItem;
            if (place == Place.AfterItem)
            {
                Refusal why = ReadItem(after, requireUri: true, out var anchorItem, out _);
                if (why != Refusal.None) return why;
                anchor = _items.Count;
                _items.Add(anchorItem);
            }
            _ops.Add(new Op(Kind.Mov, Place: place, Anchor: anchor, ItemsAsKey: true, ItemsStart: start, ItemsCount: count));
            return Refusal.None;
        }

        /// <summary>UPDATE_ITEM_ATTRIBUTES { index, new_attributes, old_attributes } — rule-derived (§3.10).</summary>
        Refusal UpdateItem(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            int index = 0;
            bool hasIndex = false, hasNew = false, hasOld = false;
            ReadOnlySpan<byte> newState = default, oldState = default;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 0) { index = r.Int32(); hasIndex = true; }
                else if (field == 2 && wire == 2) { newState = r.Bytes(); hasNew = true; }
                else if (field == 3 && wire == 2) { oldState = r.Bytes(); hasOld = true; }
                else return Refusal.UnknownField;
            }
            if (!hasIndex || !hasNew) return Refusal.OpShape;
            Refusal why = ItemState(newState, out var values, out var set, out var unset);
            if (why != Refusal.None) return why;
            WireItem old = default;
            ItemAttrs oldSet = ItemAttrs.None, oldUnset = ItemAttrs.None;
            if (hasOld)
            {
                why = ItemState(oldState, out old, out oldSet, out oldUnset);
                if (why != Refusal.None) return why;
            }
            int start = _items.Count;
            _items.Add(values);
            if (hasOld) _items.Add(old);
            _ops.Add(new Op(Kind.UpdateItemAttributes, From: index, ItemsStart: start, ItemsCount: hasOld ? 2 : 1,
                Set: set, Unset: unset, OldSet: oldSet, OldUnset: oldUnset));
            return Refusal.None;
        }

        /// <summary>UPDATE_LIST_ATTRIBUTES { new_attributes, old_attributes } — capture-proven for name + description
        /// (§3.10). <c>old_attributes</c> is well-formedness-checked and otherwise not consulted: the header's own
        /// answer is the revision the batch is checked against.</summary>
        Refusal UpdateList(ReadOnlySpan<byte> bytes)
        {
            if (!Framed(bytes)) return Refusal.Malformed;
            bool hasNew = false;
            ReadOnlySpan<byte> newState = default;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 2) { newState = r.Bytes(); hasNew = true; }
                else if (field == 2 && wire == 2) { if (!Framed(r.Bytes())) return Refusal.Malformed; }
                else return Refusal.UnknownField;
            }
            if (!hasNew || !Framed(newState)) return hasNew ? Refusal.Malformed : Refusal.OpShape;

            string? name = null, description = null;
            ListAttrs unset = ListAttrs.None;
            var s = new Spotify.Decode.ProtoReader(newState);
            while (s.Next())
            {
                int field = s.Field, wire = s.Wire;
                if (field == 1 && wire == 2)                                  // values: ListAttributes
                {
                    ReadOnlySpan<byte> values = s.Bytes();
                    if (!Framed(values)) return Refusal.Malformed;
                    var v = new Spotify.Decode.ProtoReader(values);
                    while (v.Next())
                    {
                        if (v.Field == 1 && v.Wire == 2) name = Encoding.UTF8.GetString(v.Bytes());
                        else if (v.Field == 2 && v.Wire == 2) description = Encoding.UTF8.GetString(v.Bytes());
                        else return Refusal.ListAttribute;
                    }
                }
                else if (field == 2 && wire == 0)                             // no_value: ListAttributeKind
                {
                    ListAttrs kind = ListKindOf(s.Int32());
                    if (kind == ListAttrs.None) return Refusal.ListAttribute;
                    unset |= kind;
                }
                else if (field == 2 && wire == 2)                             // no_value, packed
                {
                    ReadOnlySpan<byte> packed = s.Bytes();
                    int p = 0;
                    while (p < packed.Length)
                    {
                        if (!Var(packed, ref p, out ulong value)) return Refusal.Malformed;
                        ListAttrs kind = ListKindOf((int)value);
                        if (kind == ListAttrs.None) return Refusal.ListAttribute;
                        unset |= kind;
                    }
                }
                else return Refusal.UnknownField;
            }
            if ((name is not null && (unset & ListAttrs.Name) != 0) || (description is not null && (unset & ListAttrs.Description) != 0))
                return Refusal.OpShape;
            if (name is null && description is null && unset == ListAttrs.None) return Refusal.OpShape;
            _lists.Add(new ListAttributeChange(name, description, unset));
            _ops.Add(new Op(Kind.UpdateListAttributes, Change: _lists.Count - 1));
            return Refusal.None;

            static ListAttrs ListKindOf(int wire) => wire switch { 1 => ListAttrs.Name, 2 => ListAttrs.Description, _ => ListAttrs.None };
        }

        /// <summary>An <c>ItemAttributesPartialState</c> { values, no_value[] } → the values as a uri-less row and the
        /// set / unset masks. Anything outside <see cref="ItemAttrs"/> refuses.</summary>
        static Refusal ItemState(ReadOnlySpan<byte> bytes, out WireItem values, out ItemAttrs set, out ItemAttrs unset)
        {
            values = new WireItem("");
            set = ItemAttrs.None;
            unset = ItemAttrs.None;
            if (!Framed(bytes)) return Refusal.Malformed;
            var r = new Spotify.Decode.ProtoReader(bytes);
            while (r.Next())
            {
                int field = r.Field, wire = r.Wire;
                if (field == 1 && wire == 2)
                {
                    Refusal why = ReadAttributes(r.Bytes(), "", out values, out bool other);
                    if (why != Refusal.None) return why;
                    if (other) return Refusal.ItemAttribute;
                    set = values.Present;
                }
                else if (field == 2 && wire == 0)
                {
                    ItemAttrs kind = ItemKindOf(r.Int32());
                    if (kind == ItemAttrs.None) return Refusal.ItemAttribute;
                    unset |= kind;
                }
                else if (field == 2 && wire == 2)
                {
                    ReadOnlySpan<byte> packed = r.Bytes();
                    int p = 0;
                    while (p < packed.Length)
                    {
                        if (!Var(packed, ref p, out ulong value)) return Refusal.Malformed;
                        ItemAttrs kind = ItemKindOf((int)value);
                        if (kind == ItemAttrs.None) return Refusal.ItemAttribute;
                        unset |= kind;
                    }
                }
                else return Refusal.UnknownField;
            }
            return Refusal.None;

            static ItemAttrs ItemKindOf(int wire) => wire switch
            {
                1 => ItemAttrs.AddedBy,
                2 => ItemAttrs.Timestamp,
                10 => ItemAttrs.Public,
                _ => ItemAttrs.None,
            };
        }
    }

    /// <summary>One <c>Item</c> { uri, attributes }. An item's own unnamed fields are data, not shape: they are read
    /// past, as the full-read decoder reads past them.</summary>
    static Refusal ReadItem(ReadOnlySpan<byte> bytes, bool requireUri, out WireItem item, out bool otherAttributes)
    {
        item = default;
        otherAttributes = false;
        if (!Framed(bytes)) return Refusal.Malformed;
        string? uri = null;
        ReadOnlySpan<byte> attributes = default;
        bool sawAttributes = false;
        var r = new Spotify.Decode.ProtoReader(bytes);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 1 && wire == 2) uri = Encoding.UTF8.GetString(r.Bytes());
            else if (field == 2 && wire == 2) { attributes = r.Bytes(); sawAttributes = true; }
            else r.Skip();
        }
        if (requireUri && string.IsNullOrEmpty(uri)) return Refusal.Malformed;
        uri ??= "";
        if (!sawAttributes) { item = new WireItem(uri); return Refusal.None; }
        return ReadAttributes(attributes, uri, out item, out otherAttributes);
    }

    /// <summary>An <c>ItemAttributes</c> → a row. <paramref name="other"/> reports any attribute outside
    /// <see cref="ItemAttrs"/> (item_id, seen_at, format_attributes, an unnamed field such as 17) — data on an ADD's
    /// row, a refusal on an UPDATE_ITEM's values.</summary>
    static Refusal ReadAttributes(ReadOnlySpan<byte> bytes, string uri, out WireItem item, out bool other)
    {
        item = new WireItem(uri);
        other = false;
        if (!Framed(bytes)) return Refusal.Malformed;
        string? itemId = null, addedBy = null;
        long timestamp = 0;
        bool isPublic = false;
        byte chartStatus = 0;
        ushort chartPos = 0, chartPrev = 0;
        ItemAttrs present = ItemAttrs.None;
        var r = new Spotify.Decode.ProtoReader(bytes);
        while (r.Next())
        {
            int field = r.Field, wire = r.Wire;
            if (field == 1 && wire == 2) { addedBy = Encoding.UTF8.GetString(r.Bytes()); present |= ItemAttrs.AddedBy; }
            else if (field == 2 && wire == 0) { timestamp = (long)r.Varint(); present |= ItemAttrs.Timestamp; }
            else if (field == 10 && wire == 0) { isPublic = r.Bool(); present |= ItemAttrs.Public; }
            else if (field == 12 && wire == 2)
            {
                ReadOnlySpan<byte> id = r.Bytes();
                itemId = id.IsEmpty ? null : Convert.ToHexStringLower(id);
                other = true;
            }
            else if (field == 11 && wire == 2)
            {
                // format_attributes: a chart row's triple, keyed exactly as Spotify.Decode.PlaylistRevision keys it.
                ReadOnlySpan<byte> attribute = r.Bytes();
                if (!Framed(attribute)) return Refusal.Malformed;
                new Spotify.Decode.ProtoReader(attribute).Fields(1, 2, out var key, out var value);
                if (Spotify.Decode.Is(key, "status")) chartStatus = Spotify.Decode.ChartStatus(value);
                else if (Spotify.Decode.Is(key, "current_pos")) chartPos = Clamp(Spotify.Decode.Number(value));
                else if (Spotify.Decode.Is(key, "previous_pos")) chartPrev = Clamp(Spotify.Decode.Number(value));
                other = true;
            }
            else { r.Skip(); other = true; }
        }
        item = new WireItem(uri, itemId, addedBy, timestamp, isPublic, present, chartStatus, chartPos, chartPrev);
        return Refusal.None;

        static ushort Clamp(long n) => n is < 0 or > ushort.MaxValue ? (ushort)0 : (ushort)n;
    }

    /// <summary>A revision's bytes → its wire spelling <c>{counter},{hex}</c>; null under five bytes (not a revision).</summary>
    static string? Revision(ReadOnlySpan<byte> revision)
    {
        if (revision.Length < 5 || revision.Length > 64) return null;
        Span<char> chars = stackalloc char[16 + 2 * 64];
        int n = Spotify.Api.FormatRevision(revision, chars);
        return n == 0 ? null : new string(chars[..n]);
    }

    /// <summary>STRICT: every tag and length in this frame is well-formed and the walk ends exactly at its end.
    /// <c>ProtoReader</c> ends a garbled walk silently (right for a full read, which keeps what it decoded); a replay must
    /// know, so every message the decoder opens is checked here first.</summary>
    static bool Framed(ReadOnlySpan<byte> frame)
    {
        int p = 0;
        while (p < frame.Length)
        {
            if (!Var(frame, ref p, out ulong tag)) return false;
            if ((tag >> 3) == 0) return false;
            switch ((int)(tag & 7))
            {
                case 0: if (!Var(frame, ref p, out _)) return false; break;
                case 1: if (frame.Length - p < 8) return false; p += 8; break;
                case 2:
                    if (!Var(frame, ref p, out ulong length) || length > (ulong)(frame.Length - p)) return false;
                    p += (int)length;
                    break;
                case 5: if (frame.Length - p < 4) return false; p += 4; break;
                default: return false;
            }
        }
        return true;
    }

    static bool Var(ReadOnlySpan<byte> bytes, ref int p, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (p >= bytes.Length) return false;
            byte b = bytes[p++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}

/// <summary>WHEN AN OPEN PAINTS, AND WHEN IT ASKS FIRST (plan §2, §3.3). Correctness is the revision + <c>/diff</c> on
/// open and on reconnect; the dealer is an optimisation. Freshness stamps are memory-only, so the first open of a list in
/// a session always revalidates. The playlist page, the library pane and the queue's context all ask this. PURE.</summary>
public static class ListFreshness
{
    /// <summary>A list revalidated within this long, and not dirtied since, paints without asking.</summary>
    public const int WindowMs = 5 * 60 * 1000;

    /// <summary>The most an open waits on a blocking revalidation before it paints what it has — so yesterday's copy is
    /// never painted-then-swapped, and a slow network never holds a page blank.</summary>
    public const int BlockingBudgetMs = 1500;

    public enum Verdict : byte
    {
        /// <summary>Fresh: paint the held list; no request.</summary>
        Paint,
        /// <summary>Paint the held list now and revalidate behind it — the answer is almost certainly "unchanged".</summary>
        PaintThenRevalidate,
        /// <summary>Revalidate FIRST, waiting at most <see cref="BlockingBudgetMs"/>; then paint (the diff's result, or
        /// the held list if the budget ran out — the diff still lands after).</summary>
        RevalidateThenPaint,
        /// <summary>Nothing held: the full read.</summary>
        FullRead,
    }

    /// <summary>The open rule. <paramref name="revalidatedAtMs"/> is when this list was last revalidated IN THIS
    /// SESSION (the caller's monotonic ms clock; 0 = never — a real stamp is never 0).
    /// <list type="number">
    /// <item>no baseline ⇒ <see cref="Verdict.FullRead"/>;</item>
    /// <item>never revalidated this session ⇒ <see cref="Verdict.RevalidateThenPaint"/> — the baseline came off disk and
    /// may be days old;</item>
    /// <item>dirty (a push said it moved and it was not applied) ⇒ <see cref="Verdict.RevalidateThenPaint"/>;</item>
    /// <item>inside <see cref="WindowMs"/> ⇒ <see cref="Verdict.Paint"/>;</item>
    /// <item>past the window: a ROLLING identity (a daylist — its served contents turn over on the server's clock, and
    /// its refresh push is head-only) ⇒ <see cref="Verdict.RevalidateThenPaint"/>; any other list — verified this
    /// session and never dirtied, so the dealer saw no change — ⇒ <see cref="Verdict.PaintThenRevalidate"/>.</item>
    /// </list>
    /// A clock that went backwards reads as past the window.</summary>
    public static Verdict Decide(bool hasBaseline, bool dirty, int revalidatedAtMs, int nowMs, bool rolling)
    {
        if (!hasBaseline) return Verdict.FullRead;
        if (revalidatedAtMs == 0 || dirty) return Verdict.RevalidateThenPaint;
        int age = unchecked(nowMs - revalidatedAtMs);
        if (age >= 0 && age < WindowMs) return Verdict.Paint;
        return rolling ? Verdict.RevalidateThenPaint : Verdict.PaintThenRevalidate;
    }

    /// <summary>How a revalidation ended.</summary>
    public enum Outcome : byte { Unchanged, Replayed, FullRead, Failed }

    /// <summary>Must a ROLLING identity re-ask its header now? After any outcome that did not re-read the list in full:
    /// an "unchanged" (or a replay of row ops) says nothing about a header that turns over on the server's clock (§2).
    /// A failed revalidation re-asks nothing — the next revalidation will.</summary>
    public static bool ReaskHeader(bool rolling, Outcome outcome)
        => rolling && outcome is Outcome.Unchanged or Outcome.Replayed;
}

/// <summary>WHAT A DEALER PUSH FOR ONE LIST DOES (plan §3.3, L3). A push is an optimisation over the revision gate,
/// never a source of truth: a head it did not come with ops for is NEVER stored — the list is marked dirty and the next
/// open (or, when it is open, a revalidation now) asks <c>/diff</c>. PURE.</summary>
public static class ListPush
{
    public enum Verdict : byte
    {
        /// <summary>The push names the revision already held: an echo of our own write, or the second arrival.</summary>
        Drop,
        /// <summary>Remember the list is stale; the next open revalidates (anti-herd: no request now).</summary>
        MarkDirty,
        /// <summary>Replay the push's ops over the held list (<see cref="PlaylistOps.TryApply{TRow,TAccess}"/>) and store
        /// rows + its new revision together — zero HTTP. A replay that refuses falls back to <see cref="MarkDirty"/>
        /// (or <see cref="RevalidateNow"/> when open).</summary>
        ApplyInPlace,
        /// <summary>The list is on screen: ask <c>/diff</c> now.</summary>
        RevalidateNow,
    }

    /// <summary>A playlist push. <paramref name="hasOps"/> is <see cref="PlaylistOps.PushAnswer.HasReplayableOps"/>;
    /// <paramref name="resident"/>: the list's rows are held as a complete baseline; <paramref name="pendingLocal"/>:
    /// optimistic rows sit on it (only settled membership is replayed and stored).
    /// <list type="number">
    /// <item><paramref name="newRev"/> == <paramref name="storedRev"/> ⇒ <see cref="Verdict.Drop"/>;</item>
    /// <item>head-only (no ops, or no <paramref name="parentRev"/> — every editorial refresh and every bulk ADD's push)
    /// ⇒ dirty, or revalidate now when open;</item>
    /// <item>ops, resident, <paramref name="storedRev"/> == <paramref name="parentRev"/>, nothing pending ⇒
    /// <see cref="Verdict.ApplyInPlace"/>;</item>
    /// <item>anything else (another parent — a missed push between; pending rows; not resident) ⇒ dirty, or revalidate
    /// now when open.</item>
    /// </list>
    /// Revision COUNTERS are never compared: editorial lists push counter 0 on every refresh (captures, §3.8).</summary>
    public static Verdict Decide(string? storedRev, string? parentRev, string newRev, bool hasOps, bool resident, bool pendingLocal, bool open)
    {
        if (!string.IsNullOrEmpty(storedRev) && string.Equals(storedRev, newRev, StringComparison.Ordinal)) return Verdict.Drop;
        Verdict stale = open ? Verdict.RevalidateNow : Verdict.MarkDirty;
        if (!hasOps || string.IsNullOrEmpty(parentRev)) return stale;
        if (resident && !pendingLocal && !string.IsNullOrEmpty(storedRev) && string.Equals(storedRev, parentRev, StringComparison.Ordinal))
            return Verdict.ApplyInPlace;
        return stale;
    }

    /// <summary>A ROOTLIST push. Its live shape is still unobserved (§3.10: its topic, its message, whether it arrives
    /// twice), so it is never applied in place: the revision already held drops it (the v2 and legacy topics arrive as a
    /// pair — dedupe by revision), anything else marks it dirty, or revalidates now when <paramref name="open"/>.</summary>
    public static Verdict DecideRootlist(string? storedRev, string? newRev, bool open)
    {
        if (!string.IsNullOrEmpty(newRev) && string.Equals(storedRev, newRev, StringComparison.Ordinal)) return Verdict.Drop;
        return open ? Verdict.RevalidateNow : Verdict.MarkDirty;
    }
}
