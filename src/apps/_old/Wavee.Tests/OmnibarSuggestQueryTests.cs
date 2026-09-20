using System;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The omnibar's suggestion request lifecycle, pinned as a pure state machine. The bug it replaces: the engine box opened
// its popup synchronously on the keystroke while the fetch waited out a 150 ms debounce, and the popup's only two
// no-rows states were "loading" and "No results found" — so every first letter flashed the sentence. The rules here are
// what the popup renders BY, so they are asserted directly rather than read off a 6 fps clip.
public class OmnibarSuggestQueryTests
{
    static SearchSuggestions Some(params string[] queries) => new(queries, Array.Empty<SearchSuggestionItem>());

    // ── the keystroke edge ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Keystroke_EntersPending_BeforeAnyFetchIsIssued()
    {
        var q = new OmnibarSuggestQuery();

        int gen = q.Begin("t");

        // No Complete/Fail has happened — this is the debounce window, and it is Pending, never Empty.
        Assert.Equal(SuggestState.Pending, q.State);
        Assert.Equal("t", q.Query);
        Assert.Equal(gen, q.Generation);
        Assert.Same(SearchSuggestions.Empty, q.Suggestions);
    }

    [Fact]
    public void Keystroke_KeepsThePreviousRows_UnderThePendingState()
    {
        // Rows for "bl" stay on screen (under the progress bar) while "blu" is pending — the field does not blank on
        // every letter — and the ghost keeps completing from them.
        var q = new OmnibarSuggestQuery();
        q.Complete(q.Begin("bl"), Some("blue monday"));

        q.Begin("blu");

        Assert.Equal(SuggestState.Pending, q.State);
        Assert.Equal("blue monday", Assert.Single(q.Suggestions.Queries));
        Assert.Equal("blue monday", SearchSuggestions.GhostFor("blu", q.Suggestions.Queries));
    }

    [Fact]
    public void SameTrimmedText_KeepsTheGeneration_SoAnInFlightRequestStaysValid()
    {
        var q = new OmnibarSuggestQuery();
        int gen = q.Begin("blue");
        int changes = 0;
        q.Changed += () => changes++;

        Assert.Equal(gen, q.Begin("blue "));
        Assert.Equal(gen, q.Begin(" blue"));
        Assert.Equal(0, changes);
        Assert.True(q.Complete(gen, Some("blue monday")));
        Assert.Equal(SuggestState.Results, q.State);
    }

    [Fact]
    public void EveryTextChange_IsANewGeneration()
    {
        var q = new OmnibarSuggestQuery();
        int a = q.Begin("b");
        int b = q.Begin("bl");
        int c = q.Begin("b");   // back to an earlier text is STILL a new generation

        Assert.True(a < b && b < c);
        Assert.Equal(c, q.Generation);
    }

    // ── publishing ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Complete_WithRows_IsResults()
    {
        var q = new OmnibarSuggestQuery();
        int gen = q.Begin("blue");

        Assert.True(q.Complete(gen, Some("blue monday")));

        Assert.Equal(SuggestState.Results, q.State);
        Assert.Equal("blue monday", Assert.Single(q.Suggestions.Queries));
        Assert.Null(q.Failure);
    }

    [Fact]
    public void Complete_WithNothing_IsEmpty_TheOnlyStateThatSaysNoResults()
    {
        var q = new OmnibarSuggestQuery();
        int gen = q.Begin("zzzz");

        Assert.True(q.Complete(gen, SearchSuggestions.Empty));

        Assert.Equal(SuggestState.Empty, q.State);
    }

    [Fact]
    public void AStaleGeneration_IsDropped_EvenWhenItsTextMatches()
    {
        // The old guard was text equality: a superseded response for a retyped query won. Generation, not text.
        var q = new OmnibarSuggestQuery();
        int first = q.Begin("blue");
        q.Begin("blu");
        int again = q.Begin("blue");

        Assert.False(q.Complete(first, Some("stale answer")));
        Assert.Equal(SuggestState.Pending, q.State);
        Assert.Same(SearchSuggestions.Empty, q.Suggestions);

        Assert.True(q.Complete(again, Some("fresh answer")));
        Assert.Equal("fresh answer", Assert.Single(q.Suggestions.Queries));
    }

    [Fact]
    public void ASlowOlderAnswer_NeverOverwritesANewerOne()
    {
        var q = new OmnibarSuggestQuery();
        int older = q.Begin("b");
        int newer = q.Begin("bl");

        Assert.True(q.Complete(newer, Some("blue monday")));
        Assert.False(q.Complete(older, Some("bad guy")));

        Assert.Equal("blue monday", Assert.Single(q.Suggestions.Queries));
        Assert.Equal(SuggestState.Results, q.State);
    }

    [Fact]
    public void AStaleFailure_IsDropped()
    {
        var q = new OmnibarSuggestQuery();
        int older = q.Begin("b");
        int newer = q.Begin("bl");
        q.Complete(newer, Some("blue monday"));

        Assert.False(q.Fail(older, new InvalidOperationException("down")));

        Assert.Equal(SuggestState.Results, q.State);
        Assert.Null(q.Failure);
    }

    // ── failure ≠ empty ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fail_IsFailed_NotEmpty_AndCarriesTheReason()
    {
        var q = new OmnibarSuggestQuery();
        q.Complete(q.Begin("bl"), Some("blue monday"));
        int gen = q.Begin("blue");

        Assert.True(q.Fail(gen, new InvalidOperationException("pathfinder is down")));

        Assert.Equal(SuggestState.Failed, q.State);
        Assert.Equal("pathfinder is down", q.FailureText);
        // The previous query's rows would claim an answer the server never gave.
        Assert.Same(SearchSuggestions.Empty, q.Suggestions);
    }

    [Fact]
    public void Cancellation_IsNotAnAnswer_TheGenerationStaysPending()
    {
        // The omnibar cancels the in-flight request on unmount; the store keeps Pending so the re-mounted field
        // re-issues the same generation instead of showing a failure for a request nobody refused.
        var q = new OmnibarSuggestQuery();
        int gen = q.Begin("blue");

        Assert.False(q.Fail(gen, new OperationCanceledException()));
        Assert.False(q.Fail(gen, new TaskCanceledException()));

        Assert.Equal(SuggestState.Pending, q.State);
        Assert.Null(q.Failure);
    }

    [Fact]
    public void Retry_ReArmsTheFailedQuery_AsANewPendingGeneration()
    {
        var q = new OmnibarSuggestQuery();
        int failed = q.Begin("blue");
        q.Fail(failed, new InvalidOperationException("down"));

        int retried = q.Retry();

        Assert.NotEqual(failed, retried);
        Assert.Equal(SuggestState.Pending, q.State);
        Assert.Equal("blue", q.Query);
        Assert.Null(q.Failure);
        Assert.False(q.Complete(failed, Some("late")));   // the failed generation is gone for good
        Assert.True(q.Complete(retried, Some("blue monday")));
        Assert.Equal(SuggestState.Results, q.State);
    }

    [Theory]
    [InlineData(SuggestState.Idle)]
    [InlineData(SuggestState.Pending)]
    [InlineData(SuggestState.Results)]
    [InlineData(SuggestState.Empty)]
    public void Retry_OutsideFailed_ChangesNothing(SuggestState state)
    {
        var q = Arrange(state);
        int gen = q.Generation;
        int changes = 0;
        q.Changed += () => changes++;

        Assert.Equal(gen, q.Retry());
        Assert.Equal(state, q.State);
        Assert.Equal(0, changes);
    }

    // ── clearing ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SuggestState.Pending)]
    [InlineData(SuggestState.Results)]
    [InlineData(SuggestState.Empty)]
    [InlineData(SuggestState.Failed)]
    public void Clear_IsIdle_NotEmpty_AndDropsALateAnswer(SuggestState state)
    {
        var q = Arrange(state);
        int before = q.Generation;

        q.Clear();

        Assert.Equal(SuggestState.Idle, q.State);
        Assert.Equal("", q.Query);
        Assert.Same(SearchSuggestions.Empty, q.Suggestions);
        Assert.Null(q.Failure);
        Assert.False(q.Complete(before, Some("late")));
        Assert.Equal(SuggestState.Idle, q.State);
    }

    [Fact]
    public void BlankText_Clears_AndClearingTwiceIsSilent()
    {
        var q = new OmnibarSuggestQuery();
        q.Complete(q.Begin("blue"), Some("blue monday"));
        int changes = 0;
        q.Changed += () => changes++;

        q.Begin("   ");
        Assert.Equal(SuggestState.Idle, q.State);
        Assert.Equal(1, changes);

        q.Begin("");
        q.Clear();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Changed_FiresOnceForEveryTransition()
    {
        var q = new OmnibarSuggestQuery();
        var seen = new System.Collections.Generic.List<SuggestState>();
        q.Changed += () => seen.Add(q.State);

        int gen = q.Begin("blue");
        q.Fail(gen, new InvalidOperationException("down"));
        int retried = q.Retry();
        q.Complete(retried, SearchSuggestions.Empty);
        q.Clear();

        Assert.Equal(
            new[] { SuggestState.Pending, SuggestState.Failed, SuggestState.Pending, SuggestState.Empty, SuggestState.Idle },
            seen.ToArray());
    }

    static OmnibarSuggestQuery Arrange(SuggestState state)
    {
        var q = new OmnibarSuggestQuery();
        if (state == SuggestState.Idle) return q;
        int gen = q.Begin("blue");
        switch (state)
        {
            case SuggestState.Results: q.Complete(gen, Some("blue monday")); break;
            case SuggestState.Empty: q.Complete(gen, SearchSuggestions.Empty); break;
            case SuggestState.Failed: q.Fail(gen, new InvalidOperationException("down")); break;
        }
        Assert.Equal(state, q.State);
        return q;
    }
}
