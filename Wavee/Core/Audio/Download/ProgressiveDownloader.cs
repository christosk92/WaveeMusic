using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio.Cache;

namespace Wavee.Core.Audio.Download;

/// <summary>
/// Stream wrapper for progressive HTTP download with range request support.
/// </summary>
/// <remarks>
/// Features:
/// - On-demand HTTP range fetching (downloads only what's needed)
/// - RangeSet tracking for efficient seeking without re-downloading
/// - Blocking reads that wait for data availability
/// - Background prefetch for smooth playback
/// - Head file support for instant playback start
/// - Retry logic with exponential backoff
/// - Buffer status events for UI feedback
/// </remarks>
public sealed class ProgressiveDownloader : Stream, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cdnUrl;
    private readonly long _fileSize;
    private readonly FileId _fileId;
    private readonly AudioFetchParams _params;
    private readonly ILogger? _logger;
    private readonly AudioCacheManager? _cache;

    private readonly RangeSet _downloadedRanges = new();
    private readonly FileStream _tempFile;
    private readonly string _tempFilePath;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly CancellationTokenSource _disposeCts = new();

    private long _position;
    private bool _disposed;
    private DateTime _lastReadTime = DateTime.UtcNow;
    private long _bytesDownloadedTotal;
    private readonly object _throughputLock = new();
    private int _currentThroughput;

    // Signaled by FetchChunkAsync whenever new data is written to the temp file.
    // EnsureDataAvailable waits on this instead of doing its own HTTP request,
    // which avoids _fetchLock contention with the background download loop.
    private readonly ManualResetEventSlim _newDataAvailable = new(false);

    // Background download state
    private Task? _backgroundDownloadTask;
    private CancellationTokenSource? _backgroundDownloadCts;

    /// <summary>
    /// Raised when buffer state changes.
    /// </summary>
    public event Action<BufferStatus>? BufferStateChanged;

    /// <summary>
    /// Raised when a download error occurs.
    /// </summary>
    public event Action<DownloadError>? DownloadError;

    /// <summary>
    /// Creates a new ProgressiveDownloader.
    /// </summary>
    /// <param name="httpClient">HTTP client for CDN requests.</param>
    /// <param name="cdnUrl">CDN URL for the audio file.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <param name="fileId">File ID for caching/logging.</param>
    /// <param name="headData">Optional head file data for instant start.</param>
    /// <param name="params">Fetch parameters.</param>
    /// <param name="logger">Optional logger.</param>
    public ProgressiveDownloader(
        HttpClient httpClient,
        string cdnUrl,
        long fileSize,
        FileId fileId,
        byte[]? headData = null,
        AudioFetchParams? @params = null,
        ILogger? logger = null,
        AudioCacheManager? cache = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cdnUrl);
        if (fileSize <= 0)
            throw new ArgumentException("File size must be positive", nameof(fileSize));

        _httpClient = httpClient;
        _cdnUrl = cdnUrl;
        _fileSize = fileSize;
        _fileId = fileId;
        _params = @params ?? AudioFetchParams.Default;
        _logger = logger;
        _cache = cache;

        // Create temp file for random access storage
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"wavee_audio_{fileId.ToBase16()}.tmp");
        _tempFile = new FileStream(
            _tempFilePath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | FileOptions.DeleteOnClose);

        // Pre-allocate file size
        _tempFile.SetLength(fileSize);

        // Initialize cache entry so chunks can be written
        if (_cache != null)
        {
            _cache.GetOrCreateEntry(fileId, fileSize, AudioFileFormat.OGG_VORBIS_320);
        }

        // Write head data if provided (for instant playback)
        if (headData != null && headData.Length > 0)
        {
            WriteToTempFile(0, headData);
            _downloadedRanges.AddRange(0, headData.Length);
            _bytesDownloadedTotal = headData.Length;

            // Cache the head data too (use ArrayPool to avoid LOH allocations)
            if (_cache != null)
            {
                var cacheChunkSize = AudioCacheConfig.Default.ChunkSize;
                for (var offset = 0; offset < headData.Length; offset += cacheChunkSize)
                {
                    var chunkIdx = offset / cacheChunkSize;
                    var len = Math.Min(cacheChunkSize, headData.Length - offset);
                    var chunk = ArrayPool<byte>.Shared.Rent(len);
                    Buffer.BlockCopy(headData, offset, chunk, 0, len);
                    _ = _cache.WriteChunkAsync(fileId, chunkIdx, chunk.AsMemory(0, len), CancellationToken.None)
                        .ContinueWith(_ => ArrayPool<byte>.Shared.Return(chunk),
                            TaskContinuationOptions.ExecuteSynchronously);
                }
            }

            _logger?.LogDebug(
                "Initialized ProgressiveDownloader with {HeadSize} bytes head data for file {FileId}",
                headData.Length, fileId.ToBase16());
        }
    }

    #region Stream Properties

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _fileSize;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _fileSize)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    /// <summary>
    /// Gets the current buffer status.
    /// </summary>
    public BufferStatus GetBufferStatus()
    {
        var bufferedAhead = _downloadedRanges.ContainedLengthFrom(_position);
        var state = bufferedAhead > _params.MinimumChunkSize
            ? BufferState.Ready
            : BufferState.Buffering;

        return new BufferStatus(
            state,
            bufferedAhead,
            _fileSize,
            _downloadedRanges.TotalBytes,
            _currentThroughput);
    }

    /// <summary>
    /// Gets whether the entire file has been downloaded.
    /// </summary>
    public bool IsFullyDownloaded => _downloadedRanges.TotalBytes >= _fileSize;


    #endregion

    #region Stream Read Operations

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_position >= _fileSize)
            return 0;

        var bytesToRead = (int)Math.Min(buffer.Length, _fileSize - _position);
        if (bytesToRead == 0)
            return 0;

        // Wait for data — blocks until the background download delivers it
        EnsureDataAvailable(_position, bytesToRead);

        // Read from temp file
        var bytesRead = ReadFromTempFile(_position, buffer[..bytesToRead]);
        _position += bytesRead;
        _lastReadTime = DateTime.UtcNow;

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_position >= _fileSize)
            return 0;

        var bytesToRead = (int)Math.Min(buffer.Length, _fileSize - _position);
        if (bytesToRead == 0)
            return 0;

        // Ensure data is available (async)
        await EnsureDataAvailableAsync(_position, bytesToRead, cancellationToken);

        // Read from temp file
        var bytesRead = ReadFromTempFile(_position, buffer.Span[..bytesToRead]);
        _position += bytesRead;
        _lastReadTime = DateTime.UtcNow;

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _fileSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _position = newPosition;

        _logger?.LogDebug("Seek to position {Position}/{FileSize}", _position, _fileSize);

        return _position;
    }

    #endregion

    #region Data Fetching

    private void EnsureDataAvailable(long start, int length)
    {
        var end = start + length;

        // Fast path — data already downloaded
        if (_downloadedRanges.ContainsRange(start, end))
            return;

        // Passive wait: instead of launching a competing HTTP request that fights
        // the background download for _fetchLock, we wait for the background
        // download to deliver the data. It signals _newDataAvailable after every chunk.
        while (!_downloadedRanges.ContainsRange(start, end))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Reset before re-checking to close the race window: any Set() that
            // fires after our ContainsRange check will be visible to the next Wait().
            _newDataAvailable.Reset();

            if (_downloadedRanges.ContainsRange(start, end))
                return;

            if (!_newDataAvailable.Wait(TimeSpan.FromSeconds(30), _disposeCts.Token))
            {
                // Timeout — background download may have stalled. Fall back to
                // a direct on-demand fetch so playback doesn't hang forever.
                _logger?.LogWarning(
                    "Waited 30s for data at pos={Position}, falling back to on-demand fetch", start);
                Task.Run(() => FetchRangeAsync(start, end, _disposeCts.Token)).GetAwaiter().GetResult();
                return;
            }
        }
    }

    private async Task EnsureDataAvailableAsync(long start, int length, CancellationToken cancellationToken)
    {
        var end = start + length;

        if (_downloadedRanges.ContainsRange(start, end))
            return;

        await FetchRangeAsync(start, end, cancellationToken);
    }

    /// <summary>
    /// Fetches a range of data from the CDN.
    /// </summary>
    public async Task FetchRangeAsync(long start, long end, CancellationToken cancellationToken)
    {
        // Clamp to file bounds
        start = Math.Max(0, start);
        end = Math.Min(_fileSize, end);

        if (start >= end)
            return;

        // Find gaps in our downloaded ranges
        var gaps = _downloadedRanges.GetGaps(start, end);
        if (gaps.Count == 0)
            return;

        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check gaps after acquiring lock
            gaps = _downloadedRanges.GetGaps(start, end);

            foreach (var gap in gaps)
            {
                // Expand gap to minimum chunk size for efficiency
                var fetchStart = gap.Start;
                var fetchEnd = Math.Min(
                    Math.Max(gap.End, gap.Start + _params.MinimumChunkSize),
                    _fileSize);

                // Skip if already downloaded (could have been fetched by another task)
                if (_downloadedRanges.ContainsRange(fetchStart, gap.End))
                    continue;

                await FetchChunkWithRetryAsync(fetchStart, fetchEnd, cancellationToken);
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task FetchChunkWithRetryAsync(long start, long end, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var delay = _params.InitialRetryDelay;

        while (true)
        {
            try
            {
                await FetchChunkAsync(start, end, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                retryCount++;
                var willRetry = retryCount < _params.MaxRetries;

                _logger?.LogWarning(ex,
                    "Fetch failed for range [{Start}-{End}], attempt {Attempt}/{MaxRetries}",
                    start, end, retryCount, _params.MaxRetries);

                DownloadError?.Invoke(new DownloadError(ex, retryCount, willRetry));

                if (!willRetry)
                    throw;

                await Task.Delay(delay, cancellationToken);
                delay *= 2; // Exponential backoff
            }
        }
    }

    private async Task FetchChunkAsync(long start, long end, CancellationToken cancellationToken)
    {
        // Try reading from cache first
        if (_cache != null)
        {
            var chunkSize = AudioCacheConfig.Default.ChunkSize;
            var startChunk = (int)(start / chunkSize);
            var endChunk = (int)((end - 1) / chunkSize);

            // Simple case: single chunk request that's fully cached
            if (startChunk == endChunk && _cache.HasChunk(_fileId, startChunk))
            {
                var cached = _cache.ReadChunk(_fileId, startChunk);
                if (cached != null)
                {
                    var chunkStart = (long)startChunk * chunkSize;
                    WriteToTempFile(chunkStart, cached);
                    _downloadedRanges.AddRange(chunkStart, chunkStart + cached.Length);
                    _newDataAvailable.Set();
                    _logger?.LogDebug("Cache hit: chunk {Chunk} for file {FileId}", startChunk, _fileId.ToBase16());
                    BufferStateChanged?.Invoke(GetBufferStatus());
                    return;
                }
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_params.RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, _cdnUrl);
        request.Headers.Range = new RangeHeaderValue(start, end - 1);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        if (response.StatusCode != HttpStatusCode.PartialContent &&
            response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"CDN returned {response.StatusCode} for range [{start}-{end}]");
        }

        // Read response into buffer
        var buffer = _bufferPool.Rent((int)(end - start));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(
                       buffer.AsMemory(totalRead, (int)(end - start) - totalRead),
                       cts.Token)) > 0)
            {
                totalRead += bytesRead;
            }

            // Write to temp file
            WriteToTempFile(start, buffer.AsSpan(0, totalRead));

            // Write to cache (fire-and-forget, don't block playback)
            // Use ArrayPool to avoid LOH allocations (chunks are 512KB+ → go to LOH with new byte[])
            if (_cache != null && totalRead > 0)
            {
                var cacheChunkSize = AudioCacheConfig.Default.ChunkSize;
                var chunkIdx = (int)(start / cacheChunkSize);
                var chunkData = ArrayPool<byte>.Shared.Rent(totalRead);
                Buffer.BlockCopy(buffer, 0, chunkData, 0, totalRead);
                _ = _cache.WriteChunkAsync(_fileId, chunkIdx, chunkData.AsMemory(0, totalRead), _disposeCts.Token)
                    .ContinueWith(_ => ArrayPool<byte>.Shared.Return(chunkData),
                        TaskContinuationOptions.ExecuteSynchronously);
            }

            // Update tracking
            _downloadedRanges.AddRange(start, start + totalRead);
            Interlocked.Add(ref _bytesDownloadedTotal, totalRead);

            // Wake any reader blocked in EnsureDataAvailable
            _newDataAvailable.Set();

            // Update throughput
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds > 0)
            {
                var throughput = (int)(totalRead * 1000 / stopwatch.ElapsedMilliseconds);
                lock (_throughputLock)
                {
                    _currentThroughput = (_currentThroughput + throughput) / 2; // Moving average
                }
            }

            _logger?.LogDebug(
                "Fetched {Bytes} bytes [{Start}-{End}] in {ElapsedMs}ms for file {FileId}",
                totalRead, start, start + totalRead, stopwatch.ElapsedMilliseconds, _fileId.ToBase16());

            // Notify buffer state change
            BufferStateChanged?.Invoke(GetBufferStatus());
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    #endregion

    #region Temp File Operations

    private void WriteToTempFile(long position, ReadOnlySpan<byte> data)
    {
        lock (_tempFile)
        {
            _tempFile.Position = position;
            _tempFile.Write(data);
            _tempFile.Flush();
        }
    }

    private int ReadFromTempFile(long position, Span<byte> buffer)
    {
        lock (_tempFile)
        {
            _tempFile.Position = position;
            return _tempFile.Read(buffer);
        }
    }

    #endregion

    #region Background Download

    /// <summary>
    /// Starts background downloading ahead of playback position.
    /// Downloads are throttled based on connection speed - faster connections
    /// use smaller buffers, slower connections buffer more ahead.
    /// </summary>
    public void StartBackgroundDownload()
    {
        if (_backgroundDownloadTask is not null)
            return; // Already running

        _backgroundDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _backgroundDownloadTask = BackgroundDownloadLoopAsync(_backgroundDownloadCts.Token);
        _logger?.LogDebug("Started background download for file {FileId}", _fileId.ToBase16());
    }

    /// <summary>
    /// Stops the background download.
    /// </summary>
    public void StopBackgroundDownload()
    {
        _backgroundDownloadCts?.Cancel();
        _backgroundDownloadCts?.Dispose();
        _backgroundDownloadCts = null;
        _backgroundDownloadTask = null;
    }

    // Approximate bytes-per-second for OGG Vorbis 320 kbps (typical format).
    // Used to convert ReadAheadDuration to a byte budget.
    private const int EstimatedBytesPerSecond = 40_000; // ~320 kbps

    private async Task BackgroundDownloadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsFullyDownloaded)
            {
                // Check how much is already buffered ahead of current playback position
                var bufferedAhead = _downloadedRanges.ContainedLengthFrom(_position);
                var readAheadBytes = (long)(_params.ReadAheadDuration.TotalSeconds * EstimatedBytesPerSecond);

                if (bufferedAhead >= readAheadBytes)
                {
                    // Enough data buffered — wait before checking again.
                    // Wake up when playback advances (consumes buffer) or after 500ms.
                    try
                    {
                        await Task.Delay(500, cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Get gaps only between current position and the read-ahead horizon
                var horizon = Math.Min(_position + readAheadBytes, _fileSize);
                var gaps = _downloadedRanges.GetGaps(_position, horizon);

                // If the read-ahead window is fully downloaded, look for any remaining gaps
                // (allows eventual full download when idle, but read-ahead is prioritized)
                if (gaps.Count == 0)
                {
                    gaps = _downloadedRanges.GetGaps(0, _fileSize);
                    if (gaps.Count == 0)
                    {
                        _logger?.LogDebug("Background download complete for file {FileId}", _fileId.ToBase16());
                        break;
                    }

                    // We have the read-ahead covered but the file isn't complete.
                    // Download remaining gaps at a relaxed pace.
                    var gap = FindNextGapFromPosition(_position, gaps);
                    var chunkEnd = Math.Min(gap.Start + _params.MaximumChunkSize, gap.End);
                    await FetchRangeAsync(gap.Start, chunkEnd, cancellationToken);

                    // Throttle: no rush since playback buffer is healthy
                    try { await Task.Delay(200, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Download the next gap in the read-ahead window
                var nextGap = gaps[0]; // Already ordered by position
                var end = Math.Min(nextGap.Start + _params.MaximumChunkSize, nextGap.End);
                await FetchRangeAsync(nextGap.Start, end, cancellationToken);

                // Brief yield to let playback thread process data
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Background download failed for file {FileId}", _fileId.ToBase16());
        }
    }

    private ByteRange FindNextGapFromPosition(long position, List<ByteRange> gaps)
    {
        // First, try to find a gap that starts at or after current position
        foreach (var gap in gaps)
        {
            if (gap.Start >= position)
                return gap;
        }

        // If no gap after position, wrap around to the first gap
        return gaps[0];
    }

    #endregion

    #region Unsupported Operations

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Stop background download first
            _backgroundDownloadCts?.Cancel();
            _backgroundDownloadCts?.Dispose();

            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _newDataAvailable.Set(); // Unblock any waiting reader
            _newDataAvailable.Dispose();
            _fetchLock.Dispose();
            _tempFile.Dispose();

            // Delete temp file if it still exists
            try
            {
                if (File.Exists(_tempFilePath))
                    File.Delete(_tempFilePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // Stop background download first
        if (_backgroundDownloadCts is not null)
        {
            await _backgroundDownloadCts.CancelAsync();
            _backgroundDownloadCts.Dispose();
        }

        await _disposeCts.CancelAsync();
        _disposeCts.Dispose();
        _newDataAvailable.Set(); // Unblock any waiting reader
        _newDataAvailable.Dispose();
        _fetchLock.Dispose();
        await _tempFile.DisposeAsync();

        try
        {
            if (File.Exists(_tempFilePath))
                File.Delete(_tempFilePath);
        }
        catch
        {
            // Ignore cleanup errors
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion
}
