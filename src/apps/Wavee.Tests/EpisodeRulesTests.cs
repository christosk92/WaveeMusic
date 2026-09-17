// ── Wavee.Tests/EpisodeRulesTests.cs — the show page's episode decisions (ch 09 §8, last row) ────────────────────────
//
// NEW in 0.3 — "none today, write them" (ch 09 §8). 0.2.9 kept these as private statics inside the `EpisodeList`
// component (`EpisodeList.cs:40-42, 56, 59, 65-72, 78-81`), which the no-source-text-test rule makes untestable, so the
// facts below state that component's behaviour over `Episode.Rules`: the thresholds, the four-way filter, the Newest /
// Oldest view over ORIGINAL indices, the resume pick over ALL episodes, the cursor gate, and the §9.4 empty-show fix.

using Wavee;
using Xunit;
using Rules = Wavee.Episode.Rules;
using Status = Wavee.Episode.Rules.Status;

namespace Wavee.Tests;

public class EpisodeRulesTests
{
    // ── Pct (EpisodeList.cs:40) ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 60_000, 0f)]
    [InlineData(20_000, 60_000, 1f / 3f)]
    [InlineData(90_000, 60_000, 1f)]          // clamped: a position past the end is played, not 150 %
    [InlineData(-5, 60_000, 0f)]              // clamped at zero
    [InlineData(30_000, 0, 0f)]               // an unknown duration is no progress at all
    public void Pct_IsTheClampedFraction(int progressMs, int durationMs, float expected)
        => Assert.Equal(expected, Rules.Pct(progressMs, durationMs), 5);

    // ── the thresholds (EpisodeList.cs:41-42, 197, 230) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, false, false, false)]
    [InlineData(0.01f, false, false, false)]  // exactly the floor is still unplayed
    [InlineData(0.011f, true, false, true)]
    [InlineData(0.5f, true, false, true)]
    [InlineData(0.979f, true, false, true)]
    [InlineData(0.98f, false, true, true)]    // exactly the ceiling is played
    [InlineData(1f, false, true, true)]
    public void Thresholds_InProgress_Played_AndTheRule(float pct, bool inProgress, bool played, bool rule)
    {
        Assert.Equal(inProgress, Rules.InProgress(pct));
        Assert.Equal(played, Rules.Played(pct));
        Assert.Equal(rule, Rules.HasRule(pct));
    }

    [Fact]
    public void Thresholds_AreZeroPointZeroOneAndZeroPointNineEight()
    {
        Assert.Equal(0.01f, Rules.InProgressFloor);
        Assert.Equal(0.98f, Rules.PlayedCeiling);
    }

    // ── the status filter (EpisodeList.cs:56) ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Status.All, 0f, true)]
    [InlineData(Status.All, 1f, true)]
    [InlineData(Status.Unplayed, 0f, true)]
    [InlineData(Status.Unplayed, 0.01f, true)]
    [InlineData(Status.Unplayed, 0.5f, false)]
    [InlineData(Status.InProgress, 0.5f, true)]
    [InlineData(Status.InProgress, 0.99f, false)]
    [InlineData(Status.Played, 0.98f, true)]
    [InlineData(Status.Played, 0.5f, false)]
    public void Matches_TheFourWayFilter(Status status, float pct, bool expected)
        => Assert.Equal(expected, Rules.Matches(status, pct));

    [Fact]
    public void Status_KeepsTheSelectorBarsOrder()
    {
        Assert.Equal(0, (byte)Status.All);
        Assert.Equal(1, (byte)Status.Unplayed);
        Assert.Equal(2, (byte)Status.InProgress);
        Assert.Equal(3, (byte)Status.Played);
    }

    // ── the view (EpisodeList.cs:52-59) ─────────────────────────────────────────────────────────────────────────────

    static readonly float[] Mixed = [0f, 0.33f, 1f, 0.5f, 0f];

    [Fact]
    public void View_IsOriginalIndices_NewestFirst()
    {
        Span<int> into = stackalloc int[8];
        int n = Rules.View(Mixed, Status.All, oldest: false, into);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, into[..n].ToArray());
    }

    [Fact]
    public void View_Oldest_ReversesTheFilteredList()
    {
        Span<int> into = stackalloc int[8];
        int n = Rules.View(Mixed, Status.InProgress, oldest: true, into);
        Assert.Equal(new[] { 3, 1 }, into[..n].ToArray());       // Play still addresses the show context's index
    }

    [Fact]
    public void View_AnEmptyFilterIsZero()
    {
        Span<int> into = stackalloc int[8];
        Assert.Equal(0, Rules.View([0f, 0f], Status.Played, oldest: false, into));
    }

    // ── the resume pick (EpisodeList.cs:78-81) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResumePick_IsTheMostProgressedInProgressEpisode()
        => Assert.Equal(3, Rules.ResumePick(Mixed));

    [Fact]
    public void ResumePick_IgnoresPlayedAndUnplayed_AndTiesKeepTheNewer()
    {
        Assert.Equal(-1, Rules.ResumePick([0f, 1f, 0.99f, 0.005f]));
        Assert.Equal(1, Rules.ResumePick([0f, 0.4f, 0.4f]));
    }

    [Fact]
    public void ResumePick_ReadsEveryEpisode_NotTheFilteredView()
    {
        // The banner is picked from ALL episodes (ch 09 W8): a "Played" filter does not hide what to listen to next.
        Span<int> into = stackalloc int[8];
        int shown = Rules.View(Mixed, Status.Played, oldest: false, into);
        Assert.Equal(1, shown);
        Assert.Equal(3, Rules.ResumePick(Mixed));
    }

    // ── the load-more cursor (EpisodeList.cs:65-72) ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(300, 0, 700, true)]
    [InlineData(300, 600, 700, true)]         // the page's own cursor walked further than the model's
    [InlineData(600, 700, 700, false)]
    [InlineData(700, 0, 700, false)]
    [InlineData(0, 0, 0, false)]
    public void CanLoadMore_IsTheMaxCursorAgainstTheTotal(int edgeAsked, int localAsked, int total, bool expected)
        => Assert.Equal(expected, Rules.CanLoadMore(edgeAsked, localAsked, total));

    // ── minutes (EpisodeList.cs:178, 195) ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59_999, 0)]
    [InlineData(60_000, 1)]
    [InlineData(43 * 60_000 + 59_000, 43)]    // integer arithmetic: 43:59 is "43 min", as 0.2.9 printed it
    public void Minutes_IsIntegerArithmetic(int durationMs, int expected)
        => Assert.Equal(expected, Rules.Minutes(durationMs));

    // ── the empty show (ch 09 §9.4 fix) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEmptyShow_OnlyWithNoFilterAndNoEpisodes()
    {
        // 0.2.9 said "No episodes match this filter" for a podcast with zero episodes (EpisodeList.cs:84-86); the
        // show's own empty copy belongs to the unfiltered, genuinely empty set (ch 09 §9.4, 09-show-episode-module.md:1364-1366).
        Assert.True(Rules.IsEmptyShow(total: 0, resident: 0, Status.All));
        Assert.False(Rules.IsEmptyShow(total: 0, resident: 0, Status.Played));   // a filter is applied: the filter copy
        Assert.False(Rules.IsEmptyShow(total: 12, resident: 0, Status.All));     // members exist: not an empty show
        Assert.False(Rules.IsEmptyShow(total: 12, resident: 12, Status.All));
    }

    // ── the page's paging decisions (EpisodeList.cs:65-72, 107-123; extracted from Show.Page, WP-5.M) ───────────────

    [Fact]
    public void CanLoadMore_OnlyAPartialListPages()
    {
        // A Complete membership never shows the pill, whatever the cursor says; a partial one gates on the cursor.
        Assert.False(Rules.CanLoadMore(EdgeState.Complete, edgeAsked: 0, localAsked: 0, total: 700));
        Assert.False(Rules.CanLoadMore(EdgeState.Unknown, edgeAsked: 0, localAsked: 0, total: 700));
        Assert.True(Rules.CanLoadMore(EdgeState.Partial, edgeAsked: 300, localAsked: 0, total: 700));
        Assert.False(Rules.CanLoadMore(EdgeState.Partial, edgeAsked: 300, localAsked: 700, total: 700));
    }

    [Fact]
    public void Paging_IsOut_UntilTheVersionMovesOrTheAskFails()
    {
        Assert.False(Rules.Paging(askedFrom: -1, versionAtAsk: 4, versionNow: 4, failed: false));   // nothing asked
        Assert.True(Rules.Paging(askedFrom: 300, versionAtAsk: 4, versionNow: 4, failed: false));   // still out
        Assert.False(Rules.Paging(askedFrom: 300, versionAtAsk: 4, versionNow: 5, failed: false));  // a page landed
        Assert.False(Rules.Paging(askedFrom: 300, versionAtAsk: 4, versionNow: 4, failed: true));   // it failed
    }

    [Fact]
    public void LocalCursor_AdvancesPastAnAnsweredPage_EvenAnEmptyOne_AndHoldsOnAFailure()
    {
        // The cursor, not the resident count, is the gate: a page of withdrawn members lands no rows but still moves it.
        Assert.Equal(301, Rules.LocalCursorAfter(localAsked: 0, askedFrom: 300, paging: false, failed: false));
        Assert.Equal(0, Rules.LocalCursorAfter(localAsked: 0, askedFrom: 300, paging: true, failed: false));
        Assert.Equal(0, Rules.LocalCursorAfter(localAsked: 0, askedFrom: 300, paging: false, failed: true));
        Assert.Equal(900, Rules.LocalCursorAfter(localAsked: 900, askedFrom: 300, paging: false, failed: false));
        Assert.Equal(5, Rules.LocalCursorAfter(localAsked: 5, askedFrom: -1, paging: false, failed: false));
    }

    [Theory]
    [InlineData(300, 0, 280, 300)]     // the model's cursor is ahead of the resident rows (withdrawn members)
    [InlineData(300, 601, 300, 601)]   // the page's own cursor stepped past an answered page
    [InlineData(0, 0, 50, 50)]         // no cursor yet: start after what is resident
    public void NextOffset_IsTheFurthestOfTheThree(int edgeAsked, int localAsked, int resident, int expected)
        => Assert.Equal(expected, Rules.NextOffset(edgeAsked, localAsked, resident));
}

/// <summary>`PctOf` reads the row: an unknown position renders unplayed (ch 09 §7).</summary>
[Collection(EntitiesCollection.Name)]
public class EpisodeRulesRowTests
{
    [Fact]
    public void PctOf_UnknownProgress_IsZero_KnownProgress_IsTheFraction()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var cold = ref s.Episodes.RowFor(s.Text("spotify:episode:cold"), Authority.Full, (uint)EpisodeFields.Identity);
        cold.DurationMs = 60_000;
        cold.ProgressMs = 30_000;                                   // present in the struct, but the group is not claimed
        ref var warm = ref s.Episodes.RowFor(s.Text("spotify:episode:warm"), Authority.Full,
            (uint)(EpisodeFields.Identity | EpisodeFields.Progress));
        warm.DurationMs = 60_000;
        warm.ProgressMs = 30_000;
        TestScope.CommitAndPublish(s);

        Assert.Equal(0f, Rules.PctOf(Entities.Episode(EntityUri.Parse("spotify:episode:cold"))));
        Assert.Equal(0.5f, Rules.PctOf(Entities.Episode(EntityUri.Parse("spotify:episode:warm"))), 5);
    }
}
