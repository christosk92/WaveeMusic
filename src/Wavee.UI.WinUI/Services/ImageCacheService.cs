using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// O(1) LRU cache of <see cref="CachedImage"/> instances. Each entry owns a
/// <see cref="LoadedImageSurface"/> — the decoded pixels live in GPU memory,
/// not on the managed heap. Prevents duplicate downloads and GC of frequently
/// used images. Thread-safe via <see cref="ReaderWriterLockSlim"/>.
/// </summary>
public sealed class ImageCacheService
{
    private readonly record struct CacheKey(string Uri, int DecodeSize);

    private sealed class CacheEntry
    {
        public required CachedImage Image { get; init; }
        public long LastAccessedTick { get; set; }
    }

    private readonly LinkedList<KeyValuePair<CacheKey, CacheEntry>> _lruList = new();
    private readonly Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, CacheEntry>>> _cache = new();
    private readonly Dictionary<CacheKey, int> _pinCounts = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly int _maxSize;

    // Worst-case footprint: 200 × ≤1 MB (512-bucket) ≈ 200 MB of GPU memory;
    // typical mix of 64/128/256 buckets averages ~50 KB → 10 MB ceiling. The
    // GPU number is what matters now — no decoded CPU pixel buffer per entry.
    public ImageCacheService(int maxSize = 200)
    {
        _maxSize = maxSize;
    }

    public int Count
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _cache.Count; }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Snaps a requested decode pixel size to the smallest bucket (64 / 128 / 256 / 512)
    /// that is &gt;= the request. Without bucketing, requests for 36, 40, 48, 68, 80,
    /// 96, 128, 160, 200, 240, 280, 400 would each create a separate cache entry and
    /// trigger 11 decodes for the same image. Bucketing collapses this to at most 4
    /// entries per URL.
    /// </summary>
    /// <remarks>
    /// A request of 0 means "do not constrain decode size" (native). Preserve that
    /// sentinel so callers that don't know the target size aren't force-bucketed.
    /// </remarks>
    private static int SnapToBucket(int requested)
    {
        if (requested <= 0) return 0;
        if (requested <= 64) return 64;
        if (requested <= 128) return 128;
        if (requested <= 256) return 256;
        return 512;
    }

    /// <summary>
    /// Gets or creates a cached image surface. The underlying
    /// <see cref="LoadedImageSurface"/> begins loading immediately and returns
    /// before the bitmap is decoded; subscribe to
    /// <see cref="CachedImage.LoadCompleted"/> for completion.
    ///
    /// <para>
    /// Pass <paramref name="pin"/> = <c>true</c> when the caller intends to hold
    /// the entry (visible control). Pinning happens atomically inside the
    /// write lock BEFORE the LRU trim runs — required to prevent the
    /// self-eviction race where a freshly-added entry is the only unpinned
    /// candidate when the cache is otherwise full of pinned entries.
    /// </para>
    /// </summary>
    public CachedImage? GetOrCreate(string? uri, int decodePixelSize = 0, bool pin = false)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        decodePixelSize = SnapToBucket(decodePixelSize);
        var key = new CacheKey(uri, decodePixelSize);

        _rwLock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _rwLock.EnterWriteLock();
                try
                {
                    node.Value.Value.LastAccessedTick = Environment.TickCount64;
                    if (!ReferenceEquals(_lruList.First, node))
                    {
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                    }
                    if (pin) BumpPin(key);
                    return node.Value.Value.Image;
                }
                finally { _rwLock.ExitWriteLock(); }
            }
        }
        finally { _rwLock.ExitUpgradeableReadLock(); }

        // Miss — create the surface outside the lock.
        CachedImage? created = null;
        try
        {
            var sizeHint = decodePixelSize > 0
                ? new Size(decodePixelSize, decodePixelSize)
                : Size.Empty;
            var surface = decodePixelSize > 0
                ? LoadedImageSurface.StartLoadFromUri(new Uri(uri), sizeHint)
                : LoadedImageSurface.StartLoadFromUri(new Uri(uri));
            created = new CachedImage(surface, uri, decodePixelSize);
        }
        catch
        {
            return null;
        }

        _rwLock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                // Another caller won the race — dispose the loser, refresh the winner.
                created.Dispose();
                existing.Value.Value.LastAccessedTick = Environment.TickCount64;
                if (!ReferenceEquals(_lruList.First, existing))
                {
                    _lruList.Remove(existing);
                    _lruList.AddFirst(existing);
                }
                if (pin) BumpPin(key);
                return existing.Value.Value.Image;
            }

            var entry = new CacheEntry { Image = created, LastAccessedTick = Environment.TickCount64 };
            var newNode = _lruList.AddFirst(new KeyValuePair<CacheKey, CacheEntry>(key, entry));
            _cache[key] = newNode;

            // Pin BEFORE trim so the just-added entry can't be self-evicted.
            if (pin) BumpPin(key);

            TrimToCapacityNoLock();

            return created;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    private void BumpPin(CacheKey key)
    {
        _pinCounts.TryGetValue(key, out var count);
        _pinCounts[key] = count + 1;
    }

    /// <summary>
    /// Peek the cache without creating a new entry or starting a network load.
    /// Returns the existing <see cref="CachedImage"/> when present, otherwise null.
    /// Use this from realized controls that want the fast-path "display
    /// already-cached image" branch without kicking off a cold fetch — e.g.
    /// <c>CompositionImage</c> bypassing the global image-loading suspension
    /// gate when the surface is already decoded.
    /// </summary>
    public CachedImage? TryGet(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var key = new CacheKey(uri, SnapToBucket(decodePixelSize));
        _rwLock.EnterReadLock();
        try
        {
            return _cache.TryGetValue(key, out var node) ? node.Value.Value.Image : null;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Pins a cache entry so the LRU trim and stale cleanup will not evict it.
    /// Ref-counted — each <see cref="Pin"/> must be balanced by one
    /// <see cref="Unpin"/>. Pinning a not-yet-present entry still increments
    /// the count, protecting it once inserted.
    /// </summary>
    public void Pin(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var key = new CacheKey(uri, SnapToBucket(decodePixelSize));
        _rwLock.EnterWriteLock();
        try { BumpPin(key); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Decrements the pin count. When it hits zero the entry becomes evictable.
    /// </summary>
    public void Unpin(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var key = new CacheKey(uri, SnapToBucket(decodePixelSize));
        _rwLock.EnterWriteLock();
        try
        {
            if (_pinCounts.TryGetValue(key, out var count))
            {
                if (count <= 1) _pinCounts.Remove(key);
                else _pinCounts[key] = count - 1;
            }

            TrimToCapacityNoLock();
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    private void TrimToCapacityNoLock()
    {
        while (_cache.Count > _maxSize)
        {
            var candidate = _lruList.Last;
            var removed = false;

            while (candidate != null)
            {
                var prev = candidate.Previous;
                if (!_pinCounts.ContainsKey(candidate.Value.Key))
                {
                    _cache.Remove(candidate.Value.Key);
                    _lruList.Remove(candidate);
                    candidate.Value.Value.Image.Dispose();
                    removed = true;
                    break;
                }
                candidate = prev;
            }

            if (!removed) break;
        }
    }

    /// <summary>
    /// Forcibly removes an entry from the cache (typically after a decode failure)
    /// so the next <see cref="GetOrCreate"/> creates a fresh surface.
    /// Does not touch pin counts — a poisoned entry can still be retried by an
    /// already-pinned caller. The disposed surface is detached from any
    /// <c>CompositionSurfaceBrush</c> it was bound to; consumers re-fetch and rebind.
    /// </summary>
    public void Invalidate(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var key = new CacheKey(uri, SnapToBucket(decodePixelSize));
        _rwLock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _cache.Remove(key);
                _lruList.Remove(node);
                node.Value.Value.Image.Dispose();
            }
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes all stale entries older than the given max age. Pinned entries
    /// are skipped (not halted-on — older pinned entries don't block eviction
    /// of newer-but-still-stale unpinned ones).
    /// </summary>
    public int CleanupStale(TimeSpan maxAge)
    {
        var cutoffTick = Environment.TickCount64 - (long)maxAge.TotalMilliseconds;
        var removed = 0;

        _rwLock.EnterWriteLock();
        try
        {
            var node = _lruList.Last;
            while (node != null && node.Value.Value.LastAccessedTick < cutoffTick)
            {
                var prev = node.Previous;
                if (!_pinCounts.ContainsKey(node.Value.Key))
                {
                    _cache.Remove(node.Value.Key);
                    _lruList.Remove(node);
                    node.Value.Value.Image.Dispose();
                    removed++;
                }
                node = prev;
            }
        }
        finally { _rwLock.ExitWriteLock(); }

        return removed;
    }

    /// <summary>
    /// Hard clear — disposes every entry including pinned ones and wipes the
    /// pin table. Used by sign-out / user-switch and by the Tier-3 memory
    /// emergency path in <c>MemoryBudgetService</c>. Visible controls will
    /// re-pin and re-load on next layout pass.
    /// </summary>
    public void Clear()
    {
        _rwLock.EnterWriteLock();
        try
        {
            foreach (var node in _lruList)
                node.Value.Image.Dispose();
            _cache.Clear();
            _lruList.Clear();
            _pinCounts.Clear();
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Soft clear used by memory-pressure escalation. Disposes every UNPINNED
    /// LRU entry but keeps pinned entries (and their pin counts) intact, so
    /// images on currently-visible cards survive a budget overshoot.
    /// </summary>
    public int ClearUnpinned()
    {
        _rwLock.EnterWriteLock();
        try
        {
            var removed = 0;
            var node = _lruList.Last;
            while (node is not null)
            {
                var prev = node.Previous;
                if (!_pinCounts.ContainsKey(node.Value.Key))
                {
                    _cache.Remove(node.Value.Key);
                    _lruList.Remove(node);
                    node.Value.Value.Image.Dispose();
                    removed++;
                }
                node = prev;
            }
            return removed;
        }
        finally { _rwLock.ExitWriteLock(); }
    }
}
