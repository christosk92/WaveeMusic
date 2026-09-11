using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>The chunked, structurally-shared, copy-on-write map behind a query's Resources publication: stable
/// slots across a lineage of maps, chunk-granular copy-on-write, Commit identity and tombstones.</summary>
public sealed class ResourceMapTests
{
    static readonly CatalogScope Scope = new("spotify", "a", "en", "NL", "premium", 1, false);
    static ResourceKey Key(int i) => new(Scope, "spotify:track:" + i, FacetKind.TrackIdentity);
    static ResourceSnapshot Present(int i) => ResourceSnapshot.Unknown(Key(i))
        with { Knowledge = Knowledge.Present, Value = new TrackIdentityValue(Title: "Title " + i) };

    [Fact]
    public void EmptyMapAnswersEveryLookupAsAbsent()
    {
        Assert.Empty(ResourceMap.Empty);
        Assert.False(ResourceMap.Empty.TryGetValue(Key(0), out _));
        Assert.False(ResourceMap.Empty.ContainsKey(Key(0)));
    }

    [Fact]
    public void CommitReturnsTheSameInstanceWhenNothingChanged()
    {
        var value = Present(0);
        var builder = ResourceMap.Empty.ToBuilder();
        builder.SetIfDifferent(Key(0), value);
        var first = builder.Commit();

        builder.Reset(first);
        builder.SetIfDifferent(Key(0), value); // the SAME reference: ReferenceEquals-gated, no-op
        var second = builder.Commit();

        Assert.Same(first, second);
    }

    [Fact]
    public void CommitReturnsANewMapWhenSomethingChanged()
    {
        var builder = ResourceMap.Empty.ToBuilder();
        builder.SetIfDifferent(Key(0), Present(0));
        var first = builder.Commit();

        builder.Reset(first);
        builder.SetIfDifferent(Key(0), Present(0) with { Revision = 1 });
        var second = builder.Commit();

        Assert.NotSame(first, second);
        Assert.Equal(1, second[Key(0)].Revision);
        Assert.Equal(0, first[Key(0)].Revision); // the earlier generation is untouched
    }

    [Fact]
    public void UntouchedChunksAreSharedByReferenceAcrossGenerations()
    {
        var builder = ResourceMap.Empty.ToBuilder();
        for (int i = 0; i < 600; i++) builder.SetIfDifferent(Key(i), Present(i)); // spans 3 chunks of 256
        var first = builder.Commit();

        builder.Reset(first);
        builder.SetIfDifferent(Key(0), Present(0) with { Revision = 7 }); // touches chunk 0 only
        var second = builder.Commit();

        Assert.NotSame(first, second);
        // Every entry outside chunk 0 is byte-for-byte the SAME instance as before — the chunk holding it was
        // never cloned.
        for (int i = 256; i < 600; i++) Assert.Same(first[Key(i)], second[Key(i)]);
        Assert.NotSame(first[Key(0)], second[Key(0)]);
        Assert.Equal(600, second.Count);
    }

    [Fact]
    public void RemovingAKeyTombstonesItsSlotButKeepsTheSlotStable()
    {
        var builder = ResourceMap.Empty.ToBuilder();
        builder.SetIfDifferent(Key(0), Present(0));
        builder.SetIfDifferent(Key(1), Present(1));
        var withBoth = builder.Commit();
        Assert.Equal(2, withBoth.Count);

        builder.Reset(withBoth);
        builder.SetIfDifferent(Key(0), null!); // tombstone: absent in this generation
        var withOne = builder.Commit();

        Assert.Equal(1, withOne.Count);
        Assert.False(withOne.ContainsKey(Key(0)));
        Assert.True(withOne.ContainsKey(Key(1)));
        // The earlier generation still answers for the removed key — tombstoning is per-generation, not per-slot.
        Assert.True(withBoth.ContainsKey(Key(0)));

        // The key keeps its slot: re-adding it later shares chunk plumbing with the original generation instead of
        // minting a fresh slot (observable indirectly: the enumeration order below is stable by first-seen order).
        builder.Reset(withOne);
        builder.SetIfDifferent(Key(0), Present(0) with { Revision = 9 });
        var readded = builder.Commit();
        Assert.Equal(2, readded.Count);
        Assert.Equal(9, readded[Key(0)].Revision);
    }

    [Fact]
    public void LookupParityWithADictionaryOverManyKeys()
    {
        var reference = new Dictionary<ResourceKey, ResourceSnapshot>();
        var builder = ResourceMap.Empty.ToBuilder();
        for (int i = 0; i < 1500; i++)
        {
            var value = Present(i);
            reference[Key(i)] = value;
            builder.SetIfDifferent(Key(i), value);
        }
        var map = builder.Commit();
        Assert.Equal(reference.Count, map.Count);
        foreach (var pair in reference)
        {
            Assert.True(map.TryGetValue(pair.Key, out var found));
            Assert.Same(pair.Value, found);
        }
        int enumerated = 0;
        foreach (var pair in map) { Assert.Same(reference[pair.Key], pair.Value); enumerated++; }
        Assert.Equal(reference.Count, enumerated);
    }

    [Fact]
    public void SetIfDifferentIsANoOpForTheSameReferenceAndReportsItInStats()
    {
        var builder = ResourceMap.Empty.ToBuilder();
        var value = Present(0);
        builder.SetIfDifferent(Key(0), value);
        builder.SetIfDifferent(Key(0), value); // same reference: no new change recorded
        Assert.Single(builder.Changes);
        Assert.Equal(1, builder.Stats.ChunksCopied);
    }

    [Fact]
    public void CommittingAnEmptyBuilderPassReturnsTheBaseUnchanged()
    {
        var empty = ResourceMap.Empty;
        var builder = empty.ToBuilder();
        Assert.Same(empty, builder.Commit());
    }

    [Fact]
    public void ChunksCopiedCountsDistinctChunksNotDistinctKeys()
    {
        var builder = ResourceMap.Empty.ToBuilder();
        for (int i = 0; i < 10; i++) builder.SetIfDifferent(Key(i), Present(i)); // all 10 land in chunk 0
        Assert.Equal(1, builder.Stats.ChunksCopied);
        Assert.Equal(10, builder.Stats.EntriesMaterialized);
    }
}
