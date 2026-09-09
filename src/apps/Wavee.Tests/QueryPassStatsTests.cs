using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>The change-set-driven <see cref="QueryReadContext"/>: on a REJOIN pass, a key not named by the change
/// set (and already present in the previous pass's map) is answered without a catalog Peek or a chunk copy; only
/// changed keys and never-observed keys materialize. Exercised directly against the public
/// <see cref="QueryReadContext"/> constructor (the same one <c>QueryService.Node.Project</c> uses) on a
/// 1500-member/256-key wave, matching the shape <c>QueryServiceCostTests</c> already pins.</summary>
public sealed class QueryPassStatsTests
{
    const int Members = 1500, WaveSize = 256;

    static (CatalogFixture Fixture, ResourceKey[] Keys) SeededCatalog()
    {
        var fixture = new CatalogFixture();
        var keys = new ResourceKey[Members];
        for (int i = 0; i < Members; i++) keys[i] = fixture.Key("track-" + i, FacetKind.TrackIdentity);
        return (fixture, keys);
    }

    static async Task SeedAsync(CatalogFixture fixture, ResourceKey[] keys)
    {
        var requests = await fixture.Repository.CaptureRequestsAsync(keys, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync(requests.Select(request => new ResourceResponse(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(request.Key.Subject))))).ToArray());
    }

    /// <summary>First pass over the full membership commits every key into a <see cref="ResourceMap"/>, exactly
    /// like a node's first join.</summary>
    static async Task<ResourceMap> FirstPassAsync(CatalogFixture fixture, ResourceKey[] keys)
    {
        var builder = ResourceMap.Empty.ToBuilder();
        var read = new QueryReadContext(fixture.Repository, builder, fullRefresh: true, changedKeys: [], observed: null);
        foreach (var key in keys) read.Read(key);
        return read.Commit();
    }

    [Fact]
    public async Task FetchWaveMaterializesOnlyChangedEntries()
    {
        var (fixture, keys) = SeededCatalog();
        await using var _ = fixture;
        await SeedAsync(fixture, keys);
        var map = await FirstPassAsync(fixture, keys);
        Assert.Equal(Members, map.Count);

        // A Durable publication that named exactly WaveSize keys as changed.
        var changed = new HashSet<ResourceKey>(keys.Take(WaveSize));
        var builder = map.ToBuilder();
        var read = new QueryReadContext(fixture.Repository, builder, fullRefresh: false, changedKeys: changed, observed: new());
        foreach (var key in keys) read.Read(key); // the full membership walk a real definition.Read performs

        var stats = read.Stats;
        // Every changed key materializes; every never-observed key would too, but there are none here — the whole
        // membership was already in the map from the first pass.
        Assert.Equal(WaveSize, stats.EntriesMaterialized);
        Assert.Equal(Members, stats.KeysObserved);
    }

    [Fact]
    public async Task NeverObservedKeysAlwaysMaterializeEvenOutsideTheChangeSet()
    {
        var (fixture, keys) = SeededCatalog();
        await using var _ = fixture;
        // Seed only the first half; the rest are Unknown and were never part of any previous pass's map.
        var seeded = keys.Take(Members / 2).ToArray();
        await SeedAsync(fixture, seeded);
        var map = await FirstPassAsync(fixture, seeded);

        var builder = map.ToBuilder();
        var read = new QueryReadContext(fixture.Repository, builder, fullRefresh: false, changedKeys: [], observed: new());
        foreach (var key in keys) read.Read(key); // walks the FULL membership, half of it never observed before

        Assert.Equal(Members - seeded.Length, read.Stats.EntriesMaterialized);
    }

    [Fact]
    public async Task ColdCandidatesOnlyUnknown()
    {
        var (fixture, keys) = SeededCatalog();
        await using var _ = fixture;
        await SeedAsync(fixture, keys.Take(Members - 1).ToArray()); // every key but the last is Present

        var read = new QueryReadContext(fixture.Repository);
        foreach (var key in keys) read.Read(key);

        Assert.Single(read.ColdCandidates);
        Assert.Equal(keys[^1], read.ColdCandidates.Single());
    }

    [Fact]
    public async Task DurableChangeNewMapSharesChunks()
    {
        var (fixture, keys) = SeededCatalog();
        await using var _ = fixture;
        await SeedAsync(fixture, keys);
        var map = await FirstPassAsync(fixture, keys);

        // A contiguous 256-key wave inside the first 512 slots touches at most 2 of the map's 256-wide chunks.
        // Actually re-fetch those keys with a new value so the second pass has something real to observe — Peek
        // on an untouched key returns the SAME cached instance, which SetIfDifferent would (correctly) treat as
        // unchanged.
        var wave = keys.Skip(100).Take(WaveSize).ToArray();
        var changed = new HashSet<ResourceKey>(wave);
        var requests = await fixture.Repository.CaptureRequestsAsync(wave, ResourcePriority.Visible);
        await fixture.Repository.AcceptAsync(requests.Select(request => new ResourceResponse(request,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new TrackIdentityValue(request.Key.Subject + "-v2"))))).ToArray());

        var builder = map.ToBuilder();
        var read = new QueryReadContext(fixture.Repository, builder, fullRefresh: false, changedKeys: changed, observed: new());
        foreach (var key in keys) read.Read(key);
        var next = read.Commit();

        Assert.NotSame(map, next);
        Assert.True(read.Stats.ChunksCopied <= 3, $"expected <=3 chunks copied, got {read.Stats.ChunksCopied}");
        // Every key outside the wave kept its exact snapshot instance from the first pass.
        foreach (var key in keys.Take(100)) Assert.Same(map[key], next[key]);
        foreach (var key in keys.Skip(100 + WaveSize)) Assert.Same(map[key], next[key]);
    }

    [Fact]
    public async Task ReplicasNotReallocatedWhenEqual()
    {
        var (fixture, keys) = SeededCatalog();
        await using var _ = fixture;
        await SeedAsync(fixture, keys);
        var map = await FirstPassAsync(fixture, keys);

        // Two consecutive change-set-driven passes with the SAME (empty) change set touch nothing, so the second
        // Commit hands back the identical map — the allocation-free republish path a status-only pass relies on.
        var builder = map.ToBuilder();
        var read1 = new QueryReadContext(fixture.Repository, builder, fullRefresh: false, changedKeys: [], observed: new());
        foreach (var key in keys) read1.Read(key);
        var pass1 = read1.Commit();
        Assert.Same(map, pass1);

        builder.Reset(pass1);
        var read2 = new QueryReadContext(fixture.Repository, builder, fullRefresh: false, changedKeys: [], observed: new());
        foreach (var key in keys) read2.Read(key);
        var pass2 = read2.Commit();
        Assert.Same(pass1, pass2);
        Assert.Equal(0, read2.Stats.ChunksCopied);
    }
}
