using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Pins the <c>toIndex</c> convention of the playlist move op — the contract the drag-and-drop commit path
/// (<c>WaveeResourceDrop.DepositTracksAsync</c> → <c>MovePlaylistRowsAsync</c>) depends on, and the reason that path
/// does NOT pre-correct its insertion index the way <c>SidebarPane.MovePin</c> does for the rootlist.
///
/// The question: rows [1,2] moved to index 5 of a 10-row list — is 5 read BEFORE the rows are lifted out ("insert
/// before the row currently at index 5") or AFTER ("land at final position 5")? Both implementations answer
/// PRE-removal, and each discounts the lifted rows above the target internally, so a caller that subtracted
/// removedBefore itself would move the block two rows too far up. The wire shape changed in P2 (one item-keyed MOV
/// with an anchor instead of a run of positional MOVs); the seam convention this file pins did not.
/// </summary>
public class MoveRowsConventionTests
{
    static PlaylistRowRef Row(int index) => new(index, $"spotify:track:{index}", $"id{index}");

    static List<string> ApplyServerMove(int count, int[] selected, int toIndex)
    {
        var list = new List<PlaylistMember>(count);
        for (int i = 0; i < count; i++) list.Add(new PlaylistMember($"id{i}", $"spotify:track:{i}", null, 0));
        var op = PlaylistMutationSource.BuildKeyedMove(list, selected.Select(Row).ToArray(), toIndex);
        if (op is not null) PlaylistDiffApplier.Apply(list, new[] { op });   // null = the drop changes nothing
        return list.Select(m => m.ItemId).ToList();
    }

    [Fact]
    public void ServerMove_ReadsToIndexBeforeTheRowsAreLifted()
    {
        // 10 rows, lift [1,2], toIndex 5.
        //   PRE-removal reading  → the block lands immediately before original row 5 ⇒ final positions 3,4.
        //   POST-removal reading → the block would land AT final positions 5,6.
        var moved = ApplyServerMove(10, new[] { 1, 2 }, 5);

        Assert.Equal(
            new[] { "id0", "id3", "id4", "id1", "id2", "id5", "id6", "id7", "id8", "id9" },
            moved);
        Assert.Equal(3, moved.IndexOf("id1"));                  // NOT 5 — the op discounts the two rows lifted above it
        Assert.Equal("id5", moved[5]);                          // the row that was at toIndex still follows the block
    }

    [Fact]
    public void ServerMove_UpwardIsUnaffectedByTheConvention()
    {
        // Nothing is removed above the target, so pre- and post-removal readings coincide — the asymmetry the
        // caller would have to reason about if it corrected the index itself.
        Assert.Equal(
            new[] { "id5", "id6", "id0", "id1", "id2", "id3", "id4", "id7", "id8", "id9" },
            ApplyServerMove(10, new[] { 5, 6 }, 0));
    }

    [Fact]
    public void ServerMove_AppendUsesTheListLength()
    {
        Assert.Equal(
            new[] { "id0", "id3", "id4", "id5", "id6", "id7", "id8", "id9", "id1", "id2" },
            ApplyServerMove(10, new[] { 1, 2 }, 10));
    }

    [Fact]
    public void LocalMove_ImplementsTheSameConvention()
    {
        // The in-process source is the other implementation behind MovePlaylistRowsAsync (wavee:playlist:* targets);
        // it must not disagree with the server op, or a drop would land differently per playlist kind.
        var source = new UserPlaylistSource();
        string uri = source.CreatePlaylist("Convention");
        for (int i = 0; i < 10; i++)
            source.AddTrack(uri, new Track($"t{i}", $"spotify:track:{i}", $"T{i}",
                Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1, false, null));

        var before = source.ResolveContext(uri)!;
        var rows = new[]
        {
            new PlaylistRowRef(1, before[1].Uri, before[1].ContextUid!),
            new PlaylistRowRef(2, before[2].Uri, before[2].ContextUid!),
        };
        source.MoveRows(uri, rows, 5);

        Assert.Equal(
            new[] { "T0", "T3", "T4", "T1", "T2", "T5", "T6", "T7", "T8", "T9" },
            source.ResolveContext(uri)!.Select(t => t.Title));
    }

    [Fact]
    public void PreCorrectingTheIndexWouldOvershoot()
    {
        // The negative control for the SidebarPane.MovePin-style `slot > at ? slot - 1 : slot` correction: applying it
        // on top of an op that already discounts removals moves the block two rows too far up. This is why the
        // deposit path passes OriginalInsertionIndex(displaySlot) through unmodified.
        const int at = 5;
        int removedBefore = new[] { 1, 2 }.Count(i => i < at);
        Assert.Equal(2, removedBefore);
        Assert.Equal(
            new[] { "id0", "id1", "id2", "id3", "id4", "id5", "id6", "id7", "id8", "id9" },
            ApplyServerMove(10, new[] { 1, 2 }, at - removedBefore));   // a pure no-op: the block never leaves index 1
    }
}
