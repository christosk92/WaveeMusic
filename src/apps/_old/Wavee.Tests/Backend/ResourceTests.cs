using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests.Engine;

public class ResourceTests
{
    static SessionContext Ctx() => SessionContext.LoggedOut;

    [Fact]
    public async Task GetAsync_SequentialSameKey_Immutable_FetchesOnce()
    {
        int calls = 0;
        var res = new Resource<string, string>((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("v"); },
            new FreshnessPolicy.Immutable(), Ctx);
        await res.GetAsync("k");
        await res.GetAsync("k");
        Assert.Equal(1, res.FetchCount);
        Assert.Equal(1, res.HitCount);
    }

    [Fact]
    public async Task GetAsync_PerEntryTtl_ExpiryRefetches()
    {
        var res = new Resource<string, string>((_, _) => Task.FromResult("v"),
            new FreshnessPolicy.Immutable(), Ctx, ttlOf: _ => TimeSpan.FromMilliseconds(50));
        await res.GetAsync("k");
        await Task.Delay(80);
        await res.GetAsync("k");
        Assert.Equal(2, res.FetchCount);
    }

    [Fact]
    public async Task GetAsync_PerEntryTtl_FreshHits()
    {
        var res = new Resource<string, string>((_, _) => Task.FromResult("v"),
            new FreshnessPolicy.Immutable(), Ctx, ttlOf: _ => TimeSpan.FromSeconds(30));
        await res.GetAsync("k");
        await res.GetAsync("k");
        Assert.Equal(1, res.FetchCount);
        Assert.Equal(1, res.HitCount);
    }

    [Fact]
    public async Task Invalidate_DropsValue_NextGetFetches()
    {
        var res = new Resource<string, string>((_, _) => Task.FromResult("v"),
            new FreshnessPolicy.Immutable(), Ctx);
        await res.GetAsync("k");
        res.Invalidate("k");
        Assert.False(res.Peek("k").IsReady);
        await res.GetAsync("k");
        Assert.Equal(2, res.FetchCount);
    }

    [Fact]
    public async Task GetAsync_CallerCancelled_FetchStillSeedsCache()
    {
        var res = new Resource<string, string>(async (_, _) =>
        {
            await Task.Delay(100);
            return "v";
        }, new FreshnessPolicy.Immutable(), Ctx);
        using var cts = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => res.GetAsync("k", cts.Token));
        await res.GetAsync("k");
        Assert.Equal(1, res.FetchCount);
        Assert.True(res.Peek("k").IsReady);
    }

    [Fact]
    public async Task GetAsync_ErroredEntry_RetriesAndSurfacesErr()
    {
        int calls = 0;
        var res = new Resource<string, string>((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("fail");
            return Task.FromResult("ok");
        }, new FreshnessPolicy.Immutable(), Ctx);
        var first = await res.GetAsync("k");
        Assert.False(first.IsReady);
        Assert.Equal("fail", first.Error);
        var second = await res.GetAsync("k");
        Assert.True(second.IsReady);
        Assert.Equal("ok", second.Value);
        Assert.Equal(2, res.FetchCount);
    }

    [Fact]
    public async Task MaxEntries_EvictsLru_NotInFlight()
    {
        var res = new Resource<string, string>((key, _) => Task.FromResult(key),
            new FreshnessPolicy.Immutable(), Ctx, maxEntries: 2);
        await res.GetAsync("A");
        await res.GetAsync("B");
        await res.GetAsync("C");
        Assert.False(res.Peek("A").IsReady);
        await res.GetAsync("A");
        Assert.Equal(4, res.FetchCount);
    }
}
