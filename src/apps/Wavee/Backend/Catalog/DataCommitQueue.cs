using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wavee.Backend.Catalog;

/// <summary>Prepared under the single owner. Publish runs only after Persist succeeds.</summary>
public sealed record DataCommit<T>(Func<CancellationToken, ValueTask> Persist, Action Publish, T Result);

/// <summary>The shared catalog/replica commit owner. Never put network work in a command.</summary>
public sealed class DataCommitQueue : IAsyncDisposable
{
    sealed class Execution(DataCommitQueue owner) { public DataCommitQueue Owner = owner; public bool Active = true; }
    static readonly AsyncLocal<Execution?> Current = new();
    public const int DefaultCapacity = 1024;
    public const int DefaultByteCapacity = 16 * 1024 * 1024;
    readonly Channel<IWork> _work;
    readonly Task _worker;
    readonly object _budgetGate = new();
    readonly object _publicationGate = new();
    List<Action> _notifications = new();
    int _publicationDepth;
    readonly int _byteCapacity;
    int _bytes;
    bool _closed;
    TaskCompletionSource _budgetChanged = NewSignal();

    public DataCommitQueue(int capacity = DefaultCapacity, int byteCapacity = DefaultByteCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCapacity);
        _byteCapacity = byteCapacity;
        _work = System.Threading.Channels.Channel.CreateBounded<IWork>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(DrainAsync);
    }

    // ── Always-on contention timing ─────────────────────────────────────────────────────────────────────────────
    // The publication gate is the one lock every query join, every catalog publication and every trim look share,
    // and the commit worker is the one thread every persistence round-trip runs on. A page that shows nothing for
    // three seconds with no network in flight is waiting on one of them; without these lines the gap is invisible.
    // Thresholds are frame-scale (a wait a frame would notice), the lines are rate-limited per kind so a stuck
    // second produces a handful of lines, not thousands.
    const double GateWaitSlowMs = 4, PublishHoldSlowMs = 8, CommitRunSlowMs = 8, CommitQueueSlowMs = 50;
    static readonly double TicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    long _lastGateLogTicks, _lastPublishLogTicks;
    long _gateWaitTicksTotal, _gateWaitSlowCount, _publishHoldTicksTotal, _publishSlowCount, _commitRunTicksTotal, _commitSlowCount, _commitCount;
    long _lastCommitTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Commands already admitted and waiting behind the running one.</summary>
    public int Pending => _work.Reader.Count;

    /// <summary>How long since a command last finished on the owner thread. Background maintenance reads this to
    /// stay out of a navigation's way without a UI hook: a page load is a burst of commands, and the burst having
    /// gone quiet is the same signal as "no navigation is being served right now".</summary>
    public TimeSpan OwnerIdleFor => TimeSpan.FromMilliseconds(
        (System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref _lastCommitTicks)) * TicksToMs);

    /// <summary>Cumulative contention counters for the memory/perf sampler: total publication-gate wait, how many
    /// waits crossed the slow line, total publication hold, total commit run time, commits run / slow.</summary>
    public (double GateWaitMs, long GateWaitSlow, double PublishHoldMs, long PublishSlow, long Commits, double CommitRunMs, long CommitSlow, int Pending) Contention
        => (Interlocked.Read(ref _gateWaitTicksTotal) * TicksToMs, Interlocked.Read(ref _gateWaitSlowCount),
            Interlocked.Read(ref _publishHoldTicksTotal) * TicksToMs, Interlocked.Read(ref _publishSlowCount),
            Interlocked.Read(ref _commitCount), Interlocked.Read(ref _commitRunTicksTotal) * TicksToMs,
            Interlocked.Read(ref _commitSlowCount), _work.Reader.Count);

    static bool RateLimited(ref long lastTicks, long now)
    {
        if (now - Volatile.Read(ref lastTicks) < System.Diagnostics.Stopwatch.Frequency) return true;
        Volatile.Write(ref lastTicks, now);
        return false;
    }

    /// <summary>Reads the current catalog/replica publication as one synchronous unit. The callback must perform no I/O.</summary>
    public T ReadConsistent<T>(Func<T> read, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_publicationGate)
        {
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            NoteGateWait(t0, t1, label);
            return read();
        }
    }

    void NoteGateWait(long t0, long t1, string label)
    {
        long wait = t1 - t0;
        Interlocked.Add(ref _gateWaitTicksTotal, wait);
        double waitMs = wait * TicksToMs;
        if (waitMs < GateWaitSlowMs) return;
        Interlocked.Increment(ref _gateWaitSlowCount);
        if (RateLimited(ref _lastGateLogTicks, t1)) return;
        WaveeLog.Instance.Event(WaveeLogLevel.Warning, "catalog", "catalog.gate.wait",
            "publication gate waited " + waitMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + " ms label=" + label + " pendingCommits=" + _work.Reader.Count
            + " slowWaits=" + Interlocked.Read(ref _gateWaitSlowCount));
    }

    /// <summary>Publishes already durable state atomically across its catalog and replica owners. Never await here.</summary>
    public void Publish(Action publish, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        List<Action>? notifications = null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (_publicationGate)
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                NoteGateWait(t0, t1, label);
                _publicationDepth++;
                try { publish(); }
                finally
                {
                    if (--_publicationDepth == 0) { notifications = _notifications; _notifications = new(); }
                    long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _publishHoldTicksTotal, t2 - t1);
                    double holdMs = (t2 - t1) * TicksToMs;
                    if (holdMs >= PublishHoldSlowMs)
                    {
                        Interlocked.Increment(ref _publishSlowCount);
                        if (!RateLimited(ref _lastPublishLogTicks, t2))
                            WaveeLog.Instance.Event(WaveeLogLevel.Warning, "catalog", "catalog.publish.slow",
                                "publication held the gate " + holdMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                + " ms label=" + label + " observers=" + (notifications?.Count ?? 0) + " depth=" + _publicationDepth);
                    }
                }
            }
        }
        finally
        {
            if (notifications is not null)
                foreach (var notify in notifications) NotifySafely(notify);
        }
    }

    /// <summary>Observers run after every owner state swap in the outer publication, and after releasing its read gate.</summary>
    public void NotifyAfterPublish(Action notify)
    {
        lock (_publicationGate)
        {
            if (_publicationDepth > 0) { _notifications.Add(notify); return; }
        }
        NotifySafely(notify);
    }

    static void NotifySafely(Action notify)
    {
        try { notify(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Data publication observer failed: {0}", error); }
    }

    public int PendingBytes { get { lock (_budgetGate) return _bytes; } }
    public bool IsExecuting => Current.Value is { Active: true } execution && ReferenceEquals(execution.Owner, this);

    public Task<T> CommitAsync<T>(Func<DataCommit<T>> prepare, CancellationToken callerCt = default,
        int encodedBytes = 0, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return CommitAsync(_ => ValueTask.FromResult(prepare()), callerCt, encodedBytes, label);
    }

    public Task<T> CommitAsync<T>(Func<CancellationToken, ValueTask<DataCommit<T>>> prepare,
        CancellationToken callerCt = default, int encodedBytes = 0, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return ExecuteAsync(async token =>
        {
            var commit = await prepare(token).ConfigureAwait(false);
            await commit.Persist(token).ConfigureAwait(false);
            Publish(commit.Publish, label);
            return commit.Result;
        }, callerCt, encodedBytes, label);
    }

    /// <param name="encodedBytes">Bytes retained by this command. Payload-owning callers must reserve their encoded size.</param>
    /// <param name="label">The caller's name (compiler-supplied): what a slow commit is attributed to.</param>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken callerCt = default, int encodedBytes = 0, [System.Runtime.CompilerServices.CallerMemberName] string label = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (encodedBytes < 0 || encodedBytes > _byteCapacity)
            throw new ArgumentOutOfRangeException(nameof(encodedBytes));
        callerCt.ThrowIfCancellationRequested();
        await ReserveAsync(encodedBytes, callerCt).ConfigureAwait(false);
        var work = new Work<T>(this, operation, encodedBytes, label);
        try { await _work.Writer.WriteAsync(work, callerCt).ConfigureAwait(false); }
        catch { Release(encodedBytes); throw; }
        // An admitted transaction belongs to the database owner. Navigation cancels only this await.
        return await work.Completion.Task.WaitAsync(callerCt).ConfigureAwait(false);
    }

    /// <summary>One <c>commit.slow</c> line for a command that ran ≥ 8 ms on the owner thread or waited ≥ 50 ms in
    /// the queue behind others — the label names the caller, the queue depth says who it waited behind.</summary>
    void NoteCommit(string label, long queuedTicks, long startedTicks, long finishedTicks, int bytes, bool failed)
    {
        Interlocked.Increment(ref _commitCount);
        Interlocked.Add(ref _commitRunTicksTotal, finishedTicks - startedTicks);
        Volatile.Write(ref _lastCommitTicks, finishedTicks);
        double runMs = (finishedTicks - startedTicks) * TicksToMs, queueMs = (startedTicks - queuedTicks) * TicksToMs;
        if (runMs < CommitRunSlowMs && queueMs < CommitQueueSlowMs) return;
        Interlocked.Increment(ref _commitSlowCount);
        WaveeLog.Instance.Event(WaveeLogLevel.Warning, "catalog", "commit.slow",
            "commit label=" + label + " runMs=" + runMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + " queueMs=" + queueMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + " bytes=" + bytes + " pendingAfter=" + _work.Reader.Count + (failed ? " failed=1" : ""));
    }

    public async Task FlushAsync(CancellationToken ct = default)
        => _ = await ExecuteAsync(static _ => ValueTask.FromResult(true), ct).ConfigureAwait(false);

    async ValueTask ReserveAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_budgetGate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_bytes <= _byteCapacity - bytes) { _bytes += bytes; return; }
                changed = _budgetChanged.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    void Release(int bytes)
    {
        TaskCompletionSource changed;
        lock (_budgetGate)
        {
            _bytes -= bytes;
            changed = _budgetChanged;
            _budgetChanged = NewSignal();
        }
        changed.TrySetResult();
    }

    async Task DrainAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var execution = new Execution(this);
            Current.Value = execution;
            try { await work.RunAsync().ConfigureAwait(false); }
            finally { execution.Active = false; Current.Value = null; Release(work.Bytes); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource changed;
        lock (_budgetGate) { _closed = true; changed = _budgetChanged; }
        changed.TrySetResult();
        _work.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    interface IWork { int Bytes { get; } ValueTask RunAsync(); }
    sealed class Work<T>(DataCommitQueue owner, Func<CancellationToken, ValueTask<T>> operation, int bytes, string label) : IWork
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Bytes => bytes;
        readonly long _queuedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        public async ValueTask RunAsync()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool failed = false;
            try { Completion.TrySetResult(await operation(CancellationToken.None).ConfigureAwait(false)); }
            catch (Exception ex) { failed = true; Completion.TrySetException(ex); }
            finally { owner.NoteCommit(label, _queuedTicks, started, System.Diagnostics.Stopwatch.GetTimestamp(), bytes, failed); }
        }
    }
}
