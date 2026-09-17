// ── Wavee.Tests/MembershipDiffTests.cs — the keyed row diff and the per-row identity ────────────────────────────────
//
// Wave 4.5's gate for `Track.MembershipDiff` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's MembershipDiffTests
// (226 lines): identity via the membership item id with a uri#occurrence fallback, structural-only reset classification,
// and shift-displaced survivors in Moves. The call shape moved: a 0.2.9 `Track` record carried its own `ContextUid`; a
// 0.3 row is a handle plus the membership's `ItemId` (a StringId on `PlaylistTrackEdge`), so the fixtures build real
// handles in a fake scope and the diff runs over `Keys(tracks, itemIds)`.
//
// Added for 0.3: a gid-form row (which has NO uri string anywhere — the key is formatted into a stack buffer) and the
// "compares in place" contract measured, because the callers are bound re-skin closures that run per row per scroll step.

using FluentGpu.Foundation;
using Xunit;
using Diff = Wavee.Track.MembershipDiff;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class MembershipDiffTests
{
    static Track T(string id) => Entities.Track(EntityUri.Parse("spotify:track:" + id));

    static StringId Uid(string? uid) => uid is null ? default : Entities.Strings.Intern(uid);

    /// <summary>The keys of a membership snapshot whose every row carries the item id <c>"uid-" + id</c>.</summary>
    static string[] Tracks(params string[] ids)
    {
        var tracks = new Track[ids.Length];
        var itemIds = new StringId[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            tracks[i] = T(ids[i]);
            itemIds[i] = Uid("uid-" + ids[i]);
        }
        return Diff.Keys(tracks, itemIds);
    }

    static string[] Range(string prefix, int count)
    {
        var ids = new string[count];
        for (int i = 0; i < count; i++) ids[i] = prefix + i;
        return ids;
    }

    [Fact]
    public void SingleMove_YieldsMoveChangesOnly_NoReset()
    {
        TestScope.Fresh();
        // a→c b→a c→b is one MOV: every survivor's index changed, zero structural changes.
        var d = Diff.Diff(Tracks("a", "b", "c"), Tracks("c", "a", "b"));
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
        TestScope.Fresh();
        var oldIds = Range("t", 100);
        var old = Tracks(oldIds);
        var next = Tracks(["new", .. oldIds]);

        var d = Diff.Diff(old, next);
        Assert.Single(d.Adds);
        Assert.Equal((null, 0), (d.Adds[0].OldIndex, d.Adds[0].NewIndex!.Value));
        Assert.Empty(d.Removes);
        Assert.Equal(100, d.Moves.Count);          // every survivor displaced by one (the FLIP pass needs these)
        Assert.False(d.IsReset);                   // ONE structural change — never a reset despite 100 shifted rows
    }

    [Fact]
    public void RemoveAndAdd_Combined()
    {
        TestScope.Fresh();
        var d = Diff.Diff(Tracks("a", "b", "c"), Tracks("a", "x", "c"));
        Assert.Equal("uid-x", Assert.Single(d.Adds).Key);
        Assert.Equal("uid-b", Assert.Single(d.Removes).Key);
        Assert.Equal(1, Assert.Single(d.Removes).OldIndex);
        Assert.Empty(d.Moves);                     // a and c kept their indices
        Assert.False(d.IsReset);
    }

    [Fact]
    public void CuratedRecut_MostContentReplaced_IsReset()
    {
        TestScope.Fresh();
        // Discover-Weekly style: 30 rows all replaced → retained 0 → reset (whole-list crossfade, no row storm).
        var d = Diff.Diff(Tracks(Range("old", 30)), Tracks(Range("new", 30)));
        Assert.True(d.IsReset);
        Assert.Equal(0.0, d.RetainedFraction);
    }

    [Fact]
    public void BulkEdit_ManyStructuralChanges_IsReset()
    {
        TestScope.Fresh();
        // retained fraction high (100 survive of 141) but 41 adds > the structural cap → still a reset.
        var old = Tracks(Range("t", 141));
        string[] next = [.. old.Take(100), .. Tracks(Range("n", 41))];
        var d = Diff.Diff(old, next);
        Assert.Equal(41 + 41, d.Adds.Count + d.Removes.Count);
        Assert.True(d.IsReset);
    }

    [Fact]
    public void FirstFill_EmptyOld_NeverAReset()
    {
        TestScope.Fresh();
        var d = Diff.Diff([], Tracks("a", "b"));
        Assert.Equal(2, d.Adds.Count);
        Assert.False(d.IsReset);
        Assert.Equal(1.0, d.RetainedFraction);
    }

    [Fact]
    public void NoChange_IsEmpty()
    {
        TestScope.Fresh();
        var d = Diff.Diff(Tracks("a", "b"), Tracks("a", "b"));
        Assert.True(d.IsEmpty);
        Assert.False(d.IsReset);
    }

    [Fact]
    public void DuplicateUris_WithoutItemIds_StayDistinctViaOccurrence()
    {
        TestScope.Fresh();
        // no item id → uri#occurrence keys; removing ONE of two duplicate rows is one remove, the other survives.
        var dup = T("dup");
        var b = T("b");
        var d = Diff.Diff(Diff.Keys([dup, dup, b], []), Diff.Keys([dup, b], []));
        Assert.Equal("spotify:track:dup#1", Assert.Single(d.Removes).Key);
        Assert.Empty(d.Adds);
        Assert.Single(d.Moves);                    // b shifted 2→1
    }

    // ── per-ROW identity (RowKey / RowKeyMatches) ────────────────────────────────────────────────────────────────
    // The same rule the diff keys on, for one row at a time. Per-row UI STATE (the versions drawer) used to be keyed by
    // TRACK URI: a playlist holding the same song twice expanded EVERY row carrying that uri at once.

    [Fact]
    public void RowKey_PrefersContextUid_SoDuplicateUrisAreDistinctRows()
    {
        TestScope.Fresh();
        var dup = T("dup");                        // same track uri, two membership rows
        var uid1 = Uid("uid-1");
        var uid2 = Uid("uid-2");
        Assert.Equal("uid-1", Diff.RowKey(dup, uid1, 0));
        Assert.Equal("uid-2", Diff.RowKey(dup, uid2, 7));
        Assert.NotEqual(Diff.RowKey(dup, uid1, 0), Diff.RowKey(dup, uid2, 7));

        // …and each key names ONLY its own row.
        Assert.True(Diff.RowKeyMatches("uid-1", dup, uid1, 0));
        Assert.False(Diff.RowKeyMatches("uid-1", dup, uid2, 7));
    }

    [Fact]
    public void RowKey_WithUid_IsIndependentOfDisplayPosition()
    {
        TestScope.Fresh();
        // A sort or filter reorders the list; a real playlist keeps its drawer attached to the ROW across it.
        var t = T("a");
        var uid = Uid("uid-a");
        Assert.Equal(Diff.RowKey(t, uid, 0), Diff.RowKey(t, uid, 41));
        Assert.True(Diff.RowKeyMatches(Diff.RowKey(t, uid, 0), t, uid, 41));
    }

    [Fact]
    public void RowKey_WithoutUid_FallsBackToUriQualifiedByPosition()
    {
        TestScope.Fresh();
        // No membership row behind it (an album tracklist, a chart) — position is the only thing left that can tell two
        // identical uris apart. The FALLBACK, never the primary: a re-sort closes the drawer rather than opening a
        // different song.
        var t = T("a");
        Assert.Equal("spotify:track:a#@3", Diff.RowKey(t, default, 3));
        Assert.True(Diff.RowKeyMatches("spotify:track:a#@3", t, default, 3));
        Assert.False(Diff.RowKeyMatches("spotify:track:a#@3", t, default, 4));
        Assert.NotEqual(Diff.RowKey(t, default, 3), Diff.RowKey(t, default, 4));
    }

    // ── the keyed MOVE keeps every row addressable (the "blank slot after a reorder" report) ───────────────────────
    // A same-list drag commits ONE item-keyed MOV, so the re-projected list must diff as a pure MOVE: a row that lost its
    // identity across the swap would diff as remove+add, which renders as an empty row-height band.

    [Fact]
    public void KeyedMove_DiffsAsMovesOnly_WithNoNullOrEmptyRowKeys()
    {
        TestScope.Fresh();
        var before = Tracks("a", "b", "c", "d");
        var after = Tracks("b", "c", "d", "a");        // row "a" dragged to the end — same uids, new order

        var d = Diff.Diff(before, after);

        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(4, d.Moves.Count);
        Assert.False(d.IsReset);
        Assert.Equal(1.0, d.RetainedFraction);
        // Every change names a real row on BOTH sides — nothing is a phantom insert or a phantom removal.
        Assert.All(d.Moves, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Key));
            Assert.NotNull(m.OldIndex);
            Assert.NotNull(m.NewIndex);
        });
        var moved = d.Moves.Single(m => m.Key == "uid-a");
        Assert.Equal((0, 3), (moved.OldIndex!.Value, moved.NewIndex!.Value));
        // …and the per-row identity the list keys its slots by survives the move unchanged.
        Assert.Equal(Diff.RowKey(T("a"), Uid("uid-a"), 0), Diff.RowKey(T("a"), Uid("uid-a"), 3));
    }

    [Fact]
    public void KeyedMove_OfDuplicateUris_StillMovesTheRowThatWasDragged()
    {
        TestScope.Fresh();
        // The same song twice: only the per-row item id distinguishes them, and the wire's keyed MOV moves exactly one.
        var dup = T("dup");
        var other = T("x");
        StringId u1 = Uid("uid-1"), u2 = Uid("uid-2"), ux = Uid("uid-x");
        var d = Diff.Diff(Diff.Keys([dup, dup, other], [u1, u2, ux]), Diff.Keys([dup, other, dup], [u2, ux, u1]));

        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(3, d.Moves.Count);
        Assert.Equal((0, 2), Value(d.Moves.Single(m => m.Key == "uid-1")));
        Assert.Equal((1, 0), Value(d.Moves.Single(m => m.Key == "uid-2")));

        static (int, int) Value(Track.RowChange c) => (c.OldIndex!.Value, c.NewIndex!.Value);
    }

    [Fact]
    public void RowKeyMatches_IsFalseForEmptyKeys_AndForAForeignKeyShape()
    {
        TestScope.Fresh();
        var t = T("a");
        var uid = Uid("uid-a");
        Assert.False(Diff.RowKeyMatches("", t, uid, 0));
        Assert.False(Diff.RowKeyMatches(null, t, uid, 0));
        // A uid-shaped key never matches a uid-less row, and a position-shaped key never matches a uid row.
        Assert.False(Diff.RowKeyMatches("uid-a", t, default, 0));
        Assert.False(Diff.RowKeyMatches("spotify:track:a#@0", t, uid, 0));
        // A prefix of the uri with no position suffix is not a match either.
        Assert.False(Diff.RowKeyMatches("spotify:track:a", t, default, 0));
    }

    [Fact]
    public void RowKeyMatches_AgreesWithRowKey_AcrossUidPresenceAndPositions()
    {
        TestScope.Fresh();
        foreach (var raw in new string?[] { null, "", "uid-a" })
            for (int i = 0; i < 5; i++)
            {
                var t = T("a");
                var uid = Uid(raw);
                string key = Diff.RowKey(t, uid, i);
                Assert.True(Diff.RowKeyMatches(key, t, uid, i));
                for (int j = 0; j < 5; j++)
                    Assert.Equal(Diff.RowKey(t, uid, j) == key, Diff.RowKeyMatches(key, t, uid, j));
            }
    }

    // ── 0.3: the gid row, and "in place" measured ──────────────────────────────────────────────────────────────────

    /// <summary>A catalogue row keeps NO uri string (its identity is the packed gid), so the fallback key and the in-place
    /// match both go through the formatted uri — and must agree with each other and with the occurrence keys.</summary>
    [Fact]
    public void A_gid_row_keys_and_matches_through_its_formatted_uri()
    {
        TestScope.Fresh();
        const string uri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
        var t = T("4uLU6hMCjMI75M1A2tKUQC");
        Assert.Equal(EntityForm.Gid, t.Id.Form);

        Assert.Equal(uri + "#@12", Diff.RowKey(t, default, 12));
        Assert.True(Diff.RowKeyMatches(uri + "#@12", t, default, 12));
        Assert.False(Diff.RowKeyMatches(uri + "#@13", t, default, 12));
        Assert.False(Diff.RowKeyMatches("spotify:track:4uLU6hMCjMI75M1A2tKUQD#@12", t, default, 12));
        Assert.Equal(new[] { uri + "#0", uri + "#1" }, Diff.Keys([t, t], []));
    }

    /// <summary>The re-skin closures call this per row per scroll step, so it compares IN PLACE — for a text-form row and
    /// for a gid row alike (warmed first, so the measurement is the steady state, not the first call's JIT).</summary>
    [Fact]
    public void RowKeyMatches_allocates_nothing_on_the_steady_state_path()
    {
        TestScope.Fresh();
        var text = T("a");
        var gid = T("4uLU6hMCjMI75M1A2tKUQC");
        var uid = Uid("uid-a");
        string textKey = Diff.RowKey(text, default, 3);
        string gidKey = Diff.RowKey(gid, default, 3);

        bool warm = false;
        for (int i = 0; i < 4; i++)
            warm |= Diff.RowKeyMatches(textKey, text, default, 3) & Diff.RowKeyMatches(gidKey, gid, default, 3)
                    & Diff.RowKeyMatches("uid-a", text, uid, 9);
        Assert.True(warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int hits = 0;
        for (int i = 0; i < 100; i++)
        {
            if (Diff.RowKeyMatches(textKey, text, default, 3)) hits++;
            if (Diff.RowKeyMatches(gidKey, gid, default, i)) hits++;
            if (Diff.RowKeyMatches("uid-a", text, uid, i)) hits++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(100 + 1 + 100, hits);
    }
}
