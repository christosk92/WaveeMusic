// ── Wavee.Tests/LibraryNavReadinessTests.cs — the navigator's shimmer gate (Entities/User.NavMount.cs, plan
// docs/plans/wavee/library-stabilization-plan.md §5 step A4) ───────────────────────────────────────────────────────
//
// The rule has two independent reasons to keep the shimmer up, and either one is enough: the relation itself has not
// answered (unchanged from before the rework), or the sort is alphabetical and the rows it would group have not all
// reported their titles yet — UNLESS a row's own demand has already failed, in which case waiting forever would trade
// a small visible wrong (a row briefly parked under '#') for a permanent one (a navigator that never leaves its
// skeleton). What is pinned here is that boundary, exhaustively.

using Xunit;

namespace Wavee.Tests;

public class LibraryNavReadinessTests
{
    static readonly bool[] Bools = [false, true];

    // ── the relation-not-answered arm (unchanged rule) ──────────────────────────────────────────────────────────────

    [Fact]
    public void NothingHasAnswered_NoFilter_IsPending()
        => Assert.True(LibraryNavReadiness.IsPending(count: 0, hasFilter: false, answered: false, alphabetical: false, titlesKnown: true, anyFailed: false));

    [Fact]
    public void AnAnsweredEmptyLibrary_IsNotPending()
        // Zero rows because the relation answered "you have none" — a real, renderable empty state, not a shimmer.
        => Assert.False(LibraryNavReadiness.IsPending(count: 0, hasFilter: false, answered: true, alphabetical: false, titlesKnown: true, anyFailed: false));

    [Fact]
    public void AFilterWithText_IsNeverPendingThroughTheFirstArm_EvenUnanswered()
        // A filter that has matched nothing is its own answered state (the page's own comment: "a filter that matched
        // nothing is ANSWERED") — but the pure rule does not even need that: typed filter text alone exits this arm.
        => Assert.False(LibraryNavReadiness.IsPending(count: 0, hasFilter: true, answered: false, alphabetical: false, titlesKnown: true, anyFailed: false));

    [Fact]
    public void ANonEmptyUnansweredCount_IsNotPending()
        // Rows already on screen (a persisted relation read before the fresh answer lands) are real rows: the rule
        // shimmers only an EMPTY, unfiltered, unanswered list — hiding rows the user can already use would be a flash.
        => Assert.False(LibraryNavReadiness.IsPending(count: 12, hasFilter: false, answered: false, alphabetical: false, titlesKnown: true, anyFailed: false));

    // ── the alphabetical arm (A4, the new rule) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlphabeticalWithUnknownTitles_IsPending_EvenThoughTheEdgeAnswered()
        // The relation answered (rows exist), but sorting/grouping them by a title that has not landed is wrong data:
        // it would file under the wrong letter (or '#') and jump bands the moment the real title arrives.
        => Assert.True(LibraryNavReadiness.IsPending(count: 40, hasFilter: false, answered: true, alphabetical: true, titlesKnown: false, anyFailed: false));

    [Fact]
    public void AlphabeticalWithUnknownTitles_ButARowDemandFailed_IsNotPending()
        // A title that will never land must not hold the whole navigator in a shimmer forever — the gate yields once
        // the app knows it is not merely "still coming".
        => Assert.False(LibraryNavReadiness.IsPending(count: 40, hasFilter: false, answered: true, alphabetical: true, titlesKnown: false, anyFailed: true));

    [Fact]
    public void AlphabeticalWithEveryTitleKnown_IsNotPending()
        => Assert.False(LibraryNavReadiness.IsPending(count: 40, hasFilter: false, answered: true, alphabetical: true, titlesKnown: true, anyFailed: false));

    [Fact]
    public void NonAlphabeticalSorts_NeverGateOnTitlesKnown()
        // Recents/albums/etc. do not group by letter, so an unlanded title is the row's own refresh to make later —
        // never a reason to hold the whole list back.
        => Assert.False(LibraryNavReadiness.IsPending(count: 40, hasFilter: false, answered: true, alphabetical: false, titlesKnown: false, anyFailed: false));

    [Fact]
    public void BothArmsCanFirePending_Independently()
    {
        // Unanswered AND alphabetical-with-unknown-titles: still just Pending, not a third state.
        Assert.True(LibraryNavReadiness.IsPending(count: 0, hasFilter: false, answered: false, alphabetical: true, titlesKnown: false, anyFailed: false));
        // Answered with rows, alphabetical, titles known: neither arm fires.
        Assert.False(LibraryNavReadiness.IsPending(count: 40, hasFilter: false, answered: true, alphabetical: true, titlesKnown: true, anyFailed: false));
    }

    // ── the whole table, exhaustively ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTruthTableIsExhaustive()
    {
        // 2 (count zero/non-zero) × 2^5 = 64 combinations. `count` only matters through "is it zero", so one
        // representative non-zero value stands for the whole positive range.
        int[] counts = [0, 40];
        int seen = 0;
        foreach (int count in counts)
        foreach (bool hasFilter in Bools)
        foreach (bool answered in Bools)
        foreach (bool alphabetical in Bools)
        foreach (bool titlesKnown in Bools)
        foreach (bool anyFailed in Bools)
        {
            bool expected = (count == 0 && !hasFilter && !answered) || (alphabetical && !titlesKnown && !anyFailed);
            Assert.Equal(expected, LibraryNavReadiness.IsPending(count, hasFilter, answered, alphabetical, titlesKnown, anyFailed));
            seen++;
        }
        Assert.Equal(64, seen);
    }

    [Theory]
    [InlineData(0, false, false, false, true, false, true)]     // not-answered arm
    [InlineData(0, true, false, false, true, false, false)]     // filter text defuses the not-answered arm
    [InlineData(0, false, true, false, true, false, false)]     // answered defuses it
    [InlineData(7, false, true, true, false, false, true)]      // alphabetical arm
    [InlineData(7, false, true, true, false, true, false)]      // failed defuses the alphabetical arm
    [InlineData(7, false, true, true, true, false, false)]      // titles known defuses it
    [InlineData(7, false, true, false, false, false, false)]    // not alphabetical: the arm never engages
    public void NamedScenarios(int count, bool hasFilter, bool answered, bool alphabetical, bool titlesKnown, bool anyFailed, bool expected)
        => Assert.Equal(expected, LibraryNavReadiness.IsPending(count, hasFilter, answered, alphabetical, titlesKnown, anyFailed));
}
