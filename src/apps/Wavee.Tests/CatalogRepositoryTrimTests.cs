using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>TrimUnpinned's k-smallest-by-FetchedAt eviction (a bounded max-heap instead of a full sort of the
/// resident table) and its pooled retained set (the caller writes pins into a reused <see cref="HashSet{T}"/>
/// instead of TrimUnpinned allocating a fresh one every quiet-coalesced look).</summary>
public sealed class CatalogRepositoryTrimTests
{
    [Fact]
    public async Task EvictsExactlyTheOldestUnpinnedEntriesDownToTheMaximum()
    {
        await using var fixture = new CatalogFixture();
        var keys = new ResourceKey[20];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = fixture.Key("track-" + i);
            await fixture.AcceptCountAsync(keys[i], i);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1)); // strictly increasing FetchedAt, oldest first
        }

        // Pin the 5 NEWEST so eviction must reach into the middle of the table, not just its tail.
        var pinned = new HashSet<ResourceKey> { keys[15], keys[16], keys[17], keys[18], keys[19] };
        long freed = fixture.Repository.TrimUnpinned(into => { foreach (var key in pinned) into.Add(key); }, 10);

        Assert.True(freed > 0);
        Assert.Equal(10, fixture.Repository.ResidentCount);
        // The 10 OLDEST unpinned entries (indices 0..9) are gone; the pinned newest 5 plus the next-oldest
        // unpinned 5 (indices 10..14) survive.
        for (int i = 0; i < 10; i++) Assert.Null(fixture.Repository.Peek(keys[i]).Value);
        for (int i = 10; i < 20; i++) Assert.NotNull(fixture.Repository.Peek(keys[i]).Value);
    }

    [Fact]
    public async Task PinnedEntriesAreNeverEvictedEvenWhenOldest()
    {
        await using var fixture = new CatalogFixture();
        var oldest = fixture.Key("oldest");
        await fixture.AcceptCountAsync(oldest, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = fixture.Key("newer");
        await fixture.AcceptCountAsync(newer, 2);

        fixture.Repository.TrimUnpinned(into => into.Add(oldest), 1);

        Assert.NotNull(fixture.Repository.Peek(oldest).Value);   // pinned, survives despite being oldest
        Assert.Null(fixture.Repository.Peek(newer).Value);        // unpinned, evicted to reach the maximum
    }

    [Fact]
    public async Task BelowTheMaximumEvictsNothing()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key("solo");
        await fixture.AcceptCountAsync(key, 1);

        long freed = fixture.Repository.TrimUnpinned(_ => { }, 10);

        Assert.Equal(0, freed);
        Assert.NotNull(fixture.Repository.Peek(key).Value);
    }

    [Fact]
    public async Task TheCollectPinsCallbackAlwaysSeesAnEmptySetOnEntry()
    {
        await using var fixture = new CatalogFixture();
        var key = fixture.Key("only");
        await fixture.AcceptCountAsync(key, 1);

        // Two looks in a row: the second must not see anything the first call's callback left behind in the
        // repository's own reused set.
        fixture.Repository.TrimUnpinned(into => into.Add(fixture.Key("irrelevant")), 100);
        fixture.Repository.TrimUnpinned(into =>
        {
            Assert.Empty(into);
            into.Add(key);
        }, 0);

        Assert.NotNull(fixture.Repository.Peek(key).Value); // pinned via the callback, survives a maximum of 0
    }
}
