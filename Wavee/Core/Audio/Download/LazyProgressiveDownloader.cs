using Microsoft.Extensions.Logging;
using Wavee.Core.Crypto;
using Wavee.Protocol.Storage;

namespace Wavee.Core.Audio.Download;

/// <summary>
/// Stream that provides instant playback start by serving head data immediately,
/// then lazily initializing CDN download when head data is exhausted.
/// </summary>
/// <remarks>
/// This enables true instant start:
/// 1. Head file is already decrypted - serve immediately without waiting for audioKey or CDN
/// 2. When position reaches end of head data, await deferred tasks
/// 3. Create ProgressiveDownloader + AudioDecryptStream on demand
/// 4. Seamlessly continue with decrypted CDN data
/// </remarks>
public sealed class LazyProgressiveDownloader : Stream
{
    private readonly byte[] _headData;
    private readonly Task<byte[]> _audioKeyTask;
    private readonly Task<StorageResolveResponse> _cdnTask;
    private readonly HttpClient _httpClient;
    private readonly FileId _fileId;
    private readonly ILogger? _logger;
    private readonly Func<long>? _fileSizeResolver;

    private long _position;
    private long _fileSize;
    private bool _fileSizeKnown;

    // CDN resources - created on demand when head data exhausted
    private ProgressiveDownloader? _cdnDownloader;
    private AudioDecryptStream? _decryptStream;
    private bool _cdnInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private bool _disposed;

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
    /// <param name="audioKeyTask">Deferred audio key task (started but not awaited).</param>
    /// <param name="cdnTask">Deferred CDN resolution task (started but not awaited).</param>
    /// <param name="httpClient">HTTP client for CDN requests.</param>
    /// <param name="fileId">File ID for the audio file.</param>
    /// <param name="fileSizeResolver">Optional function to resolve file size (via HEAD request).</param>
    /// <param name="logger">Optional logger.</param>
    public LazyProgressiveDownloader(
        byte[] headData,
        Task<byte[]> audioKeyTask,
        Task<StorageResolveResponse> cdnTask,
        HttpClient httpClient,
        FileId fileId,
        Func<long>? fileSizeResolver = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(headData);
        ArgumentNullException.ThrowIfNull(audioKeyTask);
        ArgumentNullException.ThrowIfNull(cdnTask);
        ArgumentNullException.ThrowIfNull(httpClient);

        _headData = headData;
        _audioKeyTask = audioKeyTask;
        _cdnTask = cdnTask;
        _httpClient = httpClient;
        _fileId = fileId;
        _fileSizeResolver = fileSizeResolver;
        _logger = logger;

        // Estimate initial size from head data - will update when CDN initialized
        _fileSize = headData.Length;
        _fileSizeKnown = false;

        _logger?.LogDebug(
            "LazyProgressiveDownloader created with {HeadSize} bytes head data",
            headData.Length);
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

            // Try to resolve file size without blocking
            if (_fileSizeResolver != null && !_cdnInitialized)
            {
                try
                {
                    _fileSize = _fileSizeResolver();
                    _fileSizeKnown = true;
                }
                catch
                {
                    // Fall back to head size
                }
            }
            else if (_cdnDownloader != null)
            {
                _fileSize = _cdnDownloader.Length;
                _fileSizeKnown = true;
            }

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
        if (_cdnInitialized)
            return;

        _initLock.Wait();
        try
        {
            if (_cdnInitialized)
                return;

            InitializeCdnResources();
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureCdnInitializedAsync(CancellationToken cancellationToken)
    {
        if (_cdnInitialized)
            return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_cdnInitialized)
                return;

            await InitializeCdnResourcesAsync(cancellationToken);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void InitializeCdnResources()
    {
        InitializeCdnResourcesAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task InitializeCdnResourcesAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Initializing CDN resources (head data exhausted at position {Position})", _position);

        // Wait for both deferred tasks
        await Task.WhenAll(_audioKeyTask, _cdnTask);

        var audioKey = await _audioKeyTask;
        var cdnResponse = await _cdnTask;

        if (cdnResponse.Cdnurl.Count == 0)
        {
            throw new InvalidOperationException($"No CDN URLs returned for file {_fileId.ToBase16()}");
        }

        var cdnUrl = cdnResponse.Cdnurl[0];

        // Get file size from CDN
        _fileSize = await GetFileSizeAsync(cdnUrl, cancellationToken);
        _fileSizeKnown = true;

        _logger?.LogDebug("CDN resolved: URL ready, file size = {FileSize}", _fileSize);

        // Create progressive downloader with head data already included
        _cdnDownloader = new ProgressiveDownloader(
            _httpClient,
            cdnUrl,
            _fileSize,
            _fileId,
            _headData,
            logger: _logger);

        // Forward events
        _cdnDownloader.BufferStateChanged += status => BufferStateChanged?.Invoke(status);
        _cdnDownloader.DownloadError += error => DownloadError?.Invoke(error);

        // Wrap with decryption (skip head data which is already decrypted)
        _decryptStream = new AudioDecryptStream(
            audioKey,
            _cdnDownloader,
            decryptionStartOffset: _headData.Length);

        // Sync position
        _decryptStream.Position = _position;

        _cdnInitialized = true;

        _logger?.LogInformation("CDN initialized - continuing playback from position {Position}", _position);
    }

    private async Task<long> GetFileSizeAsync(string cdnUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
        }
        catch
        {
            // Fall through to estimate
        }

        // Estimate based on bitrate (3 minutes at 320kbps = ~7.2MB)
        return 8 * 1024 * 1024; // 8MB default estimate
    }

    #endregion

    #region CDN Read Operations

    private int ReadFromCdn(Span<byte> buffer)
    {
        if (_decryptStream == null)
            return 0;

        _decryptStream.Position = _position;
        var bytesRead = _decryptStream.Read(buffer);
        _position += bytesRead;
        return bytesRead;
    }

    private async ValueTask<int> ReadFromCdnAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_decryptStream == null)
            return 0;

        _decryptStream.Position = _position;
        var bytesRead = await _decryptStream.ReadAsync(buffer, cancellationToken);
        _position += bytesRead;
        return bytesRead;
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
            _decryptStream?.Dispose();
            _cdnDownloader?.Dispose();
            _initLock.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_decryptStream != null)
            await _decryptStream.DisposeAsync();

        if (_cdnDownloader != null)
            await _cdnDownloader.DisposeAsync();

        _initLock.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    #endregion
}
