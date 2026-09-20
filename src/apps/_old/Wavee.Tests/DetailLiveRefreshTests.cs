using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The open detail page's live-refresh driver. These pin the two properties the old inline debounce did NOT have, and
/// whose absence is what made an optimistic playlist edit take seconds to appear: the FIRST request after idle refreshes
/// immediately (no debounce ahead of the user's own edit), and a request that lands while a pass is running can never
/// abandon that pass — it folds into exactly one follow-up.
/// </summary>
public class DetailLiveRefreshTests
{
    static Func<int, CancellationToken, Task> NoSettle => (_, _) => Task.CompletedTask;

    [Fact]
    public async Task FirstRequestAfterIdle_RunsImmediately_WithNoDebounce()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A settle that never completes: if the pump debounced ahead of the first pass, this test would hang.
        using var pump = new DetailLiveRefresh(_ => { ran.TrySetResult(); return Task.CompletedTask; },
            delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));

        pump.Request();

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, pump.Passes);
    }

    [Fact]
    public async Task ChangesDuringAPass_NeverCancelIt_AndCoalesceIntoOneFollowUp()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;

        using var pump = new DetailLiveRefresh(async _ =>
        {
            if (Interlocked.Increment(ref completed) == 1)
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);   // hold pass #1 open across the burst
            }
            else finished.TrySetResult();
        }, NoSettle);

        pump.Request();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The store keeps talking while the page is mid-load: a trait-projector bulk, the drain's adopt, a revalidate.
        // The OLD shape cancelled the in-flight load on every one of these, so the page's own optimistic edit never
        // reached a publish while the traffic lasted.
        for (int i = 0; i < 20; i++) pump.Request();

        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Pass #1 completed (it was never abandoned), and 20 signals folded into exactly ONE follow-up.
        Assert.Equal(2, Volatile.Read(ref completed));
        Assert.Equal(2, pump.Passes);
    }

    [Fact]
    public async Task RequestWhileIdle_AfterAPass_StartsAFreshPass()
    {
        int n = 0;
        var pass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new DetailLiveRefresh(_ => { if (Interlocked.Increment(ref n) == 2) pass.TrySetResult(); return Task.CompletedTask; }, NoSettle);

        pump.Request();
        await WaitUntil(() => !pump.Busy);
        pump.Request();

        await pass.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, pump.Passes);
    }

    [Fact]
    public async Task AFaultedPass_DoesNotStrandThePump()
    {
        int n = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new DetailLiveRefresh(_ =>
        {
            if (Interlocked.Increment(ref n) == 1) throw new InvalidOperationException("load blew up");
            second.TrySetResult();
            return Task.CompletedTask;
        }, NoSettle);

        pump.Request();
        await WaitUntil(() => !pump.Busy);
        pump.Request();

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispose_StopsFurtherPasses()
    {
        int n = 0;
        var pump = new DetailLiveRefresh(_ => { Interlocked.Increment(ref n); return Task.CompletedTask; }, NoSettle);
        pump.Request();
        await WaitUntil(() => !pump.Busy);
        pump.Dispose();
        pump.Request();
        Assert.Equal(1, Volatile.Read(ref n));
    }

    [Fact]
    public async Task RequestDuringCooldown_IsDelayedAndCoalescedIntoOneTrailingPass()
    {
        int passes = 0;
        var cooldownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCooldown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new DetailLiveRefresh(
            _ => { Interlocked.Increment(ref passes); return Task.CompletedTask; },
            async (_, ct) =>
            {
                cooldownEntered.TrySetResult();
                await releaseCooldown.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        pump.Request();
        await cooldownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(pump.Busy);
        Assert.Equal(1, Volatile.Read(ref passes));

        for (int i = 0; i < 50; i++) pump.Request();
        Assert.Equal(1, Volatile.Read(ref passes));
        releaseCooldown.SetResult();

        await WaitUntil(() => !pump.Busy);
        Assert.Equal(2, Volatile.Read(ref passes));
        Assert.Equal(2, pump.Passes);
    }

    [Fact]
    public async Task SustainedTraffic_RunsAtMostOnePassPerCooldown_AndTripsOncePerWindow()
    {
        int passes = 0, cooldowns = 0, storms = 0;
        long now = 0, stormAt = 0;
        DetailLiveRefresh? pump = null;
        pump = new DetailLiveRefresh(
            _ => { Interlocked.Increment(ref passes); return Task.CompletedTask; },
            (ms, _) =>
            {
                Interlocked.Increment(ref cooldowns);
                Interlocked.Add(ref now, ms);
                if (Volatile.Read(ref cooldowns) < 60)
                    for (int i = 0; i < 25; i++) pump!.Request();
                return Task.CompletedTask;
            },
            nowMs: () => Interlocked.Read(ref now),
            onStorm: count => { Interlocked.Increment(ref storms); Interlocked.Exchange(ref stormAt, count); });
        using (pump)
        {
            pump.Request();
            await WaitUntil(() => !pump.Busy);

            Assert.Equal(60, Volatile.Read(ref passes));
            Assert.Equal(60, Volatile.Read(ref cooldowns));
            Assert.Equal(1, Volatile.Read(ref storms));
            Assert.Equal(DetailLiveRefresh.StormPasses + 1, Interlocked.Read(ref stormAt));
        }
    }

    [Fact]
    public async Task Dispose_CancelsACoalescedTrailingPass()
    {
        int passes = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new DetailLiveRefresh(async _ =>
        {
            Interlocked.Increment(ref passes);
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
        }, NoSettle);

        pump.Request();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pump.Request();
        pump.Dispose();
        release.SetResult();

        await WaitUntil(() => !pump.Busy);
        Assert.Equal(1, Volatile.Read(ref passes));
    }

    static async Task WaitUntil(Func<bool> cond)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "condition never became true");
    }
}
