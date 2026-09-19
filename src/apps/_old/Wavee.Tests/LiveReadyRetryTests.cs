using Wavee.Backend.Hydration;
using Xunit;

namespace Wavee.Tests;

public class LiveReadyRetryTests
{
    const string Album = "album:spotify:album:71TimGdYvnolc8o298RVxs";

    [Fact]
    public void CachedHeaderIsRetainedUntilOneLiveRetryPublishesTracks()
    {
        var retry = new LiveReadyRetry();
        var shown = (Title: "PURE", Tracks: 0);
        Assert.Equal(0, retry.TryBegin(Album, false, true, false, false));
        long ticket = retry.TryBegin(Album, true, true, false, false);
        Assert.NotEqual(0, ticket);
        Assert.Equal(("PURE", 0), shown); // starting recovery does not clear the cached header
        if (retry.Complete(ticket)) shown = ("PURE", 13);
        Assert.Equal(("PURE", 13), shown);
        Assert.Equal(0, retry.TryBegin(Album, true, true, false, true));
    }

    [Fact]
    public void InitialOfflineReadMustSettleBeforeRecoveryCanPublish()
    {
        var retry = new LiveReadyRetry();
        Assert.Equal(0, retry.TryBegin(Album, false, true, true, false));
        Assert.Equal(0, retry.TryBegin(Album, true, true, true, false));
        Assert.NotEqual(0, retry.TryBegin(Album, true, true, false, false));
    }

    [Fact]
    public void AlreadyLiveInitialLoadIsNotDuplicated()
    {
        var retry = new LiveReadyRetry();
        Assert.Equal(0, retry.TryBegin(Album, true, true, true, false));
        Assert.Equal(0, retry.TryBegin(Album, true, true, false, false));
    }

    [Fact]
    public void FullyCachedAlbumAndSubsequentEvictionDoNotCauseAnotherFetch()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, true);
        Assert.Equal(0, retry.TryBegin(Album, true, true, false, true));
        Assert.Equal(0, retry.TryBegin(Album, true, true, false, false));
    }

    [Fact]
    public void FailedOrAuthoritativelyEmptyLiveResultDoesNotLoopOnStoreChanges()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        long ticket = retry.TryBegin(Album, true, true, false, false);
        Assert.True(retry.Complete(ticket));
        for (int bulk = 0; bulk < 1000; bulk++)
            Assert.Equal(0, retry.TryBegin(Album, true, true, false, false));
    }

    [Fact]
    public void LiveEdgeWhileParkedDefersUntilActivation()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        Assert.Equal(0, retry.TryBegin(Album, true, false, false, false));
        Assert.NotEqual(0, retry.TryBegin(Album, true, true, false, false));
    }

    [Fact]
    public void ParkingCancelsLatePublicationAndReturnsTheAttempt()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        long old = retry.TryBegin(Album, true, true, false, false);
        retry.Cancel(old); // effect cleanup on activation edge
        Assert.False(retry.Complete(old));
        Assert.Equal(0, retry.TryBegin(Album, true, false, false, false));
        long resumed = retry.TryBegin(Album, true, true, false, false);
        Assert.NotEqual(old, resumed);
        Assert.True(retry.Complete(resumed));
    }

    [Fact]
    public void NewRouteRejectsOldCompletionWithoutRefetchingNewLiveLoad()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        long old = retry.TryBegin(Album, true, true, false, false);
        Assert.Equal(0, retry.TryBegin("album:another", true, true, true, false));
        retry.Cancel(old);
        Assert.False(retry.Complete(old));
        Assert.Equal(0, retry.TryBegin("album:another", true, true, false, false));
    }

    [Fact]
    public void LogoutRejectsQueuedResultAndNextLoginHasOneFreshAttempt()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        long old = retry.TryBegin(Album, true, true, false, false);
        Assert.Equal(0, retry.TryBegin(Album, false, true, false, false));
        Assert.False(retry.Complete(old));
        long next = retry.TryBegin(Album, true, true, false, false);
        Assert.NotEqual(0, next);
        Assert.NotEqual(old, next);
        Assert.True(retry.Complete(next));
    }

    [Fact]
    public void ConcurrentNotificationsDoNotDuplicateRunningAttempt()
    {
        var retry = new LiveReadyRetry();
        retry.TryBegin(Album, false, true, false, false);
        long ticket = retry.TryBegin(Album, true, true, false, false);
        for (int i = 0; i < 100; i++) Assert.Equal(0, retry.TryBegin(Album, true, true, false, false));
        Assert.True(retry.IsCurrent(ticket));
        Assert.True(retry.Complete(ticket));
        Assert.False(retry.Complete(ticket));
    }
}
