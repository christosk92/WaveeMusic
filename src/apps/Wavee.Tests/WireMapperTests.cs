using Google.Protobuf;
using System;
using System.Linq;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// Map the playlist4 SelectedListContent/Op wire types onto the proto-free domain (PlaylistMember/PlaylistOp).
public class PlaylistWireMapperTests
{
    [Fact]
    public void ParseContents_ReadsOrderedMembers_AndRevision()
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(1, 2, 3) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:a", Attributes = new Pl.ItemAttributes { AddedBy = "alice", Timestamp = 1700, ItemId = ByteString.CopyFrom(0xAB) } });
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:b" });
        slc.Contents = contents;

        var (members, rev) = PlaylistWireMapper.ParseContents(slc);
        Assert.Equal(2, members.Count);
        Assert.Equal("spotify:track:a", members[0].ItemUri);
        Assert.Equal("alice", members[0].AddedBy);
        Assert.Equal(1700L, members[0].AddedAt);
        Assert.Equal("ab", members[0].ItemId);          // item_id bytes → lowercase hex
        Assert.Null(members[1].AddedBy);
        Assert.Equal(new byte[] { 1, 2, 3 }, rev);
    }

    [Fact]
    public void MapOps_MapsAddRemMov()
    {
        var add = new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = new Pl.Add { FromIndex = 1 } };
        add.Add.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        var rem = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 2, Length = 3 } };
        var mov = new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = new Pl.Mov { FromIndex = 0, Length = 1, ToIndex = 4 } };

        var ops = PlaylistWireMapper.MapOps(new[] { add, rem, mov });
        Assert.Equal(3, ops.Count);
        Assert.Equal(PlaylistOpKind.Add, ops[0].Kind);
        Assert.Equal(1, ops[0].FromIndex);
        Assert.Equal("spotify:track:x", ops[0].Items![0].ItemUri);
        Assert.Equal(PlaylistOpKind.Remove, ops[1].Kind);
        Assert.Equal(3, ops[1].Length);
        Assert.Equal(PlaylistOpKind.Move, ops[2].Kind);
        Assert.Equal(4, ops[2].ToIndex);
    }

    [Fact]
    public void MapOps_IndexRem_CarriesItemsForVerification()
    {
        var rem = new Pl.Rem { FromIndex = 4, Length = 2 };
        rem.Items.Add(new Pl.Item { Uri = "spotify:track:a", Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(0xA1) } });
        rem.Items.Add(new Pl.Item { Uri = "spotify:track:b", Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(0xB2) } });

        var op = Assert.Single(PlaylistWireMapper.MapOps(new[] { new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = rem } }));

        Assert.Equal(PlaylistOpKind.Remove, op.Kind);
        Assert.False(op.ItemsAsKey);
        Assert.Equal(4, op.FromIndex);
        Assert.Equal(2, op.Length);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, op.Items!.Select(i => i.ItemUri).ToArray());
        Assert.Equal(new[] { "a1", "b2" }, op.Items!.Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public void BuildChanges_WritesListChanges_WithBaseRevAndOps()
    {
        var ops = new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { new PlaylistMember("", "spotify:track:x", null, 0) }),
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 2, Length: 1),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 0, Length: 1, ToIndex: 3),
        };
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 }, ops, "alice", 1_700_000_000_000);
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);

        Assert.Equal(new byte[] { 5, 6 }, changes.BaseRevision.ToByteArray());
        Assert.True(changes.WantResultingRevisions);
        Assert.True(changes.WantSyncResult);
        Assert.InRange(Assert.Single(changes.Nonces), 1, int.MaxValue - 1L);
        var delta = Assert.Single(changes.Deltas);
        Assert.Equal(3, delta.Ops.Count);
        Assert.Equal(Pl.Op.Types.Kind.Add, delta.Ops[0].Kind);
        Assert.True(delta.Ops[0].Add.AddLast);
        Assert.False(delta.Ops[0].Add.HasFromIndex);           // an end-insert carries add_last ONLY
        Assert.Equal("spotify:track:x", delta.Ops[0].Add.Items[0].Uri);
        Assert.Equal(Pl.Op.Types.Kind.Rem, delta.Ops[1].Kind);
        Assert.Equal(2, delta.Ops[1].Rem.FromIndex);
        Assert.Equal(Pl.Op.Types.Kind.Mov, delta.Ops[2].Kind);
        Assert.Equal(3, delta.Ops[2].Mov.ToIndex);
    }

    // A.1 / the b063 capture: the desktop ChangeInfo is user + timestamp and NOTHING else, and a Delta never repeats the
    // base revision the envelope already carries. Wavee used to send admin+undo+merge and a per-delta base_version.
    [Fact]
    public void BuildChanges_InfoIsUserAndTimestampOnly()
    {
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 },
            new[] { new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(Name: "n")) },
            "alice", 1_700_000_000_000);
        var delta = Assert.Single(Pl.ListChanges.Parser.ParseFrom(bytes).Deltas);

        Assert.False(delta.HasBaseVersion);
        var info = delta.Info;
        Assert.Equal("alice", info.User);
        Assert.Equal(1_700_000_000_000L, info.Timestamp);
        Assert.False(info.HasAdmin);
        Assert.False(info.HasUndo);
        Assert.False(info.HasRedo);
        Assert.False(info.HasMerge);
        Assert.False(info.HasCompressed);
        Assert.False(info.HasMigration);
        Assert.False(info.HasSplitId);
        Assert.Null(info.Source);
    }

    // The keyed MOV round-trip: domain anchor -> Mov{items, add_after_item|add_first|add_last} -> domain anchor. The
    // wire form carries NO index fields at all; that is what makes it safe under a concurrent foreign edit.
    [Theory]
    [InlineData(PlaylistMoveAnchorKind.First)]
    [InlineData(PlaylistMoveAnchorKind.Last)]
    [InlineData(PlaylistMoveAnchorKind.AfterItem)]
    public void MapOps_KeyedMov_RoundTrip(PlaylistMoveAnchorKind kind)
    {
        var anchor = kind == PlaylistMoveAnchorKind.AfterItem
            ? new PlaylistMoveAnchor(kind, "a1b2c3d4e5f60718")
            : new PlaylistMoveAnchor(kind);
        var op = new PlaylistOp(PlaylistOpKind.Move, ItemsAsKey: true,
            Items: new[]
            {
                new PlaylistMember("0011223344556677", "spotify:track:x", null, 0),
                new PlaylistMember("8899aabbccddeeff", "spotify:track:y", null, 0),
            },
            Anchor: anchor, AnchorUri: "spotify:track:anchor");

        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 }, new[] { op }, "alice", 1_700_000_000_000);
        var wire = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(bytes).Deltas).Ops);

        Assert.Equal(Pl.Op.Types.Kind.Mov, wire.Kind);
        Assert.False(wire.Mov.HasFromIndex);
        Assert.False(wire.Mov.HasLength);
        Assert.False(wire.Mov.HasToIndex);
        Assert.Null(wire.Mov.AddBeforeItem);
        Assert.Equal(2, wire.Mov.Items.Count);
        Assert.Equal("spotify:track:x", wire.Mov.Items[0].Uri);
        Assert.Equal("0011223344556677", Golden.Hex(wire.Mov.Items[0].Attributes.ItemId));
        if (kind == PlaylistMoveAnchorKind.AfterItem)
        {
            Assert.Equal("spotify:track:anchor", wire.Mov.AddAfterItem.Uri);
            Assert.Equal("a1b2c3d4e5f60718", Golden.Hex(wire.Mov.AddAfterItem.Attributes.ItemId));
        }
        else
        {
            Assert.Null(wire.Mov.AddAfterItem);
            Assert.Equal(kind == PlaylistMoveAnchorKind.First, wire.Mov.HasAddFirst && wire.Mov.AddFirst);
            Assert.Equal(kind == PlaylistMoveAnchorKind.Last, wire.Mov.HasAddLast && wire.Mov.AddLast);
        }

        var back = Assert.Single(PlaylistWireMapper.MapOps(new[] { wire }));
        Assert.Equal(PlaylistOpKind.Move, back.Kind);
        Assert.True(back.ItemsAsKey);
        Assert.Equal(anchor, back.Anchor);
        Assert.Equal(new[] { "0011223344556677", "8899aabbccddeeff" }, back.Items!.Select(i => i.ItemId).ToArray());
        Assert.Equal(0, back.FromIndex);
        Assert.Equal(0, back.Length);
        Assert.Equal(0, back.ToIndex);
    }

    // add_before_item has no domain representation ("before X" is "after the row before X", which needs the list). It
    // is rejected as torn so the caller refetches, never silently reinterpreted.
    [Fact]
    public void MapOps_KeyedMov_AddBeforeItem_IsRejectedAsTorn()
    {
        var mov = new Pl.Mov { AddBeforeItem = new Pl.Item { Uri = "spotify:track:anchor" } };
        mov.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        var wire = new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = mov };

        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistWireMapper.MapOps(new[] { wire }));
    }

    // A keyed REM used to drop the item_id on the floor, which made removing one of two identical tracks a coin flip.
    [Fact]
    public void BuildRem_Keyed_CarriesItemId()
    {
        var op = new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
            Items: new[] { new PlaylistMember("0102030405060708", "spotify:track:x", null, 0) });
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 }, new[] { op }, "alice", 1_700_000_000_000);
        var wire = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(bytes).Deltas).Ops);

        Assert.Equal(Pl.Op.Types.Kind.Rem, wire.Kind);
        Assert.True(wire.Rem.ItemsAsKey);
        Assert.False(wire.Rem.HasFromIndex);
        Assert.False(wire.Rem.HasLength);
        var item = Assert.Single(wire.Rem.Items);
        Assert.Equal("spotify:track:x", item.Uri);
        Assert.Equal("0102030405060708", Golden.Hex(item.Attributes.ItemId));

        // The rootlist unfollow keys on the uri alone (rootlist entries have no row ids) - still no attributes at all.
        var unfollow = new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
            Items: new[] { new PlaylistMember("", "spotify:playlist:p", null, 0) });
        var rootBytes = PlaylistWireMapper.BuildRootlistChanges(new byte[] { 9 }, new[] { unfollow }, "alice", 1);
        var rootWire = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(rootBytes).Deltas).Ops);
        Assert.Null(Assert.Single(rootWire.Rem.Items).Attributes);
    }

    // The client mints item ids (A 046): the ADD carries them, plus the add timestamp - and NOT added_by, which desktop
    // omits and the server derives from the authenticated user.
    [Fact]
    public void BuildAdd_CarriesMintedItemId()
    {
        var op = new PlaylistOp(PlaylistOpKind.Add, AddLast: true,
            Items: new[] { new PlaylistMember("aabbccddeeff0011", "spotify:track:x", "alice", 1_700_000_000_000) });
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 }, new[] { op }, "alice", 1_700_000_000_000);
        var wire = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(bytes).Deltas).Ops);

        var item = Assert.Single(wire.Add.Items);
        Assert.Equal("aabbccddeeff0011", Golden.Hex(item.Attributes.ItemId));
        Assert.Equal(1_700_000_000_000L, item.Attributes.Timestamp);
        Assert.False(item.Attributes.HasAddedBy);
        Assert.False(item.Attributes.HasPublic);
    }

    // The Wavee-local ADD anchor (I5) exists ONLY in the durable-outbox serialization and must never reach the wire.
    [Fact]
    public void OutboxBlob_CarriesTheInsertAnchor_ButBuildChangesNeverDoes()
    {
        var op = new PlaylistOp(PlaylistOpKind.Add, FromIndex: 3,
            Items: new[] { new PlaylistMember("aabbccddeeff0011", "spotify:track:x", null, 5) },
            Anchor: new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, "1122334455667788"));

        var wireOp = Assert.Single(Assert.Single(Pl.ListChanges.Parser
            .ParseFrom(PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 }, new[] { op }, "alice", 1)).Deltas).Ops);
        Assert.False(wireOp.Add.HasWaveeAnchorItemId);

        var (baseRev, back) = PlaylistWireMapper.ParseOutboxBlob(
            PlaylistWireMapper.BuildOutboxBlob(new byte[] { 5, 6 }, new[] { op }));
        Assert.Equal(new byte[] { 5, 6 }, baseRev);
        Assert.Equal(op.Anchor, Assert.Single(back).Anchor);
        Assert.Equal(3, back[0].FromIndex);
    }

    // P1 tombstone: ListAttributes.deleted_by_owner (field 6) is the wire shape of a REMOTE DELETE. It has to survive
    // BOTH directions (emit → parse) or the dealer's UPDATE_LIST new{deleted_by_owner=1} silently becomes a no-op patch.
    [Fact]
    public void PatchOf_DeletedByOwner_RoundTrips()
    {
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 5, 6 },
            new[] { new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(DeletedByOwner: true)) },
            "alice", 1_700_000_000_000);
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);
        var wire = Assert.Single(Assert.Single(changes.Deltas).Ops);

        Assert.Equal(Pl.Op.Types.Kind.UpdateListAttributes, wire.Kind);
        Assert.True(wire.UpdateListAttributes.NewAttributes.Values.DeletedByOwner);

        var back = Assert.Single(PlaylistWireMapper.MapOps(new[] { wire }));
        Assert.Equal(PlaylistOpKind.UpdateList, back.Kind);
        Assert.True(back.ListPatch!.DeletedByOwner);
    }

    // The dealer's OLD attributes say "it was not deleted before" via no_value[LIST_DELETED_BY_OWNER]. Parsed as an
    // explicit false so the pair round-trips instead of collapsing into an empty patch (which would read as a tombstone-
    // free UPDATE_LIST and lose the change entirely).
    [Fact]
    public void PatchOf_DeletedByOwner_NoValue_ParsesAsFalse()
    {
        var partial = new Pl.ListAttributesPartialState { Values = new Pl.ListAttributes() };
        partial.NoValue.Add(Pl.ListAttributeKind.ListDeletedByOwner);
        var wire = new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateListAttributes,
            UpdateListAttributes = new Pl.UpdateListAttributes { NewAttributes = partial },
        };

        var op = Assert.Single(PlaylistWireMapper.MapOps(new[] { wire }));
        Assert.False(op.ListPatch!.DeletedByOwner);
    }

    // ── chart playlist per-row rank movement (ItemAttributes.format_attributes, desktop-verified) ──────────────────────
    static Pl.Item ChartItem(string uri, params (string Key, string Value)[] pairs)
    {
        var attrs = new Pl.ItemAttributes();
        foreach (var (k, v) in pairs) attrs.FormatAttributes.Add(new Pl.FormatListAttribute { Key = k, Value = v });
        return new Pl.Item { Uri = uri, Attributes = attrs };
    }

    static PlaylistMember ParseOne(Pl.Item item)
    {
        var slc = new Pl.SelectedListContent();
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(item);
        slc.Contents = contents;
        return PlaylistWireMapper.ParseContents(slc).Members[0];
    }

    [Fact]
    public void ToMember_ParsesChartStatus_Up()
    {
        var m = ParseOne(ChartItem("spotify:track:a",
            ("status", "UP"), ("current_pos", "3"), ("previous_pos", "4"), ("rank", "41545")));
        Assert.NotNull(m.Chart);
        Assert.Equal(ChartEntryStatus.Up, m.Chart!.Value.Status);
        Assert.Equal(3, m.Chart.Value.CurrentPos);
        Assert.Equal(4, m.Chart.Value.PreviousPos);
        Assert.Equal(41545L, m.Chart.Value.Rank);
    }

    [Fact]
    public void ToMember_ParsesChartStatus_NewHasNoPreviousPos()
    {
        var m = ParseOne(ChartItem("spotify:track:b",
            ("status", "NEW"), ("current_pos", "22"), ("rank", "18579")));
        Assert.NotNull(m.Chart);
        Assert.Equal(ChartEntryStatus.New, m.Chart!.Value.Status);
        Assert.Equal(22, m.Chart.Value.CurrentPos);
        Assert.Equal(0, m.Chart.Value.PreviousPos);   // absent on the wire when NEW
        Assert.Equal(18579L, m.Chart.Value.Rank);
    }

    [Fact]
    public void ToMember_ParsesChartStatus_Equal()
    {
        var m = ParseOne(ChartItem("spotify:track:c",
            ("status", "EQUAL"), ("current_pos", "1"), ("previous_pos", "1"), ("rank", "49051")));
        Assert.NotNull(m.Chart);
        Assert.Equal(ChartEntryStatus.Equal, m.Chart!.Value.Status);
        Assert.Equal(1, m.Chart.Value.CurrentPos);
        Assert.Equal(1, m.Chart.Value.PreviousPos);
    }

    [Fact]
    public void ToMember_NoFormatAttributes_ChartIsNull()
    {
        var m = ParseOne(new Pl.Item { Uri = "spotify:track:d", Attributes = new Pl.ItemAttributes { AddedBy = "alice" } });
        Assert.Null(m.Chart);
    }

    [Fact]
    public void ToMember_UnknownChartStatus_MapsToUnknown()
    {
        var m = ParseOne(ChartItem("spotify:track:e", ("status", "SIDEWAYS"), ("current_pos", "9")));
        Assert.NotNull(m.Chart);
        Assert.Equal(ChartEntryStatus.Unknown, m.Chart!.Value.Status);
    }
}

// Map the collection2v2 DeltaResponse/PageResponse onto the domain CollectionDelta.
public class CollectionWireMapperTests
{
    [Fact]
    public void ParseDelta_MapsItemsAndToken()
    {
        var resp = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-9" };
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 100, IsRemoved = false });
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:album:b", IsRemoved = true });

        var delta = CollectionWireMapper.ParseDelta("albums", resp);
        Assert.Equal("albums", delta.SetId);
        Assert.Equal("tok-9", delta.NewRevision);
        Assert.Equal(2, delta.Items.Count);
        Assert.False(delta.Items[0].Removed);
        Assert.Equal(100_000L, delta.Items[0].AddedAt);   // wire added_at is int32 SECONDS; the domain carries ms
        Assert.True(delta.Items[1].Removed);
    }

    [Fact]
    public void ParsePage_MapsItemsAndToken()
    {
        var resp = new Col.PageResponse { SyncToken = "tok-1", NextPageToken = "" };
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a", AddedAt = 5 });

        var delta = CollectionWireMapper.ParsePage("liked", resp);
        Assert.Equal("liked", delta.SetId);
        Assert.Equal("tok-1", delta.NewRevision);
        Assert.Equal("spotify:track:a", Assert.Single(delta.Items).Uri);
    }
}
