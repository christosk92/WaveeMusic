using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// The recents-page boundary mapper (GET /playlist/v2/list/recents/page → RecentsItem[]), driven against CRAFTED
// playlist4 protos in the PlaylistWireMapper/SpotifyExportMapper style. What is pinned here is the thing that makes
// recents different from every other playlist4 list: the semantic payload lives in the format_attribute KEYS (the
// values are empty), and the ONE valued attribute (`group_metadata`) is base64 of a nested proto.
public class RecentsWireMapperTests
{
    // ── item_id → lowercase hex (the reconciler key), and a group header's uri may legitimately be "" ────────────────
    [Fact]
    public void ItemId_MapsToLowercaseHex_AndHeaderUriMayBeEmpty()
    {
        var slc = RecentsWire.Page(null,
            RecentsWire.Item("", itemId: [0x0A, 0xFF, 0x10, 0x00], ts: 1_700_000_000_000L,
                RecentsWire.Attr("group_id_0"),
                RecentsWire.Attr("children_group_id", "7")));

        var (items, _) = RecentsWireMapper.Map(slc);

        var item = Assert.Single(items);
        Assert.Equal("0aff1000", item.ItemId);                 // hex, lowercase, no dashes
        Assert.Equal("", item.Uri);                            // single-context header carries no uri of its own
        Assert.Equal(1_700_000_000_000L, item.PlayedAtMs);
    }

    // ── the KEY is the payload: group_id_<N> / children_group_id / recent_type_* / content_type_* ─────────────────────
    [Fact]
    public void FormatAttributeKeys_AreParsed_ValuesAreIgnored()
    {
        var slc = RecentsWire.Page(null,
            // a header: group_id_0 + children_group_id, played, music
            RecentsWire.Item("spotify:playlist:h", itemId: [0x01], ts: 5,
                RecentsWire.Attr("group_id_0"),
                RecentsWire.Attr("children_group_id", "12"),
                RecentsWire.Attr("recent_type_played"),
                RecentsWire.Attr("content_type_music")),
            // a collapsed member of group 12: the group id is in the KEY, the value is empty
            RecentsWire.Item("spotify:track:m", itemId: [0x02], ts: 4,
                RecentsWire.Attr("group_id_12"),
                RecentsWire.Attr("recent_type_played"),
                RecentsWire.Attr("content_type_music")),
            // a saved podcast single: no group keys at all
            RecentsWire.Item("spotify:show:s", itemId: [0x03], ts: 3,
                RecentsWire.Attr("recent_type_saved"),
                RecentsWire.Attr("content_type_podcasts")));

        var (items, _) = RecentsWireMapper.Map(slc);

        Assert.Equal(3, items.Count);

        Assert.True(items[0].HasChildrenGroupId);
        Assert.Equal(0, items[0].GroupId);
        Assert.Equal(RecentsReason.Played, items[0].Reason);
        Assert.Equal("music", items[0].ContentType);

        Assert.False(items[1].HasChildrenGroupId);
        Assert.Equal(12, items[1].GroupId);                    // parsed out of the KEY, not the value
        Assert.Equal(RecentsReason.Played, items[1].Reason);

        Assert.Null(items[2].GroupId);
        Assert.False(items[2].HasChildrenGroupId);
        Assert.Equal(RecentsReason.Saved, items[2].Reason);
        Assert.Equal("podcasts", items[2].ContentType);
    }

    // ── group_metadata: base64 VALUE → RecentsGroupMetadata → proto-free RecentsGroupInfo (incl. the Kind facet) ──────
    [Fact]
    public void GroupMetadata_Base64Proto_DecodesChildCountUrisAndKind()
    {
        var slc = RecentsWire.Page(null,
            RecentsWire.Item("", itemId: [0x11], ts: 9,
                RecentsWire.Attr("group_id_0"),
                RecentsWire.Attr("children_group_id", "3"),
                RecentsWire.Attr("group_metadata", RecentsWire.GroupMetadata(
                    childCount: 11,
                    childUris: ["spotify:track:a", "spotify:track:b", "spotify:track:c"],
                    kindName: "Album", kindCount: 11))));

        var (items, _) = RecentsWireMapper.Map(slc);

        var group = Assert.Single(items).Group;
        Assert.NotNull(group);
        Assert.Equal(11, group!.ChildCount);                   // authoritative
        Assert.Equal(3, group.ChildUris.Count);                // the wire list is TRUNCATED — never the count
        Assert.Equal("spotify:track:a", group.ChildUris[0]);
        Assert.Equal("Album", group.KindName);
        Assert.Equal(11, group.KindCount);
    }

    // ── a malformed group_metadata drops that ONE header's group and keeps the rest of the ~9k-item batch ─────────────
    [Fact]
    public void MalformedGroupMetadata_SkipsThatHeaderOnly_BatchSurvives()
    {
        var slc = RecentsWire.Page(null,
            RecentsWire.Item("spotify:playlist:bad-b64", itemId: [0x21], ts: 3,
                RecentsWire.Attr("group_id_0"),
                RecentsWire.Attr("group_metadata", "!!! not base64 !!!")),
            RecentsWire.Item("spotify:playlist:bad-proto", itemId: [0x22], ts: 2,
                RecentsWire.Attr("group_id_0"),
                // valid base64, truncated varint → not a parseable RecentsGroupMetadata
                RecentsWire.Attr("group_metadata", Convert.ToBase64String([0xFF, 0xFF, 0xFF]))),
            RecentsWire.Item("spotify:playlist:good", itemId: [0x23], ts: 1,
                RecentsWire.Attr("group_id_0"),
                RecentsWire.Attr("group_metadata", RecentsWire.GroupMetadata(4, ["spotify:track:a"]))));

        var (items, _) = RecentsWireMapper.Map(slc);

        Assert.Equal(3, items.Count);            // nothing was dropped from the batch
        Assert.Null(items[0].Group);             // …the bad ones just carry no decoded group
        Assert.Null(items[1].Group);
        Assert.NotNull(items[2].Group);
        Assert.Equal(4, items[2].Group!.ChildCount);
    }

    // ── the revision rides out as raw bytes (round-trippable for the next /diff); an absent one is null ──────────────
    [Fact]
    public void Revision_IsCarriedOut_AndAbsentContentsMapsToNoItems()
    {
        var rev = RecentsWire.Rev(3396, 0xAB, 0xCD);
        var (items, mapped) = RecentsWireMapper.Map(RecentsWire.Page(rev, RecentsWire.Item("spotify:track:a", [0x01])));
        Assert.Equal(rev, mapped);
        Assert.Single(items);

        // a body with NO contents (the /diff no-change shape) maps to zero items — which is exactly why the diff path
        // must never turn one into a snapshot (B-1).
        var (none, rev2) = RecentsWireMapper.Map(new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev) });
        Assert.Empty(none);
        Assert.Equal(rev, rev2);
    }
}

/// <summary>Crafted-proto builders for the recents wire shape, shared by the three Recents test files.</summary>
internal static class RecentsWire
{
    /// <summary>A recents format_attribute. Almost every one is KEY-ONLY — the value stays unset unless given.</summary>
    public static Pl.FormatListAttribute Attr(string key, string? value = null)
    {
        var fa = new Pl.FormatListAttribute { Key = key };
        if (value is not null) fa.Value = value;
        return fa;
    }

    public static Pl.Item Item(string uri, byte[]? itemId = null, long ts = 0, params Pl.FormatListAttribute[] attrs)
    {
        var attributes = new Pl.ItemAttributes();
        if (itemId is not null) attributes.ItemId = ByteString.CopyFrom(itemId);
        if (ts != 0) attributes.Timestamp = ts;
        for (int i = 0; i < attrs.Length; i++) attributes.FormatAttributes.Add(attrs[i]);
        return new Pl.Item { Uri = uri, Attributes = attributes };
    }

    /// <summary>The base64 VALUE of a header's <c>group_metadata</c> attribute.</summary>
    public static string GroupMetadata(int childCount, string[] childUris, string? kindName = null, int kindCount = 0)
    {
        var gm = new Pl.RecentsGroupMetadata { ChildCount = childCount };
        for (int i = 0; i < childUris.Length; i++) gm.ChildUri.Add(childUris[i]);
        if (kindName is not null) gm.Kind = new Pl.RecentsGroupMetadata.Types.Kind { Name = kindName, Count = kindCount };
        return Convert.ToBase64String(gm.ToByteArray());
    }

    /// <summary>A full recents page reply: revision + <c>contents</c> holding the items in wire order.</summary>
    public static Pl.SelectedListContent Page(byte[]? revision, params Pl.Item[] items)
    {
        var slc = new Pl.SelectedListContent();
        if (revision is not null) slc.Revision = ByteString.CopyFrom(revision);
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        for (int i = 0; i < items.Length; i++) contents.Items.Add(items[i]);
        slc.Contents = contents;
        return slc;
    }

    /// <summary>A playlist4 wire revision: 4-byte big-endian counter + hash bytes.</summary>
    public static byte[] Rev(int counter, params byte[] hash)
    {
        var bytes = new byte[4 + hash.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, counter);
        hash.CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Flat items → the grouped rows a resident page holds.</summary>
    public static IReadOnlyList<RecentsRow> Rows(Pl.SelectedListContent slc)
        => RecentsList.Group(RecentsWireMapper.Map(slc).Items);
}
