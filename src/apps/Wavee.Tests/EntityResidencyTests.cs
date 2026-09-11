using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// Bounded entity residency (the string-floor fix): LRU eviction with a reachability pin-set, the always-on 12k→8k upsert
// backstop, cold-fallback rehydration after eviction, the census accessors, and the Pathfinder request-body hit-path
// cleanup. All exercised against the source-included Backend (no engine, no GPU).
public class EntityResidencyTests
{
    [Fact]
    public async Task ResidentTrim_PreservesDemandedFactsAndReloadsEvictedFactsFromPersistence()
    {
        await using var fixture = new CatalogFixture();
        var first = fixture.Key("spotify:track:first");
        var second = fixture.Key("spotify:track:second");
        await fixture.AcceptCountAsync(first, 1);
        await fixture.AcceptCountAsync(second, 2);
        Assert.True(fixture.Repository.TrimUnpinned(into => into.Add(first), 1) > 0);
        Assert.NotNull(fixture.Repository.Peek(first).Value);
        Assert.Null(fixture.Repository.Peek(second).Value);
        Assert.Equal(2, Assert.IsType<Wavee.Core.Catalog.PlayCountValue>((await fixture.Repository.ReadAsync(second)).Value).Count);
    }

    [Fact]
    public async Task Pathfinder_CacheHit_DoesNotStrandTheRequestBody()
    {
        var http = new FakeExchange((req, n) => new HttpResp(200, new Dictionary<string, string>(),
            System.Text.Encoding.UTF8.GetBytes("{\"data\":1}")));
        var pf = new PathfinderResource(new PathfinderClient(http),
            () => new SessionContext("", "US", "premium", "en", Tier.Premium, false));

        var first = await pf.GetBytesAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash, null);
        Assert.NotNull(first);
        Assert.Equal(1, pf.FetchCount);        // the miss fetched
        Assert.Equal(0, pf.PendingBodyCount);  // FetchAsync removed its own body

        var second = await pf.GetBytesAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash, null);
        Assert.NotNull(second);
        Assert.Equal(1, pf.FetchCount);        // served from cache (no second fetch) → this was the HIT path
        Assert.Equal(0, pf.PendingBodyCount);  // …and the hit path cleaned up the body it set (the leak fix)
    }

}
