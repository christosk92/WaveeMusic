using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The pure, proto-free ordered-membership op applier — the heart of incremental playlist sync. Indices are interpreted
// against the evolving list (relative to all preceding ops), matching the reference /diff semantics.
public class PlaylistDiffApplierTests
{
    static List<PlaylistMember> List(params string[] ids) =>
        ids.Select(u => new PlaylistMember(u, "spotify:track:" + u, null, 0)).ToList();
    static string[] Ids(IReadOnlyList<PlaylistMember> l) =>
        l.Select(m => m.ItemUri.Replace("spotify:track:", "")).ToArray();
    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);

    [Fact]
    public void Add_AtIndex_Inserts()
    {
        var l = List("a", "b", "c");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Add, FromIndex: 1, Items: new[] { M("x") }) });
        Assert.Equal(new[] { "a", "x", "b", "c" }, Ids(l));
    }

    [Fact]
    public void Add_First_And_Last()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, AddFirst: true, Items: new[] { M("h") }),
            new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("t") }),
        });
        Assert.Equal(new[] { "h", "a", "b", "t" }, Ids(l));
    }

    [Fact]
    public void Remove_Range()
    {
        var l = List("a", "b", "c", "d");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 2) });
        Assert.Equal(new[] { "a", "d" }, Ids(l));
    }

    [Fact]
    public void Move_ToIndexIsPreRemoval()
    {
        var l = List("a", "b", "c", "d");
        // move [a] (index 0, len 1) to post-removal index 2: remove → b,c,d ; insert a at 2 → b,c,a,d
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Move, FromIndex: 0, Length: 1, ToIndex: 2) });
        Assert.Equal(new[] { "b", "a", "c", "d" }, Ids(l));
    }

    [Theory]
    [InlineData(0, 1, 4, new[] { "b", "c", "d", "a" })]
    [InlineData(0, 2, 4, new[] { "c", "d", "a", "b" })]
    [InlineData(2, 1, 0, new[] { "c", "a", "b", "d" })]
    [InlineData(1, 1, 2, new[] { "a", "b", "c", "d" })]
    public void Move_PreRemovalCases(int from, int len, int to, string[] expected)
    {
        var l = List("a", "b", "c", "d");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Move, FromIndex: from, Length: len, ToIndex: to) });
        Assert.Equal(expected, Ids(l));
    }

    [Fact]
    public void Sequence_IndicesAreRelativeToEvolvingList()
    {
        var l = List("a", "b", "c");
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1),                       // → b,c
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 1, Items: new[] { M("x") }),            // → b,x,c
        });
        Assert.Equal(new[] { "b", "x", "c" }, Ids(l));
    }

    [Fact]
    public void UpdateItem_MergesAddedAttributes()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: 1, Items: new[] { new PlaylistMember("b", "spotify:track:b", "alice", 999) }) });
        Assert.Equal("alice", l[1].AddedBy);
        Assert.Equal(999, l[1].AddedAt);
    }

    [Fact]
    public void UpdateList_IsNoOp_OnMembership()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.UpdateList) });
        Assert.Equal(new[] { "a", "b" }, Ids(l));   // list-level attrs (name/desc) are the header's concern, not membership
    }

    [Fact]
    public void Remove_IndexWithMatchingItems_Applies()
    {
        var l = List("a", "b", "c", "d");
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 2, Items: new[] { M("b"), M("c") }),
        });
        Assert.Equal(new[] { "a", "d" }, Ids(l));
    }

    [Fact]
    public void Remove_IndexWithMismatchedItems_Throws()
    {
        var l = new List<PlaylistMember>
        {
            new("row-a", "spotify:track:a", null, 0),
            new("row-b", "spotify:track:b", null, 0),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l, new[]
            {
                new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 1,
                    Items: new[] { new PlaylistMember("other-row", "spotify:track:b", null, 0) }),
            }));
    }

    [Fact]
    public void CapturedEventStream_MatchesServerCheckpoints()
    {
        var l = List("t1", "t2", "t3", "t4");

        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 3, Items: new[] { M("X1") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 4, Items: new[] { M("X2") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 5, Items: new[] { M("X3") }),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 4, Length: 1, ToIndex: 2),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 1, Length: 1, ToIndex: 0),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 5, Length: 1, ToIndex: 0),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 3, Length: 1, ToIndex: 6),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 6, Items: new[] { M("X4") }),
        });
        Assert.Equal(new[] { "X3", "t2", "t1", "t3", "X1", "X2", "X4", "t4" }, Ids(l));

        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 4, Length: 3, Items: new[] { M("X1"), M("X2"), M("X4") }),
        });
        Assert.Equal(new[] { "X3", "t2", "t1", "t3", "t4" }, Ids(l));

        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 4, Items: new[] { M("Y1") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 5, Items: new[] { M("Y2") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 6, Items: new[] { M("Y3") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 7, Items: new[] { M("Y4") }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 8, Items: new[] { M("Y5") }),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 4, Length: 5, ToIndex: 0),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 0, Length: 1, ToIndex: 2),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 4, Length: 1, ToIndex: 3),
            new PlaylistOp(PlaylistOpKind.Move, FromIndex: 7, Length: 2, ToIndex: 4),
        });
        Assert.Equal(new[] { "Y2", "Y1", "Y3", "Y5", "t1", "t3", "Y4", "X3", "t2", "t4" }, Ids(l));
    }

    // ── keyed ops (P2): rows are addressed by item_id, never by position ─────────────────────────────────────────────

    static PlaylistOp KeyedMove(PlaylistMoveAnchor anchor, params PlaylistMember[] items)
        => new(PlaylistOpKind.Move, ItemsAsKey: true, Items: items, Anchor: anchor);

    // Two membership rows for the SAME track are different rows. The keyed MOV must move the one whose item_id it
    // names — resolving by uri would move whichever came first and silently reorder the wrong copy.
    [Fact]
    public void ApplyMove_Keyed_ByItemId_PrefersIdOverUri_Duplicates()
    {
        var l = new List<PlaylistMember>
        {
            new("row1", "spotify:track:dup", null, 0),
            new("row2", "spotify:track:other", null, 0),
            new("row3", "spotify:track:dup", null, 0),
            new("row4", "spotify:track:tail", null, 0),
        };

        // Move the SECOND copy of :dup (row3) to the head.
        PlaylistDiffApplier.Apply(l, new[]
        {
            KeyedMove(new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First),
                new PlaylistMember("row3", "spotify:track:dup", null, 0)),
        });
        Assert.Equal(new[] { "row3", "row1", "row2", "row4" }, l.Select(m => m.ItemId).ToArray());

        // …and a gapped pair lands as one contiguous run immediately after the anchor, in the op order.
        PlaylistDiffApplier.Apply(l, new[]
        {
            KeyedMove(new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, "row2"),
                new PlaylistMember("row3", "spotify:track:dup", null, 0),
                new PlaylistMember("row4", "spotify:track:tail", null, 0)),
        });
        Assert.Equal(new[] { "row1", "row2", "row3", "row4" }, l.Select(m => m.ItemId).ToArray());
    }

    // The anchor is a row identity. If somebody else removed it between build and apply, the op no longer describes a
    // reachable destination: that is TORN, and the caller refetches rather than dropping the rows at a guess.
    [Fact]
    public void ApplyMove_Keyed_AnchorMissing_Throws()
    {
        var l = List("a", "b", "c");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l, new[]
            {
                KeyedMove(new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, "gone"), M("a")),
            }));

        // …and so is a moved ROW that is no longer there.
        var l2 = List("a", "b");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l2, new[]
            {
                KeyedMove(new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First), M("zzz")),
            }));
    }

    [Fact]
    public void ApplyRemove_Keyed_ItemId_Duplicates()
    {
        var l = new List<PlaylistMember>
        {
            new("row1", "spotify:track:dup", null, 0),
            new("row2", "spotify:track:dup", null, 0),
            new("row3", "spotify:track:dup", null, 0),
        };

        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
                Items: new[] { new PlaylistMember("row2", "spotify:track:dup", null, 0) }),
        });
        Assert.Equal(new[] { "row1", "row3" }, l.Select(m => m.ItemId).ToArray());

        // A keyed REM naming an id that is not in the list is torn — our baseline drifted.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l, new[]
            {
                new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
                    Items: new[] { new PlaylistMember("row2", "spotify:track:dup", null, 0) }),
            }));
    }

    // The rootlist unfollow keys on the uri alone (rootlist entries carry no row ids) and must stay no-op-on-absent:
    // the optimistic edit already removed the row locally, so local absence proves nothing about the server.
    [Fact]
    public void ApplyRemove_KeyedWithoutItemId_IsNoOpOnAbsent()
    {
        var l = new List<PlaylistMember> { new("", "spotify:playlist:a", null, 0) };
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
                Items: new[] { new PlaylistMember("", "spotify:playlist:gone", null, 0) }),
        });
        Assert.Single(l);
    }

    [Theory]
    [InlineData(PlaylistOpKind.Remove, 5, 1, 0)]   // REM out of range
    [InlineData(PlaylistOpKind.Add, 9, 0, 0)]      // ADD index past end
    [InlineData(PlaylistOpKind.Move, 0, 1, 9)]     // MOV dest out of range
    public void OutOfRange_Throws_SoCallerCanFullRefetch(PlaylistOpKind kind, int from, int len, int to)
    {
        var l = List("a");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(kind, FromIndex: from, Length: len, ToIndex: to, Items: new[] { M("z") }) }));
    }
}
