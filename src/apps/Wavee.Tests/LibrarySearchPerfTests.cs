using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class LibrarySearchPerfTests
{
    [Fact]
    public void CancelledMatching_DoesNotWalkTheCorpus()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LibrarySearchSelection.Select(CatalogSearchCorpus.Empty,
            LibrarySearchFixture.Scope, LibrarySearchScope.Artists, ["spotify:artist:a"], "a", _ => throw new Exception("walked"), cancellation.Token));
    }

    [Fact]
    public void BroadMatching_BoundsSelectedPayloadReadsBeforeConstructingDtos()
    {
        var scope = LibrarySearchFixture.Scope;
        var rows = Enumerable.Range(0, 5000).Select(i => new CatalogSearchRow(scope, "spotify:album:" + i,
            EntityKind.Album, "match " + i, null, [])).ToArray();
        var selected = LibrarySearchSelection.Select(new(rows, []), scope, LibrarySearchScope.Albums,
            rows.Select(row => row.Uri).ToArray(), "match", _ => "spotify", TestContext.Current.CancellationToken);
        Assert.Equal(LibrarySearchSelection.ResultEntityLimit, selected.Albums.Count);
    }

    [Fact]
    public async Task ColdPromotion_RejoinsSelectedFactsWithoutReloadingTheSearchCorpus()
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        var result = await fixture.Search("billie");
        Assert.Equal("Billie Jean", Assert.Single(Assert.Single(Assert.Single(result.Artists).Albums).Tracks).Title);
        Assert.Equal(1, fixture.Storage.CorpusReads);
        Assert.DoesNotContain(fixture.Storage.ReadKeys, key => key.Subject is "spotify:artist:q" or "spotify:album:bad" or "spotify:track:bi");
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task DisposedQuery_CancelsItsLocalPreparation()
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Storage.ReadCorpus = async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return CatalogSearchCorpus.Empty;
        };
        var handle = fixture.Data.Queries.Acquire(new LibrarySearchQuery(LibrarySearchFixture.Scope, "billie", LibrarySearchScope.Artists));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        handle.Dispose();
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SupersededPreparation_CannotPublishAnOldSelectionEvenIfItIgnoresCancellation()
    {
        await using var fixture = await LibrarySearchFixture.CreateAsync();
        var original = await fixture.Storage.ReadSearchCorpusAsync(LibrarySearchFixture.Scope, TestContext.Current.CancellationToken);
        fixture.Storage.CorpusReads = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CatalogSearchCorpus>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Storage.ReadCorpus = async (call, _) =>
        {
            if (call == 1) { firstStarted.TrySetResult(); return await release.Task; }
            secondStarted.TrySetResult(); return CatalogSearchCorpus.Empty;
        };
        using var handle = fixture.Data.Queries.Acquire(new LibrarySearchQuery(LibrarySearchFixture.Scope, "billie", LibrarySearchScope.Artists));
        var observed = new List<LibrarySearchResults>();
        using var subscription = handle.Changes.Subscribe(new Capture(snapshot => { lock (observed) observed.Add(snapshot.Value); }));
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.Data.Catalog.InvalidateAsync([LibrarySearchFixture.Key("spotify:artist:mj", FacetKind.ArtistIdentity)], TestContext.Current.CancellationToken);
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await handle.RefreshAsync(TestContext.Current.CancellationToken);
        release.TrySetResult(original);
        await fixture.Search("billie");
        Assert.True(handle.Current.Value.IsEmpty);
        lock (observed) Assert.All(observed, result => Assert.True(result.IsEmpty));
    }

    sealed class Capture(Action<QuerySnapshot<LibrarySearchResults>> action) : IObserver<QuerySnapshot<LibrarySearchResults>>
    {
        public void OnNext(QuerySnapshot<LibrarySearchResults> value) => action(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
}
