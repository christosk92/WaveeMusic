using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// PlaylistMutationSource.BuildKeyedMove — the ONE playlist reorder shape (P2). A (possibly gapped) selection becomes
// exactly ONE item-keyed MOV: every moved row named by item_id, one anchor for the destination. The op is verified by
// applying it through PlaylistDiffApplier and comparing the resulting order, because the op alone says nothing about
// where rows land — the anchor does.
public class PlaylistMoveOpsTests
{
    static PlaylistRowRef Row(int index) => new(index, $"spotify:track:{index}", $"id{index}");

    static List<PlaylistMember> Membership(int count)
    {
        var list = new List<PlaylistMember>(count);
        for (int i = 0; i < count; i++) list.Add(new PlaylistMember($"id{i}", $"spotify:track:{i}", null, 0));
        return list;
    }

    /// <summary>Applies BuildKeyedMove to a list of <paramref name="count"/> rows named by original index.</summary>
    static List<string> Simulate(int count, int[] selected, int toIndex)
    {
        var list = Membership(count);
        var op = PlaylistMutationSource.BuildKeyedMove(list, selected.Select(Row).ToArray(), toIndex);
        if (op is not null) PlaylistDiffApplier.Apply(list, new[] { op });
        return list.Select(m => m.ItemId).ToList();
    }

    static List<string> Expected(int count, int[] selected, int toIndex)
    {
        var sel = new SortedSet<int>(selected);
        var result = new List<string>(count);
        foreach (int i in Enumerable.Range(0, count).Where(i => !sel.Contains(i) && i < toIndex)) result.Add($"id{i}");
        foreach (int i in sel) result.Add($"id{i}");
        foreach (int i in Enumerable.Range(0, count).Where(i => !sel.Contains(i) && i >= toIndex)) result.Add($"id{i}");
        return result;
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, 0)]        // contiguous run up
    [InlineData(new[] { 1, 2 }, 5)]        // contiguous run to end
    [InlineData(new[] { 0, 2 }, 5)]        // gapped selection down
    [InlineData(new[] { 1, 3 }, 0)]        // gapped selection up
    [InlineData(new[] { 0, 2, 4 }, 2)]     // gapped selection into the middle
    [InlineData(new[] { 4 }, 0)]           // single row up
    [InlineData(new[] { 0 }, 5)]           // single row to end
    [InlineData(new[] { 0, 1, 2, 3, 4 }, 0)] // everything (no-op)
    public void GappedAndContiguousMoves_LandInSelectionOrderAtTarget(int[] selected, int toIndex)
    {
        Assert.Equal(Expected(5, selected, toIndex), Simulate(5, selected, toIndex));
    }

    // The A 148 shape: a gapped multi-select is ONE op carrying N items and ONE anchor — never a run of range moves.
    // The anchor walks back over rows that are themselves being moved, which is exactly what makes the gapped selection
    // arrive as one contiguous block.
    [Fact]
    public void KeyedMove_GappedSelection_OneOp_LandsAfterAnchor()
    {
        var list = Membership(6);
        var op = PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(1), Row(3) }, 5);
        Assert.NotNull(op);
        Assert.Equal(PlaylistOpKind.Move, op!.Kind);
        Assert.True(op.ItemsAsKey);
        Assert.Equal(0, op.FromIndex);
        Assert.Equal(0, op.Length);
        Assert.Equal(0, op.ToIndex);
        Assert.Equal(new[] { "id1", "id3" }, op.Items!.Select(i => i.ItemId).ToArray());
        Assert.Equal(PlaylistMoveAnchorKind.AfterItem, op.Anchor!.Value.Kind);
        Assert.Equal("id4", op.Anchor.Value.AfterItemId);
        Assert.Equal("spotify:track:4", op.AnchorUri);

        PlaylistDiffApplier.Apply(list, new[] { op });
        Assert.Equal(new[] { "id0", "id2", "id4", "id1", "id3", "id5" }, list.Select(m => m.ItemId).ToArray());
    }

    // Dropping onto slot 0 — and dropping onto a slot whose every predecessor is itself selected — is add_first.
    [Fact]
    public void KeyedMove_ToTop_AddFirst()
    {
        var list = Membership(4);
        var top = PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(2) }, 0);
        Assert.Equal(PlaylistMoveAnchorKind.First, top!.Anchor!.Value.Kind);
        Assert.Null(top.AnchorUri);

        // Slot 1's only predecessor is row 0, which is itself moving → walking back runs out → add_first.
        var walked = PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(0), Row(3) }, 1);
        Assert.Equal(PlaylistMoveAnchorKind.First, walked!.Anchor!.Value.Kind);
        var applied = Membership(4);
        PlaylistDiffApplier.Apply(applied, new[] { walked });
        Assert.Equal(new[] { "id0", "id3", "id1", "id2" }, applied.Select(m => m.ItemId).ToArray());
    }

    [Fact]
    public void KeyedMove_ToEnd_AddLast()
    {
        var list = Membership(4);
        var op = PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(0) }, 4);
        Assert.Equal(PlaylistMoveAnchorKind.Last, op!.Anchor!.Value.Kind);
        PlaylistDiffApplier.Apply(list, new[] { op });
        Assert.Equal(new[] { "id1", "id2", "id3", "id0" }, list.Select(m => m.ItemId).ToArray());
    }

    // A row (or the anchor) with no item_id yet is an unfinished optimistic insert. There is NO positional fallback:
    // sending indices while our own add is still in flight is exactly how rows land in the wrong place.
    [Fact]
    public void KeyedMove_MissingItemId_ThrowsPending()
    {
        var list = Membership(4);
        var unkeyedRow = Assert.Throws<PlaylistMutationException>(() =>
            PlaylistMutationSource.BuildKeyedMove(list, new[] { new PlaylistRowRef(1, "spotify:track:1", "") }, 3));
        Assert.Equal(PlaylistMutationFailure.Pending, unkeyedRow.Kind);

        list[1] = list[1] with { ItemId = "" };   // the ANCHOR is the unkeyed one this time
        var unkeyedAnchor = Assert.Throws<PlaylistMutationException>(() =>
            PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(3) }, 2));
        Assert.Equal(PlaylistMutationFailure.Pending, unkeyedAnchor.Kind);
    }

    // Dropping a selection back onto its own position must not spend a write.
    [Fact]
    public void KeyedMove_NoOp_ReturnsNull()
    {
        var list = Membership(5);
        Assert.Null(PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(0), Row(1) }, 0));
        Assert.Null(PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(1), Row(2) }, 1));
        Assert.Null(PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(4) }, 5));
        Assert.Null(PlaylistMutationSource.BuildKeyedMove(list, new[] { Row(0), Row(1), Row(2), Row(3), Row(4) }, 0));
    }

    // Remove is keyed too (A 143): one Delta, one keyed REM per row, no index anywhere — so a duplicate uri removes the
    // right ROW. Only a batch containing a row with no id falls back to descending index REMs, and then wholesale.
    [Fact]
    public void RemoveRows_KeyedWhenEveryRowHasAnId_ElseDescendingIndex()
    {
        var keyed = PlaylistMutationSource.BuildRemoveOps(new[] { Row(1), Row(4), Row(7) });
        Assert.Equal(3, keyed.Count);
        Assert.All(keyed, op =>
        {
            Assert.Equal(PlaylistOpKind.Remove, op.Kind);
            Assert.True(op.ItemsAsKey);
            Assert.Equal(0, op.FromIndex);
            Assert.Equal(0, op.Length);
            Assert.Single(op.Items!);
        });
        Assert.Equal(new[] { "id1", "id4", "id7" }, keyed.Select(o => o.Items![0].ItemId).ToArray());

        var mixed = PlaylistMutationSource.BuildRemoveOps(new[]
        {
            Row(1), new PlaylistRowRef(4, "spotify:track:4", ""), Row(7),
        });
        Assert.All(mixed, op => Assert.False(op.ItemsAsKey));                  // ALL positional — never a mixed Delta
        Assert.Equal(new[] { 7, 4, 1 }, mixed.Select(o => o.FromIndex).ToArray());   // descending
    }

    [Fact]
    public void RootlistMove_MovesBalancedFolderAsOneRange_AndSupportsInside()
    {
        RootlistEntry[] entries =
        [
            new(0, 0, "spotify:playlist:a", null, 0),
            new(1, 1, "spotify:start-group:g:Folder", "Folder", 0),
            new(2, 0, "spotify:playlist:b", null, 1),
            new(3, 0, "spotify:playlist:c", null, 1),
            new(4, 2, "spotify:end-group:g", null, 0),
            new(5, 0, "spotify:playlist:d", null, 0),
        ];

        Assert.True(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("spotify:playlist:a", false), new RootlistItemRef("g", true),
            RootlistDropPlacement.Inside, out var inside));
        Assert.Equal((0, 1, 4), (inside!.FromIndex, inside.Length, inside.ToIndex));

        Assert.True(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("g", true), new RootlistItemRef("spotify:playlist:d", false),
            RootlistDropPlacement.After, out var folder));
        Assert.Equal((1, 4, 6), (folder!.FromIndex, folder.Length, folder.ToIndex));
    }

    [Fact]
    public void RootlistMove_RejectsFolderIntoItsOwnSubtree()
    {
        RootlistEntry[] entries =
        [
            new(0, 1, "spotify:start-group:g:Folder", "Folder", 0),
            new(1, 0, "spotify:playlist:b", null, 1),
            new(2, 2, "spotify:end-group:g", null, 0),
        ];
        Assert.False(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("g", true), new RootlistItemRef("spotify:playlist:b", false),
            RootlistDropPlacement.Before, out _));
    }

    [Fact]
    public void LocalPlaylist_DuplicateMembershipsKeepStableIndependentRowIds()
    {
        var source = new UserPlaylistSource();
        string uri = source.CreatePlaylist("Drag target");
        var a = new Track("a", "spotify:track:a", "A", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 1, false, null);
        var b = new Track("b", "spotify:track:b", "B", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 1, false, null);

        source.AddTrack(uri, a);
        source.AddTrack(uri, a);
        source.InsertTracks(uri, [b], 1);
        var before = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "A", "B", "A" }, before.Select(t => t.Title));
        Assert.NotEqual(before[0].ContextUid, before[2].ContextUid);

        string firstUid = before[0].ContextUid!;
        string secondUid = before[2].ContextUid!;
        source.MoveRows(uri, [new PlaylistRowRef(0, a.Uri, firstUid)], 3);
        var moved = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "B", "A", "A" }, moved.Select(t => t.Title));
        Assert.Equal(firstUid, moved[2].ContextUid);

        source.RemoveRows(uri, [new PlaylistRowRef(1, a.Uri, secondUid)]);
        var removed = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "B", "A" }, removed.Select(t => t.Title));
        Assert.Equal(firstUid, removed[1].ContextUid);
    }
}
