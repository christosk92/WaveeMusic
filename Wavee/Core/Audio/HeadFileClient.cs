using Microsoft.Extensions.Logging;

namespace Wavee.Core.Audio;

/// <summary>
/// Client for fetching head files for instant playback.
/// </summary>
/// <remarks>
/// Head files are the first portion of encrypted audio files, served from Spotify's CDN.
/// They enable instant playback start while the full CDN download begins in parallel.
///
/// Key characteristics:
/// - Unauthenticated (no access token required)
/// - Returns first ~128KB of audio file
/// - Same encryption as main CDN file (needs AudioKey for decryption)
/// </remarks>
public sealed class HeadFileClient
{
    private const string HeadFilesBaseUrl = "https://heads-fa.spotify.com/head/";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new HeadFileClient.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Optional logger.</param>
    public HeadFileClient(HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches head file data for instant playback start.
    /// </summary>
    /// <param name="fileId">The audio file ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Head file data (first ~128KB of audio file).</returns>
    /// <exception cref="HeadFileException">Thrown if the fetch fails.</exception>
    public async Task<byte[]> FetchHeadAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        if (!fileId.IsValid)
            throw new ArgumentException("FileId is not valid", nameof(fileId));

        var fileIdHex = fileId.ToBase16().ToLowerInvariant();
        var url = HeadFilesBaseUrl + fileIdHex;

        _logger?.LogDebug("Fetching head file for {FileId} from {Url}", fileIdHex, url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);

        try
        {
            var response = await _httpClient.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new HeadFileException(
                    HeadFileFailureReason.HttpError,
                    $"Head file request failed: {response.StatusCode}",
                    fileId);
            }

            var data = await response.Content.ReadAsByteArrayAsync(cts.Token);

            _logger?.LogDebug("Fetched head file for {FileId}: {Bytes} bytes", fileIdHex, data.Length);

            return data;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HeadFileException(
                HeadFileFailureReason.Timeout,
                $"Head file request timed out after {DefaultTimeout.TotalSeconds}s",
                fileId);
        }
        catch (HttpRequestException ex)
        {
            throw new HeadFileException(
                HeadFileFailureReason.NetworkError,
                $"Head file request failed: {ex.Message}",
                fileId,
                ex);
        }
    }

    /// <summary>
    /// Attempts to fetch head file data, returning null on failure.
    /// </summary>
    /// <remarks>
    /// Use this when head file is optional (playback can proceed without it, just slower start).
    /// </remarks>
    /// <param name="fileId">The audio file ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Head file data, or null if fetch failed.</returns>
    public async Task<byte[]?> TryFetchHeadAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await FetchHeadAsync(fileId, cancellationToken);
        }
        catch (HeadFileException ex)
        {
            _logger?.LogDebug(ex, "Head file fetch failed for {FileId}, playback will start slower",
                fileId.ToBase16());
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>
/// Exception thrown when head file fetch fails.
/// </summary>
public sealed class HeadFileException : Exception
{
    /// <summary>
    /// The reason for failure.
    /// </summary>
    public HeadFileFailureReason Reason { get; }

    /// <summary>
    /// The file ID that was being fetched.
    /// </summary>
    public FileId FileId { get; }

    public HeadFileException(HeadFileFailureReason reason, string message, FileId fileId)
        : base(message)
    {
        Reason = reason;
        FileId = fileId;
    }

    public HeadFileException(HeadFileFailureReason reason, string message, FileId fileId, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
        FileId = fileId;
    }
}

/// <summary>
/// Reasons for head file fetch failure.
/// </summary>
public enum HeadFileFailureReason
{
    /// <summary>
    /// HTTP request returned an error status code.
    /// </summary>
    HttpError,

    /// <summary>
    /// Network error during request.
    /// </summary>
    NetworkError,

    /// <summary>
    /// Request timed out.
    /// </summary>
    Timeout
}
