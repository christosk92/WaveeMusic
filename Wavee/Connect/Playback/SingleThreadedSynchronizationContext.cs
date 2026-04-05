using System.Collections.Concurrent;

namespace Wavee.Connect.Playback;

/// <summary>
/// A SynchronizationContext that runs all continuations on a single dedicated thread.
/// Used by the audio playback loop to keep all await continuations off the thread pool,
/// preventing UI thread pool starvation from causing audio underflows.
/// </summary>
internal sealed class SingleThreadedSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _threadId;

    public SingleThreadedSynchronizationContext()
    {
        _threadId = Environment.CurrentManagedThreadId;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            d(state);
        }
        else
        {
            using var done = new ManualResetEventSlim();
            _queue.Add((s => { d(s); done.Set(); }, state));
            done.Wait();
        }
    }

    /// <summary>
    /// Pumps the message queue on the current thread until the given task completes.
    /// All await continuations posted via <see cref="Post"/> are executed here.
    /// </summary>
    public void RunUntilComplete(Task task)
    {
        task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);

        foreach (var (cb, state) in _queue.GetConsumingEnumerable())
        {
            cb(state);
        }

        task.GetAwaiter().GetResult();
    }
}
