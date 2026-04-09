using System.Collections.Concurrent;

namespace Wavee.AudioHost.Audio.Streaming;

/// <summary>
/// Holds deferred CDN resolution tasks. The UI process sends head data first,
/// then sends CDN URL + audio key later. AudioHost's LazyProgressiveDownloader
/// awaits the deferred task to seamlessly continue from CDN.
/// </summary>
public sealed class DeferredResolutionRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeferredResult>> _pending = new();

    /// <summary>
    /// Creates a deferred resolution slot and returns the task to await.
    /// </summary>
    public Task<DeferredResult> CreateDeferred(string id)
    {
        var tcs = new TaskCompletionSource<DeferredResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        return tcs.Task;
    }

    /// <summary>
    /// Completes a deferred resolution with CDN URL + audio key.
    /// </summary>
    public bool Complete(string id, string cdnUrl, byte[] audioKey, long fileSize)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            tcs.SetResult(new DeferredResult(cdnUrl, audioKey, fileSize));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cancels a deferred resolution (e.g., track skipped before CDN resolved).
    /// </summary>
    public void Cancel(string id)
    {
        if (_pending.TryRemove(id, out var tcs))
            tcs.TrySetCanceled();
    }

    /// <summary>
    /// Cancels all pending resolutions.
    /// </summary>
    public void CancelAll()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }
    }
}

/// <summary>
/// Result of a deferred CDN resolution.
/// </summary>
public sealed record DeferredResult(string CdnUrl, byte[] AudioKey, long FileSize);
