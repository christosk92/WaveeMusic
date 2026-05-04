using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// O(1) LRU hot cache for any cache entry type.
/// Uses Dictionary + LinkedListNode for O(1) operations on all methods.
/// Thread-safe with ReaderWriterLockSlim for concurrent read access.
/// </summary>
/// <typeparam name="TEntry">Cache entry type implementing ICacheEntry.</typeparam>
public sealed class HotCache<TEntry> : IHotCache<TEntry>, ICleanableCache where TEntry : class
{
    // Entry stores the cache entry, its LRU node, and last access time for TTL cleanup
    private sealed record CacheNode(TEntry Entry, LinkedListNode<string> Node, DateTimeOffset LastAccessed);

    private readonly Dictionary<string, CacheNode> _cache;
    private readonly LinkedList<string> _lruList = new();  // Head = most recent, Tail = least recent
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly int _maxSize;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>
    /// Creates a new HotCache with the specified maximum size.
    /// </summary>
    /// <param name="maxSize">Maximum number of entries (default 10,000).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HotCache(int maxSize = 10_000, ILogger? logger = null)
    {
        if (maxSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size must be positive");

        _maxSize = maxSize;
        _cache = new Dictionary<string, CacheNode>(maxSize);
        _logger = logger;
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _cache.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <inheritdoc />
    public int MaxSize => _maxSize;

    /// <inheritdoc />
    public TEntry? Get(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(uri, out var entry))
            {
                _lock.EnterWriteLock();
                try
                {
                    // Move to front (most recently used) - O(1)
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                    _cache[uri] = entry with { LastAccessed = DateTimeOffset.UtcNow };
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                return entry.Entry;
            }
            return null;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <inheritdoc />
    public Dictionary<string, TEntry> GetMany(IEnumerable<string> uris)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new Dictionary<string, TEntry>();
        _lock.EnterUpgradeableReadLock();
        try
        {
            var toUpdate = new List<CacheNode>();
            foreach (var uri in uris)
            {
                if (_cache.TryGetValue(uri, out var entry))
                {
                    result[uri] = entry.Entry;
                    toUpdate.Add(entry);
                }
            }

            if (toUpdate.Count > 0)
            {
                _lock.EnterWriteLock();
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var cacheNode in toUpdate)
                    {
                        _lruList.Remove(cacheNode.Node);
                        _lruList.AddFirst(cacheNode.Node);
                        _cache[cacheNode.Node.Value] = cacheNode with { LastAccessed = now };
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
        return result;
    }

    /// <inheritdoc />
    public bool TryPeek(string uri, out TEntry? entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(uri, out var cacheEntry))
            {
                entry = cacheEntry.Entry;
                return true;
            }
            entry = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public void Set(string uri, TEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(uri, out var existing))
            {
                // Update existing - move to front
                _lruList.Remove(existing.Node);
                _lruList.AddFirst(existing.Node);
                _cache[uri] = existing with { Entry = entry, LastAccessed = now };
            }
            else
            {
                // New entry
                var node = new LinkedListNode<string>(uri);
                _lruList.AddFirst(node);
                _cache[uri] = new CacheNode(entry, node, now);

                // Evict if over capacity - O(1) per eviction
                EvictIfNeeded();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void SetMany(IEnumerable<(string Uri, TEntry Entry)> entries)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (uri, entry) in entries)
            {
                if (_cache.TryGetValue(uri, out var existing))
                {
                    _lruList.Remove(existing.Node);
                    _lruList.AddFirst(existing.Node);
                    _cache[uri] = existing with { Entry = entry, LastAccessed = now };
                }
                else
                {
                    var node = new LinkedListNode<string>(uri);
                    _lruList.AddFirst(node);
                    _cache[uri] = new CacheNode(entry, node, now);
                }
            }

            // Evict excess entries
            EvictIfNeeded();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool Remove(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(uri, out var entry))
            {
                _lruList.Remove(entry.Node);
                _cache.Remove(uri);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool Contains(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterReadLock();
        try
        {
            return _cache.ContainsKey(uri);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
            _logger?.LogDebug("HotCache<{EntryType}> cleared", typeof(TEntry).Name);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAllUris()
    {
        _lock.EnterReadLock();
        try
        {
            return _lruList.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Evicts entries until under capacity. Called under write lock.
    /// </summary>
    private void EvictIfNeeded()
    {
        var evictedCount = 0;
        while (_cache.Count > _maxSize && _lruList.Last != null)
        {
            var lruUri = _lruList.Last.Value;
            _lruList.RemoveLast();
            _cache.Remove(lruUri);
            evictedCount++;
        }

        if (evictedCount > 0)
        {
            _logger?.LogDebug("Evicted {Count} entries from HotCache<{EntryType}>", evictedCount, typeof(TEntry).Name);
        }
    }

    #region ICleanableCache

    string ICleanableCache.CacheName => $"HotCache<{typeof(TEntry).Name}>";

    int ICleanableCache.CurrentCount => Count;

    Task<int> ICleanableCache.CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;

        _lock.EnterWriteLock();
        try
        {
            // Walk from tail (oldest) towards head
            var node = _lruList.Last;
            while (node != null)
            {
                ct.ThrowIfCancellationRequested();
                var prev = node.Previous;

                if (_cache.TryGetValue(node.Value, out var cacheNode) && cacheNode.LastAccessed < cutoff)
                {
                    _lruList.Remove(node);
                    _cache.Remove(node.Value);
                    removed++;
                }
                else
                {
                    // LRU tail entries are oldest; stop at first non-stale entry
                    break;
                }

                node = prev;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (removed > 0)
            _logger?.LogDebug("Cleaned {Count} stale entries from HotCache<{EntryType}>", removed, typeof(TEntry).Name);

        return Task.FromResult(removed);
    }

    Task<int> ICleanableCache.ClearAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int before;
        _lock.EnterWriteLock();
        try
        {
            before = _cache.Count;
            _cache.Clear();
            _lruList.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (before > 0)
            _logger?.LogDebug("Cleared {Count} entries from HotCache<{EntryType}>", before, typeof(TEntry).Name);

        return Task.FromResult(before);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lock.Dispose();
        _logger?.LogDebug("HotCache<{EntryType}> disposed", typeof(TEntry).Name);
    }
}
