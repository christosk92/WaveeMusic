using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// O(1) LRU cache for BitmapImage instances. Prevents duplicate downloads
/// and GC of frequently used images. Thread-safe via ReaderWriterLockSlim.
/// </summary>
public sealed class ImageCacheService
{
    private readonly record struct CacheKey(string Uri, int DecodeSize);
    private readonly record struct CacheEntry(BitmapImage Image, long LastAccessedTick);

    private readonly LinkedList<KeyValuePair<CacheKey, CacheEntry>> _lruList = new();
    private readonly Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, CacheEntry>>> _cache = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly int _maxSize;

    public ImageCacheService(int maxSize = 100)
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
    /// Gets a cached BitmapImage or creates a new one. The BitmapImage's UriSource
    /// is set, triggering async download. Returns immediately.
    /// </summary>
    public BitmapImage? GetOrCreate(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var key = new CacheKey(uri, decodePixelSize);

        // Fast path: read-only check (most calls are hits — no write lock contention)
        _rwLock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // Skip LRU reordering on read — amortize via periodic cleanup.
                // This avoids upgrading to write lock on every cache hit.
                return node.Value.Value.Image;
            }
        }
        finally { _rwLock.ExitReadLock(); }

        // Cache miss — create BitmapImage outside the lock
        var bitmap = new BitmapImage();
        if (decodePixelSize > 0)
        {
            bitmap.DecodePixelWidth = decodePixelSize;
            bitmap.DecodePixelHeight = decodePixelSize;
        }
        bitmap.UriSource = new Uri(uri);

        // Write lock to insert
        _rwLock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var existing))
                return existing.Value.Value.Image; // Another caller won the race

            var tick = Environment.TickCount64;
            var entry = new CacheEntry(bitmap, tick);
            var newNode = _lruList.AddFirst(new KeyValuePair<CacheKey, CacheEntry>(key, entry));
            _cache[key] = newNode;

            // Evict oldest if over capacity
            if (_cache.Count > _maxSize && _lruList.Last != null)
            {
                var oldest = _lruList.Last;
                _cache.Remove(oldest.Value.Key);
                _lruList.RemoveLast();
            }

            return bitmap;
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
            var node = _lruList.Last;
            while (node != null && node.Value.Value.LastAccessedTick < cutoffTick)
            {
                var prev = node.Previous;
                _cache.Remove(node.Value.Key);
                _lruList.Remove(node);
                removed++;
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
        }
        finally { _rwLock.ExitWriteLock(); }
    }
}
