using System.Collections.Concurrent;

namespace Wavee.AudioHost.Audio.Streaming;

/// <summary>
/// Holds deferred CDN resolution tasks. The UI process sends head data first,
/// then sends CDN URL + audio key later. AudioHost's LazyProgressiveDownloader
/// awaits the deferred task to seamlessly continue from CDN.
/// </summary>
/// <remarks>
/// Each entry has a server-side timeout: if the UI process never sends the
/// deferred result (crash, IPC stall, upstream audio-key timeout after the head
/// was already shipped), the awaiting downloader fails fast instead of waiting
/// forever. 60 s is sized to cover the worst-case audio-key retry budget
/// (~21 s) plus CDN URL resolution, with headroom for slow networks.
/// </remarks>
public sealed class DeferredResolutionRegistry
{
    /// <summary>
    /// Maximum time to wait for the UI process to send the deferred resolution.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _pending = new();

    /// <summary>
    /// Creates a deferred resolution slot and returns the task to await.
    /// The returned task will fault with <see cref="DeferredResolutionTimeoutException"/>
    /// if neither <see cref="Complete"/> nor <see cref="CompleteFromCache"/> is called
    /// within <paramref name="timeout"/> (default 60 s).
    /// </summary>
    public Task<DeferredResult> CreateDeferred(string id, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<DeferredResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var registration = cts.Token.Register(static state =>
        {
            var (tcs, id) = ((TaskCompletionSource<DeferredResult>, string))state!;
            tcs.TrySetException(new DeferredResolutionTimeoutException(id));
        }, (tcs, id));

        _pending[id] = new Entry(tcs, cts, registration);
        return tcs.Task;
    }

    /// <summary>
    /// Completes a deferred resolution with CDN URL + audio key.
    /// </summary>
    public bool Complete(string id, string cdnUrl, byte[] audioKey, long fileSize,
        string? spotifyFileId = null)
    {
        if (_pending.TryRemove(id, out var entry))
        {
            entry.Dispose();
            entry.Tcs.TrySetResult(new DeferredResult(cdnUrl, audioKey, fileSize, spotifyFileId, null));
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
        if (_pending.TryRemove(id, out var entry))
        {
            entry.Dispose();
            entry.Tcs.TrySetResult(new DeferredResult("", audioKey, fileSize, localCacheFileId, localCacheFileId));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cancels a deferred resolution (e.g., track skipped before CDN resolved).
    /// </summary>
    public void Cancel(string id)
    {
        if (_pending.TryRemove(id, out var entry))
        {
            entry.Dispose();
            entry.Tcs.TrySetCanceled();
        }
    }

    /// <summary>
    /// Cancels all pending resolutions.
    /// </summary>
    public void CancelAll()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var entry))
            {
                entry.Dispose();
                entry.Tcs.TrySetCanceled();
            }
        }
    }

    private sealed record Entry(
        TaskCompletionSource<DeferredResult> Tcs,
        CancellationTokenSource Cts,
        CancellationTokenRegistration Registration)
    {
        public void Dispose()
        {
            Registration.Dispose();
            Cts.Dispose();
        }
    }
}

/// <summary>
/// Thrown when the UI process fails to send the deferred CDN resolution
/// (audio key + URL) within the registry's timeout.
/// </summary>
public sealed class DeferredResolutionTimeoutException : Exception
{
    public string DeferredId { get; }

    public DeferredResolutionTimeoutException(string deferredId)
        : base($"Deferred CDN resolution for '{deferredId}' timed out")
    {
        DeferredId = deferredId;
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
