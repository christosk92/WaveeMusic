using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.Audio.Streaming;

/// <summary>
/// Stream that provides instant playback start by serving head data immediately,
/// then lazily initializing CDN download when head data is exhausted.
/// </summary>
/// <remarks>
/// This enables true instant start:
/// 1. Head file is already decrypted - serve immediately without waiting for audioKey or CDN
/// 2. When position reaches end of head data, await deferred task
/// 3. Create ProgressiveDownloader + AudioDecryptStream on demand
/// 4. Seamlessly continue with decrypted CDN data
/// </remarks>
public sealed class LazyProgressiveDownloader : Stream
{
    private const int SpotifyHeaderSize = 0xa7;

    private readonly byte[] _headData;
    private readonly Task<DeferredResult> _deferredTask;
    private readonly HttpClient _httpClient;
    private readonly FileId _fileId;
    private readonly ILogger? _logger;

    // Optional audio cache directory. When the deferred result carries a LocalCacheFileId,
    // we open the cached file from here instead of downloading from CDN.
    private readonly string? _audioCacheDirectory;
    private readonly long? _audioCacheMaxBytes;

    private long _position;
    private long _fileSize;
    private bool _fileSizeKnown;

    // CDN resources - created once by eager background init
    private ProgressiveDownloader? _cdnDownloader;
    // Used when reading from a local cached file (no CDN needed)
    private Stream? _cachedFileStream;
    private AudioDecryptStream? _decryptStream;
    private volatile bool _cdnInitialized;
    private Task? _eagerInitTask;

    private bool _disposed;
    private readonly CancellationTokenSource _disposeCts = new();
    // Linked source: fires when EITHER _disposeCts OR the playback CT passed
    // by AudioEngine fires. Used so EnsureCdnInitialized's sync wait can break
    // out the moment the engine cancels playback, instead of waiting the full
    // _deferredTask timeout (which is minutes long).
    private readonly CancellationTokenSource _initWaitCts;

    /// <summary>
    /// Raised when buffer state changes (forwarded from ProgressiveDownloader).
    /// </summary>
    public event Action<BufferStatus>? BufferStateChanged;

    /// <summary>
    /// Raised when a download error occurs (forwarded from ProgressiveDownloader).
    /// </summary>
    public event Action<DownloadError>? DownloadError;

    /// <summary>
    /// Creates a lazy progressive downloader for instant playback start.
    /// </summary>
    /// <param name="headData">Already-decrypted head file data.</param>
    /// <param name="deferredTask">Deferred resolution task providing CDN URL, audio key, and file size.</param>
    /// <param name="httpClient">HTTP client for CDN requests.</param>
    /// <param name="fileId">File ID for the audio file.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="audioCacheDirectory">
    /// Directory where persistent audio cache files live. When the deferred result sets
    /// <c>LocalCacheFileId</c>, the file is opened from here instead of CDN.
    /// </param>
    /// <param name="audioCacheMaxBytes">Maximum persistent cache size before LRU pruning.</param>
    public LazyProgressiveDownloader(
        byte[] headData,
        Task<DeferredResult> deferredTask,
        HttpClient httpClient,
        FileId fileId,
        ILogger? logger = null,
        string? audioCacheDirectory = null,
        long? audioCacheMaxBytes = null,
        CancellationToken playbackToken = default)
    {
        ArgumentNullException.ThrowIfNull(headData);
        ArgumentNullException.ThrowIfNull(deferredTask);
        ArgumentNullException.ThrowIfNull(httpClient);

        _headData = headData;
        _deferredTask = deferredTask;
        _httpClient = httpClient;
        _fileId = fileId;
        _logger = logger;
        _audioCacheDirectory = audioCacheDirectory;
        _audioCacheMaxBytes = audioCacheMaxBytes;

        _initWaitCts = playbackToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, playbackToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);

        // Estimate initial size from head data - will update when CDN initialized
        _fileSize = headData.Length;
        _fileSizeKnown = false;

        _logger?.LogDebug(
            "LazyProgressiveDownloader created with {HeadSize} bytes head data",
            headData.Length);

        // Eagerly start CDN initialization in the background so it's ready by the
        // time head data is exhausted. This is the ONLY init path — sync callers
        // just wait on this task.
        _eagerInitTask = Task.Run(() => InitializeCdnResourcesAsync(_initWaitCts.Token));
    }

    #region Stream Properties

    public override bool CanRead => !_disposed;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            if (_fileSizeKnown)
                return _fileSize;

            // Seekable decoders can cache Length during reader construction.
            // Exposing the temporary head-file length makes a full track look
            // like it naturally ends after the instant-start window.
            EnsureCdnInitialized();
            return _fileSize;
        }
    }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Length of the head data (already decrypted portion).
    /// </summary>
    public int HeadDataLength => _headData.Length;

    #endregion

    #region Stream Operations

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return 0;

        // Check if we can satisfy entirely from head data
        if (_position < _headData.Length)
        {
            var headBytesAvailable = (int)(_headData.Length - _position);
            var bytesToReadFromHead = Math.Min(buffer.Length, headBytesAvailable);

            _headData.AsSpan((int)_position, bytesToReadFromHead).CopyTo(buffer);
            _position += bytesToReadFromHead;

            // If we have more to read and exhausted head, initialize CDN
            if (bytesToReadFromHead < buffer.Length && _position >= _headData.Length)
            {
                EnsureCdnInitialized();

                // Read remaining from CDN
                var remaining = buffer.Slice(bytesToReadFromHead);
                var cdnBytesRead = ReadFromCdn(remaining);
                return bytesToReadFromHead + cdnBytesRead;
            }

            return bytesToReadFromHead;
        }

        // Position is past head data - need CDN
        EnsureCdnInitialized();
        return ReadFromCdn(buffer);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return 0;

        // Check if we can satisfy entirely from head data
        if (_position < _headData.Length)
        {
            var headBytesAvailable = (int)(_headData.Length - _position);
            var bytesToReadFromHead = Math.Min(buffer.Length, headBytesAvailable);

            _headData.AsMemory((int)_position, bytesToReadFromHead).CopyTo(buffer);
            _position += bytesToReadFromHead;

            // If we have more to read and exhausted head, initialize CDN
            if (bytesToReadFromHead < buffer.Length && _position >= _headData.Length)
            {
                await EnsureCdnInitializedAsync(cancellationToken);

                // Read remaining from CDN
                var remaining = buffer.Slice(bytesToReadFromHead);
                var cdnBytesRead = await ReadFromCdnAsync(remaining, cancellationToken);
                return bytesToReadFromHead + cdnBytesRead;
            }

            return bytesToReadFromHead;
        }

        // Position is past head data - need CDN
        await EnsureCdnInitializedAsync(cancellationToken);
        return await ReadFromCdnAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var oldPosition = _position;
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot seek before beginning of stream");

        _position = newPosition;

        // Also sync decrypt stream position if initialized
        if (_decryptStream != null)
        {
            _decryptStream.Position = newPosition;
        }

        return _position;
    }

    #endregion

    #region CDN Initialization

    private void EnsureCdnInitialized()
    {
        if (_cdnInitialized) return;
        var t = _eagerInitTask;
        if (t == null) return;
        if (t.IsCompleted)
        {
            t.GetAwaiter().GetResult();
            return;
        }
        // Sync wait, but cancellation-aware: the linked CT fires when AudioEngine
        // cancels playback OR we're disposed, so we don't have to wait for the
        // (long) _deferredTask timeout. Without this, a stuck CDN resolution
        // pinned the AudioHost command pump for minutes.
        try
        {
            t.Wait(_initWaitCts.Token);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            throw ae.InnerExceptions[0];
        }
        // Surface any task fault that Wait swallowed via AggregateException above.
        t.GetAwaiter().GetResult();
    }

    private async Task EnsureCdnInitializedAsync(CancellationToken cancellationToken)
    {
        if (_cdnInitialized) return;
        if (_eagerInitTask != null)
            await _eagerInitTask;
    }

    private async Task InitializeCdnResourcesAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Initializing audio resources (head data exhausted at position {Position})", _position);

        // Wait for the deferred resolution
        var deferred = await _deferredTask;

        var audioKey = deferred.AudioKey;
        _fileSize = deferred.FileSize;
        _fileSizeKnown = true;

        var useLocalCache = !string.IsNullOrEmpty(deferred.LocalCacheFileId)
                             && _audioCacheDirectory != null
                             && audioKey is { Length: 16 };
        string? localCachePath = useLocalCache
            ? Path.Combine(_audioCacheDirectory!, "audio", deferred.LocalCacheFileId + ".enc")
            : null;

        if (useLocalCache)
        {
            // ── Local cache path ────────────────────────────────────────────────────
            // The file is fully on disk — no CDN download needed.
            _logger?.LogInformation("Using local cache for {FileId} ({Bytes} bytes)", deferred.LocalCacheFileId, _fileSize);

            _cachedFileStream = new FileStream(localCachePath!, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);

            _decryptStream = new AudioDecryptStream(audioKey, _cachedFileStream, decryptionStartOffset: 0, logger: _logger);

            // Validate the decrypted header. Spotify Vorbis files are observed in two
            // layouts: OggS at byte 0, or OggS after the 0xa7 Spotify header.
            // If neither location matches, this cache file was written by a broken
            // persist path (clear-head not re-encrypted, wrong key at write time, or
            // pre-fix code) and decoders will throw FileFormat on it. Delete the file
            // and surface a retriable error; the next playback attempt will resolve
            // fresh and download from CDN.
            // We only reach this branch when audioKey is a real 16-byte key (gated
            // above), so a magic mismatch genuinely means the cache file is broken,
            // not just unkeyed.
            if (!TryFindOggMagic(_decryptStream, out var magicOffset, out var magicReport))
            {
                _logger?.LogWarning(
                    "Cached file {FileId} failed Ogg-magic check ({Magic}); deleting so the next playback fetches a fresh copy from CDN",
                    deferred.LocalCacheFileId, magicReport);

                await _decryptStream.DisposeAsync();
                _decryptStream = null;
                await _cachedFileStream.DisposeAsync();
                _cachedFileStream = null;
                try { File.Delete(localCachePath!); }
                catch (Exception ex) { _logger?.LogDebug(ex, "Failed to delete corrupt cache file {Path}", localCachePath); }

                // The deferred result we received chose the local-cache shortcut and
                // its CdnUrl is empty — we cannot rebuild the CDN downloader from
                // inside this method. Surface a clear failure so the playback layer
                // retries; by then the cache file is gone and resolution will return
                // a real CDN URL.
                throw new InvalidOperationException(
                    $"Local audio cache for {deferred.LocalCacheFileId} was corrupt ({magicReport} after decrypt); deleted, retry playback");
            }

            _logger?.LogDebug("Cached file {FileId} passed Ogg-magic check at offset {Offset}",
                deferred.LocalCacheFileId, magicOffset);
        }

        if (!useLocalCache)
        {
            // ── CDN download path ───────────────────────────────────────────────────
            var cdnUrl = deferred.CdnUrl;
            _logger?.LogDebug("CDN resolved: URL ready, file size = {FileSize}", _fileSize);

            // Compute persistent cache path if we know the Spotify file ID
            string? persistCachePath = null;
            if (!string.IsNullOrEmpty(deferred.SpotifyFileId) && _audioCacheDirectory != null)
                persistCachePath = Path.Combine(_audioCacheDirectory, "audio", deferred.SpotifyFileId + ".enc");

            _cdnDownloader = new ProgressiveDownloader(
                _httpClient,
                cdnUrl!,
                _fileSize,
                _fileId,
                _headData,
                logger: _logger,
                persistCachePath: persistCachePath,
                maxCacheBytes: _audioCacheMaxBytes,
                persistentCacheAudioKey: audioKey);

            // Forward events
            _cdnDownloader.BufferStateChanged += status => BufferStateChanged?.Invoke(status);
            _cdnDownloader.DownloadError += error => DownloadError?.Invoke(error);

            // Start background download of entire file
            _cdnDownloader.StartBackgroundDownload();

            // Wrap with decryption (skip head data which is already decrypted)
            _decryptStream = new AudioDecryptStream(
                audioKey,
                _cdnDownloader,
                decryptionStartOffset: _headData.Length,
                logger: _logger);
        }

        // Sync position
        _decryptStream.Position = _position;

        _cdnInitialized = true;

        _logger?.LogInformation("Audio source initialized - continuing playback from position {Position}", _position);
    }

    private static bool TryFindOggMagic(Stream stream, out long offset, out string report)
    {
        var startPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            var foundAtZero = TryReadOggMagicAt(stream, 0, out var zeroMagic);
            var foundAfterSpotifyHeader = TryReadOggMagicAt(stream, SpotifyHeaderSize, out var headerMagic);

            report = $"0:{zeroMagic}, {SpotifyHeaderSize}:{headerMagic}";

            if (foundAtZero)
            {
                offset = 0;
                return true;
            }

            if (foundAfterSpotifyHeader)
            {
                offset = SpotifyHeaderSize;
                return true;
            }

            offset = 0;
            return false;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = startPosition;
        }
    }

    private static bool TryReadOggMagicAt(Stream stream, long position, out string magicHex)
    {
        if (!stream.CanSeek)
        {
            magicHex = "not-seekable";
            return false;
        }

        stream.Position = position;
        Span<byte> magic = stackalloc byte[4];
        var read = stream.Read(magic);
        magicHex = read > 0 ? Convert.ToHexString(magic[..read]) : "(empty)";

        return read == 4
            && magic[0] == (byte)'O'
            && magic[1] == (byte)'g'
            && magic[2] == (byte)'g'
            && magic[3] == (byte)'S';
    }

    #endregion

    #region CDN Read Operations

    private int ReadFromCdn(Span<byte> buffer)
    {
        if (_decryptStream == null)
            return 0;

        if (_decryptStream.Position != _position)
            _decryptStream.Position = _position;
        var bytesRead = _decryptStream.Read(buffer);
        _position += bytesRead;
        return bytesRead;
    }

    private async ValueTask<int> ReadFromCdnAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_decryptStream == null)
            return 0;

        if (_decryptStream.Position != _position)
            _decryptStream.Position = _position;
        var bytesRead = await _decryptStream.ReadAsync(buffer, cancellationToken);
        _position += bytesRead;
        return bytesRead;
    }

    #endregion

    #region Prefetch Operations

    /// <summary>
    /// Prefetches data at the specified byte position to ensure it's available for reading.
    /// Call this before seeking in NVorbis to ensure OGG pages are downloaded.
    /// </summary>
    /// <param name="bytePosition">Start byte position to prefetch from.</param>
    /// <param name="length">Number of bytes to prefetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PrefetchRangeAsync(long bytePosition, int length, CancellationToken cancellationToken = default)
    {
        // Ensure CDN is initialized first
        await EnsureCdnInitializedAsync(cancellationToken);

        if (_cdnDownloader != null)
        {
            // Fetch the range so it's ready when NVorbis seeks
            await _cdnDownloader.FetchRangeAsync(bytePosition, bytePosition + length, cancellationToken);
            _logger?.LogDebug("Prefetched {Length} bytes at position {Position}", length, bytePosition);
        }
    }

    /// <summary>
    /// Forwards a seek notification to the CDN downloader so it cancels in-flight
    /// prefetch for the old read horizon. Pre-CDN (still serving head data) it's
    /// a no-op — there's nothing in flight to cancel.
    /// </summary>
    public void NotifySeek() => _cdnDownloader?.NotifySeek();

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
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _initWaitCts.Dispose();
            _decryptStream?.Dispose();
            _cdnDownloader?.Dispose();
            _cachedFileStream?.Dispose();
            // init lock removed — eager task handles sync
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _initWaitCts.Dispose();

        if (_decryptStream != null)
            await _decryptStream.DisposeAsync();

        if (_cdnDownloader != null)
            await _cdnDownloader.DisposeAsync();

        if (_cachedFileStream != null)
            await _cachedFileStream.DisposeAsync();

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    #endregion
}
