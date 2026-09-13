using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

// ── Incremental playlist sync — the pure ordered-membership op model + applier ────────────────────────────────────────
// Proto-free domain logic: the SpotifyLive layer maps the playlist4 `Op` protos onto these, so this applier stays unit-
// testable without a wire. A playlist's membership is an ordered list keyed by a stable per-row ItemId (a uri may repeat
// and positions drift), which is why remove/reorder rebase keys on ItemId, never on raw indices.

/// <summary>One ordered membership row: the stable per-row <see cref="ItemId"/>, the referenced entity
/// <see cref="ItemUri"/> (joined to the shared Store entity at read), and the per-membership add facts.
/// <paramref name="Chart"/> carries a chart playlist's per-row rank movement (null off a non-chart list, or when the
/// wire's ItemAttributes carried no format_attributes at all).</summary>
public readonly record struct PlaylistMember(string ItemId, string ItemUri, string? AddedBy, long AddedAt, ChartEntry? Chart = null);

/// <summary>List-level attribute patch carried by <see cref="PlaylistOpKind.UpdateList"/> — name, description, cover,
/// collaborative toggle, or per-field clears.</summary>
public sealed record PlaylistListAttributePatch(
    string? Name = null, string? Description = null,
    byte[]? PictureBytes = null, bool ClearPicture = false, bool? Collaborative = null,
    bool ClearName = false, bool ClearDescription = false,
    // playlist4 ListAttributes.deleted_by_owner (field 6) — the wire shape of a REMOTE DELETE. The dealer delivers a
    // playlist-topic UPDATE_LIST new{deleted_by_owner=1}; the sync loop turns that into LibrarySync.ApplyTombstone.
    bool? DeletedByOwner = null);

public enum PlaylistOpKind { Add, Remove, Move, UpdateItem, UpdateList }

/// <summary>A single change to the ordered membership. Index fields are interpreted against the list state produced by
/// all PRECEDING ops in the same batch (the reference /diff semantics).</summary>
public sealed record PlaylistOp(
    PlaylistOpKind Kind,
    int FromIndex = 0,                          // ADD insertion index (when not add_first/add_last) / REM start / positional MOV source / UPDATE_ITEM index
    int Length = 0,                             // REM count / positional MOV count
    int ToIndex = 0,                            // positional MOV destination: pre-removal wire index
    bool AddFirst = false,                      // ADD at head
    bool AddLast = false,                       // ADD at tail
    IReadOnlyList<PlaylistMember>? Items = null,    // ADD payload / UPDATE_ITEM attribute carrier / keyed REM+MOV rows
    bool ItemsAsKey = false,                    // REM/MOV keyed by Items instead of index (the desktop playlist-edit shape)
    PlaylistListAttributePatch? ListPatch = null,   // UPDATE_LIST metadata payload
    bool? ItemPublic = null,                    // UPDATE_ITEM: rootlist public flag
    // Where a KEYED MOV lands (required when Kind==Move && ItemsAsKey) and — for an index ADD — the row the insertion
    // point was derived from at build time (invariant I5: replaying an index op against a moved base recomputes
    // FromIndex from this anchor instead of sending a stale index).
    PlaylistMoveAnchor? Anchor = null,
    // The anchor row's ENTITY uri. Carried only for a keyed MOV, and only because playlist4's `Item` names a row by
    // (uri, attributes.item_id) so the wire needs both; the anchor's IDENTITY is Anchor.AfterItemId alone.
    string? AnchorUri = null);

public static class PlaylistDiffApplier
{
    /// <summary>Applies <paramref name="ops"/> in order to <paramref name="list"/> in place. Throws
    /// <see cref="ArgumentOutOfRangeException"/> on any op that doesn't fit the current list — the caller
    /// treats that as a torn apply and falls back to a full re-fetch (the reference's behavior).</summary>
    public static void Apply(List<PlaylistMember> list, IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Kind)
            {
                case PlaylistOpKind.Add: ApplyAdd(list, op); break;
                case PlaylistOpKind.Remove: ApplyRemove(list, op); break;
                case PlaylistOpKind.Move: ApplyMove(list, op); break;
                case PlaylistOpKind.UpdateItem: ApplyUpdateItem(list, op); break;
                case PlaylistOpKind.UpdateList: break;   // list-level attrs (name/description) belong to the header, not the membership
            }
        }
    }

    static void ApplyAdd(List<PlaylistMember> list, PlaylistOp op)
    {
        var items = Idempotent(list, op.Items ?? Array.Empty<PlaylistMember>());
        int at = op.AddFirst ? 0 : op.AddLast ? list.Count : op.FromIndex;   // add_first > add_last > from_index
        if (at < 0 || at > list.Count) throw new ArgumentOutOfRangeException(nameof(op), $"ADD index {at} out of range [0,{list.Count}]");
        if (items.Count == 0) return;                                       // every item was already present (I6)
        list.InsertRange(at, items);
    }

    /// <summary>I6 — a keyed ADD is idempotent by <c>item_id</c>: an item whose id is ALREADY a membership row is
    /// skipped, so replaying our own echo (or re-applying a pending op on top of a snapshot that already contains it)
    /// cannot duplicate the row. Items without an id are never deduped — the server permits duplicate uris, and a
    /// uri-only ADD carries no identity to compare.</summary>
    static IReadOnlyList<PlaylistMember> Idempotent(List<PlaylistMember> list, IReadOnlyList<PlaylistMember> items)
    {
        List<PlaylistMember>? kept = null;
        for (int k = 0; k < items.Count; k++)
        {
            bool present = false;
            var id = items[k].ItemId;
            if (!string.IsNullOrEmpty(id))
                for (int i = 0; i < list.Count; i++)
                    if (string.Equals(list[i].ItemId, id, StringComparison.Ordinal)) { present = true; break; }
            if (present) { kept ??= Prefix(items, k); continue; }
            kept?.Add(items[k]);
        }
        return kept ?? items;
    }

    static List<PlaylistMember> Prefix(IReadOnlyList<PlaylistMember> items, int count)
    {
        var l = new List<PlaylistMember>(items.Count);
        for (int i = 0; i < count; i++) l.Add(items[i]);
        return l;
    }

    static void ApplyRemove(List<PlaylistMember> list, PlaylistOp op)
    {
        // Keyed REM (items_as_key) — the desktop playlist-edit shape and the rootlist-unfollow shape in one branch.
        // A key WITH an item_id addresses exactly one row: not finding it means our baseline drifted → torn → refetch.
        // A key WITHOUT an item_id (the rootlist unfollow, whose entries have no row ids at all) falls back to the first
        // uri match and stays no-op-on-absent — the optimistic edit already removed it locally, so local absence proves
        // nothing about the server.
        if (op.ItemsAsKey)
        {
            if (op.Items is not { } keys) return;
            for (int k = 0; k < keys.Count; k++)
            {
                var key = keys[k];
                if (!string.IsNullOrEmpty(key.ItemId))
                {
                    int at = IndexOfId(list, key.ItemId);
                    if (at < 0) throw new ArgumentOutOfRangeException(nameof(op), $"keyed REM item_id {key.ItemId} is not in the list");
                    list.RemoveAt(at);
                    continue;
                }
                for (int i = 0; i < list.Count; i++)
                    if (list[i].ItemUri == key.ItemUri) { list.RemoveAt(i); break; }
            }
            return;
        }
        if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"REM [{op.FromIndex},+{op.Length}] out of range (count {list.Count})");
        if (op.Items is { Count: > 0 } items && items.Count == op.Length)
        {
            for (int k = 0; k < items.Count; k++)
                if (!SameMember(list[op.FromIndex + k], items[k]))
                    throw new ArgumentOutOfRangeException(nameof(op), $"REM item mismatch at {op.FromIndex + k}");
        }
        list.RemoveRange(op.FromIndex, op.Length);
    }

    static void ApplyMove(List<PlaylistMember> list, PlaylistOp op)
    {
        if (op.ItemsAsKey) { ApplyKeyedMove(list, op); return; }
        if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV source [{op.FromIndex},+{op.Length}] out of range (count {list.Count})");
        if (op.ToIndex < 0 || op.ToIndex > list.Count)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV dest {op.ToIndex} out of range [0,{list.Count}]");
        var moved = list.GetRange(op.FromIndex, op.Length);
        list.RemoveRange(op.FromIndex, op.Length);
        int at = op.ToIndex > op.FromIndex ? op.ToIndex - op.Length : op.ToIndex;
        if (at < 0)
            throw new ArgumentOutOfRangeException(nameof(op), $"MOV dest {op.ToIndex} inside moved range");
        list.InsertRange(at, moved);
    }

    /// <summary>The item-keyed MOV — the ONLY shape Wavee sends for a playlist reorder, and the shape desktop sends.
    /// Rows are addressed by <c>item_id</c> (a uri fallback covers keys minted before ids were known), the whole
    /// selection is lifted out in one go, and it lands at the anchor resolved against the POST-removal list. An
    /// unresolved row or a vanished anchor is a torn apply: throw so the caller refetches rather than guessing.</summary>
    static void ApplyKeyedMove(List<PlaylistMember> list, PlaylistOp op)
    {
        if (op.Items is not { Count: > 0 } items)
            throw new ArgumentOutOfRangeException(nameof(op), "keyed MOV carries no items");
        if (op.Anchor is not { } anchor)
            throw new ArgumentOutOfRangeException(nameof(op), "keyed MOV carries no anchor");

        var at = new int[items.Count];
        var claimed = new bool[list.Count];
        for (int k = 0; k < items.Count; k++)
        {
            int found = -1;
            var id = items[k].ItemId;
            for (int i = 0; i < list.Count; i++)
            {
                if (claimed[i]) continue;
                bool hit = string.IsNullOrEmpty(id)
                    ? list[i].ItemUri == items[k].ItemUri
                    : string.Equals(list[i].ItemId, id, StringComparison.Ordinal);
                if (hit) { found = i; break; }
            }
            if (found < 0)
                throw new ArgumentOutOfRangeException(nameof(op),
                    $"keyed MOV row {(string.IsNullOrEmpty(id) ? items[k].ItemUri : id)} is not in the list");
            claimed[found] = true;
            at[k] = found;
        }

        // Lift the rows out (descending, so the earlier indices stay valid) but keep them in the op's order — that IS
        // the order they land in.
        var moved = new PlaylistMember[items.Count];
        for (int k = 0; k < items.Count; k++) moved[k] = list[at[k]];
        var descending = (int[])at.Clone();
        Array.Sort(descending);
        for (int i = descending.Length - 1; i >= 0; i--) list.RemoveAt(descending[i]);

        int dest;
        switch (anchor.Kind)
        {
            case PlaylistMoveAnchorKind.First: dest = 0; break;
            case PlaylistMoveAnchorKind.Last: dest = list.Count; break;
            default:
                int a = string.IsNullOrEmpty(anchor.AfterItemId) ? -1 : IndexOfId(list, anchor.AfterItemId!);
                if (a < 0) throw new ArgumentOutOfRangeException(nameof(op), $"keyed MOV anchor {anchor.AfterItemId} is not in the list");
                dest = a + 1;
                break;
        }
        list.InsertRange(dest, moved);
    }

    static int IndexOfId(List<PlaylistMember> list, string itemId)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].ItemId, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    static bool SameMember(PlaylistMember actual, PlaylistMember expected)
        => !string.IsNullOrEmpty(actual.ItemId) && !string.IsNullOrEmpty(expected.ItemId)
            ? string.Equals(actual.ItemId, expected.ItemId, StringComparison.Ordinal)
            : string.Equals(actual.ItemUri, expected.ItemUri, StringComparison.Ordinal);

    static void ApplyUpdateItem(List<PlaylistMember> list, PlaylistOp op)
    {
        if (op.FromIndex < 0 || op.FromIndex >= list.Count) throw new ArgumentOutOfRangeException(nameof(op), $"UPDATE_ITEM index {op.FromIndex} out of range (count {list.Count})");
        if (op.Items is { Count: > 0 } items)
        {
            var updated = items[0];
            list[op.FromIndex] = list[op.FromIndex] with { AddedBy = updated.AddedBy, AddedAt = updated.AddedAt };
        }
    }
}
