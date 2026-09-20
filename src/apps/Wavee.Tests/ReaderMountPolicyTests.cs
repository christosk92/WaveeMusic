// ── Wavee.Tests/ReaderMountPolicyTests.cs — the artist reader's mount identity + catalogue gate
// (Entities/Artist.Reader.Policy.cs, plan docs/plans/wavee/library-stabilization-plan.md §5 wave 2, step R0) ────────
//
// `MountKey` is what RC6/RC7's fix reduces the reader's remount key to: the four things a USER changes (artist,
// scope, sort, the narrow/wide block layout) — never `_generation`, never the block order, because those move on
// every data landing and a keyed-child diff inside an unchanged `ItemsView` already reshapes in place (R2).
//
// `CatalogueReady` is R3's gate over the three catalogue facets (albums/singles/compilations): the catalogue group
// must not appear until every facet has SETTLED — answered (state moved off `Unknown`) or given up (its own ask
// failed) — so it lands once, as an append, instead of interleaving as each facet's page happens to arrive first.

using Xunit;

namespace Wavee.Tests;

public class ReaderMountPolicyTests
{
    // ── MountKey ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MountKey_HasNoGenerationOrOrderInput_ByConstruction()
        // The strongest form of "ignores data landing": the four-argument signature has nowhere to put a generation
        // counter or a block-order fingerprint. Two calls with identical (artist, scope, sort, narrow) always agree,
        // however many reshapes happened between them.
        => Assert.Equal(ReaderMountPolicy.MountKey(7, 1, 0, false), ReaderMountPolicy.MountKey(7, 1, 0, false));

    [Fact]
    public void MountKey_MovesWithArtist()
        => Assert.NotEqual(ReaderMountPolicy.MountKey(7, 1, 0, false), ReaderMountPolicy.MountKey(8, 1, 0, false));

    [Fact]
    public void MountKey_MovesWithScope()
        => Assert.NotEqual(ReaderMountPolicy.MountKey(7, 1, 0, false), ReaderMountPolicy.MountKey(7, 2, 0, false));

    [Fact]
    public void MountKey_MovesWithSort()
        => Assert.NotEqual(ReaderMountPolicy.MountKey(7, 1, 0, false), ReaderMountPolicy.MountKey(7, 1, 1, false));

    [Fact]
    public void MountKey_MovesWithNarrow()
        => Assert.NotEqual(ReaderMountPolicy.MountKey(7, 1, 0, false), ReaderMountPolicy.MountKey(7, 1, 0, true));

    [Fact]
    public void MountKey_TreatsEachFieldAsItsOwnSeparatorSegment_NotJustConcatenation()
    {
        // A naive concatenation could collide two different (artist, scope) pairs through the separator itself
        // (e.g. artist=1, scope=23 vs artist=12, scope=3). The ":" separator plus each field being a plain integer
        // print keeps them apart because no field's text can itself contain ':'.
        Assert.NotEqual(ReaderMountPolicy.MountKey(1, 23, 0, false), ReaderMountPolicy.MountKey(12, 3, 0, false));
    }

    [Fact]
    public void MountKey_DistinguishesNarrowFromWide_ByLetterNotByBool_ToStayHumanReadableInLogs()
    {
        Assert.EndsWith(":n", ReaderMountPolicy.MountKey(7, 1, 0, true));
        Assert.EndsWith(":w", ReaderMountPolicy.MountKey(7, 1, 0, false));
    }

    // ── CatalogueReady ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NotReady_WhileAnyFacetIsStillUnknownAndHasNotFailed()
    {
        Assert.False(ReaderMountPolicy.CatalogueReady(EdgeState.Unknown, EdgeState.Complete, EdgeState.Complete, false, false, false));
        Assert.False(ReaderMountPolicy.CatalogueReady(EdgeState.Complete, EdgeState.Unknown, EdgeState.Complete, false, false, false));
        Assert.False(ReaderMountPolicy.CatalogueReady(EdgeState.Complete, EdgeState.Complete, EdgeState.Unknown, false, false, false));
    }

    [Fact]
    public void Ready_WhenAllThreeFacetsHaveAnswered()
    {
        Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Partial, EdgeState.Complete, EdgeState.Complete, false, false, false));
        Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Complete, EdgeState.Complete, EdgeState.Complete, false, false, false));
    }

    [Fact]
    public void Ready_WhenAFacetFailedInsteadOfAnswering()
        // "Everyone has said something", not "everyone succeeded" — a permanently-failing facet must not hold the
        // other two, already-answered facets off the screen forever.
        => Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Unknown, EdgeState.Complete, EdgeState.Complete, albumsFailed: true, false, false));

    [Fact]
    public void Ready_WhenAllThreeFacetsFailed()
        => Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Unknown, EdgeState.Unknown, EdgeState.Unknown, true, true, true));

    [Fact]
    public void TheFailedBool_IsOnlyConsultedWhileItsOwnFacetIsUnknown()
    {
        // A facet that already answered (state != Unknown) does not need its failed bit at all — a stale `true` left
        // over from an earlier failed attempt that later succeeded must not matter.
        Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Complete, EdgeState.Complete, EdgeState.Complete, albumsFailed: true, singlesFailed: true, compilationsFailed: true));
    }

    [Fact]
    public void EdgeStateFailed_CountsAsSettled_EvenWithoutTheBoolTrue()
        // `EdgeState.Failed` is a real enum value (Edges.cs's own comment: never stored, but the app assembly still
        // defines it) — the gate treats "not Unknown" as settled regardless of which non-Unknown value it is, so a
        // caller that hands the enum's Failed value through needs no matching bool.
        => Assert.True(ReaderMountPolicy.CatalogueReady(EdgeState.Failed, EdgeState.Complete, EdgeState.Complete, false, false, false));

    [Fact]
    public void TheThreeFacetsAreIndependent_NoOrderingMatters()
    {
        Assert.Equal(
            ReaderMountPolicy.CatalogueReady(EdgeState.Unknown, EdgeState.Complete, EdgeState.Unknown, true, false, true),
            ReaderMountPolicy.CatalogueReady(EdgeState.Unknown, EdgeState.Unknown, EdgeState.Complete, true, true, false));
    }

    [Fact]
    public void TheWholeGate_IsExhaustive_OverSettledPerFacet()
    {
        // Reduce each facet to "settled" = (state != Unknown || failed): the gate's actual formula depends on nothing
        // else, so every state/failed combination that maps to the same settled bit must agree with every other one
        // that does, and the three-facet AND must match exactly.
        EdgeState[] states = [EdgeState.Unknown, EdgeState.Partial, EdgeState.Complete, EdgeState.Failed];
        int seen = 0;
        foreach (var a in states) foreach (bool af in Bools)
        foreach (var s in states) foreach (bool sf in Bools)
        foreach (var c in states) foreach (bool cf in Bools)
        {
            bool settledA = a != EdgeState.Unknown || af;
            bool settledS = s != EdgeState.Unknown || sf;
            bool settledC = c != EdgeState.Unknown || cf;
            bool expected = settledA && settledS && settledC;
            Assert.Equal(expected, ReaderMountPolicy.CatalogueReady(a, s, c, af, sf, cf));
            seen++;
        }
        Assert.Equal(4 * 2 * 4 * 2 * 4 * 2, seen);
    }

    static readonly bool[] Bools = [false, true];
}
