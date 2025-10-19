using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Session;

/// <summary>
/// Resolves Spotify Access Point (AP) servers for connection.
/// </summary>
/// <remarks>
/// This class queries Spotify's apresolve service to discover available AP servers.
/// Fallback APs are provided in case the service is unreachable.
///
/// See: https://apresolve.spotify.com/
/// </remarks>
internal static class ApResolver
{
    private const string ApResolveUrl = "https://apresolve.spotify.com/?type=accesspoint";

    private static readonly string[] FallbackAps =
    [
        "ap.spotify.com:443",
        "ap.spotify.com:4070",
        "ap-gew4.spotify.com:443",
        "ap-gew4.spotify.com:4070"
    ];

    /// <summary>
    /// Resolves available Access Point URLs.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of AP URLs in priority order.</returns>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        HttpClient httpClient,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger?.LogDebug("Resolving Access Points from {Url}", ApResolveUrl);

            var response = await httpClient.GetFromJsonAsync<ApResolveResponse>(
                ApResolveUrl,
                cancellationToken);

            if (response?.AccessPoint is { Length: > 0 })
            {
                logger?.LogInformation("Resolved {Count} Access Points", response.AccessPoint.Length);
                return response.AccessPoint;
            }

            logger?.LogWarning("ApResolve returned empty list, using fallback APs");
            return FallbackAps;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to resolve Access Points, using fallback APs");
            return FallbackAps;
        }
    }

    private sealed record ApResolveResponse
    {
        [JsonPropertyName("accesspoint")]
        public string[] AccessPoint { get; init; } = [];

        [JsonPropertyName("dealer")]
        public string[] Dealer { get; init; } = [];

        [JsonPropertyName("spclient")]
        public string[] SpClient { get; init; } = [];
    }
}
