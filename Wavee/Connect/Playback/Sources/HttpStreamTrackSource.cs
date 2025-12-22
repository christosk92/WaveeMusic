using Microsoft.Extensions.Logging;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Track source for loading HTTP/HTTPS audio streams.
/// Supports direct audio files and radio streams (Shoutcast/Icecast).
/// </summary>
public sealed class HttpStreamTrackSource : ITrackSource
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Audio content types that indicate streamable audio.
    /// </summary>
    private static readonly HashSet<string> AudioContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg",
        "audio/mp3",
        "audio/ogg",
        "audio/aac",
        "audio/aacp",
        "audio/x-aac",
        "audio/flac",
        "audio/wav",
        "audio/x-wav",
        "application/ogg",
        "application/octet-stream" // Some servers send this for audio
    };

    /// <summary>
    /// Audio file extensions.
    /// </summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".aac", ".flac", ".wav", ".m4a"
    };

    /// <summary>
    /// Creates a new HTTP stream track source.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="logger">Optional logger.</param>
    public HttpStreamTrackSource(HttpClient httpClient, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    /// <inheritdoc/>
    public string SourceName => "HttpStream";

    /// <inheritdoc/>
    public bool CanHandle(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Loading HTTP stream: {Uri}", uri);

        // Try HEAD request first to check content type and length
        long? contentLength = null;
        string? contentType = null;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResponse.IsSuccessStatusCode)
            {
                contentLength = headResponse.Content.Headers.ContentLength;
                contentType = headResponse.Content.Headers.ContentType?.MediaType;
                _logger?.LogDebug("HEAD response: ContentType={ContentType}, ContentLength={ContentLength}",
                    contentType, contentLength);
            }
        }
        catch (Exception ex)
        {
            // HEAD request failed, continue with GET
            _logger?.LogDebug(ex, "HEAD request failed, falling back to GET");
        }

        // Determine if this is an infinite stream (radio)
        bool isInfinite = !contentLength.HasValue || contentLength == 0;

        // Create GET request with optional ICY metadata headers
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // Request ICY metadata for radio streams
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        // Some servers need a specific user agent
        if (string.IsNullOrEmpty(_httpClient.DefaultRequestHeaders.UserAgent.ToString()))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Wavee/1.0");
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP stream request failed with status {response.StatusCode}");
        }

        // Get ICY metadata interval if present
        int icyMetaInt = 0;
        if (response.Headers.TryGetValues("icy-metaint", out var metaIntValues))
        {
            var metaIntStr = metaIntValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(metaIntStr) && int.TryParse(metaIntStr, out var parsed))
            {
                icyMetaInt = parsed;
                _logger?.LogDebug("ICY metadata interval: {MetaInt} bytes", icyMetaInt);
            }
        }

        // Get stream name from ICY headers if available
        string? streamName = null;
        if (response.Headers.TryGetValues("icy-name", out var nameValues))
        {
            streamName = nameValues.FirstOrDefault();
        }

        // Get content type from response if not from HEAD
        contentType ??= response.Content.Headers.ContentType?.MediaType;
        contentLength ??= response.Content.Headers.ContentLength;
        isInfinite = !contentLength.HasValue || contentLength == 0;

        _logger?.LogDebug("Stream info: ContentType={ContentType}, ContentLength={ContentLength}, IsInfinite={IsInfinite}, IcyMetaInt={IcyMetaInt}",
            contentType, contentLength, isInfinite, icyMetaInt);

        // Get the underlying stream
        var innerStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Wrap in buffered stream with ICY metadata handling
        var bufferedStream = new BufferedHttpStream(innerStream, icyMetaInt);

        // Pre-buffer more data for smooth live streaming
        await bufferedStream.PreBufferAsync(262144, cancellationToken);

        // Build track metadata
        var title = streamName ?? ExtractTitleFromUri(uri);
        var metadata = new TrackMetadata
        {
            Uri = uri,
            Title = title,
            DurationMs = isInfinite ? null : EstimateDuration(contentLength, contentType),
            AdditionalMetadata = new Dictionary<string, string>
            {
                ["source"] = "http",
                ["contentType"] = contentType ?? "unknown",
                ["isInfinite"] = isInfinite.ToString()
            }
        };

        return new HttpStreamTrackStream(response, bufferedStream, metadata, isInfinite, uri);
    }

    private static string ExtractTitleFromUri(string uri)
    {
        try
        {
            var uriObj = new Uri(uri);

            // Try to get filename from path
            var path = uriObj.AbsolutePath;
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                var filename = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(filename))
                    return filename;
            }

            // Fall back to host name
            return uriObj.Host;
        }
        catch
        {
            return "HTTP Stream";
        }
    }

    private static long? EstimateDuration(long? contentLength, string? contentType)
    {
        if (!contentLength.HasValue)
            return null;

        // Estimate based on typical bitrates
        var bitrate = contentType switch
        {
            "audio/mpeg" or "audio/mp3" => 192_000, // 192 kbps
            "audio/aac" or "audio/aacp" => 128_000, // 128 kbps
            "audio/ogg" => 160_000, // 160 kbps
            "audio/flac" => 800_000, // 800 kbps average
            _ => 192_000 // Default to 192 kbps
        };

        // Duration in ms = (bytes * 8 / bitrate) * 1000
        return (contentLength.Value * 8 * 1000) / bitrate;
    }
}
