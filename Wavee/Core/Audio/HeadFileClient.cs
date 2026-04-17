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
    /// <summary>
    /// Fallback URL template used only when the session hasn't yet received a
    /// <c>head-files-url</c> from ProductInfo. Spotify currently hands out
    /// <c>https://heads-fa-tls13.spotifycdn.com/head/{file_id}</c> for premium
    /// accounts; the legacy <c>heads-fa.spotify.com</c> host still resolves
    /// but is older and may be deprecated ahead of the active CDN.
    /// </summary>
    private const string FallbackHeadFilesUrlTemplate = "https://heads-fa.spotify.com/head/{file_id}";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Delegate that returns the server-provided head-files URL template (from
    /// <c>UserData.HeadFilesUrl</c>) at request time. Evaluated per call so a
    /// session that authenticates after the client is constructed picks up the
    /// real template once ProductInfo arrives.
    /// </summary>
    private readonly Func<string?>? _urlTemplateResolver;

    /// <summary>
    /// Creates a new HeadFileClient.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="urlTemplateResolver">
    /// Optional delegate returning the head-files URL template from the session's
    /// <c>UserData.HeadFilesUrl</c>. When null or the delegate returns null,
    /// falls back to <see cref="FallbackHeadFilesUrlTemplate"/>. Wire this to
    /// <c>() =&gt; session.UserData?.HeadFilesUrl</c> so the live CDN host from
    /// ProductInfo is honoured instead of the hardcoded legacy host.
    /// </param>
    public HeadFileClient(
        HttpClient httpClient,
        ILogger? logger = null,
        Func<string?>? urlTemplateResolver = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _logger = logger;
        _urlTemplateResolver = urlTemplateResolver;
    }

    /// <summary>
    /// Resolves the URL template to use for the next request. Prefers the
    /// session-provided <c>head-files-url</c>; falls back to the legacy host.
    /// </summary>
    private string ResolveUrlTemplate()
    {
        var fromSession = _urlTemplateResolver?.Invoke();
        return !string.IsNullOrEmpty(fromSession) ? fromSession : FallbackHeadFilesUrlTemplate;
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
        // ProductInfo delivers the CDN host as a template like
        // "https://heads-fa-tls13.spotifycdn.com/head/{file_id}". Substitute the
        // file id into that template; otherwise fall back to the legacy host.
        var template = ResolveUrlTemplate();
        var url = template.Contains("{file_id}", StringComparison.Ordinal)
            ? template.Replace("{file_id}", fileIdHex, StringComparison.Ordinal)
            : template.TrimEnd('/') + "/" + fileIdHex;

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
