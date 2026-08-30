using System.Linq;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The GitHub rate-limit policy for issue chips. Wavee ships no token, so the whole app gets 60 unauthenticated REST
// requests an hour: one careless page-open can spend the day's budget and leave every chip stuck on its snapshot state.
public class IssueStateBudgetTests
{
    const string Repo = "christosk92/WaveeMusic";
    const long Now = 1_700_000_000_000;

    static IssueStateCache Cache(params (int Number, long FetchedAtMs)[] entries)
    {
        var cache = new IssueStateCache();
        foreach (var (number, fetched) in entries)
            cache.Set(IssueStateCache.Key(Repo, number), new IssueState { State = "closed", StateReason = "completed", Title = "t", FetchedAtMs = fetched });
        return cache;
    }

    static string[] Keys(params int[] numbers) => numbers.Select(n => IssueStateCache.Key(Repo, n)).ToArray();

    [Fact]
    public void TheKeyIsRepoHashNumber()
        => Assert.Equal("christosk92/WaveeMusic#412", IssueStateCache.Key(Repo, 412));

    [Fact]
    public void AnEmptyCache_PlansEverything_InInputOrder()
    {
        var plan = new IssueStateBudget().Plan(Keys(3, 1, 2), new IssueStateCache(), Now);
        Assert.Equal(Keys(3, 1, 2), plan);
    }

    [Fact]
    public void DuplicatesCollapse()
    {
        var plan = new IssueStateBudget().Plan(Keys(7, 7, 8, 7), new IssueStateCache(), Now);
        Assert.Equal(Keys(7, 8), plan);
    }

    [Fact]
    public void FreshEntriesAreNotRefetched_StaleOnesAre()
    {
        var budget = new IssueStateBudget();
        var cache = Cache((1, Now - 1_000), (2, Now - IssueStateBudget.OneDayMs - 1));

        Assert.Equal(Keys(2, 3), budget.Plan(Keys(1, 2, 3), cache, Now));
    }

    [Fact]
    public void TheTtlBoundary_IsExclusive()
    {
        var budget = new IssueStateBudget(ttlMs: 1000);
        var cache = Cache((1, Now - 1000));                       // exactly at the TTL → stale
        Assert.Equal(Keys(1), budget.Plan(Keys(1), cache, Now));

        var fresh = Cache((1, Now - 999));
        Assert.Empty(budget.Plan(Keys(1), fresh, Now));
    }

    [Fact]
    public void AFutureTimestamp_CountsAsFresh_NotAsAStorm()
    {
        var cache = Cache((1, Now + 60_000));                     // clock skew, not a reason to refetch
        Assert.Empty(new IssueStateBudget().Plan(Keys(1), cache, Now));
    }

    [Fact]
    public void ThePlanIsCapped()
    {
        var many = Enumerable.Range(1, 100).Select(n => IssueStateCache.Key(Repo, n)).ToArray();
        Assert.Equal(20, new IssueStateBudget().Plan(many, new IssueStateCache(), Now).Length);
        Assert.Equal(3, new IssueStateBudget(maxPerOpen: 3).Plan(many, new IssueStateCache(), Now).Length);
        Assert.Empty(new IssueStateBudget(maxPerOpen: 0).Plan(many, new IssueStateCache(), Now));
    }

    [Fact]
    public void EmptyAndNullInputs_AreEmptyPlans()
    {
        var budget = new IssueStateBudget();
        Assert.Empty(budget.Plan([], new IssueStateCache(), Now));
        Assert.Empty(budget.Plan(null!, new IssueStateCache(), Now));
        Assert.Empty(budget.Plan(["", null!], new IssueStateCache(), Now));
    }

    // ── stopping ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(403, null)]                 // rate limited, or rejected for the user agent
    [InlineData(429, null)]
    [InlineData(200, "0")]                  // the quota ran out on this very response
    [InlineData(304, "0")]                  // an unauthenticated 304 still costs a request
    [InlineData(200, " 0 ")]
    public void StopConditions(int status, string? remaining)
        => Assert.True(new IssueStateBudget().ShouldStop(status, remaining));

    [Theory]
    [InlineData(200, "59")]
    [InlineData(200, null)]                 // no header: keep going, the cap is the other guard
    [InlineData(200, "")]
    [InlineData(404, "42")]                 // a deleted issue is a per-issue problem, not a stop
    [InlineData(500, "42")]
    [InlineData(200, "nonsense")]
    public void NonStopConditions(int status, string? remaining)
        => Assert.False(new IssueStateBudget().ShouldStop(status, remaining));

    // ── the cache half ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lookup_ResolvesADocumentIssueReference()
    {
        var cache = Cache((412, Now));
        var issue = new ReleaseIssue { Repo = Repo, Number = 412, State = "open" };

        var live = cache.Lookup(issue);
        Assert.NotNull(live);
        Assert.Equal("closed", live!.State);
        Assert.Equal("completed", live.StateReason);

        Assert.Null(cache.Lookup(new ReleaseIssue { Repo = Repo, Number = 999 }));
        Assert.Null(cache.Lookup(""));
    }
}
