using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// O(1) LRU cache for BitmapImage instances. Prevents duplicate downloads
/// and GC of frequently used images. Thread-safe.
/// </summary>
public sealed class ImageCacheService
{
    private readonly record struct CacheKey(string Uri, int DecodeSize);
    private readonly record struct CacheEntry(BitmapImage Image, DateTimeOffset LastAccessed);

    private readonly LinkedList<KeyValuePair<CacheKey, CacheEntry>> _lruList = new();
    private readonly Dictionary<CacheKey, LinkedListNode<KeyValuePair<CacheKey, CacheEntry>>> _cache = new();
    private readonly object _lock = new();
    private readonly int _maxSize;

    public ImageCacheService(int maxSize = 100)
    {
        _maxSize = maxSize;
    }

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>
    /// Gets a cached BitmapImage or creates a new one. The BitmapImage's UriSource
    /// is set, triggering async download. Returns immediately.
    /// </summary>
    public BitmapImage? GetOrCreate(string? uri, int decodePixelSize = 0)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var key = new CacheKey(uri, decodePixelSize);

        lock (_lock)
        {
            // Cache hit — promote to front
            if (_cache.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                node.Value = new KeyValuePair<CacheKey, CacheEntry>(
                    key, node.Value.Value with { LastAccessed = DateTimeOffset.UtcNow });
                return node.Value.Value.Image;
            }

            // Cache miss — create new
            var bitmap = new BitmapImage();
            if (decodePixelSize > 0)
            {
                bitmap.DecodePixelWidth = decodePixelSize;
                bitmap.DecodePixelHeight = decodePixelSize;
            }
            bitmap.UriSource = new Uri(uri);

            var entry = new CacheEntry(bitmap, DateTimeOffset.UtcNow);
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
    }

    /// <summary>
    /// Removes all stale entries older than the given max age.
    /// </summary>
    public int CleanupStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;

        lock (_lock)
        {
            var node = _lruList.Last;
            while (node != null && node.Value.Value.LastAccessed < cutoff)
            {
                var prev = node.Previous;
                _cache.Remove(node.Value.Key);
                _lruList.Remove(node);
                removed++;
                node = prev;
            }
        }

        return removed;
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }
}
