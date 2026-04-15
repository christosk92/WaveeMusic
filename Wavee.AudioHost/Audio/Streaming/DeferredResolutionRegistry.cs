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
    public bool Complete(string id, string cdnUrl, byte[] audioKey, long fileSize,
        string? spotifyFileId = null)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            tcs.SetResult(new DeferredResult(cdnUrl, audioKey, fileSize, spotifyFileId, null));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Completes a deferred resolution using a locally cached file instead of CDN.
    /// AudioHost will read from <c>$cacheDir/audio/$localCacheFileId.enc</c>.
    /// </summary>
    public bool CompleteFromCache(string id, byte[] audioKey, long fileSize, string localCacheFileId)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            tcs.SetResult(new DeferredResult("", audioKey, fileSize, localCacheFileId, localCacheFileId));
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
/// <param name="CdnUrl">CDN URL; empty string when reading from local cache.</param>
/// <param name="AudioKey">Decryption key (always required).</param>
/// <param name="FileSize">Total encrypted file size in bytes.</param>
/// <param name="SpotifyFileId">Spotify file ID (40-char hex) for persistent caching. Null if not provided.</param>
/// <param name="LocalCacheFileId">When non-null, read from <c>$cacheDir/audio/$LocalCacheFileId.enc</c> instead of CDN.</param>
public sealed record DeferredResult(
    string CdnUrl,
    byte[] AudioKey,
    long FileSize,
    string? SpotifyFileId,
    string? LocalCacheFileId);
