using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.OAuth;

/// <summary>
/// Internal HTTP client for making OAuth 2.0 requests to Spotify.
/// Handles form-urlencoded requests, retry logic, and error parsing.
/// </summary>
internal sealed class OAuthHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new OAuth HTTP client.
    /// </summary>
    /// <param name="userAgent">Custom User-Agent header (optional).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public OAuthHttpClient(string? userAgent = null, ILogger? logger = null)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            userAgent ?? "Wavee/1.0 (Spotify Client)");
        _logger = logger;
    }

    /// <summary>
    /// Sends a form-urlencoded POST request with retry logic.
    /// </summary>
    /// <param name="url">The endpoint URL.</param>
    /// <param name="parameters">Form parameters as key-value pairs.</param>
    /// <param name="headers">Optional additional headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed JSON response as JsonDocument.</returns>
    /// <exception cref="OAuthException">Thrown on HTTP or OAuth errors.</exception>
    public async Task<JsonDocument> PostFormAsync(
        string url,
        Dictionary<string, string> parameters,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(parameters);

        _logger?.LogDebug("POST {Url} with {ParamCount} parameters", url, parameters.Count);

        var content = new FormUrlEncodedContent(parameters);

        // Add custom headers if provided
        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        // Retry logic with exponential backoff
        const int maxRetries = 3;
        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsync(url, content, cancellationToken);

                // Success - parse and return
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger?.LogDebug("Received successful response ({StatusCode})", response.StatusCode);
                    return JsonDocument.Parse(responseBody);
                }

                // Error - parse error response
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("HTTP {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);

                // Try to parse OAuth error
                var error = ParseOAuthError(errorBody);

                // Retry on transient errors
                if (IsTransientError(response.StatusCode, error) && attempt < maxRetries)
                {
                    var delay = delays[attempt];
                    _logger?.LogInformation("Transient error, retrying in {Delay}...", delay);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                // Permanent error - throw
                throw CreateOAuthException(response.StatusCode, error, errorBody);
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError(ex, "Network error on attempt {Attempt}/{MaxRetries}", attempt + 1, maxRetries + 1);

                // Retry on network errors
                if (attempt < maxRetries)
                {
                    await Task.Delay(delays[attempt], cancellationToken);
                    continue;
                }

                throw new OAuthException(
                    OAuthFailureReason.NetworkError,
                    "Network error during OAuth request",
                    ex);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("OAuth request cancelled");
                throw new OAuthException(
                    OAuthFailureReason.Cancelled,
                    "OAuth request was cancelled");
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to parse OAuth response");
                throw new OAuthException(
                    OAuthFailureReason.Unknown,
                    "Failed to parse OAuth server response",
                    ex);
            }
        }

        // Should not reach here
        throw new OAuthException(
            OAuthFailureReason.Unknown,
            "OAuth request failed after all retry attempts");
    }

    /// <summary>
    /// Parses OAuth error response.
    /// </summary>
    private static (string? error, string? description) ParseOAuthError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var error = root.TryGetProperty("error", out var errorProp)
                ? errorProp.GetString()
                : null;

            var description = root.TryGetProperty("error_description", out var descProp)
                ? descProp.GetString()
                : null;

            return (error, description);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Checks if an error is transient and should be retried.
    /// </summary>
    private static bool IsTransientError(HttpStatusCode statusCode, (string? error, string? description) error)
    {
        // Retry on server errors
        if (statusCode >= HttpStatusCode.InternalServerError)
            return true;

        // Retry on rate limiting
        if (statusCode == HttpStatusCode.TooManyRequests)
            return true;

        // Don't retry OAuth-specific errors
        return false;
    }

    /// <summary>
    /// Creates an appropriate OAuthException based on error code and HTTP status.
    /// </summary>
    private static OAuthException CreateOAuthException(
        HttpStatusCode statusCode,
        (string? error, string? description) error,
        string responseBody)
    {
        var (errorCode, description) = error;
        var message = description ?? errorCode ?? $"HTTP {(int)statusCode}";

        var reason = errorCode switch
        {
            "invalid_client" => OAuthFailureReason.InvalidClient,
            "invalid_grant" => OAuthFailureReason.InvalidGrant,
            "unauthorized_client" => OAuthFailureReason.UnauthorizedClient,
            "invalid_scope" => OAuthFailureReason.InvalidScope,
            "access_denied" => OAuthFailureReason.AccessDenied,
            "authorization_pending" => OAuthFailureReason.AuthorizationPending,
            "slow_down" => OAuthFailureReason.SlowDown,
            "expired_token" => OAuthFailureReason.ExpiredDeviceCode,
            "server_error" => OAuthFailureReason.ServerError,
            _ when statusCode >= HttpStatusCode.InternalServerError => OAuthFailureReason.ServerError,
            _ => OAuthFailureReason.Unknown
        };

        return new OAuthException(reason, $"OAuth error: {message}");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
