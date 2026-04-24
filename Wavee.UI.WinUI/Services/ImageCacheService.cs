using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// O(1) LRU cache for BitmapImage instances. Prevents duplicate downloads
/// and GC of frequently used images. Thread-safe via ReaderWriterLockSlim.
///
/// Each BitmapImage holds a decoded bitmap in unmanaged DirectX memory, so keeping
/// the capacity modest is a real memory win. Pair this with the periodic
/// <see cref="CleanupStale"/> call from CacheCleanupService so images sitting
/// unused for longer than the TTL are dropped even when the cache is not full.
/// </summary>
public sealed class ImageCacheService
{
    private readonly record struct CacheKey(string Uri, int DecodeSize);
    private readonly record struct CacheEntry(BitmapImage Image, long LastAccessedTick);

    private readonly LinkedList<KeyValuePair<CacheKey, CacheEntry>> _lruList = new();
    private readonly Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, CacheEntry>>> _cache = new();
    private readonly Dictionary<CacheKey, int> _pinCounts = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly int _maxSize;

    // 60 BitmapImages (~1 MB each unmanaged at 512x512) caps the worst-case unmanaged
    // footprint near 60 MB, down from ~100-200 MB with the previous default of 100.
    // Large enough to cover a typical page of album art + player bar + queue without
    // constant re-fetching.
    public ImageCacheService(int maxSize = 60)
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
    /// that is &gt;= the request. This caps decode-size fragmentation — the codebase
    /// currently asks for 11 distinct sizes (36, 40, 48, 68, 80, 96, 128, 160, 200,
    /// 240, 280, 400), which without bucketing would create 11 separate cache entries
    /// (and trigger 11 decodes) for the same image. Bucketing collapses this to at
    /// most 4 entries per URL. A 128-bucket bitmap rendered in a 96 px slot looks
    /// identical because <c>BitmapImage</c> scales at render time; the small extra
    /// GPU memory cost is dwarfed by the decode-work savings.
    /// </summary>
    /// <remarks>
    /// A request of 0 means "do not set DecodePixelSize" (native size). Preserve that
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
    /// Gets a cached BitmapImage or creates a new one. The BitmapImage's UriSource
    /// is set, triggering async download. Returns immediately.
    /// </summary>
    public BitmapImage? GetOrCreate(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        // Snap the requested decode size to a fixed bucket before lookup so callers
        // requesting e.g. 96 and 128 both hit the same cache entry.
        decodePixelSize = SnapToBucket(decodePixelSize);
        var key = new CacheKey(uri, decodePixelSize);

        // Fast path: no LRU list reorder on hit, but refresh LastAccessedTick under
        // the write lock so CleanupStale does not evict still-in-use hot images.
        _rwLock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _rwLock.EnterWriteLock();
                try
                {
                    var image = node.Value.Value.Image;
                    node.Value = new KeyValuePair<CacheKey, CacheEntry>(
                        key,
                        new CacheEntry(image, Environment.TickCount64));
                    return image;
                }
                finally { _rwLock.ExitWriteLock(); }
            }
        }
        finally { _rwLock.ExitUpgradeableReadLock(); }

        // Cache miss — create BitmapImage outside the lock
        var bitmap = new BitmapImage();
        if (decodePixelSize > 0)
        {
            bitmap.DecodePixelWidth = decodePixelSize;
        }
        bitmap.UriSource = new Uri(uri);

        // Write lock to insert
        _rwLock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                // Another caller won the race. Refresh the tick so the entry is
                // treated as hot and bump it to the front of the LRU list.
                var refreshed = new CacheEntry(existing.Value.Value.Image, Environment.TickCount64);
                existing.Value = new KeyValuePair<CacheKey, CacheEntry>(key, refreshed);
                if (!ReferenceEquals(_lruList.First, existing))
                {
                    _lruList.Remove(existing);
                    _lruList.AddFirst(existing);
                }
                return refreshed.Image;
            }

            var tick = Environment.TickCount64;
            var entry = new CacheEntry(bitmap, tick);
            var newNode = _lruList.AddFirst(new KeyValuePair<CacheKey, CacheEntry>(key, entry));
            _cache[key] = newNode;

            // Evict oldest unpinned entry if over capacity. Pinned entries (held by
            // visible Image controls) are skipped so we don't force WinUI to re-decode
            // a BitmapImage that's still on screen.
            if (_cache.Count > _maxSize)
            {
                var candidate = _lruList.Last;
                while (candidate != null)
                {
                    var prev = candidate.Previous;
                    if (!_pinCounts.ContainsKey(candidate.Value.Key))
                    {
                        _cache.Remove(candidate.Value.Key);
                        _lruList.Remove(candidate);
                        break;
                    }
                    candidate = prev;
                }
            }

            return bitmap;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Pins a cache entry so <see cref="GetOrCreate"/> and <see cref="CleanupStale"/>
    /// will not evict it. Ref-counted — callers must balance each <see cref="Pin"/>
    /// with one <see cref="Unpin"/>. Pinning a not-yet-present entry still increments
    /// the count (protects the entry once it's inserted).
    /// </summary>
    public void Pin(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var key = new CacheKey(uri, SnapToBucket(decodePixelSize));
        _rwLock.EnterWriteLock();
        try
        {
            _pinCounts.TryGetValue(key, out var count);
            _pinCounts[key] = count + 1;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Decrements the pin count. When it hits zero the entry is removed from the pin
    /// table and becomes evictable again.
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
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Forcibly removes an entry from the cache (typically after a decode failure)
    /// so the next <see cref="GetOrCreate"/> creates a fresh <c>BitmapImage</c>.
    /// Does not touch pin counts — a poisoned entry can still be retried.
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
            }
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes all stale entries older than the given max age.
    /// </summary>
    public int CleanupStale(TimeSpan maxAge)
    {
        var cutoffTick = Environment.TickCount64 - (long)maxAge.TotalMilliseconds;
        var removed = 0;

        _rwLock.EnterWriteLock();
        try
        {
            // Walk from the tail (oldest) forward. Pinned entries are held by visible
            // Image controls and must be skipped, not halted-on — an older pinned
            // entry shouldn't block eviction of a newer-but-still-stale unpinned one.
            var node = _lruList.Last;
            while (node != null && node.Value.Value.LastAccessedTick < cutoffTick)
            {
                var prev = node.Previous;
                if (!_pinCounts.ContainsKey(node.Value.Key))
                {
                    _cache.Remove(node.Value.Key);
                    _lruList.Remove(node);
                    removed++;
                }
                node = prev;
            }
        }
        finally { _rwLock.ExitWriteLock(); }

        return removed;
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void Clear()
    {
        _rwLock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
            _pinCounts.Clear();
        }
        finally { _rwLock.ExitWriteLock(); }
    }
}
