// ── Wavee.Tests/PageInvalidationTests.cs — RowStamp, the render-cost fix's shared early-out shape ──────────────────
//
// FIX 2 (Album.Page.cs's page render, its trailing band's pending gate and demand, Artist.Page.cs's row demand)
// replaces four hand-rolled "did any of these numbers change" comparisons with one `readonly record struct`:
// `RowStamp(int Slot, uint Generation, uint Version)` + `Moved(in RowStamp)`. NOTHING here touches production
// source text (house rule) — this is a pure fact about the struct's own equality/inequality contract, exercised
// directly, exactly the way `HasPanel`, `PendingNow`, `Demand` and `DemandRows` use it: take a stamp, compare it to
// a previously cached one, and trust `Moved` to say whether the render/demand body may skip.
//
// NOTHING HERE STARTS THE ENGINE LOOP, OPENS A WINDOW OR TOUCHES A SCOPE: `RowStamp` is an engine-free value type,
// so this suite needs no `EntitiesCollection` and can run beside every other test.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PageInvalidationTests
{
    // ── the three inputs, one at a time ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Moved_IsFalse_WhenNothingChanged()
    {
        var a = new RowStamp(Slot: 7, Generation: 3, Version: 11);
        var b = new RowStamp(Slot: 7, Generation: 3, Version: 11);

        Assert.False(a.Moved(b));
        Assert.False(b.Moved(a));
    }

    [Fact]
    public void Moved_IsTrue_WhenOnlyTheRowVersionChanged()
    {
        // The row itself (or the edge parent it stands for) was written — the table generation may not even have
        // been re-read yet by the caller, but the version alone is enough to report a move.
        var previous = new RowStamp(Slot: 7, Generation: 3, Version: 11);
        var next = new RowStamp(Slot: 7, Generation: 3, Version: 12);

        Assert.True(next.Moved(previous));
        Assert.True(previous.Moved(next));
    }

    [Fact]
    public void Moved_IsTrue_WhenOnlyTheTableGenerationChanged()
    {
        // A drain touched the table (bumping its whole-table Changed generation) even though THIS row's own version
        // did not move — HasPanel's `tracksPublished` case (a member track's own field write bumps the Tracks
        // table's generation without bumping the AlbumTracks edge's row version for that album).
        var previous = new RowStamp(Slot: 7, Generation: 3, Version: 11);
        var next = new RowStamp(Slot: 7, Generation: 4, Version: 11);

        Assert.True(next.Moved(previous));
        Assert.True(previous.Moved(next));
    }

    [Fact]
    public void Moved_IsFalse_WhenNeitherGenerationNorVersionChanged_EvenAcrossManyComparisons()
    {
        var stamp = new RowStamp(Slot: 42, Generation: 100, Version: 9);
        var cached = stamp;

        // Repeated checks against an unmoved cache must keep answering false — this is exactly the "cached verdict
        // stands" branch every render-cost site takes on the common case (nothing relevant changed).
        for (int i = 0; i < 5; i++)
            Assert.False(stamp.Moved(cached));
    }

    // ── no aliasing across slots ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Moved_IsTrue_ForADifferentSlot_EvenWithIdenticalGenerationAndVersion()
    {
        // Two entirely different rows (or edge parents) that happen to carry the same (generation, version) pair —
        // plausible after a recycled slot, or simply two different artists fetched at the same drain — must never
        // compare as "unmoved". This is what protects a component that can survive keep-alive across a different
        // artist/album (Artist.Page.cs's PageHost is keyed by ROUTE, not by artist) from reusing a stale cache.
        var forSlotA = new RowStamp(Slot: 5, Generation: 2, Version: 1);
        var forSlotB = new RowStamp(Slot: 6, Generation: 2, Version: 1);

        Assert.True(forSlotB.Moved(forSlotA));
        Assert.True(forSlotA.Moved(forSlotB));
    }

    [Fact]
    public void Moved_IsTrue_ForTheDefaultStamp_AgainstAnyRealSlot()
    {
        // default(RowStamp) — Slot 0 (Table.None), Generation 0, Version 0 — is what every cache field starts as
        // (and what the four sites reset to on a scope/subject switch). A real row's slot is always > 0, so the
        // very first comparison after a fresh mount or a reset must always report moved, forcing the first-ever
        // compute rather than accidentally short-circuiting on an all-zero coincidence.
        var freshCache = default(RowStamp);
        var firstRealStamp = new RowStamp(Slot: 1, Generation: 0, Version: 0);

        Assert.True(firstRealStamp.Moved(freshCache));
    }

    [Fact]
    public void Moved_IsFalse_ForTwoDefaultStamps()
    {
        // Two never-yet-computed caches (e.g. two sibling gates on a page that has not rendered its first real slot)
        // are indistinguishable from each other, which is fine — neither has anything cached to alias with a live row.
        Assert.False(default(RowStamp).Moved(default));
    }

    // ── the combined "any of the three differs" contract, as the call sites use it ──────────────────────────────────────

    [Theory]
    [InlineData(7, 3, 11, 7, 3, 11, false)]   // identical → unmoved
    [InlineData(7, 3, 11, 7, 3, 12, true)]    // version differs
    [InlineData(7, 3, 11, 7, 4, 11, true)]    // generation differs
    [InlineData(7, 3, 11, 8, 3, 11, true)]    // slot differs
    [InlineData(7, 3, 11, 8, 4, 12, true)]    // everything differs
    public void Moved_ReflectsAnyFieldDifference(int slotA, uint genA, uint verA, int slotB, uint genB, uint verB, bool expectedMoved)
    {
        var a = new RowStamp(slotA, genA, verA);
        var b = new RowStamp(slotB, genB, verB);

        Assert.Equal(expectedMoved, a.Moved(b));
        // Moved is symmetric: it only asks "does anything differ", not "did THIS one move forward".
        Assert.Equal(expectedMoved, b.Moved(a));
    }

    [Fact]
    public void Two_sources_at_the_same_slot_generation_and_version_still_read_as_moved()
    {
        // THE ALIASING CASE the Source tag exists for. The fans gate builds its stamp from one of two DIFFERENT
        // tables depending on the render (the lead artist's related edge vs a seed track's; or an artist's own
        // related edge vs the account row's followed-artists edge). A slot is a PER-TABLE index, so artist slot 5
        // and track slot 5 both exist, and two different edge tables can easily sit at the same version. Without
        // the tag those two stamps compare equal, the gate reads "not moved", and the fetch is silently skipped —
        // leaving the fans row empty with nothing in the logs.
        var fromTrack = new RowStamp(5, 12u, 3u, Source: 1);
        var fromArtist = new RowStamp(5, 12u, 3u, Source: 2);
        Assert.True(fromArtist.Moved(fromTrack));
        Assert.True(fromTrack.Moved(fromArtist));
    }

    [Fact]
    public void An_untagged_stamp_is_unchanged_by_the_source_field()
    {
        // Every fixed-table site leaves Source at its default, so adding the field must not perturb them.
        Assert.False(new RowStamp(7, 4u, 9u).Moved(new RowStamp(7, 4u, 9u)));
        Assert.True(new RowStamp(7, 4u, 9u).Moved(new RowStamp(7, 4u, 10u)));
    }
}
