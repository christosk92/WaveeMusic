using FluentGpu.Signals;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>Findings 4.2: the pure orchestration <see cref="Services"/>'s constructor delegates to on every
/// <c>CatalogChangeKind.Session</c> publication — set the UI signal, rebind the scope-scoped UI projections, log
/// the flip, and stay idempotent for a re-publish of the same scope (which covers both a duplicate observer fire
/// and a caller that never checked before calling).
///
/// <para>The cold-start work adds the other half: a launch that recalled its last session's scope is ALREADY serving
/// that scope when the AP welcome lands, so the welcome must rebind nothing and say so once — the evidence that the
/// session confirmed rather than replaced.</para></summary>
public sealed class CatalogScopePublisherTests
{
    static readonly CatalogScope ScopeA = new("spotify", "account-a", "en", "US", "premium", 1, false);
    static readonly CatalogScope ScopeB = new("spotify", "account-b", "de", "DE", "premium", 1, false);

    [Fact]
    public void FirstPublishSetsTheSignalRebindsAndLogsTheFlip()
    {
        var current = ScopeA;
        var signal = new Signal<CatalogScope>(ScopeA with { ProviderAccount = "" });   // the pre-login seed, not yet ScopeA
        var rebindCalls = new List<(CatalogScope Previous, CatalogScope Next)>();
        var logs = new List<(string Event, string Message)>();
        var publisher = new CatalogScopePublisher(() => current, () => 7, signal,
            (previous, next) => rebindCalls.Add((previous, next)), (id, message) => logs.Add((id, message)));

        publisher.Publish();

        Assert.Equal(ScopeA, signal.Value);
        var rebind = Assert.Single(rebindCalls);
        Assert.Equal("", rebind.Previous.ProviderAccount);
        Assert.Equal(ScopeA, rebind.Next);
        var entry = Assert.Single(logs);
        Assert.Equal(CatalogScopePublisher.PublishedEvent, entry.Event);
        Assert.Contains("account=account-a", entry.Message);
        Assert.Contains("locale=en", entry.Message);
        Assert.Contains("epoch=7", entry.Message);
    }

    [Fact]
    public void AnUnchangedScopeUnderANewEpochConfirmsWithoutRebinding()
    {
        // The provisional scope this launch is already serving, now certified by the session install.
        var current = ScopeA;
        long epoch = 1;
        var signal = new Signal<CatalogScope>(ScopeA);
        var rebindCalls = new List<(CatalogScope Previous, CatalogScope Next)>();
        var logs = new List<(string Event, string Message)>();
        var publisher = new CatalogScopePublisher(() => current, () => epoch, signal,
            (previous, next) => rebindCalls.Add((previous, next)), (id, message) => logs.Add((id, message)));

        epoch = 2;
        publisher.Publish();

        Assert.Empty(rebindCalls);                       // nothing is torn down and re-acquired
        Assert.Equal(ScopeA, signal.Value);
        var entry = Assert.Single(logs);
        Assert.Equal(CatalogScopePublisher.ConfirmedEvent, entry.Event);
        Assert.Contains("epoch=2", entry.Message);
        Assert.Contains("rebound=false", entry.Message);
    }

    [Fact]
    public void ASecondPublicationOfTheSameEpochSaysNothingTwice()
    {
        var current = ScopeA;
        var signal = new Signal<CatalogScope>(ScopeA);
        var rebindCalls = new List<(CatalogScope Previous, CatalogScope Next)>();
        var logs = new List<(string Event, string Message)>();
        var publisher = new CatalogScopePublisher(() => current, () => 5, signal,
            (previous, next) => rebindCalls.Add((previous, next)), (id, message) => logs.Add((id, message)));

        publisher.Publish();
        publisher.Publish();

        Assert.Empty(rebindCalls);
        Assert.Single(logs);
    }

    [Fact]
    public void ADifferentScopeRebindsAgainAndNamesThePreviousAccount()
    {
        var current = ScopeA;
        var signal = new Signal<CatalogScope>(ScopeA);
        var rebindCalls = new List<(CatalogScope Previous, CatalogScope Next)>();
        var logs = new List<(string Event, string Message)>();
        var publisher = new CatalogScopePublisher(() => current, () => 2, signal,
            (previous, next) => rebindCalls.Add((previous, next)), (id, message) => logs.Add((id, message)));

        current = ScopeB;
        publisher.Publish();

        Assert.Equal(ScopeB, signal.Value);
        Assert.Equal([(ScopeA, ScopeB)], rebindCalls);
        var entry = Assert.Single(logs);
        Assert.Equal(CatalogScopePublisher.PublishedEvent, entry.Event);
        Assert.Contains("previousAccount=account-a", entry.Message);
        Assert.Contains("reacquire=True", entry.Message);      // a different account: the handles must be re-acquired
    }
}
