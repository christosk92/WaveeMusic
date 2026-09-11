using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class DataPublicationTests
{
    [Fact]
    public async Task NestedOwnerPublicationsNotifyOnlyAfterEveryStateSwap()
    {
        await using var queue = new DataCommitQueue();
        int catalog = 0, replica = 0;
        bool early = false;
        var seen = new List<(int Catalog, int Replica)>();
        queue.Publish(() =>
        {
            catalog = 1;
            queue.NotifyAfterPublish(() => seen.Add(queue.ReadConsistent(() => (catalog, replica))));
            queue.Publish(() =>
            {
                replica = 2;
                queue.NotifyAfterPublish(() => seen.Add(queue.ReadConsistent(() => (catalog, replica))));
            });
            early = seen.Count != 0;
        });
        Assert.False(early);
        Assert.Equal(new[] { (1, 2), (1, 2) }, seen);
    }

    [Fact]
    public async Task ObserverFailureCannotFailAnAlreadyDurableCommitOrSuppressTheNextObserver()
    {
        await using var queue = new DataCommitQueue();
        bool persisted = false, published = false, observed = false;
        var result = await queue.CommitAsync(() => new DataCommit<int>(_ =>
        { persisted = true; return ValueTask.CompletedTask; }, () =>
        {
            published = true;
            queue.NotifyAfterPublish(() => throw new System.InvalidOperationException("observer"));
            queue.NotifyAfterPublish(() => observed = persisted && published);
        }, 7));
        Assert.Equal(7, result);
        Assert.True(observed);
    }
}
