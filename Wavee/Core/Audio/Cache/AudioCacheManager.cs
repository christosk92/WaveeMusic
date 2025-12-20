using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Audio.Cache;

/// <summary>
/// Manages disk caching for audio files.
/// </summary>
/// <remarks>
/// Features:
/// - Chunk-based storage for efficient partial caching
/// - LRU eviction when cache size exceeds limit
/// - Thread-safe concurrent access
/// - Automatic background pruning
/// </remarks>
public sealed class AudioCacheManager : IAsyncDisposable
{
    private readonly AudioCacheConfig _config;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _pruneCts = new();
    private readonly Task _pruneTask;
    private bool _disposed;

    /// <summary>
    /// Creates a new AudioCacheManager.
    /// </summary>
    public AudioCacheManager(AudioCacheConfig? config = null, ILogger? logger = null)
    {
        _config = config ?? AudioCacheConfig.Default;
        _logger = logger;

        if (_config.EnableCaching)
        {
            EnsureCacheDirectoryExists();
            LoadExistingEntries();
            _pruneTask = RunPruneLoopAsync(_pruneCts.Token);
        }
        else
        {
            _pruneTask = Task.CompletedTask;
        }
    }

    /// <summary>
    /// Gets current cache statistics.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var entries = _entries.Values.ToList();
        return new CacheStatistics(
            TotalEntries: entries.Count,
            TotalCachedBytes: entries.Sum(e => e.CachedBytes),
            MaxCacheBytes: _config.MaxCacheSizeBytes,
            CompleteFiles: entries.Count(e => e.IsComplete),
            PartialFiles: entries.Count(e => !e.IsComplete));
    }

    /// <summary>
    /// Checks if a file has any cached chunks.
    /// </summary>
    public bool HasCachedFile(FileId fileId)
    {
        if (!_config.EnableCaching)
            return false;

        return _entries.TryGetValue(fileId.ToBase16(), out var entry) &&
               entry.CachedChunks.Count > 0;
    }

    /// <summary>
    /// Checks if a file is completely cached.
    /// </summary>
    public bool HasCompleteFile(FileId fileId)
    {
        if (!_config.EnableCaching)
            return false;

        return _entries.TryGetValue(fileId.ToBase16(), out var entry) && entry.IsComplete;
    }

    /// <summary>
    /// Gets or creates a cache entry for a file.
    /// </summary>
    public CacheEntry GetOrCreateEntry(FileId fileId, long fileSize, AudioFileFormat format)
    {
        var key = fileId.ToBase16();

        return _entries.GetOrAdd(key, _ =>
        {
            var entry = CacheEntry.Create(fileId, fileSize, format, _config.ChunkSize);
            SaveEntryMetadata(entry);
            return entry;
        });
    }

    /// <summary>
    /// Checks if a specific chunk is cached.
    /// </summary>
    public bool HasChunk(FileId fileId, int chunkIndex)
    {
        if (!_config.EnableCaching)
            return false;

        if (!_entries.TryGetValue(fileId.ToBase16(), out var entry))
            return false;

        return entry.HasChunk(chunkIndex);
    }

    /// <summary>
    /// Reads a chunk from cache.
    /// </summary>
    /// <returns>Chunk data, or null if not cached.</returns>
    public byte[]? ReadChunk(FileId fileId, int chunkIndex)
    {
        if (!_config.EnableCaching)
            return null;

        if (!_entries.TryGetValue(fileId.ToBase16(), out var entry))
            return null;

        if (!entry.HasChunk(chunkIndex))
            return null;

        var chunkPath = GetChunkPath(entry.FileId, chunkIndex);
        if (!File.Exists(chunkPath))
        {
            // Chunk file missing, update entry
            entry.CachedChunks.Remove(chunkIndex);
            return null;
        }

        try
        {
            var data = File.ReadAllBytes(chunkPath);

            // Update last accessed time
            entry.LastAccessed = DateTime.UtcNow;

            _logger?.LogDebug("Cache hit: file {FileId} chunk {ChunkIndex}",
                entry.FileId, chunkIndex);

            return data;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read cached chunk {ChunkIndex} for file {FileId}",
                chunkIndex, entry.FileId);
            return null;
        }
    }

    /// <summary>
    /// Writes a chunk to cache.
    /// </summary>
    public async Task WriteChunkAsync(FileId fileId, int chunkIndex, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_config.EnableCaching || data.Length == 0)
            return;

        var key = fileId.ToBase16();
        if (!_entries.TryGetValue(key, out var entry))
        {
            _logger?.LogWarning("Cannot write chunk: no cache entry for file {FileId}", key);
            return;
        }

        if (entry.HasChunk(chunkIndex))
            return; // Already cached

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (entry.HasChunk(chunkIndex))
                return;

            var chunkPath = GetChunkPath(key, chunkIndex);
            var directory = Path.GetDirectoryName(chunkPath)!;

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(chunkPath, data, cancellationToken);

            entry.CachedChunks.Add(chunkIndex);
            entry.LastAccessed = DateTime.UtcNow;

            _logger?.LogDebug("Cached chunk {ChunkIndex} for file {FileId} ({Bytes} bytes)",
                chunkIndex, key, data.Length);

            // Periodically save entry metadata
            if (entry.CachedChunks.Count % 10 == 0)
            {
                SaveEntryMetadata(entry);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to cache chunk {ChunkIndex} for file {FileId}",
                chunkIndex, key);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Opens a stream for reading a complete cached file.
    /// </summary>
    /// <returns>Stream for reading, or null if file is not completely cached.</returns>
    public Stream? OpenCachedFile(FileId fileId)
    {
        if (!_config.EnableCaching)
            return null;

        if (!_entries.TryGetValue(fileId.ToBase16(), out var entry))
            return null;

        if (!entry.IsComplete)
            return null;

        // Update last accessed time
        entry.LastAccessed = DateTime.UtcNow;

        // Return a stream that reads chunks sequentially
        return new CachedFileStream(this, entry);
    }

    /// <summary>
    /// Deletes a cached file and its chunks.
    /// </summary>
    public async Task DeleteFileAsync(FileId fileId)
    {
        var key = fileId.ToBase16();

        if (_entries.TryRemove(key, out var entry))
        {
            await _writeLock.WaitAsync();
            try
            {
                var directory = GetFileDirectory(key);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                _logger?.LogDebug("Deleted cached file {FileId}", key);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete cached file {FileId}", key);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    /// <summary>
    /// Prunes cache to stay under size limit using LRU eviction.
    /// </summary>
    public async Task PruneCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.EnableCaching)
            return;

        var targetSize = (long)(_config.MaxCacheSizeBytes * (1 - _config.MinFreeSpacePercent));
        var currentSize = _entries.Values.Sum(e => e.CachedBytes);

        if (currentSize <= targetSize)
            return;

        _logger?.LogInformation("Pruning cache: current {CurrentMB}MB, target {TargetMB}MB",
            currentSize / 1024 / 1024, targetSize / 1024 / 1024);

        // Get entries sorted by last access time (oldest first)
        var entriesToPrune = _entries.Values
            .OrderBy(e => e.LastAccessed)
            .ToList();

        foreach (var entry in entriesToPrune)
        {
            if (currentSize <= targetSize)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            var fileId = FileId.FromBase16(entry.FileId);
            var entrySize = entry.CachedBytes;

            await DeleteFileAsync(fileId);
            currentSize -= entrySize;

            _logger?.LogDebug("Pruned file {FileId}, freed {SizeKB}KB",
                entry.FileId, entrySize / 1024);
        }

        _logger?.LogInformation("Cache pruning complete: {SizeMB}MB remaining",
            currentSize / 1024 / 1024);
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        if (!_config.EnableCaching)
            return;

        await _writeLock.WaitAsync();
        try
        {
            _entries.Clear();

            var audioDir = Path.Combine(_config.CacheDirectory, "audio");
            if (Directory.Exists(audioDir))
            {
                Directory.Delete(audioDir, recursive: true);
                Directory.CreateDirectory(audioDir);
            }

            _logger?.LogInformation("Cache cleared");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #region Private Methods

    private void EnsureCacheDirectoryExists()
    {
        var audioDir = Path.Combine(_config.CacheDirectory, "audio");
        if (!Directory.Exists(audioDir))
        {
            Directory.CreateDirectory(audioDir);
        }
    }

    private void LoadExistingEntries()
    {
        var audioDir = Path.Combine(_config.CacheDirectory, "audio");
        if (!Directory.Exists(audioDir))
            return;

        foreach (var fileDir in Directory.GetDirectories(audioDir))
        {
            var metadataPath = Path.Combine(fileDir, "metadata.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                var json = File.ReadAllText(metadataPath);
                var entry = CacheEntry.FromJson(json);
                if (entry != null)
                {
                    // Verify which chunks actually exist
                    var existingChunks = new HashSet<int>();
                    foreach (var chunkIndex in entry.CachedChunks)
                    {
                        var chunkPath = GetChunkPath(entry.FileId, chunkIndex);
                        if (File.Exists(chunkPath))
                        {
                            existingChunks.Add(chunkIndex);
                        }
                    }
                    entry.CachedChunks.Clear();
                    foreach (var chunk in existingChunks)
                    {
                        entry.CachedChunks.Add(chunk);
                    }

                    _entries[entry.FileId] = entry;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load cache entry from {Path}", metadataPath);
            }
        }

        _logger?.LogDebug("Loaded {Count} cache entries", _entries.Count);
    }

    private void SaveEntryMetadata(CacheEntry entry)
    {
        try
        {
            var directory = GetFileDirectory(entry.FileId);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var metadataPath = Path.Combine(directory, "metadata.json");
            File.WriteAllText(metadataPath, entry.ToJson());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save cache entry metadata for {FileId}", entry.FileId);
        }
    }

    private string GetFileDirectory(string fileIdHex)
    {
        return Path.Combine(_config.CacheDirectory, "audio", fileIdHex);
    }

    private string GetChunkPath(string fileIdHex, int chunkIndex)
    {
        return Path.Combine(GetFileDirectory(fileIdHex), $"{chunkIndex:D4}.chunk");
    }

    private async Task RunPruneLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.PruneInterval, cancellationToken);
                await PruneCacheAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache prune task failed");
            }
        }
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _pruneCts.CancelAsync();
        _pruneCts.Dispose();

        try
        {
            await _pruneTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Save all entry metadata on shutdown
        foreach (var entry in _entries.Values)
        {
            SaveEntryMetadata(entry);
        }

        _writeLock.Dispose();
    }

    #endregion
}

/// <summary>
/// Cache statistics for monitoring.
/// </summary>
public readonly record struct CacheStatistics(
    int TotalEntries,
    long TotalCachedBytes,
    long MaxCacheBytes,
    int CompleteFiles,
    int PartialFiles)
{
    /// <summary>
    /// Cache usage as percentage.
    /// </summary>
    public double UsagePercent => MaxCacheBytes > 0
        ? (double)TotalCachedBytes / MaxCacheBytes * 100
        : 0;
}

/// <summary>
/// Stream that reads from cached chunks.
/// </summary>
internal sealed class CachedFileStream : Stream
{
    private readonly AudioCacheManager _cache;
    private readonly CacheEntry _entry;
    private long _position;
    private bool _disposed;

    public CachedFileStream(AudioCacheManager cache, CacheEntry entry)
    {
        _cache = cache;
        _entry = entry;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _entry.FileSize;
    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _entry.FileSize);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _position >= _entry.FileSize)
            return 0;

        var totalRead = 0;

        while (count > 0 && _position < _entry.FileSize)
        {
            var chunkIndex = (int)(_position / _entry.ChunkSize);
            var chunkOffset = (int)(_position % _entry.ChunkSize);

            var fileId = FileId.FromBase16(_entry.FileId);
            var chunkData = _cache.ReadChunk(fileId, chunkIndex);

            if (chunkData == null)
                throw new InvalidOperationException($"Chunk {chunkIndex} not found in cache");

            var bytesToCopy = Math.Min(count, chunkData.Length - chunkOffset);
            bytesToCopy = (int)Math.Min(bytesToCopy, _entry.FileSize - _position);

            Array.Copy(chunkData, chunkOffset, buffer, offset, bytesToCopy);

            _position += bytesToCopy;
            offset += bytesToCopy;
            count -= bytesToCopy;
            totalRead += bytesToCopy;
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _entry.FileSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        _position = Math.Clamp(_position, 0, _entry.FileSize);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
