using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee;
using Wavee.Backend.Hydration;
using Xunit;

namespace Wavee.Tests;

// ── The one background lane (design §2.1) ────────────────────────────────────────────────────────────────────────────
// Two properties that only matter when something has already gone wrong, which is exactly when they must hold: the
// queue has to REFUSE work rather than grow without bound, and shutting the lane down must not throw out of a
// background task nobody is in a position to catch.
public class HydrationPumpTests
{
    [Fact]
    public void AFullQueue_ShedsTheLowestPriority_AndStaysAtCapacity()
    {
        // Producers enqueue from INSIDE jobs (the ref-closure, EnsureManyAsync's Background mode), so a wedged
        // transport plus a big list used to be a queue that grew until the process died. Concurrency 1 with a job that
        // never returns pins the lane so the queue can actually fill.
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new HydrationPump(CancellationToken.None, concurrency: 1, capacity: 4);
        pump.Enqueue(10, _ => block.Task);
        SpinWait.SpinUntil(() => pump.Running == 1, TimeSpan.FromSeconds(5));

        for (int i = 0; i < 4; i++) pump.Enqueue(1, _ => Task.CompletedTask);       // fills it
        Assert.Equal(4, pump.Pending);
        Assert.Equal(0, pump.Dropped);

        pump.Enqueue(-1, _ => Task.CompletedTask);                                   // prefetch at a full queue
        Assert.Equal(4, pump.Pending);                                               // refused, not admitted
        Assert.Equal(1, pump.Dropped);

        block.SetResult();
    }

    [Fact]
    public void AFullQueue_LetsInteractiveWorkDisplacePrefetch()
    {
        // Shedding "the lowest priority present" and not "the newcomer" is the half that matters: an open must still
        // get in when the queue is full of prefetch.
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new HydrationPump(CancellationToken.None, concurrency: 1, capacity: 2);
        pump.Enqueue(10, _ => block.Task);
        SpinWait.SpinUntil(() => pump.Running == 1, TimeSpan.FromSeconds(5));

        int ran = 0;
        pump.Enqueue(-1, _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        pump.Enqueue(-1, _ => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        pump.Enqueue(5, _ => { Interlocked.Add(ref ran, 100); return Task.CompletedTask; });   // displaces a prefetch

        Assert.Equal(2, pump.Pending);
        Assert.Equal(1, pump.Dropped);

        block.SetResult();
        SpinWait.SpinUntil(() => pump.Pending == 0 && pump.Running == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(101, Volatile.Read(ref ran));   // the interactive job plus ONE surviving prefetch
    }

    [Fact]
    public async Task DisposeWhileDraining_DoesNotThrowOutOfTheLoop()
    {
        // The drain loop and a running job both touch the token AFTER Dispose returns; disposing the source under them
        // is the classic ObjectDisposedException from a background task nobody catches. Dispose cancels and leaves the
        // source alive precisely so those touches stay legal.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? escaped = null;
        var pump = new HydrationPump(CancellationToken.None, concurrency: 1);

        pump.Enqueue(0, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        for (int i = 0; i < 8; i++) pump.Enqueue(-1, _ => Task.CompletedTask);   // queued behind the parked job
        await started.Task;

        try
        {
            pump.Dispose();
            pump.Dispose();          // idempotent
            release.SetResult();
            await Task.Delay(50);
            pump.Enqueue(0, _ => Task.CompletedTask);   // a producer that did not notice the shutdown
        }
        catch (Exception ex) { escaped = ex; }

        Assert.Null(escaped);
        Assert.Equal(0, pump.Pending);                 // the queue is dropped, not left holding closures
        Assert.True(pump.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task JobsRunHighestPriorityFirst_ThenFifo()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new HydrationPump(CancellationToken.None, concurrency: 1);
        pump.Enqueue(100, _ => block.Task);
        SpinWait.SpinUntil(() => pump.Running == 1, TimeSpan.FromSeconds(5));

        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        pump.Enqueue(-1, _ => { order.Enqueue("prefetch"); return Task.CompletedTask; });
        pump.Enqueue(1, _ => { order.Enqueue("open-a"); return Task.CompletedTask; });
        pump.Enqueue(1, _ => { order.Enqueue("open-b"); return Task.CompletedTask; });

        block.SetResult();
        for (int i = 0; i < 200 && order.Count < 3; i++) await Task.Delay(5);

        Assert.Equal(["open-a", "open-b", "prefetch"], order);
    }

    [Fact]
    public void FullQueueWarning_IsSampledWhileDroppedCountRemainsExact()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CapturingWaveeLog { MinLevel = WaveeLogLevel.Warning };
        using var pump = new HydrationPump(CancellationToken.None, new WaveeLogger(sink, "hydration"),
            concurrency: 1, capacity: 1);
        pump.Enqueue(10, _ => block.Task);
        SpinWait.SpinUntil(() => pump.Running == 1, TimeSpan.FromSeconds(5));
        pump.Enqueue(0, _ => Task.CompletedTask);   // occupy the sole queue slot

        for (int i = 0; i < 512; i++) pump.Enqueue(-1, _ => Task.CompletedTask);

        Assert.Equal(512, pump.Dropped);
        Assert.Equal(3, sink.Entries.Count(e => e.EventId == "hydration.pump.full"));   // drops 1, 256, 512
        block.SetResult();
    }
}
