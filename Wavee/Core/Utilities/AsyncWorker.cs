using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Utilities;

/// <summary>
/// Generic async worker that processes work items in the background using a Channel.
/// Provides exception isolation and non-blocking work submission.
/// Similar to Librespot's AsyncWorker pattern.
/// </summary>
/// <typeparam name="T">Type of work items to process.</typeparam>
public sealed class AsyncWorker<T> : IAsyncDisposable
{
    private readonly string _name;
    private readonly ILogger? _logger;
    private readonly Channel<T> _queue;
    private readonly Func<T, ValueTask> _worker;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;
    private bool _disposed;

    /// <summary>
    /// Gets the current number of pending work items in the queue.
    /// </summary>
    public int PendingCount => _queue.Reader.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncWorker{T}"/> class.
    /// </summary>
    /// <param name="name">Name for logging purposes.</param>
    /// <param name="worker">Async work handler that processes each item.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="capacity">Maximum queue capacity (null for unbounded). Default is unbounded.</param>
    public AsyncWorker(
        string name,
        Func<T, ValueTask> worker,
        ILogger? logger = null,
        int? capacity = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _logger = logger;

        // Create channel with optional bounded capacity
        _queue = capacity.HasValue
            ? Channel.CreateBounded<T>(new BoundedChannelOptions(capacity.Value)
            {
                FullMode = BoundedChannelFullMode.Wait // Block submission when full
            })
            : Channel.CreateUnbounded<T>();

        _cts = new CancellationTokenSource();
        _workerTask = RunWorkerLoopAsync(_cts.Token);

        _logger?.LogDebug("AsyncWorker '{Name}' started (capacity: {Capacity})",
            _name, capacity?.ToString() ?? "unbounded");
    }

    /// <summary>
    /// Submits a work item to the queue for processing.
    /// Returns immediately without blocking (unless queue is full).
    /// </summary>
    /// <param name="item">Work item to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask SubmitAsync(T item, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AsyncWorker<T>));

        await _queue.Writer.WriteAsync(item, cancellationToken);
        _logger?.LogTrace("Work item submitted to AsyncWorker '{Name}' (pending: {Pending})",
            _name, _queue.Reader.Count);
    }

    /// <summary>
    /// Attempts to submit a work item without waiting.
    /// Returns true if successful, false if queue is full.
    /// </summary>
    /// <param name="item">Work item to process.</param>
    public bool TrySubmit(T item)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AsyncWorker<T>));

        return _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Main worker loop that processes items from the queue.
    /// </summary>
    private async Task RunWorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _worker(item);
                }
                catch (Exception ex)
                {
                    // Isolate exceptions per work item - don't let one failure break the worker
                    _logger?.LogError(ex, "AsyncWorker '{Name}' failed to process work item", _name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("AsyncWorker '{Name}' cancelled", _name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in AsyncWorker '{Name}' loop", _name);
        }
    }

    /// <summary>
    /// Completes the worker (no more work items will be accepted).
    /// Waits for all pending items to be processed.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        if (_disposed)
            return;

        _logger?.LogDebug("Completing AsyncWorker '{Name}' (pending: {Pending})", _name, _queue.Reader.Count);

        // Mark channel as complete (no more writes)
        _queue.Writer.Complete();

        // Wait for worker to finish processing all items
        try
        {
            await _workerTask;
        }
        catch (OperationCanceledException)
        {
            // Expected if cancelled
        }

        _logger?.LogDebug("AsyncWorker '{Name}' completed", _name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger?.LogDebug("Disposing AsyncWorker '{Name}'", _name);

        // Cancel and complete channel
        _cts.Cancel();
        _queue.Writer.Complete();

        // Wait for worker task to finish
        try
        {
            await _workerTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cts.Dispose();

        _logger?.LogDebug("AsyncWorker '{Name}' disposed", _name);
    }
}
