using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Hydration;

// ── The ONE background lane for hydration work that no surface waits on (design §2.1) ────────────────────────────────
// Prefetch waves, the ref-closure, below-the-fold Full upgrades, show episode paging, trait fills after a container
// opened — all of it used to be ad-hoc `Task.Run` fan-outs with their own breathers and caps (DiscographyPrefetcher's
// 250 ms sleep, PagedHydrateAsync's 300 pages, MetadataService.ScheduleClosure). One bounded, priority-ordered lane
// with the session's cancellation token means: a logout stops everything at once, interactive work (priority ≥ 0)
// always runs before prefetch (−1), and the concurrency is a single knob.
//
// "Bounded" is meant literally, and was not: the queue had no ceiling at all. That is only safe while every producer is
// a page open, and it is not — the ref-closure and `EnsureManyAsync`'s Background mode both enqueue from INSIDE jobs, so
// a wedged transport plus a 10k list is a queue that grows until the process dies. It now sheds at a hard cap, lowest
// priority first, which is the right thing to drop: a prefetch is by definition work nothing is waiting for.
public sealed class HydrationPump : IDisposable
{
    readonly record struct Job(int Priority, long Seq, Func<CancellationToken, Task> Work);

    /// <summary>The queue ceiling. 4096 is far above any real burst (a 10k list pages 300 at a time — ~34 jobs) and far
    /// below "the queue IS the leak".</summary>
    public const int DefaultCapacity = 4096;

    readonly PriorityQueue<Job, (int NegPriority, long Seq)> _queue = new();
    readonly object _gate = new();
    readonly SemaphoreSlim _slots;
    readonly CancellationTokenSource _cts;
    // Captured ONCE, at construction. Every loop and every job reads this rather than `_cts.Token`, because `.Token` on
    // a disposed source THROWS — and a drain loop necessarily outlives Dispose by however long its job takes. Reading a
    // cancelled token is always safe, which is why Dispose cancels and never disposes (see below).
    readonly CancellationToken _token;
    readonly WaveeLogger _log;
    readonly int _capacity;
    long _seq;
    int _running;
    int _dropped;
    int _disposed;
    int _draining;

    /// <param name="concurrency">How many jobs may run at once. 2 keeps a prefetch wave from monopolising the lane
    /// against the Background-mode work a surface actually asked for, while still overlapping network with projection.</param>
    /// <param name="capacity">The queue ceiling; work past it is shed lowest-priority-first (see <see cref="Enqueue"/>).</param>
    public HydrationPump(CancellationToken sessionCt, WaveeLogger log = default, int concurrency = 2,
                         int capacity = DefaultCapacity)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        _token = _cts.Token;
        _slots = new SemaphoreSlim(Math.Max(1, concurrency));
        _capacity = Math.Max(1, capacity);
        _log = log;
    }

    /// <summary>The lane's own token: linked to the SESSION, independent of any one caller. The provider hydrator runs
    /// its shared ladder passes on this, so a caller navigating away cancels its own wait and nobody else's.</summary>
    public CancellationToken Token => _token;
    public int Pending { get { lock (_gate) return _queue.Count; } }
    public int Running => Volatile.Read(ref _running);
    /// <summary>How many jobs the ceiling has shed this session. Non-zero is a real signal (the lane is saturated), not
    /// noise — it is the diagnostic that tells a saturated pump apart from a slow one.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Queue work. Higher <paramref name="priority"/> runs first; equal priorities run FIFO. Never throws to
    /// the caller; a job's exception is logged and swallowed (a pump job is best-effort by definition).
    /// <para>At <see cref="DefaultCapacity"/> the queue SHEDS rather than grows, and it sheds the LAST job in run order
    /// — the lowest priority, latest arrival. The newcomer is usually that job (a prefetch flood is refused at the
    /// door), but an interactive enqueue arriving at a queue full of prefetch displaces one instead, which is the whole
    /// point of shedding by priority rather than by arrival.</para></summary>
    public void Enqueue(int priority, Func<CancellationToken, Task> work)
    {
        if (work is null || _token.IsCancellationRequested) return;
        int evicted = 0;
        bool shed = false;
        lock (_gate)
        {
            var key = (-priority, ++_seq);
            var job = new Job(priority, _seq, work);
            if (_queue.Count < _capacity) _queue.Enqueue(job, key);
            else
            {
                // The heap is ordered by (-priority, seq), so "runs last" is the MAXIMUM key — and a binary heap can
                // only cheaply reach its minimum. The scan is O(capacity) and happens ONLY on the shedding path, i.e.
                // only once the lane is already saturated; paying it there is far cheaper than the second index it
                // would take to avoid, and it keeps the steady-state enqueue a single heap push.
                shed = true;
                var (worstJob, worstKey) = Worst();
                if (Compare(worstKey, key) > 0)
                {
                    _queue.Remove(worstJob, out _, out _);
                    _queue.Enqueue(job, key);
                    evicted = worstJob.Priority;
                }
                else evicted = priority;   // nothing queued is worse than the newcomer — refuse it at the door
            }
        }
        if (shed)
        {
            int dropped = Interlocked.Increment(ref _dropped);
            if (dropped == 1 || (dropped & 255) == 0)
                _log.Event(WaveeLogLevel.Warning, "hydration.pump.full", "hydration queue full — lowest-priority job dropped",
                    fields: [WaveeLogField.Of("capacity", _capacity), WaveeLogField.Of("priority", priority),
                             WaveeLogField.Of("droppedPriority", evicted), WaveeLogField.Of("dropped", dropped)]);
        }
        Kick();
    }

    /// <summary>The queued job that would run LAST. Caller holds <c>_gate</c>.</summary>
    (Job Job, (int NegPriority, long Seq) Key) Worst()
    {
        Job worstJob = default;
        (int NegPriority, long Seq) worstKey = default;
        bool found = false;
        foreach (var (element, key) in _queue.UnorderedItems)
            if (!found || Compare(key, worstKey) > 0) { worstJob = element; worstKey = key; found = true; }
        return (worstJob, worstKey);
    }

    static int Compare((int NegPriority, long Seq) a, (int NegPriority, long Seq) b)
        => a.NegPriority != b.NegPriority ? a.NegPriority.CompareTo(b.NegPriority) : a.Seq.CompareTo(b.Seq);

    /// <summary>Start the drain loop if one is not already running. Exactly ONE loop exists at a time, which is what
    /// makes the queue the single place work waits — see <see cref="DrainAsync"/>.</summary>
    void Kick()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        _ = DrainAsync();
    }

    /// <summary>SLOT FIRST, then dequeue — the order is the whole bound.
    ///
    /// <para>Dequeuing first (and then parking on the semaphore holding the job) meant the queue drained into a pile of
    /// waiting loops the instant anything was enqueued: <c>Pending</c> read 0 while thousands of jobs were in flight,
    /// the capacity ceiling bounded a collection that was never allowed to fill, and the priority order was decided at
    /// ENQUEUE time — so a page open queued behind a prefetch wave still ran last. Taking the slot first leaves every
    /// waiting job in the heap, where the cap can see it and where the highest priority present at DISPATCH time wins.</para></summary>
    async Task DrainAsync()
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                lock (_gate) { if (_queue.Count == 0) return; }
                try { await _slots.WaitAsync(_token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }    // disposed / logged out while we waited for a slot

                Job job;
                lock (_gate)
                {
                    // `_running` is bumped under the SAME lock that removes the job, so a drainer can never observe
                    // "Pending 0, Running 0" while a job is in the handoff — the quiescence test every test harness uses.
                    if (!_queue.TryDequeue(out job, out _)) { _slots.Release(); return; }
                    Interlocked.Increment(ref _running);
                }

                _ = Task.Run(async () =>
                {
                    try { await job.Work(_token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log.Event(WaveeLogLevel.Warning, "hydration.pump.job", "background hydration job failed", ex: ex); }
                    finally { Interlocked.Decrement(ref _running); _slots.Release(); }
                }, _token);
            }
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
            // An Enqueue that raced this reset saw a loop already running and did not start one; re-check so its work
            // cannot sit in the queue with nobody left to dispatch it.
            bool more;
            lock (_gate) more = _queue.Count > 0;
            if (more && !_token.IsCancellationRequested) Kick();
        }
    }

    /// <summary>Stop the lane. CANCEL ONLY — the token source is deliberately never disposed.
    ///
    /// <para>Dispose does not (and cannot cheaply) join: a drain loop may be parked in <c>_slots.WaitAsync(token)</c>
    /// and a job may be mid-flight, and both touch the token after this returns. Disposing the source under them is the
    /// textbook "ObjectDisposedException from a background task nobody catches", which is exactly what a logout racing
    /// a prefetch used to produce here. Cancelling first makes every one of those touches safe — a cancelled token
    /// needs no registration — and the source is a handful of bytes whose linked registration dies with the session
    /// token that owns it. Idempotent.</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }   // the session source went first — already dead, nothing to do
        lock (_gate) _queue.Clear();
    }
}
