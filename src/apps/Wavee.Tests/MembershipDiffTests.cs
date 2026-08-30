using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase 7 (§4.6) — the pure keyed row diff behind the realtime tracklist choreography: identity via ContextUid (ItemId)
// with a uri#occurrence fallback, structural-only reset classification, and shift-displaced survivors in Moves.
public class MembershipDiffTests
{
    static Track T(string id, string? uid = null) => new(
        Id: id, Uri: "spotify:track:" + id, Title: id, Artists: Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""), DurationMs: 1000, IsExplicit: false, Image: null, ContextUid: uid);

    static IReadOnlyList<Track> Tracks(params string[] ids) => ids.Select(i => T(i, "uid-" + i)).ToArray();

    [Fact]
    public void SingleMove_YieldsMoveChangesOnly_NoReset()
    {
        // a→c b→a c→b is one MOV: every survivor's index changed, zero structural changes.
        var d = MembershipDiff.Diff(Tracks("a", "b", "c"), Tracks("c", "a", "b"));
        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(3, d.Moves.Count);
        Assert.Equal(1.0, d.RetainedFraction);
        Assert.False(d.IsReset);
        var c = d.Moves.Single(m => m.Key == "uid-c");
        Assert.Equal((2, 0), (c.OldIndex!.Value, c.NewIndex!.Value));
    }

    [Fact]
    public void InsertAtTop_OneAdd_SurvivorsShift_NotAReset()
    {
        var old = Tracks(Enumerable.Range(0, 100).Select(i => "t" + i).ToArray());
        var next = new List<Track> { T("new", "uid-new") };
        next.AddRange(old);

        var d = MembershipDiff.Diff(old, next.ToArray());
        Assert.Single(d.Adds);
        Assert.Equal((null, 0), (d.Adds[0].OldIndex, d.Adds[0].NewIndex!.Value));
        Assert.Empty(d.Removes);
        Assert.Equal(100, d.Moves.Count);          // every survivor displaced by one (the FLIP pass needs these)
        Assert.False(d.IsReset);                   // ONE structural change — never a reset despite 100 shifted rows
    }

    [Fact]
    public void RemoveAndAdd_Combined()
    {
        var d = MembershipDiff.Diff(Tracks("a", "b", "c"), Tracks("a", "x", "c"));
        Assert.Equal("uid-x", Assert.Single(d.Adds).Key);
        Assert.Equal("uid-b", Assert.Single(d.Removes).Key);
        Assert.Equal(1, Assert.Single(d.Removes).OldIndex);
        Assert.Empty(d.Moves);                     // a and c kept their indices
        Assert.False(d.IsReset);
    }

    [Fact]
    public void CuratedRecut_MostContentReplaced_IsReset()
    {
        // Discover-Weekly style: 30 rows all replaced → retained 0 → reset (whole-list crossfade, no row storm).
        var old = Tracks(Enumerable.Range(0, 30).Select(i => "old" + i).ToArray());
        var next = Tracks(Enumerable.Range(0, 30).Select(i => "new" + i).ToArray());
        var d = MembershipDiff.Diff(old, next);
        Assert.True(d.IsReset);
        Assert.Equal(0.0, d.RetainedFraction);
    }

    [Fact]
    public void BulkEdit_ManyStructuralChanges_IsReset()
    {
        // retained fraction high (100 survive of 141) but 41 adds > the structural cap → still a reset.
        var old = Tracks(Enumerable.Range(0, 141).Select(i => "t" + i).ToArray());
        var next = old.Take(100).Concat(Tracks(Enumerable.Range(0, 41).Select(i => "n" + i).ToArray())).ToArray();
        var d = MembershipDiff.Diff(old, next);
        Assert.Equal(41 + 41, d.Adds.Count + d.Removes.Count);
        Assert.True(d.IsReset);
    }

    [Fact]
    public void FirstFill_EmptyOld_NeverAReset()
    {
        var d = MembershipDiff.Diff(Array.Empty<Track>(), Tracks("a", "b"));
        Assert.Equal(2, d.Adds.Count);
        Assert.False(d.IsReset);
        Assert.Equal(1.0, d.RetainedFraction);
    }

    [Fact]
    public void NoChange_IsEmpty()
    {
        var d = MembershipDiff.Diff(Tracks("a", "b"), Tracks("a", "b"));
        Assert.True(d.IsEmpty);
        Assert.False(d.IsReset);
    }

    [Fact]
    public void DuplicateUris_WithoutItemIds_StayDistinctViaOccurrence()
    {
        // no ContextUid → uri#occurrence keys; removing ONE of two duplicate rows is one remove, the other survives.
        var a1 = T("dup"); var a2 = T("dup"); var b = T("b");
        var d = MembershipDiff.Diff(new[] { a1, a2, b }, new[] { a1, b });
        Assert.Equal("spotify:track:dup#1", Assert.Single(d.Removes).Key);
        Assert.Empty(d.Adds);
        Assert.Single(d.Moves);                    // b shifted 2→1
    }

    // ── per-ROW identity (RowKey / RowKeyMatches) ────────────────────────────────────────────────────────────────
    // The same rule the diff keys on, reachable for one row at a time. It exists because per-row UI STATE (the
    // versions drawer) used to be keyed by TRACK URI: a playlist that legitimately holds the same song twice then
    // expanded EVERY row carrying that uri at once, and minted duplicate reconciler keys for their drawers.

    [Fact]
    public void RowKey_PrefersContextUid_SoDuplicateUrisAreDistinctRows()
    {
        var first = T("dup", "uid-1");
        var second = T("dup", "uid-2");   // same track uri, a second membership row
        Assert.Equal("uid-1", MembershipDiff.RowKey(first, 0));
        Assert.Equal("uid-2", MembershipDiff.RowKey(second, 7));
        Assert.NotEqual(MembershipDiff.RowKey(first, 0), MembershipDiff.RowKey(second, 7));

        // …and each key names ONLY its own row.
        Assert.True(MembershipDiff.RowKeyMatches("uid-1", first, 0));
        Assert.False(MembershipDiff.RowKeyMatches("uid-1", second, 7));
    }

    [Fact]
    public void RowKey_WithUid_IsIndependentOfDisplayPosition()
    {
        // A sort or filter reorders the list; a real playlist keeps its drawer attached to the ROW across it.
        var t = T("a", "uid-a");
        Assert.Equal(MembershipDiff.RowKey(t, 0), MembershipDiff.RowKey(t, 41));
        Assert.True(MembershipDiff.RowKeyMatches(MembershipDiff.RowKey(t, 0), t, 41));
    }

    [Fact]
    public void RowKey_WithoutUid_FallsBackToUriQualifiedByPosition()
    {
        // No membership row behind it (an album tracklist, a chart) — position is the only thing left that can tell
        // two identical uris apart. It is the FALLBACK, never the primary: a re-sort closes the drawer rather than
        // opening a different song.
        var t = T("a");
        Assert.Equal("spotify:track:a#@3", MembershipDiff.RowKey(t, 3));
        Assert.True(MembershipDiff.RowKeyMatches("spotify:track:a#@3", t, 3));
        Assert.False(MembershipDiff.RowKeyMatches("spotify:track:a#@3", t, 4));
        Assert.NotEqual(MembershipDiff.RowKey(t, 3), MembershipDiff.RowKey(t, 4));
    }

    // -- the keyed MOVE keeps every row addressable (the "blank slot after a reorder" report) -----------------------
    // A same-list drag commits ONE item-keyed MOV. The list the page re-projects afterwards must therefore diff as a
    // pure MOVE: no adds, no removes, no null-keyed rows. A row that lost its identity across the swap would diff as a
    // remove+add, which is exactly what renders as an empty row-height band where the moved row used to be.
    [Fact]
    public void KeyedMove_DiffsAsMovesOnly_WithNoNullOrEmptyRowKeys()
    {
        var before = Tracks("a", "b", "c", "d");
        var after = Tracks("b", "c", "d", "a");        // row "a" dragged to the end - same uids, new order

        var d = MembershipDiff.Diff(before, after);

        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(4, d.Moves.Count);
        Assert.False(d.IsReset);
        Assert.Equal(1.0, d.RetainedFraction);
        // Every change names a real row on BOTH sides - nothing is a phantom insert or a phantom removal.
        Assert.All(d.Moves, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Key));
            Assert.NotNull(m.OldIndex);
            Assert.NotNull(m.NewIndex);
        });
        var moved = d.Moves.Single(m => m.Key == "uid-a");
        Assert.Equal((0, 3), (moved.OldIndex!.Value, moved.NewIndex!.Value));
        // ...and the per-row identity the list keys its slots by survives the move unchanged.
        Assert.Equal(MembershipDiff.RowKey(before[0], 0), MembershipDiff.RowKey(after[3], 3));
    }

    [Fact]
    public void KeyedMove_OfDuplicateUris_StillMovesTheRowThatWasDragged()
    {
        // The same song twice: only the per-row ItemId distinguishes them, and the wire's keyed MOV moves exactly one.
        var a1 = T("dup", "uid-1");
        var a2 = T("dup", "uid-2");
        var other = T("x", "uid-x");
        var d = MembershipDiff.Diff(new[] { a1, a2, other }, new[] { a2, other, a1 });

        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(3, d.Moves.Count);
        Assert.Equal((0, 2), Value(d.Moves.Single(m => m.Key == "uid-1")));
        Assert.Equal((1, 0), Value(d.Moves.Single(m => m.Key == "uid-2")));

        static (int, int) Value(RowChange c) => (c.OldIndex!.Value, c.NewIndex!.Value);
    }

    [Fact]
    public void RowKeyMatches_IsFalseForEmptyKeys_AndForAForeignKeyShape()
    {
        var withUid = T("a", "uid-a");
        var withoutUid = T("a");
        Assert.False(MembershipDiff.RowKeyMatches("", withUid, 0));
        Assert.False(MembershipDiff.RowKeyMatches(null, withUid, 0));
        // A uid-shaped key never matches a uid-less row, and a position-shaped key never matches a uid row.
        Assert.False(MembershipDiff.RowKeyMatches("uid-a", withoutUid, 0));
        Assert.False(MembershipDiff.RowKeyMatches("spotify:track:a#@0", withUid, 0));
        // A prefix of the uri with no position suffix is not a match either.
        Assert.False(MembershipDiff.RowKeyMatches("spotify:track:a", withoutUid, 0));
    }

    [Fact]
    public void RowKeyMatches_AgreesWithRowKey_AcrossUidPresenceAndPositions()
    {
        foreach (var uid in new string?[] { null, "", "uid-a" })
            for (int i = 0; i < 5; i++)
            {
                var t = T("a", uid);
                string key = MembershipDiff.RowKey(t, i);
                Assert.True(MembershipDiff.RowKeyMatches(key, t, i));
                for (int j = 0; j < 5; j++)
                    Assert.Equal(MembershipDiff.RowKey(t, j) == key, MembershipDiff.RowKeyMatches(key, t, j));
            }
    }
}
