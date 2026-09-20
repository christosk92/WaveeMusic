using System;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Xunit;

namespace Wavee.Tests;

// Collection token-delta application: added/removed items fold onto the Store's set membership, coalesced into one signal.
public class CollectionDeltaApplierTests
{
    sealed class Counter : IObserver<StoreChange>
    {
        public int N;
        public void OnNext(StoreChange v) => N++;
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    [Fact]
    public void Apply_AddsAndRemoves_SetMembership()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:keep", true, SyncState.Confirmed);
        store.SetSaved("albums", "spotify:album:gone", true, SyncState.Confirmed);

        CollectionDeltaApplier.Apply(store, new CollectionDelta("albums", "tok-2", new[]
        {
            new CollectionItem("spotify:album:new", false, 100),
            new CollectionItem("spotify:album:gone", true, 0),
        }));

        Assert.True(store.IsSaved("albums", "spotify:album:keep"));   // untouched
        Assert.True(store.IsSaved("albums", "spotify:album:new"));    // added
        Assert.False(store.IsSaved("albums", "spotify:album:gone"));  // removed
    }

    [Fact]
    public void Apply_CoalescesIntoOneBulkSignal()
    {
        var store = new InMemoryStore();
        var counter = new Counter();
        using var sub = store.Changes.Subscribe(counter);

        CollectionDeltaApplier.Apply(store, new CollectionDelta("liked", "t", new[]
        {
            new CollectionItem("spotify:track:a", false, 0),
            new CollectionItem("spotify:track:b", false, 0),
            new CollectionItem("spotify:track:c", false, 0),
        }));

        Assert.Equal(1, counter.N);   // BeginBulk → exactly one Bulk signal for the whole delta, not one per item
    }
}
