using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// ── The playlist-wire golden manifest (plan P2 item 7 / P3 item 4, fixture half) ──────────────────────────────────────
// Two layers of assertion over the byte-exact desktop captures:
//   (1) DECODE — what Appendix A claims about the wire, checked against the ACTUAL bytes with the current protos, so a
//       proto edit that silently changes how a real desktop body parses fails here.
//   (2) REBUILD (P2) — the capture is mapped to domain ops and re-serialized through the SAME builder production uses,
//       with the capture own user/timestamp/nonce injected, and the result must equal the capture BYTE FOR BYTE. That
//       is the only assertion strong enough to catch a stray field (the admin/undo/merge ChangeInfo, a per-delta
//       base_version, an added_by on an ADD item) or a missing one (the client-minted item_id).
public class WireGoldenTests
{
    static Pl.Delta SingleDelta(Pl.ListChanges c) => Assert.Single(c.Deltas);
    static Pl.Op SingleOp(Pl.ListChanges c) => Assert.Single(SingleDelta(c).Ops);

    /// <summary>Every /changes envelope desktop sends carries want_resulting_revisions + want_sync_result + exactly one
    /// nonce (the per-list, per-session monotonic counter), and a ChangeInfo of user + timestamp ONLY.</summary>
    static void AssertEnvelope(Pl.ListChanges c, long nonce)
    {
        Assert.True(c.HasWantResultingRevisions && c.WantResultingRevisions);
        Assert.True(c.HasWantSyncResult && c.WantSyncResult);
        Assert.Equal(new[] { nonce }, c.Nonces.ToArray());

        var info = SingleDelta(c).Info;
        Assert.NotNull(info);
        Assert.Equal(Golden.CaptureUser, info.User);
        Assert.True(info.HasTimestamp && info.Timestamp > 0);
        // A.1: desktop sends NEITHER Admin/Undo/Merge (Wavee's BuildChanges sets all three today — P2 drops them).
        Assert.False(info.HasAdmin);
        Assert.False(info.HasUndo);
        Assert.False(info.HasRedo);
        Assert.False(info.HasMerge);
        Assert.False(info.HasCompressed);
        Assert.False(info.HasMigration);
        Assert.False(info.HasSplitId);
        Assert.Null(info.Source);
    }

    // ── manifest integrity ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Fixtures_AllPresent_AndSizesMatchCapture()
    {
        foreach (var (name, size) in Golden.RequestSizes.Concat(Golden.ResponseSizes))
        {
            var path = Path.Combine(Golden.Dir, name + ".bin");
            Assert.True(File.Exists(path), "missing golden: " + path);
            Assert.Equal(size, Golden.Bytes(name).Length);
        }
    }

    /// <summary>Goldens are BODIES, never captures of a whole HTTP exchange: no header block, no bearer token.</summary>
    [Fact]
    public void Fixtures_AreBodiesOnly_NoCapturedHeaders()
    {
        foreach (var name in Golden.RequestSizes.Keys.Concat(Golden.ResponseSizes.Keys))
        {
            var text = System.Text.Encoding.Latin1.GetString(Golden.Bytes(name));
            Assert.DoesNotContain("HTTP/1.1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer ", text, StringComparison.Ordinal);
        }
    }

    // ── the decoded manifest ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Fixtures_ParseAsExpectedMessages()
    {
        // a031 — create via /changes. The base is EIGHT bytes (not a 24-B revision), and the delta carries NO
        // base_version of its own; one UPDATE_LIST sets the name.
        var a031 = Golden.Changes("a031-create-p1");
        Assert.Equal(8, a031.BaseRevision.Length);
        Assert.Equal("00000000726f6f74", Golden.Hex(a031.BaseRevision));
        Assert.Equal(Golden.CreateBase, a031.BaseRevision.ToByteArray());
        Assert.False(SingleDelta(a031).HasBaseVersion);
        var a031Op = SingleOp(a031);
        Assert.Equal(Pl.Op.Types.Kind.UpdateListAttributes, a031Op.Kind);
        Assert.Equal("Daily Mix 1 (2)", a031Op.UpdateListAttributes.NewAttributes.Values.Name);
        Assert.Null(a031Op.UpdateListAttributes.OldAttributes);
        AssertEnvelope(a031, nonce: 1);

        // a042 — the rootlist ADD that follows the create: one item at index 0 with attrs{timestamp, public}.
        var a042 = Golden.Changes("a042-rootlist-add-p1");
        Assert.Equal(24, a042.BaseRevision.Length);
        var a042Op = SingleOp(a042);
        Assert.Equal(Pl.Op.Types.Kind.Add, a042Op.Kind);
        Assert.True(a042Op.Add.HasFromIndex);
        Assert.Equal(0, a042Op.Add.FromIndex);
        var a042Item = Assert.Single(a042Op.Add.Items);
        Assert.Equal("spotify:playlist:6EVbQZBiAg9zHzMjChxvRd", a042Item.Uri);
        Assert.Equal(1786796442000L, a042Item.Attributes.Timestamp);
        Assert.True(a042Item.Attributes.HasPublic && a042Item.Attributes.Public);
        AssertEnvelope(a042, nonce: 1);

        // a046 — 50 tracks appended in ONE ADD, every item carrying a CLIENT-MINTED 8-byte item_id (all distinct).
        var a046 = Golden.Changes("a046-add-50-tracks");
        var a046Op = SingleOp(a046);
        Assert.Equal(Pl.Op.Types.Kind.Add, a046Op.Kind);
        Assert.True(a046Op.Add.HasAddLast && a046Op.Add.AddLast);
        Assert.False(a046Op.Add.HasFromIndex);
        Assert.Equal(50, a046Op.Add.Items.Count);
        Assert.All(a046Op.Add.Items, it =>
        {
            Assert.True(it.Attributes.HasItemId);
            Assert.Equal(8, it.Attributes.ItemId.Length);
            Assert.Equal(1786796442000L, it.Attributes.Timestamp);
        });
        Assert.Equal(50, a046Op.Add.Items.Select(i => Golden.Hex(i.Attributes.ItemId)).Distinct().Count());
        AssertEnvelope(a046, nonce: 2);

        // a143 — THREE keyed REM ops in ONE delta; each carries one Item{uri, attrs{item_id}} and items_as_key.
        var a143 = Golden.Changes("a143-keyed-rem-x3");
        var a143Delta = SingleDelta(a143);
        Assert.Equal(3, a143Delta.Ops.Count);
        Assert.All(a143Delta.Ops, op =>
        {
            Assert.Equal(Pl.Op.Types.Kind.Rem, op.Kind);
            Assert.True(op.Rem.HasItemsAsKey && op.Rem.ItemsAsKey);
            Assert.False(op.Rem.HasFromIndex);          // a keyed REM carries no index at all …
            Assert.False(op.Rem.HasLength);
            var item = Assert.Single(op.Rem.Items);     // … just the one item it keys on
            Assert.StartsWith("spotify:track:", item.Uri, StringComparison.Ordinal);
            Assert.True(item.Attributes.HasItemId);
            Assert.Equal(8, item.Attributes.ItemId.Length);
        });
        AssertEnvelope(a143, nonce: 5);

        // a164 — folder create: ONE delta, TWO ADDs (start-group then end-group) sharing the create timestamp.
        var a164 = Golden.Changes("a164-folder-create");
        var a164Delta = SingleDelta(a164);
        Assert.Equal(2, a164Delta.Ops.Count);
        Assert.All(a164Delta.Ops, op => Assert.Equal(Pl.Op.Types.Kind.Add, op.Kind));
        Assert.Equal(0, a164Delta.Ops[0].Add.FromIndex);
        Assert.Equal(1, a164Delta.Ops[1].Add.FromIndex);
        var start = Assert.Single(a164Delta.Ops[0].Add.Items);
        var end = Assert.Single(a164Delta.Ops[1].Add.Items);
        Assert.Equal("spotify:start-group:edb339e10aebcf38:New+Folder", start.Uri);
        Assert.Equal("spotify:end-group:edb339e10aebcf38", end.Uri);
        Assert.Equal(1786796469000L, start.Attributes.Timestamp);
        Assert.Equal(1786796469000L, end.Attributes.Timestamp);
        AssertEnvelope(a164, nonce: 2);

        // a281 — deleting a playlist is a rootlist INDEX rem: from/length set, one bare Item{uri}, NO items_as_key,
        // NO attributes. (The playlist itself is never called.)
        var a281 = Golden.Changes("a281-rootlist-index-rem");
        var a281Op = SingleOp(a281);
        Assert.Equal(Pl.Op.Types.Kind.Rem, a281Op.Kind);
        Assert.Equal(0, a281Op.Rem.FromIndex);
        Assert.Equal(1, a281Op.Rem.Length);
        Assert.False(a281Op.Rem.HasItemsAsKey);
        var a281Item = Assert.Single(a281Op.Rem.Items);
        Assert.Equal("spotify:playlist:4vkIrispQ6gcMNIojGPd0L", a281Item.Uri);
        Assert.Null(a281Item.Attributes);
        AssertEnvelope(a281, nonce: 13);

        // b037 — folder rename: REM{from,len=1} with NO items, then ADD re-inserting the start-group at the same index
        // carrying the ORIGINAL create timestamp (not "now" — the delta's own info.timestamp is 8 minutes later).
        var b037 = Golden.Changes("b037-folder-rename");
        var b037Delta = SingleDelta(b037);
        Assert.Equal(2, b037Delta.Ops.Count);
        Assert.Equal(Pl.Op.Types.Kind.Rem, b037Delta.Ops[0].Kind);
        Assert.Equal(2, b037Delta.Ops[0].Rem.FromIndex);
        Assert.Equal(1, b037Delta.Ops[0].Rem.Length);
        Assert.Empty(b037Delta.Ops[0].Rem.Items);
        Assert.Equal(Pl.Op.Types.Kind.Add, b037Delta.Ops[1].Kind);
        Assert.Equal(2, b037Delta.Ops[1].Add.FromIndex);
        var renamed = Assert.Single(b037Delta.Ops[1].Add.Items);
        Assert.Equal("spotify:start-group:edb339e10aebcf38:named+folder+update", renamed.Uri);
        Assert.Equal(1786796469000L, renamed.Attributes.Timestamp);   // the a164 create ts, resent verbatim
        Assert.Equal(1786797274000L, b037Delta.Info.Timestamp);       // … while the delta itself is stamped "now"
        AssertEnvelope(b037, nonce: 16);

        // b049 — the rootlist reorder stays POSITIONAL (fields 1/2/3), unlike the playlist-side keyed MOVs.
        var b049 = Golden.Changes("b049-rootlist-mov");
        var b049Op = SingleOp(b049);
        Assert.Equal(Pl.Op.Types.Kind.Mov, b049Op.Kind);
        Assert.True(b049Op.Mov.HasFromIndex && b049Op.Mov.HasLength && b049Op.Mov.HasToIndex);
        Assert.Equal(0, b049Op.Mov.FromIndex);
        Assert.Equal(1, b049Op.Mov.Length);
        Assert.Equal(3, b049Op.Mov.ToIndex);
        AssertEnvelope(b049, nonce: 17);

        // b063 — rename a playlist: UPDATE_LIST with new_attributes only (desktop sends no old_attributes here).
        var b063 = Golden.Changes("b063-update-list-name");
        var b063Op = SingleOp(b063);
        Assert.Equal(Pl.Op.Types.Kind.UpdateListAttributes, b063Op.Kind);
        Assert.Equal("updated playlist name", b063Op.UpdateListAttributes.NewAttributes.Values.Name);
        Assert.Null(b063Op.UpdateListAttributes.OldAttributes);
        AssertEnvelope(b063, nonce: 11);

        // b128 — the same rename shape one level out (a root-level folder).
        var b128 = Golden.Changes("b128-folder-rename-outer");
        var b128Delta = SingleDelta(b128);
        Assert.Equal(2, b128Delta.Ops.Count);
        Assert.Equal(0, b128Delta.Ops[0].Rem.FromIndex);
        Assert.Empty(b128Delta.Ops[0].Rem.Items);
        Assert.Equal("spotify:start-group:3dd9e795c88ae3e4:root+folder+updated+name",
            Assert.Single(b128Delta.Ops[1].Add.Items).Uri);
        Assert.Equal(1786796474000L, Assert.Single(b128Delta.Ops[1].Add.Items).Attributes.Timestamp);
        AssertEnvelope(b128, nonce: 18);
    }

    // ── keyed MOV ────────────────────────────────────────────────────────────────────────────────────────────────────
    // An item-keyed MOV omits from_index/length/to_index entirely: the moved rows are named by (uri, item_id) in
    // `items` (field 4) and the destination is `add_after_item` (6) / `add_first` (7) / `add_last` (8).
    [Theory]
    [InlineData("a148-keyed-mov-after-item", 6L, PlaylistMoveAnchorKind.AfterItem, 3)]
    [InlineData("a154-keyed-mov-add-first", 7L, PlaylistMoveAnchorKind.First, 1)]
    [InlineData("a498-keyed-mov-add-last", 8L, PlaylistMoveAnchorKind.Last, 3)]
    public void KeyedMovGoldens_ParseAsOneKeyedMove_WithTheRightAnchor(
        string name, long nonce, PlaylistMoveAnchorKind kind, int itemCount)
    {
        var c = Golden.Changes(name);
        var wire = SingleOp(c);
        Assert.Equal(Pl.Op.Types.Kind.Mov, wire.Kind);
        Assert.False(wire.Mov.HasFromIndex);
        Assert.False(wire.Mov.HasLength);
        Assert.False(wire.Mov.HasToIndex);
        Assert.Null(wire.Mov.AddBeforeItem);           // never observed on the wire
        Assert.Equal(itemCount, wire.Mov.Items.Count);
        Assert.All(wire.Mov.Items, it =>
        {
            Assert.StartsWith("spotify:track:", it.Uri, StringComparison.Ordinal);
            Assert.True(it.Attributes.HasItemId);
            Assert.Equal(8, it.Attributes.ItemId.Length);
            Assert.False(it.Attributes.HasTimestamp);  // a keyed row carries its id and NOTHING else
            Assert.False(it.Attributes.HasAddedBy);
        });

        var op = Assert.Single(PlaylistWireMapper.MapOps(new[] { wire }));
        Assert.Equal(PlaylistOpKind.Move, op.Kind);
        Assert.True(op.ItemsAsKey);
        Assert.Equal(kind, op.Anchor!.Value.Kind);
        Assert.Equal(itemCount, op.Items!.Count);
        if (kind == PlaylistMoveAnchorKind.AfterItem)
        {
            Assert.Equal("22ef762d8b213c33", op.Anchor.Value.AfterItemId);
            Assert.Equal("spotify:track:7IRZ9aTBu35je2AHT2LxvL", op.AnchorUri);
        }
        else
        {
            Assert.Null(op.Anchor.Value.AfterItemId);
            Assert.Null(op.AnchorUri);
        }

        AssertEnvelope(c, nonce);
    }

    // ── (2) BYTE-EXACT REBUILD ───────────────────────────────────────────────────────────────────────────────────────
    // capture bytes -> MapOps -> BuildChanges(with the capture own user/timestamp/nonce) -> the same bytes. Any field
    // Wavee adds, drops, or orders differently from desktop shows up as an inequality here.
    [Theory]
    [InlineData("a046-add-50-tracks", 2L)]           // ADD add_last, 50 client-minted item_ids
    [InlineData("a143-keyed-rem-x3", 5L)]            // one delta, three keyed REMs
    [InlineData("a148-keyed-mov-after-item", 6L)]    // keyed MOV, add_after_item
    [InlineData("a154-keyed-mov-add-first", 7L)]     // keyed MOV, add_first
    [InlineData("a498-keyed-mov-add-last", 8L)]      // keyed MOV, add_last
    [InlineData("b063-update-list-name", 11L)]       // UPDATE_LIST name
    public void PlaylistRequestGoldens_RebuildByteExact(string name, long nonce)
    {
        var captured = Golden.Bytes(name);
        var changes = Golden.Changes(name);
        var delta = SingleDelta(changes);

        var ops = PlaylistWireMapper.MapOps(delta.Ops);
        var rebuilt = PlaylistWireMapper.BuildChanges(
            changes.BaseRevision.ToByteArray(), ops, delta.Info.User, delta.Info.Timestamp, nonce);

        Assert.Equal(captured, rebuilt);
    }

    // The rootlist flavour of the same rebuild: same envelope, plus public=true on ADD items.
    [Theory]
    [InlineData("a042-rootlist-add-p1", 1L)]         // ADD at index 0, attrs{timestamp, public}
    [InlineData("a281-rootlist-index-rem", 13L)]     // index REM, one bare Item{uri}
    [InlineData("b049-rootlist-mov", 17L)]           // positional MOV{from,len,to}
    [InlineData("a164-folder-create", 2L)]           // folder create: two ADDs, one delta, NO public on the markers
    [InlineData("b037-folder-rename", 16L)]          // folder rename: REM (no items) + ADD with the original ts
    [InlineData("b128-folder-rename-outer", 18L)]    // the same rename one level out
    public void RootlistRequestGoldens_RebuildByteExact(string name, long nonce)
    {
        var captured = Golden.Bytes(name);
        var changes = Golden.Changes(name);
        var delta = SingleDelta(changes);

        var ops = PlaylistWireMapper.MapOps(delta.Ops);
        var rebuilt = PlaylistWireMapper.BuildRootlistChanges(
            changes.BaseRevision.ToByteArray(), ops, delta.Info.User, delta.Info.Timestamp, nonce);

        Assert.Equal(captured, rebuilt);
    }

    // ── the permission proto dialect ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PermissionGoldens_AreTheProtoDialect()
    {
        Assert.Equal(new byte[] { 0x08, 0x01 }, Golden.Bytes("b078-perm-set-blocked"));
        Assert.Equal(new byte[] { 0x08, 0x02 }, Golden.Bytes("b108-perm-set-viewer"));

        var blocked = Pl.SetPermissionLevelRequest.Parser.ParseFrom(Golden.Bytes("b078-perm-set-blocked"));
        var viewer = Pl.SetPermissionLevelRequest.Parser.ParseFrom(Golden.Bytes("b108-perm-set-viewer"));
        Assert.Equal(Pl.PermissionLevel.Blocked, blocked.PermissionLevel);
        Assert.Equal(Pl.PermissionLevel.Viewer, viewer.PermissionLevel);

        // GET /permission/base after the BLOCKED set: Permission{revision(8 B, NOT a 24-B playlist revision), level}.
        var perm = Pl.Permission.Parser.ParseFrom(Golden.Bytes("perm-get-blocked"));
        Assert.Equal(Pl.PermissionLevel.Blocked, perm.PermissionLevel);
        Assert.Equal(8, perm.Revision.Length);
        Assert.Equal("3b907c0d29c940a3", Golden.Hex(perm.Revision));
    }

    // ── /changes responses ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ResponseGoldens_ParseAsSelectedListContent()
    {
        // The create reply carries NO name and NO contents — just the revision bookkeeping. Anything that expects to
        // read the new playlist's attributes out of this response is wrong (P3 seeds the store optimistically instead).
        var create = Golden.Content("a178-create-response");
        Assert.True(create.HasRevision);
        Assert.Equal(24, create.Revision.Length);
        Assert.Null(create.Attributes);
        Assert.Null(create.Contents);
        Assert.NotNull(create.SyncResult);
        Assert.Equal(Golden.Hex(create.SyncResult.FromRevision), Golden.Hex(create.SyncResult.ToRevision));
        Assert.Empty(create.SyncResult.Ops);                       // I4: a rev-only advance IS allowed when sync_result is empty
        Assert.Equal(Golden.Hex(create.Revision), Golden.Hex(Assert.Single(create.ResultingRevisions)));
        Assert.False(create.MultipleHeads);
        Assert.Equal(new[] { 1L }, create.Nonces.ToArray());
        Assert.NotNull(create.Capabilities);

        // The rootlist folder-create reply: same shape, uncompressed on the wire (single-delta rootlist writes are).
        var folder = Golden.Content("a164-folder-create-response");
        Assert.Equal(24, folder.Revision.Length);
        Assert.Equal(Golden.Hex(folder.SyncResult.FromRevision), Golden.Hex(folder.SyncResult.ToRevision));
        Assert.Empty(folder.SyncResult.Ops);
        Assert.Equal(Golden.Hex(folder.Revision), Golden.Hex(Assert.Single(folder.ResultingRevisions)));
        Assert.False(folder.MultipleHeads);
        Assert.Equal(new[] { 2L }, folder.Nonces.ToArray());
    }
}
