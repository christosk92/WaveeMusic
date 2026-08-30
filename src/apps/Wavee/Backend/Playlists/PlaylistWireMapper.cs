using System;
using System.Collections.Generic;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

// The SpotifyLive boundary mapper: playlist4 wire protos → the proto-free domain (PlaylistMember / PlaylistOp). Kept in
// the Backend (not SpotifyLive) so it is unit-tested against crafted protos, exactly like ExtendedMetadataSource.
//
// The OUTBOUND envelope is desktop-verified byte-for-byte (Fixtures/playlist-wire, 2026-08-15): ListChanges{
// base_revision, deltas[ Delta{ ops, info{user, timestamp} } ], want_resulting_revisions, want_sync_result, nonces[1] }.
// Two things that look like omissions are the actual desktop shape: a Delta carries NO base_version of its own (the
// top-level base_revision is the base), and ChangeInfo carries NOTHING but user + timestamp.
public static class PlaylistWireMapper
{
    /// <summary>Project a SelectedListContent into ordered membership + the opaque revision bytes.</summary>
    public static (IReadOnlyList<PlaylistMember> Members, byte[]? Revision) ParseContents(Pl.SelectedListContent slc)
    {
        byte[]? rev = slc.HasRevision ? slc.Revision.ToByteArray() : null;
        var members = new List<PlaylistMember>();
        if (slc.Contents is { } contents)
            foreach (var item in contents.Items)
                members.Add(ToMember(item));
        return (members, rev);
    }

    static PlaylistMember ToMember(Pl.Item item)
    {
        string itemId = "";
        string? addedBy = null;
        long addedAt = 0;
        ChartEntry? chart = null;
        if (item.Attributes is { } a)
        {
            if (a.HasItemId) itemId = Convert.ToHexStringLower(a.ItemId.Span);   // stable per-row key (survives reorder)
            if (a.HasAddedBy) addedBy = a.AddedBy;
            if (a.HasTimestamp) addedAt = a.Timestamp;
            chart = ChartEntryOf(a);
        }
        return new PlaylistMember(itemId, item.Uri, addedBy, addedAt, chart);
    }

    /// <summary>A chart playlist's per-row rank movement off <c>ItemAttributes.format_attributes</c> (desktop-verified:
    /// <c>status</c>/<c>current_pos</c>/<c>previous_pos</c>/<c>rank</c>). Null when the item carries no format_attributes
    /// at all (a non-chart list, or a chart row the wire has not yet stated) or no <c>status</c> key — every captured
    /// example carries one, so its absence means this is not a chart-format attribute bag.</summary>
    static ChartEntry? ChartEntryOf(Pl.ItemAttributes a)
    {
        if (a.FormatAttributes.Count == 0) return null;
        string? status = null;
        int currentPos = 0, previousPos = 0;
        long rank = 0;
        for (int i = 0; i < a.FormatAttributes.Count; i++)
        {
            var item = a.FormatAttributes[i];
            if (!item.HasKey || !item.HasValue) continue;
            switch (item.Key)
            {
                case "status": status = item.Value; break;
                case "current_pos": int.TryParse(item.Value, NumberStyles.None, CultureInfo.InvariantCulture, out currentPos); break;
                // Absent on a NEW entry (no prior position) — currentPos/previousPos default to 0 either way, defensive parse.
                case "previous_pos": int.TryParse(item.Value, NumberStyles.None, CultureInfo.InvariantCulture, out previousPos); break;
                case "rank": long.TryParse(item.Value, NumberStyles.None, CultureInfo.InvariantCulture, out rank); break;
            }
        }
        if (status is null) return null;
        return new ChartEntry(ChartStatusOf(status), currentPos, previousPos, rank);
    }

    /// <summary>Only <c>EQUAL</c>/<c>UP</c>/<c>DOWN</c>/<c>NEW</c> have ever been observed on the wire; any other string
    /// (a status this client does not understand) maps to <see cref="ChartEntryStatus.Unknown"/> — rendered as no glyph
    /// rather than a guessed arrow.</summary>
    static ChartEntryStatus ChartStatusOf(string status) => status switch
    {
        "EQUAL" => ChartEntryStatus.Equal,
        "UP" => ChartEntryStatus.Up,
        "DOWN" => ChartEntryStatus.Down,
        "NEW" => ChartEntryStatus.New,
        _ => ChartEntryStatus.Unknown,
    };

    /// <summary>Map the playlist4 Ops onto the domain ops the applier understands. Throws
    /// <see cref="ArgumentOutOfRangeException"/> on a shape this client cannot express — the same signal a torn apply
    /// raises, so every caller's existing "refetch instead of guessing" arm covers it.</summary>
    public static IReadOnlyList<PlaylistOp> MapOps(IEnumerable<Pl.Op> ops)
    {
        var list = new List<PlaylistOp>();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case Pl.Op.Types.Kind.Add when op.Add is { } add:
                    list.Add(new PlaylistOp(PlaylistOpKind.Add, FromIndex: add.FromIndex, AddFirst: add.AddFirst, AddLast: add.AddLast,
                        Items: ToMembers(add.Items), Anchor: OutboxAnchorOf(add)));
                    break;
                case Pl.Op.Types.Kind.Rem when op.Rem is { } rem:
                    list.Add(rem.ItemsAsKey
                        ? new PlaylistOp(PlaylistOpKind.Remove, Items: ToMembers(rem.Items), ItemsAsKey: true)
                        : new PlaylistOp(PlaylistOpKind.Remove, FromIndex: rem.FromIndex, Length: rem.Length,
                            Items: rem.Items.Count > 0 ? ToMembers(rem.Items) : null));
                    break;
                // An item-keyed MOV is identified by carrying items at all: it then has no index fields and its
                // destination is the anchor. Everything else is the positional rootlist/dealer-echo shape.
                case Pl.Op.Types.Kind.Mov when op.Mov is { Items.Count: > 0 } keyed:
                    var anchorItem = keyed.AddAfterItem;
                    list.Add(new PlaylistOp(PlaylistOpKind.Move, ItemsAsKey: true, Items: ToMembers(keyed.Items),
                        Anchor: AnchorOf(keyed), AnchorUri: anchorItem?.Uri));
                    break;
                case Pl.Op.Types.Kind.Mov when op.Mov is { } mov:
                    list.Add(new PlaylistOp(PlaylistOpKind.Move, FromIndex: mov.FromIndex, Length: mov.Length, ToIndex: mov.ToIndex));
                    break;
                case Pl.Op.Types.Kind.UpdateItemAttributes when op.UpdateItemAttributes is { } u:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: u.Index,
                        ItemPublic: u.NewAttributes?.Values is { HasPublic: true } v ? v.Public : null));
                    break;
                case Pl.Op.Types.Kind.UpdateListAttributes when op.UpdateListAttributes is { } u:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: PatchOf(u.NewAttributes)));
                    break;
                case Pl.Op.Types.Kind.UpdateListAttributes:
                    list.Add(new PlaylistOp(PlaylistOpKind.UpdateList));
                    break;
            }
        }
        return list;
    }

    /// <summary>Where a keyed MOV lands. <c>add_before_item</c> (field 5, never observed on the wire) has no domain
    /// representation — "before X" is only expressible as "after X's predecessor", which we cannot resolve without the
    /// list — so it is rejected as torn rather than silently reinterpreted.</summary>
    static PlaylistMoveAnchor AnchorOf(Pl.Mov mov)
    {
        if (mov.AddBeforeItem is not null)
            throw new ArgumentOutOfRangeException(nameof(mov), "keyed MOV add_before_item is not an expressible anchor");
        if (mov.HasAddFirst && mov.AddFirst) return new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First);
        if (mov.HasAddLast && mov.AddLast) return new PlaylistMoveAnchor(PlaylistMoveAnchorKind.Last);
        if (mov.AddAfterItem is { } after && after.Attributes is { HasItemId: true } a)
            return new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, Convert.ToHexStringLower(a.ItemId.Span));
        throw new ArgumentOutOfRangeException(nameof(mov), "keyed MOV carries no resolvable anchor");
    }

    /// <summary>The Wavee-local ADD anchor (I5) that only ever exists inside a durable-outbox blob. Absent on every
    /// byte that ever came off the wire.</summary>
    static PlaylistMoveAnchor? OutboxAnchorOf(Pl.Add add)
        => !add.HasWaveeAnchorItemId ? null
            : add.WaveeAnchorItemId.Length == 0 ? new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First)
            : new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, add.WaveeAnchorItemId);

    static IReadOnlyList<PlaylistMember> ToMembers(RepeatedField<Pl.Item> items)
    {
        var list = new List<PlaylistMember>(items.Count);
        foreach (var i in items) list.Add(ToMember(i));
        return list;
    }

    static PlaylistListAttributePatch? PatchOf(Pl.ListAttributesPartialState? s)
    {
        if (s is null) return null;
        string? name = null, desc = null;
        byte[]? picture = null;
        bool? collab = null, deleted = null;
        bool clearPic = false, clearName = false, clearDesc = false;
        if (s.Values is { } values)
        {
            if (values.HasName) name = values.Name;
            if (values.HasDescription) desc = values.Description;
            if (values.Picture.Length > 0) picture = values.Picture.ToByteArray();
            if (values.HasCollaborative) collab = values.Collaborative;
            if (values.HasDeletedByOwner) deleted = values.DeletedByOwner;
        }
        foreach (var nv in s.NoValue)
        {
            switch (nv)
            {
                case Pl.ListAttributeKind.ListPicture: clearPic = true; break;
                case Pl.ListAttributeKind.ListName: clearName = true; break;
                case Pl.ListAttributeKind.ListDescription: clearDesc = true; break;
                case Pl.ListAttributeKind.ListCollaborative: collab = false; break;
                // A remote delete's OLD attributes carry no_value[LIST_DELETED_BY_OWNER] ("it was not deleted before").
                // Parsed as an explicit false so the old/new pair round-trips instead of silently dropping the field.
                case Pl.ListAttributeKind.ListDeletedByOwner: deleted = false; break;
            }
        }
        if (name is null && desc is null && picture is null && collab is null && deleted is null
            && !clearPic && !clearName && !clearDesc)
            return new PlaylistListAttributePatch();
        return new PlaylistListAttributePatch(name, desc, picture, clearPic, collab, clearName, clearDesc, deleted);
    }

    static Pl.ListAttributesPartialState PartialOf(PlaylistListAttributePatch patch)
    {
        var partial = new Pl.ListAttributesPartialState();
        var hasValues = patch.Name is not null || patch.Description is not null || patch.PictureBytes is { Length: > 0 }
            || patch.Collaborative is not null || patch.DeletedByOwner is not null;
        if (hasValues)
        {
            var v = new Pl.ListAttributes();
            if (patch.Name is not null) v.Name = patch.Name;
            if (patch.Description is not null) v.Description = patch.Description;
            if (patch.PictureBytes is { Length: > 0 }) v.Picture = ByteString.CopyFrom(patch.PictureBytes);
            if (patch.Collaborative is not null) v.Collaborative = patch.Collaborative.Value;
            // deleted_by_owner=false travels as values.deleted_by_owner=false (HasDeletedByOwner round-trips it) — never
            // ALSO as a no_value entry; the no_value form is a PARSE-side shape only (the dealer's old_attributes).
            if (patch.DeletedByOwner is not null) v.DeletedByOwner = patch.DeletedByOwner.Value;
            partial.Values = v;
        }
        if (patch.ClearPicture) partial.NoValue.Add(Pl.ListAttributeKind.ListPicture);
        if (patch.ClearName) partial.NoValue.Add(Pl.ListAttributeKind.ListName);
        if (patch.ClearDescription) partial.NoValue.Add(Pl.ListAttributeKind.ListDescription);
        // Collaborative=false travels as values.collaborative=false (HasCollaborative round-trips it) — never ALSO as
        // a no_value entry; emitting both made emit/parse asymmetric.
        return partial;
    }

    /// <summary>The CREATE body (golden a031): a <c>/changes</c> envelope posted to the CLIENT-MINTED playlist id,
    /// based on the 8-byte <see cref="PlaylistRevisions.CreateBase"/> and carrying exactly one op — UPDATE_LIST with the
    /// new name. There is no <c>base_version</c> inside the delta and no attributes beyond the name; the response comes
    /// back with revision bookkeeping only (no name, no contents), which is why the store is seeded optimistically.</summary>
    public static byte[] BuildCreateChanges(string name, string username, long nowMs, long? nonce = null)
        => BuildChanges(PlaylistRevisions.NewCreateBase(),
            new[] { new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(Name: name)) },
            username, nowMs, nonce);

    // ── write direction: domain ops → the ListChanges body POSTed to /playlist/v2/{path}/changes ──
    /// <summary>Serialize a playlist edit into the desktop <c>/changes</c> envelope. <paramref name="nonce"/> is the
    /// per-request dedup nonce (Spotify will not apply the same nonce twice); pass one explicitly only from a byte-exact
    /// golden test — production always takes the random default.</summary>
    public static byte[] BuildChanges(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops, string username, long nowMs, long? nonce = null)
        => Build(baseRev, ops, username, nowMs, nonce, rootlistPublic: false, includeOutboxAnchors: false);

    // ── §2.5/§2.7 — the rootlist ListChanges body (follow = ADD, unfollow = REM) ──
    /// <summary>The rootlist flavour of <see cref="BuildChanges"/>: identical envelope, plus <c>public=true</c>
    /// ItemAttributes on ADD items (the rootlist path; the timestamp rides the member's AddedAt).</summary>
    public static byte[] BuildRootlistChanges(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops, string username, long nowMs, long? nonce = null)
        => Build(baseRev, ops, username, nowMs, nonce, rootlistPublic: true, includeOutboxAnchors: false);

    /// <summary>The DURABLE-OUTBOX serialization of a queued edit — never posted, only written to and read back from
    /// the SQLite <c>outbox.op</c> column. Same message so the column shape is unchanged, but it carries no ChangeInfo
    /// (user/timestamp are stamped at replay, not at enqueue) and it DOES carry the Wavee-local ADD anchor (I5).</summary>
    public static byte[] BuildOutboxBlob(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops)
        => Build(baseRev, ops, username: "", nowMs: 0, nonce: null, rootlistPublic: false, includeOutboxAnchors: true);

    /// <summary>Inverse of <see cref="BuildOutboxBlob"/> — reload a persisted outbox edit as (base revision, ops).</summary>
    public static (byte[]? BaseRev, IReadOnlyList<PlaylistOp> Ops) ParseOutboxBlob(byte[] blob)
    {
        var changes = Pl.ListChanges.Parser.ParseFrom(blob);
        byte[]? baseRev = changes.HasBaseRevision ? changes.BaseRevision.ToByteArray() : null;
        var ops = new List<PlaylistOp>();
        foreach (var delta in changes.Deltas) ops.AddRange(MapOps(delta.Ops));
        return (baseRev, ops);
    }

    static byte[] Build(byte[]? baseRev, IReadOnlyList<PlaylistOp> ops, string username, long nowMs, long? nonce,
                        bool rootlistPublic, bool includeOutboxAnchors)
    {
        // The desktop envelope: both result flags plus ONE positive nonce. want_sync_result makes the accepted edit
        // authoritative on the response path (I4); the nonce prevents a replayed request being applied twice.
        var changes = new Pl.ListChanges { WantResultingRevisions = true, WantSyncResult = true };
        var delta = new Pl.Delta();
        // Desktop stamps the delta with user + timestamp and NOTHING else (no admin/undo/merge — Appendix A.1), and
        // never repeats the base inside the delta.
        if (!string.IsNullOrEmpty(username) || nowMs > 0)
            delta.Info = new Pl.ChangeInfo { User = username, Timestamp = nowMs };
        if (baseRev is not null) changes.BaseRevision = ByteString.CopyFrom(baseRev);
        for (int i = 0; i < ops.Count; i++) delta.Ops.Add(ToWireOp(ops[i], rootlistPublic, includeOutboxAnchors));
        changes.Deltas.Add(delta);
        changes.Nonces.Add(nonce ?? System.Random.Shared.NextInt64(1, int.MaxValue));
        return changes.ToByteArray();
    }

    /// <summary>The resulting revision of a /changes (or bootstrap) response: the top-level <c>revision</c> when present,
    /// else the first <c>resulting_revisions</c> entry (§2.5 step 5 / §2.7).</summary>
    public static byte[]? ResultingRevision(Pl.SelectedListContent slc)
    {
        if (slc.HasRevision) return slc.Revision.ToByteArray();
        if (slc.ResultingRevisions.Count > 0) return slc.ResultingRevisions[0].ToByteArray();
        return null;
    }

    /// <summary>I4 — the head a PLAYLIST <c>/changes</c> 200 leaves us on: the LAST <c>resulting_revisions</c> entry (one
    /// per accepted delta; the last one is the head), falling back to the top-level <c>revision</c>. Distinct from
    /// <see cref="ResultingRevision"/>, which the rootlist bootstrap/GET path uses (there the top-level revision IS the
    /// answer and there is at most one delta).</summary>
    public static byte[]? LastResultingRevision(Pl.SelectedListContent slc)
    {
        if (slc.ResultingRevisions.Count > 0) return slc.ResultingRevisions[^1].ToByteArray();
        if (slc.HasRevision) return slc.Revision.ToByteArray();
        return null;
    }

    static Pl.Op ToWireOp(PlaylistOp op, bool rootlistPublic, bool includeOutboxAnchors) => op.Kind switch
    {
        PlaylistOpKind.Add => new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = BuildAdd(op, rootlistPublic, includeOutboxAnchors) },
        PlaylistOpKind.Remove => new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = BuildRem(op) },
        PlaylistOpKind.Move => new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = BuildMov(op) },
        PlaylistOpKind.UpdateList => new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateListAttributes,
            UpdateListAttributes = op.ListPatch is { } patch
                ? new Pl.UpdateListAttributes { NewAttributes = PartialOf(patch) }
                : null,
        },
        PlaylistOpKind.UpdateItem => new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateItemAttributes,
            UpdateItemAttributes = new Pl.UpdateItemAttributes
            {
                Index = op.FromIndex,
                NewAttributes = new Pl.ItemAttributesPartialState
                {
                    Values = new Pl.ItemAttributes { Public = op.ItemPublic ?? false },
                },
            },
        },
        _ => new Pl.Op { Kind = Pl.Op.Types.Kind.Unknown },
    };

    // Keyed MOV (the playlist reorder): the moved rows by (uri, item_id) plus ONE anchor. No index fields at all — the
    // server resolves everything against the base revision, so a concurrent foreign edit cannot land our rows on the
    // wrong positions. Positional MOV stays the rootlist shape.
    static Pl.Mov BuildMov(PlaylistOp op)
    {
        if (!op.ItemsAsKey) return new Pl.Mov { FromIndex = op.FromIndex, Length = op.Length, ToIndex = op.ToIndex };
        var mov = new Pl.Mov();
        if (op.Items is { } items)
            for (int i = 0; i < items.Count; i++) mov.Items.Add(KeyItem(items[i].ItemUri, items[i].ItemId));
        var anchor = op.Anchor ?? throw new InvalidOperationException("keyed MOV without an anchor");
        switch (anchor.Kind)
        {
            case PlaylistMoveAnchorKind.First: mov.AddFirst = true; break;
            case PlaylistMoveAnchorKind.Last: mov.AddLast = true; break;
            default:
                mov.AddAfterItem = KeyItem(op.AnchorUri ?? "", anchor.AfterItemId ?? "");
                break;
        }
        return mov;
    }

    static Pl.Item KeyItem(string uri, string itemIdHex) => new()
    {
        Uri = uri,
        Attributes = string.IsNullOrEmpty(itemIdHex) ? null
            : new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(Convert.FromHexString(itemIdHex)) },
    };

    // Keyed REM (items_as_key): one Item{uri, attrs{item_id}} per row, no index at all — the server resolves each row
    // against the base revision, so concurrent position drift cannot delete the wrong track. The rootlist unfollow uses
    // the same shape with no item_id (rootlist entries have none). Index REM stays the fallback for rows lacking ids.
    static Pl.Rem BuildRem(PlaylistOp op)
    {
        if (!op.ItemsAsKey)
        {
            var rem = new Pl.Rem { FromIndex = op.FromIndex, Length = op.Length };
            if (op.Items is { Count: > 0 } items)
                for (int i = 0; i < items.Count; i++) rem.Items.Add(KeyItem(items[i].ItemUri, items[i].ItemId));
            return rem;
        }
        var keyed = new Pl.Rem { ItemsAsKey = true };
        if (op.Items is { } keyedItems)
            for (int i = 0; i < keyedItems.Count; i++) keyed.Items.Add(KeyItem(keyedItems[i].ItemUri, keyedItems[i].ItemId));
        return keyed;
    }

    // ADD items carry the client-minted item_id (which the server KEEPS — verified: the ids we send come back on the
    // next GET) plus the add timestamp, and additionally public=true on the rootlist path. added_by is deliberately not
    // sent: desktop omits it and the server derives it from the authenticated user. from_index is emitted only when the
    // op is not an end-insert, and add_first/add_last only when true — an ADD that sets all three is not the wire shape.
    static Pl.Add BuildAdd(PlaylistOp op, bool rootlistPublic, bool includeOutboxAnchors)
    {
        var add = new Pl.Add();
        if (op.AddFirst) add.AddFirst = true;
        else if (op.AddLast) add.AddLast = true;
        else add.FromIndex = op.FromIndex;
        if (op.Items is { } items)
            for (int i = 0; i < items.Count; i++)
            {
                var m = items[i];
                var item = new Pl.Item { Uri = m.ItemUri };
                Pl.ItemAttributes? attrs = null;
                if (m.AddedAt > 0) (attrs ??= new Pl.ItemAttributes()).Timestamp = m.AddedAt;
                // public=true is a rootlist PLAYLIST row attribute (a042). Folder markers never carry it — a164's
                // start/end-group ADDs and b037/b128's rename ADD stop at the timestamp.
                if (rootlistPublic && !IsGroupMarker(m.ItemUri)) (attrs ??= new Pl.ItemAttributes()).Public = true;
                if (!string.IsNullOrEmpty(m.ItemId))
                    (attrs ??= new Pl.ItemAttributes()).ItemId = ByteString.CopyFrom(Convert.FromHexString(m.ItemId));
                if (attrs is not null) item.Attributes = attrs;
                add.Items.Add(item);
            }
        // I5, outbox only — remember the row this insertion point was derived from so a replay against a moved base can
        // recompute from_index. Never set on a body that goes to Spotify.
        if (includeOutboxAnchors && op.Anchor is { } anchor && !op.AddFirst && !op.AddLast)
            add.WaveeAnchorItemId = anchor.Kind == PlaylistMoveAnchorKind.AfterItem ? anchor.AfterItemId ?? "" : "";
        return add;
    }

    /// <summary>A rootlist folder marker (<c>spotify:start-group:…</c> / <c>spotify:end-group:…</c>) rather than a
    /// playlist row.</summary>
    static bool IsGroupMarker(string uri)
        => uri.StartsWith("spotify:start-group:", StringComparison.Ordinal)
        || uri.StartsWith("spotify:end-group:", StringComparison.Ordinal);
}
