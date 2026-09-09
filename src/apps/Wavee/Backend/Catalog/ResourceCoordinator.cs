using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Finite shared work, two transports and one deadline timer. Providers only return observations.</summary>
public sealed class ResourceCoordinator : IResourceCoordinator, IAsyncDisposable
{
    public const int MaxInFlight = 4;
    public const int QueueCapacity = 4096;
    public const int MaximumBatchUris = 300;
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    readonly CatalogRepository _repository;
    readonly IReadOnlyDictionary<string, ICatalogResourceProvider> _providers;
    readonly TimeProvider _time;
    readonly ProviderExecutionGate? _execution;
    readonly object _gate = new();
    readonly Dictionary<ResourceKey, Job> _jobs = new();
    readonly SemaphoreSlim _admission = new(1, 1);
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly SemaphoreSlim _slots = new(MaxInFlight, MaxInFlight);
    readonly List<Task> _inFlight = [];
    readonly CancellationTokenSource _shutdown = new();
    readonly ITimer _timer;
    readonly Task[] _workers;
    int _inFlightCount;
    TaskCompletionSource _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _sequence;
    bool _disposed;

    sealed class Job(ResourceRequest request, long sequence)
    {
        public ResourceRequest Request = request;
        public readonly long Sequence = sequence;
        public readonly TaskCompletionSource<ResourceEnsureResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ResourcePriority Priority = request.Priority;
        public int Attempts, Waiters;
        public bool Running;
        public DateTimeOffset DueAt;
    }

    public ResourceCoordinator(CatalogRepository repository, IEnumerable<ICatalogResourceProvider> providers,
        TimeProvider time, ProviderExecutionGate? execution = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _execution = execution;
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Provider, StringComparer.Ordinal);
        _timer = time.CreateTimer(_ => OnDeadline(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _workers = [Task.Run(WorkerAsync)];
    }

    public int Pending { get { lock (_gate) return _jobs.Values.Count(job => !job.Running); } }
    /// <summary>Jobs known (queued + running) and provider batches in flight — the sampler's view of fetch pressure.</summary>
    public (int Jobs, int InFlight) Load { get { lock (_gate) return (_jobs.Count, _inFlightCount); } }
    public int Running { get { lock (_gate) return _jobs.Values.Count(job => job.Running); } }

    public async Task<IReadOnlyList<ResourceEnsureResult>> EnsureAsync(IReadOnlyList<ResourceKey> keys,
        ResourcePriority priority = ResourcePriority.Visible, bool force = false, CancellationToken ct = default,
        Func<ResourceKey, CancellationToken>? waiterCancellation = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (_execution is null) return await EnsureCoreAsync(keys, priority, force, ct, waiterCancellation).ConfigureAwait(false);
        // Native/local work starts independently; a pending Spotify login must not hold its admission lock.
        var gated = keys.Where(NeedsProtocol).ToArray();
        var local = keys.Where(key => !NeedsProtocol(key)).ToArray();
        var localRead = EnsureCoreAsync(local, priority, force, ct, waiterCancellation);
        var remoteRead = EnsureProtocolAsync(gated);
        await Task.WhenAll(localRead, remoteRead).ConfigureAwait(false);
        var results = localRead.Result.Concat(remoteRead.Result).DistinctBy(result => result.Key).ToDictionary(result => result.Key);
        return keys.Select(key => results[key]).ToArray();

        bool NeedsProtocol(ResourceKey key) => key.Scope.Provider == "spotify"
            && _providers.TryGetValue(key.Scope.Provider, out var provider) && provider.RequiresNetwork(key);
        async Task<IReadOnlyList<ResourceEnsureResult>> EnsureProtocolAsync(ResourceKey[] remote)
        {
            if (remote.Length == 0) return [];
            long epoch = _repository.Epoch;
            // Warm persistence even while transport is initializing. This publication may reveal cached page data.
            await _repository.ReadManyAsync(remote, ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            if (!await _execution.WaitAsync(epoch, linked.Token).ConfigureAwait(false))
            {
                // A same-account confirm bumps the epoch (protocol fencing) without going offline. The wait
                // captured the previous epoch and would otherwise return Deferred/Offline and re-paint the
                // cached sidebar. Follow the live epoch once; a real offline transition still fails.
                long current = _repository.Epoch;
                if (current == epoch || !_repository.IsOnline
                    || !await _execution.WaitAsync(current, linked.Token).ConfigureAwait(false))
                    return remote.Select(key => new ResourceEnsureResult(key, ResourceEnsureStatus.Deferred,
                        _repository.Peek(key) with { Activity = ResourceActivity.Offline })).ToArray();
            }
            return await EnsureCoreAsync(remote, priority, force, ct, waiterCancellation).ConfigureAwait(false);
        }
    }

    async Task<IReadOnlyList<ResourceEnsureResult>> EnsureCoreAsync(IReadOnlyList<ResourceKey> keys,
        ResourcePriority priority, bool force, CancellationToken ct,
        Func<ResourceKey, CancellationToken>? waiterCancellation)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return Array.Empty<ResourceEnsureResult>();
        var tasks = new Dictionary<ResourceKey, Task<ResourceEnsureResult>>();
        var resets = new List<Job>();
        // Finding #11: only the in-memory `_jobs` decide-and-admit step below needs `_admission` — these two
        // persistence round trips are pure reads (a fresh entry, not yet touching `_jobs`), so a caller with a
        // slow cold read no longer holds every OTHER disjoint-key caller off the coordinator while it runs.
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var distinct = keys.Distinct().ToArray();
        if (force) await _repository.InvalidateAsync(distinct, ct).ConfigureAwait(false);
        var snapshots = await _repository.ReadManyAsync(distinct, ct).ConfigureAwait(false);
        var admittedRequests = new List<ResourceRequest>();
        // The outer try/finally guarantees a wake-up even if the decide/capture phase below throws (e.g. a
        // cancelled capture) or the tail notifications do — an admitted job must never sit un-woken. The inner
        // try/finally is the narrowed piece: `_admission` covers only the `_jobs` decide-and-admit step, and is
        // released before the two persistence notifications that follow (which need no `_jobs` exclusivity).
        try
        {
            await _admission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
                var captures = new List<(ResourceKey Key, bool Network)>();
                for (int i = 0; i < distinct.Length; i++)
                {
                    var key = distinct[i];
                    var snapshot = snapshots[i];
                    var waiterToken = waiterCancellation?.Invoke(key) ?? ct;
                    if (waiterToken.IsCancellationRequested)
                    {
                        tasks[key] = Task.FromResult(new ResourceEnsureResult(key, ResourceEnsureStatus.Deferred, snapshot));
                        continue;
                    }
                    lock (_gate)
                    {
                        if (_jobs.TryGetValue(key, out var existing))
                        {
                            if (existing.Request.Stamp.Epoch == _repository.Epoch
                                && existing.Request.Stamp.Generation == snapshot.Generation)
                            {
                                if (priority > existing.Priority) existing.Priority = priority;
                                existing.Waiters++;
                                tasks[key] = AwaitJobAsync(existing, ct, waiterToken);
                                continue;
                            }
                            CompleteLocked(existing, ResourceEnsureStatus.Superseded, resets);
                        }
                    }
                    var terminal = snapshot.Knowledge == Knowledge.Unsupported && !force ? ResourceEnsureStatus.Unsupported
                        : snapshot.Error is not null && !force ? ResourceEnsureStatus.Failed
                        : snapshot.IsFresh(_time.GetUtcNow())
                            ? snapshot.Knowledge == Knowledge.Absent ? ResourceEnsureStatus.Absent : ResourceEnsureStatus.Ready
                            : (ResourceEnsureStatus?)null;
                    if (terminal is { } ready)
                    {
                        tasks[key] = Task.FromResult(new ResourceEnsureResult(key, ready, snapshot));
                        continue;
                    }
                    bool network = _providers.TryGetValue(key.Scope.Provider, out var provider) && provider.RequiresNetwork(key);
                    if (!_repository.CanRequest(key, network))
                    {
                        tasks[key] = Task.FromResult(new ResourceEnsureResult(key, ResourceEnsureStatus.Deferred,
                            snapshot with { Activity = ResourceActivity.Offline }));
                        continue;
                    }
                    captures.Add((key, network));
                }
                // The two network classes are captured together; no per-row task or transport fanout.
                foreach (var group in captures.GroupBy(item => item.Network))
                {
                    var requests = await _repository.CaptureRequestsAsync(group.Select(item => item.Key).ToArray(),
                        priority, requiresNetwork: group.Key, ct: ct).ConfigureAwait(false);
                    foreach (var request in requests)
                    {
                        var waiterToken = waiterCancellation?.Invoke(request.Key) ?? ct;
                        Job? admitted;
                        lock (_gate)
                        {
                            admitted = waiterToken.IsCancellationRequested ? null : AdmitLocked(request, resets);
                            if (admitted is not null)
                            {
                                admitted.Waiters++;
                                tasks[request.Key] = AwaitJobAsync(admitted, ct, waiterToken);
                            }
                            else tasks[request.Key] = Task.FromResult(new ResourceEnsureResult(request.Key,
                                ResourceEnsureStatus.Deferred, _repository.Peek(request.Key)));
                        }
                        if (admitted is not null) admittedRequests.Add(request);
                    }
                }
            }
            finally { _admission.Release(); }
            // Neither of these needs `_jobs` exclusivity — only notifying persistence of what the decide/capture
            // step above already committed to `_jobs`.
            if (admittedRequests.Count > 0)
                await _repository.SetActivitiesAsync(admittedRequests, ResourceActivity.Queued, ct).ConfigureAwait(false);
            // A key superseded here (a newer stamp replaced its job, or the admission queue evicted a lower-
            // priority one) leaves the entry mid-flight (Queued/Fetching) with no job behind it unless cleared.
            await ResetAbandonedActivityAsync(resets).ConfigureAwait(false);
        }
        finally { lock (_gate) WakeLocked(); }
        var results = await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        var byKey = results.ToDictionary(result => result.Key);
        return keys.Select(key => byKey[key]).ToArray();
    }

    public async Task InvalidateAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct = default)
    {
        await _repository.InvalidateAsync(keys, ct).ConfigureAwait(false);
        var resets = new List<Job>();
        lock (_gate)
        {
            foreach (var key in keys)
                if (_jobs.TryGetValue(key, out var job)) CompleteLocked(job, ResourceEnsureStatus.Superseded, resets);
            ArmTimerLocked();
        }
        await ResetAbandonedActivityAsync(resets).ConfigureAwait(false);
    }

    Job? AdmitLocked(ResourceRequest request, List<Job> resets)
    {
        if (_disposed) return null;
        if (_jobs.Values.Count(job => !job.Running) >= QueueCapacity)
        {
            var victim = _jobs.Values.Where(job => !job.Running && job.Priority < request.Priority)
                .OrderBy(job => job.Priority).ThenByDescending(job => job.Sequence).FirstOrDefault();
            if (victim is null) return null;
            CompleteLocked(victim, ResourceEnsureStatus.Deferred, resets);
        }
        var admitted = new Job(request, ++_sequence);
        _jobs[request.Key] = admitted;
        return admitted;
    }

    async Task<ResourceEnsureResult> AwaitJobAsync(Job job, CancellationToken ct, CancellationToken waiterToken)
    {
        try { return await job.Completion.Task.WaitAsync(waiterToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waiterToken.IsCancellationRequested && !ct.IsCancellationRequested)
        { return new(job.Request.Key, ResourceEnsureStatus.Deferred, _repository.Peek(job.Request.Key)); }
        finally
        {
            bool clearActivity = false;
            lock (_gate)
            {
                job.Waiters--;
                // An in-flight read may finish for the cache. Parked views do not own later retries.
                if (job.Waiters == 0 && !job.Running && !job.Completion.Task.IsCompleted)
                {
                    CompleteLocked(job, ResourceEnsureStatus.Deferred);
                    clearActivity = true;
                }
                ArmTimerLocked();
            }
            if (clearActivity)
                await _repository.SetActivityAsync(job.Request.Key, job.Request.Stamp, ResourceActivity.Idle,
                    _repository.Peek(job.Request.Key).Error).ConfigureAwait(false);
        }
    }

    async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Job[] batch;
                var resets = new List<Job>();
                lock (_gate)
                {
                    batch = TakeBatchLocked(resets);
                    ArmTimerLocked();
                    if (_jobs.Values.Any(job => !job.Running && job.DueAt <= _time.GetUtcNow())) WakeLocked();
                }
                // A stale-generation job (TakeBatchLocked below) completes as Superseded with no job left behind
                // it; clear its activity the way AwaitJobAsync's finally does, or the entry sticks at Queued/Fetching.
                await ResetAbandonedActivityAsync(resets).ConfigureAwait(false);
                if (batch.Length == 0)
                {
                    _slots.Release();
                    continue;
                }
                lock (_gate) _inFlightCount++;
                var task = RunBatchAsync(batch);
                lock (_gate) _inFlight.Add(task);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    async Task RunBatchAsync(Job[] batch)
    {
        try { await FetchBatchAsync(batch).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _slots.Release();
            lock (_gate)
            {
                for (int i = _inFlight.Count - 1; i >= 0; i--)
                    if (_inFlight[i].IsCompleted) _inFlight.RemoveAt(i);
                _inFlightCount = Math.Max(0, _inFlightCount - 1);
                if (_jobs.Values.Any(job => !job.Running && job.DueAt <= _time.GetUtcNow())) WakeLocked();
            }
        }
    }

    Job[] TakeBatchLocked(List<Job> resets)
    {
        foreach (var stale in _jobs.Values.Where(job => !job.Running && !_repository.IsCurrent(job.Request)).ToArray())
            CompleteLocked(stale, ResourceEnsureStatus.Superseded, resets);
        var ready = _jobs.Values.Where(job => !job.Running && job.DueAt <= _time.GetUtcNow())
            .OrderByDescending(job => job.Priority).ThenBy(job => job.Sequence).ToArray();
        if (ready.Length == 0) return Array.Empty<Job>();
        var first = ready[0];
        _providers.TryGetValue(first.Request.Key.Scope.Provider, out var provider);
        string batchGroup = provider?.BatchGroup(first.Request.Key) ?? "unsupported";
        var uris = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<Job>();
        foreach (var job in ready)
        {
            if (job.Request.Key.Scope != first.Request.Key.Scope) continue;
            if ((provider?.BatchGroup(job.Request.Key) ?? "unsupported") != batchGroup) continue;
            if (!uris.Contains(job.Request.Key.Subject) && uris.Count == MaximumBatchUris) continue;
            uris.Add(job.Request.Key.Subject);
            job.Running = true; job.Attempts++;
            job.Request = job.Request with { Priority = job.Priority };
            picked.Add(job);
        }
        return picked.ToArray();
    }

    async Task FetchBatchAsync(Job[] batch)
    {
        var requests = batch.Select(job => job.Request).ToArray();
        IReadOnlyList<ResourceResponse> responses;
        long fetchStart = Stopwatch.GetTimestamp();
        try
        {
            await _repository.SetActivitiesAsync(requests, ResourceActivity.Fetching, _shutdown.Token).ConfigureAwait(false);
            if (_providers.TryGetValue(requests[0].Key.Scope.Provider, out var provider))
            {
                using var timeout = new CancellationTokenSource(RequestTimeout, _time);
                using var transport = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _shutdown.Token);
                responses = await provider.FetchAsync(requests, transport.Token).AsTask()
                    .WaitAsync(RequestTimeout, _time, _shutdown.Token).ConfigureAwait(false);
            }
            else responses = requests.Select(request => new ResourceResponse(request,
                new ResourceFetchResult(ResourceFetchStatus.Unsupported))).ToArray();
            responses = MatchResponses(requests, responses);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            var kind = error is ArgumentException or System.Text.Json.JsonException
                ? ResourceErrorKind.Decode : ResourceErrorKind.Transport;
            responses = requests.Select(request => new ResourceResponse(request,
                ResourceFetchResult.Failed(new ResourceError(kind, error.Message)))).ToArray();
        }
        long fetchMs = (long)Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds;

        long acceptStart = Stopwatch.GetTimestamp();
        IReadOnlyList<ResourceEnsureResult> accepted;
        try { accepted = await _repository.AcceptAsync(responses, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            var problem = new ResourceError(error is ArgumentException or System.Text.Json.JsonException
                ? ResourceErrorKind.InvalidResponse : ResourceErrorKind.Persistence, error.Message);
            foreach (var request in requests)
                await _repository.SetActivityAsync(request.Key, request.Stamp, ResourceActivity.Idle, problem)
                    .ConfigureAwait(false);
            accepted = requests.Select(request => new ResourceEnsureResult(request.Key,
                ResourceEnsureStatus.Failed, _repository.Peek(request.Key))).ToArray();
        }
        long acceptMs = (long)Stopwatch.GetElapsedTime(acceptStart).TotalMilliseconds;
        LogBatch(requests, accepted, fetchMs, acceptMs);

        for (int i = 0; i < batch.Length; i++)
        {
            var job = batch[i];
            var result = accepted[i];
            var delay = result.Status == ResourceEnsureStatus.Failed
                ? ResourcePolicy.RetryDelay(result.Snapshot.Error, job.Attempts) : null;
            DateTimeOffset? retryAt = null;
            lock (_gate)
                if (!_disposed && IsCurrentLocked(job) && job.Waiters > 0 && delay is { } retry)
                    retryAt = _time.GetUtcNow() + retry;
            if (retryAt is { } at)
                await _repository.SetActivityAsync(job.Request.Key, job.Request.Stamp, ResourceActivity.Backoff,
                    result.Snapshot.Error, at).ConfigureAwait(false);
            bool clearAbandonedBackoff = false;
            lock (_gate)
            {
                job.Running = false;
                if (!IsCurrentLocked(job)) continue;
                if (!_disposed && job.Waiters > 0 && retryAt is { } deadline)
                {
                    job.DueAt = deadline;
                }
                else
                {
                    _jobs.Remove(job.Request.Key);
                    SignalCapacityLocked();
                    job.Completion.TrySetResult(result);
                    clearAbandonedBackoff = retryAt is not null;
                }
                ArmTimerLocked();
                WakeLocked();
            }
            if (clearAbandonedBackoff)
                await _repository.SetActivityAsync(job.Request.Key, job.Request.Stamp, ResourceActivity.Idle,
                    result.Snapshot.Error).ConfigureAwait(false);
        }
    }

    // Always-on: the one line that shows what a batch actually cost (an album open, a sidebar rootlist wave, …).
    void LogBatch(ResourceRequest[] requests, IReadOnlyList<ResourceEnsureResult> accepted, long fetchMs, long acceptMs)
    {
        _providers.TryGetValue(requests[0].Key.Scope.Provider, out var provider);
        string providerName = requests[0].Key.Scope.Provider;
        string group = provider?.BatchGroup(requests[0].Key) ?? "unsupported";
        var firstArgs = requests[0].Key.Arguments;
        string facet = requests[0].Key.Facet.ToString();
        // Cheap even for a big batch (one key's fields): what makes a single-key batch diagnosable in the log.
        string args = firstArgs.Offset.ToString(CultureInfo.InvariantCulture) + "/"
            + firstArgs.Limit.ToString(CultureInfo.InvariantCulture) + "/" + (firstArgs.Filter ?? "");
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests) subjects.Add(request.Key.Subject);
        int ready = 0, absent = 0, failed = 0, superseded = 0;
        string? error = null;
        foreach (var result in accepted)
        {
            switch (result.Status)
            {
                case ResourceEnsureStatus.Ready: ready++; break;
                case ResourceEnsureStatus.Absent: absent++; break;
                case ResourceEnsureStatus.Failed: failed++; error ??= result.Snapshot.Error?.Message; break;
                case ResourceEnsureStatus.Superseded: superseded++; break;
            }
        }
        int inFlight;
        lock (_gate) inFlight = _inFlightCount;
        string message = "fetch batch provider=" + providerName + " group=" + group
            + " keys=" + requests.Length.ToString(CultureInfo.InvariantCulture)
            + " subjects=" + subjects.Count.ToString(CultureInfo.InvariantCulture)
            + " inFlight=" + inFlight.ToString(CultureInfo.InvariantCulture)
            + " first=" + requests[0].Key.Subject
            + " facet=" + facet + " args=" + args
            + " fetchMs=" + fetchMs.ToString(CultureInfo.InvariantCulture)
            + " acceptMs=" + acceptMs.ToString(CultureInfo.InvariantCulture)
            + " ready=" + ready.ToString(CultureInfo.InvariantCulture)
            + " absent=" + absent.ToString(CultureInfo.InvariantCulture)
            + " failed=" + failed.ToString(CultureInfo.InvariantCulture);
        WaveeLog.Instance.Event(failed == 0 ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "catalog", "catalog.fetch.batch",
            message, elapsedMs: fetchMs + acceptMs,
            fields:
            [
                WaveeLogField.Of("provider", providerName),
                WaveeLogField.Of("group", group),
                WaveeLogField.Of("keys", requests.Length),
                WaveeLogField.Of("subjects", subjects.Count),
                WaveeLogField.Of("inFlight", inFlight),
                WaveeLogField.Of("first", requests[0].Key.Subject),
                WaveeLogField.Of("facet", facet),
                WaveeLogField.Of("args", args),
                WaveeLogField.Of("fetchMs", fetchMs),
                WaveeLogField.Of("acceptMs", acceptMs),
                WaveeLogField.Of("ready", ready),
                WaveeLogField.Of("absent", absent),
                WaveeLogField.Of("failed", failed),
                WaveeLogField.Of("superseded", superseded),
                WaveeLogField.Of("error", error ?? ""),
            ]);
    }

    static IReadOnlyList<ResourceResponse> MatchResponses(ResourceRequest[] requests, IReadOnlyList<ResourceResponse> responses)
    {
        var byId = new Dictionary<long, ResourceResponse>();
        foreach (var response in responses)
            if (!byId.TryAdd(response.Request.Stamp.RequestId, response))
                throw new ArgumentException("Provider returned a duplicate response.");
        var matched = new ResourceResponse[requests.Length];
        for (int i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            matched[i] = byId.TryGetValue(request.Stamp.RequestId, out var response) && response.Request == request
                ? response : new ResourceResponse(request, ResourceFetchResult.Failed(
                    new ResourceError(ResourceErrorKind.InvalidResponse, "Provider omitted the requested resource.")));
        }
        return matched;
    }

    bool IsCurrentLocked(Job job) => _jobs.TryGetValue(job.Request.Key, out var current) && ReferenceEquals(job, current);
    public Task WaitForCapacityAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _jobs.Values.Count(job => !job.Running) < QueueCapacity
                ? Task.CompletedTask : _capacityChanged.Task.WaitAsync(ct);
        }
    }
    void SignalCapacityLocked()
    {
        var previous = _capacityChanged;
        _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
    // Completing a job as Superseded/Deferred does not, by itself, clear the entry's Queued/Fetching activity —
    // there is no job left behind to ever set it back to Idle. `resets` collects the jobs that actually
    // transitioned here (a no-op TrySetResult means something else already completed it, and already reset it);
    // the caller drains them via ResetAbandonedActivityAsync once it has released `_gate` (this runs under it).
    void CompleteLocked(Job job, ResourceEnsureStatus status, List<Job>? resets = null)
    {
        if (IsCurrentLocked(job)) _jobs.Remove(job.Request.Key);
        SignalCapacityLocked();
        if (job.Completion.TrySetResult(new ResourceEnsureResult(job.Request.Key, status, _repository.Peek(job.Request.Key)))
            && resets is not null)
            resets.Add(job);
    }
    // A job superseded by a STALE stamp (its own generation/requestId can never Matches() again — a sibling
    // caller raced the coordinator's own bookkeeping, or an admission evicted it) cannot be reset via
    // SetActivityAsync: the very mismatch that made it stale is what SetActivityAsync's own guard rejects.
    // ClearAbandonedActivityAsync resets purely by key instead; the "still absent from _jobs" recheck here is
    // what stops it from clobbering a genuinely newer job admitted for the same key in the interim.
    async Task ResetAbandonedActivityAsync(List<Job> resets)
    {
        foreach (var reset in resets)
        {
            bool stillAbandoned;
            lock (_gate) stillAbandoned = !_jobs.ContainsKey(reset.Request.Key);
            if (stillAbandoned) await _repository.ClearAbandonedActivityAsync(reset.Request.Key).ConfigureAwait(false);
        }
    }
    void WakeLocked()
    {
        if (!_disposed && _wake.CurrentCount == 0) _wake.Release();
    }
    void OnDeadline() { lock (_gate) WakeLocked(); }
    void ArmTimerLocked()
    {
        if (_disposed) return;
        var deadlines = _jobs.Values.Where(job => !job.Running && job.DueAt > _time.GetUtcNow()).Select(job => job.DueAt);
        var next = deadlines.Any() ? deadlines.Min() - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
        _timer.Change(next < TimeSpan.Zero && next != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : next, Timeout.InfiniteTimeSpan);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _jobs.Values.ToArray()) CompleteLocked(job, ResourceEnsureStatus.Deferred);
        }
        _shutdown.Cancel();
        await _timer.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        Task[] inflight;
        lock (_gate) inflight = _inFlight.ToArray();
        if (inflight.Length > 0) await Task.WhenAll(inflight).ConfigureAwait(false);
        _shutdown.Dispose();
        _slots.Dispose();
    }
}
