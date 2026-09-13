using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SingleFlightMemoTests
{
    [Fact]
    public async Task ConcurrentCallers_ShareOneFlight()
    {
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMinutes(1));
        int startCount = 0;
        var gate = new TaskCompletionSource();

        async Task<int?> Start(string key)
        {
            Interlocked.Increment(ref startCount);
            await gate.Task;
            return 42;
        }

        var t1 = memo.GetOrStartAsync("k", Start, CancellationToken.None);
        var t2 = memo.GetOrStartAsync("k", Start, CancellationToken.None);
        Assert.False(t1.IsCompleted);
        Assert.False(t2.IsCompleted);

        gate.SetResult();
        Assert.Equal(42, await t1);
        Assert.Equal(42, await t2);
        Assert.Equal(1, startCount);   // ONE flight served both callers
    }

    [Fact]
    public async Task SuccessfulResult_CachedUntilTtlExpires()
    {
        long now = 0;
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMilliseconds(100), () => now);
        int calls = 0;

        Task<int?> Start(string key) { calls++; return Task.FromResult<int?>(calls); }

        Assert.Equal(1, await memo.GetOrStartAsync("k", Start, CancellationToken.None));

        now += 50;   // inside the TTL window
        Assert.Equal(1, await memo.GetOrStartAsync("k", Start, CancellationToken.None));
        Assert.Equal(1, calls);   // still the cached answer — no second flight

        now += 100;   // past the TTL window
        Assert.Equal(2, await memo.GetOrStartAsync("k", Start, CancellationToken.None));
        Assert.Equal(2, calls);   // expired → a fresh flight ran
    }

    [Fact]
    public async Task CallerCancel_DetachesWithoutCancellingTheFlight()
    {
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMinutes(1));
        var gate = new TaskCompletionSource();
        bool flightRanToCompletion = false;

        async Task<int?> Start(string key)
        {
            await gate.Task;
            flightRanToCompletion = true;
            return 7;
        }

        using var cts = new CancellationTokenSource();
        var cancelledCaller = memo.GetOrStartAsync("k", Start, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);

        // The flight itself must still be alive — it was never linked to the cancelled caller's token.
        gate.SetResult();
        var laterCaller = await memo.GetOrStartAsync("k", Start, CancellationToken.None);
        Assert.True(flightRanToCompletion);
        Assert.Equal(7, laterCaller);
    }

    [Fact]
    public async Task Invalidate_MidFlight_NextCallerStartsAFreshFlight()
    {
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMinutes(1));
        var gate1 = new TaskCompletionSource();
        int calls = 0;

        async Task<int?> Start(string key)
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 1) await gate1.Task;   // only the FIRST flight blocks
            return n;
        }

        var firstFlight = memo.GetOrStartAsync("k", Start, CancellationToken.None);
        memo.Invalidate("k");
        var secondCaller = memo.GetOrStartAsync("k", Start, CancellationToken.None);

        Assert.Equal(2, calls);   // the second caller did NOT join the invalidated (still in-flight) first entry
        Assert.Equal(2, await secondCaller);

        gate1.SetResult();
        Assert.Equal(1, await firstFlight);   // the invalidated flight still ran to completion for its own caller
    }

    [Fact]
    public async Task FailedFlight_IsNotCached_NextCallRetries()
    {
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMinutes(1));
        int calls = 0;

        Task<int?> Start(string key)
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("boom");
            return Task.FromResult<int?>(99);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => memo.GetOrStartAsync("k", Start, CancellationToken.None));

        // The fault must NOT be cached — the next call starts a brand new flight rather than replaying the exception.
        Assert.Equal(99, await memo.GetOrStartAsync("k", Start, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DifferentKeys_NeverShareAFlight()
    {
        var memo = new SingleFlightMemo<string>(TimeSpan.FromMinutes(1));
        Task<string?> Start(string key) => Task.FromResult<string?>("value:" + key);

        Assert.Equal("value:a", await memo.GetOrStartAsync("a", Start, CancellationToken.None));
        Assert.Equal("value:b", await memo.GetOrStartAsync("b", Start, CancellationToken.None));
    }

    [Fact]
    public async Task Clear_DropsEveryEntry()
    {
        var memo = new SingleFlightMemo<int?>(TimeSpan.FromMinutes(1));
        int calls = 0;
        Task<int?> Start(string key) { calls++; return Task.FromResult<int?>(calls); }

        Assert.Equal(1, await memo.GetOrStartAsync("k", Start, CancellationToken.None));
        memo.Clear();
        Assert.Equal(2, await memo.GetOrStartAsync("k", Start, CancellationToken.None));
    }
}
