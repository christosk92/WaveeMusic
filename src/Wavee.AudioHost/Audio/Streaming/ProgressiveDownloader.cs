using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.Audio.Streaming;

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

    // Optional persistent cache: when set, the fully downloaded file is copied here
    // so future plays can bypass CDN resolution entirely.
    private readonly string? _persistCachePath;

    private readonly RangeSet _downloadedRanges = new();
    private readonly FileStream _tempFile;
    private readonly string _tempFilePath;
    // Allow 2 concurrent HTTP fetches so the on-demand fetch for a seek target
    // can start in parallel with an in-flight background prefetch — the bg
    // fetch is no longer cancelled on seek (see NotifySeek) and would otherwise
    // hold the lock for hundreds of ms after the user moved on. HttpClient is
    // thread-safe for concurrent SendAsync; temp-file writes target distinct
    // ranges; RangeSet is internally guarded.
    private readonly SemaphoreSlim _fetchLock = new(2, 2);
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly CancellationTokenSource _disposeCts = new();

    private long _position;
    private bool _disposed;
    private DateTime _lastReadTime = DateTime.UtcNow;
    private long _bytesDownloadedTotal;
    private readonly object _throughputLock = new();
    private int _currentThroughput;

    // Signaled by FetchChunkAsync whenever new data is written to the temp file.
    // Kept so waiting readers can be released during disposal and for future
    // passive wait paths; seek-critical reads issue their own priority fetch.
    private readonly ManualResetEventSlim _newDataAvailable = new(false);

    // Released by NotifySeek to wake the background loop out of its idle Task.Delay
    // without cancelling the in-flight FetchChunkAsync (whose cancellation would
    // close the warm TCP+TLS connection, paying a fresh handshake on the next fetch).
    private readonly SemaphoreSlim _seekWakeSignal = new(0, 1);

    // Background download state
    private Task? _backgroundDownloadTask;
    private CancellationTokenSource? _backgroundDownloadCts;
    // Cancellable independently of the loop itself: NotifySeek() rotates this CTS
    // so any in-flight prefetch FetchRangeAsync aborts mid-request, freeing the
    // loop to re-plan against the new _position. Without this, a seek triggers
    // both the old read-ahead burst (~600 KB still completing) AND a new burst
    // for the new position — the 3 MB pile-up we used to see in the log.
    private CancellationTokenSource? _backgroundFetchCts;
    private readonly object _fetchCtsLock = new();

    // Wall-clock tick (Environment.TickCount64) at which the post-seek "random
    // access" recovery window expires. Inside the window the background loop
    // shrinks both the read-ahead horizon and the per-chunk fetch size — just
    // enough to satisfy the unmute threshold + NVorbis pre-roll, then idles.
    // After the window ends it returns to the full ReadAheadDuration target.
    // Mirrors librespot's mode switch: random-access fetches near the seek,
    // then back to streaming-mode prefetch once playback is stable.
    private long _postSeekRecoveryUntilTick;
    private const int PostSeekRecoveryMs = 2000;

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
    /// <param name="fileId">File ID for logging.</param>
    /// <param name="headData">Optional head file data for instant start.</param>
    /// <param name="params">Fetch parameters.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="persistCachePath">
    /// When set, the fully downloaded file is written here so future plays can skip CDN resolution.
    /// </param>
    public ProgressiveDownloader(
        HttpClient httpClient,
        string cdnUrl,
        long fileSize,
        FileId fileId,
        byte[]? headData = null,
        AudioFetchParams? @params = null,
        ILogger? logger = null,
        string? persistCachePath = null)
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
        _persistCachePath = persistCachePath;

        // Create a unique temp file per downloader instance.
        // Reusing only fileId in the name can collide when old playback is still disposing
        // while a new playback for the same file starts.
        _tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"wavee_audio_{fileId.ToBase16()}_{Environment.ProcessId}_{Guid.NewGuid():N}.tmp");
        _tempFile = new FileStream(
            _tempFilePath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | FileOptions.DeleteOnClose);

        // Pre-allocate file size
        _tempFile.SetLength(fileSize);

        // Write head data if provided (for instant playback)
        if (headData != null && headData.Length > 0)
        {
            WriteToTempFile(0, headData);
            _downloadedRanges.AddRange(0, headData.Length);
            _bytesDownloadedTotal = headData.Length;

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

        // Wait for data -- blocks until the background download delivers it
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

        // Fast path -- data already downloaded
        if (_downloadedRanges.ContainsRange(start, end))
            return;

        // [seek-trace] only when we actually have to wait — fast-path returns above.
        var traceSeq = Wavee.AudioHost.Diagnostics.SeekTrace.CurrentSeq;
        var traceStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var bufferedAheadKb = _downloadedRanges.ContainedLengthFrom(_position) / 1024;
        _logger?.LogDebug(
            "[seek-trace] seq={Seq} PD.wait need=[{Start}-{End}] pos={Pos} downloaded_ahead={KB}KB",
            traceSeq, start, end, _position, bufferedAheadKb);

        try
        {
            FetchRangeAsync(start, end, _disposeCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw;
        }

        var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - traceStart) * 1000d
                      / System.Diagnostics.Stopwatch.Frequency;
        _logger?.LogDebug("[seek-trace] seq={Seq} PD.wait resolved elapsed={Ms:F1}ms",
            traceSeq, totalMs);
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
                // Don't retry-and-warn when the caller cancelled (e.g. seek
                // rotated the bg fetch CTS). The cancellation is expected;
                // surfacing it as "Fetch failed attempt 1/5" muddies real
                // failure logs and would otherwise enter a 1+2+4 s backoff
                // storm if Task.Delay below didn't observe the cancellation.
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

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
        lock (_fetchCtsLock)
        {
            _backgroundFetchCts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundDownloadCts.Token);
        }
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
        lock (_fetchCtsLock)
        {
            _backgroundFetchCts?.Dispose();
            _backgroundFetchCts = null;
        }
        _backgroundDownloadTask = null;
    }

    /// <summary>
    /// Tells the downloader the read pointer just jumped (e.g. user seek). The
    /// in-flight prefetch is intentionally NOT cancelled — cancelling it
    /// disposes the HTTP response mid-stream, which closes the warm TCP+TLS
    /// connection and forces the next fetch (for the seek target) to pay a
    /// full handshake. The bytes the bg fetch is still pulling land in
    /// <c>_downloadedRanges</c> and remain useful. We rotate
    /// <c>_backgroundFetchCts</c> so the *next* loop iteration plans against
    /// the new <c>_position</c>, and rely on the relaxed <c>_fetchLock</c>
    /// (2 concurrent) + warm HttpClient pool to let the on-demand seek-target
    /// fetch race ahead in parallel.
    /// </summary>
    public void NotifySeek()
    {
        lock (_fetchCtsLock)
        {
            if (_backgroundDownloadCts == null) return; // not running
            // The previous _backgroundFetchCts is intentionally NOT cancelled
            // and NOT disposed: the in-flight FetchChunkAsync still holds a
            // child CTS linked to its token (line ~398 inside FetchChunkAsync)
            // and disposing the parent while subscribed would throw
            // ObjectDisposedException. Let GC reclaim it once the request
            // finishes; CancellationTokenSource has no unmanaged resources
            // unless WaitHandle was accessed.
            _backgroundFetchCts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundDownloadCts.Token);
        }
        // Enter random-access mode for ~2 s. The loop will only fetch one
        // MinimumChunkSize ahead during this window, so we don't pile a
        // full-buffer prefetch burst on top of the decoder pre-roll the user
        // is actually waiting on.
        Volatile.Write(ref _postSeekRecoveryUntilTick, Environment.TickCount64 + PostSeekRecoveryMs);
        // Wake the bg loop out of any idle Task.Delay so it re-plans against
        // the new _position now, not after the 200/500 ms timer fires.
        try { _seekWakeSignal.Release(); } catch (SemaphoreFullException) { }
    }

    // Approximate bytes-per-second for OGG Vorbis 320 kbps (typical format).
    // Used to convert ReadAheadDuration to a byte budget.
    private const int EstimatedBytesPerSecond = 40_000; // ~320 kbps

    private async Task BackgroundDownloadLoopAsync(CancellationToken loopToken)
    {
        try
        {
            while (!loopToken.IsCancellationRequested && !IsFullyDownloaded)
            {
                // Snapshot the seek-cancellable fetch token. NotifySeek() may rotate
                // _backgroundFetchCts at any time; each loop iteration uses the
                // current token so a seek interrupts the in-flight FetchRangeAsync
                // and the next iteration plans against the new _position.
                CancellationToken fetchToken;
                lock (_fetchCtsLock)
                {
                    fetchToken = _backgroundFetchCts?.Token ?? loopToken;
                }

                // Check how much is already buffered ahead of current playback position
                var bufferedAhead = _downloadedRanges.ContainedLengthFrom(_position);
                var readAheadBytes = (long)(_params.ReadAheadDuration.TotalSeconds * EstimatedBytesPerSecond);

                // Random-access mode: shrink the horizon to one MinimumChunkSize
                // until the post-seek window expires. The decoder pre-roll + sink
                // unmute threshold are both well under one chunk, so the user gets
                // their audio back ASAP without us racing to refill the full buffer.
                var inRandomAccess = Environment.TickCount64 < Volatile.Read(ref _postSeekRecoveryUntilTick);
                if (inRandomAccess)
                {
                    readAheadBytes = _params.MinimumChunkSize;
                }

                if (bufferedAhead >= readAheadBytes)
                {
                    // Enough data buffered -- wait before checking again. Wakes
                    // up early on seek (NotifySeek releases _seekWakeSignal),
                    // otherwise after 500 ms.
                    try
                    {
                        await _seekWakeSignal.WaitAsync(500, loopToken);
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
                        // Persist to audio cache so future plays skip CDN resolution
                        if (_persistCachePath != null)
                            _ = PersistToCacheAsync(_persistCachePath, CancellationToken.None);
                        break;
                    }

                    // We have the read-ahead covered but the file isn't complete.
                    // Download remaining gaps at a relaxed pace.
                    var gap = FindNextGapFromPosition(_position, gaps);
                    var chunkEnd = Math.Min(gap.Start + _params.MaximumChunkSize, gap.End);
                    try { await FetchRangeAsync(gap.Start, chunkEnd, fetchToken); }
                    catch (OperationCanceledException) when (!loopToken.IsCancellationRequested) { continue; }

                    // Throttle: no rush since playback buffer is healthy.
                    // Wakes early on seek so we re-plan against new _position.
                    try { await _seekWakeSignal.WaitAsync(200, loopToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Download the next gap in the read-ahead window. In random-access
                // mode use the small chunk size so the wait-for-bytes is short and
                // we don't grab a 256 KB payload only to discard it on the next seek.
                var nextGap = gaps[0]; // Already ordered by position
                var chunkCap = inRandomAccess ? _params.MinimumChunkSize : _params.MaximumChunkSize;
                var end = Math.Min(nextGap.Start + chunkCap, nextGap.End);
                try { await FetchRangeAsync(nextGap.Start, end, fetchToken); }
                catch (OperationCanceledException) when (!loopToken.IsCancellationRequested) { continue; }

                // Brief yield to let playback thread process data
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[Download] Background prefetch cancelled for file {FileId}", _fileId.ToBase16());
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

    /// <summary>
    /// Copies the fully downloaded temp file to the persistent cache path.
    /// Fire-and-forget: if it fails we just lose the cache benefit for this session.
    /// </summary>
    private async Task PersistToCacheAsync(string cachePath, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Read temp file from start and write to cache path atomically via a temp swap
            var swapPath = cachePath + ".tmp";
            _tempFile.Seek(0, SeekOrigin.Begin);
            await using (var dest = new FileStream(swapPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, useAsync: true))
            {
                await _tempFile.CopyToAsync(dest, ct);
            }
            File.Move(swapPath, cachePath, overwrite: true);

            _logger?.LogInformation("Audio file {FileId} persisted to cache ({Bytes} bytes)",
                _fileId.ToBase16(), _fileSize);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to persist audio cache for {FileId}", _fileId.ToBase16());
        }
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
            lock (_fetchCtsLock)
            {
                _backgroundFetchCts?.Dispose();
                _backgroundFetchCts = null;
            }

            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _newDataAvailable.Set(); // Unblock any waiting reader
            _newDataAvailable.Dispose();
            _fetchLock.Dispose();
            _seekWakeSignal.Dispose();
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
        lock (_fetchCtsLock)
        {
            _backgroundFetchCts?.Dispose();
            _backgroundFetchCts = null;
        }

        await _disposeCts.CancelAsync();
        _disposeCts.Dispose();
        _newDataAvailable.Set(); // Unblock any waiting reader
        _newDataAvailable.Dispose();
        _fetchLock.Dispose();
        _seekWakeSignal.Dispose();
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
